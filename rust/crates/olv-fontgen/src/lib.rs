//! Builds the original "OLV Retro" 8x16 pixel font.
//!
//! The glyph source is human-editable ASCII art ([`parse_source`]); [`build_ttf`] turns it into a
//! TrueType font in which every horizontal run of lit pixels is one rectangle contour;
//! [`render_preview`] rasterizes that TTF (not the bitmap source) into a preview image.
//! The font and this tool are MIT-licensed and every glyph is original work.

mod preview;
mod source;
mod ttf;

pub use preview::{Image, encode_png, render_preview};
pub use source::parse_source;
pub use ttf::build_ttf;

/// Pixels per glyph row.
pub const GLYPH_W: usize = 8;
/// Rows per glyph.
pub const GLYPH_H: usize = 16;
/// Font units per pixel.
pub const PIXEL: i16 = 64;
/// Units per em (16 pixels).
pub const UNITS_PER_EM: u16 = 1024;
/// Rows above the baseline (the baseline sits under row 12).
pub const ASCENT_ROWS: i16 = 12;
/// Horizontal advance in font units (8 pixels).
pub const ADVANCE: u16 = 512;

/// Everything that can go wrong reading the source or producing a file.
#[derive(Debug)]
pub enum Error {
    /// The glyph source is malformed; the message names the line.
    Source(String),
    /// The font could not be built (for example a code point outside the BMP).
    Font(String),
    /// Preview rendering or PNG encoding failed.
    Preview(String),
}

impl std::fmt::Display for Error {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Error::Source(m) => write!(f, "glyph source: {m}"),
            Error::Font(m) => write!(f, "font: {m}"),
            Error::Preview(m) => write!(f, "preview: {m}"),
        }
    }
}

impl std::error::Error for Error {}

/// One glyph: `None` is `.notdef`, otherwise the Unicode code point.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Glyph {
    pub code: Option<u32>,
    /// 16 rows, top first; bit 7 is the leftmost column.
    pub rows: [u8; GLYPH_H],
}

/// A horizontal run of lit pixels: `len` pixels starting at column `start` on `row` (0 = top).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Run {
    pub row: usize,
    pub start: usize,
    pub len: usize,
}

impl Glyph {
    /// Whether the pixel at (`col`, `row`) is lit.
    pub fn lit(&self, col: usize, row: usize) -> bool {
        col < GLYPH_W && row < GLYPH_H && self.rows[row] & (0x80 >> col) != 0
    }

    /// The glyph's runs, top to bottom, left to right.
    pub fn runs(&self) -> Vec<Run> {
        let mut runs = Vec::new();
        for row in 0..GLYPH_H {
            let mut col = 0;
            while col < GLYPH_W {
                if self.lit(col, row) {
                    let start = col;
                    while col < GLYPH_W && self.lit(col, row) {
                        col += 1;
                    }
                    runs.push(Run {
                        row,
                        start,
                        len: col - start,
                    });
                } else {
                    col += 1;
                }
            }
        }
        runs
    }

    /// Outline bounding box in font units as (x_min, y_min, x_max, y_max); `None` for a blank glyph.
    pub fn bbox(&self) -> Option<(i16, i16, i16, i16)> {
        let runs = self.runs();
        let first = runs.first()?;
        let last = runs.last()?;
        let x_min = runs.iter().map(|r| r.start).min()?;
        let x_max = runs.iter().map(|r| r.start + r.len).max()?;
        let top = |row: usize| (ASCENT_ROWS - row as i16) * PIXEL;
        Some((
            x_min as i16 * PIXEL,
            top(last.row + 1),
            x_max as i16 * PIXEL,
            top(first.row),
        ))
    }
}
