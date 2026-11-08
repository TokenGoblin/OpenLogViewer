//! `SsmSource` (the C# `SsmTests` that need a conversation), `TunerStudioSource`,
//! `MaxxEcuSource` and `MaxxGauges.For`.
//!
//! The SSM request, reply and parameter-file tests that need no source were ported with the code
//! in `olv-protocol`. `MaxxEcuSource` is replayed from the captures in
//! `tests/OpenLogViewer.Tests/Fixtures/`.

mod common;

use std::collections::VecDeque;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use common::{FakeSubaru, FakeTransport, Misbehaviour, ScriptedElm, ecu_model, piece};
use olv_core::ini::msq_ini;
use olv_core::model::LiveSource;
use olv_core::{Error, Result};
use olv_live::connection::ecu_connection::EcuConnection;
use olv_live::connection::maxx_ecu_source::{MaxxEcuSource, maxx_gauges};
use olv_live::connection::ssm_source::SsmSource;
use olv_live::connection::tuner_studio_source::TunerStudioSource;
use olv_protocol::ms::maxx_frame_reader::MaxxFrameReader;
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::obd::realtime_decoder::RealtimeDecoder;
use olv_protocol::obd::ssm::{self, SsmParameter};
use olv_protocol::obd::ssm_parameter_file;

// ===== SSM ===================================================================================

fn template() -> Vec<SsmParameter> {
    ssm_parameter_file::read(Some(ssm_parameter_file::template()))
}

fn connect_subaru(car: &FakeSubaru, parameters: Vec<SsmParameter>) -> Result<SsmSource> {
    SsmSource::connect(Box::new(car.clone()), parameters)
}

/// An answer to something else is not an answer to this.
#[test]
fn a_mode01_reply_is_not_mistaken_for_an_ssm_one() {
    assert!(ssm::read_reply(Some("410C0E2B"), 1).is_empty());
}

#[test]
fn reads_the_parameters_it_was_given() {
    let car = FakeSubaru::new();
    let mut source = connect_subaru(&car, template()).unwrap();

    assert_eq!(source.names(), ["Engine Speed", "Coolant"]);

    let values = source.read().unwrap();

    assert!((values[0] - 909.5).abs() < 0.0005);
    assert_eq!(values[1], 94.0);
}

/// A two-byte value is read one address at a time, because the hardware this was proven
/// against will not carry two in one request.
#[test]
fn a_multi_byte_value_is_asked_for_a_byte_at_a_time() {
    let car = FakeSubaru::new();
    let mut source = connect_subaru(&car, template()).unwrap();
    source.read().unwrap();

    let received = car.received();
    assert!(received.contains(&"A80000000E".to_string()));
    assert!(received.contains(&"A80000000F".to_string()));
}

/// With no addresses there is nothing to do, and it says why rather than opening a session
/// that shows an empty screen.
#[test]
fn refuses_to_start_with_no_parameters() {
    let e = match connect_subaru(&FakeSubaru::new(), Vec::new()) {
        Ok(_) => panic!("there is nothing to read"),
        Err(e) => e,
    };

    assert!(matches!(e, Error::Protocol(_)));
    assert!(e.to_string().contains(ssm_parameter_file::NAME));
}

/// Every address in the file may be wrong. A session that starts happily and shows a screen of
/// dashes is worse than one that refuses and says so.
#[test]
fn refuses_to_start_when_the_car_will_not_answer_the_first_address() {
    let car = FakeSubaru::new();
    car.with(|s| s.memory.clear());

    let e = match connect_subaru(&car, vec![SsmParameter::new("Invented", 0x123456)]) {
        Ok(_) => panic!("the car never answered"),
        Err(e) => e,
    };

    assert!(e.to_string().to_lowercase().contains("did not answer"));
}

/// The adapter is put back to ordinary addressing on the way out, or whatever connects next
/// is talking to the engine module by mistake.
#[test]
fn puts_the_adapter_back_when_it_is_done_with() {
    let car = FakeSubaru::new();

    let source = connect_subaru(&car, template()).unwrap();
    drop(source);

    let received = car.received();
    assert!(received.contains(&"ATSH7DF".to_string()));

    // In the order the restore defines, after everything else.
    let tail: Vec<&str> = received[received.len() - 3..].iter().map(String::as_str).collect();
    assert_eq!(tail, ssm::RESTORE);
}

