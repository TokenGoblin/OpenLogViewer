//! Every theme's stated and derived colour tokens, and the colour maths under them, against the
//! C# (rust/oracle/expected/w6-ui-base). `tools/OpenLogViewer.Oracle/Emitters/W6Themes.cs` links
//! the shipping `Theme.cs`, `ColorMath.cs` and `ThemeCatalog.cs` as they are, so these are the
//! application's own formulas. Colours are compared byte for byte.

use olv::ui::color_math::{self as cm, Color};
use olv::ui::theme::{Theme, ThemeGroup};
use olv::ui::theme_fonts::ThemeFont;
use olv::ui::{TableStyle, ThemeCatalog};
use serde::Deserialize;

fn bits(hex: &str) -> f64 {
    f64::from_bits(u64::from_str_radix(hex, 16).expect("hex bits"))
}

fn hex(c: Color) -> String {
    c.to_string()
}

fn hexes(colours: &[Color]) -> Vec<String> {
    colours.iter().map(|c| hex(*c)).collect()
}

#[derive(Deserialize)]
struct ThemeCase {
    id: String,
    name: String,
    group: String,
    stated: Stated,
    style: Style,
    derived: Derived,
}

#[derive(Deserialize)]
struct Stated {
    background: String,
    panel: String,
    panel_alt: String,
    line: String,
    text: String,
    muted: String,
    accent: String,
    marker: String,
    ramp_cool: String,
    ramp_warm: String,
    series: Vec<String>,
}

#[derive(Deserialize)]
struct Style {
    ui_font_is_retro: bool,
    data_font_is_retro: bool,
    corner_scale: String,
    pixel_text: bool,
    table: String,
}

#[derive(Deserialize)]
struct Derived {
    is_dark: bool,
    readout: String,
    chrome: String,
    grid: String,
    hover: String,
    faint: String,
    scroll: String,
    scroll_hover: String,
    selected: String,
    on_accent: String,
    header: String,
    lane: String,
    cursor: String,
    card: String,
    empty_cell: String,
    warning: String,
    danger: String,
    nominal: String,
    sequential_ramp: Vec<String>,
    cool_arm: Vec<String>,
    warm_arm: Vec<String>,
}

fn group_name(group: ThemeGroup) -> &'static str {
    match group {
        ThemeGroup::Dark => "Dark",
        ThemeGroup::Light => "Light",
        ThemeGroup::Editor => "Editor",
        ThemeGroup::HighContrast => "HighContrast",
        ThemeGroup::Retro => "Retro",
    }
}

#[test]
fn the_catalog_is_the_same_fifteen_themes_in_the_same_order() {
    let cases: Vec<ThemeCase> = olv_oracle::cases("w6-ui-base", "themes");

    let ours: Vec<&str> = ThemeCatalog::themes().iter().map(|t| t.id).collect();
    let theirs: Vec<&str> = cases.iter().map(|c| c.id.as_str()).collect();
    assert_eq!(15, theirs.len());
    assert_eq!(theirs, ours);
}

