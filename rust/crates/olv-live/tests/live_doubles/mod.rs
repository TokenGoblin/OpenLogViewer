//! Test doubles for the w4-live tests: the C# test project's `FlakyTransport` and
//! `ThrowingTransport` (in `EcuProtocolTests.cs`), the sources `LiveRecordingTests` and
//! `CsvExportTests` define, and the temp-file and polling helpers each test class carries.

#![allow(dead_code)]

pub mod model;

use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use olv_core::model::LiveSource;
use olv_core::{Error, Result};
use olv_live::connection::ecu_connection::{EcuConnection, EcuConnectionSettings};
use olv_live::live::live_session::{LiveSession, LiveSessionSettings};
use olv_protocol::ms::ms_protocol::EcuTransport;

/// The exceptions a real port throws, by what the C# tests used to stand for them.
#[derive(Clone, Debug)]
pub enum Fault {
    /// `IOException`.
    Io(&'static str),
    /// `InvalidOperationException`.
    InvalidOperation(&'static str),
    /// `ObjectDisposedException`.
    ObjectDisposed(&'static str),
    /// `ArgumentOutOfRangeException`.
    ArgumentOutOfRange(&'static str),
    /// `UnauthorizedAccessException`.
    Unauthorized(&'static str),
    /// `NotSupportedException`.
    NotSupported(&'static str),
    /// Something that is not an error value at all: the transport panics.
    Panic(&'static str),
}

impl Fault {
    pub fn raise(&self) -> Error {
        match self {
            Fault::Io(m) => Error::Io(std::io::Error::other(*m)),
            Fault::InvalidOperation(m) | Fault::ObjectDisposed(m) | Fault::NotSupported(m) => {
                Error::InvalidOperation((*m).to_string())
            }
            Fault::ArgumentOutOfRange(m) => Error::Argument((*m).to_string()),
            Fault::Unauthorized(m) => Error::Io(std::io::Error::new(std::io::ErrorKind::PermissionDenied, *m)),
            Fault::Panic(m) => panic!("{m}"),
        }
    }
}

struct FlakyState {
    reply: Vec<u8>,
    pending: Vec<u8>,
    open: bool,
    down: Option<Fault>,
    opens: usize,
    closes: usize,
}

/// A link that can be taken away and given back, the way a key-off and key-on does. Each drop
/// can throw a different exception, because a real port does: it depends where the port was
/// when the device went.
#[derive(Clone)]
pub struct FlakyTransport(Arc<Mutex<FlakyState>>);

impl FlakyTransport {
    pub fn new(reply: Vec<u8>) -> Self {
        Self(Arc::new(Mutex::new(FlakyState {
            reply,
            pending: Vec::new(),
            open: false,
            down: None,
            opens: 0,
            closes: 0,
        })))
    }

    /// Set to make every operation throw until it is cleared.
    pub fn set_down(&self, fault: Option<Fault>) {
        self.0.lock().unwrap().down = fault;
    }

    pub fn opens(&self) -> usize {
        self.0.lock().unwrap().opens
    }

    pub fn closes(&self) -> usize {
        self.0.lock().unwrap().closes
    }

    fn check(&self) -> Result<()> {
        let down = self.0.lock().unwrap().down.clone();
        match down {
            Some(fault) => Err(fault.raise()),
            None => Ok(()),
        }
    }
}

impl EcuTransport for FlakyTransport {
    fn is_open(&self) -> bool {
        self.0.lock().unwrap().open
    }

    fn open(&mut self) -> Result<()> {
        self.check()?;

        let mut s = self.0.lock().unwrap();
        s.opens += 1;
        s.open = true;
        Ok(())
    }

    fn close(&mut self) {
        let mut s = self.0.lock().unwrap();
        s.closes += 1;
        s.open = false;
    }

    fn write(&mut self, _data: &[u8]) -> Result<()> {
        self.check()?;

        let mut s = self.0.lock().unwrap();
        s.pending = s.reply.clone();
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        self.check()?;

        let mut s = self.0.lock().unwrap();
        let take = buffer.len().min(s.pending.len());
        buffer[..take].copy_from_slice(&s.pending[..take]);
        s.pending.drain(..take);
        Ok(take)
    }

    fn discard_input(&mut self) {
        // The C# throws here when the link is down; `discard_input` returns nothing, so the
        // next write or read reports it instead.
    }
}

/// A transport whose device has gone: every operation fails, the way a serial port does once
/// its USB adapter has been unplugged.
pub struct ThrowingTransport {
    thrown: Fault,
    /// Closing panics too (the C#'s `ThrowOnClose`): `close` cannot return an error.
    pub throw_on_close: bool,
}

impl ThrowingTransport {
    pub fn new(thrown: Fault) -> Self {
        Self {
            thrown,
            throw_on_close: false,
        }
    }
}

impl EcuTransport for ThrowingTransport {
    fn is_open(&self) -> bool {
        true
    }

    fn open(&mut self) -> Result<()> {
        Ok(())
    }

    fn close(&mut self) {
        if self.throw_on_close {
            panic!("closing a port whose device has gone");
        }
    }

    fn write(&mut self, _data: &[u8]) -> Result<()> {
        Err(self.thrown.raise())
    }

    fn read(&mut self, _buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Err(self.thrown.raise())
    }

    fn discard_input(&mut self) {}
}

/// A source that answers instantly, so tests are not timing exercises (`LiveRecordingTests`'
/// `Ticker`).
pub struct Ticker {
    n: i32,
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
}

impl Ticker {
    pub fn new() -> Self {
        Self {
            n: 0,
            names: vec!["RPM".into(), "MAP".into()],
            units: vec!["rpm".into(), "kPa".into()],
            digits: vec![0, 1],
        }
    }
}

impl LiveSource for Ticker {
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
        Ok(())
    }
    fn read(&mut self) -> Result<Vec<f64>> {
        self.n += 1;
        let n = self.n;
        Ok(vec![f64::from(1000 + n), f64::from(50 + n % 10)])
    }
    fn recover(&mut self) -> Result<()> {
        Ok(())
    }
    fn retries(&self) -> i32 {
        0
    }
}

/// A source that answers instantly with the same row (`CsvExportTests`' `FixedSource`).
pub struct FixedSource {
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
    row: Vec<f64>,
}

impl FixedSource {
    pub fn new(names: &[&str], units: &[&str], row: &[f64]) -> Self {
        Self {
            names: names.iter().map(|s| (*s).to_string()).collect(),
            units: units.iter().map(|s| (*s).to_string()).collect(),
            digits: names.iter().map(|_| 1).collect(),
            row: row.to_vec(),
        }
    }
}

impl LiveSource for FixedSource {
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
        Ok(())
    }
    fn read(&mut self) -> Result<Vec<f64>> {
        Ok(self.row.clone())
    }
    fn recover(&mut self) -> Result<()> {
        Ok(())
    }
    fn retries(&self) -> i32 {
        0
    }
}

/// Temp files, deleted when the test ends (the `IDisposable` test classes' `_temp`).
#[derive(Default)]
pub struct TempFiles(Vec<PathBuf>);

static COUNTER: AtomicU32 = AtomicU32::new(0);

impl TempFiles {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn path(&mut self, prefix: &str) -> String {
        let n = COUNTER.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!("{prefix}-{}-{n}.csv", std::process::id()));
        self.0.push(path.clone());
        path.to_string_lossy().into_owned()
    }
}

impl Drop for TempFiles {
    fn drop(&mut self) {
        for path in &self.0 {
            let _ = std::fs::remove_file(path);
        }
    }
}

/// Polls `condition` every `every_ms` until it holds or `seconds` pass. Returns whether it held.
pub fn wait_for(seconds: f64, every_ms: u64, mut condition: impl FnMut() -> bool) -> bool {
    let deadline = Instant::now() + Duration::from_secs_f64(seconds);
    while !condition() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(every_ms));
    }
    condition()
}

/// Runs until the session has at least this many samples, or gives up.
pub fn until(session: &LiveSession, samples: usize) {
    wait_for(5.0, 10, || session.status().samples >= samples);
}

/// A session over the TunerStudio protocol with a connection quick enough for a test.
pub fn tuner_session(
    transport: impl EcuTransport + 'static,
    ini: &str,
    settings: Option<LiveSessionSettings>,
) -> LiveSession {
    use olv_core::ini::msq_ini;
    use olv_protocol::obd::realtime_decoder::RealtimeDecoder;

    let connection = Arc::new(EcuConnection::new(
        Box::new(transport),
        EcuConnectionSettings {
            timeout: Duration::from_millis(50),
            retry_pause: Duration::ZERO,
            ..EcuConnectionSettings::default()
        },
    ));

    LiveSession::for_tuner_studio(
        connection,
        RealtimeDecoder::new(msq_ini::read_output_channels(ini, None), None),
        &msq_ini::read_datalog(ini, None),
        settings.unwrap_or_default(),
    )
}

// ----- a recorded MaxxECU conversation ---------------------------------------

/// The request and reply pairs MTune 1.161 exchanged with a MaxxECU Race over USB
/// (`tests/OpenLogViewer.Tests/Fixtures/maxxecu-usb.txt`). Nothing in them was composed here.
pub fn recorded_maxx_usb() -> Vec<(Vec<u8>, Vec<u8>)> {
    let path =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../tests/OpenLogViewer.Tests/Fixtures/maxxecu-usb.txt");
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("fixture {}: {e}", path.display()));

    let unhex = |s: &str| -> Vec<u8> {
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect()
    };

    text.lines()
        .filter(|l| !l.starts_with('#') && !l.is_empty())
        .filter_map(|l| {
            let halves: Vec<&str> = l.split(' ').filter(|h| !h.is_empty()).collect();
            (halves.len() == 2).then(|| (unhex(halves[0]), unhex(halves[1])))
        })
        .collect()
}

/// Replays a recorded conversation: a request is answered with the reply recorded under the next
/// pair, from where the last one was found, whose request is the same bytes (wrapping round, so a
/// run of telemetry rounds can be asked for again and again). A request nothing recorded matches is
/// met with silence.
pub struct RecordedEcu {
    pairs: Vec<(Vec<u8>, Vec<u8>)>,
    cursor: usize,
    reply: Vec<u8>,
    open: bool,
    /// Every request put on the wire, in order.
    pub requests: Vec<Vec<u8>>,
}

impl RecordedEcu {
    pub fn new() -> Self {
        Self {
            pairs: recorded_maxx_usb(),
            cursor: 0,
            reply: Vec::new(),
            open: false,
            requests: Vec::new(),
        }
    }
}

impl EcuTransport for RecordedEcu {
    fn is_open(&self) -> bool {
        self.open
    }

    fn open(&mut self) -> Result<()> {
        self.open = true;
        Ok(())
    }

    fn close(&mut self) {
        self.open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.requests.push(data.to_vec());

        let n = self.pairs.len();
        let found = (0..n).map(|i| (self.cursor + i) % n).find(|&i| self.pairs[i].0 == data);

        self.reply = match found {
            Some(i) => {
                self.cursor = i + 1;
                self.pairs[i].1.clone()
            }
            None => Vec::new(),
        };

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let taken = buffer.len().min(self.reply.len());
        buffer[..taken].copy_from_slice(&self.reply[..taken]);
        self.reply.drain(..taken);
        Ok(taken)
    }

    fn discard_input(&mut self) {
        self.reply.clear();
    }
}
