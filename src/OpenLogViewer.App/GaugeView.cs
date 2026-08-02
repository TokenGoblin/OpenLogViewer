using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// One dial, drawn to the firmware's own description of it.
///
/// Everything on the face — the span, where the numbers turn amber and where
/// they turn red, how many decimals to show — comes from the INI rather than
/// from anything decided here. A gauge whose scale was guessed at is worse than
/// no gauge, because it reads as a measurement.
///
/// Drawn rather than composed from shapes: there is one of these per channel and
/// a live session repaints them several times a second, so each is a handful of
/// arcs and a line rather than a few dozen elements the layout system has to
/// walk.
/// </summary>
public sealed class GaugeView : FrameworkElement
{
    /// <summary>
    /// Where the dial starts and stops, in degrees clockwise from the top.
    ///
    /// A 270° sweep with the gap at the bottom is the shape every car has used
    /// since dials were mechanical; the value sits in the gap.
    /// </summary>
    private const double StartAngle = -135;

    private const double SweepAngle = 270;

    /// <summary>
    /// Dependency properties rather than plain ones: a dial is created per item
    /// by a template, so both of these are set by binding and nothing else.
    /// </summary>
    public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(
        nameof(Spec), typeof(GaugeSpec), typeof(GaugeView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GaugeView),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(GaugeView),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TroughProperty = DependencyProperty.Register(
        nameof(Trough), typeof(double), typeof(GaugeView),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private Brush _face = null!;
    private Brush _ink = null!;
    private Brush _muted = null!;
    private Pen _track = null!;
    private Pen _normalBand = null!;
    private Pen _warningBand = null!;
    private Pen _dangerBand = null!;
    private Pen _needle = null!;
    private Pen _hold = null!;
    private Pen _warningHold = null!;
    private Pen _dangerHold = null!;
    private Brush _warningInk = null!;
    private Brush _dangerInk = null!;
    private Typeface _typeface = null!;

    public GaugeView()
    {
        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
    }

    public GaugeSpec? Spec
    {
        get => (GaugeSpec?)GetValue(SpecProperty);
        set => SetValue(SpecProperty, value);
    }

    /// <summary>The reading. NaN shows the dial with no needle rather than a zero.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Highest reading so far, marked on the face. NaN hides the marker.</summary>
    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    /// <summary>Lowest reading so far.</summary>
    public double Trough
    {
        get => (double)GetValue(TroughProperty);
        set => SetValue(TroughProperty, value);
    }

    private void ApplyTheme(Theme theme)
    {
        _face = Fill(theme.Card);
        _ink = Fill(theme.Text);
        _muted = Fill(theme.Muted);
        _warningInk = Fill(theme.Warning);
        _dangerInk = Fill(theme.Danger);

        _track = Pen(theme.Grid, 7);
        _normalBand = Pen(theme.Nominal, 7);
        _warningBand = Pen(theme.Warning, 7);
        _dangerBand = Pen(theme.Danger, 7);
        _needle = Pen(theme.Text, 2.5);

        _hold = Pen(theme.Muted, 2);
        _warningHold = Pen(theme.Warning, 2);
        _dangerHold = Pen(theme.Danger, 2);

        _typeface = new Typeface(
            new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        InvalidateVisual();
    }

    private static Brush Fill(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Color c, double thickness)
    {
        var pen = new Pen(Fill(c), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Spec is not { } spec) return;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 40 || bounds.Height < 40) return;

        dc.DrawRoundedRectangle(_face, null, bounds, 8, 8);

        // The face is square and centred; the title sits above it and the
        // reading inside the gap at the bottom.
        double size = Math.Min(bounds.Width, bounds.Height - 18);
        var centre = new Point(bounds.Width / 2, 18 + size / 2);

        // Room for the end labels, which sit outside the track.
        double radius = size / 2 - 22;

        DrawTitle(dc, spec, bounds);

        if (radius > 10 && spec.HasScale)
        {
            DrawFace(dc, spec, centre, radius);
            DrawExtreme(dc, spec, centre, radius, Peak);
            DrawExtreme(dc, spec, centre, radius, Trough);
            DrawNeedle(dc, spec, centre, radius);
        }

        DrawReading(dc, spec, centre, radius);
        DrawExtremeReadings(dc, spec, bounds);
    }

    private void DrawTitle(DrawingContext dc, GaugeSpec spec, Rect bounds)
    {
        FormattedText title = Text(spec.Title, 11, _muted);
        title.MaxTextWidth = Math.Max(20, bounds.Width - 8);
        title.MaxLineCount = 1;
        title.Trimming = TextTrimming.CharacterEllipsis;

        dc.DrawText(title, new Point((bounds.Width - title.Width) / 2, 3));
    }

    /// <summary>
    /// The track and its bands.
    ///
    /// Bands are drawn over the track rather than instead of it, so a gauge whose
    /// limits sit outside its own range — which happens, the firmware writes
    /// ±999 to mean "never" — still shows a complete dial.
    /// </summary>
    private void DrawFace(DrawingContext dc, GaugeSpec spec, Point centre, double radius)
    {
        dc.DrawGeometry(null, _track, Arc(centre, radius, 0, 1));

        if (spec.HasBands)
        {
            double lowDanger = spec.Fraction(spec.LowDanger);
            double lowWarning = spec.Fraction(spec.LowWarning);
            double highWarning = spec.Fraction(spec.HighWarning);
            double highDanger = spec.Fraction(spec.HighDanger);

            Band(dc, centre, radius, lowWarning, highWarning, _normalBand);
            Band(dc, centre, radius, lowDanger, lowWarning, _warningBand);
            Band(dc, centre, radius, highWarning, highDanger, _warningBand);
            Band(dc, centre, radius, 0, lowDanger, _dangerBand);
            Band(dc, centre, radius, highDanger, 1, _dangerBand);
        }
        else
        {
            Band(dc, centre, radius, 0, 1, _normalBand);
        }

        // The ends of the scale, so the dial can be read without the number.
        //
        // Outside the track rather than inside it: the reading sits in the mouth
        // of the dial, which is exactly where the two end labels would otherwise
        // be, and three numbers on top of each other is worse than none.
        string low = spec.Low.ToString($"F{spec.LabelDigits}", CultureInfo.CurrentCulture);
        string high = spec.High.ToString($"F{spec.LabelDigits}", CultureInfo.CurrentCulture);

        FormattedText lowText = Text(low, 9, _muted);
        FormattedText highText = Text(high, 9, _muted);

        double out0 = radius + 8;
        dc.DrawText(lowText, Offset(At(centre, out0, 0), -lowText.Width, -lowText.Height / 2));
        dc.DrawText(highText, Offset(At(centre, out0, 1), 0, -highText.Height / 2));
    }

    private static void Band(DrawingContext dc, Point centre, double radius, double from, double to, Pen pen)
    {
        if (to - from < 0.004) return;

        dc.DrawGeometry(null, pen, Arc(centre, radius, Math.Max(0, from), Math.Min(1, to)));
    }

    /// <summary>
    /// A tick left behind at an extreme, the way a max-hold needle would sit.
    ///
    /// Outside the track rather than on it, so it neither hides a band nor gets
    /// hidden by the needle passing over it.
    /// </summary>
    private void DrawExtreme(DrawingContext dc, GaugeSpec spec, Point centre, double radius, double value)
    {
        if (double.IsNaN(value)) return;

        double at = spec.Fraction(value);

        Pen pen = spec.BandFor(value) switch
        {
            GaugeBand.Danger => _dangerHold,
            GaugeBand.Warning => _warningHold,
            _ => _hold,
        };

        dc.DrawLine(pen, At(centre, radius + 1, at), At(centre, radius + 6, at));
    }

    /// <summary>
    /// The two extremes in figures, under the title.
    ///
    /// A marker on the face says where, but not what — and a peak is usually
    /// wanted as a number, since the point of holding it is that the moment has
    /// passed.
    /// </summary>
    private void DrawExtremeReadings(DrawingContext dc, GaugeSpec spec, Rect bounds)
    {
        if (double.IsNaN(Peak) && double.IsNaN(Trough)) return;

        string format = $"F{spec.ValueDigits}";
        string low = double.IsNaN(Trough) ? "—" : Trough.ToString(format, CultureInfo.CurrentCulture);
        string high = double.IsNaN(Peak) ? "—" : Peak.ToString(format, CultureInfo.CurrentCulture);

        FormattedText text = Text($"▾{low}   ▴{high}", 9, _muted);
        text.MaxTextWidth = Math.Max(20, bounds.Width - 6);
        text.MaxLineCount = 1;

        dc.DrawText(text, new Point((bounds.Width - text.Width) / 2, bounds.Height - text.Height - 2));
    }

    private void DrawNeedle(DrawingContext dc, GaugeSpec spec, Point centre, double radius)
    {
        double value = Value;
        if (double.IsNaN(value)) return;

        double at = spec.Fraction(value);

        dc.DrawLine(_needle, At(centre, radius * 0.18, at), At(centre, radius * 0.92, at));
        dc.DrawEllipse(_ink, null, centre, 3, 3);
    }

    /// <summary>The reading itself, coloured by the band it falls in.</summary>
    private void DrawReading(DrawingContext dc, GaugeSpec spec, Point centre, double radius)
    {
        double value = Value;

        Brush ink = spec.BandFor(value) switch
        {
            GaugeBand.Danger => _dangerInk,
            GaugeBand.Warning => _warningInk,
            _ => _ink,
        };

        string reading = double.IsNaN(value)
            ? "—"
            : value.ToString($"F{spec.ValueDigits}", CultureInfo.CurrentCulture);

        double scale = spec.HasScale ? 1 : 1.4;
        FormattedText text = Text(reading, Math.Max(13, radius * 0.34 * scale), ink);

        // In the gap at the bottom of a dial, or in the middle of a face that has
        // no dial to sit inside.
        double y = spec.HasScale ? centre.Y + radius * 0.42 : centre.Y - text.Height / 2;
        dc.DrawText(text, new Point(centre.X - text.Width / 2, y));

        if (spec.Units.Length == 0) return;

        FormattedText units = Text(spec.Units, 9.5, _muted);
        dc.DrawText(units, new Point(centre.X - units.Width / 2, y + text.Height - 2));
    }

    /// <summary>An arc of the dial between two positions along it, 0 to 1.</summary>
    private static Geometry Arc(Point centre, double radius, double from, double to)
    {
        Point start = At(centre, radius, from);
        Point end = At(centre, radius, to);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = (to - from) * SweepAngle > 180,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return geometry;
    }

    /// <summary>A point on the dial at a given radius and position along the sweep.</summary>
    private static Point At(Point centre, double radius, double fraction)
    {
        double radians = (StartAngle + fraction * SweepAngle - 90) * Math.PI / 180;

        return new Point(centre.X + radius * Math.Cos(radians), centre.Y + radius * Math.Sin(radians));
    }

    private static Point Offset(Point p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    private FormattedText Text(string text, double size, Brush ink) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, size, ink,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
