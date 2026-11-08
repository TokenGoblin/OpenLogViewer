//! Minimal TrueType writer: only the tables a rasterizer needs, all hand-built.

use crate::{ADVANCE, ASCENT_ROWS, Error, Glyph, PIXEL, UNITS_PER_EM};

const COPYRIGHT: &str = "Copyright (c) 2026 OpenLogViewer contributors";
const FAMILY: &str = "OLV Retro";
const STYLE: &str = "Regular";
const FULL_NAME: &str = "OLV Retro Regular";
const POSTSCRIPT: &str = "OLVRetro-Regular";
const LICENSE: &str = "MIT";
/// 2026-01-01T00:00:00Z in seconds since 1904-01-01, so the output never depends on the clock.
const TIMESTAMP: i64 = 44561 * 86400;

fn u16be(out: &mut Vec<u8>, v: u16) {
    out.extend_from_slice(&v.to_be_bytes());
}
fn i16be(out: &mut Vec<u8>, v: i16) {
    out.extend_from_slice(&v.to_be_bytes());
}
fn u32be(out: &mut Vec<u8>, v: u32) {
    out.extend_from_slice(&v.to_be_bytes());
}

fn checksum(data: &[u8]) -> u32 {
    let mut sum = 0u32;
    for chunk in data.chunks(4) {
        let mut word = [0u8; 4];
        word[..chunk.len()].copy_from_slice(chunk);
        sum = sum.wrapping_add(u32::from_be_bytes(word));
    }
    sum
}

/// One simple glyph: every run of lit pixels is a clockwise rectangle contour.
fn glyph_data(g: &Glyph) -> Vec<u8> {
    let runs = g.runs();
    let Some((x_min, y_min, x_max, y_max)) = g.bbox() else {
        return Vec::new();
    };
    let mut points: Vec<(i16, i16)> = Vec::new();
    for r in &runs {
        let x0 = r.start as i16 * PIXEL;
        let x1 = (r.start + r.len) as i16 * PIXEL;
        let y1 = (ASCENT_ROWS - r.row as i16) * PIXEL;
        let y0 = y1 - PIXEL;
        points.extend([(x0, y0), (x0, y1), (x1, y1), (x1, y0)]);
    }
    let mut out = Vec::new();
    i16be(&mut out, runs.len() as i16);
    for v in [x_min, y_min, x_max, y_max] {
        i16be(&mut out, v);
    }
    for i in 0..runs.len() {
        u16be(&mut out, (i * 4 + 3) as u16);
    }
    u16be(&mut out, 0); // no instructions
    out.extend(std::iter::repeat_n(0x01u8, points.len())); // on-curve, 16-bit deltas
    let mut prev = 0;
    for &(x, _) in &points {
        i16be(&mut out, x - prev);
        prev = x;
    }
    let mut prev = 0;
    for &(_, y) in &points {
        i16be(&mut out, y - prev);
        prev = y;
    }
    while out.len() % 4 != 0 {
        out.push(0);
    }
    out
}

fn name_table() -> Vec<u8> {
    let records: [(u16, &str); 8] = [
        (0, COPYRIGHT),
        (1, FAMILY),
        (2, STYLE),
        (3, "OLVRetro-Regular;1.0"),
        (4, FULL_NAME),
        (5, "Version 1.0"),
        (6, POSTSCRIPT),
        (13, LICENSE),
    ];
    let mut strings = Vec::new();
    let mut header = Vec::new();
    u16be(&mut header, 0);
    u16be(&mut header, records.len() as u16);
    u16be(&mut header, (6 + 12 * records.len()) as u16);
    for (id, text) in records {
        let start = strings.len();
        for unit in text.encode_utf16() {
            u16be(&mut strings, unit);
        }
        for v in [3, 1, 0x409, id, (strings.len() - start) as u16, start as u16] {
            u16be(&mut header, v);
        }
    }
    header.extend(strings);
    header
}

