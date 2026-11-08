//! Ported from `FtdiEcuTransport.cs`. Owned by package `w2-transports`.
//!
//! A MaxxECU's USB port, which is an FTDI device and not a COM port.
//!
//! This is the whole reason USB needed anything new. Every other cabled ECU here is reached
//! through [`SerialEcuTransport`](super::serial_ecu_transport::SerialEcuTransport), because
//! Windows gives it a COM number, and a MaxxECU never gets one. Its USB is an FTDI FT-X chip,
//! and the driver Maxxtuning ships for it (an FTDI *bus* driver for `VID_0403&PID_9728`)
//! installs the bus half only: there is no `FtdiPort` section in the INF, and so no virtual
//! COM port is ever created. Confirmed on a MaxxECU Race, where the device enumerates as
//! "MaxxECU" under the `FTDIBUS` service with no child port.
//!
//! The C# therefore went through FTDI's D2XX library (`ftd2xx.dll`), which is what MTune uses
//! too. That DLL is a third-party C library: it is never linked at build time and never shipped.
//! On Windows it is loaded at run time (see `d2xx.rs`); elsewhere, per OS:
//!
//! * **Linux and macOS**: the chip is driven directly over USB with `nusb`. [`protocol`] is
//!   FTDI's vendor control requests (reset, purge, baud-rate divisor, line properties, flow
//!   control, latency timer) as pure functions with byte-level tests, and the 2-byte modem
//!   status header FTDI puts at the front of every IN packet is stripped. On Linux the kernel's
//!   `ftdi_sio` driver is detached for the session (and returns when the device is released).
//! * **Windows**: FTDI's driver owns the device and there is no way in through USB. The route is
//!   FTDI's D2XX library, `ftd2xx.dll`, loaded at run time with `libloading` and called exactly
//!   as the C# called it (`d2xx.rs`). If it will not load, the fall-back is the virtual COM port
//!   the driver can expose, found in the serial port list by the FTDI vendor/product id and
//!   opened through our own `SerialPort`. If neither exists the error says the library is missing
//!   and the FTDI VCP driver option is needed (the Windows INF for the MaxxECU does not
//!   create one; enabling "Load VCP" in the device's advanced driver properties, or installing
//!   FTDI's CDM driver, does).
//!
//! Every setting the C# applied is kept: the baud rate (default 115200), 8-N-1, flow control
//! off, a 2 ms latency timer, a 50 ms driver read timeout (here, the read loop's own polling),
//! a 500 ms write timeout, and a read loop that waits for the whole buffer or the deadline.
//!
//! # Differences the bench must confirm
//!
//! * **Nothing here has touched a MaxxECU.** The request layer is checked against FTDI's
//!   documented byte values (libftdi's), not against the wire.
//! * The wIndex of every request carries interface number 1 (libftdi's convention for the
//!   first interface of a single- or multi-port chip) rather than D2XX's own choice.
//! * The baud divisor is libftdi's algorithm for the FT232R/FT-X family (48 MHz base, three
//!   fractional bits), and the 120 MHz one for the H chips. 115200 gives value 0x001A, index 0.
//! * An explicit device reset is sent before configuring; D2XX's `FT_Open` did its own.
//! * The write is chunked at 4096 bytes; D2XX took a whole buffer.
//! * On Windows the 2 ms latency timer cannot be set through a COM port: the VCP's own setting
//!   (default 16 ms) applies, and is changed in the port's advanced properties. Expect slower
//!   replies until it is lowered.
//! * `is_open` on a listed device is always `false`: nothing says whether another program
//!   (MTune) holds a device short of opening it, which reports "already open" as the C# did.
//! * On Linux, `nusb` needs permission on `/dev/bus/usb` (a udev rule for vendor 0403); the
//!   error says so. On macOS the system FTDI driver may refuse the claim, in which case the
//!   virtual COM port it exposes is used instead.
//! * Whether `ftdi_sio` reattaches by itself once the interface is released on Linux.

use std::io;
use std::sync::Arc;
use std::time::{Duration, Instant};

use olv_core::{Error, Result};
use olv_protocol::ms::ms_protocol::EcuTransport;

use super::serial_ecu_transport::{SerialIo, list_system_ports, open_real_port_with};
use super::serial_port::{PortInfo, PortType};

#[cfg(any(windows, test))]
mod d2xx;

// ---------------------------------------------------------------------------------------------
// Devices.
// ---------------------------------------------------------------------------------------------

/// One FTDI device the machine can see.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FtdiDevice {
    /// Its position in the list, which is how it is opened.
    pub index: usize,
    /// The serial in the chip's own EEPROM, `MX000000` on a MaxxECU. Steadier than the index,
    /// which renumbers as other FTDI devices come and go.
    pub serial: String,
    /// The chip's product string: "MaxxECU" on a MaxxECU.
    pub description: String,
    /// True when another program already has it. Not knowable without opening it, so the
    /// system listing leaves it false.
    pub is_open: bool,
    /// USB product id (the vendor id is always [`protocol::FTDI_VENDOR_ID`]).
    pub product_id: u16,
    /// The `bcdDevice` field, which says which FTDI chip it is.
    pub device_version: u16,
    usb_id: Option<nusb::DeviceId>,
}

/// The USB product id of a MaxxECU (vendor id [`protocol::FTDI_VENDOR_ID`]).
pub const MAXX_ECU_PRODUCT_ID: u16 = 0x9728;

impl FtdiDevice {
    pub fn new(index: usize, serial: &str, description: &str, is_open: bool) -> Self {
        Self {
            index,
            serial: serial.to_string(),
            description: description.to_string(),
            is_open,
            product_id: 0,
            device_version: 0,
            usb_id: None,
        }
    }

    /// True when this is a MaxxECU rather than some other FTDI device.
    ///
    /// Stricter than the C#, which went by the name alone: the USB product id must be a
    /// MaxxECU's (`VID_0403&PID_9728`) as well as the description or serial looking right. On
    /// Linux and macOS a match means detaching a kernel driver from the device, which must never
    /// happen to a MicroSquirt on an FT232R cable that merely has a serial starting "MX". A
    /// device whose product id is not known (0) is therefore not a MaxxECU.
    pub fn is_maxx_ecu(&self) -> bool {
        self.product_id == MAXX_ECU_PRODUCT_ID
            && (self.description.to_ascii_lowercase().contains("maxxecu")
                || self.serial.len() >= 2 && self.serial[..2].eq_ignore_ascii_case("MX"))
    }

    /// A MaxxECU that another program has open, as D2XX lists it.
    ///
    /// D2XX hides the serial and description of a device that is open elsewhere, so it cannot
    /// pass [`is_maxx_ecu`](Self::is_maxx_ecu); but it still reports the open flag and the USB
    /// id, and the MaxxECU's product id is its own. This is what MTune looks like while it is
    /// connected. It is never opened.
    pub fn is_maxx_ecu_held_elsewhere(&self) -> bool {
        self.is_open && self.product_id == MAXX_ECU_PRODUCT_ID && self.serial.is_empty() && self.description.is_empty()
    }

    /// What to show in a device list.
    pub fn label(&self) -> String {
        match (self.description.is_empty(), self.serial.is_empty()) {
            (false, false) => format!("{} ({})", self.description, self.serial),
            (false, true) => self.description.clone(),
            (true, false) => self.serial.clone(),
            (true, true) => format!("FTDI device {}", self.index),
        }
    }
}

/// Every FTDI device on the machine, found by USB vendor id on every OS.
///
/// Returns nothing at all rather than failing where USB cannot be listed: a machine with no
/// MaxxECU has none to list, and a connect menu is not the place to explain that.
fn system_devices() -> Vec<FtdiDevice> {
    use nusb::MaybeFuture;

    let Ok(list) = nusb::list_devices().wait() else {
        return Vec::new();
    };

    list.filter(|d| d.vendor_id() == protocol::FTDI_VENDOR_ID)
        .enumerate()
        .map(|(index, d)| FtdiDevice {
            index,
            serial: d.serial_number().unwrap_or_default().to_string(),
            description: d.product_string().unwrap_or_default().to_string(),
            is_open: false,
            product_id: d.product_id(),
            device_version: d.device_version(),
            usb_id: Some(d.id()),
        })
        .collect()
}

