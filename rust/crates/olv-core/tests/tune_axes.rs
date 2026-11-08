//! The C# `TuneAxesTests`: reading axes out of a tune, and binning a log onto them.
//!
//! `MsqTune.ParseBreakpoints` is crate-private in the port, so the breakpoint-parsing tests go
//! through [`read_axis_sets`], which parses an axis exactly as `ParseBreakpoints` does and
//! drops a pair whose axis it refuses. A refused axis is observed as a skipped set, with the
//! other axis of the pair always valid.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use olv_core::analysis::histogram_table::{HistogramInputs, HistogramStatistic, HistogramTable};
use olv_core::filters::log_filter::{FilterComparison, LogFilter, SampleFilter};
use olv_core::logs::log_reader_factory;
use olv_core::logs::mlg_reader::MlgDataType;
use olv_core::model::tune_axes::{TuneAxis, read_axis_sets};
use olv_core::model::{LogChannel, LogDocument};

/// An MSQ shaped like the real thing, default namespace and all.
fn msq(constants: &[(&str, &str, &str)]) -> String {
    let body = constants
        .iter()
        .map(|(name, units, values)| {
            format!(r#"<constant cols="1" digits="0" name="{name}" rows="4" units="{units}">{values}</constant>"#)
        })
        .collect::<Vec<_>>()
        .join("\n");

    format!(
        "<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\n<msq xmlns=\"http://www.msefi.com/:msq\">\n<page>\n{body}\n</page>\n</msq>"
    )
}

/// The breakpoints `ParseBreakpoints` makes of `text`, or `None` where it refuses it, seen
/// through a VE table's X axis with a valid Y axis beside it.
fn parse_breakpoints(text: &str) -> Option<Vec<f64>> {
    let xml = msq(&[("frpm_table1", "RPM", text), ("fmap_table1", "kPa", "30 50 70 100")]);
    read_axis_sets(Some(&xml)).into_iter().next().map(|s| s.x.breakpoints)
}

// ----- breakpoint parsing ------------------------------------------------

#[test]
fn ascending_breakpoints_are_read() {
    let axis = parse_breakpoints("500.0 800.0 1100.0 1400.0");

    assert_eq!(Some(vec![500.0, 800.0, 1100.0, 1400.0]), axis);
}

#[test]
fn padding_repeats_at_the_top_are_collapsed() {
    // Firmwares pad an axis to the table width by repeating the top value; kept as-is they
    // would create zero-width bins.
    let axis = parse_breakpoints("500 800 1100 6500 7000 7000 7000");

    assert_eq!(Some(vec![500.0, 800.0, 1100.0, 6500.0, 7000.0]), axis);
}

#[test]
fn rolled_dozen_axes_are_rejected() {
    // frpm_table2doz is stored out of order, not ascending; binning onto it would silently
    // scramble the table.
    let axis = parse_breakpoints("5200 5700 6100 6500 502 801 1101 1401");

    assert_eq!(None, axis);
}

#[test]
fn unusable_axes_are_rejected() {
    for text in [
        "500 800",     // too few points to be an axis
        "500 500 500", // collapses to one point
        "500 800 not-a-number",
        "",
    ] {
        assert_eq!(None, parse_breakpoints(text), "{text:?}");
    }
}

// ----- axis set discovery ------------------------------------------------

#[test]
fn known_table_pairs_are_found_through_the_default_namespace() {
    let xml = msq(&[
        ("frpm_table1", "RPM", "500 800 1100 1400"),
        ("fmap_table1", "kPa", "30 50 70 100"),
        ("srpm_table1", "RPM", "700 1000 1300 1600"),
        ("smap_table1", "kPa", "20 40 60 80"),
    ]);

    let sets = read_axis_sets(Some(&xml));

    assert_eq!(2, sets.len());
    assert_eq!("VE table 1", sets[0].name);
    assert_eq!(vec![500.0, 800.0, 1100.0, 1400.0], sets[0].x.breakpoints);
    assert_eq!("kPa", sets[0].y.units);
    assert_eq!("Spark table 1", sets[1].name);
}

#[test]
fn a_pair_missing_one_axis_is_skipped() {
    let xml = msq(&[("frpm_table1", "RPM", "500 800 1100 1400")]);

    assert!(read_axis_sets(Some(&xml)).is_empty());
}

#[test]
fn a_pair_with_an_unusable_axis_is_skipped() {
    let xml = msq(&[
        ("frpm_table1", "RPM", "5200 5700 6100 502"), // rolled
        ("fmap_table1", "kPa", "30 50 70 100"),
    ]);

    assert!(read_axis_sets(Some(&xml)).is_empty());
}

#[test]
fn unusable_tune_text_yields_no_axes() {
    for xml in [None, Some(""), Some("not xml at all"), Some("<msq><unclosed></msq>")] {
        assert!(read_axis_sets(xml).is_empty(), "{xml:?}");
    }
}

// ----- binning onto tune breakpoints -------------------------------------

fn channel(name: &str, values: &[f64]) -> Arc<LogChannel> {
    Arc::new(LogChannel::new(name, "", 2, values, false))
}

fn on_breakpoints(
    (x, y, z): (&Arc<LogChannel>, &Arc<LogChannel>, &Arc<LogChannel>),
    columns: &[f64],
    rows: &[f64],
    last: isize,
) -> HistogramTable {
    HistogramTable::build_on_breakpoints(
        HistogramInputs::new(x, y, z),
        columns,
        rows,
        0,
        last,
        HistogramStatistic::Mean,
    )
    .unwrap()
}

#[test]
fn samples_go_to_the_nearest_breakpoint() {
    // Breakpoints 1000/2000/3000: 1400 is nearer 1000, 1600 nearer 2000.
    let x = channel("RPM", &[1400.0, 1600.0, 2900.0]);
    let y = channel("MAP", &[50.0, 50.0, 50.0]);
    let z = channel("AFR", &[10.0, 20.0, 30.0]);

    let table = on_breakpoints((&x, &y, &z), &[1000.0, 2000.0, 3000.0], &[50.0], 2);

    assert!(table.from_tune);
    assert_eq!(Some(10.0), table.values[(0, 0)]);
    assert_eq!(Some(20.0), table.values[(1, 0)]);
    assert_eq!(Some(30.0), table.values[(2, 0)]);
}

#[test]
fn values_outside_the_breakpoints_fall_to_the_nearest_end() {
    // Nothing is discarded for being off the end of the tune's axis.
    let x = channel("RPM", &[100.0, 9000.0]);
    let y = channel("MAP", &[50.0, 50.0]);
    let z = channel("AFR", &[11.0, 12.0]);

    let table = on_breakpoints((&x, &y, &z), &[1000.0, 2000.0, 3000.0], &[50.0], 1);

    assert_eq!(Some(11.0), table.values[(0, 0)]);
    assert_eq!(Some(12.0), table.values[(2, 0)]);
    assert_eq!(2, table.sample_count);
}

#[test]
fn uneven_breakpoint_spacing_is_honoured() {
    // A real RPM axis is tight at idle and wide up top. Uniform arithmetic over the same range
    // would put both of these in the wrong cell.
    let rpm = [500.0, 800.0, 1100.0, 1400.0, 2000.0, 2600.0, 3100.0, 6000.0];
    let x = channel("RPM", &[1300.0, 5000.0]);
    let y = channel("MAP", &[40.0, 40.0]);
    let z = channel("AFR", &[14.0, 12.0]);

    let table = on_breakpoints((&x, &y, &z), &rpm, &[40.0], 1);

    assert_eq!(Some(14.0), table.values[(3, 0)]); // 1300 -> 1400
    assert_eq!(Some(12.0), table.values[(7, 0)]); // 5000 -> 6000
}

#[test]
fn a_value_exactly_between_breakpoints_takes_the_lower_one() {
    // 1250 is equidistant from 1100 and 1400; the choice is arbitrary but must be consistent,
    // or the same log would bin differently each run.
    let x = channel("RPM", &[1250.0]);
    let y = channel("MAP", &[40.0]);
    let z = channel("AFR", &[13.0]);

    let table = on_breakpoints((&x, &y, &z), &[1100.0, 1400.0], &[40.0], 0);

    assert_eq!(Some(13.0), table.values[(0, 0)]);
    assert_eq!(None, table.values[(1, 0)]);
}

#[test]
fn tune_axes_combine_with_filters() {
    let x = channel("RPM", &[1000.0, 1000.0, 2000.0, 2000.0]);
    let y = channel("MAP", &[50.0, 50.0, 50.0, 50.0]);
    let z = channel("AFR", &[10.0, 20.0, 30.0, 40.0]);
    let clt = channel("CLT", &[100.0, 180.0, 100.0, 180.0]);

    let doc = LogDocument::new(
        "x",
        "test",
        vec![x.clone(), y.clone(), z.clone(), clt],
        channel("Time", &[0.0, 1.0, 2.0, 3.0]),
    );

    let warm = SampleFilter::build(
        &doc,
        &[LogFilter::new("warm", "CLT", FilterComparison::AboveOrEqual).with_low(160.0)],
    );

    let table = HistogramTable::build_on_breakpoints(
        HistogramInputs::new(&x, &y, &z).with_mask(Some(&warm)),
        &[1000.0, 2000.0],
        &[50.0],
        0,
        3,
        HistogramStatistic::Mean,
    )
    .unwrap();

    assert_eq!(2, table.sample_count);
    assert_eq!(Some(20.0), table.values[(0, 0)]);
    assert_eq!(Some(40.0), table.values[(1, 0)]);
}

// ----- extraction from a log --------------------------------------------

/// The part of the C# `MlgBuilder` these tests need (the full one lives in `logs_readers.rs`,
/// which an integration test cannot import): an F32 Time and a U16 RPM, with an optional tune
/// embedded after the signature and capture strings.
fn mlg_bytes(samples: usize, embedded_tune: Option<&str>) -> Vec<u8> {
    const DESCRIPTOR: usize = 89;
    let fields = [
        (MlgDataType::F32, "Time", "s", 4usize),
        (MlgDataType::U16, "RPM", "RPM", 2),
    ];
    let payload: usize = fields.iter().map(|f| f.3).sum();

    let info_start = 24 + fields.len() * DESCRIPTOR;
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
    head[20..22].copy_from_slice(&(payload as u16).to_be_bytes());
    head[22..24].copy_from_slice(&(fields.len() as u16).to_be_bytes());
    buffer.extend_from_slice(&head);

    for (ty, name, units, _) in &fields {
        let mut d = [0u8; DESCRIPTOR];
        d[0] = *ty as u8;
        d[1..1 + name.len()].copy_from_slice(name.as_bytes());
        d[35..35 + units.len()].copy_from_slice(units.as_bytes());
        d[46..50].copy_from_slice(&1.0f32.to_be_bytes());
        buffer.extend_from_slice(&d);
    }
    buffer.extend_from_slice(&info);

    for s in 0..samples {
        let mut record = vec![0u8; 4 + payload + 1];
        record[1] = s as u8;
        record[2..4].copy_from_slice(&((s * 100) as u16).to_be_bytes());
        record[4..8].copy_from_slice(&(s as f32).to_be_bytes());
        record[8..10].copy_from_slice(&((1000 + s) as u16).to_be_bytes());
        buffer.extend_from_slice(&record);
    }
    buffer
}

static NEXT: AtomicUsize = AtomicUsize::new(0);

/// A temp file removed when dropped (the C#'s `IDisposable` test class).
struct TempFile(PathBuf);

impl TempFile {
    fn new(bytes: &[u8]) -> Self {
        let p = std::env::temp_dir().join(format!(
            "olv-tuneaxes-{}-{}.mlg",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::write(&p, bytes).unwrap();
        Self(p)
    }
}

impl Drop for TempFile {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

#[test]
fn the_tune_is_recovered_from_an_mlg() {
    let xml = msq(&[
        ("frpm_table1", "RPM", "500 800 1100 1400"),
        ("fmap_table1", "kPa", "30 50 70 100"),
    ]);

    let file = TempFile::new(&mlg_bytes(5, Some(&xml)));

    let log = log_reader_factory::load(&file.0).unwrap();

    let tune = log.embedded_tune().expect("the log should carry its tune");
    assert!(tune.contains("frpm_table1"));

    let sets = read_axis_sets(Some(tune));
    assert_eq!(1, sets.len());
    assert_eq!(vec![500.0, 800.0, 1100.0, 1400.0], sets[0].x.breakpoints);
}

#[test]
fn a_log_with_no_tune_simply_has_none() {
    let file = TempFile::new(&mlg_bytes(3, None));

    assert!(log_reader_factory::load(&file.0).unwrap().embedded_tune().is_none());
}

// ----- units an INI left as an expression --------------------------------

fn axis_with_units(units: &str) -> TuneAxis {
    TuneAxis {
        constant: "c".into(),
        units: units.into(),
        breakpoints: vec![1.0],
    }
}

#[test]
fn a_plain_unit_is_shown_as_it_is() {
    for (declared, expected) in [("kPa", "kPa"), ("RPM", "RPM"), ("%", "%"), ("  deg  ", "deg")] {
        assert_eq!(expected, axis_with_units(declared).plain_units());
    }
}

#[test]
fn anything_to_be_evaluated_is_shown_as_nothing() {
    // What MS2Extra actually declares for its load axis, because the axis is kilopascals on
    // speed density and per cent on alpha-N and the ECU decides at runtime. Nothing here
    // evaluates it, and printing it verbatim put a line of INI source where a tuner expected
    // "kPa".
    for declared in [
        "{ bitStringValue( algorithmUnits , algorithm ) }",
        "{ someExpression }",
        "bitStringValue(a, b)",
        "",
        "   ",
    ] {
        assert_eq!("", axis_with_units(declared).plain_units(), "{declared:?}");
    }
}

#[test]
fn no_unit_beats_a_wrong_one() {
    // The breakpoints are still shown either way. A number with no unit reads as an unlabelled
    // axis; a number with an INI expression after it reads as a bug, which is what it was.
    let axis = TuneAxis {
        constant: "mapBins".into(),
        units: "{ bitStringValue( algorithmUnits , algorithm ) }".into(),
        breakpoints: vec![30.0, 100.0],
    };

    assert!(axis.plain_units().is_empty());
    assert_eq!(2, axis.breakpoints.len());
}
