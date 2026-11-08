//! The live package against the C# reference (`rust/oracle/expected/w4-live/*.json`).
//!
//! `tools/OpenLogViewer.Oracle/Emitters/W4Live.cs` runs the shipping C# against a model of a MaxxECU
//! (a 64 KB tune, a CAN ring, a drop counter, and ways to misbehave) behind a transport that records
//! every byte it is sent. It writes the requests the tune code builds across offsets and lengths,
//! every write scenario (with the guards that refuse a write before anything is sent), the table
//! CRC over sample tables, the layout and settings pages a tune is shaped into, the CAN ring decode,
//! the bytes a recording is made of, and a replay of the MTune capture. The Rust runs the same
//! scenarios against the same model and must send the same bytes and end every one the same way.
//!
//! Nothing here is ever pointed at a real ECU: MaxxECU writes are live and permanent.

mod live_doubles;

use live_doubles::model::{self, LossyEcu, ModelEcu, hex};
use live_doubles::{RecordedEcu, TempFiles, wait_for};
use olv_core::Error;
use olv_core::logs::maxx_channel_definitions::MaxxChannelDefinition;
use olv_core::model::{CanFrame, LiveSource};
use olv_live::live::live_session::{LiveSession, LiveSessionSettings};
use olv_live::live::maxx_can;
use olv_live::live::maxx_can_source::MaxxCanSource;
use olv_live::live::maxx_tune::{self, MaxxTable, MaxxWriteStatus};
use olv_live::live::maxx_tune_definitions::{self, MaxxSettingDefinition};
use olv_live::live::maxx_usb_source::{self, MaxxUsbSource};
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use serde::Deserialize;

/// A double the C# wrote as its IEEE bits, with NaN meaning "any NaN" (the C#'s `double.NaN` has
/// its sign bit set; Rust's does not, and neither is meaningful).
fn same_f64(bits: &str, actual: f64) -> bool {
    let expected = f64::from_bits(u64::from_str_radix(bits, 16).expect("hex bits"));

    if expected.is_nan() {
        actual.is_nan()
    } else {
        expected.to_bits() == actual.to_bits()
    }
}

/// The C# exception types, against the one `Error` variant each becomes.
fn kind_matches(expected: Option<&str>, actual: Option<&Error>) -> bool {
    matches!(
        (expected, actual),
        (None, None)
            | (
                Some("ArgumentOutOfRangeException" | "ArgumentException"),
                Some(Error::Argument(_))
            )
            | (Some("EcuProtocolException"), Some(Error::Protocol(_)))
    )
}

fn hex8(value: u32) -> String {
    format!("{value:08X}")
}

fn unix(text: &str) -> String {
    text.replace("\r\n", "\n")
}

// ----- requests ---------------------------------------------------------------------

#[derive(Deserialize)]
struct Requests {
    name: String,
    requests: Vec<String>,
    result: String,
    error_kind: Option<String>,
    message: Option<String>,
    discards: i32,
    retries: i32,
    progress: Vec<String>,
    blob: usize,
}

fn can_record(id: i32, length: usize, at: u32) -> Vec<u8> {
    let mut record = vec![0u8; maxx_can::RECORD_LENGTH];
    record[0] = (length & 0x0F) as u8;
    record[1..5].copy_from_slice(&(id as u32).to_le_bytes());
    record[5..9].copy_from_slice(&at.to_le_bytes());
    for i in 0..8 {
        record[9 + i] = (0xA0 + id + i as i32) as u8;
    }
    record
}

struct Outcome {
    requests: Vec<String>,
    result: String,
    error: Option<Error>,
    retries: i32,
    discards: i32,
    progress: Vec<String>,
    blob: usize,
}

fn outcome(ecu: &ModelEcu, result: String, error: Option<Error>) -> Outcome {
    Outcome {
        requests: ecu.written_hex(),
        result,
        error,
        retries: 0,
        discards: 0,
        progress: Vec::new(),
        blob: 0,
    }
}

fn bool_text(value: bool) -> String {
    if value { "True" } else { "False" }.to_string()
}

