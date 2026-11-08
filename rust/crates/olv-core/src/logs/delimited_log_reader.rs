//! Ported from `DelimitedLogReader.cs`. Owned by package `w2-logs`.
//!
//! Reader for delimited text logs (.msl, .csv, .txt, .log): the export format
//! essentially every ECU and logger can produce.
//!
//! Exports differ in almost every respect, so nothing is assumed from the file
//! extension. The reader detects, in order: text encoding, delimiter, header and
//! units rows, decimal separator, and the time base. That keeps one code path
//! working for TunerStudio, rusEFI, MaxxECU, Haltech, MoTeC, Link, AEM and
//! generic data-logger output.

use crate::Error;
use crate::Result;
use crate::channels::channel_classifier;
use crate::fmt;
use crate::model::{LogChannel, LogDocument, LogReader};
use std::borrow::Cow;
use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;

const DELIMITERS: [char; 4] = ['\t', ',', ';', '|'];

/// Column names that identify a time base, normalised to lower case.
const TIME_NAMES: [&str; 21] = [
    "time",
    "times",
    "time s",
    "timestamp",
    "time stamp",
    "elapsed",
    "elapsed time",
    "reltime",
    "rel time",
    "run time",
    "runtime",
    "seconds",
    "secs",
    "sec",
    "t",
    "zeit",
    "tid",
    "tempo",
    "session time",
    "log time",
    "abs time",
];

/// The reader for delimited text logs.
#[derive(Clone, Copy, Debug, Default)]
pub struct DelimitedLogReader;

impl DelimitedLogReader {
    pub fn new() -> Self {
        Self
    }
}

impl LogReader for DelimitedLogReader {
    fn format_name(&self) -> &str {
        "Delimited text"
    }

    fn can_read(&self, path: &Path) -> bool {
        let Ok(bytes) = std::fs::read(path) else {
            return false;
        };
        let (text, _) = decode_text(&bytes);
        let lines = split_lines(&text);
        lines.len() >= 2 && detect_delimiter(&lines).is_some()
    }

    fn read(&self, path: &Path) -> Result<LogDocument> {
        let bytes = std::fs::read(path)?;
        let (text, latin1) = decode_text(&bytes);
        let lines = split_lines(&text);
        if lines.is_empty() {
            return Err(Error::LogFormat("File is empty.".to_string()));
        }

        let delimiter = detect_delimiter(&lines)
            .ok_or_else(|| Error::LogFormat("No consistent column delimiter found.".to_string()))?;

        let (header_index, data_index) = locate_header(&lines, delimiter)?;
        let mut raw_names = Vec::new();
        split_line_into(lines[header_index], delimiter, &mut raw_names);
        let width = raw_names.len();

        // A units row sits between the header and the data when the gap is > 1.
        let mut units_row = Vec::new();
        if data_index - header_index >= 2 {
            split_line_into(lines[header_index + 1], delimiter, &mut units_row);
        }

        let decimal_comma = uses_decimal_comma(&lines, data_index, delimiter);

        let mut names: Vec<String> = Vec::with_capacity(width);
        let mut units: Vec<String> = Vec::with_capacity(width);
        for i in 0..width {
            // Units may be a separate row, or bracketed in the header itself.
            let (name, inline_units) = split_name_and_units(clean(&raw_names[i]));
            let unit = if i < units_row.len() && !units_row[i].trim().is_empty() {
                clean(&units_row[i]).to_string()
            } else {
                inline_units.unwrap_or("").to_string()
            };
            names.push(if name.is_empty() {
                format!("Column {}", i + 1)
            } else {
                name.to_string()
            });
            units.push(unit);
        }

        // The time column is also accumulated at full precision; every other
        // column goes straight into floats.
        let time_column = find_time_column(&names, &units);
        let (columns, precise_time) = parse_rows(&lines, data_index, delimiter, width, decimal_comma, time_column);

        let mut channels: Vec<Arc<LogChannel>> = Vec::with_capacity(width);
        let mut seen: HashMap<String, i32> = HashMap::new();
        for (i, column) in columns.into_iter().enumerate() {
            // Logs really do repeat a name (MS3 emits "Fuel Consumption" twice,
            // in GPH and l/hr), and identical entries are unusable in the UI.
            let mut name = names[i].clone();
            if let Some(count) = seen.get_mut(&fold(&name)) {
                *count += 1;
                let count = *count;
                name = if units[i].is_empty() {
                    format!("{name} #{count}")
                } else {
                    format!("{name} ({})", units[i])
                };
                if seen.contains_key(&fold(&name)) {
                    name = format!("{} #{count}", names[i]);
                }
            }
            seen.insert(fold(&name), 1);

            let digits = infer_digits(&column);
            channels.push(Arc::new(LogChannel::adopt(&name, &units[i], digits, column)));
        }

        let sample_count = channels.first().map_or(0, |c| c.len());
        let time = resolve_time_base(
            precise_time.as_deref(),
            time_column,
            &names,
            &units,
            &lines,
            data_index,
            delimiter,
            sample_count,
        );

        let preamble = &lines[..header_index];
        let source = detect_source(preamble);
        let recorded_at = std::fs::metadata(path)?.modified()?;

        Ok(LogDocument::new(
            &path.to_string_lossy(),
            &format_label(delimiter, source, latin1),
            channels,
            Arc::new(time),
        )
        .with_signature(preamble.first().map(|l| clean(l).to_string()))
        .with_capture_info(preamble.get(1).map(|l| clean(l).to_string()))
        .with_recorded_at(Some(recorded_at)))
    }
}

