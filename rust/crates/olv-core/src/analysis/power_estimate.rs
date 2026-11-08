//! Ported from `PowerEstimate.cs`. Owned by package `w3-analysis`.
//!
//! Estimating an engine's power from what a logger already recorded.
//!
//! Two independent routes to the same number, which is the point of doing both. Speed density
//! works out the air from the manifold (pressure, temperature, engine speed and how completely
//! each stroke fills) and turns it into power through the mixture and the fuel consumption.
//! The injector route ignores the air entirely and counts the fuel instead, from how long the
//! injectors are held open and how hard the rail is pushing on them.
//!
//! They rest on almost nothing in common. Speed density leans on volumetric efficiency, which
//! is the number a tuner is least sure of; the injector route leans on injector data and dead
//! time. So where they agree there is reason to believe the figure, and where they do not, the
//! disagreement is itself the useful output. That comparison is what the spread channel is for.
//!
//! None of this is a dyno. Every method here multiplies by a brake specific fuel consumption
//! that has been assumed rather than measured. What they are good for is shape and change:
//! where the power is made, what a modification did, whether two runs match.

use crate::base::tuning_math::{self, Fuel};
use crate::channels::channel_roles::{self, ChannelRole};
use crate::fmt;
use crate::math::channel_units;
use crate::math::math_channel::MathChannel;
use crate::model::{LogChannel, LogDocument};

/// What the engine is, as far as estimating its power needs to know.
#[derive(Clone, Debug, PartialEq)]
pub struct EngineSpec {
    pub litres: f64,
    pub cylinders: i32,
    pub fuel: Fuel,
    /// Brake specific fuel consumption: the efficiency every one of these methods turns on,
    /// and the largest thing anyone here is guessing at.
    ///
    /// The full-throttle figure rather than the sizing one. Sizing uses a deliberately
    /// pessimistic number because oversizing an injector is cheap; using that here would
    /// understate the engine by a fifth.
    pub bsfc: f64,
    /// Volumetric efficiency, per cent, for logs that do not record it.
    pub volumetric_efficiency: f64,
    /// Mixture to assume where the log carries no wideband.
    pub lambda: f64,
    /// One injector's rated static flow, in cc per minute.
    pub injector_cc_per_minute: f64,
    /// The pressure difference that rating was taken at.
    pub injector_rated_kpa: f64,
    /// How long an injector takes to open before it flows anything. Subtracted from the
    /// logged pulse width: at 7,000 rpm a millisecond of dead time is six per cent of the
    /// cycle.
    pub injector_dead_time_ms: f64,
    /// True where the injectors fire twice per cycle rather than once. Doubles the duty a
    /// given pulse width represents.
    pub batch_injection: bool,
    /// True where the logged fuel pressure is already the difference across the injector,
    /// rather than a rail pressure to have the manifold taken off it.
    pub fuel_pressure_is_differential: bool,
    /// Driveline loss to a wheel figure, per cent. Zero leaves it at the crank.
    pub drivetrain_loss_percent: f64,
}

impl Default for EngineSpec {
    fn default() -> Self {
        Self {
            litres: 2.0,
            cylinders: 4,
            fuel: Fuel::Petrol,
            bsfc: tuning_math::FULL_THROTTLE_BSFC,
            volumetric_efficiency: 95.0,
            lambda: 0.85,
            injector_cc_per_minute: 550.0,
            injector_rated_kpa: 300.0,
            injector_dead_time_ms: 1.0,
            batch_injection: false,
            fuel_pressure_is_differential: false,
            drivetrain_loss_percent: 0.0,
        }
    }
}

/// One way of estimating power, and the channels that work it out.
#[derive(Clone, Debug, PartialEq)]
pub struct PowerMethod {
    /// What it is called.
    pub name: String,
    /// What it rests on, including anything that had to be assumed.
    pub basis: String,
    /// In order: later ones read earlier ones.
    pub channels: Vec<MathChannel>,
}

/// A method that could not be offered on this log, and what it wanted.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct UnavailableMethod {
    pub name: String,
    pub needs: String,
}

/// What a log can support.
#[derive(Clone, Debug, PartialEq)]
pub struct PowerEstimateResult {
    pub methods: Vec<PowerMethod>,
    pub unavailable: Vec<UnavailableMethod>,
}

