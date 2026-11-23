namespace OpenLogViewer.Core;

/// <summary>One upshift, and the ratio step it measured.</summary>
/// <param name="Seconds">When it started.</param>
/// <param name="FromRpm">Engine speed as the clutch went down.</param>
/// <param name="ToRpm">Engine speed as it came back up.</param>
/// <param name="Took">How long the shift took.</param>
public readonly record struct ShiftStep(double Seconds, double FromRpm, double ToRpm, double Took)
{
    /// <summary>
    /// The ratio of the gear taken to the gear left, which is what the two engine
    /// speeds are in proportion to.
    /// </summary>
    public double Ratio => FromRpm > 0 ? ToRpm / FromRpm : double.NaN;
}

/// <summary>What the shifts in a log say about the gearbox that was entered.</summary>
public sealed record GearboxCheck
{
    public required IReadOnlyList<ShiftStep> Shifts { get; init; }

    /// <summary>The distinct ratio steps the shifts fell into, smallest first.</summary>
    public required IReadOnlyList<double> MeasuredSteps { get; init; }

    /// <summary>
    /// How far the worst measured step sits from the nearest step the entered
    /// gearbox can make, as a fraction.
    /// </summary>
    public required double WorstMismatch { get; init; }

    public required string Summary { get; init; }

    /// <summary>
    /// Whether the gearbox entered can account for every step that was measured.
    /// </summary>
    public bool Consistent => double.IsFinite(WorstMismatch) && WorstMismatch <= Gearshifts.Tolerance;

    public bool Measured => MeasuredSteps.Count > 0;

    public override string ToString() => Summary;
}

/// <summary>What counts as a shift.</summary>
public sealed record ShiftSettings
{
    /// <summary>How fast engine speed has to be falling, in rpm a second.</summary>
    public double FallingRpmPerSecond { get; init; } = 1200;

    /// <summary>Below this the car may be stopping rather than shifting.</summary>
    public double MinimumRpm { get; init; } = 2600;

    /// <summary>Shortest and longest a shift may take.</summary>
    public double MinimumSeconds { get; init; } = 0.15;

    public double MaximumSeconds { get; init; } = 0.75;

    /// <summary>The band of ratio steps a gearbox plausibly makes.</summary>
    public double SmallestStep { get; init; } = 0.50;

    public double LargestStep { get; init; } = 0.88;

    /// <summary>
    /// How far open the throttle has to come afterwards before the shift counts.
    ///
    /// A driver who changes gear and then coasts leaves engine speed falling past
    /// the moment the clutch came home, and the step measures larger than the
    /// gears are. Requiring the throttle back keeps the measurement to shifts
    /// where the engine was caught by the road rather than left to sink.
    /// </summary>
    public double ThrottleBackPercent { get; init; } = 20;

    /// <summary>The window engine speed is differentiated over to find the fall.</summary>
    public double WindowSeconds { get; init; } = 0.35;

    /// <summary>How near two shifts have to be to count as the same gear pair.</summary>
    public double SameStepWithin { get; init; } = 0.03;
}

/// <summary>
/// Reading the gearbox off the gearshifts.
///
/// <para>
/// A shift is the one moment a log states a gear ratio outright. The car's road
/// speed does not change while the clutch is down, so engine speed before and
/// after an upshift stands in exactly the ratio of the two gears — whatever the
/// tyres are, whatever the differential is, and with no road speed sensor
/// anywhere. On a car that has never had one wired, this is the only thing in the
/// recording that can be checked against the gearing somebody typed in.
/// </para>
/// <para>
/// It measures the <em>steps</em> and not the ratios. Nothing here can say
/// whether top gear is direct or an overdrive, because both look the same from
/// the inside; what it can say is whether the box entered makes steps of the
/// sizes that were driven. That is enough to catch the mistake worth catching. A
/// dogleg five-speed and an overdrive five-speed put quite different gaps between
/// their gears, and on twenty real logs the steps measured explain one to eight
/// per cent and the other to twenty-one — which is the difference between a
/// horsepower figure and a number.
/// </para>
/// <para>
/// <b>The measurement runs a little low.</b> Engine speed is caught at the bottom
/// of its fall, and at fifteen samples a second the clutch comes home somewhere
/// inside the last sample of that fall, by which time the engine has dropped a
/// further few per cent on its own. Nothing here corrects for it — a correction
/// would be a guess dressed as arithmetic — so the tolerance below is wide enough
/// to absorb it, and the bias is always in the same direction, which is worth
/// knowing when a measured step sits just under a real one.
/// </para>
/// </summary>
public static class Gearshifts
{
    /// <summary>
    /// How far a measured step may sit from a real one and still be that one.
    ///
    /// Ten per cent, which sounds slack and is not. It has to cover the low bias
    /// described above, a driver who is quick on one shift and slow on the next,
    /// and engine speed sampled fifteen times a second through an event lasting
    /// half of one. What it does not have to cover is the difference between one
    /// gearbox and another, which is larger.
    /// </summary>
    public const double Tolerance = 0.10;

