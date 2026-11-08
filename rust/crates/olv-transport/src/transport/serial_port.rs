//! Our own serial-port layer, replacing the `serialport` crate (MPL-2.0; this project allows
//! only permissive licences). Written for what `SerialEcuTransport.cs` and `FtdiEcuTransport.cs`
//! get from `System.IO.Ports.SerialPort`, and nothing more.
//!
//! One cross-platform [`SerialPort`] and one cross-platform [`available_ports`]:
//!
//! | | port | enumeration |
//! |---|---|---|
//! | Windows | Win32 comm API (`CreateFileW`, `SetCommState`, `SetCommTimeouts`) through `windows-sys` | SetupAPI "Ports" class: hardware id for VID and PID, instance id for the serial number |
//! | Linux | termios and `poll` through `rustix` | `/sys/class/tty/*/device`, walked up to the USB device's `idVendor` |
//! | macOS | termios and `poll` through `rustix` | IOKit (`IOSerialBSDClient`) through `io-kit-sys` and `core-foundation` |
//!
//! # What System.IO.Ports does that this reproduces
//!
//! * **A read returns what has arrived once there is a first byte.** It does not wait to fill
//!   the buffer ([`SerialPort::read`]). Windows gets this from the comm timeouts .NET sets
//!   (`ReadIntervalTimeout = MAXDWORD`, `ReadTotalTimeoutMultiplier = MAXDWORD`,
//!   `ReadTotalTimeoutConstant = ms`); Linux and macOS from `poll` followed by a read that
//!   takes what is there.
//! * **Read and write timeouts are separate** ([`SerialPort::set_read_timeout`],
//!   [`SerialPort::set_write_timeout`]), not one shared value.
//! * **A timeout and a dead port are different errors.** Waiting out the time with nothing to
//!   show is [`io::ErrorKind::TimedOut`]; a port that fails is every other kind. Windows reports
//!   `ERROR_SEM_TIMEOUT` (121) when a Bluetooth link drops, and Rust would name that
//!   `TimedOut` too; it is rewritten to `ConnectionAborted` here, because the C# saw an
//!   `IOException` for it and the transport above treats a timeout as "quiet line".
//! * **Opening is exclusive.** Windows opens with share mode 0; on Linux and macOS `TIOCEXCL` is
//!   requested, so a second opener fails as it does under .NET.
//!
//! # Differences the bench must confirm
//!
//! * Linux and macOS: the port is left in non-blocking mode and every wait is a `poll` with the
//!   deadline, so a write cannot overrun its timeout. (The usual open-nonblocking-then-clear
//!   dance exists to stop a blocking read hanging; here nothing blocks.)
//! * macOS: `poll` on a serial device is unproven on this machine; Apple's man page has long
//!   said it does not support devices, yet USB serial devices work in practice.
//! * Rates: the C# uses 9600, 38400, 115200, 500000 and 921600 (the last for a MaxxECU's FTDI
//!   virtual COM port). All are standard `B*` constants on Linux. Any other rate goes through
//!   `BOTHER` on Linux (`termios2`). On macOS every rate is just the number, which the kernel
//!   hands to the driver; a driver that cannot do it fails `tcsetattr`.
//! * Windows: the 2 ms FTDI latency timer cannot be reached from here (see the FTDI transport).

use std::io;
use std::time::{Duration, Instant};

#[cfg(unix)]
#[path = "serial_port/posix.rs"]
mod sys;
#[cfg(windows)]
#[path = "serial_port/win32.rs"]
mod sys;

// Pure parts of the port enumeration. They are compiled on every OS so the tests run
// everywhere, though only one OS uses each.
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
mod iokit;
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
mod sysfs;
#[cfg_attr(not(windows), allow(dead_code))]
mod windows_ids;

// ----- the settings -------------------------------------------------------------------------

/// Data bits per character.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DataBits {
    Five,
    Six,
    Seven,
    Eight,
}

impl DataBits {
    pub fn bits(self) -> u8 {
        match self {
            Self::Five => 5,
            Self::Six => 6,
            Self::Seven => 7,
            Self::Eight => 8,
        }
    }
}

/// Parity. Mark and space parity (which .NET also has) are not used by any ECU here.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Parity {
    None,
    Odd,
    Even,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StopBits {
    One,
    Two,
}

