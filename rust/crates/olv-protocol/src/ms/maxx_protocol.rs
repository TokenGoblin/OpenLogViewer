//! Ported from `MaxxProtocol.cs`. Owned by package `w1-ms-maxx`.
//!
//! The binary stream a MaxxECU speaks over Bluetooth.
//!
//! Nothing to do with the TunerStudio protocol the MegaSquirt and rusEFI paths
//! use: different framing, no signature to ask for, and no notion of reading
//! memory. It carries telemetry and nothing else, which is why a MaxxECU gets
//! gauges and logging but no calibration.
//!
//! Reverse-engineered against a MaxxECU Race; the framing below was verified on
//! 1,181 of 1,181 captured frames.
//!
//! The checksum used to be the limit here: an exhaustive search over every 16-bit
//! CRC parameterisation failed to reproduce it, so the activation and
//! subscription messages could only be replayed and the channel list could not be
//! chosen. It is identified now, and it was hiding in plain sight: see
//! [`checksum`]. The captured messages below are kept as captured anyway, because
//! a constant recorded off the wire is evidence and a constant this file computes
//! is only a claim; the tests then check that composing them reproduces the
//! recording byte for byte.
//!
//! The same framing carries over every link a MaxxECU offers, because the
//! Bluetooth and Wi-Fi gateway adds nothing of its own to an application payload:
//! it only attaches and strips a per-transport header. So a USB cable and a paired
//! Bluetooth port speak exactly what is below, with no difference for this code to
//! know about.

use olv_core::{Error, Result};

use super::maxx_frame_reader::MaxxFrameReader;

/// One message from or to a MaxxECU.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MaxxFrame {
    pub frame_type: u8,
    pub payload: Vec<u8>,
}

impl MaxxFrame {
    /// Payload length, which the frame states after the payload rather than before it.
    pub fn length(&self) -> usize {
        self.payload.len()
    }
}

/// One subscribed channel: what it is called and how to scale it.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct MaxxChannel {
    pub id: i32,
    pub name: &'static str,
    pub units: &'static str,
    pub scale: f64,
    pub is_signed: bool,
    pub digits: i32,
}

const fn channel(
    id: i32,
    name: &'static str,
    units: &'static str,
    scale: f64,
    is_signed: bool,
    digits: i32,
) -> MaxxChannel {
    MaxxChannel {
        id,
        name,
        units,
        scale,
        is_signed,
        digits,
    }
}

/// Marks the start of every frame, after the type byte.
pub const MAGIC: [u8; 3] = [0x77, 0xAA, 0x77];

/// Ends every frame, in both directions.
pub const TRAILER: [u8; 4] = [0xCC, 0x77, 0xAA, 0x44];

/// Bytes a frame carries besides its payload: type, magic, length, checksum, trailer.
pub const OVERHEAD: usize = 1 + 3 + 2 + 2 + 4;

/// Largest frame seen is 391 bytes; this is well clear of it.
pub const MAXIMUM_FRAME: usize = 512;

/// The message type carrying subscribed channel values.
pub const TELEMETRY: u8 = 0x01;

/// Asks the ECU which firmware it is running.
pub const VERSION_REQUEST: u8 = 0x15;

/// Asks for the channel names, in the newer of the two forms.
pub const NAMES_REQUEST: u8 = 0x18;

/// Tells the ECU which channels to stream.
pub const REALTIME_SETUP: u8 = 0x13;

/// The ECU's own numbering runs 0-2047: the dispatcher masks every channel id to
/// eleven bits, so a larger number is not refused, it subscribes to a different
/// channel.
pub const HIGHEST_CHANNEL_ID: i32 = 0x7FF;

/// The checksum every frame carries, in both directions.
///
/// This is the STM32's CRC peripheral rather than anything a host would reach for:
/// CRC-32/MPEG-2 (polynomial 0x04C11DB7, initialised to all ones, neither input nor
/// output reflected, no final XOR) of which only the low sixteen bits are kept. It
/// is computed over the frame from its first byte up to but not including the
/// checksum itself, so the type, the magic, the payload and the length are all
/// covered and the trailer is not.
///
/// The detail that hides it from a search over checksum algorithms is that the
/// peripheral consumes *words*, not bytes. The frame's bytes are read back four at
/// a time as little-endian 32-bit values, which reverses each group of four before
/// it reaches the polynomial, and a body that is not a whole number of words is
/// padded with zeroes to the next one. An exhaustive sweep of 16-bit CRC
/// parameterisations was never going to find that, which is why this used to be
/// recorded here as unidentified and the activation and subscription frames could
/// only be replayed.
///
/// Confirmed against every frame of both recordings (411 of 411, spanning bodies of
/// every alignment but one) and against the four frames this file had already
/// captured to replay.
pub fn checksum(body: &[u8]) -> u16 {
    crc32(body) as u16
}

