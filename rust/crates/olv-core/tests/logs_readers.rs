//! The C# tests for the log readers: `MlgReaderTests`, `DelimitedLogReaderTests`,
//! `LogDocumentTests` and `MaxxLogTests`, with `MlgBuilder` as [`MlgBuilder`].
//!
//! Kept in one integration test, not beside each reader, because the builders
//! (an MLG file, a zip) and the temporary files are shared between the sets.

use olv_core::Error;
use olv_core::logs::log_reader_factory;
use olv_core::logs::maxx_log_reader::MaxxLogReader;
use olv_core::logs::mlg_reader::MlgDataType;
use olv_core::model::{LogChannel, LogDocument, LogReader};
use olv_core::units::UnitSystem;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

// ----- shared helpers ------------------------------------------------------

static NEXT: AtomicUsize = AtomicUsize::new(0);

/// Files that are deleted when the set goes out of scope (`IDisposable`).
#[derive(Default)]
struct Temp(Vec<PathBuf>);

impl Temp {
    fn path(&mut self, name_tail: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!(
            "olv-test-{}-{}-{name_tail}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        self.0.push(p.clone());
        p
    }

    fn bytes(&mut self, tail: &str, bytes: &[u8]) -> PathBuf {
        let p = self.path(tail);
        std::fs::write(&p, bytes).unwrap();
        p
    }

    /// `File.WriteAllLines`.
    fn lines(&mut self, extension: &str, lines: &[&str]) -> PathBuf {
        let mut text = lines.join("\n");
        text.push('\n');
        self.bytes(&format!("x{extension}"), text.as_bytes())
    }
}

impl Drop for Temp {
    fn drop(&mut self) {
        for p in &self.0 {
            let _ = std::fs::remove_file(p);
        }
    }
}

fn load(path: &Path) -> LogDocument {
    log_reader_factory::load(path).expect("the log should load")
}

fn channel<'a>(log: &'a LogDocument, name: &str) -> &'a LogChannel {
    log.find_channel(name).unwrap_or_else(|| panic!("no channel {name}"))
}

/// `Assert.Equal(expected, actual, precision)`.
#[track_caller]
fn near(expected: f64, actual: f64, decimals: i32) {
    assert!(
        (expected - actual).abs() < 0.5 * 10f64.powi(-decimals) + 1e-12,
        "expected {expected}, got {actual} (to {decimals} places)"
    );
}

fn names(log: &LogDocument) -> Vec<String> {
    log.channels().iter().map(|c| c.name().to_string()).collect()
}

// ----- MlgBuilder ----------------------------------------------------------

const FIELD_DESCRIPTOR_SIZE: usize = 89;

struct Field {
    ty: MlgDataType,
    name: &'static str,
    units: &'static str,
    scale: f32,
    transform: f32,
    digits: u8,
}

/// Builds a well-formed MLG file in memory so the reader can be exercised without
/// depending on sample logs outside the repository.
#[derive(Default)]
struct MlgBuilder {
    fields: Vec<Field>,
}

impl MlgBuilder {
    fn add(mut self, ty: MlgDataType, name: &'static str, units: &'static str) -> Self {
        self.fields.push(Field {
            ty,
            name,
            units,
            scale: 1.0,
            transform: 0.0,
            digits: 0,
        });
        self
    }

    fn scaled(mut self, ty: MlgDataType, name: &'static str, units: &'static str, scale: f32, digits: u8) -> Self {
        self.fields.push(Field {
            ty,
            name,
            units,
            scale,
            transform: 0.0,
            digits,
        });
        self
    }

    fn transformed(
        mut self,
        ty: MlgDataType,
        name: &'static str,
        units: &'static str,
        scale: f32,
        transform: f32,
    ) -> Self {
        self.fields.push(Field {
            ty,
            name,
            units,
            scale,
            transform,
            digits: 0,
        });
        self
    }

    fn payload_size(&self) -> usize {
        self.fields.iter().map(|f| size_of(f.ty)).sum()
    }

