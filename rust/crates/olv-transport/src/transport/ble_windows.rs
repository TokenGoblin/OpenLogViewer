//! Bluetooth LE on Windows, over WinRT through the `windows` crate (MIT / Apache-2.0).
//!
//! This is the same API `BleEcuTransport.cs` and `BleDevices.cs` used, called in the same order:
//!
//! 1. `BluetoothLEDevice.FromBluetoothAddressAsync(address)`: the device from its address alone,
//!    so a paired adapter that is not advertising can still be reached. No result is "could not be
//!    reached".
//! 2. `GetGattServicesAsync(BluetoothCacheMode.Uncached)`: read from the device, not from what
//!    Windows saw last time. This is also what opens the GATT connection. A status other than
//!    `Success` is reported with the status's name.
//! 3. `GetCharacteristicsAsync(Uncached)` for each service the transport might use.
//! 4. `ValueChanged +=` on the notify characteristic, *then*
//!    `WriteClientCharacteristicConfigurationDescriptorAsync(Notify or Indicate)`: the handler first,
//!    so nothing sent in reply to the first write is missed. A failed descriptor write removes the
//!    handler again.
//! 5. `WriteValueAsync(buffer, WriteWithResponse | WriteWithoutResponse)` for each 20-byte piece.
//!    (The C# used this overload, not `WriteValueWithResultAsync`, so it is what is used here.)
//! 6. Letting go: `ValueChanged -=` for an unsubscribe (no descriptor write, as in the C#), and
//!    `Close()` (`IClosable`, the C#'s `Dispose()`) on the services and then the device.
//!
//! There are no timeouts on the WinRT calls themselves, as in the C#; the only waits are the
//! transport's own (3 s for a service to answer, 1.5 s between the three open attempts).
//!
//! # Threads
//!
//! The C# ran every WinRT await on the thread pool (`Task.Run`) because waiting for one on the UI
//! thread deadlocks the window. The equivalent here: each blocking WinRT operation runs on a
//! thread of its own that is a member of the process's multithreaded apartment (a thread that has
//! initialised COM as a single-threaded apartment, as a UI thread has, would otherwise be the one
//! blocked). [`in_mta`] does it, and keeps one thread alive initialised as MTA for the life of the
//! process so the apartment the WinRT objects were made in does not go away between calls.
//! `ValueChanged` and `Received` handlers run on WinRT's own thread pool.
//!
//! # Unsafe
//!
//! One item: [`init_apartment`] calls `RoInitialize`, which the binding declares `unsafe`.

use std::sync::{Arc, Mutex, OnceLock};
use std::thread;
use std::time::Duration;

use olv_core::{Error, Result};
use uuid::Uuid;
use windows::Devices::Bluetooth::Advertisement::{
    BluetoothLEAdvertisementReceivedEventArgs, BluetoothLEAdvertisementWatcher, BluetoothLEScanningMode,
};
use windows::Devices::Bluetooth::GenericAttributeProfile::{
    GattCharacteristic, GattClientCharacteristicConfigurationDescriptorValue, GattCommunicationStatus,
    GattDeviceService, GattValueChangedEventArgs, GattWriteOption,
};
use windows::Devices::Bluetooth::{BluetoothCacheMode, BluetoothLEDevice};
use windows::Foundation::TypedEventHandler;
use windows::Storage::Streams::{DataReader, DataWriter, IBuffer};
use windows::Win32::System::WinRT::{RO_INIT_MULTITHREADED, RoInitialize};
use windows::core::GUID;

use crate::transport::ble_ecu_transport::{
    BleBackend, CharProps, GattCharacteristic as CharInfo, GattLink, GattService as ServiceInfo, Incoming,
    SERIAL_SERVICES,
};

// ---------------------------------------------------------------------------------------------
// Pure pieces, tested without a radio.
// ---------------------------------------------------------------------------------------------

/// A WinRT GUID as a `Uuid` (both are the same 128 bits, written the same way).
fn uuid_of(guid: GUID) -> Uuid {
    Uuid::from_u128(guid.to_u128())
}

