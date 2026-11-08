//! Ported from `Theme.cs`. Owned by package `w6-ui-base`.
//!
//! One colour scheme. A theme states only the colours that carry its identity (its surfaces, its
//! accent, and its trace palette) and derives the rest, so hover states and heat-table ramps stay
//! consistent with the surfaces they sit on without every theme having to restate them.
//!
//! `series` is the one part that cannot be derived. Overlaid traces have to stay separable from
//! one another, including under the common forms of colour-vision deficiency, so each theme's
//! palette is snapped to steps that pass that check rather than lifted verbatim from the scheme it
//! is named after. Editor schemes are built for syntax spans, which are never adjacent and carry
//! identity from position; traces are neither.

use super::color_math::{self as cm, Color};
use super::theme_fonts::ThemeFont;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum ThemeGroup {
    Dark,
    Light,
    Editor,
    HighContrast,
    Retro,
}

impl ThemeGroup {
    pub const ALL: [ThemeGroup; 5] = [
        ThemeGroup::Dark,
        ThemeGroup::Light,
        ThemeGroup::Editor,
        ThemeGroup::HighContrast,
        ThemeGroup::Retro,
    ];
}

/// How the tune table fills its cells.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum TableStyle {
    /// Each cell shaded by its value on the theme's sequential ramp.
    Heat,
    /// Unfilled cells on a ruled grid, values in the readout ink.
    Grid,
}

/// The part of a theme that is not colour: type, corners and how text is rasterised. Kept apart
/// from the colours so a scheme that only recolours leaves it out and gets [`ThemeStyle::MODERN`],
/// and a theme that changes the application's character (a DOS-era screen, a print-like page) can
/// do so without the code that paints it knowing which theme is which.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct ThemeStyle {
    /// Labels, menus, buttons.
    pub ui_font: ThemeFont,
    /// Numbers: table cells, readouts, anything that lines up in columns.
    pub data_font: ThemeFont,
    /// Multiplies every corner radius. 0 squares the application off; 1 is the radii as drawn.
    pub corner_scale: f64,
    /// Rasterise text to whole pixels with no smoothing. A pixel font drawn the usual way is
    /// blurred across the pixel grid it was designed on, which loses exactly what it was chosen
    /// for.
    pub pixel_text: bool,
    pub table: TableStyle,
}

impl ThemeStyle {
    /// What every theme had before style was something a theme could say.
    pub const MODERN: ThemeStyle = ThemeStyle {
        ui_font: ThemeFont::Standard,
        data_font: ThemeFont::Standard,
        corner_scale: 1.0,
        pixel_text: false,
        table: TableStyle::Heat,
    };
}

impl Default for ThemeStyle {
    fn default() -> Self {
        Self::MODERN
    }
}

/// The corner radii the interface draws with, by name. A widget names the size it wants and the
/// theme decides what that size is, so a square theme does not have to restate every border.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Corner {
    /// Chips, combo boxes, jump buttons, scroll thumbs.
    Small,
    /// Buttons, toolbar toggles, menu rows.
    Normal,
    /// Available.
    Medium,
    /// Menus and context menus.
    Large,
    /// Saved-selection (preset) chips.
    Pill,
}

impl Corner {
    pub const ALL: [Corner; 5] = [
        Corner::Small,
        Corner::Normal,
        Corner::Medium,
        Corner::Large,
        Corner::Pill,
    ];

    /// The radius in design pixels, before the theme's `corner_scale` and the interface zoom.
    ///
    /// OPEN-QUESTION: 4.4. The C# leaves a few self-drawn radii (plot readout card 4, its hovered
    /// row 3, gauge face 8) out of this table, so they stay round in Retro. Screens that draw those
    /// pick one of the two behaviours; nothing here forces either.
    pub const fn design_radius(self) -> f64 {
        match self {
            Corner::Small => 3.0,
            Corner::Normal => 4.0,
            Corner::Medium => 5.0,
            Corner::Large => 6.0,
            Corner::Pill => 9.0,
        }
    }
}

