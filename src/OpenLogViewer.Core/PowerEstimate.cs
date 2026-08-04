namespace OpenLogViewer.Core;

/// <summary>
/// What the engine is, as far as estimating its power needs to know.
/// </summary>
public sealed record EngineSpec
{
    public double Litres { get; init; } = 2.0;

    public int Cylinders { get; init; } = 4;

    public Fuel Fuel { get; init; } = Fuel.Petrol;

    /// <summary>
    /// Brake specific fuel consumption: the efficiency every one of these methods
    /// turns on, and the largest thing anyone here is guessing at.
    ///
    /// The full-throttle figure rather than the sizing one. Sizing uses a
    /// deliberately pessimistic number because oversizing an injector is cheap;
    /// using that here would understate the engine by a fifth.
    /// </summary>
    public double Bsfc { get; init; } = TuningMath.FullThrottleBsfc;

    /// <summary>Volumetric efficiency, per cent, for logs that do not record it.</summary>
    public double VolumetricEfficiency { get; init; } = 95;

    /// <summary>Mixture to assume where the log carries no wideband.</summary>
    public double Lambda { get; init; } = 0.85;

    /// <summary>One injector's rated static flow, in cc per minute.</summary>
    public double InjectorCcPerMinute { get; init; } = 550;

    /// <summary>The pressure difference that rating was taken at.</summary>
    public double InjectorRatedKpa { get; init; } = 300;

    /// <summary>
    /// How long an injector takes to open before it flows anything.
    ///
    /// Subtracted from the logged pulse width, and worth getting roughly right:
    /// at 7,000 rpm a millisecond of dead time is six per cent of the cycle, so
    /// leaving it out overstates the fuel and therefore the power.
    /// </summary>
    public double InjectorDeadTimeMs { get; init; } = 1.0;

    /// <summary>
    /// True where the injectors fire twice per cycle rather than once.
    ///
    /// Doubles the duty a given pulse width represents, so getting it wrong is a
    /// factor of two on everything downstream.
    /// </summary>
    public bool BatchInjection { get; init; }

    /// <summary>
    /// True where the logged fuel pressure is already the difference across the
    /// injector, rather than a rail pressure to have the manifold taken off it.
    /// </summary>
    public bool FuelPressureIsDifferential { get; init; }

    /// <summary>Driveline loss to a wheel figure, per cent. Zero leaves it at the crank.</summary>
    public double DrivetrainLossPercent { get; init; }
}

/// <summary>One way of estimating power, and the channels that work it out.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Basis">What it rests on, including anything that had to be assumed.</param>
/// <param name="Channels">In order — later ones read earlier ones.</param>
public sealed record PowerMethod(string Name, string Basis, IReadOnlyList<MathChannel> Channels);

/// <summary>A method that could not be offered on this log, and what it wanted.</summary>
public sealed record UnavailableMethod(string Name, string Needs);

/// <summary>What a log can support.</summary>
public sealed record PowerEstimateResult(
    IReadOnlyList<PowerMethod> Methods,
    IReadOnlyList<UnavailableMethod> Unavailable)
{
    /// <summary>Every channel from every method, in the order they must be built.</summary>
    public IReadOnlyList<MathChannel> Channels => [.. Methods.SelectMany(m => m.Channels)];
}

