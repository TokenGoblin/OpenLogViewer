//! Ported from `Obd2Pids.cs`: the standard OBD2 parameters this reads, per SAE J1979.
//!
//! The great advantage over every other ECU here is that none of this needs a
//! definition file: the numbering, the scaling and the units are the same on every
//! compliant vehicle, which is the entire point of the standard. What differs is
//! which parameters a given car answers to, and the car itself says so: `0100`,
//! `0120` and `0140` each return a bitmask of the thirty-two that follow.
//!
//! Ranges are the standard's own, so a dial's ends mean what the encoding can hold.
//! The warning and danger limits are not: OBD2 describes what a value is and never
//! what a safe one would be, so those are the figures a workshop manual would use on
//! an ordinary car. They are conventions, the gauge list says as much, and a
//! highly-strung engine or a cold climate can make any of them wrong. Only the
//! parameters with a convention worth having get them; nobody can say what a wrong
//! road speed is.

use std::fmt;
use std::sync::OnceLock;

use olv_core::{Error, Result};

/// Fills `into` with this PID's channels, in order (`Obd2Decode`).
pub type Obd2Decode = fn(&[u8], &mut [f64]);

/// One thing a PID reports: a name, a unit, and the range it can hold.
#[derive(Clone, Debug, PartialEq)]
pub struct Obd2Channel {
    pub name: String,
    pub units: String,
    pub digits: i32,
    pub low: f64,
    pub high: f64,

    /// Where the readings stop being good news, where that can be said at all.
    ///
    /// Not from the standard, which describes what a value is and never what a safe
    /// one would be: it has no idea what this engine's coolant should read. These are
    /// the figures a workshop manual would use on an ordinary car, and they are
    /// stated as conventions rather than facts about the vehicle in front of you.
    ///
    /// Left at NaN where there is no convention worth having.
    pub low_danger: f64,
    pub low_warning: f64,
    pub high_warning: f64,
    pub high_danger: f64,
}

impl Obd2Channel {
    /// A channel with no limits stated (all four NaN).
    pub fn new(name: &str, units: &str, digits: i32, low: f64, high: f64) -> Self {
        Self {
            name: name.to_string(),
            units: units.to_string(),
            digits,
            low,
            high,
            low_danger: f64::NAN,
            low_warning: f64::NAN,
            high_warning: f64::NAN,
            high_danger: f64::NAN,
        }
    }

    /// The same channel with limits, filling the unused ends with the scale's own so
    /// they say nothing.
    ///
    /// A warning given without a danger does not become one: a single stored fault
    /// code is worth a note and is not an emergency, so the danger falls back to the
    /// end of the scale rather than to the warning. A danger given without a warning
    /// does drag the warning up to meet it, since a warning band sitting above a
    /// danger band would paint the dial in the wrong order.
    pub fn limits(
        &self,
        low_danger: Option<f64>,
        low_warning: Option<f64>,
        high_warning: Option<f64>,
        high_danger: Option<f64>,
    ) -> Self {
        let mut c = self.clone();
        c.low_danger = low_danger.unwrap_or(self.low);
        c.low_warning = low_warning.or(low_danger).unwrap_or(self.low);
        c.high_warning = high_warning.or(high_danger).unwrap_or(self.high);
        c.high_danger = high_danger.unwrap_or(self.high);
        c
    }
}

/// A mode-01 parameter: what to ask for, how much comes back, and what it means.
#[derive(Clone, Debug)]
pub struct Obd2Pid {
    pub pid: u8,

    /// Data bytes the standard defines for this PID, after the echoed mode and PID.
    pub data_bytes: usize,

    pub channels: Vec<Obd2Channel>,

    /// The decode itself; call [`Obd2Pid::decode`], which checks lengths.
    pub decode: Obd2Decode,

    /// Asked for on every cycle rather than in rotation.
    ///
    /// One request is one round trip and a dongle answers a few dozen a second at
    /// best, so asking for everything every time would drag the headline gauges down
    /// to the speed of the slowest thing on the list. The handful marked here are the
    /// ones a needle is expected to follow.
    pub hot: bool,
}

impl Obd2Pid {
    /// Runs the decode. The C# indexes the spans directly and throws on a short
    /// one; this reports it as an error instead of panicking.
    pub fn decode(&self, data: &[u8], into: &mut [f64]) -> Result<()> {
        if data.len() < self.data_bytes {
            return Err(Error::Argument(format!(
                "{self} needs {} data bytes, got {}.",
                self.data_bytes,
                data.len()
            )));
        }
        if into.len() < self.channels.len() {
            return Err(Error::Argument(format!(
                "{self} has {} channels, got room for {}.",
                self.channels.len(),
                into.len()
            )));
        }

        (self.decode)(data, into);
        Ok(())
    }
}

