//! Ported from `ChannelRoles.cs`. Owned by package `w1-channels`.
//!
//! Finding the channel that does a particular job.
//!
//! Every firmware names these differently and none of them is wrong: a MegaSquirt logs CLT
//! and TPS, a MaxxECU logs "Throttle position", an OBD2 car logs "Coolant" and "Throttle
//! Position". Anything that looks for one spelling works on one make of controller and
//! silently does nothing on the others.
//!
//! Names are matched whole before they are matched loosely, and the units have to suit the
//! job. Without that, a MaxxECU's "TPS input voltage" is taken for the throttle, and
//! filtering on a sensor's raw volts rather than its position throws away the wrong samples
//! while looking like it worked.

use std::sync::Arc;

use crate::model::{LogChannel, LogDocument};

/// A job a channel does, whatever its firmware chose to call it.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ChannelRole {
    EngineSpeed,
    Coolant,
    Throttle,
    Mixture,
    MixtureTarget,
    ManifoldPressure,

    /// Whether the ECU is cutting fuel, however it says so.
    FuelCut,

    /// Charge temperature, which sets how much a given manifold pressure weighs.
    IntakeAir,

    /// Ambient pressure, where the ECU records it.
    Barometric,

    /// How long an injector is held open, before dead time is taken off.
    InjectorPulseWidth,

    /// The same as a proportion of the time available, where the ECU works it out.
    InjectorDuty,

    /// Rail pressure, which decides what an injector actually flows.
    FuelPressure,

    /// Measured air mass, on a car that has a meter for it.
    MassAirFlow,

    /// How completely each stroke fills, where the ECU reports it.
    VolumetricEfficiency,

    /// Road speed.
    VehicleSpeed,

    /// Ignition timing as finally commanded, after every correction.
    SparkAdvance,

    /// Timing taken away because knock was heard. Zero when none was.
    KnockRetard,

    /// Supply voltage, which sets how long an injector takes to open.
    BatteryVoltage,

    /// The closed-loop trim: how much the controller is moving fuelling to hit its target. A
    /// hundred per cent means it is leaving the table alone.
    MixtureCorrection,

    /// Extra fuel for a cold engine, as a percentage.
    WarmupCorrection,

    /// Manifold pressure above atmospheric, where the controller reports it separately.
    Boost,
}

/// The channel doing a job in a log, or `None`.
pub fn find(document: &LogDocument, role: ChannelRole) -> Option<&Arc<LogChannel>> {
    let names = aliases(role);

    // A whole-name match first. "Throttle position" should win over
    // "Throttle position sensor 2" wherever both exist.
    for alias in names {
        if let Some(exact) = document
            .channels()
            .iter()
            .find(|c| simplify(c.name()) == *alias && suits(role, c))
        {
            return Some(exact);
        }
    }

    for alias in names {
        if let Some(near) = document
            .channels()
            .iter()
            .find(|c| extends(&simplify(c.name()), alias) && suits(role, c))
        {
            return Some(near);
        }
    }

    None
}

/// Whether one channel name, on its own, plays a role: the same matching [`find`] does,
/// minus the units check it has no channel to make.
///
/// For a caller holding names rather than a [`LogDocument`]: a live session knows its own
/// column names long before anything assembles them into a document, and building one just
/// to ask which column is engine speed would copy every column to answer a question about
/// one. Looser than [`find`] by exactly the units guard, so prefer that wherever a document
/// is already in hand.
pub fn matches(channel_name: &str, role: ChannelRole) -> bool {
    if channel_name.trim().is_empty() {
        return false;
    }

    let simplified = simplify(channel_name);
    let names = aliases(role);

    names.iter().any(|alias| simplified == *alias) || names.iter().any(|alias| extends(&simplified, alias))
}

/// Whether a name is the alias with no more than a bank or sensor number after it: "afr1" or
/// "lambdaa", but never "afrload".
///
/// Matching any name that merely begins with the alias is too loose, and it showed: a
/// MegaSquirt log holds both "AFR" and "AFR Load", the second being the load axis its fuel
/// table is drawn against rather than a mixture at all. Suggesting a mixture filter on it
/// would throw away every sample outside a load range, while reading as though it had
/// filtered on AFR.
fn extends(name: &str, alias: &str) -> bool {
    let Some(rest) = name.strip_prefix(alias) else {
        return false;
    };

    // `rest.Length` in the C# counts UTF-16 units.
    match rest.encode_utf16().count() {
        0 => true,
        1 => {
            let c = rest.chars().next().unwrap_or('\0');
            c.is_ascii_digit() || c == 'a' || c == 'b'
        }
        2 => matches!(rest, "a1" | "a2" | "b1" | "b2" | "01" | "02"),
        _ => false,
    }
}

