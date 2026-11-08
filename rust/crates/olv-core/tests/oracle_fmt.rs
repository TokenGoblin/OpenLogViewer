//! `fmt` against the C# reference (rust/oracle/expected/w0-model/fmt.json).
//!
//! Every format string the C# uses (rust/oracle/formats.txt) plus the default
//! `ToString()`, over doubles chosen for the ways formatters differ and a spread of
//! ints, in both the invariant culture and en-US. Text must match exactly.

use olv_core::fmt::{self, Culture};
use serde::Deserialize;

#[derive(Deserialize)]
struct Case {
    kind: String,
    culture: String,
    format: String,
    text: String,
    bits: Option<String>,
    value: Option<i64>,
}

#[test]
fn every_format_and_value_matches_the_csharp_in_both_cultures() {
    let cases: Vec<Case> = olv_oracle::cases("w0-model", "fmt");
    assert!(!cases.is_empty());

    let mut failures = Vec::new();
    for c in &cases {
        let culture = match c.culture.as_str() {
            "invariant" => Culture::Invariant,
            "en-US" => Culture::EnUs,
            other => panic!("unknown culture {other}"),
        };
        let (what, got) = match c.kind.as_str() {
            "f64" => {
                let bits = c.bits.as_deref().expect("f64 case has bits");
                let v = f64::from_bits(u64::from_str_radix(bits, 16).expect("hex bits"));
                (format!("{v:e} ({bits})"), fmt::try_f64(v, &c.format, culture))
            }
            "int" => {
                let v = c.value.expect("int case has a value");
                let got = match i32::try_from(v) {
                    Ok(small) => fmt::try_int(small, &c.format, culture),
                    Err(_) => fmt::try_long(v, &c.format, culture),
                };
                (v.to_string(), got)
            }
            other => panic!("unknown kind {other}"),
        };
        match got {
            Ok(text) if text == c.text => {}
            other => failures.push(format!(
                "{} {what} {:?} in {}: got {other:?}, C# gave {:?}",
                c.kind, c.format, c.culture, c.text
            )),
        }
    }
    assert!(
        failures.is_empty(),
        "{} of {} cases differ:\n{}",
        failures.len(),
        cases.len(),
        failures.join("\n")
    );
}
