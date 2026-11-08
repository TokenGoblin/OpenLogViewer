//! Ported from `MsqWriter.cs`. Owned by package `w4-tune`.
//!
//! Writes a tune out as an `.msq`.
//!
//! The other half of reading one, and the thing that makes a controller's settings yours
//! rather than the controller's: a tune that exists only in an ECU is one power supply away
//! from being gone. It is also the format every other tool in this world reads, so a tune
//! saved here opens in TunerStudio and can be sent to whoever is helping.
//!
//! Values are written the way a person reads them, not as bytes, because that is what the
//! format is for: a file full of raw pages would say nothing without the definition beside it,
//! and the whole point of an MSQ is that a human and another program can both make sense of
//! it. A bit field is written as the name of the option chosen, and a number with as many
//! decimals as it takes to get the same byte back, which is not the same as the decimals the
//! firmware shows.
//!
//! The conditional symbols go in too. A tune written without them is a tune that cannot be
//! read back correctly: the reader would have to guess which build it came from, and guessing
//! wrong scales every temperature in it.
//!
//! # The bibliography line
//!
//! The one part of the file that is not a function of the tune is `<bibliography>`: who wrote
//! it and when. The C# takes the assembly's version and the machine's clock and time zone.
//! [`MsqStamp::now`] does the same here, except that the clock is UTC and says so (std has no
//! local time zone, and the layer above can pass its own with
//! [`write_stamped`]). Everything else is byte for byte what the C# writes.

use super::ecu_tune::EcuTune;
use super::msq_apply::winners;
use super::tune_version::civil;
use crate::definitions::msq_file::MsqFile;
use crate::fmt;
use crate::ini::msq_ini::{RealtimeType, SymbolSet};
use crate::ini::tune_layout::TuneConstant;
use crate::model::eq_ignore_case;
use crate::{Error, Result};
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

/// What the format calls itself. Written as TunerStudio writes it.
const FILE_FORMAT: &str = "5.0";

/// Who wrote the file and when: the `author` and `writeDate` of `<bibliography>`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MsqStamp {
    pub author: String,
    pub write_date: String,
}

impl MsqStamp {
    /// This program and this moment, in the C#'s shapes: `OpenLogViewer - 1.0.0` and
    /// `Sat Nov 08 12:34:56 UTC 2026`. The C# prints the machine's local time and its zone's
    /// standard name; this prints UTC.
    pub fn now() -> Self {
        let seconds = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs() as i64)
            .unwrap_or(0);

        MsqStamp {
            author: format!("OpenLogViewer - {}", env!("CARGO_PKG_VERSION")),
            write_date: stamp(seconds, "UTC"),
        }
    }
}

/// `ddd MMM dd HH:mm:ss <zone> yyyy`, as the C# builds it.
pub fn stamp(unix_seconds: i64, zone: &str) -> String {
    const DAYS: [&str; 7] = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    const MONTHS: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    let c = civil(unix_seconds);

    format!(
        "{} {} {:02} {:02}:{:02}:{:02} {} {:04}",
        DAYS[c.weekday as usize],
        MONTHS[(c.month - 1) as usize],
        c.day,
        c.hour,
        c.minute,
        c.second,
        zone,
        c.year
    )
}

/// Turns a tune into the text of an MSQ.
///
/// `signature` is the firmware signature, which is what tells a reader which definition this
/// belongs to. Without it the file is a list of numbers nobody can place. `symbols` is the
/// build's conditional symbols. `comment` is whatever the tuner wants to say about it.
///
/// `source` is a file this was read from, if there was one. Its PC variables and its firmware
/// string are carried across: they belong to the tune and are not in the controller, so they
/// would otherwise be lost by the round trip through an ECU.
pub fn write(
    tune: &EcuTune,
    signature: &str,
    symbols: Option<&SymbolSet>,
    comment: &str,
    source: Option<&MsqFile>,
) -> String {
    write_stamped(tune, signature, symbols, comment, source, &MsqStamp::now())
}

