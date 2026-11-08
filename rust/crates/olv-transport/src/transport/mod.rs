//! `transport`: see `rust/packages.toml` for which package owns each file.

pub mod ble_devices;
pub mod ble_ecu_transport;
pub mod ftdi_ecu_transport;
pub mod serial_ecu_transport;
pub mod serial_port;
pub mod serial_port_names;
pub mod wifi_ecu_transport;

// Bluetooth LE, one backend per OS behind `ble_ecu_transport::BleBackend` (Linux's BlueZ backend
// lives in `ble_ecu_transport` itself).
#[cfg(target_os = "macos")]
pub mod ble_macos;
#[cfg(windows)]
pub mod ble_windows;
