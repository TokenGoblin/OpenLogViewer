//! The serial port on Linux and macOS: termios and `poll`, through `rustix`.
//!
//! Open with `O_NOCTTY | O_NONBLOCK` (so opening never waits for carrier and the port never
//! becomes our controlling terminal), put the line in raw mode at the requested rate with
//! `VMIN = VTIME = 0`, and do every wait with `poll` against a deadline. The descriptor stays
//! non-blocking: with only `poll` doing the waiting, a write cannot overrun its timeout the
//! way a blocking `write` on a stalled port can.
//!
//! Almost all of this is safe code. The `unsafe` is `ioctl`, which `rustix` marks unsafe
//! because the request number and the argument type must agree: `TIOCEXCL` (exclusive open)
//! and `TIOCMBIS`/`TIOCMBIC` (DTR and RTS).

use std::io;
use std::os::fd::{AsFd, BorrowedFd, OwnedFd};
use std::os::raw::c_int;
use std::time::{Duration, Instant};

use rustix::event::{PollFd, PollFlags, Timespec, poll};
use rustix::fs::{self as rfs, Mode, OFlags};
use rustix::io::Errno;
use rustix::ioctl::{self, NoArg, Opcode, Setter};
use rustix::termios::{
    ControlModes, InputModes, OptionalActions, QueueSelector, SpecialCodeIndex, Termios, tcflush, tcgetattr, tcsetattr,
};

use super::{DataBits, Line, Parity, Purge, RawIo, SerialSettings, TermiosPlan, drive_write};

// The ioctl request numbers for the modem lines and exclusive access. Linux's are fixed
// numbers on the architectures below; macOS builds them with the BSD `_IO`/`_IOW` macros.
#[cfg(all(
    target_os = "linux",
    any(
        target_arch = "x86",
        target_arch = "x86_64",
        target_arch = "arm",
        target_arch = "aarch64",
        target_arch = "riscv32",
        target_arch = "riscv64",
        target_arch = "loongarch64",
        target_arch = "s390x"
    )
))]
mod request {
    use super::Opcode;
    pub(super) const TIOCEXCL: Opcode = 0x540C;
    pub(super) const TIOCMBIS: Opcode = 0x5416;
    pub(super) const TIOCMBIC: Opcode = 0x5417;
}
#[cfg(target_os = "macos")]
mod request {
    use super::{Opcode, c_int, ioctl};
    pub(super) const TIOCEXCL: Opcode = ioctl::opcode::none(b't', 13);
    pub(super) const TIOCMBIS: Opcode = ioctl::opcode::write::<c_int>(b't', 108);
    pub(super) const TIOCMBIC: Opcode = ioctl::opcode::write::<c_int>(b't', 107);
}
#[cfg(not(any(
    target_os = "macos",
    all(
        target_os = "linux",
        any(
            target_arch = "x86",
            target_arch = "x86_64",
            target_arch = "arm",
            target_arch = "aarch64",
            target_arch = "riscv32",
            target_arch = "riscv64",
            target_arch = "loongarch64",
            target_arch = "s390x"
        )
    )
)))]
compile_error!("serial_port: no ioctl request numbers for this operating system or architecture");

const TIOCM_DTR: c_int = 0x002;
const TIOCM_RTS: c_int = 0x004;

pub(super) struct Port {
    fd: OwnedFd,
}

fn errno(e: Errno) -> io::Error {
    io::Error::from(e)
}

pub(super) fn open(name: &str, settings: &SerialSettings) -> io::Result<Port> {
    let fd = rfs::open(
        name,
        OFlags::RDWR | OFlags::NOCTTY | OFlags::NONBLOCK | OFlags::CLOEXEC,
        Mode::empty(),
    )
    .map_err(errno)?;

    // Exclusive, as a .NET `SerialPort` is (a second opener gets EBUSY). Best effort: a driver
    // that does not support it is still a usable port.
    let _ = exclusive(&fd);

    configure(&fd, &TermiosPlan::from_settings(settings)).map_err(|e| {
        if e.raw_os_error() == Some(Errno::NOTTY.raw_os_error()) {
            io::Error::new(io::ErrorKind::InvalidInput, format!("{name} is not a serial port."))
        } else {
            e
        }
    })?;

    let mut port = Port { fd };
    if let Some(on) = settings.dtr {
        port.set_line(Line::Dtr, on)?;
    }
    if let Some(on) = settings.rts {
        port.set_line(Line::Rts, on)?;
    }
    Ok(port)
}

