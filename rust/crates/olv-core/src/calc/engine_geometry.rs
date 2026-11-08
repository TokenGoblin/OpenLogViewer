//! Ported from `EngineGeometry.cs`. Owned by package `w1-calc-a`.
//!
//! The engine as a set of dimensions: how big it is, how fast its pistons are going, and how
//! hard it squeezes. Bore and stroke decide displacement, stroke and engine speed decide piston
//! speed, and bore and stroke with what is left above the piston decide the compression.

pub const CUBIC_CENTIMETRES_PER_CUBIC_INCH: f64 = 16.387064;

pub const FEET_PER_METRE: f64 = 3.280839895013123;

/// The volume of a cylinder, in cc, from a diameter and a height in millimetres. Used for the
/// swept volume and for every part of the clearance volume as well.
pub fn cylinder_volume_cc(diameter_mm: f64, height_mm: f64) -> f64 {
    std::f64::consts::PI * diameter_mm * diameter_mm * height_mm / 4_000.0
}

/// What one cylinder sweeps.
pub fn swept_volume_cc(bore_mm: f64, stroke_mm: f64) -> f64 {
    if bore_mm > 0.0 && stroke_mm > 0.0 {
        cylinder_volume_cc(bore_mm, stroke_mm)
    } else {
        f64::NAN
    }
}

/// What the whole engine sweeps, which is the number on the badge.
pub fn displacement_cc(bore_mm: f64, stroke_mm: f64, cylinders: i32) -> f64 {
    if cylinders > 0 {
        swept_volume_cc(bore_mm, stroke_mm) * f64::from(cylinders)
    } else {
        f64::NAN
    }
}

pub fn cubic_inches(cc: f64) -> f64 {
    cc / CUBIC_CENTIMETRES_PER_CUBIC_INCH
}

/// Bore divided by stroke: over one is oversquare, under it undersquare.
pub fn bore_to_stroke(bore_mm: f64, stroke_mm: f64) -> f64 {
    if stroke_mm > 0.0 { bore_mm / stroke_mm } else { f64::NAN }
}

// ----- piston speed --------------------------------------------------------

/// Mean piston speed, in metres per second: two strokes per revolution, so twice the stroke
/// times the engine speed. A mean, not a maximum.
pub fn mean_piston_speed(stroke_mm: f64, rpm: f64) -> f64 {
    if stroke_mm > 0.0 && rpm > 0.0 {
        2.0 * stroke_mm * rpm / 60_000.0
    } else {
        f64::NAN
    }
}

/// The same in feet per minute, which is the unit the old rules of thumb use.
pub fn mean_piston_speed_feet_per_minute(stroke_mm: f64, rpm: f64) -> f64 {
    mean_piston_speed(stroke_mm, rpm) * FEET_PER_METRE * 60.0
}

/// What a piston speed means, in words. The bands are conventions, not thresholds anything
/// fails at.
pub fn piston_speed_verdict(metres_per_second: f64) -> &'static str {
    let v = metres_per_second;

    if v.is_nan() || v <= 0.0 {
        "\u{2014}"
    } else if v < 12.0 {
        "gentle, well short of any limit"
    } else if v < 18.0 {
        "ordinary for a road engine"
    } else if v < 22.0 {
        "a performance redline"
    } else if v < 25.0 {
        "race territory, parts are consumables"
    } else if v < 28.0 {
        "serious race engine, nothing production"
    } else {
        "beyond production practice"
    }
}

// ----- compression ---------------------------------------------------------

/// Everything still above the piston at top dead centre, in cc.
///
/// `gasket_thickness_mm` is the compressed thickness. `deck_clearance_mm` is how far the crown
/// sits below the deck at TDC, negative if the piston comes out of the hole. `piston_cc` is the
/// crown's own volume: positive for a dish, negative for a dome.
pub fn clearance_volume_cc(
    bore_mm: f64,
    chamber_cc: f64,
    gasket_bore_mm: f64,
    gasket_thickness_mm: f64,
    deck_clearance_mm: f64,
    piston_cc: f64,
) -> f64 {
    if bore_mm.is_nan() || bore_mm <= 0.0 {
        return f64::NAN;
    }

    let gasket = if gasket_bore_mm > 0.0 && gasket_thickness_mm > 0.0 {
        cylinder_volume_cc(gasket_bore_mm, gasket_thickness_mm)
    } else {
        0.0
    };

    // Signed on purpose: a piston out of the hole is a negative height and must come off the total.
    let deck = cylinder_volume_cc(bore_mm, deck_clearance_mm);

    chamber_cc + gasket + deck + piston_cc
}

