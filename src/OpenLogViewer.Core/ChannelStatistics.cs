namespace OpenLogViewer.Core;

/// <summary>
/// Summary of one channel over a span of samples.
///
/// The question a tuner asks of a marked region is "what did this do here" —
/// how hot did it get, how lean did it go, what did it average. A cursor gives
/// one instant; this gives the span.
/// </summary>
public readonly record struct ChannelStatistics(double Min, double Max, double Mean, int Count)
{
    public static ChannelStatistics Empty { get; } = new(double.NaN, double.NaN, double.NaN, 0);

    public bool HasData => Count > 0;

    /// <summary>Difference between the extremes, or NaN when there is no data.</summary>
    public double Span => Count > 0 ? Max - Min : double.NaN;

    /// <summary>
    /// Summarises samples <paramref name="first"/>..<paramref name="last"/>
    /// inclusive. Missing readings are skipped rather than counted as zero,
    /// which would drag an average toward nothing.
    /// </summary>
    public static ChannelStatistics Over(LogChannel channel, int first, int last)
    {
        double[] values = channel.Values;
        int from = Math.Max(0, Math.Min(first, last));
        int to = Math.Min(values.Length - 1, Math.Max(first, last));

        if (values.Length == 0 || from > to) return Empty;

        double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
        int count = 0;

        for (int i = from; i <= to; i++)
        {
            double v = values[i];
            if (double.IsNaN(v)) continue;

            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
            count++;
        }

        return count == 0 ? Empty : new ChannelStatistics(min, max, sum / count, count);
    }
}
