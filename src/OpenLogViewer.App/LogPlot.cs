using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// Time-series plot for datalog channels.
///
/// Traces are overlaid and each is scaled to its own range, which is how tuners
/// read logs: the shape and phase relationship between channels matters more
/// than a shared magnitude. Per-channel numbers are read off the cursor readout
/// rather than a shared Y axis.
/// </summary>
public sealed class LogPlot : FrameworkElement
{
    private const double AxisHeight = 26;
    private const double Pad = 8;

    // Rebuilt whenever the theme changes. This element paints itself rather than
    // going through the styling system, so it has to be told; DynamicResource
    // reaches the chrome around it but not the drawing below.
    private Brush BackgroundBrush = null!;
    private Brush AxisTextBrush = null!;
    private Pen GridPen = null!;
    private Pen CursorPen = null!;
    private Pen MarkerPen = null!;

    /// <summary>How close, in pixels, the pointer must be to claim a trace.</summary>
    private const double HoverTolerance = 40;

    private Brush CardBrush = null!;
    private Pen CardPen = null!;
    private Brush CardLabel = null!;
    private Brush CardValue = null!;
    private Brush CardAction = null!;
    private Brush CardRowHover = null!;

    /// <summary>Vertical gap between stacked lanes, in pixels.</summary>
    private const double LaneGap = 6;

    private Pen LanePen = null!;

    private Brush SelectionFill = null!;
    private Pen SelectionEdge = null!;
    private Brush OccurrenceFill = null!;

    private void ApplyTheme(Theme theme)
    {
        BackgroundBrush = Fill(theme.Background);
        AxisTextBrush = Fill(theme.Muted);
        GridPen = Stroke(theme.Grid, 1);
        CursorPen = Stroke(theme.Cursor, 1);
        MarkerPen = Frozen(new Pen(Fill(theme.Marker), 1) { DashStyle = new DashStyle([3, 3], 0) });

        // Held just off opaque so a trace running underneath the card is still
        // faintly readable, which is what tells you the card is floating.
        CardBrush = Fill(Color.FromArgb(0xF2, theme.Card.R, theme.Card.G, theme.Card.B));
        CardPen = Stroke(theme.Line, 1);
        CardLabel = Fill(theme.Muted);
        CardValue = Fill(theme.Text);
        CardAction = Fill(theme.Accent);
        CardRowHover = Fill(theme.Hover);

        LanePen = Stroke(theme.Lane, 1);

        SelectionFill = Fill(Color.FromArgb(0x38, theme.Accent.R, theme.Accent.G, theme.Accent.B));
        SelectionEdge = Stroke(theme.Accent, 1);
        OccurrenceFill = Fill(Color.FromArgb(0x33, theme.Marker.R, theme.Marker.G, theme.Marker.B));

        InvalidateVisual();
    }

    private static Brush Fill(Color c) => Frozen(new SolidColorBrush(c));

    private static Pen Stroke(Color c, double thickness) => Frozen(new Pen(Fill(c), thickness));

    private bool _stacked;

    private LogDocument? _document;
    private IReadOnlyList<ChannelItem> _series = [];
    private double _viewStart, _viewEnd;
    private double _cursorTime = double.NaN;
    private int _cursorIndex = -1;
    private ChannelItem? _hover;
    private Point _pointer;
    private Rect _maxHit = Rect.Empty;
    private Rect _minHit = Rect.Empty;
    private bool _panning;
    private Point _panAnchor;
    private double _panStart, _panEnd;

    private bool _selecting;
    private double _selectionAnchor = double.NaN;
    private double _selectionFrom = double.NaN;
    private double _selectionTo = double.NaN;

    /// <summary>True while a time range is marked on the plot.</summary>
    public bool HasSelection => !double.IsNaN(_selectionFrom) && _selectionTo > _selectionFrom;

    /// <summary>
    /// Raised as a range is marked or cleared, carrying the inclusive sample
    /// span, or null when the selection is dropped.
    /// </summary>
    public event Action<(int First, int Last)?>? SelectionChanged;

    private IReadOnlyList<(double From, double To)> _occurrences = [];

    /// <summary>
    /// Marks every span where some condition held — the other visits to a
    /// histogram cell, so the one being shown is seen in context rather than as
    /// if it were the only time the engine was there.
    /// </summary>
    public void SetOccurrences(IReadOnlyList<(double From, double To)> spans)
    {
        _occurrences = spans;
        InvalidateVisual();
    }

