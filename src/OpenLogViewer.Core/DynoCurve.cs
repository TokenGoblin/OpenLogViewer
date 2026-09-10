namespace OpenLogViewer.Core;

/// <summary>Which route to the figure a curve took.</summary>
public enum PowerMethodKind
{
    /// <summary>From how hard the car accelerated. Knows nothing about combustion.</summary>
    RoadLoad,

    /// <summary>From the fuel the injectors delivered.</summary>
    Injectors,

    /// <summary>From the air a meter measured.</summary>
    MassAirFlow,

    /// <summary>From the air the manifold implies.</summary>
    SpeedDensity,

    /// <summary>From the controller's own torque model.</summary>
    EcuTorque,
}

/// <summary>
/// Where in the driveline a figure is quoted.
///
/// Not decoration. A road test measures at the wheels and cannot measure
/// anywhere else; anything worked out from the fuel or the air is what the
/// engine made before the driveline took its share. The two differ by fifteen
/// per cent or so on a rear-drive manual, and subtracting one from the other
/// without saying so credits the gearbox to the tuning.
/// </summary>
public enum PowerReference
{
    /// <summary>What reached the road. What a road test measures.</summary>
    Wheels,

    /// <summary>What the engine made. What fuel and air imply.</summary>
    Crank,
}

/// <summary>One rung of the curve.</summary>
/// <param name="Rpm">The engine speed this step stands for.</param>
/// <param name="Horsepower">Power there.</param>
/// <param name="PoundFeet">The same figure as torque, referenced to engine speed.</param>
/// <param name="Samples">How many readings went into it. One is a rung to distrust.</param>
public readonly record struct DynoPoint(double Rpm, double Horsepower, double PoundFeet, int Samples)
{
    public double Kilowatts => Horsepower * RoadLoad.WattsPerHorsepower / 1000;

    public double NewtonMetres => PoundFeet * 1.3558179483314004;

    public bool IsEmpty => Samples == 0;
}

/// <summary>What counts as a curve.</summary>
public sealed record DynoSettings
{
    /// <summary>
    /// How far apart the rungs are, in rpm.
    ///
    /// Even steps in engine speed rather than the log's own samples, because the
    /// samples are evenly spaced in <em>time</em> and a pull is not. Engine speed
    /// climbs fastest where the engine is strongest, so a curve drawn straight
    /// from the samples has its readings bunched at the top and thin in the
    /// middle — most closely spaced exactly where the engine is doing least.
    /// </summary>
    public int RpmStep { get; init; } = 100;

    /// <summary>
    /// Readings a step needs before it is drawn at all.
    ///
    /// One, because a lone reading here is not a lone reading. Every power figure
    /// arriving at this point has already been through a fit spanning half a
    /// second of log — nine samples at 20 Hz — so a step holding one of them
    /// holds a fitted value, not a raw one. Demanding two would punch holes in
    /// the fastest part of the pull, which is the part where the engine is
    /// strongest and engine speed climbs through a step in a single sample.
    ///
    /// Worth raising where a log is slow enough that the fit itself is thin.
    /// </summary>
    public int MinimumSamplesPerStep { get; init; } = 1;

    /// <summary>The window engine speed and road speed are differentiated over.</summary>
    public double WindowSeconds { get; init; } = RateOfChange.DefaultWindowSeconds;

    /// <summary>
    /// How much of each end of the pull to leave off, as a fraction of the
    /// fitting window.
    ///
    /// <para>
    /// The fit needs readings either side of the sample it is describing, and at
    /// the two ends of a pull there are none on one side. What it does there is
    /// still a fit rather than a guess — a one-sided quadratic through a smooth
    /// curve is a perfectly good quadratic — but it has half the evidence, and
    /// the last rung of a curve is the one people read the peak off.
    /// </para>
    /// <para>
    /// A half at each end, which is exactly the region with one-sided support:
    /// a window reaches half its length either way. It costs the top of the
    /// curve, so a pull should be taken a few hundred rpm past wherever the
    /// interest ends — which is what running to the limiter already achieves.
    /// </para>
    /// </summary>
    public double TrimWindows { get; init; } = 0.5;
}

/// <summary>
/// A pull, drawn.
/// </summary>
public sealed record DynoCurve
{
    public required IReadOnlyList<DynoPoint> Points { get; init; }

    public required PowerMethodKind Method { get; init; }

    /// <summary>What the figure rests on, including anything that was assumed.</summary>
    public required string Basis { get; init; }

    /// <summary>Where in the driveline these figures are quoted.</summary>
    public required PowerReference Reference { get; init; }

    public required PowerCorrection Correction { get; init; }

    public required double CorrectionFactor { get; init; }

    public required int RpmStep { get; init; }

    /// <summary>Readings that went into the whole curve, after trimming.</summary>
    public required int Samples { get; init; }

    public required IReadOnlyList<string> Cautions { get; init; }