/// How to open a port. Flow control is always off, as `Handshake.None` was in the C#.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SerialSettings {
    pub baud_rate: u32,
    pub data_bits: DataBits,
    pub parity: Parity,
    pub stop_bits: StopBits,
    /// `Some` drives the line to that level on opening; `None` leaves it as the driver has it
    /// (a MaxxECU's virtual COM port was left alone, as FTDI's D2XX did).
    pub dtr: Option<bool>,
    pub rts: Option<bool>,
}

impl SerialSettings {
    /// 8 data bits, no parity, one stop bit, DTR and RTS left alone.
    pub fn new(baud_rate: u32) -> Self {
        Self {
            baud_rate,
            data_bits: DataBits::Eight,
            parity: Parity::None,
            stop_bits: StopBits::One,
            dtr: None,
            rts: None,
        }
    }

    /// Drives DTR and RTS high (`true`) or low on opening.
    #[must_use]
    pub fn with_lines(mut self, asserted: bool) -> Self {
        self.dtr = Some(asserted);
        self.rts = Some(asserted);
        self
    }
}

// ----- the timeout rules, over a primitive that a fake can stand in for ----------------------

/// What an operating system gives us, reduced to what the timeout rules need.
pub(crate) trait RawIo {
    /// Waits up to `timeout` for input, then reads whatever is there, up to `buf.len()`.
    /// `Ok(0)` means the time passed with nothing to read. A failure is a dead port.
    fn read_within(&mut self, buf: &mut [u8], timeout: Duration) -> io::Result<usize>;

    /// Hands as much of `data` to the driver as it takes within `timeout`. Fewer bytes than
    /// given (down to none) means the time ran out; a failure is a dead port.
    fn write_within(&mut self, data: &[u8], timeout: Duration) -> io::Result<usize>;
}

/// .NET's `TimeoutException`, as an I/O error. The only thing in this module that is `TimedOut`.
fn timed_out() -> io::Error {
    io::Error::new(io::ErrorKind::TimedOut, "The operation has timed out.")
}

/// Whether an error is a timeout (as opposed to a port that has failed).
pub fn is_timeout(e: &io::Error) -> bool {
    matches!(e.kind(), io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock)
}

/// An error from the operating system on a port that is open. Never a timeout: the OS's own
/// "timed out" codes (Windows' 121 from a Bluetooth link that dropped) mean the port failed.
fn dead_port(e: io::Error) -> io::Error {
    if is_timeout(&e) {
        io::Error::new(io::ErrorKind::ConnectionAborted, e)
    } else {
        e
    }
}

/// A [`RawIo`] with a read timeout and a write timeout of its own.
pub(crate) struct Timed<R> {
    io: R,
    read_timeout: Duration,
    write_timeout: Duration,
}

impl<R: RawIo> Timed<R> {
    pub(crate) fn new(io: R) -> Self {
        Self {
            io,
            read_timeout: DEFAULT_TIMEOUT,
            write_timeout: DEFAULT_TIMEOUT,
        }
    }

    /// At least one byte, or [`io::ErrorKind::TimedOut`]: .NET's `SerialPort.Read`.
    pub(crate) fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        match self.io.read_within(buf, self.read_timeout).map_err(dead_port)? {
            0 => Err(timed_out()),
            n => Ok(n),
        }
    }

    /// All of `data`, or [`io::ErrorKind::TimedOut`] if the driver stops taking it before the
    /// write timeout (measured from the start of the call) is up.
    pub(crate) fn write_all(&mut self, mut data: &[u8]) -> io::Result<()> {
        let deadline = Instant::now() + self.write_timeout;
        while !data.is_empty() {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(timed_out());
            }
            match self.io.write_within(data, remaining).map_err(dead_port)? {
                0 => return Err(timed_out()),
                n => data = &data[n.min(data.len())..],
            }
        }
        Ok(())
    }
}

/// What .NET's `SerialPort` starts with (`ReadTimeout = 500`; the C# also sets 500 for writes).
const DEFAULT_TIMEOUT: Duration = Duration::from_millis(500);

