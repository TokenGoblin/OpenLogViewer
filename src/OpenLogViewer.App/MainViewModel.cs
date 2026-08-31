using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
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

public sealed partial class MainViewModel : ObservableObject
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
    private readonly ChannelStyleStore _styleStore;
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
        SettingsStore? settings = null, MathChannelStore? math = null,
        ChannelStyleStore? styles = null)
    {
        _store = presets ?? new PresetStore();
        _filterStore = filters ?? new FilterStore();
        _settings = settings ?? new SettingsStore();
        _mathStore = math ?? new MathChannelStore();
        _styleStore = styles ?? new ChannelStyleStore();

        Workspace = new Workspace(_settings.DataFolder);
        SerialPortNames.Recall(_settings.KnownEcus);
        SerialPortNames.RecallLastUsed(_settings.EcuLastUsed);

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

    // ----- finding a moment in the log --------------------------------------

    private string _findCondition = "";
    private LogSearchResult? _found;
    private int _foundIndex = -1;
    private bool _finding;

    /// <summary>Whether the find bar is showing.</summary>
    public bool Finding
    {
        get => _finding;
        set => Set(ref _finding, value);
    }

    /// <summary>The condition being looked for, in the calculated-channel syntax.</summary>
    public string FindCondition
    {
        get => _findCondition;
        set
        {
            // The result belongs to the old condition, so it goes with it rather
            // than sitting there describing something no longer being asked.
            if (Set(ref _findCondition, value)) ClearFound();
        }
    }

    /// <summary>What was found, or null when nothing has been looked for.</summary>
    public LogSearchResult? Found => _found;

    public string FindSummary { get; private set; } = "";

    public bool CanStepFindings => _found is { IsEmpty: false };

    private void ClearFound()
    {
        _found = null;
        _foundIndex = -1;
        FindSummary = "";

        Raise(nameof(Found));
        Raise(nameof(FindSummary));
        Raise(nameof(CanStepFindings));
    }

    /// <summary>
    /// Runs the search. Filters apply: they say which part of the drive is under
    /// consideration, and jumping to a moment they exclude would answer a
    /// question nobody asked.
    /// </summary>
    public bool RunFind()
    {
        if (Document is not { SampleCount: > 0 } doc)
        {
            FindSummary = "Open a log first.";
            Raise(nameof(FindSummary));
            return false;
        }

        SampleMask mask = SampleFilter.Build(doc, Filters.Select(f => f.Filter));
        LogSearchResult found = LogSearch.Find(doc, _findCondition, mask);

        if (found.HasProblem)
        {
            ClearFound();
            FindSummary = found.Problem!;
            Raise(nameof(FindSummary));
            return false;
        }

        _found = found;
        _foundIndex = -1;

        var parts = new List<string>();

        if (found.IsEmpty)
        {
            parts.Add("Nothing matched");
        }
        else
        {
            parts.Add($"{found.Matches:N0} sample{(found.Matches == 1 ? "" : "s")} in "
                      + $"{found.Runs.Count:N0} stretch{(found.Runs.Count == 1 ? "" : "es")}");
        }

        // Said rather than folded into the misses: a comparison against a reading
        // that was never taken is unanswerable, not false.
        if (found.Unknown > 0)
            parts.Add($"{found.Unknown:N0} could not be judged");

        if (mask.FiltersApplied)
            parts.Add($"{mask.Total - mask.PassCount:N0} excluded by filters");

        FindSummary = string.Join("   •   ", parts);

        Raise(nameof(Found));
        Raise(nameof(FindSummary));
        Raise(nameof(CanStepFindings));
        return !found.IsEmpty;
    }

    /// <summary>
    /// The next stretch to look at, wrapping at the end, or null when there is
    /// nothing to step to.
    /// </summary>
    public (int First, int Last)? StepFinding(bool forward)
    {
        if (_found is not { IsEmpty: false } found) return null;

        _foundIndex = _foundIndex < 0
            ? forward ? 0 : found.Runs.Count - 1
            : (_foundIndex + (forward ? 1 : -1) + found.Runs.Count) % found.Runs.Count;

        FindSummary = $"Stretch {_foundIndex + 1:N0} of {found.Runs.Count:N0}"
                      + $"   •   {found.Matches:N0} samples matched in all";

        Raise(nameof(FindSummary));
        return found.Runs[_foundIndex];
    }

    // ----- pinned colours and scales ----------------------------------------

    /// <summary>The palette of the current scheme, offered as the easy choice.</summary>
    public IReadOnlyList<Color> PaletteColors => Palette;

    private ChannelItem? _styleTarget;
    private string _styleMin = "";
    private string _styleMax = "";

    /// <summary>The channel the appearance editor is open on, or null when it is shut.</summary>
    public ChannelItem? StyleTarget
    {
        get => _styleTarget;
        private set
        {
            if (!Set(ref _styleTarget, value)) return;

            Raise(nameof(EditingStyle));
            Raise(nameof(StyleTargetName));
        }
    }

    public bool EditingStyle => _styleTarget is not null;

    public string StyleTargetName => _styleTarget?.Name ?? "";

    private string _styleUnits = "";

    /// <summary>
    /// The units the boxes are in, shown beside them. Stated rather than left to
    /// be inferred: the number the editor wants is only unambiguous if it says
    /// which unit it is in.
    /// </summary>
    public string StyleUnits
    {
        get => _styleUnits;
        private set
        {
            if (Set(ref _styleUnits, value)) Raise(nameof(HasStyleUnits));
        }
    }

    /// <summary>False for a channel the log gave no unit for, where "In " alone would be noise.</summary>
    public bool HasStyleUnits => _styleUnits.Length > 0;

    public string StyleMin
    {
        get => _styleMin;
        set => Set(ref _styleMin, value);
    }

    public string StyleMax
    {
        get => _styleMax;
        set => Set(ref _styleMax, value);
    }

    /// <summary>
    /// Opens the appearance editor on a channel, seeded with what it is drawn
    /// over now rather than with empty boxes — the usual edit is a nudge to the
    /// range already on screen, and retyping it from the plot is the slow way to
    /// get there.
    /// </summary>
    public void BeginStyleEdit(ChannelItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        (double min, double range) = item.Scale(LogPlot.HoldSteady);

        // Seeded and read back in the units the row is showing, which are the
        // units on the axis beside it. The pin itself is stored raw so it means
        // the same range whichever system is chosen later, but a box seeded with
        // 80 next to an axis labelled 176 °F invites a number in the wrong one.
        StyleMin = Round(UnitConvert.Value(min, item.Channel.Units, Units));
        StyleMax = Round(UnitConvert.Value(min + range, item.Channel.Units, Units));
        StyleUnits = item.Units;
        StyleTarget = item;
    }

    /// <summary>
    /// Seeded bounds at the channel's own precision. The full double is the
    /// float's rounding error printed out, which is not a number anybody wants
    /// to edit.
    /// </summary>
    private static string Round(double value) =>
        Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

    public void CancelStyleEdit() => StyleTarget = null;

    /// <summary>Applies the typed bounds, keeping the editor open on a bad pair.</summary>
    public bool CommitStyleEdit()
    {
        if (_styleTarget is not { } item) return false;

        bool blank = string.IsNullOrWhiteSpace(_styleMin) && string.IsNullOrWhiteSpace(_styleMax);

        if (blank)
        {
            PinRange(item, null, null);
            StyleTarget = null;
            return true;
        }

        if (!double.TryParse(_styleMin, NumberStyles.Float, CultureInfo.InvariantCulture, out double min)
            || !double.TryParse(_styleMax, NumberStyles.Float, CultureInfo.InvariantCulture, out double max))
        {
            Hint = "A pinned scale needs two numbers, or neither to go back to automatic.";
            return false;
        }

        // Back into the log's own units, since that is what the boxes were
        // seeded from and what the plot maps samples against.
        string units = item.Channel.Units;

        if (!PinRange(item, UnitConvert.ToReported(min, units, Units),
                            UnitConvert.ToReported(max, units, Units)))
        {
            return false;
        }

        StyleTarget = null;
        return true;
    }

    /// <summary>Puts whatever is pinned for this channel's name onto its row.</summary>
    /// <summary>
    /// Sets how hard a channel's trace is smoothed, and remembers it.
    ///
    /// Remembered by channel name like a pinned colour, so a sensor that is
    /// noisy on this car is drawn readably in every log from it rather than
    /// being set again each time.
    /// </summary>
    public void SetSmoothing(ChannelItem item, SmoothingLevel level)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_styleStore.SetSmoothing(item.Name, level))
        {
            Hint = "There is no room left to remember another channel's settings. "
                   + "Clear one that is pinned and try again.";
            return;
        }

        item.SetSmoothing(level);

        Hint = level == SmoothingLevel.None
            ? $"{item.Name} is drawn as logged again"
            : $"{item.Name} is drawn smoothed — a median of {Smoothing.Window(level)} samples. "
              + "Measurements still use it as logged.";

        PlotInvalidated?.Invoke();
    }

    private void ApplyStyle(ChannelItem item)
    {
        if (_styleStore.For(item.Name) is not { } style) return;

        if (style.HasColor)
            item.SetFixedColor(Color.FromRgb(
                (byte)(style.Color!.Value >> 16),
                (byte)(style.Color.Value >> 8),
                (byte)style.Color.Value));

        if (style.HasRange) item.SetFixedRange((style.Min!.Value, style.Max!.Value));

        if (style.HasSmoothing) item.SetSmoothing(style.Smoothing);
    }

    /// <summary>
    /// Pins a trace colour by channel name, or with null hands it back to the
    /// palette — which then re-picks for every plotted channel, since the
    /// released colour is available again and the palette is handed out in plot
    /// order.
    /// </summary>
    public void PinColor(ChannelItem item, Color? color)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_styleStore.SetColor(item.Name, color is { } c ? (c.R << 16) | (c.G << 8) | c.B : null))
        {
            Hint = "There is no room left to remember another channel's colour. "
                   + "Clear one that is pinned and try again.";
            return;
        }

        item.SetFixedColor(color);

        if (color is null) RecolorChannels();

        Hint = color is null
            ? $"{item.Name} takes the scheme's palette again"
            : $"{item.Name} is pinned to this colour in every log";

        PlotInvalidated?.Invoke();
    }

    /// <summary>
    /// Pins the vertical range a channel is drawn over, or with either bound null
    /// hands it back to the channel's own range.
    /// </summary>
    /// <returns>False when the bounds are not a range, which is not an error
    /// worth a dialog — the caller reports it.</returns>
    public bool PinRange(ChannelItem item, double? min, double? max)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (min is null || max is null)
        {
            _styleStore.SetRange(item.Name, null, null);
            item.SetFixedRange(null);
            Hint = $"{item.Name} is scaled to its own range again";
            PlotInvalidated?.Invoke();
            return true;
        }

        if (!double.IsFinite(min.Value) || !double.IsFinite(max.Value) || max <= min)
        {
            Hint = "A pinned scale needs a low value below a high one.";
            return false;
        }

        if (!_styleStore.SetRange(item.Name, min, max))
        {
            Hint = "There is no room left to remember another channel's scale. "
                   + "Clear one that is pinned and try again.";
            return false;
        }

        item.SetFixedRange((min.Value, max.Value));

        Hint = $"{item.Name} is drawn over {item.Channel.Format(min.Value, Units)}"
               + $" … {item.Channel.Format(max.Value, Units)} in every log";

        PlotInvalidated?.Invoke();
        return true;
    }

    /// <summary>Unpins both halves, putting a channel back to automatic.</summary>
    public void ClearStyle(ChannelItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _styleStore.Clear(item.Name);
        item.SetFixedColor(null);
        item.SetFixedRange(null);
        item.SetSmoothing(SmoothingLevel.None);

        RecolorChannels();
        Hint = $"{item.Name} is back to the scheme's colour, its own range and as logged";
        PlotInvalidated?.Invoke();
    }

    /// <summary>
    /// Hands out the new palette in the order channels are plotted, so what is on
    /// screen gets the widely separated entries rather than whatever the file
    /// order happens to give.
    /// </summary>
    private void RecolorChannels()
    {
        // Entries a pinned channel already holds are stepped over, so a pinned
        // colour is not handed out again to the trace beside it. Only pins that
        // landed on a palette entry can collide at all; a colour from outside
        // the palette takes nothing away from it.
        HashSet<Color> pinned = [.. Channels.Where(c => c.HasFixedColor).Select(c => c.Color)];

        Color[] palette = [.. Palette.Where(c => !pinned.Contains(c))];

        // Unless pinning has claimed the palette entire, in which case some
        // repetition is unavoidable and the full palette is the better of two
        // bad answers — an empty one would throw.
        if (palette.Length == 0) palette = Palette;

        int next = 0;

        // A pinned channel is skipped rather than given an entry it will not
        // use, so pinning one does not shuffle the colours of the traces beside
        // it every time the scheme changes.
        foreach (ChannelItem item in Channels.Where(c => c.IsVisible))
            if (!item.HasFixedColor) item.SetColor(palette[next++ % palette.Length]);

        foreach (ChannelItem item in Channels.Where(c => !c.IsVisible))
            if (!item.HasFixedColor) item.SetColor(palette[next++ % palette.Length]);

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
            Raise(nameof(HasDocument));
        }
    }

    /// <summary>
    /// Whether a log is open, for commands that need one.
    ///
    /// Worth having as its own property rather than binding to
    /// <see cref="Document"/> and converting: a menu item bound to a name the
    /// view model does not have fails silently and leaves the item at its
    /// default, which for IsEnabled is enabled — so the command stays available
    /// and nothing anywhere reports the mistake.
    /// </summary>
    public bool HasDocument => _document is not null;

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

    private LogView _logView = LogView.Plot;
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

    /// <summary>Every sample placed by two channels and coloured by a third.</summary>
    public ScatterPlot? Points { get; private set; }

    /// <summary>Which reading of the recording is on screen.</summary>
    public LogView LogView
    {
        get => _logView;
        set
        {
            if (!Set(ref _logView, value)) return;

            Raise(nameof(ShowLog));
            Raise(nameof(ShowHistogram));
            Raise(nameof(ShowScatter));
            Raise(nameof(ShowAxisPanel));
            Raise(nameof(ZAxisCaption));
            HistogramInvalidated?.Invoke();
        }
    }

    public bool ShowLog
    {
        get => _logView == LogView.Plot;
        set { if (value) LogView = LogView.Plot; }
    }

    public bool ShowHistogram
    {
        get => _logView == LogView.Histogram;
        set { if (value) LogView = LogView.Histogram; }
    }

    public bool ShowScatter
    {
        get => _logView == LogView.Scatter;
        set { if (value) LogView = LogView.Scatter; }
    }

    /// <summary>
    /// Whether the sidebar shows the axis and filter settings rather than the
    /// channel list. The table and the scatter are chosen and filtered the same
    /// way, so they share the panel; only what is done with the samples differs.
    /// </summary>
    public bool ShowAxisPanel => _logView is LogView.Histogram or LogView.Scatter;

    public string ZAxisCaption => _logView == LogView.Scatter
        ? "Z axis — the colour of each mark"
        : "Z axis — the value in each cell";

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
            Raise(nameof(InGuideMode));
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

    public bool InGuideMode
    {
        get => _mode == WorkspaceMode.Guide;
        set { if (value) Mode = WorkspaceMode.Guide; }
    }

    // ----- the guide --------------------------------------------------------

    // The first section, so the pane is never blank and the list never opens
    // with nothing selected.
    private GuideSection? _guideSection = Guide.Sections[0];
    private string _guideSearch = "";

    public IReadOnlyList<GuideSection> GuideSections => Guide.Sections;

    /// <summary>The section on screen. Null only while a search is showing.</summary>
    public GuideSection? GuideSection
    {
        get => _guideSection;
        set
        {
            if (!Set(ref _guideSection, value)) return;

            Raise(nameof(GuideEntries));
            Raise(nameof(GuideHeading));
            Raise(nameof(GuideBlurb));
        }
    }

    /// <summary>
    /// Narrows the guide to entries mentioning this, across every section.
    ///
    /// Across all of them rather than within the chosen one, because somebody
    /// searching a manual is looking for the page they cannot find — restricting
    /// it to the section they happen to be on would answer only when they had
    /// already guessed right.
    /// </summary>
    public string GuideSearch
    {
        get => _guideSearch;
        set
        {
            if (!Set(ref _guideSearch, value)) return;

            Raise(nameof(GuideEntries));
            Raise(nameof(GuideHeading));
            Raise(nameof(GuideBlurb));
            Raise(nameof(SearchingGuide));
        }
    }

    public bool SearchingGuide => _guideSearch.Trim().Length > 0;

    /// <summary>What is on screen: a section, or whatever the search turned up.</summary>
    public IReadOnlyList<GuideEntry> GuideEntries
    {
        get
        {
            string text = _guideSearch.Trim();

            if (text.Length > 0) return [.. Guide.AllEntries.Where(e => e.Matches(text))];

            return _guideSection?.Entries ?? Guide.Sections[0].Entries;
        }
    }

    public string GuideHeading
    {
        get
        {
            if (!SearchingGuide) return (_guideSection ?? Guide.Sections[0]).Title;

            int found = GuideEntries.Count;
            return found == 1 ? "1 result" : $"{found:N0} results";
        }
    }

    public string GuideBlurb => SearchingGuide
        ? GuideEntries.Count == 0
            ? "Nothing here mentions that. Try a shorter word — the search covers every section."
            : $"Mentioning “{_guideSearch.Trim()}”, across every section."
        : (_guideSection ?? Guide.Sections[0]).Blurb;

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

    /// <summary>
    /// The session, assigned through here so that whatever must happen to every
    /// session happens once rather than at each of the four places one is made.
    ///
    /// There are four: a serial ECU, an OBD2 adapter, a Subaru over SSM and a
    /// MaxxECU. Attaching the agent stream at the site being worked on and not
    /// at the other three is the exact shape of defect three code reviews have
    /// already caught here, so the assignment does it rather than the caller.
    /// </summary>
    private LiveSession? Live
    {
        get => _live;

        set
        {
            if (ReferenceEquals(_live, value)) return;

            if (_live is not null) _live.Frame -= PublishToAgents;

            _live = value;

            // Fed from the poll thread rather than from the repaint, so the
            // stream runs at the ECU's pace rather than the window's.
            if (_live is not null) _live.Frame += PublishToAgents;
        }
    }

    private string _liveStatus = "";
    private string _livePort = "";
    private string _liveSignature = "";

    /// <summary>The firmware on the other end, for anything that reports on the session.</summary>
    public string LiveSignature => _liveSignature;
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
            $"Channels  {_live!.Names.Count}"
                + (_obd2Undecoded.Count > 0 ? $"  (+{_obd2Undecoded.Count} not decoded)" : ""),
            $"Recording {(_liveRecording.Length > 0 ? _liveRecording : "not recording")}",
        ])
        : "Not connected.";

    // ----- recording ----------------------------------------------------------

    /// <summary>
    /// How a session's opening line reads, which depends on whether anything is
    /// being written.
    ///
    /// Said either way. A session that is not recording has to say so as plainly
    /// as one that is — the failure this guards against is somebody driving for
    /// half an hour believing there will be a file at the end of it.
    /// </summary>
    private static string Opening(string? recording) => recording is { Length: > 0 }
        ? $"Recording to {recording}."
        : "Watching only — nothing is being written. Press Record when you want a log.";

    /// <summary>Whether a file is being written right now.</summary>
    public bool IsRecording => _live is { IsRecording: true };

    public bool CanRecord => IsLive;

    /// <summary>The file being written, or the last one written this session.</summary>
    public string RecordingPath => _live?.RecordingPath ?? _live?.LastRecordingPath ?? "";

    /// <summary>
    /// The record button's label, which is also its state.
    ///
    /// One button rather than two, because recording and not recording are
    /// exclusive and a pair of buttons means one of them is always dead.
    /// </summary>
    public string RecordLabel => IsRecording ? "■ Stop recording" : "● Record…";

    /// <summary>What the recording has caught so far, for the status line.</summary>
    public string RecordingSummary
    {
        get
        {
            if (_live is not { } live) return "";
            if (!live.IsRecording)
                return live.LastRecordingPath is { Length: > 0 } last
                    ? $"Saved {Path.GetFileName(last)}"
                    : "Not recording";

            double seconds = live.RecordedSeconds;

            return $"● Recording {Path.GetFileName(live.RecordingPath ?? "")} · "
                   + $"{live.RecordedRows:N0} rows · {seconds:F0} s";
        }
    }

    /// <summary>
    /// Whether connecting starts a recording on its own.
    ///
    /// Off by default: connecting watches, and recording is asked for. Turning it
    /// on is for somebody who would rather never think about it.
    /// </summary>
    public bool RecordOnConnect
    {
        get => _settings.RecordOnConnect;
        set
        {
            if (value == _settings.RecordOnConnect) return;

            _settings.SetRecordOnConnect(value);
            Raise(nameof(RecordOnConnect));
        }
    }

    /// <summary>
    /// A name and folder to offer for a new recording.
    ///
    /// Named for the ECU and the moment rather than left blank. A dialog that
    /// opens with an empty name in a folder nobody chose is how recordings end up
    /// called "log", "log2" and "log (final)" — and the one thing a person is
    /// certain to want to know later is which car and when.
    /// </summary>
    public string SuggestedRecordingPath()
    {
        string folder = _settings.RecordingFolder is { Length: > 0 } chosen && Directory.Exists(chosen)
            ? chosen
            : Workspace.Ensure(Workspace.Logs);

        string ecu = Clean(_liveSignature.Length > 0 ? _liveSignature : "live");

        return Path.Combine(folder, $"{ecu}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
    }

    /// <summary>Trims a signature down to something a file system will take.</summary>
    private static string Clean(string name)
    {
        var kept = new string(
            [.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c)]);

        return kept.Trim('-') is { Length: > 0 } trimmed ? trimmed : "live";
    }

    /// <summary>
    /// Starts writing to a file of the caller's choosing.
    ///
    /// Replaces a recording in progress rather than refusing, so "record again
    /// somewhere else" is one action. The first is closed properly and stays on
    /// disk complete.
    /// </summary>
    public string StartRecording(string path)
    {
        if (_live is not { } live) return "Not connected.";

        try
        {
            string started = live.StartRecording(path);
            _liveRecording = started;

            // Where it went, so the next one is offered the same folder. The
            // workspace default is a fine first answer and a poor second one.
            if (Path.GetDirectoryName(started) is { Length: > 0 } folder)
                _settings.SetRecordingFolder(folder);

            RaiseRecording();

            return $"Recording to {started}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Could not start recording: {e.Message}";
        }
    }

    /// <summary>
    /// Closes the recording and leaves the session running.
    ///
    /// The file is complete at this point rather than at disconnect: every row
    /// was flushed as it was written.
    /// </summary>
    public string StopRecording()
    {
        if (_live is not { } live) return "Not connected.";
        if (live.StopRecording() is not { } path) return "Nothing was being recorded.";

        int rows = live.RecordedRows;
        RaiseRecording();

        return $"Saved {rows:N0} rows to {path}. The session is still live.";
    }

    /// <summary>
    /// The recording half of the status line.
    ///
    /// "Not recording" is stated rather than left blank. Blank reads as an
    /// application that has not got round to saying yet, and the whole risk this
    /// feature introduces is somebody assuming a file is being written when none
    /// is — so the quiet case is the one that has to be loud.
    /// </summary>
    private string Written()
    {
        if (_live is not { IsRecording: true } live) return "not recording";

        return $"REC {live.RecordedRows:N0} rows";
    }

    private void RaiseRecording()
    {
        Raise(nameof(IsRecording));
        Raise(nameof(RecordingPath));
        Raise(nameof(RecordLabel));
        Raise(nameof(RecordingSummary));
        Raise(nameof(LiveDetail));
    }

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
            Raise(nameof(HasSettingsPages));
            Raise(nameof(SettingsSummary));
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

    // ----- settings pages ---------------------------------------------------

    private TuneInterface? _ecuInterface;
    private TuneSettingsEdit? _settingsEdit;

    /// <summary>
    /// Values the firmware defines as expressions over other values, which its
    /// dialogs are then written against.
    /// </summary>
    private IReadOnlyDictionary<string, string> _derived =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private SettingsMenuEntry? _openMenuEntry;

    /// <summary>Everything the firmware offers, flattened into one list.</summary>
    public ObservableCollection<SettingsMenuEntry> SettingsMenu { get; } = [];

    public bool HasSettingsPages => SettingsMenu.Count > 0;

    /// <summary>The page on screen, or null when none is open.</summary>
    public SettingsDialog? OpenDialog { get; private set; }

    /// <summary>
    /// Whether the left-hand list is offering settings pages rather than tables.
    /// </summary>
    private bool _showSettings;

    public bool ShowSettingsPages
    {
        get => _showSettings;
        set
        {
            if (!Set(ref _showSettings, value)) return;

            Raise(nameof(ShowEcuTables));
            Raise(nameof(ShowTableView));
            Raise(nameof(ShowCurves));
            Raise(nameof(ShowSettingsPagesOnly));
            Raise(nameof(ShowSettingsFields));
            Raise(nameof(SettingsSummary));
        }
    }

    public bool ShowEcuTables
    {
        get => !_showSettings;
        set { if (value) ShowSettingsPages = false; }
    }

    /// <summary>
    /// The fields half of the settings area, which a curve takes the place of.
    ///
    /// Both are reached from the same list, because a firmware's menu makes no
    /// distinction between them — an entry names something, and whether it turns
    /// out to be a page of fields or a line to drag is worked out here.
    /// </summary>
    public bool ShowSettingsPagesOnly => _showSettings && !HasOpenCurves;

    /// <summary>
    /// The fields half, which is shown whenever the page has any.
    ///
    /// A page may hold both — a curve with a note under it is the usual shape —
    /// so this is not the opposite of showing a curve. A page whose every item
    /// was a curve has no rows left and would otherwise leave an empty panel and
    /// a Send button under the plot.
    /// </summary>
    public bool ShowSettingsFields =>
        _showSettings && OpenDialog is { } dialog && dialog.Visible.Count > 0;

    public SettingsMenuEntry? OpenMenuEntry
    {
        get => _openMenuEntry;
        set
        {
            if (!Set(ref _openMenuEntry, value)) return;

            OpenPage(value);
        }
    }

    public string SettingsSummary
    {
        get
        {
            if (_settingsEdit is not { } edit) return "";

            int changed = edit.ChangedCount;

            return changed == 0
                ? $"{SettingsMenu.Count(m => !m.IsHeading):N0} pages · nothing changed"
                : $"{changed:N0} setting{(changed == 1 ? "" : "s")} changed · "
                  + $"{edit.BytesToWrite:N0} bytes to send";
        }
    }

    public bool HasSettingChanges => _settingsEdit?.HasChanges == true;

    /// <summary>
    /// Builds the list of pages from the firmware's menus.
    ///
    /// Flattened, with the menu names left in as headings. A tree would mirror
    /// the file more closely, but a tuner looking for "Rev Limiter" wants to find
    /// it by typing, and there are only a few hundred of them.
    /// </summary>
    private void BuildSettingsMenu()
    {
        SettingsMenu.Clear();

        if (_ecuInterface is not { } ui) return;

        foreach (TuneMenu menu in ui.Menus)
        {
            var entries = new List<SettingsMenuEntry>();

            foreach (MenuEntry entry in menu.Entries)
            {
                // Separators are a rule in a drop-down and have nothing to open;
                // the tool's own editors are not this firmware's to describe.
                if (entry.IsSeparator || entry.IsBuiltIn) continue;

                // A menu entry names a dialog, a table or a curve, and the file
                // does not say which. Curves were skipped until now, which left
                // 23 of a MicroSquirt's 131 entries and 48 of an MS3's 246
                // opening nothing — warmup enrichment, cranking pulsewidth,
                // injector dead time, most of what a tuner actually changes.
                bool isDialog = ui.Find(entry.Dialog) is not null;

                // Only where a curve can actually be built from it. One naming a
                // curve whose bins this build does not have would otherwise be
                // offered and then open a blank pane, which is worse than not
                // offering it at all.
                bool isCurve = !isDialog && _curveNames.Contains(entry.Dialog);

                // And a table, which is the third thing an entry can name and
                // the last one that opened nothing.
                bool isTable = !isDialog && !isCurve && TableNamed(entry.Dialog) is not null;

                if (!isDialog && !isCurve && !isTable) continue;

                entries.Add(new SettingsMenuEntry(
                    entry.Dialog,
                    entry.Title.Length > 0 ? entry.Title : entry.Dialog,
                    entry.Condition)
                {
                    IsCurve = isCurve,
                    IsTable = isTable,
                });
            }

            // A menu whose every entry was a separator or a built-in is not a
            // heading over nothing.
            if (entries.Count == 0) continue;

            SettingsMenu.Add(SettingsMenuEntry.Heading(menu.Title));
            foreach (SettingsMenuEntry entry in entries) SettingsMenu.Add(entry);
        }

        Raise(nameof(HasSettingsPages));
        Raise(nameof(SettingsSummary));
    }

    /// <summary>
    /// The table a menu entry names, under either of the two names a firmware
    /// gives it: the grid and its three-dimensional view.
    /// </summary>
    private TuneTable? TableNamed(string name)
    {
        if (_ecuTableDefinitions.FirstOrDefault(
                t => t.Id.Equals(name, StringComparison.OrdinalIgnoreCase)
                     || t.Map.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } definition)
        {
            return null;
        }

        return EcuTables.FirstOrDefault(
            t => t.Name.Equals(definition.Title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every curve this firmware describes, by name.</summary>
    private IReadOnlyDictionary<string, TuneCurve> _ecuCurves =
        new Dictionary<string, TuneCurve>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Only those a curve can actually be built from.</summary>
    /// <summary>
    /// Only the curves that can actually be built.
    ///
    /// Asked by building one, which is the only question that settles it. A
    /// curve naming both of its rows passes every cheaper test and still fails
    /// where this firmware has no constant by those names, or where the two
    /// rows turn out to be different lengths — and an entry offered on that
    /// basis opens a blank pane, which is worse than not being offered.
    /// </summary>
    private static IReadOnlySet<string> Named(
        IReadOnlyDictionary<string, TuneCurve> curves, EcuTune? tune) =>
        new HashSet<string>(
            tune is null
                ? []
                : curves.Where(c => TuneCurveEdit.For(c.Value, tune) is not null).Select(c => c.Key),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Their names, for the page builder to recognise a panel by.</summary>
    private IReadOnlySet<string> _curveNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The curves on screen, in the order the page names them.
    ///
    /// A list rather than one, because a page routinely holds several: 25 of an
    /// MS3's pages declare two or more, and showing the first and dropping the
    /// rest loses settings with nothing to say they are missing.
    /// </summary>
    public IReadOnlyList<TuneCurveEdit> OpenCurves { get; private set; } = [];

    public bool HasOpenCurves => OpenCurves.Count > 0;

    /// <summary>
    /// Whether the plots belong on screen, which also takes the half the user is
    /// looking at. Without that a curve stays drawn — buttons and all — over the
    /// table editor once the Tables chip is clicked.
    /// </summary>
    public bool ShowCurves => _showSettings && HasOpenCurves;

    /// <summary>
    /// Whether the table editor belongs on screen: because the tables half is
    /// showing, or because a settings entry named a table.
    /// </summary>
    public bool ShowTableView =>
        ShowEcuTables || (_showSettings && OpenMenuEntry is { IsTable: true });

    /// <summary>What the curve header says: how much is here, and what is pending.</summary>
    public string CurveSummary
    {
        get
        {
            if (!HasOpenCurves) return "";

            int moved = OpenCurves.Sum(c => c.ChangedCount);
            string what = OpenCurves.Count == 1
                ? $"{OpenCurves[0].Title} — {OpenCurves[0].Count} points"
                : $"{OpenCurves.Count} curves";

            return moved > 0
                ? $"{what} · {moved} point{(moved == 1 ? "" : "s")} moved, not yet sent"
                : $"{what} · nothing changed";
        }
    }

    public bool HasCurveChanges => OpenCurves.Any(c => c.HasChanges);

    /// <summary>Whether the curve on screen could be sent.</summary>
    public bool CanWriteCurve =>
        HasCurveChanges && _ecuConnection is not null && _tuneLayout is not null
        && !TuneIsPlaceholder && !TuneIsFromFile;

    /// <summary>Puts every curve on the page back to what the ECU holds.</summary>
    public void RevertCurve()
    {
        foreach (TuneCurveEdit curve in OpenCurves) curve.Revert();
        CurveChanged();
    }

    /// <summary>Called after any edit, so the header and the buttons keep up.</summary>
    public void CurveChanged()
    {
        Raise(nameof(OpenCurves));
        Raise(nameof(HasOpenCurves));
        Raise(nameof(ShowCurves));
        Raise(nameof(ShowSettingsPagesOnly));
        Raise(nameof(ShowSettingsFields));
        Raise(nameof(ShowTableView));
        Raise(nameof(CurveSummary));
        Raise(nameof(HasCurveChanges));
        Raise(nameof(CanWriteCurve));
    }

    /// <summary>
    /// Sends the curve to the ECU.
    ///
    /// Both rows go together or neither does — see
    /// <see cref="TuneCurveEdit.Encode"/> — and the writes are applied to the
    /// copies held here only once the controller has taken them.
    /// </summary>
    public string WriteCurveToEcu()
    {
        // What the tune is, before what is on screen: the same refusal every
        // other write makes, and reachable from a scripted run where no button
        // is consulted.
        if (TuneIsPlaceholder)
            return "This is a firmware definition rather than a tune — every value in it reads as "
                   + "zero, so nothing here may be sent to a controller.";

        if (TuneIsFromFile)
            return "This tune was opened from a file rather than read off the controller, so it "
                   + "cannot be sent back. Read the ECU's own tune first.";

        if (!HasOpenCurves) return "No curve is open.";
        if (_ecuConnection is not { } connection) return "Not connected to an ECU.";
        if (_ecuTune is not { } tune || _tuneLayout is not { } layout) return "No tune has been read.";
        if (!HasCurveChanges) return "Nothing has been changed.";

        // Every write worked out and every page checked before the first byte
        // goes out. A curve's two rows have to land together — values sent
        // without the breakpoints they were drawn against leave the ECU
        // interpolating new numbers over the old axis — and finding halfway
        // through that a page cannot be written is exactly the split this is
        // meant to prevent.
        var planned = new List<TuneWrite>();

        foreach (TuneCurveEdit each in OpenCurves)
        {
            if (!each.HasChanges) continue;

            if (each.Encode(tune) is not { } writes)
                return $"\"{each.Title}\" cannot be encoded for this firmware, so nothing was sent.";

            planned.AddRange(writes);
        }

        if (planned.Count == 0) return "Nothing has been changed.";

        foreach (TuneWrite write in planned)
        {
            TunePage? page = layout.Pages.FirstOrDefault(p => p.Index == write.Page);

            if (page is null || page.ChunkWriteCommand.Length == 0)
                return $"This firmware declares no way to write page {write.Page}, "
                       + "so nothing was sent.";
        }

        int moved = OpenCurves.Sum(c => c.ChangedCount);

        try
        {
            foreach (TuneWrite write in planned)
            {
                TunePage page = layout.Pages.First(p => p.Index == write.Page);

                connection.WriteTunePage(
                    page, layout.BlockingFactor, layout.LittleEndian, write.Offset, write.Data,
                    layout.InterWriteDelay);

                // Both copies move with the controller: the tune, or the next
                // read-modify-write is against stale bytes, and the settings
                // edit, or these bytes read as settings waiting to be sent and
                // the next thing sent from a page would undo this.
                tune.Accept(write);
                _settingsEdit?.Accept(write);
                _settingsPagesWritten.Add(write.Page);
            }

            Rebuild(tune);

            return $"Sent {moved} moved point{(moved == 1 ? "" : "s")} to the ECU. "
                   + "It is running this now, and will forget it at the next power cycle "
                   + "unless you burn it.";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            // Whatever landed is on the controller, so the curves are rebuilt
            // from its bytes rather than left showing an edit that is now partly
            // applied and partly not.
            Rebuild(tune);

            return $"The write failed: {e.Message}";
        }
    }

    /// <summary>
    /// Reads the tables and curves on screen back out of the tune.
    ///
    /// Called after something has changed a great many bytes at once. Whatever
    /// is being looked at came off the controller, and the controller has moved.
    /// </summary>
    private void RefreshOpenTune()
    {
        if (_ecuTune is not { } tune) return;

        Rebuild(tune);

        EcuTables.Clear();
        foreach (TuneTable table in Ordered(tune.Tables(_ecuTableDefinitions))) EcuTables.Add(table);
        EcuTableChoices.Refresh();

        SelectedEcuTable = EcuTables.FirstOrDefault(
            t => t.Name.Equals(SelectedEcuTable?.Name, StringComparison.OrdinalIgnoreCase))
            ?? EcuTables.FirstOrDefault();

        OpenDialog?.Refresh(Resolver);
        Raise(nameof(OpenDialog));
    }

    /// <summary>Reads the open curves back out of the tune, after a write.</summary>
    private void Rebuild(EcuTune tune)
    {
        OpenCurves =
        [
            .. OpenCurves
                .Select(c => TuneCurveEdit.For(c.Curve, tune))
                .Where(c => c is not null)
                .Select(c => c!),
        ];

        CurveChanged();
        Raise(nameof(CanBurnSettings));
    }

    private void OpenPage(SettingsMenuEntry? entry)
    {
        OpenDialog = entry is { IsHeading: false, IsCurve: false }
                     && _ecuInterface is { } ui && _ecuTune is { } tune
            ? SettingsDialog.Build(
                entry.Dialog, ui, tune.Constant, _settingsEdit, entry.Title, _curveNames)
            : null;

        // Curves come either from the menu entry itself or from the page it
        // opens — a firmware puts one on a page with the same directive a group
        // of fields uses — and a page may hold several: 25 of an MS3's do.
        var curves = new List<TuneCurveEdit>();

        if (entry is { IsHeading: false } && _ecuTune is { } holding)
        {
            IEnumerable<string> named =
                entry.IsCurve ? [entry.Dialog] : OpenDialog?.Curves ?? [];

            foreach (string name in named)
            {
                if (_ecuCurves.TryGetValue(name, out TuneCurve? curve)
                    && TuneCurveEdit.For(curve, holding) is { } built)
                {
                    curves.Add(built);
                }
            }
        }

        OpenCurves = curves;

        // A table opens on the right while the settings list stays where it is,
        // so following a menu into one does not lose the reader's place in it.
        if (entry is { IsHeading: false, IsTable: true } && TableNamed(entry.Dialog) is { } table)
            SelectedEcuTable = table;

        Raise(nameof(ShowTableView));

        if (OpenDialog is { } dialog)
        {
            dialog.Refresh(Resolver);

            // Any edit may reveal or hide other fields on the same page, since
            // conditions are written against the tune's own settings.
            foreach (SettingRow row in dialog.Rows)
            {
                row.Changed += OnSettingChanged;

                // A refusal has to reach somebody. The box putting the stored
                // value back says the edit did not take; this says why, which
                // is the half that lets them type something that will.
                row.Refused += why => Hint = why;
            }
        }

        Raise(nameof(OpenDialog));
        CurveChanged();
    }

    private void OnSettingChanged()
    {
        OpenDialog?.Refresh(Resolver);

        Raise(nameof(SettingsSummary));
        Raise(nameof(HasSettingChanges));
        Raise(nameof(CanWriteSettings));
    }

    /// <summary>
    /// The lookup a condition is judged against: settings, then live readings,
    /// then anything the firmware defines in terms of those.
    ///
    /// Built afresh each time rather than kept, because the derived values are
    /// worked out on demand and a resolver holds the set it is part way through.
    /// </summary>
    private Func<string, double> Resolver => DerivedChannels.Resolving(_derived, Setting);

    /// <summary>
    /// A setting's value for a condition to be judged against — the edited one,
    /// so turning something on reveals what it configures at once.
    ///
    /// Falls back to the live readings, because a good many conditions are
    /// written against those rather than against settings: a button offered only
    /// with the engine stopped is testing RPM.
    /// </summary>
    private double Setting(string name)
    {
        if (_settingsEdit is { } edit)
        {
            double value = edit.Value(name);
            if (!double.IsNaN(value)) return value;
        }

        // The newest reading rather than one under a cursor: a condition on a
        // live channel is asking what the engine is doing now, and a settings
        // page is not somewhere anybody is scrubbing a log.
        if (Document is not { SampleCount: > 0 } doc) return double.NaN;

        return doc.FindChannel(name) is { } channel ? channel.At(doc.SampleCount - 1) : double.NaN;
    }

    /// <summary>
    /// Opens a firmware definition with no controller behind it, so its settings
    /// pages can be looked at.
    ///
    /// <b>The values are not a tune.</b> Every page reads as nought, because
    /// there is nothing to read them from — this says what a firmware offers and
    /// how it is laid out, not what any particular engine is set to. Editing a
    /// real tune away from the car means loading its saved values, which is a
    /// different thing and not this.
    /// </summary>
    public bool OpenDefinition(string iniPath)
    {
        try
        {
            string ini = TuningText.Read(iniPath);
            TuneLayout layout = TuneLayoutReader.Read(ini);

            if (layout.Pages.Count == 0)
            {
                EcuTuneSummary = $"{Path.GetFileName(iniPath)} declares no pages of settings.";
                return false;
            }

            _tuneLayout = layout;
            _ecuTune = EcuTune.FromPages(layout, [.. layout.Pages.Select(p => new byte[p.Size])]);
            _ecuTableDefinitions = TableEditorReader.Read(ini);
            _ecuInterface = TuneInterfaceReader.Read(ini);
            _ecuCurves = TuneCurveReader.Read(ini);
            _curveNames = Named(_ecuCurves, _ecuTune);
            _derived = DerivedChannels.Read(ini);
            _settingsEdit = new TuneSettingsEdit(_ecuTune);

            TuneIsPlaceholder = true;
            TuneIsFromFile = false;

            // A definition is not a tune and belongs to no saved file, so
            // nothing about the last one survives into it.
            _tuneFile = null;
            _tuneSymbols = null;
            _ecuSignature = "";

            // And everything belonging to the tune that was open until now. A
            // page left on screen stays bound to the edit just thrown away, so
            // what is typed into it lands in an image nothing will ever send;
            // and pages recorded as written are numbered for the other
            // firmware, which is not what a burn of this one would commit.
            _settingsPagesWritten.Clear();
            OpenDialog = null;
            OpenCurves = [];
            _openMenuEntry = null;

            EcuTables.Clear();
            foreach (TuneTable table in Ordered(_ecuTune.Tables(_ecuTableDefinitions))) EcuTables.Add(table);
            EcuTableChoices.Refresh();

            BuildSettingsMenu();

            EcuTuneSummary =
                $"{Path.GetFileName(iniPath)} — {EcuTables.Count} tables · "
                + $"{SettingsMenu.Count(m => !m.IsHeading):N0} pages. "
                + (_ecuConnection is null
                    ? "No ECU is connected, so every value reads as zero."
                    : "Every value reads as zero — this is what the firmware "
                      + "offers, not what the attached ECU is set to, so nothing "
                      + "here can be sent to it.");

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LogFormatException)
        {
            EcuTuneSummary = $"Could not read {Path.GetFileName(iniPath)}: {e.Message}";
            return false;
        }
        finally
        {
            Raise(nameof(HasEcuTune));
            Raise(nameof(NoEcuTune));
            Raise(nameof(ShowNoTuneNotice));
            Raise(nameof(EcuTableSummary));
            Raise(nameof(EcuTuneSummary));
            Raise(nameof(HasSettingsPages));
            Raise(nameof(SettingsSummary));
            Raise(nameof(OpenDialog));
            Raise(nameof(OpenMenuEntry));
            RaiseWriteGates();
            CurveChanged();
        }
    }

    /// <summary>
    /// Tells the screen that what may be written has changed.
    ///
    /// <b>Every one of these depends on the connection, and the connection is
    /// not a property anything watches.</b> A binding is evaluated once and then
    /// only when it is told to, so a gate that is never raised is a button
    /// frozen in whatever state it had at startup — which for all of these is
    /// disabled, because nothing was connected then. Burn and the whole restore
    /// menu were unreachable for a session however much was connected, and after
    /// a disconnect every write button stayed lit over no link at all.
    ///
    /// Gathered into one method rather than listed at each call site, because
    /// the list has grown five times and been forgotten at least twice.
    /// </summary>
    private void RaiseWriteGates()
    {
        Raise(nameof(CanWriteTable));
        Raise(nameof(CanWriteSettings));
        Raise(nameof(CanWriteCurve));
        Raise(nameof(CanBurn));
        Raise(nameof(CanBurnSettings));
        Raise(nameof(CanSaveTune));
        Raise(nameof(CanApplyRestore));
        Raise(nameof(TuneIsPlaceholder));
        Raise(nameof(TuneIsFromFile));
    }

    /// <summary>
    /// True while the tune on show came from a definition file rather than off a
    /// controller, so every value in it is a zero standing in for one.
    ///
    /// <b>Nothing may be sent while this holds.</b> A definition can be opened
    /// with an ECU still attached — to read what some other firmware offers, say
    /// — and the placeholder tune that results is laid out like a real one and
    /// full of noughts. Sending it would write those noughts to a running
    /// engine, and burning would commit page indices belonging to the firmware
    /// that was open before.
    /// </summary>
    public bool TuneIsPlaceholder { get; private set; }

    /// <summary>Whether there is something to send, and somewhere to send it.</summary>
    public bool CanWriteSettings =>
        HasSettingChanges && _ecuConnection is not null && _tuneLayout is not null
        && !TuneIsPlaceholder && !TuneIsFromFile;

    /// <summary>Pages written since the tune was read, which a burn would commit.</summary>
    private readonly SortedSet<int> _settingsPagesWritten = [];

    public bool CanBurnSettings =>
        _settingsPagesWritten.Count > 0 && _ecuConnection is not null && !TuneIsPlaceholder
        && !TuneIsFromFile;

    /// <summary>What a confirmation needs to say before anything is sent.</summary>
    public int SettingsChangedCount => _settingsEdit?.ChangedCount ?? 0;

    public int SettingsBytesToWrite => _settingsEdit?.BytesToWrite ?? 0;

    public int SettingsPagesToWrite => _settingsEdit?.PagesToWrite.Count ?? 0;

    public int SettingsPagesWritten => _settingsPagesWritten.Count;

    /// <summary>
    /// Sends the changed settings to the controller.
    ///
    /// <para>
    /// Unlike a table, this is several writes and they may span pages — so a
    /// failure part way through leaves some of them applied. That is reported as
    /// what it is rather than as "the write failed", because the two call for
    /// different things: one means try again, the other means find out what the
    /// ECU is now running before doing anything else.
    /// </para>
    /// <para>
    /// Each write is read back and compared by the connection before it counts,
    /// and nothing is burned, so a power cycle undoes all of it.
    /// </para>
    /// </summary>
    public string WriteSettingsToEcu()
    {
        if (_settingsEdit is not { } edit) return "No tune has been read.";
        if (_ecuConnection is not { } connection) return "Not connected to an ECU.";
        if (_tuneLayout is not { } layout || _ecuTune is not { } tune) return "No tune has been read.";
        if (!edit.HasChanges) return "Nothing has been changed.";

        IReadOnlyList<TuneWrite> writes = edit.Writes();
        if (writes.Count == 0) return "Nothing has been changed.";

        int settings = edit.ChangedCount;
        int bytes = 0, done = 0;

        try
        {
            foreach (TuneWrite write in writes)
            {
                TunePage? page = layout.Pages.FirstOrDefault(p => p.Index == write.Page);

                if (page is null)
                    throw new EcuProtocolException($"The firmware declares no page {write.Page}.");

                if (page.ChunkWriteCommand.Length == 0)
                    throw new EcuProtocolException(
                        $"This firmware declares no way to write page {write.Page}.");

                connection.WriteTunePage(
                    page, layout.BlockingFactor, layout.LittleEndian, write.Offset, write.Data,
                    layout.InterWriteDelay);

                // The copy held here has to move with the controller, or the
                // next read-modify-write is against stale bytes.
                tune.Accept(write);
                _settingsPagesWritten.Add(write.Page);

                bytes += write.Data.Length;
                done++;
            }

            return $"Sent {settings} setting{(settings == 1 ? "" : "s")} "
                   + $"({bytes:N0} bytes) to the ECU. It is running them now, and will forget them "
                   + "at the next power cycle unless you burn them.";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            return done == 0
                ? $"Nothing was sent: {e.Message}"
                : $"{done} of {writes.Count} writes reached the ECU ({bytes:N0} bytes) and then "
                  + $"this failed: {e.Message} Some settings have changed and some have not. "
                  + "Nothing was burned, so turning the key off restores all of it.";
        }
        finally
        {
            // What the ECU took stops being a pending change; what it did not
            // stays one. Which is which is answered by comparing, not by
            // counting the writes that got through.
            edit.Reconcile();
            OnSettingChanged();
            Raise(nameof(CanWriteSettings));
            Raise(nameof(CanBurnSettings));
        }
    }

    /// <summary>
    /// Commits the pages that were written to the controller's flash.
    ///
    /// Only those pages. A tune is several pages and burning one that was not
    /// touched is a flash write for no reason — flash wears, and a burn stops
    /// the controller answering while it happens.
    /// </summary>
    public string BurnSettingsToEcu()
    {
        if (_ecuConnection is not { } connection) return "Not connected to an ECU.";
        if (_tuneLayout is not { } layout) return "No tune has been read.";

        if (_settingsPagesWritten.Count == 0)
            return "Nothing has been sent, so there is nothing to burn.";

        var burned = new List<int>();

        try
        {
            // Each page struck off as it lands, rather than the lot at the end.
            // A burn part way through can fail, and pressing the button again
            // would then put the pages already committed through flash a second
            // time — against the whole point of only burning what was touched.
            foreach (int index in _settingsPagesWritten.ToArray())
            {
                TunePage? page = layout.Pages.FirstOrDefault(p => p.Index == index);

                if (page is null || page.BurnCommand.Length == 0)
                {
                    // Nothing can commit this one, so leaving it on the list
                    // only offers a burn that will never do anything.
                    _settingsPagesWritten.Remove(index);
                    continue;
                }

                try
                {
                    connection.BurnPage(page, layout.LittleEndian, layout.AfterBurnDelay);
                }
                catch (EcuProtocolException e) when (!e.Refused)
                {
                    // An unconfirmed burn is struck off too. The ECU may well
                    // have committed the page and gone quiet writing it, and
                    // leaving it listed keeps the Burn button lit over exactly
                    // that page — so the obvious next move puts a page that is
                    // already in flash through a second erase, which is what
                    // striking each one off as it lands exists to prevent. A
                    // refusal is different: the controller said no, so it stays
                    // listed and burning it again is the right answer.
                    _settingsPagesWritten.Remove(index);
                    throw;
                }

                _settingsPagesWritten.Remove(index);
                burned.Add(index);
            }

            if (burned.Count == 0)
            {
                // The list has just been emptied of pages nothing can commit, so
                // the button that offers a burn is now offering nothing. Said,
                // or it stays lit and answers "there is nothing to burn".
                Raise(nameof(CanBurnSettings));

                return "This firmware declares no way to burn the pages that were written.";
            }

            Raise(nameof(CanBurnSettings));

            return $"Burned {burned.Count} page{(burned.Count == 1 ? "" : "s")} to flash. "
                   + "These settings now survive a power cycle.";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            Raise(nameof(CanBurnSettings));

            // A burn the ECU never answered is not one that failed. Its own
            // message says it may have completed and how to find out, and
            // putting "the burn failed" in front of that contradicts it in a
            // single sentence.
            bool unconfirmed = e is EcuProtocolException { Refused: false };

            if (burned.Count == 0) return unconfirmed ? e.Message : $"The burn failed: {e.Message}";

            return $"{burned.Count} page{(burned.Count == 1 ? "" : "s")} were burned and then "
                   + (unconfirmed ? "this happened: " : "this failed: ") + e.Message
                   + " The rest are in working memory and will be lost at the next power cycle.";
        }
    }

    /// <summary>Puts every changed setting back to what the ECU holds.</summary>
    public void RevertSettings()
    {
        _settingsEdit?.RevertAll();
        OnSettingChanged();
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

            // The nudge follows the table. A spark table stored in quarter
            // degrees and a fuel table stored in whole per cent want different
            // steps, and anything finer than the firmware's own storage is a
            // change the ECU rounds away.
            if (TableEdit is { } edit) TableNudge = edit.Step;

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

            // Raised here as well as on selection, because opening a table does
            // not necessarily move the selection: it is already the first cell,
            // so the assignment that follows changes nothing and notifies
            // nobody. Without this the readout stays blank until the user
            // happens to click a different cell.
            RaiseTablePreview();
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
        set
        {
            if (!Set(ref _cells, value)) return;

            Raise(nameof(TableEditSummary));
            RaiseTablePreview();
        }
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
        HasTableChanges && _ecuConnection is not null && _tuneLayout is not null
        && !TuneIsPlaceholder && !TuneIsFromFile;

    /// <summary>
    /// Whether burning is possible at all, which needs a controller on the other
    /// end.
    ///
    /// Gated on the connection rather than on having a tune, because a tune can
    /// now be opened from a definition file with nothing attached — and a Burn
    /// button that looks live with no ECU behind it is an offer this cannot keep.
    /// </summary>
    public bool CanBurn =>
        _ecuConnection is not null && _tuneLayout is not null && !TuneIsPlaceholder
        && !TuneIsFromFile;

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
            case TuneEditKind.Set: edit.Set(SelectedCells, change.Amount); break;

            case TuneEditKind.Interpolate:
                // Said rather than swallowed: a selection with no middle has
                // nothing to fill, and a button that silently did nothing would
                // read as the table refusing to be edited.
                if (!edit.Interpolate(SelectedCells))
                    Hint = "Select at least three cells across or down — "
                         + "interpolation fills what is between the ends.";
                break;
        }

        AfterTableEdit();
    }

    /// <summary>
    /// How much one press of the nudge is worth.
    ///
    /// The firmware's own storage step by default, because anything finer is a
    /// change the ECU rounds away — which reads as the write having been ignored.
    /// </summary>
    private double _nudge = 1;

    public double TableNudge
    {
        get => _nudge;
        set
        {
            if (!Set(ref _nudge, value)) return;

            Raise(nameof(TableNudgeUpLabel));
            Raise(nameof(TableNudgeDownLabel));
        }
    }

    /// <summary>
    /// What the buttons themselves say they will do.
    ///
    /// Written onto the buttons rather than left to a label, because "Nudge"
    /// beside a box reading "1" and a "%" — the table's own unit, VE being
    /// measured in per cent — read as "nudge by one per cent", which is what the
    /// scale buttons do and this does not. A button that says "+ 1" cannot be
    /// misread as a percentage.
    /// </summary>
    public string TableNudgeUpLabel => $"+ {TableNudge:0.###}";

    public string TableNudgeDownLabel => $"− {TableNudge:0.###}";

    public string TableScaleUpLabel => $"+ {TableScaleStep:0.###}%";

    public string TableScaleDownLabel => $"− {TableScaleStep:0.###}%";

    /// <summary>The unit an absolute step is in, which is the table's own.</summary>
    public string TableStepUnits => TableEdit?.Units ?? "";

    private double _scaleStep = 1;

    public double TableScaleStep
    {
        get => _scaleStep;
        set
        {
            if (!Set(ref _scaleStep, value)) return;

            Raise(nameof(TableScaleUpLabel));
            Raise(nameof(TableScaleDownLabel));
        }
    }

    /// <summary>
    /// What the selected cells would become, spelled out before anything is sent.
    ///
    /// The question this exists to answer is not "by how much" but "to what".
    /// Those are only the same question if you can remember what the cell said
    /// four nudges ago, and nobody can.
    /// </summary>
    public TuneChange? TableChange =>
        TableEdit is { } edit ? edit.Preview(SelectedCells) : null;

    /// <summary>Which cell or region the readout is describing.</summary>
    public string TableChangeWhere
    {
        get
        {
            if (TableEdit is not { } edit) return "";

            TuneSelection area = SelectedCells.ClampedTo(edit.Columns, edit.Rows);

            if (area.Count != 1) return $"{area.Columns}×{area.Rows} cells";

            return $"{edit.Table.X.Breakpoints[area.Left]:G5} {edit.Table.X.PlainUnits}".TrimEnd()
                 + " × "
                 + $"{edit.Table.Y.Breakpoints[area.Top]:G5} {edit.Table.Y.PlainUnits}".TrimEnd();
        }
    }

    private string Format(double value) =>
        TableEdit is { } edit
            ? value.ToString("0." + new string('#', Math.Max(1, edit.Digits)))
            : value.ToString("0.##");

    /// <summary>What the ECU has now.</summary>
    public string TableChangeFrom => TableChange is not { } c
        ? ""
        : c.IsSingle ? Format(c.From) : $"{Format(c.FromLow)}–{Format(c.FromHigh)}";

    /// <summary>What it would become.</summary>
    public string TableChangeTo => TableChange is not { } c
        ? ""
        : c.IsSingle ? Format(c.To) : $"{Format(c.ToLow)}–{Format(c.ToHigh)}";

    /// <summary>The move itself, which is the part that used to be all there was.</summary>
    public string TableChangeDelta
    {
        get
        {
            if (TableChange is not { Any: true } c) return "";

            string units = TableEdit?.Units ?? "";

            // "More" and "less" rather than a second signed percentage. On a VE
            // table the cells are themselves per cent, so "+2 % (+2%)" reads as
            // the same figure twice when the two are quite different things —
            // two points of VE, and two per cent of what was there.
            if (c.IsSingle)
                return double.IsNaN(c.Percent)
                    ? $"{c.Delta:+0.###;−0.###} {units}".Trim()
                    : $"{c.Delta:+0.###;−0.###} {units}".Trim()
                      + $"   ({Math.Abs(c.Percent):0.0}% {(c.Percent >= 0 ? "more" : "less")})";

            return c.Uniform
                ? $"{c.DeltaLow:+0.###;−0.###} {units} on every cell".Trim()
                : $"{c.DeltaLow:+0.###;−0.###} to {c.DeltaHigh:+0.###;−0.###} {units}".Trim();
        }
    }

    /// <summary>Whether there is a change worth drawing an arrow for.</summary>
    public bool HasTableChangePreview => TableChange is { Any: true };

    /// <summary>Where the change stands: edited, sent, or neither.</summary>
    public string TableChangeState => TableChange is not { } c
        ? ""
        : c.Any
            ? $"not sent · {TableEdit?.ChangedCount ?? 0} changed in this table"
            : "unchanged";

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
        RaiseTablePreview();
    }

    private void RaiseTablePreview()
    {
        Raise(nameof(TableChange));
        Raise(nameof(TableChangeWhere));
        Raise(nameof(TableChangeFrom));
        Raise(nameof(TableChangeTo));
        Raise(nameof(TableChangeDelta));
        Raise(nameof(HasTableChangePreview));
        Raise(nameof(TableChangeState));
        Raise(nameof(TableNudgeUpLabel));
        Raise(nameof(TableNudgeDownLabel));
        Raise(nameof(TableStepUnits));
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
        // What the tune is comes before what is on screen. The same refusal the
        // settings and the burn make, and for the same reason: a placeholder
        // tune is all zeros and a tune from a file belongs to whatever saved it.
        // Asked first so that the answer does not depend on whether a table
        // happens to be open — a greyed button is not the whole guard, since
        // this is reachable from a scripted run as well.
        if (TuneIsPlaceholder)
            return "This is a firmware definition rather than a tune — every value in it reads as "
                   + "zero, so nothing here may be sent to a controller.";

        if (TuneIsFromFile)
            return "This tune was opened from a file rather than read off the controller, so it "
                   + "cannot be sent back. Read the ECU's own tune first.";

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
                page, layout.BlockingFactor, layout.LittleEndian, write.Offset, write.Data,
                layout.InterWriteDelay);

            // The tune held in memory has to move with the ECU, or the next
            // read-modify-write would be against stale bytes and would undo
            // this one.
            tune.Accept(write);

            // And so does the settings edit, which holds a copy of the pages
            // taken when it was made and works out what to send by comparing the
            // two. Left behind, those table bytes read as pending settings
            // changes carrying the values from before this write — so the next
            // time anything is sent from a settings page it puts the table back,
            // on a running engine, with nothing reporting an error.
            _settingsEdit?.Accept(write);

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
            connection.BurnPage(page, layout.LittleEndian, layout.AfterBurnDelay);

            // That page is in flash now, whatever put it there. A settings
            // change sent to the same page is committed by this burn as surely
            // as by its own, so leaving it listed keeps the settings Burn button
            // lit over a page already written — and pressing it spends a second
            // erase for nothing. Striking each page off as it lands is what the
            // settings burn documents itself as existing to do; burning from the
            // table side is the sibling path that was not doing it.
            _settingsPagesWritten.Remove(page.Index);
            RaiseWriteGates();

            return $"Burned page {page.Index}. This survives a power cycle.";
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            // Never "the burn failed" over a message that says it may well have
            // succeeded. Only a refusal is a failure this end can vouch for;
            // anything else is an unconfirmed burn, and its own message already
            // says what that means and how to check.
            return e is EcuProtocolException { Refused: false } ? e.Message : $"The burn failed: {e.Message}";
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

    /// <summary>
    /// Whether Calibration should say there is no tune.
    ///
    /// Not the same as there being no tune. A standard vehicle has none and never
    /// will, but it is not waiting to be connected either — it is connected, and
    /// the tab is showing it its fault codes. Telling that user to "connect to an
    /// ECU" was the tab describing a state the application was not in.
    /// </summary>
    public bool ShowNoTuneNotice => _ecuTune is null && !IsObd2Live;

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
        _ecuInterface = null;
        _ecuCurves = new Dictionary<string, TuneCurve>(StringComparer.OrdinalIgnoreCase);
        _curveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        OpenCurves = [];
        _settingsEdit = null;
        _derived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _settingsPagesWritten.Clear();
        TuneIsPlaceholder = false;
        TuneIsFromFile = false;

        // Everything that described where the last tune came from. The symbols
        // especially: they say which build a definition should be read as, and
        // carrying MS2Extra's over to a Speeduino would write a file whose
        // signature and whose conditionals disagree — which takes the wrong
        // branch everywhere it is read back, silently.
        _tuneFile = null;
        _tuneSymbols = null;
        EcuTables.Clear();
        SettingsMenu.Clear();

        // Said out loud rather than left for the list box to notice. Clearing
        // the menu happens to drive the selection to null and close the page,
        // but only while that list is on screen — on the tables half it is not
        // realised, and the page would stay bound to the edit just discarded.
        OpenDialog = null;
        _openMenuEntry = null;
        Raise(nameof(OpenDialog));
        Raise(nameof(OpenMenuEntry));
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

            // The settings interface: what the firmware calls each of its eight
            // hundred-odd constants, which page it belongs on, and when it
            // applies at all.
            _ecuInterface = TuneInterfaceReader.Read(iniText);
            _ecuCurves = TuneCurveReader.Read(iniText);
            _curveNames = Named(_ecuCurves, _ecuTune);
            _derived = DerivedChannels.Read(iniText);
            _settingsEdit = new TuneSettingsEdit(tune);
            BuildSettingsMenu();

            foreach (TuneTable table in Ordered(tune.Tables(_ecuTableDefinitions)))
                EcuTables.Add(table);

            EcuTableChoices.Refresh();

            EcuTuneSummary =
                $"{layout.TotalSize:N0} bytes read from the ECU in {clock.ElapsedMilliseconds:N0} ms · "
                + $"{EcuTables.Count} tables · {tune.Scalars().Count:N0} settings"
                + (_ecuInterface is { IsEmpty: false } ui
                    ? $" · {ui.Dialogs.Count:N0} pages"
                    : "");
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            EcuTuneSummary = $"The ECU's settings could not be read: {e.Message}";
        }
        finally
        {
            Raise(nameof(HasEcuTune));
            Raise(nameof(NoEcuTune));
            Raise(nameof(ShowNoTuneNotice));
            Raise(nameof(EcuTableSummary));
            Raise(nameof(HasSettingsPages));
            Raise(nameof(SettingsSummary));
            RaiseWriteGates();

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
        string? recording = _settings.RecordOnConnect ? Workspace.NewRecording(DateTime.Now) : null;

        Live = new LiveSession(source, new LiveSessionSettings
        {
            RecordingPath = recording,
            MaximumRate = LiveRate,
        });

        Live.Start();

        _livePort = port;
        _liveSignature = "MaxxECU";
        _liveVersion = "";
        _liveIni = "";
        _liveRecording = recording ?? "";

        SeedMaxxGauges();

        Status = $"Live — MaxxECU   •   {Live.Names.Count} channels";
        Title = "Live: MaxxECU — OpenLogViewer";
        Hint = $"{Opening(recording)} A MaxxECU sends a fixed set of channels, "
               + "and its tune cannot be read, so calibration is not available for it.";

        Raise(nameof(IsLive));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanExport));
        Raise(nameof(CanRecord));
        Raise(nameof(CanReconnect));
        RaiseRecording();
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
    public void ConnectObd2(string port) =>
        StartObd2(Elm327Source.ConnectOnPort(port, BatchMemory), port);

    /// <summary>
    /// What batching has already cost each adapter, kept in the settings file.
    ///
    /// One instance for every OBD2 route, because the thing being remembered is
    /// a property of the dongle rather than of how it was reached.
    /// </summary>
    private IObd2BatchMemory BatchMemory => _batchMemory ??= new SettingsBatchMemory(_settings);

    private IObd2BatchMemory? _batchMemory;

    private sealed class SettingsBatchMemory(SettingsStore settings) : IObd2BatchMemory
    {
        public int DeathsOn(string adapter) => settings.Obd2BatchDeaths.GetValueOrDefault(adapter);

        public void Died(string adapter) => settings.RecordObd2BatchDeath(adapter);
    }

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
            Elm327Source.Connect(
                new BleEcuTransport(adapter.Address, adapter.Name), memory: BatchMemory),
            adapter.Label);
    }

    /// <summary>
    /// Connects to an OBD2 adapter over Wi-Fi — a Vgate iCar Pro and the clones
    /// built like it.
    ///
    /// The same ELM327 conversation again, over a TCP socket this time. These are
    /// invisible to everything else here: no COM port, no pairing, nothing in any
    /// list Windows keeps, so an address is the only way to reach one and the
    /// computer has to be on the dongle's own Wi-Fi before it means anything.
    /// </summary>
    /// <param name="address">
    /// "host" or "host:port", defaulting to the port these adapters listen on.
    /// Empty tries each address one is known to answer at.
    /// </param>
    public void ConnectObd2Wifi(string address = "") =>
        StartObd2Session(OpenWifiAdapter(address));

    /// <summary>
    /// Connects to a Wi-Fi adapter without starting a session on it.
    ///
    /// The two halves belong to different threads, which is why they are
    /// separate calls. Opening a socket to a dongle that may not be there costs
    /// seconds of waiting, so the window does this part in the background;
    /// everything after it touches the gauges and the dashboard.
    ///
    /// What this gives the window that calling
    /// <see cref="Elm327Source.ConnectOverWifi(string, IObd2BatchMemory?)"/>
    /// directly does not is the batch memory. It lives here, with the settings,
    /// and an adapter reached without it learns what batching costs it again on
    /// every drive — at the price of a dropped session each time, since that is
    /// the only way the thing can be learnt.
    /// </summary>
    /// <param name="address">
    /// "host" or "host:port". Empty tries each address one is known to answer at.
    /// </param>
    public Elm327Source OpenWifiAdapter(string address = "") =>
        address.Trim().Length > 0
            ? Elm327Source.ConnectOverWifi(address, BatchMemory)
            : Elm327Source.ConnectOverWifi(BatchMemory);

    /// <summary>
    /// Starts a session on an OBD2 adapter that is already talking.
    ///
    /// Its own entry because connecting is the slow part and this is not: opening
    /// a socket to a dongle that is not there takes seconds and is pure protocol,
    /// so the window does it on a background thread and hands the result over
    /// here. Everything from this point touches the gauges and the dashboard,
    /// which belong to the interface thread.
    /// </summary>
    public void StartObd2Session(Elm327Source source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Labelled with the address that answered rather than the one that was
        // asked for. A connection with no address given tries several, and a
        // session named after the wrong one of them is a note about a device
        // that was never reached.
        StartObd2(source, source.Link.Length > 0 ? $"Wi-Fi {source.Link}" : "OBD2");
    }

    /// <summary>
    /// The same, over a link that is already decided on.
    ///
    /// Separated so the whole path — discovery, channels, which gauges reach the
    /// dashboard — can be exercised against an adapter in software. There is no
    /// other way to test it: a car is required otherwise, and the parts that have
    /// actually gone wrong here are in choosing gauges rather than in the wire.
    ///
    /// It carries the batch memory because the route it stands in for does. A
    /// seam that leaves out what the real path includes cannot catch a real path
    /// that leaves it out, which is exactly how the Wi-Fi route came to be
    /// connecting without it while its tests went on passing.
    /// </summary>
    public void ConnectObd2(IEcuTransport transport, string port) =>
        StartObd2(Elm327Source.Connect(transport, memory: BatchMemory), port);

    // ----- Subaru's own protocol ---------------------------------------------

    /// <summary>
    /// Connects to a Subaru over SSM, reading the addresses from the definitions
    /// folder.
    ///
    /// A separate act from connecting over OBD2, because it is a different
    /// conversation with a different set of channels — and because the addresses
    /// are the user's to supply, so this can fail in a way a standard OBD2
    /// connection never does.
    /// </summary>
    public void ConnectSsm(string port) =>
        ConnectSsm(new SerialEcuTransport(port) { OpenAttempts = 3 }, port);

    /// <summary>The same, over a link already decided on, so the path can be tested.</summary>
    public void ConnectSsm(IEcuTransport transport, string port)
    {
        IReadOnlyList<SsmParameter> parameters =
            SsmParameterFile.ReadFrom(Workspace.EnsureDefinitions());

        StartSsm(SsmSource.Connect(transport, parameters), port);
    }

    /// <summary>Where the addresses are read from, for pointing somebody at it.</summary>
    public string SsmParameterPath =>
        SsmParameterFile.PathIn(Workspace.Ensure(Workspace.Definitions));

    private void StartSsm(SsmSource source, string port)
    {
        Disconnect();

        string? recording = _settings.RecordOnConnect ? Workspace.NewRecording(DateTime.Now) : null;

        Live = new LiveSession(source, new LiveSessionSettings
        {
            RecordingPath = recording,

            // No point pacing above what the link can do. One address per request
            // at about 146 ms means a handful of parameters is under 1 Hz, and a
            // faster cap would only spin the loop.
            MaximumRate = Math.Min(LiveRate, 5),
        });

        Live.Start();

        _livePort = port;
        _liveSignature = source.Adapter.Length > 0 ? $"SSM · {source.Adapter}" : "SSM";
        _liveVersion = "";
        _liveIni = SsmParameterPath;
        _liveRecording = recording ?? "";

        SeedSsmGauges(source);

        SerialPortNames.Remember(port, _liveSignature);
        _settings.SetKnownEcus(SerialPortNames.Remembered());
        _settings.SetEcuLastUsed(SerialPortNames.LastUsed());
        Raise(nameof(ReconnectLabel));
        Raise(nameof(CanReconnect));

        Status = $"Live — SSM   •   {Live.Names.Count} parameters   •   {source.Adapter}";
        Title = "Live: SSM — OpenLogViewer";

        Hint = $"{Opening(recording)} Reading {source.Parameters.Count} parameter"
               + $"{(source.Parameters.Count == 1 ? "" : "s")} over Subaru's own protocol — one "
               + "address per request, so this updates about once a second. That suits what SSM "
               + $"is for: values the ECU has learnt, which move over minutes. Edit {SsmParameterFile.Name} "
               + "in the definitions folder to change what is read.";

        Raise(nameof(IsLive));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanExport));
        Raise(nameof(CanRecord));
        RaiseRecording();
    }

    /// <summary>
    /// Builds gauges from the parameters the file declares.
    ///
    /// Every one of them reaches the dashboard, unlike OBD2 where a car reporting
    /// thirty parameters would open as thirty dials. A list somebody wrote by hand
    /// is already the short list.
    /// </summary>
    private void SeedSsmGauges(SsmSource source)
    {
        foreach (GaugeItem existing in AllGauges) existing.ShownChanged -= OnGaugeShownChanged;

        AllGauges.Clear();
        Dashboard.Clear();
        _gaugeIni = null;
        _revCounterScaled = false;

        foreach (SsmParameter parameter in source.Parameters)
        {
            var spec = new GaugeSpec
            {
                Name = parameter.Name,
                Channel = parameter.Name,
                Title = parameter.Name,
                Units = parameter.Units,

                // Named for the address as well as the parameter, because the
                // address is what a forum post or a definition file quotes and
                // the name is whatever the person typed.
                Category = $"SSM · 0x{parameter.Address:X6}",
                Low = parameter.Low,
                High = parameter.High,
                ValueDigits = parameter.Digits,
                LabelDigits = parameter.Digits > 1 ? 1 : parameter.Digits,

                // No warning or danger bands. The file says what an address is
                // and never what a safe value would be, and inventing a red arc
                // for a number nobody here understands would be worse than a
                // plain dial.
            };

            var item = new GaugeItem(spec, parameter.Name);
            item.Show(Units);
            item.Show(true);
            item.ShownChanged += OnGaugeShownChanged;

            AllGauges.Add(item);
            Dashboard.Add(item);
        }

        _projectTuneName = $"the addresses in {SsmParameterFile.Name}";

        GaugeChoices.Refresh();
        Raise(nameof(GaugeSummary));
        Raise(nameof(HasGauges));
        Raise(nameof(NoGauges));
    }

    /// <summary>
    /// The OBD2 link, where the live session is one.
    ///
    /// Kept apart from <c>_live</c> because fault scanning is the one thing here
    /// that is not a reading: it belongs to the adapter rather than to the session
    /// polling through it, and every other live source in this application has no
    /// such thing to offer.
    /// </summary>
    private Elm327Source? _obd2;

    // ----- getting back to the ECU you were on last --------------------------

    /// <summary>
    /// The device to offer as one click, or null when there is nothing to offer.
    ///
    /// The most recently used of the devices that are actually present. Presence
    /// matters as much as recency: a USB adapter that is unplugged is not in the
    /// port list at all, and a button offering to connect to it would be offering
    /// a wait and then a failure. A paired Bluetooth port is always listed
    /// whether or not the ECU has power, so that one can still disappoint — but
    /// it is the best guess available and it is one click to find out.
    /// </summary>
    /// <remarks>
    /// A device with no recorded time still counts. Every profile written before
    /// times were kept has signatures and no timestamps, so requiring one would
    /// hide the shortcut from exactly the people who have been using this longest
    /// — and it would come back on its own after the next connection, which makes
    /// it look intermittent rather than broken. Undated devices simply sort last.
    /// </remarks>
    public SerialPortInfo? LastConnected =>
        SerialPortNames.Describe()
            .Where(p => p.IsKnown && !p.IsIncoming)
            .OrderByDescending(p => p.LastUsed ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

    /// <summary>Whether the shortcut has anything to point at.</summary>
    public bool CanReconnect => !IsLive && LastConnected is not null;

    /// <summary>
    /// What the shortcut says it will do.
    ///
    /// Named after the ECU rather than the port, because the port number is
    /// Windows' business and changes with the order things were plugged in. What
    /// somebody remembers is that they were on the Speeduino.
    /// </summary>
    public string ReconnectLabel => LastConnected is { } port
        ? $"Connect: {(port.KnownEcu.Length > 0 ? Shorten(port.KnownEcu) : port.PortName)}"
        : "Connect";

    /// <summary>
    /// An ECU signature cut down to something that fits on a button.
    ///
    /// Firmware signatures are not names — a MegaSquirt reports "MS2/Extra 3.4.1
    /// release 20151223 14:04GMT(c)KC/JSM/JB uS", which is a build stamp with a
    /// copyright notice in it. The first couple of words are the part a person
    /// would say out loud.
    /// </summary>
    private static string Shorten(string signature)
    {
        string[] words = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length <= 2 ? signature : string.Join(' ', words.Take(2));
    }

    /// <summary>
    /// Forgets every device this has connected to.
    ///
    /// Only the devices. Presets, filters and calculated channels are somebody's
    /// work and are not swept up in this — the reason to reach for it is a dongle
    /// that has been sold or an adapter replaced, whose name keeps appearing in a
    /// list of things that are no longer there.
    /// </summary>
    public string ForgetKnownEcus()
    {
        int count = _settings.KnownEcus.Count;

        SerialPortNames.Forget();
        _settings.ForgetKnownEcus();

        Raise(nameof(CanReconnect));
        Raise(nameof(ReconnectLabel));

        return count == 0
            ? "No devices were remembered."
            : $"Forgot {count} remembered device{(count == 1 ? "" : "s")}. "
              + "Presets, filters and calculated channels are untouched.";
    }

    /// <summary>Whether faults can be scanned for — which needs a standard vehicle.</summary>
    public bool IsObd2Live => _obd2 is not null && IsLive;

    private IReadOnlyList<byte> _obd2Undecoded = [];

    /// <summary>
    /// Parameters this car offers that this application cannot yet read.
    ///
    /// Worth naming in the interface rather than leaving in a log nobody opens.
    /// Every one is a channel the vehicle is willing to send, and the difference
    /// between "your car does not report that" and "this does not decode it yet"
    /// is the difference between a dead end and a morning's work.
    /// </summary>
    public string Obd2Gaps => _obd2Undecoded.Count == 0
        ? ""
        : $"{_obd2Undecoded.Count} more parameter{(_obd2Undecoded.Count == 1 ? "" : "s")} "
          + $"this vehicle supports but this cannot decode yet: "
          + string.Join(", ", _obd2Undecoded.Select(p => $"0x{p:X2}"));

    /// <summary>
    /// Asks the car what it is complaining about.
    ///
    /// Runs where it is called from, which is the user interface thread, and takes
    /// a second or two on a car with codes to report. Left synchronous on purpose:
    /// it holds the adapter for that time and the gauges visibly stop, which is
    /// honest about what is happening to the link.
    /// </summary>
    public FaultScan? ScanFaults() => _obd2?.ReadFaults();

    /// <summary>
    /// Asks the car to erase them. See <see cref="Obd2Faults.Clear"/> for what
    /// that costs beyond the codes themselves.
    /// </summary>
    public FaultClear? ClearFaults() => _obd2?.ClearFaults();

    private void StartObd2(Elm327Source source, string port)
    {
        Disconnect();

        string? recording = _settings.RecordOnConnect ? Workspace.NewRecording(DateTime.Now) : null;

        Live = new LiveSession(source, new LiveSessionSettings
        {
            RecordingPath = recording,
            MaximumRate = LiveRate,
        });

        Live.Start();

        _obd2 = source;
        _livePort = port;
        _liveSignature = source.Adapter.Length > 0 ? source.Adapter : "OBD2";
        _liveVersion = "";
        _liveIni = "";
        _liveRecording = recording ?? "";

        // What the car offers that this cannot read yet. Reported rather than
        // discarded silently: it is the only way anyone finds out that a vehicle
        // was willing to send something this application threw away.
        _obd2Undecoded = source.Undecoded;

        SeedObd2Gauges(source);

        SerialPortNames.Remember(port, _liveSignature);
        _settings.SetKnownEcus(SerialPortNames.Remembered());
        _settings.SetEcuLastUsed(SerialPortNames.LastUsed());
        Raise(nameof(ReconnectLabel));
        Raise(nameof(CanReconnect));

        Status = $"Live — OBD2   •   {Live.Names.Count} channels   •   {_liveSignature}";
        Title = $"Live: OBD2 — OpenLogViewer";
        Hint = $"{Opening(recording)} "
               + (source.Batching
                   ? "This car answers several parameters to one request, so a round of readings "
                     + "is two exchanges rather than six — still slower than a tuning cable, and "
                     + "much better than OBD2 usually manages. "
                   : "OBD2 asks for one parameter at a time, so this updates about twice a second "
                     + "rather than 25 times — the protocol's limit, not the link's. ")
               + "A standard vehicle has no tune to read, so calibration "
               + "shows its fault codes instead."
               + (Obd2Gaps.Length > 0 ? $"  {Obd2Gaps}." : "");

        Raise(nameof(IsLive));
        Raise(nameof(IsObd2Live));
        Raise(nameof(ShowNoTuneNotice));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanExport));
        Raise(nameof(CanRecord));
        Raise(nameof(CanReconnect));
        RaiseRecording();
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

    public void Connect(string port, bool bluetooth = false) =>
        Connect(
            // A Bluetooth link is the same protocol over a virtual COM port, but
            // it answers in hundreds of milliseconds where a cable answers in
            // three, so it needs longer to reply and longer to fall quiet
            // between attempts.
            new SerialEcuTransport(port) { OpenAttempts = bluetooth ? 3 : 1 },
            port,
            bluetooth ? EcuConnectionSettings.Bluetooth : null);

    /// <summary>
    /// Everything a connection is, over a transport already chosen.
    ///
    /// <para>
    /// Split out for the same reason <see cref="ConnectObd2"/> is: a seam that
    /// leaves out what the real path includes cannot catch a real path that
    /// leaves it out. Everything below this line — matching the definition,
    /// reading the tune, building the settings menu, the gates that decide what
    /// may be written and burned — had no test of any kind, because the only way
    /// in built its own serial port. That is where three reviews running have
    /// found the same defect: state wired into one path and not its siblings.
    /// </para>
    /// </summary>
    public void Connect(IEcuTransport transport, string port, EcuConnectionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        Disconnect();

        var connection = new EcuConnection(transport, settings);

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
        _settings.SetEcuLastUsed(SerialPortNames.LastUsed());
        Raise(nameof(ReconnectLabel));
        Raise(nameof(CanReconnect));

        string iniText = TuningText.Read(ini.Path);
        RealtimeLayout layout = MsqIni.ReadOutputChannels(iniText);
        IReadOnlyList<DatalogEntry> datalog = MsqIni.ReadDatalog(iniText);

        // From here the firmware's own request format is used, which is the only
        // way one program reads both a MegaSquirt page and a rusEFI block.
        connection.Use(layout, _settings.SingleRequestBlock);

        _projectTune = ReadProjectTune(ini.Path);

        _ecuConnection = connection;

        // Kept so a tune read off this controller can be written to a file that
        // says which firmware it belongs to. Without it the file is a list of
        // numbers nobody — this program included — can place.
        _ecuSignature = signature;

        ReadTuneFromEcu(connection, iniText);
        SeedGauges(iniText, datalog);

        // The tune supplies what the wire does not: firmware derives channels
        // from settings such as the cylinder count as well as from live values.
        var decoder = new RealtimeDecoder(layout, MsqTune.ReadScalars(TuneXml));

        string? recording = _settings.RecordOnConnect ? Workspace.NewRecording(DateTime.Now) : null;

        Live = new LiveSession(connection, decoder, datalog, new LiveSessionSettings
        {
            RecordingPath = recording,
            MaximumRate = LiveRate,
        });

        Live.Start();

        _livePort = port;
        _liveSignature = signature;
        _liveVersion = version;
        _liveIni = ini.Path;
        _liveRecording = recording ?? "";

        Status = $"Live — {signature}   •   {Live.Names.Count} channels   •   {ini.Name}";

        // Worth saying plainly: on a bench almost nothing moves, and the default
        // filter then hides almost everything. The channels are all being
        // recorded regardless.
        string quiet = Live.Names.Count > 0
            ? "  Untick \"Hide unused\" to see every channel — all of them are being recorded either way."
            : "";
        Title = $"Live: {signature} — OpenLogViewer";
        Hint = $"{Opening(recording)} The plot follows the newest data until you zoom or pan." + quiet;

        Raise(nameof(IsLive));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanExport));
        Raise(nameof(CanRecord));
        Raise(nameof(CanReconnect));
        RaiseRecording();
    }

    /// <summary>Stops the session and closes the port, leaving the data in place.</summary>
    public void Disconnect()
    {
        if (_live is null) return;

        _live.Stop();
        _live.Dispose();
        Live = null;
        _ecuConnection = null;
        _obd2 = null;
        _obd2Undecoded = [];

        // A permission granted over whatever was just unplugged does not carry
        // over to whatever is plugged in next. The same laptop meets a bench
        // engine one afternoon and a car the next.
        AgentWritesArmed = false;

        LiveStatus = "";
        _livePort = _liveSignature = _liveVersion = _liveIni = _liveRecording = "";

        Raise(nameof(IsLive));
        Raise(nameof(IsObd2Live));
        Raise(nameof(ShowNoTuneNotice));
        Raise(nameof(LiveDetail));
        Raise(nameof(CanRecord));
        Raise(nameof(CanReconnect));
        Raise(nameof(ReconnectLabel));
        RaiseWriteGates();
        RaiseRecording();

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

        // The dot is the link; whether anything is being written is said
        // separately, because the two are now independent and a filled dot on a
        // session recording nothing would be the misreading that matters.
        // Retries only appear once there are any, so a healthy link stays quiet.
        LiveStatus = status switch
        {
            { Faulted: true } => status.Error!,
            { Reconnecting: true } => $"○ {_livePort} · waiting for the ECU to come back…",
            _ => $"● {_livePort} · {_liveSignature} · {status.Rate:F1} Hz" +
                 (status.Retries > 0 ? $" · {status.Retries} retries" : "") +
                 $" · {Written()}",
        };

        // Cheap, and this is the only thing that ticks while a session runs — so
        // it is what makes a growing recording visibly grow.
        Raise(nameof(RecordLabel));
        Raise(nameof(RecordingSummary));

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
            ApplyStyle(item);
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
            if (Document?.EmbeddedTune is { Length: > 0 }) return "from the log";

            // Named rather than called "none": the log has one and cannot use it,
            // which is a different situation from having none.
            return Document?.UnreadableTune is { Length: > 0 } format
                ? $"{format}, unreadable"
                : "none";
        }
    }

    /// <summary>The longer form, for the tooltip on the toolbar indicator.</summary>
    public string TuneDetail
    {
        get
        {
            if (_loadedTune is not null)
                return $"Tables come from {_loadedTuneName}. Right-click to go back to the log's own tune.";

            if (Document?.EmbeddedTune is { Length: > 0 })
                return "Tables come from the tune stored in this log — the one that was running "
                       + "when it was recorded.";

            // Telling a MaxxECU owner to open a .msq sends them after a file
            // their ECU does not produce. What is true is that the tune is
            // there and this cannot read it.
            if (Document?.UnreadableTune is { Length: > 0 } format)
                return $"This log carries its {format} tune, in a format this cannot read. "
                       + "Axis breakpoints and VE Calibration need a tune it can, so both are "
                       + "unavailable for this log.";

            return "This log carries no tune. Open a .msq to bin onto its table axes and to use "
                   + "VE Calibration.";
        }
    }

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

    private double _veDelaySeconds;

    /// <summary>
    /// How long the wideband takes to see the mixture that was metered now, in
    /// seconds.
    ///
    /// Seconds rather than samples because that is the physical quantity — it is
    /// a pipe length and a sensor, not a log rate — and the same setting then
    /// means the same thing on a log recorded at 40 Hz and one at 2 Hz.
    ///
    /// Capped at two seconds. Beyond that it is no longer a transport delay but
    /// a way of comparing a reading against an unrelated part of the drive.
    /// </summary>
    public double VeDelaySeconds
    {
        get => _veDelaySeconds;
        set { if (Set(ref _veDelaySeconds, Math.Clamp(value, 0, 2))) HistogramInvalidated?.Invoke(); }
    }

    /// <summary>
    /// The delay in samples, which is what the analysis works in.
    ///
    /// Rounded from the log's own median interval, so a request for 300 ms is
    /// however many samples that is in this recording. Reported back so the
    /// panel can say what was actually applied: on a 2 Hz OBD2 log the smallest
    /// step is half a second, and silently rounding 300 ms to nothing would look
    /// like the setting doing something when it is doing nothing.
    /// </summary>
    public int VeDelaySamples
    {
        get
        {
            if (Document is not { } doc || _veDelaySeconds <= 0) return 0;

            double interval = doc.MedianSampleInterval;
            return interval <= 0 ? 0 : (int)Math.Round(_veDelaySeconds / interval);
        }
    }

    private string _veDelayFinding = "";

    /// <summary>What the last search of the log concluded, or nothing.</summary>
    public string VeDelayFinding
    {
        get => _veDelayFinding;
        private set
        {
            if (Set(ref _veDelayFinding, value)) Raise(nameof(HasVeDelayFinding));
        }
    }

    public bool HasVeDelayFinding => _veDelayFinding.Length > 0;

    /// <summary>
    /// Asks the log how long the sensor takes, and takes the answer.
    ///
    /// The value is applied only when the search is prepared to stand behind it;
    /// a search that cannot say leaves the setting exactly as it was and says
    /// why. Answering with a number anyway would be the one outcome worse than
    /// not offering this at all, since a made-up delay is indistinguishable from
    /// a measured one once it is sitting in the box.
    /// </summary>
    public void FindVeDelay()
    {
        if (Document is not { SampleCount: > 0 } doc)
        {
            VeDelayFinding = "Open a log first.";
            return;
        }

        if (XAxis is null || YAxis is null || ZAxis is null || _zCompare.Channel is null)
        {
            VeDelayFinding = "Pick the axes and a target channel first.";
            return;
        }

        SampleMask mask = SampleFilter.Build(doc, Filters.Select(f => f.Filter));

        // The same grid the analysis uses, so a delay found here is a delay
        // measured against the cells it will be applied to.
        TuneTable grid = _axisSource.Table
            ?? VeAnalysis.GridFrom(
                XAxis.Channel, YAxis.Channel, _columns, _rows, 0, doc.SampleCount - 1, mask);

        DelaySearchResult found = WidebandDelay.Find(
            grid, XAxis.Channel, YAxis.Channel, ZAxis.Channel, _zCompare.Channel.Channel,
            0, doc.SampleCount - 1, doc.MedianSampleInterval, mask);

        if (found.HasProblem)
        {
            VeDelayFinding = found.Problem!;
            return;
        }

        VeDelaySeconds = found.BestSeconds;

        // The band, not just the winner. Several neighbouring candidates
        // routinely sit within the noise of each other, and reporting only the
        // lowest would claim a precision the log does not carry.
        string over = $"over {found.SamplesScored:N0} samples";

        if (found.IsPrecise)
        {
            VeDelayFinding = found.BestSamples == 0
                ? $"No delay — the readings already line up, {over}."
                : $"{found.BestSeconds:F2} s, {over}.";
            return;
        }

        VeDelayFinding = found.NoneIsPlausible
            ? $"Somewhere between none and {found.HighSeconds:F2} s; {found.BestSeconds:F2} s fits "
              + $"best, but not by enough to tell them apart {over}. Anything longer is worse."
            : $"Between {found.LowSeconds:F2} and {found.HighSeconds:F2} s; {found.BestSeconds:F2} s "
              + $"fits best, {over}.";
    }

    /// <summary>What the delay setting actually came to, for saying so.</summary>
    public string VeDelayNote
    {
        get
        {
            if (_veDelaySeconds <= 0) return "No delay — readings are credited to the moment they arrive.";

            int samples = VeDelaySamples;

            return samples == 0
                ? "Less than one sample at this log's rate, so nothing is shifted."
                : $"{samples:N0} sample{(samples == 1 ? "" : "s")} at this log's rate.";
        }
    }

    /// <summary>
    /// True when the analysis has what it needs, which is an AFR target to
    /// compare against and nothing more.
    ///
    /// It used to require the tune's own values, which meant it could not run on
    /// a log by itself — every MaxxECU, whose tune cannot be read at all, and
    /// every log opened away from the car. The tune's grid is still much better
    /// when there is one, because its cells line up with the cells being tuned;
    /// it is no longer the difference between working and not.
    /// </summary>
    public bool VeAvailable => Document is not null && XAxis is not null && YAxis is not null;

    public string VeSummary { get; private set; } = "";

    /// <summary>
    /// Runs the analysis, or explains why it cannot. Returns false to fall back
    /// to an ordinary binned table, so switching to VE Calibration without the parts
    /// it needs still shows something rather than an empty panel.
    /// </summary>
    private bool BuildVeAnalysis(int firstSample, int lastSample, SampleMask mask, LogChannel? target)
    {
        // The tune's own grid where there is one, so each cell lines up with a
        // cell being tuned and the result can be read straight across. Failing
        // that, a grid off the log — which cannot be pasted anywhere, but still
        // says where the mixture is out and by how much.
        TuneTable tune = _axisSource.Table
            ?? VeAnalysis.GridFrom(
                XAxis!.Channel, YAxis!.Channel, _columns, _rows, firstSample, lastSample, mask);

        bool fromTune = _axisSource.Table is not null;

        if (target is null)
        {
            VeSummary = "Set \"Compare against\" to the AFR target channel.";
            Raise(nameof(VeSummary));
            return false;
        }

        VeAnalysisResult result = VeAnalysis.Analyse(
            tune, XAxis!.Channel, YAxis!.Channel, ZAxis!.Channel, target,
            firstSample, lastSample, mask,
            new VeAnalysisSettings
            {
                MinimumSamples = _veMinimumSamples,
                MaxChangePercent = _veMaxChange,
                MeasurementDelaySamples = VeDelaySamples,

                // Tied to the trust threshold rather than given a knob of its
                // own: both answer "how much data before I believe a cell", and
                // two numbers for one question is two ways to be inconsistent.
                ConfidenceSamples = _veMinimumSamples,
            });

        // A mismatch is not a thin result, it is a wrong one, and it must not be
        // drawn. Falling back to the ordinary binned table shows the data
        // honestly and says why.
        if (result.HasProblem)
        {
            VeResult = null;
            VeSummary = result.Problem!;
            Hint = result.Problem!;

            Raise(nameof(VeSummary));
            return false;
        }

        VeResult = result;

        Table = _veShowSuggested
            ? result.AsSuggestedTable(XAxis.Channel, YAxis.Channel, ZAxis.Channel, firstSample, lastSample, mask)
            : result.AsChangeTable(XAxis.Channel, YAxis.Channel, ZAxis.Channel, target, firstSample, lastSample, mask);

        string grid = fromTune ? tune.Name : $"{tune.Name} — the log's own bins, not the ECU's";

        VeSummary = result.IsEmpty
            ? $"Nothing to suggest — no cell reached {_veMinimumSamples} samples."
            : $"{result.CellsSuggested} of {tune.Columns * tune.Rows} cells, " +
              $"{result.CellsThin} too thin, largest change {result.LargestChangePercent:F1}%, " +
              $"from {result.SamplesUsed:N0} samples   ·   {grid}";

        Hint = result.IsEmpty
            ? "No cell has enough samples yet. Lower the sample threshold, or drive the untouched areas."
            : fromTune
                ? $"Suggesting {result.CellsSuggested} cells of {tune.Name}. "
                  + "Cells with too little data are left alone. Export the table to paste it into your tuning app."
                : $"Showing how far out {result.CellsSuggested} cells are, binned on the log's own range "
                  + "rather than the ECU's breakpoints — open the tune, or connect, to get a table that "
                  + "lines up cell for cell with the one you are tuning.";

        Raise(nameof(VeSummary));
        return true;
    }

    /// <summary>
    /// Rebuilds <see cref="Points"/> over the given sample window, from the same
    /// three channels and the same filters the table uses.
    /// </summary>
    public void RebuildScatter(int firstSample, int lastSample)
    {
        if (Document is null || XAxis is null || YAxis is null || ZAxis is null)
        {
            Points = null;
            return;
        }

        SampleMask mask = SampleFilter.Build(Document, Filters.Select(f => f.Filter));

        Points = ScatterPlot.Build(
            XAxis.Channel, YAxis.Channel, ZAxis.Channel,
            firstSample, lastSample, mask, _zCompare.Channel?.Channel);

        if (Points.IsEmpty)
        {
            Hint = mask.FiltersApplied && mask.PassCount == 0
                ? "Every sample was filtered out — loosen or switch off a filter."
                : "No samples to plot — try a wider time range.";
            return;
        }

        var parts = new List<string> { $"{Points.Count:N0} samples" };

        // The Z comparison channel, which is a channel of this log. Not
        // CompareName — that is the second log opened for comparison, an
        // unrelated feature, and naming it here would be blank whenever no
        // second log is open and wrong whenever one is.
        if (Points.ZCompare is { } target) parts.Insert(0, $"difference against {target.Name}");

        // Both figures over the same window. Points.Filtered counts only what
        // was rejected inside it, so pairing it with the whole log's total would
        // read as "12 of 50,000" for a pull of five hundred.
        if (mask.FiltersApplied)
        {
            int considered = Points.Count + Points.Dropped + Points.Filtered;
            parts.Add($"{Points.Filtered:N0} of {considered:N0} excluded by filters");
        }

        // Said rather than absorbed: a scatter quietly missing a third of the
        // log would look like a sparser drive than it was.
        if (Points.Dropped > 0)
            parts.Add($"{Points.Dropped:N0} with a reading missing");

        if (mask.UnknownChannels.Count > 0)
            parts.Add($"not in this log: {string.Join(", ", mask.UnknownChannels.Distinct())}");

        Hint = string.Join("   •   ", parts);
    }

    /// <summary>
    /// Cells the span marked on the plot passed through, or null when nothing is
    /// marked. Set by <see cref="RebuildHistogram"/>, since it can only be known
    /// once there is a table to place the samples in.
    /// </summary>
    public CellVisits? VisitedCells { get; private set; }

    /// <summary>
    /// Adds what the marked span reached to the status line.
    ///
    /// Appended to whatever the table already said rather than replacing it: how
    /// many samples the table rests on and how many a filter excluded are still
    /// true, and are the numbers that explain a sparse table.
    /// </summary>
    private string DescribeVisited(CellVisits visits)
    {
        if (visits.IsEmpty)
            return visits.Outside > 0
                ? "the marked span falls outside this table"
                : "the marked span reached no cell";

        string text = $"the marked span reached {visits.Cells:N0} cell{(visits.Cells == 1 ? "" : "s")}";

        // A span mostly outside the table marks almost nothing, and looking
        // broken is the alternative to saying why.
        if (visits.Outside > 0)
            text += $" ({visits.Outside:N0} of its samples fall outside)";

        return text;
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
            VisitedCells = null;
            Hint = mask.FiltersApplied && mask.PassCount == 0
                ? "Every sample was filtered out — loosen or switch off a filter."
                : "No samples fall in this table — try a wider time range.";
            return;
        }

        // Where a span is marked on the plot, the cells it reached. Computed
        // after the table rather than with it: the cells only exist once the
        // axes are settled, and a filter or a change of breakpoints moves them.
        VisitedCells = Selection is { } span ? Table.VisitedBy(span.First, span.Last) : null;

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

        if (VisitedCells is { } visits) parts.Add(DescribeVisited(visits));

        if (ApplyComparison()) parts.Insert(0, $"difference against {CompareName}");

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

        // And whatever the last log was searched for. The runs are sample
        // numbers into that log, and stepping to one past the end of a shorter
        // one asks the plot to go to a time that is NaN — every comparison
        // against which is false, so the view bounds stay NaN and the plot draws
        // nothing at all, with nothing to say why.
        ClearFound();

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

            ApplyStyle(item);
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

    /// <summary>What the open log could estimate power from, and with what.</summary>
    public PowerEstimateResult? EstimatePower(EngineSpec spec) =>
        Document is { } doc ? PowerEstimate.For(doc, spec) : null;

    /// <summary>
    /// Adds the power estimate's channels, replacing any left from a previous go.
    ///
    /// Replacing rather than adding: the whole point is to try a figure, look at
    /// the plot and try another, and a store that grew a second "Power (speed
    /// density)" each time would fail on the name and silently keep showing the
    /// first attempt's answer.
    /// </summary>
    public int AddPowerChannels(EngineSpec spec)
    {
        if (EstimatePower(spec) is not { } estimate || estimate.Channels.Count == 0) return 0;

        foreach (MathChannel channel in estimate.Channels)
            foreach (MathChannel existing in _mathStore.Channels
                         .Where(c => c.Name.Equals(channel.Name, StringComparison.OrdinalIgnoreCase))
                         .ToList())
                _mathStore.Remove(existing);

        foreach (MathChannel channel in estimate.Channels) _mathStore.Add(channel);

        RefreshMathChannels();
        Reapply();

        Hint = $"Added {estimate.Channels.Count} channels from "
             + $"{string.Join(" and ", estimate.Methods.Select(m => m.Name.ToLowerInvariant()))}.";

        return estimate.Channels.Count;
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

                ApplyStyle(item);
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

    /// <summary>Writes the points behind the current scatter, one row per sample.</summary>
    public void ExportPointsCsv(string path)
    {
        if (Points is not { Count: > 0 } points) return;

        WriteAtomic(path, writer => CsvExport.WritePoints(writer, points));

        Hint = $"Saved {points.Count:N0} points to {Path.GetFileName(path)}";
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
        using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
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

    /// <summary>
    /// The same account for a mark on the scatter, with the spread named. A
    /// block whose samples disagreed is the one worth going back to the log for,
    /// so the readout says so before the plot is even framed.
    /// </summary>
    public void DescribeMarkTrace(
        ScatterPlot points,
        ScatterBins bins,
        (int Column, int Row) block,
        IReadOnlyList<(int First, int Last)> visits,
        (int First, int Last) longest)
    {
        int bin = bins.Index(block.Column, block.Row);
        int count = bins.Counts[bin];

        double x = bins.XMin + (block.Column + 0.5) / bins.Columns * (bins.XMax - bins.XMin);
        double y = bins.YMin + (block.Row + 0.5) / bins.Rows * (bins.YMax - bins.YMin);

        string where = $"{points.X.Name} {x:G6} · {points.Y.Name} {y:G6}";

        string visitText = visits.Count == 1
            ? "one visit"
            : $"{visits.Count} visits, all marked";

        string spread = count > 1 && bins.SpreadIn(block.Column, block.Row) > 0
            ? $" ({points.Z.Format(bins.Lowest[bin])} to {points.Z.Format(bins.Highest[bin])})"
            : "";

        Hint = $"{where} — {points.Z.Name} {points.Z.Format(bins.Means[bin])}{spread} from " +
               $"{count:N0} samples over {visitText}. " +
               $"Showing the longest ({longest.Last - longest.First + 1:N0} samples).";
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
        if (item.IsVisible && !item.HasFixedColor)
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

    // ----- a second log, read against the first ---------------------------------

    private LogDocument? _compare;
    private bool _showDifference = true;

    /// <summary>The log being compared against, where one is open.</summary>
    public LogDocument? CompareDocument => _compare;

    public bool HasComparison => _compare is not null;

    public bool NoComparison => _compare is null;

    /// <summary>What the two logs have in common, worked out when the second was opened.</summary>
    public ChannelOverlap? Overlap { get; private set; }

    /// <summary>The comparison log's name, for the toolbar.</summary>
    public string CompareName =>
        _compare is null ? "" : Path.GetFileName(_compare.FilePath);

    /// <summary>
    /// Whether the table shows the difference or just the first log.
    ///
    /// A toggle rather than a separate view, because the thing somebody actually
    /// does is look at one, look at the change, and look back.
    /// </summary>
    public bool ShowDifference
    {
        get => _showDifference;
        set { if (Set(ref _showDifference, value)) HistogramInvalidated?.Invoke(); }
    }

    /// <summary>What the comparison amounts to, for the sidebar.</summary>
    public string CompareSummary { get; private set; } = "";

    /// <summary>
    /// Opens a second log to read the first against.
    ///
    /// Reported rather than thrown: a file that will not load is an ordinary thing
    /// to pick by mistake, and it should cost the comparison rather than the
    /// session.
    /// </summary>
    public string LoadComparison(string path)
    {
        if (Document is null) return "Open a log first, then a second one to compare it against.";

        try
        {
            LogDocument second = LogReaderFactory.Load(path);

            Overlap = LogComparison.Compare(Document, second);

            if (!Overlap.AnythingShared)
            {
                Overlap = null;
                return Overlap?.Summary
                       ?? "That log shares no channel names with this one, so there is nothing "
                          + "to compare. They are probably from different firmware.";
            }

            _compare = second;

            RaiseComparison();
            HistogramInvalidated?.Invoke();

            return $"Comparing against {Path.GetFileName(path)}. {Overlap.Summary}";
        }
        catch (Exception e) when (e is LogFormatException or IOException
                                      or UnauthorizedAccessException or NotSupportedException)
        {
            return $"Could not open that log: {e.Message}";
        }
    }

    public void ClearComparison()
    {
        if (_compare is null) return;

        _compare = null;
        Overlap = null;
        CompareSummary = "";

        RaiseComparison();
        HistogramInvalidated?.Invoke();
    }

    private void RaiseComparison()
    {
        Raise(nameof(CompareDocument));
        Raise(nameof(HasComparison));
        Raise(nameof(NoComparison));
        Raise(nameof(CompareName));
        Raise(nameof(CompareSummary));
    }

    /// <summary>
    /// The same table built from the comparison log, on the first one's axes.
    ///
    /// On <em>the first one's</em> axes, and that is the whole trick. Two logs
    /// binned independently choose their own ranges from their own data, so their
    /// cells would not line up and subtracting them would compare 2,400 rpm
    /// against 2,650. Passing the first table's own centres as the second's
    /// breakpoints forces them onto one grid.
    ///
    /// The second log is read whole rather than over the zoomed range: a sample
    /// index means nothing between two files, and silently applying the first
    /// log's zoom to the second would compare a pull against whatever happened to
    /// be at the same offset in a different drive.
    /// </summary>
    private HistogramTable? CompareTable(HistogramTable against)
    {
        if (_compare is not { } second || XAxis is null || YAxis is null || ZAxis is null) return null;

        LogChannel? x = second.FindChannel(XAxis.Channel.Name);
        LogChannel? y = second.FindChannel(YAxis.Channel.Name);
        LogChannel? z = second.FindChannel(ZAxis.Channel.Name);

        if (x is null || y is null || z is null) return null;

        LogChannel? compareTo = _zCompare.Channel is { } option
            ? second.FindChannel(option.Channel.Name)
            : null;

        SampleMask mask = SampleFilter.Build(second, Filters.Select(f => f.Filter));

        return HistogramTable.Build(
            x, y, z,
            against.ColumnCenters, against.RowCenters,
            0, second.SampleCount - 1, _statistic, mask, compareTo);
    }

    /// <summary>
    /// Turns the table into a difference, where a comparison is open and wanted.
    ///
    /// Returns false when there is nothing to compare — a missing channel, or two
    /// runs that never visited the same cell — and says why, rather than leaving a
    /// table that looks empty for no stated reason.
    /// </summary>
    private bool ApplyComparison()
    {
        if (Table is null || _compare is null || !_showDifference) return false;

        HistogramTable? other = CompareTable(Table);

        if (other is null)
        {
            CompareSummary =
                $"{CompareName} does not carry all three of these channels, so the difference "
                + "cannot be worked out. Pick axes both logs have.";

            Raise(nameof(CompareSummary));
            return false;
        }

        (int both, int onlyFirst, int onlySecond) = LogComparison.Coverage(Table, other);

        if (both == 0)
        {
            CompareSummary =
                "The two runs never visited the same cell, so there is nothing to subtract. "
                + "They may have been driven in different gears or over a different range.";

            Raise(nameof(CompareSummary));
            return false;
        }

        Table = LogComparison.Difference(Table, other);

        ComparisonSummary summary = LogComparison.Summarise(Table);

        CompareSummary = summary.Any
            ? $"{both} cells in both runs · average change {summary.Mean:+0.##;-0.##;0}"
              + $" · largest {summary.Largest:+0.##;-0.##;0}"
              + $" at {summary.AtColumn:N0} × {summary.AtRow:N0}"
              + (onlyFirst + onlySecond > 0
                  ? $" · {onlyFirst} cells only this log, {onlySecond} only {CompareName}"
                  : "")
            : $"{both} cells overlap but none has enough samples to be worth reporting.";

        Raise(nameof(CompareSummary));

        return true;
    }
}
