//! Ported from `SniperHefi.cs`. Owned by package `w1-sniper`.
//!
//! Layer 3 of `SNIPER_ECU_CLIENT_SPEC.md`: the HEFI application protocol riding on top of
//! the CAN frames `sniper_can` addresses: building an outbound read/write message,
//! reassembling a block reply, and telling a read opcode from a write one.
//!
//! **Nothing here has been run against real hardware.** See [`UNCONFIRMED_MARKER`], the
//! open/close block-transfer framing and the calibration-offset map for exactly which parts
//! the spec itself calls unconfirmed.

/// Table for the standard CRC-32 (poly 0xEDB88320 reflected, init 0xFFFFFFFF, final invert;
/// the same algorithm `zlib.crc32`/PKZIP use), which spec section 3.1 calls for.
///
/// Deliberately separate from the MaxxECU CRC-32/MPEG-2 (poly 0x04C11DB7, no reflection, no
/// final invert): a different algorithm that happens to share a name.
const CRC_TABLE: [u32; 256] = build_crc_table();

const fn build_crc_table() -> [u32; 256] {
    let mut table = [0u32; 256];
    let mut i = 0;
    while i < 256 {
        let mut c = i as u32;
        let mut bit = 0;
        while bit < 8 {
            c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
            bit += 1;
        }
        table[i] = c;
        i += 1;
    }
    table
}

/// The standard CRC-32 of `data`.
pub fn standard_crc32(data: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for &b in data {
        crc = CRC_TABLE[((crc ^ u32::from(b)) & 0xFF) as usize] ^ (crc >> 8);
    }
    !crc
}

/// The constant word every outbound message opens with. Spec section 3.1: the spec names it
/// `DAT_0054da48` in the decompiled app but could not read its value without a hardware
/// capture. **UNCONFIRMED [CONFIRM ON HW]**, and this remains true even against the
/// reference client (`sniper_can.py:build_hefi_message`): it also leaves this at 0 with the
/// same comment. Zero is a documented placeholder two independent readings of the decompiled
/// app agree on using, not a value anyone has confirmed the real dongle accepts.
pub const UNCONFIRMED_MARKER: u32 = 0;

/// Recognises the first frame of a block reply. Spec section 3.2: the reassembler knows a
/// transfer has begun by this `htonl` marker value, and the dword right after it is the
/// total reply length. Verified in the spec.
pub const BLOCK_REPLY_MARKER: u32 = 0xF249_3722;

/// Builds one outbound HEFI message; replicates `FUN_00492e10`, section 3.1, checked against
/// the reference implementation (`sniper_can.py:build_hefi_message`): assemble native
/// (little) endian, byte-swap whole 4-byte words only, CRC the rest. Verified for the whole
/// shape; UNCONFIRMED only for whether [`UNCONFIRMED_MARKER`] is the real marker value.
///
/// Returns the complete big-endian, CRC'd buffer, **not yet fragmented** into CAN frames; see
/// [`fragment_for_send`].
pub fn build_message(cmd0: u32, cmd1: u32, cmd2: u32, payload: &[u8]) -> Vec<u8> {
    const HEADER_WORDS: usize = 4; // marker, cmd0, cmd1, cmd2
    let mut buffer = vec![0u8; HEADER_WORDS * 4 + payload.len()];

    buffer[0..4].copy_from_slice(&UNCONFIRMED_MARKER.to_le_bytes());
    buffer[4..8].copy_from_slice(&cmd0.to_le_bytes());
    buffer[8..12].copy_from_slice(&cmd1.to_le_bytes());
    buffer[12..16].copy_from_slice(&cmd2.to_le_bytes());
    buffer[16..].copy_from_slice(payload);

    // Step 2: byte-swap every *whole* 4-byte word in place; the wire is big-endian. The
    // reference swaps `range(0, len(buf) - (len(buf) % 4), 4)`: any trailing partial word (a
    // payload whose length is not a multiple of 4) is left exactly as written rather than
    // refused or padded.
    let (words, _partial_tail) = buffer.as_chunks_mut::<4>();
    for word in words {
        word.reverse();
    }

    // Step 3: CRC-32 over the buffer excluding the first (marker) word, appended big-endian;
    // matches `zlib.crc32(bytes(buf[4:]))`.
    let crc = standard_crc32(&buffer[4..]);
    buffer.extend_from_slice(&crc.to_be_bytes());
    buffer
}

