//! A MaxxECU's USB port on Windows through FTDI's D2XX library, loaded at run time.
//!
//! Ported call for call from `FtdiEcuTransport.cs`, which is the only route proven on a
//! MaxxECU Race: the stock MaxxECU driver creates no COM port, and FTDI's driver owns the chip.
//!
//! `ftd2xx.dll` is **loaded at run time with `libloading`, never linked at build time and never
//! shipped**. It arrives with the MaxxECU driver and with MTune. A machine without it simply
//! fails to load it, and the caller falls back to the virtual COM port and then to a clear error.
//!
//! The functions D2XX is asked for, in the order the C# calls them:
//!
//! * Listing: `FT_CreateDeviceInfoList`, then `FT_GetDeviceInfoDetail` per index (flags bit 0 is
//!   "open"; the serial is 16 bytes and the description 64, both NUL-terminated ASCII).
//! * Opening: `FT_Open(index)`, then `FT_SetBaudRate`, `FT_SetDataCharacteristics(8, 0, 0)`,
//!   `FT_SetFlowControl(0, 0, 0)`, `FT_SetTimeouts(50, write)`, each checked (a failure closes the
//!   handle and says what was refused), then `FT_SetLatencyTimer(2)` unchecked, then
//!   `FT_Purge(3)`.
//! * Writing: `FT_Write`; a short write is an error, not a slow one.
//! * Reading: `FT_GetQueueStatus`, a 1 ms sleep while it is zero, then `FT_Read` for what is
//!   waiting, up to the buffer.
//! * Closing: `FT_Close`.
//!
//! # Differences the bench must confirm
//!
//! * The C# let the loader find `ftd2xx.dll` by name, which also searches the current directory
//!   and `PATH`. Here the load is restricted to the application directory, System32 and the
//!   directories added by the user (`LOAD_LIBRARY_SEARCH_DEFAULT_DIRS`), so a DLL dropped into a
//!   working directory is not run. A `ftd2xx.dll` that lives only in MTune's folder or on `PATH`
//!   is not found; the driver package's copy in System32 is.
//! * Bitness: the C# names `ftd2xx.dll` and treats a wrong-bitness file (`BadImageFormat`) as a
//!   missing driver. So does this: a 32-bit DLL in a 64-bit process fails to load and gives the
//!   same message. There is no `ftd2xx64.dll` lookup.
//! * The C# opened by the index in D2XX's own list. The index here is looked up again by serial
//!   (or description when there is no serial) in D2XX's list just before `FT_Open`, because the
//!   list the device was picked from may have come from USB, which orders devices differently.

use std::io;
use std::sync::{Arc, OnceLock};
use std::thread;
use std::time::{Duration, Instant};

use olv_core::{Error, Result};

use super::{FtdiBackend, FtdiDevice, FtdiLink, ensure_maxx_ecu, vcp_missing_text};

/// D2XX's `FT_STATUS`. Zero is `FT_OK`.
pub(crate) type Status = u32;

/// What D2XX calls "device not opened": on a device that is plainly there, almost always
/// another program holding it.
const FT_DEVICE_NOT_OPENED: Status = 3;

/// `FT_Purge` mask: `FT_PURGE_RX | FT_PURGE_TX`.
const PURGE_BOTH: u32 = 3;

/// How long a single call into the driver may sit in a read: left short, with the caller's own
/// deadline kept here.
const READ_TIMEOUT_MS: u32 = 50;

/// The device's own latency timer, in milliseconds (the default is sixteen): dead time on the
/// front of every reply.
const LATENCY_TIMER_MS: u8 = 2;

/// An `FT_HANDLE`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct Handle(pub usize);

/// What `FT_GetDeviceInfoDetail` returns for one device.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct InfoDetail {
    pub flags: u32,
    /// USB id: vendor in the high word, product in the low.
    pub id: u32,
    pub serial: [u8; 16],
    pub description: [u8; 64],
}

/// FTDI's D2XX entry points, as much of them as a byte stream needs, behind a safe interface so
/// the logic above is testable with a fake. Every method returns D2XX's status or the value.
pub(crate) trait D2xxApi: Send + Sync {
    fn create_device_info_list(&self) -> std::result::Result<u32, Status>;
    fn get_device_info_detail(&self, index: u32) -> std::result::Result<InfoDetail, Status>;
    fn open(&self, index: i32) -> std::result::Result<Handle, Status>;
    fn close(&self, handle: Handle) -> Status;
    fn set_baud_rate(&self, handle: Handle, baud_rate: u32) -> Status;
    fn set_data_characteristics(&self, handle: Handle, bits: u8, stop_bits: u8, parity: u8) -> Status;
    fn set_flow_control(&self, handle: Handle, flow: u16, xon: u8, xoff: u8) -> Status;
    fn set_timeouts(&self, handle: Handle, read: u32, write: u32) -> Status;
    fn set_latency_timer(&self, handle: Handle, milliseconds: u8) -> Status;
    fn purge(&self, handle: Handle, mask: u32) -> Status;
    fn write(&self, handle: Handle, data: &[u8]) -> std::result::Result<u32, Status>;
    fn read(&self, handle: Handle, buf: &mut [u8]) -> std::result::Result<u32, Status>;
    fn get_queue_status(&self, handle: Handle) -> std::result::Result<u32, Status>;
}

/// Opens a MaxxECU's virtual COM port, or says there is none (`None`).
pub(crate) type VcpOutcome = Result<Option<Box<dyn FtdiLink>>>;

pub(crate) type VcpOpener = dyn Fn(&FtdiDevice, u32) -> VcpOutcome + Send + Sync;

/// Finds and loads the library: `Err` is why it could not be (the file is absent, or is for the
/// other bitness, or lacks an entry point). Replaced by a fake in the tests.
pub(crate) type Loader = Arc<dyn Fn() -> std::result::Result<Arc<dyn D2xxApi>, String> + Send + Sync>;

// ----- pure: device listing -----------------------------------------------------------------

/// A NUL-terminated ASCII field as text (`Text` in the C#).
pub(crate) fn text(raw: &[u8]) -> String {
    let end = raw.iter().position(|&b| b == 0).unwrap_or(raw.len());
    raw[..end].iter().map(|&b| char::from(b)).collect()
}

/// One listed device from what D2XX said about it.
pub(crate) fn device_from_info(index: u32, info: &InfoDetail) -> FtdiDevice {
    let mut device = FtdiDevice::new(
        index as usize,
        &text(&info.serial),
        &text(&info.description),
        info.flags & 1 != 0,
    );
    device.product_id = (info.id & 0xFFFF) as u16;
    device
}

/// Every FTDI device D2XX lists. A failing list call, or none, is no devices; a device whose
/// detail cannot be read is skipped.
pub(crate) fn list_devices(api: &dyn D2xxApi) -> Vec<FtdiDevice> {
    let Ok(count) = api.create_device_info_list() else {
        return Vec::new();
    };

    (0..count)
        .filter_map(|i| {
            api.get_device_info_detail(i)
                .ok()
                .map(|info| device_from_info(i, &info))
        })
        .collect()
}

