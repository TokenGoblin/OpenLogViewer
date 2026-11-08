//! Ported from `ColorMath.cs`. Owned by package `w6-ui-base`.
//!
//! Perceptual colour helpers, in OKLab. Themes specify only a handful of anchor colours; the rest
//! of a theme's surfaces and its heat-table ramps are derived here, so a new theme cannot be
//! internally inconsistent.
//!
//! OKLab rather than HSL because lightness has to mean the same thing across hues: a ramp built
//! by stepping HSL lightness looks even in yellow and crushed in blue, which is exactly the
//! failure a sequential scale must not have.

use std::fmt;

/// An opaque sRGB colour, one byte per channel (WPF's `Color` with alpha always 255).
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub struct Color {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Color {
    pub const BLACK: Color = Color::rgb(0, 0, 0);
    pub const WHITE: Color = Color::rgb(255, 255, 255);

    pub const fn rgb(r: u8, g: u8, b: u8) -> Self {
        Self { r, g, b }
    }

    /// Reads `#RRGGBB` at compile time, so a catalog of themes can be a `static`. A malformed
    /// string stops the build (in a `const`) or panics, rather than yielding a wrong colour.
    pub const fn hex(text: &str) -> Self {
        let s = text.as_bytes();
        assert!(s.len() == 7 && s[0] == b'#', "a colour is written #RRGGBB");
        Self {
            r: nibble(s[1]) << 4 | nibble(s[2]),
            g: nibble(s[3]) << 4 | nibble(s[4]),
            b: nibble(s[5]) << 4 | nibble(s[6]),
        }
    }

    /// Reads `#RRGGBB` or `RRGGBB`; `None` for anything else.
    pub fn parse(text: &str) -> Option<Self> {
        let t = text.trim().trim_start_matches('#');
        if t.len() != 6 || !t.bytes().all(|b| b.is_ascii_hexdigit()) {
            return None;
        }
        let v = u32::from_str_radix(t, 16).ok()?;
        Some(Self::rgb((v >> 16) as u8, (v >> 8) as u8, v as u8))
    }

    pub fn to_color32(self) -> egui::Color32 {
        egui::Color32::from_rgb(self.r, self.g, self.b)
    }
}

const fn nibble(c: u8) -> u8 {
    match c {
        b'0'..=b'9' => c - b'0',
        b'a'..=b'f' => c - b'a' + 10,
        b'A'..=b'F' => c - b'A' + 10,
        _ => panic!("not a hex digit"),
    }
}

/// `#RRGGBB`, upper case.
impl fmt::Display for Color {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "#{:02X}{:02X}{:02X}", self.r, self.g, self.b)
    }
}

impl From<Color> for egui::Color32 {
    fn from(c: Color) -> Self {
        c.to_color32()
    }
}

/// Mixes two colours; `t` = 0 gives `a`. `t` is clamped to [0, 1]; each channel rounds half to
/// even, as .NET's `Math.Round` does.
pub fn blend(a: Color, b: Color, t: f64) -> Color {
    let t = t.clamp(0.0, 1.0);
    let channel = |x: u8, y: u8| (f64::from(x) + f64::from(i32::from(y) - i32::from(x)) * t).round_ties_even() as u8;
    Color::rgb(channel(a.r, b.r), channel(a.g, b.g), channel(a.b, b.b))
}

/// WCAG relative luminance.
pub fn luminance(c: Color) -> f64 {
    0.2126 * to_linear(c.r) + 0.7152 * to_linear(c.g) + 0.0722 * to_linear(c.b)
}

pub fn contrast(a: Color, b: Color) -> f64 {
    let (x, y) = (luminance(a), luminance(b));
    (x.max(y) + 0.05) / (x.min(y) + 0.05)
}

/// Whichever of the two candidates reads more clearly on `on` (`first` on a tie).
pub fn readable(on: Color, first: Color, second: Color) -> Color {
    if contrast(on, first) >= contrast(on, second) {
        first
    } else {
        second
    }
}

