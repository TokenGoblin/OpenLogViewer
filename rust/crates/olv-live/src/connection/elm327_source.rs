//! Ported from `Elm327Source.cs`. Owned by package `w3-connection`.
//!
//! A live source over an ELM327 OBD2 adapter: any compliant vehicle, with no definition
//! file and no aftermarket ECU.
//!
//! This is the one link here where nothing has to be known in advance. The parameter
//! numbering, the scaling and the units are the same on every OBD2 vehicle by law, and the
//! car itself reports which parameters it answers to, so a connection produces named and
//! scaled channels on a car nobody has ever plugged this into.
//!
//! What it costs is speed. Every other ECU here hands over its whole realtime block in one
//! exchange; OBD2 has no such thing, so each parameter is a separate request and a separate
//! wait. A row of readings takes a good part of a second, against forty per second on a
//! tuning cable. That is the protocol and not this implementation: it is fine for watching
//! a car and no use for catching a misfire.
//!
//! The one thing here that changes a car is [`Elm327Source::clear_faults`] (mode 04), and it
//! is only ever called by an explicit user action above this crate.
//!
//! **Sharing.** The C# shared one `IEcuTransport` between the source and the `Elm327` it
//! owns, and serialised every adapter command with a lock inside `Elm327` so a fault scan
//! from the user interface and the poll thread take turns. The same is done here with
//! [`SharedTransport`] (one transport, two owners) and an `Arc<Mutex<Elm327>>` that every
//! command locks for its own length; [`Elm327Source::faults`] hands the UI a handle that
//! scans through the same lock.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use olv_core::model::LiveSource;
use olv_core::{Error, Result};
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::obd::elm327::{self, BatchAnswer, Elm327};
use olv_protocol::obd::obd2_faults::{self, FaultClear, FaultScan};
use olv_protocol::obd::obd2_pids::{self, Obd2Pid};
use olv_transport::transport::serial_ecu_transport::SerialEcuTransport;
use olv_transport::transport::wifi_ecu_transport::{self, WifiEcuTransport};

/// What asking for several parameters at once has already cost an adapter (`IObd2BatchMemory`).
///
/// Remembered between sessions, and it has to be. A dongle that cannot survive a batched
/// request does not refuse one: it answers, and then the link dies, so learning costs a
/// dropped session every time. Learning it once per adapter is a few seconds of churn on
/// one drive; relearning it every time the application starts is a few seconds of blank
/// gauges on every drive, for ever.
///
/// Keyed by whatever names the adapter: the address of a Wi-Fi dongle, or the name it
/// reports. That name is not unique to the device (every clone claims to be an ELM327), so
/// this is a verdict on a kind of adapter rather than on one particular unit, which is the
/// right grain for what is being remembered.
///
/// `&self` methods and `Send + Sync`: the implementation is shared with the settings store.
pub trait Obd2BatchMemory: Send + Sync {
    /// Links that batching has demonstrably killed on this adapter.
    fn deaths_on(&self, adapter: &str) -> i32;

    /// Records another one.
    fn died(&self, adapter: &str);
}

/// One transport with two owners: the source (to close, reopen and test it) and the
/// [`Elm327`] it talks through. The C# passed the same object to both. Every call locks for
/// its own length only.
#[derive(Clone)]
pub struct SharedTransport(Arc<Mutex<Box<dyn EcuTransport>>>);

impl SharedTransport {
    pub fn new(transport: Box<dyn EcuTransport>) -> Self {
        Self(Arc::new(Mutex::new(transport)))
    }

    fn lock(&self) -> MutexGuard<'_, Box<dyn EcuTransport>> {
        self.0.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

impl EcuTransport for SharedTransport {
    fn is_open(&self) -> bool {
        self.lock().is_open()
    }

    fn open(&mut self) -> Result<()> {
        self.lock().open()
    }

    fn close(&mut self) {
        self.lock().close();
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.lock().write(data)
    }

    fn write_timeout(&self) -> Duration {
        self.lock().write_timeout()
    }

    fn set_write_timeout(&mut self, timeout: Duration) {
        self.lock().set_write_timeout(timeout);
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        self.lock().read(buffer, timeout)
    }

