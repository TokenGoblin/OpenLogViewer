using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenLogViewer.App;

public partial class App : Application
{
    private string[] _args = [];

    /// <summary>
    /// Where a scripted run reports what went wrong.
    ///
    /// A file, not the console: this is a Windows GUI application, so it has no
    /// console attached and anything written to one goes nowhere. A failed
    /// --connect or --screenshot was therefore silent, which is the worst way
    /// for a scripted run to fail.
    /// </summary>
    public static string RunLog { get; } =
        Path.Combine(Path.GetTempPath(), "openlogviewer-run.log");

    public static void Report(string message)
    {
        try
        {
            File.AppendAllText(RunLog, $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Reporting a failure must not itself become one.
        }
    }

    /// <summary>Switches that are followed by a value rather than standing alone.</summary>
    private static readonly string[] TakesAValue =
    [
        "--theme", "--screenshot", "--export", "--connect", "--connect-ble", "--connect-menu",
        "--settle", "--menu", "--scan-menu", "--top-menu", "--calculators", "--power", "--calibration",
        "--cell", "--tune-cell", "--select", "--compare", "--z", "--tune-axes", "--pointer", "--mark",
        "--find", "--guide", "--settings", "--page", "--live-page",
        "--insights",
        "--open-tune", "--save-tune", "--compare-tune", "--plan-restore",
        "--faults", "--connect-ssm", "--connect-wifi", "--agent-api", "--tuning-project",
    ];

    /// <summary>
    /// The file to open, which is the one argument that is not a switch or a
    /// switch's value.
    ///
    /// Worth doing properly rather than taking the first token without a leading
    /// dash: that reads "--connect COM9" as a request to open a log called COM9,
    /// and the failure surfaces as a modal dialog before the window is shown,
    /// which looks exactly like the app hanging on startup.
    /// </summary>
    private static string? LogPathIn(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (TakesAValue.Contains(args[i], StringComparer.OrdinalIgnoreCase)) i++;
                continue;
            }

            return args[i];
        }

        return null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;

        // An exception escaping a background thread terminates the process
        // outright — no dialog, no log, the window simply vanishes. This cannot
        // prevent that, but it records what happened, which is the difference
        // between a reproducible bug and "it crashed".
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Report($"unhandled on a background thread: {args.ExceptionObject}");

        DispatcherUnhandledException += (_, args) =>
            Report($"unhandled on the UI thread: {args.Exception}");

        // The window is created here rather than via StartupUri so a file passed
        // on the command line (or by a file association) can be loaded before it
        // is shown, instead of flashing an empty viewer first.
        var window = new MainWindow();
        MainWindow = window;

        // Before Show, so a themed screenshot never captures the stored theme first.
        int theme = Array.IndexOf(e.Args, "--theme");
        if (theme >= 0 && theme + 1 < e.Args.Length) window.PreviewTheme(e.Args[theme + 1]);

        window.Show();

        string? log = LogPathIn(e.Args);
        if (log is not null) window.LoadFile(log);

        int connect = Array.IndexOf(e.Args, "--connect");
        if (connect >= 0 && connect + 1 < e.Args.Length)
            window.ConnectTo(e.Args[connect + 1], e.Args.Contains("--obd2"));

        // "--tuning-project [vehicle]" opens the project window, and the vehicle
        // with it where one is named.
        int project = Array.IndexOf(e.Args, "--tuning-project");
        if (project >= 0)
        {
            window.ShowProject(
                project + 1 < e.Args.Length && !e.Args[project + 1].StartsWith("--", StringComparison.Ordinal)
                    ? e.Args[project + 1]
                    : null);
        }

        // "--agent-api [port]" starts the local API without anybody clicking.
        // It still opens read-only: there is deliberately no flag that arms
        // writing, so the decision that lets a program change an engine cannot
        // be made once in a shortcut and then forgotten about.
        int agent = Array.IndexOf(e.Args, "--agent-api");
        if (agent >= 0)
        {
            window.StartAgentApi(
                agent + 1 < e.Args.Length && int.TryParse(e.Args[agent + 1], out int port) && port > 0
                    ? port
                    : 8765);
        }

        // "--connect-ssm COM10" opens a Subaru over its own protocol, which is a
        // deliberate choice rather than something guessed from the adapter.
        int ssm = Array.IndexOf(e.Args, "--connect-ssm");
        if (ssm >= 0 && ssm + 1 < e.Args.Length) window.ConnectOverSsm(e.Args[ssm + 1]);

        int ble = Array.IndexOf(e.Args, "--connect-ble");
        if (ble >= 0 && ble + 1 < e.Args.Length) window.ConnectToBle(e.Args[ble + 1]);

