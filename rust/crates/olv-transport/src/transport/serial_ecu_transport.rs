//! Ported from `SerialEcuTransport.cs`. Owned by package `w2-transports`.
//!
//! A serial port, whether that is a USB tuning cable or a Bluetooth adapter: both appear as
//! a serial port and neither knows the difference.
//!
//! The device itself sits behind the small [`SerialIo`] trait, implemented for real ports by
//! our own [`SerialPort`] (`serial_port.rs`: Win32 on Windows, termios on Linux and macOS) and
//! by fakes in the tests. Everything the C# decided (timeouts, the open retry, how a partial
//! read is reported, which errors become what) is plain logic on top of that trait.
//!
//! # Differences the bench must confirm
//!
//! * .NET's `SerialPort` has separate read and write timeouts, and so does our `SerialPort`
//!   (the `serialport` crate it replaces had one). This transport sets the write timeout to
//!   500 ms (widened around a burn) and the read timeout to the time remaining before each read.
//! * `SerialPort.Read` returns as soon as one byte is available and throws `TimeoutException`
//!   when none arrives; our `SerialPort::read` does the same (a read returns what is there,
//!   `TimedOut` when nothing is, any other error for a port that has failed). Confirm on a real
//!   MicroSquirt that a 512-byte block still arrives whole through the read loop.
//! * The "semaphore timeout" rewording of an RFCOMM failure matches the message text, as the
//!   C# did, so on a Windows installed in another language it will not trigger. It also
//!   matches Windows error 121, which the C# message came from.
//! * Sorting ports by number is stable here; `Array.Sort` was not, which only shows for names
//!   with no digits.
//! * A failing `DiscardInBuffer` straight after opening closes the port again; the C# left the
//!   half-opened port in place, where it reported itself closed anyway.

use std::io;
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

use olv_core::{Error, Result};
use olv_protocol::ms::ms_protocol::EcuTransport;

use super::serial_port::{PortInfo, SerialPort, SerialSettings, available_ports, is_timeout};

/// What a write is given before it is called a dead port.
pub const DEFAULT_WRITE_TIMEOUT: Duration = Duration::from_millis(500);

/// A serial device, reduced to what the transport needs, so the logic is testable with fakes.
///
/// A timeout is reported as [`io::ErrorKind::TimedOut`] (or `WouldBlock`); any other error is a
/// port that has failed.
pub trait SerialIo: Send {
    /// Sets how long the next reads may wait for a first byte.
    fn set_read_timeout(&mut self, timeout: Duration);

    /// Sets how long the next writes may take to be accepted.
    fn set_write_timeout(&mut self, timeout: Duration);

    /// Reads whatever is available, up to `buf.len()`: at least one byte, or a timeout error.
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize>;

    /// Writes all of `data`.
    fn write_all(&mut self, data: &[u8]) -> io::Result<()>;

    /// Drops anything already received but not yet read.
    fn discard_input(&mut self) -> io::Result<()>;

    /// Drops anything buffered in either direction.
    fn discard_all(&mut self) -> io::Result<()> {
        self.discard_input()
    }
}

/// Opens a port by name and baud rate, ready to use: 8-N-1, no flow control, DTR and RTS asserted.
pub type PortOpener = Arc<dyn Fn(&str, u32) -> io::Result<Box<dyn SerialIo>> + Send + Sync>;

impl SerialIo for SerialPort {
    fn set_read_timeout(&mut self, timeout: Duration) {
        SerialPort::set_read_timeout(self, timeout);
    }

    fn set_write_timeout(&mut self, timeout: Duration) {
        SerialPort::set_write_timeout(self, timeout);
    }

    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        SerialPort::read(self, buf)
    }

    fn write_all(&mut self, data: &[u8]) -> io::Result<()> {
        SerialPort::write_all(self, data)
    }

    fn discard_input(&mut self) -> io::Result<()> {
        SerialPort::discard_input(self)
    }

    fn discard_all(&mut self) -> io::Result<()> {
        SerialPort::discard_all(self)
    }
}

/// Opens a real serial port the way the C# configured its `SerialPort`.
pub fn open_real_port(name: &str, baud_rate: u32) -> io::Result<Box<dyn SerialIo>> {
    open_real_port_with(name, baud_rate, true)
}

