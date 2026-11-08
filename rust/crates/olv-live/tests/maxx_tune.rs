//! Reading a MaxxECU's tune out of the blob it hands over, and writing it back: the C#
//! `MaxxTuneTests`.
//!
//! Built against a blob composed here rather than a recorded one, because what is being checked is
//! the packing and every part of it has to be varied: a table whose axes are different lengths
//! catches a transposition that a square one hides, and the nine bytes between the axes and the
//! cells only show up as wrong if the cells are not all the same.
//!
//! The layout it is composed to is the one a real MaxxECU Race handed over on 2026-09-13, where 118
//! of its 120 live tables decoded inside their own declared limits.
//!
//! `EcuTune.FromPages(layout, blob).Tables(tables)` (the editor's reader, in a package that is not
//! ported yet) is stood in for by [`decode`], which reads the same constants out of the same bytes
//! the same way: little-endian signed 16-bit, scaled, cells column-fastest.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::Error;
use olv_core::Result;
use olv_core::ini::table_editor_reader::TableDefinition;
use olv_core::ini::tune_layout::{TuneConstant, TuneLayout};
use olv_core::logs::maxx_channel_definitions::MaxxChannelDefinition;
use olv_live::live::maxx_tune::{self, MaxxTable, MaxxWriteStatus};
use olv_live::live::maxx_tune_definitions::{self, MaxxSettingDefinition};
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use olv_protocol::ms::ms_protocol::EcuTransport;

// ----- composing a blob ------------------------------------------------------

/// Lays a table into a blob the way a MaxxECU does, and hands back the address its config struct
/// went to.
#[allow(clippy::too_many_arguments)]
fn compose(
    blob: &mut [u8],
    config: usize,
    data_offset: usize,
    x_channel: u16,
    y_channel: u16,
    x: &[i16],
    y: &[i16],
    cells: &[Vec<i16>],
) -> usize {
    blob[config..config + 2].copy_from_slice(&x_channel.to_le_bytes());
    blob[config + 0x02..config + 0x04].copy_from_slice(&y_channel.to_le_bytes());
    blob[config + 0x06] = x.len() as u8;
    blob[config + 0x07] = y.len() as u8;
    blob[config + 0x08] = 1;
    blob[config + 0x0B..config + 0x0D].copy_from_slice(&(data_offset as u16).to_le_bytes());

    let at = maxx_tune::TABLE_DATA + data_offset;

    blob[at] = 0;
    let mut cursor = at + 1;

    for value in x.iter().chain(y) {
        blob[cursor..cursor + 2].copy_from_slice(&value.to_le_bytes());
        cursor += 2;
    }

    // The nine bytes that are not the grid: two zeroes, a value, a checksum, and a byte the second
    // checksum covers.
    //
    // Written as nine rather than as `PREAMBLE` on purpose. Composing the fixture with the same
    // constant the decoder reads it with means both sides move together, and the test goes on
    // passing with the number set to anything at all, which is exactly the mistake it exists to
    // catch. Nine is what a MaxxECU Race puts there.
    cursor += 9;

    for row in cells.iter().take(y.len()) {
        for value in row.iter().take(x.len()) {
            blob[cursor..cursor + 2].copy_from_slice(&value.to_le_bytes());
            cursor += 2;
        }
    }

    config
}

fn blank() -> Vec<u8> {
    vec![0u8; maxx_tune::BLOB_SIZE]
}

const RPM: [i16; 16] = [
    200, 800, 1000, 1200, 1600, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000,
];
const KPA: [i16; 11] = [200, 400, 503, 600, 800, 999, 1199, 1399, 1599, 1799, 1999];

/// A VE table the shape of the one on the bench Race: 16 wide, 11 deep.
fn with_ve_table(config: usize, data_offset: usize) -> Vec<u8> {
    let mut blob = blank();

    let cells: Vec<Vec<i16>> = (0..KPA.len())
        .map(|row| {
            (0..RPM.len())
                .map(|column| (280 + (row * 80) + (column * 2)) as i16)
                .collect()
        })
        .collect();

    compose(&mut blob, config, data_offset, 61, 20, &RPM, &KPA, &cells);

    blob
}

fn ve() -> Vec<u8> {
    with_ve_table(1578, 496)
}

fn ve_definition() -> Vec<MaxxSettingDefinition> {
    vec![MaxxSettingDefinition::new(
        "VE Table 1 data",
        "dynamicTable",
        1578,
        22 * 22,
        0.1,
        0.0,
        2500.0,
    )]
}

// ----- reading a table back through the layout (EcuTune's job) -------------------

struct Decoded {
    columns: usize,
    rows: usize,
    /// `values[column][row]`, as the C#'s `Values[column, row]`.
    values: Vec<Vec<f64>>,
    x: Vec<f64>,
    y: Vec<f64>,
    x_units: String,
    y_units: String,
}

fn constant<'a>(layout: &'a TuneLayout, name: &str) -> &'a TuneConstant {
    layout
        .constants
        .iter()
        .find(|c| c.name == name)
        .unwrap_or_else(|| panic!("no constant {name}"))
}

fn read_constant(blob: &[u8], c: &TuneConstant, index: usize) -> f64 {
    let at = c.offset as usize + index * 2;
    let raw = i16::from_le_bytes([blob[at], blob[at + 1]]);

    (f64::from(raw) + c.transform) * c.scale
}

