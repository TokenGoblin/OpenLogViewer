//! `units` against the C# reference (rust/oracle/expected/w0-model/units.json).
//!
//! The pattern every ported module follows: deserialize the cases, run the Rust,
//! compare. Doubles travel as IEEE bits, so the comparison is exact: the same
//! formula in the same order gives the same bits in both languages.

use olv_core::units::{self, UnitSystem};
use serde::Deserialize;

#[derive(Deserialize)]
struct Case {
    units: String,
    system: String,
    label: String,
    converts: bool,
    values: Vec<Values>,
}

#[derive(Deserialize)]
struct Values {
    input: String,
    value: String,
    to_reported: String,
}

fn bits(hex: &str) -> u64 {
    u64::from_str_radix(hex, 16).expect("hex bits")
}

#[test]
fn every_unit_and_system_matches_the_csharp() {
    let cases: Vec<Case> = olv_oracle::cases("w0-model", "units");
    assert!(!cases.is_empty());

    for c in &cases {
        let system = match c.system.as_str() {
            "AsReported" => UnitSystem::AsReported,
            "Metric" => UnitSystem::Metric,
            "Imperial" => UnitSystem::Imperial,
            other => panic!("unknown system {other}"),
        };
        let what = format!("{:?} in {}", c.units, c.system);

        assert_eq!(units::label(&c.units, system), c.label, "label of {what}");
        assert_eq!(units::converts(&c.units, system), c.converts, "converts {what}");

        for v in &c.values {
            let input = f64::from_bits(bits(&v.input));
            assert_eq!(
                units::value(input, &c.units, system).to_bits(),
                bits(&v.value),
                "value({input}) for {what}"
            );
            assert_eq!(
                units::to_reported(input, &c.units, system).to_bits(),
                bits(&v.to_reported),
                "to_reported({input}) for {what}"
            );
        }
    }
}
