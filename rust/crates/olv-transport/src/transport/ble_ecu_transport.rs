//! Ported from `BleEcuTransport.cs`. Owned by package `w2-transports`.
//!
//! A Bluetooth Low Energy adapter, presented as a byte stream.
//!
//! Many OBD2 dongles (and by now most cheap ones) are BLE rather than Bluetooth Classic. BLE has
//! no serial port profile, so these never become a COM port however long you wait for one, which
//! is a confusing way for a working dongle to fail.
//!
//! What they do instead is carry the same ASCII ELM327 conversation over two GATT
//! characteristics: bytes written to one, replies arriving as notifications on the other. That
//! is a byte stream in all but name, so it is presented as one.
//!
//! Verified in the C# against an "OBDII" dongle on a live vehicle.
//!
//! # Structure
//!
//! Everything the C# decided (the service list and the order it is tried in, proving a service
//! by asking it something, the retry, the 20-byte write chunks, the write-with or without-response
//! choice, how a read waits) is plain logic here, over two small traits: [`BleBackend`] connects
//! and [`GattLink`] is one connected device's GATT table. The operating system sits behind them:
//!
//! * **Windows and macOS**: `btleplug` (WinRT; CoreBluetooth), driven from a private tokio runtime.
//! * **Linux**: BlueZ over D-Bus with the pure-Rust `zbus` (blocking API). `btleplug`'s own Linux
//!   backend links `libdbus`, which `deny.toml` bans.
//!
//! # Differences the bench must confirm
//!
//! * **None of this has run against a real adapter.** The C# was verified on one; the Rust
//!   shares only its logic.
//! * The C# asked WinRT for the services *uncached* so a stale pairing could not list
//!   characteristics that cannot be subscribed to. `btleplug` discovers on connect and offers no
//!   cache switch; BlueZ resolves services itself. If a reconnect after a crashed session finds
//!   empty characteristics, that is this, and the 3-attempt retry (1.5 s apart) is the cover.
//! * The C# disposed every service it was not using so the link would not linger. Here the
//!   whole connection is dropped on close (and between attempts); nothing is kept per service.
//! * On Windows `btleplug` creates the peripheral straight from its address, as
//!   `FromBluetoothAddressAsync` did, so a paired adapter that is not advertising can still be
//!   reached. On macOS CoreBluetooth has no addresses at all: the "address" is a stable hash of
//!   the peripheral's identifier (see `ble_devices::address_from_id`), and connecting scans for up
//!   to ten seconds until a peripheral with that hash is heard, so the adapter must be
//!   advertising.
//! * On Linux the device must already be known to BlueZ (paired, or seen by a scan), because
//!   BlueZ cannot connect to an address it has never heard of.
//! * Write-with-response is chosen only when the characteristic does not offer
//!   write-without-response, exactly as in the C#. BlueZ's `type` option (`request` or `command`)
//!   needs BlueZ 5.50 or later.

use std::collections::VecDeque;
use std::io;
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::thread;
use std::time::{Duration, Instant};

use olv_core::{Error, Result};
use olv_protocol::ms::ms_protocol::EcuTransport;
use uuid::Uuid;

/// A 16-bit Bluetooth UUID in its full 128-bit form.
pub const fn uuid16(assigned: u16) -> Uuid {
    Uuid::from_u128(((assigned as u128) << 96) | 0x0000_1000_8000_0080_5f9b_34fb)
}

/// Services known to carry an ELM327 conversation, in the order worth trying.
///
/// There is no standard for this: BLE defines no serial profile, so each maker picked a vendor
/// service and the clones copied whichever they were based on. A dongle commonly advertises more
/// than one and answers on only one of them: the adapter this was written against publishes
/// both, and 0xAE00 accepts every write and never replies. So the list is tried in order and the
/// first that actually answers is the one used.
pub const SERIAL_SERVICES: [Uuid; 4] = [
    uuid16(0xFFF0),                                             // Vgate, Viecar, and most of the cheap clones
    uuid16(0xAE00),                                             // LeLink and Carista
    uuid16(0xFFE0),                                             // HM-10 modules, and anything built on one
    Uuid::from_u128(0x6e40_0001_b5a3_f393_e0a9_e50e_24dc_ca9e), // Nordic UART
];

/// How many times to try opening.
///
/// Windows keeps a GATT session alive for a while after the process that made it has gone, and
/// while it lingers the services still list but their characteristics come back empty, so a
/// second attempt a moment later succeeds where the first could not. Reconnecting after a
/// session is the ordinary case, not the exception.
const OPEN_ATTEMPTS: u32 = 3;

/// The default BLE payload, less the three bytes of ATT header.
const CHUNK: usize = 20;

// ---------------------------------------------------------------------------------------------
// What the operating system provides.
// ---------------------------------------------------------------------------------------------

/// What a characteristic can do.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CharProps {
    pub write: bool,
    pub write_without_response: bool,
    pub notify: bool,
    pub indicate: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GattCharacteristic {
    pub uuid: Uuid,
    pub props: CharProps,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GattService {
    pub uuid: Uuid,
    pub characteristics: Vec<GattCharacteristic>,
}

/// One connected device's GATT table. Services and characteristics are addressed by their index
/// in what [`services`](Self::services) returned. Dropping it lets go of the connection.
pub trait GattLink: Send {
    /// The device's services, read from the device. `Err` is the platform's status text.
    fn services(&mut self) -> std::result::Result<Vec<GattService>, String>;

    /// Turns notifications (or indications) on for a characteristic; whatever arrives is pushed
    /// to the sink the link was connected with.
    fn subscribe(&mut self, service: usize, characteristic: usize, indicate: bool) -> std::result::Result<(), String>;

    /// Turns them off again. Best effort.
    fn unsubscribe(&mut self, service: usize, characteristic: usize);

    /// Writes one piece (at most [`CHUNK`] bytes), with or without a response.
    fn write(
        &mut self,
        service: usize,
        characteristic: usize,
        data: &[u8],
        with_response: bool,
    ) -> std::result::Result<(), String>;
}

/// Connects to a BLE device by address.
pub trait BleBackend: Send + Sync {
    /// Connects, with notifications going to `sink`. `Ok(None)` is a device that could not be
    /// reached (unknown, out of range, switched off).
    fn connect(&self, address: u64, sink: Arc<Incoming>) -> Result<Option<Box<dyn GattLink>>>;
}

// ---------------------------------------------------------------------------------------------
// The receive side.
// ---------------------------------------------------------------------------------------------

/// Bytes that have arrived by notification, and a count of notifications not yet waited on.
///
/// The queue and the count share one lock so a read that finds the queue empty and then waits
/// cannot miss a notification that lands between the two.
#[derive(Default)]
pub struct Incoming {
    state: Mutex<IncomingState>,
    arrived: Condvar,
}

#[derive(Default)]
struct IncomingState {
    queue: VecDeque<u8>,
    /// The C#'s semaphore: released once per notification rather than once per byte, because a
    /// waiting read rechecks the queue anyway and the count only has to not be lost.
    permits: usize,
}

impl Incoming {
    fn lock(&self) -> MutexGuard<'_, IncomingState> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Called by a backend for each notification.
    pub fn push(&self, bytes: &[u8]) {
        let mut s = self.lock();
        s.queue.extend(bytes);
        s.permits += 1;
        self.arrived.notify_all();
    }

    /// Moves queued bytes into `out`; returns how many.
    fn drain_into(&self, out: &mut [u8]) -> usize {
        let mut s = self.lock();
        let n = out.len().min(s.queue.len());
        for slot in out.iter_mut().take(n) {
            *slot = s.queue.pop_front().unwrap_or_default();
        }
        n
    }

    /// Waits up to `timeout` for a notification; true if one arrived.
    fn wait(&self, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        let mut s = self.lock();
        loop {
            if s.permits > 0 {
                s.permits -= 1;
                return true;
            }
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                return false;
            }
            s = self.arrived.wait_timeout(s, left).unwrap_or_else(|e| e.into_inner()).0;
        }
    }

    fn clear(&self) {
        let mut s = self.lock();
        s.queue.clear();
        s.permits = 0;
    }
}