fn decode(layout: &TuneLayout, tables: &[TableDefinition], blob: &[u8]) -> Decoded {
    assert_eq!(tables.len(), 1, "expected a single table");
    let table = &tables[0];

    let cells = constant(layout, &table.values);
    let x = constant(layout, &table.x_bins);
    let y = constant(layout, &table.y_bins);

    let columns = cells.columns as usize;
    let rows = cells.rows as usize;

    Decoded {
        columns,
        rows,
        values: (0..columns)
            .map(|column| {
                (0..rows)
                    .map(|row| read_constant(blob, cells, row * columns + column))
                    .collect()
            })
            .collect(),
        x: (0..x.columns as usize).map(|i| read_constant(blob, x, i)).collect(),
        y: (0..y.columns as usize).map(|i| read_constant(blob, y, i)).collect(),
        x_units: x.units.clone(),
        y_units: y.units.clone(),
    }
}

fn close(a: f64, b: f64) {
    assert!((a - b).abs() < 5e-4, "{a} is not {b}");
}

// ----- tables ----------------------------------------------------------------------

#[test]
fn a_table_is_found_through_its_config_struct() {
    let table = maxx_tune::table_at(&ve(), "VE Table 1 data", 1578).expect("a table");

    assert_eq!(table.name, "VE Table 1");
    assert_eq!(table.columns, 16);
    assert_eq!(table.rows, 11);
    assert_eq!(table.x_channel, 61);
    assert_eq!(table.y_channel, 20);
}

/// The nine bytes between the axes and the cells are counted.
///
/// This is the whole of what makes the decode right or wrong, and getting it wrong does not look
/// wrong: the cells land four and a half cells early, so a VE table still reads as numbers between
/// 28 and 116 and still rises to the right, just not the numbers that are in the ECU. The check is
/// that the first cell is the first cell.
#[test]
fn the_cells_begin_after_the_preamble_and_not_at_the_axes() {
    let blob = ve();

    let model = maxx_tune::build(&blob, &ve_definition(), None);
    let table = decode(&model.layout, &model.tables, &blob);

    // Composed as 280 + row*80 + column*2, at a scale of a tenth.
    close(table.values[0][0], 28.0);
    close(table.values[5][0], 29.0);
    close(table.values[0][1], 36.0);
    close(table.values[15][10], 111.0);
}

/// Rows and columns are not swapped.
///
/// A square table cannot catch this and most of a MaxxECU's are not square, so the 16 x 11 shape is
/// the test. Swapped, the table is 11 x 16 and every value after the first row is somebody else's.
#[test]
fn the_grid_is_not_transposed() {
    let blob = ve();

    let model = maxx_tune::build(&blob, &ve_definition(), None);
    let table = decode(&model.layout, &model.tables, &blob);

    assert_eq!(table.columns, 16);
    assert_eq!(table.rows, 11);
    assert_eq!(table.x.len(), 16);
    assert_eq!(table.y.len(), 11);
}

/// An axis is counted in what its own channel is counted in, not in the table's units.
///
/// A VE table is scaled by a tenth because its cells are percent. Its columns are RPM and stored
/// whole, so scaling them the same way turns 5,000 RPM into 500, which looks like an idle speed
/// rather than an obvious fault.
#[test]
fn an_axis_is_scaled_by_its_own_channel_and_not_by_the_table() {
    let blob = ve();

    let mut channels = HashMap::new();
    channels.insert(
        61,
        MaxxChannelDefinition {
            index: 61,
            name: "RPM".into(),
            units: "rpm".into(),
            scale: 1.0,
            is_signed: true,
            digits: 0,
        },
    );
    channels.insert(
        20,
        MaxxChannelDefinition {
            index: 20,
            name: "MAP".into(),
            units: "kPa".into(),
            scale: 0.1,
            is_signed: true,
            digits: 1,
        },
    );

    let model = maxx_tune::build(&blob, &ve_definition(), Some(&channels));
    let table = decode(&model.layout, &model.tables, &blob);

    close(table.x[0], 200.0);
    close(table.x[15], 7000.0);
    assert_eq!(table.x_units, "rpm");

    close(table.y[0], 20.0);
    close(table.y[10], 199.9);
    assert_eq!(table.y_units, "kPa");

    // And the table knows what indexes it, by name.
    assert_eq!(model.tables[0].x_channel, "RPM");
    assert_eq!(model.tables[0].y_channel, "MAP");
}

/// A table the ECU is not using is left out rather than decoded into nonsense. The counts are what
/// say so: zero, or past the 64 a MaxxECU allows.
#[test]
fn a_table_with_impossible_dimensions_is_not_offered() {
    for (columns, rows) in [(0u8, 11u8), (16, 0), (200, 11), (16, 200)] {
        let mut blob = ve();
        blob[1578 + 0x06] = columns;
        blob[1578 + 0x07] = rows;

        assert!(
            maxx_tune::table_at(&blob, "VE Table 1 data", 1578).is_none(),
            "{columns} by {rows}"
        );

        assert!(maxx_tune::build(&blob, &ve_definition(), None).tables.is_empty());
    }
}

/// A table whose axis does not climb is not a table.
///
/// Two thirds of the addresses in the definitions are not stated but follow on from what came
/// before, so one drift reads a config struct off bytes that are not one, and what comes back is a
/// plausible size and an offset inside the blob, not an obvious failure. On the bench ECU that
/// produced six tables that drew perfectly well, one of them fifteen by thirty-nine with columns
/// running 1,000 to 8,000 and then 0, 27, -25,795.
#[test]
fn a_table_whose_axis_does_not_climb_is_not_offered() {
    let mut blob = ve();

    // One breakpoint out of order, in the middle of an otherwise good axis.
    let at = maxx_tune::TABLE_DATA + 496 + 1 + (5 * 2);
    blob[at..at + 2].copy_from_slice(&(-25795i16).to_le_bytes());

    assert!(maxx_tune::table_at(&blob, "VE Table 1 data", 1578).is_none());

    assert!(maxx_tune::build(&blob, &ve_definition(), None).tables.is_empty());
}

