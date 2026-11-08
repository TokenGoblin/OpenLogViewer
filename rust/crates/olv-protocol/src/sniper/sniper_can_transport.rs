//! Ported from `SniperCanTransport.cs`. Owned by package `w1-sniper`.
//!
//! A live CAN link to a Holley Sniper USB-to-CAN dongle (VID 0x2AD0, PID 0x1005): send a
//! frame, receive whatever arrives. Source: `SNIPER_ECU_CLIENT_SPEC.md` sections 1-2.
//!
//! The C# reached the dongle through Windows WinUSB P/Invoke. Here the work is split so
//! almost all of it is pure and testable without hardware:
//!
//! * the **framing** (the 20-byte `USBC` record, the RX resynchroniser, the vendor control
//!   requests) is plain functions and types in this file;
//! * the **device** sits behind the small [`UsbLink`] trait, implemented for real hardware by
//!   [`NusbLink`] (the pure-Rust `nusb` crate: no unsafe, no Windows-only API) and by fakes
//!   in the tests;
//! * [`SniperUsbTransport`] glues the two and implements [`SniperCanTransport`], the
//!   interface the session uses (the C# `ISniperCanTransport`).
//!
//! **Nothing here has been run against real hardware.** Treat a successful `open` as "the
//! USB layer answered", not as "this dongle and this code agree on anything above USB".
//!
//! # Differences from the C# the bench must confirm
//!
//! * Control requests and bulk writes time out after 1 s (the C# set no timeout).
//! * A bulk IN read is bounded by the receive deadline and cancelled when it passes (the C#
//!   blocked until data arrived).
//!
//! # The bootloader sequence cannot be sent
//!
//! The vendor control request that enters the dongle's firmware-flash bootloader
//! (`bRequest` 0x03 then 0x04) can brick the dongle, and this application never has a reason
//! to send it. It is made unreachable by construction: a [`ControlRequest`] has private
//! fields and can only be built from the closed [`DongleCommand`] enum (set speed, get
//! hardware version), whose request codes are compile-time-asserted not to be 0x03 or 0x04;
//! [`UsbLink`] implementations can only be handed a [`ControlRequest`]; and [`NusbLink`]
//! re-checks every request code against [`check_request_allowed`] before it reaches the wire.

use std::io;
use std::time::{Duration, Instant};

use nusb::MaybeFuture;
use nusb::descriptors::TransferType;
use nusb::transfer::{Buffer, Bulk, ControlIn, ControlOut, ControlType, In, Out, Recipient};
use olv_core::{Error, Result};

/// The dongle's USB vendor id. Spec section 1.
pub const VENDOR_ID: u16 = 0x2AD0;

/// The dongle's USB product id. Spec section 1.
pub const PRODUCT_ID: u16 = 0x1005;

/// The 4-byte ASCII marker opening every USB record, both directions: "USBC" read
/// little-endian. Spec section 1.
pub const USB_RECORD_MAGIC: u32 = 0x4342_5355;

/// Magic(4) + `can_frame`(16). Spec section 1.
pub const RECORD_SIZE: usize = 20;

/// One CAN frame as the Sniper dongle's own wire record carries it.
///
/// Not `olv_core::model::CanFrame`: that carries an arrival timestamp and an extended flag,
/// neither of which this dongle's 20-byte USB record has room for (offset 0: "USBC"; offset
/// 4: `uint32 can_id; uint32 dlc; uint8 data[8];`). Every id this protocol uses is 29-bit
/// extended, so there is no flag to carry either.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SniperCanFrame {
    /// The 29-bit extended identifier, held in the low bits of a `u32`.
    pub can_id: u32,
    /// The payload, 0-8 bytes: exactly `dlc` bytes, not padded.
    pub data: Vec<u8>,
}

impl SniperCanFrame {
    pub fn new(can_id: u32, data: impl Into<Vec<u8>>) -> Self {
        Self {
            can_id,
            data: data.into(),
        }
    }

    /// What the wire's `dlc` field says: how many bytes are real.
    pub fn dlc(&self) -> usize {
        self.data.len()
    }
}

/// A live CAN link to a Sniper dongle: send a frame, receive whatever arrives.
///
/// Deliberately not `CanSource`: that trait is a read-only sniffer's shape, and this dongle
/// is the opposite: everything above talks *through* it to a specific ECU, so sending is as
/// central as receiving.
///
/// Never exposes a way to enter the dongle's firmware-flash bootloader; see the module docs.
pub trait SniperCanTransport: Send {
    fn is_open(&self) -> bool;

    fn open(&mut self) -> Result<()>;

    fn close(&mut self);

    /// Sends one frame on the bus.
    fn send(&mut self, frame: &SniperCanFrame) -> Result<()>;

    /// The next frame to arrive, or `None` if none did within `timeout`. The timeout is a
    /// real one: a quiet bus returns after it, it does not block.
    fn receive(&mut self, timeout: Duration) -> Result<Option<SniperCanFrame>>;

    /// Sets the CAN bus bit rate. UNCONFIRMED: spec section 4 says the firmware-flash path
    /// uses 1 Mbit and calls it "the most likely bus rate", not a confirmed one.
    /// **[CONFIRM ON HW]**
    fn set_bitrate(&mut self, bits_per_second: u32) -> Result<()>;

