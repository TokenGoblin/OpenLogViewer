//! The ASCII-art glyph source format.

use crate::{Error, GLYPH_H, GLYPH_W, Glyph};
use std::collections::HashSet;

/// Parses the source text. Blocks are a header (`U+0041 A` or `.notdef`) followed by 16 rows of
/// 8 characters (`#` lit, `.` clear). Blank lines and lines starting with `# ` are ignored
/// between blocks. A `.notdef` block is required; code points may not repeat.
pub fn parse_source(text: &str) -> Result<Vec<Glyph>, Error> {
    let mut glyphs = Vec::new();
    let mut seen = HashSet::new();
    let mut lines = text.lines().enumerate();
    while let Some((n, raw)) = lines.next() {
        let line = raw.trim_end();
        if line.is_empty() || line.starts_with("# ") || line == "#" {
            continue;
        }
        let code = if line == ".notdef" {
            None
        } else if let Some(rest) = line.strip_prefix("U+") {
            let hex = rest.split_whitespace().next().unwrap_or("");
            let value = u32::from_str_radix(hex, 16)
                .map_err(|_| Error::Source(format!("line {}: bad code point in header {line:?}", n + 1)))?;
            Some(value)
        } else {
            return Err(Error::Source(format!(
                "line {}: expected a glyph header, found {line:?}",
                n + 1
            )));
        };
        if !seen.insert(code) {
            return Err(Error::Source(format!("line {}: duplicate glyph {line:?}", n + 1)));
        }
        let mut rows = [0u8; GLYPH_H];
        for row in &mut rows {
            let Some((rn, r)) = lines.next() else {
                return Err(Error::Source(format!("line {}: glyph {line:?} is missing rows", n + 1)));
            };
            let r = r.trim_end();
            if r.chars().count() != GLYPH_W {
                return Err(Error::Source(format!(
                    "line {}: a row must be {GLYPH_W} characters, got {r:?}",
                    rn + 1
                )));
            }
            for (col, ch) in r.chars().enumerate() {
                match ch {
                    '#' => *row |= 0x80 >> col,
                    '.' => {}
                    _ => return Err(Error::Source(format!("line {}: unexpected {ch:?} in a row", rn + 1))),
                }
            }
        }
        glyphs.push(Glyph { code, rows });
    }
    if !seen.contains(&None) {
        return Err(Error::Source("no .notdef glyph".to_string()));
    }
    Ok(glyphs)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn block(head: &str, row: &str) -> String {
        format!("{head}\n{}\n", [row; 16].join("\n"))
    }

    #[test]
    fn parses_a_block() {
        let text = format!(
            "# c\n\n{}{}",
            block(".notdef", "#......."),
            block("U+0041 A", "..#.....")
        );
        let g = parse_source(&text).unwrap();
        assert_eq!(g.len(), 2);
        assert_eq!(g[1].code, Some(0x41));
        assert!(g[1].lit(2, 5));
        assert!(!g[1].lit(0, 5));
    }

    #[test]
    fn rejects_bad_input() {
        assert!(parse_source(&block("U+0041 A", "........")).is_err(), "missing .notdef");
        let dup = format!(
            "{}{}{}",
            block(".notdef", "........"),
            block("U+0041", "........"),
            block("U+0041", "........")
        );
        assert!(parse_source(&dup).is_err());
        assert!(parse_source(&block(".notdef", ".......")).is_err());
        assert!(parse_source("U+0041 A\n........\n").is_err());
    }
}
