//! A stand-in for a rusEFI board, written to the behaviour a real one showed on a bench rather than
//! to what its INI implies: the C# `RusefiTests`.
//!
//! It differs from a MegaSquirt in four ways that all matter, and three of them are silent: it
//! answers 'S' with its signature and refuses 'Q'; it reads the offset and count of a read
//! little-endian; and it will not send more than its blocking factor in one reply. That last one is
//! modelled as the port dying, because that is what happens: asking a real board for 1200 bytes when
//! it declares 1024 takes it off the USB bus until it is replugged.

use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::ini::ini_catalog::{self, IniFile};
use olv_core::ini::msq_ini::{self, RealtimeLayout, RealtimeType};
use olv_core::model::{LiveSource, RealtimeCommand};
use olv_core::{Error, Result};
use olv_live::connection::ecu_connection::{EcuConnection, EcuConnectionSettings};
use olv_live::live::live_session::{LiveSession, LiveSessionSettings};
use olv_protocol::ms::ms_protocol::{self, EcuTransport};
use olv_protocol::obd::realtime_decoder::RealtimeDecoder;

struct RusefiState {
    block: Vec<u8>,
    blocking_factor: usize,
    pending: Vec<u8>,
    open: bool,
    off_the_bus: bool,
    /// Every realtime read asked for, as (offset, count).
    reads: Vec<(usize, usize)>,
}

/// A rusEFI board. A cloneable handle, so the connection owns one clone and the test keeps another.
#[derive(Clone)]
struct FakeRusefi(Arc<Mutex<RusefiState>>);

impl FakeRusefi {
    fn new(block: Vec<u8>) -> Self {
        Self::with_blocking_factor(block, 1024)
    }

    fn with_blocking_factor(block: Vec<u8>, blocking_factor: usize) -> Self {
        Self(Arc::new(Mutex::new(RusefiState {
            block,
            blocking_factor,
            pending: Vec::new(),
            open: false,
            off_the_bus: false,
            reads: Vec::new(),
        })))
    }

    fn off_the_bus(&self) -> bool {
        self.0.lock().unwrap().off_the_bus
    }

    fn reads(&self) -> Vec<(usize, usize)> {
        self.0.lock().unwrap().reads.clone()
    }
}

fn gone() -> Error {
    Error::Io(std::io::Error::other("The device is not responding."))
}

fn reply_of(data: &[u8]) -> Vec<u8> {
    let mut body = vec![0x00];
    body.extend_from_slice(data);
    ms_protocol::frame(&body)
}

fn refusal(status: u8) -> Vec<u8> {
    ms_protocol::frame(&[status])
}

fn unframe(framed: &[u8]) -> Vec<u8> {
    if framed.len() < 7 {
        return Vec::new();
    }

    let length = (usize::from(framed[0]) << 8) | usize::from(framed[1]);
    framed[2..2 + length].to_vec()
}

impl RusefiState {
    fn answer(&mut self, payload: &[u8]) -> Result<Vec<u8>> {
        Ok(match payload[0] {
            b'S' => reply_of(b"rusEFI master.2024.11.17.uaefi.2834573262"),
            b'V' => reply_of(b"rusEFI v20241117@1431655765"),

            // MegaSquirt's query command. rusEFI has no such thing and says so.
            b'Q' => refusal(0x83),

            b'O' => self.realtime(payload)?,

            _ => refusal(0x83),
        })
    }

    fn realtime(&mut self, payload: &[u8]) -> Result<Vec<u8>> {
        // Little-endian, which is the whole point of this fake.
        let offset = usize::from(payload[1]) | (usize::from(payload[2]) << 8);
        let count = usize::from(payload[3]) | (usize::from(payload[4]) << 8);

        self.reads.push((offset, count));

        if count > self.blocking_factor {
            // Not an error reply. The board goes away.
            self.off_the_bus = true;
            return Err(gone());
        }

        if count < 1 || offset + count > self.block.len() {
            return Ok(refusal(0x84));
        }

        Ok(reply_of(&self.block[offset..offset + count]))
    }
}

