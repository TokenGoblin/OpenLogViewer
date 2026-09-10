namespace OpenLogViewer.Core;

/// <summary>Where the road speed for a pull is taken from.</summary>
public enum SpeedSource
{
    /// <summary>
    /// Engine speed through the gearing. Preferred where it can be trusted: it is
    /// finer grained and faster sampled than any road speed sensor, and it is not
    /// quantised to whole units.
    /// </summary>
    EngineSpeedAndGear,

    /// <summary>
    /// The road speed channel. Necessary wherever engine speed and road speed
    /// stop being proportional — a slipping torque converter, a clutch that is
    /// not fully home, or wheelspin.
    /// </summary>
    VehicleSpeedChannel,
}

/// <summary>Something about a pull worth saying before its number is believed.</summary>
public enum PullFault
{
    /// <summary>The throttle came off part way through.</summary>
    ThrottleLifted,

    /// <summary>
    /// Engine speed and road speed stopped keeping step. On an automatic that is
    /// the converter; on anything it may be the tyres.
    /// </summary>
    RatioDrifted,

    /// <summary>Too little time to differentiate.</summary>
    TooShort,

    /// <summary>Too little of the rev range to be a curve rather than a point.</summary>
    TooNarrow,

    /// <summary>Too few samples a second for the fit to mean much.</summary>
    TooSlowlySampled,

    /// <summary>The ratio matched no gear the vehicle says it has.</summary>
    GearNotRecognised,

    /// <summary>No road speed at all, so the gear was taken on trust.</summary>
    GearNotChecked,

    /// <summary>No throttle channel, so nothing could confirm the pedal was down.</summary>
    ThrottleNotChecked,
}

/// <summary>What the gearing said about a stretch of log.</summary>
/// <param name="Gear">The gear it matched, counting from one, or zero for none.</param>
/// <param name="RpmPerMetrePerSecond">The ratio measured, whatever gear it turned out to be.</param>
/// <param name="SpreadPercent">
/// How much that ratio wandered across the pull, as a percentage of itself. A
/// manual gearbox with the clutch home holds it to a fraction of a per cent. A
/// slipping converter does not, and neither does a spinning tyre.
/// </param>
/// <param name="ErrorPercent">How far the measured ratio sits from the gear it was matched to.</param>
/// <param name="SpeedUnit">What the road speed channel turned out to be in.</param>
/// <param name="SpeedToMetresPerSecond">
/// What to multiply that channel by, carried alongside the name of the unit
/// because it is the number everything downstream actually wants and working it
/// back out from the name would be a second chance to get it wrong.
/// </param>
public sealed record GearFit(
    int Gear,
    double RpmPerMetrePerSecond,
    double SpreadPercent,
    double ErrorPercent,
    string SpeedUnit,
    double SpeedToMetresPerSecond = double.NaN)
{
    public bool Recognised => Gear > 0;

    /// <summary>
    /// Whether the ratio was actually measured, as against attempted.
    ///
    /// The distinction matters because a channel can exist and carry nothing
    /// usable. A car with no receiver logs its GPS speed as a flat zero all
    /// session, and every sample of it is discarded as too slow to divide by —
    /// which leaves no ratio, no gear, and a pull that would otherwise be
    /// reported as having matched no gear the vehicle has. It matched nothing
    /// because nothing was measured, and those are different complaints.
    /// </summary>
    public bool Checked => double.IsFinite(SpreadPercent);
}

/// <summary>One wide-open stretch of a log, and what is wrong with it.</summary>
public sealed record DynoPull
{
    public required int First { get; init; }

    public required int Last { get; init; }

    public required double StartSeconds { get; init; }

    public required double EndSeconds { get; init; }

    public required double StartRpm { get; init; }

    public required double EndRpm { get; init; }

    /// <summary>The gear it was made in, as far as anything could tell.</summary>
    public required GearFit Gearing { get; init; }

    public required SpeedSource Speed { get; init; }

    public required double SampleRateHz { get; init; }

    public required IReadOnlyList<PullFault> Faults { get; init; }

    /// <summary>Where the throttle came off, if it did — for marking on the chart.</summary>
    public double LiftAtRpm { get; init; } = double.NaN;

