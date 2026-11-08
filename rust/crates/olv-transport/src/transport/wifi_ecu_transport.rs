//! Ported from `WifiEcuTransport.cs`. Owned by package `w2-transports`.
//!
//! A Wi-Fi OBD2 adapter, presented as a byte stream.
//!
//! The third radio these dongles come with, after Bluetooth Classic and BLE, and the one that
//! is invisible to everything else here: it becomes no serial port, it pairs with nothing, and
//! the operating system lists it nowhere. A Vgate iCar Pro Wi-Fi plugged into a car is simply
//! a wireless access point (`V-LINK`) with a TCP socket behind it, and the only way to find it
//! is to know where to look.
//!
//! What arrives over that socket is the same ASCII ELM327 conversation the other two carry, so
//! this is a byte stream and nothing else.
//!
//! The one thing that is genuinely different is what has to be true before it works. A
//! Bluetooth adapter is reached from a computer that is doing whatever else it likes; a Wi-Fi
//! one requires this computer to leave its own network and join the dongle's, which has no
//! route to anything. Windows treats a network with no internet as a mistake and quietly hops
//! back to one that has some, so a link that was up a moment ago fails with the machine
//! apparently connected to Wi-Fi, and the failure is on the other side of the room from its
//! cause. That is what the connect error is for.
//!
//! # Differences the bench must confirm
//!
//! This has never met the real Vgate dongle in either language. Beyond that:
//!
//! * .NET's `ConnectAsync(host, port)` tries every address a name resolves to under one
//!   five-second cancellation; here each address is tried in turn with whatever is left of the
//!   five seconds. A name lookup itself cannot be cancelled, so a slow resolver can overrun it.
//! * .NET reports a timed-out receive as a `SocketException`; std reports `WouldBlock` (Unix)
//!   or `TimedOut` (Windows). Both end the read, as in the C#.
//! * std has no `Socket.Available`, so discarding drains with a non-blocking read instead.

use std::io::{self, Read, Write};
use std::net::{Shutdown, TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};

use olv_core::fmt;
use olv_core::{Error, Result};
use olv_protocol::ms::ms_protocol::EcuTransport;

/// Where a Vgate iCar Pro Wi-Fi answers, and most of the clones with it.
///
/// The dongle runs the access point and hands out addresses on it, so this is its own address
/// on its own network rather than anything a router assigned; it is fixed in the firmware and
/// the same on every one of them.
pub const DEFAULT_HOST: &str = "192.168.0.10";

/// The port those adapters listen on.
pub const DEFAULT_PORT: u16 = 35000;

/// Endpoints worth trying, in order.
///
/// Told apart deliberately, because they are known to different standards. The first is where
/// a Vgate iCar Pro Wi-Fi answers (verified on a 2014 Subaru, though by a different client on
/// the same dongle rather than by this one). The second is what a number of other Wi-Fi ELM327
/// clones ship with; it is widely reported and has not been checked here, which is why it is
/// second and not a default.
///
/// Guessing an address is cheap in a way guessing a scaling factor is not: a wrong one refuses
/// the connection in a second and says so, where a wrong scaling produces a gauge that is
/// confidently incorrect. So trying both is reasonable and inventing either would not have been.
pub const KNOWN_ADDRESSES: [&str; 2] = ["192.168.0.10:35000", "192.168.4.1:35000"];

/// A Wi-Fi OBD2 adapter as a byte stream.
pub struct WifiEcuTransport {
    host: String,
    port: u16,
    connect_within: Duration,
    socket: Option<TcpStream>,
    /// Set when the adapter closes the connection, which is not the same as its having nothing
    /// to say.
    ///
    /// A stream socket reports a closed peer by returning zero bytes, and it does so
    /// immediately and for ever after. Read as "no data yet" that spins the read loop at full
    /// speed until the timeout runs out, on every request, for the rest of the session.
    ended: bool,
}

