//! Ported from `EcuWriteTests.cs`: the tests that drive `EcuConnection` over fake boards, which
//! `olv-core` cannot host (it cannot depend on `olv-live`). The tune side of that class (encoding,
//! the command templates, the end-to-end re-read over `TunePageIo`) is in
//! `olv-core/tests/tune_ecu_write.rs`.
//!
//! The fakes are this file's own, one per C# class: `WritableEcu`, `LateTransport`,
//! `CountingSettle`, `RecordingEcu`, and `EcuBurnReplyTests`' `Board`, `SilentBoard` and
//! `SlowBoard`. Each is a cloneable handle, so the connection owns one clone and the test keeps
//! the other to look at.

use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::thread;
use std::time::{Duration, Instant};

use olv_core::Error;
use olv_core::Result;
use olv_core::ini::msq_ini;
use olv_core::ini::tune_layout::{self, TunePage};
use olv_live::connection::ecu_connection::{EcuConnection, EcuConnectionSettings, EcuError};
use olv_protocol::ms::ms_protocol::{self, EcuTransport};

const INI: &str = r#"[Constants]
endianness      = little
nPages          = 1
pageSize        = 256
pageIdentifier  = "\x00\x00"
pageReadCommand = "R%2o%2c"
pageChunkWrite  = "C%2o%2c%v"
burnCommand     = "B"
blockingFactor  = 64

page = 1
veTable    = array, U16, 0,  [4x2], "%", 0.1, 0, 0, 999, 1
veRpmBins  = array, U16, 16, [4],   "RPM", 1, 0, 0, 18000, 0
veLoadBins = array, U16, 24, [2],   "kPa", 1, 0, 0, 255, 0
idleTrim   = scalar, S16, 40, "%", 0.1, 0, -100, 100, 1
"#;

fn page() -> TunePage {
    tune_layout::read(INI, None).pages[0].clone()
}

fn quick() -> EcuConnectionSettings {
    EcuConnectionSettings {
        retries: 1,
        timeout: Duration::from_millis(20),
        retry_pause: Duration::ZERO,
        ..EcuConnectionSettings::default()
    }
}

fn lock<T>(state: &Mutex<T>) -> MutexGuard<'_, T> {
    state.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Hands out what is pending, as the C# fakes' `Read` does.
fn take_pending(pending: &mut Vec<u8>, buffer: &mut [u8]) -> usize {
    let take = buffer.len().min(pending.len());
    buffer[..take].copy_from_slice(&pending[..take]);
    pending.drain(..take);
    take
}

/// The payload of a framed request, or empty when it is too short to be one.
fn payload_of(data: &[u8]) -> Vec<u8> {
    if data.len() < 7 {
        return Vec::new();
    }

    let length = (usize::from(data[0]) << 8) | usize::from(data[1]);
    data.get(2..2 + length).map(<[u8]>::to_vec).unwrap_or_default()
}

fn reply_framed(status: u8, data: &[u8]) -> Vec<u8> {
    let mut body = vec![status];
    body.extend_from_slice(data);
    ms_protocol::frame(&body)
}

// ----- WritableEcu -----------------------------------------------------------

struct WritableState {
    pending: Vec<u8>,
    open: bool,
    page: Vec<u8>,
    blocking_factor: usize,
    burns: usize,
    /// Each write's offset and count.
    writes: Vec<(usize, usize)>,
    /// Writes land at this many bytes off, the way a byte-order slip would.
    skew: i64,
    /// When set, writes are acknowledged and thrown away.
    silently_discard_writes: bool,
}

/// A board whose page can be read and written, and which can be told to take a write badly in
/// each of the ways that matter.
#[derive(Clone)]
struct WritableEcu(Arc<Mutex<WritableState>>);

impl WritableEcu {
    fn new(page: Vec<u8>) -> Self {
        Self(Arc::new(Mutex::new(WritableState {
            pending: Vec::new(),
            open: false,
            page,
            blocking_factor: 64,
            burns: 0,
            writes: Vec::new(),
            skew: 0,
            silently_discard_writes: false,
        })))
    }

    fn with_skew(self, skew: i64) -> Self {
        lock(&self.0).skew = skew;
        self
    }

