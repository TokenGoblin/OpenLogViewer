//! Ported from `TuneRestoreTests.cs`: working out what restoring a saved tune to a controller
//! would do.
//!
//! The largest thing this application can do to an engine, so the whole design is that it is
//! worked out and described before any of it happens.

use olv_core::definitions::msq_file::MsqFile;
use olv_core::ini::tune_layout::{self, TuneLayout};
use olv_core::tune::ecu_tune::EcuTune;
use olv_core::tune::msq_apply;
use olv_core::tune::tune_compare;
use olv_core::tune::tune_restore;

const INI: &str = r#"[Constants]
page = 1
nPages = 2
pageSize = 32, 32
pageIdentifier = "\x01", "\x02"
pageReadCommand = "r%2o%2c", "r%2o%2c"
pageValueWrite  = "w%2o%2c%v", "w%2o%2c%v"
burnCommand     = "b%2i", "b%2i"
   crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
   revLimit    = scalar, U16, 2, "rpm", 1, 0, 0, 10000, 0
   fanOn       = bits,   U08, 4, [0:0], "No", "Yes"

page = 2
   idleTarget  = scalar, U16, 0, "rpm", 1, 0, 0, 3000, 0
   reserved    = scalar, U08, 4, "", 1, 0, 0, 255, 0
"#;

fn layout() -> TuneLayout {
    tune_layout::read(INI, None)
}

fn tune(settings: &[(&str, f64)]) -> EcuTune {
    let mut tune = EcuTune::from_pages(layout(), vec![vec![0u8; 32], vec![0u8; 32]]);

    for (name, value) in settings {
        let constant = tune.constant(name).unwrap().clone();
        assert!(tune.poke_into_own(&constant, 0, *value), "{name}");
    }

    tune
}

/// A saved tune stating exactly the settings named.
fn file(signature: &str, settings: &[(&str, &str)]) -> MsqFile {
    let body = settings
        .iter()
        .map(|(n, v)| format!("<constant name=\"{n}\">{v}</constant>"))
        .collect::<Vec<_>>()
        .join("\n");

    MsqFile::read(&format!(
        r#"<msq xmlns="http://www.msefi.com/:msq">
<versionInfo fileFormat="5.0" nPages="2" signature="{signature}"/>
<page number="0" size="32">
{body}
</page>
</msq>"#
    ))
    .unwrap()
}

// ----- what it would do ---------------------------------------------------

#[test]
fn restoring_what_the_ecu_already_holds_does_nothing() {
    // The property everything else rests on. If this wrote anything, every restore would be
    // writing bytes nobody asked it to.
    let ecu = tune(&[("crankingRPM", 300.0), ("revLimit", 6500.0), ("idleTarget", 900.0)]);

    let plan = tune_restore::plan(
        &ecu,
        &file(
            "firmware",
            &[
                ("crankingRPM", "300.0"),
                ("revLimit", "6500.0"),
                ("fanOn", "\"No\""),
                ("idleTarget", "900.0"),
                ("reserved", "0.0"),
            ],
        ),
        "",
    );

    assert!(plan.is_empty());
    assert!(plan.writes.is_empty());
    assert!(plan.differences.is_empty());
    assert!(plan.summary().contains("already holds this tune"));
}

#[test]
fn only_the_settings_that_differ_are_written() {
    let ecu = tune(&[("crankingRPM", 300.0), ("revLimit", 6500.0), ("idleTarget", 900.0)]);

    let plan = tune_restore::plan(
        &ecu,
        &file(
            "firmware",
            &[
                ("crankingRPM", "300.0"),
                ("revLimit", "7000.0"),
                ("fanOn", "\"No\""),
                ("idleTarget", "900.0"),
                ("reserved", "0.0"),
            ],
        ),
        "",
    );

    assert_eq!(plan.differences.len(), 1);
    let difference = &plan.differences[0];
    assert_eq!(difference.name, "revLimit");
    assert_eq!(difference.mine, Some(7000.0));
    assert_eq!(difference.theirs, Some(6500.0));

    assert_eq!(plan.writes.len(), 1);
    let write = &plan.writes[0];
    assert_eq!(write.page, 0);
    assert_eq!(write.offset, 2);
    assert_eq!(write.data.len(), 2);
}

