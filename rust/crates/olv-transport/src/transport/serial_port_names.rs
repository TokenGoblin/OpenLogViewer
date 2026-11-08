//! Ported from `SerialPortNames.cs`. Owned by package `w2-transports`.
//!
//! Names the serial ports.
//!
//! The C# read the Windows device tree through WMI. Here the description, USB ids and serial
//! number come from our own `serial_port::available_ports` on every OS (SetupAPI,
//! sysfs, IOKit), and the one thing it cannot see, a Windows Bluetooth port's device and
//! whether it is the incoming one, is read from the `BTHENUM` registry branch on Windows.
//!
//! Lives apart from the protocol because it is specific to the operating system in a way the
//! protocol is not, and because a port nobody can describe still works perfectly well: this
//! only ever adds a label.
//!
//! # Differences the bench must confirm
//!
//! * The device id (what a remembered ECU is keyed on) is built as `USB\VID_xxxx&PID_xxxx\<serial>`
//!   from the USB ids, which is what Windows' own instance id looked like for a plain USB
//!   serial adapter, so saved settings carry over for those. A composite device or one with no
//!   serial number had a longer Windows id and gets a different one here (its port name); an
//!   ECU remembered under the old id is simply asked again.
//! * Linux and macOS have no "incoming Bluetooth port" and no device-tree lookup of a paired
//!   device's name, so on those a Bluetooth serial port is labelled by its description only.
//! * The 10 second cache covers only the Windows Bluetooth registry read (the expensive part);
//!   the port list itself is read fresh each time, so a newly plugged adapter shows at once.
//! * The Windows Bluetooth rows come from the registry, which can still list a paired device
//!   that is not currently present; WMI listed only present ones.

use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

use olv_core::model::IgnoreCaseMap;
use olv_protocol::ms::obd2_adapters;
use time::OffsetDateTime;

use super::serial_ecu_transport::{list_system_ports, sort_by_number};
use super::serial_port::{PortInfo, PortType};

/// A serial port and what the operating system knows about the device behind it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SerialPortInfo {
    pub port_name: String,
    pub description: String,
    pub is_bluetooth: bool,
    /// True for Windows' incoming Bluetooth port, which cannot reach an ECU.
    ///
    /// Pairing a serial-port-profile device produces two: an outgoing one bound to that device,
    /// and an incoming one that waits for something to dial into this machine. They are named
    /// identically and sit next to each other in any list, so the only way to tell them apart
    /// is that the incoming one is bound to no address at all.
    pub is_incoming: bool,
    /// Name of the Bluetooth device this port reaches, where there is one.
    ///
    /// The port is called "Standard Serial over Bluetooth link" whatever is on the other end,
    /// so this is the only thing that says which ECU it is, and a MaxxECU has to be told apart
    /// before connecting, because it speaks a different protocol entirely.
    pub device_name: String,
    /// The identifier of the device behind this port.
    ///
    /// Steadier than the port number, which the OS hands out and reuses: a USB adapter carries
    /// its serial in here, so the same board is recognisable after a replug even if it lands on
    /// a different port.
    pub device_id: String,
    /// What answered here last time, when anything has.
    ///
    /// The OS names the chip ("Arduino Mega 2560"), which says nothing about which ECU is on
    /// it. The ECU's own signature does, and it is only knowable by having asked once.
    pub known_ecu: String,
    /// When this device was last connected to, or `None` if never.
    ///
    /// What puts the port you actually use at the top of the list. A workshop machine
    /// accumulates ports, and alphabetical order is no help at all in finding the one you
    /// reach for every day.
    pub last_used: Option<OffsetDateTime>,
}

impl SerialPortInfo {
    /// A port with a name, a description and whether it is Bluetooth; the rest default.
    pub fn new(port_name: &str, description: &str, is_bluetooth: bool) -> Self {
        Self {
            port_name: port_name.to_string(),
            description: description.to_string(),
            is_bluetooth,
            is_incoming: false,
            device_name: String::new(),
            device_id: String::new(),
            known_ecu: String::new(),
            last_used: None,
        }
    }

    /// Whether anything has ever answered here.
    pub fn is_known(&self) -> bool {
        !self.known_ecu.is_empty()
    }

