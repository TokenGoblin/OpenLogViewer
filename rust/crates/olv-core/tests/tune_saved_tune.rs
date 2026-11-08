//! Ported from `SavedTuneTests.cs`: saved tunes, reading an `.msq`, laying it over a firmware
//! definition, writing one back out, and saying what two tunes disagree about.

use olv_core::Error;
use olv_core::definitions::msq_file::MsqFile;
use olv_core::ini::msq_ini::SymbolSet;
use olv_core::ini::tune_layout::{self, TuneConstant, TuneLayout};
use olv_core::tune::ecu_tune::EcuTune;
use olv_core::tune::msq_apply;
use olv_core::tune::msq_writer;
use olv_core::tune::tune_compare;

/// A small firmware: a scalar, a scaled one, a bit field whose padding repeats a label, a text
/// field and a table.
const INI: &str = r#"#define pins = "Off", "INVALID", "Out A", "INVALID"

[Constants]
page = 1
nPages = 1
pageSize = 64
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
   crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
   dwell       = scalar, U08, 2, "ms", 0.0666, 0, 0, 12, 1
   coolant     = scalar, U08, 3, "F", 1.8, -22.23, -40, 215, 0
   pulseWidth  = scalar, U08, 6, "ms", 0.02, 0, 0, 5, 1
   fanPin      = bits,   U08, 4, [0:1], $pins
   fanOn       = bits,   U08, 4, [2:2], "No", "Yes"
   alias       = string, ASCII, 8, 6
   veTable     = array,  U08, 16, [2x2], "%", 0.5, 0, 0, 120, 1
   mafRange    = bits,   U08, 7, [0:1], "650g/s", "1300g/s", "1950g/s", "2600g/s"
   mafFlow     = array,  U16, 24, [2], "g/sec", {0.01 * (mafRange + 1)}, 0, 0, {655 * (mafRange + 1)}, 2
   period      = scalar, U16, 28, "ms", 0.128, 0, 0, 8388, 1
   equivRpm    = scalar, U16, 28, "RPM", 1.0, { (120000/period) - (period/0.128) }, 0, 937500, 0
"#;

fn layout() -> TuneLayout {
    tune_layout::read(INI, None)
}

fn constant(layout: &TuneLayout, name: &str) -> TuneConstant {
    layout
        .constants
        .iter()
        .find(|c| c.name == name)
        .unwrap_or_else(|| panic!("no constant {name}"))
        .clone()
}

fn tune(settings: &[(&str, f64)]) -> EcuTune {
    let layout = layout();
    let constants: Vec<_> = settings.iter().map(|(n, _)| constant(&layout, n)).collect();
    let mut tune = EcuTune::from_pages(layout, vec![vec![0u8; 64]]);

    for ((_, value), c) in settings.iter().zip(&constants) {
        assert!(tune.poke_into_own(c, 0, *value));
    }

    tune
}

fn write(tune: &EcuTune, signature: &str) -> String {
    msq_writer::write(tune, signature, None, "", None)
}

/// xunit's `Assert.Equal(expected, actual, precision)`: both rounded to that many decimals.
fn assert_digits(expected: f64, actual: f64, digits: i32) {
    let p = 10f64.powi(digits);
    let r = |v: f64| (v * p).round_ties_even() / p;
    assert_eq!(r(expected), r(actual), "{expected} vs {actual}");
}

// ----- reading a file ---------------------------------------------------

#[test]
fn a_tune_carries_the_symbols_of_the_build_it_came_from() {
    // The reason this matters: the same definition describes a Fahrenheit build and a Celsius
    // one, and the tune is the only thing that knows which. Reading it with the wrong ones
    // scales every temperature.
    let file = MsqFile::read(
        r#"<?xml version="1.0" encoding="ISO-8859-1"?>
<msq xmlns="http://www.msefi.com/:msq">
<versionInfo fileFormat="5.0" nPages="1" signature="MS2Extra comms342a2"/>
<page number="0" size="64"><constant digits="0" name="crankingRPM">600.0</constant></page>
<settings>
<setting name="FAHRENHEIT" value="FAHRENHEIT"/>
<setting name="CAN_COMMANDS" value="CAN_COMMANDS"/>
</settings>
</msq>"#,
    )
    .unwrap();

    assert_eq!(file.signature, "MS2Extra comms342a2");
    assert_eq!(file.page_count, 1);
    assert!(file.symbols.contains("FAHRENHEIT"));
    assert_eq!(file.values.get("crankingRPM").unwrap().trim(), "600.0");
}

