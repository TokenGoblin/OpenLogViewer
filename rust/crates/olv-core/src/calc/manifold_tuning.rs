//! Ported from `ManifoldTuning.cs`. Owned by package `w2-manifold`.
//!
//! Runner and primary lengths, port sizes and plenum volume: the pipework, sized by the
//! pressure waves running up and down it.
//!
//! A manifold is not a plumbing problem. An opening valve launches a pressure wave along the
//! pipe; the far end reflects it back inverted; and if it arrives at the valve before it
//! shuts, it either packs charge in or pulls exhaust out. Length decides when it arrives, so
//! length decides the engine speed it works at, and that is a choice about what the engine is
//! for rather than a correct answer waiting to be found.
//!
//! Two calculations run through everything here. The first is timing: the wave makes K round
//! trips of the pipe in the time the valve is open, so 2KL/a = theta/(6*rpm), and
//! L = a*theta / (12*K*rpm), with a the speed of sound in the gas, theta the valve open
//! duration in crank degrees and K the harmonic. The second is velocity:
//! v = 6*rpm*V_swept*VE / (theta*A).
//!
//! Both are derived rather than fitted, so they can be checked against something: the exhaust
//! length agrees with A. Graham Bell's empirical formula to within about five per cent at a
//! mean primary gas temperature of 600 degrees C.
//!
//! What this is not is a flow bench or a simulation. It sizes pipework from wave timing and
//! mean velocity, and knows nothing about port shape, valve curtain area or bend radii.

use super::engine_geometry;
use super::engine_recipe::RecipeWarning;
use crate::base::tuning_math as tm;
use crate::fmt;

/// How the engine gets its air, which changes what the pipework is for.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Induction {
    NaturallyAspirated,
    Turbocharged,
}

impl Induction {
    pub const ALL: [Induction; 2] = [Induction::NaturallyAspirated, Induction::Turbocharged];
}

/// What the engine is being built to do, which is the only thing that decides between two
/// lengths that are both "correct".
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ManifoldGoal {
    /// Boost early, torque low, response over peak power.
    QuickSpool,
    /// A road engine that still has a top end.
    Balanced,
    /// Everything given to peak power, with the bottom end spent to get it.
    HighRpmRace,
}

impl ManifoldGoal {
    pub const ALL: [ManifoldGoal; 3] = [
        ManifoldGoal::QuickSpool,
        ManifoldGoal::Balanced,
        ManifoldGoal::HighRpmRace,
    ];
}

/// One harmonic of the tuned length, and where it puts the resonance.
#[derive(Clone, Copy, Debug, PartialEq, Default)]
pub struct TuningOrder {
    /// How many complete out-and-back trips the wave makes while the valve is open. Lower is a
    /// longer pipe tuned lower.
    pub order: i32,
    /// The acoustic length, end correction included.
    pub effective_length_mm: f64,
    /// The length to actually build: the acoustic length less the end correction.
    pub length_mm: f64,
    /// The engine speed this order is tuned for.
    pub rpm: f64,
    /// Whether this one can be built in an engine bay.
    pub practical: bool,
}

/// What is being designed, and for what.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ManifoldSpec {
    pub litres: f64,
    pub cylinders: i32,
    pub induction: Induction,
    pub goal: ManifoldGoal,
    /// The bottom of the range being designed for.
    pub peak_torque_rpm: f64,
    /// The top of the range, where the air peaks and the ports run out.
    pub peak_power_rpm: f64,
    /// How long the inlet valve is open, in crank degrees.
    pub intake_duration_deg: f64,
    /// How long the exhaust valve is open, in crank degrees.
    pub exhaust_duration_deg: f64,
    /// Air temperature in the runner.
    pub intake_air_celsius: f64,
    /// Mean gas temperature along the primary, not the temperature at the valve.
    pub exhaust_celsius: f64,
    pub volumetric_efficiency: f64,
    pub compression_ratio: f64,
    /// Absolute manifold pressure at peak power, which on a turbo engine is the boost.
    pub manifold_kpa: f64,
    /// Mixture at full throttle.
    pub afr: f64,
    /// Pressure in the exhaust manifold, absolute.
    pub exhaust_back_pressure_kpa: f64,
    /// Runner diameter, if already fixed. Zero sizes it from the velocity target.
    pub intake_runner_diameter_mm: f64,
    /// Primary diameter, if already fixed. Zero sizes it from velocity.
    pub exhaust_primary_diameter_mm: f64,
    /// Plenum as a multiple of displacement. Zero takes the goal's default.
    pub plenum_multiple: f64,
}

impl Default for ManifoldSpec {
    fn default() -> Self {
        ManifoldSpec {
            litres: 2.0,
            cylinders: 4,
            induction: Induction::NaturallyAspirated,
            goal: ManifoldGoal::Balanced,
            peak_torque_rpm: 4500.0,
            peak_power_rpm: 7000.0,
            intake_duration_deg: 240.0,
            exhaust_duration_deg: 250.0,
            intake_air_celsius: 30.0,
            exhaust_celsius: 600.0,
            volumetric_efficiency: 100.0,
            compression_ratio: 10.5,
            manifold_kpa: tm::ATMOSPHERIC_KPA,
            afr: 12.5,
            exhaust_back_pressure_kpa: tm::ATMOSPHERIC_KPA,
            intake_runner_diameter_mm: 0.0,
            exhaust_primary_diameter_mm: 0.0,
            plenum_multiple: 0.0,
        }
    }
}

/// The intake side of the answer.
#[derive(Clone, Debug, PartialEq)]
pub struct IntakePlan {
    pub speed_of_sound: f64,
    pub tuned_rpm: f64,
    pub orders: Vec<TuningOrder>,
    /// The one to build, chosen for fitting in a car as well as for being tuned.
    pub recommended: TuningOrder,
    pub runner_diameter_mm: f64,
    pub runner_area_mm2: f64,
    pub runner_volume_cc: f64,
    /// Mean port velocity at peak power, in metres per second.
    pub velocity_at_peak_power: f64,
    pub velocity_at_peak_torque: f64,
    /// Mean velocity at peak power as a fraction of the speed of sound.
    pub mach_at_peak_power: f64,
    pub plenum_volume_cc: f64,
    pub plenum_multiple: f64,
    /// The plenum and every runner together: the charge this page can account for, in cc.
    pub tract_volume_cc: f64,
    /// Where the plenum and runners resonate together, in hertz.
    pub helmholtz_hz: f64,
    /// That resonance against the rate the cylinder actually draws at the tuned speed.
    pub helmholtz_ratio: f64,
}

/// The exhaust side of the answer.
#[derive(Clone, Debug, PartialEq)]
pub struct ExhaustPlan {
    pub speed_of_sound: f64,
    pub tuned_rpm: f64,
    /// Crank degrees from the exhaust valve opening to overlap top dead centre.
    pub wave_window_deg: f64,
    pub orders: Vec<TuningOrder>,
    pub recommended: TuningOrder,
    pub primary_diameter_mm: f64,
    pub primary_volume_cc: f64,
    /// Every primary together, which is what a turbine has to fill before it spins.
    pub total_primary_volume_cc: f64,
    pub velocity_at_peak_power: f64,
    pub collector_diameter_mm: f64,
    pub collector_length_mm: f64,
    /// Bell's published length for the same cam and speed, for comparison.
    pub bell_length_mm: f64,
}

/// An intake and exhaust designed together, and what to worry about.
#[derive(Clone, Debug, PartialEq)]
pub struct ManifoldPlan {
    pub intake: IntakePlan,
    pub exhaust: ExhaustPlan,
    pub warnings: Vec<RecipeWarning>,
}

/// Ratio of specific heats for air.
pub const GAMMA_AIR: f64 = 1.4;

/// The same for burnt gas, which is lower: the products are triatomic.
pub const GAMMA_EXHAUST: f64 = 1.33;

/// Specific gas constant for air, J/kg.K.
pub const GAS_CONSTANT_AIR: f64 = 287.05;

/// The same for exhaust.
pub const GAS_CONSTANT_EXHAUST: f64 = 287.0;

/// How much acoustically longer an open pipe behaves than it measures. The flanged value is
/// used because a runner opening into a plenum, and a primary into a collector, are both
/// closer to a hole in a wall than to a pipe in free air.
pub const FLANGED_END_CORRECTION: f64 = 0.82;

/// The shortest runner worth calling one.
pub const MIN_PRACTICAL_RUNNER_MM: f64 = 100.0;

/// Past this, it does not fit under a bonnet.
pub const MAX_PRACTICAL_RUNNER_MM: f64 = 650.0;

/// Past this, a primary is a packaging fight rather than a pipe.
pub const MAX_PRACTICAL_PRIMARY_MM: f64 = 1_100.0;

// ----- the gas ---------------------------------------------------------------

/// Speed of sound in a gas: sqrt(gamma*R*T), the whole of the acoustics in one line.
pub fn speed_of_sound(celsius: f64, gamma: f64, gas_constant: f64) -> f64 {
    let kelvin = celsius + 273.15;

    if kelvin > 0.0 && gamma > 0.0 && gas_constant > 0.0 {
        (gamma * gas_constant * kelvin).sqrt()
    } else {
        f64::NAN
    }
}

/// Speed of sound in the intake charge.
pub fn speed_of_sound_air(celsius: f64) -> f64 {
    speed_of_sound(celsius, GAMMA_AIR, GAS_CONSTANT_AIR)
}

/// Speed of sound in the exhaust, which is half as fast again for being hot.
pub fn speed_of_sound_exhaust(celsius: f64) -> f64 {
    speed_of_sound(celsius, GAMMA_EXHAUST, GAS_CONSTANT_EXHAUST)
}

// ----- wave timing -----------------------------------------------------------

/// The acoustic length that puts the returning wave at the valve on time:
/// L = a*theta / (12*K*rpm), straight from 2KL/a = theta/(6*rpm). No fitted constant anywhere.
pub fn tuned_length_mm(speed_of_sound: f64, window_deg: f64, order: i32, rpm: f64) -> f64 {
    if speed_of_sound > 0.0 && window_deg > 0.0 && order > 0 && rpm > 0.0 {
        1_000.0 * speed_of_sound * window_deg / (12.0 * f64::from(order) * rpm)
    } else {
        f64::NAN
    }
}

/// The same relation read the other way: what an existing pipe is tuned for.
pub fn tuned_rpm(speed_of_sound: f64, window_deg: f64, order: i32, effective_length_mm: f64) -> f64 {
    if speed_of_sound > 0.0 && window_deg > 0.0 && order > 0 && effective_length_mm > 0.0 {
        1_000.0 * speed_of_sound * window_deg / (12.0 * f64::from(order) * effective_length_mm)
    } else {
        f64::NAN
    }
}

