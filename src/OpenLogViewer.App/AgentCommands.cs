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

    // ----- propose / apply ---------------------------------------------------

    /// <summary>
    /// Works out what a batch of settings and table cells would change, without
    /// sending anything.
    ///
    /// Answerable even when writes are not armed and even against a tune opened
    /// from a file, because nothing here can reach an ECU: a placeholder tune —
    /// all noughts, from a definition alone — is the one thing refused, since a
    /// diff against it would not mean anything.
    /// </summary>
    internal TuneProposalResult AgentProposeTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells)
    {
        if (_ecuTune is not { } tune || TuneIsPlaceholder)
        {
            return new TuneProposalResult(
                [], ["no tune has been read"],
                "No tune has been read, so there is nothing to propose changes against.");
        }

        (EcuTune working, List<string> rejected) = BuildProposal(tune, settings, cells);
        IReadOnlyList<TuneDifference> changes = TuneCompare.Compare(working, tune);

        return new TuneProposalResult(changes, rejected, SummarizeProposal(changes, rejected));
    }

    /// <summary>
    /// Applies the same shape of batch for real: the usual <see cref="Refusal"/>
    /// gate once for the whole thing, then each setting and cell through
    /// <see cref="AgentSetSetting"/>/<see cref="AgentSetTableCell"/> in turn — the
    /// same machinery a lone call already uses, so this is not a second way of
    /// writing to the ECU. Keeps a version of what landed on success, the same
    /// way a person pressing "keep tune" would.
    /// </summary>
    internal TuneApplyResult AgentApplyTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string note)
    {
        using IDisposable origin = WireOriginScope.Enter(WireOrigin.Agent);

        if (Refusal() is { } refused) return new TuneApplyResult(refused, [], [], "");
        if (_ecuTune is not { } tune)
            return new TuneApplyResult(new AgentRefusal("no tune has been read"), [], [], "");

        // The same working copy that answers /tune/propose, so what is about to
        // be sent cannot silently differ from what a caller who proposed first
        // was shown.
        (EcuTune working, List<string> rejected) = BuildProposal(tune, settings, cells);
        IReadOnlyList<TuneDifference> wanted = TuneCompare.Compare(working, tune);

        if (wanted.Count == 0)
        {
            return new TuneApplyResult(null, [], rejected,
                rejected.Count > 0
                    ? "Nothing valid to apply. " + string.Join(" ", rejected)
                    : "Nothing would change; the ECU already holds these values.");
        }

        var appliedSettingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appliedTableConstants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProposedSetting s in settings)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || tune.Constant(s.Name) is null) continue;

            if (AgentSetSetting(s.Name, s.Value) is { } settingRefused)
                rejected.Add($"{s.Name}: {settingRefused.Reason}");
            else
                appliedSettingNames.Add(s.Name);
        }

        foreach (ProposedCell c in cells)
        {
            TuneTable? shape = EcuTables.FirstOrDefault(
                t => t.Name.Equals(c.Table, StringComparison.OrdinalIgnoreCase));

            if (shape is null) continue; // already rejected by BuildProposal

            if (AgentSetTableCell(c.Table, c.Column, c.Row, c.Value) is { } cellRefused)
                rejected.Add($"{c.Table}[{c.Column},{c.Row}]: {cellRefused.Reason}");
            else if (ConstantFor(shape) is { } constant)
                appliedTableConstants.Add(constant.Name);
        }

        List<TuneDifference> applied =
        [
            .. wanted.Where(d =>
                appliedSettingNames.Contains(d.Name) || appliedTableConstants.Contains(d.Name)),
        ];

        if (applied.Count > 0)
        {
            string described = string.Join("; ", applied.Select(d => d.Summary));
            string kept = string.IsNullOrWhiteSpace(note)
                ? $"Agent applied: {described}"
                : $"{note} — applied: {described}";

            // Best effort: a successful write to the ECU must never be reported
            // as failed because there was nowhere to record it, the same rule
            // KeepBurnedTune already follows for a burn.
            AgentKeepTune(kept);
        }

        string summary = applied.Count == 0
            ? "Nothing was applied. " + string.Join(" ", rejected)
            : $"Applied {applied.Count:N0} change{(applied.Count == 1 ? "" : "s")}."
              + (rejected.Count > 0 ? " " + string.Join(" ", rejected) : "");

        return new TuneApplyResult(null, applied, rejected, summary);
    }

    /// <summary>
    /// Lays a batch of proposed settings and table cells over a private copy of
    /// the tune in hand, and says which of them could not go in.
    ///
    /// <b>The copy is what makes this safe to call before writes are armed.</b>
    /// <see cref="EcuTune.FromPages"/> over cloned page bytes shares nothing with
    /// <paramref name="tune"/>'s own storage, so every edit below — through the
    /// same <see cref="TuneSettingsEdit"/> and <see cref="TuneEdit"/> a real edit
    /// uses — lands on bytes nobody else is looking at.
    /// </summary>
    private (EcuTune Working, List<string> Rejected) BuildProposal(
        EcuTune tune, IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells)
    {
        EcuTune working = EcuTune.FromPages(tune.Layout, [.. tune.Pages.Select(p => p.ToArray())]);
        var rejected = new List<string>();

        if (settings.Count > 0)
        {
            var edit = new TuneSettingsEdit(working);

            foreach (ProposedSetting s in settings)
            {
                if (string.IsNullOrWhiteSpace(s.Name)) { rejected.Add("a setting with no name"); continue; }

                if (working.Constant(s.Name) is null)
                {
                    rejected.Add($"{s.Name}: no such setting");
                    continue;
                }

                if (!edit.Set(s.Name, s.Value))
                {
                    rejected.Add(
                        $"{s.Name}: {s.Value} would not go in — out of range, or not a number");
                }
            }

            foreach (TuneWrite write in edit.Writes()) working.Accept(write);
        }

        foreach (ProposedCell c in cells)
        {
            TuneTable? shape = EcuTables.FirstOrDefault(
                t => t.Name.Equals(c.Table, StringComparison.OrdinalIgnoreCase));

            if (shape is null) { rejected.Add($"{c.Table}: no such table"); continue; }

            if (c.Column < 0 || c.Column >= shape.Columns || c.Row < 0 || c.Row >= shape.Rows)
            {
                rejected.Add(
                    $"{c.Table}[{c.Column},{c.Row}]: not in the table ({shape.Columns} by {shape.Rows})");
                continue;
            }

            if (ConstantFor(shape) is not { } constant)
            {
                rejected.Add($"{c.Table}: this table cannot be edited for this firmware");
                continue;
            }

            // Read fresh off the working copy rather than off what is on screen,
            // so an earlier item in this same batch sharing bytes with this
            // table is seen by the next one.
            if (working.Table(shape.Name, constant.Name, shape.X.Constant, shape.Y.Constant) is not { } fresh)
            {
                rejected.Add($"{c.Table}: could not be read off the working copy");
                continue;
            }

            var cellEdit = new TuneEdit(fresh, constant);
            cellEdit.Set(TuneSelection.Cell(c.Column, c.Row), c.Value);

            if (cellEdit.Encode(working) is { } write) working.Accept(write);
        }

        return (working, rejected);
    }

    private static string SummarizeProposal(IReadOnlyList<TuneDifference> changes, IReadOnlyList<string> rejected)
    {
        string head = changes.Count == 0
            ? "Nothing would change."
            : $"{changes.Count:N0} setting{(changes.Count == 1 ? "" : "s")} would change.";

        return rejected.Count == 0 ? head : $"{head} {string.Join(" ", rejected)}";
    }

    // ----- staging ------------------------------------------------------------

    /// <summary>
    /// Writes a tune out as a real <c>.msq</c> in the staging folder — the tune
    /// in hand, or what a batch of proposed changes would make it.
    /// </summary>
    internal AgentStageResult AgentStageTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string filename)
    {
        if (_ecuTune is not { } tune || TuneIsPlaceholder)
        {
            return new AgentStageResult(
                new AgentRefusal("no tune has been read",
                    "There is nothing to stage. Connect to an ECU and read its tune, or open a saved one."),
                null);
        }

        // BuildProposal's rejections are not surfaced here: a name that made no
        // sense simply does not move any bytes, and the file staged is still
        // exactly what the rest of the batch would produce.
        EcuTune toWrite = settings.Count > 0 || cells.Count > 0
            ? BuildProposal(tune, settings, cells).Working
            : tune;

        string name = string.IsNullOrWhiteSpace(filename)
            ? $"tune-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.msq"
            : filename;

        try
        {
            string xml = MsqWriter.Write(toWrite, _ecuSignature, comment: "Staged by an agent.");
            string path = new AgentStaging(Workspace).Stage(name, xml);

            return new AgentStageResult(null, FileInfoOf(path));
        }
        catch (ArgumentException e)
        {
            return new AgentStageResult(new AgentRefusal("could not stage that file", e.Message), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AgentStageResult(new AgentRefusal("could not write the staged file", e.Message), null);
        }
    }

    /// <summary>Writes one named table out as an import-ready CSV in the staging folder.</summary>
    internal AgentStageResult AgentStageTable(string name, string filename)
    {
        TuneTable? table = EcuTables.FirstOrDefault(
            t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (table is null)
            return new AgentStageResult(new AgentRefusal("no such table", name), null);

        string fileName = string.IsNullOrWhiteSpace(filename)
            ? $"{SafeStem(table.Name)}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv"
            : filename;

        try
        {
            using var text = new StringWriter();
            CsvExport.WriteTuneTable(text, table);

            string path = new AgentStaging(Workspace).Stage(fileName, text.ToString());

            return new AgentStageResult(null, FileInfoOf(path));
        }
        catch (ArgumentException e)
        {
            return new AgentStageResult(new AgentRefusal("could not stage that file", e.Message), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AgentStageResult(new AgentRefusal("could not write the staged file", e.Message), null);
        }
    }

    /// <summary>What is sitting in the staging folder right now.</summary>
    internal IReadOnlyList<AgentStagedFile> AgentListStaged() => new AgentStaging(Workspace).ListStaged();

    private static AgentStagedFile FileInfoOf(string path)
    {
        var info = new FileInfo(path);
        return new AgentStagedFile(info.Name, info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>A table name made safe to open a file with, for the default staged name.</summary>
    private static string SafeStem(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();

        return new string([.. name.Select(c => c == ' ' ? '-' : invalid.Contains(c) ? '_' : c)]);
    }
}
