using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Before CenterScreen does its arithmetic, so a window that would not
        // have fitted is centred at the size it ends up being.
        FitToWorkArea();

        _vm.PlotInvalidated += OnPlotInvalidated;
        _vm.HistogramInvalidated += RebuildHistogram;
        Plot.CursorSampleChanged += _vm.UpdateCursor;
        Plot.HoverChannelChanged += _vm.HighlightChannel;
        Plot.SelectionChanged += _vm.UpdateSelection;
        Histogram.CellActivated += OnHistogramCellActivated;
        Plot.ViewChangedByUser += () => _follow = false;

        // The title bar belongs to Windows, so it has to be coloured by hand —
        // and again on every theme change, or a dark scheme leaves a white strip
        // above it. Only once the handle exists, which is what SourceInitialized
        // marks.
        SourceInitialized += (_, _) => TitleBar.Apply(this, ThemeManager.Current);
        ThemeManager.Changed += OnThemeChanged;

        // A live session holds a port open and a file being written; neither
        // should outlive the window.
        Closed += (_, _) =>
        {
            ThemeManager.Changed -= OnThemeChanged;
            StopLive();
        };

        InputBindings.Add(new KeyBinding(new RelayCommand(Open), Key.O, ModifierKeys.Control));
    }

    private void OnThemeChanged(Theme theme) => TitleBar.Apply(this, theme);

    /// <summary>
    /// Shrinks the window to the screen it will open on.
    ///
    /// The declared size suits a desktop display. On a smaller one — a laptop
    /// panel, or a scaled desktop, which is the same thing in the units WPF
    /// measures in — centring a window taller than the screen puts its title bar
    /// above the top edge, where it cannot be dragged back down. The margin
    /// leaves room for the border and caption, whose thickness is not known
    /// until the window has a handle.
    /// </summary>
    private void FitToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;

        Width = Math.Max(MinWidth, Math.Min(Width, work.Width - 40));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height - 40));
    }

    /// <summary>Applies a theme for this run without recording it as the preference.</summary>
    public void PreviewTheme(string id) => _vm.PreviewTheme(id);

    /// <summary>Switches to the gauge dashboard, for a scripted run.</summary>
    public void ShowGauges() => _vm.Mode = WorkspaceMode.Gauges;

    /// <summary>Switches to calibration, optionally on a named table, for a scripted run.</summary>
    public void ShowCalibration(string? table)
    {
        _vm.Mode = WorkspaceMode.Calibration;

        if (table is null) return;

        _vm.SelectedEcuTable = _vm.EcuTables.FirstOrDefault(
            t => t.Name.Contains(table, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Connects to an ECU on startup, for a scripted run.
    ///
    /// Reports a failure to stderr rather than in a dialog: this runs before the
    /// window is shown, and a modal box there blocks startup with nothing behind
    /// it — which looks exactly like a hang.
    /// </summary>
    public void ConnectTo(string port)
    {
        App.Report($"connecting on {port}…");
        StartLive(port, quiet: true);
        App.Report(_vm.IsLive ? $"live: {_vm.Status}" : "not live");
    }

    // ----- live connection --------------------------------------------------

    /// <summary>
    /// How often the plot takes a new snapshot. Well under the poll rate on
    /// purpose: redrawing a few hundred channels for every block would spend
    /// more time painting than reading, and the eye cannot tell.
    /// </summary>
    private static readonly TimeSpan LiveRefresh = TimeSpan.FromMilliseconds(200);

    private DispatcherTimer? _liveTimer;

    /// <summary>
    /// True while the plot should stay on the newest data. Cleared as soon as the
    /// user zooms or pans, because from then on they are reading history.
    /// </summary>
    private bool _follow = true;

    /// <summary>
    /// Opens the connect menu and draws it to a PNG.
    ///
    /// A menu lives in its own top-level window, so it is in no render of this
    /// one; and a screen grab needs the app in front, which a background process
    /// cannot arrange. Rendering the menu's own visual is the only way to see
    /// what it looks like without a person at the keyboard.
    /// </summary>
    public void CaptureConnectMenu(string path)
    {
        OnConnectClick(this, new RoutedEventArgs());

        PortsMenu.UpdateLayout();
        ImageExport.Save(PortsMenu, path);

        // Closed and the capture released before returning. An open menu holds
        // the mouse and keeps WPF in menu mode, and a shutdown asked for from
        // inside that never arrives.
        PortsMenu.IsOpen = false;
        Mouse.Capture(null);
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsLive) { StopLive(); return; }

        PortsMenu.Items.Clear();

        IReadOnlyList<string> ports = _vm.SerialPorts;
        if (ports.Count == 0)
        {
            PortsMenu.Items.Add(new MenuItem { Header = "No serial ports found", IsEnabled = false });
        }
        else
        {
            foreach (string port in ports)
            {
                var item = new MenuItem { Header = port };
                item.Click += (_, _) => StartLive(port);
                PortsMenu.Items.Add(item);
            }
        }

        PortsMenu.Items.Add(new Separator());
        PortsMenu.Items.Add(RateMenu());

        PortsMenu.PlacementTarget = ConnectButton;
        PortsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        PortsMenu.IsOpen = true;
    }

    /// <summary>
    /// The logging rate, offered alongside the ports rather than buried in a
    /// settings dialog — it is a decision about the session you are about to
    /// start, and this is where you start one.
    /// </summary>
    private MenuItem RateMenu()
    {
        var menu = new MenuItem { Header = $"Logging rate: {_vm.LiveRate:N0} Hz" };

        foreach (double rate in MainViewModel.LiveRates)
        {
            var item = new MenuItem
            {
                Header = rate == SettingsStore.DefaultLiveRate ? $"{rate:N0} Hz  (default)" : $"{rate:N0} Hz",
                IsCheckable = true,
                IsChecked = rate == _vm.LiveRate,
                StaysOpenOnClick = false,
            };

            item.Click += (_, _) => _vm.LiveRate = rate;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "25 Hz is past what a wideband can resolve; raise it only for transients",
            IsEnabled = false,
        });

        return menu;
    }

    private void StartLive(string port, bool quiet = false)
    {
        try
        {
            _vm.Connect(port);
        }
        // Broad on the scripted path on purpose: this runs during startup, and
        // anything escaping here takes down the app behind a crash dialog with
        // no window and no explanation.
        catch (Exception ex) when (quiet || ex is LogFormatException or IOException
                                      or UnauthorizedAccessException or EcuProtocolException)
        {
            if (quiet) App.Report($"Could not connect on {port}: {ex}");
            else
                MessageBox.Show(this,
                    $"Could not connect on {port}.\n\n{ex.Message}",
                    "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }

        ConnectButton.Content = "Disconnect";
        _follow = true;

        _liveTimer = new DispatcherTimer { Interval = LiveRefresh };
        _liveTimer.Tick += OnLiveTick;
        _liveTimer.Start();
    }

    private void StopLive()
    {
        _liveTimer?.Stop();
        _liveTimer = null;

        _vm.Disconnect();
        ConnectButton.Content = "Connect ▾";
    }

    private void OnLiveTick(object? sender, EventArgs e)
    {
        if (!_vm.RefreshLive()) return;

        if (!_vm.IsLive) { StopLive(); return; }

        if (_vm.Document is { } document) Plot.ExtendDocument(document, _vm.Channels, _follow);

        // The heat table is far more work than the plot, so it is rebuilt at a
        // fraction of the rate — and only when it is the thing being looked at.
        if (_vm.ShowHistogram && ++_histogramTick % 5 == 0) RebuildHistogram();
    }

    private int _histogramTick;

    // ----- export -----------------------------------------------------------

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu }) return;

        menu.PlacementTarget = (Button)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Workspace.Ensure(_vm.Workspace.Root);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Could not open the folder.\n\n{ex.Message}",
                "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnChangeDataFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where should recordings and exports go?",
            InitialDirectory = Workspace.Ensure(_vm.Workspace.Root),
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _vm.SetDataFolder(dialog.FolderName);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "OpenLogViewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExportPlottedCsvClick(object sender, RoutedEventArgs e) => ExportCsv(plottedOnly: true);

    private void OnExportAllCsvClick(object sender, RoutedEventArgs e) => ExportCsv(plottedOnly: false);

    private void ExportCsv(bool plottedOnly)
    {
        string? path = AskWhereToSave(
            _vm.SuggestExportName(plottedOnly ? "plotted" : "channels", ".csv"),
            "CSV file|*.csv|All files|*.*");

        if (path is not null) Export(() => _vm.ExportLogCsv(path, plottedOnly));
    }

    private void OnExportTableCsvClick(object sender, RoutedEventArgs e) => ExportTableCsv(counts: false);

    private void OnExportCountsCsvClick(object sender, RoutedEventArgs e) => ExportTableCsv(counts: true);

    private void ExportTableCsv(bool counts)
    {
        string? path = AskWhereToSave(
            _vm.SuggestExportName(counts ? "counts" : "table", ".csv"),
            "CSV file|*.csv|All files|*.*");

        if (path is not null) Export(() => _vm.ExportTableCsv(path, counts));
    }

    private void OnExportPlotPngClick(object sender, RoutedEventArgs e) => ExportPng(Plot, "plot");

    private void OnExportTablePngClick(object sender, RoutedEventArgs e) => ExportPng(Histogram, "table");

    private void ExportPng(FrameworkElement view, string suffix)
    {
        string? path = AskWhereToSave(
            _vm.SuggestExportName(suffix, ".png"), "PNG image|*.png|All files|*.*");

        if (path is null) return;

        Export(() =>
        {
            // Both views draw their own ground, but only within their bounds; the
            // backdrop covers the rounding at the edges.
            ImageExport.Save(view, path, Backdrop());
            _vm.ReportSaved(path, suffix);
        });
    }

    /// <summary>
    /// Writes every export the current mode offers into a folder, skipping the
    /// dialogs. Drives the same code the menu does, so a scripted run and a
    /// clicked one cannot drift apart.
    /// </summary>
    public void ExportAll(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);

        string In(string suffix, string extension) =>
            Path.Combine(folder, _vm.SuggestExportName(suffix, extension));

        if (_vm.ShowHistogram)
        {
            _vm.ExportTableCsv(In("table", ".csv"), counts: false);
            _vm.ExportTableCsv(In("counts", ".csv"), counts: true);
            ImageExport.Save(Histogram, In("table", ".png"), Backdrop());
            return;
        }

        _vm.ExportLogCsv(In("plotted", ".csv"), plottedOnly: true);
        _vm.ExportLogCsv(In("channels", ".csv"), plottedOnly: false);
        ImageExport.Save(Plot, In("plot", ".png"), Backdrop());
    }

    private static Brush Backdrop() => new SolidColorBrush(ThemeManager.Current.Background);

    private string? AskWhereToSave(string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,

            // The workspace, not the folder the log came from. Exports belong
            // somewhere the user can find again, rather than scattered across
            // wherever each log happened to live.
            InitialDirectory = Workspace.Ensure(_vm.Workspace.Exports),
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// A failed save is reported where the user is looking rather than only in
    /// the status strip: a read-only folder or a file open in a spreadsheet are
    /// both ordinary, and silence would look like success.
    /// </summary>
    private void Export(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or InvalidOperationException)
        {
            MessageBox.Show(this,
                $"Could not save the file.\n\n{ex.Message}",
                "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void LoadFile(string path)
    {
        try
        {
            _vm.Load(path);
            Plot.SetDocument(_vm.Document, _vm.Channels);
        }
        catch (Exception ex) when (ex is LogFormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"Could not open {Path.GetFileName(path)}.\n\n{ex.Message}",
                "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Switches to the binned table view, optionally selecting one of the
    /// breakpoint sources by position (0 is "from the log data").
    /// </summary>
    public void ShowHistogram(
        int axisSource = 0, bool colourByCount = false, bool countStatistic = false, string? compareTo = null)
    {
        _vm.ShowHistogram = true;

        if (axisSource > 0 && axisSource < _vm.AxisSources.Count)
            _vm.AxisSource = _vm.AxisSources[axisSource];

        if (compareTo is { Length: > 0 })
        {
            CompareOption? match = _vm.CompareOptions.FirstOrDefault(
                o => o.Channel?.Name.Equals(compareTo, StringComparison.OrdinalIgnoreCase) == true);
            if (match is not null) _vm.ZCompare = match;
        }

        if (colourByCount) _vm.ColorByCount = true;
        if (countStatistic) _vm.StatCount = true;
    }

    /// <summary>Turns on VE Calibration, for a scripted run.</summary>
    public void EnableVeAnalyze(bool showSuggested)
    {
        _vm.VeAnalyze = true;
        _vm.VeShowSuggested = showSuggested;
    }

    /// <summary>
    /// Picks the fuel table read off the ECU as the breakpoint source.
    ///
    /// Separate from <see cref="ShowHistogram"/> because the axis sources do not
    /// exist until a session has produced its first samples, which is long after
    /// the command line has been dealt with.
    /// </summary>
    public bool UseEcuVeTable()
    {
        AxisSourceOption? source = _vm.AxisSources.FirstOrDefault(
            s => s.Label.Contains("VE Table", StringComparison.OrdinalIgnoreCase)
                 && s.Label.Contains("from the ECU", StringComparison.OrdinalIgnoreCase));

        if (source is null) return false;

        _vm.AxisSource = source;
        return true;
    }

    /// <summary>
    /// Positions the cursor from fractions of the plot area. Used to capture a
    /// deterministic screenshot with the hover readout showing.
    /// </summary>
    public void PreviewPointer(double fractionX, double fractionY) =>
        Plot.MoveCursorTo(new Point(Plot.ActualWidth * fractionX, Plot.ActualHeight * fractionY));

    /// <summary>Marks a span of the log, in seconds.</summary>
    public void SelectRange(double from, double to) => Plot.SelectRange(from, to);

    /// <summary>Traces a table cell back to the log.</summary>
    public void ActivateCell(int column, int row)
    {
        RebuildHistogram();
        OnHistogramCellActivated((column, row));
    }

    /// <summary>Gives each plotted channel its own strip.</summary>
    public void SetStackedLanes(bool stacked) => _vm.StackedLanes = stacked;

    private void Open()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open datalog",
            Filter = LogReaderFactory.OpenFileFilter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    /// <summary>
    /// Rebuilds the table. The sample window comes from the plot's current zoom
    /// when the user has asked to restrict it, so a table can be built from one
    /// pull rather than the whole log.
    /// </summary>
    private void RebuildHistogram()
    {
        if (_vm.Document is not { SampleCount: > 0 } doc)
        {
            Histogram.SetTable(null, _vm.ColorByCount);
            return;
        }

        int first = 0, last = doc.SampleCount - 1;
        if (_vm.HistogramZoomOnly && Plot.ViewEnd > Plot.ViewStart)
        {
            first = doc.IndexAtTime(Plot.ViewStart);
            last = doc.IndexAtTime(Plot.ViewEnd);
        }

        _vm.RebuildHistogram(first, last);
        Histogram.SetTable(_vm.Table, _vm.ColorByCount);
    }

    /// <summary>
    /// Traces a table cell back to the log. A cell is nearly always visited many
    /// times, so the longest visit is selected and the rest are marked — showing
    /// the span from first to last sample would cover most of the recording.
    /// </summary>
    private void OnHistogramCellActivated((int Column, int Row) cell)
    {
        if (_vm.Table is not { } table || _vm.Document is not { } doc) return;

        IReadOnlyList<(int First, int Last)> visits = table.VisitsTo(cell.Column, cell.Row);
        if (visits.Count == 0) return;

        (int First, int Last) longest = table.LongestVisitTo(cell.Column, cell.Row)!.Value;

        Histogram.SetSelectedCell(cell);
        _vm.ShowHistogram = false;

        Plot.SetOccurrences([.. visits.Select(v => (doc.Time.At(v.First), doc.Time.At(v.Last)))]);

        // Frame the visit with a little room either side, then mark it.
        double from = doc.Time.At(longest.First);
        double to = doc.Time.At(longest.Last);
        double margin = Math.Max((to - from) * 2, 1.0);

        Plot.ZoomTo(from - margin, to + margin);
        Plot.SelectRange(from, to);

        _vm.DescribeCellTrace(table, cell, visits, longest);
    }

    private void OnPlotInvalidated()
    {
        Plot.SetStacked(_vm.StackedLanes);

        // Channel visibility changed but the document did not, so keep the
        // current zoom window and just redraw.
        if (Plot.ViewEnd > Plot.ViewStart) Plot.Refresh();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => Open();

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        // On a live session this is also how you get back to watching, after
        // having zoomed in to read something that went past.
        if (_vm.IsLive) _follow = true;
        else Plot.ResetView();
    }

    private void OnPlotCommonClick(object sender, RoutedEventArgs e) => _vm.PlotCommon();

    // ----- data filters -----------------------------------------------------

    private void OnAddFilterClick(object sender, RoutedEventArgs e) =>
        FilterEditor.Visibility = Visibility.Visible;

    private void OnConfirmFilterClick(object sender, RoutedEventArgs e)
    {
        if (_vm.AddFilter()) FilterEditor.Visibility = Visibility.Collapsed;
    }

    private void OnCancelFilterClick(object sender, RoutedEventArgs e) =>
        FilterEditor.Visibility = Visibility.Collapsed;

    private void OnFiltersNoneClick(object sender, RoutedEventArgs e) => _vm.SetAllFilters(false);

    private void OnDeleteFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        FilterItem? item = element.DataContext as FilterItem
            ?? ((element.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as FilterItem;

        if (item is not null) _vm.DeleteFilter(item);
    }

    // ----- calculated channels ----------------------------------------------

    private void OnAddMathChannelClick(object sender, RoutedEventArgs e) =>
        MathEditor.Visibility = Visibility.Visible;

    private void OnCancelMathChannelClick(object sender, RoutedEventArgs e)
    {
        _vm.CancelMathEdit();
        MathEditor.Visibility = Visibility.Collapsed;
    }

    private void OnSaveMathChannelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.AddMathChannel();
            MathEditor.Visibility = Visibility.Collapsed;
        }
        catch (InvalidOperationException ex)
        {
            // A name already taken, or the limit reached. The editor stays open
            // with what was typed still in it.
            MessageBox.Show(this, ex.Message, "OpenLogViewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Reopens a definition in the editor, which removes it until saved again.</summary>
    private void OnEditMathChannelClick(object sender, RoutedEventArgs e)
    {
        if (Definition(sender) is not { } channel) return;

        _vm.EditMathChannel(channel);
        MathEditor.Visibility = Visibility.Visible;
    }

    private void OnDeleteMathChannelClick(object sender, RoutedEventArgs e)
    {
        if (Definition(sender) is { } channel) _vm.RemoveMathChannel(channel);
    }

    /// <summary>
    /// The definition behind a chip or its context menu. A menu item's data
    /// context is the menu, not the button it was opened from.
    /// </summary>
    private static MathChannel? Definition(object sender)
    {
        if (sender is not FrameworkElement element) return null;

        return element.DataContext as MathChannel
            ?? ((element.Parent as ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as MathChannel;
    }

    // ----- presets ----------------------------------------------------------

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        PresetNameRow.Visibility = Visibility.Visible;
        PresetNameBox.Text = "";
        PresetNameBox.Focus();
    }

    private void OnConfirmPresetClick(object sender, RoutedEventArgs e)
    {
        if (_vm.SavePreset(PresetNameBox.Text))
            PresetNameRow.Visibility = Visibility.Collapsed;
    }

    private void OnCancelPresetClick(object sender, RoutedEventArgs e)
    {
        PresetNameRow.Visibility = Visibility.Collapsed;
        _vm.ResetHint();
    }

    private void OnPresetNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnConfirmPresetClick(sender, e);
        else if (e.Key == Key.Escape) OnCancelPresetClick(sender, e);
        else return;

        e.Handled = true;
    }

    private void OnApplyPresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is { } preset) _vm.ApplyPreset(preset);
    }

    private void OnOverwritePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is { } preset) _vm.SavePreset(preset.Name);
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetFrom(sender) is { } preset) _vm.DeletePreset(preset);
    }

    private static ChannelPreset? PresetFrom(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        if (element.DataContext is ChannelPreset preset) return preset;

        return (element.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext as ChannelPreset
            : null;
    }

    private void OnJumpToMaxClick(object sender, RoutedEventArgs e) =>
        JumpTo(ChannelFrom(sender), max: true);

    private void OnJumpToMinClick(object sender, RoutedEventArgs e) =>
        JumpTo(ChannelFrom(sender), max: false);

    private void OnPlotOnlyClick(object sender, RoutedEventArgs e)
    {
        if (ChannelFrom(sender) is not { } item) return;

        _vm.SetAllVisible(false);
        item.IsVisible = true;
    }

    /// <summary>
    /// Resolves the channel a click belongs to. A menu item inside a context menu
    /// is outside the visual tree, so its data context is taken from the row the
    /// menu was opened on.
    /// </summary>
    private static ChannelItem? ChannelFrom(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        if (element.DataContext is ChannelItem item) return item;

        return (element.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext as ChannelItem
            : null;
    }

    /// <summary>
    /// Moves the cursor to where a channel peaks or bottoms out. The channel is
    /// plotted first if it was not already, so the jump has something to land on.
    /// </summary>
    private void JumpTo(ChannelItem? item, bool max)
    {
        if (item is null || _vm.Document is not { } doc) return;

        int index = max ? item.Channel.MaxIndex : item.Channel.MinIndex;
        if (index < 0) return;

        item.IsVisible = true;
        Plot.FocusTime(doc.Time.At(index));
    }

    private void OnShowAllClick(object sender, RoutedEventArgs e) => _vm.SetAllVisible(true);

    private void OnShowNoneClick(object sender, RoutedEventArgs e) => _vm.SetAllVisible(false);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;

        // A tune and a log are both things you drag onto a log viewer, and the
        // extension says which was meant.
        if (Path.GetExtension(files[0]).Equals(".msq", StringComparison.OrdinalIgnoreCase))
            LoadTuneFile(files[0]);
        else
            LoadFile(files[0]);
    }

    private void OnOpenTuneClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open tune",
            Filter = "TunerStudio tune|*.msq|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true) LoadTuneFile(dialog.FileName);
    }

    private void OnClearTuneClick(object sender, RoutedEventArgs e)
    {
        _vm.ClearTune();
        RebuildHistogram();
    }

    private void LoadTuneFile(string path)
    {
        try
        {
            _vm.LoadTune(path);
            RebuildHistogram();
        }
        catch (Exception ex) when (ex is LogFormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"Could not read {Path.GetFileName(path)}.\n\n{ex.Message}",
                "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}