/// `OrdinalIgnoreCase` key.
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

// ----- text handling -----------------------------------------------------

/// Reads the file as UTF-8, falling back to Latin-1. Older TunerStudio
/// exports are ISO-8859-1, where a degree sign is a single invalid-UTF-8
/// byte that would otherwise decode to a replacement character.
///
/// Returns the text and whether it fell back to Latin-1.
fn decode_text(bytes: &[u8]) -> (Cow<'_, str>, bool) {
    let stripped = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(bytes);
    match std::str::from_utf8(stripped) {
        Ok(text) => (Cow::Borrowed(text), false),
        Err(_) => (Cow::Owned(bytes.iter().map(|&b| char::from(b)).collect()), true),
    }
}

fn split_lines(text: &str) -> Vec<&str> {
    text.split(['\r', '\n']).filter(|l| !l.is_empty()).collect()
}

/// Picks the delimiter that splits the most lines into the same column count.
/// Counting occurrences alone is misleading: prose in the preamble is full of
/// commas, and a tab-delimited file with quoted text would lose to them.
fn detect_delimiter(lines: &[&str]) -> Option<char> {
    let mut best = None;
    let mut best_score = 0usize;
    let mut cells = Vec::new();

    for candidate in DELIMITERS {
        // Insertion-ordered, because `MaxBy` keeps the first of equal counts.
        let mut counts: Vec<(usize, usize)> = Vec::new();
        for line in lines.iter().take(200) {
            split_line_into(line, candidate, &mut cells);
            let fields = cells.len();
            if fields >= 2 {
                match counts.iter_mut().find(|(w, _)| *w == fields) {
                    Some(entry) => entry.1 += 1,
                    None => counts.push((fields, 1)),
                }
            }
        }

        if counts.is_empty() {
            continue;
        }

        // Score by the largest run of same-width lines, weighted by width so a
        // 60-column match beats a 2-column coincidence.
        let mut top = counts[0];
        for &c in &counts[1..] {
            if c.1 > top.1 {
                top = c;
            }
        }
        let (width, runs) = top;
        let score = runs * width.min(40);
        if runs >= 3 && score > best_score {
            best_score = score;
            best = Some(candidate);
        }
    }

    best
}

/// Splits one line, honouring RFC 4180 double-quoted fields.
fn split_line_into<'a>(line: &'a str, delimiter: char, out: &mut Vec<Cow<'a, str>>) {
    out.clear();
    if !line.contains('"') {
        out.extend(line.split(delimiter).map(Cow::Borrowed));
        return;
    }

    let mut sb = String::new();
    let mut quoted = false;
    let mut chars = line.chars().peekable();

    while let Some(c) = chars.next() {
        if c == '"' {
            if quoted && chars.peek() == Some(&'"') {
                sb.push('"');
                chars.next();
            } else {
                quoted = !quoted;
            }
        } else if c == delimiter && !quoted {
            out.push(Cow::Owned(std::mem::take(&mut sb)));
        } else {
            sb.push(c);
        }
    }

    out.push(Cow::Owned(sb));
}

