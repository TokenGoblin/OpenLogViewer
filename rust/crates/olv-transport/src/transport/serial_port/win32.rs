//! The serial port on Windows: the Win32 comm API through `windows-sys`.
//!
//! The handle is opened the way .NET's `SerialStream` opens one: `\\.\COMn`, read and write,
//! share mode 0 (exclusive), not overlapped. A plain synchronous handle is enough because the
//! transports use a port from one thread at a time, and it is what makes `ReadFile` honour
//! the comm timeouts directly: `ReadIntervalTimeout = MAXDWORD`, `ReadTotalTimeoutMultiplier =
//! MAXDWORD` and `ReadTotalTimeoutConstant = ms` return as soon as one byte is there and wait
//! up to `ms` if none is (see [`CommTimeouts`]).
//!
//! Every Win32 call is wrapped in its own small function carrying its own `unsafe` and
//! `// SAFETY:` comment, so the rest is safe code.
//!
//! Enumeration is SetupAPI's "Ports" device class (present devices only), with each device's
//! `PortName` read from its device registry key, its hardware ids and its instance id parsed by
//! [`windows_ids`](super::windows_ids). Ports in `HARDWARE\DEVICEMAP\SERIALCOMM` that SetupAPI
//! did not list (a legacy or software port) are added with no detail.

use std::io;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle, RawHandle};
use std::ptr::{null, null_mut};
use std::time::Duration;

use windows_sys::Win32::Devices::Communication::{
    CLRDTR, CLRRTS, COMMTIMEOUTS, DCB, EscapeCommFunction, GetCommState, PURGE_RXCLEAR, PURGE_TXCLEAR, PurgeComm,
    SETDTR, SETRTS, SetCommState, SetCommTimeouts, SetupComm,
};
use windows_sys::Win32::Devices::DeviceAndDriverInstallation::{
    CM_Get_Device_IDW, CM_Get_Parent, CR_SUCCESS, DICS_FLAG_GLOBAL, DIGCF_PRESENT, DIREG_DEV, GUID_DEVCLASS_PORTS,
    HDEVINFO, SP_DEVINFO_DATA, SPDRP_ENUMERATOR_NAME, SPDRP_FRIENDLYNAME, SPDRP_HARDWAREID, SPDRP_MFG,
    SetupDiDestroyDeviceInfoList, SetupDiEnumDeviceInfo, SetupDiGetClassDevsW, SetupDiGetDeviceInstanceIdW,
    SetupDiGetDeviceRegistryPropertyW, SetupDiOpenDevRegKey,
};
use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_ATTRIBUTE_NORMAL, OPEN_EXISTING, ReadFile, WriteFile};
use windows_sys::Win32::System::Registry::{HKEY, KEY_READ, RegCloseKey, RegQueryValueExW};

use super::windows_ids;
use super::{CommTimeouts, DcbPlan, Line, PortInfo, PortType, Purge, RawIo, SerialSettings};

pub(super) struct Port {
    handle: OwnedHandle,
    /// The timeouts the driver has now, so that a read does not call `SetCommTimeouts` again
    /// with what it already has.
    applied: CommTimeouts,
}

/// `COM3` is `\\.\COM3`; the prefix is required from `COM10` up and harmless below.
fn device_path(name: &str) -> String {
    if name.starts_with(r"\\") {
        name.to_string()
    } else {
        format!(r"\\.\{name}")
    }
}