/// A repeated breakpoint counts as not climbing, which is what leaves the unused tables out: a
/// MaxxECU parks one it is not using at two by two with both axes at zero.
#[test]
fn an_unused_table_parked_at_zero_is_not_offered() {
    let mut blob = blank();
    compose(&mut blob, 400, 100, 61, 20, &[0, 0], &[0, 0], &[vec![0, 0], vec![0, 0]]);

    assert!(maxx_tune::table_at(&blob, "ClosedBoost Target2 Table data", 400).is_none());
}

/// A table whose record would run past the end of the blob is refused. The data offset is sixteen
/// bits and the cells are counted from it, so a corrupt or unexpected offset otherwise reads
/// whatever is next.
#[test]
fn a_table_whose_data_runs_past_the_blob_is_not_offered() {
    let mut blob = ve();
    blob[1578 + 0x0B..1578 + 0x0D].copy_from_slice(&65000u16.to_le_bytes());

    assert!(maxx_tune::table_at(&blob, "VE Table 1 data", 1578).is_none());
}

/// Two tables that MTune gives the same name are kept apart.
///
/// A constant is found by name, so a repeat would shadow the first, and the shadowing is silent: the
/// second table's title would sit over the first one's numbers.
#[test]
fn tables_sharing_a_name_are_kept_apart() {
    let mut blob = blank();
    let axis = [100i16, 200];
    let cells = vec![vec![0i16; 2]; 2];

    compose(&mut blob, 400, 100, 61, 20, &axis, &axis, &cells);
    compose(&mut blob, 500, 300, 61, 20, &axis, &axis, &cells);

    let definitions = vec![
        MaxxSettingDefinition::new("Boost Table data", "dynamicTable", 400, 4, 0.1, 0.0, 300.0),
        MaxxSettingDefinition::new("Boost Table data", "dynamicTable", 500, 4, 0.1, 0.0, 300.0),
    ];

    let model = maxx_tune::build(&blob, &definitions, None);

    assert_eq!(model.tables.len(), 2);
    let mut distinct: Vec<&str> = model.tables.iter().map(|t| t.values.as_str()).collect();
    distinct.sort_unstable();
    distinct.dedup();
    assert_eq!(distinct.len(), 2);
    assert_eq!(model.layout.constants.len(), 6);
}

/// A setting whose name collides with a generated axis name does not take the axis over.
///
/// The axis constants are invented here ("VE Table 1 X") and the ECU's own settings are not, so
/// nothing stops the definitions containing that name already. A constant is found by name and the
/// later one wins, so the table would read its breakpoints out of that setting, at that setting's
/// scale, with nothing on screen to say so.
#[test]
fn a_setting_cannot_take_over_a_tables_axis() {
    let mut blob = ve();

    // Something plausible at an address nowhere near the axis.
    blob[9000..9002].copy_from_slice(&1234i16.to_le_bytes());

    let mut definitions = ve_definition();
    definitions.push(MaxxSettingDefinition::new(
        "VE Table 1 X",
        "int16",
        9000,
        1,
        1.0,
        -30000.0,
        30000.0,
    ));

    let model = maxx_tune::build(&blob, &definitions, None);
    let table = decode(&model.layout, &model.tables, &blob);

    // The breakpoints are the table's own, not the setting's single value.
    assert_eq!(table.x.len(), 16);
    close(table.x[0], 200.0);
    close(table.x[15], 7000.0);
}

/// The page a MaxxECU's tune sits on declares no way to write it.
///
/// This is what keeps the ordinary write path off a controller it must not touch. Send and Burn are
/// driven by the templates the firmware gives for them (`C%2o%2c%v` and `B` on a MegaSquirt) and a
/// MaxxECU is not written that way at all. Both are empty, so there is nothing for that path to
/// send even if it were reached, and the burn is empty for the further reason that this ECU has
/// none.
#[test]
fn the_tune_page_offers_no_write_and_no_burn() {
    let layout = maxx_tune::build(&ve(), &ve_definition(), None).layout;

    assert_eq!(layout.pages.len(), 1);
    let page = &layout.pages[0];

    assert!(page.chunk_write_command.is_empty());
    assert!(page.burn_command.is_empty());
    assert!(page.read_command.is_empty());
}

// ----- the invented settings interface ---------------------------------------

fn some_settings() -> Vec<MaxxSettingDefinition> {
    vec![
        MaxxSettingDefinition::new("IATSensor Input Voltage", "list", 100, 18, 0.001, 0.0, 5.0),
        MaxxSettingDefinition::new("IATSensor Predef", "uint16", 200, 1, 1.0, 0.0, 100.0),
        MaxxSettingDefinition::new("Fuel Injmethod", "uint8", 300, 1, 1.0, 0.0, 3.0),
        MaxxSettingDefinition::new("VE Table 1 data", "dynamicTable", 1578, 484, 0.1, 0.0, 2500.0),
    ]
}

/// Settings are grouped into pages by the subsystem their name begins with, because MTune's
/// definitions describe no pages at all and 8,748 settings with nowhere to be shown is the same as
/// none.
#[test]
fn settings_are_grouped_into_pages_by_subsystem() {
    let ui = maxx_tune::build(&ve(), &some_settings(), None).pages;

    assert!(!ui.is_empty());
    assert_eq!(ui.dialogs.len(), 2);
    assert!(ui.dialogs.values().any(|d| d.title == "IATSensor"));
    assert!(ui.dialogs.values().any(|d| d.title == "Fuel"));
}