/// Writes as much of `data` as goes within `timeout`, over two primitives so the loop is testable.
///
/// `attempt` tries one write: `Ok(Some(n))` took `n` bytes, `Ok(None)` would block. A write that
/// takes `Some(0)` made no progress, and is treated like a block: the deadline is checked and the
/// loop does not spin. `wait` blocks until the device may take more, or `remaining` passes.
#[cfg_attr(not(unix), allow(dead_code))]
pub(crate) fn drive_write(
    data: &[u8],
    timeout: Duration,
    mut attempt: impl FnMut(&[u8]) -> io::Result<Option<usize>>,
    mut wait: impl FnMut(Duration) -> io::Result<()>,
) -> io::Result<usize> {
    let deadline = Instant::now() + timeout;
    let mut done = 0;
    while done < data.len() {
        let took = attempt(&data[done..])?;
        if let Some(n) = took
            && n > 0
        {
            done += n;
            continue;
        }

        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            break;
        }
        wait(remaining)?;
        // A device that says "writable" and then takes nothing (`Some(0)`) would spin on `wait`
        // returning at once; give it a moment, within the deadline.
        if took == Some(0) {
            std::thread::sleep(
                deadline
                    .saturating_duration_since(Instant::now())
                    .min(Duration::from_millis(1)),
            );
        }
    }
    Ok(done)
}

// ----- the port -----------------------------------------------------------------------------

/// An open serial port.
pub struct SerialPort {
    name: String,
    timed: Timed<sys::Port>,
}

impl SerialPort {
    /// Opens `name` (`COM3`, `/dev/ttyUSB0`, `/dev/cu.usbserial-1410`) exclusively.
    pub fn open(name: &str, settings: &SerialSettings) -> io::Result<Self> {
        Ok(Self {
            name: name.to_string(),
            timed: Timed::new(sys::open(name, settings)?),
        })
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    /// How long a read waits for its first byte (500 ms until set).
    pub fn set_read_timeout(&mut self, timeout: Duration) {
        self.timed.read_timeout = timeout;
    }

    /// How long a write may take to be accepted (500 ms until set).
    pub fn set_write_timeout(&mut self, timeout: Duration) {
        self.timed.write_timeout = timeout;
    }

    pub fn read_timeout(&self) -> Duration {
        self.timed.read_timeout
    }

    pub fn write_timeout(&self) -> Duration {
        self.timed.write_timeout
    }

    /// Reads what has arrived, as soon as there is at least one byte: `Ok(n)` with `n >= 1`.
    /// [`io::ErrorKind::TimedOut`] if nothing arrives within the read timeout; any other error
    /// is a port that has failed.
    pub fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        self.timed.read(buf)
    }

    /// Writes all of `data`, or fails with `TimedOut` after the write timeout.
    pub fn write_all(&mut self, data: &[u8]) -> io::Result<()> {
        self.timed.write_all(data)
    }

    /// Drops anything received but not yet read (`DiscardInBuffer`).
    pub fn discard_input(&mut self) -> io::Result<()> {
        self.timed.io.purge(Purge::Input)
    }

    /// Drops anything buffered in either direction.
    pub fn discard_all(&mut self) -> io::Result<()> {
        self.timed.io.purge(Purge::Both)
    }

    pub fn set_dtr(&mut self, asserted: bool) -> io::Result<()> {
        self.timed.io.set_line(Line::Dtr, asserted)
    }

