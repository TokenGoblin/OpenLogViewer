//! Ported from `MsProtocol.cs`. Owned by package `w1-ms-maxx`.
//!
//! The serial protocol MegaSquirt 2 and 3 speak to TunerStudio.
//!
//! Every request is framed as a length, a payload and a CRC32; every reply comes
//! back as a length, a status byte, the data and a CRC32. The framing is worth
//! having even on a USB cable: it is the only thing that catches a truncated or
//! corrupted block, which is the normal failure on a Bluetooth link.
//!
//! The commands named here are the reads: a signature, a version, a realtime
//! block. Write and burn are absent not because they are unsupported but because
//! they are not fixed: each firmware declares its own in its INI, and
//! `EcuConnection` parses them from there rather than assuming them. The care they
//! need is real either way: a wrong byte written to a running engine is not a bug
//! that can be undone.

use std::fmt;
use std::time::Duration;

use olv_core::model::WireFailureKind;
use olv_core::{Error, Result};

/// Raised when a reply is missing, malformed, or fails its checksum
/// (`EcuProtocolException`).
///
/// Carries what the C# exception carries beyond its message, which
/// [`olv_core::Error::Protocol`] cannot: callers that retry need `refused`, and
/// the wire trace needs `failure_kind`. Converts into `Error::Protocol` where
/// that is all that is wanted.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EcuProtocolError {
    pub message: String,
    /// True when the ECU understood the request and declined it, rather than the
    /// reply going astray. Worth telling apart: a refusal repeated is refused
    /// again, so retrying one only spends the timeout.
    pub refused: bool,
    /// Which of the ways a wire exchange fails this one was, for the wire trace
    /// rather than for a person reading the message. Left at its default for
    /// errors raised before a request is even attempted (a page too small, a
    /// firmware with no write command): those never reach the wire trace.
    pub failure_kind: WireFailureKind,
}

impl EcuProtocolError {
    /// The C#'s `new EcuProtocolException(message)`: not a refusal, no failure kind.
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            refused: false,
            failure_kind: WireFailureKind::None,
        }
    }

    fn with_kind(message: String, failure_kind: WireFailureKind) -> Self {
        Self {
            message,
            refused: false,
            failure_kind,
        }
    }
}

impl fmt::Display for EcuProtocolError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for EcuProtocolError {}

impl From<EcuProtocolError> for Error {
    fn from(e: EcuProtocolError) -> Self {
        Error::Protocol(e.message)
    }
}

/// Somewhere to send bytes and read them back: a serial port, or a fake in a test
/// (`IEcuTransport`). This is the I/O boundary: the real implementations belong
/// to `olv-transport`.
///
/// `Send` so a source holding one can be polled on its own thread. The C#'s
/// `IDisposable` is `Drop`.
pub trait EcuTransport: Send {
    fn is_open(&self) -> bool;

    fn open(&mut self) -> Result<()>;

    fn close(&mut self);

    fn write(&mut self, data: &[u8]) -> Result<()>;

    /// How long a write may block before the transport gives up on it.
    ///
    /// Adjustable because one operation needs far longer than the rest: a
    /// controller writing its flash stops servicing the link, so the bytes still
    /// buffered for it cannot be handed over and the write that delivered the
    /// burn command is itself what blocks. Everything else should fail fast:
    /// Windows' incoming Bluetooth port never accepts a write at all, and waiting
    /// on it is time spent learning nothing.
    ///
    /// Ignored by transports that have no such notion, which is why this has a
    /// default (zero, and setting it does nothing) rather than being forced on
    /// every implementation.
    fn write_timeout(&self) -> Duration {
        Duration::ZERO
    }

    fn set_write_timeout(&mut self, _timeout: Duration) {}

    /// Reads until `buffer` is full or the timeout passes. Returns how many bytes
    /// it got, which may be short.
    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize>;

    fn discard_input(&mut self);
}

/// Command that returns the firmware signature, e.g. "MS3 Format 0569.00".
pub const QUERY_SIGNATURE: u8 = b'Q';

