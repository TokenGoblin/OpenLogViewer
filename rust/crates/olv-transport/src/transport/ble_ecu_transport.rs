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
//! * **Windows**: WinRT through the `windows` crate (`ble_windows`), making the calls the C# made.
//! * **macOS**: CoreBluetooth through `objc2-core-bluetooth` (`ble_macos`).
//! * **Linux**: BlueZ over D-Bus with the pure-Rust `zbus` (blocking API), below.
//!
//! Each is the operating system's own API; no third-party C library is linked on any of them.
//!
//! # Differences the bench must confirm
//!
//! * **None of this has run against a real adapter.** The C# was verified on one; the Rust
//!   shares only its logic. The Windows backend makes the same WinRT calls in the same order;
//!   the macOS backend has only been type-checked.
//! * The Windows backend lists characteristics for the four known serial services up front (as
//!   part of listing the services), where the C# asked for each service's characteristics only
//!   when it came to try it. Same calls, same uncached mode, earlier. Services that are not
//!   candidates are closed straight away; the rest when their subscription is let go of or the
//!   connection is closed, where the C# closed them all when opening finished.
//! * On Windows the device is made from its address as `FromBluetoothAddressAsync` did, so a paired
//!   adapter that is not advertising can still be reached. On macOS CoreBluetooth has no addresses
//!   at all: the "address" is a stable hash of the peripheral's identifier (see
//!   `ble_devices::address_from_peripheral_id`), and connecting scans for up to ten seconds until a
//!   peripheral with that hash is heard, so the adapter must be advertising.
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

impl CharProps {
    /// From the GATT characteristic-properties bit field. WinRT's `GattCharacteristicProperties`
    /// and CoreBluetooth's `CBCharacteristicProperties` both use the Bluetooth Core
    /// specification's bits, so one reading serves both.
    #[cfg(any(windows, target_os = "macos", test))]
    pub(crate) fn from_bits(bits: u32) -> Self {
        Self {
            write_without_response: bits & 0x04 != 0,
            write: bits & 0x08 != 0,
            notify: bits & 0x10 != 0,
            indicate: bits & 0x20 != 0,
        }
    }
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

#[cfg(windows)]
pub(crate) use crate::transport::ble_windows as platform;

#[cfg(target_os = "macos")]
pub(crate) use crate::transport::ble_macos as platform;

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

/// A value behind a lock that can be waited on until it satisfies a condition: how an
/// asynchronous callback API (CoreBluetooth's delegate) is turned into the blocking calls
/// [`GattLink`] wants. The callbacks `update` it; the caller `wait_for`s the outcome it asked for.
#[cfg(any(target_os = "macos", test))]
pub(crate) struct Mailbox<S> {
    state: Mutex<S>,
    changed: Condvar,
}

#[cfg(any(target_os = "macos", test))]
impl<S> Mailbox<S> {
    pub(crate) fn new(state: S) -> Self {
        Self {
            state: Mutex::new(state),
            changed: Condvar::new(),
        }
    }

    /// Changes the state and wakes every waiter.
    pub(crate) fn update<R>(&self, change: impl FnOnce(&mut S) -> R) -> R {
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let result = change(&mut state);
        self.changed.notify_all();
        result
    }

    /// Reads the state.
    pub(crate) fn read<R>(&self, look: impl FnOnce(&S) -> R) -> R {
        look(&self.state.lock().unwrap_or_else(|e| e.into_inner()))
    }

    /// Waits up to `timeout` for `ready` to return `Some`, checking at once and after every
    /// update. `None` is a timeout.
    pub(crate) fn wait_for<T>(&self, timeout: Duration, mut ready: impl FnMut(&mut S) -> Option<T>) -> Option<T> {
        let deadline = Instant::now() + timeout;
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if let Some(value) = ready(&mut state) {
                return Some(value);
            }
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                return None;
            }
            state = self
                .changed
                .wait_timeout(state, left)
                .unwrap_or_else(|e| e.into_inner())
                .0;
        }
    }
}

