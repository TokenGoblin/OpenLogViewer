namespace OpenLogViewer.Core;

/// <summary>An annotation recorded alongside the samples, at <paramref name="Time"/> seconds.</summary>
public sealed record LogMarker(double Time, string Text);

/// <summary>
/// A fully decoded datalog: a time base plus a set of equal-length channels.
/// </summary>
public sealed class LogDocument
{
    public required string FilePath { get; init; }

    public required IReadOnlyList<LogChannel> Channels { get; init; }

    /// <summary>
    /// The time base in seconds. Always present; synthesised from the sample
    /// index if the source file carried no usable time column.
    /// </summary>
    public required LogChannel Time { get; init; }

    public int SampleCount => Time.Length;

    /// <summary>
    /// Whether a channel is the column serving as the time base.
    ///
    /// Matched by name, not by reference. <see cref="Time"/> is built from its
    /// own full-precision copy of the column, so it is never the same object as
    /// the entry in <see cref="Channels"/> and a reference comparison quietly
    /// matches nothing — which is how the time column ended up offered as a
    /// plottable channel. A synthesised base is named "Sample" and matches no
    /// real channel, which is correct: there is none to exclude.
    /// </summary>
    public bool IsTimeBase(LogChannel channel) =>
        ReferenceEquals(channel, Time)
        || channel.Name.Equals(Time.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Annotations captured during logging, ordered by time.</summary>
    public IReadOnlyList<LogMarker> Markers { get; init; } = [];

    /// <summary>
    /// The tune the log was recorded with, as MSQ XML, when the container
    /// embeds one. Its table axes can be used as histogram breakpoints.
    /// </summary>
    public string? EmbeddedTune { get; init; }

    /// <summary>ECU signature string, e.g. "MS2Extra comms342a2: MS2/Extra 3.4.2 release".</summary>
    public string? Signature { get; init; }

    /// <summary>Free-form capture metadata recorded by the logging application.</summary>
    public string? CaptureInfo { get; init; }

    public DateTimeOffset? RecordedAt { get; init; }

    /// <summary>Source format label for display, e.g. "MLG v2" or "MSL".</summary>
    public required string FormatName { get; init; }

    public double Duration => SampleCount == 0 ? 0 : Time.At(SampleCount - 1) - Time.At(0);

    private double? _medianInterval;

    /// <summary>
    /// Typical spacing between consecutive samples. The median is used rather
    /// than the mean so a pause in logging does not skew it.
    /// </summary>
    public double MedianSampleInterval => _medianInterval ??= ComputeMedianInterval();

    /// <summary>
    /// Time step beyond which two samples are considered separated by a gap in
    /// logging rather than normal jitter. Logs are routinely paused and resumed,
    /// and drawing straight through such a gap implies data that does not exist.
    /// </summary>
    public double GapThreshold
    {
        get
        {
            double median = MedianSampleInterval;
            return median > 0 ? median * 10 : double.PositiveInfinity;
        }
    }

    private double ComputeMedianInterval()
    {
        int count = Time.Length;
        if (count < 3) return 0;

        // Very long logs are sampled rather than measured in full; the median is
        // stable either way and this keeps loading O(1) in practice.
        int stride = Math.Max(1, (count - 1) / 20_000);
        var deltas = new List<double>((count - 1) / stride + 1);

        for (int i = stride; i < count; i += stride)
        {
            double delta = (Time.At(i) - Time.At(i - stride)) / stride;
            if (delta > 0) deltas.Add(delta);
        }

        if (deltas.Count == 0) return 0;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }

    public LogChannel? FindChannel(string name) =>
        Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Index of the sample at or just before <paramref name="seconds"/>.
    /// Binary search; the time base is monotonic by construction.
    /// </summary>
    public int IndexAtTime(double seconds)
    {
        int count = Time.Length;
        if (count == 0) return 0;

        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Time.At(mid) <= seconds) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
