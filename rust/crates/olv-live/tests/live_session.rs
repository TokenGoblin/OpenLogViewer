//! `LiveSession`: the C# `LiveSessionTests`, over a TunerStudio source on scripted transports.
//!
//! The C# `[Theory]` over four exception types becomes four cases of one helper, and the
//! exceptions become the `olv_core::Error` variants and I/O kinds a Rust transport can return
//! (plus a panic, which stands for "whatever the platform throws" and has no C# equivalent: a
//! .NET exception that escapes a thread ends the process, a Rust panic only ends the thread,
//! so the poll loop catches it too).

mod common;
mod live_doubles;

use std::sync::Arc;
use std::time::{Duration, Instant};

use common::{FakeTransport, reply};
use live_doubles::{Fault, FlakyTransport, TempFiles, ThrowingTransport, tuner_session, until, wait_for};
use olv_core::logs::log_reader_factory;
use olv_live::live::live_session::{LiveSession, LiveSessionSettings};
use olv_protocol::ms::ms_protocol::EcuTransport;

const INI: &str = r#"[OutputChannels]
ochBlockSize = 8
seconds  = scalar, U16, 0, "s",   1.000, 0.0
rpm      = scalar, U16, 2, "RPM", 1.000, 0.0
map      = scalar, S16, 4, "kPa", 0.100, 0.0
internal = scalar, U16, 6, "x",   1.000, 0.0

[Datalog]
entry = rpm,     "RPM", int,   "%d"
entry = map,     "MAP", float, "%.1f"
"#;

/// A framed reply carrying one realtime block.
fn block(rpm: i32, map_tenths: i32) -> Vec<u8> {
    reply(&[
        0,
        0,
        (rpm >> 8) as u8,
        (rpm & 0xFF) as u8,
        (map_tenths >> 8) as u8,
        (map_tenths & 0xFF) as u8,
        0,
        0,
    ])
}

fn repeating(block: Vec<u8>) -> FakeTransport {
    let transport = FakeTransport::new();
    transport.with(|s| s.repeating = Some(block));
    transport
}

fn session(transport: impl EcuTransport + 'static, settings: Option<LiveSessionSettings>) -> LiveSession {
    tuner_session(transport, INI, settings)
}

fn settings(f: impl FnOnce(&mut LiveSessionSettings)) -> Option<LiveSessionSettings> {
    let mut s = LiveSessionSettings::default();
    f(&mut s);
    Some(s)
}

#[test]
fn only_the_datalog_channels_are_recorded_and_under_their_log_names() {
    // The block has four fields; the datalog names two of them. Recording everything would give
    // internal names no preset would ever match.
    let session = session(FakeTransport::new(), None);

    assert_eq!(session.names(), ["RPM", "MAP"]);
}

#[test]
fn polling_fills_the_session_and_it_snapshots_as_an_ordinary_log() {
    let session = session(repeating(block(3000, 1000)), None);

    session.start().unwrap();
    until(&session, 5);
    session.stop();

    let document = session.snapshot();

    assert!(document.sample_count() >= 5);
    let names: Vec<&str> = document.channels().iter().map(|c| c.name()).collect();
    assert_eq!(names, ["RPM", "MAP"]);
    assert_eq!(document.format_name(), "Live");
    assert!((document.find_channel("RPM").unwrap().at(0) - 3000.0).abs() < 1e-3);
    assert!((document.find_channel("MAP").unwrap().at(0) - 100.0).abs() < 1e-3);
}

#[test]
fn the_time_base_advances_and_is_usable() {
    let session = session(repeating(block(1000, 500)), None);

    session.start().unwrap();
    until(&session, 6);
    session.stop();

    let document = session.snapshot();

    assert!(document.duration() > 0.0);
    assert!(document.time().at(1) > document.time().at(0));
    assert!(document.median_sample_interval() > 0.0);
}

#[test]
fn a_snapshot_is_reused_until_more_samples_arrive() {
    // The plot asks on every repaint; rebuilding a few hundred columns for a document that has
    // not changed would cost more than drawing it.
    let session = session(repeating(block(1000, 500)), None);

    session.start().unwrap();
    until(&session, 3);
    session.stop();

    assert!(Arc::ptr_eq(&session.snapshot(), &session.snapshot()));
}