fn clean(cell: &str) -> &str {
    cell.trim().trim_matches('"').trim()
}

/// Pulls units out of a bracketed suffix: "MAP (kPa)", "RPM [rpm]", "CLT {°C}".
/// Common for exports with no dedicated units row.
fn split_name_and_units(header: &str) -> (&str, Option<&str>) {
    if header.encode_utf16().count() < 3 {
        return (header, None);
    }

    let Some(close) = header.chars().next_back() else {
        return (header, None);
    };
    let open = match close {
        ')' => '(',
        ']' => '[',
        '}' => '{',
        _ => return (header, None),
    };

    let Some(start) = header.rfind(open) else {
        return (header, None);
    };
    if start == 0 {
        return (header, None);
    }

    let name = header[..start].trim();
    let units = header[start + 1..header.len() - 1].trim();

    // "Fuel Consumption (2)" is a disambiguator, not a unit.
    if name.is_empty() || units.is_empty() || units.chars().all(is_digit) {
        return (header, None);
    }

    (name, Some(units))
}

/// `char.IsDigit`: decimal digits in any script.
fn is_digit(c: char) -> bool {
    c.is_ascii_digit() || (!c.is_ascii() && c.is_numeric() && c.to_digit(10).is_none() && is_decimal_digit(c))
}

/// Unicode category Nd, approximated: a numeric character that sits in a run of
/// ten consecutive digits. Good for the scripts a log could plausibly carry.
fn is_decimal_digit(c: char) -> bool {
    const ZEROS: [u32; 12] = [
        0x0660, 0x06F0, 0x07C0, 0x0966, 0x09E6, 0x0A66, 0x0AE6, 0x0B66, 0x0BE6, 0x0C66, 0x0CE6, 0xFF10,
    ];
    let u = c as u32;
    ZEROS.iter().any(|&z| (z..z + 10).contains(&u))
}

// ----- structure detection ----------------------------------------------

/// Returns the header row and the first data row, by finding the earliest row
/// whose width is matched by predominantly numeric rows below it.
fn locate_header(lines: &[&str], delimiter: char) -> Result<(usize, usize)> {
    const WINDOW: usize = 24;
    let mut header = Vec::new();

    for i in 0..lines.len().min(200) {
        split_line_into(lines[i], delimiter, &mut header);
        let header_width = header.len();
        if header_width < 2 {
            continue;
        }

        // Allow an optional units row between the header and the first data row.
        for gap in 1..=2 {
            if i + gap >= lines.len() {
                break;
            }
            let data = i + gap;
            if !is_numeric_row(lines[data], delimiter, header_width) {
                continue;
            }

            // Confirm against the rows that follow. Real logs interleave
            // annotations ("MARK ...") and rows with dropped cells, so require
            // a numeric majority rather than an unbroken run.
            let mut numeric = 0;
            let mut considered = 0;
            for line in &lines[data..lines.len().min(data + WINDOW)] {
                if line.is_empty() {
                    continue;
                }
                considered += 1;
                if is_numeric_row(line, delimiter, header_width) {
                    numeric += 1;
                }
            }

            if considered > 0 && numeric * 2 >= considered {
                return Ok((i, data));
            }
        }
    }

    Err(Error::LogFormat(
        "Could not locate a header row followed by numeric data.".to_string(),
    ))
}

fn is_numeric_row(line: &str, delimiter: char, width: usize) -> bool {
    let mut cells = Vec::new();
    split_line_into(line, delimiter, &mut cells);
    if cells.len() < width || width == 0 {
        return false;
    }

    let values = cells[..width].iter().filter(|c| looks_like_value(c)).count();
    values as f64 >= width as f64 * 0.8
}

