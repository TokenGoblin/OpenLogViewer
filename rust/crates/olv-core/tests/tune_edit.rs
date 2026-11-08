//! Ported from `TuneEditTests.cs`.
//!
//! Changing a table before any of it is sent.
//!
//! The whole point of keeping this separate from the connection is that it can be checked
//! without an engine attached, and it is the half where a mistake is silent, since a wrong
//! number encodes to a perfectly valid byte.

use olv_core::ini::msq_ini::RealtimeType;
use olv_core::ini::tune_layout::{TuneConstant, TuneLayout, TunePage};
use olv_core::model::{Array2, IgnoreCaseMap, TuneAxis, TuneTable};
use olv_core::tune::ecu_tune::EcuTune;
use olv_core::tune::tune_edit::{TuneEdit, TuneSelection};

fn axis(constant: &str, units: &str, breakpoints: &[f64]) -> TuneAxis {
    TuneAxis {
        constant: constant.into(),
        units: units.into(),
        breakpoints: breakpoints.to_vec(),
    }
}

/// A 4x3 fuel table, stored as bytes, 0 to 255 per the firmware.
fn fuel() -> TuneEdit {
    let mut values: Array2<f64> = Array2::new(4, 3);
    for column in 0..4 {
        for row in 0..3 {
            values[(column, row)] = 50.0 + (column * 10) as f64 + row as f64;
        }
    }

    let table = TuneTable {
        name: "VE Table".into(),
        x: axis("rpmBins", "rpm", &[800.0, 2000.0, 4000.0, 6000.0]),
        y: axis("mapBins", "kPa", &[30.0, 60.0, 100.0]),
        values,
        units: "%".into(),
    };

    let mut constant = TuneConstant::new("veTable", 0, 0, RealtimeType::U08);
    constant.columns = 4;
    constant.rows = 3;
    constant.scale = 1.0;
    constant.low = 0.0;
    constant.high = 255.0;
    constant.digits = 0;

    TuneEdit::new(table, Some(constant))
}

fn cell(column: i32, row: i32) -> TuneSelection {
    TuneSelection::cell(column, row)
}

/// xunit's `Assert.Equal(expected, actual, precision)`: both rounded to that many decimals.
fn near(expected: f64, actual: f64, precision: i32) {
    let scale = 10f64.powi(precision);
    assert_eq!(
        (expected * scale).round_ties_even(),
        (actual * scale).round_ties_even(),
        "{expected} vs {actual}"
    );
}

// ----- what was read stays available ------------------------------------

#[test]
fn nothing_is_changed_to_begin_with() {
    let edit = fuel();

    assert!(!edit.has_changes());
    assert_eq!(edit.changed_count(), 0);
}

#[test]
fn what_the_ecu_said_is_still_there_after_an_edit() {
    // A tuner who cannot see what they changed cannot decide whether to send it, so the
    // original has to survive alongside the edit.
    let mut edit = fuel();
    let before = edit.get(1, 1);

    edit.set(cell(1, 1), 99.0);

    assert_eq!(edit.get(1, 1), 99.0);
    assert_eq!(edit.original(1, 1), before);
    assert!(edit.is_changed(1, 1));
    assert!(!edit.is_changed(0, 0));
    assert_eq!(edit.changed_count(), 1);
}

#[test]
fn reverting_puts_everything_back() {
    let mut edit = fuel();
    edit.scale(TuneSelection::new(0, 0, 3, 2), 25.0);

    assert!(edit.has_changes());

    edit.revert_all();

    assert!(!edit.has_changes());
    assert_eq!(edit.original(2, 1), edit.get(2, 1));
}

#[test]
fn part_of_a_table_can_be_put_back_without_losing_the_rest() {
    let mut edit = fuel();
    edit.set(cell(0, 0), 10.0);
    edit.set(cell(3, 2), 20.0);

    edit.revert(cell(0, 0));

    assert!(!edit.is_changed(0, 0));
    assert!(edit.is_changed(3, 2));
}

// ----- the operations ----------------------------------------------------

