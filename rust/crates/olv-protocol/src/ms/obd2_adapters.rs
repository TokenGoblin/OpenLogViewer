//! Ported from `Obd2Adapters.cs`. Owned by package `w1-ms-maxx`.
//!
//! Recognising an OBD2 dongle by the name it advertises.
//!
//! A guess, and unavoidably one. Neither Bluetooth Classic nor BLE publishes
//! anything that says "I am an ELM327": Classic offers the serial port profile,
//! which a MegaSquirt over Bluetooth offers too, and BLE offers vendor service
//! numbers that mean whatever the maker decided. The name is all there is.
//!
//! Getting it wrong is not cosmetic. A dongle that goes unrecognised is probed
//! with TunerStudio commands instead, finds nothing, and reports an unknown ECU,
//! which is a confusing thing to be told about hardware that is working perfectly.
//! A real one did exactly that: an OBDLink advertises as "ScanTool.net-5487", after
//! the company rather than the product, and matched none of the names being looked
//! for.

/// Fragments of names that mean "this is an OBD2 adapter".
///
/// Matched anywhere in the name, case-insensitively, because these arrive with a
/// serial number or a model suffix attached far more often than not.
pub const NAMES: &[&str] = &[
    // Generic, and what most of the cheap ones call themselves.
    "OBDII", "OBD2", "OBD-II", "ELM327",
    // OBDLink, and the two other names the same company ships under. The product is
    // called one thing and advertises as another: an OBDLink r2.6 comes up as
    // "ScanTool.net-5487".
    "OBDLink", "ScanTool", "OBDSol", "ElmScan", "STN11", "STN21",
    // The rest of the common aftermarket ones.
    "Vgate", "vLinker", "V-LINK", "VEEPEAK", "Konnwei", "Carista", "LELink", "Viecar", "iCar",
];

/// Whether a name looks like an OBD2 adapter.
///
/// False for `None` and blank on purpose: an unnamed device is not evidence of
/// anything, and guessing it is an adapter would route a MegaSquirt into the OBD2
/// path, which is the same mistake in the other direction.
pub fn looks_like_one(name: Option<&str>) -> bool {
    let Some(name) = name else { return false };
    if name.chars().all(char::is_whitespace) {
        return false;
    }

    let haystack: Vec<char> = name.chars().collect();
    NAMES.iter().any(|n| contains_ignore_case(&haystack, n))
}

/// Whether any of several names does.
pub fn looks_like_any(names: &[Option<&str>]) -> bool {
    names.iter().any(|n| looks_like_one(*n))
}

/// `string.Contains(n, StringComparison.OrdinalIgnoreCase)` for an ASCII needle.
/// .NET's ordinal casing never folds a non-ASCII letter (the dotless i, the long s,
/// the Kelvin sign) onto an ASCII one, so only ASCII case differences count.
fn contains_ignore_case(haystack: &[char], needle: &str) -> bool {
    let needle: Vec<char> = needle.chars().collect();
    haystack
        .windows(needle.len())
        .any(|w| w.iter().zip(&needle).all(|(a, b)| a.eq_ignore_ascii_case(b)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn real_advertised_names_are_recognised() {
        assert!(looks_like_one(Some("ScanTool.net-5487")));
        assert!(looks_like_one(Some("vgate iCar Pro")));
        assert!(looks_like_one(Some("OBDII")));
        assert!(looks_like_one(Some("my elm327 v1.5")));
        assert!(looks_like_one(Some("xxV-LINKxx")));
    }

    #[test]
    fn unnamed_and_unrelated_devices_are_not() {
        assert!(!looks_like_one(None));
        assert!(!looks_like_one(Some("")));
        assert!(!looks_like_one(Some("  \t")));
        assert!(!looks_like_one(Some("MicroSquirt")));
        // The dotless i is not an ASCII i under ordinal casing.
        assert!(!looks_like_one(Some("\u{131}Car")));
    }

    #[test]
    fn any_of_several_names() {
        assert!(looks_like_any(&[None, Some("x"), Some("Konnwei")]));
        assert!(!looks_like_any(&[None, Some("x")]));
        assert!(!looks_like_any(&[]));
    }
}