#[derive(Clone, PartialEq, Debug)]
pub struct Theme {
    /// Stable lowercase id, stored in settings and accepted by `--theme`.
    pub id: &'static str,
    pub name: &'static str,
    pub group: ThemeGroup,
    /// Plot and window ground.
    pub background: Color,
    /// Sidebars, toolbar and status strip.
    pub panel: Color,
    /// Raised surfaces: buttons, popups, chips.
    pub panel_alt: Color,
    pub line: Color,
    pub text: Color,
    pub muted: Color,
    pub accent: Color,
    /// Log annotations and the highlight on traced-back samples.
    pub marker: Color,
    /// Trace colours, handed out in order as channels are plotted.
    pub series: [Color; 8],
    /// Hue for the heat table's sequential scale, and its below-target arm.
    pub ramp_cool: Color,
    /// Hue for the heat table's above-target arm.
    pub ramp_warm: Color,
    /// Type, corners and text rendering. Recolouring themes leave it out.
    pub style: ThemeStyle,
    /// Stated only by a theme that wants numbers to read apart from their labels; see
    /// [`Theme::readout`].
    pub readout_override: Option<Color>,
    /// Stated only by a theme that frames the window in something other than the panel colour;
    /// see [`Theme::chrome`].
    pub chrome_override: Option<Color>,
    /// Stated only by a theme that wants its plot rules a particular colour; see
    /// [`Theme::grid`].
    pub grid_override: Option<Color>,
}

impl Theme {
    // ----- optional colours -------------------------------------------------
    //
    // Each of these is derived unless a theme states it, so the schemes that predate them look
    // exactly as they did.

    /// Ink for live values and table cells, where a theme wants numbers to read apart from their
    /// labels. Body text unless stated.
    pub fn readout(&self) -> Color {
        self.readout_override.unwrap_or(self.text)
    }

    /// The window's frame: title bar, menu bar and status strip. The panel colour unless stated,
    /// which is what keeps the seam under a coloured title bar from showing.
    pub fn chrome(&self) -> Color {
        self.chrome_override.unwrap_or(self.panel)
    }

    /// Plot and chart rules. A quiet step off the ground unless stated.
    pub fn grid(&self) -> Color {
        self.grid_override
            .unwrap_or_else(|| cm::blend(self.background, self.text, 0.13))
    }

    pub fn is_dark(&self) -> bool {
        cm::luminance(self.background) < 0.35
    }

    // ----- derived surfaces -------------------------------------------------

    pub fn hover(&self) -> Color {
        cm::blend(self.panel_alt, self.text, 0.14)
    }

    /// Supporting text: a channel's range, a hint under a control.
    pub fn faint(&self) -> Color {
        cm::blend(self.muted, self.background, 0.35)
    }

    /// Scrollbar thumb, pulled from the primary ink towards the panel. Not from `muted`: a scheme
    /// whose muted ink is already close to its panel (a cream ground with warm grey secondary
    /// text) would leave the thumb barely there, and the thumb is the only part of the bar that
    /// is drawn at all.
    pub fn scroll(&self) -> Color {
        cm::blend(self.text, self.panel, 0.45)
    }

    pub fn scroll_hover(&self) -> Color {
        cm::blend(self.text, self.panel, 0.20)
    }

    pub fn selected(&self) -> Color {
        cm::blend(self.panel, self.accent, 0.22)
    }

    /// Ink for text sitting on top of `accent`. Taken from the ends of the range rather than the
    /// theme's own ink: an accent close in lightness to both the background and the text would
    /// leave a hovered button's glyph unreadable whichever of the two it picked.
    pub fn on_accent(&self) -> Color {
        cm::readable(self.accent, Color::rgb(0x0B, 0x0B, 0x0B), Color::WHITE)
    }

    /// Category headers in the channel list.
    pub fn header(&self) -> Color {
        cm::blend(self.panel, self.background, 0.5)
    }

    /// Lane dividers in stacked mode: quieter than the grid.
    pub fn lane(&self) -> Color {
        cm::blend(self.background, self.text, 0.08)
    }

