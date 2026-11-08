//! The findings of the Opus review of w4-live, each pinned by a test:
//!
//! 1. a stale byte in the input must not turn a refused tune write into an "Ok";
//! 2. letting a session go must be something a reconnect can wait for, boundedly;
//! 3. a recording that cannot be written must not take the session's pacing or the cable with it;
//! 4. the link's tune operations are serialised against the poll, and a stop that times out leaves
//!    ownership where a restart can find it.

mod live_doubles;

use std::collections::VecDeque;
use std::io::Write;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use live_doubles::{Ticker, wait_for};
use olv_core::model::LiveSource;
use olv_core::{Error, Result};
use olv_live::live::live_session::{LiveSession, LiveSessionSettings};
use olv_live::live::maxx_tune::{self, MaxxWriteStatus};
use olv_live::live::maxx_usb_source::MaxxUsbSource;
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use olv_protocol::ms::ms_protocol::EcuTransport;

// ----- 1. a stale byte in the input ------------------------------------------------------------

struct StaleState {
    input: VecDeque<u8>,
    /// Writes of a tune header seen, so the next is the payload.
    awaiting_payload: bool,
    discards: usize,
}

/// An ECU with an input queue that really queues (a reply does not replace an unread one): it
/// acknowledges a write header, and refuses the payload that follows.
#[derive(Clone)]
struct RefusingEcu(Arc<Mutex<StaleState>>);

impl RefusingEcu {
    fn with_stale(stale: &[u8]) -> Self {
        Self(Arc::new(Mutex::new(StaleState {
            input: stale.iter().copied().collect(),
            awaiting_payload: false,
            discards: 0,
        })))
    }
}

impl EcuTransport for RefusingEcu {
    fn is_open(&self) -> bool {
        true
    }

    fn open(&mut self) -> Result<()> {
        Ok(())
    }

    fn close(&mut self) {}

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut s = self.0.lock().unwrap();

        if s.awaiting_payload {
            // The payload: refused (0x30, out of range).
            s.awaiting_payload = false;
            s.input.push_back(0x30);
        } else if data.len() == usb::REQUEST_LENGTH && data[0] == usb::WRITE {
            s.awaiting_payload = true;
            s.input.push_back(usb::OK);
        } else {
            // Anything else is a read whose reply is garbage (bad checksum), left in the input.
            s.input.extend([usb::OK; 261]);
        }

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let mut s = self.0.lock().unwrap();
        let mut taken = 0;

        while taken < buffer.len()
            && let Some(byte) = s.input.pop_front()
        {
            buffer[taken] = byte;
            taken += 1;
        }

        Ok(taken)
    }

    fn discard_input(&mut self) {
        let mut s = self.0.lock().unwrap();
        s.input.clear();
        s.discards += 1;
    }
}

/// A late telemetry reply whose first byte is 0x80 was read as the header's acknowledgement, the
/// real acknowledgement was then read as the payload's, and a payload the ECU had refused was
/// reported as written. The header and payload acknowledgements are the same bare byte and cannot
/// be told apart, so the line is made quiet before each.
#[test]
fn a_stale_acknowledgement_does_not_turn_a_refused_payload_into_ok() {
    let ecu = RefusingEcu::with_stale(&[usb::OK]);

    let status = maxx_tune::write(&mut ecu.clone(), 1000, &[1, 2, 3, 4]).unwrap();

    assert_eq!(status, MaxxWriteStatus::OutOfRange, "the ECU refused the payload");
}

/// The same, with a longer tail of stale bytes, and with none (the ordinary case still works).
#[test]
fn a_clean_line_and_a_long_stale_tail_both_report_what_the_ecu_said() {
    for stale in [vec![], vec![usb::OK; 5], vec![0x80, 0x80, 0x00, 0x80]] {
        let ecu = RefusingEcu::with_stale(&stale);

        assert_eq!(
            maxx_tune::write(&mut ecu.clone(), 0, &[9]).unwrap(),
            MaxxWriteStatus::OutOfRange,
            "stale input {stale:?}"
        );
    }
}

