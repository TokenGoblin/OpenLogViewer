namespace OpenLogViewer.Core;

/// <summary>
/// Every sample of a log placed at its own X and Y, coloured by a third
/// channel — the same three channels the heat table bins, drawn without the
/// binning.
///
/// The table and this answer different questions about the same data. A table
/// says what a region of the map averaged, which is what you edit a tuning
/// table against. It cannot say whether the twelve samples behind a cell agreed
/// with each other, and a cell reading dead on target is the same colour whether
/// it was measured twelve times at target or six times rich and six times lean.
/// That distinction is the whole of what a scatter is for: structure and spread
/// inside what a table has already averaged away.
///
/// <para>
/// <b>Overplotting is the thing that makes a naive scatter lie.</b> A drive is
/// tens or hundreds of thousands of samples, and an engine spends most of a
/// drive in a small part of the map. Drawn one mark per sample they land on top
/// of one another thousands deep, and the colour that survives is whichever
/// sample was drawn last — an accident of the order the log happens to be in,
/// presented with all the authority of a measurement. Alpha blending only moves
/// the problem: it makes dense regions saturate to a single colour that is not
/// any channel's value.
/// </para>
/// <para>
/// So the points are aggregated onto the display's own grid before anything is
/// drawn — see <see cref="Bin"/>. Every mark is then the mean of what actually
/// landed under it, computed rather than raced for, and it carries the count and
/// the spread behind it so a mark averaging disagreement can say so. At a few
/// pixels per block this is finer than any tuning table by two orders of
/// magnitude, so it shows the structure a table destroys while still being
/// bounded by the size of the window rather than the size of the log.
/// </para>
/// </summary>
public sealed class ScatterPlot
{
    private ScatterPlot(int capacity)
    {
        Xs = new double[capacity];
        Ys = new double[capacity];
        Zs = new double[capacity];
        Samples = new int[capacity];
    }

    /// <summary>X of each surviving point. Valid up to <see cref="Count"/>.</summary>
    public double[] Xs { get; private set; }

    public double[] Ys { get; private set; }

    /// <summary>
    /// Z of each point — already the deviation from <see cref="ZCompare"/> where
    /// one is set, taken per sample as the table takes it.
    /// </summary>
    public double[] Zs { get; private set; }

    /// <summary>Index back into the log for each point, so a mark can be traced.</summary>
    public int[] Samples { get; private set; }

    public int Count { get; private set; }

    /// <summary>
    /// Samples in range that were dropped because one of the three channels had
    /// no reading there. Reported rather than absorbed: a scatter that quietly
    /// drew a third of the log would look like a sparser drive than it was.
    /// </summary>
    public int Dropped { get; private set; }

    /// <summary>Samples excluded by the filters, for the same reason.</summary>
    public int Filtered { get; private set; }

    public required LogChannel X { get; init; }

    public required LogChannel Y { get; init; }

    public required LogChannel Z { get; init; }

    /// <summary>When set, <see cref="Zs"/> holds Z minus this channel.</summary>
    public LogChannel? ZCompare { get; init; }

    public bool IsDelta => ZCompare is not null;

    public double XMin { get; private set; }

    public double XMax { get; private set; }

    public double YMin { get; private set; }

    public double YMax { get; private set; }

    public double ZMin { get; private set; }

    public double ZMax { get; private set; }

    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Largest deviation either side of zero, for a diverging scale that has to
    /// be symmetric about it — equal errors in opposite directions shaded with
    /// different intensities would read as unequal.
    /// </summary>
    public double MaxDeviation => Math.Max(Math.Abs(ZMin), Math.Abs(ZMax));

