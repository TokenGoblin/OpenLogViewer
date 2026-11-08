//! Ported from `MaxxLogReader.cs`. Owned by package `w2-logs`.
//!
//! Reads a MaxxECU log, which is a zip holding three things.
//!
//! * `*.MaxxECU-Log`: tab-separated text, a header of channel names and then a
//!   row per sample.
//! * `*.LogMetaData`: one line, `LogRate=` the seconds between samples. There is
//!   no time column, so this is the only thing that says when anything happened.
//! * `*.MaxxECU-save`: the tune that was running.
//!
//! Each header names its channel and gives MTune's index for it in brackets:
//! `Coolant temp [18]`. The index is what MTune's channel table is keyed by, so
//! units come from the same definitions the live gauges use rather than being
//! guessed from the name.
//!
//! The C# reads the archive with `System.IO.Compression`; there is no such thing
//! in the standard library, so [`zip`] below is a minimal reader of the central
//! directory (stored and deflate entries, no Zip64, no encryption).

use super::log_reader_factory::file_name;
use super::maxx_channel_table;
use crate::fmt;
use crate::model::{LogChannel, LogDocument, LogReader};
use crate::{Error, Result};
use std::collections::HashSet;
use std::path::Path;
use std::sync::Arc;

/// The largest log this will expand from an archive. Generous: the sample logs
/// are tens of megabytes, and a day's continuous recording is not close to this.
const MAXIMUM_UNCOMPRESSED: u64 = 512 * 1024 * 1024;

/// The reader for MaxxECU zip logs.
#[derive(Clone, Copy, Debug, Default)]
pub struct MaxxLogReader;

impl MaxxLogReader {
    pub fn new() -> Self {
        Self
    }
}

impl LogReader for MaxxLogReader {
    fn format_name(&self) -> &str {
        "MaxxECU"
    }

    /// True for a zip that contains a MaxxECU log.
    ///
    /// The contents are checked rather than the extension: these arrive as
    /// `.MaxxECU-Zip-log`, but a zip renamed by a browser or an email client is
    /// still the same file.
    fn can_read(&self, path: &Path) -> bool {
        match zip::Archive::open(path) {
            Ok(archive) => find_log(&archive).is_some(),
            Err(_) => false,
        }
    }

    fn read(&self, path: &Path) -> Result<LogDocument> {
        let archive = zip::Archive::open(path)?;

        let Some(log) = find_log(&archive) else {
            return Err(Error::LogFormat(format!(
                "'{}' is a zip but holds no MaxxECU log.",
                file_name(path)
            )));
        };

        // A zip says how large its contents are before any of it is read, and
        // that is worth asking. Compressed data expands by a factor of a
        // thousand or more, so a file small enough to arrive by email can ask
        // for more memory than the machine has. A real 22 MB log declares 22 MB.
        //
        // The figure is what the archive claims rather than what it delivers, so
        // this catches the careless case rather than a determined one.
        if log.uncompressed_size > MAXIMUM_UNCOMPRESSED {
            return Err(Error::LogFormat(format!(
                "'{}' expands to {} MB, past the {} MB this will open. A zip that small holding a log that large is more often corrupt than real.",
                file_name(path),
                fmt::long((log.uncompressed_size / (1024 * 1024)) as i64, "N0"),
                fmt::long((MAXIMUM_UNCOMPRESSED / (1024 * 1024)) as i64, "N0"),
            )));
        }

        let interval = read_log_rate(&archive);
        let (names, ids, columns, rows) = read_columns(&archive.extract(log)?);

        if names.is_empty() || rows == 0 {
            return Err(Error::LogFormat(format!("'{}' holds no samples.", file_name(path))));
        }

        let units = maxx_channel_table::units();

        let channels: Vec<Arc<LogChannel>> = names
            .iter()
            .zip(&ids)
            .zip(columns)
            .map(|((name, id), column)| {
                let unit = units.get(id).map_or("", String::as_str);
                Arc::new(LogChannel::adopt(name, unit, 3, column))
            })
            .collect();

        // Built from the rate, because the file carries no time of its own. A
        // log with no rate would otherwise plot against sample number, which
        // looks like seconds and is not.
        let time: Vec<f64> = (0..rows).map(|i| i as f64 * interval).collect();

        // The zip carries the tune that was running, as a `.MaxxECU-save`. That
        // is MTune's own format and nothing here can read it, so what is recorded
        // is that it exists rather than a guess at what is in it. Decoding it
        // wrong would not fail; it would produce plausible breakpoints and a VE
        // suggestion built on them.
        let unreadable = find_tune(&archive).map(|_| "MaxxECU".to_string());

        let recorded_at = std::fs::metadata(path)?.modified()?;

        Ok(LogDocument::new(
            &path.to_string_lossy(),
            "MaxxECU",
            channels,
            Arc::new(LogChannel::new("Time", "s", 3, &time, true)),
        )
        .with_unreadable_tune(unreadable)
        .with_recorded_at(Some(recorded_at)))
    }
}