/// A sequential ramp in the anchor's hue, running from least to most visible against the
/// surface: dark and saturated up to pale on a dark theme, pale down to deep on a light one.
/// Magnitude is carried by lightness, which survives colour-vision deficiency and greyscale
/// printing.
///
/// The dark end does not run all the way down. A blue hue at low perceptual lightness carries
/// very little luminance (blue is 7% of it), so a ramp starting lower would put its first step
/// level with an empty cell, and "never visited" would look like "visited once".
pub fn ramp(anchor: Color, on_dark: bool, steps: usize) -> Vec<Color> {
    build(
        anchor,
        steps,
        if on_dark { 0.50 } else { 0.78 },
        if on_dark { 0.88 } else { 0.36 },
        1.00,
        0.45,
    )
}

/// A diverging arm: the neutral midpoint, then the ramp.
///
/// Deliberately not the sequential ramp with a midpoint bolted on. A sequential scale carries
/// magnitude in lightness alone, so it runs the full range and can shed chroma at the far end;
/// on a diverging scale that far end is the largest error, and two arms that both fade towards
/// the same near-white would make the worst rich cell and the worst lean cell look alike. The
/// arms therefore travel less in lightness and hold their hue, which is what carries polarity.
pub fn diverging_arm(anchor: Color, surface: Color, on_dark: bool) -> Vec<Color> {
    let tail = build(
        anchor,
        5,
        if on_dark { 0.47 } else { 0.72 },
        if on_dark { 0.76 } else { 0.43 },
        1.00,
        0.85,
    );

    let mut arm = Vec::with_capacity(tail.len() + 1);
    // Near the surface, so cells on target recede rather than competing with the cells that are
    // off it.
    arm.push(blend(surface, if on_dark { Color::WHITE } else { Color::BLACK }, 0.18));
    arm.extend(tail);
    arm
}

/// Steps a hue between two lightnesses, scaling chroma from `from_chroma` to `to_chroma` as a
/// fraction of the anchor's own. Chroma has to fall away as a step approaches the surface, or
/// the step leaves the sRGB gamut and clips flat against its neighbour.
fn build(anchor: Color, steps: usize, start_l: f64, end_l: f64, from_chroma: f64, to_chroma: f64) -> Vec<Color> {
    let (_, chroma, hue) = to_oklch(anchor);

    // Floored so a washed-out anchor still yields identifiable steps, and capped where the pale
    // end would otherwise clip.
    let chroma = chroma.clamp(0.09, 0.20);

    (0..steps)
        .map(|i| {
            let t = if steps == 1 { 0.0 } else { i as f64 / (steps - 1) as f64 };
            from_oklch(
                start_l + (end_l - start_l) * t,
                chroma * (from_chroma + (to_chroma - from_chroma) * t),
                hue,
            )
        })
        .collect()
}

// ----- OKLab ------------------------------------------------------------

/// Lightness, chroma and hue (radians).
pub fn to_oklch(c: Color) -> (f64, f64, f64) {
    let (l, a, b) = to_oklab(c);
    (l, (a * a + b * b).sqrt(), b.atan2(a))
}

pub fn to_oklab(c: Color) -> (f64, f64, f64) {
    let (r, g, bl) = (to_linear(c.r), to_linear(c.g), to_linear(c.b));

    let l = (0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * bl).cbrt();
    let m = (0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * bl).cbrt();
    let s = (0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * bl).cbrt();

    (
        0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
        1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
        0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s,
    )
}

/// Clipped into sRGB, so an out-of-gamut request still returns the right hue.
pub fn from_oklch(lightness: f64, chroma: f64, hue: f64) -> Color {
    from_oklab(lightness, chroma * hue.cos(), chroma * hue.sin())
}

