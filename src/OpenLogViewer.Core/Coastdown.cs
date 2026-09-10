namespace OpenLogViewer.Core;

/// <summary>One stretch of a log where the car was slowing with nothing driving it.</summary>
public sealed record CoastdownRun
{
    public required int First { get; init; }

    public required int Last { get; init; }

    public required double StartSeconds { get; init; }

    public required double EndSeconds { get; init; }

    /// <summary>Speed at the start, in metres a second — the faster end.</summary>
    public required double FromSpeedMs { get; init; }

    public required double ToSpeedMs { get; init; }

    /// <summary>
    /// The gear it was rolling in, if it was in one at all.
    ///
    /// Zero means the ratio matched no gear, which on a decelerating car is what
    /// neutral looks like and is what a coastdown has to be.
    /// </summary>
    public required int Gear { get; init; }

    public bool InGear => Gear > 0;

    public double Seconds => EndSeconds - StartSeconds;

    /// <summary>
    /// How much of a spread in speed squared the run covers.
    ///
    /// The number that decides whether the two coefficients can be told apart at
    /// all. Rolling resistance is flat with speed and drag grows with its square,
    /// so separating them means watching the total change as the square changes.
    /// A run from sixty to fifty covers a spread of 1.4 and the fit has almost
    /// nothing to work with; one from eighty to thirty covers 7 and it has
    /// plenty.
    /// </summary>
    public double SpeedSquaredSpread =>
        ToSpeedMs > 0 ? (FromSpeedMs * FromSpeedMs) / (ToSpeedMs * ToSpeedMs) : double.NaN;

    public override string ToString() =>
        $"{StartSeconds:N1} – {EndSeconds:N1} s · "
        + $"{FromSpeedMs * 3.6:N0} – {ToSpeedMs * 3.6:N0} km/h · "
        + (InGear ? $"in {Gear}" : "neutral");
}

/// <summary>What a coastdown measured.</summary>
public sealed record CoastdownFit
{
    /// <summary>Drag coefficient times frontal area, in square metres.</summary>
    public required double DragAreaM2 { get; init; }

    /// <summary>Rolling resistance coefficient.</summary>
    public required double RollingResistance { get; init; }

    /// <summary>
    /// The gradient of the road it was measured on, in per cent, in the direction
    /// of the first run.
    ///
    /// Only knowable from a pair of runs in opposite directions, and NaN
    /// otherwise. This describes the <em>road</em>, not the car — it is not
    /// carried into the vehicle by <see cref="ApplyTo"/>, because a figure
    /// measured on that stretch says nothing about wherever the next pull
    /// happens.
    /// </summary>
    public required double GradePercent { get; init; }

    /// <summary>
    /// How much of the deceleration the straight line accounts for, nought to
    /// one.
    ///
    /// Not a measure of whether the answer is right, only of whether the data was
    /// consistent. A run down a steady hill fits beautifully and gives a rolling
    /// resistance that is mostly gravity.
    /// </summary>
    public required double Agreement { get; init; }

    public required int Samples { get; init; }

    public required bool BothDirections { get; init; }

    /// <summary>Everything worth knowing before the figures are used.</summary>
    public required IReadOnlyList<string> Cautions { get; init; }

    public bool Usable => DragAreaM2 > 0 && RollingResistance > 0;

    /// <summary>
    /// The vehicle with its two guessed coefficients replaced by these measured
    /// ones.
    ///
    /// The gradient is deliberately not carried over. It was a property of the
    /// road the coastdown happened on, and applying it to a pull made somewhere
    /// else would subtract a hill that is not there.
    /// </summary>
    public VehicleSpec ApplyTo(VehicleSpec vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (!Usable) return vehicle;

        return vehicle with
        {
            DragAreaM2 = DragAreaM2,
            RollingResistance = RollingResistance,
        };
    }

    public override string ToString() =>
        $"CdA {DragAreaM2:N3} m², Crr {RollingResistance:N4}"
        + (double.IsFinite(GradePercent) ? $", road {GradePercent:+0.00;-0.00;0.00}%" : "")
        + $" — {Samples} samples, agreement {Agreement:P0}";
}

/// <summary>What counts as a coastdown.</summary>
public sealed record CoastdownSettings
{
    /// <summary>Shortest run worth fitting.</summary>
    public double MinimumSeconds { get; init; } = 4;

