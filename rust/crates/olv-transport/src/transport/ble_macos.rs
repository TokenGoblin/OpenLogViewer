//! Bluetooth LE on macOS, over CoreBluetooth through `objc2-core-bluetooth` (Zlib / Apache-2.0 /
//! MIT), `objc2-foundation`, `objc2` and `dispatch2`.
//!
//! **This file has only been type-checked** (`cargo check --target aarch64-apple-darwin`); it has
//! never run. Everything it does is on the list in `ble_ecu_transport`'s "differences the bench must
//! confirm".
//!
//! # How CoreBluetooth is bridged
//!
//! CoreBluetooth is callback based: `connectPeripheral:` returns at once and the outcome arrives
//! later at a delegate. [`GattLink`] is blocking, so each call here starts an operation and then
//! waits (with a timeout) on a [`Mailbox`] that the delegate callbacks fill in. One delegate object
//! ([`Delegate`]) serves as both the central manager's and the peripheral's delegate.
//!
//! # Threads and the main run loop
//!
//! * The `CBCentralManager` is created with a **serial dispatch queue of our own**, not `nil`.
//!   With `nil` its callbacks go to the *main* queue, which only runs if the main thread is
//!   spinning its run loop, and which would deadlock a caller that blocks the main thread while it
//!   waits for the result. With a private queue the callbacks arrive on that queue whatever the
//!   caller is doing, so these blocking calls work from any thread, the main one included, and no
//!   main run loop is needed for BLE to function.
//! * CoreBluetooth's own methods (`scan`, `connect`, `writeValue`, `setNotifyValue`...) may be
//!   called from any thread; they hand the work to the queue.
//! * The delegate is only ever called on that queue, and only touches the [`Mailbox`], which is a
//!   mutex. It never calls back into the transport while holding the lock, and the notification
//!   sink is pushed to after the lock is released.
//! * On teardown the delegates are set to `nil` and the queue is drained before the delegate is
//!   released, so a callback cannot arrive at a freed object.
//!
//! # Permission
//!
//! macOS 11 and later asks the user before an app may use Bluetooth, and **terminates a process
//! that touches CoreBluetooth when its bundle has no `NSBluetoothAlwaysUsageDescription`** in
//! `Info.plist`. The shipped app bundle must carry that key. A bare binary run from a terminal is
//! attributed to the terminal app, which needs the permission instead. A refusal shows up here as
//! the manager state `Unauthorized`, reported with the System Settings path to fix it.
//!
//! # Addresses
//!
//! CoreBluetooth never reveals a radio address. A peripheral is known by `CBPeripheral.identifier`
//! (an `NSUUID`, stable on one Mac for one peripheral, different on another Mac). The transport's
//! `u64` "address" is therefore the 48-bit hash of that identifier from
//! `ble_devices::address_from_peripheral_id` (the same mapping the earlier `btleplug`-based code
//! used: FNV-1a of the lowercase hyphenated identifier, folded to 48 bits). Connecting by address
//! means finding the peripheral again: first among those the system already has connected, then by
//! scanning for up to ten seconds, so the adapter must be advertising. The numbers in a saved
//! connection are only meaningful on the Mac that made them.
//!
//! # Notify or indicate
//!
//! `setNotifyValue:forCharacteristic:` is the whole API: it writes the client configuration
//! descriptor itself and offers no way to ask for indication rather than notification. The
//! transport still picks the characteristic the C# would (notify wherever it is offered, indicate
//! only otherwise), but which of the two CoreBluetooth then writes when a characteristic offers
//! both is not documented by Apple and cannot be influenced. The bench must confirm it against a
//! clone that advertises both.
//!
//! # Differences from the Windows calls
//!
//! There is no cache switch: `discoverServices:` and `discoverCharacteristics:` may be answered
//! from CoreBluetooth's cache for a peripheral it has seen. None of the C#'s per-call timeouts exist
//! in CoreBluetooth at all (it waits forever), so these are ours: see the constants.
//!
//! # Unsafe
//!
//! The generated bindings declare almost every Objective-C method `unsafe`, because nothing checks
//! that the arguments are what the method expects. Each item that calls one is `#[allow(unsafe_code)]`
//! with a `// SAFETY:` note; the arguments are always objects the bindings returned, with the
//! right types. Two `unsafe impl Send` cover the CoreBluetooth objects that cross threads.

