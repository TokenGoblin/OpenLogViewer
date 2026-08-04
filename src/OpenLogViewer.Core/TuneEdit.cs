namespace OpenLogViewer.Core;

/// <summary>
/// A rectangle of cells, as a selection in a table.
///
/// Held as two corners rather than an origin and a size, because that is what
/// dragging produces and either corner may be the first one touched.
/// </summary>
public readonly record struct TuneSelection(int FromColumn, int FromRow, int ToColumn, int ToRow)
{
    public static TuneSelection Cell(int column, int row) => new(column, row, column, row);

    public int Left => Math.Min(FromColumn, ToColumn);

    public int Right => Math.Max(FromColumn, ToColumn);

    public int Top => Math.Min(FromRow, ToRow);

    public int Bottom => Math.Max(FromRow, ToRow);

    public int Columns => Right - Left + 1;

    public int Rows => Bottom - Top + 1;

    public int Count => Columns * Rows;

    public bool Contains(int column, int row) =>
        column >= Left && column <= Right && row >= Top && row <= Bottom;

    /// <summary>The same selection held inside a table of this size.</summary>
    public TuneSelection ClampedTo(int columns, int rows) => new(
        Math.Clamp(FromColumn, 0, columns - 1), Math.Clamp(FromRow, 0, rows - 1),
        Math.Clamp(ToColumn, 0, columns - 1), Math.Clamp(ToRow, 0, rows - 1));
}

/// <summary>
/// What a selection was and what it has become.
/// </summary>
/// <param name="Cells">How many are selected.</param>
/// <param name="Changed">How many of those differ from the ECU's own values.</param>
/// <param name="FromLow">Lowest value the ECU had here.</param>
/// <param name="FromHigh">Highest it had.</param>
/// <param name="ToLow">Lowest it would become.</param>
/// <param name="ToHigh">Highest it would become.</param>
/// <param name="DeltaLow">Smallest change across the selection, signed.</param>
/// <param name="DeltaHigh">Largest change, signed.</param>
public readonly record struct TuneChange(
    int Cells,
    int Changed,
    double FromLow,
    double FromHigh,
    double ToLow,
    double ToHigh,
    double DeltaLow,
    double DeltaHigh)
{
    public bool IsSingle => Cells == 1;

    public bool Any => Changed > 0;

    /// <summary>The value, where exactly one cell is selected.</summary>
    public double From => FromLow;

    public double To => ToLow;

    public double Delta => DeltaLow;

    /// <summary>
    /// The change as a proportion of what was there, where that means anything.
    ///
    /// NaN from a cell that held zero, which a spark table's cells legitimately
    /// do — a percentage of nothing is not a number, and showing it as an
    /// infinite increase would be worse than showing nothing.
    /// </summary>
    public double Percent =>
        IsSingle && Math.Abs(From) > 1e-9 ? (To - From) / From * 100 : double.NaN;

    /// <summary>Whether every changed cell moved by the same amount.</summary>
    public bool Uniform => Math.Abs(DeltaHigh - DeltaLow) < 1e-9;
}

/// <summary>
/// A tuning table being changed, and what it was before.
///
/// Editing a table is not editing a grid of numbers. What is on screen has to
/// be turned back into the bytes the ECU holds, held to the limits the firmware
/// declares, and — most of all — kept separable from what was read off the
/// controller, so that at every moment it is clear which cells have been touched
/// and what they used to say. A tuner who cannot see what they have changed
/// cannot decide whether to send it.
///
/// Nothing here writes anything. This produces the edited values and the bytes
/// they encode to; sending them is a separate, deliberate act.
/// </summary>
public sealed class TuneEdit
{
    private readonly double[,] _original;
    private readonly double[,] _values;

    public TuneEdit(TuneTable table, TuneConstant? constant = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        Table = table;
        Constant = constant;

        _original = (double[,])table.Values.Clone();
        _values = (double[,])table.Values.Clone();
    }

    public TuneTable Table { get; }