impl WifiEcuTransport {
    /// A transport for `host:port`. The host is trimmed; blank is refused, as is a port outside
    /// 1 to 65535.
    pub fn new(host: &str, port: i32) -> Result<Self> {
        if host.trim().is_empty() {
            return Err(Error::Argument(
                "The value cannot be an empty string or composed entirely of whitespace. (Parameter 'host')".into(),
            ));
        }
        let port = u16::try_from(port).ok().filter(|p| *p >= 1).ok_or_else(|| {
            Error::Argument(format!(
                "port ('{port}') must be between 1 and 65535. (Parameter 'port')"
            ))
        })?;

        Ok(Self {
            host: host.trim().to_string(),
            port,
            connect_within: Duration::from_secs(5),
            socket: None,
            ended: false,
        })
    }

    /// An endpoint written as "host" or "host:port", the port defaulting to [`DEFAULT_PORT`].
    ///
    /// Anything that is not a number after the colon is part of the host, so a name with a
    /// colon in it is not silently truncated to something that does not resolve. (A bare IPv6
    /// literal ending in digits is read as host and port, as in the C#.)
    pub fn at(address: &str) -> Result<Self> {
        if address.trim().is_empty() {
            return Err(Error::Argument(
                "The value cannot be an empty string or composed entirely of whitespace. (Parameter 'address')".into(),
            ));
        }

        let text = address.trim();
        // `LastIndexOf(':')` is a UTF-16 index in the C#; the split is the same either way.
        if let Some(colon) = text.rfind(':').filter(|c| *c > 0)
            && let Some(port) = fmt::parse_i32(&text[colon + 1..]).filter(|p| (1..=65535).contains(p))
        {
            return Self::new(&text[..colon], port);
        }

        Self::new(text, i32::from(DEFAULT_PORT))
    }

    pub fn host(&self) -> &str {
        &self.host
    }

    pub fn port(&self) -> u16 {
        self.port
    }

    /// How the endpoint is written, and what the menu and messages call it.
    pub fn address(&self) -> String {
        format!("{}:{}", self.host, self.port)
    }

    /// How long to wait for the socket to open.
    ///
    /// Long enough to cross a link that is up and short enough that finding out it is not does
    /// not feel like a hang. A dongle that is not there answers in milliseconds (nothing on its
    /// network refuses the connection), so this is really the allowance for one that is there
    /// and busy.
    #[must_use]
    pub fn with_connect_within(mut self, connect_within: Duration) -> Self {
        self.connect_within = connect_within;
        self
    }

    /// Why a Wi-Fi adapter did not answer, in terms of the three things that are actually
    /// wrong when it does not.
    ///
    /// Worth the words. The underlying message is "No connection could be made because the
    /// target machine actively refused it" or a bare cancellation, and neither says the thing
    /// the user needs to do, which is nearly always to look at which network this computer is on.
    fn explain(&self, cause: &io::Error, timed_out: bool) -> String {
        let reason = if timed_out {
            format!(
                "The connection was still being attempted after {} seconds, which is what a \
                 network with no dongle on it looks like.\n\n",
                fmt::f64(self.connect_within.as_secs_f64(), "0.#")
            )
        } else {
            format!("{cause}\n\n")
        };

        format!(
            "Nothing answered at {}. {reason}\
             A Wi-Fi dongle is its own access point rather than something on your network, so:\n\n\
             \u{2022} join its Wi-Fi first \u{2014} a Vgate iCar Pro publishes V-LINK \u{2014} and check Windows has \
             stayed on it. A network with no internet gets dropped for one that has some, often \
             within seconds of joining it.\n\
             \u{2022} close any phone app that is using the dongle. These accept one connection at a \
             time, and a second one is refused rather than queued.\n\
             \u{2022} check the ignition is on, and that the adapter's own address is the one above.",
            self.address()
        )
    }