#[test]
fn a_rectangle_is_edited_together() {
    let mut edit = fuel();

    edit.set(TuneSelection::new(1, 0, 2, 1), 77.0);

    assert_eq!(edit.get(1, 0), 77.0);
    assert_eq!(edit.get(2, 1), 77.0);
    assert!(!edit.is_changed(0, 0));
    assert!(!edit.is_changed(3, 2));
    assert_eq!(edit.changed_count(), 4);
}

#[test]
fn a_selection_dragged_backwards_covers_the_same_cells() {
    // Either corner may be the one touched first.
    let mut forwards = fuel();
    let mut backwards = fuel();

    forwards.set(TuneSelection::new(1, 0, 2, 1), 77.0);
    backwards.set(TuneSelection::new(2, 1, 1, 0), 77.0);

    for column in 0..forwards.columns() {
        for row in 0..forwards.rows() {
            assert_eq!(forwards.get(column, row), backwards.get(column, row));
        }
    }
}

#[test]
fn scaling_is_the_operation_tuning_is_done_in() {
    // A region reading four per cent lean is corrected by adding four per cent to it, not by
    // typing every cell.
    let mut edit = fuel();
    let before = edit.get(2, 1);

    edit.scale(cell(2, 1), 4.0);

    near(before * 1.04, edit.get(2, 1), 6);
}

#[test]
fn adding_is_the_arrow_key_edit() {
    let mut edit = fuel();
    let before = edit.get(0, 0);

    edit.add(cell(0, 0), 1.0);
    near(before + 1.0, edit.get(0, 0), 6);

    edit.add(cell(0, 0), -1.0);
    near(before, edit.get(0, 0), 6);
    assert!(!edit.is_changed(0, 0));
}

// ----- the firmware's limits ---------------------------------------------

#[test]
fn a_value_past_what_the_firmware_allows_is_held_at_the_limit() {
    // Clamped rather than refused: scaling a whole table up should not fail because one cell
    // was already at the top. What must not happen is an out-of-range value reaching the
    // encoder, which would refuse the write entirely and turn one bad cell into a table that
    // cannot be sent.
    let mut edit = fuel();

    edit.set(cell(0, 0), 5000.0);
    assert_eq!(edit.get(0, 0), 255.0);

    edit.set(cell(0, 1), -40.0);
    assert_eq!(edit.get(0, 1), 0.0);
}

#[test]
fn a_table_with_no_declared_range_is_left_to_the_encoder() {
    // Some firmware omits the limits. Inventing one would be worse than letting the datatype
    // decide, which the encoder already checks.
    let table = TuneTable {
        name: "Unknown".into(),
        x: axis("x", "", &[1.0, 2.0]),
        y: axis("y", "", &[1.0]),
        values: Array2::new(2, 1),
        units: String::new(),
    };

    let mut edit = TuneEdit::new(table, None);
    edit.set(cell(0, 0), 5000.0);

    assert_eq!(edit.get(0, 0), 5000.0);
    assert!(edit.low().is_nan());
}

#[test]
fn nonsense_never_reaches_a_cell() {
    let mut edit = fuel();

    edit.scale(cell(0, 0), f64::NAN);

    assert!(!edit.get(0, 0).is_nan());
}

// ----- what it encodes to ------------------------------------------------

#[test]
fn an_edit_encodes_to_the_bytes_the_ecu_would_hold() {
    // The half that cannot be seen on screen, and the half where being wrong is silent: a
    // wrong number encodes to a perfectly valid byte.
    let mut ve = TuneConstant::new("veTable", 0, 0, RealtimeType::U08);
    ve.columns = 4;
    ve.rows = 3;
    ve.scale = 1.0;
    ve.low = 0.0;
    ve.high = 255.0;

    let layout = TuneLayout {
        pages: vec![TunePage {
            index: 0,
            size: 12,
            identifier: String::new(),
            read_command: "r".into(),
            chunk_write_command: String::new(),
            burn_command: String::new(),
        }],
        constants: vec![ve],
        pc_variables: Vec::new(),
        derived: IgnoreCaseMap::new(),
        little_endian: true,
        blocking_factor: 0,
        inter_write_delay: 0,
        after_burn_delay: 0,
    };

    let tune = EcuTune::from_pages(layout, vec![vec![0u8; 12]]);
    let mut edit = fuel();

    edit.set(cell(0, 0), 100.0);

    let write = edit.encode(&tune).expect("a write");

    assert_eq!(write.page, 0);
    assert_eq!(write.offset, 0);
    assert_eq!(write.data.len(), 12);

    // Row-major, as the firmware stores it: cell (column 0, row 0) first.
    assert_eq!(write.data[0], 100);

    // And the cells that were not touched still carry what they read.
    assert_eq!(write.data[1], edit.original(1, 0) as u8);
}

