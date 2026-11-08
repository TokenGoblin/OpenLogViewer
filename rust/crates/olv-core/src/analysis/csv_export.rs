//! Ported from `CsvExport.cs`. Owned by package `w3-analysis`.
//!
//! Writes logs and heat tables out as CSV.
//!
//! Two things are deliberate. Numbers are always invariant-culture, so a file written on a
//! machine with a comma decimal separator still opens everywhere. And a written log reads back
//! into this app: the header and units rows are the shape the delimited reader already
//! detects, and a missing reading is an empty cell, which it already decodes as one.
//!
//! **Line endings.** The C# calls `WriteLine`, which writes `Environment.NewLine`; its only
//! platform was Windows, so every file it ever wrote ends its lines with CRLF. This keeps
//! CRLF on every platform, so an export reads the same wherever it was made.

use std::io::Write;

use super::histogram_table::HistogramTable;
use super::scatter_plot::ScatterPlot;
use crate::Result;
use crate::fmt;
use crate::model::{LogChannel, LogDocument, TuneTable};

/// What the C#'s `WriteLine` ends a line with.
pub const NEWLINE: &str = "\r\n";

fn write_line(writer: &mut impl Write, line: &str) -> Result<()> {
    writer.write_all(line.as_bytes())?;
    writer.write_all(NEWLINE.as_bytes())?;
    Ok(())
}

/// Writes the time base followed by the given channels, over an inclusive sample range.
/// Channels are written in the order supplied.
pub fn write_log(
    writer: &mut impl Write,
    document: &LogDocument,
    channels: &[impl AsRef<LogChannel>],
    first_sample: isize,
    last_sample: isize,
) -> Result<()> {
    let first = 0.max(first_sample);
    let last = (document.sample_count() as isize - 1).min(last_sample);

    // The time base leads and is never repeated, however the caller ordered its list: a
    // second Time column would make the file ambiguous.
    let mut columns: Vec<&LogChannel> = vec![document.time().as_ref()];
    columns.extend(
        channels
            .iter()
            .map(|c| c.as_ref())
            .filter(|c| !document.is_time_base(c)),
    );

    let names: Vec<String> = columns.iter().map(|c| escape(c.name())).collect();
    write_line(writer, &names.join(","))?;
    let units: Vec<String> = columns.iter().map(|c| escape(c.units())).collect();
    write_line(writer, &units.join(","))?;

    let mut line = String::new();
    let mut i = first;
    while i <= last {
        line.clear();
        for (c, column) in columns.iter().enumerate() {
            if c > 0 {
                line.push(',');
            }
            line.push_str(&number(column.at(i as usize)));
        }
        write_line(writer, &line)?;
        i += 1;
    }

    Ok(())
}

/// Writes a heat table in the shape a tuning table has: the X breakpoints across the top, the
/// Y breakpoints down the side, highest row first so it matches what is on screen and what a
/// tuning app expects. Cells that were never visited are left empty rather than written as
/// zero, which would read as a real measurement of nothing.
pub fn write_table(writer: &mut impl Write, table: &HistogramTable) -> Result<()> {
    let mut header = vec![escape(&format!("{} \\ {}", table.y.name(), table.x.name()))];
    header.extend(table.column_centers.iter().map(|&v| axis(v)));
    write_line(writer, &header.join(","))?;

    for row in (0..table.rows).rev() {
        let mut cells = vec![axis(table.row_centers[row])];

        for column in 0..table.columns {
            cells.push(match table.values[(column, row)] {
                None => String::new(),
                Some(value) => fmt::f64_inv(round(value), "R"),
            });
        }

        write_line(writer, &cells.join(","))?;
    }

    Ok(())
}

/// Writes a tuning table's cells as CSV, in TunerStudio's own import layout: the table's name
/// in the corner, the X breakpoints across the top, the Y breakpoints down the left, and the
/// highest row first so the file reads the same way up as the table does on screen.
pub fn write_tune_table(writer: &mut impl Write, table: &TuneTable) -> Result<()> {
    let mut header = vec![escape(&table.name)];
    header.extend(table.x.breakpoints.iter().map(|&v| axis(v)));
    write_line(writer, &header.join(","))?;

    for row in (0..table.rows()).rev() {
        let mut cells = vec![axis(table.y.breakpoints[row])];

        for column in 0..table.columns() {
            cells.push(fmt::f64_inv(round(table.values[(column, row)]), "R"));
        }

        write_line(writer, &cells.join(","))?;
    }

    Ok(())
}