    /// Connects to the first resolved address that answers, within the overall allowance.
    fn connect(&self) -> std::result::Result<TcpStream, (io::Error, bool)> {
        let started = Instant::now();
        let addrs: Vec<_> = (self.host.as_str(), self.port)
            .to_socket_addrs()
            .map_err(|e| (e, false))?
            .collect();

        let mut last: Option<io::Error> = None;
        for addr in addrs {
            let left = self.connect_within.saturating_sub(started.elapsed());
            if left.is_zero() {
                return Err((io::ErrorKind::TimedOut.into(), true));
            }
            match TcpStream::connect_timeout(&addr, left) {
                Ok(stream) => return Ok(stream),
                Err(e) if e.kind() == io::ErrorKind::TimedOut => return Err((e, true)),
                Err(e) => last = Some(e),
            }
        }

        Err((
            last.unwrap_or_else(|| io::Error::other("The host name resolved to no addresses.")),
            false,
        ))
    }
}

impl EcuTransport for WifiEcuTransport {
    fn is_open(&self) -> bool {
        self.socket.is_some() && !self.ended
    }

    fn open(&mut self) -> Result<()> {
        if self.is_open() {
            return Ok(());
        }

        self.close();

        match self.connect() {
            Ok(stream) => {
                // An ELM327 command is a handful of bytes and every one of them is waited on.
                // Nagle would hold each request back looking for company that is not coming,
                // which on a link already limited to one request at a time is pure latency.
                let _ = stream.set_nodelay(true);
                self.socket = Some(stream);
                self.ended = false;
                Ok(())
            }
            Err((cause, timed_out)) => Err(Error::Io(io::Error::new(cause.kind(), self.explain(&cause, timed_out)))),
        }
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let address = self.address();
        let Some(socket) = self.socket.as_mut() else {
            return Err(Error::InvalidOperation("The adapter is not open.".into()));
        };

        // A stream socket may take fewer bytes than it was offered, and a command that arrives
        // without its carriage return is one the adapter waits for the rest of rather than one
        // it refuses; write_all keeps going until it has all of it.
        socket.write_all(data).map_err(|e| {
            Error::Io(io::Error::new(
                e.kind(),
                format!("The adapter at {address} would not take the request: {e}"),
            ))
        })
    }

    /// Fills the buffer, or returns fewer bytes if the timeout passes first: the same contract
    /// as the serial port, so nothing above here has to know which it has.
    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        let Some(socket) = self.socket.as_mut() else {
            return Err(Error::InvalidOperation("The adapter is not open.".into()));
        };

        let mut total = 0;
        let deadline = Instant::now() + timeout;

        while total < buffer.len() && !self.ended {
            let remaining = deadline.saturating_duration_since(Instant::now()).as_millis() as u64;
            if remaining == 0 {
                break;
            }

            // The socket's own blocking read rather than polling: this returns the moment the
            // prompt arrives, which is what makes an exchange cost what the car took rather
            // than what the timeout allows.
            if socket.set_read_timeout(Some(Duration::from_millis(remaining))).is_err() {
                self.ended = true;
                break;
            }

            match socket.read(&mut buffer[total..]) {
                // Zero bytes from a stream socket is the far end having closed, for ever,
                // rather than a quiet moment.
                Ok(0) => {
                    self.ended = true;
                    break;
                }
                Ok(n) => total += n,
                Err(e) if matches!(e.kind(), io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock) => {
                    break;
                }
                Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
                Err(_) => {
                    // The link has gone. Reported as what was read rather than as an error,
                    // because that is what a serial port whose adapter has been unplugged
                    // does, and the caller above already treats a round of silence as a link
                    // to recover.
                    self.ended = true;
                    break;
                }
            }
        }

        Ok(total)
    }

    /// Drops anything already arrived.
    ///
    /// Whatever is buffered belongs to the previous exchange (a reply that came after its
    /// timeout, or the tail of an abandoned one) and read as the front of the next answer it
    /// decodes as a different reading altogether.
    fn discard_input(&mut self) {
        let Some(socket) = self.socket.as_mut() else {
            return;
        };

        if socket.set_nonblocking(true).is_err() {
            return;
        }

        let mut discard = [0u8; 512];
        loop {
            match socket.read(&mut discard) {
                Ok(0) => {
                    self.ended = true;
                    break;
                }
                Ok(_) => {}
                Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
                // Nothing left, or hygiene before a request that cannot be done: a link that
                // cannot be drained will say so again on the read that follows.
                Err(_) => break,
            }
        }

        let _ = socket.set_nonblocking(false);
    }

    fn close(&mut self) {
        if let Some(socket) = self.socket.take() {
            // Shut down first so the adapter sees the connection go rather than being left
            // holding it. These take one client at a time, and one that is dropped without
            // notice keeps the slot for a minute or so, which presents as a dongle that refuses
            // the next connection for no visible reason. A socket whose far end has already
            // gone cannot be closed politely; that is not worth reporting.
            let _ = socket.shutdown(Shutdown::Both);
        }
        self.ended = false;
    }
}