#[test]
fn two_settings_named_the_same_but_for_their_case_are_two_settings() {
    // MS2Extra really does this: MAFFlow is a twelve-point flow curve on one page and mafflow
    // a sixty-four-point one on another, and its own saved tunes store both. Merging them
    // loses one.
    let file = MsqFile::read(
        r#"<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="8">
<constant name="MAFFlow">1.0</constant>
<constant name="mafflow">2.0</constant>
</page>
</msq>"#,
    )
    .unwrap();

    assert_eq!(file.values.len(), 2);
    assert_eq!(file.value("MAFFlow").unwrap().trim(), "1.0");
    assert_eq!(file.value("mafflow").unwrap().trim(), "2.0");
}

#[test]
fn a_loose_match_still_finds_a_setting_spelled_differently() {
    // Most firmwares are not careful about case, and a name that used to resolve must keep
    // resolving.
    let file = MsqFile::read(
        r#"<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="8"><constant name="CrankingRPM">600.0</constant></page>
</msq>"#,
    )
    .unwrap();

    assert_eq!(file.value("crankingrpm").unwrap().trim(), "600.0");
}

#[test]
fn something_that_is_not_a_tune_is_refused_rather_than_read_as_an_empty_one() {
    assert!(matches!(
        MsqFile::read("<html><body>not a tune</body></html>"),
        Err(Error::LogFormat(_))
    ));
    assert!(matches!(
        MsqFile::read("this is not xml at all <<<"),
        Err(Error::LogFormat(_))
    ));
}

// ----- laying it over a definition --------------------------------------

#[test]
fn values_come_back_as_the_file_stated_them() {
    let load = msq_apply::load(
        &layout(),
        &MsqFile::read(
            r#"<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="64">
<constant name="crankingRPM">600.0</constant>
<constant name="dwell">3.7296</constant>
<constant name="coolant">68.0</constant>
<constant name="fanPin">"Out A"</constant>
<constant name="fanOn">"Yes"</constant>
<constant name="alias">"CLT"</constant>
<constant name="pulseWidth">1.12</constant>
<constant name="mafRange">"650g/s"</constant>
<constant cols="2" name="mafFlow" rows="1"> 1.50 3.00 </constant>
<constant name="period">1.024</constant>
<constant name="equivRpm">8.0</constant>
<constant cols="2" name="veTable" rows="2"> 40.0 41.0
 42.0 43.0 </constant>
</page>
</msq>"#,
        )
        .unwrap(),
        None,
    );

    let tune = &load.tune;

    assert!(load.is_complete(), "{}", load.summary());
    assert_eq!(tune.scalar("crankingRPM"), 600.0);
    assert_digits(3.7296, tune.scalar("dwell"), 4);
    // Half a degree out, which is all a byte can hold: 68 F is a raw 60.01 and the byte keeps
    // 60.
    assert_digits(68.0, tune.scalar("coolant"), 1);
    assert_eq!(tune.scalar("fanPin"), 2.0);
    assert_eq!(tune.scalar("fanOn"), 1.0);
    assert_eq!(tune.text_in(tune.pages(), "alias").as_deref(), Some("CLT"));
    assert_eq!(tune.array("veTable").unwrap(), vec![40.0, 41.0, 42.0, 43.0]);
}

#[test]
fn a_setting_the_file_never_mentions_is_reported_rather_than_left_at_zero() {
    // The dangerous case, and the reason the report exists: a page of zeros looks exactly like
    // a page of settings, and sending one writes them.
    let load = msq_apply::load(
        &layout(),
        &MsqFile::read(
            r#"<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="64"><constant name="crankingRPM">600.0</constant></page>
</msq>"#,
        )
        .unwrap(),
        None,
    );

    assert!(!load.is_complete());
    assert!(load.missing.iter().any(|m| m.name == "dwell"));
    assert_eq!(load.applied, 1);
    assert!(load.looks_like_another_firmware());
}

#[test]
fn an_option_the_firmware_does_not_offer_is_refused_rather_than_guessed_at() {
    let load = msq_apply::load(
        &layout(),
        &MsqFile::read(
            r#"<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="64"><constant name="fanPin">"Out Z"</constant></page>
</msq>"#,
        )
        .unwrap(),
        None,
    );

    assert!(load.rejected.iter().any(|r| r.name == "fanPin"));
}

#[test]
fn a_table_of_the_wrong_shape_is_refused() {
    let load = msq_apply::load(
        &layout(),
        &MsqFile::read(
            r#"<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="64">
<constant cols="2" name="veTable" rows="2"> 40.0 41.0 42.0 </constant>
</page>
</msq>"#,
        )
        .unwrap(),
        None,
    );

    assert!(
        load.rejected
            .iter()
            .any(|r| r.name == "veTable" && r.reason.contains('3'))
    );
}

