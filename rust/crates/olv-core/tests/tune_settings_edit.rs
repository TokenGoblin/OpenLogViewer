//! The C# `TuneSettingsEditTests`: editing the settings that are not tables.
//!
//! The tests that matter are about bit fields sharing a byte. Getting that wrong does not fail:
//! the ECU takes the byte, the read-back matches what was sent, and two options nobody touched
//! have quietly changed.
//!
//! The C# edit holds the tune and reads its pages afresh; here the tune is passed to each call.

use olv_core::ini::msq_ini::RealtimeType;
use olv_core::ini::tune_layout::{self, TuneConstant, TuneLayout, TunePage};
use olv_core::model::IgnoreCaseMap;
use olv_core::tune::ecu_tune::{EcuTune, TuneWrite};
use olv_core::tune::tune_settings_edit::{SettingChange, TuneSettingsEdit};

fn constant(name: &str, offset: i32, type_: RealtimeType) -> TuneConstant {
    TuneConstant::new(name, 0, offset, type_)
}

fn bit(name: &str, low: i32, high: i32) -> TuneConstant {
    let mut c = constant(name, 2, RealtimeType::U08);
    c.bit_low = low;
    c.bit_high = high;
    c
}

/// A firmware with one page: a scalar, four options packed into one byte, a wider bit field, an
/// array and a name.
fn layout() -> TuneLayout {
    let mut cranking = constant("crankingRPM", 0, RealtimeType::U16);
    cranking.scale = 1.0;
    cranking.low = 0.0;
    cranking.high = 10000.0;

    let mut dwell = constant("dwell", 4, RealtimeType::U08);
    dwell.scale = 0.1;
    dwell.low = 0.0;
    dwell.high = 12.0;

    let mut trims = constant("trims", 6, RealtimeType::U08);
    trims.columns = 4;
    trims.scale = 1.0;
    trims.low = 0.0;
    trims.high = 255.0;

    let mut alias = constant("alias", 12, RealtimeType::U08);
    alias.is_text = true;
    alias.columns = 8;

    TuneLayout {
        pages: vec![TunePage {
            index: 0,
            size: 32,
            identifier: String::new(),
            read_command: String::new(),
            chunk_write_command: String::new(),
            burn_command: String::new(),
        }],
        constants: vec![
            cranking,
            // Four options in the byte at offset 2.
            bit("optA", 0, 0),
            bit("optB", 1, 1),
            bit("optC", 2, 2),
            bit("mode", 4, 6),
            dwell,
            trims,
            alias,
        ],
        pc_variables: Vec::new(),
        derived: IgnoreCaseMap::new(),
        little_endian: false,
        blocking_factor: 0,
        inter_write_delay: 0,
        after_burn_delay: 0,
    }
}

fn tune(bytes: &[(usize, u8)]) -> EcuTune {
    let mut page = vec![0u8; 32];
    for &(at, value) in bytes {
        page[at] = value;
    }

    EcuTune::from_pages(layout(), vec![page])
}

fn near(a: f64, b: f64) {
    assert!((a - b).abs() < 5e-7, "{a} is not {b}");
}

// ----- scalars ----------------------------------------------------------

#[test]
fn a_setting_reads_back_as_what_it_was_set_to() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "crankingRPM", 350.0, 0));
    assert_eq!(edit.value(&tune, "crankingRPM", 0), 350.0);
    assert!(edit.has_changes());
    assert_eq!(edit.changed_count(), 1);
}

#[test]
fn scale_and_transform_are_applied() {
    // Dwell is stored in tenths, so 4.5 ms is 45 in the byte.
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "dwell", 4.5, 0));
    near(edit.value(&tune, "dwell", 0), 4.5);

    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0].data[0], 45);
}

#[test]
fn a_value_outside_what_the_firmware_declares_is_refused() {
    // Far tighter than the storage allows, which is the point: a byte would take 255 quite
    // happily.
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set(&tune, "dwell", 99.0, 0));
    assert!(!edit.set(&tune, "dwell", -1.0, 0));
    assert!(!edit.has_changes());
}

