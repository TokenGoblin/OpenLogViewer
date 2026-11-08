//! The w2-manifold calculator against the C# reference (rust/oracle/expected/w2-manifold/manifold.json).
//!
//! Each case is a named call with its inputs and outputs as IEEE bits. The Rust runs the same
//! arithmetic in the same order, so the comparison is on bits. Verdict strings and warning text
//! are compared verbatim.

use olv_core::calc::manifold_tuning as mt;
use olv_core::calc::manifold_tuning::{Induction, ManifoldGoal, ManifoldSpec, TuningOrder};
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

fn flat(o: &TuningOrder) -> Vec<f64> {
    vec![
        f64::from(o.order),
        o.effective_length_mm,
        o.length_mm,
        o.rpm,
        if o.practical { 1.0 } else { 0.0 },
    ]
}

fn flat_list(orders: &[TuningOrder]) -> Vec<f64> {
    let mut v = vec![orders.len() as f64];
    v.extend(orders.iter().flat_map(flat));
    v
}

fn induction(i: f64) -> Induction {
    Induction::ALL[i as usize]
}

fn goal(i: f64) -> ManifoldGoal {
    ManifoldGoal::ALL[i as usize]
}

#[test]
fn every_manifold_calculation_matches_the_csharp() {
    let cases: Vec<Case> = olv_oracle::cases("w2-manifold", "manifold");
    assert!(cases.len() > 1900);

    for c in &cases {
        let i: Vec<f64> = c.input.iter().map(|h| f(h)).collect();
        match c.name.as_str() {
            "sound" => check(
                c,
                &[
                    mt::speed_of_sound_air(i[0]),
                    mt::speed_of_sound_exhaust(i[0]),
                    mt::speed_of_sound(i[0], 1.4, 287.0),
                ],
            ),
            "wave" => {
                let order = i[2] as i32;
                check(
                    c,
                    &[
                        mt::tuned_length_mm(i[0], i[1], order, i[3]),
                        mt::tuned_rpm(i[0], i[1], order, i[3]),
                    ],
                );
            }
            "geometry" => {
                let d = i[0];
                check(
                    c,
                    &[
                        mt::end_correction_mm(d),
                        mt::area_mm2(d),
                        mt::diameter_mm(d),
                        mt::collector_diameter_mm(d, 4),
                        mt::collector_diameter_mm(d, 1),
                        mt::collector_length_mm(d),
                        mt::pipe_volume_cc(500.0, d),
                        mt::pipe_volume_cc(d, 40.0),
                        mt::exhaust_wave_window_deg(d),
                        mt::bell_primary_length_inches(d, 6500.0),
                    ],
                );
            }
            "velocity" => {
                let v = mt::port_velocity(i[0], i[1], i[2], i[3], i[4]);
                check(c, &[v, mt::area_for_velocity(i[0], i[1], i[2], i[3], i[4])]);
                assert_eq!(mt::velocity_verdict(v), c.text[0], "{}", c.name);
                assert_eq!(mt::exhaust_velocity_verdict(v), c.text[1], "{}", c.name);
            }
            "verdict" => {
                assert_eq!(mt::velocity_verdict(i[0]), c.text[0], "velocity verdict {}", i[0]);
                assert_eq!(
                    mt::exhaust_velocity_verdict(i[0]),
                    c.text[1],
                    "exhaust verdict {}",
                    i[0]
                );
            }
            "effective_cylinder" => check(c, &[mt::effective_cylinder_volume_cc(i[0], i[1])]),
            "helmholtz" => {
                let hz = mt::helmholtz_hz(i[0], i[1], i[2], i[3]);
                check(c, &[hz, mt::helmholtz_ratio(hz, 6000.0), mt::helmholtz_ratio(hz, 0.0)]);
            }
            "plenum_volume" => check(
                c,
                &[
                    mt::plenum_volume_cc(i[0], i[1]),
                    mt::tract_share_of_typical_installation(i[0], i[1] * 1000.0),
                ],
            ),
            "plenum_verdict" => {
                assert_eq!(
                    mt::plenum_verdict(i[1], induction(i[0])),
                    c.text[0],
                    "plenum verdict {} {}",
                    i[0],
                    i[1]
                );
            }
            "exhaust_volume" => check(
                c,
                &[mt::exhaust_volume_per_cycle_cc(
                    i[0], i[1], i[2], i[3], i[4], i[5], i[6],
                )],
            ),
            "goals" => {
                let g = goal(i[0]);
                check(
                    c,
                    &[
                        mt::intake_tuning_rpm(g, i[2], i[3]),
                        mt::target_intake_velocity(g),
                        mt::target_exhaust_velocity(g),
                        mt::default_plenum_multiple(g, induction(i[1])),
                        mt::max_runner_for_goal(g),
                    ],
                );
            }
            "orders" => {
                let max = i[4];
                let list = mt::orders(i[0], i[1], i[2], i[3], max, i[5] as i32);
                let mut out = flat_list(&list);
                out.extend(flat(&mt::recommend(&list, max, false)));
                out.extend(flat(&mt::recommend(&list, max, true)));
                check(c, &out);
            }
            "plan" => {
                let spec = ManifoldSpec {
                    litres: i[0],
                    cylinders: i[1] as i32,
                    induction: induction(i[2]),
                    goal: goal(i[3]),
                    peak_torque_rpm: i[4],
                    peak_power_rpm: i[5],
                    intake_duration_deg: i[6],
                    exhaust_duration_deg: i[7],
                    intake_air_celsius: i[8],
                    exhaust_celsius: i[9],
                    volumetric_efficiency: i[10],
                    compression_ratio: i[11],
                    manifold_kpa: i[12],
                    afr: i[13],
                    exhaust_back_pressure_kpa: i[14],
                    intake_runner_diameter_mm: i[15],
                    exhaust_primary_diameter_mm: i[16],
                    plenum_multiple: i[17],
                };
                let p = mt::plan(&spec);
                let (a, e) = (&p.intake, &p.exhaust);

                let mut out = vec![
                    a.speed_of_sound,
                    a.tuned_rpm,
                    a.runner_diameter_mm,
                    a.runner_area_mm2,
                    a.runner_volume_cc,
                    a.velocity_at_peak_power,
                    a.velocity_at_peak_torque,
                    a.mach_at_peak_power,
                    a.plenum_volume_cc,
                    a.plenum_multiple,
                    a.tract_volume_cc,
                    a.helmholtz_hz,
                    a.helmholtz_ratio,
                ];
                out.extend(flat(&a.recommended));
                out.extend(flat_list(&a.orders));
                out.extend([
                    e.speed_of_sound,
                    e.tuned_rpm,
                    e.wave_window_deg,
                    e.primary_diameter_mm,
                    e.primary_volume_cc,
                    e.total_primary_volume_cc,
                    e.velocity_at_peak_power,
                    e.collector_diameter_mm,
                    e.collector_length_mm,
                    e.bell_length_mm,
                ]);
                out.extend(flat(&e.recommended));
                out.extend(flat_list(&e.orders));
                check(c, &out);

                let warnings: Vec<String> = p
                    .warnings
                    .iter()
                    .map(|w| format!("{}|{}", w.severity, w.text))
                    .collect();
                assert_eq!(warnings, c.text, "{} warnings for {i:?}", c.name);
            }
            other => panic!("unknown case {other}"),
        }
    }
}
