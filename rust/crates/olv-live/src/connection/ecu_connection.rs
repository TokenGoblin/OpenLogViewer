//! Ported from `EcuConnection.cs`. Owned by package `w3-connection`.
//!
//! Talks to an ECU: asks what it is, then reads its realtime block over and over.
//!
//! Retries are built in rather than bolted on, because the link this is most likely
//! to be used over is Bluetooth, where a dropped or truncated reply is ordinary
//! rather than exceptional. A reply that fails its checksum is thrown away and asked
//! for again; only a run of consecutive failures ends the session.
//!
//! This is the one type here that can change an ECU. Reads are the bulk of it, but
//! [`EcuConnection::write_tune_page`] and [`EcuConnection::burn_page`] live here too,
//! and they are deliberately two calls rather than one: a write is undone by turning
//! the key off, a burn is not. A write is read back and compared before it is
//! reported as having worked, because an acknowledgement only says the command was
//! understood.
//!
//! **There is no confirmation gate in this file, and none was ported out of it**: the
//! C# has none here either. Whether a write may happen at all (a MicroSquirt in a live
//! car is read-only unless asked) is decided by the application above, before it calls
//! these methods. What this file does guard is the *shape* of a write: it must lie
//! inside the page, the firmware must declare a write command, the bytes must read back
//! identically, and a burn is sent exactly once.
//!
//! **Opening a port resets a Speeduino** (DTR is asserted and the Arduino reboots), so
//! every [`EcuConnection::reopen`] discards whatever was written and not yet burned.
//! [`EcuConnection::read_identity`] reopens on purpose, and so does the recovery path
//! of a live source: callers must not reopen between a write and its burn.

use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use olv_core::Error;
use olv_core::ini::msq_ini::RealtimeLayout;
use olv_core::ini::tune_layout::TunePage;
use olv_core::model::{RealtimeCommand, WireAttempt, WireFailureKind, WireOutcome, WireTrace};
use olv_protocol::ms::ms_protocol::{self, EcuProtocolError, EcuTransport};

/// What an [`EcuConnection`] call can fail with.
///
/// The C# throws `EcuProtocolException` for everything the ECU or the framing did
/// wrong and lets other exceptions (`ArgumentException`, `InvalidOperationException`)
/// pass through; callers tell a refusal from a silence with `Refused`. A bare
/// [`olv_core::Error::Protocol`] would lose that, so the protocol error travels whole.
#[derive(Debug)]
pub enum EcuError {
    /// An `EcuProtocolException`: carries `refused` and the wire failure kind.
    Protocol(EcuProtocolError),
    /// Anything else the C# lets through unwrapped (`ArgumentException` and its
    /// kin, `InvalidOperationException`, an `IOException` outside a request).
    Other(Error),
}

/// `Result` for the connection and the sources built on it.
pub type EcuResult<T> = std::result::Result<T, EcuError>;

impl EcuError {
    /// A protocol error with the C#'s constructor defaults (not a refusal, no kind).
    pub fn protocol(message: impl Into<String>) -> Self {
        EcuError::Protocol(EcuProtocolError::new(message))
    }

    /// True only for an `EcuProtocolException` whose `Refused` is set: the ECU
    /// understood the request and declined it.
    pub fn refused(&self) -> bool {
        matches!(self, EcuError::Protocol(e) if e.refused)
    }

    /// Whether this is an `EcuProtocolException`, as opposed to anything else.
    pub fn is_protocol(&self) -> bool {
        matches!(self, EcuError::Protocol(_))
    }

    /// The text the C#'s `Exception.Message` would carry.
    pub fn message(&self) -> String {
        self.to_string()
    }
}

impl std::fmt::Display for EcuError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            EcuError::Protocol(e) => e.fmt(f),
            EcuError::Other(e) => e.fmt(f),
        }
    }
}

impl std::error::Error for EcuError {}

impl From<EcuProtocolError> for EcuError {
    fn from(e: EcuProtocolError) -> Self {
        EcuError::Protocol(e)
    }
}

impl From<Error> for EcuError {
    fn from(e: Error) -> Self {
        EcuError::Other(e)
    }
}

impl From<EcuError> for Error {
    /// For the `LiveSource` trait, which speaks [`olv_core::Error`]. A protocol error
    /// becomes [`Error::Protocol`] with the same message.
    fn from(e: EcuError) -> Self {
        match e {
            EcuError::Protocol(p) => Error::Protocol(p.message),
            EcuError::Other(o) => o,
        }
    }
}