fn run_request(name: &str) -> Outcome {
    let ecu = ModelEcu::new();

    if name == "read_all" {
        let mut progress = Vec::new();
        let mut record = |done: i32, total: i32| progress.push(format!("{done}/{total}"));
        let blob = maxx_tune::read(&mut ecu.clone(), Some(&mut record), None).unwrap();

        return Outcome {
            progress,
            blob: blob.len(),
            ..outcome(&ecu, hex8(maxx_protocol::crc32(&blob)), None)
        };
    }

    if name == "read_all_clean_retries" {
        let mut retries = 0;
        let mut count = || retries += 1;
        let blob = maxx_tune::read(&mut ecu.clone(), None, Some(&mut count)).unwrap();

        return Outcome {
            retries,
            ..outcome(&ecu, hex8(maxx_protocol::crc32(&blob)), None)
        };
    }

    if name == "read_all_with_losses" {
        let mut flaky = LossyEcu::new(&[3, 4]);
        let discards = std::sync::Arc::clone(&flaky.discards);
        let mut retries = 0;
        let mut count = || retries += 1;
        let blob = maxx_tune::read(&mut flaky, None, Some(&mut count)).unwrap();

        let inner = flaky.inner.clone();
        let discards = *discards.lock().unwrap();

        return Outcome {
            retries,
            discards,
            ..outcome(&inner, hex8(maxx_protocol::crc32(&blob)), None)
        };
    }

    if name == "read_silent" {
        ecu.with(|s| s.silent_replies = i32::MAX);
        let error = maxx_tune::read(&mut ecu.clone(), None, None).unwrap_err();

        return outcome(&ecu, String::new(), Some(error));
    }

    if let Some(rest) = name.strip_prefix("verify_wrong_") {
        let (offset, length) = rest.split_once('_').unwrap();
        let (offset, length): (usize, usize) = (offset.parse().unwrap(), length.parse().unwrap());

        let mut wrong = ecu.with(|s| s.tune[offset..offset + length].to_vec());
        wrong[0] ^= 1;

        let verified = maxx_tune::verify(&mut ecu.clone(), offset as i32, &wrong).unwrap();
        return outcome(&ecu, bool_text(verified), None);
    }

    if let Some(rest) = name.strip_prefix("verify_") {
        let (offset, length) = rest.split_once('_').unwrap();
        let (offset, length): (usize, usize) = (offset.parse().unwrap(), length.parse().unwrap());

        let expected = ecu.with(|s| s.tune[offset..offset + length].to_vec());
        let verified = maxx_tune::verify(&mut ecu.clone(), offset as i32, &expected).unwrap();
        return outcome(&ecu, bool_text(verified), None);
    }

    if name == "checksums_unanswered" {
        ecu.with(|s| s.silent_replies = 1);
        let sums = maxx_tune::checksums(&mut ecu.clone(), maxx_tune::WRITE_CEILING).unwrap();
        return outcome(&ecu, sums.len().to_string(), None);
    }

    if let Some(rest) = name.strip_prefix("checksums_") {
        let covers: i32 = rest.parse().unwrap();

        return match maxx_tune::checksums(&mut ecu.clone(), covers) {
            Ok(sums) => outcome(&ecu, sums.iter().map(|s| hex8(*s)).collect::<Vec<_>>().join(","), None),
            Err(e) => outcome(&ecu, String::new(), Some(e)),
        };
    }

    if let Some(rest) = name.strip_prefix("can_ask_") {
        let frames: i64 = rest.parse().unwrap();

        // A negative count has no `usize`; the C#'s refusal of it is the type's.
        let asked = usize::try_from(frames).map_err(|_| Error::Argument("negative".to_string()));

        return match asked.and_then(maxx_can::ask) {
            Ok(request) => Outcome {
                requests: vec![hex(&request)],
                ..outcome(&ecu, String::new(), None)
            },
            Err(e) => Outcome {
                requests: Vec::new(),
                ..outcome(&ecu, String::new(), Some(e))
            },
        };
    }

    if name == "can_drop_count" {
        ecu.with(|s| s.drop_counter = 0xFFF0);
        let source = MaxxUsbSource::new(Box::new(ecu.clone()), None);
        let count = source.can_drop_count().unwrap();
        return outcome(&ecu, count.map_or("null".to_string(), |c| c.to_string()), None);
    }

    if name == "can_read_frames" {
        for i in 0..20 {
            ecu.enqueue(can_record(0x100 + i, 1 + (i as usize % 8), (1000 + i) as u32));
        }
        let source = MaxxUsbSource::new(Box::new(ecu.clone()), None);
        let frames = source.read_can_frames(maxx_can::MOST_PER_READ).unwrap();

        let text = frames
            .iter()
            .map(|f: &CanFrame| format!("{:X}:{}:{}:{}", f.id, bool_text(f.is_extended), hex(&f.data), f.at))
            .collect::<Vec<_>>()
            .join(";");
        return outcome(&ecu, text, None);
    }

    panic!("the oracle has a request case this test does not know: {name}");
}

#[test]
fn every_request_the_tune_and_can_code_builds_matches_the_csharp_byte_for_byte() {
    let cases: Vec<Requests> = olv_oracle::cases("w4-live", "maxx_requests");
    assert_eq!(cases.len(), 48);

    for c in &cases {
        let got = run_request(&c.name);

        assert_eq!(got.requests, c.requests, "{}: requests", c.name);
        assert_eq!(got.result, c.result, "{}: result", c.name);
        assert!(
            kind_matches(c.error_kind.as_deref(), got.error.as_ref()),
            "{}: {:?} against {:?}",
            c.name,
            c.error_kind,
            got.error
        );
        assert_eq!(got.retries, c.retries, "{}: retries", c.name);
        assert_eq!(got.discards, c.discards, "{}: discards", c.name);
        assert_eq!(got.progress, c.progress, "{}: progress", c.name);
        assert_eq!(got.blob, c.blob, "{}: blob", c.name);

        // The messages the C# gives that this port words the same way. (Others differ in text only:
        // .NET words its argument checks its own way, and the kind is what is compared.)
        if let (Some(expected), Some(error)) = (&c.message, &got.error)
            && matches!(error, Error::Protocol(_))
        {
            assert_eq!(unix(expected), error.to_string(), "{}: message", c.name);
        }
    }
}

// ----- writes ---------------------------------------------------------------------

#[derive(Deserialize)]
struct Write {
    name: String,
    offset: i32,
    length: i32,
    header: u8,
    answer_header: bool,
    answer_payload: bool,
    requests: Vec<String>,
    status: Option<String>,
    status_value: Option<i32>,
    error_kind: Option<String>,
    message: Option<String>,
    tune_crc: String,
}

