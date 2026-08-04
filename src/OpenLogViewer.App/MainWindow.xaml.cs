using System.Diagnostics;
using System.Globalization;
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

        // The table view knows which keys were pressed and nothing else; what a
        // table may become is the view model's business.
        TuneTable.SelectionChanged += cells => _vm.SelectedCells = cells;
        TuneTable.EditRequested += _vm.EditTable;
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

        // The calibration tab's fault panel. Pointed at the view model now and
        // told to ask the car the first time somebody actually switches to it —
        // scanning at startup would take the adapter for a view nobody has looked
        // at, and on this link that is the gauges stopping for no reason.
        CalibrationFaults.Attach(_vm);
        CalibrationFaults.ScanOnFirstSight();

        InputBindings.Add(new KeyBinding(new RelayCommand(Open), Key.O, ModifierKeys.Control));

        // Ctrl+K repeats the last connection. Does nothing when there is nothing
        // to repeat, rather than opening a menu somebody did not ask for.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => OnReconnectClick(this, new RoutedEventArgs())),
            Key.K, ModifierKeys.Control));
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

    /// <summary>
    /// Opens the calculators on a named tab and draws them, for a scripted run.
    ///
    /// A calculator nobody has looked at is how one ships showing "—" in every
    /// answer, or with the arithmetic right and the layout unreadable.
    /// </summary>
    public void CaptureCalculators(string tab, string path)
    {
        OnCalculatorsClick(this, new RoutedEventArgs());

        if (_calculators is null) return;

        // Reported rather than ignored: a misspelt name used to draw whichever
        // calculator happened to be open and look like a successful run.
        if (!_calculators.Show(tab))
            App.Report($"no calculator called '{tab}'");

        _calculators.UpdateLayout();
        ImageExport.Save(_calculators.Content as FrameworkElement ?? _calculators, path);

        _calculators.Close();
    }

    private void OnResetGaugePeaksClick(object sender, RoutedEventArgs e) => _vm.ResetGaugePeaks();

    // ----- the menu -----------------------------------------------------------

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnDisconnectClick(object sender, RoutedEventArgs e) => StopLive();

    private void OnDefinitionsClick(object sender, RoutedEventArgs e) =>
        OpenFolder(_vm.Workspace.EnsureDefinitions());

    private CalculatorsWindow? _calculators;

    /// <summary>
    /// The tuning calculators, in a window of their own.
    ///
    /// Kept alive rather than made fresh each time, so the figures typed in are
    /// still there when it is reopened — sizing an injector and then checking
    /// the pump for the same engine should not mean typing the power twice.
    /// </summary>
    private void OnCalculatorsClick(object sender, RoutedEventArgs e)
    {
        if (_calculators is null)
        {
            _calculators = new CalculatorsWindow { Owner = this };
            _calculators.Closed += (_, _) => _calculators = null;
            _calculators.Show();

            return;
        }

        if (_calculators.WindowState == WindowState.Minimized)
            _calculators.WindowState = WindowState.Normal;

        _calculators.Activate();
    }

    /// <summary>
    /// Starts or stops writing the live session to a file.
    ///
    /// One handler for both, because they are one control. Starting asks where
    /// to put it — the whole point of the feature is that the name and the place
    /// are chosen rather than assigned — and stopping just stops, since a
    /// question at that moment would be a question about something already
    /// decided.
    /// </summary>
    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanRecord) return;

        if (_vm.IsRecording)
        {
            Report(_vm.StopRecording());
            return;
        }

        string suggested = _vm.SuggestedRecordingPath();

        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(suggested),
            Filter = "CSV log|*.csv|All files|*.*",
            AddExtension = true,
            DefaultExt = ".csv",
            OverwritePrompt = true,
            Title = "Record the live session to",

            // Where the last one went, which is almost always where this one
            // should go. Falls back to the workspace on the first recording.
            InitialDirectory = Path.GetDirectoryName(suggested) ?? "",
        };

        if (dialog.ShowDialog(this) != true) return;

        Report(_vm.StartRecording(dialog.FileName));
    }

    private void OnOpenRecordingsClick(object sender, RoutedEventArgs e) =>
        OpenFolder(Workspace.Ensure(_vm.Workspace.Logs));

    private FaultsWindow? _faults;

    /// <summary>
    /// The vehicle's fault codes.
    ///
    /// Not kept alive between openings, unlike the calculators and the power
    /// estimate. Those hold figures somebody typed and would be tedious to retype;
    /// this holds what the car said a moment ago, and a stale list of faults is
    /// the one thing a diagnostic window must never show. It scans as it opens.
    /// </summary>
    private void OnFaultsClick(object sender, RoutedEventArgs e)
    {
        if (_faults is not null)
        {
            if (_faults.WindowState == WindowState.Minimized)
                _faults.WindowState = WindowState.Normal;

            _faults.Activate();
            _faults.Scan();

            return;
        }

        _faults = new FaultsWindow(_vm) { Owner = this };
        _faults.Closed += (_, _) => _faults = null;
    }

    /// <summary>Opens the fault list and draws it, for a scripted run.</summary>
    public void CaptureFaults(string path)
    {
        OnFaultsClick(this, new RoutedEventArgs());

        if (_faults is null) return;

        _faults.UpdateLayout();
        ImageExport.Save(_faults.Content as FrameworkElement ?? _faults, path);

        _faults.Close();
    }

    private PowerWindow? _power;

    /// <summary>
    /// The power estimate, kept alive the same way the calculators are.
    ///
    /// The whole use of it is to try a figure, look at the plot and try another,
    /// so a window that forgot the engine every time it was closed would make
    /// that a retyping exercise.
    /// </summary>
    private void OnEstimatePowerClick(object sender, RoutedEventArgs e)
    {
        if (_power is null)
        {
            _power = new PowerWindow(_vm) { Owner = this };
            _power.Closed += (_, _) => _power = null;

            return;
        }

        if (_power.WindowState == WindowState.Minimized) _power.WindowState = WindowState.Normal;

        _power.Activate();
    }

    /// <summary>Opens the power estimate and draws it, for a scripted run.</summary>
    public void CapturePower(string path)
    {
        OnEstimatePowerClick(this, new RoutedEventArgs());

        if (_power is null) return;

        _power.UpdateLayout();
        ImageExport.Save(_power.Content as FrameworkElement ?? _power, path);

        _power.Close();
    }

    /// <summary>
    /// Filled when opened rather than declared, because both of these are lists
    /// the view model owns and either can change while the window is up.
    /// </summary>
    private void OnRateMenuOpened(object sender, RoutedEventArgs e)
    {
        RateMenuItem.Items.Clear();
        foreach (object item in Rates()) RateMenuItem.Items.Add(item);
    }

    private void OnUnitsMenuOpened(object sender, RoutedEventArgs e)
    {
        UnitsMenuItem.Items.Clear();
        foreach (object item in Units()) UnitsMenuItem.Items.Add(item);
    }

    private void OnThemeMenuOpened(object sender, RoutedEventArgs e)
    {
        ThemeMenuItem.Items.Clear();

        foreach (Theme theme in _vm.Themes)
        {
            var item = new MenuItem
            {
                Header = theme.Name,
                IsCheckable = true,
                IsChecked = theme.Id == _vm.SelectedTheme?.Id,
                StaysOpenOnClick = false,
            };

            Theme chosen = theme;
            item.Click += (_, _) => _vm.SelectedTheme = chosen;

            ThemeMenuItem.Items.Add(item);
        }
    }

    private void OnDocumentationClick(object sender, RoutedEventArgs e) =>
        Launch("https://github.com/TokenGoblin/OpenLogViewer#readme");

    /// <summary>
    /// What this is and what it was built against.
    ///
    /// The hardware list is the useful part: this reads several controllers that
    /// describe themselves differently, and which ones have actually been run
    /// against a real engine is the thing someone deciding whether to trust it
    /// wants to know.
    /// </summary>
    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        MessageBox.Show(
            this,
            $"OpenLogViewer {version}\n\n"
            + "Datalog viewer and live tuning for engine ECUs.\n\n"
            + "Reads MegaSquirt and TunerStudio logs, MaxxECU logs, and delimited text.\n"
            + "Connects live to MegaSquirt, MicroSquirt, rusEFI, Speeduino, MaxxECU,\n"
            + "and any OBD2 vehicle through an ELM327 adapter.\n\n"
            + "No network code: nothing here is ever sent anywhere.\n\n"
            + "https://github.com/TokenGoblin/OpenLogViewer",
            "About OpenLogViewer",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>Opens a link or a folder with whatever Windows uses for it.</summary>
    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            App.Report($"Could not open {target}: {e.Message}");
        }
    }

    // ----- editing a table ----------------------------------------------------

    /// <summary>
    /// Sends the changed cells, having said plainly what that means.
    ///
    /// Asked for confirmation because this is the one action here that reaches
    /// out and changes a running engine, and because the number of cells is the
    /// thing worth checking before it does — a table scaled by five per cent
    /// when one cell was meant is 256 changes, and it looks identical to one
    /// change until it is counted.
    /// </summary>
    private void OnWriteTableClick(object sender, RoutedEventArgs e)
    {
        if (_vm.TableEdit is not { } edit) return;

        int cells = edit.ChangedCount;

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Send {cells} changed cell{(cells == 1 ? "" : "s")} of {edit.Name} to the ECU?\n\n"
            + "This takes effect immediately on a running engine.\n\n"
            + "It is not permanent: the ECU forgets it at the next power cycle unless you burn it.",
            "OpenLogViewer",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        Report(_vm.WriteTableToEcu());
    }

    /// <summary>
    /// Burns the page. Confirmed separately and more firmly than a write,
    /// because a write is undone by turning the key off and this is not.
    /// </summary>
    private void OnBurnTableClick(object sender, RoutedEventArgs e)
    {
        if (_vm.TableEdit is not { } edit) return;

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Burn the page holding {edit.Name} to the ECU's flash?\n\n"
            + "This is permanent. A power cycle will not undo it.\n\n"
            + "Burn with the engine stopped: the ECU pauses while it writes flash.",
            "OpenLogViewer",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        Report(_vm.BurnTableToEcu());
    }

    private void OnRevertTableClick(object sender, RoutedEventArgs e) => _vm.RevertTable();

    // ----- the table tools ------------------------------------------------------
    //
    // Each of these is the same request the keyboard already raises, so the two
    // ways in cannot drift apart: the buttons exist because the keys are not
    // discoverable, not because they do anything different.

    private void OnNudgeUpClick(object sender, RoutedEventArgs e) =>
        _vm.EditTable(TuneTableEdit.Add(_vm.TableNudge));

    private void OnNudgeDownClick(object sender, RoutedEventArgs e) =>
        _vm.EditTable(TuneTableEdit.Add(-_vm.TableNudge));

    private void OnScaleUpClick(object sender, RoutedEventArgs e) =>
        _vm.EditTable(TuneTableEdit.Scale(_vm.TableScaleStep));

    private void OnScaleDownClick(object sender, RoutedEventArgs e) =>
        _vm.EditTable(TuneTableEdit.Scale(-_vm.TableScaleStep));

    private void OnInterpolateClick(object sender, RoutedEventArgs e) =>
        _vm.EditTable(TuneTableEdit.Interpolate());

    private void OnRevertCellsClick(object sender, RoutedEventArgs e) =>
        _vm.EditTable(TuneTableEdit.RevertSelection());

    private void OnSetValueClick(object sender, RoutedEventArgs e) => ApplySetValue();

    /// <summary>
    /// Selects a numeric field's contents on focus, so typing replaces the value.
    ///
    /// The same treatment the calculators get, for the same reason: these hold a
    /// working number rather than a placeholder, and typing into one used to
    /// append to it.
    /// </summary>
    private void OnFieldFocused(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    /// <summary>Focuses a field on the first click instead of placing a caret in it.</summary>
    private void OnFieldClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || box.IsKeyboardFocusWithin) return;

        box.Focus();
        e.Handled = true;
    }

    private void OnSetValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        ApplySetValue();
        e.Handled = true;
    }

    /// <summary>
    /// Sets the selected cells to a typed value.
    ///
    /// Nothing typed and nothing that parses are both left alone rather than
    /// treated as zero — setting a fuel table's cells to nothing because a box
    /// was empty is not a mistake worth making on a running engine.
    /// </summary>
    private void ApplySetValue()
    {
        if (!double.TryParse(
                SetValue.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
        {
            _vm.SetHint("Type a number to set the selected cells to.");
            return;
        }

        _vm.EditTable(TuneTableEdit.Set(value));
    }

    private void Report(string outcome)
    {
        _vm.SetHint(outcome);
        App.Report(outcome);
    }

    /// <summary>Switches to calibration, optionally on a named table, for a scripted run.</summary>
    public void ShowCalibration(string? table)
    {
        _vm.Mode = WorkspaceMode.Calibration;

        if (table is null) return;

        // Through the search box rather than straight at the view model, so a
        // scripted run exercises the same path a person's typing does — setting
        // the property directly would pass whether or not the box is bound to it.
        TableSearch.Text = table;
        TableSearch.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

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
    /// <summary>
    /// Connects the way the menu does, for a scripted run.
    ///
    /// Separate from <see cref="ConnectTo"/> because the menu takes a different
    /// route — it probes the port off the interface thread first — and checking
    /// the other route proves nothing about this one.
    /// </summary>
    public async Task ConnectViaMenu(string port)
    {
        SerialPortInfo described = SerialPortNames.Describe()
            .FirstOrDefault(p => p.PortName.Equals(port, StringComparison.OrdinalIgnoreCase))
            ?? new SerialPortInfo(port, "", false);

        App.Report($"connecting via the menu on {port}…");
        await StartLiveFromMenu(described);
        App.Report(_vm.IsLive ? $"live: {_vm.Status}" : $"not live: {_vm.Hint}");
    }

    public void ConnectTo(string port, bool asObd2 = false)
    {
        App.Report($"connecting on {port}…");

        _forceObd2 = asObd2;

        try
        {
            StartLive(port, quiet: true);
        }
        finally
        {
            _forceObd2 = false;
        }

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
    /// <summary>
    /// Scans for what is actually switched on, then draws the menu, for a
    /// scripted run. The scan is the part worth seeing the result of, and it
    /// cannot be reached without a hand on the mouse.
    /// </summary>
    public async Task CaptureScannedMenu(string path)
    {
        IReadOnlyList<SerialPortInfo> ports = SerialPortNames.Describe();

        await ScanPaired([.. ports.Where(p => p.IsBluetooth)], BleDevices.Obd2Adapters());

        CaptureConnectMenu(path);
    }

    /// <summary>
    /// Opens one of the menu bar's menus and draws it, for a scripted run.
    ///
    /// A menu's list lives in a popup, which is its own top-level window and so
    /// appears in no render of this one. Drawing that popup's own content is the
    /// only way to see what a drop-down looks like without a person at the
    /// keyboard — and a drop-down nobody has looked at is exactly how a menu
    /// ships with its arrows and ticks in the wrong places.
    /// </summary>
    public void CaptureMenu(string header, string path)
    {
        MenuItem? top = MainMenu.Items.OfType<MenuItem>().FirstOrDefault(
            m => (m.Header as string)?.Replace("_", "", StringComparison.Ordinal)
                     .Equals(header, StringComparison.OrdinalIgnoreCase) == true);

        if (top is null)
        {
            App.Report($"no menu called \"{header}\"");
            return;
        }

        top.IsSubmenuOpen = true;
        UpdateLayout();

        // The popup's child, rather than the popup: a popup has no size of its
        // own, so rendering it produces an empty image.
        if (top.Template.FindName("PART_Popup", top) is System.Windows.Controls.Primitives.Popup
            { Child: FrameworkElement child })
        {
            child.UpdateLayout();
            ImageExport.Save(child, path);
        }

        top.IsSubmenuOpen = false;
        Mouse.Capture(null);
    }

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

    /// <summary>
    /// Most recently used first, then everything else.
    ///
    /// Sorted rather than merely grouped, because "known" is not one bucket on a
    /// machine that has seen three ECUs — the one wanted now is almost always the
    /// one wanted last time.
    /// </summary>
    private static long Rank(SerialPortInfo port) =>
        port.LastUsed is { } when ? -when.ToUnixTimeSeconds() : long.MaxValue;

    /// <summary>
    /// Reconnects to the ECU used last, which is what the shortcut is for.
    ///
    /// Goes through the same path as picking it from the menu rather than a
    /// quicker one of its own: an OBD2 dongle needs a different conversation from
    /// a MegaSquirt, and a second route to connecting would be a second place for
    /// that decision to be got wrong.
    /// </summary>
    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_vm.LastConnected is not { } port) return;

        ConnectTo(port.PortName, port.IsObd2);
    }

    private void OnForgetEcusClick(object sender, RoutedEventArgs e) =>
        Report(_vm.ForgetKnownEcus());

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsLive) { StopLive(); return; }

        PopulateConnectMenu(PortsMenu.Items, includeSettings: true);

        PortsMenu.PlacementTarget = ConnectButton;
        PortsMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        PortsMenu.IsOpen = true;
    }

    /// <summary>Fills the Tools menu's Connect submenu from the same code as the toolbar's.</summary>
    private void OnConnectMenuOpened(object sender, RoutedEventArgs e) =>
        PopulateConnectMenu(ConnectMenuItem.Items, includeSettings: false);

    /// <summary>
    /// Lists what can be connected to.
    ///
    /// Written once and used from two places — the toolbar button and the Tools
    /// menu — because two lists of ports would be two lists to keep right, and
    /// the one that got forgotten would be the one somebody used.
    ///
    /// <paramref name="includeSettings"/> carries the session settings along for
    /// the toolbar, where they are the decisions you make on the way to starting
    /// a session. The Tools menu has them in its own right and does not want
    /// them twice.
    /// </summary>
    private void PopulateConnectMenu(ItemCollection items, bool includeSettings)
    {
        items.Clear();

        void Add(object item) => items.Add(item);

        IReadOnlyList<SerialPortInfo> ports = SerialPortNames.Describe();
        IReadOnlyList<BleDevice> adapters = BleDevices.Obd2Adapters();

        // Cabled ports first, and they mean something: a USB adapter appears in
        // this list when it is plugged in and vanishes when it is not, so its
        // presence is already the answer to "is it there".
        SerialPortInfo[] wired = [.. ports.Where(p => !p.IsBluetooth).OrderBy(Rank)];

        // Anything that has answered before goes above anything that has not,
        // and among those the one used most recently goes first. The heading
        // only appears when both kinds are present — on a machine with one
        // adapter it would be a label over a list of one.
        bool split = wired.Any(p => p.IsKnown) && wired.Any(p => !p.IsKnown);
        bool headed = false;

        if (split && wired.Length > 0)
        {
            Add(new MenuItem { Header = "Used before", IsEnabled = false });
            headed = true;
        }

        foreach (SerialPortInfo port in wired)
        {
            if (headed && !port.IsKnown)
            {
                Add(new MenuItem { Header = "Other ports", IsEnabled = false });
                headed = false;
            }

            Add(PortItem(port));
        }

        // Everything over a radio is a different matter. Pairing is a fact about
        // this computer rather than about the device, so a paired ECU is listed
        // whether or not it has power — which is how a menu comes to offer three
        // ECUs when none of them is switched on. Said plainly rather than left
        // to be discovered by picking one and waiting out a timeout.
        SerialPortInfo[] paired = [.. ports.Where(p => p.IsBluetooth)];

        if (paired.Length + adapters.Count > 0)
        {
            if (wired.Length > 0) Add(new Separator());

            Add(new MenuItem
            {
                Header = "Paired — listed whether or not switched on",
                IsEnabled = false,
            });

            foreach (SerialPortInfo port in paired) Add(PortItem(port));

            foreach (BleDevice adapter in adapters)
            {
                var item = new MenuItem
                {
                    Header = Decorate(adapter.Label, Seen(adapter.Name)),
                    ToolTip = "A Bluetooth LE OBD2 adapter. Reads any standard vehicle — "
                              + "no definition file needed.",
                };

                item.Click += (_, _) => StartLiveOverBle(adapter);
                Add(item);
            }

            var scan = new MenuItem
            {
                Header = "Check which of these are switched on",
                ToolTip = "Listens for the LE adapters and tries the others. Takes a few "
                          + "seconds, because a paired port that answers nothing has to be "
                          + "waited out before it can be called dead.",
            };

            scan.Click += async (_, _) => await ScanPaired(paired, adapters);
            Add(scan);
        }

        if (wired.Length + paired.Length + adapters.Count == 0)
            Add(new MenuItem { Header = "Nothing found", IsEnabled = false });

        if (ports.Count > 0)
        {
            Add(new Separator());
            Add(Obd2Menu(ports));
            Add(SsmMenu(ports));
        }

        if (!includeSettings) return;

        Add(new Separator());
        Add(RateMenu());
        Add(UnitsMenu());

        var whole = new MenuItem
        {
            Header = "Read the block in one request",
            IsCheckable = true,
            IsChecked = _vm.SingleRequestBlock,
            ToolTip = "Faster on a MegaSquirt, which serves its whole block despite declaring "
                      + "a smaller limit. A rusEFI asked for more than it declares stops "
                      + "responding until it is replugged.",
        };

        whole.Click += (_, _) => _vm.SingleRequestBlock = whole.IsChecked;
        Add(whole);

        var definitions = new MenuItem
        {
            Header = "ECU definition files…",
            ToolTip = "Where to put the .ini for an ECU this machine does not already know",
        };

        definitions.Click += (_, _) => OpenFolder(_vm.Workspace.EnsureDefinitions());
        Add(definitions);
    }

    /// <summary>
    /// The logging rate, offered alongside the ports rather than buried in a
    /// settings dialog — it is a decision about the session you are about to
    /// start, and this is where you start one.
    /// </summary>
    private MenuItem RateMenu()
    {
        var menu = new MenuItem { Header = $"Logging rate: {_vm.LiveRate:N0} Hz" };

        foreach (object item in Rates()) menu.Items.Add(item);

        return menu;
    }

    /// <summary>
    /// The choices themselves, so the same list serves the toolbar's connect
    /// menu and the Tools menu without either being a copy of the other.
    /// </summary>
    private IEnumerable<object> Rates()
    {
        foreach (double rate in MainViewModel.LiveRates)
        {
            var item = new MenuItem
            {
                Header = rate == SettingsStore.DefaultLiveRate ? $"{rate:N0} Hz  (default)" : $"{rate:N0} Hz",
                IsCheckable = true,
                IsChecked = rate == _vm.LiveRate,
                StaysOpenOnClick = false,
            };

            double chosen = rate;
            item.Click += (_, _) => _vm.LiveRate = chosen;

            yield return item;
        }

        yield return new Separator();
        yield return new MenuItem
        {
            Header = "25 Hz is past what a wideband can resolve; raise it only for transients",
            IsEnabled = false,
        };
    }

    /// <summary>
    /// Which units readings are shown in.
    ///
    /// Display only, and said so here: the recording keeps whatever the ECU
    /// reported, so switching mid-session does not put two systems of units in
    /// one file.
    /// </summary>
    private MenuItem UnitsMenu()
    {
        var menu = new MenuItem { Header = $"Units: {_vm.UnitsLabel}" };

        foreach (object item in Units()) menu.Items.Add(item);

        return menu;
    }

    private IEnumerable<object> Units()
    {
        foreach (UnitSystem system in MainViewModel.UnitSystems)
        {
            var item = new MenuItem
            {
                Header = system switch
                {
                    UnitSystem.Metric => "Metric  (°C, km/h, kPa)",
                    UnitSystem.Imperial => "Imperial  (°F, mph, psi)",
                    _ => "As reported  (default)",
                },
                IsCheckable = true,
                IsChecked = system == _vm.Units,
                StaysOpenOnClick = false,
            };

            UnitSystem chosen = system;
            item.Click += (_, _) => _vm.Units = chosen;

            yield return item;
        }

        yield return new Separator();
        yield return new MenuItem
        {
            Header = "Display only — recordings keep the units the ECU reported",
            IsEnabled = false,
        };
    }

    /// <summary>
    /// Set while a connection is being made as an OBD2 adapter deliberately,
    /// rather than because the port's name gave it away.
    /// </summary>
    private bool _forceObd2;

    /// <summary>
    /// Connecting to a port as an OBD2 adapter whatever it is called.
    ///
    /// The common dongles are recognised by name and need nothing from here, but
    /// a great many are generic USB adapters that Windows describes as a
    /// "USB-SERIAL CH340" and nothing more. There is no way to tell one of those
    /// from a tuning cable without talking to it, and the two want opposite
    /// opening moves — so it is asked for rather than guessed.
    /// </summary>
    /// <summary>
    /// Connecting over Subaru's own protocol rather than the standard.
    ///
    /// Its own entry rather than something guessed at from the adapter's name,
    /// because SSM is a deliberate choice with a real cost: it reads what the ECU
    /// has learnt -- knock correction, the learnt timing, fuelling trims -- and
    /// none of that is in OBD2, but it manages about one round a second against
    /// OBD2's three. Nobody should land on it by accident.
    /// </summary>
    /// <summary>Opens a Subaru over SSM, for a scripted run.</summary>
    public void ConnectOverSsm(string port) => StartLiveOverSsm(port);

    private MenuItem SsmMenu(IReadOnlyList<SerialPortInfo> ports)
    {
        var menu = new MenuItem
        {
            Header = "Connect over SSM (Subaru)",
            ToolTip = "Subaru's own protocol. Reads knock correction, learnt timing and "
                      + "fuelling trims, which OBD2 does not carry at any speed — about one "
                      + "round a second. Addresses come from ssm-parameters.json in the "
                      + "definitions folder.",
        };

        foreach (SerialPortInfo port in ports)
        {
            var item = new MenuItem
            {
                Header = port.Label.Replace("_", "__", StringComparison.Ordinal),
            };

            item.Click += (_, _) => StartLiveOverSsm(port.PortName);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var edit = new MenuItem
        {
            Header = "Edit the parameter list…",
            ToolTip = "Which addresses are read, and what they mean",
        };

        edit.Click += (_, _) => OpenFolder(_vm.Workspace.EnsureDefinitions());
        menu.Items.Add(edit);

        return menu;
    }

    /// <summary>
    /// Starts an SSM session, reporting the ways it can refuse.
    ///
    /// It refuses more readily than an OBD2 connection does, and deliberately: the
    /// addresses are the user's own and every one of them may be wrong, so a
    /// session that started happily and showed a screen of dashes would be a worse
    /// outcome than one that will not start and says which address was refused.
    /// </summary>
    private void StartLiveOverSsm(string port)
    {
        try
        {
            _vm.ConnectSsm(port);
        }
        catch (Exception e) when (e is EcuProtocolException or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, e.Message, "OpenLogViewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private MenuItem Obd2Menu(IReadOnlyList<SerialPortInfo> ports)
    {
        var menu = new MenuItem
        {
            Header = "Connect as an OBD2 adapter",
            ToolTip = "For an ELM327 dongle whose name does not say what it is. "
                      + "Reads any standard OBD2 vehicle — no definition file needed.",
        };

        foreach (SerialPortInfo port in ports)
        {
            var item = new MenuItem
            {
                Header = port.Label.Replace("_", "__", StringComparison.Ordinal),
            };

            item.Click += async (_, _) =>
            {
                _forceObd2 = true;

                try
                {
                    await StartLiveFromMenu(port);
                }
                finally
                {
                    _forceObd2 = false;
                }
            };

            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>
    /// Connects from the menu, without freezing the window while it tries.
    ///
    /// Opening a Bluetooth port whose device is switched off blocks for the best
    /// part of twenty seconds — a paired port stays listed whether or not
    /// anything is on the other end, so this is the ordinary way to discover an
    /// ECU is off. Doing that on the interface thread greys the window out and
    /// Windows files it as a hang, which is indistinguishable from a crash to
    /// anyone watching.
    ///
    /// So the port is opened and closed on a background thread first, and the
    /// connection proper only starts once something is known to answer. The
    /// second open costs milliseconds on a link that is already up.
    /// </summary>
    private async Task StartLiveFromMenu(SerialPortInfo port)
    {
        ConnectButton.IsEnabled = false;
        _vm.SetHint($"Connecting to {port.Label}…");

        try
        {
            Exception? failure = await Task.Run(() => TryReach(port));

            if (failure is not null)
            {
                // Remembered so the menu can say so next time. Windows lists a
                // paired Bluetooth port whether or not anything is on the other
                // end — both a live ECU and a switched-off one report Status=OK
                // and Present=True — so having tried is the only knowledge there
                // is, and throwing it away means finding out again each time.
                Record(port.PortName, "no answer");

                MessageBox.Show(this,
                    $"Could not connect on {port.PortName}.\n\n{failure.Message}",
                    "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);

                _vm.SetHint($"{port.PortName} did not answer.");
                return;
            }

            Record(port.PortName, "answered");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }

        StartLive(port.PortName);
    }

    /// <summary>
    /// What was last learnt about each port or adapter, ready to show.
    ///
    /// Kept as the words rather than as a flag, because the three things worth
    /// saying are not two: a port can have answered, or have been asked and said
    /// nothing, or — for a radio device that was merely listened for — not have
    /// been heard, which is weaker than silence and must not be reported as it.
    ///
    /// Windows lists a paired device whether or not it has power, so without
    /// this the menu offers several ECUs with no hint that only one is on.
    /// </summary>
    private readonly Dictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One entry in the port list, labelled with whatever is known about it.</summary>
    private MenuItem PortItem(SerialPortInfo port)
    {
        var item = new MenuItem
        {
            Header = Decorate(port.Label, Seen(port.PortName)),
            ToolTip = port.IsBluetooth
                ? "A Bluetooth link. Slower to answer than a cable, so it is given "
                  + "longer to reply and more room to settle between attempts."
                : null,
        };

        item.Click += async (_, _) => await StartLiveFromMenu(port);

        return item;
    }

    /// <summary>
    /// What is known about whether something is there, or nothing when it has
    /// not been tried.
    /// </summary>
    private string Seen(string key) => _seen.GetValueOrDefault(key, "");

    /// <summary>
    /// A menu header. The underscore is doubled because a header reads a single
    /// one as an access key and swallows it, which turned a device advertising
    /// as MaxxECU_28xf7p into MaxxECU28xf7p.
    /// </summary>
    private static string Decorate(string label, string note) =>
        (note.Length > 0 ? $"{label} — {note}" : label).Replace("_", "__", StringComparison.Ordinal);

    /// <summary>
    /// Finds out which paired devices are actually switched on.
    ///
    /// Two different questions needing two different answers. A Bluetooth LE
    /// adapter announces itself several times a second, so listening for a
    /// couple of seconds settles it without connecting to anything. A
    /// serial-port-profile device announces nothing at all — the only way to
    /// know is to open the port and see, and a dead one has to be waited out.
    /// They are tried together so the wait is one wait rather than several.
    /// </summary>
    private async Task ScanPaired(
        IReadOnlyList<SerialPortInfo> paired, IReadOnlyList<BleDevice> adapters)
    {
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Scanning…";

        try
        {
            Task<IReadOnlySet<ulong>> advertising =
                BleDevices.AdvertisingAsync(TimeSpan.FromSeconds(3));

            Task<(string Port, bool Answered)>[] probes =
            [
                .. paired.Select(p => Task.Run(() => (p.PortName, TryReach(p) is null))),
            ];

            await Task.WhenAll([advertising, .. probes.Cast<Task>()]);

            // A serial port was actually opened, so silence here is a real
            // answer about a real attempt.
            foreach (Task<(string Port, bool Answered)> probe in probes)
            {
                (string port, bool answered) = probe.Result;

                Record(port, answered ? "answered" : "no answer");
            }

            // An LE adapter was only listened for, which is weaker and is worded
            // as such. Being heard proves it is on; not being heard does not
            // prove it is off, because a device already connected to something
            // — a phone in the same car — stops advertising while being alive.
            IReadOnlySet<ulong> heard = advertising.Result;

            foreach (BleDevice adapter in adapters)
                Record(adapter.Name, heard.Contains(adapter.Address) ? "switched on" : "not heard");
        }
        catch (Exception ex)
        {
            // A scan that fails costs the labels, not the menu.
            App.Report($"Could not scan for devices: {ex}");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Connect ▾";
        }

        // Reopened so the results are visible, since a menu cannot be rebuilt
        // while it is showing.
        OnConnectClick(this, new RoutedEventArgs());
    }

    private void Record(string key, string note) => _seen[key] = $"{note} at {DateTime.Now:HH:mm}";

    /// <summary>Opens the port and closes it again, returning why it could not be reached.</summary>
    private static Exception? TryReach(SerialPortInfo port)
    {
        // One attempt for a cable, which either works or does not; a second for
        // Bluetooth, where establishing the link is reported to fail once after
        // an ECU boots and succeed immediately after.
        using var transport = new SerialEcuTransport(port.PortName)
        {
            OpenAttempts = port.IsBluetooth ? 2 : 1,
        };

        try
        {
            transport.Open();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>
    /// Connects to a Bluetooth LE adapter.
    ///
    /// Its own path rather than a branch of the port one, because there is no
    /// port: nothing here has a COM number to open, probe or remember.
    /// </summary>
    public void StartLiveOverBle(BleDevice adapter, bool quiet = false)
    {
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            _vm.ConnectObd2Ble(adapter);
        }
        // Everything, as with the ports. A radio fails in more ways than a cable
        // and each type left off a list is an application that disappears
        // instead of showing a message.
        catch (Exception ex)
        {
            if (quiet) App.Report($"Could not connect to {adapter.Name}: {ex}");
            else
                MessageBox.Show(this,
                    $"Could not connect to {adapter.Name}.\n\n{ex.Message}",
                    "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        ConnectButton.Content = "Disconnect";
        _follow = true;

        _liveTimer = new DispatcherTimer { Interval = LiveRefresh };
        _liveTimer.Tick += OnLiveTick;
        _liveTimer.Start();
    }

    /// <summary>Connects to the first paired BLE adapter whose name matches, for a scripted run.</summary>
    public void ConnectToBle(string name)
    {
        App.Report($"connecting over Bluetooth LE to {name}…");

        BleDevice? adapter = BleDevices.All().FirstOrDefault(
            d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (adapter is null)
        {
            App.Report($"no paired Bluetooth LE device matching \"{name}\"");
            return;
        }

        StartLiveOverBle(adapter, quiet: true);
        App.Report(_vm.IsLive ? $"live: {_vm.Status}" : "not live");
    }

    private void StartLive(string port, bool quiet = false)
    {
        // Asked here rather than passed in, so every route to a connection gets
        // it right — the menu, the command line, and anything added later.
        SerialPortInfo? described = SerialPortNames.Describe()
            .FirstOrDefault(p => p.PortName.Equals(port, StringComparison.OrdinalIgnoreCase));

        bool bluetooth = described?.IsBluetooth ?? false;
        bool maxxEcu = described?.IsMaxxEcu ?? false;
        bool obd2 = _forceObd2 || (described?.IsObd2 ?? false);

        // Connecting reads the ECU's whole settings memory, which is 50 ms on a
        // rusEFI over USB and three seconds on a MegaSquirt over serial — 20 KB
        // in 256-byte pieces at 115200 baud. The window cannot repaint while
        // that runs, so at least say the wait is deliberate.
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            // Each of these speaks its own protocol; probing one with
            // TunerStudio commands would find nothing and tell the user the ECU
            // is unknown, which is a confusing thing to be told about hardware
            // that is working perfectly.
            if (obd2) _vm.ConnectObd2(port);
            else if (maxxEcu) _vm.ConnectMaxxEcu(port);
            else _vm.Connect(port, bluetooth);
        }
        // Everything, deliberately. Naming the expected types looked careful and
        // was not: a serial port answers with whatever it likes depending on
        // where it was when it failed, and each type left off the list is an
        // application that disappears instead of showing a message. It has
        // already been TimeoutException from a write and InvalidOperationException
        // from discarding a buffer. Failing to connect is never worth a crash.
        catch (Exception ex)
        {
            if (quiet) App.Report($"Could not connect on {port}: {ex}");
            else
                MessageBox.Show(this,
                    $"Could not connect on {port}.\n\n{ex.Message}",
                    "OpenLogViewer", MessageBoxButton.OK, MessageBoxImage.Warning);

            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
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

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) =>
        OpenFolder(Workspace.Ensure(_vm.Workspace.Root));

    private void OpenFolder(string folder)
    {
        try
        {
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
        int axisSource = 0, bool colourByCount = false, bool countStatistic = false,
        string? compareTo = null, string? zChannel = null)
    {
        _vm.ShowHistogram = true;

        // The measured channel, for a scripted run. Chosen before the comparison
        // so the two are settled together — a measurement and a target on
        // different scales is the one pairing this must never quietly accept.
        if (zChannel is { Length: > 0 })
        {
            ChannelItem? pick = _vm.Channels.FirstOrDefault(
                c => c.Name.Equals(zChannel, StringComparison.OrdinalIgnoreCase));

            if (pick is not null) _vm.ZAxis = pick;
        }

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

    /// <summary>
    /// Selects a cell of the tune table and optionally nudges it, for a scripted
    /// run.
    ///
    /// Local only. This is the same edit the buttons make and it goes no further
    /// than the copy on screen — a scripted run cannot send or burn anything.
    /// </summary>
    public void ActivateTuneCell(int column, int row, double nudge)
    {
        _vm.SelectedCells = TuneSelection.Cell(column, row);

        if (nudge != 0) _vm.EditTable(TuneTableEdit.Add(nudge));
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



