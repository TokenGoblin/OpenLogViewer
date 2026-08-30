using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// Draws a <see cref="TuneCurveEdit"/> as the thing the format calls it: a line
/// you drag.
///
/// <para>
/// The firmware describes these as curves rather than as tables because that is
/// how they are read and how they are changed — a warmup line that is lumpy in
/// the middle is a stumble at one coolant temperature, and it is the shape that
/// says so, not the numbers. A grid of ten cells would be easier to build and
/// would hide exactly what a person opens this to see.
/// </para>
/// <para>
/// Dragging moves the value. Holding shift moves the breakpoint instead, which
/// is deliberately the harder gesture: the values are what tuning changes, and a
/// breakpoint that wanders is a change to what every value on the line means.
/// </para>
/// </summary>
public sealed class CurveView : FrameworkElement
{
    private const double LeftGutter = 62;
    private const double BottomGutter = 34;
    private const double TopPad = 16;
    private const double RightPad = 18;
    private const double Grab = 9;

    private Brush Background = null!;
    private Brush AxisInk = null!;
    private Brush TitleInk = null!;
    private Brush PointFill = null!;
    private Brush MovedFill = null!;
    private Pen GridPen = null!;
    private Pen LinePen = null!;
    private Pen OriginalPen = null!;
    private Pen PointPen = null!;
    private Pen HoverPen = null!;

    private TuneCurveEdit? _curve;
    private int _hover = -1;
    private int _dragging = -1;
    private bool _draggingX;

    /// <summary>
    /// The axes as they were when a drag began, held for the length of it.
    ///
    /// <b>Without this the mapping moves under the drag that is changing it.</b>
    /// The range is derived from the values, so dragging the topmost point
    /// upward raises the top of the plot, which lowers where the pointer lands,
    /// which raises the value again — the point lags the pointer while the
    /// number climbs about a tenth on every mouse move. On a curve whose values
    /// declare no range there is nothing to stop it, and that number is what
    /// Send writes to a running engine.
    /// </summary>
    private (double Low, double High)? _heldX;

    private (double Low, double High)? _heldY;

    public CurveView()
    {
        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;

        // Let go when the element does. The event is static, so without this it
        // holds a detached view alive for the life of the process and re-renders
        // it on every theme change — which every other view here already avoids.
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;

        Focusable = true;
    }

