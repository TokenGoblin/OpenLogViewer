//! Ported from `Elm327.cs`: talking to an ELM327 OBD2 adapter.
//!
//! Unlike every other link here this one is text. A command is ASCII terminated by a
//! carriage return, a reply is ASCII hex, and the adapter signals that it has finished
//! and is ready for the next one by sending `>`. That prompt is the only reliable
//! end-of-reply marker: replies are not fixed length, "SEARCHING..." can appear before
//! the data, and a multi-line reply from a car with several responding modules has no
//! other terminator.
//!
//! The AT commands are the adapter's own; anything else is passed to the car.
//!
//! I/O stays behind [`EcuTransport`]. The C# serialised commands with a lock, because
//! fault scanning is asked for from the user interface while the poll loop is still
//! running. Here every command takes `&mut self`, so the caller shares the adapter
//! behind a `Mutex` and the same guarantee holds: two commands written into the same
//! adapter interleave into one stream, and both would parse while neither would be
//! true.

use std::time::{Duration, Instant};

use olv_core::Result;

use crate::ms::ms_protocol::EcuTransport;

/// The lowest ELM327 protocol number that is a CAN variant.
pub const FIRST_CAN_PROTOCOL: i32 = 6;

/// Parameters one request may carry, which the standard puts at six.
///
/// The cost of OBD2 is round trips rather than bytes, so this is the single biggest
/// thing available to it: fifteen exchanges become three.
pub const MAX_BATCH_PIDS: usize = 6;

/// The commands that put the adapter into the only mode this can parse.
const SETUP: [&str; 5] = ["ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"];

/// One parameter's worth of a batched reply.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BatchAnswer {
    pub pid: u8,
    pub data: Vec<u8>,
}

/// An ELM327 adapter on the far side of a transport.
pub struct Elm327 {
    transport: Box<dyn EcuTransport>,

    /// How long to wait for the adapter to finish answering.
    ///
    /// Generous for a link this slow. A clone answers a known PID in 40 to 100 ms, but
    /// the first request after a reset can take seconds while it works out which of
    /// nine OBD2 protocols the car speaks.
    pub timeout: Duration,

    /// What a reset is given, which is longer than anything else.
    pub reset_timeout: Duration,

    /// Silence, with an answer already in hand, that counts as the end of it.
    ///
    /// The prompt is supposed to be the only thing that means finished, and on a
    /// well-behaved adapter it is. A Vgate iCar Pro sends it on roughly 60 to 80 % of
    /// reads: measured against a live car, the rest ran out the whole window with a
    /// complete payload sitting in the buffer and nothing but the terminator missing.
    /// No window is long enough for a character that is not coming, so quiet has to be
    /// able to finish a reply too.
    ///
    /// **This must stay longer than the adapter's own response timeout.** An ELM327
    /// waits `ATST` (about 205 ms by default) for the car before giving up and printing
    /// NO DATA, and a gap shorter than that fires while the adapter is still working.
    /// Ending a read early is not free: a command sent before a prompt arrives is
    /// answered "STOPPED", which ends the next read early in turn and sustains itself.
    pub idle_gap: Duration,

    /// Longest the line will be drained before giving up on it going quiet.
    pub drain_at_most: Duration,

    /// Longest wait for an answer that missed its own read, before concluding that it
    /// really is not coming.
    ///
    /// Bounded, because an adapter that has genuinely gone silent must cost this once
    /// rather than on every command for the rest of the session. An answer later than
    /// this is not caught here at all: it is caught by the reply carrying the number of
    /// the parameter it answers, which is why a late one costs a reading rather than
    /// putting one channel's value on another's gauge.
    pub tail_wait: Duration,

    /// How long the line must be silent before it counts as settled.
    ///
    /// Longer than it needs to be for a tidy adapter, because the ones that need
    /// settling at all are the untidy ones: the tail of a Vgate's previous exchange has
    /// been measured arriving better than 80 ms behind the reply it belongs to.
    pub quiet_for: Duration,

    protocol: Option<i32>,

    /// Set by a read that ended on its timeout, so the next one waits for the late
    /// answer to arrive and be thrown away before asking anything.
    resync: bool,
}

impl Elm327 {
    pub fn new(transport: Box<dyn EcuTransport>) -> Self {
        Self {
            transport,
            timeout: Duration::from_secs(2),
            reset_timeout: Duration::from_secs(5),
            idle_gap: Duration::from_millis(300),
            drain_at_most: Duration::from_secs(1),
            tail_wait: Duration::from_millis(400),
            quiet_for: Duration::from_millis(150),
            protocol: None,
            resync: false,
        }
    }

    /// Resets the adapter and puts it into the only mode this can parse.
    ///
    /// Order matters, and none of it is optional:
    ///
    /// - `ATZ`: full reset, so a previous session's settings are gone.
    /// - `ATE0`: echo off. Left on, every reply is preceded by the command that caused
    ///   it, which parses as leading hex.
    /// - `ATL0`, `ATS0`: no linefeeds, no spaces, so a reply is one run of hex digits.
    /// - `ATH0`: no headers, so the reply is the data and not the CAN addressing
    ///   around it.
    /// - `ATSP0`: try each OBD2 protocol until one answers, rather than requiring the
    ///   user to know which of them their car speaks.
    ///
    /// `within` is how long to allow, where the caller has reason to want something
    /// other than `reset_timeout`: a second attempt after a first that heard nothing is
    /// asking a different question and deserves a shorter answer.
    ///
    /// Returns what the adapter calls itself, e.g. "ELM327 v1.5".
    pub fn reset(&mut self, within: Option<Duration>) -> Result<String> {
        let identity = self.send("ATZ", within.unwrap_or(self.reset_timeout), false, 0)?;

        // A reset undoes the protocol search along with everything else, and a
        // reconnection may land on a different car.
        self.protocol = None;

        // NOTHING AT ALL came back: not a banner, not noise. Every ELM327 answers ATZ
        // out of its own firmware without troubling the car, so this is a link that has
        // stopped existing rather than an adapter that is slow, asleep, or set to the
        // wrong speed. Configuring five options on it costs a timeout apiece and
        // changes nothing that is there.
        //
        // Noise is a different matter and still gets the full treatment: that is what a
        // wrong baud rate looks like, and the adapter is real.
        if identity.is_empty() {
            return Ok(String::new());
        }

        for command in SETUP {
            self.send(command, self.timeout, false, 0)?;
        }

        Ok(clean(&identity))
    }

    /// Asks the car something and throws the answer away.
    ///
    /// The first request after `ATSP0` is the one that triggers the protocol search,
    /// and the adapter narrates it: "SEARCHING..." arrives ahead of the data, seconds
    /// can pass, and the word is not inert, since S, E, A, R, C and H all pass a hex
    /// parser as digits. Spending that on a request nobody reads makes every request
    /// after it an ordinary one, which matters more since a reply can now be finished
    /// by quiet: a pause in the middle of a protocol search would otherwise end a read
    /// on the word "SEARCHING" alone.
    pub fn warm_up(&mut self) -> Result<()> {
        self.send("0100", self.reset_timeout, false, 0)?;
        Ok(())
    }

