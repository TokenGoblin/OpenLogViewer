//! Ported from `LiveSession.cs`. Owned by package `w4-live`.
//!
//! Polls an ECU and turns its realtime blocks into a log.
//!
//! The result is an ordinary [`LogDocument`], so everything already built works on a live
//! session unchanged: the plot, the heat table, filters, calculated channels and VE
//! Calibration. Channels take the names the firmware's datalog definition gives them, which are
//! the names a recorded log uses, so a preset saved against a file applies to the ECU as well.
//!
//! Recording is continuous rather than saved at the end. A live session ends when a cable is
//! pulled or a laptop sleeps at least as often as it ends by being stopped, and losing a
//! session to that is worse than the cost of keeping a writer open.
//!
//! **Threads.** One background thread named "ECU poll" owns the source for as long as a session
//! runs; the C# shared the source object, and here it moves into the thread and back. Nothing
//! the window calls waits on the source or on the disk:
//!
//! * `status`, `latest` and `snapshot` take only the sample lock, which the poll thread holds
//!   for the length of a push.
//! * `is_recording`, `recorded_rows`, `recorded_seconds`, `recording_path` and
//!   `last_recording_path` read atomics or a small lock that is never held across I/O (the C#
//!   guarded the recorder alone with a lock the poll thread held across every file write; only
//!   `recorded_seconds` took it).
//! * The source's retry count is published by the poll thread after every poll rather than read
//!   from the source on demand, because the source is not reachable while it is being polled.
//! * Dropping a session never disposes the source on the calling thread: closing a connection
//!   waits for the request in flight, so the source is handed to a detached thread to drop.
//!   `stop` itself waits at most two seconds for the poll thread, as the C#'s `Join` did.

use std::fs::{self, File};
use std::io::Write;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard, PoisonError};
use std::thread::JoinHandle;
use std::time::{Duration, Instant, SystemTime};

use olv_core::analysis::csv_export;
use olv_core::channels::channel_roles::{self, ChannelRole};
use olv_core::ini::msq_ini::DatalogEntry;
use olv_core::model::{LiveSource, LogChannel, LogDocument};
use olv_core::{Error, Result, fmt};
use olv_protocol::obd::realtime_decoder::RealtimeDecoder;

use crate::connection::ecu_connection::EcuConnection;
use crate::connection::tuner_studio_source::TunerStudioSource;

/// How a session is run.
#[derive(Clone, Debug)]
pub struct LiveSessionSettings {
    /// Where to record. `None` keeps the session in memory only.
    pub recording_path: Option<String>,

    /// Samples kept in memory. Older ones are dropped from the view but stay in the recording,
    /// so a long session cannot grow without bound while still leaving the whole run on disk.
    pub maximum_samples: i32,

    /// Consecutive failures before the link is treated as lost.
    pub failures_before_stopping: i32,

    /// Samples per second to aim for. Zero polls as fast as the link allows.
    ///
    /// A cap rather than a target, and it exists because the link is often much faster than
    /// the data is worth. A rusEFI over USB answers about 280 times a second, and taking it up
    /// on that writes 14 MB a minute and holds 3.2 KB of memory per sample across 823
    /// channels: an hour of driving that no laptop finishes.
    ///
    /// Nothing is lost by slowing down. A wideband takes 100 ms or more to respond and the
    /// exhaust takes another 50 to 300 to reach it, so the mixture reading is band-limited to
    /// under 10 Hz before it arrives; 25 samples a second is already well clear of it. Raise it
    /// for transient work (accel enrichment, knock, per-cylinder events) where the signal
    /// really does move that fast.
    pub maximum_rate: f64,

    /// How long to keep trying to get a lost link back before ending the session.
    ///
    /// Not zero, because the thing that most often ends a link is the key going off and on
    /// again. A session that dies from that loses everything after it for no better reason
    /// than the ECU rebooting, which is a thing ECUs do. Zero disables it and ends the session
    /// at the first loss.
    pub reconnect_for: Duration,

    /// Wait before the first attempt to get the link back.
    pub reconnect_every: Duration,

    /// Longest wait between attempts, which each one doubles towards.
    ///
    /// A minute of retrying at three-quarters of a second is eighty attempts, and on the links
    /// where recovery takes that long it is eighty attempts at something that needs to be left
    /// alone: a Wi-Fi OBD2 dongle accepts one client, takes a moment to release the last one,
    /// and a connect-and-reset arriving inside that moment wedges it. Backing off costs a few
    /// seconds on an ECU that comes back late and nothing at all on the common case, which is
    /// a key turned off and on and answers on the first or second try.
    pub reconnect_at_most: Duration,
}