/// A list becomes a row per point.
///
/// A field with no subscript addresses the first element, so an eighteen point sensor calibration
/// would show as a single box reading 4.660, a fifth of one of its points, with nothing on screen
/// saying so.
#[test]
fn every_point_of_a_list_is_shown() {
    let ui = maxx_tune::build(&ve(), &some_settings(), None).pages;
    let sensor = ui.dialogs.values().find(|d| d.title == "IATSensor").unwrap();

    assert_eq!(sensor.items.len(), 19);
    assert_eq!(sensor.items[0].target, "IATSensor Input Voltage[0]");
    assert_eq!(sensor.items[17].target, "IATSensor Input Voltage[17]");
    assert_eq!(sensor.items[0].target_index(), 0);
    assert_eq!(sensor.items[17].target_index(), 17);
    assert_eq!(sensor.items[17].target_constant(), "IATSensor Input Voltage");
}

/// Tables are not among them. They have the other half of that view, and a table offered as a field
/// would show one cell of itself.
#[test]
fn tables_are_not_offered_as_settings() {
    let ui = maxx_tune::build(&ve(), &some_settings(), None).pages;

    assert!(
        !ui.dialogs
            .values()
            .flat_map(|d| &d.items)
            .any(|item| item.target_constant().contains("VE Table"))
    );
}

/// No page is longer than it should be. The largest subsystem declares 1,201 settings, which as one
/// page is a scroll nobody finds anything in.
#[test]
fn a_long_subsystem_is_split_across_pages() {
    let many: Vec<MaxxSettingDefinition> = (0..130)
        .map(|i| MaxxSettingDefinition::new(format!("GPO Thing {i}"), "uint8", 1000 + i, 1, 1.0, 0.0, 255.0))
        .collect();

    let ui = maxx_tune::build(&blank(), &many, None).pages;

    assert!(
        ui.dialogs.len() >= 3,
        "expected several pages, got {}",
        ui.dialogs.len()
    );
    assert!(ui.dialogs.values().all(|d| (1..=60).contains(&d.items.len())));
    assert_eq!(ui.dialogs.values().map(|d| d.items.len()).sum::<usize>(), 130);
}

// ----- the definitions file --------------------------------------------------

/// An address that is not stated follows on from the one before.
///
/// Two thirds of the file states no address at all, so a reader that takes only the ones that do
/// puts everything else at nought.
#[test]
fn an_address_that_is_not_stated_follows_the_one_before() {
    let xml = r#"<ECUSettingsDefinitions version="1">
  <ECUSetting name="First"  type="uint16" length="1" address="100" />
  <ECUSetting name="Second" type="uint16" length="1" address="seq" />
  <ECUSetting name="Third"  type="uint8"  length="4" />
  <ECUSetting name="Fourth" type="uint16" length="1" />
  <ECUSetting name="Fifth"  type="uint16" length="1" address="900" />
</ECUSettingsDefinitions>"#;

    let path = std::env::temp_dir().join(format!("olv-maxx-{}.xml", std::process::id()));
    std::fs::write(&path, xml).unwrap();

    let read = maxx_tune_definitions::read(Some(&path));
    let _ = std::fs::remove_file(&path);

    assert_eq!(
        read.iter().map(|d| d.address).collect::<Vec<_>>(),
        [100, 102, 104, 108, 900]
    );
}

/// A list is signed when the firmware says its smallest value is below zero, because the file gives
/// lists no element type at all. Read unsigned, a temperature curve starting at -40 degrees C begins
/// at 6,513 degrees C.
#[test]
fn signedness_comes_from_the_type_and_then_from_the_range() {
    for (kind, low, signed) in [
        ("list", -40.0, true),
        ("list", 0.0, false),
        ("uint16", -40.0, false),
        ("int16", 0.0, true),
    ] {
        let definition = MaxxSettingDefinition::new("x", kind, 0, 1, 0.1, low, 100.0);

        assert_eq!(definition.is_signed(), signed, "{kind} from {low}");
    }
}

#[test]
fn missing_definitions_are_not_an_error() {
    let missing = std::env::temp_dir().join("no-such-file.xml");

    assert!(maxx_tune_definitions::read(Some(&missing)).is_empty());
    assert!(maxx_tune_definitions::read(None).is_empty());
}

// ----- writing ---------------------------------------------------------------

struct EcuState {
    reply: Vec<u8>,
    status: u8,
    answer_the_header: bool,
    answer_the_payload: bool,
    tune: Vec<u8>,
    expecting: Option<(usize, usize)>,
    writes: i32,
    /// Everything that was put on the wire, in order.
    sent: Vec<Vec<u8>>,
}

/// An ECU that holds a tune and lets it be written the way a MaxxECU does: the header acknowledged
/// on its own, then the payload with its checksum. A cloneable handle, so the test keeps one.
#[derive(Clone)]
struct FakeEcu(Arc<Mutex<EcuState>>);

impl FakeEcu {
    fn new() -> Self {
        Self::answering(usb::OK)
    }

    fn answering(status: u8) -> Self {
        Self(Arc::new(Mutex::new(EcuState {
            reply: Vec::new(),
            status,
            answer_the_header: true,
            answer_the_payload: true,
            tune: vec![0; maxx_tune::BLOB_SIZE],
            expecting: None,
            writes: 0,
            sent: Vec::new(),
        })))
    }

    fn with<R>(&self, f: impl FnOnce(&mut EcuState) -> R) -> R {
        f(&mut self.0.lock().unwrap())
    }

    fn tune_at(&self, offset: usize, length: usize) -> Vec<u8> {
        self.with(|s| s.tune[offset..offset + length].to_vec())
    }

    fn writes(&self) -> i32 {
        self.with(|s| s.writes)
    }

    fn sent(&self) -> Vec<Vec<u8>> {
        self.with(|s| s.sent.clone())
    }
}

impl EcuTransport for FakeEcu {
    fn is_open(&self) -> bool {
        true
    }