    /// <summary>
    /// Slowest speed to take readings at, in metres a second.
    /// </summary>
    public double MinimumSpeedMs { get; init; } = 8;

    /// <summary>
    /// Least spread in speed squared before the two coefficients are considered
    /// separable. See <see cref="CoastdownRun.SpeedSquaredSpread"/>.
    /// </summary>
    public double MinimumSpeedSquaredSpread { get; init; } = 2.0;

    /// <summary>
    /// How hard the car has to be slowing before it counts as coasting rather
    /// than cruising, in metres per second squared.
    /// </summary>
    public double MinimumDecelerationMs2 { get; init; } = 0.15;

    /// <summary>
    /// How far up its own range the throttle may be and still be shut.
    ///
    /// A fraction of the channel's observed range rather than an absolute
    /// figure, so it works on a percentage, on a fraction of one, and on a
    /// channel with an offset in it.
    /// </summary>
    public double ClosedThrottleFraction { get; init; } = 0.05;

    /// <summary>
    /// The window the speed is differentiated over.
    ///
    /// Longer than a pull's, because a coastdown has no features to preserve. It
    /// is one smooth curve, and the only thing a longer window costs is the two
    /// ends.
    /// </summary>
    public double WindowSeconds { get; init; } = 1.0;

    /// <summary>How near a gear the ratio has to be before the car is judged to be in it.</summary>
    public double GearTolerancePercent { get; init; } = 6;

    /// <summary>
    /// How steady the ratio of engine speed to road speed has to be before the
    /// car is taken to be in gear at all.
    ///
    /// The primary test, ahead of which gear it matches. See the remarks on the
    /// gear check itself.
    /// </summary>
    public double RatioTolerancePercent { get; init; } = 2.5;
}

/// <summary>Every coastdown in a log, and why the rest of it is not one.</summary>
public sealed record CoastdownSearchResult(IReadOnlyList<CoastdownRun> Runs, string Summary)
{
    /// <summary>The ones that can actually be fitted.</summary>
    public IEnumerable<CoastdownRun> Neutral => Runs.Where(r => !r.InGear);
}

/// <summary>
/// Measuring what the road and the air cost, instead of guessing at them.
///
/// <para>
/// <see cref="VehicleSpec"/> carries five numbers about the car. Three are
/// nearly exact — the mass from a weighbridge, the inertias from a catalogue.
/// The other two, the drag area and the rolling resistance, are looked up in a
/// table of vaguely similar cars and are the largest uncertainty in the whole
/// dyno. This is how to stop guessing at them: put the car in neutral at speed,
/// take your feet off everything, and let it slow down.
/// </para>
/// <para>
/// With nothing driving it, everything slowing the car is road load, and the two
/// terms separate themselves because they depend on speed differently. Rolling
/// resistance is flat; drag grows with the square. So the deceleration force
/// plotted against speed squared is a straight line, its intercept is the rolling
/// resistance and its slope is the drag area. That is the whole method.
/// </para>
/// <para>
/// <b>It has to be done in both directions.</b> A road that looks flat is not,
/// and one per cent of gradient on an ordinary car is about a sixth of a
/// kilonewton — the same order as the entire rolling resistance. Measured one
/// way, gravity is indistinguishable from the tyres and lands in the intercept
/// wholesale. Measured both ways it reverses sign while the tyres do not, so
/// averaging the two intercepts gives the rolling resistance and half their
/// difference gives the hill. The hill is worth having as an output: it says
/// whether the stretch was worth using.
/// </para>
/// <para>
/// Wind does the same thing to the other term and is cancelled the same way, but
/// only to first order. A headwind adds to the air the car meets and a tailwind
/// subtracts, so the cross term reverses and averages out; what survives is the
/// square of the wind speed, which for a light breeze is small and for anything
/// more is a reason to come back another day.
/// </para>
/// <para>
/// <b>Neutral, not just off the throttle.</b> Coasting in gear drags the engine
/// round, and engine braking is far larger than either coefficient being
/// measured. It would land almost entirely in the rolling resistance and produce
/// a figure several times too big. Whether the car was in gear is not taken on
/// trust: engine speed over road speed is checked against the gearbox exactly as
/// it is for a pull, and a run that matches a gear is reported as being in it.
/// </para>
/// </summary>
public static class Coastdown
{
    /// <summary>
    /// Every stretch of the log where the car was coasting.
    ///
    /// <paramref name="speedToMetresPerSecond"/> overrides what the road speed
    /// channel says it is in. Needed because a coasting car cannot have its speed
    /// units worked out from the gearing the way a pull can — in neutral the
    /// ratio matches nothing by design. Where a log contains a pull as well,
    /// <see cref="DynoRun.InferGear"/> identifies the unit from that and it can be
    /// passed in here.
    /// </summary>
    public static CoastdownSearchResult Find(
        LogDocument log,
        VehicleSpec vehicle,
        CoastdownSettings? settings = null,
        double speedToMetresPerSecond = double.NaN)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(vehicle);

