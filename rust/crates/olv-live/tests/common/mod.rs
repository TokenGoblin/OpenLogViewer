//! Test doubles shared by the connection tests.
//!
//! `ScriptedElm` and `FakeElm` are copies of the doubles `olv-protocol` keeps behind
//! `#[cfg(test)]` (they cannot be reached from another crate): an adapter whose bytes
//! arrive when they are told to, and one that answers instantly and completely with all
//! the awkward habits of the real ones. `FakeSubaru` and `FakeElmOverTcp` are the C#
//! test project's own doubles; `FakeTransport` and `ecu_model` (in `ms`) drive
//! `EcuConnection` byte for byte.

#![allow(dead_code, unused_imports)]

pub mod ms;
pub mod subaru;
pub mod tcp;

pub use ms::{FakeTransport, Misbehaviour, ecu_model, reply, reply_with_status};
pub use subaru::FakeSubaru;
pub use tcp::FakeElmOverTcp;

use std::time::{Duration, Instant};

use olv_core::Result;
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::obd::obd2_faults;

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::{Arc, Mutex};

/// One instalment of a reply: how long after the request, and what.
pub struct Piece {
    pub after: Duration,
    pub text: String,
}

pub fn piece(after_ms: u64, text: &str) -> Piece {
    Piece {
        after: Duration::from_millis(after_ms),
        text: text.to_string(),
    }
}

type Script = Box<dyn Fn(&str) -> Vec<Piece> + Send>;

struct ScriptedState {
    script: Script,
    wire: Vec<(Instant, u8)>,
    pending: String,
    received: Vec<String>,
    open: bool,
}

/// An adapter whose bytes arrive when they are told to.
///
/// `FakeElm` answers instantly and completely, which is the right model for the
/// decode and the wrong one for the framing: every defect this exists to pin is
/// about *when* a byte turns up.
///
/// One modelling rule is load-bearing: `discard_input` drops only what has already
/// arrived, never what is still in flight. A real link cannot throw away a byte the
/// adapter has not sent yet, and a fake that can will happily pass every test with
/// the fix taken back out.
#[derive(Clone)]
pub struct ScriptedElm(Arc<Mutex<ScriptedState>>);

impl ScriptedElm {
    pub fn new(script: impl Fn(&str) -> Vec<Piece> + Send + 'static) -> Self {
        Self(Arc::new(Mutex::new(ScriptedState {
            script: Box::new(script),
            wire: Vec::new(),
            pending: String::new(),
            received: Vec::new(),
            open: true,
        })))
    }

    /// Puts bytes on the wire that belong to no request: the tail of an earlier
    /// exchange, still on its way.
    pub fn interject(&self, text: &str, after: Duration) {
        let at = Instant::now() + after;
        let mut s = self.0.lock().unwrap();
        for c in text.chars() {
            s.wire.push((at, c as u8));
        }
    }

    #[allow(dead_code)]
    pub fn received(&self) -> Vec<String> {
        self.0.lock().unwrap().received.clone()
    }
}

impl EcuTransport for ScriptedElm {
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

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut s = self.0.lock().unwrap();
        for &b in data {
            if b != b'\r' {
                s.pending.push(char::from(b));
                continue;
            }

            let command = s.pending.trim().to_string();
            s.pending.clear();
            s.received.push(command.clone());

            let sent = Instant::now();
            let pieces = (s.script)(&command);
            for p in pieces {
                for c in p.text.chars() {
                    s.wire.push((sent + p.after, c as u8));
                }
            }
        }
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
        let deadline = Instant::now() + timeout;

        loop {
            {
                let mut s = self.0.lock().unwrap();
                let mut taken = 0;

                while taken < buffer.len() && !s.wire.is_empty() && s.wire[0].0 <= Instant::now() {
                    buffer[taken] = s.wire.remove(0).1;
                    taken += 1;
                }

                // Anything at all is enough to return, as a radio transport does:
                // the caller is reading a byte at a time looking for the prompt.
                if taken > 0 {
                    return Ok(taken);
                }
            }

            if Instant::now() >= deadline {
                return Ok(0);
            }

            std::thread::sleep(Duration::from_millis(2));
        }
    }

    fn discard_input(&mut self) {
        let now = Instant::now();
        self.0.lock().unwrap().wire.retain(|b| b.0 > now);
    }
}