    /// <summary>
    /// The share of the car's effort that went into the air, averaged over the
    /// pull.
    ///
    /// How much the road-load coefficients are worth on this particular run, and
    /// therefore how much a coastdown would buy. Two or three per cent on a hard
    /// pull in a low gear, where the answer is nearly all mass times
    /// acceleration; a third or more on a long top-gear run, where it is nearly
    /// all air. NaN for a curve that did not come from road load.
    /// </summary>
    public double AeroShare { get; init; } = double.NaN;

    public IEnumerable<DynoPoint> Drawn => Points.Where(p => !p.IsEmpty);

    /// <summary>
    /// The same curve quoted at the other end of the driveline.
    ///
    /// The conversion is exact arithmetic on a number that is not a measurement:
    /// no road test can see the driveline's share, because it and the engine's
    /// output only ever appear added together. Converting is how two methods are
    /// compared at all; it does not make either of them better known.
    /// </summary>
    public DynoCurve At(PowerReference reference, VehicleSpec vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (reference == Reference) return this;

        double remaining = 1 - (vehicle.DrivetrainLossPercent / 100);

        if (!(remaining > 0)) return this;

        double factor = reference == PowerReference.Crank ? 1 / remaining : remaining;

        return this with
        {
            Reference = reference,
            Points = [.. Points.Select(p => new DynoPoint(
                p.Rpm, p.Horsepower * factor, p.PoundFeet * factor, p.Samples))],
            Basis = Basis + $", restated at the {(reference == PowerReference.Crank ? "crank" : "wheels")} "
                          + $"on a declared {vehicle.DrivetrainLossPercent:N0}% driveline loss",
        };
    }

    public bool IsEmpty => !Drawn.Any();

    public DynoPoint PeakPower => Best(p => p.Horsepower);

    public DynoPoint PeakTorque => Best(p => p.PoundFeet);

    /// <summary>The lowest and highest rung actually drawn.</summary>
    public double FromRpm => Drawn.Any() ? Drawn.Min(p => p.Rpm) : double.NaN;

    public double ToRpm => Drawn.Any() ? Drawn.Max(p => p.Rpm) : double.NaN;

    /// <summary>
    /// Where power and torque cross, which is a check rather than a finding.
    ///
    /// It is 5,252 rpm on every dyno sheet ever printed, and not by convention:
    /// torque in pound-feet is power in horsepower times that constant over
    /// engine speed, so the two are equal exactly where engine speed equals the
    /// constant. A curve whose lines cross anywhere else has arithmetic wrong in
    /// it, which is why this is computed from the drawn points rather than
    /// assumed.
    /// </summary>
    public double CrossoverRpm
    {
        get
        {
            DynoPoint[] drawn = [.. Drawn];

            for (int i = 1; i < drawn.Length; i++)
            {
                // A rung of no power at all is not a crossing. Power is clamped
                // at nought where the car was slowing, and a pull with a driver's
                // lift in it — which is kept and flagged rather than thrown away —
                // has rungs like that. Both figures are zero there, their
                // difference is zero, and the check meant to confirm the units
                // would report the lift as the crossover instead.
                if (drawn[i - 1].Horsepower == 0 || drawn[i].Horsepower == 0) continue;

                double before = drawn[i - 1].Horsepower - drawn[i - 1].PoundFeet;
                double after = drawn[i].Horsepower - drawn[i].PoundFeet;

                if (before == 0) return drawn[i - 1].Rpm;
                if (before * after > 0) continue;

                double share = before / (before - after);

                return drawn[i - 1].Rpm + (share * (drawn[i].Rpm - drawn[i - 1].Rpm));
            }

            return double.NaN;
        }
    }

    /// <summary>The value at a given engine speed, interpolated between rungs.</summary>
    public double HorsepowerAt(double rpm) => Interpolate(rpm, p => p.Horsepower);

    public double PoundFeetAt(double rpm) => Interpolate(rpm, p => p.PoundFeet);

    public override string ToString() =>
        IsEmpty
            ? $"{Method}: nothing drawn"
            : $"{PeakPower.Horsepower:N0} hp @ {PeakPower.Rpm:N0}, "
              + $"{PeakTorque.PoundFeet:N0} lb-ft @ {PeakTorque.Rpm:N0}";

    private DynoPoint Best(Func<DynoPoint, double> of)
    {
        DynoPoint best = default;
        double top = double.NegativeInfinity;

        foreach (DynoPoint p in Drawn)
        {
            double v = of(p);

            if (!double.IsFinite(v) || v <= top) continue;

            top = v;
            best = p;
        }

        return best;
    }

    private double Interpolate(double rpm, Func<DynoPoint, double> of)
    {
        DynoPoint[] drawn = [.. Drawn];

        if (drawn.Length == 0) return double.NaN;

        // Outside the curve there is no answer. On either edge there is exactly
        // one — and it must be that edge's, which the combined test this replaces
        // got wrong at the top: asking for the highest engine speed drawn came
        // back with the value at the lowest, silently, and the top of a pull is
        // the one figure anybody reads off by name.
        if (rpm < drawn[0].Rpm || rpm > drawn[^1].Rpm) return double.NaN;
        if (rpm == drawn[0].Rpm) return of(drawn[0]);
        if (rpm == drawn[^1].Rpm) return of(drawn[^1]);

        for (int i = 1; i < drawn.Length; i++)
        {
            if (drawn[i].Rpm < rpm) continue;

            double share = (rpm - drawn[i - 1].Rpm) / (drawn[i].Rpm - drawn[i - 1].Rpm);

            return of(drawn[i - 1]) + (share * (of(drawn[i]) - of(drawn[i - 1])));
        }

        return of(drawn[^1]);
    }

