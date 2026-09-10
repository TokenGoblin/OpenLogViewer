namespace OpenLogViewer.Core;

/// <summary>Where a figure the dyno needs actually came from.</summary>
public enum InputSource
{
    /// <summary>Read out of the recording's own channels.</summary>
    Log,

    /// <summary>Read out of the tune the recording carries.</summary>
    Tune,

    /// <summary>Typed in by whoever is running this.</summary>
    Entered,

    /// <summary>Nobody said, so something ordinary was used.</summary>
    Assumed,

    /// <summary>Nobody said and nothing sensible can be used instead.</summary>
    Missing,
}

/// <summary>One thing the dyno needs, and how well it is known.</summary>
/// <param name="Name">What it is, in the words a person would use.</param>
/// <param name="Value">What is being used, formatted for reading.</param>
/// <param name="Source">Where that came from.</param>
/// <param name="Note">Why it matters, or what to do about it.</param>
public readonly record struct DynoInput(string Name, string Value, InputSource Source, string Note)
{
    public bool NeedsAttention => Source is InputSource.Missing or InputSource.Assumed;
}

/// <summary>Everything the dyno knows before it draws anything.</summary>
public sealed record DynoInputs
{
    public required IReadOnlyList<DynoInput> Inputs { get; init; }

    /// <summary>The engine as far as the log and its tune could describe it.</summary>
    public required EngineSpec Engine { get; init; }

    /// <summary>Which routes to a figure this recording can support.</summary>
    public required IReadOnlyList<PowerMethodKind> Available { get; init; }

    /// <summary>And what the rest wanted.</summary>
    public required IReadOnlyList<(PowerMethodKind Method, string Needs)> Unavailable { get; init; }

    public required string Summary { get; init; }

    public bool CanDrawAnything => Available.Count > 0;

    public IEnumerable<DynoInput> Missing => Inputs.Where(i => i.Source == InputSource.Missing);

    public IEnumerable<DynoInput> Assumed => Inputs.Where(i => i.Source == InputSource.Assumed);

    public IEnumerable<DynoInput> Measured =>
        Inputs.Where(i => i.Source is InputSource.Log or InputSource.Tune);

    public override string ToString() => Summary;
}

/// <summary>
/// What the dyno needs, what the recording already knows, and what is left to
/// ask for.
///
/// <para>
/// The list of inputs is long and most of it is already written down somewhere.
/// The channels say what the engine did. The tune the recording carries says how
/// the controller was set up — how many cylinders, how often each injector
/// opens, how long one takes to open and how that changes with the supply. What
/// nobody but the driver knows is the car: what it weighs, what it is geared
/// with, what it pushes through the air.
/// </para>
/// <para>
/// So this asks the recording first and the person second, and says which is
/// which for everything it ends up using. That distinction is the point. A
/// figure resting on six measurements and one guess is worth quoting; the same
/// figure resting on one measurement and six guesses is not, and from the
/// outside they look identical.
/// </para>
/// <para>
/// <b>Reading the tune is not a convenience.</b> Working the injector timing out
/// by inference from the controller's own duty channel gave the wrong answer by
/// a factor of two here, because MegaSquirt reports the same duty whether an
/// injector opens once a cycle or twice. The tune says outright. Nothing else in
/// the recording can.
/// </para>
/// </summary>
public static class DynoSetup
{
    /// <summary>
    /// Everything known and everything wanting, for this recording and this car.
    /// </summary>
    public static DynoInputs Read(LogDocument log, VehicleSpec vehicle, EngineSpec entered)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(entered);

        var inputs = new List<DynoInput>();

        EngineSpec engine = FromTune(log, entered, inputs);

        ReadChannels(log, inputs);
        ReadVehicle(vehicle, inputs);

        (var available, var unavailable) = WhatCanBeDrawn(log, vehicle);