#[test]
fn the_recording_reopens_as_a_log() {
    // A session that cannot be reopened is not much of a record.
    let mut temp = TempFiles::new();
    let path = temp.path("olv-live");

    {
        let session = session(
            repeating(block(2500, 800)),
            settings(|s| s.recording_path = Some(path.clone())),
        );
        session.start().unwrap();
        until(&session, 5);
        session.stop();
    }

    let reopened = log_reader_factory::load(std::path::Path::new(&path)).unwrap();

    assert!(reopened.sample_count() >= 5);
    let names: Vec<&str> = reopened.channels().iter().map(|c| c.name()).collect();
    assert_eq!(names, ["Time", "RPM", "MAP"]);
    assert_eq!(reopened.find_channel("RPM").unwrap().units(), "RPM");
    assert_eq!(reopened.find_channel("MAP").unwrap().units(), "kPa");
    assert!((reopened.find_channel("RPM").unwrap().at(0) - 2500.0).abs() < 1e-3);
    assert!((reopened.find_channel("MAP").unwrap().at(0) - 80.0).abs() < 1e-3);
}

#[test]
fn the_recording_is_written_as_it_goes_rather_than_at_the_end() {
    // A pulled cable or a sleeping laptop should cost the row in hand, not the session.
    let mut temp = TempFiles::new();
    let path = temp.path("olv-live");

    let session = session(
        repeating(block(1200, 400)),
        settings(|s| s.recording_path = Some(path.clone())),
    );
    session.start().unwrap();
    until(&session, 5);

    // Read while it is still running, without stopping it.
    let size = std::fs::metadata(&path).unwrap().len();
    assert!(size > 0, "nothing had reached disk while the session was running");

    session.stop();
}

#[test]
fn a_run_of_failures_ends_the_session_and_says_why() {
    let session = session(
        FakeTransport::new(), // never replies
        settings(|s| {
            s.failures_before_stopping = 3;
            s.reconnect_for = Duration::ZERO;
        }),
    );

    session.start().unwrap();

    wait_for(5.0, 20, || !session.is_running());

    assert!(!session.is_running());
    assert!(session.status().faulted());
    assert!(session.status().error.unwrap().contains("stopped responding"));
}

#[test]
fn one_bad_block_does_not_end_the_session() {
    // Ordinary on a radio link. The connection retries beneath this, so the session should not
    // even see it.
    let mut corrupt = block(3000, 1000);
    *corrupt.last_mut().unwrap() ^= 0xFF;

    let transport = repeating(block(3000, 1000));
    transport.enqueue([corrupt]);

    let session = session(transport, None);
    session.start().unwrap();
    until(&session, 4);
    session.stop();

    assert!(!session.status().faulted());
    assert!(session.status().samples >= 4);
}

#[test]
fn the_view_is_trimmed_but_the_recording_is_not() {
    // A long session must not grow without bound in memory, and must still leave the whole run
    // on disk.
    let mut temp = TempFiles::new();
    let path = temp.path("olv-live");

    let session = session(
        repeating(block(1000, 500)),
        settings(|s| {
            s.recording_path = Some(path.clone());
            s.maximum_samples = 4;
        }),
    );

    session.start().unwrap();
    until(&session, 12);
    session.stop();

    assert_eq!(session.snapshot().sample_count(), 4);
    assert!(
        log_reader_factory::load(std::path::Path::new(&path))
            .unwrap()
            .sample_count()
            >= 10
    );
}

// ----- pacing -----------------------------------------------------------

#[test]
fn a_session_is_held_to_its_rate() {
    // The link here answers instantly and would otherwise be polled tens of thousands of times a
    // second. The bound is what matters, so it is asserted generously in the direction a loaded
    // machine can miss.
    let session = session(repeating(block(3000, 1000)), settings(|s| s.maximum_rate = 20.0));

    session.start().unwrap();
    std::thread::sleep(Duration::from_millis(600));
    session.stop();

    let samples = session.status().samples;

    assert!((2..=20).contains(&samples), "{samples} samples");
}

#[test]
fn an_uncapped_session_is_much_faster() {
    // Proves the cap is doing the work rather than the fake being slow.
    let session = session(repeating(block(3000, 1000)), settings(|s| s.maximum_rate = 0.0));

    session.start().unwrap();
    std::thread::sleep(Duration::from_millis(300));
    session.stop();

    assert!(
        session.status().samples > 100,
        "an uncapped session managed only {} samples",
        session.status().samples
    );
}

#[test]
fn stopping_is_not_held_up_by_the_wait_between_samples() {
    // A session at 1 Hz must not take a second to stop, which is what a plain sleep between polls
    // would cost.
    let session = session(repeating(block(3000, 1000)), settings(|s| s.maximum_rate = 1.0));

    session.start().unwrap();
    until(&session, 1);

    let clock = Instant::now();
    session.stop();
    let elapsed = clock.elapsed();

    assert!(elapsed < Duration::from_millis(500), "stopping took {elapsed:?}");
}