    // ================= building one =============================================

    /// <summary>
    /// A curve from a pull, measured off how hard the car accelerated.
    ///
    /// <para>
    /// The one route to a figure that knows nothing about combustion. What it
    /// knows about instead is the car — its mass, the air it pushes, what the
    /// tyres cost — and it is only as good as those, which is what
    /// <see cref="Coastdown"/> exists to fix and what <see cref="Basis"/> exists
    /// to declare.
    /// </para>
    /// <para>
    /// Road speed comes from wherever the pull said it should. Engine speed
    /// through the gearing where the two kept step, because it is the finer
    /// signal; the road speed channel where they did not, because there the
    /// tachometer is describing a driveline that is slipping rather than a car
    /// that is moving.
    /// </para>
    /// </summary>
    public static DynoCurve FromRoadLoad(
        LogDocument log,
        VehicleSpec vehicle,
        DynoPull pull,
        Ambient air,
        PowerCorrection correction = PowerCorrection.None,
        DynoSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(pull);

        DynoSettings s = settings ?? new DynoSettings();

        LogChannel? engine = ChannelRoles.Find(log, ChannelRole.EngineSpeed);

        if (engine is null) return Nothing(PowerMethodKind.RoadLoad, "no engine speed channel");

        int gear = pull.Gearing.Gear;

        if (gear <= 0)
        {
            return Nothing(
                PowerMethodKind.RoadLoad,
                "the gear is not known, and effective mass cannot be worked out without it");
        }

        int count = pull.Last - pull.First + 1;

        var rpm = new double[count];
        var speed = new double[count];
        var times = new double[count];

        LogChannel? road = ChannelRoles.Find(log, ChannelRole.VehicleSpeed);
        double factor = pull.Gearing.SpeedToMetresPerSecond;

        bool fromRoad = pull.Speed == SpeedSource.VehicleSpeedChannel;

        if (fromRoad && (road is null || !(factor > 0)))
        {
            return Nothing(
                PowerMethodKind.RoadLoad,
                "the driveline was slipping, so engine speed cannot stand in for road speed, and "
                + "the road speed channel is not usable");
        }

        for (int i = 0; i < count; i++)
        {
            int at = pull.First + i;

            times[i] = log.Time.At(at);
            rpm[i] = engine.At(at);
            speed[i] = fromRoad
                ? road!.At(at) * factor
                : vehicle.SpeedMsFromRpm(rpm[i], gear);
        }

        // Fitted across the pull alone rather than across the log. Outside it the
        // car is in another gear, or on the limiter, or slowing — and a window
        // that reached out there would describe that instead. The cost is that
        // the two ends have support on one side only, which is what the trim
        // below is for.
        ChannelFit motion = RateOfChange.Fit(speed, times, s.WindowSeconds);

        double density = AirDensity.KgPerCubicMetre(air);
        double cf = AirDensity.Factor(correction, air);

        var power = new double[count];
        double aeroShare = 0;
        int shares = 0;

        for (int i = 0; i < count; i++)
        {
            power[i] = RoadLoad.Horsepower(
                vehicle, gear, motion.Value[i], motion.SlopePerSecond[i], density) * cf;

            double share = RoadLoad
                .Forces(vehicle, gear, motion.Value[i], motion.SlopePerSecond[i], density)
                .AeroShare;

            if (!double.IsFinite(share)) continue;

            aeroShare += share;
            shares++;
        }

        aeroShare = shares > 0 ? aeroShare / shares : double.NaN;

        (int from, int to) = Trim(times, s.WindowSeconds, s.TrimWindows);

        var cautions = new List<string>();

        foreach (PullFault fault in pull.Faults) cautions.Add(DynoRun.Describe(fault));

        // Said in proportion to what it is worth, which the pull decides rather
        // than the vehicle. On a hard pull in a low gear the air takes two or
        // three per cent of the effort, and being forty per cent wrong about the
        // drag area moves the answer by one — calling it "the largest guess in
        // this figure" there is simply false, and points away from the mass and
        // the gear, which are worth far more.
        if (!vehicle.RoadLoadMeasured && aeroShare >= 0.10)
        {
            cautions.Add(
                $"The air is taking {aeroShare:P0} of the effort here, and the drag area it is worked "
                + "out from was entered rather than measured. A coastdown replaces it, and at this "
                + "share it is worth having.");
        }
        else if (!vehicle.RoadLoadMeasured)
        {
            cautions.Add(
                $"The drag area and rolling resistance were entered rather than measured, and a "
                + $"coastdown would measure them — but the air is only taking {aeroShare:P0} of the "
                + "effort on this pull, so they barely signify. What this figure rests on is the mass "
                + "and the gear.");
        }

        if (correction != PowerCorrection.None && vehicle.Boosted)
        {
            cautions.Add(
                $"{AirDensity.Name(correction)} has been applied to a boosted engine. It assumes a "
                + "thin day starves the engine of air, which a turbo has already compensated for, so "
                + "the figure is flattered.");
        }

        return Build(
            rpm.AsSpan(from, to - from), power.AsSpan(from, to - from),
            PowerMethodKind.RoadLoad, RoadLoadBasis(vehicle, pull, air),
            correction, cf, cautions, s, aeroShare, PowerReference.Wheels);
    }