#[test]
fn the_adapter_is_put_into_ssm_addressing_before_the_first_read_and_proven_with_one() {
    let car = FakeSubaru::new();
    let _source = connect_subaru(&car, template()).unwrap();

    let received = car.received();
    let setup_at = received.iter().position(|c| c == "ATSH7E0").unwrap();

    assert_eq!(received[setup_at..setup_at + 5], ssm::SETUP);
    assert_eq!(received[setup_at + 5], "A80000000E");
}

#[test]
fn an_address_the_ecu_refuses_is_reported_as_wrong_rather_than_as_silence() {
    // A refusal proves the vehicle read the command, which is the difference between "this
    // car does not speak SSM" and "that address was wrong".
    let wire = ScriptedElm::new(|command| {
        let text = command.to_ascii_uppercase();
        let answer = if text == "ATZ" || text == "ATI" {
            "ELM327 v1.3a"
        } else if text.starts_with("ST") {
            "?"
        } else if text.starts_with("AT") {
            "OK"
        } else if text == "0100" {
            "4100BE3FA813"
        } else {
            "7FA813"
        };

        vec![piece(2, &format!("{answer}\r\r>"))]
    });

    let e = match SsmSource::connect(Box::new(wire), vec![SsmParameter::new("Engine Speed", 0x00000E)]) {
        Ok(_) => panic!("the ECU refused every address"),
        Err(e) => e,
    };

    assert!(
        e.to_string()
            .starts_with("The ECU understood the request and refused it. Address 0x00000E is probably wrong")
    );
}

#[test]
fn a_round_the_ecu_answers_nothing_to_is_an_error_and_a_retry() {
    let car = FakeSubaru::new();
    let mut source = connect_subaru(&car, template()).unwrap();
    source.read().unwrap();

    car.with(|s| s.memory.clear());

    let e = source.read().unwrap_err();

    assert!(e.to_string().starts_with("The ECU answered nothing this round."));
    assert_eq!(source.retries(), 1);
}

#[test]
fn an_address_that_never_answers_is_dropped_after_eight_silences_and_reported() {
    let car = FakeSubaru::new();
    let mut source = connect_subaru(&car, template()).unwrap();

    // Only the coolant stops answering; the engine speed carries on.
    car.with(|s| {
        s.memory.remove(&0x08);
    });

    for _ in 0..8 {
        let row = source.read().unwrap();
        assert!(row[0].is_finite());
    }

    assert_eq!(source.dropped(), ["Coolant"]);

    // And it is no longer asked for.
    car.with(|s| s.received.clear());
    source.read().unwrap();
    assert!(!car.received().contains(&"A800000008".to_string()));
}

#[test]
fn a_dropped_link_is_recovered_by_reopening_resetting_and_proving_it_with_a_read() {
    let car = FakeSubaru::new();
    let mut source = connect_subaru(&car, template()).unwrap();

    car.with(|s| {
        s.received.clear();
    });

    source.recover().unwrap();

    let received = car.received();
    assert_eq!(received[0], "ATZ");
    assert!(received.contains(&"ATSH7E0".to_string()));
    assert!(received.contains(&"A80000000E".to_string()));
}

#[test]
fn opening_a_source_that_is_already_open_does_nothing() {
    let car = FakeSubaru::new();
    let mut source = connect_subaru(&car, template()).unwrap();

    car.with(|s| s.received.clear());
    source.open().unwrap();

    assert!(car.received().is_empty());
}

// ===== TunerStudioSource =====================================================================

