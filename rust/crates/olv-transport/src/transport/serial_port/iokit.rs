//! macOS port enumeration through IOKit.
//!
//! Every serial port on macOS is an `IOSerialBSDClient` service in the IORegistry, with the
//! `/dev/cu.*` path as its `IOCalloutDevice`. For a USB adapter the USB device is an ancestor
//! in the registry and carries `idVendor`, `idProduct`, `USB Serial Number` and
//! `USB Product Name`, read from the nearest USB device node found by walking up the
//! parents. The calls are in [`native`] (macOS only, and so only type-checked from other
//! machines); what is decided from them is plain code below, tested everywhere.
//!
//! `io-kit-sys` and `core-foundation` are the bindings (both MIT OR Apache-2.0).

use super::{PortInfo, PortType, UsbPortInfo};

/// What a port is, from its callout path and the USB device above it, if there is one.
///
/// A USB device above it makes it USB. Without one, the system's own Bluetooth serial
/// (`/dev/cu.Bluetooth-Incoming-Port`) and paired devices (`/dev/cu.<name>`, which macOS
/// creates for an SPP device) are told by the name only for the built-in incoming port; there
/// is nothing else in the registry to tell a paired module from a UART, so the rest are
/// `Unknown`.
pub(crate) fn classify(callout: &str, usb: Option<UsbPortInfo>, bluetooth_above: bool) -> PortType {
    // Bluetooth first: on an Intel Mac a paired SPP port sits in the registry under the USB
    // Bluetooth radio, whose ids must not become the port's.
    if bluetooth_above || callout.to_ascii_lowercase().contains("bluetooth") {
        return PortType::Bluetooth;
    }
    match usb {
        Some(usb) => PortType::Usb(usb),
        None => PortType::Unknown,
    }
}

/// The USB identity of the nearest node that is a USB device (it has an id), read whole from
/// that one node. Never completed from a node further up: a device with no serial number has
/// none, and must not be given the hub's or the host controller's.
pub(crate) fn nearest_usb<N>(lineage: &[N], read: impl Fn(&N) -> Option<UsbPortInfo>) -> Option<UsbPortInfo> {
    lineage.iter().find_map(read)
}

/// `/dev/cu.*` names as ports, with no USB detail: the fall-back for when IOKit lists nothing
/// (a sandbox that hides the registry). Only the call-out devices (`cu.`), not the dial-in
/// twins (`tty.`), which block on carrier detect.
pub(crate) fn fallback_ports<I: IntoIterator<Item = String>>(dev_names: I, dev_root: &str) -> Vec<PortInfo> {
    let mut names: Vec<String> = dev_names.into_iter().filter(|n| n.starts_with("cu.")).collect();
    names.sort();
    names
        .into_iter()
        .map(|n| {
            let path = format!("{}/{n}", dev_root.trim_end_matches('/'));
            PortInfo {
                port_type: classify(&path, None, false),
                port_name: path,
            }
        })
        .collect()
}

#[cfg(target_os = "macos")]
pub(crate) mod native {
    use std::io;
    use std::os::raw::c_char;

    use core_foundation::base::{CFType, TCFType};
    use core_foundation::number::CFNumber;
    use core_foundation::string::CFString;
    use io_kit_sys::keys::kIOServicePlane;
    use io_kit_sys::ret::kIOReturnSuccess;
    use io_kit_sys::serial::keys::{kIOCalloutDeviceKey, kIOSerialBSDServiceValue};
    use io_kit_sys::{
        IOIteratorNext, IOObjectGetClass, IOObjectRelease, IORegistryEntryCreateCFProperty,
        IORegistryEntryGetParentEntry, IOServiceGetMatchingServices, IOServiceMatching, kIOMasterPortDefault,
    };

    use super::{PortInfo, UsbPortInfo, classify, fallback_ports};

    /// An IOKit object handle, released when dropped.
    struct Object(u32);

    impl Drop for Object {
        #[allow(unsafe_code)]
        fn drop(&mut self) {
            // SAFETY: the handle came from IOKit (`IOIteratorNext`) and is released exactly once,
            // here; this type is not `Clone`.
            unsafe {
                IOObjectRelease(self.0);
            }
        }
    }

