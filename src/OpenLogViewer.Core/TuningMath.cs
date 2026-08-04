namespace OpenLogViewer.Core;

/// <summary>
/// A fuel, and the air-fuel ratio at which it burns completely.
///
/// Stoichiometry is what makes lambda useful: it is the same number on every
/// fuel, where an air-fuel ratio is not. Lambda 0.85 is the same richness
/// whether the tank holds petrol or ethanol; 12.5:1 is comfortably rich on
/// petrol and dangerously lean on E85.
/// </summary>
public enum Fuel
{
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

/// <summary>
/// The arithmetic behind the tuning calculators.
///
/// Kept apart from the window that shows it because these are the part that can
/// be wrong in a way nobody sees. A calculator that reads plausibly and is out
/// by ten per cent sizes an injector ten per cent small, and the engine finds
/// out before the tuner does.
///
/// Every figure here is either a definition or a published constant, and the
/// ones that are conventions rather than facts — brake specific fuel
/// consumption above all — are named as such where they are used.
/// </summary>
public static class TuningMath
{
    /// <summary>
    /// Air-fuel ratio at which each fuel burns completely.
    ///
    /// The blends are computed from their constituents rather than quoted, so
    /// E30 and E50 are consistent with E85 and with petrol rather than being
    /// three separate approximations.
    /// </summary>
    public static double Stoichiometric(Fuel fuel) => fuel switch
    {
        Fuel.Petrol => 14.7,
        Fuel.E10 => Blend(0.10),
        Fuel.E30 => Blend(0.30),
        Fuel.E50 => Blend(0.50),
        Fuel.E85 => Blend(0.85),
        Fuel.Ethanol => 9.0,
        Fuel.Methanol => 6.45,
        Fuel.Diesel => 14.5,
        Fuel.Lpg => 15.67,
        Fuel.Cng => 17.2,
        _ => 14.7,
    };

    private const double PetrolDensity = 0.745;

    private const double EthanolDensity = 0.789;

    /// <summary>
    /// The fraction of a blend's mass that is ethanol.
    ///
    /// The two fuels are mixed by volume and burn by mass, so everything that
    /// follows from a blend — its stoichiometry, its energy — is weighted by
    /// this rather than by the volume fraction on the pump.
    ///
    /// The correction is small: E85 comes out at 9.81 where a straight volume
    /// average of 14.7 and 9.0 would say 9.86, and no blend differs by as much
    /// as one per cent. It is done this way because it is the right derivation
    /// and it keeps the blends consistent with each other and with their
    /// constituents, not because the difference would change a tune.
    /// </summary>
    private static double EthanolMassFraction(double ethanolByVolume)
    {
        double ethanol = ethanolByVolume * EthanolDensity;
        double petrol = (1 - ethanolByVolume) * PetrolDensity;

        return ethanol / (ethanol + petrol);
    }

    /// <summary>An ethanol blend's stoichiometric ratio, weighted by mass.</summary>
    private static double Blend(double ethanolByVolume)
    {
        double ethanol = EthanolMassFraction(ethanolByVolume);

        // Air needed per unit of mixture mass, which is the ratio itself.
        return (ethanol * 9.0) + ((1 - ethanol) * 14.7);
    }

    /// <summary>
    /// Density in kilograms per litre, for turning a mass flow into a volume.
    ///
    /// Petrol is the awkward one: it is not a compound but a specification, and
    /// real pump fuel runs from about 0.72 to 0.775 depending on the blend, the
    /// season and the temperature. Everything downstream that converts a mass to
    /// a volume — injector cc, pump litres — inherits that spread, so those
    /// figures are worth about three per cent either way whatever precision they
    /// are printed to.
    /// </summary>
    public static double Density(Fuel fuel) => fuel switch
    {
        Fuel.Petrol => PetrolDensity,
        Fuel.E10 => BlendDensity(0.10),
        Fuel.E30 => BlendDensity(0.30),
        Fuel.E50 => BlendDensity(0.50),
        Fuel.E85 => BlendDensity(0.85),
        Fuel.Ethanol => EthanolDensity,
        Fuel.Methanol => 0.792,
        Fuel.Diesel => 0.832,
        Fuel.Lpg => 0.510,

        // Compressed to about 250 bar, where it is still a gas. A litre of it at
        // the rail is not the quantity a fuel system is specified in, which is
        // why the pump and injector figures do not mean much on this one.
        Fuel.Cng => 0.180,

        _ => PetrolDensity,
    };

    /// <summary>A blend's density, which unlike its stoichiometry is a volume average.</summary>
    private static double BlendDensity(double ethanolByVolume) =>
        (ethanolByVolume * EthanolDensity) + ((1 - ethanolByVolume) * PetrolDensity);

