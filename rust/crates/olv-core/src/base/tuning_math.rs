//! Ported from `TuningMath.cs`: the arithmetic behind the tuning calculators.
//!
//! Kept apart from the window that shows it because these are the part that can be wrong in
//! a way nobody sees. A calculator that reads plausibly and is out by ten per cent sizes an
//! injector ten per cent small, and the engine finds out before the tuner does.
//!
//! Every figure here is either a definition or a published constant, and the ones that are
//! conventions rather than facts (brake specific fuel consumption above all) are named as
//! such where they are used.

/// A fuel, and the air-fuel ratio at which it burns completely.
///
/// Stoichiometry is what makes lambda useful: it is the same number on every fuel, where an
/// air-fuel ratio is not. Lambda 0.85 is the same richness whether the tank holds petrol or
/// ethanol; 12.5:1 is comfortably rich on petrol and dangerously lean on E85.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Fuel {
    Petrol,
    E10,
    E30,
    E50,
    E85,
    Ethanol,
    Methanol,
    Diesel,
    Lpg,
    Cng,
}

impl Fuel {
    /// Every fuel, in declaration order (`Enum.GetValues<Fuel>()`).
    pub const ALL: [Fuel; 10] = [
        Fuel::Petrol,
        Fuel::E10,
        Fuel::E30,
        Fuel::E50,
        Fuel::E85,
        Fuel::Ethanol,
        Fuel::Methanol,
        Fuel::Diesel,
        Fuel::Lpg,
        Fuel::Cng,
    ];
}

// TODO(fmt): .NET "N2" (culture-invariant, grouped, half away from zero on the decimal
// expansion). Placeholder until `olv_core::fmt` lands; `format!` rounds ties to even.
fn n2(value: f64) -> String {
    format!("{value:.2}")
}

// TODO(fmt): .NET "P0". Placeholder until `olv_core::fmt` lands. Note the C# uses the current
// culture: en-US gives "67%", the invariant culture gives "67 %". This placeholder is the former.
fn p0(fraction: f64) -> String {
    format!("{:.0}%", fraction * 100.0)
}

/// Air-fuel ratio at which each fuel burns completely.
///
/// The blends are computed from their constituents rather than quoted, so E30 and E50 are
/// consistent with E85 and with petrol rather than being three separate approximations.
pub fn stoichiometric(fuel: Fuel) -> f64 {
    match fuel {
        Fuel::Petrol => 14.7,
        Fuel::E10 => blend(0.10),
        Fuel::E30 => blend(0.30),
        Fuel::E50 => blend(0.50),
        Fuel::E85 => blend(0.85),
        Fuel::Ethanol => 9.0,
        Fuel::Methanol => 6.45,
        Fuel::Diesel => 14.5,
        Fuel::Lpg => 15.67,
        Fuel::Cng => 17.2,
    }
}

const PETROL_DENSITY: f64 = 0.745;

const ETHANOL_DENSITY: f64 = 0.789;

/// The fraction of a blend's mass that is ethanol.
///
/// The two fuels are mixed by volume and burn by mass, so everything that follows from a
/// blend (its stoichiometry, its energy) is weighted by this rather than by the volume
/// fraction on the pump.
///
/// The correction is small: E85 comes out at 9.81 where a straight volume average of 14.7
/// and 9.0 would say 9.86, and no blend differs by as much as one per cent. It is done this
/// way because it is the right derivation and it keeps the blends consistent with each other
/// and with their constituents, not because the difference would change a tune.
fn ethanol_mass_fraction(ethanol_by_volume: f64) -> f64 {
    let ethanol = ethanol_by_volume * ETHANOL_DENSITY;
    let petrol = (1.0 - ethanol_by_volume) * PETROL_DENSITY;

    ethanol / (ethanol + petrol)
}

/// An ethanol blend's stoichiometric ratio, weighted by mass.
fn blend(ethanol_by_volume: f64) -> f64 {
    let ethanol = ethanol_mass_fraction(ethanol_by_volume);

    // Air needed per unit of mixture mass, which is the ratio itself.
    (ethanol * 9.0) + ((1.0 - ethanol) * 14.7)
}

/// Density in kilograms per litre, for turning a mass flow into a volume.
///
/// Petrol is the awkward one: it is not a compound but a specification, and real pump fuel
/// runs from about 0.72 to 0.775 depending on the blend, the season and the temperature.
/// Everything downstream that converts a mass to a volume (injector cc, pump litres)
/// inherits that spread, so those figures are worth about three per cent either way whatever
/// precision they are printed to.
pub fn density(fuel: Fuel) -> f64 {
    match fuel {
        Fuel::Petrol => PETROL_DENSITY,
        Fuel::E10 => blend_density(0.10),
        Fuel::E30 => blend_density(0.30),
        Fuel::E50 => blend_density(0.50),
        Fuel::E85 => blend_density(0.85),
        Fuel::Ethanol => ETHANOL_DENSITY,
        Fuel::Methanol => 0.792,
        Fuel::Diesel => 0.832,
        Fuel::Lpg => 0.510,

        // Compressed to about 250 bar, where it is still a gas. A litre of it at the rail is
        // not the quantity a fuel system is specified in, which is why the pump and injector
        // figures do not mean much on this one.
        Fuel::Cng => 0.180,
    }
}

/// A blend's density, which unlike its stoichiometry is a volume average.
fn blend_density(ethanol_by_volume: f64) -> f64 {
    (ethanol_by_volume * ETHANOL_DENSITY) + ((1.0 - ethanol_by_volume) * PETROL_DENSITY)
}

/// Lower heating value, in megajoules per kilogram: the energy the fuel actually releases,
/// with the water it makes left as vapour.
///
/// This is what decides how much of a fuel an engine drinks. A given power is a given amount
/// of energy per hour, so a fuel carrying less of it per kilogram is burned in proportionally
/// greater mass, which is the whole of why E85 needs bigger injectors and a bigger pump.
pub fn energy_mj_per_kg(fuel: Fuel) -> f64 {
    match fuel {
        Fuel::Petrol => PETROL_ENERGY,
        Fuel::E10 => blend_energy(0.10),
        Fuel::E30 => blend_energy(0.30),
        Fuel::E50 => blend_energy(0.50),
        Fuel::E85 => blend_energy(0.85),
        Fuel::Ethanol => ETHANOL_ENERGY,
        Fuel::Methanol => 19.9,
        Fuel::Diesel => 42.6,
        Fuel::Lpg => 46.4,
        Fuel::Cng => 50.0,
    }
}

const PETROL_ENERGY: f64 = 43.4;

const ETHANOL_ENERGY: f64 = 26.8;

fn blend_energy(ethanol_by_volume: f64) -> f64 {
    let ethanol = ethanol_mass_fraction(ethanol_by_volume);

    (ethanol * ETHANOL_ENERGY) + ((1.0 - ethanol) * PETROL_ENERGY)
}

/// The name that fits in a column rather than in a sentence.
pub fn short_name(fuel: Fuel) -> &'static str {
    match fuel {
        Fuel::Petrol => "Petrol",
        Fuel::E10 => "E10",
        Fuel::E30 => "E30",
        Fuel::E50 => "E50",
        Fuel::E85 => "E85",
        Fuel::Ethanol => "Ethanol",
        Fuel::Methanol => "Methanol",
        Fuel::Diesel => "Diesel",
        Fuel::Lpg => "LPG",
        Fuel::Cng => "CNG",
    }
}

pub fn name(fuel: Fuel) -> &'static str {
    match fuel {
        Fuel::Petrol => "Petrol / gasoline",
        Fuel::E10 => "E10",
        Fuel::E30 => "E30",
        Fuel::E50 => "E50",
        Fuel::E85 => "E85",
        Fuel::Ethanol => "Ethanol (E100)",
        Fuel::Methanol => "Methanol",
        Fuel::Diesel => "Diesel",
        Fuel::Lpg => "LPG / propane",
        Fuel::Cng => "CNG / methane",
    }
}

// ----- mixture -------------------------------------------------------------

/// The air-fuel ratio a lambda corresponds to on a given fuel.
///
/// Nothing at or below zero is a mixture, and answering one with a negative ratio would put
/// a number on the screen that reads as a verdict.
pub fn afr_from_lambda(lambda: f64, fuel: Fuel) -> f64 {
    if lambda > 0.0 {
        lambda * stoichiometric(fuel)
    } else {
        f64::NAN
    }
}

/// The lambda an air-fuel ratio corresponds to on a given fuel.
pub fn lambda_from_afr(afr: f64, fuel: Fuel) -> f64 {
    let stoich = stoichiometric(fuel);

    if afr > 0.0 && stoich > 0.0 {
        afr / stoich
    } else {
        f64::NAN
    }
}

// ----- pressure ------------------------------------------------------------

/// Standard atmosphere, and the difference between gauge and absolute.
pub const ATMOSPHERIC_KPA: f64 = 101.325;

pub const KPA_PER_PSI: f64 = 6.894757293168361;

pub const KPA_PER_BAR: f64 = 100.0;

/// Boost as a gauge reading turned into absolute manifold pressure.
///
/// The distinction that catches people out: an ECU reports MAP absolutely, so atmospheric is
/// about 101 kPa and not zero, while a boost gauge reads zero at atmospheric. Ten psi of
/// boost is 170 kPa absolute, not 69.
///
/// Atmospheric is an argument rather than a constant because it is not one: the usual
/// default is [`ATMOSPHERIC_KPA`] (sea level), and a car a mile up is breathing nearer
/// 83 kPa, where the same ten psi on the gauge is 152 kPa absolute and a noticeably
/// different pressure ratio.
pub fn absolute_from_gauge(gauge_kpa: f64, atmospheric_kpa: f64) -> f64 {
    gauge_kpa + atmospheric_kpa
}

pub fn gauge_from_absolute(absolute_kpa: f64, atmospheric_kpa: f64) -> f64 {
    absolute_kpa - atmospheric_kpa
}

/// How much more air is packed in than atmospheric, which is what an engine's airflow
/// actually scales with.
pub fn pressure_ratio(absolute_kpa: f64, atmospheric_kpa: f64) -> f64 {
    if atmospheric_kpa > 0.0 {
        absolute_kpa / atmospheric_kpa
    } else {
        f64::NAN
    }
}