        CoastdownSettings s = settings ?? new CoastdownSettings();

        LogChannel? road = ChannelRoles.Find(log, ChannelRole.VehicleSpeed);

        if (road is null)
        {
            return new CoastdownSearchResult(
                [],
                "A coastdown is measured from road speed, and this log has no road speed channel. "
                + "Engine speed cannot stand in for it: the whole point is that the engine is "
                + "disconnected.");
        }

        double factor = double.IsFinite(speedToMetresPerSecond)
            ? speedToMetresPerSecond
            : ChannelUnits.SpeedToMetresPerSecond(road);

        if (!(factor > 0))
        {
            return new CoastdownSearchResult(
                [],
                $"The road speed channel \"{road.Name}\" does not say what it is in, and a coasting "
                + "car gives nothing to work it out from — in neutral the ratio to engine speed "
                + "matches no gear on purpose. A pull elsewhere in the same log identifies the "
                + "unit, or it can be stated outright.");
        }

        double interval = log.MedianSampleInterval;

        if (interval > 0 && interval * 2 >= s.WindowSeconds)
        {
            return new CoastdownSearchResult(
                [],
                $"This log is sampled about {1 / interval:N0} times a second, which is too slow to "
                + $"differentiate over a {s.WindowSeconds:N2} s window.");
        }

        double[] speeds = new double[log.SampleCount];

        for (int i = 0; i < speeds.Length; i++) speeds[i] = road.At(i) * factor;

        ChannelFit motion = RateOfChange.Fit(
            speeds, Times(log), s.WindowSeconds, log.GapThreshold);

        LogChannel? throttle = ChannelRoles.Find(log, ChannelRole.Throttle);
        LogChannel? rpm = ChannelRoles.Find(log, ChannelRole.EngineSpeed);

        double shut = ClosedThrottleLevel(throttle, s);

        List<CoastdownRun> runs = [];
        int tooShort = 0;
        int tooNarrow = 0;

        foreach ((int first, int last) in CoastingRuns(log, motion, speeds, throttle, shut, s))
        {
            double seconds = log.Time.At(last) - log.Time.At(first);

            if (seconds < s.MinimumSeconds) { tooShort++; continue; }

            var run = new CoastdownRun
            {
                First = first,
                Last = last,
                StartSeconds = log.Time.At(first),
                EndSeconds = log.Time.At(last),
                FromSpeedMs = motion.Value[first],
                ToSpeedMs = motion.Value[last],
                Gear = GearOf(vehicle, rpm, speeds, first, last, s),
            };

            if (run.SpeedSquaredSpread < s.MinimumSpeedSquaredSpread) { tooNarrow++; continue; }

            runs.Add(run);
        }

