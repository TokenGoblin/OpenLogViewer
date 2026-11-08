using System.Windows.Media;
using OpenLogViewer.App;

namespace OpenLogViewer.Oracle;

/// <summary>
/// Every colour token of every theme, stated and derived. Theme.cs, ColorMath.cs and
/// ThemeCatalog.cs are compiled in as they ship (see the csproj), so these are the formulas the
/// application uses, not a copy of them.
/// </summary>
public sealed class W6Themes : IEmitter
{
    public string Package => "w6-ui-base";

    public string Set => "themes";

    public IReadOnlyList<object> Emit(EmitContext context) =>
        ThemeCatalog.Themes.Select(t => (object)new
        {
            id = t.Id,
            name = t.Name,
            group = t.Group.ToString(),
            stated = new
            {
                background = Hex(t.Background),
                panel = Hex(t.Panel),
                panel_alt = Hex(t.PanelAlt),
                line = Hex(t.Line),
                text = Hex(t.Text),
                muted = Hex(t.Muted),
                accent = Hex(t.Accent),
                marker = Hex(t.Marker),
                ramp_cool = Hex(t.RampCool),
                ramp_warm = Hex(t.RampWarm),
                series = t.Series.Select(Hex).ToArray(),
            },
            style = new
            {
                ui_font_is_retro = t.Style.UiFont == ThemeFonts.Retro,
                data_font_is_retro = t.Style.DataFont == ThemeFonts.Retro,
                corner_scale = Exact.Bits(t.Style.CornerScale),
                pixel_text = t.Style.PixelText,
                table = t.Style.Table.ToString(),
            },
            derived = new
            {
                is_dark = t.IsDark,
                readout = Hex(t.Readout),
                chrome = Hex(t.Chrome),
                grid = Hex(t.Grid),
                hover = Hex(t.Hover),
                faint = Hex(t.Faint),
                scroll = Hex(t.Scroll),
                scroll_hover = Hex(t.ScrollHover),
                selected = Hex(t.Selected),
                on_accent = Hex(t.OnAccent),
                header = Hex(t.Header),
                lane = Hex(t.Lane),
                cursor = Hex(t.Cursor),
                card = Hex(t.Card),
                empty_cell = Hex(t.EmptyCell),
                warning = Hex(t.Warning),
                danger = Hex(t.Danger),
                nominal = Hex(t.Nominal),
                sequential_ramp = t.SequentialRamp.Select(Hex).ToArray(),
                cool_arm = t.CoolArm.Select(Hex).ToArray(),
                warm_arm = t.WarmArm.Select(Hex).ToArray(),
            },
        }).ToList();

    internal static string Hex(Color c) => c.ToString();
}

/// <summary>ColorMath over a spread of colours and parameters, including the awkward ones.</summary>
public sealed class W6ColorMath : IEmitter
{
    public string Package => "w6-ui-base";

    public string Set => "color_math";

    private static readonly string[] Colours =
    [
        "#000000", "#FFFFFF", "#808080", "#FF0000", "#00FF00", "#0000FF", "#14171C", "#DDE3EA", "#4FC3F7",
        "#FFB34D", "#3987E5", "#D03B3B", "#2E8B57", "#8A2BE2", "#DCDCDC", "#FBF7EF", "#A85A16", "#010203",
        "#FEFDFC", "#7F7F7F", "#808081", "#55FF55", "#0000AA", "#FF40FF",
    ];

    private static readonly double[] Ts =
        [-1, 0, 0.0001, 0.07, 0.08, 0.13, 0.14, 0.18, 0.2, 0.22, 0.25, 0.35, 0.45, 0.5, 0.95, 1, 2];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();
        Color[] colours = Colours.Select(h => (Color)ColorConverter.ConvertFromString(h)!).ToArray();

        // Pairs are drawn from the first fourteen colours only, to keep the file small.
        Color[] few = colours.Take(14).ToArray();

        // Blend: every ordered pair at every ratio.
        foreach (Color a in few)
        {
            foreach (Color b in few)
            {
                cases.Add(new
                {
                    kind = "blend",
                    a = W6Themes.Hex(a),
                    b = W6Themes.Hex(b),
                    results = Ts.Select(t => new { t = Exact.Bits(t), result = W6Themes.Hex(ColorMath.Blend(a, b, t)) }).ToArray(),
                });
            }
        }

        // Per colour: luminance, OKLab, OKLCH, the ramps on both polarities, and the round trip.
        foreach (Color c in colours)
        {
            (double l, double a, double b) = ColorMath.ToOklab(c);
            (double cl, double cc, double ch) = ColorMath.ToOklch(c);
            cases.Add(new
            {
                kind = "colour",
                colour = W6Themes.Hex(c),
                luminance = Exact.Bits(ColorMath.Luminance(c)),
                oklab = new[] { Exact.Bits(l), Exact.Bits(a), Exact.Bits(b) },
                oklch = new[] { Exact.Bits(cl), Exact.Bits(cc), Exact.Bits(ch) },
                round_trip = W6Themes.Hex(ColorMath.FromOklab(l, a, b)),
                ramp_dark = ColorMath.Ramp(c, true).Select(W6Themes.Hex).ToArray(),
                ramp_light = ColorMath.Ramp(c, false).Select(W6Themes.Hex).ToArray(),
                ramp_one_step = ColorMath.Ramp(c, true, 1).Select(W6Themes.Hex).ToArray(),
                arm_dark = ColorMath.DivergingArm(c, Colors.Black, true).Select(W6Themes.Hex).ToArray(),
                arm_light = ColorMath.DivergingArm(c, Colors.White, false).Select(W6Themes.Hex).ToArray(),
            });
        }

        // Contrast and Readable over every pair.
        foreach (Color a in few)
        {
            foreach (Color b in few)
            {
                cases.Add(new
                {
                    kind = "contrast",
                    a = W6Themes.Hex(a),
                    b = W6Themes.Hex(b),
                    contrast = Exact.Bits(ColorMath.Contrast(a, b)),
                    readable = W6Themes.Hex(ColorMath.Readable(a, b, Color.FromRgb(0x0B, 0x0B, 0x0B))),
                });
            }
        }

        // The status colours' inputs, in and out of gamut.
        foreach (double l in new[] { 0.55, 0.62, 0.68, 0.76, 0.8, 0.58 })
        {
            foreach (double chroma in new[] { 0.0, 0.13, 0.15, 0.20, 0.4 })
            {
                foreach (double hue in new[] { 27.0, 85.0, 152.0, 0.0, 300.0 })
                {
                    cases.Add(new
                    {
                        kind = "oklch",
                        lightness = Exact.Bits(l),
                        chroma = Exact.Bits(chroma),
                        hue_degrees = Exact.Bits(hue),
                        result = W6Themes.Hex(ColorMath.FromOklch(l, chroma, hue * Math.PI / 180)),
                    });
                }
            }
        }

        return cases;
    }
}