pub fn from_oklab(lightness: f64, a: f64, b: f64) -> Color {
    let cube = |v: f64| v * v * v;
    let l = cube(lightness + 0.3963377774 * a + 0.2158037573 * b);
    let m = cube(lightness - 0.1055613458 * a - 0.0638541728 * b);
    let s = cube(lightness - 0.0894841775 * a - 1.2914855480 * b);

    Color::rgb(
        to_byte(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
        to_byte(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
        to_byte(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s),
    )
}

fn to_linear(v: u8) -> f64 {
    let s = f64::from(v) / 255.0;
    if s <= 0.04045 {
        s / 12.92
    } else {
        ((s + 0.055) / 1.055).powf(2.4)
    }
}

fn to_byte(linear: f64) -> u8 {
    let c = linear.clamp(0.0, 1.0);
    let s = if c <= 0.0031308 {
        12.92 * c
    } else {
        1.055 * c.powf(1.0 / 2.4) - 0.055
    };
    (s * 255.0).round_ties_even().clamp(0.0, 255.0) as u8
}

#[cfg(test)]
mod tests {
    use super::*;

    const RED: Color = Color::hex("#FF0000");
    const GRAY: Color = Color::hex("#808080");

    #[test]
    fn blend_reaches_both_ends_and_the_middle() {
        assert_eq!(Color::BLACK, blend(Color::BLACK, Color::WHITE, 0.0));
        assert_eq!(Color::WHITE, blend(Color::BLACK, Color::WHITE, 1.0));
        // 127.5 rounds half to even, which is 128.
        assert_eq!(Color::rgb(128, 128, 128), blend(Color::BLACK, Color::WHITE, 0.5));
    }

    #[test]
    fn blend_rounds_half_to_even_as_dotnet_does() {
        // 0.5 -> 0 and 1.5 -> 2 under banker's rounding; "round half up" gives 1 and 2.
        assert_eq!(Color::rgb(0, 0, 0), blend(Color::BLACK, Color::rgb(1, 1, 1), 0.5));
        assert_eq!(
            Color::rgb(2, 2, 2),
            blend(Color::rgb(1, 1, 1), Color::rgb(2, 2, 2), 1.0)
        );
        assert_eq!(
            Color::rgb(2, 2, 2),
            blend(Color::rgb(1, 1, 1), Color::rgb(3, 3, 3), 0.5)
        );
    }

    #[test]
    fn blend_clamps_rather_than_extrapolating() {
        assert_eq!(Color::BLACK, blend(Color::BLACK, Color::WHITE, -2.0));
        assert_eq!(Color::WHITE, blend(Color::BLACK, Color::WHITE, 9.0));
    }

    #[test]
    fn contrast_matches_the_known_extremes() {
        assert!((contrast(Color::BLACK, Color::WHITE) - 21.0).abs() < 0.05);
        assert!((contrast(RED, RED) - 1.0).abs() < 0.0005);
    }

    #[test]
    fn readable_picks_the_end_that_actually_stands_out() {
        assert_eq!(Color::BLACK, readable(Color::WHITE, Color::BLACK, Color::WHITE));
        assert_eq!(Color::WHITE, readable(Color::BLACK, Color::BLACK, Color::WHITE));
    }

    #[test]
    fn a_tie_picks_the_first_candidate() {
        assert_eq!(Color::BLACK, readable(GRAY, Color::BLACK, Color::BLACK));
    }

    #[test]
    fn oklab_survives_a_round_trip() {
        // Red, SeaGreen, BlueViolet, Gainsboro.
        for c in [RED, Color::hex("#2E8B57"), Color::hex("#8A2BE2"), Color::hex("#DCDCDC")] {
            let (l, a, b) = to_oklab(c);
            let back = from_oklab(l, a, b);

            assert!(
                back.r.abs_diff(c.r) <= 1 && back.g.abs_diff(c.g) <= 1 && back.b.abs_diff(c.b) <= 1,
                "{c} came back as {back}"
            );
        }
    }

    #[test]
    fn a_grey_anchor_still_produces_a_usable_ramp() {
        // Chroma is floored, so a badly chosen anchor cannot yield a ramp of indistinguishable
        // greys.
        let ramp = ramp(GRAY, true, 5);

        assert_eq!(5, ramp.len());
        for c in ramp {
            assert!(to_oklch(c).1 > 0.0, "{c} is a pure grey");
        }
    }

    #[test]
    fn colours_parse_and_print_as_rrggbb() {
        assert_eq!(Some(Color::rgb(0x14, 0x17, 0x1C)), Color::parse("#14171c"));
        assert_eq!(Some(Color::rgb(0x14, 0x17, 0x1C)), Color::parse("14171C"));
        assert_eq!("#14171C", Color::rgb(0x14, 0x17, 0x1C).to_string());
        assert_eq!(None, Color::parse("#14171"));
        assert_eq!(None, Color::parse("#14171G"));
        assert_eq!(None, Color::parse(""));
    }
}