/// The tune the log was recorded with, which this can see but not read.
fn find_tune(archive: &zip::Archive) -> Option<&zip::Entry> {
    archive
        .entries()
        .iter()
        .find(|e| ends_with_ignore_case(&e.name, ".MaxxECU-save"))
}

fn find_log(archive: &zip::Archive) -> Option<&zip::Entry> {
    archive
        .entries()
        .iter()
        .find(|e| ends_with_ignore_case(&e.name, ".MaxxECU-Log"))
}

fn ends_with_ignore_case(s: &str, suffix: &str) -> bool {
    s.to_uppercase().ends_with(&suffix.to_uppercase())
}

/// Seconds between samples, from the metadata entry.
///
/// Falls back to a hundredth of a second when it is missing or unreadable:
/// wrong, but wrong by a constant, which keeps every trace the right shape and
/// only mislabels the axis.
fn read_log_rate(archive: &zip::Archive) -> f64 {
    const FALLBACK: f64 = 0.01;

    let Some(meta) = archive
        .entries()
        .iter()
        .find(|e| ends_with_ignore_case(&e.name, ".LogMetaData"))
    else {
        return FALLBACK;
    };

    let Ok(bytes) = archive.extract(meta) else {
        return FALLBACK;
    };
    let text = decode_ascii(&bytes);

    for line in read_lines(&text) {
        let Some(equals) = line.find('=') else {
            continue;
        };

        if !crate::model::eq_ignore_case(line[..equals].trim(), "LogRate") {
            continue;
        }

        return match fmt::parse_f64_inv(line[equals + 1..].trim()) {
            Some(rate) if rate > 0.0 => rate,
            _ => FALLBACK,
        };
    }

    FALLBACK
}

/// `StreamReader(stream, Encoding.ASCII)`: bytes above 0x7F decode to `?`, but a
/// UTF-8 byte order mark switches the reader to UTF-8, as the C# one does.
fn decode_ascii(bytes: &[u8]) -> String {
    if let Some(rest) = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
        return String::from_utf8_lossy(rest).into_owned();
    }
    bytes
        .iter()
        .map(|&b| if b < 0x80 { char::from(b) } else { '?' })
        .collect()
}

/// `StreamReader.ReadLine`: breaks on `\r`, `\n` and `\r\n`; a final line with
/// no terminator counts, an empty one after the last break does not.
fn read_lines(text: &str) -> Vec<&str> {
    let mut lines = Vec::new();
    let b = text.as_bytes();
    let mut start = 0;
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'\r' || b[i] == b'\n' {
            lines.push(&text[start..i]);
            if b[i] == b'\r' && b.get(i + 1) == Some(&b'\n') {
                i += 1;
            }
            start = i + 1;
        }
        i += 1;
    }
    if start < b.len() {
        lines.push(&text[start..]);
    }
    lines
}

/// .NET's `float.NaN` has its sign bit set; Rust's `f32::NAN` does not.
const DOTNET_NAN: f32 = f32::from_bits(0xFFC0_0000);

type Columns = (Vec<String>, Vec<i32>, Vec<Vec<f32>>, usize);