        return new CoastdownSearchResult(runs, Summarise(runs, tooShort, tooNarrow, s));
    }

    // ----- the fit ---------------------------------------------------------------

    /// <summary>
    /// One run, fitted.
    ///
    /// Everything the road and the air did lands in two numbers, and with only
    /// one direction there is no way to tell the tyres from the hill — so the
    /// rolling resistance that comes back has whatever gradient there was folded
    /// into it, and says so.
    /// </summary>
    public static CoastdownFit Fit(
        LogDocument log, VehicleSpec vehicle, Ambient air, CoastdownRun run,
        CoastdownSettings? settings = null, double speedToMetresPerSecond = double.NaN)
    {
        ArgumentNullException.ThrowIfNull(run);

        Line line = Regress(log, vehicle, run, settings, speedToMetresPerSecond, out int samples);

        double rho = AirDensity.KgPerCubicMetre(air);
        double weight = vehicle.MassKg * RoadLoad.Gravity;

        List<string> cautions = [SingleDirection];

        AddCommonCautions(cautions, run, line, samples);

        return new CoastdownFit
        {
            DragAreaM2 = rho > 0 ? 2 * line.Slope / rho : double.NaN,
            RollingResistance = weight > 0 ? line.Intercept / weight : double.NaN,
            GradePercent = double.NaN,
            Agreement = line.Agreement,
            Samples = samples,
            BothDirections = false,
            Cautions = cautions,
        };
    }

    /// <summary>
    /// Two fits from runs in opposite directions, combined into one answer.
    ///
    /// <para>
    /// The way it should be done. Each run on its own reports a rolling
    /// resistance holding the tyres and the hill added together, but the hill
    /// reverses sign between the two and the tyres do not — so their average is
    /// the rolling resistance and half their difference is the gradient,
    /// reported in the direction <paramref name="outward"/> was travelling.
    /// </para>
    /// <para>
    /// The two drag areas are averaged, which does the same thing to a steady
    /// wind. A headwind adds to the air the car meets and a tailwind subtracts,
    /// so the term linear in the wind cancels; the term in its square does not,
    /// and is the reason to do this on a still day.
    /// </para>
    /// <para>
    /// It takes two finished fits rather than two runs because the pair are very
    /// often not in the same recording — a coast one way, a turn round, a coast
    /// back, and quite possibly the logger stopped in between. Nothing about the
    /// combination needs the samples again.
    /// </para>
    /// </summary>
    public static CoastdownFit Combine(CoastdownFit outward, CoastdownFit back)
    {
        ArgumentNullException.ThrowIfNull(outward);
        ArgumentNullException.ThrowIfNull(back);

        // Each fit already divided its intercept by the car's weight, so the
        // averaging and the difference can both be done in the reported figures.
        double rolling = (outward.RollingResistance + back.RollingResistance) / 2;
        double sine = (outward.RollingResistance - back.RollingResistance) / 2;

        // As a gradient rather than as an angle, since that is how a road is
        // described and how VehicleSpec asks for it.
        double grade = Math.Abs(sine) < 1 ? Math.Tan(Math.Asin(sine)) * 100 : double.NaN;

        List<string> cautions =
        [
            .. outward.Cautions.Where(c => c != SingleDirection),
            .. back.Cautions.Where(c => c != SingleDirection),
        ];

        if (Math.Abs(grade) > 1)
        {
            cautions.Insert(0,
                $"The road ran {Math.Abs(grade):N1}% uphill or down over this stretch, enough that a "
                + "single-direction run on it would have put more gravity than tyre into the rolling "
                + "resistance. Both directions were used, so it has been taken back out.");
        }

        return new CoastdownFit
        {
            DragAreaM2 = (outward.DragAreaM2 + back.DragAreaM2) / 2,
            RollingResistance = rolling,
            GradePercent = grade,
            Agreement = Math.Min(outward.Agreement, back.Agreement),
            Samples = outward.Samples + back.Samples,
            BothDirections = true,
            Cautions = cautions,
        };
    }

    /// <summary>
    /// Said of every single-direction fit, and taken back out by
    /// <see cref="Combine"/>. A constant so the two cannot drift apart.
    /// </summary>
    private const string SingleDirection =
        "Measured in one direction, so any gradient in the road is inside the rolling "
        + "resistance rather than separated from it. A run back the other way splits them.";

    /// <summary>The straight line the deceleration force makes against speed squared.</summary>
    /// <param name="Intercept">The force at a standstill: tyres, and any hill.</param>
    /// <param name="Slope">Force per unit of speed squared — half the air density times the drag area.</param>
    /// <param name="Agreement">How much of the scatter the line accounts for.</param>
    private readonly record struct Line(double Intercept, double Slope, double Agreement);

    private static Line Regress(
        LogDocument log, VehicleSpec vehicle, CoastdownRun run,
        CoastdownSettings? settings, double factorOverride, out int samples)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(vehicle);

        CoastdownSettings s = settings ?? new CoastdownSettings();

        LogChannel? road = ChannelRoles.Find(log, ChannelRole.VehicleSpeed);

        samples = 0;

        if (road is null) return new Line(double.NaN, double.NaN, double.NaN);

        double factor = double.IsFinite(factorOverride)
            ? factorOverride
            : ChannelUnits.SpeedToMetresPerSecond(road);

        if (!(factor > 0)) return new Line(double.NaN, double.NaN, double.NaN);

        double[] speeds = new double[log.SampleCount];

        for (int i = 0; i < speeds.Length; i++) speeds[i] = road.At(i) * factor;

        ChannelFit motion = RateOfChange.Fit(speeds, Times(log), s.WindowSeconds, log.GapThreshold);

        // Neutral: the engine is disconnected and its inertia is genuinely not
        // being decelerated along with the car. Using a gear's effective mass
        // here would overstate every force by the whole of the engine term.
        double mass = vehicle.EffectiveMassKg(VehicleSpec.Neutral);

        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;

        for (int i = run.First; i <= run.Last; i++)
        {
            double v = motion.Value[i];
            double a = motion.SlopePerSecond[i];

            if (!double.IsFinite(v) || !double.IsFinite(a) || v < s.MinimumSpeedMs) continue;

            // The force resisting the car, taken as positive while it slows.
            double x = v * v;
            double y = -mass * a;

            n++;
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
            syy += y * y;
        }

        samples = (int)n;

        double denominator = (n * sxx) - (sx * sx);

        if (n < 3 || Math.Abs(denominator) < 1e-9) return new Line(double.NaN, double.NaN, double.NaN);

        double slope = ((n * sxy) - (sx * sy)) / denominator;
        double intercept = (sy - (slope * sx)) / n;

        double totalScatter = syy - (sy * sy / n);
        double residual = syy - (intercept * sy) - (slope * sxy);
        double agreement = totalScatter > 0 ? 1 - (residual / totalScatter) : double.NaN;

        return new Line(intercept, slope, agreement);
    }

    private static void AddCommonCautions(List<string> cautions, CoastdownRun run, Line line, int samples)
    {
        if (run.InGear)
        {
            cautions.Add(
                $"This run was in {run.Gear}, not neutral. Engine braking is far larger than either "
                + "coefficient being measured and lands almost entirely in the rolling resistance, "
                + "so the figures are not usable.");
        }

        if (line.Slope < 0)
        {
            cautions.Add(
                "The drag came out negative, which nothing can be. Something other than the road was "
                + "slowing the car — a touch of brake, a change of gradient part way, or a gust.");
        }

        if (line.Intercept < 0)
        {
            cautions.Add(
                "The rolling resistance came out negative, which usually means the run was downhill "
                + "steeply enough for gravity to outweigh the tyres.");
        }

        if (double.IsFinite(line.Agreement) && line.Agreement < 0.8)
        {
            cautions.Add(
                $"Only {line.Agreement:P0} of the deceleration follows a straight line against speed "
                + "squared. A clean coast is well over ninety; the rest is braking, gusting, or a "
                + "road that changes slope.");
        }

        if (samples < 30)
        {
            cautions.Add($"Only {samples} readings went into this run, which is thin for a fit.");
        }
    }

    // ----- finding the runs -------------------------------------------------------

    private static IEnumerable<(int First, int Last)> CoastingRuns(
        LogDocument log, ChannelFit motion, double[] speeds,
        LogChannel? throttle, double shut, CoastdownSettings s)
    {
        double gap = log.GapThreshold;
        int start = -1;

        for (int i = 0; i < log.SampleCount; i++)
        {
            bool coasting =
                motion.SlopePerSecond[i] <= -s.MinimumDecelerationMs2
                && double.IsFinite(speeds[i])
                && speeds[i] >= s.MinimumSpeedMs
                && (double.IsNaN(shut) || IsShut(throttle!, i, shut));

            bool broken = i > 0 && gap > 0 && log.Time.At(i) - log.Time.At(i - 1) > gap;

            if (coasting && !broken)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0 && i - 1 > start) yield return (start, i - 1);

            start = coasting ? i : -1;
        }

        if (start >= 0 && log.SampleCount - 1 > start) yield return (start, log.SampleCount - 1);
    }

    private static bool IsShut(LogChannel throttle, int i, double shut)
    {
        double v = throttle.At(i);

        return !double.IsFinite(v) || v <= shut;
    }

    /// <summary>
    /// Where the bottom of the throttle's travel is, on this log.
    ///
    /// Taken as a fraction of the channel's own range rather than as an absolute
    /// figure, so it works on a percentage, on a fraction of one, and on a
    /// channel that never quite reads zero because the sensor has an offset.
    /// </summary>
    private static double ClosedThrottleLevel(LogChannel? throttle, CoastdownSettings s)
    {
        if (throttle is null || throttle.IsFlat) return double.NaN;

        return throttle.Min + ((throttle.Max - throttle.Min) * s.ClosedThrottleFraction);
    }

    /// <summary>
    /// Which gear the car was rolling in, or zero for none — which is what
    /// neutral looks like.
    ///
    /// Worked out here rather than through <see cref="DynoRun.InferGear"/>
    /// because that one searches the plausible speed units for whichever makes a
    /// gear fit, and a coasting car has no gear to fit. Handed an unlabelled
    /// channel it would try five units against a ratio that matches none of
    /// them, and the nearest miss would decide the answer.
    /// </summary>
    private static int GearOf(
        VehicleSpec vehicle, LogChannel? rpm, double[] speeds, int first, int last,
        CoastdownSettings s)
    {
        if (rpm is null) return 0;

        List<double> ratios = [];

        for (int i = first; i <= last; i++)
        {
            double r = rpm.At(i);
            double v = speeds[i];

            if (!double.IsFinite(r) || !double.IsFinite(v) || v < 3 || r <= 0) continue;

            ratios.Add(r / v);
        }

        if (ratios.Count < 3) return 0;

        ratios.Sort();

        double median = ratios[ratios.Count / 2];

        if (!(median > 0)) return 0;

        // Whether the ratio held, before whether it matched anything.
        //
        // In gear the wheels are turning the engine, so the ratio is as constant
        // as it is during a pull. In neutral the engine sits at idle while the
        // car slows, so the ratio climbs the whole way down — from about 27 rpm
        // per metre a second at motorway speed to nearer 80 by the end of the
        // run. That sweep is the signal, and it is much the stronger one:
        // matching on the value alone, a slow enough coast passes through the
        // ratio of the tallest gear on its way past and would be reported as
        // being in it.
        double spread = (ratios[(int)(ratios.Count * 0.9)] - ratios[(int)(ratios.Count * 0.1)])
                        / median * 100;

        if (spread > s.RatioTolerancePercent) return 0;

        for (int g = 1; g <= vehicle.GearRatios.Count; g++)
        {
            double expected = vehicle.RpmFromSpeedMs(1, g);

            if (!(expected > 0)) continue;

            if (Math.Abs(median - expected) / expected * 100 <= s.GearTolerancePercent) return g;
        }

        return 0;
    }

    private static double[] Times(LogDocument log)
    {
        var times = new double[log.SampleCount];

        for (int i = 0; i < times.Length; i++) times[i] = log.Time.At(i);

        return times;
    }

    private static string Summarise(
        IReadOnlyList<CoastdownRun> runs, int tooShort, int tooNarrow, CoastdownSettings s)
    {
        if (runs.Count > 0)
        {
            int neutral = runs.Count(r => !r.InGear);

            string found = runs.Count == 1 ? "One coast" : $"{runs.Count} coasts";

            return neutral == runs.Count
                ? $"{found} found, all in neutral."
                + (neutral >= 2
                    ? " Fit two of them in opposite directions to separate the tyres from the road."
                    : " A second one back the other way would separate the tyres from the road.")
                : $"{found} found, {neutral} of them in neutral. The rest were in gear, where engine "
                  + "braking swamps what is being measured.";
        }

        List<string> reasons = [];

        if (tooShort > 0) reasons.Add($"{tooShort} were shorter than {s.MinimumSeconds:N0} s");

        if (tooNarrow > 0)
        {
            reasons.Add(
                $"{tooNarrow} covered too little speed to separate rolling resistance from drag");
        }

        return reasons.Count == 0
            ? "Nothing in this log is a coast: the car never slowed steadily with the throttle shut."
            : "No usable coast: " + string.Join("; ", reasons)
              + ". Coast from as high a speed as you can down to a low one, in neutral, off "
              + "everything.";
    }
}