// ----- temperature ---------------------------------------------------------

/// Degrees Celsius from Fahrenheit.
///
/// Here rather than written out wherever it is wanted because it is the one conversion with
/// an offset as well as a factor, and an offset is the thing that gets applied in the wrong
/// order. Multiplying first gives a number that looks like a temperature and is out by
/// eighteen degrees.
pub fn celsius_from_fahrenheit(fahrenheit: f64) -> f64 {
    (fahrenheit - 32.0) * 5.0 / 9.0
}

pub fn fahrenheit_from_celsius(celsius: f64) -> f64 {
    (celsius * 9.0 / 5.0) + 32.0
}

// ----- altitude ------------------------------------------------------------

pub const METRES_PER_FOOT: f64 = 0.3048;

/// Barometric pressure at an altitude, on the ICAO standard atmosphere.
///
/// The troposphere model, which holds to eleven kilometres and so covers every road on earth
/// with a wide margin. Sea level is 101.325 kPa, five thousand feet is 84.3, and ten
/// thousand is 69.7: figures anyone can check against a published table.
///
/// It is the standard atmosphere and not the weather: a deep low or a hot day moves the real
/// figure a few kPa either side of this. Anyone with a barometric reading from their own ECU
/// should type that instead.
pub fn barometric_kpa(metres: f64) -> f64 {
    let ratio = 1.0 - (TEMPERATURE_LAPSE_RATE * metres / SEA_LEVEL_KELVIN);

    if ratio > 0.0 {
        ATMOSPHERIC_KPA * ratio.powf(BAROMETRIC_EXPONENT)
    } else {
        0.0
    }
}

/// The altitude a barometric pressure corresponds to, for going back the other way.
pub fn altitude_metres(barometric_kpa: f64) -> f64 {
    if barometric_kpa <= 0.0 {
        return f64::NAN;
    }

    let ratio = (barometric_kpa / ATMOSPHERIC_KPA).powf(1.0 / BAROMETRIC_EXPONENT);

    SEA_LEVEL_KELVIN / TEMPERATURE_LAPSE_RATE * (1.0 - ratio)
}

const TEMPERATURE_LAPSE_RATE: f64 = 0.0065;

const SEA_LEVEL_KELVIN: f64 = 288.15;

/// gM/RL, the exponent the barometric formula turns on.
const BAROMETRIC_EXPONENT: f64 = 5.255877;

// ----- compressor ----------------------------------------------------------

/// What an air filter and the pipe behind it cost the compressor inlet, as a figure to
/// assume when nothing better is known. A convention, and the one turbocharger makers use in
/// their own worked examples.
pub const TYPICAL_INLET_LOSS_KPA: f64 = KPA_PER_PSI;

/// The same for the intercooler and charge pipe between compressor and manifold. Two psi
/// rather than one, which is the figure the same worked examples use: an intercooler costs
/// more than a filter does.
pub const TYPICAL_CHARGE_LOSS_KPA: f64 = 2.0 * KPA_PER_PSI;

/// The pressures either side of a compressor, and the ratio between them.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Compressor {
    /// Absolute pressure the compressor is breathing.
    pub inlet_kpa: f64,
    /// Absolute pressure it has to deliver.
    pub outlet_kpa: f64,
    /// The second over the first, which is what a map is read on.
    pub ratio: f64,
}

/// The pressure ratio a compressor is actually being asked to work at.
///
/// Both pressures are taken at the compressor rather than at the manifold, which is what
/// makes this different from the boost figure. The inlet is below ambient because the filter
/// and the pipe to it cost something; the outlet is above manifold pressure because the
/// intercooler and charge pipe cost something too. Both losses push the ratio up, so leaving
/// them out flatters the turbocharger and picks one that runs out of map.
///
/// Altitude pushes it up as well, and harder than people expect: a gauge reads boost against
/// whatever the engine is breathing, so the same twelve psi asks for a ratio of 2.10 at sea
/// level and 2.34 at five thousand feet.
///
/// Not to be confused with [`charge_density_ratio`], which is taken against sea level rather
/// than against the local air, and moves the other way with altitude.
///
/// C# defaults: `barometricKpa = AtmosphericKpa`, `inletLossKpa = 0`, `chargeLossKpa = 0`.
pub fn compressor_pressures(
    boost_gauge_kpa: f64,
    barometric_kpa: f64,
    inlet_loss_kpa: f64,
    charge_loss_kpa: f64,
) -> Compressor {
    let inlet = barometric_kpa - inlet_loss_kpa;
    let outlet = barometric_kpa + boost_gauge_kpa + charge_loss_kpa;

    let ratio = if inlet > 0.0 && outlet > 0.0 {
        outlet / inlet
    } else {
        f64::NAN
    };

    Compressor {
        inlet_kpa: inlet,
        outlet_kpa: outlet,
        ratio,
    }
}

/// How dense the charge is against the standard atmosphere, which is the figure an airflow
/// in pounds per minute has to be scaled by.
///
/// Taken against sea level on purpose, and this is the trap in the whole tab. The
/// compressor's ratio is measured against the air it is breathing, so it climbs as you go
/// up. This one is measured against the standard density that the 0.0765 lb/ft³ constant is
/// quoted at, so it falls.
///
/// Dividing this by the local barometric instead would cancel the altitude straight back out
/// and overstate the mass flow by a fifth at five thousand feet.
pub fn charge_density_ratio(manifold_absolute_kpa: f64) -> f64 {
    manifold_absolute_kpa / ATMOSPHERIC_KPA
}

// ----- airflow -------------------------------------------------------------

/// Cubic inches in a litre, for the airflow formula, which is imperial.
pub const CUBIC_INCHES_PER_LITRE: f64 = 61.023744;

/// Air an engine demands, in cubic feet per minute.
///
/// The standard four-stroke formula: displacement times speed, halved because a four-stroke
/// draws in once every two revolutions, and divided by 1,728 to turn cubic inches into cubic
/// feet. The 3,456 below is those two together.
///
/// Volumetric efficiency is how completely each stroke fills: around 80 per cent for an
/// ordinary engine, near 100 for a good one on song. Under boost the whole figure scales with
/// the pressure ratio, because that is what forcing more air in means.
///
/// C# default: `pressureRatio = 1`.
pub fn cubic_feet_per_minute(litres: f64, rpm: f64, ve_percent: f64, pressure_ratio: f64) -> f64 {
    if litres <= 0.0 || rpm <= 0.0 {
        return 0.0;
    }

    let cubic_inches = litres * CUBIC_INCHES_PER_LITRE;

    cubic_inches * rpm * (ve_percent / 100.0) * pressure_ratio / 3456.0
}

/// The same in cubic metres per hour, for anyone not working in feet.
pub fn cubic_metres_per_hour(cubic_feet_per_minute: f64) -> f64 {
    cubic_feet_per_minute * 0.0283168 * 60.0
}

/// Air mass in pounds per minute, which is how turbocharger maps are drawn.
///
/// At the standard density of dry air at sea level and 15 °C. A compressor map is read in
/// these units, so this is the number that says which turbocharger an engine wants.
///
/// The assumption worth knowing about: taking a boosted volume flow at standard density is
/// taking the charge as intercooled the whole way back to ambient, which nothing does. A
/// real charge at 45 °C is about a tenth less dense than that, so under boost this reads
/// about a tenth high. It errs towards the larger turbocharger, which is the safe direction.
pub fn air_pounds_per_minute(cubic_feet_per_minute: f64) -> f64 {
    cubic_feet_per_minute * 0.0764742
}

/// The BSFC a reasonably efficient engine actually achieves at full throttle, which is not
/// the same use of the number as sizing.
///
/// The 0.60 used for injectors and pumps is deliberately pessimistic: it oversizes, and
/// oversizing a pump costs money where undersizing one costs an engine. Estimating power is
/// the other direction: a pessimistic figure there quietly understates what the engine makes.
pub const FULL_THROTTLE_BSFC: f64 = 0.50;

/// The power a given mass of air can make, on a given fuel.
///
/// Air is the thing an engine is actually short of, so this is the sizing arithmetic run
/// backwards: the air divided by the ratio gives the fuel, and the fuel divided by the BSFC
/// gives the power.
///
/// The result that surprises people: it is very nearly the same number on every fuel. A
/// kilogram of air carries about 2.95 MJ of petrol with it at stoichiometric, 2.98 of ethanol
/// and 3.09 of methanol. What actually makes the alcohols worth having is that they resist
/// knock and cool the charge, which arrives as more air, not as more power per pound of it.
///
/// `lambda` is the mixture at full throttle, not at cruise. `petrol_bsfc` is the efficiency
/// as it would be on petrol; the fuel's own figure is scaled from it by [`suggested_bsfc`].
pub fn horsepower_from_air(air_pounds_per_minute: f64, fuel: Fuel, lambda: f64, petrol_bsfc: f64) -> f64 {
    let afr = afr_from_lambda(lambda, fuel);
    let bsfc = suggested_bsfc(fuel, petrol_bsfc);

    if air_pounds_per_minute <= 0.0 || afr.is_nan() || afr <= 0.0 || bsfc.is_nan() || bsfc <= 0.0 {
        return 0.0;
    }

    air_pounds_per_minute * 60.0 / (afr * bsfc)
}

// ----- injectors -----------------------------------------------------------

/// Brake specific fuel consumption worth assuming when nothing better is known: pounds of
/// fuel per horsepower per hour.
///
/// A convention rather than a fact, and the figure the whole injector calculation rests on.
/// Naturally aspirated petrol engines sit near 0.45 to 0.50; boosted ones are run richer for
/// safety and land nearer 0.55 to 0.65.
pub const NATURALLY_ASPIRATED_BSFC: f64 = 0.48;

pub const BOOSTED_BSFC: f64 = 0.60;

/// What a compression-ignition engine runs at, which is not a scaling of anything above.
pub const DIESEL_BSFC: f64 = 0.36;