    /// <summary>
    /// Raised after a drag has moved something, so the header keeps up.
    ///
    /// A routed event rather than a plain one, because these are made by a
    /// template now — a page may hold several curves — and there is nothing to
    /// attach a handler to one by one.
    /// </summary>
    public static readonly RoutedEvent EditedEvent = EventManager.RegisterRoutedEvent(
        nameof(Edited), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CurveView));

    public event RoutedEventHandler Edited
    {
        add => AddHandler(EditedEvent, value);
        remove => RemoveHandler(EditedEvent, value);
    }

    private void RaiseEdited() => RaiseEvent(new RoutedEventArgs(EditedEvent, this));

    /// <summary>The curve being changed, bound from the view model.</summary>
    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve), typeof(TuneCurveEdit), typeof(CurveView),
        new FrameworkPropertyMetadata(
            null, FrameworkPropertyMetadataOptions.AffectsRender, OnCurveChanged));

    public TuneCurveEdit? Curve
    {
        get => (TuneCurveEdit?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    private static void OnCurveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CurveView view) return;

        // A different curve means nothing is under the pointer and nothing is
        // being dragged, whatever was a moment ago.
        view._curve = (TuneCurveEdit?)e.NewValue;
        view._hover = -1;
        view._dragging = -1;
        view._heldX = null;
        view._heldY = null;

        if (view.IsMouseCaptured) view.ReleaseMouseCapture();

        view.InvalidateVisual();
    }

    private void ApplyTheme(Theme theme)
    {
        Background = Fill(theme.Background);
        AxisInk = Fill(theme.Muted);
        TitleInk = Fill(theme.Text);
        PointFill = Fill(theme.Background);
        MovedFill = Fill(theme.Accent);

        GridPen = Frozen(new Pen(Fill(theme.Grid), 1));
        LinePen = Frozen(new Pen(Fill(theme.Accent), 2));
        PointPen = Frozen(new Pen(Fill(theme.Accent), 1.6));
        HoverPen = Frozen(new Pen(Fill(theme.Text), 2));

        // What the ECU holds, behind what it would become. Dashed and faint so
        // it reads as the ground the edit is being made against rather than as a
        // second curve competing with it.
        OriginalPen = Frozen(new Pen(Fill(theme.Muted), 1)
        {
            DashStyle = new DashStyle([4, 4], 0),
        });

        InvalidateVisual();
    }

    private static Brush Fill(Color c) => Frozen(new SolidColorBrush(c));

    private static T Frozen<T>(T thing) where T : Freezable
    {
        thing.Freeze();
        return thing;
    }

    // ----- the plot's geometry ------------------------------------------------

    private Rect Plot => new(
        LeftGutter, TopPad,
        Math.Max(1, ActualWidth - LeftGutter - RightPad),
        Math.Max(1, ActualHeight - TopPad - BottomGutter));

    /// <summary>
    /// What the axes span.
    ///
    /// The firmware's own range where it declares one, so the same curve is
    /// drawn the same way whatever happens to be in it — a warmup line does not
    /// change shape because its top point was lowered. Falling back to what the
    /// points cover, padded, when it declares nothing.
    /// </summary>
    private (double Low, double High) Span(bool horizontal)
    {
        if (horizontal && _heldX is { } heldX) return heldX;
        if (!horizontal && _heldY is { } heldY) return heldY;

        if (_curve is not { } curve) return (0, 1);

        TuneConstant? constant = horizontal ? curve.XConstant : curve.YConstant;

        if (constant is { HasRange: true } c && c.High > c.Low)
        {
            // Held to what the points actually use where the declared range is
            // far wider — a pin list of a range would squash every curve on it
            // into one line across the bottom.
            double lowest = Enumerable.Range(0, curve.Count)
                .Min(i => horizontal ? curve.X(i) : curve.Y(i));
            double highest = Enumerable.Range(0, curve.Count)
                .Max(i => horizontal ? curve.X(i) : curve.Y(i));

            double used = highest - lowest;
            double declared = c.High - c.Low;

            if (used > 0 && declared > used * 4)
            {
                double pad = used * 0.15;
                return (Math.Max(c.Low, lowest - pad), Math.Min(c.High, highest + pad));
            }

            return (c.Low, c.High);
        }

        double low = double.MaxValue, high = double.MinValue;

        for (int i = 0; i < curve.Count; i++)
        {
            double v = horizontal ? curve.X(i) : curve.Y(i);
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        if (low > high) return (0, 1);
        if (Math.Abs(high - low) < 1e-9) return (low - 1, high + 1);

        double margin = (high - low) * 0.1;
        return (low - margin, high + margin);
    }

    private Point Where(double x, double y)
    {
        (double xLow, double xHigh) = Span(horizontal: true);
        (double yLow, double yHigh) = Span(horizontal: false);

        Rect plot = Plot;

        double across = xHigh > xLow ? (x - xLow) / (xHigh - xLow) : 0.5;
        double up = yHigh > yLow ? (y - yLow) / (yHigh - yLow) : 0.5;

        return new Point(
            plot.Left + (Math.Clamp(across, 0, 1) * plot.Width),
            plot.Bottom - (Math.Clamp(up, 0, 1) * plot.Height));
    }

    /// <summary>The value a pointer at this height stands for.</summary>
    private double ValueAt(double screenY)
    {
        (double low, double high) = Span(horizontal: false);
        Rect plot = Plot;

        double up = plot.Height > 0 ? (plot.Bottom - screenY) / plot.Height : 0;
        return low + (Math.Clamp(up, 0, 1) * (high - low));
    }

    private double BreakpointAt(double screenX)
    {
        (double low, double high) = Span(horizontal: true);
        Rect plot = Plot;

        double across = plot.Width > 0 ? (screenX - plot.Left) / plot.Width : 0;
        return low + (Math.Clamp(across, 0, 1) * (high - low));
    }

    // ----- drawing ------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_curve is not { Count: > 0 } curve) return;

        Rect plot = Plot;
        if (plot.Width < 20 || plot.Height < 20) return;

        DrawGrid(dc, curve, plot);

        // What the ECU holds, where it differs from what is on screen.
        if (curve.HasChanges)
        {
            var was = new PathFigure { StartPoint = Where(curve.OriginalX(0), curve.OriginalY(0)) };
            for (int i = 1; i < curve.Count; i++)
                was.Segments.Add(new LineSegment(Where(curve.OriginalX(i), curve.OriginalY(i)), true));

            dc.DrawGeometry(null, OriginalPen, Frozen(new PathGeometry([was])));
        }

        var now = new PathFigure { StartPoint = Where(curve.X(0), curve.Y(0)) };
        for (int i = 1; i < curve.Count; i++)
            now.Segments.Add(new LineSegment(Where(curve.X(i), curve.Y(i)), true));

        dc.DrawGeometry(null, LinePen, Frozen(new PathGeometry([now])));

        for (int i = 0; i < curve.Count; i++)
        {
            Point at = Where(curve.X(i), curve.Y(i));
            bool moved = curve.IsChanged(i);

            dc.DrawEllipse(
                moved ? MovedFill : PointFill,
                i == _hover || i == _dragging ? HoverPen : PointPen,
                at, 4.5, 4.5);
        }

        if (_hover >= 0 && _hover < curve.Count) DrawReadout(dc, curve, plot, _hover);
    }

    private void DrawGrid(DrawingContext dc, TuneCurveEdit curve, Rect plot)
    {
        (double xLow, double xHigh) = Span(horizontal: true);
        (double yLow, double yHigh) = Span(horizontal: false);

        dc.DrawRectangle(null, GridPen, plot);

        // Four lines each way, which is enough to read a value off and few
        // enough not to become the picture.
        for (int i = 1; i < 4; i++)
        {
            double y = plot.Top + (plot.Height * i / 4.0);
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            double value = yHigh - ((yHigh - yLow) * i / 4.0);
            Label(dc, Number(value, curve.YDigits), LeftGutter - 6, y, right: true);
        }

        Label(dc, Number(yHigh, curve.YDigits), LeftGutter - 6, plot.Top, right: true);
        Label(dc, Number(yLow, curve.YDigits), LeftGutter - 6, plot.Bottom, right: true);

        for (int i = 1; i < 4; i++)
        {
            double x = plot.Left + (plot.Width * i / 4.0);
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            Label(dc, Number(xLow + ((xHigh - xLow) * i / 4.0), curve.XDigits),
                  x, plot.Bottom + 14, centre: true);
        }

        Label(dc, Number(xLow, curve.XDigits), plot.Left, plot.Bottom + 14, centre: true);
        Label(dc, Number(xHigh, curve.XDigits), plot.Right, plot.Bottom + 14, centre: true);

        string across = curve.XUnits.Length > 0 ? $"{curve.XLabel} ({curve.XUnits})" : curve.XLabel;
        string up = curve.YUnits.Length > 0 ? $"{curve.YLabel} ({curve.YUnits})" : curve.YLabel;

        Label(dc, across, plot.Left + (plot.Width / 2), plot.Bottom + 28, centre: true, title: true);

        // From the left edge rather than ending at the gutter: right-aligned, a
        // name any longer than the gutter is wide starts at a negative x and its
        // first characters are simply not drawn.
        Label(dc, up, 2, plot.Top - 10, title: true);
    }

    /// <summary>The point under the pointer, spelled out where it cannot be misread.</summary>
    private void DrawReadout(DrawingContext dc, TuneCurveEdit curve, Rect plot, int index)
    {
        string text = $"{Number(curve.X(index), curve.XDigits)} {curve.XUnits}"
                      + $"  →  {Number(curve.Y(index), curve.YDigits)} {curve.YUnits}";

        if (curve.IsChanged(index))
            text += $"   (was {Number(curve.OriginalY(index), curve.YDigits)})";

        Label(dc, text, plot.Left + 8, plot.Top + 4, title: true);
    }

    private void Label(
        DrawingContext dc, string text, double x, double y,
        bool right = false, bool centre = false, bool title = false)
    {
        var run = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, title ? TitleInk : AxisInk,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(run, new Point(
            right ? x - run.Width : centre ? x - (run.Width / 2) : x,
            y - (run.Height / 2)));
    }

    private static string Number(double value, int digits) =>
        value.ToString($"F{Math.Clamp(digits, 0, 3)}", CultureInfo.CurrentCulture);

    // ----- dragging -----------------------------------------------------------

    /// <summary>The point nearest the pointer, or −1 when none is near enough.</summary>
    private int Nearest(Point pointer)
    {
        if (_curve is not { Count: > 0 } curve) return -1;

        int best = -1;
        double nearest = Grab * Grab;

        for (int i = 0; i < curve.Count; i++)
        {
            Point at = Where(curve.X(i), curve.Y(i));
            double dx = at.X - pointer.X, dy = at.Y - pointer.Y;
            double distance = (dx * dx) + (dy * dy);

            if (distance <= nearest)
            {
                nearest = distance;
                best = i;
            }
        }

        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_curve is not { } curve) return;

        Point pointer = e.GetPosition(this);

        if (_dragging >= 0)
        {
            if (_draggingX) curve.SetX(_dragging, BreakpointAt(pointer.X));
            else curve.SetY(_dragging, ValueAt(pointer.Y));

            InvalidateVisual();
            RaiseEdited();
            return;
        }

        int was = _hover;
        _hover = Nearest(pointer);

        Cursor = _hover >= 0 ? Cursors.SizeNS : Cursors.Arrow;

        if (_hover != was) InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_curve is null) return;

        _dragging = Nearest(e.GetPosition(this));
        if (_dragging < 0) return;

        // Shift moves the breakpoint rather than the value. The harder gesture
        // for the rarer and more consequential change: a breakpoint that moves
        // changes what every value on the line means.
        _draggingX = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        // Fixed for the length of the drag, so the pointer means the same value
        // at the end of it as at the start.
        _heldX = Span(horizontal: true);
        _heldY = Span(horizontal: false);

        Focus();
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_dragging < 0) return;

        _dragging = -1;
        _heldX = null;
        _heldY = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        RaiseEdited();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_dragging >= 0) return;

        _hover = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// The keyboard, for the times a mouse cannot say what is meant. A point is
    /// nudged by one step of what the ECU can actually store, so a press moves
    /// it as little as the hardware allows rather than by a made-up amount.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_curve is not { Count: > 0 } curve || _hover < 0) return;

        double step = curve.YConstant is { Scale: > 0 } c ? Math.Abs(c.Scale) : 1;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) step *= 10;

        switch (e.Key)
        {
            case Key.Up: curve.AddY(_hover, step); break;
            case Key.Down: curve.AddY(_hover, -step); break;
            case Key.Left when _hover > 0: _hover--; break;
            case Key.Right when _hover < curve.Count - 1: _hover++; break;
            default: return;
        }

        e.Handled = true;
        InvalidateVisual();
        RaiseEdited();
    }
}
