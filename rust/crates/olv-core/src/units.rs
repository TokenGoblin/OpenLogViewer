//! Ported from `UnitConvert.cs`: temperatures, speeds and pressures between the units
//! a log reports and the system the user reads in. Everything else passes through.
//!
//! This is the worked example of a ported module: the C# tests' intent as unit tests
//! here, and the C# reference outputs checked in `tests/oracle_units.rs`.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum UnitSystem {
    AsReported,
    Metric,
    Imperial,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Measure {
    None,
    Celsius,
    Fahrenheit,
    KilometresPerHour,
    MilesPerHour,
    Kilopascals,
    PoundsPerSquareInch,
}

const MILES_TO_KILOMETRES: f64 = 1.609344;
const KILOPASCALS_PER_PSI: f64 = 6.894_757_293_168_361;

/// A reading in the units it was logged in, converted to `to`.
pub fn value(value: f64, units: &str, to: UnitSystem) -> f64 {
    if value.is_nan() {
        return value;
    }
    use Measure::*;
    use UnitSystem::*;
    match (identify(units), to) {
        (Celsius, Imperial) => (value * 9.0 / 5.0) + 32.0,
        (Fahrenheit, Metric) => (value - 32.0) * 5.0 / 9.0,
        (KilometresPerHour, Imperial) => value / MILES_TO_KILOMETRES,
        (MilesPerHour, Metric) => value * MILES_TO_KILOMETRES,
        (Kilopascals, Imperial) => value / KILOPASCALS_PER_PSI,
        (PoundsPerSquareInch, Metric) => value * KILOPASCALS_PER_PSI,
        _ => value,
    }
}

/// The inverse of [`value`]: a reading typed in `from`, back to the logged units.
pub fn to_reported(value: f64, units: &str, from: UnitSystem) -> f64 {
    if value.is_nan() {
        return value;
    }
    use Measure::*;
    use UnitSystem::*;
    match (identify(units), from) {
        (Celsius, Imperial) => (value - 32.0) * 5.0 / 9.0,
        (Fahrenheit, Metric) => (value * 9.0 / 5.0) + 32.0,
        (KilometresPerHour, Imperial) => value * MILES_TO_KILOMETRES,
        (MilesPerHour, Metric) => value / MILES_TO_KILOMETRES,
        (Kilopascals, Imperial) => value * KILOPASCALS_PER_PSI,
        (PoundsPerSquareInch, Metric) => value / KILOPASCALS_PER_PSI,
        _ => value,
    }
}

/// The unit label to show for `units` read in `to`.
pub fn label(units: &str, to: UnitSystem) -> String {
    use Measure::*;
    use UnitSystem::*;
    match (identify(units), to) {
        (Celsius, Imperial) => "°F",
        (Fahrenheit, Metric) => "°C",
        (KilometresPerHour, Imperial) => "mph",
        (MilesPerHour, Metric) => "km/h",
        (Kilopascals, Imperial) => "psi",
        (PoundsPerSquareInch, Metric) => "kPa",
        _ => units,
    }
    .to_string()
}

/// Whether reading `units` in `to` changes anything.
pub fn converts(units: &str, to: UnitSystem) -> bool {
    label(units, to) != units
}

/// What a unit string is measuring, where that is beyond doubt. "degrees" is
/// ignition advance and a bare "°" is OBD2 timing, so both fall through unconverted.
fn identify(units: &str) -> Measure {
    match simplify(units).as_str() {
        "c" | "degc" | "degreesc" | "celsius" | "centigrade" => Measure::Celsius,
        "f" | "degf" | "degreesf" | "fahrenheit" => Measure::Fahrenheit,
        "km/h" | "kmh" | "kph" | "kmph" | "kilometresperhour" => Measure::KilometresPerHour,
        "mph" | "mi/h" | "milesperhour" => Measure::MilesPerHour,
        "kpa" | "kilopascals" | "kilopascal" => Measure::Kilopascals,
        "psi" | "lbf/in2" | "poundspersquareinch" => Measure::PoundsPerSquareInch,
        _ => Measure::None,
    }
}

/// Lowercase, without the degree sign, spaces, dots, underscores or tabs.
fn simplify(units: &str) -> String {
    units
        .chars()
        .filter(|c| !matches!(c, '°' | ' ' | '.' | '_' | '\t'))
        .flat_map(char::to_lowercase)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nan_passes_through_every_conversion() {
        assert!(value(f64::NAN, "°C", UnitSystem::Imperial).is_nan());
        assert!(to_reported(f64::NAN, "psi", UnitSystem::Metric).is_nan());
    }
}
