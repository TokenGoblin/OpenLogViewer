using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
            server.Activity.Changed += OnAgentActivity;

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

        _agent.Activity.Changed -= OnAgentActivity;
        _agent.Dispose();
        _agent = null;
        AgentWritesArmed = false;

        // The indicator has nothing left to watch, so it should not go on
        // showing whatever it last said.
        _agentActivityDecay?.Dispose();
        _agentActivityDecay = null;
        _agentWriteDecay?.Dispose();
        _agentWriteDecay = null;
        AgentIsActive = false;
        AgentLastAction = "";
        AgentIsWritingToEcu = false;

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

    /// <summary>Hands one full, unfiltered frame to agents that asked for it. Never blocks.</summary>
    private void PublishRawToAgents(double seconds, IReadOnlyList<string> names, IReadOnlyList<double> values) =>
        _agent?.PublishRaw(seconds, names, values);

    /// <summary>Every channel the connected ECU decodes, not just the ones its own datalog names.</summary>
    internal IReadOnlyList<string> AgentRawChannelNames =>
        _live is { SupportsRawTelemetry: true } live ? live.RawNames : [];

    internal IReadOnlyList<string> AgentRawChannelUnits =>
        _live is { SupportsRawTelemetry: true } live ? live.RawUnits : [];

    // ----- the two things an agent may change --------------------------------

    /// <summary>The tune as an agent reads it, or nothing when none was read.</summary>
    internal IReadOnlyDictionary<string, double> AgentTuneValues() =>
        _ecuTune is { } tune && !TuneIsPlaceholder
            ? tune.Scalars()
            : new Dictionary<string, double>();

    /// <summary>
    /// The metadata half of <see cref="AgentTuneValues"/> — the same non-array
    /// constants, with the units/options/range the firmware declared for each.
    /// Kept as a second call rather than folded into the dictionary above so that
    /// endpoint keeps its existing shape.
    /// </summary>
    internal IReadOnlyList<TuneConstant> AgentTuneConstants() =>
        _ecuTune is { } tune && !TuneIsPlaceholder
            ? [.. tune.Layout.Constants.Where(c => !c.IsArray)]
            : [];

    /// <summary>
    /// Sets one setting, through the same path and the same gates the dialog
    /// uses.
    /// </summary>
    internal AgentRefusal? AgentSetSetting(string name, double value)
    {
        using IDisposable origin = WireOriginScope.Enter(WireOrigin.Agent);

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
        using IDisposable origin = WireOriginScope.Enter(WireOrigin.Agent);

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

    // ----- making the activity visible ----------------------------------------

    /// <summary>
    /// How long the indicator keeps saying "active" after the last agent
    /// request finishes, so a burst of reads — an agent asking for state, then
    /// channels, then values, a handful of milliseconds apart — reads as one
    /// continuous "the AI is here" instead of flickering on and off between
    /// each request.
    /// </summary>
    private static readonly TimeSpan AgentActivityDecay = TimeSpan.FromSeconds(1.5);

    private Timer? _agentActivityDecay;
    private Timer? _agentWriteDecay;

    /// <summary>
    /// True while an agent request is in flight, or was in the last
    /// <see cref="AgentActivityDecay"/> — see that field for why it lingers.
    /// </summary>
    public bool AgentIsActive
    {
        get => _agentIsActive;
        private set => Set(ref _agentIsActive, value);
    }

    private bool _agentIsActive;

    /// <summary>The most recent thing an agent asked for or changed, in words.</summary>
    public string AgentLastAction
    {
        get => _agentLastAction;
        private set => Set(ref _agentLastAction, value);
    }

    private string _agentLastAction = "";

    /// <summary>
    /// True only while an agent-originated write to the ECU is in flight or
    /// was in the last <see cref="AgentActivityDecay"/> — deliberately
    /// narrower than <see cref="AgentIsActive"/>, so the UI can make a write
    /// louder than a read without every read looking like one.
    /// </summary>
    public bool AgentIsWritingToEcu
    {
        get => _agentIsWritingToEcu;
        private set => Set(ref _agentIsWritingToEcu, value);
    }

    private bool _agentIsWritingToEcu;

    /// <summary>
    /// Runs on whatever thread <see cref="AgentActivityLog.Changed"/> fired
    /// on, which is an agent's own request thread, never the UI thread — so
    /// the actual state change is handed to the UI dispatcher rather than
    /// applied here.
    /// </summary>
    private void OnAgentActivity(AgentActivityEvent activity) => OnUiThread(() => ApplyAgentActivity(activity));

    /// <summary>
    /// Applies one activity event to the indicator's state. Kept separate from
    /// <see cref="OnAgentActivity"/>, and internal rather than private, so a
    /// test can drive it directly without needing a live dispatcher to pump
    /// messages for it.
    /// </summary>
    internal void ApplyAgentActivity(AgentActivityEvent activity)
    {
        AgentLastAction = activity.Detail;
        AgentIsActive = true;
        RestartDecay(ref _agentActivityDecay, AgentActivityDecay, () => AgentIsActive = false);

        if (activity.Kind != AgentActivityKind.Write) return;

        AgentIsWritingToEcu = true;
        RestartDecay(ref _agentWriteDecay, AgentActivityDecay, () => AgentIsWritingToEcu = false);
    }

    /// <summary>
    /// A plain <see cref="Timer"/> rather than a <see cref="DispatcherTimer"/>,
    /// because it is (re)started from whichever thread just recorded an
    /// activity event, not necessarily the UI thread — a
    /// <see cref="DispatcherTimer"/> created there would tick against that
    /// thread's own dispatcher, which nothing pumps, and would simply never
    /// fire. The expiry callback still has to get back onto the UI thread
    /// before touching bound state, the same as <see cref="OnAgentActivity"/>.
    /// </summary>
    private void RestartDecay(ref Timer? timer, TimeSpan window, Action expired)
    {
        if (timer is null)
        {
            timer = new Timer(_ => OnUiThread(expired), null, window, Timeout.InfiniteTimeSpan);
            return;
        }

        timer.Change(window, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Runs something on the UI thread, whatever thread this is called from.
    /// Applied directly rather than dispatched when there is no
    /// <see cref="Application"/> to own a dispatcher at all — a headless test
    /// driving this without a running WPF application, where "direct" and "on
    /// the UI thread" mean the same nonexistent thing.
    /// </summary>
    private static void OnUiThread(Action work)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess()) { work(); return; }

        dispatcher.BeginInvoke(work);
    }
}
