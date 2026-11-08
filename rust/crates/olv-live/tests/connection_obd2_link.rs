//! The C# `Obd2WedgeTests` and `Obd2WifiTests`: a Vgate iCar Pro's habits, batching that kills a
//! dongle, a session that has died, and the Wi-Fi link over a real loopback socket.
//!
//! Plus the dropped-link, slow-reply and NO DATA storm tests the w2-obd review asked for, driven
//! through `Elm327Source` with `ScriptedElm` and `FakeElm`.

mod common;

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use common::{BatchReply, FakeElm, FakeElmOverTcp, ScriptedElm, piece};
use olv_core::model::LiveSource;
use olv_core::{Error, Result};
use olv_live::connection::elm327_source::{BATCH_DEATHS_BEFORE_GIVING_UP, Elm327Source, Obd2BatchMemory};
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::obd::elm327::Elm327;
use olv_protocol::obd::obd2_pids;
use olv_transport::transport::wifi_ecu_transport::{self, WifiEcuTransport};

fn length(pid: u8) -> usize {
    obd2_pids::data_bytes_of(pid)
}

/// A car answering the parameters most of them do.
fn car(batches: BatchReply) -> FakeElm {
    let car = FakeElm::new();

    car.with(|s| {
        s.batches = batches;
        s.answers
            .insert(0x00, vec![0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000]);
        s.answers.insert(0x04, vec![0x7F]);
        s.answers.insert(0x05, vec![0x5A]);
        s.answers.insert(0x0B, vec![0x64]);
        s.answers.insert(0x0C, vec![0x1A, 0xF8]);
        s.answers.insert(0x0D, vec![0x40]);
        s.answers.insert(0x0F, vec![0x46]);
        s.answers.insert(0x11, vec![0x33]);
    });

    car
}

fn rpm(source: &mut Elm327Source) -> f64 {
    let at = source.names().iter().position(|n| n == "RPM").unwrap();
    source.read().unwrap()[at]
}

/// What a session remembers, without a settings file behind it.
#[derive(Default)]
struct Remembered {
    deaths: Mutex<HashMap<String, i32>>,
    recorded: Mutex<Vec<String>>,
}

impl Remembered {
    fn recorded(&self) -> Vec<String> {
        self.recorded.lock().unwrap().clone()
    }
}

impl Obd2BatchMemory for Remembered {
    fn deaths_on(&self, adapter: &str) -> i32 {
        self.deaths
            .lock()
            .unwrap()
            .get(&adapter.to_lowercase())
            .copied()
            .unwrap_or(0)
    }

    fn died(&self, adapter: &str) {
        *self.deaths.lock().unwrap().entry(adapter.to_lowercase()).or_insert(0) += 1;
        self.recorded.lock().unwrap().push(adapter.to_string());
    }
}

fn connect_remembering(transport: impl EcuTransport + 'static, link: &str, memory: &Arc<Remembered>) -> Elm327Source {
    let memory: Arc<dyn Obd2BatchMemory> = Arc::clone(memory) as Arc<dyn Obd2BatchMemory>;
    Elm327Source::connect(Box::new(transport), link, Some(memory)).unwrap()
}

const DONGLE: &str = "192.168.0.10:35000";

// ----- the reply finishing itself ------------------------------------------------

#[test]
fn an_answer_of_the_length_it_had_to_be_does_not_wait_for_the_prompt() {
    // The prompt trails the payload by about 210 ms on this adapter, and with batching dead
    // that gap is the poll cycle: every read holding a complete answer sat waiting for a
    // terminator.
    let wire = ScriptedElm::new(|_| vec![piece(20, "410C1AF8"), piece(900, "\r\r>")]);

    let mut adapter = Elm327::new(Box::new(wire));
    adapter.timeout = Duration::from_secs(3);

    let mut data = [0u8; 8];
    let started = Instant::now();
    let got = adapter.try_read(0x0C, 2, &mut data, None).unwrap();
    let took = started.elapsed();

    assert!(got.is_some());
    assert!(
        took < Duration::from_millis(250),
        "the read took {} ms",
        took.as_millis()
    );
}

