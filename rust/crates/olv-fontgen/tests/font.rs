//! Proves the generated font works: a real TrueType parser maps and measures every glyph, and the
//! rasterizer egui uses (`ab_glyph`) lights exactly the pixels the source bitmap says.

use ab_glyph::{Font, FontRef, GlyphId, PxScale, point};
use olv_fontgen::{Glyph, build_ttf, parse_source};
use std::path::PathBuf;

fn fonts_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../assets/fonts")
}

fn glyphs() -> Vec<Glyph> {
    let text = std::fs::read_to_string(fonts_dir().join("olv-retro-8x16.txt")).unwrap();
    parse_source(&text).unwrap()
}

fn code_of(g: &Glyph) -> Option<char> {
    g.code.and_then(char::from_u32)
}

/// Characters the app's UI text uses beyond printable ASCII.
const APP_CHARS: &str = "\u{a0}\u{a2}\u{a7}\u{b0}\u{b1}\u{b2}\u{b3}\u{b5}\u{b7}\u{d7}\u{f7}\u{192}\u{394}\u{3a9}\u{3b3}\u{3b8}\
    \u{3bb}\u{3c0}\u{2013}\u{2014}\u{2018}\u{2019}\u{201c}\u{201d}\u{2022}\u{2026}\u{2039}\u{203a}\u{2190}\u{2191}\u{2192}\
    \u{2193}\u{2194}\u{2212}\u{221a}\u{223f}\u{2264}\u{2265}\u{2338}\u{25a0}\u{25b2}\u{25b4}\u{25b8}\u{25bc}\u{25be}\u{25c2}\
    \u{25c8}\u{25cb}\u{25cf}\u{2713}\u{2715}";

#[test]
fn source_covers_ascii_and_the_apps_characters() {
    let have: Vec<char> = glyphs().iter().filter_map(code_of).collect();
    for c in (0x20u8..=0x7e).map(char::from).chain(APP_CHARS.chars()) {
        assert!(have.contains(&c), "missing U+{:04X}", c as u32);
    }
}

#[test]
fn every_glyph_is_mapped_and_its_outline_matches_its_pixels() {
    let glyphs = glyphs();
    let ttf = build_ttf(&glyphs).unwrap();
    let face = ttf_parser::Face::parse(&ttf, 0).unwrap();
    assert_eq!(face.units_per_em(), 1024);
    assert_eq!(face.number_of_glyphs() as usize, glyphs.len());
    for g in &glyphs {
        let id = match code_of(g) {
            Some(c) => face
                .glyph_index(c)
                .unwrap_or_else(|| panic!("U+{:04X} not in cmap", c as u32)),
            None => ttf_parser::GlyphId(0),
        };
        assert_eq!(face.glyph_hor_advance(id), Some(512));
        let lit: Vec<(i32, i32)> = (0..16)
            .flat_map(|r| (0..8).map(move |c| (c, r)))
            .filter(|&(c, r)| g.lit(c as usize, r as usize))
            .collect();
        let bbox = face.glyph_bounding_box(id);
        if lit.is_empty() {
            assert!(bbox.is_none(), "blank glyph {:?} has an outline", g.code);
            continue;
        }
        let b = bbox.unwrap_or_else(|| panic!("glyph {:?} has no outline", g.code));
        let x0 = lit.iter().map(|p| p.0).min().unwrap();
        let x1 = lit.iter().map(|p| p.0).max().unwrap() + 1;
        let top = lit.iter().map(|p| p.1).min().unwrap();
        let bottom = lit.iter().map(|p| p.1).max().unwrap() + 1;
        // 64 units per pixel; the baseline (y = 0) is under row 12.
        let expect = (x0 * 64, (12 - bottom) * 64, x1 * 64, (12 - top) * 64);
        let got = (
            i32::from(b.x_min),
            i32::from(b.y_min),
            i32::from(b.x_max),
            i32::from(b.y_max),
        );
        assert_eq!(got, expect, "bounding box of {:?}", g.code);
    }
}