impl PowerEstimateResult {
    /// Every channel from every method, in the order they must be built.
    pub fn channels(&self) -> Vec<MathChannel> {
        self.methods.iter().flat_map(|m| m.channels.iter().cloned()).collect()
    }
}

/// Kilograms of air per minute to pounds per hour.
const KG_PER_MINUTE_TO_LB_PER_HOUR: f64 = 132.27735731092654;

/// Grams per second to pounds per hour.
const GRAMS_PER_SECOND_TO_LB_PER_HOUR: f64 = 7.936641438521303;

/// Grams per minute to pounds per hour.
const GRAMS_PER_MINUTE_TO_LB_PER_HOUR: f64 = 0.13227735731092654;

/// The gas constant for air, doubled for the two revolutions a four-stroke takes to fill
/// once: the 574 that turns litres, rpm and kilopascals into kilograms a minute.
const AIR_DENSITY_DIVISOR: f64 = 574.0;

pub const AIRFLOW_CHANNEL: &str = "Airflow (speed density)";
pub const SPEED_DENSITY_CHANNEL: &str = "Power (speed density)";
pub const DUTY_CHANNEL: &str = "Injector duty (est)";
pub const INJECTOR_FLOW_CHANNEL: &str = "Injector flow (est)";
pub const FUEL_FLOW_CHANNEL: &str = "Fuel flow (injectors)";
pub const INJECTOR_POWER_CHANNEL: &str = "Power (injectors)";
pub const MAF_POWER_CHANNEL: &str = "Power (MAF)";
pub const TORQUE_CHANNEL: &str = "Torque (est)";
pub const SPREAD_CHANNEL: &str = "Power spread";
pub const WHEEL_CHANNEL: &str = "Power (at the wheels)";

fn channel(name: &str, units: &str, digits: i32, expression: String) -> MathChannel {
    MathChannel {
        name: name.to_string(),
        units: units.to_string(),
        digits,
        expression,
        ..MathChannel::default()
    }
}

fn number(value: f64) -> String {
    channel_units::number(value)
}

/// Every estimate this log can support, and why the rest cannot be offered.
pub fn estimate(log: &LogDocument, spec: &EngineSpec) -> PowerEstimateResult {
    let mut methods: Vec<PowerMethod> = Vec::new();
    let mut missing: Vec<UnavailableMethod> = Vec::new();

    let find = |role| channel_roles::find(log, role).map(|c| c.as_ref());
    let rpm = find(ChannelRole::EngineSpeed);
    let map = find(ChannelRole::ManifoldPressure);
    let iat = find(ChannelRole::IntakeAir);
    let mixture = find(ChannelRole::Mixture);
    let ve = find(ChannelRole::VolumetricEfficiency);
    let maf = find(ChannelRole::MassAirFlow);
    let pulse_width = find(ChannelRole::InjectorPulseWidth);
    let duty = find(ChannelRole::InjectorDuty);

    let afr = mixture_expression(mixture, spec);
    let bsfc = number(spec.bsfc);

    if let Some(density) = speed_density(spec, rpm, map, iat, ve, &afr, &bsfc, mixture) {
        methods.push(density);
    } else {
        missing.push(UnavailableMethod {
            name: "Speed density".to_string(),
            needs: needed(&[
                (rpm.is_some(), "engine speed"),
                (map.is_some(), "manifold pressure"),
                (iat.is_some(), "intake air temperature"),
            ]),
        });
    }

    if let Some(injectors) = injectors(spec, rpm, pulse_width, duty, log, &bsfc) {
        methods.push(injectors);
    } else {
        missing.push(UnavailableMethod {
            name: "Injectors".to_string(),
            needs: if pulse_width.is_none() && duty.is_none() {
                "an injector pulse width or duty cycle".to_string()
            } else {
                needed(&[(rpm.is_some(), "engine speed")])
            },
        });
    }

    if let Some(maf) = maf {
        methods.push(mass_air_flow(maf, &afr, &bsfc, mixture));
    } else {
        missing.push(UnavailableMethod {
            name: "Mass air flow".to_string(),
            needs: "a mass air flow channel".to_string(),
        });
    }

    // Only worth having where there are two figures to compare.
    if methods.iter().any(|m| m.name == "Speed density") && methods.iter().any(|m| m.name == "Injectors") {
        methods.push(spread());
    }

    if !methods.is_empty()
        && let Some(rpm) = rpm
    {
        let torque = torque(&methods[0], rpm);
        methods.push(torque);
    }

    if !methods.is_empty() && spec.drivetrain_loss_percent > 0.0 {
        let wheels = wheels(&methods[0], spec);
        methods.push(wheels);
    }

    PowerEstimateResult {
        methods,
        unavailable: missing,
    }
}