    /// <summary>
    /// A curve from the air the manifold implies, which cannot see the gearbox.
    ///
    /// <para>
    /// The other half of the argument. Road load knows nothing about combustion
    /// and everything about the car; this knows nothing about the car and
    /// everything about combustion — how much air went in, from the pressure,
    /// the temperature and the engine speed, and what was burned with it, from
    /// the wideband. It does not know or care what gear the pull was in, what
    /// the car weighs, or how much air it pushes aside.
    /// </para>
    /// <para>
    /// That independence is the whole value. Where the two agree the figure is
    /// worth believing; where they do not, the disagreement is about an input,
    /// and which input it is can usually be read off the shape. And on a car with
    /// no road speed sensor it does something road load cannot do alone: it fixes
    /// the gear, because only one gear makes the two agree. See
    /// <see cref="GearAgreement"/>.
    /// </para>
    /// <para>
    /// What it rests on instead is the fuel consumption, which nobody measured,
    /// and how completely the cylinder fills, which is either read from the
    /// controller's own table or assumed. Both are declared in the basis. The
    /// figure is at the crank by construction — this is what the engine made,
    /// before the driveline took anything.
    /// </para>
    /// </summary>
    public static DynoCurve FromSpeedDensity(
        LogDocument log,
        EngineSpec engine,
        DynoPull pull,
        Ambient air,
        PowerCorrection correction = PowerCorrection.None,
        DynoSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pull);

        DynoSettings s = settings ?? new DynoSettings();

        LogChannel? speed = ChannelRoles.Find(log, ChannelRole.EngineSpeed);
        LogChannel? manifold = ChannelRoles.Find(log, ChannelRole.ManifoldPressure);
        LogChannel? charge = ChannelRoles.Find(log, ChannelRole.IntakeAir);

        if (speed is null || manifold is null || charge is null)
        {
            return Nothing(
                PowerMethodKind.SpeedDensity,
                Needed(
                    (speed, "engine speed"),
                    (manifold, "manifold pressure"),
                    (charge, "charge temperature")));
        }

        LogChannel? mixture = ChannelRoles.Find(log, ChannelRole.Mixture);
        LogChannel? filling = ChannelRoles.Find(log, ChannelRole.VolumetricEfficiency);

        // Kept apart from `filling` so the caution can say what was rejected and
        // why, rather than silently falling back to the assumed figure.
        LogChannel? fuellingTable = null;

        if (filling is not null && PowerEstimate.LooksLikeFuellingTable(filling))
        {
            fuellingTable = filling;
            filling = null;
        }
        LogChannel? ambient = ChannelRoles.Find(log, ChannelRole.Barometric);

        // A manifold pressure that goes below nothing is a gauge reading, and has
        // to have the day's pressure added before the gas law will take it. Told
        // from the values because the units cannot say it — "psi" is written the
        // same either way.
        bool gauge = PowerEstimate.LooksLikeGauge(manifold);
        double toKpa = ChannelUnits.PressureToKilopascals(manifold);

        int count = pull.Last - pull.First + 1;

        var rpm = new double[count];
        var power = new double[count];
        var times = new double[count];

        double cf = AirDensity.Factor(correction, air);

        for (int i = 0; i < count; i++)
        {
            int at = pull.First + i;

            times[i] = log.Time.At(at);
            rpm[i] = speed.At(at);

            double kpa = manifold.At(at) * toKpa;

            if (gauge)
            {
                kpa += ambient is not null
                    ? ambient.At(at) * ChannelUnits.PressureToKilopascals(ambient)
                    : air.PressureKpa;
            }

            double kelvin = ChannelUnits.Kelvin(charge, charge.At(at));

            double ve = filling is not null
                ? ChannelUnits.Fraction(filling, filling.At(at))
                : engine.VolumetricEfficiency / 100;

            double afr = mixture is not null
                ? ChannelUnits.AirFuelRatio(mixture, mixture.At(at), engine.Fuel)
                : engine.Lambda * TuningMath.Stoichiometric(engine.Fuel);

            // The ideal gas law, halved for the two turns a four-stroke takes to
            // fill once — the 574 that turns litres, rpm and kilopascals into
            // kilograms a minute.
            double airKgPerMinute = engine.Litres * ve * rpm[i] * kpa / (574 * kelvin);

            power[i] = afr > 0 && engine.Bsfc > 0
                ? airKgPerMinute * KgPerMinuteToLbPerHour / (afr * engine.Bsfc) * cf
                : double.NaN;
        }

