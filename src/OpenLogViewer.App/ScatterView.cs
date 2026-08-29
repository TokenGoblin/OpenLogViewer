using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// Renders a <see cref="ScatterPlot"/> — every sample at its own X and Y,
/// coloured by a third channel.
///
/// The marks are the blocks of <see cref="ScatterPlot.Bin"/>, drawn as a bitmap
/// one pixel per block and scaled up with nearest-neighbour. That is not an
/// optimisation dressed up as a design: the grid <em>is</em> a pixel grid, so
/// making the bitmap the same shape means a block cannot be resampled into its
/// neighbour and every mark keeps the colour that was computed for it.
///
/// Blocks nothing landed in are left transparent rather than tinted the way the
/// heat table tints an empty cell. On a sixteen-by-sixteen table "no samples
/// here" is a fact about a cell being tuned and worth drawing; at three pixels a
/// block it is most of the window, and drawing it would put a slab over the
/// plot to say the engine cannot run at 7000 rpm and 20 kPa at once.
/// </summary>
public sealed class ScatterView : FrameworkElement
{
    private const double LeftGutter = 66;
    private const double BottomGutter = 32;
    private const double TopGutter = 44;
    private const double RightPad = 14;

    /// <summary>
    /// Device-independent pixels per block, in both directions.
    ///
    /// Three is a judgement between two failures. Finer and a mark is too small
    /// to carry a colour at a glance, and a sparse log becomes dust. Coarser and
    /// this starts averaging away the spread it exists to show — at ten pixels a
    /// block on a normal window the grid is down to about ninety by sixty, which
    /// is on its way back to being a table.
    /// </summary>
    private const int BlockSize = 3;

    private Color[] Ramp = [];
    private Color[] CoolArm = [];
    private Color[] WarmArm = [];

    private Brush Background = null!;
    private Brush AxisInk = null!;
    private Brush TitleInk = null!;
    private Pen GridPen = null!;
    private Pen HoverPen = null!;

    private void ApplyTheme(Theme theme)
    {
        Ramp = theme.SequentialRamp;
        CoolArm = theme.CoolArm;
        WarmArm = theme.WarmArm;

        Background = Fill(theme.Background);
        AxisInk = Fill(theme.Muted);
        TitleInk = Fill(theme.Text);
        GridPen = Frozen(new Pen(Fill(theme.EmptyCell), 1));
        HoverPen = Frozen(new Pen(Fill(theme.Text), 1.5));

        Invalidate();
    }

    private static Brush Fill(Color c) => Frozen(new SolidColorBrush(c));

    private ScatterPlot? _plot;
    private ScatterBins? _bins;
    private WriteableBitmap? _marks;
    private bool _colorByCount;
    private (int Column, int Row) _hover = (-1, -1);

    /// <summary>Raised when an occupied block is clicked, to trace it back to the log.</summary>
    public event Action<(int Column, int Row)>? BlockActivated;

    /// <summary>The block grid the marks were drawn on, for tracing a click back.</summary>
    public ScatterBins? Bins => _bins;

    public ScatterView()
    {
        ClipToBounds = true;

        // The bitmap is the block grid at one pixel per block; anything but
        // nearest-neighbour would blend a mark into the one beside it.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
    }

    public void SetPlot(ScatterPlot? plot, bool colorByCount)
    {
        _plot = plot;
        _colorByCount = colorByCount;
        _hover = (-1, -1);
        Invalidate();
    }

    /// <summary>Throws away the binned marks, so the next render rebuilds them.</summary>
    private void Invalidate()
    {
        _bins = null;
        _marks = null;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Invalidate();
    }