    /// Serialises the log. `raw` supplies the pre-scaling value for (field index,
    /// sample index). Markers are emitted after the sample index they are keyed to.
    fn build(
        &self,
        sample_count: usize,
        raw: &dyn Fn(usize, usize) -> f64,
        markers: &[(usize, &str)],
        declared_payload_override: Option<usize>,
        embedded_tune: Option<&str>,
    ) -> Vec<u8> {
        let payload = self.payload_size();
        let info_start = 24 + self.fields.len() * FIELD_DESCRIPTOR_SIZE;
        let info: Vec<u8> = format!(
            "\"TEST ECU signature\"\0\"Capture Date: test\"\0{}",
            embedded_tune.unwrap_or("")
        )
        .chars()
        .map(|c| c as u8)
        .collect();
        let data_start = info_start + info.len();

        let mut buffer = Vec::new();
        let mut head = [0u8; 24];
        head[0..5].copy_from_slice(b"MLVLG");
        head[6..8].copy_from_slice(&2u16.to_be_bytes());
        head[8..12].copy_from_slice(&1_700_000_000u32.to_be_bytes());
        head[12..16].copy_from_slice(&(info_start as i32).to_be_bytes());
        head[16..20].copy_from_slice(&(data_start as i32).to_be_bytes());
        head[20..22].copy_from_slice(&(declared_payload_override.unwrap_or(payload) as u16).to_be_bytes());
        head[22..24].copy_from_slice(&(self.fields.len() as u16).to_be_bytes());
        buffer.extend_from_slice(&head);

        for f in &self.fields {
            let mut d = [0u8; FIELD_DESCRIPTOR_SIZE];
            d[0] = f.ty as u8;
            write_fixed(&mut d, 1, 34, f.name);
            write_fixed(&mut d, 35, 11, f.units);
            d[46..50].copy_from_slice(&f.scale.to_be_bytes());
            d[50..54].copy_from_slice(&f.transform.to_be_bytes());
            d[54] = f.digits;
            buffer.extend_from_slice(&d);
        }

        buffer.extend_from_slice(&info);

        let mut counter = 0u8;
        for s in 0..sample_count {
            let mut record = vec![0u8; 4 + payload + 1];
            record[1] = counter;
            counter = counter.wrapping_add(1);
            record[2..4].copy_from_slice(&((s * 100) as u16).to_be_bytes());

            let mut offset = 4;
            for (f, field) in self.fields.iter().enumerate() {
                write_value(&mut record[offset..], field.ty, raw(f, s));
                offset += size_of(field.ty);
            }
            buffer.extend_from_slice(&record);

            for (_, text) in markers.iter().filter(|m| m.0 == s) {
                let mut marker = [0u8; 54];
                marker[0] = 1;
                marker[1] = counter;
                counter = counter.wrapping_add(1);
                marker[2..4].copy_from_slice(&((s * 100) as u16).to_be_bytes());
                write_fixed(&mut marker, 4, 50, text);
                buffer.extend_from_slice(&marker);
            }
        }

        buffer
    }
}

fn write_fixed(target: &mut [u8], offset: usize, length: usize, value: &str) {
    let bytes = value.as_bytes();
    let n = bytes.len().min(length);
    target[offset..offset + n].copy_from_slice(&bytes[..n]);
}

fn write_value(span: &mut [u8], ty: MlgDataType, value: f64) {
    match ty {
        MlgDataType::U08 | MlgDataType::Bits => span[0] = value as u8,
        MlgDataType::S08 => span[0] = (value as i8) as u8,
        MlgDataType::U16 => span[0..2].copy_from_slice(&(value as u16).to_be_bytes()),
        MlgDataType::S16 => span[0..2].copy_from_slice(&(value as i16).to_be_bytes()),
        MlgDataType::U32 => span[0..4].copy_from_slice(&(value as u32).to_be_bytes()),
        MlgDataType::S32 => span[0..4].copy_from_slice(&(value as i32).to_be_bytes()),
        MlgDataType::S64 => span[0..8].copy_from_slice(&(value as i64).to_be_bytes()),
        MlgDataType::F32 => span[0..4].copy_from_slice(&(value as f32).to_be_bytes()),
    }
}

fn size_of(ty: MlgDataType) -> usize {
    match ty {
        MlgDataType::U08 | MlgDataType::S08 | MlgDataType::Bits => 1,
        MlgDataType::U16 | MlgDataType::S16 => 2,
        MlgDataType::U32 | MlgDataType::S32 | MlgDataType::F32 => 4,
        MlgDataType::S64 => 8,
    }
}

// ----- MlgReaderTests ------------------------------------------------------

fn mlg_file(
    temp: &mut Temp,
    b: &MlgBuilder,
    n: usize,
    raw: &dyn Fn(usize, usize) -> f64,
    markers: &[(usize, &str)],
    declared: Option<usize>,
) -> PathBuf {
    temp.bytes("x.mlg", &b.build(n, raw, markers, declared, None))
}

fn basic() -> MlgBuilder {
    MlgBuilder::default()
        .scaled(MlgDataType::F32, "Time", "s", 1.0, 3)
        .add(MlgDataType::U16, "RPM", "RPM")
        .scaled(MlgDataType::S16, "MAP", "kPa", 0.1, 1)
}

#[test]
fn reads_channel_metadata_and_samples() {
    let mut t = Temp::default();
    let path = mlg_file(
        &mut t,
        &basic(),
        10,
        &|f, s| match f {
            0 => s as f64 * 0.05,
            1 => 1000.0 + s as f64 * 100.0,
            _ => 820.0,
        },
        &[],
        None,
    );

    let log = load(&path);

    assert_eq!(log.format_name(), "MLG v2");
    assert_eq!(log.channels().len(), 3);
    assert_eq!(log.sample_count(), 10);
    assert_eq!(names(&log), ["Time", "RPM", "MAP"]);
    assert_eq!(channel(&log, "MAP").units(), "kPa");
    assert_eq!(log.signature(), Some("TEST ECU signature"));
}

