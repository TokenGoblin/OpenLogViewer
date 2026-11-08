//! Ported from `ChannelUnits.cs`. Owned by package `w2-math-channels`.
//!
//! Reading a channel in the units the arithmetic wants, whatever the logger wrote it in.
//!
//! The quiet destroyer of anything computed across logs. A MegaSquirt logs MAP in kilopascals
//! and an American tune logs boost in psi; intake air is degrees C on one controller and F on
//! another. An estimate that assumes one of them is out by a factor of seven, or by forty
//! degrees of absolute temperature, and it produces a horsepower figure that looks like a
//! number rather than like a mistake.
//!
//! Each function returns a fragment of expression rather than a value, because the conversion
//! has to happen inside the calculated channel where every sample goes through it.

use crate::base::tuning_math::{self, Fuel};
use crate::channels::channel_roles;
use crate::fmt;
use crate::model::LogChannel;

/// A pressure channel, in kilopascals.
///
/// Absolute or gauge is not converted here and cannot be: the units say "psi", never "psi
/// above atmosphere".
pub fn to_kilopascals(channel: &LogChannel) -> String {
    let factor = match simplify(channel.units()).as_str() {
        "kpa" | "" => 1.0,
        "psi" | "psig" | "psia" => tuning_math::KPA_PER_PSI,
        "bar" => 100.0,
        "mbar" | "millibar" => 0.1,
        "inhg" | "hg" => 3.386389,
        "hpa" => 0.1,
        "pa" => 0.001,
        _ => 1.0,
    };
    scale(channel, factor)
}

/// A temperature channel, in degrees Celsius. Fahrenheit needs an offset as well as a factor.
pub fn to_celsius(channel: &LogChannel) -> String {
    let name = reference(channel);

    match simplify(channel.units()).as_str() {
        "f" | "degf" | "fahrenheit" => format!("(({name} - 32) * 5 / 9)"),
        "k" | "kelvin" => format!("({name} - 273.15)"),
        _ => name,
    }
}

/// A temperature channel, in kelvin, which is what gas density needs.
pub fn to_kelvin(channel: &LogChannel) -> String {
    format!("({} + 273.15)", to_celsius(channel))
}

/// A mass flow channel, in grams per second.
pub fn to_grams_per_second(channel: &LogChannel) -> String {
    let factor = match simplify(channel.units()).as_str() {
        "g/s" | "gs" | "gps" | "" => 1.0,
        "kg/h" | "kgh" | "kg/hr" | "kghr" => 1000.0 / 3600.0,
        "kg/min" | "kgmin" => 1000.0 / 60.0,
        "lb/min" | "lbmin" | "lbsmin" => 453.59237 / 60.0,
        "lb/h" | "lbh" | "lb/hr" | "lbhr" | "lbsh" => 453.59237 / 3600.0,
        "g/min" | "gmin" => 1.0 / 60.0,
        _ => 1.0,
    };
    scale(channel, factor)
}

/// A time channel, in milliseconds: injector pulse widths are logged in both.
pub fn to_milliseconds(channel: &LogChannel) -> String {
    let factor = match simplify(channel.units()).as_str() {
        "ms" | "msec" | "millisecond" | "milliseconds" | "" => 1.0,
        "s" | "sec" | "second" | "seconds" => 1000.0,
        "us" | "usec" | "microsecond" | "microseconds" => 0.001,
        _ => 1.0,
    };
    scale(channel, factor)
}

/// A proportion channel as a fraction of one, whether it was logged as a percentage or
/// already as a fraction. Told apart by the unit and not by the values.
pub fn to_fraction(channel: &LogChannel) -> String {
    let percent = matches!(simplify(channel.units()).as_str(), "%" | "percent" | "pct");
    scale(channel, if percent { 0.01 } else { 1.0 })
}

/// Whether a mixture channel is lambda rather than an air-fuel ratio.
///
/// The unit decides where it says so. Where it does not, the values do: lambda lives around
/// one and an air-fuel ratio around fifteen.
pub fn is_lambda(channel: &LogChannel) -> bool {
    match simplify(channel.units()).as_str() {
        "lambda" | "l" => return true,
        // ":1" can never match: `simplify` removes the colon (ported as-is).
        "afr" | ":1" | "ratio" => return false,
        _ => {}
    }

    // The middle of the range rather than the mean, so a few samples of a sensor warming up
    // cannot swing it.
    let middle = typical(channel);

    middle.is_finite() && middle < 5.0
}

/// A mixture channel expressed as an air-fuel ratio on the given fuel.
pub fn to_air_fuel_ratio(channel: &LogChannel, fuel: Fuel) -> String {
    if is_lambda(channel) {
        format!(
            "({} * {})",
            reference(channel),
            number(tuning_math::stoichiometric(fuel))
        )
    } else {
        reference(channel)
    }
}

/// The midpoint of the values actually present, ignoring the ones that are not readings.
pub(crate) fn typical(channel: &LogChannel) -> f64 {
    let mut low = f64::INFINITY;
    let mut high = f64::NEG_INFINITY;

    for i in 0..channel.len() {
        let v = channel.at(i);
        if !v.is_finite() {
            continue;
        }
        low = low.min(v);
        high = high.max(v);
    }

    if low.is_finite() && high.is_finite() {
        (low + high) / 2.0
    } else {
        f64::NAN
    }
}

/// A channel by name.
pub fn reference(channel: &LogChannel) -> String {
    channel.name().to_string()
}

fn scale(channel: &LogChannel, factor: f64) -> String {
    if (factor - 1.0).abs() < 1e-12 {
        reference(channel)
    } else {
        format!("({} * {})", reference(channel), number(factor))
    }
}

/// A number as the expression parser will read it: invariant, always, since a decimal comma
/// turns one number into two arguments.
pub fn number(value: f64) -> String {
    fmt::f64_inv(value, "R")
}

fn simplify(units: &str) -> String {
    channel_roles::simplify(units)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ch(units: &str, v: &[f64]) -> LogChannel {
        LogChannel::new("X", units, 1, v, false)
    }

    #[test]
    fn lambda_by_unit_and_by_value() {
        assert!(is_lambda(&ch("", &[0.9, 1.0, 1.1])));
        assert!(!is_lambda(&ch("", &[12.0, 14.7])));
        assert!(!is_lambda(&ch("AFR", &[0.9])));
    }

    #[test]
    fn conversions() {
        assert_eq!(to_kilopascals(&ch("bar", &[1.0])), "(X * 100)");
        assert_eq!(to_celsius(&ch("F", &[1.0])), "((X - 32) * 5 / 9)");
        assert_eq!(to_fraction(&ch("%", &[1.0])), "(X * 0.01)");
        assert_eq!(to_milliseconds(&ch("ms", &[1.0])), "X");
    }
}