#[test]
fn a_setting_the_file_never_mentioned_is_left_as_the_ecu_has_it() {
    // The difference between a restore and a wrecked tune. A tune from a neighbouring revision
    // is missing a handful of constants, and writing zeros over them because the file was
    // silent is not restoring anything.
    let ecu = tune(&[("crankingRPM", 300.0), ("revLimit", 6500.0), ("idleTarget", 900.0)]);

    let plan = tune_restore::plan(&ecu, &file("firmware", &[("crankingRPM", "400.0")]), "");

    assert!(plan.missing.iter().any(|m| m.name == "revLimit"));
    assert!(plan.missing.iter().any(|m| m.name == "idleTarget"));

    // Only the one it named.
    assert_eq!(plan.differences.len(), 1);
    assert_eq!(plan.target.scalar("revLimit"), 6500.0);
    assert_eq!(plan.target.scalar("idleTarget"), 900.0);
}

#[test]
fn applying_the_writes_lands_exactly_on_what_was_planned() {
    let layout = layout();
    let ecu = tune(&[("crankingRPM", 300.0), ("revLimit", 6500.0), ("idleTarget", 900.0)]);

    let plan = tune_restore::plan(
        &ecu,
        &file(
            "firmware",
            &[
                ("crankingRPM", "450.0"),
                ("revLimit", "7000.0"),
                ("fanOn", "\"Yes\""),
                ("idleTarget", "1100.0"),
                ("reserved", "0.0"),
            ],
        ),
        "",
    );

    let mut copy = EcuTune::from_pages(layout, ecu.pages().to_vec());
    for write in &plan.writes {
        copy.accept(write);
    }

    assert_eq!(plan.target.pages()[0], copy.pages()[0]);
    assert_eq!(plan.target.pages()[1], copy.pages()[1]);
    assert!(tune_compare::compare(&copy, &plan.target).is_empty());
}

#[test]
fn every_page_that_would_be_touched_is_named() {
    let ecu = tune(&[("crankingRPM", 300.0), ("idleTarget", 900.0)]);

    let plan = tune_restore::plan(
        &ecu,
        &file("firmware", &[("crankingRPM", "400.0"), ("idleTarget", "1000.0")]),
        "",
    );

    assert_eq!(plan.pages(), vec![0, 1]);

    // Two bytes, not four. Both settings are sixteen bits and both changes are small enough to
    // leave the high byte alone (300 to 400 rpm is 0x012C to 0x0190) so only the low byte of
    // each is in a run. Writing what differs rather than what was named is the whole point.
    assert_eq!(plan.bytes(), 2);
}

// ----- which firmware -----------------------------------------------------

#[test]
fn a_file_and_a_controller_naming_different_firmwares_is_flagged() {
    let ecu = tune(&[("crankingRPM", 300.0)]);

    let plan = tune_restore::plan(
        &ecu,
        &file("some other firmware", &[("crankingRPM", "400.0")]),
        "firmware",
    );

    assert!(!plan.signatures_agree());
    assert_eq!(plan.file_signature, "some other firmware");
    assert_eq!(plan.ecu_signature, "firmware");
}

#[test]
fn the_same_firmware_both_sides_agrees() {
    let ecu = tune(&[("crankingRPM", 300.0)]);

    assert!(tune_restore::plan(&ecu, &file("firmware", &[("crankingRPM", "400.0")]), "firmware").signatures_agree());
}

#[test]
fn nothing_to_compare_counts_as_agreement() {
    // A file that names no firmware, or a controller that was never asked. Not a mismatch to
    // warn about; simply nothing said either way.
    let ecu = tune(&[("crankingRPM", 300.0)]);

    assert!(tune_restore::plan(&ecu, &file("", &[("crankingRPM", "400.0")]), "firmware").signatures_agree());
    assert!(tune_restore::plan(&ecu, &file("firmware", &[("crankingRPM", "400.0")]), "").signatures_agree());
}

// ----- what it says -------------------------------------------------------