pub(super) fn open(name: &str, settings: &SerialSettings) -> io::Result<Port> {
    let handle = create_file(&device_path(name))?;
    let raw = handle.as_raw_handle();

    // .NET asks for 4096 bytes of driver buffer each way; at 921600 baud the default is small.
    let _ = setup_comm(raw, 4096, 4096);

    let plan = DcbPlan::from_settings(settings);
    let mut dcb = get_comm_state(raw)?;
    dcb.BaudRate = plan.baud_rate;
    dcb.ByteSize = plan.byte_size;
    dcb.Parity = plan.parity;
    dcb.StopBits = plan.stop_bits;
    dcb._bitfield = plan.flags(dcb._bitfield);
    set_comm_state(raw, &dcb)?;

    let applied = CommTimeouts::new(super::DEFAULT_TIMEOUT, super::DEFAULT_TIMEOUT);
    set_comm_timeouts(raw, &applied)?;

    let mut port = Port { handle, applied };
    // The DCB set the lines' modes; assert or drop them explicitly as well, as .NET's
    // `DtrEnable`/`RtsEnable` do, so the level is what was asked for whatever the driver did.
    if let Some(on) = settings.dtr {
        port.set_line(Line::Dtr, on)?;
    }
    if let Some(on) = settings.rts {
        port.set_line(Line::Rts, on)?;
    }
    Ok(port)
}

impl Port {
    pub(super) fn purge(&mut self, which: Purge) -> io::Result<()> {
        let flags = match which {
            Purge::Input => PURGE_RXCLEAR,
            Purge::Both => PURGE_RXCLEAR | PURGE_TXCLEAR,
        };
        purge_comm(self.handle.as_raw_handle(), flags)
    }

    pub(super) fn set_line(&mut self, line: Line, asserted: bool) -> io::Result<()> {
        let function = match (line, asserted) {
            (Line::Dtr, true) => SETDTR,
            (Line::Dtr, false) => CLRDTR,
            (Line::Rts, true) => SETRTS,
            (Line::Rts, false) => CLRRTS,
        };
        escape_comm_function(self.handle.as_raw_handle(), function)
    }

    fn apply(&mut self, wanted: CommTimeouts) -> io::Result<()> {
        if wanted != self.applied {
            set_comm_timeouts(self.handle.as_raw_handle(), &wanted)?;
            self.applied = wanted;
        }
        Ok(())
    }
}

impl RawIo for Port {
    fn read_within(&mut self, buf: &mut [u8], timeout: Duration) -> io::Result<usize> {
        // Only the read half changes; the write half stays what it was.
        let read = CommTimeouts::new(timeout, Duration::ZERO);
        self.apply(CommTimeouts {
            write_multiplier: self.applied.write_multiplier,
            write_constant: self.applied.write_constant,
            ..read
        })?;
        // Zero bytes back is the timeout passing with nothing to read.
        read_file(self.handle.as_raw_handle(), buf)
    }

    fn write_within(&mut self, data: &[u8], timeout: Duration) -> io::Result<usize> {
        let write = CommTimeouts::new(Duration::ZERO, timeout);
        self.apply(CommTimeouts {
            read_interval: self.applied.read_interval,
            read_multiplier: self.applied.read_multiplier,
            read_constant: self.applied.read_constant,
            ..write
        })?;
        // Fewer bytes than given, down to none, is the write timeout passing.
        write_file(self.handle.as_raw_handle(), data)
    }
}

// ----- the Win32 calls, one small function each ---------------------------------------------

fn last_error() -> io::Error {
    io::Error::last_os_error()
}