/// The whole 32-bit result, of which [`checksum`] keeps the low half.
///
/// Both halves are used, by different links. The Bluetooth framing has a 16-bit
/// checksum field and throws the top away; the USB framing keeps all thirty-two. It
/// is the same arithmetic over the same bytes either way, which is the single
/// strongest sign that the two are one controller's work rather than two protocols
/// that happen to share a cable.
pub fn crc32(body: &[u8]) -> u32 {
    let mut crc: u32 = 0xFFFF_FFFF;

    for chunk in body.chunks(4) {
        let mut word: u32 = 0;
        for (b, &byte) in chunk.iter().enumerate() {
            word |= u32::from(byte) << (8 * b);
        }

        crc ^= word;

        for _ in 0..32 {
            crc = if crc & 0x8000_0000 != 0 {
                (crc << 1) ^ 0x04C1_1DB7
            } else {
                crc << 1
            };
        }
    }

    crc
}

/// Builds a frame around a payload.
///
/// What [`checksum`] buys: until it was identified the only frames that could be
/// sent were ones that had been recorded coming out of MTune, which is why the
/// channel list below is a fixed fourteen rather than a choice. Anything the ECU
/// understands can now be composed.
pub fn frame(frame_type: u8, payload: &[u8]) -> Result<Vec<u8>> {
    if payload.len() > MAXIMUM_FRAME - OVERHEAD {
        return Err(Error::Argument(format!(
            "A {} byte payload makes a frame longer than the {} bytes this protocol carries. (Parameter 'payload')",
            payload.len(),
            MAXIMUM_FRAME
        )));
    }

    let mut frame = Vec::with_capacity(OVERHEAD + payload.len());

    frame.push(frame_type);
    frame.extend_from_slice(&MAGIC);
    frame.extend_from_slice(payload);

    // The length sits after the payload rather than before it, which is what makes
    // the stream awkward to parse forwards. See MaxxFrameReader.
    frame.extend_from_slice(&(payload.len() as u16).to_le_bytes());

    let sum = checksum(&frame);
    frame.extend_from_slice(&sum.to_le_bytes());

    frame.extend_from_slice(&TRAILER);

    Ok(frame)
}

/// Builds a subscription asking for these channels, in this order.
///
/// A telemetry frame lists the values in subscription order and says nothing about
/// which channel each one is, so whoever sends this owns the decode.
pub fn subscribe(channel_ids: &[i32]) -> Result<Vec<u8>> {
    if channel_ids.is_empty() {
        return Err(Error::Argument(
            "A subscription with no channels leaves the ECU streaming nothing. (Parameter 'channelIds')".to_string(),
        ));
    }

    for &id in channel_ids {
        if !(0..=HIGHEST_CHANNEL_ID).contains(&id) {
            return Err(Error::Argument(format!(
                "Channel {id} is outside the 0\u{2013}{HIGHEST_CHANNEL_ID} the ECU numbers. It would not be refused \u{2014} the dispatcher masks it to eleven bits and subscribes to whatever that lands on. (Parameter 'channelIds')"
            )));
        }
    }

    let mut payload = Vec::with_capacity(channel_ids.len() * 2);
    for &id in channel_ids {
        payload.extend_from_slice(&(id as u16).to_le_bytes());
    }

    frame(REALTIME_SETUP, &payload)
}

