//! Ported from `OctaneBlend.cs`. Owned by package `w1-calc-a`.
//!
//! What blending alcohol into petrol does to its octane, and to how cold it runs the charge.
//!
//! Octane blends very nearly linearly by molecule, not by volume: ethanol's molar mass is 46
//! against petrol's 105, and it is denser, so ten per cent of the volume is twenty-one per cent
//! of the molecules. Convert to a mole fraction first and the "non-linear" curve straightens out
//! (Anderson and colleagues, SAE 2012-01-1274). Checked against published measurements: on an
//! 88 RON blendstock this gives 92.4 at E10, 94.3 at E15 and 98.7 at E30.
//!
//! The chart functions join lines with `"\n"`; the C# used `Environment.NewLine`, which is
//! `"\r\n"` only on Windows.

use crate::base::tuning_math::{self as tm, Fuel};
use crate::fmt;

/// What gets blended into petrol to raise its octane.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Blendstock {
    Ethanol,
    Methanol,
    E85,
}

impl Blendstock {
    /// Every blendstock, in declaration order (`Enum.GetValues<Blendstock>()`).
    pub const ALL: [Blendstock; 3] = [Blendstock::Ethanol, Blendstock::Methanol, Blendstock::E85];
}

/// What a blend came out at. `Default` is all zeros, which the C# returns for NaN inputs.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct OctaneResult {
    /// Research octane number, which is what most of the world quotes.
    pub ron: f64,
    /// Motor octane number, measured hotter and harder.
    pub mon: f64,
    /// The average of the two, which is what a US pump quotes.
    pub anti_knock_index: f64,
    /// Alcohol in the finished mixture, which is not the number on the jug when it held E85.
    pub ethanol_by_volume: f64,
    /// The same quantity by molecule, which is the one the octane actually follows.
    pub alcohol_mole_fraction: f64,
    /// Energy the fuel takes to evaporate.
    pub heat_of_vaporisation_kj_per_kg: f64,
    /// The same, per kilogram of air it evaporates into.
    pub cooling_kj_per_kg_air: f64,
}

/// Average molar mass of petrol (a distribution; 105 is what fits the published blends).
pub const PETROL_MOLAR_MASS: f64 = 105.0;
pub const ETHANOL_MOLAR_MASS: f64 = 46.068;
pub const METHANOL_MOLAR_MASS: f64 = 32.042;

// Neat octane numbers, which is what the molar model wants.
pub const ETHANOL_RON: f64 = 109.0;
pub const ETHANOL_MON: f64 = 90.0;
pub const METHANOL_RON: f64 = 109.0;
pub const METHANOL_MON: f64 = 89.0;

/// Heat of vaporisation in kilojoules per kilogram. Petrol is a range (305 to 350), so
/// everything downstream is worth about a tenth either way; the alcohols are better pinned.
pub const PETROL_HOV: f64 = 350.0;
pub const ETHANOL_HOV: f64 = 840.0;
pub const METHANOL_HOV: f64 = 1_100.0;

/// How much of E85 is actually ethanol, by volume.
pub const E85_ETHANOL_FRACTION: f64 = 0.85;

/// The base octanes a pump offers, as the rows of the chart.
pub const PUMP_GRADES: [f64; 6] = [85.0, 87.0, 89.0, 91.0, 93.0, 95.0];

/// The blend fractions worth showing, as the columns of it.
pub const CHART_FRACTIONS: [f64; 8] = [0.0, 0.10, 0.20, 0.30, 0.50, 0.70, 0.85, 1.00];

/// Sensitivity worth assuming: how far RON runs ahead of MON on pump petrol. A convention.
pub const TYPICAL_SENSITIVITY: f64 = 8.0;

pub fn name(stock: Blendstock) -> &'static str {
    match stock {
        Blendstock::Ethanol => "Ethanol (E100)",
        Blendstock::Methanol => "Methanol",
        Blendstock::E85 => "E85",
    }
}