/// Command that returns the longer version string.
pub const QUERY_VERSION: u8 = b'S';

/// Every command that makes an ECU say what it is, in the order worth asking.
///
/// There is no single one, and the same letter means different things on
/// different firmware: MegaSquirt answers 'Q' with its signature and 'S' with a
/// build string, while rusEFI answers 'S' with its signature, 'V' with a build
/// string, and refuses 'Q' outright. Rather than deciding in advance which family
/// is on the other end, ask all three and let the one that matches an INI settle
/// it.
pub const IDENTITY_COMMANDS: &[u8] = b"SQV";

const READ_COMMAND: u8 = b'r';

/// The page holding the realtime block.
const REALTIME_TABLE: u8 = 7;

/// The bit a reply's status byte sets to report a failure.
///
/// Every error in this protocol carries it (underrun 0x80, CRC 0x82, unrecognised
/// 0x83, out of range 0x84, framing 0x8D) and every way of saying yes does not:
/// 0x00 acknowledges a request, 0x04 a burn, 0x07 a controller command.
const FAILED: u8 = 0x80;

pub fn frame(payload: &[u8]) -> Vec<u8> {
    let mut framed = Vec::with_capacity(2 + payload.len() + 4);

    framed.extend_from_slice(&(payload.len() as u16).to_be_bytes());
    framed.extend_from_slice(payload);
    framed.extend_from_slice(&crc32(payload).to_be_bytes());

    framed
}

/// Pulls the data out of a reply, checking its length and checksum.
///
/// The checksum is the point of the exercise: a short read on a flaky link gives
/// bytes that decode into perfectly plausible readings, and only the CRC tells you
/// they are not real.
pub fn unframe(reply: &[u8]) -> std::result::Result<Vec<u8>, EcuProtocolError> {
    if reply.len() < 7 {
        return Err(EcuProtocolError::with_kind(
            format!("Reply was {} bytes; a framed reply is at least 7.", reply.len()),
            WireFailureKind::ShortReply,
        ));
    }

    let length = usize::from(u16::from_be_bytes([reply[0], reply[1]]));
    if length < 1 || 2 + length + 4 > reply.len() {
        return Err(EcuProtocolError::with_kind(
            format!("Reply declares {} bytes but {} arrived.", length, reply.len() - 6),
            WireFailureKind::Malformed,
        ));
    }

    let body = &reply[2..2 + length];
    let at = 2 + length;
    let declared = u32::from_be_bytes([reply[at], reply[at + 1], reply[at + 2], reply[at + 3]]);
    let actual = crc32(body);

    if declared != actual {
        return Err(EcuProtocolError::with_kind(
            format!("Reply failed its checksum ({actual:08X} against {declared:08X}); the link dropped bytes."),
            WireFailureKind::ChecksumMismatch,
        ));
    }

    // An error is marked by the high bit, not by being anything other than zero.
    // There is more than one way for this protocol to say yes: 0x00 is a plain
    // acknowledgement, 0x04 acknowledges a burn and 0x07 a controller command,
    // while every failure (underrun 0x80, CRC 0x82, unrecognised 0x83, out of
    // range 0x84, framing 0x8D) sets it.
    //
    // Insisting on 0x00 made a rusEFI's successful burn read as a refusal. The
    // board had written its flash, answered 0x04 to say so, and was told the burn
    // had been declined; the value was there after a reboot. A write reported as
    // failed is the one error worth going out of the way to avoid, because the
    // answer to it is to write again.
    if body[0] & FAILED != 0 {
        return Err(EcuProtocolError {
            message: format!("The ECU refused the request (status 0x{:02X}).", body[0]),
            refused: true,
            failure_kind: WireFailureKind::Refused,
        });
    }

    Ok(body[1..].to_vec())
}