    /// <summary>Marks a span of the log, in seconds.</summary>
    public void SelectRange(double fromSeconds, double toSeconds)
    {
        if (_document is not { SampleCount: > 0 }) return;

        _selecting = false;
        _selectionAnchor = fromSeconds;
        _selectionFrom = Math.Min(fromSeconds, toSeconds);
        _selectionTo = Math.Max(fromSeconds, toSeconds);

        ReportSelection();
        InvalidateVisual();
    }

    /// <summary>Frames a span of the log, clamped to the data.</summary>
    public void ZoomTo(double fromSeconds, double toSeconds)
    {
        if (_document is not { SampleCount: > 0 } doc) return;

        _viewStart = Math.Min(fromSeconds, toSeconds);
        _viewEnd = Math.Max(fromSeconds, toSeconds);
        ClampView(doc);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        if (!HasSelection && !_selecting && _occurrences.Count == 0) return;

        _selecting = false;
        _selectionAnchor = _selectionFrom = _selectionTo = double.NaN;

        // The marked spans belong to the traced cell that produced this
        // selection; dropping one ends the other.
        _occurrences = [];

        SelectionChanged?.Invoke(null);
        InvalidateVisual();
    }

    private void ReportSelection()
    {
        if (_document is not { SampleCount: > 0 } doc) return;

        SelectionChanged?.Invoke(HasSelection
            ? (doc.IndexAtTime(_selectionFrom), doc.IndexAtTime(_selectionTo))
            : null);
    }

    public LogPlot()
    {
        ClipToBounds = true;
        Focusable = true;

        ApplyTheme(ThemeManager.Current);
        ThemeManager.Changed += ApplyTheme;
        Unloaded += (_, _) => ThemeManager.Changed -= ApplyTheme;
    }

    /// <summary>Raised as the cursor moves, carrying the sample index under it.</summary>
    public event Action<int>? CursorSampleChanged;

    /// <summary>Raised when the pointer moves onto or off a trace.</summary>
    public event Action<ChannelItem?>? HoverChannelChanged;

    /// <summary>
    /// Centres the view on a moment in the log without changing the zoom, and
    /// parks the cursor there. Used to jump to a channel's extremes.
    /// </summary>
    public void FocusTime(double time)
    {
        if (_document is not { SampleCount: > 0 } doc) return;

        double span = _viewEnd - _viewStart;
        _viewStart = time - span / 2;
        _viewEnd = time + span / 2;
        ClampView(doc);

        _cursorIndex = doc.IndexAtTime(time);
        _cursorTime = doc.Time.At(_cursorIndex);
        CursorSampleChanged?.Invoke(_cursorIndex);
        InvalidateVisual();
    }

    public double ViewStart => _viewStart;

    public double ViewEnd => _viewEnd;

    public void SetDocument(LogDocument? document, IReadOnlyList<ChannelItem> series)
    {
        _document = document;
        _series = series;

        // Selections and traced spans are times in the previous recording. Left
        // in place they would be drawn over the new log at meaningless offsets.
        _selecting = false;
        _selectionAnchor = _selectionFrom = _selectionTo = double.NaN;
        _occurrences = [];
        _hover = null;
        _cursorIndex = -1;
        _cursorTime = double.NaN;

        ResetView();
    }

    public void Refresh() => InvalidateVisual();

    /// <summary>
    /// Swaps in a longer version of the same recording, as a live session does
    /// every time it polls.
    ///
    /// Not <see cref="SetDocument"/>: that resets the view, which on a live
    /// session would yank the plot back to full extent several times a second
    /// and make it impossible to look at anything. The window keeps its width
    /// and, while <paramref name="follow"/> holds, slides to stay on the newest
    /// data — which stops as soon as the user zooms or pans, because at that
    /// point they are reading history rather than watching.
    /// </summary>
    /// <summary>
    /// Raised when the user zooms, pans or fits the view. A live session stops
    /// following the newest data at that point: they are reading history, and
    /// having the window slide out from under them is the worst of both.
    /// </summary>
    public event Action? ViewChangedByUser;

