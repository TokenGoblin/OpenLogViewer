//! Ported from `RealtimeCommand.cs`: the INI's `ochGetCommand`, turned into the
//! bytes that ask for a block.
//!
//! Every firmware asks differently. MegaSquirt reads a page with
//! `r\$tsCanId\x07%2o%2c`; rusEFI has a command of its own, `O%2o%2c`; an older
//! MS2 on plain serial sends `A` and gets the lot. The template is in the INI
//! precisely so that a reader does not have to know which: building the request
//! from it, rather than from a hard-coded guess, is the whole of what makes one
//! program able to talk to both.

use crate::{Error, Result, fmt};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Part {
    /// A byte written as it stands.
    Literal,
    /// The controller's CAN id: `\$tsCanId`.
    CanId,
    /// Two bytes: where in the block to start, `%2o`.
    Offset,
    /// Two bytes: how much of it to send, `%2c`.
    Count,
    /// Two bytes: which page, `%2i`. Always the realtime one.
    Page,
    /// The bytes being written, `%v`. Only a write command has one.
    Payload,
}

/// A parsed command template.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RealtimeCommand {
    parts: Vec<(Part, u8)>,
    takes_range: bool,
}

/// The plain MegaSquirt page read, for a layout that declares no template.
impl Default for RealtimeCommand {
    fn default() -> Self {
        Self::parse("r\\$tsCanId\\x07%2o%2c")
    }
}

impl RealtimeCommand {
    /// Whether the command can ask for part of the block.
    ///
    /// False for a template like `A`, which returns everything and has nowhere to
    /// put an offset: such a firmware cannot have its block read in pieces,
    /// however large it is.
    pub fn takes_range(&self) -> bool {
        self.takes_range
    }

    /// Parses a template. The C# works on UTF-16 code units, and a literal is the
    /// low byte of one; this does the same.
    pub fn parse(template: &str) -> Self {
        let text: Vec<u16> = template.trim().trim_matches('"').encode_utf16().collect();
        let mut parts = Vec::new();

        let mut i = 0;
        while i < text.len() {
            let c = text[i];

            if c == u16::from(b'%')
                && let Some((field, length)) = try_field(&text, i)
            {
                parts.push((field, 0));
                i += length;
                continue;
            }

            if c == u16::from(b'\\')
                && let Some((escape, length)) = try_escape(&text, i)
            {
                // An escape may resolve to nothing: an unrecognised variable is
                // consumed and skipped rather than guessed at, since a wrong byte
                // in the request asks for something else entirely.
                if let Some(resolved) = escape {
                    parts.push(resolved);
                }
                i += length;
                continue;
            }

            parts.push((Part::Literal, c as u8));
            i += 1;
        }

        let takes_range = parts.iter().any(|(p, _)| matches!(p, Part::Offset | Part::Count));
        Self { parts, takes_range }
    }

    /// Builds one request.
    ///
    /// `little_endian` comes from the INI's `endianness`, and applies to the
    /// offset and the count as much as to the data coming back: rusEFI reads both
    /// ends the same way round. Sending them the wrong way round does not produce
    /// an error you can recognise: a count of 1824 arrives as 8199, which the
    /// firmware refuses as out of range, and a count of 1024 arrives as 4, which it
    /// honours.
    ///
    /// `page` supplies the bytes for a `%2i`, which is not a number but the page's
    /// own identifier string: `\x00\x00` on a rusEFI, `\$tsCanId\x04` on a
    /// MegaSquirt. A realtime template names its page with a literal and passes an
    /// empty slice. The C#'s defaults are `can_id` 0, big-endian, no page and no
    /// payload.
    pub fn build(
        &self,
        offset: i32,
        count: i32,
        can_id: u8,
        little_endian: bool,
        page: &[u8],
        payload: &[u8],
    ) -> Result<Vec<u8>> {
        if offset < 0 {
            return Err(Error::Argument(format!(
                "offset ('{offset}') must be a non-negative value."
            )));
        }
        if count < 1 {
            return Err(Error::Argument(format!(
                "count ('{count}') must be greater than or equal to '1'."
            )));
        }

        let mut request = Vec::with_capacity(self.parts.len() + 6 + payload.len());

        for &(kind, value) in &self.parts {
            match kind {
                Part::Literal => request.push(value),
                Part::CanId => request.push(can_id),
                Part::Offset => write(&mut request, offset, little_endian),
                Part::Count => write(&mut request, count, little_endian),
                Part::Page => {
                    if page.is_empty() {
                        write(&mut request, 0, little_endian);
                    } else {
                        request.extend_from_slice(page);
                    }
                }
                Part::Payload => request.extend_from_slice(payload),
            }
        }

        Ok(request)
    }
}

