namespace OpenLogViewer.Core;

/// <summary>A channel refitted locally: the value, and how fast it is changing.</summary>
/// <param name="Value">The fitted value at each sample — smoothed, but honestly so.</param>
/// <param name="SlopePerSecond">
/// The derivative at each sample, in the channel's own units per second.
/// </param>
public sealed record ChannelFit(double[] Value, double[] SlopePerSecond);

/// <summary>
/// How fast a channel is changing, worked out well enough to measure with.
///
/// <para>
/// <b>This is the opposite of <see cref="Smoothing"/>, and the distinction is
/// load-bearing.</b> That one is a moving median, for the eye, and its own
/// documentation forbids anything that judges an engine from seeing it. This one
/// exists to be measured from: a virtual dyno is a differentiated speed trace and
/// almost nothing else, so the quality of the answer here <em>is</em> the quality
/// of the dyno.
/// </para>
/// <para>
/// A median is exactly the wrong tool for that. It is non-linear, it has no
/// derivative worth the name, and it deliberately discards the local slope that
/// is the whole quantity being sought. Differencing consecutive samples is worse
/// still — road speed off a vehicle sensor arrives quantised to whole units and
/// updated a handful of times a second, and subtracting one noisy number from
/// another and dividing by a small time amplifies the noise by the reciprocal of
/// that time. A tenth of a mile an hour of jitter over a fiftieth of a second is
/// five miles an hour per second of invented acceleration, which on a fifteen
/// hundred kilogram car is a hundred horsepower that is not there.
/// </para>
/// <para>
/// So the fit is done properly: around every sample a quadratic is fitted to the
/// points nearby by weighted least squares, and the slope of that quadratic at
/// the sample is the answer. Fitting a curve rather than a line matters — a
/// straight line through a curving signal has its slope pulled towards the chord,
/// which flattens exactly the peaks a dyno is looking for.
/// </para>
/// <para>
/// <b>Against the timestamps, not against the sample numbers.</b> The textbook
/// method for this — Savitzky-Golay — precomputes one set of coefficients and
/// assumes every sample sits the same distance from the last. Logs are not like
/// that. Intervals jitter, a controller misses a frame under load, and a link
/// stalls; <see cref="LogDocument.MedianSampleInterval"/> and
/// <see cref="LogDocument.GapThreshold"/> exist because of it. Coefficients that
/// assume even spacing, applied to uneven spacing, return a derivative scaled by
/// whatever the spacing really was, and nothing in the answer says so. Fitting
/// against the real timestamps costs a small matrix per sample and cannot be
/// wrong in that particular way.
/// </para>
/// <para>
/// <b>The window is in seconds, not in samples</b> — the other way round from
/// <see cref="Smoothing"/>, on purpose. Sensor noise is per reading, so a window
/// against it is counted in readings. What is sought here is a physical rate, and
/// how much of the signal to look at is set by how quickly the thing itself
/// changes rather than by how often the logger happened to write it down. A
/// window in samples would mean half a second of road at 10 Hz and a tenth of a
/// second at 50 Hz, and the same car would make different power on two loggers.
/// </para>
/// </summary>
public static class RateOfChange
{
    /// <summary>
    /// The window to fit over, in seconds, where nothing better is known.
    ///
    /// Half a second is long enough to bury the quantisation of a road speed
    /// sensor and short enough to keep the shape of a pull: a strong car passes
    /// through about four hundred rpm in that time, which is well inside the
    /// width of any feature worth seeing.
    /// </summary>
    public const double DefaultWindowSeconds = 0.5;

    /// <summary>Points needed before a quadratic is fitted rather than a line.</summary>
    private const int PointsForCurve = 4;

    /// <summary>
    /// Fits the channel and returns both the fitted value and its rate of change.
    ///
    /// <paramref name="gapThreshold"/> is the interval past which two samples are
    /// not treated as neighbours. A window never reaches across one: the log did
    /// not record what happened in the hole, and a fit that spans it draws a line
    /// through the missing part and reports its slope with the same confidence as
    /// any other. Pass zero to disable the check.
    /// </summary>
    public static ChannelFit Fit(
        ReadOnlySpan<double> values,
        ReadOnlySpan<double> times,
        double windowSeconds = DefaultWindowSeconds,
        double gapThreshold = 0)
    {
        int n = values.Length;

        if (times.Length != n)
        {
            throw new ArgumentException(
                "A channel and its time base must be the same length.", nameof(times));
        }

        if (!(windowSeconds > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSeconds), "The window must be a positive time.");
        }

