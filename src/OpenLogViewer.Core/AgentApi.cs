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
    /// Returns null when it was done, or a refusal saying why not. Nothing here
    /// burns; a power cycle undoes whatever this does.
    /// </summary>
    AgentRefusal? SetSetting(string name, double value);

    /// <summary>Puts one cell of one table into the controller's working memory.</summary>
    AgentRefusal? SetTableCell(string table, int column, int row, double value);

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
    /// or <see cref="SetTableCell"/> already has. Keeps a version of the result
    /// on success, the same as <see cref="KeepTune"/> does by hand.
    /// </summary>
    TuneApplyResult ApplyTune(
        IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string note);

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
}

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