/// <summary>
/// Estimating an engine's power from what a logger already recorded.
///
/// Two independent routes to the same number, which is the point of doing both.
/// Speed density works out the air from the manifold — pressure, temperature,
/// engine speed and how completely each stroke fills — and turns it into power
/// through the mixture and the fuel consumption. The injector route ignores the
/// air entirely and counts the fuel instead, from how long the injectors are held
/// open and how hard the rail is pushing on them.
///
/// They rest on almost nothing in common. Speed density leans on volumetric
/// efficiency, which is the number a tuner is least sure of; the injector route
/// leans on injector data and dead time, which is a different kind of uncertain.
/// So where they agree there is reason to believe the figure, and where they do
/// not, the disagreement is itself the useful output — a VE table that is out, or
/// injector data that is not what the box said. That comparison is what the
/// spread channel is for.
///
/// None of this is a dyno. Every method here multiplies by a brake specific fuel
/// consumption that has been assumed rather than measured, so the absolute number
/// carries that assumption's error — ten per cent of it, easily. What they are
/// good for is shape and change: where the power is made, what a modification did,
/// whether two runs match. Those are ratios, and the assumption divides out of a
/// ratio.
/// </summary>
public static class PowerEstimate
{
    /// <summary>Kilograms of air per minute to pounds per hour.</summary>
    private const double KgPerMinuteToLbPerHour = 132.27735731092654;

    /// <summary>Grams per second to pounds per hour.</summary>
    private const double GramsPerSecondToLbPerHour = 7.936641438521303;

    /// <summary>Grams per minute to pounds per hour.</summary>
    private const double GramsPerMinuteToLbPerHour = 0.13227735731092654;

    /// <summary>
    /// The gas constant for air, doubled for the two revolutions a four-stroke
    /// takes to fill once — the 574 that turns litres, rpm and kilopascals into
    /// kilograms a minute.
    /// </summary>
    private const double AirDensityDivisor = 574;

    public const string AirflowChannel = "Airflow (speed density)";
    public const string SpeedDensityChannel = "Power (speed density)";
    public const string DutyChannel = "Injector duty (est)";
    public const string InjectorFlowChannel = "Injector flow (est)";
    public const string FuelFlowChannel = "Fuel flow (injectors)";
    public const string InjectorPowerChannel = "Power (injectors)";
    public const string MafPowerChannel = "Power (MAF)";
    public const string TorqueChannel = "Torque (est)";
    public const string SpreadChannel = "Power spread";
    public const string WheelChannel = "Power (at the wheels)";

    /// <summary>
    /// Every estimate this log can support, and why the rest cannot be offered.
    /// </summary>
    public static PowerEstimateResult For(LogDocument log, EngineSpec spec)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(spec);

        var methods = new List<PowerMethod>();
        var missing = new List<UnavailableMethod>();

        LogChannel? rpm = ChannelRoles.Find(log, ChannelRole.EngineSpeed);
        LogChannel? map = ChannelRoles.Find(log, ChannelRole.ManifoldPressure);
        LogChannel? iat = ChannelRoles.Find(log, ChannelRole.IntakeAir);
        LogChannel? mixture = ChannelRoles.Find(log, ChannelRole.Mixture);
        LogChannel? ve = ChannelRoles.Find(log, ChannelRole.VolumetricEfficiency);
        LogChannel? maf = ChannelRoles.Find(log, ChannelRole.MassAirFlow);
        LogChannel? pulseWidth = ChannelRoles.Find(log, ChannelRole.InjectorPulseWidth);
        LogChannel? duty = ChannelRoles.Find(log, ChannelRole.InjectorDuty);

        string afr = Mixture(mixture, spec);
        string bsfc = ChannelUnits.Number(spec.Bsfc);

        if (SpeedDensity(log, spec, rpm, map, iat, ve, afr, bsfc, mixture) is { } density)
            methods.Add(density);
        else
            missing.Add(new UnavailableMethod(
                "Speed density",
                Needed([(rpm, "engine speed"), (map, "manifold pressure"), (iat, "intake air temperature")])));

        if (Injectors(spec, rpm, pulseWidth, duty, log, bsfc) is { } injectors)
            methods.Add(injectors);
        else
            missing.Add(new UnavailableMethod(
                "Injectors",
                pulseWidth is null && duty is null
                    ? "an injector pulse width or duty cycle"
                    : Needed([(rpm, "engine speed")])));

        if (maf is not null)
            methods.Add(MassAirFlow(maf, afr, bsfc, mixture));
        else
            missing.Add(new UnavailableMethod("Mass air flow", "a mass air flow channel"));