#[test]
fn applies_scale_and_transform() {
    // MAP is s16 scaled by 0.1, so a raw 820 must decode to 82.0 kPa.
    let mut t = Temp::default();
    let path = mlg_file(&mut t, &basic(), 4, &|f, _| if f == 2 { 820.0 } else { 0.0 }, &[], None);

    let log = load(&path);
    let map = channel(&log, "MAP");

    near(82.0, map.at(0), 3);
    assert_eq!(map.format(map.at(0), UnitSystem::AsReported), "82.0");
}

#[test]
fn a_transform_is_added_before_the_scale_and_not_after() {
    // The declaration is MS2's VE trim, copied out of one of the user's own
    // logs: scale 0.009765625, transform 10240, in per cent. A trim that is
    // doing nothing sits at a raw zero and must read 100%.
    //
    // Adding after the scale instead reads 10,240% - which is what this log
    // really did show before, and what made the ordering worth settling.
    // See TuneConstant.Transform for the four firmwares that settle it.
    let b = MlgBuilder::default().add(MlgDataType::F32, "Time", "s").transformed(
        MlgDataType::S16,
        "VE Trim 1",
        "%",
        0.009765625,
        10240.0,
    );

    let mut t = Temp::default();
    let path = mlg_file(&mut t, &b, 3, &|f, s| if f == 1 { 0.0 } else { s as f64 }, &[], None);

    let log = load(&path);
    near(100.0, channel(&log, "VE Trim 1").at(0), 3);
}

#[test]
fn flag_byte_keeps_following_channels_aligned() {
    // A type-16 descriptor is unnamed but still consumes one payload byte.
    // Ignoring it would shift every channel after it by one byte.
    let b = MlgBuilder::default()
        .add(MlgDataType::F32, "Time", "s")
        .add(MlgDataType::U16, "RPM", "RPM")
        .add(MlgDataType::Bits, "", "bits")
        .scaled(MlgDataType::S16, "MAP", "kPa", 0.1, 1);

    let mut t = Temp::default();
    let path = mlg_file(
        &mut t,
        &b,
        5,
        &|f, s| match f {
            0 => s as f64,
            1 => 3000.0,
            2 => f64::from(0b1010_0101u8),
            _ => 950.0,
        },
        &[],
        None,
    );

    let log = load(&path);

    assert_eq!(channel(&log, "RPM").at(0), 3000.0);
    near(95.0, channel(&log, "MAP").at(0), 3);
    assert_eq!(channel(&log, "Flags 1").at(0), f64::from(0b1010_0101u8));
}

#[test]
fn walks_interleaved_marker_records() {
    // Markers are a different length to samples, so a fixed stride would
    // desynchronise everything after the first one.
    let mut t = Temp::default();
    let path = mlg_file(
        &mut t,
        &basic(),
        20,
        &|f, s| match f {
            0 => s as f64 * 0.1,
            1 => 2000.0 + s as f64,
            _ => 800.0,
        },
        &[(5, "MARK 000 - test marker"), (12, "MARK 001 - second")],
        None,
    );

    let log = load(&path);

    assert_eq!(log.sample_count(), 20);
    assert_eq!(log.markers().len(), 2);
    assert_eq!(log.markers()[0].text, "MARK 000 - test marker");
    assert_eq!(log.markers()[1].text, "MARK 001 - second");

    // Samples after a marker must still decode correctly.
    assert_eq!(channel(&log, "RPM").at(19), 2019.0);
    near(1.9, log.time().at(19), 3);
}

#[test]
fn throws_when_declared_payload_disagrees_with_channel_table() {
    let mut t = Temp::default();
    let path = mlg_file(&mut t, &basic(), 5, &|_, _| 0.0, &[], Some(999));

    let Err(Error::LogFormat(message)) = log_reader_factory::load(&path) else {
        panic!("expected a LogFormat error");
    };
    assert!(message.contains("999"));
}

#[test]
fn rejects_non_mlg_content() {
    let mut t = Temp::default();
    let path = t.bytes("x.mlg", &[0, 1, 2, 3, 4, 5, 6, 7]);

    assert!(matches!(log_reader_factory::load(&path), Err(Error::LogFormat(_))));
}

#[test]
fn time_channel_is_used_as_the_time_base() {
    let mut t = Temp::default();
    let path = mlg_file(
        &mut t,
        &basic(),
        6,
        &|f, s| if f == 0 { s as f64 * 0.25 } else { 0.0 },
        &[],
        None,
    );

    let log = load(&path);

    assert_eq!(log.time().name(), "Time");
    near(1.25, log.duration(), 3);
}

#[test]
fn falls_back_to_sample_index_when_time_is_flat() {
    let mut t = Temp::default();
    let path = mlg_file(&mut t, &basic(), 6, &|_, _| 0.0, &[], None);

    let log = load(&path);

    assert_eq!(log.time().name(), "Sample");
    assert_eq!(log.time().at(5), 5.0);
}

