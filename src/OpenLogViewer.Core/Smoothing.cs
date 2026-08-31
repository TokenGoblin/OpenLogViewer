namespace OpenLogViewer.Core;

/// <summary>How hard to smooth a trace, in the terms a person picks from.</summary>
public enum SmoothingLevel
{
    /// <summary>As logged.</summary>
    None = 0,

    /// <summary>Five samples: takes the fuzz off without moving anything.</summary>
    Light = 1,

    /// <summary>Fifteen: a noisy sensor becomes a readable line.</summary>
    Medium = 2,

    /// <summary>Fifty-one: the shape only, for a channel that is mostly noise.</summary>
    Strong = 3,
}

/// <summary>
/// Smoothing a channel for the eye, without touching what it holds.
///
/// <para>
/// <b>This is a way of drawing, not a way of measuring.</b> A smoothed AFR hides
/// exactly the single-sample lean excursion that damages a piston, so nothing
/// that judges an engine may see a smoothed number — the insights, the VE
/// calibration, the histogram, the statistics and every export read the channel
/// as logged. What smoothing changes is the line on the plot and the figure read
/// off it, which is a question about eyesight rather than about the engine.
/// </para>
/// <para>
/// <b>A median, not an average.</b> Sensor noise arrives as spikes, and a mean
/// smears each spike across the whole window — one bad sample in fifteen moves
/// the line for fifteen samples, which is worse than the spike. A median throws
/// the spike away and leaves the level and, importantly, the edges: a step from
/// one pressure to another survives a median and is rounded off by a mean.
/// </para>
/// <para>
/// Counted in samples rather than in seconds, deliberately. Noise of this kind
/// is per reading — one sample of it is one bad reading, whether they arrive at
/// 1 Hz or 50 — and a window stated in time smooths nothing at all on a slow log
/// and destroys a fast one.
/// </para>
/// </summary>
public static class Smoothing
{
    /// <summary>How many samples each level takes the median of.</summary>
    public static int Window(SmoothingLevel level) => level switch
    {
        SmoothingLevel.Light => 5,
        SmoothingLevel.Medium => 15,
        SmoothingLevel.Strong => 51,
        _ => 1,
    };

    /// <summary>What to call a level on a menu.</summary>
    public static string Name(SmoothingLevel level) => level switch
    {
        SmoothingLevel.Light => "Light",
        SmoothingLevel.Medium => "Medium",
        SmoothingLevel.Strong => "Strong",
        _ => "None",
    };

    /// <summary>
    /// A moving median of the values, the same length as the input.
    ///
    /// <para>
    /// The window shrinks at the two ends rather than being padded, so the first
    /// and last samples are the median of what there is either side of them. A
    /// padded end invents data and bends the line towards whatever was invented,
    /// which on a trace people read the start and finish of is the worst place
    /// to do it.
    /// </para>
    /// <para>
    /// A sample that is itself missing stays missing, so the pen still lifts
    /// across a gap in the log. Missing samples inside a window are passed over
    /// rather than counted, so a run of them thins the evidence rather than
    /// poisoning it.
    /// </para>
    /// </summary>
    public static double[] Median(IReadOnlyList<double> values, int window)
    {
        ArgumentNullException.ThrowIfNull(values);

        var smoothed = new double[values.Count];

        if (window <= 1)
        {
            for (int i = 0; i < values.Count; i++) smoothed[i] = values[i];
            return smoothed;
        }

        int half = window / 2;
        var buffer = new double[window];

        for (int i = 0; i < values.Count; i++)
        {
            if (double.IsNaN(values[i])) { smoothed[i] = double.NaN; continue; }

            int from = Math.Max(0, i - half);
            int to = Math.Min(values.Count - 1, i + half);
            int count = 0;

            for (int j = from; j <= to; j++)
                if (!double.IsNaN(values[j])) buffer[count++] = values[j];

            if (count == 0) { smoothed[i] = double.NaN; continue; }

            Array.Sort(buffer, 0, count);

            smoothed[i] = count % 2 == 1
                ? buffer[count / 2]
                : (buffer[(count / 2) - 1] + buffer[count / 2]) / 2;
        }

        return smoothed;
    }
}