/// As [`open_real_port`], choosing whether DTR and RTS are asserted. When they are not, the
/// modem lines are not touched at all: a MaxxECU's virtual COM port left them alone, as FTDI's
/// D2XX did.
pub fn open_real_port_with(name: &str, baud_rate: u32, assert_lines: bool) -> io::Result<Box<dyn SerialIo>> {
    let mut port = SerialPort::open(name, &port_settings(baud_rate, assert_lines))?;
    port.set_read_timeout(Duration::from_millis(500));
    port.set_write_timeout(DEFAULT_WRITE_TIMEOUT);
    Ok(Box::new(port))
}

/// 8 data bits, no parity, one stop bit, no flow control. Some adapters hold the line low until DTR
/// and RTS are asserted, and stay silent rather than reporting anything wrong. Without
/// `assert_lines` the two lines are not touched at all (not cleared either), as D2XX left them.
fn port_settings(baud_rate: u32, assert_lines: bool) -> SerialSettings {
    let settings = SerialSettings::new(baud_rate);
    if assert_lines {
        settings.with_lines(true)
    } else {
        settings
    }
}

/// The serial ports the operating system lists, with whatever USB detail it knows. Empty
/// rather than failing: a port list is for a menu.
pub(crate) fn list_system_ports() -> Vec<PortInfo> {
    available_ports().unwrap_or_default()
}

/// A serial port, whether that is a USB tuning cable or a Bluetooth adapter.
pub struct SerialEcuTransport {
    port_name: String,
    baud_rate: u32,
    open_attempts: u32,
    opener: PortOpener,
    port: Option<Box<dyn SerialIo>>,
    write_timeout: Duration,
}

impl SerialEcuTransport {
    /// A transport for `port_name` at `baud_rate` (the C# default is 115200).
    pub fn new(port_name: impl Into<String>, baud_rate: u32) -> Self {
        Self::with_opener(port_name, baud_rate, Arc::new(open_real_port))
    }

    /// A transport whose port comes from `opener`: a fake in a test.
    pub fn with_opener(port_name: impl Into<String>, baud_rate: u32, opener: PortOpener) -> Self {
        Self {
            port_name: port_name.into(),
            baud_rate,
            open_attempts: 1,
            opener,
            port: None,
            write_timeout: DEFAULT_WRITE_TIMEOUT,
        }
    }

    /// How many times to try opening the port.
    ///
    /// One for a cable, which either works or does not. More for Bluetooth: establishing an
    /// RFCOMM link is reported to fail on the first attempt after an ECU boots (an SDP
    /// discovery failure rather than anything wrong) and to succeed on the second. Retrying
    /// costs a moment; not retrying costs a connection that would have worked.
    #[must_use]
    pub fn with_open_attempts(mut self, attempts: u32) -> Self {
        self.open_attempts = attempts;
        self
    }

    pub fn port_name(&self) -> &str {
        &self.port_name
    }

    pub fn baud_rate(&self) -> u32 {
        self.baud_rate
    }

    /// Ports currently present, for the connection picker.
    ///
    /// COM10 sorts before COM9 alphabetically, which reads as a mistake in a list the user is
    /// picking from, so they are ordered by the number in the name.
    pub fn available_ports() -> Vec<String> {
        let mut names: Vec<String> = list_system_ports().into_iter().map(|p| p.port_name).collect();
        sort_by_number(&mut names);
        names
    }

    fn open_once(&mut self) -> Result<()> {
        if self.is_open() {
            return Ok(());
        }

        let mut port = (self.opener)(&self.port_name, self.baud_rate)?;
        self.write_timeout = DEFAULT_WRITE_TIMEOUT;

        // Anything already buffered predates this session and would be read as the front of
        // the first reply.
        thread::sleep(Duration::from_millis(150));

        if let Err(e) = port.discard_input() {
            // The handle went away between opening it and this line, which a USB adapter does
            // while Windows is still attaching it: plug one in and connect straight away and
            // the port lists, opens, and is gone again a moment later. Reported as an I/O error
            // because that is what a port that will not stay open is, and what every caller
            // already expects to catch.
            return Err(Error::Io(io::Error::new(
                e.kind(),
                format!(
                    "{} opened and then closed again. If the adapter was just plugged in, \
                     give it a moment and try again.",
                    self.port_name
                ),
            )));
        }

        self.port = Some(port);
        Ok(())
    }
}

