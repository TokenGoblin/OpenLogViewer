using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModelContextProtocol.Server;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// What the window is showing, and the few things that change that.
///
/// <para>
/// Every method here — reads included — goes through the dispatcher. A live
/// session appends samples on a timer, so an unmarshalled read can catch the
/// view model mid-update.
/// </para>
/// </summary>
[McpServerToolType]
public static class AppTools
{
    [McpServerTool]
    [Description(
        "What OpenLogViewer currently has open: the workspace mode and view, the loaded log, "
        + "the loaded tune, whether a controller is connected, and the theme. Call this first — "
        + "most other tools need one of these to be true and say so in their refusals.")]
    public static Task<object> GetAppState(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() => new
        {
            mode = vm.Mode.ToString(),
            view = vm.LogView.ToString(),
            theme = vm.SelectedTheme.Id,
            workspace = vm.DataFolder,
            units = vm.Units.ToString(),
            log = vm.Document is { } document
                ? new
                {
                    loaded = true,
                    path = document.FilePath,
                    format = document.FormatName,
                    samples = document.SampleCount,
                    seconds = Math.Round(document.Duration, 3),
                    channels = document.Channels.Count,
                }
                : (object)new { loaded = false },
            live = new
            {
                running = vm.IsLive,
                healthy = vm.LiveHealthy,
                status = vm.LiveStatus,
                obd2 = vm.IsObd2Live,
                recording = vm.IsRecording,
            },
            tune = new
            {
                loaded = vm.HasEcuTune,
                source = vm.TuneSource,
                detail = vm.TuneDetail,
                fromFile = vm.TuneIsFromFile,
                placeholder = vm.TuneIsPlaceholder,
                warning = vm.HasTuneWarning ? vm.TuneWarning : null,
            },
            comparison = vm.HasComparison ? vm.CompareName : null,
        });

    [McpServerTool]
    [Description(
        "Switches the workspace mode. One of: Log, Gauges, Calibration, Guide. This is what the "
        + "View menu does, so the change is visible in the window.")]
    public static Task<object> SetWorkspaceMode(
        [Description("Log, Gauges, Calibration or Guide.")] string mode,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!Enum.TryParse(mode, ignoreCase: true, out WorkspaceMode wanted))
            {
                return new
                {
                    changed = false,
                    reason = $"'{mode}' is not a workspace mode. Use Log, Gauges, Calibration or Guide.",
                };
            }

            vm.Mode = wanted;

            return new { changed = true, mode = vm.Mode.ToString() };
        });

    [McpServerTool]
    [Description(
        "Switches which analysis the Log workspace shows. One of: Plot, Histogram, Scatter. "
        + "Histogram and Scatter need a log open and their axes chosen — build_histogram and "
        + "build_scatter do both in one call.")]
    public static Task<object> SetView(
        [Description("Plot, Histogram or Scatter.")] string view,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!Enum.TryParse(view, ignoreCase: true, out LogView wanted))
                return new { changed = false, reason = $"'{view}' is not a view. Use Plot, Histogram or Scatter." };

            vm.LogView = wanted;

            return new { changed = true, view = vm.LogView.ToString() };
        });

    [McpServerTool]
    [Description(
        "Draws the window to a PNG file and returns the path. The cheapest way to see what a "
        + "change actually did, rather than inferring it from the state tools.")]
    public static Task<object> Screenshot(
        [Description("Where to write the PNG. An existing file is replaced.")] string path,
        IWindowSource windows,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (windows.Window is not { } window)
                return new { captured = false, reason = "There is no window to draw." };

            // A window opened on top of the main one is what was being looked at
            // — the calculators, the insights, the fault codes. Capturing the one
            // behind it produces a picture of everything except the subject.
            Window target = window.OwnedWindows.OfType<Window>().LastOrDefault(w => w.IsVisible)
                            ?? window;

            target.UpdateLayout();

            DpiScale dpi = VisualTreeHelper.GetDpi(target);

            if (target.ActualWidth <= 0 || target.ActualHeight <= 0)
                return new { captured = false, reason = "The window has no size yet." };

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);

            bitmap.Render(target);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            try
            {
                using FileStream file = File.Create(path);
                encoder.Save(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new { captured = false, reason = $"Could not write {path}: {e.Message}" };
            }

            return new { captured = true, path, width = bitmap.PixelWidth, height = bitmap.PixelHeight };
        });

    [McpServerTool]
    [Description(
        "The tail of the application's run log, which is where a scripted or agent-triggered "
        + "failure is recorded. Useful when a tool reported something went wrong and the detail "
        + "is not in the reply.")]
    public static Task<object> GetRunLog(
        [Description("How many lines from the end. Capped at 200.")] int lines = 40)
    {
        // Deliberately not marshalled: this reads a file, touches no view model
        // state, and is the tool most likely to be called while the UI thread is
        // busy with whatever went wrong.
        int wanted = Math.Clamp(lines, 1, 200);

        if (!File.Exists(App.RunLog))
            return Task.FromResult<object>(new { read = true, path = App.RunLog, lines = Array.Empty<string>() });

        try
        {
            string[] all = File.ReadAllLines(App.RunLog);

            return Task.FromResult<object>(new
            {
                read = true,
                path = App.RunLog,
                lines = all.TakeLast(wanted).ToArray(),
            });
        }
        catch (IOException e)
        {
            return Task.FromResult<object>(new { read = false, reason = e.Message });
        }
    }
}