impl EcuTransport for FakeRusefi {
    fn is_open(&self) -> bool {
        self.0.lock().unwrap().open
    }

    fn open(&mut self) -> Result<()> {
        self.0.lock().unwrap().open = true;
        Ok(())
    }

    fn close(&mut self) {
        self.0.lock().unwrap().open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut s = self.0.lock().unwrap();
        if s.off_the_bus {
            return Err(gone());
        }

        let payload = unframe(data);
        s.pending = if payload.is_empty() {
            Vec::new()
        } else {
            s.answer(&payload)?
        };
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let mut s = self.0.lock().unwrap();
        if s.off_the_bus {
            return Err(gone());
        }

        let take = buffer.len().min(s.pending.len());
        buffer[..take].copy_from_slice(&s.pending[..take]);
        s.pending.drain(..take);
        Ok(take)
    }

    fn discard_input(&mut self) {}
}

/// A board that counts how often it is asked the MegaSquirt query command.
struct CountingRusefi {
    inner: FakeRusefi,
    queries: Arc<Mutex<usize>>,
}

impl EcuTransport for CountingRusefi {
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
        if data.len() >= 3 && data[2] == b'Q' {
            *self.queries.lock().unwrap() += 1;
        }
        self.inner.write(data)
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        self.inner.read(buffer, timeout)
    }

    fn discard_input(&mut self) {
        self.inner.discard_input();
    }
}

/// The protocol-relevant lines of a rusEFI INI, in their real sections.
const INI: &str = r#"[TunerStudio]
queryCommand = "S"
versionInfo  = "V"
signature    = "rusEFI master.2024.11.17.uaefi.2834573262"

[Constants]
endianness     = little
blockingFactor = 1024

[OutputChannels]
ochGetCommand = "O%2o%2c"
ochBlockSize  = 32

RPMValue  = scalar, U16, 4,  "RPM",   1,    0
coolant   = scalar, S16, 12, "deg C", 0.01, 0
VBatt     = scalar, U16, 16, "V",     0.001, 0
MAPValue  = scalar, F32, 20, "kPa",   1,    0
needBurn  = bits,   U32, 0,  [6:6]

[Datalog]
entry = RPMValue, "RPM",  int,   "%d"
entry = coolant,  "CLT",  float, "%.2f"
entry = MAPValue, "MAP",  float, "%.2f"
"#;

fn layout() -> RealtimeLayout {
    msq_ini::read_output_channels(INI, None)
}

/// A block written the way the board writes it: little-endian throughout.
fn block() -> Vec<u8> {
    let mut block = vec![0u8; 32];

    block[0..4].copy_from_slice(&0b0100_0000u32.to_le_bytes()); // needBurn, bit 6
    block[4..6].copy_from_slice(&3000u16.to_le_bytes()); // RPM
    block[12..14].copy_from_slice(&2150i16.to_le_bytes()); // coolant 21.50 degrees C
    block[16..18].copy_from_slice(&13800u16.to_le_bytes()); // 13.8 V
    block[20..24].copy_from_slice(&101.3f32.to_le_bytes()); // MAP

    block
}

fn quick() -> EcuConnectionSettings {
    EcuConnectionSettings {
        retries: 2,
        timeout: Duration::from_millis(20),
        retry_pause: Duration::ZERO,
        ..EcuConnectionSettings::default()
    }
}

fn connection(transport: impl EcuTransport + 'static) -> EcuConnection {
    EcuConnection::new(Box::new(transport), quick())
}

fn at(decoder: &RealtimeDecoder, values: &[f64], name: &str) -> f64 {
    decoder
        .names()
        .iter()
        .rposition(|n| n.eq_ignore_ascii_case(name))
        .map_or(f64::NAN, |i| values[i])
}

fn close(actual: f64, expected: f64, tolerance: f64) {
    assert!((actual - expected).abs() <= tolerance, "{actual} is not {expected}");
}