const INI: &str = "[OutputChannels]\n\
ochGetCommand = \"r\\$tsCanId\\x07%2o%2c\"\n\
ochBlockSize = 6\n\
rpm = scalar, U16, 0, \"RPM\", 1.0, 0.0\n\
coolant = scalar, S16, 2, \"C\", 0.1, 0.0\n\
map = scalar, U08, 4, \"kPa\", 1.0, 0.0\n\
afr = scalar, U08, 5, \"AFR\", 0.1, 0.0\n\
[Constants]\n\
endianness = big\n\
blockingFactor = 4\n\
[Datalog]\n\
entry = rpm, \"RPM\", int, \"%d\"\n\
entry = coolant, \"CLT\", float, \"%.1f\"\n\
entry = missing, \"Gone\", int, \"%d\"\n\
entry = map, \"rpm\", int, \"%d\"\n\
entry = afr, \"\", float, \"%.2f\"\n";

/// rpm 3000, coolant 90.0, map 100, afr 14.7.
fn block() -> Vec<u8> {
    vec![0x0B, 0xB8, 0x03, 0x84, 100, 147]
}

fn tuner_source(memory: Arc<Mutex<Vec<u8>>>) -> (TunerStudioSource, FakeTransport, Arc<EcuConnection>) {
    let layout = msq_ini::read_output_channels(INI, None);
    let datalog = msq_ini::read_datalog(INI, None);

    let transport = FakeTransport::responding(ecu_model(memory, false, Misbehaviour::None));
    let connection = Arc::new(EcuConnection::with_defaults(Box::new(transport.clone())));
    connection.use_layout(&layout, false).unwrap();

    let source = TunerStudioSource::new(Arc::clone(&connection), RealtimeDecoder::new(layout, None), &datalog);

    (source, transport, connection)
}

#[test]
fn only_the_channels_the_datalog_names_are_published_under_the_names_it_gives_them() {
    let (source, _, _) = tuner_source(Arc::new(Mutex::new(block())));

    // "missing" is not in the block's layout and is skipped; the second "rpm" label is a
    // repeat of the first (case-insensitively) and is dropped rather than sharing a column; a
    // blank label falls back to the channel's own name.
    assert_eq!(source.names(), ["RPM", "CLT", "afr"]);
    assert_eq!(source.units(), ["RPM", "C", "AFR"]);
    assert_eq!(source.digits(), [0, 1, 2]);
}

#[test]
fn a_read_returns_the_datalog_row_and_keeps_the_whole_decoded_block() {
    let (mut source, _, _) = tuner_source(Arc::new(Mutex::new(block())));

    let raw = source.raw_telemetry().unwrap();
    assert!(raw.last_raw_frame().is_none(), "nothing has been read yet");
    assert_eq!(raw.raw_names(), ["rpm", "coolant", "map", "afr"]);
    assert_eq!(raw.raw_units(), ["RPM", "C", "kPa", "AFR"]);

    let row = source.read().unwrap();

    assert_eq!(row[0], 3000.0);
    assert!((row[1] - 90.0).abs() < 1e-9);
    assert!((row[2] - 14.7).abs() < 1e-9);

    let frame = source.raw_telemetry().unwrap().last_raw_frame().unwrap();
    assert_eq!(frame.len(), 4);
    assert_eq!(frame[2], 100.0); // map: in the raw frame, though no datalog entry names it
}

#[test]
fn the_raw_frame_is_replaced_each_poll_and_an_old_one_is_not_disturbed() {
    // A reader on another thread gets either the previous frame or this one, never a partly
    // written one, because the whole array is replaced rather than mutated.
    let memory = Arc::new(Mutex::new(block()));
    let (mut source, _, _) = tuner_source(Arc::clone(&memory));

    source.read().unwrap();
    let first = source.raw_telemetry().unwrap().last_raw_frame().unwrap();

    memory.lock().unwrap()[4] = 55;
    source.read().unwrap();
    let second = source.raw_telemetry().unwrap().last_raw_frame().unwrap();

    assert_eq!(first[2], 100.0);
    assert_eq!(second[2], 55.0);
}