#[test]
fn an_out_of_range_original_can_still_be_written_back_to_itself() {
    // A byte holding 255, scaled by 0.1, reads as 25.5 - the firmware's own value, sitting
    // outside the declared 0-12 range. A real ECU does this: a "disabled" sentinel that
    // predates any edit, not something a write produced. The range guard exists to stop a new
    // out-of-range write, not to make an already out-of-range setting permanently unwritable.
    let tune = tune(&[(4, 255)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    near(edit.original(&tune, "dwell", 0), 25.5);

    // Moving away from the sentinel and back again must not get stuck: the firmware's own value
    // has to stay writable even once the working copy no longer holds it.
    assert!(edit.set(&tune, "dwell", 6.0, 0));
    assert!(edit.set(&tune, "dwell", 25.5, 0));
    near(edit.value(&tune, "dwell", 0), 25.5);
    assert!(!edit.has_changes());

    // A different out-of-range value - not the one the firmware itself reported - is still
    // refused.
    assert!(!edit.set(&tune, "dwell", 99.0, 0));
}

#[test]
fn a_value_that_is_not_a_number_is_refused() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set(&tune, "crankingRPM", f64::NAN, 0));
    assert!(!edit.set(&tune, "crankingRPM", f64::INFINITY, 0));
}

#[test]
fn a_setting_this_firmware_does_not_have_is_refused() {
    let tune = tune(&[]);
    assert!(!TuneSettingsEdit::new(&tune).set(&tune, "noSuchThing", 1.0, 0));
}

// ----- bit fields, which is where the damage would be -------------------

#[test]
fn changing_one_option_leaves_the_others_in_its_byte_alone() {
    // All four set: 0b0111_0111. Turning optB off must clear exactly one bit.
    let tune = tune(&[(2, 0b0111_0111)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "optB", 0.0, 0));

    assert_eq!(edit.value(&tune, "optA", 0), 1.0);
    assert_eq!(edit.value(&tune, "optB", 0), 0.0);
    assert_eq!(edit.value(&tune, "optC", 0), 1.0);
    assert_eq!(edit.value(&tune, "mode", 0), 7.0);

    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0].data[0], 0b0111_0101);
}

#[test]
fn two_options_in_one_byte_both_survive_being_changed() {
    // The failure this class exists to prevent. Encoded separately against the ECU's original
    // bytes, the second write would carry the first field's old value and undo it -- with no
    // error anywhere.
    let tune = tune(&[(2, 0)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "optA", 1.0, 0));
    assert!(edit.set(&tune, "optC", 1.0, 0));

    assert_eq!(edit.value(&tune, "optA", 0), 1.0);
    assert_eq!(edit.value(&tune, "optC", 0), 1.0);

    // And they leave as one write, because they are one byte.
    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0].data[0], 0b0000_0101);
}

#[test]
fn a_wide_bit_field_writes_only_its_own_bits() {
    // mode is bits 4..6. Setting it must not touch the options below it nor bit 7 above it.
    let tune = tune(&[(2, 0b1000_0111)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "mode", 5.0, 0));

    assert_eq!(edit.value(&tune, "mode", 0), 5.0);
    assert_eq!(edit.value(&tune, "optA", 0), 1.0);
    assert_eq!(edit.value(&tune, "optB", 0), 1.0);
    assert_eq!(edit.value(&tune, "optC", 0), 1.0);

    assert_eq!(edit.writes(&tune)[0].data[0], 0b1101_0111);
}

#[test]
fn a_value_too_wide_for_the_field_is_refused_rather_than_truncated() {
    // mode is three bits, so eight does not fit. Masking it would store nought and report
    // success -- the change list saying 8 while the bytes say 0 -- and a bit field carries no
    // declared range, so nothing else would have caught it. Started from a non-zero byte, since
    // a zero one hides a truncation that happens to land on zero.
    let tune = tune(&[(2, 0b0101_0111)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set(&tune, "mode", 8.0, 0));
    assert!(!edit.set(&tune, "mode", -1.0, 0));

    assert_eq!(edit.value(&tune, "mode", 0), 5.0);
    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
}

#[test]
fn an_element_past_the_end_of_an_array_is_refused() {
    // trims is four bytes at offset 6, and alias begins at 12. Without a bound on the element,
    // the seventh trim writes over the first letter of a name -- inside the page, so no other
    // check catches it.
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set(&tune, "trims", 5.0, 6));
    assert!(!edit.set(&tune, "trims", 5.0, -1));

    assert!(edit.writes(&tune).is_empty());
}

