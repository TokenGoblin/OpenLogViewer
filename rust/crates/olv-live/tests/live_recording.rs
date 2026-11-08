//! Starting and stopping a recording without ending the session: the C# `LiveRecordingTests`,
//! plus `CsvExportTests.ARecordingCarriesAByteOrderMarkAndStillReadsBack` (which needed
//! `LiveSession` and so could not run in `olv-core`).
//!
//! These were the same act until now: a session recorded from connect to disconnect and the
//! file was named after the moment you happened to plug in. That is the wrong unit. Somebody
//! connects to check the link works, warms the engine, finds the road, and only then wants a
//! log: everything before that is noise they have to cut off the front afterwards.
//!
//! What is easy to get wrong here is not the file handling. It is the two threads: the poll
//! loop writes rows while the interface starts and stops the writer underneath it.

mod live_doubles;

use std::fs::OpenOptions;
use std::path::Path;
use std::time::Duration;

use live_doubles::{FixedSource, TempFiles, Ticker, wait_for};
use olv_core::logs::log_reader_factory;
use olv_live::live::live_session::{LiveSession, LiveSessionSettings};

fn session(settings: Option<LiveSessionSettings>) -> LiveSession {
    LiveSession::new(
        Box::new(Ticker::new()),
        settings.unwrap_or(LiveSessionSettings {
            maximum_rate: 200.0,
            ..LiveSessionSettings::default()
        }),
    )
}

fn recording_to(path: &str) -> Option<LiveSessionSettings> {
    Some(LiveSessionSettings {
        recording_path: Some(path.to_string()),
        maximum_rate: 200.0,
        ..LiveSessionSettings::default()
    })
}

fn until(session: &LiveSession, samples: usize) {
    wait_for(5.0, 5, || session.status().samples >= samples);
}

fn until_recorded(session: &LiveSession, rows: i32) {
    wait_for(5.0, 5, || session.recorded_rows() >= rows);
}

/// The file's lines, read while it may still be open for writing.
fn rows(path: &str) -> Vec<String> {
    let text = std::fs::read_to_string(path).unwrap();

    text.split('\n')
        .filter(|l| !l.is_empty())
        .map(|l| l.trim_end_matches('\r').to_string())
        .collect()
}

fn first_field(row: &str) -> f64 {
    row.split(',').next().unwrap().parse().unwrap()
}

// ----- watching without writing -------------------------------------------

/// A session with no path given watches and writes nothing. This is what the "record on connect"
/// preference turns off, and it has to be genuinely off rather than writing somewhere nobody asked
/// for.
#[test]
fn a_session_can_run_without_recording_anything() {
    let session = session(None);
    session.start().unwrap();
    until(&session, 5);

    assert!(!session.is_recording());
    assert_eq!(session.recording_path(), None);
    assert!(session.status().samples >= 5, "the session should still be collecting");
}

/// The whole point: the data keeps arriving whether or not any of it is being written, so the
/// gauges and the plot do not depend on recording.
#[test]
fn stopping_a_recording_leaves_the_session_running() {
    let mut temp = TempFiles::new();
    let path = temp.path("olv-rec");

    let session = session(recording_to(&path));

    session.start().unwrap();
    until_recorded(&session, 5);

    let at_stop = session.status().samples;
    assert_eq!(session.stop_recording().as_deref(), Some(path.as_str()));
    assert!(!session.is_recording());

    until(&session, at_stop + 5);

    assert!(session.is_running(), "the session should outlive the recording");
    assert!(session.status().samples > at_stop, "samples should keep arriving");
}

// ----- starting mid-session -----------------------------------------------

#[test]
fn recording_can_be_started_after_the_session_has_been_running_a_while() {
    let session = session(None);
    session.start().unwrap();
    until(&session, 20);

    let mut temp = TempFiles::new();
    let path = temp.path("olv-rec");
    session.start_recording(&path).unwrap();

    assert!(session.is_recording());
    assert_eq!(session.recording_path().as_deref(), Some(path.as_str()));

    until_recorded(&session, 5);
    session.stop_recording();

    // Header, units, then rows.
    assert!(
        rows(&path).len() >= 7,
        "the recording should hold what arrived after it began"
    );
}