/// `ArgumentOutOfRangeException.ThrowIfLessThan(value, 1)`, in the message style the
/// ported `olv_core` code already uses.
fn at_least_one(name: &str, value: i32) -> EcuResult<()> {
    if value < 1 {
        return Err(EcuError::Other(Error::Argument(format!(
            "{name} ('{value}') must be greater than or equal to '1'."
        ))));
    }
    Ok(())
}

/// How patient a connection is, and how it retries (`EcuConnectionSettings`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EcuConnectionSettings {
    /// How long to wait for a reply before giving up on it.
    pub timeout: Duration,

    /// The allowance a burn gets instead, in both directions.
    ///
    /// A burn is sent once rather than retried, so unlike every other request it has
    /// no second attempt to fall back on, and it is the request most likely to need
    /// the time. A controller writing its flash stops answering, and stops accepting
    /// bytes, for as long as the erase takes: a rusEFI acknowledges first and erases
    /// afterwards, but one that erases first would miss a 500 ms window every time and
    /// have a burn that worked reported as unconfirmed.
    pub burn_timeout: Duration,

    /// Extra attempts before a request is considered failed.
    pub retries: i32,

    /// Settling time after a bad reply, so its tail does not corrupt the next.
    /// (Declared by the C# and never read by it; kept so settings round-trip.)
    pub retry_pause: Duration,

    /// How long to keep draining a link before giving up on it going quiet.
    pub settle_for: Duration,

    /// Silence of this length counts as quiet.
    pub quiet_for: Duration,

    /// How long to keep asking an ECU what it is before giving up.
    ///
    /// Long enough for a board that reboots when the port opens: an Arduino Mega
    /// spends about two seconds in its bootloader, and a Speeduino asked during that
    /// window looks like an ECU that will not identify itself.
    pub identify_within: Duration,

    /// Silence to leave a freshly opened port before speaking to it again.
    ///
    /// Long enough for an Arduino bootloader to give up waiting for an upload and
    /// start the firmware, which is a shade under two seconds on a Mega.
    pub boot_wait: Duration,

    /// CAN id of the controller; 0 is the ECU itself.
    pub can_id: u8,
}

impl Default for EcuConnectionSettings {
    fn default() -> Self {
        Self {
            timeout: Duration::from_millis(500),
            burn_timeout: Duration::from_secs(8),
            retries: 3,
            retry_pause: Duration::from_millis(60),
            settle_for: Duration::from_millis(400),
            quiet_for: Duration::from_millis(40),
            identify_within: Duration::from_secs(12),
            boot_wait: Duration::from_millis(2500),
            can_id: 0,
        }
    }
}

impl EcuConnectionSettings {
    /// Settings for a link that is slower and less orderly than a cable.
    ///
    /// A Bluetooth SPP port is a virtual COM port, so nothing else about the
    /// connection changes, but a reply that would arrive in three milliseconds over
    /// USB can take hundreds, and one that is merely late is worse than one that never
    /// comes.
    pub fn bluetooth() -> Self {
        Self {
            timeout: Duration::from_millis(1000),
            settle_for: Duration::from_millis(1200),
            quiet_for: Duration::from_millis(120),
            retries: 2,
            ..Self::default()
        }
    }
}

/// Everything a conversation touches, behind the gate.
struct Core {
    transport: Box<dyn EcuTransport>,
    settings: EcuConnectionSettings,
    retries: Arc<AtomicI32>,
    trace: Option<Arc<WireTrace>>,

    buffer: Vec<u8>,
    command: RealtimeCommand,
    little_endian: bool,
    chunk: i32,
}

/// A connection to an ECU over a transport (`EcuConnection`).
///
/// Shareable between threads (`Send + Sync`): every method takes the one gate for
/// its whole length, which is the C#'s `lock (_gate)`.
///
/// One conversation at a time. A live session polls this from its own thread while
/// the window is free to ask for something else, reading the tune or writing a
/// table back. Both are a request followed by its reply on a single serial line,
/// and two of them interleaved is not two conversations but one ruined one: the
/// poll reads the write's acknowledgement as its block of channels, and the write
/// reads a block of channels as its acknowledgement. The write path reads its own
/// bytes back through the same code, which is why the inner methods live on
/// `Core` separately from the locked public ones (the C#'s lock is reentrant).
pub struct EcuConnection {
    core: Mutex<Core>,
    settings: EcuConnectionSettings,
    retries: Arc<AtomicI32>,
    trace: Option<Arc<WireTrace>>,
}