    fn open(&mut self) -> Result<()> {
        Ok(())
    }

    fn close(&mut self) {}

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.with(|s| {
            s.sent.push(data.to_vec());

            if let Some((offset, length)) = s.expecting.take() {
                // The payload, and the checksum the ECU verifies before taking it.
                let sent = u32::from_le_bytes(data[length..length + 4].try_into().unwrap());

                if sent == maxx_protocol::crc32(&data[..length]) {
                    s.tune[offset..offset + length].copy_from_slice(&data[..length]);
                    s.writes += 1;

                    // Taken, and the acknowledgement lost on the way back, which is the case the
                    // caller must not read as "nothing happened".
                    s.reply = if s.answer_the_payload {
                        vec![usb::OK]
                    } else {
                        Vec::new()
                    };
                } else {
                    s.reply = vec![MaxxWriteStatus::OutOfRange.value()];
                }
                return;
            }

            let offset = usize::from(data[2]) | (usize::from(data[3]) << 8);
            let length = usize::from(data[4]) | (usize::from(data[5]) << 8);

            if data[0] == usb::WRITE {
                if !s.answer_the_header {
                    s.reply = Vec::new();
                    return;
                }
                if s.status != usb::OK {
                    s.reply = vec![s.status];
                    return;
                }

                s.expecting = Some((offset, length));
                s.reply = vec![usb::OK];
                return;
            }

            // A read of the tune, which is how a write is checked.
            let mut reply = vec![usb::OK];
            reply.extend_from_slice(&s.tune[offset..offset + length]);
            reply.extend_from_slice(&maxx_protocol::crc32(&reply).to_le_bytes());

            s.reply = reply;
        });

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(self.with(|s| {
            let taken = buffer.len().min(s.reply.len());
            buffer[..taken].copy_from_slice(&s.reply[..taken]);
            s.reply.drain(..taken);
            taken
        }))
    }

    fn discard_input(&mut self) {
        self.with(|s| s.reply.clear());
    }
}

fn write(ecu: &FakeEcu, offset: i32, data: &[u8]) -> Result<MaxxWriteStatus> {
    maxx_tune::write(&mut ecu.clone(), offset, data)
}

fn verify(ecu: &FakeEcu, offset: i32, expected: &[u8]) -> bool {
    maxx_tune::verify(&mut ecu.clone(), offset, expected).unwrap()
}

/// A table's cells go to the ECU with the checksum the firmware checks them against, computed over
/// the pad byte in front of the grid as well as the grid.
///
/// Cells written without it are cells the ECU has been told not to trust. The span was recovered by
/// searching for it against a real record and confirmed on three tables, and then confirmed again by
/// writing a cell to a bench ECU and having it keep the checksum given to it.
#[test]
fn a_tables_cells_are_sent_with_the_checksum_over_them() {
    let blob = ve();
    let table = maxx_tune::table_at(&blob, "VE Table 1 data", 1578).unwrap();

    let cells: Vec<i16> = (0..table.columns * table.rows).map(|i| (300 + i) as i16).collect();
    let record = maxx_tune::record(&blob, &table, &cells).unwrap();

    assert_eq!(record.len(), (cells.len() * 2) + 4);
    assert_eq!(i16::from_le_bytes([record[0], record[1]]), 300);

    let at = cells.len() * 2;
    let sent = u32::from_le_bytes(record[at..at + 4].try_into().unwrap());

    let mut covered = vec![blob[table.cells_at - 1]];
    covered.extend_from_slice(&record[..at]);
    let expected = maxx_protocol::crc32(&covered);

    assert_eq!(expected, sent);

    // And not the checksum of the cells alone, which is the tempting wrong span and differs.
    assert_ne!(maxx_protocol::crc32(&record[..at]), sent);
}

#[test]
fn a_grid_of_the_wrong_size_is_refused() {
    let blob = ve();
    let table = maxx_tune::table_at(&blob, "VE Table 1 data", 1578).unwrap();

    assert!(matches!(
        maxx_tune::record(&blob, &table, &[0; 3]),
        Err(Error::Argument(_))
    ));
}

/// Whether a table can be sent at all in one write, which on a controller with no burn is the
/// difference between an edit that is safe and one that leaves the ECU holding a grid its checksum
/// disagrees with.
#[test]
fn a_table_too_big_to_send_at_once_says_so() {
    let blob = ve();

    // 16 x 11 is 352 bytes of cells and cannot go in one 256-byte write.
    assert!(!maxx_tune::fits_in_one_write(
        &maxx_tune::table_at(&blob, "VE Table 1 data", 1578).unwrap()
    ));

    let mut small = blank();
    let axis = [10i16, 20, 30, 40, 50, 60];
    compose(&mut small, 400, 100, 61, 20, &axis, &axis, &vec![vec![0; 6]; 6]);

    // 6 x 6 is 72 bytes, and fits with its checksum.
    assert!(maxx_tune::fits_in_one_write(
        &maxx_tune::table_at(&small, "Fuel ASE Table data", 400).unwrap()
    ));
}

/// A table that fits goes in one write, so it is never inconsistent with its own checksum for even
/// a moment.
#[test]
fn a_table_that_fits_is_sent_as_one_write() {
    let mut blob = blank();
    let axis = [10i16, 20, 30, 40, 50, 60];
    compose(&mut blob, 400, 100, 61, 20, &axis, &axis, &vec![vec![0; 6]; 6]);

    let table = maxx_tune::table_at(&blob, "Fuel ASE Table data", 400).unwrap();
    let pieces = maxx_tune::writes_for(&blob, &table, &[0; 36]).unwrap();

    assert_eq!(pieces.len(), 1);
    assert_eq!(pieces[0].0, table.cells_at);
    assert_eq!(pieces[0].1.len(), (36 * 2) + 4);
}