    /// What the adapter actually is, which is not what it says it is.
    ///
    /// Every OBD2 adapter answers `ATI` with an ELM327 version, genuine or otherwise,
    /// because everything ever written for these expects one: an OBDLink r2.6 reports
    /// "ELM327 v1.3a", and the real ELM327 v1.3a it is claiming to be was superseded a
    /// decade before this device was built.
    ///
    /// The STN chips that OBDLink and a few others are built on answer two more
    /// questions that a clone does not: `STDI` with the product ("OBDLink r2.6") and
    /// `STI` with the firmware ("STN1100 v2.2.2"). So this both names the hardware
    /// properly and, by getting an answer at all, distinguishes a real one from a copy.
    ///
    /// Falls back to the ELM327 name, which is what an adapter without the extended
    /// command set has to offer. An unsupported command is answered with "?" rather
    /// than an error, so that is the thing to recognise.
    pub fn identify(&mut self) -> Result<String> {
        let ati = self.send("ATI", self.timeout, false, 0)?;
        let elm = clean(&ati);

        let product = self.extended("STDI")?;
        let firmware = self.extended("STI")?;

        Ok(match (!product.is_empty(), !firmware.is_empty(), !elm.is_empty()) {
            (true, true, _) => format!("{product} ({firmware})"),
            (true, false, _) => product,
            (false, true, true) => format!("{elm} ({firmware})"),
            (false, true, false) => firmware,
            _ => elm,
        })
    }

    /// Which OBD2 protocol the adapter settled on, in its own words.
    ///
    /// "ISO 15765-4 (CAN 11/500)" on anything modern, and one of the older serial buses
    /// on a car from before CAN was mandated. The "AUTO, " prefix the adapter adds after
    /// a protocol search is dropped: it says how the protocol was arrived at rather than
    /// what it is.
    pub fn protocol_name(&mut self) -> Result<String> {
        let reply = self.extended("ATDP")?;
        if reply.is_empty() {
            return Ok(String::new());
        }

        const AUTOMATIC: &str = "AUTO,";

        Ok(match strip_prefix_ignore_case(&reply, AUTOMATIC) {
            Some(rest) => rest.trim().to_string(),
            None => reply,
        })
    }

    /// Whether the car is on CAN, which decides how a fault reply is laid out.
    ///
    /// Not a detail that can be skipped over. A mode 03 reply on CAN carries the number
    /// of codes immediately after the mode echo and on the older serial protocols it
    /// does not, so the same six bytes are one fault code or two depending on this
    /// answer, and the wrong reading produces codes that look perfectly plausible and
    /// are not on the car.
    ///
    /// Assumed CAN when the adapter will not say, that being every vehicle built since
    /// the requirement came in; but only for reading a fault reply, where a guess has
    /// to be made either way. Nothing that can simply not be done may be gated on this;
    /// see [`Elm327::protocol_number`].
    pub fn is_can(&mut self) -> Result<bool> {
        let n = self.protocol_number()?;

        Ok(if n >= FIRST_CAN_PROTOCOL {
            true
        } else {
            // 1..=5 are the serial buses; anything else is unknown, and guessed CAN.
            n < 1
        })
    }

    /// The protocol the adapter settled on, by number, or -1 where it will not say.
    ///
    /// Kept separate from [`Elm327::is_can`] because the two questions are not the same
    /// one, and treating them as the same has bitten. "Not slow" is not "is CAN": an
    /// unknown protocol is neither, and both answers must be no. An asleep ECU
    /// answering `STOPPED` parses as unknown, and a CAN-only request sent on the
    /// strength of that reaches a J1850 truck as a malformed one. So anything optional
    /// (batching, above all) is gated on a positive identification, and only the
    /// unavoidable guess falls back to CAN.
    ///
    /// `ATDPN` answers with the number, prefixed "A" where it was found by searching: 1
    /// to 5 are the J1850 and ISO serial buses, 6 upwards are the CAN variants.
    pub fn protocol_number(&mut self) -> Result<i32> {
        if let Some(known) = self.protocol {
            return Ok(known);
        }

        let reply = self.extended("ATDPN")?;

        // The trailing digit is the protocol; anything before it is the "A" that says
        // it was searched for rather than set.
        let number = reply.chars().next_back().map_or('\0', |c| c.to_ascii_uppercase());

        let protocol = match number {
            '1'..='9' => number as i32 - '0' as i32,
            'A'..='C' => number as i32 - 'A' as i32 + 10,
            _ => -1,
        };

        // Protocol zero is "automatic, nothing decided yet": the adapter has been told
        // to search and has not yet succeeded. That is not an answer and must not be
        // cached as one: a link that settles on ISO 9141 a moment later would spend the
        // rest of the session being read as CAN, which puts a count byte where a fault
        // code's first half is.
        if protocol > 0 {
            self.protocol = Some(protocol);
        }

        Ok(protocol)
    }

    /// One of the ST commands, or empty where the adapter has no such thing.
    ///
    /// An ELM327 answers an unknown command with "?", and a clone that never heard of
    /// these answers that or nothing at all. Either way there is no product name to be
    /// had, which is itself worth knowing.
    fn extended(&mut self, command: &str) -> Result<String> {
        let reply = self.send(command, self.timeout, false, 0)?;

        for line in split_lines(&reply) {
            let text = line.trim();

            // The echo of the command comes back when echo is still on, and "?" is how
            // anything unrecognised is refused.
            if text.is_empty() || text == "?" {
                continue;
            }
            if text.eq_ignore_ascii_case(command) {
                continue;
            }
            if text.eq_ignore_ascii_case("OK") {
                continue;
            }

            return Ok(text.to_string());
        }

        Ok(String::new())
    }