#[test]
fn no_data_is_not_mistaken_for_an_answer_of_the_right_length() {
    // Worth its own test: "NODATA" is six characters, exactly as many as a one-byte reply,
    // so counting alone would take it for one. What rules it out is that an answer begins 41
    // and this does not.
    let wire = ScriptedElm::new(|_| vec![piece(20, "NO DATA\r\r>")]);

    let mut adapter = Elm327::new(Box::new(wire));
    adapter.timeout = Duration::from_secs(2);

    let mut data = [0u8; 8];

    assert!(adapter.try_read(0x05, 1, &mut data, None).unwrap().is_none());
}

#[test]
fn a_refusal_from_one_module_does_not_end_the_read_before_another_answers() {
    // Where the 41 test earns its place, and it is not the one it looks like. "NO DATA" is
    // already ruled out by having letters in it; a negative response is not: 7F 01 12 is pure
    // hex and exactly as long as a one-byte answer, so counting alone takes it for one.
    //
    // A request is a broadcast, so that refusal can be one module's while the answer is
    // another's, arriving behind it. Ending the read on the refusal loses the reading and
    // leaves the real answer to turn up in the next read, one command out of step.
    let wire = ScriptedElm::new(|_| vec![piece(10, "7F0112\r"), piece(120, "41051E\r\r>")]);

    let mut adapter = Elm327::new(Box::new(wire));
    adapter.timeout = Duration::from_secs(2);

    let mut data = [0u8; 8];

    assert!(
        adapter.try_read(0x05, 1, &mut data, None).unwrap().is_some(),
        "the refusal was taken for the answer"
    );
    assert_eq!(data[0], 0x1E);
}

#[test]
fn a_segmented_reply_is_never_cut_short_by_the_count() {
    // The guard that earns its place. A second module's fragment ahead of a segmented answer
    // shifts the count, so an equality test that kept counting would strike mid-reply and
    // truncate it. Any character that is not hex (the colon of a marker) says this is not the
    // reply the length was computed for.
    let wire = ScriptedElm::new(|_| {
        vec![
            piece(10, "410100040000\r"),
            piece(40, "008\r0:415C600C1AF8\r"),
            piece(70, "1:0D40\r\r>"),
        ]
    });

    let mut adapter = Elm327::new(Box::new(wire));
    adapter.timeout = Duration::from_secs(2);

    let answers = adapter.read_many(&[0x01, 0x5C, 0x0C, 0x0D], &length, None).unwrap();

    assert_eq!(answers.iter().map(|a| a.pid).collect::<Vec<_>>(), [0x5C, 0x0C, 0x0D]);
}

#[test]
fn a_capability_bitmap_waits_for_every_module_that_answers() {
    // Never finished by length, by construction: a mode-01 request is a broadcast, the
    // transmission's sparse map can arrive tens of milliseconds ahead of the engine's, and
    // both are wanted. Stopping at the first would make discovery a coin toss, and the map
    // that wins is the one without a rev counter in it.
    let wire = ScriptedElm::new(|_| vec![piece(10, "410080000001\r"), piece(120, "4100BE3FA813\r\r>")]);

    let mut adapter = Elm327::new(Box::new(wire));
    adapter.timeout = Duration::from_secs(2);

    assert_eq!(adapter.read_all(0x00, 4, None).unwrap().len(), 2);
}

// ----- the dongle that dies of being asked ---------------------------------------

#[test]
fn a_car_that_will_not_be_asked_several_things_at_once_is_not_blamed_on_the_adapter() {
    // A batch answered NO DATA while the singles answer is the *car* declining the request;
    // the adapter is passing it on faithfully. So batching stops for this drive and nothing
    // is written down.
    //
    // The record is keyed on the link's address, and every one of these Wi-Fi dongles
    // answers at 192.168.0.10:35000. Writing a car's refusal there would turn batching off
    // for every other vehicle the adapter is ever plugged into.
    let car = car(BatchReply::All);
    let memory = Arc::new(Remembered::default());

    let mut source = connect_remembering(car.clone(), DONGLE, &memory);
    assert!(source.batching());

    car.with(|s| s.batches = BatchReply::Refuse);
    for _ in 0..3 {
        source.read().unwrap();
    }

    assert!(!source.batching());
    assert!(memory.recorded().is_empty());
}