use std::collections::HashMap;
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use dispatch2::{DispatchQueue, DispatchQueueAttr, DispatchRetained};
use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2::{AnyThread, DefinedClass, Message, define_class, msg_send};
use objc2_core_bluetooth::{
    CBCentralManager, CBCentralManagerDelegate, CBCharacteristic, CBCharacteristicWriteType, CBManagerState,
    CBPeripheral, CBPeripheralDelegate, CBPeripheralState, CBService, CBUUID,
};
use objc2_foundation::{NSArray, NSData, NSDictionary, NSError, NSNumber, NSObject, NSObjectProtocol, NSString};
use olv_core::{Error, Result};

use crate::transport::ble_devices::address_from_peripheral_id;
use crate::transport::ble_ecu_transport::{
    BleBackend, CharProps, GattCharacteristic as CharInfo, GattLink, GattService as ServiceInfo, Incoming, Mailbox,
    Outcomes, SERIAL_SERVICES, uuid_from_cb_string,
};

/// How long the manager may take to report whether Bluetooth is usable.
const STATE_WITHIN: Duration = Duration::from_secs(5);
/// How long a scan may run looking for a device it cannot connect to by address.
const FIND_WITHIN: Duration = Duration::from_secs(10);
/// How long a connection may take. CoreBluetooth waits forever by default.
const CONNECT_WITHIN: Duration = Duration::from_secs(10);
/// How long service and characteristic discovery may each take.
const DISCOVER_WITHIN: Duration = Duration::from_secs(10);
/// How long the descriptor write behind `setNotifyValue:` may take.
const NOTIFY_WITHIN: Duration = Duration::from_secs(5);
/// How long a write with response may wait for the device's reply.
const WRITE_WITHIN: Duration = Duration::from_secs(5);
/// How long a write without response may wait for room in the radio's queue.
const READY_WITHIN: Duration = Duration::from_secs(3);

fn io(message: impl Into<String>) -> Error {
    Error::Io(std::io::Error::other(message.into()))
}

// ---------------------------------------------------------------------------------------------
// What the delegate reports.
// ---------------------------------------------------------------------------------------------

/// A peripheral handle that may be held and used from any thread.
struct Peripheral(Retained<CBPeripheral>);

// SAFETY: a `CBPeripheral` is documented as usable from any thread: its methods hand their work
// to the central manager's dispatch queue, and its properties are read-only snapshots. The wrapper
// only ever calls those.
#[allow(unsafe_code)]
unsafe impl Send for Peripheral {}

struct Found {
    peripheral: Peripheral,
    address: u64,
    name: Option<String>,
}

#[derive(Default)]
struct State {
    /// `CBManagerState` as the raw integer; 0 is unknown.
    manager_state: isize,
    found: Vec<Found>,
    connect: Option<std::result::Result<(), String>>,
    disconnected: bool,
    services: Option<std::result::Result<(), String>>,
    /// The outcome of `discoverCharacteristics:forService:`, by the service's address.
    characteristics: HashMap<usize, std::result::Result<(), String>>,
    /// Outcomes of `setNotifyValue:` and of writes with response, by characteristic address, so
    /// a late reply for one characteristic is never taken for another's.
    notify: Outcomes,
    written: Outcomes,
    can_write_without_response: bool,
    /// The characteristic whose values go to the sink (by address), and the sink.
    forward: Option<(usize, Arc<Incoming>)>,
}

type Shared = Arc<Mailbox<State>>;

fn outcome(error: Option<&NSError>) -> std::result::Result<(), String> {
    match error {
        None => Ok(()),
        Some(e) => Err(e.localizedDescription().to_string()),
    }
}