    /// Sends one command and returns everything up to the prompt.
    ///
    /// Line breaks are kept. An adapter puts each response on its own line and says
    /// "SEARCHING..." on a line of its own while it works out the protocol, and the
    /// letters in that word are mostly hex digits, so run together with the reply that
    /// follows it they decode as a different reading. Keeping the lines apart is what
    /// makes them separable.
    ///
    /// `expected_bytes` is how many bytes a well-formed answer to this carries, where
    /// that is known in advance: the echoed mode and parameter plus its data. Zero for
    /// anything whose length depends on the answer.
    ///
    /// This is what stops a late prompt costing the whole idle gap on every single
    /// read. Measured on a Vgate: the prompt trails the payload by about 210 ms, so a
    /// reply complete at 60 ms was not acted on until 260, and with batching off, that
    /// gap *is* the poll cycle. Knowing the length turns the reply itself into the
    /// terminator.
    pub fn send(&mut self, command: &str, timeout: Duration, settle: bool, expected_bytes: usize) -> Result<String> {
        // Anything still buffered belongs to the previous exchange: a reply that
        // arrived after its timeout, or the tail of one that was abandoned. Read as the
        // front of this answer it would decode as a different reading altogether, which
        // is worse than a slow one.
        self.transport.discard_input();

        // Discarding is not enough after a read that timed out. A timeout means the
        // adapter was late, not absent, so its answer is still on its way, and what has
        // not arrived yet cannot be thrown away. It lands in this read instead, and
        // every reply stays one command behind for the rest of the session.
        if self.resync {
            self.wait_for_quiet(Some(self.tail_wait))?;
        } else if settle {
            self.wait_for_quiet(None)?;
        }

        self.resync = false;

        self.transport.write(&ascii_bytes(&format!("{command}\r")))?;

        self.read_to_prompt(command, timeout, expected_bytes)
    }

    /// Waits until nothing more is arriving, throwing away whatever does.
    ///
    /// Discarding what is buffered is not enough on its own, because the tail of the
    /// previous reply may not have arrived yet to be discarded. The piece that matters
    /// is the "\r\r>" on the end of it: still in flight when the next command goes out,
    /// it is read as *this* reply's prompt and cuts the answer off before the rest of
    /// it lands.
    ///
    /// That truncation is invisible on a single-module reply, which is complete by then
    /// anyway. It shows up on a car where several modules answer: the second one's line
    /// is the part that goes missing, and with it every parameter only that module
    /// supports. The same car reported 3, then 15, then 24 parameters on consecutive
    /// connections before this was here.
    ///
    /// Only worth its cost where the answer is not repeated. A poll that loses a sample
    /// gets another in half a second.
    ///
    /// `first_byte` is how long to wait for the late answer to *start* arriving, where
    /// there is reason to believe one is coming. Quiet alone does not cover the case a
    /// timeout leaves behind: the line is quiet at that moment precisely because the
    /// adapter has not answered yet, so a drain that gives up on the first silence
    /// proves nothing. Once anything arrives the ordinary quiet rule takes over.
    fn wait_for_quiet(&mut self, first_byte: Option<Duration>) -> Result<()> {
        let mut wait = first_byte.unwrap_or(self.quiet_for);

        // Bounded, because "wait for silence" is not a plan against something that
        // never goes silent. An adapter stuck repeating itself would otherwise hold
        // this loop for the life of the session.
        let deadline = deadline_after(self.drain_at_most);
        let mut one = [0u8; 1];

        while Instant::now() < deadline && self.transport.read(&mut one, wait)? == 1 {
            // Deliberately dropped: this is the previous exchange finishing.
            wait = self.quiet_for;
        }

        Ok(())
    }

    /// Reads one reply: everything up to the prompt, or up to the point where the
    /// adapter plainly has nothing more to say.
    ///
    /// Three rules, and each of them was learnt from an adapter rather than from the
    /// datasheet:
    ///
    /// - A prompt always finishes a reply.
    /// - Quiet finishes one too, but only with something in hand that is more than the
    ///   command coming back. This adapter ignores `ATE0`, echoes, pauses, and only then
    ///   sends the data, so an echo treated as an answer completes the read before the
    ///   answer exists, and the answer then lands in the next command's window.
    /// - Silence finishes nothing. A read that has received no answer waits out the
    ///   whole timeout, because nothing arriving is not a short reply.
    /// - And where the answer's length is known before it is asked for, the answer
    ///   finishes itself; see [`complete`].
    fn read_to_prompt(&mut self, command: &str, timeout: Duration, expected_bytes: usize) -> Result<String> {
        let mut reply = String::new();
        let deadline = deadline_after(timeout);
        let mut one = [0u8; 1];

        loop {
            let remaining = match deadline.checked_duration_since(Instant::now()) {
                Some(r) if !r.is_zero() => r,
                _ => break,
            };

            let anything = has_payload(&reply);

            // One byte at a time, so this returns the moment the prompt arrives rather
            // than waiting out the timeout for a buffer that will never fill. The
            // transport blocks until at least one byte is there: for the gap once an
            // answer has started, and for the whole window while there is still nothing
            // to be quiet after.
            let wait = if anything && !self.idle_gap.is_zero() && self.idle_gap < remaining {
                self.idle_gap
            } else {
                remaining
            };

            let asked = Instant::now();

            if self.transport.read(&mut one, wait)? != 1 {
                if Instant::now() >= deadline {
                    break;
                }

                // Quiet. Finished, unless all that has arrived is the echo.
                if anything && !is_only_echo(&reply, command) {
                    return Ok(reply);
                }

                // NOTHING, AND BACK EARLY. A read handed the whole remaining window and
                // returning empty before that window is up did not wait at all: it has
                // nothing left to wait for, which for a socket is a far end that has
                // closed. Going round again does not sleep either, so it is a core spun
                // flat out until the deadline, two seconds a command and five on a
                // reset, on exactly the failure these adapters are known for.
                //
                // The same is true once an echo has arrived, and that case is not caught
                // by comparing the window: the wait is the idle gap by then rather than
                // the whole of what is left. So the test is whether the read spent the
                // time it was given. One that came back in a fraction of it was not
                // waiting on anything, and going round again only spins faster.
                if wait == remaining {
                    break;
                }
                if asked.elapsed() < wait / 2 {
                    break;
                }

                continue;
            }

            let c = char::from(one[0]);

            if c == '>' {
                // A PROMPT WITH NOTHING BEFORE IT IS NOT AN ANSWER. It is the one left
                // over from the previous exchange, which finished on the gap with its
                // own prompt still in flight, so it could not have been drained because
                // it had not been sent. Taken at face value it completes this read with
                // nothing in it and the real answer arrives one command late, for ever.
                // Reading on is safe: the adapter sends another prompt after the real
                // reply, and the timeout still bounds the wait.
                if !anything {
                    continue;
                }

                return Ok(reply);
            }

            reply.push(c);

            // The whole answer is here, and nothing is owed but its terminator.
            if expected_bytes > 0 && complete(&reply, command, expected_bytes) {
                return Ok(reply);
            }
        }

        // Out of time. Whatever arrived is still worth returning (the caller decides
        // whether it parses) but the next command has to wait for the late answer first.
        self.resync = true;

        Ok(reply)
    }

