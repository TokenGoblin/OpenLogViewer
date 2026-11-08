//! Ported from `ChannelClassifier.cs`. Owned by package `w1-channels`.
//!
//! Sorts ECU channel names into categories so a 179-channel log can be browsed.
//!
//! Names vary between firmwares ("DutyCycle1", "Duty Cycle1", "duty_cycle_1"), so names are
//! normalised to lower-case space-separated tokens first. Matching is then done on whole
//! tokens, which keeps short keys like "ve" and "pw" from matching inside unrelated words
//! such as "valve" or "power". All comparisons are ordinal, as the C#'s are.

use std::collections::HashSet;

/// Broad grouping for a channel. Declaration order is display order.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum ChannelCategory {
    Common,
    Engine,
    Air,
    Fuel,
    Ignition,
    Temperature,
    Idle,
    Electrical,
    Diagnostics,
    Other,
}

/// The channels a tuner reaches for first, promoted above their natural category.
const COMMON_NAMES: &[&str] = &[
    "rpm",
    "map",
    "tps",
    "afr",
    "afr 1",
    "afr 2",
    "lambda",
    "clt",
    "mat",
    "iat",
    "batt v",
    "boost psi",
    "boost",
    "ve 1",
    "pw",
    "pw 1",
    "spark advance",
    "advance",
    "dwell",
    "knock",
    "afr target 1",
    "afr 1 target",
    "target afr",
    "duty cycle 1",
    "ego cor 1",
    "accel enrich",
    "load",
    "afr load",
    "engine",
];

/// A leading namespace token and the system it assigns the channel to.
fn namespace_owner(token: &str) -> Option<ChannelCategory> {
    Some(match token {
        "fuel" | "seq" | "inj" | "injector" => ChannelCategory::Fuel,
        "spk" | "spark" | "ign" | "knock" => ChannelCategory::Ignition,
        "idle" => ChannelCategory::Idle,
        _ => return None,
    })
}

/// Ordered: the first rule whose token matches wins, so more specific rules come first.
/// Several channels legitimately touch two systems ("Injector timing", "SPK: Fuel cut
/// retard", "VVT duty"), and the ordering is what decides which system owns them.
const RULES: &[(ChannelCategory, &[&str])] = &[
    // Unambiguously spark-side, claimed before "fuel"/"duty" can grab them.
    (ChannelCategory::Ignition, &["spk", "spark", "dwell", "knock", "coil"]),
    (ChannelCategory::Idle, &["idle", "iac", "isv", "pwmidle"]),
    (
        ChannelCategory::Air,
        &[
            "map",
            "tps",
            "boost",
            "baro",
            "barometer",
            "throttle",
            "vacuum",
            "wastegate",
            "vvt",
            "cam",
        ],
    ),
    (
        ChannelCategory::Fuel,
        &[
            "afr", "ego", "lambda", "ve", "pw", "duty", "injector", "inj", "fuel", "accel", "enrich", "maf", "gammae",
            "warmup", "priming", "ase", "pulse", "squirt", "o2", "wideband", "mpg", "gallons",
        ],
    ),
    // Weaker spark words, only after fuel has had its say.
    (
        ChannelCategory::Ignition,
        &["advance", "retard", "timing", "ign", "ignition"],
    ),
    (
        ChannelCategory::Temperature,
        &["clt", "mat", "iat", "temp", "egt", "coolant", "cht", "thermistor"],
    ),
    (
        ChannelCategory::Electrical,
        &[
            "batt", "battery", "volt", "volts", "adc", "gpio", "gpioadc", "current", "amps", "amp", "sensor",
        ],
    ),
    (
        ChannelCategory::Engine,
        &[
            "rpm", "load", "torque", "speed", "gear", "vss", "crank", "sync", "engine", "revs", "power", "odometer",
            "trip", "miles", "distance",
        ],
    ),
    // "sec" covers SecL, which camel-splits to "sec l".
    (
        ChannelCategory::Diagnostics,
        &[
            "error", "status", "cel", "can", "flag", "flags", "bit", "bits", "mainloop", "sec", "secl", "seconds",
            "counter", "count", "watchdog", "loop", "fault", "warning", "sdcard", "port",
        ],
    ),
];

/// `char.ToLowerInvariant`: a one-to-one mapping, so a character whose lowercase is several
/// characters stays as it is.
fn lower_char(c: char) -> char {
    let mut lower = c.to_lowercase();
    match (lower.next(), lower.next()) {
        (Some(l), None) => l,
        _ => c,
    }
}