/// The octane and the charge cooling of petrol blended with an alcohol.
///
/// `by_volume` is how much of the finished mixture is the blendstock, so blending E85 at 0.5
/// leaves a mixture that is 42.5 per cent ethanol, not 85.
pub fn blend(base_anti_knock_index: f64, sensitivity: f64, stock: Blendstock, by_volume: f64) -> OctaneResult {
    if base_anti_knock_index.is_nan() || sensitivity.is_nan() || by_volume.is_nan() {
        return OctaneResult::default();
    }

    let fraction = by_volume.clamp(0.0, 1.0);

    // E85 is a pre-made blend, so it is reduced to the alcohol it carries and the petrol that
    // came with it.
    let alcohol = if stock == Blendstock::E85 {
        fraction * E85_ETHANOL_FRACTION
    } else {
        fraction
    };
    let petrol = 1.0 - alcohol;

    let (molar_mass, ron, mon, hov, density, stoich) = if stock == Blendstock::Methanol {
        (
            METHANOL_MOLAR_MASS,
            METHANOL_RON,
            METHANOL_MON,
            METHANOL_HOV,
            tm::density(Fuel::Methanol),
            tm::stoichiometric(Fuel::Methanol),
        )
    } else {
        (
            ETHANOL_MOLAR_MASS,
            ETHANOL_RON,
            ETHANOL_MON,
            ETHANOL_HOV,
            tm::density(Fuel::Ethanol),
            tm::stoichiometric(Fuel::Ethanol),
        )
    };

    let petrol_density = tm::density(Fuel::Petrol);

    // ----- octane, by molecule rather than by volume ---------------------------------

    let alcohol_moles = alcohol * density / molar_mass;
    let petrol_moles = petrol * petrol_density / PETROL_MOLAR_MASS;
    let total_moles = alcohol_moles + petrol_moles;

    let x = if total_moles > 0.0 {
        alcohol_moles / total_moles
    } else {
        0.0
    };

    let base_ron = base_anti_knock_index + (sensitivity / 2.0);
    let base_mon = base_anti_knock_index - (sensitivity / 2.0);

    let blend_ron = (x * ron) + ((1.0 - x) * base_ron);
    let blend_mon = (x * mon) + ((1.0 - x) * base_mon);

    // ----- cooling, by mass -------------------------------------------------------------

    let alcohol_mass = alcohol * density;
    let petrol_mass = petrol * petrol_density;
    let total_mass = alcohol_mass + petrol_mass;

    let blend_hov = if total_mass > 0.0 {
        ((alcohol_mass * hov) + (petrol_mass * PETROL_HOV)) / total_mass
    } else {
        PETROL_HOV
    };

    let blend_stoich = if total_mass > 0.0 {
        ((alcohol_mass * stoich) + (petrol_mass * tm::stoichiometric(Fuel::Petrol))) / total_mass
    } else {
        tm::stoichiometric(Fuel::Petrol)
    };

    OctaneResult {
        ron: blend_ron,
        mon: blend_mon,
        anti_knock_index: (blend_ron + blend_mon) / 2.0,
        ethanol_by_volume: alcohol,
        alcohol_mole_fraction: x,
        heat_of_vaporisation_kj_per_kg: blend_hov,
        cooling_kj_per_kg_air: if blend_stoich > 0.0 {
            blend_hov / blend_stoich
        } else {
            f64::NAN
        },
    }
}

/// Cooling of neat petrol, as the thing every other figure is compared to.
pub fn petrol_cooling_kj_per_kg_air() -> f64 {
    PETROL_HOV / tm::stoichiometric(Fuel::Petrol)
}

/// `{value,width:fmt}`: right-aligned to `width` characters.
fn pad(text: String, width: usize) -> String {
    format!("{text:>width$}")
}

/// The chart: what each pump grade becomes at each blend fraction, as anti-knock index.
///
/// No sensitivity argument, and not by oversight: splitting a grade into RON and MON adds a
/// half-sensitivity to one and takes it off the other, and averaging cancels it exactly.
pub fn chart(stock: Blendstock) -> String {
    let mut lines: Vec<String> = Vec::new();

    let header: String = CHART_FRACTIONS
        .iter()
        .map(|f| format!("{}%", pad(fmt::f64(f * 100.0, "N0"), 7)))
        .collect();
    lines.push(format!("base{header}"));

    for grade in PUMP_GRADES {
        let row: String = CHART_FRACTIONS
            .iter()
            .map(|&f| {
                pad(
                    fmt::f64(blend(grade, TYPICAL_SENSITIVITY, stock, f).anti_knock_index, "N1"),
                    8,
                )
            })
            .collect();

        lines.push(format!("{}{row}", pad(fmt::f64(grade, "N0"), 4)));
    }

    lines.join("\n")
}

