//! Ported from `MaxxUsbSource.cs`. Owned by package `w4-live`.
//!
//! A live source over a MaxxECU's USB cable.
//!
//! Its own source rather than a transport swapped underneath `MaxxEcuSource`, because USB is a
//! different protocol and not a different wire; see `olv_protocol::ms::maxx_usb_protocol`.
//! Everything about the conversation differs: there is no activation, no subscription, and the
//! ECU is asked for telemetry rather than pushing it.
//!
//! The shape of a round is two requests. The first asks how many bytes are waiting; the second
//! takes them. What comes back is a list of channel ids with their values, carrying only what
//! has changed, so a round is a handful of channels and not a block, and the reading this
//! hands back is the accumulated picture rather than what arrived in the last message.
//!
//! That is also why the channel list has to be learnt rather than declared, and why learning
//! it is not enough on its own. Opening listens for a couple of seconds and takes what
//! arrives, but what arrives is only what moved, so the list depends on what the engine was
//! doing rather than on what the ECU has. On a bench Race that is 136 channels on the first
//! connect of the day and fifty on the next, and listening longer does not help: the first
//! number is a queued backlog being drained and the plateau is reached in about a second.
//! [`always_logged`] is what stops that deciding which channels a log has, because a channel
//! that first appears later cannot be added: a log's columns cannot change once it has rows.
//!
//! **Threads.** In the C# one `MaxxUsbSource` object is shared between the poll thread and
//! whatever starts a tune read or write. A Rust `LiveSession` owns its source outright, so the
//! cable is split out as a [`MaxxUsbLink`]: a cloneable handle on the one conversation, which
//! the tune and CAN operations go through and which serialises them against the telemetry
//! rounds exactly as the C#'s `_cable` lock does.

use std::collections::{BTreeSet, HashMap};
use std::path::PathBuf;
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, LazyLock, Mutex, MutexGuard, PoisonError};
use std::time::{Duration, Instant};

use olv_core::logs::{maxx_channel_definitions, maxx_channel_table, maxx_gauges};
use olv_core::model::{CanFrame, LiveSource};
use olv_core::{Error, Result};
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use olv_protocol::ms::ms_protocol::EcuTransport;

use super::maxx_can;
use super::maxx_tune::{self, MaxxWriteStatus};

/// How long one request waits for its reply.
const REPLY_TIMEOUT: Duration = Duration::from_millis(400);

/// How long to keep listening at connect before fixing the channel list.
///
/// Two seconds because a longer wait buys nothing: measured against a bench Race, a raw listen
/// had found 51 ids after one second, 55 after five, and 55 after sixty. Everything a
/// channel-by-channel listen is going to hear, it hears at once.
const LEARN_FOR: Duration = Duration::from_secs(2);

/// Throttle position, which the Bluetooth subscription has no room for.
const THROTTLE_POSITION: i32 = 19;

/// Channels that become columns whether or not the ECU has mentioned them.
///
/// Learning the list by listening has one flaw, and it is not a small one: a MaxxECU sends a
/// channel only when its value changes, so what is heard in the first two seconds is whatever
/// happened to be moving. Connect with the engine off (which is the ordinary order, cable first
/// and key second) and engine speed, coolant, lambda and ignition angle have all been still, so
/// none of them is a column, and a log's columns cannot change once it has rows. Measured on a
/// bench Race: fifty channels of input voltages and counters, and not one of the things anybody
/// connects to see.
///
/// These are the fourteen MaxxECU itself subscribes to for its Bluetooth dash
/// ([`maxx_protocol::SUBSCRIBED`], so the two links agree on what matters) with throttle
/// position added, which that set leaves out. A MaxxECU has all of them whatever is wired to
/// it. Fifteen ids, ascending.
///
/// One that has not been sent yet reads zero until the ECU first reports it, which is what any
/// channel does between being discovered and its first update. Zero is wrong for a coolant
/// temperature and right for engine speed, and it lasts only until the value moves, where being
/// absent from the log lasts for the whole session.
pub fn always_logged() -> &'static [i32] {
    static ALWAYS_LOGGED: LazyLock<Vec<i32>> = LazyLock::new(|| {
        let mut ids: Vec<i32> = maxx_protocol::SUBSCRIBED.iter().map(|c| c.id).collect();
        ids.push(THROTTLE_POSITION);
        ids.sort_unstable();
        ids
    });

    &ALWAYS_LOGGED
}

