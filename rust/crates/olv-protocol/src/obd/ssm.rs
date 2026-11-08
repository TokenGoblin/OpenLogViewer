//! Ported from `Ssm.cs`: talking to a Subaru ECU in its own language, over CAN.
//!
//! SSM is Subaru's diagnostic protocol and it reaches things OBD2 has no notion of: what
//! the ECU has learnt rather than what it is measuring. Knock correction, fine knock
//! learning, the ignition advance multiplier, fuelling trims the standard does not
//! expose: the values that say whether an engine is happy, none of which appear in mode
//! 01 at any speed.
//!
//! The received wisdom is that an ELM327-compatible adapter cannot speak it, and over the
//! older K-line cars that is true: SSM frames itself there, with a header, a length and a
//! checksum the adapter will not produce. Over CAN the ISO-TP layer does the framing, the
//! command bytes alone are the payload, and a 2014 Crosstrek answers an OBDLink r2.6
//! perfectly well. Confirmed on a running car: engine speed read over SSM landed between
//! two OBD2 readings taken either side of it, and coolant returned the identical raw byte
//! to PID 05.
//!
//! One byte per request, and that is not a shortcut taken here. The ECU does not
//! implement the block read; a two-address request needs eight bytes and the adapter
//! will not send more than seven, with auto-formatting on or off; and the extended send
//! command that exists to solve exactly this is absent from this firmware. Measured at
//! 146 ms a byte, so eight values is about 0.85 Hz.
//!
//! Which suits what SSM is for. These are learnt values that move over seconds and
//! minutes, not transients: for watching what an ECU has decided about a drive it is
//! ample, and for catching a misfire it is useless.

use olv_core::model::eq_ignore_case;
use olv_core::{Error, Result};

use super::elm327::{split_lines, unhex};

/// Read one or more addresses. The reply echoes [`READ_ECHO`].
pub const READ_ADDRESSES: u8 = 0xA8;

/// A positive answer to [`READ_ADDRESSES`].
pub const READ_ECHO: u8 = 0xE8;

/// Where the engine module listens.
pub const ENGINE_HEADER: &str = "7E0";

/// And answers.
pub const ENGINE_REPLIES: &str = "7E8";

/// The most addresses one request can carry.
///
/// One, on the hardware this was proven against, and the reason is worth keeping next to
/// the number. The request is the command, a mandatory padding byte and three bytes per
/// address, so two addresses is eight bytes where an ELM327 send caps at seven. Every
/// route round it was tried on a live car and closed: the ECU refuses the block read,
/// refuses a request without the padding byte, and the adapter refuses eight bytes
/// whether or not it is formatting the frame itself.
///
/// Left as a constant rather than inlined because a newer adapter would lift it, and when
/// one does this is the only line that needs to change.
pub const ADDRESSES_PER_REQUEST: usize = 1;

/// The commands that put an adapter into a state where SSM can be spoken.
pub const SETUP: [&str; 5] = ["ATSH7E0", "ATCRA7E8", "ATFCSH7E0", "ATFCSD300000", "ATFCSM1"];

/// Puts an adapter back to ordinary OBD2 addressing.
pub const RESTORE: [&str; 3] = ["ATCRA", "ATFCSM0", "ATSH7DF"];

/// One value an ECU will report over SSM: where it lives and what it means.
#[derive(Clone, Debug, PartialEq)]
pub struct SsmParameter {
    /// What to call the channel.
    pub name: String,

    /// Where the first byte lives in the ECU's address space.
    pub address: i32,

    /// How many consecutive bytes make up the value, most significant first.
    pub bytes: i32,

    /// What the scaled value is in.
    pub units: String,

    /// Multiplied by the raw value.
    pub scale: f64,

    /// Added after scaling, so a temperature is raw minus 40.
    pub offset: f64,

    /// Decimals worth showing.
    pub digits: i32,

    /// Bottom of the gauge, where one is wanted.
    pub low: f64,

    /// Top of the gauge.
    pub high: f64,

