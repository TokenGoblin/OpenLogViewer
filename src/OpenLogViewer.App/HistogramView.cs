using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// Renders a <see cref="HistogramTable"/> as a heat table — the shape of an ECU
/// tuning table, so a log can be read against the tune it came from.
/// </summary>
public sealed class HistogramView : FrameworkElement
{
    private const double LeftGutter = 66;
    private const double BottomGutter = 32;
    private const double TopGutter = 44;
    private const double RightPad = 14;
    private const double CellGap = 1.5;

    /// <summary>
    /// Sequential ramp, low→high. One hue, monotonically lightening against a
    /// dark ground and darkening against a light one: magnitude is carried by
    /// lightness, which survives colour-vision deficiency and greyscale.
    /// </summary>
    private Color[] Ramp = [];

    /// <summary>
    /// Diverging scale for deltas: two hues away from a neutral midpoint, cool
    /// below the target and warm above it. Polarity is not magnitude — a single
    /// hue could not show which side of zero a cell sits on. The midpoint sits
    /// near the surface so cells that are on target recede.
    /// </summary>
    private Color[] CoolArm = [];

    private Color[] WarmArm = [];

    private Brush Background = null!;
    private Brush AxisInk = null!;
    private Brush TitleInk = null!;
    private Brush EmptyCell = null!;
    private Brush DarkInk = null!;
    private Brush LightInk = null!;
    private Pen HoverPen = null!;
    private Pen SelectedCellPen = null!;

    private void ApplyTheme(Theme theme)
    {
        Ramp = theme.SequentialRamp;
        CoolArm = theme.CoolArm;
        WarmArm = theme.WarmArm;

        Background = Fill(theme.Background);
        AxisInk = Fill(theme.Muted);
        TitleInk = Fill(theme.Text);
        EmptyCell = Fill(theme.EmptyCell);

        // Cell text flips between these two by the fill's luminance, so they are
        // the extremes rather than the theme's own ink.
        DarkInk = Fill(Color.FromRgb(0x0B, 0x0B, 0x0B));
        LightInk = Fill(Colors.White);

        HoverPen = Frozen(new Pen(Fill(theme.Text), 1.5));
        SelectedCellPen = Frozen(new Pen(Fill(theme.Marker), 2));

        InvalidateVisual();
    }

    private static Brush Fill(Color c) => Frozen(new SolidColorBrush(c));

    private HistogramTable? _table;
    private bool _colorByCount;
    private (int Column, int Row) _hover = (-1, -1);
    private (int Column, int Row) _selected = (-1, -1);

    /// <summary>Raised when a populated cell is clicked, to trace it back to the log.</summary>
    public event Action<(int Column, int Row)>? CellActivated;

    /// <summary>Marks the cell whose samples are being shown in the log view.</summary>
    public void SetSelectedCell((int Column, int Row)? cell)
    {
        _selected = cell ?? (-1, -1);
        InvalidateVisual();
    }

    public HistogramView()
    {
        ClipToBounds = true;

        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
    }

    public void SetTable(HistogramTable? table, bool colorByCount)
    {
        _table = table;
        _colorByCount = colorByCount;
        _hover = (-1, -1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, full);

        if (_table is not { } table)
        {
            Centre(dc, full, "Choose channels to build a table");
            return;
        }

        if (table.IsEmpty)
        {
            Centre(dc, full, "No samples in this range");
            return;
        }

        double cellWidth = (ActualWidth - LeftGutter - RightPad) / table.Columns;
        double cellHeight = (ActualHeight - TopGutter - BottomGutter) / table.Rows;
        if (cellWidth < 6 || cellHeight < 6) { Centre(dc, full, "Not enough room — reduce rows or columns"); return; }

        DrawTitle(dc, table);
        DrawLegend(dc, table);

        bool roomForText = cellWidth >= 34 && cellHeight >= 15;

        for (int c = 0; c < table.Columns; c++)
        for (int r = 0; r < table.Rows; r++)
        {
            Rect cell = CellBounds(table, c, r, cellWidth, cellHeight);

            if (table.Values[c, r] is not { } value)
            {
                dc.DrawRectangle(EmptyCell, null, cell);
                continue;
            }

            Color fill = Shade(table, c, r, value);
            dc.DrawRectangle(new SolidColorBrush(fill), null, cell);

            if (!roomForText) continue;

            FormattedText text = Label(table.Format(c, r), 11, Ink(fill));
            if (text.Width > cell.Width - 4) continue;

            dc.DrawText(text, new Point(
                cell.X + (cell.Width - text.Width) / 2,
                cell.Y + (cell.Height - text.Height) / 2));
        }

        DrawAxes(dc, table, cellWidth, cellHeight);
        DrawHover(dc, table, cellWidth, cellHeight);
    }