impl EcuConnection {
    pub fn new(transport: Box<dyn EcuTransport>, settings: EcuConnectionSettings) -> Self {
        let retries = Arc::new(AtomicI32::new(0));

        Self {
            core: Mutex::new(Core {
                transport,
                settings: settings.clone(),
                retries: Arc::clone(&retries),
                trace: None,
                buffer: vec![0; 4096],
                command: RealtimeCommand::default(),
                little_endian: false,
                chunk: 0,
            }),
            settings,
            retries,
            trace: None,
        }
    }

    /// The C#'s `new EcuConnection(transport)`: default settings, no trace.
    pub fn with_defaults(transport: Box<dyn EcuTransport>) -> Self {
        Self::new(transport, EcuConnectionSettings::default())
    }

    /// Records every request this connection makes into `trace` (the C#'s `Trace`
    /// initialiser). Left unset costs nothing beyond a null check.
    #[must_use]
    pub fn with_trace(mut self, trace: Arc<WireTrace>) -> Self {
        self.core.get_mut().unwrap_or_else(PoisonError::into_inner).trace = Some(Arc::clone(&trace));
        self.trace = Some(trace);
        self
    }

    pub fn settings(&self) -> &EcuConnectionSettings {
        &self.settings
    }

    /// Replies thrown away for a bad checksum or a short read, over the session.
    ///
    /// Read without taking the gate, as the C#'s auto-property is: a status bar must
    /// not wait for a conversation to finish.
    pub fn retries(&self) -> i32 {
        self.retries.load(Ordering::Relaxed)
    }

    /// Where every request this connection makes is recorded, or `None`.
    pub fn trace(&self) -> Option<&Arc<WireTrace>> {
        self.trace.as_ref()
    }

    fn gate(&self) -> MutexGuard<'_, Core> {
        // A panic in another conversation leaves nothing this could half-trust, but
        // refusing every later request for it would end a session over a bug; carry on.
        self.core.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// `Dispose`: closes the link. Idempotent, and does not need the last owner to go.
    pub fn dispose(&self) {
        self.gate().transport.close();
    }

    pub fn open(&self) -> EcuResult<()> {
        let mut core = self.gate();
        if !core.transport.is_open() {
            core.transport.open()?;
        }
        Ok(())
    }

    /// Closes and reopens the link.
    ///
    /// Needed after the device has gone: the handle is dead but still reports itself
    /// open, so reopening without closing first does nothing and every later read
    /// fails the same way.
    ///
    /// On a Speeduino this resets the board, and whatever was written but not burned
    /// is lost with it.
    pub fn reopen(&self) -> EcuResult<()> {
        self.gate().reopen()
    }

    /// What the ECU says it is: the string an INI must match before anything is
    /// decoded. Firmware versions move channel offsets, so decoding a block with the
    /// wrong INI does not fail, it silently reads every channel from the wrong place.
    pub fn read_signature(&self) -> EcuResult<String> {
        let mut core = self.gate();
        let data = core.request(&[ms_protocol::QUERY_SIGNATURE], &Ask::new("read signature"))?;
        Ok(ms_protocol::read_signature(&data))
    }

    /// The longer build string, for display.
    pub fn read_version(&self) -> EcuResult<String> {
        let mut core = self.gate();
        let data = core.request(&[ms_protocol::QUERY_VERSION], &Ask::new("read version"))?;
        Ok(ms_protocol::read_signature(&data))
    }

    /// Everything the ECU will say about itself, best candidate first, asking for
    /// [`EcuConnectionSettings::identify_within`].
    ///
    /// Which reply is the signature and which is a build string depends on the
    /// firmware, and the caller cannot know the firmware until it has the signature.
    /// So all of them are collected and the INI catalogue decides: the signature is,
    /// operationally, whichever of these matches a definition file. Commands the ECU
    /// refuses are simply absent.
    pub fn read_identity(&self) -> EcuResult<Vec<String>> {
        self.read_identity_within(self.settings.identify_within)
    }