#[test]
fn every_write_scenario_sends_the_same_bytes_and_is_refused_in_the_same_places() {
    let cases: Vec<Write> = olv_oracle::cases("w4-live", "maxx_writes");
    assert_eq!(cases.len(), 27);

    for c in &cases {
        let ecu = ModelEcu::new();
        ecu.with(|s| {
            s.header_status = c.header;
            s.answer_header = c.answer_header;
            s.answer_payload = c.answer_payload;
        });

        let data: Vec<u8> = (0..c.length.max(0))
            .map(|i| (0x30 + i64::from(i) + i64::from(c.offset)) as u8)
            .collect();

        let result = maxx_tune::write(&mut ecu.clone(), c.offset, &data);

        assert_eq!(ecu.written_hex(), c.requests, "{}: what went on the wire", c.name);
        assert_eq!(ecu.tune_crc(), c.tune_crc, "{}: the tune afterwards", c.name);

        match &result {
            Ok(status) => {
                assert!(c.error_kind.is_none(), "{}: wrote, but the C# refused", c.name);
                assert_eq!(Some(i32::from(status.value())), c.status_value, "{}: status", c.name);

                // Named statuses are named alike; an unfamiliar byte is just its number.
                let named = match status {
                    MaxxWriteStatus::Other(byte) => byte.to_string(),
                    other => format!("{other:?}"),
                };
                assert_eq!(Some(&named), c.status.as_ref(), "{}: status name", c.name);
            }
            Err(error) => {
                assert!(
                    kind_matches(c.error_kind.as_deref(), Some(error)),
                    "{}: refused as {error}, the C# threw {:?}",
                    c.name,
                    c.error_kind
                );
                assert!(c.status.is_none(), "{}: refused, but the C# wrote", c.name);

                // The ceiling's own refusal is worded the same: the one a person reads.
                if let Some(expected) = &c.message
                    && expected.starts_with("A MaxxECU will not be written above")
                {
                    assert_eq!(unix(expected), error.to_string(), "{}: message", c.name);
                }
            }
        }
    }
}

// ----- tables ---------------------------------------------------------------------

#[derive(Deserialize)]
struct Table {
    name: String,
    found: bool,
    table_name: Option<String>,
    columns: Option<usize>,
    rows: Option<usize>,
    x_channel: Option<i32>,
    y_channel: Option<i32>,
    x_axis_at: Option<usize>,
    y_axis_at: Option<usize>,
    cells_at: Option<usize>,
    fits: Option<bool>,
    record: Option<String>,
    record_crc: Option<String>,
    pieces: Option<Vec<String>>,
    wrong_size: Option<String>,
    covers: Option<i32>,
    sums: Option<Vec<String>>,
    error_kind: Option<String>,
}

/// The blob, title and config address each named table case was built from in the C#.
fn table_case(name: &str) -> Option<(Vec<u8>, &'static str, i32)> {
    let corrupt = |f: &dyn Fn(&mut Vec<u8>)| {
        let mut blob = model::with_ve_table(1578, 496);
        f(&mut blob);
        blob
    };

    Some(match name {
        "ve_16x11" => (model::with_ve_table(1578, 496), "VE Table 1 data", 1578),
        "small_6x6" => (model::with_small_table(400, 100), "Fuel ASE Table data", 400),
        "ve_other_place" => (model::with_ve_table(2000, 1500), "Boost Target data", 2000),
        "not_a_table" => (vec![0; maxx_tune::BLOB_SIZE], "Nothing data", 100),
        "no_suffix" => (model::with_small_table(400, 100), "Spark Map", 400),
        "upper_suffix" => (model::with_small_table(400, 100), "Spark MAP DATA", 400),
        "axis_does_not_climb" => (
            corrupt(&|b| {
                let at = maxx_tune::TABLE_DATA + 496 + 1 + (5 * 2);
                b[at..at + 2].copy_from_slice(&(-25795i16).to_le_bytes());
            }),
            "VE Table 1 data",
            1578,
        ),
        "no_columns" => (corrupt(&|b| b[1578 + 0x06] = 0), "VE Table 1 data", 1578),
        "too_many_rows" => (corrupt(&|b| b[1578 + 0x07] = 65), "VE Table 1 data", 1578),
        "two_layers" => (corrupt(&|b| b[1578 + 0x08] = 2), "VE Table 1 data", 1578),
        "past_the_blob" => (
            corrupt(&|b| b[1578 + 0x0B..1578 + 0x0D].copy_from_slice(&65000u16.to_le_bytes())),
            "VE Table 1 data",
            1578,
        ),
        "negative_config" => (model::with_ve_table(1578, 496), "VE Table 1 data", -1),
        "config_at_the_end" => (
            model::with_ve_table(1578, 496),
            "VE Table 1 data",
            (maxx_tune::BLOB_SIZE - 20) as i32,
        ),
        _ => return None,
    })
}