    /// <summary>
    /// Lower heating value, in megajoules per kilogram: the energy the fuel
    /// actually releases, with the water it makes left as vapour.
    ///
    /// This is what decides how much of a fuel an engine drinks. A given power
    /// is a given amount of energy per hour, so a fuel carrying less of it per
    /// kilogram is burned in proportionally greater mass — which is the whole of
    /// why E85 needs bigger injectors and a bigger pump.
    /// </summary>
    public static double EnergyMjPerKg(Fuel fuel) => fuel switch
    {
        Fuel.Petrol => PetrolEnergy,
        Fuel.E10 => BlendEnergy(0.10),
        Fuel.E30 => BlendEnergy(0.30),
        Fuel.E50 => BlendEnergy(0.50),
        Fuel.E85 => BlendEnergy(0.85),
        Fuel.Ethanol => EthanolEnergy,
        Fuel.Methanol => 19.9,
        Fuel.Diesel => 42.6,
        Fuel.Lpg => 46.4,
        Fuel.Cng => 50.0,
        _ => PetrolEnergy,
    };

    private const double PetrolEnergy = 43.4;

    private const double EthanolEnergy = 26.8;

    private static double BlendEnergy(double ethanolByVolume)
    {
        double ethanol = EthanolMassFraction(ethanolByVolume);

        return (ethanol * EthanolEnergy) + ((1 - ethanol) * PetrolEnergy);
    }

    /// <summary>The name that fits in a column rather than in a sentence.</summary>
    public static string ShortName(Fuel fuel) => fuel switch
    {
        Fuel.Petrol => "Petrol",
        Fuel.E10 => "E10",
        Fuel.E30 => "E30",
        Fuel.E50 => "E50",
        Fuel.E85 => "E85",
        Fuel.Ethanol => "Ethanol",
        Fuel.Methanol => "Methanol",
        Fuel.Diesel => "Diesel",
        Fuel.Lpg => "LPG",
        Fuel.Cng => "CNG",
        _ => fuel.ToString(),
    };

    public static string Name(Fuel fuel) => fuel switch
    {
        Fuel.Petrol => "Petrol / gasoline",
        Fuel.E10 => "E10",
        Fuel.E30 => "E30",
        Fuel.E50 => "E50",
        Fuel.E85 => "E85",
        Fuel.Ethanol => "Ethanol (E100)",
        Fuel.Methanol => "Methanol",
        Fuel.Diesel => "Diesel",
        Fuel.Lpg => "LPG / propane",
        Fuel.Cng => "CNG / methane",
        _ => fuel.ToString(),
    };

    // ----- mixture -------------------------------------------------------------

    /// <summary>
    /// The air-fuel ratio a lambda corresponds to on a given fuel.
    ///
    /// Nothing at or below zero is a mixture, and answering one with a negative
    /// ratio would put a number on the screen that reads as a verdict.
    /// </summary>
    public static double AfrFromLambda(double lambda, Fuel fuel) =>
        lambda > 0 ? lambda * Stoichiometric(fuel) : double.NaN;

    /// <summary>The lambda an air-fuel ratio corresponds to on a given fuel.</summary>
    public static double LambdaFromAfr(double afr, Fuel fuel)
    {
        double stoich = Stoichiometric(fuel);

        return afr > 0 && stoich > 0 ? afr / stoich : double.NaN;
    }

    // ----- pressure ------------------------------------------------------------

    /// <summary>Standard atmosphere, and the difference between gauge and absolute.</summary>
    public const double AtmosphericKpa = 101.325;

    public const double KpaPerPsi = 6.894757293168361;

    public const double KpaPerBar = 100;

    /// <summary>
    /// Boost as a gauge reading turned into absolute manifold pressure.
    ///
    /// The distinction that catches people out: an ECU reports MAP absolutely,
    /// so atmospheric is about 101 kPa and not zero, while a boost gauge reads
    /// zero at atmospheric. Ten psi of boost is 170 kPa absolute, not 69.
    ///
    /// Atmospheric is an argument rather than a constant because it is not one:
    /// the default here is sea level, and a car a mile up is breathing nearer
    /// 83 kPa, where the same ten psi on the gauge is 152 kPa absolute and a
    /// noticeably different pressure ratio.
    /// </summary>
    public static double AbsoluteFromGauge(double gaugeKpa, double atmosphericKpa = AtmosphericKpa) =>
        gaugeKpa + atmosphericKpa;

    public static double GaugeFromAbsolute(double absoluteKpa, double atmosphericKpa = AtmosphericKpa) =>
        absoluteKpa - atmosphericKpa;

