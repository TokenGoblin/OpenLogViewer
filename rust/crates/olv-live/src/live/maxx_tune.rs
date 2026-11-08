//! Ported from `MaxxTune.cs`. Owned by package `w4-live`.
//!
//! A MaxxECU's tune, read off the ECU over USB and shaped into the same [`TuneLayout`]
//! everything else here already works on.
//!
//! The shaping is the point. A MaxxECU has no INI and its tune is not paged, so the obvious
//! approach would be a MaxxECU-shaped tune object beside the existing one, and then a
//! MaxxECU-shaped table view, compare, export and edit beside each of those. Handing back a
//! layout instead means the tables, the heat colouring, the curve editor, comparing against a
//! file and everything else are the code that was already there, and a MaxxECU is simply
//! another ECU with a tune in it.
//!
//! What the blob is, and how it is read, is in `MAXXECU_USB_PROTOCOL_RECOVERED.md` and
//! `MAXXECU_TUNE_BLOB_DECODING.md` in the TCU repository. The short of it: command `0x05`
//! returns all 64 KB, 256 bytes at a time; settings sit at the addresses MTune's definitions
//! give; and each table's real shape is in a 21-byte config struct at the address the
//! definitions give for it, which points at a record holding the axes and the cells.
//!
//! **Writes are live and permanent.** There is no burn step and so no undo. Every guard on a
//! write lives in [`write`]: the offset must not be negative, the payload must be 1 to
//! [`MAXIMUM_WRITE`] bytes, and `offset + length` must not pass [`WRITE_CEILING`]. The command
//! byte is always [`READ_TUNE`] (0x05); command 0x2F, which erases the ECU's flash, is refused
//! by `olv_protocol::ms::maxx_usb_protocol::request` and nothing here can reach it.

use std::collections::HashMap;
use std::time::Duration;

use olv_core::ini::msq_ini::RealtimeType;
use olv_core::ini::table_editor_reader::TableDefinition;
use olv_core::ini::tune_dialogs::{DialogItem, DialogItemKind, MenuEntry, TuneDialog, TuneInterface, TuneMenu};
use olv_core::ini::tune_layout::{TuneConstant, TuneLayout, TunePage};
use olv_core::logs::maxx_channel_definitions::{self, MaxxChannelDefinition};
use olv_core::model::IgnoreCaseMap;
use olv_core::{Error, Result};
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;
use olv_protocol::ms::ms_protocol::EcuTransport;

use super::maxx_tune_definitions::MaxxSettingDefinition;

/// One table as the ECU currently has it: where its axes and cells are, and how big they are
/// right now.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MaxxTable {
    /// MTune's name for it, with the trailing "data" taken off.
    pub name: String,
    /// Cells across, which is the X axis length.
    pub columns: usize,
    /// Cells down.
    pub rows: usize,
    /// The channel id the ECU indexes the columns by.
    pub x_channel: i32,
    /// The same for the rows.
    pub y_channel: i32,
    /// Offset of the first X breakpoint in the blob.
    pub x_axis_at: usize,
    /// Offset of the first Y breakpoint.
    pub y_axis_at: usize,
    /// Offset of the first cell.
    pub cells_at: usize,
}

/// A MaxxECU's tune, as everything above the protocol needs it.
///
/// The four parts come out of one pass because they all have to agree about what things are
/// called; see [`build`].
#[derive(Clone, Debug)]
pub struct MaxxTuneModel {
    /// The tune model's view of it: every constant and its place.
    pub layout: TuneLayout,
    /// Which constants make up each table, for the table editor.
    pub tables: Vec<TableDefinition>,
    /// The tables as the ECU holds them: where the cells are, and how big. This is what a
    /// write needs, and it carries the same names the layout uses.
    pub maps: Vec<MaxxTable>,
    /// Somewhere to show the settings. Invented here rather than read from the firmware,
    /// which describes none.
    pub pages: TuneInterface,
}

/// What the ECU said about a write. The numbers are the firmware's own status bytes, so an
/// unfamiliar one can be reported as itself rather than as "failed".
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MaxxWriteStatus {
    /// It took the bytes (0x80).
    Ok,

    /// Nothing came back, and nothing had been sent yet (0).
    ///
    /// The header goes out first and is acknowledged on its own, so silence there means the
    /// payload never left. The tune is untouched.
    NoAnswer,

    /// The payload went out and was never acknowledged, so **whether it landed is not known**
    /// (1).
    ///
    /// This is the one status that must not be reported as a failure. The ECU applies a write
    /// as it arrives and saves it itself; an acknowledgement lost on the way back looks
    /// exactly like a write that never happened, and the difference is a tune that is
    /// permanently changed. Anything receiving this has to go and look ([`checksums`] is the
    /// cheap way) before telling anybody what happened.
    Uncertain,

    /// The command was not recognised (0x20). Also what the ECU answers a write to a command
    /// that does not exist, which is how MTune's handshake `0x3B` was found not to be a
    /// command at all.
    UnknownCommand,

    /// The offset or the length is out of range (0x30).
    OutOfRange,

    /// Refused: something else is writing (0x40).
    WriterBusy,

    /// Refused: something else is reading (0x60).
    ReaderBusy,

    /// Refused: the flash path is busy (0x61).
    FlashBusy,

    /// Any other byte the ECU answered, including 0 and 1, which only the local statuses use.
    Other(u8),
}

