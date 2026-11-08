//! Ported from `MaxxFrameReader.cs`. Owned by package `w1-ms-maxx`.
//!
//! Reassembles MaxxECU frames from a byte stream.
//!
//! Needed because Bluetooth serial does not preserve message boundaries: a 34-byte
//! frame can arrive as any number of reads of any size, and two frames can arrive in
//! one. Nothing may assume that one read is one message.
//!
//! The frame is awkward to parse forwards, because it states its payload length
//! *after* the payload:
//!
//! `type(1) | 77 AA 77 | payload(N) | N as u16 LE | checksum(2) | CC 77 AA 44`
//!
//! So the reader finds a trailer, reads the length from just before it, and checks
//! that the frame that implies starts where a header actually is. The trailer alone
//! is not enough (a payload may legitimately contain those four bytes) and it is the
//! length agreeing with the header position that makes the framing unambiguous.

use super::maxx_protocol::{self, MAGIC, MAXIMUM_FRAME, MaxxFrame, OVERHEAD, TRAILER};

#[derive(Debug, Default)]
pub struct MaxxFrameReader {
    buffer: Vec<u8>,
    discarded: i32,
    rejected: i32,
}

impl MaxxFrameReader {
    pub fn new() -> Self {
        Self::default()
    }

    /// Bytes thrown away as unparseable, for reporting a link that is out of step.
    pub fn discarded(&self) -> i32 {
        self.discarded
    }

    /// Candidate frames that framed correctly and failed their checksum.
    ///
    /// Not the same thing as a discarded byte. Some of these are a trailer occurring
    /// inside a payload with a length that happened to line up, which is the case
    /// this rejects and is harmless; a steady stream of them instead means the link
    /// is corrupting data, which is worth being able to say.
    pub fn rejected(&self) -> i32 {
        self.rejected
    }

    /// Adds received bytes to whatever is already waiting.
    ///
    /// Appends and nothing else. Capping the buffer here looks like prudent
    /// housekeeping and silently destroys data: one large read then discards
    /// everything but its tail before a single frame has been taken out of it.
    /// Bounding belongs where the reader gives up on finding a frame, which is the
    /// only place it can tell the difference.
    pub fn feed(&mut self, data: &[u8]) {
        self.buffer.extend_from_slice(data);
    }

    /// Takes the next complete frame, or returns `None` when more bytes are
    /// needed. Call until it returns `None`.
    pub fn try_take(&mut self) -> Option<MaxxFrame> {
        loop {
            // Anchor on a header rather than on a trailer. Searching backwards from
            // the first trailer looks simpler and is wrong: a payload may contain
            // those four bytes, and reading a length from in front of a false
            // trailer occasionally lands on something that passes for a header,
            // which swallows real frames rather than failing. Against a recording
            // of a real ECU that cost two thirds of them.
            let Some(start) = self.index_of_header() else {
                // Nothing that could begin a frame. Keep only the tail a header
                // might straddle, so a stream of noise cannot grow without bound.
                if self.buffer.len() > 3 {
                    let drop = self.buffer.len() - 3;
                    self.discarded += drop as i32;
                    self.buffer.drain(..drop);
                }

                return None;
            };

            if start > 0 {
                self.discarded += start as i32;
                self.buffer.drain(..start);
            }

            // The trailer of a frame starting here sits where the length field
            // agrees: for a payload of N the frame is N + 12 long, its trailer
            // begins at N + 8, and the length is the two bytes before that.
            let mut found: Option<usize> = None;

            let mut t = OVERHEAD - 4;
            while t + 4 <= self.buffer.len() {
                if !self.is_trailer_at(t) {
                    t += 1;
                    continue;
                }

                let declared = usize::from(self.buffer[t - 4]) | (usize::from(self.buffer[t - 3]) << 8);
                if declared != t - 8 {
                    t += 1;
                    continue;
                }

                // And the checksum agrees. Magic, a trailer and a length that lines
                // up already make the framing all but unambiguous, but "all but" is
                // the whole difficulty here: a payload may contain any of those
                // bytes, and a frame accepted at the wrong offset is decoded rather
                // than refused. The checksum is what turns that from unlikely into
                // arithmetic.
                let sum = maxx_protocol::checksum(&self.buffer[..t - 2]);
                if usize::from(sum) != (usize::from(self.buffer[t - 2]) | (usize::from(self.buffer[t - 1]) << 8)) {
                    self.rejected += 1;
                    t += 1;
                    continue;
                }

                found = Some(t);
                break;
            }

            let Some(found) = found else {
                // Either the rest has not arrived, or this header was a false one
                // inside a payload. Only give up on it once there is more than a
                // whole frame of evidence.
                if self.buffer.len() <= MAXIMUM_FRAME + OVERHEAD {
                    return None;
                }

                self.discarded += 1;
                self.buffer.drain(..1);
                continue;
            };

            let length = found - 8;
            let payload = self.buffer[4..4 + length].to_vec();

            let frame = MaxxFrame {
                frame_type: self.buffer[0],
                payload,
            };
            self.buffer.drain(..found + 4);

            return Some(frame);
        }
    }