/// The index in D2XX's own list of the MaxxECU `wanted`: by serial where there is one, else by
/// description. Only a listed device that is itself a MaxxECU can match, so a mismatch between
/// two lists can never land on a different FTDI device.
pub(crate) fn resolve_index(listed: &[FtdiDevice], wanted: &FtdiDevice) -> Option<usize> {
    listed
        .iter()
        .filter(|d| d.is_maxx_ecu())
        .find(|d| {
            if wanted.serial.is_empty() {
                d.serial.is_empty() && d.description.eq_ignore_ascii_case(&wanted.description)
            } else {
                d.serial.eq_ignore_ascii_case(&wanted.serial)
            }
        })
        .map(|d| d.index)
}

// ----- pure: the line setup -----------------------------------------------------------------

/// One call of the open sequence, with its arguments.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Setup {
    BaudRate(u32),
    /// Word length, stop bits (0 = one), parity (0 = none): 8-N-1.
    DataCharacteristics {
        bits: u8,
        stop_bits: u8,
        parity: u8,
    },
    /// 0 = none.
    FlowControl {
        flow: u16,
        xon: u8,
        xoff: u8,
    },
    Timeouts {
        read: u32,
        write: u32,
    },
}

impl Setup {
    /// What to say was refused, as the C# worded it.
    pub(crate) fn what(self) -> &'static str {
        match self {
            Self::BaudRate(_) => "set the baud rate",
            Self::DataCharacteristics { .. } => "set 8-N-1",
            Self::FlowControl { .. } => "turn flow control off",
            Self::Timeouts { .. } => "set the timeouts",
        }
    }

    fn apply(self, api: &dyn D2xxApi, handle: Handle) -> Status {
        match self {
            Self::BaudRate(rate) => api.set_baud_rate(handle, rate),
            Self::DataCharacteristics {
                bits,
                stop_bits,
                parity,
            } => api.set_data_characteristics(handle, bits, stop_bits, parity),
            Self::FlowControl { flow, xon, xoff } => api.set_flow_control(handle, flow, xon, xoff),
            Self::Timeouts { read, write } => api.set_timeouts(handle, read, write),
        }
    }
}

/// Whole milliseconds of a timeout, as `(uint)TimeSpan.TotalMilliseconds`.
pub(crate) fn millis(timeout: Duration) -> u32 {
    u32::try_from(timeout.as_millis()).unwrap_or(u32::MAX)
}

/// The checked calls after `FT_Open`, in the C#'s order. They are the chip's UART settings, not
/// USB: the FTDI is a serial port in front of the controller.
pub(crate) fn setup_sequence(baud_rate: u32, write_timeout: Duration) -> [Setup; 4] {
    [
        Setup::BaudRate(baud_rate),
        Setup::DataCharacteristics {
            bits: 8,
            stop_bits: 0,
            parity: 0,
        },
        Setup::FlowControl {
            flow: 0,
            xon: 0,
            xoff: 0,
        },
        Setup::Timeouts {
            read: READ_TIMEOUT_MS,
            write: millis(write_timeout),
        },
    ]
}

// ----- error texts, as the C# worded them ---------------------------------------------------

pub(crate) fn already_open(device: &FtdiDevice) -> String {
    format!(
        "{} is already open in another program. MTune holds the USB link for as long as it is \
         connected to the ECU, so disconnect it there first.",
        device.label()
    )
}

pub(crate) fn would_not_open(device: &FtdiDevice, status: Status) -> String {
    format!("{} would not open (FTDI status {status}).", device.label())
}

/// The message for a library that would not load (`MissingDriver` in the C#).
pub(crate) fn missing_driver(detail: &str) -> String {
    format!(
        "ftd2xx.dll could not be loaded, so a MaxxECU's USB port cannot be reached. It is \
         installed by MTune and by the MaxxECU USB driver package. ({detail})"
    )
}

/// What `Find()` says when there is no FTDI device listed and the library is not there.
pub(crate) const DRIVER_NOT_INSTALLED: &str = "The FTDI driver is not installed, so a MaxxECU's USB port cannot be \
     reached. It arrives with MTune and with the driver package MaxxECU publishes.";

fn io_error(kind: io::ErrorKind, message: String) -> Error {
    Error::Io(io::Error::new(kind, message))
}

// ----- the link -----------------------------------------------------------------------------

/// An open D2XX handle as a byte stream.
pub(crate) struct D2xxLink {
    api: Arc<dyn D2xxApi>,
    handle: Handle,
    /// The write timeout the driver has now, so that a write does not set it again.
    write_timeout_ms: u32,
}

impl D2xxLink {
    /// Opens `device` (a MaxxECU, found again in D2XX's own list) at `baud_rate`, configured
    /// and purged. On any failure the handle is closed.
    pub(crate) fn open(
        api: Arc<dyn D2xxApi>,
        device: &FtdiDevice,
        baud_rate: u32,
        write_timeout: Duration,
    ) -> Result<Self> {
        ensure_maxx_ecu(device)?;

        let listed = list_devices(api.as_ref());
        let index = resolve_index(&listed, device).ok_or_else(|| {
            io_error(
                io::ErrorKind::NotFound,
                format!("{} is no longer plugged in.", device.label()),
            )
        })?;

        // The C# opened by index (`FT_Open`), so this does too, but verifies that index again
        // immediately before: it must still hold this MaxxECU, by serial, or nothing is opened.
        let still_there = api
            .get_device_info_detail(index as u32)
            .ok()
            .map(|info| device_from_info(index as u32, &info));
        if still_there.is_none_or(|d| resolve_index(&[d], device) != Some(index)) {
            return Err(io_error(
                io::ErrorKind::NotFound,
                format!("{} is no longer plugged in.", device.label()),
            ));
        }

        let handle = match api.open(index as i32) {
            Ok(handle) => handle,
            Err(FT_DEVICE_NOT_OPENED) => return Err(io_error(io::ErrorKind::ResourceBusy, already_open(device))),
            Err(status) => return Err(io_error(io::ErrorKind::Other, would_not_open(device, status))),
        };

        // From here the link owns the handle, so a refusal below closes it when the link drops.
        let write_timeout_ms = millis(write_timeout);
        let link = Self {
            api,
            handle,
            write_timeout_ms,
        };

        for step in setup_sequence(baud_rate, write_timeout) {
            let status = step.apply(link.api.as_ref(), handle);
            if status != 0 {
                return Err(io_error(
                    io::ErrorKind::Other,
                    format!(
                        "The MaxxECU's USB device refused to {} (FTDI status {status}).",
                        step.what()
                    ),
                ));
            }
        }

        // Two milliseconds rather than the default sixteen. Not checked, as in the C#.
        link.api.set_latency_timer(handle, LATENCY_TIMER_MS);

        // Hygiene; failing is not worth reporting.
        link.api.purge(handle, PURGE_BOTH);
        Ok(link)
    }
}

impl Drop for D2xxLink {
    fn drop(&mut self) {
        // A device that has gone cannot be closed politely; the status is not interesting.
        self.api.close(self.handle);
    }
}