#[test]
fn a_name_the_ini_defines_twice_is_read_from_the_later_definition() {
    // Three do in the MS3 firmware, and the later definition is the one in force.
    let ini = "[OutputChannels]\nochBlockSize = 4\n\
               rpm = scalar, U08, 0, \"RPM\", 1.0, 0.0\n\
               rpm = scalar, U08, 3, \"RPM\", 1.0, 0.0\n\
               [Datalog]\nentry = rpm, \"RPM\", int, \"%d\"\n";
    let layout = msq_ini::read_output_channels(ini, None);
    let datalog = msq_ini::read_datalog(ini, None);

    let transport = FakeTransport::new();
    transport.enqueue([common::reply(&[1, 2, 3, 4])]);
    let connection = Arc::new(EcuConnection::with_defaults(Box::new(transport)));
    let mut source = TunerStudioSource::new(connection, RealtimeDecoder::new(layout, None), &datalog);

    assert_eq!(source.read().unwrap(), [4.0]);
}

#[test]
fn the_block_is_asked_for_in_pieces_the_size_the_firmware_declares() {
    let (mut source, transport, connection) = tuner_source(Arc::new(Mutex::new(block())));

    source.read().unwrap();

    // 6 bytes, a blocking factor of 4: 4 at 0, then 2 at 4.
    let written = transport.written();
    let payloads: Vec<&[u8]> = written.iter().map(|w| &w[2..w.len() - 4]).collect();
    assert_eq!(
        payloads,
        [
            &[0x72, 0x00, 0x07, 0x00, 0x00, 0x00, 0x04][..],
            &[0x72, 0x00, 0x07, 0x00, 0x04, 0x00, 0x02][..]
        ]
    );
    assert_eq!(source.retries(), connection.retries());
}

#[test]
fn recovery_closes_reopens_and_proves_the_link_with_a_read() {
    let (mut source, transport, _) = tuner_source(Arc::new(Mutex::new(block())));
    source.open().unwrap();

    let before = transport.written().len();
    source.recover().unwrap();

    let (opens, closes) = transport.with(|s| (s.opens, s.closes));
    assert_eq!((opens, closes), (2, 1), "open, then close and open again");
    assert_eq!(
        transport.written().len() - before,
        2,
        "the proving read, in its two pieces"
    );
}

#[test]
fn recovery_fails_while_the_ecu_stays_silent() {
    // Opening also proves nothing on its own: an adapter can enumerate before the ECU behind
    // it is answering.
    let layout = msq_ini::read_output_channels(INI, None);
    let datalog = msq_ini::read_datalog(INI, None);
    let transport = FakeTransport::new(); // never replies
    let connection = Arc::new(EcuConnection::new(
        Box::new(transport.clone()),
        olv_live::connection::ecu_connection::EcuConnectionSettings {
            timeout: Duration::from_millis(1),
            retries: 0,
            ..Default::default()
        },
    ));
    let mut source = TunerStudioSource::new(connection, RealtimeDecoder::new(layout, None), &datalog);

    assert!(matches!(source.recover(), Err(Error::Protocol(_))));
}

#[test]
fn dropping_the_source_closes_the_link_even_while_the_connection_is_shared() {
    let (mut source, transport, connection) = tuner_source(Arc::new(Mutex::new(block())));
    source.open().unwrap();
    assert!(transport.with(|s| s.open));

    drop(source);

    assert!(!transport.with(|s| s.open));
    drop(connection);
}

// ===== MaxxEcuSource =========================================================================

fn fixture(name: &str) -> Vec<u8> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../../tests/OpenLogViewer.Tests/Fixtures")
        .join(name);

    std::fs::read(&path).unwrap_or_else(|e| panic!("fixture {}: {e}", path.display()))
}

/// A MaxxECU that says nothing until it has been spoken to, and then plays back a capture in
/// pieces of 512 bytes, the way a radio hands them over.
#[derive(Clone)]
struct Replay(Arc<Mutex<ReplayState>>);

struct ReplayState {
    stream: VecDeque<u8>,
    written: Vec<Vec<u8>>,
    armed: bool,
    open: bool,
    opens: usize,
}

impl Replay {
    fn new(stream: &[u8]) -> Self {
        Self(Arc::new(Mutex::new(ReplayState {
            stream: stream.iter().copied().collect(),
            written: Vec::new(),
            armed: false,
            open: false,
            opens: 0,
        })))
    }