    /// The dongle's own firmware version, mostly useful as a "did this even open" check.
    fn hardware_version(&mut self) -> Result<u32>;
}

/// Lets a session borrow a transport it does not own (the C# `ownsTransport = false`).
impl<T: SniperCanTransport + ?Sized> SniperCanTransport for &mut T {
    fn is_open(&self) -> bool {
        (**self).is_open()
    }
    fn open(&mut self) -> Result<()> {
        (**self).open()
    }
    fn close(&mut self) {
        (**self).close()
    }
    fn send(&mut self, frame: &SniperCanFrame) -> Result<()> {
        (**self).send(frame)
    }
    fn receive(&mut self, timeout: Duration) -> Result<Option<SniperCanFrame>> {
        (**self).receive(timeout)
    }
    fn set_bitrate(&mut self, bits_per_second: u32) -> Result<()> {
        (**self).set_bitrate(bits_per_second)
    }
    fn hardware_version(&mut self) -> Result<u32> {
        (**self).hardware_version()
    }
}

impl<T: SniperCanTransport + ?Sized> SniperCanTransport for Box<T> {
    fn is_open(&self) -> bool {
        (**self).is_open()
    }
    fn open(&mut self) -> Result<()> {
        (**self).open()
    }
    fn close(&mut self) {
        (**self).close()
    }
    fn send(&mut self, frame: &SniperCanFrame) -> Result<()> {
        (**self).send(frame)
    }
    fn receive(&mut self, timeout: Duration) -> Result<Option<SniperCanFrame>> {
        (**self).receive(timeout)
    }
    fn set_bitrate(&mut self, bits_per_second: u32) -> Result<()> {
        (**self).set_bitrate(bits_per_second)
    }
    fn hardware_version(&mut self) -> Result<u32> {
        (**self).hardware_version()
    }
}

// ---------------------------------------------------------------------------------------
// Framing: the 20-byte USB record.
// ---------------------------------------------------------------------------------------

/// Packs a frame into the dongle's 20-byte record: `"USBC"`, `can_id`, `dlc` (all
/// little-endian), then the data zero-padded to 8 bytes. Refuses more than 8 data bytes.
pub fn encode_record(frame: &SniperCanFrame) -> Result<[u8; RECORD_SIZE]> {
    if frame.dlc() > 8 {
        return Err(Error::Argument(
            "A CAN frame carries at most 8 bytes. (Parameter 'frame')".into(),
        ));
    }
    let mut record = [0u8; RECORD_SIZE];
    record[0..4].copy_from_slice(&USB_RECORD_MAGIC.to_le_bytes());
    record[4..8].copy_from_slice(&frame.can_id.to_le_bytes());
    record[8..12].copy_from_slice(&(frame.dlc() as u32).to_le_bytes());
    record[12..12 + frame.dlc()].copy_from_slice(&frame.data);
    Ok(record)
}

/// Bytes read off the IN pipe but not yet resolved into frames. RX has to resynchronise on
/// the "USBC" marker because USB packet boundaries do not align to 20-byte records (spec
/// section 1), so leftover bytes from one read carry over to the next.
#[derive(Debug, Default)]
pub struct RecordParser {
    buffer: Vec<u8>,
}

impl RecordParser {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn push(&mut self, bytes: &[u8]) {
        self.buffer.extend_from_slice(bytes);
    }

    pub fn clear(&mut self) {
        self.buffer.clear();
    }

    /// Bytes held that have not yet become a frame.
    pub fn pending(&self) -> usize {
        self.buffer.len()
    }

    /// Resynchronises on the "USBC" marker (discarding anything before it, one byte at a
    /// time in effect) and pulls out one complete record, if there is one. A `dlc` above 8 is
    /// clamped to 8 data bytes.
    pub fn next_frame(&mut self) -> Option<SniperCanFrame> {
        let magic = USB_RECORD_MAGIC.to_le_bytes();
        loop {
            if self.buffer.len() < 4 {
                return None;
            }
            if self.buffer[0..4] != magic {
                self.buffer.remove(0);
                continue;
            }
            if self.buffer.len() < RECORD_SIZE {
                return None;
            }
            let can_id = u32::from_le_bytes(self.buffer[4..8].try_into().ok()?);
            let dlc = u32::from_le_bytes(self.buffer[8..12].try_into().ok()?);
            let data_length = dlc.min(8) as usize;
            let data = self.buffer[12..12 + data_length].to_vec();
            self.buffer.drain(..RECORD_SIZE);
            return Some(SniperCanFrame { can_id, data });
        }
    }
}

// ---------------------------------------------------------------------------------------
// Framing: vendor control requests.
// ---------------------------------------------------------------------------------------

/// `bRequest` for SetSpeed. Spec section 1.
const REQUEST_SET_SPEED: u8 = 0x02;
/// `bRequest` for GetHardwareVersion. Spec section 1.
const REQUEST_GET_HARDWARE_VERSION: u8 = 0x00;

/// The request codes that must never be sent: 0x03 followed by 0x04 enters the dongle's
/// firmware-flash bootloader (spec sections 1 and 9). Each is refused on its own, which is
/// stricter than refusing the sequence and needs no state.
pub const FORBIDDEN_REQUESTS: [u8; 2] = [0x03, 0x04];