impl Drop for WifiEcuTransport {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;
    use std::thread;

    fn listener() -> (TcpListener, u16) {
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = l.local_addr().unwrap().port();
        (l, port)
    }

    fn connected(port: u16) -> WifiEcuTransport {
        let mut t = WifiEcuTransport::new("127.0.0.1", i32::from(port)).unwrap();
        t.open().unwrap();
        t
    }

    #[test]
    fn the_known_addresses_are_the_vgate_first_then_the_other_clones() {
        assert_eq!(KNOWN_ADDRESSES, ["192.168.0.10:35000", "192.168.4.1:35000"]);
        assert_eq!(format!("{DEFAULT_HOST}:{DEFAULT_PORT}"), KNOWN_ADDRESSES[0]);
    }

    #[test]
    fn an_address_is_host_or_host_and_port() {
        let t = WifiEcuTransport::at("192.168.0.10").unwrap();
        assert_eq!((t.host(), t.port()), ("192.168.0.10", 35000));

        let t = WifiEcuTransport::at("  192.168.4.1:6789 ").unwrap();
        assert_eq!((t.host(), t.port()), ("192.168.4.1", 6789));
        assert_eq!(t.address(), "192.168.4.1:6789");
    }

    #[test]
    fn something_that_is_not_a_port_after_the_colon_stays_in_the_host() {
        let t = WifiEcuTransport::at("dongle.local:abc").unwrap();
        assert_eq!((t.host(), t.port()), ("dongle.local:abc", 35000));

        // Out of range, so also part of the host.
        let t = WifiEcuTransport::at("host:70000").unwrap();
        assert_eq!((t.host(), t.port()), ("host:70000", 35000));

        // A leading colon is no host at all.
        let t = WifiEcuTransport::at(":80").unwrap();
        assert_eq!((t.host(), t.port()), (":80", 35000));
    }

    #[test]
    fn a_blank_address_or_nonsense_port_is_refused() {
        assert!(matches!(WifiEcuTransport::at("   "), Err(Error::Argument(_))));
        assert!(matches!(WifiEcuTransport::new("", 1), Err(Error::Argument(_))));
        assert!(matches!(WifiEcuTransport::new("h", 0), Err(Error::Argument(_))));
        assert!(matches!(WifiEcuTransport::new("h", 65536), Err(Error::Argument(_))));
        assert!(WifiEcuTransport::new("h", 65535).is_ok());
    }

    #[test]
    fn reading_or_writing_before_opening_says_so() {
        let mut t = WifiEcuTransport::new("127.0.0.1", 1).unwrap();
        assert!(!t.is_open());
        assert!(matches!(t.write(b"x"), Err(Error::InvalidOperation(_))));
        assert!(matches!(
            t.read(&mut [0u8; 1], Duration::ZERO),
            Err(Error::InvalidOperation(_))
        ));
        t.discard_input();
        t.close();
    }

    #[test]
    fn a_request_goes_out_whole_and_the_reply_comes_back() {
        let (l, port) = listener();
        let server = thread::spawn(move || {
            let (mut s, _) = l.accept().unwrap();
            let mut got = [0u8; 4];
            s.read_exact(&mut got).unwrap();
            s.write_all(b"OK>").unwrap();
            got
        });

        let mut t = connected(port);
        assert!(t.is_open());
        t.write(b"ATZ\r").unwrap();

        let mut buf = [0u8; 3];
        assert_eq!(t.read(&mut buf, Duration::from_secs(2)).unwrap(), 3);
        assert_eq!(&buf, b"OK>");
        assert_eq!(&server.join().unwrap(), b"ATZ\r");
    }

