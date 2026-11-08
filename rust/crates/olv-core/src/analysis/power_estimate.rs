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

/// Estimating power from a log.
///
/// The expressions are not inspected: they are run. Every check below builds a log whose
/// numbers are known, hands the generated definitions to the same evaluator the application
/// uses, and reads the horsepower out the far end. An expression that is right about its
/// arithmetic and wrong about a unit, or that references a channel by a name the parser cannot
/// resolve, fails here in the same way it would fail on a real log.
#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use super::*;
    use crate::math::math_channel;

    /// xunit's `Assert.Equal(expected, actual, precision)`.
    fn near(expected: f64, actual: f64, precision: i32) {
        let scale = 10f64.powi(precision);
        assert_eq!(
            (expected * scale).round_ties_even(),
            (actual * scale).round_ties_even(),
            "{expected} vs {actual}"
        );
    }

    fn in_range(value: f64, low: f64, high: f64) {
        assert!((low..=high).contains(&value), "{value} not in {low}..{high}");
    }

    fn log(channels: &[(&str, &str, &[f64])]) -> LogDocument {
        LogDocument::new(
            "test.csv",
            "CSV",
            channels
                .iter()
                .map(|(n, u, v)| Arc::new(LogChannel::new(n, u, 3, v, false)))
                .collect(),
            Arc::new(LogChannel::new("Time", "s", 3, &[0.0, 1.0, 2.0], true)),
        )
    }

    /// Builds the estimate's channels for real and hands back a reading from one.
    fn value_at(log: &LogDocument, spec: &EngineSpec, channel: &str, sample: usize) -> f64 {
        let estimate = estimate(log, spec);
        let built = math_channel::build(log, &estimate.channels());

        assert!(built.problems.is_empty(), "{:?}", built.problems);

        let found = built.channels.iter().find(|c| c.name() == channel);
        let found = found.unwrap_or_else(|| panic!("no channel {channel}"));

        found.at(sample)
    }

    fn value(log: &LogDocument, spec: &EngineSpec, channel: &str) -> f64 {
        value_at(log, spec, channel, 0)
    }

    /// A 2.0 litre at 7,000 rpm, wide open, at sea level on a 20 degree C day.
    ///
    /// Chosen so the answer is checkable by hand: at full volumetric efficiency it swallows
    /// 8.43 kg of air a minute, which at 12.5:1 and a BSFC of 0.50 is 178 horsepower.
    fn wide_open() -> LogDocument {
        log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "kPa", &[101.325, 101.325, 101.325]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
            ("AFR", "afr", &[12.5, 12.5, 12.5]),
        ])
    }

    fn two_litre() -> EngineSpec {
        EngineSpec {
            litres: 2.0,
            cylinders: 4,
            bsfc: 0.50,
            volumetric_efficiency: 100.0,
            ..EngineSpec::default()
        }
    }

    // ----- speed density -------------------------------------------------------

    #[test]
    fn the_air_is_what_the_gas_law_says_it_is() {
        // 8.43 kg/min, worked out independently: two litres, half of 7,000 rpm, at
        // 1.2044 kg/m3, which is 101.325 kPa over 287 x 293.15 K.
        let airflow = value(&wide_open(), &two_litre(), AIRFLOW_CHANNEL);

        near(8.43, airflow, 2);
    }

    #[test]
    fn a_two_litre_at_seven_thousand_makes_about_one_hundred_and_eighty_horsepower() {
        let hp = value(&wide_open(), &two_litre(), SPEED_DENSITY_CHANNEL);

        near(178.4, hp, 1);
    }

    #[test]
    fn the_estimate_agrees_with_the_calculator_it_shares() {
        // The airflow tab answers the same question through a different route: cubic feet a
        // minute at standard density, then pounds a minute. Getting the same figure two ways
        // is worth more than either on its own.
        let hp = value(&wide_open(), &two_litre(), SPEED_DENSITY_CHANNEL);

        let cfm = tuning_math::cubic_feet_per_minute(2.0, 7000.0, 100.0, 1.0);
        let via_calculator =
            tuning_math::horsepower_from_air(tuning_math::air_pounds_per_minute(cfm), Fuel::Petrol, 0.85, 0.50);

        // Not identical: the calculator works at the standard 15 C where this log is a 20 C
        // day, which is worth about two per cent of the density.
        in_range(hp / via_calculator, 0.97, 1.0);
    }

    #[test]
    fn the_units_the_logger_chose_do_not_change_the_answer() {
        // The same air, described in whatever units the logger felt like.
        let cases = [
            ("kPa", 101.325, "C", 20.0),
            ("psi", 14.6959, "C", 20.0),
            ("bar", 1.01325, "C", 20.0),
            ("kPa", 101.325, "F", 68.0),
            ("psi", 14.6959, "F", 68.0),
        ];

        for (map_units, map_value, iat_units, iat_value) in cases {
            let l = log(&[
                ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
                ("MAP", map_units, &[map_value; 3]),
                ("IAT", iat_units, &[iat_value; 3]),
                ("AFR", "afr", &[12.5, 12.5, 12.5]),
            ]);

            near(178.4, value(&l, &two_litre(), SPEED_DENSITY_CHANNEL), 0);
        }
    }

    #[test]
    fn a_lambda_channel_is_not_mistaken_for_an_air_fuel_ratio() {
        // 0.85 lambda is 12.5:1 on petrol, so this must land on the same answer as the log
        // that spelled it out. Reading 0.85 as a ratio would divide the air by 0.85 and report
        // fifteen times the power.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "kPa", &[101.325, 101.325, 101.325]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
            ("Lambda", "lambda", &[0.85034, 0.85034, 0.85034]),
        ]);

        near(178.4, value(&l, &two_litre(), SPEED_DENSITY_CHANNEL), 0);
    }

    #[test]
    fn a_bare_mixture_channel_is_told_apart_by_its_values() {
        // Plenty of firmware logs a mixture with no unit at all. Around one it is lambda;
        // around fifteen it is a ratio; nothing sensible is both.
        let lambda = LogChannel::new("Mixture", "", 3, &[0.85, 0.88, 0.90], false);
        let ratio = LogChannel::new("Mixture", "", 3, &[12.5, 13.0, 13.2], false);

        assert!(channel_units::is_lambda(&lambda));
        assert!(!channel_units::is_lambda(&ratio));
    }

    #[test]
    fn boost_logged_as_gauge_pressure_is_recognised_and_added_back_on() {
        // A channel that goes below zero is a gauge reading: an absolute pressure has nowhere
        // under vacuum to go. Treating 10 psi of boost as an absolute 69 kPa would report a
        // third of the real airflow.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "psi", &[-8.0, 0.0, 10.0]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
            ("AFR", "afr", &[12.5, 12.5, 12.5]),
        ]);

        // The third sample is 10 psi of boost: 170.3 kPa absolute, which is 1.68 times the
        // atmospheric case and so 1.68 times the power.
        let boosted = value_at(&l, &two_litre(), SPEED_DENSITY_CHANNEL, 2);
        let atmospheric = value_at(&l, &two_litre(), SPEED_DENSITY_CHANNEL, 1);

        near(178.4, atmospheric, 0);
        near(1.681, boosted / atmospheric, 2);
    }

    #[test]
    fn a_logged_volumetric_efficiency_is_used_rather_than_the_assumed_one() {
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "kPa", &[101.325, 101.325, 101.325]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
            ("AFR", "afr", &[12.5, 12.5, 12.5]),
            ("VE", "%", &[50.0, 50.0, 50.0]),
        ]);

        // Half the filling, half the air, half the power, and the spec's 100% ignored in
        // favour of what the ECU actually reported.
        near(89.2, value(&l, &two_litre(), SPEED_DENSITY_CHANNEL), 1);
    }

    // ----- injectors -----------------------------------------------------------

    /// Four 1,000 cc injectors at 7,000 rpm on a 10.3 ms pulse, of which 0.3 ms is the
    /// injector opening: 58.33 per cent duty, 2,333 cc of petrol a minute, 229.9 lb/hr, and at
    /// a BSFC of 0.50 that is 460 horsepower.
    fn big_injectors() -> EngineSpec {
        EngineSpec {
            litres: 2.0,
            cylinders: 4,
            bsfc: 0.50,
            injector_cc_per_minute: 1000.0,
            injector_dead_time_ms: 0.3,
            ..EngineSpec::default()
        }
    }

    fn rpm_and_pulse(pulse: f64) -> LogDocument {
        log(&[("RPM", "rpm", &[7000.0, 7000.0, 7000.0]), ("PW", "ms", &[pulse; 3])])
    }

    #[test]
    fn the_duty_is_what_the_pulse_width_and_the_engine_speed_make() {
        near(58.33, value(&rpm_and_pulse(10.3), &big_injectors(), DUTY_CHANNEL), 2);
    }

    #[test]
    fn the_fuel_and_the_power_follow_from_the_duty() {
        let l = rpm_and_pulse(10.3);

        near(229.9, value(&l, &big_injectors(), FUEL_FLOW_CHANNEL), 1);
        near(459.9, value(&l, &big_injectors(), INJECTOR_POWER_CHANNEL), 0);
    }

    #[test]
    fn dead_time_is_taken_off_and_never_added_back() {
        // A millisecond of dead time at 7,000 rpm is six per cent of the cycle. Leaving it
        // out overstates the fuel, and therefore the engine.
        let l = rpm_and_pulse(10.3);

        let with_dead_time = value(&l, &big_injectors(), INJECTOR_POWER_CHANNEL);
        let without = value(
            &l,
            &EngineSpec {
                injector_dead_time_ms: 0.0,
                ..big_injectors()
            },
            INJECTOR_POWER_CHANNEL,
        );

        assert!(without > with_dead_time);
        near(0.3 / 10.3, 1.0 - (with_dead_time / without), 3);
    }

    #[test]
    fn a_pulse_shorter_than_the_dead_time_is_no_fuel_rather_than_negative_fuel() {
        // Overrun: the ECU commands a pulse too short to open the injector. The arithmetic
        // would otherwise produce a negative duty and negative power, which would drag the
        // channel's whole axis down with it.
        let l = rpm_and_pulse(0.1);

        near(0.0, value(&l, &big_injectors(), DUTY_CHANNEL), 6);
        near(0.0, value(&l, &big_injectors(), INJECTOR_POWER_CHANNEL), 6);
    }

    #[test]
    fn batch_injection_is_worth_exactly_twice_sequential() {
        let l = rpm_and_pulse(4.0);

        let sequential = value(&l, &big_injectors(), INJECTOR_POWER_CHANNEL);
        let batch = value(
            &l,
            &EngineSpec {
                batch_injection: true,
                ..big_injectors()
            },
            INJECTOR_POWER_CHANNEL,
        );

        near(2.0, batch / sequential, 6);
    }

    #[test]
    fn a_logged_duty_cycle_is_used_instead_of_working_one_out() {
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("Injector duty", "%", &[58.333, 58.333, 58.333]),
        ]);

        // The same answer the pulse-width route reaches, which is the check that the two ways
        // in agree rather than merely both producing a number.
        near(459.9, value(&l, &big_injectors(), INJECTOR_POWER_CHANNEL), 0);
    }

    // ----- fuel pressure -------------------------------------------------------

    #[test]
    fn flow_follows_the_square_root_of_the_pressure_across_the_injector() {
        // Rated at 300 kPa and run at 400: the square root of 4/3 is 1.155, so a 1,000 cc
        // injector flows 1,155.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("PW", "ms", &[10.3, 10.3, 10.3]),
            ("Fuel pressure", "kPa", &[400.0, 400.0, 400.0]),
        ]);

        near(
            1155.0,
            value(
                &l,
                &EngineSpec {
                    fuel_pressure_is_differential: true,
                    ..big_injectors()
                },
                INJECTOR_FLOW_CHANNEL,
            ),
            0,
        );
    }

    #[test]
    fn boost_eats_into_the_rail_pressure_unless_the_regulator_follows_it() {
        // The subtlety worth having this feature for. The rail gauge reads 300 kPa; the
        // manifold is at 170.3 absolute, which is 69 above the atmosphere. So the injector is
        // only working against 231, not 300, and flows the square root of that fraction less.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("PW", "ms", &[10.3, 10.3, 10.3]),
            ("MAP", "kPa", &[170.3, 170.3, 170.3]),
            ("Fuel pressure", "kPa", &[300.0, 300.0, 300.0]),
        ]);

        let flow = value(&l, &big_injectors(), INJECTOR_FLOW_CHANNEL);

        near(1000.0 * ((300.0 + 101.325 - 170.3) / 300.0f64).sqrt(), flow, 0);
        in_range(flow, 870.0, 890.0);
    }

    #[test]
    fn a_rail_that_collapses_is_not_a_source_of_imaginary_fuel() {
        // A pressure difference below zero cannot happen and a square root of it is not a
        // number. Clamped, so a bad sensor reading costs a sample rather than the channel.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("PW", "ms", &[10.3, 10.3, 10.3]),
            ("MAP", "kPa", &[500.0, 500.0, 500.0]),
            ("Fuel pressure", "kPa", &[100.0, 100.0, 100.0]),
        ]);

        near(0.0, value(&l, &big_injectors(), INJECTOR_FLOW_CHANNEL), 6);
    }

    // ----- the two together ----------------------------------------------------

    #[test]
    fn the_two_methods_agree_when_the_inputs_are_consistent() {
        // The heart of it. An engine swallowing 8.43 kg of air a minute at 12.5:1 is burning
        // 0.674 kg of fuel a minute, which is 905 cc of petrol: 22.6 per cent duty on four
        // 1,000 cc injectors at 7,000 rpm. Fed both, the two independent estimates must land
        // on the same horsepower.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "kPa", &[101.325, 101.325, 101.325]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
            ("AFR", "afr", &[12.5, 12.5, 12.5]),
            ("PW", "ms", &[4.176, 4.176, 4.176]),
        ]);

        let spec = EngineSpec {
            litres: 2.0,
            cylinders: 4,
            bsfc: 0.50,
            volumetric_efficiency: 100.0,
            injector_cc_per_minute: 1000.0,
            injector_dead_time_ms: 0.3,
            ..EngineSpec::default()
        };

        let air = value(&l, &spec, SPEED_DENSITY_CHANNEL);
        let fuel = value(&l, &spec, INJECTOR_POWER_CHANNEL);

        near(178.4, air, 1);
        in_range(fuel / air, 0.99, 1.01);

        // And the spread channel says so.
        in_range(value(&l, &spec, SPREAD_CHANNEL), -1.5, 1.5);
    }

    #[test]
    fn the_spread_is_what_says_a_ve_table_is_wrong() {
        // The diagnostic. The injectors are counting real fuel; the speed density figure
        // believes a VE that is twenty per cent optimistic. The spread is what makes that
        // visible rather than leaving two plausible numbers.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "kPa", &[101.325, 101.325, 101.325]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
            ("AFR", "afr", &[12.5, 12.5, 12.5]),
            ("PW", "ms", &[4.176, 4.176, 4.176]),
            ("VE", "%", &[120.0, 120.0, 120.0]),
        ]);

        let spec = EngineSpec {
            litres: 2.0,
            cylinders: 4,
            bsfc: 0.50,
            injector_cc_per_minute: 1000.0,
            injector_dead_time_ms: 0.3,
            ..EngineSpec::default()
        };

        let spread = value(&l, &spec, SPREAD_CHANNEL);

        // The injector figure sits about a sixth below the inflated air figure.
        in_range(spread, -20.0, -14.0);
    }

    // ----- mass air flow, torque and the wheels --------------------------------

    #[test]
    fn a_measured_airflow_is_used_directly() {
        // 8.43 kg/min is 140.5 g/s, and the same 178 horsepower.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAF", "g/s", &[140.5, 140.5, 140.5]),
            ("AFR", "afr", &[12.5, 12.5, 12.5]),
        ]);

        near(178.4, value(&l, &two_litre(), MAF_POWER_CHANNEL), 0);
    }

    #[test]
    fn torque_is_power_and_engine_speed_and_nothing_else() {
        let hp = value(&wide_open(), &two_litre(), SPEED_DENSITY_CHANNEL);
        let torque = value(&wide_open(), &two_litre(), TORQUE_CHANNEL);

        near(hp * 5252.0 / 7000.0, torque, 1);
        near(133.9, torque, 1);
    }

    #[test]
    fn the_wheel_figure_is_only_offered_when_a_loss_is_given() {
        let none = estimate(&wide_open(), &two_litre());

        assert!(!none.channels().iter().any(|c| c.name == WHEEL_CHANNEL));

        // Against the crank figure this log actually produced rather than against a rounded
        // copy of it, so the check is the fifteen per cent and not the rounding.
        let crank = value(&wide_open(), &two_litre(), SPEED_DENSITY_CHANNEL);
        let wheels = value(
            &wide_open(),
            &EngineSpec {
                drivetrain_loss_percent: 15.0,
                ..two_litre()
            },
            WHEEL_CHANNEL,
        );

        near(0.85, wheels / crank, 4);
    }

    // ----- what a log cannot support -------------------------------------------

    #[test]
    fn a_method_is_not_offered_when_the_log_cannot_feed_it() {
        // An OBD2 log with no pulse width and no air meter: speed density only.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "kPa", &[101.325, 101.325, 101.325]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
        ]);

        let result = estimate(&l, &two_litre());

        assert!(result.methods.iter().any(|m| m.name == "Speed density"));
        assert!(!result.methods.iter().any(|m| m.name == "Injectors"));
        assert!(!result.methods.iter().any(|m| m.name == "Mass air flow"));

        // And says what was wanted rather than going quiet about it.
        assert!(
            result
                .unavailable
                .iter()
                .any(|u| u.name == "Injectors" && u.needs.contains("pulse"))
        );
        assert!(result.unavailable.iter().any(|u| u.name == "Mass air flow"));
    }

    #[test]
    fn a_log_with_nothing_useful_offers_nothing_and_explains_itself() {
        let l = log(&[("Coolant", "C", &[80.0, 80.0, 80.0])]);

        let result = estimate(&l, &two_litre());

        assert!(result.methods.is_empty());
        assert_eq!(result.unavailable.len(), 3);

        assert!(result.unavailable.iter().all(|u| !u.needs.trim().is_empty()));

        // Named individually, so the user knows which sensor would unlock which.
        assert!(result.unavailable.iter().any(|u| u.needs.contains("engine speed")));
    }

    #[test]
    fn every_generated_expression_parses_on_the_log_it_was_built_for() {
        // The blanket check. Channel names with spaces in them, numbers formatted for the
        // current culture, a chain where each channel reads the one before: any of these could
        // break the parser, and all of them are generated rather than written out.
        let l = log(&[
            ("Engine Speed", "rpm", &[7000.0, 3000.0, 1000.0]),
            ("Manifold Absolute Pressure", "kPa", &[101.325, 60.0, 30.0]),
            ("Intake Air Temperature", "C", &[20.0, 25.0, 30.0]),
            ("Air Fuel Ratio", "afr", &[12.5, 14.7, 14.7]),
            ("Injector Pulse Width", "ms", &[10.3, 4.0, 2.0]),
            ("Fuel Rail Pressure", "kPa", &[300.0, 300.0, 300.0]),
            ("Mass Air Flow", "g/s", &[140.0, 60.0, 20.0]),
        ]);

        let result = estimate(&l, &big_injectors());
        let channels = result.channels();
        let built = math_channel::build(&l, &channels);

        assert!(built.problems.is_empty(), "{:?}", built.problems);
        assert_eq!(channels.len(), built.channels.len());

        // And every one of them produced real numbers rather than a column of NaN.
        for channel in &built.channels {
            assert!(channel.at(0).is_finite(), "{} produced nothing", channel.name());
        }
    }

    #[test]
    fn no_generated_channel_collides_with_one_the_log_already_has() {
        // The builder refuses a name the log already carries, and a collision would silently
        // cost whichever method owned it.
        let l = log(&[
            ("RPM", "rpm", &[7000.0, 7000.0, 7000.0]),
            ("MAP", "kPa", &[101.325, 101.325, 101.325]),
            ("IAT", "C", &[20.0, 20.0, 20.0]),
            ("PW", "ms", &[10.3, 10.3, 10.3]),
        ]);

        let channels = estimate(&l, &big_injectors()).channels();

        for channel in &channels {
            assert!(
                !l.channels()
                    .iter()
                    .any(|c| c.name().eq_ignore_ascii_case(&channel.name))
            );
        }

        // The names are distinct from each other too.
        let mut names: Vec<String> = channels.iter().map(|c| c.name.to_ascii_lowercase()).collect();
        names.sort();
        names.dedup();
        assert_eq!(channels.len(), names.len());
    }
}