    pub fn set_rts(&mut self, asserted: bool) -> io::Result<()> {
        self.timed.io.set_line(Line::Rts, asserted)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Purge {
    Input,
    Both,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Line {
    Dtr,
    Rts,
}

// ----- fields built from the settings (pure, tested everywhere) ------------------------------

/// The `DCB` fields for a set of settings (Windows). The bit layout is `winbase.h`'s.
#[cfg_attr(not(windows), allow(dead_code))]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct DcbPlan {
    pub baud_rate: u32,
    pub byte_size: u8,
    /// `NOPARITY` 0, `ODDPARITY` 1, `EVENPARITY` 2.
    pub parity: u8,
    /// `ONESTOPBIT` 0, `TWOSTOPBITS` 2.
    pub stop_bits: u8,
    pub dtr: Option<bool>,
    pub rts: Option<bool>,
    pub parity_check: bool,
}

// The `DCB` bit field (`winbase.h`): fBinary is bit 0, fParity bit 1, fDtrControl bits 4-5,
// fTXContinueOnXoff bit 7, fRtsControl bits 12-13.
#[cfg_attr(not(windows), allow(dead_code))]
mod dcb_bits {
    pub(super) const BINARY: u32 = 1 << 0;
    pub(super) const PARITY: u32 = 1 << 1;
    pub(super) const DTR_SHIFT: u32 = 4;
    pub(super) const RTS_SHIFT: u32 = 12;
    pub(super) const CONTROL_MASK: u32 = 0b11;
    /// `DTR_CONTROL_ENABLE` and `RTS_CONTROL_ENABLE`.
    pub(super) const ENABLE: u32 = 1;
    /// `DTR_CONTROL_HANDSHAKE` and `RTS_CONTROL_HANDSHAKE`; 3 is `RTS_CONTROL_TOGGLE`.
    pub(super) const HANDSHAKE: u32 = 2;
    /// `fTXContinueOnXoff`, which .NET sets.
    pub(super) const TX_CONTINUE_ON_XOFF: u32 = 1 << 7;
}

#[cfg_attr(not(windows), allow(dead_code))]
impl DcbPlan {
    pub(crate) fn from_settings(s: &SerialSettings) -> Self {
        Self {
            baud_rate: s.baud_rate,
            byte_size: s.data_bits.bits(),
            parity: match s.parity {
                Parity::None => 0,
                Parity::Odd => 1,
                Parity::Even => 2,
            },
            stop_bits: match s.stop_bits {
                StopBits::One => 0,
                StopBits::Two => 2,
            },
            dtr: s.dtr,
            rts: s.rts,
            parity_check: s.parity != Parity::None,
        }
    }

    /// The `DCB` bit field: `existing` is what `GetCommState` returned, from which only the DTR
    /// and RTS control fields are kept (when the settings leave those lines alone).
    ///
    /// Everything else is what .NET sets: binary mode, parity checking if parity is on, no
    /// CTS/DSR/XON-XOFF flow control, no error character, no null stripping, and no abort on
    /// error. A DTR or RTS field that a previous program left on handshake or toggle is
    /// brought back to plain "enabled": flow control stays off.
    pub(crate) fn flags(&self, existing: u32) -> u32 {
        let keep = |shift: u32, want: Option<bool>| -> u32 {
            let value = match want {
                Some(true) => dcb_bits::ENABLE,
                Some(false) => 0,
                None => match (existing >> shift) & dcb_bits::CONTROL_MASK {
                    dcb_bits::HANDSHAKE | 3 => dcb_bits::ENABLE,
                    kept => kept,
                },
            };
            value << shift
        };

        dcb_bits::BINARY
            | if self.parity_check { dcb_bits::PARITY } else { 0 }
            | keep(dcb_bits::DTR_SHIFT, self.dtr)
            | dcb_bits::TX_CONTINUE_ON_XOFF
            | keep(dcb_bits::RTS_SHIFT, self.rts)
    }
}

/// The `COMMTIMEOUTS` for a read timeout and a write timeout (Windows), as .NET sets them.
#[cfg_attr(not(windows), allow(dead_code))]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct CommTimeouts {
    pub read_interval: u32,
    pub read_multiplier: u32,
    pub read_constant: u32,
    pub write_multiplier: u32,
    pub write_constant: u32,
}

#[cfg_attr(not(windows), allow(dead_code))]
impl CommTimeouts {
    /// A read timeout of zero returns what is already there and nothing more
    /// (`MAXDWORD, 0, 0`). Anything longer is "return as soon as there is a byte, else wait up to
    /// the constant" (`MAXDWORD, MAXDWORD, ms`). A write timeout of zero would mean "forever" to
    /// Windows, so it is held at 1 ms. Whole milliseconds, rounded up so that no wait becomes
    /// none, and kept below `MAXDWORD`, which Windows reserves for the combination above.
    pub(crate) fn new(read: Duration, write: Duration) -> Self {
        let ms = |d: Duration| -> u32 {
            let up = d.as_nanos().div_ceil(1_000_000);
            up.min(u128::from(u32::MAX - 1)) as u32
        };
        let read_ms = ms(read);
        Self {
            read_interval: u32::MAX,
            read_multiplier: if read_ms == 0 { 0 } else { u32::MAX },
            read_constant: read_ms,
            write_multiplier: 0,
            write_constant: ms(write).max(1),
        }
    }
}

/// Every baud rate the Linux kernel has a `B*` constant for. Any other rate is set as a
/// custom one (`BOTHER`, `termios2`). macOS has no such table: a rate is just the number.
#[cfg_attr(not(test), allow(dead_code))]
pub(crate) const STANDARD_BAUD_RATES: &[u32] = &[
    50, 75, 110, 134, 150, 200, 300, 600, 1200, 1800, 2400, 4800, 9600, 19200, 38400, 57600, 115_200, 230_400, 460_800,
    500_000, 576_000, 921_600, 1_000_000, 1_152_000, 1_500_000, 2_000_000, 2_500_000, 3_000_000, 3_500_000, 4_000_000,
];

#[cfg_attr(not(test), allow(dead_code))]
pub(crate) fn is_standard_baud(baud_rate: u32) -> bool {
    STANDARD_BAUD_RATES.contains(&baud_rate)
}

/// The termios settings for a set of settings (Linux and macOS), as plain numbers so they are
/// tested on every OS. The control-mode flags are applied from this in `posix.rs`.
#[cfg_attr(not(unix), allow(dead_code))]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct TermiosPlan {
    pub data_bits: DataBits,
    pub parity: Parity,
    pub two_stop_bits: bool,
    pub baud_rate: u32,
    /// `VMIN`: 0 and `VTIME` 0, so a read returns what is there (waiting is `poll`'s job).
    pub vmin: u8,
    pub vtime: u8,
}