    public int SampleCount => Last - First + 1;

    public double Seconds => EndSeconds - StartSeconds;

    public double RpmSpan => EndRpm - StartRpm;

    /// <summary>
    /// Nothing to say about it, which is not the same as it being right — only
    /// that none of the things that can be checked came back wrong.
    /// </summary>
    public bool IsClean => Faults.Count == 0;

    public override string ToString() =>
        $"{StartSeconds:N1} – {EndSeconds:N1} s · "
        + (Gearing.Recognised ? $"{Ordinal(Gearing.Gear)} · " : "gear unknown · ")
        + $"{StartRpm:N0} – {EndRpm:N0} rpm · {SampleRateHz:N0} Hz";

    private static string Ordinal(int gear) => gear switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{gear}th",
    };
}

/// <summary>Where full throttle sits on this log, in the channel's own units.</summary>
/// <param name="WideOpen">The reading the pedal reaches when it is on the floor.</param>
/// <param name="Tolerance">How far under that still counts, in those same units.</param>
internal readonly record struct WideOpenThrottle(double WideOpen, double Tolerance)
{
    public bool Usable => double.IsFinite(WideOpen);

    public double Floor => WideOpen - Tolerance;
}

/// <summary>What counts as a pull.</summary>
public sealed record PullSettings
{
    /// <summary>
    /// Shortest run worth differentiating, in seconds.
    ///
    /// Two seconds is about four fitting windows. Less than that and most of the
    /// curve is the one-sided fit at the two ends, which is where the answer is
    /// least reliable.
    /// </summary>
    public double MinimumSeconds { get; init; } = 2.0;

    /// <summary>Least of the rev range that makes a curve rather than a point.</summary>
    public double MinimumRpmSpan { get; init; } = 1500;

    /// <summary>
    /// Slowest sampling the fit can be asked to work with.
    ///
    /// Ten a second puts five samples inside the default half-second window,
    /// which is the fewest a quadratic can be fitted to without the answer being
    /// mostly about the three points nearest the middle.
    /// </summary>
    public double MinimumSampleRateHz { get; init; } = 10;

    /// <summary>
    /// How far under the log's own full throttle still counts as full throttle.
    ///
    /// Against the log's own maximum rather than against a hundred, because a
    /// throttle channel reaches wherever its calibration puts it: plenty of
    /// controllers stop at 96, and one that reports a voltage may top out
    /// anywhere. A fixed threshold of ninety finds nothing on one log and
    /// everything on another.
    /// </summary>
    public double ThrottleTolerancePercent { get; init; } = 4;

    /// <summary>
    /// How high a throttle channel must reach before it is believed to have seen
    /// full throttle at all.
    ///
    /// A log where the pedal never went past half cannot say where full throttle
    /// is, and taking its maximum as the reference would call a gentle
    /// part-throttle acceleration a dyno pull.
    /// </summary>
    public double MinimumWideOpenPercent { get; init; } = 70;

    /// <summary>
    /// How far the gear ratio may wander across a pull before it is called out.
    ///
    /// A manual with the clutch home holds it to a fraction of a per cent, so two
    /// and a half is generous. It is set where it is to catch a torque converter
    /// still slipping rather than to police measurement noise.
    /// </summary>
    public double RatioTolerancePercent { get; init; } = 2.5;

    /// <summary>How far the measured ratio may sit from a gear and still be called that gear.</summary>
    public double GearTolerancePercent { get; init; } = 8;

    /// <summary>
    /// How fast engine speed has to be climbing to count as pulling.
    ///
    /// Not simply "rising", which would be a comparison against zero. A steady
    /// idle fitted over half a second comes out with a slope of a few parts in a
    /// quadrillion rather than exactly nothing, and on the wrong side of zero
    /// about half the time — enough to glue a minute of idling onto the front of
    /// the pull that follows it and take the start of the curve from there.
    ///
    /// Fifty a second is far below any real pull, which manages hundreds even in
    /// a tall gear, and far above the noise. It also trims the flat stretch at a
    /// limiter off the end, where there is no acceleration left to measure.
    /// </summary>
    public double MinimumRiseRpmPerSecond { get; init; } = 50;

