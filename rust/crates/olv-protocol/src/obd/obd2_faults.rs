//! Ported from `Obd2Faults.cs`: reading and erasing a vehicle's fault codes over OBD2.
//!
//! Four modes of the standard, and the interesting part is not any of them
//! individually:
//!
//! - `03`: the confirmed faults, which are the ones lighting the lamp.
//! - `07`: pending, seen once and not yet believed.
//! - `0A`: permanent, which mode 04 is not allowed to erase.
//! - `04`: erase everything the controller is permitted to erase.
//!
//! The awkward part is that mode 03 is the first reply in this application long enough
//! not to fit in one CAN frame. Every mode-01 reading is seven bytes or fewer and
//! arrives on one line; three fault codes are ten bytes and arrive as an ISO-TP
//! sequence, which an ELM327 with headers off prints as a length and then numbered
//! fragments:
//!
//! ```text
//! 00A
//! 0:43040133
//! 1:01340173019600
//! ```
//!
//! Run through the ordinary hex reader those digits decode into codes that are not on
//! the car: the "0:" alone contributes a nibble and shifts everything after it. So this
//! reassembles the fragments rather than reusing that path.
//!
//! The other trap is the count byte. On CAN the reply carries the number of codes
//! straight after the mode echo; on the older serial protocols it does not, and the pairs
//! start immediately. Read the wrong way round, `43 02 01 33` is either one fault (P0133)
//! or two nonexistent ones, so the protocol is asked for rather than assumed.

use std::fmt;

use olv_core::{Error, Result};

use super::elm327::{Elm327, nibble, split_lines, unhex};
use super::obd2_codes::{self, DtcAuthority};

/// Confirmed faults.
pub const STORED_MODE: u8 = 0x03;

/// Erase faults, freeze frames and the readiness monitors.
pub const CLEAR_MODE: u8 = 0x04;

/// Faults seen once and not yet confirmed.
pub const PENDING_MODE: u8 = 0x07;

/// Faults the controller alone may erase.
pub const PERMANENT_MODE: u8 = 0x0A;

const ATTEMPTS: usize = 3;

/// Which of the three lists a code was found in.
///
/// The standard keeps them apart for a reason and they mean quite different things
/// about the car, so they are not merged here either.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DtcState {
    /// Confirmed: the fault happened often enough for the car to commit to it, and on
    /// an emissions monitor this is what lights the lamp.
    Stored,

    /// Seen once and not yet confirmed. Most monitors want the fault on two consecutive
    /// drive cycles before they will store it, so a pending code is either the
    /// beginning of a real fault or a one-off that will clear itself.
    Pending,

    /// Stored and not clearable. These were added so that a car could not be presented
    /// for an emissions test with its faults wiped minutes earlier: only the controller
    /// may erase one, and only after it has watched the monitor pass on its own.
    Permanent,
}

/// One fault code, and which list it came from.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Dtc {
    pub code: String,
    pub state: DtcState,
}

impl Dtc {
    pub fn new(code: impl Into<String>, state: DtcState) -> Self {
        Self {
            code: code.into(),
            state,
        }
    }

    /// The standard's meaning, or empty where there is none to give.
    pub fn description(&self) -> String {
        obd2_codes::describe(Some(&self.code))
    }

    pub fn authority(&self) -> DtcAuthority {
        obd2_codes::authority(Some(&self.code))
    }

    /// Powertrain, Chassis, Body or Network.
    pub fn system(&self) -> &'static str {
        obd2_codes::system(Some(&self.code))
    }

    /// What can honestly be said about this code, which for a manufacturer's own number
    /// is that nobody outside the marque can read it.
    ///
    /// Deliberately not a guess. The maker owns those ranges and assigns them freely, so
    /// the same five characters mean unrelated things on two different cars, and a
    /// plausible-sounding description of the wrong one is how a person ends up buying a
    /// sensor they did not need.
    pub fn detail(&self) -> String {
        let description = self.description();
        if !description.is_empty() {
            description
        } else if self.authority() == DtcAuthority::Manufacturer {
            "Defined by the vehicle manufacturer \u{2014} the standard does not assign this number, \
             so it has to be looked up against the marque."
                .to_string()
        } else {
            "In the standard's range, but not in the list carried here. Worth looking up.".to_string()
        }
    }
}

impl fmt::Display for Dtc {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let description = self.description();
        if description.is_empty() {
            f.write_str(&self.code)
        } else {
            write!(f, "{}  {}", self.code, description)
        }
    }
}

/// What the car reported when it was asked about its faults.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FaultScan {
    /// Every code found, across all three lists.
    pub faults: Vec<Dtc>,

    /// Whether the car says the lamp is commanded on.
    pub mil_on: bool,

    /// How many confirmed faults the car counts, from PID 01. Worth keeping beside the
    /// codes rather than deriving from them: a difference between the two means one
    /// module answered the count and another did not answer mode 03, which is the
    /// difference between "no faults" and "not everything was asked".
    pub reported_count: i32,

    /// What the adapter negotiated, in its own words.
    pub protocol: String,

    /// What went wrong, where anything did.
    pub trouble: String,
}

impl FaultScan {
    fn in_state(&self, state: DtcState) -> Vec<Dtc> {
        self.faults.iter().filter(|f| f.state == state).cloned().collect()
    }

    pub fn stored(&self) -> Vec<Dtc> {
        self.in_state(DtcState::Stored)
    }

    pub fn pending(&self) -> Vec<Dtc> {
        self.in_state(DtcState::Pending)
    }