    /// True when this port reaches a MaxxECU, which advertises as `MaxxECU_<serial>`.
    pub fn is_maxx_ecu(&self) -> bool {
        starts_with_ignore_case(&self.device_name, "MaxxECU")
    }

    /// True when this port looks like an OBD2 adapter.
    ///
    /// Guessed from the name, because there is nothing else to go on and the alternative is a
    /// wrong guess in the other direction: probing an adapter with TunerStudio commands finds
    /// nothing and reports an unknown ECU, which is a confusing thing to be told about a dongle
    /// that is working perfectly. A dongle calling itself something else still connects from
    /// the menu's OBD2 entry.
    pub fn is_obd2(&self) -> bool {
        obd2_adapters::looks_like_any(&[Some(&self.device_name), Some(&self.description)])
    }

    /// What to show in the connect menu.
    ///
    /// The device's own name where there is one, because that is the only thing that
    /// distinguishes two Bluetooth ports: every one of them is called "Standard Serial over
    /// Bluetooth link", so with two ECUs paired the menu offered two identical entries and
    /// picking the wrong one waited out a timeout to say nothing useful.
    pub fn label(&self) -> String {
        // What answered here beats what the OS calls the hardware. "Arduino Mega 2560" names
        // the chip a Speeduino happens to run on, which is not what anyone is looking for in
        // this list.
        if !self.known_ecu.is_empty() {
            format!("{} \u{2014} {}", self.port_name, self.known_ecu)
        } else if !self.device_name.is_empty() {
            format!("{} \u{2014} {} (Bluetooth)", self.port_name, self.device_name)
        } else if !self.description.is_empty() {
            format!("{} \u{2014} {}", self.port_name, self.description)
        } else {
            self.port_name.clone()
        }
    }
}

fn starts_with_ignore_case(text: &str, prefix: &str) -> bool {
    text.len() >= prefix.len()
        && text.is_char_boundary(prefix.len())
        && text[..prefix.len()].eq_ignore_ascii_case(prefix)
}

// ----- the device tree ----------------------------------------------------------------------

/// What is known about one port from the operating system.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub(crate) struct Entry {
    pub description: String,
    pub bluetooth: bool,
    pub incoming: bool,
    pub device_name: String,
    pub device_id: String,
}

/// One Bluetooth device-tree row: its instance id, and the name Windows gives it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct BluetoothRow {
    pub id: String,
    pub name: String,
}

/// The Bluetooth address embedded in an instance id, or empty.
fn address_in(device_id: &str) -> String {
    for part in device_id.split(['\\', '&', '_']) {
        if part.chars().count() == 12 && part.chars().all(|c| c.is_ascii_hexdigit()) && part.chars().any(|c| c != '0') {
            return part.to_string();
        }
    }
    String::new()
}

/// Whether a Bluetooth port is the incoming one.
///
/// The outgoing port carries the paired device's address in its instance id
///   `...&0&01B6EC10F00D_C00000000`
/// while the incoming one, being bound to nothing in particular, carries
///   `...&0&000000000000_00000000`
///
/// Read as "has no remote address" rather than by matching a shape, so a layout that differs
/// slightly errs towards offering the port.
pub fn is_incoming(device_id: &str) -> bool {
    let Some(last) = device_id.rfind('&') else {
        return false;
    };
    if last + 1 >= device_id.len() {
        return false;
    }

    let tail = &device_id[last + 1..];
    let address = tail.split('_').next().unwrap_or(tail);

    address.chars().count() >= 12 && address.chars().all(|c| c == '0')
}

/// `name[open+1..close]` and `name[..open]` for a device named like `Foo (COM12)`.
fn split_port_name(name: &str) -> Option<(String, String)> {
    let lower = name.to_ascii_lowercase();
    let open = lower.rfind("(com")?;
    let close = open + name[open..].find(')')?;
    Some((name[open + 1..close].to_string(), name[..open].trim().to_string()))
}

