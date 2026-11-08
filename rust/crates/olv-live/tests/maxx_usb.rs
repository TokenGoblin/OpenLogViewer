//! The protocol a MaxxECU speaks over USB, checked against a recording of MTune speaking it, and the
//! source that polls it: the C# `MaxxUsbTests`.
//!
//! `maxxecu-usb.txt` is request and reply pairs lifted from a capture of MTune 1.161 connecting to a
//! MaxxECU Race over its USB cable: every distinct command it used, plus a run of telemetry rounds.
//! Nothing in it was composed here, which is the point: this protocol was recovered by watching
//! rather than from any documentation, so the only thing that can confirm it is the wire.
//!
//! The protocol-only tests also exist in `olv-protocol` (three of them); they are repeated here
//! because the brief lists the whole class. The source tests, and the replays of the recording
//! through `MaxxUsbSource` and `maxx_tune`, are this package's own.

mod live_doubles;

use std::collections::HashMap;
use std::time::Duration;

use live_doubles::{RecordedEcu, recorded_maxx_usb};
use olv_core::logs::maxx_channel_definitions;
use olv_core::model::LiveSource;
use olv_core::{Error, Result};
use olv_live::live::maxx_tune;
use olv_live::live::maxx_usb_source::{self, MaxxUsbSource};
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use olv_protocol::ms::ms_protocol::EcuTransport;

fn asked(request: &[u8]) -> i32 {
    i32::from(request[4]) | (i32::from(request[5]) << 8)
}

#[test]
fn the_recording_is_there() {
    assert_eq!(recorded_maxx_usb().len(), 30);
}

#[test]
fn every_recorded_request_is_reproduced_exactly() {
    // Composing each request from its own fields has to land on the bytes MTune sent, checksum
    // included. This is the whole claim about the request format in one assertion.
    for (sent, _) in recorded_maxx_usb() {
        let rebuilt = usb::request(
            sent[0],
            sent[1],
            i32::from(sent[2]) | (i32::from(sent[3]) << 8),
            i32::from(sent[4]) | (i32::from(sent[5]) << 8),
        )
        .unwrap();

        assert_eq!(sent, rebuilt);
    }
}

#[test]
fn every_recorded_reply_is_accepted_and_its_data_recovered() {
    for (request, reply) in recorded_maxx_usb() {
        let asked = asked(&request);

        // Only the reads: a write's reply answers the payload that followed the header rather than
        // the header's own length.
        if request[0] != usb::READ {
            continue;
        }
        if reply.len() as i32 != usb::reply_length(asked) {
            continue;
        }

        let data = usb::try_read_reply(&reply, asked)
            .unwrap_or_else(|| panic!("A recorded reply to command 0x{:02X} was refused.", request[1]));

        assert_eq!(data.len() as i32, asked);
    }
}

#[test]
fn a_request_is_ten_bytes_with_the_checksum_over_its_header() {
    let request = usb::ask_what_is_waiting();

    assert_eq!(request.len(), usb::REQUEST_LENGTH);
    assert_eq!(request[0], usb::READ);
    assert_eq!(request[1], usb::WAITING);

    // The checksum is the whole 32 bits here, where the Bluetooth framing keeps only the low half of
    // the same arithmetic.
    let carried = u32::from_le_bytes([request[6], request[7], request[8], request[9]]);

    assert_eq!(maxx_protocol::crc32(&request[..6]), carried);
    assert_eq!(maxx_protocol::checksum(&request[..6]), carried as u16);
}

#[test]
fn a_reply_with_a_broken_checksum_is_refused() {
    let (request, reply) = recorded_maxx_usb()
        .into_iter()
        .find(|(request, _)| request[0] == usb::READ)
        .unwrap();
    let asked = asked(&request);

    assert!(usb::try_read_reply(&reply, asked).is_some());

    let mut broken = reply.clone();
    *broken.last_mut().unwrap() ^= 0xFF;

    assert!(usb::try_read_reply(&broken, asked).is_none());
}

#[test]
fn a_reply_of_the_wrong_length_is_refused_rather_than_read_past() {
    let reply = [usb::OK, 1, 2, 3, 4, 5];

    assert!(usb::try_read_reply(&reply, 2).is_none());
    assert!(usb::try_read_reply(&reply, 99).is_none());
    assert!(usb::try_read_reply(&[], 0).is_none());
}