/// The number in a port name, or `i32::MAX` when it has none: every ASCII digit in the name,
/// read together (the C#'s `Number`).
fn port_number(name: &str) -> i32 {
    let digits: String = name.chars().filter(char::is_ascii_digit).collect();
    digits.parse::<i32>().unwrap_or(i32::MAX)
}

/// Orders port names by the number in them.
pub fn sort_by_number(names: &mut [String]) {
    names.sort_by_key(|n| port_number(n));
}

/// Whether an open failure is what Windows says when an RFCOMM link cannot be established.
fn is_semaphore_timeout(e: &io::Error) -> bool {
    e.raw_os_error() == Some(121) || e.to_string().to_ascii_lowercase().contains("semaphore")
}

impl EcuTransport for SerialEcuTransport {
    fn is_open(&self) -> bool {
        self.port.is_some()
    }

    fn open(&mut self) -> Result<()> {
        if self.is_open() {
            return Ok(());
        }

        for _ in 1..self.open_attempts.max(1) {
            match self.open_once() {
                Ok(()) => return Ok(()),
                Err(Error::Io(_)) => thread::sleep(Duration::from_millis(700)),
                Err(e) => return Err(e),
            }
        }

        match self.open_once() {
            Err(Error::Io(e)) if is_semaphore_timeout(&e) => {
                // What Windows says when an RFCOMM link cannot be established. The message
                // names a kernel primitive and tells the user nothing; the cause is almost
                // always that the device is switched off or out of range, since a paired port
                // exists whether or not anything answers on it.
                Err(Error::Io(io::Error::new(
                    e.kind(),
                    format!(
                        "{} did not answer. A paired Bluetooth port stays listed whether or not \
                         the device is switched on, so check the ECU has power and is in range.",
                        self.port_name
                    ),
                )))
            }
            other => other,
        }
    }

    /// Closes the port, tolerating one whose device has already gone. Unplugging a USB adapter
    /// leaves a port that fails from close as readily as from read, and failing to shut down is
    /// not a useful way to report that something is already shut down.
    fn close(&mut self) {
        self.port = None;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let timeout = self.write_timeout;
        let Some(port) = self.port.as_mut() else {
            return Err(Error::InvalidOperation("The port is not open.".into()));
        };

        port.set_write_timeout(timeout);

        match port.write_all(data) {
            Ok(()) => Ok(()),
            Err(e) if is_timeout(&e) => {
                // A port that will not even accept bytes. Windows' incoming Bluetooth port does
                // this: it is a listener waiting for something to dial in, so nothing is on the
                // other end of a write and it blocks until the timeout.
                //
                // Reported as an I/O error because that is what it is, and because a timeout
                // is not something a caller of a transport has any reason to expect: it
                // escaped the connect path and took the application down with it.
                Err(Error::Io(io::Error::new(
                    io::ErrorKind::TimedOut,
                    format!(
                        "{} would not accept the request. If this is a Bluetooth port, \
                         it may be the incoming one, which waits to be dialled rather than \
                         dialling out.",
                        self.port_name
                    ),
                )))
            }
            Err(e) => Err(e.into()),
        }
    }

    /// How long a write may block, which a burn widens for its length and puts back after.
    fn write_timeout(&self) -> Duration {
        if self.port.is_some() {
            self.write_timeout
        } else {
            DEFAULT_WRITE_TIMEOUT
        }
    }

    fn set_write_timeout(&mut self, timeout: Duration) {
        if self.port.is_some() && timeout > Duration::ZERO {
            self.write_timeout = timeout;
        }
    }

    /// Fills the buffer, or returns fewer bytes if the timeout passes first.
    ///
    /// Exactly as many bytes as asked for, rather than "until the line seems quiet". A
    /// 512-byte block takes about 45 ms to arrive at 115200 baud and the driver hands it over
    /// in pieces; treating a gap between pieces as the end of the reply truncates it, and a
    /// truncated frame is indistinguishable from a corrupt one by the time the checksum sees it.
    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        let Some(port) = self.port.as_mut() else {
            return Err(Error::InvalidOperation("The port is not open.".into()));
        };