/// What .NET's `ToString()` printed for a `GattCommunicationStatus`, which is what the C# put in
/// its messages.
fn status_name(status: i32) -> String {
    match status {
        0 => "Success".to_string(),
        1 => "Unreachable".to_string(),
        2 => "ProtocolError".to_string(),
        3 => "AccessDenied".to_string(),
        other => other.to_string(),
    }
}

fn status_text(status: GattCommunicationStatus) -> String {
    status_name(status.0)
}

fn failed(e: &windows::core::Error) -> String {
    let text = e.message();
    if text.is_empty() {
        format!("{:#010X}", e.code().0)
    } else {
        text
    }
}

/// Whether `uuid` is one of the services the transport may use.
fn is_candidate(uuid: Uuid) -> bool {
    SERIAL_SERVICES.contains(&uuid)
}

// ---------------------------------------------------------------------------------------------
// The apartment.
// ---------------------------------------------------------------------------------------------

/// Joins the multithreaded apartment and keeps it alive for the rest of the process.
///
/// `RoInitialize` is the only unsafe call in this backend: the binding declares it `unsafe`
/// because initialising an apartment has process-wide effects.
#[allow(unsafe_code)]
fn init_apartment() -> bool {
    // SAFETY: called once, on a thread of our own that has not initialised COM, so there is no
    // apartment type to conflict with. The thread never uninitialises (it parks for the life of
    // the process), so the apartment outlives every WinRT object made inside it.
    unsafe { RoInitialize(RO_INIT_MULTITHREADED).is_ok() }
}

fn keep_apartment_alive() {
    static KEEPER: OnceLock<bool> = OnceLock::new();
    KEEPER.get_or_init(|| {
        let (tx, rx) = std::sync::mpsc::channel();
        let spawned = thread::Builder::new()
            .name("olv-winrt-apartment".into())
            .spawn(move || {
                let ok = init_apartment();
                let _ = tx.send(ok);
                loop {
                    thread::park();
                }
            });
        spawned.is_ok() && rx.recv().unwrap_or(false)
    });
}

/// Runs `work` on a thread that is not the caller's, in the multithreaded apartment, and waits
/// for it. This is the C#'s `Task.Run(...).GetAwaiter().GetResult()`.
fn in_mta<T: Send>(work: impl FnOnce() -> T + Send) -> T {
    keep_apartment_alive();
    thread::scope(|scope| match scope.spawn(work).join() {
        Ok(value) => value,
        Err(panic) => std::panic::resume_unwind(panic),
    })
}

// ---------------------------------------------------------------------------------------------
// The backend.
// ---------------------------------------------------------------------------------------------

struct Backend;

pub(crate) fn backend() -> Arc<dyn BleBackend> {
    Arc::new(Backend)
}

impl BleBackend for Backend {
    fn connect(&self, address: u64, sink: Arc<Incoming>) -> Result<Option<Box<dyn GattLink>>> {
        // A null result is the C#'s `?? throw "could not be reached"`. The binding reports a null
        // result as an error whose code is S_OK ("The operation completed successfully."), which
        // is checked for here; any other failure (no radio, Bluetooth off) is an I/O error, which
        // the transport retries and then reports.
        let device =
            in_mta(|| BluetoothLEDevice::FromBluetoothAddressAsync(address).and_then(|operation| operation.join()));
        let device = match device {
            Ok(device) => Some(device),
            Err(e) if e.code().is_ok() => None,
            Err(e) => {
                return Err(Error::Io(std::io::Error::other(format!(
                    "Bluetooth is unavailable ({}).",
                    failed(&e)
                ))));
            }
        };
        Ok(device.map(|device| {
            Box::new(Link {
                device,
                services: Vec::new(),
                sink,
                subscription: None,
            }) as Box<dyn GattLink>
        }))
    }
}

/// One service of the device, and the characteristics listed for it.
struct Held {
    service: GattDeviceService,
    characteristics: Vec<GattCharacteristic>,
    closed: bool,
}