impl FtdiLink for D2xxLink {
    fn write(&mut self, data: &[u8], timeout: Duration) -> Result<()> {
        // The C#'s `WriteTimeout` setter re-applies the timeouts when it changes.
        let wanted = millis(timeout);
        if wanted != self.write_timeout_ms {
            self.api.set_timeouts(self.handle, READ_TIMEOUT_MS, wanted);
            self.write_timeout_ms = wanted;
        }

        let written = self.api.write(self.handle, data).map_err(|status| {
            io_error(
                io::ErrorKind::Other,
                format!("The MaxxECU's USB device refused a write (FTDI status {status})."),
            )
        })?;

        // A short write is not a slow one. The driver takes the whole buffer or the device has
        // gone, and carrying on with half a frame sent puts the controller's parser out of step
        // with no way back.
        if written as usize != data.len() {
            return Err(io_error(
                io::ErrorKind::WriteZero,
                format!(
                    "Only {written} of {} bytes reached the MaxxECU's USB device.",
                    data.len()
                ),
            ));
        }
        Ok(())
    }

    fn read_some(&mut self, buf: &mut [u8], timeout: Duration) -> Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        let deadline = Instant::now() + timeout;

        loop {
            let waiting = self.api.get_queue_status(self.handle).map_err(|status| {
                io_error(
                    io::ErrorKind::Other,
                    format!("The MaxxECU's USB device stopped answering (FTDI status {status})."),
                )
            })?;

            if waiting > 0 {
                let wanted = (waiting as usize).min(buf.len());
                let read = self.api.read(self.handle, &mut buf[..wanted]).map_err(|status| {
                    io_error(
                        io::ErrorKind::Other,
                        format!("The MaxxECU's USB device failed a read (FTDI status {status})."),
                    )
                })?;
                return Ok((read as usize).min(wanted));
            }

            // Nothing yet. A short sleep rather than a spin; shorter than Windows' timer tick
            // is not worth asking for.
            if Instant::now() >= deadline {
                return Ok(0);
            }
            thread::sleep(Duration::from_millis(1));
        }
    }

    fn purge(&mut self) -> Result<()> {
        // The status is ignored, as in the C#.
        self.api.purge(self.handle, PURGE_BOTH);
        Ok(())
    }
}

/// Returns the library in `cell` if it was loaded before, else loads it with `load` and keeps it.
pub(crate) fn cached_load(
    cell: &OnceLock<Arc<dyn D2xxApi>>,
    load: &dyn Fn() -> std::result::Result<Arc<dyn D2xxApi>, String>,
) -> std::result::Result<Arc<dyn D2xxApi>, String> {
    if let Some(api) = cell.get() {
        return Ok(api.clone());
    }
    let api = load()?;
    Ok(cell.get_or_init(|| api).clone())
}

// ----- the Windows route: D2XX, else the virtual COM port, else a clear error ---------------

/// Opens a MaxxECU on Windows.
///
/// 1. Never anything that is not positively a MaxxECU, on either route.
/// 2. D2XX, when the library loads. A failure after that is reported as it is; the virtual COM
///    port is not tried behind a D2XX error, which would only hide it.
/// 3. Otherwise the virtual COM port, if the FTDI driver has made one (`vcp` returns `None`
///    when there is none).
/// 4. Otherwise the error that says the library is missing and what would make a route.
pub(crate) fn windows_open(
    device: &FtdiDevice,
    baud_rate: u32,
    write_timeout: Duration,
    load: &dyn Fn() -> std::result::Result<Arc<dyn D2xxApi>, String>,
    vcp: &dyn Fn(&FtdiDevice, u32) -> VcpOutcome,
) -> Result<Box<dyn FtdiLink>> {
    ensure_maxx_ecu(device)?;

    let load_error = match load() {
        Ok(api) => return Ok(Box::new(D2xxLink::open(api, device, baud_rate, write_timeout)?)),
        Err(e) => e,
    };

    match vcp(device, baud_rate)? {
        Some(link) => Ok(link),
        None => Err(io_error(
            io::ErrorKind::NotFound,
            format!("{} {}", missing_driver(&load_error), vcp_missing_text(device)),
        )),
    }
}

/// The Windows backend: devices from D2XX when it loads (so the index is D2XX's), else from USB.
pub(crate) struct WindowsBackend {
    pub(crate) loader: Loader,
    pub(crate) vcp: Arc<VcpOpener>,
    pub(crate) usb_devices: Arc<dyn Fn() -> Vec<FtdiDevice> + Send + Sync>,
}

impl WindowsBackend {
    /// The real machine: `ftd2xx.dll`, the system's serial ports, and USB listing.
    #[cfg(windows)]
    pub(crate) fn system() -> Self {
        /// The library, once loaded: a process loads it once, as the C#'s `DllImport` did, not on
        /// every listing and open. A failure is not remembered, so installing the driver while the
        /// application runs is noticed.
        static LOADED: OnceLock<Arc<dyn D2xxApi>> = OnceLock::new();

        Self {
            loader: Arc::new(|| cached_load(&LOADED, &dll::load)),
            vcp: Arc::new(|device, baud_rate| {
                let ports = super::list_system_ports();
                if super::find_vcp_port(device, &ports).is_none() {
                    return Ok(None);
                }
                Ok(Some(Box::new(super::vcp_link::VcpLink::open(
                    device, &ports, baud_rate,
                )?)))
            }),
            usb_devices: Arc::new(super::system_devices),
        }
    }
}

impl FtdiBackend for WindowsBackend {
    fn devices(&self) -> Vec<FtdiDevice> {
        if let Ok(api) = (self.loader)() {
            let listed = list_devices(api.as_ref());
            if !listed.is_empty() {
                return listed;
            }
        }
        (self.usb_devices)()
    }

    fn open(&self, device: &FtdiDevice, baud_rate: u32, write_timeout: Duration) -> Result<Box<dyn FtdiLink>> {
        windows_open(device, baud_rate, write_timeout, &*self.loader, &*self.vcp)
    }

    /// With the library absent and nothing listed, the C# said the driver was not installed.
    fn unavailable_reason(&self) -> Option<String> {
        (self.loader)().err().map(|_| DRIVER_NOT_INSTALLED.to_string())
    }
}

// ----- the real library --------------------------------------------------------------------

#[cfg(windows)]
mod dll {
    use std::ffi::c_void;
    use std::ptr::{from_mut, null_mut};
    use std::sync::Arc;

    use libloading::os::windows::{LOAD_LIBRARY_SEARCH_DEFAULT_DIRS, Library};

    use super::{D2xxApi, Handle, InfoDetail, Status};

    type Raw = *mut c_void;