#[test]
fn a_failed_status_is_not_read_as_data() {
    // 0x30 appears in the recording and is not 0x80. Reading its body as a reply is how an error
    // becomes a row of numbers.
    let body = [0x30u8, 0x00];
    let crc = maxx_protocol::crc32(&body);

    let mut reply = body.to_vec();
    reply.extend_from_slice(&crc.to_le_bytes());

    assert!(usb::try_read_reply(&reply, 1).is_none());
}

#[test]
fn every_recorded_telemetry_payload_decodes_as_ascending_channels() {
    let mut rounds = 0;
    let mut channels: HashMap<i32, u16> = HashMap::new();

    for (request, reply) in recorded_maxx_usb() {
        if request[1] != usb::TAKE {
            continue;
        }

        let asked = asked(&request);
        let Some(payload) = usb::try_read_reply(&reply, asked) else {
            continue;
        };

        assert!(
            usb::looks_like_telemetry(&payload),
            "A recorded telemetry payload does not read as a list of channels."
        );

        assert!(usb::read_updates(&payload, &mut channels) > 0);
        rounds += 1;
    }

    assert!(rounds > 0);

    // And the values are the ones the bench ECU was actually showing: a charged supply, and a
    // barometric MAP with the engine stopped. Ranges rather than exact counts, because both wander
    // by a count or two between samples, but a wrong pairing, a wrong endianness or a half-sample
    // offset survives the structural check above and none of them lands anywhere near these.
    let battery = f64::from(channels[&21]) * 0.01;
    let map = f64::from(channels[&20]) * 0.1;
    assert!((12.0..=15.0).contains(&battery), "battery {battery}");
    assert!((80.0..=105.0).contains(&map), "MAP {map}");
}

#[test]
fn a_payload_whose_channels_do_not_ascend_is_refused() {
    // Ids climb and never repeat in all 1,765 recorded payloads, so anything else is a stream being
    // read at the wrong offset, which otherwise yields channel numbers that exist and values that are
    // nonsense.
    assert!(!usb::looks_like_telemetry(&[
        0x05, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01, 0x00
    ]));
    assert!(!usb::looks_like_telemetry(&[
        0x05, 0x00, 0x01, 0x00, 0x05, 0x00, 0x01, 0x00
    ]));
    assert!(!usb::looks_like_telemetry(&[0x05, 0x00, 0x01]));
    assert!(usb::looks_like_telemetry(&[
        0x05, 0x00, 0x01, 0x00, 0x06, 0x00, 0x01, 0x00
    ]));
}

#[test]
fn decimals_come_from_the_scale_rather_than_being_chosen() {
    for (scale, digits) in [(1.0, 0), (0.1, 1), (0.01, 2), (0.001, 3)] {
        assert_eq!(maxx_channel_definitions::digits_for(scale), digits, "scale {scale}");
    }
}

#[test]
fn an_offset_or_length_that_will_not_fit_the_frame_is_refused() {
    assert!(matches!(usb::request(0, 0x12, 70000, 1), Err(Error::Argument(_))));
    assert!(matches!(usb::request(0, 0x12, 0, 70000), Err(Error::Argument(_))));
    assert!(matches!(usb::request(0, 0x12, -1, 1), Err(Error::Argument(_))));
}

// ----- what the ECU does not mention ---------------------------------------

/// An ECU that answers, and sends only the handful of channels that happen to be moving, which is
/// what a real one does with the engine off.
struct QuietEcu {
    sends: Vec<i32>,
    reply: Vec<u8>,
    sent: bool,
    open: bool,
}

impl QuietEcu {
    fn new(sends: &[i32]) -> Self {
        Self {
            sends: sends.to_vec(),
            reply: Vec::new(),
            sent: false,
            open: false,
        }
    }

    /// The whole state once, then nothing, because nothing changes.
    fn waiting(&self) -> Vec<u8> {
        let bytes = if self.sent { 0 } else { self.sends.len() * 4 };

        vec![(bytes & 0xFF) as u8, (bytes >> 8) as u8]
    }

    fn telemetry(&mut self, length: usize) -> Vec<u8> {
        self.sent = true;

        let mut payload = vec![0u8; length];

        for (i, id) in self.sends.iter().enumerate() {
            if (i * 4) + 4 > length {
                break;
            }

            payload[i * 4] = (id & 0xFF) as u8;
            payload[(i * 4) + 1] = (id >> 8) as u8;
            payload[(i * 4) + 2] = 1;
        }

        payload
    }