#[test]
fn a_scalar_has_only_the_one_element() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set(&tune, "dwell", 5.0, 1));
    assert!(edit.writes(&tune).is_empty());
}

// ----- arrays and text --------------------------------------------------

#[test]
fn one_element_of_an_array_can_be_changed_on_its_own() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "trims", 12.0, 2));

    assert_eq!(edit.value(&tune, "trims", 0), 0.0);
    assert_eq!(edit.value(&tune, "trims", 2), 12.0);

    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0].offset, 8); // offset 6, third element
    assert_eq!(writes[0].data[0], 12);
}

#[test]
fn a_text_setting_round_trips() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set_text(&tune, "alias", "Coolant"));
    assert_eq!(edit.text(&tune, "alias"), "Coolant");
    assert!(edit.has_changes());
}

#[test]
fn text_too_long_for_its_field_is_refused_rather_than_cut_short() {
    // This used to keep the first eight characters and report success, which is the same shape
    // of lie as an out-of-range number being stored: the caller counts it applied and something
    // else is on the controller. Restoring a tune from a build with a wider field reported the
    // name written and put a different name on the ECU, and a person typing past the end saw it
    // accepted and found it shortened later.
    //
    // The field is eight bytes.
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set_text(&tune, "alias", "MuchTooLongToFit"));

    // And nothing was written on the way to refusing, so no write is left waiting to carry half
    // a name to the ECU.
    assert!(edit.writes(&tune).is_empty());
    assert!(!edit.has_changes());
}

#[test]
fn but_a_name_filling_the_field_exactly_still_fits() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set_text(&tune, "alias", "MuchTooL"));
    assert_eq!(edit.text(&tune, "alias"), "MuchTooL");
    assert!(
        edit.writes(&tune)
            .iter()
            .all(|w| w.offset as usize + w.data.len() <= 20)
    );
}

#[test]
fn text_outside_ascii_is_refused_rather_than_mangled() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set_text(&tune, "alias", "K\u{fc}hlmittel"));

    // And nothing was written on the way to refusing. Rejecting part way through leaves the
    // field half overwritten while the caller is told nothing changed, and those bytes go to
    // the ECU on the next send.
    assert!(edit.writes(&tune).is_empty());
    assert_eq!(edit.bytes_to_write(&tune), 0);
    assert!(!edit.has_changes());
}

#[test]
fn writing_a_name_back_to_the_one_it_already_has_is_not_a_change() {
    // The padding has to match what the ECU stores, or an identical name still differs byte for
    // byte and a write is offered for nothing.
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set_text(&tune, "alias", ""));

    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
}

#[test]
fn a_text_field_is_padded_the_way_the_ecu_pads_it() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set_text(&tune, "alias", "CLT");

    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 1);
    let write = &writes[0];

    // Three characters and then nulls, not spaces.
    assert_eq!(write.data[0], b'C');
    assert!(write.data[3..].iter().all(|&b| b == 0));
}

#[test]
fn a_number_cannot_be_written_to_a_text_setting_nor_the_reverse() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set(&tune, "alias", 5.0, 0));
    assert!(!edit.set_text(&tune, "crankingRPM", "350"));
}

// ----- what would be sent -----------------------------------------------

#[test]
fn nothing_changed_means_nothing_to_send() {
    let tune = tune(&[(0, 1), (2, 0b0101)]);
    let edit = TuneSettingsEdit::new(&tune);

    assert!(edit.writes(&tune).is_empty());
    assert_eq!(edit.bytes_to_write(&tune), 0);
    assert!(!edit.has_changes());
}