#[allow(unsafe_code)]
fn create_file(path: &str) -> io::Result<OwnedHandle> {
    let wide: Vec<u16> = path.encode_utf16().chain(std::iter::once(0)).collect();
    // SAFETY: `wide` is NUL-terminated and outlives the call; the security attributes and
    // template handle are null, which `CreateFileW` documents as "none".
    let handle = unsafe {
        CreateFileW(
            wide.as_ptr(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            null(),
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE {
        return Err(last_error());
    }
    // SAFETY: `handle` is a valid handle `CreateFileW` has just returned, and nothing else owns
    // it; `OwnedHandle` closes it once.
    Ok(unsafe { OwnedHandle::from_raw_handle(handle) })
}

#[allow(unsafe_code)]
fn setup_comm(handle: RawHandle, input: u32, output: u32) -> io::Result<()> {
    // SAFETY: `handle` is an open comm handle for the duration of the call.
    if unsafe { SetupComm(handle, input, output) } == 0 {
        return Err(last_error());
    }
    Ok(())
}

#[allow(unsafe_code)]
fn get_comm_state(handle: RawHandle) -> io::Result<DCB> {
    // SAFETY: an all-zero `DCB` is a valid value (plain integers). `DCBlength` is set as the
    // API requires, and `handle` is an open comm handle for the duration of the call.
    unsafe {
        let mut dcb: DCB = std::mem::zeroed();
        dcb.DCBlength = std::mem::size_of::<DCB>() as u32;
        if GetCommState(handle, &mut dcb) == 0 {
            return Err(last_error());
        }
        Ok(dcb)
    }
}

#[allow(unsafe_code)]
fn set_comm_state(handle: RawHandle, dcb: &DCB) -> io::Result<()> {
    // SAFETY: `dcb` is a valid, initialised `DCB` that `get_comm_state` filled in, and `handle`
    // is an open comm handle.
    if unsafe { SetCommState(handle, dcb) } == 0 {
        return Err(last_error());
    }
    Ok(())
}

#[allow(unsafe_code)]
fn set_comm_timeouts(handle: RawHandle, t: &CommTimeouts) -> io::Result<()> {
    let timeouts = COMMTIMEOUTS {
        ReadIntervalTimeout: t.read_interval,
        ReadTotalTimeoutMultiplier: t.read_multiplier,
        ReadTotalTimeoutConstant: t.read_constant,
        WriteTotalTimeoutMultiplier: t.write_multiplier,
        WriteTotalTimeoutConstant: t.write_constant,
    };
    // SAFETY: `timeouts` is a valid `COMMTIMEOUTS` that lives through the call; `handle` is an
    // open comm handle.
    if unsafe { SetCommTimeouts(handle, &timeouts) } == 0 {
        return Err(last_error());
    }
    Ok(())
}

#[allow(unsafe_code)]
fn purge_comm(handle: RawHandle, flags: u32) -> io::Result<()> {
    // SAFETY: `handle` is an open comm handle; `flags` are `PURGE_*` bits.
    if unsafe { PurgeComm(handle, flags) } == 0 {
        return Err(last_error());
    }
    Ok(())
}

#[allow(unsafe_code)]
fn escape_comm_function(handle: RawHandle, function: u32) -> io::Result<()> {
    // SAFETY: `handle` is an open comm handle; `function` is one of the `SET*`/`CLR*` codes.
    if unsafe { EscapeCommFunction(handle, function) } == 0 {
        return Err(last_error());
    }
    Ok(())
}

#[allow(unsafe_code)]
fn read_file(handle: RawHandle, buf: &mut [u8]) -> io::Result<usize> {
    let len = u32::try_from(buf.len()).unwrap_or(u32::MAX);
    let mut read = 0u32;
    // SAFETY: `buf` is valid for writes of `len` bytes (`len <= buf.len()`) and `read` is a
    // valid `u32` for the count; the handle is synchronous, so the null `OVERLAPPED` is correct
    // and the call finishes before returning.
    let ok = unsafe { ReadFile(handle, buf.as_mut_ptr().cast(), len, &mut read, null_mut()) };
    if ok == 0 {
        return Err(last_error());
    }
    Ok(read as usize)
}

#[allow(unsafe_code)]
fn write_file(handle: RawHandle, data: &[u8]) -> io::Result<usize> {
    let len = u32::try_from(data.len()).unwrap_or(u32::MAX);
    let mut written = 0u32;
    // SAFETY: `data` is valid for reads of `len` bytes and `written` is a valid `u32`; the
    // handle is synchronous, so the null `OVERLAPPED` is correct.
    let ok = unsafe { WriteFile(handle, data.as_ptr().cast(), len, &mut written, null_mut()) };
    if ok == 0 {
        return Err(last_error());
    }
    Ok(written as usize)
}

// ----- enumeration --------------------------------------------------------------------------

/// A SetupAPI device-information set, destroyed on drop.
struct DeviceSet(HDEVINFO);

impl Drop for DeviceSet {
    #[allow(unsafe_code)]
    fn drop(&mut self) {
        // SAFETY: the set came from `SetupDiGetClassDevsW` and is destroyed exactly once, here.
        unsafe {
            SetupDiDestroyDeviceInfoList(self.0);
        }
    }
}

#[allow(unsafe_code)]
fn present_ports() -> io::Result<DeviceSet> {
    // SAFETY: the class GUID is a static, the enumerator is null (any), and there is no parent
    // window.
    let set = unsafe { SetupDiGetClassDevsW(&GUID_DEVCLASS_PORTS, null(), null_mut(), DIGCF_PRESENT) };
    if set == INVALID_HANDLE_VALUE as HDEVINFO {
        return Err(last_error());
    }
    Ok(DeviceSet(set))
}

#[allow(unsafe_code)]
fn device_at(set: &DeviceSet, index: u32) -> Option<SP_DEVINFO_DATA> {
    // SAFETY: an all-zero `SP_DEVINFO_DATA` is valid (integers and a GUID); `cbSize` is set as
    // required, and `set` is a live set.
    unsafe {
        let mut data: SP_DEVINFO_DATA = std::mem::zeroed();
        data.cbSize = std::mem::size_of::<SP_DEVINFO_DATA>() as u32;
        (SetupDiEnumDeviceInfo(set.0, index, &mut data) != 0).then_some(data)
    }
}

/// A device registry property as raw UTF-16 units (`REG_SZ` or `REG_MULTI_SZ`).
#[allow(unsafe_code)]
fn property(set: &DeviceSet, device: &SP_DEVINFO_DATA, which: u32) -> Option<Vec<u16>> {
    let mut size = 0u32;
    // SAFETY: the first call passes a null buffer of size 0 to ask for the size; `set` and
    // `device` are live. The second passes a buffer of exactly `size` bytes (`units * 2`).
    unsafe {
        SetupDiGetDeviceRegistryPropertyW(set.0, device, which, null_mut(), null_mut(), 0, &mut size);
        if size == 0 {
            return None;
        }
        let mut units = vec![0u16; (size as usize).div_ceil(2)];
        let ok = SetupDiGetDeviceRegistryPropertyW(
            set.0,
            device,
            which,
            null_mut(),
            units.as_mut_ptr().cast(),
            (units.len() * 2) as u32,
            null_mut(),
        );
        (ok != 0).then_some(units)
    }
}

#[allow(unsafe_code)]
fn instance_id(set: &DeviceSet, device: &SP_DEVINFO_DATA) -> Option<String> {
    let mut buf = [0u16; 512];
    // SAFETY: `buf` holds 512 units and that is the size passed; `set` and `device` are live.
    let ok = unsafe { SetupDiGetDeviceInstanceIdW(set.0, device, buf.as_mut_ptr(), buf.len() as u32, null_mut()) };
    (ok != 0).then(|| windows_ids::parse_sz(&buf))
}

/// The instance id of the device one level up the tree (for a composite USB device's serial).
#[allow(unsafe_code)]
fn parent_instance_id(device: &SP_DEVINFO_DATA) -> Option<String> {
    let mut parent = 0u32;
    let mut buf = [0u16; 512];
    // SAFETY: `parent` is a valid `u32` out-parameter and `device.DevInst` a device node from
    // SetupAPI; `buf` holds 512 units and that is the size passed.
    unsafe {
        if CM_Get_Parent(&mut parent, device.DevInst, 0) != CR_SUCCESS {
            return None;
        }
        (CM_Get_Device_IDW(parent, buf.as_mut_ptr(), buf.len() as u32, 0) == CR_SUCCESS)
            .then(|| windows_ids::parse_sz(&buf))
    }
}

/// The `PortName` value in the device's own registry key.
#[allow(unsafe_code)]
fn port_name(set: &DeviceSet, device: &SP_DEVINFO_DATA) -> Option<String> {
    // SAFETY: `set` and `device` are live. The key (if any) is closed below before returning,
    // and the value buffer is `units.len() * 2` bytes, the size passed. The name is a valid
    // NUL-terminated string.
    unsafe {
        let key: HKEY = SetupDiOpenDevRegKey(set.0, device, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if key as isize == INVALID_HANDLE_VALUE as isize {
            return None;
        }
        let name: Vec<u16> = "PortName\0".encode_utf16().collect();
        let mut units = [0u16; 64];
        let mut size = (units.len() * 2) as u32;
        let status = RegQueryValueExW(
            key,
            name.as_ptr(),
            null_mut(),
            null_mut(),
            units.as_mut_ptr().cast(),
            &mut size,
        );
        RegCloseKey(key);
        (status == 0).then(|| windows_ids::parse_sz(&units))
    }
}

fn describe(set: &DeviceSet, device: &SP_DEVINFO_DATA) -> Option<PortInfo> {
    let name = port_name(set, device).filter(|n| windows_ids::is_com_port(n))?;

    let text = |which: u32| property(set, device, which).map(|u| windows_ids::parse_sz(&u));
    let enumerator = text(SPDRP_ENUMERATOR_NAME).unwrap_or_default();
    let hardware_ids = property(set, device, SPDRP_HARDWAREID)
        .map(|u| windows_ids::parse_multi_sz(&u))
        .unwrap_or_default();
    let instance = instance_id(set, device).unwrap_or_default();
    // Only a composite function has no serial of its own, and only then is the parent needed.
    let parent = (windows_ids::serial_from_instance_id(&instance).is_none())
        .then(|| parent_instance_id(device))
        .flatten();

    Some(PortInfo {
        port_name: name,
        port_type: windows_ids::classify(
            &enumerator,
            &hardware_ids,
            &instance,
            parent.as_deref(),
            text(SPDRP_FRIENDLYNAME).as_deref(),
            text(SPDRP_MFG).as_deref(),
        ),
    })
}

/// Port names in `HARDWARE\DEVICEMAP\SERIALCOMM`.
fn serialcomm_names() -> Vec<String> {
    use winreg::RegKey;
    use winreg::enums::HKEY_LOCAL_MACHINE;

    let Ok(key) = RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey(r"HARDWARE\DEVICEMAP\SERIALCOMM") else {
        return Vec::new();
    };
    key.enum_values()
        .flatten()
        .filter_map(|(value_name, _)| key.get_value::<String, _>(&value_name).ok())
        .filter(|n| windows_ids::is_com_port(n))
        .collect()
}

pub(super) fn available_ports() -> io::Result<Vec<PortInfo>> {
    let mut ports = Vec::new();
    if let Ok(set) = present_ports() {
        let mut index = 0;
        while let Some(device) = device_at(&set, index) {
            if let Some(info) = describe(&set, &device) {
                ports.push(info);
            }
            index += 1;
        }
    }

    for name in serialcomm_names() {
        if !ports.iter().any(|p| p.port_name.eq_ignore_ascii_case(&name)) {
            ports.push(PortInfo {
                port_name: name,
                port_type: PortType::Unknown,
            });
        }
    }
    Ok(ports)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_device_path_always_has_the_prefix_windows_needs_above_com9() {
        assert_eq!(device_path("COM3"), r"\\.\COM3");
        assert_eq!(device_path("COM12"), r"\\.\COM12");
        assert_eq!(device_path(r"\\.\COM12"), r"\\.\COM12");
    }

    #[test]
    fn a_port_that_does_not_exist_fails_to_open_without_being_a_timeout() {
        let e = super::super::SerialPort::open("COM250", &SerialSettings::new(115_200))
            .err()
            .unwrap();
        assert!(!super::super::is_timeout(&e), "{e}");
        assert_eq!(e.kind(), io::ErrorKind::NotFound);
    }

    #[test]
    fn listing_ports_returns_only_com_names() {
        for p in available_ports().unwrap() {
            assert!(windows_ids::is_com_port(&p.port_name), "{}", p.port_name);
        }
    }
}