/// What wakes the ECU up.
///
/// A MaxxECU that has not seen an mDash session since it was powered on accepts a
/// Bluetooth socket, reports itself connected, and sends nothing at all,
/// indefinitely. These three frames unlock it.
///
/// Replayed byte for byte. They were captured nine times across separate sessions
/// and were identical every time: no session token, no sequence number, no
/// challenge. That is what makes replaying them sound rather than a guess.
///
/// The three are, in order: ask for the channel names, ask for the firmware
/// version, and subscribe to the rev limit. Nothing in any of them is specific to
/// Bluetooth: they are ordinary commands for the main controller, which is the same
/// controller a USB cable reaches.
pub const ACTIVATION: &[u8] = &[
    0x18, 0x77, 0xAA, 0x77, 0x00, 0x00, 0x80, 0x2D, 0xCC, 0x77, 0xAA, 0x44, 0x15, 0x77, 0xAA, 0x77, 0x00, 0x00, 0x88,
    0xCE, 0xCC, 0x77, 0xAA, 0x44, 0x13, 0x77, 0xAA, 0x77, 0xE7, 0x01, 0x02, 0x00, 0xBF, 0x6C, 0xCC, 0x77, 0xAA, 0x44,
];

/// Asks for the fourteen channels below, in this order.
///
/// Activation alone only makes the ECU talk; it sends configuration and label dumps
/// and never a reading. Values arrive only for channels that were subscribed to, and
/// the reply lists them in subscription order, so this frame and [`SUBSCRIBED`]
/// define the telemetry layout between them, and disagreeing would decode every
/// channel after the disagreement as its neighbour.
pub const SUBSCRIPTION: &[u8] = &[
    0x13, 0x77, 0xAA, 0x77, 0x3D, 0x00, 0x11, 0x00, 0x12, 0x00, 0x14, 0x00, 0x15, 0x00, 0x05, 0x00, 0xE7, 0x01, 0x09,
    0x00, 0x6C, 0x00, 0x3C, 0x00, 0x40, 0x00, 0x59, 0x00, 0xF6, 0x00, 0x85, 0x03, 0x1C, 0x00, 0xEA, 0x4A, 0xCC, 0x77,
    0xAA, 0x44,
];

/// The channels [`SUBSCRIPTION`] asks for, in order.
///
/// A channel's offset in a telemetry frame is twice its position here, so the two
/// must agree; [`verify`] checks that rather than trusting it. Names, units and
/// scales are MTune's own.
pub const SUBSCRIBED: &[MaxxChannel] = &[
    channel(61, "RPM", "RPM", 1.0, false, 0),
    channel(17, "IAT", "deg C", 0.1, true, 1),
    channel(18, "CLT", "deg C", 0.1, true, 1),
    channel(20, "MAP", "kPa", 0.1, false, 1),
    channel(21, "Battery", "V", 0.01, false, 2),
    channel(5, "Lambda", "", 0.001, false, 3),
    channel(487, "Rev limit", "RPM", 1.0, false, 0),
    channel(9, "User AIN1", "", 0.1, true, 1),
    channel(108, "Error count", "", 1.0, false, 0),
    channel(60, "Ignition angle", "BTDC", 0.1, true, 1),
    channel(64, "Fuel duty", "%", 0.1, false, 1),
    channel(89, "Speed", "km/h", 0.1, false, 1),
    channel(246, "Ethanol", "%", 0.1, false, 1),
    channel(901, "Torque", "Nm", 1.0, true, 0),
];

/// Payload length of a telemetry frame for [`SUBSCRIBED`].
pub const TELEMETRY_LENGTH: usize = SUBSCRIBED.len() * 2;

/// Checks the subscription frame against the channel table it is decoded with, so a
/// mismatch is a refusal to start rather than confident nonsense.
pub fn verify() -> bool {
    let frame = SUBSCRIPTION;

    if frame.len() != OVERHEAD + TELEMETRY_LENGTH {
        return false;
    }

    let Some((sent, _)) = MaxxFrameReader::read(frame) else {
        return false;
    };
    if sent.frame_type != 0x13 || sent.length() != TELEMETRY_LENGTH {
        return false;
    }

    for (i, channel) in SUBSCRIBED.iter().enumerate() {
        let at = i * 2;
        if i32::from(u16::from_le_bytes([sent.payload[at], sent.payload[at + 1]])) != channel.id {
            return false;
        }
    }

    true
}