        let mut total = 0;
        let deadline = Instant::now() + timeout;

        while total < buffer.len() {
            // Whole milliseconds, truncated, as the C# did: under one is out of time.
            let remaining = deadline.saturating_duration_since(Instant::now()).as_millis() as u64;
            if remaining == 0 {
                break;
            }

            // The port's own blocking read, rather than polling with a sleep: Windows rounds a
            // 1 ms sleep up to a timer tick of about 15 ms, which was costing more per poll than
            // the transfer itself.
            port.set_read_timeout(Duration::from_millis(remaining));

            match port.read(&mut buffer[total..]) {
                Ok(0) => break,
                Ok(n) => total += n,
                Err(e) if is_timeout(&e) => break,
                Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
                Err(e) => return Err(e.into()),
            }
        }

        Ok(total)
    }

    /// Drops anything buffered. Failing here is not worth reporting: it is done before a
    /// request as hygiene, and a port that cannot be cleared will say so again on the read
    /// that follows.
    fn discard_input(&mut self) {
        if let Some(port) = self.port.as_mut() {
            let _ = port.discard_input();
        }
    }
}

impl Drop for SerialEcuTransport {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::Mutex;

    /// A scripted port: each read returns the next piece, then times out.
    #[derive(Default)]
    struct Script {
        pieces: VecDeque<Vec<u8>>,
        written: Vec<u8>,
        timeouts: Vec<Duration>,
        write_timeouts: Vec<Duration>,
        write_error: Option<io::ErrorKind>,
        discard_error: bool,
        discards: usize,
    }

    struct FakePort(Arc<Mutex<Script>>);

    impl SerialIo for FakePort {
        fn set_read_timeout(&mut self, timeout: Duration) {
            self.0.lock().unwrap().timeouts.push(timeout);
        }

        fn set_write_timeout(&mut self, timeout: Duration) {
            self.0.lock().unwrap().write_timeouts.push(timeout);
        }

        fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
            let mut s = self.0.lock().unwrap();
            let Some(mut piece) = s.pieces.pop_front() else {
                // Like a real port: nothing to read means waiting out the whole timeout.
                let wait = s.timeouts.last().copied().unwrap_or_default();
                drop(s);
                thread::sleep(wait.min(Duration::from_millis(300)));
                return Err(io::ErrorKind::TimedOut.into());
            };
            let n = piece.len().min(buf.len());
            buf[..n].copy_from_slice(&piece[..n]);
            if n < piece.len() {
                s.pieces.push_front(piece.split_off(n));
            }
            Ok(n)
        }

        fn write_all(&mut self, data: &[u8]) -> io::Result<()> {
            let mut s = self.0.lock().unwrap();
            if let Some(kind) = s.write_error {
                return Err(kind.into());
            }
            s.written.extend_from_slice(data);
            Ok(())
        }