#[test]
fn a_link_that_dies_altogether_is_not_blamed_on_batching() {
    // Measured, by connecting: this dongle's session dies during ordinary single-parameter
    // traffic too, and when it does every read returns nothing while the socket still
    // reports itself connected. A silent batch is therefore not evidence about the batch: it
    // is what a dead link looks like from here, and writing a permanent verdict on it would
    // condemn a capable adapter for something it did not do.
    //
    // The fallback is what tells them apart: singles answering where the batch did not is the
    // request being refused; singles falling silent too is the link, and batching is a
    // bystander.
    let car = car(BatchReply::All);
    let memory = Arc::new(Remembered::default());

    let mut source = connect_remembering(car.clone(), DONGLE, &memory);
    assert!(source.batching());

    // Everything stops answering, batch and single alike.
    car.with(|s| s.answers.clear());

    for _ in 0..4 {
        let _ = source.read();
    }

    assert!(memory.recorded().is_empty());
}

#[test]
fn an_adapter_with_form_is_not_even_asked() {
    // The probe is itself a batched request, so probing something already known to fail is
    // not a cheap check: it is the entire cost of the thing being checked for, paid again on
    // every connection.
    let memory = Arc::new(Remembered::default());

    for _ in 0..BATCH_DEATHS_BEFORE_GIVING_UP {
        memory.died(DONGLE);
    }

    let car = car(BatchReply::All);
    let source = connect_remembering(car.clone(), DONGLE, &memory);

    assert!(!source.batching());
    assert!(!car.received().iter().any(|c| c.starts_with("01") && c.len() > 4));
}

#[test]
fn one_bad_link_is_not_enough_to_condemn_an_adapter() {
    // A link can die for reasons that have nothing to do with the request in flight (out of
    // range, key off, a cable pulled). Giving up on the first would cost the whole advantage
    // on a capable adapter, permanently and invisibly.
    let memory = Arc::new(Remembered::default());
    memory.died(DONGLE);

    let source = connect_remembering(car(BatchReply::All), DONGLE, &memory);

    assert!(source.batching());
}

#[test]
fn what_is_remembered_is_the_adapter_and_not_the_car() {
    // A different dongle starts clean. The verdict is about hardware that cannot survive a
    // request, and carrying it to another device would hide a working feature for no reason.
    let memory = Arc::new(Remembered::default());

    for _ in 0..BATCH_DEATHS_BEFORE_GIVING_UP {
        memory.died(DONGLE);
    }

    let source = connect_remembering(car(BatchReply::All), "192.168.4.1:35000", &memory);

    assert!(source.batching());
}

#[test]
fn with_nothing_remembering_it_the_session_still_learns_within() {
    // No memory at all is the old behaviour and must still work: it costs one link per
    // session rather than one ever.
    let car = car(BatchReply::All);
    let mut source = Elm327Source::connect(Box::new(car.clone()), "", None).unwrap();

    car.with(|s| s.batches = BatchReply::Refuse);
    for _ in 0..3 {
        source.read().unwrap();
    }

    assert!(!source.batching());
}

// ----- the corpse -------------------------------------------------------------------

/// A link that works, and then does not, while still reporting itself open, accepting every
/// write, and answering none of them. The shape of the wedge: not a refused connection, a
/// session that has stopped existing without saying so.
struct Fragile {
    inner: FakeElm,
    wedged: Arc<AtomicBool>,
}

impl EcuTransport for Fragile {
    fn is_open(&self) -> bool {
        self.inner.is_open()
    }

    fn open(&mut self) -> Result<()> {
        self.inner.open()
    }