/// Outcomes of asynchronous requests, each kept under the key of the attribute it was asked of
/// (a characteristic's address).
///
/// One shared slot per kind of request is wrong: the late "notifications are now off" for service
/// A would be taken as the answer to the "notifications are now on" asked of service B, which
/// then returns before its descriptor is written and loses the first reply. Keyed, A's outcome
/// can only ever be A's.
#[cfg(any(target_os = "macos", test))]
#[derive(Default)]
pub(crate) struct Outcomes {
    by_key: std::collections::HashMap<usize, std::result::Result<(), String>>,
}

#[cfg(any(target_os = "macos", test))]
impl Outcomes {
    /// Forgets any outcome for `key`, before a request is made.
    pub(crate) fn expect(&mut self, key: usize) {
        self.by_key.remove(&key);
    }

    /// Records the answer to a request, from a callback.
    pub(crate) fn post(&mut self, key: usize, outcome: std::result::Result<(), String>) {
        self.by_key.insert(key, outcome);
    }

    /// Takes the answer for `key`, if it has come.
    pub(crate) fn take(&mut self, key: usize) -> Option<std::result::Result<(), String>> {
        self.by_key.remove(&key)
    }
}

/// A CoreBluetooth `CBUUID` string as a `Uuid`: four hex digits (a 16-bit assigned number, as
/// `FFF0`), eight (32-bit), or the full 36-character form.
#[cfg(any(target_os = "macos", test))]
pub(crate) fn uuid_from_cb_string(text: &str) -> Option<Uuid> {
    match text.len() {
        4 => u16::from_str_radix(text, 16).ok().map(uuid16),
        8 => u32::from_str_radix(text, 16)
            .ok()
            .map(|n| Uuid::from_u128(u128::from(n) << 96 | 0x0000_1000_8000_0080_5f9b_34fb)),
        _ => Uuid::parse_str(text).ok(),
    }
}

/// A background thread that forwards one subscription's notifications, with a stop signal of its
/// own.
///
/// Each subscription gets one, so letting go of one service's listener can never switch off, or be
/// undone by, the listener of the next: a signal shared between subscriptions was reset by the
/// next `subscribe`, which left the previous listener running.
#[cfg(any(target_os = "linux", test))]
pub(crate) struct Listener {
    stop: Arc<std::sync::atomic::AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
}

#[cfg(any(target_os = "linux", test))]
impl Listener {
    /// Starts `work`, which must return soon after the flag it is given is set.
    pub(crate) fn spawn(work: impl FnOnce(Arc<std::sync::atomic::AtomicBool>) + Send + 'static) -> Self {
        let stop = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let flag = stop.clone();
        Self {
            stop,
            thread: Some(thread::spawn(move || work(flag))),
        }
    }

    /// Sets the stop signal, without waiting.
    pub(crate) fn request_stop(&self) {
        self.stop.store(true, std::sync::atomic::Ordering::SeqCst);
    }

    /// Sets the stop signal, then waits up to `wait` for the thread to end and joins it. A thread
    /// still blocked then is left to end by itself: it has seen the signal by the time it wakes,
    /// and pushes nothing once it has.
    pub(crate) fn stop(mut self, wait: Duration) {
        self.request_stop();
        let Some(thread) = self.thread.take() else {
            return;
        };
        let deadline = Instant::now() + wait;
        while !thread.is_finished() && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(5));
        }
        if thread.is_finished() {
            let _ = thread.join();
        }
    }
}

#[cfg(any(target_os = "linux", test))]
impl Drop for Listener {
    fn drop(&mut self) {
        self.stop.store(true, std::sync::atomic::Ordering::SeqCst);
    }
}

/// Whether a D-Bus signal is a characteristic's notification: sent by BlueZ itself (its unique bus
/// name, resolved when the subscription started) and for the characteristic being watched.
///
/// Any local process can broadcast `PropertiesChanged` on the system bus. Without the sender check
/// it could put bytes in front of the ELM327 parser as though the ECU had sent them.
#[cfg(any(target_os = "linux", test))]
pub(crate) fn is_notification_from(sender: Option<&str>, bluez_owner: &str, path: Option<&str>, watched: &str) -> bool {
    sender == Some(bluez_owner) && path == Some(watched)
}