    fn discard_input(&mut self) {
        self.lock().discard_input();
    }
}

/// Consecutive non-answers before a parameter is left out of the rotation.
///
/// The car said it supported these, so a few refusals are more likely to be a busy module
/// than a mistake; but a parameter that never answers costs a round trip every cycle, and
/// on this link that is the whole budget.
const GIVE_UP_AFTER: i32 = 6;

/// Links batching may kill on one adapter before it is not tried again.
///
/// Two rather than one, because a link can die for reasons that have nothing to do with
/// the request that happened to be in flight (out of range, key off, a cable pulled) and
/// condemning a capable adapter on one of those would cost the whole advantage permanently.
pub const BATCH_DEATHS_BEFORE_GIVING_UP: i32 = 2;

/// Times a capability mask is asked for before it is believed absent.
const DISCOVERY_ATTEMPTS: usize = 4;

/// Empty batched replies in a row before the idea is dropped.
const BATCH_MISSES_BEFORE_GIVING_UP: i32 = 3;

/// What a second reset gets, when the first one heard nothing at all.
///
/// Short on purpose, because it is asking a narrower question. The first attempt has to be
/// generous (this adapter's banner has been seen arriving well over a second late) but a
/// link that has already failed to produce it once is being asked only whether anything is
/// there, and a live one answers that in tens of milliseconds.
const CONFIRMING_SILENCE: Duration = Duration::from_secs(1);

/// Speeds an OBD2 adapter might be listening at, in the order worth trying.
///
/// Unlike every other link here, this one cannot be assumed. A genuine ELM327 leaves the
/// factory at 38,400; clones ship at that, at 9,600, or at 115,200 depending on the batch;
/// the fast USB ones run at 500,000. Get it wrong and the adapter is not silent: it answers
/// with rubbish, which is a worse symptom than nothing.
///
/// A Bluetooth adapter ignores the setting altogether, the speed being negotiated by the
/// radio, so the first attempt is the only one for a paired dongle.
pub const BAUD_RATES: [u32; 4] = [38400, 115200, 9600, 500000];

/// Whether an error is one the C# catches as `EcuProtocolException or IOException or
/// InvalidOperationException`: the ways a link dies.
fn is_link_failure(error: &Error) -> bool {
    matches!(error, Error::Protocol(_) | Error::Io(_) | Error::InvalidOperation(_))
}

/// A handle for scanning and clearing faults from another thread while the session polls.
///
/// Every command goes through the adapter's own gate, so a scan and a round of readings
/// take turns rather than interleaving: the poll pauses for as long as this takes, which on
/// a car with codes to report is a second or two.
#[derive(Clone)]
pub struct Elm327Faults(Arc<Mutex<Elm327>>);

impl Elm327Faults {
    /// Asks the car what it is complaining about.
    pub fn scan(&self) -> Result<FaultScan> {
        obd2_faults::scan(&mut self.0.lock().unwrap_or_else(PoisonError::into_inner))
    }

    /// Asks the car to erase them.
    ///
    /// Separated from the scan by more than convenience: this is the one thing this
    /// application can ask a standard vehicle to do that changes it, and what it erases
    /// goes well beyond the codes; see [`obd2_faults::clear`].
    pub fn clear(&self) -> Result<FaultClear> {
        obd2_faults::clear(&mut self.0.lock().unwrap_or_else(PoisonError::into_inner))
    }
}

pub struct Elm327Source {
    transport: SharedTransport,
    elm: Arc<Mutex<Elm327>>,
    pids: Vec<Obd2Pid>,
    at: Vec<usize>,
    values: Vec<f64>,
    silences: Vec<i32>,
    answered: Vec<bool>,
    hot: Vec<usize>,
    index_of: HashMap<u8, usize>,

    rotating: usize,
    batch_misses: i32,
    batch_given_up: bool,
    died_batching: bool,

    adapter: String,
    link: String,
    supports: Vec<u8>,
    memory: Option<Arc<dyn Obd2BatchMemory>>,

    names: Vec<String>,
    units: Vec<String>,
    digits: Vec<i32>,
    retries: i32,
    batching: bool,
}

impl Elm327Source {
    fn new(
        transport: SharedTransport,
        elm: Arc<Mutex<Elm327>>,
        pids: Vec<Obd2Pid>,
        adapter: String,
        link: String,
        supports: Vec<u8>,
        memory: Option<Arc<dyn Obd2BatchMemory>>,
    ) -> Self {
        let hot = (0..pids.len()).filter(|&i| pids[i].hot).collect();
        // `ToDictionary` would throw on a repeated PID; `known` never yields one.
        let index_of = pids.iter().enumerate().map(|(i, p)| (p.pid, i)).collect();

        let mut names = Vec::new();
        let mut units = Vec::new();
        let mut digits = Vec::new();
        let mut at = Vec::with_capacity(pids.len());

        for pid in &pids {
            at.push(names.len());

            for channel in &pid.channels {
                names.push(channel.name.clone());
                units.push(channel.units.clone());
                digits.push(channel.digits);
            }
        }

        Self {
            transport,
            elm,
            at,
            // Nothing has been read yet, and zero is a reading. A gauge shows an empty
            // face until its parameter has actually answered.
            values: vec![f64::NAN; names.len()],
            silences: vec![0; pids.len()],
            answered: vec![false; pids.len()],
            hot,
            index_of,
            pids,
            rotating: 0,
            batch_misses: 0,
            batch_given_up: false,
            died_batching: false,
            adapter,
            link,
            supports,
            memory,
            names,
            units,
            digits,
            retries: 0,
            batching: false,
        }
    }