        var fitted = new double[n];
        var slope = new double[n];

        if (n == 0) return new ChannelFit(fitted, slope);

        // Which run of uninterrupted samples each one belongs to. Worked out once
        // rather than per sample, since every window has to be clipped to it.
        var runStart = new int[n];
        var runEnd = new int[n];
        MarkRuns(times, gapThreshold, runStart, runEnd);

        double half = windowSeconds / 2;

        for (int i = 0; i < n; i++)
        {
            if (!double.IsFinite(values[i]) || !double.IsFinite(times[i]))
            {
                // A missing reading stays missing. It must not be filled in from
                // its neighbours: a dropout in the middle of a pull is a hole in
                // the evidence, and the pen has to lift.
                fitted[i] = double.NaN;
                slope[i] = double.NaN;
                continue;
            }

            Sums sums = Accumulate(values, times, i, runStart[i], runEnd[i], half);

            Solve(sums, half, out fitted[i], out slope[i]);
        }

        return new ChannelFit(fitted, slope);
    }

    /// <summary>
    /// The same, for a channel of a log, taking the log's own gap threshold.
    ///
    /// The samples are widened to double on the way in. A channel holds floats,
    /// which is ample for a reading but not for the sums below: the fourth moment
    /// of the window multiplies a value by itself several times over, and doing
    /// that in single precision throws away digits the determinant then divides
    /// by.
    /// </summary>
    public static ChannelFit Fit(
        LogDocument log, LogChannel channel, double windowSeconds = DefaultWindowSeconds)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(channel);

        return Fit(Widen(channel), Widen(log.Time), windowSeconds, log.GapThreshold);
    }

    private static double[] Widen(LogChannel channel)
    {
        var values = new double[channel.Length];

        for (int i = 0; i < values.Length; i++) values[i] = channel.At(i);

        return values;
    }

    /// <summary>Just the rate, for callers that do not want the fitted value.</summary>
    public static double[] PerSecond(
        ReadOnlySpan<double> values,
        ReadOnlySpan<double> times,
        double windowSeconds = DefaultWindowSeconds,
        double gapThreshold = 0) =>
        Fit(values, times, windowSeconds, gapThreshold).SlopePerSecond;

    // ----- the fit --------------------------------------------------------------

    /// <summary>
    /// Weighted sums of the window, in a coordinate scaled to the half-window.
    ///
    /// Scaled because the alternative conditions the matrix on the length of the
    /// window in seconds: at a half-window of a quarter second the fourth moment
    /// of the unscaled coordinate is about four thousandths of a millionth, and
    /// asking for the determinant of a matrix built from numbers like that is
    /// asking for the answer to be rounding error. In the scaled coordinate every
    /// moment is of order one however long the window is.
    /// </summary>
    private struct Sums
    {
        public double W0, W1, W2, W3, W4;   // moments of the position
        public double V0, V1, V2;           // the same, against the value
        public int Points;
    }

    private static Sums Accumulate(
        ReadOnlySpan<double> values, ReadOnlySpan<double> times,
        int i, int lo, int hi, double half)
    {
        Sums sums = default;

        double t0 = times[i];

        // Out from the sample in both directions, stopping at the edge of the
        // window rather than scanning the whole run.
        for (int j = i; j >= lo; j--)
        {
            double x = times[j] - t0;
            if (x < -half) break;

            Add(ref sums, x / half, values[j]);
        }

        for (int j = i + 1; j <= hi; j++)
        {
            double x = times[j] - t0;
            if (x > half) break;

            Add(ref sums, x / half, values[j]);
        }

        return sums;
    }

    private static void Add(ref Sums s, double u, double y)
    {
        if (!double.IsFinite(y)) return;

        // Tricube, the weighting local regression is usually done with. It falls
        // to nothing at the edge of the window rather than stopping abruptly, so
        // a sample entering or leaving as the window slides does not step the
        // answer — a rectangular window makes a derivative that ripples at the
        // sample rate, which looks like real detail and is not.
        double a = Math.Abs(u);
        if (a >= 1) return;

        double t = 1 - (a * a * a);
        double w = t * t * t;

        if (!(w > 0)) return;

        double wu = w * u;
        double wuu = wu * u;

        s.W0 += w;
        s.W1 += wu;
        s.W2 += wuu;
        s.W3 += wuu * u;
        s.W4 += wuu * u * u;

        s.V0 += w * y;
        s.V1 += wu * y;
        s.V2 += wuu * y;

        s.Points++;
    }

    /// <summary>
    /// Solves the fit for the value and the slope at the sample.
    ///
    /// The sample sits at the origin of the fitted coordinate, so the constant
    /// term is the fitted value there and the linear term is its slope — no
    /// evaluation of the polynomial is needed, only its coefficients.
    /// </summary>
    private static void Solve(in Sums s, double half, out double value, out double slope)
    {
        value = double.NaN;
        slope = double.NaN;

        if (s.Points >= PointsForCurve)
        {
            double d =
                (s.W0 * ((s.W2 * s.W4) - (s.W3 * s.W3)))
                - (s.W1 * ((s.W1 * s.W4) - (s.W3 * s.W2)))
                + (s.W2 * ((s.W1 * s.W3) - (s.W2 * s.W2)));

            // Points spread over too little of the window leave the quadratic
            // underdetermined — four readings at almost the same instant say
            // nothing about curvature. Falling back to a line is better than
            // dividing by very nearly nothing.
            if (Math.Abs(d) > 1e-9 * s.W0 * s.W0 * s.W0)
            {
                double dValue =
                    (s.V0 * ((s.W2 * s.W4) - (s.W3 * s.W3)))
                    - (s.W1 * ((s.V1 * s.W4) - (s.W3 * s.V2)))
                    + (s.W2 * ((s.V1 * s.W3) - (s.W2 * s.V2)));

                double dSlope =
                    (s.W0 * ((s.V1 * s.W4) - (s.W3 * s.V2)))
                    - (s.V0 * ((s.W1 * s.W4) - (s.W3 * s.W2)))
                    + (s.W2 * ((s.W1 * s.V2) - (s.V1 * s.W2)));

                value = dValue / d;
                slope = dSlope / d / half;

                return;
            }
        }

        if (s.Points >= 2)
        {
            double d = (s.W0 * s.W2) - (s.W1 * s.W1);

            if (Math.Abs(d) > 1e-9 * s.W0 * s.W0)
            {
                value = ((s.V0 * s.W2) - (s.V1 * s.W1)) / d;
                slope = (((s.W0 * s.V1) - (s.W1 * s.V0)) / d) / half;
            }
        }
    }

    // ----- runs -----------------------------------------------------------------

    /// <summary>
    /// Marks the stretches of samples that are neighbours of one another.
    ///
    /// A run is broken by a gap longer than the threshold, and by time running
    /// backwards — which happens in a log stitched together from two sessions and
    /// would otherwise put the end of one next to the start of the other and fit a
    /// line between them. Two samples sharing a timestamp are not a break;
    /// duplicated stamps are common where a logger writes faster than its clock
    /// resolves, and the fit copes with them.
    /// </summary>
    private static void MarkRuns(
        ReadOnlySpan<double> times, double gapThreshold, Span<int> start, Span<int> end)
    {
        int n = times.Length;
        int s = 0;

        for (int i = 1; i <= n; i++)
        {
            bool cut = i == n;

            if (!cut)
            {
                double dt = times[i] - times[i - 1];

                cut = !double.IsFinite(dt)
                      || dt < 0
                      || (gapThreshold > 0 && dt > gapThreshold);
            }

            if (!cut) continue;

            for (int j = s; j < i; j++)
            {
                start[j] = s;
                end[j] = i - 1;
            }

            s = i;
        }
    }
}
