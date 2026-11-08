//! Ported from `Intercooling.cs`. Owned by package `w1-calc-a`.
//!
//! What a compressor does to the air, and what it costs to undo. Compressing air heats it, and
//! hot air is thin, so a turbocharger delivering 20 psi of hot air delivers considerably less
//! oxygen than 20 psi suggests. The C# `ChargeAir` static class is the free functions here.

/// Specific heat of air at constant pressure, BTU per pound per degree F.
pub const CP_AIR: f64 = 0.24;

/// (gamma-1)/gamma for air, the exponent in the compression temperature rise.
///
/// Gamma is 1.4 for a diatomic gas, so this is 0.4/1.4. It makes the temperature rise depend
/// on pressure ratio rather than on boost: the first ten psi cost far less heat than the second.
pub const GAMMA_EXPONENT: f64 = 0.4 / 1.4;

/// Absolute zero on the Fahrenheit scale, for anything that needs Rankine.
pub const RANKINE_OFFSET: f64 = 459.67;

/// Sea level, near enough, in psi absolute.
pub const STANDARD_BARO_PSI: f64 = 14.696;

/// Air out of the compressor, before anything has cooled it.
///
/// The ideal rise is what perfect compression would cost; a real compressor is worse and the
/// shortfall all becomes heat, which is why the rise is divided by efficiency. `efficiency` is
/// isentropic, 0 to 1 (0.70 is ordinary, 0.78 is good).
pub fn compressor_outlet_f(inlet_f: f64, pressure_ratio: f64, efficiency: f64) -> f64 {
    // C# `efficiency is <= 0 or > 1`: false for NaN, so a NaN efficiency is not refused.
    if pressure_ratio < 1.0 || efficiency <= 0.0 || efficiency > 1.0 {
        return f64::NAN;
    }

    let inlet_r = inlet_f + RANKINE_OFFSET;
    let ideal_rise = inlet_r * (pressure_ratio.powf(GAMMA_EXPONENT) - 1.0);

    inlet_f + (ideal_rise / efficiency)
}

/// Pressure ratio from gauge boost and the barometer.
pub fn pressure_ratio(boost_psi: f64, baro_psi: f64) -> f64 {
    if baro_psi > 0.0 {
        (boost_psi + baro_psi) / baro_psi
    } else {
        f64::NAN
    }
}

/// Heat that has to leave the charge, in BTU per minute: mass flow times specific heat times
/// the drop.
pub fn heat_load_btu_per_min(air_lb_per_min: f64, drop_f: f64) -> f64 {
    if air_lb_per_min > 0.0 && drop_f > 0.0 {
        air_lb_per_min * CP_AIR * drop_f
    } else {
        f64::NAN
    }
}

/// What comes out, given how good the core is. Effectiveness is the fraction of the available
/// temperature difference the core takes: 1.0 would leave the charge at ambient.
pub fn outlet_f(inlet_f: f64, ambient_f: f64, effectiveness: f64) -> f64 {
    if (0.0..=1.0).contains(&effectiveness) {
        inlet_f - (effectiveness * (inlet_f - ambient_f))
    } else {
        f64::NAN
    }
}

/// What a core is achieving, from temperatures actually measured.
pub fn effectiveness_from(inlet_f: f64, outlet_f: f64, ambient_f: f64) -> f64 {
    if (inlet_f - ambient_f).abs() > 0.001 {
        (inlet_f - outlet_f) / (inlet_f - ambient_f)
    } else {
        f64::NAN
    }
}

/// How much denser the charge is after cooling, at the same pressure. Density goes as absolute
/// temperature.
pub fn density_ratio(from_f: f64, to_f: f64) -> f64 {
    let from = from_f + RANKINE_OFFSET;
    let to = to_f + RANKINE_OFFSET;

    if to > 0.0 && from > 0.0 { from / to } else { f64::NAN }
}

/// Pressure lost across the core, as a fraction of what went in.
pub fn pressure_loss_fraction(drop_psi: f64, boost_psi: f64, baro_psi: f64) -> f64 {
    if boost_psi + baro_psi > 0.0 {
        drop_psi / (boost_psi + baro_psi)
    } else {
        f64::NAN
    }
}

/// A core's outside dimensions, in inches.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct IntercoolerCore {
    pub width_in: f64,
    pub height_in: f64,
    pub thickness_in: f64,
}