/// How a car answers a request carrying more than one parameter.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
#[allow(dead_code)]
pub enum BatchReply {
    /// NO DATA, as a car that does not understand the request.
    Refuse,
    /// Only the first parameter listed, in the ordinary shape.
    First,
    /// Every parameter it has, as one run of groups.
    All,
}

pub struct FakeState {
    out: VecDeque<u8>,
    input: String,
    pub answers: HashMap<u8, Vec<u8>>,
    pub extra_answers: HashMap<u8, Vec<Vec<u8>>>,
    pub stored_codes: Vec<String>,
    pub pending_codes: Vec<String>,
    pub permanent_codes: Vec<String>,
    pub protocol_number: i32,
    pub refuse_clear: bool,
    pub clear_requests: u32,
    pub elm327_name: String,
    pub product: String,
    pub firmware: String,
    pub echo: bool,
    pub sticky_echo: bool,
    pub spaces: bool,
    pub searching: bool,
    pub garble: bool,
    pub received: Vec<String>,
    pub open: bool,
    pub batches: BatchReply,
    pub omit_from_batch: HashSet<u8>,
}

/// An ELM327 adapter and a car behind it, in software.
///
/// Faithful to the awkward parts rather than to a tidy idea of the protocol: the
/// adapter echoes commands until it is told not to, inserts spaces until it is told
/// not to, announces "SEARCHING..." on its own line before the first reply, says
/// "NO DATA" for a parameter this car does not have, and ends every exchange with a
/// `>` prompt. Each of those has broken a real client.
#[derive(Clone)]
pub struct FakeElm(pub Arc<Mutex<FakeState>>);

impl FakeElm {
    pub fn new() -> Self {
        Self(Arc::new(Mutex::new(FakeState {
            out: VecDeque::new(),
            input: String::new(),
            answers: HashMap::new(),
            extra_answers: HashMap::new(),
            stored_codes: Vec::new(),
            pending_codes: Vec::new(),
            permanent_codes: Vec::new(),
            protocol_number: 6,
            refuse_clear: false,
            clear_requests: 0,
            elm327_name: "ELM327 v1.5".to_string(),
            product: String::new(),
            firmware: String::new(),
            echo: true,
            sticky_echo: false,
            spaces: true,
            searching: true,
            garble: false,
            received: Vec::new(),
            open: false,
            batches: BatchReply::Refuse,
            omit_from_batch: HashSet::new(),
        })))
    }

    pub fn with<R>(&self, f: impl FnOnce(&mut FakeState) -> R) -> R {
        f(&mut self.0.lock().unwrap())
    }

    pub fn received(&self) -> Vec<String> {
        self.with(|s| s.received.clone())
    }
}

impl EcuTransport for FakeElm {
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

fn hex_with(spaces: bool, bytes: &[u8]) -> String {
    let pairs: Vec<String> = bytes.iter().map(|b| format!("{b:02X}")).collect();
    if spaces { pairs.join(" ") } else { pairs.concat() }
}

impl FakeState {
    fn hex(&self, bytes: &[u8]) -> String {
        hex_with(self.spaces, bytes)
    }

    fn can(&self) -> bool {
        self.protocol_number >= 6
    }

    fn say(&mut self, line: &str) {
        for c in line.chars() {
            self.out.push_back(c as u8);
        }
        self.out.push_back(b'\r');
    }

    fn prompt(&mut self) {
        self.out.push_back(b'\r');
        self.out.push_back(b'>');
    }