/// A tune read that gives up leaves the last attempt's late reply in the input unless it is
/// cleared; the next request would then read its answer from the middle of it.
#[test]
fn a_tune_read_that_gives_up_leaves_the_input_quiet() {
    let ecu = RefusingEcu::with_stale(&[]);

    let error = maxx_tune::read(&mut ecu.clone(), None, None).unwrap_err();
    assert!(matches!(error, Error::Protocol(_)));

    let s = ecu.0.lock().unwrap();
    assert!(s.input.is_empty(), "{} stale bytes left behind", s.input.len());
    assert!(s.discards >= 1);
}

// ----- a source whose connection closes slowly --------------------------------------------------

/// A source standing for a port: opening it while it is held fails "busy", and dropping it (the
/// close) takes `close_for` before the port is free again, as closing a connection that waits for
/// a request in flight does.
struct Port {
    held: Arc<AtomicBool>,
    close_for: Duration,
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
    /// While set, `read` blocks (a request stuck in flight).
    stuck: Option<Arc<Latch>>,
    closed: Arc<AtomicBool>,
}

struct Latch {
    open: Mutex<bool>,
    wake: Condvar,
}

impl Latch {
    fn new(open: bool) -> Arc<Self> {
        Arc::new(Self {
            open: Mutex::new(open),
            wake: Condvar::new(),
        })
    }

    fn release(&self) {
        *self.open.lock().unwrap() = true;
        self.wake.notify_all();
    }

    fn wait(&self) {
        let open = self.open.lock().unwrap();
        let _unused = self.wake.wait_while(open, |open| !*open).unwrap();
    }
}

impl Port {
    fn new(held: &Arc<AtomicBool>, close_for: Duration) -> Self {
        Self {
            held: Arc::clone(held),
            close_for,
            names: vec!["RPM".into()],
            units: vec!["rpm".into()],
            digits: vec![0],
            stuck: None,
            closed: Arc::new(AtomicBool::new(false)),
        }
    }
}

impl LiveSource for Port {
    fn names(&self) -> &[String] {
        &self.names
    }
    fn units(&self) -> &[String] {
        &self.units
    }
    fn digits(&self) -> &[i32] {
        &self.digits
    }
    fn open(&mut self) -> Result<()> {
        if self.held.swap(true, Ordering::SeqCst) {
            return Err(Error::Io(std::io::Error::other("The port is busy.")));
        }
        Ok(())
    }
    fn read(&mut self) -> Result<Vec<f64>> {
        if let Some(latch) = &self.stuck {
            latch.wait();
        }
        std::thread::sleep(Duration::from_millis(2));
        Ok(vec![1.0])
    }
    fn recover(&mut self) -> Result<()> {
        Ok(())
    }
    fn retries(&self) -> i32 {
        0
    }
}

impl Drop for Port {
    fn drop(&mut self) {
        std::thread::sleep(self.close_for);
        self.held.store(false, Ordering::SeqCst);
        self.closed.store(true, Ordering::SeqCst);
    }
}

fn quick() -> LiveSessionSettings {
    LiveSessionSettings {
        maximum_rate: 200.0,
        ..LiveSessionSettings::default()
    }
}

// ----- 2. letting go, and reopening -----------------------------------------------------------------

/// The C# disposed synchronously, so a reconnect straight after a disconnect never met a port that
/// was still closing. Here the close is off the caller's thread, so the reconnect waits for it:
/// without the wait it is told "busy"; with it, it opens as soon as the close is done.
#[test]
fn reopening_straight_after_a_disconnect_waits_for_the_release_and_is_never_busy() {
    let held = Arc::new(AtomicBool::new(false));

    let first = LiveSession::new(Box::new(Port::new(&held, Duration::from_millis(300))), quick());
    first.start().unwrap();
    wait_for(5.0, 5, || first.status().samples >= 2);

    let clock = Instant::now();
    let released = first.dispose();
    assert!(
        clock.elapsed() < Duration::from_millis(250),
        "dispose must not wait for the close"
    );
    assert!(!released.is_released(), "the port is still closing");

    let second = LiveSession::new(Box::new(Port::new(&held, Duration::ZERO)), quick());

    // Straight away: the port is held, and that is all a caller could learn from it.
    assert!(second.start().is_err(), "the port was still held");

    // Waiting for the release first: it opens.
    second.start_after(&released, Duration::from_secs(5)).unwrap();
    assert!(released.is_released());
    wait_for(5.0, 5, || second.status().samples >= 2);
    assert!(second.is_running());
}