/// Air from the manifold, and power from the air.
///
/// The density of what the engine swallowed, from the ideal gas law: pressure over
/// temperature, times the swept volume, times how completely it filled. Halved because a
/// four-stroke fills once every two turns.
#[allow(clippy::too_many_arguments)]
fn speed_density(
    spec: &EngineSpec,
    rpm: Option<&LogChannel>,
    map: Option<&LogChannel>,
    iat: Option<&LogChannel>,
    ve: Option<&LogChannel>,
    afr: &str,
    bsfc: &str,
    mixture: Option<&LogChannel>,
) -> Option<PowerMethod> {
    let (rpm, map, iat) = (rpm?, map?, iat?);

    let gauge = looks_like_gauge(map);

    let absolute = if gauge {
        format!(
            "({} + {})",
            channel_units::to_kilopascals(map),
            number(tuning_math::ATMOSPHERIC_KPA)
        )
    } else {
        channel_units::to_kilopascals(map)
    };

    let filling = match ve {
        Some(ve) => channel_units::to_fraction(ve),
        None => number(spec.volumetric_efficiency / 100.0),
    };

    let airflow = format!(
        "{} * {} * {} * {} / ({} * {})",
        number(spec.litres),
        filling,
        rpm.name(),
        absolute,
        number(AIR_DENSITY_DIVISOR),
        channel_units::to_kelvin(iat)
    );

    let power = format!(
        "{AIRFLOW_CHANNEL} * {} / ({afr} * {bsfc})",
        number(KG_PER_MINUTE_TO_LB_PER_HOUR)
    );

    let ve_text = match ve {
        Some(ve) => format!("VE from {}", ve.name()),
        None => format!("VE assumed {}%", fmt::f64(spec.volumetric_efficiency, "N0")),
    };

    Some(PowerMethod {
        name: "Speed density".to_string(),
        basis: format!(
            "{} L, {}, {}, BSFC {}{}",
            fmt::f64(spec.litres, "N1"),
            ve_text,
            describe(mixture, Some(spec)),
            fmt::f64(spec.bsfc, "N2"),
            if gauge { ", manifold pressure read as gauge" } else { "" }
        ),
        channels: vec![
            channel(AIRFLOW_CHANNEL, "kg/min", 3, airflow),
            channel(SPEED_DENSITY_CHANNEL, "hp", 1, power),
        ],
    })
}