/// A table too big for one write is split, and the checksum goes in the last piece.
///
/// That ordering is the whole safety property. Between the first piece landing and the last, the ECU
/// holds a grid its stored checksum disagrees with and reads the table as nought for one evaluation;
/// putting the checksum anywhere but last would leave it disagreeing after the cells were already
/// right.
#[test]
fn a_big_table_is_split_with_the_checksum_last() {
    let blob = ve();
    let table = maxx_tune::table_at(&blob, "VE Table 1 data", 1578).unwrap();

    let cells: Vec<i16> = (0..table.columns * table.rows).map(|i| (300 + i) as i16).collect();
    let pieces = maxx_tune::writes_for(&blob, &table, &cells).unwrap();

    assert!(pieces.len() > 1, "a 16 by 11 table does not fit in one write");
    assert!(
        pieces
            .iter()
            .all(|(_, data)| (1..=maxx_tune::MAXIMUM_WRITE).contains(&data.len()))
    );

    // Nose to tail from the first cell, with no gap and no overlap.
    assert_eq!(pieces[0].0, table.cells_at);

    for i in 1..pieces.len() {
        assert_eq!(pieces[i - 1].0 + pieces[i - 1].1.len(), pieces[i].0);
    }

    // The whole record, and the checksum at the end of the last piece.
    let whole: Vec<u8> = pieces.iter().flat_map(|(_, data)| data.iter().copied()).collect();
    assert_eq!(maxx_tune::record(&blob, &table, &cells).unwrap(), whole);

    let (last, tail) = pieces.last().unwrap();
    assert_eq!(table.cells_at + (cells.len() * 2) + 4, last + tail.len());
}

fn patterned() -> Vec<u8> {
    (0..maxx_tune::BLOB_SIZE).map(|i| (i * 7) as u8).collect()
}

/// The tune's checksums are one CRC-32 per 4 KB, and the last one covers only what is left rather
/// than a whole chunk.
///
/// The short last chunk is what made the hardware agreement convincing: over the range MTune asks
/// for, it is 740 bytes and not 4,096, and a reading that was merely close would not have matched
/// it.
#[test]
fn the_tunes_checksums_are_one_per_chunk_with_a_short_last() {
    let blob = patterned();

    let checksums = maxx_tune::checksums_of(&blob, maxx_tune::WRITE_CEILING).unwrap();

    assert_eq!(checksums.len(), 16);

    // The last covers 0xF2E7 - 15 * 4096 = 743 bytes.
    let last = maxx_tune::WRITE_CEILING as usize - (15 * maxx_tune::CHECKSUM_CHUNK);
    assert_eq!(last, 743);
    assert_eq!(
        maxx_protocol::crc32(&blob[15 * maxx_tune::CHECKSUM_CHUNK..15 * maxx_tune::CHECKSUM_CHUNK + last]),
        checksums[15]
    );

    assert_eq!(maxx_protocol::crc32(&blob[..maxx_tune::CHECKSUM_CHUNK]), checksums[0]);
}

/// Changing one byte changes the checksum of its chunk and no other, which is what makes this a
/// useful thing to compare after a write.
#[test]
fn only_the_chunk_that_changed_gets_a_new_checksum() {
    let mut blob = patterned();

    let before = maxx_tune::checksums_of(&blob, maxx_tune::WRITE_CEILING).unwrap();

    blob[(3 * maxx_tune::CHECKSUM_CHUNK) + 11] ^= 0xFF;

    let after = maxx_tune::checksums_of(&blob, maxx_tune::WRITE_CEILING).unwrap();

    assert_ne!(before[3], after[3]);

    for i in 0..before.len() {
        if i != 3 {
            assert_eq!(before[i], after[i], "chunk {i}");
        }
    }
}

/// A payload that went out and was never acknowledged is uncertain, not a failure.
///
/// This is the most dangerous status in the file. The ECU applies a write as it arrives and saves it
/// itself, so an acknowledgement lost on the way back looks exactly like a write that never happened,
/// and calling that "nothing was changed" tells somebody their tune is untouched when it may be
/// permanently different. The header is acknowledged separately, so silence there really does mean
/// nothing was sent.
#[test]
fn a_payload_sent_without_an_acknowledgement_is_uncertain_rather_than_failed() {
    let ecu = FakeEcu::new();
    ecu.with(|s| s.answer_the_payload = false);

    assert_eq!(write(&ecu, 1000, &[1, 2, 3, 4]).unwrap(), MaxxWriteStatus::Uncertain);

    // And it really did go out, which is the whole point.
    assert_eq!(ecu.tune_at(1000, 4), [1, 2, 3, 4]);
}

#[test]
fn a_header_that_is_not_answered_means_nothing_was_sent() {
    let ecu = FakeEcu::new();
    ecu.with(|s| s.answer_the_header = false);

    assert_eq!(write(&ecu, 1000, &[1, 2, 3, 4]).unwrap(), MaxxWriteStatus::NoAnswer);
    assert_eq!(ecu.writes(), 0);

    // Only the header went out: the payload never left.
    assert_eq!(ecu.sent().len(), 1);
}

// ----- one pass, one set of names ------------------------------------------

