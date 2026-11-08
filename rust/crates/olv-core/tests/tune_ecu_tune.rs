//! Ported from `EcuTuneTests.cs`: the tune as read out of an ECU, decoded.

use olv_core::ini::tune_layout::{self, TuneConstant, TuneLayout};
use olv_core::ini::{gauge_catalog, table_editor_reader, tuning_context};
use olv_core::model::RealtimeCommand;
use olv_core::tune::ecu_tune::EcuTune;

/// A small firmware definition with the shape of a real one.
const INI: &str = r#"[Constants]
endianness     = little
nPages         = 1
pageSize       = 64
pageIdentifier = "\x00\x00"
pageReadCommand = "R%2o%2c"
blockingFactor = 16

page = 1
rpmHardLimit = scalar, U16, 0, "rpm", 1, 0, 0, 20000, 0
displacement = scalar, F32, 4, "L", 1, 0, 0, 10, 3
twoStroke    = bits,   U08, 8, [0:0], "four", "two"
veTable      = array,  U08, 16, [4x3], "%", 0.5, 0, 0, 999, 1
veRpmBins    = array,  U16, 32, [4], "RPM", 1, 0, 0, 18000, 0
veLoadBins   = array,  U08, 40, [3], "kPa", 1, 0, 0, 255, 0

[TableEditor]
table = veTableTbl, veTableMap, "VE Table", 1
  xBins = veRpmBins, RPMValue
  yBins = veLoadBins, veTableYAxis
  zBins = veTable
table = brokenTbl, brokenMap, "Missing Its Axes", 1
  xBins = noSuchThing, RPMValue
  yBins = veLoadBins, veTableYAxis
  zBins = veTable
"#;

fn layout() -> TuneLayout {
    tune_layout::read(INI, None)
}

/// A page image with known values at the declared offsets.
fn page() -> Vec<u8> {
    let mut page = vec![0u8; 64];

    page[0..2].copy_from_slice(&5500u16.to_le_bytes());
    page[4..8].copy_from_slice(&3.378f32.to_le_bytes());
    page[8] = 0b0000_0001;

    // 4 columns by 3 rows, row-major, at half a percent each.
    let cells: [u8; 12] = [20, 40, 60, 80, 100, 120, 140, 160, 180, 200, 220, 240];
    page[16..28].copy_from_slice(&cells);

    page[32..34].copy_from_slice(&600u16.to_le_bytes());
    page[34..36].copy_from_slice(&2000u16.to_le_bytes());
    page[36..38].copy_from_slice(&4000u16.to_le_bytes());
    page[38..40].copy_from_slice(&6000u16.to_le_bytes());

    page[40] = 30;
    page[41] = 60;
    page[42] = 90;

    page
}

fn tune() -> EcuTune {
    EcuTune::from_pages(layout(), vec![page()])
}

/// xunit's `Assert.Equal(expected, actual, precision)`: both rounded to that many places.
fn assert_places(expected: f64, actual: f64, places: i32) {
    let scale = 10f64.powi(places);
    assert_eq!(
        (expected * scale).round() / scale,
        (actual * scale).round() / scale,
        "{expected} vs {actual} to {places} places"
    );
}

fn named<'a>(constants: &'a [TuneConstant], name: &str) -> &'a TuneConstant {
    let mut found = constants.iter().filter(|c| c.name == name);
    let one = found.next().expect("one constant of that name");
    assert!(found.next().is_none());
    one
}

// ----- the layout -------------------------------------------------------

#[test]
fn the_page_is_read_with_its_size_command_and_identifier() {
    let layout = layout();
    assert_eq!(layout.pages.len(), 1);
    let page = &layout.pages[0];

    assert_eq!(page.size, 64);
    assert_eq!(page.read_command, "R%2o%2c");
    assert_eq!(page.identifier, r"\x00\x00");
}

#[test]
fn pages_are_numbered_from_zero_even_though_the_file_counts_from_one() {
    // "page = 1" introduces the first page. Reading it as index 1 puts every
    // constant on a page that does not exist.
    assert!(layout().constants.iter().all(|c| c.page == 0));
}

