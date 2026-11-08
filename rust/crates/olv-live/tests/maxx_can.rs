//! Reading a MaxxECU's CAN ring (what MTune calls the CAN Analyzer): the C# `MaxxCanTests`.
//!
//! The records are composed here to the layout read out of the 1.151 firmware: seventeen bytes, a
//! length and flag byte, a 32-bit identifier, a 32-bit microsecond stamp, and eight data bytes of
//! which only the declared length means anything. Nothing about this has been on a wire yet, so
//! these tests are what the decode is checked against until an ECU is on a bus.

use olv_core::Error;
use olv_live::live::maxx_can;
use olv_live::live::maxx_tune;
use olv_live::live::maxx_tune_definitions::MaxxSettingDefinition;
use olv_protocol::ms::maxx_protocol;
use olv_protocol::ms::maxx_usb_protocol as usb;

/// Lays one record out the way the ECU does.
fn record(id: u32, length: usize, at: u32, data: &[u8], flag: bool, tail: Option<&[u8]>) -> Vec<u8> {
    let mut record = vec![0u8; maxx_can::RECORD_LENGTH];

    record[0] = ((length & 0x0F) | if flag { 0x10 } else { 0 }) as u8;
    record[1..5].copy_from_slice(&id.to_le_bytes());
    record[5..9].copy_from_slice(&at.to_le_bytes());

    // The whole eight bytes are written, because the ring does not clear what a shorter frame leaves
    // behind.
    let tail = tail.unwrap_or(data);
    record[9..9 + tail.len()].copy_from_slice(tail);
    record[9..9 + data.len()].copy_from_slice(data);

    record
}

fn plain(id: u32, length: usize, at: u32, data: &[u8]) -> Vec<u8> {
    record(id, length, at, data, false, None)
}

#[test]
fn a_frame_comes_out_of_its_record() {
    let reply = plain(0x7E8, 8, 1_234_567, &[1, 2, 3, 4, 5, 6, 7, 8]);

    let frames = maxx_can::frames(&reply);
    assert_eq!(frames.len(), 1);
    let frame = &frames[0];

    assert_eq!(frame.id, 0x7E8);
    assert!(!frame.is_extended);
    assert_eq!(frame.data, [1, 2, 3, 4, 5, 6, 7, 8]);
    assert_eq!(frame.at, 1_234_567);
    assert_eq!(frame.label(), "7E8");
}

/// A 29-bit identifier arrives whole.
///
/// The record carries the identifier as a full 32-bit word for this reason. Masked to eleven bits
/// (the obvious thing to do, and wrong) an extended frame is filed under the low bits it happens to
/// share with something else, which on a bus that uses both looks like one identifier behaving
/// erratically rather than two identifiers.
#[test]
fn an_extended_identifier_is_not_truncated() {
    let reply = record(0x18DA_F110, 8, 0, &[0; 8], true, None);

    let frames = maxx_can::frames(&reply);
    assert_eq!(frames.len(), 1);

    assert_eq!(frames[0].id, 0x18DA_F110);
    assert!(frames[0].is_extended);
    assert_eq!(frames[0].label(), "18DAF110");
}

/// An identifier too big for eleven bits is extended whatever the flag says.
///
/// The flag beside the length is copied from the driver and has not been pinned down: it may mark an
/// extended frame or merely a filled slot. The identifier cannot be ambiguous in the same way, so it
/// decides.
#[test]
fn a_wide_identifier_is_extended_even_without_the_flag() {
    let reply = record(0x1F_FFFF, 1, 0, &[0xAA], false, None);

    let frames = maxx_can::frames(&reply);
    assert_eq!(frames.len(), 1);
    assert!(frames[0].is_extended);
}

/// Only the declared bytes are payload.
///
/// The ring does not wipe a slot before reusing it, so a three-byte frame landing where an eight-byte
/// one was leaves five bytes of the old one behind. Reporting those as data invents payload that was
/// never on the bus, and on an undocumented bus, invented bytes are what somebody then spends an
/// evening trying to decode.
#[test]
fn the_bytes_after_the_declared_length_are_not_payload() {
    let reply = record(0x100, 3, 0, &[0x11, 0x22, 0x33], false, Some(&[9; 8]));

    let frames = maxx_can::frames(&reply);
    assert_eq!(frames.len(), 1);

    assert_eq!(frames[0].length(), 3);
    assert_eq!(frames[0].data, [0x11, 0x22, 0x33]);
    assert_eq!(frames[0].hex(), "112233");
}

/// Several frames come out of one reply, in the order the ring held them.
#[test]
fn a_reply_carries_several_frames_in_order() {
    let mut reply = plain(0x100, 1, 10, &[1]);
    reply.extend(plain(0x200, 1, 20, &[2]));
    reply.extend(plain(0x300, 1, 30, &[3]));

    let frames = maxx_can::frames(&reply);

    assert_eq!(frames.iter().map(|f| f.id).collect::<Vec<_>>(), [0x100, 0x200, 0x300]);
    assert_eq!(frames.iter().map(|f| f.at).collect::<Vec<_>>(), [10, 20, 30]);
}