#[test]
fn tables_records_pieces_and_checksums_match_the_csharp() {
    let cases: Vec<Table> = olv_oracle::cases("w4-live", "maxx_tables");
    assert_eq!(cases.len(), 26);

    let pattern = model::pattern();

    for c in &cases {
        if let Some((blob, title, config)) = table_case(&c.name) {
            let table = maxx_tune::table_at(&blob, title, config);

            assert_eq!(table.is_some(), c.found, "{}: found", c.name);
            let Some(table) = table else { continue };

            assert_eq!(Some(&table.name), c.table_name.as_ref(), "{}: name", c.name);
            assert_eq!(Some(table.columns), c.columns, "{}: columns", c.name);
            assert_eq!(Some(table.rows), c.rows, "{}: rows", c.name);
            assert_eq!(Some(table.x_channel), c.x_channel, "{}: x channel", c.name);
            assert_eq!(Some(table.y_channel), c.y_channel, "{}: y channel", c.name);
            assert_eq!(Some(table.x_axis_at), c.x_axis_at, "{}: x axis", c.name);
            assert_eq!(Some(table.y_axis_at), c.y_axis_at, "{}: y axis", c.name);
            assert_eq!(Some(table.cells_at), c.cells_at, "{}: cells", c.name);
            assert_eq!(Some(maxx_tune::fits_in_one_write(&table)), c.fits, "{}: fits", c.name);

            let count = table.columns * table.rows;
            let cells: Vec<i16> = (0..count).map(|i| ((i * 37) as i32 - 400) as i16).collect();

            let record = maxx_tune::record(&blob, &table, &cells).unwrap();
            assert_eq!(Some(hex(&record)), c.record, "{}: record", c.name);

            let crc = u32::from_le_bytes(record[count * 2..count * 2 + 4].try_into().unwrap());
            assert_eq!(Some(hex8(crc)), c.record_crc, "{}: table crc", c.name);

            let pieces: Vec<String> = maxx_tune::writes_for(&blob, &table, &cells)
                .unwrap()
                .iter()
                .map(|(offset, data)| format!("{offset}:{}", hex(data)))
                .collect();
            assert_eq!(Some(pieces), c.pieces, "{}: pieces", c.name);

            assert!(
                matches!(maxx_tune::record(&blob, &table, &[0; 3]), Err(Error::Argument(_))),
                "{}: a grid of the wrong size",
                c.name
            );
            assert_eq!(c.wrong_size.as_deref(), Some("ArgumentException"));
        } else if let Some(rest) = c.name.strip_prefix("checksums_of_") {
            if rest == "zero" {
                assert!(maxx_tune::checksums_of(&pattern, 0).is_err());
                assert_eq!(c.error_kind.as_deref(), Some("ArgumentOutOfRangeException"));
            } else {
                let covers: i32 = rest.parse().unwrap();
                assert_eq!(Some(covers), c.covers);

                let sums: Vec<String> = maxx_tune::checksums_of(&pattern, covers)
                    .unwrap()
                    .iter()
                    .map(|s| hex8(*s))
                    .collect();
                assert_eq!(Some(sums), c.sums, "{}: checksums", c.name);
            }
        } else if let Some(pad) = c.name.strip_prefix("pad_byte_") {
            // The pad byte in front of the cells is covered by the table CRC: change it and the
            // checksum changes.
            let mut blob = model::with_small_table(400, 100);
            let table = maxx_tune::table_at(&blob, "Fuel ASE Table data", 400).unwrap();
            blob[table.cells_at - 1] = u8::from_str_radix(pad, 16).unwrap();

            let record = maxx_tune::record(&blob, &table, &[0; 36]).unwrap();
            let crc = u32::from_le_bytes(record[72..76].try_into().unwrap());
            assert_eq!(Some(hex8(crc)), c.record_crc, "{}", c.name);
        } else {
            panic!("the oracle has a table case this test does not know: {}", c.name);
        }
    }
}

// ----- the layout and pages -------------------------------------------------------------

#[derive(Deserialize)]
struct Build {
    name: String,
    kind: String,
    #[serde(default)]
    constants: Vec<ConstantJson>,
    #[serde(default)]
    pages: Vec<PageJson>,
    little_endian: Option<bool>,
    blocking_factor: Option<i32>,
    #[serde(default)]
    tables: Vec<TableJson>,
    #[serde(default)]
    maps: Vec<MapJson>,
    #[serde(default)]
    dialogs: Vec<DialogJson>,
    #[serde(default)]
    menus: Vec<MenuJson>,
    #[serde(default)]
    overlap: Vec<OverlapJson>,
}

#[derive(Deserialize)]
struct ConstantJson {
    name: String,
    page: i32,
    offset: i32,
    #[serde(rename = "type")]
    type_: String,
    scale: String,
    units: String,
    digits: i32,
    low: String,
    high: String,
    columns: i32,
    rows: i32,
}

#[derive(Deserialize)]
struct PageJson {
    index: i32,
    size: i32,
    identifier: String,
    read_command: String,
    chunk_write_command: String,
    burn_command: String,
}

#[derive(Deserialize)]
struct TableJson {
    id: String,
    title: String,
    map: String,
    values: String,
    x_bins: String,
    y_bins: String,
    x_channel: String,
    y_channel: String,
}

#[derive(Deserialize)]
struct MapJson {
    name: String,
    columns: usize,
    rows: usize,
    x_channel: i32,
    y_channel: i32,
    x_axis_at: usize,
    y_axis_at: usize,
    cells_at: usize,
}

#[derive(Deserialize)]
struct ItemJson {
    kind: String,
    label: String,
    target: String,
    condition: String,
    position: String,
}

#[derive(Deserialize)]
struct DialogJson {
    key: String,
    name: String,
    title: String,
    layout: String,
    items: Vec<ItemJson>,
}

#[derive(Deserialize)]
struct EntryJson {
    dialog: String,
    title: String,
    condition: String,
}

#[derive(Deserialize)]
struct MenuJson {
    title: String,
    entries: Vec<EntryJson>,
}

#[derive(Deserialize)]
struct OverlapJson {
    offset: i32,
    length: i32,
    hits: Vec<String>,
}

fn setting(
    name: &str,
    kind: &str,
    address: i32,
    length: i32,
    scale: f64,
    low: f64,
    high: f64,
) -> MaxxSettingDefinition {
    MaxxSettingDefinition::new(name, kind, address, length, scale, low, high)
}

fn channel_definitions() -> std::collections::HashMap<i32, MaxxChannelDefinition> {
    let mut channels = std::collections::HashMap::new();
    for (index, name, units, scale, digits) in [(61, "RPM", "rpm", 1.0, 0), (20, "MAP", "kPa", 0.1, 1)] {
        channels.insert(
            index,
            MaxxChannelDefinition {
                index,
                name: name.into(),
                units: units.into(),
                scale,
                is_signed: true,
                digits,
            },
        );
    }
    channels
}