impl Held {
    /// `Dispose()`: let go of the handle on the connection.
    fn close(&mut self) {
        if !self.closed {
            self.closed = true;
            // A device that has gone out of range cannot be closed politely.
            let _ = self.service.Close();
        }
    }
}

/// The `ValueChanged` subscription in force.
struct Subscription {
    characteristic: GattCharacteristic,
    token: i64,
}

struct Link {
    device: BluetoothLEDevice,
    /// Parallel to the `GattService`s given out.
    services: Vec<Held>,
    sink: Arc<Incoming>,
    subscription: Option<Subscription>,
}

/// The bytes in a WinRT buffer: the C#'s `DataReader.FromBuffer` and `ReadBytes`.
fn bytes_of(buffer: &IBuffer) -> windows::core::Result<Vec<u8>> {
    let mut bytes = vec![0u8; buffer.Length()? as usize];
    DataReader::FromBuffer(buffer)?.ReadBytes(&mut bytes)?;
    Ok(bytes)
}

impl Link {
    fn remove_handler(&mut self) {
        if let Some(subscription) = self.subscription.take() {
            let _ = subscription.characteristic.RemoveValueChanged(subscription.token);
        }
    }

    fn list(&mut self) -> std::result::Result<Vec<ServiceInfo>, String> {
        let result = self
            .device
            .GetGattServicesWithCacheModeAsync(BluetoothCacheMode::Uncached)
            .and_then(|operation| operation.join())
            .map_err(|e| failed(&e))?;

        let status = result.Status().map_err(|e| failed(&e))?;
        if status != GattCommunicationStatus::Success {
            return Err(status_text(status));
        }

        let mut held = Vec::new();
        let mut out = Vec::new();
        for service in result.Services().map_err(|e| failed(&e))? {
            let uuid = uuid_of(service.Uuid().map_err(|e| failed(&e))?);

            if !is_candidate(uuid) {
                // Never going to be used: let go of it now rather than when opening finishes.
                let _ = service.Close();
                held.push(Held {
                    service,
                    characteristics: Vec::new(),
                    closed: true,
                });
                out.push(ServiceInfo {
                    uuid,
                    characteristics: Vec::new(),
                });
                continue;
            }

            // Uncached, as the C# asked. A service whose characteristics cannot be listed is not
            // a candidate (the C# returned false for it), which an empty list says.
            let characteristics: Vec<GattCharacteristic> = service
                .GetCharacteristicsWithCacheModeAsync(BluetoothCacheMode::Uncached)
                .and_then(|operation| operation.join())
                .ok()
                .filter(|r| r.Status().is_ok_and(|s| s == GattCommunicationStatus::Success))
                .and_then(|r| r.Characteristics().ok())
                .map(|list| list.into_iter().collect())
                .unwrap_or_default();

            let infos = characteristics
                .iter()
                .map(|c| {
                    Ok(CharInfo {
                        uuid: uuid_of(c.Uuid()?),
                        props: CharProps::from_bits(c.CharacteristicProperties()?.0),
                    })
                })
                .collect::<windows::core::Result<Vec<_>>>()
                .map_err(|e| failed(&e))?;

            held.push(Held {
                service,
                characteristics,
                closed: false,
            });
            out.push(ServiceInfo {
                uuid,
                characteristics: infos,
            });
        }

        // A new listing replaces the last.
        self.services.iter_mut().for_each(Held::close);
        self.services = held;
        Ok(out)
    }

