//! Reading a Windows serial port's device ids: the pure half of the Windows enumeration, so
//! the parsing is tested on every OS. The SetupAPI calls that fetch the strings are in
//! `win32.rs`.
//!
//! What Windows reports for the ports this project meets:
//!
//! | device | enumerator | hardware id | instance id |
//! |---|---|---|---|
//! | Arduino Mega (Speeduino) | `USB` | `USB\VID_2341&PID_0042&REV_0001` | `USB\VID_2341&PID_0042\95730333837351C01221` |
//! | CDC ACM, composite | `USB` | `USB\VID_0483&PID_5740&MI_01` | `USB\VID_0483&PID_5740&MI_01\7&9DDFB75&0&0001` (serial is on the parent) |
//! | FTDI virtual COM port | `FTDIBUS` | `FTDIBUS\COMPORT&VID_0403&PID_6001` | `FTDIBUS\VID_0403+PID_6001+A50285BIA\0000` |
//! | paired Bluetooth | `BTHENUM` | `BTHENUM\{00001101-...}_LOCALMFG&0000` | `BTHENUM\{...}\9&3058B9ED&0&01B6EC10F00D_C00000000` |

use super::{PortType, UsbPortInfo};

/// The USB vendor and product id in a hardware or instance id.
///
/// Matches `VID_xxxx` followed, after `&` or `+`, by `PID_xxxx`, in any case, wherever it
/// appears in the string.
pub(crate) fn parse_vid_pid(id: &str) -> Option<(u16, u16)> {
    let upper = id.to_ascii_uppercase();
    let at = upper.find("VID_")?;
    let rest = &upper[at + 4..];
    let vid = hex4(rest)?;
    let after = rest.get(4..)?;
    let after = after.strip_prefix(['&', '+'])?.strip_prefix("PID_")?;
    Some((vid, hex4(after)?))
}

/// The four hex digits at the start of `s`, and not a fifth.
fn hex4(s: &str) -> Option<u16> {
    let digits = s.get(..4)?;
    if !digits.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    if s.as_bytes().get(4).is_some_and(u8::is_ascii_hexdigit) {
        return None;
    }
    u16::from_str_radix(digits, 16).ok()
}

/// The serial number a USB device reported, from its instance id (or its parent's, for one
/// function of a composite device).
///
/// `USB\VID_xxxx&PID_xxxx\<serial>`: the last segment. When a device has no serial number
/// Windows makes one up from the port topology (`7&9DDFB75&0&0001`), which has `&` in it and is
/// not a serial number. `FTDIBUS\VID_xxxx+PID_xxxx+<serial>\0000`: the third `+` part, which
/// carries the channel letter after the EEPROM serial (`A50285BIA`).
pub(crate) fn serial_from_instance_id(instance_id: &str) -> Option<String> {
    let mut parts = instance_id.split('\\');
    let enumerator = parts.next()?;
    let middle = parts.next()?;

    let serial = if enumerator.eq_ignore_ascii_case("FTDIBUS") {
        middle.split('+').nth(2)?
    } else if enumerator.eq_ignore_ascii_case("USB") && !middle.to_ascii_uppercase().contains("&MI_") {
        parts.next()?
    } else {
        return None;
    };

    (!serial.is_empty() && !serial.contains('&')).then(|| serial.to_string())
}

/// A `REG_MULTI_SZ` (NUL-separated strings, ended by an empty one), as the strings.
pub(crate) fn parse_multi_sz(units: &[u16]) -> Vec<String> {
    units
        .split(|&u| u == 0)
        .take_while(|s| !s.is_empty())
        .map(String::from_utf16_lossy)
        .collect()
}

/// A NUL-terminated UTF-16 string (a `REG_SZ`), as text.
pub(crate) fn parse_sz(units: &[u16]) -> String {
    let end = units.iter().position(|&u| u == 0).unwrap_or(units.len());
    String::from_utf16_lossy(&units[..end])
}

/// Whether a port name is a COM port. The Ports class also holds parallel ports (`LPT1`).
pub(crate) fn is_com_port(name: &str) -> bool {
    name.len() > 3 && name[..3].eq_ignore_ascii_case("COM") && name[3..].bytes().all(|b| b.is_ascii_digit())
}