    fn elm(&self) -> MutexGuard<'_, Elm327> {
        self.elm.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// What the adapter calls itself, e.g. "ELM327 v1.5".
    pub fn adapter(&self) -> &str {
        &self.adapter
    }

    /// Where it was reached, for the links that have such a thing.
    ///
    /// "192.168.0.10:35000" for a Wi-Fi dongle and empty for everything else. A port and a
    /// paired radio are already named by the thing that listed them; a Wi-Fi adapter is
    /// listed by nothing, so unless the session says which address answered there is
    /// nowhere to read it back off.
    pub fn link(&self) -> &str {
        &self.link
    }

    /// The parameters this car reported and this knows how to decode.
    pub fn parameters(&self) -> &[Obd2Pid] {
        &self.pids
    }

    /// Every parameter the car said it supports, decodable or not.
    ///
    /// Kept because the two lists are not the same and the difference used to vanish
    /// without trace: the car enumerates what it will answer, this keeps the ones it has a
    /// decoder for, and the rest were dropped on the floor with nothing said. On a vehicle
    /// offering a channel this cannot read, that is a missing gauge that looks exactly like
    /// a car that never had it.
    pub fn supports(&self) -> &[u8] {
        &self.supports
    }

    /// What the car offers that this cannot yet read, in order.
    ///
    /// The list worth acting on: every one of these is a channel the vehicle is willing to
    /// report and that only wants a decoder writing. Anything above 0x60 also proves the
    /// discovery walk reached that far, which it did not before.
    pub fn undecoded(&self) -> Vec<u8> {
        // The support queries themselves are not readings; a car listing 0x20 is saying
        // "ask me about the next range", which has already happened by the time anyone
        // reads this.
        let mut undecoded: Vec<u8> = self
            .supports
            .iter()
            .copied()
            .filter(|p| !obd2_pids::all().iter().any(|k| k.pid == *p) && !obd2_pids::SUPPORT_QUERIES.contains(p))
            .collect();

        undecoded.sort_unstable();
        undecoded.dedup();
        undecoded
    }

    /// Whether this car is being asked for several parameters at a time.
    ///
    /// Off until the car has been shown to do it, off for good if it stops, and never even
    /// tried on a bus that cannot: see `try_batching`. Worth reporting, because it is the
    /// difference between a rev counter that moves and one that catches up.
    pub fn batching(&self) -> bool {
        self.batching
    }

    /// Opens the adapter, asks the car what it supports, and builds the channels from the
    /// answer.
    ///
    /// Done before the session starts rather than inside [`LiveSource::open`], because
    /// which channels exist is only knowable by asking and a session needs to know its
    /// columns before it can record any.
    ///
    /// `link` is where this adapter was reached, where that is a thing worth saying: the
    /// address of a Wi-Fi dongle, which is not discoverable and so is the only way back to
    /// the same device. Empty for a port or a radio, which name themselves. `memory` is what
    /// batching has cost this adapter before, if anything is keeping track; without one,
    /// every session finds out again the hard way.
    pub fn connect(
        transport: Box<dyn EcuTransport>,
        link: &str,
        memory: Option<Arc<dyn Obd2BatchMemory>>,
    ) -> Result<Elm327Source> {
        let shared = SharedTransport::new(transport);
        shared.clone().open()?;

        let mut elm = Elm327::new(Box::new(shared.clone()));
        let reset = elm.reset(None)?;

        // Asked after the reset, so the answers arrive with the echo already off. The
        // name from the reset is kept as the fallback: it is what proves an adapter
        // answered at all, and Identify can come back empty on a device that is being sent
        // noise.
        let mut adapter = if reset.is_empty() {
            String::new()
        } else {
            elm.identify()?
        };
        if adapter.is_empty() {
            adapter = reset.clone();
        }

        // Before anything whose answer is kept. The protocol search happens on the first
        // request the car ever sees, and it is narrated: this spends it, so discovery is
        // asked of an adapter that has already settled.
        if !reset.is_empty() {
            elm.warm_up()?;
        }

        let supported = supported(&mut elm)?;
        let pids = obd2_pids::known(supported.iter().copied());

        // Told apart deliberately. An adapter that named itself and a car that listed
        // nothing is a key turned off, which the user can fix in a second; an adapter that
        // did not name itself is the wrong port or the wrong speed, and waiting for the
        // ignition would be waiting for nothing.
        if pids.is_empty() {
            return Err(Error::Protocol(if adapter.is_empty() {
                "Nothing here answered as an OBD2 adapter.".to_string()
            } else {
                format!(
                    "{adapter} answered, but the vehicle listed no parameters. \
                     Check the ignition is on \u{2014} most cars report nothing with the key out."
                )
            }));
        }

        let mut source = Elm327Source::new(
            shared,
            Arc::new(Mutex::new(elm)),
            pids,
            adapter,
            link.to_string(),
            supported,
            memory,
        );

        // The probe here is documented as being enough to kill one of these dongles
        // outright. Letting that escape leaks the transport and tells the memory nothing,
        // so the next connection makes the same mistake. (Dropping the source closes the
        // transport; `try_batching` has already told the memory.)
        source.try_batching()?;

        Ok(source)
    }