    private Rect PlotArea => new(
        LeftGutter, TopGutter,
        Math.Max(0, ActualWidth - LeftGutter - RightPad),
        Math.Max(0, ActualHeight - TopGutter - BottomGutter));

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, full);

        if (_plot is not { } plot)
        {
            Centre(dc, full, "Choose channels to plot");
            return;
        }

        if (plot.IsEmpty)
        {
            Centre(dc, full, "No samples in this range");
            return;
        }

        Rect area = PlotArea;
        if (area.Width < 40 || area.Height < 40) { Centre(dc, full, "Not enough room"); return; }

        ScatterBins bins = Rebuild(plot, area);

        DrawTitle(dc, plot);
        DrawLegend(dc, plot, bins);
        DrawGrid(dc, plot, area);

        if (_marks is not null) dc.DrawImage(_marks, area);

        DrawAxes(dc, plot, area);
        DrawHover(dc, plot, bins, area);
    }

    /// <summary>
    /// Bins the points onto the grid this window has room for and paints them
    /// into the bitmap. Cached: the hover redraws on every mouse move, and the
    /// marks do not change when the pointer does.
    /// </summary>
    private ScatterBins Rebuild(ScatterPlot plot, Rect area)
    {
        if (_bins is { } cached && _marks is not null) return cached;

        int columns = Math.Max(1, (int)(area.Width / BlockSize));
        int rows = Math.Max(1, (int)(area.Height / BlockSize));

        ScatterBins bins = plot.Bin(columns, rows);
        _bins = bins;

        var bitmap = new WriteableBitmap(columns, rows, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new uint[columns * rows];

        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int bin = bins.Index(column, row);
            if (bins.Counts[bin] == 0) continue;

            // Row 0 holds the lowest Y and belongs at the bottom of the image.
            pixels[((rows - 1 - row) * columns) + column] = Argb(Shade(plot, bins, bin));
        }

        bitmap.WritePixels(new Int32Rect(0, 0, columns, rows), pixels, columns * 4, 0);
        bitmap.Freeze();
        _marks = bitmap;

        return bins;
    }

    private static uint Argb(Color c) =>
        (0xFFu << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>Position of a block on the scale, by its mean or by how busy it is.</summary>
    private Color Shade(ScatterPlot plot, ScatterBins bins, int bin)
    {
        if (_colorByCount)
        {
            // Log-scaled, unlike the heat table's. A drive spends orders of
            // magnitude more time at idle than anywhere else, so on a linear
            // scale idle is the only block with any colour in it and the rest of
            // the map reads as equally empty.
            double t = bins.Busiest <= 1
                ? 1
                : Math.Log(bins.Counts[bin]) / Math.Log(bins.Busiest);
            return Sample(Ramp, Math.Clamp(t, 0, 1));
        }

        double value = bins.Means[bin];

        if (plot.IsDelta)
        {
            double reach = bins.MeanExtent;
            double t = reach <= 0 ? 0 : Math.Clamp(Math.Abs(value) / reach, 0, 1);
            return Sample(value < 0 ? CoolArm : WarmArm, t);
        }

        // Over the trimmed range, so the drive is coloured rather than the two
        // transients at its extremes. Clamped, so a block past the bound
        // saturates and stays the most extreme mark on the plot.
        double span = bins.ColorHigh - bins.ColorLow;
        return Sample(Ramp, span <= 0 ? 1 : Math.Clamp((value - bins.ColorLow) / span, 0, 1));
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

    private void DrawTitle(DrawingContext dc, ScatterPlot plot)
    {
        string measure = plot.IsDelta ? $"{plot.Z.Name} − {plot.ZCompare!.Name}" : plot.Z.Name;
        FormattedText title = Label(
            $"{plot.X.Name}  ×  {plot.Y.Name}   —   {measure}", 13, TitleInk);
        dc.DrawText(title, new Point(LeftGutter, 10));
    }

    private void DrawLegend(DrawingContext dc, ScatterPlot plot, ScatterBins bins)
    {
        const double width = 132, height = 9;
        bool diverging = plot.IsDelta && !_colorByCount;

        // A bound the trim moved is marked ≤ or ≥, because a number that is not
        // the largest value on the plot must not be read as one.
        (string low, string high) = _colorByCount
            ? ("1", bins.Busiest.ToString("N0"))
            : diverging
                ? ($"−{plot.Z.Format(bins.MeanExtent)}", $"+{plot.Z.Format(bins.MeanExtent)}")
                : ((bins.ClipsLow ? "≤" : "") + plot.Z.Format(bins.ColorLow),
                   (bins.ClipsHigh ? "≥" : "") + plot.Z.Format(bins.ColorHigh));

        FormattedText lowText = Label(low, 10, AxisInk);
        FormattedText highText = Label(high, 10, AxisInk);
        FormattedText caption = Label(
            _colorByCount ? "samples" : diverging ? "vs target" : plot.Z.Name, 10, AxisInk);

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

            Color c = diverging
                ? t < 0.5 ? Sample(CoolArm, 1 - t * 2) : Sample(WarmArm, (t - 0.5) * 2)
                : Sample(Ramp, t);

            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(barX + i, y, 1.2, height));
        }

        if (diverging)
        {
            FormattedText zero = Label("0", 9, AxisInk);
            dc.DrawText(zero, new Point(barX + width / 2 - zero.Width / 2, y + height + 1));
        }

        dc.DrawText(caption, new Point(captionX, textY));
        dc.DrawText(lowText, new Point(lowX, textY));
        dc.DrawText(highText, new Point(highX, textY));
    }

    /// <summary>
    /// Gridlines behind the marks, on the same ticks the axes are labelled with.
    /// Behind rather than over: a line drawn across the data would be read as a
    /// gap in it.
    /// </summary>
    private void DrawGrid(DrawingContext dc, ScatterPlot plot, Rect area)
    {
        foreach (double tick in Ticks(plot.XMin, plot.XMax))
        {
            double x = Math.Round(area.X + Fraction(tick, plot.XMin, plot.XMax) * area.Width) + 0.5;
            dc.DrawLine(GridPen, new Point(x, area.Top), new Point(x, area.Bottom));
        }

        foreach (double tick in Ticks(plot.YMin, plot.YMax))
        {
            double y = Math.Round(area.Bottom - Fraction(tick, plot.YMin, plot.YMax) * area.Height) + 0.5;
            dc.DrawLine(GridPen, new Point(area.Left, y), new Point(area.Right, y));
        }
    }

    private void DrawAxes(DrawingContext dc, ScatterPlot plot, Rect area)
    {
        foreach (double tick in Ticks(plot.XMin, plot.XMax))
        {
            FormattedText text = Label(Axis(tick), 10, AxisInk);
            double x = area.X + Fraction(tick, plot.XMin, plot.XMax) * area.Width;
            dc.DrawText(text, new Point(x - text.Width / 2, area.Bottom + 6));
        }

        foreach (double tick in Ticks(plot.YMin, plot.YMax))
        {
            FormattedText text = Label(Axis(tick), 10, AxisInk);
            double y = area.Bottom - Fraction(tick, plot.YMin, plot.YMax) * area.Height;
            dc.DrawText(text, new Point(LeftGutter - text.Width - 7, y - text.Height / 2));
        }

        FormattedText xName = Label(plot.X.Name, 10, AxisInk);
        dc.DrawText(xName, new Point(
            area.X + (area.Width - xName.Width) / 2, ActualHeight - xName.Height - 2));
    }

    private static double Fraction(double value, double min, double max) =>
        max <= min ? 0.5 : (value - min) / (max - min);

    /// <summary>
    /// Round tick values across a range — steps of 1, 2 or 5 times a power of
    /// ten, which are the ones people read off an axis without doing arithmetic.
    /// </summary>
    internal static IReadOnlyList<double> Ticks(double min, double max, int target = 6)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) return [min];

        double raw = (max - min) / target;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalised = raw / magnitude;

        double step = magnitude * (normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10);

        var ticks = new List<double>();
        for (double t = Math.Ceiling(min / step) * step; t <= max + step * 1e-9; t += step)
            ticks.Add(Math.Abs(t) < step * 1e-9 ? 0 : t);

        return ticks.Count > 0 ? ticks : [min];
    }

    private void DrawHover(DrawingContext dc, ScatterPlot plot, ScatterBins bins, Rect area)
    {
        (int column, int row) = _hover;
        if (column < 0 || row < 0) return;

        double blockWidth = area.Width / bins.Columns;
        double blockHeight = area.Height / bins.Rows;

        // Ringed a little wider than the mark itself, which is three pixels
        // across and would otherwise be hidden by its own outline.
        var mark = new Rect(
            area.X + column * blockWidth - 2,
            area.Bottom - (row + 1) * blockHeight - 2,
            blockWidth + 4, blockHeight + 4);

        dc.DrawRectangle(null, HoverPen, mark);

        int bin = bins.Index(column, row);
        int count = bins.Counts[bin];

        double x = bins.XMin + (column + 0.5) / bins.Columns * (bins.XMax - bins.XMin);
        double y = bins.YMin + (row + 0.5) / bins.Rows * (bins.YMax - bins.YMin);

        string where = $"{plot.X.Name} {Axis(x)} · {plot.Y.Name} {Axis(y)}";

        if (count == 0)
        {
            dc.DrawText(Label($"{where} — nothing here", 11, TitleInk), Caption(area));
            return;
        }

        string detail = $"{where} — {plot.Z.Name} {plot.Z.Format(bins.Means[bin])} "
                        + $"from {count:N0} sample{(count == 1 ? "" : "s")}";

        // The spread is what a mean hides, so it is said rather than left to be
        // inferred from a colour that looks perfectly settled.
        double spread = bins.SpreadIn(column, row);
        if (count > 1 && spread > 0)
            detail += $", spanning {plot.Z.Format(bins.Lowest[bin])} to {plot.Z.Format(bins.Highest[bin])}";

        dc.DrawText(Label(detail, 11, TitleInk), Caption(area));
    }

    private Point Caption(Rect area) => new(area.X, ActualHeight - 16);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        (int, int) hit = BlockAt(e.GetPosition(this));
        if (hit == _hover) return;

        _hover = hit;
        InvalidateVisual();
    }

    /// <summary>
    /// The block under a point.
    ///
    /// The nearest occupied block within a small radius wins over the one
    /// literally under the pointer: a mark is three pixels across, and asking
    /// somebody to land on it exactly to read it would make the readout
    /// unreachable for most of the data.
    /// </summary>
    private (int Column, int Row) BlockAt(Point p)
    {
        if (_bins is not { } bins) return (-1, -1);

        Rect area = PlotArea;
        if (!area.Contains(p)) return (-1, -1);

        int column = (int)((p.X - area.X) / area.Width * bins.Columns);
        int row = (int)((area.Bottom - p.Y) / area.Height * bins.Rows);

        column = Math.Clamp(column, 0, bins.Columns - 1);
        row = Math.Clamp(row, 0, bins.Rows - 1);

        if (bins.Counts[bins.Index(column, row)] > 0) return (column, row);

        const int radius = 2;
        int best = 0;
        (int Column, int Row) found = (column, row);

        for (int dr = -radius; dr <= radius; dr++)
        for (int dc = -radius; dc <= radius; dc++)
        {
            int c = column + dc, r = row + dr;
            if (c < 0 || c >= bins.Columns || r < 0 || r >= bins.Rows) continue;

            int count = bins.Counts[bins.Index(c, r)];
            if (count <= best) continue;

            best = count;
            found = (c, r);
        }

        return found;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        (int column, int row) = _hover;
        if (_bins is not { } bins || column < 0 || row < 0) return;
        if (bins.Counts[bins.Index(column, row)] == 0) return;

        BlockActivated?.Invoke((column, row));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == (-1, -1)) return;

        _hover = (-1, -1);
        InvalidateVisual();
    }

    private static string Axis(double value) =>
        Math.Abs(value) >= 100 ? value.ToString("N0") : value.ToString("0.##");

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