/// The brake specific fuel consumption to expect on a fuel, given the figure that would
/// apply on petrol in the same engine.
///
/// Scaled by energy content, because that is the thing that actually varies: a given power
/// is a given amount of energy per hour, so a fuel carrying less of it per kilogram is burned
/// in proportionally greater mass. E85 carries about two thirds of petrol's energy by mass
/// and wants half again the BSFC; methanol carries under half and wants a little over twice.
/// LPG and CNG carry more than petrol and want slightly less.
///
/// Diesel is answered with a figure of its own rather than by this scaling: it carries almost
/// exactly as much energy per kilogram as petrol, so scaling would hand back petrol's number
/// where the engine burning it runs near 0.36.
pub fn suggested_bsfc(fuel: Fuel, petrol_bsfc: f64) -> f64 {
    if fuel == Fuel::Diesel {
        return DIESEL_BSFC;
    }

    let energy = energy_mj_per_kg(fuel);

    if energy > 0.0 && petrol_bsfc > 0.0 {
        petrol_bsfc * PETROL_ENERGY / energy
    } else {
        f64::NAN
    }
}

/// The petrol figure a BSFC on some other fuel came from: the inverse of [`suggested_bsfc`].
///
/// For following a fuel change without throwing away what was typed. Diesel has no petrol
/// equivalent, because its figure never came from one.
pub fn petrol_equivalent_bsfc(fuel: Fuel, bsfc: f64) -> f64 {
    if fuel == Fuel::Diesel || bsfc.is_nan() || bsfc <= 0.0 {
        return f64::NAN;
    }

    let energy = energy_mj_per_kg(fuel);

    if energy > 0.0 {
        bsfc * energy / PETROL_ENERGY
    } else {
        f64::NAN
    }
}

/// Typical BSFC for every fuel, aspirated and boosted, as a table.
///
/// Worked out from the energy content rather than written down, so the legend cannot drift
/// away from what the calculator actually does with the figure.
///
/// Lines are joined with `\n` (the C# used `Environment.NewLine`, which is `\r\n` on the
/// Windows machines it ran on).
pub fn bsfc_legend(highlight: Fuel) -> String {
    let mut lines = vec![format!("  {:<9} {:>5} {:>8}", "", "NA", "boosted")];

    for fuel in Fuel::ALL {
        // Diesel is a fixed figure rather than a scaling, and it is the same one whether the
        // engine is boosted or not: most are.
        let (na, boosted) = if fuel == Fuel::Diesel {
            (n2(DIESEL_BSFC), "\u{2014}".to_string())
        } else {
            (
                n2(suggested_bsfc(fuel, NATURALLY_ASPIRATED_BSFC)),
                n2(suggested_bsfc(fuel, BOOSTED_BSFC)),
            )
        };

        lines.push(format!(
            "{} {:<9} {:>5} {:>8}",
            if fuel == highlight { "\u{25B8}" } else { " " },
            short_name(fuel),
            na,
            boosted
        ));
    }

    lines.join("\n")
}

/// The BSFC worth typing for a fuel, as the line that sits beside the box.
pub fn bsfc_hint(fuel: Fuel) -> String {
    if fuel == Fuel::Diesel {
        format!("lb/hp/hr \u{2014} about {}", n2(DIESEL_BSFC))
    } else {
        format!(
            "lb/hp/hr \u{2014} {} NA, {} boosted",
            n2(suggested_bsfc(fuel, NATURALLY_ASPIRATED_BSFC)),
            n2(suggested_bsfc(fuel, BOOSTED_BSFC))
        )
    }
}

/// What the fuel changes about sizing an injector, in words.
///
/// Here rather than in the window because it was wrong in the window and nothing could see
/// it. A single sentence written around E85 was shown to anyone who picked any fuel but
/// petrol or diesel. It was low for E85, less than half of what methanol wants, and the wrong
/// direction entirely for LPG and CNG.
///
/// The advice a calculator gives is as much its output as the numbers are, and a tuner acts
/// on it just as directly.
pub fn bsfc_guidance(fuel: Fuel) -> String {
    if fuel == Fuel::Petrol {
        return "BSFC is a convention rather than a measurement. If you know yours from a \
                previous tune, use it \u{2014} it is the figure everything here rests on."
            .to_string();
    }

    if fuel == Fuel::Diesel {
        return format!(
            "A diesel's fuel consumption is set by the engine's efficiency rather than by \
             the fuel, so it does not scale from petrol's: figure on {}. And a \
             common-rail injector is rated in cubic millimetres per stroke at over a thousand \
             bar, not in cc/min at three, so the sizing above does not describe one.",
            n2(DIESEL_BSFC)
        );
    }

    let share = energy_mj_per_kg(fuel) / energy_mj_per_kg(Fuel::Petrol);
    let boosted = suggested_bsfc(fuel, BOOSTED_BSFC);

    let gas = if matches!(fuel, Fuel::Lpg | Fuel::Cng) {
        " Being a gas, it is also neither injected nor measured the way the figures above assume."
    } else {
        ""
    };

    if share < 1.0 {
        format!(
            "{} carries about {} of petrol's energy per kilogram, so the same \
             power takes proportionally more of it: figure on {} where petrol would be \
             {}. Left at petrol's, the sizing above comes out at that same \
             {} of the injector the fuel actually needs.{}",
            name(fuel),
            p0(share),
            n2(boosted),
            n2(BOOSTED_BSFC),
            p0(share),
            gas
        )
    } else {
        format!(
            "{} carries about {} of petrol's energy per kilogram, so the same \
             power takes slightly less of it by mass: figure on {} where petrol would \
             be {}.{}",
            name(fuel),
            p0(share),
            n2(boosted),
            n2(BOOSTED_BSFC),
            gas
        )
    }
}

/// The flow one injector needs, in pounds per hour.
///
/// Duty cycle is deliberately not 100 per cent: an injector held open near its limit has no
/// headroom for a cold start or a hot restart, and above about 85 per cent many stop flowing
/// linearly, so the tune stops matching what the ECU thinks it is commanding.
pub fn injector_pounds_per_hour(horsepower: f64, cylinders: i32, bsfc: f64, duty_cycle_percent: f64) -> f64 {
    if horsepower <= 0.0 || cylinders <= 0 || bsfc <= 0.0 {
        return 0.0;
    }

    let duty = duty_cycle_percent / 100.0;
    if duty <= 0.0 {
        return 0.0;
    }

    horsepower * bsfc / (f64::from(cylinders) * duty)
}

/// The same flow in cubic centimetres per minute, which is how most of the world sizes
/// injectors.
///
/// Computed from the fuel's density rather than with the usual constant of 10.5. That
/// constant is a density of 0.72 kg/L: a light petrol, and not the 0.745 assumed here, which
/// gives 10.15. On E85 it is nine per cent high and on methanol ten.
///
/// Note the direction, because it is the opposite of what gets said about it: too large a
/// constant asks for more cc than the mass needs, so 10.5 oversizes an injector on ethanol
/// rather than undersizing it. What undersizes an injector on ethanol is leaving the BSFC at
/// petrol's, which is worth half again and is what [`suggested_bsfc`] is for.
///
/// Ignored here, as everywhere else: an injector's rated flow is measured on a test fluid,
/// and a denser fuel flows a couple of per cent less volume through the same orifice at the
/// same pressure.
///
/// The C# default for `fuel` is [`Fuel::Petrol`].
pub fn cc_per_minute(pounds_per_hour: f64, fuel: Fuel) -> f64 {
    let density = density(fuel);

    if density > 0.0 {
        pounds_per_hour * 453.59237 / (60.0 * density)
    } else {
        0.0
    }
}

/// The C# default for `fuel` is [`Fuel::Petrol`].
pub fn pounds_per_hour_from_cc(cc_per_minute: f64, fuel: Fuel) -> f64 {
    cc_per_minute * 60.0 * density(fuel) / 453.59237
}

/// The power a set of injectors can support, which is the question asked when the injectors
/// are already fitted.
pub fn supported_horsepower(pounds_per_hour_each: f64, cylinders: i32, bsfc: f64, duty_cycle_percent: f64) -> f64 {
    if bsfc <= 0.0 {
        return 0.0;
    }

    pounds_per_hour_each * f64::from(cylinders) * (duty_cycle_percent / 100.0) / bsfc
}

/// How an injector's flow changes with the pressure across it.
///
/// Flow follows the square root of pressure, so a 550 cc injector rated at three bar flows
/// about 635 at four. Worth having because injectors are rated at one pressure and fitted to
/// engines running another, and the error goes unnoticed.
pub fn flow_at_pressure(rated_flow: f64, rated_kpa: f64, actual_kpa: f64) -> f64 {
    if rated_kpa <= 0.0 || actual_kpa <= 0.0 {
        return 0.0;
    }

    rated_flow * (actual_kpa / rated_kpa).sqrt()
}

// ----- fuel pump -----------------------------------------------------------

/// The fuel a given power needs, in litres per hour at the rail.
///
/// A pump's rating is not this figure. Pumps are rated at a pressure and flow less as
/// pressure rises, and a pump run at its limit runs hot and dies, so the headroom below is
/// applied on top rather than left as an exercise.
///
/// The C# default for `fuel` is [`Fuel::Petrol`].
pub fn fuel_litres_per_hour(horsepower: f64, bsfc: f64, fuel: Fuel) -> f64 {
    let density = density(fuel);
    if density <= 0.0 || horsepower <= 0.0 || bsfc <= 0.0 {
        return 0.0;
    }

    let pounds_per_hour = horsepower * bsfc;

    pounds_per_hour * 0.45359237 / density
}

/// The pump to look for, with headroom.
///
/// Twenty per cent by convention (the C# default for `headroomPercent`): a pump chosen with
/// none is a pump at full output whenever the engine is, which is where they get hot and
/// fail. The C# default for `fuel` is [`Fuel::Petrol`].
pub fn pump_litres_per_hour(horsepower: f64, bsfc: f64, fuel: Fuel, headroom_percent: f64) -> f64 {
    fuel_litres_per_hour(horsepower, bsfc, fuel) * (1.0 + (headroom_percent / 100.0))
}

