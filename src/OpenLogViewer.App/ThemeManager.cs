using System.Windows;
using System.Windows.Media;

namespace OpenLogViewer.App;

/// <summary>
/// Holds the active theme and publishes it two ways: as brushes in the
/// application resource dictionary, which the XAML picks up through
/// DynamicResource, and as a <see cref="Changed"/> notification for the two
/// surfaces that paint themselves — the plot and the heat table — since neither
/// goes through the styling system.
/// </summary>
public static class ThemeManager
{
    private static Theme _current = ThemeCatalog.Default;

    /// <summary>Raised after <see cref="Current"/> and the resources have both been updated.</summary>
    public static event Action<Theme>? Changed;

    public static Theme Current => _current;

    public static void Apply(Theme theme)
    {
        _current = theme;

        if (Application.Current is { } app) Publish(app.Resources, theme);

        Changed?.Invoke(theme);
    }

    public static void Apply(string? id) => Apply(ThemeCatalog.Find(id));

    /// <summary>
    /// Writes the theme's colours into a resource dictionary. Keys match the
    /// names the XAML binds to; every one is replaced on each apply, so no stale
    /// brush from the previous theme can survive.
    /// </summary>
    public static void Publish(ResourceDictionary resources, Theme theme)
    {
        Set(resources, "Bg", theme.Background);
        Set(resources, "Panel", theme.Panel);
        Set(resources, "PanelAlt", theme.PanelAlt);
        Set(resources, "Hover", theme.Hover);
        Set(resources, "Selected", theme.Selected);
        Set(resources, "Header", theme.Header);
        Set(resources, "Line", theme.Line);
        Set(resources, "Text", theme.Text);
        Set(resources, "Muted", theme.Muted);
        Set(resources, "Faint", theme.Faint);
        Set(resources, "Accent", theme.Accent);
        Set(resources, "OnAccent", theme.OnAccent);
        Set(resources, "Marker", theme.Marker);
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