    fn close(&mut self) {
        self.inner.close();
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        if !self.wedged.load(Ordering::SeqCst) {
            self.inner.write(data)?;
        }
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        if self.wedged.load(Ordering::SeqCst) {
            return Ok(0);
        }
        self.inner.read(buffer, timeout)
    }

    fn discard_input(&mut self) {
        self.inner.discard_input();
    }
}

#[test]
fn a_session_that_answers_nothing_to_a_reset_is_not_walked_into_as_if_it_were_slow() {
    // ATZ never reaches the vehicle (the adapter answers it out of its own firmware) so
    // silence is a dead session rather than a sleeping car. Walking on spends the warm-up,
    // the protocol question and a whole poll round proving what the first reply already said,
    // which is most of ten seconds of blank gauges before anything reconnects.
    let wedged = Arc::new(AtomicBool::new(false));
    let wire = Fragile {
        inner: car(BatchReply::Refuse),
        wedged: Arc::clone(&wedged),
    };

    let mut source = Elm327Source::connect(Box::new(wire), "", None).unwrap();

    wedged.store(true, Ordering::SeqCst);

    let started = Instant::now();
    let e = source.recover().unwrap_err();
    let took = started.elapsed();

    assert!(matches!(e, Error::Protocol(_)));
    assert!(
        e.to_string()
            .contains("a session that has died rather than an ECU that is slow")
    );

    // One generous reset, because this adapter's banner really can be a second late, and
    // then a short second opinion, against the fifteen or twenty seconds of handshake and
    // polling that a corpse used to absorb before anything concluded it was one.
    assert!(
        took < Duration::from_secs(8),
        "a corpse took {:.1} s to report itself",
        took.as_secs_f64()
    );
}

/// The dongle this whole file is about: a link that answers a request for several
/// parameters, completely and on time, and has stopped existing by the time the next one is
/// sent.
///
/// A fresh connection brings it back, which is the part that matters. The socket is what
/// dies, not the adapter and not the car, so reopening gets a working link, and one batched
/// request kills that one too.
struct KilledByBatching {
    inner: FakeElm,
    dead: bool,
}

impl EcuTransport for KilledByBatching {
    fn is_open(&self) -> bool {
        self.inner.is_open()
    }

    fn open(&mut self) -> Result<()> {
        self.dead = false;
        self.inner.open()
    }

    fn close(&mut self) {
        self.inner.close();
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        if self.dead {
            return Ok(());
        }

        self.inner.write(data)?;

        // Mode 01 carrying more than one parameter: "010C0B" and longer. The reply to it has
        // already been queued, and is allowed through: answering is exactly what this
        // adapter does before it goes.
        let command = String::from_utf8_lossy(data).trim().to_string();

        if command.to_ascii_uppercase().starts_with("01") && command.len() > 4 {
            self.dead = true;
        }

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        self.inner.read(buffer, timeout)
    }

    fn discard_input(&mut self) {
        self.inner.discard_input();
    }
}

#[test]
fn a_link_killed_by_batching_comes_back_without_it_rather_than_being_killed_again() {
    // The failure the memory exists for, and the one path that never reached it. The probe is
    // a batched request, so a recovery that probes kills the link it has just rebuilt:
    // connect, probe, die, reconnect, for as long as the reconnection window lasts, with
    // nothing concluded and nothing written down.
    let wire = KilledByBatching {
        inner: car(BatchReply::All),
        dead: false,
    };
    let memory = Arc::new(Remembered::default());

    let mut source = connect_remembering(wire, DONGLE, &memory);

    // The probe itself was the fatal request: it was answered, and the link is already gone.
    assert!(source.batching());
    assert!(matches!(source.read(), Err(Error::Protocol(_))));

    source.recover().unwrap();

    assert!(
        !source.batching(),
        "the recovery went back to batching on a link batching had killed"
    );
    assert_eq!(memory.recorded(), [DONGLE]);

    // And the session is a working one, on single requests.
    assert_eq!(rpm(&mut source), 1726.0);
}