    public void ExtendDocument(LogDocument document, IReadOnlyList<ChannelItem> series, bool follow)
    {
        _document = document;

        // The series list has to come too. Without it the plot holds the rows
        // from whatever was open before — which for a session that started with
        // no file is nothing, and the plot draws an empty frame while the
        // sidebar shows five channels ticked.
        _series = series;

        if (document.SampleCount == 0) { InvalidateVisual(); return; }

        double oldest = document.Time.At(0);
        double newest = document.Time.At(document.SampleCount - 1);

        if (follow)
        {
            _viewEnd = newest;

            // A young session has less data than the window is wide; showing the
            // empty minute before it started is just blank space.
            _viewStart = Math.Max(oldest, newest - FollowSpan);
            if (_viewEnd - _viewStart < 1e-6) _viewEnd = _viewStart + 1;
        }

        InvalidateVisual();
    }

    /// <summary>
    /// How much of a live session the window shows, in seconds.
    ///
    /// Held rather than taken from the current view each time. Deriving it from
    /// the view meant the clamp against the session's start shortened it, and
    /// that shortened span became the next tick's input — the window collapsed
    /// to milliseconds within a second or two.
    /// </summary>
    public double FollowSpan { get; set; } = 30;

    /// <summary>
    /// Gives each plotted channel its own horizontal strip instead of overlaying
    /// them all. Overlaid traces show phase relationships well but become
    /// unreadable past a handful of channels.
    /// </summary>
    public void SetStacked(bool stacked)
    {
        if (_stacked == stacked) return;

        _stacked = stacked;
        InvalidateVisual();
    }

    /// <summary>Strip belonging to one channel, or the whole area when overlaid.</summary>
    private Rect LaneFor(int index, int count, Rect area)
    {
        if (!_stacked || count <= 1) return area;

        double height = area.Height / count;
        return new Rect(area.Left, area.Top + index * height, area.Width, Math.Max(1, height - LaneGap));
    }

    /// <summary>Which lane a point falls in, ignoring the gaps so there is no dead band.</summary>
    private int LaneAt(double y, int count, Rect area)
    {
        if (!_stacked || count <= 1 || area.Height <= 0) return 0;

        return Math.Clamp((int)((y - area.Top) / (area.Height / count)), 0, count - 1);
    }

    public void ResetView()
    {
        if (_document is { SampleCount: > 0 } doc)
        {
            _viewStart = doc.Time.At(0);
            _viewEnd = doc.Time.At(doc.SampleCount - 1);
            if (_viewEnd <= _viewStart) _viewEnd = _viewStart + 1;
        }
        else
        {
            _viewStart = 0;
            _viewEnd = 1;
        }
        InvalidateVisual();
    }

    private Rect PlotArea => new(
        Pad,
        Pad,
        Math.Max(1, ActualWidth - 2 * Pad),
        Math.Max(1, ActualHeight - AxisHeight - 2 * Pad));

    private double TimeToX(double t, Rect area) =>
        area.Left + (t - _viewStart) / (_viewEnd - _viewStart) * area.Width;

    private double XToTime(double x, Rect area) =>
        _viewStart + (x - area.Left) / area.Width * (_viewEnd - _viewStart);

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(BackgroundBrush, null, full);

        if (_document is not { SampleCount: > 1 } doc)
        {
            DrawCentredHint(dc, full, "Open a datalog to begin  (Ctrl+O)");
            return;
        }

        Rect area = PlotArea;
        if (area.Width < 4 || area.Height < 4) return;

        DrawTimeAxis(dc, area);

        var visible = _series.Where(s => s.IsVisible).ToList();
        if (visible.Count == 0)
        {
            DrawCentredHint(dc, full, "Tick channels on the left to plot them");
            return;
        }

        LogChannel time = doc.Time;
        int i0 = Math.Max(0, doc.IndexAtTime(_viewStart) - 1);
        int i1 = Math.Min(time.Length, doc.IndexAtTime(_viewEnd) + 2);

        for (int i = 0; i < visible.Count; i++)
        {
            ChannelItem item = visible[i];
            Rect lane = LaneFor(i, visible.Count, area);

            if (_stacked)
            {
                DrawLaneChrome(dc, item, lane, i, visible.Count);

                // The hovered lane still gets a heavier stroke; without overlap
                // it reads as emphasis rather than as separation.
                Pen pen = ReferenceEquals(item, _hover) ? item.HighlightPen : item.Pen;
                dc.DrawGeometry(null, pen, BuildTrace(item, time, i0, i1, lane, doc.GapThreshold));
                continue;
            }

            // Overlaid: the hovered trace is drawn last so it sits on top.
            if (ReferenceEquals(item, _hover)) continue;
            dc.DrawGeometry(null, item.Pen, BuildTrace(item, time, i0, i1, area, doc.GapThreshold));
        }