/// A channel a USB session reads: the C#'s `MaxxChannel`, with owned strings because these are
/// named from MTune's definitions at run time (`olv_protocol`'s own `MaxxChannel` is
/// `'static`, for the fixed Bluetooth subscription).
#[derive(Clone, Debug, PartialEq)]
pub struct MaxxUsbChannel {
    pub id: i32,
    pub name: String,
    pub units: String,
    pub scale: f64,
    pub is_signed: bool,
    pub digits: i32,
}

/// The cable and the counters that belong to it.
struct Shared {
    /// Held by anything that puts bytes on the cable.
    ///
    /// A MaxxECU has one USB device and one conversation on it: a request goes out and the
    /// reply is read back, with nothing to say which request a reply belongs to. The poll loop
    /// runs that conversation continuously on its own thread, so a tune read or a write started
    /// from the interface would interleave with it, and what comes back is then a reply to
    /// somebody else's question, with a length and a checksum that happen to agree.
    cable: Mutex<Box<dyn EcuTransport>>,

    /// Requests that had to be repeated, over the session.
    retries: AtomicI32,

    /// Rounds where the ECU had nothing new to say, which is not a fault.
    empty_rounds: AtomicI32,
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, Box<dyn EcuTransport>> {
        self.cable.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn count_retry(&self) {
        self.retries.fetch_add(1, Ordering::Relaxed);
    }

    /// Sends a request and reads its reply, once more if the first goes astray.
    ///
    /// The link is a cable rather than a radio, so a lost reply is rare, but the two requests
    /// of a round are not independent, and a failed first one must not be followed by a read of
    /// a length nobody agreed. The retry loop itself is `maxx_usb_protocol::try_ask`, shared
    /// with `maxx_tune::read`; what stays here is this source's own choices: two attempts,
    /// [`REPLY_TIMEOUT`], the reused `buffer`, and counting a retry against `retries`.
    fn ask(
        &self,
        transport: &mut dyn EcuTransport,
        buffer: &mut [u8],
        request: &[u8],
        data_length: i32,
    ) -> Result<Option<Vec<u8>>> {
        let length = usb::reply_length(data_length) as usize;
        let mut retry = || self.count_retry();

        let answer = usb::try_ask(
            transport,
            request,
            &mut buffer[..length],
            data_length,
            REPLY_TIMEOUT,
            2,
            Some(&mut retry),
        )?;

        // `try_ask` leaves the last failed attempt's late reply in the input, and the next request
        // would read its answer starting in the middle of it (or, for a tune write, take it for an
        // acknowledgement).
        if answer.is_none() {
            transport.discard_input();
        }

        Ok(answer)
    }

    /// One exchange: ask what is waiting, then take it.
    ///
    /// Nothing waiting is the ordinary case between updates rather than a failure, so it is
    /// counted and passed over. The previous reading stands, which is right: a channel that has
    /// not changed still has its value.
    fn round(&self, transport: &mut dyn EcuTransport, buffer: &mut [u8], values: &mut HashMap<i32, u16>) -> Result<()> {
        let Some(waiting) = self.ask(transport, buffer, &usb::ask_what_is_waiting(), 2)? else {
            return Ok(());
        };

        let mut bytes = i32::from(waiting[0]) | (i32::from(waiting[1]) << 8);

        if bytes == 0 {
            self.empty_rounds.fetch_add(1, Ordering::Relaxed);
            return Ok(());
        }

        if bytes > usb::MAXIMUM_DATA as i32 {
            bytes = usb::MAXIMUM_DATA as i32;
        }

        let Some(payload) = self.ask(transport, buffer, &usb::ask_for(bytes)?, bytes)? else {
            return Ok(());
        };

        // A payload whose ids do not ascend is not a list of channels, whatever its length
        // claimed. Taking it anyway writes plausible values against channel numbers nobody
        // sent.
        if !usb::looks_like_telemetry(&payload) {
            return Ok(());
        }

        usb::read_updates(&payload, values);
        Ok(())
    }
}

/// A handle on a MaxxECU's one USB conversation, for everything that is not the telemetry
/// poll: reading and writing the tune, and draining the CAN ring. Cloneable; every operation
/// takes the cable lock, so it runs between two rounds of the poll loop.
///
/// Each call can block for as long as the exchange in front of it (a tune read is about four
/// seconds), so none belongs on a path a window waits on.
#[derive(Clone)]
pub struct MaxxUsbLink {
    shared: Arc<Shared>,
}

impl MaxxUsbLink {
    /// Requests that had to be repeated, over the session.
    pub fn retries(&self) -> i32 {
        self.shared.retries.load(Ordering::Relaxed)
    }