    /// Whether to actually read it. The whole point of the flag: one address per request
    /// means the list you read is a budget, so the file is allowed to hold every
    /// parameter a car offers while only a handful are switched on. Choosing from a list
    /// beats remembering an address.
    pub enabled: bool,
}

impl SsmParameter {
    /// A parameter with the C#'s defaults: one byte, no units, scale 1, offset 0, a gauge
    /// from 0 to 255, enabled.
    pub fn new(name: impl Into<String>, address: i32) -> Self {
        Self {
            name: name.into(),
            address,
            bytes: 1,
            units: String::new(),
            scale: 1.0,
            offset: 0.0,
            digits: 0,
            low: 0.0,
            high: 255.0,
            enabled: true,
        }
    }

    /// Every address this parameter occupies, in order.
    pub fn addresses(&self) -> impl Iterator<Item = i32> + use<> {
        let start = self.address;
        (0..self.bytes.max(1)).map(move |i| start.wrapping_add(i))
    }

    /// The reading, from the raw bytes in the order the ECU returned them.
    ///
    /// Big-endian, which is what SSM is: engine speed's high byte lives at the lower
    /// address. Read the other way round a 900 rpm idle comes out at 13,830, which is
    /// wrong in a way nobody would fail to notice, but the same mistake on a two-byte
    /// temperature is quietly plausible.
    pub fn read(&self, raw: &[u8]) -> f64 {
        if raw.is_empty() {
            return f64::NAN;
        }

        let mut value: i64 = 0;
        for &b in raw {
            value = (value << 8) | i64::from(b);
        }

        (value as f64 * self.scale) + self.offset
    }

    /// Whether this describes something a gauge could be drawn for.
    pub fn is_usable(&self) -> bool {
        !self.name.is_empty() && self.address >= 0 && (1..=4).contains(&self.bytes) && self.scale != 0.0
    }
}

/// The request that reads a run of addresses.
///
/// The padding byte after the command is not optional. Sent without it the ECU parses the
/// request and refuses it for length, which is a useful thing to have learnt, since it
/// proves the vehicle is reading the command rather than ignoring it.
pub fn read_request(addresses: &[i32]) -> Result<String> {
    if addresses.is_empty() {
        return Err(Error::Argument("No addresses.".to_string()));
    }

    let mut text = String::new();
    text.push_str(&format!("{READ_ADDRESSES:02X}"));
    text.push_str("00");

    for &address in addresses {
        if !(0..=0xFF_FFFF).contains(&address) {
            return Err(Error::Argument(format!(
                "An SSM address is three bytes. (Parameter 'addresses')\nActual value was {address}."
            )));
        }

        text.push_str(&format!("{address:06X}"));
    }

    Ok(text)
}

/// The data bytes out of a reply, or empty where it was not one.
///
/// A positive answer is the echo followed by one byte per address asked for. A refusal is
/// 0x7F, the command, and why (0x13 for a length the ECU did not expect, 0x12 for a
/// sub-function it does not support) and those are told apart from silence because they
/// mean the vehicle is listening.
pub fn read_reply(reply: Option<&str>, expected: usize) -> Vec<u8> {
    let Some(reply) = reply else { return Vec::new() };
    if reply.chars().all(char::is_whitespace) {
        return Vec::new();
    }

    let mut bytes = [0u8; 32];

    for line in split_lines(reply) {
        let got = unhex(line, &mut bytes);

        if got < 1 + expected || bytes[0] != READ_ECHO {
            continue;
        }

        return bytes[1..1 + expected].to_vec();
    }

    Vec::new()
}

/// Whether a reply is the ECU refusing rather than the link being silent.
///
/// Worth separating. A refusal proves SSM reached a module that understood it, which is
/// the difference between "this car does not speak SSM" and "that address was wrong", and
/// only one of those is worth another attempt.
pub fn refused(reply: Option<&str>) -> bool {
    reply.is_some_and(|r| contains_ignore_case(r, "7F") && contains_ignore_case(r, "A8"))
}

fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    let h: Vec<char> = haystack.chars().collect();
    let n: Vec<char> = needle.chars().collect();
    h.windows(n.len())
        .any(|w| eq_ignore_case(&w.iter().collect::<String>(), needle))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_read_request_pads_and_zero_fills_each_address() {
        // The two addresses that were measured on a running car.
        assert_eq!(read_request(&[0x00000E]).unwrap(), "A800".to_string() + "00000E");
        assert_eq!(read_request(&[0x000008]).unwrap(), "A80000".to_string() + "0008");
    }

    #[test]
    fn a_read_request_refuses_what_it_cannot_encode() {
        assert!(read_request(&[]).is_err());
        assert!(read_request(&[-1]).is_err());
        assert!(read_request(&[0x1000000]).is_err());
        assert_eq!(read_request(&[0xFFFFFF]).unwrap(), "A800FFFFFF");
    }

    #[test]
    fn a_positive_reply_yields_the_data_after_the_echo() {
        assert_eq!(read_reply(Some("E8 1A F8\r"), 2), vec![0x1A, 0xF8]);
        assert_eq!(read_reply(Some("SEARCHING...\rE81AF8\r"), 2), vec![0x1A, 0xF8]);
        assert!(read_reply(Some("E81A"), 2).is_empty());
        assert!(read_reply(Some("7F A8 13"), 1).is_empty());
        assert!(read_reply(Some("  "), 1).is_empty());
        assert!(read_reply(None, 1).is_empty());
    }

    #[test]
    fn a_refusal_is_told_apart_from_silence() {
        assert!(refused(Some("7F A8 13")));
        assert!(refused(Some("7fa813")));
        assert!(!refused(Some("NO DATA")));
        assert!(!refused(None));
    }

    #[test]
    fn engine_speed_reads_big_endian() {
        // Engine speed's high byte lives at the lower address: raw 0x0E10 = 3600, a quarter
        // of which is 900 rpm.
        let rpm = SsmParameter {
            bytes: 2,
            scale: 0.25,
            ..SsmParameter::new("Engine Speed", 0x0E)
        };
        assert_eq!(rpm.read(&[0x0E, 0x10]), 900.0);

        let coolant = SsmParameter {
            offset: -40.0,
            ..SsmParameter::new("Coolant", 0x08)
        };
        assert_eq!(coolant.read(&[0x78]), 80.0);
        assert!(coolant.read(&[]).is_nan());
    }

    #[test]
    fn a_parameter_occupies_its_bytes_and_is_usable_only_when_sensible() {
        let two = SsmParameter {
            bytes: 2,
            ..SsmParameter::new("x", 0x10)
        };
        assert_eq!(two.addresses().collect::<Vec<_>>(), vec![0x10, 0x11]);

        let zero = SsmParameter {
            bytes: 0,
            ..SsmParameter::new("x", 0x10)
        };
        assert_eq!(zero.addresses().collect::<Vec<_>>(), vec![0x10]);
        assert!(!zero.is_usable());

        assert!(SsmParameter::new("x", 1).is_usable());
        assert!(!SsmParameter::new("", 1).is_usable());
        assert!(!SsmParameter::new("x", -1).is_usable());
        assert!(
            !SsmParameter {
                scale: 0.0,
                ..SsmParameter::new("x", 1)
            }
            .is_usable()
        );
        assert!(
            !SsmParameter {
                bytes: 5,
                ..SsmParameter::new("x", 1)
            }
            .is_usable()
        );
    }

    #[test]
    fn setup_and_restore_are_the_commands_the_hardware_needed() {
        assert_eq!(SETUP, ["ATSH7E0", "ATCRA7E8", "ATFCSH7E0", "ATFCSD300000", "ATFCSM1"]);
        assert_eq!(RESTORE, ["ATCRA", "ATFCSM0", "ATSH7DF"]);
    }
}

/// `SsmTests`: the parts with no source on top.
#[cfg(test)]
mod ssm_tests {
    use super::*;
    use crate::obd::ssm_parameter_file as file;