#[test]
fn the_summary_says_how_much_would_change_and_how_much_would_not() {
    let ecu = tune(&[("crankingRPM", 300.0), ("revLimit", 6500.0), ("idleTarget", 900.0)]);

    let plan = tune_restore::plan(&ecu, &file("firmware", &[("crankingRPM", "400.0")]), "");

    assert!(plan.summary().contains("1 setting would change"));
    assert!(plan.summary().contains("not in the file"));
}

// ----- padding a controller chose for itself ------------------------------

/// An INI with a text field, which is where a firmware's own padding lives.
const TEXT_INI: &str = r#"[Constants]
page = 1
nPages = 1
pageSize = 32
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
pageValueWrite  = "w%2o%2c%v"
burnCommand     = "b%2i"
   label = string, ASCII, 0, 16
   revLimit = scalar, U16, 16, "rpm", 1, 0, 0, 10000, 0
"#;

/// A tune whose text field is padded the way a controller pads it.
fn padded(text: &str, padding: &[u8]) -> EcuTune {
    let layout = tune_layout::read(TEXT_INI, None);
    let mut page = vec![0u8; 32];

    for (i, b) in text.bytes().enumerate() {
        page[i] = b;
    }
    for (i, b) in padding.iter().enumerate() {
        page[text.len() + i] = *b;
    }

    EcuTune::from_pages(layout, vec![page])
}

fn text_file(label: &str) -> MsqFile {
    MsqFile::read(&format!(
        r#"<msq xmlns="http://www.msefi.com/:msq">
<versionInfo fileFormat="5.0" nPages="1" signature="firmware"/>
<page number="0" size="32">
<constant name="label">"{label}"</constant>
<constant name="revLimit">0.0</constant>
</page>
</msq>"#
    ))
    .unwrap()
}

#[test]
fn restoring_a_tune_to_the_controller_it_came_from_would_send_nothing() {
    // Found on a rusEFI, whose 8000-byte Lua script field ends with the newlines the script
    // was written with. Reading a text field trims its padding and writing one pads with
    // nulls, so the tune saved off the controller came back differing from it by exactly those
    // two bytes, and the plan plainly contradicted itself: "0 settings would change, 2 bytes
    // across 1 page".
    let ecu = padded("print('hi')", b"\n\n");

    let plan = tune_restore::plan(&ecu, &text_file("print('hi')"), "firmware");

    assert!(plan.differences.is_empty());
    assert!(plan.is_empty(), "{}", plan.summary());
    assert_eq!(plan.bytes(), 0);
}

/// Trimming is what makes any of these vanish on the way out, so all of them come back as a
/// phantom write unless the bytes are left alone.
fn whichever_character_a_controller_pads_with(pad: u8) {
    let ecu = padded("name", &[pad, pad, pad]);

    assert!(tune_restore::plan(&ecu, &text_file("name"), "firmware").is_empty());
}

#[test]
fn whichever_character_a_controller_pads_with_space() {
    whichever_character_a_controller_pads_with(b' ');
}

#[test]
fn whichever_character_a_controller_pads_with_line_feed() {
    whichever_character_a_controller_pads_with(b'\n');
}

#[test]
fn whichever_character_a_controller_pads_with_carriage_return() {
    whichever_character_a_controller_pads_with(b'\r');
}

#[test]
fn whichever_character_a_controller_pads_with_tab() {
    whichever_character_a_controller_pads_with(b'\t');
}

#[test]
fn but_a_genuinely_different_name_still_gets_written() {
    // The fix must not turn into "text is never restored".
    let ecu = padded("old name", b"\n");

    let plan = tune_restore::plan(&ecu, &text_file("new name"), "firmware");

    assert!(!plan.is_empty());
    assert_eq!(plan.differences.len(), 1);
    assert_eq!(plan.differences[0].name, "label");
}

#[test]
fn and_the_field_still_ends_up_holding_what_the_file_said() {
    let ecu = padded("old name", b"\n");

    let plan = tune_restore::plan(&ecu, &text_file("new name"), "firmware");

    assert_eq!(
        plan.target.text_in(plan.target.pages(), "label").as_deref(),
        Some("new name")
    );
}