/// Builds a read of the realtime page. No other page is ever asked for.
///
/// `size` below 1 is an `ArgumentOutOfRangeException` in the C#. The C#'s default
/// `canId` is 0.
pub fn realtime_request(size: i32, can_id: u8) -> Result<Vec<u8>> {
    if size < 1 {
        return Err(Error::Argument(format!(
            "size ('{size}') must be greater than or equal to '1'."
        )));
    }

    Ok(vec![
        READ_COMMAND,
        can_id,
        REALTIME_TABLE,
        0,
        0,
        (size >> 8) as u8,
        (size & 0xFF) as u8,
    ])
}

/// Trims the padding an ECU puts on its signature.
///
/// `Encoding.ASCII.GetString`: a byte above 0x7F decodes to `?`.
pub fn read_signature(data: &[u8]) -> String {
    let text: String = data
        .iter()
        .map(|&b| if b < 0x80 { char::from(b) } else { '?' })
        .collect();
    text.trim_end_matches(['\0', ' ', '\r', '\n']).to_string()
}

/// CRC-32, as used by Ethernet and Zip; the one MegaSquirt frames with.
pub fn crc32(data: &[u8]) -> u32 {
    let mut crc: u32 = 0xFFFF_FFFF;

    for &b in data {
        crc ^= u32::from(b);
        for _ in 0..8 {
            crc = if crc & 1 != 0 {
                (crc >> 1) ^ 0xEDB8_8320
            } else {
                crc >> 1
            };
        }
    }

    !crc
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reply(data: &[u8]) -> Vec<u8> {
        // <len:2><status 0><data><crc32:4>, the shape an ECU sends back.
        let mut body = vec![0x00];
        body.extend_from_slice(data);

        let mut framed = vec![(body.len() >> 8) as u8, (body.len() & 0xFF) as u8];
        framed.extend_from_slice(&body);
        framed.extend_from_slice(&crc32(&body).to_be_bytes());
        framed
    }

    #[test]
    fn a_request_is_length_payload_and_checksum() {
        let framed = frame(b"Q");

        assert_eq!(framed.len(), 7);
        assert_eq!(framed[0], 0x00);
        assert_eq!(framed[1], 0x01);
        assert_eq!(framed[2], b'Q');
    }

    #[test]
    fn a_reply_unframes_to_its_data() {
        assert_eq!(unframe(&reply(b"MS3")).unwrap(), b"MS3");
    }

    #[test]
    fn a_reply_with_a_bad_checksum_is_refused() {
        // The whole point of the framing. A truncated reply decodes into perfectly
        // plausible readings, and only the checksum says otherwise.
        let mut r = reply(b"MS3");
        *r.last_mut().unwrap() ^= 0xFF;

        let thrown = unframe(&r).unwrap_err();
        assert!(thrown.message.to_lowercase().contains("checksum"));
        assert_eq!(thrown.failure_kind, WireFailureKind::ChecksumMismatch);
        assert!(!thrown.refused);
    }

    #[test]
    fn a_reply_shorter_than_its_declared_length_is_refused() {
        let r = reply(&[0u8; 20]);

        let thrown = unframe(&r[..12]).unwrap_err();
        assert_eq!(thrown.failure_kind, WireFailureKind::Malformed);
        assert_eq!(thrown.message, "Reply declares 21 bytes but 6 arrived.");
    }

    #[test]
    fn a_reply_under_seven_bytes_is_short() {
        let thrown = unframe(&[0, 1, 0, 0, 0, 0]).unwrap_err();
        assert_eq!(thrown.failure_kind, WireFailureKind::ShortReply);
        assert_eq!(thrown.message, "Reply was 6 bytes; a framed reply is at least 7.");
    }

    #[test]
    fn a_zero_length_reply_is_malformed() {
        let thrown = unframe(&[0, 0, 0, 0, 0, 0, 0]).unwrap_err();
        assert_eq!(thrown.failure_kind, WireFailureKind::Malformed);
    }

    #[test]
    fn a_refusal_from_the_ecu_is_reported() {
        let body = [0x83u8, 0x01];
        let mut framed = vec![0x00, body.len() as u8];
        framed.extend_from_slice(&body);
        framed.extend_from_slice(&crc32(&body).to_be_bytes());

        let thrown = unframe(&framed).unwrap_err();
        assert!(thrown.message.to_lowercase().contains("refused"));
        assert_eq!(thrown.message, "The ECU refused the request (status 0x83).");
        assert!(thrown.refused);
        assert_eq!(thrown.failure_kind, WireFailureKind::Refused);
    }

    #[test]
    fn a_burn_acknowledgement_is_not_a_refusal() {
        // rusEFI answers a successful burn 0x04 and a controller command 0x07.
        for status in [0x00u8, 0x04, 0x07] {
            let body = [status, 0xAA];
            let mut framed = vec![0x00, 2];
            framed.extend_from_slice(&body);
            framed.extend_from_slice(&crc32(&body).to_be_bytes());
            assert_eq!(unframe(&framed).unwrap(), [0xAA]);
        }
    }

    #[test]
    fn the_realtime_request_only_ever_asks_for_the_realtime_page() {
        // Table 7. Nothing here can be pointed at a tune page.
        let request = realtime_request(512, 0).unwrap();

        assert_eq!(request[0], b'r');
        assert_eq!(request[2], 7);
        assert_eq!(request[3], 0);
        assert_eq!(request[4], 0);
        assert_eq!(request[5], 0x02);
        assert_eq!(request[6], 0x00);
    }

    #[test]
    fn the_realtime_request_bytes_are_exact() {
        assert_eq!(realtime_request(1824, 3).unwrap(), [b'r', 3, 7, 0, 0, 0x07, 0x20]);
        assert_eq!(realtime_request(1, 0).unwrap(), [b'r', 0, 7, 0, 0, 0, 1]);
        assert!(realtime_request(0, 0).is_err());
        assert!(realtime_request(-5, 0).is_err());
    }

    #[test]
    fn framing_bytes_are_exact() {
        // 'Q': length 0001, payload 51, CRC-32 of "Q" big-endian.
        let framed = frame(b"Q");
        let crc = crc32(b"Q");
        assert_eq!(&framed[..3], [0x00, 0x01, 0x51]);
        assert_eq!(&framed[3..], crc.to_be_bytes());
        assert_eq!(frame(&[]), [0, 0, 0, 0, 0, 0]);
        let long = frame(&[0xAB; 300]);
        assert_eq!(&long[..2], [0x01, 0x2C]);
    }

    #[test]
    fn the_identity_commands_are_asked_in_order() {
        assert_eq!(IDENTITY_COMMANDS, b"SQV");
        assert_eq!(QUERY_SIGNATURE, 0x51);
        assert_eq!(QUERY_VERSION, 0x53);
    }

    #[test]
    fn a_signature_loses_its_padding() {
        assert_eq!(read_signature(b"MS3 Format 0569.00 \0"), "MS3 Format 0569.00");
        assert_eq!(read_signature(b"AB\r\n \0"), "AB");
        // Encoding.ASCII turns a high byte into '?'.
        assert_eq!(read_signature(&[0x41, 0xFF, 0x42]), "A?B");
    }

    #[test]
    fn the_checksum_matches_the_known_value() {
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
    }

    struct Null;
    impl EcuTransport for Null {
        fn is_open(&self) -> bool {
            false
        }
        fn open(&mut self) -> Result<()> {
            Ok(())
        }
        fn close(&mut self) {}
        fn write(&mut self, _data: &[u8]) -> Result<()> {
            Ok(())
        }
        fn read(&mut self, _buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
            Ok(0)
        }
        fn discard_input(&mut self) {}
    }

    #[test]
    fn a_transport_without_a_write_timeout_ignores_setting_one() {
        let mut t = Null;
        t.set_write_timeout(Duration::from_secs(9));
        assert_eq!(t.write_timeout(), Duration::ZERO);
    }

    #[test]
    fn the_error_converts_to_the_core_protocol_error() {
        let e: Error = EcuProtocolError::new("boom").into();
        assert!(matches!(e, Error::Protocol(m) if m == "boom"));
    }
}