    /// Every service of a class (`IOServiceMatching` takes a NUL-terminated class name).
    #[allow(unsafe_code)]
    fn services(class: *const c_char) -> io::Result<Vec<Object>> {
        // SAFETY: `class` is one of io-kit-sys's NUL-terminated constants. `IOServiceMatching`
        // returns a dictionary whose ownership `IOServiceGetMatchingServices` takes (even when it
        // fails), so it is not released here; the iterator it fills is owned by `Object` and
        // released on drop; every handle `IOIteratorNext` returns is wrapped in an `Object`.
        unsafe {
            let matching = IOServiceMatching(class);
            if matching.is_null() {
                return Err(io::Error::other("IOServiceMatching failed"));
            }
            let mut iterator = 0;
            let r = IOServiceGetMatchingServices(kIOMasterPortDefault, matching, &mut iterator);
            if r != kIOReturnSuccess {
                return Err(io::Error::other(format!(
                    "IOServiceGetMatchingServices failed ({r:#x})"
                )));
            }
            let iterator = Object(iterator);
            let mut found = Vec::new();
            loop {
                let next = IOIteratorNext(iterator.0);
                if next == 0 {
                    break;
                }
                found.push(Object(next));
            }
            Ok(found)
        }
    }

    /// A property of the service itself.
    #[allow(unsafe_code)]
    fn own_property(entry: &Object, key: &str) -> Option<CFType> {
        let key = CFString::new(key);
        // SAFETY: `entry.0` is a live registry entry and `key` a live CFString for the whole
        // call. A null allocator is the default one. The result follows the "create" rule (we
        // own one reference), which `wrap_under_create_rule` takes over; null is checked first.
        unsafe {
            let raw = IORegistryEntryCreateCFProperty(entry.0, key.as_concrete_TypeRef(), std::ptr::null(), 0);
            if raw.is_null() {
                None
            } else {
                Some(CFType::wrap_under_create_rule(raw))
            }
        }
    }

    /// The entry's parent in the service plane.
    #[allow(unsafe_code)]
    fn parent_of(entry: &Object) -> Option<Object> {
        let mut parent = 0;
        // SAFETY: `entry.0` is a live registry entry; `kIOServicePlane` is a NUL-terminated
        // constant; `parent` is a valid out-parameter. On success the handle is ours to release,
        // which `Object` does.
        let r = unsafe { IORegistryEntryGetParentEntry(entry.0, kIOServicePlane, &mut parent) };
        (r == kIOReturnSuccess && parent != 0).then_some(Object(parent))
    }

    /// The entry's IOKit class name.
    #[allow(unsafe_code)]
    fn class_name(entry: &Object) -> String {
        let mut name = [0 as c_char; 128];
        // SAFETY: `io_name_t` is 128 bytes, which is the size of `name`; IOKit NUL-terminates it.
        let r = unsafe { IOObjectGetClass(entry.0, name.as_mut_ptr()) };
        if r != kIOReturnSuccess {
            return String::new();
        }
        let end = name.iter().position(|&c| c == 0).unwrap_or(name.len());
        name[..end].iter().map(|&c| char::from(c as u8)).collect()
    }

    /// The parents above `entry`, nearest first, to a sane depth.
    fn parents(entry: &Object) -> Vec<Object> {
        let mut found: Vec<Object> = Vec::new();
        while found.len() < 16 {
            let Some(next) = parent_of(found.last().unwrap_or(entry)) else {
                break;
            };
            found.push(next);
        }
        found
    }

    fn string_of(value: Option<CFType>) -> Option<String> {
        value?.downcast_into::<CFString>().map(|s| s.to_string())
    }

    fn id_of(value: Option<CFType>) -> Option<u16> {
        u16::try_from(value?.downcast_into::<CFNumber>()?.to_i64()?).ok()
    }

    /// The USB identity held by one node, if it is a USB device: every field from that node.
    fn usb_in(node: &Object) -> Option<UsbPortInfo> {
        let text = |a: &str, b: &str| {
            string_of(own_property(node, a))
                .or_else(|| string_of(own_property(node, b)))
                .filter(|s| !s.is_empty())
        };
        Some(UsbPortInfo {
            vid: id_of(own_property(node, "idVendor"))?,
            pid: id_of(own_property(node, "idProduct"))?,
            serial_number: text("USB Serial Number", "kUSBSerialNumberString"),
            manufacturer: text("USB Vendor Name", "kUSBVendorString"),
            product: text("USB Product Name", "kUSBProductString"),
        })
    }