/// Reads the header and every row.
///
/// Grown into lists rather than sized up front: the row count is not stated
/// anywhere and a 22 MB log holds several thousand of them, so counting first
/// would mean reading the whole thing twice.
fn read_columns(bytes: &[u8]) -> Columns {
    let text = decode_ascii(bytes);
    let mut lines = read_lines(&text).into_iter();

    let Some(header) = lines.next() else {
        return (vec![], vec![], vec![], 0);
    };

    let titles: Vec<&str> = header.split('\t').collect();

    let mut names: Vec<String> = Vec::with_capacity(titles.len());
    let mut ids: Vec<i32> = Vec::with_capacity(titles.len());
    let mut growing: Vec<Vec<f32>> = Vec::with_capacity(titles.len());
    let mut taken: HashSet<String> = HashSet::new();

    // The header ends with a tab, so the last field is empty.
    for (i, raw) in titles.iter().enumerate() {
        let title = raw.trim();
        if title.is_empty() {
            continue;
        }

        let (mut name, id) = split_index(title);

        // Duplicate names would collide in every preset and filter.
        if !taken.insert(fold(&name)) {
            name = format!("{name} ({i})");
        }

        names.push(name);
        ids.push(id);
        growing.push(Vec::new());
    }

    let columns = names.len();
    let mut rows = 0usize;

    for line in lines {
        if line.is_empty() {
            continue;
        }

        let mut at = 0;
        for cell in line.split('\t').take(columns) {
            growing[at].push(number(cell));
            at += 1;
        }

        // A short row leaves the rest of its channels unknown rather than
        // shifting everything after it up by one.
        while at < columns {
            growing[at].push(DOTNET_NAN);
            at += 1;
        }

        rows += 1;
    }

    (names, ids, growing, rows)
}

fn fold(s: &str) -> String {
    s.chars()
        .map(|c| {
            let mut up = c.to_uppercase();
            match (up.next(), up.next()) {
                (Some(u), None) => u,
                _ => c,
            }
        })
        .collect()
}

/// Splits "Coolant temp [18]" into its name and MTune's index for it.
///
/// The index is worth keeping and the bracket is not: it is how the channel is
/// looked up in MTune's definitions, and it is noise in a channel list.
fn split_index(title: &str) -> (String, i32) {
    if !title.ends_with(']') {
        return (title.to_string(), -1);
    }

    let Some(open) = title.rfind('[') else {
        return (title.to_string(), -1);
    };
    if open == 0 {
        return (title.to_string(), -1);
    }

    // `Length - open - 2` characters after the bracket, which is everything up to
    // the closing one.
    match fmt::parse_i32(&title[open + 1..title.len() - 1]) {
        Some(id) => (title[..open].trim().to_string(), id),
        None => (title.to_string(), -1),
    }
}

/// `float.TryParse(text, NumberStyles.Float, InvariantCulture)`, NaN on failure.
fn number(text: &str) -> f32 {
    let Some(wide) = fmt::parse_f64_inv(text) else {
        return DOTNET_NAN;
    };
    if !wide.is_finite() {
        return wide as f32;
    }
    // Parsed from the text, not narrowed from the double: rounding twice can land
    // on a neighbouring float.
    let t = text
        .trim_start_matches(|c: char| c == ' ' || ('\t'..='\r').contains(&c))
        .trim_end_matches('\0')
        .trim_end_matches(|c: char| c == ' ' || ('\t'..='\r').contains(&c));
    t.parse::<f32>().unwrap_or(wide as f32)
}

/// A minimal zip reader: the central directory, stored and deflated entries.
mod zip {
    use crate::{Error, Result};
    use std::path::Path;

    pub struct Entry {
        /// The final path segment, as `ZipArchiveEntry.Name`.
        pub name: String,
        pub uncompressed_size: u64,
        compressed_size: usize,
        method: u16,
        local_offset: usize,
    }

    pub struct Archive {
        data: Vec<u8>,
        entries: Vec<Entry>,
    }

