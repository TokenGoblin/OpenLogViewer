//! Linux port enumeration from sysfs: the pure half (reading the files, finding the USB
//! device above a tty), so it is tested on every OS. `posix.rs` supplies the real
//! `/sys/class/tty`.
//!
//! `/sys/class/tty/<name>/device` is a symlink to the device behind the tty. For a USB serial
//! adapter that is a `usb-serial` port (FTDI, CH340, CP210x) or a USB interface (CDC ACM, an
//! Arduino); in both cases the USB *device* is an ancestor, and it is the directory with an
//! `idVendor` file. Walking up until one is found covers both without knowing which.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use super::{PortInfo, PortType, UsbPortInfo};

fn read_trimmed(dir: &Path, file: &str) -> Option<String> {
    let text = fs::read_to_string(dir.join(file)).ok()?;
    let text = text.trim();
    (!text.is_empty()).then(|| text.to_string())
}

fn hex16(text: &str) -> Option<u16> {
    u16::from_str_radix(text.trim(), 16).ok()
}

/// The USB identity in `dir`, if it is a USB device's directory (`idVendor` and `idProduct`
/// are there and are hexadecimal, as the kernel writes them: `2341`).
pub(crate) fn usb_info_in(dir: &Path) -> Option<UsbPortInfo> {
    Some(UsbPortInfo {
        vid: hex16(&read_trimmed(dir, "idVendor")?)?,
        pid: hex16(&read_trimmed(dir, "idProduct")?)?,
        serial_number: read_trimmed(dir, "serial"),
        manufacturer: read_trimmed(dir, "manufacturer"),
        product: read_trimmed(dir, "product"),
    })
}

/// The USB device above `start` (a resolved path), or `start` itself if it is one.
pub(crate) fn find_usb_device(start: &Path) -> Option<UsbPortInfo> {
    start.ancestors().find_map(usb_info_in)
}

/// The last component of a symlink target: `../../../bus/usb` is `usb`.
pub(crate) fn subsystem_name(link_target: &Path) -> Option<String> {
    link_target.file_name().map(|n| n.to_string_lossy().into_owned())
}

/// What a tty is, from its resolved device path and its subsystem name. `None` is a tty that is
/// not a serial port worth listing.
///
/// Bluetooth is the `bluetooth` subsystem (an `rfcomm` binding), and is decided first: an rfcomm
/// tty sits under `hci0`, which sits under the USB Bluetooth dongle, so asking "is there a USB
/// device above it" first would list a paired SPP adapter as USB with the radio chip's ids. Any
/// other port with a USB device above it is USB whatever its subsystem. PCI is the `pci` subsystem. Anything else
/// (a motherboard UART on `pnp` or `platform`) is listed only if a driver is bound to it: the
/// 8250 driver creates `ttyS0` to `ttyS31` whether or not there is hardware behind them.
pub(crate) fn classify(device_path: &Path, subsystem: Option<&str>, has_driver: bool) -> Option<PortType> {
    if subsystem == Some("bluetooth") {
        return Some(PortType::Bluetooth);
    }
    if let Some(usb) = find_usb_device(device_path) {
        return Some(PortType::Usb(usb));
    }
    match subsystem {
        Some("pci") if has_driver => Some(PortType::Pci),
        _ if has_driver => Some(PortType::Unknown),
        _ => None,
    }
}