/// The other half of the chart: what the blend does to charge cooling, which does not depend
/// on the base grade and so is one row rather than six.
pub fn cooling_chart(stock: Blendstock) -> String {
    let row: Vec<OctaneResult> = CHART_FRACTIONS
        .iter()
        .map(|&f| blend(91.0, TYPICAL_SENSITIVITY, stock, f))
        .collect();

    let petrol = petrol_cooling_kj_per_kg_air();

    let header: String = CHART_FRACTIONS
        .iter()
        .map(|f| format!("{}%", pad(fmt::f64(f * 100.0, "N0"), 7)))
        .collect();
    let hov: String = row
        .iter()
        .map(|r| pad(fmt::f64(r.heat_of_vaporisation_kj_per_kg, "N0"), 8))
        .collect();
    let air: String = row
        .iter()
        .map(|r| pad(fmt::f64(r.cooling_kj_per_kg_air, "N0"), 8))
        .collect();
    let times: String = row
        .iter()
        .map(|r| pad(fmt::f64(r.cooling_kj_per_kg_air / petrol, "N2"), 8))
        .collect();

    [
        format!("     {header}"),
        format!("kJ/kg{hov}"),
        format!("/air {air}"),
        format!("\u{00d7}    {times}"),
    ]
    .join("\n")
}

#[cfg(test)]
mod tests {
    use super::*;

    /// `Assert.Equal(expected, actual, digits)`: both rounded (ties to even) then compared.
    fn eq(expected: f64, actual: f64, digits: i32) -> bool {
        let scale = 10f64.powi(digits);
        (expected * scale).round_ties_even() == (actual * scale).round_ties_even()
    }

    fn in_range(v: f64, lo: f64, hi: f64) -> bool {
        v >= lo && v <= hi
    }

    /// Measured research octane numbers for ethanol blended into an 88 RON blendstock.
    #[test]
    fn ethanol_blends_match_the_published_measurements() {
        for (by_volume, measured_ron) in [(0.10, 92.45), (0.15, 94.3), (0.30, 98.6)] {
            const SENSITIVITY: f64 = 8.0;
            const BASE_AKI: f64 = 88.0 - (SENSITIVITY / 2.0);

            let b = blend(BASE_AKI, SENSITIVITY, Blendstock::Ethanol, by_volume);

            assert!(eq(measured_ron, b.ron, 0));
            assert!(
                (b.ron - measured_ron).abs() < 0.3,
                "E{} came out at {:.2} against a measured {measured_ron}",
                by_volume * 100.0,
                b.ron
            );
        }
    }

    #[test]
    fn neat_alcohol_is_its_own_octane_whatever_it_was_blended_into() {
        for grade in PUMP_GRADES {
            let ethanol = blend(grade, 8.0, Blendstock::Ethanol, 1.0);

            assert!(eq(ETHANOL_RON, ethanol.ron, 6));
            assert!(eq(ETHANOL_MON, ethanol.mon, 6));
            assert!(eq(99.5, ethanol.anti_knock_index, 6));
        }
    }

    #[test]
    fn nothing_blended_in_leaves_the_fuel_alone_entirely() {
        let neat = blend(91.0, 8.0, Blendstock::Ethanol, 0.0);

        assert!(eq(91.0, neat.anti_knock_index, 6));
        assert!(eq(95.0, neat.ron, 6));
        assert!(eq(87.0, neat.mon, 6));
        assert!(eq(PETROL_HOV, neat.heat_of_vaporisation_kj_per_kg, 6));
        assert!(eq(0.0, neat.ethanol_by_volume, 6));
    }

    /// Ethanol is a small, dense molecule, so a splash of it by volume is a great deal of it
    /// by count, and octane follows the count.
    #[test]
    fn ten_per_cent_by_volume_is_twenty_per_cent_by_molecule() {
        let e10 = blend(91.0, 8.0, Blendstock::Ethanol, 0.10);

        assert!(in_range(e10.alcohol_mole_fraction, 0.20, 0.22));

        let m10 = blend(91.0, 8.0, Blendstock::Methanol, 0.10);

        assert!(m10.alcohol_mole_fraction > e10.alcohol_mole_fraction);
        assert!(in_range(m10.alcohol_mole_fraction, 0.26, 0.30));
    }