#[test]
fn scaling_by_a_percentage_and_back_changes_nothing() {
    // Floating-point dust must not leave a table claiming to be dirty while encoding to the
    // identical bytes: that reads as a write being ignored.
    let mut edit = fuel();
    let whole = TuneSelection::new(0, 0, 3, 2);

    edit.scale(whole, 10.0);
    edit.scale(whole, -100.0 / 11.0);

    assert!(!edit.has_changes());
}

#[test]
fn the_smallest_useful_step_comes_from_how_the_value_is_stored() {
    // A table of whole bytes cannot hold half a percent, so nudging by less would show a
    // change the ECU rounds away.
    assert_eq!(fuel().step(), 1.0);
}

// ----- filling between the ends -----------------------------------------

/// A table whose middle has been pulled about, for straightening.
fn ragged() -> TuneEdit {
    let mut edit = fuel();

    // The ends of the top row are 50 and 80; make the middle nonsense.
    edit.set(cell(1, 0), 10.0);
    edit.set(cell(2, 0), 200.0);

    edit
}

#[test]
fn interpolating_a_row_puts_a_straight_line_between_its_ends() {
    let mut edit = ragged();

    assert!(edit.interpolate(TuneSelection::new(0, 0, 3, 0)));

    // 50 to 80 across four cells: 50, 60, 70, 80.
    near(50.0, edit.get(0, 0), 6);
    near(60.0, edit.get(1, 0), 6);
    near(70.0, edit.get(2, 0), 6);
    near(80.0, edit.get(3, 0), 6);
}

#[test]
fn the_ends_are_left_exactly_as_they_were() {
    // The corners are the input, not the output. Anything that moved them would drift the
    // table a little further every time it was used.
    let mut edit = ragged();

    edit.interpolate(TuneSelection::new(0, 0, 3, 0));

    let once = [edit.get(0, 0), edit.get(1, 0), edit.get(2, 0), edit.get(3, 0)];

    edit.interpolate(TuneSelection::new(0, 0, 3, 0));

    let twice = [edit.get(0, 0), edit.get(1, 0), edit.get(2, 0), edit.get(3, 0)];

    for i in 0..once.len() {
        near(once[i], twice[i], 9);
    }
}

#[test]
fn interpolating_a_column_works_the_other_way() {
    let mut edit = fuel();

    // Column 0 holds 50, 51, 52. Pull the middle out and put it back.
    edit.set(cell(0, 1), 99.0);

    assert!(edit.interpolate(TuneSelection::new(0, 0, 0, 2)));

    near(51.0, edit.get(0, 1), 6);
}

#[test]
fn a_rectangle_is_filled_from_its_four_corners() {
    let mut edit = fuel();

    // The whole 4x3 table. Its corners are 50, 80, 52 and 82, and the interior should come out
    // as the plane through them, which for this table is what it already held, since it was
    // built as one.
    assert!(edit.interpolate(TuneSelection::new(0, 0, 3, 2)));

    for column in 0..4 {
        for row in 0..3 {
            near(50.0 + (column * 10) as f64 + row as f64, edit.get(column, row), 6);
        }
    }
}

#[test]
fn a_selection_with_no_middle_is_refused_rather_than_reported_done() {
    // Two cells across are both ends and nothing between. Saying "done" would leave the user
    // believing the table had been smoothed.
    let mut edit = fuel();

    assert!(!edit.interpolate(cell(1, 1)));
    assert!(!edit.interpolate(TuneSelection::new(0, 0, 1, 0)));
    assert!(!edit.interpolate(TuneSelection::new(0, 0, 1, 1)));

    assert!(!edit.has_changes());
}