impl MaxxWriteStatus {
    /// The status an ECU answered with: the named value where there is one, the byte itself
    /// where there is not.
    ///
    /// 0 and 1 are the *local* statuses [`NoAnswer`](Self::NoAnswer) and
    /// [`Uncertain`](Self::Uncertain), made when nothing came back, and are never what an ECU
    /// said. The C# cast the byte to the enum, so an ECU that answered a bare 0 or 1 read as
    /// "nothing was sent, the tune is untouched" or "the write may have landed"; here they are
    /// [`Other`](Self::Other), an unfamiliar answer, which is a refusal.
    pub fn from_byte(byte: u8) -> Self {
        match byte {
            0x80 => Self::Ok,
            0x20 => Self::UnknownCommand,
            0x30 => Self::OutOfRange,
            0x40 => Self::WriterBusy,
            0x60 => Self::ReaderBusy,
            0x61 => Self::FlashBusy,
            other => Self::Other(other),
        }
    }

    /// The enum's numeric value.
    pub fn value(self) -> u8 {
        match self {
            Self::Ok => 0x80,
            Self::NoAnswer => 0,
            Self::Uncertain => 1,
            Self::UnknownCommand => 0x20,
            Self::OutOfRange => 0x30,
            Self::WriterBusy => 0x40,
            Self::ReaderBusy => 0x60,
            Self::FlashBusy => 0x61,
            Self::Other(byte) => byte,
        }
    }
}

/// The command that reads the tune.
pub const READ_TUNE: u8 = 0x05;

/// The longest read it will answer.
///
/// Not the protocol's 512. A 512-byte read of this command is refused outright, which is
/// indistinguishable from the command not existing, and is why the tune looked unreachable
/// for a while.
pub const PIECE: usize = 256;

/// How much of it there is.
pub const BLOB_SIZE: usize = 65536;

/// Where table data begins, relative to the blob.
///
/// The firmware's table-data base minus its tune base: 0x20010D51 - 0x2000C974. A table's
/// `data offset` is counted from here.
pub const TABLE_DATA: usize = 0x43DD;

/// Bytes between a table's axes and its cells: two zero bytes, a u16 whose meaning is not
/// known, a CRC-32 over everything before it, and one byte that belongs to the checksum after
/// the cells.
///
/// The single most important number here. Walk straight from the axes into the grid (which is
/// what the firmware's evaluator reads like) and every cell is out by four and a half cells,
/// which does not look wrong. It looks like a VE table with implausible numbers in it.
pub const PREAMBLE: usize = 9;

/// The highest offset the ECU will write to. Above this it answers 0x30.
pub const WRITE_CEILING: i32 = 0xF2E7;

/// The most the ECU will take in one write.
///
/// The firmware refuses a payload of 531 or more outright. This stays at the 256 the read
/// uses, because a write is the half with no undo and there is nothing to be gained by being
/// close to a limit.
pub const MAXIMUM_WRITE: usize = 256;

/// How much of the tune the ECU's own checksums cover, in one 4 KB piece each.
pub const CHECKSUM_CHUNK: usize = 4096;

/// Bytes a `dynamicTable` entry's config struct occupies at its own address; not the cells,
/// which live elsewhere in the blob and are found through the struct rather than stored where
/// it is. Shared with [`MaxxSettingDefinition::size`], which is what a table actually needs
/// to advance the definitions file's implicit-address cursor past: the struct that is really
/// there, not the cell grid.
pub const TABLE_CONFIG_SIZE: usize = 21;

/// The reply timeout every tune exchange uses.
const TIMEOUT: Duration = Duration::from_millis(600);

/// The one page a MaxxECU has, which is the whole blob.
///
/// The identifier and the command templates are empty because they are TunerStudio's way of
/// asking for a page and a MaxxECU is not asked that way: [`read`] does the asking. The burn
/// command is empty for a better reason: this ECU has no burn. A write to it is applied to the
/// running tune and persisted by the ECU itself in one step, so there is nothing to send
/// afterwards and nothing to withhold.
pub fn page() -> TunePage {
    TunePage {
        index: 0,
        size: BLOB_SIZE as i32,
        identifier: String::new(),
        read_command: String::new(),
        chunk_write_command: String::new(),
        burn_command: String::new(),
    }
}

/// `ArgumentOutOfRangeException(param, message)`: .NET appends the parameter name.
fn out_of_range(param: &str, message: &str) -> Error {
    Error::Argument(format!("{message} (Parameter '{param}')"))
}