impl Default for LiveSessionSettings {
    fn default() -> Self {
        Self {
            recording_path: None,
            maximum_samples: 500_000,
            failures_before_stopping: 20,
            maximum_rate: 25.0,
            reconnect_for: Duration::from_secs(60),
            reconnect_every: Duration::from_millis(750),
            reconnect_at_most: Duration::from_secs(6),
        }
    }
}

/// What a session has done so far, for the status line.
#[derive(Clone, Debug, PartialEq)]
pub struct LiveSessionStatus {
    pub samples: usize,
    pub seconds: f64,
    pub rate: f64,
    pub retries: i32,
    pub failures: i32,
    pub error: Option<String>,
    pub reconnecting: bool,
}

impl LiveSessionStatus {
    pub fn faulted(&self) -> bool {
        self.error.is_some()
    }
}

/// A handler for the session's status, raised after each block on the polling thread.
pub type UpdatedHandler = Arc<dyn Fn(&LiveSessionStatus) + Send + Sync>;

/// A handler for a decoded frame: the time, the channel names, and the values in that order.
pub type FrameHandler = Arc<dyn Fn(f64, &[String], &[f64]) + Send + Sync>;

#[derive(Default)]
struct Handlers {
    updated: Vec<UpdatedHandler>,
    frame: Vec<FrameHandler>,
    raw_frame: Vec<FrameHandler>,
}

/// What a source that decodes more than it reports offers, captured when the session is built.
struct RawInfo {
    names: Vec<String>,
    units: Vec<String>,
}

/// The sample store: the channels being recorded, and what has been read into them.
///
/// Not readonly, because a source that learns its channels from the ECU has none to offer
/// until it has been opened; see [`LiveSession::start`]. Fixed from that moment on: a log's
/// columns cannot change once it has rows.
#[derive(Default)]
struct Data {
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
    columns: Vec<Vec<f32>>,
    time: Vec<f64>,
    snapshot: Option<Arc<LogDocument>>,
    snapshot_at: Option<usize>,
}

/// The open recording file, and where on the session's clock it began.
struct Recorder {
    file: File,
    from: f64,
}

#[derive(Default)]
struct RecordingPaths {
    current: Option<String>,
    last: Option<String>,
}

/// A wait that stopping a session cuts short (the C#'s `CancellationToken.WaitHandle`).
struct Cancel {
    flag: Mutex<bool>,
    wake: Condvar,
}

impl Cancel {
    fn new() -> Self {
        Self {
            flag: Mutex::new(false),
            wake: Condvar::new(),
        }
    }

    fn cancel(&self) {
        *lock(&self.flag) = true;
        self.wake.notify_all();
    }

    fn is_cancelled(&self) -> bool {
        *lock(&self.flag)
    }

    /// Waits up to `wait`, returning true if the wait was cut short by cancellation.
    fn wait(&self, wait: Duration) -> bool {
        let guard = lock(&self.flag);
        let (guard, _) = self
            .wake
            .wait_timeout_while(guard, wait, |cancelled| !*cancelled)
            .unwrap_or_else(PoisonError::into_inner);

        *guard
    }
}

/// Set when the poll thread has finished, so `stop` can wait for it with a timeout.
struct Finished {
    done: Mutex<bool>,
    wake: Condvar,
}

struct Worker {
    handle: JoinHandle<()>,
    finished: Arc<Finished>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

struct Inner {
    settings: LiveSessionSettings,

    /// The source, when no poll thread has it. Never held across a poll.
    source: Mutex<Option<Box<dyn LiveSource>>>,
    disposed: AtomicBool,
    raw: Option<RawInfo>,

    /// The sample store (`_gate`). Held by every repaint's `snapshot`, so nothing slow goes
    /// behind it.
    data: Mutex<Data>,

    /// Guards the recorder alone.
    ///
    /// Separate from `data` deliberately. That one is held by every repaint (the plot asks for
    /// a snapshot continuously) and putting a file write behind it would make drawing wait on
    /// the disk. This one is only ever contended when somebody presses record, which is rare
    /// and brief.
    recorder: Mutex<Option<Recorder>>,
    recording: AtomicBool,
    recorded_rows: AtomicI32,
    recording_from_bits: AtomicU64,
    paths: Mutex<RecordingPaths>,