        // "--connect-wifi 192.168.0.10:35000" opens a Wi-Fi OBD2 dongle, which
        // is reachable by address and by nothing else. The address may be left
        // off — "--connect-wifi auto" — to try the ones they are known to use.
        int wifi = Array.IndexOf(e.Args, "--connect-wifi");
        if (wifi >= 0 && wifi + 1 < e.Args.Length)
        {
            string at = e.Args[wifi + 1];

            _ = window.ConnectToWifi(
                at.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : at);
        }

        int viaMenu = Array.IndexOf(e.Args, "--connect-menu");
        if (viaMenu >= 0 && viaMenu + 1 < e.Args.Length)
            _ = window.ConnectViaMenu(e.Args[viaMenu + 1]);

        if (e.Args.Contains("--stacked")) window.SetStackedLanes(true);

        if (e.Args.Contains("--gauges")) window.ShowGauges();

        int table = Array.IndexOf(e.Args, "--calibration");
        if (table >= 0)
            window.ShowCalibration(table + 1 < e.Args.Length && !e.Args[table + 1].StartsWith("--", StringComparison.Ordinal)
                ? e.Args[table + 1]
                : null);

        int scanned = Array.IndexOf(e.Args, "--scan-menu");
        if (scanned >= 0 && scanned + 1 < e.Args.Length)
        {
            string to = e.Args[scanned + 1];
            RunThenExit(window, async () => await window.CaptureScannedMenu(to));
            return;
        }

        // "--top-menu View out.png" draws one of the menu bar's drop-downs.
        int top = Array.IndexOf(e.Args, "--top-menu");
        if (top >= 0 && top + 2 < e.Args.Length)
        {
            string header = e.Args[top + 1];
            string to = e.Args[top + 2];
            RunThenExit(window, () => window.CaptureMenu(header, to));
            return;
        }

        // "--calculators Injectors out.png" draws one of the calculator tabs.
        int calc = Array.IndexOf(e.Args, "--calculators");
        if (calc >= 0 && calc + 2 < e.Args.Length)
        {
            string tab = e.Args[calc + 1];
            string to = e.Args[calc + 2];
            RunThenExit(window, () => window.CaptureCalculators(tab, to));
            return;
        }

        // "--power out.png" draws the power estimate over whatever log was opened.
        int power = Array.IndexOf(e.Args, "--power");
        if (power >= 0 && power + 1 < e.Args.Length)
        {
            string to = e.Args[power + 1];
            RunThenExit(window, () => window.CapturePower(to));
            return;
        }

        // "--faults out.png" draws the vehicle's fault codes over whatever OBD2
        // connection was made, which needs a --connect ahead of it to show any.
        int faults = Array.IndexOf(e.Args, "--faults");
        if (faults >= 0 && faults + 1 < e.Args.Length)
        {
            string to = e.Args[faults + 1];
            RunThenExit(window, () => window.CaptureFaults(to));
            return;
        }

        int menu = Array.IndexOf(e.Args, "--menu");
        if (menu >= 0 && menu + 1 < e.Args.Length)
        {
            string to = e.Args[menu + 1];
            RunThenExit(window, () => window.CaptureConnectMenu(to));
            return;
        }

        int cell = Array.IndexOf(e.Args, "--cell");
        if (cell >= 0 && cell + 1 < e.Args.Length)
        {
            string[] rc = e.Args[cell + 1].Split(',');
            if (rc.Length == 2 && int.TryParse(rc[0], out int col) && int.TryParse(rc[1], out int row))
                window.ActivateCell(col, row);
        }

        // "--tune-cell 4,4,1" selects a cell of the tune table and nudges it by
        // one, so a scripted run can draw what an edit actually looks like.
        // Local to the copy on screen; nothing is sent or burned.
        int tuneCell = Array.IndexOf(e.Args, "--tune-cell");
        if (tuneCell >= 0 && tuneCell + 1 < e.Args.Length)
        {
            string[] parts = e.Args[tuneCell + 1].Split(',');

            if (parts.Length >= 2
                && int.TryParse(parts[0], out int tc)
                && int.TryParse(parts[1], out int tr))
            {
                double nudge = parts.Length > 2
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                        ? n
                        : 0;

                window.ActivateTuneCell(tc, tr, nudge);
            }
        }

        int sel = Array.IndexOf(e.Args, "--select");
        if (sel >= 0 && sel + 1 < e.Args.Length)
        {
            string[] bounds = e.Args[sel + 1].Split(',');
            if (bounds.Length == 2
                && double.TryParse(bounds[0], out double from)
                && double.TryParse(bounds[1], out double to))
            {
                window.SelectRange(from, to);
            }
        }