/// US gallons in a litre.
///
/// From the gallon's own definition (3.785411784 litres exactly) rather than from a rounded
/// reciprocal, because it is used on figures that are then compared against a pump's printed
/// rating.
pub const US_GALLONS_PER_LITRE: f64 = 1.0 / 3.785411784;

/// Litres an hour as US gallons a minute, which is how larger pumps are quoted.
pub fn gallons_per_minute(litres_per_hour: f64) -> f64 {
    litres_per_hour * US_GALLONS_PER_LITRE / 60.0
}

// ----- pumps as they are actually sold --------------------------------------

/// A pump as it appears in a catalogue, rather than as a bare flow figure.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FuelPump {
    pub maker: &'static str,
    pub part: &'static str,
    pub nickname: &'static str,
    /// Flow at `rated_psi` and 13.5 volts.
    pub litres_per_hour: f64,
    /// The pressure the headline figure is quoted at, which is nearly always lower than the
    /// pressure the pump will actually see.
    pub rated_psi: f64,
    /// Whether the maker rates it for ethanol. A pump that is not will pass fuel perfectly
    /// well for a while and then fail, which is the worst way to find out.
    pub alcohol_safe: bool,
}

impl FuelPump {
    pub fn name(&self) -> String {
        format!("{} {}", self.maker, self.part)
    }
}

const fn pump(
    maker: &'static str,
    part: &'static str,
    nickname: &'static str,
    litres_per_hour: f64,
    rated_psi: f64,
    alcohol_safe: bool,
) -> FuelPump {
    FuelPump {
        maker,
        part,
        nickname,
        litres_per_hour,
        rated_psi,
        alcohol_safe,
    }
}

/// A short catalogue of the in-tank pumps most commonly fitted.
///
/// Deliberately short, and deliberately the well-known ones: a long list would be more useful
/// and much harder to keep true, and a suggestion made from a stale figure is worse than no
/// suggestion. Every entry carries the pressure its rating was taken at rather than the bare
/// number, because the bare number is the thing people compare wrongly.
///
/// These figures are transcribed from published specifications and are the part of this file
/// that goes out of date on its own. Check against the maker's current data before buying
/// anything on the strength of it.
pub const PUMPS: [FuelPump; 8] = [
    pump("Walbro", "GSS250", "190", 190.0, 43.5, false),
    pump("Walbro", "GSS341", "255 offset", 255.0, 43.5, false),
    pump("Walbro", "GSS342", "255", 255.0, 43.5, false),
    pump("AEM", "50-1000", "320", 320.0, 40.0, true),
    pump("AEM", "50-1009", "400", 400.0, 40.0, true),
    pump("Walbro", "F90000262", "400", 400.0, 40.0, false),
    pump("Walbro", "F90000267", "450", 450.0, 40.0, true),
    pump("Walbro", "F90000285", "525", 525.0, 40.0, true),
];

/// How much of its rating a pump still makes at a higher pressure.
///
/// Doubling the pressure above the rating costs about forty per cent of the flow. Fitted to
/// the published curves for the 255, which loses roughly a quarter by 70 psi and about two
/// fifths by 87, and applied to the rest, so it is an approximation across pumps that
/// genuinely differ, not a specification.
pub const PUMP_PRESSURE_FALLOFF: f64 = 0.40;

/// The flow a pump still delivers at the pressure it will actually see.
///
/// Not the square-root law that governs injectors: an injector is an orifice with a pressure
/// drop across it, where a pump is a pump working against a head, and the two fall off quite
/// differently.
pub fn pump_flow_at_pressure(pump: &FuelPump, psi: f64) -> f64 {
    if psi <= 0.0 || pump.rated_psi <= 0.0 {
        return f64::NAN;
    }

    let scale = 1.0 - (PUMP_PRESSURE_FALLOFF * (psi - pump.rated_psi) / pump.rated_psi);

    // Math.Max(x, 0): x is never NaN here.
    (pump.litres_per_hour * scale).max(0.0)
}

/// What each additional pump in parallel is worth, against the first.
///
/// Not another whole pump: they share a line, a filter and a regulator, and the pressure drop
/// through all three rises with the flow going through them. A tenth is a convention rather
/// than a measurement, and it is the conservative direction.
pub const PARALLEL_PUMP_EFFICIENCY: f64 = 0.90;

/// Flow from several of the same pump plumbed together.
pub fn parallel_flow(single_flow: f64, count: i32) -> f64 {
    if count <= 0 {
        0.0
    } else {
        single_flow * (1.0 + (f64::from(count - 1) * PARALLEL_PUMP_EFFICIENCY))
    }
}

/// A pump, and how many of it the job takes.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PumpChoice {
    pub pump: FuelPump,
    pub count: i32,
    pub delivered_litres_per_hour: f64,
}

/// Most a sane fuel system runs in parallel before it should be something else.
pub const MOST_PUMPS_WORTH_WIRING: i32 = 3;

/// Whether a fuel needs a pump the maker rates for alcohol.
///
/// E10 is not on this list because pumps have been built to survive it for decades.
/// Everything above it is, and methanol is harsher than any of them: a pump rated for E85 is
/// not thereby rated for methanol.
pub fn needs_alcohol_safe_pump(fuel: Fuel) -> bool {
    matches!(fuel, Fuel::E30 | Fuel::E50 | Fuel::E85 | Fuel::Ethanol | Fuel::Methanol)
}

/// Pumps that would do the job, fewest first and least wasteful after that.
///
/// Empty means nothing in the catalogue gets there within [`MOST_PUMPS_WORTH_WIRING`], which
/// is the answer's way of saying the car wants a mechanical pump rather than another in-tank
/// one.
pub fn suggest_pumps(litres_per_hour_needed: f64, rail_psi: f64, alcohol_safe: bool) -> Vec<PumpChoice> {
    if litres_per_hour_needed.is_nan() || litres_per_hour_needed <= 0.0 || rail_psi.is_nan() || rail_psi <= 0.0 {
        return Vec::new();
    }

    let mut choices: Vec<PumpChoice> = Vec::new();

    for pump in PUMPS.iter() {
        if alcohol_safe && !pump.alcohol_safe {
            continue;
        }

        let single = pump_flow_at_pressure(pump, rail_psi);
        if single.is_nan() || single <= 0.0 {
            continue;
        }

        for count in 1..=MOST_PUMPS_WORTH_WIRING {
            let delivered = parallel_flow(single, count);

            if delivered >= litres_per_hour_needed {
                choices.push(PumpChoice {
                    pump: *pump,
                    count,
                    delivered_litres_per_hour: delivered,
                });
                break;
            }
        }
    }

    // OrderBy(Count).ThenBy(Delivered), both stable.
    choices.sort_by(|a, b| {
        a.count.cmp(&b.count).then_with(|| {
            a.delivered_litres_per_hour
                .partial_cmp(&b.delivered_litres_per_hour)
                .unwrap_or(std::cmp::Ordering::Equal)
        })
    });

    choices
}

#[cfg(test)]
mod tests {
    use super::*;

    /// xunit's `Assert.Equal(expected, actual, precision)`: equal once both are rounded to
    /// `precision` decimal places.
    fn assert_equal(expected: f64, actual: f64, precision: usize) {
        let scale = 10f64.powi(precision as i32);
        assert_eq!(
            (expected * scale).round(),
            (actual * scale).round(),
            "expected {expected}, got {actual}"
        );
    }

    fn in_range(actual: f64, low: f64, high: f64) {
        assert!((low..=high).contains(&actual), "{actual} is not in [{low}, {high}]");
    }

    const SEA: f64 = ATMOSPHERIC_KPA;

    fn absolute(gauge: f64) -> f64 {
        absolute_from_gauge(gauge, SEA)
    }

    fn compressor(boost: f64) -> Compressor {
        compressor_pressures(boost, SEA, 0.0, 0.0)
    }

    fn cfm(litres: f64, rpm: f64, ve: f64) -> f64 {
        cubic_feet_per_minute(litres, rpm, ve, 1.0)
    }

    fn pump_litres(hp: f64, bsfc: f64) -> f64 {
        pump_litres_per_hour(hp, bsfc, Fuel::Petrol, 20.0)
    }

    // ----- mixture -------------------------------------------------------------

    #[test]
    fn petrol_is_stoichiometric_at_the_familiar_number() {
        assert_eq!(stoichiometric(Fuel::Petrol), 14.7);
        assert_equal(14.7, afr_from_lambda(1.0, Fuel::Petrol), 3);
        assert_equal(1.0, lambda_from_afr(14.7, Fuel::Petrol), 6);
    }

    /// Widely quoted as 9.7 to 9.8. Worth checking, because a volume average of 14.7 and 9.0
    /// gives 9.86: near enough to look right and wrong for the wrong reason. The two fuels
    /// mix by volume and burn by mass.
    #[test]
    fn e85_lands_where_the_published_figure_does() {
        in_range(stoichiometric(Fuel::E85), 9.6, 9.9);
    }

    /// Pinned because the reason for doing it this way was once written down as a difference
    /// of half a ratio, and it is a twentieth of one. The derivation is right; the size of it
    /// is not an argument for anything, and a calculator that oversells its own precision is
    /// not trustworthy about the rest.
    #[test]
    fn mass_weighting_a_blend_is_a_correction_of_under_one_percent() {
        for by_volume in [0.10, 0.30, 0.50, 0.85] {
            let fuel = match by_volume {
                0.10 => Fuel::E10,
                0.30 => Fuel::E30,
                0.50 => Fuel::E50,
                _ => Fuel::E85,
            };

            let by_mass = stoichiometric(fuel);
            let by_volume_average = (by_volume * 9.0) + ((1.0 - by_volume) * 14.7);

            in_range((by_mass - by_volume_average).abs() / by_mass, 0.0, 0.01);
        }
    }

    #[test]
    fn a_density_is_published_for_every_fuel_and_blends_fall_between() {
        for fuel in Fuel::ALL {
            assert!(density(fuel) > 0.0, "{fuel:?} has no density");
        }

        assert_equal(0.7824, density(Fuel::E85), 4);
        in_range(density(Fuel::E50), density(Fuel::Petrol), density(Fuel::Ethanol));
    }

    // ----- energy and BSFC -----------------------------------------------------