define_class!(
    // SAFETY:
    // - The superclass `NSObject` has no subclassing requirements.
    // - `Delegate` does not implement `Drop`.
    #[unsafe(super = NSObject)]
    #[name = "OlvBleDelegate"]
    #[ivars = Shared]
    struct Delegate;

    // SAFETY: `NSObjectProtocol` has no safety requirements.
    unsafe impl NSObjectProtocol for Delegate {}

    // SAFETY: `CBCentralManagerDelegate` has no safety requirements; each signature is the
    // protocol's.
    #[allow(non_snake_case)]
    unsafe impl CBCentralManagerDelegate for Delegate {
        #[unsafe(method(centralManagerDidUpdateState:))]
        fn centralManagerDidUpdateState(&self, central: &CBCentralManager) {
            // SAFETY: `state` is a plain read on a live manager.
            #[allow(unsafe_code)]
            let state = unsafe { central.state() }.0;
            self.ivars().update(|s| s.manager_state = state);
        }

        #[unsafe(method(centralManager:didDiscoverPeripheral:advertisementData:RSSI:))]
        fn centralManager_didDiscoverPeripheral_advertisementData_RSSI(
            &self,
            _central: &CBCentralManager,
            peripheral: &CBPeripheral,
            _advertisement_data: &NSDictionary<NSString, objc2::runtime::AnyObject>,
            _rssi: &NSNumber,
        ) {
            // SAFETY: `identifier` and `name` are plain reads on a live peripheral.
            #[allow(unsafe_code)]
            let (id, name) = unsafe {
                (
                    peripheral.identifier().UUIDString().to_string(),
                    peripheral.name().map(|n| n.to_string()),
                )
            };
            let address = address_from_peripheral_id(&id);
            let peripheral = peripheral.retain();
            self.ivars()
                .update(|s| match s.found.iter_mut().find(|f| f.address == address) {
                    Some(known) => {
                        if known.name.is_none() {
                            known.name = name;
                        }
                    }
                    None => s.found.push(Found {
                        peripheral: Peripheral(peripheral),
                        address,
                        name,
                    }),
                });
        }

        #[unsafe(method(centralManager:didConnectPeripheral:))]
        fn centralManager_didConnectPeripheral(&self, _central: &CBCentralManager, _peripheral: &CBPeripheral) {
            self.ivars().update(|s| {
                s.connect = Some(Ok(()));
                s.disconnected = false;
            });
        }

        #[unsafe(method(centralManager:didFailToConnectPeripheral:error:))]
        fn centralManager_didFailToConnectPeripheral_error(
            &self,
            _central: &CBCentralManager,
            _peripheral: &CBPeripheral,
            error: Option<&NSError>,
        ) {
            let result = outcome(error).and(Err("the connection failed".to_string()));
            self.ivars().update(|s| s.connect = Some(result));
        }

        #[unsafe(method(centralManager:didDisconnectPeripheral:error:))]
        fn centralManager_didDisconnectPeripheral_error(
            &self,
            _central: &CBCentralManager,
            _peripheral: &CBPeripheral,
            _error: Option<&NSError>,
        ) {
            self.ivars().update(|s| s.disconnected = true);
        }
    }

    // SAFETY: `CBPeripheralDelegate` has no safety requirements; each signature is the
    // protocol's.
    #[allow(non_snake_case)]
    unsafe impl CBPeripheralDelegate for Delegate {
        #[unsafe(method(peripheral:didDiscoverServices:))]
        fn peripheral_didDiscoverServices(&self, _peripheral: &CBPeripheral, error: Option<&NSError>) {
            let result = outcome(error);
            self.ivars().update(|s| s.services = Some(result));
        }

        #[unsafe(method(peripheral:didDiscoverCharacteristicsForService:error:))]
        fn peripheral_didDiscoverCharacteristicsForService_error(
            &self,
            _peripheral: &CBPeripheral,
            service: &CBService,
            error: Option<&NSError>,
        ) {
            let result = outcome(error);
            let key = std::ptr::from_ref(service) as usize;
            self.ivars().update(|s| {
                s.characteristics.insert(key, result);
            });
        }

        #[unsafe(method(peripheral:didUpdateValueForCharacteristic:error:))]
        fn peripheral_didUpdateValueForCharacteristic_error(
            &self,
            _peripheral: &CBPeripheral,
            characteristic: &CBCharacteristic,
            error: Option<&NSError>,
        ) {
            if error.is_some() {
                return;
            }
            let key = std::ptr::from_ref(characteristic) as usize;
            let sink = self.ivars().read(|s| {
                s.forward
                    .as_ref()
                    .filter(|(k, _)| *k == key)
                    .map(|(_, sink)| sink.clone())
            });
            let Some(sink) = sink else { return };

            // SAFETY: `value` is a plain read on a live characteristic.
            #[allow(unsafe_code)]
            let value = unsafe { characteristic.value() };
            if let Some(data) = value {
                // Pushed after the lock is released, so the reader that wakes is not blocked on it.
                sink.push(&data.to_vec());
            }
        }

        #[unsafe(method(peripheral:didWriteValueForCharacteristic:error:))]
        fn peripheral_didWriteValueForCharacteristic_error(
            &self,
            _peripheral: &CBPeripheral,
            characteristic: &CBCharacteristic,
            error: Option<&NSError>,
        ) {
            let result = outcome(error);
            let key = std::ptr::from_ref(characteristic) as usize;
            self.ivars().update(|s| s.written.post(key, result));
        }

        #[unsafe(method(peripheral:didUpdateNotificationStateForCharacteristic:error:))]
        fn peripheral_didUpdateNotificationStateForCharacteristic_error(
            &self,
            _peripheral: &CBPeripheral,
            characteristic: &CBCharacteristic,
            error: Option<&NSError>,
        ) {
            let result = outcome(error);
            let key = std::ptr::from_ref(characteristic) as usize;
            self.ivars().update(|s| s.notify.post(key, result));
        }

        #[unsafe(method(peripheralIsReadyToSendWriteWithoutResponse:))]
        fn peripheralIsReadyToSendWriteWithoutResponse(&self, _peripheral: &CBPeripheral) {
            self.ivars().update(|s| s.can_write_without_response = true);
        }
    }
);