    /// Asks for one mode-01 parameter and returns its data bytes.
    ///
    /// `None` when the car did not answer with that parameter: "NO DATA" for one it does
    /// not support, "SEARCHING..." before the protocol is settled, an error, or silence.
    /// All of those are ordinary and none is worth an error; the caller decides what a
    /// run of them means.
    ///
    /// Fills `into` and returns the count, as the C# `TryRead` did.
    pub fn try_read(
        &mut self,
        pid: u8,
        data_bytes: usize,
        into: &mut [u8],
        within: Option<Duration>,
    ) -> Result<Option<usize>> {
        // The echoed mode and parameter, then its data: a length the standard fixes, so
        // the reply can end itself rather than waiting for a prompt that may be 200 ms
        // behind it.
        let reply = self.send(
            &format!("01{pid:02X}"),
            within.unwrap_or(self.timeout),
            false,
            2 + data_bytes,
        )?;

        Ok(try_parse(&reply, pid, data_bytes, into))
    }

    /// Asks for one parameter and returns every module's answer.
    ///
    /// For a reading, the first answer will do: the modules are reporting the same
    /// number. For a capability mask they are not: each module reports what it alone
    /// supports, and taking one of them means the rest of the car goes unread. Which
    /// module answers first is not fixed, so this is also the difference between a
    /// connection that finds everything and one that finds almost nothing, on the same
    /// car a minute apart.
    pub fn read_all(&mut self, pid: u8, data_bytes: usize, within: Option<Duration>) -> Result<Vec<Vec<u8>>> {
        let reply = self.send(&format!("01{pid:02X}"), within.unwrap_or(self.timeout), true, 0)?;

        Ok(parse_all(&reply, pid, data_bytes))
    }

    /// Asks for several parameters in one request.
    ///
    /// ISO 15765 only: a request like this reaches a J1850 vehicle as a malformed one,
    /// so the caller must have identified the bus positively rather than merely failed
    /// to identify it as slow.
    ///
    /// A parameter whose length this does not know is left out rather than asked for: a
    /// batched reply is a run of (parameter, data) with nothing to separate the groups,
    /// so one group of unknown length loses every group after it. Singly it does not
    /// matter, because there the length is the caller's to state.
    pub fn read_many(
        &mut self,
        pids: &[u8],
        length_of: &dyn Fn(u8) -> usize,
        within: Option<Duration>,
    ) -> Result<Vec<BatchAnswer>> {
        let mut asked: Vec<u8> = Vec::new();
        for &p in pids {
            if length_of(p) > 0 && !asked.contains(&p) {
                asked.push(p);
            }
        }
        asked.truncate(MAX_BATCH_PIDS);
        if asked.len() < 2 {
            return Ok(Vec::new());
        }

        let command = format!("01{}", asked.iter().map(|p| format!("{p:02X}")).collect::<String>());

        // One 0x41 and then each parameter with its data. Only ever reached by a reply
        // that stayed in one frame, since a segmented one carries markers and those stop
        // the length being counted at all.
        let expected: usize = 1 + asked.iter().map(|&p| 1 + length_of(p)).sum::<usize>();

        let reply = self.send(&command, within.unwrap_or(self.timeout), false, expected)?;

        Ok(parse_batch(&reply, &asked, length_of))
    }
}

/// Pulls the parameters out of a batched reply.
///
/// Harder than the single-parameter case in three ways, each of which has produced a
/// real defect:
///
/// - The reply is multi-frame, and an adapter prints those as a length header and
///   segment-marked lines: "0:", "1:". Those markers are valid hex digits, so anything
///   that strips non-hex characters and pairs the rest turns the "0" of "0:" into half
///   a byte and shifts everything after it by a nibble. Each line, and each piece of a
///   line, is therefore converted on its own, and a trailing half-byte is dropped with
///   it, which is also what makes a three-digit length header harmless.
/// - There is nothing between the groups. A group is a parameter number and however
///   many bytes that parameter is defined to carry, so the walk is only in step for as
///   long as every number is recognised; one that is not stops it rather than guessing a
///   length.
/// - More than one module answers, and the reply can be two responses run together.
///   Anchoring on the first plausible start is not enough: on a live car a leading
///   fragment came first, and taking it yielded one parameter of four, which reads as
///   "batching does not work here" and costs the whole feature. So every candidate
///   start is scored by how much of the reply it explains and the best one wins,
///   earliest on a tie.
pub fn parse_batch(reply: &str, asked: &[u8], length_of: &dyn Fn(u8) -> usize) -> Vec<BatchAnswer> {
    let bytes = flatten(reply);
    let mut best: Vec<BatchAnswer> = Vec::new();

    let mut i = 0;
    while i + 1 < bytes.len() {
        // The positive answer, followed by something that was actually asked for. A
        // lone 0x41 also occurs inside data, and any length header sits in front of the
        // real one.
        if bytes[i] == 0x41 && asked.contains(&bytes[i + 1]) {
            let walked = walk(&bytes, i + 1, asked, length_of);

            if walked.len() > best.len() {
                best = walked;
            }
            if best.len() == asked.len() {
                break;
            }
        }

        i += 1;
    }

    best
}

/// The hex of a reply, converted line by line and piece by piece so that a segment
/// marker cannot shift the bytes after it.
fn flatten(reply: &str) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(64);
    let mut piece = [0u8; 64];

    for line in split_lines(reply) {
        // Split on the marker's colon rather than filtering it out afterwards: by then
        // its digit has already been paired with the byte in front of it.
        for part in line.split(':') {
            let got = unhex(part, &mut piece);
            bytes.extend_from_slice(&piece[..got]);
        }
    }

    bytes
}

/// Reads (parameter, data) groups from one starting point until they stop making
/// sense.
fn walk(bytes: &[u8], start: usize, asked: &[u8], length_of: &dyn Fn(u8) -> usize) -> Vec<BatchAnswer> {
    let mut found: Vec<BatchAnswer> = Vec::with_capacity(MAX_BATCH_PIDS);

    let mut at = start;
    while at < bytes.len() && found.len() < asked.len() {
        let pid = bytes[at];

        // Something nobody asked for: the padding at the end of a frame, or a second
        // module's answer beginning. Either way the groups have run out, and reading on
        // would be reading a length out of somebody else's data.
        if !asked.contains(&pid) {
            break;
        }

        // Repeating itself, which is the same thing said differently.
        if found.iter().any(|a| a.pid == pid) {
            break;
        }

        let length = length_of(pid);
        if length == 0 || at + 1 + length > bytes.len() {
            break;
        }

        found.push(BatchAnswer {
            pid,
            data: bytes[at + 1..at + 1 + length].to_vec(),
        });
        at += 1 + length;
    }

    found
}

/// Every complete answer to one request, one per responding module.
pub fn parse_all(reply: &str, pid: u8, data_bytes: usize) -> Vec<Vec<u8>> {
    let mut answers = Vec::new();

    for line in split_lines(reply) {
        let mut data = vec![0u8; data_bytes];

        if try_parse(line, pid, data_bytes, &mut data).is_some() {
            answers.push(data);
        }
    }

    answers
}