        // Not trimmed, unlike road load. The trim is there because a derivative
        // has support on one side only at the ends of a pull — and there is no
        // derivative anywhere in this. Every reading here is worked out from that
        // sample and no other, so the first and last are as good as the middle.
        //
        // Trimming anyway threw away the top of every pull, which on a
        // turbocharged engine is exactly where the boost, and the power, are.
        var cautions = new List<string>();

        foreach (PullFault fault in pull.Faults)
        {
            // The gear is not an input here, so its absence is not a fault of this
            // figure. Saying so would be borrowing somebody else's doubt.
            if (fault is PullFault.GearNotChecked or PullFault.GearNotRecognised
                or PullFault.RatioDrifted or PullFault.SpeedUnitAmbiguous)
            {
                continue;
            }

            cautions.Add(DynoRun.Describe(fault));
        }

        cautions.Add(
            $"Everything here is divided by a brake specific fuel consumption of {engine.Bsfc:N2}, "
            + "which was assumed rather than measured. The shape of the curve does not depend on it; "
            + "the height of it depends on nothing else.");

        if (fuellingTable is not null)
        {
            cautions.Add(
                $"\"{fuellingTable.Name}\" reaches {fuellingTable.Max:N0}%, so it is the controller's "
                + "fuelling table rather than a volumetric efficiency — a cylinder cannot fill to more "
                + $"than itself. {engine.VolumetricEfficiency:N0}% was assumed instead. Taking the table "
                + "at face value would have inflated the air, and every horsepower with it, by about "
                + $"{(fuellingTable.Max / engine.VolumetricEfficiency) - 1:P0}.");
        }
        else if (filling is null)
        {
            cautions.Add(
                $"This log does not report how completely the cylinder fills, so "
                + $"{engine.VolumetricEfficiency:N0}% was assumed. It multiplies the answer directly.");
        }
        else
        {
            cautions.Add(
                $"Filling was taken from \"{filling.Name}\". That is the controller's own table, and "
                + "whether it is scaled to true volumetric efficiency is a question about the tune "
                + "rather than about the log.");
        }