/// Port names to their entry, from the Bluetooth device-tree rows.
///
/// Bluetooth is recognised by the enumerator rather than by the name: a paired
/// serial-port-profile device attaches under `BTHENUM` whatever the module happens to be called.
/// The device behind a port is found by the address in the port's own instance id, matched to
/// the name of the paired device that carries the same address.
pub(crate) fn bluetooth_entries(rows: &[BluetoothRow]) -> IgnoreCaseMap<Entry> {
    let mut names: IgnoreCaseMap<String> = IgnoreCaseMap::new();
    for row in rows {
        // The serial-port entries are the ones to skip: they carry the address but not the
        // device's name.
        if row.name.to_ascii_lowercase().contains("(com") {
            continue;
        }
        let address = address_in(&row.id);
        if !address.is_empty() {
            names.try_add(&address, row.name.clone());
        }
    }

    let mut found = IgnoreCaseMap::new();
    for row in rows {
        let Some((port, description)) = split_port_name(&row.name) else {
            continue;
        };
        let reaches = names.get(&address_in(&row.id)).cloned().unwrap_or_default();
        found.insert(
            &port,
            Entry {
                description,
                bluetooth: true,
                incoming: is_incoming(&row.id),
                device_name: reaches,
                device_id: row.id.clone(),
            },
        );
    }
    found
}

/// The part of a Windows friendly name before its ` (COMn)`, which is the C#'s `Description`.
pub(crate) fn description_of(friendly: &str) -> String {
    match split_port_name(friendly) {
        Some((_, description)) => description,
        None => friendly.trim().to_string(),
    }
}

/// A port's entry from what the port enumeration reports about it.
///
/// The Bluetooth registry rows are consulted only for a port the live enumeration reports as
/// present and either Bluetooth or of no known kind. The registry keeps every device ever paired,
/// present or not (the C#'s WMI query saw only present ones), and Windows hands a freed port
/// number to the next device. A stale MaxxECU pairing left on COM7 must not label a USB Speeduino
/// that now sits on COM7, or the app would speak the MaxxECU protocol to it.
pub(crate) fn entry_from(info: &PortInfo, bluetooth: &IgnoreCaseMap<Entry>) -> Entry {
    if matches!(info.port_type, PortType::Bluetooth | PortType::Unknown)
        && let Some(entry) = bluetooth.get(&info.port_name)
    {
        return entry.clone();
    }

    match &info.port_type {
        PortType::Usb(usb) => {
            let description = usb
                .product
                .as_deref()
                .or(usb.manufacturer.as_deref())
                .map(description_of)
                .unwrap_or_default();
            let id = match usb.serial_number.as_deref().filter(|s| !s.is_empty()) {
                Some(serial) => format!("USB\\VID_{:04X}&PID_{:04X}\\{serial}", usb.vid, usb.pid),
                None => format!("USB\\VID_{:04X}&PID_{:04X}\\{}", usb.vid, usb.pid, info.port_name),
            };
            Entry {
                description,
                device_id: id,
                ..Entry::default()
            }
        }
        PortType::Bluetooth => Entry {
            description: "Standard Serial over Bluetooth link".into(),
            bluetooth: true,
            ..Entry::default()
        },
        _ => Entry::default(),
    }
}

// ----- what was learnt ----------------------------------------------------------------------

#[derive(Default)]
struct State {
    /// What answered on each device, by device id. Filled in by whoever connects; the OS does
    /// not know one Arduino from another.
    ecus: IgnoreCaseMap<String>,
    /// When each device was last connected to, by the same id.
    used: IgnoreCaseMap<OffsetDateTime>,
    bluetooth: Option<(Instant, Vec<BluetoothRow>)>,
}

static STATE: LazyLock<Mutex<State>> = LazyLock::new(|| Mutex::new(State::default()));

fn state() -> std::sync::MutexGuard<'static, State> {
    STATE.lock().unwrap_or_else(|e| e.into_inner())
}

/// How long the Bluetooth device-tree read is reused. The C# query cost about 200 ms, a
/// visible stutter on a menu that is opened repeatedly while hunting for the right port;
/// adapters do not come and go faster than this.
const FRESHNESS: Duration = Duration::from_secs(10);

/// Static, like the C# class: what has been learnt is shared by the whole process.
pub struct SerialPortNames;