    fn discarding(self) -> Self {
        lock(&self.0).silently_discard_writes = true;
        self
    }

    fn burns(&self) -> usize {
        lock(&self.0).burns
    }

    fn writes(&self) -> Vec<(usize, usize)> {
        lock(&self.0).writes.clone()
    }

    fn page(&self) -> Vec<u8> {
        lock(&self.0).page.clone()
    }
}

impl WritableState {
    fn answer(&mut self, payload: &[u8]) -> Vec<u8> {
        let refusal = |status: u8| reply_framed(status, &[]);
        let number = |at: usize| usize::from(payload[at]) | (usize::from(payload[at + 1]) << 8);

        match payload[0] as char {
            // R <offset:2 LE> <count:2 LE>
            'R' => {
                if payload.len() < 5 {
                    return refusal(0x80);
                }
                let (offset, count) = (number(1), number(3));

                if count > self.blocking_factor {
                    return refusal(0x84);
                }
                if count < 1 || offset + count > self.page.len() {
                    return refusal(0x84);
                }

                reply_framed(0x00, &self.page[offset..offset + count])
            }

            // C <offset:2 LE> <count:2 LE> <data>
            'C' => {
                if payload.len() < 5 {
                    return refusal(0x80);
                }
                let (offset, count) = (number(1), number(3));

                if payload.len() < 5 + count {
                    return refusal(0x80);
                }
                if offset + count > self.page.len() {
                    return refusal(0x84);
                }

                self.writes.push((offset, count));

                if !self.silently_discard_writes {
                    let at = offset as i64 + self.skew;
                    if at >= 0 && at as usize + count <= self.page.len() {
                        let at = at as usize;
                        self.page[at..at + count].copy_from_slice(&payload[5..5 + count]);
                    }
                }

                reply_framed(0x00, &[])
            }

            'B' => {
                self.burns += 1;
                reply_framed(0x00, &[])
            }

            _ => refusal(0x83),
        }
    }
}

impl EcuTransport for WritableEcu {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let payload = payload_of(data);
        let mut state = lock(&self.0);
        state.pending = if payload.is_empty() {
            Vec::new()
        } else {
            state.answer(&payload)
        };
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(take_pending(&mut lock(&self.0).pending, buffer))
    }

    fn discard_input(&mut self) {}
}

// ----- LateTransport -----------------------------------------------------------

struct LateState {
    first: Vec<u8>,
    second: Vec<u8>,
    late: Vec<Vec<u8>>,
    pending: Vec<u8>,
    writes: usize,
    discards: usize,
    open: bool,
}

/// A link that answers late, the way a Bluetooth one does.
///
/// The reply to a request that timed out arrives while the next is in flight. Nothing in the
/// protocol says which request a reply answers (there is no echo of the offset or count), so a
/// straggler is otherwise decoded as the answer to whatever was asked next.
#[derive(Clone)]
struct LateTransport(Arc<Mutex<LateState>>);

impl LateTransport {
    fn new(first: Vec<u8>, second: Vec<u8>) -> Self {
        Self(Arc::new(Mutex::new(LateState {
            first,
            second,
            late: Vec::new(),
            pending: Vec::new(),
            writes: 0,
            discards: 0,
            open: false,
        })))
    }
}

impl EcuTransport for LateTransport {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write(&mut self, _data: &[u8]) -> Result<()> {
        let mut state = lock(&self.0);
        state.writes += 1;

        // The first request is answered too late to be read, so its reply is still queued when
        // the second goes out.
        if state.writes == 1 {
            let first = state.first.clone();
            state.late.push(first);
        } else {
            state.pending = if state.late.is_empty() {
                state.second.clone()
            } else {
                state.late.remove(0)
            };
        }
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(take_pending(&mut lock(&self.0).pending, buffer))
    }

    fn discard_input(&mut self) {
        lock(&self.0).discards += 1;
    }
}

// ----- CountingSettle -----------------------------------------------------------

#[derive(Default)]
struct SettleState {
    writes: usize,
    reads_since_write: usize,
    drained_before_second_write: bool,
    open: bool,
}

/// Never answers, and records whether a drain happened between writes.
#[derive(Clone, Default)]
struct CountingSettle(Arc<Mutex<SettleState>>);