/// The device this transport is for, or the message saying why there is none.
///
/// The serial is matched case-insensitively when one is given; otherwise the first MaxxECU.
///
/// **Stricter than the C#.** `FtdiEcuTransport.Find()` fell back to the first FTDI device of
/// any kind when no MaxxECU was plugged in. Here only a device that
/// [`is_maxx_ecu`](FtdiDevice::is_maxx_ecu) is ever chosen, even when its serial is named. The
/// fallback is dangerous on Linux and macOS, where opening a device means taking it from the
/// kernel's own driver: a MicroSquirt on an FT232R cable that happened to be the only FTDI
/// device would have its session killed (mid-burn, perhaps) and then be sent MaxxECU frames.
/// A device that is not positively a MaxxECU is left alone.
pub fn choose_device<'a>(all: &'a [FtdiDevice], serial: &str) -> std::result::Result<&'a FtdiDevice, String> {
    if all.is_empty() {
        return Err(
            "No FTDI device is plugged in. A MaxxECU's USB port is an FTDI device rather \
                    than a COM port, so it appears in no list of serial ports \u{2014} check the \
                    cable, and that the ECU has power."
                .into(),
        );
    }

    let present = || all.iter().map(FtdiDevice::label).collect::<Vec<_>>().join(", ");

    // A MaxxECU that MTune (or anything else) has open lists with no serial and no description,
    // so it looks like nothing at all. Say what it is, rather than "no MaxxECU is plugged in".
    let held_elsewhere = || {
        all.iter().any(FtdiDevice::is_maxx_ecu_held_elsewhere).then(|| {
            "A MaxxECU is plugged in, but it is already open in another program. MTune holds the \
             USB link for as long as it is connected to the ECU, so disconnect it there first."
                .to_string()
        })
    };

    if serial.is_empty() {
        if let Some(found) = all.iter().find(|d| d.is_maxx_ecu()) {
            return Ok(found);
        }
        return Err(held_elsewhere().unwrap_or_else(|| {
            format!(
                "No MaxxECU is plugged in. The FTDI devices present are not MaxxECUs and are left \
                 alone: {}.",
                present()
            )
        }));
    }

    match all.iter().find(|d| d.serial.eq_ignore_ascii_case(serial)) {
        Some(d) if d.is_maxx_ecu() => Ok(d),
        None if held_elsewhere().is_some() => Err(held_elsewhere().unwrap_or_default()),
        Some(d) => Err(format!(
            "{} is not a MaxxECU, so it is left alone. Only a device identified as a MaxxECU is \
             ever opened here, because opening an FTDI device takes it from the driver that owns it.",
            d.label()
        )),
        None => Err(format!(
            "No FTDI device with serial {serial} is plugged in. Present: {}.",
            present()
        )),
    }
}

/// Refuses to touch a device that is not a MaxxECU. The last line of defence before a kernel
/// driver is detached: [`choose_device`] already only returns a MaxxECU, and this holds if a
/// backend is ever handed some other device.
fn ensure_maxx_ecu(device: &FtdiDevice) -> Result<()> {
    if device.is_maxx_ecu() {
        return Ok(());
    }
    Err(Error::Io(io::Error::new(
        io::ErrorKind::PermissionDenied,
        format!("{} is not a MaxxECU, so it is left alone.", device.label()),
    )))
}

// ---------------------------------------------------------------------------------------------
// FTDI's USB protocol, as pure functions.
// ---------------------------------------------------------------------------------------------

/// FTDI's vendor control requests and bulk framing. No I/O: bytes in, bytes out.
pub mod protocol {
    /// USB vendor id of FTDI.
    pub const FTDI_VENDOR_ID: u16 = 0x0403;

    /// `bmRequestType` of every request here: host to device, vendor, device recipient.
    pub const REQUEST_TYPE_OUT: u8 = 0x40;

    /// The wIndex interface number of the first (or only) port, libftdi's `INTERFACE_A`.
    pub const INTERFACE_A: u16 = 1;

    /// Bytes at the front of every IN packet: modem status, then line status.
    pub const STATUS_BYTES: usize = 2;

    /// The latency timer the C# set: how long the chip waits for more bytes before sending a
    /// short packet up the USB, which is dead time on the front of every reply.
    pub const LATENCY_TIMER_MS: u8 = 2;

    pub const SIO_RESET: u8 = 0x00;
    pub const SIO_SET_FLOW_CTRL: u8 = 0x02;
    pub const SIO_SET_BAUDRATE: u8 = 0x03;
    pub const SIO_SET_DATA: u8 = 0x04;
    pub const SIO_SET_LATENCY_TIMER: u8 = 0x09;

    const RESET_SIO: u16 = 0;
    const RESET_PURGE_RX: u16 = 1;
    const RESET_PURGE_TX: u16 = 2;