    fn written(&self) -> Vec<Vec<u8>> {
        self.0.lock().unwrap().written.clone()
    }
}

impl EcuTransport for Replay {
    fn is_open(&self) -> bool {
        self.0.lock().unwrap().open
    }

    fn open(&mut self) -> Result<()> {
        let mut s = self.0.lock().unwrap();
        s.open = true;
        s.opens += 1;
        Ok(())
    }

    fn close(&mut self) {
        self.0.lock().unwrap().open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut s = self.0.lock().unwrap();
        s.written.push(data.to_vec());
        // Spoken to: the subscription is the second message, and only then do readings flow.
        s.armed = s.written.len() >= 2;
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let mut s = self.0.lock().unwrap();
        if !s.armed {
            return Ok(0);
        }

        let take = buffer.len().min(512).min(s.stream.len());
        for slot in buffer.iter_mut().take(take) {
            *slot = s.stream.pop_front().unwrap();
        }

        Ok(take)
    }

    fn discard_input(&mut self) {}
}

/// The two recordings hold the configuration and label traffic a cold ECU sends after its
/// activation and no telemetry reading (the subscription was not part of the capture), so a
/// reading is appended to them: type 0x01 with one little-endian 16 bit value per subscribed
/// channel, in subscription order.
fn telemetry(raw: [i16; 14]) -> Vec<u8> {
    let payload: Vec<u8> = raw.iter().flat_map(|v| v.to_le_bytes()).collect();
    maxx_protocol::frame(maxx_protocol::TELEMETRY, &payload).unwrap()
}

const FIRST: [i16; 14] = [3000, 215, 880, 1013, 1380, 1000, 7000, 0, 0, 150, 125, 0, 0, -50];
const SECOND: [i16; 14] = [3100, 216, 881, 1010, 1381, 990, 7000, 0, 1, 155, 130, 123, 0, -40];

/// What the protocol says those raw values mean: raw times scale, the signed ones as signed.
fn decoded(raw: [i16; 14]) -> Vec<f64> {
    maxx_protocol::SUBSCRIBED
        .iter()
        .zip(raw)
        .map(|(c, r)| {
            let r = if c.is_signed { f64::from(r) } else { f64::from(r as u16) };
            r * c.scale
        })
        .collect()
}

/// A capture ends wherever the recording stopped, usually inside a frame, and a reading appended
/// to that would be swallowed by the truncated frame ahead of it. So the capture is cut back to
/// its whole frames (re-encoded, which `frame` reproduces exactly).
fn whole_frames(capture: &str) -> Vec<u8> {
    let mut reader = MaxxFrameReader::new();
    reader.feed(&fixture(capture));

    let mut stream = Vec::new();
    while let Some(frame) = reader.try_take() {
        stream.extend(maxx_protocol::frame(frame.frame_type, &frame.payload).unwrap());
    }

    stream
}

fn with_reading(capture: &str, readings: &[[i16; 14]]) -> Vec<u8> {
    let mut stream = whole_frames(capture);
    for r in readings {
        stream.extend(telemetry(*r));
    }
    stream
}

#[test]
fn a_maxx_source_carries_the_subscribed_channels() {
    let source = MaxxEcuSource::new(Box::new(FakeTransport::new())).unwrap();

    assert_eq!(source.names().len(), 14);
    assert_eq!(source.names()[0], "RPM");
    assert_eq!(source.units()[1], "deg C");
    assert_eq!(source.digits()[4], 2);
    assert_eq!(source.retries(), 0);
}

#[test]
fn opening_sends_the_activation_then_the_subscription_byte_for_byte() {
    let replay = Replay::new(&with_reading("maxxecu-steady.bin", &[FIRST]));
    let mut source = MaxxEcuSource::new(Box::new(replay.clone())).unwrap();

    source.open().unwrap();

    // Replayed byte for byte, because their checksum algorithm is unidentified: they can only
    // be repeated, never composed.
    assert_eq!(
        replay.written(),
        [maxx_protocol::ACTIVATION.to_vec(), maxx_protocol::SUBSCRIPTION.to_vec()]
    );
    assert!(replay.0.lock().unwrap().open);
}

