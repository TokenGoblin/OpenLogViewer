namespace OpenLogViewer.Core;

/// <summary>Where a fix has got to.</summary>
public enum FixState
{
    /// <summary>Seen in the data, nothing changed yet.</summary>
    Open,

    /// <summary>A change was made. Whether it worked is not yet known.</summary>
    Applied,

    /// <summary>A later log says the change did what it was meant to.</summary>
    Verified,

    /// <summary>Dropped — wrong diagnosis, or not worth chasing.</summary>
    Abandoned,
}

/// <summary>
/// One thing wrong with a tune, and what has been done about it.
///
/// <para>
/// The unit the whole project turns on. Tuning is a long argument with an engine
/// conducted one change at a time, and the thing that is always lost between
/// sessions is not the numbers — those are in the logs — but <em>why</em> a
/// number was changed and whether it worked. A fix carries that.
/// </para>
/// </summary>
public sealed record TuningFix
{
    /// <summary>Short and stable, so it can be referred to across sessions.</summary>
    public required string Id { get; init; }

    /// <summary>What is wrong, in one line.</summary>
    public required string Title { get; init; }

    /// <summary>The reasoning: what was seen, and what it is thought to mean.</summary>
    public string Detail { get; init; } = "";

    public FixState State { get; init; } = FixState.Open;

    /// <summary>What was actually changed, once something was.</summary>
    public string Change { get; init; } = "";

    /// <summary>
    /// What has been observed about it since, newest last.
    ///
    /// A list rather than a field, because the useful record of a fix is the
    /// sequence — "still lean, less so", "gone above 4,000 but not below" — and
    /// overwriting one note with the next throws away the only evidence that the
    /// change is working at all.
    /// </summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    public DateTimeOffset Raised { get; init; } = DateTimeOffset.Now;

    /// <summary>When it was verified or abandoned; null while it is still live.</summary>
    public DateTimeOffset? Settled { get; init; }

    public bool IsOpen => State is FixState.Open or FixState.Applied;
}

/// <summary>One sitting with the car: a log looked at, and what it said.</summary>
public sealed record ProjectSession
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>The log's file name, not its path — paths move and mean nothing later.</summary>
    public string Log { get; init; } = "";

    public string Signature { get; init; } = "";

    public int Samples { get; init; }

    public double Seconds { get; init; }

    /// <summary>What the insights said, flattened to text.</summary>
    public IReadOnlyList<SessionFinding> Findings { get; init; } = [];

    /// <summary>Anything a person or an agent wanted to record about the sitting.</summary>
    public string Note { get; init; } = "";

    /// <summary>The findings worth acting on, which is what a summary leads with.</summary>
    public int Warnings => Findings.Count(f => f.Level is "Warning" or "Watch");
}

/// <summary>An insight as the project keeps it — the words, without the machinery.</summary>
public sealed record SessionFinding(string Level, string Topic, string Title)
{
    public string Detail { get; init; } = "";
}

/// <summary>
/// Everything known about one vehicle's tune, across every sitting.
///
/// <para>
/// This exists because the analysis is the easy half. A log tells you the
/// mixture is lean above 150 kPa; what it cannot tell you is that you already
/// knew that three weeks ago, added four per cent to the top of the VE table,
/// and it got better but not right. That is the half that lives in somebody's
/// head and is gone by the next session — and it is exactly the half an
/// assistant needs in order to be useful rather than to start over each time.
/// </para>
/// <para>
/// Kept as JSON and read back as prose. The file is the record; the prose is how
/// anything — a person, a model — actually reads it.
/// </para>
/// </summary>
public sealed record TuningProject
{
    /// <summary>What the car is called. Also its folder name.</summary>
    public required string Vehicle { get; init; }

    public string Engine { get; init; } = "";

    /// <summary>The firmware last seen on it, for spotting a project opened against the wrong car.</summary>
    public string Signature { get; init; } = "";

    /// <summary>Anything that stays true: fuel, injectors, what the car is for.</summary>
    public string Notes { get; init; } = "";

    public IReadOnlyList<ProjectSession> Sessions { get; init; } = [];

    public IReadOnlyList<TuningFix> Fixes { get; init; } = [];

    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;

    public IEnumerable<TuningFix> Open => Fixes.Where(f => f.IsOpen);

    /// <summary>Adds a sitting, newest last.</summary>
    public TuningProject With(ProjectSession session) =>
        this with { Sessions = [.. Sessions, session] };

    /// <summary>
    /// Adds a fix, or replaces the one with the same id.
    ///
    /// Replacing rather than appending, so that an agent told to move a fix from
    /// Open to Applied does not leave two of it.
    /// </summary>
    public TuningProject With(TuningFix fix)
    {
        ArgumentNullException.ThrowIfNull(fix);

        var kept = Fixes.Where(f => !f.Id.Equals(fix.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        kept.Add(fix);

        return this with { Fixes = kept };
    }

    /// <summary>The fix by that id, however it was capitalised.</summary>
    public TuningFix? Fix(string id) =>
        Fixes.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An id nothing else is using, made from the title.
    ///
    /// Words rather than a number, because these are read aloud in notes and
    /// referred to by an agent across sessions — "lean-under-load" survives being
    /// quoted from memory and "fix-7" does not.
    /// </summary>
    public string NewId(string title)
    {
        var slug = new System.Text.StringBuilder();

        foreach (char c in title ?? "")
        {
            if (char.IsLetterOrDigit(c)) slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');

            if (slug.Length >= 32) break;
        }

        string basis = slug.ToString().Trim('-');
        if (basis.Length == 0) basis = "fix";

        if (Fix(basis) is null) return basis;

        for (int n = 2; ; n++)
            if (Fix($"{basis}-{n}") is null) return $"{basis}-{n}";
    }
}