/// Raw mode, the plan's frame format and rate, the receiver on, modem control ignored
/// (`CLOCAL`), no flow control, and reads that return at once with whatever is there.
fn configure(fd: &OwnedFd, plan: &TermiosPlan) -> io::Result<()> {
    let mut t = tcgetattr(fd).map_err(errno)?;
    apply_plan(&mut t, plan)?;
    tcsetattr(fd, OptionalActions::Now, &t).map_err(errno)
}

fn apply_plan(t: &mut Termios, plan: &TermiosPlan) -> io::Result<()> {
    t.make_raw();
    // `make_raw` leaves the input flow-control flags a previous program set: with XOFF on the
    // port would emit 0x11/0x13 into the ECU's stream, and IXANY or IUCLC would rewrite bytes.
    t.input_modes
        .remove(InputModes::IXON | InputModes::IXOFF | InputModes::IXANY);
    #[cfg(target_os = "linux")]
    t.input_modes.remove(InputModes::IUCLC);

    t.control_modes.remove(
        ControlModes::CSIZE
            | ControlModes::PARENB
            | ControlModes::PARODD
            | ControlModes::CSTOPB
            | ControlModes::CRTSCTS,
    );
    t.control_modes.insert(
        match plan.data_bits {
            DataBits::Five => ControlModes::CS5,
            DataBits::Six => ControlModes::CS6,
            DataBits::Seven => ControlModes::CS7,
            DataBits::Eight => ControlModes::CS8,
        } | ControlModes::CREAD
            | ControlModes::CLOCAL,
    );
    match plan.parity {
        Parity::None => {}
        Parity::Even => {
            t.control_modes.insert(ControlModes::PARENB);
            t.input_modes.insert(InputModes::INPCK);
        }
        Parity::Odd => {
            t.control_modes.insert(ControlModes::PARENB | ControlModes::PARODD);
            t.input_modes.insert(InputModes::INPCK);
        }
    }
    if plan.two_stop_bits {
        t.control_modes.insert(ControlModes::CSTOPB);
    }

    t.special_codes[SpecialCodeIndex::VMIN] = plan.vmin;
    t.special_codes[SpecialCodeIndex::VTIME] = plan.vtime;

    // Linux: a `B*` constant for a standard rate, `BOTHER` and `termios2` for any other.
    // macOS: the number itself.
    t.set_speed(plan.baud_rate).map_err(errno)
}

#[allow(unsafe_code)]
fn exclusive(fd: &OwnedFd) -> io::Result<()> {
    // SAFETY: `TIOCEXCL` takes no argument, which is what `NoArg` passes.
    unsafe { ioctl::ioctl(fd, NoArg::<{ request::TIOCEXCL }>::new()) }.map_err(errno)
}

#[allow(unsafe_code)]
fn modem_bits(fd: &OwnedFd, set: bool, bits: c_int) -> io::Result<()> {
    // SAFETY: `TIOCMBIS` and `TIOCMBIC` each read one `int` of modem-line bits from the pointer
    // `Setter` passes, and `Setter<_, c_int>` supplies exactly that.
    unsafe {
        if set {
            ioctl::ioctl(fd, Setter::<{ request::TIOCMBIS }, c_int>::new(bits))
        } else {
            ioctl::ioctl(fd, Setter::<{ request::TIOCMBIC }, c_int>::new(bits))
        }
    }
    .map_err(errno)
}

impl Port {
    pub(super) fn purge(&mut self, which: Purge) -> io::Result<()> {
        tcflush(
            &self.fd,
            match which {
                Purge::Input => QueueSelector::IFlush,
                Purge::Both => QueueSelector::IOFlush,
            },
        )
        .map_err(errno)
    }

    pub(super) fn set_line(&mut self, line: Line, asserted: bool) -> io::Result<()> {
        modem_bits(
            &self.fd,
            asserted,
            match line {
                Line::Dtr => TIOCM_DTR,
                Line::Rts => TIOCM_RTS,
            },
        )
    }
}

