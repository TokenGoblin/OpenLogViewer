//! Preview rendering: rasterizes the generated TTF with `ab_glyph`, the same rasterizer egui uses.

use crate::{ADVANCE, ASCENT_ROWS, Error, GLYPH_H, GLYPH_W, Glyph};
use ab_glyph::{Font, FontRef, GlyphId, PxScale, point};

/// An RGB image, row-major, 3 bytes per pixel.
pub struct Image {
    pub width: usize,
    pub height: usize,
    pub rgb: Vec<u8>,
}

const GREEN: [f32; 3] = [85.0, 255.0, 85.0];
const SAMPLES: [&str; 2] = [
    "RPM 3000  MAP 100.2 kPa  AFR 14.7:1  VE 92.0 %",
    "\u{394} +0.0 %  CLT 86 \u{b0}C  \u{25b2}\u{25bc} \u{2713} \u{2192} \u{2026}",
];
const COLUMNS: usize = 40;
const CELL_W: usize = 10;
const CELL_H: usize = 18;
const MARGIN: usize = 8;

struct Canvas {
    width: usize,
    height: usize,
    cover: Vec<f32>,
}

impl Canvas {
    fn glyph(&mut self, font: &FontRef<'_>, id: GlyphId, x: usize, y: usize) {
        // 16 px em height: font unit 64 is exactly one pixel.
        let scale = PxScale::from(GLYPH_H as f32);
        let g = id.with_scale_and_position(scale, point(x as f32, y as f32 + f32::from(ASCENT_ROWS)));
        if let Some(outline) = font.outline_glyph(g) {
            let b = outline.px_bounds();
            outline.draw(|gx, gy, c| {
                let px = b.min.x as i64 + i64::from(gx);
                let py = b.min.y as i64 + i64::from(gy);
                if px >= 0 && py >= 0 && (px as usize) < self.width && (py as usize) < self.height {
                    let cell = &mut self.cover[py as usize * self.width + px as usize];
                    *cell = (*cell + c).min(1.0);
                }
            });
        }
    }

    fn text(&mut self, font: &FontRef<'_>, s: &str, x: usize, y: usize) {
        for (i, ch) in s.chars().enumerate() {
            self.glyph(font, font.glyph_id(ch), x + i * GLYPH_W, y);
        }
    }
}

/// Renders every glyph in a grid and the two sample lines, green on black, scaled 2x
/// nearest-neighbour. The pixels come from rasterizing `ttf`; `glyphs` only says which to show.
pub fn render_preview(ttf: &[u8], glyphs: &[Glyph]) -> Result<Image, Error> {
    debug_assert_eq!(usize::from(ADVANCE), GLYPH_W * 64);
    let font = FontRef::try_from_slice(ttf).map_err(|e| Error::Preview(format!("cannot load the TTF: {e}")))?;
    let mut shown: Vec<Option<char>> = vec![None];
    let mut codes: Vec<u32> = glyphs.iter().filter_map(|g| g.code).collect();
    codes.sort_unstable();
    shown.extend(codes.into_iter().map(char::from_u32));
    let rows = shown.len().div_ceil(COLUMNS);
    let sample_w = SAMPLES.iter().map(|s| s.chars().count()).max().unwrap_or(0) * GLYPH_W;
    let width = MARGIN * 2 + (COLUMNS * CELL_W).max(sample_w);
    let grid_h = rows * CELL_H;
    let height = MARGIN * 2 + grid_h + 8 + SAMPLES.len() * (GLYPH_H + 4);
    let mut canvas = Canvas {
        width,
        height,
        cover: vec![0.0; width * height],
    };
    for (i, item) in shown.iter().enumerate() {
        let id = item.map_or(GlyphId(0), |c| font.glyph_id(c));
        canvas.glyph(
            &font,
            id,
            MARGIN + (i % COLUMNS) * CELL_W + 1,
            MARGIN + (i / COLUMNS) * CELL_H + 1,
        );
    }
    for (i, s) in SAMPLES.iter().enumerate() {
        canvas.text(&font, s, MARGIN, MARGIN + grid_h + 8 + i * (GLYPH_H + 4));
    }
    let (w2, h2) = (width * 2, height * 2);
    let mut rgb = Vec::with_capacity(w2 * h2 * 3);
    for y in 0..h2 {
        for x in 0..w2 {
            let c = canvas.cover[(y / 2) * width + x / 2];
            rgb.extend(GREEN.iter().map(|v| (v * c).round() as u8));
        }
    }
    Ok(Image {
        width: w2,
        height: h2,
        rgb,
    })
}

/// Encodes an [`Image`] as PNG.
pub fn encode_png(img: &Image) -> Result<Vec<u8>, Error> {
    let mut out = Vec::new();
    let mut enc = png::Encoder::new(&mut out, img.width as u32, img.height as u32);
    enc.set_color(png::ColorType::Rgb);
    enc.set_depth(png::BitDepth::Eight);
    let err = |e: png::EncodingError| Error::Preview(format!("png: {e}"));
    let mut writer = enc.write_header().map_err(err)?;
    writer.write_image_data(&img.rgb).map_err(err)?;
    writer.finish().map_err(err)?;
    Ok(out)
}
