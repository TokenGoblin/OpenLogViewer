//! Ported from `CamProfiles.cs`. Owned by package `w1-calc-b`.
//!
//! Camshafts by what the engine is for, so the page can be used without a cam
//! card in hand. These are seat-to-seat durations: a cam card leads with
//! duration at 0.050 in lift, which runs 44 to 48 crank degrees shorter, and
//! typing that into a box that wants seat-to-seat shortens every length by
//! something like a fifth. Each entry therefore carries both.

use super::engine_families::{self, EngineFamily};

/// A camshaft as a catalogue describes it, together with the gas conditions the
/// engine it belongs on tends to run.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CamProfile {
    /// What to call it in a list.
    pub name: &'static str,
    /// Inlet valve open duration in crank degrees, seat to seat.
    pub intake_duration_deg: f64,
    /// The same for the exhaust valve.
    pub exhaust_duration_deg: f64,
    /// Inlet duration at 0.050 in lift, the number cam cards lead with.
    pub intake_at_fifty_deg: f64,
    /// The same for the exhaust valve.
    pub exhaust_at_fifty_deg: f64,
    /// Lobe separation angle, in cam degrees. Carried to work out the overlap.
    pub lobe_separation_deg: f64,
    /// Charge temperature in the runner this build tends to see.
    pub charge_celsius: f64,
    /// Mean gas temperature along the primary.
    pub exhaust_celsius: f64,
    /// What the engine is like to live with.
    pub note: &'static str,
}

impl CamProfile {
    /// True for the entry that stands for "whatever was typed in".
    pub fn is_custom(&self) -> bool {
        self.intake_duration_deg <= 0.0
    }

    /// Crank degrees both valves are open together: (DI + DE)/2 - 2*LSA.
    ///
    /// The single number that says most about what an engine is like to own:
    /// idle quality, manifold vacuum and exhaust scavenging all come from it.
    pub fn overlap_deg(&self) -> f64 {
        if self.is_custom() {
            f64::NAN
        } else {
            ((self.intake_duration_deg + self.exhaust_duration_deg) / 2.0) - (2.0 * self.lobe_separation_deg)
        }
    }
}

impl std::fmt::Display for CamProfile {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.name)
    }
}

/// The five worth offering, mildest first, then the custom entry.
///
/// The descriptions talk about brakes and idle rather than horsepower, because
/// that is what decides whether somebody can live with a cam.
pub static ALL: [CamProfile; 6] = [
    CamProfile {
        name: "Stock",
        intake_duration_deg: 252.0,
        exhaust_duration_deg: 262.0,
        intake_at_fifty_deg: 204.0,
        exhaust_at_fifty_deg: 214.0,
        lobe_separation_deg: 112.0,
        charge_celsius: 45.0,
        exhaust_celsius: 560.0,
        note: "OEM or a stock replacement \u{2014} smooth idle, full vacuum for the brakes, pulls from idle and works with the standard converter and gearing",
    },
    CamProfile {
        name: "Stock +",
        intake_duration_deg: 262.0,
        exhaust_duration_deg: 270.0,
        intake_at_fifty_deg: 218.0,
        exhaust_at_fifty_deg: 224.0,
        lobe_separation_deg: 110.0,
        charge_celsius: 40.0,
        exhaust_celsius: 580.0,
        note: "a mild street grind \u{2014} the first one you can hear, still idles cleanly and keeps enough vacuum for everything the car already has",
    },
    CamProfile {
        name: "Performance",
        intake_duration_deg: 274.0,
        exhaust_duration_deg: 286.0,
        intake_at_fifty_deg: 230.0,
        exhaust_at_fifty_deg: 236.0,
        lobe_separation_deg: 110.0,
        charge_celsius: 35.0,
        exhaust_celsius: 600.0,
        note: "street and strip \u{2014} a definite lope, wants a little more compression and converter, and the bottom of the rev range starts to go hollow",
    },
    CamProfile {
        name: "Performance +",
        intake_duration_deg: 288.0,
        exhaust_duration_deg: 300.0,
        intake_at_fifty_deg: 242.0,
        exhaust_at_fifty_deg: 248.0,
        lobe_separation_deg: 108.0,
        charge_celsius: 32.0,
        exhaust_celsius: 620.0,
        note: "serious street and strip \u{2014} poor idle vacuum, needs a high-stall converter and gearing to suit, and power arrives well off idle",
    },
    CamProfile {
        name: "Full race",
        intake_duration_deg: 310.0,
        exhaust_duration_deg: 318.0,
        intake_at_fifty_deg: 262.0,
        exhaust_at_fifty_deg: 268.0,
        lobe_separation_deg: 106.0,
        charge_celsius: 28.0,
        exhaust_celsius: 650.0,
        note: "no idle worth the name and no vacuum at all \u{2014} a narrow band up top, on an engine that is never asked to do anything else",
    },
    CamProfile {
        name: "From your cam card",
        intake_duration_deg: 0.0,
        exhaust_duration_deg: 0.0,
        intake_at_fifty_deg: 0.0,
        exhaust_at_fifty_deg: 0.0,
        lobe_separation_deg: 0.0,
        charge_celsius: 0.0,
        exhaust_celsius: 0.0,
        note: "your own figures \u{2014} use duration seat to seat, not at 0.050 in",
    },
];