    fn answer(&mut self, command: &str) {
        self.received.push(command.to_string());

        if self.garble {
            // Framing errors at the wrong speed turn the reply into whatever the
            // bits happen to land on. Deliberately hex-ish: rubbish that parses as
            // nothing would be too easy to reject.
            self.say("\u{fe}\u{c3}ADF3\u{b0}\u{c0}");
            self.prompt();
            return;
        }

        if self.echo {
            self.say(command);
        }

        let upper = command.to_ascii_uppercase();

        match upper.as_str() {
            "ATZ" => {
                self.echo = true;
                self.spaces = true;
                let name = self.elm327_name.clone();
                self.say(&name);
            }
            "ATI" => {
                let name = self.elm327_name.clone();
                self.say(&name);
            }
            "ATE0" => {
                self.echo = self.sticky_echo;
                self.say("OK");
            }
            "ATS0" => {
                self.spaces = false;
                self.say("OK");
            }

            // Both forms of "which protocol did you settle on". The "A" says it was
            // found by searching rather than set, and it is on the front of the
            // number rather than instead of it.
            "ATDP" => {
                let text = format!("AUTO, {}", self.protocol_description());
                self.say(&text);
            }
            "ATDPN" => {
                let text = format!("A{:X}", self.protocol_number);
                self.say(&text);
            }

            // The ST commands, which only the STN chips behind an OBDLink have. A
            // plain ELM327 and every clone answer "?", which is the thing that tells
            // one from the other, so the fake has to refuse them properly rather
            // than not recognise them at all.
            "STDI" => {
                let text = if self.product.is_empty() {
                    "?".to_string()
                } else {
                    self.product.clone()
                };
                self.say(&text);
            }
            "STI" => {
                let text = if self.firmware.is_empty() {
                    "?".to_string()
                } else {
                    self.firmware.clone()
                };
                self.say(&text);
            }
            s if s.starts_with("ST") => self.say("?"),

            s if s.starts_with("AT") => self.say("OK"),

            "03" => self.faults(0x03),
            "07" => self.faults(0x07),
            "0A" => self.faults(0x0A),
            "04" => self.erase(),

            _ => self.mode01(command),
        }

        self.prompt();
    }

    fn protocol_description(&self) -> &'static str {
        match self.protocol_number {
            1 => "SAE J1850 PWM",
            2 => "SAE J1850 VPW",
            3 => "ISO 9141-2",
            4 => "ISO 14230-4 KWP (5 baud init)",
            5 => "ISO 14230-4 KWP (fast init)",
            7 => "ISO 15765-4 (CAN 29/500)",
            _ => "ISO 15765-4 (CAN 11/500)",
        }
    }

    /// Erases what the standard allows, which is not everything.
    ///
    /// Permanent codes survive on purpose, because that is the whole reason the
    /// mode exists, and a fake that wiped them would let a bug through that only a
    /// car built after 2010 could catch.
    fn erase(&mut self) {
        self.clear_requests += 1;

        if self.refuse_clear {
            // A negative response: mode, then the code for conditions not correct.
            let text = self.hex(&[0x7F, 0x04, 0x22]);
            self.say(&text);
            return;
        }

        self.stored_codes.clear();
        self.pending_codes.clear();

        let text = self.hex(&[0x44]);
        self.say(&text);
    }

    /// Answers one of the fault modes the way the real protocols do: CAN and short
    /// enough to fit (one line, with a count byte), CAN and too long (a total
    /// length, then fragments "0:", "1:" and so on, six data bytes in the first and
    /// seven in the rest), or anything older (one line per three codes, no count,
    /// zero-padded).
    fn faults(&mut self, mode: u8) {
        let echoed = obd2_faults::echoes(mode);
        let codes = match mode {
            0x03 => self.stored_codes.clone(),
            0x07 => self.pending_codes.clone(),
            _ => self.permanent_codes.clone(),
        };

        if self.searching {
            self.say("SEARCHING...");
            self.searching = false;
        }

        let mut pairs: Vec<u8> = Vec::new();
        for c in &codes {
            let (high, low) = obd2_faults::encode(c).unwrap();
            pairs.push(high);
            pairs.push(low);
        }

        if !self.can() {
            // Three codes to a message, padded out to a full six data bytes, and no
            // count anywhere, which is the whole difficulty.
            if pairs.is_empty() {
                self.say("NO DATA");
                return;
            }

            for chunk in pairs.chunks(6) {
                let mut message = [0u8; 7];
                message[0] = echoed;
                message[1..=chunk.len()].copy_from_slice(chunk);

                let text = self.hex(&message);
                self.say(&text);
            }

            return;
        }

        let mut payload = vec![echoed, codes.len() as u8];
        payload.extend_from_slice(&pairs);

        self.say_frames(&payload, true);
    }

