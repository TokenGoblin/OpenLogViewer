//! Ported from `TurboSizing.cs`. Owned by package `w1-calc-a`.
//!
//! Choosing a turbocharger for a power target: the sizing worked backwards. The equations are
//! the maker's own (Garrett's worked example, 650 hp on a 5.7 litre at 6,000 rpm, is a test),
//! so answers can be checked against the tool everybody already uses. This is not a compressor
//! map: flow and pressure ratio narrow the field, the map decides between them.

use crate::base::tuning_math as tm;

/// A turbocharger as a catalogue lists it.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Turbo {
    /// The series it belongs to, for grouping.
    pub family: &'static str,
    /// What it is called.
    pub model: &'static str,
    /// Compressor wheel inducer, the diameter air is drawn in at.
    pub inducer_mm: f64,
    /// Compressor wheel exducer, the diameter it leaves at.
    pub exducer_mm: f64,
    /// What the maker rates it for. On the G series this is the model number itself.
    pub rated_horsepower: f64,
}

impl Turbo {
    /// The air it can pass, worked out from its rating rather than transcribed.
    ///
    /// A compressor map's flow axis has no single "maximum", while the horsepower rating is the
    /// maker's own claim. Converted with the maker's airflow equation at their worked-example
    /// mixture and fuel consumption.
    pub fn max_flow_lb_per_minute(&self) -> f64 {
        air_for_horsepower(self.rated_horsepower, RATED_AFR, RATED_BSFC)
    }
}

impl std::fmt::Display for Turbo {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.model)
    }
}

/// One turbo against a requirement, and how much room it has left.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TurboMatch {
    pub turbo: Turbo,
    /// Flow spare, as a fraction of what is needed.
    pub headroom: f64,
    /// Two of the same turbo, where the requirement wants a pair. 1 otherwise.
    pub count: i32,
}

impl TurboMatch {
    pub fn new(turbo: Turbo, headroom: f64) -> Self {
        TurboMatch {
            turbo,
            headroom,
            count: 1,
        }
    }

    pub fn label(&self) -> String {
        if self.count > 1 {
            format!("{} \u{00d7} {}", crate::fmt::int(self.count, ""), self.turbo.model)
        } else {
            self.turbo.model.to_string()
        }
    }
}

/// What an engine needs of a turbocharger.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TurboRequirement {
    /// Air the target power takes.
    pub air_lb_per_minute: f64,
    /// Absolute manifold pressure that airflow needs.
    pub manifold_kpa: f64,
    /// The same as a gauge reading.
    pub boost_kpa: f64,
    /// What the compressor is asked to work at, losses included.
    pub pressure_ratio: f64,
    /// Absolute pressure at the compressor inlet.
    pub compressor_inlet_kpa: f64,
    /// Absolute pressure it must deliver.
    pub compressor_outlet_kpa: f64,
}

/// Mixture the catalogue's horsepower ratings are taken at (the maker's worked example).
pub const RATED_AFR: f64 = 11.5;

/// Fuel consumption those ratings are taken at, from the same example.
pub const RATED_BSFC: f64 = 0.46;

/// Headroom worth having over the flow actually needed.
pub const SENSIBLE_HEADROOM: f64 = 0.10;

/// The air a power target needs, in pounds per minute: power times the mixture times the fuel
/// consumption, over the sixty minutes in an hour.
pub fn air_for_horsepower(horsepower: f64, afr: f64, bsfc: f64) -> f64 {
    if horsepower > 0.0 && afr > 0.0 && bsfc > 0.0 {
        horsepower * afr * bsfc / 60.0
    } else {
        f64::NAN
    }
}

/// The absolute manifold pressure a given airflow needs: the gas law run backwards, in
/// kilopascals and litres.
#[allow(clippy::neg_cmp_op_on_partial_ord)]
pub fn manifold_kpa_for(air_lb_per_minute: f64, litres: f64, rpm: f64, ve_percent: f64, charge_celsius: f64) -> f64 {
    let ve = ve_percent / 100.0;

    if !(air_lb_per_minute > 0.0) || !(litres > 0.0) || !(rpm > 0.0) || !(ve > 0.0) {
        return f64::NAN;
    }

    let kg_per_minute = air_lb_per_minute * 0.45359237;
    let kelvin = charge_celsius + 273.15;

    if !(kelvin > 0.0) {
        return f64::NAN;
    }

    kg_per_minute * 574.0 * kelvin / (litres * ve * rpm)
}