    /// `within` is how long to keep asking before concluding that nothing is there.
    ///
    /// Asking once is not enough, because opening a serial port asserts DTR and that
    /// resets some boards: a Speeduino on an Arduino Mega reboots into its bootloader
    /// and answers nothing for about two seconds. Probed immediately, it looks like an
    /// ECU that will not identify itself; given a moment, it says "speeduino 202402"
    /// straight away. Costs nothing on a board that was already awake, since the first
    /// question is answered.
    pub fn read_identity_within(&self, within: Duration) -> EcuResult<Vec<String>> {
        let mut core = self.gate();
        let deadline = Instant::now() + within;

        let mut said = core.ask_what_it_is()?;
        if !said.is_empty() {
            return Ok(said);
        }

        // Nothing. Which may mean nothing is there, or may mean the board reset when
        // the port opened and was still in its bootloader when it was asked.
        //
        // A Speeduino on an Arduino Mega does exactly that, and the failure is not a
        // matter of asking again: an Arduino bootloader listens for an upload, and
        // bytes arriving while it does leave it stuck there for good. Measured: probed
        // at 0.15 s, the board was still silent at 8 seconds, while a port opened and
        // left alone for three answered immediately.
        //
        // So the recovery is a fresh port and then silence, not persistence.
        while Instant::now() < deadline {
            if core.reopen().is_err() {
                return Ok(said);
            }

            std::thread::sleep(core.settings.boot_wait);

            said = core.ask_what_it_is()?;
            if !said.is_empty() {
                return Ok(said);
            }
        }

        Ok(said)
    }

    /// Adopts a firmware's own way of being asked for its realtime block.
    ///
    /// Called once the INI is known, which is after the signature has been read; until
    /// then the MegaSquirt page read is assumed, since that is what the identity
    /// commands need and they are the same on both families.
    ///
    /// `single_request` asks for the whole realtime block in one reply, ignoring the
    /// blocking factor. Off by default because whether a firmware tolerates it is not
    /// something the INI says, and finding out the wrong way is expensive. Measured on
    /// two: an MS3 declaring 256 serves its whole 512-byte block quite happily, while a
    /// rusEFI declaring 1024 answers 1024 and, asked for 1280, stops replying and
    /// leaves the USB bus until it is replugged. So it is worth a third of the poll
    /// rate on one and unusable on the other, which makes it a choice rather than a
    /// default.
    pub fn use_layout(&self, layout: &RealtimeLayout, single_request: bool) {
        let mut core = self.gate();

        core.command = layout.command();
        core.little_endian = layout.little_endian;
        core.chunk = if single_request { 0 } else { layout.blocking_factor };

        // Sized for the largest reply this firmware will send, which is one chunk when
        // it chunks and the whole block when it does not.
        let largest = if core.chunk > 0 && core.command.takes_range() {
            core.chunk.min(layout.block_size)
        } else {
            layout.block_size
        };

        core.grow_for(largest);
    }

    /// Asks for a realtime block in one reply regardless of the blocking factor.
    ///
    /// For finding out what a firmware will actually serve. Not the path a session
    /// uses: a rusEFI asked for more than it can send has been seen to leave the USB
    /// bus rather than refuse.
    pub fn read_realtime_unchunked(&self, size: i32) -> EcuResult<Vec<u8>> {
        let mut core = self.gate();
        at_least_one("size", size)?;

        core.grow_for(size);

        let can = core.settings.can_id;
        let request = core.command.build(0, size, can, core.little_endian, &[], &[])?;
        core.request(&request, &Ask::new("read realtime (unchunked)"))
    }

    /// Fetches one realtime block, in as many pieces as the firmware insists on.
    ///
    /// The blocking factor is a hard limit rather than a hint. A rusEFI asked for more
    /// than it declares does not answer with an error: it leaves the USB bus, and does
    /// not come back until the board is replugged. That rules out the obvious
    /// alternative of asking for the whole block and falling back on a refusal: the
    /// first attempt would end the session it was meant to start.
    ///
    /// So it is obeyed even where a firmware would have tolerated more. An MS3 declares
    /// 256 and serves 512 quite happily, but the cost of not finding out the hard way
    /// is one extra round trip on a link whose time goes on the bytes rather than the
    /// turnaround. The one real loss is that a split sample is no longer taken at a
    /// single instant.
    pub fn read_realtime(&self, size: i32) -> EcuResult<Vec<u8>> {
        let mut core = self.gate();
        at_least_one("size", size)?;

        let can = core.settings.can_id;
        let little = core.little_endian;

        if !core.command.takes_range() || core.chunk <= 0 || size <= core.chunk {
            let request = core.command.build(0, size, can, little, &[], &[])?;
            return core.request(&request, &Ask::new("read realtime").expecting(size));
        }

        let mut block = vec![0u8; size as usize];
        let mut at = 0;

        while at < size {
            let wanted = core.chunk.min(size - at);

            let request = core.command.build(at, wanted, can, little, &[], &[])?;
            let piece = core.request(&request, &Ask::new(format!("read realtime @{at}")).expecting(wanted))?;

            block[at as usize..(at + wanted) as usize].copy_from_slice(&piece[..wanted as usize]);
            at += wanted;
        }

        Ok(block)
    }