impl IntercoolerCore {
    pub fn new(width_in: f64, height_in: f64, thickness_in: f64) -> Self {
        IntercoolerCore {
            width_in,
            height_in,
            thickness_in,
        }
    }

    /// What the oncoming air sees.
    pub fn frontal_area_sq_in(&self) -> f64 {
        self.width_in * self.height_in
    }

    pub fn volume_cu_in(&self) -> f64 {
        self.width_in * self.height_in * self.thickness_in
    }

    pub fn is_usable(&self) -> bool {
        self.width_in > 0.0 && self.height_in > 0.0 && self.thickness_in > 0.0
    }

    /// Heat the core is being asked to move per cubic inch of itself. Not a pass or a fail:
    /// good for comparing two candidates, since the same duty over half the core is twice the
    /// loading.
    pub fn loading_per_cu_in(&self, heat_btu_per_min: f64) -> f64 {
        if self.is_usable() && heat_btu_per_min > 0.0 {
            heat_btu_per_min / self.volume_cu_in()
        } else {
            f64::NAN
        }
    }

    pub fn loading_per_frontal_sq_in(&self, heat_btu_per_min: f64) -> f64 {
        if self.is_usable() && heat_btu_per_min > 0.0 {
            heat_btu_per_min / self.frontal_area_sq_in()
        } else {
            f64::NAN
        }
    }
}

/// What a core is made of, and how little that turns out to matter.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct CoreMaterial {
    pub name: &'static str,
    pub conductivity_w_m_k: f64,
    pub relative_density: f64,
    pub note: &'static str,
}

/// Core materials, and an honest account of what choosing between them does.
///
/// Very little, thermally: a heat exchanger's resistance is overwhelmingly the film of slow air
/// on the fins, and the metal is a fraction of a millimetre of something that conducts hundreds
/// of times better. What the material decides is weight, cost, corrosion and brazing.
pub const CORE_MATERIALS: [CoreMaterial; 3] = [
    CoreMaterial {
        name: "Aluminium",
        conductivity_w_m_k: 205.0,
        relative_density: 1.0,
        note: "What essentially every intercooler is. Light, cheap, brazes well, and \
               conducts far better than the air film that is actually holding things up.",
    },
    CoreMaterial {
        name: "Copper / brass",
        conductivity_w_m_k: 385.0,
        relative_density: 3.3,
        note: "Nearly twice the conductivity and more than three times the weight. The \
               conductivity buys almost nothing, because the metal was never the \
               restriction; the weight is charged in full.",
    },
    CoreMaterial {
        name: "Stainless steel",
        conductivity_w_m_k: 16.0,
        relative_density: 2.9,
        note: "An eighth of aluminium's conductivity and three times its weight. Chosen \
               for corrosion or pressure, never for cooling.",
    },
];