    clock: Mutex<Option<Instant>>,

    failures: AtomicI32,
    reconnecting: AtomicBool,
    error: Mutex<Option<String>>,
    retries: AtomicI32,

    cancel: Mutex<Arc<Cancel>>,
    worker: Mutex<Option<Worker>>,
    handlers: Mutex<Handlers>,
}

impl Inner {
    /// Seconds on the session's clock (a `Stopwatch` restarted by `start`).
    fn elapsed(&self) -> f64 {
        lock(&self.clock).map_or(0.0, |start| start.elapsed().as_secs_f64())
    }

    /// Takes the channel list from the source, for whichever of the two times it is known.
    fn adopt(&self, source: &dyn LiveSource) {
        // Under the same lock every other reader of these fields takes (see `status`,
        // `append`, `latest` and `snapshot`): without it a repaint mid-adopt could see the
        // names already replaced but the columns not yet, which is a length mismatch every
        // reader here assumes can't happen.
        let mut data = lock(&self.data);

        data.names = source.names().to_vec();
        data.units = source.units().to_vec();
        data.digits = source.digits().to_vec();
        data.columns = data.names.iter().map(|_| Vec::new()).collect();
    }

    fn status(&self) -> LiveSessionStatus {
        let data = lock(&self.data);

        let seconds = data.time.last().copied().unwrap_or(0.0);
        let samples = data.time.len();

        LiveSessionStatus {
            samples,
            seconds,
            rate: if seconds > 0.0 { samples as f64 / seconds } else { 0.0 },
            retries: self.retries.load(Ordering::Relaxed),
            failures: self.failures.load(Ordering::Relaxed),
            error: lock(&self.error).clone(),
            reconnecting: self.reconnecting.load(Ordering::Relaxed),
        }
    }

    /// Reporting a fault must not become one; a handler that throws is not our problem to
    /// inherit.
    fn announce(&self) {
        let handlers = lock(&self.handlers).updated.clone();
        if handlers.is_empty() {
            return;
        }

        let status = self.status();

        // Nothing useful to do on a panic, and unwinding out of the poll thread ends the
        // session's thread without a word.
        let _ = catch_unwind(AssertUnwindSafe(|| {
            for handler in &handlers {
                handler(&status);
            }
        }));
    }

    /// Writes one row, if anything is recording.
    ///
    /// Held under the recorder's own lock for the whole write. Recording can be stopped from
    /// another thread at any moment, and a writer closed between the check and the write must
    /// not be written to.
    ///
    /// Times are relative to where the recording began, so the file reads as a log of its own
    /// run rather than of the session that happened to contain it.
    fn record(&self, at: f64, values: &[f64], row: &mut [f64]) -> Result<()> {
        let mut recorder = lock(&self.recorder);
        let Some(recorder) = recorder.as_mut() else {
            return Ok(());
        };

        let width = row.len() - 1;
        if values.len() < width {
            return Err(Error::Argument(
                "The source returned fewer values than it has names.".to_string(),
            ));
        }

        row[0] = at - recorder.from;
        row[1..].copy_from_slice(&values[..width]);

        // The row goes to the file in one write, so a pulled cable costs at most the row in
        // hand (the C# flushed after every write for the same reason).
        let mut line = Vec::with_capacity(8 * row.len());
        csv_export::write_row(&mut line, row)?;
        recorder.file.write_all(&line)?;

        self.recorded_rows.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }

    fn append(&self, seconds: f64, values: &[f64]) -> Result<()> {
        let mut data = lock(&self.data);

        if values.len() < data.columns.len() {
            return Err(Error::Argument(
                "The source returned fewer values than it has names.".to_string(),
            ));
        }

        data.time.push(seconds);
        for (column, value) in data.columns.iter_mut().zip(values) {
            column.push(*value as f32);
        }

        // Trimmed from the front, so the view stays bounded on a long session while the
        // recording on disk keeps everything.
        let excess = data.time.len() as i64 - i64::from(self.settings.maximum_samples);
        if excess <= 0 {
            return Ok(());
        }

        let excess = excess as usize;
        data.time.drain(..excess);
        for column in &mut data.columns {
            column.drain(..excess.min(column.len()));
        }

        Ok(())
    }

