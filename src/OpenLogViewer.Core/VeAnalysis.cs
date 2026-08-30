namespace OpenLogViewer.Core;

public sealed record VeAnalysisSettings
{
    /// <summary>
    /// Samples a cell needs before its suggestion is trusted. A cell crossed
    /// twice on the way to somewhere else says more about the transient than
    /// about the fuelling there.
    /// </summary>
    public int MinimumSamples { get; init; } = 12;

    /// <summary>
    /// Largest change suggested for one pass, as a percentage of the current
    /// value. Fuelling is not the only thing that moves AFR — a cell read during
    /// an accel-enrichment event or a gear change can imply a correction far
    /// larger than the table is actually wrong by, and applying it whole turns
    /// one bad reading into a hole in the table.
    /// </summary>
    public double MaxChangePercent { get; init; } = 15;

    /// <summary>
    /// Fraction of the indicated correction to apply. Converging in steps is how
    /// this is done in practice: the measurement lags the change, so taking the
    /// whole of it tends to overshoot and oscillate.
    /// </summary>
    public double Authority { get; init; } = 1.0;

    /// <summary>
    /// How many samples later the wideband reads the mixture that was metered
    /// now.
    ///
    /// <para>
    /// The reading at a given moment is not evidence about that moment. Fuel
    /// metered on this revolution is burned, pushed out of the port, carried
    /// down the pipe to wherever the sensor is, and only then measured — and the
    /// sensor itself takes time to respond. Compared without accounting for it,
    /// every reading is credited to whatever the engine was doing a few hundred
    /// milliseconds too late.
    /// </para>
    /// <para>
    /// At steady state that costs nothing, because the engine was doing the same
    /// thing then as now. It bites during exactly the transients the clamps
    /// already exist to defend against: through a fast ramp the mixture from
    /// 3,000 rpm is credited to the 4,000 rpm cell, so a correction is not
    /// merely wrong in size but attributed to the wrong cell, and it smears
    /// across a region of the table rather than landing anywhere.
    /// </para>
    /// <para>
    /// In samples rather than seconds because this is where the arithmetic
    /// happens; callers convert from a time using the log's own sample interval.
    /// Zero by default, which is the old behaviour — the right figure depends on
    /// where the sensor is fitted and how long its pipe is, and nothing here can
    /// know that. It is offered rather than guessed at.
    /// </para>
    /// </summary>
    public int MeasurementDelaySamples { get; init; }

    /// <summary>
    /// How much data a cell needs before its correction is taken at face value.
    ///
    /// <see cref="MinimumSamples"/> is a floor: below it a cell is not touched at
    /// all. Above it the old behaviour was a cliff — a cell with twelve samples
    /// and a cell with two hundred both got the whole correction, though one is
    /// a measurement and the other is a glance. The correction is scaled by
    /// <c>n / (n + this)</c> instead, which is nought at no data, half at this
    /// many samples, and approaches the whole of it as the evidence mounts.
    /// A cell backed by little data therefore stays near the number it already
    /// holds, which carries the weight of however it was arrived at.
    ///
    /// Set to zero to take every correction whole, which is what this did before.
    /// </summary>
    public int ConfidenceSamples { get; init; } = 12;
}

/// <summary>
/// What one pass of the analysis concluded, cell by cell.
/// </summary>
public sealed record VeAnalysisResult
{
    public required TuneTable Table { get; init; }

    /// <summary>Suggested value per cell, or null where there was not enough data.</summary>
    public required double?[,] Suggested { get; init; }

    /// <summary>Change from the current table, as a percentage. Null where unchanged.</summary>
    public required double?[,] ChangePercent { get; init; }

    /// <summary>Samples behind each cell.</summary>
    public required int[,] Counts { get; init; }

    /// <summary>
    /// How far each cell's own measurement was trusted, 0 to 1 — the factor its
    /// correction was scaled by for want of data. One where the evidence is
    /// ample, and reported rather than hidden so that a small suggested change
    /// can be told from a small measured error.
    /// </summary>
    public required double[,] Weight { get; init; }