/// Whether a cell reads as data rather than a label. A wall-clock timestamp
/// counts: some exports use one as the time column, and rejecting it would
/// stop the whole file being recognised.
fn looks_like_value(cell: &str) -> bool {
    let s = clean(cell);
    if s.is_empty() {
        return true;
    }
    if looks_numeric(s) {
        return true;
    }

    // Require a date or time separator, so plain words are not read as dates.
    s.encode_utf16().count() >= 8
        && (s.contains(':') || s.contains('-') || s.contains('/'))
        && parse_date_time(s).is_some()
}

fn looks_numeric(cell: &str) -> bool {
    let s = clean(cell);
    if s.is_empty() {
        return true;
    }

    fmt::parse_f64_inv(s).is_some() || fmt::parse_f64_inv(&s.replace(',', ".")).is_some()
}

/// Detects the European convention where "1234,5" is a decimal. Only possible
/// when the comma is not itself the delimiter.
fn uses_decimal_comma(lines: &[&str], data_index: usize, delimiter: char) -> bool {
    if delimiter == ',' {
        return false;
    }

    let mut comma_decimals = 0;
    let mut dot_decimals = 0;
    let mut cells = Vec::new();
    for line in &lines[data_index..lines.len().min(data_index + 40)] {
        split_line_into(line, delimiter, &mut cells);
        for cell in &cells {
            let s = clean(cell);
            if s.is_empty() {
                continue;
            }
            if s.contains(',') && fmt::parse_f64_inv(&s.replace(',', ".")).is_some() {
                comma_decimals += 1;
            } else if s.contains('.') {
                dot_decimals += 1;
            }
        }
    }

    comma_decimals > dot_decimals
}

// ----- parsing ----------------------------------------------------------

type Columns = (Vec<Vec<f32>>, Option<Vec<f64>>);

fn parse_rows(
    lines: &[&str],
    data_index: usize,
    delimiter: char,
    width: usize,
    decimal_comma: bool,
    time_column: Option<usize>,
) -> Columns {
    // Row count is established first so each column can be allocated once at
    // its final size. Growing a list per column and then copying it out cost
    // more transient memory than the log itself.
    let rows = count_data_rows(lines, data_index, delimiter, width);

    let mut columns: Vec<Vec<f32>> = (0..width).map(|_| vec![0.0f32; rows]).collect();

    let time_column = time_column.filter(|&c| c < width);
    let mut time = time_column.map(|_| vec![0.0f64; rows]);

    let mut cells = Vec::new();
    let mut row = 0usize;
    for line in &lines[data_index..] {
        if row >= rows {
            break;
        }
        if line.is_empty() {
            continue;
        }

        split_line_into(line, delimiter, &mut cells);
        // Annotation rows ("MARK ...") and ragged rows are skipped.
        if cells.len() < width {
            continue;
        }

        for (c, column) in columns.iter_mut().enumerate() {
            let v = parse_cell(&cells[c], decimal_comma);
            column[row] = v as f32;
            if Some(c) == time_column
                && let Some(t) = time.as_mut()
            {
                t[row] = v;
            }
        }

        row += 1;
    }

    (columns, time)
}

fn count_data_rows(lines: &[&str], data_index: usize, delimiter: char, width: usize) -> usize {
    let mut rows = 0;
    let mut cells = Vec::new();
    for line in &lines[data_index..] {
        if line.is_empty() {
            continue;
        }

        // Counting separators answers the question unless quoting is in play,
        // and avoids allocating a second split for every row in the file.
        let fields = if line.contains('"') {
            split_line_into(line, delimiter, &mut cells);
            cells.len()
        } else {
            line.matches(delimiter).count() + 1
        };

        if fields >= width {
            rows += 1;
        }
    }
    rows
}

/// .NET's `double.NaN` has its sign bit set (0xFFF8...), which a cast to float
/// keeps; Rust's `f64::NAN` does not. Samples are hashed by bits, so it matters.
const DOTNET_NAN: f64 = f64::from_bits(0xFFF8_0000_0000_0000);

fn parse_cell(cell: &str, decimal_comma: bool) -> f64 {
    let s = clean(cell);
    if s.is_empty() {
        return DOTNET_NAN;
    }
    if decimal_comma {
        return fmt::parse_f64_inv(&s.replace('.', "").replace(',', ".")).unwrap_or(DOTNET_NAN);
    }

    fmt::parse_f64_inv(s).unwrap_or(DOTNET_NAN)
}