impl EcuTransport for CountingSettle {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write(&mut self, _data: &[u8]) -> Result<()> {
        let mut state = lock(&self.0);
        state.writes += 1;

        // Reads after the first write and before the second are the header read that timed out
        // plus the drain that followed it.
        if state.writes == 2 && state.reads_since_write > 1 {
            state.drained_before_second_write = true;
        }

        state.reads_since_write = 0;
        Ok(())
    }

    fn read(&mut self, _buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        lock(&self.0).reads_since_write += 1;
        Ok(0)
    }

    fn discard_input(&mut self) {}
}

// ----- RecordingEcu -----------------------------------------------------------

struct RecordingState {
    pending: Vec<u8>,
    open: bool,
    page: Vec<u8>,
    big_endian: bool,
    /// Each request's payload, without the length and checksum around it.
    requests: Vec<Vec<u8>>,
}

/// A board that remembers every request it was asked, so a test can check the bytes rather than
/// only the effect. Speaks the MegaSquirt commands the real firmware declares: 'w' to write, 'r'
/// to read, 'b' to burn.
#[derive(Clone)]
struct RecordingEcu(Arc<Mutex<RecordingState>>);

impl RecordingEcu {
    fn new(page: Vec<u8>, big_endian: bool) -> Self {
        Self(Arc::new(Mutex::new(RecordingState {
            pending: Vec::new(),
            open: false,
            page,
            big_endian,
            requests: Vec::new(),
        })))
    }

    fn requests(&self) -> Vec<Vec<u8>> {
        lock(&self.0).requests.clone()
    }

    fn page(&self) -> Vec<u8> {
        lock(&self.0).page.clone()
    }
}

impl RecordingState {
    fn number(&self, p: &[u8], at: usize) -> usize {
        if self.big_endian {
            (usize::from(p[at]) << 8) | usize::from(p[at + 1])
        } else {
            usize::from(p[at]) | (usize::from(p[at + 1]) << 8)
        }
    }

    fn answer(&mut self, p: &[u8]) -> Vec<u8> {
        // Every command here carries the CAN id and the page byte before its arguments,
        // exactly as the firmware's templates write them.
        match p[0] as char {
            'w' => {
                let (offset, count) = (self.number(p, 3), self.number(p, 5));

                if offset + count <= self.page.len() && p.len() >= 7 + count {
                    self.page[offset..offset + count].copy_from_slice(&p[7..7 + count]);
                }

                reply_framed(0x00, &[])
            }

            'r' => {
                let (offset, count) = (self.number(p, 3), self.number(p, 5));

                if offset + count <= self.page.len() {
                    reply_framed(0x00, &self.page[offset..offset + count])
                } else {
                    reply_framed(0x84, &[])
                }
            }

            'b' => reply_framed(0x00, &[]),

            _ => reply_framed(0x83, &[]),
        }
    }
}

impl EcuTransport for RecordingEcu {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut state = lock(&self.0);
        if data.len() < 7 {
            state.pending = Vec::new();
            return Ok(());
        }

        let payload = payload_of(data);
        state.requests.push(payload.clone());
        state.pending = state.answer(&payload);
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(take_pending(&mut lock(&self.0).pending, buffer))
    }

    fn discard_input(&mut self) {}
}

fn protocol_message(error: &EcuError) -> String {
    assert!(error.is_protocol(), "expected an EcuProtocolException, got {error:?}");
    error.message()
}

// ===== EcuWriteTests ===============================================================

// ----- the round trip ---------------------------------------------------

#[test]
fn what_is_written_can_be_read_back() {
    let board = WritableEcu::new(vec![0u8; 256]);
    let connection = EcuConnection::new(Box::new(board.clone()), quick());

    let data: Vec<u8> = (0..16).map(|i| (i * 3) as u8).collect();
    connection.write_tune_page(&page(), 64, true, 0, &data, 0).unwrap();

    assert_eq!(board.page()[..16], data[..]);
}