fn the_settings() -> Vec<MaxxSettingDefinition> {
    let nan = f64::NAN;

    vec![
        setting("IATSensor Input Voltage", "list", 100, 18, 0.001, 0.0, 5.0),
        setting("IATSensor Predef", "uint16", 200, 1, 1.0, 0.0, 100.0),
        setting("Fuel Injmethod", "uint8", 300, 1, 1.0, 0.0, 3.0),
        setting("Fuel Trim", "int16", 310, 1, 0.1, -50.0, 50.0),
        setting("Fuel Trim", "int16", 312, 1, 0.1, -50.0, 50.0),
        setting("VE Table 1 data", "dynamicTable", 1578, 484, 0.1, 0.0, 2500.0),
        setting("VE Table 1 X", "int16", 9000, 1, 1.0, -30000.0, 30000.0),
        setting("Script One", "miniScript", 4000, 1, 1.0, nan, nan),
        setting("Name Text", "string", 4100, 16, 1.0, nan, nan),
        setting("Wide Word", "uint32", 5000, 2, 1.0, 0.0, 100_000.0),
        setting("Signed Byte", "int8", 5100, 1, 1.0, -100.0, 100.0),
        setting("Curve", "list", 5200, 4, 1.0, -40.0, 100.0),
        setting("Off The End", "uint16", 65535, 1, 1.0, 0.0, 1.0),
        setting("1stSensor Thing", "uint8", 6000, 1, 1.0, 0.0, 1.0),
        setting("zeta lower", "uint8", 6001, 1, 1.0, 0.0, 1.0),
        setting("Alpha one", "uint8", 6002, 1, 1.0, 0.0, 1.0),
        setting("alpha Two", "uint8", 6003, 1, 1.0, 0.0, 1.0),
    ]
}

#[test]
fn the_layout_tables_and_pages_a_tune_is_shaped_into_match_the_csharp() {
    let cases: Vec<Build> = olv_oracle::cases("w4-live", "maxx_build");
    assert_eq!(cases.len(), 7);

    let ve = model::with_ve_table(1578, 496);
    let ve_only = vec![setting(
        "VE Table 1 data",
        "dynamicTable",
        1578,
        22 * 22,
        0.1,
        0.0,
        2500.0,
    )];
    let channels = channel_definitions();

    for c in &cases {
        if c.kind == "overlap" {
            let settings = the_settings();

            for o in &c.overlap {
                assert_eq!(
                    maxx_tune::settings_overlapping(&settings, o.offset, o.length),
                    o.hits,
                    "overlap at {} for {}",
                    o.offset,
                    o.length
                );
            }
            continue;
        }

        let (blob, definitions, with_channels): (Vec<u8>, Vec<MaxxSettingDefinition>, bool) = match c.name.as_str() {
            "ve_only" => (ve.clone(), ve_only.clone(), false),
            "ve_with_channels" => (ve.clone(), ve_only.clone(), true),
            "settings_and_a_table" => (ve.clone(), the_settings(), true),
            "tables_sharing_a_name" => {
                let mut blob = vec![0u8; maxx_tune::BLOB_SIZE];
                let axis = [100i16, 200];
                model::compose(&mut blob, 400, 100, 61, 20, &axis, &axis, &|_, _| 0);
                model::compose(&mut blob, 500, 300, 61, 20, &axis, &axis, &|_, _| 0);
                (
                    blob,
                    vec![
                        setting("Boost Table data", "dynamicTable", 400, 4, 0.1, 0.0, 300.0),
                        setting("Boost Table data", "dynamicTable", 500, 4, 0.1, 0.0, 300.0),
                    ],
                    false,
                )
            }
            "a_long_subsystem" => (
                vec![0; maxx_tune::BLOB_SIZE],
                (0..130)
                    .map(|i| setting(&format!("GPO Thing {i}"), "uint8", 1000 + i, 1, 1.0, 0.0, 255.0))
                    .collect(),
                false,
            ),
            "a_long_list" => (
                vec![0; maxx_tune::BLOB_SIZE],
                vec![
                    setting("Long List", "list", 1000, 64, 0.01, 0.0, 5.0),
                    setting("Long List Two", "list", 1200, 64, 0.01, 0.0, 5.0),
                ],
                false,
            ),
            other => panic!("the oracle has a build case this test does not know: {other}"),
        };

        let model = maxx_tune::build(&blob, &definitions, with_channels.then_some(&channels));
        let name = &c.name;

        // The constants.
        assert_eq!(model.layout.constants.len(), c.constants.len(), "{name}: constants");
        for (got, want) in model.layout.constants.iter().zip(&c.constants) {
            let what = format!("{name}: constant {}", want.name);
            assert_eq!(got.name, want.name, "{what}");
            assert_eq!(got.page, want.page, "{what}: page");
            assert_eq!(got.offset, want.offset, "{what}: offset");
            assert_eq!(format!("{:?}", got.type_), want.type_, "{what}: type");
            assert!(same_f64(&want.scale, got.scale), "{what}: scale");
            assert_eq!(got.units, want.units, "{what}: units");
            assert_eq!(got.digits, want.digits, "{what}: digits");
            assert!(same_f64(&want.low, got.low), "{what}: low");
            assert!(same_f64(&want.high, got.high), "{what}: high");
            assert_eq!(got.columns, want.columns, "{what}: columns");
            assert_eq!(got.rows, want.rows, "{what}: rows");
        }

        // The page: no way to write and no burn.
        assert_eq!(model.layout.pages.len(), c.pages.len());
        for (got, want) in model.layout.pages.iter().zip(&c.pages) {
            assert_eq!(got.index, want.index);
            assert_eq!(got.size, want.size);
            assert_eq!(got.identifier, want.identifier);
            assert_eq!(got.read_command, want.read_command);
            assert_eq!(got.chunk_write_command, want.chunk_write_command);
            assert_eq!(got.burn_command, want.burn_command);
        }
        assert_eq!(Some(model.layout.little_endian), c.little_endian);
        assert_eq!(Some(model.layout.blocking_factor), c.blocking_factor);

        // The table editor's list, and the tables as a write finds them.
        assert_eq!(model.tables.len(), c.tables.len(), "{name}: tables");
        for (got, want) in model.tables.iter().zip(&c.tables) {
            assert_eq!(
                (
                    &got.id,
                    &got.title,
                    &got.map,
                    &got.values,
                    &got.x_bins,
                    &got.y_bins,
                    &got.x_channel,
                    &got.y_channel
                ),
                (
                    &want.id,
                    &want.title,
                    &want.map,
                    &want.values,
                    &want.x_bins,
                    &want.y_bins,
                    &want.x_channel,
                    &want.y_channel
                ),
                "{name}: table {}",
                want.id
            );
        }

        assert_eq!(model.maps.len(), c.maps.len(), "{name}: maps");
        for (got, want) in model.maps.iter().zip(&c.maps) {
            let expected = MaxxTable {
                name: want.name.clone(),
                columns: want.columns,
                rows: want.rows,
                x_channel: want.x_channel,
                y_channel: want.y_channel,
                x_axis_at: want.x_axis_at,
                y_axis_at: want.y_axis_at,
                cells_at: want.cells_at,
            };
            assert_eq!(*got, expected, "{name}: map {}", want.name);
        }

        // The invented settings pages, in the order they were made.
        let dialogs: Vec<_> = model.pages.dialogs.iter().collect();
        assert_eq!(dialogs.len(), c.dialogs.len(), "{name}: dialogs");
        for ((key, got), want) in dialogs.iter().zip(&c.dialogs) {
            assert_eq!(*key, want.key, "{name}: dialog key");
            assert_eq!(
                (&got.name, &got.title, &got.layout),
                (&want.name, &want.title, &want.layout)
            );
            assert_eq!(got.items.len(), want.items.len(), "{name}: {} items", want.title);

            for (item, wanted) in got.items.iter().zip(&want.items) {
                assert_eq!(format!("{:?}", item.kind), wanted.kind);
                assert_eq!(
                    (&item.label, &item.target, &item.condition, &item.position),
                    (&wanted.label, &wanted.target, &wanted.condition, &wanted.position)
                );
            }
        }

        assert_eq!(model.pages.menus.len(), c.menus.len(), "{name}: menus");
        for (got, want) in model.pages.menus.iter().zip(&c.menus) {
            assert_eq!(got.title, want.title, "{name}: menu");
            assert_eq!(got.entries.len(), want.entries.len(), "{name}: {} entries", want.title);

            for (entry, wanted) in got.entries.iter().zip(&want.entries) {
                assert_eq!(
                    (&entry.dialog, &entry.title, &entry.condition),
                    (&wanted.dialog, &wanted.title, &wanted.condition)
                );
            }
        }
    }
}