    type CreateDeviceInfoList = unsafe extern "system" fn(count: *mut u32) -> Status;
    type GetDeviceInfoDetail = unsafe extern "system" fn(
        index: u32,
        flags: *mut u32,
        kind: *mut u32,
        id: *mut u32,
        location: *mut u32,
        serial: *mut u8,
        description: *mut u8,
        handle: *mut Raw,
    ) -> Status;
    type Open = unsafe extern "system" fn(index: i32, handle: *mut Raw) -> Status;
    type Close = unsafe extern "system" fn(handle: Raw) -> Status;
    type SetBaudRate = unsafe extern "system" fn(handle: Raw, baud_rate: u32) -> Status;
    type SetDataCharacteristics = unsafe extern "system" fn(handle: Raw, bits: u8, stop_bits: u8, parity: u8) -> Status;
    type SetFlowControl = unsafe extern "system" fn(handle: Raw, flow: u16, xon: u8, xoff: u8) -> Status;
    type SetTimeouts = unsafe extern "system" fn(handle: Raw, read: u32, write: u32) -> Status;
    type SetLatencyTimer = unsafe extern "system" fn(handle: Raw, milliseconds: u8) -> Status;
    type Purge = unsafe extern "system" fn(handle: Raw, mask: u32) -> Status;
    type Transfer = unsafe extern "system" fn(handle: Raw, buffer: *mut u8, count: u32, done: *mut u32) -> Status;
    type GetQueueStatus = unsafe extern "system" fn(handle: Raw, waiting: *mut u32) -> Status;

    /// The resolved entry points. The library stays loaded for as long as this lives, which is
    /// what keeps every pointer here valid.
    struct Ft2xx {
        create_device_info_list: CreateDeviceInfoList,
        get_device_info_detail: GetDeviceInfoDetail,
        open: Open,
        close: Close,
        set_baud_rate: SetBaudRate,
        set_data_characteristics: SetDataCharacteristics,
        set_flow_control: SetFlowControl,
        set_timeouts: SetTimeouts,
        set_latency_timer: SetLatencyTimer,
        purge: Purge,
        write: Transfer,
        read: Transfer,
        get_queue_status: GetQueueStatus,
        _library: Library,
    }

    /// Resolves one symbol to its raw function pointer.
    macro_rules! symbol {
        ($library:expr, $name:literal, $ty:ty) => {{
            // SAFETY: the name is a NUL-terminated D2XX export, and `$ty` is that export's
            // documented `__stdcall` signature (FTD2XX.h). The pointer is copied out of the
            // `Symbol`, and the library is kept alive in the same struct.
            #[allow(unsafe_code)]
            let symbol = unsafe { $library.get::<$ty>(concat!($name, "\0").as_bytes()) }
                .map_err(|e| format!("{} is missing {} ({e})", super::LIBRARY, $name))?;
            *symbol
        }};
    }

    /// Loads `ftd2xx.dll`. `Err` says why not, for the message the user sees.
    pub(super) fn load() -> Result<Arc<dyn D2xxApi>, String> {
        // SAFETY: loading a DLL runs its `DllMain`. This is FTDI's own library, which the
        // C# loaded in the same way. The search is limited to the application directory,
        // System32 and user-added directories, so nothing in the working directory is run.
        #[allow(unsafe_code)]
        let library = unsafe { Library::load_with_flags(super::LIBRARY, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) }
            .map_err(|e| format!("{}: {e}", super::LIBRARY))?;

        Ok(Arc::new(Ft2xx {
            create_device_info_list: symbol!(library, "FT_CreateDeviceInfoList", CreateDeviceInfoList),
            get_device_info_detail: symbol!(library, "FT_GetDeviceInfoDetail", GetDeviceInfoDetail),
            open: symbol!(library, "FT_Open", Open),
            close: symbol!(library, "FT_Close", Close),
            set_baud_rate: symbol!(library, "FT_SetBaudRate", SetBaudRate),
            set_data_characteristics: symbol!(library, "FT_SetDataCharacteristics", SetDataCharacteristics),
            set_flow_control: symbol!(library, "FT_SetFlowControl", SetFlowControl),
            set_timeouts: symbol!(library, "FT_SetTimeouts", SetTimeouts),
            set_latency_timer: symbol!(library, "FT_SetLatencyTimer", SetLatencyTimer),
            purge: symbol!(library, "FT_Purge", Purge),
            write: symbol!(library, "FT_Write", Transfer),
            read: symbol!(library, "FT_Read", Transfer),
            get_queue_status: symbol!(library, "FT_GetQueueStatus", GetQueueStatus),
            _library: library,
        }))
    }

    fn raw(handle: Handle) -> Raw {
        handle.0 as Raw
    }

    fn result<T>(status: Status, value: T) -> Result<T, Status> {
        if status == 0 { Ok(value) } else { Err(status) }
    }

    impl D2xxApi for Ft2xx {
        #[allow(unsafe_code)]
        fn create_device_info_list(&self) -> Result<u32, Status> {
            let mut count = 0u32;
            // SAFETY: `count` is a valid `u32` out-parameter for the call; the entry point is
            // kept valid by `_library`.
            let status = unsafe { (self.create_device_info_list)(&mut count) };
            result(status, count)
        }

        #[allow(unsafe_code)]
        fn get_device_info_detail(&self, index: u32) -> Result<InfoDetail, Status> {
            let (mut flags, mut kind, mut id, mut location) = (0u32, 0u32, 0u32, 0u32);
            let mut serial = [0u8; 16];
            let mut description = [0u8; 64];
            let mut handle: Raw = null_mut();
            // SAFETY: every out-parameter is a valid local of the size D2XX writes (the serial
            // is at most 16 bytes including its NUL and the description 64, as FTD2XX.h
            // documents); the entry point is kept valid by `_library`.
            let status = unsafe {
                (self.get_device_info_detail)(
                    index,
                    &mut flags,
                    &mut kind,
                    &mut id,
                    &mut location,
                    serial.as_mut_ptr(),
                    description.as_mut_ptr(),
                    from_mut(&mut handle),
                )
            };
            result(
                status,
                InfoDetail {
                    flags,
                    id,
                    serial,
                    description,
                },
            )
        }

        #[allow(unsafe_code)]
        fn open(&self, index: i32) -> Result<Handle, Status> {
            let mut handle: Raw = null_mut();
            // SAFETY: `handle` is a valid out-parameter; D2XX fills it in on success only.
            let status = unsafe { (self.open)(index, from_mut(&mut handle)) };
            result(status, Handle(handle as usize))
        }

        #[allow(unsafe_code)]
        fn close(&self, handle: Handle) -> Status {
            // SAFETY: `handle` came from `FT_Open` and the owner closes it once.
            unsafe { (self.close)(raw(handle)) }
        }

        #[allow(unsafe_code)]
        fn set_baud_rate(&self, handle: Handle, baud_rate: u32) -> Status {
            // SAFETY: `handle` is an open D2XX handle; the arguments are plain values.
            unsafe { (self.set_baud_rate)(raw(handle), baud_rate) }
        }

        #[allow(unsafe_code)]
        fn set_data_characteristics(&self, handle: Handle, bits: u8, stop_bits: u8, parity: u8) -> Status {
            // SAFETY: as `set_baud_rate`.
            unsafe { (self.set_data_characteristics)(raw(handle), bits, stop_bits, parity) }
        }

        #[allow(unsafe_code)]
        fn set_flow_control(&self, handle: Handle, flow: u16, xon: u8, xoff: u8) -> Status {
            // SAFETY: as `set_baud_rate`.
            unsafe { (self.set_flow_control)(raw(handle), flow, xon, xoff) }
        }

