//! Ported from `ThemeFonts.cs`. Owned by package `w6-ui-base`.
//!
//! Turns a theme's font choice into egui font definitions and sizes.
//!
//! One place, so every screen that draws its own text resolves a theme's fonts the same way. A
//! view that built its own `FontId` for a Retro theme would quietly draw the default face. Views
//! ask [`ThemeFonts::ui`] and [`ThemeFonts::data`]; a source scan in the tests keeps it that way.
//!
//! # The two faces
//!
//! * [`ThemeFont::Standard`] is what the C# calls Segoe UI (labels) and Consolas (numbers). Those
//!   are Windows fonts, so until the user decides what stands in for them elsewhere (UI-SPEC §1,
//!   open question) this is egui's own proportional and monospace faces on every platform.
//!   OPEN-QUESTION: 4.2. egui's faces lack the arrows, triangles, tick and bullet the interface is
//!   written with, so OLV Retro trails both stacks and draws just those.
//! * [`ThemeFont::Retro`] is OLV Retro 8x16, drawn for this project and MIT like the rest of it.
//!   It covers ASCII and the symbols the interface uses (degrees, Delta, the arrows, the
//!   ellipsis); any other character falls back, glyph by glyph, to the face below it in the
//!   family (egui's default), rather than drawing a box.
//!
//! # Pixel text
//!
//! The C# draws the pixel font aliased, at the interface's ordinary sizes. egui cannot: it
//! rasterises every glyph with coverage and has no threshold. What it does allow is hinting
//! switched to its strongest, whole-pixel glyph positions, and no sub-pixel binning, and with the
//! text sized to a whole multiple of the font's 16-pixel design height (see [`retro_size`]) the
//! outlines land exactly on pixel boundaries, which gives the same hard-edged result. At any other
//! size they would be smeared, so Retro text snaps to those sizes. This is a layout change from
//! the C# (UI-SPEC §1 open question, "snap to multiples of 16"), decided here for the demo and
//! easy to reverse in one function. OPEN-QUESTION: 4.3.

use std::sync::Arc;

use egui::epaint::text::{FontData, FontDefinitions, FontTweak, HintingTarget};
use egui::{Context, FontFamily, FontId};

use super::theme::Theme;

/// Family name of the bundled pixel font.
pub const RETRO_FAMILY: &str = "OLV Retro";

/// The OLV Retro 8x16 font, compiled in so a theme never depends on what happens to be installed.
static RETRO_TTF: &[u8] = include_bytes!("../../../../assets/fonts/OLVRetro-8x16.ttf");

/// The height, in physical pixels, the pixel font is drawn on.
pub const RETRO_CELL: f32 = 16.0;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum ThemeFont {
    /// The platform's ordinary face: proportional for labels, monospace for numbers.
    Standard,
    /// OLV Retro 8x16, with the standard face behind it for the glyphs it lacks.
    Retro,
}

/// What a theme's fonts are made of: the part of its style that needs the font set rebuilt when it
/// changes.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct FontSet {
    pub ui_retro: bool,
    pub data_retro: bool,
}

impl FontSet {
    pub fn of(theme: &Theme) -> Self {
        Self {
            ui_retro: theme.style.ui_font == ThemeFont::Retro,
            data_retro: theme.style.data_font == ThemeFont::Retro,
        }
    }
}

/// The size, in points, to draw pixel text so it lands on whole pixels: `design` is the size the
/// C# would have used, in design pixels.
///
/// The glyphs sit on a 16-pixel grid, so the text is drawn at a whole multiple of 16 physical
/// pixels (at least one), the nearest to what was asked for. A size in between would resample the
/// glyphs and blur them.
pub fn retro_size(design: f32, pixels_per_point: f32) -> f32 {
    let ppp = pixels_per_point.max(0.25);
    let multiple = (design * ppp / RETRO_CELL).round().max(1.0);
    RETRO_CELL * multiple / ppp
}