/// The tables a write uses carry the same names the editor shows.
///
/// They were worked out separately and could disagree: a table renamed for the layout because
/// something else had claimed its name kept the original in the list a write looks it up in, so that
/// table could never be sent: the button stayed shut with nothing to say why.
#[test]
fn the_tables_a_write_uses_are_named_like_the_ones_on_screen() {
    let mut blob = blank();
    let axis = [10i16, 20];
    let cells = vec![vec![0i16; 2]; 2];

    compose(&mut blob, 400, 100, 61, 20, &axis, &axis, &cells);
    compose(&mut blob, 500, 300, 61, 20, &axis, &axis, &cells);

    let definitions = vec![
        MaxxSettingDefinition::new("Boost Table data", "dynamicTable", 400, 4, 0.1, 0.0, 300.0),
        MaxxSettingDefinition::new("Boost Table data", "dynamicTable", 500, 4, 0.1, 0.0, 300.0),
    ];

    let model = maxx_tune::build(&blob, &definitions, None);

    assert_eq!(model.maps.len(), 2);

    let mut titles: Vec<&str> = model.tables.iter().map(|t| t.title.as_str()).collect();
    let mut names: Vec<&str> = model.maps.iter().map(|m| m.name.as_str()).collect();
    titles.sort_unstable();
    names.sort_unstable();

    assert_eq!(titles, names);
}

/// A settings page points at the constant the layout actually made, not at the name the file used.
///
/// Where a setting's name is already taken (by a table, by an axis, or by another setting) the layout
/// renames it. A page that went on referring to the original would put that row on top of whatever
/// holds the name now, and show one setting's bytes under another's label.
#[test]
fn a_settings_page_points_at_the_constant_the_layout_made() {
    let blob = ve();

    // Named exactly like the table's X axis, which is generated first.
    let mut definitions = ve_definition();
    definitions.push(MaxxSettingDefinition::new(
        "VE Table 1 X",
        "int16",
        9000,
        1,
        1.0,
        -30000.0,
        30000.0,
    ));

    let model = maxx_tune::build(&blob, &definitions, None);

    let targets: Vec<&str> = model
        .pages
        .dialogs
        .values()
        .flat_map(|d| &d.items)
        .map(|i| i.target_constant())
        .collect();

    assert!(
        targets
            .iter()
            .all(|t| model.layout.constants.iter().any(|c| c.name == *t))
    );

    // The setting did not take the axis over, so it is under some other name.
    let axes: Vec<_> = model
        .layout
        .constants
        .iter()
        .filter(|c| c.name == "VE Table 1 X")
        .collect();
    assert_eq!(axes.len(), 1);
    assert_eq!(axes[0].columns, 16);
    assert!(!targets.contains(&"VE Table 1 X"));
}

#[test]
fn a_write_lands_where_it_was_aimed() {
    let ecu = FakeEcu::new();
    let data = [1, 2, 3, 4];

    assert_eq!(write(&ecu, 1000, &data).unwrap(), MaxxWriteStatus::Ok);
    assert_eq!(ecu.tune_at(1000, 4), data);
    assert!(verify(&ecu, 1000, &data));
}

/// Reading back is what says a write took, and it has to be able to say no.
#[test]
fn verifying_fails_when_the_tune_does_not_match() {
    let ecu = FakeEcu::new();
    write(&ecu, 1000, &[1, 2, 3, 4]).unwrap();

    assert!(!verify(&ecu, 1000, &[1, 2, 3, 5]));
}

/// A refusal is reported as what the ECU said, not as a bare failure: busy is worth retrying and out
/// of range never is.
#[test]
fn a_refused_write_says_why() {
    for (answered, expected) in [
        (0x40, MaxxWriteStatus::WriterBusy),
        (0x30, MaxxWriteStatus::OutOfRange),
        (0x20, MaxxWriteStatus::UnknownCommand),
    ] {
        let ecu = FakeEcu::answering(answered);

        assert_eq!(write(&ecu, 1000, &[1, 2, 3, 4]).unwrap(), expected);
        assert_eq!(ecu.writes(), 0);
    }
}

/// A write past what the ECU accepts is refused here rather than sent.
///
/// The ECU would answer 0x30 and nothing would happen, so this is not about safety on the wire: it is
/// that the caller finds out before the bytes go out, which on a controller with no undo is the habit
/// worth having.
#[test]
fn a_write_above_the_ceiling_is_refused_before_it_is_sent() {
    let ecu = FakeEcu::new();

    assert!(matches!(
        write(&ecu, maxx_tune::WRITE_CEILING - 2, &[0; 4]),
        Err(Error::Argument(_))
    ));

    assert_eq!(ecu.writes(), 0);
    assert!(ecu.sent().is_empty(), "nothing at all may go on the wire");
}

/// The ceiling is exactly where the firmware's is: a write that ends on it is the last that may
/// land, and a byte more is refused. Not in the C#, which only checked two bytes over; pinning both
/// sides is what makes a changed constant fail here.
#[test]
fn the_ceiling_is_0xf2e7_and_a_write_ending_on_it_is_the_last_allowed() {
    assert_eq!(maxx_tune::WRITE_CEILING, 0xF2E7);

    let ecu = FakeEcu::new();

    // Ends exactly on the ceiling: allowed.
    assert_eq!(
        write(&ecu, maxx_tune::WRITE_CEILING - 4, &[7; 4]).unwrap(),
        MaxxWriteStatus::Ok
    );
    assert_eq!(ecu.writes(), 1);

    // One byte further: refused, and nothing further goes out.
    let sent = ecu.sent().len();
    let error = write(&ecu, maxx_tune::WRITE_CEILING - 3, &[7; 4]).unwrap_err();
    assert!(
        matches!(&error, Error::Argument(m) if m.contains("will not be written above 62183")),
        "{error}"
    );
    assert_eq!(ecu.sent().len(), sent);

    // A single byte at the ceiling itself is past it (offset + length must not exceed it).
    assert!(write(&ecu, maxx_tune::WRITE_CEILING, &[1]).is_err());

    // And the offsets that would overflow a 32-bit sum are refused, not wrapped past the check.
    assert!(write(&ecu, i32::MAX, &[1]).is_err());
    assert!(write(&ecu, -1, &[1]).is_err());
    assert_eq!(ecu.sent().len(), sent);
}