#[test]
fn a_rate_slower_than_the_link_still_records_every_sample() {
    // Pacing must not drop samples, only space them out.
    let mut temp = TempFiles::new();
    let path = temp.path("olv-live");

    let session = session(
        repeating(block(2500, 800)),
        settings(|s| {
            s.recording_path = Some(path.clone());
            s.maximum_rate = 50.0;
        }),
    );

    session.start().unwrap();
    until(&session, 5);
    session.stop();

    assert_eq!(
        session.status().samples,
        log_reader_factory::load(std::path::Path::new(&path))
            .unwrap()
            .sample_count()
    );
}

fn recovering(seconds: f64) -> Option<LiveSessionSettings> {
    settings(|s| {
        s.failures_before_stopping = 2;
        s.reconnect_for = Duration::from_secs_f64(seconds);
        s.reconnect_every = Duration::from_millis(30);
    })
}

#[test]
fn a_link_that_comes_back_resumes_the_same_session() {
    // Key off, key on. The session should carry on rather than end, because an ECU rebooting is not
    // a reason to lose everything after it.
    let transport = FlakyTransport::new(block(2000, 600));
    let session = session(transport.clone(), recovering(5.0));

    session.start().unwrap();
    until(&session, 3);

    let before = session.status().samples;
    transport.set_down(Some(Fault::Io("device removed")));

    wait_for(3.0, 10, || session.status().reconnecting);
    assert!(
        session.status().reconnecting,
        "the session never reported that it was waiting"
    );

    transport.set_down(None);
    until(&session, before + 3);
    session.stop();

    assert!(!session.status().faulted());
    assert!(!session.status().reconnecting);
    assert!(session.status().samples > before);
}

#[test]
fn a_second_drop_recovers_like_the_first() {
    // The bug this was reported as: the first unplug was survived and the second was not, because a
    // port that is already shut throws something different on the way out than one that dies
    // mid-read.
    let transport = FlakyTransport::new(block(2000, 600));
    let session = session(transport.clone(), recovering(5.0));

    session.start().unwrap();
    until(&session, 2);

    for drop in [Fault::Io("mid-read"), Fault::InvalidOperation("port is not open")] {
        let before = session.status().samples;

        transport.set_down(Some(drop.clone()));
        wait_for(3.0, 10, || session.status().reconnecting);

        assert!(session.status().reconnecting, "did not wait after {drop:?}");

        transport.set_down(None);
        until(&session, before + 2);

        assert!(!session.status().faulted());
    }

    session.stop();
    assert!(transport.opens() >= 3); // the first open plus one per recovery
}

#[test]
fn recovery_closes_before_reopening() {
    // A handle whose device has gone still reports itself open, so reopening without closing first
    // does nothing at all.
    let transport = FlakyTransport::new(block(1000, 400));
    let session = session(transport.clone(), recovering(5.0));

    session.start().unwrap();
    until(&session, 2);

    transport.set_down(Some(Fault::ObjectDisposed("SerialPort")));
    wait_for(3.0, 10, || session.status().reconnecting);

    transport.set_down(None);
    let target = session.status().samples + 2;
    until(&session, target);
    session.stop();

    assert!(transport.closes() >= 1, "the link was reopened without being closed");
}

#[test]
fn a_link_that_never_comes_back_ends_the_session_with_a_reason() {
    let transport = FlakyTransport::new(block(1000, 400));
    let session = session(transport.clone(), recovering(0.4));

    session.start().unwrap();
    until(&session, 2);
    transport.set_down(Some(Fault::Io("gone for good")));

    wait_for(6.0, 20, || !session.is_running());

    assert!(!session.is_running());
    assert!(session.status().error.unwrap().contains("did not come back"));
}

#[test]
fn retrying_slows_down_rather_than_hammering() {
    // A link that does not come back at once is not helped by being asked harder. An OBD2 dongle
    // takes one client at a time and needs a moment to release the last one, so a fresh connect and
    // a reset arriving every few hundred milliseconds is a way to wedge the thing being reached.
    let transport = FlakyTransport::new(block(1000, 400));

    let session = session(
        transport.clone(),
        settings(|s| {
            s.failures_before_stopping = 2;
            s.reconnect_for = Duration::from_secs_f64(1.5);
            s.reconnect_every = Duration::from_millis(50);
            s.reconnect_at_most = Duration::from_millis(300);
        }),
    );

    session.start().unwrap();
    until(&session, 2);

    let before = transport.closes();
    transport.set_down(Some(Fault::Io("gone for good")));

    wait_for(8.0, 20, || !session.is_running());

    // 50 ms flat across a second and a half is thirty attempts; doubling to a 300 ms ceiling is
    // nearer seven. The bound is loose because the point is the shape, not a count: what must not
    // happen is the flat rate.
    let attempts = transport.closes() - before;

    assert!(!session.is_running());
    assert!(attempts > 0 && attempts < 15, "{attempts} reconnect attempts in 1.5 s");
}