#[test]
fn font_metrics_and_names_are_as_specified() {
    let ttf = build_ttf(&glyphs()).unwrap();
    let face = ttf_parser::Face::parse(&ttf, 0).unwrap();
    assert_eq!((face.ascender(), face.descender(), face.line_gap()), (768, -256, 0));
    assert!(face.is_monospaced());
    assert!(!face.is_bold() && !face.is_italic());
    let name = |id: u16| {
        face.names()
            .into_iter()
            .find(|n| n.name_id == id)
            .and_then(|n| n.to_string())
            .unwrap_or_default()
    };
    assert_eq!(name(1), "OLV Retro");
    assert_eq!(name(2), "Regular");
    assert_eq!(name(5), "Version 1.0");
    assert_eq!(name(0), "Copyright (c) 2026 OpenLogViewer contributors");
    assert_eq!(name(13), "MIT");
}

/// Rasterizes one glyph at 16 px and returns coverage on the 8x16 cell.
fn rasterize(font: &FontRef<'_>, id: GlyphId) -> [[f32; 8]; 16] {
    let mut cells = [[0.0f32; 8]; 16];
    // The baseline is 12 px below the top of the cell.
    let glyph = id.with_scale_and_position(PxScale::from(16.0), point(0.0, 12.0));
    if let Some(outline) = font.outline_glyph(glyph) {
        let b = outline.px_bounds();
        outline.draw(|x, y, c| {
            let (px, py) = (b.min.x as i32 + x as i32, b.min.y as i32 + y as i32);
            assert!(
                (0..8).contains(&px) && (0..16).contains(&py),
                "ink outside the cell at ({px}, {py})"
            );
            cells[py as usize][px as usize] += c;
        });
    }
    cells
}

fn check_rasterized(font: &FontRef<'_>, g: &Glyph) {
    let id = match code_of(g) {
        Some(c) => font.glyph_id(c),
        None => GlyphId(0),
    };
    let cells = rasterize(font, id);
    for (row, cols) in cells.iter().enumerate() {
        for (col, &cover) in cols.iter().enumerate() {
            let want = if g.lit(col, row) { 1.0 } else { 0.0 };
            assert!(
                (cover - want).abs() < 0.02,
                "glyph {:?} pixel ({col}, {row}): coverage {cover}, bitmap says {want}",
                g.code
            );
        }
    }
}

#[test]
fn rasterized_glyphs_light_the_pixels_the_bitmap_says() {
    let glyphs = glyphs();
    let ttf = build_ttf(&glyphs).unwrap();
    let font = FontRef::try_from_slice(&ttf).unwrap();
    // A few glyphs a person can eyeball, including a descender, a symbol and .notdef ...
    for want in [
        Some('A'),
        Some('g'),
        Some('.'),
        Some('\u{394}'),
        Some('\u{2192}'),
        Some('\u{2026}'),
        None,
    ] {
        let g = glyphs
            .iter()
            .find(|g| code_of(g) == want && (want.is_some() || g.code.is_none()))
            .unwrap();
        check_rasterized(&font, g);
    }
    // ... and a lit pixel in a known place: the left stem of 'H' is column 1, rows 3..=11.
    let h = rasterize(&font, font.glyph_id('H'));
    assert!(h[3][1] > 0.98 && h[11][1] > 0.98 && h[2][1] < 0.02 && h[12][1] < 0.02 && h[5][0] < 0.02);
    // ... then everything.
    for g in &glyphs {
        check_rasterized(&font, g);
    }
}

#[test]
fn committed_font_is_up_to_date_with_the_source() {
    let built = build_ttf(&glyphs()).unwrap();
    let committed = std::fs::read(fonts_dir().join("OLVRetro-8x16.ttf")).unwrap();
    assert!(
        built == committed,
        "OLVRetro-8x16.ttf is stale; run `cargo run -p olv-fontgen` in rust/"
    );
}

#[test]
fn letters_do_not_touch_their_neighbours() {
    // The brief: most glyphs keep a pixel of side bearing; letters and digits always do.
    for g in glyphs() {
        if let Some(c) = code_of(&g).filter(char::is_ascii_alphanumeric) {
            for r in 0..16 {
                assert!(!g.lit(7, r), "{c:?} touches its right edge on row {r}");
            }
        }
    }
}