    /// <summary>
    /// Collects the samples in a window that have a reading on all three
    /// channels and survive the filters.
    /// </summary>
    /// <param name="firstSample">Start of the window, inclusive.</param>
    /// <param name="lastSample">End of the window, inclusive.</param>
    /// <param name="mask">Filters, or null to take every sample.</param>
    /// <param name="zCompare">Subtracted from Z per sample, where the user has
    /// asked for a deviation rather than a reading.</param>
    public static ScatterPlot Build(
        LogChannel x, LogChannel y, LogChannel z,
        int firstSample, int lastSample,
        SampleMask? mask = null,
        LogChannel? zCompare = null)
    {
        int length = Math.Min(x.Length, Math.Min(y.Length, z.Length));
        int from = Math.Max(0, Math.Min(firstSample, lastSample));
        int to = Math.Min(length - 1, Math.Max(firstSample, lastSample));

        var plot = new ScatterPlot(Math.Max(0, to - from + 1))
        {
            X = x,
            Y = y,
            Z = z,
            ZCompare = zCompare,
        };

        return from > to ? plot : plot.Collect(x, y, z, from, to, mask);
    }

    private ScatterPlot Collect(
        LogChannel x, LogChannel y, LogChannel z, int from, int to, SampleMask? mask)
    {
        double xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        double zMin = double.PositiveInfinity, zMax = double.NegativeInfinity;
        int n = 0;

        for (int i = from; i <= to; i++)
        {
            if (mask is not null && !mask[i])
            {
                Filtered++;
                continue;
            }

            double xv = x.At(i), yv = y.At(i), zv = z.At(i);
            if (double.IsNaN(xv) || double.IsNaN(yv) || double.IsNaN(zv))
            {
                Dropped++;
                continue;
            }

            if (ZCompare is { } compare)
            {
                double target = compare.At(i);
                if (double.IsNaN(target))
                {
                    Dropped++;
                    continue;
                }

                zv -= target;
            }

            Xs[n] = xv;
            Ys[n] = yv;
            Zs[n] = zv;
            Samples[n] = i;
            n++;

            if (xv < xMin) xMin = xv;
            if (xv > xMax) xMax = xv;
            if (yv < yMin) yMin = yv;
            if (yv > yMax) yMax = yv;
            if (zv < zMin) zMin = zv;
            if (zv > zMax) zMax = zv;
        }

        Count = n;

        if (n == 0) return this;

        XMin = xMin;
        XMax = xMax;
        YMin = yMin;
        YMax = yMax;
        ZMin = zMin;
        ZMax = zMax;
        return this;
    }

    /// <summary>
    /// Aggregates the points onto a <paramref name="columns"/> × <paramref
    /// name="rows"/> grid spanning the data, which is how the view turns them
    /// into marks it can draw. See the note on overplotting above for why this
    /// is not optional.
    ///
    /// The grid spans the observed range exactly, so the extreme samples land in
    /// the end blocks rather than half outside them.
    /// </summary>
    public ScatterBins Bin(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        var bins = new ScatterBins(columns, rows, XMin, XMax, YMin, YMax);
        if (Count == 0) return bins;

        for (int i = 0; i < Count; i++)
            bins.Add(BlockOf(Xs[i], XMin, XMax, columns), BlockOf(Ys[i], YMin, YMax, rows), Zs[i]);

        return bins.Finish();
    }

    /// <summary>
    /// Every sample that landed in one block, in log order — what a click on a
    /// mark has to trace back to.
    ///
    /// Recomputed rather than stored: a grid the size of a window holds tens of
    /// thousands of blocks, nearly all of them never clicked, and keeping a list
    /// per block would cost more memory than the log does.
    /// </summary>
    public IReadOnlyList<int> SamplesIn(ScatterBins bins, int column, int row)
    {
        var found = new List<int>();

        for (int i = 0; i < Count; i++)
        {
            if (BlockOf(Xs[i], XMin, XMax, bins.Columns) != column) continue;
            if (BlockOf(Ys[i], YMin, YMax, bins.Rows) != row) continue;
            found.Add(Samples[i]);
        }

        return found;
    }

    /// <summary>
    /// Which block a value falls in. A channel that never moved has no range to
    /// divide, so everything goes to the middle rather than to an edge, which
    /// would draw a flat channel as a line pinned against one side.
    /// </summary>
    internal static int BlockOf(double value, double min, double max, int count)
    {
        if (max <= min) return count / 2;

        int block = (int)((value - min) / (max - min) * count);
        return Math.Clamp(block, 0, count - 1);
    }

