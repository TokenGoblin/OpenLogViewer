//! Ported from `EngineRecipe.cs`. Owned by package `w1-calc-a`.
//!
//! One build, worked through end to end. Every calculator answers a question on its own; this
//! one asks them all in the order a person asks them and hands back a parts list. The value is
//! in their sharing one set of assumptions: one mixture and one fuel consumption run through
//! all of it, and the margins (injector duty, pump headroom, compressor flow to spare) are
//! separate and explicit.
//!
//! The peak torque speed earns its place: airflow at peak power says whether a compressor is
//! large enough; airflow at the torque peak says whether it is too large.

use super::engine_geometry;
use super::turbo_sizing::{self, TurboMatch};
use crate::base::tuning_math::{self as tm, Fuel, PumpChoice};
use crate::fmt;

/// The build being planned: what the engine is, and what is wanted of it.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RecipeSpec {
    pub litres: f64,
    pub cylinders: i32,
    /// What the engine is meant to make, at the crank.
    pub target_horsepower: f64,
    /// Where full boost is wanted by: the low end of the range the compressor works across.
    pub peak_torque_rpm: f64,
    /// Where the power figure is made, which is where the air peaks.
    pub peak_power_rpm: f64,
    pub fuel: Fuel,
    /// Mixture at full throttle.
    pub lambda: f64,
    /// Fuel consumption on petrol; the fuel's own is scaled from it.
    pub petrol_bsfc: f64,
    pub volumetric_efficiency: f64,
    /// Charge temperature in the manifold, after the intercooler.
    pub charge_celsius: f64,
    /// The most an injector should be asked to work at.
    pub injector_duty_limit: f64,
    /// Base fuel pressure, before boost is referenced onto it.
    pub rail_psi: f64,
    /// Flow a pump should have spare over what is burned.
    pub pump_headroom_percent: f64,
    pub barometric_kpa: f64,
    pub inlet_loss_kpa: f64,
    pub charge_loss_kpa: f64,
}

impl Default for RecipeSpec {
    fn default() -> Self {
        RecipeSpec {
            litres: 2.0,
            cylinders: 4,
            target_horsepower: 500.0,
            peak_torque_rpm: 3500.0,
            peak_power_rpm: 7000.0,
            fuel: Fuel::Petrol,
            lambda: 0.80,
            petrol_bsfc: tm::FULL_THROTTLE_BSFC,
            volumetric_efficiency: 97.0,
            charge_celsius: 45.0,
            injector_duty_limit: 85.0,
            rail_psi: 43.5,
            pump_headroom_percent: 20.0,
            barometric_kpa: tm::ATMOSPHERIC_KPA,
            inlet_loss_kpa: tm::TYPICAL_INLET_LOSS_KPA,
            charge_loss_kpa: tm::TYPICAL_CHARGE_LOSS_KPA,
        }
    }
}

/// Something about the plan worth saying out loud.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RecipeWarning {
    /// How much it matters: "note", "watch" or "stop".
    pub severity: &'static str,
    pub text: String,
}

/// The parts list, and the arithmetic behind it.
#[derive(Clone, Debug, PartialEq)]
pub struct Recipe {
    pub air_at_peak_power: f64,
    /// Air at the torque peak, which is the other end of the range.
    pub air_at_peak_torque: f64,
    pub afr: f64,
    pub bsfc: f64,
    pub manifold_kpa: f64,
    pub boost_kpa: f64,
    pub pressure_ratio: f64,
    pub turbos: Vec<TurboMatch>,
    pub injector_cc_each: f64,
    pub injector_lb_hr_each: f64,
    pub fuel_litres_per_hour: f64,
    pub pump_litres_per_hour: f64,
    pub rail_under_boost_psi: f64,
    pub pumps: Vec<PumpChoice>,
    pub mean_piston_speed: f64,
    pub specific_output: f64,
    pub warnings: Vec<RecipeWarning>,
}