// ----- what the INI says ------------------------------------------------

#[test]
fn the_byte_order_is_read_from_the_constants_section() {
    // It governs the channels but is declared nowhere near them, which is exactly why it is easy to
    // miss.
    assert!(layout().little_endian);
}

#[test]
fn a_megasquirt_ini_is_still_big_endian() {
    let ms = "[Constants]\nendianness = big\n\n[OutputChannels]\nochBlockSize = 4\n";

    assert!(!msq_ini::read_output_channels(ms, None).little_endian);
}

#[test]
fn an_ini_that_says_nothing_about_byte_order_is_big_endian() {
    assert!(!msq_ini::read_output_channels("[OutputChannels]\nochBlockSize = 4\n", None).little_endian);
}

#[test]
fn the_blocking_factor_is_read() {
    assert_eq!(layout().blocking_factor, 1024);
}

#[test]
fn float_channels_are_understood() {
    let layout = layout();
    let maps: Vec<_> = layout.fields.iter().filter(|f| f.name == "MAPValue").collect();

    assert_eq!(maps.len(), 1);
    assert_eq!(maps[0].type_, RealtimeType::F32);
    assert_eq!(maps[0].size(), 4);
    assert!(layout.skipped.is_empty());
}

// ----- the request ------------------------------------------------------

#[test]
fn the_rusefi_read_command_is_built_from_its_template() {
    let request = RealtimeCommand::parse("O%2o%2c")
        .build(0, 1824, 0, true, &[], &[])
        .unwrap();

    // 1824 is 0x0720, and little-endian on the wire it is 20 07. Sent the other way round the board
    // reads it as 8199 and refuses the read.
    assert_eq!(request, [b'O', 0x00, 0x00, 0x20, 0x07]);
}

#[test]
fn the_megasquirt_template_still_builds_the_request_it_always_did() {
    let from_template = RealtimeCommand::parse("r\\$tsCanId\\x07%2o%2c")
        .build(0, 512, 0, false, &[], &[])
        .unwrap();

    assert_eq!(ms_protocol::realtime_request(512, 0).unwrap(), from_template);
}

#[test]
fn a_can_id_goes_where_the_template_says_it_does() {
    let request = RealtimeCommand::parse("r\\$tsCanId\\x07%2o%2c")
        .build(0, 512, 3, false, &[], &[])
        .unwrap();

    assert_eq!(request[1], 3);
}

#[test]
fn a_plain_command_takes_no_range() {
    let simple = RealtimeCommand::parse("A");

    assert!(!simple.takes_range());
    assert_eq!(simple.build(0, 512, 0, false, &[], &[]).unwrap(), [b'A']);
}

#[test]
fn an_unknown_variable_contributes_no_byte() {
    // A guessed byte would ask for something else entirely.
    assert_eq!(
        RealtimeCommand::parse("O\\$whatIsThis")
            .build(0, 1, 0, false, &[], &[])
            .unwrap(),
        [b'O']
    );
}

// ----- reading it -------------------------------------------------------

#[test]
fn a_rusefi_block_decodes_little_endian() {
    let decoder = RealtimeDecoder::new(layout(), None);
    let values = decoder.decode(&block());

    close(at(&decoder, &values, "RPMValue"), 3000.0, 1e-4);
    close(at(&decoder, &values, "coolant"), 21.5, 1e-4);
    close(at(&decoder, &values, "VBatt"), 13.8, 1e-4);
    close(at(&decoder, &values, "MAPValue"), 101.3, 1e-3);
    close(at(&decoder, &values, "needBurn"), 1.0, 1e-4);
}

#[test]
fn the_same_bytes_read_big_endian_are_not_obviously_wrong() {
    // The reason the byte order has to come from the INI rather than be sniffed: read the wrong way
    // round, this block yields 47115 RPM and 15.2 degrees C, both of which a display renders without
    // complaint.
    let decoder = RealtimeDecoder::new(
        RealtimeLayout {
            little_endian: false,
            ..layout()
        },
        None,
    );
    let values = decoder.decode(&block());

    assert_ne!(at(&decoder, &values, "RPMValue"), 3000.0);
    assert!(!at(&decoder, &values, "RPMValue").is_nan());
}