// ---------------------------------------------------------------------------------------------
// The transport.
// ---------------------------------------------------------------------------------------------

/// The characteristics in use.
#[derive(Clone, Copy)]
struct Selected {
    service: usize,
    write: usize,
    notify: usize,
    without_response: bool,
}

/// A Bluetooth LE adapter as a byte stream.
pub struct BleEcuTransport {
    address: u64,
    name: String,
    backend: Arc<dyn BleBackend>,
    incoming: Arc<Incoming>,
    link: Option<Box<dyn GattLink>>,
    services: Vec<GattService>,
    selected: Option<Selected>,
    retry_delay: Duration,
    answer_wait: Duration,
}

impl BleEcuTransport {
    /// A transport for the device at `address`, advertising as `name` (for messages; may be
    /// empty), through the platform's BLE stack.
    pub fn new(address: u64, name: &str) -> Self {
        Self::with_backend(address, name, default_backend())
    }

    /// As [`new`](Self::new), with the platform side supplied: a fake in a test.
    pub fn with_backend(address: u64, name: &str, backend: Arc<dyn BleBackend>) -> Self {
        Self {
            address,
            name: name.to_string(),
            backend,
            incoming: Arc::new(Incoming::default()),
            link: None,
            services: Vec::new(),
            selected: None,
            retry_delay: Duration::from_millis(1500),
            answer_wait: Duration::from_secs(3),
        }
    }

    /// How long to wait between failed open attempts (1.5 s in the C#).
    #[must_use]
    pub fn with_retry_delay(mut self, delay: Duration) -> Self {
        self.retry_delay = delay;
        self
    }

    /// How long a candidate service has to answer a carriage return (3 s in the C#).
    #[must_use]
    pub fn with_answer_wait(mut self, wait: Duration) -> Self {
        self.answer_wait = wait;
        self
    }

    /// What the adapter advertises as, for messages.
    pub fn name(&self) -> &str {
        &self.name
    }

    fn describe(&self) -> String {
        if self.name.is_empty() {
            format!("The adapter at {:012X}", self.address)
        } else {
            self.name.clone()
        }
    }

    fn io(message: String) -> Error {
        Error::Io(io::Error::other(message))
    }

    fn open_once(&mut self) -> Result<()> {
        let mut link = self
            .backend
            .connect(self.address, self.incoming.clone())?
            .ok_or_else(|| {
                Self::io(format!(
                    "{} could not be reached. A paired Bluetooth device stays listed whether or \
                     not it is powered, so check the adapter is in the socket and the ignition \
                     is on.",
                    self.describe()
                ))
            })?;

        let services = link.services().map_err(|status| {
            Self::io(format!(
                "{} did not list its services ({status}). \
                 If it is already connected to a phone, disconnect it there first \u{2014} \
                 these adapters accept one connection at a time.",
                self.describe()
            ))
        })?;

        self.link = Some(link);
        self.services = services;

        for wanted in SERIAL_SERVICES {
            let Some(index) = self.services.iter().position(|s| s.uuid == wanted) else {
                continue;
            };
            if self.try_use(index) {
                return Ok(());
            }
        }

        Err(Self::io(format!(
            "{} is connected, but none of its services answered as an ELM327. \
             If a session was open a moment ago, the previous connection may still be \
             letting go \u{2014} wait a few seconds and try again. If it is connected to a \
             phone, disconnect it there first; these accept one connection at a time.\n\n\
             It publishes {} service(s): {}",
            self.describe(),
            self.services.len(),
            self.services
                .iter()
                .map(|s| s.uuid.to_string())
                .collect::<Vec<_>>()
                .join(", ")
        )))
    }

    /// Subscribes to a candidate service, and proves it by asking it something.
    ///
    /// Proving matters. A dongle that publishes two serial services accepts writes on both and
    /// answers on one, so a subscription that succeeds is no evidence at all: without asking,
    /// the silent one gets picked half the time and the session opens onto a dongle that never
    /// says anything.
    fn try_use(&mut self, service: usize) -> bool {
        let chars = &self.services[service].characteristics;

        let write = chars
            .iter()
            .position(|c| c.props.write || c.props.write_without_response);
        let notify = chars.iter().position(|c| c.props.notify || c.props.indicate);
        let (Some(write), Some(notify)) = (write, notify) else {
            return false;
        };

        let without_response = chars[write].props.write_without_response;
        let indicate = !chars[notify].props.notify;

        let Some(link) = self.link.as_mut() else {
            return false;
        };
        if link.subscribe(service, notify, indicate).is_err() {
            return false;
        }

        self.selected = Some(Selected {
            service,
            write,
            notify,
            without_response,
        });

        if self.answers() {
            return true;
        }

        // Silent. Let go of it cleanly and try the next.
        if let Some(link) = self.link.as_mut() {
            link.unsubscribe(service, notify);
        }
        self.selected = None;
        false
    }

