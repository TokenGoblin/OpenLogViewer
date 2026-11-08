//! MegaSquirt-family doubles: a scripted transport and a model of an ECU's memory.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::Result;
use olv_protocol::ms::ms_protocol::{self, EcuTransport};

/// `<len:2><status><data><crc32:4>`, the shape an ECU sends back.
pub fn reply_with_status(status: u8, data: &[u8]) -> Vec<u8> {
    let mut body = vec![status];
    body.extend_from_slice(data);
    ms_protocol::frame(&body)
}

/// A plain acknowledgement carrying `data`.
pub fn reply(data: &[u8]) -> Vec<u8> {
    reply_with_status(0x00, data)
}

/// What the responder of a [`FakeTransport`] is handed: the whole framed request.
pub type Responder = Box<dyn FnMut(&[u8]) -> Vec<u8> + Send>;

pub struct FakeTransportState {
    replies: VecDeque<Vec<u8>>,
    pending: Vec<u8>,
    responder: Option<Responder>,
    /// Answered indefinitely once the queue runs dry, for a polling test.
    pub repeating: Option<Vec<u8>>,
    pub written: Vec<Vec<u8>>,
    pub open: bool,
    pub opens: usize,
    pub closes: usize,
    pub discards: usize,
    pub write_timeout: Duration,
    /// Every value `set_write_timeout` was given, in order.
    pub write_timeout_history: Vec<Duration>,
}

/// A transport that replies from a script, so the retry and framing behaviour can be
/// exercised without an ECU, including the failures a real link produces and a cable on a
/// desk almost never does (`FakeTransport`). A cloneable handle: the connection owns one
/// clone and the test keeps the other.
#[derive(Clone)]
pub struct FakeTransport(pub Arc<Mutex<FakeTransportState>>);

impl FakeTransport {
    pub fn new() -> Self {
        Self(Arc::new(Mutex::new(FakeTransportState {
            replies: VecDeque::new(),
            pending: Vec::new(),
            responder: None,
            repeating: None,
            written: Vec::new(),
            open: false,
            opens: 0,
            closes: 0,
            discards: 0,
            write_timeout: Duration::from_millis(500),
            write_timeout_history: Vec::new(),
        })))
    }

    /// Answers every write by calling `responder` with the framed request.
    pub fn responding(responder: impl FnMut(&[u8]) -> Vec<u8> + Send + 'static) -> Self {
        let t = Self::new();
        t.with(|s| s.responder = Some(Box::new(responder)));
        t
    }

    pub fn with<R>(&self, f: impl FnOnce(&mut FakeTransportState) -> R) -> R {
        f(&mut self.0.lock().unwrap())
    }

    pub fn enqueue(&self, replies: impl IntoIterator<Item = Vec<u8>>) {
        self.with(|s| s.replies.extend(replies));
    }

    pub fn written(&self) -> Vec<Vec<u8>> {
        self.with(|s| s.written.clone())
    }

    pub fn discards(&self) -> usize {
        self.with(|s| s.discards)
    }
}

impl Default for FakeTransport {
    fn default() -> Self {
        Self::new()
    }
}

impl EcuTransport for FakeTransport {
    fn is_open(&self) -> bool {
        self.with(|s| s.open)
    }

    fn open(&mut self) -> Result<()> {
        self.with(|s| {
            s.open = true;
            s.opens += 1;
        });
        Ok(())
    }

    fn close(&mut self) {
        self.with(|s| {
            s.open = false;
            s.closes += 1;
        });
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.with(|s| {
            s.written.push(data.to_vec());
            s.pending = if let Some(responder) = s.responder.as_mut() {
                responder(data)
            } else if let Some(next) = s.replies.pop_front() {
                next
            } else {
                s.repeating.clone().unwrap_or_default()
            };
        });
        Ok(())
    }

    fn write_timeout(&self) -> Duration {
        self.with(|s| s.write_timeout)
    }

    fn set_write_timeout(&mut self, timeout: Duration) {
        self.with(|s| {
            s.write_timeout = timeout;
            s.write_timeout_history.push(timeout);
        });
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(self.with(|s| {
            let take = buffer.len().min(s.pending.len());
            buffer[..take].copy_from_slice(&s.pending[..take]);
            s.pending.drain(..take);
            take
        }))
    }

    fn discard_input(&mut self) {
        self.with(|s| s.discards += 1);
    }
}

/// How a [`ecu_model`] behaves when it is written to and burned.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Misbehaviour {
    /// Acknowledges writes and applies them: a healthy ECU.
    None,
    /// Acknowledges writes and throws the bytes away.
    IgnoresWrites,
    /// Refuses every write with status 0x83.
    RefusesWrites,
    /// Refuses a burn with status 0x83.
    RefusesBurn,
    /// Answers a burn with the given status (rusEFI says 0x04).
    BurnStatus(u8),
    /// Says nothing at all to a burn.
    SilentBurn,
}

/// A page of ECU memory with the MegaSquirt-family commands in front of it: every
/// `r`/`p`/`R` read answers from the memory, every `w`/`M`/`C` write lands in it, and
/// every `b`/`B` burn answers per `behaviour`. Every request has the shape
/// `<cmd><2 id bytes><offset:2><count:2>[<data>]`, which all three families share.
pub fn ecu_model(
    memory: Arc<Mutex<Vec<u8>>>,
    little_endian: bool,
    behaviour: Misbehaviour,
) -> impl FnMut(&[u8]) -> Vec<u8> + Send + 'static {
    move |framed: &[u8]| {
        let payload = &framed[2..framed.len() - 4];
        let word = |a: u8, b: u8| -> usize {
            if little_endian {
                usize::from(b) << 8 | usize::from(a)
            } else {
                usize::from(a) << 8 | usize::from(b)
            }
        };

        match payload[0] {
            // The identity questions, from an ECU that refuses MegaSquirt's query command.
            b'S' => reply(b"speeduino 202501-dev"),
            b'Q' => reply_with_status(0x83, &[]),
            b'V' => reply(b"Speeduino 2025.01"),
            // rusEFI's realtime read has no page: `O<offset:2><count:2>`.
            b'O' => {
                let offset = word(payload[1], payload[2]);
                let count = word(payload[3], payload[4]);
                let memory = memory.lock().unwrap();
                reply(&memory[offset..offset + count])
            }
            b'r' | b'p' | b'R' => {
                let offset = word(payload[3], payload[4]);
                let count = word(payload[5], payload[6]);
                let memory = memory.lock().unwrap();
                reply(&memory[offset..offset + count])
            }
            b'w' | b'M' | b'C' if behaviour == Misbehaviour::RefusesWrites => reply_with_status(0x83, &[]),
            b'w' | b'M' | b'C' => {
                let offset = word(payload[3], payload[4]);
                let count = word(payload[5], payload[6]);
                if behaviour != Misbehaviour::IgnoresWrites {
                    memory.lock().unwrap()[offset..offset + count].copy_from_slice(&payload[7..7 + count]);
                }
                reply(&[])
            }
            b'b' | b'B' => match behaviour {
                Misbehaviour::RefusesBurn => reply_with_status(0x83, &[]),
                Misbehaviour::BurnStatus(status) => reply_with_status(status, &[]),
                Misbehaviour::SilentBurn => Vec::new(),
                _ => reply(&[]),
            },
            other => panic!("the fake ECU was sent a command it does not know: {other:#04x}"),
        }
    }
}
