using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// A tuning table as the ECU holds it: breakpoints down the side and along the
/// bottom, cells shaded by value.
///
/// Shaded on the same sequential ramp the heat table uses, so a fuel table read
/// off the controller and a fuel table binned out of a log look like the same
/// kind of object — which is the point of having both on screen.
/// </summary>
public sealed class TuneTableView : FrameworkElement
{
    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(TuneTable), typeof(TuneTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The table being changed, when there is one.
    ///
    /// Drawn from this in preference to <see cref="Table"/>, because it knows
    /// both what the cells say now and what the ECU said — and showing the
    /// difference is the whole job. A tuner who cannot see what they have
    /// touched cannot decide whether to send it.
    /// </summary>
    public static readonly DependencyProperty EditProperty = DependencyProperty.Register(
        nameof(Edit), typeof(TuneEdit), typeof(TuneTableView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(TuneSelection), typeof(TuneTableView),
        new FrameworkPropertyMetadata(default(TuneSelection), FrameworkPropertyMetadataOptions.AffectsRender));

    public TuneEdit? Edit
    {
        get => (TuneEdit?)GetValue(EditProperty);
        set => SetValue(EditProperty, value);
    }

    public TuneSelection Selection
    {
        get => (TuneSelection)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>Raised when the pointer or the keyboard moves the selection.</summary>
    public event Action<TuneSelection>? SelectionChanged;

    /// <summary>Raised when a key that changes values is pressed over the table.</summary>
    public event Action<TuneTableEdit>? EditRequested;

    private Color[] _ramp = [];
    private Brush _ground = null!;
    private Brush _ink = null!;
    private Brush _axisInk = null!;
    private Brush _darkInk = null!;
    private Brush _lightInk = null!;
    private Pen _grid = null!;
    private Pen _selected = null!;
    private Pen _changed = null!;
    private Typeface _typeface = null!;

    public TuneTableView()
    {
        ClipToBounds = true;
        Focusable = true;

        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
    }

    /// <summary>The geometry of the last render, for turning a point into a cell.</summary>
    private (double Left, double Top, double CellWidth, double CellHeight, int Columns, int Rows) _grid2;

    /// <summary>The cell under a point, or null when the point is outside the table.</summary>
    private (int Column, int Row)? CellAt(Point point)
    {
        if (_grid2.CellWidth <= 0 || _grid2.CellHeight <= 0) return null;

        int column = (int)((point.X - _grid2.Left) / _grid2.CellWidth);

        // Rows are drawn with load increasing upward, so the arithmetic runs the
        // other way from the pixels.
        int row = _grid2.Rows - 1 - (int)((point.Y - _grid2.Top) / _grid2.CellHeight);

        if (column < 0 || column >= _grid2.Columns || row < 0 || row >= _grid2.Rows) return null;

        return (column, row);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (CellAt(e.GetPosition(this)) is not { } cell) return;

        // Shift extends the block from where it started, which is how every
        // other grid behaves and how a region is picked out for scaling.
        Selection = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? Selection with { ToColumn = cell.Column, ToRow = cell.Row }
            : TuneSelection.Cell(cell.Column, cell.Row);

        SelectionChanged?.Invoke(Selection);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        if (CellAt(e.GetPosition(this)) is not { } cell) return;

        Selection = Selection with { ToColumn = cell.Column, ToRow = cell.Row };
        SelectionChanged?.Invoke(Selection);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary>
    /// The keyboard, which is how tuning is actually done.
    ///
    /// Arrows move, shift-arrows extend the block, and the value keys act on
    /// whatever is selected. Nothing here writes to the ECU: every one of these
    /// changes the table on screen and leaves sending it a separate, deliberate
    /// act.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Edit is not { } edit) return;

        bool extend = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.Left: Move(-1, 0, extend); break;
            case Key.Right: Move(1, 0, extend); break;
            case Key.Up: Move(0, 1, extend); break;
            case Key.Down: Move(0, -1, extend); break;

            // The nudge. A page-step is ten of the firmware's own smallest
            // increment, which keeps a coarse move meaningful on a table stored
            // as tenths as well as one stored as whole numbers.
            case Key.OemPlus or Key.Add:
                Raise(TuneTableEdit.Add(extend ? edit.Step * 10 : edit.Step));
                break;

            case Key.OemMinus or Key.Subtract:
                Raise(TuneTableEdit.Add(extend ? -edit.Step * 10 : -edit.Step));
                break;

            case Key.PageUp: Raise(TuneTableEdit.Scale(extend ? 5 : 1)); break;
            case Key.PageDown: Raise(TuneTableEdit.Scale(extend ? -5 : -1)); break;

            case Key.Escape: Raise(TuneTableEdit.RevertSelection()); break;

            default: return;
        }

        e.Handled = true;
    }

    private void Raise(TuneTableEdit edit) => EditRequested?.Invoke(edit);

    private void Move(int byColumn, int byRow, bool extend)
    {
        if (Edit is not { } edit) return;

        int column = Math.Clamp(Selection.ToColumn + byColumn, 0, edit.Columns - 1);
        int row = Math.Clamp(Selection.ToRow + byRow, 0, edit.Rows - 1);

        Selection = extend
            ? Selection with { ToColumn = column, ToRow = row }
            : TuneSelection.Cell(column, row);

        SelectionChanged?.Invoke(Selection);
    }

    public TuneTable? Table
    {
        get => (TuneTable?)GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    private void ApplyTheme(Theme theme)
    {
        _ramp = theme.SequentialRamp;
        _ground = Fill(theme.Background);
        _ink = Fill(theme.Text);
        _axisInk = Fill(theme.Muted);

        // Cell text flips by the fill's luminance, so these are the extremes
        // rather than the theme's own ink.
        _darkInk = Fill(Color.FromRgb(0x0B, 0x0B, 0x0B));
        _lightInk = Fill(Colors.White);

        _grid = Frozen(new Pen(Fill(theme.Line), 1));

        // The selection has to be findable on a 16x16 grid of coloured cells, so
        // it is the accent at full weight; a changed cell is the same colour
        // thinner, which reads as "these are the same kind of thing" without the
        // two competing.
        _selected = Frozen(new Pen(Fill(theme.Accent), 2));
        _changed = Frozen(new Pen(Fill(theme.Accent), 1));
        _typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        InvalidateVisual();
    }

    private static Brush Fill(Color c) => Frozen(new SolidColorBrush(c));

    private static Rect Deflate(Rect r, double by) =>
        new(r.X + by, r.Y + by, Math.Max(0, r.Width - by * 2), Math.Max(0, r.Height - by * 2));

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(_ground, null, full);

        // The edit when there is one, so the cells on screen are the cells that
        // would be sent.
        TuneEdit? edit = Edit;
        TuneTable? table = edit?.AsTable() ?? Table;

        if (table is null || table.Columns == 0 || table.Rows == 0) return;

        // Room for the row labels down the left and the column labels along the
        // bottom, which is where a tuning table puts them.
        const double LabelWidth = 58;
        const double LabelHeight = 22;
        const double Pad = 10;

        double width = ActualWidth - LabelWidth - Pad * 2;
        double height = ActualHeight - LabelHeight - Pad * 2;
        if (width < 40 || height < 40) return;

        double cellWidth = width / table.Columns;
        double cellHeight = height / table.Rows;

        (double low, double high) = Extent(table);

        // Remembered so a click can be turned back into a cell.
        _grid2 = (Pad + LabelWidth, Pad, cellWidth, cellHeight, table.Columns, table.Rows);

        TuneSelection selection = Selection.ClampedTo(table.Columns, table.Rows);
        string format = edit is not null ? "0." + new string('#', Math.Max(1, edit.Digits)) : "0.#";

        for (int row = 0; row < table.Rows; row++)
        {
            // Load increases upward, as every tuning table draws it.
            double y = Pad + (table.Rows - 1 - row) * cellHeight;

            for (int column = 0; column < table.Columns; column++)
            {
                double x = Pad + LabelWidth + column * cellWidth;
                double value = table.Values[column, row];

                var cell = new Rect(x, y, cellWidth + 0.5, cellHeight + 0.5);
                Color fill = Shade(value, low, high);

                dc.DrawRectangle(Fill(fill), null, cell);

                // A changed cell is outlined rather than recoloured, so the
                // shading still says what the value is while the outline says
                // it is not what the ECU holds. Recolouring would cost the one
                // and answer the other badly.
                if (edit?.IsChanged(column, row) == true)
                    dc.DrawRectangle(null, _changed, Deflate(cell, 1.5));

                if (cellWidth < 26 || cellHeight < 12) continue;

                FormattedText text = Text(
                    value.ToString(format, CultureInfo.CurrentCulture),
                    Math.Min(11, cellHeight * 0.62),
                    ColorMath.Luminance(fill) > 0.5 ? _darkInk : _lightInk);

                dc.DrawText(text, new Point(
                    x + (cellWidth - text.Width) / 2,
                    y + (cellHeight - text.Height) / 2));
            }

            if (cellHeight < 11) continue;

            FormattedText label = Text($"{table.Y.Breakpoints[row]:G5}", 10, _axisInk);
            dc.DrawText(label, new Point(
                Pad + LabelWidth - label.Width - 5,
                y + (cellHeight - label.Height) / 2));
        }

        // Column breakpoints along the bottom, thinned when they will not fit.
        int step = Math.Max(1, (int)Math.Ceiling(34 / cellWidth));

        for (int column = 0; column < table.Columns; column += step)
        {
            FormattedText label = Text($"{table.X.Breakpoints[column]:G5}", 10, _axisInk);

            dc.DrawText(label, new Point(
                Pad + LabelWidth + column * cellWidth + (cellWidth - label.Width) / 2,
                Pad + height + 4));
        }

        dc.DrawRectangle(null, _grid, new Rect(Pad + LabelWidth, Pad, width, height));

        // The selection last, over everything, because it is the thing being
        // acted on and has to be findable at a glance on a 16x16 table.
        if (edit is not null)
        {
            double left = Pad + LabelWidth + selection.Left * cellWidth;
            double top = Pad + (table.Rows - 1 - selection.Bottom) * cellHeight;

            dc.DrawRectangle(null, _selected, new Rect(
                left, top, selection.Columns * cellWidth, selection.Rows * cellHeight));
        }

        // Units, where the two axis labels meet.
        if (table.Units.Length > 0)
            dc.DrawText(Text(table.Units, 10, _ink), new Point(Pad, Pad + height + 4));
    }

    /// <summary>
    /// The range to shade over.
    ///
    /// Taken from the table rather than from its declared limits: a fuel table
    /// spanning 26 to 114 shaded against a 0 to 999 declaration is one flat
    /// colour, which shows nothing.
    /// </summary>
    private static (double Low, double High) Extent(TuneTable table)
    {
        double low = double.MaxValue;
        double high = double.MinValue;

        foreach (double value in table.Values)
        {
            if (double.IsNaN(value)) continue;

            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }

        return low <= high ? (low, high) : (0, 1);
    }

    private Color Shade(double value, double low, double high)
    {
        if (_ramp.Length == 0 || double.IsNaN(value)) return Colors.Gray;
        if (high <= low) return _ramp[_ramp.Length / 2];

        double at = Math.Clamp((value - low) / (high - low), 0, 1) * (_ramp.Length - 1);
        int index = (int)at;

        return index >= _ramp.Length - 1
            ? _ramp[^1]
            : ColorMath.Blend(_ramp[index], _ramp[index + 1], at - index);
    }

    private FormattedText Text(string text, double size, Brush ink) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, size, ink,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