/// Pulls the data bytes out of a mode-01 reply.
///
/// A positive answer echoes the mode with bit 6 set (0x41), then the PID, then the
/// data. Both are checked rather than assumed: a reply that arrives late is answering
/// the previous question, and taking it at face value would put one channel's number on
/// another channel's gauge.
///
/// Each line is tried on its own. A car with more than one responding module answers a
/// single request several times over, one response per line, and the first complete one
/// is as good as any; "SEARCHING..." and "NO DATA" simply fail to match and are passed
/// over.
///
/// Returns the count written to `into` (always `data_bytes`) on success.
pub fn try_parse(reply: &str, pid: u8, data_bytes: usize, into: &mut [u8]) -> Option<usize> {
    let mut bytes = [0u8; 32];

    for line in split_lines(reply) {
        let got = unhex(line, &mut bytes);

        // The echoed mode and PID, plus every data byte that was asked for.
        if got < 2 + data_bytes || bytes[0] != 0x41 || bytes[1] != pid {
            continue;
        }
        if data_bytes > into.len() {
            return None;
        }

        into[..data_bytes].copy_from_slice(&bytes[2..2 + data_bytes]);

        return Some(data_bytes);
    }

    None
}

/// Reads a run of hex digits into bytes, ignoring anything else.
///
/// Tolerant on purpose. Adapters differ over whether they send "41 0C 1A F8" or
/// "410C1AF8", clones prepend "SEARCHING..." unbidden, and a stray character on a
/// Bluetooth link should cost a reading rather than a session. A trailing half-byte is
/// dropped, being a reply that was cut short.
pub fn unhex(text: &str, into: &mut [u8]) -> usize {
    let mut count = 0;
    let mut high: Option<u8> = None;

    for c in text.chars() {
        let Some(nibble) = nibble(c) else { continue };

        let Some(h) = high else {
            high = Some(nibble);
            continue;
        };

        if count == into.len() {
            break;
        }

        into[count] = (h << 4) | nibble;
        count += 1;
        high = None;
    }

    count
}

/// One hex digit, or `None` for anything else.
pub fn nibble(c: char) -> Option<u8> {
    match c {
        '0'..='9' => Some(c as u8 - b'0'),
        'A'..='F' => Some(c as u8 - b'A' + 10),
        'a'..='f' => Some(c as u8 - b'a' + 10),
        _ => None,
    }
}

/// `reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)`.
pub fn split_lines(text: &str) -> impl Iterator<Item = &str> {
    text.split(['\r', '\n']).filter(|l| !l.is_empty())
}

/// What the adapter called itself, or empty if nothing recognisable did.
///
/// A reset is answered while echo is still on, so the command comes back first and the
/// version follows it: "ATZ\rELM327 v1.5". The version is found by looking for the name
/// rather than by taking the last line, since clones pad the reply differently.
///
/// Empty matters as much as the name does. It is how a wrong baud rate is recognised: an
/// adapter being sent noise does not fall silent, it answers with noise, and the one
/// thing that noise reliably is not is its own name. Every OBD2 adapter worth the name
/// reports as an ELM327; the better chips claim it too, for compatibility with
/// everything already written.
fn clean(identity: &str) -> String {
    let chars: Vec<char> = identity.chars().collect();
    let Some(at) = chars.windows(3).position(|w| {
        w[0].eq_ignore_ascii_case(&'E') && w[1].eq_ignore_ascii_case(&'L') && w[2].eq_ignore_ascii_case(&'M')
    }) else {
        return String::new();
    };

    // To the end of that line: what follows is the prompt or the next reply.
    let text: String = chars[at..].iter().collect();
    let end = text.find(['\r', '\n']);

    end.map_or(text.as_str(), |e| &text[..e]).trim().to_string()
}

/// Whether what has arrived is already a whole, well-formed answer of the length this
/// request has to produce.
///
/// The reply then terminates itself and the prompt becomes a formality, which is the
/// difference between a read costing what the car took and a read costing the idle gap
/// on top, on every single request.
///
/// Three things have to hold, and each of them is what stops this firing where it must
/// not:
///
/// - **It begins with 41.** A positive mode-01 answer does. "NO DATA" does not, and
///   needs saying: it is six characters, exactly as long as a one-byte reply, so length
///   alone would take it for one.
/// - **It is nothing but hex.** A well-formed single-frame answer is. The moment
///   anything else appears (the colon of a segment marker, a letter) this is not the
///   reply the length was computed for, and counting on regardless would strike the
///   length mid-reply and truncate it. That is not hypothetical: a second module's
///   fragment ahead of a segmented answer does exactly that.
/// - **The length is known at all.** Anything whose reply length depends on the answer
///   passes zero and never comes here; a capability bitmap most of all, where a second
///   module's answer arrives tens of milliseconds later and both are wanted.
///
/// Every way of being wrong therefore ends in "not complete", which costs the idle gap
/// and nothing else. None of them can end in a short read.
fn complete(reply: &str, command: &str, expected_bytes: usize) -> bool {
    let mut text = reply.trim();

    // Past the echo, which this adapter sends whatever it was told about ATE0. Safe to
    // step over because the caller knows what it asked.
    if let Some(rest) = strip_prefix_ignore_case(text, command) {
        text = rest.trim();
    }

    let mut digits = 0;
    let mut seen: [Option<char>; 4] = [None; 4];

    for c in text.chars() {
        // Whether these are here at all is a setting the adapter may have ignored, as it
        // ignored the one about echo, so they say nothing either way.
        if c.is_whitespace() {
            continue;
        }

        if nibble(c).is_none() {
            return false;
        }

        if let Some(slot) = seen.iter_mut().find(|s| s.is_none()) {
            *slot = Some(c);
        }

        digits += 1;
    }

    if digits != expected_bytes * 2 || seen[0] != Some('4') || seen[1] != Some('1') {
        return false;
    }

    // And the parameter it answers about must be the one that was asked. A late reply to
    // the previous question is the same length as this one whenever both parameters are a
    // single byte: 0x04, 0x05, 0x0B, 0x0D and 0x11 all are, and they are all in the
    // rotation. Taken as complete, the read returns early, the decoder rejects it for
    // naming the wrong parameter, and the right answer arriving a moment later is never
    // collected.
    let command: Vec<char> = command.chars().collect();
    if command.len() >= 4
        && let (Some(third), Some(fourth)) = (seen[2], seen[3])
        && (nibble(third) != nibble(command[2]) || nibble(fourth) != nibble(command[3]))
    {
        return false;
    }

    true
}

/// Whether anything but line endings and spaces has arrived.
fn has_payload(reply: &str) -> bool {
    reply.chars().any(|c| !c.is_whitespace())
}

/// Whether all that has come back is the command itself.
///
/// Safe to strip precisely because the caller knows what it asked: an echo is the exact
/// command and nothing else, so this cannot eat an answer.
fn is_only_echo(reply: &str, command: &str) -> bool {
    if command.is_empty() {
        return false;
    }

    let text = reply.trim();

    text.chars().count() == command.chars().count() && text.eq_ignore_ascii_case(command)
}