        #[allow(unsafe_code)]
        fn set_timeouts(&self, handle: Handle, read: u32, write: u32) -> Status {
            // SAFETY: as `set_baud_rate`.
            unsafe { (self.set_timeouts)(raw(handle), read, write) }
        }

        #[allow(unsafe_code)]
        fn set_latency_timer(&self, handle: Handle, milliseconds: u8) -> Status {
            // SAFETY: as `set_baud_rate`.
            unsafe { (self.set_latency_timer)(raw(handle), milliseconds) }
        }

        #[allow(unsafe_code)]
        fn purge(&self, handle: Handle, mask: u32) -> Status {
            // SAFETY: as `set_baud_rate`.
            unsafe { (self.purge)(raw(handle), mask) }
        }

        #[allow(unsafe_code)]
        fn write(&self, handle: Handle, data: &[u8]) -> Result<u32, Status> {
            let mut written = 0u32;
            let count = u32::try_from(data.len()).unwrap_or(u32::MAX);
            // SAFETY: `data` is valid for reads of `count` bytes (`count <= data.len()`), and
            // D2XX only reads from the buffer for a write, so the const-to-mut cast is not
            // written through; `written` is a valid out-parameter.
            let status = unsafe { (self.write)(raw(handle), data.as_ptr().cast_mut(), count, &mut written) };
            result(status, written)
        }

        #[allow(unsafe_code)]
        fn read(&self, handle: Handle, buf: &mut [u8]) -> Result<u32, Status> {
            let mut read = 0u32;
            let count = u32::try_from(buf.len()).unwrap_or(u32::MAX);
            // SAFETY: `buf` is valid for writes of `count` bytes (`count <= buf.len()`) and
            // `read` is a valid out-parameter.
            let status = unsafe { (self.read)(raw(handle), buf.as_mut_ptr(), count, &mut read) };
            result(status, read)
        }

        #[allow(unsafe_code)]
        fn get_queue_status(&self, handle: Handle) -> Result<u32, Status> {
            let mut waiting = 0u32;
            // SAFETY: `waiting` is a valid out-parameter; `handle` is an open D2XX handle.
            let status = unsafe { (self.get_queue_status)(raw(handle), &mut waiting) };
            result(status, waiting)
        }
    }
}

/// The library's file name, resolved by the loader as the C# let it.
#[cfg_attr(not(windows), allow(dead_code))]
const LIBRARY: &str = "ftd2xx.dll";

#[cfg(test)]
mod tests {
    use super::*;

    /// A device as D2XX lists it: a MaxxECU has its product id, anything else an FT232R's.
    fn test_device(index: usize, serial: &str, description: &str, is_open: bool) -> FtdiDevice {
        let mut d = FtdiDevice::new(index, serial, description, is_open);
        let maxx =
            description.to_ascii_lowercase().contains("maxxecu") || serial.to_ascii_uppercase().starts_with("MX");
        d.product_id = if maxx {
            super::super::MAXX_ECU_PRODUCT_ID
        } else {
            0x6001
        };
        d
    }
    use std::collections::VecDeque;
    use std::sync::Mutex;

    #[derive(Debug, Clone, PartialEq, Eq)]
    enum Call {
        List,
        Detail(u32),
        Open(i32),
        Close,
        Baud(u32),
        Data(u8, u8, u8),
        Flow(u16, u8, u8),
        Timeouts(u32, u32),
        Latency(u8),
        Purge(u32),
        Write(usize),
        Read(usize),
        Queue,
    }

    #[derive(Default)]
    struct State {
        calls: Vec<Call>,
        devices: Vec<(u32, [u8; 16], [u8; 64])>,
        open_status: Status,
        /// Status to fail the Nth checked setup call with (0 = baud, 1 = data, 2 = flow, 3 = timeouts).
        fail_setup: Option<(usize, Status)>,
        setup_seen: usize,
        queue: VecDeque<u32>,
        incoming: VecDeque<u8>,
        written: Vec<u8>,
        accept: Option<u32>,
        queue_status: Status,
        detail_calls: usize,
        open_flags: u32,
        /// After this many detail calls, every device reads back as an FT232R.
        swap_after: Option<usize>,
    }

    #[derive(Default)]
    struct Fake(Mutex<State>);

    fn ascii<const N: usize>(s: &str) -> [u8; N] {
        let mut out = [0u8; N];
        out[..s.len()].copy_from_slice(s.as_bytes());
        out
    }

    impl Fake {
        fn with(devices: &[(&str, &str)]) -> Arc<Self> {
            let fake = Fake::default();
            fake.0.lock().unwrap().devices = devices
                .iter()
                .map(|(s, d)| (0x0403_9728, ascii::<16>(s), ascii::<64>(d)))
                .collect();
            Arc::new(fake)
        }

        fn calls(&self) -> Vec<Call> {
            self.0.lock().unwrap().calls.clone()
        }

        fn setup(&self, call: Call) -> Status {
            let mut s = self.0.lock().unwrap();
            s.calls.push(call);
            let n = s.setup_seen;
            s.setup_seen += 1;
            match s.fail_setup {
                Some((at, status)) if at == n => status,
                _ => 0,
            }
        }
    }

    impl D2xxApi for Fake {
        fn create_device_info_list(&self) -> std::result::Result<u32, Status> {
            let mut s = self.0.lock().unwrap();
            s.calls.push(Call::List);
            Ok(s.devices.len() as u32)
        }

        fn get_device_info_detail(&self, index: u32) -> std::result::Result<InfoDetail, Status> {
            let mut s = self.0.lock().unwrap();
            s.calls.push(Call::Detail(index));
            s.detail_calls += 1;
            if s.swap_after.is_some_and(|n| s.detail_calls > n) {
                return Ok(InfoDetail {
                    flags: 0,
                    id: 0x0403_6001,
                    serial: ascii("A50285BI"),
                    description: ascii("FT232R USB UART"),
                });
            }
            let (id, serial, description) = s.devices.get(index as usize).copied().ok_or(6u32)?;
            Ok(InfoDetail {
                flags: s.open_flags,
                id,
                serial,
                description,
            })
        }

        fn open(&self, index: i32) -> std::result::Result<Handle, Status> {
            let mut s = self.0.lock().unwrap();
            s.calls.push(Call::Open(index));
            if s.open_status != 0 {
                Err(s.open_status)
            } else {
                Ok(Handle(7))
            }
        }

        fn close(&self, _: Handle) -> Status {
            self.0.lock().unwrap().calls.push(Call::Close);
            0
        }

        fn set_baud_rate(&self, _: Handle, rate: u32) -> Status {
            self.setup(Call::Baud(rate))
        }

        fn set_data_characteristics(&self, _: Handle, b: u8, s: u8, p: u8) -> Status {
            self.setup(Call::Data(b, s, p))
        }

        fn set_flow_control(&self, _: Handle, f: u16, x: u8, y: u8) -> Status {
            self.setup(Call::Flow(f, x, y))
        }

        fn set_timeouts(&self, _: Handle, r: u32, w: u32) -> Status {
            self.setup(Call::Timeouts(r, w))
        }

