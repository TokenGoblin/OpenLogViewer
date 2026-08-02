using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
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
    /// Trace colours, from this view model's own theme rather than the global
    /// one. <see cref="ThemeManager"/> exists to reach the chrome and the two
    /// self-drawn surfaces; reading the palette back out of it would make the
    /// colours depend on whoever set the theme last.
    /// </summary>
    private Color[] Palette => _theme.Series;

    /// <summary>Channels ticked on load, when the log has them.</summary>
    private static readonly string[] DefaultChannels = ["RPM", "MAP", "TPS", "AFR", "CLT"];

    private const string DefaultHint =
        "Scroll = zoom  •  Drag = pan  •  Double-click = fit  •  " +
        "Hover a trace for its min/max, then click either to jump there  •  " +
        "Shift-drag to mark a span and summarise it  •  Right-click for more";

    private readonly PresetStore _store;
    private readonly SettingsStore _settings;
    private readonly MathChannelStore _mathStore;

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

    /// <summary>
    /// Stores are injectable so tests can point them at a temporary directory
    /// rather than reading and writing the user's real settings.
    /// </summary>
    public MainViewModel(
        PresetStore? presets = null, FilterStore? filters = null,
        SettingsStore? settings = null, MathChannelStore? math = null)
    {
        _store = presets ?? new PresetStore();
        _filterStore = filters ?? new FilterStore();
        _settings = settings ?? new SettingsStore();
        _mathStore = math ?? new MathChannelStore();

        _theme = ThemeCatalog.Find(_settings.ThemeId);
        ThemeManager.Apply(_theme);

        ChannelView = CollectionViewSource.GetDefaultView(Channels);
        ChannelView.Filter = FilterChannel;
        ApplySort();
        RefreshPresets();
        RefreshMathChannels();
    }

    public IReadOnlyList<Theme> Themes => ThemeCatalog.Themes;

    private Theme _theme;

    /// <summary>
    /// The active colour scheme. Changing it recolours the traces as well as the
    /// chrome: a palette is chosen to sit against one particular background, so
    /// carrying the old one over to a new theme would undo the check that the
    /// traces stay apart from each other and from the ground.
    /// </summary>
    public Theme SelectedTheme
    {
        get => _theme;
        set { if (value is not null && SwitchTheme(value)) _settings.SetTheme(value.Id); }
    }

    /// <summary>
    /// Switches theme for this run only, without recording the choice — the
    /// <c>--theme</c> switch, which exists so the documentation shots can be
    /// taken without disturbing the user's setting.
    /// </summary>
    public void PreviewTheme(string? id) => SwitchTheme(ThemeCatalog.Find(id));

    private bool SwitchTheme(Theme theme)
    {
        if (!Set(ref _theme, theme, nameof(SelectedTheme))) return false;

        ThemeManager.Apply(theme);
        RecolorChannels();
        return true;
    }

    /// <summary>
    /// Hands out the new palette in the order channels are plotted, so what is on
    /// screen gets the widely separated entries rather than whatever the file
    /// order happens to give.
    /// </summary>
    private void RecolorChannels()
    {
        Color[] palette = Palette;
        int next = 0;

        foreach (ChannelItem item in Channels.Where(c => c.IsVisible))
            item.SetColor(palette[next++ % palette.Length]);

        foreach (ChannelItem item in Channels.Where(c => !c.IsVisible))
            item.SetColor(palette[next++ % palette.Length]);

        _colorCursor = next;
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
        private set
        {
            if (!Set(ref _document, value)) return;

            Raise(nameof(CanExport));
            Raise(nameof(CanExportPlotted));
        }
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
            Raise(nameof(VeAvailable));

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

    private readonly FilterStore _filterStore;
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
    // ----- VE Calibration -------------------------------------------------------

    private bool _veAnalyze;
    private bool _veShowSuggested;
    private int _veMinimumSamples = 12;
    private double _veMaxChange = 15;

    /// <summary>The last analysis, for the summary line and the export.</summary>
    public VeAnalysisResult? VeResult { get; private set; }

    /// <summary>
    /// Suggest a new fuel table from logged AFR against the AFR the tune was
    /// asking for. Needs the tune's own numbers, so it is only available once an
    /// axis source carrying them is picked.
    /// </summary>
    public bool VeAnalyze
    {
        get => _veAnalyze;
        set
        {
            if (!Set(ref _veAnalyze, value)) return;

            Raise(nameof(VeAvailable));
            HistogramInvalidated?.Invoke();
        }
    }

    /// <summary>Show the new numbers rather than how far each moves.</summary>
    public bool VeShowSuggested
    {
        get => _veShowSuggested;
        set { if (Set(ref _veShowSuggested, value)) HistogramInvalidated?.Invoke(); }
    }

    public int VeMinimumSamples
    {
        get => _veMinimumSamples;
        set { if (Set(ref _veMinimumSamples, Math.Max(1, value))) HistogramInvalidated?.Invoke(); }
    }

    public double VeMaxChange
    {
        get => _veMaxChange;
        set { if (Set(ref _veMaxChange, Math.Clamp(value, 1, 100))) HistogramInvalidated?.Invoke(); }
    }

    /// <summary>True when the picked axis source brought the tune's values with it.</summary>
    public bool VeAvailable => _axisSource.HasValues;

    public string VeSummary { get; private set; } = "";

    /// <summary>
    /// Runs the analysis, or explains why it cannot. Returns false to fall back
    /// to an ordinary binned table, so switching to VE Calibration without the parts
    /// it needs still shows something rather than an empty panel.
    /// </summary>
    private bool BuildVeAnalysis(int firstSample, int lastSample, SampleMask mask, LogChannel? target)
    {
        if (_axisSource.Table is not { } tune)
        {
            VeSummary = "Pick one of the tune's own tables above — VE Calibration needs its numbers.";
            Raise(nameof(VeSummary));
            return false;
        }

        if (target is null)
        {
            VeSummary = "Set \"Compare against\" to the AFR target channel.";
            Raise(nameof(VeSummary));
            return false;
        }

        VeAnalysisResult result = VeAnalysis.Analyse(
            tune, XAxis!.Channel, YAxis!.Channel, ZAxis!.Channel, target,
            firstSample, lastSample, mask,
            new VeAnalysisSettings { MinimumSamples = _veMinimumSamples, MaxChangePercent = _veMaxChange });

        VeResult = result;

        Table = _veShowSuggested
            ? result.AsSuggestedTable(XAxis.Channel, YAxis.Channel, ZAxis.Channel, firstSample, lastSample, mask)
            : result.AsChangeTable(XAxis.Channel, YAxis.Channel, ZAxis.Channel, target, firstSample, lastSample, mask);

        VeSummary = result.IsEmpty
            ? $"Nothing to suggest — no cell reached {_veMinimumSamples} samples."
            : $"{result.CellsSuggested} of {tune.Columns * tune.Rows} cells, " +
              $"{result.CellsThin} too thin, largest change {result.LargestChangePercent:F1}%, " +
              $"from {result.SamplesUsed:N0} samples";

        Hint = result.IsEmpty
            ? "No cell has enough samples yet. Lower the sample threshold, or drive the untouched areas."
            : $"Suggesting {result.CellsSuggested} cells of {tune.Name}. " +
              "Cells with too little data are left alone. Export the table to paste it into your tuning app.";

        Raise(nameof(VeSummary));
        return true;
    }

    public void RebuildHistogram(int firstSample, int lastSample)
    {
        if (Document is null || XAxis is null || YAxis is null || ZAxis is null)
        {
            Table = null;
            return;
        }

        SampleMask mask = SampleFilter.Build(Document, Filters.Select(f => f.Filter));

        LogChannel? against = _zCompare.Channel?.Channel;

        if (_veAnalyze && BuildVeAnalysis(firstSample, lastSample, mask, against)) return;

        VeResult = null;

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

        // Offer the tune's own table axes when the log carries a tune, carrying
        // the table's values through where they could be read — VE Calibration needs
        // the numbers, not just the grid.
        AxisSources.Clear();
        AxisSources.Add(AxisSourceOption.FromData);

        Dictionary<string, TuneTable> withValues = MsqTune.ReadTables(document.EmbeddedTune)
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (TuneAxisSet set in MsqTune.ReadAxisSets(document.EmbeddedTune))
            AxisSources.Add(new AxisSourceOption(
                set.Label, set, withValues.GetValueOrDefault(set.Name)));

        _axisSource = AxisSourceOption.FromData;
        _veAnalyze = false;

        Raise(nameof(XAxis));
        Raise(nameof(YAxis));
        Raise(nameof(ZAxis));
        Raise(nameof(NewFilterChannel));
        Raise(nameof(ZCompare));
        Raise(nameof(AxisSource));
        Raise(nameof(UsingTuneAxes));
        Raise(nameof(UsingDataAxes));
        Raise(nameof(HasTuneAxes));
        Raise(nameof(VeAnalyze));
        Raise(nameof(VeAvailable));

        ChannelItem? Pick(string name) => AxisChannels.FirstOrDefault(
            c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void Load(string path)
    {
        LogDocument doc = LogReaderFactory.Load(path);

        Channels.Clear();
        _colorCursor = 0;

        // Added in file order; the collection view applies the chosen ordering.
        foreach (LogChannel channel in doc.Channels.Where(c => !doc.IsTimeBase(c)))
            Add(channel, calculated: false);

        // Appended so they behave like any other channel from here on: plottable,
        // usable as a histogram axis, and available to filters.
        MathChannelResult math = MathChannelBuilder.Build(doc, _mathStore.Channels);
        foreach (LogChannel channel in math.Channels) Add(channel, calculated: true);

        _mathProblems = math.Problems;
        RefreshMathChannels();

        Document = doc;

        void Add(LogChannel channel, bool calculated)
        {
            var item = new ChannelItem(channel, Palette[_colorCursor++ % Palette.Length])
            {
                IsCalculated = calculated,
            };

            item.VisibilityChanged += OnVisibilityChanged;
            Channels.Add(item);
        }

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

        Selection = span;
        Raise(nameof(ExportScope));

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

    // ----- calculated channels ---------------------------------------------

    private IReadOnlyList<MathChannelProblem> _mathProblems = [];
    private string _newMathName = "";
    private string _newMathUnits = "";
    private string _newMathExpression = "";
    private MathChannel? _editing;
    private bool _editWasPlotted;

    /// <summary>The user's definitions, for the sidebar list.</summary>
    public ObservableCollection<MathChannel> MathChannels { get; } = [];

    public string NewMathName
    {
        get => _newMathName;
        set { if (Set(ref _newMathName, value)) Raise(nameof(MathPreview)); }
    }

    public string NewMathUnits
    {
        get => _newMathUnits;
        set => Set(ref _newMathUnits, value);
    }

    public string NewMathExpression
    {
        get => _newMathExpression;
        set { if (Set(ref _newMathExpression, value)) Raise(nameof(MathPreview)); }
    }

    /// <summary>
    /// Checks the expression as it is typed and says what it would produce, so a
    /// mistake is caught while the cursor is still on it rather than after the
    /// channel has been created and plotted as a flat line of nothing.
    /// </summary>
    public string MathPreview
    {
        get
        {
            if (_newMathExpression.Trim().Length == 0) return "Type an expression, e.g. AFR - AFR Target 1";
            if (Document is not { } doc) return "Open a log to check this expression.";

            IEnumerable<string> names = Channels.Select(c => c.Name).Append(doc.Time.Name);

            if (!MathExpression.TryParse(_newMathExpression, names, out MathExpression? expression, out string? error))
                return error!;

            LogChannel? sample = Preview(doc, expression!);
            if (sample is null) return "Reads: " + string.Join(", ", expression!.References);

            return sample.IsFlat
                ? $"Constant {sample.Format(sample.Min)}"
                : $"Ranges {sample.Format(sample.Min)} … {sample.Format(sample.Max)}";
        }
    }

    /// <summary>Evaluates a candidate over the open log without adding it.</summary>
    private LogChannel? Preview(LogDocument doc, MathExpression expression)
    {
        MathChannelResult result = MathChannelBuilder.Build(doc,
            [new MathChannel { Name = PreviewName(doc), Units = _newMathUnits, Expression = _newMathExpression }]);

        return result.Channels.Count > 0 ? result.Channels[0] : null;

        // A name that cannot collide with a real one, since the name being typed
        // may well already be taken at this instant.
        static string PreviewName(LogDocument doc) => " preview";
    }

    /// <summary>Definitions that could not be applied to the open log.</summary>
    public string MathProblems => _mathProblems.Count == 0
        ? ""
        : string.Join("   •   ", _mathProblems.Select(p => $"{p.Name}: {p.Reason}"));

    public bool HasMathProblems => _mathProblems.Count > 0;

    public void AddMathChannel()
    {
        string name = _newMathName.Trim();
        if (name.Length == 0 || _newMathExpression.Trim().Length == 0) return;

        _mathStore.Add(new MathChannel
        {
            Name = name,
            Units = _newMathUnits.Trim(),
            Expression = _newMathExpression.Trim(),
            Digits = 2,
        });

        NewMathName = "";
        NewMathUnits = "";
        NewMathExpression = "";

        RefreshMathChannels();
        Reapply();

        // Editing is a remove and a re-add, so a channel that was on the plot has
        // to be put back on it — including under a new name, if it was renamed.
        if (_editWasPlotted)
        {
            _editWasPlotted = false;

            ChannelItem? added = Channels.FirstOrDefault(
                c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (added is not null) added.IsVisible = true;
        }

        Hint = $"Added the calculated channel \"{name}\".";
    }

    public void RemoveMathChannel(MathChannel channel)
    {
        if (!_mathStore.Remove(channel)) return;

        RefreshMathChannels();
        Reapply();
        Hint = $"Removed \"{channel.Name}\".";
    }

    /// <summary>Loads the definition back into the editor so it can be changed.</summary>
    public void EditMathChannel(MathChannel channel)
    {
        _editing = channel;
        _editWasPlotted = Channels.Any(
            c => c.IsVisible && c.Name.Equals(channel.Name, StringComparison.OrdinalIgnoreCase));

        NewMathName = channel.Name;
        NewMathUnits = channel.Units;
        NewMathExpression = channel.Expression;

        _mathStore.Remove(channel);
        RefreshMathChannels();
        Reapply();
    }

    /// <summary>
    /// Abandons an edit. Opening the editor removes the definition, so cancelling
    /// has to put the original back — not whatever was half-typed over it.
    /// </summary>
    public void CancelMathEdit()
    {
        MathChannel? original = _editing;

        _editing = null;
        NewMathName = "";
        NewMathUnits = "";
        NewMathExpression = "";

        if (original is null)
        {
            _editWasPlotted = false;
            return;
        }

        _mathStore.Add(original);
        RefreshMathChannels();
        Reapply();

        if (!_editWasPlotted) return;
        _editWasPlotted = false;

        ChannelItem? restored = Channels.FirstOrDefault(
            c => c.Name.Equals(original.Name, StringComparison.OrdinalIgnoreCase));
        if (restored is not null) restored.IsVisible = true;
    }

    private void RefreshMathChannels()
    {
        MathChannels.Clear();
        foreach (MathChannel channel in _mathStore.Channels) MathChannels.Add(channel);

        Raise(nameof(MathProblems));
        Raise(nameof(HasMathProblems));
        Raise(nameof(HasMathChannels));
    }

    public bool HasMathChannels => MathChannels.Count > 0;

    /// <summary>
    /// Rebuilds the calculated channels over the open log, keeping what was
    /// plotted plotted. Reloading the file would be simpler but would throw away
    /// the zoom, the marked span and the selection along with it.
    /// </summary>
    private void Reapply()
    {
        if (Document is not { } doc) return;

        var plotted = Channels.Where(c => c.IsVisible).Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Batch(() =>
        {
            foreach (ChannelItem item in Channels.Where(c => c.IsCalculated).ToList())
            {
                item.IsVisible = false;
                Channels.Remove(item);
            }

            MathChannelResult math = MathChannelBuilder.Build(doc, _mathStore.Channels);
            _mathProblems = math.Problems;

            foreach (LogChannel channel in math.Channels)
            {
                var item = new ChannelItem(channel, Palette[_colorCursor++ % Palette.Length])
                {
                    IsCalculated = true,
                };

                item.VisibilityChanged += OnVisibilityChanged;
                Channels.Add(item);

                if (plotted.Contains(channel.Name)) item.IsVisible = true;
            }
        });

        Raise(nameof(MathProblems));
        Raise(nameof(HasMathProblems));
        SeedHistogramAxes(doc);
        HistogramInvalidated?.Invoke();
    }

    /// <summary>Marked sample span, or null when the whole log is in scope.</summary>
    public (int First, int Last)? Selection { get; private set; }

    public bool CanExport => Document is not null;

    public bool CanExportPlotted => Channels.Any(c => c.IsVisible);

    /// <summary>
    /// What an export would cover, for the menu to say so before it happens —
    /// exporting forty minutes when the user meant the marked corner is a slow
    /// mistake to notice.
    /// </summary>
    public string ExportScope =>
        Selection is { } span
            ? $"marked span, {span.Last - span.First + 1:N0} samples"
            : "whole log";

    /// <summary>A name beside the log's own, so exports land together and sort together.</summary>
    public string SuggestExportName(string suffix, string extension)
    {
        string stem = Document is { FilePath.Length: > 0 } doc
            ? Path.GetFileNameWithoutExtension(doc.FilePath)
            : "log";

        return $"{stem}-{suffix}{extension}";
    }

    /// <summary>
    /// Writes the log as CSV over the marked span, or all of it when nothing is
    /// marked. Channels keep the log's own order rather than the sidebar's, so
    /// the file does not change shape with the sort the user happens to be on.
    /// </summary>
    public void ExportLogCsv(string path, bool plottedOnly)
    {
        if (Document is not { } doc) return;

        List<LogChannel> channels =
        [
            .. Channels.Where(c => !plottedOnly || c.IsVisible).Select(c => c.Channel)
        ];

        (int first, int last) = Selection ?? (0, doc.SampleCount - 1);

        WriteAtomic(path, writer => CsvExport.WriteLog(writer, doc, channels, first, last));

        Hint = $"Saved {channels.Count} channel{(channels.Count == 1 ? "" : "s")} " +
               $"over the {ExportScope} to {Path.GetFileName(path)}";
    }

    /// <summary>
    /// Reports a save the window carried out itself. Rendering a view to an image
    /// needs the visual tree, so that one export cannot live here, but its
    /// outcome still belongs in the same place as every other.
    /// </summary>
    public void ReportSaved(string path, string what) =>
        Hint = $"Saved the {what} to {Path.GetFileName(path)}";

    /// <summary>Writes the current heat table, either its values or its sample counts.</summary>
    public void ExportTableCsv(string path, bool counts)
    {
        if (Table is not { } table) return;

        WriteAtomic(path, writer =>
        {
            if (counts) CsvExport.WriteTableCounts(writer, table);
            else CsvExport.WriteTable(writer, table);
        });

        Hint = $"Saved the {table.Columns}×{table.Rows} table to {Path.GetFileName(path)}";
    }

    /// <summary>
    /// Written to a temporary file and moved into place. An export interrupted
    /// part way leaves the previous file intact rather than a truncated one that
    /// still looks like a successful save.
    /// </summary>
    private static void WriteAtomic(string path, Action<TextWriter> write)
    {
        string temporary = path + ".tmp";

        // No BOM: it is the first thing a spreadsheet or a parser sees, and
        // plenty of them read it as part of the first column name.
        using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
            write(writer);

        File.Move(temporary, path, overwrite: true);
    }

    public void SetAllVisible(bool visible)
    {
        // Showing acts on what the filter lists; clearing always clears the lot,
        // otherwise traces hidden by the filter would stay stuck on the plot.
        List<ChannelItem> targets = visible
            ? ChannelView.Cast<ChannelItem>().ToList()
            : [.. Channels];

        Batch(() =>
        {
            foreach (ChannelItem item in targets) item.IsVisible = visible;
        });
    }

    /// <summary>Mirrors the plot's hovered trace into the channel list.</summary>
    public void HighlightChannel(ChannelItem? hovered)
    {
        foreach (ChannelItem item in Channels)
            item.IsHighlighted = ReferenceEquals(item, hovered);
    }

    /// <summary>Replaces the selection with the channels most logs are read for.</summary>
    public void PlotCommon() => Batch(() =>
    {
        foreach (ChannelItem item in Channels)
            item.IsVisible = item.Category == ChannelCategory.Common && !item.IsFlat;
    });

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

        Batch(() =>
        {
            foreach (ChannelItem item in Channels) item.IsVisible = false;
            foreach (ChannelItem item in matches) item.IsVisible = true;
        });

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

    /// <summary>
    /// Explains what a traced cell actually covers. The count of visits is the
    /// important part: one cell averaged over twelve separate passes is a very
    /// different thing from one sustained stretch, and the table cannot show that.
    /// </summary>
    public void DescribeCellTrace(
        HistogramTable table,
        (int Column, int Row) cell,
        IReadOnlyList<(int First, int Last)> visits,
        (int First, int Last) longest)
    {
        int samples = visits.Sum(v => v.Last - v.First + 1);
        string where = $"{table.X.Name} {table.ColumnCenters[cell.Column]:G6} · " +
                       $"{table.Y.Name} {table.RowCenters[cell.Row]:G6}";

        string visitText = visits.Count == 1
            ? "one visit"
            : $"{visits.Count} visits, all marked";

        Hint = $"{where} — {table.Format(cell.Column, cell.Row)} from {samples:N0} samples over " +
               $"{visitText}. Showing the longest ({longest.Last - longest.First + 1:N0} samples).";
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
    private bool _batching;

    /// <summary>
    /// Runs a bulk visibility change as one update. Without this, switching a
    /// preset on a 179-channel log redraws the plot and recounts the list once
    /// per channel rather than once in total.
    /// </summary>
    private void Batch(Action work)
    {
        _batching = true;
        try { work(); }
        finally { _batching = false; }

        RefreshView();
        Raise(nameof(CanExportPlotted));
        PlotInvalidated?.Invoke();
    }

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

        if (_batching) return;

        // Plotting a constant channel changes whether the filter withholds it,
        // so the list has to be re-evaluated or the row it needs never appears.
        if (_hideUnused && item.IsFlat) ChannelView.Refresh();

        Raise(nameof(CanExportPlotted));
        UpdateSummary();
        PlotInvalidated?.Invoke();
    }

    private bool FilterChannel(object obj)
    {
        if (obj is not ChannelItem item) return false;
        if (_search.Length > 0) return MatchesSearch(item);

        // A plotted channel always stays listed, whatever the filter says: a
        // trace on screen with no row to untick it is a trap.
        return !(_hideUnused && item.IsFlat && !item.IsVisible);
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