/// The share of the total resistance that is the metal, for a wall this thick.
///
/// `wall_mm` is the wall thickness (0.3 to 0.8 is usual); `air_side_w_m2_k` the air-side heat
/// transfer coefficient (50 to 150 for forced convection over finned surfaces). C# defaults:
/// 0.5 and 100.
pub fn metal_share_of_resistance(material: &CoreMaterial, wall_mm: f64, air_side_w_m2_k: f64) -> f64 {
    if material.conductivity_w_m_k <= 0.0 || wall_mm <= 0.0 || air_side_w_m2_k <= 0.0 {
        return f64::NAN;
    }

    // Per square metre: the wall's resistance against the two air films.
    let wall = wall_mm / 1000.0 / material.conductivity_w_m_k;
    let films = 2.0 / air_side_w_m2_k;

    wall / (wall + films)
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

    /// 20 psi on a standard day is a pressure ratio of 2.36, and at 70% efficiency that is
    /// about 300 F out of the compressor (283.7 by hand).
    #[test]
    fn compressor_outlet_is_the_ideal_rise_divided_by_efficiency() {
        let pr = pressure_ratio(20.0, STANDARD_BARO_PSI);

        assert!(eq(2.3609, pr, 3));

        let outlet = compressor_outlet_f(70.0, pr, 0.70);

        assert!(in_range(outlet, 280.0, 288.0));
        assert!(compressor_outlet_f(70.0, pr, 1.0) < outlet - 50.0);
    }

    /// The rise depends on pressure ratio, not on boost.
    #[test]
    fn the_second_ten_psi_costs_less_heat_than_the_first() {
        let first = compressor_outlet_f(70.0, pressure_ratio(10.0, STANDARD_BARO_PSI), 0.72) - 70.0;
        let to_twenty = compressor_outlet_f(70.0, pressure_ratio(20.0, STANDARD_BARO_PSI), 0.72) - 70.0;

        let second = to_twenty - first;

        assert!(second < first, "first ten cost {first:.0} F, second {second:.0}");
    }

    #[test]
    fn an_impossible_efficiency_is_refused() {
        for efficiency in [0.0, -1.0, 1.5] {
            assert!(compressor_outlet_f(70.0, 2.0, efficiency).is_nan(), "{efficiency}");
        }
    }

    /// 40 lb/min cooled by 150 F is 1,440 BTU a minute.
    #[test]
    fn the_heat_load_is_mass_flow_times_specific_heat_times_the_drop() {
        assert!(eq(1440.0, heat_load_btu_per_min(40.0, 150.0), 6));
        assert!(eq(86_400.0, heat_load_btu_per_min(40.0, 150.0) * 60.0, 6));
    }

    /// A 70% core with 285 F in and 90 F ambient leaves 285 - 0.7 x 195 = 148.5 F.
    #[test]
    fn effectiveness_is_the_share_of_the_available_drop_taken() {
        assert!(eq(148.5, outlet_f(285.0, 90.0, 0.70), 6));
        assert!(eq(90.0, outlet_f(285.0, 90.0, 1.0), 6));
        assert!(eq(285.0, outlet_f(285.0, 90.0, 0.0), 6));
    }

    #[test]
    fn effectiveness_can_be_read_back_from_measured_temperatures() {
        assert!(eq(0.70, effectiveness_from(285.0, 148.5, 90.0), 6));
    }

    /// Density goes as absolute temperature: 285 F down to 148.5 is about 22% more air.
    #[test]
    fn cooling_buys_density_in_proportion_to_absolute_temperature() {
        let ratio = density_ratio(285.0, 148.5);

        assert!(eq((285.0 + 459.67) / (148.5 + 459.67), ratio, 9));
        assert!(in_range(ratio, 1.20, 1.25));
    }

    #[test]
    fn a_cores_loading_is_the_heat_spread_over_its_size() {
        let core = IntercoolerCore::new(24.0, 12.0, 3.0);

        assert!(eq(288.0, core.frontal_area_sq_in(), 6));
        assert!(eq(864.0, core.volume_cu_in(), 6));
        assert!(eq(1440.0 / 864.0, core.loading_per_cu_in(1440.0), 6));

        // Half the core is twice the loading, which is the comparison it is for.
        let half = IntercoolerCore::new(24.0, 12.0, 1.5);
        assert!(eq(
            2.0 * core.loading_per_cu_in(1440.0),
            half.loading_per_cu_in(1440.0),
            6
        ));
    }

    /// A half-millimetre aluminium wall is well under one per cent of the resistance between
    /// the two air streams, so doubling its conductivity cannot buy anything worth having.
    #[test]
    fn the_metal_is_not_what_is_stopping_the_heat() {
        let aluminium = &CORE_MATERIALS[0];
        let copper = &CORE_MATERIALS[1];

        let al_share = metal_share_of_resistance(aluminium, 0.5, 100.0);

        assert!(al_share < 0.01, "aluminium wall is {al_share} of the resistance");

        let gain = al_share - metal_share_of_resistance(copper, 0.5, 100.0);

        assert!(gain < 0.005, "copper would buy {gain} of the total resistance");
    }

    #[test]
    fn every_material_says_what_it_is_actually_for_rather_than_claiming_cooling() {
        for m in CORE_MATERIALS.iter() {
            assert!(!m.note.trim().is_empty());
            assert!(m.conductivity_w_m_k > 0.0);
            assert!(m.relative_density > 0.0);
        }
    }

    #[test]
    fn no_airflow_means_no_answer_rather_than_infinity() {
        assert!(heat_load_btu_per_min(0.0, 100.0).is_nan());
        assert!(IntercoolerCore::new(0.0, 0.0, 0.0).loading_per_cu_in(1440.0).is_nan());
    }
}