    pub fn cursor(&self) -> Color {
        cm::blend(self.text, self.background, 0.25)
    }

    /// Floating readout card, held just off the ground beneath it.
    pub fn card(&self) -> Color {
        cm::blend(self.panel, self.text, 0.04)
    }

    pub fn empty_cell(&self) -> Color {
        cm::blend(self.background, self.text, 0.05)
    }

    /// Status colours for gauge bands, at fixed hues rather than from the trace palette.
    ///
    /// A redline means one thing and has to mean it in every scheme, so these do not follow the
    /// theme's accent, only its polarity, taking the lightness that reads on the surface they sit
    /// on. Keeping them out of `series` is deliberate: a trace colour that also means "danger"
    /// says the wrong thing whenever a channel happens to be handed it.
    pub fn warning(&self) -> Color {
        Self::status(if self.is_dark() { 0.80 } else { 0.62 }, 0.15, 85.0)
    }

    pub fn danger(&self) -> Color {
        Self::status(if self.is_dark() { 0.68 } else { 0.55 }, 0.20, 27.0)
    }

    /// The band a reading is in when it is where it should be.
    pub fn nominal(&self) -> Color {
        Self::status(if self.is_dark() { 0.76 } else { 0.58 }, 0.13, 152.0)
    }

    /// Hue in degrees, which is how OKLCH is written down; the maths wants radians.
    fn status(lightness: f64, chroma: f64, hue_degrees: f64) -> Color {
        cm::from_oklch(lightness, chroma, hue_degrees * std::f64::consts::PI / 180.0)
    }

    pub fn sequential_ramp(&self) -> Vec<Color> {
        cm::ramp(self.ramp_cool, self.is_dark(), 5)
    }

    pub fn cool_arm(&self) -> Vec<Color> {
        cm::diverging_arm(self.ramp_cool, self.background, self.is_dark())
    }

    pub fn warm_arm(&self) -> Vec<Color> {
        cm::diverging_arm(self.ramp_warm, self.background, self.is_dark())
    }

    /// A corner radius in design pixels (before the interface zoom), scaled by this theme's
    /// style: 0 for a theme that squares the application off.
    pub fn radius(&self, corner: Corner) -> f64 {
        corner.design_radius() * self.style.corner_scale
    }
}

#[cfg(test)]
mod tests {
    use super::super::theme_catalog::{DEFAULT_ID, ThemeCatalog};
    use super::*;