/// Format 4 cmap (Windows Unicode BMP). Glyph ids follow code order, so each run of consecutive
/// code points is one segment with a constant delta.
fn cmap_table(codes: &[u32]) -> Vec<u8> {
    // (start, end, first glyph id)
    let mut segs: Vec<(u16, u16, u16)> = Vec::new();
    for (i, &c) in codes.iter().enumerate() {
        let gid = (i + 1) as u16;
        let c = c as u16;
        match segs.last_mut() {
            Some(s) if s.1.checked_add(1) == Some(c) => s.1 = c,
            _ => segs.push((c, c, gid)),
        }
    }
    segs.push((0xFFFF, 0xFFFF, 0));
    let n = segs.len() as u16;
    let mut sub = Vec::new();
    u16be(&mut sub, 4);
    u16be(&mut sub, 16 + 8 * n);
    u16be(&mut sub, 0);
    u16be(&mut sub, n * 2);
    let entry_selector = 15 - n.leading_zeros() as u16;
    let search_range = 2 * (1u16 << entry_selector);
    u16be(&mut sub, search_range);
    u16be(&mut sub, entry_selector);
    u16be(&mut sub, n * 2 - search_range);
    for s in &segs {
        u16be(&mut sub, s.1);
    }
    u16be(&mut sub, 0);
    for s in &segs {
        u16be(&mut sub, s.0);
    }
    for s in &segs {
        // The final 0xFFFF segment maps to glyph 0 via delta 1 (0xFFFF + 1 wraps to 0).
        let delta = if s.0 == 0xFFFF { 1 } else { s.2.wrapping_sub(s.0) };
        u16be(&mut sub, delta);
    }
    for _ in &segs {
        u16be(&mut sub, 0);
    }
    let mut out = Vec::new();
    u16be(&mut out, 0);
    u16be(&mut out, 2);
    for (platform, encoding) in [(0u16, 3u16), (3, 1)] {
        u16be(&mut out, platform);
        u16be(&mut out, encoding);
        u32be(&mut out, 20);
    }
    out.extend(sub);
    out
}

fn os2_table(first: u16, last: u16) -> Vec<u8> {
    let mut t = Vec::new();
    u16be(&mut t, 4); // version
    i16be(&mut t, ADVANCE as i16); // xAvgCharWidth
    u16be(&mut t, 400); // weight
    u16be(&mut t, 5); // width: medium
    u16be(&mut t, 0); // fsType: installable
    for v in [256, 256, 0, 64, 256, 256, 0, 384, 64, 320] {
        i16be(&mut t, v); // subscript, superscript, strikeout
    }
    i16be(&mut t, 0); // family class
    t.extend_from_slice(&[2, 0, 0, 9, 0, 0, 0, 0, 0, 0]); // PANOSE: monospaced
    // Basic Latin, Latin-1, Greek, General Punctuation | Arrows, Math, Technical | Shapes, Dingbats.
    u32be(&mut t, 1 | 2 | (1 << 7) | (1 << 31));
    u32be(
        &mut t,
        (1 << (37 - 32)) | (1 << (38 - 32)) | (1 << (39 - 32)) | (1 << (45 - 32)) | (1 << (46 - 32)),
    );
    u32be(&mut t, 0);
    u32be(&mut t, 0);
    t.extend_from_slice(b"OLV ");
    u16be(&mut t, 0x00C0); // REGULAR | USE_TYPO_METRICS
    u16be(&mut t, first);
    u16be(&mut t, last);
    i16be(&mut t, 768);
    i16be(&mut t, -256);
    i16be(&mut t, 0);
    u16be(&mut t, 768);
    u16be(&mut t, 256);
    u32be(&mut t, 1); // code pages: Latin 1
    u32be(&mut t, 0);
    i16be(&mut t, 7 * PIXEL); // x-height
    i16be(&mut t, 9 * PIXEL); // cap height
    u16be(&mut t, 0);
    u16be(&mut t, 0x20);
    u16be(&mut t, 1);
    t
}