/// Reads the whole blob.
///
/// Against a bench Race this takes about four seconds and comes back identically on a second
/// read, which is how it was told apart from the regions that look like settings and are live
/// data.
///
/// `progress` is called with bytes read and bytes total. `on_retry` is called before each
/// attempt past the first, for a caller keeping its own count: `MaxxUsbSource` folds these
/// into the same retries its telemetry and configuration requests count against, so a flaky
/// tune read shows up the same way a flaky poll does rather than going unseen.
pub fn read(
    transport: &mut dyn EcuTransport,
    mut progress: Option<&mut dyn FnMut(i32, i32)>,
    mut on_retry: Option<&mut dyn FnMut()>,
) -> Result<Vec<u8>> {
    let mut blob = vec![0u8; BLOB_SIZE];
    let mut reply = vec![0u8; usb::reply_length(PIECE as i32) as usize];

    let mut at = 0;
    while at < BLOB_SIZE {
        let request = usb::request(usb::READ, READ_TUNE, at as i32, PIECE as i32)?;

        let retry = on_retry.as_mut().map(|r| &mut **r as &mut dyn FnMut());
        let Some(piece) = usb::try_ask(transport, &request, &mut reply, PIECE as i32, TIMEOUT, 3, retry)? else {
            // `try_ask` leaves the last attempt's late reply in the input; the next request would read
            // its answer starting in the middle of it.
            transport.discard_input();

            return Err(Error::Protocol(format!(
                "The MaxxECU stopped answering {PIECE} bytes into its tune at offset {at}. \
                 Nothing has been changed on the ECU \u{2014} this is a read."
            )));
        };

        blob[at..at + PIECE].copy_from_slice(&piece);

        if let Some(progress) = progress.as_mut() {
            progress((at + PIECE) as i32, BLOB_SIZE as i32);
        }

        at += PIECE;
    }

    Ok(blob)
}

/// Sends a run of bytes into the ECU's tune.
///
/// **There is no burn, and so there is no undo.** Every other controller this program writes
/// to follows the MegaSquirt convention, where a write lands in working memory and is lost at
/// the next power cycle unless it is burned, which makes turning the key off the way out of a
/// mistake. A MaxxECU does not work that way: the write goes into the running tune with
/// interrupts disabled and the ECU queues its own persist to an external store, so it is
/// applied and permanent in one step. Anything calling this must have read the range first
/// and kept the original bytes, because the ECU will not keep them and nothing else will
/// either.
///
/// A write is two exchanges. The header goes out and is acknowledged on its own; only then
/// does the payload follow, with its own checksum over it. The ECU answers each with a status
/// byte; see [`MaxxWriteStatus`].
///
/// Recovered from the firmware rather than from the wire: no capture of MTune writing a tune
/// exists, so the command and the field order here are what the 1.151 dispatcher reads,
/// cross-checked against the two writes MTune's connect handshake does send. See
/// `MAXXECU_USB_PROTOCOL_RECOVERED.md`. (Hardware has since proven `dir=1 cmd=0x05` live.)
///
/// The offset goes where a read's offset goes, which was not obvious and is now settled. The
/// dispatcher appears to take the offset from byte 4 and the length from byte 2 (backwards)
/// because it is not handed the packet at all: the packet handler copies the header into a
/// state struct whose `+0x04` offset is filled from wire bytes 2-3 and whose `+0x02` length
/// comes from bytes 4-5. Read and write then address one and the same buffer, so the
/// addresses in MTune's definitions are write addresses too.
///
/// Refused before anything is sent (`Error::Argument`, the C#'s `ArgumentOutOfRange`): a
/// negative `offset`, an empty `data`, more than [`MAXIMUM_WRITE`] bytes of it, and any write
/// that would reach past [`WRITE_CEILING`].
pub fn write(transport: &mut dyn EcuTransport, offset: i32, data: &[u8]) -> Result<MaxxWriteStatus> {
    if offset < 0 {
        return Err(Error::Argument(format!(
            "offset ('{offset}') must be a non-negative value. (Parameter 'offset')\nActual value was {offset}."
        )));
    }
    if data.is_empty() {
        return Err(Error::Argument(
            "data.Length ('0') must be a non-zero value. (Parameter 'data.Length')\nActual value was 0.".to_string(),
        ));
    }
    if data.len() > MAXIMUM_WRITE {
        return Err(Error::Argument(format!(
            "data.Length ('{}') must be less than or equal to '{MAXIMUM_WRITE}'. \
             (Parameter 'data.Length')\nActual value was {}.",
            data.len(),
            data.len()
        )));
    }

    // Widened, so an offset near i32::MAX cannot wrap negative and slip past the ceiling (the
    // C#'s int sum would; its request builder refused such an offset afterwards).
    let reach = i64::from(offset) + data.len() as i64;
    if reach > i64::from(WRITE_CEILING) {
        return Err(out_of_range(
            "offset",
            &format!("A MaxxECU will not be written above {WRITE_CEILING}, and this would reach {reach}."),
        ));
    }

    let mut status = [0u8; 1];

    // Start from a quiet line, under the same hold on the cable as the exchange itself.
    //
    // The header's acknowledgement and the payload's are the same bare status byte, with no
    // framing, no length and no checksum, so a reply cannot be told which of them it is. A late
    // reply left over from an earlier exchange (a telemetry reply whose first byte is 0x80, say)
    // would be read as the header's acknowledgement; the real one would then be taken as the
    // payload's, and a payload the ECU refused would be reported as written. The only defence is
    // that nothing stale is in the input when each acknowledgement is waited for, so it is
    // cleared before the header goes out and again before the payload does. (The C# did neither;
    // the shipping app has the defect.) Whatever lands after the payload is sent still cannot be
    // told apart, which is why a caller confirms a write with `verify` and not with this status
    // alone.
    transport.discard_input();

    transport.write(&usb::request(usb::WRITE, READ_TUNE, offset, data.len() as i32)?)?;

    if transport.read(&mut status, TIMEOUT)? != 1 {
        return Ok(MaxxWriteStatus::NoAnswer);
    }
    if status[0] != usb::OK {
        return Ok(MaxxWriteStatus::from_byte(status[0]));
    }

    transport.discard_input();

    // The payload and its own checksum, which the ECU checks before it takes any of it. Sent
    // as one write so the two cannot be separated on the wire by anything that reads from the
    // same device.
    let mut payload = Vec::with_capacity(data.len() + 4);
    payload.extend_from_slice(data);
    payload.extend_from_slice(&maxx_protocol::crc32(data).to_le_bytes());

    transport.write(&payload)?;

    // From here the bytes are gone. A missing acknowledgement says nothing about whether the
    // ECU took them, and on this controller "took them" means applied and saved, so this is
    // Uncertain and not a failure.
    if transport.read(&mut status, TIMEOUT)? != 1 {
        return Ok(MaxxWriteStatus::Uncertain);
    }

    Ok(if status[0] == usb::OK {
        MaxxWriteStatus::Ok
    } else {
        MaxxWriteStatus::from_byte(status[0])
    })
}