    private Rect CellBounds(HistogramTable table, int column, int row, double width, double height) =>
        new(LeftGutter + column * width + CellGap / 2,
            // Row 0 holds the lowest Y value and belongs at the bottom.
            TopGutter + (table.Rows - 1 - row) * height + CellGap / 2,
            Math.Max(1, width - CellGap),
            Math.Max(1, height - CellGap));

    /// <summary>Position of a cell on the scale, by aggregated value or by sample count.</summary>
    private Color Shade(HistogramTable table, int column, int row, double value)
    {
        if (_colorByCount)
        {
            double t = table.MaxCount <= 1 ? 1 : (double)(table.Counts[column, row] - 1) / (table.MaxCount - 1);
            return Sample(Ramp, Math.Clamp(t, 0, 1));
        }

        if (table.ShowsDeviation)
        {
            // Scaled by the largest deviation either way, so equal errors in
            // opposite directions get equal intensity.
            double reach = table.MaxDeviation;
            double t = reach <= 0 ? 0 : Math.Clamp(Math.Abs(value) / reach, 0, 1);
            return Sample(value < 0 ? CoolArm : WarmArm, t);
        }

        double span = table.MaxValue - table.MinValue;
        return Sample(Ramp, span <= 0 ? 1 : Math.Clamp((value - table.MinValue) / span, 0, 1));
    }