    fn answer(data: &[u8]) -> Vec<u8> {
        let mut reply = vec![usb::OK];
        reply.extend_from_slice(data);
        reply.extend_from_slice(&maxx_protocol::crc32(&reply).to_le_bytes());

        reply
    }
}

impl EcuTransport for QuietEcu {
    fn is_open(&self) -> bool {
        self.open
    }

    fn open(&mut self) -> Result<()> {
        self.open = true;
        Ok(())
    }

    fn close(&mut self) {
        self.open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let command = data[1];
        let length = usize::from(data[4]) | (usize::from(data[5]) << 8);

        self.reply = match command {
            usb::HELLO => Self::answer(&[0x00]),
            usb::WAITING => Self::answer(&self.waiting()),
            usb::TAKE => {
                let payload = self.telemetry(length);
                Self::answer(&payload)
            }
            _ => Self::answer(&vec![0u8; length]),
        };

        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let taken = buffer.len().min(self.reply.len());
        buffer[..taken].copy_from_slice(&self.reply[..taken]);
        self.reply.drain(..taken);

        Ok(taken)
    }

    fn discard_input(&mut self) {
        self.reply.clear();
    }
}

/// The channels anybody connects to see are columns even when the ECU has not mentioned them.
///
/// They were not, and the cost was the whole session: a MaxxECU sends a channel only when its value
/// changes, so connecting with the engine off (cable first, key second) learnt input voltages and
/// counters and left out engine speed, coolant, lambda and ignition angle. A log's columns cannot
/// change once it has rows, so they stayed out for the rest of the drive.
#[test]
fn the_channels_worth_logging_are_columns_even_when_nothing_has_moved() {
    // Three channels, as a bench ECU with nothing running offers: a raw input voltage, manifold
    // pressure and the battery.
    let mut source = MaxxUsbSource::new(Box::new(QuietEcu::new(&[0, 20, 21])), None);
    source.open().unwrap();

    let found: Vec<i32> = source.channels().iter().map(|c| c.id).collect();

    assert!(found.contains(&61)); // RPM
    assert!(found.contains(&18)); // coolant
    assert!(found.contains(&5)); // lambda
    assert!(found.contains(&60)); // ignition angle
    assert!(found.contains(&19)); // throttle position

    for id in maxx_usb_source::always_logged() {
        assert!(found.contains(id), "{id} is always logged");
    }

    // And what the ECU did send is still there.
    assert!(found.contains(&0));
    assert!(found.contains(&20));
}

/// The core set is the one MaxxECU chose for its own Bluetooth dash, so a channel means the same
/// thing over either link, with throttle position added because that set leaves it out.
#[test]
fn the_core_set_is_what_bluetooth_subscribes_to_plus_the_throttle() {
    let always = maxx_usb_source::always_logged();

    for channel in maxx_protocol::SUBSCRIBED {
        assert!(always.contains(&channel.id), "{}", channel.id);
    }

    assert!(always.contains(&19));
    assert_eq!(always.len(), maxx_protocol::SUBSCRIBED.len() + 1);
    assert_eq!(always.len(), 15);
    assert!(always.windows(2).all(|w| w[0] < w[1]), "ascending, no repeats");
}

// ----- the source, over the recording --------------------------------------------

/// The recording, replayed through the source: it learns every channel any recorded payload names,
/// plus the core set, and nothing else. No MTune is consulted (a path that does not exist), so the
/// channels are named by number.
#[test]
fn the_source_learns_the_recorded_channels_and_names_them_by_number() {
    let mut expected: HashMap<i32, u16> = HashMap::new();
    for (request, reply) in recorded_maxx_usb() {
        if request[1] == usb::TAKE
            && let Some(payload) = usb::try_read_reply(&reply, asked(&request))
        {
            usb::read_updates(&payload, &mut expected);
        }
    }

    let nowhere = Some(std::env::temp_dir().join("olv-no-such-definitions.xml"));
    let mut source = MaxxUsbSource::new(Box::new(RecordedEcu::new()), nowhere);
    source.open().unwrap();

    let mut ids: Vec<i32> = expected.keys().copied().collect();
    ids.extend(maxx_usb_source::always_logged());
    ids.sort_unstable();
    ids.dedup();

    assert_eq!(source.channels().iter().map(|c| c.id).collect::<Vec<_>>(), ids);
    assert_eq!(
        source.names().iter().map(String::as_str).collect::<Vec<_>>(),
        ids.iter().map(|id| format!("Channel {id}")).collect::<Vec<_>>()
    );
    assert_eq!(source.names().len(), source.units().len());
    assert_eq!(source.names().len(), source.digits().len());

    // And a reading is one value per channel. Unnamed, a channel is unscaled, so the battery is
    // its raw count (hundredths of a volt) where the bench had it.
    let reading = source.read().unwrap();
    assert_eq!(reading.len(), ids.len());
    let battery = reading[ids.iter().position(|&id| id == 21).unwrap()];
    assert!((1200.0..=1500.0).contains(&battery), "battery {battery}");
}