#[test]
fn the_steady_capture_is_traffic_and_the_reading_behind_it_decodes_to_what_the_protocol_says() {
    let mut source = MaxxEcuSource::new(Box::new(Replay::new(&with_reading(
        "maxxecu-steady.bin",
        &[FIRST, SECOND],
    ))))
    .unwrap();

    // Opening proves the link with the first reading and discards it...
    source.open().unwrap();

    // ... after the 87 configuration and label frames ahead of it, which are normal.
    assert_eq!(source.other_frames(), 87);

    assert_eq!(source.read().unwrap(), decoded(SECOND));
    assert_eq!(source.retries(), 0);
}

#[test]
fn the_opening_capture_has_its_configuration_frames_counted_as_traffic_and_not_failures() {
    // 17,544 bytes over twelve seconds on a cold ECU: configuration and label dumps ahead of
    // the first reading. Counting them as failures would report a healthy link as one making
    // hundreds of retries, which is what it did.
    let mut source = MaxxEcuSource::new(Box::new(Replay::new(&with_reading(
        "maxxecu-opening.bin",
        &[FIRST, SECOND],
    ))))
    .unwrap();

    source.open().unwrap();

    assert_eq!(
        source.other_frames(),
        324,
        "every non-reading frame is counted, none is lost"
    );
    assert_eq!(source.read().unwrap(), decoded(SECOND));
    assert_eq!(source.retries(), 0);
}

#[test]
fn a_reading_split_across_reads_is_reassembled() {
    // The radio hands bytes over in pieces; one read can end inside a frame.
    let stream = telemetry(FIRST);
    let (head, tail) = stream.split_at(11);

    struct Pieces(Vec<Vec<u8>>);

    impl EcuTransport for Pieces {
        fn is_open(&self) -> bool {
            true
        }
        fn open(&mut self) -> Result<()> {
            Ok(())
        }
        fn close(&mut self) {}
        fn write(&mut self, _data: &[u8]) -> Result<()> {
            Ok(())
        }
        fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
            if self.0.is_empty() {
                return Ok(0);
            }
            let next = self.0.remove(0);
            buffer[..next.len()].copy_from_slice(&next);
            Ok(next.len())
        }
        fn discard_input(&mut self) {}
    }

    let mut source = MaxxEcuSource::new(Box::new(Pieces(vec![head.to_vec(), tail.to_vec()]))).unwrap();

    assert_eq!(source.read().unwrap(), decoded(FIRST));
}

#[test]
fn a_link_that_says_nothing_is_reported_with_what_to_check() {
    let replay = Replay::new(&[]);
    let mut source = MaxxEcuSource::new(Box::new(replay)).unwrap();

    let started = Instant::now();
    let e = source.read().unwrap_err();

    assert!(started.elapsed() >= Duration::from_secs(2));
    assert!(matches!(e, Error::Protocol(_)));
    assert!(
        e.to_string()
            .starts_with("No reading arrived within 2 seconds. Nothing that speaks MaxxECU answered.")
    );
    assert!(e.to_string().contains("check the ECU has power"));
}

#[test]
fn an_ecu_that_talks_but_sends_no_telemetry_is_reported_as_not_having_taken_the_subscription() {
    // A configuration frame (type 0x15) and nothing else, from a link that has been spoken to.
    let frame = maxx_protocol::frame(0x15, &[1, 2, 3]).unwrap();
    let mut link = Replay::new(&frame);
    link.write(&[0]).unwrap();
    link.write(&[0]).unwrap();
    let mut talking = MaxxEcuSource::new(Box::new(link)).unwrap();

    let e = talking.read().unwrap_err();

    assert!(e.to_string().contains("A MaxxECU is there and talking"));
    assert!(e.to_string().contains("1 other frames arrived"));
    assert!(e.to_string().contains("did not take the subscription"));
    assert_eq!(talking.other_frames(), 1);
}