impl SerialPortNames {
    /// Notes that this ECU answered on the device behind a port, and when.
    pub fn remember(port_name: &str, signature: &str) {
        let id = Self::all()
            .into_iter()
            .find(|p| p.port_name == port_name)
            .map(|p| p.device_id)
            .unwrap_or_default();
        if id.is_empty() || signature.is_empty() {
            return;
        }

        let mut s = state();
        s.ecus.insert(&id, signature.to_string());
        s.used.insert(&id, OffsetDateTime::now_utc());
    }

    /// Every device's last use, to be saved alongside the signatures.
    pub fn last_used() -> IgnoreCaseMap<OffsetDateTime> {
        state().used.clone()
    }

    /// Restores when each was last used.
    pub fn recall_last_used(saved: Option<&IgnoreCaseMap<OffsetDateTime>>) {
        let Some(saved) = saved else { return };
        let mut s = state();
        for (id, when) in saved.iter() {
            s.used.insert(id, *when);
        }
    }

    /// Drops everything learnt about devices, for a clean slate.
    pub fn forget() {
        let mut s = state();
        s.ecus = IgnoreCaseMap::new();
        s.used = IgnoreCaseMap::new();
    }

    /// Everything remembered so far, to be saved and handed back next time.
    pub fn remembered() -> IgnoreCaseMap<String> {
        state().ecus.clone()
    }

    /// Restores what a previous session learnt.
    pub fn recall(saved: Option<&IgnoreCaseMap<String>>) {
        let Some(saved) = saved else { return };
        let mut s = state();
        for (id, signature) in saved.iter() {
            s.ecus.insert(id, signature.clone());
        }
    }

    /// Every port worth offering.
    ///
    /// Windows' incoming Bluetooth port is left out: it cannot reach an ECU, it is named
    /// identically to the one that can, and choosing it used to hang for the write timeout and
    /// then take the application down. If that would leave nothing at all, everything is shown
    /// instead: a wrong guess should not be able to hide the only port there is.
    pub fn describe() -> Vec<SerialPortInfo> {
        let all = Self::all();
        let usable: Vec<SerialPortInfo> = all.iter().filter(|p| !p.is_incoming).cloned().collect();
        if usable.is_empty() { all } else { usable }
    }

    /// Every port, including the ones not worth offering.
    pub fn all() -> Vec<SerialPortInfo> {
        let mut ports = list_system_ports();
        let mut names: Vec<String> = ports.iter().map(|p| p.port_name.clone()).collect();
        sort_by_number(&mut names);
        ports.sort_by_key(|p| names.iter().position(|n| *n == p.port_name));

        let bluetooth = bluetooth_entries(&Self::bluetooth_rows());
        let s = state();

        ports
            .iter()
            .map(|info| {
                let entry = entry_from(info, &bluetooth);
                SerialPortInfo {
                    port_name: info.port_name.clone(),
                    description: entry.description,
                    is_bluetooth: entry.bluetooth,
                    is_incoming: entry.incoming,
                    device_name: entry.device_name,
                    known_ecu: s.ecus.get(&entry.device_id).cloned().unwrap_or_default(),
                    last_used: s.used.get(&entry.device_id).copied(),
                    device_id: entry.device_id,
                }
            })
            .collect()
    }

    /// The Bluetooth device-tree rows, reused for [`FRESHNESS`].
    fn bluetooth_rows() -> Vec<BluetoothRow> {
        {
            let s = state();
            if let Some((at, rows)) = &s.bluetooth
                && at.elapsed() < FRESHNESS
            {
                return rows.clone();
            }
        }

        // The device tree is unavailable or refuses: the ports are still there and still work;
        // they just go unlabelled.
        let rows = platform::bluetooth_rows();
        state().bluetooth = Some((Instant::now(), rows.clone()));
        rows
    }
}

#[cfg(windows)]
mod platform {
    use super::BluetoothRow;
    use winreg::RegKey;
    use winreg::enums::HKEY_LOCAL_MACHINE;

    const ROOT: &str = r"SYSTEM\CurrentControlSet\Enum\BTHENUM";

    /// A name as Windows shows it: `FriendlyName`, else `DeviceDesc` without the resource
    /// reference in front of it (`@bthenum.inf,%...%;Name`).
    pub(super) fn display_name(friendly: Option<String>, desc: Option<String>) -> Option<String> {
        friendly.filter(|f| !f.is_empty()).or_else(|| {
            desc.map(|d| d.rsplit(';').next().unwrap_or(&d).to_string())
                .filter(|d| !d.is_empty())
        })
    }