    fn subscribe_to(
        &mut self,
        service: usize,
        characteristic: usize,
        indicate: bool,
    ) -> std::result::Result<(), String> {
        let notify = self.services[service].characteristics[characteristic].clone();

        // The handler first.
        let sink = self.sink.clone();
        let token = notify
            .ValueChanged(&TypedEventHandler::new(
                move |_: windows::core::Ref<GattCharacteristic>,
                      args: windows::core::Ref<GattValueChangedEventArgs>| {
                    if let Some(args) = args.as_ref()
                        && let Ok(bytes) = args.CharacteristicValue().and_then(|b| bytes_of(&b))
                    {
                        sink.push(&bytes);
                    }
                    Ok(())
                },
            ))
            .map_err(|e| failed(&e))?;
        let subscription = Subscription {
            characteristic: notify.clone(),
            token,
        };

        let value = if indicate {
            GattClientCharacteristicConfigurationDescriptorValue::Indicate
        } else {
            GattClientCharacteristicConfigurationDescriptorValue::Notify
        };
        let status = notify
            .WriteClientCharacteristicConfigurationDescriptorAsync(value)
            .and_then(|operation| operation.join());

        match status {
            Ok(GattCommunicationStatus::Success) => {
                self.subscription = Some(subscription);
                Ok(())
            }
            other => {
                let _ = subscription.characteristic.RemoveValueChanged(subscription.token);
                Err(match other {
                    Ok(status) => status_text(status),
                    Err(e) => failed(&e),
                })
            }
        }
    }

    fn write_piece(
        &self,
        service: usize,
        characteristic: usize,
        data: &[u8],
        with_response: bool,
    ) -> std::result::Result<(), String> {
        let target = &self.services[service].characteristics[characteristic];

        let writer = DataWriter::new().map_err(|e| failed(&e))?;
        writer.WriteBytes(data).map_err(|e| failed(&e))?;
        let buffer = writer.DetachBuffer().map_err(|e| failed(&e))?;

        let option = if with_response {
            GattWriteOption::WriteWithResponse
        } else {
            GattWriteOption::WriteWithoutResponse
        };
        let sent = target
            .WriteValueWithOptionAsync(&buffer, option)
            .and_then(|operation| operation.join())
            .map_err(|e| failed(&e))?;

        if sent == GattCommunicationStatus::Success {
            Ok(())
        } else {
            Err(status_text(sent))
        }
    }
}

impl GattLink for Link {
    fn services(&mut self) -> std::result::Result<Vec<ServiceInfo>, String> {
        in_mta(|| self.list())
    }

    fn subscribe(&mut self, service: usize, characteristic: usize, indicate: bool) -> std::result::Result<(), String> {
        in_mta(|| self.subscribe_to(service, characteristic, indicate))
    }

    fn unsubscribe(&mut self, service: usize, _characteristic: usize) {
        // `notify.ValueChanged -= OnNotified`, and nothing else: the C# did not switch the
        // descriptor off.
        self.remove_handler();
        // A service that did not answer is let go of cleanly; the one in use, on close.
        if let Some(held) = self.services.get_mut(service) {
            held.close();
        }
    }

    fn write(
        &mut self,
        service: usize,
        characteristic: usize,
        data: &[u8],
        with_response: bool,
    ) -> std::result::Result<(), String> {
        in_mta(|| self.write_piece(service, characteristic, data, with_response))
    }
}

impl Drop for Link {
    fn drop(&mut self) {
        self.remove_handler();
        // The services before the device, and all of them without fail: either left open holds the
        // connection past the life of this object, and the symptom lands on whatever connects next.
        self.services.iter_mut().for_each(Held::close);
        // A device that has gone out of range cannot be closed politely.
        let _ = self.device.Close();
    }
}

// ---------------------------------------------------------------------------------------------
// Scanning.
// ---------------------------------------------------------------------------------------------

type Heard = Vec<(u64, Option<String>)>;

