using System.Windows.Media;

namespace OpenLogViewer.App;

/// <summary>
/// Perceptual colour helpers, in OKLab. Themes specify only a handful of anchor
/// colours; the rest of a theme's surfaces and its heat-table ramps are derived
/// here, so a new theme cannot be internally inconsistent.
///
/// OKLab rather than HSL because lightness has to mean the same thing across
/// hues: a ramp built by stepping HSL lightness looks even in yellow and
/// crushed in blue, which is exactly the failure a sequential scale must not
/// have.
/// </summary>
public static class ColorMath
{
    /// <summary>Mixes two colours, <paramref name="t"/> = 0 gives <paramref name="a"/>.</summary>
    public static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(Color c) =>
        0.2126 * ToLinear(c.R) + 0.7152 * ToLinear(c.G) + 0.0722 * ToLinear(c.B);

    public static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Whichever of the two candidates reads more clearly on <paramref name="on"/>.</summary>
    public static Color Readable(Color on, Color first, Color second) =>
        Contrast(on, first) >= Contrast(on, second) ? first : second;

    /// <summary>
    /// A sequential ramp in the anchor's hue, running from least to most visible
    /// against the surface: dark and saturated up to pale on a dark theme, pale
    /// down to deep on a light one. Magnitude is carried by lightness, which
    /// survives colour-vision deficiency and greyscale printing.
    /// </summary>
    /// <summary>
    /// The dark end does not run all the way down. A blue hue at low perceptual
    /// lightness carries very little luminance — blue is 7% of it — so a ramp
    /// starting lower would put its first step level with an empty cell, and
    /// "never visited" would look like "visited once".
    /// </summary>
    public static Color[] Ramp(Color anchor, bool onDark, int steps = 5) =>
        Build(anchor, steps, onDark ? 0.50 : 0.78, onDark ? 0.88 : 0.36, 1.00, 0.45);

    /// <summary>
    /// A diverging arm: the neutral midpoint, then the ramp.
    ///
    /// Deliberately not the sequential ramp with a midpoint bolted on. A
    /// sequential scale carries magnitude in lightness alone, so it runs the
    /// full range and can shed chroma at the far end; on a diverging scale that
    /// far end is the largest error, and two arms that both fade towards the
    /// same near-white would make the worst rich cell and the worst lean cell
    /// look alike. The arms therefore travel less in lightness and hold their
    /// hue, which is what carries polarity.
    /// </summary>
    public static Color[] DivergingArm(Color anchor, Color surface, bool onDark)
    {
        Color[] ramp = Build(anchor, 5, onDark ? 0.47 : 0.72, onDark ? 0.76 : 0.43, 1.00, 0.85);

        var arm = new Color[ramp.Length + 1];

        // Near the surface, so cells on target recede rather than competing with
        // the cells that are off it.
        arm[0] = Blend(surface, onDark ? Colors.White : Colors.Black, 0.18);
        Array.Copy(ramp, 0, arm, 1, ramp.Length);
        return arm;
    }

    /// <summary>
    /// Steps a hue between two lightnesses, scaling chroma from
    /// <paramref name="fromChroma"/> to <paramref name="toChroma"/> as a
    /// fraction of the anchor's own. Chroma has to fall away as a step
    /// approaches the surface, or the step leaves the sRGB gamut and clips flat
    /// against its neighbour.
    /// </summary>
    private static Color[] Build(
        Color anchor, int steps, double startL, double endL, double fromChroma, double toChroma)
    {
        (_, double chroma, double hue) = ToOklch(anchor);

        // Floored so a washed-out anchor still yields identifiable steps, and
        // capped where the pale end would otherwise clip.
        chroma = Math.Clamp(chroma, 0.09, 0.20);

        var ramp = new Color[steps];
        for (int i = 0; i < steps; i++)
        {
            double t = steps == 1 ? 0 : (double)i / (steps - 1);
            ramp[i] = FromOklch(
                startL + (endL - startL) * t,
                chroma * (fromChroma + (toChroma - fromChroma) * t),
                hue);
        }
        return ramp;
    }

    // ----- OKLab ------------------------------------------------------------

    public static (double L, double C, double H) ToOklch(Color c)
    {
        (double l, double a, double b) = ToOklab(c);
        return (l, Math.Sqrt(a * a + b * b), Math.Atan2(b, a));
    }

    public static (double L, double A, double B) ToOklab(Color c)
    {
        double r = ToLinear(c.R), g = ToLinear(c.G), bl = ToLinear(c.B);

        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bl);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bl);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bl);

        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>Clipped into sRGB, so an out-of-gamut request still returns the right hue.</summary>
    public static Color FromOklch(double lightness, double chroma, double hue) =>
        FromOklab(lightness, chroma * Math.Cos(hue), chroma * Math.Sin(hue));

    public static Color FromOklab(double lightness, double a, double b)
    {
        double l = Cube(lightness + 0.3963377774 * a + 0.2158037573 * b);
        double m = Cube(lightness - 0.1055613458 * a - 0.0638541728 * b);
        double s = Cube(lightness - 0.0894841775 * a - 1.2914855480 * b);

        return Color.FromRgb(
            ToByte(+4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
            ToByte(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
            ToByte(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s));

        static double Cube(double v) => v * v * v;
    }

    private static double ToLinear(byte v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static byte ToByte(double linear)
    {
        double c = Math.Clamp(linear, 0, 1);
        double s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
    }
}