#[test]
fn the_byte_order_and_blocking_factor_come_from_the_same_section() {
    let layout = layout();

    assert!(layout.little_endian);
    assert_eq!(layout.blocking_factor, 16);
}

#[test]
fn scalars_arrays_and_bit_fields_are_all_read() {
    let layout = layout();
    let constants = &layout.constants;

    assert_eq!(constants.len(), 6);
    assert!(!named(constants, "rpmHardLimit").is_array());
    assert!(named(constants, "twoStroke").is_bit_field());

    let ve = named(constants, "veTable");
    assert!(ve.is_array());
    assert_eq!(ve.columns, 4);
    assert_eq!(ve.rows, 3);
    assert_eq!(ve.size(), 12);
}

#[test]
fn nothing_reaches_past_the_end_of_its_page() {
    let layout = layout();

    assert!(
        layout
            .constants
            .iter()
            .all(|c| c.offset + c.size() <= layout.pages[c.page as usize].size)
    );
}

// ----- decoding ---------------------------------------------------------

#[test]
fn scalars_decode_with_their_scale() {
    let tune = tune();

    assert_eq!(tune.scalar("rpmHardLimit"), 5500.0);
    assert_places(3.378, tune.scalar("displacement"), 4);
    assert_eq!(tune.scalar("twoStroke"), 1.0);
}

#[test]
fn an_absent_setting_is_not_a_number() {
    assert!(tune().scalar("nothingLikeThis").is_nan());
}

#[test]
fn arrays_are_scaled_too() {
    let cells = tune().array("veTable").expect("an array");

    assert_eq!(
        cells,
        vec![
            10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0, 110.0, 120.0
        ]
    );
}

#[test]
fn a_big_endian_firmware_decodes_the_other_way_round() {
    // Little-endian bytes 5500 read big-endian are 32021 — a number, and the
    // wrong one, which is why this comes from the INI.
    let mut layout = layout();
    layout.little_endian = false;
    let tune = EcuTune::from_pages(layout, vec![page()]);

    assert_ne!(tune.scalar("rpmHardLimit"), 5500.0);
}

// ----- tables -----------------------------------------------------------

#[test]
fn a_table_comes_back_with_its_axes_the_right_way_round() {
    // The first dimension runs along X. Transposed, a square table is wrong
    // in a way nothing catches.
    let tables = tune().tables(&table_editor_reader::read(INI, None));
    assert_eq!(tables.len(), 1);
    let table = &tables[0];

    assert_eq!(table.name, "VE Table");
    assert_eq!(table.columns(), 4);
    assert_eq!(table.rows(), 3);

    assert_eq!(table.x.breakpoints, vec![600.0, 2000.0, 4000.0, 6000.0]);
    assert_eq!(table.y.breakpoints, vec![30.0, 60.0, 90.0]);

    // Row-major in the page: row 0 is 10,20,30,40.
    assert_eq!(table.values[(0, 0)], 10.0);
    assert_eq!(table.values[(3, 0)], 40.0);
    assert_eq!(table.values[(0, 2)], 90.0);
    assert_eq!(table.values[(3, 2)], 120.0);
}

#[test]
fn a_table_naming_something_absent_is_left_out_rather_than_assembled() {
    // Two are declared; the second names an axis this firmware has not got.
    // Half a table is not a near miss.
    let definitions = table_editor_reader::read(INI, None);

    assert_eq!(definitions.len(), 2);
    assert_eq!(tune().tables(&definitions).len(), 1);
}

#[test]
fn the_axis_channels_are_carried_through() {
    // What the ECU indexes the table by, which is what a log has to be
    // binned against to reproduce its decisions.
    let definitions = table_editor_reader::read(INI, None);
    let ve = definitions
        .iter()
        .find(|d| d.looks_like_ve_table())
        .expect("a VE table");

    assert_eq!(ve.x_channel, "RPMValue");
    assert_eq!(ve.y_channel, "veTableYAxis");
    assert_eq!(ve.values, "veTable");
}