    /// The figure that decides the injector, and the one this window used to get wrong: it
    /// offered "0.75 to 0.85 on E85 where petrol would be 0.60", a scaling of 1.25 to 1.42
    /// where the energy content says 1.49, and offered the same sentence to anyone who picked
    /// methanol, which wants more than twice.
    #[test]
    fn e85_wants_half_again_the_petrol_bsfc_and_methanol_twice() {
        in_range(suggested_bsfc(Fuel::E85, 0.60), 0.86, 0.92);
        in_range(suggested_bsfc(Fuel::Methanol, 0.60), 1.25, 1.35);
        assert_equal(0.60, suggested_bsfc(Fuel::Petrol, 0.60), 6);
    }

    /// Following E85's figure on methanol sizes the injector at not much over two thirds of
    /// what it needs, which is the failure this exists to prevent.
    #[test]
    fn methanol_is_not_given_ethanols_advice_even_though_both_are_alcohols() {
        let e85 = suggested_bsfc(Fuel::E85, 0.60);
        let methanol = suggested_bsfc(Fuel::Methanol, 0.60);

        assert!(
            methanol > e85 * 1.3,
            "methanol wanted {methanol:.2} against E85's {e85:.2} - too close to be right"
        );
    }

    /// The direction that a sentence written around ethanol would get wrong: not every
    /// alternative fuel needs more of it.
    #[test]
    fn gaseous_fuels_carry_more_energy_and_want_a_lower_bsfc() {
        assert!(suggested_bsfc(Fuel::Lpg, 0.60) < 0.60);
        assert!(suggested_bsfc(Fuel::Cng, 0.60) < 0.60);
    }

    /// Diesel fuel is within two per cent of petrol's energy per kilogram, so scaling would
    /// hand back 0.61 for an engine that runs near 0.36.
    #[test]
    fn diesel_is_answered_by_its_engine_rather_than_by_scaling_petrol() {
        assert_equal(DIESEL_BSFC, suggested_bsfc(Fuel::Diesel, 0.60), 6);
        in_range(suggested_bsfc(Fuel::Diesel, 0.60), 0.30, 0.42);
    }

    /// The failure this replaced: one sentence quoting E85's BSFC was shown for every fuel
    /// but petrol and diesel. Each fuel's advice should now contain the figure that fuel
    /// actually wants.
    #[test]
    fn every_fuel_is_advised_with_its_own_figure_rather_than_anothers_sentence() {
        for fuel in Fuel::ALL {
            let guidance = bsfc_guidance(fuel);

            assert!(!guidance.trim().is_empty(), "{fuel:?} has no guidance");

            if fuel == Fuel::Petrol {
                continue;
            }

            let want = suggested_bsfc(fuel, BOOSTED_BSFC);

            assert!(guidance.contains(&n2(want)));

            // And the short line beside the box agrees with the long one below it.
            let expected = if fuel == Fuel::Diesel {
                n2(DIESEL_BSFC)
            } else {
                n2(want)
            };
            assert!(bsfc_hint(fuel).contains(&expected));
        }
    }

    /// Specifically: methanol must not be handed E85's 0.89, which is what choosing
    /// methanol used to produce.
    #[test]
    fn no_fuel_is_told_to_use_the_figure_that_belongs_to_another() {
        let methanol = bsfc_guidance(Fuel::Methanol);

        assert!(methanol.contains("1.31"));
        assert!(!methanol.contains("0.89"));
        assert!(!methanol.contains("E85"));
    }

    /// A sentence generalised from ethanol says every alternative fuel needs more. LPG and
    /// CNG need less, and neither is injected as a liquid.
    #[test]
    fn the_gases_are_told_they_need_less_rather_than_more() {
        for fuel in [Fuel::Lpg, Fuel::Cng] {
            let guidance = bsfc_guidance(fuel);

            assert!(guidance.contains("less"));
            assert!(guidance.contains("gas"));
        }
    }

    /// Cc per minute at three bar is not how a common-rail injector is specified, and the
    /// tab will happily print one anyway.
    #[test]
    fn the_diesel_warning_says_the_sizing_does_not_describe_it() {
        let guidance = bsfc_guidance(Fuel::Diesel);

        assert!(guidance.contains("0.36"));
        assert!(guidance.contains("does not describe"));
    }

    /// The two are the same statement, and this is the check that they stay that way if
    /// either table is edited.
    #[test]
    fn bsfc_scales_in_step_with_how_much_energy_the_fuel_is_short_of() {
        for fuel in Fuel::ALL {
            if fuel == Fuel::Diesel {
                continue;
            }

            let energy_share = energy_mj_per_kg(fuel) / energy_mj_per_kg(Fuel::Petrol);

            assert_equal(0.60 / energy_share, suggested_bsfc(fuel, 0.60), 6);
        }
    }

    #[test]
    fn blends_sit_in_order_between_their_constituents() {
        let petrol = stoichiometric(Fuel::Petrol);
        let ethanol = stoichiometric(Fuel::Ethanol);

        let blends = [
            stoichiometric(Fuel::E10),
            stoichiometric(Fuel::E30),
            stoichiometric(Fuel::E50),
            stoichiometric(Fuel::E85),
        ];

        for b in blends {
            in_range(b, ethanol, petrol);
        }
        assert!(
            blends.windows(2).all(|p| p[1] < p[0]),
            "more ethanol should mean a lower ratio"
        );
    }

    /// The whole reason lambda is worth using. 0.85 is the same richness on petrol and on
    /// E85; 12.5:1 is comfortable on one and lean enough to hurt on the other.
    #[test]
    fn the_same_lambda_is_the_same_richness_on_every_fuel() {
        for fuel in [Fuel::Petrol, Fuel::E85, Fuel::Methanol, Fuel::Diesel] {
            let afr = afr_from_lambda(0.85, fuel);

            assert_equal(0.85, lambda_from_afr(afr, fuel), 6);
        }
    }

    #[test]
    fn twelve_and_a_half_to_one_is_safe_on_petrol_and_lean_on_e85() {
        in_range(lambda_from_afr(12.5, Fuel::Petrol), 0.84, 0.86);
        assert!(
            lambda_from_afr(12.5, Fuel::E85) > 1.25,
            "12.5:1 on E85 should be well lean of stoichiometric"
        );
    }

    // ----- pressure ------------------------------------------------------------

    /// The distinction that catches people out: an ECU reports MAP absolutely, so
    /// atmospheric is 101 kPa rather than zero.
    ///
    /// Ten psi of boost is 24.7 psi absolute, and 24.7 x 6.895 is 170.3, which is the check,
    /// since the same figure arrived at two ways is worth more than the formula restated.
    #[test]
    fn ten_psi_of_boost_is_one_hundred_and_seventy_kpa_absolute() {
        let gauge = 10.0 * KPA_PER_PSI;
        let abs = absolute(gauge);

        assert_equal(170.3, abs, 1);
        assert_equal(24.7 * KPA_PER_PSI, abs, 1);
        assert_equal(gauge, gauge_from_absolute(abs, SEA), 6);
    }

    /// The window and the comments both promise "170 kPa, not 69" to anyone reading them,
    /// and one of them said 169. A calculator whose own explanation is out by a kilopascal
    /// invites doubt about the rest of it.
    #[test]
    fn the_boost_figure_quoted_in_the_prose_is_the_one_the_code_produces() {
        assert_equal(170.0, absolute(10.0 * KPA_PER_PSI), 0);
    }

    /// A mile up, atmospheric is nearer 83 kPa. The same ten psi on the gauge is then
    /// 152 kPa absolute against sea level's 170, and a pressure ratio of 1.83 against
    /// 1.68, which is a different turbocharger.
    #[test]
    fn altitude_is_an_argument_because_it_is_not_a_constant() {
        const MILE: f64 = 83.0;

        let gauge = 10.0 * KPA_PER_PSI;
        let abs = absolute_from_gauge(gauge, MILE);

        assert_equal(152.0, abs, 0);
        in_range(pressure_ratio(abs, MILE), 1.8, 1.9);
        in_range(pressure_ratio(absolute(gauge), SEA), 1.6, 1.7);
    }

    #[test]
    fn atmospheric_is_zero_boost_and_a_pressure_ratio_of_one() {
        assert_equal(0.0, gauge_from_absolute(ATMOSPHERIC_KPA, SEA), 6);
        assert_equal(1.0, pressure_ratio(ATMOSPHERIC_KPA, SEA), 6);
    }

    #[test]
    fn one_bar_of_boost_doubles_the_air_packed() {
        let abs = absolute(ATMOSPHERIC_KPA);

        assert_equal(2.0, pressure_ratio(abs, SEA), 6);
    }

    // ----- altitude ------------------------------------------------------------

    /// The standard atmosphere is tabulated everywhere, so this is checked against the table
    /// rather than against the formula restated. Feet, because that is how altitude is
    /// signposted where turbochargers are argued about.
    #[test]
    fn barometric_pressure_matches_the_published_altitude_table() {
        let published = [
            (0.0, 101.325),
            (1_000.0, 97.72),
            (2_500.0, 92.50),
            (5_000.0, 84.31),
            (7_500.0, 76.71),
            (10_000.0, 69.68),
            (14_000.0, 59.53),
        ];

        for (feet, kpa) in published {
            let metres = feet * METRES_PER_FOOT;

            assert_equal(kpa, barometric_kpa(metres), 1);
        }
    }

    #[test]
    fn altitude_and_barometric_are_each_others_inverse() {
        for feet in [0.0, 1_000.0, 5_280.0, 10_000.0] {
            let metres = feet * METRES_PER_FOOT;

            assert_equal(metres, altitude_metres(barometric_kpa(metres)), 6);
        }
    }

    #[test]
    fn sea_level_is_the_default_and_is_the_standard_atmosphere() {
        assert_equal(ATMOSPHERIC_KPA, barometric_kpa(0.0), 6);
        assert_equal(0.0, altitude_metres(ATMOSPHERIC_KPA), 6);
    }

    /// Denver, and the figure the boost documentation promises.
    #[test]
    fn a_mile_up_is_the_eighty_three_kilopascals_quoted_in_the_prose() {
        assert_equal(83.4, barometric_kpa(5_280.0 * METRES_PER_FOOT), 1);
    }