#[test]
fn opening_a_silent_link_fails_after_the_longer_allowance_the_first_frame_gets() {
    let mut source = MaxxEcuSource::new(Box::new(Replay::new(&[]))).unwrap();

    let started = Instant::now();
    let e = source.open().unwrap_err();
    let took = started.elapsed();

    // The first reading gets eight seconds (a cold ECU on a fresh USB cable takes longer than
    // two), after the 300 ms between the activation and the subscription.
    assert!(took >= Duration::from_secs(8), "gave up after {took:?}");
    assert!(e.to_string().starts_with("No reading arrived within 8 seconds."));
}

#[test]
fn recovery_arms_the_link_again_because_the_ecu_forgets_its_subscription_with_the_socket() {
    let replay = Replay::new(&with_reading("maxxecu-steady.bin", &[FIRST, SECOND]));
    let mut source = MaxxEcuSource::new(Box::new(replay.clone())).unwrap();
    source.open().unwrap();

    // The socket goes; the subscription goes with it.
    replay.0.lock().unwrap().armed = false;
    source.recover().unwrap();

    let written = replay.written();
    assert_eq!(written.len(), 4);
    assert_eq!(written[2], maxx_protocol::ACTIVATION);
    assert_eq!(written[3], maxx_protocol::SUBSCRIPTION);
    assert_eq!(replay.0.lock().unwrap().opens, 2);
}

#[test]
fn recovery_fails_when_the_link_comes_back_silent() {
    // Opening and arming are not evidence that anything is coming back.
    let replay = Replay::new(&with_reading("maxxecu-steady.bin", &[FIRST]));
    let mut source = MaxxEcuSource::new(Box::new(replay.clone())).unwrap();
    source.open().unwrap();

    assert!(matches!(source.recover(), Err(Error::Protocol(_))));
}

// ===== MaxxGauges.For ========================================================================

/// `MaxxGauges.Problem` is process-wide, as the C#'s static is, so the tests that read it take turns.
static MTUNE: Mutex<()> = Mutex::new(());

fn temp_definitions(xml: &str) -> PathBuf {
    let path = std::env::temp_dir().join(format!(
        "olv-maxx-definitions-{}-{:?}.xml",
        std::process::id(),
        std::thread::current().id()
    ));
    std::fs::write(&path, xml).unwrap();
    path
}

#[test]
fn a_gauge_is_built_for_each_subscribed_channel_with_the_range_mtune_defines() {
    let _turn = MTUNE.lock().unwrap_or_else(|e| e.into_inner());
    let path = temp_definitions(
        "<?xml version=\"1.0\"?><ECURealtimeDataDefinitions>\
         <ECURealtimeDataItem index=\"61\" minValue=\"0\" maxValue=\"12000\"/>\
         <ECURealtimeDataItem index=\"21\" minValue=\"6\" maxValue=\"18\"/>\
         <ECURealtimeDataItem index=\"18\" minValue=\"5\" maxValue=\"5\"/>\
         </ECURealtimeDataDefinitions>",
    );

    let gauges = maxx_gauges::for_channels(maxx_protocol::SUBSCRIBED, Some(&path));
    std::fs::remove_file(&path).ok();

    assert_eq!(gauges.len(), 14);

    let rpm = &gauges[0];
    assert_eq!(
        (rpm.name.as_str(), rpm.channel.as_str(), rpm.title.as_str()),
        ("RPM", "RPM", "RPM")
    );
    assert_eq!(rpm.category, "MaxxECU");
    assert_eq!((rpm.low, rpm.high), (0.0, 12000.0));
    assert_eq!((rpm.value_digits, rpm.label_digits), (0, 0));
    assert!(
        !rpm.has_bands(),
        "MTune's limits are what a channel can hold, not thresholds"
    );

    let battery = &gauges[4];
    assert_eq!((battery.low, battery.high), (6.0, 18.0));
    assert_eq!(battery.units, "V");
    // Two decimals on the reading, one on the scale labels.
    assert_eq!((battery.value_digits, battery.label_digits), (2, 1));

    // high must exceed low to count: CLT's 5 to 5 is no range.
    assert_eq!((gauges[2].low, gauges[2].high), (0.0, 0.0));
    // And a channel MTune says nothing about is a bare readout.
    assert!(!gauges[3].has_scale());
}