/// Builds the bytes that put a table's cells into the ECU: the grid, and the checksum the
/// firmware checks it against.
///
/// A cell cannot be written on its own. Each table record carries a CRC-32 over its cells that
/// the firmware verifies every time it evaluates the table, so cells written without it are
/// cells the ECU has been told not to trust. The checksum covers the pad byte in front of the
/// grid as well as the grid itself, which is why the blob is needed here and not just the new
/// values.
///
/// The result is written in one go at [`MaxxTable::cells_at`], so the ECU never sees a grid
/// without the checksum that goes with it, but only while it fits in one write. A table whose
/// cells run past [`MAXIMUM_WRITE`] minus four cannot be sent atomically, and
/// [`fits_in_one_write`] is how to ask before starting.
///
/// `blob` is the tune as it currently stands, `table` is as [`table_at`] found it, and `cells`
/// is the new grid, row by row with the column changing fastest (the order the ECU stores it
/// in).
pub fn record(blob: &[u8], table: &MaxxTable, cells: &[i16]) -> Result<Vec<u8>> {
    let count = table.columns * table.rows;

    if cells.len() != count {
        return Err(Error::Argument(format!(
            "{} is {} by {}, which is {count} cells, and {} were given. (Parameter 'cells')",
            table.name,
            table.columns,
            table.rows,
            cells.len()
        )));
    }

    if table.cells_at < 1 || table.cells_at + (count * 2) > blob.len() {
        return Err(out_of_range("table", "That table is not inside this tune."));
    }

    let mut record = vec![0u8; (count * 2) + 4];

    for (i, cell) in cells.iter().enumerate() {
        record[i * 2..i * 2 + 2].copy_from_slice(&cell.to_le_bytes());
    }

    // The checksum covers the byte in front of the grid as well, which is the last of the nine
    // between the axes and the cells.
    let mut covered = Vec::with_capacity(1 + count * 2);
    covered.push(blob[table.cells_at - 1]);
    covered.extend_from_slice(&record[..count * 2]);

    let crc = maxx_protocol::crc32(&covered);
    record[count * 2..].copy_from_slice(&crc.to_le_bytes());

    Ok(record)
}

/// Whether a table's cells and their checksum go in one write.
///
/// A 6 x 6 does; a 16 x 11 VE table does not, at four bytes per cell pair plus the checksum
/// against a 256-byte limit. It is worth knowing which, because a table sent in one write is
/// never briefly inconsistent and one sent in several is; see [`writes_for`].
pub fn fits_in_one_write(table: &MaxxTable) -> bool {
    (table.columns * table.rows * 2) + 4 <= MAXIMUM_WRITE
}

/// Splits a table's record into the writes that carry it, checksum last.
///
/// A record longer than [`MAXIMUM_WRITE`] cannot arrive at once, and between the first piece
/// landing and the last the ECU holds a grid its stored checksum disagrees with. That was
/// worth refusing until the firmware said what it does about it, and it turns out to do very
/// little: the evaluator recomputes the checksum every time it evaluates a table and, on a
/// mismatch, returns nought for that one evaluation and ticks a counter (the channels MTune
/// calls `Table error counter` and `Table error last`). Nothing latches, nothing limps, and
/// the table is not zeroed; the next evaluation after the last piece lands reads normally
/// again.
///
/// So the window is real and brief, and what matters is that it is as short as it can be and
/// ends definitively. The checksum sits at the end of the record, so sending the pieces in
/// order puts it in the last one: the table disagrees with itself from the first write until
/// the last byte of the last write, and not a moment longer.
///
/// A cold nought for one evaluation is harmless on a bench and is not something to do to an
/// engine under load. Whoever calls this should say so to whoever is about to press the
/// button.
pub fn writes_for(blob: &[u8], table: &MaxxTable, cells: &[i16]) -> Result<Vec<(usize, Vec<u8>)>> {
    let record = record(blob, table, cells)?;
    let mut pieces = Vec::new();

    let mut at = 0;
    while at < record.len() {
        let length = MAXIMUM_WRITE.min(record.len() - at);

        pieces.push((table.cells_at + at, record[at..at + length].to_vec()));
        at += MAXIMUM_WRITE;
    }

    Ok(pieces)
}

