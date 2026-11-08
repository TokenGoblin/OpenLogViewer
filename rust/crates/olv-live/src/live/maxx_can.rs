//! Ported from `MaxxCan.cs`. Owned by package `w4-live`.
//!
//! A MaxxECU relaying its CAN bus over USB: what MTune calls the CAN Analyzer.
//!
//! The ECU keeps received frames in a ring of sixty seventeen-byte records and hands them over
//! on command `0x38`, emptying what it gives. Records carry the identifier as a full 32-bit
//! word, so 29-bit extended identifiers arrive intact, and each one has a hardware microsecond
//! timestamp taken when the frame arrived rather than when it was collected, which is what
//! makes bus timing worth measuring rather than guessing at.
//!
//! **It sees the whole bus.** The controller's hardware acceptance filter is set to match
//! everything (identifier nought, mask nought) and the analyzer is fed from the driver before
//! any of the ECU's own identifier matching happens. So frames the ECU is not configured to
//! use, and identifiers nobody has told it about, all reach the ring. That is what makes this
//! useful for reading a bus nobody has documented, and it is not something to assume of a
//! relay: an ECU that filtered in hardware would show only what it already knew.
//!
//! Read out of the 1.151 firmware by the analysis in the TCU repository: the ring producer,
//! the filter set-up, and the superloop drain that feeds it. Not yet confirmed on a wire,
//! which for the promiscuity claim above means: enable it, put the ECU on any bus, and watch
//! for an identifier it has no reason to know.

use olv_core::model::CanFrame;
use olv_core::{Error, Result};
use olv_protocol::ms::maxx_usb_protocol as usb;

use super::maxx_tune::MAXIMUM_WRITE;
use super::maxx_tune_definitions::MaxxSettingDefinition;

/// The command that empties the ring.
pub const TAKE: u8 = 0x38;

/// How long one record is.
pub const RECORD_LENGTH: usize = 17;

/// Frames one read can carry.
///
/// The ECU caps a reply at 256 bytes whatever is being read, and fifteen records is 255 of
/// them. Asking for sixteen asks for 272 and is refused, so this is the ceiling rather than a
/// choice.
pub const MOST_PER_READ: usize = MAXIMUM_WRITE / RECORD_LENGTH;

/// The setting that turns the analyzer on, as MTune names it.
///
/// Nothing reaches the ring while it is nought, and the ECU clears it when the link drops, so
/// it is armed for a session rather than set once and forgotten. It lives in the tune like any
/// other setting, which means `maxx_tune::write` can set it, and a MaxxECU write is permanent
/// as it lands.
pub const ENABLE_SETTING: &str = "CAN Analyzer Enable";

/// Where [`ENABLE_SETTING`] lives in this tune, or `None` if MTune's definitions do not
/// declare it.
///
/// Resolved by name rather than kept as a fixed offset: every other tune address in this
/// codebase comes from MTune's own definitions file, which is what lets a firmware update move
/// a setting without silently reading the wrong byte here. A caller with no definitions loaded
/// (no MTune installed) cannot tell whether the analyzer is armed and should treat that as
/// "unknown", not "off".
pub fn enable_at(definitions: &[MaxxSettingDefinition]) -> Option<i32> {
    definitions.iter().find(|d| d.name == ENABLE_SETTING).map(|d| d.address)
}

/// The command that reads the ECU's runtime memory rather than its tune.
///
/// The only way to see anything the firmware keeps outside the tune, which includes the one
/// number that says whether a capture is complete.
pub const SNAPSHOT: u8 = 0x10;

/// The bound the firmware enforces: it refuses unless `offset + length` is **below** this.
///
/// So this is not the size of the window. The readable window is 3,200 bytes, offsets 0 to
/// 3,199, and 3,201 is the first total the ECU will not answer. The two are easy to conflate
/// and the difference is the last byte.
///
/// Taken from the handler rather than from watching MTune, which asks for more than it can
/// have: its last read of the window is 3,072 plus 216, and the ECU answers that one `0x30`
/// (out of range) on every connect. A host program's request is evidence of what it wants, not
/// of what it gets, and the reply sitting under each request in a capture is the half that says
/// which.
pub const SNAPSHOT_LIMIT: i32 = 0xC81;

