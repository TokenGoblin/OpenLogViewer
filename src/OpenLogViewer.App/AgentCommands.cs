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
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string note,
        bool confirmDangerous = false)
    {
        using IDisposable origin = WireOriginScope.Enter(WireOrigin.Agent);

        if (string.IsNullOrWhiteSpace(note))
        {
            return new TuneApplyResult(
                new AgentRefusal(
                    "no rationale given",
                    "Say in one line why this change is being made. It doubles as the version note."),
                [], [], "");
        }

        if (GeneralRefusal() is { } refused) return new TuneApplyResult(refused, [], [], "");
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

            if (AgentSetSetting(s.Name, s.Value, note, confirmDangerous) is { } settingRefused)
                rejected.Add($"{s.Name}: {settingRefused.Reason}");
            else
                appliedSettingNames.Add(s.Name);
        }

        foreach (ProposedCell c in cells)
        {
            TuneTable? shape = EcuTables.FirstOrDefault(
                t => t.Name.Equals(c.Table, StringComparison.OrdinalIgnoreCase));

            if (shape is null) continue; // already rejected by BuildProposal

            if (AgentSetTableCell(c.Table, c.Column, c.Row, c.Value, note, confirmDangerous) is { } cellRefused)
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
