namespace OpenLogViewer.Core;

/// <summary>
/// The build being planned: what the engine is, and what is wanted of it.
/// </summary>
public sealed record RecipeSpec
{
    public double Litres { get; init; } = 2.0;

    public int Cylinders { get; init; } = 4;

    /// <summary>What the engine is meant to make, at the crank.</summary>
    public double TargetHorsepower { get; init; } = 500;

    /// <summary>
    /// Where full boost is wanted by.
    ///
    /// Not a detail. It is the low end of the range the compressor has to work
    /// across, and it is what decides whether a turbocharger that flows enough
    /// is also small enough to be worth driving.
    /// </summary>
    public double PeakTorqueRpm { get; init; } = 3500;

    /// <summary>Where the power figure is made, which is where the air peaks.</summary>
    public double PeakPowerRpm { get; init; } = 7000;

    public Fuel Fuel { get; init; } = Fuel.Petrol;

    /// <summary>Mixture at full throttle.</summary>
    public double Lambda { get; init; } = 0.80;

    /// <summary>
    /// Fuel consumption on petrol; the fuel's own is scaled from it, so one
    /// figure runs through the whole recipe and the parts match each other.
    /// </summary>
    public double PetrolBsfc { get; init; } = TuningMath.FullThrottleBsfc;

    public double VolumetricEfficiency { get; init; } = 97;

    /// <summary>Charge temperature in the manifold, after the intercooler.</summary>
    public double ChargeCelsius { get; init; } = 45;

    /// <summary>The most an injector should be asked to work at.</summary>
    public double InjectorDutyLimit { get; init; } = 85;

    /// <summary>Base fuel pressure, before boost is referenced onto it.</summary>
    public double RailPsi { get; init; } = 43.5;

    /// <summary>Flow a pump should have spare over what is burned.</summary>
    public double PumpHeadroomPercent { get; init; } = 20;

    public double BarometricKpa { get; init; } = TuningMath.AtmosphericKpa;

    public double InletLossKpa { get; init; } = TuningMath.TypicalInletLossKpa;

    public double ChargeLossKpa { get; init; } = TuningMath.TypicalChargeLossKpa;
}

/// <summary>Something about the plan worth saying out loud.</summary>
/// <param name="Severity">How much it matters: "note", "watch" or "stop".</param>
/// <param name="Text">What it is.</param>
public readonly record struct RecipeWarning(string Severity, string Text);

/// <summary>The parts list, and the arithmetic behind it.</summary>
public sealed record Recipe
{
    public required double AirAtPeakPower { get; init; }

    /// <summary>Air at the torque peak, which is the other end of the range.</summary>
    public required double AirAtPeakTorque { get; init; }

    public required double Afr { get; init; }

    public required double Bsfc { get; init; }

    public required double ManifoldKpa { get; init; }

    public required double BoostKpa { get; init; }

    public required double PressureRatio { get; init; }

    public required IReadOnlyList<TurboMatch> Turbos { get; init; }

    public required double InjectorCcEach { get; init; }

    public required double InjectorLbHrEach { get; init; }

    public required double FuelLitresPerHour { get; init; }

    public required double PumpLitresPerHour { get; init; }

    public required double RailUnderBoostPsi { get; init; }

    public required IReadOnlyList<TuningMath.PumpChoice> Pumps { get; init; }

    public required double MeanPistonSpeed { get; init; }

    public required double SpecificOutput { get; init; }

    public required IReadOnlyList<RecipeWarning> Warnings { get; init; }
}