/// [`write`] with the author and date given rather than taken from the clock.
pub fn write_stamped(
    tune: &EcuTune,
    signature: &str,
    symbols: Option<&SymbolSet>,
    comment: &str,
    source: Option<&MsqFile>,
    stamp: &MsqStamp,
) -> String {
    let layout = tune.layout();
    let mut text = String::with_capacity(1 << 20);

    text.push_str("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\n");
    text.push_str("<msq xmlns=\"http://www.msefi.com/:msq\">\n");

    text.push_str("<bibliography author=\"");
    text.push_str(&attribute(&stamp.author));
    text.push_str("\" tuneComment=\"");
    text.push_str(&attribute(comment));
    text.push_str("\" writeDate=\"");
    text.push_str(&attribute(&stamp.write_date));
    text.push_str("\"/>\n");

    text.push_str("<versionInfo fileFormat=\"");
    text.push_str(FILE_FORMAT);
    text.push_str("\" firmwareInfo=\"");
    text.push_str(&attribute(source.map(|s| s.firmware.as_str()).unwrap_or("")));
    text.push_str("\" nPages=\"");
    text.push_str(&fmt::int_inv(layout.pages.len() as i32, ""));
    text.push_str("\" signature=\"");
    text.push_str(&attribute(signature));
    text.push_str("\"/>\n");

    // The settings that live on this machine rather than in the ECU. Written first, in a page
    // with no number, exactly as the format has them.
    if let Some(source) = source
        && !source.pc_variables.is_empty()
    {
        text.push_str("<page>\n");

        for (name, value) in source.pc_variables.iter() {
            text.push_str("<pcVariable name=\"");
            text.push_str(&attribute(name));
            text.push_str("\">");
            text.push_str(&escape(value));
            text.push_str("</pcVariable>\n");
        }

        text.push_str("</page>\n");
    }

    // Where a name is declared twice the later one wins, which is how it resolves everywhere
    // else. Spelled exactly: two settings differing only in case are two settings, and
    // MS2Extra really has a pair.
    let winners = winners(&layout.constants);

    for page in &layout.pages {
        text.push_str("<page number=\"");
        text.push_str(&fmt::int_inv(page.index, ""));
        text.push_str("\" size=\"");
        text.push_str(&fmt::int_inv(page.size, ""));
        text.push_str("\">\n");

        for (i, constant) in layout.constants.iter().enumerate() {
            if constant.page != page.index {
                continue;
            }
            if winners.get(constant.name.as_str()) != Some(&i) {
                continue;
            }

            write_constant(&mut text, tune, constant);
        }

        text.push_str("</page>\n");
    }

    if let Some(symbols) = symbols
        && !symbols.is_empty()
    {
        text.push_str("<settings>\n");

        // Ordinal order: by UTF-16 code unit, as `StringComparer.Ordinal` sorts.
        let mut sorted: Vec<&str> = symbols.iter().collect();
        sorted.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));

        for symbol in sorted {
            text.push_str("<setting name=\"");
            text.push_str(&attribute(symbol));
            text.push_str("\" value=\"");
            text.push_str(&attribute(symbol));
            text.push_str("\"/>\n");
        }

        text.push_str("</settings>\n");
    }

    text.push_str("</msq>\n");

    text
}

/// Saves it, in the encoding the tuning world writes these in.
///
/// ISO-8859-1, which is what the declaration at the top says and what every other tool
/// expects. A tune whose text will not fit in it (which would take a name in a script this
/// application does not otherwise handle) is written as UTF-8 and says so, because a file that
/// is honest about its encoding beats one that quietly replaces the characters it cannot
/// spell.
pub fn save(
    path: &Path,
    tune: &EcuTune,
    signature: &str,
    symbols: Option<&SymbolSet>,
    comment: &str,
    source: Option<&MsqFile>,
) -> Result<()> {
    save_text(path, &write(tune, signature, symbols, comment, source))
}

/// The encoding half of [`save`], for text already written.
pub fn save_text(path: &Path, xml: &str) -> Result<()> {
    let bytes: Vec<u8> = if xml.chars().all(|c| (c as u32) <= 0xFF) {
        xml.chars().map(|c| c as u32 as u8).collect()
    } else {
        xml.replace("encoding=\"ISO-8859-1\"", "encoding=\"UTF-8\"")
            .into_bytes()
    };

    std::fs::write(path, bytes).map_err(Error::Io)
}