/// True when `request` is one of [`FORBIDDEN_REQUESTS`].
pub const fn is_forbidden_request(request: u8) -> bool {
    let mut i = 0;
    while i < FORBIDDEN_REQUESTS.len() {
        if FORBIDDEN_REQUESTS[i] == request {
            return true;
        }
        i += 1;
    }
    false
}

/// Refuses a forbidden request code with an error.
pub fn check_request_allowed(request: u8) -> Result<()> {
    if is_forbidden_request(request) {
        return Err(Error::InvalidOperation(format!(
            "Control request 0x{request:02X} can put the Sniper dongle into its firmware \
             bootloader and is never sent."
        )));
    }
    Ok(())
}

// Compile-time proof that the only requests this module can build are allowed ones.
const _: () = assert!(!is_forbidden_request(REQUEST_SET_SPEED));
const _: () = assert!(!is_forbidden_request(REQUEST_GET_HARDWARE_VERSION));

/// The only vendor commands this application can send to the dongle. There is deliberately
/// no variant for the bootloader entry: see the module docs.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DongleCommand {
    /// Sets the CAN bus bit rate.
    SetSpeed(u32),
    /// Reads the dongle's firmware version (4 bytes, little-endian).
    GetHardwareVersion,
}

/// Which way a control transfer's data stage goes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ControlData {
    /// Host to device: these bytes are sent.
    Out(Vec<u8>),
    /// Device to host: this many bytes are requested.
    In(u16),
}

/// One vendor control request, ready for a [`UsbLink`]. Fields are private and the only way
/// to make one is [`ControlRequest::for_command`], so no code outside this module can ask
/// for an arbitrary request code (in particular 0x03/0x04).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ControlRequest {
    request: u8,
    data: ControlData,
}

impl ControlRequest {
    /// Builds the request for a [`DongleCommand`].
    pub fn for_command(command: DongleCommand) -> Self {
        match command {
            DongleCommand::SetSpeed(bits_per_second) => Self {
                request: REQUEST_SET_SPEED,
                data: ControlData::Out(bits_per_second.to_le_bytes().to_vec()),
            },
            DongleCommand::GetHardwareVersion => Self {
                request: REQUEST_GET_HARDWARE_VERSION,
                data: ControlData::In(4),
            },
        }
    }

    /// `bRequest`.
    pub fn request(&self) -> u8 {
        self.request
    }

    /// The data stage.
    pub fn data(&self) -> &ControlData {
        &self.data
    }

    /// The `bmRequestType` byte: 0x22 (OUT, class, endpoint recipient) for SetSpeed, 0xA2
    /// (IN, class, endpoint recipient) for GetHardwareVersion. Spec section 1.
    pub fn request_type(&self) -> u8 {
        match self.data {
            ControlData::Out(_) => 0x22,
            ControlData::In(_) => 0xA2,
        }
    }

    /// `wValue` (always 0).
    pub fn value(&self) -> u16 {
        0
    }

    /// `wIndex` (always 0).
    pub fn index(&self) -> u16 {
        0
    }

    /// `wLength`.
    pub fn length(&self) -> u16 {
        match &self.data {
            ControlData::Out(bytes) => bytes.len() as u16,
            ControlData::In(len) => *len,
        }
    }
}

/// Reads the little-endian firmware version out of a GetHardwareVersion reply.
///
/// Matches the C#, which ignored how many bytes the control read returned and decoded its
/// zeroed 4-byte buffer: a short reply is zero-extended (`[1, 2]` gives `0x0201`), not an error.
pub fn parse_hardware_version(reply: &[u8]) -> u32 {
    let mut word = [0u8; 4];
    let n = reply.len().min(4);
    word[..n].copy_from_slice(&reply[..n]);
    u32::from_le_bytes(word)
}

// ---------------------------------------------------------------------------------------
// The device seam.
// ---------------------------------------------------------------------------------------

/// The raw USB side of the dongle: two bulk pipes and the control endpoint. This is the only
/// part that touches an operating system; [`NusbLink`] is the real one.
pub trait UsbLink: Send {
    /// Writes `bytes` to the bulk OUT pipe, all of it, or fails.
    fn bulk_write(&mut self, bytes: &[u8], timeout: Duration) -> Result<()>;