/// The listener of the current subscription, and the characteristic it is for.
#[cfg(any(target_os = "linux", test))]
#[derive(Default)]
pub(crate) struct ListenerSlot {
    current: Option<(Listener, String)>,
}

#[cfg(any(target_os = "linux", test))]
impl ListenerSlot {
    pub(crate) fn set(&mut self, listener: Listener, path: String) {
        self.current = Some((listener, path));
    }

    /// Stops the listener, if there is one: the signal first, so it is set whenever the listener
    /// wakes; then `wake`, which must make something happen on the listener's source (BlueZ's
    /// reply to `StopNotify` or `Disconnect`); then a wait of up to `wait` for it to end.
    /// Returns whether there was a listener.
    pub(crate) fn stop(&mut self, wake: impl FnOnce(&str), wait: Duration) -> bool {
        let Some((listener, path)) = self.current.take() else {
            return false;
        };
        listener.request_stop();
        wake(&path);
        listener.stop(wait);
        true
    }
}

/// The attribute handle in a BlueZ object path such as `/org/bluez/hci0/dev_X/service000a/char000b`
/// (the last component's trailing hex digits). BlueZ names its objects by handle, so this is the
/// order the device's attribute table has them in.
#[cfg(any(target_os = "linux", test))]
pub(crate) fn handle_of_path(path: &str) -> u32 {
    let last = path.rsplit('/').next().unwrap_or("");
    let hex = last
        .strip_prefix("service")
        .or_else(|| last.strip_prefix("char"))
        .unwrap_or("");
    u32::from_str_radix(hex, 16).unwrap_or(u32::MAX)
}