/// `text.StartsWith(prefix, OrdinalIgnoreCase)`, returning what follows. Only ASCII
/// letters fold, which covers every command this sends.
fn strip_prefix_ignore_case<'a>(text: &'a str, prefix: &str) -> Option<&'a str> {
    let mut rest = text.char_indices();
    let mut end = 0;

    for p in prefix.chars() {
        let (i, c) = rest.next()?;
        if !c.eq_ignore_ascii_case(&p) {
            return None;
        }
        end = i + c.len_utf8();
    }

    Some(&text[end..])
}

/// `Encoding.ASCII.GetBytes`: anything outside ASCII becomes `?`.
fn ascii_bytes(text: &str) -> Vec<u8> {
    text.chars()
        .map(|c| if c.is_ascii() { c as u8 } else { b'?' })
        .collect()
}

/// `now + span`, without panicking on a span too long to represent.
fn deadline_after(span: Duration) -> Instant {
    let now = Instant::now();
    now.checked_add(span)
        .unwrap_or_else(|| now + Duration::from_secs(60 * 60 * 24 * 365))
}

/// Test doubles shared by this module's tests and the fault tests: an adapter whose
/// bytes arrive when they are told to (`ScriptedElm`), and one that answers instantly
/// and completely, with all the awkward habits of the real ones (`FakeElm`).
#[cfg(test)]
pub(crate) mod test_support {
    use std::collections::{HashMap, HashSet, VecDeque};
    use std::sync::{Arc, Mutex};

    use super::*;
    use crate::obd::obd2_faults;

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
}

#[cfg(test)]
mod tests {
    use std::time::Instant;

    use super::test_support::*;
    use super::*;

    const WINDOW: Duration = Duration::from_secs(2);

    /// An adapter with the timings these tests are about.
    fn adapter(wire: &ScriptedElm) -> Elm327 {
        let mut elm = Elm327::new(Box::new(wire.clone()));
        elm.timeout = WINDOW;
        elm.idle_gap = Duration::from_millis(300);
        elm
    }

    fn time_of(work: impl FnOnce()) -> Duration {
        let clock = Instant::now();
        work();
        clock.elapsed()
    }

    fn ms(n: u64) -> Duration {
        Duration::from_millis(n)
    }

    // ----- the prompt --------------------------------------------------------

    #[test]
    fn a_prompt_still_finishes_a_reply_at_once() {
        // The healthy case, which must not be made slower by any of the rest.
        let wire = ScriptedElm::new(|_| vec![piece(20, "410C1AF8\r\r>")]);

        let mut reply = String::new();
        let took = time_of(|| reply = adapter(&wire).send("010C", WINDOW, false, 0).unwrap());

        assert!(reply.contains("410C1AF8"));
        assert!(took < ms(250), "a prompt-terminated read took {} ms", took.as_millis());
    }

    #[test]
    fn a_reply_with_no_prompt_is_finished_by_the_quiet_after_it() {
        // The Vgate's own failure: the payload arrives and the terminator never does.
        // Before this, the read burned the whole window and the poll rate collapsed on
        // a car that was answering perfectly.
        let wire = ScriptedElm::new(|_| vec![piece(20, "410C1AF8\r")]);

        let mut reply = String::new();
        let took = time_of(|| reply = adapter(&wire).send("010C", WINDOW, false, 0).unwrap());

        assert!(reply.contains("410C1AF8"));
        assert!(took < ms(900), "a promptless read took {} ms", took.as_millis());
    }

    #[test]
    fn silence_is_not_a_short_reply() {
        // Nothing arriving must keep waiting. An adapter that is merely slow is told
        // apart from one that has answered by whether anything came back at all, so a
        // read with an empty buffer waits out the window rather than reporting an empty
        // answer after the gap.
        let wire = ScriptedElm::new(|_| vec![]);

        let took = time_of(|| {
            adapter(&wire).send("010C", ms(900), false, 0).unwrap();
        });

        assert!(took > ms(700), "silence was given up on after {} ms", took.as_millis());
    }

    // ----- the echo ----------------------------------------------------------

    #[test]
    fn the_echo_alone_does_not_finish_a_reply() {
        // THE trap the quiet rule creates. This adapter ignores ATE0: it echoes the
        // command, pauses, and only then sends the data, and for a longer request the
        // pause is longer than the gap. An echo accepted as an answer completes the read
        // before the answer exists, and the answer lands in the next command's window:
        // every reply one behind, for ever.
        let wire = ScriptedElm::new(|command| vec![piece(10, &format!("{command}\r")), piece(700, "410C1AF8\r\r>")]);

        let reply = adapter(&wire).send("010C", WINDOW, false, 0).unwrap();

        assert!(reply.contains("410C1AF8"));
    }

    #[test]
    fn an_echo_followed_by_a_real_answer_is_still_finished_by_quiet() {
        // The two rules together: the echo does not count, the data does, and there is no
        // prompt to end it.
        let wire = ScriptedElm::new(|command| vec![piece(10, &format!("{command}\r")), piece(120, "410C1AF8\r")]);

        let mut reply = String::new();
        let took = time_of(|| reply = adapter(&wire).send("010C", WINDOW, false, 0).unwrap());

        assert!(reply.contains("410C1AF8"));
        assert!(took < ms(900), "took {} ms", took.as_millis());
    }

    // ----- the leftover ------------------------------------------------------

    #[test]
    fn a_prompt_with_nothing_before_it_is_a_leftover_and_not_an_answer() {
        // The consequence of finishing on quiet: the prompt that belonged to the
        // previous reply is still in flight, so it cannot be drained, as it had not been
        // sent. It arrives at the very start of the next read, which by the rule that a
        // prompt ends a reply accepts it as a complete empty answer. The real one then
        // turns up a command late, and it sustains itself.
        let wire = ScriptedElm::new(|_| vec![piece(90, "410C1AF8\r\r>")]);

        // Still in flight while the request goes out, which is the whole point: a byte
        // that has not been sent yet cannot be drained, so it arrives at the front of
        // the next read however carefully the buffer was cleared.
        wire.interject(">", ms(30));

        let reply = adapter(&wire).send("010C", WINDOW, false, 0).unwrap();

        assert!(reply.contains("410C1AF8"));
    }

    #[test]
    fn an_adapter_that_sends_nothing_but_leftovers_still_gives_up() {
        // Discarding empty prompts must not become a loop. The timeout is what bounds
        // it, and it still has to.
        let wire = ScriptedElm::new(|_| vec![piece(10, ">"), piece(200, ">"), piece(400, ">")]);

        let mut elm = Elm327::new(Box::new(wire.clone()));
        elm.timeout = ms(700);

        let mut reply = String::new();
        let took = time_of(|| reply = elm.send("010C", ms(700), false, 0).unwrap());

        assert_eq!(reply.trim(), "");
        assert!(took < Duration::from_secs(2), "gave up after {} ms", took.as_millis());
    }