/// `string.ToLowerInvariant`, character by character.
fn lower_invariant(s: &str) -> String {
    s.chars().map(lower_char).collect()
}

/// Decodes one UTF-16 unit as the C# sees it: a lone surrogate half is no character at all
/// (and so is neither a letter nor a digit).
fn unit_char(unit: u16) -> Option<char> {
    char::from_u32(u32::from(unit))
}

/// Classifies a channel by name, falling back to its units (pass `""` for none).
pub fn classify(name: &str, units: &str) -> ChannelCategory {
    let normalised = normalise(name);
    if normalised.is_empty() {
        return ChannelCategory::Other;
    }
    if COMMON_NAMES.contains(&normalised.as_str()) {
        return ChannelCategory::Common;
    }

    let ordered: Vec<&str> = normalised.split(' ').filter(|t| !t.is_empty()).collect();
    let tokens: HashSet<&str> = ordered.iter().copied().collect();

    // Firmwares namespace channels with a leading word ("Fuel: Baro cor",
    // "SPK: Fuel cut retard"). That prefix names the system the channel
    // belongs to, and outranks any keyword later in the name.
    if ordered.len() > 1
        && let Some(owner) = namespace_owner(ordered[0])
    {
        return owner;
    }

    for (category, keys) in RULES {
        if keys.iter().any(|k| tokens.contains(k)) {
            return *category;
        }
    }

    // Firmwares sometimes run words together in all-caps ("TPSADC", "canin1"), leaving a
    // single token. Fall back to prefix matching: requiring the key at the start of a token
    // keeps "ase" from matching inside "phase", which plain substring matching does.
    for (category, keys) in RULES {
        if keys
            .iter()
            .any(|k| k.len() >= 3 && tokens.iter().any(|t| t.starts_with(k)))
        {
            return *category;
        }
    }

    classify_by_units(units)
}

fn classify_by_units(units: &str) -> ChannelCategory {
    match lower_invariant(units.trim()).as_str() {
        "°f" | "°c" | "degf" | "degc" => ChannelCategory::Temperature,
        "v" | "volts" | "mv" => ChannelCategory::Electrical,
        "bit" | "bits" => ChannelCategory::Diagnostics,
        "rpm" => ChannelCategory::Engine,
        "kpa" | "psi" | "bar" => ChannelCategory::Air,
        "afr" | "lambda" | "ms" => ChannelCategory::Fuel,
        _ => ChannelCategory::Other,
    }
}

/// What a category is called in the channel browser.
pub fn display_name(category: ChannelCategory) -> &'static str {
    match category {
        ChannelCategory::Common => "Common",
        ChannelCategory::Engine => "Engine",
        ChannelCategory::Air => "Air & boost",
        ChannelCategory::Fuel => "Fuel",
        ChannelCategory::Ignition => "Ignition",
        ChannelCategory::Temperature => "Temperature",
        ChannelCategory::Idle => "Idle",
        ChannelCategory::Electrical => "Electrical",
        ChannelCategory::Diagnostics => "Diagnostics",
        ChannelCategory::Other => "Other",
    }
}