// ----- the definitions file -----------------------------------------------------------

#[derive(Deserialize)]
struct DefinitionsCase {
    name: String,
    xml: String,
    settings: Vec<SettingJson>,
}

#[derive(Deserialize)]
struct SettingJson {
    name: String,
    kind: String,
    address: i32,
    length: i32,
    scale: String,
    low: String,
    high: String,
    is_table: bool,
    is_signed: bool,
    element_size: i32,
    #[serde(rename = "type")]
    type_: String,
    size: i32,
}

#[test]
fn the_definitions_file_reads_in_order_with_its_implied_addresses_like_the_csharp() {
    let cases: Vec<DefinitionsCase> = olv_oracle::cases("w4-live", "maxx_definitions");
    assert_eq!(cases.len(), 2);

    for c in &cases {
        let mut temp = TempFiles::new();
        let path = if c.xml.is_empty() {
            std::env::temp_dir().join("olv-no-such-definitions.xml")
        } else {
            let path = std::path::PathBuf::from(temp.path("olv-oracle-defs"));
            std::fs::write(&path, &c.xml).unwrap();
            path
        };

        let read = maxx_tune_definitions::read(Some(&path));

        assert_eq!(read.len(), c.settings.len(), "{}: settings", c.name);

        for (got, want) in read.iter().zip(&c.settings) {
            let what = format!("{}: {}", c.name, want.name);
            assert_eq!(got.name, want.name, "{what}");
            assert_eq!(got.kind, want.kind, "{what}: kind");
            assert_eq!(got.address, want.address, "{what}: address");
            assert_eq!(got.length, want.length, "{what}: length");
            assert!(same_f64(&want.scale, got.scale), "{what}: scale");
            assert!(same_f64(&want.low, got.low), "{what}: low");
            assert!(same_f64(&want.high, got.high), "{what}: high");
            assert_eq!(got.is_table(), want.is_table, "{what}: table");
            assert_eq!(got.is_signed(), want.is_signed, "{what}: signed");
            assert_eq!(got.element_size(), want.element_size, "{what}: element size");
            assert_eq!(format!("{:?}", got.type_()), want.type_, "{what}: type");
            assert_eq!(got.size(), want.size, "{what}: size");
        }
    }
}