/// An empty slot is not a frame at identifier nought.
///
/// A ring the ECU has not filled reads as zeroes, and a zero record has a legal length (nought) so
/// taking it at face value produces a phantom frame every time the bus is quiet. Identifier nought is
/// also a real and very high priority identifier on a live bus, so the phantom lands somewhere it
/// would be believed.
#[test]
fn an_empty_slot_is_not_a_frame() {
    assert!(maxx_can::frames(&[0u8; maxx_can::RECORD_LENGTH * 3]).is_empty());

    // But a genuine nought-length frame at a real identifier is one.
    let real = plain(0x123, 0, 99, &[]);
    let frames = maxx_can::frames(&real);
    assert_eq!(frames.len(), 1);

    assert_eq!(frames[0].id, 0x123);
    assert!(frames[0].data.is_empty());
}

/// A reply that stops mid-record gives up what is whole and no more.
#[test]
fn a_half_record_at_the_end_is_ignored() {
    let mut reply = plain(0x100, 1, 5, &[7]);
    reply.extend([0x01, 0x02, 0x03]);

    assert_eq!(maxx_can::frames(&reply).len(), 1);
}

/// A read asks for whole records, and for no more than the ECU will send.
///
/// The 256-byte reply cap is not a choice here: fifteen records is 255 bytes and sixteen is 272,
/// which is refused outright rather than truncated.
#[test]
fn a_read_asks_for_whole_records_within_the_reply_cap() {
    assert_eq!(maxx_can::MOST_PER_READ, 15);

    let request = maxx_can::ask(maxx_can::MOST_PER_READ).unwrap();

    assert_eq!(request[0], usb::READ);
    assert_eq!(request[1], maxx_can::TAKE);
    assert_eq!(usize::from(request[4]) | (usize::from(request[5]) << 8), 255);
    const { assert!(255 <= maxx_tune::MAXIMUM_WRITE) };

    assert!(matches!(
        maxx_can::ask(maxx_can::MOST_PER_READ + 1),
        Err(Error::Argument(_))
    ));
    assert!(matches!(maxx_can::ask(0), Err(Error::Argument(_))));
}

/// The request is a well-formed one, checksum and all, so it goes out like any other.
#[test]
fn the_request_is_an_ordinary_read_with_its_checksum() {
    let request = maxx_can::ask(4).unwrap();

    assert_eq!(request.len(), usb::REQUEST_LENGTH);
    assert_eq!(
        maxx_protocol::crc32(&request[..6]),
        u32::from_le_bytes([request[6], request[7], request[8], request[9]])
    );
}

/// The dropped-frame counter is inside the window the runtime-snapshot command will answer, so
/// reading it needs no command of its own.
///
/// Worth pinning as arithmetic rather than a remembered number: the counter is at 0x2000881E, the
/// window starts at 0x20007DAC and the firmware's bound is 3,201, and if either of those ever turns
/// out different the offset is wrong in a way that reads plausible rubbish rather than failing.
#[test]
fn the_drop_counter_is_inside_the_snapshot_window() {
    assert_eq!(maxx_can::DROP_COUNT_AT, 0x2000_881E - 0x2000_7DAC);
    assert_eq!(maxx_can::DROP_COUNT_AT, 2674);

    // Two bytes of it, inside the bound the firmware enforces. The bound is exclusive, so the window
    // is 3,200 bytes and 3,201 is the first total refused, not the 3,288 MTune asks for and is told
    // 0x30 about.
    const { assert!(maxx_can::DROP_COUNT_AT + 2 <= maxx_can::SNAPSHOT_WINDOW) };
    assert_eq!(maxx_can::SNAPSHOT_LIMIT, 3201);
    assert_eq!(maxx_can::SNAPSHOT_WINDOW, 3200);
}

/// Losses are counted as a distance, so a counter that wrapped still reports more rather than less.
///
/// Sixteen bits, free-running, never reset. On a bus shedding frames steadily it comes all the way
/// round in well under a minute, and a capture that subtracted its starting reading would report the
/// remainder: seventy thousand frames lost, reported as four and a half thousand. That is worse than
/// admitting ignorance, because it looks like a measurement.
#[test]
fn losses_are_counted_as_a_distance_and_not_a_subtraction() {
    for (last, now, expected) in [
        (0, 0, 0),
        (10, 25, 15),
        (65_530, 4, 10),
        (65_535, 0, 1),
        (0, 65_535, 65_535),
    ] {
        assert_eq!(maxx_can::since(last, now), expected, "since({last}, {now})");
    }
}

/// The analyzer's enable flag is where the firmware reads it, and it is a setting somebody can be
/// shown by name rather than an address to poke.
#[test]
fn the_enable_flag_is_a_named_setting_resolved_from_the_definitions() {
    let definitions = [MaxxSettingDefinition::new(
        "CAN Analyzer Enable",
        "uint8",
        52658,
        1,
        1.0,
        0.0,
        1.0,
    )];

    assert_eq!(maxx_can::enable_at(&definitions), Some(52658));
    assert_eq!(maxx_can::ENABLE_SETTING, "CAN Analyzer Enable");
}

/// A machine with no MTune installed has no definitions at all, and "unheard of" is not the same
/// claim as "present and off".
#[test]
fn the_enable_flag_is_unknown_without_definitions() {
    assert_eq!(maxx_can::enable_at(&[]), None);
}
