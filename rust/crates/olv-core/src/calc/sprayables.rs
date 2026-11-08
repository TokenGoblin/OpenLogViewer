//! Ported from `Sprayables.cs`. Owned by package `w1-calc-a`.
//!
//! A liquid sprayed into the charge to cool it, and what that costs in flow. The arithmetic is a
//! balance: the heat the air has to lose equals the heat the liquid takes turning into vapour.
//! Blends are combined by mass fraction, which is why a fifty-fifty by volume is not
//! fifty-fifty by weight. The C# `ChemicalIntercooling` static class is the free functions here.

use super::intercooling::{CP_AIR, heat_load_btu_per_min};

/// A liquid sprayed into the charge to cool it, and what it is made of.
#[derive(Clone, Debug, PartialEq)]
pub struct Sprayable {
    pub name: String,
    /// Heat absorbed turning one pound of it from liquid to vapour.
    pub latent_btu_per_lb: f64,
    /// What a cc weighs, which turns a nozzle rating (cc/min) into a mass flow.
    pub density_g_per_cc: f64,
    /// Liquid specific heat, for the sensible heating on the way up.
    pub specific_heat_btu_per_lb_f: f64,
    /// The share by mass that burns. Water contributes none.
    pub combustible_fraction: f64,
    /// Energy in the combustible part, for working out what it displaces.
    pub fuel_lhv_btu_per_lb: f64,
    pub note: String,
}

impl Sprayable {
    /// Grams per cc is the same number as kilograms per litre; pounds per cc for the arithmetic.
    pub fn lb_per_cc(&self) -> f64 {
        self.density_g_per_cc / 453.59237
    }

    /// A nozzle's cc/min rating as a mass flow.
    pub fn lb_per_min_from_cc_per_min(&self, cc_per_min: f64) -> f64 {
        cc_per_min * self.lb_per_cc()
    }

    /// And back, because nozzles are bought in cc/min.
    pub fn cc_per_min_from_lb_per_min(&self, lb_per_min: f64) -> f64 {
        if self.lb_per_cc() > 0.0 {
            lb_per_min / self.lb_per_cc()
        } else {
            f64::NAN
        }
    }

    /// Cooling from one pound of it, including the sensible heating on the way to vaporising.
    /// For water the sensible part is worth another 12 to 15 per cent.
    pub fn cooling_btu_per_lb(&self, liquid_f: f64, charge_f: f64) -> f64 {
        let sensible = if charge_f > liquid_f {
            self.specific_heat_btu_per_lb_f * (charge_f - liquid_f)
        } else {
            0.0
        };

        self.latent_btu_per_lb + sensible
    }
}

/// BTU per pound for one kJ per kilogram.
pub const BTU_PER_LB_PER_KJ_PER_KG: f64 = 0.429923;

/// Water: the best coolant of the lot, and it does not burn.
pub fn water() -> Sprayable {
    Sprayable {
        name: "100% water".to_string(),
        latent_btu_per_lb: 971.6,
        density_g_per_cc: 1.000,
        specific_heat_btu_per_lb_f: 1.00,
        combustible_fraction: 0.0,
        fuel_lhv_btu_per_lb: 0.0,
        note: "Twice methanol's cooling per pound and no fuel value at all, so a big spray \
               does not enrich anything — which cuts both ways: it cools best and it is the \
               one that leans the mixture if you were relying on the spray. Freezes, so it \
               is a poor choice anywhere that gets cold, and needs something in it to stop \
               the pump corroding."
            .to_string(),
    }
}

pub fn methanol() -> Sprayable {
    Sprayable {
        name: "100% methanol".to_string(),
        latent_btu_per_lb: 472.9,
        density_g_per_cc: 0.792,
        specific_heat_btu_per_lb_f: 0.60,
        combustible_fraction: 1.0,
        fuel_lhv_btu_per_lb: 8_640.0,
        note: "Half water's cooling per pound, and it burns — which is the point. At full \
               strength the spray is a second fuel supply and has to be accounted for in \
               the tune, or the engine runs rich with it and lean the moment it stops. \
               Does not freeze. Attacks some plastics and rubbers."
            .to_string(),
    }
}

/// E85 as a sprayable, which is a different thing from E85 in the tank: almost all fuel and
/// only some coolant, 332 BTU per pound against water's 972.
pub fn e85() -> Sprayable {
    Sprayable {
        name: "E85".to_string(),
        latent_btu_per_lb: 332.0,
        density_g_per_cc: 0.7825,
        specific_heat_btu_per_lb_f: 0.58,
        combustible_fraction: 1.0,
        fuel_lhv_btu_per_lb: 11_500.0,
        note: "A third of water's cooling per pound and entirely combustible. Sprayed into \
               the charge it is a fuel delivery with a cooling side effect rather than the \
               other way round — size it as fuel first."
            .to_string(),
    }
}