#[test]
fn a_failed_proving_read_does_not_lose_the_suspicion_against_batching() {
    // Defect fixed in the Rust: the proving read of a recovery runs with batching already
    // switched off, and the wrapper that records a link's death wrote that post-reset value
    // over the suspicion the recovery was keeping. A key still off when the link came back
    // therefore threw away the only evidence against the adapter, as the C# did.
    let car = car(BatchReply::All);
    let wire = KilledByBatching {
        inner: car.clone(),
        dead: false,
    };
    let memory = Arc::new(Remembered::default());

    let mut source = connect_remembering(wire, DONGLE, &memory);
    assert!(matches!(source.read(), Err(Error::Protocol(_))));

    // The link is back, but the car still answers nothing: the proving read fails.
    let answers = car.with(|s| std::mem::take(&mut s.answers));
    assert!(source.recover().is_err());
    assert!(memory.recorded().is_empty(), "nothing was proved yet");

    // Key on: the next recovery proves the link and must still remember what killed the last.
    car.with(|s| s.answers = answers);
    source.recover().unwrap();

    assert_eq!(memory.recorded(), [DONGLE]);
    assert!(!source.batching());
}

#[test]
fn a_reconnection_that_followed_no_death_blames_nothing() {
    // Recover is public and is not only reached from a link that has died: a reconnection
    // can be asked for. Nothing was in flight, so there is nothing to draw a conclusion from,
    // and a verdict written here would be written against an adapter that had done nothing at
    // all.
    let car = car(BatchReply::All);
    let memory = Arc::new(Remembered::default());

    let mut source = connect_remembering(car, DONGLE, &memory);
    assert!(source.batching());

    source.recover().unwrap();

    assert!(memory.recorded().is_empty());
    assert!(source.batching(), "a reconnection gave up batching on its own");
}

#[test]
fn a_link_that_died_with_batching_already_off_is_not_blamed_for_it() {
    // The same rule from the other side. Once batching has been given up, every later death
    // happened without it, so nothing that follows is evidence about it, and the count must
    // not go on climbing towards a verdict the adapter has already had.
    let car = car(BatchReply::Refuse);
    let memory = Arc::new(Remembered::default());

    let mut source = connect_remembering(car.clone(), DONGLE, &memory);
    assert!(!source.batching(), "the refused probe turned batching on");

    // Everything stops answering, and then comes back.
    car.with(|s| s.answers.clear());
    let _ = source.read();

    car.with(|s| s.answers.insert(0x0C, vec![0x1A, 0xF8]));
    source.recover().unwrap();

    assert!(memory.recorded().is_empty());
}

// ----- the w2-obd review: sessions against slow, silent and stormy adapters ------------------------

/// A transport that goes away on demand, the way a USB or Wi-Fi adapter does mid-poll: every
/// operation fails with an `Io` error until it is given back.
struct Pullable {
    inner: FakeElm,
    gone: Arc<AtomicBool>,
}

impl Pullable {
    fn check(&self) -> Result<()> {
        if self.gone.load(Ordering::SeqCst) {
            return Err(Error::Io(std::io::Error::other("The device is not connected.")));
        }
        Ok(())
    }
}

impl EcuTransport for Pullable {
    fn is_open(&self) -> bool {
        // Dead but still reporting itself open: the handle outlives the device.
        self.inner.is_open()
    }

    fn open(&mut self) -> Result<()> {
        self.check()?;
        self.inner.open()
    }

    fn close(&mut self) {
        self.inner.close();
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.check()?;
        self.inner.write(data)
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        self.check()?;
        self.inner.read(buffer, timeout)
    }

    fn discard_input(&mut self) {
        self.inner.discard_input();
    }
}

