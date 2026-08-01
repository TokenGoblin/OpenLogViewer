using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

public enum ChannelSort
{
    /// <summary>Grouped by what the channel measures.</summary>
    Category,

    /// <summary>Flat alphabetical list.</summary>
    Name,

    /// <summary>Currently plotted channels first.</summary>
    Plotted,
}

public sealed class MainViewModel : ObservableObject
{
    /// <summary>
    /// Trace colours, chosen to stay distinguishable against the dark ground and
    /// to remain separable for the common red/green colour-vision deficiencies.
    /// </summary>
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0xFF, 0x70, 0x43),
        Color.FromRgb(0x9C, 0xCC, 0x65), Color.FromRgb(0xFF, 0xCA, 0x28),
        Color.FromRgb(0xBA, 0x68, 0xC8), Color.FromRgb(0x26, 0xC6, 0xDA),
        Color.FromRgb(0xF0, 0x62, 0x92), Color.FromRgb(0xA1, 0x88, 0x7F),
        Color.FromRgb(0x7E, 0x8C, 0xE0), Color.FromRgb(0xFF, 0xA7, 0x26),
    ];

    /// <summary>Channels ticked on load, when the log has them.</summary>
    private static readonly string[] DefaultChannels = ["RPM", "MAP", "TPS", "AFR", "CLT"];

    private const string DefaultHint =
        "Scroll = zoom  •  Drag = pan  •  Double-click = fit  •  " +
        "Hover a trace for its min/max, then click either to jump there  •  " +
        "Shift-drag to mark a span and summarise it  •  Right-click for more";

    private readonly PresetStore _store = new();

    private LogDocument? _document;
    private string _hint = DefaultHint;
    private string _search = "";
    private string _status = "Ready — open a .mlg, .msl or .csv datalog";
    private string _title = "OpenLogViewer";
    private string _cursorTime = "—";
    private string _filterSummary = "";
    private ChannelSort _sort = ChannelSort.Category;
    private bool _hideUnused = true;
    private int _colorCursor;

    public MainViewModel()
    {
        ChannelView = CollectionViewSource.GetDefaultView(Channels);
        ChannelView.Filter = FilterChannel;
        ApplySort();
        RefreshPresets();
    }

    public ObservableCollection<ChannelItem> Channels { get; } = [];

    /// <summary>Saved channel selections, in name order.</summary>
    public ObservableCollection<ChannelPreset> Presets { get; } = [];

    /// <summary>Status strip text: interaction hints, or the result of the last action.</summary>
    public string Hint
    {
        get => _hint;
        private set => Set(ref _hint, value);
    }

    public ICollectionView ChannelView { get; }

    public LogDocument? Document
    {
        get => _document;
        private set => Set(ref _document, value);
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) RefreshView(); }
    }

    /// <summary>
    /// Logs routinely declare dozens of channels that never move — 98 of 179 in
    /// one sample. Hiding them by default is the single biggest usability win.
    /// </summary>
    public bool HideUnused
    {
        get => _hideUnused;
        set { if (Set(ref _hideUnused, value)) RefreshView(); }
    }

    public ChannelSort Sort
    {
        get => _sort;
        set
        {
            if (!Set(ref _sort, value)) return;
            ApplySort();
            Raise(nameof(SortByCategory));
            Raise(nameof(SortByName));
            Raise(nameof(SortByPlotted));
        }
    }

    // Bindable facades so the sort chips need no value converter.
    public bool SortByCategory
    {
        get => _sort == ChannelSort.Category;
        set { if (value) Sort = ChannelSort.Category; }
    }

    public bool SortByName
    {
        get => _sort == ChannelSort.Name;
        set { if (value) Sort = ChannelSort.Name; }
    }

    public bool SortByPlotted
    {
        get => _sort == ChannelSort.Plotted;
        set { if (value) Sort = ChannelSort.Plotted; }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string CursorTime
    {
        get => _cursorTime;
        private set => Set(ref _cursorTime, value);
    }

    /// <summary>"81 of 179 channels · 5 plotted", shown under the filter bar.</summary>
    public string FilterSummary
    {
        get => _filterSummary;
        private set => Set(ref _filterSummary, value);
    }

    /// <summary>Raised when the channel set or its visibility changes.</summary>
    public event Action? PlotInvalidated;

    private bool _stackedLanes;

    /// <summary>
    /// Give each plotted channel its own strip. Overlaid traces read well for
    /// phase relationships; stacked lanes read well for shape and magnitude.
    /// </summary>
    public bool StackedLanes
    {
        get => _stackedLanes;
        set
        {
            if (!Set(ref _stackedLanes, value)) return;

            Raise(nameof(OverlaidLanes));
            PlotInvalidated?.Invoke();
        }
    }

    public bool OverlaidLanes
    {
        get => !_stackedLanes;
        set { if (value) StackedLanes = false; }
    }

    // ----- histogram --------------------------------------------------------

    private bool _showHistogram;
    private ChannelItem? _xAxis, _yAxis, _zAxis;
    private int _columns = 16, _rows = 16;
    private HistogramStatistic _statistic = HistogramStatistic.Mean;
    private bool _colorByCount;
    private bool _zoomOnly;

    /// <summary>Channels offered as table axes: those that actually vary.</summary>
    public ObservableCollection<ChannelItem> AxisChannels { get; } = [];

    // ----- axis breakpoints -------------------------------------------------

    private AxisSourceOption _axisSource = AxisSourceOption.FromData;

    /// <summary>
    /// Breakpoint sources: uniform bins from the data, or a table from the tune
    /// embedded in the log.
    /// </summary>
    public ObservableCollection<AxisSourceOption> AxisSources { get; } = [];

    public AxisSourceOption AxisSource
    {
        get => _axisSource;
        set
        {
            if (value is null || !Set(ref _axisSource, value)) return;

            Raise(nameof(UsingTuneAxes));
            Raise(nameof(UsingDataAxes));

            // The tune's axes are RPM against load, so move the pickers onto
            // matching channels — but only when the current pick does not
            // already measure the right thing, or choosing a tune table would
            // silently swap a deliberate selection for an equivalent channel.
            if (value.Axes is { } axes)
            {
                _xAxis = KeepOrMatch(_xAxis, axes.X.Units, "rpm");
                _yAxis = KeepOrMatch(_yAxis, axes.Y.Units, "kpa");
                Raise(nameof(XAxis));
                Raise(nameof(YAxis));
            }

            HistogramInvalidated?.Invoke();
        }
    }

    private TuneAxisSet? _tuneAxes => _axisSource.Axes;

    /// <summary>
    /// Keeps the current channel when it already measures the right quantity,
    /// otherwise picks the first one that does.
    /// </summary>
    private ChannelItem? KeepOrMatch(ChannelItem? current, string units, string fallback)
    {
        if (current is not null && (Matches(current, units) || Matches(current, fallback)))
            return current;

        return AxisChannels.FirstOrDefault(c => Matches(c, units))
               ?? AxisChannels.FirstOrDefault(c => Matches(c, fallback))
               ?? current;

        static bool Matches(ChannelItem c, string units) =>
            c.Units.Equals(units, StringComparison.OrdinalIgnoreCase);
    }

    public bool UsingTuneAxes => _tuneAxes is not null;

    /// <summary>Rows and columns only apply when bins come from the data.</summary>
    public bool UsingDataAxes => _tuneAxes is null;

    public bool HasTuneAxes => AxisSources.Count > 1;

    // ----- data filters -----------------------------------------------------

    private readonly FilterStore _filterStore = new();
    private ChannelItem? _newFilterChannel;
    private ComparisonOption _newComparison = ComparisonOption.All[0];
    private string _newLow = "";
    private string _newHigh = "";

    /// <summary>Conditions a sample must meet to be counted in the table.</summary>
    public ObservableCollection<FilterItem> Filters { get; } = [];

    public IReadOnlyList<ComparisonOption> Comparisons => ComparisonOption.All;

    public ChannelItem? NewFilterChannel
    {
        get => _newFilterChannel;
        set => Set(ref _newFilterChannel, value);
    }

    public ComparisonOption NewComparison
    {
        get => _newComparison;
        set { if (Set(ref _newComparison, value)) Raise(nameof(NeedsSecondValue)); }
    }

    public bool NeedsSecondValue => _newComparison.NeedsSecondValue;

    public string NewLow
    {
        get => _newLow;
        set => Set(ref _newLow, value);
    }

    public string NewHigh
    {
        get => _newHigh;
        set => Set(ref _newHigh, value);
    }

    /// <summary>Adds the filter described by the editor fields.</summary>
    public bool AddFilter()
    {
        if (NewFilterChannel is null)
        {
            Hint = "Pick a channel for the filter.";
            return false;
        }

        if (!double.TryParse(_newLow, out double low))
        {
            Hint = "Enter a number for the filter value.";
            return false;
        }

        double high = 0;
        if (_newComparison.NeedsSecondValue && !double.TryParse(_newHigh, out high))
        {
            Hint = "This comparison needs two values.";
            return false;
        }

        var filter = new LogFilter
        {
            Name = NewFilterChannel.Name,
            Channel = NewFilterChannel.Name,
            Comparison = _newComparison.Value,
            Low = low,
            High = high,
            Enabled = true,
        };

        AddFilterItem(filter);
        SaveFilters();

        NewLow = "";
        NewHigh = "";
        HistogramInvalidated?.Invoke();
        return true;
    }

    public void DeleteFilter(FilterItem item)
    {
        if (!Filters.Remove(item)) return;

        SaveFilters();
        HistogramInvalidated?.Invoke();
    }

    public void SetAllFilters(bool enabled)
    {
        foreach (FilterItem item in Filters) item.Enabled = enabled;
    }

    private void AddFilterItem(LogFilter filter)
    {
        var item = new FilterItem(filter);
        item.Changed += OnFilterChanged;
        Filters.Add(item);
    }

    private void OnFilterChanged()
    {
        SaveFilters();
        HistogramInvalidated?.Invoke();
    }

    private void SaveFilters() => _filterStore.Replace(Filters.Select(f => f.Filter));

    /// <summary>
    /// Loads saved filters, then offers suggestions for any channel this log has
    /// that is not already covered. Suggestions arrive switched off, so opening a
    /// log never silently changes what the table counts.
    /// </summary>
    private void SeedFilters(LogDocument document)
    {
        Filters.Clear();

        foreach (LogFilter filter in _filterStore.Filters) AddFilterItem(filter);

        foreach (LogFilter suggestion in SampleFilter.Suggest(document))
        {
            bool covered = Filters.Any(f =>
                f.Filter.Channel.Equals(suggestion.Channel, StringComparison.OrdinalIgnoreCase));
            if (!covered) AddFilterItem(suggestion);
        }
    }

    public HistogramTable? Table { get; private set; }

    /// <summary>Raised when a histogram setting changes and the table needs rebuilding.</summary>
    public event Action? HistogramInvalidated;

    public bool ShowHistogram
    {
        get => _showHistogram;
        set
        {
            if (!Set(ref _showHistogram, value)) return;
            Raise(nameof(ShowLog));
            HistogramInvalidated?.Invoke();
        }
    }

    public bool ShowLog
    {
        get => !_showHistogram;
        set { if (value) ShowHistogram = false; }
    }

    public ChannelItem? XAxis
    {
        get => _xAxis;
        set { if (Set(ref _xAxis, value)) HistogramInvalidated?.Invoke(); }
    }

    public ChannelItem? YAxis
    {
        get => _yAxis;
        set { if (Set(ref _yAxis, value)) HistogramInvalidated?.Invoke(); }
    }

    public ChannelItem? ZAxis
    {
        get => _zAxis;
        set { if (Set(ref _zAxis, value)) HistogramInvalidated?.Invoke(); }
    }

    private CompareOption _zCompare = CompareOption.None;

    /// <summary>Channels Z can be measured against, plus a "None" entry.</summary>
    public ObservableCollection<CompareOption> CompareOptions { get; } = [];

    /// <summary>
    /// Subtracting a target turns "what did it read" into "how far off is it",
    /// which is the question a tuning table is actually asked.
    /// </summary>
    public CompareOption ZCompare
    {
        get => _zCompare;
        set { if (value is not null && Set(ref _zCompare, value)) HistogramInvalidated?.Invoke(); }
    }

    public int HistogramColumns
    {
        get => _columns;
        set { if (Set(ref _columns, Math.Clamp(value, 2, 40))) HistogramInvalidated?.Invoke(); }
    }

    public int HistogramRows
    {
        get => _rows;
        set { if (Set(ref _rows, Math.Clamp(value, 2, 40))) HistogramInvalidated?.Invoke(); }
    }

    public bool ColorByCount
    {
        get => _colorByCount;
        set { if (Set(ref _colorByCount, value)) HistogramInvalidated?.Invoke(); }
    }

    /// <summary>Restricts the table to the time range currently zoomed to on the plot.</summary>
    public bool HistogramZoomOnly
    {
        get => _zoomOnly;
        set { if (Set(ref _zoomOnly, value)) HistogramInvalidated?.Invoke(); }
    }

    public bool StatMean
    {
        get => _statistic == HistogramStatistic.Mean;
        set { if (value) SetStatistic(HistogramStatistic.Mean); }
    }

    public bool StatMin
    {
        get => _statistic == HistogramStatistic.Min;
        set { if (value) SetStatistic(HistogramStatistic.Min); }
    }

    public bool StatMax
    {
        get => _statistic == HistogramStatistic.Max;
        set { if (value) SetStatistic(HistogramStatistic.Max); }
    }

    public bool StatCount
    {
        get => _statistic == HistogramStatistic.Count;
        set { if (value) SetStatistic(HistogramStatistic.Count); }
    }

    private void SetStatistic(HistogramStatistic statistic)
    {
        if (_statistic == statistic) return;

        _statistic = statistic;
        Raise(nameof(StatMean));
        Raise(nameof(StatMin));
        Raise(nameof(StatMax));
        Raise(nameof(StatCount));
        HistogramInvalidated?.Invoke();
    }

    /// <summary>Rebuilds <see cref="Table"/> over the given sample window.</summary>
    public void RebuildHistogram(int firstSample, int lastSample)
    {
        if (Document is null || XAxis is null || YAxis is null || ZAxis is null)
        {
            Table = null;
            return;
        }

        SampleMask mask = SampleFilter.Build(Document, Filters.Select(f => f.Filter));

        LogChannel? against = _zCompare.Channel?.Channel;

        Table = _tuneAxes is { } axes
            ? HistogramTable.Build(
                XAxis.Channel, YAxis.Channel, ZAxis.Channel,
                axes.X.Breakpoints, axes.Y.Breakpoints,
                firstSample, lastSample, _statistic, mask, against)
            : HistogramTable.Build(
                XAxis.Channel, YAxis.Channel, ZAxis.Channel,
                _columns, _rows, firstSample, lastSample, _statistic, mask, against);

        if (Table.IsEmpty)
        {
            Hint = mask.FiltersApplied && mask.PassCount == 0
                ? "Every sample was filtered out — loosen or switch off a filter."
                : "No samples fall in this table — try a wider time range.";
            return;
        }

        int cells = Table.Columns * Table.Rows;
        var parts = new List<string>
        {
            $"{Table.SampleCount:N0} samples across {Table.PopulatedCells} of {cells} cells",
        };

        if (_tuneAxes is not null) parts.Add($"axes from {_tuneAxes.Name}");

        if (mask.FiltersApplied)
            parts.Add($"{mask.Total - mask.PassCount:N0} of {mask.Total:N0} excluded by filters");

        if (mask.UnknownChannels.Count > 0)
            parts.Add($"not in this log: {string.Join(", ", mask.UnknownChannels.Distinct())}");

        Hint = string.Join("   •   ", parts);
    }

    /// <summary>Picks sensible axes for a newly opened log.</summary>
    private void SeedHistogramAxes(LogDocument document)
    {
        AxisChannels.Clear();
        foreach (ChannelItem item in Channels
                     .Where(c => !c.IsFlat)
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            AxisChannels.Add(item);

        _xAxis = Pick("RPM") ?? AxisChannels.FirstOrDefault();
        _yAxis = Pick("MAP") ?? Pick("Load") ?? AxisChannels.Skip(1).FirstOrDefault();
        _zAxis = Pick("AFR") ?? Pick("Lambda") ?? AxisChannels.Skip(2).FirstOrDefault();
        _newFilterChannel = _xAxis;

        // Every channel, not just the ones that vary: a target is very often a
        // fixed value (a flat 14.7 stoich target), and that is precisely the
        // case worth measuring against.
        CompareOptions.Clear();
        CompareOptions.Add(CompareOption.None);
        foreach (ChannelItem item in Channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            CompareOptions.Add(new CompareOption(item.Name, item));

        _zCompare = CompareOption.None;

        // Offer the tune's own table axes when the log carries a tune.
        AxisSources.Clear();
        AxisSources.Add(AxisSourceOption.FromData);
        foreach (TuneAxisSet set in MsqTune.ReadAxisSets(document.EmbeddedTune))
            AxisSources.Add(new AxisSourceOption(set.Label, set));

        _axisSource = AxisSourceOption.FromData;

        Raise(nameof(XAxis));
        Raise(nameof(YAxis));
        Raise(nameof(ZAxis));
        Raise(nameof(NewFilterChannel));
        Raise(nameof(ZCompare));
        Raise(nameof(AxisSource));
        Raise(nameof(UsingTuneAxes));
        Raise(nameof(UsingDataAxes));
        Raise(nameof(HasTuneAxes));

        ChannelItem? Pick(string name) => AxisChannels.FirstOrDefault(
            c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void Load(string path)
    {
        LogDocument doc = LogReaderFactory.Load(path);

        Channels.Clear();
        _colorCursor = 0;

        // Added in file order; the collection view applies the chosen ordering.
        foreach (LogChannel channel in doc.Channels.Where(c => !ReferenceEquals(c, doc.Time)))
        {
            var item = new ChannelItem(channel, Palette[_colorCursor++ % Palette.Length]);
            item.VisibilityChanged += OnVisibilityChanged;
            Channels.Add(item);
        }

        Document = doc;

        foreach (string name in DefaultChannels)
        {
            ChannelItem? item = Channels.FirstOrDefault(
                c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !c.IsFlat);
            if (item is not null) item.IsVisible = true;
        }

        // Nothing matched the defaults, so fall back to the first few real signals.
        if (!Channels.Any(c => c.IsVisible))
            foreach (ChannelItem item in Channels.Where(c => !c.IsFlat).Take(4))
                item.IsVisible = true;

        Title = $"{Path.GetFileName(path)} — OpenLogViewer";
        Status = Describe(doc);
        SeedHistogramAxes(doc);
        SeedFilters(doc);
        RefreshView();
        PlotInvalidated?.Invoke();
        HistogramInvalidated?.Invoke();
    }

    private static string Describe(LogDocument doc)
    {
        var parts = new List<string>
        {
            doc.FormatName,
            $"{doc.Channels.Count} channels",
            $"{doc.SampleCount:N0} samples",
            $"{doc.Duration:F1} s",
        };

        if (doc.Markers.Count > 0) parts.Add($"{doc.Markers.Count} markers");
        if (doc.Signature is { Length: > 0 } sig) parts.Add(sig);

        return string.Join("   •   ", parts);
    }

    public void UpdateCursor(int index)
    {
        if (Document is not { } doc || index < 0) return;

        CursorTime = $"{doc.Time.At(index):F3} s   (sample {index:N0})";
        foreach (ChannelItem item in Channels) item.UpdateCursor(index);
    }

    /// <summary>
    /// Summarises every channel over a marked span, or restores the whole-log
    /// figures when the span is cleared.
    /// </summary>
    public void UpdateSelection((int First, int Last)? span)
    {
        if (Document is not { } doc) return;

        if (span is not { } range)
        {
            foreach (ChannelItem item in Channels) item.SetSelection(null);
            CursorTime = "—";
            ResetHint();
            return;
        }

        foreach (ChannelItem item in Channels)
            item.SetSelection(ChannelStatistics.Over(item.Channel, range.First, range.Last));

        double from = doc.Time.At(range.First);
        double to = doc.Time.At(range.Last);
        int count = range.Last - range.First + 1;

        CursorTime = $"{to - from:F3} s selected   ({count:N0} samples)";
        Hint = $"Marked {from:F2}–{to:F2} s. Rows show min … max and average over the span. " +
               "Click the plot to clear.";
    }

    public void SetAllVisible(bool visible)
    {
        // Showing acts on what the filter lists; clearing always clears the lot,
        // otherwise traces hidden by the filter would stay stuck on the plot.
        List<ChannelItem> targets = visible
            ? ChannelView.Cast<ChannelItem>().ToList()
            : [.. Channels];

        foreach (ChannelItem item in targets) item.IsVisible = visible;
    }

    /// <summary>Mirrors the plot's hovered trace into the channel list.</summary>
    public void HighlightChannel(ChannelItem? hovered)
    {
        foreach (ChannelItem item in Channels)
            item.IsHighlighted = ReferenceEquals(item, hovered);
    }

    /// <summary>Replaces the selection with the channels most logs are read for.</summary>
    public void PlotCommon()
    {
        foreach (ChannelItem item in Channels)
            item.IsVisible = item.Category == ChannelCategory.Common && !item.IsFlat;
    }

    // ----- presets ----------------------------------------------------------

    /// <summary>Saves the current selection under <paramref name="name"/>.</summary>
    public bool SavePreset(string name)
    {
        string[] plotted = Channels.Where(c => c.IsVisible).Select(c => c.Name).ToArray();
        if (plotted.Length == 0)
        {
            Hint = "Plot some channels first, then save them as a preset.";
            return false;
        }

        try
        {
            bool replacing = _store.Find(name) is not null;
            ChannelPreset saved = _store.Save(name, plotted);
            RefreshPresets();
            Hint = $"{(replacing ? "Updated" : "Saved")} preset “{saved.Name}” — {plotted.Length} channels.";
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
        {
            Hint = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Plots exactly the channels a preset names. A preset saved against another
    /// ECU may name nothing in this log, in which case the current selection is
    /// left alone rather than clearing the plot.
    /// </summary>
    public void ApplyPreset(ChannelPreset preset)
    {
        var wanted = new HashSet<string>(preset.Channels, StringComparer.OrdinalIgnoreCase);
        List<ChannelItem> matches = Channels.Where(c => wanted.Contains(c.Name)).ToList();

        if (matches.Count == 0)
        {
            Hint = $"“{preset.Name}” names no channel in this log, so nothing changed.";
            return;
        }

        foreach (ChannelItem item in Channels) item.IsVisible = false;
        foreach (ChannelItem item in matches) item.IsVisible = true;

        int missing = preset.Channels.Count - matches.Count;
        Hint = missing == 0
            ? $"Applied “{preset.Name}” — {matches.Count} channels."
            : $"Applied “{preset.Name}” — {matches.Count} of {preset.Channels.Count} channels; {missing} not in this log.";
    }

    public void DeletePreset(ChannelPreset preset)
    {
        if (!_store.Delete(preset.Name)) return;

        RefreshPresets();
        Hint = $"Deleted preset “{preset.Name}”.";
    }

    public void ResetHint() => Hint = DefaultHint;

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (ChannelPreset preset in _store.Presets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            Presets.Add(preset);
    }

    private void ApplySort()
    {
        using (ChannelView.DeferRefresh())
        {
            ChannelView.GroupDescriptions.Clear();
            ChannelView.SortDescriptions.Clear();

            switch (_sort)
            {
                case ChannelSort.Category:
                    ChannelView.GroupDescriptions.Add(
                        new PropertyGroupDescription(nameof(ChannelItem.CategoryName)));
                    ChannelView.SortDescriptions.Add(
                        new SortDescription(nameof(ChannelItem.CategoryOrder), ListSortDirection.Ascending));
                    ChannelView.SortDescriptions.Add(
                        new SortDescription(nameof(ChannelItem.Name), ListSortDirection.Ascending));
                    break;

                case ChannelSort.Plotted:
                    ChannelView.SortDescriptions.Add(
                        new SortDescription(nameof(ChannelItem.IsVisible), ListSortDirection.Descending));
                    ChannelView.SortDescriptions.Add(
                        new SortDescription(nameof(ChannelItem.Name), ListSortDirection.Ascending));
                    break;

                default:
                    ChannelView.SortDescriptions.Add(
                        new SortDescription(nameof(ChannelItem.Name), ListSortDirection.Ascending));
                    break;
            }
        }

        UpdateSummary();
    }

    private void RefreshView()
    {
        ChannelView.Refresh();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (Channels.Count == 0) { FilterSummary = ""; return; }

        int shown = ChannelView.Cast<object>().Count();
        int plotted = Channels.Count(c => c.IsVisible);
        int hidden = Channels.Count - shown;

        // Say how many are being withheld, so a missing channel is explained
        // rather than just absent.
        FilterSummary = hidden > 0
            ? $"{shown}/{Channels.Count} · {plotted} plotted · {hidden} hidden"
            : $"{shown}/{Channels.Count} · {plotted} plotted";
    }

    /// <summary>
    /// Assigning colours by list position leaves the plotted set looking alike,
    /// because the channels a tuner picks are scattered through the alphabet. So
    /// a colour is claimed when a channel is plotted, preferring one no other
    /// visible trace is already using.
    /// </summary>
    private void OnVisibilityChanged(ChannelItem item)
    {
        if (item.IsVisible)
        {
            HashSet<Color> taken = Channels
                .Where(c => c.IsVisible && !ReferenceEquals(c, item))
                .Select(c => c.Color)
                .ToHashSet();

            Color choice = Palette.FirstOrDefault(
                c => !taken.Contains(c), Palette[_colorCursor++ % Palette.Length]);
            item.SetColor(choice);
        }

        UpdateSummary();
        PlotInvalidated?.Invoke();
    }

    private bool FilterChannel(object obj)
    {
        if (obj is not ChannelItem item) return false;
        if (_search.Length > 0) return MatchesSearch(item);

        // A plotted channel always stays listed, so it can be switched back off.
        return !(_hideUnused && item.IsFlat);
    }

    /// <summary>
    /// Searching is an explicit request for a channel by name, so it overrides
    /// the unused filter. A channel pinned at one value is exactly what a tuner
    /// needs to notice — a wideband reading a constant 10.0 AFR is a dead sensor,
    /// not a boring channel — and silently hiding it from a search would bury it.
    /// </summary>
    private bool MatchesSearch(ChannelItem item) =>
        item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
        || item.Units.Contains(_search, StringComparison.OrdinalIgnoreCase)
        || item.CategoryName.Contains(_search, StringComparison.OrdinalIgnoreCase);
}