#[test]
fn without_mtune_every_gauge_is_a_bare_readout_and_the_problem_is_said() {
    let _turn = MTUNE.lock().unwrap_or_else(|e| e.into_inner());
    let gauges = maxx_gauges::for_channels(maxx_protocol::SUBSCRIBED, None);

    assert_eq!(gauges.len(), 14);
    assert!(gauges.iter().all(|g| !g.has_scale()));
    assert_eq!(
        olv_core::logs::maxx_gauges::problem(),
        "MTune is not installed, so there are no ranges to draw dials over."
    );
}

// ===== recovery and the Speeduino ============================================================

#[test]
fn recovery_leaves_the_freshly_opened_port_alone_for_the_boot_wait_before_speaking() {
    // A Speeduino resets when its port opens and sits in its bootloader for about two
    // seconds; a request arriving then leaves it stuck there. Read-identity waits; so must
    // the recovery.
    let layout = msq_ini::read_output_channels(INI, None);
    let datalog = msq_ini::read_datalog(INI, None);

    let first_request = Arc::new(Mutex::new(None::<Instant>));
    let seen = Arc::clone(&first_request);
    let memory = Arc::new(Mutex::new(block()));
    let mut model = ecu_model(Arc::clone(&memory), false, Misbehaviour::None);
    let transport = FakeTransport::responding(move |framed: &[u8]| {
        seen.lock().unwrap().get_or_insert_with(Instant::now);
        model(framed)
    });

    let connection = Arc::new(EcuConnection::new(
        Box::new(transport),
        olv_live::connection::ecu_connection::EcuConnectionSettings {
            boot_wait: Duration::from_millis(200),
            ..Default::default()
        },
    ));
    connection.use_layout(&layout, false).unwrap();
    let mut source = TunerStudioSource::new(Arc::clone(&connection), RealtimeDecoder::new(layout, None), &datalog);

    let started = Instant::now();
    source.recover().unwrap();

    let spoke_after = first_request.lock().unwrap().unwrap().duration_since(started);
    assert!(
        spoke_after >= Duration::from_millis(200),
        "spoke after only {spoke_after:?}"
    );
}

#[test]
fn a_recovery_between_a_write_and_its_burn_makes_the_burn_refuse() {
    // The poll thread recovery reopens the port, which resets a Speeduino and loses the
    // change just written; the burn must not then report success.
    use olv_core::ini::tune_layout::TunePage;

    let layout = msq_ini::read_output_channels(INI, None);
    let datalog = msq_ini::read_datalog(INI, None);
    let memory = Arc::new(Mutex::new(vec![0u8; 0x100]));
    let transport = FakeTransport::responding(ecu_model(Arc::clone(&memory), false, Misbehaviour::None));
    let connection = Arc::new(EcuConnection::new(
        Box::new(transport.clone()),
        olv_live::connection::ecu_connection::EcuConnectionSettings {
            boot_wait: Duration::from_millis(1),
            ..Default::default()
        },
    ));
    connection.use_layout(&layout, false).unwrap();
    let mut source = TunerStudioSource::new(Arc::clone(&connection), RealtimeDecoder::new(layout, None), &datalog);

    let page = TunePage {
        index: 0,
        size: 0x100,
        identifier: r"\$tsCanId\x04".to_string(),
        read_command: r"r\$tsCanId\x04%2o%2c".to_string(),
        chunk_write_command: r"w\$tsCanId\x04%2o%2c%v".to_string(),
        burn_command: r"b\$tsCanId\x04".to_string(),
    };

    connection
        .write_tune_page(&page, 0, false, 0x10, &[1, 2, 3, 4], 0)
        .unwrap();
    source.recover().unwrap();

    let error = connection.burn_page(&page, false, 0).unwrap_err();
    assert!(error.refused());
    assert!(transport.written().iter().all(|w| w[2] != b'b'), "no burn was sent");
}