#[test]
fn the_tune_supplies_the_context_gauge_scales_are_written_against() {
    // The whole reason for reading it at connect: a rev counter runs to
    // {rpmHardLimit + 2000} and has no scale until something says what that
    // is.
    let gauges = r#"[GaugeConfigurations]
RPMGauge = RPMValue, "RPM", "RPM", 0, {rpmHardLimit + 2000}, 200, 350, {rpmHardLimit - 500}, {rpmHardLimit}, 0, 0
"#;

    let scalars = tune().scalars();
    let context = tuning_context::build(Some(INI), None, None, Some(&scalars));

    let read = gauge_catalog::read(gauges, Some(&context), None);
    assert_eq!(read.len(), 1);
    let rpm = &read[0];

    assert!(rpm.has_scale());
    assert_eq!(rpm.high, 7500.0);
    assert_eq!(rpm.high_danger, 5500.0);
}

#[test]
fn what_the_ecu_says_beats_what_a_file_says() {
    // A saved tune can be stale; the controller cannot be.
    let stale = r#"<?xml version="1.0"?>
<msq><page><constant name="rpmHardLimit">3000</constant></page></msq>"#;

    let scalars = tune().scalars();
    let context = tuning_context::build(Some(INI), Some(stale), None, Some(&scalars));

    assert_eq!(context.get("rpmHardLimit").copied(), Some(5500.0));
}

// ----- the request ------------------------------------------------------

#[test]
fn a_page_identifier_is_substituted_for_its_placeholder() {
    // "%2i" is not a number but the page's own identifier bytes.
    let identifier = RealtimeCommand::parse(r"\$tsCanId\x04")
        .build(0, 1, 2, false, &[], &[])
        .unwrap();
    let request = RealtimeCommand::parse("r%2i%2o%2c")
        .build(0, 1024, 2, false, &identifier, &[])
        .unwrap();

    assert_eq!(request, vec![b'r', 2, 0x04, 0, 0, 0x04, 0x00]);
}

#[test]
fn a_template_with_no_placeholder_is_unaffected_by_an_identifier() {
    let request = RealtimeCommand::parse("R%2o%2c")
        .build(0, 1024, 0, true, &[0xAA, 0xBB], &[])
        .unwrap();

    assert_eq!(request, vec![b'R', 0, 0, 0x00, 0x04]);
}

// ----- a scale the firmware works out for itself ---------------------------

/// A Speeduino, in miniature. Its load axes are scaled by fuelLoadRes, which
/// is not a setting at all but a line in [OutputChannels] choosing between
/// two numbers depending on the fuel algorithm.
const DERIVED_SCALE_INI: &str = r#"[Constants]
page = 1
nPages = 1
pageSize = 32
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
pageChunkWrite  = "w%2o%2c%v"
burnCommand     = "b%2i"
   algorithm    = bits,  U08, 0, [0:2], "MAP", "TPS", "IMAP", "MAF"
   fuelLoadBins = array, U08, 4, [4], "kPa", {fuelLoadRes}, 0.0, 0.0, 511, 1

[OutputChannels]
ochBlockSize = 4
ochGetCommand = "A"
   rpm = scalar, U16, 0, "rpm", 1, 0
   fuelLoadRes = { ((algorithm == 0) || (algorithm == 2)) ? 2.000 : 0.500 }
"#;

fn derived_tune(ini: &str) -> EcuTune {
    EcuTune::from_pages(tune_layout::read(ini, None), vec![vec![0u8; 32]])
}

fn constant_of(tune: &EcuTune, name: &str) -> TuneConstant {
    tune.constant(name).expect("a constant").clone()
}

#[test]
fn a_scale_the_firmware_derives_is_worked_out_rather_than_fallen_back_from() {
    // Found on a real Speeduino: every load axis read at half, because the
    // scale is {fuelLoadRes} and the resolver only knew about settings. It
    // fell back to the declared 1 and TunerStudio used 2, so the same tune
    // looked like two — and restoring one would have doubled the axis.
    let mut tune = derived_tune(DERIVED_SCALE_INI);

    // algorithm 0 is MAP, so the resolution is 2.
    let algorithm = constant_of(&tune, "algorithm");
    tune.poke_into_own(&algorithm, 0, 0.0);
    tune.rescale();

    assert_places(2.0, tune.constant("fuelLoadBins").unwrap().scale, 6);

    // A raw 5 therefore means 10, which is what TunerStudio shows.
    tune.pages_mut()[0][4] = 5;
    assert_eq!(tune.value_in(tune.pages(), "fuelLoadBins", 0), Some(10.0));
}

