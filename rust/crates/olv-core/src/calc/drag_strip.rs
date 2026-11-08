//! Ported from `DragStrip.cs`. Owned by package `w1-calc-b`.
//!
//! Elapsed time and trap speed against power and weight.
//!
//! These are correlations fitted to thousands of real runs, not physics. There
//! is no term for traction, launch, gearing, the air, or the driver. Trap speed
//! is very nearly a measure of power to weight; elapsed time carries the launch
//! in it for good. Read power from the trap, then ask what time that trap ought
//! to have come with: if the car ran slower, the difference is the launch.

/// One of the published power-to-weight correlations.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DragFormula {
    /// Whose it is.
    pub name: &'static str,
    /// Multiplies the cube root of weight over power.
    pub et_constant: f64,
    /// Multiplies the cube root of power over weight.
    pub mph_constant: f64,
    /// What sort of run it assumes.
    pub note: &'static str,
}

impl std::fmt::Display for DragFormula {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.name)
    }
}

/// What a timeslip says about the car that made it.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SlipReading {
    /// Power the trap speed implies.
    pub power_from_trap: f64,
    /// Power the elapsed time implies.
    pub power_from_et: f64,
    /// The time that trap speed should have come with.
    pub et_the_trap_deserved: f64,
    /// Seconds the run lost against the time its trap speed deserved. Positive
    /// means the car went slower than its own speed says it should have.
    pub launch_cost: f64,
}

/// The three published correlations, quickest first.
///
/// They differ by about eight per cent in time and four in speed. Hale
/// describes a run that hooked up; Huntington one that did not much.
pub const FORMULAS: [DragFormula; 3] = [
    DragFormula {
        name: "Hale",
        et_constant: 5.825,
        mph_constant: 234.0,
        note: "a run that hooked up \u{2014} sticky tyres and a launch that worked",
    },
    DragFormula {
        name: "Fox",
        et_constant: 6.269,
        mph_constant: 230.0,
        note: "between the two, and the usual middle answer",
    },
    DragFormula {
        name: "Huntington",
        et_constant: 6.290,
        mph_constant: 224.0,
        note: "the oldest and most forgiving \u{2014} where a street car on street tyres lands",
    },
];

/// The middle formula, for anyone who has no reason to prefer one.
pub const DEFAULT: DragFormula = FORMULAS[1];

/// How much longer the quarter takes than the eighth.
///
/// Published as anything from 1.55 to 1.57. Not the 1.414 constant acceleration
/// would give: the second half is covered with less acceleration left.
pub const QUARTER_OVER_EIGHTH_ET: f64 = 1.5657;

/// How much faster the car is going at the quarter than at the eighth.
pub const QUARTER_OVER_EIGHTH_MPH: f64 = 1.27;

/// What a launch is worth saying about: a tenth either way is the formula's own
/// scatter; past about three tenths the launch is the first thing to fix.
pub const LAUNCH_WORTH_MENTIONING: f64 = 0.30;

fn usable(horsepower: f64, weight_lb: f64) -> bool {
    horsepower > 0.0 && weight_lb > 0.0
}

/// Elapsed time over the quarter, in seconds.
pub fn quarter_et(horsepower: f64, weight_lb: f64, formula: DragFormula) -> f64 {
    if usable(horsepower, weight_lb) {
        formula.et_constant * (weight_lb / horsepower).cbrt()
    } else {
        f64::NAN
    }
}

/// Speed at the far end of the quarter, in miles per hour.
pub fn quarter_mph(horsepower: f64, weight_lb: f64, formula: DragFormula) -> f64 {
    if usable(horsepower, weight_lb) {
        formula.mph_constant * (horsepower / weight_lb).cbrt()
    } else {
        f64::NAN
    }
}

/// Elapsed time over the eighth, in seconds.
pub fn eighth_et(horsepower: f64, weight_lb: f64, formula: DragFormula) -> f64 {
    quarter_et(horsepower, weight_lb, formula) / QUARTER_OVER_EIGHTH_ET
}

/// Speed at the far end of the eighth, in miles per hour.
pub fn eighth_mph(horsepower: f64, weight_lb: f64, formula: DragFormula) -> f64 {
    quarter_mph(horsepower, weight_lb, formula) / QUARTER_OVER_EIGHTH_MPH
}

/// The power a trap speed implies: the reliable direction.
pub fn horsepower_from_trap(mph: f64, weight_lb: f64, formula: DragFormula) -> f64 {
    if mph > 0.0 && weight_lb > 0.0 && formula.mph_constant > 0.0 {
        weight_lb * (mph / formula.mph_constant).powf(3.0)
    } else {
        f64::NAN
    }
}