        if (!_stacked && _hover is { IsVisible: true } hover)
            dc.DrawGeometry(null, hover.HighlightPen,
                BuildTrace(hover, time, i0, i1, area, doc.GapThreshold));

        DrawOccurrences(dc, area);
        DrawSelection(dc, doc, area);
        DrawMarkers(dc, doc, area);
        DrawCursor(dc, area);
        DrawHoverCard(dc, area, doc);
    }

    /// <summary>Faintly shades every span where the traced condition held.</summary>
    private void DrawOccurrences(DrawingContext dc, Rect area)
    {
        foreach ((double from, double to) in _occurrences)
        {
            if (to < _viewStart || from > _viewEnd) continue;

            double left = Math.Max(area.Left, TimeToX(from, area));
            double right = Math.Min(area.Right, TimeToX(to, area));

            // A single-sample visit still has to be visible.
            if (right - left < 1.5) right = left + 1.5;
            if (right <= area.Left || left >= area.Right) continue;

            dc.DrawRectangle(OccurrenceFill, null, new Rect(left, area.Top, right - left, area.Height));
        }
    }

    /// <summary>Shades the marked span and reports its extent.</summary>
    private void DrawSelection(DrawingContext dc, LogDocument doc, Rect area)
    {
        if (!HasSelection) return;

        double left = Math.Max(area.Left, TimeToX(_selectionFrom, area));
        double right = Math.Min(area.Right, TimeToX(_selectionTo, area));
        if (right <= left) return;

        dc.DrawRectangle(SelectionFill, null, new Rect(left, area.Top, right - left, area.Height));
        dc.DrawLine(SelectionEdge, new Point(left, area.Top), new Point(left, area.Bottom));
        dc.DrawLine(SelectionEdge, new Point(right, area.Top), new Point(right, area.Bottom));

        int first = doc.IndexAtTime(_selectionFrom);
        int last = doc.IndexAtTime(_selectionTo);
        double span = _selectionTo - _selectionFrom;

        FormattedText label = Text(
            $"{span:F2} s   ·   {last - first + 1:N0} samples", 11, SelectionEdge.Brush);

        double x = Math.Clamp(left + 6, area.Left, Math.Max(area.Left, area.Right - label.Width - 2));
        dc.DrawText(label, new Point(x, area.Top + 20));
    }

    /// <summary>
    /// Separator, name and scale for one lane. Each lane is scaled to its own
    /// channel, so the labels are what say where the trace actually sits.
    /// </summary>
    private void DrawLaneChrome(DrawingContext dc, ChannelItem item, Rect lane, int index, int count)
    {
        if (index > 0)
            dc.DrawLine(LanePen, new Point(lane.Left, lane.Top - LaneGap / 2),
                                 new Point(lane.Right, lane.Top - LaneGap / 2));

        // Below about this height the labels would cover the trace they describe.
        if (lane.Height < 26) return;

        LogChannel channel = item.Channel;
        FormattedText name = Text(channel.Name, 10, item.Brush);
        dc.DrawText(name, new Point(lane.Left + 4, lane.Top + 1));

        // The range the lane is actually drawn over, not the channel's own
        // extremes. The two differ whenever a scale is pinned, and by a little
        // whenever the steady floor has widened a near-constant channel — a
        // label that named the data instead of the axis would be describing a
        // trace that does not reach it.
        (double min, double range) = item.Scale(HoldSteady);

        FormattedText high = Text(channel.Format(min + range, item.System), 9, AxisTextBrush);
        FormattedText low = Text(channel.Format(min, item.System), 9, AxisTextBrush);
        dc.DrawText(high, new Point(lane.Right - high.Width - 2, lane.Top + 1));
        dc.DrawText(low, new Point(lane.Right - low.Width - 2, lane.Bottom - low.Height - 1));
    }

    /// <summary>
    /// Whether a nearly-constant channel is drawn as steady rather than having its
    /// last decimal place stretched to fill the lane.
    ///
    /// On by default. Off is the raw view, which is what somebody chasing a small
    /// drift actually wants — the shape this hides is exactly the shape they are
    /// looking for.
    /// </summary>
    public static bool HoldSteady { get; set; } = true;

    /// <summary>
    /// Maps a channel value to a y coordinate. This is the single place that
    /// mapping is defined — the trace geometry and the pointer hit-test must
    /// agree exactly.
    /// </summary>
    private static double ChannelY(ChannelItem item, double value, Rect area)
    {
        (double min, double range) = item.Scale(HoldSteady);

        // Inset slightly so flat-topped traces do not sit on the frame edge.
        double top = area.Top + 3;
        double height = Math.Max(1, area.Height - 6);
        return top + height * (1 - (value - min) / range);
    }

    /// <summary>
    /// Trace under the pointer. Stacked lanes make this exact — the lane the
    /// pointer is in names the channel — where overlaid traces need a proximity
    /// test that can legitimately find nothing.
    /// </summary>
    private ChannelItem? FindTraceAt(Point pointer, Rect area, int index)
    {
        List<ChannelItem> visible = [.. _series.Where(s => s.IsVisible)];
        if (visible.Count == 0) return null;

        if (_stacked)
            return pointer.Y < area.Top || pointer.Y > area.Bottom
                ? null
                : visible[LaneAt(pointer.Y, visible.Count, area)];

        ChannelItem? best = null;
        double bestDistance = HoverTolerance;

        foreach (ChannelItem item in visible)
        {
            // Through the item, so the pointer finds the line that is drawn
            // rather than the one underneath it.
            double value = item.ValueAt(index);
            if (double.IsNaN(value)) continue;

            double distance = Math.Abs(ChannelY(item, value, area) - pointer.Y);
            if (distance < bestDistance) { bestDistance = distance; best = item; }
        }

        return best;
    }

    /// <summary>The lane a channel occupies, for placing the readout beside it.</summary>
    private Rect LaneOf(ChannelItem item, Rect area)
    {
        if (!_stacked) return area;

        List<ChannelItem> visible = [.. _series.Where(s => s.IsVisible)];
        int index = visible.IndexOf(item);
        return index < 0 ? area : LaneFor(index, visible.Count, area);
    }

    /// <summary>
    /// Summary for the trace under the pointer: its value here, and both extremes
    /// with the moment each occurs, so the peak can be found without hunting.
    /// </summary>
    private void DrawHoverCard(DrawingContext dc, Rect area, LogDocument doc)
    {
        _maxHit = _minHit = Rect.Empty;
        if (_hover is not { IsVisible: true } hover || _cursorIndex < 0) return;

        LogChannel channel = hover.Channel;
        FormattedText title = Text(channel.Name, 12, hover.Brush);

        // Over a marked span the card reports that span, since it is what the
        // user just asked about; otherwise it reports the whole log.
        (string Label, string Value)[] rows = hover.Selection is { HasData: true } s
            ?
            [
                ("avg", channel.FormatWithUnits(s.Mean, hover.System)),
                ("max", channel.FormatWithUnits(s.Max, hover.System)),
                ("min", channel.FormatWithUnits(s.Min, hover.System)),
            ]
            :
            [
                ("now", channel.FormatWithUnits(hover.ValueAt(_cursorIndex), hover.System)),
                ("max", $"{channel.FormatWithUnits(channel.Max, hover.System)}  @ {Clock(doc.Time.At(channel.MaxIndex))}"),
                ("min", $"{channel.FormatWithUnits(channel.Min, hover.System)}  @ {Clock(doc.Time.At(channel.MinIndex))}"),
            ];

        // The extremes are clickable, so their labels are tinted to say so.
        // Over a span they are summary figures, not moments to jump to.
        bool canJump = channel is { MaxIndex: >= 0, MinIndex: >= 0 }
                       && !channel.IsFlat
                       && hover.Selection is not { HasData: true };
        var labels = rows.Select((r, i) =>
            Text(r.Label, 10, canJump && i > 0 ? CardAction : CardLabel)).ToArray();
        var values = rows.Select(r => Text(r.Value, 11, CardValue)).ToArray();

        const double padding = 8, gap = 8, lineHeight = 15;
        double labelWidth = labels.Max(t => t.Width);
        double width = padding * 2 + Math.Max(title.Width, labelWidth + gap + values.Max(t => t.Width));
        double height = padding * 2 + title.Height + 3 + rows.Length * lineHeight;

        // Keep the card on screen and clear of the pointer.
        double x = TimeToX(_cursorTime, area) + 14;
        if (x + width > area.Right) x = TimeToX(_cursorTime, area) - width - 14;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));

        // Anchored to the trace, but never allowed outside the plot.
        Rect lane = LaneOf(hover, area);
        double y = Math.Clamp(ChannelY(hover, hover.ValueAt(_cursorIndex), lane) - height / 2,
                              area.Top, Math.Max(area.Top, area.Bottom - height));

        dc.DrawRoundedRectangle(CardBrush, CardPen, new Rect(x, y, width, height), 4, 4);
        dc.DrawText(title, new Point(x + padding, y + padding));

        double row = y + padding + title.Height + 3;
        for (int i = 0; i < rows.Length; i++)
        {
            var bounds = new Rect(x + 3, row - 1, width - 6, lineHeight);

            if (canJump && i > 0)
            {
                if (i == 1) _maxHit = bounds; else _minHit = bounds;
                if (bounds.Contains(_pointer))
                    dc.DrawRoundedRectangle(CardRowHover, null, bounds, 3, 3);
            }

            dc.DrawText(labels[i], new Point(x + padding, row + 1));
            dc.DrawText(values[i], new Point(x + padding + labelWidth + gap, row));
            row += lineHeight;
        }
    }

    private static string Clock(double seconds) => seconds < 60
        ? $"{seconds:F2} s"
        : $"{(int)(seconds / 60)}:{seconds % 60:00.0}";

    /// <summary>
    /// Builds a polyline for one channel. When the window holds more samples than
    /// pixels the trace is decimated to a per-column min/max envelope, which keeps
    /// rendering linear in pixel width and preserves spikes that naive sampling
    /// would drop.
    /// </summary>
    private Geometry BuildTrace(
        ChannelItem item, LogChannel time, int i0, int i1, Rect area, double gapThreshold)
    {
        LogChannel channel = item.Channel;
        var geo = new StreamGeometry();

        double Y(double v) => ChannelY(item, v, area);

        using (StreamGeometryContext ctx = geo.Open())
        {
            bool open = false;
            int column = int.MinValue;
            double colMin = 0, colMax = 0;

            for (int i = i0; i < i1; i++)
            {
                double v = item.ValueAt(i);

                // Lift the pen across a pause in logging or a missing sample, so
                // the trace shows an absence of data rather than a flat line.
                bool discontinuity = double.IsNaN(v)
                                     || (i > 0 && time.At(i) - time.At(i - 1) > gapThreshold);

                if (discontinuity)
                {
                    if (column != int.MinValue)
                    {
                        EmitColumn(ctx, column, colMin, colMax, Y, ref open);
                        column = int.MinValue;
                    }
                    open = false;
                    if (double.IsNaN(v)) continue;
                }

                double x = TimeToX(time.At(i), area);
                int c = (int)Math.Round(x);

                if (c != column)
                {
                    if (column != int.MinValue)
                        EmitColumn(ctx, column, colMin, colMax, Y, ref open);

                    column = c;
                    colMin = colMax = v;
                }
                else
                {
                    if (v < colMin) colMin = v;
                    if (v > colMax) colMax = v;
                }
            }

            if (column != int.MinValue)
                EmitColumn(ctx, column, colMin, colMax, Y, ref open);
        }

        geo.Freeze();
        return geo;
    }

    private static void EmitColumn(
        StreamGeometryContext ctx, int x, double lo, double hi, Func<double, double> y, ref bool open)
    {
        // Entering at the high value and leaving at the low keeps successive
        // columns connected into a continuous stroke.
        var a = new Point(x, y(hi));
        var b = new Point(x, y(lo));

        if (!open)
        {
            ctx.BeginFigure(a, isFilled: false, isClosed: false);
            open = true;
        }
        else
        {
            ctx.LineTo(a, isStroked: true, isSmoothJoin: false);
        }

        if (b != a) ctx.LineTo(b, isStroked: true, isSmoothJoin: false);
    }

    private void DrawTimeAxis(DrawingContext dc, Rect area)
    {
        double span = _viewEnd - _viewStart;
        double step = NiceStep(span / 8);
        double first = Math.Ceiling(_viewStart / step) * step;

        // Pick one format for the whole axis; mixing "40s" with "1:20" reads badly.
        bool clock = _viewEnd >= 60;

        for (double t = first; t <= _viewEnd; t += step)
        {
            double x = TimeToX(t, area);
            if (x < area.Left - 1 || x > area.Right + 1) continue;

            dc.DrawLine(GridPen, new Point(x, area.Top), new Point(x, area.Bottom));
            FormattedText label = Text(FormatTime(t, step, clock), 11, AxisTextBrush);
            dc.DrawText(label, new Point(x - label.Width / 2, area.Bottom + 6));
        }

        dc.DrawLine(GridPen, new Point(area.Left, area.Bottom), new Point(area.Right, area.Bottom));
    }

    private void DrawMarkers(DrawingContext dc, LogDocument doc, Rect area)
    {
        // Markers cluster tightly when zoomed out. Every one still gets a rule,
        // but labels are dropped unless there is room to read them.
        const double MinLabelGap = 15;
        double lastLabelX = double.NegativeInfinity;

        foreach (LogMarker marker in doc.Markers)
        {
            if (marker.Time < _viewStart || marker.Time > _viewEnd) continue;

            double x = TimeToX(marker.Time, area);
            dc.DrawLine(MarkerPen, new Point(x, area.Top), new Point(x, area.Bottom));

            if (x - lastLabelX < MinLabelGap) continue;
            lastLabelX = x;

            // Rotated -90 the text runs upward, so anchor it near the bottom of
            // the plot and clip the string to the height available.
            var label = Text(marker.Text, 10, MarkerPen.Brush);
            label.MaxTextWidth = Math.Max(20, area.Height - 12);
            label.MaxLineCount = 1;
            label.Trimming = TextTrimming.CharacterEllipsis;

            var origin = new Point(x + 4, area.Bottom - 6);
            dc.PushTransform(new RotateTransform(-90, origin.X, origin.Y));
            dc.DrawText(label, origin);
            dc.Pop();
        }
    }

    private void DrawCursor(DrawingContext dc, Rect area)
    {
        if (double.IsNaN(_cursorTime)) return;

        double x = TimeToX(_cursorTime, area);
        if (x < area.Left || x > area.Right) return;

        dc.DrawLine(CursorPen, new Point(x, area.Top), new Point(x, area.Bottom));

        FormattedText label = Text($"{_cursorTime:F3} s", 11, AxisTextBrush);
        double lx = Math.Min(x + 5, area.Right - label.Width);
        dc.DrawText(label, new Point(lx, area.Top + 2));
    }

    private void DrawCentredHint(DrawingContext dc, Rect full, string message)
    {
        FormattedText text = Text(message, 13, AxisTextBrush);
        dc.DrawText(text, new Point((full.Width - text.Width) / 2, (full.Height - text.Height) / 2));
    }

    // ----- interaction -------------------------------------------------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_document is not { SampleCount: > 0 } doc) return;

        Rect area = PlotArea;
        Point p = e.GetPosition(this);

        if (_selecting)
        {
            double here = XToTime(p.X, area);
            _selectionFrom = Math.Min(_selectionAnchor, here);
            _selectionTo = Math.Max(_selectionAnchor, here);
            ReportSelection();
            InvalidateVisual();
            return;
        }

        if (_panning)
        {
            double perPixel = (_panEnd - _panStart) / area.Width;
            double shift = (_panAnchor.X - p.X) * perPixel;
            _viewStart = _panStart + shift;
            _viewEnd = _panEnd + shift;
            ClampView(doc);
            InvalidateVisual();
            return;
        }

        MoveCursorTo(p);
    }

    /// <summary>
    /// Places the crosshair and trace highlight as though the pointer were at
    /// <paramref name="position"/>, in control coordinates.
    /// </summary>
    public void MoveCursorTo(Point position)
    {
        if (_document is not { SampleCount: > 0 } doc) return;

        Rect area = PlotArea;
        _pointer = position;

        // Over a clickable row of the readout, keep the current cursor so the
        // card does not slide away from under the pointer.
        if (_maxHit.Contains(position) || _minHit.Contains(position))
        {
            Cursor = Cursors.Hand;
            InvalidateVisual();
            return;
        }
        Cursor = Cursors.Cross;

        // Snap the crosshair to the nearest sample so the line and the numeric
        // readout can never disagree.
        _cursorIndex = doc.IndexAtTime(XToTime(position.X, area));
        _cursorTime = doc.Time.At(_cursorIndex);
        CursorSampleChanged?.Invoke(_cursorIndex);

        ChannelItem? hit = FindTraceAt(position, area, _cursorIndex);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            HoverChannelChanged?.Invoke(hit);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursorTime = double.NaN;
        _cursorIndex = -1;

        if (_hover is not null)
        {
            _hover = null;
            HoverChannelChanged?.Invoke(null);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        // Clicking an extreme in the readout jumps straight to it.
        if (_hover is { } hovered && _document is { } document)
        {
            Point click = e.GetPosition(this);
            int target = _maxHit.Contains(click) ? hovered.Channel.MaxIndex
                       : _minHit.Contains(click) ? hovered.Channel.MinIndex
                       : -1;

            if (target >= 0)
            {
                FocusTime(document.Time.At(target));
                e.Handled = true;
                return;
            }
        }

        if (e.ClickCount == 2) { ViewChangedByUser?.Invoke(); ResetView(); return; }

        Point start = e.GetPosition(this);

        // Shift-drag marks a span to summarise; a plain drag still pans.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _document is { SampleCount: > 0 })
        {
            _selecting = true;
            _selectionAnchor = XToTime(start.X, PlotArea);
            _selectionFrom = _selectionTo = _selectionAnchor;
            CaptureMouse();
            Cursor = Cursors.SizeWE;
            InvalidateVisual();
            return;
        }

        // Clicking away drops an existing selection.
        if (HasSelection) ClearSelection();

        _panning = true;
        ViewChangedByUser?.Invoke();
        _panAnchor = start;
        _panStart = _viewStart;
        _panEnd = _viewEnd;
        CaptureMouse();
        Cursor = Cursors.ScrollWE;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_selecting)
        {
            _selecting = false;

            // A shift-click with no drag is a clear, not a zero-width span.
            if (!HasSelection) ClearSelection(); else ReportSelection();
        }

        _panning = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_document is not { SampleCount: > 0 } doc) return;

        ViewChangedByUser?.Invoke();

        Rect area = PlotArea;
        double focus = XToTime(e.GetPosition(this).X, area);
        double factor = e.Delta > 0 ? 1 / 1.25 : 1.25;

        // Zoom about the pointer so the value under it stays put.
        _viewStart = focus - (focus - _viewStart) * factor;
        _viewEnd = focus + (_viewEnd - focus) * factor;

        ClampView(doc);
        InvalidateVisual();
    }

    private void ClampView(LogDocument doc)
    {
        double lo = doc.Time.At(0);
        double hi = doc.Time.At(doc.SampleCount - 1);
        if (hi <= lo) hi = lo + 1;

        // Never zoom past a few samples, and never scroll off the data.
        double minSpan = Math.Max((hi - lo) / 100000, 1e-4);
        if (_viewEnd - _viewStart < minSpan)
        {
            double mid = (_viewStart + _viewEnd) / 2;
            _viewStart = mid - minSpan / 2;
            _viewEnd = mid + minSpan / 2;
        }

        double span = Math.Min(_viewEnd - _viewStart, hi - lo);
        if (_viewStart < lo) { _viewStart = lo; _viewEnd = lo + span; }
        if (_viewEnd > hi) { _viewEnd = hi; _viewStart = hi - span; }
    }

    // ----- helpers -----------------------------------------------------------

    private static string FormatTime(double seconds, double step, bool clock)
    {
        int decimals = step >= 1 ? 0 : step >= 0.1 ? 1 : step >= 0.01 ? 2 : 3;

        if (!clock)
            return seconds.ToString("F" + decimals, CultureInfo.InvariantCulture) + "s";

        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        string body = $"{(int)span.TotalMinutes}:{span.Seconds:00}";
        return decimals == 0 ? body : $"{body}.{span.Milliseconds:000}";
    }

    /// <summary>Rounds a raw interval up to the nearest 1/2/5 x 10^n.</summary>
    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 1;

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / magnitude;
        double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private FormattedText Text(string s, double size, Brush brush) => new(
        s,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