    /// What names this adapter for the purpose of remembering things about it.
    ///
    /// The address where there is one, since that is the device rather than the story it
    /// tells about itself: every clone answers to the name ELM327.
    fn key(&self) -> &str {
        if self.link.is_empty() {
            &self.adapter
        } else {
            &self.link
        }
    }

    /// Finds out whether this car will answer several parameters at once, and turns it on
    /// if it will.
    ///
    /// Asked rather than assumed, and asked in a way that can only answer yes on evidence.
    /// **Two parameters must come back**: one proves nothing, because an ordinary
    /// single-parameter reply to the first one listed looks exactly like a batched reply
    /// that carried one.
    ///
    /// Only on a bus positively identified as CAN. Not "not identified as slow": an unknown
    /// protocol is neither, and a request like this reaching a J1850 vehicle is a malformed
    /// one rather than a refused one. An asleep ECU answering `STOPPED` is how a link comes
    /// to be unidentified at exactly the wrong moment.
    fn try_batching(&mut self) -> Result<()> {
        self.batching = false;

        if self.batch_given_up {
            return Ok(());
        }

        // Not even asked, once an adapter has form. The probe is itself a batched request;
        // on the dongle this was measured against, that one request is enough to kill the
        // session, so probing something already known to fail is not a cheap check, it is
        // the whole cost of the thing being checked for.
        if let Some(memory) = &self.memory
            && memory.deaths_on(self.key()) >= BATCH_DEATHS_BEFORE_GIVING_UP
        {
            self.batch_given_up = true;
            return Ok(());
        }

        if self.elm().protocol_number()? < elm327::FIRST_CAN_PROTOCOL {
            return Ok(());
        }

        let indices: Vec<usize> = if self.hot.len() >= 2 {
            self.hot.clone()
        } else {
            (0..self.pids.len()).collect()
        };
        let probe = self.chosen(&indices);
        if probe.len() < 2 {
            return Ok(());
        }

        // The probe is itself a batched request, so a link that dies during it died
        // batching, and this is the one place where that is certain rather than inferred.
        // Recorded before the exception leaves, or the next connection probes again and
        // dies again.
        let outcome = self.elm().read_many(&probe, &obd2_pids::data_bytes_of, None);

        match outcome {
            Ok(answers) => {
                self.batching = answers.len() >= 2;
                Ok(())
            }
            Err(e) if is_link_failure(&e) => {
                self.batching = false;
                self.condemn();
                Err(e)
            }
            Err(e) => Err(e),
        }
    }

    /// The parameter numbers behind a set of positions, at most a batch's worth.
    fn chosen(&self, indices: &[usize]) -> Vec<u8> {
        indices
            .iter()
            .take(elm327::MAX_BATCH_PIDS)
            .map(|&i| self.pids[i].pid)
            .collect()
    }

    /// Finds an adapter on a port, whatever speed it is set to.
    ///
    /// Each speed costs a reset and a wait, so the order matters more than the list does; a
    /// wrong speed is recognised by the adapter failing to name itself, which is what it
    /// does when it is being sent noise.
    pub fn connect_on_port(port_name: &str, memory: Option<Arc<dyn Obd2BatchMemory>>) -> Result<Elm327Source> {
        let mut last: Option<Error> = None;

        for baud in BAUD_RATES {
            let transport = SerialEcuTransport::new(port_name, baud).with_open_attempts(3);

            match Elm327Source::connect(Box::new(transport), "", memory.clone()) {
                Ok(source) => return Ok(source),
                // The failed attempt's transport went with it.
                Err(e) => last = Some(e),
            }
        }

        Err(last.unwrap_or_else(|| Error::Protocol(format!("Nothing on {port_name} answered as an OBD2 adapter."))))
    }

    /// Finds an adapter over Wi-Fi, at an address written "host" or "host:port".
    ///
    /// Nothing is searched for and nothing can be: a Wi-Fi dongle becomes no COM port,
    /// pairs with nothing and is listed nowhere, so an address is the only way to reach
    /// one. [`wifi_ecu_transport::KNOWN_ADDRESSES`] is where the ones worth trying live.
    pub fn connect_over_wifi(address: &str, memory: Option<Arc<dyn Obd2BatchMemory>>) -> Result<Elm327Source> {
        let transport = WifiEcuTransport::at(address)?;
        let link = transport.address();

        Elm327Source::connect(Box::new(transport), &link, memory)
    }