    /// One poll: read, keep, record, hand on.
    fn step(&self, source: &mut dyn LiveSource, names: &[String], row: &mut [f64]) -> Result<()> {
        let values = source.read()?;
        let at = self.elapsed();

        self.append(at, &values)?;
        self.failures.store(0, Ordering::Relaxed);

        self.record(at, &values, row)?;

        // Handed on at the rate the ECU produces it rather than the rate a window repaints at.
        // The two differ by a lot (the plot is redrawn a few times a second while this loop
        // runs at whatever the link sustains) and anything watching the engine rather than
        // watching the screen wants the faster of them.
        //
        // Whatever is on the other end must not be able to slow this down; a handler that
        // blocks here delays the next poll, which is a reading not taken.
        let (frame, raw_frame) = {
            let handlers = lock(&self.handlers);
            (handlers.frame.clone(), handlers.raw_frame.clone())
        };

        if !frame.is_empty() {
            // Not this loop's problem to inherit.
            let _ = catch_unwind(AssertUnwindSafe(|| {
                for handler in &frame {
                    handler(at, names, &values);
                }
            }));
        }

        if !raw_frame.is_empty()
            && let Some(raw) = source.raw_telemetry()
            && let Some(raw_values) = raw.last_raw_frame()
        {
            let raw_names = raw.raw_names();
            let _ = catch_unwind(AssertUnwindSafe(|| {
                for handler in &raw_frame {
                    handler(at, raw_names, &raw_values);
                }
            }));
        }

        Ok(())
    }

    fn poll(&self, source: &mut dyn LiveSource, cancel: &Cancel) {
        let names = lock(&self.data).names.clone();
        let mut row = vec![0.0; names.len() + 1];
        let mut pace = Pacer::new(self.settings.maximum_rate);

        while !cancel.is_cancelled() {
            // Deliberately everything. This runs on a background thread, and a panic that
            // escapes one of those does not fail the session, it ends the thread silently. A
            // serial port whose device has gone throws whatever it likes, and which one you
            // get depends on where the port was when it died, so none of them can be treated
            // as the signal.
            let outcome = catch_unwind(AssertUnwindSafe(|| self.step(source, &names, &mut row)));

            self.retries.store(source.retries(), Ordering::Relaxed);

            if !matches!(outcome, Ok(Ok(()))) {
                if self.failures.fetch_add(1, Ordering::Relaxed) + 1 < self.settings.failures_before_stopping {
                    continue;
                }

                if self.recover(source, cancel) {
                    continue;
                }

                *lock(&self.error) = Some(if self.settings.reconnect_for > Duration::ZERO {
                    format!(
                        "The ECU did not come back within {} seconds. \
                         The session so far is still here and its recording is complete.",
                        fmt::f64(self.settings.reconnect_for.as_secs_f64(), "F0")
                    )
                } else {
                    "The ECU stopped responding \u{2014} switched off, or the cable pulled. \
                     The session so far is still here and its recording is complete."
                        .to_string()
                });

                break;
            }

            self.announce();
            pace.wait_for_next(cancel);
        }

        self.announce();
    }

    /// Tries to get a lost link back, and says whether polling can resume.
    ///
    /// The link has to be closed before it is reopened: a handle whose device has gone still
    /// reports itself open, so reopening alone does nothing and every read afterwards fails
    /// identically. That is also why the failure looks different the second time: the port is
    /// already shut by then, and throws a different exception on the way out.
    fn recover(&self, source: &mut dyn LiveSource, cancel: &Cancel) -> bool {
        if self.settings.reconnect_for.is_zero() {
            return false;
        }

        self.reconnecting.store(true, Ordering::Relaxed);
        self.announce();

        let deadline = Instant::now() + self.settings.reconnect_for;
        let mut wait = self.settings.reconnect_every;
        let mut recovered = false;

        while !cancel.is_cancelled() && Instant::now() < deadline {
            if cancel.wait(wait) {
                break;
            }

            // Each attempt waits longer than the last, up to the cap. The first few are quick
            // because the usual cause is the key going off and on again; after that,
            // attempting harder stops helping and starts hurting. An OBD2 dongle takes one
            // client at a time and needs a moment to let the last session go, so a fresh
            // connect and a reset every three-quarters of a second is a good way to wedge the
            // thing you are trying to reach.
            wait = slower(wait, self.settings.reconnect_at_most);

            // Opening a port proves nothing (the adapter can enumerate before the ECU behind
            // it is answering) so the source is expected to prove it with a reading of its
            // own. Still gone is not an error: wait and try again until the window closes.
            if matches!(catch_unwind(AssertUnwindSafe(|| source.recover())), Ok(Ok(()))) {
                self.failures.store(0, Ordering::Relaxed);
                recovered = true;
                break;
            }
        }

        self.reconnecting.store(false, Ordering::Relaxed);
        recovered
    }