/// Waits for `want` on `fd`. `None` is the time running out; `Some(flags)` is what `poll`
/// reported, which is empty if a signal interrupted it (the caller just goes round again).
fn wait(fd: BorrowedFd<'_>, want: PollFlags, timeout: Duration) -> io::Result<Option<PollFlags>> {
    let limit = Timespec {
        tv_sec: i64::try_from(timeout.as_secs()).unwrap_or(i64::MAX),
        tv_nsec: i64::from(timeout.subsec_nanos()),
    };
    let mut fds = [PollFd::from_borrowed_fd(fd, want)];
    match poll(&mut fds, Some(&limit)) {
        Ok(0) => Ok(None),
        Ok(_) => Ok(Some(fds[0].revents())),
        Err(Errno::INTR) => Ok(Some(PollFlags::empty())),
        Err(e) => Err(errno(e)),
    }
}

/// A port that has gone: unplugged, or the other end of a pty closed.
fn gone() -> io::Error {
    io::Error::new(io::ErrorKind::NotConnected, "The serial port is no longer connected.")
}

impl RawIo for Port {
    fn read_within(&mut self, buf: &mut [u8], timeout: Duration) -> io::Result<usize> {
        let deadline = Instant::now() + timeout;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            let Some(events) = wait(self.fd.as_fd(), PollFlags::IN, remaining)? else {
                return Ok(0);
            };

            if events.contains(PollFlags::NVAL) {
                return Err(errno(Errno::BADF));
            }
            if events.contains(PollFlags::IN) {
                match rustix::io::read(&self.fd, &mut *buf) {
                    Ok(0) if events.contains(PollFlags::HUP) => return Err(gone()),
                    // Readable and then nothing: another thread flushed, or a spurious wake.
                    Ok(0) | Err(Errno::AGAIN | Errno::INTR) => {}
                    Ok(n) => return Ok(n),
                    // A USB adapter pulled out reads as EIO on Linux.
                    Err(e) => return Err(errno(e)),
                }
            } else if events.intersects(PollFlags::HUP | PollFlags::ERR) {
                return Err(gone());
            }

            if remaining.is_zero() {
                return Ok(0);
            }
        }
    }

    fn write_within(&mut self, data: &[u8], timeout: Duration) -> io::Result<usize> {
        let fd = &self.fd;
        drive_write(
            data,
            timeout,
            |rest| loop {
                match rustix::io::write(fd, rest) {
                    Ok(n) => return Ok(Some(n)),
                    Err(Errno::INTR) => {}
                    Err(Errno::AGAIN) => return Ok(None),
                    Err(e) => return Err(errno(e)),
                }
            },
            |remaining| match wait(fd.as_fd(), PollFlags::OUT, remaining)? {
                Some(events) if events.intersects(PollFlags::NVAL | PollFlags::ERR | PollFlags::HUP) => Err(gone()),
                _ => Ok(()),
            },
        )
    }
}