#[test]
fn names_in_the_file_that_this_firmware_lacks_are_counted_not_applied() {
    let load = msq_apply::load(
        &layout(),
        &MsqFile::read(
            r#"<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="64">
<constant name="crankingRPM">600.0</constant>
<constant name="somethingElseEntirely">1.0</constant>
</page>
</msq>"#,
        )
        .unwrap(),
        None,
    );

    assert!(load.unknown.iter().any(|u| u == "somethingElseEntirely"));
}

// ----- writing one back out ---------------------------------------------

#[test]
fn a_tune_survives_being_written_and_read_back() {
    let before = tune(&[
        ("crankingRPM", 600.0),
        ("dwell", 3.7296),
        ("coolant", 68.0),
        ("fanOn", 1.0),
    ]);

    let after = msq_apply::load(
        &layout(),
        &MsqFile::read(&write(&before, "test firmware")).unwrap(),
        None,
    );

    assert!(after.is_complete(), "{}", after.summary());
    assert_eq!(before.pages()[0], after.tune.pages()[0]);
}

#[test]
fn a_number_is_written_with_enough_decimals_to_get_the_same_byte_back() {
    // This one is stored in hundredths and asks for one decimal, so a raw 56 is 1.12 ms shown
    // as "1.1", and 1.1 read back is a raw 55. Writing what is displayed rather than what is
    // stored moves the setting a step every time a tune goes through a file, which is drift
    // nobody would attribute to the tool. It was found on a real MS2 tune, where two bytes of
    // 7,168 came back one out.
    let tune = tune(&[("pulseWidth", 1.12)]);

    assert_eq!(tune.pages()[0][6], 56);

    let xml = write(&tune, "test firmware");

    assert!(xml.contains("1.12"));
    let back = msq_apply::load(&layout(), &MsqFile::read(&xml).unwrap(), None);
    assert_eq!(back.tune.pages()[0][6], 56);
}

#[test]
fn a_setting_that_needs_no_more_decimals_gets_no_more() {
    // The search stops at the firmware's own digits where those suffice, so an ordinary file
    // still reads like an ordinary file.
    assert!(write(&tune(&[("crankingRPM", 600.0)]), "test firmware").contains(">600.0<"));
}

#[test]
fn an_option_label_shared_with_another_value_is_written_as_its_number() {
    // Firmwares pad an option list out to the width of the field, so the same word repeats:
    // this one has "INVALID" at 1 and at 3. An unconfigured pin really does read 3, and
    // writing the word would restore it as pin 1: a real output, quietly assigned.
    let tune = tune(&[("fanPin", 3.0)]);

    let xml = write(&tune, "test firmware");

    assert!(!xml.contains("\"INVALID\""));
    let back = msq_apply::load(&layout(), &MsqFile::read(&xml).unwrap(), None);
    assert_eq!(back.tune.scalar("fanPin"), 3.0);
}

#[test]
fn an_option_label_that_names_only_itself_is_written_as_the_label() {
    let xml = write(&tune(&[("fanPin", 2.0)]), "test firmware");

    assert!(xml.contains("\"Out A\""));
}

#[test]
fn the_signature_and_the_symbols_are_written_so_the_file_can_be_read_back() {
    let symbols = SymbolSet::ordinal(["FAHRENHEIT", "CAN_COMMANDS"]);

    let back = MsqFile::read(&msq_writer::write(
        &tune(&[]),
        "speeduino 202501",
        Some(&symbols),
        "",
        None,
    ))
    .unwrap();

    let mut got: Vec<&str> = back.symbols.iter().collect();
    got.sort_unstable();
    assert_eq!(back.signature, "speeduino 202501");
    assert_eq!(got, vec!["CAN_COMMANDS", "FAHRENHEIT"]);
}

#[test]
fn laying_a_file_over_an_ecu_leaves_bits_no_constant_declares_alone() {
    // A definition does not declare every bit it has. On a Speeduino that is 81 bytes of
    // 3,408, and starting a restore from zero would clear them.
    let mut controller = EcuTune::from_pages(layout(), vec![vec![0u8; 64]]);
    controller.pages_mut()[0][5] = 0xFF; // no constant is declared here
    controller.pages_mut()[0][4] = 0b1000_0111; // bit 7 belongs to nothing either

    let xml = write(&tune(&[("fanPin", 3.0), ("fanOn", 1.0)]), "test firmware");
    let load = msq_apply::load(&layout(), &MsqFile::read(&xml).unwrap(), Some(&controller));

    assert_eq!(load.tune.pages()[0][5], 0xFF);
    assert_eq!(load.tune.pages()[0][4] & 0b1000_0000, 0b1000_0000);
    assert_eq!(load.tune.scalar("fanPin"), 3.0);
}

// ----- comparing two ------------------------------------------------------