/// A water-methanol blend at any strength, mixed by volume the way it is sold.
///
/// Methanol is a fifth lighter than water, so half methanol by volume is only 44 per cent by
/// weight, and every property here is a mass average.
pub fn blend(name: &str, methanol_by_volume: f64) -> Sprayable {
    let m = methanol_by_volume.clamp(0.0, 1.0);
    let w = 1.0 - m;

    let methanol = methanol();
    let water = water();

    let methanol_mass = m * methanol.density_g_per_cc;
    let water_mass = w * water.density_g_per_cc;
    let total_mass = methanol_mass + water_mass;

    if total_mass <= 0.0 {
        return water;
    }

    let methanol_fraction = methanol_mass / total_mass;
    let water_fraction = water_mass / total_mass;

    Sprayable {
        name: name.to_string(),
        latent_btu_per_lb: (methanol_fraction * methanol.latent_btu_per_lb)
            + (water_fraction * water.latent_btu_per_lb),
        density_g_per_cc: total_mass,
        specific_heat_btu_per_lb_f: (methanol_fraction * methanol.specific_heat_btu_per_lb_f)
            + (water_fraction * water.specific_heat_btu_per_lb_f),
        combustible_fraction: methanol_fraction,
        fuel_lhv_btu_per_lb: methanol.fuel_lhv_btu_per_lb,
        note: format!(
            "{} methanol by volume, which is {} by weight — the figure the cooling is worked \
             out from. Methanol is lighter than water, so the two are never the same number.",
            crate::fmt::f64(m, "P0"),
            crate::fmt::f64(methanol_fraction, "P0")
        ),
    }
}

/// The usual answer: half and half by volume, which is 44% methanol by mass.
pub fn fifty_fifty() -> Sprayable {
    blend("50/50 water-methanol", 0.5)
}

/// The mixtures worth offering, in the order somebody would consider them.
pub fn all() -> Vec<Sprayable> {
    vec![
        water(),
        blend("70/30 water-methanol", 0.30),
        fifty_fifty(),
        blend("30/70 water-methanol", 0.70),
        methanol(),
        e85(),
    ]
}

/// How much of the spray actually vaporises in the charge, by default (three-quarters is fair
/// for a decent nozzle well upstream of the throttle).
pub const TYPICAL_EVAPORATED: f64 = 0.75;

/// Spray needed to take a given amount of heat out of the charge, in lb/min.
///
/// C# defaults: `liquidF = 70`, `evaporated = TypicalEvaporated`.
pub fn flow_lb_per_min(
    air_lb_per_min: f64,
    drop_f: f64,
    fluid: &Sprayable,
    charge_f: f64,
    liquid_f: f64,
    evaporated: f64,
) -> f64 {
    let heat = heat_load_btu_per_min(air_lb_per_min, drop_f);
    let per_pound = fluid.cooling_btu_per_lb(liquid_f, charge_f);
    let share = evaporated.clamp(0.05, 1.0);

    if heat.is_nan() || per_pound <= 0.0 {
        f64::NAN
    } else {
        heat / per_pound / share
    }
}

/// The same, as a nozzle rating.
pub fn flow_cc_per_min(
    air_lb_per_min: f64,
    drop_f: f64,
    fluid: &Sprayable,
    charge_f: f64,
    liquid_f: f64,
    evaporated: f64,
) -> f64 {
    fluid.cc_per_min_from_lb_per_min(flow_lb_per_min(
        air_lb_per_min,
        drop_f,
        fluid,
        charge_f,
        liquid_f,
        evaporated,
    ))
}

/// The drop a given nozzle actually buys, which is the question the other way round.
pub fn drop_f_for(
    cc_per_min: f64,
    air_lb_per_min: f64,
    fluid: &Sprayable,
    charge_f: f64,
    liquid_f: f64,
    evaporated: f64,
) -> f64 {
    if air_lb_per_min <= 0.0 || cc_per_min <= 0.0 {
        return f64::NAN;
    }

    let lb_per_min = fluid.lb_per_min_from_cc_per_min(cc_per_min) * evaporated.clamp(0.05, 1.0);

    lb_per_min * fluid.cooling_btu_per_lb(liquid_f, charge_f) / (air_lb_per_min * CP_AIR)
}

/// Litres an hour, for sizing a tank.
pub fn litres_per_hour(cc_per_min: f64) -> f64 {
    cc_per_min * 60.0 / 1000.0
}

