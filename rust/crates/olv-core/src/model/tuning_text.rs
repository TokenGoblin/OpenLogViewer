//! Ported from `TuningText.cs`: reads the text files the tuning world is made of.
//!
//! INIs and MSQ tunes are ISO-8859-1: an MSQ says so in its XML declaration, and
//! an INI does not say anything but is written that way regardless. Read as UTF-8
//! they mostly work, because most of the content is ASCII, and then a degree sign,
//! the one byte that matters in a units string, decodes to a replacement character
//! and every temperature channel is labelled "?F".
//!
//! UTF-8 is tried first all the same, since a modern file may well be, and only a
//! file that is not valid UTF-8 falls back.

use crate::Result;
use std::path::Path;

/// Reads a file and decodes it as [`decode`] does.
pub fn read(path: &Path) -> Result<String> {
    Ok(decode(&std::fs::read(path)?))
}

/// Strict UTF-8 without its byte order mark, or else Latin-1.
///
/// The Latin-1 fallback decodes the whole input, a byte order mark included, as
/// the C# does.
pub fn decode(bytes: &[u8]) -> String {
    let stripped = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    match std::str::from_utf8(stripped) {
        Ok(text) => text.to_string(),
        Err(_) => bytes.iter().map(|&b| char::from(b)).collect(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_latin1_degree_sign_survives() {
        assert_eq!(decode(b"units = \"\xB0F\""), "units = \"°F\"");
    }

    #[test]
    fn utf8_is_read_as_utf8_without_its_bom() {
        assert_eq!(decode("\u{feff}°C".as_bytes()), "°C");
    }
}