/// Decodes a telemetry frame into one value per subscribed channel.
///
/// Refuses a frame of the wrong length rather than reading past its payload. Type
/// 0x01 arrives with two different lengths (a two-byte heartbeat as well as the
/// reading block), so length is part of a message's identity here, not merely its
/// size.
pub fn try_decode(frame: &MaxxFrame, values: &mut [f64]) -> bool {
    if frame.frame_type != TELEMETRY || frame.length() != TELEMETRY_LENGTH {
        return false;
    }
    if values.len() < SUBSCRIBED.len() {
        return false;
    }

    for (i, channel) in SUBSCRIBED.iter().enumerate() {
        let at = [frame.payload[i * 2], frame.payload[i * 2 + 1]];

        let raw = if channel.is_signed {
            f64::from(i16::from_le_bytes(at))
        } else {
            f64::from(u16::from_le_bytes(at))
        };

        values[i] = raw * channel.scale;
    }

    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use std::path::PathBuf;

    fn capture(name: &str) -> Vec<u8> {
        let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../tests/OpenLogViewer.Tests/Fixtures")
            .join(name);
        std::fs::read(&path).unwrap_or_else(|e| panic!("fixture {}: {e}", path.display()))
    }

    fn opening() -> Vec<u8> {
        capture("maxxecu-opening.bin")
    }

    fn steady() -> Vec<u8> {
        capture("maxxecu-steady.bin")
    }

    fn read_all(stream: &[u8]) -> (Vec<MaxxFrame>, MaxxFrameReader) {
        let mut reader = MaxxFrameReader::new();
        reader.feed(stream);

        let mut frames = Vec::new();
        while let Some(frame) = reader.try_take() {
            frames.push(frame);
        }

        (frames, reader)
    }

    // ----- the recording ----------------------------------------------------

    #[test]
    fn the_capture_is_there() {
        assert_eq!(opening().len(), 10007);
        assert_eq!(steady().len(), 1200);
    }

    #[test]
    fn the_recording_yields_exactly_the_frames_it_contains() {
        // 324 frames out of 10,007 bytes with 7 left over, so all but seven bytes of
        // the recording are accounted for. Exact rather than "enough", because the
        // recording never changes and an off-by-a-few means frames are being
        // swallowed, which is how the first version of this reader failed: quietly,
        // at two thirds of them.
        let (frames, reader) = read_all(&opening());

        assert_eq!(frames.len(), 324);
        assert_eq!(reader.discarded(), 7);
    }

    #[test]
    fn no_frame_claims_more_than_the_protocol_allows() {
        for f in read_all(&opening()).0 {
            assert!(f.length() <= MAXIMUM_FRAME);
        }
    }

    #[test]
    fn the_message_types_and_counts_match_the_recording() {
        let mut counts: HashMap<u8, usize> = HashMap::new();
        for f in read_all(&opening()).0 {
            *counts.entry(f.frame_type).or_default() += 1;
        }

        assert_eq!(counts[&0x01], 160); // heartbeats and readings
        assert_eq!(counts[&0x03], 137); // indexed table dump
        assert_eq!(counts[&0x04], 1);
        assert_eq!(counts[&0x06], 8);
        assert_eq!(counts[&0x07], 1); // ASCII labels
        assert_eq!(counts[&0x09], 8);
        assert_eq!(counts[&0x10], 8);
        assert_eq!(counts[&0x11], 1); // ASCII labels
    }

    #[test]
    fn the_label_frames_really_contain_labels() {
        // 379 bytes of ASCII in one frame and 68 in another. If the payload were
        // being sliced at the wrong offset these would be noise rather than words,
        // and the 379-byte one is the frame a cruder reader loses, because it is
        // long enough to contain plenty that looks like framing.
        let frames = read_all(&opening()).0;

        let only = |t: u8| {
            let mut it = frames.iter().filter(|f| f.frame_type == t);
            let one = it.next().expect("one frame of the type");
            assert!(it.next().is_none(), "more than one frame of type {t:#x}");
            String::from_utf8_lossy(&one.payload).into_owned()
        };

        assert!(only(0x07).contains("Boostlevel"));
        assert!(only(0x11).contains("Switch"));
    }

    #[test]
    fn the_heartbeat_carries_the_documented_value() {
        // Type 0x01 with a two-byte payload is 88 13 = 5000, the rev limit, and is
        // not a reading. Decoding it as telemetry would read far past it.
        let frames = read_all(&opening()).0;
        let beat = frames.iter().find(|f| f.frame_type == 0x01 && f.length() == 2).unwrap();

        assert_eq!(i32::from(beat.payload[0]) | (i32::from(beat.payload[1]) << 8), 5000);
    }

    // ----- reassembly -------------------------------------------------------

    #[test]
    fn a_stream_split_into_awkward_pieces_parses_the_same() {
        // Bluetooth serial does not preserve message boundaries, so this is the
        // normal case rather than an edge one.
        let opening = opening();
        let whole = read_all(&opening).0;

        let mut reader = MaxxFrameReader::new();
        let mut pieces = Vec::new();

        for piece in opening.chunks(7) {
            reader.feed(piece);
            while let Some(frame) = reader.try_take() {
                pieces.push(frame);
            }
        }

        assert_eq!(whole.len(), pieces.len());
        assert_eq!(whole[0].payload, pieces[0].payload);
        assert_eq!(whole.last().unwrap().payload, pieces.last().unwrap().payload);

        // The bug this catches: bounding the buffer on every append threw away all
        // but the tail of a large read, so one big feed found 66 frames where the
        // same bytes in sevens found 324.
        assert_eq!(pieces.len(), 324);
    }

    #[test]
    fn two_frames_in_one_read_are_both_found() {
        let (frames, _) = read_all(&steady());

        assert!(frames.len() > 1);
    }

    #[test]
    fn rubbish_before_a_frame_is_skipped_and_counted() {
        let mut reader = MaxxFrameReader::new();
        reader.feed(&[0xDE, 0xAD, 0xBE, 0xEF]);
        reader.feed(&steady());

        assert!(reader.try_take().is_some());
        assert_eq!(reader.discarded(), 4);
    }

    #[test]
    fn a_partial_frame_waits_for_the_rest() {
        let mut reader = MaxxFrameReader::new();
        reader.feed(&steady()[..6]);

        assert!(reader.try_take().is_none());
    }

    #[test]
    fn a_stream_that_never_yields_a_frame_does_not_grow_forever() {
        let mut reader = MaxxFrameReader::new();

        for _ in 0..100 {
            reader.feed(&[0u8; 256]);
        }

        assert!(reader.try_take().is_none());
        assert!(reader.discarded() > 0);
    }

    // ----- the checksum -----------------------------------------------------

    fn contains(stream: &[u8], frame: &[u8]) -> bool {
        stream.windows(frame.len()).any(|w| w == frame)
    }

    #[test]
    fn every_frame_in_both_recordings_passes_its_checksum() {
        // The point of this one. The checksum was recorded here as unidentified for
        // as long as the only frames that could be sent were ones captured out of
        // MTune, so the claim that it is identified now has to be answerable by the
        // recordings rather than by argument.
        //
        // 411 frames of a real MaxxECU Race, none of them constructed here.
        let (opening_bytes, steady_bytes) = (opening(), steady());
        let (mut frames, first) = read_all(&opening_bytes);
        let (more, second) = read_all(&steady_bytes);
        frames.extend(more);

        assert_eq!(frames.len(), 411);

        // Rebuilding each frame from its type and payload alone has to land on bytes
        // that are actually in the recording: checksum, length, trailer and all.
        // Nothing here is constructed from the composer's own output.
        for f in &frames {
            let rebuilt = frame(f.frame_type, &f.payload).unwrap();
            assert!(
                contains(&opening_bytes, &rebuilt) || contains(&steady_bytes, &rebuilt),
                "A composed type 0x{:02X} frame of {} bytes is not in the recording.",
                f.frame_type,
                f.length()
            );
        }

        // And the reader, which now checks the checksum before accepting a frame,
        // threw none of them out.
        assert_eq!(first.rejected(), 0);
        assert_eq!(second.rejected(), 0);
    }

    #[test]
    fn a_composed_frame_carries_the_captured_checksum() {
        // The three messages this file replays, each with the checksum it was
        // captured carrying. Composing them has to land on the same bytes, or
        // "identified" means only that a formula was found that fits nothing.
        // (The C# theory: ask for the channel names, ask for the firmware version,
        // subscribe to the rev limit.)
        for (t, payload, expected) in [
            (0x18u8, &[][..], 0x2D80u16),
            (0x15, &[][..], 0xCE88),
            (0x13, &[0xE7, 0x01][..], 0x6CBF),
        ] {
            let f = frame(t, payload).unwrap();
            let n = f.len();
            assert_eq!(
                u16::from(f[n - 6]) | (u16::from(f[n - 5]) << 8),
                expected,
                "type {t:#x}"
            );
        }
    }

    #[test]
    fn composing_the_activation_reproduces_the_capture() {
        // The activation was captured nine times and is replayed verbatim. If
        // composing it lands on the same 38 bytes then the composer is right about a
        // real message rather than merely self-consistent.
        let mut composed = frame(NAMES_REQUEST, &[]).unwrap();
        composed.extend(frame(VERSION_REQUEST, &[]).unwrap());
        composed.extend(subscribe(&[487]).unwrap());

        assert_eq!(ACTIVATION, composed.as_slice());
        assert_eq!(composed.len(), 38);
    }

    #[test]
    fn composing_the_subscription_reproduces_the_capture() {
        // The same, for the fourteen-channel subscription, and this is the one that
        // matters, because it is the message whose channel list was fixed by what had
        // been recorded. Reproducing it exactly is what says the list can now be
        // chosen.
        let ids: Vec<i32> = SUBSCRIBED.iter().map(|c| c.id).collect();
        let composed = subscribe(&ids).unwrap();

        assert_eq!(SUBSCRIPTION, composed.as_slice());
    }

    #[test]
    fn a_frame_with_a_broken_checksum_is_refused() {
        let mut f = frame(0x01, &[1, 2, 3, 4]).unwrap();
        let n = f.len();
        f[n - 6] ^= 0xFF;

        assert!(MaxxFrameReader::read(&f).is_none());

        let mut reader = MaxxFrameReader::new();
        reader.feed(&f);

        assert!(reader.try_take().is_none());
        assert_eq!(reader.rejected(), 1);
    }

    #[test]
    fn a_channel_outside_the_ecus_numbering_is_refused() {
        // Not refused by the ECU: the dispatcher masks a channel id to eleven bits,
        // so 2048 subscribes to channel 0 and reports it under whatever name the
        // caller had in mind for 2048.
        assert!(matches!(subscribe(&[2048]), Err(Error::Argument(_))));
        assert!(matches!(subscribe(&[-1]), Err(Error::Argument(_))));
    }

    // ----- the subscription and its decode ----------------------------------

    #[test]
    fn the_subscription_matches_the_table_it_is_decoded_with() {
        // These two define the telemetry layout between them. Disagreeing would
        // decode every channel after the disagreement as its neighbour: well formed,
        // plausible, wrong.
        assert!(verify());
    }

    #[test]
    fn the_subscription_frame_is_well_formed() {
        let (f, used) = MaxxFrameReader::read(SUBSCRIPTION).unwrap();

        assert_eq!(f.frame_type, 0x13);
        assert_eq!(f.length(), 28);
        assert_eq!(used, SUBSCRIPTION.len());
    }

    #[test]
    fn the_activation_is_three_complete_frames() {
        let (frames, reader) = read_all(ACTIVATION);

        assert_eq!(frames.len(), 3);
        assert_eq!(reader.discarded(), 0);
        assert_eq!(
            frames.iter().map(|f| f.frame_type).collect::<Vec<_>>(),
            [0x18, 0x15, 0x13]
        );
    }

    fn put(payload: &mut [u8], index: usize, value: u16) {
        payload[index * 2] = value as u8;
        payload[index * 2 + 1] = (value >> 8) as u8;
    }

    #[test]
    fn telemetry_decodes_to_its_channels() {
        // A frame built to the documented layout: RPM 3000, IAT 21.0, CLT 85.0, MAP
        // 101.3, battery 13.80, lambda 0.980.
        let mut payload = vec![0u8; TELEMETRY_LENGTH];
        put(&mut payload, 0, 3000);
        put(&mut payload, 1, 210);
        put(&mut payload, 2, 850);
        put(&mut payload, 3, 1013);
        put(&mut payload, 4, 1380);
        put(&mut payload, 5, 980);

        let mut values = [0.0f64; 14];
        assert!(try_decode(
            &MaxxFrame {
                frame_type: 0x01,
                payload
            },
            &mut values
        ));

        assert_eq!(values[0], 3000.0);
        assert!((values[1] - 21.0).abs() < 1e-4);
        assert!((values[2] - 85.0).abs() < 1e-4);
        assert!((values[3] - 101.3).abs() < 1e-4);
        assert!((values[4] - 13.80).abs() < 1e-4);
        assert!((values[5] - 0.980).abs() < 1e-4);
    }

    #[test]
    fn a_signed_channel_goes_negative() {
        // Intake air temperature below freezing, and ignition angle after top dead
        // centre. Read unsigned, -10 C becomes 6553.5.
        let mut payload = vec![0u8; TELEMETRY_LENGTH];
        put(&mut payload, 1, (-100i16) as u16);
        put(&mut payload, 9, (-50i16) as u16);

        let mut values = [0.0f64; 14];
        assert!(try_decode(
            &MaxxFrame {
                frame_type: 0x01,
                payload
            },
            &mut values
        ));

        assert!((values[1] - -10.0).abs() < 1e-4);
        assert!((values[9] - -5.0).abs() < 1e-4);
    }

    #[test]
    fn the_short_heartbeat_is_not_decoded_as_telemetry() {
        // Type 0x01 arrives with two lengths, and the two-byte one is not a reading.
        // Length is part of a message's identity here.
        let mut values = [0.0f64; 14];

        assert!(!try_decode(
            &MaxxFrame {
                frame_type: 0x01,
                payload: vec![0x88, 0x13]
            },
            &mut values
        ));
    }

    #[test]
    fn another_message_type_is_not_decoded_as_telemetry() {
        let mut values = [0.0f64; 14];

        assert!(!try_decode(
            &MaxxFrame {
                frame_type: 0x03,
                payload: vec![0u8; TELEMETRY_LENGTH]
            },
            &mut values
        ));
    }

    // ----- byte-level additions ---------------------------------------------

    #[test]
    fn a_short_values_buffer_is_refused() {
        let mut values = [0.0f64; 13];
        assert!(!try_decode(
            &MaxxFrame {
                frame_type: 0x01,
                payload: vec![0u8; TELEMETRY_LENGTH]
            },
            &mut values
        ));
    }

    #[test]
    fn the_request_frames_are_the_captured_bytes() {
        assert_eq!(
            frame(NAMES_REQUEST, &[]).unwrap(),
            [0x18, 0x77, 0xAA, 0x77, 0x00, 0x00, 0x80, 0x2D, 0xCC, 0x77, 0xAA, 0x44]
        );
        assert_eq!(
            frame(VERSION_REQUEST, &[]).unwrap(),
            [0x15, 0x77, 0xAA, 0x77, 0x00, 0x00, 0x88, 0xCE, 0xCC, 0x77, 0xAA, 0x44]
        );
        assert_eq!(
            subscribe(&[487]).unwrap(),
            [
                0x13, 0x77, 0xAA, 0x77, 0xE7, 0x01, 0x02, 0x00, 0xBF, 0x6C, 0xCC, 0x77, 0xAA, 0x44
            ]
        );
    }

    #[test]
    fn the_highest_channel_and_an_empty_list_are_judged_at_the_edge() {
        assert!(subscribe(&[0, 2047]).is_ok());
        assert!(matches!(subscribe(&[]), Err(Error::Argument(_))));
        assert_eq!(&subscribe(&[2047]).unwrap()[4..6], [0xFF, 0x07]);
    }

    #[test]
    fn the_largest_payload_fits_and_one_more_does_not() {
        let biggest = MAXIMUM_FRAME - OVERHEAD;
        assert_eq!(frame(0x01, &vec![0u8; biggest]).unwrap().len(), MAXIMUM_FRAME);
        assert!(frame(0x01, &vec![0u8; biggest + 1]).is_err());
    }

    #[test]
    fn the_checksum_pads_a_partial_word_with_zeroes() {
        // Words are little-endian and a short tail is zero-padded, so a trailing
        // zero byte changes nothing only when it completes the word.
        assert_eq!(crc32(&[]), 0xFFFF_FFFF);
        assert_eq!(crc32(&[1, 2, 3]), crc32(&[1, 2, 3, 0]));
        assert_ne!(crc32(&[1, 2, 3, 4]), crc32(&[4, 3, 2, 1]));
    }
}