/// Lower-cases and splits a channel name into space-separated tokens, breaking at camel-case
/// humps and letter/digit boundaries so that "DutyCycle1", "Duty Cycle1" and "duty_cycle_1"
/// all normalise alike.
///
/// Walks UTF-16 units, as the C# walks `char`s.
pub fn normalise(name: &str) -> String {
    let units: Vec<u16> = name.encode_utf16().collect();
    let mut sb = String::with_capacity(name.len() + 8);

    for (i, &unit) in units.iter().enumerate() {
        let Some(c) = unit_char(unit).filter(|c| c.is_alphanumeric()) else {
            if !sb.is_empty() && !sb.ends_with(' ') {
                sb.push(' ');
            }
            continue;
        };

        if i > 0 && !sb.is_empty() && !sb.ends_with(' ') {
            let previous = unit_char(units[i - 1]);
            let hump = previous.is_some_and(char::is_lowercase) && c.is_uppercase();
            let letter_digit = previous.is_some_and(char::is_alphabetic) != c.is_alphabetic();
            if hump || letter_digit {
                sb.push(' ');
            }
        }

        sb.push(lower_char(c));
    }

    sb.trim().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use ChannelCategory::*;

    #[test]
    fn normalise_splits_humps_and_separators() {
        for (input, expected) in [
            ("DutyCycle1", "duty cycle 1"),
            ("Duty Cycle1", "duty cycle 1"),
            ("duty_cycle_1", "duty cycle 1"),
            ("VE1", "ve 1"),
            ("AFR2", "afr 2"),
            ("Batt V", "batt v"),
            ("Fuel: Warmup cor", "fuel warmup cor"),
            ("gpioadc4", "gpioadc 4"),
            ("MAP", "map"),
        ] {
            assert_eq!(normalise(input), expected, "{input}");
        }
    }

    #[test]
    fn everyday_channels_are_promoted_to_common() {
        for name in ["RPM", "MAP", "AFR", "CLT", "TPS", "Batt V", "VE1"] {
            assert_eq!(classify(name, ""), Common, "{name}");
        }
    }

    #[test]
    fn channels_land_in_the_expected_category() {
        for (name, expected) in [
            ("EGO cor2", Fuel),
            ("Fuel: Accel enrich", Fuel),
            ("MAFload", Fuel),
            ("Boost psi", Common),
            ("MAP dot", Air),
            ("Barometer", Air),
            ("EGT 6 temp", Temperature),
            ("Dwell", Common),
            ("Spark Advance", Common),
            ("Closed loop idle target RPM", Idle),
            ("ADC6", Electrical),
            ("CAN error count", Diagnostics),
            ("Mainloop time", Diagnostics),
            ("SecL", Diagnostics),
            ("Torque", Engine),
        ] {
            assert_eq!(classify(name, ""), expected, "{name}");
        }
    }

    #[test]
    fn short_keys_do_not_match_inside_longer_words() {
        // "ve" must not match "Valve", nor "pw" match "Power".
        assert_ne!(classify("Valve position", "%"), Fuel);
        assert_ne!(classify("Power level", "%"), Fuel);
    }

    #[test]
    fn run_together_caps_fall_back_to_prefix_matching() {
        // "TPSADC" normalises to a single token that matches no whole key.
        assert_eq!(classify("TPSADC", ""), Air);
        assert_eq!(classify("canin1_8", ""), Diagnostics);
    }

    #[test]
    fn prefix_fallback_does_not_match_mid_word() {
        // "ase" (after-start enrichment) must not match inside "ph-ase".
        assert_eq!(classify("SDcard phase", ""), Diagnostics);
    }

    #[test]
    fn overlapping_channels_resolve_by_rule_order() {
        // Channels that touch two systems; rule order decides the owner.
        for (name, expected) in [
            ("SPK: Fuel cut retard", Ignition),
            ("SPK: Idle Correction Advance", Ignition),
            ("Injector timing pri", Fuel),
            ("Timing err", Ignition),
            ("PWM Idle duty", Idle),
            ("VVT duty 1", Air),
            ("Duty Cycle2", Fuel),
            ("Fuel temperature cor", Fuel),
            ("ECU Temperature", Temperature),
        ] {
            assert_eq!(classify(name, ""), expected, "{name}");
        }
    }

    #[test]
    fn leading_namespace_token_claims_the_channel() {
        // The leading namespace token wins over keywords later in the name.
        for (name, expected) in [
            ("Fuel: Baro cor", Fuel),
            ("Fuel: Air cor", Fuel),
            ("Seq PW1", Fuel),
            ("SPK: MAT Retard", Ignition),
            ("SPK: Launch VSS Retard", Ignition),
            ("Ign load", Ignition),
        ] {
            assert_eq!(classify(name, ""), expected, "{name}");
        }
    }

    #[test]
    fn units_are_used_when_the_name_says_nothing() {
        for (name, units, expected) in [
            ("Widget", "°F", Temperature),
            ("Widget", "kPa", Air),
            ("Widget", "bits", Diagnostics),
            ("Widget", "", Other),
        ] {
            assert_eq!(classify(name, units), expected, "{name} {units}");
        }
    }

    #[test]
    fn every_category_has_a_display_name() {
        for c in [
            Common,
            Engine,
            Air,
            Fuel,
            Ignition,
            Temperature,
            Idle,
            Electrical,
            Diagnostics,
            Other,
        ] {
            assert!(!display_name(c).trim().is_empty());
        }
    }
}