/// Splits a built message into 8-byte CAN-frame payloads; replicates `sniper_can.py:fragment`
/// exactly: a plain 8-byte chunker over the whole built message (marker through CRC), last
/// chunk short if the length is not a multiple of 8, nothing more.
///
/// **An unresolved disagreement between the two reverse-engineering sources, not a settled
/// fact.** Both the main spec (section 3.1) and `HEFI_CAN_protocol.md`'s prose describe the
/// app as "opening with a length/handshake frame and closing with a zero-length frame" around
/// the fragmented body, but `sniper_can.py`'s own `fragment()` implements no such thing, and
/// `HEFI_CAN_protocol.md`'s closing note says its pure-protocol helpers were unit-tested
/// offline. This follows the reference; a capture could still show an extra framing step this
/// omits. **[CONFIRM ON HW]** either way.
pub fn fragment_for_send(message: &[u8]) -> Vec<Vec<u8>> {
    message.chunks(8).map(<[u8]>::to_vec).collect()
}

/// Reassembles a Sniper block-read reply from its CAN-frame fragments; replicates
/// `FUN_004d6310`, spec section 3.2.
///
/// One instance per in-flight reply: feed it frame payloads as they arrive via
/// [`offer`](Self::offer); [`is_complete`](Self::is_complete) turns true once
/// [`result`](Self::result) has every expected byte.
#[derive(Debug, Default)]
pub struct SniperBlockReassembler {
    buffer: Vec<u8>,
    expected_length: Option<i32>,
    started: bool,
    complete: bool,
}

impl SniperBlockReassembler {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn is_complete(&self) -> bool {
        self.complete
    }

    /// The reassembled bytes, valid once [`is_complete`](Self::is_complete) is true.
    pub fn result(&self) -> Vec<u8> {
        self.buffer.clone()
    }

    fn has_expected(&self, len: i32) -> bool {
        self.buffer.len() as i64 >= i64::from(len)
    }

    /// Feeds one CAN frame's payload into the reassembly. Section 3.2: the first 8-byte frame
    /// carries the marker and a length dword (`expected_len = payload_len_dword - 4`);
    /// following frames' payloads concatenate until that many bytes have arrived; a zero-dlc
    /// frame flushes/pads.
    pub fn offer(&mut self, payload: &[u8]) {
        if self.complete {
            return;
        }

        if payload.is_empty() {
            // "A zero-dlc frame flushes/pads": the spec does not say pads with what or to
            // where, so this is treated as end-of-transfer when a length is already known,
            // and otherwise ignored.
            if self.started
                && let Some(expected) = self.expected_length
                && self.has_expected(expected)
            {
                self.complete = true;
            }
            return;
        }

        if !self.started {
            self.started = true;

            if payload.len() == 8 {
                let marker = u32::from_be_bytes([payload[0], payload[1], payload[2], payload[3]]);
                let length_dword = u32::from_be_bytes([payload[4], payload[5], payload[6], payload[7]]);

                if marker == BLOCK_REPLY_MARKER {
                    // "expected_len = payload_len_dword - 4" per section 3.2.
                    self.expected_length = Some((length_dword as i32).wrapping_sub(4));
                    return;
                }
            }

            // The first frame did not look like the documented marker+length header. Treated
            // as ordinary payload rather than thrown away, since the marker itself was never
            // captured against a real reply.
        }

        self.buffer.extend_from_slice(payload);

        if let Some(len) = self.expected_length
            && self.has_expected(len)
        {
            self.complete = true;
        }
    }
}

/// The command taxonomy dispatched by "DoQueue" (`FUN_0048f040`), spec sections 3.3-3.4:
/// telling a read opcode from a write one, and where a write lands in the calibration image.
pub mod sniper_opcodes {
    /// The fixed pattern every per-table opcode triplet shares once both the table nibble
    /// (bits 4-7) and the direction nibble (bits 0-3) are masked off: `0x40000a__`, 3.3.
    const TRIPLET_BASE: u32 = 0x4000_0a00;

    /// True for a read opcode: `0x40000a_1`/`0x40000a_3` in the per-table triplet family (the
    /// table nibble varies per table and is ignored; only the direction nibble is checked).
    /// Outbound payload for one of these is NULL; the ECU streams the block back. 3.3.
    pub fn is_read(opcode: u32) -> bool {
        (opcode & 0xFFFF_FF00) == TRIPLET_BASE && matches!(opcode & 0x0F, 0x01 | 0x03)
    }

    /// True for a write opcode: `0x40000a_2`. Outbound payload is the calibration region
    /// being pushed. 3.3.
    pub fn is_write(opcode: u32) -> bool {
        (opcode & 0xFFFF_FF00) == TRIPLET_BASE && (opcode & 0x0F) == 0x02
    }