#[test]
fn a_tune_compared_with_itself_differs_in_nothing() {
    assert!(tune_compare::compare(&tune(&[("crankingRPM", 600.0)]), &tune(&[("crankingRPM", 600.0)])).is_empty());
}

#[test]
fn one_setting_moved_is_named_with_both_values() {
    let differences = tune_compare::compare(&tune(&[("crankingRPM", 400.0)]), &tune(&[("crankingRPM", 300.0)]));
    assert_eq!(differences.len(), 1);
    let difference = &differences[0];

    assert_eq!(difference.name, "crankingRPM");
    assert_eq!(difference.mine, Some(400.0));
    assert_eq!(difference.theirs, Some(300.0));
    assert!(difference.summary().contains("400 rpm"));
}

#[test]
fn a_table_says_how_many_of_its_cells_differ() {
    // "3 of 256" and "all 256" are different situations, and the first values alone do not
    // tell them apart.
    let layout = layout();
    let ve = constant(&layout, "veTable");

    let mut mine = EcuTune::from_pages(layout.clone(), vec![vec![0u8; 64]]);
    let mut theirs = EcuTune::from_pages(layout, vec![vec![0u8; 64]]);

    for i in 0..4 {
        mine.poke_into_own(&ve, i, 40.0);
    }
    for i in 0..4 {
        theirs.poke_into_own(&ve, i, if i == 2 { 45.0 } else { 40.0 });
    }

    let differences = tune_compare::compare(&mine, &theirs);
    assert_eq!(differences.len(), 1);
    let difference = &differences[0];

    assert_eq!(difference.cells, 1);
    assert!(difference.is_array());
    assert!(difference.summary().contains("1 of 4 cells"));
}

#[test]
fn a_bit_field_is_compared_by_its_option_names() {
    let differences = tune_compare::compare(&tune(&[("fanOn", 1.0)]), &tune(&[("fanOn", 0.0)]));
    assert_eq!(differences.len(), 1);

    assert_eq!(differences[0].mine_shown(), "Yes");
    assert_eq!(differences[0].theirs_shown(), "No");
}

// ----- a scale the firmware wrote as an expression ------------------------

#[test]
fn a_scale_written_as_an_expression_is_worked_out_from_the_tune() {
    // MS2 scales its MAF curve {0.01 * (maf_range + 1)}, because the same bytes cover a
    // 650 g/s sensor and a 2,600 g/s one and which it is is a setting three pages away. Read
    // with the fallback of 1 the curve is out by a factor of a hundred, and looks like an
    // ordinary number the whole way. Found by comparing a saved tune against the MicroSquirt
    // it came off, where 63 of 64 cells disagreed.
    let layout = layout();
    let flow = constant(&layout, "mafFlow");

    assert_eq!(flow.scale_expression, "{0.01 * (mafRange + 1)}");
    assert_eq!(flow.scale, 1.0); // the fallback, until there is a tune

    // Set before the tune is built, because the sum is done once when it is: the scale is a
    // property of the tune as read, and a setting changed afterwards does not rescale a table
    // under the person editing it.
    let mut page = vec![0u8; 64];
    page[7] = 1;

    let tune = EcuTune::from_pages(layout, vec![page]);

    assert_digits(0.02, tune.constant("mafFlow").unwrap().scale, 6); // 0.01 x (1 + 1)
}

#[test]
fn an_expression_resting_on_the_same_bytes_is_left_as_declared() {
    // MS2 shows its injector test period a second way, as an "approximate equivalent RPM"
    // whose translate is worked out from the very bytes it reads. Resolving that reads
    // correctly and then writes nonsense: storing a value through it moves the bytes, which
    // moves the constant the translate came from, which moves the translate; and the tune
    // stops surviving a round trip.
    let layout = layout();
    let period = constant(&layout, "period");
    let mut tune = EcuTune::from_pages(layout, vec![vec![0u8; 64]]);

    tune.poke_into_own(&period, 0, 100.0);

    assert_eq!(tune.constant("equivRpm").unwrap().transform, 0.0);
}

#[test]
fn a_tune_with_an_expression_scale_still_survives_a_write_and_a_read_back() {
    let layout = layout();
    let mut before = EcuTune::from_pages(layout.clone(), vec![vec![0u8; 64]]);

    let flow = before.constant("mafFlow").unwrap().clone();
    before.poke_into_own(&flow, 0, 1.5);
    let flow = before.constant("mafFlow").unwrap().clone();
    before.poke_into_own(&flow, 1, 300.0);

    let after = msq_apply::load(&layout, &MsqFile::read(&write(&before, "test firmware")).unwrap(), None);

    assert_eq!(before.pages()[0], after.tune.pages()[0]);
    assert_eq!(after.tune.array("mafFlow").unwrap(), vec![1.5, 300.0]);
}