        fn set_latency_timer(&self, _: Handle, ms: u8) -> Status {
            self.0.lock().unwrap().calls.push(Call::Latency(ms));
            0
        }

        fn purge(&self, _: Handle, mask: u32) -> Status {
            self.0.lock().unwrap().calls.push(Call::Purge(mask));
            0
        }

        fn write(&self, _: Handle, data: &[u8]) -> std::result::Result<u32, Status> {
            let mut s = self.0.lock().unwrap();
            s.calls.push(Call::Write(data.len()));
            s.written.extend_from_slice(data);
            Ok(s.accept.unwrap_or(data.len() as u32))
        }

        fn read(&self, _: Handle, buf: &mut [u8]) -> std::result::Result<u32, Status> {
            let mut s = self.0.lock().unwrap();
            s.calls.push(Call::Read(buf.len()));
            let n = buf.len().min(s.incoming.len());
            for slot in buf.iter_mut().take(n) {
                *slot = s.incoming.pop_front().unwrap();
            }
            Ok(n as u32)
        }

        fn get_queue_status(&self, _: Handle) -> std::result::Result<u32, Status> {
            let mut s = self.0.lock().unwrap();
            s.calls.push(Call::Queue);
            if s.queue_status != 0 {
                return Err(s.queue_status);
            }
            Ok(s.queue.pop_front().unwrap_or(s.incoming.len() as u32))
        }
    }

    fn maxx() -> FtdiDevice {
        test_device(5, "MX000000", "MaxxECU", false)
    }

    // ----- pure -------------------------------------------------------------------------

    #[test]
    fn text_stops_at_the_nul_and_reads_a_full_field() {
        assert_eq!(text(&ascii::<16>("MX000000")), "MX000000");
        assert_eq!(text(&[b'A'; 16]), "AAAAAAAAAAAAAAAA");
        assert_eq!(text(&[0; 16]), "");
        assert_eq!(text(b"ab\0cd"), "ab");
    }

    #[test]
    fn a_listed_device_takes_its_open_flag_and_product_id() {
        let info = InfoDetail {
            flags: 1,
            id: 0x0403_9728,
            serial: ascii("MX000000"),
            description: ascii("MaxxECU"),
        };
        let d = device_from_info(2, &info);
        assert_eq!(
            (d.index, d.serial.as_str(), d.description.as_str()),
            (2, "MX000000", "MaxxECU")
        );
        assert!(d.is_open);
        assert_eq!(d.product_id, 0x9728);
        assert!(!device_from_info(0, &InfoDetail { flags: 2, ..info }).is_open);
    }

    #[test]
    fn listing_skips_a_device_it_cannot_describe_and_survives_a_failing_list() {
        let fake = Fake::with(&[("A50285BI", "FT232R USB UART"), ("MX000000", "MaxxECU")]);
        let listed = list_devices(fake.as_ref());
        assert_eq!(listed.len(), 2);
        assert_eq!(listed[1].label(), "MaxxECU (MX000000)");
        assert_eq!(fake.calls(), [Call::List, Call::Detail(0), Call::Detail(1)]);

        assert!(list_devices(Fake::with(&[]).as_ref()).is_empty());
    }

    #[test]
    fn the_index_is_looked_up_by_serial_among_maxx_ecus_only() {
        let listed = [
            test_device(0, "A50285BI", "FT232R USB UART", false),
            test_device(1, "MX000001", "MaxxECU", false),
            test_device(2, "MX000000", "MaxxECU", false),
        ];
        // The wanted device carries the index of a different list; only its serial matters.
        assert_eq!(resolve_index(&listed, &maxx()), Some(2));
        assert_eq!(
            resolve_index(&listed, &test_device(9, "mx000001", "MaxxECU", false)),
            Some(1)
        );

        // A serial that names an FTDI device that is not a MaxxECU never resolves.
        assert_eq!(
            resolve_index(&listed, &test_device(0, "A50285BI", "MaxxECU", false)),
            None
        );
        assert_eq!(resolve_index(&[], &maxx()), None);
    }

    #[test]
    fn without_a_serial_the_description_decides() {
        let listed = [test_device(3, "", "MaxxECU", false)];
        assert_eq!(resolve_index(&listed, &test_device(0, "", "maxxecu", false)), Some(3));
        assert_eq!(resolve_index(&listed, &test_device(0, "", "Other", false)), None);
    }

    #[test]
    fn the_setup_sequence_is_the_c_sharps_in_its_order() {
        assert_eq!(
            setup_sequence(921_600, Duration::from_millis(500)),
            [
                Setup::BaudRate(921_600),
                Setup::DataCharacteristics {
                    bits: 8,
                    stop_bits: 0,
                    parity: 0
                },
                Setup::FlowControl {
                    flow: 0,
                    xon: 0,
                    xoff: 0
                },
                Setup::Timeouts { read: 50, write: 500 },
            ]
        );
        let what: Vec<_> = setup_sequence(115_200, Duration::ZERO)
            .iter()
            .map(|s| s.what())
            .collect();
        assert_eq!(
            what,
            [
                "set the baud rate",
                "set 8-N-1",
                "turn flow control off",
                "set the timeouts"
            ]
        );
    }

    #[test]
    fn milliseconds_truncate_like_the_c_sharp_cast() {
        assert_eq!(millis(Duration::from_micros(1_999)), 1);
        assert_eq!(millis(Duration::from_secs(9)), 9_000);
        assert_eq!(millis(Duration::from_secs(u64::MAX / 2)), u32::MAX);
    }

    #[test]
    fn the_texts_are_the_c_sharps() {
        assert_eq!(
            already_open(&maxx()),
            "MaxxECU (MX000000) is already open in another program. MTune holds the USB link for as \
             long as it is connected to the ECU, so disconnect it there first."
        );
        assert_eq!(
            would_not_open(&maxx(), 4),
            "MaxxECU (MX000000) would not open (FTDI status 4)."
        );
        let missing = missing_driver("ftd2xx.dll: not found");
        assert!(missing.starts_with("ftd2xx.dll could not be loaded, so a MaxxECU's USB port cannot be reached."));
        assert!(missing.contains("installed by MTune and by the MaxxECU USB driver package."));
        assert!(DRIVER_NOT_INSTALLED.contains("It arrives with MTune and with the driver package MaxxECU publishes."));
    }

    // ----- the link over a fake library ---------------------------------------------------

    fn open_link(fake: &Arc<Fake>) -> D2xxLink {
        D2xxLink::open(fake.clone(), &maxx(), 921_600, Duration::from_millis(500)).unwrap()
    }

    #[test]
    fn opening_calls_d2xx_in_the_c_sharps_order_with_its_arguments() {
        let fake = Fake::with(&[("A50285BI", "FT232R USB UART"), ("MX000000", "MaxxECU")]);
        let link = open_link(&fake);

        assert_eq!(
            fake.calls(),
            [
                Call::List,
                Call::Detail(0),
                Call::Detail(1),
                Call::Detail(1),
                Call::Open(1),
                Call::Baud(921_600),
                Call::Data(8, 0, 0),
                Call::Flow(0, 0, 0),
                Call::Timeouts(50, 500),
                Call::Latency(2),
                Call::Purge(3),
            ]
        );
        drop(link);
        assert_eq!(fake.calls().last(), Some(&Call::Close));
    }