    /// Reads up to `buf.len()` bytes from the bulk IN pipe. Returns `Ok(0)` when nothing
    /// arrived within `timeout`; that is the ordinary quiet-bus case, not an error.
    fn bulk_read(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize>;

    /// Performs one control request. Returns the bytes read for an IN request, empty for OUT.
    fn control(&mut self, request: &ControlRequest, timeout: Duration) -> Result<Vec<u8>>;
}

const WRITE_TIMEOUT: Duration = Duration::from_secs(1);
const CONTROL_TIMEOUT: Duration = Duration::from_secs(1);
const RX_CHUNK: usize = 512;

/// The dongle over `nusb`: pure Rust, WinUSB on Windows, usbfs on Linux, IOKit on macOS.
///
/// On Windows the dongle must be bound to the WinUSB driver (as it is for Holley's own
/// software). The C# set the IN pipe's AUTO_FLUSH and RAW_IO policies. `nusb` 0.2.9 turns on
/// RAW_IO itself for every IN endpoint on Windows; it has no AUTO_FLUSH, which does not
/// matter while every IN request is a whole number of packets (as it is here).
pub struct NusbLink {
    // Held so the interface stays claimed while the endpoints are in use.
    interface: nusb::Interface,
    ep_in: nusb::Endpoint<Bulk, In>,
    ep_out: nusb::Endpoint<Bulk, Out>,
}

impl NusbLink {
    /// Finds the first dongle by VID/PID, opens it and claims its bulk interface.
    pub fn connect() -> Result<Self> {
        let info = nusb::list_devices()
            .wait()
            .map_err(usb_error)?
            .find(|d| d.vendor_id() == VENDOR_ID && d.product_id() == PRODUCT_ID)
            .ok_or_else(|| {
                Error::Io(io::Error::new(
                    io::ErrorKind::NotFound,
                    format!(
                        "No Sniper dongle found (USB VID_{VENDOR_ID:04X}&PID_{PRODUCT_ID:04X}). \
                         Check it is plugged in; this is the USB-to-CAN dongle, not the ECU itself."
                    ),
                ))
            })?;
        let device = info.open().wait().map_err(usb_error)?;
        // The C# used whichever interface WinUSB named for the device path. Here: the
        // interface (alternate setting 0) that has the bulk IN and OUT pipes, else 0.
        let layout: Vec<(u8, Vec<EndpointInfo>)> = device
            .active_configuration()
            .map(|config| {
                config
                    .interface_alt_settings()
                    .filter(|alt| alt.alternate_setting() == 0)
                    .map(|alt| {
                        let eps = alt
                            .endpoints()
                            .map(|ep| EndpointInfo {
                                address: ep.address(),
                                bulk: ep.transfer_type() == TransferType::Bulk,
                            })
                            .collect();
                        (alt.interface_number(), eps)
                    })
                    .collect()
            })
            .unwrap_or_default();
        let number = choose_interface(&layout);
        let interface = device.claim_interface(number).wait().map_err(usb_error)?;

        let found = layout
            .iter()
            .find(|(n, _)| *n == number)
            .and_then(|(_, eps)| bulk_pipes(eps));
        let Some((in_addr, out_addr)) = found else {
            return Err(Error::Io(io::Error::other(
                "The Sniper dongle did not present the two bulk pipes this protocol expects.",
            )));
        };

        let ep_in = interface.endpoint::<Bulk, In>(in_addr).map_err(usb_error)?;
        let ep_out = interface.endpoint::<Bulk, Out>(out_addr).map_err(usb_error)?;
        Ok(Self {
            interface,
            ep_in,
            ep_out,
        })
    }
}

/// One endpoint of an interface, reduced to what choosing the dongle's pipes needs.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct EndpointInfo {
    pub address: u8,
    pub bulk: bool,
}

/// The first bulk IN and first bulk OUT address among `endpoints` (spec section 1: the
/// direction bit 0x80 selects the role), if both exist.
pub fn bulk_pipes(endpoints: &[EndpointInfo]) -> Option<(u8, u8)> {
    let bulk = || endpoints.iter().filter(|e| e.bulk);
    let in_addr = bulk().find(|e| e.address & 0x80 != 0)?.address;
    let out_addr = bulk().find(|e| e.address & 0x80 == 0)?.address;
    Some((in_addr, out_addr))
}

/// The interface number to claim: the first whose endpoints include the bulk IN/OUT pair,
/// falling back to 0 only if none does.
pub fn choose_interface(interfaces: &[(u8, Vec<EndpointInfo>)]) -> u8 {
    interfaces
        .iter()
        .find(|(_, eps)| bulk_pipes(eps).is_some())
        .map_or(0, |(n, _)| *n)
}

/// Read failures that polling again cannot cure: the device disconnected (`nusb` maps that to
/// `ConnectionAborted`), is gone, or the request itself is invalid.
fn is_fatal_read(e: &io::Error) -> bool {
    matches!(
        e.kind(),
        io::ErrorKind::ConnectionAborted | io::ErrorKind::NotFound | io::ErrorKind::InvalidInput
    )
}

fn usb_error(e: nusb::Error) -> Error {
    Error::Io(io::Error::from(e))
}

fn transfer_error(what: &str, e: nusb::transfer::TransferError) -> Error {
    // Keep the error kind `nusb` maps to (a disconnect is ConnectionAborted), which the
    // transport uses to tell a fatal read failure from a transient one.
    let kind = io::Error::from(e).kind();
    Error::Io(io::Error::new(kind, format!("The Sniper dongle refused {what}: {e}")))
}

impl UsbLink for NusbLink {
    fn bulk_write(&mut self, bytes: &[u8], timeout: Duration) -> Result<()> {
        let completion = self.ep_out.transfer_blocking(Buffer::from(bytes), timeout);
        completion.status.map_err(|e| transfer_error("a write", e))?;
        if completion.actual_len != bytes.len() {
            return Err(Error::Io(io::Error::other(
                "The Sniper dongle accepted only part of a write.",
            )));
        }
        Ok(())
    }

