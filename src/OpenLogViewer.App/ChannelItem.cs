using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>A channel as presented in the sidebar: colour, visibility, live readout.</summary>
public sealed class ChannelItem : ObservableObject
{
    private bool _isVisible;
    private bool _isHighlighted;
    private string _value = "—";

    public ChannelItem(LogChannel channel, Color color)
    {
        Channel = channel;
        Category = ChannelClassifier.Classify(channel.Name, channel.Units);
        SetColor(color);
    }

    public LogChannel Channel { get; private set; }

    /// <summary>
    /// Points the row at a longer version of the same channel, as a live session
    /// produces on every poll. Colour and visibility are the user's and survive;
    /// only the data behind the row changes.
    /// </summary>
    public void Rebind(LogChannel channel)
    {
        if (ReferenceEquals(Channel, channel)) return;

        Channel = channel;
        Raise(nameof(Channel));
        Raise(nameof(Range));
        Raise(nameof(IsFlat));
        Raise(nameof(CanJump));
    }

    public string Name => Channel.Name;

    public string Units => UnitConvert.Label(Channel.Units, System);

    /// <summary>
    /// Which units this row reads in. Set by the view model rather than looked
    /// up, so every row of a list is answering in the same system.
    /// </summary>
    public UnitSystem System { get; private set; } = UnitSystem.AsReported;

    /// <summary>Shows this row in another system of units.</summary>
    public void Show(UnitSystem system)
    {
        if (system == System) return;

        System = system;

        Raise(nameof(Units));
        Raise(nameof(Range));

        // The cursor value is a stored string rather than a computed one, so it
        // has to be rebuilt rather than merely re-announced.
        UpdateCursor(_cursorIndex);
    }

    private int _cursorIndex = -1;

    public ChannelCategory Category { get; }

    /// <summary>
    /// True for a channel the user defined rather than the logger recorded. The
    /// list marks these: a value that was computed and one that was measured
    /// answer different questions when a trace looks wrong.
    /// </summary>
    public bool IsCalculated { get; init; }

    public string CategoryName => ChannelClassifier.DisplayName(Category);

    /// <summary>Groups sort by this, so categories appear in a deliberate order.</summary>
    public int CategoryOrder => (int)Category;

    /// <summary>
    /// A scale the user pinned, or null to take the channel's own range.
    ///
    /// Auto-scaling every trace to its own range is what lets a dozen channels
    /// in different units share a plot. What it costs is comparability: the same
    /// channel is drawn over a different range in every log, and in the same log
    /// before and after a filter, so two traces cannot be read against each
    /// other by eye and a shape cannot be remembered between sessions. Pinning
    /// RPM to 0…8000 gives that back, for the channels where it matters.
    /// </summary>
    public (double Min, double Max)? FixedRange { get; private set; }

    public bool HasFixedRange => FixedRange is not null;

    /// <summary>
    /// True when the colour was pinned rather than handed out, which is what
    /// keeps a theme change from taking it back.
    /// </summary>
    public bool HasFixedColor { get; private set; }

    /// <summary>
    /// How hard this channel's trace is smoothed when it is drawn.
    ///
    /// <b>Drawing only.</b> Nothing that judges the engine reads through this —
    /// a smoothed AFR hides exactly the single-sample lean excursion that
    /// damages a piston, so the insights, the calibration, the histogram and
    /// every export take the channel as logged.
    /// </summary>
    public SmoothingLevel Smoothing { get; private set; }

    public bool IsSmoothed => Smoothing != SmoothingLevel.None;

    private double[]? _smoothed;

    /// <summary>Sets the smoothing, throwing away whatever was worked out before.</summary>
    public void SetSmoothing(SmoothingLevel level)
    {
        if (Smoothing == level) return;

        Smoothing = level;
        _smoothed = null;

        Raise(nameof(Smoothing));
        Raise(nameof(IsSmoothed));
        Raise(nameof(SmoothingNote));
        Raise(nameof(ScaleNote));
    }

    /// <summary>What the row says about it, or nothing where it is as logged.</summary>
    public string SmoothingNote =>
        IsSmoothed
            ? $"smoothed · median of {Core.Smoothing.Window(Smoothing)}"
            : "";

    /// <summary>
    /// The value to draw at a sample: smoothed where this channel is, as logged
    /// otherwise.
    ///
    /// Worked out once for the whole channel and kept, because a plot asks for
    /// values a great many times over as it is panned and zoomed, and a median
    /// window recomputed per repaint would be felt.
    /// </summary>
    public double ValueAt(int index)
    {
        if (!IsSmoothed) return Channel.At(index);

        _smoothed ??= Core.Smoothing.Median(
            [.. Enumerable.Range(0, Channel.Length).Select(Channel.At)],
            Core.Smoothing.Window(Smoothing));

        return index >= 0 && index < _smoothed.Length ? _smoothed[index] : double.NaN;
    }