/// Writes the header and units rows. Shared with the live recorder, so a session captured from
/// an ECU is the same shape as an exported log and reopens the same way.
pub fn write_header(writer: &mut impl Write, names: &[impl AsRef<str>], units: &[impl AsRef<str>]) -> Result<()> {
    let n: Vec<String> = names.iter().map(|n| escape(n.as_ref())).collect();
    write_line(writer, &n.join(","))?;

    let u: Vec<String> = (0..names.len())
        .map(|i| escape(units.get(i).map_or("", |u| u.as_ref())))
        .collect();
    write_line(writer, &u.join(","))?;

    Ok(())
}

/// One row of values, formatted as the exporter formats them.
pub fn write_row(writer: &mut impl Write, values: &[f64]) -> Result<()> {
    let cells: Vec<String> = values.iter().map(|&v| number(v)).collect();
    write_line(writer, &cells.join(","))
}

/// The points of a scatter, one row per sample that survived: the sample's index in the log,
/// then its X, Y and Z.
///
/// The index is first because it is what makes the file answerable: a row here can be found
/// again in a full-log export, or in the log itself, which a bare triple of numbers cannot.
/// Samples are written in log order.
pub fn write_points(writer: &mut impl Write, points: &ScatterPlot) -> Result<()> {
    // Named for what the column holds: with a comparison set, Z is a deviation and labelling
    // it with the channel's own name would be wrong.
    let z = match &points.z_compare {
        Some(compare) => format!("{} − {}", points.z.name(), compare.name()),
        None => points.z.name().to_string(),
    };

    write_header(
        writer,
        &["Sample", points.x.name(), points.y.name(), &z],
        &["", points.x.units(), points.y.units(), points.z.units()],
    )?;

    for i in 0..points.count {
        let line = format!(
            "{},{},{},{}",
            fmt::int_inv(points.samples[i] as i32, ""),
            number(points.xs[i]),
            number(points.ys[i]),
            number(points.zs[i])
        );
        write_line(writer, &line)?;
    }

    Ok(())
}

/// Sample counts per cell, in the same shape as [`write_table`].
pub fn write_table_counts(writer: &mut impl Write, table: &HistogramTable) -> Result<()> {
    let mut header = vec![escape(&format!("{} \\ {}", table.y.name(), table.x.name()))];
    header.extend(table.column_centers.iter().map(|&v| axis(v)));
    write_line(writer, &header.join(","))?;

    for row in (0..table.rows).rev() {
        let mut cells = vec![axis(table.row_centers[row])];
        for column in 0..table.columns {
            cells.push(fmt::int_inv(table.counts[(column, row)] as i32, ""));
        }

        write_line(writer, &cells.join(","))?;
    }

    Ok(())
}

/// The shortest string that reads back as the same sample.
///
/// Samples are held as float and would print seventeen digits of rounding error if formatted
/// as the doubles they widen to: 13.399999618530273 for a reading of 13.4. The time base is
/// held as double, but only some bases need it: one taken from a 32-bit field is a widened
/// float and gets the same treatment, which is why this asks whether the value survives the
/// narrowing rather than which column it came from.
fn number(value: f64) -> String {
    if value.is_nan() {
        return String::new();
    }

    let narrowed = value as f32;
    if f64::from(narrowed) == value {
        fmt::f32_inv(narrowed, "R")
    } else {
        fmt::f64_inv(value, "R")
    }
}

/// `Math.Round(value, digits)` as .NET Core implements it for 0 to 15 digits.
fn round_digits(value: f64, digits: usize) -> f64 {
    const POWERS: [f64; 16] = [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
    ];
    if value.abs() < 1e16 {
        let power10 = POWERS[digits];
        (value * power10).round_ties_even() / power10
    } else {
        value
    }
}

/// Breakpoints are display values, so they are not worth full precision.
fn axis(value: f64) -> String {
    fmt::f64_inv(round_digits(value, 4), "R")
}

fn round(value: f64) -> f64 {
    round_digits(value, 6)
}

/// RFC 4180 quoting, for the channel names that contain a comma.
fn escape(field: &str) -> String {
    if field.is_empty() || !field.contains([',', '"', '\r', '\n']) {
        return field.to_string();
    }

    format!("\"{}\"", field.replace('"', "\"\""))
}