#[test]
fn index_at_time_finds_the_sample_at_or_before_a_timestamp() {
    for (seconds, expected) in [(0.0, 0), (0.24, 0), (0.25, 1), (1.1, 4), (99.0, 5)] {
        let mut t = Temp::default();
        let path = mlg_file(
            &mut t,
            &basic(),
            6,
            &|f, s| if f == 0 { s as f64 * 0.25 } else { 0.0 },
            &[],
            None,
        );

        assert_eq!(load(&path).index_at_time(seconds), expected, "at {seconds}");
    }
}

// ----- DelimitedLogReaderTests ---------------------------------------------

#[test]
fn quoted_delimiters_do_not_change_the_row_count() {
    // Columns are sized from a separator count that skips quoted lines. If
    // that count disagreed with the split, rows would be dropped or the tail
    // of every column would be left as zeros.
    let mut t = Temp::default();
    let path = t.lines(
        ".csv",
        &[
            "Time,RPM,MAP,AFR,Note",
            "0.0,1500,45,14.7,\"idle, warm\"",
            "0.1,1600,46,14.6,plain",
            "0.2,1700,47,14.5,\"a,b,c,d\"",
            "0.3,1800,48,14.4,plain",
        ],
    );

    let log = load(&path);

    assert_eq!(log.sample_count(), 4);
    assert_eq!(log.channels().len(), 5);

    let rpm = channel(&log, "RPM");
    assert_eq!(rpm.at(0), 1500.0);
    assert_eq!(rpm.at(3), 1800.0);
    assert_eq!(rpm.max(), 1800.0);
}

#[test]
fn ragged_rows_are_skipped_without_leaving_a_gap_in_the_columns() {
    // A short row is dropped, so the allocated column length must count only
    // the rows that are actually parsed.
    let mut t = Temp::default();
    let path = t.lines(".csv", &["Time,RPM,MAP", "0.0,1500,45", "0.1,1600", "0.2,1700,47"]);

    let log = load(&path);

    assert_eq!(log.sample_count(), 2);
    assert_eq!(channel(&log, "RPM").to_vec(), [1500.0, 1700.0]);
    near(0.2, log.time().at(1), 6);
}

#[test]
fn parses_tuner_studio_msl_with_signature_and_units_row() {
    let mut t = Temp::default();
    let path = t.lines(
        ".msl",
        &[
            "\"MS3 Format 0592.12P: test\"",
            "\"Capture Date: Fri Jul 31 10:00:00 EDT 2026\"",
            "Time\tRPM\tMAP\tAFR",
            "s\tRPM\tkPa\tAFR",
            "0.000\t1500\t45.0\t14.7",
            "0.020\t1600\t46.5\t14.6",
            "0.040\t1700\t48.0\t14.5",
        ],
    );

    let log = load(&path);

    assert_eq!(log.format_name(), "TunerStudio tab-delimited");
    assert_eq!(log.channels().len(), 4);
    assert_eq!(log.sample_count(), 3);
    assert_eq!(log.signature(), Some("MS3 Format 0592.12P: test"));

    let map = channel(&log, "MAP");
    assert_eq!(map.units(), "kPa");
    near(45.0, map.at(0), 3);
    near(48.0, map.max(), 3);
}

#[test]
fn parses_csv_without_a_units_row() {
    let mut t = Temp::default();
    let path = t.lines(
        ".csv",
        &["Time,RPM,MAP", "0.0,1500,45.0", "0.1,1600,46.0", "0.2,1700,47.0"],
    );

    let log = load(&path);

    assert_eq!(log.format_name(), "CSV");
    assert_eq!(log.sample_count(), 3);
    assert_eq!(channel(&log, "RPM").max(), 1700.0);
    // With no preamble there is no signature to report.
    assert_eq!(log.signature(), None);
}

#[test]
fn skips_ragged_and_annotation_rows() {
    let mut t = Temp::default();
    let path = t.lines(
        ".msl",
        &[
            "Time\tRPM\tMAP",
            "s\tRPM\tkPa",
            "0.0\t1500\t45.0",
            "MARK - an annotation",
            "0.1\t1600\t46.0",
            "",
            "0.2\t1700\t47.0",
        ],
    );

    let log = load(&path);

    assert_eq!(log.sample_count(), 3);
    assert_eq!(channel(&log, "RPM").to_vec(), [1500.0, 1600.0, 1700.0]);
}

#[test]
fn uses_the_time_column_as_the_time_base() {
    let mut t = Temp::default();
    let path = t.lines(".csv", &["Time,RPM", "0.0,1500", "0.5,1600", "1.0,1700"]);

    let log = load(&path);

    assert_eq!(log.time().name(), "Time");
    near(1.0, log.duration(), 3);
}