/// Lists the ports under `sysfs_root` (`/sys`), named under `dev_root` (`/dev`), sorted by name.
pub(crate) fn list(sysfs_root: &Path, dev_root: &str) -> io::Result<Vec<PortInfo>> {
    let tty = sysfs_root.join("class/tty");
    let mut names: Vec<String> = fs::read_dir(&tty)?
        .filter_map(Result::ok)
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .collect();
    names.sort();

    let mut ports = Vec::new();
    for name in names {
        let device: PathBuf = tty.join(&name).join("device");
        // Virtual consoles and ptys have no device; the path must resolve, so a dangling link
        // is skipped as well.
        let Ok(resolved) = fs::canonicalize(&device) else {
            continue;
        };
        let subsystem = fs::read_link(device.join("subsystem"))
            .ok()
            .and_then(|t| subsystem_name(&t));
        let has_driver = device.join("driver").exists();

        if let Some(port_type) = classify(&resolved, subsystem.as_deref(), has_driver) {
            ports.push(PortInfo {
                port_name: format!("{}/{name}", dev_root.trim_end_matches('/')),
                port_type,
            });
        }
    }
    Ok(ports)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    /// A scratch directory that removes itself.
    struct Scratch(PathBuf);

    impl Scratch {
        fn new() -> Self {
            static N: AtomicU32 = AtomicU32::new(0);
            let dir = std::env::temp_dir().join(format!(
                "olv-sysfs-{}-{}",
                std::process::id(),
                N.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }

        fn file(&self, rel: &str, text: &str) {
            let path = self.0.join(rel);
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, text).unwrap();
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn mega(s: &Scratch, dir: &str) {
        s.file(&format!("{dir}/idVendor"), "2341\n");
        s.file(&format!("{dir}/idProduct"), "0042\n");
        s.file(&format!("{dir}/serial"), "95730333837351C01221\n");
        s.file(&format!("{dir}/product"), "Arduino Mega 2560\n");
        s.file(&format!("{dir}/manufacturer"), "Arduino (www.arduino.cc)\n");
    }

    #[test]
    fn a_usb_device_directory_gives_its_ids_serial_and_names() {
        let s = Scratch::new();
        mega(&s, "usb1/1-2");

        let usb = usb_info_in(&s.0.join("usb1/1-2")).unwrap();
        assert_eq!(usb.vid, 0x2341);
        assert_eq!(usb.pid, 0x0042);
        assert_eq!(usb.serial_number.as_deref(), Some("95730333837351C01221"));
        assert_eq!(usb.product.as_deref(), Some("Arduino Mega 2560"));
        assert_eq!(usb.manufacturer.as_deref(), Some("Arduino (www.arduino.cc)"));
    }

    #[test]
    fn a_device_without_a_serial_or_names_has_none() {
        let s = Scratch::new();
        s.file("d/idVendor", "1a86");
        s.file("d/idProduct", "7523");
        s.file("d/serial", "  \n");

        let usb = usb_info_in(&s.0.join("d")).unwrap();
        assert_eq!((usb.vid, usb.pid), (0x1a86, 0x7523));
        assert_eq!((usb.serial_number, usb.product, usb.manufacturer), (None, None, None));
    }

    #[test]
    fn a_directory_without_readable_ids_is_not_a_usb_device() {
        let s = Scratch::new();
        s.file("a/idVendor", "2341");
        assert!(usb_info_in(&s.0.join("a")).is_none(), "no idProduct");
        s.file("b/idVendor", "zzzz");
        s.file("b/idProduct", "0042");
        assert!(usb_info_in(&s.0.join("b")).is_none(), "not hex");
        assert!(usb_info_in(&s.0.join("nowhere")).is_none());
    }

    #[test]
    fn the_usb_device_is_found_above_an_interface_and_a_serial_port() {
        // CDC ACM: tty -> device is the interface 1-2:1.0.
        // usb-serial: tty -> device is 1-2:1.0/ttyUSB0.
        let s = Scratch::new();
        mega(&s, "pci0000_00/usb1/1-2");
        let iface = s.0.join("pci0000_00/usb1/1-2/1-2_1.0");
        let port = iface.join("ttyUSB0");
        fs::create_dir_all(&port).unwrap();

        for start in [&iface, &port] {
            let usb = find_usb_device(start).unwrap();
            assert_eq!(usb.vid, 0x2341, "{}", start.display());
        }
    }

    #[test]
    fn no_usb_device_above_is_none() {
        let s = Scratch::new();
        fs::create_dir_all(s.0.join("platform/serial8250")).unwrap();
        assert!(find_usb_device(&s.0.join("platform/serial8250")).is_none());
    }

    #[test]
    fn the_subsystem_is_the_end_of_the_link() {
        assert_eq!(subsystem_name(Path::new("../../../bus/usb")).as_deref(), Some("usb"));
        assert_eq!(
            subsystem_name(Path::new("../../../../../../bus/pci")).as_deref(),
            Some("pci")
        );
        assert_eq!(subsystem_name(Path::new("")), None);
    }

    #[test]
    fn classification_prefers_usb_then_the_subsystem() {
        let s = Scratch::new();
        mega(&s, "usb1/1-2");
        fs::create_dir_all(s.0.join("usb1/1-2/iface")).unwrap();

        assert!(matches!(
            classify(&s.0.join("usb1/1-2/iface"), Some("usb"), true),
            Some(PortType::Usb(_))
        ));

        let none = s.0.join("elsewhere");
        assert_eq!(classify(&none, Some("bluetooth"), false), Some(PortType::Bluetooth));
        assert_eq!(classify(&none, Some("pci"), true), Some(PortType::Pci));
        assert_eq!(classify(&none, Some("pnp"), true), Some(PortType::Unknown));
        assert_eq!(classify(&none, None, true), Some(PortType::Unknown));
    }

    #[test]
    fn a_bluetooth_tty_under_a_usb_dongle_is_bluetooth_not_usb() {
        // rfcomm0 -> hci0 -> the USB radio. The radio's ids must not become the port's.
        let s = Scratch::new();
        s.file("usb1/1-7/idVendor", "8087");
        s.file("usb1/1-7/idProduct", "0a2b");
        let rfcomm = s.0.join("usb1/1-7/1-7_1.0/bluetooth/hci0");
        fs::create_dir_all(&rfcomm).unwrap();

        assert_eq!(classify(&rfcomm, Some("bluetooth"), true), Some(PortType::Bluetooth));
        assert_eq!(classify(&rfcomm, Some("bluetooth"), false), Some(PortType::Bluetooth));
        // Without the subsystem it would have been the dongle: the order is what matters.
        assert!(matches!(classify(&rfcomm, Some("usb"), true), Some(PortType::Usb(_))));
    }

    #[test]
    fn a_uart_with_no_driver_is_not_a_port() {
        // ttyS4 to ttyS31 on a PC: the 8250 driver makes them, nothing is behind them.
        let none = Path::new("/nonexistent/ttyS9");
        assert_eq!(classify(none, Some("pnp"), false), None);
        assert_eq!(classify(none, Some("pci"), false), None);
        assert_eq!(classify(none, None, false), None);
    }

    /// The whole walk, over a fake `/sys` with real symlinks. Symlinks need privilege on
    /// Windows, so this runs where they are free.
    #[cfg(unix)]
    #[test]
    fn a_fake_sys_lists_usb_serial_acm_rfcomm_and_skips_the_phantoms() {
        use std::os::unix::fs::symlink;

        let s = Scratch::new();
        let root = &s.0;
        mega(&s, "devices/usb1/1-2");
        fs::create_dir_all(root.join("devices/usb1/1-2/1-2:1.0/tty/ttyACM0")).unwrap();
        fs::create_dir_all(root.join("bus/usb")).unwrap();
        fs::create_dir_all(root.join("class/tty/ttyACM0")).unwrap();
        symlink(
            root.join("devices/usb1/1-2/1-2:1.0"),
            root.join("class/tty/ttyACM0/device"),
        )
        .unwrap();
        symlink(root.join("bus/usb"), root.join("devices/usb1/1-2/1-2:1.0/subsystem")).unwrap();

        // A phantom UART: a device with no driver.
        fs::create_dir_all(root.join("devices/platform/serial8250")).unwrap();
        fs::create_dir_all(root.join("class/tty/ttyS5")).unwrap();
        symlink(
            root.join("devices/platform/serial8250"),
            root.join("class/tty/ttyS5/device"),
        )
        .unwrap();

        // A virtual console: no device at all.
        fs::create_dir_all(root.join("class/tty/tty1")).unwrap();

        // A Bluetooth binding.
        fs::create_dir_all(root.join("devices/virtual/bluetooth/hci0")).unwrap();
        fs::create_dir_all(root.join("bus/bluetooth")).unwrap();
        fs::create_dir_all(root.join("class/tty/rfcomm0")).unwrap();
        symlink(
            root.join("devices/virtual/bluetooth/hci0"),
            root.join("class/tty/rfcomm0/device"),
        )
        .unwrap();
        symlink(
            root.join("bus/bluetooth"),
            root.join("devices/virtual/bluetooth/hci0/subsystem"),
        )
        .unwrap();

        let ports = list(root, "/dev").unwrap();
        let names: Vec<&str> = ports.iter().map(|p| p.port_name.as_str()).collect();
        assert_eq!(names, ["/dev/rfcomm0", "/dev/ttyACM0"]);
        assert_eq!(ports[0].port_type, PortType::Bluetooth);
        let PortType::Usb(u) = &ports[1].port_type else {
            panic!("USB")
        };
        assert_eq!((u.vid, u.pid), (0x2341, 0x0042));
    }

    #[test]
    fn listing_a_missing_sys_is_an_error_not_a_panic() {
        assert!(list(Path::new("/definitely/not/sys"), "/dev").is_err());
    }
}