/// Bytes of the runtime snapshot that can actually be read.
pub const SNAPSHOT_WINDOW: i32 = SNAPSHOT_LIMIT - 1;

/// Where the dropped-frame count sits in that window.
///
/// The firmware counts losses at `0x2000881E` and the window begins at `0x20007DAC`, which
/// puts the counter 2,674 bytes into it, inside [`SNAPSHOT_LIMIT`], so no new command is needed
/// to read it.
///
/// Sixteen bits, and free-running: nothing resets it, including arming the analyzer or
/// clearing its ring. So a capture takes a reading at the start and adds up the distances
/// between readings afterwards, rather than subtracting one number from another: at a few
/// thousand losses a second the counter comes all the way round in half a minute, and a capture
/// that subtracted would report the remainder.
///
/// One counter, not two. The receive interrupts and the analyzer ring all increment this same
/// place, so it covers both the frames the driver could not buffer and the frames the ring
/// could not hold. That is what lets a capture claim to be complete: nought here means nothing
/// was missed, short of the controller's own hardware queue overrunning, which takes the
/// receive interrupt being starved rather than merely a busy bus.
pub const DROP_COUNT_AT: i32 = 0x2000_881E - 0x2000_7DAC;

/// How far a sixteen-bit counter has moved between two readings.
///
/// A distance and not a subtraction. The counter wraps at 65,536 and is never reset, so on a
/// bus losing frames steadily it comes all the way round in well under a minute, and a reading
/// taken after that is numerically smaller than the one before it. Subtracting gives a
/// negative, or with unsigned arithmetic an enormous positive; both are reported as fact by
/// something whose whole job is saying honestly how much was missed.
pub fn since(last: i32, now: i32) -> i32 {
    (now.wrapping_sub(last) as u32 & 0xFFFF) as i32
}

/// Pulls frames out of a reply.
///
/// The record is a length and flags byte, a 32-bit identifier, a 32-bit microsecond stamp, then
/// the data. The low nibble of the first byte is how many of the eight data bytes mean
/// anything; the rest of them are whatever the ring happened to hold and must not be shown as
/// payload.
pub fn frames(reply: &[u8]) -> Vec<CanFrame> {
    let mut frames = Vec::with_capacity(reply.len() / RECORD_LENGTH);

    for record in reply.as_chunks::<RECORD_LENGTH>().0 {
        let mut length = usize::from(record[0] & 0x0F);

        // A record the ECU never filled, which is what an empty ring slot reads as.
        // Nought-length frames are legal on a bus, but a slot that is entirely zero has no
        // identifier and no stamp either, and taking it produces a phantom frame at identifier
        // nought.
        if length == 0 && record[1..].iter().all(|&b| b == 0) {
            continue;
        }

        if length > 8 {
            length = 8;
        }

        let id = u32::from_le_bytes([record[1], record[2], record[3], record[4]]);
        let at = u32::from_le_bytes([record[5], record[6], record[7], record[8]]);

        frames.push(CanFrame {
            id: id as i32,

            // The flag beside the length. The firmware copies it from the driver's own record
            // and it has not been established whether it marks an extended identifier or
            // simply a filled slot, so the identifier is trusted over it: anything past eleven
            // bits is extended because it cannot be anything else.
            is_extended: (record[0] & 0x10) != 0 || id > 0x7FF,

            data: record[9..9 + length].to_vec(),
            at: i64::from(at),
        });
    }

    frames
}

/// Asks the ECU for up to `frames` of them.
///
/// What comes back is emptied from the ring, so a reply not read is a reply lost: there is no
/// asking again.
pub fn ask(frames: usize) -> Result<Vec<u8>> {
    if frames == 0 {
        return Err(Error::Argument(
            "frames ('0') must be a non-zero value. (Parameter 'frames')\nActual value was 0.".to_string(),
        ));
    }
    if frames > MOST_PER_READ {
        return Err(Error::Argument(format!(
            "frames ('{frames}') must be less than or equal to '{MOST_PER_READ}'. \
             (Parameter 'frames')\nActual value was {frames}."
        )));
    }

    usb::request(usb::READ, TAKE, 0, (frames * RECORD_LENGTH) as i32)
}