    /// <summary>Cells the analysis is prepared to change.</summary>
    public required int CellsSuggested { get; init; }

    /// <summary>Cells with samples but too few to trust.</summary>
    public required int CellsThin { get; init; }

    public required int SamplesUsed { get; init; }

    /// <summary>Largest single change suggested, as a percentage.</summary>
    public required double LargestChangePercent { get; init; }

    public bool IsEmpty => CellsSuggested == 0;

    /// <summary>
    /// Why the answer should not be believed, where that can be said.
    ///
    /// Set when the two channels being compared are not the same quantity —
    /// measured AFR against a lambda target, most often, since a firmware
    /// commonly logs both and nothing about their names says they are on
    /// different scales. The arithmetic is perfectly happy with it and produces
    /// a full table of confident nonsense.
    /// </summary>
    public string? Problem { get; init; }

    public bool HasProblem => Problem is { Length: > 0 };
}

/// <summary>
/// Compares logged AFR against the AFR the tune was asking for, and suggests a
/// new fuel table.
///
/// The reasoning is one line: the engine took in a known amount of air, the ECU
/// metered fuel for it using the VE number in the cell, and the wideband says
/// what the mixture actually came out as. If it came out richer than the target,
/// the ECU thought there was more air than there was, so the VE number is too
/// high — scale it by measured over target.
///
/// What makes this trustworthy is not the arithmetic but what it refuses to do.
/// A cell backed by a handful of samples, or one implying an implausible jump,
/// is left alone and said to be left alone.
/// </summary>
public static class VeAnalysis
{
    private static double Total(double[,] values)
    {
        double sum = 0;
        foreach (double value in values) sum += value;

        return sum;
    }

    /// <summary>The mean of a channel over a window, for saying what scale it is on.</summary>
    private static double Average(LogChannel channel, int from, int to)
    {
        double sum = 0;
        int count = 0;

        for (int i = from; i <= to; i++)
        {
            double value = channel.At(i);
            if (double.IsNaN(value)) continue;

            sum += value;
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// A grid taken from the log itself, for when no tune supplies one.
    ///
    /// The ECU's own breakpoints are better and are used whenever they can be
    /// had, because a table binned onto them reads cell-for-cell against the
    /// table being tuned. But requiring them meant this could not run at all on
    /// a log by itself — which is every MaxxECU, whose tune cannot be read, and
    /// every log opened away from the car it came from.
    ///
    /// The cells hold nothing. There is no current fuelling to scale, so the
    /// analysis reports how far out each cell is and leaves suggesting a new
    /// number to a session that knows the old one.
    /// </summary>
    public static TuneTable GridFrom(
        LogChannel x, LogChannel y, int columns, int rows,
        int firstSample, int lastSample, SampleMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        return new TuneTable(
            $"{x.Name} × {y.Name}",
            new TuneAxis(x.Name, x.Units, Breakpoints(x, columns, firstSample, lastSample, mask)),
            new TuneAxis(y.Name, y.Units, Breakpoints(y, rows, firstSample, lastSample, mask)),
            new double[columns, rows],
            "%");
    }

    /// <summary>
    /// Bin centres spread over what the channel actually did in the window,
    /// matching how the heat table lays its axes out — so the two agree about
    /// which cell a sample belongs to.
    /// </summary>
    private static double[] Breakpoints(
        LogChannel channel, int count, int firstSample, int lastSample, SampleMask? mask)
    {
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(channel.Length - 1, Math.Max(firstSample, lastSample));

        double low = double.MaxValue, high = double.MinValue;

        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i]) continue;

            double value = channel.At(i);
            if (double.IsNaN(value)) continue;

            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }

        if (low > high) (low, high) = (0, 1);
        if (high - low < 1e-9) high = low + 1;

        double step = (high - low) / count;