    pub fn permanent(&self) -> Vec<Dtc> {
        self.in_state(DtcState::Permanent)
    }

    pub fn clean(&self) -> bool {
        self.faults.is_empty() && !self.mil_on
    }

    /// Whether the count and the codes disagree.
    ///
    /// A car that says it has two faults and lists none of them has not been fully
    /// read: usually a module that answers PID 01 and ignores mode 03, or a scan that
    /// was cut short. Saying "no faults found" there would be a lie by omission, so this
    /// exists to stop that being said.
    pub fn count_disagrees(&self) -> bool {
        self.reported_count as usize > self.stored().len()
    }

    /// One line for a status bar.
    pub fn summary(&self) -> String {
        if !self.trouble.is_empty() {
            return self.trouble.clone();
        }

        let mut parts = Vec::new();
        let stored = self.stored().len();
        let pending = self.pending().len();
        let permanent = self.permanent().len();
        if stored > 0 {
            parts.push(format!("{stored} stored"));
        }
        if pending > 0 {
            parts.push(format!("{pending} pending"));
        }
        if permanent > 0 {
            parts.push(format!("{permanent} permanent"));
        }

        let lamp = if self.mil_on { "lamp on" } else { "lamp off" };

        if parts.is_empty() {
            format!("No fault codes \u{2014} {lamp}.")
        } else {
            format!("{} \u{2014} {lamp}.", parts.join(", "))
        }
    }
}

/// What came of asking the car to erase its faults.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FaultClear {
    /// Whether the car acknowledged the request.
    pub erased: bool,

    /// What to tell the person who asked.
    pub message: String,

    /// Permanent codes, which mode 04 cannot touch. Read back afterwards rather than
    /// assumed, since a car that has none is the ordinary case and saying so is worth
    /// more than a warning about codes it does not have.
    pub remaining: Vec<Dtc>,
}

/// A positive reply echoes the mode with bit 6 set.
pub fn echoes(mode: u8) -> u8 {
    mode.wrapping_add(0x40)
}

/// Asks the car for every fault it will admit to.
///
/// All three lists, always, even though most cars have nothing in two of them. A pending
/// code is the useful one when a fault is intermittent: it is the evidence that
/// something happened on the drive that has just finished, and a permanent code is the
/// thing that explains why a car that was "cleared" still fails an emissions test.
pub fn scan(elm: &mut Elm327) -> Result<FaultScan> {
    let protocol = elm.protocol_name()?;
    let counted = elm.is_can()?;

    let mut faults = Vec::new();
    let mut trouble: Vec<&str> = Vec::new();

    for (mode, state) in [
        (STORED_MODE, DtcState::Stored),
        (PENDING_MODE, DtcState::Pending),
        (PERMANENT_MODE, DtcState::Permanent),
    ] {
        let reply = ask(elm, mode)?;

        if answered(Some(&reply), mode) {
            faults.extend(parse(&reply, mode, state, counted));
            continue;
        }

        // "NO DATA" is an answer, and on the older protocols it is the only answer a
        // clean car gives: where a CAN vehicle replies "43 00" to say it has no faults,
        // an ISO 9141 one does not reply at all and the adapter reports that. It is also
        // what any car says to a mode it does not implement. None of those is a failure.
        if no_data(&reply) {
            continue;
        }

        // Silence is. Told apart on purpose from the above, because reporting the two
        // the same way is how "no faults found" comes to mean "the link was down".
        //
        // Except for permanent codes, which only exist on vehicles built to the later
        // requirement: an older car that ignores mode 0A is not a fault and is not worth
        // a warning on every scan.
        if mode != PERMANENT_MODE {
            trouble.push(name(mode));
        }
    }

    let (mil, count) = lamp(elm)?;

    Ok(FaultScan {
        faults,
        mil_on: mil,
        reported_count: count,
        protocol,
        trouble: if trouble.is_empty() {
            String::new()
        } else {
            format!(
                "The vehicle did not answer the request for {} codes. \
                 What is listed may not be all of it.",
                trouble.join(" or ")
            )
        },
    })
}

fn name(mode: u8) -> &'static str {
    match mode {
        STORED_MODE => "stored",
        PENDING_MODE => "pending",
        _ => "permanent",
    }
}

/// Asks once, and again if nothing came back.
///
/// Worth a retry in a way an ordinary reading is not: this happens when somebody
/// presses a button, and an unanswered attempt reads as a car with no faults rather than
/// as a question that went astray. A polled gauge that misses a sample gets another one
/// immediately.
fn ask(elm: &mut Elm327, mode: u8) -> Result<String> {
    for _ in 0..ATTEMPTS {
        let reply = elm.send(&format!("{mode:02X}"), elm.reset_timeout, true, 0)?;
        if answered(Some(&reply), mode) {
            return Ok(reply);
        }

        // A car that means "I do not support this" says so at once and will keep saying
        // it. Only silence is worth asking about twice.
        if refused(&reply) {
            return Ok(reply);
        }
    }

    Ok(String::new())
}

/// A settled answer, as opposed to nothing arriving: either way there is no point asking
/// twice.
fn refused(reply: &str) -> bool {
    no_data(reply) || contains_ignore_case(reply, "7F") || contains_ignore_case(reply, "UNABLE TO CONNECT")
}