    // ----- compressor ----------------------------------------------------------

    /// Garrett's own sizing example, which is the reason to trust this at all: 12 psi of
    /// boost at sea level, 2 psi lost through the intercooler and 1 psi through the filter,
    /// comes out at 2.09.
    ///
    /// 28.7 psia over 13.7 psia. Checked in psi as well as kPa, because arriving at the same
    /// answer through a different unit is worth more than the formula restated.
    #[test]
    fn the_pressure_ratio_matches_the_turbocharger_makers_worked_example() {
        let c = compressor_pressures(12.0 * KPA_PER_PSI, SEA, 1.0 * KPA_PER_PSI, 2.0 * KPA_PER_PSI);

        // 2.0952 against his 2.0949: the difference is that he rounds the atmosphere to a
        // flat 14.7 psia where this uses 101.325 kPa exactly, and the two agree to four
        // significant figures.
        in_range(c.ratio, 2.09, 2.10);
        assert_equal(13.7, c.inlet_kpa / KPA_PER_PSI, 1);
        assert_equal(28.7, c.outlet_kpa / KPA_PER_PSI, 1);

        // And the defaults are those same conventional losses, so the worked example is what
        // the tab shows before anything is typed into it.
        assert_equal(
            c.ratio,
            compressor_pressures(
                12.0 * KPA_PER_PSI,
                barometric_kpa(0.0),
                TYPICAL_INLET_LOSS_KPA,
                TYPICAL_CHARGE_LOSS_KPA,
            )
            .ratio,
            6,
        );
    }

    /// Both losses push the ratio up. A compressor picked without them is picked on a
    /// smaller number than it will actually be asked for, which is the direction that runs
    /// out of map.
    #[test]
    fn leaving_the_losses_out_flatters_the_turbocharger() {
        let bare = compressor(12.0 * KPA_PER_PSI).ratio;
        let honest =
            compressor_pressures(12.0 * KPA_PER_PSI, SEA, TYPICAL_INLET_LOSS_KPA, TYPICAL_CHARGE_LOSS_KPA).ratio;

        assert_equal(1.82, bare, 2);
        assert!(
            honest > bare,
            "losses should raise the ratio, but {honest:.2} <= {bare:.2}"
        );
        in_range(honest, 2.05, 2.15);

        // Fifteen per cent, which is the difference between a compressor sitting in the
        // middle of its map and one against the edge of it.
        in_range((honest / bare) - 1.0, 0.13, 0.17);
    }

    /// A gauge reads against whatever the engine is breathing, so the same number on the
    /// dial is a bigger job in the mountains. This is the whole reason altitude is an input.
    #[test]
    fn the_same_boost_asks_more_of_a_compressor_higher_up() {
        let sea = compressor_pressures(
            12.0 * KPA_PER_PSI,
            barometric_kpa(0.0),
            TYPICAL_INLET_LOSS_KPA,
            TYPICAL_CHARGE_LOSS_KPA,
        )
        .ratio;

        let mountain = compressor_pressures(
            12.0 * KPA_PER_PSI,
            barometric_kpa(5_000.0 * METRES_PER_FOOT),
            TYPICAL_INLET_LOSS_KPA,
            TYPICAL_CHARGE_LOSS_KPA,
        )
        .ratio;

        in_range(sea, 2.05, 2.15);
        in_range(mountain, 2.3, 2.45);
        assert!(
            mountain > sea * 1.1,
            "five thousand feet should be well over a tenth more"
        );
    }

    /// An inlet loss larger than the air available is nonsense, not a negative pressure
    /// ratio.
    #[test]
    fn a_compressor_cannot_breathe_what_is_not_there() {
        assert!(compressor_pressures(100.0, ATMOSPHERIC_KPA, 200.0, 0.0).ratio.is_nan());
    }

    /// Both are called the pressure ratio and they are not the same number. The
    /// compressor's is against the air it breathes and rises as you climb; the charge
    /// density is against sea level and falls, because the same boost on the gauge is less
    /// absolute pressure in the manifold.
    ///
    /// Conflating them (dividing the charge density by local barometric) cancels the
    /// altitude out and overstates the air by a fifth.
    #[test]
    fn the_two_pressure_ratios_move_in_opposite_directions_with_altitude() {
        let boost = 10.0 * KPA_PER_PSI;
        let high = barometric_kpa(5_000.0 * METRES_PER_FOOT);

        let compressor_sea = compressor(boost).ratio;
        let compressor_high = compressor_pressures(boost, high, 0.0, 0.0).ratio;

        let charge_sea = charge_density_ratio(absolute(boost));
        let charge_high = charge_density_ratio(absolute_from_gauge(boost, high));

        assert!(
            compressor_high > compressor_sea,
            "the compressor is asked for more up high"
        );
        assert!(charge_high < charge_sea, "and yet there is less air in the manifold");

        // The size of the mistake, if the charge density were taken against the local air
        // like the compressor's is.
        let wrong = pressure_ratio(absolute_from_gauge(boost, high), high);

        in_range(wrong / charge_high, 1.15, 1.25);
    }

    // ----- airflow -------------------------------------------------------------

    /// 122 cubic inches at 7,000 rpm and full volumetric efficiency is a touch under
    /// 250 cfm: the figure a carburettor would be picked on.
    #[test]
    fn a_two_litre_at_seven_thousand_demands_about_two_hundred_and_fifty_cfm() {
        in_range(cfm(2.0, 7000.0, 100.0), 240.0, 250.0);
    }

    #[test]
    fn boost_scales_the_demand_by_the_pressure_ratio() {
        let natural = cfm(2.0, 7000.0, 100.0);
        let boosted = cubic_feet_per_minute(2.0, 7000.0, 100.0, 2.0);

        assert_equal(natural * 2.0, boosted, 6);
    }

    /// 250 cfm is a little over 19 lb/min, which is where a small turbocharger's map is
    /// read.
    #[test]
    fn airflow_in_the_pounds_per_minute_a_compressor_map_uses() {
        let flow = cfm(2.0, 7000.0, 100.0);

        in_range(air_pounds_per_minute(flow), 18.0, 20.0);
    }

    /// The consequence a tuner would recognise, and the reason the altitude has to reach the
    /// airflow tab rather than only the compressor one: the gauge still says ten psi in the
    /// mountains and the engine still makes less power, because absolute pressure is what it
    /// breathes.
    #[test]
    fn the_same_boost_is_less_air_higher_up() {
        fn air_at(feet: f64) -> f64 {
            let baro = barometric_kpa(feet * METRES_PER_FOOT);
            let map = absolute_from_gauge(10.0 * KPA_PER_PSI, baro);

            air_pounds_per_minute(cubic_feet_per_minute(2.0, 7000.0, 95.0, charge_density_ratio(map)))
        }

        let sea = air_at(0.0);
        let mountain = air_at(5_000.0);

        in_range(sea, 29.0, 31.0);
        in_range(mountain, 26.0, 28.0);
        in_range(1.0 - (mountain / sea), 0.08, 0.12);
    }

    #[test]
    fn nothing_is_demanded_by_an_engine_that_is_not_turning() {
        assert_eq!(cfm(2.0, 0.0, 100.0), 0.0);
        assert_eq!(cfm(0.0, 7000.0, 100.0), 0.0);
    }

    // ----- power from air ------------------------------------------------------

    /// The rule of thumb everyone quotes, recovered from the arithmetic rather than written
    /// in as a constant, which also pins what it quietly assumes, since it is only true at
    /// these figures.
    #[test]
    fn the_familiar_ten_horsepower_per_pound_of_air_comes_out() {
        let hp = horsepower_from_air(1.0, Fuel::Petrol, 0.85, 0.48);

        assert_equal(10.0, hp, 1);

        // And the turbocharger maker's 9.5, which assumes a richer mixture and a worse BSFC.
        // Both are "the" figure, depending on who is quoting it.
        assert_equal(9.5, horsepower_from_air(1.0, Fuel::Petrol, 0.78, 0.55), 1);
    }

    /// A figure a tuner would recognise: a 2.0 at 30 lb/min is a 300 hp car.
    #[test]
    fn thirty_pounds_of_air_a_minute_is_about_three_hundred_horsepower() {
        let hp = horsepower_from_air(30.0, Fuel::Petrol, 0.85, FULL_THROTTLE_BSFC);

        in_range(hp, 280.0, 300.0);
    }

    /// The answer most of a workshop would argue with, and the reason to compute it rather
    /// than assert it: a pound of air carries about as much energy whichever fuel arrives
    /// with it, so at one lambda and one efficiency the alcohols are worth a few per cent
    /// and no more.
    #[test]
    fn the_same_air_is_nearly_the_same_power_on_every_fuel() {
        let petrol = horsepower_from_air(30.0, Fuel::Petrol, 0.85, 0.50);
        let ethanol = horsepower_from_air(30.0, Fuel::Ethanol, 0.85, 0.50);
        let methanol = horsepower_from_air(30.0, Fuel::Methanol, 0.85, 0.50);

        in_range((ethanol / petrol) - 1.0, 0.00, 0.02);
        in_range((methanol / petrol) - 1.0, 0.03, 0.06);

        // Ordered, and all three within five per cent of each other.
        assert!(methanol > ethanol && ethanol > petrol);
        in_range(methanol / petrol, 1.0, 1.05);
    }

    /// The two are the same statement: power per unit air tracks the energy each fuel brings
    /// with a unit of air at stoichiometric. Checked against that independently, so a
    /// mistake in one would not agree with the other.
    #[test]
    fn the_power_comparison_is_what_the_energy_per_pound_of_air_says() {
        for fuel in [Fuel::Petrol, Fuel::E85, Fuel::Ethanol, Fuel::Methanol] {
            let per_air = energy_mj_per_kg(fuel) / stoichiometric(fuel);
            let petrol_per_air = energy_mj_per_kg(Fuel::Petrol) / stoichiometric(Fuel::Petrol);

            let hp = horsepower_from_air(30.0, fuel, 0.85, 0.50);
            let petrol_hp = horsepower_from_air(30.0, Fuel::Petrol, 0.85, 0.50);

            assert_equal(per_air / petrol_per_air, hp / petrol_hp, 6);
        }
    }