    /// Whether anything at all comes back from a carriage return.
    fn answers(&mut self) -> bool {
        self.discard_input();
        if self.write(b"\r").is_err() {
            return false;
        }

        let mut one = [0u8; 1];
        matches!(self.read(&mut one, self.answer_wait), Ok(1))
    }
}

impl EcuTransport for BleEcuTransport {
    fn is_open(&self) -> bool {
        self.selected.is_some()
    }

    fn open(&mut self) -> Result<()> {
        if self.is_open() {
            return Ok(());
        }

        for _ in 1..OPEN_ATTEMPTS {
            match self.open_once() {
                Ok(()) => return Ok(()),
                Err(Error::Io(_)) => {
                    self.close();
                    thread::sleep(self.retry_delay);
                }
                Err(e) => return Err(e),
            }
        }

        let result = self.open_once();
        if result.is_err() {
            self.close();
        }
        result
    }

    fn close(&mut self) {
        if let (Some(link), Some(s)) = (self.link.as_mut(), self.selected) {
            link.unsubscribe(s.service, s.notify);
        }

        self.selected = None;

        // The connection before anything else is forgotten, and without fail: left open it
        // holds the link past the life of this object, and the symptom lands on whatever tries
        // to connect next rather than here.
        self.link = None;
        self.services.clear();
        self.discard_input();
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let (Some(link), Some(s)) = (self.link.as_mut(), self.selected) else {
            return Err(Error::InvalidOperation("The adapter is not open.".into()));
        };

        // In pieces the radio will carry. The default BLE payload is twenty bytes and an ELM327
        // command is far shorter, but a longer one is silently truncated rather than refused,
        // which reads as the adapter ignoring a command it never received in full.
        for piece in data.chunks(CHUNK) {
            link.write(s.service, s.write, piece, !s.without_response)
                .map_err(|status| {
                    let who = if self.name.is_empty() {
                        format!("The adapter at {:012X}", self.address)
                    } else {
                        self.name.clone()
                    };
                    Self::io(format!("{who} refused a write ({status})."))
                })?;
        }
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        let deadline = Instant::now() + timeout;
        let mut total = 0;

        while total < buffer.len() {
            total += self.incoming.drain_into(&mut buffer[total..]);
            if total == buffer.len() {
                break;
            }

            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                break;
            }

            // Waits for the next notification and then looks again, until the buffer is full or
            // the time is up. A quiet spell after some bytes have arrived ends the read with
            // what there is.
            if !self.incoming.wait(remaining) && total > 0 {
                break;
            }
        }

        Ok(total)
    }

    fn discard_input(&mut self) {
        self.incoming.clear();
    }
}

impl Drop for BleEcuTransport {
    fn drop(&mut self) {
        self.close();
    }
}

// ---------------------------------------------------------------------------------------------
// The platform side.
// ---------------------------------------------------------------------------------------------

/// The BLE stack of this operating system.
pub fn default_backend() -> Arc<dyn BleBackend> {
    platform::backend()
}

#[cfg(any(windows, target_os = "macos"))]
pub(crate) use btleplug_backend as platform;

#[cfg(target_os = "linux")]
pub(crate) use bluez as platform;

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub(crate) use unsupported as platform;

/// An operating system with no BLE backend: every connect fails, saying so.
#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub(crate) mod unsupported {
    use super::*;

    struct Unsupported;

    impl BleBackend for Unsupported {
        fn connect(&self, _: u64, _: Arc<Incoming>) -> Result<Option<Box<dyn GattLink>>> {
            Err(Error::Io(io::Error::new(
                io::ErrorKind::Unsupported,
                "Bluetooth LE is not supported on this operating system.",
            )))
        }
    }

    pub(crate) fn backend() -> Arc<dyn BleBackend> {
        Arc::new(Unsupported)
    }
}

/// Windows (WinRT) and macOS (CoreBluetooth), through `btleplug`.
#[cfg(any(windows, target_os = "macos"))]
pub(crate) mod btleplug_backend {
    use super::*;
    use btleplug::api::{Central, CharPropFlags, Characteristic, Manager as _, Peripheral as _, ScanFilter, WriteType};
    use btleplug::platform::{Adapter, Manager, Peripheral};
    use futures::StreamExt;
    use tokio::runtime::Runtime;
    use tokio::task::JoinHandle;

    /// How long a scan may run looking for a device on a platform that cannot connect by address.
    #[cfg(target_os = "macos")]
    const FIND_WITHIN: Duration = Duration::from_secs(10);

    fn runtime() -> std::result::Result<Arc<Runtime>, io::Error> {
        // On a thread pool of its own, always: this is called from a click handler, and waiting
        // on a future from the interface thread deadlocks the window rather than connecting.
        tokio::runtime::Builder::new_multi_thread()
            .worker_threads(1)
            .enable_all()
            .build()
            .map(Arc::new)
    }

    fn status(e: impl std::fmt::Display) -> String {
        e.to_string()
    }

    /// The first Bluetooth adapter, or the reason there is none.
    pub(crate) async fn first_adapter() -> Result<Adapter> {
        let manager = Manager::new()
            .await
            .map_err(|e| Error::Io(io::Error::other(format!("Bluetooth is unavailable ({e})."))))?;
        manager
            .adapters()
            .await
            .map_err(|e| Error::Io(io::Error::other(format!("Bluetooth is unavailable ({e})."))))?
            .into_iter()
            .next()
            .ok_or_else(|| Error::Io(io::Error::other("This computer has no Bluetooth adapter.")))
    }

    /// The addresses (and names) heard advertising during `window`: `address_from_id` of the
    /// peripheral's identifier on macOS, which has no addresses.
    pub(crate) fn scan(window: Duration) -> Result<Vec<(u64, Option<String>)>> {
        let rt = runtime()?;
        let heard = rt.block_on(async {
            let adapter = first_adapter().await?;
            adapter
                .start_scan(ScanFilter::default())
                .await
                .map_err(|e| Error::Io(io::Error::other(format!("Could not scan ({e})."))))?;
            tokio::time::sleep(window).await;
            let found = adapter.peripherals().await.unwrap_or_default();
            let _ = adapter.stop_scan().await;

            let mut heard = Vec::new();
            for p in found {
                let name = p.properties().await.ok().flatten().and_then(|props| props.local_name);
                heard.push((peripheral_address(&p), name));
            }
            Ok::<_, Error>(heard)
        });
        // Dropping a runtime from async context would panic; this is plain blocking code, but
        // shutting down in the background is the safe habit.
        if let Ok(rt) = Arc::try_unwrap(rt) {
            rt.shutdown_background();
        }
        heard
    }

