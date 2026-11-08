//! Ported from `SniperSession.cs`. Owned by package `w1-sniper`.
//!
//! A session with a Holley Sniper ECU over its USB-to-CAN dongle: the plumbing from
//! `SNIPER_ECU_CLIENT_SPEC.md` section 4 (connect/discover/identify/sync) and section 5
//! (telemetry poll), built on [`SniperCanTransport`], `sniper_can` and `sniper_hefi`.
//!
//! **Deliberately not a `LiveSource`.** That trait needs fixed, named channels this session
//! cannot honestly provide: section 5 says the per-channel offset inside a polled telemetry
//! reply is "not yet mapped" and needs a hardware capture. Inventing an offset to make a gauge
//! appear would be exactly the kind of unverified-presented-as-verified this project's own
//! hardware testing discipline exists to prevent. What this type gives instead is the proven
//! mechanics (send a request, get a reassembled reply), so that once real hardware maps the
//! channel offsets, an adapter can sit on top without touching anything below it.
//!
//! **Nothing in this type has been run against a real dongle or ECU.** Section 4 marks the
//! whole session sequence as "assembled from the evidence", with exact ordering and timing
//! explicitly **[CONFIRM ON HW]**. Treat a successful [`SniperSession::open`] as "the USB and
//! CAN layers work", not as "this talks to a real Sniper ECU".
//!
//! Ownership: the C#'s `ownsTransport` flag is the type here. Pass the transport by value and
//! the session owns it (dropping the session drops, and so closes, the transport); pass
//! `&mut transport` and the session only borrows it.

use std::time::{Duration, Instant};

use olv_core::{Error, Result, fmt};

use super::sniper_can::{self, SniperFrameKind, simple_command};
use super::sniper_can_transport::{SniperCanFrame, SniperCanTransport};
use super::sniper_hefi::{self, SniperBlockReassembler, sniper_opcodes};

/// Spec section 4: "the flash path uses 1,000,000 (1 Mbit); this is the most likely bus rate".
/// UNCONFIRMED as the rate a live session should use.
pub const LIKELY_BITRATE: u32 = 1_000_000;

/// Spec section 5: the VE/Learn overlay's poll timer interval.
pub const TELEMETRY_POLL_INTERVAL: Duration = Duration::from_millis(2000);

pub struct SniperSession<T: SniperCanTransport> {
    transport: T,
    src_node: i32,
    dst_node: i32,
}

impl<T: SniperCanTransport> SniperSession<T> {
    pub fn new(transport: T) -> Self {
        Self {
            transport,
            src_node: 0,
            dst_node: -1,
        }
    }

    pub fn is_open(&self) -> bool {
        self.transport.is_open()
    }

    /// The ECU's node id, once [`open`](Self::open) has discovered it. -1 until then.
    pub fn ecu_node(&self) -> i32 {
        self.dst_node
    }

    /// Opens the transport, sets the bus rate, and attempts node discovery: spec section 4
    /// steps 1-3. Fails if the transport will not open or no node-discovery frame arrives
    /// within `discovery_timeout` (default 3 s).
    ///
    /// This is the point past which "it opened" stops meaning "the USB link works" and starts
    /// meaning "something on the CAN bus answered a ping the way the spec expects a Sniper
    /// ECU to": a real but much weaker claim given none of this is hardware-confirmed.
    ///
    /// **Open question the reference client raises rather than answers:** `sniper_can.py`
    /// does not implement discovery at all; its `send_hefi` just defaults to `dst=1, src=0`
    /// and sends. That may mean a Sniper ECU's node id is conventionally always 1, or that the
    /// reference client skips a step the real `Sniper.exe` performs. Discovery remains the
    /// primary path because the main spec's section 4 calls for it; a caller whose discovery
    /// times out could reasonably try `dst=1`, but this method does not do that automatically.
    pub fn open(&mut self, discovery_timeout: Option<Duration>) -> Result<()> {
        self.transport.open()?;
        self.transport.set_bitrate(LIKELY_BITRATE)?;

        self.src_node = 0;

        self.transport.send(&SniperCanFrame::new(
            sniper_can::simple_id(self.src_node, simple_command::PING_OR_ENUMERATE),
            Vec::new(),
        ))?;

        let timeout = discovery_timeout.unwrap_or(Duration::from_secs(3));
        let deadline = Instant::now() + timeout;

        while Instant::now() < deadline {
            let remaining = deadline.saturating_duration_since(Instant::now());
            let Some(frame) = self.transport.receive(remaining)? else {
                break;
            };

            if sniper_can::classify(frame.can_id) == SniperFrameKind::NodeDiscovery {
                // Spec section 4: "the app stores the discovered node id and uses it as
                // dst/src thereafter." Which field of the frame carries the node id is not
                // given by the spec beyond "learn... from NODE-DISCOVERY frames".
                //
                // Every id in section 2 packs a node as `node << 3`, so the node is
                // `(can_id >> 3) & 0x7FF`. (The C# read `can_id & 0x7FF`, which keeps the
                // three command bits below the node and so addressed the wrong node. Found
                // in hardware testing 2026-09-19; fixed here. The bench must confirm the
                // corrected read against a real ECU.)
                self.dst_node = ((frame.can_id >> 3) & 0x7FF) as i32;
                return Ok(());
            }
        }

        Err(Error::Timeout(
            "No node-discovery reply arrived from a Sniper ECU within the timeout. Unverified \
             against real hardware \u{2014} this may mean nothing is listening, or that this \
             implementation's guess at the discovery handshake is wrong."
                .into(),
        ))
    }

