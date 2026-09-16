namespace OpenLogViewer.Core;

/// <summary>One channel, as an agent needs to know it before asking for values.</summary>
public sealed record AgentChannel(string Name, string Units, int Digits)
{
    /// <summary>The job it does, where one was recognised — "EngineSpeed", "Mixture".</summary>
    public string Role { get; init; } = "";
}

/// <summary>What the application is doing right now.</summary>
public sealed record AgentState
{
    /// <summary>"live", "log" or "idle".</summary>
    public required string Mode { get; init; }

    /// <summary>The firmware signature, on a live session.</summary>
    public string Signature { get; init; } = "";

    /// <summary>The log's file name, when one is open.</summary>
    public string File { get; init; } = "";

    public int Samples { get; init; }

    public double Seconds { get; init; }

    /// <summary>Polls a second, on a live session.</summary>
    public double Rate { get; init; }

    public int Channels { get; init; }

    /// <summary>Whether a tune has been read off a controller.</summary>
    public bool HasTune { get; init; }

    /// <summary>
    /// Whether writing is armed. False is the resting state and the answer
    /// after every disconnect.
    /// </summary>
    public bool WritesArmed { get; init; }

    public string Error { get; init; } = "";
}

/// <summary>A refusal, said in a way an agent can act on rather than guess at.</summary>
public sealed record AgentRefusal(string Reason, string Detail = "");

/// <summary>One entry from the wire trace, flattened for a reader that is not a window.</summary>
public sealed record AgentWireEvent(
    long Sequence, DateTime At, string Context, string Origin, int Attempt,
    double ElapsedMs, string Outcome, string FailureKind, string Detail);

/// <summary>A rollup of recent wire activity, for "is the link healthy" at a glance.</summary>
public sealed record AgentWireHealth(
    int Sampled,
    int Failures,
    double SuccessRate,
    IReadOnlyDictionary<string, int> FailuresByKind,
    double? SinceLastSuccessSeconds,
    double? LongestRecentMs);

/// <summary>
/// Everything the agent API is allowed to ask the application for.
///
/// <para>
/// An interface rather than a reference to the view model, so the server can be
/// built and tested without a window — and, more usefully, so the whole of what
/// an agent can reach is one file long. Anything not on here is not reachable,
/// which is a property worth being able to check by reading rather than by
/// tracing calls.
/// </para>
/// <para>
/// <b>Reading is always allowed; writing is not.</b> The write members return a
/// refusal unless somebody has armed writing in the application, and there is
/// deliberately no burn: a burn is permanent, and the one thing on the far side
/// of this interface is a person who can see what is about to happen. An agent
/// may move a number in the controller's working memory, which the key turning
/// off undoes; making that survive is a decision for whoever is standing next to
/// the engine.
/// </para>
/// </summary>
public interface IAgentBridge
{
    /// <summary>What is loaded or connected, and whether writing is armed.</summary>
    AgentState State();

    /// <summary>
    /// Every channel available, live or from the log in hand.
    ///
    /// <paramref name="raw"/> asks for every channel a connected ECU's firmware
    /// decodes, whether or not its own datalog definition names it — the
    /// human-curated set is the default because most callers want names a
    /// preset or filter will match, but an agent hunting for something nobody
    /// thought to log needs the rest of it too.
    /// </summary>
    IReadOnlyList<AgentChannel> Channels(bool raw = false);

    /// <summary>What went over the wire recently, and how healthy the link looks.</summary>
    AgentWireHealth WireHealth();

    /// <summary>The most recent wire events, newest first.</summary>
    IReadOnlyList<AgentWireEvent> WireEvents(int count);

    /// <summary>
    /// The samples of one channel, newest last. <paramref name="seconds"/> of
    /// zero means all of them.
    /// </summary>
    IReadOnlyList<double> Values(string channel, double seconds);

    /// <summary>The time column matching <see cref="Values"/>.</summary>
    IReadOnlyList<double> Times(double seconds);

    /// <summary>The findings the Insights window shows, as text.</summary>
    IReadOnlyList<AgentFinding> Insights();

    /// <summary>Every setting in the tune, or a refusal when none has been read.</summary>
    IReadOnlyDictionary<string, double> TuneValues();

    /// <summary>One table by name, with its axes.</summary>
    TuneTable? Table(string name);

    /// <summary>The names of every table the firmware declares.</summary>
    IReadOnlyList<string> TableNames();

    // ----- the project ------------------------------------------------------

    /// <summary>
    /// The vehicle's project as prose, or an empty string when none is open.
    ///
    /// The reason this is on the read side rather than being something an agent
    /// assembles for itself: what is worth knowing at the start of a session is
    /// what was already tried and what it did, and no amount of reading the
    /// current log recovers that. It is the same thing a scratchpad does for a
    /// model working on code.
    /// </summary>
    string ProjectBrief();

    /// <summary>Every vehicle that has a project, whether or not one is open.</summary>
    IReadOnlyList<string> Projects();

    /// <summary>
    /// Keeps the tune in hand as a version of the open project.
    ///
    /// Not a write to the ECU and not gated like one: this records what the
    /// controller already holds. Nothing about an engine changes.
    /// </summary>
    AgentRefusal? KeepTune(string note);

