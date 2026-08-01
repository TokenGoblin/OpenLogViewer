using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>A filter condition as shown in the sidebar, with a bindable toggle.</summary>
public sealed class FilterItem : ObservableObject
{
    private LogFilter _filter;

    public FilterItem(LogFilter filter) => _filter = filter;

    public LogFilter Filter => _filter;

    public string Name => _filter.Name;

    /// <summary>The condition itself, e.g. "CLT ≥ 160".</summary>
    public string Description => _filter.Describe();

    public bool Enabled
    {
        get => _filter.Enabled;
        set
        {
            if (_filter.Enabled == value) return;

            _filter = _filter with { Enabled = value };
            Raise();
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when the toggle changes, so the table can be rebuilt and saved.</summary>
    public event Action? Changed;
}

/// <summary>
/// Where the table's bin edges come from. A real option object rather than a
/// nullable entry, because WPF will not apply an item template to null and the
/// "from the data" row would render blank.
/// </summary>
public sealed record AxisSourceOption(string Label, TuneAxisSet? Axes)
{
    public static AxisSourceOption FromData { get; } = new("From the log data", null);
}

/// <summary>
/// A channel to subtract from Z, or "None". A real option object rather than a
/// nullable entry, because WPF will not apply an item template to null.
/// </summary>
public sealed record CompareOption(string Label, ChannelItem? Channel)
{
    public static CompareOption None { get; } = new("None — show the value itself", null);
}

/// <summary>A comparison paired with the symbol shown for it.</summary>
public sealed record ComparisonOption(FilterComparison Value, string Label)
{
    public static IReadOnlyList<ComparisonOption> All { get; } =
    [
        new(FilterComparison.AboveOrEqual, "≥  at or above"),
        new(FilterComparison.Above, ">  above"),
        new(FilterComparison.BelowOrEqual, "≤  at or below"),
        new(FilterComparison.Below, "<  below"),
        new(FilterComparison.Between, "↔  between"),
        new(FilterComparison.Outside, "↮  outside"),
    ];

    public bool NeedsSecondValue =>
        Value is FilterComparison.Between or FilterComparison.Outside;
}