    /// The write opcode to calibration-image byte offset map, 3.3. Verified for the pairs
    /// themselves; UNCONFIRMED **[CONFIRM ON HW]** for what each region actually is.
    pub const WRITE_OPCODE_TO_IMAGE_OFFSET: [(u32, i32); 8] = [
        (0x4000_0a02, 0x040c),
        (0x4000_0a12, 0x2a8c),
        (0x4000_0a22, 0x3c54),
        (0x4000_0a42, 0x3d08),
        (0x4000_0a62, 0x3e90),
        (0x4000_0a72, 0x40a4),
        (0x4000_0a92, 0x4830),
        (0x4000_0ac2, 0x4f98),
    ];

    /// Looks a write opcode up in [`WRITE_OPCODE_TO_IMAGE_OFFSET`].
    pub fn write_opcode_to_image_offset(opcode: u32) -> Option<i32> {
        WRITE_OPCODE_TO_IMAGE_OFFSET
            .iter()
            .find(|(op, _)| *op == opcode)
            .map(|(_, offset)| *offset)
    }

    /// Where a block reply lands in the "current tune" cache, 3.4: the same offsets as the
    /// write map plus one more (`+0xd8`) that has no corresponding write opcode in 3.3's
    /// table. The missing write counterpart is left unexplained by the spec.
    pub const REPLY_CACHE_OFFSETS: [i32; 9] = [0xd8, 0x40c, 0x2a8c, 0x3c54, 0x3d08, 0x3e90, 0x40a4, 0x4830, 0x4f98];
}

#[cfg(test)]
mod tests {
    use super::sniper_opcodes as ops;
    use super::*;

    /// Section 3.1 step 3 says the CRC-32 is "standard... i.e. zlib.crc32": checked against
    /// the algorithm's own canonical check value (CRC of ASCII "123456789" is 0xCBF43926),
    /// independent of anything else in this codebase.
    #[test]
    fn standard_crc32_matches_its_own_canonical_check_value() {
        assert_eq!(standard_crc32(b"123456789"), 0xCBF4_3926);
    }

    /// Section 3.1 step 3: the CRC covers the buffer *excluding the first (marker) word*,
    /// stored big-endian as the message's last four bytes.
    #[test]
    fn build_message_appends_a_crc_over_everything_after_the_marker() {
        let message = build_message(0x0102_0304, 0x0506_0708, 0x090a_0b0c, &[]);
        let n = message.len();
        let stored = u32::from_be_bytes(message[n - 4..].try_into().unwrap());
        let expected = standard_crc32(&message[4..n - 4]);
        assert_eq!(stored, expected);
    }

    #[test]
    fn build_message_byte_swaps_every_word_so_the_wire_holds_big_endian() {
        // The net effect of "assemble, then byte-swap every word" is that cmd0 lands on the
        // wire as its plain big-endian representation: the second word, after the marker.
        let message = build_message(0x0102_0304, 0, 0, &[]);
        assert_eq!(&message[4..8], &[0x01, 0x02, 0x03, 0x04]);
    }

    /// Confirmed against `sniper_can.py:build_hefi_message`: a trailing partial word is left
    /// exactly as written, not refused and not padded.
    #[test]
    fn build_message_leaves_a_trailing_partial_word_unswapped() {
        let message = build_message(0, 0, 0, &[0xAA, 0xBB, 0xCC]);
        // 4 header words (16 bytes) + 3-byte payload = 19 bytes before the CRC. The payload's
        // odd 3 bytes are the last, non-whole word and must be untouched by the swap pass.
        assert_eq!(&message[16..19], &[0xAA, 0xBB, 0xCC]);
    }

    #[test]
    fn build_message_layout_is_marker_then_three_command_words_then_payload() {
        let message = build_message(0x1111_1111, 0x2222_2222, 0x3333_3333, &[0xAA, 0xBB, 0xCC, 0xDD]);
        // 4 header words + 1 payload word + 1 CRC word = 24 bytes.
        assert_eq!(message.len(), 24);
    }