#[test]
fn recovery_can_be_turned_off() {
    let transport = FlakyTransport::new(block(1000, 400));
    let session = session(
        transport.clone(),
        settings(|s| {
            s.failures_before_stopping = 2;
            s.reconnect_for = Duration::ZERO;
        }),
    );

    session.start().unwrap();
    until(&session, 2);
    transport.set_down(Some(Fault::Io("gone")));

    wait_for(5.0, 20, || !session.is_running());

    assert!(!session.is_running());
    assert!(session.status().error.unwrap().contains("stopped responding"));
}

#[test]
fn stopping_while_waiting_for_the_ecu_does_not_hang() {
    // Disconnect has to work during a recovery wait, not after it.
    let transport = FlakyTransport::new(block(1000, 400));
    let session = session(transport.clone(), recovering(30.0));

    session.start().unwrap();
    until(&session, 2);
    transport.set_down(Some(Fault::Io("gone")));

    wait_for(3.0, 10, || session.status().reconnecting);

    let clock = Instant::now();
    session.stop();
    let elapsed = clock.elapsed();

    assert!(!session.is_running());
    assert!(elapsed < Duration::from_secs(3), "stopping took {elapsed:?}");
}

#[test]
fn the_ecu_going_away_ends_the_session_rather_than_the_process() {
    // Switching an ECU off takes its USB adapter with it, and a serial port whose device has gone
    // throws whatever it likes. The poll loop runs on a background thread, where anything escaping
    // terminates the process, so this must be caught however unlikely the type looks.
    let transport = ThrowingTransport::new(Fault::ObjectDisposed("SerialPort"));

    let session = session(
        transport,
        settings(|s| {
            s.failures_before_stopping = 2;
            s.reconnect_for = Duration::ZERO;
        }),
    );

    session.start().unwrap();

    wait_for(5.0, 20, || !session.is_running());

    assert!(!session.is_running());
    assert!(session.status().faulted());

    // Stopped at once rather than after fifty tries: a handle that no longer exists will not start
    // existing again.
    assert!(session.status().error.unwrap().contains("stopped responding"));
}

fn no_exception_escapes_the_poll_thread(kind: Fault) {
    let session = session(
        ThrowingTransport::new(kind),
        settings(|s| {
            s.failures_before_stopping = 3;
            s.reconnect_for = Duration::ZERO;
        }),
    );

    session.start().unwrap();

    wait_for(5.0, 20, || !session.is_running());

    assert!(!session.is_running());
    assert!(session.status().faulted());
}

#[test]
fn no_exception_from_the_transport_escapes_the_poll_thread_argument_out_of_range() {
    no_exception_escapes_the_poll_thread(Fault::ArgumentOutOfRange("the timeout"));
}

#[test]
fn no_exception_from_the_transport_escapes_the_poll_thread_invalid_operation() {
    no_exception_escapes_the_poll_thread(Fault::InvalidOperation("not open"));
}

#[test]
fn no_exception_from_the_transport_escapes_the_poll_thread_unauthorized_access() {
    no_exception_escapes_the_poll_thread(Fault::Unauthorized("access denied"));
}

#[test]
fn no_exception_from_the_transport_escapes_the_poll_thread_not_supported() {
    no_exception_escapes_the_poll_thread(Fault::NotSupported("not supported"));
}

#[test]
fn no_exception_from_the_transport_escapes_the_poll_thread_a_panic() {
    // Not in the C#: a panic is what a Rust driver bug looks like, and it must end the session
    // the same way rather than the thread.
    no_exception_escapes_the_poll_thread(Fault::Panic("a driver bug"));
}

#[test]
fn stopping_works_after_the_link_has_already_failed() {
    // The usual way Stop is reached. Closing a port whose device has gone throws, and a disconnect
    // that throws leaves the app wedged.
    let mut transport = ThrowingTransport::new(Fault::ObjectDisposed("SerialPort"));
    transport.throw_on_close = true;

    let session = session(
        transport,
        settings(|s| {
            s.failures_before_stopping = 2;
            s.reconnect_for = Duration::ZERO;
        }),
    );
    session.start().unwrap();
    std::thread::sleep(Duration::from_millis(200));

    session.stop();
    drop(session);
}

#[test]
fn a_session_with_no_recording_path_keeps_no_file() {
    let session = session(repeating(block(1000, 500)), None);

    session.start().unwrap();
    until(&session, 3);
    session.stop();

    assert_eq!(session.recording_path(), None);
    assert!(session.snapshot().sample_count() >= 3);
}