/// The adapter reporting that the car said nothing within its own timeout.
///
/// For a fault mode this means the car has none, not that the question failed. A CAN
/// vehicle with a clean bill of health answers "43 00" (the mode echo and a count of
/// zero) but the older serial protocols have no count to send, so a car with nothing to
/// report simply does not reply and the adapter says this instead. Treating it as a
/// failure would put a warning on every scan of every healthy pre-CAN car.
fn no_data(reply: &str) -> bool {
    contains_ignore_case(reply, "NO DATA")
}

fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    haystack.to_ascii_uppercase().contains(&needle.to_ascii_uppercase())
}

/// Whether the reply contains a positive answer to the mode that was asked.
pub fn answered(reply: Option<&str>, mode: u8) -> bool {
    reply.is_some_and(|r| !units(r, echoes(mode)).is_empty())
}

/// Whether the lamp is on, and how many faults the car counts.
///
/// From PID 01, which is a mode-01 reading like any other and so goes through the
/// ordinary path. Its top bit is the lamp and the remaining seven are the count: the same
/// parameter the dashboard already carries as a gauge, asked here so that a scan is a
/// complete answer on its own.
fn lamp(elm: &mut Elm327) -> Result<(bool, i32)> {
    let mut data = [0u8; 4];

    match elm.try_read(0x01, 4, &mut data, None)? {
        Some(got) if got >= 1 => Ok(((data[0] & 0x80) != 0, i32::from(data[0] & 0x7F))),
        _ => Ok((false, 0)),
    }
}

/// Erases everything the controller is allowed to erase.
///
/// Far more than the name suggests, and worth saying so somewhere the caller can read
/// it. Mode 04 clears the stored and pending codes, the freeze frame captured when the
/// fault happened, the oxygen sensor test results and the readiness monitors, so a car
/// cleared this morning cannot pass an emissions test this afternoon whatever its actual
/// condition, because it now has no evidence that its monitors ever ran. The freeze
/// frame is the loss that stings: it is the one record of what the engine was doing at
/// the moment the fault occurred, and it is not recoverable.
///
/// Permanent codes are untouched by design, which is what they are for.
///
/// Most vehicles refuse this with the engine running.
pub fn clear(elm: &mut Elm327) -> Result<FaultClear> {
    let reply = elm.send(&format!("{CLEAR_MODE:02X}"), elm.reset_timeout, true, 0)?;

    if !answered(Some(&reply), CLEAR_MODE) {
        return Ok(FaultClear {
            erased: false,
            message: if refused(&reply) {
                "The vehicle refused to erase its faults. Most will not do this with the \
                 engine running \u{2014} try again with the ignition on and the engine off."
                    .to_string()
            } else {
                "The vehicle did not answer the erase request, so nothing has been cleared.".to_string()
            },
            remaining: Vec::new(),
        });
    }

    // Read back rather than assumed. Permanent codes survive this, and a person who has
    // just been told the car is clear and then fails a test on a code that was there all
    // along has been misled by the tool.
    let permanent = elm.send(&format!("{PERMANENT_MODE:02X}"), elm.reset_timeout, true, 0)?;

    let remaining = if answered(Some(&permanent), PERMANENT_MODE) {
        parse(&permanent, PERMANENT_MODE, DtcState::Permanent, elm.is_can()?)
    } else {
        Vec::new()
    };

    let message = if remaining.is_empty() {
        "Erased. The stored and pending codes, the freeze frame and the readiness \
         monitors are all gone \u{2014} the car will need a full drive cycle before it can \
         pass an emissions test."
            .to_string()
    } else {
        format!(
            "Erased, but {} permanent code{} still set. Only the controller can \
             clear those, and it will not do so until it has watched the monitor pass.",
            remaining.len(),
            if remaining.len() == 1 { " is" } else { "s are" }
        )
    };

    Ok(FaultClear {
        erased: true,
        message,
        remaining,
    })
}

/// The codes in one reply.
///
/// Each responding module contributes its own list, and they are kept: two modules with
/// the same fault is one fault, but two modules with different faults is two, and which
/// module answers first is not fixed. Duplicates are collapsed for that reason and only
/// that one.
pub fn parse(reply: &str, mode: u8, state: DtcState, counted: bool) -> Vec<Dtc> {
    let mut found: Vec<Dtc> = Vec::new();
    let mut seen: Vec<String> = Vec::new();

    for unit in units(reply, echoes(mode)) {
        // Past the echoed mode, and past the count where the protocol carries one. Read
        // the wrong way round the count becomes the first half of a code that is not on
        // the car.
        let mut at = if counted { 2 } else { 1 };

        while at + 1 < unit.len() {
            // Replies are padded to the frame with zeros, and P0000 is not an assigned
            // code, so a zero pair is the padding.
            if unit[at] == 0 && unit[at + 1] == 0 {
                at += 2;
                continue;
            }

            let code = decode(unit[at], unit[at + 1]);
            // HashSet<string>(OrdinalIgnoreCase); codes are upper-case ASCII already.
            if !seen.iter().any(|s| s.eq_ignore_ascii_case(&code)) {
                seen.push(code.clone());
                found.push(Dtc::new(code, state));
            }

            at += 2;
        }
    }

    found
}

/// One code from its two bytes, per SAE J2012.
///
/// The first two bits are the system, the next two the first digit, and the remaining
/// twelve are three hex digits, so the four characters after the letter are not a decimal
/// number and a code may legitimately read P0A0F.
pub fn decode(high: u8, low: u8) -> String {
    let system = match high >> 6 {
        0 => 'P',
        1 => 'C',
        2 => 'B',
        _ => 'U',
    };

    format!("{system}{}{:X}{low:02X}", (high >> 4) & 0x3, high & 0x0F)
}

