//! Ported from `MaxxUsbProtocol.cs`. Owned by package `w1-ms-maxx`.
//!
//! The protocol a MaxxECU speaks over its USB cable.
//!
//! Not the same protocol as the Bluetooth one, which is the thing that made USB look
//! impossible for a while: the framing shares no byte with it. There is no
//! `77 AA 77` magic, no trailer, no activation to replay, and the ECU answers
//! nothing at all if you send it the Bluetooth conversation, which it does not, at
//! any baud rate, with any handshake, silently.
//!
//! What the two do share is the checksum, and that is what says they are one
//! controller's work: [`maxx_protocol::crc32`], the STM32's own CRC peripheral fed
//! as little-endian words. Bluetooth keeps its low sixteen bits, USB keeps all
//! thirty-two.
//!
//! This one is the better protocol of the two. It is a memory interface rather than
//! a subscription (every request names an offset and a length) so it can read and
//! write rather than only listen, and its telemetry carries the channel id beside
//! every value instead of relying on both ends having agreed a layout in advance.
//! Nothing here has to be told what to expect.
//!
//! Recovered by watching MTune 1.161 connect to a MaxxECU Race over USB, and checked
//! against that recording: 4,038 of 4,038 request checksums and 3,560 of 3,560 reply
//! checksums.
//!
//! This file builds requests only; it has no guard on what command byte a caller
//! passes. In particular nothing here knows that command 0x2F erases the ECU's
//! flash: [`request`] will compose it for any caller. The guard, if any, lives in
//! the callers (`MaxxUsbSource`, `MaxxTune`), which belong to a later package.

use std::time::Duration;

use olv_core::{Error, Result};

use super::maxx_protocol;
use super::ms_protocol::EcuTransport;

/// The rate the ECU's UART runs at.
///
/// There is nothing to discover here and no negotiating: MTune sets 9,600 and then
/// immediately 921,600 on every open, and the second is the one that matters. At any
/// other rate the ECU is silent, which is indistinguishable from an ECU that is not
/// there.
pub const BAUD_RATE: i32 = 921_600;

/// A request reads from the ECU.
pub const READ: u8 = 0x00;

/// A request writes to it, with the payload following the header.
pub const WRITE: u8 = 0x01;

/// How many bytes of telemetry are waiting. Always read as two bytes, and the answer
/// is the length to ask [`TAKE`] for.
pub const WAITING: u8 = 0x13;

/// Takes the waiting telemetry.
pub const TAKE: u8 = 0x12;

/// What MTune sends while it is looking for an ECU, once per poll, reading a single
/// byte. An answer to this is the whole of "something is there".
pub const HELLO: u8 = 0x16;

/// The status byte of a reply that worked.
pub const OK: u8 = 0x80;

/// Every request is this long: a six-byte header and its checksum.
pub const REQUEST_LENGTH: usize = 10;

/// A reply carries a status byte and a checksum besides its data.
pub const REPLY_OVERHEAD: usize = 1 + 4;

/// The largest reply seen, which is a 512-byte block read. Requests for more were
/// never observed, so this is also as much as is asked for.
pub const MAXIMUM_DATA: usize = 512;

/// Builds a request.
///
/// `[direction][command][offset u16][length u16][crc32]`, all little-endian, with the
/// checksum over the six header bytes.
pub fn request(direction: u8, command: u8, offset: i32, length: i32) -> Result<Vec<u8>> {
    if offset < 0 {
        return Err(Error::Argument(format!(
            "offset ('{offset}') must be a non-negative value."
        )));
    }
    if length < 0 {
        return Err(Error::Argument(format!(
            "length ('{length}') must be a non-negative value."
        )));
    }
    if offset > i32::from(u16::MAX) {
        return Err(Error::Argument(format!(
            "offset ('{offset}') must be less than or equal to '65535'."
        )));
    }
    if length > i32::from(u16::MAX) {
        return Err(Error::Argument(format!(
            "length ('{length}') must be less than or equal to '65535'."
        )));
    }

    let mut request = Vec::with_capacity(REQUEST_LENGTH);

    request.push(direction);
    request.push(command);
    request.extend_from_slice(&(offset as u16).to_le_bytes());
    request.extend_from_slice(&(length as u16).to_le_bytes());

    let crc = maxx_protocol::crc32(&request);
    request.extend_from_slice(&crc.to_le_bytes());

    Ok(request)
}

/// Asks how much telemetry is waiting.
pub fn ask_what_is_waiting() -> Vec<u8> {
    request(READ, WAITING, 0, 2).expect("constant arguments are in range")
}