/// What a port is, from the device's enumerator and ids.
///
/// `parent_instance_id` is the device one level up, where a composite USB device keeps the
/// serial number its interfaces lack.
pub(crate) fn classify(
    enumerator: &str,
    hardware_ids: &[String],
    instance_id: &str,
    parent_instance_id: Option<&str>,
    friendly_name: Option<&str>,
    manufacturer: Option<&str>,
) -> PortType {
    let is = |name: &str| enumerator.eq_ignore_ascii_case(name);

    if is("BTHENUM") {
        return PortType::Bluetooth;
    }

    if is("USB") || is("FTDIBUS") {
        let ids = hardware_ids
            .iter()
            .map(String::as_str)
            .chain(std::iter::once(instance_id));
        let Some((vid, pid)) = ids.filter_map(parse_vid_pid).next() else {
            return PortType::Unknown;
        };

        let serial =
            serial_from_instance_id(instance_id).or_else(|| parent_instance_id.and_then(serial_from_instance_id));
        return PortType::Usb(UsbPortInfo {
            vid,
            pid,
            serial_number: serial,
            manufacturer: manufacturer.filter(|m| !m.is_empty()).map(String::from),
            product: friendly_name.filter(|n| !n.is_empty()).map(String::from),
        });
    }

    if is("PCI") {
        return PortType::Pci;
    }
    PortType::Unknown
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(v: &[&str]) -> Vec<String> {
        v.iter().map(|x| x.to_string()).collect()
    }

    #[test]
    fn vid_and_pid_come_out_of_a_usb_hardware_id() {
        assert_eq!(parse_vid_pid(r"USB\VID_2341&PID_0042&REV_0001"), Some((0x2341, 0x0042)));
        assert_eq!(parse_vid_pid(r"USB\VID_0483&PID_5740&MI_01"), Some((0x0483, 0x5740)));
        assert_eq!(parse_vid_pid(r"usb\vid_0403&pid_6001"), Some((0x0403, 0x6001)));
    }

    #[test]
    fn vid_and_pid_come_out_of_an_ftdi_id() {
        assert_eq!(
            parse_vid_pid(r"FTDIBUS\COMPORT&VID_0403&PID_6001"),
            Some((0x0403, 0x6001))
        );
        assert_eq!(
            parse_vid_pid(r"FTDIBUS\VID_0403+PID_9728+MX000000A\0000"),
            Some((0x0403, 0x9728))
        );
    }

    #[test]
    fn anything_that_is_not_a_vid_and_pid_is_refused() {
        assert_eq!(parse_vid_pid(""), None);
        assert_eq!(
            parse_vid_pid(r"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000"),
            None
        );
        assert_eq!(parse_vid_pid(r"USB\VID_24&PID_0042"), None);
        assert_eq!(parse_vid_pid(r"USB\VID_ZZZZ&PID_0042"), None);
        // Five digits is not an id.
        assert_eq!(parse_vid_pid(r"USB\VID_23410&PID_0042"), None);
        assert_eq!(parse_vid_pid(r"USB\VID_2341&PID_00420"), None);
        assert_eq!(parse_vid_pid(r"USB\VID_2341"), None);
        assert_eq!(parse_vid_pid(r"USB\VID_2341&PIX_0042"), None);
    }

    #[test]
    fn a_plain_usb_device_carries_its_serial_in_the_instance_id() {
        assert_eq!(
            serial_from_instance_id(r"USB\VID_2341&PID_0042\95730333837351C01221").as_deref(),
            Some("95730333837351C01221")
        );
    }

    #[test]
    fn a_serial_windows_made_up_is_not_a_serial() {
        assert_eq!(serial_from_instance_id(r"USB\VID_2341&PID_0042\6&2A7B3C1&0&2"), None);
        assert_eq!(
            serial_from_instance_id(r"USB\VID_0483&PID_5740&MI_01\7&9DDFB75&0&0001"),
            None
        );
    }

    #[test]
    fn an_ftdi_serial_is_the_third_part_with_the_channel_letter() {
        assert_eq!(
            serial_from_instance_id(r"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000").as_deref(),
            Some("A50285BIA")
        );
        assert_eq!(serial_from_instance_id(r"FTDIBUS\VID_0403+PID_6001\0000"), None);
    }

    #[test]
    fn other_buses_have_no_usb_serial() {
        assert_eq!(serial_from_instance_id(r"PCI\VEN_1415&DEV_9501\4&1&0&00E0"), None);
        assert_eq!(serial_from_instance_id(""), None);
        assert_eq!(serial_from_instance_id("USB"), None);
    }

    #[test]
    fn multi_sz_splits_on_nul_and_stops_at_the_empty_string() {
        let units: Vec<u16> = "USB\\VID_2341&PID_0042&REV_0001\0USB\\VID_2341&PID_0042\0\0junk"
            .encode_utf16()
            .collect();
        assert_eq!(
            parse_multi_sz(&units),
            [r"USB\VID_2341&PID_0042&REV_0001", r"USB\VID_2341&PID_0042"]
        );
        assert!(parse_multi_sz(&[]).is_empty());
        assert!(parse_multi_sz(&[0, 0]).is_empty());
    }

    #[test]
    fn a_registry_string_ends_at_its_nul() {
        let units: Vec<u16> = "COM14\0\0".encode_utf16().collect();
        assert_eq!(parse_sz(&units), "COM14");
        assert_eq!(parse_sz(&[0x43, 0x4f, 0x4d]), "COM");
        assert_eq!(parse_sz(&[]), "");
    }

    #[test]
    fn only_com_ports_are_serial_ports() {
        assert!(is_com_port("COM3"));
        assert!(is_com_port("com14"));
        assert!(!is_com_port("LPT1"));
        assert!(!is_com_port("COM"));
        assert!(!is_com_port("COMX"));
        assert!(!is_com_port(""));
    }

    // ----- classification: which device is which -------------------------------------------

    #[test]
    fn a_speeduino_on_a_mega_is_usb_with_its_ids_serial_and_name() {
        let t = classify(
            "USB",
            &s(&[r"USB\VID_2341&PID_0042&REV_0001", r"USB\VID_2341&PID_0042"]),
            r"USB\VID_2341&PID_0042\95730333837351C01221",
            None,
            Some("Arduino Mega 2560 (COM14)"),
            Some("Arduino LLC (www.arduino.cc)"),
        );
        assert_eq!(
            t,
            PortType::Usb(UsbPortInfo {
                vid: 0x2341,
                pid: 0x0042,
                serial_number: Some("95730333837351C01221".into()),
                manufacturer: Some("Arduino LLC (www.arduino.cc)".into()),
                product: Some("Arduino Mega 2560 (COM14)".into()),
            })
        );
    }

    #[test]
    fn a_composite_device_takes_its_serial_from_the_parent() {
        let t = classify(
            "USB",
            &s(&[r"USB\VID_0483&PID_5740&MI_01"]),
            r"USB\VID_0483&PID_5740&MI_01\7&9DDFB75&0&0001",
            Some(r"USB\VID_0483&PID_5740\8D5B2A1F"),
            Some("USB Serial Device (COM5)"),
            None,
        );
        let PortType::Usb(u) = t else { panic!("USB") };
        assert_eq!((u.vid, u.pid), (0x0483, 0x5740));
        assert_eq!(u.serial_number.as_deref(), Some("8D5B2A1F"));
        assert_eq!(u.manufacturer, None);
    }

    #[test]
    fn a_maxxecus_ftdi_virtual_com_port_is_usb_by_its_ftdi_ids() {
        let t = classify(
            "FTDIBUS",
            &s(&[r"FTDIBUS\COMPORT&VID_0403&PID_9728"]),
            r"FTDIBUS\VID_0403+PID_9728+MX000000A\0000",
            None,
            Some("USB Serial Port (COM7)"),
            Some("FTDI"),
        );
        let PortType::Usb(u) = t else { panic!("USB") };
        assert_eq!((u.vid, u.pid), (0x0403, 0x9728));
        assert_eq!(u.serial_number.as_deref(), Some("MX000000A"));
    }

    #[test]
    fn an_empty_name_or_maker_is_no_name_or_maker() {
        let PortType::Usb(u) = classify(
            "USB",
            &s(&[r"USB\VID_1A86&PID_7523"]),
            r"USB\VID_1A86&PID_7523\5&1&0&1",
            None,
            Some(""),
            Some(""),
        ) else {
            panic!("USB")
        };
        assert_eq!((u.product, u.manufacturer, u.serial_number), (None, None, None));
    }

    #[test]
    fn a_usb_device_whose_ids_cannot_be_read_is_unknown_not_guessed() {
        assert_eq!(
            classify("USB", &s(&["USB\\Class_02"]), r"USB\ROOT_HUB30\4&1&0", None, None, None),
            PortType::Unknown
        );
    }

    #[test]
    fn the_ids_may_be_in_the_instance_id_alone() {
        let PortType::Usb(u) = classify("USB", &[], r"USB\VID_2341&PID_0042\ABC", None, None, None) else {
            panic!("USB")
        };
        assert_eq!((u.vid, u.pid), (0x2341, 0x0042));
    }

    #[test]
    fn bluetooth_pci_and_the_rest() {
        let bt = classify(
            "BTHENUM",
            &s(&[r"BTHENUM\{00001101}_LOCALMFG&0000"]),
            r"BTHENUM\x\y",
            None,
            None,
            None,
        );
        assert_eq!(bt, PortType::Bluetooth);
        assert_eq!(classify("PCI", &[], r"PCI\VEN_1415\1", None, None, None), PortType::Pci);
        assert_eq!(
            classify("ACPI", &[], r"ACPI\PNP0501\1", None, None, None),
            PortType::Unknown
        );
        // A Bluetooth port is never mistaken for USB even if a stray VID turns up in its ids.
        assert_eq!(
            classify("BTHENUM", &s(&["VID_0403&PID_6001"]), "", None, None, None),
            PortType::Bluetooth
        );
    }
}
