//! The w1-calc-a calculators against the C# reference (rust/oracle/expected/w1-calc-a/calc.json).
//!
//! Each case is a named call with its inputs and outputs as IEEE bits. The Rust runs the same
//! arithmetic in the same order, so the comparison is on bits.

use olv_core::base::tuning_math::Fuel;
use olv_core::calc::{
    engine_geometry as geo, engine_recipe as recipe, intercooling as ic, octane_blend as octane, sprayables as spray,
    turbo_sizing as turbo,
};
use serde::Deserialize;

#[derive(Deserialize)]
struct Case {
    name: String,
    input: Vec<String>,
    output: Vec<String>,
    text: Vec<String>,
}

fn f(hex: &str) -> f64 {
    f64::from_bits(u64::from_str_radix(hex, 16).expect("hex bits"))
}

/// Same bits, or both NaN (C# NaN payloads and Rust's can differ in sign bit).
fn same(actual: f64, expected_hex: &str, what: &str) {
    let expected = f(expected_hex);
    if expected.is_nan() {
        assert!(actual.is_nan(), "{what}: expected NaN, got {actual}");
    } else {
        assert_eq!(
            actual.to_bits(),
            expected.to_bits(),
            "{what}: got {actual}, expected {expected}"
        );
    }
}

fn check(c: &Case, actual: &[f64]) {
    assert_eq!(actual.len(), c.output.len(), "{} output count", c.name);
    for (i, (a, e)) in actual.iter().zip(&c.output).enumerate() {
        same(
            *a,
            e,
            &format!(
                "{} {:?} output {i}",
                c.name,
                c.input.iter().map(|h| f(h)).collect::<Vec<_>>()
            ),
        );
    }
}

fn blendstock(i: f64) -> octane::Blendstock {
    octane::Blendstock::ALL[i as usize]
}

