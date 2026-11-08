//! Ported from `BleDevices.cs`. Owned by package `w2-transports`.
//!
//! The paired Bluetooth LE devices.
//!
//! Read the same way the serial ports are, and for the same reason: this is specific to the
//! operating system in a way the protocol is not. A BLE device is never a COM port, so it cannot
//! appear in the port list however hard anyone looks, which is exactly how a working dongle
//! comes to look broken.
//!
//! The C# read Windows' device tree through WMI. Here:
//!
//! * **Windows**: the `BTHLE` branch of the registry (the same device tree WMI reads), via `winreg`.
//! * **Linux**: BlueZ's list of paired devices over D-Bus.
//! * **macOS**: CoreBluetooth cannot list paired devices or show addresses at all, so this
//!   scans for a couple of seconds and lists the named peripherals it hears; their "address" is
//!   a stable hash of the peripheral identifier ([`address_from_id`]).
//!
//! # Differences the bench must confirm
//!
//! * The registry can list a paired device that is no longer present; WMI listed only present ones.
//! * The scan ([`BleDevices::advertising`]) blocks for the window instead of returning a `Task`.
//! * macOS: [`BleDevices::all`] takes two seconds, not milliseconds.

use std::collections::HashSet;
use std::time::Duration;

use olv_protocol::ms::obd2_adapters;

/// A paired Bluetooth Low Energy device: what it calls itself, and where.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BleDevice {
    pub name: String,
    pub address: u64,
}

impl BleDevice {
    pub fn new(name: &str, address: u64) -> Self {
        Self {
            name: name.to_string(),
            address,
        }
    }

    /// Whether this looks like an OBD2 adapter.
    ///
    /// The same guess made for the Classic ones, from the same shared list, for the same reason:
    /// there is nothing else to go on. A BLE device publishes no profile that says "I am an
    /// ELM327": the serial services these use are vendor numbers that mean whatever the maker
    /// decided.
    pub fn is_obd2(&self) -> bool {
        obd2_adapters::looks_like_one(Some(&self.name))
    }

    pub fn label(&self) -> String {
        format!("{} (Bluetooth LE)", self.name)
    }
}

/// The radio address in a BLE instance id, or `None`.
///
/// The id reads `BTHLE\DEV_B96975AA93F6\9&306AB8F&0&B96975AA93F6`: the address is the twelve hex
/// digits after `DEV_`, and it is what the Bluetooth APIs want in order to reach the device.
pub fn address_in(device_id: &str) -> Option<u64> {
    const MARKER: &str = r"BTHLE\DEV_";

    let at = device_id.to_ascii_lowercase().find(&MARKER.to_ascii_lowercase())?;
    let rest = &device_id[at + MARKER.len()..];

    let hex: String = rest.chars().take(12).collect();
    if hex.chars().count() < 12 {
        return None;
    }

    // `NumberStyles.HexNumber` allows white space around the digits.
    let digits = hex.trim_matches(|c: char| matches!(c, ' ' | '\t' | '\n' | '\x0b' | '\x0c' | '\r'));
    if digits.is_empty() || !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    u64::from_str_radix(digits, 16).ok()
}

/// `AA:BB:CC:DD:EE:FF` for a radio address.
pub fn format_address(address: u64) -> String {
    let b = address.to_be_bytes();
    format!(
        "{:02X}:{:02X}:{:02X}:{:02X}:{:02X}:{:02X}",
        b[2], b[3], b[4], b[5], b[6], b[7]
    )
}

/// A radio address from `AA:BB:CC:DD:EE:FF` (any case).
pub fn parse_address(text: &str) -> Option<u64> {
    let parts: Vec<&str> = text.trim().split(':').collect();
    if parts.len() != 6 || parts.iter().any(|p| p.len() != 2) {
        return None;
    }
    let mut address = 0u64;
    for p in parts {
        address = (address << 8) | u64::from(u8::from_str_radix(p, 16).ok()?);
    }
    Some(address)
}