/// Whether a channel's units are right for the job.
///
/// The guard that keeps a MaxxECU's "TPS input voltage" from being taken for its throttle.
/// An empty unit is allowed everywhere, because plenty of firmware declares none at all.
fn suits(role: ChannelRole, channel: &LogChannel) -> bool {
    use ChannelRole::*;

    let units = simplify(channel.units());
    if units.is_empty() {
        return true;
    }
    let units = units.as_str();

    match role {
        EngineSpeed => units == "rpm",
        Coolant => matches!(units, "c" | "f" | "degc" | "degf" | "temp" | "k"),
        Throttle => matches!(units, "%" | "%tps"),
        ManifoldPressure => matches!(units, "kpa" | "psi" | "bar" | "mbar" | "inhg"),

        // A percentage on one controller, a bare code on another.
        FuelCut => matches!(units, "%" | "code" | "bits"),

        // A mixture is reported as a bare ratio as often as it is labelled, and "AFR",
        // "lambda" and ":1" are all in use.
        Mixture | MixtureTarget => matches!(units, "afr" | "lambda" | ":1" | "1" | "ratio"),

        IntakeAir => matches!(units, "c" | "f" | "degc" | "degf" | "temp" | "k"),

        Barometric | FuelPressure => {
            matches!(units, "kpa" | "psi" | "psig" | "bar" | "mbar" | "inhg" | "hpa")
        }

        // Guarded tightly, because a pulse width in milliseconds and an injector's duty in
        // per cent are both "how much fuel" and reading one as the other is out by two
        // orders of magnitude.
        InjectorPulseWidth => matches!(units, "ms" | "msec" | "s" | "sec" | "us"),
        InjectorDuty => matches!(units, "%" | "percent" | "pct"),

        MassAirFlow => matches!(
            units,
            "g/s" | "gs" | "kg/h" | "kgh" | "lb/min" | "lbmin" | "g/min" | "gmin" | "kg/min" | "kgmin"
        ),

        // "ratio" among them because that is how a rusEFI labels veValue, which is
        // nonetheless the same 0-120 figure everyone else calls a percentage rather than a
        // fraction of one.
        VolumetricEfficiency => matches!(units, "%" | "percent" | "pct" | "ratio"),

        VehicleSpeed => matches!(units, "km/h" | "kmh" | "kph" | "mph" | "m/s" | "ms"),

        SparkAdvance | KnockRetard => matches!(units, "deg" | "degrees" | "btdc" | "degbtdc" | "°"),

        BatteryVoltage => matches!(units, "v" | "volts" | "volt"),

        // Both are multipliers the controller reports as a percentage, and both sit near a
        // hundred rather than near nought.
        MixtureCorrection | WarmupCorrection => matches!(units, "%" | "percent" | "pct"),

        Boost => matches!(units, "psi" | "kpa" | "bar" | "mbar" | "psig"),
    }
}