    /// The same, trying each address a Wi-Fi adapter is known to answer on.
    ///
    /// The first failure is the one reported rather than the last. A dongle that is there
    /// answers on the first address (it is the one a Vgate iCar Pro uses) so a run through
    /// the list ends with "nothing at 192.168.4.1", which names an address the user has
    /// never heard of and sends them looking in the wrong place.
    pub fn connect_over_known_wifi(memory: Option<Arc<dyn Obd2BatchMemory>>) -> Result<Elm327Source> {
        let mut first: Option<Error> = None;

        for address in wifi_ecu_transport::KNOWN_ADDRESSES {
            match Elm327Source::connect_over_wifi(address, memory.clone()) {
                Ok(source) => return Ok(source),
                Err(e) => {
                    first.get_or_insert(e);
                }
            }
        }

        Err(first.unwrap_or_else(|| Error::Protocol("No Wi-Fi OBD2 adapter answered.".to_string())))
    }

    /// Polls one round and returns every channel (`ReadRound`, watched).
    ///
    /// The fast ones every time, and as many of the rest as a request will carry: one of
    /// them where the car answers a parameter at a time, six where it does not have to. A
    /// slow channel therefore holds its previous reading between updates, which is what a
    /// coolant temperature does anyway; what must not happen is the rev counter being
    /// dragged down to the speed of the fuel level, and that is why the two sets are asked
    /// for separately rather than as one long list.
    fn read_round(&mut self) -> Result<Vec<f64>> {
        let mut answered = false;
        let mut asked = false;

        // One request for the parameters a needle follows, and one for as many of the rest
        // as will fit, where the car allows it. A round that used to be six exchanges for
        // six readings is two for eleven, and the slow channels stop being a queue.
        let due = self.next_rotating(if self.batching { elm327::MAX_BATCH_PIDS } else { 1 });

        if !self.hot.is_empty() {
            asked = true;
            let hot = self.hot.clone();
            answered |= self.poll_group(&hot)?;
        }

        if !due.is_empty() {
            asked = true;
            answered |= self.poll_group(&due)?;
        }

        // Every question went unanswered. One parameter falling silent is ordinary; the
        // whole car doing so is the link having gone.
        if asked && !answered {
            // Remembered across the death, because nothing else survives it. A dongle
            // killed by a batched request does not refuse one: it answers, in full and on
            // time, and then stops existing, so the reply says nothing is wrong, and by the
            // time anything is plainly wrong there is no link left to ask. What was in
            // flight is the only evidence there will ever be, and `recover` is where it is
            // weighed.
            self.died_batching = self.batching;

            self.retries += 1;

            return Err(Error::Protocol(
                "The adapter answered nothing this round. If the engine was switched off, \
                 most cars stop responding to OBD2 requests with the key out."
                    .to_string(),
            ));
        }

        Ok(self.values.clone())
    }

    /// The next parameters due a turn, skipping any that have been given up on.
    ///
    /// `wanted` is one when each costs its own round trip and a batch's worth when they do
    /// not: the same rotation either way, taken in larger bites.
    fn next_rotating(&mut self, wanted: usize) -> Vec<usize> {
        let mut due = Vec::with_capacity(wanted);

        let mut tried = 0;
        while tried < self.pids.len() && due.len() < wanted {
            let at = self.rotating;
            self.rotating = (self.rotating + 1) % self.pids.len();

            if !self.pids[at].hot && self.silences[at] < GIVE_UP_AFTER {
                due.push(at);
            }

            tried += 1;
        }

        due
    }

    /// Reads a set of parameters, in one request where the car allows it.
    ///
    /// A batch that comes back with nothing is a *request* that failed, which says nothing
    /// at all about whether the car has these sensors, so the round is retried one
    /// parameter at a time rather than concluded from.
    fn poll_group(&mut self, due: &[usize]) -> Result<bool> {
        let batched = self.batching && due.len() >= 2;

        if batched && let Some(absent) = self.poll_together(due)? {
            // A REPLY CARRYING ONLY SOME OF WHAT WAS ASKED FOR IS NOT A ROUND THAT WORKED.
            // The rest are asked for singly, exactly as they would have been had this link
            // never batched at all.
            //
            // Without that they are asked for once and then never again. A parameter that
            // has answered before is deliberately never retired (see `missed`, and the
            // reading it protects) so nothing else in here will ever come back to one that
            // has stopped appearing in the reply. It keeps its gauge, holding a value from
            // minutes ago, and a stale reading is indistinguishable on screen from a live
            // one. A reply cut short by a segmented or truncated frame is exactly how a
            // parameter comes to be absent from every batch while the car is answering for
            // it perfectly well.
            //
            // Bounded by what the reply left out: a car that answers the whole batch pays
            // nothing here, and one that answers none of it costs what not batching would
            // have cost anyway.
            for index in absent {
                self.poll(index)?;
            }

            return Ok(true);
        }

        let mut answered = false;
        for &index in due {
            answered |= self.poll(index)?;
        }

        // ONLY NOW IS THERE EVIDENCE ABOUT BATCHING, and only if these answered.
        //
        // A silent batch on its own says nothing about the request: it is also what a link
        // that has died looks like, and those are not rare (this dongle stops answering
        // anything, singles included, while the socket goes on reporting itself connected).
        // Counting that as a strike against batching writes a permanent verdict against a
        // capable adapter for something it did not do.
        //
        // What tells them apart is the fallback. Singles answering where the batch did not
        // is the request being refused; singles falling silent too is the link, and
        // batching is a bystander.
        if batched && answered {
            self.blame();
        }

        Ok(answered)
    }