/// Everything the engine asks of a compressor.
///
/// C# defaults: `barometricKpa = AtmosphericKpa`, `inletLossKpa = 0`, `chargeLossKpa = 0`.
#[allow(clippy::too_many_arguments)]
pub fn required(
    horsepower: f64,
    afr: f64,
    bsfc: f64,
    litres: f64,
    rpm: f64,
    ve_percent: f64,
    charge_celsius: f64,
    barometric_kpa: f64,
    inlet_loss_kpa: f64,
    charge_loss_kpa: f64,
) -> TurboRequirement {
    let air = air_for_horsepower(horsepower, afr, bsfc);
    let manifold = manifold_kpa_for(air, litres, rpm, ve_percent, charge_celsius);

    if manifold.is_nan() {
        return TurboRequirement {
            air_lb_per_minute: air,
            manifold_kpa: f64::NAN,
            boost_kpa: f64::NAN,
            pressure_ratio: f64::NAN,
            compressor_inlet_kpa: f64::NAN,
            compressor_outlet_kpa: f64::NAN,
        };
    }

    let boost = manifold - barometric_kpa;
    let compressor = tm::compressor_pressures(boost, barometric_kpa, inlet_loss_kpa, charge_loss_kpa);

    TurboRequirement {
        air_lb_per_minute: air,
        manifold_kpa: manifold,
        boost_kpa: boost,
        pressure_ratio: compressor.ratio,
        compressor_inlet_kpa: compressor.inlet_kpa,
        compressor_outlet_kpa: compressor.outlet_kpa,
    }
}

const fn turbo(model: &'static str, inducer: f64, exducer: f64, hp: f64) -> Turbo {
    Turbo {
        family: "G Series",
        model,
        inducer_mm: inducer,
        exducer_mm: exducer,
        rated_horsepower: hp,
    }
}

/// A short catalogue of Garrett's performance turbochargers.
///
/// Short on purpose: a longer list would be more useful and far harder to keep true, and a
/// suggestion made from a stale figure is worse than none. Check against the maker's current
/// catalogue before buying anything on the strength of this.
pub const CATALOGUE: [Turbo; 11] = [
    turbo("G25-550", 48.0, 60.0, 550.0),
    turbo("G25-660", 54.0, 67.0, 660.0),
    turbo("G30-660", 58.0, 71.0, 660.0),
    turbo("G30-770", 58.0, 71.0, 770.0),
    turbo("G30-900", 62.0, 76.0, 900.0),
    turbo("G35-900", 62.0, 76.0, 900.0),
    turbo("G35-1050", 68.0, 84.0, 1050.0),
    turbo("G40-1150", 71.0, 88.0, 1150.0),
    turbo("G42-1200", 73.0, 91.0, 1200.0),
    turbo("G42-1450", 79.0, 98.0, 1450.0),
    turbo("G45-1475", 79.0, 98.0, 1475.0),
];

