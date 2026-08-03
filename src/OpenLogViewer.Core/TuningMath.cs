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

    /// <summary>
    /// An ethanol blend's stoichiometric ratio, by mass fraction.
    ///
    /// The two fuels are mixed by volume and burn by mass, so the blend is
    /// weighted by how much of the mixture's mass each contributes — which is
    /// why E85 lands near 9.8 rather than at the 10.3 a straight volume average
    /// of 14.7 and 9.0 would give.
    /// </summary>
    private static double Blend(double ethanolByVolume)
    {
        const double PetrolDensity = 0.745;
        const double EthanolDensity = 0.789;

        double ethanolMass = ethanolByVolume * EthanolDensity;
        double petrolMass = (1 - ethanolByVolume) * PetrolDensity;
        double total = ethanolMass + petrolMass;

        // Air needed per unit of mixture, then back to a ratio.
        double air = (ethanolMass * 9.0) + (petrolMass * 14.7);

        return air / total;
    }

    /// <summary>Density in kilograms per litre, for turning a mass flow into a volume.</summary>
    public static double Density(Fuel fuel) => fuel switch
    {
        Fuel.Petrol => 0.745,
        Fuel.E10 => 0.749,
        Fuel.E30 => 0.758,
        Fuel.E50 => 0.767,
        Fuel.E85 => 0.782,
        Fuel.Ethanol => 0.789,
        Fuel.Methanol => 0.792,
        Fuel.Diesel => 0.832,
        Fuel.Lpg => 0.510,
        Fuel.Cng => 0.180,
        _ => 0.745,
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

    /// <summary>The air-fuel ratio a lambda corresponds to on a given fuel.</summary>
    public static double AfrFromLambda(double lambda, Fuel fuel) => lambda * Stoichiometric(fuel);

    /// <summary>The lambda an air-fuel ratio corresponds to on a given fuel.</summary>
    public static double LambdaFromAfr(double afr, Fuel fuel)
    {
        double stoich = Stoichiometric(fuel);

        return stoich > 0 ? afr / stoich : double.NaN;
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
    /// zero at atmospheric. Ten psi of boost is 169 kPa absolute, not 69.
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
    /// </summary>
    public static double AirPoundsPerMinute(double cubicFeetPerMinute) =>
        cubicFeetPerMinute * 0.0764742;

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
    /// 10.5, which quietly assumes petrol. On E85 that constant is out by about
    /// five per cent in the direction that undersizes the injector.
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
}