#[test]
fn a_reconnect_that_cannot_wait_long_enough_says_the_previous_connection_is_still_closing() {
    let held = Arc::new(AtomicBool::new(false));

    let first = LiveSession::new(Box::new(Port::new(&held, Duration::from_millis(600))), quick());
    first.start().unwrap();
    let released = first.dispose();

    let second = LiveSession::new(Box::new(Port::new(&held, Duration::ZERO)), quick());

    let clock = Instant::now();
    let error = second.start_after(&released, Duration::from_millis(50)).unwrap_err();

    // Bounded: it came back after about the timeout, not after the close.
    assert!(
        clock.elapsed() < Duration::from_millis(500),
        "waited {:?}",
        clock.elapsed()
    );
    assert!(
        matches!(&error, Error::InvalidOperation(m) if m.contains("Still closing the previous connection")),
        "{error}"
    );
    assert!(!second.is_running());

    // And once it has closed, the same session starts.
    second.start_after(&released, Duration::from_secs(5)).unwrap();
}

/// Dropping a session whose source is slow to close does not make the dropper wait.
#[test]
fn dropping_a_session_does_not_wait_for_a_slow_close() {
    let held = Arc::new(AtomicBool::new(false));
    let source = Port::new(&held, Duration::from_millis(500));
    let closed = Arc::clone(&source.closed);

    let session = LiveSession::new(Box::new(source), quick());
    session.start().unwrap();
    let released = session.released();

    let clock = Instant::now();
    drop(session);

    assert!(
        clock.elapsed() < Duration::from_millis(400),
        "drop took {:?}",
        clock.elapsed()
    );
    assert!(!closed.load(Ordering::SeqCst));
    assert!(released.wait(Duration::from_secs(5)));
    assert!(closed.load(Ordering::SeqCst));
}

// ----- 4. stop timing out, drop and restart ownership ------------------------------------------------

/// A poll thread stuck in a request outlives `stop`'s two seconds. The session says so, will not
/// start a second poll over the same source, and takes the source back when the request ends,
/// after which it starts again.
#[test]
fn a_stop_that_times_out_hands_back_a_handle_and_a_restart_waits_for_the_source() {
    let held = Arc::new(AtomicBool::new(false));
    let latch = Latch::new(false);

    let mut port = Port::new(&held, Duration::ZERO);
    port.stuck = Some(Arc::clone(&latch));

    let session = LiveSession::new(Box::new(port), quick());
    session.start().unwrap();
    wait_for(5.0, 5, || session.is_running());
    std::thread::sleep(Duration::from_millis(50)); // the poll is now inside the stuck read

    let clock = Instant::now();
    let poll = session.stop();
    let waited = clock.elapsed();

    assert!(
        waited >= Duration::from_millis(1900) && waited < Duration::from_secs(4),
        "stop took {waited:?}"
    );
    assert!(!poll.is_done(), "the poll thread is still stuck");
    assert!(
        !session.is_running(),
        "as the C#: the session lets go of a thread it could not join"
    );

    // The source is still in that thread's hands.
    assert!(matches!(session.start(), Err(Error::InvalidOperation(_))));

    latch.release();
    assert!(
        poll.wait(Duration::from_secs(5)),
        "the thread ends once the request does"
    );

    // Back in the session's hands. The port was never closed, so free it as a reconnect would, and
    // the session starts again.
    held.store(false, Ordering::SeqCst);
    session.start().unwrap();
    wait_for(5.0, 5, || session.status().samples >= 2);
    session.stop();
}

/// Dropping a session whose poll thread is stuck: the thread is the last owner of the source, drops
/// it when its request ends, and only then is the session released.
#[test]
fn a_session_dropped_while_its_poll_is_stuck_is_released_when_the_poll_ends() {
    let held = Arc::new(AtomicBool::new(false));
    let latch = Latch::new(false);

    let mut port = Port::new(&held, Duration::ZERO);
    port.stuck = Some(Arc::clone(&latch));
    let closed = Arc::clone(&port.closed);

    let session = LiveSession::new(Box::new(port), quick());
    session.start().unwrap();
    std::thread::sleep(Duration::from_millis(50));

    let released = session.dispose();

    assert!(!released.is_released());
    assert!(!closed.load(Ordering::SeqCst), "still in the stuck thread's hands");

    latch.release();

    assert!(released.wait(Duration::from_secs(5)));
    assert!(closed.load(Ordering::SeqCst));
}