// ----- CAN -------------------------------------------------------------------------------

#[derive(Deserialize)]
struct CanCase {
    name: String,
    kind: String,
    reply: String,
    #[serde(default)]
    frames: Vec<FrameJson>,
    #[serde(default)]
    since: Vec<SinceJson>,
    #[serde(default)]
    armed: Vec<ArmedJson>,
}

#[derive(Deserialize)]
struct FrameJson {
    id: i32,
    extended: bool,
    data: String,
    at: i64,
}

#[derive(Deserialize)]
struct SinceJson {
    last: i32,
    now: i32,
    distance: i32,
}

#[derive(Deserialize)]
struct ArmedJson {
    value: u8,
    armed: bool,
    without_definitions: bool,
}

fn unhex(text: &str) -> Vec<u8> {
    (0..text.len() / 2)
        .map(|i| u8::from_str_radix(&text[i * 2..i * 2 + 2], 16).unwrap())
        .collect()
}

#[test]
fn the_can_ring_decodes_and_the_counter_wraps_like_the_csharp() {
    let cases: Vec<CanCase> = olv_oracle::cases("w4-live", "maxx_can");
    assert_eq!(cases.len(), 8);

    for c in &cases {
        match c.kind.as_str() {
            "frames" => {
                let frames = maxx_can::frames(&unhex(&c.reply));

                assert_eq!(frames.len(), c.frames.len(), "{}: frame count", c.name);
                for (got, want) in frames.iter().zip(&c.frames) {
                    assert_eq!(got.id, want.id, "{}", c.name);
                    assert_eq!(got.is_extended, want.extended, "{}", c.name);
                    assert_eq!(hex(&got.data), want.data, "{}", c.name);
                    assert_eq!(got.at, want.at, "{}", c.name);
                }
            }
            "since" => {
                for s in &c.since {
                    assert_eq!(
                        maxx_can::since(s.last, s.now),
                        s.distance,
                        "since({}, {})",
                        s.last,
                        s.now
                    );
                }
            }
            "constants" => {
                let ours = format!(
                    "{},{},{},{},{},{},{},{},{},{},{}",
                    maxx_can::MOST_PER_READ,
                    maxx_can::RECORD_LENGTH,
                    maxx_can::DROP_COUNT_AT,
                    maxx_can::SNAPSHOT_LIMIT,
                    maxx_can::SNAPSHOT_WINDOW,
                    maxx_tune::WRITE_CEILING,
                    maxx_tune::MAXIMUM_WRITE,
                    maxx_tune::PIECE,
                    maxx_tune::TABLE_DATA,
                    maxx_tune::PREAMBLE,
                    maxx_tune::TABLE_CONFIG_SIZE
                );
                assert_eq!(ours, c.reply);
            }
            "armed" => {
                let enable = [setting("CAN Analyzer Enable", "uint8", 52658, 1, 1.0, 0.0, 1.0)];
                let source = MaxxCanSource::new(MaxxUsbSource::new(Box::new(ModelEcu::new()), None).link(), false);

                for a in &c.armed[..c.armed.len() - 1] {
                    let mut tune = vec![0u8; maxx_tune::BLOB_SIZE];
                    tune[52658] = a.value;
                    assert_eq!(source.is_armed(&tune, &enable), a.armed, "armed with {}", a.value);
                    assert_eq!(source.is_armed(&tune, &[]), a.without_definitions);
                }

                // The last case: a tune too short to hold the flag.
                let short = c.armed.last().unwrap();
                assert!(!short.armed);
                assert_eq!(source.is_armed(&[0; 100], &enable), short.armed);
            }
            other => panic!("the oracle has a CAN case this test does not know: {other}"),
        }
    }
}

// ----- the bytes of a recording ---------------------------------------------------------------

#[derive(Deserialize)]
struct RecordingCase {
    name: String,
    names: Vec<String>,
    units: Vec<String>,
    head_hex: String,
    rows: Vec<String>,
    row_count: usize,
}

/// A source that answers instantly with a row computed from how many it has given (the oracle's
/// `Scripted`).
struct Scripted {
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
    n: i32,
    row: fn(i32) -> Vec<f64>,
}

impl LiveSource for Scripted {
    fn names(&self) -> &[String] {
        &self.names
    }
    fn units(&self) -> &[String] {
        &self.units
    }
    fn digits(&self) -> &[i32] {
        &self.digits
    }
    fn open(&mut self) -> olv_core::Result<()> {
        Ok(())
    }
    fn read(&mut self) -> olv_core::Result<Vec<f64>> {
        self.n += 1;
        Ok((self.row)(self.n))
    }
    fn recover(&mut self) -> olv_core::Result<()> {
        Ok(())
    }
    fn retries(&self) -> i32 {
        0
    }
}

fn script_row(name: &str) -> fn(i32) -> Vec<f64> {
    match name {
        "engine" => |n| {
            let n = f64::from(n);
            vec![
                1000.0 + (n * 37.5),
                85.1 + (n * 0.1),
                if n as i32 % 5 == 0 {
                    f64::NAN
                } else {
                    0.95 + (n * 0.0001)
                },
                -12.25 - n,
            ]
        },
        "integers_and_extremes" => |n| {
            vec![
                f64::from(n),
                f64::from(-n),
                if n % 3 == 0 {
                    f64::INFINITY
                } else {
                    f64::MAX / f64::from(n)
                },
            ]
        },
        "one_channel" => |_| vec![0.0],
        "small_and_large" => |n| {
            vec![
                1e-7 * f64::from(n),
                12_345_678_901.5 * f64::from(n),
                1.0 / f64::from(n + 2),
            ]
        },
        other => panic!("the oracle has a recording this test does not know: {other}"),
    }
}