#[allow(unsafe_code)]
fn new_delegate(shared: Shared) -> Retained<Delegate> {
    let this = Delegate::alloc().set_ivars(shared);
    // SAFETY: `init` on a freshly allocated `NSObject` subclass whose ivars are set.
    unsafe { msg_send![super(this), init] }
}

// ---------------------------------------------------------------------------------------------
// The central manager.
// ---------------------------------------------------------------------------------------------

/// A `CBCentralManager`, its delegate and the queue the delegate runs on.
struct Central {
    manager: Retained<CBCentralManager>,
    delegate: Retained<Delegate>,
    shared: Shared,
    queue: DispatchRetained<DispatchQueue>,
}

// SAFETY: see `Peripheral`. The manager's methods may be called from any thread; the delegate is
// only ever called on `queue`, and reaches the rest of the program only through the `Mailbox`,
// which is a mutex.
#[allow(unsafe_code)]
unsafe impl Send for Central {}

impl Central {
    /// A manager on a private serial queue, once Bluetooth reports it is usable.
    #[allow(unsafe_code)]
    fn new() -> Result<Self> {
        let shared: Shared = Arc::new(Mailbox::new(State::default()));
        let delegate = new_delegate(shared.clone());
        let queue = DispatchQueue::new("com.openlogviewer.ble", DispatchQueueAttr::SERIAL);

        // SAFETY: the delegate and queue are live objects of the right types, and the queue is a
        // serial one of our own, so the callbacks do not depend on the main run loop.
        let manager = unsafe {
            CBCentralManager::initWithDelegate_queue(
                CBCentralManager::alloc(),
                Some(ProtocolObject::from_ref(&*delegate)),
                Some(&queue),
            )
        };
        let central = Self {
            manager,
            delegate,
            shared,
            queue,
        };

        // Not yet known or resetting are transient; anything else is the answer.
        let state = central.shared.wait_for(STATE_WITHIN, |s| {
            (s.manager_state != CBManagerState::Unknown.0 && s.manager_state != CBManagerState::Resetting.0)
                .then_some(s.manager_state)
        });
        match state {
            Some(s) if s == CBManagerState::PoweredOn.0 => Ok(central),
            Some(s) if s == CBManagerState::Unauthorized.0 => Err(io(
                "Bluetooth is not allowed for this app. Allow it in System Settings > Privacy & Security > Bluetooth.",
            )),
            Some(s) if s == CBManagerState::PoweredOff.0 => Err(io("Bluetooth is turned off.")),
            Some(s) if s == CBManagerState::Unsupported.0 => Err(io("This computer has no Bluetooth adapter.")),
            _ => Err(io("Bluetooth is unavailable (it did not report its state).")),
        }
    }