/// Fuel from the injectors, and power from the fuel.
///
/// No air anywhere in it, which is what makes it worth having alongside the other one. Duty
/// comes from the pulse width where the ECU does not report it directly, with the dead time
/// taken off first: an injector held open for its opening time flows nothing at all.
///
/// Flow follows the square root of the pressure across the injector, so a rail that sags under
/// boost flows less than its rating whatever the sticker says. Where the log carries a rail
/// pressure this follows it; where it does not, the regulator is taken to be doing its job and
/// holding the difference constant.
fn injectors(
    spec: &EngineSpec,
    rpm: Option<&LogChannel>,
    pulse_width: Option<&LogChannel>,
    duty: Option<&LogChannel>,
    log: &LogDocument,
    bsfc: &str,
) -> Option<PowerMethod> {
    if duty.is_none() && (rpm.is_none() || pulse_width.is_none()) {
        return None;
    }

    let mut channels: Vec<MathChannel> = Vec::new();
    let duty_percent: String;

    if let Some(duty) = duty {
        duty_percent = duty.name().to_string();
    } else {
        let (rpm, pulse_width) = (rpm?, pulse_width?);

        // Per cent, so the channel reads the way an ECU reports it. The divisor is 120,000 ms
        // in a minute of firing once every two turns, over 100 for the percentage, halved
        // again for batch, which fires twice as often.
        let divisor = if spec.batch_injection { 600.0 } else { 1200.0 };

        channels.push(channel(
            DUTY_CHANNEL,
            "%",
            1,
            format!(
                "max({} - {}, 0) * {} / {}",
                channel_units::to_milliseconds(pulse_width),
                number(spec.injector_dead_time_ms),
                rpm.name(),
                number(divisor)
            ),
        ));

        duty_percent = DUTY_CHANNEL.to_string();
    }

    let find = |role| channel_roles::find(log, role).map(|c| c.as_ref());
    let fuel_pressure = find(ChannelRole::FuelPressure);
    let map = find(ChannelRole::ManifoldPressure);
    let baro = find(ChannelRole::Barometric);

    let mut flow = number(spec.injector_cc_per_minute);
    let mut pressure_note = "rail assumed steady at its rating".to_string();

    if let Some(fuel_pressure) = fuel_pressure {
        let across = differential(spec, fuel_pressure, map, baro);

        channels.push(channel(
            INJECTOR_FLOW_CHANNEL,
            "cc/min",
            0,
            format!(
                "{} * sqrt(max({across}, 0) / {})",
                number(spec.injector_cc_per_minute),
                number(spec.injector_rated_kpa)
            ),
        ));

        flow = INJECTOR_FLOW_CHANNEL.to_string();
        pressure_note = if spec.fuel_pressure_is_differential {
            format!("rail from {}, already differential", fuel_pressure.name())
        } else {
            format!(
                "rail from {}{}",
                fuel_pressure.name(),
                if map.is_some() {
                    ", less manifold pressure"
                } else {
                    ", less a standard atmosphere"
                }
            )
        };
    }

    let density = tuning_math::density(spec.fuel);

    channels.push(channel(
        FUEL_FLOW_CHANNEL,
        "lb/hr",
        1,
        format!(
            "{flow} * ({duty_percent} / 100) * {} * {} * {}",
            spec.cylinders,
            number(density),
            number(GRAMS_PER_MINUTE_TO_LB_PER_HOUR)
        ),
    ));

    channels.push(channel(
        INJECTOR_POWER_CHANNEL,
        "hp",
        1,
        format!("{FUEL_FLOW_CHANNEL} / {bsfc}"),
    ));

    let timing = match duty {
        None => format!(", {} ms dead time", fmt::f64(spec.injector_dead_time_ms, "N2")),
        Some(duty) => format!(", duty from {}", duty.name()),
    };

    Some(PowerMethod {
        name: "Injectors".to_string(),
        basis: format!(
            "{} × {} cc/min{}, {}, {}, {}, BSFC {}",
            spec.cylinders,
            fmt::f64(spec.injector_cc_per_minute, "N0"),
            timing,
            if spec.batch_injection { "batch" } else { "sequential" },
            pressure_note,
            tuning_math::name(spec.fuel),
            fmt::f64(spec.bsfc, "N2")
        ),
        channels,
    })
}

/// The pressure the injector is actually working against.
///
/// A rail sensor reads against the atmosphere while the injector sprays into the manifold, so
/// under boost the difference across it is less than the gauge says by exactly the boost. On a
/// manifold-referenced regulator the two move together and this comes out constant, which is
/// the point of one.
fn differential(
    spec: &EngineSpec,
    fuel_pressure: &LogChannel,
    map: Option<&LogChannel>,
    baro: Option<&LogChannel>,
) -> String {
    let rail = channel_units::to_kilopascals(fuel_pressure);

    if spec.fuel_pressure_is_differential {
        return rail;
    }

    let ambient = match baro {
        Some(baro) => channel_units::to_kilopascals(baro),
        None => number(tuning_math::ATMOSPHERIC_KPA),
    };

    let Some(map) = map else {
        return rail;
    };

    let manifold = if looks_like_gauge(map) {
        format!("({} + {ambient})", channel_units::to_kilopascals(map))
    } else {
        channel_units::to_kilopascals(map)
    };

    format!("({rail} + {ambient} - {manifold})")
}