        return [.. Enumerable.Range(0, count).Select(i => low + ((i + 0.5) * step))];
    }

    public static VeAnalysisResult Analyse(
        TuneTable table,
        LogChannel rpm, LogChannel load, LogChannel afr, LogChannel target,
        int firstSample, int lastSample,
        SampleMask? mask = null,
        VeAnalysisSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        settings ??= new VeAnalysisSettings();

        int columns = table.Columns, rows = table.Rows;
        var sums = new double[columns, rows];
        var counts = new int[columns, rows];

        int length = new[] { rpm.Length, load.Length, afr.Length, target.Length }.Min();
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(length - 1, Math.Max(firstSample, lastSample));

        int delay = Math.Max(0, settings.MeasurementDelaySamples);

        int used = 0;
        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i]) continue;

            // The cell, and what the ECU was aiming for, are read at the moment
            // the fuel was metered. The wideband's answer to it arrives later,
            // so that one reading — and only that one — comes from further down
            // the log.
            int measuredAt = i + delay;
            if (measuredAt > to) continue;

            // The delayed sample has to survive the filters too, or a reading
            // from a stretch the user excluded would be credited to a cell.
            if (mask is not null && !mask[measuredAt]) continue;

            double x = rpm.At(i), y = load.At(i);
            double measured = afr.At(measuredAt), wanted = target.At(i);

            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(measured) || double.IsNaN(wanted))
                continue;

            // A zero or negative target is not a target; dividing by it would
            // produce a correction from nothing.
            if (wanted <= 0 || measured <= 0) continue;

            int column = Nearest(table.X.Breakpoints, x);
            int row = Nearest(table.Y.Breakpoints, y);

            sums[column, row] += measured / wanted;
            counts[column, row]++;
            used++;
        }

        // Before any of it is believed: are these two channels even the same
        // quantity? A firmware commonly logs both AFR and lambda, and a target
        // in one against a measurement in the other divides 12.5 by 0.9 and
        // reports every cell as fifteen per cent lean — a full table of
        // confident nonsense, which is worse than an empty one because it looks
        // like an answer.
        //
        // A real tune is out by tens of per cent at worst. Anything beyond a
        // factor of two is not a mistuned engine, it is two different scales.
        if (used > 0)
        {
            double average = Total(sums) / used;

            if (average is < 0.5 or > 2)
                return new VeAnalysisResult
                {
                    Table = table,
                    Suggested = new double?[columns, rows],
                    ChangePercent = new double?[columns, rows],
                    Counts = counts,
                    Weight = new double[columns, rows],
                    CellsSuggested = 0,
                    CellsThin = 0,
                    SamplesUsed = used,
                    LargestChangePercent = 0,
                    Problem =
                        $"\"{afr.Name}\" averages {Average(afr, from, to):G4} and \"{target.Name}\" "
                        + $"averages {Average(target, from, to):G4}, which are not the same kind of "
                        + "number — one is probably lambda and the other AFR. Comparing them would "
                        + "suggest a correction of hundreds of per cent. Pick a measured channel and "
                        + "a target on the same scale.",
                };
        }

        var suggested = new double?[columns, rows];
        var change = new double?[columns, rows];
        var weight = new double[columns, rows];
        int changed = 0, thin = 0;
        double largest = 0;

        for (int c = 0; c < columns; c++)
        for (int r = 0; r < rows; r++)
        {
            if (counts[c, r] == 0) continue;

            if (counts[c, r] < settings.MinimumSamples) { thin++; continue; }

            double ratio = sums[c, r] / counts[c, r];
            double current = table.Values[c, r];

            // How much this cell's evidence is worth. Half at the confidence
            // figure, approaching the whole of it as samples accumulate, so a
            // cell backed by a glance moves less than one backed by a minute of
            // steady running rather than exactly as much.
            double confidence = Confidence(counts[c, r], settings.ConfidenceSamples);
            weight[c, r] = confidence;

            // How far out the fuelling is, which is the whole of the answer and
            // needs nothing from the table. Mixture came out richer than asked
            // for means the ECU thought there was more air than there was, so
            // the number in that cell is too high by this much.
            // Two different numbers, and which one is wanted depends on whether
            // there is a table to change.
            //
            // How far out the fuelling is: mixture richer than asked for means
            // the ECU thought there was more air than there was, so the number
            // in that cell is too high by this much.
            double error = (ratio - 1) * 100;

            // And how much of that to actually move, which is less: some of the
            // authority the caller allows, and less again where the cell rests
            // on little evidence.
            double wanted = error * settings.Authority * confidence;

            double percent = Math.Clamp(
                wanted, -settings.MaxChangePercent, settings.MaxChangePercent);

            // A new value for the cell only where there is an old one to scale.
            // A log on its own — from a controller whose tune cannot be read, or
            // opened with no tune beside it — still says how far out each cell
            // is, and that is the number a tuner acts on.
            if (current > 0)
            {
                double proposed = current * (1 + percent / 100);

                suggested[c, r] = proposed;
                percent = (proposed - current) / current * 100;
            }
            else
            {
                // The error itself, not a share of it. With no table there is
                // nothing to suggest a change to, so what is reported is the
                // measurement — and scaling a measurement by an authority that
                // applies to no table, and by a confidence the caller is handed
                // separately in Weight, would tell a tuner a cell running six
                // per cent lean was three.
                percent = Math.Clamp(
                    error, -settings.MaxChangePercent, settings.MaxChangePercent);
            }

            change[c, r] = percent;
            changed++;
            largest = Math.Max(largest, Math.Abs(percent));
        }

        return new VeAnalysisResult
        {
            Table = table,
            Suggested = suggested,
            ChangePercent = change,
            Counts = counts,
            Weight = weight,
            CellsSuggested = changed,
            CellsThin = thin,
            SamplesUsed = used,
            LargestChangePercent = largest,
        };
    }

    /// <summary>
    /// How far a cell's own measurement is trusted against the number already in
    /// it: <c>n / (n + k)</c>, the usual way of shrinking an estimate towards a
    /// prior in proportion to how much evidence stands behind it.
    ///
    /// A ratio rather than a threshold because the underlying quantity is one.
    /// The mean of n readings has a standard error that falls off smoothly with
    /// n; nothing about a cell changes character as it crosses a particular
    /// count, so nothing here should either.
    ///
    /// <paramref name="k"/> of zero means every correction is taken whole.
    /// </summary>
    internal static double Confidence(int samples, int k) =>
        k <= 0 ? 1 : (double)samples / (samples + k);

    /// <summary>
    /// The suggestion as a table the heat view can draw: cells hold the change in
    /// percent, so the colour says which way and how far, and an untouched cell
    /// is empty rather than zero.
    /// </summary>
    public static HistogramTable AsChangeTable(
        this VeAnalysisResult result, LogChannel rpm, LogChannel load, LogChannel afr,
        LogChannel target, int firstSample, int lastSample, SampleMask? mask = null) =>
        HistogramTable.FromCells(
            rpm, load, afr,
            result.Table.X.Breakpoints, result.Table.Y.Breakpoints,
            result.ChangePercent, result.Counts, firstSample, lastSample,
            // Given the comparison channel so the scale diverges about zero: a
            // cell wanting less fuel and one wanting more must not shade alike.
            $"{result.Table.Name} change, %", 1, target, mask);

    /// <summary>The suggested table itself, for reading the new numbers off.</summary>
    public static HistogramTable AsSuggestedTable(
        this VeAnalysisResult result, LogChannel rpm, LogChannel load, LogChannel afr,
        int firstSample, int lastSample, SampleMask? mask = null) =>
        HistogramTable.FromCells(
            rpm, load, afr,
            result.Table.X.Breakpoints, result.Table.Y.Breakpoints,
            result.Suggested, result.Counts, firstSample, lastSample,
            // No comparison channel: a suggested VE is a magnitude, and a
            // diverging scale would imply a midpoint the number does not have.
            $"suggested {result.Table.Name}", 1, null, mask);

    /// <summary>
    /// Index of the nearest breakpoint. The ECU interpolates between cells, but
    /// for judging which cell is wrong a sample belongs to the one it is closest
    /// to — that is the number the tuner will change.
    /// </summary>
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
