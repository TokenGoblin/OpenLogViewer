//! The reference the port is measured against.
//!
//! `tools/OpenLogViewer.Oracle` (C#) runs the shipping code over synthetic inputs and
//! over the local corpus, and writes what it got to `rust/oracle/expected/`. Tests
//! load those files through here and compare. A package is done when its tests
//! agree with the C#, not when its code looks right.
//!
//! Layout: `expected/<package-id>/<set>.json` for synthetic inputs (committed) and
//! `expected/corpus/<package-id>/<set>.json` for real logs and tunes (personal,
//! gitignored). Each file is `{ "generator": ..., "cases": [ ... ] }`.

use serde::Deserialize;
use serde::de::DeserializeOwned;
use std::path::PathBuf;

/// `rust/oracle/expected`, or `$OLV_ORACLE_DIR` when set.
pub fn dir() -> PathBuf {
    std::env::var_os("OLV_ORACLE_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../oracle/expected"))
}

/// Where corpus inputs live: `rust/corpus`.
pub fn corpus_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../corpus")
}

#[derive(Deserialize)]
struct File<T> {
    cases: Vec<T>,
}

/// The synthetic cases for a package. Panics when missing: those are committed, so a
/// missing file is a broken checkout, not a reason to skip.
pub fn cases<T: DeserializeOwned>(package: &str, set: &str) -> Vec<T> {
    let path = dir().join(package).join(format!("{set}.json"));
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str::<File<T>>(&text)
        .unwrap_or_else(|e| panic!("{}: {e}", path.display()))
        .cases
}

/// The corpus cases for a package, or None when this machine has no corpus, in which
/// case the test should return early: CI has no personal logs.
pub fn corpus_cases<T: DeserializeOwned>(package: &str, set: &str) -> Option<Vec<T>> {
    let path = dir().join("corpus").join(package).join(format!("{set}.json"));
    let text = std::fs::read_to_string(&path).ok()?;
    Some(
        serde_json::from_str::<File<T>>(&text)
            .unwrap_or_else(|e| panic!("{}: {e}", path.display()))
            .cases,
    )
}

/// Equal within `abs` or within `rel` of the larger magnitude. NaN equals NaN and an
/// infinity equals only itself, because the C# writes those too.
pub fn close(actual: f64, expected: f64, rel: f64, abs: f64) -> bool {
    if actual.is_nan() || expected.is_nan() {
        return actual.is_nan() && expected.is_nan();
    }
    if actual.is_infinite() || expected.is_infinite() {
        return actual == expected;
    }
    let diff = (actual - expected).abs();
    diff <= abs || diff <= rel * actual.abs().max(expected.abs())
}

/// The default tolerance: arithmetic done in a different order, nothing more.
#[track_caller]
pub fn assert_close(actual: f64, expected: f64, what: &str) {
    assert!(
        close(actual, expected, 1e-9, 1e-12),
        "{what}: got {actual}, C# gave {expected}"
    );
}

/// FNV-1a over the little-endian bits of every sample. The C# side computes the same
/// thing (`Fnv.Samples`), so a whole channel can be compared without shipping it.
pub fn fnv1a_f32(samples: &[f32]) -> u64 {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for s in samples {
        for byte in s.to_bits().to_le_bytes() {
            h ^= u64::from(byte);
            h = h.wrapping_mul(0x0000_0100_0000_01b3);
        }
    }
    h
}

/// A JSON number as the C# writes it: NaN and the infinities arrive as strings.
pub fn number(v: &serde_json::Value) -> f64 {
    match v {
        serde_json::Value::Number(n) => n.as_f64().unwrap_or(f64::NAN),
        serde_json::Value::String(s) => match s.as_str() {
            "NaN" => f64::NAN,
            "Infinity" => f64::INFINITY,
            "-Infinity" => f64::NEG_INFINITY,
            other => other.parse().unwrap_or(f64::NAN),
        },
        _ => f64::NAN,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nan_and_infinity_compare_as_the_csharp_writes_them() {
        assert!(close(f64::NAN, f64::NAN, 0.0, 0.0));
        assert!(!close(1.0, f64::NAN, 1.0, 1.0));
        assert!(close(f64::INFINITY, f64::INFINITY, 0.0, 0.0));
        assert!(!close(f64::INFINITY, f64::NEG_INFINITY, 1.0, 1.0));
    }

    #[test]
    fn fnv_of_nothing_is_the_offset_basis() {
        assert_eq!(fnv1a_f32(&[]), 0xcbf2_9ce4_8422_2325);
    }

    #[test]
    fn fnv_matches_a_value_worked_by_hand() {
        // 1.0f32 = 0x3F800000, bytes 00 00 80 3F
        let mut h: u64 = 0xcbf2_9ce4_8422_2325;
        for b in [0x00u8, 0x00, 0x80, 0x3F] {
            h ^= u64::from(b);
            h = h.wrapping_mul(0x100_0000_01b3);
        }
        assert_eq!(fnv1a_f32(&[1.0]), h);
    }
}