pub(super) fn available_ports() -> io::Result<Vec<super::PortInfo>> {
    #[cfg(target_os = "linux")]
    {
        super::sysfs::list(std::path::Path::new("/sys"), "/dev")
    }
    #[cfg(target_os = "macos")]
    {
        super::iokit::native::available_ports()
    }
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::*;
    use crate::transport::serial_port::{SerialPort, is_timeout};
    use rustix::pty::{OpenptFlags, grantpt, openpt, ptsname, unlockpt};
    use rustix::termios::{LocalModes, SpecialCodeIndex};

    /// A pty: the master side to play the ECU, and the slave's path to open as a serial port.
    /// Needs no hardware, only `/dev/ptmx`.
    fn pty() -> (OwnedFd, String) {
        let master = openpt(OpenptFlags::RDWR | OpenptFlags::NOCTTY).unwrap();
        grantpt(&master).unwrap();
        unlockpt(&master).unwrap();
        let name = ptsname(&master, Vec::new()).unwrap().into_string().unwrap();
        (master, name)
    }

    #[test]
    fn opening_applies_raw_8n1_at_the_rate_with_reads_that_do_not_block() {
        let (_master, name) = pty();
        let port = SerialPort::open(&name, &SerialSettings::new(115_200)).unwrap();
        drop(port);

        let slave = rfs::open(&name, OFlags::RDWR | OFlags::NOCTTY | OFlags::NONBLOCK, Mode::empty()).unwrap();
        // A previous program's software flow control must not survive: it would put 0x11/0x13 into
        // the stream and rewrite bytes.
        let mut dirty = tcgetattr(&slave).unwrap();
        dirty
            .input_modes
            .insert(InputModes::IXON | InputModes::IXOFF | InputModes::IXANY | InputModes::IUCLC);
        tcsetattr(&slave, OptionalActions::Now, &dirty).unwrap();
        // Closing reset nothing we care about; re-apply and read back.
        configure(&slave, &TermiosPlan::from_settings(&SerialSettings::new(115_200))).unwrap();
        let t = tcgetattr(&slave).unwrap();
        assert!(t.control_modes.contains(ControlModes::CS8));
        assert!(
            !t.control_modes
                .intersects(ControlModes::PARENB | ControlModes::CSTOPB | ControlModes::CRTSCTS)
        );
        assert!(t.control_modes.contains(ControlModes::CLOCAL | ControlModes::CREAD));
        assert!(!t.local_modes.intersects(LocalModes::ICANON | LocalModes::ECHO));
        assert_eq!(t.special_codes[SpecialCodeIndex::VMIN], 0);
        assert_eq!(t.special_codes[SpecialCodeIndex::VTIME], 0);
        assert!(
            !t.input_modes
                .intersects(InputModes::IXON | InputModes::IXOFF | InputModes::IXANY | InputModes::IUCLC)
        );
        assert_eq!(t.output_speed(), 115_200);
    }

    #[test]
    fn a_read_returns_what_arrived_and_a_quiet_line_times_out() {
        let (master, name) = pty();
        let mut port = SerialPort::open(&name, &SerialSettings::new(115_200)).unwrap();
        port.set_read_timeout(Duration::from_millis(50));

        let started = Instant::now();
        let e = port.read(&mut [0u8; 8]).unwrap_err();
        assert!(is_timeout(&e));
        assert!(started.elapsed() >= Duration::from_millis(40));

        rustix::io::write(&master, &[1, 2, 3]).unwrap();
        let mut buf = [0u8; 512];
        assert_eq!(port.read(&mut buf).unwrap(), 3);
        assert_eq!(&buf[..3], &[1, 2, 3]);
    }

    #[test]
    fn a_write_reaches_the_other_end() {
        let (master, name) = pty();
        let mut port = SerialPort::open(&name, &SerialSettings::new(115_200)).unwrap();

        port.write_all(b"Q").unwrap();
        let mut got = [0u8; 1];
        assert_eq!(rustix::io::read(&master, &mut got[..]).unwrap(), 1);
        assert_eq!(&got, b"Q");
    }

    #[test]
    fn a_closed_far_end_is_a_dead_port_not_a_timeout() {
        let (master, name) = pty();
        let mut port = SerialPort::open(&name, &SerialSettings::new(115_200)).unwrap();
        port.set_read_timeout(Duration::from_millis(200));
        drop(master);

        let e = port.read(&mut [0u8; 8]).unwrap_err();
        assert!(!is_timeout(&e), "{e}");
    }

    #[test]
    fn discarding_input_drops_what_was_waiting() {
        let (master, name) = pty();
        let mut port = SerialPort::open(&name, &SerialSettings::new(115_200)).unwrap();
        port.set_read_timeout(Duration::from_millis(30));

        rustix::io::write(&master, &[9, 9, 9]).unwrap();
        port.discard_input().unwrap();
        assert!(is_timeout(&port.read(&mut [0u8; 8]).unwrap_err()));
    }

    #[test]
    fn a_file_that_is_not_a_serial_port_is_refused() {
        let e = SerialPort::open("/dev/null", &SerialSettings::new(9600)).err().unwrap();
        assert_eq!(e.kind(), io::ErrorKind::InvalidInput);
        assert!(e.to_string().contains("not a serial port"));
    }
}
