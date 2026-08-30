namespace OpenLogViewer.Core;

/// <summary>Where in a log a condition held.</summary>
public sealed record LogSearchResult
{
    /// <summary>
    /// Stretches of the log the condition held over, in order. Consecutive
    /// matching samples are one run rather than many.
    /// </summary>
    public required IReadOnlyList<(int First, int Last)> Runs { get; init; }

    /// <summary>Samples that matched, which is not the same as the number of runs.</summary>
    public required int Matches { get; init; }

    /// <summary>
    /// Samples the condition could not be judged at, because a channel it names
    /// had no reading there.
    ///
    /// Counted apart from the misses. A comparison against a missing reading is
    /// not false, it is unanswerable, and quietly folding those into "did not
    /// match" would report a confident answer about a stretch of log that has
    /// nothing to say.
    /// </summary>
    public required int Unknown { get; init; }

    /// <summary>Why the search could not be run, where that is the case.</summary>
    public string? Problem { get; init; }

    public bool HasProblem => Problem is { Length: > 0 };

    public bool IsEmpty => Runs.Count == 0;

    /// <summary>The run holding a sample, or the next one after it.</summary>
    public int RunAtOrAfter(int sample)
    {
        for (int i = 0; i < Runs.Count; i++)
            if (Runs[i].Last >= sample) return i;

        return -1;
    }
}

/// <summary>
/// Finds where in a log a condition was true — "RPM &gt; 4000 &amp;&amp; TPS &gt;
/// 80", or anything else the calculated-channel syntax can say.
///
/// The same expression language, deliberately: a tuner who has written one
/// calculated channel already knows how to write a search, and a search that
/// proves useful can be pasted into a calculated channel or a filter without
/// translation.
///
/// <para>
/// What this adds over a filter is <em>where</em>. A filter answers which samples
/// to count and throws the rest away; this answers which moments of the drive to
/// go and look at, and leaves the log alone.
/// </para>
/// </summary>
public static class LogSearch
{
    /// <summary>
    /// Samples that may fall short inside a run without ending it.
    ///
    /// A signal sitting near its threshold crosses it repeatedly — RPM wandering
    /// about 4,000 against "RPM &gt; 4000" alternates true and false every few
    /// samples. Strictly consecutive runs would report that as fifty separate
    /// findings when it is plainly one, so a brief dip below is bridged, as it is
    /// when a table cell's visits are worked out.
    /// </summary>
    public const int DefaultGapTolerance = 2;

    /// <summary>
    /// Evaluates <paramref name="condition"/> at every sample and groups the
    /// matches into runs.
    /// </summary>
    /// <param name="mask">
    /// Filters. A sample the user has filtered out is not searched — the filters
    /// say which part of the drive is under consideration, and a search that
    /// jumped to a moment they exclude would be answering a question that was
    /// not asked.
    /// </param>
    public static LogSearchResult Find(
        LogDocument document, string condition,
        SampleMask? mask = null, int gapTolerance = DefaultGapTolerance)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(condition)) return Nothing(null);

        var available = new Dictionary<string, LogChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (LogChannel channel in document.Channels) available[channel.Name] = channel;
        available[document.Time.Name] = document.Time;

        if (!MathExpression.TryParse(condition, available.Keys, out MathExpression? expression, out string? error))
            return Nothing(error);

        LogChannel[] sources = [.. expression.References.Select(name => available[name])];

        int count = document.SampleCount;
        var runs = new List<(int First, int Last)>();
        int matches = 0, unknown = 0;
        int start = -1, previous = -1;

        Span<double> inputs = sources.Length <= 16
            ? stackalloc double[sources.Length]
            : new double[sources.Length];

        for (int i = 0; i < count; i++)
        {
            if (mask is not null && !mask[i]) continue;

            for (int s = 0; s < sources.Length; s++) inputs[s] = sources[s].At(i);

            double value = expression.Evaluate(inputs);

            if (double.IsNaN(value))
            {
                unknown++;
                continue;
            }

            // Anything that is not zero counts, so a bare channel name reads as
            // "wherever this is not nothing" and a comparison reads as itself.
            // Infinities are not a reading, whatever their sign.
            if (!double.IsFinite(value) || value == 0) continue;

            matches++;

            if (start < 0)
            {
                start = previous = i;
                continue;
            }

            if (i - previous <= gapTolerance + 1)
            {
                previous = i;
                continue;
            }

            runs.Add((start, previous));
            start = previous = i;
        }

        if (start >= 0) runs.Add((start, previous));

        return new LogSearchResult { Runs = runs, Matches = matches, Unknown = unknown };
    }

    private static LogSearchResult Nothing(string? problem) =>
        new() { Runs = [], Matches = 0, Unknown = 0, Problem = problem };
}
