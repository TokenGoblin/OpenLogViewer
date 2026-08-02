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
        "--theme", "--screenshot", "--export", "--connect", "--settle", "--menu",
        "--cell", "--select", "--compare", "--tune-axes", "--pointer",
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
        if (connect >= 0 && connect + 1 < e.Args.Length) window.ConnectTo(e.Args[connect + 1]);

        if (e.Args.Contains("--stacked")) window.SetStackedLanes(true);

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

            window.ShowHistogram(
                given ? int.Parse(e.Args[at + 1]) : 0,
                e.Args.Contains("--count-colour"),
                e.Args.Contains("--count-value"),
                cmp >= 0 && cmp + 1 < e.Args.Length ? e.Args[cmp + 1] : null);

            if (e.Args.Contains("--ve")) window.EnableVeAnalyze(e.Args.Contains("--ve-values"));
        }

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
    private void RunThenExit(Window window, Action work)
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
                Capture(work);
            };

            timer.Start();
            return;
        }

        window.Dispatcher.InvokeAsync(() => Capture(work), DispatcherPriority.ContextIdle);
    }

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
    /// Renders the window straight from the visual tree and exits. Capturing from
    /// another process is unreliable under DWM composition, so for documentation
    /// shots the app draws itself.
    /// </summary>
    private void CaptureAndExit(Window window, string path) =>
        RunThenExit(window, () =>
        {
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