#[test]
fn a_recording_is_made_of_the_same_bytes_as_the_csharps() {
    let cases: Vec<RecordingCase> = olv_oracle::cases("w4-live", "recording");
    assert_eq!(cases.len(), 4);

    for c in &cases {
        let mut temp = TempFiles::new();
        let path = temp.path("olv-oracle-recording");

        {
            let session = LiveSession::new(
                Box::new(Scripted {
                    digits: c.names.iter().map(|_| 1).collect(),
                    names: c.names.clone(),
                    units: c.units.clone(),
                    n: 0,
                    row: script_row(&c.name),
                }),
                LiveSessionSettings {
                    recording_path: Some(path.clone()),
                    maximum_rate: 0.0,
                    ..LiveSessionSettings::default()
                },
            );

            session.start().unwrap();
            wait_for(10.0, 2, || session.recorded_rows() >= (c.row_count + 5) as i32);
            session.stop();
        }

        let bytes = std::fs::read(&path).unwrap();

        // The head: everything up to and including the second line break.
        let mut breaks = 0;
        let mut head = 0;
        while head < bytes.len() && breaks < 2 {
            if bytes[head] == b'\n' {
                breaks += 1;
            }
            head += 1;
        }

        assert_eq!(
            hex(&bytes[..head]),
            c.head_hex,
            "{}: byte-order mark, header and units",
            c.name
        );

        let text = String::from_utf8(bytes[head..].to_vec()).unwrap();
        let lines: Vec<&str> = text.split("\r\n").filter(|l| !l.is_empty()).collect();
        assert!(
            lines.len() >= c.row_count,
            "{}: only {} rows written",
            c.name,
            lines.len()
        );
        assert!(
            !text.replace("\r\n", "").contains('\n'),
            "{}: every line ends CRLF",
            c.name
        );

        let mut last_time = -1.0;
        for (i, (line, want)) in lines.iter().zip(&c.rows).enumerate() {
            let comma = line.find(',').unwrap();

            // The time column is the clock's; everything after it is the oracle's, byte for byte.
            assert_eq!(format!("T{}", &line[comma..]), *want, "{}: row {i}", c.name);

            let time: f64 = line[..comma].parse().unwrap();
            assert!(
                time >= 0.0 && time >= last_time,
                "{}: time {time} after {last_time}",
                c.name
            );
            last_time = time;
        }
    }
}

// ----- the MTune capture, replayed -------------------------------------------------------------

#[derive(Deserialize)]
struct Replay {
    name: String,
    pairs: usize,
    ids: Vec<i32>,
    names: Vec<String>,
    scales: Vec<String>,
    signed: Vec<bool>,
    digits: Vec<i32>,
    first_requests: Vec<String>,
    always_logged: Vec<i32>,
    tune_checksums: Vec<String>,
    verified: Vec<String>,
}

#[test]
fn the_mtune_capture_replays_the_same_through_the_source_and_the_tune_commands() {
    let cases: Vec<Replay> = olv_oracle::cases("w4-live", "maxx_replay");
    assert_eq!(cases.len(), 2);

    for c in &cases {
        match c.name.as_str() {
            "source_channels" => {
                let nowhere = Some(std::env::temp_dir().join("olv-no-such-definitions.xml"));
                let ecu = RecordedEcu::new();
                let mut source = MaxxUsbSource::new(Box::new(ecu), nowhere);
                source.open().unwrap();

                assert_eq!(live_doubles::recorded_maxx_usb().len(), c.pairs);
                assert_eq!(source.channels().iter().map(|ch| ch.id).collect::<Vec<_>>(), c.ids);
                assert_eq!(source.names(), c.names.as_slice());
                assert_eq!(source.digits(), c.digits.as_slice());
                assert_eq!(
                    source.channels().iter().map(|ch| ch.is_signed).collect::<Vec<_>>(),
                    c.signed
                );
                for (channel, bits) in source.channels().iter().zip(&c.scales) {
                    assert!(same_f64(bits, channel.scale), "scale of {}", channel.id);
                }
                assert_eq!(maxx_usb_source::always_logged(), c.always_logged.as_slice());
            }
            "tune_commands" => {
                let mut ecu = RecordedEcu::new();
                let sums = maxx_tune::checksums(&mut ecu, 62180).unwrap();

                assert_eq!(
                    ecu.requests.iter().map(|r| hex(r)).collect::<Vec<_>>(),
                    c.first_requests
                );
                assert_eq!(sums.iter().map(|s| hex8(*s)).collect::<Vec<_>>(), c.tune_checksums);

                let pairs = live_doubles::recorded_maxx_usb();
                let mut verified = Vec::new();
                for offset in [0xF000, 0xF100] {
                    let request = usb::request(usb::READ, maxx_tune::READ_TUNE, offset, 256).unwrap();
                    let (_, reply) = pairs.iter().find(|(r, _)| *r == request).unwrap();
                    let recorded = usb::try_read_reply(reply, 256).unwrap();

                    let mut wrong = recorded.clone();
                    wrong[0] ^= 1;

                    verified.push(format!(
                        "{offset:04X}:{}:{}:{}",
                        recorded.len(),
                        bool_text(maxx_tune::verify(&mut RecordedEcu::new(), offset, &recorded).unwrap()),
                        bool_text(maxx_tune::verify(&mut RecordedEcu::new(), offset, &wrong).unwrap()),
                    ));
                }
                assert_eq!(verified, c.verified);
            }
            other => panic!("the oracle has a replay this test does not know: {other}"),
        }
    }
}