impl fmt::Display for Obd2Pid {
    /// `01XX`, the request that asks for it.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "01{:02X}", self.pid)
    }
}

/// A tachometer's top.
///
/// The one place the standard's range is no use: RPM is encoded to 16,383.75, which is
/// the counter's ceiling and not any engine's, and a dial drawn to it leaves every real
/// reading in the first quarter. This is a judgement rather than a measurement: OBD2
/// offers no way to ask a car for its redline.
pub const TACHOMETER_TOP: f64 = 8000.0;

/// The PIDs that report which PIDs a car supports.
///
/// Each returns four bytes, one bit per parameter in the range that follows it, most
/// significant bit first. Asking is far better than the alternative of trying every
/// parameter and waiting for "NO DATA": a round trip apiece, on the slowest link in
/// this application.
///
/// The chain runs the whole way rather than stopping at 0x40. Each answer's last bit
/// says whether a further range exists and the walk stops the moment a car says no, so
/// on a vehicle that ends at 0x40 this costs nothing.
pub const SUPPORT_QUERIES: [u8; 7] = [0x00, 0x20, 0x40, 0x60, 0x80, 0xA0, 0xC0];

/// Every parameter this knows how to decode, in PID order.
pub fn all() -> &'static [Obd2Pid] {
    static ALL: OnceLock<Vec<Obd2Pid>> = OnceLock::new();
    ALL.get_or_init(build_all)
}

/// Reads a support bitmask into the PID numbers it stands for.
///
/// `query` is the PID that was asked; its reply covers the thirty-two numbers after
/// it. Bit 31 of the first byte is the first of them, so `0100` answering 0x80 means
/// the car supports 0x01.
pub fn supported_by(query: u8, mask: &[u8]) -> Vec<u8> {
    let mut supported = Vec::new();
    if mask.len() < 4 {
        return supported;
    }

    for bit in 0..32usize {
        let set = (mask[bit / 8] & (0x80 >> (bit % 8))) != 0;
        if set {
            supported.push((i32::from(query) + bit as i32 + 1) as u8);
        }
    }

    supported
}

/// How many data bytes a parameter carries, or zero where this does not know it.
///
/// Zero is the useful answer rather than a failure. It is what keeps an unrecognised
/// parameter out of a batched request, where the groups run into one another with
/// nothing to separate them and a length nobody knows costs every group after it.
pub fn data_bytes_of(pid: u8) -> usize {
    all().iter().find(|p| p.pid == pid).map_or(0, |p| p.data_bytes)
}

/// The parameters this knows about, out of a set the car reports.
pub fn known(supported: impl IntoIterator<Item = u8>) -> Vec<Obd2Pid> {
    let wanted: Vec<u8> = supported.into_iter().collect();
    all().iter().filter(|p| wanted.contains(&p.pid)).cloned().collect()
}

fn word(d: &[u8]) -> f64 {
    f64::from((256 * i32::from(d[0])) + i32::from(d[1]))
}

/// Fuel trim: zero is no correction, and either sign is meaningful.
fn trim(d: &[u8]) -> f64 {
    (f64::from(d[0]) / 1.28) - 100.0
}

/// A fuel trim, with the thresholds a diagnostic manual uses.
///
/// The one place these figures are close to universal: the correction is a percentage
/// against the ECU's own base fuelling, so ten per cent is ten per cent whatever the
/// engine, and beyond about twenty-five something is actually wrong rather than merely
/// being compensated for.
fn trimmed(pid: u8, name: &str) -> Obd2Pid {
    single(
        pid,
        name,
        "%",
        1,
        -100.0,
        99.2,
        decode_trim,
        Opts {
            low_danger: Some(-25.0),
            low_warning: Some(-10.0),
            high_warning: Some(10.0),
            high_danger: Some(25.0),
            ..Opts::default()
        },
    )
}

fn decode_trim(d: &[u8], v: &mut [f64]) {
    v[0] = trim(d);
}

#[derive(Default)]
struct Opts {
    bytes: Option<usize>,
    hot: bool,
    low_danger: Option<f64>,
    low_warning: Option<f64>,
    high_warning: Option<f64>,
    high_danger: Option<f64>,
}

impl Opts {
    fn bytes(n: usize) -> Self {
        Self {
            bytes: Some(n),
            ..Self::default()
        }
    }