#[test]
fn a_write_longer_than_the_ecu_takes_is_refused_before_it_is_sent() {
    let ecu = FakeEcu::new();

    assert!(matches!(
        write(&ecu, 1000, &vec![0u8; maxx_tune::MAXIMUM_WRITE + 1]),
        Err(Error::Argument(_))
    ));
    assert!(matches!(write(&ecu, 1000, &[]), Err(Error::Argument(_))));

    assert_eq!(ecu.writes(), 0);
    assert!(ecu.sent().is_empty());
}

/// Every write names the tune command, in the write direction, and nothing else: command 0x2F erases
/// the ECU's flash and no path in here can name it.
#[test]
fn a_write_only_ever_names_the_tune_command() {
    let ecu = FakeEcu::new();
    write(&ecu, 2000, &[1, 2, 3]).unwrap();
    verify(&ecu, 2000, &[1, 2, 3]);

    let header = &ecu.sent()[0];
    assert_eq!(header[0], usb::WRITE);
    assert_eq!(header[1], maxx_tune::READ_TUNE);
    assert_eq!(header[1], 0x05);

    for request in ecu.sent().iter().filter(|r| r.len() == usb::REQUEST_LENGTH) {
        assert_ne!(request[1], usb::ERASE_FLASH);
    }
}

/// The payload carries its own checksum, and a wrong one is not taken.
///
/// Checked by writing properly and then corrupting the same exchange: the fake refuses it exactly as
/// the firmware does, which is what makes the checksum in `write` load-bearing rather than
/// decorative.
#[test]
fn the_payload_is_sent_with_a_checksum_over_it() {
    let ecu = FakeEcu::new();
    let data = [9, 8, 7, 6];

    write(&ecu, 2000, &data).unwrap();

    // What write sends: the bytes, then the CRC-32 of exactly those bytes.
    let mut expected = data.to_vec();
    expected.extend_from_slice(&maxx_protocol::crc32(&data).to_le_bytes());

    assert_eq!(ecu.sent()[1], expected);
    assert_eq!(ecu.tune_at(2000, 4), data);
}

/// A table's cell address comes from the ECU's own live config struct, not from the definitions
/// file's address cursor every setting uses: found on a real bench Race, where an unconfigured user
/// table's cells began one byte into an unrelated setting's own declared address. Reported by name
/// rather than refused; see `settings_overlapping` for why a blocking version of this check turned
/// out not to be trustworthy.
#[test]
fn a_table_write_that_would_land_on_a_named_setting_is_reported() {
    let definitions = [MaxxSettingDefinition::new(
        "Fuel Trim Min CLT",
        "int16",
        21830,
        1,
        0.1,
        f64::NAN,
        f64::NAN,
    )];

    // The table's cells start one byte into that setting's two.
    assert_eq!(
        maxx_tune::settings_overlapping(&definitions, 21831, 50),
        ["Fuel Trim Min CLT"]
    );

    // Just past it, or well clear of it, is not a collision.
    assert!(maxx_tune::settings_overlapping(&definitions, 21832, 50).is_empty());
    assert!(maxx_tune::settings_overlapping(&definitions, 30000, 50).is_empty());
}

#[test]
fn non_numeric_definitions_do_not_count_as_claiming_their_address() {
    let definitions = [
        MaxxSettingDefinition::new("Some Script", "miniScript", 5000, 1, 1.0, f64::NAN, f64::NAN),
        MaxxSettingDefinition::new("Some Table Kind", "table", 5000, 1, 1.0, f64::NAN, f64::NAN),
    ];

    assert!(maxx_tune::settings_overlapping(&definitions, 5000, 4).is_empty());
}

/// A table's own config struct is a real claim on its bytes too, not only scalar settings; see
/// `settings_overlapping`.
#[test]
fn a_table_config_struct_counts_as_claiming_its_address_too() {
    let definitions = [MaxxSettingDefinition::new(
        "Other Table data",
        "dynamicTable",
        9646,
        1,
        1.0,
        f64::NAN,
        f64::NAN,
    )];

    assert_eq!(
        maxx_tune::settings_overlapping(&definitions, 9650, 10),
        ["Other Table data"]
    );
    assert!(maxx_tune::settings_overlapping(&definitions, 9667, 10).is_empty());
}

/// Every real name that overlaps is reported, not just the first: a caller deciding whether to worry
/// needs to see how much a write would actually touch, not just that it touches something.
#[test]
fn every_overlapping_setting_is_reported_not_just_one() {
    let setting = |name: &str, at: i32| MaxxSettingDefinition::new(name, "uint8", at, 1, 1.0, f64::NAN, f64::NAN);
    let definitions = [
        setting("First", 100),
        setting("Second", 101),
        setting("Third", 102),
        setting("Unrelated", 500),
    ];

    assert_eq!(
        maxx_tune::settings_overlapping(&definitions, 100, 3),
        ["First", "Second", "Third"]
    );
}

// ----- reading ----------------------------------------------------------------

/// `MaxxTable` for a caller that holds only a name and a config address.
#[test]
fn a_table_carries_where_its_axes_and_cells_are() {
    let table: MaxxTable = maxx_tune::table_at(&ve(), "VE Table 1 data", 1578).unwrap();

    // record + 1, then 16 and 11 two-byte breakpoints, then the nine-byte preamble.
    let record = maxx_tune::TABLE_DATA + 496;
    assert_eq!(table.x_axis_at, record + 1);
    assert_eq!(table.y_axis_at, record + 1 + 16 * 2);
    assert_eq!(table.cells_at, record + 1 + 16 * 2 + 11 * 2 + 9);
}