    /// <summary>Pins the vertical range, or clears it with null.</summary>
    public void SetFixedRange((double Min, double Max)? range)
    {
        if (range is { } r && (!double.IsFinite(r.Min) || !double.IsFinite(r.Max) || r.Max <= r.Min))
            range = null;

        FixedRange = range;

        Raise(nameof(FixedRange));
        Raise(nameof(HasFixedRange));
        Raise(nameof(ScaleNote));
    }

    /// <summary>
    /// The range this trace is drawn over: the pinned one where there is one,
    /// otherwise the channel's own with the steady-channel floor applied.
    ///
    /// A pinned range is used exactly as given. The floor exists to stop a
    /// nearly-constant channel filling its lane with its own last decimal place,
    /// and somebody who has said what range they want has already answered that.
    /// </summary>
    public (double Min, double Range) Scale(bool holdSteady) =>
        FixedRange is { } r
            ? (r.Min, r.Max - r.Min)
            : TraceScale.For(Channel.Min, Channel.Max, holdSteady);

    /// <summary>
    /// What the row says about a pinned scale, so a trace drawn over something
    /// other than its own range says so rather than looking like the data.
    /// </summary>
    public string ScaleNote => FixedRange is { } r
        ? $"scale {Channel.Format(r.Min, System)} … {Channel.Format(r.Max, System)}"
        : "";

    public Color Color { get; private set; }

    public SolidColorBrush Brush { get; private set; } = null!;

    public Pen Pen { get; private set; } = null!;

    /// <summary>Heavier stroke used while the pointer is over this trace.</summary>
    public Pen HighlightPen { get; private set; } = null!;

    /// <summary>True while the pointer is over this trace; emphasised in the list.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set { if (Set(ref _isVisible, value)) VisibilityChanged?.Invoke(this); }
    }

    /// <summary>Value under the cursor, formatted at the channel's own precision.</summary>
    public string Value
    {
        get => _value;
        private set => Set(ref _value, value);
    }

    private ChannelStatistics? _selection;

    /// <summary>
    /// Whole-log range, or the marked span's range once one is set — the row
    /// answers "what did this do here" rather than "over the whole drive".
    /// </summary>
    public string Range
    {
        get
        {
            if (_selection is { HasData: true } s)
                return $"{Channel.Format(s.Min, System)} … {Channel.Format(s.Max, System)}"
                       + $"   avg {Channel.Format(s.Mean, System)}";

            return Channel.IsFlat
                ? $"constant {Channel.Format(Channel.Min, System)}"
                : $"{Channel.Format(Channel.Min, System)} … {Channel.Format(Channel.Max, System)}";
        }
    }

    /// <summary>Statistics over the marked span, or null when nothing is marked.</summary>
    public ChannelStatistics? Selection => _selection;

    public void SetSelection(ChannelStatistics? statistics)
    {
        _selection = statistics;

        // Recomputed, not just re-announced: dropping a span used to leave the
        // row showing that span's average until the pointer happened to move.
        Value = statistics is { HasData: true } s
            ? Channel.FormatWithUnits(s.Mean, System)
            : "—";

        Raise(nameof(Range));
        Raise(nameof(Selection));
    }

    /// <summary>Flat channels are logged but carry no signal; the UI de-emphasises them.</summary>
    public bool IsFlat => Channel.IsFlat;

    /// <summary>Jumping to an extreme only means anything for a channel that moves.</summary>
    public bool CanJump => !Channel.IsFlat && Channel.MaxIndex >= 0;

    public event Action<ChannelItem>? VisibilityChanged;

    /// <summary>
    /// Pins this channel's colour, or with null hands it back to the palette.
    ///
    /// A pinned colour survives a theme change, which is the point of it and
    /// also its cost — see <see cref="ChannelStyleStore"/>.
    /// </summary>
    public void SetFixedColor(Color? color)
    {
        HasFixedColor = color is not null;

        if (color is { } c) SetColor(c);

        Raise(nameof(HasFixedColor));
    }

    /// <summary>
    /// Recolours the trace. Colours are handed out as channels are plotted rather
    /// than fixed per channel, so whatever is on screen stays distinguishable —
    /// unless the user has pinned one, which <see cref="HasFixedColor"/> marks
    /// and callers handing out the palette are expected to respect.
    /// </summary>
    public void SetColor(Color color)
    {
        Color = color;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Brush = brush;

        var pen = new Pen(brush, 1.4);
        pen.Freeze();
        Pen = pen;

        var highlight = new Pen(brush, 2.8);
        highlight.Freeze();
        HighlightPen = highlight;

        Raise(nameof(Color));
        Raise(nameof(Brush));
        Raise(nameof(Pen));
        Raise(nameof(HighlightPen));
    }

    public void UpdateCursor(int index)
    {
        _cursorIndex = index;

        // A marked span outranks the cursor: the average over the span is the
        // number being read, and the cursor is incidental while dragging.
        if (_selection is { HasData: true } s)
        {
            Value = Channel.FormatWithUnits(s.Mean, System);
            return;
        }

        Value = index < 0 ? "—" : Channel.FormatWithUnits(Channel.At(index), System);
    }
}
