//! Ported from `TuneCurveEditTests.cs`.
//!
//! Editing a curve: the half of tuning beside the tables.
//!
//! A MicroSquirt declares 37 of these and points 23 of its 131 menu entries at one; an MS3
//! declares 135. What makes a curve not a table is that both rows are editable: it is a line
//! you drag, and moving a point sideways is as ordinary as moving it up.

use olv_core::ini::tune_dialogs::{TuneCurve, read_curves};
use olv_core::ini::tune_layout::{self, TuneLayout};
use olv_core::tune::ecu_tune::EcuTune;
use olv_core::tune::tune_curve_edit::{CurvePoint, TuneCurveEdit};

const INI: &str = r#"[Constants]
page = 1
nPages = 1
pageSize = 64
pageIdentifier = "\x01"
pageReadCommand = "r%2o%2c"
pageValueWrite  = "w%2o%2c%v"
   wueBins = array, U08, 0, [4], "F", 1.0, -40, -40, 215, 0
   wuePct  = array, U08, 8, [4], "%", 1.0, 0, 0, 250, 0

[CurveEditor]
   curve = warmup, "Warmup Enrichment"
      columnLabel = "Coolant", "Enrichment"
      xBins = wueBins, coolant
      yBins = wuePct
"#;

fn layout() -> TuneLayout {
    tune_layout::read(INI, None)
}

/// A tune holding the given breakpoints and values.
fn tune(x: &[f64], y: &[f64]) -> EcuTune {
    let layout = layout();
    let bins = layout.constants.iter().find(|c| c.name == "wueBins").unwrap().clone();
    let pct = layout.constants.iter().find(|c| c.name == "wuePct").unwrap().clone();
    let mut tune = EcuTune::from_pages(layout, vec![vec![0u8; 64]]);

    for i in 0..x.len() {
        assert!(tune.poke_into_own(&bins, i as i32, x[i]));
        assert!(tune.poke_into_own(&pct, i as i32, y[i]));
    }

    tune
}

fn edit(tune: &EcuTune) -> TuneCurveEdit {
    TuneCurveEdit::for_curve(read_curves(INI, None).get("warmup").unwrap(), tune).unwrap()
}

fn standard() -> EcuTune {
    tune(&[-40.0, 0.0, 100.0, 200.0], &[180.0, 150.0, 110.0, 100.0])
}

// ----- reading ----------------------------------------------------------

#[test]
fn a_curve_comes_out_with_its_points_its_labels_and_its_units() {
    let edit = edit(&standard());

    assert_eq!(edit.count(), 4);
    assert_eq!(edit.title(), "Warmup Enrichment");
    assert_eq!(edit.x_label(), "Coolant");
    assert_eq!(edit.y_label(), "Enrichment");
    assert_eq!(edit.x_units(), "F");
    assert_eq!(edit.y_units(), "%");

    assert_eq!(
        edit.points(),
        vec![
            CurvePoint { x: -40.0, y: 180.0 },
            CurvePoint { x: 0.0, y: 150.0 },
            CurvePoint { x: 100.0, y: 110.0 },
            CurvePoint { x: 200.0, y: 100.0 },
        ]
    );
}

#[test]
fn a_curve_whose_rows_disagree_is_refused_rather_than_half_drawn() {
    // A line whose points are in the wrong places is not a near miss.
    let ragged = r#"[Constants]
page = 1
nPages = 1
pageSize = 64
   bins = array, U08, 0, [4], "F", 1.0, 0, 0, 250, 0
   pct  = array, U08, 8, [6], "%", 1.0, 0, 0, 250, 0

[CurveEditor]
   curve = odd, "Odd"
      xBins = bins
      yBins = pct
"#;

    let layout = tune_layout::read(ragged, None);

    assert!(
        TuneCurveEdit::for_curve(
            read_curves(ragged, None).get("odd").unwrap(),
            &EcuTune::from_pages(layout, vec![vec![0u8; 64]])
        )
        .is_none()
    );
}

#[test]
fn a_curve_naming_constants_this_firmware_lacks_is_refused() {
    let curve = TuneCurve {
        name: "ghost".into(),
        title: "Ghost".into(),
        x_bins: "noSuchX".into(),
        y_bins: "noSuchY".into(),
        x_label: String::new(),
        y_label: String::new(),
        x_low: String::new(),
        x_high: String::new(),
        y_low: String::new(),
        y_high: String::new(),
        help: String::new(),
    };

    assert!(TuneCurveEdit::for_curve(&curve, &EcuTune::from_pages(layout(), vec![vec![0u8; 64]])).is_none());
}

// ----- editing ----------------------------------------------------------

#[test]
fn moving_a_point_up_is_recorded_against_what_the_ecu_holds() {
    let mut edit = edit(&standard());

    edit.set_y(1, 160.0);

    assert!(edit.has_changes());
    assert_eq!(edit.changed_count(), 1);
    assert!(edit.is_changed(1));
    assert!(!edit.is_changed(0));
    assert_eq!(edit.original_y(1), 150.0);
}