    /// <summary>
    /// Contiguous runs of samples, so a click on a mark is traced back the way a
    /// table cell is: an engine passes through the same point on the map many
    /// times in a drive, and the span from the first sample to the last would
    /// cover most of the recording.
    /// </summary>
    public static IReadOnlyList<(int First, int Last)> VisitsAmong(
        IReadOnlyList<int> samples, int gapTolerance = 2)
    {
        var visits = new List<(int, int)>();
        if (samples.Count == 0) return visits;

        int start = samples[0], previous = samples[0];

        for (int i = 1; i < samples.Count; i++)
        {
            // A sample or two of noise should not split one visit in half.
            if (samples[i] - previous <= gapTolerance + 1)
            {
                previous = samples[i];
                continue;
            }

            visits.Add((start, previous));
            start = previous = samples[i];
        }

        visits.Add((start, previous));
        return visits;
    }
}

/// <summary>
/// The points of a <see cref="ScatterPlot"/> reduced to one mark per block of
/// the display grid: what is drawn, and what a hover reports.
/// </summary>
public sealed class ScatterBins
{
    private readonly double[] _sums;

    internal ScatterBins(int columns, int rows, double xMin, double xMax, double yMin, double yMax)
    {
        Columns = columns;
        Rows = rows;
        XMin = xMin;
        XMax = xMax;
        YMin = yMin;
        YMax = yMax;

        _sums = new double[columns * rows];
        Counts = new int[columns * rows];
        Means = new double[columns * rows];
        Lowest = new double[columns * rows];
        Highest = new double[columns * rows];
    }

    public int Columns { get; }

    public int Rows { get; }

    public double XMin { get; }

    public double XMax { get; }

    public double YMin { get; }

    public double YMax { get; }

    /// <summary>Samples in each block, indexed <c>row * Columns + column</c>.</summary>
    public int[] Counts { get; }

    /// <summary>Mean Z of each block. Meaningless where the count is zero.</summary>
    public double[] Means { get; }

    /// <summary>Lowest Z in each block.</summary>
    public double[] Lowest { get; }

    /// <summary>Highest Z in each block.</summary>
    public double[] Highest { get; }

    /// <summary>Blocks that got at least one sample — the marks to draw.</summary>
    public int Occupied { get; private set; }

    /// <summary>Most samples in any one block, which is what density is shaded against.</summary>
    public int Busiest { get; private set; }

    /// <summary>Full range of the block means — the extremes actually measured.</summary>
    public double MeanMin { get; private set; }

    public double MeanMax { get; private set; }

    /// <summary>
    /// The range colour is scaled over, which is deliberately not the full one.
    ///
    /// A drive spends nearly all of itself in a narrow band — an engine in
    /// closed loop holds AFR within a point or so of target — and then touches
    /// both extremes for a moment during a transient. Scaled over the full
    /// range, those few blocks own the whole ramp and every other mark on the
    /// plot lands within a shade or two of the same colour: the picture is of
    /// two accelerator pumps, and the drive that surrounds them is flat.
    ///
    /// So the ends are trimmed to the <see cref="Trim"/> percentiles of the
    /// occupied blocks and anything past them saturates. Nothing is hidden by
    /// this — a clipped block is still drawn, still the most extreme colour on
    /// the plot, and still reports its own value on hover — but blocks past the
    /// bound stop being distinguishable from one another, which is a real cost
    /// and the reason the legend says when the bound is a clip rather than a
    /// maximum.
    ///
    /// Over blocks rather than over samples, because it is blocks that are being
    /// coloured. Weighting by sample count would let the seconds spent at idle
    /// decide the scale that the whole map is drawn on.
    /// </summary>
    public double ColorLow { get; private set; }

    public double ColorHigh { get; private set; }

    /// <summary>
    /// True when the scale stops meaningfully short of the values measured.
    ///
    /// Meaningfully, because the trim almost always moves a bound by a little
    /// and a bound that moved by a hundredth of the range is not a clip anyone
    /// needs telling about. Announcing one would put a ≥ in front of a number
    /// that is, to every practical purpose, the largest value on the plot —
    /// which is the opposite of what the mark is for.
    /// </summary>
    public bool ClipsLow => ColorLow - MeanMin > Material;

    public bool ClipsHigh => MeanMax - ColorHigh > Material;

