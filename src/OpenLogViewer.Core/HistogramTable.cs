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

    /// <summary>
    /// When set, cells hold <see cref="Z"/> minus this channel rather than Z
    /// itself — "how far off target am I", which is the question being asked of
    /// a tuning table, rather than "what did it read".
    /// </summary>
    public LogChannel? ZCompare { get; init; }

    public bool IsDelta => ZCompare is not null;

    /// <summary>
    /// Whether cells hold a signed deviation, which is what makes a diverging
    /// scale the right one. Counting samples is a magnitude even when a
    /// comparison channel is set, and shading counts as if they were deviations
    /// from a target would put "no samples" at the neutral midpoint.
    /// </summary>
    public bool ShowsDeviation => IsDelta && Statistic != HistogramStatistic.Count;

    /// <summary>
    /// Largest deviation either side of zero. A delta scale has to be symmetric
    /// about zero, or equal errors in opposite directions would be shaded with
    /// different intensities and read as unequal.
    /// </summary>
    public double MaxDeviation => Math.Max(Math.Abs(MinValue), Math.Abs(MaxValue));

    /// <summary>Smallest aggregated value across populated cells.</summary>
    public double MinValue { get; private set; }

    /// <summary>Largest aggregated value across populated cells.</summary>
    public double MaxValue { get; private set; }

    public int MaxCount { get; private set; }

    /// <summary>Samples that landed in a cell; excludes rows with a missing reading.</summary>
    public int SampleCount { get; private set; }

    public int PopulatedCells { get; private set; }

    public bool IsEmpty => PopulatedCells == 0;

    /// <summary>Sample window the table was built over, for tracing cells back.</summary>
    public int FirstSample { get; private set; }

    public int LastSample { get; private set; }

    private SampleMask? _mask;

    /// <summary>
    /// Sample indices that landed in one cell, in order.
    ///
    /// Recomputed rather than stored: a per-sample cell index would cost memory
    /// on every table for a lookup used only when a cell is clicked.
    /// </summary>
    public IReadOnlyList<int> SamplesIn(int column, int row)
    {
        var hits = new List<int>();
        if ((uint)column >= (uint)Columns || (uint)row >= (uint)Rows) return hits;

        for (int i = FirstSample; i <= LastSample; i++)
        {
            if (_mask is not null && !_mask[i]) continue;

            double xv = X.At(i), yv = Y.At(i), zv = Z.At(i);
            if (double.IsNaN(xv) || double.IsNaN(yv) || double.IsNaN(zv)) continue;
            if (ZCompare is { } compare && double.IsNaN(compare.At(i))) continue;

            if (Nearest(ColumnCenters, xv) == column && Nearest(RowCenters, yv) == row)
                hits.Add(i);
        }

        return hits;
    }

    /// <summary>
    /// The cell's samples grouped into visits. An engine passes through the same
    /// RPM and load many times in a drive, so a cell is almost never one stretch
    /// of the log — treating it as one would span nearly the whole recording.
    /// </summary>
    public IReadOnlyList<(int First, int Last)> VisitsTo(int column, int row, int gapTolerance = 2)
    {
        var visits = new List<(int, int)>();
        int start = -1, previous = -1;

        foreach (int i in SamplesIn(column, row))
        {
            if (start < 0) { start = previous = i; continue; }

            // A sample or two of noise should not split one visit in half.
            if (i - previous <= gapTolerance + 1) { previous = i; continue; }

            visits.Add((start, previous));
            start = previous = i;
        }

        if (start >= 0) visits.Add((start, previous));
        return visits;
    }

    /// <summary>The longest visit to a cell, which is the one worth looking at first.</summary>
    public (int First, int Last)? LongestVisitTo(int column, int row)
    {
        IReadOnlyList<(int First, int Last)> visits = VisitsTo(column, row);
        if (visits.Count == 0) return null;

        (int First, int Last) best = visits[0];
        foreach ((int first, int last) in visits)
            if (last - first > best.Last - best.First)
                best = (first, last);

        return best;
    }

    /// <summary>The cell a sample falls in, or null when it is excluded.</summary>
    public (int Column, int Row)? CellOf(int sample)
    {
        if (sample < FirstSample || sample > LastSample) return null;
        if (_mask is not null && !_mask[sample]) return null;

        double xv = X.At(sample), yv = Y.At(sample);
        if (double.IsNaN(xv) || double.IsNaN(yv)) return null;

        return (Nearest(ColumnCenters, xv), Nearest(RowCenters, yv));
    }

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
        SampleMask? mask = null,
        LogChannel? zCompare = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        int length = Math.Min(x.Length, Math.Min(y.Length, z.Length));
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(length - 1, Math.Max(firstSample, lastSample));

        var table = new HistogramTable(columns, rows)
        {
            X = x,
            Y = y,
            Z = z,
            Statistic = statistic,
            ZCompare = zCompare,
        };

        if (from > to) return table.Finish();

        // Axes are scaled to the samples that survive filtering, so excluding
        // warmup or idle also tightens the axis onto the data that remains.
        (double xMin, double xStep) = Axis(x, from, to, columns, mask);
        (double yMin, double yStep) = Axis(y, from, to, rows, mask);

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
        SampleMask? mask = null,
        LogChannel? zCompare = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columnBreakpoints.Length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowBreakpoints.Length, 1);

        int length = Math.Min(x.Length, Math.Min(y.Length, z.Length));
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(length - 1, Math.Max(firstSample, lastSample));

        var table = new HistogramTable(columnBreakpoints.Length, rowBreakpoints.Length)
        {
            X = x,
            Y = y,
            Z = z,
            Statistic = statistic,
            FromTune = true,
            ZCompare = zCompare,
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

        FirstSample = from;
        LastSample = to;
        _mask = mask;

        var sums = new double[columns, rows];
        var mins = new double[columns, rows];
        var maxes = new double[columns, rows];

        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i]) continue;

            double xv = x.At(i), yv = y.At(i), zv = z.At(i);
            if (double.IsNaN(xv) || double.IsNaN(yv) || double.IsNaN(zv)) continue;

            if (ZCompare is { } compare)
            {
                // The deviation is taken per sample and then aggregated, so the
                // mean of the error is reported rather than the error of the means.
                double target = compare.At(i);
                if (double.IsNaN(target)) continue;
                zv -= target;
            }

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
        LogChannel channel, int from, int to, int bins, SampleMask? mask)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i]) continue;

            double v = channel.At(i);
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
    public string Format(int column, int row) =>
        Values[column, row] is { } value ? Format(value) : "";

    /// <summary>
    /// Formats a value on this table's scale. Deltas carry an explicit sign, so
    /// the direction of an error is readable without consulting the colour.
    /// </summary>
    public string Format(double value)
    {
        if (Statistic == HistogramStatistic.Count) return ((int)value).ToString("N0");

        string text = Z.Format(value);
        return IsDelta && value > 0 ? "+" + text : text;
    }
}