/// Infers a sensible display precision from the magnitude of the data.
fn infer_digits(values: &[f32]) -> i32 {
    for &v in values {
        if v.is_nan() {
            continue;
        }
        if v != v.floor() {
            return 2;
        }
    }
    0
}

// ----- time base --------------------------------------------------------

/// Built from the separately parsed doubles rather than a stored channel: a
/// time base keeps full precision, because it accumulates over the recording.
#[allow(clippy::too_many_arguments)]
fn resolve_time_base(
    raw: Option<&[f64]>,
    index: Option<usize>,
    names: &[String],
    units: &[String],
    lines: &[&str],
    data_index: usize,
    delimiter: char,
    sample_count: usize,
) -> LogChannel {
    if let (Some(raw), Some(index)) = (raw, index) {
        let factor = time_scale(&units[index]);

        if is_monotonic(raw) && !raw.is_empty() && raw[raw.len() - 1] > raw[0] {
            let seconds: Cow<[f64]> = if (factor - 1.0).abs() > f64::from_bits(1) {
                Cow::Owned(raw.iter().map(|v| v * factor).collect())
            } else {
                Cow::Borrowed(raw)
            };

            return LogChannel::new(&names[index], "s", 3, &seconds, true);
        }

        // The column may hold wall-clock strings rather than numbers.
        if let Some(elapsed) = try_read_timestamps(lines, data_index, delimiter, index, sample_count) {
            return LogChannel::new("Time", "s", 3, &elapsed, true);
        }
    }

    let synthetic: Vec<f64> = (0..sample_count).map(|i| i as f64).collect();
    LogChannel::new("Sample", "#", 0, &synthetic, true)
}

fn find_time_column(names: &[String], units: &[String]) -> Option<usize> {
    for (i, name) in names.iter().enumerate() {
        let normal = channel_classifier::normalise(name);
        if TIME_NAMES.contains(&normal.as_str()) {
            return Some(i);
        }
    }

    // Fall back to the first column when its units are a time unit.
    for (i, u) in units.iter().enumerate() {
        let lower = u.trim().to_lowercase();
        if time_scale(u) != 1.0 || lower == "s" || lower == "sec" {
            return Some(i);
        }
    }

    None
}

/// Multiplier converting a column's units into seconds.
fn time_scale(units: &str) -> f64 {
    match units.trim().to_lowercase().as_str() {
        "ms" | "msec" | "millisecond" | "milliseconds" => 0.001,
        "us" | "usec" | "microsecond" | "microseconds" => 0.000001,
        "min" | "minute" | "minutes" => 60.0,
        _ => 1.0,
    }
}

fn try_read_timestamps(
    lines: &[&str],
    data_index: usize,
    delimiter: char,
    column: usize,
    sample_count: usize,
) -> Option<Vec<f64>> {
    let mut stamps: Vec<i64> = Vec::with_capacity(sample_count);
    let mut cells = Vec::new();

    for line in &lines[data_index..] {
        if line.is_empty() {
            continue;
        }
        split_line_into(line, delimiter, &mut cells);
        if cells.len() <= column {
            continue;
        }

        stamps.push(parse_date_time(clean(&cells[column]))?);
    }

    if stamps.len() != sample_count || stamps.is_empty() {
        return None;
    }

    let first = stamps[0];
    // TimeSpan.TotalSeconds: ticks over ticks-per-second.
    let elapsed: Vec<f64> = stamps.iter().map(|&s| (s - first) as f64 / 10_000_000.0).collect();

    (is_monotonic(&elapsed) && elapsed[elapsed.len() - 1] > 0.0).then_some(elapsed)
}

fn is_monotonic(values: &[f64]) -> bool {
    for i in 1..values.len() {
        if values[i] < values[i - 1] {
            return false;
        }
    }
    true
}

// ----- date parsing -----------------------------------------------------

const TICKS_PER_SECOND: i64 = 10_000_000;