    pub(crate) fn available_ports() -> io::Result<Vec<PortInfo>> {
        let mut ports = Vec::new();
        let key = c_constant(kIOCalloutDeviceKey);
        for entry in services(kIOSerialBSDServiceValue).unwrap_or_default() {
            let Some(callout) = string_of(own_property(&entry, &key)) else {
                continue;
            };
            let above = parents(&entry);
            let lineage: Vec<&Object> = std::iter::once(&entry).chain(above.iter()).collect();
            let usb = super::nearest_usb(&lineage, |n| usb_in(n));
            let bluetooth = lineage
                .iter()
                .any(|n| class_name(n).to_ascii_lowercase().contains("bluetooth"));
            ports.push(PortInfo {
                port_type: classify(&callout, usb, bluetooth),
                port_name: callout,
            });
        }

        if ports.is_empty() {
            // IOKit gave nothing (a sandbox, or no ports): the device nodes themselves, with no
            // USB detail.
            let names = std::fs::read_dir("/dev")
                .map(|d| {
                    d.filter_map(Result::ok)
                        .map(|e| e.file_name().to_string_lossy().into_owned())
                        .collect::<Vec<_>>()
                })
                .unwrap_or_default();
            return Ok(fallback_ports(names, "/dev"));
        }

        ports.sort_by(|a, b| a.port_name.cmp(&b.port_name));
        Ok(ports)
    }

    /// A C string constant as text.
    #[allow(unsafe_code)]
    fn c_constant(key: *const c_char) -> String {
        // SAFETY: io-kit-sys's key constants are NUL-terminated static byte strings.
        unsafe { std::ffi::CStr::from_ptr(key) }.to_string_lossy().into_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn usb() -> UsbPortInfo {
        UsbPortInfo {
            vid: 0x0403,
            pid: 0x6001,
            serial_number: Some("A50285BI".into()),
            manufacturer: Some("FTDI".into()),
            product: Some("FT232R USB UART".into()),
        }
    }

    #[test]
    fn a_usb_device_above_makes_it_usb() {
        assert_eq!(
            classify("/dev/cu.usbserial-A50285BI", Some(usb()), false),
            PortType::Usb(usb())
        );
    }

    #[test]
    fn the_builtin_bluetooth_port_is_bluetooth_and_a_bare_uart_is_unknown() {
        assert_eq!(
            classify("/dev/cu.Bluetooth-Incoming-Port", None, false),
            PortType::Bluetooth
        );
        assert_eq!(classify("/dev/cu.debug-console", None, false), PortType::Unknown);
    }

    #[test]
    fn a_bluetooth_port_under_a_usb_radio_is_bluetooth_not_usb() {
        // Intel Macs: the paired port sits in the registry under the USB radio, whose ids are
        // not the port's. Found by a Bluetooth class above it, or by the name.
        assert_eq!(classify("/dev/cu.HC-05-DevB", Some(usb()), true), PortType::Bluetooth);
        assert_eq!(
            classify("/dev/cu.Bluetooth-Incoming-Port", Some(usb()), false),
            PortType::Bluetooth
        );
        assert!(matches!(
            classify("/dev/cu.usbserial-1", Some(usb()), false),
            PortType::Usb(_)
        ));
    }

    #[test]
    fn the_usb_identity_comes_whole_from_the_nearest_usb_node() {
        // Nodes nearest first: the serial client (no ids), the USB device (ids, no serial), then
        // a hub that has a serial of its own. The device has none, and must not be given the hub's.
        let hub = UsbPortInfo {
            vid: 0x05e3,
            pid: 0x0608,
            serial_number: Some("HUBSERIAL".into()),
            manufacturer: None,
            product: Some("USB2.0 Hub".into()),
        };
        let device = UsbPortInfo {
            vid: 0x0403,
            pid: 0x9728,
            serial_number: None,
            manufacturer: None,
            product: Some("MaxxECU".into()),
        };
        let nodes = [None, Some(device.clone()), Some(hub)];
        let found = nearest_usb(&nodes, |n| n.clone()).unwrap();
        assert_eq!(found, device);
        assert_eq!(found.serial_number, None);
        assert_eq!(nearest_usb::<Option<UsbPortInfo>>(&[None, None], |n| n.clone()), None);
    }

    #[test]
    fn the_fallback_lists_only_callout_devices_sorted_and_without_usb_detail() {
        let names = [
            "tty.usbserial-1",
            "cu.usbserial-2",
            "cu.Bluetooth-Incoming-Port",
            "null",
            "cu.usbserial-1",
        ]
        .map(String::from);
        let ports = fallback_ports(names, "/dev");

        let listed: Vec<&str> = ports.iter().map(|p| p.port_name.as_str()).collect();
        assert_eq!(
            listed,
            [
                "/dev/cu.Bluetooth-Incoming-Port",
                "/dev/cu.usbserial-1",
                "/dev/cu.usbserial-2"
            ]
        );
        assert_eq!(ports[0].port_type, PortType::Bluetooth);
        assert_eq!(ports[1].port_type, PortType::Unknown);
    }
}
