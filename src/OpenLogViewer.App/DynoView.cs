using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>One curve to draw, and what to call it.</summary>
/// <param name="Curve">The figures.</param>
/// <param name="Label">What the legend says.</param>
/// <param name="Ink">Which of the theme's series colours it takes.</param>
public sealed record DynoTrace(DynoCurve Curve, string Label, int Ink);

/// <summary>
/// A dyno sheet: power and torque against engine speed, drawn.
///
/// <para>
/// <b>Both on one scale, which is right here and would be wrong almost
/// anywhere else.</b> Two measures of different size normally want two charts,
/// because a second axis lets a drawing imply any relationship the author
/// likes. These two are not independent: torque in pound-feet is power in
/// horsepower times 5,252 over engine speed, so they are the same measurement
/// twice and they cross at exactly 5,252 rpm on every sheet ever printed.
/// Sharing the axis is what makes that crossing visible, and the crossing is a
/// free check on the arithmetic behind the lines.
/// </para>
/// <para>
/// Several routes to the figure can be laid over one another, which is the
/// point of having more than one. Where they lie together the number is worth
/// believing; where they part, the shape of the parting says which input is
/// wrong.
/// </para>
/// </summary>
public sealed class DynoView : FrameworkElement
{
    private const double LeftGutter = 54;
    private const double BottomGutter = 30;
    private const double TopPad = 18;

    /// <summary>
    /// Room down the right for the legend, which is where the names of the routes
    /// live. Wide enough for the longest of them — a name that runs off the edge
    /// is worse than no legend at all, since the colour it belongs to is still
    /// on the chart with nothing to say what it is.
    /// </summary>
    private const double RightPad = 156;

    private IReadOnlyList<DynoTrace> _traces = [];
    private double _cursorRpm = double.NaN;

    private Brush _background = Brushes.Transparent;
    private Brush _axisInk = Brushes.Gray;
    private Brush _titleInk = Brushes.Black;
    private Brush _panel = Brushes.White;
    private Pen _gridPen = new(Brushes.Gray, 1);
    private Pen _crossPen = new(Brushes.Gray, 1);
    private Pen _cursorPen = new(Brushes.Gray, 1);
    private Color[] _series = [Colors.SteelBlue];

    public DynoView()
    {
        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;

        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
    }

    /// <summary>Engine speed the pointer is over, for a caller to read out.</summary>
    public event Action<double>? CursorMoved;

    /// <summary>What to draw. Empty clears it.</summary>
    public void Show(IReadOnlyList<DynoTrace> traces)
    {
        _traces = traces ?? [];
        InvalidateVisual();
    }

    private void ApplyTheme(Theme theme)
    {
        _background = Fill(theme.Background);
        _axisInk = Fill(theme.Muted);
        _titleInk = Fill(theme.Text);
        _panel = Fill(theme.Panel);
        _series = theme.Series;

        _gridPen = Frozen(new Pen(Fill(theme.Grid), 1));
        _cursorPen = Frozen(new Pen(Fill(theme.Cursor), 1));

        _crossPen = Frozen(new Pen(Fill(theme.Faint), 1)
        {
            DashStyle = new DashStyle([2, 4], 0),
        });

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        dc.DrawRectangle(_background, null, new Rect(RenderSize));

        double w = ActualWidth;
        double h = ActualHeight;

        if (w < 160 || h < 120) return;

        DynoTrace[] drawn = [.. _traces.Where(t => !t.Curve.IsEmpty)];

        if (drawn.Length == 0)
        {
            Label(dc, "No pull is drawn.", LeftGutter, h / 2, title: true);

            return;
        }

        double fromRpm = drawn.Min(t => t.Curve.FromRpm);
        double toRpm = drawn.Max(t => t.Curve.ToRpm);

        if (!(toRpm > fromRpm)) return;

        double top = drawn.Max(t => t.Curve.Drawn.Max(p => Math.Max(p.Horsepower, p.PoundFeet)));
        top = Math.Max(50, (Math.Ceiling(top / 50) * 50) + 50);

        double plotW = w - LeftGutter - RightPad;
        double plotH = h - TopPad - BottomGutter;

        double X(double rpm) => LeftGutter + ((rpm - fromRpm) / (toRpm - fromRpm) * plotW);
        double Y(double v) => TopPad + plotH - (v / top * plotH);

        // ----- the grid, and the values it is labelled with -----------------------
        double step = top > 500 ? 100 : 50;

        for (double v = 0; v <= top + 0.1; v += step)
        {
            dc.DrawLine(_gridPen, new Point(LeftGutter, Y(v)), new Point(w - RightPad, Y(v)));
            Label(dc, v.ToString("N0", CultureInfo.CurrentCulture), LeftGutter - 7, Y(v), right: true);
        }

        double rpmStep = toRpm - fromRpm > 3000 ? 500 : 250;

        for (double r = Math.Ceiling(fromRpm / rpmStep) * rpmStep; r <= toRpm; r += rpmStep)
        {
            dc.DrawLine(_gridPen, new Point(X(r), TopPad), new Point(X(r), TopPad + plotH));
            Label(dc, (r / 1000).ToString("0.0", CultureInfo.CurrentCulture) + "k",
                  X(r), TopPad + plotH + 12, centre: true);
        }

        // Where power and torque must cross, which is arithmetic rather than
        // opinion and is therefore worth marking.
        if (RoadLoad.TorqueConstant > fromRpm && RoadLoad.TorqueConstant < toRpm)
        {
            double x = X(RoadLoad.TorqueConstant);

            dc.DrawLine(_crossPen, new Point(x, TopPad), new Point(x, TopPad + plotH));
            Label(dc, "5252 · hp = lb-ft", x + 5, TopPad + 8);
        }

        Label(dc, "whp — solid · lb-ft — dashed", LeftGutter, TopPad - 8, title: true);

        // ----- the curves ----------------------------------------------------------
        for (int i = 0; i < drawn.Length; i++)
        {
            DynoTrace trace = drawn[i];
            Color ink = _series[trace.Ink % _series.Length];

            Draw(dc, trace.Curve, p => p.Horsepower, X, Y, ink, false);
            Draw(dc, trace.Curve, p => p.PoundFeet, X, Y, ink, true);

            // The peak, on the first trace only: three sets of labels on one
            // drawing is a thicket, and the first is the one being read.
            if (i != 0) continue;

            Peak(dc, trace.Curve.PeakPower, X, Y, ink, "whp", w);
            Peak(dc, trace.Curve.PeakTorque, X, Y, ink, "lb-ft", w);
        }

        // ----- the legend, so identity is never colour alone ------------------------
        double legendY = TopPad + 6;

        foreach (DynoTrace trace in drawn)
        {
            Color ink = _series[trace.Ink % _series.Length];

            dc.DrawRectangle(Fill(ink), null, new Rect(w - RightPad + 8, legendY, 9, 9));
            Label(dc, trace.Label, w - RightPad + 22, legendY + 5);

            legendY += 16;
        }

        // ----- where the pointer is -------------------------------------------------
        if (!double.IsFinite(_cursorRpm) || _cursorRpm < fromRpm || _cursorRpm > toRpm) return;

        double cx = X(_cursorRpm);

        dc.DrawLine(_cursorPen, new Point(cx, TopPad), new Point(cx, TopPad + plotH));

        foreach (DynoTrace trace in drawn)
        {
            double hp = trace.Curve.HorsepowerAt(_cursorRpm);

            if (!double.IsFinite(hp)) continue;

            dc.DrawEllipse(
                Fill(_series[trace.Ink % _series.Length]), Frozen(new Pen(_panel, 1.5)),
                new Point(cx, Y(hp)), 3.5, 3.5);
        }
    }