    fn get(id: &str) -> &'static Theme {
        ThemeCatalog::find(Some(id))
    }

    /// The C# tests are parameterised over every theme; here each body runs for each theme.
    fn for_every_theme(check: impl Fn(&'static Theme)) {
        for theme in ThemeCatalog::themes() {
            check(theme);
        }
    }

    #[test]
    fn the_catalog_offers_a_dozen_schemes_across_every_group() {
        assert!(ThemeCatalog::themes().len() >= 12);

        for group in ThemeGroup::ALL {
            assert!(ThemeCatalog::themes().iter().any(|t| t.group == group), "{group:?}");
        }
    }

    #[test]
    fn theme_ids_are_unique_and_stable() {
        // Ids are written into settings.json, so a duplicate would make the stored preference
        // ambiguous.
        let mut ids: Vec<String> = ThemeCatalog::themes()
            .iter()
            .map(|t| t.id.to_ascii_lowercase())
            .collect();
        let count = ids.len();
        ids.sort();
        ids.dedup();
        assert_eq!(count, ids.len());
    }

    #[test]
    fn an_unknown_or_missing_id_falls_back_to_the_default() {
        // A hand-edited settings file must not stop the app starting.
        assert_eq!(DEFAULT_ID, ThemeCatalog::find(Some("no-such-theme")).id);
        assert_eq!(DEFAULT_ID, ThemeCatalog::find(None).id);
        assert_eq!(DEFAULT_ID, ThemeCatalog::find(Some("")).id);
    }

    #[test]
    fn every_theme_offers_eight_trace_colours() {
        for_every_theme(|t| assert_eq!(8, t.series.len(), "{}", t.id));
    }

    #[test]
    fn trace_colours_clear_their_own_background() {
        // 3:1 is the floor for a graphical object to be perceivable. A trace below it against the
        // ground it is drawn on is simply not visible.
        for_every_theme(|theme| {
            for c in theme.series {
                let ratio = cm::contrast(c, theme.background);
                assert!(ratio >= 3.0, "{}: {c} holds only {ratio:.2}:1", theme.id);
            }
        });
    }

    #[test]
    fn adjacent_trace_colours_stay_apart_under_colour_vision_deficiency() {
        // Colours are handed out in order, so consecutive entries are the pair most likely to be
        // on screen together. 6.0 is the floor the palettes were snapped to; the app carries
        // direct labels and a sidebar swatch as the secondary encoding that floor assumes.
        for_every_theme(|theme| {
            for i in 1..theme.series.len() {
                let gap = cvd_separation(theme.series[i - 1], theme.series[i]);
                assert!(
                    gap >= 6.0,
                    "{}: entries {} and {i} differ by only {gap:.1}",
                    theme.id,
                    i - 1
                );
            }
        });
    }

    #[test]
    fn no_trace_colour_reads_as_grey() {
        // Below this chroma a hue stops being identifiable as a colour at all, and two such
        // traces are told apart only by lightness.
        for_every_theme(|theme| {
            for c in theme.series {
                assert!(cm::to_oklch(c).1 >= 0.10, "{}: {c} is nearly grey", theme.id);
            }
        });
    }

    #[test]
    fn body_text_is_readable_on_every_surface() {
        for_every_theme(|theme| {
            assert!(cm::contrast(theme.text, theme.background) >= 4.5, "{}", theme.id);
            assert!(cm::contrast(theme.text, theme.panel) >= 4.5, "{}", theme.id);
            assert!(cm::contrast(theme.text, theme.panel_alt) >= 4.5, "{}", theme.id);

            // Supporting text may be quieter, but still has to be legible.
            assert!(cm::contrast(theme.muted, theme.panel) >= 3.0, "{}", theme.id);
        });
    }

    #[test]
    fn the_scrollbar_thumb_is_visible_on_the_surfaces_it_sits_on() {
        // The thumb is the only part of the bar that is drawn (the track is transparent), so if
        // it does not separate from the panel there is no scrollbar at all.
        for_every_theme(|theme| {
            let on_panel = cm::contrast(theme.scroll(), theme.panel);
            let in_popup = cm::contrast(theme.scroll(), theme.panel_alt);
            assert!(
                on_panel >= 2.5,
                "{}: thumb holds only {on_panel:.2}:1 on the panel",
                theme.id
            );
            assert!(
                in_popup >= 2.0,
                "{}: thumb holds only {in_popup:.2}:1 in a popup",
                theme.id
            );
        });
    }

    #[test]
    fn the_scrollbar_responds_visibly_to_hover() {
        // A hover state that looked the same would make the bar feel dead.
        for_every_theme(|theme| {
            assert!(
                cm::contrast(theme.scroll_hover(), theme.panel) > cm::contrast(theme.scroll(), theme.panel),
                "{}",
                theme.id
            );
        });
    }

    #[test]
    fn ink_on_the_accent_is_readable() {
        // The jump buttons put text on the accent when hovered; picking the wrong end would leave
        // the glyph invisible at the moment it is aimed at.
        for_every_theme(|theme| {
            assert!(cm::contrast(theme.on_accent(), theme.accent) >= 4.5, "{}", theme.id);
        });
    }

    #[test]
    fn the_sequential_ramp_runs_one_way_in_lightness() {
        // A ramp that reverses anywhere would read as two different magnitudes at the same cell
        // value.
        for_every_theme(|theme| {
            let ramp = theme.sequential_ramp();
            let dark = theme.is_dark();

            for i in 1..ramp.len() {
                let previous = cm::to_oklch(ramp[i - 1]).0;
                let current = cm::to_oklch(ramp[i]).0;

                assert!(
                    if dark { current > previous } else { current < previous },
                    "{}: step {i} breaks the ramp ({previous:.3} -> {current:.3})",
                    theme.id
                );
            }
        });
    }

    #[test]
    fn each_step_of_the_ramp_is_distinct_from_the_one_before_it() {
        for_every_theme(|theme| {
            let ramp = theme.sequential_ramp();

            for i in 1..ramp.len() {
                let step = (cm::to_oklch(ramp[i]).0 - cm::to_oklch(ramp[i - 1]).0).abs();
                assert!(
                    step >= 0.06,
                    "{}: steps {} and {i} differ by only {step:.3}",
                    theme.id,
                    i - 1
                );
            }
        });
    }

    #[test]
    fn both_diverging_arms_start_from_the_same_neutral_midpoint() {
        // The arms meet at zero. Two different midpoints would put a step in the scale exactly
        // where the value is on target.
        for_every_theme(|theme| assert_eq!(theme.cool_arm()[0], theme.warm_arm()[0], "{}", theme.id));
    }

    #[test]
    fn the_diverging_midpoint_recedes_into_the_surface() {
        // On-target cells should be the quiet ones; a loud midpoint would draw the eye to
        // exactly the cells that need no attention.
        for_every_theme(|theme| {
            assert!(
                cm::contrast(theme.cool_arm()[0], theme.background) < 2.0,
                "{}",
                theme.id
            )
        });
    }

    #[test]
    fn the_two_arms_are_tellable_apart_away_from_zero() {
        // Rich and lean must not look alike, or the table cannot show which way a cell is off.
        for_every_theme(|theme| {
            let cool = *theme.cool_arm().last().unwrap();
            let warm = *theme.warm_arm().last().unwrap();
            assert!(cvd_separation(cool, warm) >= 8.0, "{}", theme.id);
        });
    }

    #[test]
    fn an_empty_cell_is_distinguishable_from_a_populated_one() {
        // "Never visited" and "visited once" mean different things.
        for_every_theme(|theme| {
            let ratio = cm::contrast(theme.empty_cell(), theme.sequential_ramp()[0]);
            assert!(
                ratio >= 1.5,
                "{}: an empty cell and the bottom of the ramp differ by {ratio:.2}:1",
                theme.id
            );
        });
    }

    #[test]
    fn dark_and_light_themes_are_classified_by_their_background() {
        assert!(get("midnight").is_dark());
        assert!(get("contrast-dark").is_dark());
        assert!(!get("daylight").is_dark());
        assert!(!get("solarized-light").is_dark());
    }

    #[test]
    fn high_contrast_themes_earn_the_name() {
        for theme in ThemeCatalog::themes()
            .iter()
            .filter(|t| t.group == ThemeGroup::HighContrast)
        {
            assert!(cm::contrast(theme.text, theme.background) >= 15.0, "{}", theme.id);
            assert!(cm::contrast(theme.line, theme.background) >= 7.0, "{}", theme.id);
        }
    }

    // ----- the optional colours and the style (ThemeStyleTests) ---------------

    #[test]
    fn the_retro_theme_is_in_the_catalog_with_its_own_style() {
        let retro = get("retro");

        assert_eq!("retro", retro.id);
        assert_eq!(ThemeGroup::Retro, retro.group);
        assert_eq!(0.0, retro.style.corner_scale);
        assert!(retro.style.pixel_text);
        assert_eq!(TableStyle::Grid, retro.style.table);
        assert_eq!(ThemeFont::Retro, retro.style.ui_font);
        assert_eq!(ThemeFont::Retro, retro.style.data_font);
    }

    #[test]
    fn themes_that_only_recolour_look_exactly_as_they_did_before() {
        // Style arrived after fourteen themes existed. None of them states one, and none of them
        // should have changed by its arrival.
        for theme in ThemeCatalog::themes().iter().filter(|t| t.group != ThemeGroup::Retro) {
            assert_eq!(ThemeStyle::MODERN, theme.style, "{}", theme.id);
            assert_eq!(theme.text, theme.readout(), "{}", theme.id);
            assert_eq!(theme.panel, theme.chrome(), "{}", theme.id);
            assert_eq!(
                cm::blend(theme.background, theme.text, 0.13),
                theme.grid(),
                "{}",
                theme.id
            );
        }
    }

    #[test]
    fn the_modern_style_is_what_the_application_drew_before_themes_had_one() {
        // The C# names Segoe UI and Consolas here; `ThemeFont::Standard` is the platform's
        // stand-in for them (see `theme_fonts`).
        assert_eq!(ThemeFont::Standard, ThemeStyle::MODERN.ui_font);
        assert_eq!(ThemeFont::Standard, ThemeStyle::MODERN.data_font);
        assert_eq!(1.0, ThemeStyle::MODERN.corner_scale);
        let modern = ThemeStyle::MODERN;
        assert!(!modern.pixel_text);
        assert_eq!(TableStyle::Heat, ThemeStyle::MODERN.table);
    }

    #[test]
    fn text_is_readable_on_the_window_frame() {
        // Chrome is the menu bar and status strip, and carries body text.
        for_every_theme(|theme| {
            let ratio = cm::contrast(theme.text, theme.chrome());
            assert!(ratio >= 4.5, "{}: text on chrome is {ratio:.2}:1", theme.id);
        });
    }

    #[test]
    fn readouts_are_readable_wherever_they_are_drawn() {
        for_every_theme(|theme| {
            assert!(cm::contrast(theme.readout(), theme.background) >= 4.5, "{}", theme.id);
            assert!(cm::contrast(theme.readout(), theme.card()) >= 4.5, "{}", theme.id);
        });
    }

    #[test]
    fn corners_scale_with_the_theme() {
        assert_eq!(0.0, get("retro").radius(Corner::Normal));
        assert_eq!(4.0, get("midnight").radius(Corner::Normal));

        for corner in Corner::ALL {
            assert_eq!(0.0, get("retro").radius(corner));
            assert_eq!(corner.design_radius(), get("midnight").radius(corner));
        }
    }

    /// Worst-case OKLab separation across protanopia and deuteranopia, x100.
    fn cvd_separation(a: Color, b: Color) -> f64 {
        distance(simulate(a, true), simulate(b, true)).min(distance(simulate(a, false), simulate(b, false)))
    }

    fn distance(a: Color, b: Color) -> f64 {
        let (l1, a1, b1) = cm::to_oklab(a);
        let (l2, a2, b2) = cm::to_oklab(b);
        ((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2)).sqrt() * 100.0
    }

    /// Brettel/Vienot-style simulation, in linear sRGB.
    fn simulate(c: Color, protan: bool) -> Color {
        let m: [[f64; 3]; 3] = if protan {
            [
                [0.152286, 1.052583, -0.204868],
                [0.114503, 0.786281, 0.099216],
                [-0.003882, -0.048116, 1.051998],
            ]
        } else {
            [
                [0.367322, 0.860646, -0.227968],
                [0.280085, 0.672501, 0.047413],
                [-0.011820, 0.042940, 0.968881],
            ]
        };

        let linear = |v: u8| {
            let s = f64::from(v) / 255.0;
            if s <= 0.04045 {
                s / 12.92
            } else {
                ((s + 0.055) / 1.055).powf(2.4)
            }
        };
        let encode = |l: f64| {
            let v = l.clamp(0.0, 1.0);
            let s = if v <= 0.0031308 {
                12.92 * v
            } else {
                1.055 * v.powf(1.0 / 2.4) - 0.055
            };
            (s * 255.0).round_ties_even().clamp(0.0, 255.0) as u8
        };

        let (r, g, b) = (linear(c.r), linear(c.g), linear(c.b));
        Color::rgb(
            encode(m[0][0] * r + m[0][1] * g + m[0][2] * b),
            encode(m[1][0] * r + m[1][1] * g + m[1][2] * b),
            encode(m[2][0] * r + m[2][1] * g + m[2][2] * b),
        )
    }
}