#[test]
fn falls_back_to_sample_index_when_time_is_not_monotonic() {
    let mut t = Temp::default();
    let path = t.lines(".csv", &["Time,RPM", "5.0,1500", "1.0,1600", "3.0,1700"]);

    let log = load(&path);

    assert_eq!(log.time().name(), "Sample");
    assert_eq!(log.sample_count(), 3);
}

#[test]
fn non_numeric_cells_become_nan_without_breaking_the_column() {
    let mut t = Temp::default();
    let path = t.lines(".csv", &["Time,RPM", "0.0,1500", "0.1,n/a", "0.2,1700"]);

    let log = load(&path);
    let rpm = channel(&log, "RPM");

    assert!(rpm.at(1).is_nan());
    // Min/max must ignore the gap rather than propagate NaN.
    assert_eq!(rpm.min(), 1500.0);
    assert_eq!(rpm.max(), 1700.0);
}

#[test]
fn reports_a_clear_error_for_unrecognised_content() {
    let mut t = Temp::default();
    let path = t.lines(".txt", &["this file", "has no tabular data at all"]);

    assert!(matches!(log_reader_factory::load(&path), Err(Error::LogFormat(_))));
}

#[test]
fn parses_semicolon_separated_decimal_comma_export() {
    // Common for European-locale exports.
    let mut t = Temp::default();
    let path = t.lines(
        ".csv",
        &["Time;RPM;MAP", "0,000;1500;45,5", "0,100;1600;46,5", "0,200;1700;47,5"],
    );

    let log = load(&path);

    assert_eq!(log.format_name(), "semicolon CSV");
    assert_eq!(log.sample_count(), 3);
    near(45.5, channel(&log, "MAP").at(0), 3);
    near(0.2, log.duration(), 3);
}

#[test]
fn honours_quoted_fields_containing_the_delimiter() {
    let mut t = Temp::default();
    let path = t.lines(
        ".csv",
        &[
            "\"Time\",\"Engine Speed, rpm\",\"MAP\"",
            "0.0,1500,45.0",
            "0.1,1600,46.0",
            "0.2,1700,47.0",
        ],
    );

    let log = load(&path);

    assert_eq!(log.channels().len(), 3);
    assert!(log.find_channel("Engine Speed, rpm").is_some());
}

#[test]
fn extracts_units_bracketed_in_the_header() {
    let mut t = Temp::default();
    let path = t.lines(
        ".csv",
        &[
            "Time (s),RPM (rpm),MAP [kPa],CLT {degC}",
            "0.0,1500,45.0,80.0",
            "0.1,1600,46.0,81.0",
            "0.2,1700,47.0,82.0",
        ],
    );

    let log = load(&path);

    assert_eq!(channel(&log, "MAP").units(), "kPa");
    assert_eq!(channel(&log, "RPM").units(), "rpm");
    assert_eq!(channel(&log, "CLT").units(), "degC");
    near(0.2, log.duration(), 3);
}

#[test]
fn disambiguates_repeated_channel_names() {
    // MS3 emits "Fuel Consumption" twice, in different units.
    let mut t = Temp::default();
    let path = t.lines(
        ".msl",
        &[
            "Time\tFuel Consumption\tFuel Consumption",
            "s\tGPH\tl/hr",
            "0.0\t1.0\t3.8",
            "0.1\t1.1\t4.2",
            "0.2\t1.2\t4.5",
        ],
    );

    let log = load(&path);

    assert_eq!(log.channels().len(), 3);
    let mut distinct = names(&log);
    distinct.sort();
    distinct.dedup();
    assert_eq!(distinct.len(), 3);
    near(4.5, channel(&log, "Fuel Consumption (l/hr)").max(), 3);
}

#[test]
fn converts_a_millisecond_time_base_to_seconds() {
    let mut t = Temp::default();
    let path = t.lines(".csv", &["Time,RPM", "0,1500", "500,1600", "1000,1700"]);

    // Units come from the bracketed header form here.
    let path2 = t.lines(".csv", &["Time (ms),RPM", "0,1500", "500,1600", "1000,1700"]);

    near(1000.0, load(&path).duration(), 3);
    near(1.0, load(&path2).duration(), 3);
}

#[test]
fn builds_a_time_base_from_wall_clock_timestamps() {
    let mut t = Temp::default();
    let path = t.lines(
        ".csv",
        &[
            "Timestamp,RPM",
            "2026-07-31 10:00:00.000,1500",
            "2026-07-31 10:00:00.500,1600",
            "2026-07-31 10:00:01.000,1700",
        ],
    );

    let log = load(&path);

    assert_eq!(log.time().name(), "Time");
    near(1.0, log.duration(), 3);
}

#[test]
fn reads_latin1_degree_symbols() {
    let mut t = Temp::default();
    let text = ["Time\tCLT", "s\t\u{b0}F", "0.0\t180.0", "0.1\t181.0", "0.2\t182.0"].join("\n");
    let latin1: Vec<u8> = text.chars().map(|c| c as u8).collect();
    let path = t.bytes("x.msl", &latin1);

    let log = load(&path);

    assert_eq!(channel(&log, "CLT").units(), "\u{b0}F");
    assert!(log.format_name().contains("Latin-1"));
}