/// The recording's clock starts where the recording does.
///
/// A file that opens at t=418 s because that is when somebody pressed record is not a log of a
/// run. Every tool that reads it, this one included, draws seven minutes of nothing before the
/// first sample, and the person who pressed record at the start line gets a plot whose
/// interesting part is a sliver at the right-hand edge.
#[test]
fn the_recordings_clock_starts_where_the_recording_does() {
    let session = session(None);
    session.start().unwrap();

    // Let real time pass, so a session-relative timestamp would be visibly non-zero rather than
    // merely different.
    std::thread::sleep(Duration::from_millis(400));
    until(&session, 20);

    let mut temp = TempFiles::new();
    let path = temp.path("olv-rec");
    session.start_recording(&path).unwrap();
    until_recorded(&session, 5);
    session.stop_recording();

    let rows = rows(&path);
    let first = first_field(&rows[2]);

    assert!(first < 0.25, "the first row should be near zero, was {first:.3} s");

    let last = first_field(rows.last().unwrap());
    assert!(last >= first, "time should advance through the recording");
}

/// Only what arrived after record was pressed. The buffer is not flushed into it.
#[test]
fn a_recording_holds_what_came_after_it_started() {
    let session = session(None);
    session.start().unwrap();
    until(&session, 40);

    let mut temp = TempFiles::new();
    let path = temp.path("olv-rec");
    session.start_recording(&path).unwrap();
    until_recorded(&session, 5);

    let recorded = session.recorded_rows();
    session.stop_recording();

    assert!(
        recorded < session.status().samples as i32,
        "the recording should be shorter than the session that contains it"
    );
}

// ----- more than one recording --------------------------------------------

#[test]
fn two_recordings_from_one_session_are_two_separate_files() {
    let session = session(None);
    session.start().unwrap();

    let mut temp = TempFiles::new();

    let first = temp.path("olv-rec");
    session.start_recording(&first).unwrap();
    until_recorded(&session, 4);
    session.stop_recording();

    let second = temp.path("olv-rec");
    session.start_recording(&second).unwrap();
    until_recorded(&session, 4);
    session.stop_recording();

    assert!(Path::new(&first).exists());
    assert!(Path::new(&second).exists());

    // Each carries its own header and its own clock, so each opens as a log.
    for path in [&first, &second] {
        let rows = rows(path);

        // The byte-order mark leads the first line.
        assert!(rows[0].trim_start_matches('\u{feff}').starts_with("Time,"));
        assert!(first_field(&rows[2]) < 0.25, "{path} should start near zero");
    }
}

/// Starting a second recording without stopping the first closes the first properly rather than
/// abandoning its handle. An abandoned writer is a file that stays locked and short.
#[test]
fn starting_a_second_recording_closes_the_first() {
    let session = session(None);
    session.start().unwrap();

    let mut temp = TempFiles::new();

    let first = temp.path("olv-rec");
    session.start_recording(&first).unwrap();
    until_recorded(&session, 4);

    let second = temp.path("olv-rec");
    session.start_recording(&second).unwrap();
    until_recorded(&session, 4);

    assert_eq!(session.recording_path().as_deref(), Some(second.as_str()));

    // Readable and complete while the session is still running, which it would not be if the
    // handle had merely been dropped.
    assert!(rows(&first).len() >= 3);

    session.stop_recording();
}

#[test]
fn stopping_when_nothing_is_recording_is_not_an_error() {
    let session = session(None);
    session.start().unwrap();
    until(&session, 3);

    assert_eq!(session.stop_recording(), None);
    assert_eq!(session.stop_recording(), None);
}

/// After stopping, the session still knows where the file went, which is the one thing somebody
/// wants to know immediately after pressing stop.
#[test]
fn the_session_remembers_where_the_recording_went() {
    let mut temp = TempFiles::new();
    let path = temp.path("olv-rec");

    let session = session(None);
    session.start().unwrap();
    session.start_recording(&path).unwrap();
    until_recorded(&session, 3);
    session.stop_recording();

    assert_eq!(session.recording_path(), None);
    assert_eq!(session.last_recording_path().as_deref(), Some(path.as_str()));
}