    #[test]
    fn a_refused_setup_step_says_what_and_closes_the_handle() {
        for (step, what) in [
            (0, "set the baud rate"),
            (1, "set 8-N-1"),
            (2, "turn flow control off"),
            (3, "set the timeouts"),
        ] {
            let fake = Fake::with(&[("MX000000", "MaxxECU")]);
            fake.0.lock().unwrap().fail_setup = Some((step, 9));
            let Err(Error::Io(e)) = D2xxLink::open(fake.clone(), &maxx(), 115_200, Duration::from_millis(500)) else {
                panic!("expected an I/O error")
            };
            assert_eq!(
                e.to_string(),
                format!("The MaxxECU's USB device refused to {what} (FTDI status 9).")
            );
            assert_eq!(fake.calls().last(), Some(&Call::Close), "closed after {what}");
            // Nothing after the refused step ran, not even the latency timer.
            assert!(!fake.calls().iter().any(|c| matches!(c, Call::Latency(_))));
        }
    }

    #[test]
    fn status_3_means_another_program_has_it_and_other_statuses_say_the_number() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        fake.0.lock().unwrap().open_status = 3;
        let Err(Error::Io(e)) = D2xxLink::open(fake.clone(), &maxx(), 115_200, Duration::from_millis(500)) else {
            panic!("expected an I/O error")
        };
        assert_eq!(e.kind(), io::ErrorKind::ResourceBusy);
        assert!(e.to_string().contains("MTune holds the USB link"));
        // Nothing was opened, so nothing is closed.
        assert!(!fake.calls().contains(&Call::Close));