    /// <summary>
    /// How much more air is packed in than atmospheric, which is what an engine's
    /// airflow actually scales with.
    /// </summary>
    public static double PressureRatio(double absoluteKpa, double atmosphericKpa = AtmosphericKpa) =>
        atmosphericKpa > 0 ? absoluteKpa / atmosphericKpa : double.NaN;

    // ----- temperature ---------------------------------------------------------

    /// <summary>
    /// Degrees Celsius from Fahrenheit.
    ///
    /// Here rather than written out wherever it is wanted because it is the one
    /// conversion with an offset as well as a factor, and an offset is the thing
    /// that gets applied in the wrong order. Multiplying first gives a number
    /// that looks like a temperature and is out by eighteen degrees.
    /// </summary>
    public static double CelsiusFromFahrenheit(double fahrenheit) =>
        (fahrenheit - 32) * 5 / 9;

    public static double FahrenheitFromCelsius(double celsius) =>
        (celsius * 9 / 5) + 32;

    // ----- altitude ------------------------------------------------------------

    public const double MetresPerFoot = 0.3048;

    /// <summary>
    /// Barometric pressure at an altitude, on the ICAO standard atmosphere.
    ///
    /// The troposphere model, which holds to eleven kilometres and so covers
    /// every road on earth with a wide margin. Sea level is 101.325 kPa, five
    /// thousand feet is 84.3, and ten thousand is 69.7 — figures anyone can
    /// check against a published table, which is the point of using the standard
    /// model rather than a rule of thumb.
    ///
    /// It is the standard atmosphere and not the weather: a deep low or a hot
    /// day moves the real figure a few kPa either side of this. Anyone with a
    /// barometric reading from their own ECU should type that instead.
    /// </summary>
    public static double BarometricKpa(double metres)
    {
        double ratio = 1 - (TemperatureLapseRate * metres / SeaLevelKelvin);

        return ratio > 0 ? AtmosphericKpa * Math.Pow(ratio, BarometricExponent) : 0;
    }

    /// <summary>The altitude a barometric pressure corresponds to, for going back the other way.</summary>
    public static double AltitudeMetres(double barometricKpa)
    {
        if (barometricKpa <= 0) return double.NaN;

        double ratio = Math.Pow(barometricKpa / AtmosphericKpa, 1 / BarometricExponent);

        return SeaLevelKelvin / TemperatureLapseRate * (1 - ratio);
    }

    private const double TemperatureLapseRate = 0.0065;

    private const double SeaLevelKelvin = 288.15;

    /// <summary>gM/RL, the exponent the barometric formula turns on.</summary>
    private const double BarometricExponent = 5.255877;

    // ----- compressor ----------------------------------------------------------

    /// <summary>
    /// What an air filter and the pipe behind it cost the compressor inlet, as a
    /// figure to assume when nothing better is known. A convention, and the one
    /// turbocharger makers use in their own worked examples.
    /// </summary>
    public const double TypicalInletLossKpa = KpaPerPsi;

    /// <summary>
    /// The same for the intercooler and charge pipe between compressor and
    /// manifold. Two psi rather than one, which is the figure the same worked
    /// examples use — an intercooler costs more than a filter does.
    /// </summary>
    public const double TypicalChargeLossKpa = 2 * KpaPerPsi;

    /// <summary>
    /// The pressures either side of a compressor, and the ratio between them.
    /// </summary>
    /// <param name="InletKpa">Absolute pressure the compressor is breathing.</param>
    /// <param name="OutletKpa">Absolute pressure it has to deliver.</param>
    /// <param name="Ratio">The second over the first, which is what a map is read on.</param>
    public readonly record struct Compressor(double InletKpa, double OutletKpa, double Ratio);

    /// <summary>
    /// The pressure ratio a compressor is actually being asked to work at.
    ///
    /// Both pressures are taken at the compressor rather than at the manifold,
    /// which is what makes this different from the boost figure. The inlet is
    /// below ambient because the filter and the pipe to it cost something; the
    /// outlet is above manifold pressure because the intercooler and charge pipe
    /// cost something too. Both losses push the ratio up, so leaving them out
    /// flatters the turbocharger and picks one that runs out of map.
    ///
    /// Altitude pushes it up as well, and harder than people expect: a gauge
    /// reads boost against whatever the engine is breathing, so the same twelve
    /// psi asks for a ratio of 2.10 at sea level and 2.34 at five thousand feet.
    /// A compressor chosen at sea level can be over its island in the mountains
    /// at boost it makes every day at home.
    ///
    /// Not to be confused with <see cref="ChargeDensityRatio"/>, which is taken
    /// against sea level rather than against the local air, and moves the other
    /// way with altitude. They are both called the pressure ratio and they are
    /// not the same number.
    /// </summary>
    public static Compressor CompressorPressures(
        double boostGaugeKpa,
        double barometricKpa = AtmosphericKpa,
        double inletLossKpa = 0,
        double chargeLossKpa = 0)
    {
        double inlet = barometricKpa - inletLossKpa;
        double outlet = barometricKpa + boostGaugeKpa + chargeLossKpa;

        double ratio = inlet > 0 && outlet > 0 ? outlet / inlet : double.NaN;

        return new Compressor(inlet, outlet, ratio);
    }