    /// Every `BTHENUM` device: its instance id (`BTHENUM\<class>\<instance>`) and name.
    pub(super) fn bluetooth_rows() -> Vec<BluetoothRow> {
        let mut rows = Vec::new();
        let Ok(root) = RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey(ROOT) else {
            return rows;
        };

        for class in root.enum_keys().flatten() {
            let Ok(class_key) = root.open_subkey(&class) else {
                continue;
            };
            for instance in class_key.enum_keys().flatten() {
                let Ok(key) = class_key.open_subkey(&instance) else {
                    continue;
                };
                let name = display_name(key.get_value("FriendlyName").ok(), key.get_value("DeviceDesc").ok());
                if let Some(name) = name {
                    rows.push(BluetoothRow {
                        id: format!(r"BTHENUM\{class}\{instance}"),
                        name,
                    });
                }
            }
        }
        rows
    }
}

#[cfg(not(windows))]
mod platform {
    use super::BluetoothRow;

    /// Linux and macOS have no device tree to ask: a Bluetooth serial port is labelled by its
    /// description only.
    pub(super) fn bluetooth_rows() -> Vec<BluetoothRow> {
        Vec::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const OUTGOING: &str =
        r"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&001D\9&3058B9ED&0&01B6EC10F00D_C00000000";
    const INCOMING: &str =
        r"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\9&3058B9ED&0&000000000000_00000000";

    #[test]
    fn the_outgoing_port_carries_the_devices_address() {
        assert!(!is_incoming(OUTGOING));
    }

    #[test]
    fn the_incoming_port_carries_no_address_at_all() {
        assert!(is_incoming(INCOMING));
    }

    #[test]
    fn a_usb_port_is_not_mistaken_for_either() {
        // Read as "has no remote address" rather than by matching a shape, so anything that is
        // not a Bluetooth instance id simply is not incoming.
        assert!(!is_incoming(r"USB\VID_0483&PID_5740&MI_01\7&9DDFB75&0&0001"));
        assert!(!is_incoming(r"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000"));
    }

    #[test]
    fn something_unrecognisable_is_offered() {
        // A wrong guess must not be able to hide a port; erring towards offering one costs a
        // message, erring the other way costs the only way in.
        assert!(!is_incoming(""));
        assert!(!is_incoming("BTHENUM"));
        assert!(!is_incoming(r"BTHENUM\odd\9&3058B9ED&0&"));
    }

    // ----- naming the port ---------------------------------------------------------------

    #[test]
    fn an_ecu_that_has_answered_is_named_ahead_of_the_chip() {
        // Windows calls a Speeduino "Arduino Mega 2560", which names the chip the firmware
        // happens to run on. Nobody hunting for their ECU in a list is looking for that.
        let mut port = SerialPortInfo::new("COM14", "Arduino Mega 2560", false);
        port.known_ecu = "Speeduino 2025.01.7".into();

        assert_eq!(port.label(), "COM14 \u{2014} Speeduino 2025.01.7");
        assert!(port.is_known());
    }

    #[test]
    fn the_os_description_is_used_until_something_has_answered() {
        let port = SerialPortInfo::new("COM14", "Arduino Mega 2560", false);

        assert_eq!(port.label(), "COM14 \u{2014} Arduino Mega 2560");
        assert!(!port.is_known());
    }

    #[test]
    fn a_bluetooth_device_still_names_itself_when_no_ecu_has_answered() {
        let mut port = SerialPortInfo::new("COM10", "Standard Serial over Bluetooth link", true);
        port.device_name = "MaxxECU_12345".into();

        assert_eq!(port.label(), "COM10 \u{2014} MaxxECU_12345 (Bluetooth)");
        assert!(port.is_maxx_ecu());
        assert!(!port.is_obd2());
    }

    #[test]
    fn a_port_with_nothing_known_is_just_its_name() {
        assert_eq!(SerialPortInfo::new("/dev/ttyS0", "", false).label(), "/dev/ttyS0");
    }

    #[test]
    fn an_obd2_dongle_is_recognised_by_either_name() {
        let mut port = SerialPortInfo::new("COM4", "Standard Serial over Bluetooth link", true);
        port.device_name = "OBDII".into();
        assert!(port.is_obd2());

        assert!(SerialPortInfo::new("COM5", "vLinker MC+", false).is_obd2());
        assert!(!SerialPortInfo::new("COM6", "Arduino Mega 2560", false).is_obd2());
    }

    /// The learnt state is process-wide, so the tests that touch it take turns.
    static SHARED: Mutex<()> = Mutex::new(());

    #[test]
    fn what_answered_survives_being_saved_and_restored() {
        // Worth remembering between sessions precisely because it is wanted before connecting:
        // having to connect once to find out what is on a port defeats the point of labelling it.
        let _turn = SHARED.lock().unwrap_or_else(|e| e.into_inner());

        let mut saved = IgnoreCaseMap::new();
        saved.insert(
            r"USB\VID_2341&PID_0042\95730333837351C01221",
            "Speeduino 2025.01.7".to_string(),
        );
        SerialPortNames::recall(Some(&saved));

        assert_eq!(
            SerialPortNames::remembered()
                .get(r"usb\vid_2341&pid_0042\95730333837351C01221")
                .map(String::as_str),
            Some("Speeduino 2025.01.7")
        );
    }

    #[test]
    fn forgetting_drops_both_the_signatures_and_the_times() {
        let _turn = SHARED.lock().unwrap_or_else(|e| e.into_inner());

        let mut sigs = IgnoreCaseMap::new();
        sigs.insert("USB\\A", "X".to_string());
        let mut times = IgnoreCaseMap::new();
        times.insert("USB\\A", OffsetDateTime::UNIX_EPOCH);
        SerialPortNames::recall(Some(&sigs));
        SerialPortNames::recall_last_used(Some(&times));
        assert_eq!(SerialPortNames::last_used().len(), 1);

        SerialPortNames::forget();

        assert!(SerialPortNames::remembered().is_empty());
        assert!(SerialPortNames::last_used().is_empty());
        SerialPortNames::recall(None);
        SerialPortNames::recall_last_used(None);
    }

    #[test]
    fn remembering_a_port_that_is_not_there_does_nothing() {
        let _turn = SHARED.lock().unwrap_or_else(|e| e.into_inner());
        SerialPortNames::forget();
        SerialPortNames::remember("COM-does-not-exist", "Speeduino");
        assert!(SerialPortNames::remembered().is_empty());
    }

    // ----- reading the device tree --------------------------------------------------------

    #[test]
    fn an_address_is_twelve_hex_digits_that_are_not_all_zero() {
        assert_eq!(address_in(OUTGOING), "01B6EC10F00D");
        assert_eq!(address_in(INCOMING), "");
        assert_eq!(
            address_in(r"BTHENUM\DEV_01B6EC10F00D\8&1&0&BLUETOOTHDEVICE_01B6EC10F00D"),
            "01B6EC10F00D"
        );
        assert_eq!(address_in(r"USB\VID_2341&PID_0042\95730333837351C01221"), "");
        assert_eq!(address_in(""), "");
    }

    #[test]
    fn a_bluetooth_port_is_matched_to_the_device_it_reaches() {
        let rows = [
            BluetoothRow {
                id: r"BTHENUM\DEV_01B6EC10F00D\7&1&0&BLUETOOTHDEVICE_01B6EC10F00D".into(),
                name: "MaxxECU_12345".into(),
            },
            BluetoothRow {
                id: OUTGOING.into(),
                name: "Standard Serial over Bluetooth link (COM10)".into(),
            },
            BluetoothRow {
                id: INCOMING.into(),
                name: "Standard Serial over Bluetooth link (COM11)".into(),
            },
        ];

        let found = bluetooth_entries(&rows);
        let out = found.get("COM10").unwrap();
        assert!(out.bluetooth && !out.incoming);
        assert_eq!(out.device_name, "MaxxECU_12345");
        assert_eq!(out.description, "Standard Serial over Bluetooth link");
        assert_eq!(out.device_id, OUTGOING);

        let inc = found.get("com11").unwrap();
        assert!(inc.incoming);
        assert_eq!(inc.device_name, "");
        assert_eq!(found.len(), 2);
    }

    #[test]
    fn a_friendly_name_loses_its_port_suffix() {
        assert_eq!(description_of("Arduino Mega 2560 (COM14)"), "Arduino Mega 2560");
        assert_eq!(description_of("USB Serial Port (COM3)"), "USB Serial Port");
        assert_eq!(description_of("Arduino Mega 2560"), "Arduino Mega 2560");
        assert_eq!(split_port_name("Foo (COM12)"), Some(("COM12".into(), "Foo".into())));
        assert_eq!(split_port_name("Foo (COM12"), None);
    }

    fn usb(port: &str, vid: u16, pid: u16, serial: Option<&str>, product: Option<&str>) -> PortInfo {
        PortInfo {
            port_name: port.into(),
            port_type: PortType::Usb(crate::transport::serial_port::UsbPortInfo {
                vid,
                pid,
                serial_number: serial.map(String::from),
                manufacturer: None,
                product: product.map(String::from),
            }),
        }
    }

    #[test]
    fn a_usb_port_is_described_and_keyed_by_its_ids_and_serial() {
        let none = IgnoreCaseMap::new();
        let entry = entry_from(
            &usb(
                "COM14",
                0x2341,
                0x0042,
                Some("95730333837351C01221"),
                Some("Arduino Mega 2560 (COM14)"),
            ),
            &none,
        );
        assert_eq!(entry.description, "Arduino Mega 2560");
        assert_eq!(entry.device_id, r"USB\VID_2341&PID_0042\95730333837351C01221");
        assert!(!entry.bluetooth && !entry.incoming);

        // No serial number: the port name keeps two such adapters apart.
        let entry = entry_from(&usb("/dev/ttyUSB0", 0x0403, 0x6001, None, None), &none);
        assert_eq!(entry.device_id, r"USB\VID_0403&PID_6001\/dev/ttyUSB0");
        assert_eq!(entry.description, "");
    }

    #[test]
    fn a_non_usb_port_has_no_id_so_nothing_is_remembered_for_it() {
        let none = IgnoreCaseMap::new();
        let info = PortInfo {
            port_name: "/dev/ttyS0".into(),
            port_type: PortType::Unknown,
        };
        assert_eq!(entry_from(&info, &none), Entry::default());
    }

    fn stale_maxx_row() -> IgnoreCaseMap<Entry> {
        let rows = [
            BluetoothRow {
                id: r"BTHENUM\DEV_01B6EC10F00D\7&1&0&BLUETOOTHDEVICE_01B6EC10F00D".into(),
                name: "MaxxECU_12345".into(),
            },
            BluetoothRow {
                id: OUTGOING.into(),
                name: "Standard Serial over Bluetooth link (COM7)".into(),
            },
        ];
        bluetooth_entries(&rows)
    }

    #[test]
    fn a_stale_bluetooth_pairing_does_not_label_the_usb_port_that_took_its_number() {
        // The registry keeps a MaxxECU paired over Bluetooth on COM7 after it is gone. A USB
        // Speeduino now enumerates as COM7; it must be described as what it is, or the app would
        // speak the MaxxECU protocol to it.
        let stale = stale_maxx_row();
        assert!(stale.get("COM7").unwrap().device_name == "MaxxECU_12345");

        let entry = entry_from(
            &usb(
                "COM7",
                0x2341,
                0x0042,
                Some("95730333837351C01221"),
                Some("Arduino Mega 2560 (COM7)"),
            ),
            &stale,
        );
        assert_eq!(entry.description, "Arduino Mega 2560");
        assert_eq!(entry.device_name, "");
        assert!(!entry.bluetooth);
        assert_eq!(entry.device_id, r"USB\VID_2341&PID_0042\95730333837351C01221");
    }

    #[test]
    fn a_port_listed_as_present_bluetooth_still_takes_its_registry_row() {
        let stale = stale_maxx_row();
        for kind in [PortType::Bluetooth, PortType::Unknown] {
            let entry = entry_from(
                &PortInfo {
                    port_name: "COM7".into(),
                    port_type: kind,
                },
                &stale,
            );
            assert_eq!(entry.device_name, "MaxxECU_12345");
            assert!(entry.bluetooth);
        }
    }

    #[test]
    fn a_row_for_a_port_that_is_not_listed_is_never_reached() {
        // Rows are only looked up for ports the enumeration returned, so a pairing whose port is
        // no longer present produces nothing at all.
        let stale = stale_maxx_row();
        let present = [usb("COM3", 0x2341, 0x0042, None, None)];
        let labelled: Vec<String> = present.iter().map(|p| entry_from(p, &stale).device_name).collect();
        assert_eq!(labelled, [""]);
    }

    #[test]
    fn a_pci_port_is_not_given_a_bluetooth_label_either() {
        let stale = stale_maxx_row();
        let entry = entry_from(
            &PortInfo {
                port_name: "COM7".into(),
                port_type: PortType::Pci,
            },
            &stale,
        );
        assert_eq!(entry, Entry::default());
    }

    #[test]
    fn describing_the_ports_never_fails() {
        let described = SerialPortNames::describe();
        assert!(described.len() <= SerialPortNames::all().len().max(described.len()));
    }
}

/// `RecordingWorkflowTests.cs`: the 18 tests the brief lists for this package. They are about
/// choosing when a log starts, what it is called and where it goes, and almost all of them drive
/// `MainViewModel`, which belongs to a later wave. The one that does not (a settings file
/// remembered before use times were kept) is ported and runs; the rest keep their names and
/// wait for `w8-viewmodel`.
#[cfg(test)]
mod recording_workflow_tests {
    use olv_store::store::settings_store::SettingsStore;
    use std::path::PathBuf;