    // ----- getting back into step --------------------------------------------

    #[test]
    fn an_answer_that_arrives_after_its_timeout_does_not_become_the_next_one() {
        // A timeout means the adapter was late, not absent. What has not arrived yet
        // cannot be discarded, so without waiting for it the late reply satisfies the
        // next read, and a reading lands on the wrong channel, which is worse than a
        // reading that never came.
        let wire = ScriptedElm::new(|command| {
            if command == "010C" {
                vec![piece(550, "410C1AF8\r\r>")]
            } else {
                vec![piece(40, "41051E\r\r>")]
            }
        });

        let mut elm = Elm327::new(Box::new(wire.clone()));
        elm.timeout = ms(300);

        // Times out: the rev counter's answer is still 250 ms away.
        elm.send("010C", ms(300), false, 0).unwrap();

        let coolant = elm.send("0105", WINDOW, false, 0).unwrap();

        assert!(!coolant.contains("410C"));
        assert!(coolant.contains("41051E"));
    }

    #[test]
    fn an_answer_later_than_any_wait_still_cannot_be_read_as_another_channel() {
        // The tail wait is bounded, so an answer late enough will outlive it and land in
        // somebody else's read. What must never happen is that it is believed: a reply
        // carries the number of the parameter it answers, and that check is the thing
        // standing between a late rev counter and a coolant gauge reading 1,726.
        let wire = ScriptedElm::new(|command| {
            if command == "010C" {
                vec![Piece {
                    after: Duration::from_secs(2),
                    text: "410C1AF8\r\r>".to_string(),
                }]
            } else {
                vec![]
            }
        });

        let mut elm = Elm327::new(Box::new(wire.clone()));
        elm.timeout = ms(200);
        elm.tail_wait = ms(50);

        elm.send("010C", ms(200), false, 0).unwrap();

        let mut data = [0u8; 8];
        let answered = elm.try_read(0x05, 1, &mut data, Some(Duration::from_secs(3))).unwrap();

        assert!(
            answered.is_none(),
            "the rev counter's answer was accepted as a coolant reading"
        );
    }

    #[test]
    fn a_dropped_instalment_does_not_cost_the_ones_after_it() {
        // A car with more than one responding module answers on several lines, and the
        // gap has to be wide enough to hold them together. Two modules 90 ms apart is
        // inside it; the quiet rule must not cut the second one off, because what is
        // lost is every parameter only that module reports.
        let wire = ScriptedElm::new(|_| vec![piece(20, "4100BE3FA813\r"), piece(110, "410080000001\r")]);

        let reply = adapter(&wire).send("0100", WINDOW, false, 0).unwrap();

        assert_eq!(parse_all(&reply, 0x00, 4).len(), 2);
    }

    // ----- a link that has gone ----------------------------------------------

    /// A link whose far end has closed: every read comes back empty, at once and for
    /// ever. What `WifiEcuTransport` does once its socket has seen the FIN. There is
    /// nothing left to wait for, so it does not wait, which is the whole point, and also
    /// what makes the caller's "keep reading until the deadline" into a spin rather than
    /// a sleep.
    struct ClosedLink;

    impl EcuTransport for ClosedLink {
        fn is_open(&self) -> bool {
            true
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
    fn a_closed_link_is_given_up_on_rather_than_spun_on() {
        // A transport that returns empty without waiting means the link is gone, and the
        // read has to say so instead of asking again. Going round again costs nothing to
        // ask and gets the same answer, so the loop runs flat out until the deadline: a
        // core at 100 % for two seconds a command and five on a reset, which is
        // precisely when the dongle has dropped the session and the recovery wants to be
        // quick.
        let took = time_of(|| {
            Elm327::new(Box::new(ClosedLink))
                .send("010C", WINDOW, false, 0)
                .unwrap();
        });

        assert!(took < ms(250), "a dead link took {} ms to give up", took.as_millis());
    }

    // ----- ElmDeadLinkTests ---------------------------------------------------

    /// A transport that answers once and then goes away, returning immediately and for
    /// ever, which is what a closed socket does.
    struct DiesAfterEcho {
        out: std::collections::VecDeque<u8>,
        reads: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    }

    impl EcuTransport for DiesAfterEcho {
        fn is_open(&self) -> bool {
            true
        }
        fn open(&mut self) -> Result<()> {
            Ok(())
        }
        fn close(&mut self) {}
        fn write(&mut self, _data: &[u8]) -> Result<()> {
            Ok(())
        }
        fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
            self.reads.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

            // The echo comes back, then nothing, and nothing without waiting, which is
            // the whole point.
            let Some(b) = self.out.pop_front() else { return Ok(0) };
            if buffer.is_empty() {
                return Ok(0);
            }
            buffer[0] = b;
            Ok(1)
        }
        fn discard_input(&mut self) {}
    }

    #[test]
    fn a_link_that_dies_mid_exchange_is_given_up_on_rather_than_spun_against() {
        // The echo arrives and then the far end goes. Before, the loop kept the idle gap
        // as its wait, so the "came back early" guard, which only compared against the
        // whole remaining window, never fired.
        let reads = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let transport = DiesAfterEcho {
            out: b"ATZ\r".iter().copied().collect(),
            reads: reads.clone(),
        };
        let mut elm = Elm327::new(Box::new(transport));
        elm.timeout = Duration::from_secs(2);

        let clock = Instant::now();
        elm.send("ATZ", Duration::from_secs(2), false, 0).unwrap();
        let took = clock.elapsed();

        assert!(
            took < Duration::from_secs(1),
            "gave up after {:.2}s, which means it was spinning",
            took.as_secs_f64()
        );

        let n = reads.load(std::sync::atomic::Ordering::SeqCst);
        assert!(n < 100, "read {n} times, which is a spin");
    }

    // ----- pure parsing (no clock) --------------------------------------------

    #[test]
    fn spaces_in_a_reply_are_tolerated_whichever_way_the_adapter_sends_them() {
        let mut a = [0u8; 2];
        assert_eq!(try_parse("41 0C 1A F8\r", 0x0C, 2, &mut a), Some(2));
        assert_eq!(a, [0x1A, 0xF8]);
        assert_eq!(try_parse("410C1AF8", 0x0C, 2, &mut a), Some(2));
    }

    #[test]
    fn searching_and_no_data_are_not_readings() {
        let mut a = [0u8; 2];
        assert_eq!(try_parse("SEARCHING...\rNO DATA\r", 0x0C, 2, &mut a), None);
        assert_eq!(try_parse("SEARCHING...\r410C1AF8\r", 0x0C, 2, &mut a), Some(2));
    }