    /// <summary>The window the engine speed is differentiated over to find the rise.</summary>
    public double WindowSeconds { get; init; } = RateOfChange.DefaultWindowSeconds;
}

/// <summary>Every pull in a log, and why the rest of it is not one.</summary>
/// <param name="Pulls">What was found, in the order it happened.</param>
/// <param name="Summary">A sentence about the search, for showing when it found nothing.</param>
public sealed record PullSearchResult(IReadOnlyList<DynoPull> Pulls, string Summary)
{
    public bool Any => Pulls.Count > 0;

    public IEnumerable<DynoPull> Clean => Pulls.Where(p => p.IsClean);
}

/// <summary>
/// Finding the parts of a log that are dyno pulls, and deciding what to believe
/// about them.
///
/// <para>
/// A road dyno's arithmetic is not hard. Picking the right stretch of log to
/// point it at is where the answers actually go wrong, because every one of the
/// ways it goes wrong produces a plausible curve rather than an obvious failure:
/// a gearshift in the middle reads as a torque dip, a lift reads as a hole, a
/// spinning tyre reads as extra power, and a pull in a gear other than the one
/// entered reads as a car that is uniformly stronger or weaker than it is.
/// </para>
/// <para>
/// So nothing here is inferred from a name or a convention. Runs are cut where
/// engine speed stops rising, which separates gearshifts without needing to know
/// that a gearshift happened. Full throttle is read against the log's own
/// maximum rather than against a hundred. And where a road speed channel exists,
/// the gear is <em>measured</em> from the ratio of engine speed to road speed
/// rather than taken on trust — which also, at no extra cost, catches the two
/// cases where the ratio is not constant at all.
/// </para>
/// <para>
/// <b>That last check is what makes an automatic safe.</b> A torque converter
/// that is not locked means engine speed is not proportional to road speed, and
/// working the car's speed out from the tachometer is then simply wrong — badly
/// so at the bottom of a pull, where the slip is greatest. The same test catches
/// a tyre losing grip, which is the other way a road test credits the engine with
/// something it did not do. Where the ratio holds, engine speed is used because
/// it is the better signal; where it does not, the road speed channel is used
/// instead and the pull is flagged.
/// </para>
/// </summary>
public static class DynoRun
{
    /// <summary>
    /// Every stretch of the log that could be a pull.
    ///
    /// The vehicle is needed even to look, because recognising the gear is part
    /// of recognising the pull.
    /// </summary>
    public static PullSearchResult Find(
        LogDocument log, VehicleSpec vehicle, PullSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(vehicle);

        PullSettings s = settings ?? new PullSettings();

        LogChannel? rpm = ChannelRoles.Find(log, ChannelRole.EngineSpeed);

        if (rpm is null)
        {
            return new PullSearchResult(
                [], "This log has no engine speed channel, so there is nothing to make a curve against.");
        }

        if (log.SampleCount < 4)
        {
            return new PullSearchResult([], "This log is too short to contain a pull.");
        }

        LogChannel? throttle = ChannelRoles.Find(log, ChannelRole.Throttle);
        LogChannel? road = ChannelRoles.Find(log, ChannelRole.VehicleSpeed);

        WideOpenThrottle pedal = WideOpenLevel(throttle, s);

        // A throttle channel that never went far open is not the same as having
        // none. Having none means the pedal cannot be checked, and runs are
        // offered with that said against them. A channel that stayed shut is
        // positive evidence that nothing here was a pull, and offering the
        // briskest bit of part-throttle acceleration instead would be worse than
        // finding nothing.
        if (throttle is not null && !pedal.Usable)
        {
            return new PullSearchResult(
                [],
                "The throttle never went far enough open in this log to say where full throttle is, "
                + "so none of it is a pull.");
        }

        // A window has to hold samples either side of the one being fitted. Where
        // the log is slower than that, every slope comes back unknown, no run
        // ever looks like it is rising, and the search would otherwise report
        // "nothing here has engine speed rising for long enough" — blaming the
        // driving for what is a property of the recording. A two to four hertz
        // OBD2 session, which this application itself produces over a dongle,
        // lands squarely in it.
        double interval = log.MedianSampleInterval;

        if (interval > 0 && interval * 2 >= s.WindowSeconds)
        {
            return new PullSearchResult(
                [],
                $"This log is sampled about {1 / interval:N0} times a second, which is too slow to "
                + $"differentiate over a {s.WindowSeconds:N2} s window — the fit needs samples either "
                + $"side of each one, and at this rate it has none. A window of {interval * 5:N1} s or "
                + "more would work, at the cost of smoothing the curve.");
        }

        ChannelFit rise = RateOfChange.Fit(log, rpm, s.WindowSeconds);

        List<DynoPull> pulls = [];
        int rejectedShort = 0;
        int rejectedPartThrottle = 0;

        foreach ((int rising, int until) in RisingRuns(log, rise, rpm, s))
        {
            int first = rising;
            int last = until;

            // Trimmed to where the throttle was actually open, not merely to
            // where engine speed was rising.
            //
            // The two are not the same, and the difference is not cosmetic. The
            // fit that finds the rise looks half a window either side of every
            // sample, so it sees a pull coming: at the instant before the pedal
            // goes down it is already reporting a steep climb, and the run
            // therefore starts a quarter of a second early, in whatever the car
            // was doing beforehand. Left alone, the curve begins at an engine
            // speed the engine was never pulling at.
            if (pedal.Usable
                && !TrimToWideOpen(throttle!, pedal.Floor, ref first, ref last))
            {
                // Nothing in the run was at full throttle, so it is an
                // acceleration rather than a pull.
                rejectedPartThrottle++;
                continue;
            }

            double seconds = log.Time.At(last) - log.Time.At(first);

            // Discarded outright only below one fitting window, which is the
            // point at which there is no derivative to be had at all. Anything
            // longer is offered with its shortcomings recorded against it, the
            // way a thinly sampled run already was — a run that is nearly long
            // enough is still the person's to judge, and dropping it silently
            // tells them nothing.
            if (seconds < s.WindowSeconds) { rejectedShort++; continue; }

            pulls.Add(Describe(log, vehicle, s, rpm, throttle, road, rise, first, last, pedal));
        }

        return new PullSearchResult(
            pulls, Summarise(pulls, throttle, rejectedShort, rejectedPartThrottle));
    }