/// The two bytes a code encodes to: the reverse of [`decode`].
pub fn encode(code: &str) -> Result<(u8, u8)> {
    if !obd2_codes::is_well_formed(Some(code)) {
        return Err(Error::Argument(format!("'{code}' is not a fault code.")));
    }

    let text = code.to_ascii_uppercase();
    let bytes = text.as_bytes();

    let system: i32 = match bytes[0] {
        b'P' => 0,
        b'C' => 1,
        b'B' => 2,
        _ => 3,
    };
    let first = i32::from(bytes[1]) - i32::from(b'0');
    let second = i32::from(nibble(char::from(bytes[2])).unwrap_or(0));

    let high = ((system << 6) | (first << 4) | second) as u8;
    let low = u8::from_str_radix(&text[3..5], 16).unwrap_or(0);

    Ok((high, low))
}

/// Every complete answer in a reply, one per responding module, with the ISO-TP
/// fragments already put back together.
///
/// Three shapes have to come out the same way:
///
/// - A single frame: one line of hex beginning with the echoed mode.
/// - A CAN reply too long for one frame: a line giving the total length, then numbered
///   fragments, `0:` through `F:`.
/// - An older serial protocol: several lines, each beginning with the echoed mode and
///   each carrying up to three codes.
///
/// The fragment prefixes are the reason this cannot go through the ordinary hex reader,
/// which ignores anything that is not a hex digit: "0:" is not ignored, the "0" is a
/// digit, and one stray nibble shifts every byte after it by half a byte. Every code past
/// the first would be wrong and none of them would look wrong.
pub fn units(reply: &str, echoed: u8) -> Vec<Vec<u8>> {
    let lines: Vec<&str> = split_lines(reply).collect();

    let mut units: Vec<Vec<u8>> = Vec::new();
    let mut assembling: Option<Vec<u8>> = None;
    let mut declared: usize = 0;

    // One buffer for the whole reply rather than one per line.
    let mut bytes = [0u8; 64];

    for i in 0..lines.len() {
        let line = lines[i].trim();
        if line.is_empty() {
            continue;
        }

        if let Some((index, data)) = fragment(line) {
            // Fragment zero starts a reply; a fragment arriving without one is the tail
            // of something whose beginning was lost, and half a reply decodes into codes
            // that were never set.
            if index == 0 {
                assembling = Some(Vec::new());
            }
            let Some(buffer) = assembling.as_mut() else { continue };

            buffer.extend_from_slice(&data);

            // The declared length is what says where the real data stops and the last
            // frame's padding begins.
            if declared > 0 && buffer.len() >= declared {
                let whole: Vec<u8> = buffer.iter().copied().take(declared).collect();
                take(&mut units, whole, echoed);
                assembling = None;
                declared = 0;
            }

            continue;
        }

        // A length header is only a length header when fragments follow it. Nothing
        // about the line itself says so: mode 04's whole reply is the two characters
        // "44", which read on its own is a perfectly good declaration of 68 bytes that
        // never arrive, and the erase would be reported as unanswered every time.
        if let Some(total) = length(line)
            && fragment(&next(&lines, i)).is_some()
        {
            declared = total;
            assembling = None;

            continue;
        }

        // An ordinary line: one module's whole answer.
        let got = unhex(line, &mut bytes);

        if got >= 1 {
            take(&mut units, bytes[..got].to_vec(), echoed);
        }
    }

    // A sequence that never reached its declared length is still worth what arrived: the
    // fragments are in order, so a truncated reply is a short list of real codes rather
    // than a wrong one.
    if let Some(buffer) = assembling
        && buffer.len() >= 2
    {
        take(&mut units, buffer, echoed);
    }

    units
}

/// The next non-blank line, or empty at the end.
fn next(lines: &[&str], after: usize) -> String {
    for line in &lines[after + 1..] {
        let trimmed = line.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }

    String::new()
}

fn take(units: &mut Vec<Vec<u8>>, unit: Vec<u8>, echoed: u8) {
    // The echo is checked rather than assumed. A reply that arrives late is answering
    // the previous question, and mode 07's answer read as mode 03's would report a
    // pending fault as a confirmed one.
    if !unit.is_empty() && unit[0] == echoed {
        units.push(unit);
    }
}

/// A numbered ISO-TP fragment, `0:43040133`, or `None` for anything else.
fn fragment(line: &str) -> Option<(i32, Vec<u8>)> {
    let chars: Vec<char> = line.chars().collect();
    let colon = chars.iter().position(|&c| c == ':')?;
    if !(1..=2).contains(&colon) {
        return None;
    }

    let index_text: String = chars[..colon].iter().collect();
    let index_text = index_text.trim();
    let mut index_chars = index_text.chars();
    let (Some(index), None) = (index_chars.next(), index_chars.next()) else {
        return None;
    };
    let index = nibble(index)?;

    let rest: String = chars[colon + 1..].iter().collect();
    let mut bytes = [0u8; 32];
    let got = unhex(&rest, &mut bytes);

    if got == 0 {
        None
    } else {
        Some((i32::from(index), bytes[..got].to_vec()))
    }
}