#[test]
fn and_follows_the_setting_it_depends_on() {
    let mut tune = derived_tune(DERIVED_SCALE_INI);

    // algorithm 1 is TPS, so the other arm: half.
    let algorithm = constant_of(&tune, "algorithm");
    tune.poke_into_own(&algorithm, 0, 1.0);
    tune.rescale();

    assert_places(0.5, tune.constant("fuelLoadBins").unwrap().scale, 6);
}

#[test]
fn and_encodes_through_it_too() {
    // The half that would have corrupted a controller. Writing 10 with the
    // scale resolved stores raw 5; with the fallback it stored raw 10.
    let mut tune = derived_tune(DERIVED_SCALE_INI);

    let algorithm = constant_of(&tune, "algorithm");
    tune.poke_into_own(&algorithm, 0, 0.0);
    tune.rescale();

    let bins = constant_of(&tune, "fuelLoadBins");
    assert!(tune.poke_into_own(&bins, 0, 10.0));
    assert_eq!(tune.pages()[0][4], 5);
}

#[test]
fn a_derived_value_defined_in_terms_of_itself_does_not_hang() {
    // A definition can say anything, and this has to survive it.
    let circular = DERIVED_SCALE_INI.replace(
        "fuelLoadRes = { ((algorithm == 0) || (algorithm == 2)) ? 2.000 : 0.500 }",
        "fuelLoadRes = { fuelLoadRes + 1 }",
    );

    let mut tune = derived_tune(&circular);

    tune.rescale();

    // Falls back to the declared scale rather than looping.
    assert_places(1.0, tune.constant("fuelLoadBins").unwrap().scale, 6);
}

#[test]
fn scalars_read_through_the_resolved_scale_like_scalar_does() {
    // Defect fixed in the Rust: `scalars` used the declared scale (the fallback 1) where
    // `scalar` used the one worked out from {fuelLoadRes}, so the two disagreed.
    let ini = DERIVED_SCALE_INI.replace(
        "   fuelLoadBins = array,",
        "   loadNow = scalar, U08, 8, \"kPa\", {fuelLoadRes}, 0.0, 0.0, 511, 1
   fuelLoadBins = array,",
    );
    let mut tune = derived_tune(&ini);
    let algorithm = constant_of(&tune, "algorithm");
    tune.poke_into_own(&algorithm, 0, 0.0);
    tune.rescale();
    tune.pages_mut()[0][8] = 5;

    assert_eq!(tune.scalar("loadNow"), 10.0);
    assert_eq!(tune.scalars().get("loadNow").copied(), Some(10.0));
}

#[test]
fn identical_tunes_with_an_unknown_cell_do_not_differ() {
    // Defect fixed in the Rust: NaN cells were counted as differing, even between a tune and
    // itself, because NaN is not within a tolerance of anything.
    use olv_core::tune::tune_compare;

    let mut page = vec![0u8; 64];
    page[4..8].copy_from_slice(&f32::NAN.to_le_bytes());
    page[0..2].copy_from_slice(&5500u16.to_le_bytes());
    let tune = EcuTune::from_pages(layout(), vec![page.clone()]);
    assert!(tune.scalar("displacement").is_nan());

    assert!(tune_compare::compare(&tune, &tune).is_empty());

    // A real difference is still found beside the NaN.
    page[0..2].copy_from_slice(&6000u16.to_le_bytes());
    let other = EcuTune::from_pages(layout(), vec![page]);
    let differences = tune_compare::compare(&tune, &other);
    assert_eq!(differences.len(), 1);
    assert_eq!(differences[0].name, "rpmHardLimit");
}