    /// Reads the ECU's tune without ending the session.
    ///
    /// The poll loop is held off for the four seconds this takes, which is a gap in the log
    /// rather than a fault: the reading either side of it is the same reading, because a
    /// MaxxECU sends only what changed and the changes wait.
    pub fn read_tune(&self) -> Result<Vec<u8>> {
        let mut transport = self.shared.lock();
        let mut retry = || self.shared.count_retry();

        maxx_tune::read(&mut **transport, None, Some(&mut retry))
    }

    /// Puts bytes into the ECU's tune, between two rounds of the poll loop.
    ///
    /// **Permanent when it lands**; see `maxx_tune::write`. This only serialises it against
    /// the telemetry; everything about what is safe to send is the caller's to know.
    pub fn write_tune(&self, offset: i32, data: &[u8]) -> Result<MaxxWriteStatus> {
        maxx_tune::write(&mut **self.shared.lock(), offset, data)
    }

    /// Reads a run of the tune back, to check a write took.
    pub fn verify_tune(&self, offset: i32, expected: &[u8]) -> Result<bool> {
        maxx_tune::verify(&mut **self.shared.lock(), offset, expected)
    }

    /// Asks the ECU to checksum its own tune: the cheap way to ask whether anything moved that
    /// should not have.
    pub fn tune_checksums(&self) -> Result<Vec<u32>> {
        maxx_tune::checksums(&mut **self.shared.lock(), maxx_tune::WRITE_CEILING)
    }

    /// Empties the ECU's CAN ring, between two rounds of the poll loop.
    ///
    /// The ring gives up what it returns, so a reply this drops is a frame nobody sees again,
    /// which is why the failure here is silence rather than a retry. Asking a second time would
    /// not fetch the same frames, it would fetch the next ones, and the gap would go
    /// unrecorded.
    pub fn read_can_frames(&self, most: usize) -> Result<Vec<CanFrame>> {
        // `maxx_can::ask` validates `most` (0 < most <= MOST_PER_READ) before the request
        // bytes are built, and it is built first, not just to have the request ready: it is
        // what makes an out-of-range `most` fail with its own clear error instead of a raw
        // multiplication and an allocation seeing that same bad value first.
        let request = maxx_can::ask(most)?;

        let wanted = most * maxx_can::RECORD_LENGTH;
        let mut reply = vec![0u8; usb::reply_length(wanted as i32) as usize];

        let mut transport = self.shared.lock();
        transport.write(&request)?;

        let got = transport.read(&mut reply, REPLY_TIMEOUT)?;

        Ok(
            if got == reply.len()
                && let Some(data) = usb::try_read_reply(&reply, wanted as i32)
            {
                maxx_can::frames(&data)
            } else {
                transport.discard_input();
                Vec::new()
            },
        )
    }

    /// How many frames the ECU says it has lost, across its receive buffer and the analyzer
    /// ring together.
    ///
    /// Lives in the runtime snapshot rather than the tune, so it is read with the command that
    /// returns that, and it is the number that decides whether a capture can be called
    /// complete.
    pub fn can_drop_count(&self) -> Result<Option<i32>> {
        let mut reply = vec![0u8; usb::reply_length(2) as usize];

        let mut transport = self.shared.lock();
        transport.write(&usb::request(
            usb::READ,
            maxx_can::SNAPSHOT,
            maxx_can::DROP_COUNT_AT,
            2,
        )?)?;

        let got = transport.read(&mut reply, REPLY_TIMEOUT)?;

        Ok(
            if got == reply.len()
                && let Some(data) = usb::try_read_reply(&reply, 2)
            {
                Some(i32::from(data[0]) | (i32::from(data[1]) << 8))
            } else {
                transport.discard_input();
                None
            },
        )
    }

