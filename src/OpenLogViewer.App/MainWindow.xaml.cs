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

    private void OnResetGaugePeaksClick(object sender, RoutedEventArgs e) => _vm.ResetGaugePeaks();

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

        IReadOnlyList<SerialPortInfo> ports = SerialPortNames.Describe();
        IReadOnlyList<BleDevice> adapters = BleDevices.Obd2Adapters();

        // Cabled ports first, and they mean something: a USB adapter appears in
        // this list when it is plugged in and vanishes when it is not, so its
        // presence is already the answer to "is it there".
        SerialPortInfo[] wired = [.. ports.Where(p => !p.IsBluetooth)];

        foreach (SerialPortInfo port in wired) PortsMenu.Items.Add(PortItem(port));

        // Everything over a radio is a different matter. Pairing is a fact about
        // this computer rather than about the device, so a paired ECU is listed
        // whether or not it has power — which is how a menu comes to offer three
        // ECUs when none of them is switched on. Said plainly rather than left
        // to be discovered by picking one and waiting out a timeout.
        SerialPortInfo[] paired = [.. ports.Where(p => p.IsBluetooth)];

        if (paired.Length + adapters.Count > 0)
        {
            if (wired.Length > 0) PortsMenu.Items.Add(new Separator());

            PortsMenu.Items.Add(new MenuItem
            {
                Header = "Paired — listed whether or not switched on",
                IsEnabled = false,
            });

            foreach (SerialPortInfo port in paired) PortsMenu.Items.Add(PortItem(port));

            foreach (BleDevice adapter in adapters)
            {
                var item = new MenuItem
                {
                    Header = Decorate(adapter.Label, Seen(adapter.Name)),
                    ToolTip = "A Bluetooth LE OBD2 adapter. Reads any standard vehicle — "
                              + "no definition file needed.",
                };

                item.Click += (_, _) => StartLiveOverBle(adapter);
                PortsMenu.Items.Add(item);
            }

            var scan = new MenuItem
            {
                Header = "Check which of these are switched on",
                ToolTip = "Listens for the LE adapters and tries the others. Takes a few "
                          + "seconds, because a paired port that answers nothing has to be "
                          + "waited out before it can be called dead.",
            };

            scan.Click += async (_, _) => await ScanPaired(paired, adapters);
            PortsMenu.Items.Add(scan);
        }

        if (wired.Length + paired.Length + adapters.Count == 0)
            PortsMenu.Items.Add(new MenuItem { Header = "Nothing found", IsEnabled = false });

        PortsMenu.Items.Add(new Separator());
        PortsMenu.Items.Add(RateMenu());
        PortsMenu.Items.Add(UnitsMenu());

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
        PortsMenu.Items.Add(whole);

        var definitions = new MenuItem
        {
            Header = "ECU definition files…",
            ToolTip = "Where to put the .ini for an ECU this machine does not already know",
        };

        definitions.Click += (_, _) => OpenFolder(_vm.Workspace.EnsureDefinitions());
        PortsMenu.Items.Add(definitions);

        if (ports.Count > 0) PortsMenu.Items.Add(Obd2Menu(ports));

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

        foreach (UnitSystem system in MainViewModel.UnitSystems)
        {
            var item = new MenuItem
            {
                Header = system switch
                {
                    UnitSystem.Metric => "Metric  (°C, km/h)",
                    UnitSystem.Imperial => "Imperial  (°F, mph)",
                    _ => "As reported  (default)",
                },
                IsCheckable = true,
                IsChecked = system == _vm.Units,
                StaysOpenOnClick = false,
            };

            item.Click += (_, _) => _vm.Units = system;
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Display only — recordings keep the units the ECU reported",
            IsEnabled = false,
        });

        return menu;
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



