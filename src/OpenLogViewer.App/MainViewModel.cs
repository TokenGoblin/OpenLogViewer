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

        Workspace = new Workspace(_settings.DataFolder);
        SerialPortNames.Recall(_settings.KnownEcus);

        _theme = ThemeCatalog.Find(_settings.ThemeId);
        ThemeManager.Apply(_theme);

        ChannelView = CollectionViewSource.GetDefaultView(Channels);
        ChannelView.Filter = FilterChannel;
        ApplySort();
        RefreshPresets();
        RefreshMathChannels();
    }

    /// <summary>Where recordings and exports go.</summary>
    public Workspace Workspace { get; private set; } = null!;

    /// <summary>The folder itself, for the menu to show and for opening.</summary>
    public string DataFolder => Workspace.Root;

    /// <summary>
    /// Moves the workspace. Existing files stay where they are: moving a user's
    /// recordings without being asked is not a settings change, and the old
    /// folder is still a folder they can open.
    /// </summary>
    public void SetDataFolder(string? folder)
    {
        if (folder is not null && !Workspace.IsUsable(folder))
            throw new IOException($"{folder} cannot be written to.");

        _settings.SetDataFolder(folder);
        Workspace = new Workspace(_settings.DataFolder);

        Raise(nameof(DataFolder));
        Hint = $"Recordings and exports now go to {Workspace.Root}.";
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

    /// <summary>Says what is happening, for a step the window has to stay alive through.</summary>
    public void SetHint(string text) => Hint = text;

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
                // Where the firmware names the channels it looks the table up
                // by, take it at its word; units are a guess by comparison, and
                // on rusEFI the load axis has no units to guess from.
                _xAxis = Channel(value.XChannel) ?? KeepOrMatch(_xAxis, axes.X.Units, "rpm");
                _yAxis = Channel(value.YChannel) ?? KeepOrMatch(_yAxis, axes.Y.Units, "kpa");
                Raise(nameof(XAxis));
                Raise(nameof(YAxis));
            }

            HistogramInvalidated?.Invoke();
        }
    }

    private TuneAxisSet? _tuneAxes => _axisSource.Axes;

    /// <summary>
    /// Offers the tables read off the ECU as breakpoint sources.
    ///
    /// Listed after any from a file and marked as coming from the controller,
    /// because the two can disagree and which one you are calibrating against
    /// changes the answer. Each carries the channels the firmware indexes it by,
    /// so picking one also sets the axes correctly.
    /// </summary>
    private void AddEcuAxisSources()
    {
        var offered = new List<AxisSourceOption>();

        foreach (TuneTable table in EcuTables)
        {
            TableDefinition? definition = _ecuTableDefinitions
                .FirstOrDefault(d => d.Title.Equals(table.Name, StringComparison.OrdinalIgnoreCase));

            string? x = LogName(definition?.XChannel);
            string? y = LogName(definition?.YChannel);

            // A table whose axes this log does not carry cannot be binned
            // against, so offering it would only be a way to produce an empty
            // grid. Seventy-five of them would also bury the handful that work.
            if (!Logged(x) || !Logged(y)) continue;

            offered.Add(new AxisSourceOption($"{table.Label} — from the ECU", table.Axes, table)
            {
                XChannel = x,
                YChannel = y,
            });
        }

        // The fuel table first: it is what VE Calibration is for, and on rusEFI
        // it is one of a few dozen otherwise indistinguishable entries.
        foreach (AxisSourceOption option in offered
                     .OrderByDescending(o => o.Label.Contains("VE ", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(o => o.Label, StringComparer.OrdinalIgnoreCase))
            AxisSources.Add(option);
    }

    private bool Logged(string? channel) =>
        channel is not null and not ""
        && Channels.Any(c => c.Name.Equals(channel, StringComparison.OrdinalIgnoreCase));

    /// <summary>Log channels the ECU indexes one of its tables by.</summary>
    private HashSet<string> EcuAxisChannels()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TableDefinition definition in _ecuTableDefinitions)
        {
            if (LogName(definition.XChannel) is { } x) names.Add(x);
            if (LogName(definition.YChannel) is { } y) names.Add(y);
        }

        return names;
    }

    /// <summary>An internal channel name as the log records it.</summary>
    private string? LogName(string? channel) =>
        channel is null or "" ? null : _logNames.GetValueOrDefault(channel, channel);

    /// <summary>An axis channel by name, or null when this log has no such thing.</summary>
    private ChannelItem? Channel(string? name) =>
        name is null or "" ? null
        : AxisChannels.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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

    // ----- top-level mode -----------------------------------------------------

    private WorkspaceMode _mode = WorkspaceMode.Log;

    /// <summary>
    /// What the window is for right now.
    ///
    /// Above the Log/Histogram choice rather than beside it: those are two ways
    /// of reading the same recording, while gauges and calibration are different
    /// jobs that happen to use the same connection.
    /// </summary>
    public WorkspaceMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;

            Raise(nameof(InLogMode));
            Raise(nameof(InGaugeMode));
            Raise(nameof(InCalibrationMode));
        }
    }

    public bool InLogMode
    {
        get => _mode == WorkspaceMode.Log;
        set { if (value) Mode = WorkspaceMode.Log; }
    }

    public bool InGaugeMode
    {
        get => _mode == WorkspaceMode.Gauges;
        set { if (value) Mode = WorkspaceMode.Gauges; }
    }

    public bool InCalibrationMode
    {
        get => _mode == WorkspaceMode.Calibration;
        set { if (value) Mode = WorkspaceMode.Calibration; }
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
    // ----- live connection --------------------------------------------------

    private LiveSession? _live;
    private string _liveStatus = "";
    private string _livePort = "";
    private string _liveSignature = "";
    private string _liveVersion = "";
    private string _liveIni = "";
    private string _liveRecording = "";

    /// <summary>True while connected to an ECU.</summary>
    public bool IsLive => _live is { IsRunning: true };

    /// <summary>
    /// What is connected and how it is doing, for the toolbar.
    ///
    /// The port and the firmware lead, because that is the question being
    /// asked — a rate alone says the link is working without saying what it is
    /// working with, and a wrong firmware match is the failure that matters.
    /// </summary>
    public string LiveStatus
    {
        get => _liveStatus;
        private set => Set(ref _liveStatus, value);
    }

    private bool _liveHealthy = true;

    /// <summary>False while the link is down and being waited on.</summary>
    public bool LiveHealthy
    {
        get => _liveHealthy;
        private set => Set(ref _liveHealthy, value);
    }

    /// <summary>The whole picture, for the tooltip: everything the toolbar trims.</summary>
    /// <summary>
    /// Which units readings are shown in.
    ///
    /// Display only. Recordings keep the units the ECU reported, so a log is
    /// always in its own ECU's units and reopening one later cannot convert it
    /// twice — and a session started in one system and finished in the other is
    /// still a single coherent file.
    /// </summary>
    public UnitSystem Units
    {
        get => _settings.Units;
        set
        {
            if (value == _settings.Units) return;

            _settings.SetUnits(value);

            foreach (GaugeItem gauge in AllGauges) gauge.Show(value);

            Raise(nameof(Units));
            Raise(nameof(UnitsLabel));

            foreach (ChannelItem channel in Channels) channel.Show(value);
        }
    }

    /// <summary>What the units setting is called, for a menu that shows it.</summary>
    public string UnitsLabel => Units switch
    {
        UnitSystem.Metric => "Metric",
        UnitSystem.Imperial => "Imperial",
        _ => "As reported",
    };

    /// <summary>Every system offered, in the order worth listing.</summary>
    public static IReadOnlyList<UnitSystem> UnitSystems { get; } =
        [UnitSystem.AsReported, UnitSystem.Metric, UnitSystem.Imperial];

    /// <summary>
    /// The channels the running session is recording, or nothing when there is
    /// no session.
    ///
    /// A gauge is fed by name, so this is what a gauge's column has to be one of.
    /// Twice now a gauge has been paired with a column that no session records —
    /// a face that never shows a number, which looks exactly like a channel that
    /// is not being read.
    /// </summary>
    public IReadOnlyList<string> LiveChannelNames => _live?.Names ?? [];

    public string LiveDetail => IsLive
        ? string.Join(Environment.NewLine,
        [
            $"Port      {_livePort}",
            $"Firmware  {_liveSignature}",
            $"Build     {_liveVersion}",
            $"INI       {_liveIni}",
            $"Channels  {_live!.Names.Count}",
            $"Recording {_liveRecording}",
        ])
        : "Not connected.";

    // ----- the tune in the ECU ------------------------------------------------

    private EcuTune? _ecuTune;

    /// <summary>
    /// The live connection, kept so the tune can be written as well as read.
    ///
    /// Held rather than passed around because writing happens long after
    /// connecting — the user reads a tune, looks at it, changes a corner of one
    /// table, and sends it. Cleared on disconnect, which is what stops a write
    /// being attempted down a port that has gone.
    /// </summary>
    private EcuConnection? _ecuConnection;
    private string _ecuTuneSummary = "";

    /// <summary>
    /// Internal channel names to the names a session records them under.
    ///
    /// The firmware talks about <c>RPMValue</c> throughout — in gauges, in table
    /// axes — while a log calls it "RPM", because those are the names a saved
    /// preset was written against. Everything crossing between the two goes
    /// through here.
    /// </summary>
    private IReadOnlyDictionary<string, string> _logNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Table definitions from the INI, paired with the tables read off the ECU.</summary>
    private IReadOnlyList<TableDefinition> _ecuTableDefinitions = [];

    /// <summary>The TunerStudio project's tune, when the matched INI came from one.</summary>
    private string? _projectTune;

    /// <summary>The tables read off the ECU, empty until a connection provides them.</summary>
    public ObservableCollection<TuneTable> EcuTables { get; } = [];

    /// <summary>
    /// The tables in the order they are worth looking at.
    ///
    /// Biggest first, which is not arbitrary: a firmware's main tables are its
    /// finest-grained ones, so the 16×16 fuel and ignition maps rise above a
    /// drawer of 4×4 corrections and 8×8 blends. rusEFI declares seventy-six in
    /// an order that suits the file rather than the reader, with the fuel table
    /// fortieth.
    ///
    /// Fuel ahead of the rest at the same size, since calibrating it is what
    /// most of this program is for.
    /// </summary>
    private static IEnumerable<TuneTable> Ordered(IEnumerable<TuneTable> tables) =>
        tables
            .OrderByDescending(t => t.Columns * t.Rows)
            .ThenByDescending(t => t.Name.Contains("VE", StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    private string _ecuTableSearch = "";
    private ICollectionView? _ecuTableView;

    /// <summary>The table list, filtered by <see cref="EcuTableSearch"/>.</summary>
    public ICollectionView EcuTableChoices
    {
        get
        {
            if (_ecuTableView is not null) return _ecuTableView;

            _ecuTableView = CollectionViewSource.GetDefaultView(EcuTables);
            _ecuTableView.Filter = o =>
                o is TuneTable table
                && (_ecuTableSearch.Length == 0
                    || table.Name.Contains(_ecuTableSearch, StringComparison.OrdinalIgnoreCase));

            return _ecuTableView;
        }
    }

    public string EcuTableSearch
    {
        get => _ecuTableSearch;
        set
        {
            if (!Set(ref _ecuTableSearch, value)) return;

            EcuTableChoices.Refresh();
            Raise(nameof(EcuTableSummary));
        }
    }

    /// <summary>How much of the list the search is showing.</summary>
    public string EcuTableSummary
    {
        get
        {
            if (EcuTables.Count == 0) return "";

            int shown = EcuTableChoices.Cast<object>().Count();

            return shown == EcuTables.Count
                ? $"{EcuTables.Count} tables"
                : $"{shown} of {EcuTables.Count} tables";
        }
    }

    private TuneTable? _selectedEcuTable;

    /// <summary>The table on screen in calibration mode.</summary>
    public TuneTable? SelectedEcuTable
    {
        get => _selectedEcuTable;
        set
        {
            if (!Set(ref _selectedEcuTable, value)) return;

            // A fresh edit per table. Changes are deliberately not carried
            // between tables: an unsent change to one table left pending while
            // another is being looked at is a change nobody can see.
            TableEdit = value is null ? null : new TuneEdit(value, ConstantFor(value));
            SelectedCells = TuneSelection.Cell(0, 0);
        }
    }

    private TuneEdit? _tableEdit;

    /// <summary>The selected table, with whatever has been changed in it.</summary>
    public TuneEdit? TableEdit
    {
        get => _tableEdit;
        private set
        {
            if (!Set(ref _tableEdit, value)) return;

            Raise(nameof(HasTableChanges));
            Raise(nameof(TableEditSummary));
            Raise(nameof(CanWriteTable));
        }
    }

    private TuneSelection _cells = TuneSelection.Cell(0, 0);

    /// <summary>
    /// The block of cells being worked on. Named for the table rather than
    /// simply "selection", which on this view model already means the span
    /// marked out on the plot.
    /// </summary>
    public TuneSelection SelectedCells
    {
        get => _cells;
        set { if (Set(ref _cells, value)) Raise(nameof(TableEditSummary)); }
    }

    /// <summary>The firmware's description of a table's cells, for its limits and precision.</summary>
    private TuneConstant? ConstantFor(TuneTable table) =>
        _ecuTableDefinitions.FirstOrDefault(d => d.Title == table.Name) is { Values.Length: > 0 } definition
            ? _tuneLayout?.Constants.FirstOrDefault(c => c.Name == definition.Values)
            : null;

    private TuneLayout? _tuneLayout;

    public bool HasTableChanges => TableEdit?.HasChanges == true;

    /// <summary>
    /// Whether a table could be sent: something changed, a live connection, and
    /// a firmware that admits to being writable.
    /// </summary>
    public bool CanWriteTable =>
        HasTableChanges && _ecuConnection is not null && _tuneLayout is not null;

    /// <summary>What is selected and what has been changed, for the calibration header.</summary>
    public string TableEditSummary
    {
        get
        {
            if (TableEdit is not { } edit) return "";

            TuneSelection area = SelectedCells.ClampedTo(edit.Columns, edit.Rows);

            string where = area.Count == 1
                ? $"{edit.Table.X.Breakpoints[area.Left]:G5} × {edit.Table.Y.Breakpoints[area.Top]:G5}"
                : $"{area.Columns}×{area.Rows} cells";

            string value = area.Count == 1
                ? $"  =  {edit[area.Left, area.Top].ToString("0." + new string('#', Math.Max(1, edit.Digits)))} {edit.Units}"
                : "";

            string changed = edit.ChangedCount switch
            {
                0 => "no changes",
                1 => "1 cell changed, not sent",
                var n => $"{n} cells changed, not sent",
            };

            return $"{where}{value}   ·   {changed}";
        }
    }

    /// <summary>Applies a keyboard edit to whatever is selected.</summary>
    public void EditTable(TuneTableEdit change)
    {
        if (TableEdit is not { } edit) return;

        switch (change.Kind)
        {
            case TuneEditKind.Add: edit.Add(SelectedCells, change.Amount); break;
            case TuneEditKind.Scale: edit.Scale(SelectedCells, change.Amount); break;
            case TuneEditKind.Revert: edit.Revert(SelectedCells); break;
        }

        AfterTableEdit();
    }

    /// <summary>Sets every selected cell to one value.</summary>
    public void SetTableCells(double value)
    {
        TableEdit?.Set(SelectedCells, value);
        AfterTableEdit();
    }

    public void RevertTable()
    {
        TableEdit?.Revert();
        AfterTableEdit();
    }

    private void AfterTableEdit()
    {
        Raise(nameof(TableEdit));
        Raise(nameof(HasTableChanges));
        Raise(nameof(CanWriteTable));
        Raise(nameof(TableEditSummary));
    }

    /// <summary>
    /// Sends the edited table to the controller's working memory.
    ///
    /// This takes effect at once on a running engine, and it is not permanent:
    /// the ECU forgets it at the next power cycle unless it is burned. That is
    /// the ECU's own arrangement rather than a safety net added here, but it is
    /// a real one — a change that turns out to be wrong is undone by turning the
    /// key off.
    ///
    /// The connection checks the write by reading the same bytes back, so a
    /// write that returns without complaint is a write the ECU has taken.
    /// </summary>
    public string WriteTableToEcu()
    {
        if (TableEdit is not { } edit) return "No table is open.";
        if (_ecuConnection is not { } connection) return "Not connected to an ECU.";
        if (_ecuTune is not { } tune || _tuneLayout is not { } layout) return "No tune has been read.";
        if (!edit.HasChanges) return "Nothing has been changed.";

        if (edit.Encode(tune) is not { } write)
            return "This table cannot be encoded for this firmware, so nothing was sent.";

        TunePage? page = layout.Pages.FirstOrDefault(p => p.Index == write.Page);
        if (page is null) return $"The firmware declares no page {write.Page}.";

        if (page.ChunkWriteCommand.Length == 0)
            return $"This firmware declares no way to write page {write.Page}.";

        try
        {
            connection.WriteTunePage(
                page, layout.BlockingFactor, layout.LittleEndian, write.Offset, write.Data);

            // The tune held in memory has to move with the ECU, or the next
            // read-modify-write would be against stale bytes and would undo
            // this one.
            tune.Accept(write);

            int cells = edit.ChangedCount;
            SelectedEcuTable = RereadTable(edit.Name) ?? SelectedEcuTable;

            return $"Sent {cells} changed cell{(cells == 1 ? "" : "s")} to the ECU. "
                   + "It is running this now, and will forget it at the next power cycle "
                   + "unless you burn it.";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            return $"The write failed: {e.Message}";
        }
    }

    /// <summary>
    /// Commits the page holding this table to the controller's flash.
    ///
    /// Separate from writing because it is separate on the ECU, and because the
    /// difference is the only thing standing between a change that can be undone
    /// with the ignition key and one that cannot.
    /// </summary>
    public string BurnTableToEcu()
    {
        if (TableEdit is not { } edit) return "No table is open.";
        if (_ecuConnection is not { } connection) return "Not connected to an ECU.";
        if (_ecuTune is not { } tune || _tuneLayout is not { } layout) return "No tune has been read.";

        if (edit.Encode(tune) is not { } write) return "This table cannot be encoded for this firmware.";

        TunePage? page = layout.Pages.FirstOrDefault(p => p.Index == write.Page);
        if (page is null) return $"The firmware declares no page {write.Page}.";

        if (page.BurnCommand.Length == 0)
            return $"This firmware declares no way to burn page {write.Page}.";

        try
        {
            connection.BurnPage(page, layout.LittleEndian);

            return $"Burned page {page.Index}. This survives a power cycle.";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            return $"The burn failed: {e.Message}";
        }
    }

    /// <summary>Rebuilds one table from the tune now held, so the view shows what the ECU has.</summary>
    private TuneTable? RereadTable(string name)
    {
        if (_ecuTune is not { } tune) return null;

        TuneTable? fresh = tune.Tables(_ecuTableDefinitions).FirstOrDefault(t => t.Name == name);
        if (fresh is null) return null;

        for (int i = 0; i < EcuTables.Count; i++)
            if (EcuTables[i].Name == name) { EcuTables[i] = fresh; break; }

        return fresh;
    }

    public bool HasEcuTune => _ecuTune is not null;

    public bool NoEcuTune => _ecuTune is null;

    /// <summary>What was read, for the calibration header.</summary>
    public string EcuTuneSummary
    {
        get => _ecuTuneSummary;
        private set => Set(ref _ecuTuneSummary, value);
    }

    /// <summary>
    /// Pulls the ECU's settings down at connect.
    ///
    /// Worth doing every time rather than on request: it is 22,960 bytes and
    /// about 50 ms on a rusEFI, and almost everything else wants it. Gauge scales
    /// are written against tune constants, VE Calibration needs the table being
    /// calibrated, and rusEFI's saved tunes carry no tables at all — so without
    /// this there is no way to get the fuel table for that firmware.
    ///
    /// Best-effort. A firmware whose pages will not read is still perfectly
    /// usable for logging, and failing the whole connection over it would be a
    /// poor trade.
    /// </summary>
    /// <summary>
    /// Reads the TunerStudio project's tune, for the variables only it holds.
    ///
    /// Used to resolve gauge scales, not adopted as the tune on display —
    /// finding a file next to the firmware definition is a good reason to read
    /// it and a poor reason to say the user opened it.
    /// </summary>
    private string? ReadProjectTune(string iniPath)
    {
        if (IniCatalog.ProjectTuneFor(iniPath) is not { } path) return null;

        try
        {
            _projectTuneName = Path.GetFileName(path);
            return TuningText.Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _projectTuneName = "";
            return null;
        }
    }

    private string _projectTuneName = "";

    private void ReadTuneFromEcu(EcuConnection connection, string iniText)
    {
        _ecuTune = null;
        _tuneLayout = null;
        EcuTables.Clear();
        EcuTuneSummary = "";

        try
        {
            TuneLayout layout = TuneLayoutReader.Read(iniText);
            if (layout.Pages.Count == 0) return;

            _tuneLayout = layout;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            EcuTune tune = EcuTune.Read(connection, layout);
            clock.Stop();

            _ecuTune = tune;
            _ecuTableDefinitions = TableEditorReader.Read(iniText);

            foreach (TuneTable table in Ordered(tune.Tables(_ecuTableDefinitions)))
                EcuTables.Add(table);

            EcuTableChoices.Refresh();

            EcuTuneSummary =
                $"{layout.TotalSize:N0} bytes read from the ECU in {clock.ElapsedMilliseconds:N0} ms · "
                + $"{EcuTables.Count} tables · {tune.Scalars().Count:N0} settings";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            EcuTuneSummary = $"The ECU's settings could not be read: {e.Message}";
        }
        finally
        {
            Raise(nameof(HasEcuTune));
            Raise(nameof(NoEcuTune));
            Raise(nameof(EcuTableSummary));

            // The first one is the biggest, which is the one worth opening on.
            SelectedEcuTable = EcuTables.FirstOrDefault();
        }
    }

    // ----- gauges -------------------------------------------------------------

    /// <summary>Every gauge this firmware defines, whether shown or not.</summary>
    public ObservableCollection<GaugeItem> AllGauges { get; } = [];

    /// <summary>The ones on the dashboard, in the order the firmware suggests.</summary>
    public ObservableCollection<GaugeItem> Dashboard { get; } = [];

    private string _gaugeSearch = "";
    private ICollectionView? _gaugeView;

    /// <summary>The gauge chooser, filtered by <see cref="GaugeSearch"/>.</summary>
    public ICollectionView GaugeChoices
    {
        get
        {
            if (_gaugeView is not null) return _gaugeView;

            _gaugeView = CollectionViewSource.GetDefaultView(AllGauges);
            _gaugeView.Filter = o =>
                o is GaugeItem item
                && (_gaugeSearch.Length == 0
                    || item.SearchText.Contains(_gaugeSearch, StringComparison.OrdinalIgnoreCase));

            _gaugeView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GaugeItem.Category)));

            return _gaugeView;
        }
    }

    public string GaugeSearch
    {
        get => _gaugeSearch;
        set
        {
            if (!Set(ref _gaugeSearch, value)) return;

            GaugeChoices.Refresh();
            Raise(nameof(GaugeSummary));
        }
    }

    public string GaugeSummary
    {
        get
        {
            if (AllGauges.Count == 0) return "Connect to an ECU to see its gauges.";

            var parts = new List<string>
            {
                $"{Dashboard.Count} shown · {AllGauges.Count(g => g.IsConnected)} available",
            };

            // Said rather than assumed: a scale that came from a file found on
            // disk should be attributable when it turns out to be wrong.
            if (_projectTuneName.Length > 0)
                parts.Add($"scales from {_projectTuneName}");

            int faceless = Dashboard.Count(g => !g.Spec.HasScale);
            if (faceless > 0)
                parts.Add($"{faceless} without a scale — open the tune that sets them");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Builds the gauge list for a firmware.
    ///
    /// The dashboard starts as the eight the firmware nominates on its front
    /// page, which is the cluster its authors thought you would want — a better
    /// default than the first eight of three hundred.
    /// </summary>
    private string? _gaugeIni;
    private IReadOnlyList<DatalogEntry> _gaugeDatalog = [];

    /// <summary>
    /// Rebuilds the gauges against the tune now in use.
    ///
    /// Scales come out of the tune — a rev counter runs to
    /// <c>{rpmHardLimit + 2000}</c> — so a tune opened after connecting is what
    /// gives several dials their faces for the first time.
    /// </summary>
    private void RescaleGauges()
    {
        if (_gaugeIni is { } ini) SeedGauges(ini, _gaugeDatalog);
    }

    /// <summary>
    /// The recorded column feeding a gauge, following the firmware's own aliases
    /// to find it.
    ///
    /// A gauge names a channel and a session records the datalog's names, and the
    /// two need not be the same word for the same number: Speeduino's throttle
    /// gauge reads <c>throttle</c>, which the firmware declares as <c>{ tps }</c>,
    /// and it is <c>tps</c> that the datalog records. Matching on the name alone
    /// dropped that gauge off the front page with the value being received all
    /// along.
    ///
    /// Chains are followed a few hops, which is more than any of these files
    /// actually use, and the bound means one that refers back to itself stops.
    /// </summary>
    private static string? ColumnFor(
        string channel,
        IReadOnlyDictionary<string, string> columns,
        IReadOnlyDictionary<string, string> aliases)
    {
        string name = channel;

        for (int hop = 0; hop < 4; hop++)
        {
            if (columns.TryGetValue(name, out string? column)) return column;
            if (!aliases.TryGetValue(name, out string? target)) return null;

            name = target;
        }

        return null;
    }

    private void SeedGauges(string iniText, IReadOnlyList<DatalogEntry> datalog)
    {
        _gaugeIni = iniText;
        _gaugeDatalog = datalog;

        // Kept across a rebuild, in the order they were arranged in — otherwise
        // opening a tune both discards a chosen dashboard and shuffles it.
        List<string> keep = [.. Dashboard.Select(g => g.Spec.Name)];

        foreach (GaugeItem existing in AllGauges) existing.ShownChanged -= OnGaugeShownChanged;

        AllGauges.Clear();
        Dashboard.Clear();

        // Internal channel name to the name the session records it under.
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DatalogEntry entry in datalog)
            columns[entry.Channel] = entry.Label.Length > 0 ? entry.Label : entry.Channel;

        _logNames = columns;

        // The project tune fills in what neither the ECU nor an opened file has:
        // TunerStudio's own variables, which is where a MegaSquirt keeps the top
        // of its rev counter. Without them three of its eight front-page gauges
        // have no scale at all.
        IReadOnlyDictionary<string, double> context = TuningContext.Build(
            iniText, TuneXml ?? _projectTune, fromEcu: _ecuTune?.Scalars());
        IReadOnlyList<GaugeSpec> specs = GaugeCatalog.Read(iniText, context);
        IReadOnlyList<string> front = GaugeCatalog.ReadFrontPage(iniText);

        var byName = new Dictionary<string, GaugeItem>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string> aliases = MsqIni.ReadAliases(iniText);

        foreach (GaugeSpec spec in specs)
        {
            var item = new GaugeItem(spec, ColumnFor(spec.Channel, columns, aliases));
            item.Show(Units);
            item.ShownChanged += OnGaugeShownChanged;

            AllGauges.Add(item);
            byName.TryAdd(spec.Name, item);
        }

        // What was on the dashboard before, or failing that the eight the
        // firmware nominates — which is the cluster its authors thought you
        // would want, and a better default than the first eight of three hundred.
        foreach (string name in keep.Count > 0 ? keep : front)
        {
            if (!byName.TryGetValue(name, out GaugeItem? item) || !item.IsConnected) continue;

            item.Show(true);
            Dashboard.Add(item);
        }

        GaugeChoices.Refresh();
        Raise(nameof(GaugeSummary));
        Raise(nameof(HasGauges));
        Raise(nameof(NoGauges));
    }

    public bool HasGauges => AllGauges.Count > 0;

    public bool NoGauges => AllGauges.Count == 0;

    /// <summary>
    /// Keeps the dashboard in step with the tick boxes.
    ///
    /// Rebuilt in catalogue order rather than appended to, so a gauge removed
    /// and put back does not jump to the end of the cluster.
    /// </summary>
    private void OnGaugeShownChanged()
    {
        var wanted = AllGauges.Where(g => g.IsShown).ToList();

        // In place, because replacing the collection would discard and rebuild
        // every dial rather than the ones that changed.
        for (int i = Dashboard.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Dashboard[i]))
                Dashboard.RemoveAt(i);

        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Dashboard.IndexOf(wanted[i]);
            if (at < 0) Dashboard.Insert(Math.Min(i, Dashboard.Count), wanted[i]);
        }

        Raise(nameof(GaugeSummary));
    }

    /// <summary>Points every shown gauge at the newest sample.</summary>
    private void RefreshGauges(LogDocument snapshot)
    {
        int last = snapshot.SampleCount - 1;
        if (last < 0) return;

        foreach (GaugeItem item in Dashboard)
        {
            item.Record(item.Column is { } column && snapshot.FindChannel(column) is { } channel
                ? channel.At(last)
                : double.NaN);
        }

        ScaleRevCounter();
    }

    /// <summary>
    /// True except on a MaxxECU, whose rev counter is scaled from its own
    /// limiter once it reports one. The firmwares with an INI already publish a
    /// sensible range, so nothing needs looking for.
    /// </summary>
    private bool _revCounterScaled = true;

    /// <summary>
    /// Scales a MaxxECU's rev counter to the engine rather than to the datatype.
    ///
    /// MTune's limits say what a channel can hold, not what it should read: RPM
    /// is declared 0 to 18,000, which on an engine limited at 5,000 leaves the
    /// needle in the first quarter for its whole life. The ECU publishes its own
    /// limiter, so the dial is built from that — the same thing the rusEFI path
    /// does with rpmHardLimit, and the ECU's number rather than a guess.
    ///
    /// Once only, and only when the ECU has actually said something sensible.
    /// </summary>
    private void ScaleRevCounter()
    {
        if (_revCounterScaled) return;

        GaugeItem? limit = AllGauges.FirstOrDefault(g => g.Spec.Name == "Rev limit");
        GaugeItem? rpm = AllGauges.FirstOrDefault(g => g.Spec.Name == "RPM");

        if (limit is null || rpm is null) return;
        if (double.IsNaN(limit.Value) || limit.Value is < 1000 or > 20000) return;

        double top = Math.Ceiling((limit.Value + 1000) / 500) * 500;

        // The low limits sit on the bottom of the scale rather than being left
        // out: a band is only believed when all four are ordered, and a limit on
        // the end of the scale already means there is no limit at that end.
        rpm.Retarget(rpm.Spec with
        {
            High = top,
            LowDanger = 0,
            LowWarning = 0,
            HighWarning = limit.Value - 500,
            HighDanger = limit.Value,
        });

        _revCounterScaled = true;
    }

    /// <summary>Clears every gauge's remembered extremes.</summary>
    public void ResetGaugePeaks()
    {
        foreach (GaugeItem item in AllGauges) item.ResetPeaks();

        Hint = "Peak and low markers cleared.";
    }

    /// <summary>COM ports present right now, for the connect menu.</summary>
    public IReadOnlyList<string> SerialPorts => SerialEcuTransport.AvailablePorts();

    /// <summary>
    /// Rates offered in the connect menu.
    ///
    /// 25 is the default and enough for fuelling work, where the wideband is the
    /// slow part. The faster ones are for transients — accel enrichment, knock,
    /// per-cylinder events — and cost what they sound like they cost: at 100 Hz
    /// a rusEFI's 823 channels are 14 MB a minute on disk.
    /// </summary>
    public static IReadOnlyList<double> LiveRates { get; } = [5, 10, 25, 50, 100, 200];

    /// <summary>
    /// Whether to ask for the whole realtime block in one request.
    ///
    /// Worth a third of the poll rate on a MegaSquirt, which serves its 512-byte
    /// block in one reply despite declaring a 256-byte blocking factor. Fatal on
    /// a rusEFI, which answers 1024 and, asked for more, leaves the USB bus until
    /// it is replugged. Nothing in the INI distinguishes the two, so it is off
    /// until someone who knows their ECU says otherwise.
    /// </summary>
    public bool SingleRequestBlock
    {
        get => _settings.SingleRequestBlock;
        set
        {
            if (value == _settings.SingleRequestBlock) return;

            _settings.SetSingleRequestBlock(value);
            Raise(nameof(SingleRequestBlock));

            Hint = value
                ? "The realtime block will be asked for in one request from the next connection. "
                  + "If the ECU stops responding, unplug it, switch this back off, and reconnect."
                : "Back to reading the realtime block in blocking-factor pieces.";
        }
    }

    /// <summary>Samples a second a live session records. Applied when the next one starts.</summary>
    public double LiveRate
    {
        get => _settings.LiveRate;
        set
        {
            if (value == _settings.LiveRate) return;

            _settings.SetLiveRate(value);
            Raise(nameof(LiveRate));

            Hint = IsLive
                ? $"Logging rate set to {_settings.LiveRate:N0} Hz — it applies to the next connection."
                : $"Logging rate set to {_settings.LiveRate:N0} Hz.";
        }
    }


    /// <summary>
    /// Opens a port, works out what the ECU is, and starts recording.
    ///
    /// The INI is chosen by the signature the ECU reports and the attempt is
    /// abandoned when none matches. Decoding with the wrong firmware definition
    /// does not fail, it reads every channel from the wrong offset — so a
    /// refusal here is the only thing between a live session and confident
    /// nonsense.
    /// </summary>
    /// <summary>
    /// Starts a session against a MaxxECU, which speaks its own protocol.
    ///
    /// Nothing of the TunerStudio path applies: there is no signature to ask
    /// for, no firmware INI, and no way to read the tune — so this gets gauges
    /// and logging, and calibration stays empty. The fourteen channels are
    /// fixed, because a subscription can only be replayed from one that was
    /// captured and not composed.
    /// </summary>
    public void ConnectMaxxEcu(string port)
    {
        Disconnect();

        var source = new MaxxEcuSource(new SerialEcuTransport(port) { OpenAttempts = 3 });
        string recording = Workspace.NewRecording(DateTime.Now);

        _live = new LiveSession(source, new LiveSessionSettings
        {
            RecordingPath = recording,
            MaximumRate = LiveRate,
        });

        _live.Start();

        _livePort = port;
        _liveSignature = "MaxxECU";
        _liveVersion = "";
        _liveIni = "";
        _liveRecording = recording;

        SeedMaxxGauges();

        Status = $"Live — MaxxECU   •   {_live.Names.Count} channels";
        Title = "Live: MaxxECU — OpenLogViewer";
        Hint = $"Recording to {recording}. A MaxxECU sends a fixed set of channels, "
               + "and its tune cannot be read, so calibration is not available for it.";

        Raise(nameof(IsLive));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanExport));
    }

    /// <summary>
    /// Connects to an OBD2 vehicle through an ELM327 adapter.
    ///
    /// The only connection here that needs nothing set up in advance: the
    /// standard fixes what every parameter means, and the car reports which ones
    /// it answers to, so this works on a vehicle nobody has ever plugged it into.
    ///
    /// Slow, though, and worth saying so — one request per parameter rather than
    /// a block, so a row of readings takes most of a second.
    /// </summary>
    /// <summary>
    /// Connects to whichever speed the adapter turns out to be set to. Unlike
    /// every other link here it cannot be assumed — a genuine ELM327 ships at
    /// 38,400 and clones at anything.
    /// </summary>
    public void ConnectObd2(string port) => StartObd2(Elm327Source.ConnectOnPort(port), port);

    /// <summary>
    /// Connects to an OBD2 adapter over Bluetooth Low Energy.
    ///
    /// The same ELM327 conversation over a different radio. BLE has no serial
    /// port profile, so these never become a COM port and cannot be reached
    /// through the port list at all — which is how a working dongle comes to
    /// look broken.
    /// </summary>
    public void ConnectObd2Ble(BleDevice adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        StartObd2(
            Elm327Source.Connect(new BleEcuTransport(adapter.Address, adapter.Name)),
            adapter.Label);
    }

    /// <summary>
    /// The same, over a link that is already decided on.
    ///
    /// Separated so the whole path — discovery, channels, which gauges reach the
    /// dashboard — can be exercised against an adapter in software. There is no
    /// other way to test it: a car is required otherwise, and the parts that have
    /// actually gone wrong here are in choosing gauges rather than in the wire.
    /// </summary>
    public void ConnectObd2(IEcuTransport transport, string port) =>
        StartObd2(Elm327Source.Connect(transport), port);

    private void StartObd2(Elm327Source source, string port)
    {
        Disconnect();

        string recording = Workspace.NewRecording(DateTime.Now);

        _live = new LiveSession(source, new LiveSessionSettings
        {
            RecordingPath = recording,
            MaximumRate = LiveRate,
        });

        _live.Start();

        _livePort = port;
        _liveSignature = source.Adapter.Length > 0 ? source.Adapter : "OBD2";
        _liveVersion = "";
        _liveIni = "";
        _liveRecording = recording;

        SeedObd2Gauges(source);

        SerialPortNames.Remember(port, _liveSignature);
        _settings.SetKnownEcus(SerialPortNames.Remembered());

        Status = $"Live — OBD2   •   {_live.Names.Count} channels   •   {_liveSignature}";
        Title = $"Live: OBD2 — OpenLogViewer";
        Hint = $"Recording to {recording}. OBD2 asks for one parameter at a time, so this "
               + "updates about twice a second rather than 25 times — the protocol's limit, "
               + "not the link's. A standard vehicle has no tune to read, so calibration "
               + "is not available for it.";

        Raise(nameof(IsLive));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanExport));
    }

    /// <summary>
    /// Builds gauges from the parameters the car actually reported.
    ///
    /// Ranges come from the standard rather than from anything invented here, and
    /// only the handful a driver watches start on screen — a car reporting thirty
    /// parameters would otherwise open as thirty dials of diagnostic counters.
    /// </summary>
    private void SeedObd2Gauges(Elm327Source source)
    {
        foreach (GaugeItem existing in AllGauges) existing.ShownChanged -= OnGaugeShownChanged;

        AllGauges.Clear();
        Dashboard.Clear();
        _gaugeIni = null;
        _revCounterScaled = false;

        var byName = new Dictionary<string, GaugeItem>(StringComparer.OrdinalIgnoreCase);

        foreach (GaugeSpec spec in Obd2Gauges.For(source.Parameters))
        {
            var item = new GaugeItem(spec, spec.Channel);
            item.Show(Units);
            item.ShownChanged += OnGaugeShownChanged;

            AllGauges.Add(item);
            byName.TryAdd(spec.Name, item);
        }

        foreach (string name in Obd2Gauges.FrontPage)
        {
            if (!byName.TryGetValue(name, out GaugeItem? item)) continue;

            item.Show(true);
            Dashboard.Add(item);
        }

        // Named for what it is. The scales come from the standard; the coloured
        // limits do not, because the standard has none — they are the figures a
        // workshop manual would use, and saying so is what stops a red arc being
        // read as this particular car's opinion of itself.
        _projectTuneName = "the OBD2 standard, with typical limits";

        GaugeChoices.Refresh();
        Raise(nameof(GaugeSummary));
        Raise(nameof(HasGauges));
        Raise(nameof(NoGauges));
    }

    /// <summary>
    /// Builds gauges for a MaxxECU from MTune's own channel definitions.
    ///
    /// There is no INI to take ranges from, but MTune ships every channel's
    /// name, unit, scale and limits — so the dials come from the same place the
    /// decode does rather than from anything invented here.
    /// </summary>
    private void SeedMaxxGauges()
    {
        foreach (GaugeItem existing in AllGauges) existing.ShownChanged -= OnGaugeShownChanged;

        AllGauges.Clear();
        Dashboard.Clear();
        _gaugeIni = null;
        _revCounterScaled = false;

        IReadOnlyList<GaugeSpec> specs = MaxxGauges.For(MaxxProtocol.Subscribed, MaxxGauges.FindDefinitions());

        foreach (GaugeSpec spec in specs)
        {
            var item = new GaugeItem(spec, spec.Channel);
            item.Show(Units);
            item.ShownChanged += OnGaugeShownChanged;
            AllGauges.Add(item);

            // Every one of them: fourteen is a dashboard, not a catalogue.
            if (item.IsConnected)
            {
                item.Show(true);
                Dashboard.Add(item);
            }
        }

        GaugeChoices.Refresh();

        // Said rather than swallowed: without MTune's definitions the dials have
        // no ranges, and a row of bare numbers with no explanation looks broken.
        _projectTuneName = MaxxGauges.Problem.Length > 0 ? "" : "MTune's channel definitions";
        if (MaxxGauges.Problem.Length > 0) Hint = MaxxGauges.Problem;

        Raise(nameof(GaugeSummary));
        Raise(nameof(HasGauges));
        Raise(nameof(NoGauges));
    }

    public void Connect(string port, bool bluetooth = false)
    {
        Disconnect();

        // A Bluetooth link is the same protocol over a virtual COM port, but it
        // answers in hundreds of milliseconds where a cable answers in three, so
        // it needs longer to reply and longer to fall quiet between attempts.
        var connection = new EcuConnection(
            new SerialEcuTransport(port) { OpenAttempts = bluetooth ? 3 : 1 },
            bluetooth ? EcuConnectionSettings.Bluetooth : null);

        connection.Open();

        IReadOnlyList<string> identity = connection.ReadIdentity();

        if (IniCatalog.MatchAny(identity, IniCatalog.Scan(Workspace.DefinitionSearchPaths))
            is not var (ini, signature))
        {
            connection.Dispose();

            // The folder is created now rather than at startup, with a note
            // naming the signature this ECU actually reported — which is the one
            // thing that makes finding the right file possible.
            string folder = Workspace.EnsureDefinitions(identity);

            throw new LogFormatException(
                (identity.Count > 0
                    ? $"The ECU reports \"{string.Join("\", \"", identity)}\", "
                      + "and no definition file on this machine matches it.\n\n"
                    : "The ECU did not say what it is.\n\n")
                + "A live ECU sends raw numbers with no names, units or scaling — all of that "
                + "is in the .ini for that exact firmware. Without it the data cannot be decoded, "
                + "and guessing would show readings that look right and are not.\n\n"
                + $"Put the file here and connect again:\n\n{folder}\n\n"
                + "There is a note in that folder explaining where to get one. "
                + "TunerStudio's own copies are searched automatically if it is installed.");
        }

        // Whatever else it said is the build string — the same reply that is the
        // signature on one firmware family is the version on the other.
        string version = identity.FirstOrDefault(t => t != signature) ?? signature;

        // Remembered against the port so the connect menu can name the ECU next
        // time. Windows names the chip — a Speeduino shows up as "Arduino Mega
        // 2560" — which does not distinguish two boards, and is the wrong half
        // of the answer when the question is which ECU to connect to.
        SerialPortNames.Remember(port, version);
        _settings.SetKnownEcus(SerialPortNames.Remembered());

        string iniText = TuningText.Read(ini.Path);
        RealtimeLayout layout = MsqIni.ReadOutputChannels(iniText);
        IReadOnlyList<DatalogEntry> datalog = MsqIni.ReadDatalog(iniText);

        // From here the firmware's own request format is used, which is the only
        // way one program reads both a MegaSquirt page and a rusEFI block.
        connection.Use(layout, _settings.SingleRequestBlock);

        _projectTune = ReadProjectTune(ini.Path);

        _ecuConnection = connection;
        ReadTuneFromEcu(connection, iniText);
        SeedGauges(iniText, datalog);

        // The tune supplies what the wire does not: firmware derives channels
        // from settings such as the cylinder count as well as from live values.
        var decoder = new RealtimeDecoder(layout, MsqTune.ReadScalars(TuneXml));

        string recording = Workspace.NewRecording(DateTime.Now);

        _live = new LiveSession(connection, decoder, datalog, new LiveSessionSettings
        {
            RecordingPath = recording,
            MaximumRate = LiveRate,
        });

        _live.Start();

        _livePort = port;
        _liveSignature = signature;
        _liveVersion = version;
        _liveIni = ini.Path;
        _liveRecording = recording;

        Status = $"Live — {signature}   •   {_live.Names.Count} channels   •   {ini.Name}";

        // Worth saying plainly: on a bench almost nothing moves, and the default
        // filter then hides almost everything. The channels are all being
        // recorded regardless.
        string quiet = _live.Names.Count > 0
            ? "  Untick \"Hide unused\" to see every channel — all of them are being recorded either way."
            : "";
        Title = $"Live: {signature} — OpenLogViewer";
        Hint = $"Recording to {recording}. The plot follows the newest data until you zoom or pan." + quiet;

        Raise(nameof(IsLive));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanExport));
    }

    /// <summary>Stops the session and closes the port, leaving the data in place.</summary>
    public void Disconnect()
    {
        if (_live is null) return;

        _live.Stop();
        _live.Dispose();
        _live = null;
        _ecuConnection = null;

        LiveStatus = "";
        _livePort = _liveSignature = _liveVersion = _liveIni = _liveRecording = "";

        Raise(nameof(IsLive));
        Raise(nameof(LiveDetail));

        if (Document is not null)
            Hint = "Disconnected. The session is still here, and its recording is on disk.";
    }

    /// <summary>
    /// Takes the newest snapshot and points the channel rows at it.
    ///
    /// Rows are re-pointed rather than rebuilt: rebuilding would throw away the
    /// colours and the tick boxes several times a second.
    /// </summary>
    public bool RefreshLive()
    {
        if (_live is null) return false;

        LiveSessionStatus status = _live.Status;

        // The dot is the recording indicator; retries only appear once there are
        // any, so a healthy link stays quiet.
        LiveStatus = status switch
        {
            { Faulted: true } => status.Error!,
            { Reconnecting: true } => $"○ {_livePort} · waiting for the ECU to come back…",
            _ => $"● {_livePort} · {_liveSignature} · {status.Rate:F1} Hz" +
                 (status.Retries > 0 ? $" · {status.Retries} retries" : ""),
        };

        // Hollow dot and a warning colour while the link is down, so a session
        // that has quietly stopped receiving is not mistaken for a healthy one
        // that happens to be sitting still.
        LiveHealthy = !status.Reconnecting;

        if (status.Faulted)
        {
            Disconnect();
            Hint = status.Error!;
            return true;
        }

        LogDocument snapshot = _live.Snapshot();
        if (snapshot.SampleCount == 0) return false;

        if (Channels.Count == 0)
        {
            SeedLiveChannels(snapshot);
        }
        else
        {
            foreach (ChannelItem item in Channels)
                if (snapshot.FindChannel(item.Name) is { } channel) item.Rebind(channel);

            // Channels come alive as the session runs — nothing moves on a bench
            // with the engine off, and "hide unused" is then hiding almost
            // everything. Without re-evaluating, a channel that starts moving
            // stays hidden for the rest of the session.
            //
            // A range only ever widens, so a channel can go from flat to moving
            // but never back; counting them is enough to know when to look again.
            int moving = Channels.Count(c => !c.IsFlat);
            if (moving != _movingChannels)
            {
                _movingChannels = moving;
                RefreshView();
            }
        }

        RefreshGauges(snapshot);

        Document = snapshot;
        return true;
    }

    private int _movingChannels = -1;

    private void SeedLiveChannels(LogDocument snapshot)
    {
        _colorCursor = 0;
        _movingChannels = -1;

        foreach (LogChannel channel in snapshot.Channels.Where(c => !snapshot.IsTimeBase(c)))
        {
            var item = new ChannelItem(channel, Palette[_colorCursor++ % Palette.Length]);
            item.Show(Units);
            item.VisibilityChanged += OnVisibilityChanged;
            Channels.Add(item);
        }

        foreach (string name in DefaultChannels)
        {
            ChannelItem? item = Channels.FirstOrDefault(
                c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item is not null) item.IsVisible = true;
        }

        SeedHistogramAxes(snapshot);
        SeedFilters(snapshot);
        RefreshView();
    }

    // ----- the tune ---------------------------------------------------------

    private string? _loadedTune;
    private string _loadedTuneName = "";

    /// <summary>
    /// The tune the tables come from: one opened by hand, else the one the log
    /// carries. TunerStudio embeds the tune in an MLG, and that copy is the one
    /// that was actually running when the log was recorded.
    /// </summary>
    private string? TuneXml => _loadedTune ?? Document?.EmbeddedTune;

    /// <summary>
    /// Which tune is in use, short enough for the toolbar. Always shown, so
    /// "the log's own" is never something the user has to infer from the absence
    /// of anything else.
    /// </summary>
    public string TuneSource
    {
        get
        {
            if (_loadedTune is not null) return _loadedTuneName;

            return Document?.EmbeddedTune is { Length: > 0 } ? "from the log" : "none";
        }
    }

    /// <summary>The longer form, for the tooltip on the toolbar indicator.</summary>
    public string TuneDetail => _loadedTune is not null
        ? $"Tables come from {_loadedTuneName}. Right-click to go back to the log's own tune."
        : Document?.EmbeddedTune is { Length: > 0 }
            ? "Tables come from the tune stored in this log — the one that was running when it was recorded."
            : "This log carries no tune. Open a .msq to bin onto its table axes and to use VE Calibration.";

    /// <summary>
    /// Set when an opened tune's fuel table differs from the one in the log.
    ///
    /// This is the whole reason opening a tune by hand is dangerous rather than
    /// merely convenient. VE Calibration scales the numbers that produced the logged
    /// AFR. Feed it a table that has been edited since the drive and it will
    /// scale numbers the engine never ran, and confidently suggest a table that
    /// is wrong by however much the tune moved in between.
    /// </summary>
    public string TuneWarning { get; private set; } = "";

    public bool HasTuneWarning => TuneWarning.Length > 0;

    /// <summary>True while tables come from a file rather than from the log.</summary>
    public bool UsingLoadedTune => _loadedTune is not null;

    /// <summary>Reads an MSQ tune file and uses its tables in place of the log's.</summary>
    public void LoadTune(string path)
    {
        string xml = TuningText.Read(path);

        if (MsqTune.ReadAxisSets(xml).Count == 0)
            throw new LogFormatException(
                "No usable tables found in that file. It should be a TunerStudio .msq tune.");

        _loadedTune = xml;
        _loadedTuneName = Path.GetFileName(path);
        TuneWarning = CompareWithEmbedded(xml);

        Hint = TuneWarning.Length > 0
            ? TuneWarning
            : $"Using tables from {_loadedTuneName}.";

        TuneChanged();
    }

    /// <summary>Goes back to the tune the log carries.</summary>
    public void ClearTune()
    {
        if (_loadedTune is null) return;

        _loadedTune = null;
        _loadedTuneName = "";
        TuneWarning = "";

        Hint = "Back to the tune stored in the log.";
        TuneChanged();
    }

    /// <summary>
    /// Announces the change and rebuilds the axis list. Announced directly rather
    /// than only through the axis rebuild, because a tune can be opened before
    /// any log is — and the toolbar has to say so either way.
    /// </summary>
    private void TuneChanged()
    {
        RescaleGauges();

        if (Document is { } doc)
        {
            SeedHistogramAxes(doc);
        }
        else
        {
            Raise(nameof(TuneSource));
            Raise(nameof(TuneDetail));
            Raise(nameof(TuneWarning));
            Raise(nameof(HasTuneWarning));
            Raise(nameof(UsingLoadedTune));
        }

        HistogramInvalidated?.Invoke();
    }

    private string CompareWithEmbedded(string xml)
    {
        if (Document?.EmbeddedTune is not { Length: > 0 } embedded) return "";

        TuneTable? opened = MsqTune.ReadTables(xml).FirstOrDefault(t => t.Name == "VE table 1");
        TuneTable? logged = MsqTune.ReadTables(embedded).FirstOrDefault(t => t.Name == "VE table 1");

        if (opened is null || logged is null) return "";
        if (SameValues(opened, logged)) return "";

        return "This tune's fuel table differs from the one stored in the log. " +
               "VE Calibration scales the numbers that produced the logged AFR, so suggestions " +
               "will be off by however much the tune has changed since the drive.";

        static bool SameValues(TuneTable a, TuneTable b)
        {
            if (a.Columns != b.Columns || a.Rows != b.Rows) return false;

            for (int c = 0; c < a.Columns; c++)
            for (int r = 0; r < a.Rows; r++)
                if (Math.Abs(a.Values[c, r] - b.Values[c, r]) > 1e-6)
                    return false;

            return true;
        }
    }

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
        // Flat channels are left out — nothing bins usefully on a constant —
        // except where the firmware indexes one of its own tables by it. On a
        // bench the engine is not turning, so RPM is flat and the fuel table's
        // own axis would be missing from the list of axes.
        HashSet<string> indexed = EcuAxisChannels();

        AxisChannels.Clear();
        foreach (ChannelItem item in Channels
                     .Where(c => !c.IsFlat || indexed.Contains(c.Name))
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

        // Offer the tune's own table axes when a tune is available, carrying the
        // table's values through where they could be read — VE Calibration needs the
        // numbers, not just the grid.
        AxisSources.Clear();
        AxisSources.Add(AxisSourceOption.FromData);

        string? tune = TuneXml;

        Dictionary<string, TuneTable> withValues = MsqTune.ReadTables(tune)
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (TuneAxisSet set in MsqTune.ReadAxisSets(tune))
            AxisSources.Add(new AxisSourceOption(
                set.Label, set, withValues.GetValueOrDefault(set.Name)));

        AddEcuAxisSources();

        _axisSource = AxisSourceOption.FromData;
        _veAnalyze = false;

        Raise(nameof(TuneSource));
        Raise(nameof(TuneDetail));
        Raise(nameof(TuneWarning));
        Raise(nameof(HasTuneWarning));
        Raise(nameof(UsingLoadedTune));

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

            item.Show(Units);
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

        // A name the log cannot already have, since the one being typed may well
        // be taken at this instant and the builder refuses a duplicate.
        static string PreviewName(LogDocument doc)
        {
            string name = "preview";
            while (doc.Channels.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name += "'";

            return name;
        }
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

                item.Show(Units);
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