/// Static compression ratio: everything the cylinder holds at the bottom, over what is left at
/// the top. Geometry only; says nothing about when the inlet valve shuts.
pub fn compression_ratio(swept_cc: f64, clearance_cc: f64) -> f64 {
    if clearance_cc > 0.0 && swept_cc > 0.0 {
        (swept_cc + clearance_cc) / clearance_cc
    } else {
        f64::NAN
    }
}

/// The clearance volume a wanted ratio needs.
pub fn clearance_for_ratio(swept_cc: f64, ratio: f64) -> f64 {
    if ratio > 1.0 && swept_cc > 0.0 {
        swept_cc / (ratio - 1.0)
    } else {
        f64::NAN
    }
}

/// Compression and boost taken together, as an index of how hard the cylinder is being worked:
/// static ratio times the pressure ratio at the manifold. An index, not a compression ratio.
///
/// C# default: `atmosphericKpa = AtmosphericKpa`.
pub fn boosted_compression_index(static_ratio: f64, manifold_absolute_kpa: f64, atmospheric_kpa: f64) -> f64 {
    if static_ratio > 0.0 && manifold_absolute_kpa > 0.0 && atmospheric_kpa > 0.0 {
        static_ratio * manifold_absolute_kpa / atmospheric_kpa
    } else {
        f64::NAN
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::base::tuning_math as tm;

    /// `Assert.Equal(expected, actual, digits)`: both rounded (ties to even) then compared.
    fn eq(expected: f64, actual: f64, digits: i32) -> bool {
        let scale = 10f64.powi(digits);
        (expected * scale).round_ties_even() == (actual * scale).round_ties_even()
    }

    fn in_range(v: f64, lo: f64, hi: f64) -> bool {
        v >= lo && v <= hi
    }

    // ----- displacement --------------------------------------------------------

    #[test]
    fn displacement_comes_out_at_the_number_on_the_badge() {
        // Bore, stroke, cylinders, and the badge the engine actually wears.
        for (bore, stroke, cylinders, expected_cc) in [
            (86.0, 86.0, 4, 1_998.0), // the archetypal square two litre
            (92.0, 86.0, 4, 2_287.0),
            (101.6, 88.4, 8, 5_733.0), // a 350 cubic inch V8
            (84.0, 90.0, 6, 2_993.0),
            (70.0, 64.0, 3, 739.0),
        ] {
            let cc = displacement_cc(bore, stroke, cylinders);

            assert!(eq(expected_cc, cc, 0), "{bore} x {stroke} x {cylinders} gave {cc}");
        }
    }

    #[test]
    fn a_small_block_chevrolet_is_three_hundred_and_fifty_cubic_inches() {
        let cc = displacement_cc(4.00 * 25.4, 3.48 * 25.4, 8);

        assert!(in_range(cubic_inches(cc), 349.0, 351.0));
        assert!(in_range(cc, 5_720.0, 5_745.0));
    }

    #[test]
    fn one_cylinder_is_the_whole_engine_divided_by_its_cylinders() {
        let swept = swept_volume_cc(86.0, 86.0);

        assert!(eq(swept * 4.0, displacement_cc(86.0, 86.0, 4), 6));
        assert!(eq(499.6, swept, 1));
    }

    #[test]
    fn bore_over_stroke_says_which_way_round_the_engine_is() {
        assert!(eq(1.0, bore_to_stroke(86.0, 86.0), 6));
        assert!(
            bore_to_stroke(92.0, 86.0) > 1.0,
            "a big bore and short stroke is oversquare"
        );
        assert!(
            bore_to_stroke(84.0, 90.0) < 1.0,
            "a small bore and long stroke is undersquare"
        );
    }

    // ----- piston speed --------------------------------------------------------

    /// Worked from the distance rather than from the formula, since the factor of two is the
    /// thing that goes missing.
    #[test]
    fn mean_piston_speed_counts_the_stroke_twice_per_revolution() {
        let metres_per_minute = 2.0 * 0.086 * 7_000.0;

        assert!(eq(metres_per_minute / 60.0, mean_piston_speed(86.0, 7_000.0), 6));
        assert!(eq(20.07, mean_piston_speed(86.0, 7_000.0), 2));
    }

    #[test]
    fn the_old_rule_of_four_thousand_feet_a_minute_is_twenty_metres_a_second() {
        let ft_min = mean_piston_speed_feet_per_minute(86.0, 7_000.0);

        assert!(eq(3_950.0, ft_min, 0));
        assert!(eq(20.3, 4_000.0 / (FEET_PER_METRE * 60.0), 1));
    }

    #[test]
    fn real_engines_land_in_the_bands_their_reputations_suggest() {
        // A diesel that never sees 4,500 rpm.
        assert!(in_range(mean_piston_speed(95.0, 4_500.0), 13.0, 15.0));
        // A road performance engine at its redline.
        assert!(in_range(mean_piston_speed(86.0, 7_000.0), 19.0, 21.0));
        // A racing V8: a short stroke spun very hard indeed.
        assert!(in_range(mean_piston_speed(82.5, 9_000.0), 24.0, 26.0));
        // And a Formula One V8 of the 20,000 rpm era, off the end of anything production.
        assert!(in_range(mean_piston_speed(39.7, 20_000.0), 25.0, 28.0));
    }

    #[test]
    fn shortening_the_stroke_buys_engine_speed_at_the_same_piston_speed() {
        let at_limit = mean_piston_speed(86.0, 7_000.0);

        assert!(eq(at_limit, mean_piston_speed(43.0, 14_000.0), 6));
    }

    #[test]
    fn every_piston_speed_gets_a_verdict_and_they_get_sterner_as_it_rises() {
        assert_eq!("\u{2014}", piston_speed_verdict(0.0));

        let mut verdicts = vec![
            piston_speed_verdict(10.0),
            piston_speed_verdict(16.0),
            piston_speed_verdict(20.0),
            piston_speed_verdict(24.0),
            piston_speed_verdict(30.0),
        ];
        let count = verdicts.len();

        assert!(verdicts.iter().all(|v| !v.trim().is_empty()));

        verdicts.sort_unstable();
        verdicts.dedup();
        assert_eq!(count, verdicts.len());
    }

    // ----- compression ---------------------------------------------------------

    /// 86 x 86, a 42 cc chamber, an 87 mm gasket a millimetre thick, half a millimetre of deck
    /// and a 5 cc dish: a normal set of parts, near ten to one.
    #[test]
    fn an_ordinary_road_engine_comes_out_at_an_ordinary_compression_ratio() {
        let swept = swept_volume_cc(86.0, 86.0);
        let clearance = clearance_volume_cc(86.0, 42.0, 87.0, 1.0, 0.5, 5.0);

        let ratio = compression_ratio(swept, clearance);

        assert!(in_range(clearance, 54.0, 57.0));
        assert!(in_range(ratio, 9.5, 10.5));
    }

    #[test]
    fn a_dome_raises_the_ratio_and_a_dish_lowers_it() {
        let swept = swept_volume_cc(86.0, 86.0);

        let clearance = |piston_cc: f64| clearance_volume_cc(86.0, 42.0, 87.0, 1.0, 0.5, piston_cc);

        let dished = compression_ratio(swept, clearance(5.0));
        let flat = compression_ratio(swept, clearance(0.0));
        let domed = compression_ratio(swept, clearance(-5.0));

        assert!(domed > flat, "a dome fills the chamber and raises the ratio");
        assert!(flat > dished, "a dish adds space and lowers it");
    }

    /// The sign that gets typed wrong: negative deck clearance means the crown comes past the
    /// deck at top dead centre, which raises compression.
    #[test]
    fn a_piston_out_of_the_hole_takes_volume_away_rather_than_adding_it() {
        let ratio = |deck: f64| {
            compression_ratio(
                swept_volume_cc(86.0, 86.0),
                clearance_volume_cc(86.0, 42.0, 87.0, 1.0, deck, 5.0),
            )
        };

        assert!(ratio(-0.5) > ratio(0.0), "out of the hole should raise it");
        assert!(ratio(0.0) > ratio(0.5), "down the hole should lower it");

        assert!(eq(2.9, cylinder_volume_cc(86.0, 0.5), 1));
    }

    #[test]
    fn a_thicker_gasket_lowers_compression_by_its_own_volume() {
        let thin = clearance_volume_cc(86.0, 42.0, 87.0, 0.8, 0.5, 5.0);
        let thick = clearance_volume_cc(86.0, 42.0, 87.0, 1.8, 0.5, 5.0);

        assert!(eq(cylinder_volume_cc(87.0, 1.0), thick - thin, 6));
        assert!(eq(5.9, thick - thin, 1));
    }

    #[test]
    fn the_ratio_and_the_clearance_it_needs_are_each_others_inverse() {
        let swept = swept_volume_cc(86.0, 86.0);

        for ratio in [8.5, 10.0, 11.5, 14.0] {
            let clearance = clearance_for_ratio(swept, ratio);

            assert!(eq(ratio, compression_ratio(swept, clearance), 6));
        }
    }

    #[test]
    fn a_ratio_is_refused_rather_than_invented_from_nonsense() {
        let swept = swept_volume_cc(86.0, 86.0);

        // No clearance volume is not infinite compression, it is a typing error.
        assert!(compression_ratio(swept, 0.0).is_nan());
        assert!(compression_ratio(swept, -3.0).is_nan());
        assert!(compression_ratio(0.0, 50.0).is_nan());

        assert!(clearance_for_ratio(swept, 1.0).is_nan());
        assert!(swept_volume_cc(0.0, 86.0).is_nan());
        assert!(displacement_cc(86.0, 86.0, 0).is_nan());
        assert!(mean_piston_speed(86.0, 0.0).is_nan());
    }

    // ----- compression against boost -------------------------------------------

    /// Fifteen psi is a pressure ratio just over two, so it roughly doubles the figure.
    #[test]
    fn ten_to_one_on_fifteen_psi_indexes_like_twenty_to_one_without() {
        let map = tm::absolute_from_gauge(15.0 * tm::KPA_PER_PSI, tm::ATMOSPHERIC_KPA);

        let index = boosted_compression_index(10.0, map, tm::ATMOSPHERIC_KPA);

        assert!(in_range(index, 19.0, 21.0));

        assert!(eq(
            10.0,
            boosted_compression_index(10.0, tm::ATMOSPHERIC_KPA, tm::ATMOSPHERIC_KPA),
            6
        ));
    }

    #[test]
    fn less_compression_buys_boost_and_the_index_says_how_much() {
        let lower = boosted_compression_index(
            8.5,
            tm::absolute_from_gauge(20.0 * tm::KPA_PER_PSI, tm::ATMOSPHERIC_KPA),
            tm::ATMOSPHERIC_KPA,
        );

        let higher = boosted_compression_index(
            10.5,
            tm::absolute_from_gauge(13.0 * tm::KPA_PER_PSI, tm::ATMOSPHERIC_KPA),
            tm::ATMOSPHERIC_KPA,
        );

        assert!(in_range((lower - higher).abs(), 0.0, 1.5));
    }

    #[test]
    fn the_index_follows_altitude_the_way_the_engine_does() {
        let high = tm::barometric_kpa(5_280.0 * tm::METRES_PER_FOOT);

        let at_sea = boosted_compression_index(10.0, tm::ATMOSPHERIC_KPA, tm::ATMOSPHERIC_KPA);
        let up_high = boosted_compression_index(10.0, high, tm::ATMOSPHERIC_KPA);

        assert!(up_high < at_sea);
    }
}
