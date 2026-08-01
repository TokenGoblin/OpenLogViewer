namespace OpenLogViewer.Core;

/// <summary>How the samples falling in one cell are reduced to a single number.</summary>
public enum HistogramStatistic
{
    Mean,
    Min,
    Max,
    Count,
}

/// <summary>
/// A two-dimensional binned summary of a log: samples are bucketed by two
/// channels (say RPM and MAP) and a third is aggregated within each bucket.
///
/// This is how a tuner turns a drive into a table — the same shape as the VE or
/// AFR table in the ECU, so the log can be read against the tune it came from.
/// </summary>
public sealed class HistogramTable
{
    private HistogramTable(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        ColumnCenters = new double[columns];
        RowCenters = new double[rows];
        Values = new double?[columns, rows];
        Counts = new int[columns, rows];
    }

    public int Columns { get; }

    public int Rows { get; }

    /// <summary>Bin centres along X, ascending.</summary>
    public double[] ColumnCenters { get; }

    /// <summary>Bin centres along Y, ascending. Row 0 is the lowest value.</summary>
    public double[] RowCenters { get; }

    /// <summary>Aggregated Z per cell, indexed [column, row]. Null where no samples landed.</summary>
    public double?[,] Values { get; }

    /// <summary>Sample count per cell, indexed [column, row].</summary>
    public int[,] Counts { get; }

    public required LogChannel X { get; init; }

    public required LogChannel Y { get; init; }

    public required LogChannel Z { get; init; }

    public required HistogramStatistic Statistic { get; init; }

    /// <summary>True when the axes came from the tune rather than the data range.</summary>
    public bool FromTune { get; init; }

    /// <summary>Smallest aggregated value across populated cells.</summary>
    public double MinValue { get; private set; }

    /// <summary>Largest aggregated value across populated cells.</summary>
    public double MaxValue { get; private set; }

    public int MaxCount { get; private set; }

    /// <summary>Samples that landed in a cell; excludes rows with a missing reading.</summary>
    public int SampleCount { get; private set; }

    public int PopulatedCells { get; private set; }

    public bool IsEmpty => PopulatedCells == 0;

    /// <summary>
    /// Bins samples <paramref name="firstSample"/>..<paramref name="lastSample"/>
    /// inclusive. Axis ranges come from that window rather than the whole log, so
    /// zooming into one pull re-scales the table around it.
    /// </summary>
    public static HistogramTable Build(
        LogChannel x, LogChannel y, LogChannel z,
        int columns, int rows,
        int firstSample, int lastSample,
        HistogramStatistic statistic,
        SampleMask? mask = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        int length = Math.Min(x.Values.Length, Math.Min(y.Values.Length, z.Values.Length));
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(length - 1, Math.Max(firstSample, lastSample));

        var table = new HistogramTable(columns, rows)
        {
            X = x,
            Y = y,
            Z = z,
            Statistic = statistic,
        };

        if (from > to) return table.Finish();

        // Axes are scaled to the samples that survive filtering, so excluding
        // warmup or idle also tightens the axis onto the data that remains.
        (double xMin, double xStep) = Axis(x.Values, from, to, columns, mask);
        (double yMin, double yStep) = Axis(y.Values, from, to, rows, mask);

        for (int i = 0; i < columns; i++) table.ColumnCenters[i] = xMin + (i + 0.5) * xStep;
        for (int i = 0; i < rows; i++) table.RowCenters[i] = yMin + (i + 0.5) * yStep;

        return table.Accumulate(x, y, z, from, to, statistic, mask);
    }

    /// <summary>
    /// Bins onto breakpoints supplied by the caller — normally the axes of the
    /// table in the ECU, so each cell here corresponds to a cell being tuned.
    /// Samples are assigned to the nearest breakpoint, which is how a value
    /// between two rows is attributed in a tuning table.
    /// </summary>
    public static HistogramTable Build(
        LogChannel x, LogChannel y, LogChannel z,
        double[] columnBreakpoints, double[] rowBreakpoints,
        int firstSample, int lastSample,
        HistogramStatistic statistic,
        SampleMask? mask = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columnBreakpoints.Length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowBreakpoints.Length, 1);