/// The total length that precedes a fragmented reply, or `None`.
///
/// Unambiguous by shape: a real answer is at least a mode echo and one data byte, so four
/// hex digits, and this is one to three. Nothing else on the line.
fn length(line: &str) -> Option<usize> {
    let count = line.encode_utf16().count();
    if !(1..=3).contains(&count) {
        return None;
    }

    let mut value = 0usize;
    for c in line.chars() {
        value = value * 16 + usize::from(nibble(c)?);
    }

    Some(value)
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use super::*;
    use crate::obd::elm327::{test_support::FakeElm, unhex};

    fn elm_for(car: &FakeElm) -> Elm327 {
        let mut elm = Elm327::new(Box::new(car.clone()));
        elm.reset_timeout = Duration::from_millis(300);
        elm.timeout = Duration::from_millis(300);
        elm
    }

    fn codes(faults: &[Dtc]) -> Vec<&str> {
        faults.iter().map(|f| f.code.as_str()).collect()
    }

    fn strings(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| s.to_string()).collect()
    }

    // ----- what two bytes mean ------------------------------------------------

    #[test]
    fn decodes_the_standards_bit_layout() {
        for (high, low, expected) in [
            (0x03, 0x01, "P0301"), // cylinder 1 misfire, the commonest of all
            (0x01, 0x71, "P0171"), // system too lean, bank 1
            (0x04, 0x20, "P0420"), // catalyst efficiency
            (0xC1, 0x00, "U0100"), // lost communication with the ECM
            (0x40, 0x35, "C0035"), // a chassis code
            (0x93, 0x42, "B1342"), // a body code, manufacturer range
        ] {
            assert_eq!(decode(high, low), expected);
        }
    }

    /// The four characters after the letter are hex digits and not a decimal number.
    /// Hybrid and electrified codes live in the range that proves it, and a decoder that
    /// formatted them as decimal would report P0A0F as P01015.
    #[test]
    fn decodes_the_hex_ranges() {
        assert_eq!(decode(0x0A, 0x0F), "P0A0F");
        assert_eq!(decode(0x34, 0xFF), "P34FF");
    }

    #[test]
    fn encodes_back_to_the_same_bytes() {
        for code in ["P0301", "U0100", "B1342", "P0A0F", "C3999"] {
            let (high, low) = encode(code).unwrap();
            assert_eq!(decode(high, low), code);
        }
        assert!(encode("X1234").is_err());
        assert!(encode("").is_err());
    }

    // ----- the count byte -----------------------------------------------------

    /// The trap that makes a fault reply different from every other reply here.
    ///
    /// On CAN the byte after the mode echo is how many codes follow; on the older serial
    /// buses it is the first half of the first code. These four bytes are a real reply
    /// either way and decode to a different fault: a misfire on cylinder 1 one way round,
    /// a mass airflow circuit fault the other. Neither looks wrong.
    #[test]
    fn the_count_byte_changes_what_the_same_bytes_mean() {
        let reply = "43 01 03 01";

        let can = parse(reply, 0x03, DtcState::Stored, true);
        let serial = parse(reply, 0x03, DtcState::Stored, false);

        assert_eq!(codes(&can), ["P0301"]);
        assert_eq!(codes(&serial), ["P0103"]);
    }

    /// Which of the two it is comes from the adapter rather than from a guess. Protocols
    /// 1 to 5 are the serial buses and 6 upwards are the CAN variants.
    #[test]
    fn takes_the_protocol_from_the_adapter() {
        for (protocol, expected) in [
            (1, false),
            (3, false), // ISO 9141-2, which plenty of pre-2008 cars still use
            (5, false),
            (6, true), // CAN 11 bit, 500 kbaud: the modern default
            (9, true),
            (0xB, true),
        ] {
            let car = FakeElm::new();
            car.with(|s| s.protocol_number = protocol);
            let mut elm = elm_for(&car);
            elm.reset(None).unwrap();

            assert_eq!(elm.is_can().unwrap(), expected, "protocol {protocol}");
        }
    }

    /// Protocol zero is "still searching", not a protocol.
    ///
    /// An adapter that has been told to find the protocol itself answers ATDPN with "A0"
    /// until it succeeds. Caching that as an answer would leave a car that settles on ISO
    /// 9141 a moment later being read as CAN for the rest of the session, which puts a
    /// count byte where a fault code's first half is and produces codes that are not on the
    /// vehicle.
    ///
    /// Found on a live car: the first probe of a running Subaru reported "is CAN: True"
    /// while the search had not finished, and the same link came back ISO 15765-4 once it
    /// had.
    #[test]
    fn a_protocol_still_being_searched_for_is_not_remembered() {
        let car = FakeElm::new();
        car.with(|s| s.protocol_number = 0);
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let asked_count = |car: &FakeElm| {
            car.received()
                .iter()
                .filter(|c| c.eq_ignore_ascii_case("ATDPN"))
                .count()
        };

        elm.is_can().unwrap();
        let asked = asked_count(&car);

        elm.is_can().unwrap();
        let asked_again = asked_count(&car);

        assert!(asked_again > asked, "an undecided protocol must be asked about again");

        // And once it settles, the real answer is taken and kept.
        car.with(|s| s.protocol_number = 3);

        assert!(!elm.is_can().unwrap());
    }

    /// The adapter prefixes the protocol with how it found it, which is not part of what
    /// it found.
    #[test]
    fn reports_the_protocol_without_the_search_prefix() {
        let car = FakeElm::new();
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        assert_eq!(elm.protocol_name().unwrap(), "ISO 15765-4 (CAN 11/500)");
    }

    // ----- replies that do not fit in one frame -------------------------------

    /// Three codes will not fit in a CAN frame, and the adapter breaks the reply into
    /// numbered pieces. Those numbers are hex digits sitting in front of the data, so the
    /// ordinary reader, which skips anything that is not a hex digit, does not skip them:
    /// the "0" of "0:" becomes half a byte and shifts every byte after it.
    ///
    /// Both halves are asserted. That the reassembly is right, and that the naive reading
    /// really is wrong rather than merely untidy, because the second is the reason the
    /// first exists.
    #[test]
    fn reassembles_a_fragmented_reply() {
        let reply = "00A\r0:430403010171\r1:04200135000000\r";

        let faults = parse(reply, 0x03, DtcState::Stored, true);

        assert_eq!(codes(&faults), ["P0301", "P0171", "P0420", "P0135"]);

        // What the mode-01 path would have made of the same line.
        let mut naive = [0u8; 16];
        let got = unhex("0:430403010171", &mut naive);

        assert_ne!(naive[0], 0x43);
        assert_eq!(got, 6);
    }

    /// The declared length is what separates the last frame's real data from the zeros it
    /// is padded out with, and a padding pair decodes to P0000, which is not a code any
    /// car sets.
    #[test]
    fn drops_the_padding_on_the_last_frame() {
        let reply = "006\r0:430203010171\r";

        let faults = parse(reply, 0x03, DtcState::Stored, true);

        assert_eq!(faults.len(), 2);
        assert!(!faults.iter().any(|f| f.code == "P0000"));
    }

    /// A fragment that arrives without the piece that started it is half a reply, and half
    /// a reply decodes into codes that were never set. Better to drop it.
    #[test]
    fn ignores_fragments_with_no_beginning() {
        assert!(parse("1:04200135000000\r", 0x03, DtcState::Stored, true).is_empty());
    }

    /// Mode 04's entire reply is the two characters "44", which read as a length header is
    /// a declaration of 68 bytes that never arrive. Nothing about the line itself
    /// distinguishes the two; only whether fragments follow it.
    #[test]
    fn does_not_mistake_a_short_reply_for_a_length_header() {
        assert!(answered(Some("44"), 0x04));
        assert!(answered(Some("44\r"), 0x04));
    }

    // ----- more than one module answering -------------------------------------

    /// Most cars have several controllers on the bus and each answers with its own faults.
    /// Taking the first line would lose whatever only the second one knows about, which on
    /// a car with a transmission fault is the transmission fault.
    #[test]
    fn keeps_every_modules_answer() {
        // The engine module with a misfire, and the transmission module with a fault of
        // its own.
        let faults = parse("43 01 03 01\r43 01 07 00\r", 0x03, DtcState::Stored, true);

        assert_eq!(codes(&faults), ["P0301", "P0700"]);
    }

    /// Two modules reporting the same fault is one fault. Collapsed for that reason and no
    /// other: different codes from different modules are kept.
    #[test]
    fn collapses_the_same_code_from_two_modules() {
        assert_eq!(parse("43010301\r43010301\r", 0x03, DtcState::Stored, true).len(), 1);
    }

    /// A reply that arrives late is answering the previous question. Mode 07's answer read
    /// as mode 03's would report a fault the car has merely noticed once as one it has
    /// confirmed.
    #[test]
    fn ignores_an_answer_to_a_different_mode() {
        assert!(parse("47010301", 0x03, DtcState::Stored, true).is_empty());
        assert!(!answered(Some("47010301"), 0x03));
        assert!(answered(Some("47010301"), 0x07));
    }

    #[test]
    fn no_answer_is_not_an_empty_answer() {
        for reply in ["NO DATA", "SEARCHING...", "", "7F 03 12"] {
            assert!(!answered(Some(reply), 0x03), "{reply:?}");
        }
    }

    /// A car with nothing wrong still answers the question: count zero. That has to be
    /// distinguishable from a car that never answered, or "no faults found" comes to mean
    /// "the link was down".
    #[test]
    fn an_empty_list_is_still_an_answer() {
        assert!(answered(Some("4300"), 0x03));
        assert!(parse("4300", 0x03, DtcState::Stored, true).is_empty());
    }

    // ----- what a code means --------------------------------------------------

    #[test]
    fn generates_the_families_at_both_ends() {
        for (code, expected) in [
            ("P0301", "Cylinder 1 misfire detected"),
            ("P0312", "Cylinder 12 misfire detected"),
            ("P0201", "Injector circuit open \u{2014} cylinder 1"),
            ("P0212", "Injector circuit open \u{2014} cylinder 12"),
            ("P0261", "Cylinder 1 injector circuit low"),
            ("P0262", "Cylinder 1 injector circuit high"),
            ("P0263", "Cylinder 1 contribution or balance fault"),
            ("P0264", "Cylinder 2 injector circuit low"),
            ("P0284", "Cylinder 8 contribution or balance fault"),
        ] {
            assert_eq!(obd2_codes::describe(Some(code)), expected, "{code}");
        }
    }

    /// The coil codes are lettered in the standard and numbered on the engine, and both
    /// are worth having: the letter is what the manual prints, the number is which plug
    /// lead to pull.
    #[test]
    fn names_ignition_coils_by_letter_and_cylinder() {
        let a = obd2_codes::describe(Some("P0351"));
        assert!(a.contains("coil A") && a.contains("cylinder 1"));
        let l = obd2_codes::describe(Some("P0362"));
        assert!(l.contains("coil L") && l.contains("cylinder 12"));
    }

    /// Six codes per oxygen sensor, three sensors a bank, and bank 2 starting twenty on
    /// rather than eighteen, so there is a real gap at P0148 that has to fall through
    /// rather than be counted over. Counted over, every bank 2 code would come out naming
    /// the wrong sensor.
    #[test]
    fn walks_the_oxygen_sensor_range() {
        for (code, place, what) in [
            ("P0130", "bank 1 sensor 1", "circuit malfunction"),
            ("P0135", "bank 1 sensor 1", "heater circuit"),
            ("P0136", "bank 1 sensor 2", "circuit malfunction"),
            ("P0147", "bank 1 sensor 3", "heater circuit"),
            ("P0150", "bank 2 sensor 1", "circuit malfunction"),
            ("P0161", "bank 2 sensor 2", "heater circuit"),
            ("P0167", "bank 2 sensor 3", "heater circuit"),
        ] {
            let text = obd2_codes::describe(Some(code));
            assert!(text.contains(place), "{code}: {text}");
            assert!(text.contains(what), "{code}: {text}");
        }
    }

    #[test]
    fn leaves_the_gap_between_the_oxygen_sensor_banks() {
        assert_eq!(obd2_codes::describe(Some("P0148")), "");
        assert_eq!(obd2_codes::describe(Some("P0149")), "");
    }

    // ----- whose number is it -------------------------------------------------

    /// The ranges are not symmetrical between the four systems, and the split in the
    /// middle of P3 is real. Getting this wrong the wrong way round puts a confident SAE
    /// meaning on a number that never had one.
    #[test]
    fn knows_whose_number_it_is() {
        use DtcAuthority::{Generic, Manufacturer};

        for (code, expected) in [
            ("P0301", Generic),
            ("P1131", Manufacturer),
            ("P2187", Generic),
            ("P3399", Manufacturer),
            ("P3400", Generic),
            ("C0035", Generic),
            ("C1234", Manufacturer),
            ("C2100", Manufacturer),
            ("B1342", Manufacturer),
            ("U0100", Generic),
            ("U1000", Manufacturer),
        ] {
            assert_eq!(obd2_codes::authority(Some(code)), expected, "{code}");
        }
    }

    /// A manufacturer code gets no description rather than a plausible one. P1131 is an
    /// oxygen sensor on a Ford and something else entirely on a Toyota, and a tool that
    /// guesses is how somebody buys the wrong part.
    #[test]
    fn refuses_to_guess_at_a_manufacturers_code() {
        assert_eq!(obd2_codes::describe(Some("P1131")), "");

        let fault = Dtc::new("P1131", DtcState::Stored);

        assert!(fault.detail().to_ascii_lowercase().contains("manufacturer"));
        assert_eq!(fault.system(), "Powertrain");
    }

    /// A generic code this does not carry says so too, and differently: the standard does
    /// define it, so it is worth looking up rather than being a dead end.
    #[test]
    fn says_when_a_generic_code_is_simply_not_listed() {
        let fault = Dtc::new("P0A0F", DtcState::Stored);

        assert_eq!(fault.description(), "");
        assert!(fault.detail().to_ascii_lowercase().contains("looking up"));
        assert!(!fault.detail().to_ascii_lowercase().contains("manufacturer"));
    }

    #[test]
    fn names_the_system() {
        for (code, expected) in [
            ("P0301", "Powertrain"),
            ("C0035", "Chassis"),
            ("B1342", "Body"),
            ("U0100", "Network"),
        ] {
            assert_eq!(obd2_codes::system(Some(code)), expected);
        }
    }

    #[test]
    fn rejects_what_is_not_a_code() {
        for code in [
            Some(""),
            Some("P030"),
            Some("P03011"),
            Some("X0301"),
            Some("P03G1"),
            None,
        ] {
            assert!(!obd2_codes::is_well_formed(code), "{code:?}");
            assert_eq!(obd2_codes::describe(code), "");
        }
    }

    // ----- the whole conversation ---------------------------------------------

    #[test]
    fn scans_all_three_lists_on_a_can_vehicle() {
        let car = FakeElm::new();
        car.with(|s| {
            s.stored_codes = strings(&["P0301", "P0171", "P0420", "P0135"]);
            s.pending_codes = strings(&["P0300"]);
            s.permanent_codes = strings(&["P0420"]);
            s.answers.insert(0x01, vec![0x81, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let result = scan(&mut elm).unwrap();

        assert_eq!(codes(&result.stored()), ["P0301", "P0171", "P0420", "P0135"]);
        assert_eq!(codes(&result.pending()), ["P0300"]);
        assert_eq!(codes(&result.permanent()), ["P0420"]);

        assert!(result.mil_on);
        assert_eq!(result.trouble, "");
        assert!(!result.clean());
        assert!(result.protocol.contains("ISO 15765-4"));
    }

    /// The same car on an older bus. The reply is laid out differently and carries no
    /// count, and the codes that come out have to be the same ones.
    #[test]
    fn scans_the_same_faults_on_a_serial_bus() {
        let car = FakeElm::new();
        car.with(|s| {
            s.protocol_number = 3;
            s.stored_codes = strings(&["P0301", "P0171", "P0420", "P0135"]);
            s.answers.insert(0x01, vec![0x84, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let result = scan(&mut elm).unwrap();

        assert_eq!(codes(&result.stored()), ["P0301", "P0171", "P0420", "P0135"]);
        assert_eq!(result.reported_count, 4);
    }

    /// A car with nothing wrong. Worth its own test because "no faults" and "no answer"
    /// arrive as very nearly the same thing and only one of them is good news.
    #[test]
    fn reports_a_clean_car_as_clean() {
        let car = FakeElm::new();
        car.with(|s| {
            s.answers.insert(0x01, vec![0x00, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let result = scan(&mut elm).unwrap();

        assert!(result.clean());
        assert!(!result.mil_on);
        assert_eq!(result.trouble, "");
        assert!(result.summary().contains("No fault codes"));
    }

    /// Older cars have no permanent codes at all and refuse mode 0A. That is not a fault
    /// and must not be reported as one, or every pre-2010 car scans with a warning on it.
    #[test]
    fn does_not_complain_when_a_car_has_no_permanent_codes() {
        let car = FakeElm::new();
        car.with(|s| {
            s.protocol_number = 3;
            s.stored_codes = strings(&["P0301"]);
            s.answers.insert(0x01, vec![0x81, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        assert_eq!(scan(&mut elm).unwrap().trouble, "");
    }

    /// The car counts two faults and lists none. Something answered PID 01 that did not
    /// answer mode 03, and reporting "no faults found" there would be a lie by omission.
    #[test]
    fn notices_when_the_count_and_the_codes_disagree() {
        let result = FaultScan {
            faults: vec![],
            mil_on: true,
            reported_count: 2,
            protocol: "CAN".to_string(),
            trouble: String::new(),
        };

        assert!(result.count_disagrees());
        assert!(!result.clean());
    }

    #[test]
    fn agrees_when_they_agree() {
        let result = FaultScan {
            faults: vec![Dtc::new("P0301", DtcState::Stored)],
            mil_on: true,
            reported_count: 1,
            protocol: "CAN".to_string(),
            trouble: String::new(),
        };

        assert!(!result.count_disagrees());
    }

    // ----- erasing ------------------------------------------------------------

    #[test]
    fn clears_the_stored_and_pending_codes() {
        let car = FakeElm::new();
        car.with(|s| {
            s.stored_codes = strings(&["P0301", "P0171"]);
            s.pending_codes = strings(&["P0300"]);
            s.answers.insert(0x01, vec![0x82, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let cleared = clear(&mut elm).unwrap();

        assert!(cleared.erased);
        assert!(car.with(|s| s.stored_codes.is_empty()));
        assert!(car.with(|s| s.pending_codes.is_empty()));
        assert_eq!(car.with(|s| s.clear_requests), 1);
    }

    /// What mode 04 actually costs, which is a great deal more than the codes: the freeze
    /// frame is the one record of what the engine was doing when the fault happened, and
    /// the readiness monitors have to be re-earned over a full drive cycle before the car
    /// can pass a test.
    #[test]
    fn says_what_else_was_erased() {
        let car = FakeElm::new();
        car.with(|s| {
            s.stored_codes = strings(&["P0420"]);
            s.answers.insert(0x01, vec![0x81, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let message = clear(&mut elm).unwrap().message.to_ascii_lowercase();

        assert!(message.contains("readiness"));
        assert!(message.contains("drive cycle"));
    }

    /// Permanent codes survive, which is the entire reason they exist. A person told the
    /// car is clear who then fails a test on a code that was there the whole time has been
    /// misled by the tool.
    #[test]
    fn reports_permanent_codes_that_survived_the_erase() {
        let car = FakeElm::new();
        car.with(|s| {
            s.stored_codes = strings(&["P0420"]);
            s.permanent_codes = strings(&["P0420"]);
            s.answers.insert(0x01, vec![0x81, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let cleared = clear(&mut elm).unwrap();

        assert!(cleared.erased);
        assert_eq!(codes(&cleared.remaining), ["P0420"]);
        let message = cleared.message.to_ascii_lowercase();
        assert!(message.contains("permanent"));
        assert!(!message.contains("readiness"));
        assert!(
            cleared
                .message
                .starts_with("Erased, but 1 permanent code is still set.")
        );
    }

    /// Most cars will not erase with the engine running, and the refusal is worth
    /// translating: "did not answer" would send somebody looking for a loose connector
    /// when the fix is to turn the key back one click.
    #[test]
    fn explains_a_refusal() {
        let car = FakeElm::new();
        car.with(|s| {
            s.refuse_clear = true;
            s.stored_codes = strings(&["P0301"]);
            s.answers.insert(0x01, vec![0x81, 0x07, 0xE5, 0x00]);
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();

        let cleared = clear(&mut elm).unwrap();

        assert!(!cleared.erased);
        assert!(cleared.message.to_ascii_lowercase().contains("engine running"));
        assert_eq!(car.with(|s| s.stored_codes.len()), 1);
    }

    // ----- Rust-side additions: the conversation around the reset ---------------

    #[test]
    fn the_reset_sequence_is_sent_in_order() {
        let car = FakeElm::new();
        let mut elm = elm_for(&car);

        let name = elm.reset(None).unwrap();

        assert_eq!(name, "ELM327 v1.5");
        assert_eq!(car.received(), ["ATZ", "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"]);
    }

    #[test]
    fn a_stn_chip_names_itself_and_a_clone_is_called_what_it_says() {
        let car = FakeElm::new();
        car.with(|s| {
            s.product = "OBDLink r2.6".into();
            s.firmware = "STN1100 v2.2.2".into();
        });
        let mut elm = elm_for(&car);
        elm.reset(None).unwrap();
        assert_eq!(elm.identify().unwrap(), "OBDLink r2.6 (STN1100 v2.2.2)");

        let clone = FakeElm::new();
        let mut elm = elm_for(&clone);
        elm.reset(None).unwrap();
        assert_eq!(elm.identify().unwrap(), "ELM327 v1.5");
    }
}