/// Builds the TrueType font. `.notdef` becomes glyph 0; the rest follow in code-point order.
pub fn build_ttf(glyphs: &[Glyph]) -> Result<Vec<u8>, Error> {
    let notdef = glyphs
        .iter()
        .find(|g| g.code.is_none())
        .ok_or_else(|| Error::Font("no .notdef glyph".to_string()))?;
    let mut rest: Vec<&Glyph> = glyphs.iter().filter(|g| g.code.is_some()).collect();
    rest.sort_by_key(|g| g.code);
    let mut codes = Vec::new();
    for g in &rest {
        let c = g.code.unwrap_or(0);
        if c > 0xFFFE || codes.last() == Some(&c) {
            return Err(Error::Font(format!("code point U+{c:04X} is unusable")));
        }
        codes.push(c);
    }
    let ordered: Vec<&Glyph> = std::iter::once(notdef).chain(rest).collect();
    let num = ordered.len();

    let mut glyf = Vec::new();
    let mut loca = Vec::new();
    let mut hmtx = Vec::new();
    let (mut max_points, mut max_contours) = (0usize, 0usize);
    let (mut gx_min, mut gy_min, mut gx_max, mut gy_max) = (i16::MAX, i16::MAX, i16::MIN, i16::MIN);
    let (mut min_lsb, mut min_rsb, mut x_extent) = (i16::MAX, i16::MAX, i16::MIN);
    for g in &ordered {
        u32be(&mut loca, glyf.len() as u32);
        glyf.extend(glyph_data(g));
        let runs = g.runs().len();
        max_contours = max_contours.max(runs);
        max_points = max_points.max(runs * 4);
        u16be(&mut hmtx, ADVANCE);
        let lsb = match g.bbox() {
            Some((x0, y0, x1, y1)) => {
                gx_min = gx_min.min(x0);
                gy_min = gy_min.min(y0);
                gx_max = gx_max.max(x1);
                gy_max = gy_max.max(y1);
                min_rsb = min_rsb.min(ADVANCE as i16 - x1);
                x_extent = x_extent.max(x1);
                x0
            }
            None => 0,
        };
        min_lsb = min_lsb.min(lsb);
        i16be(&mut hmtx, lsb);
    }
    u32be(&mut loca, glyf.len() as u32);
    if gx_min > gx_max {
        (gx_min, gy_min, gx_max, gy_max) = (0, 0, 0, 0);
        (min_lsb, min_rsb, x_extent) = (0, 0, 0);
    }

    let mut head = Vec::new();
    u32be(&mut head, 0x0001_0000);
    u32be(&mut head, 0x0001_0000); // fontRevision 1.0
    u32be(&mut head, 0); // checkSumAdjustment, patched below
    u32be(&mut head, 0x5F0F_3CF5);
    u16be(&mut head, 0x000B); // baseline at 0, lsb at x=0, integer ppem
    u16be(&mut head, UNITS_PER_EM);
    head.extend_from_slice(&TIMESTAMP.to_be_bytes());
    head.extend_from_slice(&TIMESTAMP.to_be_bytes());
    for v in [gx_min, gy_min, gx_max, gy_max] {
        i16be(&mut head, v);
    }
    u16be(&mut head, 0); // macStyle
    u16be(&mut head, 8); // lowestRecPPEM
    i16be(&mut head, 2);
    i16be(&mut head, 1); // long loca
    i16be(&mut head, 0);

    let mut hhea = Vec::new();
    u32be(&mut hhea, 0x0001_0000);
    i16be(&mut hhea, 768);
    i16be(&mut hhea, -256);
    i16be(&mut hhea, 0);
    u16be(&mut hhea, ADVANCE);
    i16be(&mut hhea, min_lsb);
    i16be(&mut hhea, min_rsb);
    i16be(&mut hhea, x_extent);
    i16be(&mut hhea, 1);
    i16be(&mut hhea, 0);
    for _ in 0..5 {
        i16be(&mut hhea, 0); // caret offset + 4 reserved
    }
    i16be(&mut hhea, 0); // metricDataFormat
    u16be(&mut hhea, num as u16);

    let mut maxp = Vec::new();
    u32be(&mut maxp, 0x0001_0000);
    u16be(&mut maxp, num as u16);
    u16be(&mut maxp, max_points as u16);
    u16be(&mut maxp, max_contours as u16);
    for v in [0, 0, 1, 0, 0, 0, 0, 0, 0, 0] {
        u16be(&mut maxp, v);
    }

    let mut post = Vec::new();
    u32be(&mut post, 0x0003_0000);
    u32be(&mut post, 0);
    i16be(&mut post, -128);
    i16be(&mut post, PIXEL);
    u32be(&mut post, 1); // fixed pitch
    for _ in 0..4 {
        u32be(&mut post, 0);
    }

    let first = codes.first().copied().unwrap_or(0x20) as u16;
    let last = codes.last().copied().unwrap_or(0x20) as u16;
    let mut tables: Vec<([u8; 4], Vec<u8>)> = vec![
        (*b"OS/2", os2_table(first, last)),
        (*b"cmap", cmap_table(&codes)),
        (*b"glyf", glyf),
        (*b"head", head),
        (*b"hhea", hhea),
        (*b"hmtx", hmtx),
        (*b"loca", loca),
        (*b"maxp", maxp),
        (*b"name", name_table()),
        (*b"post", post),
    ];
    tables.sort_by_key(|t| t.0);

    let count = tables.len() as u16;
    let mut out = Vec::new();
    u32be(&mut out, 0x0001_0000);
    let entry_selector = 15 - count.leading_zeros() as u16;
    let search_range = 16 * (1u16 << entry_selector);
    u16be(&mut out, count);
    u16be(&mut out, search_range);
    u16be(&mut out, entry_selector);
    u16be(&mut out, count * 16 - search_range);
    let mut offset = 12 + 16 * tables.len();
    let mut head_offset = 0;
    for (tag, data) in &tables {
        if tag == b"head" {
            head_offset = offset;
        }
        out.extend_from_slice(tag);
        u32be(&mut out, checksum(data));
        u32be(&mut out, offset as u32);
        u32be(&mut out, data.len() as u32);
        offset += data.len().div_ceil(4) * 4;
    }
    for (_, data) in &tables {
        out.extend_from_slice(data);
        while out.len() % 4 != 0 {
            out.push(0);
        }
    }
    let adjust = 0xB1B0_AFBAu32.wrapping_sub(checksum(&out));
    out[head_offset + 8..head_offset + 12].copy_from_slice(&adjust.to_be_bytes());
    Ok(out)
}