/// Asks the ECU to checksum its own tune, a 4 KB chunk at a time, covering the first `covers`
/// bytes ([`WRITE_CEILING`] in the C#'s default).
///
/// Command `0x37`, which is not a data read: the byte count goes in the *offset* field, and
/// what comes back is one CRC-32 per 4 KB of the live tune: the same tune `0x05` reads, at the
/// same base. MTune asks it to cover 62,180 bytes, which is sixteen chunks and the 64 bytes its
/// capture showed.
///
/// What it is for here is checking a write. Reading the range back says the bytes that were
/// sent arrived; this says nothing else moved, anywhere in the tune, which on a controller with
/// no undo is the more useful question, and it costs one exchange rather than 256.
///
/// It reads the **live** tune, so a table sent in several writes will not match anything until
/// the last piece has landed. Ask afterwards, not between.
///
/// Returns one checksum per chunk, or empty if the ECU did not answer.
pub fn checksums(transport: &mut dyn EcuTransport, covers: i32) -> Result<Vec<u32>> {
    if covers <= 0 {
        return Err(Error::Argument(format!(
            "covers ('{covers}') must be a non-zero and non-negative value. (Parameter 'covers')\nActual value was {covers}."
        )));
    }
    if covers > i32::from(u16::MAX) {
        return Err(Error::Argument(format!(
            "covers ('{covers}') must be less than or equal to '65535'. (Parameter 'covers')\nActual value was {covers}."
        )));
    }

    let chunks = ((covers as usize).div_ceil(CHECKSUM_CHUNK)) * 4;
    let mut reply = vec![0u8; usb::reply_length(chunks as i32) as usize];

    transport.write(&usb::request(usb::READ, 0x37, covers, chunks as i32)?)?;

    let got = transport.read(&mut reply, TIMEOUT)?;
    let data = if got == reply.len() {
        usb::try_read_reply(&reply, chunks as i32)
    } else {
        None
    };

    let Some(data) = data else {
        // Whatever did arrive is left in the driver's buffer otherwise, and the next request
        // reads its reply starting in the middle of this one. That turns a successful write
        // into "the ECU read back something else", which is a false alarm about the worst
        // thing this program can report.
        transport.discard_input();

        return Ok(Vec::new());
    };

    Ok(data
        .as_chunks::<4>()
        .0
        .iter()
        .map(|word| u32::from_le_bytes(*word))
        .collect())
}

/// The same checksums, worked out here, for a tune we believe the ECU has (`covers` defaults
/// to [`WRITE_CEILING`] in the C#).
///
/// Confirmed against a bench Race: all sixteen agreed with the ECU's own, including the last
/// chunk, which is short rather than a whole 4,096 and so is not something that agrees by
/// chance. That check covered the 62,180 bytes MTune asks for, where the last chunk is 740;
/// the default here is the write ceiling, 62,183, where it is 743.
pub fn checksums_of(blob: &[u8], covers: i32) -> Result<Vec<u32>> {
    if covers <= 0 {
        return Err(Error::Argument(format!(
            "covers ('{covers}') must be a non-zero and non-negative value. (Parameter 'covers')\nActual value was {covers}."
        )));
    }

    let covers = (covers as usize).min(blob.len());

    Ok((0..covers.div_ceil(CHECKSUM_CHUNK))
        .map(|i| {
            let at = i * CHECKSUM_CHUNK;

            maxx_protocol::crc32(&blob[at..at + CHECKSUM_CHUNK.min(covers - at)])
        })
        .collect())
}

/// Reads back what was just written and says whether it took.
///
/// Worth doing on every write and not only when something looks wrong. The ECU acknowledging a
/// write says it accepted the bytes, not that they are what is now in the tune, and on a
/// controller with no burn to withhold, reading back is the only confirmation there is.
pub fn verify(transport: &mut dyn EcuTransport, offset: i32, expected: &[u8]) -> Result<bool> {
    let mut reply = vec![0u8; usb::reply_length(expected.len() as i32) as usize];

    // Start from a quiet line. This is called after a write, and after something has already
    // gone wrong often enough to be worth assuming: a stray byte left over from an earlier
    // exchange shifts this reply and reports the tune as wrong when it is right.
    transport.discard_input();

    transport.write(&usb::request(usb::READ, READ_TUNE, offset, expected.len() as i32)?)?;

    if transport.read(&mut reply, TIMEOUT)? != reply.len() {
        return Ok(false);
    }

    Ok(usb::try_read_reply(&reply, expected.len() as i32).is_some_and(|got| got == expected))
}

fn u16_at(blob: &[u8], at: usize) -> u16 {
    u16::from_le_bytes([blob[at], blob[at + 1]])
}

fn i16_at(blob: &[u8], at: usize) -> i16 {
    i16::from_le_bytes([blob[at], blob[at + 1]])
}