/// <summary>
/// One build, worked through end to end.
///
/// Every calculator here answers a question on its own; this one asks them all
/// in the order a person actually asks them, and hands back a parts list. The
/// value is not in any single sum — each of those is tested where it lives — but
/// in their sharing one set of assumptions. A turbocharger sized at one fuel
/// consumption and injectors sized at another produce a car that is short of
/// one or the other, and nothing in either calculation says so.
///
/// So one mixture and one fuel consumption run through all of it, and the
/// margins are separate and explicit: duty on the injectors, headroom on the
/// pump, flow to spare on the compressor. Anyone who wants to be more careful
/// can move a margin rather than quietly bias an assumption.
///
/// The peak torque speed earns its place. Airflow at peak power says whether a
/// compressor is large enough; airflow at the torque peak says whether it is too
/// large — a turbocharger with plenty of flow left at 7,000 rpm may be sitting
/// off the left of its map at 3,500 and never spool at all. Both ends together
/// are the range the thing has to work across, and it is the reason two builds
/// wanting the same power can want different turbochargers.
/// </summary>
public static class EngineRecipe
{
    /// <summary>
    /// The least of a compressor's rated flow it is comfortable passing.
    ///
    /// A rule of thumb rather than a specification: the surge line of a real map
    /// is a curve, not a fraction, and it moves with pressure ratio. A quarter is
    /// where the left-hand edge of these maps roughly sits at the ratios a road
    /// car runs, and it is here to raise an eyebrow rather than to decide
    /// anything. The map decides.
    /// </summary>
    public const double LowestComfortableFlowFraction = 0.25;

    public static Recipe Build(RecipeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        double afr = TuningMath.AfrFromLambda(spec.Lambda, spec.Fuel);
        double bsfc = TuningMath.SuggestedBsfc(spec.Fuel, spec.PetrolBsfc);

        // ----- the air ---------------------------------------------------------

        double air = TurboSizing.AirForHorsepower(spec.TargetHorsepower, afr, bsfc);

        double manifold = TurboSizing.ManifoldKpaFor(
            air, spec.Litres, spec.PeakPowerRpm, spec.VolumetricEfficiency, spec.ChargeCelsius);

        double boost = manifold - spec.BarometricKpa;

        TuningMath.Compressor compressor = TuningMath.CompressorPressures(
            boost, spec.BarometricKpa, spec.InletLossKpa, spec.ChargeLossKpa);

        // At the torque peak the manifold is at the same pressure and the engine
        // is turning slower, so it swallows less air in proportion. The same
        // filling is assumed at both ends, which flatters the low end slightly —
        // volumetric efficiency usually peaks nearer the torque peak than the
        // power peak.
        double torqueAir = spec.PeakPowerRpm > 0
            ? air * spec.PeakTorqueRpm / spec.PeakPowerRpm
            : double.NaN;

        // ----- the fuel --------------------------------------------------------

        double injectorLbHr = TuningMath.InjectorPoundsPerHour(
            spec.TargetHorsepower, spec.Cylinders, bsfc, spec.InjectorDutyLimit);

        double injectorCc = TuningMath.CcPerMinute(injectorLbHr, spec.Fuel);

        double burned = TuningMath.FuelLitresPerHour(spec.TargetHorsepower, bsfc, spec.Fuel);
        double pump = TuningMath.PumpLitresPerHour(
            spec.TargetHorsepower, bsfc, spec.Fuel, spec.PumpHeadroomPercent);

        // A manifold-referenced regulator holds the difference across the
        // injector steady, so the rail rises with the boost and the pump sees all
        // of it. That is the ordinary arrangement and the one that makes the
        // injector sizing above hold under boost.
        double railUnderBoost = spec.RailPsi + Math.Max(boost, 0) / TuningMath.KpaPerPsi;

        // ----- what it says about the engine -----------------------------------

        double stroke = StrokeFor(spec);
        double pistonSpeed = EngineGeometry.MeanPistonSpeed(stroke, spec.PeakPowerRpm);

        return new Recipe
        {
            AirAtPeakPower = air,
            AirAtPeakTorque = torqueAir,
            Afr = afr,
            Bsfc = bsfc,
            ManifoldKpa = manifold,
            BoostKpa = boost,
            PressureRatio = compressor.Ratio,
            Turbos = TurboSizing.Suggest(air),
            InjectorCcEach = injectorCc,
            InjectorLbHrEach = injectorLbHr,
            FuelLitresPerHour = burned,
            PumpLitresPerHour = pump,
            RailUnderBoostPsi = railUnderBoost,
            Pumps = TuningMath.SuggestPumps(
                pump, railUnderBoost, TuningMath.NeedsAlcoholSafePump(spec.Fuel)),
            MeanPistonSpeed = pistonSpeed,
            SpecificOutput = spec.Litres > 0 ? spec.TargetHorsepower / spec.Litres : double.NaN,
            Warnings = Check(spec, air, torqueAir, boost, pistonSpeed),
        };
    }