    /// Closes the cable (the C#'s `Dispose`). Waits for the cable lock, so it can wait on an
    /// exchange in flight: never call it on a path a window waits on.
    pub fn close(&self) {
        self.shared.lock().close();
    }
}

pub struct MaxxUsbSource {
    link: MaxxUsbLink,
    values: HashMap<i32, u16>,
    buffer: Vec<u8>,
    ids: Vec<i32>,
    channels: Vec<MaxxUsbChannel>,
    definitions_path: Option<PathBuf>,
    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
}

impl MaxxUsbSource {
    /// `definitions_path` is where MTune's channel definitions are, when they are anywhere;
    /// `None` looks for MTune's install.
    pub fn new(transport: Box<dyn EcuTransport>, definitions_path: Option<PathBuf>) -> Self {
        Self {
            link: MaxxUsbLink {
                shared: Arc::new(Shared {
                    cable: Mutex::new(transport),
                    retries: AtomicI32::new(0),
                    empty_rounds: AtomicI32::new(0),
                }),
            },
            values: HashMap::new(),
            buffer: vec![0; usb::MAXIMUM_DATA + usb::REPLY_OVERHEAD],
            ids: Vec::new(),
            channels: Vec::new(),
            definitions_path,
            names: Vec::new(),
            units: Vec::new(),
            digits: Vec::new(),
        }
    }

    /// The handle for tune and CAN operations from another thread, and for `MaxxCanSource`.
    pub fn link(&self) -> MaxxUsbLink {
        self.link.clone()
    }

    /// Where MTune's channel definitions are, when they are anywhere.
    pub fn definitions_path(&self) -> Option<&PathBuf> {
        self.definitions_path.as_ref()
    }

    /// Rounds where the ECU had nothing new to say, which is not a fault.
    pub fn empty_rounds(&self) -> i32 {
        self.link.shared.empty_rounds.load(Ordering::Relaxed)
    }

    /// The channels this session is reading, in the order it reports them.
    pub fn channels(&self) -> &[MaxxUsbChannel] {
        &self.channels
    }

    /// See [`MaxxUsbLink::read_tune`].
    pub fn read_tune(&self) -> Result<Vec<u8>> {
        self.link.read_tune()
    }

    /// See [`MaxxUsbLink::write_tune`]. Permanent when it lands.
    pub fn write_tune(&self, offset: i32, data: &[u8]) -> Result<MaxxWriteStatus> {
        self.link.write_tune(offset, data)
    }

    /// See [`MaxxUsbLink::verify_tune`].
    pub fn verify_tune(&self, offset: i32, expected: &[u8]) -> Result<bool> {
        self.link.verify_tune(offset, expected)
    }

    /// See [`MaxxUsbLink::tune_checksums`].
    pub fn tune_checksums(&self) -> Result<Vec<u32>> {
        self.link.tune_checksums()
    }

    /// See [`MaxxUsbLink::read_can_frames`].
    pub fn read_can_frames(&self, most: usize) -> Result<Vec<CanFrame>> {
        self.link.read_can_frames(most)
    }

    /// See [`MaxxUsbLink::can_drop_count`].
    pub fn can_drop_count(&self) -> Result<Option<i32>> {
        self.link.can_drop_count()
    }

    fn round(&mut self) -> Result<()> {
        let shared = Arc::clone(&self.link.shared);
        let mut transport = shared.lock();

        shared.round(&mut **transport, &mut self.buffer, &mut self.values)
    }