// ----- saying which name changed ------------------------------------------

#[test]
fn a_text_difference_names_both_strings_rather_than_counting_cells() {
    // It used to read "label: 1 of 32 cells differ, first - against -", because the strings
    // were dropped and the field's width was taken for a number of values. A name is the one
    // setting whose value a person recognises on sight; it should say which name.
    let ecu = padded("old name", b"\n");

    let plan = tune_restore::plan(&ecu, &text_file("new name"), "firmware");
    assert_eq!(plan.differences.len(), 1);
    let d = &plan.differences[0];

    assert!(!d.is_array());
    assert_eq!(d.mine_shown(), "\"new name\"");
    assert_eq!(d.theirs_shown(), "\"old name\"");
    assert_eq!(d.summary(), "label: \"new name\" against \"old name\"");
}

#[test]
fn an_emptied_name_says_so_rather_than_showing_nothing() {
    let ecu = padded("was here", b"");

    let plan = tune_restore::plan(&ecu, &text_file(""), "firmware");
    assert_eq!(plan.differences.len(), 1);
    let d = &plan.differences[0];

    assert_eq!(d.mine_shown(), "(blank)");
    assert_eq!(d.theirs_shown(), "\"was here\"");
}

// ----- a rejected setting must leave nothing behind ------------------------

const ARRAY_INI: &str = r#"[Constants]
page = 1
nPages = 1
pageSize = 16
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
pageChunkWrite  = "w%2o%2c%v"
burnCommand     = "b%2i"
   veTable  = array, U08, 0, [4], "%", 1, 0, 0, 255, 0
   revLimit = scalar, U16, 8, "rpm", 1, 0, 0, 10000, 0
"#;

const REFUSED_FILE: &str = r#"<msq xmlns="http://www.msefi.com/:msq">
<versionInfo fileFormat="5.0" nPages="1" signature="firmware"/>
<page number="0" size="16">
<constant name="veTable">80.0 81.0 82.0 900.0</constant>
<constant name="revLimit">6500.0</constant>
</page>
</msq>"#;

#[test]
fn a_table_the_file_cannot_store_leaves_every_cell_as_it_was() {
    // Rejected means "not stored", and a restore trusts that when it decides those bytes need
    // no write. Written cell by cell, a value that will not fit part way through left the
    // cells before it already changed, so the plan reported the setting untouched and would
    // have sent half the file's table and half the controller's to a running engine.
    let layout = tune_layout::read(ARRAY_INI, None);
    let mut ecu = EcuTune::from_pages(layout.clone(), vec![vec![0u8; 16]]);

    let ve = ecu.constant("veTable").unwrap().clone();
    for i in 0..4 {
        assert!(ecu.poke_into_own(&ve, i, 50.0 + f64::from(i)));
    }

    // The last cell is past what a U08 holds, so the constant is refused, after the first
    // three have already been written.
    let file = MsqFile::read(REFUSED_FILE).unwrap();

    let loaded = msq_apply::load(&layout, &file, Some(&ecu));

    assert!(loaded.rejected.iter().any(|c| c.name == "veTable"));

    // Every cell still holds what the controller held.
    let cells = loaded.tune.array("veTable").unwrap_or_default();
    assert_eq!(cells, vec![50.0, 51.0, 52.0, 53.0]);
}

#[test]
fn and_the_restore_therefore_plans_no_write_for_it() {
    let layout = tune_layout::read(ARRAY_INI, None);
    let mut ecu = EcuTune::from_pages(layout, vec![vec![0u8; 16]]);

    let ve = ecu.constant("veTable").unwrap().clone();
    for i in 0..4 {
        ecu.poke_into_own(&ve, i, 50.0 + f64::from(i));
    }
    let rev = ecu.constant("revLimit").unwrap().clone();
    ecu.poke_into_own(&rev, 0, 6500.0);

    let file = MsqFile::read(REFUSED_FILE).unwrap();

    let plan = tune_restore::plan(&ecu, &file, "firmware");

    // Nothing at all: the one setting that could be stored already matches, and the one that
    // could not was put back.
    assert!(plan.is_empty(), "{}", plan.summary());
    assert_eq!(plan.bytes(), 0);
}