    /// The padding byte after the command is not optional. Sent without it the real ECU
    /// parses the request and refuses it for length, which is how it was found out.
    #[test]
    fn a_read_request_carries_the_command_the_padding_and_three_bytes_of_address() {
        assert_eq!(read_request(&[0x00000E]).unwrap(), "A80000000E");
        assert_eq!(read_request(&[0x000008]).unwrap(), "A800000008");
    }

    #[test]
    fn an_address_beyond_three_bytes_is_refused() {
        assert!(matches!(read_request(&[0x1000000]), Err(Error::Argument(_))));
        assert!(matches!(read_request(&[-1]), Err(Error::Argument(_))));
    }

    #[test]
    fn a_positive_reply_is_the_echo_and_one_byte_each() {
        assert_eq!(read_reply(Some("E80E"), 1), [0x0E]);
        assert_eq!(read_reply(Some("E886"), 1), [0x86]);
    }

    /// A refusal is told apart from silence, because a refusal proves the vehicle read the
    /// command, which is the difference between "this car does not speak SSM" and "that
    /// address was wrong".
    #[test]
    fn a_refusal_is_recognised_as_one() {
        for reply in [
            "7FA813", // length the ECU did not expect
            "7FA812", // sub-function it does not support
        ] {
            assert!(read_reply(Some(reply), 1).is_empty());
            assert!(refused(Some(reply)));
        }
    }

    #[test]
    fn silence_is_not_a_refusal() {
        for reply in ["NO DATA", "?", ""] {
            assert!(read_reply(Some(reply), 1).is_empty());
            assert!(!refused(Some(reply)));
        }
    }

    /// An answer to something else is not an answer to this.
    #[test]
    fn a_mode_01_reply_is_not_mistaken_for_an_ssm_one() {
        assert!(read_reply(Some("410C0E2B"), 1).is_empty());
    }

    /// The exact reading taken off the car: 0x0E36 over SSM, against OBD2 readings of
    /// 906.75 and 912.25 rpm taken either side of it.
    #[test]
    fn engine_speed_decodes_to_what_the_car_was_doing() {
        let rpm = SsmParameter {
            bytes: 2,
            scale: 0.25,
            ..SsmParameter::new("Engine Speed", 0x00000E)
        };

        let reading = rpm.read(&[0x0E, 0x36]);

        assert!((reading - 909.5).abs() < 5e-4);
        assert!((906.75..=912.25).contains(&reading));
    }

    /// Big-endian, and it matters. Read the other way round the same idle comes out at
    /// 13,830 rpm: obvious on a rev counter and quietly plausible on a two-byte
    /// temperature.
    #[test]
    fn the_high_byte_is_the_one_at_the_lower_address() {
        let rpm = SsmParameter {
            bytes: 2,
            scale: 0.25,
            ..SsmParameter::new("Engine Speed", 0x00000E)
        };

        assert!((rpm.read(&[0x0E, 0x36]) - 909.5).abs() < 5e-4);
        assert_eq!(rpm.read(&[0x36, 0x0E]), 3459.5);
    }

    /// Coolant at address 0x000008 returned 0x86, the identical raw byte to OBD2 PID 05
    /// read at the same moment, so the same arithmetic must give the same answer, 94 C.
    #[test]
    fn coolant_matches_what_obd2_said_at_the_same_moment() {
        let coolant = SsmParameter {
            units: "\u{b0}C".to_string(),
            offset: -40.0,
            ..SsmParameter::new("Coolant", 0x000008)
        };

        let raw = 0x86u8;

        assert_eq!(coolant.read(&[raw]), 94.0);
        assert_eq!(coolant.read(&[raw]), f64::from(raw) - 40.0);
    }

    #[test]
    fn a_parameter_covers_every_byte_it_spans() {
        assert_eq!(SsmParameter::new("a", 0x0E).addresses().collect::<Vec<_>>(), [0x0E]);
        let two = SsmParameter {
            bytes: 2,
            ..SsmParameter::new("a", 0x0E)
        };
        assert_eq!(two.addresses().collect::<Vec<_>>(), [0x0E, 0x0F]);
    }

