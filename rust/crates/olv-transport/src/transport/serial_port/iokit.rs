//! macOS port enumeration through IOKit.
//!
//! Every serial port on macOS is an `IOSerialBSDClient` service in the IORegistry, with the
//! `/dev/cu.*` path as its `IOCalloutDevice`. For a USB adapter the USB device is an ancestor
//! in the registry and carries `idVendor`, `idProduct`, `USB Serial Number` and
//! `USB Product Name`, which `IORegistryEntrySearchCFProperty` finds by searching up the
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
pub(crate) fn classify(callout: &str, usb: Option<UsbPortInfo>) -> PortType {
    match usb {
        Some(usb) => PortType::Usb(usb),
        None if callout.to_ascii_lowercase().contains("bluetooth") => PortType::Bluetooth,
        None => PortType::Unknown,
    }
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
                port_type: classify(&path, None),
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
        IOIteratorNext, IOObjectRelease, IORegistryEntryCreateCFProperty, IORegistryEntrySearchCFProperty,
        IOServiceGetMatchingServices, IOServiceMatching, kIOMasterPortDefault, kIORegistryIterateParents,
        kIORegistryIterateRecursively,
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

    /// A property of the service or of any parent above it: how the USB device's ids are
    /// found from the serial client sitting under it.
    #[allow(unsafe_code)]
    fn inherited_property(entry: &Object, key: &str) -> Option<CFType> {
        let key = CFString::new(key);
        // SAFETY: as `own_property`; `kIOServicePlane` is a NUL-terminated constant.
        unsafe {
            let raw = IORegistryEntrySearchCFProperty(
                entry.0,
                kIOServicePlane,
                key.as_concrete_TypeRef(),
                std::ptr::null(),
                kIORegistryIterateRecursively | kIORegistryIterateParents,
            );
            if raw.is_null() {
                None
            } else {
                Some(CFType::wrap_under_create_rule(raw))
            }
        }
    }

    fn string_of(value: Option<CFType>) -> Option<String> {
        value?.downcast_into::<CFString>().map(|s| s.to_string())
    }

    fn id_of(value: Option<CFType>) -> Option<u16> {
        u16::try_from(value?.downcast_into::<CFNumber>()?.to_i64()?).ok()
    }

    fn usb_above(entry: &Object) -> Option<UsbPortInfo> {
        Some(UsbPortInfo {
            vid: id_of(inherited_property(entry, "idVendor"))?,
            pid: id_of(inherited_property(entry, "idProduct"))?,
            serial_number: string_of(inherited_property(entry, "USB Serial Number"))
                .or_else(|| string_of(inherited_property(entry, "kUSBSerialNumberString")))
                .filter(|s| !s.is_empty()),
            manufacturer: string_of(inherited_property(entry, "USB Vendor Name"))
                .or_else(|| string_of(inherited_property(entry, "kUSBVendorString")))
                .filter(|s| !s.is_empty()),
            product: string_of(inherited_property(entry, "USB Product Name"))
                .or_else(|| string_of(inherited_property(entry, "kUSBProductString")))
                .filter(|s| !s.is_empty()),
        })
    }

    pub(crate) fn available_ports() -> io::Result<Vec<PortInfo>> {
        let mut ports = Vec::new();
        let key = c_constant(kIOCalloutDeviceKey);
        for entry in services(kIOSerialBSDServiceValue).unwrap_or_default() {
            let Some(callout) = string_of(own_property(&entry, &key)) else {
                continue;
            };
            let usb = usb_above(&entry);
            ports.push(PortInfo {
                port_type: classify(&callout, usb),
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
            classify("/dev/cu.usbserial-A50285BI", Some(usb())),
            PortType::Usb(usb())
        );
    }

    #[test]
    fn the_builtin_bluetooth_port_is_bluetooth_and_a_bare_uart_is_unknown() {
        assert_eq!(classify("/dev/cu.Bluetooth-Incoming-Port", None), PortType::Bluetooth);
        assert_eq!(classify("/dev/cu.debug-console", None), PortType::Unknown);
        // A USB device wins over a name that happens to say Bluetooth.
        assert!(matches!(
            classify("/dev/cu.Bluetooth-dongle", Some(usb())),
            PortType::Usb(_)
        ));
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