    /// Reads one page of the ECU's settings.
    ///
    /// Chunked at the blocking factor like a realtime read, and for the harder reason:
    /// this is 22,960 bytes on a rusEFI where one reply holds 1,024.
    pub fn read_tune_page(
        &self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        progress: Option<&mut dyn FnMut(i32)>,
    ) -> EcuResult<Vec<u8>> {
        let mut core = self.gate();
        at_least_one("page.Size", page.size)?;

        if !RealtimeCommand::parse(&page.read_command).takes_range() {
            return Err(EcuError::protocol(format!(
                "Page {} declares \"{}\", which cannot ask for part of a page.",
                page.index, page.read_command
            )));
        }

        core.read_tune_page_range(page, blocking_factor, little_endian, 0, page.size, progress)
    }

    /// Writes bytes into a page and reads them back to prove it took.
    ///
    /// The read-back is not optional. A write is answered with an acknowledgement that
    /// says the command was understood, not that the right bytes landed at the right
    /// offset, and every way of getting that wrong produces an engine running on
    /// numbers nobody chose. Reading the same range back and comparing is the only
    /// thing that distinguishes a write that worked from one that went somewhere else.
    ///
    /// This does not burn. The bytes are in the controller's working memory and are
    /// gone at the next power cycle until [`burn_page`](Self::burn_page) is called,
    /// which is a separate decision on the ECU as much as here.
    pub fn write_tune_page(
        &self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        offset: i32,
        data: &[u8],
        inter_write_delay: i32,
    ) -> EcuResult<()> {
        let mut core = self.gate();

        if offset < 0 {
            return Err(EcuError::Other(Error::Argument(format!(
                "offset ('{offset}') must be a non-negative value."
            ))));
        }

        if data.is_empty() {
            return Ok(());
        }

        // Widened so the sum cannot wrap past the guard it is the point of.
        let ends = i64::from(offset) + data.len() as i64;
        if ends > i64::from(page.size) {
            return Err(EcuError::protocol(format!(
                "That write ends at {} of a {} byte page.",
                ends, page.size
            )));
        }

        if page.chunk_write_command.is_empty() {
            return Err(EcuError::protocol(format!(
                "Page {} declares no write command, so this firmware cannot be written to.",
                page.index
            )));
        }

        let can = core.settings.can_id;
        let command = RealtimeCommand::parse(&page.chunk_write_command);
        let identifier = RealtimeCommand::parse(&page.identifier).build(0, 1, can, little_endian, &[], &[])?;

        // The blocking factor bounds a write as it does a read; the payload has to fit
        // in one message along with its header.
        let length = data.len() as i32;
        let mut chunk = if blocking_factor > 0 {
            (blocking_factor - 8).min(length)
        } else {
            length
        };
        if chunk < 1 {
            chunk = length;
        }

        let mut at = 0;
        while at < length {
            let take = chunk.min(length - at);

            let request = command.build(
                offset + at,
                take,
                can,
                little_endian,
                &identifier,
                &data[at as usize..(at + take) as usize],
            )?;
            core.request(
                &request,
                &Ask::new(format!("write page {} @{}", page.index, offset + at)),
            )?;

            at += take;

            // The pause the firmware asks for between writes, which TunerStudio also
            // observes. The controller is copying the last message into its own memory,
            // and the next one arriving underneath that is how a write ends up half
            // applied.
            if inter_write_delay > 0 && at < length {
                std::thread::sleep(Duration::from_millis(inter_write_delay as u64));
            }
        }

        let read_back = core.read_tune_page_range(page, blocking_factor, little_endian, offset, length, None)?;

        if read_back != data {
            return Err(EcuError::protocol(format!(
                "The ECU did not take that write: {} bytes were sent to offset {} of page {} and reading the same \
                 range back gave something else. Nothing has been burned, so a power cycle restores the ECU.",
                data.len(),
                offset,
                page.index
            )));
        }

        Ok(())
    }