        if (e.Args.Contains("--histogram"))
        {
            int at = Array.IndexOf(e.Args, "--tune-axes");
            bool given = at >= 0 && at + 1 < e.Args.Length && int.TryParse(e.Args[at + 1], out _);

            int cmp = Array.IndexOf(e.Args, "--compare");
            int z = Array.IndexOf(e.Args, "--z");

            window.ShowHistogram(
                given ? int.Parse(e.Args[at + 1]) : 0,
                e.Args.Contains("--count-colour"),
                e.Args.Contains("--count-value"),
                cmp >= 0 && cmp + 1 < e.Args.Length ? e.Args[cmp + 1] : null,
                z >= 0 && z + 1 < e.Args.Length ? e.Args[z + 1] : null);

            if (e.Args.Contains("--ve")) window.EnableVeAnalyze(e.Args.Contains("--ve-values"));
        }

        if (e.Args.Contains("--scatter"))
        {
            int cmp = Array.IndexOf(e.Args, "--compare");
            int z = Array.IndexOf(e.Args, "--z");

            window.ShowScatter(
                e.Args.Contains("--count-colour"),
                cmp >= 0 && cmp + 1 < e.Args.Length ? e.Args[cmp + 1] : null,
                z >= 0 && z + 1 < e.Args.Length ? e.Args[z + 1] : null);

            // "--mark 120,80" traces a block back to the log, the way --cell does
            // for the table.
            int mark = Array.IndexOf(e.Args, "--mark");
            if (mark >= 0 && mark + 1 < e.Args.Length
                && e.Args[mark + 1].Split(',') is [string c, string r]
                && int.TryParse(c, out int column) && int.TryParse(r, out int row))
            {
                window.ActivateMark(column, row);
            }
        }

        // "--find \"RPM > 4000\"" opens the find bar and frames the first hit.
        int find = Array.IndexOf(e.Args, "--find");
        if (find >= 0 && find + 1 < e.Args.Length) window.FindInLog(e.Args[find + 1]);

        // "--guide Scatter" opens the guide at a section, for a capture.
        int guide = Array.IndexOf(e.Args, "--guide");
        if (guide >= 0)
        {
            window.ShowGuide(
                guide + 1 < e.Args.Length && !e.Args[guide + 1].StartsWith("--", StringComparison.Ordinal)
                    ? e.Args[guide + 1]
                    : null);
        }

        // "--settings <ini> --page Rev" opens a definition's settings pages with
        // no controller behind them, for a capture.
        int settings = Array.IndexOf(e.Args, "--settings");
        if (settings >= 0 && settings + 1 < e.Args.Length)
        {
            int page = Array.IndexOf(e.Args, "--page");

            window.ShowSettings(
                e.Args[settings + 1],
                page >= 0 && page + 1 < e.Args.Length ? e.Args[page + 1] : null);
        }

        // "--live-page Rev" opens a settings page of the tune already loaded,
        // which on a live session is the one read off the controller.
        int livePage = Array.IndexOf(e.Args, "--live-page");
        if (livePage >= 0)
        {
            window.ShowSettingsPage(
                livePage + 1 < e.Args.Length && !e.Args[livePage + 1].StartsWith("--", StringComparison.Ordinal)
                    ? e.Args[livePage + 1]
                    : null);
        }

        // "--open-tune <msq> --page Rev" opens a saved tune and one of its
        // settings pages, which needs no controller.
        int openTune = Array.IndexOf(e.Args, "--open-tune");
        if (openTune >= 0 && openTune + 1 < e.Args.Length)
        {
            int page = Array.IndexOf(e.Args, "--page");

            window.ShowSavedTune(
                e.Args[openTune + 1],
                page >= 0 && page + 1 < e.Args.Length ? e.Args[page + 1] : null);
        }

        // "--compare-tune <msq>" says what a file and the tune in hand differ
        // about, and "--save-tune <msq>" writes the tune in hand to one.
        int compareTune = Array.IndexOf(e.Args, "--compare-tune");
        if (compareTune >= 0 && compareTune + 1 < e.Args.Length)
            window.CompareTune(e.Args[compareTune + 1]);

        // "--plan-restore <msq>" says what restoring a tune would change, and
        // does none of it. There is deliberately no flag that carries one out:
        // this is the largest change the application can make to an engine, and
        // it is not something to fall out of a command line.
        int planRestore = Array.IndexOf(e.Args, "--plan-restore");
        if (planRestore >= 0 && planRestore + 1 < e.Args.Length)
            window.PlanRestore(e.Args[planRestore + 1]);

        int saveTune = Array.IndexOf(e.Args, "--save-tune");
        if (saveTune >= 0 && saveTune + 1 < e.Args.Length)
            window.SaveTune(e.Args[saveTune + 1]);

        // "--insights" opens the findings for whatever log is loaded.
        if (Array.IndexOf(e.Args, "--insights") >= 0) window.ShowInsights();

        int shot = Array.IndexOf(e.Args, "--screenshot");
        if (shot >= 0 && shot + 1 < e.Args.Length)
            CaptureAndExit(window, e.Args[shot + 1]);