/// The addresses (and names) heard advertising during `window`: the C#'s `AdvertisingAsync`, an
/// active `BluetoothLEAdvertisementWatcher` run for the window.
///
/// Hearing nothing is the right answer to no radio or one switched off, so no failure is an error.
pub(crate) fn scan(window: Duration) -> Result<Vec<(u64, Option<String>)>> {
    let heard: Arc<Mutex<Heard>> = Arc::default();

    in_mta(|| {
        let Ok(watcher) = BluetoothLEAdvertisementWatcher::new() else {
            return;
        };
        let _ = watcher.SetScanningMode(BluetoothLEScanningMode::Active);

        let sink = heard.clone();
        let token = watcher.Received(&TypedEventHandler::new(
            move |_: windows::core::Ref<BluetoothLEAdvertisementWatcher>,
                  args: windows::core::Ref<BluetoothLEAdvertisementReceivedEventArgs>| {
                if let Some(args) = args.as_ref()
                    && let Ok(address) = args.BluetoothAddress()
                {
                    let name = args
                        .Advertisement()
                        .and_then(|a| a.LocalName())
                        .map(|n| n.to_string())
                        .ok()
                        .filter(|n| !n.is_empty());
                    sink.lock().unwrap_or_else(|e| e.into_inner()).push((address, name));
                }
                Ok(())
            },
        ));

        if watcher.Start().is_ok() {
            thread::sleep(window);
        }
        let _ = watcher.Stop();
        if let Ok(token) = token {
            let _ = watcher.RemoveReceived(token);
        }
    });

    // Each address once, with the first name heard for it.
    let mut seen: Vec<(u64, Option<String>)> = Vec::new();
    for (address, name) in std::mem::take(&mut *heard.lock().unwrap_or_else(|e| e.into_inner())) {
        match seen.iter_mut().find(|(a, _)| *a == address) {
            Some(existing) => {
                if existing.1.is_none() {
                    existing.1 = name;
                }
            }
            None => seen.push((address, name)),
        }
    }
    Ok(seen)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transport::ble_ecu_transport::uuid16;

    #[test]
    fn a_winrt_guid_is_the_same_uuid() {
        let guid = GUID::from_u128(0x0000_fff0_0000_1000_8000_0080_5f9b_34fb);
        assert_eq!(uuid_of(guid), uuid16(0xFFF0));
        assert_eq!(uuid_of(guid), SERIAL_SERVICES[0]);
        assert_eq!(
            uuid_of(GUID::from_u128(SERIAL_SERVICES[3].as_u128())),
            SERIAL_SERVICES[3]
        );
    }

    #[test]
    fn a_status_is_named_as_dotnet_printed_it() {
        // The C# put the enum's ToString() in its messages ("did not list its services (Unreachable)").
        assert_eq!(status_text(GattCommunicationStatus::Success), "Success");
        assert_eq!(status_text(GattCommunicationStatus::Unreachable), "Unreachable");
        assert_eq!(status_text(GattCommunicationStatus::ProtocolError), "ProtocolError");
        assert_eq!(status_text(GattCommunicationStatus::AccessDenied), "AccessDenied");
        assert_eq!(status_name(9), "9");
    }

    #[test]
    fn only_the_known_serial_services_are_candidates() {
        assert!(is_candidate(uuid16(0xFFE0)));
        assert!(!is_candidate(uuid16(0x180F)));
    }

    #[test]
    fn winrt_property_flags_read_as_the_c_sharp_tested_them() {
        use windows::Devices::Bluetooth::GenericAttributeProfile::GattCharacteristicProperties as P;
        let props = CharProps::from_bits((P::Write | P::Notify).0);
        assert!(props.write && props.notify && !props.write_without_response && !props.indicate);
        let props = CharProps::from_bits((P::WriteWithoutResponse | P::Indicate).0);
        assert!(props.write_without_response && props.indicate && !props.write && !props.notify);
    }

    #[test]
    #[ignore = "asks Windows for a device that is not there; run by hand on a machine with a radio"]
    fn a_device_that_is_not_there_is_reported_not_hung_on() {
        // The plumbing end to end without hardware: the apartment, the thread hop, the blocking
        // wait on a WinRT operation, and the status text.
        let started = std::time::Instant::now();
        let connected = Backend.connect(0x00FF_FFFF_FFFE, Arc::new(Incoming::default()));
        match connected {
            Ok(None) | Err(_) => {}
            Ok(Some(mut link)) => {
                let listed = link.services();
                eprintln!("listed: {listed:?}");
                assert!(listed.is_err() || listed.is_ok_and(|s| s.is_empty()));
            }
        }
        eprintln!("took {:?}", started.elapsed());
    }
}