/// The crank degrees an exhaust wave has to work in: from the valve cracking open to overlap
/// top dead centre, 180 + (ED-180)/2 which tidies to 90 + ED/2. Assumes the cam is symmetric
/// about bottom dead centre.
pub fn exhaust_wave_window_deg(exhaust_duration_deg: f64) -> f64 {
    if exhaust_duration_deg > 180.0 {
        90.0 + (exhaust_duration_deg / 2.0)
    } else {
        f64::NAN
    }
}

/// How much longer than its measured length a pipe behaves, from its bore.
pub fn end_correction_mm(diameter_mm: f64) -> f64 {
    if diameter_mm > 0.0 {
        FLANGED_END_CORRECTION * diameter_mm / 2.0
    } else {
        0.0
    }
}

/// A. Graham Bell's published primary length, in inches, for cross-checking:
/// L = 850*ED/rpm - 3. Empirical.
pub fn bell_primary_length_inches(exhaust_duration_deg: f64, rpm: f64) -> f64 {
    if exhaust_duration_deg > 0.0 && rpm > 0.0 {
        (850.0 * exhaust_duration_deg / rpm) - 3.0
    } else {
        f64::NAN
    }
}

// ----- ports and velocity ----------------------------------------------------

pub fn area_mm2(diameter_mm: f64) -> f64 {
    if diameter_mm > 0.0 {
        std::f64::consts::PI * diameter_mm * diameter_mm / 4.0
    } else {
        f64::NAN
    }
}

pub fn diameter_mm(area_mm2: f64) -> f64 {
    if area_mm2 > 0.0 {
        (4.0 * area_mm2 / std::f64::consts::PI).sqrt()
    } else {
        f64::NAN
    }
}

/// Mean gas velocity through a port while the valve is open, in metres per second. The
/// cylinder takes its swept volume times its filling in the theta degrees the valve is open.
pub fn port_velocity(swept_cc: f64, volumetric_efficiency: f64, rpm: f64, window_deg: f64, area_mm2: f64) -> f64 {
    if !(swept_cc > 0.0 && volumetric_efficiency > 0.0 && rpm > 0.0 && window_deg > 0.0 && area_mm2 > 0.0) {
        return f64::NAN;
    }

    // cc to m^3, mm^2 to m^2; the 6*rpm is crank degrees per second.
    let swept_m3 = swept_cc / 1e6;
    let area_m2 = area_mm2 / 1e6;

    6.0 * rpm * swept_m3 * (volumetric_efficiency / 100.0) / (window_deg * area_m2)
}

/// The port area that hits a wanted velocity, which is the sizing question.
pub fn area_for_velocity(swept_cc: f64, volumetric_efficiency: f64, rpm: f64, window_deg: f64, velocity: f64) -> f64 {
    if !(swept_cc > 0.0 && volumetric_efficiency > 0.0 && rpm > 0.0 && window_deg > 0.0 && velocity > 0.0) {
        return f64::NAN;
    }

    let swept_m3 = swept_cc / 1e6;

    1e6 * 6.0 * rpm * swept_m3 * (volumetric_efficiency / 100.0) / (window_deg * velocity)
}

/// What a mean port velocity means, in words. Bands from what engines are actually built to.
pub fn velocity_verdict(metres_per_second: f64) -> &'static str {
    let v = metres_per_second;

    if v.is_nan() || v <= 0.0 {
        "\u{2014}"
    } else if v < 60.0 {
        "oversized \u{2014} lazy off boost, poor low-lift flow"
    } else if v < 80.0 {
        "large, biased to peak power"
    } else if v < 110.0 {
        "the usual compromise"
    } else if v < 130.0 {
        "small and fast \u{2014} strong low end, tops out early"
    } else if v < 150.0 {
        "restrictive at peak power"
    } else {
        "choked; the port is the limit"
    }
}

/// The gas inside a pipe, which on a turbo manifold is the thing to minimise.
pub fn pipe_volume_cc(length_mm: f64, diameter_mm: f64) -> f64 {
    if length_mm > 0.0 && diameter_mm > 0.0 {
        engine_geometry::cylinder_volume_cc(diameter_mm, length_mm)
    } else {
        f64::NAN
    }
}

// ----- the plenum ------------------------------------------------------------

/// The cylinder's volume as the resonator sees it: the mean of what it holds through the
/// stroke, V(r+1) / 2(r-1). Engelman's effective volume.
pub fn effective_cylinder_volume_cc(swept_cc: f64, compression_ratio: f64) -> f64 {
    if swept_cc > 0.0 && compression_ratio > 1.0 {
        swept_cc * (compression_ratio + 1.0) / (2.0 * (compression_ratio - 1.0))
    } else {
        f64::NAN
    }
}

/// Where the runner and the volume behind it resonate together, in hertz: a Helmholtz
/// resonator, f = (a/2pi)*sqrt(A / V*L).
pub fn helmholtz_hz(speed_of_sound: f64, area_mm2: f64, effective_length_mm: f64, volume_cc: f64) -> f64 {
    if !(speed_of_sound > 0.0 && area_mm2 > 0.0 && effective_length_mm > 0.0 && volume_cc > 0.0) {
        return f64::NAN;
    }

    let area_m2 = area_mm2 / 1e6;
    let length_m = effective_length_mm / 1_000.0;
    let volume_m3 = volume_cc / 1e6;

    speed_of_sound / (2.0 * std::f64::consts::PI) * (area_m2 / (volume_m3 * length_m)).sqrt()
}

/// That resonance against how often the cylinder actually draws (rpm/120 hertz). A
/// cross-check and not a target: a design tuned by the wave calculation lands between about 3
/// and 4.
pub fn helmholtz_ratio(helmholtz_hz: f64, rpm: f64) -> f64 {
    if helmholtz_hz > 0.0 && rpm > 0.0 {
        helmholtz_hz / (rpm / 120.0)
    } else {
        f64::NAN
    }
}

/// Plenum volume, as a multiple of what the engine displaces.
pub fn plenum_volume_cc(displacement_cc: f64, multiple: f64) -> f64 {
    if displacement_cc > 0.0 && multiple > 0.0 {
        displacement_cc * multiple
    } else {
        f64::NAN
    }
}

/// The multiples worth putting side by side: quarter steps from half displacement to twice it.
pub const PLENUM_MULTIPLES: [f64; 7] = [0.50, 0.75, 1.00, 1.25, 1.50, 1.75, 2.00];

/// What a plenum multiple does, in words, and it differs by induction. The bands are a
/// quarter of displacement wide, matching [`PLENUM_MULTIPLES`], so every step in the table
/// reads differently.
pub fn plenum_verdict(multiple: f64, induction: Induction) -> &'static str {
    // C# switch order: a NaN multiple matches none of the comparisons and reaches the last arm.
    if multiple <= 0.0 {
        return "\u{2014}";
    }

    if induction == Induction::Turbocharged {
        if multiple < 0.65 {
            "least to pressurise, sharpest spool \u{2014} watch distribution"
        } else if multiple < 0.90 {
            "lean, chosen for spool over steadiness"
        } else if multiple < 1.15 {
            "a little more settled, slightly slower to fill"
        } else if multiple < 1.40 {
            "middling \u{2014} steadier under boost"
        } else if multiple < 1.65 {
            "generous \u{2014} smooth at full boost, slower to it"
        } else if multiple < 1.90 {
            "large \u{2014} noticeably slower to build"
        } else {
            "very large \u{2014} the slowest of these to pressurise"
        }
    } else if multiple < 0.65 {
        "very small \u{2014} sharp off idle, gone up top"
    } else if multiple < 0.90 {
        "small \u{2014} strong response, caps peak power"
    } else if multiple < 1.15 {
        "on the small side, favours the mid-range"
    } else if multiple < 1.40 {
        "a common road compromise"
    } else if multiple < 1.65 {
        "the usual compromise"
    } else if multiple < 1.90 {
        "generous \u{2014} top end over pedal feel"
    } else {
        "large \u{2014} peak power, and a soft pedal"
    }
}

/// How much of the charge a turbocharger has to pressurise this page can see. The honest
/// qualifier on every spool argument made from plenum volume.
pub fn tract_share_of_typical_installation(tract_cc: f64, inter_cooler_and_piping_cc: f64) -> f64 {
    if tract_cc > 0.0 && inter_cooler_and_piping_cc > 0.0 {
        tract_cc / (tract_cc + inter_cooler_and_piping_cc)
    } else {
        f64::NAN
    }
}

// ----- the exhaust side ------------------------------------------------------

/// The volume of gas one cylinder pushes out per cycle, at the temperature and pressure it is
/// at in the primary, in cc. Mass first (the charge plus the fuel), then expanded to where it
/// now is.
pub fn exhaust_volume_per_cycle_cc(
    swept_cc: f64,
    volumetric_efficiency: f64,
    manifold_kpa: f64,
    charge_celsius: f64,
    exhaust_celsius: f64,
    back_pressure_kpa: f64,
    afr: f64,
) -> f64 {
    if !(swept_cc > 0.0 && volumetric_efficiency > 0.0 && manifold_kpa > 0.0 && back_pressure_kpa > 0.0) {
        return f64::NAN;
    }

    let charge_k = charge_celsius + 273.15;
    let exhaust_k = exhaust_celsius + 273.15;

    if charge_k <= 0.0 || exhaust_k <= 0.0 {
        return f64::NAN;
    }

    // Charge mass: ideal gas at the manifold, times how full the cylinder gets.
    let swept_m3 = swept_cc / 1e6;
    let air_kg = manifold_kpa * 1_000.0 * swept_m3 * (volumetric_efficiency / 100.0) / (GAS_CONSTANT_AIR * charge_k);

    // The fuel leaves too, an eighth of the air mass again at 12.5:1.
    let total_kg = if afr > 0.0 {
        air_kg * (1.0 + (1.0 / afr))
    } else {
        air_kg
    };

    1e6 * total_kg * GAS_CONSTANT_EXHAUST * exhaust_k / (back_pressure_kpa * 1_000.0)
}

/// Collector diameter for a four-into-one, from the primaries feeding it: twice a primary's
/// area, about 1.4 times its diameter.
pub fn collector_diameter_mm(primary_diameter_mm: f64, primaries: i32) -> f64 {
    if primary_diameter_mm > 0.0 && primaries > 1 {
        diameter_mm(area_mm2(primary_diameter_mm) * 2.0)
    } else {
        f64::NAN
    }
}

/// How long the collector should be: about half a primary.
pub fn collector_length_mm(primary_length_mm: f64) -> f64 {
    if primary_length_mm > 0.0 {
        primary_length_mm / 2.0
    } else {
        f64::NAN
    }
}

// ----- goals -----------------------------------------------------------------