/// Asks for that many bytes of it.
pub fn ask_for(bytes: i32) -> Result<Vec<u8>> {
    request(READ, TAKE, 0, bytes)
}

/// The probe that finds out whether a MaxxECU is on the other end.
pub fn are_you_there() -> Vec<u8> {
    request(READ, HELLO, 0, 1).expect("constant arguments are in range")
}

/// How long a reply to a request for this many bytes should be.
pub fn reply_length(data_length: i32) -> i32 {
    data_length + REPLY_OVERHEAD as i32
}

/// Checks a reply and hands back its data.
///
/// Refuses rather than trusting: a reply carries no echo of the request that
/// produced it, so a stale one from an earlier round is otherwise decoded as the
/// answer to this one. The length and the checksum together are all there is to go
/// on, and both are checked.
pub fn try_read_reply(reply: &[u8], data_length: i32) -> Option<Vec<u8>> {
    if data_length < 0 || reply.len() as i64 != i64::from(reply_length(data_length)) {
        return None;
    }
    if reply[0] != OK {
        return None;
    }

    let data_length = data_length as usize;
    let covered = &reply[..1 + data_length];
    let at = 1 + data_length;
    let declared = u32::from_le_bytes([reply[at], reply[at + 1], reply[at + 2], reply[at + 3]]);

    if maxx_protocol::crc32(covered) != declared {
        return None;
    }

    Some(reply[1..1 + data_length].to_vec())
}

/// Sends a request and reads its reply, retrying the round rather than treating one
/// lost byte or bad checksum as a dead link.
///
/// Shared by `MaxxUsbSource`'s telemetry and configuration requests and
/// `MaxxTune.Read`'s tune pieces: the two used to carry this loop separately, at
/// different retry counts, which is exactly the kind of drift a caller cannot see and
/// a review has to go looking for. The two still differ deliberately: how many
/// attempts, how long each waits, and what `reply` buffer to reuse are all the
/// caller's to choose, since a hot polling read and a four-second tune dump have very
/// different costs for getting either wrong.
///
/// `reply` is sized to [`reply_length`] for `data_length`: the caller's buffer, so a
/// hot path can reuse one across calls instead of allocating a reply-sized array
/// every round. `on_retry` is called before each attempt past the first, for a caller
/// counting them.
///
/// `Ok(None)` is the C#'s `false`. A transport error is propagated as the C#'s
/// exception would be: it does not count as a failed attempt.
pub fn try_ask(
    transport: &mut dyn EcuTransport,
    request: &[u8],
    reply: &mut [u8],
    data_length: i32,
    timeout: Duration,
    attempts: i32,
    mut on_retry: Option<&mut dyn FnMut()>,
) -> Result<Option<Vec<u8>>> {
    for attempt in 0..attempts {
        if attempt > 0 {
            if let Some(retry) = on_retry.as_mut() {
                retry();
            }
            transport.discard_input();
        }

        transport.write(request)?;

        let got = transport.read(reply, timeout)?;

        if got == reply.len()
            && let Some(data) = try_read_reply(&reply[..got], data_length)
        {
            return Ok(Some(data));
        }
    }

    Ok(None)
}

/// Reads the telemetry payload into `into`, returning how many channels it carried.
///
/// The payload is a run of four-byte pairs (a channel id and its raw value, both
/// 16-bit little-endian) and it carries only the channels whose value has moved since
/// the last one. So this updates rather than replaces, and a payload of sixty bytes is
/// fifteen channels that changed and not the whole of what the ECU knows.
///
/// The ids are the same ones MTune's channel definitions use and the same ones the
/// Bluetooth path subscribes by, so a value means the same thing whichever cable it
/// arrived over.
///
/// `into` is any map (`HashMap`, `BTreeMap`): inserting a key already present replaces
/// its value, as the C# indexer does.
pub fn read_updates(payload: &[u8], into: &mut impl Extend<(i32, u16)>) -> i32 {
    let mut taken = 0;

    for pair in payload.as_chunks::<4>().0 {
        into.extend([(
            i32::from(u16::from_le_bytes([pair[0], pair[1]])),
            u16::from_le_bytes([pair[2], pair[3]]),
        )]);

        taken += 1;
    }

    taken
}