#[test]
fn setting_a_value_back_to_what_it_was_is_not_a_change() {
    // Otherwise the header offers to send bytes identical to the ones the ECU already holds.
    let tune = tune(&[(4, 45)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "dwell", 6.0, 0);
    assert!(edit.has_changes());

    edit.set(&tune, "dwell", 4.5, 0);
    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
}

#[test]
fn neighbouring_changed_bytes_leave_as_one_write() {
    // A write costs a round trip, and four bytes cost no more than one.
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "trims", 1.0, 0);
    edit.set(&tune, "trims", 2.0, 1);
    edit.set(&tune, "trims", 3.0, 2);

    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0].offset, 6);
    assert_eq!(writes[0].data, vec![1, 2, 3]);
}

#[test]
fn separate_regions_leave_as_separate_writes() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "crankingRPM", 300.0, 0);
    edit.set(&tune, "trims", 9.0, 0);

    assert_eq!(edit.writes(&tune).len(), 2);
}

#[test]
fn the_original_is_the_ecus_value_not_the_edited_one() {
    // The two have to come from different places, or a was-and-now display shows the same
    // number twice for every row that changed.
    let tune = tune(&[(4, 45)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "dwell", 6.0, 0);

    near(edit.original(&tune, "dwell", 0), 4.5);
    near(edit.value(&tune, "dwell", 0), 6.0);
}

#[test]
fn changes_come_back_in_the_order_they_were_touched() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "dwell", 6.0, 0);
    edit.set(&tune, "crankingRPM", 300.0, 0);
    edit.set(&tune, "optA", 1.0, 0);

    let names: Vec<String> = edit.changes().into_iter().map(|c| c.name).collect();
    assert_eq!(names, ["dwell", "crankingRPM", "optA"]);
}