    /// <summary>
    /// A stroke to judge piston speed by, where the build has not named one.
    ///
    /// Taken as square — bore equal to stroke — because the recipe asks for a
    /// displacement and a cylinder count and not for a bore. It is an estimate
    /// for one warning, and a genuinely long-stroke engine is worse than this
    /// says rather than better.
    /// </summary>
    private static double StrokeFor(RecipeSpec spec)
    {
        if (!(spec.Litres > 0) || spec.Cylinders <= 0) return double.NaN;

        double cylinderCc = spec.Litres * 1000 / spec.Cylinders;

        // A square cylinder of volume V has bore = stroke = cbrt(4V/pi).
        return Math.Cbrt(4 * cylinderCc * 1000 / Math.PI);
    }

    /// <summary>
    /// The parts of the plan worth saying out loud.
    ///
    /// Ordered by how much they matter, and worded as observations rather than
    /// refusals: it is somebody's engine and they may have reasons. What they
    /// should not be able to do is walk past one of these without noticing.
    /// </summary>
    private static IReadOnlyList<RecipeWarning> Check(
        RecipeSpec spec, double air, double torqueAir, double boost, double pistonSpeed)
    {
        var warnings = new List<RecipeWarning>();

        if (pistonSpeed > 25)
            warnings.Add(new RecipeWarning("stop",
                $"{pistonSpeed:N1} m/s of mean piston speed at {spec.PeakPowerRpm:N0} rpm is race-engine "
                + "territory, where rods and rings are consumables. Either the speed comes down or the "
                + "parts stop being production ones."));
        else if (pistonSpeed > 22)
            warnings.Add(new RecipeWarning("watch",
                $"{pistonSpeed:N1} m/s of mean piston speed is a performance redline. Production parts "
                + "will do it; they will not do it indefinitely."));

        double boostPsi = boost / TuningMath.KpaPerPsi;

        if (boostPsi > 35)
            warnings.Add(new RecipeWarning("stop",
                $"{boostPsi:N0} psi is a great deal of boost. Before buying anything, see whether more "
                + "displacement, more engine speed or better filling gets there on less — every part of "
                + "this list gets cheaper when the boost comes down."));
        else if (boostPsi > 25)
            warnings.Add(new RecipeWarning("watch",
                $"{boostPsi:N0} psi wants attention paid to the compression ratio, the fuel and the "
                + "intercooler, not just to the turbocharger."));

        if (boost <= 0)
            warnings.Add(new RecipeWarning("note",
                "This target needs no boost at all — the engine can breathe it naturally. The "
                + "turbocharger below is what it would take if the filling were worse than assumed."));

        // The compressor being too large is the failure this tool exists to
        // catch, and it is invisible in a flow figure taken at peak power alone.
        if (torqueAir > 0)
            foreach (TurboMatch match in TurboSizing.Suggest(air).Take(1))
            {
                double fraction = torqueAir / (match.Turbo.MaxFlowLbPerMinute * match.Count);

                if (fraction < LowestComfortableFlowFraction)
                    warnings.Add(new RecipeWarning("watch",
                        $"At {spec.PeakTorqueRpm:N0} rpm this engine wants {torqueAir:N0} lb/min, which is "
                        + $"{fraction:P0} of what the {match.Label} is rated for. That is near the left of "
                        + "its map, where it will be slow to come up and may surge. A smaller "
                        + "turbocharger, or two of them, would spool where you want it."));
            }

        if (spec.PeakTorqueRpm >= spec.PeakPowerRpm)
            warnings.Add(new RecipeWarning("note",
                "The torque peak is at or above the power peak, which no engine does. The spool check "
                + "below means nothing until those are the right way round."));

        double specific = spec.Litres > 0 ? spec.TargetHorsepower / spec.Litres : 0;

        if (specific > 250)
            warnings.Add(new RecipeWarning("watch",
                $"{specific:N0} hp per litre is a serious specific output. It is done, but not on "
                + "standard internals and not for long on pump fuel."));

        if (spec.Fuel == Fuel.Petrol && boostPsi > 20)
            warnings.Add(new RecipeWarning("note",
                "Pump petrol at this boost is the limit worth thinking about. E85 buys knock margin "
                + "cheaply — see the Octane calculator — at the cost of about half again the fuel, "
                + "which the injectors and pump below already account for if you change the fuel here."));

        return warnings;
    }
}