/// Whether a payload looks like telemetry at all.
///
/// The ids in one arrive in ascending order and never repeat (true of all 1,765
/// payloads in the recording) so a payload that breaks either rule is not a list of
/// channels, whatever its length says. Cheap, and the one check that catches a stream
/// read at the wrong offset, which otherwise produces channel numbers that exist and
/// values that are nonsense.
pub fn looks_like_telemetry(payload: &[u8]) -> bool {
    if !payload.len().is_multiple_of(4) {
        return false;
    }

    let mut previous: i32 = -1;

    for pair in payload.as_chunks::<4>().0 {
        let id = i32::from(u16::from_le_bytes([pair[0], pair[1]]));
        if id <= previous {
            return false;
        }

        previous = id;
    }

    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::{BTreeMap, VecDeque};
    use std::path::PathBuf;

    /// Request and reply pairs recorded off a MaxxECU Race over USB, MTune 1.161.
    /// Nothing in it was composed here, which is the point.
    fn recorded() -> Vec<(Vec<u8>, Vec<u8>)> {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../tests/OpenLogViewer.Tests/Fixtures/maxxecu-usb.txt");
        let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("fixture {}: {e}", path.display()));

        let unhex = |s: &str| -> Vec<u8> {
            (0..s.len())
                .step_by(2)
                .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
                .collect()
        };

        text.lines()
            .filter(|l| !l.starts_with('#') && !l.is_empty())
            .filter_map(|l| {
                let halves: Vec<&str> = l.split(' ').filter(|s| !s.is_empty()).collect();
                (halves.len() == 2).then(|| (unhex(halves[0]), unhex(halves[1])))
            })
            .collect()
    }

    #[test]
    fn the_recording_is_there() {
        assert_eq!(recorded().len(), 30);
    }

    #[test]
    fn every_recorded_request_is_reproduced_exactly() {
        // Composing each request from its own fields has to land on the bytes MTune
        // sent, checksum included. This is the whole claim about the request format
        // in one assertion.
        for (sent, _) in recorded() {
            let rebuilt = request(
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
        for (sent, reply) in recorded() {
            let asked = i32::from(sent[4]) | (i32::from(sent[5]) << 8);

            // Only the reads: a write's reply answers the payload that followed the
            // header rather than the header's own length.
            if sent[0] != READ {
                continue;
            }
            if reply.len() as i32 != reply_length(asked) {
                continue;
            }

            let data = try_read_reply(&reply, asked)
                .unwrap_or_else(|| panic!("A recorded reply to command 0x{:02X} was refused.", sent[1]));

            assert_eq!(data.len() as i32, asked);
        }
    }

    // ----- byte-level additions ---------------------------------------------

    #[test]
    fn the_three_fixed_requests_are_the_recorded_bytes() {
        // First line of the recording: MTune's hello, 00 16 0000 0100 + crc.
        assert_eq!(
            are_you_there(),
            [0x00, 0x16, 0x00, 0x00, 0x01, 0x00, 0xC9, 0x43, 0x74, 0x81]
        );
        // "How much is waiting" is a two-byte read of command 0x13, offset 0; the
        // recording has it as 001300000200885DBFFB.
        assert_eq!(
            ask_what_is_waiting(),
            [0x00, 0x13, 0x00, 0x00, 0x02, 0x00, 0x88, 0x5D, 0xBF, 0xFB]
        );
        // Take 0xB4 bytes: 00120000B4003C902F47 in the recording.
        assert_eq!(
            ask_for(0xB4).unwrap(),
            [0x00, 0x12, 0x00, 0x00, 0xB4, 0x00, 0x3C, 0x90, 0x2F, 0x47]
        );
    }

    #[test]
    fn request_fields_are_little_endian_and_range_checked() {
        let r = request(WRITE, 0x07, 0x1234, 0x0056).unwrap();
        assert_eq!(&r[..6], [0x01, 0x07, 0x34, 0x12, 0x56, 0x00]);
        assert_eq!(r.len(), REQUEST_LENGTH);
        assert!(request(READ, 1, 65535, 65535).is_ok());
        assert!(request(READ, 1, -1, 0).is_err());
        assert!(request(READ, 1, 0, -1).is_err());
        assert!(request(READ, 1, 65536, 0).is_err());
        assert!(request(READ, 1, 0, 65536).is_err());
    }

    #[test]
    fn a_reply_is_judged_on_length_status_and_checksum() {
        let mut reply = vec![OK, 1, 2, 3];
        let crc = maxx_protocol::crc32(&reply);
        reply.extend_from_slice(&crc.to_le_bytes());

        assert_eq!(try_read_reply(&reply, 3).unwrap(), [1, 2, 3]);
        assert!(try_read_reply(&reply, 2).is_none());
        assert!(try_read_reply(&reply, -1).is_none());

        let mut bad_status = reply.clone();
        bad_status[0] = 0x81;
        assert!(try_read_reply(&bad_status, 3).is_none());

        let mut bad_crc = reply.clone();
        *bad_crc.last_mut().unwrap() ^= 1;
        assert!(try_read_reply(&bad_crc, 3).is_none());

        // The 1-byte hello reply of the recording: 80 + crc, zero data bytes.
        let mut empty = vec![OK];
        let crc = maxx_protocol::crc32(&empty);
        empty.extend_from_slice(&crc.to_le_bytes());
        assert_eq!(try_read_reply(&empty, 0).unwrap(), Vec::<u8>::new());
    }

    #[test]
    fn updates_replace_rather_than_clear() {
        let mut map = BTreeMap::new();
        map.insert(61, 1u16);
        map.insert(99, 7u16);

        // id 61 -> 3000, id 17 -> 210, and three stray bytes that are ignored.
        let payload = [61, 0, 0xB8, 0x0B, 17, 0, 210, 0, 0xAA, 0xBB, 0xCC];
        assert_eq!(read_updates(&payload, &mut map), 2);

        assert_eq!(map[&61], 3000);
        assert_eq!(map[&17], 210);
        assert_eq!(map[&99], 7);
    }

    #[test]
    fn telemetry_has_ascending_unique_ids_in_whole_quads() {
        assert!(looks_like_telemetry(&[]));
        assert!(looks_like_telemetry(&[0, 0, 1, 0, 1, 0, 2, 0]));
        assert!(!looks_like_telemetry(&[1, 0, 0, 0, 1, 0, 0, 0]));
        assert!(!looks_like_telemetry(&[2, 0, 0, 0, 1, 0, 0, 0]));
        assert!(!looks_like_telemetry(&[1, 0, 0, 0, 5]));
    }

    // ----- try_ask ----------------------------------------------------------

    struct Scripted {
        replies: VecDeque<Vec<u8>>,
        writes: Vec<Vec<u8>>,
        discards: usize,
    }

    impl EcuTransport for Scripted {
        fn is_open(&self) -> bool {
            true
        }
        fn open(&mut self) -> Result<()> {
            Ok(())
        }
        fn close(&mut self) {}
        fn write(&mut self, data: &[u8]) -> Result<()> {
            self.writes.push(data.to_vec());
            Ok(())
        }
        fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
            let Some(next) = self.replies.pop_front() else {
                return Ok(0);
            };
            let n = next.len().min(buffer.len());
            buffer[..n].copy_from_slice(&next[..n]);
            Ok(n)
        }
        fn discard_input(&mut self) {
            self.discards += 1;
        }
    }

    fn good_reply(data: &[u8]) -> Vec<u8> {
        let mut reply = vec![OK];
        reply.extend_from_slice(data);
        let crc = maxx_protocol::crc32(&reply);
        reply.extend_from_slice(&crc.to_le_bytes());
        reply
    }

    #[test]
    fn a_lost_round_is_retried_and_counted() {
        let mut bad = good_reply(&[1, 2]);
        *bad.last_mut().unwrap() ^= 0xFF;
        let mut transport = Scripted {
            replies: VecDeque::from([vec![], bad, good_reply(&[1, 2])]),
            writes: vec![],
            discards: 0,
        };
        let asked = request(READ, TAKE, 0, 2).unwrap();
        let mut buffer = [0u8; 7];
        let mut retries = 0;
        let mut count = || retries += 1;

        let data = try_ask(
            &mut transport,
            &asked,
            &mut buffer,
            2,
            Duration::from_millis(50),
            3,
            Some(&mut count),
        )
        .unwrap();

        assert_eq!(data.unwrap(), [1, 2]);
        assert_eq!(retries, 2);
        assert_eq!(transport.discards, 2);
        assert_eq!(transport.writes, vec![asked.clone(), asked.clone(), asked]);
    }

    #[test]
    fn running_out_of_attempts_gives_none_and_zero_attempts_sends_nothing() {
        let asked = request(READ, TAKE, 0, 2).unwrap();
        let mut buffer = [0u8; 7];
        let mut transport = Scripted {
            replies: VecDeque::new(),
            writes: vec![],
            discards: 0,
        };

        assert!(
            try_ask(&mut transport, &asked, &mut buffer, 2, Duration::ZERO, 2, None)
                .unwrap()
                .is_none()
        );
        assert_eq!(transport.writes.len(), 2);
        assert_eq!(transport.discards, 1);

        transport.writes.clear();
        assert!(
            try_ask(&mut transport, &asked, &mut buffer, 2, Duration::ZERO, 0, None)
                .unwrap()
                .is_none()
        );
        assert!(transport.writes.is_empty());
    }
}