    #[test]
    fn richer_and_more_efficient_both_make_more_power_from_the_same_air() {
        let baseline = horsepower_from_air(30.0, Fuel::Petrol, 0.85, 0.50);

        assert!(horsepower_from_air(30.0, Fuel::Petrol, 0.80, 0.50) > baseline);
        assert!(horsepower_from_air(30.0, Fuel::Petrol, 0.85, 0.45) > baseline);
    }

    #[test]
    fn power_from_no_air_or_no_mixture_is_nothing() {
        assert_eq!(horsepower_from_air(0.0, Fuel::Petrol, 0.85, 0.50), 0.0);
        assert_eq!(horsepower_from_air(30.0, Fuel::Petrol, 0.0, 0.50), 0.0);
        assert_eq!(horsepower_from_air(30.0, Fuel::Petrol, -1.0, 0.50), 0.0);
        assert_eq!(horsepower_from_air(30.0, Fuel::Petrol, 0.85, 0.0), 0.0);
    }

    /// Deliberately different defaults. Sizing is pessimistic on purpose, because oversizing
    /// a pump costs money and undersizing one costs an engine; estimating power the same way
    /// would understate the engine.
    #[test]
    fn estimating_power_and_sizing_a_pump_use_the_same_number_differently() {
        const { assert!(FULL_THROTTLE_BSFC < BOOSTED_BSFC) };
        in_range(FULL_THROTTLE_BSFC, 0.45, 0.55);
    }

    // ----- carrying a BSFC across a change of fuel ------------------------------

    #[test]
    fn moving_a_figure_between_fuels_and_back_leaves_it_where_it_started() {
        for fuel in Fuel::ALL {
            if fuel == Fuel::Diesel {
                continue;
            }

            let moved = suggested_bsfc(fuel, 0.52);

            assert_equal(0.52, petrol_equivalent_bsfc(fuel, moved), 6);
        }
    }

    /// Somebody who typed 0.48 means "naturally aspirated", and switching to E85 should give
    /// them E85's aspirated figure rather than its boosted one. Replacing the box instead of
    /// scaling it loses that.
    #[test]
    fn changing_fuel_keeps_an_aspirated_choice_aspirated() {
        let petrol_equivalent = petrol_equivalent_bsfc(Fuel::Petrol, 0.48);
        let on_e85 = suggested_bsfc(Fuel::E85, petrol_equivalent);

        assert_equal(suggested_bsfc(Fuel::E85, NATURALLY_ASPIRATED_BSFC), on_e85, 6);
        in_range(on_e85, 0.68, 0.74);
        assert!(on_e85 < suggested_bsfc(Fuel::E85, BOOSTED_BSFC));
    }

    #[test]
    fn diesel_has_no_petrol_equivalent_to_carry_across() {
        assert!(petrol_equivalent_bsfc(Fuel::Diesel, 0.36).is_nan());
        assert!(petrol_equivalent_bsfc(Fuel::Petrol, 0.0).is_nan());
    }

    /// A printed table that drifts from the calculator beside it is worse than no table, so
    /// it is generated from the same function.
    #[test]
    fn the_legend_agrees_with_the_arithmetic_it_sits_next_to() {
        let legend = bsfc_legend(Fuel::E85);

        for fuel in Fuel::ALL {
            assert!(legend.contains(short_name(fuel)));

            if fuel == Fuel::Diesel {
                continue;
            }

            assert!(legend.contains(&n2(suggested_bsfc(fuel, BOOSTED_BSFC))));
        }

        assert!(legend.contains(&n2(DIESEL_BSFC)));

        // And it marks the fuel that is actually selected.
        assert!(legend.contains("\u{25B8} E85"));
        assert!(!legend.contains("\u{25B8} Petrol"));
    }

    // ----- injectors -----------------------------------------------------------

    /// A familiar sizing: 500 hp, four cylinders, boosted, 85 per cent duty.
    #[test]
    fn five_hundred_horsepower_on_four_injectors_wants_about_seven_hundred_and_fifty_cc() {
        let lb_hr = injector_pounds_per_hour(500.0, 4, BOOSTED_BSFC, 85.0);
        let cc = cc_per_minute(lb_hr, Fuel::Petrol);

        in_range(lb_hr, 85.0, 92.0);
        in_range(cc, 850.0, 940.0);
    }

    #[test]
    fn sizing_and_its_inverse_agree() {
        let lb_hr = injector_pounds_per_hour(400.0, 6, 0.55, 80.0);
        let power = supported_horsepower(lb_hr, 6, 0.55, 80.0);

        assert_equal(400.0, power, 6);
    }

    #[test]
    fn converting_between_pounds_and_cc_round_trips() {
        for fuel in [Fuel::Petrol, Fuel::E85, Fuel::Methanol] {
            let cc = cc_per_minute(60.0, fuel);

            assert_equal(60.0, pounds_per_hour_from_cc(cc, fuel), 6);
        }
    }

    #[test]
    fn the_usual_conversion_constant_is_not_used_on_fuel_it_does_not_fit() {
        let petrol = cc_per_minute(60.0, Fuel::Petrol) / 60.0;
        let e85 = cc_per_minute(60.0, Fuel::E85) / 60.0;

        in_range(petrol, 10.0, 10.3);
        assert!(e85 < petrol, "denser fuel is fewer cc for the same mass");
    }

    /// Pinned because this was documented backwards. 10.5 cc/min per lb/hr is a density of
    /// 0.72; every fuel here is denser, so the constant always asks for more cc than the mass
    /// needs. It oversizes an injector on ethanol; it does not undersize one.
    ///
    /// What undersizes an injector on ethanol is the BSFC, which is a much larger error in
    /// the genuinely dangerous direction.
    #[test]
    fn the_conventional_constant_errs_large_on_ethanol_rather_than_small() {
        for fuel in [Fuel::Petrol, Fuel::E85, Fuel::Ethanol, Fuel::Methanol] {
            let honest = cc_per_minute(60.0, fuel) / 60.0;

            assert!(
                10.5 > honest,
                "10.5 should be the larger figure on {fuel:?}, but the honest one was {honest:.2}"
            );
        }

        // And the size of it, which was written down as five per cent.
        let e85 = cc_per_minute(60.0, Fuel::E85) / 60.0;

        in_range((10.5 / e85) - 1.0, 0.07, 0.11);
    }

    /// A 550 cc injector rated at three bar flows about 635 at four.
    #[test]
    fn flow_follows_the_square_root_of_pressure() {
        let higher = flow_at_pressure(550.0, 300.0, 400.0);

        in_range(higher, 630.0, 640.0);

        // And unchanged at its rated pressure, which is the case worth being certain of.
        assert_equal(550.0, flow_at_pressure(550.0, 300.0, 300.0), 6);
    }

    /// Duty is an input rather than a constant, and asking for more flow at a lower duty is
    /// the whole point of it.
    #[test]
    fn an_injector_held_fully_open_is_not_assumed() {
        let at85 = injector_pounds_per_hour(500.0, 4, 0.6, 85.0);
        let at100 = injector_pounds_per_hour(500.0, 4, 0.6, 100.0);

        assert!(at85 > at100);
    }

    // ----- fuel pump -----------------------------------------------------------

    #[test]
    fn five_hundred_horsepower_drinks_about_two_hundred_litres_an_hour() {
        let litres = fuel_litres_per_hour(500.0, BOOSTED_BSFC, Fuel::Petrol);

        in_range(litres, 180.0, 190.0);
    }

    #[test]
    fn the_pump_is_chosen_with_headroom_over_what_is_burned() {
        let burned = fuel_litres_per_hour(500.0, 0.6, Fuel::Petrol);
        let pump = pump_litres_per_hour(500.0, 0.6, Fuel::Petrol, 20.0);

        assert_equal(burned * 1.2, pump, 6);
        assert!(
            pump > burned,
            "a pump at its limit whenever the engine is, is a pump that dies"
        );
    }

    /// Which is why a pump sized for petrol is not a pump sized for E85.
    #[test]
    fn ethanol_needs_more_fuel_by_volume_for_the_same_power() {
        let petrol = fuel_litres_per_hour(500.0, 0.6, Fuel::Petrol);
        let e85 = fuel_litres_per_hour(500.0, 0.85, Fuel::E85);

        assert!(
            e85 > petrol * 1.2,
            "E85 wanted {e85:.0} L/h against petrol's {petrol:.0}"
        );
    }

    // ----- pumps as they are sold ----------------------------------------------

    /// The catalogue is the part of this file that goes stale on its own, so what can be
    /// checked mechanically is: nothing blank, nothing zero, and a rating pressure on every
    /// entry, the figure that makes the flow figure mean anything.
    #[test]
    fn every_pump_in_the_catalogue_is_described_completely() {
        assert!(!PUMPS.is_empty());

        for pump in &PUMPS {
            assert!(!pump.maker.trim().is_empty());
            assert!(!pump.part.trim().is_empty());
            in_range(pump.litres_per_hour, 100.0, 1_000.0);
            in_range(pump.rated_psi, 30.0, 60.0);
        }

        // Both makers the tab names, and part numbers that are not duplicated.
        assert!(PUMPS.iter().any(|p| p.maker == "Walbro"));
        assert!(PUMPS.iter().any(|p| p.maker == "AEM"));
        let mut names: Vec<String> = PUMPS.iter().map(FuelPump::name).collect();
        names.sort();
        names.dedup();
        assert_eq!(PUMPS.len(), names.len());
    }

    /// The mistake the suggestion exists to stop: the number on the box is measured at about
    /// 40 psi, and a boosted rail is nowhere near that.
    #[test]
    fn a_pump_makes_less_than_its_rating_at_any_pressure_above_it() {
        let walbro255 = PUMPS.iter().find(|p| p.part == "GSS342").unwrap();

        assert_equal(255.0, pump_flow_at_pressure(walbro255, walbro255.rated_psi), 6);

        // Published curves put the 255 near 190 L/h at 70 psi and near 150 at 87: checked
        // against those rather than against the model restated.
        in_range(pump_flow_at_pressure(walbro255, 70.0), 180.0, 205.0);
        in_range(pump_flow_at_pressure(walbro255, 87.0), 140.0, 165.0);

        // And more than its rating below it, which is the other direction.
        assert!(pump_flow_at_pressure(walbro255, 30.0) > 255.0);
    }