        return new DynoInputs
        {
            Inputs = inputs,
            Engine = engine,
            Available = available,
            Unavailable = unavailable,
            Summary = Summarise(inputs, available, unavailable),
        };
    }

    // ----- the tune -----------------------------------------------------------------

    /// <summary>
    /// The engine as the recording's own tune describes it, over whatever was
    /// entered.
    ///
    /// The tune wins where it speaks, because it is the controller's own account
    /// of itself and the person is working from memory. Where it is silent — a
    /// firmware that names these differently, or a log with no tune in it — what
    /// was entered stands, and is marked as entered.
    /// </summary>
    public static EngineSpec FromTune(LogDocument log, EngineSpec entered, List<DynoInput>? into = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(entered);

        List<DynoInput> inputs = into ?? [];

        if (log.EmbeddedTune is not { Length: > 0 } text)
        {
            inputs.Add(new DynoInput(
                "Injector timing", $"{entered.OpeningsPerEngineCycle:N0} opening(s) a cycle",
                InputSource.Entered,
                "This recording carries no tune, so nothing here could be checked against the "
                + "controller. How often an injector opens is a factor of two on the fuel."));

            return entered;
        }

        MsqFile tune;

        try
        {
            tune = MsqFile.Read(text);
        }
        catch (LogFormatException)
        {
            return entered;
        }

        EngineSpec engine = entered;

        if (Number(tune, "nCylinders") is { } cylinders and > 0)
        {
            engine = engine with { Cylinders = (int)cylinders };
            inputs.Add(new DynoInput("Cylinders", $"{cylinders:N0}", InputSource.Tune, ""));
        }

        double? openings = Openings(tune);

        if (openings is { } each and > 0)
        {
            engine = engine with { OpeningsPerEngineCycle = each };

            string how = Text(tune, "alternate") ?? "";

            inputs.Add(new DynoInput(
                "Injector timing", $"{each:N0} opening(s) per injector per cycle",
                InputSource.Tune,
                $"{Number(tune, "nCylinders"):N0} cylinders over a divider of "
                + $"{Number(tune, "divider"):N0} is {Number(tune, "nCylinders") / Number(tune, "divider"):N0} "
                + $"squirt(s) a cycle, and {how.Trim('"').ToLowerInvariant()} decides how many of those "
                + "one injector sees."));
        }

        if (Number(tune, "injOpen") is { } dead and > 0)
        {
            engine = engine with { InjectorDeadTimeMs = dead };
            inputs.Add(new DynoInput(
                "Injector dead time", $"{dead:N2} ms", InputSource.Tune,
                "The time an injector takes to open, during which it flows nothing."));
        }

        if (Number(tune, "battFac") is { } perVolt and > 0)
        {
            engine = engine with { DeadTimeMsPerVolt = perVolt };
            inputs.Add(new DynoInput(
                "Dead time against supply", $"{perVolt:N3} ms per volt", InputSource.Tune,
                "A battery sagging under load opens the injectors more slowly, exactly where the "
                + "engine is working hardest."));
        }

        return engine;
    }

    /// <summary>
    /// How many times one injector opens per engine cycle, from the tune.
    ///
    /// The squirt count is the cylinders over the divider. How much of that one
    /// injector sees depends on the arrangement: fired simultaneously they all
    /// open on every squirt, but split into alternating banks each takes every
    /// other one. Timed injection is one apiece by definition.
    /// </summary>
    private static double? Openings(MsqFile tune)
    {
        double? cylinders = Number(tune, "nCylinders");
        double? divider = Number(tune, "divider");

        if (cylinders is not { } n || divider is not { } d || !(n > 0) || !(d > 0)) return null;

        double squirts = n / d;

        string arrangement = (Text(tune, "alternate") ?? "").Trim('"');
        string timing = (Text(tune, "seq_inj") ?? "").Trim('"');

        if (timing.Contains("timed", StringComparison.OrdinalIgnoreCase)
            && !timing.Contains("untimed", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return arrangement.StartsWith("Alt", StringComparison.OrdinalIgnoreCase)
            ? squirts / 2
            : squirts;
    }

    private static double? Number(MsqFile tune, string name) =>
        double.TryParse(
            tune.Value(name)?.Trim('"'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double v)
            ? v
            : null;

    private static string? Text(MsqFile tune, string name) => tune.Value(name);

    // ----- the channels ---------------------------------------------------------------

    private static void ReadChannels(LogDocument log, List<DynoInput> inputs)
    {
        Add(ChannelRole.EngineSpeed, "Engine speed", "Nothing can be drawn against anything without it.");
        Add(ChannelRole.Throttle, "Throttle", "Without it a brisk part-throttle run cannot be told from a pull.");
        Add(ChannelRole.ManifoldPressure, "Manifold pressure", "The air route needs it.");
        Add(ChannelRole.IntakeAir, "Charge temperature", "The air route needs it: it sets what a given pressure weighs.");
        Add(ChannelRole.Mixture, "Mixture", "The air route divides by it, and the fuel route is checked against it.");
        Add(ChannelRole.InjectorPulseWidth, "Injector pulse width", "The fuel route needs it.");
        Add(ChannelRole.Barometric, "Barometer", "Sets the day's air, and turns a gauge pressure into an absolute one.");
        Add(ChannelRole.BatteryVoltage, "Supply voltage", "Corrects the injector dead time as the battery sags.");
        Add(ChannelRole.VehicleSpeed, "Road speed", "Measures the gear outright. Without it the gear has to be worked out or told.");

        void Add(ChannelRole role, string name, string why)
        {
            LogChannel? found = ChannelRoles.Find(log, role);

            bool useless = found is not null
                           && role is ChannelRole.VehicleSpeed
                           && found.IsFlat;

            inputs.Add(found is null || useless
                ? new DynoInput(name, "—", InputSource.Missing, why)
                : new DynoInput(
                    name, $"{found.Name} [{found.Units}]", InputSource.Log,
                    useless ? "present but flat all session" : ""));
        }
    }

    private static void ReadVehicle(VehicleSpec vehicle, List<DynoInput> inputs)
    {
        inputs.Add(new DynoInput(
            "Mass", $"{vehicle.MassKg:N0} kg", InputSource.Entered,
            "Nothing in a recording knows it, and the answer moves with it directly."));

        inputs.Add(new DynoInput(
            "Gearing", $"{vehicle.GearRatios.Count} gears on {vehicle.FinalDrive:N2}",
            InputSource.Entered,
            "Check it against the shifts in the log before trusting any figure that rests on a gear."));

        inputs.Add(new DynoInput(
            "Drag area and rolling resistance",
            $"CdA {vehicle.DragAreaM2:N2}, Crr {vehicle.RollingResistance:N4}",
            vehicle.RoadLoadMeasured ? InputSource.Entered : InputSource.Assumed,
            vehicle.RoadLoadMeasured
                ? "measured by a coastdown"
                : "A coastdown measures both. On a hard pull in a low gear they barely signify."));

        inputs.Add(new DynoInput(
            "Driveline loss", $"{vehicle.DrivetrainLossPercent:N0}%",
            vehicle.DrivetrainLossPercent > 0 ? InputSource.Assumed : InputSource.Entered,
            vehicle.DrivetrainLossPercent > 0
                ? "No road test can measure it: the loss and the engine's output only ever appear "
                  + "added together. It sits between the wheel figure and the crank one."
                : "left at nothing, so everything stays at the wheels"));
    }

    // ----- what that adds up to ---------------------------------------------------------

    private static (IReadOnlyList<PowerMethodKind>, IReadOnlyList<(PowerMethodKind, string)>)
        WhatCanBeDrawn(LogDocument log, VehicleSpec vehicle)
    {
        List<PowerMethodKind> can = [];
        List<(PowerMethodKind, string)> cannot = [];

        bool rpm = ChannelRoles.Find(log, ChannelRole.EngineSpeed) is not null;

        LogChannel? road = ChannelRoles.Find(log, ChannelRole.VehicleSpeed);
        bool roadUsable = road is not null && !road.IsFlat;

        if (rpm && vehicle.GearRatios.Count > 0) can.Add(PowerMethodKind.RoadLoad);
        else cannot.Add((PowerMethodKind.RoadLoad, "engine speed and a gearbox to work road speed out from"));

        if (rpm
            && ChannelRoles.Find(log, ChannelRole.ManifoldPressure) is not null
            && ChannelRoles.Find(log, ChannelRole.IntakeAir) is not null)
        {
            can.Add(PowerMethodKind.SpeedDensity);
        }
        else
        {
            cannot.Add((PowerMethodKind.SpeedDensity, "manifold pressure and charge temperature"));
        }

        if (rpm && ChannelRoles.Find(log, ChannelRole.InjectorPulseWidth) is not null)
        {
            can.Add(PowerMethodKind.Injectors);
        }
        else
        {
            cannot.Add((PowerMethodKind.Injectors, "an injector pulse width"));
        }

        if (ChannelRoles.Find(log, ChannelRole.MassAirFlow) is not null) can.Add(PowerMethodKind.MassAirFlow);
        else cannot.Add((PowerMethodKind.MassAirFlow, "a mass air flow meter"));

        if (!roadUsable && can.Contains(PowerMethodKind.RoadLoad) && !can.Contains(PowerMethodKind.SpeedDensity))
        {
            cannot.Add((PowerMethodKind.RoadLoad,
                "no road speed, and no second route to work the gear out from — the gear has to be told"));
        }

        return (can, cannot);
    }

    private static string Summarise(
        IReadOnlyList<DynoInput> inputs,
        IReadOnlyList<PowerMethodKind> available,
        IReadOnlyList<(PowerMethodKind, string)> unavailable)
    {
        int measured = inputs.Count(i => i.Source is InputSource.Log or InputSource.Tune);
        int missing = inputs.Count(i => i.Source == InputSource.Missing);

        if (available.Count == 0)
        {
            return "Nothing can be drawn from this recording. "
                   + string.Join("; ", unavailable.Select(u => $"{u.Item1} wants {u.Item2}")) + ".";
        }

        string routes = available.Count == 1
            ? $"One route: {available[0]}."
            : $"{available.Count} routes: {string.Join(", ", available)}.";

        return $"{routes} {measured} of the figures came off the recording or its tune"
               + (missing > 0 ? $", {missing} are missing and the rest were entered." : " and the rest were entered.");
    }
}