        fake.0.lock().unwrap().open_status = 4;
        let Err(Error::Io(e)) = D2xxLink::open(fake.clone(), &maxx(), 115_200, Duration::from_millis(500)) else {
            panic!("expected an I/O error")
        };
        assert_eq!(e.to_string(), "MaxxECU (MX000000) would not open (FTDI status 4).");
    }

    #[test]
    fn the_index_is_verified_again_right_before_opening_and_nothing_opens_if_it_changed() {
        // The C# opened by index. Between the list and `FT_Open` the list can renumber; the
        // index is read back once more, and if it no longer holds this MaxxECU nothing is opened.
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        fake.0.lock().unwrap().swap_after = Some(1);
        let Err(Error::Io(e)) = D2xxLink::open(fake.clone(), &maxx(), 115_200, Duration::from_millis(500)) else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().contains("is no longer plugged in"));
        assert!(!fake.calls().iter().any(|c| matches!(c, Call::Open(_))));
    }

    #[test]
    fn a_maxx_ecu_held_by_mtune_lists_blank_with_the_open_flag_and_is_never_opened() {
        let fake = Fake::with(&[("", "")]);
        {
            let mut s = fake.0.lock().unwrap();
            s.devices[0].0 = 0x0403_9728;
            s.open_flags = 1;
        }
        let listed = list_devices(fake.as_ref());
        assert!(listed[0].is_maxx_ecu_held_elsewhere());
        assert!(!listed[0].is_maxx_ecu());
        assert!(D2xxLink::open(fake.clone(), &listed[0], 115_200, Duration::from_millis(500)).is_err());
        assert!(!fake.calls().iter().any(|c| matches!(c, Call::Open(_))));
    }

    #[test]
    fn the_library_is_loaded_once_and_a_failed_load_is_tried_again() {
        let cell = OnceLock::new();
        let loads = Mutex::new(0);
        let fake: Arc<dyn D2xxApi> = Fake::with(&[]);
        let ok = || {
            *loads.lock().unwrap() += 1;
            Ok(fake.clone())
        };
        for _ in 0..5 {
            assert!(cached_load(&cell, &ok).is_ok());
        }
        assert_eq!(*loads.lock().unwrap(), 1);

        let cell = OnceLock::new();
        let attempts = Mutex::new(0);
        let failing = || -> std::result::Result<Arc<dyn D2xxApi>, String> {
            *attempts.lock().unwrap() += 1;
            Err("absent".into())
        };
        assert!(cached_load(&cell, &failing).is_err());
        assert!(cached_load(&cell, &failing).is_err());
        assert_eq!(*attempts.lock().unwrap(), 2, "a failure is not remembered");
        assert!(cached_load(&cell, &ok).is_ok(), "a driver installed later is picked up");
    }

    #[test]
    fn a_device_that_d2xx_does_not_list_is_no_longer_plugged_in() {
        let fake = Fake::with(&[("A50285BI", "FT232R USB UART")]);
        let Err(Error::Io(e)) = D2xxLink::open(fake.clone(), &maxx(), 115_200, Duration::from_millis(500)) else {
            panic!("expected an I/O error")
        };
        assert_eq!(e.to_string(), "MaxxECU (MX000000) is no longer plugged in.");
        assert!(!fake.calls().iter().any(|c| matches!(c, Call::Open(_))));
    }

    #[test]
    fn d2xx_never_opens_a_device_that_is_not_a_maxx_ecu() {
        let fake = Fake::with(&[("A50285BI", "FT232R USB UART")]);
        let microsquirt = test_device(0, "A50285BI", "FT232R USB UART", false);
        assert!(D2xxLink::open(fake.clone(), &microsquirt, 115_200, Duration::from_millis(500)).is_err());
        assert!(fake.calls().is_empty(), "not even listed: {:?}", fake.calls());
    }

    #[test]
    fn a_write_goes_whole_and_a_short_write_is_an_error() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        let mut link = open_link(&fake);

        link.write(b"burn", Duration::from_millis(500)).unwrap();
        assert_eq!(fake.0.lock().unwrap().written, b"burn");

        fake.0.lock().unwrap().accept = Some(2);
        let Err(Error::Io(e)) = link.write(b"burn", Duration::from_millis(500)) else {
            panic!("expected an I/O error")
        };
        assert_eq!(e.to_string(), "Only 2 of 4 bytes reached the MaxxECU's USB device.");
    }

    #[test]
    fn a_changed_write_timeout_is_applied_once_with_the_short_read_timeout() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        let mut link = open_link(&fake);
        let before = fake.calls().len();

        link.write(b"a", Duration::from_millis(500)).unwrap();
        link.write(b"b", Duration::from_secs(9)).unwrap();
        link.write(b"c", Duration::from_secs(9)).unwrap();

        let timeouts: Vec<_> = fake.calls()[before..]
            .iter()
            .filter(|c| matches!(c, Call::Timeouts(..)))
            .cloned()
            .collect();
        assert_eq!(timeouts, [Call::Timeouts(50, 9_000)]);
    }

    #[test]
    fn a_read_takes_what_is_waiting_up_to_the_buffer() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        let mut link = open_link(&fake);
        fake.0.lock().unwrap().incoming.extend([1, 2, 3, 4, 5]);

        let mut buf = [0u8; 3];
        assert_eq!(link.read_some(&mut buf, Duration::from_millis(100)).unwrap(), 3);
        assert_eq!(buf, [1, 2, 3]);
        let calls = fake.calls();
        assert_eq!(&calls[calls.len() - 2..], [Call::Queue, Call::Read(3)]);
    }

    #[test]
    fn a_quiet_line_returns_nothing_after_the_timeout_without_reading() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        let mut link = open_link(&fake);

        let started = Instant::now();
        assert_eq!(link.read_some(&mut [0u8; 8], Duration::from_millis(30)).unwrap(), 0);
        assert!(started.elapsed() >= Duration::from_millis(25));
        assert!(!fake.calls().iter().any(|c| matches!(c, Call::Read(_))));
        // Even a zero timeout looks once.
        assert_eq!(link.read_some(&mut [0u8; 8], Duration::ZERO).unwrap(), 0);
    }

    #[test]
    fn a_failing_queue_status_is_a_device_that_stopped_answering() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        let mut link = open_link(&fake);
        fake.0.lock().unwrap().queue_status = 4;
        let Err(Error::Io(e)) = link.read_some(&mut [0u8; 8], Duration::from_millis(30)) else {
            panic!("expected an I/O error")
        };
        assert_eq!(
            e.to_string(),
            "The MaxxECU's USB device stopped answering (FTDI status 4)."
        );
    }

    #[test]
    fn purging_asks_for_both_directions_and_never_fails() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        let mut link = open_link(&fake);
        link.purge().unwrap();
        assert_eq!(fake.calls().last(), Some(&Call::Purge(3)));
    }

    // ----- the selection order, through a fake loader -------------------------------------

    struct Probe {
        loads: Mutex<u32>,
        vcp_asked: Mutex<u32>,
    }

    fn route(device: &FtdiDevice, d2xx: Option<Arc<Fake>>, vcp_has_port: bool) -> (Result<Box<dyn FtdiLink>>, Probe) {
        struct VcpLink;
        impl FtdiLink for VcpLink {
            fn write(&mut self, _: &[u8], _: Duration) -> Result<()> {
                Ok(())
            }
            fn read_some(&mut self, _: &mut [u8], _: Duration) -> Result<usize> {
                Ok(0)
            }
            fn purge(&mut self) -> Result<()> {
                Ok(())
            }
        }

        let probe = Probe {
            loads: Mutex::new(0),
            vcp_asked: Mutex::new(0),
        };
        let result = windows_open(
            device,
            115_200,
            Duration::from_millis(500),
            &|| {
                *probe.loads.lock().unwrap() += 1;
                match &d2xx {
                    Some(fake) => Ok(fake.clone() as Arc<dyn D2xxApi>),
                    None => Err("ftd2xx.dll: The specified module could not be found.".to_string()),
                }
            },
            &|_, _| {
                *probe.vcp_asked.lock().unwrap() += 1;
                Ok(vcp_has_port.then(|| Box::new(VcpLink) as Box<dyn FtdiLink>))
            },
        );
        (result, probe)
    }

    #[test]
    fn when_the_library_loads_d2xx_is_the_route_and_the_virtual_com_port_is_not_asked() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        let (result, probe) = route(&maxx(), Some(fake.clone()), true);
        assert!(result.is_ok());
        assert_eq!(*probe.vcp_asked.lock().unwrap(), 0);
        assert!(fake.calls().contains(&Call::Open(0)));
    }

    #[test]
    fn a_d2xx_failure_is_reported_and_not_hidden_behind_the_virtual_com_port() {
        let fake = Fake::with(&[("MX000000", "MaxxECU")]);
        fake.0.lock().unwrap().open_status = 3;
        let (result, probe) = route(&maxx(), Some(fake), true);
        let Err(Error::Io(e)) = result else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().contains("MTune holds the USB link"));
        assert_eq!(*probe.vcp_asked.lock().unwrap(), 0);
    }

    #[test]
    fn a_missing_library_falls_back_to_the_virtual_com_port() {
        let (result, probe) = route(&maxx(), None, true);
        assert!(result.is_ok());
        assert_eq!(*probe.loads.lock().unwrap(), 1);
        assert_eq!(*probe.vcp_asked.lock().unwrap(), 1);
    }

    #[test]
    fn a_missing_library_and_no_virtual_com_port_gives_the_clear_error() {
        let (result, probe) = route(&maxx(), None, false);
        let Err(Error::Io(e)) = result else {
            panic!("expected an I/O error")
        };
        let text = e.to_string();
        assert_eq!(e.kind(), io::ErrorKind::NotFound);
        assert_eq!(
            *probe.vcp_asked.lock().unwrap(),
            1,
            "the virtual COM port was tried first"
        );
        // The C#'s sentence about where the DLL comes from, the cause, and the other way in.
        assert!(text.starts_with("ftd2xx.dll could not be loaded, so a MaxxECU's USB port cannot be reached."));
        assert!(text.contains("installed by MTune and by the MaxxECU USB driver package."));
        assert!(text.contains("The specified module could not be found."));
        assert!(text.contains("MaxxECU (MX000000) is plugged in, but it has no virtual COM port."));
        assert!(text.contains("Load VCP"));
    }

    #[test]
    fn neither_route_is_tried_for_a_device_that_is_not_a_maxx_ecu() {
        let microsquirt = test_device(0, "A50285BI", "FT232R USB UART", false);
        let (result, probe) = route(&microsquirt, Some(Fake::with(&[("A50285BI", "FT232R USB UART")])), true);
        assert!(result.is_err());
        assert_eq!(*probe.loads.lock().unwrap(), 0, "the library is not even loaded");
        assert_eq!(*probe.vcp_asked.lock().unwrap(), 0);
    }

    fn backend(d2xx: Option<Arc<Fake>>, usb: Vec<FtdiDevice>) -> WindowsBackend {
        WindowsBackend {
            loader: Arc::new(move || match &d2xx {
                Some(f) => Ok(f.clone() as Arc<dyn D2xxApi>),
                None => Err("absent".to_string()),
            }),
            vcp: Arc::new(|_, _| Ok(None)),
            usb_devices: Arc::new(move || usb.clone()),
        }
    }

    #[test]
    fn devices_come_from_d2xx_when_it_loads_so_the_index_is_d2xxs() {
        let fake = Fake::with(&[("A50285BI", "FT232R USB UART"), ("MX000000", "MaxxECU")]);
        let usb = vec![test_device(0, "MX000000", "MaxxECU", false)];
        let b = backend(Some(fake), usb);
        let devices = b.devices();
        assert_eq!(devices.len(), 2);
        assert_eq!(devices[1].index, 1);
        assert!(b.unavailable_reason().is_none());
    }

    #[test]
    fn devices_come_from_usb_when_the_library_is_absent_and_the_driver_is_reported_missing() {
        let usb = vec![test_device(0, "MX000000", "MaxxECU", false)];
        let b = backend(None, usb);
        assert_eq!(b.devices().len(), 1);
        assert_eq!(b.unavailable_reason().as_deref(), Some(DRIVER_NOT_INSTALLED));
    }
}