    fn start_recording(&self, path: &str) -> Result<String> {
        if path.trim().is_empty() {
            return Err(Error::Argument(
                "The value cannot be an empty string or composed entirely of whitespace. (Parameter 'path')"
                    .to_string(),
            ));
        }

        self.stop_recording();

        if let Some(directory) = Path::new(path).parent()
            && !directory.as_os_str().is_empty()
        {
            fs::create_dir_all(directory)?;
        }

        // With a byte-order mark, matching the exporter. Excel reads a CSV without one in the
        // system codepage whatever is actually in it, so a channel in degrees C arrives as
        // "Â°C", and a recording nobody can open in the thing they open recordings in is a
        // poor recording. Every reader this application has copes with the mark, and so does
        // anything else that reads UTF-8.
        let mut file = File::create(path)?;

        let (names, units) = {
            let data = lock(&self.data);
            (data.names.clone(), data.units.clone())
        };

        let mut all_names = vec!["Time".to_string()];
        all_names.extend(names);
        let mut all_units = vec!["s".to_string()];
        all_units.extend(units);

        let mut head = vec![0xEF, 0xBB, 0xBF];
        csv_export::write_header(&mut head, &all_names, &all_units)?;
        file.write_all(&head)?;

        let mut recorder = lock(&self.recorder);

        // The recording's clock starts where the recording does, not where the session did. A
        // file that opens at t=418 s because that is when somebody pressed record is not a log
        // of a run: every tool that reads it, this one included, would draw four hundred
        // seconds of nothing before the first sample.
        let from = self.elapsed();

        *recorder = Some(Recorder { file, from });
        self.recording_from_bits.store(from.to_bits(), Ordering::Relaxed);
        self.recorded_rows.store(0, Ordering::Relaxed);
        self.recording.store(true, Ordering::Relaxed);

        let mut paths = lock(&self.paths);
        paths.current = Some(path.to_string());
        paths.last = Some(path.to_string());

        Ok(path.to_string())
    }

    fn stop_recording(&self) -> Option<String> {
        let mut recorder = lock(&self.recorder);

        let mut taken = recorder.take()?;
        self.recording.store(false, Ordering::Relaxed);

        // Whatever reached disk is what the recording has; there is nothing left to save it
        // with.
        let _ = taken.file.flush();
        drop(taken);

        lock(&self.paths).current.take()
    }
}

/// The next wait, twice the last but never past the cap.
fn slower(wait: Duration, cap: Duration) -> Duration {
    if cap > Duration::ZERO && wait + wait > cap {
        cap
    } else {
        wait + wait
    }
}

/// Holds the poll loop to a rate.
///
/// Each period is due at a fixed distance from the last, not from whenever the previous one
/// finished, so the reads a slow sample delays are made up afterwards and the average rate is
/// the one asked for, even though Windows wakes a thread on a 15.6 ms tick and no individual
/// period lands exactly. A long stall resets the schedule instead of being repaid, since a
/// burst of catch-up reads after the link recovers is samples of nothing.
struct Pacer {
    period: Duration,
    clock: Instant,
    /// Signed nanoseconds since `clock` started.
    due: i128,
}

impl Pacer {
    fn new(rate_per_second: f64) -> Self {
        Self {
            period: if rate_per_second > 0.0 {
                Duration::try_from_secs_f64(1.0 / rate_per_second).unwrap_or(Duration::MAX)
            } else {
                Duration::ZERO
            },
            clock: Instant::now(),
            due: 0,
        }
    }