// ----- talking to one ---------------------------------------------------

#[test]
fn the_signature_is_found_even_though_the_query_command_is_refused() {
    let connection = connection(FakeRusefi::new(block()));

    let identity = connection.read_identity().unwrap();

    assert!(identity.contains(&"rusEFI master.2024.11.17.uaefi.2834573262".to_string()));
    assert!(identity.contains(&"rusEFI v20241117@1431655765".to_string()));
}

#[test]
fn the_catalogue_decides_which_reply_was_the_signature() {
    // "rusEFI v2024..." comes back too and is not a signature; the INI that recognises one of them is
    // what settles it.
    let catalogue = [IniFile::new("rusefi.ini", "rusEFI master.2024.11.17.uaefi.2834573262")];
    let identity = [
        "rusEFI v20241117@1431655765",
        "rusEFI master.2024.11.17.uaefi.2834573262",
    ];

    let (ini, signature) = ini_catalog::match_any(identity, &catalogue).unwrap();

    assert_eq!(ini.path, "rusefi.ini");
    assert_eq!(signature, "rusEFI master.2024.11.17.uaefi.2834573262");
}

#[test]
fn a_megasquirt_signature_is_still_found_among_the_same_candidates() {
    let catalogue = [IniFile::new("ms3.ini", "MS3 Format 0592.13P")];
    let identity = ["MShift 1.2.3 build 4", "MS3 Format 0592.13P"];

    assert_eq!(
        ini_catalog::match_any(identity, &catalogue).unwrap().1,
        "MS3 Format 0592.13P"
    );
}

#[test]
fn no_match_is_no_match_rather_than_the_first_reply() {
    assert!(
        ini_catalog::match_any(
            ["something else entirely"],
            &[IniFile::new("a.ini", "MS3 Format 0592.13P")]
        )
        .is_none()
    );
}

#[test]
fn a_refused_command_is_not_retried() {
    // Probing a rusEFI with 'Q' is a refusal by design. Retrying it three times spends three timeouts
    // on an answer that will not change.
    let queries = Arc::new(Mutex::new(0));
    let connection = connection(CountingRusefi {
        inner: FakeRusefi::new(block()),
        queries: Arc::clone(&queries),
    });

    let _ = connection.read_identity();

    assert_eq!(*queries.lock().unwrap(), 1);
}

// ----- the blocking factor ----------------------------------------------

fn ascending(len: usize) -> Vec<u8> {
    (0..len).map(|i| i as u8).collect()
}

#[test]
fn a_block_larger_than_one_reply_is_fetched_in_pieces() {
    let block = ascending(2500);

    let transport = FakeRusefi::new(block.clone());
    let connection = connection(transport.clone());
    connection
        .use_layout(
            &RealtimeLayout {
                block_size: 2500,
                ..layout()
            },
            false,
        )
        .expect("layout accepted");

    let read = connection.read_realtime(2500).unwrap();

    assert_eq!(read, block);
    assert_eq!(transport.reads(), [(0, 1024), (1024, 1024), (2048, 452)]);
}

#[test]
fn nothing_ever_asks_for_more_than_the_blocking_factor() {
    // The one that has teeth. A board asked for more does not refuse: it leaves the USB bus, and the
    // only fix is to unplug it.
    let transport = FakeRusefi::new(vec![0; 2500]);
    let connection = connection(transport.clone());
    connection
        .use_layout(
            &RealtimeLayout {
                block_size: 2500,
                ..layout()
            },
            false,
        )
        .expect("layout accepted");

    connection.read_realtime(2500).unwrap();

    assert!(!transport.off_the_bus());
    assert!(transport.reads().iter().all(|&(_, count)| count <= 1024));
}

