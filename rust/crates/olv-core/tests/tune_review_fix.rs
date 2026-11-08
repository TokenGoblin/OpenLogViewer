//! Ported from `ReviewFixTests.cs`: the awkward cases a review found, each of which passed
//! everything else in the suite while being wrong.

use olv_core::analysis::ve_analysis::{self, VeAnalysisSettings};
use olv_core::definitions::msq_file::MsqFile;
use olv_core::ini::ini_defines;
use olv_core::ini::tune_layout;
use olv_core::model::{Array2, LogChannel, TuneAxis, TuneTable};
use olv_core::tune::ecu_tune::EcuTune;
use olv_core::tune::tune_settings_edit::TuneSettingsEdit;
use olv_core::tune::{msq_apply, msq_writer, tune_restore};

fn assert_places(expected: f64, actual: f64, places: i32) {
    let scale = 10f64.powi(places);
    assert_eq!(
        (expected * scale).round() / scale,
        (actual * scale).round() / scale,
        "{expected} vs {actual} to {places} places"
    );
}

// ----- a scale worked out from a tune that is not filled in yet -----------

const SCALED: &str = r#"[Constants]
page = 1
nPages = 1
pageSize = 64
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
   maf_range = bits,  U08, 0, [0:1], "650", "1300", "1950", "2600"
   mafflow   = array, U16, 2, [2], "g/sec", {0.01 * (maf_range + 1)}, 0, 0, 3000, 2
"#;

#[test]
fn a_scale_written_in_terms_of_a_setting_is_worked_out_after_that_setting_arrives() {
    // The sum is done when a tune is built, and a tune being filled in from
    // a file is empty at that moment — so the range read as nought, the
    // scale came out four times too small, and the file's 1000 g/s no longer
    // fitted the field and was refused outright. The identical file laid
    // over a controller loaded perfectly, which is what made it hard to see.
    let layout = tune_layout::read(SCALED, None);

    let mut page = vec![0u8; 64];
    page[0] = 3; // the 2600 g/s sensor, so scale 0.04
    page[2] = 0x61;
    page[3] = 0xA8; // raw 25,000 = 1000.0 g/sec
    page[4] = 0xC3;
    page[5] = 0x50; // raw 50,000 = 2000.0 g/sec

    let real = EcuTune::from_pages(layout.clone(), vec![page]);
    assert_places(0.04, real.constant("mafflow").unwrap().scale, 6);

    let file = MsqFile::read(&msq_writer::write(&real, "firmware", None, "", None)).unwrap();

    for onto in [Some(&real), None] {
        let load = msq_apply::load(&layout, &file, onto);

        assert!(load.rejected.is_empty());
        assert_places(0.04, load.tune.constant("mafflow").unwrap().scale, 6);
        assert_eq!(load.tune.array("mafflow").unwrap(), vec![1000.0, 2000.0]);
    }
}

// ----- what a settings edit records --------------------------------------

const TENTHS: &str = r#"[Constants]
page = 1
nPages = 1
pageSize = 16
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
   advance = scalar, U08, 0, "deg", 0.1, 0, 0, 25, 1
"#;

#[test]
fn an_edit_records_what_was_stored_rather_than_what_was_asked() {
    // A byte holding tenths cannot hold 1.24, so a confirmation list showing
    // "0 -> 1.24" names a number that will never reach the ECU.
    let layout = tune_layout::read(TENTHS, None);
    let tune = EcuTune::from_pages(layout, vec![vec![0u8; 16]]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "advance", 1.24, 0));

    let changes = edit.changes();
    assert_eq!(changes.len(), 1);
    assert_places(1.2, changes[0].now, 6);
    assert_places(1.2, edit.value(&tune, "advance", 0), 6);
}

#[test]
fn an_edit_that_moves_no_byte_is_not_a_pending_change() {
    // 5.44 and 5.4 are the same byte. Counting the first as a change lights
    // the Send button over a write that turns out to be empty — the phantom
    // the text path goes out of its way to avoid.
    let layout = tune_layout::read(TENTHS, None);
    let mut tune = EcuTune::from_pages(layout, vec![vec![0u8; 16]]);
    let advance = tune.constant("advance").unwrap().clone();
    tune.poke_into_own(&advance, 0, 5.4);

    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "advance", 5.44, 0));

    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
}

// ----- option lists with a comment after them ----------------------------

#[test]
fn a_comment_after_a_list_does_not_swallow_a_reference_or_add_labels() {
    // The comment came off after the split rather than before it, so "$pins"
    // followed by a comment failed the identifier test and stayed a literal,
    // and any comma inside the comment became further labels. Either
    // renumbers every option after it.
    let defines = ini_defines::read(
        r#"#define pins = "Off", "Out A"
#define allPins = "INVALID", $pins ; every pin, as we know them
"#,
        None,
    );

    assert_eq!(
        defines.get("allPins").unwrap(),
        &vec!["INVALID".to_string(), "Off".to_string(), "Out A".to_string()]
    );
}

