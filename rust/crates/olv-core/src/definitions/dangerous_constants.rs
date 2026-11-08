//! Ported from `DangerousConstants.cs`. Owned by package `w2-definitions`.
//!
//! Recognising the handful of tune settings that exist specifically to stop the engine
//! hurting itself, so a write to one of them can be held to a stricter gate than an ordinary
//! VE cell.
//!
//! This is a heuristic, not a certified list: a name that merely contains one of the
//! fragments is flagged whether or not it is really load-bearing. That is the right side to be
//! wrong on: a false positive costs one extra confirmation, a false negative is a rev limiter
//! moved with no warning at all. There are no numeric limits in this rule set; the only
//! comparisons are the case-insensitive name lookup in the layout and an ordinal substring
//! test of each alias against the simplified name (so a name exactly equal to an alias, or
//! containing it, matches; one missing a single letter does not).

use crate::channels::channel_roles::simplify;
use crate::ini::tune_layout::TuneLayout;
use crate::model::eq_ignore_case;

/// A tuning setting whose whole job is to stop the engine doing something.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum DangerousRole {
    /// What stops the engine over-revving at all.
    RevLimiter,
    /// The RPM a launch or two-step control holds against, off idle.
    LaunchControlRpm,
    /// The ceiling on manifold pressure a boost controller is allowed to reach.
    BoostLimit,
    /// Cutting spark as a limiter's or a shift-cut's actual mechanism.
    IgnitionCut,
    /// Cutting fuel as a limiter's, an overrun's, or a shutdown's mechanism.
    FuelCut,
}

impl DangerousRole {
    /// Every role, in the declaration order `Enum.GetValues` gives; the first role with a
    /// matching alias wins.
    pub const ALL: [DangerousRole; 5] = [
        DangerousRole::RevLimiter,
        DangerousRole::LaunchControlRpm,
        DangerousRole::BoostLimit,
        DangerousRole::IgnitionCut,
        DangerousRole::FuelCut,
    ];

    /// The C# enum member's name.
    pub fn name(self) -> &'static str {
        match self {
            DangerousRole::RevLimiter => "RevLimiter",
            DangerousRole::LaunchControlRpm => "LaunchControlRpm",
            DangerousRole::BoostLimit => "BoostLimit",
            DangerousRole::IgnitionCut => "IgnitionCut",
            DangerousRole::FuelCut => "FuelCut",
        }
    }
}

/// The role a named constant plays, or `None` when this firmware declares no such constant,
/// or declares one that matches nothing here.
///
/// Checked against the layout's own constants first: matching by name alone, with no such
/// constant in the tune, would flag a typo the same as a real setting.
pub fn find(layout: &TuneLayout, constant_name: &str) -> Option<DangerousRole> {
    if constant_name.trim().is_empty() {
        return None;
    }

    if !layout.constants.iter().any(|c| eq_ignore_case(&c.name, constant_name)) {
        return None;
    }

    let simplified = simplify(constant_name);

    DangerousRole::ALL
        .into_iter()
        .find(|&role| aliases(role).iter().any(|alias| simplified.contains(alias)))
}

/// Whether a named constant is one of the roles above, for a caller that does not need which.
pub fn is_dangerous(layout: &TuneLayout, constant_name: &str) -> bool {
    find(layout, constant_name).is_some()
}