        int length = Math.Min(x.Values.Length, Math.Min(y.Values.Length, z.Values.Length));
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(length - 1, Math.Max(firstSample, lastSample));

        var table = new HistogramTable(columnBreakpoints.Length, rowBreakpoints.Length)
        {
            X = x,
            Y = y,
            Z = z,
            Statistic = statistic,
            FromTune = true,
        };

        columnBreakpoints.CopyTo(table.ColumnCenters, 0);
        rowBreakpoints.CopyTo(table.RowCenters, 0);

        return from > to ? table.Finish() : table.Accumulate(x, y, z, from, to, statistic, mask);
    }

    private HistogramTable Accumulate(
        LogChannel x, LogChannel y, LogChannel z,
        int from, int to,
        HistogramStatistic statistic,
        SampleMask? mask)
    {
        int columns = Columns, rows = Rows;
        HistogramTable table = this;

        var sums = new double[columns, rows];
        var mins = new double[columns, rows];
        var maxes = new double[columns, rows];

        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i]) continue;

            double xv = x.Values[i], yv = y.Values[i], zv = z.Values[i];
            if (double.IsNaN(xv) || double.IsNaN(yv) || double.IsNaN(zv)) continue;

            int column = Nearest(table.ColumnCenters, xv);
            int row = Nearest(table.RowCenters, yv);

            if (table.Counts[column, row] == 0)
            {
                mins[column, row] = zv;
                maxes[column, row] = zv;
            }
            else
            {
                if (zv < mins[column, row]) mins[column, row] = zv;
                if (zv > maxes[column, row]) maxes[column, row] = zv;
            }

            sums[column, row] += zv;
            table.Counts[column, row]++;
            table.SampleCount++;
        }

        for (int c = 0; c < columns; c++)
        for (int r = 0; r < rows; r++)
        {
            int count = table.Counts[c, r];
            if (count == 0) continue;

            table.Values[c, r] = statistic switch
            {
                HistogramStatistic.Mean => sums[c, r] / count,
                HistogramStatistic.Min => mins[c, r],
                HistogramStatistic.Max => maxes[c, r],
                _ => count,
            };
        }

        return table.Finish();
    }

    private HistogramTable Finish()
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        int populated = 0, maxCount = 0;

        for (int c = 0; c < Columns; c++)
        for (int r = 0; r < Rows; r++)
        {
            if (Counts[c, r] > maxCount) maxCount = Counts[c, r];
            if (Values[c, r] is not { } v) continue;

            populated++;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        PopulatedCells = populated;
        MaxCount = maxCount;
        MinValue = populated == 0 ? 0 : min;
        MaxValue = populated == 0 ? 0 : max;
        return this;
    }

    /// <summary>
    /// Axis origin and bin width over the sample window. A channel that never
    /// changes would give a zero-width axis, so it is widened to keep every
    /// sample inside the table.
    /// </summary>
    private static (double Min, double Step) Axis(
        double[] values, int from, int to, int bins, SampleMask? mask)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i]) continue;

            double v = values[i];
            if (double.IsNaN(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (double.IsInfinity(min)) { min = 0; max = 1; }
        if (max - min <= 0) { min -= 0.5; max += 0.5; }

        return (min, (max - min) / bins);
    }

    /// <summary>
    /// Index of the closest breakpoint. Ties and out-of-range values fall to the
    /// nearest end, so every sample lands somewhere rather than being dropped.
    /// </summary>
    private static int Nearest(double[] centers, double value)
    {
        int best = 0;
        double bestDistance = Math.Abs(value - centers[0]);

        for (int i = 1; i < centers.Length; i++)
        {
            double distance = Math.Abs(value - centers[i]);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = i;
        }

        return best;
    }

    /// <summary>Formats a cell for display, using the Z channel's own precision.</summary>
    public string Format(int column, int row)
    {
        if (Values[column, row] is not { } value) return "";
        return Statistic == HistogramStatistic.Count
            ? ((int)value).ToString("N0")
            : Z.Format(value);
    }
}