/// Linux: BlueZ over D-Bus with `zbus`'s blocking API.
#[cfg(target_os = "linux")]
pub(crate) mod bluez {
    use super::*;
    use crate::transport::ble_devices::parse_address;
    use std::collections::HashMap;
    use std::sync::atomic::Ordering;
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
                listener: ListenerSlot::default(),
            })))
        }
    }

    struct Link {
        conn: Connection,
        device: String,
        /// Object paths, parallel to the services and characteristics given out.
        paths: Vec<(String, Vec<String>)>,
        sink: Arc<Incoming>,
        /// The current subscription's listener and the characteristic it is for.
        listener: ListenerSlot,
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
            // In attribute-handle order, as WinRT lists them and the C# relied on.
            services.sort_by_key(|(path, _)| handle_of_path(path));

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
                chars.sort_by_key(|(path, _)| handle_of_path(path));

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
            // thread, which then sees its stop flag.
            //
            // Only BlueZ's signals are listened to: the rule names the sender, and the thread
            // checks each message came from BlueZ's unique name (a name owner can change, and a
            // rule alone is not a guarantee).
            let owner = bluez_owner(&self.conn)?;
            let rule = MatchRule::builder()
                .msg_type(Type::Signal)
                .sender(BLUEZ)
                .map_err(d)?
                .interface("org.freedesktop.DBus.Properties")
                .map_err(d)?
                .member("PropertiesChanged")
                .map_err(d)?
                .path_namespace(self.device.as_str())
                .map_err(d)?
                .build();
            let signals = MessageIterator::for_match_rule(rule, &self.conn, None).map_err(d)?;

            // A listener left from an earlier service is stopped, and waited for, before this one
            // starts: each subscription has a stop signal of its own.
            self.stop_listener();

            let (sink, watched) = (self.sink.clone(), path.clone());
            let listener = Listener::spawn(move |stop| {
                for message in signals {
                    if stop.load(Ordering::SeqCst) {
                        break;
                    }
                    let Ok(message) = message else { continue };
                    let header = message.header();
                    if !is_notification_from(
                        header.sender().map(|n| n.as_str()),
                        &owner,
                        header.path().map(|p| p.as_str()),
                        &watched,
                    ) {
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
                        && !stop.load(Ordering::SeqCst)
                    {
                        sink.push(&bytes);
                    }
                }
            });
            self.listener.set(listener, path.clone());

            // BlueZ writes the client configuration descriptor itself, for notification whenever
            // the characteristic offers it and for indication only otherwise, which is the C#'s
            // choice too. `_indicate` therefore needs no argument here.
            let started = link_proxy(&self.conn, &path, CHARACTERISTIC)?
                .call::<_, _, ()>("StartNotify", &())
                .map_err(d);
            if started.is_err() {
                self.stop_listener();
            }
            started
        }

        fn unsubscribe(&mut self, _service: usize, _characteristic: usize) {
            self.stop_listener();
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

    /// BlueZ's unique bus name, which is what the sender of its signals reads.
    fn bluez_owner(conn: &Connection) -> std::result::Result<String, String> {
        let name = zbus::names::BusName::try_from(BLUEZ).map_err(d)?;
        let proxy = zbus::blocking::fdo::DBusProxy::new(conn).map_err(d)?;
        proxy.get_name_owner(name).map(|owner| owner.to_string()).map_err(d)
    }

    /// `StopNotify`, best effort: BlueZ answers by announcing `Notifying=false`, which wakes the
    /// listener.
    fn stop_notify(conn: &Connection, path: &str) {
        if let Ok(p) = link_proxy(conn, path, CHARACTERISTIC) {
            let _ = p.call::<_, _, ()>("StopNotify", &());
        }
    }

    /// `Disconnect`, best effort: a device that has gone out of range cannot be closed politely.
    /// BlueZ announces the change on the device, which wakes a listener whose characteristic
    /// would never say anything again.
    fn disconnect(conn: &Connection, device: &str) {
        if let Ok(p) = link_proxy(conn, device, DEVICE) {
            let _ = p.call::<_, _, ()>("Disconnect", &());
        }
    }

    impl Link {
        /// Stops the current subscription's listener and waits for it, a second at most.
        fn stop_listener(&mut self) {
            let conn = &self.conn;
            self.listener
                .stop(|path| stop_notify(conn, path), Duration::from_secs(1));
        }
    }

    impl Drop for Link {
        fn drop(&mut self) {
            // The listener is stopped and joined, not abandoned: left running, its thread and its
            // bus connection outlive the session whenever the device has already gone and
            // nothing more is ever signalled. `StopNotify`, then `Disconnect`, so that either
            // one's announcement wakes it.
            let (conn, device) = (&self.conn, &self.device);
            let had = self.listener.stop(
                |path| {
                    stop_notify(conn, path);
                    disconnect(conn, device);
                },
                Duration::from_secs(1),
            );
            if !had {
                disconnect(conn, device);
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
    fn notification_is_chosen_when_the_characteristic_offers_both() {
        // The C# subscribed with Notify whenever the characteristic offered it and used Indicate
        // only otherwise. A clone that advertises both but only ever sends notifications is
        // silent if indications are asked for, and is then rejected after the 3 s probe.
        let mut svc = serial_service(SERIAL_SERVICES[0]);
        svc.characteristics[1].props = CharProps {
            notify: true,
            indicate: true,
            ..Default::default()
        };
        let w = world(vec![svc], Some(0));
        let mut t = transport(&w, 0);

        t.open().unwrap();
        assert_eq!(w.lock().unwrap().subscribed, vec![(0, 1, false)]);
    }

    #[test]
    fn the_first_writable_and_the_first_notifying_characteristic_are_used_in_table_order() {
        // With several candidates, the C# took the first of each in the order the device lists
        // them (attribute-handle order), not the first by UUID.
        let mut svc = serial_service(SERIAL_SERVICES[0]);
        let write = |uuid| GattCharacteristic {
            uuid,
            props: CharProps {
                write: true,
                ..Default::default()
            },
        };
        let notify = |uuid| GattCharacteristic {
            uuid,
            props: CharProps {
                notify: true,
                ..Default::default()
            },
        };
        // Table order: a notifier whose UUID sorts last comes first, then a writer likewise.
        svc.characteristics = vec![
            notify(uuid16(0xFFF9)),
            write(uuid16(0xFFF8)),
            notify(uuid16(0xFFF1)),
            write(uuid16(0xFFF2)),
        ];
        let w = world(vec![svc], Some(0));
        let mut t = transport(&w, 0);

        t.open().unwrap();
        let w = w.lock().unwrap();
        assert_eq!(w.subscribed, vec![(0, 0, false)]);
        assert_eq!(w.writes[0].1, 1);
    }

    #[test]
    fn characteristic_properties_are_read_from_the_gatt_bits() {
        assert_eq!(CharProps::from_bits(0), CharProps::default());
        let p = CharProps::from_bits(0x04 | 0x08 | 0x10 | 0x20);
        assert!(p.write && p.write_without_response && p.notify && p.indicate);
        // Broadcast, read, signed writes and the encryption-required variants are not ours.
        assert_eq!(
            CharProps::from_bits(0x01 | 0x02 | 0x40 | 0x80 | 0x100 | 0x200),
            CharProps::default()
        );
    }

    #[test]
    fn a_reply_split_across_notifications_is_reassembled_in_order() {
        // An ELM327 answer is longer than one 20-byte notification, and arrives in pieces that
        // do not fall on line boundaries. The read gives the bytes back as one stream.
        let (mut t, sink) = open_with_sink();
        t.discard_input();
        let reply: &'static [u8] = b"41 0C 1A F8\r41 0D 32\r\r>";
        let feeder = thread::spawn(move || {
            for piece in reply.chunks(7) {
                thread::sleep(Duration::from_millis(10));
                sink.push(piece);
            }
        });

        let mut got = vec![0u8; reply.len()];
        assert_eq!(t.read(&mut got, Duration::from_secs(2)).unwrap(), reply.len());
        assert_eq!(got, reply);
        feeder.join().unwrap();
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
    fn a_mailbox_wait_returns_what_another_thread_posts_or_times_out() {
        let mailbox = Arc::new(Mailbox::new(None::<u32>));
        let poster = mailbox.clone();
        let t = thread::spawn(move || {
            thread::sleep(Duration::from_millis(20));
            poster.update(|s| *s = Some(7));
        });
        assert_eq!(mailbox.wait_for(Duration::from_secs(2), |s| s.take()), Some(7));
        t.join().unwrap();

        // Taken, so a second wait finds nothing and gives up on time.
        let started = Instant::now();
        assert_eq!(mailbox.wait_for(Duration::from_millis(40), |s| s.take()), None);
        assert!(started.elapsed() >= Duration::from_millis(35));
        assert_eq!(mailbox.read(|s| *s), None);
    }

    #[test]
    fn a_mailbox_outcome_posted_before_the_wait_is_not_missed() {
        let mailbox = Mailbox::new(Vec::<u8>::new());
        mailbox.update(|s| s.push(1));
        assert_eq!(mailbox.wait_for(Duration::ZERO, |s| s.pop()), Some(1));
    }

    #[test]
    fn a_corebluetooth_uuid_string_is_expanded_to_128_bits() {
        assert_eq!(uuid_from_cb_string("FFF0"), Some(SERIAL_SERVICES[0]));
        assert_eq!(uuid_from_cb_string("ae00"), Some(SERIAL_SERVICES[1]));
        assert_eq!(uuid_from_cb_string("0000FFE0"), Some(SERIAL_SERVICES[2]));
        assert_eq!(
            uuid_from_cb_string("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"),
            Some(SERIAL_SERVICES[3])
        );
        assert_eq!(uuid_from_cb_string("nonsense"), None);
        assert_eq!(uuid_from_cb_string("ZZZZ"), None);
    }

    #[test]
    fn a_late_unsubscribe_outcome_is_not_taken_for_the_next_subscribes() {
        // try_use lets go of service A (setNotifyValue(false), whose callback comes later) and
        // then subscribes B. The callback for A, arriving while B waits, must not satisfy B.
        let (a, b) = (0xA000usize, 0xB000usize);
        let mailbox = Arc::new(Mailbox::new(Outcomes::default()));

        mailbox.update(|o| o.expect(a)); // unsubscribe A requested
        mailbox.update(|o| o.expect(b)); // subscribe B requested

        let callbacks = mailbox.clone();
        let device = thread::spawn(move || {
            thread::sleep(Duration::from_millis(20));
            callbacks.update(|o| o.post(a, Ok(()))); // A's late reply...
            thread::sleep(Duration::from_millis(60));
            callbacks.update(|o| o.post(b, Err("denied".into()))); // ...then B's own
        });

        let started = Instant::now();
        let got = mailbox.wait_for(Duration::from_secs(2), |o| o.take(b));
        assert_eq!(got, Some(Err("denied".to_string())));
        assert!(
            started.elapsed() >= Duration::from_millis(70),
            "B returned on A's outcome"
        );
        device.join().unwrap();
        // A's outcome is still A's.
        assert_eq!(mailbox.wait_for(Duration::ZERO, |o| o.take(a)), Some(Ok(())));
    }

    #[test]
    fn a_write_outcome_after_a_timeout_is_not_the_next_writes() {
        let (first, second) = (1usize, 2usize);
        let mailbox = Mailbox::new(Outcomes::default());
        mailbox.update(|o| o.expect(first));
        assert_eq!(mailbox.wait_for(Duration::from_millis(10), |o| o.take(first)), None);
        mailbox.update(|o| o.expect(second));
        mailbox.update(|o| o.post(first, Ok(()))); // the timed-out write's reply, late
        assert_eq!(mailbox.wait_for(Duration::from_millis(10), |o| o.take(second)), None);
    }

    #[test]
    fn a_signal_from_anyone_but_bluez_is_not_a_notification() {
        let owner = ":1.42";
        let watched = "/org/bluez/hci0/dev_X/service000a/char000b";
        assert!(is_notification_from(Some(owner), owner, Some(watched), watched));
        // Another process on the system bus claiming to be the characteristic.
        assert!(!is_notification_from(Some(":1.666"), owner, Some(watched), watched));
        assert!(!is_notification_from(None, owner, Some(watched), watched));
        // BlueZ, but another characteristic.
        assert!(!is_notification_from(
            Some(owner),
            owner,
            Some("/org/bluez/hci0/dev_X/service000a/char000d"),
            watched
        ));
        assert!(!is_notification_from(Some(owner), owner, None, watched));
    }

    #[test]
    fn dropping_the_slot_that_holds_a_listener_stops_it() {
        use std::sync::atomic::{AtomicBool, Ordering};
        let ended = Arc::new(AtomicBool::new(false));
        let flag = ended.clone();
        let mut slot = ListenerSlot::default();
        slot.set(
            Listener::spawn(move |stop| {
                while !stop.load(Ordering::SeqCst) {
                    thread::sleep(Duration::from_millis(2));
                }
                flag.store(true, Ordering::SeqCst);
            }),
            "/p".into(),
        );
        drop(slot);
        let deadline = Instant::now() + Duration::from_secs(2);
        while !ended.load(Ordering::SeqCst) && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(5));
        }
        assert!(ended.load(Ordering::SeqCst), "the listener was left running");
    }

    #[test]
    fn stopping_sets_the_signal_before_waking_and_joins_the_listener() {
        use std::sync::atomic::{AtomicBool, Ordering};
        // The listener is blocked until woken, as a D-Bus iterator is until BlueZ says something.
        let (wake_tx, wake_rx) = std::sync::mpsc::channel::<()>();
        let saw_stop_when_woken = Arc::new(AtomicBool::new(false));
        let seen = saw_stop_when_woken.clone();
        let mut slot = ListenerSlot::default();
        slot.set(
            Listener::spawn(move |stop| {
                let _ = wake_rx.recv();
                seen.store(stop.load(Ordering::SeqCst), Ordering::SeqCst);
            }),
            "/org/bluez/char".into(),
        );

        let woken_for = std::cell::RefCell::new(String::new());
        let started = Instant::now();
        let had = slot.stop(
            |path| {
                *woken_for.borrow_mut() = path.to_string();
                wake_tx.send(()).unwrap();
            },
            Duration::from_secs(2),
        );

        assert!(had);
        assert_eq!(*woken_for.borrow(), "/org/bluez/char");
        // It had ended by the time stop() returned, well before the wait ran out, and had seen
        // the signal already set when it woke.
        assert!(started.elapsed() < Duration::from_secs(1));
        assert!(saw_stop_when_woken.load(Ordering::SeqCst));
        // Nothing left to stop.
        assert!(!slot.stop(|_| panic!("nothing to wake"), Duration::from_millis(1)));
    }

    #[test]
    fn bluez_objects_are_ordered_by_attribute_handle_not_by_name_or_uuid() {
        let base = "/org/bluez/hci0/dev_B9_69_75_AA_93_F6";
        assert_eq!(handle_of_path(&format!("{base}/service000a")), 0x0a);
        assert_eq!(handle_of_path(&format!("{base}/service000a/char000b")), 0x0b);
        assert_eq!(handle_of_path(&format!("{base}/service000a/char0010")), 0x10);
        assert_eq!(handle_of_path(&format!("{base}/service000a/desc0011")), u32::MAX);

        let mut paths = [
            format!("{base}/service000a/char0010"),
            format!("{base}/service000a/char000b"),
            format!("{base}/service000a/char000d"),
        ];
        paths.sort_by_key(|p| handle_of_path(p));
        assert!(paths[0].ends_with("char000b") && paths[1].ends_with("char000d") && paths[2].ends_with("char0010"));
    }

    #[test]
    fn each_listener_has_a_stop_signal_of_its_own() {
        use std::sync::atomic::{AtomicUsize, Ordering};
        let spin = |ticks: Arc<AtomicUsize>| {
            Listener::spawn(move |stop| {
                while !stop.load(Ordering::SeqCst) {
                    ticks.fetch_add(1, Ordering::SeqCst);
                    thread::sleep(Duration::from_millis(2));
                }
            })
        };
        let (a_ticks, b_ticks) = (Arc::new(AtomicUsize::new(0)), Arc::new(AtomicUsize::new(0)));

        let a = spin(a_ticks.clone());
        thread::sleep(Duration::from_millis(20));

        // Stopping one and starting the next: the first must really end, and the second must not
        // be affected by the first's signal.
        a.stop(Duration::from_secs(2));
        let a_after = a_ticks.load(Ordering::SeqCst);
        let b = spin(b_ticks.clone());
        thread::sleep(Duration::from_millis(30));
        assert_eq!(
            a_ticks.load(Ordering::SeqCst),
            a_after,
            "the stopped listener is still running"
        );
        assert!(b_ticks.load(Ordering::SeqCst) > 0);

        b.stop(Duration::from_secs(2));
        let b_after = b_ticks.load(Ordering::SeqCst);
        thread::sleep(Duration::from_millis(20));
        assert_eq!(b_ticks.load(Ordering::SeqCst), b_after);
    }

    #[test]
    fn a_listener_that_will_not_wake_is_left_to_end_by_itself() {
        // Blocked in a call that does not return: stop() gives up after its wait rather than hang.
        let release = Arc::new(Mutex::new(()));
        let held = release.lock().unwrap();
        let blocker = release.clone();
        let l = Listener::spawn(move |_| {
            drop(blocker.lock());
        });
        let started = Instant::now();
        l.stop(Duration::from_millis(50));
        assert!(started.elapsed() < Duration::from_secs(2));
        drop(held);
    }

    #[test]
    fn the_uuid_helper_builds_the_bluetooth_base_form() {
        assert_eq!(uuid16(0x180F).to_string(), "0000180f-0000-1000-8000-00805f9b34fb");
    }
}
