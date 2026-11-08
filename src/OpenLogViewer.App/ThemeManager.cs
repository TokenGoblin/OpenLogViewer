using System.Windows;
using System.Windows.Media;

namespace OpenLogViewer.App;

/// <summary>
/// Holds the active theme and publishes it three ways: as resources in the
/// application dictionary, which the XAML picks up through DynamicResource;
/// as a <see cref="Changed"/> notification for the surfaces that paint
/// themselves, since none of them goes through the styling system; and onto
/// every window directly, for the few things a resource cannot reach — the
/// title bar, and the font a window hands down to everything inside it.
/// </summary>
public static class ThemeManager
{
    private static Theme _current = ThemeCatalog.Default;

    /// <summary>Raised after <see cref="Current"/> and the resources have both been updated.</summary>
    public static event Action<Theme>? Changed;

    public static Theme Current => _current;

    /// <summary>
    /// The corner radii the XAML draws with, by resource key. A template names
    /// the size it wants and the theme decides what that size is, so a square
    /// theme does not have to restate every border.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, double> Corners = new Dictionary<string, double>
    {
        ["CornerSmall"] = 3,
        ["Corner"] = 4,
        ["CornerMedium"] = 5,
        ["CornerLarge"] = 6,
        ["CornerPill"] = 9,
    };

    public static void Apply(Theme theme)
    {
        _current = theme;

        if (Application.Current is { } app) Publish(app.Resources, theme);

        Changed?.Invoke(theme);
    }

    public static void Apply(string? id) => Apply(ThemeCatalog.Find(id));

    /// <summary>
    /// Writes the theme into a resource dictionary. Keys match the names the
    /// XAML binds to; every one is replaced on each apply, so nothing from the
    /// previous theme can survive.
    /// </summary>
    public static void Publish(ResourceDictionary resources, Theme theme)
    {
        Set(resources, "Bg", theme.Background);
        Set(resources, "Panel", theme.Panel);
        Set(resources, "PanelAlt", theme.PanelAlt);
        Set(resources, "Chrome", theme.Chrome);
        Set(resources, "Hover", theme.Hover);
        Set(resources, "Selected", theme.Selected);
        Set(resources, "Header", theme.Header);
        Set(resources, "Line", theme.Line);
        Set(resources, "Text", theme.Text);
        Set(resources, "Muted", theme.Muted);
        Set(resources, "Faint", theme.Faint);
        Set(resources, "Readout", theme.Readout);
        Set(resources, "Scroll", theme.Scroll);
        Set(resources, "ScrollHover", theme.ScrollHover);
        Set(resources, "Accent", theme.Accent);
        Set(resources, "OnAccent", theme.OnAccent);
        Set(resources, "Marker", theme.Marker);

        ThemeStyle style = theme.Style;

        resources["UiFont"] = ThemeFonts.Family(style.UiFont);
        resources["DataFont"] = ThemeFonts.Family(style.DataFont);

        foreach ((string key, double radius) in Corners)
            resources[key] = new CornerRadius(radius * style.CornerScale);

        resources["TextRendering"] = style.PixelText ? TextRenderingMode.Aliased : TextRenderingMode.Auto;
        resources["TextFormatting"] = style.PixelText ? TextFormattingMode.Display : TextFormattingMode.Ideal;
    }

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>
    /// Makes every window the application opens follow the theme, from now on.
    ///
    /// A class handler rather than a call in each window's constructor: there
    /// are six windows, and the one somebody adds next would otherwise open in
    /// Segoe UI under a white title bar until someone noticed. Font and text
    /// rendering are set on the window because they are inherited — everything
    /// inside, templates included, picks them up from there, which an implicit
    /// TextBlock style cannot do across a template boundary.
    /// </summary>
    public static void FollowAllWindows() =>
        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || e.OriginalSource != window) return;

        Follow(window);
    }

    /// <summary>Attaches one window. Safe to call more than once on the same window.</summary>
    internal static void Follow(Window window)
    {
        window.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "UiFont");
        window.SetResourceReference(TextOptions.TextRenderingModeProperty, "TextRendering");
        window.SetResourceReference(TextOptions.TextFormattingModeProperty, "TextFormatting");

        // The interface zoom. On the content rather than the window, which WPF
        // does not allow a layout transform on.
        if (window.Content is FrameworkElement content)
            content.SetResourceReference(FrameworkElement.LayoutTransformProperty, "UiScale");

        TitleBar.Apply(window, _current);

        // Loaded fires again when a window is re-shown, so detach before
        // attaching or each showing would add another subscription.
        Action<Theme> repaint = TitleBarFor(window);
        Changed -= repaint;
        Changed += repaint;
        window.Closed -= Detach;
        window.Closed += Detach;
    }

    /// <summary>One handler per window, kept so it can be removed again.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Action<Theme>> TitleBars = new();

    private static Action<Theme> TitleBarFor(Window window) =>
        TitleBars.GetValue(window, w => theme => TitleBar.Apply(w, theme));

    private static void Detach(object? sender, EventArgs e)
    {
        if (sender is Window window && TitleBars.TryGetValue(window, out Action<Theme>? repaint))
            Changed -= repaint;
    }
}