        fn discard_input(&mut self) -> io::Result<()> {
            let mut s = self.0.lock().unwrap();
            s.discards += 1;
            if s.discard_error {
                return Err(io::Error::new(io::ErrorKind::NotConnected, "gone"));
            }
            s.pieces.clear();
            Ok(())
        }
    }

    fn fake(script: &Arc<Mutex<Script>>) -> SerialEcuTransport {
        let script = script.clone();
        SerialEcuTransport::with_opener(
            "COM7",
            115_200,
            Arc::new(move |_, _| Ok(Box::new(FakePort(script.clone())) as Box<dyn SerialIo>)),
        )
    }

    fn opened(script: &Arc<Mutex<Script>>) -> SerialEcuTransport {
        let mut t = fake(script);
        t.open().unwrap();
        t
    }

    #[test]
    fn ports_sort_by_their_number_not_alphabetically() {
        let mut names: Vec<String> = ["COM10", "COM9", "COM2", "COM1"].map(String::from).to_vec();
        sort_by_number(&mut names);
        assert_eq!(names, ["COM1", "COM2", "COM9", "COM10"]);

        let mut linux: Vec<String> = ["/dev/ttyUSB10", "/dev/ttyUSB2", "/dev/ttyACM0"]
            .map(String::from)
            .to_vec();
        sort_by_number(&mut linux);
        assert_eq!(linux, ["/dev/ttyACM0", "/dev/ttyUSB2", "/dev/ttyUSB10"]);
    }

    #[test]
    fn a_name_without_digits_sorts_last() {
        assert_eq!(port_number("ttyS"), i32::MAX);
        assert_eq!(port_number("COM3"), 3);
        assert_eq!(port_number("99999999999"), i32::MAX);
    }

    #[test]
    fn the_modem_lines_are_asserted_only_when_asked_and_otherwise_untouched() {
        let on = port_settings(115_200, true);
        assert_eq!((on.dtr, on.rts), (Some(true), Some(true)));

        // A MaxxECU's virtual COM port: neither raised nor cleared.
        let alone = port_settings(921_600, false);
        assert_eq!((alone.dtr, alone.rts), (None, None));
        assert_eq!(alone.baud_rate, 921_600);
    }

    #[test]
    fn the_port_is_8n1_with_no_flow_control() {
        use crate::transport::serial_port::{DataBits, Parity, StopBits};
        let s = port_settings(115_200, true);
        assert_eq!(
            (s.data_bits, s.parity, s.stop_bits),
            (DataBits::Eight, Parity::None, StopBits::One)
        );
    }

    #[test]
    fn available_ports_never_fails() {
        // Whatever the machine has, including nothing.
        let _ = SerialEcuTransport::available_ports();
    }

    #[test]
    fn opening_discards_what_was_buffered_before_the_session() {
        let script = Arc::new(Mutex::new(Script::default()));
        script.lock().unwrap().pieces.push_back(vec![1, 2, 3]);

        let mut t = opened(&script);
        assert!(t.is_open());
        assert_eq!(script.lock().unwrap().discards, 1);

        let mut buf = [0u8; 3];
        assert_eq!(t.read(&mut buf, Duration::from_millis(20)).unwrap(), 0);
    }

    #[test]
    fn a_reply_that_arrives_in_pieces_is_read_whole() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = opened(&script);
        script
            .lock()
            .unwrap()
            .pieces
            .extend([vec![1, 2], vec![3], vec![4, 5, 6]]);

        let mut buf = [0u8; 6];
        assert_eq!(t.read(&mut buf, Duration::from_millis(200)).unwrap(), 6);
        assert_eq!(buf, [1, 2, 3, 4, 5, 6]);
    }

    #[test]
    fn a_short_reply_returns_what_arrived_when_the_time_is_up() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = opened(&script);
        script.lock().unwrap().pieces.push_back(vec![9, 8]);

        let mut buf = [0u8; 5];
        let started = Instant::now();
        assert_eq!(t.read(&mut buf, Duration::from_millis(60)).unwrap(), 2);
        assert_eq!(&buf[..2], &[9, 8]);
        assert!(started.elapsed() >= Duration::from_millis(50));
    }

    #[test]
    fn each_read_waits_only_for_the_time_that_is_left() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = opened(&script);
        script.lock().unwrap().timeouts.clear();

        let mut buf = [0u8; 4];
        let _ = t.read(&mut buf, Duration::from_millis(100)).unwrap();

        let s = script.lock().unwrap();
        assert!(!s.timeouts.is_empty());
        assert!(s.timeouts[0] <= Duration::from_millis(100));
        assert!(s.timeouts.windows(2).all(|w| w[1] <= w[0]));
    }

    #[test]
    fn reading_or_writing_before_opening_says_so() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = fake(&script);

        assert!(!t.is_open());
        assert!(matches!(t.write(&[1]), Err(Error::InvalidOperation(m)) if m == "The port is not open."));
        assert!(matches!(
            t.read(&mut [0u8; 1], Duration::ZERO),
            Err(Error::InvalidOperation(_))
        ));
        t.close();
        t.discard_input();
    }

    #[test]
    fn a_write_reaches_the_port_with_the_write_timeout() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = opened(&script);

        t.write(b"Q").unwrap();

        let s = script.lock().unwrap();
        assert_eq!(s.written, b"Q");
        assert_eq!(*s.write_timeouts.last().unwrap(), DEFAULT_WRITE_TIMEOUT);
    }

    #[test]
    fn a_write_that_times_out_is_an_io_error_naming_the_port() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = opened(&script);
        script.lock().unwrap().write_error = Some(io::ErrorKind::TimedOut);

        let Err(Error::Io(e)) = t.write(b"Q") else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().starts_with("COM7 would not accept the request."));
        assert!(e.to_string().contains("incoming one"));
    }

    #[test]
    fn the_write_timeout_widens_for_a_burn_and_ignores_nonsense() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = fake(&script);

        // Closed: the setter does nothing and the getter reports the default.
        t.set_write_timeout(Duration::from_secs(9));
        assert_eq!(t.write_timeout(), DEFAULT_WRITE_TIMEOUT);

        t.open().unwrap();
        t.set_write_timeout(Duration::from_secs(9));
        assert_eq!(t.write_timeout(), Duration::from_secs(9));

        t.write(b"B").unwrap();
        assert_eq!(
            *script.lock().unwrap().write_timeouts.last().unwrap(),
            Duration::from_secs(9)
        );

        t.set_write_timeout(Duration::ZERO);
        assert_eq!(t.write_timeout(), Duration::from_secs(9));
    }

    #[test]
    fn discarding_input_never_fails() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = opened(&script);
        script.lock().unwrap().discard_error = true;

        t.discard_input();
        assert_eq!(script.lock().unwrap().discards, 2);
    }

    #[test]
    fn a_port_that_closes_straight_after_opening_is_reported_as_such() {
        let script = Arc::new(Mutex::new(Script::default()));
        script.lock().unwrap().discard_error = true;
        let mut t = fake(&script);

        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().contains("opened and then closed again"));
        assert!(!t.is_open());
    }

    #[test]
    fn opening_is_retried_for_bluetooth_and_succeeds_on_the_second_attempt() {
        let script = Arc::new(Mutex::new(Script::default()));
        let calls = Arc::new(Mutex::new(0));
        let (s, c) = (script.clone(), calls.clone());
        let mut t = SerialEcuTransport::with_opener(
            "COM9",
            115_200,
            Arc::new(move |_, _| {
                let mut n = c.lock().unwrap();
                *n += 1;
                if *n < 2 {
                    Err(io::Error::other("RFCOMM link failed"))
                } else {
                    Ok(Box::new(FakePort(s.clone())) as Box<dyn SerialIo>)
                }
            }),
        )
        .with_open_attempts(3);

        t.open().unwrap();
        assert!(t.is_open());
        assert_eq!(*calls.lock().unwrap(), 2);
    }

    #[test]
    fn a_cable_is_tried_once() {
        let calls = Arc::new(Mutex::new(0));
        let c = calls.clone();
        let mut t = SerialEcuTransport::with_opener(
            "COM9",
            115_200,
            Arc::new(move |_, _| {
                *c.lock().unwrap() += 1;
                Err(io::Error::new(io::ErrorKind::PermissionDenied, "busy"))
            }),
        );

        assert!(t.open().is_err());
        assert_eq!(*calls.lock().unwrap(), 1);
    }

    #[test]
    fn the_semaphore_timeout_is_explained_in_terms_of_the_device() {
        let mut t = SerialEcuTransport::with_opener(
            "COM12",
            115_200,
            Arc::new(|_, _| Err(io::Error::other("The semaphore timeout period has expired."))),
        );

        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().starts_with("COM12 did not answer."));
        assert!(e.to_string().contains("in range"));
    }

    #[test]
    fn closing_is_idempotent_and_reopening_works() {
        let script = Arc::new(Mutex::new(Script::default()));
        let mut t = opened(&script);
        t.close();
        t.close();
        assert!(!t.is_open());
        t.open().unwrap();
        assert!(t.is_open());
    }

    #[test]
    fn the_transport_is_an_ordinary_transport() {
        fn takes<T: EcuTransport>(_: &T) {}
        let t = SerialEcuTransport::new("COM1", 115_200);
        takes(&t);
        assert_eq!(t.port_name(), "COM1");
        assert_eq!(t.baud_rate(), 115_200);
    }
}