    /// <summary>
    /// How dense the charge is against the standard atmosphere, which is the
    /// figure an airflow in pounds per minute has to be scaled by.
    ///
    /// Taken against sea level on purpose, and this is the trap in the whole
    /// tab. The compressor's ratio is measured against the air it is breathing,
    /// so it climbs as you go up. This one is measured against the standard
    /// density that the 0.0765 lb/ft³ constant is quoted at, so it falls — the
    /// same ten psi on the gauge is less absolute pressure in the manifold at
    /// altitude, and therefore less air and less power.
    ///
    /// Dividing this by the local barometric instead would cancel the altitude
    /// straight back out and overstate the mass flow by a fifth at five thousand
    /// feet — an error that reads perfectly, because the number it produces is
    /// the one you would have got at sea level.
    /// </summary>
    public static double ChargeDensityRatio(double manifoldAbsoluteKpa) =>
        manifoldAbsoluteKpa / AtmosphericKpa;

    // ----- airflow -------------------------------------------------------------

    /// <summary>
    /// Cubic inches in a litre, for the airflow formula, which is imperial.
    /// </summary>
    public const double CubicInchesPerLitre = 61.023744;

    /// <summary>
    /// Air an engine demands, in cubic feet per minute.
    ///
    /// The standard four-stroke formula: displacement times speed, halved
    /// because a four-stroke draws in once every two revolutions, and divided by
    /// 1,728 to turn cubic inches into cubic feet. The 3,456 below is those two
    /// together.
    ///
    /// Volumetric efficiency is how completely each stroke fills — around 80 per
    /// cent for an ordinary engine, near 100 for a good one on song, and beyond
    /// it for a tuned intake at resonance. Under boost the whole figure scales
    /// with the pressure ratio, because that is what forcing more air in means.
    /// </summary>
    public static double CubicFeetPerMinute(
        double litres, double rpm, double vePercent, double pressureRatio = 1)
    {
        if (litres <= 0 || rpm <= 0) return 0;

        double cubicInches = litres * CubicInchesPerLitre;

        return cubicInches * rpm * (vePercent / 100) * pressureRatio / 3456;
    }

    /// <summary>The same in cubic metres per hour, for anyone not working in feet.</summary>
    public static double CubicMetresPerHour(double cubicFeetPerMinute) =>
        cubicFeetPerMinute * 0.0283168 * 60;

    /// <summary>
    /// Air mass in pounds per minute, which is how turbocharger maps are drawn.
    ///
    /// At the standard density of dry air at sea level and 15 °C. A compressor
    /// map is read in these units, so this is the number that says which
    /// turbocharger an engine wants.
    ///
    /// The assumption worth knowing about: taking a boosted volume flow at
    /// standard density is taking the charge as intercooled the whole way back
    /// to ambient, which nothing does. A real charge at 45 °C is about a tenth
    /// less dense than that, so under boost this reads about a tenth high. It
    /// errs towards the larger turbocharger, which is the safe direction to be
    /// wrong in, but it is a tenth and not nothing.
    /// </summary>
    public static double AirPoundsPerMinute(double cubicFeetPerMinute) =>
        cubicFeetPerMinute * 0.0764742;

    /// <summary>
    /// The BSFC a reasonably efficient engine actually achieves at full
    /// throttle, which is not the same use of the number as sizing.
    ///
    /// The 0.60 used for injectors and pumps is deliberately pessimistic: it
    /// oversizes, and oversizing a pump costs money where undersizing one costs
    /// an engine. Estimating power is the other direction — a pessimistic figure
    /// there quietly understates what the engine makes. A decent modern engine
    /// is nearer 0.50 at full throttle, and this is the figure that gets the
    /// familiar rules of thumb out the far end.
    /// </summary>
    public const double FullThrottleBsfc = 0.50;