#[test]
fn a_block_that_fits_in_one_reply_is_not_split() {
    let transport = FakeRusefi::new(block());
    let connection = connection(transport.clone());
    connection.use_layout(&layout(), false).expect("layout accepted");

    connection.read_realtime(32).unwrap();

    assert_eq!(transport.reads(), [(0, 32)]);
}

#[test]
fn asking_for_the_whole_block_in_one_request_is_opt_in() {
    // Measured on two boards: an MS3 declaring 256 serves its whole 512-byte block, a rusEFI
    // declaring 1024 leaves the USB bus when asked for 1280. Nothing in either INI tells the two
    // apart, so the safe reading is the default and this is the way out of it.
    let transport = FakeRusefi::with_blocking_factor(vec![0; 2500], 4096);
    let connection = connection(transport.clone());

    connection
        .use_layout(
            &RealtimeLayout {
                block_size: 2500,
                ..layout()
            },
            true,
        )
        .expect("layout accepted");
    connection.read_realtime(2500).unwrap();

    assert_eq!(transport.reads(), [(0, 2500)]);
}

#[test]
fn the_blocking_factor_is_still_obeyed_by_default() {
    let transport = FakeRusefi::new(vec![0; 2500]);
    let connection = connection(transport.clone());

    connection
        .use_layout(
            &RealtimeLayout {
                block_size: 2500,
                ..layout()
            },
            false,
        )
        .expect("layout accepted");
    connection.read_realtime(2500).unwrap();

    assert!(transport.reads().iter().all(|&(_, count)| count <= 1024));
}

#[test]
fn a_firmware_that_declares_no_blocking_factor_is_read_whole() {
    let transport = FakeRusefi::with_blocking_factor(vec![0; 2500], 4096);
    let connection = connection(transport.clone());
    connection
        .use_layout(
            &RealtimeLayout {
                block_size: 2500,
                blocking_factor: 0,
                ..layout()
            },
            false,
        )
        .expect("layout accepted");

    connection.read_realtime(2500).unwrap();

    assert_eq!(transport.reads(), [(0, 2500)]);
}

/// End to end: an INI, a board, and the numbers a gauge would show.
#[test]
fn a_reading_comes_back_off_the_wire_as_the_right_number() {
    let layout = layout();

    let connection = connection(FakeRusefi::new(block()));
    connection.use_layout(&layout, false).expect("layout accepted");

    let decoder = RealtimeDecoder::new(layout.clone(), None);
    let values = decoder.decode(&connection.read_realtime(layout.block_size).unwrap());

    close(at(&decoder, &values, "RPMValue"), 3000.0, 1e-4);
    close(at(&decoder, &values, "MAPValue"), 101.3, 1e-3);
}

#[test]
fn a_live_session_over_rusefi_takes_its_channels_from_the_datalog() {
    let layout = layout();

    let connection = connection(FakeRusefi::new(block()));
    connection.use_layout(&layout, false).expect("layout accepted");

    let session = LiveSession::for_tuner_studio(
        Arc::new(connection),
        RealtimeDecoder::new(layout, None),
        &msq_ini::read_datalog(INI, None),
        LiveSessionSettings {
            recording_path: None,
            ..LiveSessionSettings::default()
        },
    );

    assert_eq!(session.names(), ["RPM", "CLT", "MAP"]);
}

/// The source is a `LiveSource` that can be driven without a session: the trait object a session
/// takes.
#[test]
fn a_tunerstudio_source_is_a_live_source() {
    fn takes(_: &dyn LiveSource) {}

    let layout = layout();
    let connection = Arc::new(connection(FakeRusefi::new(block())));
    connection.use_layout(&layout, false).expect("layout accepted");

    let source = olv_live::connection::tuner_studio_source::TunerStudioSource::new(
        connection,
        RealtimeDecoder::new(layout, None),
        &msq_ini::read_datalog(INI, None),
    );

    takes(&source);
}
