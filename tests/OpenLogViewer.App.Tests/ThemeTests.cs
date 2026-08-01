using System.Windows.Media;
using OpenLogViewer.App;
using Xunit;

namespace OpenLogViewer.App.Tests;

public class ThemeTests
{
    public static TheoryData<string> AllThemes()
    {
        var data = new TheoryData<string>();
        foreach (Theme t in ThemeCatalog.Themes) data.Add(t.Id);
        return data;
    }

    private static Theme Get(string id) => ThemeCatalog.Find(id);

    [Fact]
    public void TheCatalogOffersADozenSchemesAcrossEveryGroup()
    {
        Assert.True(ThemeCatalog.Themes.Count >= 12);

        foreach (ThemeGroup group in Enum.GetValues<ThemeGroup>())
            Assert.Contains(ThemeCatalog.Themes, t => t.Group == group);
    }

    [Fact]
    public void ThemeIdsAreUniqueAndStable()
    {
        // Ids are written into settings.json, so a duplicate would make the
        // stored preference ambiguous.
        var ids = ThemeCatalog.Themes.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AnUnknownOrMissingIdFallsBackToTheDefault()
    {
        // A hand-edited settings file must not stop the app starting.
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.Find("no-such-theme").Id);
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.Find(null).Id);
        Assert.Equal(ThemeCatalog.DefaultId, ThemeCatalog.Find("").Id);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryThemeOffersEightTraceColours(string id) =>
        Assert.Equal(8, Get(id).Series.Length);

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TraceColoursClearTheirOwnBackground(string id)
    {
        // 3:1 is the floor for a graphical object to be perceivable. A trace
        // below it against the ground it is drawn on is simply not visible.
        Theme theme = Get(id);

        foreach (Color c in theme.Series)
            Assert.True(ColorMath.Contrast(c, theme.Background) >= 3.0,
                $"{id}: {c} holds only {ColorMath.Contrast(c, theme.Background):F2}:1");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void AdjacentTraceColoursStayApartUnderColourVisionDeficiency(string id)
    {
        // Colours are handed out in order, so consecutive entries are the pair
        // most likely to be on screen together. 6.0 is the floor the palettes
        // were snapped to; the app carries direct labels and a sidebar swatch as
        // the secondary encoding that floor assumes.
        Theme theme = Get(id);

        for (int i = 1; i < theme.Series.Length; i++)
        {
            double gap = CvdSeparation(theme.Series[i - 1], theme.Series[i]);
            Assert.True(gap >= 6.0, $"{id}: entries {i - 1} and {i} differ by only {gap:F1}");
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void NoTraceColourReadsAsGrey(string id)
    {
        // Below this chroma a hue stops being identifiable as a colour at all,
        // and two such traces are told apart only by lightness.
        foreach (Color c in Get(id).Series)
            Assert.True(ColorMath.ToOklch(c).C >= 0.10, $"{id}: {c} is nearly grey");
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void BodyTextIsReadableOnEverySurface(string id)
    {
        Theme theme = Get(id);

        Assert.True(ColorMath.Contrast(theme.Text, theme.Background) >= 4.5);
        Assert.True(ColorMath.Contrast(theme.Text, theme.Panel) >= 4.5);
        Assert.True(ColorMath.Contrast(theme.Text, theme.PanelAlt) >= 4.5);

        // Supporting text may be quieter, but still has to be legible.
        Assert.True(ColorMath.Contrast(theme.Muted, theme.Panel) >= 3.0);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void InkOnTheAccentIsReadable(string id)
    {
        // The jump buttons put text on the accent when hovered; picking the
        // wrong end would leave the glyph invisible at the moment it is aimed at.
        Theme theme = Get(id);
        Assert.True(ColorMath.Contrast(theme.OnAccent, theme.Accent) >= 4.5);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheSequentialRampRunsOneWayInLightness(string id)
    {
        // A ramp that reverses anywhere would read as two different magnitudes
        // at the same cell value.
        Color[] ramp = Get(id).SequentialRamp;
        bool dark = Get(id).IsDark;

        for (int i = 1; i < ramp.Length; i++)
        {
            double previous = ColorMath.ToOklch(ramp[i - 1]).L;
            double current = ColorMath.ToOklch(ramp[i]).L;

            Assert.True(dark ? current > previous : current < previous,
                $"{id}: step {i} breaks the ramp ({previous:F3} → {current:F3})");
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EachStepOfTheRampIsDistinctFromTheOneBeforeIt(string id)
    {
        Color[] ramp = Get(id).SequentialRamp;

        for (int i = 1; i < ramp.Length; i++)
        {
            double step = Math.Abs(ColorMath.ToOklch(ramp[i]).L - ColorMath.ToOklch(ramp[i - 1]).L);
            Assert.True(step >= 0.06, $"{id}: steps {i - 1} and {i} differ by only {step:F3}");
        }
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void BothDivergingArmsStartFromTheSameNeutralMidpoint(string id)
    {
        // The arms meet at zero. Two different midpoints would put a step in the
        // scale exactly where the value is on target.
        Theme theme = Get(id);
        Assert.Equal(theme.CoolArm[0], theme.WarmArm[0]);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheDivergingMidpointRecedesIntoTheSurface(string id)
    {
        // On-target cells should be the quiet ones; a loud midpoint would draw
        // the eye to exactly the cells that need no attention.
        Theme theme = Get(id);
        Assert.True(ColorMath.Contrast(theme.CoolArm[0], theme.Background) < 2.0);
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheTwoArmsAreTellableApartAwayFromZero(string id)
    {
        // Rich and lean must not look alike, or the table cannot show which way
        // a cell is off.
        Theme theme = Get(id);
        Assert.True(CvdSeparation(theme.CoolArm[^1], theme.WarmArm[^1]) >= 8.0);
    }

    [Fact]
    public void AnEmptyCellIsDistinguishableFromAPopulatedOne()
    {
        // "Never visited" and "visited once" mean different things.
        foreach (Theme theme in ThemeCatalog.Themes)
        {
            double ratio = ColorMath.Contrast(theme.EmptyCell, theme.SequentialRamp[0]);
            Assert.True(ratio >= 1.5, $"{theme.Id}: an empty cell and the bottom of the ramp differ by {ratio:F2}:1");
        }
    }

    [Fact]
    public void DarkAndLightThemesAreClassifiedByTheirBackground()
    {
        Assert.True(Get("midnight").IsDark);
        Assert.True(Get("contrast-dark").IsDark);
        Assert.False(Get("daylight").IsDark);
        Assert.False(Get("solarized-light").IsDark);
    }

    [Fact]
    public void HighContrastThemesEarnTheName()
    {
        foreach (Theme theme in ThemeCatalog.Themes.Where(t => t.Group == ThemeGroup.HighContrast))
        {
            Assert.True(ColorMath.Contrast(theme.Text, theme.Background) >= 15.0);
            Assert.True(ColorMath.Contrast(theme.Line, theme.Background) >= 7.0);
        }
    }

    // ----- colour maths -----------------------------------------------------

    [Fact]
    public void BlendReachesBothEndsAndTheMiddle()
    {
        Assert.Equal(Colors.Black, ColorMath.Blend(Colors.Black, Colors.White, 0));
        Assert.Equal(Colors.White, ColorMath.Blend(Colors.Black, Colors.White, 1));
        Assert.Equal(Color.FromRgb(128, 128, 128), ColorMath.Blend(Colors.Black, Colors.White, 0.5));
    }

    [Fact]
    public void BlendClampsRatherThanExtrapolating()
    {
        Assert.Equal(Colors.Black, ColorMath.Blend(Colors.Black, Colors.White, -2));
        Assert.Equal(Colors.White, ColorMath.Blend(Colors.Black, Colors.White, 9));
    }

    [Fact]
    public void ContrastMatchesTheKnownExtremes()
    {
        Assert.Equal(21.0, ColorMath.Contrast(Colors.Black, Colors.White), 1);
        Assert.Equal(1.0, ColorMath.Contrast(Colors.Red, Colors.Red), 3);
    }

    [Fact]
    public void ReadablePicksTheEndThatActuallyStandsOut()
    {
        Assert.Equal(Colors.Black, ColorMath.Readable(Colors.White, Colors.Black, Colors.White));
        Assert.Equal(Colors.White, ColorMath.Readable(Colors.Black, Colors.Black, Colors.White));
    }

    [Fact]
    public void OklabSurvivesARoundTrip()
    {
        foreach (Color c in (Color[])[Colors.Red, Colors.SeaGreen, Colors.BlueViolet, Colors.Gainsboro])
        {
            (double l, double a, double b) = ColorMath.ToOklab(c);
            Color back = ColorMath.FromOklab(l, a, b);

            Assert.True(Math.Abs(back.R - c.R) <= 1 && Math.Abs(back.G - c.G) <= 1 && Math.Abs(back.B - c.B) <= 1,
                $"{c} came back as {back}");
        }
    }

    [Fact]
    public void AGreyAnchorStillProducesAUsableRamp()
    {
        // Chroma is floored, so a badly chosen anchor cannot yield a ramp of
        // indistinguishable greys.
        Color[] ramp = ColorMath.Ramp(Colors.Gray, onDark: true);

        Assert.Equal(5, ramp.Length);
        Assert.All(ramp, c => Assert.True(ColorMath.ToOklch(c).C > 0));
    }

    /// <summary>Worst-case OKLab separation across protanopia and deuteranopia, ×100.</summary>
    private static double CvdSeparation(Color a, Color b) =>
        Math.Min(Distance(Simulate(a, true), Simulate(b, true)),
                 Distance(Simulate(a, false), Simulate(b, false)));

    private static double Distance(Color a, Color b)
    {
        (double l1, double a1, double b1) = ColorMath.ToOklab(a);
        (double l2, double a2, double b2) = ColorMath.ToOklab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2)) * 100;
    }

    /// <summary>Brettel/Viénot-style simulation, in linear sRGB.</summary>
    private static Color Simulate(Color c, bool protan)
    {
        double[][] m = protan
            ? [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]]
            : [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.011820, 0.042940, 0.968881]];

        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);

        return Color.FromRgb(
            Encode(m[0][0] * r + m[0][1] * g + m[0][2] * b),
            Encode(m[1][0] * r + m[1][1] * g + m[1][2] * b),
            Encode(m[2][0] * r + m[2][1] * g + m[2][2] * b));

        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static byte Encode(double linear)
        {
            double v = Math.Clamp(linear, 0, 1);
            double s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
        }
    }
}