/// Works out a table's current shape from its 21-byte config struct.
///
/// The struct is where the per-tune part lives: MTune's definitions say where each table's
/// struct is and how big it may get, and the struct says how big it actually is and where its
/// data went. Returns `None` for a table this ECU is not using, which is what counts outside
/// 1..64 mean.
pub fn table_at(blob: &[u8], name: &str, config: i32) -> Option<MaxxTable> {
    if config < 0 || i64::from(config) + TABLE_CONFIG_SIZE as i64 > blob.len() as i64 {
        return None;
    }
    let config = config as usize;

    let columns = usize::from(blob[config + 0x06]);
    let rows = usize::from(blob[config + 0x07]);
    let layers = usize::from(blob[config + 0x08]);

    if !(1..=64).contains(&columns) || !(1..=64).contains(&rows) || layers > 1 {
        return None;
    }

    let record = TABLE_DATA + usize::from(u16_at(blob, config + 0x0B));
    let x = record + 1;
    let y = x + (columns * 2);
    let cells = y + (rows * 2) + PREAMBLE;

    if cells + (columns * rows * 2) > blob.len() {
        return None;
    }

    // Both axes have to climb, or this is not a table.
    //
    // The counts and the data offset come out of a struct read at an address the definitions
    // gave, and two thirds of those addresses are not stated in the file: they follow on from
    // whatever was declared before, so a single drift puts a struct on bytes that are not one.
    // What comes back then is not obviously wrong: a plausible size, an offset inside the
    // blob, and a grid that draws. On this ECU it produced a "VVT Intake PID D Gain Table" of
    // 15 by 39 whose columns ran 1,000 ... 8,000 and then 0, 27, -25,795.
    //
    // Breakpoints are the check because a tuning axis is a series of increasing thresholds
    // (RPM, pressure, temperature, seconds) and nothing indexes a table by a number that goes
    // backwards. It costs the eleven unused tables that sit at two by two with zero axes,
    // which are not worth a tab either.
    if !climbs(blob, x, columns) || !climbs(blob, y, rows) {
        return None;
    }

    Some(MaxxTable {
        name: display(name),
        columns,
        rows,
        x_channel: i32::from(u16_at(blob, config) & 0x7FF),
        y_channel: i32::from(u16_at(blob, config + 0x02) & 0x7FF),
        x_axis_at: x,
        y_axis_at: y,
        cells_at: cells,
    })
}

/// Whether a run of breakpoints strictly increases. One of them always does.
fn climbs(blob: &[u8], at: usize, count: usize) -> bool {
    (1..count).all(|i| i16_at(blob, at + (i * 2)) > i16_at(blob, at + ((i - 1) * 2)))
}

/// Whether a definition is one of the kinds that is not a number and so claims no address.
fn is_non_numeric(kind: &str) -> bool {
    matches!(kind, "miniScript" | "userScript" | "string" | "table")
}

/// Every named setting whose declared bytes overlap writing `length` bytes at `offset`. Empty
/// where nothing does.
///
/// A table's cell address comes from the ECU's own live config struct ([`table_at`]), while
/// every setting's address comes from the definitions file's implicit cursor
/// ([`super::maxx_tune_definitions::read`]): two independent mechanisms that never cross-check
/// each other, because neither one is aware the other exists. `climbs` catches one symptom of
/// a table resolving to memory that is not really its own (breakpoints that do not increase)
/// but a table can resolve to bytes that pass that check and still collide: found on a real
/// ECU, where an unconfigured 5x5 user table's cells began one byte into a completely
/// unrelated expansion-module setting's own declared address.
///
/// **Reported, not refused.** An earlier version of this refused any such write outright, and
/// excluded "Expmod " settings from the check: the definitions file declares all 1,201 of them
/// whether or not any module is actually installed, and on a bench Race with none, three
/// separate real tables collided with one. That exclusion was not enough: a second, unrelated
/// pattern ("USERAIN ", user-configurable analog inputs, also declared whether or not anything
/// is wired to them) immediately produced the same false refusal on VE Table 1. Nothing here
/// can reliably tell "this setting is inert because its hardware is not installed" from "this
/// setting matters" by name alone, and a growing exclusion list built one discovered pattern
/// at a time is not a guardrail anyone should trust. What every caller actually needs is to
/// know what a write would also touch and judge for itself: a human reading a sent-cell
/// confirmation, or an agent deciding whether to ask before touching that table again.
pub fn settings_overlapping(definitions: &[MaxxSettingDefinition], offset: i32, length: i32) -> Vec<String> {
    let mut hits = Vec::new();

    for d in definitions {
        if is_non_numeric(&d.kind) {
            continue;
        }
        if offset < d.address.wrapping_add(d.size()) && offset.wrapping_add(length) > d.address {
            hits.push(d.name.clone());
        }
    }

    hits
}