    /// Whole messages taken byte for byte from the C# (rust/oracle/expected/w1-sniper/wire.json),
    /// so the placeholder marker, the swap and the CRC are pinned to the C#, not to this code.
    #[test]
    fn build_message_matches_bytes_the_csharp_produced() {
        let hex = |s: &str| -> Vec<u8> {
            (0..s.len())
                .step_by(2)
                .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
                .collect()
        };
        assert_eq!(
            build_message(0, 0, 0, &[]),
            hex("000000000000000000000000000000007BD5C66F")
        );
        assert_eq!(
            build_message(0x4000_0A01, 0, 0x1008, &[]),
            hex("0000000040000A0100000000000010080FF66F76")
        );
        assert_eq!(
            build_message(0, 0, 0, &[0xAA]),
            hex("00000000000000000000000000000000AA39770C74")
        );
    }

    #[test]
    fn fragment_for_send_is_a_plain_eight_byte_chunker_like_the_reference_implementation() {
        // Matches sniper_can.py:fragment exactly: no length-header frame, no zero-length
        // close frame.
        let message = [0u8; 20];
        let frames = fragment_for_send(&message);
        assert_eq!(frames.len(), 3); // 8 + 8 + 4
        assert_eq!(frames[0].len(), 8);
        assert_eq!(frames[1].len(), 8);
        assert_eq!(frames[2].len(), 4);
    }

    #[test]
    fn fragment_for_send_splits_the_body_into_eight_byte_chunks() {
        let message: Vec<u8> = (0..17).collect(); // 17 bytes: 8+8+1
        let frames = fragment_for_send(&message);
        assert_eq!(frames.len(), 3);
        assert_eq!(frames[0].len(), 8);
        assert_eq!(frames[1].len(), 8);
        assert_eq!(frames[2], vec![16]);
    }

    #[test]
    fn reassembler_completes_once_the_declared_length_arrives() {
        let mut reassembler = SniperBlockReassembler::new();

        let mut header = [0u8; 8];
        header[..4].copy_from_slice(&BLOCK_REPLY_MARKER.to_be_bytes());
        header[4..].copy_from_slice(&(4u32 + 10).to_be_bytes()); // expected_len = 10

        reassembler.offer(&header);
        assert!(!reassembler.is_complete());

        reassembler.offer(&[1, 2, 3, 4, 5, 6, 7, 8]);
        assert!(!reassembler.is_complete());

        reassembler.offer(&[9, 10]);
        assert!(reassembler.is_complete());
        let result = reassembler.result();
        assert_eq!(result.len(), 10);
        assert_eq!(result[0], 1);
        assert_eq!(result[9], 10);
    }

    #[test]
    fn reassembler_treats_a_non_matching_first_frame_as_ordinary_payload() {
        let mut reassembler = SniperBlockReassembler::new();

        // Eight bytes that do not start with the block-reply marker.
        reassembler.offer(&[0, 0, 0, 0, 0, 0, 0, 0]);
        reassembler.offer(&[]); // zero-dlc flush with no known expected length: no-op

        assert!(!reassembler.is_complete());
        assert_eq!(reassembler.result().len(), 8);
    }

    #[test]
    fn is_read_recognises_only_read_direction_opcodes() {
        assert!(ops::is_read(0x4000_0a01));
        assert!(ops::is_read(0x4000_0a03));
        assert!(ops::is_read(0x4000_0a11)); // different table nibble, same direction
        assert!(!ops::is_read(0x4000_0a02));
        assert!(!ops::is_read(0x4000_0a00));
        assert!(!ops::is_read(0x5000_0a01)); // wrong high bytes entirely
    }

    #[test]
    fn is_write_recognises_only_write_direction_opcodes() {
        assert!(ops::is_write(0x4000_0a02));
        assert!(ops::is_write(0x4000_0a62));
        assert!(!ops::is_write(0x4000_0a01));
        assert!(!ops::is_write(0x4000_0a03));
    }

    #[test]
    fn every_write_opcode_in_the_specs_table_round_trips() {
        // The eight pairs section 3.3 gives verbatim.
        let pairs: [(u32, i32); 8] = [
            (0x4000_0a02, 0x040c),
            (0x4000_0a62, 0x3e90),
            (0x4000_0a12, 0x2a8c),
            (0x4000_0a72, 0x40a4),
            (0x4000_0a22, 0x3c54),
            (0x4000_0a92, 0x4830),
            (0x4000_0a42, 0x3d08),
            (0x4000_0ac2, 0x4f98),
        ];
        for (opcode, offset) in pairs {
            assert!(ops::is_write(opcode));
            assert_eq!(ops::write_opcode_to_image_offset(opcode), Some(offset));
        }
        assert_eq!(ops::WRITE_OPCODE_TO_IMAGE_OFFSET.len(), pairs.len());
        assert_eq!(ops::write_opcode_to_image_offset(0x4000_0a01), None);
    }
}