/// A working subset of `DateTime.TryParse(s, InvariantCulture, AllowWhiteSpaces)`,
/// returning ticks (100 ns) since 0001-01-01. Covers the forms loggers write:
/// ISO `yyyy-MM-dd[ T]HH:mm[:ss[.fffffff]][Z|+hh:mm]`, `M/d/yyyy [h:mm[:ss] [AM|PM]]`,
/// and a bare time of day. An offset is applied, so the result is UTC. Month
/// names and other long forms are not recognised.
fn parse_date_time(s: &str) -> Option<i64> {
    let s = s.trim();
    let b = s.as_bytes();
    let mut p = Scanner { b, i: 0 };

    let date_ticks;
    let mut have_date = false;

    // Date part, when the text starts with one.
    let save = p.i;
    if let Some(d) = p.iso_date().or_else(|| {
        p.i = save;
        p.us_date()
    }) {
        date_ticks = d;
        have_date = true;
    } else {
        p.i = save;
        date_ticks = 0;
    }

    let mut time_ticks = 0i64;
    let mut pm: Option<bool> = None;
    let mut offset_ticks = 0i64;

    if have_date {
        if p.at_end() {
            return Some(date_ticks);
        }
        if p.peek() == Some(b'T') || p.peek() == Some(b' ') {
            p.i += 1;
            while p.peek() == Some(b' ') {
                p.i += 1;
            }
        } else {
            return None;
        }
    }

    let (h, m, sec, frac) = p.time()?;
    time_ticks += (i64::from(h) * 3600 + i64::from(m) * 60 + i64::from(sec)) * TICKS_PER_SECOND + frac;

    while p.peek() == Some(b' ') {
        p.i += 1;
    }
    let rest = &s[p.i..];
    let upper = rest.to_ascii_uppercase();
    let mut consumed = 0;
    if upper.starts_with("AM") {
        pm = Some(false);
        consumed = 2;
    } else if upper.starts_with("PM") {
        pm = Some(true);
        consumed = 2;
    }
    p.i += consumed;
    if let Some(is_pm) = pm {
        if !(1..=12).contains(&h) {
            return None;
        }
        let h12 = i64::from(h % 12) + if is_pm { 12 } else { 0 };
        time_ticks += (h12 - i64::from(h)) * 3600 * TICKS_PER_SECOND;
    }

    while p.peek() == Some(b' ') {
        p.i += 1;
    }
    if p.peek() == Some(b'Z') || p.peek() == Some(b'z') {
        p.i += 1;
    } else if matches!(p.peek(), Some(b'+' | b'-')) && !p.at_end() {
        let sign = if p.peek() == Some(b'-') { -1 } else { 1 };
        p.i += 1;
        let oh = p.digits(1, 2)?;
        let om = if p.peek() == Some(b':') {
            p.i += 1;
            p.digits(2, 2)?
        } else {
            0
        };
        if oh > 14 || om > 59 {
            return None;
        }
        offset_ticks = sign * (i64::from(oh) * 3600 + i64::from(om) * 60) * TICKS_PER_SECOND;
    }

    while p.peek() == Some(b' ') {
        p.i += 1;
    }
    if !p.at_end() {
        return None;
    }

    Some(date_ticks + time_ticks - offset_ticks)
}

struct Scanner<'a> {
    b: &'a [u8],
    i: usize,
}