#[test]
fn a_link_dropped_mid_poll_surfaces_as_an_error_and_recovers_when_it_returns() {
    let gone = Arc::new(AtomicBool::new(false));
    let wire = Pullable {
        inner: car(BatchReply::Refuse),
        gone: Arc::clone(&gone),
    };

    let mut source = Elm327Source::connect(Box::new(wire), "", None).unwrap();
    assert_eq!(rpm(&mut source), 1726.0);

    // The device goes: a poll fails with the transport's own error rather than hanging or
    // returning stale numbers.
    gone.store(true, Ordering::SeqCst);
    let e = source.read().unwrap_err();
    assert!(matches!(e, Error::Io(_)), "{e:?}");

    // Recovery cannot succeed while it is gone, and says why.
    assert!(matches!(source.recover(), Err(Error::Io(_))));

    // Plugged back in, the recovery reopens, resets, warms up and proves it with a read.
    gone.store(false, Ordering::SeqCst);
    source.recover().unwrap();
    assert_eq!(rpm(&mut source), 1726.0);
}

#[test]
fn a_no_data_storm_costs_a_round_and_not_the_session() {
    // A key-off car answers NO DATA to everything. Each round fails with the "answered
    // nothing" error, the count of failed requests climbs by one per round, and the moment
    // the car answers again the next round is a good one: no parameter that has answered is
    // ever retired by the storm.
    let car = car(BatchReply::Refuse);
    let mut source = Elm327Source::connect(Box::new(car.clone()), "", None).unwrap();
    assert_eq!(rpm(&mut source), 1726.0);

    let kept = car.with(|s| s.answers.clone());
    car.with(|s| s.answers.clear());

    for storm in 1..=30 {
        let e = source.read().unwrap_err();
        assert!(e.to_string().starts_with("The adapter answered nothing this round."));
        assert_eq!(source.retries(), storm);
    }

    car.with(|s| s.answers = kept);
    assert_eq!(rpm(&mut source), 1726.0);
}

#[test]
fn a_parameter_that_never_answered_is_given_up_on_after_a_run_of_silence_and_the_rest_carry_on() {
    // The car said it supported 0x0F and then never answers it. It costs a round trip every
    // cycle on a link where that is the whole budget, so after six silent turns it is left
    // out of the rotation (the C#'s GiveUpAfter) and the other channels are unaffected.
    assert!(
        !obd2_pids::all().iter().find(|p| p.pid == 0x0F).unwrap().hot,
        "the test needs a rotating parameter"
    );

    let car = car(BatchReply::Refuse);
    car.with(|s| s.answers.remove(&0x0F));

    let mut source = Elm327Source::connect(Box::new(car.clone()), "", None).unwrap();

    for _ in 0..40 {
        source.read().unwrap();
    }

    car.with(|s| s.received.clear());
    for _ in 0..source.parameters().len() * 2 {
        source.read().unwrap();
    }

    assert!(
        !car.received().contains(&"010F".to_string()),
        "0x0F is still being asked for"
    );
    assert!(car.received().contains(&"010C".to_string()));
    assert!(car.received().contains(&"0105".to_string()));
}

#[test]
fn a_slow_reply_is_waited_for_and_a_late_one_is_not_read_as_the_next_answer() {
    // The adapter takes 600 ms to answer the first question (a timeout of 300 ms gives up on
    // it) and answers the second at once. The late answer must not land on the second
    // question: it would put one channel's number on another channel's gauge.
    let step = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let counter = Arc::clone(&step);

    let wire = ScriptedElm::new(move |command| {
        if command.eq_ignore_ascii_case("010C") {
            // Late, then ordinary.
            return if counter.fetch_add(1, Ordering::SeqCst) == 0 {
                vec![piece(600, "410C1AF8\r\r>")]
            } else {
                vec![piece(10, "410C1AF8\r\r>")]
            };
        }

        if command.eq_ignore_ascii_case("010D") {
            return vec![piece(10, "410D40\r\r>")];
        }

        vec![piece(5, "OK\r\r>")]
    });

    let mut elm = Elm327::new(Box::new(wire));
    elm.timeout = Duration::from_millis(300);
    elm.tail_wait = Duration::from_millis(800);

    let mut data = [0u8; 8];

    // The slow one gives up...
    assert!(elm.try_read(0x0C, 2, &mut data, None).unwrap().is_none());

    // ... and the next question is answered by its own reply, not by the straggler.
    assert!(elm.try_read(0x0D, 1, &mut data, None).unwrap().is_some());
    assert_eq!(data[0], 0x40);
}