        // Only worth having where there are two figures to compare.
        if (methods.Any(m => m.Name == "Speed density") && methods.Any(m => m.Name == "Injectors"))
            methods.Add(Spread());

        if (methods.Count > 0 && rpm is not null)
            methods.Add(Torque(methods[0], rpm));

        if (methods.Count > 0 && spec.DrivetrainLossPercent > 0)
            methods.Add(Wheels(methods[0], spec));

        return new PowerEstimateResult(methods, missing);
    }

    /// <summary>
    /// Air from the manifold, and power from the air.
    ///
    /// The density of what the engine swallowed, from the ideal gas law: pressure
    /// over temperature, times the swept volume, times how completely it filled.
    /// Halved because a four-stroke fills once every two turns.
    /// </summary>
    private static PowerMethod? SpeedDensity(
        LogDocument log, EngineSpec spec,
        LogChannel? rpm, LogChannel? map, LogChannel? iat, LogChannel? ve,
        string afr, string bsfc, LogChannel? mixture)
    {
        if (rpm is null || map is null || iat is null) return null;

        bool gauge = LooksLikeGauge(map);

        string absolute = gauge
            ? $"({ChannelUnits.ToKilopascals(map)} + {ChannelUnits.Number(TuningMath.AtmosphericKpa)})"
            : ChannelUnits.ToKilopascals(map);

        string filling = ve is not null
            ? ChannelUnits.ToFraction(ve)
            : ChannelUnits.Number(spec.VolumetricEfficiency / 100);

        string airflow =
            $"{ChannelUnits.Number(spec.Litres)} * {filling} * {rpm.Name} * {absolute}"
            + $" / ({ChannelUnits.Number(AirDensityDivisor)} * {ChannelUnits.ToKelvin(iat)})";

        string power =
            $"{AirflowChannel} * {ChannelUnits.Number(KgPerMinuteToLbPerHour)} / ({afr} * {bsfc})";

        return new PowerMethod(
            "Speed density",
            $"{spec.Litres:N1} L, "
            + (ve is not null ? $"VE from {ve.Name}" : $"VE assumed {spec.VolumetricEfficiency:N0}%")
            + $", {Describe(mixture, spec)}, BSFC {spec.Bsfc:N2}"
            + (gauge ? ", manifold pressure read as gauge" : ""),
            [
                new MathChannel
                {
                    Name = AirflowChannel, Units = "kg/min", Digits = 3, Expression = airflow,
                },
                new MathChannel
                {
                    Name = SpeedDensityChannel, Units = "hp", Digits = 1, Expression = power,
                },
            ]);
    }

    /// <summary>
    /// Fuel from the injectors, and power from the fuel.
    ///
    /// No air anywhere in it, which is what makes it worth having alongside the
    /// other one. Duty comes from the pulse width where the ECU does not report
    /// it directly, with the dead time taken off first — an injector held open
    /// for its opening time flows nothing at all.
    ///
    /// Flow follows the square root of the pressure across the injector, so a
    /// rail that sags under boost flows less than its rating whatever the sticker
    /// says. Where the log carries a rail pressure this follows it; where it does
    /// not, the regulator is taken to be doing its job and holding the difference
    /// constant.
    /// </summary>
    private static PowerMethod? Injectors(
        EngineSpec spec, LogChannel? rpm, LogChannel? pulseWidth, LogChannel? duty,
        LogDocument log, string bsfc)
    {
        if (duty is null && (rpm is null || pulseWidth is null)) return null;

        var channels = new List<MathChannel>();
        string dutyPercent;

        if (duty is not null)
        {
            dutyPercent = duty.Name;
        }
        else
        {
            // Per cent, so the channel reads the way an ECU reports it. The
            // divisor is 120,000 ms in a minute of firing once every two turns,
            // over 100 for the percentage — halved again for batch, which fires
            // twice as often.
            double divisor = spec.BatchInjection ? 600 : 1200;

            channels.Add(new MathChannel
            {
                Name = DutyChannel,
                Units = "%",
                Digits = 1,
                Expression =
                    $"max({ChannelUnits.ToMilliseconds(pulseWidth!)}"
                    + $" - {ChannelUnits.Number(spec.InjectorDeadTimeMs)}, 0)"
                    + $" * {rpm!.Name} / {ChannelUnits.Number(divisor)}",
            });

            dutyPercent = DutyChannel;
        }

        LogChannel? fuelPressure = ChannelRoles.Find(log, ChannelRole.FuelPressure);
        LogChannel? map = ChannelRoles.Find(log, ChannelRole.ManifoldPressure);
        LogChannel? baro = ChannelRoles.Find(log, ChannelRole.Barometric);

        string flow = ChannelUnits.Number(spec.InjectorCcPerMinute);
        string pressureNote = "rail assumed steady at its rating";

        if (fuelPressure is not null)
        {
            string across = Differential(spec, fuelPressure, map, baro);

            channels.Add(new MathChannel
            {
                Name = InjectorFlowChannel,
                Units = "cc/min",
                Digits = 0,
                Expression =
                    $"{ChannelUnits.Number(spec.InjectorCcPerMinute)}"
                    + $" * sqrt(max({across}, 0) / {ChannelUnits.Number(spec.InjectorRatedKpa)})",
            });

            flow = InjectorFlowChannel;
            pressureNote = spec.FuelPressureIsDifferential
                ? $"rail from {fuelPressure.Name}, already differential"
                : $"rail from {fuelPressure.Name}"
                  + (map is not null ? ", less manifold pressure" : ", less a standard atmosphere");
        }

        double density = TuningMath.Density(spec.Fuel);

        channels.Add(new MathChannel
        {
            Name = FuelFlowChannel,
            Units = "lb/hr",
            Digits = 1,
            Expression =
                $"{flow} * ({dutyPercent} / 100) * {spec.Cylinders}"
                + $" * {ChannelUnits.Number(density)}"
                + $" * {ChannelUnits.Number(GramsPerMinuteToLbPerHour)}",
        });

        channels.Add(new MathChannel
        {
            Name = InjectorPowerChannel,
            Units = "hp",
            Digits = 1,
            Expression = $"{FuelFlowChannel} / {bsfc}",
        });

        return new PowerMethod(
            "Injectors",
            $"{spec.Cylinders} × {spec.InjectorCcPerMinute:N0} cc/min"
            + (duty is null ? $", {spec.InjectorDeadTimeMs:N2} ms dead time" : $", duty from {duty.Name}")
            + $", {(spec.BatchInjection ? "batch" : "sequential")}, {pressureNote}"
            + $", {TuningMath.Name(spec.Fuel)}, BSFC {spec.Bsfc:N2}",
            channels);
    }

    /// <summary>
    /// The pressure the injector is actually working against.
    ///
    /// A rail sensor reads against the atmosphere while the injector sprays into
    /// the manifold, so under boost the difference across it is less than the
    /// gauge says by exactly the boost. On a manifold-referenced regulator the
    /// two move together and this comes out constant, which is the point of one.
    /// </summary>
    private static string Differential(
        EngineSpec spec, LogChannel fuelPressure, LogChannel? map, LogChannel? baro)
    {
        string rail = ChannelUnits.ToKilopascals(fuelPressure);

        if (spec.FuelPressureIsDifferential) return rail;

        string ambient = baro is not null
            ? ChannelUnits.ToKilopascals(baro)
            : ChannelUnits.Number(TuningMath.AtmosphericKpa);

        if (map is null) return rail;

        string manifold = LooksLikeGauge(map)
            ? $"({ChannelUnits.ToKilopascals(map)} + {ambient})"
            : ChannelUnits.ToKilopascals(map);

        return $"({rail} + {ambient} - {manifold})";
    }

    /// <summary>Power from a meter that measured the air, where the car has one.</summary>
    private static PowerMethod MassAirFlow(
        LogChannel maf, string afr, string bsfc, LogChannel? mixture) =>
        new("Mass air flow",
            $"measured air from {maf.Name}, {Describe(mixture, null)}, BSFC {bsfc}",
            [
                new MathChannel
                {
                    Name = MafPowerChannel,
                    Units = "hp",
                    Digits = 1,
                    Expression =
                        $"{ChannelUnits.ToGramsPerSecond(maf)}"
                        + $" * {ChannelUnits.Number(GramsPerSecondToLbPerHour)} / ({afr} * {bsfc})",
                },
            ]);

    /// <summary>
    /// How far the two independent estimates are apart, as a percentage of the
    /// air-based one.
    ///
    /// The channel worth watching. Both rest on the same assumed fuel
    /// consumption, so that cancels and what is left is the disagreement between
    /// the volumetric efficiency and the injector data. A few per cent is noise.
    /// A steady twenty means one of the two is wrong, and which one it is is
    /// usually obvious from where in the log it happens.
    /// </summary>
    private static PowerMethod Spread() =>
        new("Agreement",
            "how far the injector estimate sits from the air estimate",
            [
                new MathChannel
                {
                    Name = SpreadChannel,
                    Units = "%",
                    Digits = 1,
                    Expression =
                        $"({InjectorPowerChannel} - {SpeedDensityChannel})"
                        + $" / max({SpeedDensityChannel}, 1) * 100",
                },
            ]);

    private static PowerMethod Torque(PowerMethod preferred, LogChannel rpm) =>
        new("Torque",
            $"from {preferred.Channels[^1].Name}",
            [
                new MathChannel
                {
                    Name = TorqueChannel,
                    Units = "lb-ft",
                    Digits = 1,
                    Expression = $"{preferred.Channels[^1].Name} * 5252 / max({rpm.Name}, 1)",
                },
            ]);

    private static PowerMethod Wheels(PowerMethod preferred, EngineSpec spec) =>
        new("At the wheels",
            $"{spec.DrivetrainLossPercent:N0}% taken off {preferred.Channels[^1].Name}",
            [
                new MathChannel
                {
                    Name = WheelChannel,
                    Units = "hp",
                    Digits = 1,
                    Expression =
                        $"{preferred.Channels[^1].Name}"
                        + $" * {ChannelUnits.Number(1 - (spec.DrivetrainLossPercent / 100))}",
                },
            ]);

    /// <summary>The mixture to divide the air by: the logged one where there is one.</summary>
    private static string Mixture(LogChannel? mixture, EngineSpec spec) =>
        mixture is not null
            ? ChannelUnits.ToAirFuelRatio(mixture, spec.Fuel)
            : ChannelUnits.Number(spec.Lambda * TuningMath.Stoichiometric(spec.Fuel));

    private static string Describe(LogChannel? mixture, EngineSpec? spec) =>
        mixture is not null
            ? $"mixture from {mixture.Name}"
            : spec is not null
                ? $"lambda assumed {spec.Lambda:N2}"
                : "mixture assumed";

    /// <summary>
    /// Whether a manifold pressure channel is a gauge reading rather than an
    /// absolute one.
    ///
    /// Told by the values, because the units cannot say it — "psi" is written the
    /// same either way. Anything that goes meaningfully below zero is a gauge; a
    /// true absolute pressure has nowhere below vacuum to go.
    /// </summary>
    internal static bool LooksLikeGauge(LogChannel channel)
    {
        for (int i = 0; i < channel.Length; i++)
        {
            double v = channel.At(i);

            if (double.IsFinite(v) && v < -1) return true;
        }

        return false;
    }

    private static string Needed(IReadOnlyList<(LogChannel? Channel, string Name)> wanted)
    {
        string[] absent = [.. wanted.Where(w => w.Channel is null).Select(w => w.Name)];

        return absent.Length switch
        {
            0 => "nothing — it should have been offered",
            1 => absent[0],
            _ => string.Join(", ", absent[..^1]) + " and " + absent[^1],
        };
    }
}
