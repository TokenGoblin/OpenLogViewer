//! The gates in front of every byte an agent sends to an engine, each tried the way an agent (or
//! a bug) would try to get round it.
//!
//! Ported from `AgentGuardrailTests` and `AgentTuneWorkflowTests` (the C# drives them through the
//! view model and a `FakeController`; the writer is connected to the same kind of fake directly),
//! plus a test for every way past a gate that this layer has to close: the arming gate, the
//! disconnect, the placeholder and file tunes, the rate limit and its batch rule, the idle gate,
//! the dangerous-constant confirmation, the magnitude limit, the rationale, and the staging
//! folder's path safety.
//!
//! Every test that expects a refusal also checks the controller's bytes and write log, because a
//! refusal that still sent something is not one.

mod support;

use std::sync::Arc;
use std::time::Duration;

use olv_agent::agent::agent_api::{ProposedCell, ProposedSetting};
use olv_agent::agent::agent_writer::{AgentWriter, EcuTarget, TuneInHand};
use support::*;

fn setting(name: &str, value: f64) -> ProposedSetting {
    ProposedSetting {
        name: name.to_string(),
        value,
    }
}

fn cell(table: &str, column: i32, row: i32, value: f64) -> ProposedCell {
    ProposedCell {
        table: table.to_string(),
        column,
        row,
        value,
    }
}

// ----- a rationale is required ---------------------------------------------------------

#[test]
fn a_write_with_no_rationale_is_refused_and_nothing_is_sent() {
    let rig = armed_rig("rationale");

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "", false)
        .expect("refused");

    assert!(refused.reason.contains("no rationale"));
    assert_eq!(rig.board.page()[0], 0x01);
    assert_eq!(rig.board.page()[1], 0x2C);
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_blank_rationale_is_the_same_as_none() {
    let rig = armed_rig("blank");

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "   ", false)
        .expect("refused");

    assert!(refused.reason.contains("no rationale"));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_table_write_also_needs_a_rationale() {
    let rig = armed_rig("table-rationale");

    let refused = rig
        .writer
        .set_table_cell("VE Table", 0, 0, 55.0, "", false)
        .refusal
        .expect("refused");

    assert!(refused.reason.contains("no rationale"));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_rationale_is_not_enough_on_its_own_if_writes_are_not_armed() {
    let rig = rig("not-armed");

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "a good reason", false)
        .expect("refused");

    assert!(refused.reason.contains("not armed"));
    assert!(rig.board.writes().is_empty());
}

// ----- the arming gate ---------------------------------------------------------------------

#[test]
fn a_fresh_writer_is_not_armed_and_says_so_to_every_write_entry_point() {
    let rig = rig("fresh");
    assert!(!rig.writer.is_armed());

    let one = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", true)
        .expect("refused");
    let cell_write = rig.writer.set_table_cell("VE Table", 0, 0, 5.0, "reason", true);
    let batch = rig.writer.apply_tune(
        &[setting("crankingRPM", 310.0)],
        &[cell("VE Table", 0, 0, 5.0)],
        "reason",
        true,
    );

    assert!(one.reason.contains("not armed"));
    assert!(cell_write.refusal.expect("refused").reason.contains("not armed"));
    assert!(batch.refusal.expect("refused").reason.contains("not armed"));
    assert!(batch.applied.is_empty());
    assert!(rig.board.writes().is_empty(), "nothing may reach the controller");
    assert_eq!(rig.board.page(), Board::new().page());
}

