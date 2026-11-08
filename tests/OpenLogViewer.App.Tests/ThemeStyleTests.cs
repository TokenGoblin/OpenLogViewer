using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using OpenLogViewer.App;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The non-colour half of a theme, and the guard that keeps it reaching
/// everything. A theme can only change the font or the corners of what reads
/// them from the theme; one hardcoded <c>"Segoe UI"</c> or
/// <c>CornerRadius="4"</c> and that one place stays modern in every theme, which
/// nobody notices until they switch to Retro.
/// </summary>
public class ThemeStyleTests
{
    public static TheoryData<string> AllThemes()
    {
        var data = new TheoryData<string>();
        foreach (Theme theme in ThemeCatalog.Themes) data.Add(theme.Id);
        return data;
    }

    [Fact]
    public void TheRetroThemeIsInTheCatalogWithItsOwnStyle()
    {
        Theme retro = ThemeCatalog.Find("retro");

        Assert.Equal("retro", retro.Id);
        Assert.Equal(ThemeGroup.Retro, retro.Group);
        Assert.Equal(0, retro.Style.CornerScale);
        Assert.True(retro.Style.PixelText);
        Assert.Equal(TableStyle.Grid, retro.Style.Table);
        Assert.Equal(ThemeFonts.Retro, retro.Style.UiFont);
    }

    [Fact]
    public void ThemesThatOnlyRecolourLookExactlyAsTheyDidBefore()
    {
        // Style arrived after fourteen themes existed. None of them states one,
        // and none of them should have changed by its arrival.
        foreach (Theme theme in ThemeCatalog.Themes.Where(t => t.Group != ThemeGroup.Retro))
        {
            Assert.Same(ThemeStyle.Modern, theme.Style);
            Assert.Equal(theme.Text, theme.Readout);
            Assert.Equal(theme.Panel, theme.Chrome);
            Assert.Equal(ColorMath.Blend(theme.Background, theme.Text, 0.13), theme.Grid);
        }
    }

    [Fact]
    public void TheModernStyleIsWhatTheApplicationDrewBeforeThemesHadOne()
    {
        Assert.Equal("Segoe UI", ThemeStyle.Modern.UiFont);
        Assert.Equal("Consolas", ThemeStyle.Modern.DataFont);
        Assert.Equal(1, ThemeStyle.Modern.CornerScale);
        Assert.False(ThemeStyle.Modern.PixelText);
        Assert.Equal(TableStyle.Heat, ThemeStyle.Modern.Table);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TextIsReadableOnTheWindowFrame(string id)
    {
        // Chrome is the menu bar and status strip, and carries body text.
        Theme theme = ThemeCatalog.Find(id);
        Assert.True(ColorMath.Contrast(theme.Text, theme.Chrome) >= 4.5,
            $"{id}: text on chrome is {ColorMath.Contrast(theme.Text, theme.Chrome):F2}:1");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void ReadoutsAreReadableWhereverTheyAreDrawn(string id)
    {
        Theme theme = ThemeCatalog.Find(id);
        Assert.True(ColorMath.Contrast(theme.Readout, theme.Background) >= 4.5);
        Assert.True(ColorMath.Contrast(theme.Readout, theme.Card) >= 4.5);
    }

    [Fact]
    public void TheBundledFontIsCompiledInAndFoundByName()
    {
        // A pack URI that names nothing does not throw: WPF falls back to its
        // default face and says nothing. So ask for the glyphs themselves and
        // check whose they are.
        var face = ThemeFonts.Typeface(ThemeFonts.Retro, FontWeights.Normal);

        Assert.True(face.TryGetGlyphTypeface(out GlyphTypeface glyphs));
        Assert.Contains("OLV Retro", glyphs.FamilyNames.Values);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void PublishingWritesEveryResourceTheXamlAsksFor(string id)
    {
        var resources = new ResourceDictionary();
        ThemeManager.Publish(resources, ThemeCatalog.Find(id));
        InterfaceScale.Publish(resources, 1);

        foreach (string key in DynamicKeysInXaml())
            Assert.True(resources.Contains(key), $"{id}: the XAML binds {key}, which the theme never publishes");
    }

    [Fact]
    public void CornersScaleWithTheTheme()
    {
        var resources = new ResourceDictionary();

        ThemeManager.Publish(resources, ThemeCatalog.Find("retro"));
        Assert.Equal(new CornerRadius(0), resources["Corner"]);

        ThemeManager.Publish(resources, ThemeCatalog.Find("midnight"));
        Assert.Equal(new CornerRadius(4), resources["Corner"]);
    }

    [Fact]
    public void NoXamlHardcodesAFontOrACornerRadius()
    {
        var offences = new List<string>();

        foreach (string file in Directory.EnumerateFiles(AppSource, "*.xaml"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // Icons are glyphs in a symbol font, not text; a theme has no business changing them.
                if (Regex.IsMatch(line, @"FontFamily=""(?!\{|Segoe MDL2 Assets)")
                    || Regex.IsMatch(line, @"Property=""FontFamily"" Value=""(?!\{)")
                    || Regex.IsMatch(line, @"CornerRadius=""[\d.,\s]+"""))
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(offences.Count == 0, "Bind to the theme instead:\n" + string.Join("\n", offences));
    }

    [Fact]
    public void NoViewNamesAFontInCode()
    {
        // ThemeStyle states the defaults and ThemeFonts resolves them; nothing
        // else should be naming a face.
        string[] allowed = ["Theme.cs", "ThemeFonts.cs"];
        var offences = new List<string>();

        foreach (string file in Directory.EnumerateFiles(AppSource, "*.cs"))
        {
            if (allowed.Contains(Path.GetFileName(file))) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"""(Segoe UI|Consolas|Courier New|Arial|Calibri)""")
                    || Regex.IsMatch(lines[i], @"new FontFamily\("))
                {
                    offences.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offences.Count == 0, "Use ThemeFonts with the theme's style:\n" + string.Join("\n", offences));
    }

    private static IEnumerable<string> DynamicKeysInXaml() =>
        Directory.EnumerateFiles(AppSource, "*.xaml")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"DynamicResource (\w+)").Select(m => m.Groups[1].Value))
            .Distinct();

    private static string AppSource => Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(ThisFile())!, "..", "..", "src", "OpenLogViewer.App"));

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