// ----- what a backup says about what it could not read -------------------

#[test]
fn a_value_that_cannot_be_read_is_left_out_rather_than_written_as_zero() {
    // A float in erased flash reads as NaN. Writing "0.0" records a number
    // the ECU does not hold, and restoring that file puts a real zero over
    // it. Saying nothing is the truth, and a reader already knows to leave
    // alone anything a file does not mention.
    let floats = r#"[Constants]
page = 1
nPages = 1
pageSize = 16
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
   trim = scalar, F32, 0, "%", 1, 0
   idle = scalar, U08, 8, "rpm", 10, 0, 0, 2000, 0
"#;

    let layout = tune_layout::read(floats, None);

    let mut page = vec![0u8; 16];
    page[0..4].fill(0xFF); // erased flash: NaN
    page[8] = 90;

    let xml = msq_writer::write(
        &EcuTune::from_pages(layout.clone(), vec![page]),
        "firmware",
        None,
        "",
        None,
    );

    assert!(!xml.contains("\"trim\""));
    assert!(xml.contains("\"idle\""));

    // And reading it back reports the gap rather than inventing a value.
    let load = msq_apply::load(&layout, &MsqFile::read(&xml).unwrap(), None);
    assert!(load.missing.iter().any(|m| m.name == "trim"));
}

// ----- what a restore plan says ------------------------------------------

#[test]
fn a_plan_that_would_change_nothing_still_says_what_the_file_lacked() {
    // "The ECU already holds this tune, setting for setting" is the opposite
    // of the truth when the file carried nothing this firmware recognises.
    let layout = tune_layout::read(TENTHS, None);
    let ecu = EcuTune::from_pages(layout, vec![vec![0u8; 16]]);

    let plan = tune_restore::plan(
        &ecu,
        &MsqFile::read(
            r#"<msq xmlns="http://www.msefi.com/:msq">
<versionInfo nPages="1" signature="firmware"/>
<page number="0" size="16"/>
</msq>"#,
        )
        .unwrap(),
        "",
    );

    assert!(plan.is_empty());
    assert!(!plan.complete());
    assert!(!plan.summary().contains("already holds"));
    assert!(plan.summary().contains("not in the file"));
}

#[test]
fn a_plan_carries_what_could_not_be_stored_as_well_as_what_was_absent() {
    let layout = tune_layout::read(TENTHS, None);
    let ecu = EcuTune::from_pages(layout, vec![vec![0u8; 16]]);

    let plan = tune_restore::plan(
        &ecu,
        &MsqFile::read(
            r#"<msq xmlns="http://www.msefi.com/:msq">
<versionInfo nPages="1" signature="firmware"/>
<page number="0" size="16">
<constant name="advance">999.0</constant>
</page>
</msq>"#,
        )
        .unwrap(),
        "",
    );

    assert!(plan.rejected.iter().any(|r| r.name == "advance"));
    assert!(!plan.complete());
    assert!(plan.summary().contains("could not be stored"));
}

// ----- how far out, against how much to move -----------------------------

#[test]
fn a_log_with_no_table_reports_the_error_itself_not_a_share_of_it() {
    // With no table there is nothing to suggest a change to, so what comes
    // back is the measurement. Scaling it by an authority that applies to no
    // table, and by a confidence the caller is handed separately, would tell
    // a tuner a cell running six per cent lean was three.
    // A table of zeros, which is the "no current fuelling" shape: a log
    // from a controller whose tune cannot be read.
    let empty = TuneTable {
        name: "VE table 1".to_string(),
        x: TuneAxis {
            constant: "rpm".to_string(),
            units: "RPM".to_string(),
            breakpoints: vec![1000.0, 3000.0],
        },
        y: TuneAxis {
            constant: "map".to_string(),
            units: "kPa".to_string(),
            breakpoints: vec![40.0, 80.0],
        },
        values: Array2::new(2, 2),
        units: "%".to_string(),
    };

    // Twelve samples of a steady six per cent lean, and a confidence figure
    // set to the same twelve — so the weight is exactly a half, which is
    // what used to halve the number reported.
    let afr = vec![15.582; 12];
    let target = vec![14.7; 12];

    let result = ve_analysis::analyse(
        &empty,
        &LogChannel::new("RPM", "", 2, &[1000.0; 12], false),
        &LogChannel::new("MAP", "", 2, &[40.0; 12], false),
        &LogChannel::new("AFR", "", 2, &afr, false),
        &LogChannel::new("Target", "", 2, &target, false),
        0,
        11,
        None,
        Some(&VeAnalysisSettings {
            authority: 0.5,
            confidence_samples: 12,
            minimum_samples: 4,
            ..VeAnalysisSettings::default()
        }),
    )
    .unwrap();

    assert_places(6.0, result.change_percent[(0, 0)].unwrap(), 1);
}