    #[test]
    fn the_first_splash_is_worth_far_more_than_the_last() {
        let at0 = blend(87.0, 8.0, Blendstock::Ethanol, 0.0).anti_knock_index;
        let at10 = blend(87.0, 8.0, Blendstock::Ethanol, 0.10).anti_knock_index;
        let at90 = blend(87.0, 8.0, Blendstock::Ethanol, 0.90).anti_knock_index;
        let at100 = blend(87.0, 8.0, Blendstock::Ethanol, 1.0).anti_knock_index;

        let first10 = at10 - at0;
        let last10 = at100 - at90;

        assert!(
            first10 > last10 * 4.0,
            "the first tenth was worth {first10:.1} points and the last {last10:.1}"
        );
    }

    #[test]
    fn octane_rises_with_every_drop_of_alcohol_and_with_every_grade_of_base() {
        for stock in Blendstock::ALL {
            let mut previous = f64::MIN;

            for f in CHART_FRACTIONS {
                let aki = blend(87.0, 8.0, stock, f).anti_knock_index;

                assert!(aki > previous, "{stock:?} went backwards at {f}");
                previous = aki;
            }
        }

        for f in [0.10, 0.30, 0.50, 0.85] {
            assert!(
                blend(93.0, 8.0, Blendstock::Ethanol, f).anti_knock_index
                    > blend(87.0, 8.0, Blendstock::Ethanol, f).anti_knock_index
            );
        }
    }

    /// Half a tank of E85 leaves a mixture that is 42.5 per cent ethanol.
    #[test]
    fn mixing_e85_with_pump_petrol_is_not_mixing_in_ethanol() {
        let half = blend(91.0, 8.0, Blendstock::E85, 0.50);

        assert!(eq(0.425, half.ethanol_by_volume, 6));
        assert!(eq(
            blend(91.0, 8.0, Blendstock::Ethanol, 0.425).anti_knock_index,
            half.anti_knock_index,
            6
        ));

        assert!(eq(0.85, blend(91.0, 8.0, Blendstock::E85, 1.0).ethanol_by_volume, 6));
    }

    #[test]
    fn half_a_tank_of_e85_in_ninety_one_is_about_ninety_seven_octane() {
        let aki = blend(91.0, 8.0, Blendstock::E85, 0.50).anti_knock_index;

        assert!(in_range(aki, 95.0, 99.0));
    }

    // ----- charge cooling ------------------------------------------------------

    #[test]
    fn ethanol_is_about_two_and_a_half_times_petrol_to_evaporate() {
        let petrol = blend(91.0, 8.0, Blendstock::Ethanol, 0.0).heat_of_vaporisation_kj_per_kg;
        let ethanol = blend(91.0, 8.0, Blendstock::Ethanol, 1.0).heat_of_vaporisation_kj_per_kg;
        let methanol = blend(91.0, 8.0, Blendstock::Methanol, 1.0).heat_of_vaporisation_kj_per_kg;

        assert!(eq(PETROL_HOV, petrol, 6));
        assert!(in_range(ethanol / petrol, 2.3, 2.5));
        assert!(in_range(methanol / petrol, 3.0, 3.3));
    }

    #[test]
    fn per_pound_of_air_the_alcohols_are_worth_far_more_than_that() {
        let petrol = petrol_cooling_kj_per_kg_air();
        let ethanol = blend(91.0, 8.0, Blendstock::Ethanol, 1.0).cooling_kj_per_kg_air;
        let methanol = blend(91.0, 8.0, Blendstock::Methanol, 1.0).cooling_kj_per_kg_air;

        assert!(in_range(petrol, 22.0, 26.0));
        assert!(in_range(ethanol / petrol, 3.7, 4.3));
        assert!(in_range(methanol / petrol, 6.5, 7.5));

        assert!(
            ethanol / petrol > blend(91.0, 8.0, Blendstock::Ethanol, 1.0).heat_of_vaporisation_kj_per_kg / PETROL_HOV
        );
    }

    #[test]
    fn e85_cools_the_charge_about_three_times_as_hard_as_petrol() {
        let e85 = blend(91.0, 8.0, Blendstock::E85, 1.0);

        assert!(in_range(
            e85.cooling_kj_per_kg_air / petrol_cooling_kj_per_kg_air(),
            3.0,
            3.6
        ));
    }

