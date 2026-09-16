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

/// <summary>
/// What a table-cell write actually did.
///
/// Found live, on a real Speeduino: a cell out of the firmware's declared
/// range is clamped rather than refused (see <c>TuneEdit.Hold</c>), and an
/// early version of this route reported the request back as though it had
/// landed unchanged — a caller had no way to know without reading the table
/// again. <see cref="Value"/> is read straight from the edit that was just
/// encoded and sent, not from anything UI-bound: a first attempt at this fix
/// re-read the on-screen table instead, and on a live connection that update
/// is dispatched to the UI thread asynchronously, so the "fixed" version could
/// still report a stale value from before the write landed.
/// </summary>
public sealed record AgentCellWrite(AgentRefusal? Refusal, double Value);

/// <summary>
/// The operating limits a write can run into, stated up front rather than
/// learned by being refused.
///
/// Found live, on a real Speeduino: an agent sweeping every setting hit the
/// rate limit repeatedly and had no way to know the shape of it — how many
/// writes, in what window — short of counting 409s and guessing. This is that
/// shape, put where the same "GET /" call that lists the routes already
/// answers, so the first thing an agent does after connecting can tell it
/// how fast it is allowed to go rather than finding out by being refused.
/// </summary>
public sealed record AgentLimits
{
    /// <summary>Writes allowed inside <see cref="WriteRateWindowSeconds"/> before the next one is refused.</summary>
    public required int WriteRateCount { get; init; }

    public required double WriteRateWindowSeconds { get; init; }

    /// <summary>
    /// A single write cannot move a setting by more than this fraction of its
    /// declared range - 0.5 means half. Refused rather than clamped, so a
    /// caller finds out rather than silently landing on the boundary.
    /// </summary>
    public required double MaxChangeFractionOfRange { get; init; }

    /// <summary>True for every write this API can make. There is no route that burns.</summary>
    public bool Burns { get; init; }

    /// <summary>True: arming clears on every disconnect and is never persisted.</summary>
    public bool WritesArmedClearsOnDisconnect { get; init; } = true;

    /// <summary>
    /// True: a setting <c>DangerousConstants</c> recognises (a rev limiter, a
    /// launch control RPM, and the like) is refused without
    /// <c>confirmDangerous:true</c> on the same call.
    /// </summary>
    public bool DangerousSettingsNeedConfirmation { get; init; } = true;
}

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

    /// <summary>The write-path limits - rate, per-write magnitude, and the rest of it.</summary>
    AgentLimits Limits();

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

    /// <summary>
    /// Puts one cell of one table into the controller's working memory. See
    /// <see cref="SetSetting"/> — with one difference: a table cell out of the
    /// firmware's declared range is clamped into it rather than refused (right
    /// for a person scaling a whole table by a percentage; a single agent write
    /// needs to know when that happened), so the value that lands is part of
    /// what this returns rather than something a caller has to read back to find.
    /// </summary>
    AgentCellWrite SetTableCell(
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

    // ----- propose / apply ----------------------------------------------------

    /// <summary>
    /// Works out what a batch of settings and table cells would change, on a
    /// private copy of the tune in hand.
    ///
    /// Nothing here touches the tune the rest of the application is looking at,
    /// let alone the ECU — which is what makes this answerable even when writes
    /// are not armed. "Would this be safe to send" and "what would it do" are
    /// different questions, and this is only the second one.
    /// </summary>
    TuneProposalResult ProposeTune(IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells);

    /// <summary>
    /// Applies the same shape of batch for real, through the existing per-setting
    /// and per-cell write paths — no second write implementation, so this
    /// inherits the same read-back-and-verify safety a lone <see cref="SetSetting"/>
    /// or <see cref="SetTableCell"/> already has, <paramref name="note"/> serving
    /// as the rationale each of those requires and <paramref name="confirmDangerous"/>
    /// carried to each of them the same way. Keeps a version of the result on
    /// success, the same as <see cref="KeepTune"/> does by hand.
    /// </summary>
    TuneApplyResult ApplyTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string note,
        bool confirmDangerous = false);

    // ----- staging --------------------------------------------------------

    /// <summary>
    /// Writes a tune out as a real <c>.msq</c> in the staging folder — the tune
    /// in hand when no changes are given, or what a batch of proposed changes
    /// would make it, laid over it the same way <see cref="ProposeTune"/> does.
    ///
    /// Not gated by anything a write to the ECU is gated by: this never reaches
    /// a controller, so it works whether or not writing is armed. The only thing
    /// that can refuse it is a name that tries to leave the staging folder.
    /// </summary>
    AgentStageResult StageTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string filename);

    /// <summary>Writes one named table out as a CSV a person can import in TunerStudio.</summary>
    AgentStageResult StageTable(string name, string filename);

    /// <summary>What is sitting in the staging folder right now.</summary>
    IReadOnlyList<AgentStagedFile> ListStaged();

    // ----- firmware definitions ---------------------------------------------

    /// <summary>
    /// What the last connection attempt needed and could not find, or null when
    /// nothing is missing.
    ///
    /// <para>
    /// The application has no HTTP client and will never grow one; an agent has
    /// the internet this deliberately lacks. So this is the application saying
    /// precisely what it needs — what the ECU called itself, what is already on
    /// this machine, and, where the firmware's licence allows it,
    /// <see cref="AgentDefinitionNeed.Sources"/> to fetch it from.
    /// </para>
    /// <para>
    /// <b>An empty <see cref="AgentDefinitionNeed.Sources"/> is an instruction,
    /// not a gap.</b> MegaSquirt's definitions are licensed in a way that does
    /// not permit this, and their publisher defends the download against
    /// automation; for those, <see cref="AgentDefinitionNeed.Guidance"/> says
    /// where a person should look instead. Do not go around it.
    /// </para>
    /// </summary>
    AgentDefinitionNeed? DefinitionNeeded();

    /// <summary>
    /// Keeps a definition, having checked it is one and that it declares the
    /// signature this ECU reported.
    ///
    /// <paramref name="path"/> is the ordinary way: fetch the file, then name
    /// it. <paramref name="content"/> exists for a caller with no filesystem,
    /// and is the worse path — these files run to half a megabyte.
    /// </summary>
    AgentDefinitionImported ImportDefinition(string path, string content, string source, string name);
}