/// The power an elapsed time implies. Much less to be trusted than the trap: a
/// time carries the launch in it.
pub fn horsepower_from_et(seconds: f64, weight_lb: f64, formula: DragFormula) -> f64 {
    if seconds > 0.0 && weight_lb > 0.0 {
        weight_lb * (formula.et_constant / seconds).powf(3.0)
    } else {
        f64::NAN
    }
}

/// What a timeslip says, reading the trap for power and the time for the launch.
pub fn read(trap_mph: f64, seconds: f64, weight_lb: f64, formula: DragFormula) -> SlipReading {
    let from_trap = horsepower_from_trap(trap_mph, weight_lb, formula);
    let from_et = horsepower_from_et(seconds, weight_lb, formula);

    let deserved = quarter_et(from_trap, weight_lb, formula);

    SlipReading {
        power_from_trap: from_trap,
        power_from_et: from_et,
        et_the_trap_deserved: deserved,
        launch_cost: if deserved.is_nan() || seconds <= 0.0 {
            f64::NAN
        } else {
            seconds - deserved
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(expected: f64, actual: f64, digits: i32) {
        let tol = 0.5 * 10f64.powi(-digits);
        assert!(
            (expected - actual).abs() <= tol,
            "expected {expected}, got {actual} (to {digits} digits)"
        );
    }

    fn in_range(v: f64, lo: f64, hi: f64) {
        assert!(v >= lo && v <= hi, "{v} not in {lo}..={hi}");
    }

    fn named(name: &str) -> DragFormula {
        *FORMULAS.iter().find(|f| f.name == name).unwrap()
    }

    #[test]
    fn the_arithmetic_is_checkable_by_hand_at_eight_pounds_per_horsepower() {
        // 3,200 lb and 400 hp is eight to one, whose cube root is two.
        let hale = named("Hale");
        close(5.825 * 2.0, quarter_et(400.0, 3_200.0, hale), 9);
        close(234.0 * 0.5, quarter_mph(400.0, 3_200.0, hale), 9);
        close(11.65, quarter_et(400.0, 3_200.0, hale), 2);
        close(117.0, quarter_mph(400.0, 3_200.0, hale), 0);
    }

    #[test]
    fn the_three_formulas_bracket_each_other_rather_than_disagree() {
        let hale = named("Hale");
        let hunt = named("Huntington");
        for (hp, lb) in [(300.0, 3_500.0), (500.0, 3_000.0), (1_000.0, 2_800.0)] {
            let quick = quarter_et(hp, lb, hale);
            let slow = quarter_et(hp, lb, hunt);
            assert!(quick < slow, "Hale should be the quicker of the two");
            in_range(slow / quick, 1.05, 1.10);
            assert!(quarter_mph(hp, lb, hale) > quarter_mph(hp, lb, hunt));
        }
    }

    #[test]
    fn real_cars_land_where_real_cars_land() {
        let cases = [
            (3_600.0, 460.0, 12.0, 14.0, 105.0, 118.0),
            (3_200.0, 300.0, 13.0, 15.5, 95.0, 108.0),
            (2_800.0, 700.0, 9.0, 11.0, 130.0, 150.0),
        ];
        for (weight, hp, et_lo, et_hi, mph_lo, mph_hi) in cases {
            in_range(quarter_et(hp, weight, DEFAULT), et_lo, et_hi);
            in_range(quarter_mph(hp, weight, DEFAULT), mph_lo, mph_hi);
        }
    }

    #[test]
    fn more_power_is_quicker_and_faster_and_more_weight_is_neither() {
        let f = DEFAULT;
        assert!(quarter_et(600.0, 3_000.0, f) < quarter_et(300.0, 3_000.0, f));
        assert!(quarter_mph(600.0, 3_000.0, f) > quarter_mph(300.0, 3_000.0, f));
        assert!(quarter_et(400.0, 4_000.0, f) > quarter_et(400.0, 3_000.0, f));
        assert!(quarter_mph(400.0, 4_000.0, f) < quarter_mph(400.0, 3_000.0, f));
    }

    #[test]
    fn only_the_ratio_of_power_to_weight_matters() {
        let f = DEFAULT;
        close(quarter_et(400.0, 3_200.0, f), quarter_et(800.0, 6_400.0, f), 9);
        close(quarter_mph(400.0, 3_200.0, f), quarter_mph(800.0, 6_400.0, f), 9);
    }

    #[test]
    fn the_eighth_is_about_sixty_five_per_cent_of_the_quarter() {
        let f = DEFAULT;
        let quarter = quarter_et(400.0, 3_200.0, f);
        let eighth = eighth_et(400.0, 3_200.0, f);
        close(quarter / QUARTER_OVER_EIGHTH_ET, eighth, 9);
        in_range(quarter / eighth, 1.55, 1.57);
    }

    #[test]
    fn the_eighth_is_not_half_the_quarter_nor_the_square_root_of_two() {
        // Constant acceleration would put the ratio at 1.414.
        assert!(QUARTER_OVER_EIGHTH_ET > 2f64.sqrt());
        in_range(QUARTER_OVER_EIGHTH_ET - 2f64.sqrt(), 0.10, 0.20);
    }

    #[test]
    fn an_eighth_mile_car_lands_where_an_eighth_mile_car_lands() {
        in_range(eighth_et(400.0, 3_200.0, DEFAULT), 7.5, 8.5);
        in_range(eighth_mph(400.0, 3_200.0, DEFAULT), 85.0, 95.0);
    }

    #[test]
    fn power_and_trap_speed_invert_each_other() {
        for formula in FORMULAS {
            let mph = quarter_mph(450.0, 3_400.0, formula);
            close(450.0, horsepower_from_trap(mph, 3_400.0, formula), 6);
            let et = quarter_et(450.0, 3_400.0, formula);
            close(450.0, horsepower_from_et(et, 3_400.0, formula), 6);
        }
    }

    #[test]
    fn a_perfect_run_is_read_as_costing_nothing() {
        let f = DEFAULT;
        let mph = quarter_mph(500.0, 3_300.0, f);
        let et = quarter_et(500.0, 3_300.0, f);
        let reading = read(mph, et, 3_300.0, f);
        close(500.0, reading.power_from_trap, 6);
        close(500.0, reading.power_from_et, 6);
        close(0.0, reading.launch_cost, 6);
    }

    #[test]
    fn a_spun_launch_is_read_as_time_lost_rather_than_power_missing() {
        let f = DEFAULT;
        let mph = quarter_mph(500.0, 3_300.0, f);
        let deserved = quarter_et(500.0, 3_300.0, f);
        let reading = read(mph, deserved + 0.5, 3_300.0, f);
        close(500.0, reading.power_from_trap, 6);
        assert!(
            reading.power_from_et < 460.0,
            "reading power from a spun run's time understates it badly"
        );
        close(0.5, reading.launch_cost, 6);
        assert!(reading.launch_cost > LAUNCH_WORTH_MENTIONING);
    }

    #[test]
    fn a_good_launch_shows_as_time_gained_rather_than_power_found() {
        let f = DEFAULT;
        let mph = quarter_mph(500.0, 3_300.0, f);
        let deserved = quarter_et(500.0, 3_300.0, f);
        let reading = read(mph, deserved - 0.4, 3_300.0, f);
        close(500.0, reading.power_from_trap, 6);
        assert!(reading.power_from_et > 500.0);
        close(-0.4, reading.launch_cost, 6);
    }

    #[test]
    fn trap_speed_is_the_less_sensitive_of_the_two_to_power() {
        let f = DEFAULT;
        let ratio = quarter_mph(550.0, 3_300.0, f) / quarter_mph(500.0, 3_300.0, f);
        in_range(ratio, 1.02, 1.04);
        close(1.1f64.cbrt(), ratio, 6);
    }

    #[test]
    fn nonsense_is_not_a_quarter_mile() {
        let f = DEFAULT;
        assert!(quarter_et(0.0, 3_200.0, f).is_nan());
        assert!(quarter_et(400.0, 0.0, f).is_nan());
        assert!(quarter_mph(-100.0, 3_200.0, f).is_nan());
        assert!(horsepower_from_trap(0.0, 3_200.0, f).is_nan());
        assert!(horsepower_from_et(0.0, 3_200.0, f).is_nan());
    }

    #[test]
    fn every_formula_is_named_and_described() {
        assert_eq!(3, FORMULAS.len());
        for formula in FORMULAS {
            assert!(!formula.name.trim().is_empty());
            assert!(!formula.note.trim().is_empty());
            in_range(formula.et_constant, 5.0, 7.0);
            in_range(formula.mph_constant, 200.0, 250.0);
        }
        assert!(FORMULAS.contains(&DEFAULT));
    }
}