    /// Scans with no service filter. An advertisement need not list its services (HM-10 clones
    /// often do not), and the address is a hash of the identifier, so the match is made on that.
    #[allow(unsafe_code)]
    fn start_scan(&self) {
        // SAFETY: no filter and no options are valid arguments.
        unsafe { self.manager.scanForPeripheralsWithServices_options(None, None) };
    }

    #[allow(unsafe_code)]
    fn stop_scan(&self) {
        // SAFETY: stopping when not scanning is a no-op.
        unsafe { self.manager.stopScan() };
    }

    /// The peripheral whose identifier hashes to `address`: among those the system already holds
    /// connected (which do not advertise), else by scanning for up to [`FIND_WITHIN`].
    #[allow(unsafe_code)]
    fn find(&self, address: u64) -> Option<Peripheral> {
        let services: Vec<Retained<CBUUID>> = SERIAL_SERVICES
            .iter()
            // SAFETY: a valid UUID string.
            .map(|u| unsafe { CBUUID::UUIDWithString(&NSString::from_str(&u.to_string())) })
            .collect();
        let services = NSArray::from_retained_slice(&services);
        // SAFETY: an array of `CBUUID`.
        let connected = unsafe { self.manager.retrieveConnectedPeripheralsWithServices(&services) };
        for p in connected.to_vec() {
            // SAFETY: a plain read on a live peripheral.
            let id = unsafe { p.identifier().UUIDString().to_string() };
            if address_from_peripheral_id(&id) == address {
                return Some(Peripheral(p));
            }
        }

        self.start_scan();
        let found = self.shared.wait_for(FIND_WITHIN, |s| {
            s.found
                .iter()
                .find(|f| f.address == address)
                .map(|f| Peripheral(f.peripheral.0.clone()))
        });
        self.stop_scan();
        found
    }
}

impl Drop for Central {
    #[allow(unsafe_code)]
    fn drop(&mut self) {
        self.stop_scan();
        // SAFETY: clearing the delegate is always valid; after this the manager calls nothing.
        unsafe { self.manager.setDelegate(None) };
        // Whatever the queue was already running finishes before the delegate can be released.
        self.queue.exec_sync(|| {});
        let _ = &self.delegate;
    }
}

/// The addresses (and names) heard advertising during `window`.
pub(crate) fn scan(window: Duration) -> Result<Vec<(u64, Option<String>)>> {
    let central = Central::new()?;
    central.start_scan();
    thread::sleep(window);
    central.stop_scan();
    Ok(central
        .shared
        .read(|s| s.found.iter().map(|f| (f.address, f.name.clone())).collect()))
}

// ---------------------------------------------------------------------------------------------
// The backend.
// ---------------------------------------------------------------------------------------------

struct Backend;

pub(crate) fn backend() -> Arc<dyn BleBackend> {
    Arc::new(Backend)
}

impl BleBackend for Backend {
    #[allow(unsafe_code)]
    fn connect(&self, address: u64, sink: Arc<Incoming>) -> Result<Option<Box<dyn GattLink>>> {
        let central = Central::new()?;
        let Some(Peripheral(peripheral)) = central.find(address) else {
            return Ok(None);
        };

        central.shared.update(|s| {
            s.connect = None;
            s.disconnected = false;
        });
        // SAFETY: the delegate is the one the manager was made with, kept alive by `central`;
        // the peripheral came from this manager.
        unsafe {
            peripheral.setDelegate(Some(ProtocolObject::from_ref(&*central.delegate)));
            central.manager.connectPeripheral_options(&peripheral, None);
        }

        // A device that will not connect is a device that could not be reached.
        let connected = central.shared.wait_for(CONNECT_WITHIN, |s| s.connect.take());
        if !matches!(connected, Some(Ok(()))) {
            // SAFETY: cancelling a connection that was never made is a no-op.
            unsafe { central.manager.cancelPeripheralConnection(&peripheral) };
            return Ok(None);
        }

        Ok(Some(Box::new(Link {
            central,
            peripheral,
            services: Vec::new(),
            sink,
        })))
    }
}