    #[cfg(windows)]
    fn peripheral_address(p: &Peripheral) -> u64 {
        u64::from(p.address())
    }

    #[cfg(target_os = "macos")]
    fn peripheral_address(p: &Peripheral) -> u64 {
        crate::transport::ble_devices::address_from_id(&p.id().to_string())
    }

    struct Backend;

    pub(crate) fn backend() -> Arc<dyn BleBackend> {
        Arc::new(Backend)
    }

    impl BleBackend for Backend {
        fn connect(&self, address: u64, sink: Arc<Incoming>) -> Result<Option<Box<dyn GattLink>>> {
            let rt = runtime()?;
            let peripheral = rt.block_on(find_peripheral(address))?;
            let Some(peripheral) = peripheral else {
                return Ok(None);
            };

            // A device that will not connect is a device that could not be reached.
            if rt.block_on(peripheral.connect()).is_err() {
                return Ok(None);
            }

            Ok(Some(Box::new(Link {
                rt: Some(rt),
                peripheral,
                sink,
                services: Vec::new(),
                forwarder: None,
            })))
        }
    }

    #[cfg(windows)]
    async fn find_peripheral(address: u64) -> Result<Option<Peripheral>> {
        use btleplug::api::BDAddr;
        use btleplug::platform::PeripheralId;

        let adapter = first_adapter().await?;
        let Ok(bd) = BDAddr::try_from(address) else {
            return Ok(None);
        };
        // From the address alone, as the C#'s FromBluetoothAddressAsync did: a device the system
        // already knows can be reached without waiting for an advertisement.
        Ok(adapter.add_peripheral(&PeripheralId::from(bd)).await.ok())
    }

    #[cfg(target_os = "macos")]
    async fn find_peripheral(address: u64) -> Result<Option<Peripheral>> {
        let adapter = first_adapter().await?;
        adapter
            .start_scan(ScanFilter::default())
            .await
            .map_err(|e| Error::Io(io::Error::other(format!("Could not scan ({e})."))))?;

        let deadline = tokio::time::Instant::now() + FIND_WITHIN;
        let mut found = None;
        while found.is_none() && tokio::time::Instant::now() < deadline {
            found = adapter
                .peripherals()
                .await
                .unwrap_or_default()
                .into_iter()
                .find(|p| peripheral_address(p) == address);
            if found.is_none() {
                tokio::time::sleep(Duration::from_millis(200)).await;
            }
        }
        let _ = adapter.stop_scan().await;
        Ok(found)
    }

    struct Link {
        // Declared first so the connection is dropped before the runtime that drives it.
        peripheral: Peripheral,
        rt: Option<Arc<Runtime>>,
        sink: Arc<Incoming>,
        /// The characteristic objects, parallel to the `GattService`s given out.
        services: Vec<Vec<Characteristic>>,
        forwarder: Option<JoinHandle<()>>,
    }

    impl Link {
        fn rt(&self) -> &Runtime {
            self.rt.as_deref().expect("runtime is present until drop")
        }
    }

    fn props_of(flags: CharPropFlags) -> CharProps {
        CharProps {
            write: flags.contains(CharPropFlags::WRITE),
            write_without_response: flags.contains(CharPropFlags::WRITE_WITHOUT_RESPONSE),
            notify: flags.contains(CharPropFlags::NOTIFY),
            indicate: flags.contains(CharPropFlags::INDICATE),
        }
    }

    impl GattLink for Link {
        fn services(&mut self) -> std::result::Result<Vec<GattService>, String> {
            self.rt()
                .block_on(self.peripheral.discover_services())
                .map_err(status)?;

            let mut out = Vec::new();
            self.services.clear();
            for service in self.peripheral.services() {
                let chars: Vec<Characteristic> = service.characteristics.iter().cloned().collect();
                out.push(GattService {
                    uuid: service.uuid,
                    characteristics: chars
                        .iter()
                        .map(|c| GattCharacteristic {
                            uuid: c.uuid,
                            props: props_of(c.properties),
                        })
                        .collect(),
                });
                self.services.push(chars);
            }
            Ok(out)
        }

        fn subscribe(
            &mut self,
            service: usize,
            characteristic: usize,
            _indicate: bool,
        ) -> std::result::Result<(), String> {
            let c = self.services[service][characteristic].clone();
            let rt = self.rt.clone().expect("runtime is present until drop");

            // The stream first, so nothing sent in reply to the first write is missed.
            let mut stream = rt.block_on(self.peripheral.notifications()).map_err(status)?;
            let sink = self.sink.clone();
            let (uuid, service_uuid) = (c.uuid, c.service_uuid);
            self.forwarder = Some(rt.spawn(async move {
                while let Some(n) = stream.next().await {
                    if n.uuid == uuid && n.service_uuid == service_uuid {
                        sink.push(&n.value);
                    }
                }
            }));

            // btleplug writes the client configuration descriptor for notify or indicate as the
            // characteristic supports.
            rt.block_on(self.peripheral.subscribe(&c)).map_err(|e| {
                if let Some(f) = self.forwarder.take() {
                    f.abort();
                }
                status(e)
            })
        }

        fn unsubscribe(&mut self, service: usize, characteristic: usize) {
            if let Some(f) = self.forwarder.take() {
                f.abort();
            }
            let c = self.services[service][characteristic].clone();
            let _ = self.rt().block_on(self.peripheral.unsubscribe(&c));
        }

        fn write(
            &mut self,
            service: usize,
            characteristic: usize,
            data: &[u8],
            with_response: bool,
        ) -> std::result::Result<(), String> {
            let c = &self.services[service][characteristic];
            let kind = if with_response {
                WriteType::WithResponse
            } else {
                WriteType::WithoutResponse
            };
            self.rt().block_on(self.peripheral.write(c, data, kind)).map_err(status)
        }
    }

    impl Drop for Link {
        fn drop(&mut self) {
            if let Some(f) = self.forwarder.take() {
                f.abort();
            }
            // A device that has gone out of range cannot be closed politely.
            let _ = self.rt().block_on(self.peripheral.disconnect());
            if let Some(rt) = self.rt.take()
                && let Ok(rt) = Arc::try_unwrap(rt)
            {
                rt.shutdown_background();
            }
        }
    }
}