/// Builds the layout, the table list, the tables as the ECU holds them and the settings pages,
/// all in one pass, because they all have to agree about names.
///
/// Depends on the blob, which a layout normally does not: a table's shape and the whereabouts
/// of its cells are in the ECU rather than in the definitions, so the layout describes this
/// tune as it is now rather than the firmware in general. Reading the tune again rebuilds it.
///
/// One pass and not three because a constant is found by name and the names are not all
/// distinct in the file. Working them out separately gave three different answers to what a
/// thing is called: a table that had to be renamed for the layout kept its original name in
/// the list used to send it, so it could never be sent; and a settings page referred to a name
/// the layout had given to something else, so its fields read another setting's bytes.
/// Whatever a thing is called here, it is called that everywhere.
pub fn build(
    blob: &[u8],
    definitions: &[MaxxSettingDefinition],
    channels: Option<&HashMap<i32, MaxxChannelDefinition>>,
) -> MaxxTuneModel {
    let mut constants: Vec<TuneConstant> = Vec::new();
    let mut tables = Vec::new();
    let mut maps = Vec::new();
    let mut taken = Taken::new();
    let mut pages = SettingsPages::new();

    for definition in definitions {
        if definition.is_table() {
            let Some(table) = table_at(blob, &definition.name, definition.address) else {
                continue;
            };

            let name = unique(&table.name, &mut taken);

            // The table under the name everything else will use for it.
            maps.push(MaxxTable {
                name: name.clone(),
                ..table.clone()
            });

            // The axis names go through the same claim as the table's own. They are constants
            // like any other and are looked up by name, so a setting the definitions happen to
            // call "VE Table 1 X" would otherwise be declared later and quietly replace the
            // axis, and the table would read its breakpoints out of that setting's bytes, at
            // that setting's scale.
            let x = unique(&format!("{name} X"), &mut taken);
            let y = unique(&format!("{name} Y"), &mut taken);

            constants.push(axis(&x, table.x_axis_at, table.columns, table.x_channel, channels));
            constants.push(axis(&y, table.y_axis_at, table.rows, table.y_channel, channels));

            let mut cells = TuneConstant::new(name.clone(), 0, table.cells_at as i32, RealtimeType::S16);
            cells.scale = definition.scale;
            cells.digits = maxx_channel_definitions::digits_for(definition.scale);
            cells.low = definition.low;
            cells.high = definition.high;
            cells.columns = table.columns as i32;
            cells.rows = table.rows as i32;
            constants.push(cells);

            tables.push(TableDefinition {
                id: name.clone(),
                title: name.clone(),
                map: String::new(),
                values: name,
                x_bins: x,
                y_bins: y,
                x_channel: channel_name(table.x_channel, channels),
                y_channel: channel_name(table.y_channel, channels),
            });

            continue;
        }

        // Scripts and text are not numbers and have no place among settings that can be edited
        // as such.
        if is_non_numeric(&definition.kind) {
            continue;
        }
        if i64::from(definition.address) + i64::from(definition.size()) > BLOB_SIZE as i64 {
            continue;
        }

        let setting = unique(&definition.name, &mut taken);

        let mut constant = TuneConstant::new(setting.clone(), 0, definition.address, definition.type_());
        constant.scale = definition.scale;
        constant.digits = maxx_channel_definitions::digits_for(definition.scale);
        constant.low = definition.low;
        constant.high = definition.high;
        constant.columns = definition.length;
        constants.push(constant);

        // The page this setting goes on, under the name the layout just gave it rather than
        // the one the file used.
        pages.add(definition, &setting);
    }

    let layout = TuneLayout {
        pages: vec![page()],
        constants,
        pc_variables: Vec::new(),
        derived: IgnoreCaseMap::new(),
        little_endian: true,

        // A MaxxECU is written a range at a time rather than a page at a time, and the
        // firmware caps a write payload at 531 bytes.
        blocking_factor: 512,
        inter_write_delay: 0,
        after_burn_delay: 0,
    };

    MaxxTuneModel {
        layout,
        tables,
        maps,
        pages: pages.build(),
    }
}

/// Settings to a page, at most.
///
/// MTune's definitions have no pages, so these are invented, and an invented page has to be
/// short. The largest subsystem here declares 1,201 settings and four more declare over 500,
/// which as one page each is a scroll nobody finds anything in and a list nobody wants built.
const PER_PAGE: usize = 60;

/// Names already claimed, compared without regard to case (`HashSet<string>(OrdinalIgnoreCase)`).
type Taken = IgnoreCaseMap<()>;

/// Invents a settings interface for a MaxxECU, so its settings can be looked at rather than
/// only decoded.
///
/// Everything else here reads the interface from the firmware: an INI says which dialog holds
/// which field, what to call it and when it applies. MTune's definitions say none of that: a
/// name, a type, a scale, a range and an address, and nothing about presentation. So reading a
/// MaxxECU's tune left 8,752 settings decoded, addressable by name, and reachable by nobody.
///
/// What the names do carry is the subsystem, as the first word: `IATSensor`, `Fuel`, `Ign`,
/// `Boost`. That is enough to group them, and the file is written subsystem by subsystem, so
/// keeping the declared order keeps related settings together inside a group as well. Pages
/// break when the subsystem changes or when one gets long, and the menus are initial letters,
/// because 298 subsystems is too many headings for a list with no filter on it.
///
/// None of this is the firmware's opinion and it should not pretend to be. It is a way of
/// finding a setting, not a tuning workflow: MTune's own pages group by what somebody is doing,
/// and nothing in these files says what that grouping is.
struct SettingsPages {
    dialogs: IgnoreCaseMap<TuneDialog>,
    by_letter: std::collections::BTreeMap<String, Vec<MenuEntry>>,
    taken: Taken,
    items: Vec<DialogItem>,
    group: String,
    part: i32,
}

impl SettingsPages {
    fn new() -> Self {
        Self {
            dialogs: IgnoreCaseMap::new(),
            by_letter: std::collections::BTreeMap::new(),
            taken: Taken::new(),
            items: Vec::new(),
            group: String::new(),
            part: 1,
        }
    }

