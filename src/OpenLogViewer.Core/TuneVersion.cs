namespace OpenLogViewer.Core;

/// <summary>
/// One tune, as it stood at a moment worth keeping.
///
/// <para>
/// The thing this replaces is a folder of files called <c>claude01.msq</c>
/// through <c>claude7.msq</c>, one of them spelled <c>claud02</c>, beside a
/// <c>Before Fuel Cleanup.msq</c> and a <c>CurrentTune.msq</c> that cannot tell
/// you whether it is what the controller is running. Every one of those
/// filenames is somebody trying to record <em>why</em> in the only field the
/// filesystem gave them.
/// </para>
/// <para>
/// So a version carries the why. What it was for, which fixes it was meant to
/// address, what it came from, and whether it was ever committed to flash — and
/// the tune itself sits beside it as an ordinary <c>.msq</c> that TunerStudio
/// can still open, because a version-control system nobody else can read is a
/// trap rather than a feature.
/// </para>
/// </summary>
public sealed record TuneVersion
{
    /// <summary>Short and ordered — "v4" — so it can be said aloud and sorted.</summary>
    public required string Id { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>The firmware this came off, so a version is never read against the wrong one.</summary>
    public string Signature { get; init; } = "";

    /// <summary>
    /// What the bytes come to, so the same tune is never stored twice.
    ///
    /// Pressing burn twice, or reading the tune at the start of two sessions
    /// that changed nothing, should not make two versions. Identity is what the
    /// controller holds, not when somebody looked at it.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>The version this was made from, or empty for the first.</summary>
    public string Parent { get; init; } = "";

    /// <summary>Why this exists, in the words of whoever made it.</summary>
    public string Note { get; init; } = "";

    /// <summary>The fixes this version was made to address, by id.</summary>
    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>
    /// Whether this was committed to flash.
    ///
    /// Worth recording rather than inferring: a tune written but not burned is
    /// gone at the next power cycle, so a log recorded after one is evidence
    /// about a tune the controller may no longer be running.
    /// </summary>
    public bool Burned { get; init; }

    /// <summary>The file, relative to the project's folder.</summary>
    public required string File { get; init; }

    /// <summary>How this reads in a list.</summary>
    public string Summary =>
        $"{Id} · {At:yyyy-MM-dd HH:mm}{(Burned ? " · burned" : "")}"
        + (Note.Length > 0 ? $" — {Note}" : "");
}

/// <summary>
/// What changed between two versions, said in settings rather than in bytes.
/// </summary>
/// <param name="From">The earlier version's id.</param>
/// <param name="To">The later one's.</param>
/// <param name="Differences">Every setting the two disagree about.</param>
public sealed record VersionDifference(
    string From, string To, IReadOnlyList<TuneDifference> Differences)
{
    public bool IsEmpty => Differences.Count == 0;

    /// <summary>One line, for a list or a status bar.</summary>
    public string Summary =>
        IsEmpty
            ? $"{From} and {To} hold the same tune."
            : $"{Differences.Count:N0} setting{(Differences.Count == 1 ? "" : "s")} differ "
              + $"between {From} and {To}.";
}