struct Link {
    central: Central,
    peripheral: Retained<CBPeripheral>,
    /// Parallel to the `GattService`s given out.
    services: Vec<(Retained<CBService>, Vec<Retained<CBCharacteristic>>)>,
    sink: Arc<Incoming>,
}

// SAFETY: see `Peripheral` and `Central`: the services and characteristics are CoreBluetooth
// objects used only through methods that hand their work to the queue.
#[allow(unsafe_code)]
unsafe impl Send for Link {}

impl Link {
    #[allow(unsafe_code)]
    fn connected(&self) -> std::result::Result<(), String> {
        // SAFETY: a plain read on a live peripheral.
        let state = unsafe { self.peripheral.state() };
        if state == CBPeripheralState::Connected && !self.central.shared.read(|s| s.disconnected) {
            Ok(())
        } else {
            Err("the device is no longer connected".to_string())
        }
    }

    #[allow(unsafe_code)]
    fn list(&mut self) -> std::result::Result<Vec<ServiceInfo>, String> {
        let shared = self.central.shared.clone();

        shared.update(|s| s.services = None);
        // SAFETY: no filter is a valid argument.
        unsafe { self.peripheral.discoverServices(None) };
        match shared.wait_for(DISCOVER_WITHIN, |s| s.services.take()) {
            None => return Err("timed out discovering the services".to_string()),
            Some(Err(e)) => return Err(e),
            Some(Ok(())) => {}
        }

        // SAFETY: a plain read on a live peripheral.
        let discovered = unsafe { self.peripheral.services() }
            .map(|a| a.to_vec())
            .unwrap_or_default();

        self.services.clear();
        let mut out = Vec::new();
        for service in discovered {
            // SAFETY: a plain read on a live service.
            let text = unsafe { service.UUID().UUIDString().to_string() };
            let uuid =
                uuid_from_cb_string(&text).ok_or_else(|| format!("a service with an unreadable UUID ({text})"))?;

            // Only the services that might be used have their characteristics discovered, as in
            // the Windows backend. A service whose characteristics cannot be discovered is not a
            // candidate, which an empty list says.
            let mut characteristics = Vec::new();
            if SERIAL_SERVICES.contains(&uuid) {
                let key = Retained::as_ptr(&service) as usize;
                shared.update(|s| {
                    s.characteristics.remove(&key);
                });
                // SAFETY: no filter is a valid argument; the service came from this peripheral.
                unsafe { self.peripheral.discoverCharacteristics_forService(None, &service) };
                if let Some(Ok(())) = shared.wait_for(DISCOVER_WITHIN, |s| s.characteristics.remove(&key)) {
                    // SAFETY: a plain read on a live service.
                    characteristics = unsafe { service.characteristics() }
                        .map(|a| a.to_vec())
                        .unwrap_or_default();
                }
            }

            let infos = characteristics
                .iter()
                .map(|c| {
                    // SAFETY: plain reads on a live characteristic.
                    let (text, bits) = unsafe { (c.UUID().UUIDString().to_string(), c.properties().0) };
                    Ok(CharInfo {
                        uuid: uuid_from_cb_string(&text)
                            .ok_or_else(|| format!("a characteristic with an unreadable UUID ({text})"))?,
                        props: CharProps::from_bits(bits as u32),
                    })
                })
                .collect::<std::result::Result<Vec<_>, String>>()?;

            out.push(ServiceInfo {
                uuid,
                characteristics: infos,
            });
            self.services.push((service, characteristics));
        }
        Ok(out)
    }
}

impl GattLink for Link {
    fn services(&mut self) -> std::result::Result<Vec<ServiceInfo>, String> {
        self.connected()?;
        self.list()
    }

