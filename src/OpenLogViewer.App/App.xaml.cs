using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenLogViewer.App;

public partial class App : Application
{
    private string[] _args = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;

        // The window is created here rather than via StartupUri so a file passed
        // on the command line (or by a file association) can be loaded before it
        // is shown, instead of flashing an empty viewer first.
        var window = new MainWindow();
        MainWindow = window;

        // Before Show, so a themed screenshot never captures the stored theme first.
        int theme = Array.IndexOf(e.Args, "--theme");
        if (theme >= 0 && theme + 1 < e.Args.Length) window.PreviewTheme(e.Args[theme + 1]);

        window.Show();

        string? log = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        if (log is not null) window.LoadFile(log);

        if (e.Args.Contains("--stacked")) window.SetStackedLanes(true);

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
        window.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                work();
                Shutdown();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Shutdown(1);
            }
        }, DispatcherPriority.ContextIdle);
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