    /// <summary>
    /// The power a given mass of air can make, on a given fuel.
    ///
    /// Air is the thing an engine is actually short of, so this is the sizing
    /// arithmetic run backwards: the air divided by the ratio gives the fuel,
    /// and the fuel divided by the BSFC gives the power.
    ///
    /// The result that surprises people: it is very nearly the same number on
    /// every fuel. A kilogram of air carries about 2.95 MJ of petrol with it at
    /// stoichiometric, 2.98 of ethanol and 3.09 of methanol, so for the same air
    /// at the same lambda the alcohols are worth about one and four per cent.
    /// What actually makes them worth having is that they resist knock and cool
    /// the charge, which lets an engine run more boost and more timing — and
    /// that arrives as more air, not as more power per pound of it.
    /// </summary>
    /// <param name="lambda">Mixture at full throttle, not at cruise.</param>
    /// <param name="petrolBsfc">
    /// Efficiency as it would be on petrol; the fuel's own figure is scaled from
    /// it by <see cref="SuggestedBsfc"/>, so the comparison is like for like.
    /// </param>
    public static double HorsepowerFromAir(
        double airPoundsPerMinute, Fuel fuel, double lambda, double petrolBsfc)
    {
        double afr = AfrFromLambda(lambda, fuel);
        double bsfc = SuggestedBsfc(fuel, petrolBsfc);

        if (airPoundsPerMinute <= 0 || !(afr > 0) || !(bsfc > 0)) return 0;

        return airPoundsPerMinute * 60 / (afr * bsfc);
    }

    // ----- injectors -----------------------------------------------------------

    /// <summary>
    /// Brake specific fuel consumption worth assuming when nothing better is
    /// known: pounds of fuel per horsepower per hour.
    ///
    /// A convention rather than a fact, and the figure the whole injector
    /// calculation rests on. Naturally aspirated petrol engines sit near 0.45
    /// to 0.50; boosted ones are run richer for safety and land nearer 0.55 to
    /// 0.65. Ethanol needs roughly a third more fuel by mass for the same power,
    /// which the fuel's own stoichiometry accounts for below.
    /// </summary>
    public const double NaturallyAspiratedBsfc = 0.48;

    public const double BoostedBsfc = 0.60;

    /// <summary>
    /// What a compression-ignition engine runs at, which is not a scaling of
    /// anything above.
    /// </summary>
    public const double DieselBsfc = 0.36;

    /// <summary>
    /// The brake specific fuel consumption to expect on a fuel, given the figure
    /// that would apply on petrol in the same engine.
    ///
    /// Scaled by energy content, because that is the thing that actually varies:
    /// a given power is a given amount of energy per hour, so a fuel carrying
    /// less of it per kilogram is burned in proportionally greater mass. E85
    /// carries about two thirds of petrol's energy by mass and wants half again
    /// the BSFC; methanol carries under half and wants a little over twice. LPG
    /// and CNG carry more than petrol and want slightly less.
    ///
    /// Diesel is answered with a figure of its own rather than by this scaling.
    /// Diesel fuel carries almost exactly as much energy per kilogram as petrol,
    /// so scaling would hand back petrol's number — where the engine burning it
    /// is a different thermal efficiency altogether and runs near 0.36. The
    /// scaling is not wrong there so much as answering a question about the fuel
    /// when the difference is in the engine.
    /// </summary>
    public static double SuggestedBsfc(Fuel fuel, double petrolBsfc)
    {
        if (fuel == Fuel.Diesel) return DieselBsfc;

        double energy = EnergyMjPerKg(fuel);

        return energy > 0 && petrolBsfc > 0
            ? petrolBsfc * PetrolEnergy / energy
            : double.NaN;
    }

    /// <summary>
    /// The petrol figure a BSFC on some other fuel came from — the inverse of
    /// <see cref="SuggestedBsfc"/>.
    ///
    /// For following a fuel change without throwing away what was typed. Someone
    /// who entered 0.48 because their engine is naturally aspirated, or 0.52
    /// because they measured it, means something by that figure, and replacing
    /// it with a boosted convention loses it. Scaling keeps it.
    ///
    /// Diesel has no petrol equivalent, because its figure never came from one.
    /// </summary>
    public static double PetrolEquivalentBsfc(Fuel fuel, double bsfc)
    {
        if (fuel == Fuel.Diesel || !(bsfc > 0)) return double.NaN;

        double energy = EnergyMjPerKg(fuel);

        return energy > 0 ? bsfc * energy / PetrolEnergy : double.NaN;
    }