        return Build(
            rpm, power,
            PowerMethodKind.SpeedDensity, SpeedDensityBasis(engine, mixture, filling, gauge, air),
            correction, cf, cautions, s, double.NaN, PowerReference.Crank);
    }

    /// <summary>Kilograms of air per minute to pounds per hour.</summary>
    private const double KgPerMinuteToLbPerHour = 132.27735731092654;

    /// <summary>Grams a minute to pounds an hour.</summary>
    private const double GramsPerMinuteToLbPerHour = 0.13227735731092654;

    /// <summary>
    /// A curve from the fuel the injectors delivered, which knows nothing about
    /// the air or the gearbox.
    ///
    /// <para>
    /// The third route, and the one that settles an argument the other two cannot.
    /// Road load and speed density disagree about the power, but they disagree in
    /// a way that could be either of two assumptions — how completely the cylinder
    /// fills, or how much fuel a horsepower costs — and the air route needs both
    /// of them so it cannot separate them.
    /// </para>
    /// <para>
    /// This needs only the second. It counts fuel: how long each injector was held
    /// open, less the time it takes to crack off its seat, times what it flows, by
    /// how many there are. So held against speed density it isolates the filling,
    /// and held against road load it isolates the fuel consumption. Two unknowns,
    /// two independent comparisons.
    /// </para>
    /// <para>
    /// What it wants in return is honest injector data, and that is not the number
    /// on the box. A set sold as 850 cc/min measured 1,149 on a flow bench — a
    /// third out, and a third straight onto the answer. It also wants to know
    /// whether the injectors fire once a cycle or twice, which is a factor of two
    /// and shows up as a duty cycle that is either impossible or implausible.
    /// </para>
    /// </summary>
    public static DynoCurve FromInjectors(
        LogDocument log,
        EngineSpec engine,
        DynoPull pull,
        Ambient air,
        PowerCorrection correction = PowerCorrection.None,
        DynoSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(pull);

        DynoSettings s = settings ?? new DynoSettings();

        LogChannel? speed = ChannelRoles.Find(log, ChannelRole.EngineSpeed);
        LogChannel? width = ChannelRoles.Find(log, ChannelRole.InjectorPulseWidth);
        LogChannel? duty = ChannelRoles.Find(log, ChannelRole.InjectorDuty);

        if (speed is null || (width is null && duty is null))
        {
            return Nothing(
                PowerMethodKind.Injectors,
                speed is null
                    ? "this log has no engine speed"
                    : "this log has neither an injector pulse width nor a duty cycle");
        }

        int count = pull.Last - pull.First + 1;

        var rpm = new double[count];
        var power = new double[count];

        double cf = AirDensity.Factor(correction, air);
        double density = TuningMath.Density(engine.Fuel);

        // Once a cycle or twice — which is not the same question as batch against
        // sequential, and is the one that matters. Firing the injectors together
        // rather than timing them to each cylinder changes nothing about how much
        // fuel goes in; opening each of them twice per two turns instead of once
        // changes all of it, and pays the dead time twice over into the bargain.
        double divisor = engine.TwoSquirtsPerCycle ? 600 : 1200;

        double toMs = width is null ? 1 : ChannelUnits.TimeToMilliseconds(width);

        double peakDuty = 0;

        for (int i = 0; i < count; i++)
        {
            int at = pull.First + i;

            rpm[i] = speed.At(at);

            // From the pulse width where there is one, and from the controller's
            // own duty only where there is not.
            //
            // That is the opposite of the obvious order, and real data settled it.
            // A MegaSquirt reporting 42.6% duty at 5,976 rpm on a 8.55 ms pulse is
            // reporting 8.55 x 5976 / 1200 to the decimal — the commanded duty,
            // with the dead time still in it. But an injector held open for its
            // opening time flows nothing at all, so that figure over-states the
            // fuel by however much of the pulse was spent getting off the seat:
            // a fifth of it, at that engine speed, on a 1.5 ms injector.
            //
            // The pulse width can have the dead time taken off. A duty cycle
            // cannot, because by then the two are added together.
            double percent = width is not null
                ? Math.Max((width.At(at) * toMs) - engine.InjectorDeadTimeMs, 0) * rpm[i] / divisor
                : ChannelUnits.Fraction(duty!, duty!.At(at)) * 100;

            peakDuty = Math.Max(peakDuty, percent);

            double lbPerHour = engine.InjectorCcPerMinute * (percent / 100) * engine.Cylinders
                               * density * GramsPerMinuteToLbPerHour;

            power[i] = engine.Bsfc > 0 ? lbPerHour / engine.Bsfc * cf : double.NaN;
        }

        var cautions = new List<string>();

        foreach (PullFault fault in pull.Faults)
        {
            if (fault is PullFault.GearNotChecked or PullFault.GearNotRecognised
                or PullFault.RatioDrifted or PullFault.SpeedUnitAmbiguous)
            {
                continue;
            }

            cautions.Add(DynoRun.Describe(fault));
        }

        if (peakDuty > 100)
        {
            cautions.Add(
                $"The injectors work out at {peakDuty:N0}% duty at the top of this pull, which is more "
                + "time than there is. Either they fire twice a cycle rather than once, or the pulse "
                + "width is not in the units it looks like.");
        }
        else if (peakDuty < 25 && !engine.TwoSquirtsPerCycle)
        {
            cautions.Add(
                $"The injectors only reach {peakDuty:N0}% duty at the top of this pull, which is idle "
                + "for an engine at full throttle. Two squirts a cycle would double it — and a "
                + "controller's own duty figure cannot tell you which this is, because MegaSquirt "
                + "reports the one-squirt number either way.");
        }

        if (width is null && duty is not null)
        {
            cautions.Add(
                $"Duty came from \"{duty.Name}\" because this log carries no pulse width. A "
                + "controller's duty figure normally has the dead time inside it, and an injector "
                + "held open for its opening time flows nothing — so this reads high, by about the "
                + "share of each pulse spent getting off the seat.");
        }

        cautions.Add(
            $"{engine.Cylinders} × {engine.InjectorCcPerMinute:N0} cc/min with "
            + $"{engine.InjectorDeadTimeMs:N2} ms of dead time, and a brake specific fuel consumption "
            + $"of {engine.Bsfc:N2} that was assumed. The injector figures are worth measuring on a "
            + "bench: what is written on the box is regularly a third out.");

        return Build(
            rpm, power,
            PowerMethodKind.Injectors,
            $"{engine.Cylinders} × {engine.InjectorCcPerMinute:N0} cc/min, "
            + $"{(engine.TwoSquirtsPerCycle ? "two squirts a cycle" : "one squirt a cycle")}, "
            + $"{engine.InjectorDeadTimeMs:N2} ms dead, {TuningMath.Name(engine.Fuel)}, "
            + $"BSFC {engine.Bsfc:N2} assumed, peak duty {peakDuty:N0}%",
            correction, cf, cautions, s, double.NaN, PowerReference.Crank);
    }

    private static string SpeedDensityBasis(
        EngineSpec engine, LogChannel? mixture, LogChannel? filling, bool gauge, Ambient air) =>
        $"{engine.Litres:N2} L, "
        + (filling is not null ? $"filling from {filling.Name}" : $"filling assumed {engine.VolumetricEfficiency:N0}%")
        + ", "
        + (mixture is not null ? $"mixture from {mixture.Name}" : $"lambda assumed {engine.Lambda:N2}")
        + $", {TuningMath.Name(engine.Fuel)}, BSFC {engine.Bsfc:N2} assumed"
        + (gauge ? ", manifold pressure read as gauge" : "")
        + $", {air}";

    private static string Needed(params (LogChannel? Channel, string Name)[] wanted)
    {
        string[] absent = [.. wanted.Where(w => w.Channel is null).Select(w => w.Name)];

        return absent.Length switch
        {
            0 => "nothing — it should have been offered",
            1 => $"this log has no {absent[0]}",
            _ => "this log has no " + string.Join(", no ", absent[..^1]) + $" and no {absent[^1]}",
        };
    }

    /// <summary>
    /// The general form: engine speed and a power reading per sample, binned into
    /// a curve.
    ///
    /// <para>
    /// The seam every other route to a figure arrives through. Speed density,
    /// the injectors, a mass air flow meter and a controller's own torque model
    /// all produce a power at each sample and differ only in how; from here on
    /// they are drawn, peaked and compared identically, which is what lets two
    /// of them be laid over one another and disagreed with.
    /// </para>
    /// </summary>
    public static DynoCurve Build(
        ReadOnlySpan<double> rpm,
        ReadOnlySpan<double> horsepower,
        PowerMethodKind method,
        string basis,
        PowerCorrection correction,
        double correctionFactor,
        IReadOnlyList<string>? cautions = null,
        DynoSettings? settings = null,
        double aeroShare = double.NaN,
        PowerReference reference = PowerReference.Wheels)
    {
        if (rpm.Length != horsepower.Length)
        {
            throw new ArgumentException(
                "There must be one power reading for each engine speed.", nameof(horsepower));
        }

        DynoSettings s = settings ?? new DynoSettings();

        int step = Math.Max(1, s.RpmStep);

        var totals = new Dictionary<int, (double Sum, int Count)>();

        for (int i = 0; i < rpm.Length; i++)
        {
            double r = rpm[i];
            double hp = horsepower[i];

            if (!double.IsFinite(r) || !double.IsFinite(hp) || r <= 0) continue;

            // To the nearest rung rather than the one below, so a reading is
            // credited to the engine speed it is nearest to.
            int rung = (int)Math.Round(r / step) * step;

            (double sum, int n) = totals.GetValueOrDefault(rung);
            totals[rung] = (sum + hp, n + 1);
        }

        var points = new List<DynoPoint>();
        int samples = 0;
        int thin = 0;

        foreach (int rung in totals.Keys.Order())
        {
            (double sum, int n) = totals[rung];

            if (n < s.MinimumSamplesPerStep) { thin++; continue; }

            double hp = sum / n;

            points.Add(new DynoPoint(rung, hp, RoadLoad.PoundFeet(hp, rung), n));
            samples += n;
        }

        var notes = new List<string>(cautions ?? []);

        // Rungs missing from the middle of a curve, as against the curve simply
        // ending. A pull fast enough through the midrange leaves steps with a
        // single reading in them, and those are dropped rather than drawn.
        if (points.Count > 1)
        {
            int expected = ((int)(points[^1].Rpm - points[0].Rpm) / step) + 1;
            int missing = expected - points.Count;

            // Told apart rather than lumped together, because the two have
            // different remedies. A step nothing landed in wants a wider step or
            // a slower pull; a step that was dropped for holding too few readings
            // wants the threshold lowering, and saying "fewer than 1 readings" —
            // which is what the default setting produced — describes neither.
            if (missing > thin)
            {
                notes.Add(
                    $"{missing - thin} of {expected} steps had no reading land in them and are not "
                    + "drawn. A wider step, or a slower pull, fills them in.");
            }

            if (thin > 0)
            {
                notes.Add(
                    $"{thin} of {expected} steps held fewer than {s.MinimumSamplesPerStep} readings "
                    + "and were dropped.");
            }
        }

        return new DynoCurve
        {
            Points = points,
            Method = method,
            Basis = basis,
            Reference = reference,
            Correction = correction,
            CorrectionFactor = correctionFactor,
            RpmStep = step,
            Samples = samples,
            Cautions = notes,
            AeroShare = aeroShare,
        };
    }

    // ----- odds and ends ---------------------------------------------------------

    private static (int From, int To) Trim(double[] times, double window, double trimWindows)
    {
        if (times.Length == 0) return (0, 0);

        // A window reaches half its length either side of the sample it
        // describes, so the region with support on one side only is half a
        // window at each end — not a quarter of one, which is what dividing by
        // two again would leave and what left the top rung of the curve reading
        // a quarter low.
        double edge = window * trimWindows;

        if (!(edge > 0)) return (0, times.Length);

        double first = times[0] + edge;
        double last = times[^1] - edge;

        int from = 0;
        int to = times.Length;

        while (from < times.Length && times[from] < first) from++;
        while (to > from && times[to - 1] > last) to--;

        // Never trim everything away: a pull barely longer than the window would
        // otherwise come back empty rather than come back rough.
        return to - from >= 4 ? (from, to) : (0, times.Length);
    }

    private static string RoadLoadBasis(VehicleSpec vehicle, DynoPull pull, Ambient air) =>
        $"{vehicle.MassKg:N0} kg in {pull.Gearing.Gear}"
        + $" ({vehicle.MassFactor(pull.Gearing.Gear):N3}× with what is turning)"
        + $", CdA {vehicle.DragAreaM2:N2} and Crr {vehicle.RollingResistance:N4} "
        + (vehicle.RoadLoadMeasured ? "measured" : "as entered")
        + $", {air}"
        + (pull.Speed == SpeedSource.VehicleSpeedChannel
            ? ", road speed from the sensor because the driveline slipped"
            : ", road speed from engine speed and the gearing");

    private static DynoCurve Nothing(PowerMethodKind method, string why) =>
        new()
        {
            Points = [],
            Method = method,
            Basis = $"nothing could be drawn: {why}",
            Reference = PowerReference.Wheels,
            Correction = PowerCorrection.None,
            CorrectionFactor = 1,
            RpmStep = 0,
            Samples = 0,
            Cautions = [why],
        };
}

