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
        window.Show();

        string? log = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        if (log is not null) window.LoadFile(log);

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
        }

        int shot = Array.IndexOf(e.Args, "--screenshot");
        if (shot >= 0 && shot + 1 < e.Args.Length)
            CaptureAndExit(window, e.Args[shot + 1]);
    }

    /// <summary>
    /// Renders the window straight from the visual tree and exits. Capturing from
    /// another process is unreliable under DWM composition, so for documentation
    /// shots the app draws itself.
    /// </summary>
    private void CaptureAndExit(Window window, string path)
    {
        window.Dispatcher.InvokeAsync(() =>
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

            Shutdown();
        }, DispatcherPriority.ContextIdle);
    }
}