/// The least of a compressor's rated flow it is comfortable passing. A rule of thumb that is
/// there to raise an eyebrow rather than to decide anything; the map decides.
pub const LOWEST_COMFORTABLE_FLOW_FRACTION: f64 = 0.25;

/// `Math.Max(a, b)`: NaN if either is NaN (`f64::max` would return the other).
fn math_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a > b {
        a
    } else {
        b
    }
}

pub fn build(spec: &RecipeSpec) -> Recipe {
    let afr = tm::afr_from_lambda(spec.lambda, spec.fuel);
    let bsfc = tm::suggested_bsfc(spec.fuel, spec.petrol_bsfc);

    // ----- the air -------------------------------------------------------------

    let air = turbo_sizing::air_for_horsepower(spec.target_horsepower, afr, bsfc);

    let manifold = turbo_sizing::manifold_kpa_for(
        air,
        spec.litres,
        spec.peak_power_rpm,
        spec.volumetric_efficiency,
        spec.charge_celsius,
    );

    let boost = manifold - spec.barometric_kpa;

    let compressor = tm::compressor_pressures(boost, spec.barometric_kpa, spec.inlet_loss_kpa, spec.charge_loss_kpa);

    // At the torque peak the manifold is at the same pressure and the engine is turning slower,
    // so it swallows less air in proportion. The same filling is assumed at both ends.
    let torque_air = if spec.peak_power_rpm > 0.0 {
        air * spec.peak_torque_rpm / spec.peak_power_rpm
    } else {
        f64::NAN
    };

    // ----- the fuel ------------------------------------------------------------

    let injector_lb_hr =
        tm::injector_pounds_per_hour(spec.target_horsepower, spec.cylinders, bsfc, spec.injector_duty_limit);

    let injector_cc = tm::cc_per_minute(injector_lb_hr, spec.fuel);

    let burned = tm::fuel_litres_per_hour(spec.target_horsepower, bsfc, spec.fuel);
    let pump = tm::pump_litres_per_hour(spec.target_horsepower, bsfc, spec.fuel, spec.pump_headroom_percent);

    // A manifold-referenced regulator holds the difference across the injector steady, so the
    // rail rises with the boost and the pump sees all of it.
    let rail_under_boost = spec.rail_psi + math_max(boost, 0.0) / tm::KPA_PER_PSI;

    // ----- what it says about the engine -----------------------------------------

    let stroke = stroke_for(spec);
    let piston_speed = engine_geometry::mean_piston_speed(stroke, spec.peak_power_rpm);

    Recipe {
        air_at_peak_power: air,
        air_at_peak_torque: torque_air,
        afr,
        bsfc,
        manifold_kpa: manifold,
        boost_kpa: boost,
        pressure_ratio: compressor.ratio,
        turbos: turbo_sizing::suggest(air, turbo_sizing::SENSIBLE_HEADROOM, 4),
        injector_cc_each: injector_cc,
        injector_lb_hr_each: injector_lb_hr,
        fuel_litres_per_hour: burned,
        pump_litres_per_hour: pump,
        rail_under_boost_psi: rail_under_boost,
        pumps: tm::suggest_pumps(pump, rail_under_boost, tm::needs_alcohol_safe_pump(spec.fuel)),
        mean_piston_speed: piston_speed,
        specific_output: if spec.litres > 0.0 {
            spec.target_horsepower / spec.litres
        } else {
            f64::NAN
        },
        warnings: check(spec, air, torque_air, boost, piston_speed),
    }
}

/// A stroke to judge piston speed by, where the build has not named one: taken as square, bore
/// equal to stroke, since the recipe asks for a displacement and a cylinder count and not a bore.
#[allow(clippy::neg_cmp_op_on_partial_ord)]
fn stroke_for(spec: &RecipeSpec) -> f64 {
    if !(spec.litres > 0.0) || spec.cylinders <= 0 {
        return f64::NAN;
    }

    let cylinder_cc = spec.litres * 1000.0 / f64::from(spec.cylinders);

    // A square cylinder of volume V has bore = stroke = cbrt(4V/pi).
    (4.0 * cylinder_cc * 1000.0 / std::f64::consts::PI).cbrt()
}