#[test]
fn accepts_a_time_base_that_does_not_start_at_zero() {
    // Real MS3 dyno logs start the clock partway through a session.
    let mut t = Temp::default();
    let path = t.lines(
        ".msl",
        &["Time\tRPM", "s\tRPM", "2178.174\t780", "2178.274\t800", "2178.374\t820"],
    );

    let log = load(&path);

    near(2178.174, log.time().at(0), 3);
    near(0.2, log.duration(), 3);
}

#[test]
fn names_the_producing_tool_from_the_preamble() {
    for (preamble, expected) in [
        ("\"MS3 Format 0435.16 : MS3 release\"", "TunerStudio tab-delimited"),
        ("\"MaxxECU log export, MaxxTuner 1.234\"", "MaxxECU tab-delimited"),
        ("\"rusEFI console log\"", "rusEFI tab-delimited"),
        ("\"Haltech NSP datalog\"", "Haltech tab-delimited"),
    ] {
        let mut t = Temp::default();
        let path = t.lines(
            ".msl",
            &[
                preamble,
                "\"Capture Date: whenever\"",
                "Time\tRPM",
                "s\tRPM",
                "0.0\t1500",
                "0.1\t1600",
                "0.2\t1700",
            ],
        );

        assert_eq!(load(&path).format_name(), expected, "{preamble}");
    }
}

// ----- LogDocumentTests ----------------------------------------------------

fn build_times(t: &mut Temp, times: &[f64]) -> LogDocument {
    let mut lines = vec!["Time,RPM".to_string()];
    lines.extend(times.iter().enumerate().map(|(i, time)| format!("{time},{}", 1500 + i)));
    let refs: Vec<&str> = lines.iter().map(String::as_str).collect();
    load(&t.lines(".csv", &refs))
}

#[test]
fn median_interval_ignores_a_pause_in_logging() {
    // Steady 0.1 s sampling with one 60 s pause part way through.
    let mut t = Temp::default();
    let log = build_times(&mut t, &[0.0, 0.1, 0.2, 0.3, 60.3, 60.4, 60.5, 60.6]);

    near(0.1, log.median_sample_interval(), 3);
    near(1.0, log.gap_threshold(), 3);
}

#[test]
fn gap_threshold_separates_real_pauses_from_jitter() {
    let mut t = Temp::default();
    let log = build_times(&mut t, &[0.0, 0.1, 0.21, 0.29, 0.4, 0.5]);

    // Ordinary jitter must stay below the threshold.
    let times = log.time().to_vec();
    for i in 1..times.len() {
        assert!(times[i] - times[i - 1] < log.gap_threshold());
    }
}

#[test]
fn gap_threshold_is_infinite_when_there_is_no_usable_interval() {
    let mut t = Temp::default();
    let log = build_times(&mut t, &[5.0, 5.0]);

    assert!(log.gap_threshold().is_infinite() && log.gap_threshold() > 0.0);
}

#[test]
fn channel_records_where_its_extremes_occur() {
    let mut t = Temp::default();
    let path = t.lines(".csv", &["Time,RPM", "0.0,1500", "0.1,900", "0.2,6200", "0.3,3000"]);

    let log = load(&path);
    let rpm = channel(&log, "RPM");

    assert_eq!(rpm.max(), 6200.0);
    assert_eq!(rpm.max_index(), Some(2));
    assert_eq!(rpm.min(), 900.0);
    assert_eq!(rpm.min_index(), Some(1));
}

#[test]
fn extreme_indexes_ignore_gaps_and_take_the_first_occurrence() {
    let mut t = Temp::default();
    let path = t.lines(
        ".csv",
        &["Time,RPM", "0.0,4000", "0.1,n/a", "0.2,4000", "0.3,1000", "0.4,1000"],
    );

    let log = load(&path);
    let rpm = channel(&log, "RPM");

    assert_eq!(rpm.max_index(), Some(0));
    assert_eq!(rpm.min_index(), Some(3));
}

#[test]
fn extreme_indexes_are_unset_for_an_empty_channel() {
    let empty = LogChannel::new("Nothing", "", 0, &[], false);

    assert_eq!(empty.min_index(), None);
    assert_eq!(empty.max_index(), None);
}

#[test]
fn duration_and_index_lookup_handle_a_log_that_starts_late() {
    // Real dyno logs start the clock partway through a session.
    let mut t = Temp::default();
    let log = build_times(&mut t, &[6310.743, 6310.81, 6310.877, 6310.944]);

    near(0.201, log.duration(), 3);
    assert_eq!(log.index_at_time(0.0), 0);
    assert_eq!(log.index_at_time(99999.0), 3);
    assert_eq!(log.index_at_time(6310.85), 1);
}

// ----- MaxxLogTests --------------------------------------------------------