    // ----- finding the runs -------------------------------------------------------

    /// <summary>
    /// Stretches over which engine speed is rising, not interrupted by a hole in
    /// the log.
    ///
    /// Cutting on the rise rather than looking for gearshifts is what makes this
    /// work without knowing anything about the gearbox. A shift drops engine
    /// speed, a lift drops it, a limiter flattens it and a downshift raises it
    /// discontinuously — all of which end a run here, and all of which would
    /// otherwise sit in the middle of one and be measured as though the engine
    /// had done them.
    ///
    /// The rise is taken from the fitted slope rather than from one sample to the
    /// next, so a single noisy reading does not chop a good pull into pieces.
    /// </summary>
    private static IEnumerable<(int First, int Last)> RisingRuns(
        LogDocument log, ChannelFit rise, LogChannel rpm, PullSettings s)
    {
        double gap = log.GapThreshold;
        int start = -1;

        for (int i = 0; i < log.SampleCount; i++)
        {
            bool rising = rise.SlopePerSecond[i] >= s.MinimumRiseRpmPerSecond
                          && double.IsFinite(rpm.At(i))
                          && rpm.At(i) > 0;

            bool broken = i > 0 && gap > 0 && log.Time.At(i) - log.Time.At(i - 1) > gap;

            if (rising && !broken)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0 && i - 1 > start) yield return (start, i - 1);

            start = rising ? i : -1;
        }