    #[test]
    fn cooling_rises_with_every_drop_too() {
        let mut previous = 0.0;

        for f in CHART_FRACTIONS {
            let cooling = blend(91.0, 8.0, Blendstock::Methanol, f).cooling_kj_per_kg_air;

            assert!(cooling > previous, "cooling went backwards at {f}");
            previous = cooling;
        }
    }

    // ----- sensitivity ---------------------------------------------------------

    #[test]
    fn sensitivity_splits_an_octane_number_into_the_two_it_is_made_of() {
        let b = blend(91.0, 10.0, Blendstock::Ethanol, 0.0);

        assert!(eq(96.0, b.ron, 6));
        assert!(eq(86.0, b.mon, 6));
        assert!(eq(91.0, b.anti_knock_index, 6));
    }

    /// Not an approximation: the half-sensitivity added to RON is taken off MON, and averaging
    /// cancels it exactly.
    #[test]
    fn the_base_sensitivity_cancels_out_of_the_blended_index_entirely() {
        for f in [0.10, 0.30, 0.50, 0.85, 1.0] {
            let narrow = blend(91.0, 6.0, Blendstock::Ethanol, f).anti_knock_index;
            let wide = blend(91.0, 12.0, Blendstock::Ethanol, f).anti_knock_index;

            assert!(eq(narrow, wide, 9));
        }

        // It does still decide the two numbers the index is made of.
        assert!(!eq(
            blend(91.0, 6.0, Blendstock::Ethanol, 0.30).ron,
            blend(91.0, 12.0, Blendstock::Ethanol, 0.30).ron,
            3
        ));
    }

    #[test]
    fn blending_alcohol_in_makes_the_fuel_more_sensitive_as_well_as_higher_octane() {
        let neat = blend(91.0, 8.0, Blendstock::Ethanol, 0.0);
        let petrol_sensitivity = neat.ron - neat.mon;

        let mut previous = petrol_sensitivity;

        for f in [0.10, 0.30, 0.50, 0.85, 1.0] {
            let b = blend(91.0, 8.0, Blendstock::Ethanol, f);
            let sensitivity = b.ron - b.mon;

            assert!(sensitivity >= previous, "sensitivity fell at {f}");
            previous = sensitivity;
        }

        assert!(eq(8.0, petrol_sensitivity, 6));
        assert!(eq(19.0, previous, 6));
    }

    // ----- the chart -----------------------------------------------------------

    #[test]
    fn the_chart_has_a_row_per_grade_and_agrees_with_the_arithmetic() {
        let text = chart(Blendstock::Ethanol);

        let lines: Vec<&str> = text.split('\n').collect();

        assert_eq!(PUMP_GRADES.len() + 1, lines.len());

        for grade in PUMP_GRADES {
            assert!(text.contains(&fmt::f64(grade, "N0")));
        }

        for grade in PUMP_GRADES {
            assert!(eq(
                grade,
                blend(grade, TYPICAL_SENSITIVITY, Blendstock::Ethanol, 0.0).anti_knock_index,
                6
            ));
        }
    }

    #[test]
    fn every_chart_cell_is_a_plausible_octane_number() {
        for stock in Blendstock::ALL {
            for grade in PUMP_GRADES {
                for f in CHART_FRACTIONS {
                    let aki = blend(grade, 8.0, stock, f).anti_knock_index;

                    assert!(in_range(aki, grade - 0.001, 110.0));
                }
            }
        }
    }

    #[test]
    fn nonsense_inputs_do_not_produce_an_octane_number() {
        assert_eq!(OctaneResult::default(), blend(f64::NAN, 8.0, Blendstock::Ethanol, 0.3));
        assert_eq!(OctaneResult::default(), blend(91.0, f64::NAN, Blendstock::Ethanol, 0.3));
        assert_eq!(OctaneResult::default(), blend(91.0, 8.0, Blendstock::Ethanol, f64::NAN));

        // And a fraction outside nought to one is clamped rather than believed.
        assert_eq!(
            blend(91.0, 8.0, Blendstock::Ethanol, 1.0),
            blend(91.0, 8.0, Blendstock::Ethanol, 3.0)
        );
    }
}