    /// Puts a setting on a page, under the name the layout gave it.
    ///
    /// `name` is what the layout calls the constant, which is not always what the file calls
    /// the setting, because the file repeats a few names and a constant is found by name.
    /// Referring to the file's name here would point a field at whatever else claimed it.
    fn add(&mut self, definition: &MaxxSettingDefinition, name: &str) {
        let prefix = prefix(&definition.name);

        if !olv_core::model::eq_ignore_case(prefix, &self.group) {
            self.finish();
            self.group = prefix.to_string();
            self.part = 1;
        } else if self.items.len() >= PER_PAGE {
            self.finish();
        }

        // A list gets a row per point rather than one row.
        //
        // A field with no subscript addresses the first element, so an eighteen-point sensor
        // calibration would appear as a single box reading 4.660, which is not the setting, it
        // is a fifth of one of its points, with nothing to say so. There are only 290 lists
        // here and none is longer than 64, so every point can simply be shown.
        if definition.length > 1 {
            for i in 0..definition.length {
                if self.items.len() >= PER_PAGE {
                    self.finish();
                }

                self.items.push(
                    DialogItem::new(DialogItemKind::Field, format!("{name} [{i}]")).with_target(format!("{name}[{i}]")),
                );
            }

            return;
        }

        self.items
            .push(DialogItem::new(DialogItemKind::Field, name).with_target(name));
    }

    fn build(mut self) -> TuneInterface {
        self.finish();

        TuneInterface {
            menus: self
                .by_letter
                .into_iter()
                .map(|(title, entries)| TuneMenu { title, entries })
                .collect(),
            dialogs: self.dialogs,
        }
    }

    fn finish(&mut self) {
        if self.items.is_empty() {
            return;
        }

        // Numbered only where a subsystem needed more than one page, so one that fits is not
        // called "Fuel 1" for no reason.
        let wanted = if self.part > 1 {
            format!("{} {}", self.group, self.part)
        } else {
            self.group.clone()
        };
        let title = unique(&wanted, &mut self.taken);
        let name = format!("maxx:{title}");

        self.dialogs.insert(
            &name,
            TuneDialog {
                name: name.clone(),
                title: title.clone(),
                layout: "yAxis".to_string(),
                items: std::mem::take(&mut self.items),
                help: String::new(),
            },
        );

        self.by_letter
            .entry(letter(&self.group))
            .or_default()
            .push(MenuEntry::new(name, title, ""));

        self.part += 1;
    }
}

/// The menu a subsystem goes under: its initial, upper-cased, or `#` where it is not a letter.
///
/// The C# looks at the first UTF-16 unit, so a first character outside the BMP (a surrogate
/// pair) is not a letter there either.
fn letter(group: &str) -> String {
    let Some(first) = group.chars().next() else {
        return "#".to_string();
    };

    if first.len_utf16() == 1 && first.is_alphabetic() {
        let mut upper = first.to_uppercase();
        return match (upper.next(), upper.next()) {
            (Some(c), None) => c.to_string(),
            _ => first.to_string(),
        };
    }

    "#".to_string()
}

/// The subsystem a setting belongs to: the first word of its name.
fn prefix(name: &str) -> &str {
    match name.find(' ') {
        Some(space) if space > 0 => &name[..space],
        _ => name,
    }
}

/// An axis: a row of breakpoints counted in whatever the channel on that axis is counted in.
///
/// Which is not the table's own scale, and that is the trap: a VE table is scaled by a tenth
/// because its cells are percent, while its columns are RPM and stored whole. Scale the axis
/// like the table and a 5,000 RPM breakpoint reads 500. The channel the ECU indexes the axis by
/// is in the config struct, and MTune's realtime definitions give that channel's own scale and
/// units (RPM at 1, MAP at a tenth) so the axis is scaled by the thing it actually measures.
fn axis(
    name: &str,
    offset: usize,
    count: usize,
    channel: i32,
    channels: Option<&HashMap<i32, MaxxChannelDefinition>>,
) -> TuneConstant {
    let measured = channels.and_then(|c| c.get(&channel));

    let mut constant = TuneConstant::new(name, 0, offset as i32, RealtimeType::S16);
    constant.scale = measured.map_or(1.0, |m| m.scale);
    constant.units = measured.map_or_else(String::new, |m| m.units.clone());
    constant.digits = measured.map_or(0, |m| m.digits);
    constant.columns = count as i32;
    constant
}

/// What to call a table.
///
/// MTune names the thing that holds the numbers, "VE Table 1 data", which is accurate about
/// the file and wrong on a tab.
fn display(name: &str) -> String {
    const SUFFIX: &str = " data";

    match name.len().checked_sub(SUFFIX.len()) {
        Some(at) if name.is_char_boundary(at) && name[at..].eq_ignore_ascii_case(SUFFIX) => name[..at].to_string(),
        _ => name.to_string(),
    }
}

/// Keeps names distinct.
///
/// The definitions repeat a few, and a constant is looked up by name, so a repeat would quietly
/// shadow the first and put one table's numbers under another's title.
fn unique(name: &str, taken: &mut Taken) -> String {
    if taken.try_add(name, ()) {
        return name.to_string();
    }

    let mut n = 2;
    loop {
        let candidate = format!("{name} ({n})");
        if taken.try_add(&candidate, ()) {
            return candidate;
        }
        n += 1;
    }
}

/// What the ECU indexes an axis by, named from the realtime definitions.
///
/// The same numbering the telemetry uses, so a table's axis and a logged channel that share an
/// id are the same quantity, which is what lets a log be laid over a table.
fn channel_name(id: i32, channels: Option<&HashMap<i32, MaxxChannelDefinition>>) -> String {
    channels
        .and_then(|c| c.get(&id))
        .map_or_else(String::new, |c| c.name.clone())
}