#[test]
fn a_write_that_lands_in_the_wrong_place_is_caught() {
    // The failure that matters. A board that acknowledges the command and puts the bytes
    // somewhere else leaves an engine running on numbers nobody chose, and the acknowledgement
    // alone cannot tell you.
    let board = WritableEcu::new(vec![0u8; 256]).with_skew(4);
    let connection = EcuConnection::new(Box::new(board), quick());

    let error = connection
        .write_tune_page(&page(), 64, true, 0, &[1, 2, 3, 4, 5, 6, 7, 8], 0)
        .unwrap_err();

    assert!(protocol_message(&error).contains("did not take that write"));
}

#[test]
fn a_write_that_is_acknowledged_and_discarded_is_caught() {
    let board = WritableEcu::new(vec![0u8; 256]).discarding();
    let connection = EcuConnection::new(Box::new(board), quick());

    let error = connection
        .write_tune_page(&page(), 64, true, 0, &[9, 9, 9, 9], 0)
        .unwrap_err();

    assert!(error.is_protocol());
}

#[test]
fn the_failure_says_nothing_has_been_burned() {
    // Which is the thing to know: a power cycle undoes it.
    let board = WritableEcu::new(vec![0u8; 256]).discarding();
    let connection = EcuConnection::new(Box::new(board.clone()), quick());

    let error = connection
        .write_tune_page(&page(), 64, true, 0, &[9, 9, 9, 9], 0)
        .unwrap_err();

    assert!(protocol_message(&error).contains("burned"));
    assert_eq!(board.burns(), 0);
}

#[test]
fn a_write_is_split_to_fit_the_blocking_factor() {
    let board = WritableEcu::new(vec![0u8; 256]);
    let connection = EcuConnection::new(Box::new(board.clone()), quick());

    connection
        .write_tune_page(&page(), 64, true, 0, &[0u8; 200], 0)
        .unwrap();

    let writes = board.writes();
    assert!(writes.iter().all(|&(_, count)| count <= 64 - 8));
    assert_eq!(writes.iter().map(|&(_, count)| count).sum::<usize>(), 200);
}

#[test]
fn a_write_running_past_the_end_of_the_page_is_refused_before_it_is_sent() {
    let board = WritableEcu::new(vec![0u8; 256]);
    let connection = EcuConnection::new(Box::new(board.clone()), quick());

    let error = connection
        .write_tune_page(&page(), 64, true, 250, &[0u8; 16], 0)
        .unwrap_err();

    assert!(error.is_protocol());
    assert!(board.writes().is_empty());
}

// ----- burning ----------------------------------------------------------

#[test]
fn writing_does_not_burn() {
    // Everything up to a burn is undone by turning the key off, and that is worth keeping true.
    let board = WritableEcu::new(vec![0u8; 256]);
    let connection = EcuConnection::new(Box::new(board.clone()), quick());

    connection
        .write_tune_page(&page(), 64, true, 0, &[1, 2, 3, 4], 0)
        .unwrap();

    assert_eq!(board.burns(), 0);
}

#[test]
fn burning_is_its_own_call() {
    let board = WritableEcu::new(vec![0u8; 256]);
    let connection = EcuConnection::new(Box::new(board.clone()), quick());

    connection.burn_page(&page(), true, 0).unwrap();

    assert_eq!(board.burns(), 1);
}

#[test]
fn a_firmware_declaring_no_write_command_cannot_be_written_to() {
    let read_only = "[Constants]\nnPages = 1\npageSize = 64\npageReadCommand = \"R%2o%2c\"\n";

    let no_write = tune_layout::read(read_only, None).pages[0].clone();

    let board = WritableEcu::new(vec![0u8; 256]);
    let connection = EcuConnection::new(Box::new(board.clone()), quick());

    let error = connection
        .write_tune_page(&no_write, 64, true, 0, &[1, 2], 0)
        .unwrap_err();

    assert!(protocol_message(&error).contains("no write command"));
    assert!(board.writes().is_empty());
}

// ----- late replies -----------------------------------------------------