// ----- a scale written as an expression -----------------------------------

/// A firmware whose curve is scaled by another setting, which is the case the whole rescaling
/// mechanism exists for.
const EXPRESSION_INI: &str = r#"[Constants]
page = 1
nPages = 1
pageSize = 16
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
pageChunkWrite  = "w%2o%2c%v"
burnCommand     = "b%2i"
   maf_range = scalar, U08, 0, "", 1, 0, 0, 7, 0
   mafFlow   = array,  U08, 4, [4], "g/s", { 0.01 * (maf_range + 1) }, 0, 0, 100, 2
"#;

#[test]
fn a_write_that_moves_a_scale_reworks_the_scales_that_depend_on_it() {
    // The scale is worked out when the tune is built, and a write is exactly what moves the
    // setting it is written against. Left alone, the curve goes on dividing by the old scale,
    // and the next cell edited is *encoded* through it and sent to a running engine.
    let layout = tune_layout::read(EXPRESSION_INI, None);
    let mut tune = EcuTune::from_pages(layout, vec![vec![0u8; 16]]);

    // maf_range 0, so the scale is 0.01: a raw 100 reads as 1.00.
    let flow = tune.constant("mafFlow").unwrap().clone();
    tune.poke_into_own(&flow, 0, 1.0);
    assert_close(tune.array("mafFlow").unwrap()[0], 1.0, 6);

    // Now move maf_range to 3, the way a settings write would.
    let write = tune.encode_array("maf_range", &[3.0]);
    assert!(write.is_some());
    tune.accept(&write.unwrap());

    // The scale is now 0.04, so the same raw byte means four times as much.
    assert_close(tune.array("mafFlow").unwrap()[0], 4.0, 6);
}

#[test]
fn and_a_firmware_with_no_expression_scales_is_left_alone() {
    // The rework is not free, and every write goes through here.
    let layout = tune_layout::read(ARRAY_INI, None);
    let mut tune = EcuTune::from_pages(layout, vec![vec![0u8; 16]]);

    let ve = tune.constant("veTable").unwrap().clone();
    tune.poke_into_own(&ve, 0, 50.0);
    let write = tune.encode_array("revLimit", &[6500.0]).unwrap();
    tune.accept(&write);

    assert_close(tune.array("veTable").unwrap()[0], 50.0, 6);
    assert_close(tune.scalar("revLimit"), 6500.0, 6);
}

/// xunit's `Assert.Equal(expected, actual, precision)`: both rounded to that many decimals.
fn assert_close(actual: f64, expected: f64, digits: i32) {
    let p = 10f64.powi(digits);
    let r = |v: f64| (v * p).round_ties_even() / p;
    assert_eq!(r(expected), r(actual), "{expected} vs {actual}");
}

#[test]
fn a_name_too_long_for_the_field_is_reported_rather_than_quietly_shortened() {
    // The complaint MsqApply already had for this was unreachable, because PokeTextInto
    // truncated and said it had worked. A tune from a build with a wider field therefore
    // counted the name applied while putting a different name on the controller.
    let layout = tune_layout::read(TEXT_INI, None);
    let ecu = EcuTune::from_pages(layout.clone(), vec![vec![0u8; 32]]);

    let loaded = msq_apply::load(&layout, &text_file("a name far too long for sixteen"), Some(&ecu));

    let refused: Vec<_> = loaded.rejected.iter().filter(|c| c.name == "label").collect();
    assert_eq!(refused.len(), 1);
    assert!(refused[0].reason.to_lowercase().contains("does not fit"));
}

#[test]
fn and_the_restore_plans_no_write_for_it() {
    // Which is the point: rejected has to mean the bytes were left alone, or the plan and the
    // bytes disagree and the plan is what gets sent.
    let ecu = padded("keep me", b"\n");

    let plan = tune_restore::plan(&ecu, &text_file("a name far too long for sixteen"), "firmware");

    assert!(plan.is_empty(), "{}", plan.summary());
    assert_eq!(
        plan.target.text_in(plan.target.pages(), "label").as_deref(),
        Some("keep me")
    );
}
