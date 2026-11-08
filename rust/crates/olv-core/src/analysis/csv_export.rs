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

#[cfg(test)]
mod tests {
    use std::path::PathBuf;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicUsize, Ordering};

    use super::*;
    use crate::analysis::histogram_table::{HistogramInputs, HistogramStatistic};
    use crate::logs::log_reader_factory;
    use crate::model::{Array2, TuneAxis};

    /// A scratch file that removes itself.
    struct Temp(PathBuf);

    impl Temp {
        fn new(prefix: &str) -> Self {
            static N: AtomicUsize = AtomicUsize::new(0);
            let n = N.fetch_add(1, Ordering::Relaxed);
            Temp(std::env::temp_dir().join(format!("olv-{prefix}-{}-{n}.csv", std::process::id())))
        }
    }

    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    fn log(channels: &[(&str, &str, &[f64])]) -> LogDocument {
        let rows = channels[0].2.len();
        let mut lines: Vec<String> = Vec::new();

        let mut names = vec!["Time".to_string()];
        names.extend(channels.iter().map(|c| c.0.to_string()));
        lines.push(names.join(","));

        let mut units = vec!["s".to_string()];
        units.extend(channels.iter().map(|c| c.1.to_string()));
        lines.push(units.join(","));

        for r in 0..rows {
            let mut cells = vec![fmt::f64_inv(r as f64 * 0.1, "R")];
            cells.extend(channels.iter().map(|c| {
                if c.2[r].is_nan() {
                    String::new()
                } else {
                    fmt::f64_inv(c.2[r], "R")
                }
            }));
            lines.push(cells.join(","));
        }

        let src = Temp::new("src");
        std::fs::write(&src.0, lines.join("\r\n") + "\r\n").unwrap();
        log_reader_factory::load(&src.0).unwrap()
    }

    fn handmade(time: &[f64], channels: Vec<LogChannel>) -> LogDocument {
        LogDocument::new(
            "x",
            "test",
            channels.into_iter().map(Arc::new).collect(),
            Arc::new(LogChannel::new("Time", "s", 3, time, true)),
        )
    }

    /// The C# tests write with `NewLine = "\n"`; this port always writes CRLF, so the text is
    /// normalised before it is compared.
    fn text(bytes: Vec<u8>) -> String {
        String::from_utf8(bytes).unwrap().replace("\r\n", "\n")
    }

    fn write(doc: &LogDocument, channels: &[Arc<LogChannel>], first: isize, last: isize) -> String {
        let mut out = Vec::new();
        write_log(&mut out, doc, channels, first, last).unwrap();
        text(out)
    }

    fn lines(s: &str) -> Vec<&str> {
        s.split('\n').collect()
    }

    fn nonempty_lines(s: &str) -> Vec<&str> {
        s.split('\n').filter(|l| !l.is_empty()).collect()
    }

    #[test]
    fn the_time_base_leads_and_the_header_carries_units() {
        let doc = log(&[("RPM", "RPM", &[1500.0, 1600.0]), ("MAP", "kPa", &[45.0, 46.0])]);

        let out = write(&doc, doc.channels(), 0, 1);
        let l = lines(&out);

        assert_eq!(l[0], "Time,RPM,MAP");
        assert_eq!(l[1], "s,RPM,kPa");
        assert_eq!(l[2], "0,1500,45");
        assert_eq!(l[3], "0.1,1600,46");
    }

    #[test]
    fn a_log_with_no_usable_time_column_keeps_that_column_as_data() {
        // A single sample cannot establish a time base, so one is synthesised and the file's
        // own Time column is ordinary data: excluding it would drop the column the log
        // actually carried.
        let doc = log(&[("AFR", "AFR", &[13.4])]);

        assert_eq!(doc.time().name(), "Sample");
        assert_eq!(lines(&write(&doc, doc.channels(), 0, 0))[0], "Sample,Time,AFR");
    }

    #[test]
    fn the_time_column_is_never_written_twice() {
        // Callers pass "the channels", and the document's own list contains the time base; a
        // second Time column would make the file ambiguous.
        let doc = log(&[("RPM", "RPM", &[1500.0, 1600.0])]);

        let mut all = vec![Arc::clone(doc.time())];
        all.extend(doc.channels().iter().cloned());
        let out = write(&doc, &all, 0, 1);

        assert_eq!(lines(&out)[0], "Time,RPM");
    }

    #[test]
    fn only_the_requested_sample_range_is_written() {
        let doc = log(&[("RPM", "RPM", &[1000.0, 2000.0, 3000.0, 4000.0])]);

        let out = write(&doc, doc.channels(), 1, 2);
        let l = nonempty_lines(&out);

        assert_eq!(l.len(), 4); // header, units, two samples
        assert!(l[2].ends_with("2000"));
        assert!(l[3].ends_with("3000"));
    }

    #[test]
    fn a_range_running_past_the_log_is_clamped() {
        let doc = log(&[("RPM", "RPM", &[1000.0, 2000.0])]);

        let out = write(&doc, doc.channels(), -5, 900);

        assert_eq!(nonempty_lines(&out).len(), 4);
    }

    #[test]
    fn a_missing_reading_is_an_empty_cell_rather_than_nan() {
        // "NaN" would be read back as a label and could break the header detection of
        // whatever opens the file.
        let doc = log(&[("RPM", "RPM", &[1000.0, f64::NAN])]);

        let out = write(&doc, doc.channels(), 0, 1);

        assert_eq!(lines(&out)[3], "0.1,");
    }

    #[test]
    fn samples_are_written_at_their_own_precision_not_their_float_error() {
        // Samples are stored as float. Formatting the widened double would write
        // 13.399999618530273 for a reading of 13.4.
        let doc = log(&[("AFR", "AFR", &[13.4, 14.7])]);

        let out = write(&doc, doc.channels(), 0, 1);
        let l = lines(&out);

        assert_eq!(l[2], "0,13.4");
        assert_eq!(l[3], "0.1,14.7");
    }

    #[test]
    fn the_time_base_keeps_its_full_precision() {
        // The time base is the one column held as double, because it accumulates.
        let doc = log(&[("RPM", "RPM", &[1000.0, 2000.0, 3000.0])]);

        let body = write(&doc, doc.channels(), 0, 2);

        assert!(body.contains("0.2,"));
    }

    #[test]
    fn a_time_base_taken_from_a_32_bit_field_prints_short() {
        // MLG holds Time as an f32. The base keeps it as a double so long logs stay monotonic,
        // but the value is only a widened float and would print as 0.006000000052154064.
        let time = [0f32, 0.006f32, 0.073f32].map(f64::from);
        let doc = handmade(
            &time,
            vec![LogChannel::new("RPM", "RPM", 0, &[1000.0, 1100.0, 1200.0], false)],
        );

        let out = write(&doc, doc.channels(), 0, 2);
        let l = lines(&out);

        assert_eq!(l[3], "0.006,1100");
        assert_eq!(l[4], "0.073,1200");
    }

    #[test]
    fn a_time_base_that_needs_full_precision_keeps_it() {
        // A text log's clock can carry more than a float holds; narrowing it would make a long
        // recording stop advancing.
        let doc = handmade(
            &[6310.743, 6310.81],
            vec![LogChannel::new("RPM", "RPM", 0, &[1000.0, 1100.0], false)],
        );

        let out = write(&doc, doc.channels(), 0, 1);
        let l = lines(&out);

        assert_eq!(l[2], "6310.743,1000");
        assert_eq!(l[3], "6310.81,1100");
    }

    #[test]
    fn numbers_are_invariant_whatever_the_machine_is_set_to() {
        // A file written on a comma-decimal machine has to open elsewhere. The separator is a
        // comma too, so getting this wrong also shifts columns. There is no process culture
        // here to switch (the formatting is invariant by construction), so this checks the
        // output that the de-DE run of the C# test checks.
        let doc = log(&[("AFR", "AFR", &[13.4, 14.7])]);

        let out = write(&doc, doc.channels(), 0, 1);
        let l = lines(&out);

        assert_eq!(l[2], "0,13.4");
        assert_eq!(l[3], "0.1,14.7");
    }

    #[test]
    fn a_channel_name_containing_a_comma_is_quoted() {
        let doc = handmade(
            &[0.0, 0.1],
            vec![LogChannel::new("Fuel, total", "l", 1, &[1.0, 2.0], false)],
        );

        assert_eq!(lines(&write(&doc, doc.channels(), 0, 1))[0], "Time,\"Fuel, total\"");
    }

    #[test]
    fn an_exported_log_reads_back_into_the_app() {
        // The whole point of writing a header and a units row: the file is a log this app can
        // open, not just something a spreadsheet accepts.
        let original = log(&[
            ("RPM", "RPM", &[1500.0, 1600.0, 1700.0]),
            ("AFR", "AFR", &[13.4, 14.7, 12.1]),
        ]);

        let path = Temp::new("export");
        let mut file = std::fs::File::create(&path.0).unwrap();
        write_log(&mut file, &original, original.channels(), 0, 2).unwrap();
        drop(file);

        let reloaded = log_reader_factory::load(&path.0).unwrap();

        assert_eq!(reloaded.sample_count(), 3);
        let names: Vec<&str> = reloaded.channels().iter().map(|c| c.name()).collect();
        assert_eq!(names, ["Time", "RPM", "AFR"]);
        assert_eq!(reloaded.find_channel("AFR").unwrap().units(), "AFR");

        for i in 0..3 {
            let close = |a: f64, b: f64, p: i32| {
                let s = 10f64.powi(p);
                assert_eq!((a * s).round_ties_even(), (b * s).round_ties_even());
            };
            close(
                original.find_channel("RPM").unwrap().at(i),
                reloaded.find_channel("RPM").unwrap().at(i),
                4,
            );
            close(
                original.find_channel("AFR").unwrap().at(i),
                reloaded.find_channel("AFR").unwrap().at(i),
                4,
            );
            close(original.time().at(i), reloaded.time().at(i), 6);
        }
    }

    #[test]
    fn a_gap_in_logging_survives_the_round_trip() {
        // Gap detection is derived from the time column, so a reload has to see the same
        // intervals or a paused log would come back as continuous.
        let doc = handmade(
            &[0.0, 0.1, 0.2, 60.2, 60.3],
            vec![LogChannel::new(
                "RPM",
                "RPM",
                0,
                &[1000.0, 1100.0, 1200.0, 1300.0, 1400.0],
                false,
            )],
        );

        let path = Temp::new("gap");
        let mut file = std::fs::File::create(&path.0).unwrap();
        write_log(&mut file, &doc, doc.channels(), 0, 4).unwrap();
        drop(file);

        let reloaded = log_reader_factory::load(&path.0).unwrap();

        let close = |a: f64, b: f64| {
            assert_eq!((a * 1e6).round_ties_even(), (b * 1e6).round_ties_even(), "{a} vs {b}");
        };
        close(doc.median_sample_interval(), reloaded.median_sample_interval());
        close(doc.gap_threshold(), reloaded.gap_threshold());
    }

    // ----- heat table -------------------------------------------------------

    fn table() -> HistogramTable {
        let x = Arc::new(LogChannel::new(
            "RPM",
            "RPM",
            0,
            &[1000.0, 1000.0, 5000.0, 5000.0],
            false,
        ));
        let y = Arc::new(LogChannel::new("MAP", "kPa", 0, &[40.0, 40.0, 90.0, 90.0], false));
        let z = Arc::new(LogChannel::new("AFR", "AFR", 2, &[14.0, 15.0, 11.0, 12.0], false));

        HistogramTable::build(HistogramInputs::new(&x, &y, &z), 2, 2, 0, 3, HistogramStatistic::Mean).unwrap()
    }

    fn table_lines(write: impl FnOnce(&mut Vec<u8>)) -> Vec<String> {
        let mut out = Vec::new();
        write(&mut out);
        text(out)
            .split('\n')
            .filter(|l| !l.is_empty())
            .map(str::to_string)
            .collect()
    }

    #[test]
    fn the_table_is_written_highest_row_first() {
        // Matching what is on screen, and what a tuning app expects when the block is pasted
        // in.
        let l = table_lines(|w| write_table(w, &table()).unwrap());

        assert_eq!(l.len(), 3);
        assert_eq!(l[0], "MAP \\ RPM,2000,4000");
        assert!(l[1].starts_with("77.5,")); // the higher MAP band
        assert!(l[2].starts_with("52.5,"));
    }

    #[test]
    fn cells_hold_their_values_and_empty_ones_stay_empty() {
        // Writing an unvisited cell as 0 would read as a measurement of nothing.
        let l = table_lines(|w| write_table(w, &table()).unwrap());

        assert_eq!(l[1], "77.5,,11.5");
        assert_eq!(l[2], "52.5,14.5,");
    }

    #[test]
    fn counts_are_written_in_the_same_shape() {
        let l = table_lines(|w| write_table_counts(w, &table()).unwrap());

        assert_eq!(l[0], "MAP \\ RPM,2000,4000");
        assert_eq!(l[1], "77.5,0,2");
        assert_eq!(l[2], "52.5,2,0");
    }

    #[test]
    fn table_numbers_are_also_invariant() {
        let l = table_lines(|w| write_table(w, &table()).unwrap());

        assert_eq!(l[1], "77.5,,11.5");
    }

    // `ARecordingCarriesAByteOrderMarkAndStillReadsBack` needs `LiveSession`, which lives in
    // `olv-live` (a crate that depends on this one), so it runs there:
    // `olv-live/tests/live_recording.rs::a_recording_carries_a_byte_order_mark_and_still_reads_back`.

    // ----- a tuning table, for the staged-CSV path --------------------------

    fn tune_table(name: &str, x: &[f64], y: &[f64], cells: Array2<f64>) -> TuneTable {
        TuneTable {
            name: name.to_string(),
            x: TuneAxis {
                constant: "rpmBins".into(),
                units: "rpm".into(),
                breakpoints: x.to_vec(),
            },
            y: TuneAxis {
                constant: "mapBins".into(),
                units: "kPa".into(),
                breakpoints: y.to_vec(),
            },
            values: cells,
            units: "%".into(),
        }
    }

    #[test]
    fn a_tune_table_is_written_with_breakpoints_across_the_top_and_down_the_left() {
        let mut cells = Array2::<f64>::new(2, 2);
        cells[(0, 0)] = 40.0;
        cells[(0, 1)] = 60.0;
        cells[(1, 0)] = 50.0;
        cells[(1, 1)] = 80.0;
        let table = tune_table("VE Table", &[800.0, 3000.0], &[30.0, 100.0], cells);

        let l = table_lines(|w| write_tune_table(w, &table).unwrap());

        // The corner names the table, the X breakpoints run across the top, and the highest
        // row comes first so the file reads the same way up as the table looks on screen.
        assert_eq!(l[0], "VE Table,800,3000");
        assert_eq!(l[1], "100,60,80");
        assert_eq!(l[2], "30,40,50");
    }

    #[test]
    fn a_tune_table_with_a_comma_in_its_name_still_reads_back_as_one_field() {
        let mut cells = Array2::<f64>::new(1, 1);
        cells[(0, 0)] = 55.0;
        let table = tune_table("Fuel, Primary", &[800.0], &[30.0], cells);

        let mut out = Vec::new();
        write_tune_table(&mut out, &table).unwrap();

        assert!(String::from_utf8(out).unwrap().starts_with("\"Fuel, Primary\",800"));
    }

    #[test]
    fn scatter_points_are_written_with_their_sample_index_first() {
        // Not in CsvExportTests.cs, but WritePoints has no other test: its header names the
        // deviation column for what it holds and rows lead with the log index.
        use crate::analysis::scatter_plot::ScatterPlot;

        let x = Arc::new(LogChannel::new("RPM", "rpm", 0, &[1000.0, 2000.0], false));
        let y = Arc::new(LogChannel::new("MAP", "kPa", 0, &[40.0, 50.0], false));
        let z = Arc::new(LogChannel::new("AFR", "afr", 1, &[14.5, 13.0], false));
        let target = Arc::new(LogChannel::new("Target", "afr", 1, &[14.0, 14.0], false));
        let plot = ScatterPlot::build(&x, &y, &z, 0, 1, None, Some(&target));

        let l = table_lines(|w| write_points(w, &plot).unwrap());

        assert_eq!(l[0], "Sample,RPM,MAP,AFR \u{2212} Target");
        assert_eq!(l[1], ",rpm,kPa,afr");
        assert_eq!(l[2], "0,1000,40,0.5");
        assert_eq!(l[3], "1,2000,50,-1");
    }
}