fn write(request: &mut Vec<u8>, value: i32, little_endian: bool) {
    let high = (value >> 8) as u8;
    let low = value as u8;

    if little_endian {
        request.extend_from_slice(&[low, high]);
    } else {
        request.extend_from_slice(&[high, low]);
    }
}

fn try_field(text: &[u16], at: usize) -> Option<(Part, usize)> {
    if at + 1 >= text.len() {
        return None;
    }

    // "%v" is the data itself and has no width.
    if text[at + 1] == u16::from(b'v') {
        return Some((Part::Payload, 2));
    }

    if at + 2 >= text.len() || text[at + 1] != u16::from(b'2') {
        return None;
    }

    let field = match u8::try_from(text[at + 2]).ok()? {
        b'o' => Part::Offset,
        b'c' => Part::Count,
        b'i' => Part::Page,
        _ => return None,
    };
    Some((field, 3))
}

/// Reads one backslash escape. `None` when it is not one, in which case the
/// backslash is a literal; `Some((None, length))` when the escape is understood
/// but contributes no byte.
fn try_escape(text: &[u16], at: usize) -> Option<(Option<(Part, u8)>, usize)> {
    if at + 1 >= text.len() {
        return None;
    }

    // \xNN, a byte in hex, which is how a page number is written.
    if (text[at + 1] == u16::from(b'x') || text[at + 1] == u16::from(b'X'))
        && at + 3 < text.len()
        && let Some(value) = fmt::parse_hex_u8(&String::from_utf16_lossy(&text[at + 2..at + 4]))
    {
        return Some((Some((Part::Literal, value)), 4));
    }

    // \$name, a variable. tsCanId is the only one these templates use.
    if text[at + 1] == u16::from(b'$') {
        let mut end = at + 2;
        while end < text.len() && is_name_char(text[end]) {
            end += 1;
        }

        let name = String::from_utf16_lossy(&text[at + 2..end]);
        let part = name.eq_ignore_ascii_case("tsCanId").then_some((Part::CanId, 0));
        return Some((part, end - at));
    }

    None
}

/// `char.IsLetterOrDigit(c) || c == '_'` on one UTF-16 unit; a surrogate is
/// neither.
fn is_name_char(unit: u16) -> bool {
    unit == u16::from(b'_') || char::from_u32(u32::from(unit)).is_some_and(char::is_alphanumeric)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_megasquirt_default_reads_a_range_of_page_seven() {
        let command = RealtimeCommand::default();
        assert!(command.takes_range());
        assert_eq!(
            command.build(0x0102, 0x0304, 5, false, &[], &[]).unwrap(),
            [b'r', 5, 0x07, 0x01, 0x02, 0x03, 0x04]
        );
    }

    #[test]
    fn rusefi_reads_offset_and_count_little_endian() {
        let command = RealtimeCommand::parse("\"O%2o%2c\"");
        assert_eq!(
            command.build(0, 1824, 0, true, &[], &[]).unwrap(),
            [b'O', 0, 0, 0x20, 0x07]
        );
    }

    #[test]
    fn a_whole_block_command_takes_no_range() {
        let command = RealtimeCommand::parse("A");
        assert!(!command.takes_range());
        assert_eq!(command.build(0, 1, 0, false, &[], &[]).unwrap(), b"A");
    }

    #[test]
    fn unknown_variables_are_skipped_and_page_and_payload_are_spliced() {
        let command = RealtimeCommand::parse("M\\$other%2i%2o%v");
        assert_eq!(
            command.build(1, 1, 0, false, &[0xAA, 0xBB], &[9, 8]).unwrap(),
            [b'M', 0xAA, 0xBB, 0, 1, 9, 8]
        );
        assert_eq!(command.build(0, 1, 0, false, &[], &[]).unwrap(), [b'M', 0, 0, 0, 0]);
    }

    #[test]
    fn a_negative_offset_or_empty_count_is_refused() {
        let command = RealtimeCommand::default();
        assert!(command.build(-1, 1, 0, false, &[], &[]).is_err());
        assert!(command.build(0, 0, 0, false, &[], &[]).is_err());
    }
}