/// The names a firmware gives a job, most specific first.
///
/// rusEFI suffixes its primary sensors with "Value" (RPMValue, MAPValue, TPSValue,
/// AFRValue), which no amount of loose matching reaches, since "value" is five characters
/// and a bank number is one. Every one of those had to be spelled out, and until they were,
/// a rusEFI matched six of these twenty-one roles: no engine speed, no throttle, no manifold
/// pressure and no mixture, which between them are most of what the insights, the suggested
/// filters and the VE calibration are built on.
fn aliases(role: ChannelRole) -> &'static [&'static str] {
    use ChannelRole::*;

    match role {
        EngineSpeed => &["rpm", "rpmvalue", "enginespeed", "engspeed", "tachometer"],

        Coolant => &[
            "clt",
            "coolant",
            "coolanttemp",
            "coolanttemperature",
            "enginecoolanttemperature",
            "ect",
            "watertemp",
            "wt",
        ],

        Throttle => &[
            "tps",
            "tpsvalue",
            "throttle",
            "throttleposition",
            "throttlepos",
            "tp",
            "relativethrottle",
            "pedal",
            "throttlepedalposition",
        ],

        Mixture => &[
            "afr",
            "afrvalue",
            "lambda",
            "lambdavalue",
            "afr1",
            "lambdaa",
            "wideband",
            "o2",
        ],

        // Checked before the plain mixture names would match, since "afrtarget" also
        // starts with "afr".
        MixtureTarget => &[
            "afrtarget",
            "afrtarget1",
            "afr1target",
            "lambdatarget",
            "targetafr",
            "targetlambda",
            "afrtgt",
            "afrtgt1",
            "egotarget",
            "lambdatarget1",
        ],

        ManifoldPressure => &[
            "map",
            "mapvalue",
            "manifoldpressure",
            "manifoldabsolutepressure",
            "mapkpa",
        ],

        // A cut is reported as a percentage on one controller and as a reason code on
        // another. Either way zero means it is not cutting, which is all this needs to know.
        FuelCut => &["fuelcut", "fuelcutreason", "fuelcutcode", "dfco", "decelfuelcut"],

        // The bare word last, which is all a rusEFI calls it. Safe only because the units
        // have to be a temperature: the same board logs "intake" pressures and valve
        // positions under names starting the same way, and every one of them is kept out by
        // that guard.
        IntakeAir => &[
            "iat",
            "mat",
            "intakeairtemperature",
            "intakeair",
            "intaketemp",
            "chargetemp",
            "airtemp",
            "manifoldairtemp",
            "act",
            "inlettemp",
            "intake",
        ],

        Barometric => &[
            "baro",
            "baropressure",
            "barometricpressure",
            "barometer",
            "ambientpressure",
        ],

        // What the engine is actually being given, after every correction, which is the
        // number that decides whether it knocks, rather than what any one table asked for.
        //
        // A rusEFI logs the advance twice, before its corrections and after. This role is
        // the figure actually commanded, so the corrected one is named and
        // "baseIgnitionAdvance" deliberately is not.
        SparkAdvance => &[
            "sparkadvance",
            "spksparkadvance",
            "advance",
            "correctedignitionadvance",
            "ignitionadvance",
            "timing",
            "ignadvance",
            "spkadvance",
            "sparkangle",
            "timingadvance",
            "ignitiontiming",
        ],

        // Deliberately not the bare word "knock": a great many firmwares log a raw
        // knock-sensor input under that name, and a sensor reading is not degrees taken
        // away. rusEFI's own retard is "m_knockRetard", whose sibling "m_knockLevel" is
        // exactly such a sensor reading and is kept out by the units having to be degrees.
        KnockRetard => &[
            "knockretard",
            "spkknockretard",
            "knockcorrection",
            "knockrtd",
            "totalknockretard",
            "mknockretard",
        ],

        BatteryVoltage => &[
            "battv",
            "batteryvoltage",
            "batt",
            "battery",
            "vbatt",
            "voltage",
            "supplyvoltage",
        ],

        // Bank one where a controller logs each separately, the banks being the same on any
        // engine this could describe.
        //
        // Deliberately not "stft" or "shorttermfueltrim". This role is a multiplier around a
        // hundred (a hundred meaning the loop is leaving the table alone) while an OBD2 fuel
        // trim is a percentage around nought, where zero is no correction and either sign
        // is meaningful. Read as the other, a 3 % trim multiplies the measured fuel error by
        // thirty, a negative one is skipped altogether, and a healthy closed loop is
        // reported as holding fuelling 98 % down. The same argument kept a rusEFI's warmup
        // coefficient out of WarmupCorrection.
        MixtureCorrection => &[
            "egocor1",
            "egocor",
            "egocorrection",
            "closedloopcorrection",
            "o2correction",
            "lambdacorrection",
            "gego",
        ],

        WarmupCorrection => &[
            "fuelwarmupcor",
            "warmupcor",
            "warmupenrichment",
            "wue",
            "warmupcorrection",
            "fuelwarmup",
            "warmup",
        ],

        Boost => &["boostpsi", "boost", "boostpressure", "manifoldgaugepressure"],

        // "pw" alone is what a MegaSquirt calls it; the rest spell it out. Bank one is taken
        // where a controller logs each bank separately, the two being the same on any
        // engine this could describe.
        InjectorPulseWidth => &[
            "pw",
            "pulsewidth",
            "injpw",
            "injectorpulsewidth",
            "injectorpw",
            "injpulsewidth",
            "fuelpw",
            "pulsewidth1",
            "actuallastinjection",
        ],

        InjectorDuty => &[
            "dutycycle",
            "injectorduty",
            "injduty",
            "duty",
            "idc",
            "injectordutycycle",
        ],

        // The low-pressure side first: on a port-injected engine that is the rail, and on a
        // direct-injected one it is still the pump feeding it, which is the pressure a pulse
        // width can be reasoned about against.
        FuelPressure => &[
            "fuelpressure",
            "fuelpress",
            "fuelrailpressure",
            "railpressure",
            "fp",
            "fuelp",
            "lowfuelpressure",
            "highfuelpressure",
        ],

        MassAirFlow => &["maf", "massairflow", "airflow", "airmassflow", "mafflow", "mafmeasured"],

        // "vevalue" rather than anything looser: a rusEFI also logs "veTableYAxis", which is
        // the load the table is looked up on and not a filling efficiency at all.
        VolumetricEfficiency => &["ve", "vevalue", "volumetricefficiency", "vecurrent", "vetable"],

        VehicleSpeed => &[
            "vss",
            "vehiclespeed",
            "vehiclespeedkph",
            "speed",
            "roadspeed",
            "gpsspeed",
        ],
    }
}