    /// <summary>The firmware's description of the cells, where one is known.</summary>
    public TuneConstant? Constant { get; }

    public string Name => Table.Name;

    public string Units => Table.Units;

    public int Columns => _values.GetLength(0);

    public int Rows => _values.GetLength(1);

    /// <summary>Decimals to show, from the firmware's own declaration.</summary>
    public int Digits => Constant?.Digits ?? 0;

    /// <summary>
    /// The smallest change worth making, from the firmware's storage.
    ///
    /// A table stored as whole bytes cannot hold half a percent, so nudging by
    /// less than one raw count would show a change on screen that the ECU
    /// rounds away — which reads as the write having been ignored.
    /// </summary>
    public double Step => Constant is { Scale: > 0 } c ? Math.Abs(c.Scale) : 1;

    public double Low => Constant is { HasRange: true } c ? c.Low : double.NaN;

    public double High => Constant is { HasRange: true } c ? c.High : double.NaN;

    public double this[int column, int row] => _values[column, row];

    public double Original(int column, int row) => _original[column, row];

    /// <summary>
    /// Whether a cell differs from what was read off the ECU.
    ///
    /// Compared against a fraction of the storage step rather than exactly:
    /// scaling a table by a percentage and scaling it back leaves floating-point
    /// dust that encodes to the identical byte, and reporting that as a change
    /// would mean a table that says it is dirty and writes nothing.
    /// </summary>
    public bool IsChanged(int column, int row) =>
        Math.Abs(_values[column, row] - _original[column, row]) > Step / 1000;

    public bool HasChanges
    {
        get
        {
            for (int column = 0; column < Columns; column++)
                for (int row = 0; row < Rows; row++)
                    if (IsChanged(column, row)) return true;

            return false;
        }
    }

    public int ChangedCount
    {
        get
        {
            int count = 0;

            for (int column = 0; column < Columns; column++)
                for (int row = 0; row < Rows; row++)
                    if (IsChanged(column, row)) count++;

            return count;
        }
    }

    /// <summary>Sets every selected cell to one value.</summary>
    public void Set(TuneSelection selection, double value) =>
        Apply(selection, _ => value);

    /// <summary>Adds to every selected cell — the arrow-key edit.</summary>
    public void Add(TuneSelection selection, double delta) =>
        Apply(selection, current => current + delta);

    /// <summary>
    /// Scales every selected cell by a percentage.
    ///
    /// The operation tuning is actually done in: a region reading four per cent
    /// lean is corrected by adding four per cent to it, not by typing sixteen
    /// numbers.
    /// </summary>
    public void Scale(TuneSelection selection, double percent) =>
        Apply(selection, current => current * (1 + (percent / 100)));

    /// <summary>
    /// Fills the inside of a selection from its edges.
    ///
    /// The operation for smoothing a table after a few cells have been pulled
    /// about: pick a region whose ends are right, and let everything between
    /// them be a straight line rather than a staircase. The corners are left
    /// exactly as they are, so interpolating twice changes nothing the second
    /// time.
    ///
    /// One formula covers all three shapes. A selection one row tall has nothing
    /// to vary down it, so the vertical term falls out and it is a straight line
    /// along the row; one column wide is the same the other way; anything larger
    /// is bilinear between the four corners.
    /// </summary>
    /// <returns>
    /// False where there was nothing between the ends to fill — a selection two
    /// cells across is all corners, and reporting that as done would leave the
    /// user believing something happened.
    /// </returns>
    public bool Interpolate(TuneSelection selection)
    {
        TuneSelection area = selection.ClampedTo(Columns, Rows);

        if (area.Columns < 3 && area.Rows < 3) return false;

        double topLeft = _values[area.Left, area.Top];
        double topRight = _values[area.Right, area.Top];
        double bottomLeft = _values[area.Left, area.Bottom];
        double bottomRight = _values[area.Right, area.Bottom];

        for (int column = area.Left; column <= area.Right; column++)
        {
            double across = area.Columns > 1 ? (double)(column - area.Left) / (area.Columns - 1) : 0;

            double top = topLeft + ((topRight - topLeft) * across);
            double bottom = bottomLeft + ((bottomRight - bottomLeft) * across);

            for (int row = area.Top; row <= area.Bottom; row++)
            {
                double down = area.Rows > 1 ? (double)(row - area.Top) / (area.Rows - 1) : 0;

                _values[column, row] = Hold(top + ((bottom - top) * down));
            }
        }

        return true;
    }