    /// Counts one demonstrated failure of batching, and gives it up on the third.
    ///
    /// **Given up for this link only, and not written down.** What this counts is a batch
    /// coming back empty while the singles answer, which is the *car* declining to be asked
    /// several things at once, with the adapter perfectly healthy. Recording that against
    /// the adapter would be blaming the wrong party, and the key these are filed under is a
    /// Wi-Fi address that is the same on every one of these dongles, so two drives in such a
    /// car would turn batching off for every vehicle thereafter.
    ///
    /// A death is different and is recorded: see `condemn`.
    ///
    /// It also counts faster than it reads: a round asks two batched questions, so a car
    /// that declines them both is two of the three straight away. That is deliberate: one
    /// round of clear evidence is enough to stop paying for something this car does not do.
    fn blame(&mut self) {
        self.batch_misses += 1;
        if self.batch_misses < BATCH_MISSES_BEFORE_GIVING_UP {
            return;
        }

        self.batching = false;
        self.batch_given_up = true;
    }

    /// Gives batching up on this link for good, and writes the fact down.
    ///
    /// Not tried again on this link, including after a recovery. An adapter that has
    /// already dropped it once tends to do so again, and each attempt costs a wasted
    /// request and a probation.
    ///
    /// And written down, so the next session does not pay to find this out again: the
    /// finding out is what costs, since an adapter that cannot survive the request does not
    /// refuse it, it answers and then stops existing.
    fn condemn(&mut self) {
        self.batching = false;
        self.batch_given_up = true;

        if let Some(memory) = &self.memory {
            memory.died(self.key());
        }
    }

    /// Asks for the lot in one request.
    ///
    /// `Ok(None)` is the C#'s `false`: nothing came back, or nothing was batchable.
    /// `Ok(Some(absent))` is `true`, with what was asked for and was not in the reply. The
    /// caller reads those singly; they are deliberately not written off here, because a
    /// parameter missing from a batch has not been asked a question the car declined to
    /// answer: it has been asked one whose answer went astray on the way back.
    fn poll_together(&mut self, due: &[usize]) -> Result<Option<Vec<usize>>> {
        // Only what can be located in a reply. A batch of fewer than two is not a batch,
        // and (this is the part worth being careful about) it must not be counted as one
        // that went unanswered either: nothing was asked, so nothing failed, and three of
        // those would give up on batching for a reason that never happened.
        let asked: Vec<u8> = self
            .chosen(due)
            .into_iter()
            .filter(|&p| obd2_pids::data_bytes_of(p) > 0)
            .collect();
        if asked.len() < 2 {
            return Ok(None);
        }

        let answers: Vec<BatchAnswer> = self.elm().read_many(&asked, &obd2_pids::data_bytes_of, None)?;

        // Nothing at all. Whether that is evidence against batching is not decided here;
        // see the caller, which finds out by asking singly.
        if answers.is_empty() {
            return Ok(None);
        }

        self.batch_misses = 0;

        for answer in &answers {
            let Some(&index) = self.index_of.get(&answer.pid) else {
                continue;
            };

            self.decode(index, &answer.data)?;
        }

        // Asked for and absent. Handed back rather than counted against here: the caller
        // asks each of them singly, and `poll` already records the silence if that comes to
        // nothing too. Counting it in both places retires a parameter in half the rounds it
        // is meant to survive.
        let mut missing = Vec::new();

        for &pid in &asked {
            if answers.iter().any(|a| a.pid == pid) {
                continue;
            }
            if let Some(&index) = self.index_of.get(&pid) {
                missing.push(index);
            }
        }

        Ok(Some(missing))
    }

    fn poll(&mut self, index: usize) -> Result<bool> {
        let (pid, data_bytes) = (self.pids[index].pid, self.pids[index].data_bytes);
        let mut data = [0u8; 8];

        let got = self.elm().try_read(pid, data_bytes, &mut data, None)?;

        let Some(got) = got else {
            self.missed(index);
            return Ok(false);
        };

        self.decode(index, &data[..got])?;

        Ok(true)
    }

    fn decode(&mut self, index: usize, data: &[u8]) -> Result<()> {
        self.silences[index] = 0;
        self.answered[index] = true;

        let at = self.at[index];
        let channels = self.pids[index].channels.len();

        self.pids[index].decode(data, &mut self.values[at..at + channels])
    }

