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

    public required PowerCorrection Correction { get; init; }

    public required double CorrectionFactor { get; init; }

    public required int RpmStep { get; init; }

    /// <summary>Readings that went into the whole curve, after trimming.</summary>
    public required int Samples { get; init; }

    public required IReadOnlyList<string> Cautions { get; init; }

    public IEnumerable<DynoPoint> Drawn => Points.Where(p => !p.IsEmpty);

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

        for (int i = 0; i < count; i++)
        {
            power[i] = RoadLoad.Horsepower(
                vehicle, gear, motion.Value[i], motion.SlopePerSecond[i], density) * cf;
        }

        (int from, int to) = Trim(times, s);

        var cautions = new List<string>();

        foreach (PullFault fault in pull.Faults) cautions.Add(DynoRun.Describe(fault));

        if (!vehicle.RoadLoadMeasured)
        {
            cautions.Add(
                "The drag area and rolling resistance were taken from the vehicle as entered rather "
                + "than measured. A coastdown replaces both, and they are the largest guess in this "
                + "figure.");
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
            correction, cf, cautions, s);
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
        DynoSettings? settings = null)
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
            Correction = correction,
            CorrectionFactor = correctionFactor,
            RpmStep = step,
            Samples = samples,
            Cautions = notes,
        };
    }

    // ----- odds and ends ---------------------------------------------------------

    private static (int From, int To) Trim(double[] times, DynoSettings s)
    {
        if (times.Length == 0) return (0, 0);

        // A window reaches half its length either side of the sample it
        // describes, so the region with support on one side only is half a
        // window at each end — not a quarter of one, which is what dividing by
        // two again would leave and what left the top rung of the curve reading
        // a quarter low.
        double edge = s.WindowSeconds * s.TrimWindows;

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