#[test]
fn the_change_list_says_what_was_and_what_would_be() {
    let tune = tune(&[(0, 0), (1, 100)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "crankingRPM", 250.0, 0);

    let changes = edit.changes();
    assert_eq!(changes.len(), 1);
    assert_eq!(
        changes[0],
        SettingChange {
            name: "crankingRPM".to_string(),
            was: 100.0,
            now: 250.0
        }
    );
}

// ----- putting it back --------------------------------------------------

#[test]
fn one_setting_can_be_put_back() {
    let tune = tune(&[(4, 45)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "dwell", 6.0, 0);
    edit.revert(&tune, "dwell", 0);

    near(edit.value(&tune, "dwell", 0), 4.5);
    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
}

#[test]
fn putting_one_option_back_leaves_its_neighbours_as_they_now_are() {
    // The revert restores that field from the ECU's bytes, not the whole byte -- a neighbour
    // edited since must keep its new value.
    let tune = tune(&[(2, 0)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "optA", 1.0, 0);
    edit.set(&tune, "optC", 1.0, 0);
    edit.revert(&tune, "optA", 0);

    assert_eq!(edit.value(&tune, "optA", 0), 0.0);
    assert_eq!(edit.value(&tune, "optC", 0), 1.0);
    assert_eq!(edit.changed_count(), 1);
}

#[test]
fn everything_can_be_put_back_at_once() {
    let tune = tune(&[(0, 1), (2, 0b0101), (4, 45)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "crankingRPM", 999.0, 0);
    edit.set(&tune, "optB", 1.0, 0);
    edit.set(&tune, "dwell", 2.0, 0);

    edit.revert_all(&tune);

    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
    near(edit.value(&tune, "dwell", 0), 4.5);
    assert_eq!(edit.value(&tune, "optA", 0), 1.0);
    assert_eq!(edit.value(&tune, "optB", 0), 0.0);
}

#[test]
fn a_pc_variable_is_not_something_to_write_to_the_ecu() {
    // It has no page, so writing one would land at offset zero of page zero.
    let mut layout = layout();
    layout.pc_variables = vec![TuneConstant::new("rpmhigh", -1, 0, RealtimeType::U16)];

    let tune = EcuTune::from_pages(layout, vec![vec![0u8; 32]]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(!edit.set(&tune, "rpmhigh", 8000.0, 0));
    assert!(edit.writes(&tune).is_empty());
}

#[test]
fn an_unused_option_slot_does_not_renumber_the_ones_after_it() {
    // Real definitions leave a retired enum slot as "" rather than INVALID. Dropping the empties
    // would make "On" the value 1 when the ECU stores 2 for it -- selecting one option and
    // writing another.
    let ini = r#"[Constants]
page = 1
nPages = 1
pageSize = 8
   mode = bits, U08, 0, [0:1], "Off", "", "On", "INVALID"
"#;

    let layout = tune_layout::read(ini, None);
    let mode = layout.constants.iter().find(|c| c.name == "mode").unwrap();

    assert_eq!(mode.options.len(), 4);
    assert_eq!(mode.options[2], "On");

    // And neither the gap nor the padding is a choice to offer.
    assert!(mode.is_valid_option(0));
    assert!(!mode.is_valid_option(1));
    assert!(mode.is_valid_option(2));
    assert!(!mode.is_valid_option(3));
}

#[test]
fn a_controller_string_with_no_offset_is_not_placed_at_zero() {
    // Defaulting it would read and write the first bytes of the page, which belong to whichever
    // constants really live there.
    let ini = r#"[Constants]
page = 1
nPages = 1
pageSize = 8
   realOne = scalar, U08, 0, "", 1, 0, 0, 255, 0
   noOffset = string, ASCII, 16
"#;

    assert!(
        !tune_layout::read(ini, None)
            .constants
            .iter()
            .any(|c| c.name == "noOffset")
    );
}

// ----- what is left after a send --------------------------------------

#[test]
fn what_the_ecu_took_stops_being_a_pending_change() {
    let mut tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "crankingRPM", 300.0, 0);
    edit.set(&tune, "dwell", 6.0, 0);

    // The controller takes both, which the tune records the way a send does.
    for write in edit.writes(&tune) {
        tune.accept(&write);
    }
    edit.reconcile(&tune);

    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
}

#[test]
fn what_it_did_not_take_stays_a_pending_change() {
    // A send is several writes, and one failing leaves the earlier ones applied. What is still
    // outstanding is neither all of it nor none.
    let mut tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "crankingRPM", 300.0, 0);
    edit.set(&tune, "trims", 9.0, 0);

    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 2);

    // Only the first one reaches the ECU.
    tune.accept(&writes[0]);
    edit.reconcile(&tune);

    assert!(edit.has_changes());
    assert_eq!(edit.changed_count(), 1);
    assert_eq!(edit.changes()[0].name, "trims");

    let left = edit.writes(&tune);
    assert_eq!(left.len(), 1);
    assert_eq!(left[0].offset, 6);
}

#[test]
fn reconciling_a_text_setting_compares_its_characters() {
    let mut tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set_text(&tune, "alias", "CLT");
    assert!(edit.has_changes());

    for write in edit.writes(&tune) {
        tune.accept(&write);
    }
    edit.reconcile(&tune);

    assert!(!edit.has_changes());
}

#[test]
fn reconciling_an_array_element_looks_at_that_element() {
    let mut tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "trims", 3.0, 1);
    edit.set(&tune, "trims", 4.0, 3);

    // Both are in one run of bytes, so both go or neither does.
    for write in edit.writes(&tune) {
        tune.accept(&write);
    }
    edit.reconcile(&tune);

    assert!(!edit.has_changes());
}

#[test]
fn reconciling_with_nothing_sent_changes_nothing() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.set(&tune, "crankingRPM", 300.0, 0);
    edit.reconcile(&tune);

    assert!(edit.has_changes());
    assert_eq!(edit.writes(&tune).len(), 1);
}

#[test]
fn the_pages_to_write_are_the_ones_a_burn_would_have_to_commit() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.pages_to_write(&tune).is_empty());

    edit.set(&tune, "crankingRPM", 300.0, 0);
    edit.set(&tune, "dwell", 6.0, 0);

    // One page here, however many writes it takes.
    assert_eq!(edit.pages_to_write(&tune), vec![0]);
}

// ----- text, judged by its bytes ----------------------------------------

#[test]
fn typing_a_name_back_to_the_one_the_ecu_holds_is_not_a_change() {
    // Comparing against the working copy rather than against the controller left this counted
    // as pending while writing nothing. The header offers to send it, the send says nothing has
    // been changed and returns before the reconcile that would clear it, and the phantom never
    // goes away.
    let tune = tune(&[(12, b'C'), (13, b'L'), (14, b'T')]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set_text(&tune, "alias", "EGT"));
    assert!(edit.set_text(&tune, "alias", "CLT"));

    assert!(!edit.has_changes());
    assert_eq!(edit.bytes_to_write(&tune), 0);
}