    /// Notes that a parameter said nothing.
    ///
    /// Only one that has *never* answered is counted towards giving up on it. A parameter
    /// that has answered is a gauge somebody is watching, and the retirement is never
    /// undone, so a run of transient silence, a busy module or a moment of interference,
    /// would cost that channel for the rest of the drive. Measured elsewhere on the same
    /// protocol: a coolant temperature reading 97 \u{b0}C, dropped thirty seconds after it
    /// was on screen.
    fn missed(&mut self, index: usize) {
        if !self.answered[index] {
            self.silences[index] += 1;
        }
    }

    /// A handle that scans and clears faults through the adapter's own gate, usable from
    /// another thread while this source polls.
    pub fn faults(&self) -> Elm327Faults {
        Elm327Faults(Arc::clone(&self.elm))
    }

    /// Asks the car what it is complaining about.
    ///
    /// Safe to call while the session is polling; see [`Elm327Faults`].
    pub fn read_faults(&self) -> Result<FaultScan> {
        self.faults().scan()
    }

    /// Asks the car to erase them (mode 04). Never called by this crate itself.
    pub fn clear_faults(&self) -> Result<FaultClear> {
        self.faults().clear()
    }
}

/// Every parameter the car says it supports, across every module that answers.
///
/// Asked in three questions rather than found by trying all ninety-six and waiting for the
/// ones that never answer. Each reply is a bitmask covering the thirty-two numbers after it,
/// and the last bit of each says whether there is another range to ask about, so a car that
/// stops at the first range is not asked twice.
///
/// Every answer is combined rather than the first being taken. More than one module replies
/// on most cars, each reporting only what it alone supports, and which one gets in first is
/// not fixed. Measured on the test vehicle: the engine module answers BE3FA813 and something
/// else answers 80000001, so taking whichever arrived first gave twenty-four channels on one
/// connection and three on the next.
fn supported(elm: &mut Elm327) -> Result<Vec<u8>> {
    let mut supported = Vec::new();

    for query in obd2_pids::SUPPORT_QUERIES {
        let masks = ask_until_answered(elm, query)?;
        if masks.is_empty() {
            break;
        }

        // "And there is a further range", from any module, since the ranges they cover
        // need not be the same. Asking anyway when nobody claims one costs a round trip and
        // answers NO DATA, which reads like a fault.
        let mut more = false;

        for mask in &masks {
            let range = obd2_pids::supported_by(query, mask);
            more |= range.contains(&query.wrapping_add(0x20));
            supported.extend(range);
        }

        if !more {
            break;
        }
    }

    Ok(supported)
}

/// Asks for a capability mask, and keeps asking.
///
/// Worth being stubborn about in a way an ordinary reading is not. This runs once, and its
/// answer decides every channel the session will ever have: a range that goes unanswered on
/// the one attempt it gets silently costs every parameter above it for as long as the
/// session lasts. A reading that fails costs one sample and comes round again in half a
/// second.
///
/// Measured before this was here: the same car and the same dongle, minutes apart, reported
/// 12, then 24, then 15 parameters.
///
/// The long timeout is for the same reason the retries are. This is the first thing the car
/// is ever asked, and the adapter answers it with "SEARCHING..." while it works through the
/// nine OBD2 protocols looking for the one this vehicle speaks: seconds, where a settled
/// link answers in milliseconds.
fn ask_until_answered(elm: &mut Elm327, query: u8) -> Result<Vec<Vec<u8>>> {
    let reset_timeout = elm.reset_timeout;

    for _ in 1..DISCOVERY_ATTEMPTS {
        let masks = elm.read_all(query, 4, Some(reset_timeout))?;
        if !masks.is_empty() {
            return Ok(masks);
        }

        // Long enough for an adapter still settling the protocol to finish. Asking again
        // immediately tends to get the same silence.
        std::thread::sleep(Duration::from_millis(400));
    }

    elm.read_all(query, 4, Some(reset_timeout))
}

impl LiveSource for Elm327Source {
    fn names(&self) -> &[String] {
        &self.names
    }

    fn units(&self) -> &[String] {
        &self.units
    }

    fn digits(&self) -> &[i32] {
        &self.digits
    }

    /// Reopens the adapter if it is not already open.
    ///
    /// Ordinarily a no-op: [`Elm327Source::connect`] has already done the work, and it had
    /// to, because the channels come from the car's answer.
    fn open(&mut self) -> Result<()> {
        if self.transport.is_open() {
            return Ok(());
        }

        self.transport.open()?;
        self.elm().reset(None)?;
        Ok(())
    }