    fn wait_for_next(&mut self, cancel: &Cancel) {
        if self.period.is_zero() {
            return;
        }

        let period = self.period.as_nanos() as i128;
        self.due += period;

        let now = self.clock.elapsed().as_nanos() as i128;
        let wait = self.due - now;

        // Behind by more than a whole period: the link stalled, or the reads are simply slower
        // than the rate asked for. Either way, start counting again from here.
        if wait < -period {
            self.due = now;
            return;
        }

        // Cancellation is waited on rather than slept through, so stopping a session at 1 Hz
        // does not take a second to notice.
        if wait > 0 {
            cancel.wait(Duration::from_nanos(wait.min(i128::from(u64::MAX)) as u64));
        }
    }
}

/// Polls an ECU and turns its realtime blocks into a log.
pub struct LiveSession {
    inner: Arc<Inner>,
}

impl LiveSession {
    /// A session over any live source: the poll loop, the pacing, the recording and the
    /// reconnection are the same whatever is on the other end.
    pub fn new(source: Box<dyn LiveSource>, settings: LiveSessionSettings) -> Self {
        let raw = source.raw_telemetry().map(|raw| RawInfo {
            names: raw.raw_names().to_vec(),
            units: raw.raw_units().to_vec(),
        });

        let inner = Inner {
            settings,
            raw,
            disposed: AtomicBool::new(false),
            data: Mutex::new(Data::default()),
            recorder: Mutex::new(None),
            recording: AtomicBool::new(false),
            recorded_rows: AtomicI32::new(0),
            recording_from_bits: AtomicU64::new(0),
            paths: Mutex::new(RecordingPaths::default()),
            clock: Mutex::new(None),
            failures: AtomicI32::new(0),
            reconnecting: AtomicBool::new(false),
            error: Mutex::new(None),
            retries: AtomicI32::new(source.retries()),
            cancel: Mutex::new(Arc::new(Cancel::new())),
            worker: Mutex::new(None),
            handlers: Mutex::new(Handlers::default()),
            source: Mutex::new(None),
        };

        inner.adopt(&*source);
        *lock(&inner.source) = Some(source);

        Self { inner: Arc::new(inner) }
    }

    /// A session over the TunerStudio protocol, which is most of them.
    pub fn for_tuner_studio(
        connection: Arc<EcuConnection>,
        decoder: RealtimeDecoder,
        datalog: &[DatalogEntry],
        settings: LiveSessionSettings,
    ) -> Self {
        Self::new(Box::new(TunerStudioSource::new(connection, decoder, datalog)), settings)
    }

    /// Channels this session records, in order.
    pub fn names(&self) -> Vec<String> {
        lock(&self.inner.data).names.clone()
    }

    pub fn is_running(&self) -> bool {
        lock(&self.inner.worker)
            .as_ref()
            .is_some_and(|worker| !*lock(&worker.finished.done))
    }

    /// The file being written now, or `None` when only watching.
    pub fn recording_path(&self) -> Option<String> {
        lock(&self.inner.paths).current.clone()
    }

    /// The last file written, whether or not it is still open.
    ///
    /// Kept so that stopping a recording does not make the session forget where it went: the
    /// thing somebody wants immediately after pressing stop is to know what the file was
    /// called.
    pub fn last_recording_path(&self) -> Option<String> {
        lock(&self.inner.paths).last.clone()
    }

    pub fn is_recording(&self) -> bool {
        self.inner.recording.load(Ordering::Relaxed)
    }

    /// Rows written to the current recording.
    pub fn recorded_rows(&self) -> i32 {
        self.inner.recorded_rows.load(Ordering::Relaxed)
    }

    /// Seconds covered by the current recording.
    pub fn recorded_seconds(&self) -> f64 {
        if !self.is_recording() {
            return 0.0;
        }

        let from = f64::from_bits(self.inner.recording_from_bits.load(Ordering::Relaxed));
        (self.inner.elapsed() - from).max(0.0)
    }

    /// Raised after each block, on the polling thread.
    pub fn on_updated(&self, handler: impl Fn(&LiveSessionStatus) + Send + Sync + 'static) {
        lock(&self.inner.handlers).updated.push(Arc::new(handler));
    }

    /// True when the source decodes more than it reports through [`names`](Self::names); see
    /// `RawTelemetrySource`.
    pub fn supports_raw_telemetry(&self) -> bool {
        self.inner.raw.is_some()
    }

    /// Every channel the source decodes, raw names included. Empty when unsupported.
    pub fn raw_names(&self) -> &[String] {
        self.inner.raw.as_ref().map_or(&[], |raw| &raw.names)
    }

    pub fn raw_units(&self) -> &[String] {
        self.inner.raw.as_ref().map_or(&[], |raw| &raw.units)
    }