    #[test]
    fn a_reply_to_a_different_question_or_a_short_one_is_refused() {
        let mut a = [0u8; 2];
        assert_eq!(try_parse("410D1A", 0x0C, 1, &mut a), None);
        assert_eq!(try_parse("410C1A", 0x0C, 2, &mut a), None);
    }

    #[test]
    fn a_segmented_batch_is_read_across_its_markers() {
        let lengths = |p: u8| match p {
            0x0C => 2,
            0x0D | 0x05 | 0x0B => 1,
            _ => 0,
        };
        let reply = "00B\r0:410C1AF80D2D\r1:05460B1E\r";
        let got = parse_batch(reply, &[0x0C, 0x0D, 0x05, 0x0B], &lengths);

        assert_eq!(got.len(), 4);
        assert_eq!(
            got[0],
            BatchAnswer {
                pid: 0x0C,
                data: vec![0x1A, 0xF8]
            }
        );
        assert_eq!(
            got[3],
            BatchAnswer {
                pid: 0x0B,
                data: vec![0x1E]
            }
        );
    }
}

/// `Obd2BatchTests`: the parts that read a reply or build a request, with no source on
/// top.
#[cfg(test)]
mod batch_tests {
    use super::test_support::FakeElm;
    use super::*;
    use crate::obd::obd2_pids;

    fn length(pid: u8) -> usize {
        obd2_pids::data_bytes_of(pid)
    }

    fn pids_of(answers: &[BatchAnswer]) -> Vec<u8> {
        answers.iter().map(|a| a.pid).collect()
    }

    #[test]
    fn groups_are_read_by_the_length_each_parameter_is_defined_to_carry() {
        // There is nothing between the groups: 41, then (pid, data) over and over.
        // Knowing where one ends is knowing how long that parameter is.
        let answers = parse_batch("410C1AF80B640D40\r\r>", &[0x0C, 0x0B, 0x0D], &length);

        assert_eq!(pids_of(&answers), [0x0C, 0x0B, 0x0D]);
        assert_eq!(answers[0].data, [0x1A, 0xF8]);
        assert_eq!(answers[1].data, [0x64]);
        assert_eq!(answers[2].data, [0x40]);
    }

    #[test]
    fn a_segment_marker_does_not_shift_the_bytes_after_it() {
        // A multi-frame reply is printed as a total length and numbered segments. "0:" and
        // "1:" are not decoration: their digits are valid hex, so stripping non-hex
        // characters and pairing the rest turns the "0" into half a byte and moves
        // everything after it by a nibble.
        let reply = "00D\n0:410C1AF80B64\n1:0D4011330441\n\n>";

        let answers = parse_batch(reply, &[0x0C, 0x0B, 0x0D, 0x11, 0x04], &length);

        assert_eq!(pids_of(&answers), [0x0C, 0x0B, 0x0D, 0x11, 0x04]);
        assert_eq!(answers[0].data, [0x1A, 0xF8]);
        assert_eq!(answers[3].data, [0x33]);
    }

    #[test]
    fn the_same_reply_is_read_when_the_line_breaks_have_been_lost() {
        // Some clients strip carriage returns as they read, which runs the whole reply
        // together with the markers still embedded. The same rule carries it: a marker's
        // digit is discarded by the colon in front of the next byte rather than paired
        // with it.
        let answers = parse_batch(
            "00D0:410C1AF80B641:0D4011330441",
            &[0x0C, 0x0B, 0x0D, 0x11, 0x04],
            &length,
        );

        assert_eq!(answers.len(), 5);
        assert_eq!(answers[0].data, [0x1A, 0xF8]);
    }

    #[test]
    fn the_anchor_that_explains_most_of_the_reply_wins() {
        // Two modules answering, run together, measured on a live Crosstrek. A leading
        // fragment carries a parameter that was asked for, so the first plausible start
        // latches onto it, emits one group, meets the padding and stops. One parameter of
        // four reads as "this car does not do batching", and the whole feature is switched
        // off on a car that does: a throughput collapse with no parse error anywhere.
        // A fragment carrying 0x01, then the real answer with its own length header:
        // 41 5C 60 | 0C 1A F8 | 0D 40.
        let reply = "410100040000\r008\r0:415C600C1AF8\r1:0D40\r\r>";

        let answers = parse_batch(reply, &[0x01, 0x5C, 0x0C, 0x0D], &length);

        assert_eq!(pids_of(&answers), [0x5C, 0x0C, 0x0D]);
    }

    #[test]
    fn the_walk_stops_at_something_nobody_asked_for() {
        // Frame padding, or a second module's answer beginning. Either way the groups have
        // run out, and reading on takes a length out of another message's data.
        let answers = parse_batch("410C1AF80B6455AAAA", &[0x0C, 0x0B], &length);

        assert_eq!(pids_of(&answers), [0x0C, 0x0B]);
    }

    #[test]
    fn the_walk_stops_when_the_answer_starts_repeating_itself() {
        let answers = parse_batch("410C1AF80C1AF8", &[0x0C], &length);

        assert_eq!(answers.len(), 1);
    }

    #[test]
    fn a_truncated_reply_keeps_what_arrived() {
        // The last group is a parameter number with nothing behind it. What came before it
        // is still good, and is worth more than nothing.
        let answers = parse_batch("410C1AF80B640D", &[0x0C, 0x0B, 0x0D], &length);

        assert_eq!(pids_of(&answers), [0x0C, 0x0B]);
    }

    #[test]
    fn a_parameter_of_unknown_length_is_never_asked_for_in_a_batch() {
        // Its group could not be located in the reply, so it would not merely be missing:
        // it would lose every group after it.
        let car = FakeElm::new();
        car.with(|s| s.batches = super::test_support::BatchReply::All);
        let mut elm = Elm327::new(Box::new(car.clone()));
        elm.reset_timeout = Duration::from_millis(300);
        elm.timeout = Duration::from_millis(300);
        elm.reset(None).unwrap();

        elm.read_many(&[0x0C, 0xFE, 0x0D], &length, None).unwrap();

        assert!(car.received().iter().any(|c| c == "010C0D"));
    }

    #[test]
    fn a_batch_of_known_parameters_reads_back_every_group() {
        let car = FakeElm::new();
        car.with(|s| {
            s.batches = super::test_support::BatchReply::All;
            s.answers.insert(0x0C, vec![0x1A, 0xF8]);
            s.answers.insert(0x0B, vec![0x64]);
            s.answers.insert(0x0D, vec![0x40]);
        });
        let mut elm = Elm327::new(Box::new(car.clone()));
        elm.reset_timeout = Duration::from_millis(300);
        elm.timeout = Duration::from_millis(300);
        elm.reset(None).unwrap();

        let answers = elm.read_many(&[0x0C, 0x0B, 0x0D], &length, None).unwrap();

        assert_eq!(pids_of(&answers), [0x0C, 0x0B, 0x0D]);
        assert_eq!(answers[0].data, [0x1A, 0xF8]);
    }
}