pub struct ThemeFonts;

impl ThemeFonts {
    /// The font definitions for a set of fonts: egui's defaults, with OLV Retro first in the
    /// proportional and/or monospace stack when a theme asks for it.
    pub fn definitions(set: FontSet) -> FontDefinitions {
        let mut definitions = FontDefinitions::default();

        definitions.font_data.insert(
            RETRO_FAMILY.to_owned(),
            Arc::new(FontData::from_static(RETRO_TTF).tweak(FontTweak {
                hinting: Some(true),
                hinting_target: HintingTarget::Mono,
                subpixel_binning: Some(false),
                ..FontTweak::default()
            })),
        );

        // First in the stack where a theme asks for it; otherwise last, where it only ever draws
        // the glyphs the standard faces lack. The pixel font carries the arrows, triangles, tick
        // and bullet the interface is written with, which egui's own faces do not, so a modern
        // theme shows those rather than an empty box (the C# had them from Segoe UI).
        for (retro, family) in [
            (set.ui_retro, FontFamily::Proportional),
            (set.data_retro, FontFamily::Monospace),
        ] {
            if let Some(stack) = definitions.families.get_mut(&family) {
                if retro {
                    stack.insert(0, RETRO_FAMILY.to_owned());
                } else {
                    stack.push(RETRO_FAMILY.to_owned());
                }
            }
        }

        definitions
    }

    /// The font for labels, menus and buttons and for the text self-drawn views draw for axes and
    /// captions, at `design` pixels (before the interface zoom, which egui applies itself).
    pub fn ui(ctx: &Context, theme: &Theme, design: f32) -> FontId {
        Self::font(ctx, theme.style.ui_font, FontFamily::Proportional, design)
    }

    /// The font for numbers that line up in columns: table cells, readouts, status values.
    pub fn data(ctx: &Context, theme: &Theme, design: f32) -> FontId {
        Self::font(ctx, theme.style.data_font, FontFamily::Monospace, design)
    }

    fn font(ctx: &Context, font: ThemeFont, family: FontFamily, design: f32) -> FontId {
        let size = match font {
            ThemeFont::Standard => design,
            ThemeFont::Retro => retro_size(design, ctx.pixels_per_point()),
        };
        FontId::new(size, family)
    }
}

#[cfg(test)]
mod tests {
    use super::super::theme_catalog::ThemeCatalog;
    use super::*;

    #[test]
    fn the_bundled_font_is_compiled_in_and_found_by_name() {
        // A font that fails to parse is dropped by egui without a word and the fallback drawn
        // instead, so parse the glyphs ourselves and check whose they are.
        let face = ttf_parser::Face::parse(RETRO_TTF, 0).expect("the bundled font parses");

        let family = face
            .names()
            .into_iter()
            .find(|n| n.name_id == ttf_parser::name_id::FAMILY)
            .and_then(|n| n.to_string());
        assert_eq!(Some(RETRO_FAMILY.to_owned()), family);

        // It carries the letters the interface is written in, and it is a 16-pixel-high design.
        for c in "AaZz09 .%\u{b0}\u{394}\u{2192}".chars() {
            assert!(face.glyph_index(c).is_some(), "no glyph for {c:?}");
        }
        assert_eq!(1024, face.units_per_em());
        assert_eq!(768, face.ascender());
        assert_eq!(-256, face.descender());
    }