    /// <summary>
    /// How far a bound must move before the move is worth reporting.
    ///
    /// Comfortably above what the trim costs an evenly spread channel, which is
    /// about <see cref="Trim"/> of its range — that is the trim working as
    /// intended on data with no outliers in it, and reporting it would put a ≥
    /// on almost every plot. What this is meant to catch is the other case,
    /// where a handful of blocks sat far out and the bound moved by much more
    /// than the fraction of blocks removed.
    /// </summary>
    private double Material => (MeanMax - MeanMin) * 0.05;

    /// <summary>
    /// How much of each end is allowed to saturate. Two per cent: enough to
    /// discount the handful of blocks a transient leaves behind, small enough
    /// that a genuinely wide spread still scales over its own width.
    /// </summary>
    public const double Trim = 0.02;

    /// <summary>Symmetric limit for a diverging scale, on the same trimmed basis.</summary>
    public double MeanExtent { get; private set; }

    public int Index(int column, int row) => (row * Columns) + column;

    internal void Add(int column, int row, double z)
    {
        int i = Index(column, row);

        if (Counts[i] == 0)
        {
            Lowest[i] = z;
            Highest[i] = z;
        }
        else
        {
            if (z < Lowest[i]) Lowest[i] = z;
            if (z > Highest[i]) Highest[i] = z;
        }

        _sums[i] += z;
        Counts[i]++;
    }

    internal ScatterBins Finish()
    {
        double low = double.PositiveInfinity, high = double.NegativeInfinity;

        for (int i = 0; i < Counts.Length; i++)
        {
            if (Counts[i] == 0) continue;

            Means[i] = _sums[i] / Counts[i];
            Occupied++;

            if (Counts[i] > Busiest) Busiest = Counts[i];
            if (Means[i] < low) low = Means[i];
            if (Means[i] > high) high = Means[i];
        }

        if (Occupied == 0) return this;

        MeanMin = low;
        MeanMax = high;

        ColorLow = MeanMin;
        ColorHigh = MeanMax;

        // Trimmed only where the trim would actually discount a whole block.
        // Below that it has no outlier to remove and merely interpolates between
        // the two extreme values, narrowing the scale to no purpose — on two
        // blocks a two per cent trim moves both ends inward by two per cent of
        // the gap between them, which is a clip that discards nothing.
        if (Occupied * Trim < 1) return this;

        double[] sorted = new double[Occupied];
        for (int i = 0, n = 0; i < Counts.Length; i++)
            if (Counts[i] > 0) sorted[n++] = Means[i];

        Array.Sort(sorted);

        double trimmedLow = Percentile(sorted, Trim);
        double trimmedHigh = Percentile(sorted, 1 - Trim);

        // A trim that closes the range entirely has nothing left to scale over —
        // the middle of this data is one value. The full range is then the only
        // scale there is, and the bulk correctly renders as all one colour
        // because that is what it is.
        if (trimmedHigh <= trimmedLow) return this;

        ColorLow = trimmedLow;
        ColorHigh = trimmedHigh;

        // A diverging scale is symmetric about zero, so it takes one bound: the
        // larger deviation the trim leaves standing, either side of it.
        MeanExtent = Math.Max(Math.Abs(ColorLow), Math.Abs(ColorHigh));
        return this;
    }

    /// <summary>Linear-interpolated percentile of an ascending array.</summary>
    private static double Percentile(double[] ascending, double fraction)
    {
        if (ascending.Length == 1) return ascending[0];

        double position = fraction * (ascending.Length - 1);
        int index = (int)position;
        if (index >= ascending.Length - 1) return ascending[^1];

        double f = position - index;
        return ascending[index] + ((ascending[index + 1] - ascending[index]) * f);
    }

    /// <summary>
    /// How far apart the readings inside one block were. Zero for a block one
    /// sample deep.
    ///
    /// Worth reporting because it is the one thing a mean hides: a block whose
    /// samples ran from rich to lean shades identically to one that sat on
    /// target throughout, and only this tells them apart.
    /// </summary>
    public double SpreadIn(int column, int row)
    {
        int i = Index(column, row);
        return Counts[i] == 0 ? 0 : Highest[i] - Lowest[i];
    }
}
