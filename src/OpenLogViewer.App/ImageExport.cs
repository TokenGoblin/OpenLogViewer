using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenLogViewer.App;

/// <summary>
/// Saves a view to a PNG by rendering it from the visual tree.
///
/// The element is drawn onto a fresh surface at its current size rather than
/// captured from the screen: a screen grab picks up whatever overlaps the
/// window, is clipped to the desktop, and comes out at whatever scaling the
/// display happens to use.
/// </summary>
public static class ImageExport
{
    /// <summary>
    /// Rendering scale. Two gives a file that survives being pasted into a
    /// forum post or a tuning write-up, where a 1:1 grab of a plot turns to mush.
    /// </summary>
    private const double Scale = 2.0;

    public static void Save(FrameworkElement element, string path, Brush? background = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Rounded up: truncating drops the last row and column of pixels, which
        // on a plot is the axis line.
        int width = (int)Math.Ceiling(element.ActualWidth * Scale);
        int height = (int)Math.Ceiling(element.ActualHeight * Scale);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("The view has no size to save.");

        var target = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);

        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        var visual = new DrawingVisual();

        using (DrawingContext dc = visual.RenderOpen())
        {
            // A view with a transparent ground would otherwise save as a PNG with
            // a see-through background, which most viewers show as black.
            if (background is not null) dc.DrawRectangle(background, null, bounds);

            // Through a VisualBrush rather than rendering the element directly:
            // RenderTargetBitmap.Render draws a visual at its position within its
            // parent, so the plot — which sits to the right of a 300px sidebar —
            // would come out shifted by that much and clipped at the right edge.
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None }, null, bounds);
        }

        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        // Through a temporary file: a half-written PNG left where the user asked
        // for one is worse than a failure they can see.
        string temporary = path + ".tmp";
        using (FileStream file = File.Create(temporary)) encoder.Save(file);
        File.Move(temporary, path, overwrite: true);
    }
}
