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

    public LogChannel Channel { get; }

    public string Name => Channel.Name;

    public string Units => Channel.Units;

    public ChannelCategory Category { get; }

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

    public string Range => Channel.IsFlat
        ? $"constant {Channel.Format(Channel.Min)}"
        : $"{Channel.Format(Channel.Min)} … {Channel.Format(Channel.Max)}";

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

    public void UpdateCursor(int index) =>
        Value = index < 0 ? "—" : Channel.FormatWithUnits(Channel.At(index));
}
