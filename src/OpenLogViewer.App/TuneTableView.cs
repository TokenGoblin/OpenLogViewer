using System.Globalization;
using System.Windows;
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

    private Color[] _ramp = [];
    private Brush _ground = null!;
    private Brush _ink = null!;
    private Brush _axisInk = null!;
    private Brush _darkInk = null!;
    private Brush _lightInk = null!;
    private Pen _grid = null!;
    private Typeface _typeface = null!;

    public TuneTableView()
    {
        ClipToBounds = true;

        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
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
        _typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        InvalidateVisual();
    }

    private static Brush Fill(Color c) => Frozen(new SolidColorBrush(c));

    private static T Frozen<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(_ground, null, full);

        if (Table is not { } table || table.Columns == 0 || table.Rows == 0) return;

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

                if (cellWidth < 26 || cellHeight < 12) continue;

                FormattedText text = Text(
                    value.ToString("0.#", CultureInfo.CurrentCulture),
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