#[test]
fn every_stated_colour_and_the_style_match_the_csharp() {
    let cases: Vec<ThemeCase> = olv_oracle::cases("w6-ui-base", "themes");

    for case in &cases {
        let t: &Theme = ThemeCatalog::find(Some(&case.id));
        let what = &case.id;
        let s = &case.stated;

        assert_eq!(case.name, t.name, "{what}");
        assert_eq!(case.group, group_name(t.group), "{what}");
        assert_eq!(s.background, hex(t.background), "{what} background");
        assert_eq!(s.panel, hex(t.panel), "{what} panel");
        assert_eq!(s.panel_alt, hex(t.panel_alt), "{what} panel_alt");
        assert_eq!(s.line, hex(t.line), "{what} line");
        assert_eq!(s.text, hex(t.text), "{what} text");
        assert_eq!(s.muted, hex(t.muted), "{what} muted");
        assert_eq!(s.accent, hex(t.accent), "{what} accent");
        assert_eq!(s.marker, hex(t.marker), "{what} marker");
        assert_eq!(s.ramp_cool, hex(t.ramp_cool), "{what} ramp_cool");
        assert_eq!(s.ramp_warm, hex(t.ramp_warm), "{what} ramp_warm");
        assert_eq!(s.series, hexes(&t.series), "{what} series");

        let style = &case.style;
        assert_eq!(style.ui_font_is_retro, t.style.ui_font == ThemeFont::Retro, "{what}");
        assert_eq!(
            style.data_font_is_retro,
            t.style.data_font == ThemeFont::Retro,
            "{what}"
        );
        assert_eq!(bits(&style.corner_scale), t.style.corner_scale, "{what} corner_scale");
        assert_eq!(style.pixel_text, t.style.pixel_text, "{what}");
        assert_eq!(
            style.table,
            match t.style.table {
                TableStyle::Heat => "Heat",
                TableStyle::Grid => "Grid",
            },
            "{what}"
        );
    }
}

#[test]
fn every_derived_token_of_every_theme_matches_the_csharp_byte_for_byte() {
    let cases: Vec<ThemeCase> = olv_oracle::cases("w6-ui-base", "themes");
    let mut compared = 0;

    for case in &cases {
        let t = ThemeCatalog::find(Some(&case.id));
        let what = &case.id;
        let d = &case.derived;

        assert_eq!(d.is_dark, t.is_dark(), "{what} is_dark");
        for (name, expected, actual) in [
            ("readout", &d.readout, t.readout()),
            ("chrome", &d.chrome, t.chrome()),
            ("grid", &d.grid, t.grid()),
            ("hover", &d.hover, t.hover()),
            ("faint", &d.faint, t.faint()),
            ("scroll", &d.scroll, t.scroll()),
            ("scroll_hover", &d.scroll_hover, t.scroll_hover()),
            ("selected", &d.selected, t.selected()),
            ("on_accent", &d.on_accent, t.on_accent()),
            ("header", &d.header, t.header()),
            ("lane", &d.lane, t.lane()),
            ("cursor", &d.cursor, t.cursor()),
            ("card", &d.card, t.card()),
            ("empty_cell", &d.empty_cell, t.empty_cell()),
            ("warning", &d.warning, t.warning()),
            ("danger", &d.danger, t.danger()),
            ("nominal", &d.nominal, t.nominal()),
        ] {
            assert_eq!(*expected, hex(actual), "{what} {name}");
            compared += 1;
        }

        assert_eq!(d.sequential_ramp, hexes(&t.sequential_ramp()), "{what} sequential_ramp");
        assert_eq!(d.cool_arm, hexes(&t.cool_arm()), "{what} cool_arm");
        assert_eq!(d.warm_arm, hexes(&t.warm_arm()), "{what} warm_arm");
        compared += 3;
    }

    assert_eq!(15 * 20, compared);
}

#[derive(Deserialize)]
struct MathCase {
    kind: String,
    // blend and contrast
    a: Option<String>,
    b: Option<String>,
    results: Option<Vec<BlendResult>>,
    contrast: Option<String>,
    readable: Option<String>,
    // colour
    colour: Option<String>,
    luminance: Option<String>,
    oklab: Option<Vec<String>>,
    oklch: Option<Vec<String>>,
    round_trip: Option<String>,
    ramp_dark: Option<Vec<String>>,
    ramp_light: Option<Vec<String>>,
    ramp_one_step: Option<Vec<String>>,
    arm_dark: Option<Vec<String>>,
    arm_light: Option<Vec<String>>,
    // oklch
    lightness: Option<String>,
    chroma: Option<String>,
    hue_degrees: Option<String>,
    result: Option<String>,
}

#[derive(Deserialize)]
struct BlendResult {
    t: String,
    result: String,
}

fn c(text: &Option<String>) -> Color {
    Color::parse(text.as_deref().expect("a colour")).expect("a valid colour")
}