    fn bad(message: &str) -> Error {
        Error::LogFormat(message.to_string())
    }

    fn u16_at(b: &[u8], o: usize) -> Option<u16> {
        Some(u16::from_le_bytes(b.get(o..o + 2)?.try_into().ok()?))
    }

    fn u32_at(b: &[u8], o: usize) -> Option<u32> {
        Some(u32::from_le_bytes(b.get(o..o + 4)?.try_into().ok()?))
    }

    impl Archive {
        pub fn open(path: &Path) -> Result<Self> {
            let data = std::fs::read(path)?;
            let entries = Self::central_directory(&data)?;
            Ok(Self { data, entries })
        }

        pub fn entries(&self) -> &[Entry] {
            &self.entries
        }

        fn central_directory(data: &[u8]) -> Result<Vec<Entry>> {
            const EOCD: u32 = 0x0605_4b50;
            const CENTRAL: u32 = 0x0201_4b50;

            let eocd_error = || bad("End of Central Directory record could not be found.");

            if data.len() < 22 {
                return Err(eocd_error());
            }
            let lowest = data.len().saturating_sub(22 + 65535);
            let eocd = (lowest..=data.len() - 22)
                .rev()
                .find(|&o| u32_at(data, o) == Some(EOCD))
                .ok_or_else(eocd_error)?;

            let count = u16_at(data, eocd + 10).ok_or_else(eocd_error)? as usize;
            let mut o = u32_at(data, eocd + 16).ok_or_else(eocd_error)? as usize;

            let corrupt = || bad("A local file header is corrupt.");
            let mut entries = Vec::with_capacity(count);
            for _ in 0..count {
                if u32_at(data, o) != Some(CENTRAL) {
                    return Err(bad("A central directory header is invalid."));
                }
                let method = u16_at(data, o + 10).ok_or_else(corrupt)?;
                let compressed = u32_at(data, o + 20).ok_or_else(corrupt)?;
                let uncompressed = u32_at(data, o + 24).ok_or_else(corrupt)?;
                let name_len = u16_at(data, o + 28).ok_or_else(corrupt)? as usize;
                let extra_len = u16_at(data, o + 30).ok_or_else(corrupt)? as usize;
                let comment_len = u16_at(data, o + 32).ok_or_else(corrupt)? as usize;
                let local_offset = u32_at(data, o + 42).ok_or_else(corrupt)? as usize;
                let raw = data.get(o + 46..o + 46 + name_len).ok_or_else(corrupt)?;

                let full = String::from_utf8_lossy(raw);
                let name = full.rsplit(['/', '\\']).next().unwrap_or("").to_string();

                entries.push(Entry {
                    name,
                    uncompressed_size: u64::from(uncompressed),
                    compressed_size: compressed as usize,
                    method,
                    local_offset,
                });
                o += 46 + name_len + extra_len + comment_len;
            }
            Ok(entries)
        }

        /// The entry's bytes, inflated when it is deflated.
        pub fn extract(&self, entry: &Entry) -> Result<Vec<u8>> {
            const LOCAL: u32 = 0x0403_4b50;
            let corrupt = || bad("A local file header is corrupt.");

            let o = entry.local_offset;
            if u32_at(&self.data, o) != Some(LOCAL) {
                return Err(corrupt());
            }
            let name_len = u16_at(&self.data, o + 26).ok_or_else(corrupt)? as usize;
            let extra_len = u16_at(&self.data, o + 28).ok_or_else(corrupt)? as usize;
            let start = o + 30 + name_len + extra_len;
            let payload = self
                .data
                .get(start..start + entry.compressed_size)
                .ok_or_else(corrupt)?;

            match entry.method {
                0 => Ok(payload.to_vec()),
                8 => miniz_oxide::inflate::decompress_to_vec_with_limit(payload, super::MAXIMUM_UNCOMPRESSED as usize)
                    .map_err(|_| bad("The archive entry was compressed using an unsupported compression method.")),
                _ => Err(bad(
                    "The archive entry was compressed using an unsupported compression method.",
                )),
            }
        }
    }
}
