namespace OpenLogViewer.Core;

/// <summary>One candidate delay and how well the log fits under it.</summary>
/// <param name="Samples">The delay in samples.</param>
/// <param name="Seconds">The same delay as a time.</param>
/// <param name="Disagreement">
/// Mean squared disagreement within cells at this delay — the number being
/// minimised. Lower means the readings that land in a cell agree with each other
/// more closely.
/// </param>
public readonly record struct DelayCandidate(int Samples, double Seconds, double Disagreement);

/// <summary>What a search of the log concluded about the sensor's delay.</summary>
public sealed record DelaySearchResult
{
    /// <summary>The delay that fitted best, in samples.</summary>
    public required int BestSamples { get; init; }

    public required double BestSeconds { get; init; }

    /// <summary>Every candidate tried, in order, for drawing or for inspection.</summary>
    public required IReadOnlyList<DelayCandidate> Curve { get; init; }

    /// <summary>
    /// How much better the best candidate is than no delay at all, as a
    /// percentage of the disagreement at zero. This is the size of the effect,
    /// and the thing that says whether the answer is worth acting on.
    /// </summary>
    public required double ImprovementPercent { get; init; }

    /// <summary>Samples every candidate was scored over — the same set for each.</summary>
    public required int SamplesScored { get; init; }

    /// <summary>
    /// The shortest and longest delay that fit about as well as the best one.
    ///
    /// A sweep does not come to a point. Several neighbouring candidates
    /// routinely sit within the noise of one another, and singling out the
    /// lowest of those as <em>the</em> delay claims a precision the log does not
    /// carry — on a real log the first four candidates can differ by less than
    /// their own sampling error while the curve as a whole rises steeply. This
    /// is the band that cannot be told apart from the best, and it is the honest
    /// width of the answer.
    /// </summary>
    public required double LowSeconds { get; init; }

    public required double HighSeconds { get; init; }

    /// <summary>True when no delay at all fits as well as the best one does.</summary>
    public bool NoneIsPlausible => LowSeconds <= 0;

    /// <summary>True when the band is one candidate wide — a delay pinned down.</summary>
    public bool IsPrecise => HighSeconds - LowSeconds < 1e-9;

    /// <summary>
    /// Why the answer should not be believed, where that can be said. Set means
    /// <see cref="BestSamples"/> is not an answer, whatever it holds.
    /// </summary>
    public string? Problem { get; init; }

    public bool HasProblem => Problem is { Length: > 0 };
}

/// <summary>
/// Finds how long the wideband takes to see the mixture that was metered, by
/// asking the log.
///
/// <para>
/// The idea is short. Samples landing in one cell of the table were all taken at
/// about the same operating point, so the mixtures they measured ought to agree
/// with each other. Pair each cell with a reading taken too early or too late
/// and readings from neighbouring conditions leak into it, and the cell's
/// samples start to disagree. So sweep the delay, measure how much the readings
/// within each cell disagree, and take the delay where they agree best.
/// </para>
/// <para>
/// This is signal alignment rather than tuning: it says nothing about whether
/// the tune is right, only about which reading belongs to which moment. A badly
/// mistuned engine aligns exactly as well as a well tuned one, because every
/// cell is judged against its own readings and not against a target.
/// </para>
/// <para>
/// <b>It needs the engine to have changed.</b> Held at one operating point, every
/// delay pairs a cell with readings from the same conditions and they all score
/// alike — there is no information in the log to find the answer with. That is
/// not a failure to detect a small delay, it is the absence of evidence either
/// way, and it is reported as such rather than resolved into whichever candidate
/// noise happened to favour.
/// </para>
/// </summary>
public static class WidebandDelay
{
    /// <summary>
    /// The longest delay worth considering. Past about a second this stops being
    /// a pipe and a sensor and becomes a way of pairing a reading with an
    /// unrelated part of the drive, where any apparent fit is coincidence.
    /// </summary>
    public const double MaximumSeconds = 1.0;

    /// <summary>
    /// Most candidates to try. On a fast log stepping one sample at a time would
    /// mean hundreds of passes for a resolution far finer than the quantity is
    /// known to; the step widens instead.
    /// </summary>
    private const int MaximumCandidates = 40;

    /// <summary>
    /// Samples the search needs before it will answer at all. Fewer than this and
    /// the differences between candidates are noise being read as a curve.
    /// </summary>
    private const int MinimumSamples = 200;