// ----- the part that would crash the process ------------------------------

/// Recording started and stopped repeatedly while the poll loop is writing.
///
/// The failure this guards against is not a lost row. It is a writer closed between the poll
/// thread's check and its write, which the C# would have thrown from a background thread
/// (ending the process rather than failing the session).
#[test]
fn recording_can_be_toggled_while_the_poll_loop_is_writing() {
    let session = session(None);
    session.start().unwrap();
    until(&session, 5);

    let mut temp = TempFiles::new();
    for _ in 0..40 {
        session.start_recording(&temp.path("olv-rec")).unwrap();
        std::thread::sleep(Duration::from_millis(2));
        session.stop_recording();
    }

    let target = session.status().samples + 5;
    until(&session, target);

    assert!(session.is_running(), "the poll loop should have survived the churn");
    assert_eq!(session.status().error, None);
}

/// Every row a recording claims is a row that is actually in the file. The count is what the status
/// line reports, so a count that outruns the file would be the application lying about what it
/// saved.
#[test]
fn the_row_count_matches_what_is_on_disk() {
    let mut temp = TempFiles::new();
    let path = temp.path("olv-rec");

    let session = session(None);
    session.start().unwrap();
    session.start_recording(&path).unwrap();
    until_recorded(&session, 25);

    // Counted after stopping, not before. The poll thread is still writing until the recording
    // closes, so a count taken first is stale by however many rows arrive in between, and it is the
    // count after stopping that the interface reports as "saved N rows", so that is the one that
    // has to match the file.
    session.stop_recording();
    let claimed = session.recorded_rows();

    // Two lines of header, then one line per row.
    assert_eq!(claimed as usize + 2, rows(&path).len());
}

#[test]
fn ending_the_session_closes_the_recording() {
    let mut temp = TempFiles::new();
    let path = temp.path("olv-rec");

    let session = session(recording_to(&path));

    session.start().unwrap();
    until_recorded(&session, 4);
    session.stop();

    assert!(!session.is_recording());

    // Openable for writing, which only holds if the handle was released. (On Windows a file
    // another handle holds open for writing is not truncatable; this asks for write access.)
    let reopened = OpenOptions::new().read(true).write(true).open(&path).unwrap();
    assert!(reopened.metadata().unwrap().len() > 0);
}

// ----- from CsvExportTests ------------------------------------------------

#[test]
fn a_recording_carries_a_byte_order_mark_and_still_reads_back() {
    // Excel reads a CSV without one in the system codepage whatever is actually in it, so a channel
    // in degrees C arrives garbled. The mark fixes that, and must not cost the application's own
    // reader, which is the half worth asserting.
    let mut temp = TempFiles::new();
    let path = temp.path("olv-bom");

    let session = LiveSession::new(
        Box::new(FixedSource::new(&["Coolant"], &["\u{b0}C"], &[0.0])),
        LiveSessionSettings {
            recording_path: Some(path.clone()),
            maximum_rate: 0.0,
            ..LiveSessionSettings::default()
        },
    );

    session.start().unwrap();
    std::thread::sleep(Duration::from_millis(120));
    session.stop();
    drop(session);

    let bytes = std::fs::read(&path).unwrap();

    assert!(bytes.len() > 3);
    assert_eq!(&bytes[..3], &[0xEF, 0xBB, 0xBF]);

    let reopened = log_reader_factory::load(Path::new(&path)).unwrap();
    let coolant: Vec<_> = reopened.channels().iter().filter(|c| c.name() == "Coolant").collect();

    // The name is the real check: a byte-order mark read as data would make the first column
    // "\u{feff}Time" and nothing would match by name again.
    assert_eq!(coolant.len(), 1);
    assert_eq!(coolant[0].units(), "\u{b0}C");
    assert!(reopened.channels().iter().any(|c| c.name() == "Time"));
}