    /// The first position whose next three bytes are the magic.
    fn index_of_header(&self) -> Option<usize> {
        let mut i = 0;
        while i + 4 <= self.buffer.len() {
            if self.buffer[i + 1..i + 4] == MAGIC {
                return Some(i);
            }
            i += 1;
        }

        None
    }

    fn is_trailer_at(&self, at: usize) -> bool {
        at + TRAILER.len() <= self.buffer.len() && self.buffer[at..at + TRAILER.len()] == TRAILER
    }

    /// Reads a single frame from a complete buffer, for a fixed message. Returns
    /// the frame and how many bytes it used (all of `data`), or `None` when `data`
    /// is not exactly one good frame.
    pub fn read(data: &[u8]) -> Option<(MaxxFrame, usize)> {
        if data.len() < OVERHEAD {
            return None;
        }

        let n = data.len();
        let declared = usize::from(u16::from_le_bytes([data[n - 8], data[n - 7]]));
        if declared + OVERHEAD != n {
            return None;
        }

        if data[1..4] != MAGIC {
            return None;
        }
        if data[n - 4..] != TRAILER {
            return None;
        }

        if maxx_protocol::checksum(&data[..n - 6]) != u16::from_le_bytes([data[n - 6], data[n - 5]]) {
            return None;
        }

        Some((
            MaxxFrame {
                frame_type: data[0],
                payload: data[4..4 + declared].to_vec(),
            },
            n,
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_false_trailer_inside_a_payload_is_not_a_frame() {
        // A payload that itself contains the trailer bytes must come back whole.
        let mut payload = vec![9, 9];
        payload.extend_from_slice(&TRAILER);
        payload.extend_from_slice(&[7, 7]);
        let bytes = maxx_protocol::frame(0x03, &payload).unwrap();

        let mut reader = MaxxFrameReader::new();
        reader.feed(&bytes);
        let frame = reader.try_take().unwrap();

        assert_eq!(frame.payload, payload);
        assert_eq!(reader.discarded(), 0);
        assert!(reader.try_take().is_none());
    }

    #[test]
    fn read_refuses_a_buffer_that_is_not_exactly_one_frame() {
        let bytes = maxx_protocol::frame(0x01, &[1, 2]).unwrap();
        assert!(MaxxFrameReader::read(&bytes[..bytes.len() - 1]).is_none());

        let mut two = bytes.clone();
        two.extend_from_slice(&bytes);
        assert!(MaxxFrameReader::read(&two).is_none());
        assert!(MaxxFrameReader::read(&[0u8; 5]).is_none());
        assert_eq!(MaxxFrameReader::read(&bytes).unwrap().1, bytes.len());
    }

    #[test]
    fn noise_keeps_only_a_header_sized_tail() {
        let mut reader = MaxxFrameReader::new();
        reader.feed(&[0xAA; 10]);
        assert!(reader.try_take().is_none());
        assert_eq!(reader.discarded(), 7);
    }
}