/// The engine speed a goal tunes the intake for.
pub fn intake_tuning_rpm(goal: ManifoldGoal, peak_torque_rpm: f64, peak_power_rpm: f64) -> f64 {
    match goal {
        ManifoldGoal::QuickSpool => peak_torque_rpm,
        ManifoldGoal::HighRpmRace => peak_power_rpm,
        ManifoldGoal::Balanced => (peak_torque_rpm + peak_power_rpm) / 2.0,
    }
}

/// Mean port velocity a goal aims at, at peak power.
pub fn target_intake_velocity(goal: ManifoldGoal) -> f64 {
    match goal {
        ManifoldGoal::QuickSpool => 105.0,
        ManifoldGoal::HighRpmRace => 85.0,
        ManifoldGoal::Balanced => 95.0,
    }
}

/// The same for the exhaust. Calibrated by working the velocity backwards out of header sizes
/// that are actually on engines; all of them fall between 168 and 211 m/s.
pub fn target_exhaust_velocity(goal: ManifoldGoal) -> f64 {
    match goal {
        ManifoldGoal::QuickSpool => 205.0,
        ManifoldGoal::HighRpmRace => 165.0,
        ManifoldGoal::Balanced => 185.0,
    }
}

/// What an exhaust velocity means, on the same basis as [`target_exhaust_velocity`].
pub fn exhaust_velocity_verdict(metres_per_second: f64) -> &'static str {
    let v = metres_per_second;

    if v.is_nan() || v <= 0.0 {
        "\u{2014}"
    } else if v < 130.0 {
        "oversized \u{2014} scavenging goes away, torque with it"
    } else if v < 165.0 {
        "large, biased to peak power"
    } else if v < 215.0 {
        "usual for a real header"
    } else if v < 260.0 {
        "small and fast, restrictive up top"
    } else {
        "choked; the primary is the limit"
    }
}

/// Plenum size a goal wants, as a multiple of displacement.
pub fn default_plenum_multiple(goal: ManifoldGoal, induction: Induction) -> f64 {
    match (goal, induction) {
        (ManifoldGoal::QuickSpool, Induction::Turbocharged) => 0.75,
        (ManifoldGoal::QuickSpool, _) => 1.0,
        (ManifoldGoal::HighRpmRace, Induction::Turbocharged) => 1.6,
        (ManifoldGoal::HighRpmRace, _) => 1.8,
        (_, Induction::Turbocharged) => 1.2,
        _ => 1.4,
    }
}

/// The longest runner a goal will accept before going up an order.
pub fn max_runner_for_goal(goal: ManifoldGoal) -> f64 {
    match goal {
        ManifoldGoal::QuickSpool => MAX_PRACTICAL_RUNNER_MM,
        ManifoldGoal::HighRpmRace => 400.0,
        ManifoldGoal::Balanced => 520.0,
    }
}

// ----- putting it together ---------------------------------------------------

/// Every harmonic that could be built, longest first. C# default: `highest = 6`.
pub fn orders(
    speed_of_sound: f64,
    window_deg: f64,
    rpm: f64,
    diameter_mm: f64,
    max_length_mm: f64,
    highest: i32,
) -> Vec<TuningOrder> {
    let mut orders = Vec::new();

    let correction = end_correction_mm(diameter_mm);

    for order in 1..=highest {
        let effective = tuned_length_mm(speed_of_sound, window_deg, order, rpm);

        if effective.is_nan() {
            continue;
        }

        let physical = effective - correction;

        orders.push(TuningOrder {
            order,
            effective_length_mm: effective,
            length_mm: physical,
            rpm,
            practical: physical >= MIN_PRACTICAL_RUNNER_MM && physical <= max_length_mm,
        });
    }

    orders
}

/// The order to build: the longest practical one (or the shortest, with `prefer_shortest`,
/// which is what a turbocharged engine wants), or failing that the closest thing to it.
pub fn recommend(orders: &[TuningOrder], max_length_mm: f64, prefer_shortest: bool) -> TuningOrder {
    if orders.is_empty() {
        return TuningOrder::default();
    }

    if prefer_shortest {
        for o in orders.iter().rev() {
            if o.practical {
                return *o;
            }
        }
    } else {
        for o in orders {
            if o.practical {
                return *o;
            }
        }
    }

    // Nothing fits. The least bad is whichever is nearest the limit it missed.
    let mut best = orders[0];
    let mut best_miss = f64::MAX;

    for o in orders {
        let miss = if o.length_mm > max_length_mm {
            o.length_mm - max_length_mm
        } else {
            MIN_PRACTICAL_RUNNER_MM - o.length_mm
        };

        if miss < best_miss {
            best_miss = miss;
            best = *o;
        }
    }

    best
}

/// The whole manifold, intake and exhaust, worked out together, because they share the
/// engine: the same filling, the same speeds, the same charge temperature.
pub fn plan(spec: &ManifoldSpec) -> ManifoldPlan {
    let mut warnings = Vec::new();

    let displacement_cc = spec.litres * 1_000.0;
    let swept_cc = if spec.cylinders > 0 {
        displacement_cc / f64::from(spec.cylinders)
    } else {
        f64::NAN
    };
    let cylinders = f64::from(spec.cylinders);

    // ----- intake -----
    let a_in = speed_of_sound_air(spec.intake_air_celsius);
    let intake_rpm = intake_tuning_rpm(spec.goal, spec.peak_torque_rpm, spec.peak_power_rpm);
    let intake_window = spec.intake_duration_deg;

    let target_velocity = target_intake_velocity(spec.goal);

    let runner_area = if spec.intake_runner_diameter_mm > 0.0 {
        area_mm2(spec.intake_runner_diameter_mm)
    } else {
        area_for_velocity(
            swept_cc,
            spec.volumetric_efficiency,
            spec.peak_power_rpm,
            intake_window,
            target_velocity,
        )
    };

    let runner_diameter = if spec.intake_runner_diameter_mm > 0.0 {
        spec.intake_runner_diameter_mm
    } else {
        diameter_mm(runner_area)
    };

    let max_runner = max_runner_for_goal(spec.goal);

    let intake_orders = orders(a_in, intake_window, intake_rpm, runner_diameter, max_runner, 6);

    // A turbocharged build chosen for response takes the shortest pipe that packages rather
    // than the longest: the pulse it gives up is worth a few per cent, and the volume it saves
    // is charge that no longer has to be pressurised before boost arrives.
    let short_runners = spec.induction == Induction::Turbocharged && spec.goal == ManifoldGoal::QuickSpool;

    let intake_pick = recommend(&intake_orders, max_runner, short_runners);

    let plenum_multiple = if spec.plenum_multiple > 0.0 {
        spec.plenum_multiple
    } else {
        default_plenum_multiple(spec.goal, spec.induction)
    };

    let plenum_cc = plenum_volume_cc(displacement_cc, plenum_multiple);

    let effective_cylinder = effective_cylinder_volume_cc(swept_cc, spec.compression_ratio);
    let helmholtz = helmholtz_hz(a_in, runner_area, intake_pick.effective_length_mm, effective_cylinder);

    let velocity_at_peak_power = port_velocity(
        swept_cc,
        spec.volumetric_efficiency,
        spec.peak_power_rpm,
        intake_window,
        runner_area,
    );

    let intake = IntakePlan {
        speed_of_sound: a_in,
        tuned_rpm: intake_rpm,
        orders: intake_orders,
        recommended: intake_pick,
        runner_diameter_mm: runner_diameter,
        runner_area_mm2: runner_area,
        runner_volume_cc: pipe_volume_cc(intake_pick.length_mm, runner_diameter),
        velocity_at_peak_power,
        velocity_at_peak_torque: port_velocity(
            swept_cc,
            spec.volumetric_efficiency,
            spec.peak_torque_rpm,
            intake_window,
            runner_area,
        ),
        mach_at_peak_power: velocity_at_peak_power / a_in,
        plenum_volume_cc: plenum_cc,
        plenum_multiple,
        tract_volume_cc: plenum_cc + (pipe_volume_cc(intake_pick.length_mm, runner_diameter) * cylinders),
        helmholtz_hz: helmholtz,
        helmholtz_ratio: helmholtz_ratio(helmholtz, intake_rpm),
    };

    // ----- exhaust -----
    let a_ex = speed_of_sound_exhaust(spec.exhaust_celsius);
    let exhaust_window = exhaust_wave_window_deg(spec.exhaust_duration_deg);

    // A turbine reflects nothing useful back down the primary, so a turbo manifold is tuned
    // for the shortest path and the least volume rather than for a length. The wave answer is
    // still shown, as information.
    let exhaust_rpm = if spec.induction == Induction::Turbocharged {
        spec.peak_torque_rpm
    } else {
        intake_tuning_rpm(spec.goal, spec.peak_torque_rpm, spec.peak_power_rpm)
    };

    let exhaust_volume_cc = exhaust_volume_per_cycle_cc(
        swept_cc,
        spec.volumetric_efficiency,
        spec.manifold_kpa,
        spec.intake_air_celsius,
        spec.exhaust_celsius,
        spec.exhaust_back_pressure_kpa,
        spec.afr,
    );

    // Velocity from the real gas volume rather than from displacement: the window here is the
    // exhaust duration, and the gas is three times the volume it was on the way in.
    let exhaust_target_velocity = target_exhaust_velocity(spec.goal);

    let primary_area = if spec.exhaust_primary_diameter_mm > 0.0 {
        area_mm2(spec.exhaust_primary_diameter_mm)
    } else {
        // Same statement as area_for_velocity, with the expanded volume standing in for
        // swept volume times filling.
        let m3 = exhaust_volume_cc / 1e6;
        if spec.peak_power_rpm > 0.0 && spec.exhaust_duration_deg > 0.0 {
            1e6 * 6.0 * spec.peak_power_rpm * m3 / (spec.exhaust_duration_deg * exhaust_target_velocity)
        } else {
            f64::NAN
        }
    };

    let primary_diameter = if spec.exhaust_primary_diameter_mm > 0.0 {
        spec.exhaust_primary_diameter_mm
    } else {
        diameter_mm(primary_area)
    };

    let exhaust_orders = orders(
        a_ex,
        exhaust_window,
        exhaust_rpm,
        primary_diameter,
        MAX_PRACTICAL_PRIMARY_MM,
        6,
    );

    let exhaust_pick = recommend(&exhaust_orders, MAX_PRACTICAL_PRIMARY_MM, false);

    let primary_volume = pipe_volume_cc(exhaust_pick.length_mm, primary_diameter);

    let exhaust_velocity = if primary_area > 0.0 && spec.peak_power_rpm > 0.0 && spec.exhaust_duration_deg > 0.0 {
        6.0 * spec.peak_power_rpm * (exhaust_volume_cc / 1e6) / (spec.exhaust_duration_deg * (primary_area / 1e6))
    } else {
        f64::NAN
    };

    let exhaust = ExhaustPlan {
        speed_of_sound: a_ex,
        tuned_rpm: exhaust_rpm,
        wave_window_deg: exhaust_window,
        orders: exhaust_orders,
        recommended: exhaust_pick,
        primary_diameter_mm: primary_diameter,
        primary_volume_cc: primary_volume,
        total_primary_volume_cc: primary_volume * cylinders,
        velocity_at_peak_power: exhaust_velocity,
        collector_diameter_mm: collector_diameter_mm(primary_diameter, spec.cylinders),
        collector_length_mm: collector_length_mm(exhaust_pick.length_mm),
        bell_length_mm: bell_primary_length_inches(spec.exhaust_duration_deg, exhaust_rpm) * 25.4,
    };

    add_warnings(&mut warnings, spec, &intake, &exhaust);

    ManifoldPlan {
        intake,
        exhaust,
        warnings,
    }
}