#[test]
fn a_reply_of_the_wrong_length_is_not_taken_as_the_answer() {
    // The failure this guards against was reported from a Bluetooth rig: a straggler matched to
    // the next request decoded at the wrong offsets and showed a stationary car doing 230 km/h.
    // Well-formed, checksummed, and entirely wrong.
    let transport = LateTransport::new(reply_framed(0, &[0u8; 200]), reply_framed(0, &[0u8; 16]));

    // Three attempts: the first times out, the second collects its late reply and refuses it,
    // the third is answered properly.
    let connection = EcuConnection::new(
        Box::new(transport),
        EcuConnectionSettings {
            retries: 2,
            timeout: Duration::from_millis(20),
            settle_for: Duration::from_millis(20),
            quiet_for: Duration::from_millis(1),
            ..EcuConnectionSettings::default()
        },
    );

    connection.use_layout(
        &msq_ini::read_output_channels(
            "[Constants]\nendianness = little\n\n[OutputChannels]\nochGetCommand = \"R%2o%2c\"\nochBlockSize  = 16\n",
            None,
        ),
        false,
    ).expect("layout accepted");

    // The 200-byte straggler is refused, and the 16 that follows is right.
    assert_eq!(connection.read_realtime(16).unwrap().len(), 16);
    assert_eq!(connection.retries(), 2);
}

#[test]
fn the_link_is_drained_before_anything_is_sent_again() {
    // rusEFI's command handler has no queue: a request arriving while a reply is still being
    // written desynchronises it permanently, and only a power cycle brings the ECU back. Never
    // more than one request outstanding.
    let transport = CountingSettle::default();

    let connection = EcuConnection::new(
        Box::new(transport.clone()),
        EcuConnectionSettings {
            retries: 1,
            timeout: Duration::from_millis(5),
            settle_for: Duration::from_millis(60),
            quiet_for: Duration::from_millis(5),
            ..EcuConnectionSettings::default()
        },
    );

    let error = connection.read_signature().unwrap_err();
    assert!(error.is_protocol());

    // Drained after the failure, before the retry went out.
    assert!(
        lock(&transport.0).drained_before_second_write,
        "the retry was sent without draining first"
    );
}

// ----- the same method TunerStudio uses -----------------------------------

/// MS2Extra's own declarations, copied from the file the MicroSquirt matched. Big-endian, the CAN
/// id and the page byte as literals in every command, and the two timings the firmware asks for.
const MEGASQUIRT_TEMPLATE: &str = r#"[Constants]
endianness          = big
nPages              = 1
pageSize            = 1024
pageIdentifier      = "\$tsCanId{P}"
pageReadCommand     = "r\$tsCanId{P}%2o%2c"
pageChunkWrite      = "w\$tsCanId{P}%2o%2c%v"
burnCommand         = "b\$tsCanId{P}"
blockingFactor      = 256
interWriteDelay     = 1
pageActivationDelay = 10

page = 1
veTable = array, U08, 0, [4x4], "%", 1, 0, 0, 255, 0
"#;

/// The INI with the page byte the file really carries: a raw 0x04 follows each `\$tsCanId`, which
/// is the page number written as a literal (the C# raw string holds the control character
/// itself, which does not survive being copied as text).
fn megasquirt() -> String {
    MEGASQUIRT_TEMPLATE.replace("{P}", "\u{4}")
}

#[test]
fn a_write_is_the_bytes_the_firmware_declares() {
    // The whole of the question "is this the same method": the request on the wire has to be
    // what the .ini says it is, byte for byte.
    //
    //   w  <canId>  0x04  <offset:2 big-endian>  <count:2 big-endian>  <data>
    let layout = tune_layout::read(&megasquirt(), None);
    let ecu = RecordingEcu::new(vec![0u8; 1024], true);

    let connection = EcuConnection::new(Box::new(ecu.clone()), quick());
    connection.open().unwrap();

    connection
        .write_tune_page(
            &layout.pages[0],
            layout.blocking_factor,
            layout.little_endian,
            0x0102,
            &[0x2A, 0x2B],
            layout.inter_write_delay,
        )
        .unwrap();

    let sent = &ecu.requests()[0];

    assert_eq!(sent, &[b'w', 0x00, 0x04, 0x01, 0x02, 0x00, 0x02, 0x2A, 0x2B]);
}

#[test]
fn a_burn_is_the_bytes_the_firmware_declares() {
    let layout = tune_layout::read(&megasquirt(), None);
    let ecu = RecordingEcu::new(vec![0u8; 1024], true);

    let connection = EcuConnection::new(Box::new(ecu.clone()), quick());
    connection.open().unwrap();

    connection.burn_page(&layout.pages[0], layout.little_endian, 0).unwrap();

    assert_eq!(ecu.requests().last().unwrap(), &[b'b', 0x00, 0x04]);
}

