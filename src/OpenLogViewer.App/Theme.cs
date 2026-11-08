using System.Windows.Media;

namespace OpenLogViewer.App;

public enum ThemeGroup
{
    Dark,
    Light,
    Editor,
    HighContrast,
    Retro,
}

/// <summary>How the tune table fills its cells.</summary>
public enum TableStyle
{
    /// <summary>Each cell shaded by its value on the theme's sequential ramp.</summary>
    Heat,

    /// <summary>Unfilled cells on a ruled grid, values in the readout ink.</summary>
    Grid,
}

/// <summary>
/// The part of a theme that is not colour: type, corners and how text is
/// rasterised. Kept apart from the colours so a scheme that only recolours
/// leaves it out and gets <see cref="Modern"/>, and a theme that changes the
/// application's character — a DOS-era screen, a print-like page — can do so
/// without the code that paints it knowing which theme is which.
/// </summary>
public sealed record ThemeStyle
{
    /// <summary>What every theme had before style was something a theme could say.</summary>
    public static ThemeStyle Modern { get; } = new();

    /// <summary>
    /// Labels, menus, buttons. A font family string as WPF reads one, so it
    /// may name a fallback after a comma and a bundled font by its location —
    /// see <see cref="ThemeFonts"/>.
    /// </summary>
    public string UiFont { get; init; } = "Segoe UI";

    /// <summary>Numbers: table cells, readouts, anything that lines up in columns.</summary>
    public string DataFont { get; init; } = "Consolas";

    /// <summary>
    /// Multiplies every corner radius. 0 squares the application off; 1 is the
    /// radii as drawn.
    /// </summary>
    public double CornerScale { get; init; } = 1;

    /// <summary>
    /// Rasterise text to whole pixels with no smoothing. A pixel font drawn the
    /// usual way is blurred across the pixel grid it was designed on, which
    /// loses exactly what it was chosen for.
    /// </summary>
    public bool PixelText { get; init; }

    public TableStyle Table { get; init; } = TableStyle.Heat;
}