#[test]
fn a_value_past_what_the_firmware_allows_stops_at_the_edge() {
    // Clamped rather than refused, because this is a line being dragged: a pointer past the
    // top of the plot should stop at the top.
    let mut edit = edit(&standard());

    edit.set_y(0, 9999.0);

    assert_eq!(edit.y(0), 250.0);
}

#[test]
fn a_breakpoint_cannot_overtake_its_neighbours() {
    // The ECU interpolates between consecutive entries and takes the row as ascending. One that
    // overtakes makes the lookup read backwards over that span: not an error anywhere, just a
    // curve that does the opposite of what it shows for one stretch.
    let mut edit = edit(&standard());

    edit.set_x(1, 150.0);
    assert_eq!(edit.x(1), 100.0);

    edit.set_x(1, -200.0);
    assert_eq!(edit.x(1), -40.0);
}

#[test]
fn the_ends_are_free_to_move_within_the_firmwares_range() {
    let mut edit = edit(&standard());

    edit.set_x(3, 215.0);
    assert_eq!(edit.x(3), 215.0);

    edit.set_x(3, 9999.0);
    assert_eq!(edit.x(3), 215.0);
}

#[test]
fn a_straight_line_is_drawn_along_the_breakpoints_not_along_the_positions() {
    // Breakpoints are spread unevenly (a warmup curve is dense where it is cold) so
    // interpolating by position would bend the line.
    let mut edit = edit(&tune(&[0.0, 25.0, 75.0, 100.0], &[100.0, 0.0, 0.0, 200.0]));

    assert!(edit.interpolate(0, 3));

    assert!((edit.y(1) - 125.0).abs() < 1e-6); // a quarter of the way along
    assert!((edit.y(2) - 175.0).abs() < 1e-6); // three quarters
}

#[test]
fn interpolating_needs_something_in_between() {
    let mut edit = edit(&standard());

    assert!(!edit.interpolate(1, 2));
    assert!(!edit.interpolate(2, 2));
    assert!(!edit.interpolate(-1, 3));
}

#[test]
fn reverting_puts_both_rows_back() {
    let mut edit = edit(&standard());

    edit.set_y(1, 160.0);
    edit.set_x(1, 50.0);
    edit.revert_point(1);

    assert!(!edit.has_changes());
    assert_eq!(edit.x(1), 0.0);
    assert_eq!(edit.y(1), 150.0);
}

#[test]
fn a_row_that_is_not_ascending_does_not_bring_the_application_down() {
    // A row never configured, or one a firmware writes descending, gives a lower neighbour
    // above the upper one. Clamping between them throws rather than doing nothing, and this is
    // called straight from a drag, so it would be the application going down under the pointer.
    let mut edit = edit(&tune(&[0.0, 50.0, 100.0, 20.0], &[10.0, 20.0, 30.0, 40.0]));

    edit.set_x(2, 60.0);

    assert_eq!(edit.x(2), 100.0);
    assert!(!edit.is_changed(2));
}

// ----- sending ----------------------------------------------------------

#[test]
fn only_the_row_that_moved_is_written() {
    let tune = standard();
    let mut edit = edit(&tune);

    edit.set_y(1, 160.0);

    let writes = edit.encode(&tune).unwrap();
    assert_eq!(writes.len(), 1);
    assert_eq!(writes[0].offset, 8); // the values, not the breakpoints
}

#[test]
fn moving_a_point_sideways_writes_the_breakpoints_too() {
    // Sending values without the breakpoints they were drawn against leaves the ECU
    // interpolating new numbers over the old axis: a different curve from the one on screen,
    // and nothing would say so.
    let tune = standard();
    let mut edit = edit(&tune);

    edit.set_x(1, 50.0);
    edit.set_y(1, 160.0);

    let writes = edit.encode(&tune).unwrap();

    assert_eq!(writes.len(), 2);
    assert!(writes.iter().any(|w| w.offset == 0));
    assert!(writes.iter().any(|w| w.offset == 8));
}

#[test]
fn an_untouched_curve_writes_nothing() {
    let tune = standard();

    assert!(edit(&tune).encode(&tune).unwrap().is_empty());
}

#[test]
fn what_is_sent_is_what_comes_back() {
    let layout = layout();
    let tune = standard();
    let mut edit = edit(&tune);

    edit.set_y(2, 120.0);
    edit.set_x(2, 90.0);

    let mut copy = EcuTune::from_pages(layout, tune.pages().to_vec());
    for write in edit.encode(&tune).unwrap() {
        copy.accept(&write);
    }

    assert_eq!(copy.array("wueBins").unwrap(), vec![-40.0, 0.0, 90.0, 200.0]);
    assert_eq!(copy.array("wuePct").unwrap(), vec![180.0, 150.0, 120.0, 100.0]);
}