/// Linux: BlueZ over D-Bus with `zbus`'s blocking API.
#[cfg(target_os = "linux")]
pub(crate) mod bluez {
    use super::*;
    use crate::transport::ble_devices::parse_address;
    use std::collections::HashMap;
    use std::sync::atomic::{AtomicBool, Ordering};
    use zbus::blocking::{Connection, MessageIterator, Proxy};
    use zbus::zvariant::{OwnedObjectPath, OwnedValue, Value};
    use zbus::{MatchRule, message::Type};

    const BLUEZ: &str = "org.bluez";
    const DEVICE: &str = "org.bluez.Device1";
    const ADAPTER: &str = "org.bluez.Adapter1";
    const SERVICE: &str = "org.bluez.GattService1";
    const CHARACTERISTIC: &str = "org.bluez.GattCharacteristic1";

    type Properties = HashMap<String, OwnedValue>;
    type Objects = HashMap<OwnedObjectPath, HashMap<String, Properties>>;

    /// How long BlueZ may take to resolve a connected device's services.
    const RESOLVE_WITHIN: Duration = Duration::from_secs(10);

    fn d(e: impl std::fmt::Display) -> String {
        e.to_string()
    }

    pub(crate) fn managed_objects(conn: &Connection) -> std::result::Result<Objects, String> {
        Proxy::new(conn, BLUEZ, "/", "org.freedesktop.DBus.ObjectManager")
            .and_then(|p| p.call("GetManagedObjects", &()))
            .map_err(d)
    }

    fn string(p: &Properties, key: &str) -> Option<String> {
        p.get(key).and_then(|v| String::try_from(v.try_clone().ok()?).ok())
    }

    fn boolean(p: &Properties, key: &str) -> Option<bool> {
        p.get(key).and_then(|v| bool::try_from(v.try_clone().ok()?).ok())
    }

    fn strings(p: &Properties, key: &str) -> Vec<String> {
        p.get(key)
            .and_then(|v| Vec::<String>::try_from(v.try_clone().ok()?).ok())
            .unwrap_or_default()
    }

    /// Every device BlueZ knows: its object path, name, address and whether it is paired.
    pub(crate) struct KnownDevice {
        pub path: String,
        pub name: String,
        pub address: u64,
        pub paired: bool,
        pub heard: bool,
    }

    pub(crate) fn known_devices(conn: &Connection) -> std::result::Result<Vec<KnownDevice>, String> {
        let mut out = Vec::new();
        for (path, interfaces) in managed_objects(conn)? {
            let Some(p) = interfaces.get(DEVICE) else { continue };
            let Some(address) = string(p, "Address").and_then(|a| parse_address(&a)) else {
                continue;
            };
            out.push(KnownDevice {
                path: path.to_string(),
                name: string(p, "Name").or_else(|| string(p, "Alias")).unwrap_or_default(),
                address,
                paired: boolean(p, "Paired").unwrap_or(false),
                // BlueZ drops RSSI from a device it has stopped hearing.
                heard: p.contains_key("RSSI"),
            });
        }
        Ok(out)
    }

    /// Scans for `window` and returns the addresses heard.
    pub(crate) fn scan(window: Duration) -> std::result::Result<Vec<(u64, Option<String>)>, String> {
        let conn = Connection::system().map_err(d)?;
        let objects = managed_objects(&conn)?;
        let adapter = objects
            .iter()
            .find(|(_, i)| i.contains_key(ADAPTER))
            .map(|(p, _)| p.to_string())
            .ok_or("This computer has no Bluetooth adapter.")?;

        let proxy = Proxy::new(&conn, BLUEZ, adapter.as_str(), ADAPTER).map_err(d)?;
        proxy.call::<_, _, ()>("StartDiscovery", &()).map_err(d)?;
        thread::sleep(window);
        let heard = known_devices(&conn);
        let _ = proxy.call::<_, _, ()>("StopDiscovery", &());

        Ok(heard?
            .into_iter()
            .filter(|k| k.heard)
            .map(|k| (k.address, Some(k.name).filter(|n| !n.is_empty())))
            .collect())
    }

    struct Backend;

    pub(crate) fn backend() -> Arc<dyn BleBackend> {
        Arc::new(Backend)
    }

    impl BleBackend for Backend {
        fn connect(&self, address: u64, sink: Arc<Incoming>) -> Result<Option<Box<dyn GattLink>>> {
            let conn = Connection::system()
                .map_err(|e| Error::Io(io::Error::other(format!("Could not reach the system bus ({e})."))))?;
            let known = known_devices(&conn).map_err(|e| Error::Io(io::Error::other(e)))?;

            // BlueZ cannot connect to an address it has never heard of.
            let Some(device) = known.into_iter().find(|k| k.address == address) else {
                return Ok(None);
            };

            {
                let proxy = link_proxy(&conn, &device.path, DEVICE).map_err(|e| Error::Io(io::Error::other(e)))?;
                if proxy.call::<_, _, ()>("Connect", &()).is_err() {
                    return Ok(None);
                }
            }

            Ok(Some(Box::new(Link {
                conn,
                device: device.path,
                paths: Vec::new(),
                sink,
                listener: None,
                stop: Arc::new(AtomicBool::new(false)),
            })))
        }
    }

    struct Link {
        conn: Connection,
        device: String,
        /// Object paths, parallel to the services and characteristics given out.
        paths: Vec<(String, Vec<String>)>,
        sink: Arc<Incoming>,
        listener: Option<thread::JoinHandle<()>>,
        stop: Arc<AtomicBool>,
    }