#[test]
fn interpolation_stays_inside_what_the_firmware_allows() {
    let mut edit = fuel();

    // Ends far outside the 0..255 the constant declares.
    edit.set(cell(0, 0), 0.0);
    edit.set(cell(3, 0), 255.0);

    edit.interpolate(TuneSelection::new(0, 0, 3, 0));

    for column in 0..4 {
        let v = edit.get(column, 0);
        assert!((0.0..=255.0).contains(&v), "{v}");
    }
}

// ----- what it would become ---------------------------------------------

#[test]
fn one_cell_reports_what_it_was_and_what_it_would_be() {
    let mut edit = fuel();

    edit.add(cell(1, 1), 5.0);

    let change = edit.preview(cell(1, 1));

    assert!(change.is_single());
    near(61.0, change.from(), 6);
    near(66.0, change.to(), 6);
    near(5.0, change.delta(), 6);
    near(5.0 / 61.0 * 100.0, change.percent(), 6);
    assert_eq!(change.changed, 1);
}

#[test]
fn an_untouched_cell_says_so_rather_than_showing_a_change_of_nothing() {
    let change = fuel().preview(cell(2, 1));

    assert!(!change.any());
    assert_eq!(change.changed, 0);
    near(change.from(), change.to(), 6);
    near(0.0, change.delta(), 6);
}

#[test]
fn a_selection_reports_the_range_it_would_become() {
    let mut edit = fuel();

    // The top row, 50 60 70 80, scaled up a tenth.
    edit.scale(TuneSelection::new(0, 0, 3, 0), 10.0);

    let change = edit.preview(TuneSelection::new(0, 0, 3, 0));

    assert!(!change.is_single());
    assert_eq!(change.cells, 4);
    assert_eq!(change.changed, 4);

    near(50.0, change.from_low, 6);
    near(80.0, change.from_high, 6);
    near(55.0, change.to_low, 6);
    near(88.0, change.to_high, 6);

    // A percentage scale moves each cell by a different amount, so it is not uniform and the
    // readout has to show a range rather than one figure.
    assert!(!change.uniform());
    near(5.0, change.delta_low, 6);
    near(8.0, change.delta_high, 6);
}

#[test]
fn a_nudge_moves_every_cell_by_the_same_amount_and_says_so() {
    let mut edit = fuel();

    edit.add(TuneSelection::new(0, 0, 3, 0), 2.0);

    let change = edit.preview(TuneSelection::new(0, 0, 3, 0));

    assert!(change.uniform());
    near(2.0, change.delta_low, 6);
    near(2.0, change.delta_high, 6);
}

#[test]
fn a_percentage_of_nothing_is_not_a_number() {
    // A spark table legitimately holds zero, and a change from it has no percentage. Reporting
    // an infinite increase would be worse than reporting none. The zero has to be what the ECU
    // said, not something typed on the way past: the percentage is measured from the original.
    let mut values: Array2<f64> = Array2::new(2, 1);
    values[(0, 0)] = 0.0;
    values[(1, 0)] = 10.0;

    let mut edit = TuneEdit::new(
        TuneTable {
            name: "Spark".into(),
            x: axis("rpmBins", "rpm", &[800.0, 2000.0]),
            y: axis("mapBins", "kPa", &[30.0]),
            values,
            units: "deg".into(),
        },
        None,
    );

    edit.add(cell(0, 0), 3.0);
    edit.add(cell(1, 0), 3.0);

    assert!(edit.preview(cell(0, 0)).percent().is_nan());
    near(30.0, edit.preview(cell(1, 0)).percent(), 6);
}

#[test]
fn the_preview_follows_every_way_of_changing_a_cell() {
    let mut edit = fuel();

    let changes: [fn(&mut TuneEdit); 3] = [
        |e| e.add(cell(1, 1), 4.0),
        |e| e.scale(cell(1, 1), 10.0),
        |e| e.set(cell(1, 1), 99.0),
    ];

    for change in changes {
        edit.revert_all();
        change(&mut edit);

        let preview = edit.preview(cell(1, 1));

        near(edit.get(1, 1), preview.to(), 6);
        near(edit.original(1, 1), preview.from(), 6);
        assert!(preview.any());
    }
}