    /// Sends one HEFI request and waits for its reassembled reply (default timeout 2 s): the
    /// primitive both calibration reads and telemetry polls are built from. Spec section 3.1
    /// (build) + 3.2 (reassemble) + 4 (this session's own node ids).
    pub fn send_request(
        &mut self,
        cmd0: u32,
        cmd1: u32,
        cmd2: u32,
        payload: &[u8],
        timeout: Option<Duration>,
    ) -> Result<Vec<u8>> {
        if self.dst_node < 0 {
            return Err(Error::InvalidOperation(
                "No ECU node discovered yet \u{2014} call Open() first.".into(),
            ));
        }

        let message = sniper_hefi::build_message(cmd0, cmd1, cmd2, payload);
        let bulk_id = sniper_can::bulk_id(self.dst_node, self.src_node);

        for chunk in sniper_hefi::fragment_for_send(&message) {
            self.transport.send(&SniperCanFrame::new(bulk_id, chunk))?;
        }

        let mut reassembler = SniperBlockReassembler::new();
        let deadline = Instant::now() + timeout.unwrap_or(Duration::from_secs(2));

        while !reassembler.is_complete() && Instant::now() < deadline {
            let remaining = deadline.saturating_duration_since(Instant::now());
            let Some(frame) = self.transport.receive(remaining)? else {
                break;
            };

            if sniper_can::classify(frame.can_id) == SniperFrameKind::Bulk {
                reassembler.offer(&frame.data);
            }
        }

        if !reassembler.is_complete() {
            return Err(Error::Timeout(format!(
                "No complete reply arrived for HEFI command 0x{} within the timeout.",
                fmt::long(i64::from(cmd0), "X8")
            )));
        }

        Ok(reassembler.result())
    }

    /// Issues the device-info read (opcode group `0x45`, spec 3.3 / 4 step 4). The exact
    /// sub-opcode/cmd1/cmd2 values for this group are UNCONFIRMED: the spec only names the
    /// group, not the full triplet, so this uses the group byte alone with cmd1/cmd2 zero,
    /// which may not be what a real ECU wants.
    pub fn read_device_info(&mut self, timeout: Option<Duration>) -> Result<Vec<u8>> {
        self.send_request(0x45, 0, 0, &[], timeout)
    }

    /// Reads one calibration region by its read opcode (the `_a_1` member of a 3.3 triplet;
    /// see [`sniper_opcodes::is_read`]).
    pub fn read_calibration_block(&mut self, read_opcode: u32, timeout: Option<Duration>) -> Result<Vec<u8>> {
        if !sniper_opcodes::is_read(read_opcode) {
            return Err(Error::Argument(format!(
                "0x{} is not a read opcode. (Parameter 'readOpcode')",
                fmt::long(i64::from(read_opcode), "X8")
            )));
        }

        self.send_request(read_opcode, 0, 0, &[], timeout)
    }