/// Whether every value of a constant can actually be read.
///
/// One that cannot is left out of the file rather than written as a zero. A float sitting in
/// erased flash reads as NaN and a constant whose declared offset falls outside its page reads
/// as nothing at all: recording either as "0.0" puts a number in the backup the ECU does not
/// hold, and restoring that file writes a genuine zero over it. Saying nothing is the truth,
/// and the reader already knows what to do with a setting a file does not mention: leave the
/// controller's own alone.
fn readable(tune: &EcuTune, constant: &TuneConstant) -> bool {
    if constant.is_text {
        return tune.text_in(tune.pages(), &constant.name).is_some();
    }

    let cells = (constant.columns * constant.rows).max(1);

    for i in 0..cells {
        match tune.value_in(tune.pages(), &constant.name, i) {
            Some(v) if v.is_finite() => {}
            _ => return false,
        }
    }

    true
}

/// One constant, written the way a person reads it.
fn write_constant(text: &mut String, tune: &EcuTune, declared: &TuneConstant) {
    // The tune's copy, not the definition's. A scale the firmware wrote as an expression is
    // worked out from the values, and writing a number against the declared fallback while
    // reading it back against the worked-out one loses the round trip by whatever the
    // expression came to: 120 bytes of 7,168 on a MicroSquirt, all of them the MAF curve.
    let constant = tune.constant(&declared.name).unwrap_or(declared);

    if !readable(tune, constant) {
        return;
    }

    text.push_str("<constant");

    if constant.is_text {
        text.push_str(" name=\"");
        text.push_str(&attribute(&constant.name));
        text.push_str("\">\"");
        text.push_str(&escape(&tune.text_in(tune.pages(), &constant.name).unwrap_or_default()));
        text.push_str("\"</constant>\n");

        return;
    }

    let columns = constant.columns.max(1);
    let rows = constant.rows.max(1);
    let cells = columns * rows;

    if cells > 1 {
        text.push_str(" cols=\"");
        text.push_str(&fmt::int_inv(columns, ""));
        text.push_str("\" digits=\"");
        text.push_str(&fmt::int_inv(constant.digits, ""));
        text.push('"');
    } else if !constant.has_options() {
        text.push_str(" digits=\"");
        text.push_str(&fmt::int_inv(constant.digits, ""));
        text.push('"');
    }

    text.push_str(" name=\"");
    text.push_str(&attribute(&constant.name));
    text.push('"');

    if cells > 1 {
        text.push_str(" rows=\"");
        text.push_str(&fmt::int_inv(rows, ""));
        text.push('"');
    }

    if !constant.units.is_empty() {
        text.push_str(" units=\"");
        text.push_str(&attribute(&constant.units));
        text.push('"');
    }

    text.push('>');

    if cells > 1 {
        // Row-major and laid out a row to a line, which is how the format writes a grid and
        // how a person reads one.
        text.push('\n');

        for row in 0..rows {
            for column in 0..columns {
                text.push(' ');
                text.push_str(&number(
                    tune.value_in(tune.pages(), &constant.name, row * columns + column),
                    constant,
                ));
                text.push(' ');
            }

            text.push('\n');
        }
    } else {
        text.push_str(&single(tune, constant));
    }

    text.push_str("</constant>\n");
}

/// A lone value: the name of the option for a bit field, else the number.
///
/// A label is only written when it says which value it is. Firmwares pad an option list out to
/// the width of the field, so the same word appears many times over: Speeduino's pin list has
/// "INVALID" at 1, 2, 54 and 63, and an unconfigured pin really does read 63. Writing the word
/// and reading it back finds the first one, so a backup of a pin set to "none" restores as pin
/// 1: a real output, quietly assigned. The number cannot do that.
///
/// The same for a value the firmware names not at all, which is a real thing to find in an
/// ECU: a setting an older revision wrote, or one left blank.
fn single(tune: &EcuTune, constant: &TuneConstant) -> String {
    let value = tune.value_in(tune.pages(), &constant.name, 0);

    if constant.has_options()
        && let Some(chosen) = value
    {
        let index = chosen.round_ties_even() as i32;

        if index >= 0 && (index as usize) < constant.options.len() && names(constant, index as usize) {
            return format!("\"{}\"", escape(&constant.options[index as usize]));
        }
    }

    number(value, constant)
}

/// Whether this option's label points back at this option and no earlier one.
///
/// Matched the way a reader matches it (ignoring case, first one wins) so that the question
/// asked is the one that decides the round trip.
fn names(constant: &TuneConstant, index: usize) -> bool {
    let label = &constant.options[index];
    if label.is_empty() {
        return false;
    }

    !constant.options[..index].iter().any(|o| eq_ignore_case(o, label))
}