    /// <summary>
    /// Typical BSFC for every fuel, aspirated and boosted, as a table.
    ///
    /// Worked out from the energy content rather than written down, so the
    /// legend cannot drift away from what the calculator actually does with the
    /// figure — a printed table that disagrees with the arithmetic beside it is
    /// worse than no table.
    /// </summary>
    public static string BsfcLegend(Fuel highlight)
    {
        List<string> lines = [$"  {"",-9} {"NA",5} {"boosted",8}"];

        foreach (Fuel fuel in Enum.GetValues<Fuel>())
        {
            // Diesel is a fixed figure rather than a scaling, and it is the same
            // one whether the engine is boosted or not — most are.
            (string na, string boosted) = fuel == Fuel.Diesel
                ? (DieselBsfc.ToString("N2"), "—")
                : (SuggestedBsfc(fuel, NaturallyAspiratedBsfc).ToString("N2"),
                   SuggestedBsfc(fuel, BoostedBsfc).ToString("N2"));

            lines.Add($"{(fuel == highlight ? "▸" : " ")} {ShortName(fuel),-9} {na,5} {boosted,8}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The BSFC worth typing for a fuel, as the line that sits beside the box.
    /// </summary>
    public static string BsfcHint(Fuel fuel) => fuel == Fuel.Diesel
        ? $"lb/hp/hr — about {DieselBsfc:N2}"
        : $"lb/hp/hr — {SuggestedBsfc(fuel, NaturallyAspiratedBsfc):N2} NA, "
        + $"{SuggestedBsfc(fuel, BoostedBsfc):N2} boosted";

    /// <summary>
    /// What the fuel changes about sizing an injector, in words.
    ///
    /// Here rather than in the window because it was wrong in the window and
    /// nothing could see it. A single sentence written around E85 — "around 0.75
    /// to 0.85 where petrol would be 0.60" — was shown to anyone who picked any
    /// fuel but petrol or diesel. It was low for E85, less than half of what
    /// methanol wants, and the wrong direction entirely for LPG and CNG, which
    /// carry more energy than petrol rather than less.
    ///
    /// The advice a calculator gives is as much its output as the numbers are,
    /// and a tuner acts on it just as directly. It belongs where it can be
    /// checked against the arithmetic it is describing.
    /// </summary>
    public static string BsfcGuidance(Fuel fuel)
    {
        if (fuel == Fuel.Petrol)
            return "BSFC is a convention rather than a measurement. If you know yours from a "
                 + "previous tune, use it — it is the figure everything here rests on.";

        if (fuel == Fuel.Diesel)
            return "A diesel's fuel consumption is set by the engine's efficiency rather than by "
                 + $"the fuel, so it does not scale from petrol's: figure on {DieselBsfc:N2}. And a "
                 + "common-rail injector is rated in cubic millimetres per stroke at over a thousand "
                 + "bar, not in cc/min at three, so the sizing above does not describe one.";

        double share = EnergyMjPerKg(fuel) / EnergyMjPerKg(Fuel.Petrol);
        double boosted = SuggestedBsfc(fuel, BoostedBsfc);

        string gas = fuel is Fuel.Lpg or Fuel.Cng
            ? " Being a gas, it is also neither injected nor measured the way the figures above assume."
            : string.Empty;

        return share < 1
            ? $"{Name(fuel)} carries about {share:P0} of petrol's energy per kilogram, so the same "
            + $"power takes proportionally more of it: figure on {boosted:N2} where petrol would be "
            + $"{BoostedBsfc:N2}. Left at petrol's, the sizing above comes out at that same "
            + $"{share:P0} of the injector the fuel actually needs.{gas}"
            : $"{Name(fuel)} carries about {share:P0} of petrol's energy per kilogram, so the same "
            + $"power takes slightly less of it by mass: figure on {boosted:N2} where petrol would "
            + $"be {BoostedBsfc:N2}.{gas}";
    }

    /// <summary>
    /// The flow one injector needs, in pounds per hour.
    ///
    /// Duty cycle is deliberately not 100 per cent: an injector held open near
    /// its limit has no headroom for a cold start or a hot restart, and above
    /// about 85 per cent many stop flowing linearly, so the tune stops matching
    /// what the ECU thinks it is commanding.
    /// </summary>
    public static double InjectorPoundsPerHour(
        double horsepower, int cylinders, double bsfc, double dutyCyclePercent)
    {
        if (horsepower <= 0 || cylinders <= 0 || bsfc <= 0) return 0;

        double duty = dutyCyclePercent / 100;
        if (duty <= 0) return 0;

        return horsepower * bsfc / (cylinders * duty);
    }

    /// <summary>
    /// The same flow in cubic centimetres per minute, which is how most of the
    /// world sizes injectors.
    ///
    /// Computed from the fuel's density rather than with the usual constant of
    /// 10.5. That constant is a density of 0.72 kg/L — a light petrol, and not
    /// the 0.745 assumed here, which gives 10.15. On E85 it is nine per cent
    /// high and on methanol ten.
    ///
    /// Note the direction, because it is the opposite of what gets said about
    /// it: too large a constant asks for more cc than the mass needs, so 10.5
    /// oversizes an injector on ethanol rather than undersizing it. What
    /// undersizes an injector on ethanol is leaving the BSFC at petrol's, which
    /// is worth half again and is what <see cref="SuggestedBsfc"/> is for.
    ///
    /// Ignored here, as everywhere else: an injector's rated flow is measured on
    /// a test fluid, and a denser fuel flows a couple of per cent less volume
    /// through the same orifice at the same pressure.
    /// </summary>
    public static double CcPerMinute(double poundsPerHour, Fuel fuel = Fuel.Petrol)
    {
        double density = Density(fuel);

        return density > 0 ? poundsPerHour * 453.59237 / (60 * density) : 0;
    }

    public static double PoundsPerHourFromCc(double ccPerMinute, Fuel fuel = Fuel.Petrol) =>
        ccPerMinute * 60 * Density(fuel) / 453.59237;

    /// <summary>
    /// The power a set of injectors can support, which is the question asked
    /// when the injectors are already fitted.
    /// </summary>
    public static double SupportedHorsepower(
        double poundsPerHourEach, int cylinders, double bsfc, double dutyCyclePercent)
    {
        if (bsfc <= 0) return 0;

        return poundsPerHourEach * cylinders * (dutyCyclePercent / 100) / bsfc;
    }

    /// <summary>
    /// How an injector's flow changes with the pressure across it.
    ///
    /// Flow follows the square root of pressure, so a 550 cc injector rated at
    /// three bar flows about 635 at four. Worth having because injectors are
    /// rated at one pressure and fitted to engines running another, and the
    /// error goes unnoticed — the tune simply comes out with a strange fuel
    /// multiplier that nobody questions.
    /// </summary>
    public static double FlowAtPressure(double ratedFlow, double ratedKpa, double actualKpa)
    {
        if (ratedKpa <= 0 || actualKpa <= 0) return 0;

        return ratedFlow * Math.Sqrt(actualKpa / ratedKpa);
    }

    // ----- fuel pump -----------------------------------------------------------

    /// <summary>
    /// The fuel a given power needs, in litres per hour at the rail.
    ///
    /// A pump's rating is not this figure. Pumps are rated at a pressure and
    /// flow less as pressure rises, and a pump run at its limit runs hot and
    /// dies — so the headroom below is applied on top rather than left as an
    /// exercise.
    /// </summary>
    public static double FuelLitresPerHour(double horsepower, double bsfc, Fuel fuel = Fuel.Petrol)
    {
        double density = Density(fuel);
        if (density <= 0 || horsepower <= 0 || bsfc <= 0) return 0;

        double poundsPerHour = horsepower * bsfc;

        return poundsPerHour * 0.45359237 / density;
    }

    /// <summary>
    /// The pump to look for, with headroom.
    ///
    /// Twenty per cent by convention: a pump chosen with none is a pump at full
    /// output whenever the engine is, which is where they get hot and fail.
    /// </summary>
    public static double PumpLitresPerHour(
        double horsepower, double bsfc, Fuel fuel = Fuel.Petrol, double headroomPercent = 20) =>
        FuelLitresPerHour(horsepower, bsfc, fuel) * (1 + (headroomPercent / 100));

    /// <summary>
    /// US gallons in a litre.
    ///
    /// From the gallon's own definition — 3.785411784 litres exactly — rather
    /// than from a rounded reciprocal, because it is used on figures that are
    /// then compared against a pump's printed rating.
    /// </summary>
    public const double UsGallonsPerLitre = 1 / 3.785411784;

    /// <summary>Litres an hour as US gallons a minute, which is how larger pumps are quoted.</summary>
    public static double GallonsPerMinute(double litresPerHour) =>
        litresPerHour * UsGallonsPerLitre / 60;

    // ----- pumps as they are actually sold --------------------------------------

    /// <summary>
    /// A pump as it appears in a catalogue, rather than as a bare flow figure.
    /// </summary>
    /// <param name="LitresPerHour">Flow at <paramref name="RatedPsi"/> and 13.5 volts.</param>
    /// <param name="RatedPsi">
    /// The pressure the headline figure is quoted at, which is nearly always
    /// lower than the pressure the pump will actually see.
    /// </param>
    /// <param name="AlcoholSafe">
    /// Whether the maker rates it for ethanol. A pump that is not will pass fuel
    /// perfectly well for a while and then fail, which is the worst way to find
    /// out.
    /// </param>
    public readonly record struct FuelPump(
        string Maker,
        string Part,
        string Nickname,
        double LitresPerHour,
        double RatedPsi,
        bool AlcoholSafe)
    {
        public string Name => $"{Maker} {Part}";
    }

    /// <summary>
    /// A short catalogue of the in-tank pumps most commonly fitted.
    ///
    /// Deliberately short, and deliberately the well-known ones: a long list
    /// would be more useful and much harder to keep true, and a suggestion made
    /// from a stale figure is worse than no suggestion. Every entry carries the
    /// pressure its rating was taken at rather than the bare number, because the
    /// bare number is the thing people compare wrongly.
    ///
    /// These figures are transcribed from published specifications and are the
    /// part of this file that goes out of date on its own — makers revise parts
    /// and supersede numbers. Check against the maker's current data before
    /// buying anything on the strength of it.
    /// </summary>
    public static IReadOnlyList<FuelPump> Pumps { get; } =
    [
        new("Walbro", "GSS250", "190", 190, 43.5, false),
        new("Walbro", "GSS341", "255 offset", 255, 43.5, false),
        new("Walbro", "GSS342", "255", 255, 43.5, false),
        new("AEM", "50-1000", "320", 320, 40, true),
        new("AEM", "50-1009", "400", 400, 40, true),
        new("Walbro", "F90000262", "400", 400, 40, false),
        new("Walbro", "F90000267", "450", 450, 40, true),
        new("Walbro", "F90000285", "525", 525, 40, true),
    ];

    /// <summary>
    /// How much of its rating a pump still makes at a higher pressure.
    ///
    /// Doubling the pressure above the rating costs about forty per cent of the
    /// flow. Fitted to the published curves for the 255, which loses roughly a
    /// quarter by 70 psi and about two fifths by 87, and applied to the rest —
    /// so it is an approximation across pumps that genuinely differ, not a
    /// specification. It is here because ignoring pressure altogether produces a
    /// confident recommendation that is simply wrong for anyone running a rail
    /// above 43 psi, which is most boosted cars.
    /// </summary>
    public const double PumpPressureFalloff = 0.40;

    /// <summary>
    /// The flow a pump still delivers at the pressure it will actually see.
    ///
    /// Not the square-root law that governs injectors: an injector is an orifice
    /// with a pressure drop across it, where a pump is a pump working against a
    /// head, and the two fall off quite differently.
    /// </summary>
    public static double PumpFlowAtPressure(FuelPump pump, double psi)
    {
        if (psi <= 0 || pump.RatedPsi <= 0) return double.NaN;

        double scale = 1 - (PumpPressureFalloff * (psi - pump.RatedPsi) / pump.RatedPsi);

        return Math.Max(pump.LitresPerHour * scale, 0);
    }

    /// <summary>
    /// What each additional pump in parallel is worth, against the first.
    ///
    /// Not another whole pump: they share a line, a filter and a regulator, and
    /// the pressure drop through all three rises with the flow going through
    /// them. A tenth is a convention rather than a measurement, and it is the
    /// conservative direction.
    /// </summary>
    public const double ParallelPumpEfficiency = 0.90;

    /// <summary>Flow from several of the same pump plumbed together.</summary>
    public static double ParallelFlow(double singleFlow, int count) =>
        count <= 0 ? 0 : singleFlow * (1 + ((count - 1) * ParallelPumpEfficiency));

    /// <summary>A pump, and how many of it the job takes.</summary>
    public readonly record struct PumpChoice(FuelPump Pump, int Count, double DeliveredLitresPerHour);

    /// <summary>Most a sane fuel system runs in parallel before it should be something else.</summary>
    public const int MostPumpsWorthWiring = 3;

    /// <summary>
    /// Whether a fuel needs a pump the maker rates for alcohol.
    ///
    /// E10 is not on this list because pumps have been built to survive it for
    /// decades. Everything above it is, and methanol is harsher than any of them
    /// — a pump rated for E85 is not thereby rated for methanol.
    /// </summary>
    public static bool NeedsAlcoholSafePump(Fuel fuel) =>
        fuel is Fuel.E30 or Fuel.E50 or Fuel.E85 or Fuel.Ethanol or Fuel.Methanol;

    /// <summary>
    /// Pumps that would do the job, fewest first and least wasteful after that.
    ///
    /// Empty means nothing in the catalogue gets there within
    /// <see cref="MostPumpsWorthWiring"/>, which is the answer's way of saying
    /// the car wants a mechanical pump rather than another in-tank one.
    /// </summary>
    public static IReadOnlyList<PumpChoice> SuggestPumps(
        double litresPerHourNeeded, double railPsi, bool alcoholSafe)
    {
        if (!(litresPerHourNeeded > 0) || !(railPsi > 0)) return [];

        List<PumpChoice> choices = [];

        foreach (FuelPump pump in Pumps)
        {
            if (alcoholSafe && !pump.AlcoholSafe) continue;

            double single = PumpFlowAtPressure(pump, railPsi);
            if (!(single > 0)) continue;

            for (int count = 1; count <= MostPumpsWorthWiring; count++)
            {
                double delivered = ParallelFlow(single, count);

                if (delivered >= litresPerHourNeeded)
                {
                    choices.Add(new PumpChoice(pump, count, delivered));
                    break;
                }
            }
        }

        return [.. choices
            .OrderBy(c => c.Count)
            .ThenBy(c => c.DeliveredLitresPerHour)];
    }
}