#[test]
fn a_write_larger_than_the_blocking_factor_is_split_and_still_lands() {
    // Never more than the firmware allows in one message; a rusEFI asked for more leaves the bus
    // until it is replugged.
    let layout = tune_layout::read(&megasquirt(), None);
    let ecu = RecordingEcu::new(vec![0u8; 1024], true);

    let data: Vec<u8> = (0..600).map(|i| (i & 0xFF) as u8).collect();

    let connection = EcuConnection::new(Box::new(ecu.clone()), quick());
    connection.open().unwrap();
    connection
        .write_tune_page(
            &layout.pages[0],
            layout.blocking_factor,
            layout.little_endian,
            0,
            &data,
            0,
        )
        .unwrap();

    let requests = ecu.requests();
    assert!(requests.len() > 1, "a 600-byte write went out in one message");
    for request in requests.iter().filter(|r| r[0] == b'w') {
        assert!(
            request.len() as i32 <= layout.blocking_factor,
            "a message was {} bytes",
            request.len()
        );
    }

    assert_eq!(ecu.page()[..data.len()], data[..]);
}

// ===== EcuBurnReplyTests ===========================================================

/// The burn path, as a rusEFI actually behaves rather than as a fake was written to behave.
///
/// Every test in that class passed while all three of these were broken, because the fake
/// answered a burn with 0x00 and never stalled. A real board answers 0x04 and stops servicing USB
/// while it writes flash.
struct BoardState {
    burn_status: u8,
    pending: Vec<u8>,
    burns: usize,
    /// Set to make the port refuse the bytes, as one does mid-erase.
    refuse_writes: bool,
    open: bool,
}

/// A transport answering a burn the way the hardware does.
#[derive(Clone)]
struct Board(Arc<Mutex<BoardState>>);

impl Board {
    fn new(burn_status: u8) -> Self {
        Self(Arc::new(Mutex::new(BoardState {
            burn_status,
            pending: Vec::new(),
            burns: 0,
            refuse_writes: false,
            open: false,
        })))
    }

    fn burns(&self) -> usize {
        lock(&self.0).burns
    }

    fn refuse_writes(&self, refuse: bool) {
        lock(&self.0).refuse_writes = refuse;
    }
}

impl EcuTransport for Board {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut state = lock(&self.0);
        if state.refuse_writes {
            return Err(Error::Io(std::io::Error::other(
                "The semaphore timeout period has expired. : 'COM8'.",
            )));
        }

        let payload = payload_of(data);

        if payload.first() == Some(&b'B') {
            state.burns += 1;
            state.pending = reply_framed(state.burn_status, &[]);
            return Ok(());
        }

        state.pending = reply_framed(0x00, &[]);
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(take_pending(&mut lock(&self.0).pending, buffer))
    }

    fn discard_input(&mut self) {
        lock(&self.0).pending = Vec::new();
    }
}

fn burn_page() -> TunePage {
    TunePage {
        index: 0,
        size: 64,
        identifier: "\"\u{1}\"".to_string(),
        read_command: "\"R%2o%2c\"".to_string(),
        chunk_write_command: "\"C%2o%2c%v\"".to_string(),
        burn_command: "\"B\"".to_string(),
    }
}

#[test]
fn every_way_of_saying_yes_is_taken_for_a_yes() {
    // Insisting on 0x00 made a successful burn read as a refusal: the flash was written, the
    // board answered 0x04 to say so, and the application reported that the burn had been
    // declined.
    //
    // Not one firmware's quirk. Asked the same question directly, a Speeduino 2025.01.7 answers
    // 0x00 to a page read and 0x04 to a burn, exactly as a rusEFI does, so every burn either of
    // them was ever asked for came back looking refused.
    for status in [
        0x00, // a plain acknowledgement, which is what a page read gets
        0x04, // TS_RESPONSE_BURN_OK, measured off both a rusEFI and a Speeduino
        0x07, // and what a controller command is acknowledged with
    ] {
        let board = Board::new(status);
        let connection = EcuConnection::with_defaults(Box::new(board.clone()));
        connection.open().unwrap();

        connection
            .burn_page(&burn_page(), true, 0)
            .unwrap_or_else(|e| panic!("status 0x{status:02X} was refused: {e}"));

        assert_eq!(board.burns(), 1, "status 0x{status:02X}");
    }
}

