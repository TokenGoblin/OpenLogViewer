using System.IO;
using System.Security.Cryptography;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// The application's half of the agent API: starting it, arming it, and the two
/// things it is allowed to change.
/// </summary>
public partial class MainViewModel
{
    private AgentServer? _agent;

    /// <summary>The server, once running, for the window to report on.</summary>
    public AgentServer? Agent => _agent;

    public bool AgentIsRunning => _agent is { IsRunning: true };

    /// <summary>Where an agent points itself, or nothing when it is off.</summary>
    public string AgentAddress => _agent?.Address ?? "";

    /// <summary>
    /// Whether writing through the API is allowed at this moment.
    ///
    /// <para>
    /// False on startup, false again on every disconnect, and never persisted.
    /// The reason it is not a saved preference is that the thing it protects
    /// changes underneath it: the same laptop is plugged into a bench engine one
    /// afternoon and a car the next, and a permission granted for the first
    /// should not still be granted for the second.
    /// </para>
    /// </summary>
    public bool AgentWritesArmed
    {
        get => _agentWritesArmed;
        set
        {
            if (!Set(ref _agentWritesArmed, value)) return;

            Hint = value
                ? "An agent may now change settings and table cells in the ECU's working memory. "
                  + "Nothing it does is burned, and this turns itself off when you disconnect."
                : "Agent writes are off. The API can still read everything.";

            Raise(nameof(AgentSummary));
        }
    }

    private bool _agentWritesArmed;

    /// <summary>One line saying what the API is doing, for the window.</summary>
    public string AgentSummary =>
        _agent is not { IsRunning: true }
            ? "Off. No socket is open."
            : $"Listening on {_agent.Address} — {_agent.Subscribers} watching, "
              + (AgentWritesArmed ? "writes armed." : "read-only.");

    /// <summary>
    /// Starts the API and writes its token where an agent can find it.
    ///
    /// The token goes in the workspace rather than being shown and typed, for
    /// the same reason Jupyter does it: a person copying a secret by hand picks
    /// a short one, and a short one on a port any local program can reach is not
    /// worth having.
    /// </summary>
    public string StartAgentApi(int port = 8765)
    {
        if (_agent is { IsRunning: true }) return $"Already listening on {_agent.Address}.";

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        try
        {
            var server = new AgentServer(new AgentBridge(this), new AgentServerSettings
            {
                Port = port,
                Token = token,
            });

            server.Start();
            _agent = server;

            string where = WriteAgentToken(server, token);

            Raise(nameof(AgentIsRunning));
            Raise(nameof(AgentAddress));
            Raise(nameof(AgentSummary));

            return $"The agent API is listening on {server.Address}. Its token is in {where}.";
        }
        catch (Exception e) when (e is System.Net.HttpListenerException or IOException
                                       or UnauthorizedAccessException)
        {
            _agent = null;

            return $"The agent API could not start: {e.Message} "
                   + "Another program may already have that port.";
        }
    }

    /// <summary>
    /// Leaves the address and the token in the workspace, as JSON.
    ///
    /// Written fresh each time the server starts, so a stale file never points
    /// an agent at a port nobody is listening on with a token that no longer
    /// opens it.
    /// </summary>
    private string WriteAgentToken(AgentServer server, string token)
    {
        string path = Path.Combine(Workspace.Root, "agent-api.json");

        Directory.CreateDirectory(Workspace.Root);
        File.WriteAllText(path, $$"""
            {
              "address": "{{server.Address}}",
              "websocket": "ws://127.0.0.1:{{server.Port}}/live/stream",
              "token": "{{token}}"
            }
            """);

        return path;
    }

    public void StopAgentApi()
    {
        if (_agent is null) return;

        _agent.Dispose();
        _agent = null;
        AgentWritesArmed = false;

        // The file describes a socket that is no longer there.
        try { File.Delete(Path.Combine(Workspace.Root, "agent-api.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        Raise(nameof(AgentIsRunning));
        Raise(nameof(AgentAddress));
        Raise(nameof(AgentSummary));
    }

    /// <summary>Hands one live frame to every watching agent. Never blocks.</summary>
    private void PublishToAgents(double seconds, IReadOnlyList<string> names, IReadOnlyList<double> values) =>
        _agent?.Publish(seconds, names, values);

    // ----- the two things an agent may change --------------------------------

    /// <summary>The tune as an agent reads it, or nothing when none was read.</summary>
    internal IReadOnlyDictionary<string, double> AgentTuneValues() =>
        _ecuTune is { } tune && !TuneIsPlaceholder
            ? tune.Scalars()
            : new Dictionary<string, double>();

    /// <summary>
    /// Sets one setting, through the same path and the same gates the dialog
    /// uses.
    /// </summary>
    internal AgentRefusal? AgentSetSetting(string name, double value)
    {
        if (Refusal() is { } refused) return refused;
        if (_ecuTune is not { } tune) return new AgentRefusal("no tune has been read");

        _settingsEdit ??= new TuneSettingsEdit(tune);

        if (tune.Constant(name) is null)
            return new AgentRefusal("no such setting", $"This firmware declares no \"{name}\".");

        if (!_settingsEdit.Set(name, value))
        {
            return new AgentRefusal(
                "the value would not go in",
                "It is out of the range the firmware declares, or the setting is not a number.");
        }

        string said = WriteSettingsToEcu();

        OnSettingChanged();

        return said.StartsWith("Sent", StringComparison.OrdinalIgnoreCase)
            ? null
            : new AgentRefusal("the write did not go through", said);
    }

    /// <summary>Sets one cell of one table, through the same path as an edit on screen.</summary>
    internal AgentRefusal? AgentSetTableCell(string name, int column, int row, double value)
    {
        if (Refusal() is { } refused) return refused;

        // By the name a person sees, which is the one the API hands out.
        TuneTable? table = EcuTables.FirstOrDefault(
            t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (table is null) return new AgentRefusal("no such table", name);

        if (column < 0 || column >= table.Columns || row < 0 || row >= table.Rows)
        {
            return new AgentRefusal(
                "that cell is not in the table",
                $"\"{table.Name}\" is {table.Columns} by {table.Rows}.");
        }

        // Selected the way clicking it selects it, which is what builds the edit
        // and points the calibration view at the same table the agent named.
        SelectedEcuTable = table;

        if (_tableEdit is not { } edit) return new AgentRefusal("that table cannot be edited");

        edit.Set(TuneSelection.Cell(column, row), value);

        string said = WriteTableToEcu();

        return said.StartsWith("Sent", StringComparison.OrdinalIgnoreCase)
            ? null
            : new AgentRefusal("the write did not go through", said);
    }

    /// <summary>
    /// The reasons a write is refused before it is even attempted, in the order
    /// worth hearing them.
    /// </summary>
    private AgentRefusal? Refusal()
    {
        if (!AgentWritesArmed)
        {
            return new AgentRefusal(
                "writes are not armed",
                "Tick \"Allow agent writes\" in the application. It clears itself on disconnect.");
        }

        if (_ecuConnection is null) return new AgentRefusal("not connected to an ECU");

        if (TuneIsPlaceholder)
        {
            return new AgentRefusal(
                "the tune in hand is a placeholder",
                "It came from a definition file rather than off the controller, and is all noughts.");
        }

        if (TuneIsFromFile)
        {
            return new AgentRefusal(
                "the tune in hand came from a file",
                "Read the ECU's own tune before writing to it.");
        }

        return null;
    }
}