/// `char.ToLowerInvariant`: one character to one, else unchanged.
fn lower_char(c: char) -> char {
    let mut lower = c.to_lowercase();
    match (lower.next(), lower.next()) {
        (Some(l), None) => l,
        _ => c,
    }
}

/// A name reduced to something comparable: lower case, with spaces, underscores, dots and
/// hyphens taken out, since every firmware writes "Throttle position", "throttle_pos" and
/// "ThrottlePos" for the same thing.
pub(crate) fn simplify(text: &str) -> String {
    // A colon and a slash among them: firmware groups its channels with a prefix ("SPK:
    // Knock retard", "Fuel: Warmup cor") and that colon is a heading rather than part of the
    // name. Left in, every one of those channels is invisible to every alias.
    text.chars()
        .filter(|c| !matches!(c, ' ' | '_' | '.' | '-' | '\t' | '°' | ':' | '/'))
        .map(lower_char)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn log(channels: &[(&str, &str, &[f64])]) -> LogDocument {
        let built = channels
            .iter()
            .map(|(n, u, v)| Arc::new(LogChannel::new(n, u, 2, v, false)))
            .collect();
        LogDocument::new(
            "test.csv",
            "CSV",
            built,
            Arc::new(LogChannel::new("Time", "s", 3, &[0.0, 1.0, 2.0], true)),
        )
    }

    const MOVING: &[f64] = &[10.0, 20.0, 30.0];

    fn found(doc: &LogDocument, role: ChannelRole) -> Option<String> {
        find(doc, role).map(|c| c.name().to_string())
    }

    #[test]
    fn the_coolant_is_found_however_it_is_spelled() {
        for name in [
            "CLT",
            "Coolant",
            "Coolant temp",
            "coolant_temp",
            "Engine Coolant Temperature",
        ] {
            let doc = log(&[(name, "°C", MOVING)]);
            assert_eq!(found(&doc, ChannelRole::Coolant).as_deref(), Some(name));
        }
    }

    #[test]
    fn a_sensors_voltage_is_not_its_position() {
        let doc = log(&[("TPS input voltage", "V", MOVING), ("Throttle position", "%", MOVING)]);
        assert_eq!(found(&doc, ChannelRole::Throttle).as_deref(), Some("Throttle position"));
    }

    #[test]
    fn a_load_axis_is_not_a_mixture() {
        let doc = log(&[("AFR Load", "", MOVING), ("AFR", "", &[13.0, 14.7, 15.2])]);
        assert_eq!(found(&doc, ChannelRole::Mixture).as_deref(), Some("AFR"));
        assert!(!matches("AFR Load", ChannelRole::Mixture));
        assert!(matches("AFR1", ChannelRole::Mixture));
    }
}