#[test]
fn every_calculator_matches_the_csharp() {
    let cases: Vec<Case> = olv_oracle::cases("w1-calc-a", "calc");
    assert!(cases.len() > 600);

    for c in &cases {
        let i: Vec<f64> = c.input.iter().map(|h| f(h)).collect();
        match c.name.as_str() {
            "turbo_required" => {
                let r = turbo::required(i[0], i[1], i[2], i[3], i[4], i[5], i[6], i[7], i[8], i[9]);
                check(
                    c,
                    &[
                        r.air_lb_per_minute,
                        r.manifold_kpa,
                        r.boost_kpa,
                        r.pressure_ratio,
                        r.compressor_inlet_kpa,
                        r.compressor_outlet_kpa,
                    ],
                );
            }
            "turbo_suggest" => {
                let s = turbo::suggest(i[0], i[1], 4);
                let out: Vec<f64> = s.iter().flat_map(|m| [m.headroom, f64::from(m.count)]).collect();
                check(c, &out);
                let labels: Vec<String> = s.iter().map(|m| m.label()).collect();
                assert_eq!(labels, c.text, "{} labels for {i:?}", c.name);
            }
            "turbo_flow" => {
                let t = turbo::CATALOGUE.iter().find(|t| t.model == c.text[0]).expect("model");
                assert_eq!(t.rated_horsepower, i[0]);
                check(c, &[t.max_flow_lb_per_minute()]);
            }
            "octane_blend" => {
                let r = octane::blend(i[1], i[2], blendstock(i[0]), i[3]);
                check(
                    c,
                    &[
                        r.ron,
                        r.mon,
                        r.anti_knock_index,
                        r.ethanol_by_volume,
                        r.alcohol_mole_fraction,
                        r.heat_of_vaporisation_kj_per_kg,
                        r.cooling_kj_per_kg_air,
                    ],
                );
            }
            "octane_charts" => {
                let stock = blendstock(i[0]);
                assert_eq!(octane::chart(stock), c.text[0], "chart {stock:?}");
                assert_eq!(octane::cooling_chart(stock), c.text[1], "cooling chart {stock:?}");
                assert_eq!(octane::name(stock), c.text[2]);
            }
            "recipe" => {
                let spec = recipe::RecipeSpec {
                    litres: i[0],
                    cylinders: i[1] as i32,
                    target_horsepower: i[2],
                    peak_torque_rpm: i[3],
                    peak_power_rpm: i[4],
                    fuel: Fuel::ALL[i[5] as usize],
                    lambda: i[6],
                    petrol_bsfc: i[7],
                    volumetric_efficiency: i[8],
                    charge_celsius: i[9],
                    injector_duty_limit: i[10],
                    rail_psi: i[11],
                    pump_headroom_percent: i[12],
                    barometric_kpa: i[13],
                    inlet_loss_kpa: i[14],
                    charge_loss_kpa: i[15],
                };
                let r = recipe::build(&spec);
                let mut out = vec![
                    r.air_at_peak_power,
                    r.air_at_peak_torque,
                    r.afr,
                    r.bsfc,
                    r.manifold_kpa,
                    r.boost_kpa,
                    r.pressure_ratio,
                    r.injector_cc_each,
                    r.injector_lb_hr_each,
                    r.fuel_litres_per_hour,
                    r.pump_litres_per_hour,
                    r.rail_under_boost_psi,
                    r.mean_piston_speed,
                    r.specific_output,
                    r.turbos.len() as f64,
                    r.pumps.len() as f64,
                ];
                out.extend(r.turbos.iter().flat_map(|m| [m.headroom, f64::from(m.count)]));
                out.extend(
                    r.pumps
                        .iter()
                        .flat_map(|p| [f64::from(p.count), p.delivered_litres_per_hour]),
                );
                check(c, &out);

                let mut text: Vec<String> = r.turbos.iter().map(|m| m.label()).collect();
                text.extend(r.warnings.iter().map(|w| format!("{}|{}", w.severity, w.text)));
                assert_eq!(text, c.text, "recipe text for {i:?}");
            }
            "geometry" => {
                let (bore, stroke) = (i[0], i[1]);
                let swept = geo::swept_volume_cc(bore, stroke);
                let clearance = geo::clearance_volume_cc(bore, 42.0, 87.0, 1.0, 0.5, 5.0);
                check(
                    c,
                    &[
                        swept,
                        geo::displacement_cc(bore, stroke, 4),
                        geo::bore_to_stroke(bore, stroke),
                        geo::mean_piston_speed(stroke, 7000.0),
                        geo::mean_piston_speed_feet_per_minute(stroke, 7000.0),
                        clearance,
                        geo::compression_ratio(swept, clearance),
                        geo::clearance_for_ratio(swept, 10.0),
                        geo::boosted_compression_index(10.0, 200.0, 101.325),
                    ],
                );
                assert_eq!(
                    geo::piston_speed_verdict(geo::mean_piston_speed(stroke, 7000.0)),
                    c.text[0]
                );
            }
            "piston_verdict" => assert_eq!(geo::piston_speed_verdict(i[0]), c.text[0], "{}", i[0]),
            "compressor_outlet" => check(c, &[ic::compressor_outlet_f(i[0], i[1], i[2])]),
            "outlet" => check(
                c,
                &[
                    ic::outlet_f(i[0], i[1], i[2]),
                    ic::effectiveness_from(285.0, 148.5, 90.0),
                    ic::density_ratio(285.0, 148.5),
                    ic::pressure_ratio(20.0, ic::STANDARD_BARO_PSI),
                    ic::heat_load_btu_per_min(40.0, 150.0),
                    ic::pressure_loss_fraction(2.0, 20.0, ic::STANDARD_BARO_PSI),
                ],
            ),
            "metal_share" => {
                let m = ic::CORE_MATERIALS
                    .iter()
                    .find(|m| m.name == c.text[0])
                    .expect("material");
                check(
                    c,
                    &[
                        ic::metal_share_of_resistance(m, 0.5, 100.0),
                        ic::metal_share_of_resistance(m, 0.8, 50.0),
                    ],
                );
            }
            "core" => {
                let core = ic::IntercoolerCore::new(i[0], i[1], i[2]);
                check(
                    c,
                    &[
                        core.frontal_area_sq_in(),
                        core.volume_cu_in(),
                        core.loading_per_cu_in(1440.0),
                        core.loading_per_frontal_sq_in(1440.0),
                    ],
                );
            }
            "spray_blend" => {
                let b = spray::blend("b", i[0]);
                check(
                    c,
                    &[
                        b.latent_btu_per_lb,
                        b.density_g_per_cc,
                        b.specific_heat_btu_per_lb_f,
                        b.combustible_fraction,
                        b.fuel_lhv_btu_per_lb,
                    ],
                );
                assert_eq!(b.note, c.text[0], "blend note for {}", i[0]);
            }
            "spray_flow" => {
                let fluid = spray::all().into_iter().find(|s| s.name == c.text[0]).expect("fluid");
                assert_eq!(fluid.note, c.text[1]);
                let (air, evap) = (i[0], i[1]);
                check(
                    c,
                    &[
                        spray::flow_lb_per_min(air, 100.0, &fluid, 250.0, 70.0, evap),
                        spray::flow_cc_per_min(air, 100.0, &fluid, 250.0, 70.0, evap),
                        spray::drop_f_for(500.0, air, &fluid, 250.0, 70.0, evap),
                        fluid.cooling_btu_per_lb(40.0, 300.0),
                        fluid.cooling_btu_per_lb(300.0, 250.0),
                        spray::petrol_displaced_lb_per_min(2.0, &fluid),
                        spray::tank_minutes(5.0, 500.0),
                        spray::gallons_per_hour(500.0),
                        spray::percent_of_fuel(1.5, 5.0),
                        fluid.lb_per_cc(),
                        fluid.cc_per_min_from_lb_per_min(1.5),
                    ],
                );
            }
            other => panic!("unknown case {other}"),
        }
    }
}