#[test]
fn and_every_way_of_saying_no_still_means_no() {
    for status in [
        0x80, // underrun
        0x82, // CRC failure
        0x83, // unrecognised command
        0x84, // out of range
        0x8D, // framing error
    ] {
        let connection = EcuConnection::with_defaults(Box::new(Board::new(status)));
        connection.open().unwrap();

        let error = connection
            .burn_page(&burn_page(), true, 0)
            .expect_err(&format!("status 0x{status:02X} was taken for a yes"));

        assert!(
            protocol_message(&error).contains(&format!("0x{status:02X}")),
            "status 0x{status:02X}: {error}"
        );
    }
}

#[test]
fn a_port_that_will_not_take_the_bytes_is_reported_rather_than_thrown_raw() {
    // The transport raises an IOException where the port misbehaves rather than the ECU. Catching
    // only EcuProtocolException let it escape the retry loop and reach the application as an
    // unhandled "The semaphore timeout period has expired": a crash, and a misleading one, since
    // the burn it was raised for had gone through.
    let board = Board::new(0x04);
    board.refuse_writes(true);
    let connection = EcuConnection::with_defaults(Box::new(board.clone()));
    board.refuse_writes(false);
    connection.open().unwrap();
    board.refuse_writes(true);

    let error = connection.burn_page(&burn_page(), true, 0).unwrap_err();

    assert!(error.is_protocol(), "the raw I/O error escaped: {error:?}");
}

struct SilentState {
    burns: usize,
    write_timeout: Duration,
    open: bool,
}

/// A board that takes a burn and never answers it.
#[derive(Clone)]
struct SilentBoard(Arc<Mutex<SilentState>>);

impl SilentBoard {
    fn new() -> Self {
        Self(Arc::new(Mutex::new(SilentState {
            burns: 0,
            write_timeout: olv_transport_default_write_timeout(),
            open: false,
        })))
    }
}

/// `SerialEcuTransport.DefaultWriteTimeout`.
fn olv_transport_default_write_timeout() -> Duration {
    olv_transport::transport::serial_ecu_transport::DEFAULT_WRITE_TIMEOUT
}

impl EcuTransport for SilentBoard {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write_timeout(&self) -> Duration {
        lock(&self.0).write_timeout
    }

    fn set_write_timeout(&mut self, timeout: Duration) {
        lock(&self.0).write_timeout = timeout;
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        if payload_of(data).first() == Some(&b'B') {
            lock(&self.0).burns += 1;
        }
        Ok(())
    }

    fn read(&mut self, _buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(0)
    }

    fn discard_input(&mut self) {}
}

#[test]
fn a_burn_that_cannot_be_confirmed_says_it_may_still_have_happened() {
    // The one message worth getting right. A burn reported as failed sends somebody to burn
    // again, and a controller stops answering while it writes flash, which looks identical to
    // one that never heard. Silence is the case this is about; a refusal is not, and is tested
    // separately.
    let board = SilentBoard::new();
    let connection = EcuConnection::with_defaults(Box::new(board));
    connection.open().unwrap();

    let error = connection.burn_page(&burn_page(), true, 0).unwrap_err();

    assert!(!error.refused());
    assert!(protocol_message(&error).contains("may still have completed"));
}

#[test]
fn a_burn_is_never_sent_twice() {
    // Every other request may be repeated freely, because a lost reply is just a lost reply. A
    // burn is the one where the ECU may well have done the work and gone quiet doing it, and
    // re-sending spends a flash erase to learn nothing.
    let board = SilentBoard::new();
    let connection = EcuConnection::with_defaults(Box::new(board.clone()));
    connection.open().unwrap();

    assert!(connection.burn_page(&burn_page(), true, 0).is_err());
    assert_eq!(lock(&board.0).burns, 1);
}

// ----- the allowance a burn gets ------------------------------------------