// ----- Obd2WifiTests ----------------------------------------------------------------------

fn wifi_car(sticky_echo: bool) -> FakeElm {
    let car = FakeElm::new();

    car.with(|s| {
        s.sticky_echo = sticky_echo;

        // Supported [01-20]: 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11, and the last bit
        // clear, so nothing is asked for above this range.
        s.answers
            .insert(0x00, vec![0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000]);
        s.answers.insert(0x04, vec![0x7F]);
        s.answers.insert(0x05, vec![0x5A]);
        s.answers.insert(0x0B, vec![0x64]);
        s.answers.insert(0x0C, vec![0x1A, 0xF8]);
        s.answers.insert(0x0D, vec![0x40]);
        s.answers.insert(0x0F, vec![0x46]);
        s.answers.insert(0x11, vec![0x33]);
    });

    car
}

#[test]
fn the_first_address_tried_is_where_a_vgate_answers() {
    // Fixed in the dongle's own firmware rather than handed out by a router, because the
    // dongle is the network.
    assert_eq!(wifi_ecu_transport::KNOWN_ADDRESSES[0], "192.168.0.10:35000");
}

#[test]
fn an_address_without_a_port_gets_the_one_these_listen_on() {
    let transport = WifiEcuTransport::at("192.168.0.10").unwrap();

    assert_eq!(transport.host(), "192.168.0.10");
    assert_eq!(transport.port(), 35000);
}

#[test]
fn an_address_with_a_port_keeps_it() {
    let transport = WifiEcuTransport::at("192.168.4.1:35001").unwrap();

    assert_eq!(transport.host(), "192.168.4.1");
    assert_eq!(transport.port(), 35001);
}

#[test]
fn something_that_is_not_a_port_after_the_colon_is_part_of_the_name() {
    // A host is not truncated to something that does not resolve just because it has a
    // colon in it.
    let transport = WifiEcuTransport::at("obd:link").unwrap();

    assert_eq!(transport.host(), "obd:link");
    assert_eq!(transport.port(), wifi_ecu_transport::DEFAULT_PORT);
}

#[test]
fn an_adapter_on_the_network_answers_and_the_car_reports_its_parameters() {
    let car = wifi_car(false);
    let dongle = FakeElmOverTcp::new(&car);

    let source = Elm327Source::connect_over_wifi(&dongle.address(), None).unwrap();

    assert_eq!(source.adapter(), "ELM327 v1.5");
    assert_eq!(source.parameters().len(), 7);
}

#[test]
fn the_address_that_answered_is_remembered() {
    // The only way back to the same device: nothing lists a Wi-Fi dongle, so unless the
    // session records which address answered there is nowhere to read it off afterwards.
    let car = wifi_car(false);
    let dongle = FakeElmOverTcp::new(&car);

    let source = Elm327Source::connect_over_wifi(&dongle.address(), None).unwrap();

    assert_eq!(source.link(), dongle.address());
}

#[test]
fn a_port_or_a_radio_has_no_address_to_record() {
    let source = Elm327Source::connect(Box::new(wifi_car(false)), "", None).unwrap();

    assert_eq!(source.link(), "");
}

#[test]
fn readings_come_back_over_the_socket() {
    let car = wifi_car(false);
    let dongle = FakeElmOverTcp::new(&car);

    let mut source = Elm327Source::connect_over_wifi(&dongle.address(), None).unwrap();

    let row = source.read().unwrap();
    let rpm = source.names().iter().position(|n| n == "RPM");

    assert!(rpm.is_some(), "the car reported no rev counter");
    assert_eq!(row[rpm.unwrap()], 1726.0);
}

#[test]
fn an_adapter_that_echoes_despite_being_told_not_to_still_works() {
    // What a Vgate iCar Pro does: ATE0 is acknowledged and ignored, so every reply arrives
    // behind the command that caused it. Read as one run of hex that is a car supporting
    // nothing.
    let car = wifi_car(true);
    let dongle = FakeElmOverTcp::new(&car);

    let source = Elm327Source::connect_over_wifi(&dongle.address(), None).unwrap();

    assert!(car.with(|s| s.echo), "the fake stopped echoing, so this proves nothing");
    assert_eq!(source.parameters().len(), 7);
}