/// Turbochargers that could pass the air, smallest first.
///
/// Smallest rather than largest: it spools soonest and spends most of its life near the middle
/// of its map. A pair is offered where a single one cannot do it.
///
/// C# defaults: `headroom = SensibleHeadroom`, `most = 4`.
#[allow(clippy::neg_cmp_op_on_partial_ord)]
pub fn suggest(air_lb_per_minute: f64, headroom: f64, most: usize) -> Vec<TurboMatch> {
    if !(air_lb_per_minute > 0.0) {
        return Vec::new();
    }

    let wanted = air_lb_per_minute * (1.0 + headroom);

    let pick = |multiple: f64| -> Vec<Turbo> {
        let mut found: Vec<Turbo> = CATALOGUE
            .iter()
            .copied()
            .filter(|t| t.max_flow_lb_per_minute() * multiple >= wanted)
            .collect();
        // OrderBy is stable, and so is sort_by.
        found.sort_by(|a, b| a.max_flow_lb_per_minute().total_cmp(&b.max_flow_lb_per_minute()));
        found.truncate(most);
        found
    };

    let singles: Vec<TurboMatch> = pick(1.0)
        .into_iter()
        .map(|t| TurboMatch::new(t, (t.max_flow_lb_per_minute() / air_lb_per_minute) - 1.0))
        .collect();

    if !singles.is_empty() {
        return singles;
    }

    // Nothing single-handedly. Two of something, which is how a large engine is usually done.
    pick(2.0)
        .into_iter()
        .map(|t| TurboMatch {
            turbo: t,
            headroom: (t.max_flow_lb_per_minute() * 2.0 / air_lb_per_minute) - 1.0,
            count: 2,
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::base::tuning_math::{self as tm, Fuel};

    /// `Assert.Equal(expected, actual, digits)`: both rounded (ties to even) then compared.
    fn eq(expected: f64, actual: f64, digits: i32) -> bool {
        let scale = 10f64.powi(digits);
        (expected * scale).round_ties_even() == (actual * scale).round_ties_even()
    }

    fn in_range(v: f64, lo: f64, hi: f64) -> bool {
        v >= lo && v <= hi
    }

    // Garrett's worked example: 650 hp on a 5.7 litre at 6,000 rpm, 80% VE, a 130 F manifold.
    const HP: f64 = 650.0;
    const AFR: f64 = 11.5;
    const BSFC: f64 = 0.46;
    const LITRES: f64 = 5.7;
    const RPM: f64 = 6000.0;
    const VE: f64 = 80.0;

    fn charge_c() -> f64 {
        (130.0 - 32.0) * 5.0 / 9.0
    }

    fn req(hp: f64, litres: f64, charge: f64) -> TurboRequirement {
        required(hp, AFR, BSFC, litres, RPM, VE, charge, tm::ATMOSPHERIC_KPA, 0.0, 0.0)
    }

    #[test]
    fn the_airflow_is_the_makers_own_figure() {
        assert!(eq(57.3, air_for_horsepower(HP, AFR, BSFC), 1));
    }

    #[test]
    fn the_airflow_agrees_with_the_power_calculation_turned_around() {
        let air = air_for_horsepower(HP, AFR, BSFC);
        let back = tm::horsepower_from_air(air, Fuel::Petrol, AFR / tm::stoichiometric(Fuel::Petrol), BSFC);
        assert!(eq(HP, back, 6));
    }

    #[test]
    fn the_manifold_pressure_is_the_makers_own_figure_to_within_their_own_rounding() {
        let air = air_for_horsepower(HP, AFR, BSFC);
        let kpa = manifold_kpa_for(air, LITRES, RPM, VE, charge_c());

        assert!(in_range(kpa / tm::KPA_PER_PSI / 26.025, 0.99, 1.01));
        assert!(in_range((kpa - tm::ATMOSPHERIC_KPA) / tm::KPA_PER_PSI, 11.0, 11.5));
    }

    #[test]
    fn the_manifold_pressure_also_matches_the_makers_imperial_formula() {
        let air = air_for_horsepower(HP, AFR, BSFC);

        let cubic_inches = LITRES * tm::CUBIC_INCHES_PER_LITRE;
        let rankine = (charge_c() * 9.0 / 5.0) + 32.0 + 459.67;
        let imperial = air * 639.6 * rankine / (VE / 100.0 * (RPM / 2.0) * cubic_inches);
        let metric = manifold_kpa_for(air, LITRES, RPM, VE, charge_c()) / tm::KPA_PER_PSI;

        assert!(in_range(metric / imperial, 0.999, 1.001));
    }

    #[test]
    fn the_pressure_ratio_is_the_makers_own_figure() {
        let bare = tm::compressor_pressures(12.0 * tm::KPA_PER_PSI, 14.7 * tm::KPA_PER_PSI, tm::KPA_PER_PSI, 0.0);
        assert!(eq(1.95, bare.ratio, 2));

        let with_loss = tm::compressor_pressures(
            (26.025 - 14.7) * tm::KPA_PER_PSI,
            14.7 * tm::KPA_PER_PSI,
            tm::KPA_PER_PSI,
            2.0 * tm::KPA_PER_PSI,
        );
        assert!(eq(2.0, with_loss.ratio, 1));
    }

    #[test]
    fn the_whole_example_comes_out_where_the_maker_says_it_does() {
        let need = required(
            HP,
            AFR,
            BSFC,
            LITRES,
            RPM,
            VE,
            charge_c(),
            14.7 * tm::KPA_PER_PSI,
            tm::KPA_PER_PSI,
            2.0 * tm::KPA_PER_PSI,
        );

        assert!(eq(57.3, need.air_lb_per_minute, 1));
        assert!(in_range(need.boost_kpa / tm::KPA_PER_PSI, 11.0, 11.5));
        assert!(in_range(need.pressure_ratio, 1.98, 2.08));
    }

    #[test]
    fn more_power_needs_more_air_and_more_boost() {
        let small = req(400.0, LITRES, charge_c());
        let big = req(800.0, LITRES, charge_c());

        assert!(eq(2.0, big.air_lb_per_minute / small.air_lb_per_minute, 6));
        assert!(big.manifold_kpa > small.manifold_kpa);
        assert!(big.pressure_ratio > small.pressure_ratio);
    }

    #[test]
    fn a_bigger_engine_needs_less_boost_for_the_same_power() {
        let small = req(650.0, 2.0, charge_c());
        let big = req(650.0, 5.7, charge_c());

        assert!(eq(small.air_lb_per_minute, big.air_lb_per_minute, 6));
        assert!(big.boost_kpa < small.boost_kpa);
    }

    #[test]
    fn a_hotter_charge_needs_more_pressure_for_the_same_air() {
        let cool = req(HP, LITRES, 30.0);
        let hot = req(HP, LITRES, 70.0);

        assert!(hot.manifold_kpa > cool.manifold_kpa);
        assert!(in_range(hot.manifold_kpa / cool.manifold_kpa, 1.10, 1.16));
    }

    #[test]
    fn altitude_asks_more_of_the_compressor_for_the_same_engine() {
        let high = tm::barometric_kpa(5_000.0 * tm::METRES_PER_FOOT);

        let sea = required(
            HP,
            AFR,
            BSFC,
            LITRES,
            RPM,
            VE,
            charge_c(),
            tm::ATMOSPHERIC_KPA,
            0.0,
            0.0,
        );
        let mountain = required(HP, AFR, BSFC, LITRES, RPM, VE, charge_c(), high, 0.0, 0.0);

        assert!(eq(sea.manifold_kpa, mountain.manifold_kpa, 6));
        assert!(mountain.pressure_ratio > sea.pressure_ratio);
        assert!(mountain.boost_kpa > sea.boost_kpa);
    }

    #[test]
    fn nonsense_asks_for_nothing() {
        assert!(air_for_horsepower(0.0, AFR, BSFC).is_nan());
        assert!(air_for_horsepower(HP, 0.0, BSFC).is_nan());
        assert!(manifold_kpa_for(50.0, 0.0, RPM, VE, 40.0).is_nan());
        assert!(manifold_kpa_for(50.0, LITRES, 0.0, VE, 40.0).is_nan());
        assert!(manifold_kpa_for(50.0, LITRES, RPM, 0.0, 40.0).is_nan());
    }

    #[test]
    fn every_turbo_in_the_catalogue_is_described_completely() {
        assert!(!CATALOGUE.is_empty());

        for t in CATALOGUE.iter() {
            assert!(!t.model.trim().is_empty());
            assert!(in_range(t.inducer_mm, 30.0, 120.0));
            assert!(in_range(t.rated_horsepower, 100.0, 3_000.0));
            assert!(t.exducer_mm > t.inducer_mm, "{} has a wheel that shrinks", t.model);
        }

        let mut models: Vec<&str> = CATALOGUE.iter().map(|t| t.model).collect();
        models.sort_unstable();
        models.dedup();
        assert_eq!(CATALOGUE.len(), models.len());
    }

    #[test]
    fn a_bigger_wheel_is_rated_for_more_power() {
        let mut by_wheel = CATALOGUE.to_vec();
        by_wheel.sort_by(|a, b| a.inducer_mm.total_cmp(&b.inducer_mm));

        for i in 1..by_wheel.len() {
            assert!(
                by_wheel[i].rated_horsepower >= by_wheel[i - 1].rated_horsepower,
                "{} has a bigger wheel than {} and a lower rating",
                by_wheel[i].model,
                by_wheel[i - 1].model
            );
        }
    }

    #[test]
    fn flow_derived_from_the_rating_lands_where_reported_flow_figures_do() {
        for (model, reported) in [("G30-770", 69.0), ("G30-900", 81.0), ("G35-900", 82.5)] {
            let t = CATALOGUE.iter().find(|t| t.model == model).unwrap();
            assert!(in_range(t.max_flow_lb_per_minute() / reported, 0.94, 1.06));
        }
    }

    #[test]
    fn the_makers_own_example_is_offered_a_sensible_turbo() {
        let s = suggest(57.3, SENSIBLE_HEADROOM, 4);

        assert!(!s.is_empty());
        assert_eq!("G30-770", s[0].turbo.model);
        assert_eq!(1, s[0].count);
    }

    #[test]
    fn the_smallest_that_can_do_it_comes_first() {
        let s = suggest(50.0, SENSIBLE_HEADROOM, 4);

        for i in 1..s.len() {
            assert!(
                s[i].turbo.max_flow_lb_per_minute() >= s[i - 1].turbo.max_flow_lb_per_minute(),
                "the list should climb, so the first is the one that spools soonest"
            );
        }
    }

    #[test]
    fn nothing_is_offered_that_cannot_pass_the_air() {
        for air in [20.0, 45.0, 60.0, 90.0, 120.0] {
            for m in suggest(air, SENSIBLE_HEADROOM, 4) {
                assert!(
                    m.turbo.max_flow_lb_per_minute() * f64::from(m.count) >= air,
                    "{} was offered for {air} lb/min and cannot pass it",
                    m.label()
                );
            }
        }
    }

    #[test]
    fn headroom_is_insisted_on_and_reported() {
        let s = suggest(60.0, 0.20, 4);
        assert!(s.iter().all(|m| m.headroom >= 0.20));

        let relaxed = suggest(60.0, 0.0, 4)[0].turbo.max_flow_lb_per_minute();
        let strict = suggest(60.0, 0.40, 4)[0].turbo.max_flow_lb_per_minute();
        assert!(strict >= relaxed);
    }

    #[test]
    fn a_pair_is_offered_where_one_cannot_do_it() {
        let beyond = CATALOGUE
            .iter()
            .map(|t| t.max_flow_lb_per_minute())
            .fold(f64::MIN, f64::max)
            * 1.5;

        let s = suggest(beyond, SENSIBLE_HEADROOM, 4);

        assert!(!s.is_empty());
        assert!(s.iter().all(|m| m.count == 2));
        assert!(s.iter().all(|m| m.label().starts_with("2 \u{00d7} ")));
    }

    #[test]
    fn nothing_at_all_is_offered_for_nothing_at_all() {
        assert!(suggest(0.0, SENSIBLE_HEADROOM, 4).is_empty());
        assert!(suggest(f64::NAN, SENSIBLE_HEADROOM, 4).is_empty());
        assert!(suggest(-10.0, SENSIBLE_HEADROOM, 4).is_empty());
    }

    #[test]
    fn moving_the_mixture_and_the_consumption_together_barely_changes_the_air() {
        let lambda = 11.5 / tm::stoichiometric(Fuel::Petrol);
        let petrol = air_for_horsepower(HP, 11.5, RATED_BSFC);

        for fuel in [Fuel::E30, Fuel::E85, Fuel::Ethanol] {
            let air = air_for_horsepower(
                HP,
                tm::afr_from_lambda(lambda, fuel),
                tm::suggested_bsfc(fuel, RATED_BSFC),
            );
            assert!(in_range(air / petrol, 0.98, 1.01));
        }

        let methanol = air_for_horsepower(
            HP,
            tm::afr_from_lambda(lambda, Fuel::Methanol),
            tm::suggested_bsfc(Fuel::Methanol, RATED_BSFC),
        );
        assert!(in_range(methanol / petrol, 0.94, 0.97));
    }

    #[test]
    fn moving_only_the_consumption_asks_for_absurdly_more_air() {
        let petrol = air_for_horsepower(HP, 11.5, RATED_BSFC);

        let half_changed = air_for_horsepower(HP, 11.5, tm::suggested_bsfc(Fuel::E85, RATED_BSFC));
        assert!(in_range(half_changed / petrol, 1.4, 1.6));

        let worse = air_for_horsepower(HP, 11.5, tm::suggested_bsfc(Fuel::Methanol, RATED_BSFC));
        assert!(worse / petrol > 2.0);
    }

    #[test]
    fn the_air_needed_agrees_with_the_power_the_airflow_tab_would_report() {
        let lambda = 0.78;

        for fuel in Fuel::ALL {
            if fuel == Fuel::Diesel {
                continue;
            }

            let afr = tm::afr_from_lambda(lambda, fuel);
            let own_bsfc = tm::suggested_bsfc(fuel, RATED_BSFC);

            let air = air_for_horsepower(HP, afr, own_bsfc);
            let back = tm::horsepower_from_air(air, fuel, lambda, RATED_BSFC);

            assert!(eq(HP, back, 6), "{fuel:?}");
        }
    }
}