    fn hot() -> Self {
        Self {
            hot: true,
            ..Self::default()
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn single(pid: u8, name: &str, units: &str, digits: i32, low: f64, high: f64, decode: Obd2Decode, o: Opts) -> Obd2Pid {
    let base = Obd2Channel::new(name, units, digits, low, high);

    // No limits at all where none were given, rather than four that sit on the ends
    // of the scale. The two behave the same on screen, but only the first is honest
    // about the gauge having nothing to say.
    let channel =
        if o.low_danger.is_none() && o.low_warning.is_none() && o.high_warning.is_none() && o.high_danger.is_none() {
            base
        } else {
            base.limits(o.low_danger, o.low_warning, o.high_warning, o.high_danger)
        };

    Obd2Pid {
        pid,
        data_bytes: o.bytes.unwrap_or(1),
        channels: vec![channel],
        decode,
        hot: o.hot,
    }
}

fn build_all() -> Vec<Obd2Pid> {
    let high_warning = |warning: f64, danger: f64| Opts {
        high_warning: Some(warning),
        high_danger: Some(danger),
        ..Opts::default()
    };

    vec![
        Obd2Pid {
            pid: 0x01,
            data_bytes: 4,
            channels: vec![
                // The one limit the standard does state. "MIL" is the malfunction
                // indicator lamp, and the standard's word for it being commanded on is
                // "malfunction", so a lit lamp is not a convention about what is
                // probably bad, it is the car saying so.
                Obd2Channel::new("MIL", "", 0, 0.0, 1.0).limits(None, None, None, Some(0.5)),
                Obd2Channel::new("DTC Count", "", 0, 0.0, 127.0).limits(None, None, Some(0.5), None),
            ],
            decode: |d, v| {
                v[0] = if (d[0] & 0x80) != 0 { 1.0 } else { 0.0 };
                v[1] = f64::from(d[0] & 0x7F);
            },
            hot: false,
        },
        single(
            0x04,
            "Engine Load",
            "%",
            1,
            0.0,
            100.0,
            |d, v| v[0] = f64::from(d[0]) * 100.0 / 255.0,
            Opts::hot(),
        ),
        single(
            0x05,
            "Coolant",
            "\u{b0}C",
            0,
            -40.0,
            215.0,
            |d, v| v[0] = f64::from(d[0]) - 40.0,
            high_warning(105.0, 115.0),
        ),
        trimmed(0x06, "Short Fuel Trim B1"),
        trimmed(0x07, "Long Fuel Trim B1"),
        trimmed(0x08, "Short Fuel Trim B2"),
        trimmed(0x09, "Long Fuel Trim B2"),
        single(
            0x0A,
            "Fuel Pressure",
            "kPa",
            0,
            0.0,
            765.0,
            |d, v| v[0] = f64::from(d[0]) * 3.0,
            Opts::default(),
        ),
        single(
            0x0B,
            "MAP",
            "kPa",
            0,
            0.0,
            255.0,
            |d, v| v[0] = f64::from(d[0]),
            Opts::hot(),
        ),
        single(
            0x0C,
            "RPM",
            "rpm",
            0,
            0.0,
            TACHOMETER_TOP,
            |d, v| v[0] = word(d) / 4.0,
            Opts {
                hot: true,
                ..Opts::bytes(2)
            },
        ),
        single(
            0x0D,
            "Speed",
            "km/h",
            0,
            0.0,
            255.0,
            |d, v| v[0] = f64::from(d[0]),
            Opts::hot(),
        ),
        single(
            0x0E,
            "Timing Advance",
            "\u{b0}",
            1,
            -64.0,
            63.5,
            |d, v| v[0] = f64::from(d[0]) / 2.0 - 64.0,
            Opts::default(),
        ),
        single(
            0x0F,
            "IAT",
            "\u{b0}C",
            0,
            -40.0,
            215.0,
            |d, v| v[0] = f64::from(d[0]) - 40.0,
            high_warning(80.0, 100.0),
        ),
        single(
            0x10,
            "MAF",
            "g/s",
            2,
            0.0,
            655.35,
            |d, v| v[0] = word(d) / 100.0,
            Opts::bytes(2),
        ),
        single(
            0x11,
            "Throttle Position",
            "%",
            1,
            0.0,
            100.0,
            |d, v| v[0] = f64::from(d[0]) * 100.0 / 255.0,
            Opts::hot(),
        ),
        single(
            0x1F,
            "Run Time",
            "s",
            0,
            0.0,
            65535.0,
            |d, v| v[0] = word(d),
            Opts::bytes(2),
        ),
        single(
            0x21,
            "Distance with MIL",
            "km",
            0,
            0.0,
            65535.0,
            |d, v| v[0] = word(d),
            Opts::bytes(2),
        ),
        single(
            0x23,
            "Fuel Rail Pressure",
            "kPa",
            0,
            0.0,
            655350.0,
            |d, v| v[0] = word(d) * 10.0,
            Opts::bytes(2),
        ),
        // Four bytes: an equivalence ratio and the sensor voltage. Only the ratio is
        // taken; the voltage is diagnostic detail about the sensor rather than a
        // reading about the engine.
        single(
            0x24,
            "Lambda",
            "",
            3,
            0.0,
            2.0,
            |d, v| v[0] = word(d) / 32768.0,
            Opts::bytes(4),
        ),
        single(
            0x2F,
            "Fuel Level",
            "%",
            1,
            0.0,
            100.0,
            |d, v| v[0] = f64::from(d[0]) * 100.0 / 255.0,
            Opts {
                low_danger: Some(5.0),
                low_warning: Some(15.0),
                ..Opts::default()
            },
        ),
        single(
            0x31,
            "Distance Since Cleared",
            "km",
            0,
            0.0,
            65535.0,
            |d, v| v[0] = word(d),
            Opts::bytes(2),
        ),
        single(
            0x33,
            "Barometric Pressure",
            "kPa",
            0,
            0.0,
            255.0,
            |d, v| v[0] = f64::from(d[0]),
            Opts::default(),
        ),
        single(
            0x42,
            "Battery",
            "V",
            2,
            0.0,
            65.535,
            |d, v| v[0] = word(d) / 1000.0,
            Opts {
                low_danger: Some(11.5),
                low_warning: Some(12.2),
                high_warning: Some(14.8),
                high_danger: Some(15.2),
                ..Opts::bytes(2)
            },
        ),
        single(
            0x43,
            "Absolute Load",
            "%",
            1,
            0.0,
            25700.0,
            |d, v| v[0] = word(d) * 100.0 / 255.0,
            Opts::bytes(2),
        ),
        single(
            0x45,
            "Relative Throttle",
            "%",
            1,
            0.0,
            100.0,
            |d, v| v[0] = f64::from(d[0]) * 100.0 / 255.0,
            Opts::default(),
        ),
        single(
            0x46,
            "Ambient Air Temp",
            "\u{b0}C",
            0,
            -40.0,
            215.0,
            |d, v| v[0] = f64::from(d[0]) - 40.0,
            Opts::default(),
        ),
        single(
            0x5C,
            "Engine Oil Temp",
            "\u{b0}C",
            0,
            -40.0,
            210.0,
            |d, v| v[0] = f64::from(d[0]) - 40.0,
            high_warning(120.0, 135.0),
        ),
        single(
            0x5E,
            "Fuel Rate",
            "L/h",
            2,
            0.0,
            3276.75,
            |d, v| v[0] = word(d) / 20.0,
            Opts::bytes(2),
        ),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_table_is_in_pid_order_and_complete() {
        let pids: Vec<u8> = all().iter().map(|p| p.pid).collect();
        assert_eq!(pids.len(), 28);
        assert!(pids.windows(2).all(|w| w[0] < w[1]));
    }

    #[test]
    fn a_bitmask_names_the_pids_that_follow_it() {
        assert_eq!(supported_by(0x00, &[0x80, 0, 0, 0]), vec![0x01]);
        assert_eq!(
            supported_by(0x00, &[0xBE, 0x3F, 0xA8, 0x13]),
            vec![
                0x01, 0x03, 0x04, 0x05, 0x06, 0x07, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x13, 0x15, 0x1C, 0x1F,
                0x20
            ]
        );
        assert!(supported_by(0x00, &[0xFF, 0xFF, 0xFF]).is_empty());
    }

    #[test]
    fn unknown_parameters_have_no_length() {
        assert_eq!(data_bytes_of(0x0C), 2);
        assert_eq!(data_bytes_of(0x24), 4);
        assert_eq!(data_bytes_of(0x99), 0);
    }

    #[test]
    fn the_standard_formulas_are_applied() {
        let mut v = [0.0; 2];
        let rpm = all().iter().find(|p| p.pid == 0x0C).unwrap();
        rpm.decode(&[0x1A, 0xF8], &mut v).unwrap();
        assert_eq!(v[0], 1726.0);

        let coolant = all().iter().find(|p| p.pid == 0x05).unwrap();
        coolant.decode(&[0x1E], &mut v).unwrap();
        assert_eq!(v[0], -10.0);

        let mil = all().iter().find(|p| p.pid == 0x01).unwrap();
        mil.decode(&[0x82, 0, 0, 0], &mut v).unwrap();
        assert_eq!(v, [1.0, 2.0]);

        assert!(rpm.decode(&[0x1A], &mut v).is_err());
    }

    #[test]
    fn limits_fill_the_unused_ends_with_the_scales_own() {
        let c = Obd2Channel::new("x", "", 0, 0.0, 10.0).limits(None, None, Some(5.0), None);
        assert_eq!(c.low_danger, 0.0);
        assert_eq!(c.low_warning, 0.0);
        assert_eq!(c.high_warning, 5.0);
        assert_eq!(c.high_danger, 10.0);

        let d = Obd2Channel::new("x", "", 0, 0.0, 10.0).limits(None, None, None, Some(7.0));
        assert_eq!(d.high_warning, 7.0);
    }
}