    fn bulk_read(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize> {
        // An IN transfer's length must be a nonzero multiple of the endpoint's packet size.
        let mps = self.ep_in.max_packet_size().max(1);
        // Never request more than `buf` holds: round down to whole packets, never up.
        let want = (buf.len() / mps) * mps;
        if want == 0 {
            return Err(Error::Io(io::Error::new(
                io::ErrorKind::InvalidInput,
                "The read buffer is smaller than one USB packet.",
            )));
        }
        let completion = self.ep_in.transfer_blocking(Buffer::new(want), timeout);
        match completion.status {
            Ok(()) => {}
            // `transfer_blocking` reports a timeout as a cancelled transfer. With nothing
            // received it is the quiet bus; with a partial packet, keep what arrived.
            Err(nusb::transfer::TransferError::Cancelled) if completion.actual_len == 0 => {
                return Ok(0);
            }
            Err(nusb::transfer::TransferError::Cancelled) => {}
            Err(e) => return Err(transfer_error("a read", e)),
        }
        let n = completion.buffer.len().min(buf.len());
        buf[..n].copy_from_slice(&completion.buffer[..n]);
        Ok(n)
    }

    fn control(&mut self, request: &ControlRequest, timeout: Duration) -> Result<Vec<u8>> {
        // Defence in depth: unreachable through the public API, checked anyway.
        check_request_allowed(request.request())?;
        match request.data() {
            ControlData::Out(bytes) => {
                self.interface
                    .control_out(
                        ControlOut {
                            control_type: ControlType::Class,
                            recipient: Recipient::Endpoint,
                            request: request.request(),
                            value: request.value(),
                            index: request.index(),
                            data: bytes,
                        },
                        timeout,
                    )
                    .wait()
                    .map_err(|e| transfer_error("a control request", e))?;
                Ok(Vec::new())
            }
            ControlData::In(length) => self
                .interface
                .control_in(
                    ControlIn {
                        control_type: ControlType::Class,
                        recipient: Recipient::Endpoint,
                        request: request.request(),
                        value: request.value(),
                        index: request.index(),
                        length: *length,
                    },
                    timeout,
                )
                .wait()
                .map_err(|e| transfer_error("a control request", e)),
        }
    }
}

// ---------------------------------------------------------------------------------------
// The transport.
// ---------------------------------------------------------------------------------------

type Connector = Box<dyn FnMut() -> Result<Box<dyn UsbLink>> + Send>;

/// Reaches a Holley Sniper USB-to-CAN dongle. By default the device side is [`NusbLink`];
/// [`SniperUsbTransport::with_connector`] swaps it (for tests, or a recorded trace).
pub struct SniperUsbTransport {
    connector: Connector,
    link: Option<Box<dyn UsbLink>>,
    rx: RecordParser,
}

impl SniperUsbTransport {
    /// A transport that opens the real dongle over `nusb`.
    pub fn new() -> Self {
        Self::with_connector(|| Ok(Box::new(NusbLink::connect()?) as Box<dyn UsbLink>))
    }

    /// A transport whose `open` obtains its [`UsbLink`] from `connector`.
    pub fn with_connector(connector: impl FnMut() -> Result<Box<dyn UsbLink>> + Send + 'static) -> Self {
        Self {
            connector: Box::new(connector),
            link: None,
            rx: RecordParser::new(),
        }
    }

    fn link(&mut self) -> Result<&mut Box<dyn UsbLink>> {
        self.link
            .as_mut()
            .ok_or_else(|| Error::InvalidOperation("The Sniper dongle is not open.".into()))
    }
}

impl Default for SniperUsbTransport {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for SniperUsbTransport {
    fn drop(&mut self) {
        self.close();
    }
}

impl SniperCanTransport for SniperUsbTransport {
    fn is_open(&self) -> bool {
        self.link.is_some()
    }

    fn open(&mut self) -> Result<()> {
        if self.is_open() {
            return Ok(());
        }
        self.link = Some((self.connector)()?);
        self.rx.clear();
        Ok(())
    }

    fn close(&mut self) {
        self.link = None;
        self.rx.clear();
    }

    fn send(&mut self, frame: &SniperCanFrame) -> Result<()> {
        // The open check comes first, as in the C#.
        self.link()?;
        let record = encode_record(frame)?;
        self.link()?.bulk_write(&record, WRITE_TIMEOUT)
    }

    fn receive(&mut self, timeout: Duration) -> Result<Option<SniperCanFrame>> {
        self.link()?;
        // A timeout too large to add to the clock is treated as "wait practically forever".
        let deadline = Instant::now()
            .checked_add(timeout)
            .unwrap_or_else(|| Instant::now() + Duration::from_secs(60 * 60 * 24 * 365));

        loop {
            if let Some(frame) = self.rx.next_frame() {
                return Ok(Some(frame));
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Ok(None);
            }

            // Unlike the C#'s WinUsb_ReadPipe (which blocked until something arrived, so a
            // quiet bus hung forever), every read is bounded by what is left of the deadline.
            let mut chunk = [0u8; RX_CHUNK];
            match self.link()?.bulk_read(&mut chunk, remaining) {
                Ok(read) => self.rx.push(&chunk[..read]),
                // The C# dropped a failed read and kept polling until the deadline. Do the
                // same for a transient failure (stall, abort, timeout); stop only when the
                // device is gone or the read can never succeed.
                Err(Error::Io(e)) if !is_fatal_read(&e) => {}
                Err(e) => return Err(e),
            }
        }
    }