    fn link_proxy<'a>(
        conn: &'a Connection,
        path: &str,
        interface: &'static str,
    ) -> std::result::Result<Proxy<'a>, String> {
        Proxy::new(conn, BLUEZ, path.to_string(), interface.to_string()).map_err(d)
    }

    impl GattLink for Link {
        fn services(&mut self) -> std::result::Result<Vec<GattService>, String> {
            // The services are resolved some moments after the connection is made.
            let device = link_proxy(&self.conn, &self.device, DEVICE)?;
            let deadline = Instant::now() + RESOLVE_WITHIN;
            while !device.get_property::<bool>("ServicesResolved").unwrap_or(false) {
                if Instant::now() >= deadline {
                    return Err("timed out waiting for BlueZ to resolve the services".into());
                }
                thread::sleep(Duration::from_millis(100));
            }

            let objects = managed_objects(&self.conn)?;
            let prefix = format!("{}/", self.device);

            let mut services: Vec<(String, Uuid)> = objects
                .iter()
                .filter(|(p, i)| p.as_str().starts_with(&prefix) && i.contains_key(SERVICE))
                .filter_map(|(p, i)| {
                    let uuid = Uuid::parse_str(&string(&i[SERVICE], "UUID")?).ok()?;
                    Some((p.to_string(), uuid))
                })
                .collect();
            services.sort();

            self.paths.clear();
            let mut out = Vec::new();
            for (service_path, uuid) in services {
                let mut chars: Vec<(String, GattCharacteristic)> = objects
                    .iter()
                    .filter_map(|(p, i)| {
                        let props = i.get(CHARACTERISTIC)?;
                        let owner = props
                            .get("Service")
                            .and_then(|v| OwnedObjectPath::try_from(v.try_clone().ok()?).ok())?;
                        if owner.as_str() != service_path {
                            return None;
                        }
                        let flags = strings(props, "Flags");
                        let has = |f: &str| flags.iter().any(|x| x == f);
                        Some((
                            p.to_string(),
                            GattCharacteristic {
                                uuid: Uuid::parse_str(&string(props, "UUID")?).ok()?,
                                props: CharProps {
                                    write: has("write"),
                                    write_without_response: has("write-without-response"),
                                    notify: has("notify"),
                                    indicate: has("indicate"),
                                },
                            },
                        ))
                    })
                    .collect();
                chars.sort_by(|a, b| a.0.cmp(&b.0));

                self.paths
                    .push((service_path, chars.iter().map(|c| c.0.clone()).collect()));
                out.push(GattService {
                    uuid,
                    characteristics: chars.into_iter().map(|c| c.1).collect(),
                });
            }
            Ok(out)
        }

        fn subscribe(
            &mut self,
            service: usize,
            characteristic: usize,
            _indicate: bool,
        ) -> std::result::Result<(), String> {
            let path = self.paths[service].1[characteristic].clone();

            // Listening before asking, so nothing sent in reply to the first write is missed. The
            // rule covers the whole device subtree so that the device disconnecting wakes the
            // thread, which then sees the stop flag.
            let rule = MatchRule::builder()
                .msg_type(Type::Signal)
                .interface("org.freedesktop.DBus.Properties")
                .map_err(d)?
                .member("PropertiesChanged")
                .map_err(d)?
                .path_namespace(self.device.as_str())
                .map_err(d)?
                .build();
            let signals = MessageIterator::for_match_rule(rule, &self.conn, None).map_err(d)?;

            self.stop.store(false, Ordering::SeqCst);
            let (stop, sink, watched) = (self.stop.clone(), self.sink.clone(), path.clone());
            self.listener = Some(thread::spawn(move || {
                for message in signals {
                    if stop.load(Ordering::SeqCst) {
                        break;
                    }
                    let Ok(message) = message else { continue };
                    if message.header().path().map(|p| p.as_str()) != Some(watched.as_str()) {
                        continue;
                    }
                    let Ok((interface, changed, _)) = message.body().deserialize::<(String, Properties, Vec<String>)>()
                    else {
                        continue;
                    };
                    if interface != CHARACTERISTIC {
                        continue;
                    }
                    if let Some(bytes) = changed
                        .get("Value")
                        .and_then(|v| Vec::<u8>::try_from(v.try_clone().ok()?).ok())
                    {
                        sink.push(&bytes);
                    }
                }
            }));

            link_proxy(&self.conn, &path, CHARACTERISTIC)?
                .call::<_, _, ()>("StartNotify", &())
                .map_err(d)
        }

        fn unsubscribe(&mut self, service: usize, characteristic: usize) {
            self.stop.store(true, Ordering::SeqCst);
            let path = self.paths[service].1[characteristic].clone();
            // BlueZ answers by announcing Notifying=false, which wakes the listener to exit.
            if let Ok(p) = link_proxy(&self.conn, &path, CHARACTERISTIC) {
                let _ = p.call::<_, _, ()>("StopNotify", &());
            }
            self.listener = None;
        }

        fn write(
            &mut self,
            service: usize,
            characteristic: usize,
            data: &[u8],
            with_response: bool,
        ) -> std::result::Result<(), String> {
            let path = self.paths[service].1[characteristic].clone();
            let mut options: HashMap<&str, Value<'_>> = HashMap::new();
            options.insert("type", Value::from(if with_response { "request" } else { "command" }));
            link_proxy(&self.conn, &path, CHARACTERISTIC)?
                .call::<_, _, ()>("WriteValue", &(data.to_vec(), options))
                .map_err(d)
        }
    }

    impl Drop for Link {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::SeqCst);
            // A device that has gone out of range cannot be closed politely.
            if let Ok(p) = link_proxy(&self.conn, &self.device, DEVICE) {
                let _ = p.call::<_, _, ()>("Disconnect", &());
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // ----- the C# test ---------------------------------------------------------------------

    #[test]
    fn the_service_that_worked_in_practice_is_tried_first() {
        // No standard exists for a serial bridge over BLE, so each maker picked a vendor
        // service. 0xFFF0 is what the clones overwhelmingly use, and what the dongle this was
        // verified against answers on.
        assert_eq!(
            SERIAL_SERVICES[0],
            Uuid::parse_str("0000fff0-0000-1000-8000-00805f9b34fb").unwrap()
        );

        // 0xAE00 is second because the same dongle publishes it and never answers on it, which
        // is why the code proves a service before using it.
        assert!(SERIAL_SERVICES.contains(&Uuid::parse_str("0000ae00-0000-1000-8000-00805f9b34fb").unwrap()));
    }

    #[test]
    fn the_full_list_is_the_c_sharp_list_in_order() {
        let text: Vec<String> = SERIAL_SERVICES.iter().map(Uuid::to_string).collect();
        assert_eq!(
            text,
            [
                "0000fff0-0000-1000-8000-00805f9b34fb",
                "0000ae00-0000-1000-8000-00805f9b34fb",
                "0000ffe0-0000-1000-8000-00805f9b34fb",
                "6e400001-b5a3-f393-e0a9-e50e24dcca9e",
            ]
        );
    }

    // ----- a fake adapter ------------------------------------------------------------------

    #[derive(Default)]
    struct World {
        /// None means "could not be reached".
        reachable: bool,
        services: Vec<GattService>,
        list_error: Option<String>,
        /// A write to this service gets a reply pushed to the sink.
        answers_on: Option<usize>,
        subscribe_error: bool,
        write_error: bool,
        connects: usize,
        /// service, characteristic, data, with_response
        writes: Vec<(usize, usize, Vec<u8>, bool)>,
        subscribed: Vec<(usize, usize, bool)>,
        unsubscribed: Vec<(usize, usize)>,
        dropped_links: usize,
        sink: Option<Arc<Incoming>>,
    }

    struct FakeBackend {
        world: Arc<Mutex<World>>,
        /// This many connects fail before one works.
        fail_first: usize,
    }

    struct FakeLink(Arc<Mutex<World>>);

    impl BleBackend for FakeBackend {
        fn connect(&self, _: u64, sink: Arc<Incoming>) -> Result<Option<Box<dyn GattLink>>> {
            let mut w = self.world.lock().unwrap();
            w.connects += 1;
            if w.connects <= self.fail_first || !w.reachable {
                return Ok(None);
            }
            w.sink = Some(sink);
            Ok(Some(Box::new(FakeLink(self.world.clone()))))
        }
    }

    impl GattLink for FakeLink {
        fn services(&mut self) -> std::result::Result<Vec<GattService>, String> {
            let w = self.0.lock().unwrap();
            match &w.list_error {
                Some(e) => Err(e.clone()),
                None => Ok(w.services.clone()),
            }
        }

        fn subscribe(&mut self, s: usize, c: usize, indicate: bool) -> std::result::Result<(), String> {
            let mut w = self.0.lock().unwrap();
            if w.subscribe_error {
                return Err("denied".into());
            }
            w.subscribed.push((s, c, indicate));
            Ok(())
        }

        fn unsubscribe(&mut self, s: usize, c: usize) {
            self.0.lock().unwrap().unsubscribed.push((s, c));
        }

        fn write(&mut self, s: usize, c: usize, data: &[u8], with: bool) -> std::result::Result<(), String> {
            let mut w = self.0.lock().unwrap();
            if w.write_error {
                return Err("Unreachable".into());
            }
            w.writes.push((s, c, data.to_vec(), with));
            if w.answers_on == Some(s)
                && let Some(sink) = &w.sink
            {
                sink.push(b">");
            }
            Ok(())
        }
    }

    impl Drop for FakeLink {
        fn drop(&mut self) {
            self.0.lock().unwrap().dropped_links += 1;
        }
    }

    fn serial_service(uuid: Uuid) -> GattService {
        GattService {
            uuid,
            characteristics: vec![
                GattCharacteristic {
                    uuid: uuid16(0xFFF2),
                    props: CharProps {
                        write: true,
                        write_without_response: true,
                        ..Default::default()
                    },
                },
                GattCharacteristic {
                    uuid: uuid16(0xFFF1),
                    props: CharProps {
                        notify: true,
                        ..Default::default()
                    },
                },
            ],
        }
    }

    fn world(services: Vec<GattService>, answers_on: Option<usize>) -> Arc<Mutex<World>> {
        Arc::new(Mutex::new(World {
            reachable: true,
            services,
            answers_on,
            ..World::default()
        }))
    }

    fn transport(w: &Arc<Mutex<World>>, fail_first: usize) -> BleEcuTransport {
        let backend = Arc::new(FakeBackend {
            world: w.clone(),
            fail_first,
        });
        BleEcuTransport::with_backend(0xB96975AA93F6, "OBDII", backend)
            .with_retry_delay(Duration::from_millis(1))
            .with_answer_wait(Duration::from_millis(30))
    }

    #[test]
    fn a_service_that_answers_is_the_one_used() {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        let mut t = transport(&w, 0);

        t.open().unwrap();

        assert!(t.is_open());
        let w = w.lock().unwrap();
        assert_eq!(w.subscribed, vec![(0, 1, false)]);
        // It was proved by sending a carriage return.
        assert_eq!(w.writes, vec![(0, 0, b"\r".to_vec(), false)]);
    }

    #[test]
    fn a_silent_service_is_skipped_for_the_next_that_answers() {
        // The dongle this was verified against publishes 0xAE00 and never answers on it.
        let w = world(
            vec![serial_service(SERIAL_SERVICES[1]), serial_service(SERIAL_SERVICES[0])],
            Some(1),
        );
        // Only the service at index 1 (0xFFF0, tried first by the list's order) replies.
        let mut t = transport(&w, 0);
        t.open().unwrap();
        assert_eq!(w.lock().unwrap().subscribed.last().unwrap().0, 1);

        // Now the other way round: 0xFFF0 silent, 0xAE00 answering.
        let w = world(
            vec![serial_service(SERIAL_SERVICES[0]), serial_service(SERIAL_SERVICES[1])],
            Some(1),
        );
        let mut t = transport(&w, 0);
        t.open().unwrap();
        let w = w.lock().unwrap();
        assert_eq!(w.subscribed.iter().map(|s| s.0).collect::<Vec<_>>(), vec![0, 1]);
        // The silent one was let go of cleanly.
        assert_eq!(w.unsubscribed, vec![(0, 1)]);
    }

    #[test]
    fn no_service_answering_says_what_the_device_publishes() {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], None);
        let mut t = transport(&w, 0);

        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        let text = e.to_string();
        assert!(text.starts_with("OBDII is connected, but none of its services answered as an ELM327."));
        assert!(text.contains("It publishes 1 service(s): 0000fff0-0000-1000-8000-00805f9b34fb"));
        assert!(!t.is_open());
        // Three attempts, as in the C#.
        assert_eq!(w.lock().unwrap().connects, 3);
    }

    #[test]
    fn an_unreachable_device_is_retried_and_then_reported() {
        let w = Arc::new(Mutex::new(World {
            reachable: false,
            ..World::default()
        }));
        let mut t = transport(&w, 0);

        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        assert!(e.to_string().starts_with("OBDII could not be reached."));
        assert_eq!(w.lock().unwrap().connects, 3);
    }

    #[test]
    fn a_connection_that_works_on_the_second_attempt_is_used() {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        let mut t = transport(&w, 1);

        t.open().unwrap();
        assert!(t.is_open());
        assert_eq!(w.lock().unwrap().connects, 2);
    }

    #[test]
    fn a_device_that_will_not_list_its_services_names_the_status() {
        let w = world(vec![], None);
        w.lock().unwrap().list_error = Some("Unreachable".into());
        let mut t = transport(&w, 0);

        let Err(Error::Io(e)) = t.open() else {
            panic!("expected an I/O error")
        };
        assert!(
            e.to_string()
                .starts_with("OBDII did not list its services (Unreachable).")
        );
        assert!(e.to_string().contains("disconnect it there first"));
    }

    #[test]
    fn a_service_without_both_characteristics_is_not_a_candidate() {
        let mut only_notify = serial_service(SERIAL_SERVICES[0]);
        only_notify.characteristics.remove(0);
        let w = world(vec![only_notify], Some(0));
        let mut t = transport(&w, 0);

        assert!(t.open().is_err());
        assert!(w.lock().unwrap().subscribed.is_empty());
    }

    #[test]
    fn a_failed_subscription_rules_the_service_out() {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        w.lock().unwrap().subscribe_error = true;
        let mut t = transport(&w, 0);

        assert!(t.open().is_err());
        assert!(w.lock().unwrap().writes.is_empty());
    }

    #[test]
    fn indications_are_used_when_the_characteristic_cannot_notify() {
        let mut svc = serial_service(SERIAL_SERVICES[0]);
        svc.characteristics[1].props = CharProps {
            indicate: true,
            ..Default::default()
        };
        let w = world(vec![svc], Some(0));
        let mut t = transport(&w, 0);

        t.open().unwrap();
        assert_eq!(w.lock().unwrap().subscribed, vec![(0, 1, true)]);
    }

    #[test]
    fn a_write_goes_out_in_pieces_of_twenty_bytes() {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        let mut t = transport(&w, 0);
        t.open().unwrap();
        w.lock().unwrap().writes.clear();

        t.write(&[7u8; 45]).unwrap();

        let sizes: Vec<usize> = w.lock().unwrap().writes.iter().map(|x| x.2.len()).collect();
        assert_eq!(sizes, vec![20, 20, 5]);
    }

    #[test]
    fn write_without_response_is_chosen_when_the_characteristic_offers_it() {
        // Both flags are present on the test service: without response wins, as in the C#.
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        let mut t = transport(&w, 0);
        t.open().unwrap();
        assert!(!w.lock().unwrap().writes[0].3);

        // Only Write: with response.
        let mut svc = serial_service(SERIAL_SERVICES[0]);
        svc.characteristics[0].props = CharProps {
            write: true,
            ..Default::default()
        };
        let w = world(vec![svc], Some(0));
        let mut t = transport(&w, 0);
        t.open().unwrap();
        assert!(w.lock().unwrap().writes[0].3);
    }

    #[test]
    fn a_refused_write_is_an_io_error_naming_the_adapter() {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        let mut t = transport(&w, 0);
        t.open().unwrap();
        w.lock().unwrap().write_error = true;

        let Err(Error::Io(e)) = t.write(b"ATZ\r") else {
            panic!("expected an I/O error")
        };
        assert_eq!(e.to_string(), "OBDII refused a write (Unreachable).");
    }

    #[test]
    fn an_unnamed_adapter_is_described_by_its_address() {
        let t = BleEcuTransport::with_backend(
            0xB96975AA93F6,
            "",
            Arc::new(FakeBackend {
                world: world(vec![], None),
                fail_first: 0,
            }),
        );
        assert_eq!(t.describe(), "The adapter at B96975AA93F6");
    }

    #[test]
    fn writing_before_opening_says_so() {
        let w = world(vec![], None);
        let mut t = transport(&w, 0);
        assert!(matches!(t.write(b"x"), Err(Error::InvalidOperation(m)) if m == "The adapter is not open."));
    }

    #[test]
    fn closing_unsubscribes_drops_the_connection_and_clears_the_queue() {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        let mut t = transport(&w, 0);
        t.open().unwrap();
        let sink = w.lock().unwrap().sink.clone().unwrap();
        sink.push(b"stale");

        t.close();

        assert!(!t.is_open());
        let w = w.lock().unwrap();
        assert_eq!(w.unsubscribed, vec![(0, 1)]);
        assert_eq!(w.dropped_links, 1);
        drop(w);
        assert_eq!(t.read(&mut [0u8; 5], Duration::from_millis(10)).unwrap(), 0);
    }

    // ----- how a read waits ----------------------------------------------------------------

    fn open_with_sink() -> (BleEcuTransport, Arc<Incoming>) {
        let w = world(vec![serial_service(SERIAL_SERVICES[0])], Some(0));
        let mut t = transport(&w, 0);
        t.open().unwrap();
        let sink = w.lock().unwrap().sink.clone().unwrap();
        (t, sink)
    }

    #[test]
    fn a_read_returns_as_soon_as_the_buffer_is_full() {
        let (mut t, sink) = open_with_sink();
        sink.push(b"OK>");

        let mut buf = [0u8; 3];
        let started = Instant::now();
        assert_eq!(t.read(&mut buf, Duration::from_secs(5)).unwrap(), 3);
        assert_eq!(&buf, b"OK>");
        assert!(started.elapsed() < Duration::from_secs(1));
    }

    #[test]
    fn a_read_gathers_notifications_that_arrive_while_it_waits() {
        let (mut t, sink) = open_with_sink();
        t.discard_input();
        let feeder = thread::spawn(move || {
            for piece in [&b"AT"[..], b"Z\r", b"OK"] {
                thread::sleep(Duration::from_millis(30));
                sink.push(piece);
            }
        });

        let mut buf = [0u8; 6];
        assert_eq!(t.read(&mut buf, Duration::from_secs(2)).unwrap(), 6);
        assert_eq!(&buf, b"ATZ\rOK");
        feeder.join().unwrap();
    }

    #[test]
    fn a_read_with_nothing_to_hear_returns_zero_after_the_timeout() {
        let (mut t, _sink) = open_with_sink();
        t.discard_input();

        let started = Instant::now();
        assert_eq!(t.read(&mut [0u8; 4], Duration::from_millis(80)).unwrap(), 0);
        assert!(started.elapsed() >= Duration::from_millis(70));
    }

    #[test]
    fn a_read_that_has_some_bytes_returns_them_when_the_notifications_stop() {
        let (mut t, sink) = open_with_sink();
        t.discard_input();
        sink.push(b"AB");

        let mut buf = [0u8; 9];
        assert_eq!(t.read(&mut buf, Duration::from_millis(100)).unwrap(), 2);
        assert_eq!(&buf[..2], b"AB");
    }

    #[test]
    fn discarding_input_drops_queued_bytes_and_pending_wakeups() {
        let (mut t, sink) = open_with_sink();
        sink.push(b"old");
        t.discard_input();

        // Neither the bytes nor the "something arrived" count survives, so this does not return
        // early with nothing.
        let started = Instant::now();
        assert_eq!(t.read(&mut [0u8; 3], Duration::from_millis(60)).unwrap(), 0);
        assert!(started.elapsed() >= Duration::from_millis(50));
    }

    #[test]
    fn the_uuid_helper_builds_the_bluetooth_base_form() {
        assert_eq!(uuid16(0x180F).to_string(), "0000180f-0000-1000-8000-00805f9b34fb");
    }
}