/// The profile a pair of durations corresponds to, or the custom entry.
///
/// Matched on the durations alone and not on the temperatures, because the
/// durations are what make a cam that cam.
pub fn for_durations(intake_duration_deg: f64, exhaust_duration_deg: f64) -> CamProfile {
    if !(intake_duration_deg > 0.0 && exhaust_duration_deg > 0.0) {
        return ALL[ALL.len() - 1];
    }

    for cam in ALL {
        if cam.is_custom() {
            continue;
        }

        if (cam.intake_duration_deg - intake_duration_deg).abs() < 0.5
            && (cam.exhaust_duration_deg - exhaust_duration_deg).abs() < 0.5
        {
            return cam;
        }
    }

    ALL[ALL.len() - 1]
}

/// Where that profile sits in the list, for a combo box.
pub fn index_for(intake_duration_deg: f64, exhaust_duration_deg: f64) -> usize {
    let cam = for_durations(intake_duration_deg, exhaust_duration_deg);

    ALL.iter().position(|c| c.name == cam.name).unwrap_or(ALL.len() - 1)
}

/// Where a profile sits in the ladder, from 0 for stock to 4 for full race, or
/// -1 for somebody's own cam.
pub fn level_of(cam: &CamProfile) -> i32 {
    for (i, c) in ALL.iter().enumerate() {
        if c.name == cam.name && !c.is_custom() {
            return i as i32;
        }
    }

    -1
}

/// How much peak volumetric efficiency one step up the cam ladder is worth.
pub const POINTS_PER_CAM_LEVEL: f64 = 4.0;

/// The most a cam can add to a head, in points of volumetric efficiency: the
/// port is the limit, and a camshaft cannot make a small one flow more air.
pub const MOST_A_CAM_CAN_ADD: f64 = 13.0;

/// Peak volumetric efficiency for a head and a cam together.
///
/// The figure quoted for a family already assumes a particular cam, so this
/// counts the difference from that assumption rather than adding the cam twice.
pub fn volumetric_efficiency(family: &EngineFamily, cam: &CamProfile) -> f64 {
    if family.is_custom() || cam.is_custom() {
        return f64::NAN;
    }

    let level = level_of(cam);
    if level < 0 {
        return f64::NAN;
    }

    let moved = f64::from(level - family.implied_cam_level) * POINTS_PER_CAM_LEVEL;
    let ceiling = family.volumetric_efficiency + MOST_A_CAM_CAN_ADD;

    (family.volumetric_efficiency + moved).clamp(40.0, ceiling)
}

/// The family a volumetric efficiency corresponds to given the cam in front of
/// it, as an index into the families list (the custom entry if none fits).
///
/// Two heads can land on the same figure, so `preferred` (the selection already
/// showing) wins whenever it still fits.
pub fn family_index_for(ve_percent: f64, cam: &CamProfile, preferred: Option<usize>) -> usize {
    let families = &engine_families::ALL;

    let fits = |index: usize| {
        let ve = volumetric_efficiency(&families[index], cam);
        !ve.is_nan() && (ve - ve_percent).abs() < 0.5
    };

    if ve_percent > 0.0 && !cam.is_custom() {
        if let Some(p) = preferred.filter(|&p| p < families.len() && fits(p)) {
            return p;
        }

        for i in 0..families.len() {
            if fits(i) {
                return i;
            }
        }
    }

    families.len() - 1
}

/// What an overlap figure means for living with the engine.
pub fn overlap_verdict(overlap_deg: f64) -> &'static str {
    if overlap_deg.is_nan() {
        "\u{2014}"
    } else if overlap_deg < 20.0 {
        "idles like a standard car"
    } else if overlap_deg < 40.0 {
        "clean idle, keeps full vacuum"
    } else if overlap_deg < 55.0 {
        "just audible at idle, vacuum still fine"
    } else if overlap_deg < 70.0 {
        "a proper lope, vacuum getting thin"
    } else if overlap_deg < 90.0 {
        "rough idle, brake booster wants a pump"
    } else {
        "no usable idle or vacuum \u{2014} race only"
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_ladder_has_levels_and_a_custom_tail() {
        assert_eq!(0, level_of(&ALL[0]));
        assert_eq!(4, level_of(&ALL[4]));
        assert_eq!(-1, level_of(&ALL[5]));
        assert_eq!(5, index_for(1.0, 1.0));
        assert_eq!(2, index_for(274.2, 285.9));
    }

    #[test]
    fn overlap_and_ve_follow_the_documented_arithmetic() {
        // Stock: (252 + 262)/2 - 2*112 = 33.
        assert_eq!(33.0, ALL[0].overlap_deg());
        assert!(ALL[5].overlap_deg().is_nan());
        assert_eq!("clean idle, keeps full vacuum", overlap_verdict(33.0));

        let modern = engine_families::ALL[1];
        assert_eq!(85.0, volumetric_efficiency(&modern, &ALL[0]));
        assert_eq!(93.0, volumetric_efficiency(&modern, &ALL[2]));
        assert_eq!(98.0, volumetric_efficiency(&modern, &ALL[4]));
        assert_eq!(1, family_index_for(93.0, &ALL[2], None));
        // Two heads reach 105 with a full race grind; the preferred one wins.
        assert_eq!(2, family_index_for(105.0, &ALL[4], None));
        assert_eq!(4, family_index_for(105.0, &ALL[4], Some(4)));
    }
}