    /// One telemetry poll, raw: section 5's `FUN_00492a20(0x40000A01, 0, 0x1008, 0xf04,
    /// callback, flush=1)` read, reissued on [`TELEMETRY_POLL_INTERVAL`] by a real client. The
    /// mapping from that call's six parameters onto this session's `(cmd0, cmd1, cmd2)`
    /// triple is UNCONFIRMED, inferred by position (cmd0=0x40000A01, cmd1=0, cmd2=0x1008);
    /// the remaining two arguments (`0xf04`, `flush=1`) are not represented at all rather than
    /// guessed into the wrong slot.
    ///
    /// Returns the raw reassembled reply bytes, **not decoded into named channels**; see this
    /// module's header for why.
    pub fn poll_telemetry_raw(&mut self, timeout: Option<Duration>) -> Result<Vec<u8>> {
        self.send_request(0x4000_0A01, 0, 0x1008, &[], timeout)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sniper::sniper_hefi::BLOCK_REPLY_MARKER;
    use std::collections::VecDeque;

    /// A scripted transport: records what is sent and plays back queued frames; once the
    /// queue is empty it behaves like a quiet bus.
    #[derive(Default)]
    struct Scripted {
        open: bool,
        sent: Vec<SniperCanFrame>,
        bitrates: Vec<u32>,
        incoming: VecDeque<SniperCanFrame>,
    }

    impl SniperCanTransport for Scripted {
        fn is_open(&self) -> bool {
            self.open
        }
        fn open(&mut self) -> Result<()> {
            self.open = true;
            Ok(())
        }
        fn close(&mut self) {
            self.open = false;
        }
        fn send(&mut self, frame: &SniperCanFrame) -> Result<()> {
            self.sent.push(frame.clone());
            Ok(())
        }
        fn receive(&mut self, _timeout: Duration) -> Result<Option<SniperCanFrame>> {
            Ok(self.incoming.pop_front())
        }
        fn set_bitrate(&mut self, bits_per_second: u32) -> Result<()> {
            self.bitrates.push(bits_per_second);
            Ok(())
        }
        fn hardware_version(&mut self) -> Result<u32> {
            Ok(1)
        }
    }

    fn discovery_reply(node: u32) -> SniperCanFrame {
        SniperCanFrame::new(sniper_can::simple_id(node as i32, simple_command::NODE_ACK), Vec::new())
    }

    fn bulk_reply(chunks: &[&[u8]]) -> Vec<SniperCanFrame> {
        let id = sniper_can::bulk_id(0, 1);
        chunks.iter().map(|c| SniperCanFrame::new(id, c.to_vec())).collect()
    }

    fn reply_header(payload_len: u32) -> Vec<u8> {
        let mut h = BLOCK_REPLY_MARKER.to_be_bytes().to_vec();
        h.extend((payload_len + 4).to_be_bytes());
        h
    }

    fn opened(node_reply: u32) -> SniperSession<Scripted> {
        let mut t = Scripted::default();
        t.incoming.push_back(discovery_reply(node_reply));
        let mut s = SniperSession::new(t);
        s.open(Some(Duration::from_millis(200))).unwrap();
        s
    }

    #[test]
    fn open_sets_the_bitrate_and_sends_the_ping_frame() {
        let s = opened(5);
        assert!(s.is_open());
        assert_eq!(s.transport.bitrates, vec![LIKELY_BITRATE]);
        // The one frame Open builds: simple id 0x70014004 (ping, node 0), no payload.
        assert_eq!(s.transport.sent.len(), 1);
        assert_eq!(s.transport.sent[0].can_id, 0x7001_4004);
        assert!(s.transport.sent[0].data.is_empty());
    }

    /// Ids pack the node as `node << 3`, so a reply from node 5 gives `ecu_node() == 5`.
    /// Hardware testing found the C# read `CanId & 0x7FF` and got `(5 << 3) | 4` instead.
    #[test]
    fn open_reads_the_node_from_bits_three_to_thirteen() {
        let s = opened(5);
        assert_eq!(s.ecu_node(), 5);
        assert_eq!(opened(0).ecu_node(), 0);
        assert_eq!(opened(0x7FF).ecu_node(), 0x7FF);
    }

    #[test]
    fn open_times_out_when_nothing_answers() {
        let mut s = SniperSession::new(Scripted::default());
        let err = s.open(Some(Duration::from_millis(30))).unwrap_err();
        assert!(matches!(err, Error::Timeout(_)));
        assert_eq!(s.ecu_node(), -1);
    }

    #[test]
    fn open_ignores_frames_that_are_not_node_discovery() {
        let mut t = Scripted::default();
        t.incoming
            .push_back(SniperCanFrame::new(sniper_can::bulk_id(0, 1), vec![1]));
        t.incoming.push_back(discovery_reply(1));
        let mut s = SniperSession::new(t);
        s.open(Some(Duration::from_millis(200))).unwrap();
        assert!(s.ecu_node() >= 0);
    }

    #[test]
    fn a_request_before_open_is_refused() {
        let mut s = SniperSession::new(Scripted::default());
        let err = s.send_request(1, 0, 0, &[], None).unwrap_err();
        assert!(matches!(err, Error::InvalidOperation(_)));
    }

    /// The frames a request builds, byte for byte: bulk id (dst = discovered node, src = 0)
    /// and the message cut into 8-byte chunks.
    #[test]
    fn a_request_sends_its_message_as_bulk_frames_and_returns_the_reassembled_reply() {
        let mut s = opened(2);
        let node = s.ecu_node();
        s.transport.sent.clear();

        let header = reply_header(3);
        let frames = bulk_reply(&[&header, &[0xAA, 0xBB, 0xCC]]);
        s.transport.incoming.extend(frames);

        let reply = s.send_request(0x45, 0, 0, &[], None).unwrap();
        assert_eq!(reply, vec![0xAA, 0xBB, 0xCC]);

        // Bytes taken from the C# (rust/oracle/expected/w1-sniper/wire.json), not from build_message.
        let expected: Vec<Vec<u8>> = vec![
            vec![0, 0, 0, 0, 0, 0, 0, 0x45],
            vec![0; 8],
            vec![0xEF, 0xD0, 0x80, 0x27],
        ];
        let id = sniper_can::bulk_id(node, 0);
        assert_eq!(s.transport.sent.len(), expected.len());
        for (frame, chunk) in s.transport.sent.iter().zip(&expected) {
            assert_eq!(frame.can_id, id);
            assert_eq!(&frame.data, chunk);
        }
    }

    #[test]
    fn a_request_ignores_non_bulk_frames_while_waiting() {
        let mut s = opened(2);
        let header = reply_header(1);
        s.transport.incoming.push_back(discovery_reply(2));
        s.transport.incoming.extend(bulk_reply(&[&header, &[7]]));
        assert_eq!(s.send_request(1, 0, 0, &[], None).unwrap(), vec![7]);
    }

    #[test]
    fn a_request_with_no_reply_times_out() {
        let mut s = opened(2);
        let err = s
            .send_request(0x45, 0, 0, &[], Some(Duration::from_millis(20)))
            .unwrap_err();
        match err {
            Error::Timeout(m) => assert!(m.contains("0x00000045"), "{m}"),
            other => panic!("wrong error: {other:?}"),
        }
    }

    #[test]
    fn read_device_info_asks_for_group_0x45_with_zero_arguments() {
        let mut s = opened(2);
        s.transport.sent.clear();
        let _ = s.read_device_info(Some(Duration::from_millis(10)));
        let first = &s.transport.sent[0].data;
        // Message bytes 4..8 are cmd0 big-endian.
        assert_eq!(first[4..8], [0, 0, 0, 0x45]);
    }

    #[test]
    fn read_calibration_block_refuses_a_non_read_opcode() {
        let mut s = opened(2);
        let err = s.read_calibration_block(0x4000_0a02, None).unwrap_err();
        assert!(matches!(err, Error::Argument(_)));
    }

    #[test]
    fn poll_telemetry_raw_sends_the_inferred_triplet() {
        let mut s = opened(2);
        s.transport.sent.clear();
        let _ = s.poll_telemetry_raw(Some(Duration::from_millis(10)));
        // Message bytes from the C# (wire.json), not from build_message.
        let message: Vec<u8> = vec![
            0, 0, 0, 0, 0x40, 0, 0x0A, 0x01, 0, 0, 0, 0, 0, 0, 0x10, 0x08, 0x0F, 0xF6, 0x6F, 0x76,
        ];
        let sent: Vec<u8> = s.transport.sent.iter().flat_map(|f| f.data.clone()).collect();
        assert_eq!(sent, message);
    }

    #[test]
    fn a_borrowed_transport_is_left_open_when_the_session_is_dropped() {
        let mut t = Scripted::default();
        t.incoming.push_back(discovery_reply(1));
        {
            let mut s = SniperSession::new(&mut t);
            s.open(Some(Duration::from_millis(100))).unwrap();
        }
        assert!(t.is_open());
    }
}
