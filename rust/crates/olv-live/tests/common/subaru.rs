//! A Subaru answering SSM over an ELM327, in software (`FakeSubaru`).

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::Result;
use olv_protocol::ms::ms_protocol::EcuTransport;

/// The framing the real car used, with the values it actually returned at those
/// addresses. A cloneable handle.
#[derive(Clone)]
pub struct FakeSubaru(pub Arc<Mutex<FakeSubaruState>>);

pub struct FakeSubaruState {
    out: VecDeque<u8>,
    input: String,
    pub memory: HashMap<i32, u8>,
    pub received: Vec<String>,
    pub open: bool,
}

impl FakeSubaru {
    pub fn new() -> Self {
        Self(Arc::new(Mutex::new(FakeSubaruState {
            out: VecDeque::new(),
            input: String::new(),
            memory: HashMap::from([(0x00_000E, 0x0E), (0x00_000F, 0x36), (0x00_0008, 0x86)]),
            received: Vec::new(),
            open: false,
        })))
    }

    pub fn with<R>(&self, f: impl FnOnce(&mut FakeSubaruState) -> R) -> R {
        f(&mut self.0.lock().unwrap())
    }

    pub fn received(&self) -> Vec<String> {
        self.with(|s| s.received.clone())
    }
}

impl Default for FakeSubaru {
    fn default() -> Self {
        Self::new()
    }
}

impl FakeSubaruState {
    fn say(&mut self, line: &str) {
        self.out.extend(line.bytes());
        self.out.push_back(b'\r');
    }

    fn answer(&mut self, command: &str) {
        self.received.push(command.to_string());

        let text = command.to_ascii_uppercase();

        if text == "ATZ" || text == "ATI" {
            self.say("ELM327 v1.3a");
        } else if text == "STDI" {
            self.say("OBDLink r2.6");
        } else if text == "STI" {
            self.say("STN1100 v2.2.2");
        } else if text.starts_with("ST") {
            self.say("?");
        } else if text.starts_with("AT") {
            self.say("OK");
        } else if text == "0100" {
            self.say("4100BE3FA813");
        } else if text.starts_with("A800") {
            self.read(&text);
        } else {
            self.say("NO DATA");
        }

        self.out.push_back(b'\r');
        self.out.push_back(b'>');
    }

    fn read(&mut self, command: &str) {
        // A8, the padding byte, then three bytes of address each.
        let body = &command[4..];

        if body.is_empty() || !body.len().is_multiple_of(6) {
            // What the real car does with a length it did not expect.
            self.say("7FA813");
            return;
        }

        let mut values = vec![0xE8u8];

        for at in (0..body.len()).step_by(6) {
            let address = i32::from_str_radix(&body[at..at + 6], 16).unwrap();

            let Some(&value) = self.memory.get(&address) else {
                self.say("NO DATA");
                return;
            };

            values.push(value);
        }

        let text: String = values.iter().map(|v| format!("{v:02X}")).collect();
        self.say(&text);
    }
}

impl EcuTransport for FakeSubaru {
    fn is_open(&self) -> bool {
        self.with(|s| s.open)
    }

    fn open(&mut self) -> Result<()> {
        self.with(|s| s.open = true);
        Ok(())
    }

    fn close(&mut self) {
        self.with(|s| s.open = false);
    }

    fn write(&mut self, data: &[u8]) -> Result<()> {
        self.with(|s| {
            for &b in data {
                if b != b'\r' {
                    s.input.push(char::from(b));
                    continue;
                }

                let command = s.input.trim().to_string();
                s.answer(&command);
                s.input.clear();
            }
        });
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        Ok(self.with(|s| {
            let mut count = 0;
            while count < buffer.len() {
                let Some(b) = s.out.pop_front() else { break };
                buffer[count] = b;
                count += 1;
            }
            count
        }))
    }

    fn discard_input(&mut self) {
        self.with(|s| s.out.clear());
    }
}