    /// Listens until the ECU stops naming channels it has not named before, then fixes that as
    /// the channel list.
    fn learn(&mut self) -> Result<()> {
        let deadline = Instant::now() + LEARN_FOR;

        while Instant::now() < deadline {
            self.round()?;
        }

        if self.values.is_empty() {
            return Err(Error::Protocol(
                "The MaxxECU answered but sent no channels, so there is nothing to log.".to_string(),
            ));
        }

        let ids: BTreeSet<i32> = self
            .values
            .keys()
            .copied()
            .chain(always_logged().iter().copied())
            .collect();
        self.ids = ids.into_iter().collect();

        let units = maxx_channel_table::units();
        let defined = maxx_channel_definitions::read(
            self.definitions_path
                .clone()
                .or_else(maxx_gauges::find_definitions)
                .as_deref(),
        );

        self.channels = self
            .ids
            .iter()
            .map(|&id| {
                let known = defined.get(&id);

                MaxxUsbChannel {
                    id,
                    // Named from MTune where it is installed, and by number where it is not. A
                    // number is a poor name and an honest one; a guess from the value would be
                    // neither.
                    name: known.map_or_else(|| format!("Channel {id}"), |k| k.name.clone()),
                    units: known.map_or_else(|| units.get(&id).cloned().unwrap_or_default(), |k| k.units.clone()),
                    scale: known.map_or(1.0, |k| k.scale),
                    is_signed: known.is_some_and(|k| k.is_signed),
                    digits: known.map_or(0, |k| k.digits),
                }
            })
            .collect();

        self.names = self.channels.iter().map(|c| c.name.clone()).collect();
        self.units = self.channels.iter().map(|c| c.units.clone()).collect();
        self.digits = self.channels.iter().map(|c| c.digits).collect();

        Ok(())
    }

    fn hello(&mut self, transport: &mut dyn EcuTransport) -> Result<bool> {
        let shared = Arc::clone(&self.link.shared);

        Ok(shared
            .ask(transport, &mut self.buffer, &usb::are_you_there(), 1)?
            .is_some())
    }
}

impl LiveSource for MaxxUsbSource {
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
        let shared = Arc::clone(&self.link.shared);

        {
            let mut transport = shared.lock();
            transport.open()?;

            // Prove something is there before learning anything from it. A reply to this is
            // the whole of "a MaxxECU is on the other end", and without it the first evidence
            // of an empty cable would be an empty channel list, which reads as an ECU with
            // nothing to say rather than as no ECU.
            if !self.hello(&mut **transport)? {
                return Err(Error::Protocol(
                    "Nothing on this USB cable answered as a MaxxECU. The device is open and the \
                     request went out, so check the ECU has 12 V \u{2014} its USB chip powers up \
                     from the cable alone, so it appears in the device list whether or not the \
                     ECU itself is running."
                        .to_string(),
                ));
            }
        }

        self.learn()
    }

    fn read(&mut self) -> Result<Vec<f64>> {
        if self.ids.is_empty() {
            return Err(Error::InvalidOperation("Open the connection first.".to_string()));
        }

        self.round()?;

        Ok(self
            .ids
            .iter()
            .zip(&self.channels)
            .map(|(id, channel)| {
                let raw = self.values.get(id).copied().unwrap_or(0);

                (if channel.is_signed {
                    f64::from(raw as i16)
                } else {
                    f64::from(raw)
                }) * channel.scale
            })
            .collect())
    }

    /// Reopens the link.
    ///
    /// The channel list is kept rather than learnt again: a log's columns cannot change once
    /// it has rows, so a reconnection that discovered a different set would have nowhere to
    /// put it.
    fn recover(&mut self) -> Result<()> {
        let shared = Arc::clone(&self.link.shared);
        let mut transport = shared.lock();

        // A device that has gone cannot be closed politely.
        transport.close();

        transport.open()?;

        if !self.hello(&mut **transport)? {
            return Err(Error::Protocol(
                "The MaxxECU did not answer after the link came back.".to_string(),
            ));
        }

        Ok(())
    }

    fn retries(&self) -> i32 {
        self.link.retries()
    }
}

impl Drop for MaxxUsbSource {
    /// `Dispose`: closes the cable, even when a [`MaxxUsbLink`] is still held elsewhere.
    fn drop(&mut self) {
        self.link.close();
    }
}