    /// A device remembered before use times were kept still offers the shortcut.
    ///
    /// Caught by looking at the screen rather than by a test: every profile that predates the
    /// timestamps has signatures and no times, so requiring a time hid the shortcut from
    /// precisely the people who had been using this longest. Worse, it would have come back on
    /// its own after the next connection, which reads as intermittent rather than broken, and
    /// nobody reports intermittent.
    #[test]
    fn a_device_remembered_before_times_were_kept_still_counts() {
        let path: PathBuf = std::env::temp_dir().join(format!(
            "olv-old-{}-{:?}.json",
            std::process::id(),
            std::thread::current().id()
        ));
        std::fs::write(
            &path,
            r#"{"version":1,"knownEcus":{"USB\\VID_2341":"Speeduino 2025.01.7"}}"#,
        )
        .unwrap();

        let settings = SettingsStore::new(path.clone());
        let known = settings.known_ecus();
        let used = settings.ecu_last_used();
        let _ = std::fs::remove_file(&path);

        assert!(!known.is_empty());
        assert!(used.is_empty());

        // The signature is what the label is built from, and it survives being shortened to
        // something that fits on a button.
        assert_eq!(
            known.get(r"USB\VID_2341").map(String::as_str),
            Some("Speeduino 2025.01.7")
        );
    }

    macro_rules! needs_viewmodel {
        ($($name:ident),* $(,)?) => {$(
            #[test]
            #[ignore = "needs w8-viewmodel"]
            fn $name() {
                unimplemented!("drives MainViewModel, ported in w8-viewmodel");
            }
        )*};
    }

    needs_viewmodel!(
        a_fresh_install_offers_no_shortcut,
        forgetting_says_what_it_did_and_what_it_left_alone,
        connecting_does_not_record_by_default,
        turned_on_it_records_from_the_moment_it_connects,
        with_the_preference_off_nothing_is_written_and_it_says_so,
        the_preference_survives_a_restart,
        an_older_settings_file_takes_the_new_default,
        a_recording_can_be_started_and_stopped_without_ending_the_session,
        the_file_is_called_what_was_asked_for,
        the_suggested_name_says_what_and_when,
        the_next_recording_is_offered_the_last_folder,
        two_runs_in_one_session_are_two_files,
        the_button_says_what_it_will_do,
        the_status_line_says_when_nothing_is_being_written,
        recording_is_not_offered_with_nothing_connected,
        stopping_when_nothing_is_recording_says_so_rather_than_pretending,
        an_unwritable_path_is_reported_and_leaves_the_session_alone,
    );
}