    /// They share a line, a filter and a regulator. Counting two as exactly twice one is the
    /// optimistic direction, which is the wrong one.
    #[test]
    fn pumps_in_parallel_are_worth_less_than_the_sum_of_them() {
        assert_equal(400.0, parallel_flow(400.0, 1), 6);
        assert!(parallel_flow(400.0, 2) < 800.0);
        in_range(parallel_flow(400.0, 2), 740.0, 780.0);
        in_range(parallel_flow(400.0, 3), 1_100.0, 1_180.0);
    }

    /// 400 hp on petrol at 43.5 psi is a single 255's job, and the answer should say so
    /// rather than reaching for something exotic.
    #[test]
    fn a_modest_petrol_car_is_offered_one_ordinary_pump() {
        let needed = pump_litres(400.0, BOOSTED_BSFC);

        let picks = suggest_pumps(needed, 43.5, false);

        assert!(!picks.is_empty());
        assert_eq!(1, picks[0].count);
        assert!(picks[0].delivered_litres_per_hour >= needed);
    }

    #[test]
    fn no_pump_is_offered_that_cannot_deliver_what_was_asked_of_it() {
        for hp in [300.0, 500.0, 700.0, 900.0] {
            for rail in [43.5, 60.0, 75.0] {
                let needed = pump_litres(hp, BOOSTED_BSFC);

                for pick in suggest_pumps(needed, rail, false) {
                    assert!(
                        pick.delivered_litres_per_hour >= needed,
                        "{} x {} was offered for {needed:.0} L/h at {rail} psi but delivers {:.0}",
                        pick.pump.name(),
                        pick.count,
                        pick.delivered_litres_per_hour
                    );

                    assert!((1..=MOST_PUMPS_WORTH_WIRING).contains(&pick.count));
                }
            }
        }
    }

    /// A pump not rated for ethanol will pass fuel for a while and then fail, which is the
    /// worst way to find out. E85 is not a filter to get wrong.
    #[test]
    fn alcohol_only_ever_gets_pumps_rated_for_it() {
        let needed = pump_litres_per_hour(500.0, 0.89, Fuel::E85, 20.0);

        let picks = suggest_pumps(needed, 50.0, true);

        assert!(!picks.is_empty());
        for p in &picks {
            assert!(p.pump.alcohol_safe, "{} is not rated for it", p.pump.name());
        }

        assert!(needs_alcohol_safe_pump(Fuel::E85));
        assert!(needs_alcohol_safe_pump(Fuel::Methanol));
        assert!(!needs_alcohol_safe_pump(Fuel::Petrol));
    }

    #[test]
    fn fewest_pumps_comes_first_and_the_least_wasteful_after_that() {
        let needed = pump_litres_per_hour(600.0, BOOSTED_BSFC, Fuel::E85, 20.0);

        let picks = suggest_pumps(needed, 55.0, true);

        assert!(!picks.is_empty());

        for i in 1..picks.len() {
            assert!(picks[i].count >= picks[i - 1].count, "counts should not go back down");

            if picks[i].count == picks[i - 1].count {
                assert!(picks[i].delivered_litres_per_hour >= picks[i - 1].delivered_litres_per_hour);
            }
        }
    }

    /// A thousand horsepower on methanol at a high rail is past what three in-tank pumps do,
    /// and the honest answer is a different kind of pump rather than a fourth of the same
    /// one.
    #[test]
    fn something_ridiculous_is_told_to_stop_buying_in_tank_pumps() {
        let needed = pump_litres_per_hour(
            1_500.0,
            suggested_bsfc(Fuel::Methanol, BOOSTED_BSFC),
            Fuel::Methanol,
            20.0,
        );

        assert!(suggest_pumps(needed, 75.0, true).is_empty());
    }

    #[test]
    fn nothing_is_suggested_without_a_pressure_to_suggest_it_at() {
        assert!(suggest_pumps(300.0, 0.0, false).is_empty());
        assert!(suggest_pumps(0.0, 43.5, false).is_empty());
        assert!(suggest_pumps(f64::NAN, 43.5, false).is_empty());
    }

    /// Twenty psi of boost sits on top of the base pressure, and the pump feels all of it.
    /// The same engine can need two pumps at 63 psi where it needed one at 43.
    #[test]
    fn raising_the_rail_costs_pumps_and_can_cost_a_whole_extra_one() {
        let needed = pump_litres(600.0, BOOSTED_BSFC);

        let at_base = suggest_pumps(needed, 43.5, false);
        let at_boost = suggest_pumps(needed, 63.5, false);

        assert!(!at_base.is_empty());
        assert!(!at_boost.is_empty());
        assert!(at_boost[0].count >= at_base[0].count);
    }

    #[test]
    fn nonsense_inputs_produce_nothing_rather_than_infinity() {
        assert_eq!(injector_pounds_per_hour(500.0, 0, 0.6, 85.0), 0.0);
        assert_eq!(injector_pounds_per_hour(500.0, 4, 0.6, 0.0), 0.0);
        assert_eq!(fuel_litres_per_hour(0.0, 0.6, Fuel::Petrol), 0.0);
        assert_eq!(flow_at_pressure(550.0, 0.0, 400.0), 0.0);
    }

    /// A minus sign typed into the lambda box used to come back as an air-fuel ratio and a
    /// verdict of "safe".
    #[test]
    fn nothing_at_or_below_zero_is_a_mixture() {
        assert!(afr_from_lambda(-1.0, Fuel::Petrol).is_nan());
        assert!(afr_from_lambda(0.0, Fuel::Petrol).is_nan());
        assert!(lambda_from_afr(-14.7, Fuel::Petrol).is_nan());
        assert!(lambda_from_afr(0.0, Fuel::Petrol).is_nan());
    }

    // ----- the unit conversions nothing else covers ----------------------------

    /// 250 cfm, spelled three ways. Checked against the conversions rather than against the
    /// code, since a mistyped constant here reads perfectly.
    #[test]
    fn the_airflow_units_are_three_views_of_one_number() {
        let flow = 250.0;

        // A cubic foot is 28.3168 litres, so 250 a minute is 424.75 m3/h.
        assert_equal(424.75, cubic_metres_per_hour(flow), 1);

        // Standard air is 0.0765 lb/ft3, so 250 cfm is 19.1 lb/min.
        assert_equal(19.12, air_pounds_per_minute(flow), 1);
    }

    #[test]
    fn a_pound_of_fuel_an_hour_is_the_same_quantity_whichever_way_it_is_written() {
        // 60 lb/hr on petrol: 27.2 kg/hr, 36.5 litres an hour, 609 cc a minute.
        assert_equal(609.0, cc_per_minute(60.0, Fuel::Petrol), 0);

        // The pump calculation should agree with it: 60 lb/hr is what 100 hp at a BSFC of
        // 0.60 burns, and 609 cc/min is 36.5 litres an hour.
        assert_equal(36.5, fuel_litres_per_hour(100.0, 0.60, Fuel::Petrol), 1);
    }

    // ----- temperature ---------------------------------------------------------

    /// The fixed points, and the one place the two scales cross.
    #[test]
    fn fahrenheit_and_celsius_are_the_same_temperature() {
        for (f, c) in [
            (32.0, 0.0),
            (212.0, 100.0),
            (-40.0, -40.0),
            (68.0, 20.0),
            (130.0, 54.44444),
        ] {
            assert_equal(c, celsius_from_fahrenheit(f), 3);
            assert_equal(f, fahrenheit_from_celsius(c), 3);
        }
    }

    /// The mistake this exists to prevent. Scaling first and then adding gives 88 for a
    /// 40 degree day rather than 104: a number that looks like a temperature and is sixteen
    /// degrees out.
    #[test]
    fn the_offset_is_applied_before_the_factor_and_not_after() {
        assert_equal(104.0, fahrenheit_from_celsius(40.0), 6);
        assert_ne!((88.0_f64 * 1e6).round(), (fahrenheit_from_celsius(40.0) * 1e6).round());

        for c in [-40.0, 0.0, 25.0, 55.0, 100.0] {
            assert_equal(c, celsius_from_fahrenheit(fahrenheit_from_celsius(c)), 9);
        }
    }

    // ----- gallons -------------------------------------------------------------

    /// 3.785411784 litres exactly, so the reciprocal is taken from that rather than typed as
    /// a rounded 0.2642: these figures end up compared against a pump's printed rating.
    #[test]
    fn a_gallon_is_the_gallons_own_definition() {
        assert_equal(1.0 / 3.785411784, US_GALLONS_PER_LITRE, 12);
        assert_equal(3.785411784, 1.0 / US_GALLONS_PER_LITRE, 9);
    }

    /// A pump rated 255 L/h is a shade over a gallon a minute, which is the figure the larger
    /// and mechanical pumps are quoted in.
    #[test]
    fn litres_an_hour_become_gallons_a_minute() {
        assert_equal(1.123, gallons_per_minute(255.0), 3);

        // And the two gallon figures agree with each other.
        for litres_per_hour in [100.0, 183.0, 255.0, 450.0] {
            assert_equal(
                litres_per_hour * US_GALLONS_PER_LITRE / 60.0,
                gallons_per_minute(litres_per_hour),
                9,
            );
        }
    }

    /// 500 hp at a BSFC of 0.5 burns 152 L/h of petrol; with a fifth in hand that is
    /// 183 L/h, 48.3 US gallons an hour, and 0.80 a minute.
    #[test]
    fn the_pump_figures_agree_across_every_unit_they_are_quoted_in() {
        let pump = pump_litres(500.0, 0.50);

        assert_equal(183.0, pump, 0);
        assert_equal(48.3, pump * US_GALLONS_PER_LITRE, 1);
        assert_equal(0.80, gallons_per_minute(pump), 2);
    }
}