/// A number written with enough decimals to get the same byte back.
///
/// **Not the firmware's `digits`.** That says how many decimals to show a person, and it is
/// routinely fewer than the storage needs: MS2 keeps a dwell in sixty-sixths of a millisecond
/// and asks for one decimal, so a raw 56 is 3.7296 ms shown as "3.7", and 3.7 read back is a
/// raw 55. Writing what is displayed rather than what is stored quietly moves settings by one
/// step every time a tune goes through a file, which is exactly the sort of drift nobody
/// attributes to the tool. TunerStudio's own files show the same: `digits="1"` beside a value
/// of 3.7296.
///
/// So the shortest form that reconstructs the stored number is used, found by trying. Most
/// settings need one decimal and get one; the odd scale gets however many it takes.
fn number(value: Option<f64>, constant: &TuneConstant) -> String {
    let Some(v) = value.filter(|v| v.is_finite()) else {
        return "0.0".to_string();
    };

    let wanted = constant.digits.max(1).clamp(1, 9);

    for digits in wanted..=9 {
        let written = fmt::f64_inv(v, &format!("F{digits}"));

        if let Some(back) = fmt::parse_f64_inv(&written)
            && stored(back, constant) == stored(v, constant)
        {
            return written;
        }
    }

    // Nothing short enough reconstructs it, so write the number itself and let the reader
    // round as it must. Reached only for a scale so fine that nine decimals will not separate
    // two neighbouring bytes.
    fmt::f64_inv(v, "R")
}

/// The number that would actually be stored for a displayed value: the same arithmetic a write
/// to the controller does, so that "does this round trip" is asked of the bytes rather than of
/// the text.
///
/// Rounds half away from zero, as the encoder does. (The C# check rounded ties to even, so on
/// an exact .5 the check and the bytes could disagree and the check passed or failed on the
/// wrong value.)
fn stored(value: f64, constant: &TuneConstant) -> f64 {
    let scaled = if constant.scale != 0.0 {
        (value / constant.scale) - constant.transform
    } else {
        value
    };

    if constant.type_ == RealtimeType::F32 {
        scaled
    } else {
        scaled.round()
    }
}

fn escape(text: &str) -> String {
    text.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;")
}

fn attribute(text: &str) -> String {
    escape(text).replace('"', "&quot;")
}

#[cfg(test)]
mod tests {
    use super::*;

    // Defect fixed in the Rust: the round-trip check rounded ties to even while the encoder
    // rounds half away from zero.
    #[test]
    fn the_round_trip_check_rounds_the_way_the_encoder_does() {
        let constant = TuneConstant::new("x", 0, 0, RealtimeType::U08);

        // Raw 2.5 is stored as 3 by the encoder.
        assert_eq!(stored(2.5, &constant), 3.0);
        assert_eq!(stored(3.5, &constant), 4.0);

        // 2.4999999999 stores as 2, so "2.5" (which stores as 3) must not be accepted for it.
        let written = number(Some(2.4999999999), &constant);
        assert_ne!(written, "2.5");
        assert!(fmt::parse_f64_inv(&written).is_some_and(|back| stored(back, &constant) == 2.0));
    }

    #[test]
    fn the_stamp_reads_like_the_csharp_builds_it() {
        // `ddd MMM dd HH:mm:ss <zone> yyyy`: day-of-month padded to two, zone in the middle.
        assert_eq!(stamp(1_794_000_000, "UTC"), "Fri Nov 06 21:20:00 UTC 2026");
        assert_eq!(stamp(951_782_400, "EST"), "Tue Feb 29 00:00:00 EST 2000");
        assert_eq!(stamp(-86_400, "UTC"), "Wed Dec 31 00:00:00 UTC 1969");
        assert_eq!(stamp(0, "UTC"), "Thu Jan 01 00:00:00 UTC 1970");
    }

    #[test]
    fn the_default_author_names_the_program_and_its_version() {
        let now = MsqStamp::now();
        assert_eq!(now.author, format!("OpenLogViewer - {}", env!("CARGO_PKG_VERSION")));
        assert!(
            now.write_date
                .ends_with(&format!(" UTC {}", &now.write_date[now.write_date.len() - 4..]))
        );
    }
}