    /// Every decoded frame at its full width, for a source that supports it. Raised alongside
    /// [`on_frame`](Self::on_frame), on the same poll thread and under the same "do not block
    /// this" rule: this is the same reading `on_frame` just carried, not a second one taken.
    pub fn on_raw_frame(&self, handler: impl Fn(f64, &[String], &[f64]) + Send + Sync + 'static) {
        lock(&self.inner.handlers).raw_frame.push(Arc::new(handler));
    }

    /// Every decoded frame, as it arrives: the time, the channel names, and the values in that
    /// order.
    ///
    /// Raised on the poll thread, so a handler that blocks costs a reading. The values slice is
    /// the live one rather than a copy: anything keeping it must copy what it needs before
    /// returning.
    pub fn on_frame(&self, handler: impl Fn(f64, &[String], &[f64]) + Send + Sync + 'static) {
        lock(&self.inner.handlers).frame.push(Arc::new(handler));
    }

    pub fn status(&self) -> LiveSessionStatus {
        self.inner.status()
    }

    pub fn start(&self) -> Result<()> {
        if self.is_running() {
            return Ok(());
        }

        let inner = &self.inner;

        *lock(&inner.error) = None;
        inner.failures.store(0, Ordering::Relaxed);

        let Some(mut source) = lock(&inner.source).take() else {
            return Err(Error::InvalidOperation(
                "The session has no source to poll: it was disposed, or an earlier poll thread is still running."
                    .to_string(),
            ));
        };

        if let Err(e) = source.open() {
            *lock(&inner.source) = Some(source);
            return Err(e);
        }

        // Most sources know their channels from a definition file and have them before
        // anything is opened. One does not: a MaxxECU over USB names its channels in the
        // telemetry itself, so the list exists only once the ECU has been listened to. Asking
        // again after opening is what lets a source of that shape be recorded at all; checking
        // before it, which is where this check used to sit, refused every such session before
        // the cable was ever spoken to.
        if lock(&inner.data).names.is_empty() {
            inner.adopt(&*source);
        }

        if lock(&inner.data).names.is_empty() {
            // Opened above, and there is no poll thread yet to close it on a later `stop`:
            // leaving it open here would leak the link (and, on a serial or D2XX source, the
            // underlying handle) for a session that never actually starts. Dropped on its own
            // thread, because closing a connection can wait on a request in flight.
            std::thread::spawn(move || drop(source));

            return Err(Error::InvalidOperation("No channels to record.".to_string()));
        }

        *lock(&inner.clock) = Some(Instant::now());

        // Before the poll thread, so the first row is recorded rather than being a race
        // against the writer being ready.
        if let Some(path) = inner.settings.recording_path.as_deref().filter(|p| !p.is_empty())
            && let Err(e) = inner.start_recording(path)
        {
            *lock(&inner.source) = Some(source);
            return Err(e);
        }

        let cancel = Arc::new(Cancel::new());
        *lock(&inner.cancel) = Arc::clone(&cancel);

        let finished = Arc::new(Finished {
            done: Mutex::new(false),
            wake: Condvar::new(),
        });

        let worker_inner = Arc::clone(inner);
        let worker_finished = Arc::clone(&finished);

        let spawned = std::thread::Builder::new().name("ECU poll".to_string()).spawn(move || {
            let mut source = source;
            worker_inner.poll(&mut *source, &cancel);

            // Back where `stop` and `Drop` can find it, unless the session has been disposed
            // in the meantime, in which case this thread is the last owner. Decided under the
            // source lock, which is what `Drop` takes after setting the flag.
            let leftover = {
                let mut slot = lock(&worker_inner.source);
                if worker_inner.disposed.load(Ordering::SeqCst) {
                    Some(source)
                } else {
                    *slot = Some(source);
                    None
                }
            };
            drop(leftover);

            *lock(&worker_finished.done) = true;
            worker_finished.wake.notify_all();
        });

        match spawned {
            Ok(handle) => {
                *lock(&inner.worker) = Some(Worker { handle, finished });
                Ok(())
            }
            Err(e) => Err(Error::Io(e)),
        }
    }