/// <summary>A definition already on this machine that nearly, but does not, match.</summary>
public sealed record AgentNearMiss(string Name, string Signature, string Path);

/// <summary>What the application needs before it can decode a connected ECU.</summary>
public sealed record AgentDefinitionNeed(
    IReadOnlyList<string> Identity,
    string Family,
    string Signature,
    string Version,
    string Filename,
    IReadOnlyList<AgentNearMiss> Nearby,
    string Folder,
    string Where,
    IReadOnlyList<string> Sources,
    string Guidance);

/// <summary>What keeping a definition did, or why it was refused.</summary>
public sealed record AgentDefinitionImported(
    string Problem, string Path, string Name, string Signature, string Source)
{
    public bool Accepted => Problem.Length == 0;
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

/// <summary>One setting proposed to change, before anything is decided about it.</summary>
public sealed record ProposedSetting(string Name, double Value);

/// <summary>One table cell proposed to change.</summary>
public sealed record ProposedCell(string Table, int Column, int Row, double Value);

/// <summary>
/// What a batch of proposed settings and cells would do, worked out without
/// touching the tune the application is looking at or the ECU behind it.
/// </summary>
/// <param name="Changes">
/// What would actually change, in the same shape <see cref="TuneCompare"/>
/// already reports a difference in — before and after, and how many cells for
/// a table.
/// </param>
/// <param name="Rejected">
/// Requested changes that could not go in: a name this firmware has no
/// constant for, a value out of its range, a cell outside a table's shape.
/// Left out of <paramref name="Changes"/> rather than silently dropped, so a
/// batch that only partly makes sense says which part.
/// </param>
public sealed record TuneProposalResult(
    IReadOnlyList<TuneDifference> Changes, IReadOnlyList<string> Rejected, string Summary)
{
    /// <summary>Nothing in the batch would change anything.</summary>
    public bool IsEmpty => Changes.Count == 0;
}

/// <summary>
/// What applying a proposal did, or why it was refused before anything was
/// attempted.
/// </summary>
/// <param name="Refusal">Set exactly when nothing was sent.</param>
/// <param name="Applied">What was actually sent and taken, in the same shape a proposal reports.</param>
/// <param name="Rejected">Requested changes that did not go in, whether caught before sending or refused by the ECU itself.</param>
public sealed record TuneApplyResult(
    AgentRefusal? Refusal, IReadOnlyList<TuneDifference> Applied, IReadOnlyList<string> Rejected, string Summary);

/// <summary>One file sitting in the staging folder.</summary>
public sealed record AgentStagedFile(string Name, string Path, long Bytes, DateTime WrittenAt);

/// <summary>
/// What staging a file did, or why there was nothing to stage.
///
/// Reuses the shape of a write refusal even though staging is never gated the
/// way a write is — the two situations both come down to "here is why nothing
/// was written," and an agent acting on one should be able to act on the other
/// the same way.
/// </summary>
public sealed record AgentStageResult(AgentRefusal? Refusal, AgentStagedFile? File);

/// <summary>One insight, flattened for a reader that is not a window.</summary>
public sealed record AgentFinding(string Level, string Topic, string Title, string Detail)
{
    public string Evidence { get; init; } = "";
}