    fn set_bitrate(&mut self, bits_per_second: u32) -> Result<()> {
        let request = ControlRequest::for_command(DongleCommand::SetSpeed(bits_per_second));
        self.link()?.control(&request, CONTROL_TIMEOUT)?;
        Ok(())
    }

    fn hardware_version(&mut self) -> Result<u32> {
        let request = ControlRequest::for_command(DongleCommand::GetHardwareVersion);
        let reply = self.link()?.control(&request, CONTROL_TIMEOUT)?;
        Ok(parse_hardware_version(&reply))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;
    use std::sync::{Arc, Mutex};

    #[derive(Default)]
    struct Shared {
        writes: Vec<Vec<u8>>,
        controls: Vec<ControlRequest>,
        /// Chunks `bulk_read` hands out, one per call; empty means a quiet bus.
        reads: VecDeque<Vec<u8>>,
        version_reply: Vec<u8>,
        /// Errors `bulk_read` returns before it hands out any data, one per call.
        read_errors: VecDeque<io::ErrorKind>,
    }

    struct FakeLink(Arc<Mutex<Shared>>);

    impl UsbLink for FakeLink {
        fn bulk_write(&mut self, bytes: &[u8], _timeout: Duration) -> Result<()> {
            self.0.lock().unwrap().writes.push(bytes.to_vec());
            Ok(())
        }

        fn bulk_read(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize> {
            if let Some(kind) = self.0.lock().unwrap().read_errors.pop_front() {
                return Err(Error::Io(io::Error::new(kind, "scripted read failure")));
            }
            let next = self.0.lock().unwrap().reads.pop_front();
            match next {
                Some(chunk) => {
                    buf[..chunk.len()].copy_from_slice(&chunk);
                    Ok(chunk.len())
                }
                None => {
                    // A quiet bus: honour the timeout the way a real endpoint would.
                    std::thread::sleep(timeout.min(Duration::from_millis(5)));
                    Ok(0)
                }
            }
        }

        fn control(&mut self, request: &ControlRequest, _timeout: Duration) -> Result<Vec<u8>> {
            let mut s = self.0.lock().unwrap();
            s.controls.push(request.clone());
            Ok(match request.data() {
                ControlData::Out(_) => Vec::new(),
                ControlData::In(_) => s.version_reply.clone(),
            })
        }
    }

    fn fake() -> (SniperUsbTransport, Arc<Mutex<Shared>>) {
        let shared = Arc::new(Mutex::new(Shared::default()));
        let for_connector = Arc::clone(&shared);
        let transport = SniperUsbTransport::with_connector(move || {
            Ok(Box::new(FakeLink(Arc::clone(&for_connector))) as Box<dyn UsbLink>)
        });
        (transport, shared)
    }

    fn record(can_id: u32, data: &[u8]) -> Vec<u8> {
        encode_record(&SniperCanFrame::new(can_id, data.to_vec()))
            .unwrap()
            .to_vec()
    }

    #[test]
    fn the_magic_spells_usbc_on_the_wire() {
        assert_eq!(USB_RECORD_MAGIC.to_le_bytes(), *b"USBC");
    }

    #[test]
    fn a_record_is_magic_id_dlc_and_zero_padded_data() {
        // The ping frame the session opens with: id 0x70014004, no data.
        let ping = encode_record(&SniperCanFrame::new(0x7001_4004, vec![])).unwrap();
        assert_eq!(
            ping,
            [
                b'U', b'S', b'B', b'C', // magic
                0x04, 0x40, 0x01, 0x70, // can_id, little-endian
                0x00, 0x00, 0x00, 0x00, // dlc 0
                0, 0, 0, 0, 0, 0, 0, 0, // data, padded
            ]
        );

        let full = encode_record(&SniperCanFrame::new(0xA001_4004, vec![1, 2, 3, 4, 5, 6, 7, 8])).unwrap();
        assert_eq!(&full[0..4], b"USBC");
        assert_eq!(&full[4..8], &[0x04, 0x40, 0x01, 0xA0]);
        assert_eq!(&full[8..12], &[8, 0, 0, 0]);
        assert_eq!(&full[12..20], &[1, 2, 3, 4, 5, 6, 7, 8]);

        let short = encode_record(&SniperCanFrame::new(1, vec![0xAA, 0xBB, 0xCC])).unwrap();
        assert_eq!(&short[8..12], &[3, 0, 0, 0]);
        assert_eq!(&short[12..20], &[0xAA, 0xBB, 0xCC, 0, 0, 0, 0, 0]);
    }

    #[test]
    fn a_frame_with_more_than_eight_bytes_is_refused() {
        let err = encode_record(&SniperCanFrame::new(1, vec![0; 9])).unwrap_err();
        assert!(matches!(err, Error::Argument(_)));
    }

    #[test]
    fn the_parser_reads_a_whole_record() {
        let mut p = RecordParser::new();
        p.push(&record(0x1234_5678, &[9, 8, 7]));
        let frame = p.next_frame().unwrap();
        assert_eq!(frame, SniperCanFrame::new(0x1234_5678, vec![9, 8, 7]));
        assert_eq!(p.pending(), 0);
        assert!(p.next_frame().is_none());
    }

    #[test]
    fn the_parser_waits_for_the_rest_of_a_split_record() {
        let bytes = record(0xAB, &[1, 2]);
        let mut p = RecordParser::new();
        p.push(&bytes[..7]);
        assert!(p.next_frame().is_none());
        p.push(&bytes[7..]);
        assert_eq!(p.next_frame().unwrap().data, vec![1, 2]);
    }

    #[test]
    fn the_parser_resynchronises_past_garbage_and_splits_back_to_back_records() {
        let mut bytes = vec![0xFF, 0x00, b'U', b'S']; // garbage, including a false start
        bytes.extend(record(1, &[1]));
        bytes.extend(record(2, &[2]));
        let mut p = RecordParser::new();
        p.push(&bytes);
        assert_eq!(p.next_frame().unwrap().can_id, 1);
        assert_eq!(p.next_frame().unwrap().can_id, 2);
        assert!(p.next_frame().is_none());
    }

    #[test]
    fn the_parser_clamps_an_oversized_dlc_to_eight_bytes() {
        let mut bytes = record(7, &[1, 2, 3, 4, 5, 6, 7, 8]);
        bytes[8..12].copy_from_slice(&200u32.to_le_bytes());
        let mut p = RecordParser::new();
        p.push(&bytes);
        assert_eq!(p.next_frame().unwrap().data.len(), 8);
    }

    #[test]
    fn set_speed_is_class_out_to_the_endpoint_with_a_little_endian_rate() {
        let r = ControlRequest::for_command(DongleCommand::SetSpeed(1_000_000));
        assert_eq!(r.request_type(), 0x22);
        assert_eq!(r.request(), 0x02);
        assert_eq!((r.value(), r.index(), r.length()), (0, 0, 4));
        assert_eq!(r.data(), &ControlData::Out(vec![0x40, 0x42, 0x0F, 0x00]));
    }

    #[test]
    fn get_hardware_version_is_class_in_from_the_endpoint_for_four_bytes() {
        let r = ControlRequest::for_command(DongleCommand::GetHardwareVersion);
        assert_eq!(r.request_type(), 0xA2);
        assert_eq!(r.request(), 0x00);
        assert_eq!((r.value(), r.index(), r.length()), (0, 0, 4));
        assert_eq!(r.data(), &ControlData::In(4));
    }

    #[test]
    fn the_hardware_version_is_read_little_endian_and_a_short_reply_is_an_error() {
        assert_eq!(parse_hardware_version(&[0x01, 0x02, 0x03, 0x04]), 0x0403_0201);
        // The C# decodes its zeroed 4-byte buffer whatever the transfer length was.
        assert_eq!(parse_hardware_version(&[0x01, 0x02]), 0x0201);
        assert_eq!(parse_hardware_version(&[]), 0);
    }

    /// The bootloader sequence (control request 0x03 then 0x04) can brick the dongle. Neither
    /// code may be buildable from the public API, and neither may pass the link's own check.
    #[test]
    fn the_bootloader_entry_requests_cannot_be_built_or_sent() {
        // Every command the API can express, with a spread of speeds.
        let mut commands = vec![DongleCommand::GetHardwareVersion];
        commands.extend(
            [0, 1, 3, 4, 125_000, 500_000, 1_000_000, u32::MAX]
                .into_iter()
                .map(DongleCommand::SetSpeed),
        );
        for command in commands {
            let request = ControlRequest::for_command(command).request();
            assert!(
                !FORBIDDEN_REQUESTS.contains(&request),
                "{command:?} would send forbidden request 0x{request:02X}"
            );
            assert!(check_request_allowed(request).is_ok());
        }

        assert_eq!(FORBIDDEN_REQUESTS, [0x03, 0x04]);
        for forbidden in [0x03u8, 0x04] {
            assert!(is_forbidden_request(forbidden));
            assert!(matches!(
                check_request_allowed(forbidden),
                Err(Error::InvalidOperation(_))
            ));
        }
    }

    /// Everything the transport itself ever puts on the control endpoint, over a whole
    /// session, avoids the bootloader requests.
    #[test]
    fn a_whole_session_of_transport_calls_never_sends_a_bootloader_request() {
        let (mut t, shared) = fake();
        shared.lock().unwrap().version_reply = vec![1, 0, 0, 0];
        t.open().unwrap();
        t.set_bitrate(1_000_000).unwrap();
        t.hardware_version().unwrap();
        t.send(&SniperCanFrame::new(1, vec![])).unwrap();
        t.close();

        let s = shared.lock().unwrap();
        let sent: Vec<u8> = s.controls.iter().map(ControlRequest::request).collect();
        assert_eq!(sent, vec![0x02, 0x00]);
        assert!(sent.iter().all(|r| !FORBIDDEN_REQUESTS.contains(r)));
    }

    #[test]
    fn calls_on_a_closed_transport_are_refused() {
        let (mut t, _) = fake();
        assert!(!t.is_open());
        let frame = SniperCanFrame::new(1, vec![]);
        assert!(matches!(t.send(&frame), Err(Error::InvalidOperation(_))));
        assert!(matches!(
            t.receive(Duration::from_millis(1)),
            Err(Error::InvalidOperation(_))
        ));
        assert!(matches!(t.set_bitrate(1), Err(Error::InvalidOperation(_))));
        assert!(matches!(t.hardware_version(), Err(Error::InvalidOperation(_))));
    }

    #[test]
    fn send_writes_exactly_one_twenty_byte_record() {
        let (mut t, shared) = fake();
        t.open().unwrap();
        t.send(&SniperCanFrame::new(0x7001_4004, vec![])).unwrap();
        let s = shared.lock().unwrap();
        assert_eq!(s.writes.len(), 1);
        assert_eq!(s.writes[0], record(0x7001_4004, &[]));
        assert_eq!(s.writes[0].len(), RECORD_SIZE);
    }

    #[test]
    fn receive_assembles_a_frame_split_across_reads() {
        let (mut t, shared) = fake();
        let bytes = record(0xA001_4004, &[1, 2, 3, 4]);
        {
            let mut s = shared.lock().unwrap();
            s.reads.push_back(bytes[..9].to_vec());
            s.reads.push_back(bytes[9..].to_vec());
        }
        t.open().unwrap();
        let frame = t.receive(Duration::from_secs(1)).unwrap().unwrap();
        assert_eq!(frame, SniperCanFrame::new(0xA001_4004, vec![1, 2, 3, 4]));
    }

    /// The C# blocked forever on a quiet bus because its read had no timeout of its own. A
    /// quiet bus must return `None` after about the asked-for time.
    #[test]
    fn a_quiet_bus_times_out_instead_of_blocking_forever() {
        let (mut t, _) = fake();
        t.open().unwrap();
        let start = Instant::now();
        let got = t.receive(Duration::from_millis(60)).unwrap();
        let took = start.elapsed();
        assert!(got.is_none());
        assert!(took >= Duration::from_millis(60), "returned early: {took:?}");
        assert!(took < Duration::from_secs(2), "overran: {took:?}");
    }

    #[test]
    fn a_zero_timeout_returns_a_buffered_frame_but_never_reads() {
        let (mut t, shared) = fake();
        let two = [record(1, &[]), record(2, &[])].concat();
        shared.lock().unwrap().reads.push_back(two);
        t.open().unwrap();
        // First call has nothing buffered and no time to read.
        assert!(t.receive(Duration::ZERO).unwrap().is_none());
        assert_eq!(t.receive(Duration::from_secs(1)).unwrap().unwrap().can_id, 1);
        // The second record is already buffered, so even a zero timeout returns it.
        assert_eq!(t.receive(Duration::ZERO).unwrap().unwrap().can_id, 2);
    }

    #[test]
    fn open_is_idempotent_and_close_drops_buffered_bytes() {
        let (mut t, shared) = fake();
        t.open().unwrap();
        t.open().unwrap();
        assert!(t.is_open());
        shared.lock().unwrap().reads.push_back(vec![b'U', b'S']);
        let _ = t.receive(Duration::from_millis(1));
        t.close();
        assert!(!t.is_open());
        assert_eq!(t.rx.pending(), 0);
    }

    #[test]
    fn a_failing_connector_leaves_the_transport_closed() {
        let mut t =
            SniperUsbTransport::with_connector(|| Err(Error::Io(io::Error::new(io::ErrorKind::NotFound, "none"))));
        assert!(t.open().is_err());
        assert!(!t.is_open());
    }

    /// The C# dropped a failed read and kept polling. A stall or abort during discovery must
    /// not end the wait: the frame that follows is still delivered.
    #[test]
    fn a_transient_read_error_is_survived() {
        let (mut t, shared) = fake();
        {
            let mut s = shared.lock().unwrap();
            s.read_errors.push_back(io::ErrorKind::BrokenPipe); // a stall
            s.read_errors.push_back(io::ErrorKind::Interrupted); // a cancelled transfer
            s.reads.push_back(record(0x7001_4004, &[]));
        }
        t.open().unwrap();
        let frame = t.receive(Duration::from_secs(1)).unwrap().unwrap();
        assert_eq!(frame.can_id, 0x7001_4004);
    }

    #[test]
    fn a_disconnect_ends_the_wait() {
        let (mut t, shared) = fake();
        shared
            .lock()
            .unwrap()
            .read_errors
            .push_back(io::ErrorKind::ConnectionAborted);
        t.open().unwrap();
        assert!(matches!(t.receive(Duration::from_secs(1)), Err(Error::Io(_))));
    }

    #[test]
    fn the_interface_with_the_bulk_pair_is_chosen_and_zero_is_the_fallback() {
        let ep = |address, bulk| EndpointInfo { address, bulk };
        let control_only = vec![ep(0x83, false)];
        let bulk_pair = vec![ep(0x83, false), ep(0x81, true), ep(0x02, true)];
        assert_eq!(bulk_pipes(&bulk_pair), Some((0x81, 0x02)));
        assert_eq!(bulk_pipes(&[ep(0x81, true)]), None);
        assert_eq!(choose_interface(&[(0, control_only.clone()), (1, bulk_pair)]), 1);
        assert_eq!(choose_interface(&[(2, control_only)]), 0);
        assert_eq!(choose_interface(&[]), 0);
    }

    #[test]
    fn the_ids_are_the_dongles() {
        assert_eq!((VENDOR_ID, PRODUCT_ID), (0x2AD0, 0x1005));
    }
}