    private static Color Sample(Color[] ramp, double t)
    {
        double scaled = t * (ramp.Length - 1);
        int index = Math.Min((int)scaled, ramp.Length - 2);
        double f = scaled - index;

        Color a = ramp[index], b = ramp[index + 1];
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * f),
            (byte)Math.Round(a.G + (b.G - a.G) * f),
            (byte)Math.Round(a.B + (b.B - a.B) * f));
    }

    /// <summary>Flips the cell text between light and dark so it stays legible on any step.</summary>
    private Brush Ink(Color fill) => ColorMath.Luminance(fill) > 0.4 ? DarkInk : LightInk;

    private void DrawTitle(DrawingContext dc, HistogramTable table)
    {
        // A computed table names itself: its cells hold a suggestion, not an
        // aggregate of Z, so "mean AFR" would describe the wrong thing.
        string what = table.DisplayName ?? Aggregate(table);

        FormattedText title = Label($"{table.X.Name}  ×  {table.Y.Name}   —   {what}", 13, TitleInk);
        dc.DrawText(title, new Point(LeftGutter, 10));
    }

    private static string Aggregate(HistogramTable table)
    {
        string statistic = table.Statistic switch
        {
            HistogramStatistic.Mean => "mean",
            HistogramStatistic.Min => "min",
            HistogramStatistic.Max => "max",
            _ => "count of",
        };

        string measure = table.IsDelta
            ? $"{table.Z.Name} − {table.ZCompare!.Name}"
            : table.Z.Name;

        return $"{statistic} {measure}";
    }

    /// <summary>
    /// A ramp legend with its end values, so the colour encoding is never the only
    /// way to recover a magnitude.
    /// </summary>
    private void DrawLegend(DrawingContext dc, HistogramTable table)
    {
        const double width = 132, height = 9;
        bool diverging = table.ShowsDeviation && !_colorByCount;

        (string low, string high) = _colorByCount
            ? ("1", table.MaxCount.ToString("N0"))
            : diverging
                ? ($"−{table.Z.Format(table.MaxDeviation)}", $"+{table.Z.Format(table.MaxDeviation)}")
                : (table.Format(table.MinValue), table.Format(table.MaxValue));

        FormattedText lowText = Label(low, 10, AxisInk);
        FormattedText highText = Label(high, 10, AxisInk);
        FormattedText caption = Label(
            _colorByCount ? "samples" : diverging ? "vs target" : table.Z.Name, 10, AxisInk);

        // Laid out right to left: caption, low value, ramp, high value.
        double right = ActualWidth - RightPad;
        double highX = right - highText.Width;
        double barX = highX - 6 - width;
        double lowX = barX - 6 - lowText.Width;
        double captionX = lowX - 10 - caption.Width;

        double y = 16;
        double textY = y + (height - lowText.Height) / 2;

        for (int i = 0; i < width; i++)
        {
            double t = i / (width - 1);

            // A diverging bar runs cool → neutral → warm, with zero at the centre.
            Color c = diverging
                ? t < 0.5 ? Sample(CoolArm, 1 - t * 2) : Sample(WarmArm, (t - 0.5) * 2)
                : Sample(Ramp, t);

            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(barX + i, y, 1.2, height));
        }

        if (diverging)
        {
            // Mark the zero point, so "on target" is locatable on the scale.
            FormattedText zero = Label("0", 9, AxisInk);
            dc.DrawText(zero, new Point(barX + width / 2 - zero.Width / 2, y + height + 1));
        }

        dc.DrawText(caption, new Point(captionX, textY));
        dc.DrawText(lowText, new Point(lowX, textY));
        dc.DrawText(highText, new Point(highX, textY));
    }

    private void DrawAxes(DrawingContext dc, HistogramTable table, double cellWidth, double cellHeight)
    {
        // Label every nth bin when they would otherwise collide.
        int xStride = Math.Max(1, (int)Math.Ceiling(42 / Math.Max(1, cellWidth)));
        int yStride = Math.Max(1, (int)Math.Ceiling(15 / Math.Max(1, cellHeight)));

        for (int c = 0; c < table.Columns; c += xStride)
        {
            FormattedText text = Label(Axis(table.ColumnCenters[c]), 10, AxisInk);
            double centre = LeftGutter + (c + 0.5) * cellWidth;
            dc.DrawText(text, new Point(centre - text.Width / 2, TopGutter + table.Rows * cellHeight + 6));
        }

        for (int r = 0; r < table.Rows; r += yStride)
        {
            FormattedText text = Label(Axis(table.RowCenters[r]), 10, AxisInk);
            double centre = TopGutter + (table.Rows - 1 - r + 0.5) * cellHeight;
            dc.DrawText(text, new Point(LeftGutter - text.Width - 7, centre - text.Height / 2));
        }

        FormattedText xName = Label(table.X.Name, 10, AxisInk);
        dc.DrawText(xName, new Point(
            LeftGutter + (table.Columns * cellWidth - xName.Width) / 2,
            ActualHeight - xName.Height - 2));
    }

    private void DrawHover(DrawingContext dc, HistogramTable table, double cellWidth, double cellHeight)
    {
        if (_selected is { Column: >= 0, Row: >= 0 }
            && _selected.Column < table.Columns && _selected.Row < table.Rows)
        {
            dc.DrawRectangle(null, SelectedCellPen,
                CellBounds(table, _selected.Column, _selected.Row, cellWidth, cellHeight));
        }

        (int column, int row) = _hover;
        if (column < 0 || row < 0) return;

        Rect cell = CellBounds(table, column, row, cellWidth, cellHeight);
        dc.DrawRectangle(null, HoverPen, cell);

        int count = table.Counts[column, row];
        string detail = count == 0
            ? $"{table.X.Name} {Axis(table.ColumnCenters[column])} · {table.Y.Name} {Axis(table.RowCenters[row])} — no samples"
            : $"{table.X.Name} {Axis(table.ColumnCenters[column])} · {table.Y.Name} {Axis(table.RowCenters[row])} — " +
              $"{table.Z.Name} {table.Format(column, row)} from {count:N0} sample{(count == 1 ? "" : "s")}";

        FormattedText text = Label(detail, 11, TitleInk);
        dc.DrawText(text, new Point(LeftGutter, ActualHeight - text.Height - 2));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_table is not { } table) return;

        double cellWidth = (ActualWidth - LeftGutter - RightPad) / table.Columns;
        double cellHeight = (ActualHeight - TopGutter - BottomGutter) / table.Rows;
        if (cellWidth <= 0 || cellHeight <= 0) return;

        Point p = e.GetPosition(this);
        int column = (int)((p.X - LeftGutter) / cellWidth);
        int row = table.Rows - 1 - (int)((p.Y - TopGutter) / cellHeight);

        (int, int) hit = p.X < LeftGutter || p.Y < TopGutter
                         || column < 0 || column >= table.Columns || row < 0 || row >= table.Rows
            ? (-1, -1)
            : (column, row);

        if (hit == _hover) return;
        _hover = hit;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Only cells with data can be traced back to samples.
        (int column, int row) = _hover;
        if (_table is null || column < 0 || row < 0) return;
        if (_table.Counts[column, row] == 0) return;

        CellActivated?.Invoke((column, row));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == (-1, -1)) return;

        _hover = (-1, -1);
        InvalidateVisual();
    }

    private static string Axis(double value) =>
        Math.Abs(value) >= 100 ? value.ToString("N0") : value.ToString("0.#");

    private void Centre(DrawingContext dc, Rect full, string message)
    {
        FormattedText text = Label(message, 13, AxisInk);
        dc.DrawText(text, new Point((full.Width - text.Width) / 2, (full.Height - text.Height) / 2));
    }

    private FormattedText Label(string s, double size, Brush brush) => new(
        s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