    /// <summary>
    /// Every upshift in the log that was driven briskly enough to measure.
    /// </summary>
    public static IReadOnlyList<ShiftStep> Find(LogDocument log, ShiftSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        ShiftSettings s = settings ?? new ShiftSettings();

        LogChannel? rpm = ChannelRoles.Find(log, ChannelRole.EngineSpeed);

        if (rpm is null || log.SampleCount < 8) return [];

        LogChannel? throttle = ChannelRoles.Find(log, ChannelRole.Throttle);

        ChannelFit fit = RateOfChange.Fit(log, rpm, s.WindowSeconds);

        List<ShiftStep> shifts = [];

        int i = 1;

        while (i < log.SampleCount - 2)
        {
            if (fit.SlopePerSecond[i] > -s.FallingRpmPerSecond || rpm.At(i) < s.MinimumRpm)
            {
                i++;
                continue;
            }

            // Out to where the fall began and where it stopped. A shallow slope
            // either side is the engine settling rather than the clutch.
            int start = i;
            while (start > 0 && fit.SlopePerSecond[start - 1] < -100) start--;

            int end = i;
            while (end < log.SampleCount - 2 && fit.SlopePerSecond[end + 1] < -100) end++;

            var step = new ShiftStep(
                log.Time.At(start), rpm.At(start), rpm.At(end),
                log.Time.At(end) - log.Time.At(start));

            bool brisk = step.Took >= s.MinimumSeconds && step.Took <= s.MaximumSeconds;
            bool gearSized = step.Ratio > s.SmallestStep && step.Ratio < s.LargestStep;

            bool caught = throttle is null
                          || (end + 3 < log.SampleCount
                              && throttle.At(end + 3) > s.ThrottleBackPercent);

            if (brisk && gearSized && caught) shifts.Add(step);

            i = end + 1;
        }

        return shifts;
    }

    /// <summary>
    /// Whether the gearbox the vehicle declares can account for the shifts driven.
    /// </summary>
    public static GearboxCheck Check(
        LogDocument log, VehicleSpec vehicle, ShiftSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(vehicle);

        ShiftSettings s = settings ?? new ShiftSettings();

        IReadOnlyList<ShiftStep> shifts = Find(log, s);

        IReadOnlyList<double> measured = Cluster(shifts, s);

        if (measured.Count == 0)
        {
            return new GearboxCheck
            {
                Shifts = shifts,
                MeasuredSteps = [],
                WorstMismatch = double.NaN,
                Summary = shifts.Count == 0
                    ? "No gearshift in this log was driven briskly enough to measure a ratio from. "
                      + "A shift where the throttle comes straight back is the one thing a log says "
                      + "about the gearbox without a road speed sensor."
                    : $"{shifts.Count} shift(s) found, but no two agreed closely enough to call a "
                      + "gear ratio from.",
            };
        }

        double[] steps = Steps(vehicle);

        if (steps.Length == 0)
        {
            return new GearboxCheck
            {
                Shifts = shifts,
                MeasuredSteps = measured,
                WorstMismatch = double.NaN,
                Summary = "This vehicle declares fewer than two gears, so there is nothing to check "
                          + "the shifts against.",
            };
        }

        double worst = 0;

        foreach (double m in measured)
        {
            double nearest = steps.MinBy(x => Math.Abs(x - m));

            worst = Math.Max(worst, Math.Abs(nearest - m) / m);
        }

        string list = string.Join(", ", measured.Select(m => m.ToString("N3")));

        return new GearboxCheck
        {
            Shifts = shifts,
            MeasuredSteps = measured,
            WorstMismatch = worst,
            Summary = worst <= Tolerance
                ? $"The gearbox entered accounts for every shift driven here. {measured.Count} "
                  + $"distinct step(s) measured — {list} — from {shifts.Count} shifts, the worst "
                  + $"{worst:P0} from a ratio this box can make."
                : $"The gearbox entered does not account for the shifts driven here. The steps "
                  + $"measured are {list}, and the nearest this box can make is {worst:P0} away — "
                  + "far enough that the ratios are probably not this car's. Every power figure "
                  + "worked out from a gear rests on them.",
        };
    }

    /// <summary>The ratio steps a gearbox can make, adjacent gears only.</summary>
    private static double[] Steps(VehicleSpec vehicle)
    {
        List<double> steps = [];

        for (int g = 2; g <= vehicle.GearRatios.Count; g++)
        {
            double here = vehicle.RatioOf(g);
            double before = vehicle.RatioOf(g - 1);

            if (here > 0 && before > 0) steps.Add(here / before);
        }

        return [.. steps];
    }

    /// <summary>
    /// The distinct ratios among the shifts, taking each cluster's median.
    ///
    /// Two shifts are the same step where they land within a few hundredths of one
    /// another. A cluster of one is dropped: a single shift can be mismeasured by
    /// a driver taking their time, and a ratio nothing corroborates is not a
    /// measurement.
    /// </summary>
    private static IReadOnlyList<double> Cluster(
        IReadOnlyList<ShiftStep> shifts, ShiftSettings s)
    {
        double[] ratios = [.. shifts.Select(x => x.Ratio).Where(double.IsFinite).Order()];

        List<List<double>> groups = [];

        foreach (double r in ratios)
        {
            if (groups.Count > 0 && r - groups[^1].Average() < s.SameStepWithin) groups[^1].Add(r);
            else groups.Add([r]);
        }

        return [.. groups.Where(g => g.Count >= 2).Select(g => g[g.Count / 2])];
    }
}