/// Power from a meter that measured the air, where the car has one.
fn mass_air_flow(maf: &LogChannel, afr: &str, bsfc: &str, mixture: Option<&LogChannel>) -> PowerMethod {
    PowerMethod {
        name: "Mass air flow".to_string(),
        basis: format!(
            "measured air from {}, {}, BSFC {bsfc}",
            maf.name(),
            describe(mixture, None)
        ),
        channels: vec![channel(
            MAF_POWER_CHANNEL,
            "hp",
            1,
            format!(
                "{} * {} / ({afr} * {bsfc})",
                channel_units::to_grams_per_second(maf),
                number(GRAMS_PER_SECOND_TO_LB_PER_HOUR)
            ),
        )],
    }
}

/// How far the two independent estimates are apart, as a percentage of the air-based one.
///
/// The channel worth watching. Both rest on the same assumed fuel consumption, so that cancels
/// and what is left is the disagreement between the volumetric efficiency and the injector
/// data. A few per cent is noise. A steady twenty means one of the two is wrong.
fn spread() -> PowerMethod {
    PowerMethod {
        name: "Agreement".to_string(),
        basis: "how far the injector estimate sits from the air estimate".to_string(),
        channels: vec![channel(
            SPREAD_CHANNEL,
            "%",
            1,
            format!("({INJECTOR_POWER_CHANNEL} - {SPEED_DENSITY_CHANNEL}) / max({SPEED_DENSITY_CHANNEL}, 1) * 100"),
        )],
    }
}

fn last_channel_name(method: &PowerMethod) -> &str {
    &method.channels[method.channels.len() - 1].name
}

fn torque(preferred: &PowerMethod, rpm: &LogChannel) -> PowerMethod {
    PowerMethod {
        name: "Torque".to_string(),
        basis: format!("from {}", last_channel_name(preferred)),
        channels: vec![channel(
            TORQUE_CHANNEL,
            "lb-ft",
            1,
            format!("{} * 5252 / max({}, 1)", last_channel_name(preferred), rpm.name()),
        )],
    }
}

fn wheels(preferred: &PowerMethod, spec: &EngineSpec) -> PowerMethod {
    PowerMethod {
        name: "At the wheels".to_string(),
        basis: format!(
            "{}% taken off {}",
            fmt::f64(spec.drivetrain_loss_percent, "N0"),
            last_channel_name(preferred)
        ),
        channels: vec![channel(
            WHEEL_CHANNEL,
            "hp",
            1,
            format!(
                "{} * {}",
                last_channel_name(preferred),
                number(1.0 - (spec.drivetrain_loss_percent / 100.0))
            ),
        )],
    }
}

/// The mixture to divide the air by: the logged one where there is one.
fn mixture_expression(mixture: Option<&LogChannel>, spec: &EngineSpec) -> String {
    match mixture {
        Some(mixture) => channel_units::to_air_fuel_ratio(mixture, spec.fuel),
        None => number(spec.lambda * tuning_math::stoichiometric(spec.fuel)),
    }
}

fn describe(mixture: Option<&LogChannel>, spec: Option<&EngineSpec>) -> String {
    match (mixture, spec) {
        (Some(mixture), _) => format!("mixture from {}", mixture.name()),
        (None, Some(spec)) => format!("lambda assumed {}", fmt::f64(spec.lambda, "N2")),
        (None, None) => "mixture assumed".to_string(),
    }
}

/// Whether a manifold pressure channel is a gauge reading rather than an absolute one.
///
/// Told by the values, because the units cannot say it: "psi" is written the same either way.
/// Anything that goes meaningfully below zero is a gauge; a true absolute pressure has nowhere
/// below vacuum to go.
pub fn looks_like_gauge(channel: &LogChannel) -> bool {
    for i in 0..channel.len() {
        let v = channel.at(i);

        if v.is_finite() && v < -1.0 {
            return true;
        }
    }

    false
}

/// Names the missing inputs: `(present, name)` for each one wanted.
fn needed(wanted: &[(bool, &str)]) -> String {
    let absent: Vec<&str> = wanted.iter().filter(|(present, _)| !present).map(|(_, n)| *n).collect();

    match absent.len() {
        0 => "nothing — it should have been offered".to_string(),
        1 => absent[0].to_string(),
        n => format!("{} and {}", absent[..n - 1].join(", "), absent[n - 1]),
    }
}