#[test]
fn setting_a_name_to_what_it_already_reads_keeps_the_pending_write() {
    // The same comparison the other way about: a text box pushes its value again on every focus
    // change, and the second push dropped a record whose bytes really did still differ. The
    // summary then said nothing was changed while a genuine write was outstanding, and the
    // button greyed.
    let tune = tune(&[(12, b'C'), (13, b'L'), (14, b'T')]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set_text(&tune, "alias", "EGT"));
    assert!(edit.set_text(&tune, "alias", "EGT"));

    assert!(edit.has_changes());
    assert_eq!(edit.changed_count(), 1);
    assert!(!edit.writes(&tune).is_empty());
}

#[test]
fn a_name_padded_with_spaces_reverts_to_exactly_those_bytes() {
    // Reading a name trims the padding off and writing one pads with nulls, so putting a field
    // back through its string left three bytes differing from the ECU's with nothing admitting
    // to it: no change on the header, and a write anyway on the next send.
    let tune = tune(&[(12, b'C'), (13, b'L'), (14, b'T'), (15, b' '), (16, b' '), (17, b' ')]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set_text(&tune, "alias", "EGT"));
    edit.revert(&tune, "alias", 0);

    assert!(!edit.has_changes());
    assert_eq!(edit.bytes_to_write(&tune), 0);
}

#[test]
fn reverting_one_name_leaves_the_settings_around_it_alone() {
    let tune = tune(&[(12, b'C'), (0, 0x5E), (1, 0x01)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set_text(&tune, "alias", "EGT"));
    assert!(edit.set(&tune, "crankingRPM", 500.0, 0));

    edit.revert(&tune, "alias", 0);

    assert_eq!(edit.value(&tune, "crankingRPM", 0), 500.0);
    assert_eq!(edit.text(&tune, "alias"), "C");
    assert_eq!(edit.changed_count(), 1);
}

// ----- keeping step with writes made elsewhere ---------------------------

#[test]
fn a_table_write_does_not_become_a_pending_settings_change() {
    // A table is edited through TuneEdit, not through this, but both work on the same pages.
    // When a table write lands the controller moves and the copy held here does not, so those
    // bytes read as settings waiting to be sent -- carrying the values from before the write.
    // Sending anything from a settings page would then put the table back, on a running engine,
    // with nothing reporting an error.
    let mut tune = tune(&[(6, 40), (7, 41), (8, 42), (9, 43)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    let write = TuneWrite::new(0, 6, vec![50, 51, 52, 53]);
    tune.accept(&write);
    edit.accept(&write);

    assert!(!edit.has_changes());
    assert!(edit.writes(&tune).is_empty());
    assert_eq!(edit.bytes_to_write(&tune), 0);
}

#[test]
fn a_setting_changed_before_a_table_write_is_still_sent_after_it() {
    let mut tune = tune(&[(6, 40), (7, 41), (8, 42), (9, 43)]);
    let mut edit = TuneSettingsEdit::new(&tune);

    assert!(edit.set(&tune, "crankingRPM", 500.0, 0));

    let write = TuneWrite::new(0, 6, vec![50, 51, 52, 53]);
    tune.accept(&write);
    edit.accept(&write);

    // The setting is still pending and the table is not.
    assert_eq!(edit.changed_count(), 1);
    let writes = edit.writes(&tune);
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0].offset, 0);
    assert_eq!(edit.value(&tune, "crankingRPM", 0), 500.0);
}

#[test]
fn a_write_outside_the_pages_is_ignored_rather_than_throwing() {
    let tune = tune(&[]);
    let mut edit = TuneSettingsEdit::new(&tune);

    edit.accept(&TuneWrite::new(9, 0, vec![1]));
    edit.accept(&TuneWrite::new(0, 30, vec![1, 2, 3, 4, 5, 6]));

    assert!(edit.writes(&tune).is_empty());
}
