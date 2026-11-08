using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace OpenLogViewer.App;

/// <summary>
/// Turns a theme's font strings into font families.
///
/// One place, so XAML (through the resources <see cref="ThemeManager"/>
/// publishes) and the views that draw their own text resolve a name the same
/// way. A view that built its own <c>new FontFamily("…")</c> would quietly
/// ignore a bundled font — the name would not be found and WPF would fall back
/// to its default without saying so.
/// </summary>
public static class ThemeFonts
{
    /// <summary>
    /// The pixel font shipped in <c>Fonts/</c>: OLV Retro 8x16, drawn for this
    /// project in the spirit of a DOS text-mode screen and licensed MIT like the
    /// rest of it. It covers ASCII and the symbols the interface uses (°, Δ, the
    /// arrows, the ellipsis); anything else falls back to Consolas glyph by glyph
    /// rather than drawing boxes.
    /// </summary>
    public const string Retro = "./Fonts/#OLV Retro, Consolas";

    /// <summary>Where a <c>./Fonts/…</c> name is looked up: this assembly's embedded resources.</summary>
    private static readonly Uri Base;

    private static readonly ConcurrentDictionary<string, FontFamily> Cache = new();

    static ThemeFonts()
    {
        // pack://application: is wired up by Application's static constructor,
        // not by the pack scheme itself. The application always has run it by
        // the time a theme is applied; a test that never touched Application
        // has not, and then the font is not found and WPF silently draws the
        // fallback instead — which is how this was discovered.
        RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        Base = new Uri("pack://application:,,,/OpenLogViewer.App;component/");
    }

    public static FontFamily Family(string spec) =>
        Cache.GetOrAdd(spec, s => new FontFamily(Base, s));

    public static Typeface Typeface(string spec, FontWeight weight) =>
        new(Family(spec), FontStyles.Normal, weight, FontStretches.Normal);
}
