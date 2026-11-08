using System.Windows;
using System.Windows.Media;

namespace OpenLogViewer.App;

/// <summary>
/// How large the interface is drawn, as a zoom on every window.
///
/// A zoom rather than a font size: the windows carry some two hundred fixed text
/// sizes and as many fixed widths, so larger text alone would overflow the
/// buttons and rows it sits in. Scaling the whole of each window keeps every
/// proportion, and WPF redraws text and the self-painted views at the new size
/// rather than stretching a bitmap, so it stays sharp.
///
/// Published as the <c>UiScale</c> resource. Each window's content takes it as
/// its layout transform (see <see cref="ThemeManager.Follow"/>), and so do the
/// popups — menus, drop-downs, tooltips — which open in trees of their own and
/// would otherwise stay at the old size.
/// </summary>
public static class InterfaceScale
{
    /// <summary>The sizes offered, as fractions of the design size.</summary>
    public static IReadOnlyList<double> Steps { get; } = [1.0, 1.15, 1.25, 1.5, 1.75, 2.0];

    public static double Current { get; private set; } = 1;

    public static event Action<double>? Changed;

    public static void Apply(double scale)
    {
        Current = Snap(scale);

        if (Application.Current is { } app) Publish(app.Resources, Current);

        Changed?.Invoke(Current);
    }

    public static void Publish(ResourceDictionary resources, double scale)
    {
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        resources["UiScale"] = transform;
    }

    /// <summary>The offered step nearest a value, so a stored or typed scale lands on one.</summary>
    public static double Snap(double scale) =>
        double.IsFinite(scale) ? Steps.MinBy(s => Math.Abs(s - scale)) : 1;

    public static double Larger(double scale) =>
        Steps.FirstOrDefault(s => s > Snap(scale) + 1e-9, Steps[^1]);

    public static double Smaller(double scale) =>
        Steps.LastOrDefault(s => s < Snap(scale) - 1e-9, Steps[0]);

    public static string Label(double scale) => $"{scale * 100:0}%";
}