#[test]
fn disconnecting_disarms_and_reconnecting_does_not_arm_again() {
    let rig = armed_rig("disconnect");
    assert!(rig.writer.is_armed());

    rig.writer.disconnect();
    assert!(!rig.writer.is_armed());

    // A new connection (the next thing plugged in may be a car) starts disarmed too.
    rig.writer.connect(EcuTarget::Pages(Arc::clone(&rig.connection)));
    assert!(!rig.writer.is_armed());

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");
    assert!(refused.reason.contains("not armed"));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn armed_but_not_connected_is_refused() {
    let rig = armed_rig("unconnected");
    rig.writer.disconnect();
    rig.writer.set_armed(true);

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");

    assert_eq!(refused.reason, "not connected to an ECU");
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_placeholder_tune_is_never_written_from_even_when_armed() {
    let rig = armed_rig("placeholder");
    rig.writer.set_tune(Some(TuneInHand {
        placeholder: true,
        ..tune_in_hand(&rig.board)
    }));

    let one = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");
    let cell_write = rig.writer.set_table_cell("VE Table", 0, 0, 5.0, "reason", false);

    assert!(one.reason.contains("placeholder"));
    assert!(cell_write.refusal.expect("refused").reason.contains("placeholder"));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_tune_opened_from_a_file_is_never_written_back_but_can_still_be_proposed_against() {
    let rig = armed_rig("from-file");
    rig.writer.set_tune(Some(TuneInHand {
        from_file: true,
        ..tune_in_hand(&rig.board)
    }));

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");
    assert!(refused.reason.contains("came from a file"));
    assert!(rig.board.writes().is_empty());

    // Nothing here reaches an ECU, so a diff against it is fine.
    let proposal = rig.writer.propose_tune(&[setting("crankingRPM", 310.0)], &[]);
    assert_eq!(proposal.changes.len(), 1);
}

#[test]
fn with_no_tune_at_all_a_write_is_refused() {
    let rig = armed_rig("no-tune");
    rig.writer.set_tune(None);

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "reason", false)
        .expect("refused");

    assert_eq!(refused.reason, "no tune has been read");
    assert!(rig.board.writes().is_empty());
}

// ----- dangerous constants -----------------------------------------------------------------

#[test]
fn a_dangerous_setting_is_refused_without_confirmation() {
    // "revLimit" matches DangerousRole.RevLimiter.
    let rig = armed_rig("dangerous");

    let refused = rig
        .writer
        .set_setting("revLimit", 6600.0, "raising it for a dyno pull", false)
        .expect("refused");

    assert!(refused.reason.contains("confirmDangerous"));
    assert!(refused.detail.contains("RevLimiter"));
    assert_eq!(rig.board.page()[2], 0x19);
    assert_eq!(rig.board.page()[3], 0x64);
    assert!(rig.board.writes().is_empty());
}

#[test]
fn it_goes_through_once_confirmed() {
    let rig = armed_rig("confirmed");

    let refused = rig
        .writer
        .set_setting("revLimit", 6600.0, "raising it for a dyno pull", true);

    assert!(refused.is_none(), "{refused:?}");

    // 6600 rpm, big-endian, at the offset the firmware declares.
    assert_eq!(rig.board.page()[2], 0x19);
    assert_eq!(rig.board.page()[3], 0xC8);
}

#[test]
fn an_ordinary_setting_needs_no_confirmation() {
    let rig = armed_rig("ordinary");

    assert!(
        rig.writer
            .set_setting("crankingRPM", 310.0, "small nudge", false)
            .is_none()
    );
}

#[test]
fn a_table_whose_constant_is_dangerous_needs_confirmation_too() {
    // The cells of "Launch Table" are the constant "launchTable", which the launch-control guard
    // recognises: the table's display title carries none of the firmware's own spelling.
    let rig = armed_rig("dangerous-table");

    let refused = rig
        .writer
        .set_table_cell("Launch Table", 0, 0, 20.0, "tuning launch", false);

    assert!(refused.refusal.expect("refused").reason.contains("confirmDangerous"));
    assert!(rig.board.writes().is_empty());

    let allowed = rig
        .writer
        .set_table_cell("Launch Table", 0, 0, 20.0, "tuning launch", true);
    assert!(allowed.refusal.is_none());
    assert_eq!(rig.board.page()[24], 20);
}

#[test]
fn confirming_one_dangerous_write_does_not_confirm_the_next() {
    let rig = armed_rig("confirm-once");

    assert!(rig.writer.set_setting("revLimit", 6600.0, "first", true).is_none());

    let refused = rig
        .writer
        .set_setting("revLimit", 6700.0, "second", false)
        .expect("refused");
    assert!(refused.reason.contains("confirmDangerous"));
}

#[test]
fn a_dangerous_item_in_a_batch_is_rejected_by_name_and_the_rest_still_goes() {
    let rig = armed_rig("dangerous-batch");

    let result = rig.writer.apply_tune(
        &[setting("revLimit", 7000.0), setting("crankingRPM", 400.0)],
        &[],
        "testing",
        false,
    );

    assert!(result.refusal.is_none());
    assert!(
        result
            .rejected
            .iter()
            .any(|r| r.starts_with("revLimit:") && r.contains("confirmDangerous"))
    );
    assert_eq!(rig.board.page()[2], 0x19, "the limiter must not have moved");
    assert_eq!(rig.board.page()[3], 0x64);
    assert_eq!(rig.board.page()[1], 0x90, "the ordinary setting still landed");
}

// ----- magnitude ---------------------------------------------------------------------------

#[test]
fn a_change_over_half_the_declared_range_is_refused() {
    // crankingRPM's declared range is 0-10000; half of that is 5000, and 300 to 6000 moves it by
    // 5700.
    let rig = armed_rig("magnitude");

    let refused = rig
        .writer
        .set_setting("crankingRPM", 6000.0, "a very large change", false)
        .expect("refused");

    assert!(refused.reason.contains("too large"));
    assert!(refused.detail.contains("5700") && refused.detail.contains("0\u{2013}10000"));
    assert_eq!(rig.board.page()[0], 0x01);
    assert_eq!(rig.board.page()[1], 0x2C);
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_refused_magnitude_leaves_nothing_pending_behind_it() {
    // A refusal must not silently arm the button for the next call to send.
    let rig = armed_rig("magnitude-pending");

    rig.writer
        .set_setting("crankingRPM", 6000.0, "a very large change", false);

    assert!(!rig.writer.has_setting_changes());
}

#[test]
fn a_change_within_half_the_range_goes_through() {
    let rig = armed_rig("within");

    assert!(
        rig.writer
            .set_setting("crankingRPM", 3000.0, "a real tuning step", false)
            .is_none()
    );

    assert_eq!(rig.board.page()[0], 0x0B);
    assert_eq!(rig.board.page()[1], 0xB8);
}

#[test]
fn a_table_cells_magnitude_is_judged_against_the_firmwares_declared_range() {
    // The VE table's cells are declared 0-255; half of that is 127.5, so a jump from nought to
    // 200 is refused.
    let rig = armed_rig("table-magnitude");

    let refused = rig
        .writer
        .set_table_cell("VE Table", 0, 0, 200.0, "a big jump", false)
        .refusal
        .expect("refused");

    assert!(refused.reason.contains("too large"));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_smaller_table_cell_change_goes_through() {
    let rig = armed_rig("table-small");

    assert!(
        rig.writer
            .set_table_cell("VE Table", 0, 0, 60.0, "smoothing a cell", false)
            .refusal
            .is_none()
    );
    assert_eq!(rig.board.page()[16], 60);
}

#[test]
fn a_cell_out_of_the_firmwares_range_is_clamped_and_says_what_landed() {
    // 255 is the top of the declared range: asking for more lands on it, and the answer says so.
    let rig = armed_rig("clamp");
    assert!(
        rig.writer
            .set_table_cell("VE Table", 0, 0, 100.0, "step one", false)
            .refusal
            .is_none()
    );
    assert!(
        rig.writer
            .set_table_cell("VE Table", 0, 0, 200.0, "step two", false)
            .refusal
            .is_none()
    );

    let landed = rig
        .writer
        .set_table_cell("VE Table", 0, 0, 300.0, "past the top", false);

    assert!(landed.refusal.is_none());
    assert_eq!(landed.value, 255.0);
    assert_eq!(rig.board.page()[16], 255);
}

// ----- the rate limit ------------------------------------------------------------------------

#[test]
fn ten_writes_go_through_and_the_eleventh_is_refused() {
    let rig = armed_rig("rate");

    for i in 0..10 {
        // Starts at 301 rather than 300 (the value already on the controller), and moves by one
        // each time, so every call is a real change rather than a no-op the ECU would refuse to
        // send.
        let refused = rig
            .writer
            .set_setting("crankingRPM", 301.0 + f64::from(i), &format!("step {i}"), false);
        assert!(refused.is_none(), "{refused:?}");
    }

    let eleventh = rig
        .writer
        .set_setting("crankingRPM", 320.0, "one too many, too soon", false)
        .expect("refused");

    assert!(eleventh.reason.contains("too many writes"));
    assert!(eleventh.detail.contains("10 agent writes landed in the last 5 seconds"));
    assert_eq!(rig.board.writes().len(), 10, "the eleventh must not have been sent");
}

#[test]
fn the_limit_applies_to_cells_and_settings_together() {
    let rig = armed_rig("rate-mixed");

    for i in 0..5 {
        assert!(
            rig.writer
                .set_setting("crankingRPM", 301.0 + f64::from(i), "s", false)
                .is_none()
        );
        assert!(
            rig.writer
                .set_table_cell("VE Table", 0, 0, 1.0 + f64::from(i), "c", false)
                .refusal
                .is_none()
        );
    }

    let refused = rig
        .writer
        .set_table_cell("VE Table", 1, 1, 9.0, "one too many", false)
        .refusal
        .expect("refused");
    assert!(refused.reason.contains("too many writes"));
}

#[test]
fn the_window_expires_so_a_careful_agent_is_not_locked_out() {
    let rig = rig("rate-window");
    let writer =
        AgentWriter::with_window_for_tests(rig.workspace.clone(), rig.host.clone(), Duration::from_millis(150));
    writer.connect(EcuTarget::Pages(Arc::clone(&rig.connection)));
    writer.set_tune(Some(tune_in_hand(&rig.board)));
    writer.set_armed(true);

    for i in 0..10 {
        assert!(
            writer
                .set_setting("crankingRPM", 301.0 + f64::from(i), "s", false)
                .is_none()
        );
    }
    assert!(writer.set_setting("crankingRPM", 320.0, "too soon", false).is_some());

    std::thread::sleep(Duration::from_millis(250));

    assert!(
        writer
            .set_setting("crankingRPM", 320.0, "after the window", false)
            .is_none()
    );
}

#[test]
fn a_refused_write_does_not_spend_the_allowance() {
    let rig = armed_rig("rate-refusals");

    // Fifty refusals (no rationale, then too large): none of them reached the ECU, so none count.
    for _ in 0..25 {
        rig.writer.set_setting("crankingRPM", 310.0, "", false);
        rig.writer.set_setting("crankingRPM", 9000.0, "too big", false);
    }

    assert!(
        rig.writer
            .set_setting("crankingRPM", 310.0, "a real one", false)
            .is_none()
    );
}

#[test]
fn a_batch_larger_than_the_rate_limit_still_applies_wholly() {
    // The limit exists to catch an agent looping, not one deliberate change that happens to move
    // a lot of cells. Counting the items would leave the first ten applied and the rest refused:
    // half a table row moved on a running engine, which is the one outcome nobody asked for.
    let rig = armed_rig("batch");

    let whole_table = [
        cell("VE Table", 0, 0, 41.0),
        cell("VE Table", 1, 0, 42.0),
        cell("VE Table", 0, 1, 43.0),
        cell("VE Table", 1, 1, 44.0),
    ];

    // Twelve settings and four cells: sixteen items, well past the ten a per-item count allows.
    let many: Vec<ProposedSetting> = (0..12).map(|i| setting("crankingRPM", 300.0 + f64::from(i))).collect();

    let result = rig.writer.apply_tune(&many, &whole_table, "filling the table", false);

    assert!(result.refusal.is_none());
    assert!(
        !result.rejected.iter().any(|r| r.contains("too many writes")),
        "{:?}",
        result.rejected
    );

    // Every cell of the table landed, not just the ones before the tenth write.
    let page = rig.board.page();
    assert_eq!(page[16], 41);
    assert_eq!(page[17], 42);
    assert_eq!(page[18], 43);
    assert_eq!(page[19], 44);
}

#[test]
fn but_looping_the_batch_endpoint_still_trips_the_rate_limit() {
    // The batch standing the per-item limit down must not stand the limit itself down: an agent
    // calling apply in a loop is exactly what this catches, and each call spends one of the ten.
    let rig = armed_rig("batch-loop");

    let mut last = None;
    for i in 0..12 {
        last = Some(rig.writer.apply_tune(
            &[setting("crankingRPM", 300.0 + f64::from(i))],
            &[],
            &format!("nudge {i}"),
            false,
        ));
    }

    let refusal = last.expect("ran").refusal.expect("refused");
    assert!(refusal.reason.contains("too many writes"));
}

#[test]
fn a_batch_cannot_smuggle_past_the_per_item_guards() {
    // The rate limit stands down inside a batch; the magnitude, dangerous and idle guards must
    // not.
    let rig = armed_rig("batch-guards");

    let result = rig.writer.apply_tune(
        &[setting("crankingRPM", 9000.0)],
        &[cell("VE Table", 0, 0, 250.0)],
        "everything too large",
        false,
    );

    assert!(result.applied.is_empty());
    assert_eq!(
        result.rejected.iter().filter(|r| r.contains("too large")).count(),
        2,
        "{:?}",
        result.rejected
    );
    assert!(rig.board.writes().is_empty());
}

// ----- RPM ---------------------------------------------------------------------------------

#[test]
fn a_write_is_refused_while_the_engine_is_running_above_idle() {
    let rig = armed_rig("rpm");
    rig.host.set_rpm(Some(3000.0));

    let refused = rig
        .writer
        .set_setting("crankingRPM", 310.0, "small nudge", false)
        .expect("refused");

    assert!(refused.reason.contains("running above idle"));
    assert!(refused.detail.starts_with("RPM reads 3000, above the 1200 rpm guard."));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn every_write_entry_point_honours_the_idle_gate() {
    let rig = armed_rig("rpm-all");
    rig.host.set_rpm(Some(1200.5));

    assert!(rig.writer.set_setting("crankingRPM", 310.0, "s", false).is_some());
    assert!(
        rig.writer
            .set_table_cell("VE Table", 0, 0, 5.0, "c", false)
            .refusal
            .is_some()
    );
    let batch = rig.writer.apply_tune(&[setting("crankingRPM", 310.0)], &[], "b", false);
    assert!(batch.refusal.expect("refused").reason.contains("running above idle"));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn exactly_at_the_guard_is_idle() {
    let rig = armed_rig("rpm-edge");
    rig.host.set_rpm(Some(1200.0));

    assert!(
        rig.writer
            .set_setting("crankingRPM", 310.0, "at the edge", false)
            .is_none()
    );
}

#[test]
fn a_write_goes_through_at_idle() {
    let rig = armed_rig("rpm-idle");
    rig.host.set_rpm(Some(0.0));

    assert!(
        rig.writer
            .set_setting("crankingRPM", 310.0, "small nudge", false)
            .is_none()
    );
}

#[test]
fn a_write_is_not_refused_for_rpm_when_there_is_nothing_to_read_it_from() {
    // No sample has ever been taken, so there is nothing to read RPM off at all. This must not
    // be mistaken for "the engine is running", or every write over a fresh connection would be
    // refused.
    let rig = armed_rig("rpm-none");
    rig.host.set_rpm(None);

    assert!(
        rig.writer
            .set_setting("crankingRPM", 310.0, "small nudge", false)
            .is_none()
    );
}

#[test]
fn a_reading_that_is_not_a_number_does_not_stand_for_a_running_engine() {
    let rig = armed_rig("rpm-nan");
    rig.host.set_rpm(Some(f64::NAN));

    assert!(
        rig.writer
            .set_setting("crankingRPM", 310.0, "small nudge", false)
            .is_none()
    );
}

#[test]
fn a_saved_log_opened_while_connected_does_not_decide_whether_the_engine_is_running() {
    // Opening a file does not end the session, so for as long as the guard read the window's
    // current document it was judging the engine by the last row of somebody's old log. Here the
    // only source is the live session, so a file ending at 6,000 rpm has no way to block a write
    // to a controller that is sitting at idle.
    let rig = armed_rig("rpm-log");
    rig.host.set_rpm(Some(0.0));

    assert!(
        rig.writer
            .set_setting("crankingRPM", 310.0, "small nudge", false)
            .is_none()
    );
}

// ----- names ---------------------------------------------------------------------------------

#[test]
fn a_write_to_a_setting_the_firmware_does_not_have_is_refused_by_name() {
    let rig = armed_rig("no-such-setting");

    let refused = rig
        .writer
        .set_setting("noSuchThing", 1.0, "reason", false)
        .expect("refused");

    assert_eq!(refused.reason, "no such setting");
    assert_eq!(refused.detail, "This firmware declares no \"noSuchThing\".");
}

#[test]
fn a_cell_outside_the_table_is_refused_with_its_size() {
    let rig = armed_rig("cell-outside");

    let refused = rig
        .writer
        .set_table_cell("VE Table", 5, 0, 5.0, "reason", false)
        .refusal
        .expect("refused");

    assert_eq!(refused.reason, "that cell is not in the table");
    assert_eq!(refused.detail, "\"VE Table\" is 2 by 2.");

    let negative = rig
        .writer
        .set_table_cell("VE Table", 0, -1, 5.0, "reason", false)
        .refusal
        .expect("refused");
    assert_eq!(negative.reason, "that cell is not in the table");
    assert!(rig.board.writes().is_empty());
}

#[test]
fn a_table_is_found_by_name_without_regard_to_case_and_an_unknown_one_is_refused() {
    let rig = armed_rig("table-name");

    assert!(
        rig.writer
            .set_table_cell("ve table", 0, 0, 5.0, "reason", false)
            .refusal
            .is_none()
    );

    let refused = rig
        .writer
        .set_table_cell("Nope", 0, 0, 5.0, "reason", false)
        .refusal
        .expect("refused");
    assert_eq!(refused.reason, "no such table");
}

// ----- nothing burns -------------------------------------------------------------------------

#[test]
fn nothing_an_agent_does_is_ever_burned() {
    let rig = armed_rig("no-burn");

    rig.writer.set_setting("crankingRPM", 310.0, "s", false);
    rig.writer.set_table_cell("VE Table", 0, 0, 5.0, "c", false);
    rig.writer.apply_tune(
        &[setting("crankingRPM", 320.0)],
        &[cell("VE Table", 1, 0, 6.0)],
        "b",
        false,
    );

    assert_eq!(rig.board.burns(), 0);
    assert!(rig.board.flash().is_none());
}

// ----- propose: never touches the ECU ----------------------------------------------------------

#[test]
fn proposing_needs_no_arming_and_moves_nothing_on_the_controller() {
    let rig = rig("propose");
    assert!(!rig.writer.is_armed());

    let result = rig.writer.propose_tune(&[setting("crankingRPM", 400.0)], &[]);

    assert_eq!(result.changes.len(), 1);
    let change = &result.changes[0];
    assert_eq!(change.name, "crankingRPM");
    assert_eq!(change.mine, Some(400.0));
    assert_eq!(change.theirs, Some(300.0));

    // Nothing went over the wire: the byte the controller holds is the one it started with.
    assert_eq!(rig.board.page()[0], 0x01);
    assert_eq!(rig.board.page()[1], 0x2C);
    assert!(rig.board.writes().is_empty());
}

#[test]
fn proposing_an_unknown_setting_is_rejected_by_name_rather_than_throwing() {
    let rig = rig("propose-unknown");

    let result = rig.writer.propose_tune(&[setting("noSuchThing", 1.0)], &[]);

    assert!(result.changes.is_empty());
    assert!(result.rejected.iter().any(|r| r.contains("no such setting")));
}

#[test]
fn proposing_a_cell_outside_the_table_is_rejected_with_its_size() {
    let rig = rig("propose-outside");

    let result = rig.writer.propose_tune(&[], &[cell("VE Table", 99, 0, 50.0)]);

    assert!(result.changes.is_empty());
    assert!(result.rejected.iter().any(|r| r.contains("not in the table")));
}

#[test]
fn proposing_a_table_cell_reports_the_difference_against_the_live_table() {
    let rig = rig("propose-cell");

    let result = rig.writer.propose_tune(&[], &[cell("VE Table", 0, 0, 55.0)]);

    assert_eq!(result.changes.len(), 1);
    assert_eq!(result.changes[0].mine, Some(55.0));
    assert_eq!(result.changes[0].theirs, Some(0.0)); // the board's veTable bytes default to nought
}

#[test]
fn proposing_with_no_tune_says_so() {
    let rig = rig("propose-none");
    rig.writer.set_tune(None);

    let result = rig.writer.propose_tune(&[setting("crankingRPM", 1.0)], &[]);

    assert!(result.is_empty());
    assert_eq!(result.rejected, vec!["no tune has been read".to_string()]);
}

// ----- apply: the same write path a lone call already uses ------------------------------------

#[test]
fn applying_is_refused_until_writes_are_armed_and_nothing_moves() {
    let rig = rig("apply-unarmed");

    let result = rig
        .writer
        .apply_tune(&[setting("crankingRPM", 400.0)], &[], "testing", false);

    assert!(result.refusal.expect("refused").reason.contains("not armed"));
    assert_eq!(rig.board.page()[1], 0x2C);
}

#[test]
fn applying_is_refused_with_no_rationale_even_when_armed() {
    let rig = armed_rig("apply-norationale");

    let result = rig.writer.apply_tune(&[setting("crankingRPM", 400.0)], &[], "", false);

    assert!(result.refusal.expect("refused").reason.contains("no rationale"));
    assert_eq!(rig.board.page()[1], 0x2C);
}

#[test]
fn applying_once_armed_reaches_the_controller_and_reports_what_changed() {
    let rig = armed_rig("apply");

    let result = rig
        .writer
        .apply_tune(&[setting("crankingRPM", 400.0)], &[], "testing", false);

    assert!(result.refusal.is_none());
    assert_eq!(result.applied.len(), 1);
    assert_eq!(result.summary, "Applied 1 change.");

    // 400 rpm, big-endian, at the offset the firmware declares: the same bytes a person changing
    // it in the dialog would have sent.
    assert_eq!(rig.board.page()[0], 0x01);
    assert_eq!(rig.board.page()[1], 0x90);
}

#[test]
fn applying_a_setting_and_a_cell_together_applies_both() {
    let rig = armed_rig("apply-both");

    let result = rig.writer.apply_tune(
        &[setting("revLimit", 7000.0)],
        &[cell("VE Table", 0, 0, 55.0)],
        "testing",
        true,
    );

    assert!(result.refusal.is_none());
    assert_eq!(result.applied.len(), 2);
    assert_eq!(rig.board.page()[2], 0x1B);
    assert_eq!(rig.board.page()[3], 0x58); // 7000 = 0x1B58
    assert_eq!(rig.board.page()[16], 55); // veTable[0,0]
}

#[test]
fn applying_with_nothing_valid_refuses_no_write_but_says_why_on_each() {
    let rig = armed_rig("apply-invalid");

    let result = rig
        .writer
        .apply_tune(&[setting("noSuchThing", 1.0)], &[], "testing", false);

    assert!(result.refusal.is_none());
    assert!(result.applied.is_empty());
    assert!(!result.rejected.is_empty());
    assert!(result.summary.starts_with("Nothing valid to apply."));
    assert!(rig.board.writes().is_empty());
}

#[test]
fn applying_what_the_ecu_already_holds_changes_nothing() {
    let rig = armed_rig("apply-same");

    let result = rig
        .writer
        .apply_tune(&[setting("crankingRPM", 300.0)], &[], "testing", false);

    assert!(result.applied.is_empty());
    assert_eq!(
        result.summary,
        "Nothing would change; the ECU already holds these values."
    );
    assert!(rig.board.writes().is_empty());
}

#[test]
fn applying_with_no_project_open_still_writes_and_does_not_fail_on_that_account() {
    // Bookkeeping must never turn a successful write into a reported failure.
    let rig = armed_rig("apply-noproject");

    let result = rig
        .writer
        .apply_tune(&[setting("crankingRPM", 400.0)], &[], "testing", false);

    assert!(result.refusal.is_none());
    assert_eq!(result.applied.len(), 1);
}

#[test]
fn applying_keeps_a_version_describing_what_was_applied() {
    let rig = armed_rig("apply-keep");

    rig.writer.apply_tune(
        &[setting("crankingRPM", 400.0)],
        &[],
        "raising it for the dyno pull",
        false,
    );

    let kept = rig.host.kept();
    assert_eq!(kept.len(), 1);
    assert!(kept[0].contains("raising it for the dyno pull"));
    assert!(kept[0].contains("crankingRPM"));
}

#[test]
fn a_refused_apply_keeps_no_version() {
    let rig = armed_rig("apply-nokeep");
    rig.host.set_rpm(Some(4000.0));

    rig.writer
        .apply_tune(&[setting("crankingRPM", 400.0)], &[], "refused", false);

    assert!(rig.host.kept().is_empty());
}

// ----- staging: a second, independent path, never gated ---------------------------------------

#[test]
fn staging_the_tune_in_hand_needs_no_arming_and_writes_an_msq_under_the_workspace() {
    let rig = rig("stage");
    assert!(!rig.writer.is_armed());

    let result = rig.writer.stage_tune(&[], &[], "");

    assert!(result.refusal.is_none(), "{:?}", result.refusal);
    let file = result.file.expect("staged");
    assert!(std::path::Path::new(&file.path).exists());
    assert!(file.path.starts_with(&*rig.workspace.staging().to_string_lossy()));
    assert!(file.name.starts_with("tune-") && file.name.ends_with(".msq"));

    let content = std::fs::read_to_string(&file.path).unwrap();
    assert!(content.contains("crankingRPM"));
}

#[test]
fn staging_with_proposed_changes_stages_the_hypothetical_tune_not_the_live_one() {
    let rig = rig("stage-hypothetical");

    let result = rig
        .writer
        .stage_tune(&[setting("crankingRPM", 999.0)], &[], "hypothetical.msq");

    assert!(result.refusal.is_none());
    let content = std::fs::read_to_string(result.file.expect("staged").path).unwrap();
    assert!(content.contains("999"));

    // The live tune, and the controller behind it, never moved.
    assert_eq!(rig.board.page()[0], 0x01);
    assert_eq!(rig.board.page()[1], 0x2C);
    assert!(!rig.writer.is_armed());
    assert!(rig.board.writes().is_empty());
}

#[test]
fn staging_with_no_tune_in_hand_is_refused_rather_than_writing_an_empty_file() {
    let rig = rig("stage-none");
    rig.writer.set_tune(None);

    let result = rig.writer.stage_tune(&[], &[], "");

    assert!(result.refusal.is_some());
    assert!(result.file.is_none());
    assert!(rig.writer.list_staged().is_empty());
}

#[test]
fn staging_a_table_writes_a_csv_under_the_workspace() {
    let rig = rig("stage-table");

    let result = rig.writer.stage_table("VE Table", "");

    assert!(result.refusal.is_none());
    let file = result.file.expect("staged");
    assert!(std::path::Path::new(&file.path).exists());
    assert!(file.path.starts_with(&*rig.workspace.staging().to_string_lossy()));
    assert!(std::fs::read_to_string(&file.path).unwrap().starts_with("VE Table,"));
    assert!(file.name.starts_with("VE-Table-") && file.name.ends_with(".csv"));
}

#[test]
fn staging_an_unknown_table_is_refused_by_name() {
    let rig = rig("stage-unknown");

    let result = rig.writer.stage_table("Nope", "");

    assert_eq!(result.refusal.expect("refused").reason, "no such table");
}

#[test]
fn listing_staged_files_shows_everything_just_written() {
    let rig = rig("stage-list");

    rig.writer.stage_tune(&[], &[], "one.msq");
    rig.writer.stage_table("VE Table", "two.csv");

    let staged = rig.writer.list_staged();

    assert!(staged.iter().any(|f| f.name == "one.msq"));
    assert!(staged.iter().any(|f| f.name == "two.csv"));
}

#[test]
fn a_staged_name_cannot_leave_the_staging_folder() {
    // The path safety is SafeFolder's, and staging is the agent's only file-writing route: every
    // way of naming somewhere else is refused as a bad file name, and nothing is written.
    let rig = rig("stage-escape");
    let outside = rig.workspace.root().join("escaped.msq");

    for name in [
        "../escaped.msq",
        "..\\escaped.msq",
        "sub/escaped.msq",
        "sub\\escaped.msq",
        "../../etc/passwd",
    ] {
        let tune = rig.writer.stage_tune(&[], &[], name);
        assert_eq!(
            tune.refusal.expect("refused").reason,
            "could not stage that file",
            "{name}"
        );

        let table = rig.writer.stage_table("VE Table", name);
        assert_eq!(
            table.refusal.expect("refused").reason,
            "could not stage that file",
            "{name}"
        );
    }

    let absolute = rig.workspace.staging().join("tune.msq");
    let refused = rig.writer.stage_tune(&[], &[], &absolute.to_string_lossy());
    assert_eq!(refused.refusal.expect("refused").reason, "could not stage that file");

    assert!(!outside.exists());
    assert!(!rig.workspace.staging().join("tune.msq").exists());
}

#[test]
fn staging_never_touches_the_controller_even_when_armed() {
    let rig = armed_rig("stage-armed");

    rig.writer
        .stage_tune(&[setting("revLimit", 9000.0)], &[cell("VE Table", 0, 0, 9.0)], "x.msq");
    rig.writer.stage_table("VE Table", "x.csv");

    assert!(rig.board.writes().is_empty());
    assert_eq!(rig.board.burns(), 0);
}

// ----- tune reads -----------------------------------------------------------------------------

#[test]
fn the_tune_is_read_by_name_and_a_placeholder_reads_as_nothing() {
    let rig = rig("reads");

    let values = rig.writer.tune_values();
    assert!(values.iter().any(|(n, v)| n == "crankingRPM" && *v == 300.0));
    assert!(values.iter().any(|(n, v)| n == "revLimit" && *v == 6500.0));
    assert_eq!(
        rig.writer.table_names(),
        vec!["VE Table".to_string(), "Launch Table".to_string()]
    );

    let full = rig.writer.tune_full();
    let cranking = full.settings.iter().find(|s| s.name == "crankingRPM").expect("listed");
    assert_eq!(
        (cranking.units.as_str(), cranking.low, cranking.high),
        ("rpm", Some(0.0), Some(10000.0))
    );
    assert_eq!(full.tables.len(), 2);

    rig.writer.set_tune(Some(TuneInHand {
        placeholder: true,
        ..tune_in_hand(&rig.board)
    }));
    assert!(rig.writer.tune_values().is_empty());
    assert!(rig.writer.tune_full().settings.is_empty());
}

#[test]
fn the_limits_are_stated_up_front() {
    let rig = rig("limits");

    let limits = rig.writer.limits();

    assert_eq!(limits.write_rate_count, 10);
    assert_eq!(limits.write_rate_window_seconds, 5.0);
    assert_eq!(limits.max_change_fraction_of_range, 0.5);
    assert!(!limits.burns);
    assert!(limits.writes_armed_clears_on_disconnect && limits.dangerous_settings_need_confirmation);
}

#[test]
fn two_agents_writing_at_once_cannot_race_the_rate_limit() {
    // The server hands every request its own thread, so two writes really can arrive together.
    // Twenty attempts from four threads must land exactly ten.
    let rig = armed_rig("race");
    let landed = Arc::new(std::sync::atomic::AtomicUsize::new(0));

    let threads: Vec<_> = (0..4)
        .map(|t| {
            let writer = Arc::clone(&rig.writer);
            let landed = Arc::clone(&landed);
            std::thread::spawn(move || {
                for i in 0..5 {
                    let value = 301.0 + f64::from(t * 5 + i);
                    if writer.set_setting("crankingRPM", value, "race", false).is_none() {
                        landed.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                    }
                }
            })
        })
        .collect();

    for t in threads {
        t.join().unwrap();
    }

    assert_eq!(landed.load(std::sync::atomic::Ordering::SeqCst), 10);
    assert_eq!(rig.board.writes().len(), 10);
}

#[test]
fn what_an_agent_sends_is_tagged_as_the_agents_on_the_wire() {
    // The wire trace is what tells "the AI asked for this" from a person clicking a button, since
    // the two look identical on the wire otherwise.
    use olv_core::model::{WireOrigin, WireOutcome};

    let rig = armed_rig("origin");

    rig.writer.set_setting("crankingRPM", 310.0, "s", false);
    rig.writer.set_table_cell("VE Table", 0, 0, 5.0, "c", false);
    rig.writer.apply_tune(
        &[setting("crankingRPM", 320.0)],
        &[cell("VE Table", 1, 0, 6.0)],
        "b",
        false,
    );

    let events = rig.trace.recent(100);
    assert!(events.len() >= 6, "{} events", events.len());
    assert!(events.iter().all(|e| e.origin == WireOrigin::Agent), "{events:?}");
    assert!(events.iter().all(|e| e.outcome == WireOutcome::Ok));

    // And the tag does not outlive the call: this thread is a person's again.
    assert_eq!(WireOrigin::current(), WireOrigin::Human);
}