    #[allow(unsafe_code)]
    fn subscribe(&mut self, service: usize, characteristic: usize, _indicate: bool) -> std::result::Result<(), String> {
        // `_indicate` has no CoreBluetooth equivalent: see the module notes.
        self.connected()?;
        let target = self.services[service].1[characteristic].clone();
        let shared = self.central.shared.clone();

        // Listening first, so nothing sent in reply to the first write is missed.
        let key = Retained::as_ptr(&target) as usize;
        let sink = self.sink.clone();
        shared.update(|s| {
            s.forward = Some((key, sink));
            s.notify.expect(key);
        });

        // SAFETY: the characteristic came from this peripheral.
        unsafe { self.peripheral.setNotifyValue_forCharacteristic(true, &target) };

        let result = match shared.wait_for(NOTIFY_WITHIN, |s| s.notify.take(key)) {
            None => Err("timed out enabling notifications".to_string()),
            Some(result) => result,
        };
        if result.is_err() {
            shared.update(|s| s.forward = None);
        }
        result
    }

    #[allow(unsafe_code)]
    fn unsubscribe(&mut self, service: usize, characteristic: usize) {
        let shared = self.central.shared.clone();
        shared.update(|s| s.forward = None);
        if let Some(target) = self.services.get(service).and_then(|s| s.1.get(characteristic)) {
            let key = Retained::as_ptr(target) as usize;
            shared.update(|s| s.notify.expect(key));
            // SAFETY: the characteristic came from this peripheral. Best effort: a device that is
            // gone cannot be told.
            unsafe { self.peripheral.setNotifyValue_forCharacteristic(false, target) };
            // Its callback is awaited, so it is over before the next service is subscribed to; and
            // it is keyed anyway, so even a late one could not be taken for that subscription's.
            if self.connected().is_ok() {
                let _ = shared.wait_for(NOTIFY_WITHIN, |s| s.notify.take(key));
            }
        }
    }

    #[allow(unsafe_code)]
    fn write(
        &mut self,
        service: usize,
        characteristic: usize,
        data: &[u8],
        with_response: bool,
    ) -> std::result::Result<(), String> {
        self.connected()?;
        let target = self.services[service].1[characteristic].clone();
        let shared = self.central.shared.clone();
        let payload = NSData::with_bytes(data);

        if with_response {
            let key = Retained::as_ptr(&target) as usize;
            shared.update(|s| s.written.expect(key));
            // SAFETY: the characteristic came from this peripheral.
            unsafe {
                self.peripheral.writeValue_forCharacteristic_type(
                    &payload,
                    &target,
                    CBCharacteristicWriteType::WithResponse,
                );
            }
            return match shared.wait_for(WRITE_WITHIN, |s| s.written.take(key)) {
                None => Err("timed out waiting for the device to accept the write".to_string()),
                Some(result) => result,
            };
        }

        // A write without response is silently dropped if the radio has no room, so wait for it.
        // Cleared first, so a "ready" that arrives between the check and the wait is not lost.
        shared.update(|s| s.can_write_without_response = false);
        // SAFETY: a plain read on a live peripheral.
        if !unsafe { self.peripheral.canSendWriteWithoutResponse() }
            && shared
                .wait_for(READY_WITHIN, |s| s.can_write_without_response.then_some(()))
                .is_none()
        {
            return Err("timed out waiting for room to send".to_string());
        }
        // SAFETY: the characteristic came from this peripheral.
        unsafe {
            self.peripheral.writeValue_forCharacteristic_type(
                &payload,
                &target,
                CBCharacteristicWriteType::WithoutResponse,
            );
        }
        Ok(())
    }
}

impl Drop for Link {
    #[allow(unsafe_code)]
    fn drop(&mut self) {
        self.central.shared.update(|s| s.forward = None);
        // SAFETY: the peripheral came from this manager. A device that has gone out of range
        // cannot be closed politely; cancelling is best effort.
        unsafe {
            self.central.manager.cancelPeripheralConnection(&self.peripheral);
            self.peripheral.setDelegate(None);
        }
        // `central` drops next, which clears the manager's delegate and drains the queue.
    }
}