    /// Commits a page to flash, making it survive a power cycle.
    ///
    /// Deliberately its own call. Everything up to here can be undone by turning the
    /// key off.
    ///
    /// A status of 0x04 from the ECU is a burn that worked (rusEFI answers it so);
    /// [`ms_protocol::unframe`] treats only the high bit as a failure.
    pub fn burn_page(&self, page: &TunePage, little_endian: bool, after_burn_delay: i32) -> EcuResult<()> {
        let mut core = self.gate();

        if page.burn_command.is_empty() {
            return Err(EcuError::protocol(format!(
                "Page {} declares no burn command.",
                page.index
            )));
        }

        let can = core.settings.can_id;
        let identifier = RealtimeCommand::parse(&page.identifier).build(0, 1, can, little_endian, &[], &[])?;

        // Once, deliberately. Every other request may be repeated freely because a lost
        // reply means a lost reply; a burn is the one where the ECU may well have done
        // the work and gone quiet doing it, and re-sending then spends a flash erase to
        // learn nothing. A burn that cannot be confirmed is reported as exactly that
        // rather than as a failure, because saying it failed when it did not is what
        // sends somebody to write it again.
        //
        // Widened for the length of the burn and put straight back. The port is shared
        // by everything else on this link, and everything else should fail fast: a long
        // write timeout left in place turns a port that never accepts a write into a
        // window that hangs for the whole identify sequence rather than a moment.
        let was_allowed = core.transport.write_timeout();
        let burn_timeout = core.settings.burn_timeout;

        core.transport.set_write_timeout(burn_timeout);

        let outcome = core.burn_request(page, can, little_endian, &identifier, burn_timeout);

        core.transport.set_write_timeout(was_allowed);

        match outcome {
            // Only where the ECU never answered. A controller that answered with a
            // refusal has told us it did not burn, and saying it might have would send
            // somebody to power-cycle a car over a burn that demonstrably did not
            // happen, so that one is left to speak for itself, Refused and all.
            Err(EcuError::Protocol(e)) if !e.refused => {
                return Err(EcuError::protocol(format!(
                    "The ECU did not confirm the burn of page {}: {}\n\n\
                     It may still have completed \u{2014} a controller stops answering while it writes its flash, \
                     which looks the same from here as one that never got the command. \
                     Turn the ignition off and on and read the tune back: if the change is there, it was burned.",
                    page.index, e.message
                )));
            }
            other => other?,
        }

        // The wait the firmware asks for after a burn, which TunerStudio also observes.
        // Writing flash stops the controller answering for as long as it takes, so the
        // next request sent immediately afterwards goes into silence, and
        // desynchronises everything after it, which on a link being polled at the same
        // time is every reading until it is noticed.
        if after_burn_delay > 0 {
            std::thread::sleep(Duration::from_millis(after_burn_delay as u64));
        }

        Ok(())
    }

    /// Reads part of a page, for verifying a write.
    pub fn read_tune_page_range(
        &self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        offset: i32,
        count: i32,
        progress: Option<&mut dyn FnMut(i32)>,
    ) -> EcuResult<Vec<u8>> {
        self.gate()
            .read_tune_page_range(page, blocking_factor, little_endian, offset, count, progress)
    }
}

/// How one request is to be made: the C#'s optional parameters of `Request`.
struct Ask {
    retry_refusals: bool,
    expected: i32,
    attempts: i32,
    within: Option<Duration>,
    context: String,
}

impl Ask {
    fn new(context: impl Into<String>) -> Self {
        Self {
            retry_refusals: true,
            expected: 0,
            attempts: 0,
            within: None,
            context: context.into(),
        }
    }

    fn expecting(mut self, bytes: i32) -> Self {
        self.expected = bytes;
        self
    }

    fn attempts(mut self, attempts: i32) -> Self {
        self.attempts = attempts;
        self
    }

    fn within(mut self, within: Duration) -> Self {
        self.within = Some(within);
        self
    }

    fn without_refusal_retries(mut self) -> Self {
        self.retry_refusals = false;
        self
    }
}

impl Core {
    fn reopen(&mut self) -> EcuResult<()> {
        // Closing something already gone is not a reason not to try again.
        self.transport.close();
        self.transport.open()?;
        Ok(())
    }

    /// Makes the receive buffer big enough for one framed reply of `largest` bytes.
    fn grow_for(&mut self, largest: i32) {
        let wanted = 2 + 1 + largest.max(0) as usize + 4;
        if self.buffer.len() < wanted {
            self.buffer = vec![0; wanted];
        }
    }

    /// The burn request itself, once.
    fn burn_request(
        &mut self,
        page: &TunePage,
        can: u8,
        little_endian: bool,
        identifier: &[u8],
        burn_timeout: Duration,
    ) -> EcuResult<()> {
        let request = RealtimeCommand::parse(&page.burn_command).build(0, 1, can, little_endian, identifier, &[])?;

        self.request(
            &request,
            &Ask::new(format!("burn page {}", page.index))
                .attempts(1)
                .within(burn_timeout),
        )?;

        Ok(())
    }