/// A source that was never opened has no channels to read, and says so rather than returning an
/// empty row.
#[test]
fn a_source_that_was_never_opened_refuses_to_read() {
    let mut source = MaxxUsbSource::new(Box::new(QuietEcu::new(&[0])), None);

    assert!(matches!(source.read(), Err(Error::InvalidOperation(_))));
}

/// An empty cable is told apart from an ECU with nothing to say.
#[test]
fn nothing_answering_is_reported_as_no_maxxecu_with_a_hint_about_power() {
    let mut source = MaxxUsbSource::new(
        Box::new(live_doubles::ThrowingTransport::new(live_doubles::Fault::Io("x"))),
        None,
    );
    assert!(source.open().is_err());

    // A transport that opens and says nothing.
    struct Silent;
    impl EcuTransport for Silent {
        fn is_open(&self) -> bool {
            true
        }
        fn open(&mut self) -> Result<()> {
            Ok(())
        }
        fn close(&mut self) {}
        fn write(&mut self, _: &[u8]) -> Result<()> {
            Ok(())
        }
        fn read(&mut self, _: &mut [u8], _: Duration) -> Result<usize> {
            Ok(0)
        }
        fn discard_input(&mut self) {}
    }

    let mut source = MaxxUsbSource::new(Box::new(Silent), None);
    let error = source.open().unwrap_err();

    assert!(matches!(&error, Error::Protocol(m) if m.contains("12 V")), "{error}");
    assert!(source.retries() >= 1, "the hello was asked twice");
}

// ----- the tune commands, over the recording --------------------------------------

/// MTune asks the ECU to checksum 62,180 bytes of its tune: command 0x37 with the count in the
/// offset field. The request this builds is the one it sent, and the sixteen checksums it gets back
/// are the sixteen in the recording.
#[test]
fn the_tune_checksum_request_is_the_one_mtune_sent_and_its_reply_reads_back() {
    let (request, reply) = recorded_maxx_usb().into_iter().find(|(r, _)| r[1] == 0x37).unwrap();

    assert_eq!(request, usb::request(usb::READ, 0x37, 62180, 64).unwrap());

    let mut ecu = RecordedEcu::new();
    let checksums = maxx_tune::checksums(&mut ecu, 62180).unwrap();

    assert_eq!(checksums.len(), 16);
    assert_eq!(ecu.requests, [request]);

    let data = usb::try_read_reply(&reply, 64).unwrap();
    for (i, checksum) in checksums.iter().enumerate() {
        assert_eq!(
            *checksum,
            u32::from_le_bytes(data[i * 4..i * 4 + 4].try_into().unwrap())
        );
    }
}

/// The two 256-byte reads of the top of the tune the recording holds are asked for exactly as
/// `verify` asks for a run, and read back as the bytes the recording holds.
#[test]
fn the_recorded_reads_of_the_top_of_the_tune_verify_against_themselves() {
    for (request, reply) in recorded_maxx_usb()
        .into_iter()
        .filter(|(r, _)| r[1] == maxx_tune::READ_TUNE)
    {
        let offset = i32::from(request[2]) | (i32::from(request[3]) << 8);
        let length = asked(&request);

        assert_eq!(
            request,
            usb::request(usb::READ, maxx_tune::READ_TUNE, offset, length).unwrap()
        );

        let expected = usb::try_read_reply(&reply, length).unwrap();
        assert!(
            maxx_tune::verify(&mut RecordedEcu::new(), offset, &expected).unwrap(),
            "at {offset:#06x}"
        );

        let mut wrong = expected.clone();
        wrong[0] ^= 1;
        assert!(!maxx_tune::verify(&mut RecordedEcu::new(), offset, &wrong).unwrap());
    }
}