/// A stable 48-bit stand-in for a radio address, for a platform that hides them (macOS shows a
/// peripheral only by an identifier): FNV-1a over the identifier text, folded to 48 bits.
pub fn address_from_id(id: &str) -> u64 {
    let mut hash: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in id.bytes() {
        hash ^= u64::from(byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    (hash ^ (hash >> 48)) & 0x0000_ffff_ffff_ffff
}

/// The paired Bluetooth LE devices, and what is advertising.
pub struct BleDevices;

impl BleDevices {
    /// Every paired BLE device this machine knows about.
    ///
    /// Costing the BLE entries is not worth costing the connect menu: any failure to read the
    /// device tree gives an empty list.
    pub fn all() -> Vec<BleDevice> {
        let mut found: Vec<BleDevice> = Vec::new();
        for device in platform::paired() {
            if !found.iter().any(|d| d.address == device.address) {
                found.push(device);
            }
        }
        found
    }

    /// The ones worth offering as OBD2 adapters.
    pub fn obd2_adapters() -> Vec<BleDevice> {
        Self::all().into_iter().filter(BleDevice::is_obd2).collect()
    }

    /// Addresses heard advertising within `window`, which is the one honest way to ask a
    /// Bluetooth LE device whether it is switched on.
    ///
    /// A paired device is listed by the OS whether or not it has power (being paired is a fact
    /// about this computer, not about the device), so the list alone says nothing. A powered one
    /// announces itself several times a second and is heard within a second or two; an unpowered
    /// one cannot be, which is exactly the distinction wanted.
    ///
    /// Nothing is connected to, so an adapter already talking to something else is not disturbed
    /// by being looked for. Active scanning, which asks each device it hears for its scan
    /// response: some announce almost nothing until asked, and this is about hearing them rather
    /// than about being quiet.
    ///
    /// Being heard proves a device is on. Not being heard does not prove the opposite, and
    /// callers must not say that it does: a device already connected to something stops
    /// advertising, so an adapter paired to a phone in the same car goes silent while being
    /// perfectly alive. Verified on the Windows machine this was written on: a connected mouse is
    /// not heard.
    ///
    /// No radio, or one switched off, hears nothing, which is the right answer to that.
    pub fn advertising(window: Duration) -> HashSet<u64> {
        platform::heard(window)
    }
}

#[cfg(windows)]
mod platform {
    use super::*;
    use winreg::RegKey;
    use winreg::enums::HKEY_LOCAL_MACHINE;

    const ROOT: &str = r"SYSTEM\CurrentControlSet\Enum\BTHLE";

    /// A name as Windows shows it: `FriendlyName`, else `DeviceDesc` without the resource
    /// reference in front of it (`@bthle.inf,%...%;Name`).
    pub(super) fn display_name(friendly: Option<String>, desc: Option<String>) -> Option<String> {
        friendly.filter(|f| !f.is_empty()).or_else(|| {
            desc.map(|d| d.rsplit(';').next().unwrap_or(&d).to_string())
                .filter(|d| !d.is_empty())
        })
    }

    pub(super) fn paired() -> Vec<BleDevice> {
        let mut found = Vec::new();
        let Ok(root) = RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey(ROOT) else {
            return found;
        };

        for class in root.enum_keys().flatten() {
            let Ok(class_key) = root.open_subkey(&class) else {
                continue;
            };
            for instance in class_key.enum_keys().flatten() {
                let Ok(key) = class_key.open_subkey(&instance) else {
                    continue;
                };
                let id = format!(r"BTHLE\{class}\{instance}");
                let Some(address) = address_in(&id) else { continue };
                let name = display_name(key.get_value("FriendlyName").ok(), key.get_value("DeviceDesc").ok());
                if let Some(name) = name {
                    found.push(BleDevice { name, address });
                }
            }
        }
        found
    }

    pub(super) fn heard(window: Duration) -> HashSet<u64> {
        crate::transport::ble_ecu_transport::btleplug_backend::scan(window)
            .map(|v| v.into_iter().map(|(a, _)| a).collect())
            .unwrap_or_default()
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn a_friendly_name_beats_the_description_and_a_resource_prefix_is_dropped() {
            assert_eq!(
                display_name(Some("OBDII".into()), Some("x".into())).as_deref(),
                Some("OBDII")
            );
            assert_eq!(
                display_name(None, Some("@bthle.inf,%dev%;Generic Bluetooth LE device".into())).as_deref(),
                Some("Generic Bluetooth LE device")
            );
            assert_eq!(display_name(Some(String::new()), None), None);
        }
    }
}

#[cfg(target_os = "linux")]
mod platform {
    use super::*;
    use crate::transport::ble_ecu_transport::bluez;
    use zbus::blocking::Connection;

    pub(super) fn paired() -> Vec<BleDevice> {
        let Ok(conn) = Connection::system() else {
            return Vec::new();
        };
        bluez::known_devices(&conn)
            .unwrap_or_default()
            .into_iter()
            .filter(|k| k.paired && !k.name.is_empty())
            .map(|k| BleDevice {
                name: k.name,
                address: k.address,
            })
            .collect()
    }

    pub(super) fn heard(window: Duration) -> HashSet<u64> {
        bluez::scan(window)
            .map(|v| v.into_iter().map(|(a, _)| a).collect())
            .unwrap_or_default()
    }
}

#[cfg(target_os = "macos")]
mod platform {
    use super::*;
    use crate::transport::ble_ecu_transport::btleplug_backend;

    pub(super) fn paired() -> Vec<BleDevice> {
        btleplug_backend::scan(Duration::from_secs(2))
            .unwrap_or_default()
            .into_iter()
            .filter_map(|(address, name)| name.map(|name| BleDevice { name, address }))
            .collect()
    }

    pub(super) fn heard(window: Duration) -> HashSet<u64> {
        btleplug_backend::scan(window)
            .map(|v| v.into_iter().map(|(a, _)| a).collect())
            .unwrap_or_default()
    }
}

#[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
mod platform {
    use super::*;

    pub(super) fn paired() -> Vec<BleDevice> {
        Vec::new()
    }

    pub(super) fn heard(_: Duration) -> HashSet<u64> {
        HashSet::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The instance ids here are the real ones from a paired OBDII dongle, which is the hardware
    /// this was written against.
    const OBDII: &str = r"BTHLE\DEV_B96975AA93F6\9&306AB8F&0&B96975AA93F6";

    #[test]
    fn the_radio_address_is_read_from_the_instance_id() {
        // It is what the Bluetooth APIs want in order to reach the device, and the device tree
        // is the only place it is written down.
        assert_eq!(address_in(OBDII), Some(0xB96975AA93F6));
    }

    #[test]
    fn something_that_is_not_a_ble_device_has_no_address() {
        assert_eq!(address_in(r"USB\VID_2341&PID_0042\95730333837351C01221"), None);
        assert_eq!(
            address_in(
                r"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&001D\9&3058B9ED&0&01B6EC10F00D_C00000000"
            ),
            None
        );
        assert_eq!(address_in(""), None);
        assert_eq!(address_in(r"BTHLE\DEV_TOOSHORT"), None);
    }

    #[test]
    fn the_marker_is_found_in_any_case_and_anywhere_in_the_id() {
        assert_eq!(address_in(r"bthle\dev_b96975aa93f6\x"), Some(0xB96975AA93F6));
        assert_eq!(address_in(r"prefix BTHLE\DEV_000000000001"), Some(1));
        // Twelve characters that are not hex digits.
        assert_eq!(address_in(r"BTHLE\DEV_ZZZZZZZZZZZZ"), None);
    }

    #[test]
    fn an_adapter_is_recognised_by_what_it_advertises_as() {
        // A guess, and the only one available: BLE publishes no profile that says "I am an
        // ELM327"; the serial services these use are vendor numbers meaning whatever the maker
        // decided.
        assert!(BleDevice::new("OBDII", 1).is_obd2());
        assert!(BleDevice::new("Vgate iCar Pro", 1).is_obd2());
        assert!(BleDevice::new("vLinker MC+", 1).is_obd2());

        assert!(!BleDevice::new("MaxxECU_28xf7p", 1).is_obd2());
        assert!(!BleDevice::new("HP 720 Wireless Mouse", 1).is_obd2());
    }

    #[test]
    fn the_radio_is_named_so_the_entry_is_not_mistaken_for_a_port() {
        // It sits in the same menu as the COM ports and behaves the same way, so the one thing
        // worth saying is why it never appeared as one.
        assert_eq!(BleDevice::new("OBDII", 1).label(), "OBDII (Bluetooth LE)");
    }

    #[test]
    fn an_address_round_trips_through_its_colon_form() {
        assert_eq!(format_address(0xB96975AA93F6), "B9:69:75:AA:93:F6");
        assert_eq!(parse_address("b9:69:75:aa:93:f6"), Some(0xB96975AA93F6));
        assert_eq!(parse_address(" B9:69:75:AA:93:F6 "), Some(0xB96975AA93F6));
        assert_eq!(parse_address("B9:69:75:AA:93"), None);
        assert_eq!(parse_address("B9:69:75:AA:93:GG"), None);
        assert_eq!(parse_address("B:69:75:AA:93:F6"), None);
        assert_eq!(format_address(1), "00:00:00:00:00:01");
    }

    #[test]
    fn a_hidden_address_is_a_stable_48_bit_hash_of_the_identifier() {
        let a = address_from_id("0F3C6A52-1E55-4F36-9C7E-1A8B3F5D7C20");
        assert_eq!(a, address_from_id("0F3C6A52-1E55-4F36-9C7E-1A8B3F5D7C20"));
        assert_ne!(a, address_from_id("0F3C6A52-1E55-4F36-9C7E-1A8B3F5D7C21"));
        assert!(a <= 0xFFFF_FFFF_FFFF);
    }

    #[cfg(not(target_os = "macos"))] // CoreBluetooth wants a permission prompt a test cannot answer
    #[test]
    fn listing_and_scanning_are_safe_with_or_without_a_radio() {
        // Either a list or an empty one, never a panic or an error: this runs while a menu is
        // being built.
        let all = BleDevices::all();
        assert!(BleDevices::obd2_adapters().iter().all(|d| all.contains(d)));
        let mut seen = HashSet::new();
        assert!(all.iter().all(|d| seen.insert(d.address)));
    }

    #[test]
    #[ignore = "scans the air for two seconds; run by hand with a radio"]
    fn scanning_with_a_radio_hears_something_or_nothing() {
        let _ = BleDevices::advertising(Duration::from_secs(2));
    }
}