    /// Asks each identity command once and collects what comes back.
    fn ask_what_it_is(&mut self) -> EcuResult<Vec<String>> {
        let mut said: Vec<String> = Vec::with_capacity(3);

        for &command in ms_protocol::IDENTITY_COMMANDS {
            // One attempt each, and no repeating a refusal: probing a rusEFI with
            // MegaSquirt's query command is refused by design, and an ECU that is not
            // answering yet will not answer harder.
            let text = match self.request(
                &[command],
                &Ask::new(format!("identify ('{}')", char::from(command)))
                    .without_refusal_retries()
                    .attempts(1),
            ) {
                Ok(data) => ms_protocol::read_signature(&data),
                Err(EcuError::Protocol(_)) => continue,
                Err(other) => return Err(other),
            };

            // Seven characters is what an INI expects of a signature; shorter replies
            // are status text rather than an identity.
            if text.encode_utf16().count() >= 7 && !said.contains(&text) {
                said.push(text);
            }
        }

        Ok(said)
    }

    fn read_tune_page_range(
        &mut self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        offset: i32,
        count: i32,
        mut progress: Option<&mut dyn FnMut(i32)>,
    ) -> EcuResult<Vec<u8>> {
        at_least_one("count", count)?;

        let can = self.settings.can_id;
        let command = RealtimeCommand::parse(&page.read_command);
        let identifier = RealtimeCommand::parse(&page.identifier).build(0, 1, can, little_endian, &[], &[])?;

        let chunk = if blocking_factor > 0 {
            blocking_factor.min(count)
        } else {
            count
        };
        let mut image = vec![0u8; count as usize];

        self.grow_for(chunk);

        let mut at = 0;
        while at < count {
            let take = chunk.min(count - at);
            let request = command.build(offset + at, take, can, little_endian, &identifier, &[])?;
            let piece = self.request(
                &request,
                &Ask::new(format!("read page {} @{}", page.index, offset + at)).expecting(take),
            )?;

            image[at as usize..(at + take) as usize].copy_from_slice(&piece[..take as usize]);
            at += take;

            if let Some(progress) = progress.as_mut() {
                progress(at);
            }
        }

        Ok(image)
    }

    /// Sends one request and returns its data, retrying a reply that goes astray.
    ///
    /// `retry_refusals` is true everywhere except while probing. A refusal the ECU
    /// meant will be meant again, so retrying one only spends the timeout, but a
    /// refusal is also what a desynchronised stream looks like, since the first stale
    /// byte lands where the status belongs. After a link has dropped, the retries are
    /// the thing that gets back into step, so only a caller expecting to be refused may
    /// skip them.
    ///
    /// `expected` is how many bytes the reply should carry, or zero when anything is
    /// acceptable. Checked because a reply says nothing about which request it answers
    /// (there is no echo of the offset or the count), so a late one from a previous
    /// attempt is otherwise decoded as the answer to this one, at offsets it was never
    /// read from. That produces well-formed nonsense: a stationary car reading 230 km/h.
    ///
    /// `within` overrides how long the reply is waited for, for the one request that is
    /// allowed to take its time.
    fn request(&mut self, payload: &[u8], ask: &Ask) -> EcuResult<Vec<u8>> {
        let framed = ms_protocol::frame(payload);
        let mut last: Option<EcuProtocolError> = None;

        let tries = if ask.attempts > 0 {
            ask.attempts
        } else {
            self.settings.retries + 1
        };

        for attempt in 0..tries {
            if attempt > 0 {
                self.retries.fetch_add(1, Ordering::Relaxed);
            }

            let started = Instant::now();

            match self.exchange(&framed, ask) {
                Ok(data) => {
                    if let Some(trace) = &self.trace {
                        trace.record(WireAttempt {
                            context: ask.context.clone(),
                            attempt,
                            elapsed: started.elapsed(),
                            outcome: WireOutcome::Ok,
                            request_bytes: framed.len() as i32,
                            reply_bytes: data.len() as i32,
                            ..WireAttempt::default()
                        });
                    }

                    return Ok(data);
                }
                // An IOException among them: the transport raises one where the port
                // itself misbehaves rather than the ECU, and catching only the protocol
                // exception let that escape the retry loop unwrapped. It reached the
                // application as an unhandled "The semaphore timeout period has
                // expired", which is both a crash and a lie: the request it was raised
                // for had gone through.
                Err(failure) => {
                    let failed = match failure {
                        EcuError::Protocol(e) => e,
                        EcuError::Other(Error::Io(e)) => EcuProtocolError {
                            message: format!("The link to the ECU failed: {e}"),
                            refused: false,
                            failure_kind: WireFailureKind::TransportError,
                        },
                        other => return Err(other),
                    };

                    if let Some(trace) = &self.trace {
                        trace.record(WireAttempt {
                            context: ask.context.clone(),
                            attempt,
                            elapsed: started.elapsed(),
                            outcome: WireOutcome::Failure,
                            failure_kind: failed.failure_kind,
                            detail: failed.message.clone(),
                            request_bytes: framed.len() as i32,
                            ..WireAttempt::default()
                        });
                    }

                    let refused = failed.refused;
                    last = Some(failed);

                    // Wait for the link to fall silent before trying again. A fixed
                    // pause is not enough: over Bluetooth a reply can still be on its
                    // way when the next request goes out, and rusEFI's command handler
                    // has no queue; a request arriving mid-reply desynchronises it
                    // permanently, and only a power cycle brings the ECU back. Never
                    // more than one request outstanding.
                    self.settle();

                    if refused && !ask.retry_refusals {
                        break;
                    }
                }
            }
        }

        Err(EcuError::Protocol(last.unwrap_or_else(|| EcuProtocolError {
            message: "The ECU did not reply.".to_string(),
            refused: false,
            failure_kind: WireFailureKind::Timeout,
        })))
    }