    #[test]
    fn retro_leads_the_stacks_that_ask_for_it_and_trails_the_others() {
        let both = ThemeFonts::definitions(FontSet {
            ui_retro: true,
            data_retro: true,
        });
        assert!(both.font_data.contains_key(RETRO_FAMILY));

        for family in [FontFamily::Proportional, FontFamily::Monospace] {
            let stack = &both.families[&family];
            assert_eq!(RETRO_FAMILY, stack[0]);
            // The face behind it covers what it lacks.
            assert!(stack.len() > 1, "{family:?} has nothing to fall back on");
        }

        // A modern theme keeps its own faces first, with the pixel font only as the last resort
        // for the symbols they lack.
        let modern = ThemeFonts::definitions(FontSet {
            ui_retro: false,
            data_retro: false,
        });
        for family in [FontFamily::Proportional, FontFamily::Monospace] {
            let stack = &modern.families[&family];
            assert_ne!(RETRO_FAMILY, stack[0], "{family:?}");
            assert_eq!(RETRO_FAMILY, stack[stack.len() - 1], "{family:?}");
        }
    }

    #[test]
    fn a_theme_asks_for_the_fonts_its_style_names() {
        let retro = FontSet::of(ThemeCatalog::find(Some("retro")));
        assert_eq!(
            FontSet {
                ui_retro: true,
                data_retro: true
            },
            retro
        );

        let modern = FontSet::of(ThemeCatalog::find(Some("midnight")));
        assert_eq!(
            FontSet {
                ui_retro: false,
                data_retro: false
            },
            modern
        );
    }

    #[test]
    fn pixel_text_lands_on_whole_pixels_at_every_zoom() {
        for ppp in [1.0_f32, 1.15, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0] {
            for design in [11.0_f32, 12.0, 13.0, 16.0, 19.0, 24.0, 30.0] {
                let physical = retro_size(design, ppp) * ppp;
                let multiple = physical / RETRO_CELL;
                assert!(
                    (multiple - multiple.round()).abs() < 1e-4 && multiple >= 1.0,
                    "{design}px at {ppp}x is {physical} physical pixels"
                );
            }
        }
    }

    #[test]
    fn pixel_text_is_never_smaller_than_the_grid_it_was_drawn_on() {
        assert_eq!(16.0, retro_size(8.0, 1.0));
        assert_eq!(16.0, retro_size(12.0, 1.0));
        assert_eq!(32.0, retro_size(24.0, 1.0));
    }

    #[test]
    fn a_standard_theme_keeps_the_size_it_was_asked_for() {
        let ctx = Context::default();
        let midnight = ThemeCatalog::find(Some("midnight"));
        assert_eq!(13.0, ThemeFonts::ui(&ctx, midnight, 13.0).size);
        assert_eq!(FontFamily::Proportional, ThemeFonts::ui(&ctx, midnight, 13.0).family);
        assert_eq!(FontFamily::Monospace, ThemeFonts::data(&ctx, midnight, 13.0).family);
    }

    /// ThemeStyle states the defaults and this module resolves them; nothing else should be
    /// building a font by hand, or that one place stays modern in every theme, which nobody
    /// notices until they switch to Retro. (The C# test `NoViewNamesAFontInCode`.)
    #[test]
    fn no_view_names_a_font_in_code() {
        let allowed = ["theme_fonts.rs", "egui_theme.rs"];
        let src = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut offences = Vec::new();

        let mut stack = vec![src];
        while let Some(dir) = stack.pop() {
            for entry in std::fs::read_dir(&dir).unwrap().flatten() {
                let path = entry.path();
                if path.is_dir() {
                    stack.push(path);
                    continue;
                }
                let name = path.file_name().unwrap().to_string_lossy().into_owned();
                if !name.ends_with(".rs") || allowed.contains(&name.as_str()) {
                    continue;
                }
                for (i, line) in std::fs::read_to_string(&path).unwrap().lines().enumerate() {
                    if line.contains("FontId::proportional(")
                        || line.contains("FontId::monospace(")
                        || line.contains("FontFamily::Name")
                        || line.contains("FontId::new(")
                    {
                        offences.push(format!("{name}:{}: {}", i + 1, line.trim()));
                    }
                }
            }
        }

        assert!(
            offences.is_empty(),
            "Use ThemeFonts with the theme's style:\n{}",
            offences.join("\n")
        );
    }
}
