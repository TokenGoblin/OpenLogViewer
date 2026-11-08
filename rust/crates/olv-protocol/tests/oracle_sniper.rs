//! The Sniper wire bytes against the C# reference (rust/oracle/expected/w1-sniper/wire.json):
//! complete HEFI messages, the fragmenter's frames, and the frames the session sends for Open
//! and for each kind of request. Every byte comes from the C#, not from the Rust's own helpers.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::Result;
use olv_protocol::sniper::sniper_can::{bulk_id, simple_command, simple_id};
use olv_protocol::sniper::sniper_can_transport::{SniperCanFrame, SniperCanTransport};
use olv_protocol::sniper::sniper_hefi::{BLOCK_REPLY_MARKER, build_message, fragment_for_send};
use olv_protocol::sniper::sniper_session::SniperSession;
use serde::Deserialize;

#[derive(Deserialize)]
struct Frame {
    can_id: u32,
    data: String,
}

#[derive(Deserialize)]
struct Request {
    name: String,
    sent: Vec<Frame>,
    reply: String,
}

#[derive(Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
enum Case {
    Message {
        cmd0: u32,
        cmd1: u32,
        cmd2: u32,
        payload: String,
        message: String,
        fragments: Vec<String>,
    },
    Session {
        node_reply: u32,
        open_bitrates: Vec<u32>,
        open_sent: Vec<Frame>,
        ecu_node: i32,
        requests: Vec<Request>,
    },
}

fn unhex(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
        .collect()
}

#[derive(Default)]
struct State {
    open: bool,
    sent: Vec<SniperCanFrame>,
    bitrates: Vec<u32>,
    incoming: VecDeque<SniperCanFrame>,
}

struct Scripted(Arc<Mutex<State>>);

impl SniperCanTransport for Scripted {
    fn is_open(&self) -> bool {
        self.0.lock().unwrap().open
    }
    fn open(&mut self) -> Result<()> {
        self.0.lock().unwrap().open = true;
        Ok(())
    }
    fn close(&mut self) {
        self.0.lock().unwrap().open = false;
    }
    fn send(&mut self, frame: &SniperCanFrame) -> Result<()> {
        self.0.lock().unwrap().sent.push(frame.clone());
        Ok(())
    }
    fn receive(&mut self, _t: Duration) -> Result<Option<SniperCanFrame>> {
        Ok(self.0.lock().unwrap().incoming.pop_front())
    }
    fn set_bitrate(&mut self, b: u32) -> Result<()> {
        self.0.lock().unwrap().bitrates.push(b);
        Ok(())
    }
    fn hardware_version(&mut self) -> Result<u32> {
        Ok(1)
    }
}

fn check_frames(got: &[SniperCanFrame], want: &[Frame], what: &str) {
    assert_eq!(got.len(), want.len(), "{what}: frame count");
    for (g, w) in got.iter().zip(want) {
        assert_eq!(g.can_id, w.can_id, "{what}: id");
        assert_eq!(g.data, unhex(&w.data), "{what}: data");
    }
}

#[test]
fn hefi_messages_fragments_and_session_frames_match_the_csharp() {
    let cases: Vec<Case> = olv_oracle::cases("w1-sniper", "wire");
    assert!(!cases.is_empty());

    for case in cases {
        match case {
            Case::Message {
                cmd0,
                cmd1,
                cmd2,
                payload,
                message,
                fragments,
            } => {
                let got = build_message(cmd0, cmd1, cmd2, &unhex(&payload));
                assert_eq!(got, unhex(&message), "message {cmd0:#x} payload {payload}");
                let want: Vec<Vec<u8>> = fragments.iter().map(|f| unhex(f)).collect();
                assert_eq!(fragment_for_send(&got), want);
            }
            Case::Session {
                node_reply,
                open_bitrates,
                open_sent,
                ecu_node,
                requests,
            } => {
                let state = Arc::new(Mutex::new(State::default()));
                state.lock().unwrap().incoming.push_back(SniperCanFrame::new(
                    simple_id(node_reply as i32, simple_command::NODE_ACK),
                    vec![],
                ));
                let mut s = SniperSession::new(Scripted(Arc::clone(&state)));
                s.open(Some(Duration::from_millis(200))).unwrap();
                assert_eq!(s.ecu_node(), ecu_node);
                {
                    let st = state.lock().unwrap();
                    assert_eq!(st.bitrates, open_bitrates);
                    check_frames(&st.sent, &open_sent, "open");
                }

                for r in &requests {
                    {
                        let mut st = state.lock().unwrap();
                        st.sent.clear();
                        let id = bulk_id(0, 1);
                        let mut header = BLOCK_REPLY_MARKER.to_be_bytes().to_vec();
                        header.extend(14u32.to_be_bytes());
                        st.incoming.push_back(SniperCanFrame::new(id, header));
                        st.incoming
                            .push_back(SniperCanFrame::new(id, vec![1, 2, 3, 4, 5, 6, 7, 8]));
                        st.incoming.push_back(SniperCanFrame::new(id, vec![9, 10]));
                    }
                    let reply = match r.name.as_str() {
                        "device_info" => s.read_device_info(None),
                        "telemetry" => s.poll_telemetry_raw(None),
                        "calibration_read" => s.read_calibration_block(0x4000_0a11, None),
                        "request_with_payload" => s.send_request(0x4000_0a02, 1, 2, &[9, 8, 7, 6, 5], None),
                        other => panic!("unknown request {other}"),
                    }
                    .unwrap();
                    assert_eq!(reply, unhex(&r.reply), "{} reply", r.name);
                    check_frames(&state.lock().unwrap().sent, &r.sent, &r.name);
                }
            }
        }
    }
}
