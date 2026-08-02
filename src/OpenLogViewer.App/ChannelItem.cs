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

    public string Units => Channel.Units;

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
                return $"{Channel.Format(s.Min)} … {Channel.Format(s.Max)}   avg {Channel.Format(s.Mean)}";

            return Channel.IsFlat
                ? $"constant {Channel.Format(Channel.Min)}"
                : $"{Channel.Format(Channel.Min)} … {Channel.Format(Channel.Max)}";
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
            ? Channel.FormatWithUnits(s.Mean)
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
    /// Recolours the trace. Colours are handed out as channels are plotted rather
    /// than fixed per channel, so whatever is on screen stays distinguishable.
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
        // A marked span outranks the cursor: the average over the span is the
        // number being read, and the cursor is incidental while dragging.
        if (_selection is { HasData: true } s)
        {
            Value = Channel.FormatWithUnits(s.Mean);
            return;
        }

        Value = index < 0 ? "—" : Channel.FormatWithUnits(Channel.At(index));
    }
}
