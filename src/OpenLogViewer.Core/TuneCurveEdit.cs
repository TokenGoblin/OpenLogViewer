namespace OpenLogViewer.Core;

/// <summary>One point of a curve: where it sits along the bottom, and how high.</summary>
/// <param name="X">The breakpoint.</param>
/// <param name="Y">The value there.</param>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// A curve out of the ECU, ready to be looked at and changed.
///
/// <para>
/// The other half of tuning, beside the tables, and the half this application
/// has been unable to open. Warmup enrichment, cranking pulsewidth, injector
/// dead time against battery voltage, the AFR target against RPM — an MS3
/// declares 135 of these and a MicroSquirt 37, and on the latter 23 of the 131
/// entries in the settings menu point at one. Until now every one of those
/// opened nothing at all.
/// </para>
/// <para>
/// <b>Both rows are editable, which is what makes a curve not a table.</b> A
/// table's axes are breakpoints you rarely touch and its values are the tuning;
/// a curve is a line you drag, and moving a point sideways is as ordinary as
/// moving it up. So the breakpoints are an editable row here rather than a
/// fixed header, and they are written back like any other array.
/// </para>
/// <para>
/// Nothing here reaches the ECU. It works out what the bytes would become and
/// hands them over as writes, exactly as the table editor does.
/// </para>
/// </summary>
public sealed class TuneCurveEdit
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _originalX;
    private readonly double[] _originalY;

    /// <summary>
    /// Builds one, or nothing when the tune cannot produce this curve.
    ///
    /// Refused rather than half-drawn when the two rows are different lengths:
    /// a curve assembled from mismatched parts is not a near miss, it is a line
    /// whose points are in the wrong places.
    /// </summary>
    public static TuneCurveEdit? For(TuneCurve curve, EcuTune tune)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(tune);

        if (!curve.IsUsable) return null;

        double[]? x = tune.Array(curve.XBins);
        double[]? y = tune.Array(curve.YBins);

        if (x is null || y is null || x.Length == 0 || x.Length != y.Length) return null;

        return new TuneCurveEdit(curve, tune, x, y);
    }

    private TuneCurveEdit(TuneCurve curve, EcuTune tune, double[] x, double[] y)
    {
        Curve = curve;
        _x = x;
        _y = y;
        _originalX = [.. x];
        _originalY = [.. y];

        XConstant = tune.Constant(curve.XBins);
        YConstant = tune.Constant(curve.YBins);
    }

    public TuneCurve Curve { get; }

    /// <summary>The constant holding the breakpoints, for its units and its range.</summary>
    public TuneConstant? XConstant { get; }

    public TuneConstant? YConstant { get; }

    public string Name => Curve.Name;

    /// <summary>What to head it with, falling back to the name where it has no title.</summary>
    public string Title => Curve.Title.Length > 0 ? Curve.Title : Curve.Name;

    public string XLabel => Curve.XLabel.Length > 0 ? Curve.XLabel : Curve.XBins;

    public string YLabel => Curve.YLabel.Length > 0 ? Curve.YLabel : Curve.YBins;

    public string XUnits => XConstant?.Units ?? "";

    public string YUnits => YConstant?.Units ?? "";

    public int Count => _x.Length;

    public int XDigits => XConstant?.Digits ?? 0;

    public int YDigits => YConstant?.Digits ?? 0;

    /// <summary>The points as they now stand.</summary>
    public IReadOnlyList<CurvePoint> Points =>
        [.. _x.Select((v, i) => new CurvePoint(v, _y[i]))];

    public double X(int index) => _x[index];

    public double Y(int index) => _y[index];

    public double OriginalX(int index) => _originalX[index];

    public double OriginalY(int index) => _originalY[index];

    public bool IsChanged(int index) =>
        Math.Abs(_x[index] - _originalX[index]) > 1e-9
        || Math.Abs(_y[index] - _originalY[index]) > 1e-9;

    public bool HasChanges => Enumerable.Range(0, Count).Any(IsChanged);

    public int ChangedCount => Enumerable.Range(0, Count).Count(IsChanged);

    /// <summary>
    /// Moves a point's value, held to what the firmware says it may be.
    ///
    /// Clamped rather than refused, because this is a line being dragged: a
    /// pointer that runs past the top of the plot should stop at the top, not
    /// abandon the drag.
    /// </summary>
    public void SetY(int index, double value)
    {
        if (index < 0 || index >= Count || !double.IsFinite(value)) return;

        _y[index] = Clamp(value, YConstant);
    }

    /// <summary>
    /// Moves a breakpoint, held both to the firmware's range and to its
    /// neighbours.
    ///
    /// <b>Breakpoints must stay in order.</b> The ECU interpolates between
    /// consecutive entries and takes the row as ascending; one that overtakes
    /// its neighbour makes the lookup read backwards over that span, which is
    /// not an error anywhere — it is a curve that does the opposite of what it
    /// shows for one stretch of its range.
    /// </summary>
    public void SetX(int index, double value)
    {
        if (index < 0 || index >= Count || !double.IsFinite(value)) return;

        double low = index > 0 ? _x[index - 1] : double.NegativeInfinity;
        double high = index < Count - 1 ? _x[index + 1] : double.PositiveInfinity;

        // The neighbours are not always the right way round. A row that was
        // never configured, or one a firmware genuinely writes descending, gives
        // a lower bound above the upper — and Math.Clamp throws on that rather
        // than doing nothing, which on a drag is the application going down
        // under the pointer. This class's own doc says such a row is not an
        // error, so it has to be tolerated: where the bounds are inverted there
        // is no room between the neighbours to move into, and the point stays.
        if (low > high) return;

        _x[index] = Math.Clamp(Clamp(value, XConstant), low, high);
    }

    /// <summary>Adds to a point's value.</summary>
    public void AddY(int index, double delta)
    {
        if (index >= 0 && index < Count) SetY(index, _y[index] + delta);
    }

    /// <summary>Scales a point's value by a percentage, 100 meaning no change.</summary>
    public void ScaleY(int index, double percent)
    {
        if (index >= 0 && index < Count && double.IsFinite(percent))
            SetY(index, _y[index] * percent / 100.0);
    }

    /// <summary>
    /// Draws a straight line between two points, moving everything between them
    /// onto it.
    ///
    /// The one bulk operation a curve wants: a warmup line that has been poked
    /// at cell by cell ends up lumpy, and a lump in an enrichment curve is a
    /// stumble at one coolant temperature.
    /// </summary>
    public bool Interpolate(int from, int to)
    {
        if (from > to) (from, to) = (to, from);
        if (from < 0 || to >= Count || to - from < 2) return false;

        double span = _x[to] - _x[from];

        for (int i = from + 1; i < to; i++)
        {
            // Along the breakpoints where they are spread unevenly, which is
            // usual — a warmup curve is dense where it is cold. Falling back to
            // position where two breakpoints coincide, since the line then has
            // no width to interpolate along.
            double t = Math.Abs(span) > 1e-9
                ? (_x[i] - _x[from]) / span
                : (double)(i - from) / (to - from);

            SetY(i, _y[from] + ((_y[to] - _y[from]) * t));
        }

        return true;
    }

    /// <summary>Puts one point back to what the ECU holds.</summary>
    public void Revert(int index)
    {
        if (index < 0 || index >= Count) return;

        _x[index] = _originalX[index];
        _y[index] = _originalY[index];
    }

    /// <summary>Puts the whole curve back.</summary>
    public void Revert()
    {
        _originalX.CopyTo(_x, 0);
        _originalY.CopyTo(_y, 0);
    }

    /// <summary>
    /// The bytes this would write, or nothing where either row cannot be
    /// encoded.
    ///
    /// Both rows go together or neither does. Sending the values without the
    /// breakpoints they were drawn against leaves the ECU interpolating the new
    /// numbers over the old axis, which is a different curve from the one on
    /// screen and nothing would say so.
    /// </summary>
    public IReadOnlyList<TuneWrite>? Encode(EcuTune tune)
    {
        ArgumentNullException.ThrowIfNull(tune);

        var writes = new List<TuneWrite>();

        if (Changed(_x, _originalX))
        {
            if (tune.EncodeTable(Curve.XBins, ToGrid(_x)) is not { } write) return null;
            writes.Add(write);
        }

        if (Changed(_y, _originalY))
        {
            if (tune.EncodeTable(Curve.YBins, ToGrid(_y)) is not { } write) return null;
            writes.Add(write);
        }

        return writes;
    }

    private static bool Changed(double[] now, double[] was) =>
        now.Where((v, i) => Math.Abs(v - was[i]) > 1e-9).Any();

    /// <summary>A row as the one-column grid the array encoder takes.</summary>
    private static double[,] ToGrid(double[] values)
    {
        var grid = new double[values.Length, 1];
        for (int i = 0; i < values.Length; i++) grid[i, 0] = values[i];

        return grid;
    }

    private static double Clamp(double value, TuneConstant? constant) =>
        constant is { HasRange: true } c ? Math.Clamp(value, c.Low, c.High) : value;
}