// ----- 3. a recording that cannot be written -----------------------------------------------------------

/// Accepts the header, then fails every write: a disk that has filled up.
struct FullDisk {
    writes_allowed: usize,
    attempts: Arc<AtomicUsize>,
}

impl Write for FullDisk {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        self.attempts.fetch_add(1, Ordering::SeqCst);

        if self.writes_allowed == 0 {
            return Err(std::io::Error::other("There is not enough space on the disk."));
        }
        self.writes_allowed -= 1;
        Ok(buf.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

/// A full disk used to reset the link's failure count and skip the pacing, so the session polled
/// at link speed forever, never errored, and starved anything else that wanted the cable. Now the
/// recording gives up after a few rows, says why, and the session carries on at its own rate.
#[test]
fn a_recording_that_cannot_be_written_stops_itself_and_the_session_keeps_its_pace() {
    let session = LiveSession::new(
        Box::new(Ticker::new()),
        LiveSessionSettings {
            maximum_rate: 100.0,
            ..LiveSessionSettings::default()
        },
    );
    session.start().unwrap();

    let attempts = Arc::new(AtomicUsize::new(0));
    session
        .start_recording_to(
            "a full disk",
            Box::new(FullDisk {
                writes_allowed: 1, // the header
                attempts: Arc::clone(&attempts),
            }),
        )
        .unwrap();
    assert!(session.is_recording());

    wait_for(5.0, 5, || session.status().recording_error.is_some());
    let status = session.status();

    let reason = status
        .recording_error
        .clone()
        .expect("the recording said why it stopped");
    assert!(reason.starts_with("Recording stopped: "), "{reason}");
    assert!(reason.contains("not enough space"), "{reason}");
    assert!(!session.is_recording());
    assert_eq!(session.recording_path(), None);

    // The session is fine: running, not faulted, no link failures counted.
    assert!(session.is_running());
    assert!(!status.faulted());
    assert_eq!(status.failures, 0);

    // A bounded number of tries, not one per poll for ever.
    let tries = attempts.load(Ordering::SeqCst);
    assert!((2..=4).contains(&tries), "{tries} write attempts");

    // And it is still paced: 100 Hz for 400 ms is about 40 samples, where polling at link speed
    // would be thousands.
    let before = session.status().samples;
    std::thread::sleep(Duration::from_millis(400));
    let gained = session.status().samples - before;
    assert!((10..=80).contains(&gained), "{gained} samples in 400 ms at 100 Hz");

    // Recording to a working destination clears the report.
    session
        .start_recording_to("a good one", Box::new(std::io::sink()))
        .unwrap();
    assert_eq!(session.status().recording_error, None);
    assert!(session.is_recording());
    session.stop();
}

// ----- 4. the link is serialised against the poll ----------------------------------------------------------

struct StrictState {
    input: VecDeque<u8>,
    tune: Vec<u8>,
    expecting: Option<(usize, usize)>,
    violations: Vec<String>,
}

/// A MaxxECU that notices when two conversations interleave: a request written while a reply is
/// still unread is a violation, as is a payload that does not follow its header at once. It sends a
/// channel (id 5) with every telemetry round.
#[derive(Clone)]
struct StrictEcu(Arc<Mutex<StrictState>>);

impl StrictEcu {
    fn new() -> Self {
        Self(Arc::new(Mutex::new(StrictState {
            input: VecDeque::new(),
            tune: (0..maxx_tune::BLOB_SIZE).map(|i| ((i * 5) + (i >> 8)) as u8).collect(),
            expecting: None,
            violations: Vec::new(),
        })))
    }
}

fn framed(data: &[u8]) -> Vec<u8> {
    let mut reply = vec![usb::OK];
    reply.extend_from_slice(data);
    reply.extend_from_slice(&maxx_protocol::crc32(&reply).to_le_bytes());
    reply
}

impl EcuTransport for StrictEcu {
    fn is_open(&self) -> bool {
        true
    }

    fn open(&mut self) -> Result<()> {
        Ok(())
    }

    fn close(&mut self) {}

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut s = self.0.lock().unwrap();

        if !s.input.is_empty() {
            let command = data.get(1).copied().unwrap_or(0);
            let unread = s.input.len();
            s.violations.push(format!(
                "a write (command {command:#04x}) while {unread} reply bytes were unread"
            ));
        }

        if let Some((offset, length)) = s.expecting.take() {
            let ok = data.len() == length + 4
                && u32::from_le_bytes(data[length..].try_into().unwrap()) == maxx_protocol::crc32(&data[..length]);

            if ok {
                s.tune[offset..offset + length].copy_from_slice(&data[..length]);
                s.input.push_back(usb::OK);
            } else {
                s.violations
                    .push("a payload that is not the one the header announced".to_string());
                s.input.push_back(0x30);
            }
            return Ok(());
        }

        let command = data[1];
        let offset = usize::from(data[2]) | (usize::from(data[3]) << 8);
        let length = usize::from(data[4]) | (usize::from(data[5]) << 8);

        if data[0] == usb::WRITE {
            s.expecting = Some((offset, length));
            s.input.push_back(usb::OK);
            return Ok(());
        }

        let body = match command {
            usb::HELLO => vec![0],
            usb::WAITING => vec![4, 0],
            usb::TAKE => vec![5, 0, 1, 0],
            maxx_tune::READ_TUNE => s.tune[offset..offset + length].to_vec(),
            0x37 => maxx_tune::checksums_of(&s.tune, offset as i32)
                .unwrap()
                .iter()
                .flat_map(|sum| sum.to_le_bytes())
                .collect(),
            _ => vec![0; length],
        };
        s.input.extend(framed(&body));

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let mut s = self.0.lock().unwrap();
        let mut taken = 0;

        while taken < buffer.len()
            && let Some(byte) = s.input.pop_front()
        {
            buffer[taken] = byte;
            taken += 1;
        }

        Ok(taken)
    }

    fn discard_input(&mut self) {
        self.0.lock().unwrap().input.clear();
    }
}

/// While the poll runs flat out on its own thread, tune reads, writes, read-backs and checksums
/// from other threads each take the cable for the whole exchange: no request is ever written over an
/// unread reply, every write is acknowledged as written, and every read-back matches.
#[test]
fn tune_operations_are_serialised_against_the_poll() {
    let ecu = StrictEcu::new();

    let source = MaxxUsbSource::new(
        Box::new(ecu.clone()),
        Some(std::env::temp_dir().join("olv-no-such.xml")),
    );
    let link = source.link();

    let session = LiveSession::new(
        Box::new(source),
        LiveSessionSettings {
            maximum_rate: 0.0,
            ..LiveSessionSettings::default()
        },
    );
    session.start().unwrap(); // learns the channels: two seconds

    let writer = {
        let link = link.clone();
        std::thread::spawn(move || {
            for i in 0..60u8 {
                let offset = 1000 + i32::from(i) * 8;
                let data = [i, i.wrapping_add(1), 3, 4, 5, 6, 7, 8];

                assert_eq!(
                    link.write_tune(offset, &data).unwrap(),
                    MaxxWriteStatus::Ok,
                    "write {i}"
                );
                assert!(link.verify_tune(offset, &data).unwrap(), "read-back {i}");
            }
        })
    };

    let blob = link.read_tune().unwrap();
    writer.join().unwrap();
    assert_eq!(blob.len(), maxx_tune::BLOB_SIZE);

    // The checksums of what the ECU now holds are what is computed from it.
    let sums = link.tune_checksums().unwrap();
    let held = ecu.0.lock().unwrap().tune.clone();
    assert_eq!(sums, maxx_tune::checksums_of(&held, maxx_tune::WRITE_CEILING).unwrap());

    let samples = session.status().samples;
    session.stop();

    let s = ecu.0.lock().unwrap();
    assert!(
        s.violations.is_empty(),
        "interleaved: {:?}",
        &s.violations[..s.violations.len().min(5)]
    );
    assert!(samples > 0, "the poll ran alongside");
}