impl Scanner<'_> {
    fn peek(&self) -> Option<u8> {
        self.b.get(self.i).copied()
    }

    fn at_end(&self) -> bool {
        self.i >= self.b.len()
    }

    /// Between `min` and `max` ASCII digits.
    fn digits(&mut self, min: usize, max: usize) -> Option<u32> {
        let start = self.i;
        let mut v = 0u32;
        while self.i - start < max && self.peek().is_some_and(|c| c.is_ascii_digit()) {
            v = v * 10 + u32::from(self.b[self.i] - b'0');
            self.i += 1;
        }
        (self.i - start >= min).then_some(v)
    }

    fn iso_date(&mut self) -> Option<i64> {
        let y = self.digits(4, 4)?;
        let sep = self.peek()?;
        if sep != b'-' && sep != b'/' {
            return None;
        }
        self.i += 1;
        let m = self.digits(1, 2)?;
        if self.peek()? != sep {
            return None;
        }
        self.i += 1;
        let d = self.digits(1, 2)?;
        date_ticks(y, m, d)
    }

    fn us_date(&mut self) -> Option<i64> {
        let m = self.digits(1, 2)?;
        if self.peek()? != b'/' {
            return None;
        }
        self.i += 1;
        let d = self.digits(1, 2)?;
        if self.peek()? != b'/' {
            return None;
        }
        self.i += 1;
        let y = self.digits(4, 4)?;
        date_ticks(y, m, d)
    }

    /// `H:mm[:ss[.fraction]]`, the fraction as ticks.
    fn time(&mut self) -> Option<(u32, u32, u32, i64)> {
        let h = self.digits(1, 2)?;
        if self.peek()? != b':' {
            return None;
        }
        self.i += 1;
        let m = self.digits(2, 2)?;
        let mut sec = 0;
        let mut frac = 0i64;
        if self.peek() == Some(b':') {
            self.i += 1;
            sec = self.digits(2, 2)?;
            if self.peek() == Some(b'.') {
                self.i += 1;
                let start = self.i;
                let mut scale = TICKS_PER_SECOND / 10;
                while self.peek().is_some_and(|c| c.is_ascii_digit()) {
                    frac += i64::from(self.b[self.i] - b'0') * scale;
                    scale /= 10;
                    self.i += 1;
                }
                if self.i == start {
                    return None;
                }
            }
        }
        (h < 24 && m < 60 && sec < 60).then_some((h, m, sec, frac))
    }
}

/// Ticks from 0001-01-01 to midnight of a valid proleptic-Gregorian date.
fn date_ticks(y: u32, m: u32, d: u32) -> Option<i64> {
    if !(1..=9999).contains(&y) || !(1..=12).contains(&m) || d < 1 {
        return None;
    }
    let leap = (y.is_multiple_of(4) && !y.is_multiple_of(100)) || y.is_multiple_of(400);
    let days_in = match m {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        _ => {
            if leap {
                29
            } else {
                28
            }
        }
    };
    if d > days_in {
        return None;
    }
    let y1 = i64::from(y) - 1;
    let mut days = y1 * 365 + y1 / 4 - y1 / 100 + y1 / 400;
    const CUM: [i64; 12] = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334];
    days += CUM[(m - 1) as usize] + i64::from(d) - 1;
    if leap && m > 2 {
        days += 1;
    }
    Some(days * 86_400 * TICKS_PER_SECOND)
}

// ----- labelling --------------------------------------------------------

/// Identifies the producing tool from its preamble. Purely cosmetic: it
/// names the format in the UI and never changes how the file is parsed.
fn detect_source(preamble: &[&str]) -> Option<&'static str> {
    let blob = preamble.join(" ").to_lowercase();

    if blob.contains("tunerstudio")
        || blob.contains("megasquirt")
        || blob.contains("ms3 format")
        || blob.contains("ms2extra")
    {
        return Some("TunerStudio");
    }
    if blob.contains("rusefi") {
        return Some("rusEFI");
    }
    if blob.contains("maxxecu") || blob.contains("maxxtuner") {
        return Some("MaxxECU");
    }
    if blob.contains("haltech") {
        return Some("Haltech");
    }
    if blob.contains("motec") || blob.contains("i2 pro") {
        return Some("MoTeC");
    }
    if blob.contains("ecumaster") {
        return Some("ECUMaster");
    }
    if blob.contains("aem") && blob.contains("infinity") {
        return Some("AEM Infinity");
    }
    if blob.contains("speeduino") {
        return Some("Speeduino");
    }
    if blob.contains("holley") {
        return Some("Holley");
    }
    if blob.contains("pcmlink") || blob.contains("hp tuners") {
        return Some("HP Tuners");
    }
    if blob.contains("g4+") || blob.contains("pclink") {
        return Some("Link");
    }

    None
}

fn format_label(delimiter: char, source: Option<&str>, latin1: bool) -> String {
    let kind = match delimiter {
        '\t' => "tab-delimited",
        ';' => "semicolon CSV",
        '|' => "pipe-delimited",
        _ => "CSV",
    };

    let label = match source {
        None => kind.to_string(),
        Some(s) => format!("{s} {kind}"),
    };
    if latin1 { format!("{label} (Latin-1)") } else { label }
}