fn warning(severity: &'static str, text: String) -> RecipeWarning {
    RecipeWarning { severity, text }
}

/// The parts of the plan worth saying out loud, ordered by how much they matter and worded as
/// observations rather than refusals: it is somebody's engine and they may have reasons.
fn check(spec: &RecipeSpec, air: f64, torque_air: f64, boost: f64, piston_speed: f64) -> Vec<RecipeWarning> {
    let mut warnings: Vec<RecipeWarning> = Vec::new();

    if piston_speed > 25.0 {
        warnings.push(warning(
            "stop",
            format!(
                "{} m/s of mean piston speed at {} rpm is race-engine territory, where rods and \
                 rings are consumables. Either the speed comes down or the parts stop being \
                 production ones.",
                fmt::f64(piston_speed, "N1"),
                fmt::f64(spec.peak_power_rpm, "N0")
            ),
        ));
    } else if piston_speed > 22.0 {
        warnings.push(warning(
            "watch",
            format!(
                "{} m/s of mean piston speed is a performance redline. Production parts will do \
                 it; they will not do it indefinitely.",
                fmt::f64(piston_speed, "N1")
            ),
        ));
    }

    let boost_psi = boost / tm::KPA_PER_PSI;

    if boost_psi > 35.0 {
        warnings.push(warning(
            "stop",
            format!(
                "{} psi is a great deal of boost. Before buying anything, see whether more \
                 displacement, more engine speed or better filling gets there on less — every \
                 part of this list gets cheaper when the boost comes down.",
                fmt::f64(boost_psi, "N0")
            ),
        ));
    } else if boost_psi > 25.0 {
        warnings.push(warning(
            "watch",
            format!(
                "{} psi wants attention paid to the compression ratio, the fuel and the \
                 intercooler, not just to the turbocharger.",
                fmt::f64(boost_psi, "N0")
            ),
        ));
    }

    if boost <= 0.0 {
        warnings.push(warning(
            "note",
            "This target needs no boost at all — the engine can breathe it naturally. The \
             turbocharger below is what it would take if the filling were worse than assumed."
                .to_string(),
        ));
    }

    // The compressor being too large is the failure this tool exists to catch, and it is
    // invisible in a flow figure taken at peak power alone.
    if torque_air > 0.0 {
        for m in turbo_sizing::suggest(air, turbo_sizing::SENSIBLE_HEADROOM, 4)
            .into_iter()
            .take(1)
        {
            let fraction = torque_air / (m.turbo.max_flow_lb_per_minute() * f64::from(m.count));

            if fraction < LOWEST_COMFORTABLE_FLOW_FRACTION {
                warnings.push(warning(
                    "watch",
                    format!(
                        "At {} rpm this engine wants {} lb/min, which is {} of what the {} is \
                         rated for. That is near the left of its map, where it will be slow to \
                         come up and may surge. A smaller turbocharger, or two of them, would \
                         spool where you want it.",
                        fmt::f64(spec.peak_torque_rpm, "N0"),
                        fmt::f64(torque_air, "N0"),
                        fmt::f64(fraction, "P0"),
                        m.label()
                    ),
                ));
            }
        }
    }

    if spec.peak_torque_rpm >= spec.peak_power_rpm {
        warnings.push(warning(
            "note",
            "The torque peak is at or above the power peak, which no engine does. The spool \
             check below means nothing until those are the right way round."
                .to_string(),
        ));
    }

    let specific = if spec.litres > 0.0 {
        spec.target_horsepower / spec.litres
    } else {
        0.0
    };

    if specific > 250.0 {
        warnings.push(warning(
            "watch",
            format!(
                "{} hp per litre is a serious specific output. It is done, but not on standard \
                 internals and not for long on pump fuel.",
                fmt::f64(specific, "N0")
            ),
        ));
    }

    if spec.fuel == Fuel::Petrol && boost_psi > 20.0 {
        warnings.push(warning(
            "note",
            "Pump petrol at this boost is the limit worth thinking about. E85 buys knock margin \
             cheaply — see the Octane calculator — at the cost of about half again the fuel, \
             which the injectors and pump below already account for if you change the fuel here."
                .to_string(),
        ));
    }

    warnings
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::calc::turbo_sizing::air_for_horsepower;

    /// `Assert.Equal(expected, actual, digits)`: both rounded (ties to even) then compared.
    fn eq(expected: f64, actual: f64, digits: i32) -> bool {
        let scale = 10f64.powi(digits);
        (expected * scale).round_ties_even() == (actual * scale).round_ties_even()
    }

    fn in_range(v: f64, lo: f64, hi: f64) -> bool {
        v >= lo && v <= hi
    }

    /// A 2.0 litre four wanting 500 hp on petrol: the ordinary case.
    fn ordinary() -> RecipeSpec {
        RecipeSpec {
            litres: 2.0,
            cylinders: 4,
            target_horsepower: 500.0,
            peak_torque_rpm: 3500.0,
            peak_power_rpm: 7000.0,
            fuel: Fuel::Petrol,
            lambda: 0.80,
            volumetric_efficiency: 95.0,
            charge_celsius: 45.0,
            ..RecipeSpec::default()
        }
    }

    fn has(r: &Recipe, f: impl Fn(&RecipeWarning) -> bool) -> bool {
        r.warnings.iter().any(f)
    }

    // ----- everything rests on the same assumptions ---------------------------

    /// The whole point of doing this in one place.
    #[test]
    fn every_part_of_the_list_is_sized_on_the_same_fuel_consumption() {
        let recipe = build(&ordinary());

        let bsfc = recipe.bsfc;

        assert!(eq(
            air_for_horsepower(500.0, recipe.afr, bsfc),
            recipe.air_at_peak_power,
            6
        ));
        assert!(eq(
            tm::injector_pounds_per_hour(500.0, 4, bsfc, 85.0),
            recipe.injector_lb_hr_each,
            6
        ));
        assert!(eq(
            tm::fuel_litres_per_hour(500.0, bsfc, Fuel::Petrol),
            recipe.fuel_litres_per_hour,
            6
        ));
    }

    #[test]
    fn the_mixture_is_the_one_asked_for_on_the_fuel_chosen() {
        let recipe = build(&ordinary());

        assert!(eq(0.80 * tm::stoichiometric(Fuel::Petrol), recipe.afr, 6));
        assert!(eq(tm::FULL_THROTTLE_BSFC, recipe.bsfc, 6));
    }

    /// 500 hp from a 2.0 is a real, common target; the parts should be the parts such a car runs.
    #[test]
    fn an_ordinary_build_comes_out_with_ordinary_parts() {
        let recipe = build(&ordinary());

        assert!(in_range(recipe.air_at_peak_power, 45.0, 55.0)); // lb/min
        assert!(in_range(recipe.injector_cc_each, 700.0, 800.0)); // cc/min each
        assert!(in_range(recipe.pump_litres_per_hour, 170.0, 200.0)); // L/h

        // Thirty psi: 250 hp per litre is a great deal to ask of two litres, and the cars
        // that do it run about this.
        assert!(in_range(recipe.boost_kpa / tm::KPA_PER_PSI, 26.0, 34.0));
        assert!(in_range(recipe.pressure_ratio, 3.0, 3.7));

        assert!(!recipe.turbos.is_empty());
        assert!(!recipe.pumps.is_empty());
    }

    #[test]
    fn the_parts_suggested_can_actually_do_the_job() {
        let recipe = build(&ordinary());

        assert!(
            recipe
                .turbos
                .iter()
                .all(|t| t.turbo.max_flow_lb_per_minute() * f64::from(t.count) >= recipe.air_at_peak_power)
        );

        assert!(
            recipe
                .pumps
                .iter()
                .all(|p| p.delivered_litres_per_hour >= recipe.pump_litres_per_hour)
        );
    }

    // ----- the two engine speeds ----------------------------------------------

    #[test]
    fn the_torque_peak_sets_the_other_end_of_the_flow_range() {
        let recipe = build(&RecipeSpec {
            peak_torque_rpm: 3500.0,
            peak_power_rpm: 7000.0,
            ..ordinary()
        });

        assert!(eq(recipe.air_at_peak_power / 2.0, recipe.air_at_peak_torque, 6));
    }

    #[test]
    fn a_very_low_torque_peak_is_flagged_as_too_much_turbo_to_spool() {
        let recipe = build(&RecipeSpec {
            peak_torque_rpm: 1200.0,
            ..ordinary()
        });

        assert!(has(&recipe, |w| w.severity == "watch" && w.text.contains("left of its map")));
    }

    #[test]
    fn a_sensible_torque_peak_is_not_flagged() {
        let recipe = build(&ordinary());

        assert!(!has(&recipe, |w| w.text.contains("left of its map")));
    }

    #[test]
    fn an_impossible_order_of_peaks_is_pointed_out() {
        let recipe = build(&RecipeSpec {
            peak_torque_rpm: 7000.0,
            peak_power_rpm: 6000.0,
            ..ordinary()
        });

        assert!(has(&recipe, |w| w.text.contains("no engine does")));
    }

    // ----- changing the fuel changes the parts, coherently --------------------

    #[test]
    fn e85_needs_the_same_air_but_more_of_everything_fuel() {
        let petrol = build(&ordinary());
        let e85 = build(&RecipeSpec {
            fuel: Fuel::E85,
            ..ordinary()
        });

        assert!(in_range(e85.air_at_peak_power / petrol.air_at_peak_power, 0.98, 1.01));

        assert!(in_range(e85.injector_cc_each / petrol.injector_cc_each, 1.35, 1.55));
        assert!(in_range(
            e85.pump_litres_per_hour / petrol.pump_litres_per_hour,
            1.35,
            1.55
        ));
    }

    #[test]
    fn an_alcohol_only_gets_pumps_rated_for_it() {
        let e85 = build(&RecipeSpec {
            fuel: Fuel::E85,
            ..ordinary()
        });

        assert!(!e85.pumps.is_empty());
        assert!(e85.pumps.iter().all(|p| p.pump.alcohol_safe));
    }

    #[test]
    fn methanol_asks_for_far_more_fuel_and_barely_more_air() {
        let petrol = build(&ordinary());
        let methanol = build(&RecipeSpec {
            fuel: Fuel::Methanol,
            ..ordinary()
        });

        assert!(in_range(
            methanol.air_at_peak_power / petrol.air_at_peak_power,
            0.93,
            1.0
        ));
        assert!(methanol.injector_cc_each > petrol.injector_cc_each * 1.9);
    }

    // ----- how the build moves -------------------------------------------------

    #[test]
    fn more_power_moves_every_part_of_the_list() {
        let modest = build(&RecipeSpec {
            target_horsepower: 300.0,
            ..ordinary()
        });
        let wild = build(&RecipeSpec {
            target_horsepower: 700.0,
            ..ordinary()
        });

        assert!(wild.air_at_peak_power > modest.air_at_peak_power);
        assert!(wild.boost_kpa > modest.boost_kpa);
        assert!(wild.injector_cc_each > modest.injector_cc_each);
        assert!(wild.pump_litres_per_hour > modest.pump_litres_per_hour);
    }

    #[test]
    fn a_bigger_engine_wants_less_boost_and_the_same_fuel() {
        let small = build(&RecipeSpec {
            litres: 2.0,
            ..ordinary()
        });
        let large = build(&RecipeSpec {
            litres: 4.0,
            ..ordinary()
        });

        assert!(large.boost_kpa < small.boost_kpa);
        assert!(eq(small.air_at_peak_power, large.air_at_peak_power, 6));
        assert!(eq(small.injector_cc_each, large.injector_cc_each, 6));
    }

    #[test]
    fn a_target_an_engine_can_breathe_naturally_says_so() {
        let recipe = build(&RecipeSpec {
            litres: 8.0,
            target_horsepower: 300.0,
            ..ordinary()
        });

        assert!(recipe.boost_kpa <= 0.0);
        assert!(has(&recipe, |w| w.text.contains("no boost at all")));
    }

    /// A manifold-referenced regulator holds the difference across the injector steady, which
    /// means the pump sees the base pressure plus all of the boost.
    #[test]
    fn the_rail_rises_with_the_boost_so_the_pump_is_judged_against_it() {
        let recipe = build(&ordinary());

        assert!(recipe.rail_under_boost_psi > 43.5);
        assert!(eq(
            43.5 + (recipe.boost_kpa / tm::KPA_PER_PSI),
            recipe.rail_under_boost_psi,
            6
        ));
    }

    // ----- what it says about the engine itself -------------------------------

    #[test]
    fn a_ridiculous_engine_speed_is_stopped() {
        let recipe = build(&RecipeSpec {
            peak_power_rpm: 11_000.0,
            ..ordinary()
        });

        assert!(has(&recipe, |w| w.severity == "stop" && w.text.contains("piston speed")));
    }

    #[test]
    fn an_ordinary_engine_speed_is_not_stopped() {
        let recipe = build(&RecipeSpec {
            peak_power_rpm: 6500.0,
            ..ordinary()
        });

        assert!(!has(&recipe, |w| w.text.contains("piston speed")));
    }

    #[test]
    fn absurd_boost_is_stopped_and_ordinary_boost_is_not() {
        let absurd = build(&RecipeSpec {
            target_horsepower: 1_400.0,
            ..ordinary()
        });
        assert!(has(&absurd, |w| w.severity == "stop" && w.text.contains("boost")));

        let sane = build(&RecipeSpec {
            target_horsepower: 350.0,
            ..ordinary()
        });
        assert!(!has(&sane, |w| w.severity == "stop"));
    }

    #[test]
    fn specific_output_is_reported_and_flagged_when_it_is_heroic() {
        let ordinary_recipe = build(&ordinary());

        assert!(eq(250.0, ordinary_recipe.specific_output, 6));

        let heroic = build(&RecipeSpec {
            target_horsepower: 800.0,
            ..ordinary()
        });

        assert!(eq(400.0, heroic.specific_output, 6));
        assert!(has(&heroic, |w| w.text.contains("per litre")));
    }

    #[test]
    fn pump_petrol_at_high_boost_is_mentioned_and_e85_is_not() {
        let petrol = build(&RecipeSpec {
            target_horsepower: 650.0,
            ..ordinary()
        });

        assert!(has(&petrol, |w| w.text.contains("E85")));

        let e85 = build(&RecipeSpec {
            target_horsepower: 650.0,
            fuel: Fuel::E85,
            ..ordinary()
        });

        assert!(!has(&e85, |w| w.text.contains("E85 buys knock margin")));
    }

    #[test]
    fn every_warning_says_how_much_it_matters_and_why() {
        let recipe = build(&RecipeSpec {
            target_horsepower: 1_200.0,
            peak_power_rpm: 11_000.0,
            ..ordinary()
        });

        assert!(!recipe.warnings.is_empty());

        for w in &recipe.warnings {
            assert!(["note", "watch", "stop"].contains(&w.severity));
            // C# `Text.Length` counts UTF-16 units.
            assert!(
                w.text.encode_utf16().count() > 40,
                "a warning nobody can act on is noise"
            );
        }
    }

    #[test]
    fn nonsense_produces_nothing_rather_than_a_parts_list() {
        let recipe = build(&RecipeSpec {
            target_horsepower: 0.0,
            ..ordinary()
        });

        assert!(recipe.air_at_peak_power.is_nan());
        assert!(recipe.turbos.is_empty());
    }

    // ----- the margins ---------------------------------------------------------

    /// The two are bought in different units — a duty limit for injectors, headroom for a
    /// pump — so it is easy for one to end up with margin and the other without.
    #[test]
    fn both_the_injectors_and_the_pump_are_sized_with_margin_and_both_are_settable() {
        let tight = RecipeSpec {
            injector_duty_limit: 100.0,
            pump_headroom_percent: 0.0,
            ..ordinary()
        };

        let bare = build(&tight);
        let spared = build(&RecipeSpec {
            injector_duty_limit: 80.0,
            pump_headroom_percent: 25.0,
            ..tight
        });

        // A quarter more pump, and a quarter more injector: 100/80.
        assert!(eq(1.25, spared.pump_litres_per_hour / bare.pump_litres_per_hour, 6));
        assert!(eq(1.25, spared.injector_cc_each / bare.injector_cc_each, 6));
    }

    /// The margins are margins rather than baked-in fudge: turn both off and the parts come
    /// out at the bare requirement.
    #[test]
    fn with_no_margin_at_all_the_parts_are_exactly_what_is_burned() {
        let recipe = build(&RecipeSpec {
            injector_duty_limit: 100.0,
            pump_headroom_percent: 0.0,
            ..ordinary()
        });

        assert!(eq(recipe.fuel_litres_per_hour, recipe.pump_litres_per_hour, 6));

        // Four injectors at full duty carry the whole fuel flow between them.
        let total_lb_hr = recipe.injector_lb_hr_each * 4.0;

        assert!(eq(500.0 * recipe.bsfc, total_lb_hr, 6));
    }

    #[test]
    fn the_two_margins_are_independent_of_each_other() {
        let baseline = build(&ordinary());
        let pump_only = build(&RecipeSpec {
            pump_headroom_percent: 40.0,
            ..ordinary()
        });
        let injector_only = build(&RecipeSpec {
            injector_duty_limit: 70.0,
            ..ordinary()
        });

        assert!(eq(baseline.injector_cc_each, pump_only.injector_cc_each, 6));
        assert!(pump_only.pump_litres_per_hour > baseline.pump_litres_per_hour);

        assert!(eq(baseline.pump_litres_per_hour, injector_only.pump_litres_per_hour, 6));
        assert!(injector_only.injector_cc_each > baseline.injector_cc_each);
    }

    #[test]
    fn the_default_margins_are_close_enough_to_each_other_to_be_coherent() {
        let spec = RecipeSpec::default();

        let injector_spare = (100.0 / spec.injector_duty_limit) - 1.0;
        let pump_spare = spec.pump_headroom_percent / 100.0;

        assert!(in_range(injector_spare, 0.15, 0.20));
        assert!(in_range(pump_spare, 0.15, 0.25));
        assert!(in_range((injector_spare - pump_spare).abs(), 0.0, 0.06));
    }

    // ----- from EngineFamilyTests.cs (the only one of its tests that needs only the recipe) --

    /// Volumetric efficiency multiplies straight into the air, so the kind of engine chosen is
    /// worth as much as a fifth of the boost the build needs.
    #[test]
    fn choosing_a_family_moves_the_whole_recipe() {
        let spec = RecipeSpec {
            litres: 2.0,
            target_horsepower: 500.0,
            ..RecipeSpec::default()
        };

        let poor = build(&RecipeSpec {
            volumetric_efficiency: 75.0,
            ..spec
        });
        let good = build(&RecipeSpec {
            volumetric_efficiency: 105.0,
            ..spec
        });

        assert!(eq(poor.air_at_peak_power, good.air_at_peak_power, 6));
        assert!(poor.boost_kpa > good.boost_kpa);

        // Forty per cent more filling is forty per cent less manifold pressure needed.
        assert!(eq(105.0 / 75.0, poor.manifold_kpa / good.manifold_kpa, 3));
    }
}
