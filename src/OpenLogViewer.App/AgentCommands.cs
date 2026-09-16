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
    ///
    /// <paramref name="rationale"/> is required: a write carrying no stated
    /// reason is refused before anything else is even checked, so the guard
    /// cannot be routed around by leaving it blank. <paramref name="confirmDangerous"/>
    /// only matters when <paramref name="name"/> matches <see cref="DangerousConstants"/>.
    /// </summary>
    internal AgentRefusal? AgentSetSetting(
        string name, double value, string rationale, bool confirmDangerous = false)
    {
        using IDisposable origin = WireOriginScope.Enter(WireOrigin.Agent);

        if (string.IsNullOrWhiteSpace(rationale))
        {
            return new AgentRefusal(
                "no rationale given",
                "Say in one line why this change is being made. It is echoed back with the write.");
        }

        if (GeneralRefusal() is { } refused) return refused;
        if (_ecuTune is not { } tune) return new AgentRefusal("no tune has been read");

        _settingsEdit ??= new TuneSettingsEdit(tune);

        if (tune.Constant(name) is not { } constant)
            return new AgentRefusal("no such setting", $"This firmware declares no \"{name}\".");

        if (DangerousRefusal(name, confirmDangerous) is { } dangerous) return dangerous;

        double before = _settingsEdit.Original(name);

        if (!_settingsEdit.Set(name, value))
        {
            return new AgentRefusal(
                "the value would not go in",
                "It is out of the range the firmware declares, or the setting is not a number.");
        }

        if (MagnitudeRefusal(before, value, constant.Low, constant.High) is { } tooLarge)
        {
            // Taken back out of the pending edit: a refused write must leave
            // nothing behind for the next call to accidentally send.
            _settingsEdit.Revert(name);
            return tooLarge;
        }

        RecordAgentWrite();

        string said = WriteSettingsToEcu();

        OnSettingChanged();

        return said.StartsWith("Sent", StringComparison.OrdinalIgnoreCase)
            ? null
            : new AgentRefusal("the write did not go through", said);
    }

    /// <summary>Sets one cell of one table, through the same path as an edit on screen.</summary>
    internal AgentRefusal? AgentSetTableCell(
        string name, int column, int row, double value, string rationale, bool confirmDangerous = false)
    {
        using IDisposable origin = WireOriginScope.Enter(WireOrigin.Agent);

        if (string.IsNullOrWhiteSpace(rationale))
        {
            return new AgentRefusal(
                "no rationale given",
                "Say in one line why this change is being made. It is echoed back with the write.");
        }

        if (GeneralRefusal() is { } refused) return refused;

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

        // The constant actually behind the grid, where the firmware names one.
        // Dangerous-constant matching and the magnitude limit are judged
        // against that, not the table's display title, since a title like "VE
        // Table" carries none of the firmware's own spelling of anything.
        TuneConstant? constant = ConstantFor(table);

        if (DangerousRefusal(constant?.Name ?? "", confirmDangerous) is { } dangerous) return dangerous;

        double before = table.Values[column, row];

        if (constant is not null
            && MagnitudeRefusal(before, value, constant.Low, constant.High) is { } tooLarge)
        {
            return tooLarge;
        }

        // Selected the way clicking it selects it, which is what builds the edit
        // and points the calibration view at the same table the agent named.
        SelectedEcuTable = table;

        if (_tableEdit is not { } edit) return new AgentRefusal("that table cannot be edited");

        edit.Set(TuneSelection.Cell(column, row), value);

        RecordAgentWrite();

        string said = WriteTableToEcu();

        return said.StartsWith("Sent", StringComparison.OrdinalIgnoreCase)
            ? null
            : new AgentRefusal("the write did not go through", said);
    }

    /// <summary>
    /// The reasons a write is refused before anything about which setting or
    /// cell it names is even looked at, in the order worth hearing them.
    ///
    /// Kept ahead of resolving a table or constant name on purpose: whether
    /// writing is even possible right now — armed, connected, a real tune,
    /// the engine not mid-drive, not a runaway burst of calls — is a cheaper
    /// and more general question than "does this name exist", and a person
    /// reading a refusal should hear "writes are not armed" before "no such
    /// setting" rather than the other way round.
    /// </summary>
    private AgentRefusal? GeneralRefusal()
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

        if (RunningAboveIdleRefusal() is { } running) return running;

        if (RateLimitRefusal() is { } rate) return rate;

        return null;
    }

    /// <summary>
    /// Refuses a write to a constant <see cref="DangerousConstants"/>
    /// recognises unless <paramref name="confirmDangerous"/> says otherwise.
    ///
    /// Checked once the constant actually behind the write is known — a
    /// scalar's own name, or the constant behind a table's cells — rather
    /// than as part of <see cref="GeneralRefusal"/>, since it needs that name
    /// to judge anything at all.
    /// </summary>
    private AgentRefusal? DangerousRefusal(string touchedConstant, bool confirmDangerous)
    {
        if (touchedConstant.Length == 0 || confirmDangerous) return null;
        if (_tuneLayout is not { } layout) return null;
        if (DangerousConstants.Find(layout, touchedConstant) is not { } role) return null;

        return new AgentRefusal(
            "this setting needs confirmDangerous:true",
            $"\"{touchedConstant}\" matches the {role} guard — a setting whose job is to stop "
            + "the engine hurting itself. Pass confirmDangerous:true if this change is intentional.");
    }

    /// <summary>
    /// How far above idle the engine is allowed to be for an agent write to go
    /// through at all.
    ///
    /// A running, warmed engine idles somewhere under a thousand rpm on
    /// virtually anything this application talks to; twelve hundred leaves
    /// headroom for a fast idle or a cold start without allowing any real
    /// throttle input through, since even a light prod of the pedal moves RPM
    /// well past that within a second. The point is not to pick the exact edge
    /// of "idling" — it is to refuse the moment the car might be moving or
    /// under load, which is when a change lands somewhere the agent cannot see
    /// the consequence of before it happens.
    /// </summary>
    private const double IdleAdjacentRpm = 1200;

    /// <summary>
    /// Refuses a write while live RPM reads above <see cref="IdleAdjacentRpm"/>.
    ///
    /// Silently allows the write through — rather than refusing — when RPM
    /// cannot be found at all: a saved log with no engine-speed channel, or a
    /// session that never decoded one, is not "the engine is running above
    /// idle", it is "this cannot be answered", and the other gates (writes
    /// armed, an ECU actually connected, a live tune in hand) already do the
    /// job of keeping a write off anything that is not a live, connected
    /// controller.
    /// </summary>
    private AgentRefusal? RunningAboveIdleRefusal()
    {
        if (Document is not { } log) return null;
        if (ChannelRoles.Find(log, ChannelRole.EngineSpeed) is not { } channel) return null;
        if (channel.Length == 0) return null;

        double rpm = channel.At(channel.Length - 1);
        if (!double.IsFinite(rpm) || rpm <= IdleAdjacentRpm) return null;

        return new AgentRefusal(
            "the engine is running above idle",
            $"RPM reads {rpm:0}, above the {IdleAdjacentRpm:0} rpm guard. Writes are refused while "
            + "the engine may be under load or being driven; try again at idle or with it off.");
    }

    /// <summary>How many agent writes have actually reached the ECU recently, newest last.</summary>
    private readonly List<DateTime> _recentAgentWrites = [];

    /// <summary>
    /// The rate limit's window and count.
    ///
    /// Ten writes in five seconds is far beyond any sequence of settings a
    /// person reviewing each change could keep up with, and far below what a
    /// loop gone wrong — a retried failure, a script iterating every cell of a
    /// table with no pacing — produces in the same span. It exists to catch a
    /// runaway agent, not to pace a careful one.
    /// </summary>
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(5);

    private const int RateLimitCount = 10;

    private AgentRefusal? RateLimitRefusal()
    {
        DateTime now = DateTime.UtcNow;
        _recentAgentWrites.RemoveAll(at => now - at > RateLimitWindow);

        if (_recentAgentWrites.Count < RateLimitCount) return null;

        return new AgentRefusal(
            "too many writes in a short time",
            $"{_recentAgentWrites.Count} agent writes landed in the last "
            + $"{RateLimitWindow.TotalSeconds:0} seconds. Slow down, and check what each one did "
            + "before sending the next.");
    }

    /// <summary>Notes that a write actually reached the ECU, for <see cref="RateLimitRefusal"/>.</summary>
    private void RecordAgentWrite()
    {
        _recentAgentWrites.Add(DateTime.UtcNow);

        // A generous backstop rather than a rolling trim on every call: nothing
        // in normal use gets near it, since RateLimitRefusal already prunes
        // anything outside the window before this is ever reached.
        if (_recentAgentWrites.Count > 1000) _recentAgentWrites.RemoveRange(0, _recentAgentWrites.Count - 1000);
    }

    /// <summary>
    /// Refuses a single change bigger than half of the range the firmware
    /// itself declares for a setting.
    ///
    /// <para>
    /// Half, rather than any smaller fraction, because a real tuning step
    /// sometimes legitimately is large — moving a rev limiter by a thousand
    /// rpm for a different application is a real, intentional change, and a
    /// tight cap would just teach an agent to split every honest edit into
    /// several calls, which hides the total from this check rather than
    /// catching anything. What fifty per cent of a declared range does catch
    /// is the failure mode this exists for: a value entered in the wrong
    /// units, a misplaced decimal point, or an off-by-a-decade mistake, every
    /// one of which overshoots by far more than half of what the firmware
    /// itself says is a sane span for the setting.
    /// </para>
    /// <para>
    /// Silently allows the change when there is no declared range, or the
    /// prior value could not be read — there is nothing to judge proportion
    /// against, and the setting's own in-range check has already bounded what
    /// the new value can be.
    /// </para>
    /// </summary>
    private static AgentRefusal? MagnitudeRefusal(double before, double after, double low, double high)
    {
        if (double.IsNaN(before)) return null;

        double range = high - low;
        if (!double.IsFinite(range) || range <= 0) return null;

        double delta = Math.Abs(after - before);
        double limit = range * 0.5;

        if (delta <= limit) return null;

        return new AgentRefusal(
            "that change is too large for one write",
            $"Moving this by {delta:0.###} is more than half of its declared {low:0.###}–{high:0.###} "
            + "range in a single call. Make the change in smaller steps, checking what happened after each one.");
    }
}