    /// <summary>
    /// How much the choice of delay must matter at all before any of it is
    /// believed — the whole sweep's range, as a percentage of its starting
    /// point.
    ///
    /// This is the sensitivity of the log to the question, and it is deliberately
    /// not "how much better the best candidate is than none". Those come apart
    /// exactly where it matters: an engine held at one operating point produces a
    /// flat sweep because no delay can be told from another, and an engine with a
    /// genuinely undelayed sensor also produces no improvement over zero — but
    /// its sweep is not flat, it climbs steeply away from zero. Judging by
    /// improvement alone would call the second case uninformative and refuse to
    /// report the correct answer of none.
    ///
    /// Two per cent. Below it the minimum is wherever rounding put it, and
    /// returning that would be inventing a measurement: the number would look
    /// exactly like a real answer and be worth nothing.
    /// </summary>
    private const double MinimumSensitivityPercent = 2.0;

    /// <summary>
    /// Sweeps the delay and reports which fits best.
    /// </summary>
    /// <param name="table">The grid whose cells samples are grouped into.</param>
    /// <param name="sampleInterval">Seconds per sample, for reporting a time.</param>
    /// <param name="maxSeconds">Longest delay to consider.</param>
    public static DelaySearchResult Find(
        TuneTable table,
        LogChannel rpm, LogChannel load, LogChannel afr, LogChannel target,
        int firstSample, int lastSample, double sampleInterval,
        SampleMask? mask = null,
        double maxSeconds = MaximumSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);

        int length = new[] { rpm.Length, load.Length, afr.Length, target.Length }.Min();
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(length - 1, Math.Max(firstSample, lastSample));

        int longest = sampleInterval > 0
            ? (int)Math.Round(Math.Min(maxSeconds, MaximumSeconds) / sampleInterval)
            : 0;

        longest = Math.Max(0, Math.Min(longest, to - from));

        // Every candidate is scored over the same samples. Letting the window
        // shrink as the delay grows would score each candidate on a slightly
        // different drive, and the comparison between them is the whole answer.
        int last = to - longest;

        if (longest == 0 || last < from)
            return Nothing("This log is too short, or its samples too far apart, to look for a delay.");

        // Cells do not move with the delay — only which reading is paired with
        // them — so they are worked out once.
        int[] cells = new int[last - from + 1];
        int scored = 0;

        for (int i = from; i <= last; i++)
        {
            cells[i - from] = -1;

            if (mask is not null && !mask[i]) continue;

            double x = rpm.At(i), y = load.At(i), wanted = target.At(i);
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(wanted) || wanted <= 0) continue;