        int export = Array.IndexOf(e.Args, "--export");
        if (export >= 0 && export + 1 < e.Args.Length)
            ExportAndExit(window, e.Args[export + 1]);
    }

    /// <summary>
    /// Writes every export for the current mode into a folder and exits. Waits
    /// for layout first: the image exports render the views, which have no size
    /// until the window has been arranged.
    /// </summary>
    private void ExportAndExit(MainWindow window, string folder) =>
        RunThenExit(window, () =>
        {
            window.UpdateLayout();
            window.ExportAll(folder);
        });

    /// <summary>
    /// Runs a scripted action once layout has settled, then exits.
    ///
    /// The work is wrapped because Dispatcher.InvokeAsync captures an exception
    /// into the operation it returns rather than raising it. Nothing awaits that
    /// operation, so a failure here would otherwise leave the app sitting open
    /// with no error and no exit — which is indistinguishable from a hang.
    /// </summary>
    private void RunThenExit(Window window, Action work) => Schedule(window, () => Capture(work));

    /// <summary>
    /// Waits for the window to be ready, then runs something once.
    ///
    /// Separate from the running so that work which has to be awaited can be
    /// scheduled the same way without being wrapped in the synchronous capture,
    /// which would shut the application down at the first await.
    /// </summary>
    private void Schedule(Window window, Action run)
    {
        // "--settle <ms>" lets a scripted run wait before capturing, which a
        // live connection needs: there is nothing to draw until blocks arrive.
        int at = Array.IndexOf(_args, "--settle");
        if (at >= 0 && at + 1 < _args.Length && int.TryParse(_args[at + 1], out int delay) && delay > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                // Run here rather than queueing at ContextIdle. A live session
                // polls on a Background-priority timer, which outranks
                // ContextIdle, so queued work never runs while one is going —
                // the capture simply never happened. Waiting is what the settle
                // delay was for, so the layout has long since settled anyway.
                run();
            };

            timer.Start();
            return;
        }

        window.Dispatcher.InvokeAsync(run, DispatcherPriority.ContextIdle);
    }

    private bool Flag(string name) => _args.Contains(name, StringComparer.Ordinal);

    private void Capture(Action work)
    {
        try
        {
            work();
            Shutdown();
        }
        catch (Exception e)
        {
            Report($"scripted run failed: {e}");
            Shutdown(1);
        }
    }

    /// <summary>
    /// The same for work that has to be waited for.
    ///
    /// Passing an async lambda to the other one does not do this: it is an
    /// Action, so the lambda returns at its first await and the shutdown happens
    /// while the work is still running. A scan that takes three seconds would
    /// never finish and never be seen.
    /// </summary>
    private async void Capture(Func<Task> work)
    {
        try
        {
            await work();
            Shutdown();
        }
        catch (Exception e)
        {
            Report($"scripted run failed: {e}");
            Shutdown(1);
        }
    }

    private void RunThenExit(Window window, Func<Task> work) => Schedule(window, () => Capture(work));

    /// <summary>
    /// Renders the window straight from the visual tree and exits. Capturing from
    /// another process is unreliable under DWM composition, so for documentation
    /// shots the app draws itself.
    /// </summary>
    private void CaptureAndExit(Window window, string path) =>
        RunThenExit(window, () =>
        {
            // A window opened on top of the main one is what the run was asking
            // to see — the calculators, the insights, the fault codes. Capturing
            // the window behind it produces a picture of everything except the
            // thing under test.
            if (window.OwnedWindows.OfType<Window>().LastOrDefault(w => w.IsVisible) is { } child)
                window = child;

            window.UpdateLayout();

            // Optional "--pointer x,y" (fractions of the plot) so a screenshot can
            // show the hover readout, which otherwise needs a real mouse.
            int at = Array.IndexOf(_args, "--pointer");
            if (at >= 0 && at + 1 < _args.Length && window is MainWindow main)
            {
                string[] parts = _args[at + 1].Split(',');
                if (parts.Length == 2
                    && double.TryParse(parts[0], out double fx)
                    && double.TryParse(parts[1], out double fy))
                {
                    main.PreviewPointer(fx, fy);
                    window.UpdateLayout();
                }
            }

            // After the settle delay, because the axis sources do not exist
            // until a live session has produced its first samples.
            if (Flag("--ecu-ve") && window is MainWindow live)
            {
                Report(live.UseEcuVeTable() ? "using the ECU's fuel table" : "no ECU fuel table offered");
                if (Flag("--ve")) live.EnableVeAnalyze(Flag("--ve-values"));

                window.UpdateLayout();
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            var target = new RenderTargetBitmap(
                (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            target.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (FileStream file = File.Create(path)) encoder.Save(file);
        });
}