    /// Prints a payload the way an adapter with headers off does: one line while it
    /// fits in a CAN frame, and beyond that a total length followed by numbered
    /// segments, "0:" carrying six bytes and each one after it seven (padded to
    /// seven when `pad` is set, as the fault replies are).
    fn say_frames(&mut self, payload: &[u8], pad: bool) {
        if payload.len() <= 7 {
            let text = self.hex(payload);
            self.say(&text);
            return;
        }

        let total = format!("{:03X}", payload.len());
        self.say(&total);

        let mut taken = payload.len().min(6);
        let text = format!("0:{}", self.hex(&payload[..taken]));
        self.say(&text);

        let mut frame = 1;
        while taken < payload.len() {
            let size = (payload.len() - taken).min(7);
            let mut piece = payload[taken..taken + size].to_vec();
            if pad {
                piece.resize(7, 0);
            }

            let text = format!("{frame:X}:{}", self.hex(&piece));
            self.say(&text);
            taken += size;
            frame += 1;
        }
    }

    fn mode01(&mut self, command: &str) {
        if command.len() < 4 || !command.len().is_multiple_of(2) || !command[..2].eq_ignore_ascii_case("01") {
            self.say("?");
            return;
        }

        let mut pids: Vec<u8> = Vec::new();
        let mut at = 2;
        while at < command.len() {
            let Ok(one) = u8::from_str_radix(&command[at..at + 2], 16) else {
                self.say("?");
                return;
            };
            pids.push(one);
            at += 2;
        }

        if self.searching {
            self.say("SEARCHING...");
            self.searching = false;
        }

        if pids.len() > 1 {
            self.answer_batch(&pids);
            return;
        }

        let pid = pids[0];

        // Ahead of the real one, so anything that takes the first answer takes the
        // wrong one.
        let others = self.extra_answers.get(&pid).cloned();
        if let Some(others) = &others {
            for other in others {
                let mut bytes = vec![0x41, pid];
                bytes.extend_from_slice(other);
                let text = self.hex(&bytes);
                self.say(&text);
            }
        }

        let Some(data) = self.answers.get(&pid).cloned() else {
            if others.is_none_or(|o| o.is_empty()) {
                self.say("NO DATA");
            }
            return;
        };

        let mut bytes = vec![0x41, pid];
        bytes.extend_from_slice(&data);
        let text = self.hex(&bytes);
        self.say(&text);
    }

    /// Answers a request that carried several parameters: one 0x41 and then a run of
    /// (parameter, data) with nothing between the groups, which is what makes the
    /// reply only readable by somebody who knows how long each parameter is.
    fn answer_batch(&mut self, pids: &[u8]) {
        if self.batches == BatchReply::Refuse {
            self.say("NO DATA");
            return;
        }

        let mut payload = vec![0x41u8];
        let listed: &[u8] = if self.batches == BatchReply::First {
            &pids[..1]
        } else {
            pids
        };

        for pid in listed {
            if self.omit_from_batch.contains(pid) {
                continue;
            }
            let Some(data) = self.answers.get(pid) else { continue };

            payload.push(*pid);
            payload.extend_from_slice(data);
        }

        if payload.len() == 1 {
            self.say("NO DATA");
            return;
        }

        self.say_frames(&payload, false);
    }
}