#[test]
fn the_colour_maths_matches_the_csharp() {
    let cases: Vec<MathCase> = olv_oracle::cases("w6-ui-base", "color_math");
    let mut kinds = std::collections::BTreeMap::<String, usize>::new();

    for case in &cases {
        *kinds.entry(case.kind.clone()).or_default() += 1;

        match case.kind.as_str() {
            "blend" => {
                let (a, b) = (c(&case.a), c(&case.b));
                for r in case.results.as_ref().unwrap() {
                    assert_eq!(
                        r.result,
                        hex(cm::blend(a, b, bits(&r.t))),
                        "blend {a} {b} {}",
                        bits(&r.t)
                    );
                }
            }
            "contrast" => {
                let (a, b) = (c(&case.a), c(&case.b));
                olv_oracle::assert_close(
                    cm::contrast(a, b),
                    bits(case.contrast.as_ref().unwrap()),
                    &format!("contrast {a} {b}"),
                );
                assert_eq!(
                    *case.readable.as_ref().unwrap(),
                    hex(cm::readable(a, b, Color::rgb(0x0B, 0x0B, 0x0B))),
                    "readable {a} {b}"
                );
            }
            "colour" => {
                let col = c(&case.colour);
                olv_oracle::assert_close(cm::luminance(col), bits(case.luminance.as_ref().unwrap()), "luminance");

                let (l, a, b) = cm::to_oklab(col);
                for (got, want, name) in zip3([l, a, b], case.oklab.as_ref().unwrap(), ["L", "a", "b"]) {
                    olv_oracle::assert_close(got, want, &format!("oklab {name} of {col}"));
                }
                let (cl, cc, ch) = cm::to_oklch(col);
                for (got, want, name) in zip3([cl, cc, ch], case.oklch.as_ref().unwrap(), ["L", "C", "H"]) {
                    olv_oracle::assert_close(got, want, &format!("oklch {name} of {col}"));
                }

                assert_eq!(
                    *case.round_trip.as_ref().unwrap(),
                    hex(cm::from_oklab(l, a, b)),
                    "round trip {col}"
                );
                assert_eq!(
                    *case.ramp_dark.as_ref().unwrap(),
                    hexes(&cm::ramp(col, true, 5)),
                    "ramp dark {col}"
                );
                assert_eq!(
                    *case.ramp_light.as_ref().unwrap(),
                    hexes(&cm::ramp(col, false, 5)),
                    "ramp light {col}"
                );
                assert_eq!(
                    *case.ramp_one_step.as_ref().unwrap(),
                    hexes(&cm::ramp(col, true, 1)),
                    "ramp 1 {col}"
                );
                assert_eq!(
                    *case.arm_dark.as_ref().unwrap(),
                    hexes(&cm::diverging_arm(col, Color::BLACK, true)),
                    "arm dark {col}"
                );
                assert_eq!(
                    *case.arm_light.as_ref().unwrap(),
                    hexes(&cm::diverging_arm(col, Color::WHITE, false)),
                    "arm light {col}"
                );
            }
            "oklch" => {
                let hue = bits(case.hue_degrees.as_ref().unwrap()) * std::f64::consts::PI / 180.0;
                let got = cm::from_oklch(
                    bits(case.lightness.as_ref().unwrap()),
                    bits(case.chroma.as_ref().unwrap()),
                    hue,
                );
                assert_eq!(*case.result.as_ref().unwrap(), hex(got));
            }
            other => panic!("unknown case kind {other}"),
        }
    }

    // Guard against a emitter that quietly stops emitting a kind.
    assert!(kinds["blend"] >= 100 && kinds["colour"] >= 20 && kinds["contrast"] >= 100 && kinds["oklch"] >= 100);
}

fn zip3<'a>(got: [f64; 3], want: &'a [String], names: [&'a str; 3]) -> impl Iterator<Item = (f64, f64, &'a str)> + 'a {
    (0..3).map(move |i| (got[i], bits(&want[i]), names[i]))
}