    /// Begins writing to a file, whether or not the session is already running.
    ///
    /// Recording is separable from watching on purpose. Somebody connects to see whether the
    /// link works at all, warms the engine, finds the road, and only then wants a file, and
    /// everything before that is noise they would have to trim out of the front of the log
    /// afterwards. Deciding when a run starts is the difference between a log and a session.
    ///
    /// Replaces a recording already in progress, closing it properly first, so this cannot
    /// silently leave two writers on one session. Returns the path being written to.
    pub fn start_recording(&self, path: &str) -> Result<String> {
        self.inner.start_recording(path)
    }

    /// Closes the recording, leaving the session running.
    ///
    /// The file is complete at this point rather than at disposal: every row was written as it
    /// arrived, so what is on disk is already the whole of it. Returns what was written, or
    /// `None` if nothing was being recorded.
    pub fn stop_recording(&self) -> Option<String> {
        self.inner.stop_recording()
    }

    /// Ends the session. Safe to call after the link has already failed, which is the usual
    /// way it is reached: closing a recorder whose disk is full, or a port whose device has
    /// gone, both throw.
    ///
    /// Waits up to two seconds for the poll thread, as the C# did; one that is stuck in a
    /// request is left to finish on its own.
    pub fn stop(&self) {
        lock(&self.inner.cancel).cancel();

        let worker = lock(&self.inner.worker).take();
        if let Some(worker) = worker {
            let done = lock(&worker.finished.done);
            let (done, _) = worker
                .finished
                .wake
                .wait_timeout_while(done, Duration::from_secs(2), |finished| !*finished)
                .unwrap_or_else(PoisonError::into_inner);

            if *done {
                drop(done);
                let _ = worker.handle.join();
            }
        }

        self.inner.stop_recording();
    }

    /// The newest reading of whichever column plays `role`, or NaN when this session has no
    /// such column or has not sampled one yet.
    ///
    /// For a caller that needs one live number and needs it to be live: a guard asking "is the
    /// engine running" must not be answered by a saved log somebody opened while connected,
    /// which is what reading it out of the window's current document would do. Deliberately not
    /// built on [`snapshot`](Self::snapshot): that copies every column whenever the sample
    /// count has moved, which is every poll, and this is asked on a write path.
    pub fn latest(&self, role: ChannelRole) -> f64 {
        let data = lock(&self.inner.data);

        for (name, column) in data.names.iter().zip(&data.columns) {
            if !channel_roles::matches(name, role) {
                continue;
            }

            return column.last().map_or(f64::NAN, |&v| f64::from(v));
        }

        f64::NAN
    }

    /// The session so far as an ordinary document.
    ///
    /// Cached against the sample count: the plot asks for this on every repaint, and copying a
    /// few hundred columns each time for a document that has not changed would cost more than
    /// drawing it. (As in the C#, the count stops moving once the view is full, so a full view
    /// keeps handing back the snapshot it first built.)
    pub fn snapshot(&self) -> Arc<LogDocument> {
        let mut data = lock(&self.inner.data);

        if let Some(snapshot) = &data.snapshot
            && data.snapshot_at == Some(data.time.len())
        {
            return Arc::clone(snapshot);
        }

        let channels: Vec<Arc<LogChannel>> = (0..data.names.len())
            .map(|i| {
                Arc::new(LogChannel::adopt(
                    &data.names[i],
                    &data.units[i],
                    data.digits[i],
                    data.columns[i].clone(),
                ))
            })
            .collect();

        data.snapshot_at = Some(data.time.len());

        // The last recording rather than the current one, so stopping a recording does not
        // rename the document out from under the view.
        let paths = lock(&self.inner.paths);
        let file_path = paths
            .current
            .as_deref()
            .or(paths.last.as_deref())
            .unwrap_or("")
            .to_string();
        drop(paths);

        let document = Arc::new(
            LogDocument::new(
                &file_path,
                "Live",
                channels,
                Arc::new(LogChannel::new("Time", "s", 3, &data.time, true)),
            )
            .with_recorded_at(Some(SystemTime::now())),
        );

        data.snapshot = Some(Arc::clone(&document));
        document
    }
}

impl Drop for LiveSession {
    /// `Dispose`: stops the session, then lets go of the source.
    ///
    /// The source is dropped on a thread of its own, or by the poll thread if that is still
    /// holding it: closing a connection waits for the request in flight, and nothing a window
    /// waits on may do that.
    fn drop(&mut self) {
        self.stop();

        self.inner.disposed.store(true, Ordering::SeqCst);

        let source = lock(&self.inner.source).take();
        if let Some(source) = source {
            std::thread::spawn(move || drop(source));
        }
    }
}