#[test]
fn nothing_at_the_address_says_what_to_do_about_it() {
    let failure = match Elm327Source::connect_over_wifi(&FakeElmOverTcp::closed_address(), None) {
        Ok(_) => panic!("nothing is listening there"),
        Err(e) => e,
    };

    // The address, and the thing that is actually wrong when a Wi-Fi dongle does not answer,
    // which is nearly always which network this computer is on. "The target machine actively
    // refused it" sends nobody anywhere.
    assert!(matches!(failure, Error::Io(_)), "{failure:?}");
    assert!(failure.to_string().contains("127.0.0.1"));
    assert!(failure.to_string().contains("V-LINK"));
}

#[test]
fn an_adapter_that_hangs_up_is_not_waited_on_forever() {
    // A closed stream socket reports itself by returning nothing, at once and for ever. Read
    // as "no data yet" it spins the read loop flat out until the timeout, on every request,
    // for the rest of the session.
    let dongle = FakeElmOverTcp::hanging_up(&wifi_car(false));
    let mut transport = WifiEcuTransport::at(&dongle.address()).unwrap();

    transport.open().unwrap();

    let mut buffer = [0u8; 16];
    let started = Instant::now();
    let got = transport.read(&mut buffer, Duration::from_secs(10)).unwrap();
    let took = started.elapsed();

    assert_eq!(got, 0);
    assert!(
        took < Duration::from_secs(2),
        "a closed link took {:.1} s to report itself",
        took.as_secs_f64()
    );
}

#[test]
fn a_read_on_an_adapter_that_was_never_opened_says_so() {
    let mut transport = WifiEcuTransport::at("192.168.0.10").unwrap();

    assert!(matches!(
        transport.read(&mut [0u8; 4], Duration::ZERO),
        Err(Error::InvalidOperation(_))
    ));
}

#[test]
fn a_reply_arriving_in_pieces_is_read_whole() {
    let car = wifi_car(false);
    let dongle = FakeElmOverTcp::new(&car);
    let mut transport = WifiEcuTransport::at(&dongle.address()).unwrap();

    transport.open().unwrap();
    transport.write(b"ATI\r").unwrap();

    // The echoed command, "ELM327 v1.5" and the prompt: eighteen bytes, which the fake sends
    // in seven-byte pieces. This only passes if a short read comes back for the rest of them.
    let mut buffer = [0u8; 18];
    let got = transport.read(&mut buffer, Duration::from_secs(2)).unwrap();

    assert_eq!(got, 18);
    assert!(String::from_utf8_lossy(&buffer[..got]).contains("ELM327 v1.5"));
}

#[test]
fn what_arrived_before_a_request_is_dropped() {
    // The tail of a previous exchange, read as the front of the next answer, decodes as a
    // different reading altogether.
    let car = wifi_car(false);
    let dongle = FakeElmOverTcp::new(&car);
    let mut transport = WifiEcuTransport::at(&dongle.address()).unwrap();

    transport.open().unwrap();
    transport.write(b"ATI\r").unwrap();

    // Long enough for the whole reply to have arrived and be waiting.
    std::thread::sleep(Duration::from_millis(300));
    transport.discard_input();

    assert_eq!(transport.read(&mut [0u8; 8], Duration::from_millis(200)).unwrap(), 0);
}

#[test]
fn closing_and_opening_again_gets_a_fresh_socket() {
    // What a recovery does after the link has gone. A socket that reported itself open on a
    // connection that had ended would be reopened into nothing, and every read after it
    // would fail the same way.
    let car = wifi_car(false);
    let dongle = FakeElmOverTcp::new(&car);
    let mut transport = WifiEcuTransport::at(&dongle.address()).unwrap();

    transport.open().unwrap();
    assert!(transport.is_open());

    transport.close();
    assert!(!transport.is_open());
}