fn warn(severity: &'static str, text: String) -> RecipeWarning {
    RecipeWarning { severity, text }
}

/// What is worth saying out loud about a design that the numbers alone do not.
fn add_warnings(warnings: &mut Vec<RecipeWarning>, spec: &ManifoldSpec, intake: &IntakePlan, exhaust: &ExhaustPlan) {
    let n0 = |v: f64| fmt::f64(v, "N0");

    if spec.peak_power_rpm <= spec.peak_torque_rpm {
        warnings.push(warn(
            "stop",
            "Peak power is at or below peak torque. One of the two engine speeds is wrong, \
             and every length here is tuned off the pair."
                .to_string(),
        ));
    }

    if spec.intake_duration_deg < 180.0 || spec.intake_duration_deg > 320.0 {
        warnings.push(warn(
            "stop",
            format!(
                "An inlet duration of {}\u{b0} is outside anything a four-stroke \
                 runs. Lengths scale directly with it, so the answer will be wrong by however much it is.",
                n0(spec.intake_duration_deg)
            ),
        ));
    }

    if spec.exhaust_duration_deg <= 180.0 {
        warnings.push(warn(
            "stop",
            "The exhaust duration has to exceed 180\u{b0} \u{2014} the valve opens before bottom dead centre. \
             No wave window can be worked out from this."
                .to_string(),
        ));
    }

    if !intake.recommended.practical && intake.recommended.length_mm > 0.0 {
        warnings.push(warn(
            "watch",
            format!(
                "No harmonic gives a runner that fits: the closest is {} mm \
                 at order {}. Tuning this low usually means accepting a shorter \
                 pipe at a higher order and a weaker pulse.",
                n0(intake.recommended.length_mm),
                fmt::int(intake.recommended.order, "")
            ),
        ));
    }

    if intake.velocity_at_peak_power > 0.0 && intake.velocity_at_peak_power < 60.0 {
        warnings.push(warn(
            "watch",
            format!(
                "Mean port velocity at peak power is only {} m/s. The runner \
                 is larger than the engine can use, which costs low-speed torque and throttle response \
                 for top end it may never reach.",
                n0(intake.velocity_at_peak_power)
            ),
        ));
    }

    if intake.velocity_at_peak_power > 130.0 {
        warnings.push(warn(
            "watch",
            format!(
                "Mean port velocity reaches {} m/s at peak power, and the \
                 instantaneous peak is around half again that. The port will be the limit before the \
                 engine is.",
                n0(intake.velocity_at_peak_power)
            ),
        ));
    }

    if intake.mach_at_peak_power > 0.35 {
        warnings.push(warn(
            "watch",
            format!(
                "Mean Mach {} in the runner. Peaks run near twice the mean, so \
                 this is close to choking at mid-lift.",
                fmt::f64(intake.mach_at_peak_power, "N2")
            ),
        ));
    }

    if exhaust.velocity_at_peak_power > 0.0 && exhaust.velocity_at_peak_power < 130.0 {
        warnings.push(warn(
            "watch",
            format!(
                "Gas leaves the primary at only {} m/s at peak power, where \
                 headers on real engines run 168 to 211. A primary this large stops scavenging and takes \
                 the mid-range with it.",
                n0(exhaust.velocity_at_peak_power)
            ),
        ));
    }

    if exhaust.velocity_at_peak_power > 260.0 {
        warnings.push(warn(
            "watch",
            format!(
                "Gas reaches {} m/s in the primary. That is past anything \
                 built, and the header will be the limit before the engine is.",
                n0(exhaust.velocity_at_peak_power)
            ),
        ));
    }

    if intake.helmholtz_ratio > 0.0 && (intake.helmholtz_ratio < 2.0 || intake.helmholtz_ratio > 5.5) {
        warnings.push(warn(
            "note",
            format!(
                "Plenum and runners resonate at a ratio of {} against the tuned \
                 speed, where a consistently tuned design sits between about 3 and 4. Usually the plenum \
                 is the odd one out \u{2014} check its volume before changing a length.",
                fmt::f64(intake.helmholtz_ratio, "N1")
            ),
        ));
    }

    if spec.induction == Induction::Turbocharged {
        warnings.push(warn(
            "note",
            format!(
                "On a turbocharged engine the exhaust length below is information rather than a target. A \
                 turbine does not reflect a usable wave back down the primary, so the manifold is built \
                 short and equal-length instead \u{2014} the {} cc total volume \
                 is what decides how fast it spools.",
                n0(exhaust.total_primary_volume_cc)
            ),
        ));

        // The short runner is a quick-spool choice and not a turbocharged one; see
        // `short_runners` in `plan`, which asks for both.
        let length_note = if spec.goal == ManifoldGoal::QuickSpool {
            "and a shorter runner is chosen here for that reason rather than for its pulse"
        } else {
            "which the longest runner that packages is adding to \u{2014} at this goal the pulse is worth \
             having and the volume is what it costs"
        };

        warnings.push(warn(
            "note",
            format!(
                "The plenum and runners come to {} cc, {}. Keep it in \
                 proportion though: on an ordinary front-mount installation the intercooler core and its \
                 two pipe runs are around three quarters of what the compressor has to pressurise, and \
                 this page cannot see any of it. Plenum multiple is a real lever on spool and a small one.",
                n0(intake.tract_volume_cc),
                length_note
            ),
        ));

        if spec.exhaust_back_pressure_kpa <= tm::ATMOSPHERIC_KPA * 1.05 {
            warnings.push(warn(
                "watch",
                "Exhaust back pressure is set at about atmospheric, which no turbine produces. Manifold \
                 pressure typically runs at or above boost, and the gas is denser for it \u{2014} the primary \
                 sizing here will read large until that is set."
                    .to_string(),
            ));
        }
    } else if spec.manifold_kpa > tm::ATMOSPHERIC_KPA * 1.05 {
        warnings.push(warn(
            "watch",
            "Manifold pressure is above atmospheric on an engine marked naturally aspirated. The \
             exhaust sizing uses it for the mass flow, so it will read larger than it should."
                .to_string(),
        ));
    }

    if spec.induction == Induction::NaturallyAspirated && exhaust.bell_length_mm > 0.0 {
        let gap = (exhaust.recommended.length_mm - exhaust.bell_length_mm).abs();

        if exhaust.recommended.order == 2 && gap / exhaust.bell_length_mm > 0.15 {
            warnings.push(warn(
                "note",
                format!(
                    "This primary comes out {} mm where Bell's empirical \
                     formula gives {} mm. The two normally agree within a few per \
                     cent; the gap is almost always the mean gas temperature being set away from 600 \u{b0}C.",
                    n0(exhaust.recommended.length_mm),
                    n0(exhaust.bell_length_mm)
                ),
            ));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::calc::cam_profiles::{self, CamProfile};
    use crate::calc::engine_families::{self, EngineFamily};

    const ATM: f64 = tm::ATMOSPHERIC_KPA;

    /// xunit's `Assert.Equal(expected, actual, precision)`: both rounded to `precision` decimals.
    fn eq(expected: f64, actual: f64, precision: i32) -> bool {
        let scale = 10f64.powi(precision);
        (expected * scale).round() == (actual * scale).round()
    }

    fn in_range(v: f64, lo: f64, hi: f64) -> bool {
        v >= lo && v <= hi
    }

    fn ladder() -> Vec<CamProfile> {
        cam_profiles::ALL.iter().copied().filter(|c| !c.is_custom()).collect()
    }

    fn families() -> Vec<EngineFamily> {
        engine_families::ALL
            .iter()
            .copied()
            .filter(|f| !f.is_custom())
            .collect()
    }

    fn has_text(plan: &ManifoldPlan, needle: &str) -> bool {
        plan.warnings.iter().any(|w| w.text.contains(needle))
    }

    fn has_stop(plan: &ManifoldPlan) -> bool {
        plan.warnings.iter().any(|w| w.severity == "stop")
    }

    // ----- the gas ---------------------------------------------------------------

    /// sqrt(gamma R T) at 20 C is the number in the front of every acoustics text: 343.2 m/s.
    #[test]
    fn speed_of_sound_in_air_matches_the_textbook() {
        assert!(eq(343.2, speed_of_sound_air(20.0), 1));
        assert!(eq(331.3, speed_of_sound_air(0.0), 1));
    }

    /// Exhaust is hot, so sound runs through it far faster.
    #[test]
    fn sound_runs_faster_in_exhaust_than_in_intake_charge() {
        let intake = speed_of_sound_air(30.0);
        let exhaust = speed_of_sound_exhaust(600.0);

        assert!(exhaust > intake * 1.5, "intake {intake:.0}, exhaust {exhaust:.0}");
    }

    /// Temperature enters as a square root, so it is a weak lever.
    #[test]
    fn speed_of_sound_follows_the_square_root_of_absolute_temperature() {
        let cold = speed_of_sound_air(-123.15);
        let hot = speed_of_sound_air(26.85);

        assert!(eq(2f64.sqrt(), hot / cold, 3));
    }

    // ----- the wave equation -----------------------------------------------------

    /// The relation worked by hand: 345 x 240 / (12 x 1 x 6000) = 1.15 m.
    #[test]
    fn tuned_length_is_speed_times_window_over_twelve_times_order_times_rpm() {
        assert!(eq(1_150.0, tuned_length_mm(345.0, 240.0, 1, 6_000.0), 0));
        assert!(eq(383.33, tuned_length_mm(345.0, 240.0, 3, 6_000.0), 2));
    }

    /// Length and engine speed are the same statement rearranged, at every order.
    #[test]
    fn length_and_rpm_are_the_same_statement_both_ways() {
        for order in [1, 2, 3, 4, 6] {
            let a = speed_of_sound_air(35.0);
            let window = 235.0;
            let rpm = 6_800.0;

            let length = tuned_length_mm(a, window, order, rpm);
            let back = tuned_rpm(a, window, order, length);

            assert!(eq(rpm, back, 6), "order {order}");
        }
    }

    /// A higher order is a shorter pipe, in exact inverse proportion.
    #[test]
    fn each_order_is_exactly_the_reciprocal_of_the_last() {
        let first = tuned_length_mm(340.0, 240.0, 1, 7_000.0);

        for order in 2..=6 {
            assert!(eq(
                first / f64::from(order),
                tuned_length_mm(340.0, 240.0, order, 7_000.0),
                8
            ));
        }
    }

    /// Tuning lower down means a longer pipe. The direction is the design.
    #[test]
    fn tuning_lower_wants_a_longer_pipe() {
        let low = tuned_length_mm(345.0, 240.0, 3, 3_500.0);
        let high = tuned_length_mm(345.0, 240.0, 3, 8_000.0);

        assert!(low > high, "3,500 rpm gave {low:.0} mm, 8,000 rpm gave {high:.0} mm");
    }

    /// The exhaust window runs from the valve cracking open to overlap top dead centre: 90 + ED/2.
    #[test]
    fn exhaust_window_runs_from_valve_opening_to_overlap() {
        assert!(eq(230.0, exhaust_wave_window_deg(280.0), 6));
        assert!(eq(215.0, exhaust_wave_window_deg(250.0), 6));

        // A valve that opened after bottom dead centre would have no window at all.
        assert!(exhaust_wave_window_deg(170.0).is_nan());
    }

    // ----- the cross-check that matters ------------------------------------------

    /// The wave equation against Bell's empirical primary length, across the whole range of
    /// cams and engine speeds anyone would build. They land within seven per cent everywhere.
    #[test]
    fn exhaust_length_agrees_with_bell() {
        for (duration, rpm) in [
            (280.0, 6_500.0),
            (270.0, 7_000.0),
            (300.0, 6_000.0),
            (250.0, 5_500.0),
            (290.0, 7_500.0),
            (260.0, 8_000.0),
        ] {
            let a = speed_of_sound_exhaust(600.0);
            let window = exhaust_wave_window_deg(duration);

            let derived = tuned_length_mm(a, window, 2, rpm);
            let bell = bell_primary_length_inches(duration, rpm) * 25.4;

            let error = (derived - bell).abs() / bell;

            assert!(
                error < 0.07,
                "{duration} deg at {rpm} rpm: derived {derived:.0} mm against Bell's {bell:.0} mm"
            );
        }
    }

    /// Solved backwards, Bell's constant implies a mean primary gas temperature in the same
    /// band a thermocouple down a header actually reads.
    #[test]
    fn bells_constant_implies_a_plausible_gas_temperature() {
        for (duration, rpm) in [(280.0, 6_500.0), (270.0, 7_000.0), (300.0, 6_000.0), (250.0, 5_500.0)] {
            let window = exhaust_wave_window_deg(duration);
            let bell_mm = bell_primary_length_inches(duration, rpm) * 25.4;

            // Invert L = a*theta/(12*K*rpm) for the speed of sound, then sqrt(gamma R T) for T.
            let speed = 12.0 * 2.0 * rpm * (bell_mm / 1_000.0) / window;
            let kelvin = speed * speed / (GAMMA_EXHAUST * GAS_CONSTANT_EXHAUST);

            assert!(in_range(kelvin - 273.15, 500.0, 700.0), "{duration}/{rpm}");
        }
    }

    // ----- port velocity ---------------------------------------------------------

    /// Velocity by hand: a 500 cc cylinder at 7,000 rpm through a 240 degree window and
    /// 9.72 cm^2 is about 90 m/s.
    #[test]
    fn port_velocity_is_the_charge_divided_by_the_window_and_the_area() {
        let velocity = port_velocity(500.0, 100.0, 7_000.0, 240.0, 972.2);

        assert!(eq(90.0, velocity, 0));
    }

    /// Sizing a port for a velocity and then measuring it returns the number asked for.
    #[test]
    fn sizing_for_a_velocity_then_measuring_it_returns_it() {
        for target in [85.0, 95.0, 105.0, 130.0] {
            let area = area_for_velocity(500.0, 100.0, 7_000.0, 240.0, target);
            let back = port_velocity(500.0, 100.0, 7_000.0, 240.0, area);

            assert!(eq(target, back, 8), "{target}");
        }
    }

    /// A 2.0 litre four at 7,000 rpm wants a runner in the middle thirties of millimetres.
    #[test]
    fn a_two_litre_four_gets_a_runner_a_real_engine_would_recognise() {
        let area = area_for_velocity(500.0, 100.0, 7_000.0, 240.0, 95.0);
        let diameter = diameter_mm(area);

        assert!(in_range(diameter, 33.0, 38.0));
    }

    /// Area and diameter are each other's inverse, at any size.
    #[test]
    fn area_and_diameter_round_trip() {
        for diameter in [28.0, 35.5, 48.0] {
            assert!(eq(diameter, diameter_mm(area_mm2(diameter)), 9));
        }
    }

    /// A smaller port must move the gas faster.
    #[test]
    fn a_smaller_port_raises_velocity() {
        let small = port_velocity(500.0, 100.0, 7_000.0, 240.0, area_mm2(32.0));
        let large = port_velocity(500.0, 100.0, 7_000.0, 240.0, area_mm2(42.0));

        assert!(small > large);
    }

    // ----- plenum and Helmholtz --------------------------------------------------

    /// Engelman's effective volume is the mean of the cylinder through its stroke.
    #[test]
    fn effective_cylinder_volume_is_the_mean_through_the_stroke() {
        let effective = effective_cylinder_volume_cc(500.0, 10.0);

        let top = 500.0 / (10.0 - 1.0);
        let bottom = top + 500.0;

        assert!(eq((top + bottom) / 2.0, effective, 9));
        assert!(eq(305.56, effective, 2));
    }

    /// More compression is less gas to spring against, so the system resonates higher.
    #[test]
    fn more_compression_raises_the_resonance() {
        let low = effective_cylinder_volume_cc(500.0, 8.0);
        let high = effective_cylinder_volume_cc(500.0, 14.0);

        assert!(high < low);
    }

    /// The Helmholtz frequency moves the way the physical picture says.
    #[test]
    fn helmholtz_moves_the_way_the_resonator_says_it_should() {
        let baseline = helmholtz_hz(345.0, 1_000.0, 400.0, 300.0);

        assert!(helmholtz_hz(345.0, 1_000.0, 800.0, 300.0) < baseline, "longer neck");
        assert!(helmholtz_hz(345.0, 1_000.0, 400.0, 600.0) < baseline, "bigger volume");
        assert!(helmholtz_hz(345.0, 2_000.0, 400.0, 300.0) > baseline, "fatter neck");

        // Halving both area and length together leaves it exactly where it was.
        assert!(eq(baseline, helmholtz_hz(345.0, 500.0, 200.0, 300.0), 9));
    }

    /// A design tuned by the wave equation lands in the band the documentation claims for the
    /// Helmholtz ratio.
    #[test]
    fn a_wave_tuned_design_lands_in_the_claimed_helmholtz_band() {
        for (torque, power) in [(4_500.0, 7_000.0), (3_500.0, 6_500.0), (6_000.0, 9_000.0)] {
            let p = plan(&ManifoldSpec {
                peak_torque_rpm: torque,
                peak_power_rpm: power,
                ..ManifoldSpec::default()
            });

            assert!(in_range(p.intake.helmholtz_ratio, 2.0, 5.5), "{torque}/{power}");
        }
    }

    // ----- exhaust gas volume ----------------------------------------------------

    /// Hot gas takes up more room: roughly the ratio of the absolute temperatures.
    #[test]
    fn exhaust_gas_expands_roughly_with_absolute_temperature() {
        let volume = exhaust_volume_per_cycle_cc(500.0, 100.0, ATM, 30.0, 600.0, ATM, 12.5);

        let expansion = (600.0 + 273.15) / (30.0 + 273.15);

        // The fuel adds about a twelfth of the air mass on top of the expansion.
        assert!(in_range(volume, 500.0 * expansion, 500.0 * expansion * 1.15));
    }

    /// Back pressure squeezes the gas back down, so a turbine's manifold flows less volume.
    #[test]
    fn back_pressure_shrinks_the_gas_volume() {
        let open = exhaust_volume_per_cycle_cc(500.0, 100.0, ATM, 30.0, 600.0, ATM, 12.5);
        let behind_turbine = exhaust_volume_per_cycle_cc(500.0, 100.0, ATM, 30.0, 600.0, ATM * 2.0, 12.5);

        assert!(eq(open / 2.0, behind_turbine, 6));
    }

    /// The exhaust velocity target is calibrated against headers that exist: the calculator
    /// must land within a quarter inch of what they actually run.
    #[test]
    fn primary_sizing_lands_on_the_tube_such_engines_actually_run() {
        for (litres, cylinders, power_rpm, real_primary_mm) in [
            (2.0, 4, 7_000.0, 41.3),
            (5.7, 8, 6_000.0, 44.5),
            (1.6, 4, 8_000.0, 38.1),
            (6.2, 8, 6_500.0, 47.6),
        ] {
            let p = plan(&ManifoldSpec {
                litres,
                cylinders,
                goal: ManifoldGoal::Balanced,
                peak_power_rpm: power_rpm,
                peak_torque_rpm: power_rpm * 0.65,
                ..ManifoldSpec::default()
            });

            assert!(
                in_range(
                    p.exhaust.primary_diameter_mm,
                    real_primary_mm - 6.35,
                    real_primary_mm + 6.35
                ),
                "{litres} L: {}",
                p.exhaust.primary_diameter_mm
            );
        }
    }

    /// The velocities those real headers imply all fall inside the band the verdict calls
    /// ordinary.
    #[test]
    fn real_header_velocities_read_as_ordinary() {
        for velocity in [168.0, 190.0, 211.0] {
            assert!(exhaust_velocity_verdict(velocity).contains("usual for a real header"));
        }
    }

    /// The exhaust verdict must not simply repeat the intake one: the bands differ.
    #[test]
    fn intake_and_exhaust_verdicts_use_different_bands() {
        // 190 m/s is ordinary in a primary and choked in a runner.
        assert!(exhaust_velocity_verdict(190.0).contains("usual for a real header"));
        assert!(velocity_verdict(190.0).contains("choked"));
    }

    /// A collector is bigger than one primary and far smaller than four.
    #[test]
    fn collector_sits_between_one_primary_and_all_of_them() {
        let collector = collector_diameter_mm(44.0, 4);

        assert!(collector > 44.0);
        assert!(collector < 88.0);

        // Twice the area is sqrt(2) times the diameter.
        assert!(eq(44.0 * 2f64.sqrt(), collector, 6));
    }

    // ----- end correction --------------------------------------------------------

    /// A pipe behaves longer than it measures, so the length to build is shorter than the
    /// length calculated, never the other way round.
    #[test]
    fn the_pipe_to_build_is_shorter_than_the_acoustic_length() {
        let list = orders(345.0, 240.0, 6_000.0, 40.0, MAX_PRACTICAL_RUNNER_MM, 6);

        for o in &list {
            assert!(o.length_mm < o.effective_length_mm, "order {}", o.order);
        }

        // And the gap is the end correction, which is a fixed fraction of the bore.
        assert!(eq(FLANGED_END_CORRECTION * 40.0 / 2.0, end_correction_mm(40.0), 9));
    }

    // ----- goals -----------------------------------------------------------------

    /// Spool wants a small fast port, race wants a big one, balanced sits between.
    #[test]
    fn the_three_goals_differ_in_the_direction_they_claim() {
        let spool = target_intake_velocity(ManifoldGoal::QuickSpool);
        let balanced = target_intake_velocity(ManifoldGoal::Balanced);
        let race = target_intake_velocity(ManifoldGoal::HighRpmRace);

        assert!(spool > balanced && balanced > race);
    }

    /// A spool build tunes at the torque peak and a race build at the power peak.
    #[test]
    fn spool_tunes_low_and_race_tunes_high() {
        let torque = 3_500.0;
        let power = 7_500.0;

        assert_eq!(torque, intake_tuning_rpm(ManifoldGoal::QuickSpool, torque, power));
        assert_eq!(power, intake_tuning_rpm(ManifoldGoal::HighRpmRace, torque, power));

        let middle = intake_tuning_rpm(ManifoldGoal::Balanced, torque, power);
        assert!(in_range(middle, torque, power));

        assert!(
            default_plenum_multiple(ManifoldGoal::QuickSpool, Induction::Turbocharged)
                < default_plenum_multiple(ManifoldGoal::HighRpmRace, Induction::Turbocharged)
        );
    }

    /// A spool build must come out with a smaller, faster port than a race build.
    #[test]
    fn a_spool_build_gets_a_smaller_port_than_a_race_build() {
        let engine = ManifoldSpec {
            peak_torque_rpm: 3_500.0,
            peak_power_rpm: 7_500.0,
            ..ManifoldSpec::default()
        };

        let spool = plan(&ManifoldSpec {
            goal: ManifoldGoal::QuickSpool,
            ..engine
        });
        let race = plan(&ManifoldSpec {
            goal: ManifoldGoal::HighRpmRace,
            ..engine
        });

        assert!(spool.intake.runner_diameter_mm < race.intake.runner_diameter_mm);
        assert!(spool.intake.plenum_volume_cc < race.intake.plenum_volume_cc);
    }

    // ----- choosing an order -----------------------------------------------------

    /// The recommendation has to be one that fits in a car when any of them does, and it
    /// should be the longest such.
    #[test]
    fn the_recommendation_is_the_longest_one_that_fits() {
        let list = orders(345.0, 240.0, 6_000.0, 38.0, 520.0, 6);

        let pick = recommend(&list, 520.0, false);

        assert!(pick.practical);
        assert!(in_range(pick.length_mm, MIN_PRACTICAL_RUNNER_MM, 520.0));

        // Nothing longer than the pick may also have fitted.
        for o in &list {
            if o.order < pick.order {
                assert!(!o.practical, "order {} fitted and was passed over", o.order);
            }
        }
    }

    /// A turbocharged build chosen to spool takes the shortest pipe that packages, where an
    /// atmospheric one takes the longest. Same goal, opposite answer.
    #[test]
    fn spool_takes_the_short_runner_where_atmospheric_takes_the_long() {
        let engine = ManifoldSpec {
            goal: ManifoldGoal::QuickSpool,
            peak_torque_rpm: 3_500.0,
            peak_power_rpm: 7_000.0,
            ..ManifoldSpec::default()
        };

        let atmospheric = plan(&engine);

        let boosted = plan(&ManifoldSpec {
            induction: Induction::Turbocharged,
            manifold_kpa: ATM + 150.0,
            exhaust_back_pressure_kpa: ATM + 170.0,
            ..engine
        });

        assert!(boosted.intake.recommended.length_mm < atmospheric.intake.recommended.length_mm);

        // Both still have to be buildable.
        assert!(boosted.intake.recommended.practical);
        assert!(atmospheric.intake.recommended.practical);
    }

    /// Preferring the shortest must still respect what packages, and must pick a higher order
    /// than preferring the longest does.
    #[test]
    fn preferring_the_shortest_still_only_takes_one_that_fits() {
        let list = orders(345.0, 240.0, 4_000.0, 34.0, 650.0, 6);

        let longest = recommend(&list, 650.0, false);
        let shortest = recommend(&list, 650.0, true);

        assert!(shortest.practical);
        assert!(longest.practical);
        assert!(shortest.order > longest.order);
        assert!(shortest.length_mm < longest.length_mm);
        assert!(in_range(shortest.length_mm, MIN_PRACTICAL_RUNNER_MM, 650.0));
    }

    /// The tract is the plenum plus every runner, and it has to add up.
    #[test]
    fn the_tract_is_the_plenum_plus_every_runner() {
        let p = plan(&ManifoldSpec {
            cylinders: 4,
            ..ManifoldSpec::default()
        });

        assert!(eq(
            p.intake.plenum_volume_cc + (p.intake.runner_volume_cc * 4.0),
            p.intake.tract_volume_cc,
            6
        ));
    }

    /// A smaller plenum multiple really is a smaller plenum and a smaller tract.
    #[test]
    fn a_smaller_multiple_is_a_smaller_plenum_and_tract() {
        let engine = ManifoldSpec {
            induction: Induction::Turbocharged,
            ..ManifoldSpec::default()
        };

        let small = plan(&ManifoldSpec {
            plenum_multiple: 0.75,
            ..engine
        });
        let large = plan(&ManifoldSpec {
            plenum_multiple: 1.5,
            ..engine
        });

        assert!(eq(
            small.intake.plenum_volume_cc * 2.0,
            large.intake.plenum_volume_cc,
            6
        ));
        assert!(small.intake.tract_volume_cc < large.intake.tract_volume_cc);
    }

    /// On a typical installation the manifold is about a quarter of what has to be
    /// pressurised, so trimming the plenum moves a couple of per cent of the total.
    #[test]
    fn the_manifold_is_about_a_quarter_of_what_has_to_be_pressurised() {
        let inter_cooler_and_piping = 10_000.0;

        let turbo = ManifoldSpec {
            induction: Induction::Turbocharged,
            goal: ManifoldGoal::QuickSpool,
            manifold_kpa: ATM + 150.0,
            exhaust_back_pressure_kpa: ATM + 170.0,
            ..ManifoldSpec::default()
        };

        let share = tract_share_of_typical_installation(plan(&turbo).intake.tract_volume_cc, inter_cooler_and_piping);

        assert!(in_range(share, 0.15, 0.35));

        // And the whole plenum decision moves only a few per cent of the total.
        let lean = plan(&ManifoldSpec {
            plenum_multiple: 0.75,
            ..turbo
        })
        .intake
        .tract_volume_cc;
        let fat = plan(&ManifoldSpec {
            plenum_multiple: 0.90,
            ..turbo
        })
        .intake
        .tract_volume_cc;

        assert!(in_range((fat - lean) / (fat + inter_cooler_and_piping), 0.005, 0.05));
    }

    /// Plenum advice reads differently boosted, because the volume costs differently.
    #[test]
    fn plenum_advice_depends_on_induction() {
        assert_ne!(
            plenum_verdict(0.75, Induction::Turbocharged),
            plenum_verdict(0.75, Induction::NaturallyAspirated)
        );

        for m in PLENUM_MULTIPLES {
            assert!(!plenum_verdict(m, Induction::Turbocharged).trim().is_empty());
            assert_ne!("\u{2014}", plenum_verdict(m, Induction::NaturallyAspirated));
        }

        assert_eq!("\u{2014}", plenum_verdict(0.0, Induction::Turbocharged));
    }

    /// Every step in the table has to say something different from its neighbours.
    #[test]
    fn every_plenum_step_reads_differently_from_the_last() {
        for induction in Induction::ALL {
            let seen: Vec<&str> = PLENUM_MULTIPLES.iter().map(|&m| plenum_verdict(m, induction)).collect();

            let mut distinct = seen.clone();
            distinct.sort_unstable();
            distinct.dedup();

            assert_eq!(seen.len(), distinct.len(), "{induction:?}");
        }
    }

    /// The multiples the goals actually pick must each be findable, because the table marks
    /// the row in force by matching on the number.
    #[test]
    fn every_goals_own_multiple_gets_its_own_verdict() {
        for goal in ManifoldGoal::ALL {
            for induction in Induction::ALL {
                let multiple = default_plenum_multiple(goal, induction);

                assert!(in_range(multiple, 0.5, 2.0));
                assert_ne!("\u{2014}", plenum_verdict(multiple, induction));
            }
        }
    }

    /// A low torque peak wants a runner longer than any car can hold. The calculator must not
    /// silently hand back a metre of pipe as though it were buildable.
    #[test]
    fn an_impossibly_low_tuning_point_is_flagged_rather_than_quietly_returned() {
        let p = plan(&ManifoldSpec {
            goal: ManifoldGoal::QuickSpool,
            peak_torque_rpm: 1_800.0,
            peak_power_rpm: 5_000.0,
            induction: Induction::Turbocharged,
            ..ManifoldSpec::default()
        });

        assert!(p.intake.recommended.length_mm <= MAX_PRACTICAL_RUNNER_MM || !p.intake.recommended.practical);
    }

    // ----- the plan as a whole ---------------------------------------------------

    /// A believable 2.0 litre naturally aspirated build, checked against what such an engine
    /// actually carries.
    #[test]
    fn a_two_litre_na_build_comes_out_the_size_such_engines_are() {
        let p = plan(&ManifoldSpec {
            litres: 2.0,
            cylinders: 4,
            goal: ManifoldGoal::Balanced,
            peak_torque_rpm: 4_500.0,
            peak_power_rpm: 7_000.0,
            compression_ratio: 11.0,
            ..ManifoldSpec::default()
        });

        assert!(in_range(p.intake.runner_diameter_mm, 32.0, 40.0));
        assert!(in_range(p.intake.recommended.length_mm, 150.0, 520.0));
        assert!(in_range(p.intake.plenum_volume_cc, 2_000.0, 4_000.0));
        assert!(in_range(p.intake.velocity_at_peak_power, 85.0, 105.0));

        assert!(in_range(p.exhaust.primary_diameter_mm, 36.0, 52.0));
        assert!(in_range(p.exhaust.recommended.length_mm, 400.0, 1_100.0));
        assert!(in_range(p.exhaust.collector_diameter_mm, 50.0, 75.0));

        assert!(!has_stop(&p));
    }

    fn turbo_spec(goal: ManifoldGoal) -> ManifoldSpec {
        ManifoldSpec {
            litres: 2.0,
            cylinders: 4,
            induction: Induction::Turbocharged,
            goal,
            peak_torque_rpm: 3_500.0,
            peak_power_rpm: 7_000.0,
            manifold_kpa: ATM + 150.0,
            exhaust_back_pressure_kpa: ATM + 170.0,
            volumetric_efficiency: 105.0,
            ..ManifoldSpec::default()
        }
    }

    /// A quick-spool turbo build, the case with the most ways to go wrong. The design it
    /// produces still has to be one somebody could weld.
    #[test]
    fn a_quick_spool_turbo_build_is_still_something_you_could_weld() {
        let p = plan(&turbo_spec(ManifoldGoal::QuickSpool));

        // The runner has to package, even tuning as low as 3,500.
        assert!(p.intake.recommended.practical);

        assert!(in_range(
            p.intake.recommended.length_mm,
            MIN_PRACTICAL_RUNNER_MM,
            MAX_PRACTICAL_RUNNER_MM
        ));

        // A spool build keeps the plenum small, because it is volume to fill.
        assert!(p.intake.plenum_multiple <= 1.0);

        // Boosted, denser gas: the primary must not come out absurd either way.
        assert!(in_range(p.exhaust.primary_diameter_mm, 28.0, 50.0));
        assert!(p.exhaust.total_primary_volume_cc > 0.0);

        // And it must say that the header length is not the lever here.
        assert!(p.warnings.iter().any(|w| w.text.to_lowercase().contains("turbine")));

        assert!(!has_stop(&p));
    }

    /// The note about runner length has to agree with the runner that was actually chosen.
    #[test]
    fn the_runner_length_note_follows_the_goal_rather_than_the_boost() {
        for (goal, shorter) in [
            (ManifoldGoal::QuickSpool, true),
            (ManifoldGoal::Balanced, false),
            (ManifoldGoal::HighRpmRace, false),
        ] {
            let p = plan(&turbo_spec(goal));

            let notes: Vec<&RecipeWarning> = p
                .warnings
                .iter()
                .filter(|w| w.text.contains("plenum and runners come to"))
                .collect();
            assert_eq!(1, notes.len());

            assert_eq!(shorter, notes[0].text.contains("a shorter runner is chosen here"));
        }
    }

    /// Boost makes the exhaust gas denser, so the same engine needs less pipe area behind a
    /// turbine than it does behind an open header.
    #[test]
    fn boosted_gas_needs_less_primary_area_not_more() {
        let atmospheric = ManifoldSpec {
            manifold_kpa: ATM,
            exhaust_back_pressure_kpa: ATM,
            ..ManifoldSpec::default()
        };

        // Twice the manifold pressure and twice the back pressure: the volume flow is unchanged.
        let boosted = ManifoldSpec {
            manifold_kpa: ATM * 2.0,
            exhaust_back_pressure_kpa: ATM * 2.0,
            ..atmospheric
        };

        assert!(eq(
            plan(&atmospheric).exhaust.primary_diameter_mm,
            plan(&boosted).exhaust.primary_diameter_mm,
            6
        ));

        // Boost without the matching back pressure is more volume, so more pipe.
        let free_flowing = ManifoldSpec {
            manifold_kpa: ATM * 2.0,
            ..atmospheric
        };

        assert!(plan(&free_flowing).exhaust.primary_diameter_mm > plan(&atmospheric).exhaust.primary_diameter_mm);
    }

    /// The exhaust is fatter than the intake on the same engine.
    #[test]
    fn the_primary_is_fatter_than_the_runner() {
        let p = plan(&ManifoldSpec::default());

        assert!(p.exhaust.primary_diameter_mm > p.intake.runner_diameter_mm);
    }

    /// Volumes have to be the pipe they describe: pi/4 . d^2 . L, in cc.
    #[test]
    fn pipe_volume_is_the_cylinder_it_describes() {
        // A 40 mm pipe 500 mm long holds pi/4 x 16 cm^2 x 50 cm = 628 cc.
        assert!(eq(628.3, pipe_volume_cc(500.0, 40.0), 1));

        let p = plan(&ManifoldSpec::default());

        assert!(eq(
            pipe_volume_cc(p.intake.recommended.length_mm, p.intake.runner_diameter_mm),
            p.intake.runner_volume_cc,
            9
        ));

        assert!(eq(
            p.exhaust.primary_volume_cc * 4.0,
            p.exhaust.total_primary_volume_cc,
            9
        ));
    }

    // ----- what it refuses to do -------------------------------------------------

    /// Garbage in has to come back as NaN rather than as a number.
    #[test]
    fn nonsense_inputs_return_not_a_number() {
        assert!(tuned_length_mm(0.0, 240.0, 3, 6_000.0).is_nan());
        assert!(tuned_length_mm(345.0, 0.0, 3, 6_000.0).is_nan());
        assert!(tuned_length_mm(345.0, 240.0, 0, 6_000.0).is_nan());
        assert!(tuned_length_mm(345.0, 240.0, 3, 0.0).is_nan());
        assert!(tuned_length_mm(345.0, 240.0, -1, 6_000.0).is_nan());

        assert!(port_velocity(500.0, 100.0, 7_000.0, 240.0, 0.0).is_nan());
        assert!(area_for_velocity(500.0, 100.0, 7_000.0, 240.0, 0.0).is_nan());
        assert!(effective_cylinder_volume_cc(500.0, 1.0).is_nan());
        assert!(speed_of_sound(-300.0, 1.4, 287.0).is_nan());
        assert!(helmholtz_hz(345.0, 0.0, 400.0, 300.0).is_nan());
    }

    /// Nothing here may panic on a spec somebody could plausibly type.
    #[test]
    fn plan_survives_whatever_it_is_given() {
        for (litres, cylinders) in [(0.6, 3), (2.0, 4), (6.2, 8), (0.0, 0)] {
            let p = plan(&ManifoldSpec {
                litres,
                cylinders,
                ..ManifoldSpec::default()
            });

            // The C# asserts non-null; here the plan simply has to come back.
            let _ = (&p.intake, &p.exhaust, &p.warnings);
        }
    }

    // `PlanRejectsANullSpec` has no Rust counterpart: `plan` takes a reference, which cannot
    // be null, so the `ArgumentNullException` the C# guards against is unrepresentable.

    // ----- warnings --------------------------------------------------------------

    /// A power peak below the torque peak is a typo, and every length depends on the pair.
    #[test]
    fn power_peak_below_torque_peak_stops() {
        let p = plan(&ManifoldSpec {
            peak_torque_rpm: 7_000.0,
            peak_power_rpm: 4_000.0,
            ..ManifoldSpec::default()
        });

        assert!(has_stop(&p));
    }

    /// A turbo build is told the exhaust length is not the point.
    #[test]
    fn a_turbo_build_is_told_the_header_length_is_information_only() {
        let p = plan(&ManifoldSpec {
            induction: Induction::Turbocharged,
            manifold_kpa: ATM + 150.0,
            exhaust_back_pressure_kpa: ATM + 170.0,
            ..ManifoldSpec::default()
        });

        assert!(p.warnings.iter().any(|w| w.text.to_lowercase().contains("turbine")));
        assert!(p.exhaust.total_primary_volume_cc > 0.0);
    }

    /// Boost typed against a naturally aspirated engine is a contradiction the exhaust sizing
    /// would otherwise swallow silently.
    #[test]
    fn boost_on_a_naturally_aspirated_engine_is_queried() {
        let p = plan(&ManifoldSpec {
            induction: Induction::NaturallyAspirated,
            manifold_kpa: ATM + 100.0,
            ..ManifoldSpec::default()
        });

        assert!(has_text(&p, "naturally aspirated"));
    }

    /// A port far too big for the engine gets said out loud.
    #[test]
    fn an_oversized_port_is_called_out() {
        let p = plan(&ManifoldSpec {
            litres: 1.0,
            cylinders: 4,
            peak_power_rpm: 6_000.0,
            peak_torque_rpm: 4_000.0,
            intake_runner_diameter_mm: 60.0,
            ..ManifoldSpec::default()
        });

        assert!(has_text(&p, "larger than the engine can use"));
    }

    /// No warning may be produced with an empty message or an unknown severity; they are
    /// rendered straight into the window.
    #[test]
    fn every_warning_is_well_formed() {
        for (goal, induction) in [
            (ManifoldGoal::QuickSpool, Induction::Turbocharged),
            (ManifoldGoal::Balanced, Induction::NaturallyAspirated),
            (ManifoldGoal::HighRpmRace, Induction::NaturallyAspirated),
        ] {
            let p = plan(&ManifoldSpec {
                goal,
                induction,
                ..ManifoldSpec::default()
            });

            for w in &p.warnings {
                assert!(!w.text.trim().is_empty());
                assert!(["note", "watch", "stop"].contains(&w.severity));
                assert!(!w.text.contains('{'));
            }
        }
    }

    // ===== CamProfileTests.cs: the cam presets, which lean on the plan =============

    /// The overlap formula against grinds whose catalogue overlap is known.
    #[test]
    fn overlap_matches_the_catalogue_figure() {
        for (intake, exhaust, lsa, expected) in [
            (252.0, 252.0, 110.0, 32.0),
            (274.0, 286.0, 110.0, 60.0),
            (300.0, 300.0, 106.0, 88.0),
        ] {
            let cam = CamProfile {
                name: "test",
                intake_duration_deg: intake,
                exhaust_duration_deg: exhaust,
                intake_at_fifty_deg: 0.0,
                exhaust_at_fifty_deg: 0.0,
                lobe_separation_deg: lsa,
                charge_celsius: 30.0,
                exhaust_celsius: 600.0,
                note: "",
            };

            assert!(eq(expected, cam.overlap_deg(), 6));
        }
    }

    /// The ladder has to actually be a ladder.
    #[test]
    fn every_step_up_is_a_longer_cam_with_more_overlap() {
        let l = ladder();

        assert_eq!(5, l.len());

        for i in 1..l.len() {
            assert!(l[i].intake_duration_deg > l[i - 1].intake_duration_deg, "{}", l[i].name);
            assert!(
                l[i].exhaust_duration_deg > l[i - 1].exhaust_duration_deg,
                "{}",
                l[i].name
            );
            assert!(l[i].overlap_deg() > l[i - 1].overlap_deg(), "{}", l[i].name);
        }
    }

    /// Seat-to-seat runs somewhere around 44 to 52 crank degrees longer than the figure at
    /// 0.050 in on real grinds.
    #[test]
    fn both_duration_figures_are_consistent_with_a_real_grind() {
        for cam in ladder() {
            let intake_gap = cam.intake_duration_deg - cam.intake_at_fifty_deg;
            let exhaust_gap = cam.exhaust_duration_deg - cam.exhaust_at_fifty_deg;

            assert!(in_range(intake_gap, 42.0, 54.0), "{}", cam.name);
            assert!(in_range(exhaust_gap, 42.0, 54.0), "{}", cam.name);
        }
    }

    /// The seat-to-seat figure is always the larger one.
    #[test]
    fn seat_to_seat_is_always_the_longer_figure() {
        for cam in ladder() {
            assert!(cam.intake_duration_deg > cam.intake_at_fifty_deg, "{}", cam.name);
            assert!(cam.exhaust_duration_deg > cam.exhaust_at_fifty_deg, "{}", cam.name);
        }
    }

    /// Overlap climbs from something a standard car would idle on to something with no idle
    /// at all.
    #[test]
    fn the_ladder_spans_stock_to_no_idle_at_all() {
        let l = ladder();

        assert!(in_range(l[0].overlap_deg(), 20.0, 40.0));
        assert!(l[l.len() - 1].overlap_deg() > 95.0);

        // The mild end keeps its vacuum.
        assert!(cam_profiles::overlap_verdict(l[0].overlap_deg()).contains("vacuum"));
        assert!(cam_profiles::overlap_verdict(l[l.len() - 1].overlap_deg()).contains("race only"));
    }

    /// A wilder engine draws cooler air and runs a hotter pipe.
    #[test]
    fn gas_conditions_move_the_way_a_build_actually_does() {
        let l = ladder();

        for i in 1..l.len() {
            assert!(l[i].charge_celsius < l[i - 1].charge_celsius, "{}", l[i].name);
            assert!(l[i].exhaust_celsius > l[i - 1].exhaust_celsius, "{}", l[i].name);
        }

        // And the whole spread is worth only a few per cent of a pipe.
        let mildest = speed_of_sound_exhaust(l[0].exhaust_celsius);
        let wildest = speed_of_sound_exhaust(l[l.len() - 1].exhaust_celsius);

        assert!(in_range(wildest / mildest, 1.0, 1.10));
    }

    // ----- finding a profile again ----------------------------------------------

    /// Every entry has to be findable from the numbers it puts in the boxes.
    #[test]
    fn every_profile_is_found_from_its_own_durations() {
        for cam in ladder() {
            assert_eq!(
                cam.name,
                cam_profiles::for_durations(cam.intake_duration_deg, cam.exhaust_duration_deg).name
            );

            assert_eq!(
                cam.name,
                cam_profiles::ALL[cam_profiles::index_for(cam.intake_duration_deg, cam.exhaust_duration_deg)].name
            );
        }
    }

    /// Anything that is not one of the five is somebody's own cam.
    #[test]
    fn anything_else_is_their_own_cam() {
        for (intake, exhaust) in [(268.0, 274.0), (252.0, 300.0), (0.0, 0.0), (-10.0, 260.0)] {
            assert!(cam_profiles::for_durations(intake, exhaust).is_custom());
            assert_eq!(cam_profiles::ALL.len() - 1, cam_profiles::index_for(intake, exhaust));
        }
    }

    /// Matching is on the durations only.
    #[test]
    fn changing_the_gas_does_not_change_which_cam_it_is() {
        let performance = cam_profiles::ALL[2];

        assert_eq!(
            performance.name,
            cam_profiles::for_durations(performance.intake_duration_deg, performance.exhaust_duration_deg).name
        );
    }

    // ----- and they have to work on the page -------------------------------------

    /// Every profile has to produce a design the calculator is happy with.
    #[test]
    fn every_profile_produces_a_workable_design() {
        for cam in ladder() {
            let p = plan(&ManifoldSpec {
                intake_duration_deg: cam.intake_duration_deg,
                exhaust_duration_deg: cam.exhaust_duration_deg,
                intake_air_celsius: cam.charge_celsius,
                exhaust_celsius: cam.exhaust_celsius,
                ..ManifoldSpec::default()
            });

            assert!(!has_stop(&p));

            assert!(p.intake.recommended.length_mm > 0.0, "{}", cam.name);
            assert!(p.exhaust.recommended.length_mm > 0.0, "{}", cam.name);
            assert!(p.intake.runner_diameter_mm > 0.0, "{}", cam.name);
            assert!(p.exhaust.primary_diameter_mm > 0.0, "{}", cam.name);
        }
    }

    /// Putting the 0.050 in figure in the box instead costs about a fifth of every length.
    #[test]
    fn using_the_fifty_thou_figure_would_shorten_every_pipe_by_about_a_fifth() {
        let cam = cam_profiles::ALL[2];

        let right = ManifoldSpec {
            intake_duration_deg: cam.intake_duration_deg,
            exhaust_duration_deg: cam.exhaust_duration_deg,
            ..ManifoldSpec::default()
        };

        let wrong = ManifoldSpec {
            intake_duration_deg: cam.intake_at_fifty_deg,
            exhaust_duration_deg: cam.exhaust_at_fifty_deg,
            ..right
        };

        let correct = plan(&right).intake.recommended.effective_length_mm;
        let mistaken = plan(&wrong).intake.recommended.effective_length_mm;

        assert!(in_range(1.0 - (mistaken / correct), 0.12, 0.25));
    }

    // ----- what a cam does to breathing ------------------------------------------

    /// A bigger cam never breathes worse. Within one head, every step up the ladder has to be
    /// worth the same or more.
    #[test]
    fn a_bigger_cam_never_breathes_worse_on_the_same_head() {
        for family in families() {
            let by_level: Vec<f64> = ladder()
                .iter()
                .map(|c| cam_profiles::volumetric_efficiency(&family, c))
                .collect();

            for i in 1..by_level.len() {
                assert!(by_level[i] >= by_level[i - 1], "{}: level {i}", family.name);
            }
        }
    }

    /// A cam cannot carry a head past what it flows.
    #[test]
    fn no_cam_carries_a_head_past_what_it_flows() {
        for family in families() {
            for cam in ladder() {
                let ve = cam_profiles::volumetric_efficiency(&family, &cam);

                assert!(
                    ve <= family.volumetric_efficiency + cam_profiles::MOST_A_CAM_CAN_ADD,
                    "{} on {} reached {ve:.0}",
                    family.name,
                    cam.name
                );
            }
        }

        // The oldest head with the wildest cam still has to look like an old head.
        let old = cam_profiles::volumetric_efficiency(&engine_families::ALL[0], &cam_profiles::ALL[4]);

        assert!(in_range(old, 80.0, 92.0));
    }

    /// The race entry is the one whose description already has a big cam in it, so it must not
    /// be handed its 105 for an engine running a stock grind.
    #[test]
    fn the_race_intake_only_earns_its_figure_with_the_cam_it_assumes() {
        let race = *engine_families::ALL
            .iter()
            .find(|f| f.implied_cam_level > 0)
            .expect("a race family");

        assert!(eq(
            race.volumetric_efficiency,
            cam_profiles::volumetric_efficiency(&race, &cam_profiles::ALL[race.implied_cam_level as usize]),
            6
        ));

        let on_a_stock_cam = cam_profiles::volumetric_efficiency(&race, &cam_profiles::ALL[0]);

        assert!(on_a_stock_cam < race.volumetric_efficiency - 10.0);
    }

    /// Nothing the table can produce may be outside what a naturally aspirated engine does.
    #[test]
    fn every_combination_stays_inside_what_an_engine_actually_breathes() {
        for family in families() {
            for cam in ladder() {
                assert!(in_range(
                    cam_profiles::volumetric_efficiency(&family, &cam),
                    70.0,
                    112.0
                ));
            }
        }
    }

    /// Somebody's own head or own cam means their own figure, not a guess.
    #[test]
    fn anything_custom_declines_to_answer() {
        let custom_family = engine_families::ALL[engine_families::ALL.len() - 1];
        let custom_cam = cam_profiles::ALL[cam_profiles::ALL.len() - 1];

        assert!(cam_profiles::volumetric_efficiency(&custom_family, &cam_profiles::ALL[0]).is_nan());
        assert!(cam_profiles::volumetric_efficiency(&engine_families::ALL[0], &custom_cam).is_nan());
        assert_eq!(-1, cam_profiles::level_of(&custom_cam));
    }

    /// Every figure the table produces has to lead back to a head that really produces it.
    #[test]
    fn every_figure_leads_back_to_a_head_that_produces_it() {
        for family in families() {
            for cam in ladder() {
                let ve = cam_profiles::volumetric_efficiency(&family, &cam);

                let found = cam_profiles::family_index_for(ve, &cam, None);

                assert_ne!(engine_families::ALL.len() - 1, found);
                assert!(eq(
                    ve,
                    cam_profiles::volumetric_efficiency(&engine_families::ALL[found], &cam),
                    6
                ));
            }
        }
    }

    /// Where the figure is ambiguous, the selection already showing wins.
    #[test]
    fn an_ambiguous_figure_keeps_the_head_already_chosen() {
        let race = cam_profiles::ALL[4];

        // The collision this guards: two families, one figure.
        let fixed_cams = cam_profiles::volumetric_efficiency(&engine_families::ALL[2], &race);
        let tuned_intake = cam_profiles::volumetric_efficiency(&engine_families::ALL[4], &race);

        assert!(eq(fixed_cams, tuned_intake, 6));

        // Each keeps itself when it is the one already selected.
        assert_eq!(2, cam_profiles::family_index_for(fixed_cams, &race, Some(2)));
        assert_eq!(4, cam_profiles::family_index_for(tuned_intake, &race, Some(4)));

        // A preference that no longer fits is dropped rather than honoured.
        assert_eq!(
            0,
            cam_profiles::family_index_for(
                cam_profiles::volumetric_efficiency(&engine_families::ALL[0], &race),
                &race,
                Some(3)
            )
        );
    }

    /// A figure no head and cam would produce is somebody's own measurement.
    #[test]
    fn a_figure_nothing_produces_is_their_own() {
        assert_eq!(
            engine_families::ALL.len() - 1,
            cam_profiles::family_index_for(63.7, &cam_profiles::ALL[0], None)
        );

        assert_eq!(
            engine_families::ALL.len() - 1,
            cam_profiles::family_index_for(0.0, &cam_profiles::ALL[0], None)
        );
    }

    /// Nothing in the list may be blank, and the custom entry must be last.
    #[test]
    fn the_list_is_well_formed() {
        for cam in cam_profiles::ALL.iter() {
            assert!(!cam.name.trim().is_empty());
            assert!(!cam.note.trim().is_empty());
        }

        let all = &cam_profiles::ALL;
        assert!(all[all.len() - 1].is_custom());
        assert_eq!(1, all.iter().filter(|c| c.is_custom()).count());
        assert!(all[all.len() - 1].overlap_deg().is_nan());
    }
}