    /// One vendor control request with no data stage.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct ControlRequest {
        pub request: u8,
        pub value: u16,
        pub index: u16,
    }

    impl ControlRequest {
        /// The 8-byte USB SETUP packet: `bmRequestType`, `bRequest`, `wValue`, `wIndex`,
        /// `wLength` (zero), all 16-bit fields little-endian.
        pub fn setup_packet(&self) -> [u8; 8] {
            let v = self.value.to_le_bytes();
            let i = self.index.to_le_bytes();
            [REQUEST_TYPE_OUT, self.request, v[0], v[1], i[0], i[1], 0, 0]
        }
    }

    /// Which FTDI chip, from the device descriptor's `bcdDevice`. Decides the baud divisor.
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum ChipType {
        Am,
        Bm,
        Ft2232C,
        Ft232R,
        Ft2232H,
        Ft4232H,
        Ft232H,
        /// FT230X/FT231X/FT234XD: the MaxxECU's.
        FtX,
        /// Anything newer or unknown is driven as the FT232R family.
        Other,
    }

    impl ChipType {
        pub fn from_device_version(bcd_device: u16) -> Self {
            match bcd_device {
                0x0200 => Self::Am,
                0x0400 => Self::Bm,
                0x0500 => Self::Ft2232C,
                0x0600 => Self::Ft232R,
                0x0700 => Self::Ft2232H,
                0x0800 => Self::Ft4232H,
                0x0900 => Self::Ft232H,
                0x1000 => Self::FtX,
                _ => Self::Other,
            }
        }

        /// The high-speed chips, with a 120 MHz clock and a different index layout.
        fn is_h_type(self) -> bool {
            matches!(self, Self::Ft2232H | Self::Ft4232H | Self::Ft232H)
        }
    }

    /// SIO_RESET with value 0: resets the port.
    pub fn reset(interface: u16) -> ControlRequest {
        ControlRequest {
            request: SIO_RESET,
            value: RESET_SIO,
            index: interface,
        }
    }

    /// Purges the chip's receive buffer.
    pub fn purge_rx(interface: u16) -> ControlRequest {
        ControlRequest {
            request: SIO_RESET,
            value: RESET_PURGE_RX,
            index: interface,
        }
    }

    /// Purges the chip's transmit buffer.
    pub fn purge_tx(interface: u16) -> ControlRequest {
        ControlRequest {
            request: SIO_RESET,
            value: RESET_PURGE_TX,
            index: interface,
        }
    }

    /// Flow control off (`FT_SetFlowControl(handle, 0, 0, 0)`): the request's wIndex is the
    /// flow-control mode, zero, ORed with the interface.
    pub fn flow_control_none(interface: u16) -> ControlRequest {
        ControlRequest {
            request: SIO_SET_FLOW_CTRL,
            value: 0,
            index: interface,
        }
    }

    /// The latency timer in milliseconds (1 to 255).
    pub fn latency_timer(milliseconds: u8, interface: u16) -> ControlRequest {
        ControlRequest {
            request: SIO_SET_LATENCY_TIMER,
            value: u16::from(milliseconds),
            index: interface,
        }
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum Parity {
        None = 0,
        Odd = 1,
        Even = 2,
        Mark = 3,
        Space = 4,
    }

    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub enum StopBits {
        One = 0,
        OnePointFive = 1,
        Two = 2,
    }

    /// Data bits, parity and stop bits (`FT_SetDataCharacteristics`): the low byte is the word
    /// length, then parity in bits 8 to 10 and stop bits in bits 11 to 12.
    pub fn line_properties(data_bits: u8, stop_bits: StopBits, parity: Parity, interface: u16) -> ControlRequest {
        let value = u16::from(data_bits) | ((parity as u16) << 8) | ((stop_bits as u16) << 11);
        ControlRequest {
            request: SIO_SET_DATA,
            value,
            index: interface,
        }
    }

    /// The divisor encoding for one clock: libftdi's `ftdi_to_clkbits`. Returns the encoded
    /// divisor (14 bits of integer, three fractional-bit code bits at 14 to 16) and the baud
    /// rate it really gives.
    fn clock_bits(baud: u32, clock: u32, clock_div: u32) -> (u32, u32) {
        const FRAC_CODE: [u32; 8] = [0, 3, 2, 4, 1, 5, 6, 7];

        if baud >= clock / clock_div {
            (0, clock / clock_div)
        } else if baud >= clock / (clock_div + clock_div / 2) {
            (1, clock / (clock_div + clock_div / 2))
        } else if baud >= clock / (2 * clock_div) {
            (2, clock / (2 * clock_div))
        } else {
            // Divide by 16 to have three fractional bits and one for rounding.
            let divisor = clock * 16 / clock_div / baud;
            let mut best_divisor = if divisor & 1 == 1 { divisor / 2 + 1 } else { divisor / 2 };
            if best_divisor > 0x20000 {
                best_divisor = 0x1ffff;
            }
            let mut best_baud = clock * 16 / clock_div / best_divisor;
            best_baud = if best_baud & 1 == 1 {
                best_baud / 2 + 1
            } else {
                best_baud / 2
            };
            (
                (best_divisor >> 3) | (FRAC_CODE[(best_divisor & 7) as usize] << 14),
                best_baud,
            )
        }
    }

    /// The baud-rate request for `baud` on `chip` (`ftdi_convert_baudrate`), and the rate it
    /// really gives. `None` for zero, or when the nearest rate the divisor can make is more
    /// than 5% away (libftdi's own limit).
    pub fn baud_rate(baud: u32, chip: ChipType, interface: u16) -> Option<(ControlRequest, u32)> {
        if baud == 0 {
            return None;
        }

        const C_CLK: u32 = 48_000_000;
        const H_CLK: u32 = 120_000_000;

        let (encoded, actual) = if chip.is_h_type() && u64::from(baud) * 10 > u64::from(H_CLK) / 0x3fff {
            // 12 MHz-base rates when possible: a 14-bit divisor, a 1-bit divisor switch
            // (10 or 16), three fractional bits and a 120 MHz clock.
            let (encoded, actual) = clock_bits(baud, H_CLK, 10);
            (encoded | 0x20000, actual)
        } else {
            clock_bits(baud, C_CLK, 16)
        };

        if actual == 0 || u64::from(actual) * 21 / 20 < u64::from(baud) || u64::from(actual) * 19 / 20 > u64::from(baud)
        {
            return None;
        }

        let value = (encoded & 0xffff) as u16;
        let index = if chip.is_h_type() {
            (((encoded >> 8) & 0xff00) as u16) | interface
        } else {
            (encoded >> 16) as u16
        };

        Some((
            ControlRequest {
                request: SIO_SET_BAUDRATE,
                value,
                index,
            },
            actual,
        ))
    }

    /// Appends the payload of a raw IN transfer to `out`, dropping the 2 status bytes that open
    /// every `max_packet_size`-byte packet. A packet of two bytes or fewer is status alone.
    pub fn strip_status(raw: &[u8], max_packet_size: usize, out: &mut Vec<u8>) {
        for packet in raw.chunks(max_packet_size.max(STATUS_BYTES + 1)) {
            if packet.len() > STATUS_BYTES {
                out.extend_from_slice(&packet[STATUS_BYTES..]);
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// The device behind the transport.
// ---------------------------------------------------------------------------------------------

/// An opened FTDI device as a byte stream, so the transport's logic is testable with fakes.
pub trait FtdiLink: Send {
    /// Writes all of `data`, or fails.
    fn write(&mut self, data: &[u8], timeout: Duration) -> Result<()>;

    /// Waits up to `timeout` for data and copies what arrived into `buf`. Zero is "nothing
    /// yet", not an error.
    fn read_some(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize>;

    /// Drops anything buffered, in both directions.
    fn purge(&mut self) -> Result<()>;
}

/// Lists FTDI devices and opens one.
pub trait FtdiBackend: Send + Sync {
    fn devices(&self) -> Vec<FtdiDevice>;

    /// Why no device can be listed at all, when that is because the means of reaching one is
    /// missing (on Windows, FTDI's D2XX library): said instead of "no device is plugged in".
    fn unavailable_reason(&self) -> Option<String> {
        None
    }

    /// Opens `device` at `baud_rate`, configured and purged, ready to read.
    fn open(&self, device: &FtdiDevice, baud_rate: u32, write_timeout: Duration) -> Result<Box<dyn FtdiLink>>;
}

/// The serial port that is `device`'s virtual COM port, if the FTDI driver has made one.
///
/// Matched by FTDI's vendor id and the device's product id, and by serial where both sides
/// have one. FTDI's Windows instance ids append the port letter to the EEPROM serial
/// (`A50285BIA` for `A50285BI`), so a serial one character longer than the device's still
/// matches.
pub fn find_vcp_port(device: &FtdiDevice, ports: &[PortInfo]) -> Option<String> {
    ports
        .iter()
        .filter_map(|p| match &p.port_type {
            PortType::Usb(usb)
                if usb.vid == protocol::FTDI_VENDOR_ID && (device.product_id == 0 || usb.pid == device.product_id) =>
            {
                Some((p, usb))
            }
            _ => None,
        })
        .find(|(_, usb)| match usb.serial_number.as_deref() {
            Some(s) if !s.is_empty() && !device.serial.is_empty() => {
                s.eq_ignore_ascii_case(&device.serial)
                    || (s.len() == device.serial.len() + 1
                        && s[..device.serial.len()].eq_ignore_ascii_case(&device.serial))
            }
            _ => true,
        })
        .map(|(p, _)| p.port_name.clone())
}

fn vcp_missing_text(device: &FtdiDevice) -> String {
    format!(
        "{} is plugged in, but it has no virtual COM port. On Windows the FTDI driver owns \
         the device and a COM port is the only way to reach it without FTDI's D2XX library, so \
         the FTDI VCP driver option is needed: install FTDI's CDM driver, or in Device Manager \
         open the device's Properties > Advanced and tick \"Load VCP\", then unplug and replug \
         it. The MaxxECU driver package alone does not create one.",
        device.label()
    )
}

fn vcp_missing(device: &FtdiDevice) -> Error {
    Error::Io(io::Error::new(io::ErrorKind::NotFound, vcp_missing_text(device)))
}

/// The virtual-COM-port route: the FTDI device as an ordinary serial port.
mod vcp_link {
    use super::*;

    pub(super) struct VcpLink {
        port: Box<dyn SerialIo>,
    }

    impl VcpLink {
        pub(super) fn new(port: Box<dyn SerialIo>) -> Self {
            Self { port }
        }

        /// Opens the device's virtual COM port, 8-N-1 and no flow control, leaving DTR and RTS
        /// alone as D2XX did.
        pub(super) fn open(device: &FtdiDevice, ports: &[PortInfo], baud_rate: u32) -> Result<Self> {
            let name = find_vcp_port(device, ports).ok_or_else(|| vcp_missing(device))?;
            let port = open_real_port_with(&name, baud_rate, false)?;
            let mut link = Self::new(port);
            link.purge()?;
            Ok(link)
        }
    }

    impl FtdiLink for VcpLink {
        fn write(&mut self, data: &[u8], timeout: Duration) -> Result<()> {
            self.port.set_write_timeout(timeout);
            self.port.write_all(data).map_err(|e| {
                Error::Io(io::Error::new(
                    e.kind(),
                    format!("The MaxxECU's USB device refused a write ({e})."),
                ))
            })
        }

        fn read_some(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize> {
            // The driver's own timeout is the caller's remaining time, floored at 1 ms.
            self.port.set_read_timeout(timeout.max(Duration::from_millis(1)));
            match self.port.read(buf) {
                Ok(n) => Ok(n),
                Err(e) if matches!(e.kind(), io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock) => Ok(0),
                Err(e) => Err(Error::Io(io::Error::new(
                    e.kind(),
                    format!("The MaxxECU's USB device failed a read ({e})."),
                ))),
            }
        }

        fn purge(&mut self) -> Result<()> {
            self.port.discard_all().map_err(Error::from)
        }
    }
}

/// The direct-USB route, for Linux and macOS: FTDI's protocol over `nusb`.
#[cfg(not(windows))]
mod usb_link {
    use super::*;
    use nusb::MaybeFuture;
    use nusb::descriptors::TransferType;
    use nusb::transfer::{Buffer, Bulk, ControlOut, ControlType, In, Out, Recipient, TransferError};
    use olv_protocol::sniper::sniper_can_transport::{EndpointInfo, bulk_pipes, choose_interface};
    use std::collections::VecDeque;

    use super::protocol::{self, ChipType, ControlRequest};

    /// Control requests are quick; this is the allowance before one is called lost.
    const CONTROL_TIMEOUT: Duration = Duration::from_secs(1);

    /// Large writes go out in pieces of this size.
    const WRITE_CHUNK: usize = 4096;

    /// A read request is this many bytes (rounded to whole packets).
    const READ_REQUEST: usize = 4096;

    pub(super) struct UsbLink {
        interface: nusb::Interface,
        ep_in: nusb::Endpoint<Bulk, In>,
        ep_out: nusb::Endpoint<Bulk, Out>,
        /// Payload already received and stripped of its status bytes, not yet handed on.
        queued: VecDeque<u8>,
        max_packet: usize,
    }

    fn io_kind(e: &nusb::Error) -> io::ErrorKind {
        match e.kind() {
            nusb::ErrorKind::Disconnected => io::ErrorKind::ConnectionAborted,
            nusb::ErrorKind::Busy => io::ErrorKind::ResourceBusy,
            nusb::ErrorKind::PermissionDenied => io::ErrorKind::PermissionDenied,
            nusb::ErrorKind::NotFound => io::ErrorKind::NotFound,
            _ => io::ErrorKind::Other,
        }
    }

    fn open_error(device: &FtdiDevice, e: &nusb::Error) -> Error {
        let message = match e.kind() {
            // On a device that is plainly there this almost always means something else has it.
            // MTune holds it for as long as it is connected, and two programs cannot share one
            // FTDI device the way two can share a file.
            nusb::ErrorKind::Busy => format!(
                "{} is already open in another program. MTune holds the USB link for as long as \
                 it is connected to the ECU, so disconnect it there first.",
                device.label()
            ),
            nusb::ErrorKind::PermissionDenied => format!(
                "{} would not open: this user has no permission on the USB device. On Linux add \
                 a udev rule that gives your user access to vendor 0403 (for example \
                 SUBSYSTEM==\"usb\", ATTR{{idVendor}}==\"0403\", MODE=\"0666\") and replug it.",
                device.label()
            ),
            _ => format!("{} would not open ({e}).", device.label()),
        };
        Error::Io(io::Error::new(io_kind(e), message))
    }

    fn transfer_error(what: &str, e: TransferError) -> Error {
        let kind = match e {
            TransferError::Disconnected => io::ErrorKind::ConnectionAborted,
            TransferError::Cancelled => io::ErrorKind::TimedOut,
            _ => io::ErrorKind::Other,
        };
        Error::Io(io::Error::new(kind, format!("The MaxxECU's USB device {what} ({e}).")))
    }

    impl UsbLink {
        pub(super) fn open(device: &FtdiDevice, baud_rate: u32) -> Result<Self> {
            // Detaching the kernel driver below is only ever for a positively identified MaxxECU.
            ensure_maxx_ecu(device)?;

            let info = nusb::list_devices()
                .wait()
                .map_err(|e| open_error(device, &e))?
                .find(|d| Some(d.id()) == device.usb_id)
                .ok_or_else(|| {
                    Error::Io(io::Error::new(
                        io::ErrorKind::NotFound,
                        format!("{} is no longer plugged in.", device.label()),
                    ))
                })?;

            let usb = info.open().wait().map_err(|e| open_error(device, &e))?;

            let layout: Vec<(u8, Vec<EndpointInfo>)> = usb
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

            // The kernel's ftdi_sio owns the interface on Linux; detach it for the session.
            let interface = usb
                .detach_and_claim_interface(number)
                .wait()
                .map_err(|e| open_error(device, &e))?;

            let (in_addr, out_addr) = layout
                .iter()
                .find(|(n, _)| *n == number)
                .and_then(|(_, eps)| bulk_pipes(eps))
                .ok_or_else(|| {
                    Error::Io(io::Error::other(format!(
                        "{} did not present the two bulk pipes an FTDI chip has.",
                        device.label()
                    )))
                })?;

            let ep_in = interface
                .endpoint::<Bulk, In>(in_addr)
                .map_err(|e| open_error(device, &e))?;
            let ep_out = interface
                .endpoint::<Bulk, Out>(out_addr)
                .map_err(|e| open_error(device, &e))?;
            let max_packet = ep_in.max_packet_size().max(protocol::STATUS_BYTES + 1);

            let mut link = Self {
                interface,
                ep_in,
                ep_out,
                queued: VecDeque::new(),
                max_packet,
            };

            // Everything below is the chip's UART, not USB: the FTDI is a serial port in front
            // of the controller and these are its line settings.
            let chip = ChipType::from_device_version(device.device_version);
            link.control(protocol::reset(protocol::INTERFACE_A), "reset")?;
            let (baud_request, _) = protocol::baud_rate(baud_rate, chip, protocol::INTERFACE_A).ok_or_else(|| {
                Error::Io(io::Error::other(format!(
                    "The MaxxECU's USB device refused to set the baud rate ({baud_rate} is not \
                         a rate its divisor can make)."
                )))
            })?;
            link.control(baud_request, "set the baud rate")?;
            link.control(
                protocol::line_properties(
                    8,
                    protocol::StopBits::One,
                    protocol::Parity::None,
                    protocol::INTERFACE_A,
                ),
                "set 8-N-1",
            )?;
            link.control(
                protocol::flow_control_none(protocol::INTERFACE_A),
                "turn flow control off",
            )?;

            // Two milliseconds rather than the default sixteen. Its result was ignored in the C#.
            let _ = link.control(
                protocol::latency_timer(protocol::LATENCY_TIMER_MS, protocol::INTERFACE_A),
                "set the latency timer",
            );

            link.purge()?;
            Ok(link)
        }

        fn control(&mut self, request: ControlRequest, what: &str) -> Result<()> {
            self.interface
                .control_out(
                    ControlOut {
                        control_type: ControlType::Vendor,
                        recipient: Recipient::Device,
                        request: request.request,
                        value: request.value,
                        index: request.index,
                        data: &[],
                    },
                    CONTROL_TIMEOUT,
                )
                .wait()
                .map_err(|e| {
                    Error::Io(io::Error::other(format!(
                        "The MaxxECU's USB device refused to {what} ({e})."
                    )))
                })
        }

        /// Keeps exactly one read outstanding so nothing the chip sends is lost between calls.
        fn ensure_read_pending(&mut self) {
            if self.ep_in.pending() == 0 {
                let len = (READ_REQUEST / self.max_packet).max(1) * self.max_packet;
                self.ep_in.submit(Buffer::new(len));
            }
        }

        /// Takes a finished read's payload into `queued`.
        fn absorb(&mut self, completion: nusb::transfer::Completion) -> Result<()> {
            match completion.status {
                Ok(()) | Err(TransferError::Cancelled) => {}
                Err(e) => return Err(transfer_error("stopped answering", e)),
            }
            let raw = &completion.buffer[..completion.actual_len.min(completion.buffer.len())];
            let mut payload = Vec::with_capacity(raw.len());
            protocol::strip_status(raw, self.max_packet, &mut payload);
            self.queued.extend(payload);
            Ok(())
        }
    }

    impl FtdiLink for UsbLink {
        fn write(&mut self, data: &[u8], timeout: Duration) -> Result<()> {
            for chunk in data.chunks(WRITE_CHUNK) {
                let completion = self.ep_out.transfer_blocking(Buffer::from(chunk), timeout);
                completion.status.map_err(|e| transfer_error("refused a write", e))?;

                // A short write is not a slow one. The driver takes the whole buffer or the
                // device has gone, and carrying on with half a frame sent puts the controller's
                // parser out of step with no way back.
                if completion.actual_len != chunk.len() {
                    return Err(Error::Io(io::Error::other(format!(
                        "Only {} of {} bytes reached the MaxxECU's USB device.",
                        completion.actual_len,
                        chunk.len()
                    ))));
                }
            }
            Ok(())
        }

        fn read_some(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize> {
            if self.queued.is_empty() {
                self.ensure_read_pending();

                // The transfer stays pending if this times out, so nothing is lost.
                match self.ep_in.wait_next_complete(timeout) {
                    Some(completion) => self.absorb(completion)?,
                    None => return Ok(0),
                }
            }

            let n = buf.len().min(self.queued.len());
            for slot in buf.iter_mut().take(n) {
                *slot = self.queued.pop_front().unwrap_or_default();
            }
            Ok(n)
        }

        fn purge(&mut self) -> Result<()> {
            // Whatever has already come up the USB is stale, and so is what was handed on.
            while self.ep_in.pending() > 0 {
                if self.ep_in.wait_next_complete(Duration::ZERO).is_none() {
                    break;
                }
            }
            self.queued.clear();

            self.control(protocol::purge_rx(protocol::INTERFACE_A), "purge its receive buffer")?;
            self.control(protocol::purge_tx(protocol::INTERFACE_A), "purge its transmit buffer")
        }
    }

    impl Drop for UsbLink {
        fn drop(&mut self) {
            // Let go of the outstanding read before the interface goes, so the kernel driver can
            // take the device back.
            self.ep_in.cancel_all();
            let deadline = Instant::now() + Duration::from_millis(500);
            while self.ep_in.pending() > 0 && Instant::now() < deadline {
                let _ = self.ep_in.wait_next_complete(Duration::from_millis(50));
            }
        }
    }
}

/// The real machine: USB on Linux and macOS (with the virtual COM port as the fallback when
/// the system's own driver holds the chip), D2XX then the virtual COM port on Windows.
struct SystemBackend;

impl FtdiBackend for SystemBackend {
    fn devices(&self) -> Vec<FtdiDevice> {
        #[cfg(windows)]
        {
            d2xx::WindowsBackend::system().devices()
        }
        #[cfg(not(windows))]
        {
            system_devices()
        }
    }

    #[cfg(windows)]
    fn open(&self, device: &FtdiDevice, baud_rate: u32, write_timeout: Duration) -> Result<Box<dyn FtdiLink>> {
        d2xx::WindowsBackend::system().open(device, baud_rate, write_timeout)
    }

    #[cfg(windows)]
    fn unavailable_reason(&self) -> Option<String> {
        d2xx::WindowsBackend::system().unavailable_reason()
    }

    #[cfg(not(windows))]
    fn open(&self, device: &FtdiDevice, baud_rate: u32, _write_timeout: Duration) -> Result<Box<dyn FtdiLink>> {
        ensure_maxx_ecu(device)?;
        match usb_link::UsbLink::open(device, baud_rate) {
            Ok(link) => Ok(Box::new(link)),
            // Something else (macOS's own FTDI driver, another program) holds the chip, or this
            // user may not touch it: the virtual COM port it exposes is the other way in.
            Err(Error::Io(e)) if matches!(e.kind(), io::ErrorKind::ResourceBusy | io::ErrorKind::PermissionDenied) => {
                let ports = list_system_ports();
                if find_vcp_port(device, &ports).is_some() {
                    Ok(Box::new(vcp_link::VcpLink::open(device, &ports, baud_rate)?))
                } else {
                    Err(Error::Io(e))
                }
            }
            Err(e) => Err(e),
        }
    }
}

// ---------------------------------------------------------------------------------------------
// The transport.
// ---------------------------------------------------------------------------------------------

/// A MaxxECU's USB port as a byte stream.
pub struct FtdiEcuTransport {
    serial: String,
    baud_rate: i32,
    backend: Arc<dyn FtdiBackend>,
    link: Option<Box<dyn FtdiLink>>,
    /// What a write is given before the device is called dead.
    write_timeout: Duration,
}

impl FtdiEcuTransport {
    /// The rate to drive the FTDI's UART at.
    ///
    /// A MaxxECU's USB is an FTDI chip in front of the controller's serial port, so unlike a
    /// USB CDC device (where the rate is a fiction the host and device both ignore) this one is
    /// real and has to be right. Wrong, the link behaves exactly as though nothing were there:
    /// the bytes go out, the controller makes nothing of them, and it answers with silence or a
    /// byte or two of noise.
    pub const DEFAULT_BAUD_RATE: i32 = 115_200;

    /// Opens the MaxxECU by the serial in its own EEPROM where one is given (empty for
    /// "whichever MaxxECU is plugged in"; worth naming when two are, because the index depends
    /// on what was plugged in first and the serial does not). `baud_rate` must be at least 1.
    pub fn new(serial: &str, baud_rate: i32) -> Result<Self> {
        Self::with_backend(serial, baud_rate, Arc::new(SystemBackend))
    }

    /// As [`new`](Self::new), with the device side supplied: a fake in a test.
    pub fn with_backend(serial: &str, baud_rate: i32, backend: Arc<dyn FtdiBackend>) -> Result<Self> {
        if baud_rate < 1 {
            return Err(Error::Argument(format!(
                "baudRate ('{baud_rate}') must be greater than or equal to '1'. (Parameter 'baudRate')"
            )));
        }

        Ok(Self {
            serial: serial.to_string(),
            baud_rate,
            backend,
            link: None,
            write_timeout: Duration::from_millis(500),
        })
    }

    pub fn serial(&self) -> &str {
        &self.serial
    }

    pub fn baud_rate(&self) -> i32 {
        self.baud_rate
    }

    /// Every FTDI device on the machine.
    pub fn devices() -> Vec<FtdiDevice> {
        SystemBackend.devices()
    }

    /// Every MaxxECU on the machine, which is the list worth offering.
    pub fn maxx_ecus() -> Vec<FtdiDevice> {
        Self::devices().into_iter().filter(FtdiDevice::is_maxx_ecu).collect()
    }
}

impl EcuTransport for FtdiEcuTransport {
    fn is_open(&self) -> bool {
        self.link.is_some()
    }

    fn open(&mut self) -> Result<()> {
        if self.is_open() {
            return Ok(());
        }

        let all = self.backend.devices();
        if all.is_empty()
            && let Some(reason) = self.backend.unavailable_reason()
        {
            return Err(Error::Io(io::Error::new(io::ErrorKind::NotFound, reason)));
        }
        let device =
            choose_device(&all, &self.serial).map_err(|m| Error::Io(io::Error::new(io::ErrorKind::NotFound, m)))?;

        // The link applies the line settings and purges; on any failure it is dropped, which
        // closes the device, as the C#'s `Check` did.
        self.link = Some(self.backend.open(device, self.baud_rate as u32, self.write_timeout)?);
        Ok(())
    }

    fn close(&mut self) {
        // A device that has gone cannot be closed politely.
        self.link = None;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let timeout = self.write_timeout;
        let Some(link) = self.link.as_mut() else {
            return Err(Error::InvalidOperation("The MaxxECU's USB device is not open.".into()));
        };
        if data.is_empty() {
            return Ok(());
        }
        link.write(data, timeout)
    }

    fn write_timeout(&self) -> Duration {
        self.write_timeout
    }

    fn set_write_timeout(&mut self, timeout: Duration) {
        if timeout > Duration::ZERO {
            self.write_timeout = timeout;
        }
    }

    /// Fills the buffer, or returns fewer bytes if the timeout passes first: the same contract
    /// the serial transport keeps.
    ///
    /// The device's own read is kept short and the deadline is kept here, so a caller asking for
    /// a large buffer with a small allowance is not held for the driver's timeout instead of its
    /// own.
    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        let Some(link) = self.link.as_mut() else {
            return Err(Error::InvalidOperation("The MaxxECU's USB device is not open.".into()));
        };
        if buffer.is_empty() {
            return Ok(0);
        }

        let mut total = 0;
        let deadline = Instant::now() + timeout;

        while total < buffer.len() {
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                break;
            }
            total += link.read_some(&mut buffer[total..], left)?;
        }

        Ok(total)
    }

    /// Drops anything buffered, in both directions. Failing is not worth reporting: this is
    /// hygiene before a request, and a device that cannot be cleared will say so again on the
    /// read that follows.
    fn discard_input(&mut self) {
        if let Some(link) = self.link.as_mut() {
            let _ = link.purge();
        }
    }
}

impl Drop for FtdiEcuTransport {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::protocol::*;
    use super::*;

    /// A device as the USB list reports it: a MaxxECU has its product id, anything else an FT232R's.
    fn test_device(index: usize, serial: &str, description: &str, is_open: bool) -> FtdiDevice {
        let mut d = FtdiDevice::new(index, serial, description, is_open);
        let maxx =
            description.to_ascii_lowercase().contains("maxxecu") || serial.to_ascii_uppercase().starts_with("MX");
        d.product_id = if maxx { MAXX_ECU_PRODUCT_ID } else { 0x6001 };
        d
    }
    use std::collections::VecDeque;
    use std::sync::Mutex;
    use std::thread;

    // ----- the C# tests ---------------------------------------------------------------------

    #[test]
    fn a_maxx_ecu_is_told_from_any_other_ftdi_device() {
        // description, serial, expected
        let cases = [
            ("MaxxECU", "MX000000", true), // what a real one reports
            ("maxxecu", "MX123456", true), // the description is not case-sensitive
            ("", "MX000000", true),        // serial alone is enough
            ("MaxxECU Race", "", true),    // so is the description
            ("USB Serial Converter", "A50285BIA", false),
            ("FT232R USB UART", "", false),
            ("", "", false),
        ];
        for (description, serial, expected) in cases {
            assert_eq!(
                test_device(0, serial, description, false).is_maxx_ecu(),
                expected,
                "{description:?} {serial:?}"
            );
        }
    }

    #[test]
    fn a_device_is_labelled_with_whatever_it_reports() {
        let cases = [
            ("MaxxECU", "MX000000", "MaxxECU (MX000000)"),
            ("MaxxECU", "", "MaxxECU"),
            ("", "MX000000", "MX000000"),
            ("", "", "FTDI device 3"),
        ];
        for (description, serial, expected) in cases {
            assert_eq!(test_device(3, serial, description, false).label(), expected);
        }
    }

    #[test]
    fn listing_devices_is_safe_with_or_without_usb() {
        // Either a list or an empty one, and never a panic: this runs while a menu is being built.
        let all = FtdiEcuTransport::devices();
        assert!(all.iter().all(|d| d.index < all.len()));

        // And the MaxxECU list is a subset of it, by the same rule.
        let maxx = FtdiEcuTransport::maxx_ecus();
        assert!(maxx.iter().all(FtdiDevice::is_maxx_ecu));
        assert!(maxx.iter().all(|m| all.iter().any(|d| d.serial == m.serial)));
    }

    #[test]
    fn a_nonsense_baud_rate_is_refused_before_anything_is_opened() {
        assert!(matches!(FtdiEcuTransport::new("", 0), Err(Error::Argument(_))));
        assert!(matches!(FtdiEcuTransport::new("", -5), Err(Error::Argument(_))));
    }

    /// A machine with these devices plugged in, and links that are fakes.
    struct FakeBackend {
        devices: Vec<FtdiDevice>,
        link: Mutex<Option<Box<dyn FtdiLink>>>,
        opened: Mutex<Vec<(String, u32)>>,
    }

    impl FakeBackend {
        fn new(devices: Vec<FtdiDevice>, link: Option<Box<dyn FtdiLink>>) -> Arc<Self> {
            Arc::new(Self {
                devices,
                link: Mutex::new(link),
                opened: Mutex::new(Vec::new()),
            })
        }
    }

    impl FtdiBackend for FakeBackend {
        fn devices(&self) -> Vec<FtdiDevice> {
            self.devices.clone()
        }

        fn open(&self, device: &FtdiDevice, baud: u32, _: Duration) -> Result<Box<dyn FtdiLink>> {
            self.opened.lock().unwrap().push((device.serial.clone(), baud));
            self.link
                .lock()
                .unwrap()
                .take()
                .ok_or_else(|| Error::Io(io::Error::other("no link")))
        }
    }

    #[derive(Default)]
    struct Wire {
        incoming: VecDeque<Vec<u8>>,
        written: Vec<Vec<u8>>,
        purges: usize,
    }

    struct FakeLink(Arc<Mutex<Wire>>);

    impl FtdiLink for FakeLink {
        fn write(&mut self, data: &[u8], _: Duration) -> Result<()> {
            self.0.lock().unwrap().written.push(data.to_vec());
            Ok(())
        }

        fn read_some(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize> {
            let mut w = self.0.lock().unwrap();
            let Some(mut piece) = w.incoming.pop_front() else {
                drop(w);
                thread::sleep(timeout.min(Duration::from_millis(5)));
                return Ok(0);
            };
            let n = piece.len().min(buf.len());
            buf[..n].copy_from_slice(&piece[..n]);
            if n < piece.len() {
                w.incoming.push_front(piece.split_off(n));
            }
            Ok(n)
        }

        fn purge(&mut self) -> Result<()> {
            let mut w = self.0.lock().unwrap();
            w.purges += 1;
            w.incoming.clear();
            Ok(())
        }
    }

    fn maxx() -> FtdiDevice {
        test_device(0, "MX000000", "MaxxECU", false)
    }

    fn open_transport(wire: &Arc<Mutex<Wire>>) -> FtdiEcuTransport {
        let backend = FakeBackend::new(vec![maxx()], Some(Box::new(FakeLink(wire.clone()))));
        let mut t = FtdiEcuTransport::with_backend("", 115_200, backend).unwrap();
        t.open().unwrap();
        t
    }

    #[test]
    fn reading_or_writing_before_opening_says_so_rather_than_crashing() {
        let mut t = FtdiEcuTransport::new("no-such-device", 115_200).unwrap();

        assert!(!t.is_open());
        assert!(matches!(t.write(&[1, 2, 3]), Err(Error::InvalidOperation(_))));
        assert!(matches!(
            t.read(&mut [0u8; 8], Duration::ZERO),
            Err(Error::InvalidOperation(_))
        ));

        // And closing one that was never open is not an error, because that is what dropping a
        // failed connection does.
        t.close();
        t.discard_input();
    }

    #[test]
    fn opening_a_device_that_is_not_there_explains_itself() {
        let backend = FakeBackend::new(vec![], None);
        let mut t = FtdiEcuTransport::with_backend("definitely-not-a-real-serial", 115_200, backend).unwrap();

        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().starts_with("No FTDI device is plugged in."));
        assert!(!t.is_open());
    }

    #[test]
    fn a_maxx_ecu_usb_transport_is_an_ordinary_transport() {
        // It has to be, or none of the protocol above it could be shared with the Bluetooth
        // path. This is the whole design in one assertion.
        fn takes<T: EcuTransport>(_: &T) {}
        let t = FtdiEcuTransport::new("", FtdiEcuTransport::DEFAULT_BAUD_RATE).unwrap();
        takes(&t);
        assert_eq!(t.baud_rate(), 115_200);
    }

    // ----- choosing the device --------------------------------------------------------------

    #[test]
    fn the_serial_picks_the_device_case_insensitively() {
        let all = [
            test_device(0, "A50285BI", "FT232R USB UART", false),
            test_device(1, "MX000001", "MaxxECU", false),
        ];
        assert_eq!(choose_device(&all, "mx000001").unwrap().index, 1);
    }

    #[test]
    fn with_no_serial_the_first_maxx_ecu_wins_over_other_ftdi_devices() {
        let all = [
            test_device(0, "A50285BI", "FT232R USB UART", false),
            test_device(1, "MX000001", "MaxxECU", false),
        ];
        assert_eq!(choose_device(&all, "").unwrap().index, 1);
    }

    #[test]
    fn with_no_maxx_ecu_an_other_ftdi_device_is_never_chosen() {
        // The C# fell back to the first FTDI device. On Linux that detaches the kernel driver, so
        // a MicroSquirt on an FT232R cable would have its session killed and be sent MaxxECU
        // frames. Only a device identified as a MaxxECU is ever chosen.
        let none = [test_device(0, "A50285BI", "FT232R USB UART", false)];
        let message = choose_device(&none, "").unwrap_err();
        assert!(message.starts_with("No MaxxECU is plugged in."), "{message}");
        assert!(message.contains("FT232R USB UART (A50285BI)"));
        assert!(message.contains("left alone"));
    }

    #[test]
    fn naming_the_serial_of_an_other_ftdi_device_does_not_choose_it_either() {
        let all = [
            test_device(0, "A50285BI", "FT232R USB UART", false),
            test_device(1, "MX000001", "MaxxECU", false),
        ];
        let message = choose_device(&all, "a50285bi").unwrap_err();
        assert!(message.contains("is not a MaxxECU"), "{message}");
        assert!(message.contains("left alone"));
    }

    #[test]
    fn a_device_that_is_not_a_maxx_ecu_is_refused_before_any_driver_is_detached() {
        // The last line of defence: every route to a link goes through this first.
        let microsquirt = test_device(0, "A50285BI", "FT232R USB UART", false);
        let Err(Error::Io(e)) = ensure_maxx_ecu(&microsquirt) else {
            panic!("expected a refusal")
        };
        assert!(e.to_string().contains("is not a MaxxECU, so it is left alone."));
        assert!(ensure_maxx_ecu(&test_device(0, "MX000000", "", false)).is_ok());
        assert!(ensure_maxx_ecu(&test_device(0, "", "MaxxECU", false)).is_ok());
    }

    #[test]
    fn a_transport_given_only_another_ftdi_device_opens_nothing() {
        let wire = Arc::new(Mutex::new(Wire::default()));
        let backend = FakeBackend::new(
            vec![test_device(0, "A50285BI", "FT232R USB UART", false)],
            Some(Box::new(FakeLink(wire))),
        );
        let mut t = FtdiEcuTransport::with_backend("", 115_200, backend).unwrap();

        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().starts_with("No MaxxECU is plugged in."));
        assert!(!t.is_open());
    }

    #[test]
    fn a_serial_that_is_not_present_lists_what_is() {
        let all = [
            test_device(0, "A50285BI", "FT232R USB UART", false),
            test_device(1, "MX000001", "MaxxECU", false),
        ];
        let message = choose_device(&all, "MX999").unwrap_err();
        assert_eq!(
            message,
            "No FTDI device with serial MX999 is plugged in. Present: FT232R USB UART (A50285BI), MaxxECU (MX000001)."
        );
    }

    #[test]
    fn nothing_listed_because_the_library_is_missing_says_the_driver_is_not_installed() {
        struct NoLibrary;
        impl FtdiBackend for NoLibrary {
            fn devices(&self) -> Vec<FtdiDevice> {
                Vec::new()
            }
            fn open(&self, _: &FtdiDevice, _: u32, _: Duration) -> Result<Box<dyn FtdiLink>> {
                unreachable!("nothing to open")
            }
            fn unavailable_reason(&self) -> Option<String> {
                Some("The FTDI driver is not installed.".into())
            }
        }
        let mut t = FtdiEcuTransport::with_backend("", 115_200, Arc::new(NoLibrary)).unwrap();
        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        assert_eq!(e.to_string(), "The FTDI driver is not installed.");
    }

    #[test]
    fn a_serial_that_looks_like_a_maxx_ecu_on_another_product_id_is_not_one() {
        // A MicroSquirt on an FT232R cable whose EEPROM serial happens to start "MX": on Linux
        // and macOS a match detaches its kernel driver, so the product id must agree.
        let mut cable = FtdiDevice::new(0, "MX123456", "FT232R USB UART", false);
        cable.product_id = 0x6001;
        assert!(!cable.is_maxx_ecu());
        cable.description = "MaxxECU".into();
        assert!(!cable.is_maxx_ecu());

        // And one whose product id is simply not known is not positively identified either.
        let unknown = FtdiDevice::new(0, "MX000000", "MaxxECU", false);
        assert!(!unknown.is_maxx_ecu());
        assert!(ensure_maxx_ecu(&unknown).is_err());

        let all = [cable];
        assert!(choose_device(&all, "").is_err());
        assert!(choose_device(&all, "MX123456").is_err());
    }

    #[test]
    fn a_maxx_ecu_that_mtune_holds_is_reported_as_held_and_never_chosen() {
        // D2XX lists a device open in another process with no serial and no description, but
        // with the open flag and the USB id.
        let mut held = FtdiDevice::new(0, "", "", true);
        held.product_id = MAXX_ECU_PRODUCT_ID;
        assert!(!held.is_maxx_ecu());
        assert!(held.is_maxx_ecu_held_elsewhere());

        for serial in ["", "MX000000"] {
            let message = choose_device(std::slice::from_ref(&held), serial).unwrap_err();
            assert!(message.contains("already open in another program"), "{message}");
            assert!(message.contains("MTune holds the USB link"));
            assert!(!message.contains("No MaxxECU is plugged in"));
        }

        // An empty FT232R that is open is not a MaxxECU, and is not mistaken for one.
        let mut other = FtdiDevice::new(0, "", "", true);
        other.product_id = 0x6001;
        assert!(!other.is_maxx_ecu_held_elsewhere());
        assert!(
            choose_device(&[other], "")
                .unwrap_err()
                .starts_with("No MaxxECU is plugged in.")
        );

        // Not open: a blank device is just a blank device.
        let mut closed = FtdiDevice::new(0, "", "", false);
        closed.product_id = MAXX_ECU_PRODUCT_ID;
        assert!(!closed.is_maxx_ecu_held_elsewhere());

        // And it is never opened.
        assert!(ensure_maxx_ecu(&held).is_err());
    }

    #[test]
    fn no_devices_at_all_says_to_check_the_cable() {
        let message = choose_device(&[], "").unwrap_err();
        assert!(message.contains("appears in no list of serial ports"));
        assert!(message.contains("that the ECU has power"));
    }

    // ----- the transport over a fake link ---------------------------------------------------

    #[test]
    fn opening_asks_the_backend_for_the_chosen_device_at_the_baud_rate() {
        let wire = Arc::new(Mutex::new(Wire::default()));
        let backend = FakeBackend::new(
            vec![test_device(0, "OTHER", "FT232R", false), maxx()],
            Some(Box::new(FakeLink(wire))),
        );
        let mut t = FtdiEcuTransport::with_backend("", 57_600, backend.clone()).unwrap();
        t.open().unwrap();

        assert!(t.is_open());
        assert_eq!(*backend.opened.lock().unwrap(), vec![("MX000000".to_string(), 57_600)]);
        // Opening again does nothing.
        t.open().unwrap();
    }

    #[test]
    fn a_read_gathers_pieces_until_the_buffer_is_full() {
        let wire = Arc::new(Mutex::new(Wire::default()));
        let mut t = open_transport(&wire);
        wire.lock()
            .unwrap()
            .incoming
            .extend([vec![1, 2], vec![3], vec![4, 5, 6]]);

        let mut buf = [0u8; 6];
        assert_eq!(t.read(&mut buf, Duration::from_millis(500)).unwrap(), 6);
        assert_eq!(buf, [1, 2, 3, 4, 5, 6]);
    }

    #[test]
    fn a_short_read_returns_what_arrived_when_the_time_is_up() {
        let wire = Arc::new(Mutex::new(Wire::default()));
        let mut t = open_transport(&wire);
        wire.lock().unwrap().incoming.push_back(vec![9, 8]);

        let mut buf = [0u8; 5];
        let started = Instant::now();
        assert_eq!(t.read(&mut buf, Duration::from_millis(60)).unwrap(), 2);
        assert!(started.elapsed() >= Duration::from_millis(55));
    }

    #[test]
    fn a_read_of_nothing_returns_at_once() {
        let wire = Arc::new(Mutex::new(Wire::default()));
        let mut t = open_transport(&wire);
        assert_eq!(t.read(&mut [], Duration::from_secs(5)).unwrap(), 0);
    }

    #[test]
    fn an_empty_write_sends_nothing_and_a_real_one_goes_whole() {
        let wire = Arc::new(Mutex::new(Wire::default()));
        let mut t = open_transport(&wire);

        t.write(&[]).unwrap();
        t.write(&[0xAA, 0x55]).unwrap();

        assert_eq!(wire.lock().unwrap().written, vec![vec![0xAA, 0x55]]);
    }

    #[test]
    fn discarding_purges_the_link_and_closing_is_quiet() {
        let wire = Arc::new(Mutex::new(Wire::default()));
        let mut t = open_transport(&wire);
        wire.lock().unwrap().incoming.push_back(vec![1]);

        t.discard_input();
        assert_eq!(wire.lock().unwrap().purges, 1);
        assert!(wire.lock().unwrap().incoming.is_empty());

        t.close();
        t.close();
        assert!(!t.is_open());
    }

    #[test]
    fn the_write_timeout_starts_at_half_a_second_and_ignores_nonsense() {
        let mut t = FtdiEcuTransport::new("", 115_200).unwrap();
        assert_eq!(t.write_timeout(), Duration::from_millis(500));

        t.set_write_timeout(Duration::ZERO);
        assert_eq!(t.write_timeout(), Duration::from_millis(500));

        t.set_write_timeout(Duration::from_secs(20));
        assert_eq!(t.write_timeout(), Duration::from_secs(20));
    }

    // ----- FTDI's requests, byte for byte ---------------------------------------------------

    #[test]
    fn a_setup_packet_is_the_eight_bytes_usb_sends() {
        // bmRequestType 0x40 (vendor, device, host to device), bRequest, wValue and wIndex
        // little-endian, wLength 0.
        let r = ControlRequest {
            request: 0x03,
            value: 0x4138,
            index: 0x0102,
        };
        assert_eq!(r.setup_packet(), [0x40, 0x03, 0x38, 0x41, 0x02, 0x01, 0x00, 0x00]);
    }

    #[test]
    fn reset_and_purge_are_request_zero_with_the_value_choosing_which() {
        assert_eq!(
            reset(1).setup_packet(),
            [0x40, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]
        );
        assert_eq!(
            purge_rx(1).setup_packet(),
            [0x40, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00]
        );
        assert_eq!(
            purge_tx(1).setup_packet(),
            [0x40, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00]
        );
    }

    #[test]
    fn the_latency_timer_is_request_nine_with_the_milliseconds_as_the_value() {
        // The C# set 2 ms rather than the default 16.
        assert_eq!(
            latency_timer(LATENCY_TIMER_MS, 1).setup_packet(),
            [0x40, 0x09, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00]
        );
        assert_eq!(latency_timer(16, 1).value, 16);
    }

    #[test]
    fn flow_control_off_is_request_two_with_no_mode() {
        assert_eq!(
            flow_control_none(1).setup_packet(),
            [0x40, 0x02, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00]
        );
    }

    #[test]
    fn eight_n_one_is_a_value_of_eight() {
        // FT_SetDataCharacteristics(handle, 8, 0, 0): eight bits, one stop bit, no parity.
        let r = line_properties(8, StopBits::One, Parity::None, 1);
        assert_eq!(r.setup_packet(), [0x40, 0x04, 0x08, 0x00, 0x01, 0x00, 0x00, 0x00]);
    }

    #[test]
    fn parity_and_stop_bits_sit_above_the_word_length() {
        // 7 data bits, even parity (2 << 8), two stop bits (2 << 11).
        assert_eq!(line_properties(7, StopBits::Two, Parity::Even, 1).value, 0x1207);
        assert_eq!(line_properties(8, StopBits::OnePointFive, Parity::Odd, 1).value, 0x0908);
        assert_eq!(line_properties(8, StopBits::One, Parity::Space, 1).value, 0x0408);
    }

    #[test]
    fn the_ft_x_divisor_for_115200_is_0x001a() {
        // 48 MHz * 16 / 16 / 115200 = 416 -> 208 -> 26 whole, fraction 0.
        let (r, actual) = baud_rate(115_200, ChipType::FtX, 1).unwrap();
        assert_eq!((r.request, r.value, r.index), (0x03, 0x001A, 0x0000));
        assert_eq!(r.setup_packet(), [0x40, 0x03, 0x1A, 0x00, 0x00, 0x00, 0x00, 0x00]);
        // 48e6 / 208 = 230769, halved with rounding: within 0.4% of what was asked for.
        assert!((115_000..=116_000).contains(&actual), "{actual}");
    }

    #[test]
    fn the_divisor_for_9600_is_the_well_known_0x4138() {
        let (r, _) = baud_rate(9_600, ChipType::Ft232R, 1).unwrap();
        assert_eq!((r.value, r.index), (0x4138, 0x0000));
    }

    #[test]
    fn the_top_rates_use_the_special_divisor_codes() {
        assert_eq!(baud_rate(3_000_000, ChipType::FtX, 1).unwrap().0.value, 0);
        assert_eq!(baud_rate(2_000_000, ChipType::FtX, 1).unwrap().0.value, 1);
        assert_eq!(baud_rate(1_500_000, ChipType::FtX, 1).unwrap().0.value, 2);
    }

    #[test]
    fn a_rate_the_divisor_cannot_make_is_refused() {
        assert!(baud_rate(0, ChipType::FtX, 1).is_none());
        // Far above the chip's 3 Mbaud ceiling: the nearest is 3,000,000, more than 5% away.
        assert!(baud_rate(10_000_000, ChipType::FtX, 1).is_none());
    }

    #[test]
    fn the_high_speed_chips_use_a_120_mhz_clock_and_a_different_index() {
        // 12 Mbaud is divisor zero with the CLK/10 switch (0x20000) on; its high bits land in
        // the index's upper byte, with the interface in the low one.
        let (r, actual) = baud_rate(12_000_000, ChipType::Ft232H, 1).unwrap();
        assert_eq!((r.value, r.index, actual), (0x0000, 0x0201, 12_000_000));

        // 9600 on an H chip also uses the fast clock: 120 MHz * 16 / 10 / 9600 = 20000, halved
        // to 10000, whose whole part is 1250 and whose fraction is zero.
        let (r, _) = baud_rate(9_600, ChipType::Ft2232H, 1).unwrap();
        assert_eq!((r.value, r.index), (1250, 0x0201));

        // Only a rate under about 732 baud is slow enough to need the 48 MHz clock, and its
        // index is laid out the H way (high bits shifted, interface in the low byte).
        let (r, _) = baud_rate(600, ChipType::Ft2232H, 1).unwrap();
        assert_eq!((r.value, r.index), (5000, 0x0001));
    }

    #[test]
    fn the_chip_is_read_from_the_device_version() {
        assert_eq!(ChipType::from_device_version(0x1000), ChipType::FtX);
        assert_eq!(ChipType::from_device_version(0x0600), ChipType::Ft232R);
        assert_eq!(ChipType::from_device_version(0x0900), ChipType::Ft232H);
        assert_eq!(ChipType::from_device_version(0x0000), ChipType::Other);
    }

    #[test]
    fn every_in_packet_loses_its_two_status_bytes() {
        // Modem status 0x31 (CTS, plus FTDI's fixed bits), line status 0x60 (THRE | TEMT).
        let mut out = Vec::new();
        strip_status(&[0x31, 0x60, b'O', b'K'], 64, &mut out);
        assert_eq!(out, b"OK");

        // A packet that is only status carries nothing.
        out.clear();
        strip_status(&[0x31, 0x60], 64, &mut out);
        assert!(out.is_empty());
    }

    #[test]
    fn a_transfer_of_several_packets_loses_the_status_of_each() {
        // Two full 8-byte packets then a short one, each opened by a status pair.
        let mut raw = vec![0x31, 0x60, 1, 2, 3, 4, 5, 6];
        raw.extend([0x31, 0x60, 7, 8, 9, 10, 11, 12]);
        raw.extend([0x31, 0x60, 13]);

        let mut out = Vec::new();
        strip_status(&raw, 8, &mut out);
        assert_eq!(out, (1..=13).collect::<Vec<u8>>());
    }

    #[test]
    fn stripping_appends_and_tolerates_an_empty_transfer() {
        let mut out = vec![0xEE];
        strip_status(&[], 64, &mut out);
        strip_status(&[0x31], 64, &mut out);
        assert_eq!(out, [0xEE]);
    }

    // ----- finding the virtual COM port (Windows' route) ------------------------------------

    fn vcp(port: &str, vid: u16, pid: u16, serial: Option<&str>) -> PortInfo {
        PortInfo {
            port_name: port.into(),
            port_type: PortType::Usb(crate::transport::serial_port::UsbPortInfo {
                vid,
                pid,
                serial_number: serial.map(String::from),
                manufacturer: None,
                product: None,
            }),
        }
    }

    fn device_with(serial: &str, pid: u16) -> FtdiDevice {
        let mut d = test_device(0, serial, "MaxxECU", false);
        d.product_id = pid;
        d
    }

    #[test]
    fn the_virtual_com_port_is_found_by_ftdi_ids_and_serial() {
        let ports = [
            vcp("COM3", 0x2341, 0x0042, Some("95730333837351C01221")),
            vcp("COM7", 0x0403, 0x9728, Some("MX000000")),
            vcp("COM8", 0x0403, 0x6001, Some("A50285BI")),
        ];
        assert_eq!(
            find_vcp_port(&device_with("MX000000", 0x9728), &ports).as_deref(),
            Some("COM7")
        );
        assert_eq!(
            find_vcp_port(&device_with("A50285BI", 0x6001), &ports).as_deref(),
            Some("COM8")
        );
    }

    #[test]
    fn a_serial_with_the_port_letter_appended_still_matches() {
        // FTDI's Windows instance ids append the channel letter to the EEPROM serial.
        let ports = [vcp("COM8", 0x0403, 0x6001, Some("A50285BIA"))];
        assert_eq!(
            find_vcp_port(&device_with("A50285BI", 0x6001), &ports).as_deref(),
            Some("COM8")
        );
        assert_eq!(find_vcp_port(&device_with("A50285XX", 0x6001), &ports), None);
    }

    #[test]
    fn with_no_serial_on_either_side_the_ids_alone_decide() {
        let ports = [vcp("COM9", 0x0403, 0x9728, None)];
        assert_eq!(
            find_vcp_port(&device_with("MX000000", 0x9728), &ports).as_deref(),
            Some("COM9")
        );
        assert_eq!(find_vcp_port(&device_with("", 0x9728), &ports).as_deref(), Some("COM9"));
        assert_eq!(find_vcp_port(&device_with("", 0x6001), &ports), None);
    }

    #[test]
    fn no_virtual_com_port_says_the_vcp_driver_option_is_needed() {
        let Error::Io(e) = vcp_missing(&maxx()) else {
            panic!("expected an I/O error")
        };
        let text = e.to_string();
        assert!(text.contains("MaxxECU (MX000000) is plugged in"));
        assert!(text.contains("FTDI VCP driver option is needed"));
        assert!(text.contains("Load VCP"));
    }
}