            cells[i - from] = (Nearest(table.X.Breakpoints, x) * table.Rows)
                              + Nearest(table.Y.Breakpoints, y);
            scored++;
        }

        if (scored < MinimumSamples)
            return Nothing(
                $"Only {scored:N0} samples are usable here, which is too few to tell a delay from "
                + "noise. Widen the time range, or loosen the filters.");

        int step = Math.Max(1, (int)Math.Ceiling((double)longest / MaximumCandidates));
        var curve = new List<DelayCandidate>();

        int cellCount = table.Columns * table.Rows;
        var sums = new double[cellCount];
        var squares = new double[cellCount];
        var counts = new int[cellCount];

        for (int delay = 0; delay <= longest; delay += step)
        {
            Array.Clear(sums);
            Array.Clear(squares);
            Array.Clear(counts);

            int used = 0;

            for (int i = from; i <= last; i++)
            {
                int cell = cells[i - from];
                if (cell < 0) continue;

                int at = i + delay;
                if (mask is not null && !mask[at]) continue;

                double measured = afr.At(at);
                if (double.IsNaN(measured) || measured <= 0) continue;

                double ratio = measured / target.At(i);

                sums[cell] += ratio;
                squares[cell] += ratio * ratio;
                counts[cell]++;
                used++;
            }

            curve.Add(new DelayCandidate(delay, delay * sampleInterval, Disagreement(sums, squares, counts)));
        }

        return Judge(curve, scored);
    }

    /// <summary>
    /// Mean squared disagreement within cells: each cell's own spread about its
    /// own mean, pooled over every cell and divided by the samples behind them.
    ///
    /// A cell holding one sample contributes nothing — a single reading agrees
    /// with itself at every delay, and counting it would dilute the measure with
    /// cells that cannot inform it.
    /// </summary>
    private static double Disagreement(double[] sums, double[] squares, int[] counts)
    {
        double total = 0;
        int n = 0;

        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] < 2) continue;

            // Sum of squared deviations about the cell's mean.
            double deviation = squares[i] - (sums[i] * sums[i] / counts[i]);

            // Rounding can carry this a hair below zero on a cell whose samples
            // are identical.
            total += Math.Max(0, deviation);
            n += counts[i];
        }

        return n == 0 ? double.NaN : total / n;
    }

    /// <summary>
    /// Reads the sweep, and decides whether it is an answer.
    ///
    /// Three ways it is not. A curve with no usable figures in it at all. A curve
    /// so flat that the delay makes no difference, which is what an engine held
    /// at one operating point produces — its minimum is wherever rounding put it.
    /// And a minimum sitting on the last candidate, which is not a minimum but
    /// the edge of where we looked.
    ///
    /// A minimum at zero is none of those. It is the answer that the sensor's
    /// delay is shorter than this log can resolve, and it is reported as one so
    /// long as the sweep was sensitive enough for that to mean something.
    /// </summary>
    private static DelaySearchResult Judge(List<DelayCandidate> curve, int scored)
    {
        DelayCandidate best = curve[0];
        double highest = double.NegativeInfinity;

        foreach (DelayCandidate candidate in curve)
        {
            if (!double.IsFinite(candidate.Disagreement)) continue;

            if (!double.IsFinite(best.Disagreement) || candidate.Disagreement < best.Disagreement)
                best = candidate;

            if (candidate.Disagreement > highest) highest = candidate.Disagreement;
        }

        double baseline = curve[0].Disagreement;

        if (!double.IsFinite(baseline) || !double.IsFinite(best.Disagreement) || baseline <= 0)
            return Nothing("The readings in this log do not vary enough to find a delay from.")
                with { Curve = curve, SamplesScored = scored };

        // How far two of these figures can differ before the difference means
        // something. Each is a mean of squared deviations over `scored` samples,
        // whose own sampling error is about sqrt(2/n) of itself — so anything
        // within that of the minimum fits indistinguishably well.
        double tolerance = best.Disagreement * Math.Sqrt(2.0 / Math.Max(1, scored));

        double low = best.Seconds, high = best.Seconds;

        foreach (DelayCandidate candidate in curve)
        {
            if (!double.IsFinite(candidate.Disagreement)) continue;
            if (candidate.Disagreement > best.Disagreement + tolerance) continue;

            low = Math.Min(low, candidate.Seconds);
            high = Math.Max(high, candidate.Seconds);
        }

        var result = new DelaySearchResult
        {
            BestSamples = best.Samples,
            BestSeconds = best.Seconds,
            Curve = curve,
            ImprovementPercent = (baseline - best.Disagreement) / baseline * 100,
            SamplesScored = scored,
            LowSeconds = low,
            HighSeconds = high,
        };

        // How much the answer depends on the delay at all. Flat means the log
        // holds no evidence either way, whatever its minimum happens to be.
        double sensitivity = (highest - best.Disagreement) / baseline * 100;

        if (sensitivity < MinimumSensitivityPercent)
            return result with
            {
                Problem =
                    "Changing the delay barely changes the fit, so this log cannot say what it is. "
                    + "That usually means it holds no sharp changes — every delay pairs a cell with "
                    + "readings from the same conditions, so none of them can be told apart. Log a "
                    + "few quick throttle changes and try again.",
            };

        if (curve.Count > 1 && best.Samples == curve[^1].Samples)
            return result with
            {
                Problem =
                    $"The fit was still improving at {best.Seconds:F2} s, which is as far as this "
                    + "looks. A delay that long is more than a sensor and a pipe, so this is "
                    + "probably something else moving with time rather than a transport delay.",
            };

        return result;
    }

    private static DelaySearchResult Nothing(string problem) => new()
    {
        BestSamples = 0,
        BestSeconds = 0,
        Curve = [],
        ImprovementPercent = 0,
        SamplesScored = 0,
        LowSeconds = 0,
        HighSeconds = 0,
        Problem = problem,
    };

    /// <summary>Index of the nearest breakpoint, as the analysis assigns cells.</summary>
    private static int Nearest(double[] breakpoints, double value)
    {
        int best = 0;
        double closest = Math.Abs(value - breakpoints[0]);

        for (int i = 1; i < breakpoints.Length; i++)
        {
            double distance = Math.Abs(value - breakpoints[i]);
            if (distance >= closest) continue;

            closest = distance;
            best = i;
        }

        return best;
    }
}