/// Name fragments, most of them substrings rather than whole names: a dangerous constant's
/// spelling varies by firmware far more than a log channel's does, so this is matched loosely
/// on purpose.
fn aliases(role: DangerousRole) -> &'static [&'static str] {
    match role {
        // "kindoflimiting" and "hardcut" are Speeduino's, read off a running 202501: they
        // choose how the limiter cuts, which is the limiter.
        DangerousRole::RevLimiter => &[
            "revlimiter",
            "revlimit",
            "revlim",
            "softlimit",
            "softlim",
            "hardlimit",
            "hardlim",
            "rpmlimit",
            "speedlimiter",
            "maxrpm",
            "limiterrpm",
            "rpmhardlimit",
            "rpmsoftlimit",
            "hardrevlimit",
            "softrevlimit",
            "kindoflimiting",
            "hardcut",
        ],

        // Bare "launch" and "lnch" on purpose. Speeduino calls the switch launchEnable and it
        // was slipping through a list that only knew launchRpm. A pin assignment caught
        // alongside it costs one extra confirmation; the switch itself missed costs more.
        DangerousRole::LaunchControlRpm => &[
            "launch",
            "lnch",
            "flatshiftrpm",
            "flatshift",
            "antilagrpm",
            "antilag",
            "clutchrpm",
            "twostep",
            "2steprpm",
            "stagedlaunch",
        ],

        DangerousRole::BoostLimit => &[
            "boostlimit",
            "boostctrlmax",
            "overboost",
            "maxboost",
            "boostcut",
            "boostfailsafe",
            "wastegatemax",
            "boostlimitkpa",
            "boostcontrolmax",
            "boostbygear",
        ],

        DangerousRole::IgnitionCut => &[
            "ignitioncut",
            "igncut",
            "sparkcut",
            "coilcut",
            "shiftcuttime",
            "flatshiftign",
            "cuttimeign",
            "sparkcuttime",
        ],

        // "afrprotect" guards the engine against running lean under load; the time it cuts
        // for is part of that guard, not a tuning preference.
        DangerousRole::FuelCut => &[
            "fuelcutrpm",
            "fuelcut",
            "overruncut",
            "decelfuelcutoff",
            "dfcorpm",
            "injectorcut",
            "fuelcutoff",
            "fuelcutoffrpm",
            "afrprotect",
        ],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ini::msq_ini::RealtimeType;
    use crate::ini::tune_layout::{TuneConstant, TunePage};
    use crate::model::IgnoreCaseMap;

    fn layout_with(names: &[&str]) -> TuneLayout {
        TuneLayout {
            pages: vec![TunePage {
                index: 0,
                size: 64,
                identifier: String::new(),
                read_command: String::new(),
                chunk_write_command: String::new(),
                burn_command: String::new(),
            }],
            constants: names
                .iter()
                .map(|n| {
                    let mut c = TuneConstant::new(*n, 0, 0, RealtimeType::U16);
                    c.low = 0.0;
                    c.high = 10000.0;
                    c
                })
                .collect(),
            pc_variables: Vec::new(),
            derived: IgnoreCaseMap::new(),
            little_endian: false,
            blocking_factor: 0,
            inter_write_delay: 0,
            after_burn_delay: 0,
        }
    }

    fn rank(role: DangerousRole) -> usize {
        DangerousRole::ALL.iter().position(|r| *r == role).unwrap()
    }

    // ----- DangerousConstantsTests -------------------------------------------------

    #[test]
    fn recognised_spellings_match_the_role_they_play() {
        use DangerousRole::*;
        let cases = [
            ("revLimit", RevLimiter),
            ("SoftLimit", RevLimiter),
            ("HardRevLim", RevLimiter),
            ("rpmHardLimit", RevLimiter),
            ("launchRpm", LaunchControlRpm),
            ("launchTiming", LaunchControlRpm),
            ("flatShiftRpm", LaunchControlRpm),
            ("boostCtrlMax", BoostLimit),
            ("overBoost", BoostLimit),
            ("ignitionCutTime", IgnitionCut),
            ("sparkCutRpm", IgnitionCut),
            ("fuelCutRpm", FuelCut),
            ("decelFuelCutOff", FuelCut),
        ];
        for (name, role) in cases {
            let layout = layout_with(&[name]);
            assert_eq!(find(&layout, name), Some(role), "{name}");
            assert!(is_dangerous(&layout, name), "{name}");
        }
    }

    #[test]
    fn an_ordinary_setting_matches_nothing() {
        for name in ["crankingRPM", "veTable1", "injectorLatency", "vehicleName"] {
            let layout = layout_with(&[name]);
            assert_eq!(find(&layout, name), None, "{name}");
            assert!(!is_dangerous(&layout, name), "{name}");
        }
    }

    #[test]
    fn a_spelling_the_firmware_does_not_declare_is_never_flagged() {
        // Matching by name alone, with no such constant in the tune, would flag a typo just
        // as readily as a real setting, and would refuse a write for the wrong reason: "no
        // such setting" is the honest one.
        let layout = layout_with(&["crankingRPM"]);
        assert_eq!(find(&layout, "revLimit"), None);
    }

    #[test]
    fn matching_is_case_insensitive_like_the_rest_of_an_ini() {
        let layout = layout_with(&["REVLIMIT"]);
        assert_eq!(find(&layout, "REVLIMIT"), Some(DangerousRole::RevLimiter));
        // And the lookup in the layout is too: a differently-cased request still finds it.
        assert_eq!(find(&layout, "revlimit"), Some(DangerousRole::RevLimiter));
    }

    #[test]
    fn an_empty_or_blank_name_is_never_flagged() {
        let layout = layout_with(&["revLimit"]);
        assert_eq!(find(&layout, ""), None);
        assert_eq!(find(&layout, "   "), None);
    }

    // ----- DangerousConstantsRealFirmwareTests -------------------------------------

    #[test]
    fn a_real_safety_critical_speeduino_setting_is_recognised() {
        // Read off a Speeduino running 202501, out of the 682 settings it declares. Four
        // were being missed; launchEnable mattered most, since the bench board has launch
        // control switched on with a 2,700 rpm soft limit.
        for name in [
            "hardRevLim",
            "SoftRevLim",
            "SoftLimMax",
            "SoftLimitMode",
            "SoftLimRetard",
            "launchEnable",
            "lnchHardLim",
            "lnchSoftLim",
            "launchHiLo",
            "boostLimit",
            "boostCutEnabled",
            "boostByGear1",
            "hardCutType",
            "kindOfLimiting0",
            "afrProtectCutTime",
        ] {
            assert!(
                is_dangerous(&layout_with(&[name]), name),
                "{name} is a setting whose job is to stop the engine hurting itself, and it was not recognised"
            );
        }
    }

    #[test]
    fn an_ordinary_setting_is_not_treated_as_dangerous() {
        // Ordinary settings from the same 682, which must not all become dangerous: a guard
        // that fires on everything is one people learn to pass blindly.
        for name in [
            "crankingRPM",
            "aseTaperTime",
            "aeColdPct",
            "battVCorMode",
            "boostFreq",
            "boostKP",
            "iacTPSlimit",
        ] {
            assert!(
                !is_dangerous(&layout_with(&[name]), name),
                "{name} is an ordinary setting and should not need confirming"
            );
        }
    }

    #[test]
    fn a_setting_this_firmware_does_not_declare_is_not_judged_at_all() {
        assert!(!is_dangerous(&layout_with(&["crankingRPM"]), "hardRevLim"));
    }

    // ----- the edges: every alias at, just inside and just outside its limit --------
    //
    // The rules have no numeric limits. Their edges are the alias text: the alias itself
    // (at the limit), the alias inside a longer name (just inside), and the alias missing
    // its last letter (just outside), for every alias of every role.

    #[test]
    fn every_alias_is_flagged_alone_and_inside_a_longer_name() {
        for role in DangerousRole::ALL {
            for alias in aliases(role) {
                // At the limit: the alias alone. The first role in declaration order with a
                // matching alias wins, so it is this role or an earlier one.
                let layout = layout_with(&[alias]);
                let found = find(&layout, alias).unwrap_or_else(|| panic!("{alias} alone must be flagged"));
                assert!(rank(found) <= rank(role), "{alias}: {found:?} vs {role:?}");

                // Just inside: embedded in a longer name, with separators and case, which the
                // simplification ignores.
                let wrapped = format!("My_{}_Setting", alias.to_uppercase());
                let layout = layout_with(&[&wrapped]);
                assert!(is_dangerous(&layout, &wrapped), "{wrapped} must be flagged");
            }
        }
    }

    #[test]
    fn an_alias_one_letter_short_is_flagged_only_if_another_alias_is_inside_it() {
        // Just outside: the alias without its last letter. It may still contain a shorter
        // alias ("launchrpm" minus a letter still holds "launch"), so the expectation is
        // computed from the list itself, and the cases with no shorter alias inside (checked
        // by name below) must come out unflagged.
        let mut unflagged = 0;
        for role in DangerousRole::ALL {
            for alias in aliases(role) {
                let short = &alias[..alias.len() - 1];
                let layout = layout_with(&[short]);
                let contained = DangerousRole::ALL
                    .iter()
                    .any(|r| aliases(*r).iter().any(|a| short.contains(a)));
                assert_eq!(is_dangerous(&layout, short), contained, "{short}");
                if !contained {
                    unflagged += 1;
                }
            }
        }
        assert!(
            unflagged > 20,
            "most short spellings must be left alone, got {unflagged}"
        );
    }

    #[test]
    fn the_explicit_just_outside_spellings_are_not_flagged() {
        // One per role, written out rather than derived from the list.
        for name in [
            "revimit",
            "launc",
            "lnc",
            "boostlimi",
            "boostcu",
            "ignitioncu",
            "sparkcu",
            "fuelcu",
            "afrprotec",
        ] {
            assert!(!is_dangerous(&layout_with(&[name]), name), "{name}");
        }
        // And the just-inside neighbour of the first: one letter longer still contains the
        // alias.
        assert!(is_dangerous(&layout_with(&["revlimite"]), "revlimite"));
    }

    #[test]
    fn when_two_roles_match_the_first_declared_wins() {
        // "flatshiftign" is both launch ("flatshift") and ignition cut; launch is declared
        // first. "fuelcuthardcut" holds a rev limiter fragment and a fuel cut one.
        let layout = layout_with(&["flatShiftIgn", "fuelcutHardCut"]);
        assert_eq!(find(&layout, "flatShiftIgn"), Some(DangerousRole::LaunchControlRpm));
        assert_eq!(find(&layout, "fuelcutHardCut"), Some(DangerousRole::RevLimiter));
    }

    #[test]
    fn simplification_ignores_separators_and_case_and_nothing_else() {
        for name in [
            "Rev-Limit",
            "rev_limit",
            "Rev Limit",
            "REV.LIMIT",
            "SPK:Rev/Limit",
            "rev\tlimit",
        ] {
            let layout = layout_with(&[name]);
            assert_eq!(find(&layout, name), Some(DangerousRole::RevLimiter), "{name}");
        }
        // A character that is not stripped breaks the fragment.
        let layout = layout_with(&["rev+limit"]);
        assert_eq!(find(&layout, "rev+limit"), None);
    }

    #[test]
    fn a_blank_name_is_refused_before_the_layout_is_consulted() {
        // Even constants literally named "" or " " are never flagged.
        let layout = layout_with(&["", " "]);
        assert_eq!(find(&layout, ""), None);
        assert_eq!(find(&layout, " "), None);
    }
}