        if (start >= 0 && log.SampleCount - 1 > start) yield return (start, log.SampleCount - 1);
    }

    // ----- describing one --------------------------------------------------------

    private static DynoPull Describe(
        LogDocument log, VehicleSpec vehicle, PullSettings s,
        LogChannel rpm, LogChannel? throttle, LogChannel? road, ChannelFit rise,
        int first, int last, WideOpenThrottle pedal)
    {
        double seconds = log.Time.At(last) - log.Time.At(first);
        double hz = seconds > 0 ? (last - first) / seconds : 0;

        GearFit gearing = InferGear(vehicle, rpm, road, first, last, s);

        var faults = new List<PullFault>();

        if (seconds < s.MinimumSeconds) faults.Add(PullFault.TooShort);
        if (rpm.At(last) - rpm.At(first) < s.MinimumRpmSpan) faults.Add(PullFault.TooNarrow);
        if (hz < s.MinimumSampleRateHz) faults.Add(PullFault.TooSlowlySampled);
        if (throttle is null) faults.Add(PullFault.ThrottleNotChecked);

        double liftAt = double.NaN;

        // Whether the throttle came off is tracked apart from where it did. The
        // two were the same value, so an engine speed that could not be read
        // would have taken the fault away with it. As things stand a run holds
        // only samples whose fit succeeded, so that could not actually happen —
        // but a fact riding on a value which is allowed to go missing is one
        // reordering away from being lost, and this one is the difference
        // between a driver's lift and a hole in the engine's curve.
        if (pedal.Usable && Lifted(throttle!, rise, first, last, pedal.Floor, out liftAt))
        {
            faults.Add(PullFault.ThrottleLifted);
        }

        if (road is null || !gearing.Checked)
        {
            faults.Add(PullFault.GearNotChecked);
        }
        else if (gearing.SpreadPercent > s.RatioTolerancePercent)
        {
            faults.Add(PullFault.RatioDrifted);
        }
        else if (!gearing.Recognised)
        {
            faults.Add(PullFault.GearNotRecognised);
        }

        // Engine speed is the better signal and is used wherever it can be
        // trusted. It stops being trustworthy exactly when it stops keeping step
        // with the road, which is the drift above.
        SpeedSource source =
            gearing.Checked && gearing.SpreadPercent > s.RatioTolerancePercent
                ? SpeedSource.VehicleSpeedChannel
                : SpeedSource.EngineSpeedAndGear;

        return new DynoPull
        {
            First = first,
            Last = last,
            StartSeconds = log.Time.At(first),
            EndSeconds = log.Time.At(last),
            // What the log says at the two ends, not what the fit says. The fit
            // reaches half a window either side of every sample, so at the first
            // sample of a pull it is still partly describing whatever preceded
            // it — a downshift, a cruise — and would report the curve as starting
            // at an engine speed the engine never pulled at.
            StartRpm = rpm.At(first),
            EndRpm = rpm.At(last),
            Gearing = gearing,
            Speed = source,
            SampleRateHz = hz,
            Faults = faults,
            LiftAtRpm = liftAt,
        };
    }

    /// <summary>
    /// The engine speed at which the throttle first came off, or NaN if it never
    /// did.
    ///
    /// Reported against engine speed rather than against time because that is
    /// the axis the curve is drawn on, and a notch in a curve is only explicable
    /// once you can see it lines up with the lift.
    /// </summary>
    private static bool Lifted(
        LogChannel throttle, ChannelFit rise, int first, int last, double floor, out double atRpm)
    {
        atRpm = double.NaN;

        for (int i = first; i <= last; i++)
        {
            double tps = throttle.At(i);

            if (!double.IsFinite(tps) || tps >= floor) continue;

            atRpm = rise.Value[i];

            return true;

        }

        return false;
    }

    // ----- the gear ---------------------------------------------------------------

    /// <summary>
    /// Which gear a stretch of log was driven in, measured rather than assumed.
    ///
    /// <para>
    /// Engine speed over road speed is a constant in a given gear, and the
    /// constant is different for every gear, so the ratio names the gear. What
    /// makes it worth doing rather than asking is that the same measurement
    /// answers two further questions for nothing: whether the ratio held at all,
    /// which is the slip check, and what units the road speed channel is in.
    /// </para>
    /// <para>
    /// <b>The units fall out of the gearing.</b> A great many logs record a speed
    /// and never say whether it is miles or kilometres an hour, and no inspection
    /// of the values can tell — seventy is ordinary in either. But the gearbox is
    /// known, and only one reading of the channel makes the ratio land on a gear
    /// the car has. Guessing wrong would be a factor of 1.6 on every road speed
    /// and therefore on every horsepower, while looking entirely believable, so
    /// this is worth more than the small amount of code it costs.
    /// </para>
    /// </summary>
    public static GearFit InferGear(
        VehicleSpec vehicle, LogChannel rpm, LogChannel? road, int first, int last,
        PullSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(rpm);

        PullSettings s = settings ?? new PullSettings();

        if (road is null) return new GearFit(0, double.NaN, double.NaN, double.NaN, "");

        double declared = ChannelUnits.SpeedToMetresPerSecond(road);

        IReadOnlyList<ChannelUnits.SpeedUnit> candidates = double.IsFinite(declared)
            ? [new ChannelUnits.SpeedUnit(road.Units, declared)]
            : ChannelUnits.SpeedUnits;

        GearFit? best = null;

        foreach (ChannelUnits.SpeedUnit unit in candidates)
        {
            GearFit fit = FitOneUnit(vehicle, rpm, road, first, last, unit, s);

            // The unit that puts the ratio nearest a real gear is the unit. Where
            // the channel declared itself there is only one candidate and this
            // decides nothing.
            if (best is null || Better(fit, best)) best = fit;
        }

        return best!;
    }

    private static bool Better(GearFit candidate, GearFit incumbent)
    {
        if (candidate.Recognised != incumbent.Recognised) return candidate.Recognised;

        return candidate.ErrorPercent < incumbent.ErrorPercent;
    }

    private static GearFit FitOneUnit(
        VehicleSpec vehicle, LogChannel rpm, LogChannel road, int first, int last,
        ChannelUnits.SpeedUnit unit, PullSettings s)
    {
        List<double> ratios = [];

        for (int i = first; i <= last; i++)
        {
            double r = rpm.At(i);
            double v = road.At(i) * unit.ToMetresPerSecond;

            // Below walking pace the ratio is all quantisation — a road speed
            // reported to the nearest whole unit divides very badly when it is
            // only a few units.
            if (!double.IsFinite(r) || !double.IsFinite(v) || v < 3 || r <= 0) continue;

            ratios.Add(r / v);
        }

        if (ratios.Count < 3)
        {
            return new GearFit(
                0, double.NaN, double.NaN, double.PositiveInfinity,
                unit.Unit, unit.ToMetresPerSecond);
        }

        ratios.Sort();

        double median = Quantile(ratios, 0.5);

        // The ten to ninety spread rather than the full range, so one bad sample
        // at either end does not condemn a pull that held its ratio throughout.
        double spread = median > 0
            ? (Quantile(ratios, 0.9) - Quantile(ratios, 0.1)) / median * 100
            : double.NaN;

        int gear = 0;
        double bestError = double.PositiveInfinity;

        for (int g = 1; g <= vehicle.GearRatios.Count; g++)
        {
            double expected = vehicle.RpmFromSpeedMs(1, g);

            if (!(expected > 0)) continue;

            double error = Math.Abs(median - expected) / expected * 100;

            if (error >= bestError) continue;

            bestError = error;
            gear = g;
        }

        if (bestError > s.GearTolerancePercent) gear = 0;

        return new GearFit(gear, median, spread, bestError, unit.Unit, unit.ToMetresPerSecond);
    }

    // ----- odds and ends ----------------------------------------------------------

    /// <summary>
    /// Where full throttle is on this log, or NaN if the log never went there.
    ///
    /// Taken at the ninety-ninth percentile rather than at the maximum, so a
    /// single spike above the sensor's real ceiling does not move the reference
    /// and disqualify every genuine pull below it.
    /// </summary>
    private static WideOpenThrottle WideOpenLevel(LogChannel? throttle, PullSettings s)
    {
        var none = new WideOpenThrottle(double.NaN, double.NaN);

        if (throttle is null) return none;

        List<double> values = [];

        for (int i = 0; i < throttle.Length; i++)
        {
            double v = throttle.At(i);
            if (double.IsFinite(v)) values.Add(v);
        }

        if (values.Count < 4) return none;

        values.Sort();

        double top = Quantile(values, 0.99);

        // A throttle logged as a fraction of one rather than as a percentage.
        // Told from the values here, where ChannelUnits.ToFraction deliberately
        // will not: that one converts every sample, and a duty cycle really can
        // sit under one per cent all log. This is the largest reading of a pedal
        // across a whole session, and no pedal peaks at one per cent. Getting it
        // wrong the other way costs a confident refusal of a log full of genuine
        // pulls.
        double scale = top is > 0 and <= 1.5 ? 100 : 1;

        return top * scale >= s.MinimumWideOpenPercent
            ? new WideOpenThrottle(top, s.ThrottleTolerancePercent / scale)
            : none;
    }

    /// <summary>
    /// Narrows a run to the first and last sample at full throttle, and says
    /// whether anything was left.
    ///
    /// The outermost ones rather than an unbroken stretch of them, deliberately.
    /// A lift in the middle of a pull is something to report about that pull, not
    /// a reason to cut it in two and offer the halves — the person wants to see
    /// the notch and be told what it was.
    /// </summary>
    private static bool TrimToWideOpen(LogChannel throttle, double floor, ref int first, ref int last)
    {
        int open = -1;
        int shut = -1;

        for (int i = first; i <= last; i++)
        {
            double v = throttle.At(i);

            if (!double.IsFinite(v) || v < floor) continue;

            if (open < 0) open = i;
            shut = i;
        }

        if (open < 0) return false;

        first = open;
        last = shut;

        return true;
    }

    /// <summary>A quantile of an already sorted list, interpolated between samples.</summary>
    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];

        double position = q * (sorted.Count - 1);
        int below = (int)Math.Floor(position);
        int above = Math.Min(below + 1, sorted.Count - 1);
        double fraction = position - below;

        return sorted[below] + ((sorted[above] - sorted[below]) * fraction);
    }

    /// <summary>
    /// A sentence about the search, written for the case where it found nothing.
    ///
    /// "No pulls found" on its own is the unhelpful answer, because every reason
    /// it might have found none is fixable and the person is entitled to know
    /// which one applies to them.
    /// </summary>
    private static string Summarise(
        IReadOnlyList<DynoPull> pulls, LogChannel? throttle, int shortRuns, int partThrottle)
    {
        if (pulls.Count > 0)
        {
            int clean = pulls.Count(p => p.IsClean);

            string found = pulls.Count == 1 ? "One pull" : $"{pulls.Count} pulls";
            string state = clean == pulls.Count
                ? "nothing to report on any of them"
                : clean == 0
                    ? "every one of them has something worth reading first"
                    : $"{clean} with nothing to report";

            string gearing = pulls.All(p => p.Faults.Contains(PullFault.GearNotChecked))
                ? " Nothing usable to measure the gear against, so it was taken on trust."
                : "";

            return $"{found} found — {state}.{gearing}";
        }

        List<string> reasons = [];

        if (throttle is null)
        {
            reasons.Add("there is no throttle channel, so full throttle could not be told from part");
        }

        if (partThrottle > 0) reasons.Add($"{partThrottle} were not at full throttle");
        if (shortRuns > 0) reasons.Add($"{shortRuns} were too brief to differentiate at all");

        return reasons.Count == 0
            ? "Nothing in this log has engine speed rising for long enough to be a pull."
            : "No pulls found: " + string.Join("; ", reasons) + ".";
    }

    /// <summary>What to call a fault, in the words the person would use.</summary>
    public static string Describe(PullFault fault) => fault switch
    {
        PullFault.ThrottleLifted =>
            "the throttle came off part way through, so the notch in the curve is the driver rather than the engine",
        PullFault.RatioDrifted =>
            "engine speed and road speed did not keep step — a converter still slipping, a clutch not quite home, or a tyre losing grip",
        PullFault.TooShort => "too little time in it to differentiate confidently",
        PullFault.TooNarrow => "too little of the rev range to be a curve rather than a point",
        PullFault.TooSlowlySampled => "sampled too slowly for the fit to say much",
        PullFault.GearNotRecognised => "the ratio matched no gear this vehicle says it has",
        PullFault.GearNotChecked => "no road speed channel, so the gear could not be checked",
        PullFault.ThrottleNotChecked =>
            "no throttle channel, so nothing here confirms the pedal was on the floor throughout",
        _ => fault.ToString(),
    };
}