    #[test]
    fn a_quiet_adapter_returns_a_short_read_when_the_time_is_up() {
        let (l, port) = listener();
        let server = thread::spawn(move || {
            let (mut s, _) = l.accept().unwrap();
            s.write_all(b"AB").unwrap();
            thread::sleep(Duration::from_millis(400));
        });

        let mut t = connected(port);
        let mut buf = [0u8; 5];
        let started = Instant::now();
        assert_eq!(t.read(&mut buf, Duration::from_millis(150)).unwrap(), 2);
        assert!(started.elapsed() >= Duration::from_millis(100));
        assert!(t.is_open());
        server.join().unwrap();
    }

    #[test]
    fn a_closed_adapter_ends_the_transport_instead_of_spinning() {
        let (l, port) = listener();
        let server = thread::spawn(move || {
            let (mut s, _) = l.accept().unwrap();
            s.write_all(b"X").unwrap();
        });

        let mut t = connected(port);
        server.join().unwrap();

        let mut buf = [0u8; 8];
        let started = Instant::now();
        // The peer is gone: one byte, then the end, long before the 5 s allowance.
        assert_eq!(t.read(&mut buf, Duration::from_secs(5)).unwrap(), 1);
        assert!(started.elapsed() < Duration::from_secs(2));
        assert!(!t.is_open());

        // And it stays ended: later reads return at once.
        let again = Instant::now();
        assert_eq!(t.read(&mut buf, Duration::from_secs(5)).unwrap(), 0);
        assert!(again.elapsed() < Duration::from_millis(500));
    }

    #[test]
    fn discarding_drops_a_late_reply() {
        let (l, port) = listener();
        let server = thread::spawn(move || {
            let (mut s, _) = l.accept().unwrap();
            s.write_all(b"STALE").unwrap();
            thread::sleep(Duration::from_millis(300));
            s.write_all(b"FRESH").unwrap();
            thread::sleep(Duration::from_millis(300));
        });

        let mut t = connected(port);
        thread::sleep(Duration::from_millis(100));
        t.discard_input();

        let mut buf = [0u8; 5];
        assert_eq!(t.read(&mut buf, Duration::from_secs(2)).unwrap(), 5);
        assert_eq!(&buf, b"FRESH");
        server.join().unwrap();
    }

    #[test]
    fn nothing_listening_is_explained_in_terms_of_the_three_likely_causes() {
        // A port that was just free: refused at once on loopback.
        let (l, port) = listener();
        drop(l);

        let mut t = WifiEcuTransport::new("127.0.0.1", i32::from(port)).unwrap();
        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        let text = e.to_string();
        assert!(text.starts_with(&format!("Nothing answered at 127.0.0.1:{port}. ")));
        assert!(text.contains("join its Wi-Fi first"));
        assert!(text.contains("close any phone app"));
        assert!(text.contains("check the ignition is on"));
        assert!(!t.is_open());
    }

    #[test]
    fn the_timeout_message_says_how_long_it_waited() {
        let t = WifiEcuTransport::new("192.0.2.1", 35000)
            .unwrap()
            .with_connect_within(Duration::from_millis(2500));
        let text = t.explain(&io::ErrorKind::TimedOut.into(), true);
        assert!(text.contains("still being attempted after 2.5 seconds"));

        let five = WifiEcuTransport::new("192.0.2.1", 35000).unwrap();
        assert!(
            five.explain(&io::ErrorKind::TimedOut.into(), true)
                .contains("after 5 seconds")
        );
    }

    #[test]
    fn closing_twice_and_reopening_works() {
        let (l, port) = listener();
        let server = thread::spawn(move || {
            for _ in 0..2 {
                let _ = l.accept().unwrap();
            }
        });

        let mut t = connected(port);
        t.close();
        t.close();
        assert!(!t.is_open());
        t.open().unwrap();
        assert!(t.is_open());
        server.join().unwrap();
    }
}