    private void Draw(
        DrawingContext dc, DynoCurve curve, Func<DynoPoint, double> of,
        Func<double, double> x, Func<double, double> y, Color ink, bool dashed)
    {
        DynoPoint[] points = [.. curve.Drawn];

        if (points.Length < 2) return;

        var figure = new PathFigure { StartPoint = new Point(x(points[0].Rpm), y(of(points[0]))) };

        for (int i = 1; i < points.Length; i++)
        {
            figure.Segments.Add(new LineSegment(
                new Point(x(points[i].Rpm), y(of(points[i]))), true));
        }

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();

        var pen = new Pen(Fill(ink), 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        if (dashed) pen.DashStyle = new DashStyle([3.5, 2], 0);

        // A ring of the panel colour under each line, so where two cross the one
        // in front stays legible instead of blending into a third colour.
        dc.DrawGeometry(null, Frozen(new Pen(_panel, 5) { LineJoin = PenLineJoin.Round }), geometry);
        dc.DrawGeometry(null, Frozen(pen), geometry);
    }

    private void Peak(
        DrawingContext dc, DynoPoint peak, Func<double, double> x, Func<double, double> y,
        Color ink, string units, double width)
    {
        if (peak.IsEmpty) return;

        double px = x(peak.Rpm);
        double py = y(units == "whp" ? peak.Horsepower : peak.PoundFeet);

        dc.DrawEllipse(Fill(ink), Frozen(new Pen(_panel, 2)), new Point(px, py), 4.5, 4.5);

        bool right = px > width - RightPad - 96;
        double lx = right ? px - 9 : px + 9;

        // Power above its peak and torque below it, so the torque label does not
        // land on the plateau it is labelling.
        double ly = units == "whp" ? py - 16 : py + 16;

        Label(dc,
            $"{(units == "whp" ? peak.Horsepower : peak.PoundFeet):N0} {units}  @ {peak.Rpm:N0}",
            lx, ly, right: right, title: true);
    }

    // ----- the pointer -------------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        DynoTrace[] drawn = [.. _traces.Where(t => !t.Curve.IsEmpty)];

        if (drawn.Length == 0) return;

        double fromRpm = drawn.Min(t => t.Curve.FromRpm);
        double toRpm = drawn.Max(t => t.Curve.ToRpm);
        double plotW = ActualWidth - LeftGutter - RightPad;

        if (!(plotW > 0) || !(toRpm > fromRpm)) return;

        double share = Math.Clamp((e.GetPosition(this).X - LeftGutter) / plotW, 0, 1);

        _cursorRpm = fromRpm + (share * (toRpm - fromRpm));

        CursorMoved?.Invoke(_cursorRpm);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        _cursorRpm = double.NaN;

        CursorMoved?.Invoke(double.NaN);
        InvalidateVisual();
    }

    // ----- odds and ends ------------------------------------------------------------

    private void Label(
        DrawingContext dc, string text, double x, double y,
        bool right = false, bool centre = false, bool title = false)
    {
        var run = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, title ? _titleInk : _axisInk,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(run, new Point(
            right ? x - run.Width : centre ? x - (run.Width / 2) : x,
            y - (run.Height / 2)));
    }

    private static Brush Fill(Color c) => Frozen(new SolidColorBrush(c));

    private static T Frozen<T>(T thing) where T : Freezable
    {
        thing.Freeze();

        return thing;
    }
}