/// US gallons an hour, likewise.
pub fn gallons_per_hour(cc_per_min: f64) -> f64 {
    litres_per_hour(cc_per_min) / 3.785411784
}

/// How long a tank lasts at full flow, in minutes.
pub fn tank_minutes(tank_gallons: f64, cc_per_min: f64) -> f64 {
    if cc_per_min > 0.0 {
        tank_gallons * 3785.411784 / cc_per_min
    } else {
        f64::NAN
    }
}

/// Petrol this spray displaces, in lb/min, from the combustible part of it. An engine given both
/// without anyone taking the petrol out runs rich, then leans out sharply when the spray stops.
pub fn petrol_displaced_lb_per_min(spray_lb_per_min: f64, fluid: &Sprayable) -> f64 {
    const PETROL_LHV: f64 = 18_400.0;

    if spray_lb_per_min > 0.0 && fluid.combustible_fraction > 0.0 {
        spray_lb_per_min * fluid.combustible_fraction * fluid.fuel_lhv_btu_per_lb / PETROL_LHV
    } else {
        0.0
    }
}

/// Spray as a percentage of fuel flow, which is how systems are usually talked about.
pub fn percent_of_fuel(spray_lb_per_min: f64, fuel_lb_per_min: f64) -> f64 {
    if fuel_lb_per_min > 0.0 {
        spray_lb_per_min / fuel_lb_per_min * 100.0
    } else {
        f64::NAN
    }
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

    /// The published figures, converted once: water 2,260 kJ/kg, methanol 1,100.
    #[test]
    fn the_latent_heats_match_the_published_figures() {
        assert!(eq(2260.0 * BTU_PER_LB_PER_KJ_PER_KG, water().latent_btu_per_lb, 1));
        assert!(eq(1100.0 * BTU_PER_LB_PER_KJ_PER_KG, methanol().latent_btu_per_lb, 1));
        assert!(in_range(
            water().latent_btu_per_lb / methanol().latent_btu_per_lb,
            2.0,
            2.1
        ));
    }

    /// Fifty-fifty is sold by volume, and methanol is a fifth lighter than water, so it is 44%
    /// methanol by mass and the cooling has to be worked out from the mass fraction.
    #[test]
    fn fifty_fifty_by_volume_is_not_fifty_fifty_by_weight() {
        let mix = fifty_fifty();

        assert!(eq(0.442, mix.combustible_fraction, 3));
        assert!(eq(0.896, mix.density_g_per_cc, 3));

        let by_mass = (0.442 * methanol().latent_btu_per_lb) + (0.558 * water().latent_btu_per_lb);
        assert!(eq(by_mass, mix.latent_btu_per_lb, 1));

        let by_volume_wrongly = (0.5 * methanol().latent_btu_per_lb) + (0.5 * water().latent_btu_per_lb);
        assert!(
            mix.latent_btu_per_lb > by_volume_wrongly,
            "taking the volume fraction understates the cooling"
        );
    }

    #[test]
    fn the_ends_of_the_blend_range_are_the_neat_fluids() {
        assert!(eq(water().latent_btu_per_lb, blend("w", 0.0).latent_btu_per_lb, 6));
        assert!(eq(methanol().latent_btu_per_lb, blend("m", 1.0).latent_btu_per_lb, 6));
    }

    /// Nozzles are sold in cc/min and the physics is in pounds.
    #[test]
    fn a_nozzle_rating_is_a_volume_and_the_thermodynamics_is_a_mass() {
        let w = water();
        assert!(eq(1000.0 / 453.59237, w.lb_per_min_from_cc_per_min(1000.0), 6));
        assert!(eq(2.2046, w.lb_per_min_from_cc_per_min(1000.0), 3));

        assert!(methanol().lb_per_min_from_cc_per_min(1000.0) < w.lb_per_min_from_cc_per_min(1000.0));

        assert!(eq(
            1000.0,
            w.cc_per_min_from_lb_per_min(w.lb_per_min_from_cc_per_min(1000.0)),
            6
        ));
    }

    /// 40 lb/min of air dropped 100 F is 960 BTU a minute; water at 70 F into a 250 F charge
    /// removes 971.6 + 180 per pound, and at 75% evaporating that is 1.11 lb/min.
    #[test]
    fn sizing_a_spray_for_a_known_drop_agrees_with_the_arithmetic_by_hand() {
        let lb = flow_lb_per_min(40.0, 100.0, &water(), 250.0, 70.0, 0.75);

        let per_pound = 971.6 + (1.0 * (250.0 - 70.0));

        assert!(eq(960.0 / per_pound / 0.75, lb, 6));
        assert!(in_range(lb, 1.10, 1.12));

        let cc = flow_cc_per_min(40.0, 100.0, &water(), 250.0, 70.0, 0.75);

        assert!(in_range(cc, 495.0, 515.0));
    }

    #[test]
    fn water_needs_the_least_flow_and_e85_the_most() {
        let flow = |f: &Sprayable| flow_lb_per_min(40.0, 100.0, f, 250.0, 70.0, TYPICAL_EVAPORATED);

        assert!(flow(&water()) < flow(&fifty_fifty()));
        assert!(flow(&fifty_fifty()) < flow(&methanol()));
        assert!(flow(&methanol()) < flow(&e85()));

        assert!(in_range(flow(&e85()) / flow(&water()), 2.0, 3.5));
    }

    /// The question the other way round: what a nozzle already fitted buys.
    #[test]
    fn the_drop_a_given_nozzle_buys_is_the_inverse_of_sizing_one() {
        let f = fifty_fifty();
        let cc = flow_cc_per_min(40.0, 100.0, &f, 250.0, 70.0, TYPICAL_EVAPORATED);
        let drop = drop_f_for(cc, 40.0, &f, 250.0, 70.0, TYPICAL_EVAPORATED);

        assert!(eq(100.0, drop, 6));
    }

    /// Half evaporating needs twice the flow.
    #[test]
    fn what_does_not_evaporate_in_the_charge_still_has_to_be_sprayed() {
        let all = flow_lb_per_min(40.0, 100.0, &water(), 250.0, 70.0, 1.0);
        let half = flow_lb_per_min(40.0, 100.0, &water(), 250.0, 70.0, 0.5);

        assert!(eq(2.0 * all, half, 6));
    }

    /// Cold water into a hot charge is worth another sixth of the latent heat.
    #[test]
    fn the_liquids_own_warming_counts_too() {
        let cold = water().cooling_btu_per_lb(40.0, 300.0);
        let warm = water().cooling_btu_per_lb(200.0, 300.0);

        assert!(cold > warm);
        assert!(eq(971.6 + 260.0, cold, 6));

        // Liquid hotter than the charge contributes no sensible cooling rather than a negative amount.
        assert!(eq(971.6, water().cooling_btu_per_lb(300.0, 250.0), 6));
    }

    /// Methanol is fuel: a pound carries 8,640 BTU against petrol's 18,400.
    #[test]
    fn the_combustible_part_displaces_petrol_and_is_reported() {
        let displaced = petrol_displaced_lb_per_min(2.0, &methanol());

        assert!(eq(2.0 * 8_640.0 / 18_400.0, displaced, 6));
        assert!(in_range(displaced, 0.93, 0.95));
    }

    #[test]
    fn water_displaces_no_fuel_because_it_does_not_burn() {
        assert_eq!(0.0, petrol_displaced_lb_per_min(2.0, &water()));
    }

    #[test]
    fn a_fifty_fifty_blend_displaces_less_than_neat_methanol() {
        assert!(petrol_displaced_lb_per_min(2.0, &fifty_fifty()) < petrol_displaced_lb_per_min(2.0, &methanol()));
    }

    #[test]
    fn spray_is_also_expressed_as_a_share_of_fuel_because_controllers_are() {
        assert!(eq(30.0, percent_of_fuel(1.5, 5.0), 6));
    }

    /// 500 cc/min is 30 litres an hour, so a five gallon tank is barely half an hour.
    #[test]
    fn tank_life_is_shorter_than_people_expect() {
        assert!(eq(30.0, litres_per_hour(500.0), 6));
        assert!(in_range(gallons_per_hour(500.0), 7.8, 8.0));

        assert!(in_range(tank_minutes(5.0, 500.0), 36.0, 39.0));
    }

    #[test]
    fn no_airflow_means_no_answer_rather_than_infinity() {
        assert!(flow_lb_per_min(0.0, 100.0, &water(), 250.0, 70.0, TYPICAL_EVAPORATED).is_nan());
        assert!(drop_f_for(500.0, 0.0, &water(), 250.0, 70.0, TYPICAL_EVAPORATED).is_nan());
    }

    #[test]
    fn every_sprayable_says_what_it_is_for_and_what_it_costs() {
        for s in all() {
            assert!(s.latent_btu_per_lb > 0.0, "{} has no latent heat", s.name);
            assert!(s.density_g_per_cc > 0.0, "{} has no density", s.name);
            assert!(!s.note.trim().is_empty(), "{} has no note", s.name);
        }
    }
}