/// <summary>
/// One colour scheme. A theme states only the colours that carry its identity —
/// its surfaces, its accent, and its trace palette — and derives the rest, so
/// hover states and heat-table ramps stay consistent with the surfaces they sit
/// on without every theme having to restate them.
///
/// <see cref="Series"/> is the one part that cannot be derived. Overlaid traces
/// have to stay separable from one another, including under the common forms of
/// colour-vision deficiency, so each theme's palette is snapped to steps that
/// pass that check rather than lifted verbatim from the scheme it is named
/// after. Editor schemes are built for syntax spans, which are never adjacent
/// and carry identity from position; traces are neither.
/// </summary>
public sealed record Theme
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ThemeGroup Group { get; init; }

    /// <summary>Plot and window ground.</summary>
    public required Color Background { get; init; }

    /// <summary>Sidebars, toolbar and status strip.</summary>
    public required Color Panel { get; init; }

    /// <summary>Raised surfaces: buttons, popups, chips.</summary>
    public required Color PanelAlt { get; init; }

    public required Color Line { get; init; }

    public required Color Text { get; init; }

    public required Color Muted { get; init; }

    public required Color Accent { get; init; }

    /// <summary>Log annotations and the highlight on traced-back samples.</summary>
    public required Color Marker { get; init; }

    /// <summary>Trace colours, handed out in order as channels are plotted.</summary>
    public required Color[] Series { get; init; }

    /// <summary>Hue for the heat table's sequential scale, and its below-target arm.</summary>
    public required Color RampCool { get; init; }

    /// <summary>Hue for the heat table's above-target arm.</summary>
    public required Color RampWarm { get; init; }

    /// <summary>Type, corners and text rendering. Recolouring themes leave it out.</summary>
    public ThemeStyle Style { get; init; } = ThemeStyle.Modern;

    // ----- optional colours -------------------------------------------------
    //
    // Each of these is derived unless a theme states it, so the schemes that
    // predate them look exactly as they did.

    private readonly Color? _readout;
    private readonly Color? _chrome;
    private readonly Color? _grid;

    /// <summary>
    /// Ink for live values and table cells, where a theme wants numbers to
    /// read apart from their labels. Body text unless stated.
    /// </summary>
    public Color Readout { get => _readout ?? Text; init => _readout = value; }

    /// <summary>
    /// The window's frame: title bar, menu bar and status strip. The panel
    /// colour unless stated, which is what keeps the seam under a coloured
    /// title bar from showing.
    /// </summary>
    public Color Chrome { get => _chrome ?? Panel; init => _chrome = value; }

    public bool IsDark => ColorMath.Luminance(Background) < 0.35;

    // ----- derived surfaces -------------------------------------------------

    public Color Hover => ColorMath.Blend(PanelAlt, Text, 0.14);

    /// <summary>Supporting text: a channel's range, a hint under a control.</summary>
    public Color Faint => ColorMath.Blend(Muted, Background, 0.35);

    /// <summary>
    /// Scrollbar thumb, pulled from the primary ink towards the panel. Not from
    /// <see cref="Muted"/>: a scheme whose muted ink is already close to its
    /// panel — a cream ground with warm grey secondary text — would leave the
    /// thumb barely there, and the thumb is the only part of the bar that is
    /// drawn at all.
    /// </summary>
    public Color Scroll => ColorMath.Blend(Text, Panel, 0.45);

    public Color ScrollHover => ColorMath.Blend(Text, Panel, 0.20);

    public Color Selected => ColorMath.Blend(Panel, Accent, 0.22);

    /// <summary>
    /// Ink for text sitting on top of <see cref="Accent"/>. Taken from the ends
    /// of the range rather than the theme's own ink: an accent close in
    /// lightness to both the background and the text would leave a hovered
    /// button's glyph unreadable whichever of the two it picked.
    /// </summary>
    public Color OnAccent =>
        ColorMath.Readable(Accent, Color.FromRgb(0x0B, 0x0B, 0x0B), Colors.White);

    /// <summary>Category headers in the channel list.</summary>
    public Color Header => ColorMath.Blend(Panel, Background, 0.5);

    /// <summary>Plot and chart rules. A quiet step off the ground unless stated.</summary>
    public Color Grid { get => _grid ?? ColorMath.Blend(Background, Text, 0.13); init => _grid = value; }

    /// <summary>Lane dividers in stacked mode — quieter than the grid.</summary>
    public Color Lane => ColorMath.Blend(Background, Text, 0.08);

    public Color Cursor => ColorMath.Blend(Text, Background, 0.25);

    /// <summary>Floating readout card, held just off the ground beneath it.</summary>
    public Color Card => ColorMath.Blend(Panel, Text, 0.04);

    public Color EmptyCell => ColorMath.Blend(Background, Text, 0.05);

    /// <summary>
    /// Status colours for gauge bands, at fixed hues rather than from the trace
    /// palette.
    ///
    /// A redline means one thing and has to mean it in every scheme, so these do
    /// not follow the theme's accent — only its polarity, taking the lightness
    /// that reads on the surface they sit on. Keeping them out of
    /// <see cref="Series"/> is deliberate: a trace colour that also means
    /// "danger" says the wrong thing whenever a channel happens to be handed it.
    /// </summary>
    public Color Warning => Status(IsDark ? 0.80 : 0.62, 0.15, 85);

    public Color Danger => Status(IsDark ? 0.68 : 0.55, 0.20, 27);

    /// <summary>The band a reading is in when it is where it should be.</summary>
    public Color Nominal => Status(IsDark ? 0.76 : 0.58, 0.13, 152);

    /// <summary>Hue in degrees, which is how OKLCH is written down; the maths wants radians.</summary>
    private static Color Status(double lightness, double chroma, double hueDegrees) =>
        ColorMath.FromOklch(lightness, chroma, hueDegrees * Math.PI / 180);

    public Color[] SequentialRamp => ColorMath.Ramp(RampCool, IsDark);

    public Color[] CoolArm => ColorMath.DivergingArm(RampCool, Background, IsDark);

    public Color[] WarmArm => ColorMath.DivergingArm(RampWarm, Background, IsDark);
}