    /// Runs a round, remembering what was in flight if the link dies during it.
    ///
    /// **A link can die by throwing as well as by falling silent.** A socket that resets
    /// and a serial port that goes away both raise rather than answer nothing, and the
    /// branch that records the death sits after the read, so a dongle killed by a batched
    /// request was never blamed for it, the recovery kept batching on, and its proving read
    /// killed the fresh link again. That is the connect-probe-die loop this design exists to
    /// prevent.
    fn read(&mut self) -> Result<Vec<f64>> {
        let batching = self.batching;

        match self.read_round() {
            Err(e) if is_link_failure(&e) => {
                self.died_batching = batching;
                Err(e)
            }
            other => other,
        }
    }

    /// Puts the link back together.
    ///
    /// The adapter forgets its settings when the port goes, and one left in its default
    /// state echoes commands and inserts spaces, so a reconnection without a reset produces
    /// replies that parse as different readings rather than none. The channels are kept as
    /// they are: they are this session's columns, and a car that now reports a different set
    /// is a different session.
    fn recover(&mut self) -> Result<()> {
        // A link whose adapter has gone cannot be closed politely.
        self.transport.close();

        self.transport.open()?;

        // A LINK THAT SAYS NOTHING TO A RESET IS DEAD, NOT SLOW. ATZ never reaches the
        // vehicle (an adapter answers it with its own banner, whatever the car is doing) so
        // silence here is not an ECU that has not woken up, it is a session that is gone.
        // The wedge this catches accepts a connection and then delivers nothing for ever
        // while the socket goes on reporting itself connected, and a fresh one answers in a
        // few tens of milliseconds.
        //
        // Saying so at once matters more than it looks. Walking on into the handshake
        // spends the warm-up, the protocol question and a whole poll round on a corpse, the
        // better part of ten seconds of blank gauges, before anything concludes what the
        // first reply already said.
        let silent = {
            let mut elm = self.elm();
            elm.reset(None)?.is_empty() && elm.reset(Some(CONFIRMING_SILENCE))?.is_empty()
        };
        if silent {
            return Err(Error::Protocol(
                "The adapter accepted the connection and then said nothing at all, \
                 which is a session that has died rather than an ECU that is slow. \
                 Reconnecting opens a fresh one."
                    .to_string(),
            ));
        }

        // A reset undoes the protocol search, and the adapter does not know what the car
        // speaks until it has spoken to it: asked before that, it answers "undetermined",
        // which is not a bus this may batch on. So the warm-up comes first and the question
        // after it, in that order.
        self.elm().warm_up()?;

        // NOT PROBED AGAIN, AND HERE THAT IS THE WHOLE POINT. The probe is itself a batched
        // request. Where a batched request is what killed the link, one sent on the way back
        // kills its replacement just as fast, and the session becomes a loop of connect,
        // probe, die, reconnect that never ends and never concludes anything, because the
        // one thing that could tell it apart is a link with the batching left off.
        //
        // So a link that died with batching on comes back without it. Whether this car can
        // batch was settled when the session connected and a reset does not change that
        // answer; what is in doubt is the adapter, and the only cheap way to ask about it is
        // to stop.
        let suspect = self.died_batching;

        if suspect {
            self.batching = false;
        }

        // Proves it. Opening and resetting say nothing about whether the car is answering
        // again.
        //
        // The suspicion is deliberately still standing while this runs. A proving read that
        // throws is the ordinary case (the link went down because the key went off, and it
        // is still off) and clearing the flag beforehand threw away the only reason to be
        // suspicious: the next attempt came back with nothing to conclude, so an adapter
        // that really does die under batching would be reconnected, re-probed and re-killed
        // every session without ever being written down. That is the loop this whole
        // mechanism exists to stop. Cleared below, once there is an answer.
        self.read()?;

        self.died_batching = false;

        // AND THAT READING IS THE EVIDENCE. Single requests answering on a link that had
        // gone silent under batched ones is the same discrimination `poll_group` makes
        // inside a round (singles alive where the batch was not) made across a
        // reconnection instead, which is the only place it can be made when the failure
        // takes the socket with it.
        //
        // One is enough here where `blame` wants three, because it is not the same evidence
        // and cannot be gathered three times. An empty batched reply costs one request and
        // is worth being unsure about; this costs a session, a reconnection and a proving
        // read, and asking for it again means killing the link again, which is the cost
        // this exists to stop paying.
        //
        // A key turned off and on again at exactly the wrong moment produces this shape
        // once and is written down wrongly. That is what the memory's own threshold is for:
        // it takes `BATCH_DEATHS_BEFORE_GIVING_UP` separate drives before an adapter stops
        // being asked at all, so one bad verdict costs nothing on its own.
        if suspect {
            self.condemn();
        }

        Ok(())
    }

    /// Requests that went unanswered, over the session.
    fn retries(&self) -> i32 {
        self.retries
    }
}

impl Drop for Elm327Source {
    /// `Dispose`: closes the link, even if a [`Elm327Faults`] handle outlives the source.
    fn drop(&mut self) {
        self.transport.close();
    }
}
