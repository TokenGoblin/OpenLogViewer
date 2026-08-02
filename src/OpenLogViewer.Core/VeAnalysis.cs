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

    /// <summary>Cells the analysis is prepared to change.</summary>
    public required int CellsSuggested { get; init; }

    /// <summary>Cells with samples but too few to trust.</summary>
    public required int CellsThin { get; init; }

    public required int SamplesUsed { get; init; }

    /// <summary>Largest single change suggested, as a percentage.</summary>
    public required double LargestChangePercent { get; init; }

    public bool IsEmpty => CellsSuggested == 0;
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

        int used = 0;
        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i]) continue;

            double x = rpm.At(i), y = load.At(i);
            double measured = afr.At(i), wanted = target.At(i);

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

        var suggested = new double?[columns, rows];
        var change = new double?[columns, rows];
        int changed = 0, thin = 0;
        double largest = 0;

        for (int c = 0; c < columns; c++)
        for (int r = 0; r < rows; r++)
        {
            if (counts[c, r] == 0) continue;

            if (counts[c, r] < settings.MinimumSamples) { thin++; continue; }

            double current = table.Values[c, r];
            if (current <= 0) continue;

            double ratio = sums[c, r] / counts[c, r];
            double proposed = current * (1 + (ratio - 1) * settings.Authority);

            // Clamped rather than dropped: a cell that wants more than one pass
            // allows should still move, just not all the way at once.
            double limit = current * settings.MaxChangePercent / 100;
            proposed = Math.Clamp(proposed, current - limit, current + limit);

            double percent = (proposed - current) / current * 100;

            suggested[c, r] = proposed;
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
            CellsSuggested = changed,
            CellsThin = thin,
            SamplesUsed = used,
            LargestChangePercent = largest,
        };
    }

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