/// A zip with stored (uncompressed) entries, or deflated where asked.
fn zip(entries: &[(&str, &[u8], bool)]) -> Vec<u8> {
    fn crc32(data: &[u8]) -> u32 {
        let mut crc = 0xFFFF_FFFFu32;
        for &b in data {
            crc ^= u32::from(b);
            for _ in 0..8 {
                crc = if crc & 1 != 0 {
                    (crc >> 1) ^ 0xEDB8_8320
                } else {
                    crc >> 1
                };
            }
        }
        !crc
    }

    let mut out = Vec::new();
    let mut central = Vec::new();
    for (name, data, deflated) in entries {
        let stored: Vec<u8> = if *deflated {
            miniz_oxide::deflate::compress_to_vec(data, 6)
        } else {
            data.to_vec()
        };
        let method: u16 = if *deflated { 8 } else { 0 };
        let offset = out.len() as u32;
        let crc = crc32(data);

        out.extend_from_slice(&0x0403_4b50u32.to_le_bytes());
        out.extend_from_slice(&20u16.to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(&method.to_le_bytes());
        out.extend_from_slice(&[0, 0, 0, 0]);
        out.extend_from_slice(&crc.to_le_bytes());
        out.extend_from_slice(&(stored.len() as u32).to_le_bytes());
        out.extend_from_slice(&(data.len() as u32).to_le_bytes());
        out.extend_from_slice(&(name.len() as u16).to_le_bytes());
        out.extend_from_slice(&0u16.to_le_bytes());
        out.extend_from_slice(name.as_bytes());
        out.extend_from_slice(&stored);

        central.extend_from_slice(&0x0201_4b50u32.to_le_bytes());
        central.extend_from_slice(&20u16.to_le_bytes());
        central.extend_from_slice(&20u16.to_le_bytes());
        central.extend_from_slice(&0u16.to_le_bytes());
        central.extend_from_slice(&method.to_le_bytes());
        central.extend_from_slice(&[0, 0, 0, 0]);
        central.extend_from_slice(&crc.to_le_bytes());
        central.extend_from_slice(&(stored.len() as u32).to_le_bytes());
        central.extend_from_slice(&(data.len() as u32).to_le_bytes());
        central.extend_from_slice(&(name.len() as u16).to_le_bytes());
        central.extend_from_slice(&[0; 12]);
        central.extend_from_slice(&offset.to_le_bytes());
        central.extend_from_slice(name.as_bytes());
    }

    let cd_offset = out.len() as u32;
    out.extend_from_slice(&central);
    out.extend_from_slice(&0x0605_4b50u32.to_le_bytes());
    out.extend_from_slice(&[0; 4]);
    out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
    out.extend_from_slice(&(entries.len() as u16).to_le_bytes());
    out.extend_from_slice(&(central.len() as u32).to_le_bytes());
    out.extend_from_slice(&cd_offset.to_le_bytes());
    out.extend_from_slice(&0u16.to_le_bytes());
    out
}

/// Header names carry MTune's channel index in brackets.
const BODY: &str = "RPM [61]\tCoolant temp [18]\tMAP [20]\tLambda [5]\t\n\
800\t85.5\t101.3\t0.98\t\n\
1200\t86.1\t150.2\t0.85\t\n\
3000\t87.0\t218.7\t0.61\t\n";

/// Builds an archive shaped like the real thing.
fn write_maxx(
    t: &mut Temp,
    body: &str,
    metadata: Option<&str>,
    with_log: bool,
    with_tune: bool,
    deflate: bool,
) -> PathBuf {
    let mut entries: Vec<(&str, &[u8], bool)> = Vec::new();
    if with_log {
        entries.push(("log_2025-01-16_123737.MaxxECU-Log", body.as_bytes(), deflate));
    }
    if let Some(m) = metadata {
        entries.push(("fileinfo01.LogMetaData", m.as_bytes(), deflate));
    }
    if with_tune {
        entries.push(("V8 2025-01-16_123744.MaxxECU-save", b"", false));
    }
    t.bytes("maxx.MaxxECU-Zip-log", &zip(&entries))
}

fn maxx(t: &mut Temp, body: &str) -> PathBuf {
    write_maxx(t, body, Some("LogRate=0.0501985546875"), true, true, false)
}

#[test]
fn a_maxx_ecu_archive_is_recognised() {
    let mut t = Temp::default();
    let path = maxx(&mut t, BODY);
    assert!(MaxxLogReader.can_read(&path));
}

#[test]
fn something_else_is_not() {
    // A zip with no MaxxECU log in it, and a file that is not a zip at all.
    let mut t = Temp::default();
    let not_maxx = t.bytes("other.zip", &zip(&[("something.txt", b"", false)]));
    assert!(!MaxxLogReader.can_read(&not_maxx));

    let text = t.bytes("text.csv", b"Time,RPM\n0,800\n");
    assert!(!MaxxLogReader.can_read(&text));
}

#[test]
fn the_factory_picks_it_up() {
    let mut t = Temp::default();
    let path = maxx(&mut t, BODY);

    assert_eq!(load(&path).format_name(), "MaxxECU");
}

#[test]
fn channels_keep_their_names_without_the_index() {
    // The bracketed index is how MTune's definitions are keyed, and noise in a
    // channel list.
    let mut t = Temp::default();
    let path = maxx(&mut t, BODY);

    assert_eq!(names(&load(&path)), ["RPM", "Coolant temp", "MAP", "Lambda"]);
}

#[test]
fn values_arrive_as_recorded() {
    let mut t = Temp::default();
    let path = maxx(&mut t, BODY);
    let log = load(&path);

    assert_eq!(log.sample_count(), 3);
    near(800.0, channel(&log, "RPM").at(0), 3);
    near(218.7, channel(&log, "MAP").at(2), 3);
    near(0.61, channel(&log, "Lambda").at(2), 3);
}

#[test]
fn time_comes_from_the_declared_rate() {
    // There is no time column; the metadata is the only thing that says when
    // anything happened.
    let mut t = Temp::default();
    let path = maxx(&mut t, BODY);
    let log = load(&path);

    near(0.0, log.time().at(0), 6);
    near(0.0501985546875, log.time().at(1), 9);
    near(0.100397109375, log.time().at(2), 9);
}

#[test]
fn a_missing_rate_still_opens() {
    // Wrong by a constant keeps every trace the right shape and only mislabels
    // the axis, which beats refusing the file.
    let mut t = Temp::default();
    let path = write_maxx(&mut t, BODY, None, true, true, false);
    let log = load(&path);

    assert_eq!(log.sample_count(), 3);
    assert!(log.time().at(1) > log.time().at(0));
}

#[test]
fn a_short_row_loses_only_its_own_channels() {
    // Filling the gap keeps every later channel in its own column; dropping it
    // would shift them all up by one.
    let ragged = "RPM [61]\tCoolant temp [18]\tMAP [20]\t\n800\t85.5\t101.3\t\n900\t86.0\t\n";

    let mut t = Temp::default();
    let path = maxx(&mut t, ragged);
    let log = load(&path);

    near(900.0, channel(&log, "RPM").at(1), 3);
    near(86.0, channel(&log, "Coolant temp").at(1), 3);
    assert!(channel(&log, "MAP").at(1).is_nan());
}

#[test]
fn two_channels_of_the_same_name_stay_separate() {
    // Names have to be unique: a preset or a filter matches on one.
    let mut t = Temp::default();
    let path = maxx(&mut t, "RPM [61]\tRPM [62]\t\n800\t810\t\n");
    let log = load(&path);

    assert_eq!(log.channels().len(), 2);
    let mut distinct = names(&log);
    distinct.sort();
    distinct.dedup();
    assert_eq!(distinct.len(), 2);
}

#[test]
fn an_archive_with_no_log_is_refused_clearly() {
    let mut t = Temp::default();
    let path = write_maxx(&mut t, BODY, Some("LogRate=0.05"), false, true, false);

    assert!(!MaxxLogReader.can_read(&path));
    assert!(matches!(log_reader_factory::load(&path), Err(Error::LogFormat(_))));
}

#[test]
fn an_empty_log_is_refused_rather_than_opened_empty() {
    let mut t = Temp::default();
    let path = maxx(&mut t, "RPM [61]\t\n");

    assert!(matches!(log_reader_factory::load(&path), Err(Error::LogFormat(_))));
}

// ----- beyond the C# tests: the parts Rust had to write itself -------------

#[test]
fn a_deflated_archive_reads_the_same_as_a_stored_one() {
    // The C# gets deflate from System.IO.Compression; here it is miniz_oxide.
    let mut t = Temp::default();
    let path = write_maxx(&mut t, BODY, Some("LogRate=0.0501985546875"), true, true, true);
    let log = load(&path);

    assert_eq!(names(&log), ["RPM", "Coolant temp", "MAP", "Lambda"]);
    near(0.100397109375, log.time().at(2), 9);
    assert_eq!(log.unreadable_tune(), Some("MaxxECU"));
}

#[test]
fn a_missing_file_is_an_io_error() {
    let mut t = Temp::default();
    let path = t.path("never-written.mlg");

    let Err(Error::Io(e)) = log_reader_factory::load(&path) else {
        panic!("expected an Io error");
    };
    assert_eq!(e.to_string(), "Log file not found.");
}

#[test]
fn a_file_no_reader_knows_is_refused_with_its_name() {
    let mut t = Temp::default();
    let path = t.bytes("blob.bin", &[0xFF, 0xFE, 0x00, 0x01]);

    let Err(Error::LogFormat(message)) = log_reader_factory::load(&path) else {
        panic!("expected a LogFormat error");
    };
    assert!(
        message.ends_with("blob.bin' is not a recognised datalog format."),
        "{message}"
    );
}