#[cfg_attr(not(unix), allow(dead_code))]
impl TermiosPlan {
    pub(crate) fn from_settings(s: &SerialSettings) -> Self {
        Self {
            data_bits: s.data_bits,
            parity: s.parity,
            two_stop_bits: s.stop_bits == StopBits::Two,
            baud_rate: s.baud_rate,
            vmin: 0,
            vtime: 0,
        }
    }
}

// ----- enumeration --------------------------------------------------------------------------

/// What a USB serial device says about itself.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UsbPortInfo {
    pub vid: u16,
    pub pid: u16,
    pub serial_number: Option<String>,
    pub manufacturer: Option<String>,
    /// The product string. On Windows this is the device's friendly name, which ends in
    /// ` (COMn)`; the caller strips that.
    pub product: Option<String>,
}

/// Which kind of port it is, as far as the operating system will say.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PortType {
    Usb(UsbPortInfo),
    Pci,
    Bluetooth,
    Unknown,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PortInfo {
    pub port_name: String,
    pub port_type: PortType,
}

/// The serial ports the operating system lists, with USB ids where it knows them.
///
/// Windows: the SetupAPI Ports class, plus anything in `HARDWARE\DEVICEMAP\SERIALCOMM` it did
/// not list. Linux: `/sys/class/tty`. macOS: IOKit, or `/dev/cu.*` with no USB detail if IOKit
/// gives nothing.
pub fn available_ports() -> io::Result<Vec<PortInfo>> {
    sys::available_ports()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;

    /// A scripted device: reads and writes follow a list of outcomes, and every timeout it is
    /// given is recorded.
    #[derive(Default)]
    struct Fake {
        reads: VecDeque<io::Result<Vec<u8>>>,
        /// How many bytes each write accepts; `Err` is a dead port.
        writes: VecDeque<io::Result<usize>>,
        read_timeouts: Vec<Duration>,
        write_timeouts: Vec<Duration>,
        written: Vec<u8>,
    }

    impl RawIo for Fake {
        fn read_within(&mut self, buf: &mut [u8], timeout: Duration) -> io::Result<usize> {
            self.read_timeouts.push(timeout);
            match self.reads.pop_front() {
                None => Ok(0),
                Some(Err(e)) => Err(e),
                Some(Ok(bytes)) => {
                    let n = bytes.len().min(buf.len());
                    buf[..n].copy_from_slice(&bytes[..n]);
                    Ok(n)
                }
            }
        }

        fn write_within(&mut self, data: &[u8], timeout: Duration) -> io::Result<usize> {
            self.write_timeouts.push(timeout);
            let n = match self.writes.pop_front() {
                None => data.len(),
                Some(Err(e)) => return Err(e),
                Some(Ok(n)) => n.min(data.len()),
            };
            self.written.extend_from_slice(&data[..n]);
            Ok(n)
        }
    }

    fn timed(fake: Fake) -> Timed<Fake> {
        Timed::new(fake)
    }

    #[test]
    fn a_read_returns_what_has_arrived_without_waiting_to_fill_the_buffer() {
        // .NET: "Read returns as soon as one byte is available, with however many there are".
        let mut t = timed(Fake::default());
        t.io.reads.push_back(Ok(vec![1, 2, 3]));

        let mut buf = [0u8; 512];
        assert_eq!(t.read(&mut buf).unwrap(), 3);
        assert_eq!(&buf[..3], &[1, 2, 3]);
    }

    #[test]
    fn nothing_arriving_is_a_timeout() {
        let mut t = timed(Fake::default());
        let e = t.read(&mut [0u8; 4]).unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::TimedOut);
        assert!(is_timeout(&e));
        assert_eq!(e.to_string(), "The operation has timed out.");
    }

    #[test]
    fn a_dead_port_is_not_a_timeout() {
        let mut t = timed(Fake::default());
        t.io.reads
            .push_back(Err(io::Error::new(io::ErrorKind::BrokenPipe, "gone")));
        let e = t.read(&mut [0u8; 4]).unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::BrokenPipe);
        assert!(!is_timeout(&e));

        t.io.writes
            .push_back(Err(io::Error::new(io::ErrorKind::PermissionDenied, "unplugged")));
        let e = t.write_all(b"Q").unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::PermissionDenied);
    }

    #[test]
    fn an_os_timeout_code_on_an_open_port_is_a_dead_port_not_a_timeout() {
        // Windows' ERROR_SEM_TIMEOUT (121) comes back from a Bluetooth link that dropped, and
        // std names it TimedOut. The C# saw an IOException; the transport above must too, or it
        // reads a dead link as a quiet line.
        let mut t = timed(Fake::default());
        t.io.reads.push_back(Err(io::Error::new(
            io::ErrorKind::TimedOut,
            "The semaphore timeout period has expired.",
        )));
        let e = t.read(&mut [0u8; 4]).unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::ConnectionAborted);
        assert!(e.to_string().contains("semaphore"), "the message survives: {e}");

        t.io.writes.push_back(Err(io::ErrorKind::WouldBlock.into()));
        assert_eq!(t.write_all(b"Q").unwrap_err().kind(), io::ErrorKind::ConnectionAborted);
    }

    #[test]
    fn reads_and_writes_wait_for_their_own_timeouts() {
        let mut t = timed(Fake::default());
        t.read_timeout = Duration::from_millis(30);
        t.write_timeout = Duration::from_secs(5);

        let _ = t.read(&mut [0u8; 4]);
        t.write_all(b"burn").unwrap();

        assert_eq!(t.io.read_timeouts, [Duration::from_millis(30)]);
        // The write is given what is left of its own timeout, never the read's.
        assert!(t.io.write_timeouts[0] > Duration::from_secs(4));
        assert!(t.io.write_timeouts[0] <= Duration::from_secs(5));
    }

    #[test]
    fn both_timeouts_start_at_500_ms_like_the_c_sharp() {
        let t = timed(Fake::default());
        assert_eq!(t.read_timeout, Duration::from_millis(500));
        assert_eq!(t.write_timeout, Duration::from_millis(500));
    }

    #[test]
    fn a_write_the_driver_takes_in_pieces_is_written_whole() {
        let mut t = timed(Fake::default());
        t.io.writes.extend([Ok(2), Ok(1), Ok(10)]);

        t.write_all(&[1, 2, 3, 4, 5, 6]).unwrap();
        assert_eq!(t.io.written, [1, 2, 3, 4, 5, 6]);
        assert_eq!(t.io.write_timeouts.len(), 3);
    }

    #[test]
    fn a_write_the_driver_stops_taking_is_a_timeout() {
        let mut t = timed(Fake::default());
        t.io.writes.extend([Ok(2), Ok(0)]);

        let e = t.write_all(&[1, 2, 3, 4]).unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::TimedOut);
        assert_eq!(t.io.written, [1, 2]);
    }

    #[test]
    fn a_write_that_has_used_its_time_is_a_timeout_without_asking_the_driver() {
        let mut t = timed(Fake::default());
        t.write_timeout = Duration::ZERO;

        assert_eq!(t.write_all(b"Q").unwrap_err().kind(), io::ErrorKind::TimedOut);
        assert!(t.io.write_timeouts.is_empty());
    }

    #[test]
    fn a_write_that_takes_nothing_does_not_spin_and_gives_up_at_the_deadline() {
        // `write` returning Ok(0) with the port reporting writable would loop forever at full
        // speed. It is no progress: the deadline holds and the loop sleeps between tries.
        let mut tries = 0u32;
        let started = Instant::now();
        let wrote = drive_write(
            b"burn",
            Duration::from_millis(40),
            |_| {
                tries += 1;
                Ok(Some(0))
            },
            |_| Ok(()),
        )
        .unwrap();
        assert_eq!(wrote, 0);
        assert!(started.elapsed() >= Duration::from_millis(35));
        assert!(started.elapsed() < Duration::from_secs(2));
        assert!(tries < 200, "{tries} tries means it spun");

        // Through the timeout rules above it, that is a TimedOut error.
        struct Zero;
        impl RawIo for Zero {
            fn read_within(&mut self, _: &mut [u8], _: Duration) -> io::Result<usize> {
                Ok(0)
            }
            fn write_within(&mut self, data: &[u8], t: Duration) -> io::Result<usize> {
                drive_write(data, t, |_| Ok(Some(0)), |_| Ok(()))
            }
        }
        let mut t = Timed::new(Zero);
        t.write_timeout = Duration::from_millis(20);
        assert_eq!(t.write_all(b"Q").unwrap_err().kind(), io::ErrorKind::TimedOut);
    }

    #[test]
    fn a_write_that_blocks_waits_and_then_goes_through() {
        let mut room = [None, None, Some(3usize), Some(10)].into_iter();
        let mut waits = 0;
        let wrote = drive_write(
            &[1, 2, 3, 4, 5, 6],
            Duration::from_secs(1),
            |rest| Ok(room.next().unwrap().map(|n| n.min(rest.len()))),
            |_| {
                waits += 1;
                Ok(())
            },
        )
        .unwrap();
        assert_eq!(wrote, 6);
        assert_eq!(waits, 2);
    }

    #[test]
    fn a_failing_write_is_a_dead_port_and_a_failing_wait_too() {
        let e = drive_write(
            b"x",
            Duration::from_secs(1),
            |_| Err(io::ErrorKind::BrokenPipe.into()),
            |_| Ok(()),
        )
        .unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::BrokenPipe);
        let e = drive_write(
            b"x",
            Duration::from_secs(1),
            |_| Ok(None),
            |_| Err(io::ErrorKind::NotConnected.into()),
        )
        .unwrap_err();
        assert_eq!(e.kind(), io::ErrorKind::NotConnected);
    }

    #[test]
    fn writing_or_reading_nothing_succeeds_at_once() {
        let mut t = timed(Fake::default());
        t.write_all(&[]).unwrap();
        assert_eq!(t.read(&mut []).unwrap(), 0);
        assert!(t.io.write_timeouts.is_empty() && t.io.read_timeouts.is_empty());
    }

    // ----- the Windows DCB ----------------------------------------------------------------

    #[test]
    fn dcb_fields_follow_the_settings() {
        let mut s = SerialSettings::new(921_600);
        s.parity = Parity::Even;
        s.stop_bits = StopBits::Two;
        s.data_bits = DataBits::Seven;
        let plan = DcbPlan::from_settings(&s);

        assert_eq!(plan.baud_rate, 921_600);
        assert_eq!(plan.byte_size, 7);
        assert_eq!(plan.parity, 2);
        assert_eq!(plan.stop_bits, 2);
        assert!(plan.parity_check);

        let n81 = DcbPlan::from_settings(&SerialSettings::new(115_200));
        assert_eq!((n81.byte_size, n81.parity, n81.stop_bits), (8, 0, 0));
        assert!(!n81.parity_check);
        assert_eq!(
            DcbPlan::from_settings(&{
                let mut o = SerialSettings::new(9600);
                o.parity = Parity::Odd;
                o
            })
            .parity,
            1
        );
    }

    #[test]
    fn dcb_flags_are_binary_with_no_flow_control_and_lines_asserted_on_request() {
        let plan = DcbPlan::from_settings(&SerialSettings::new(115_200).with_lines(true));
        let flags = plan.flags(0);

        // fBinary, fTXContinueOnXoff, fDtrControl = ENABLE, fRtsControl = ENABLE.
        assert_eq!(flags, 1 | (1 << 7) | (1 << 4) | (1 << 12));
        // fParity, fOutxCtsFlow, fOutxDsrFlow, fDsrSensitivity, fOutX, fInX, fErrorChar, fNull,
        // fAbortOnError: all clear.
        for bit in [1, 2, 3, 6, 8, 9, 10, 11, 14] {
            assert_eq!(flags & (1 << bit), 0, "bit {bit}");
        }

        let low = DcbPlan::from_settings(&SerialSettings::new(115_200).with_lines(false)).flags(u32::MAX);
        assert_eq!(low & (0b11 << 4), 0, "DTR dropped");
        assert_eq!(low & (0b11 << 12), 0, "RTS dropped");
    }

    #[test]
    fn dcb_flags_set_parity_checking_only_when_parity_is_on() {
        let mut s = SerialSettings::new(9600);
        assert_eq!(DcbPlan::from_settings(&s).flags(0) & dcb_bits::PARITY, 0);
        s.parity = Parity::Odd;
        assert_eq!(DcbPlan::from_settings(&s).flags(0) & dcb_bits::PARITY, dcb_bits::PARITY);
    }

    #[test]
    fn lines_left_alone_keep_what_the_driver_had_but_never_handshake() {
        let plan = DcbPlan::from_settings(&SerialSettings::new(921_600));

        // DTR enabled, RTS disabled: kept as they were.
        assert_eq!(plan.flags(1 << 4) & (0b11 << 4), 1 << 4);
        assert_eq!(plan.flags(1 << 4) & (0b11 << 12), 0);
        // DTR on handshake, RTS on toggle (a previous program's flow control): plain enable.
        let was = (2 << 4) | (3 << 12) | (1 << 8) | (1 << 9) | (1 << 2);
        let flags = plan.flags(was);
        assert_eq!((flags >> 4) & 0b11, 1);
        assert_eq!((flags >> 12) & 0b11, 1);
        assert_eq!(
            flags & ((1 << 8) | (1 << 9) | (1 << 2)),
            0,
            "software and CTS flow control off"
        );
    }

    #[test]
    fn comm_timeouts_return_at_the_first_byte_like_net() {
        let t = CommTimeouts::new(Duration::from_millis(500), Duration::from_millis(250));
        assert_eq!(
            t,
            CommTimeouts {
                read_interval: u32::MAX,
                read_multiplier: u32::MAX,
                read_constant: 500,
                write_multiplier: 0,
                write_constant: 250,
            }
        );
    }

    #[test]
    fn a_zero_read_timeout_returns_only_what_is_already_there() {
        let t = CommTimeouts::new(Duration::ZERO, Duration::from_millis(500));
        assert_eq!((t.read_interval, t.read_multiplier, t.read_constant), (u32::MAX, 0, 0));
    }

    #[test]
    fn comm_timeouts_round_up_and_never_mean_forever() {
        // Under a millisecond is still a wait; a zero write timeout would be "no timeout".
        let t = CommTimeouts::new(Duration::from_micros(100), Duration::ZERO);
        assert_eq!(t.read_constant, 1);
        assert_eq!(t.write_constant, 1);

        // MAXDWORD is reserved in the read fields; a huge timeout stops just short of it.
        let t = CommTimeouts::new(Duration::from_secs(u64::MAX / 2), Duration::from_secs(1 << 40));
        assert_eq!(t.read_constant, u32::MAX - 1);
        assert_eq!(t.write_constant, u32::MAX - 1);
    }

    // ----- termios ------------------------------------------------------------------------

    #[test]
    fn termios_plan_follows_the_settings_and_reads_without_blocking() {
        let plan = TermiosPlan::from_settings(&SerialSettings::new(115_200));
        assert_eq!(plan.data_bits, DataBits::Eight);
        assert_eq!(plan.parity, Parity::None);
        assert!(!plan.two_stop_bits);
        assert_eq!((plan.vmin, plan.vtime), (0, 0));

        let mut s = SerialSettings::new(9600);
        s.stop_bits = StopBits::Two;
        s.parity = Parity::Even;
        let plan = TermiosPlan::from_settings(&s);
        assert!(plan.two_stop_bits);
        assert_eq!(plan.parity, Parity::Even);
        assert_eq!(plan.baud_rate, 9600);
    }

    #[test]
    fn every_rate_the_c_sharp_uses_is_a_standard_linux_rate() {
        // Elm327Source.BaudRates, SerialEcuTransport's default, and MaxxUsbProtocol.BaudRate.
        for rate in [9600, 38_400, 115_200, 500_000, 921_600, 230_400] {
            assert!(is_standard_baud(rate), "{rate}");
        }
        // Not a B* constant: goes through BOTHER on Linux.
        assert!(!is_standard_baud(250_000));
        assert!(!is_standard_baud(74_880));
    }

    #[test]
    fn data_bits_count() {
        assert_eq!(
            [DataBits::Five, DataBits::Six, DataBits::Seven, DataBits::Eight].map(DataBits::bits),
            [5, 6, 7, 8]
        );
    }

    #[test]
    fn listing_ports_never_fails() {
        // Whatever the machine has, including nothing.
        let _ = available_ports();
    }
}