/// <summary>One rung of the difference between two curves.</summary>
public readonly record struct DynoDelta(double Rpm, double Horsepower, double PoundFeet);

/// <summary>
/// Two curves read against each other.
///
/// <para>
/// The question the whole application exists for: change something, run it
/// again, and find out what moved. Two curves compare far more honestly than two
/// logs do, because a curve is already indexed by engine speed — there is no
/// question of lining up two recordings in time, and no temptation to.
/// </para>
/// <para>
/// It is also the shape of the other comparison worth making, which is between
/// two <em>methods</em> on the same pull rather than two pulls. Where a
/// road-load curve and an injector curve lie on top of one another the figure is
/// worth believing; where they diverge, the shape of the divergence says which
/// input is wrong. That is the same subtraction.
/// </para>
/// </summary>
public sealed record DynoComparison(
    IReadOnlyList<DynoDelta> Steps,
    double PeakPowerChange,
    double PeakTorqueChange,
    string Summary)
{
    public bool Any => Steps.Count > 0;

    /// <summary>
    /// Subtracts the second curve from the first, rung by rung.
    ///
    /// Only where both were drawn: a step one curve is missing is not a change of
    /// zero, and filling it in with one would put a hole in the middle of a
    /// difference and call it agreement.
    /// </summary>
    public static DynoComparison Between(DynoCurve now, DynoCurve before)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(before);

        if (now.Reference != before.Reference && !now.IsEmpty && !before.IsEmpty)
        {
            return new DynoComparison(
                [], double.NaN, double.NaN,
                $"One of these is quoted at the {Where(now.Reference)} and the other at the "
                + $"{Where(before.Reference)}. Subtracting them would credit the driveline to "
                + "whatever changed — restate one of them first.");
        }

        if (now.RpmStep != before.RpmStep && !now.IsEmpty && !before.IsEmpty)
        {
            return new DynoComparison(
                [], double.NaN, double.NaN,
                $"These curves are stepped differently — {now.RpmStep} rpm against "
                + $"{before.RpmStep}. Draw both the same way and they will subtract.");
        }

        Dictionary<double, DynoPoint> older = before.Drawn.ToDictionary(p => p.Rpm);

        List<DynoDelta> steps = [];

        foreach (DynoPoint p in now.Drawn)
        {
            if (!older.TryGetValue(p.Rpm, out DynoPoint was)) continue;

            steps.Add(new DynoDelta(
                p.Rpm, p.Horsepower - was.Horsepower, p.PoundFeet - was.PoundFeet));
        }

        double peakPower = now.PeakPower.Horsepower - before.PeakPower.Horsepower;
        double peakTorque = now.PeakTorque.PoundFeet - before.PeakTorque.PoundFeet;

        return new DynoComparison(steps, peakPower, peakTorque, Describe(steps, peakPower, peakTorque));
    }

    private static string Where(PowerReference reference) =>
        reference == PowerReference.Crank ? "crank" : "wheels";

    /// <summary>The rung where the two differ most, which is where to go looking.</summary>
    public DynoDelta BiggestChange
    {
        get
        {
            DynoDelta best = default;

            foreach (DynoDelta d in Steps)
            {
                if (Math.Abs(d.Horsepower) > Math.Abs(best.Horsepower)) best = d;
            }

            return best;
        }
    }

    private static string Describe(
        IReadOnlyList<DynoDelta> steps, double peakPower, double peakTorque)
    {
        if (steps.Count == 0)
        {
            return "These two curves share no engine speeds, so there is nothing to compare.";
        }

        string peaks =
            $"{peakPower:+0.0;-0.0;0.0} hp at the peak, {peakTorque:+0.0;-0.0;0.0} lb-ft";

        // Where a change is broad it is the engine; where it is narrow it is a
        // moment, and worth pointing at rather than averaging away.
        DynoDelta biggest = steps.Aggregate((a, b) =>
            Math.Abs(b.Horsepower) > Math.Abs(a.Horsepower) ? b : a);

        return $"{peaks}. The largest single difference is {biggest.Horsepower:+0.0;-0.0;0.0} hp "
               + $"at {biggest.Rpm:N0} rpm, over {steps.Count} steps in common.";
    }
}