    /// <summary>
    /// What changed between two versions, setting by setting, or a refusal
    /// naming which of them could not be read.
    /// </summary>
    string CompareVersions(string from, string to);

    /// <summary>
    /// Records the log in hand as a sitting, raising a fix for anything newly
    /// warned about and noting a repeat against the fix already tracking it.
    /// </summary>
    AgentRefusal? RecordSitting(string note);

    /// <summary>
    /// Adds a fix, or moves one already there. <paramref name="id"/> empty
    /// raises a new one and the id it was given comes back in the brief.
    ///
    /// Deliberately not a write to the ECU: this changes the record of what is
    /// being worked on, which is safe, and is the one thing an agent should be
    /// able to do freely.
    /// </summary>
    AgentRefusal? NoteFix(string id, string title, string detail, string state, string change);

    // ----- the guarded half --------------------------------------------------

    /// <summary>
    /// Puts one setting into the controller's working memory.
    ///
    /// <paramref name="rationale"/> is required, not decorative: a refusal of
    /// "no rationale given" is how this stops being a socket that moves numbers
    /// for no stated reason. <paramref name="confirmDangerous"/> has to be true
    /// for a constant <see cref="DangerousConstants"/> recognises — a rev
    /// limiter, a launch RPM, a boost or fuel/ignition cut — so that class of
    /// write cannot happen by the same one-line call as an ordinary VE cell.
    ///
    /// Returns null when it was done, or a refusal saying why not. Nothing here
    /// burns; a power cycle undoes whatever this does.
    /// </summary>
    AgentRefusal? SetSetting(string name, double value, string rationale, bool confirmDangerous = false);

    /// <summary>Puts one cell of one table into the controller's working memory. See <see cref="SetSetting"/>.</summary>
    AgentRefusal? SetTableCell(
        string table, int column, int row, double value, string rationale, bool confirmDangerous = false);

    // ----- composition, for a session that does not want to ask forty times -----

    /// <summary>
    /// Every scalar and every table in one payload, each setting carrying the
    /// units/options/range <see cref="TuneConstant"/> already knows about it.
    ///
    /// What <see cref="TuneValues"/> plus <see cref="TableNames"/> plus one
    /// <see cref="Table"/> call per name would otherwise cost a session that
    /// wants to reason about the whole tune at once, rather than a setting at a
    /// time.
    /// </summary>
    AgentTuneFull TuneFull();

    /// <summary>
    /// Every channel's samples in one call, windowed by <paramref name="seconds"/>
    /// exactly as <see cref="Values"/> is, and then thinned by
    /// <paramref name="decimate"/> — keeping one sample in every that many, one
    /// for zero or one. A full, undecimated log can run to tens of megabytes as
    /// JSON, so this is the pragmatic knob rather than a new mechanism.
    /// </summary>
    AgentLogFull LogFull(double seconds, int decimate);

    /// <summary>
    /// One call bundling what is worth reading before anything else: the
    /// project's own prose, what is connected, how big the tune in hand is, the
    /// latest findings and the link's health. The literal "read this first" for
    /// a fresh session — deliberately not the full tune or the full log, which
    /// stay a further call away once this says they are worth making.
    /// </summary>
    AgentContext Context();
}

/// <summary>One setting, with the metadata worth knowing before writing back to it.</summary>
public sealed record AgentSetting(string Name, double Value, string Units)
{
    /// <summary>What each value means, in order from zero. Empty where the firmware named none.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The firmware's own usable range, in its displayed units. Null where it states none.</summary>
    public double? Low { get; init; }

    public double? High { get; init; }
}

/// <summary>
/// One table's cells and axes, flattened the same way a single <c>/table</c>
/// answer is — <see cref="AgentTuneFull"/> is many of these plus every scalar.
/// </summary>
public sealed record AgentTable(
    string Name, string Units, int Columns, int Rows,
    IReadOnlyList<double> XBins, IReadOnlyList<double> YBins,
    string XUnits, string YUnits, string XConstant, string YConstant,
    IReadOnlyList<IReadOnlyList<double>> Values);

/// <summary>The whole tune: every setting and every table, in one payload.</summary>
public sealed record AgentTuneFull(IReadOnlyList<AgentSetting> Settings, IReadOnlyList<AgentTable> Tables);

/// <summary>One channel's samples, sharing the time column every other channel in the same answer does.</summary>
public sealed record AgentChannelSamples(string Name, string Units, IReadOnlyList<double> Values);

/// <summary>Every channel of the log in hand, in one call.</summary>
public sealed record AgentLogFull(IReadOnlyList<double> Times, IReadOnlyList<AgentChannelSamples> Channels);

/// <summary>How big the tune in hand is, without paying for the whole of it.</summary>
public sealed record AgentTuneSummary(int SettingCount, IReadOnlyList<string> Tables);

/// <summary>The "read this first" bundle for a session that has just started.</summary>
public sealed record AgentContext(
    string ProjectBrief,
    AgentState State,
    AgentTuneSummary Tune,
    IReadOnlyList<AgentFinding> Insights,
    AgentWireHealth WireHealth);

/// <summary>One insight, flattened for a reader that is not a window.</summary>
public sealed record AgentFinding(string Level, string Topic, string Title, string Detail)
{
    public string Evidence { get; init; } = "";
}
