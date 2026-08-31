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

    /// <summary>Every channel available, live or from the log in hand.</summary>
    IReadOnlyList<AgentChannel> Channels();

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
}

/// <summary>One insight, flattened for a reader that is not a window.</summary>
public sealed record AgentFinding(string Level, string Topic, string Title, string Detail)
{
    public string Evidence { get; init; } = "";
}