    /// One attempt: clear the input, send, read the frame, check it.
    fn exchange(&mut self, framed: &[u8], ask: &Ask) -> EcuResult<Vec<u8>> {
        self.transport.discard_input();
        self.transport.write(framed)?;

        let within = ask.within.unwrap_or(self.settings.timeout);
        let length = self.read_frame(within)?;
        let data = ms_protocol::unframe(&self.buffer[..length])?;

        if ask.expected > 0 && data.len() as i32 != ask.expected {
            return Err(EcuError::Protocol(EcuProtocolError {
                message: format!(
                    "The reply carried {} bytes where {} were asked for; it belongs to an earlier request.",
                    data.len(),
                    ask.expected
                ),
                refused: false,
                failure_kind: WireFailureKind::Malformed,
            }));
        }

        Ok(data)
    }

    /// Reads and throws away whatever is still arriving, until the link is quiet or
    /// the allowance runs out.
    fn settle(&mut self) {
        let mut discard = [0u8; 256];
        let deadline = Instant::now() + self.settings.settle_for;

        while Instant::now() < deadline {
            let quiet_for = self.settings.quiet_for;

            let got = match self.transport.read(&mut discard, quiet_for) {
                Ok(got) => got,
                // A port whose device has gone cannot be drained, and saying so is the
                // caller's job rather than this one's.
                Err(_) => return,
            };

            if got == 0 {
                return;
            }
        }
    }

    /// Reads one framed reply: the two-byte length, then exactly that many bytes plus
    /// the checksum. Returns how much of the buffer is the frame.
    ///
    /// Read in two steps because the length is not known until the first two bytes
    /// arrive. Asking for a fixed guess instead either truncates a long reply or waits
    /// out the timeout on every short one.
    fn read_frame(&mut self, within: Duration) -> EcuResult<usize> {
        let header = self.transport.read(&mut self.buffer[..2], within)?;
        if header < 2 {
            return Err(EcuError::Protocol(EcuProtocolError {
                message: "The ECU did not reply.".to_string(),
                refused: false,
                failure_kind: WireFailureKind::Timeout,
            }));
        }

        let length = (usize::from(self.buffer[0]) << 8) | usize::from(self.buffer[1]);
        if length < 1 || 2 + length + 4 > self.buffer.len() {
            return Err(EcuError::Protocol(EcuProtocolError {
                message: format!("The ECU declared a {length} byte reply, which is not usable."),
                refused: false,
                failure_kind: WireFailureKind::Malformed,
            }));
        }

        let wanted = length + 4;
        let body = self.transport.read(&mut self.buffer[2..2 + wanted], within)?;

        if body < wanted {
            return Err(EcuError::Protocol(EcuProtocolError {
                message: format!("The reply stopped after {body} of {wanted} bytes."),
                refused: false,
                failure_kind: WireFailureKind::ShortReply,
            }));
        }

        Ok(2 + wanted)
    }
}