    // ----- the file the addresses come from ------------------------------------

    #[test]
    fn the_template_is_a_file_this_can_read() {
        let parameters = file::read(Some(file::template()));

        assert_eq!(parameters.len(), 2);

        let rpm = parameters.iter().find(|p| p.name == "Engine Speed").unwrap();

        assert_eq!(rpm.address, 0x00000E);
        assert_eq!(rpm.bytes, 2);
        assert!((rpm.read(&[0x0E, 0x36]) - 909.5).abs() < 5e-4);

        let coolant = parameters.iter().find(|p| p.name == "Coolant").unwrap();

        assert_eq!(coolant.read(&[0x86]), 94.0);
    }

    #[test]
    fn addresses_are_read_as_hex_however_they_are_written() {
        for (text, expected) in [("0x00000E", 0x0E), ("00000E", 0x0E), ("0X8", 8), ("FFFFFF", 0xFFFFFF)] {
            assert_eq!(file::parse_address(Some(text)), Some(expected), "{text}");
        }
    }

    #[test]
    fn an_unusable_address_is_refused() {
        for text in [
            Some(""),
            None,
            Some("nonsense"),
            Some("1000000"), // beyond three bytes
        ] {
            assert_eq!(file::parse_address(text), None, "{text:?}");
        }
    }

    /// One bad entry costs that entry and not the file. Somebody filling this in by hand
    /// will get one wrong, and losing every parameter because the fourth has a typo would
    /// be a poor trade.
    #[test]
    fn a_bad_entry_does_not_cost_the_good_ones() {
        let json = r#"
            {
              "version": 1,
              "parameters": [
                { "name": "Good", "address": "0x000008" },
                { "name": "No address" },
                { "address": "0x00000E" },
                { "name": "Silly bytes", "address": "0x000010", "bytes": 99 },
                { "name": "Also good", "address": "0x000046" }
              ]
            }
            "#;

        let names: Vec<String> = file::read(Some(json)).into_iter().map(|p| p.name).collect();
        assert_eq!(names, ["Good", "Also good"]);
    }

    /// Two channels with one name would share a column, and every preset and filter
    /// matching on it would find whichever came first.
    #[test]
    fn a_duplicate_name_is_dropped() {
        let json = r#"
            {
              "parameters": [
                { "name": "Coolant", "address": "0x000008" },
                { "name": "coolant", "address": "0x000009" }
              ]
            }
            "#;

        assert_eq!(file::read(Some(json)).len(), 1);
    }

    /// A file may list everything a car offers while reading a few.
    ///
    /// One address per request makes the enabled list a budget rather than a preference:
    /// the base set is a hundred and sixty parameters and reading all of them would be
    /// twenty-four seconds a round. So the file holds the lot and a dozen are switched on,
    /// which makes changing what you watch a matter of moving a flag rather than finding an
    /// address and its scaling again.
    #[test]
    fn only_the_enabled_parameters_are_read() {
        let json = r#"
            {
              "parameters": [
                { "name": "On by default",  "address": "0x000008" },
                { "name": "Explicitly on",  "address": "0x000009", "enabled": true },
                { "name": "Switched off",   "address": "0x00000A", "enabled": false }
              ]
            }
            "#;

        // Everything is still in the file, so it can be switched back on.
        assert_eq!(file::read(Some(json)).len(), 3);

        let names: Vec<String> = file::enabled(Some(json)).into_iter().map(|p| p.name).collect();
        assert_eq!(names, ["On by default", "Explicitly on"]);
    }

    #[test]
    fn an_empty_or_broken_file_is_no_parameters_rather_than_a_throw() {
        for json in ["", "not json at all", "{}", r#"{"parameters":[]}"#] {
            assert!(file::read(Some(json)).is_empty(), "{json:?}");
        }
    }
}