struct SlowState {
    erase: Duration,
    pending: Vec<u8>,
    answers_at: Option<Instant>,
    write_timeout: Duration,
    /// The longest a write was ever allowed while one was outstanding.
    widest_seen: Duration,
    open: bool,
}

/// A transport that answers only after the erase, and stalls writes meanwhile.
#[derive(Clone)]
struct SlowBoard(Arc<Mutex<SlowState>>);

impl SlowBoard {
    fn new(erase: Duration) -> Self {
        Self(Arc::new(Mutex::new(SlowState {
            erase,
            pending: Vec::new(),
            answers_at: None,
            write_timeout: olv_transport_default_write_timeout(),
            widest_seen: Duration::ZERO,
            open: false,
        })))
    }
}

impl EcuTransport for SlowBoard {
    fn is_open(&self) -> bool {
        lock(&self.0).open
    }

    fn open(&mut self) -> Result<()> {
        lock(&self.0).open = true;
        Ok(())
    }

    fn close(&mut self) {
        lock(&self.0).open = false;
    }

    fn write_timeout(&self) -> Duration {
        lock(&self.0).write_timeout
    }

    fn set_write_timeout(&mut self, timeout: Duration) {
        lock(&self.0).write_timeout = timeout;
    }

    fn write(&mut self, _data: &[u8]) -> Result<()> {
        let mut state = lock(&self.0);
        if state.write_timeout > state.widest_seen {
            state.widest_seen = state.write_timeout;
        }

        // A port that will not take the bytes for as long as the erase runs.
        if state.write_timeout < state.erase {
            return Err(Error::Io(std::io::Error::other(
                "The semaphore timeout period has expired.",
            )));
        }

        state.pending = ms_protocol::frame(&[0x04]);
        state.answers_at = Some(Instant::now() + state.erase);
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        // Nothing comes back until the erase is done, and only if the caller is prepared to wait
        // that long.
        let answers_at = {
            let state = lock(&self.0);
            match state.answers_at {
                Some(at) if Instant::now() + timeout >= at => at,
                _ => return Ok(0),
            }
        };

        while Instant::now() < answers_at {
            thread::sleep(Duration::from_millis(5));
        }

        Ok(take_pending(&mut lock(&self.0).pending, buffer))
    }

    fn discard_input(&mut self) {
        lock(&self.0).pending = Vec::new();
    }
}

#[test]
fn a_burn_is_given_longer_than_everything_else_is() {
    // Sending a burn once rather than retrying took away the time the retry loop used to buy. A
    // controller that erases before it answers would then miss the ordinary 500 ms window every
    // single time, and a burn that worked would be reported as unconfirmed on every attempt.
    let board = SlowBoard::new(Duration::from_millis(900));
    let connection = EcuConnection::with_defaults(Box::new(board));
    connection.open().unwrap();

    connection.burn_page(&burn_page(), true, 0).unwrap();
}

#[test]
fn and_the_long_allowance_is_put_back_afterwards() {
    // The port is shared with everything else on the link, and everything else should fail fast:
    // Windows' incoming Bluetooth port never accepts a write at all, and waiting five seconds on
    // each one hangs the window through the whole identify sequence.
    let board = SlowBoard::new(Duration::from_millis(50));
    let connection = EcuConnection::with_defaults(Box::new(board.clone()));
    connection.open().unwrap();

    let before = lock(&board.0).write_timeout;
    connection.burn_page(&burn_page(), true, 0).unwrap();

    let state = lock(&board.0);
    assert_eq!(state.write_timeout, before);
    assert!(
        state.widest_seen >= Duration::from_secs(1),
        "the burn should have widened it, but the widest seen was {:?}",
        state.widest_seen
    );
}

#[test]
fn a_refusal_is_left_to_speak_for_itself() {
    // "It may still have completed: turn the ignition off and on" is the right thing to say
    // about silence and the wrong thing to say about a controller that answered. 0x83 means it
    // did not recognise the burn command, so nothing was burned and nobody should be sent to the
    // car.
    let board = Board::new(0x83);
    let connection = EcuConnection::with_defaults(Box::new(board));
    connection.open().unwrap();

    let error = connection.burn_page(&burn_page(), true, 0).unwrap_err();

    assert!(error.refused());
    assert!(!protocol_message(&error).contains("may still have completed"));
}