    /// <summary>
    /// What the selected cells were and what they have become.
    ///
    /// The question a tuner is actually asking before sending anything: not "how
    /// much did I change it by" but "what is it going to be". Those are the same
    /// question only if you can remember what it said before, and by the fourth
    /// nudge nobody can.
    /// </summary>
    public TuneChange Preview(TuneSelection selection)
    {
        TuneSelection area = selection.ClampedTo(Columns, Rows);

        double fromLow = double.PositiveInfinity, fromHigh = double.NegativeInfinity;
        double toLow = double.PositiveInfinity, toHigh = double.NegativeInfinity;
        double deltaLow = double.PositiveInfinity, deltaHigh = double.NegativeInfinity;
        int changed = 0;

        for (int column = area.Left; column <= area.Right; column++)
            for (int row = area.Top; row <= area.Bottom; row++)
            {
                double was = _original[column, row];
                double now = _values[column, row];

                fromLow = Math.Min(fromLow, was);
                fromHigh = Math.Max(fromHigh, was);
                toLow = Math.Min(toLow, now);
                toHigh = Math.Max(toHigh, now);
                deltaLow = Math.Min(deltaLow, now - was);
                deltaHigh = Math.Max(deltaHigh, now - was);

                if (IsChanged(column, row)) changed++;
            }

        return new TuneChange(
            area.Count, changed, fromLow, fromHigh, toLow, toHigh, deltaLow, deltaHigh);
    }

    /// <summary>Puts every cell back to what the ECU said.</summary>
    public void Revert() =>
        Array.Copy(_original, _values, _original.Length);

    /// <summary>Puts the selected cells back, leaving other edits alone.</summary>
    public void Revert(TuneSelection selection)
    {
        TuneSelection area = selection.ClampedTo(Columns, Rows);

        for (int column = area.Left; column <= area.Right; column++)
            for (int row = area.Top; row <= area.Bottom; row++)
                _values[column, row] = _original[column, row];
    }

    private void Apply(TuneSelection selection, Func<double, double> change)
    {
        TuneSelection area = selection.ClampedTo(Columns, Rows);

        for (int column = area.Left; column <= area.Right; column++)
            for (int row = area.Top; row <= area.Bottom; row++)
                _values[column, row] = Hold(change(_values[column, row]));
    }

    /// <summary>
    /// Holds a value inside what the firmware allows.
    ///
    /// Clamped rather than refused: a tuner scaling a whole table up by ten per
    /// cent should not have the operation rejected because one cell was already
    /// at the limit. What must not happen is a value going out past the range
    /// and being written — the encoder would refuse it and the whole write would
    /// fail, which turns a small mistake in one cell into a table that cannot be
    /// sent at all.
    /// </summary>
    private double Hold(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 0;

        return Constant is { HasRange: true } c ? Math.Clamp(value, c.Low, c.High) : value;
    }

    /// <summary>The edited cells, for drawing.</summary>
    public double[,] Values => (double[,])_values.Clone();

    /// <summary>The edited table, so the same view can draw it.</summary>
    public TuneTable AsTable() => Table with { Values = Values };

    /// <summary>
    /// The bytes this would write, or null when the firmware's table cannot be
    /// encoded — a value out of range, or a constant this INI does not describe.
    /// </summary>
    public TuneWrite? Encode(EcuTune tune)
    {
        ArgumentNullException.ThrowIfNull(tune);

        return Constant is null ? null : tune.EncodeTable(Constant.Name, _values);
    }
}
