using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>How much attention a finding deserves.</summary>
public enum InsightLevel
{
    /// <summary>Checked, and it is as it should be. Worth saying so.</summary>
    Good,

    /// <summary>Nothing to act on, but something a tuner would want to know.</summary>
    Note,

    /// <summary>Worth looking at before the next drive.</summary>
    Watch,

    /// <summary>Something that damages engines. Said first and said plainly.</summary>
    Warning,

    /// <summary>The log cannot answer this — usually a channel that is not in it.</summary>
    Unanswered,
}

/// <summary>
/// One thing a log has to say, with the arithmetic that says it.
/// </summary>
/// <param name="Level">How much attention it deserves.</param>
/// <param name="Title">The finding, in one line.</param>
/// <param name="Detail">What was measured and what it means.</param>
/// <param name="Evidence">
/// The numbers behind it, so a tuner can disagree with the conclusion without
/// having to take the measurement on trust.
/// </param>
/// <param name="Samples">How many samples the finding rests on.</param>
public sealed record LogInsight(
    InsightLevel Level, string Topic, string Title, string Detail, string Evidence, int Samples)
{
    public override string ToString() => $"[{Level}] {Title} — {Detail}";
}

/// <summary>
/// What a datalog says about the engine that produced it.
///
/// <para>
/// <b>Every finding here is arithmetic on the samples, not a rule of thumb.</b>
/// A tuner can already see the traces; what they cannot see is that the mixture
/// under boost is four tenths lean of target with a standard error of six
/// hundredths, which is a real difference, while the same four tenths at idle
/// over nine samples is not. The whole value of this is telling those two apart.
/// </para>
/// <para>
/// So each finding carries what it rests on, and a finding that cannot be made
/// safely is not made. Three rules throughout:
/// </para>
/// <list type="bullet">
/// <item>A claim about a difference states its standard error. A mean of a
/// handful of samples is not evidence and is reported as "not enough data"
/// rather than as a small effect.</item>
/// <item>Where outliers decide the answer — a single lean spike during a gear
/// change — the median and the percentiles are used rather than the mean.</item>
/// <item>Silence is a finding. A channel that is absent, or a condition never
/// reached, is said out loud, because "no warning" and "never checked" look
/// identical otherwise and only one of them is reassuring.</item>
/// </list>
/// </summary>
public static class LogInsights
{
    /// <summary>Below this a mixture reading is not the engine's steady state.</summary>
    private const int MinimumSamples = 30;

    /// <summary>
    /// How far the fuelling was out, as a fraction: delivered against required.
    ///
    /// <para>
    /// <b>Not the difference between two AFR numbers.</b> A raw difference is in
    /// units of the target, so a tenth at 12:1 under boost and a tenth at 16:1
    /// on the overrun are the same number and nothing like the same error. This
    /// is a ratio, which cancels the target — so cells with different targets
    /// can be averaged together, and a drive at altitude compares with one at
    /// sea level.
    /// </para>
    /// <para>
    /// <b>And the closed loop is folded back in, which is the important part.</b>
    /// Measured mixture is what the map did <em>plus</em> whatever the loop
    /// rescued. Read without folding that back, a map that is fifteen per cent
    /// out while the loop quietly covers it reads as perfect — and then misbehaves
    /// the moment the loop drops out, which is exactly when it is least welcome.
    /// </para>
    /// <para>
    /// The correction is divided out rather than multiplied in. A controller
    /// scales its pulse width by this percentage, so fuel from the map alone is
    /// what was delivered divided by that scaling: a loop pulling ten per cent
    /// out is covering a map ten per cent rich. If a firmware turns out to
    /// report the reciprocal, this sign flips, so the assumption is stated in the
    /// evidence rather than buried here.
    /// </para>
    /// </summary>
    /// <returns>Per cent: positive is rich, negative lean. NaN where unusable.</returns>
    private static double FuelError(Engine e, int at, int mixtureAt)
    {
        if (e.Afr is not { } afr || e.Target is not { } target) return double.NaN;

        double measured = afr.At(mixtureAt);
        double wanted = target.At(at);

        if (double.IsNaN(measured) || double.IsNaN(wanted) || measured <= 0 || wanted <= 0)
            return double.NaN;

        double ratio = wanted / measured;

        if (e.Correction is { } trim)
        {
            double correction = trim.At(at);

            // A correction of nought would be the controller commanding no fuel
            // at all, which is a channel that is not what it appears to be.
            if (!double.IsNaN(correction) && correction > 1) ratio *= 100.0 / correction;
        }

        return (ratio - 1) * 100;
    }

    /// <summary>Throttle movement, per cent a second, above which fuelling is transient.</summary>
    private const double SettledThrottleRate = 20;

    /// <summary>Everything a log has to say, most serious first.</summary>
    public static IReadOnlyList<LogInsight> From(LogDocument log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var found = new List<LogInsight>();

        if (log.SampleCount < 2)
        {
            return [Unanswered("The log", "There is nothing in this log to measure.",
                               "A log needs at least a couple of samples.", 0)];
        }

        var engine = new Engine(log);
        engine.MeasureDelay();

        found.AddRange(MixtureAgainstTarget(engine));
        found.AddRange(MixtureUnderLoad(engine));
        found.AddRange(ClosedLoop(engine));
        found.AddRange(InjectorHeadroom(engine));
        found.AddRange(DeadTimeAndVoltage(engine));
        found.AddRange(Knock(engine));
        found.AddRange(Warmup(engine));
        found.AddRange(ManifoldAgainstAmbient(engine));
        found.AddRange(PressureReference(engine));
        found.AddRange(IdleSteadiness(engine));
        found.AddRange(StuckChannels(engine));
        found.AddRange(SampleRate(engine));
        found.AddRange(MixtureDelay(engine));
        found.AddRange(Coverage(engine));

        // Worst first, and within a level the best-evidenced first: a warning
        // resting on two thousand samples belongs above one resting on thirty.
        return
        [
            .. found
                .OrderByDescending(f => Rank(f.Level))
                .ThenByDescending(f => f.Samples),
        ];
    }

    private static int Rank(InsightLevel level) => level switch
    {
        InsightLevel.Warning => 4,
        InsightLevel.Watch => 3,
        InsightLevel.Note => 2,
        InsightLevel.Good => 1,
        _ => 0,
    };

    // ----- the log, with the channels this needs already found ---------------

    private sealed class Engine(LogDocument log)
    {
        public LogDocument Log { get; } = log;

        public int Count => Log.SampleCount;

        public LogChannel? Rpm { get; } = ChannelRoles.Find(log, ChannelRole.EngineSpeed);

        public LogChannel? Map { get; } = ChannelRoles.Find(log, ChannelRole.ManifoldPressure);

        public LogChannel? Afr { get; } = ChannelRoles.Find(log, ChannelRole.Mixture);

        public LogChannel? Target { get; } = ChannelRoles.Find(log, ChannelRole.MixtureTarget);

        public LogChannel? Throttle { get; } = ChannelRoles.Find(log, ChannelRole.Throttle);

        public LogChannel? Coolant { get; } = ChannelRoles.Find(log, ChannelRole.Coolant);

        public LogChannel? Duty { get; } = ChannelRoles.Find(log, ChannelRole.InjectorDuty);

        public LogChannel? PulseWidth { get; } = ChannelRoles.Find(log, ChannelRole.InjectorPulseWidth);

        public LogChannel? Battery { get; } = ChannelRoles.Find(log, ChannelRole.BatteryVoltage);

        public LogChannel? Knock { get; } = ChannelRoles.Find(log, ChannelRole.KnockRetard);

        public LogChannel? Correction { get; } = ChannelRoles.Find(log, ChannelRole.MixtureCorrection);

        public LogChannel? Warmup { get; } = ChannelRoles.Find(log, ChannelRole.WarmupCorrection);

        public LogChannel? Baro { get; } = ChannelRoles.Find(log, ChannelRole.Barometric);

        public LogChannel? Boost { get; } = ChannelRoles.Find(log, ChannelRole.Boost);

        /// <summary>Samples where the engine was turning fast enough to be running.</summary>
        public bool Running(int i) => Rpm is { } rpm && rpm.At(i) > 400;

        /// <summary>
        /// Samples where the throttle was still.
        ///
        /// Fuelling during a tip-in is accelerator enrichment rather than the
        /// table, and judging a table by it is judging the wrong thing. Where
        /// there is no throttle channel every sample counts, which is worse but
        /// is not a reason to say nothing.
        /// </summary>
        public bool Settled(int i)
        {
            if (Throttle is not { } tps || i < 1) return true;

            double dt = Log.Time.At(i) - Log.Time.At(i - 1);
            if (dt <= 0 || double.IsNaN(dt)) return true;

            double rate = Math.Abs(tps.At(i) - tps.At(i - 1)) / dt;

            return !double.IsNaN(rate) && rate < SettledThrottleRate;
        }

        /// <summary>
        /// How many samples the mixture reads behind, as whole samples.
        ///
        /// The wideband measures the charge that was injected several frames
        /// ago. Ignoring it smears every conclusion across the boundary between
        /// one operating point and the next — on a real log, reading a mixture
        /// without the delay gave 12.3 AFR where applying it gave 10.9, which is
        /// the difference between "somewhat rich" and "a third rich".
        /// </summary>
        public int Delay { get; private set; }

        public void MeasureDelay()
        {
            if (Afr is not { } afr || Target is not { } target) return;
            if (Rpm is not { } rpm || Map is not { } map || Count < 200) return;

            double interval = Spacing;
            if (interval <= 0) return;

            try
            {
                DelaySearchResult found = WidebandDelay.Find(
                    VeAnalysis.GridFrom(rpm, map, 8, 8, 0, Count - 1),
                    rpm, map, afr, target, 0, Count - 1, interval);

                if (!found.HasProblem && !found.NoneIsPlausible)
                    Delay = (int)Math.Round(found.BestSeconds / interval);
            }
            catch (ArgumentException)
            {
                // A grid the log cannot fill. Nothing to align against.
            }
        }

        private double _spacing = -1;

        /// <summary>The usual gap between samples, in seconds.</summary>
        public double Spacing
        {
            get
            {
                if (_spacing >= 0) return _spacing;

                var gaps = new List<double>();

                for (int i = 1; i < Count; i++)
                {
                    double dt = Log.Time.At(i) - Log.Time.At(i - 1);
                    if (!double.IsNaN(dt) && dt > 0) gaps.Add(dt);
                }

                return _spacing = gaps.Count == 0 ? 0 : Percentile(gaps, 50);
            }
        }

        /// <summary>
        /// Whether the engine held still either side of this sample.
        ///
        /// A rate of change catches a throttle being moved; it does not catch an
        /// engine that is oscillating about a point, which is just as unsteady
        /// and just as poisonous to a fuelling average. Half a second either
        /// side, which is the shortest window in which "steady" means anything.
        /// </summary>
        public bool Still(int i)
        {
            int half = Math.Max(1, (int)Math.Round(0.5 / Math.Max(Spacing, 1e-6)));

            return Within(Rpm, i, half, 120) && Within(Map, i, half, 4);
        }

        private bool Within(LogChannel? channel, int at, int half, double allowed)
        {
            if (channel is not { } c) return true;

            int from = Math.Max(0, at - half);
            int to = Math.Min(Count - 1, at + half);

            double low = double.MaxValue, high = double.MinValue;

            for (int i = from; i <= to; i++)
            {
                double v = c.At(i);
                if (double.IsNaN(v)) continue;

                low = Math.Min(low, v);
                high = Math.Max(high, v);
            }

            return low > high || high - low <= allowed * 2;
        }

        /// <summary>
        /// Whether the engine was warm <em>at this sample</em>.
        ///
        /// Per sample, not per log. Asking only whether the log ever got warm
        /// lets every sample from a cold start onward through — which is most of
        /// an ordinary drive's opening minutes, all of it carrying warmup
        /// enrichment on top of the table. Averaged in, that reads as a rich map
        /// and is nothing of the sort.
        /// </summary>
        public bool Warm(int i)
        {
            if (Coolant is not { } clt) return true;

            double now = clt.At(i);
            if (double.IsNaN(now)) return true;

            return now >= (Fahrenheit(clt) ? 160 : 71);
        }

        private bool _hotKnown;
        private bool _hot;

        /// <summary>
        /// Whether this log ever reached operating temperature at all, judged
        /// against the units the channel declares rather than a bare number.
        /// </summary>
        private bool Hot(LogChannel clt)
        {
            if (_hotKnown) return _hot;

            _hotKnown = true;
            double warm = Fahrenheit(clt) ? 160 : 71;

            for (int i = 0; i < Count; i++)
                if (clt.At(i) >= warm) { _hot = true; break; }

            return _hot;
        }

        public static bool Fahrenheit(LogChannel channel) =>
            channel.Units.Contains('F', StringComparison.OrdinalIgnoreCase)
            && !channel.Units.Contains('C', StringComparison.OrdinalIgnoreCase);
    }

    // ----- the findings ------------------------------------------------------

    /// <summary>
    /// Whether the mixture matched what was asked for, over the whole log.
    ///
    /// Judged on the mean error against its own standard error, which is what
    /// separates a table that is genuinely out from one that merely wanders. A
    /// tenth of an AFR either side is not worth a tuner's afternoon; a tenth
    /// that is twenty standard errors from zero is a table to fix.
    /// </summary>
    private static IEnumerable<LogInsight> MixtureAgainstTarget(Engine e)
    {
        if (e.Afr is not { } afr)
        {
            yield return Unanswered(
                "Mixture",
                "No wideband reading in this log, so nothing here can judge fuelling.",
                "Log an AFR or lambda channel and every mixture finding below becomes available.", 0);

            yield break;
        }

        if (e.Target is not { } target)
        {
            yield return Unanswered(
                "Mixture",
                "No AFR target in this log, so the mixture cannot be judged against anything.",
                $"\"{afr.Name}\" is here but nothing says what it should have been. "
                + "Add the target channel to the datalog.", 0);

            yield break;
        }

        var errors = new List<double>();
        int running = 0;

        for (int i = 0; i < e.Count - e.Delay; i++)
        {
            if (!e.Running(i)) continue;

            running++;

            if (!e.Settled(i) || !e.Warm(i) || !e.Still(i)) continue;

            // The mixture is read where the sensor saw it, the conditions where
            // the fuel was metered.
            double error = FuelError(e, i, i + e.Delay);
            if (!double.IsNaN(error)) errors.Add(error);
        }

        if (errors.Count < MinimumSamples)
        {
            yield return Unanswered(
                "Mixture",
                "Not enough settled running to judge the mixture.",
                $"{errors.Count} of {running:N0} running samples had the engine warm, the throttle "
                + "still and speed and load holding steady either side. About "
                + $"{MinimumSamples} are needed before an average means anything.",
                errors.Count);

            yield break;
        }

        (double mean, double error2) = MeanAndStandardError(errors);

        // The median within the settled set rather than the mean: a wideband
        // throws outliers on transients that survive any filter, and one of them
        // moves a mean and does not move a median.
        double median = Percentile(errors, 50);
        double kept = 100.0 * errors.Count / Math.Max(1, running);

        string numbers =
            $"median {median:+0.0;−0.0;0.0}% of required fuel, mean {mean:+0.0;−0.0;0.0} ± "
            + $"{error2:0.0} (standard error), {errors.Count:N0} of {running:N0} running samples "
            + $"kept ({kept:0}%)"
            + (e.Delay > 0 ? $", mixture read {e.Delay} samples late" : "")
            + (e.Correction is not null ? ", closed-loop correction divided back out" : "");

        // Three standard errors before a difference is called real, and two per
        // cent of the fuel required before it is called worth an afternoon.
        bool real = Math.Abs(median) > 3 * error2;
        bool matters = Math.Abs(median) > 2;

        if (!real || !matters)
        {
            yield return new LogInsight(
                InsightLevel.Good, "Mixture",
                "Fuelling is on target overall.",
                $"The map delivers within {Math.Abs(median):0.0}% of the fuel asked for, which is "
                + (real
                    ? "a real difference but too small to chase."
                    : "within what the measurement can resolve.")
                + Survival(kept),
                numbers, errors.Count);

            yield break;
        }

        yield return new LogInsight(
            InsightLevel.Watch, "Mixture",
            median > 0
                ? $"The map is delivering {median:0.0}% more fuel than asked for."
                : $"The map is delivering {Math.Abs(median):0.0}% less fuel than asked for.",
            "Measured as delivered against required rather than as a difference between two AFR "
            + "numbers, so cells with different targets can be counted together"
            + (e.Correction is not null
                ? " — and with the closed loop's correction divided back out, so this is the map's "
                  + "own error rather than what is left after the loop rescued it."
                : ".")
            + (median > 0
                ? " Rich washes bores and hides the real error."
                : " Lean costs power and, held under load, costs pistons.")
            + Survival(kept),
            numbers, errors.Count);
    }

    /// <summary>
    /// The same question again, asked only where it can hurt.
    ///
    /// A mixture error at idle is a curiosity. The same error at high load is
    /// what melts things, and averaging the two together buries it — most of a
    /// log is idle and cruise, so a whole-log mean is dominated by the samples
    /// that matter least.
    /// </summary>
    private static IEnumerable<LogInsight> MixtureUnderLoad(Engine e)
    {
        if (e.Afr is not { } afr || e.Target is not { } target || e.Map is not { } map) yield break;

        (double ambient, bool measured) = Atmosphere(e, map);

        // Nine tenths of atmospheric is the right line when atmospheric is
        // known. When it was assumed from the highest reading in the log, that
        // line lands at nine tenths of *peak boost* and throws away nearly all
        // the loaded running this exists to judge — so the load is taken
        // relatively instead, from the log's own spread.
        double threshold = measured ? ambient * 0.9 : LoadedFrom(e, map);

        var errors = new List<double>();
        double highest = double.MinValue;

        for (int i = 0; i < e.Count - e.Delay; i++)
        {
            if (!e.Running(i)) continue;

            double load = map.At(i);
            if (double.IsNaN(load) || load < threshold) continue;

            double short_ = FuelError(e, i, i + e.Delay);
            if (double.IsNaN(short_)) continue;

            errors.Add(short_);
            highest = Math.Max(highest, load);
        }

        string where = ambient > 0
            ? $"above {threshold:N0} {map.Units} — near and above atmospheric, which here reads "
              + $"{ambient:N0}"
            : "at high load";

        if (errors.Count < MinimumSamples)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Mixture under load",
                "Not enough high-load running in this log to judge it.",
                $"Only {errors.Count} samples were {where}. A log that never loads the engine "
                + "cannot say whether it is safe when loaded — which is the part worth knowing.",
                $"{errors.Count} samples above {threshold:N0} {map.Units}", errors.Count);

            yield break;
        }

        (double mean, double error) = MeanAndStandardError(errors);

        // Counted rather than taken as a percentile. A percentile can only find
        // a spike that affects more of the log than the percentile leaves out —
        // at the 95th, an excursion in one sample of two hundred is invisible,
        // and one excursion is what damages a piston. So the samples that are
        // dangerously lean are simply counted.
        //
        // Three of them before it is called a warning: one reading can be the
        // wideband catching a gear change or a misfire, and a tool that cries
        // out at every single sample is one nobody reads twice.
        // Six per cent short of the fuel asked for. Under load that is the
        // region where a mixture error stops costing power and starts costing
        // pistons.
        const double Dangerous = -6;

        int spikes = errors.Count(v => v < Dangerous);
        double worst = errors.Min();
        double middle = Percentile(errors, 50);

        string numbers =
            $"median {middle:+0.0;−0.0;0.0}%, mean {mean:+0.0;−0.0;0.0} ± {error:0.0}, "
            + $"worst {worst:+0.0;−0.0;0.0}%, {spikes:N0} sample{(spikes == 1 ? "" : "s")} more "
            + $"than {Math.Abs(Dangerous):0}% short, {errors.Count:N0} samples, "
            + $"peak {highest:N0} {map.Units}";

        if (spikes >= 3)
        {
            yield return new LogInsight(
                InsightLevel.Warning, "Mixture under load",
                $"Lean under load on {spikes:N0} samples, the worst {Math.Abs(worst):0.0}% short "
                + "of the fuel asked for.",
                // Said with the average beside it, because the two routinely
                // disagree and a warning that contradicts the mean without
                // explaining itself gets waved away.
                (middle > 0.5
                    ? $"On average this region runs {middle:0.0}% rich, which is exactly why the "
                      + "average is the wrong statistic here: a"
                    : "Averages hide this. A")
                + " single lean excursion at high load is what damages a piston, and it does not "
                + "need to be common to do it. Look at the highest-load cells of the fuel table, "
                + "and at whether the fuel system holds pressure there.",
                numbers, errors.Count);
        }
        else if (Math.Abs(middle) > 3 * error && middle < -2)
        {
            yield return new LogInsight(
                InsightLevel.Warning, "Mixture under load",
                $"Delivering {Math.Abs(middle):0.0}% less fuel than asked for where the engine is "
                + "loaded.",
                "Consistently lean under load, rather than a spike. This is the region where a "
                + "fuelling error turns into heat rather than into a slow car.",
                numbers, errors.Count);
        }
        else if (Math.Abs(middle) > 3 * error && middle > 2)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Mixture under load",
                $"Delivering {middle:0.0}% more fuel than asked for where the engine is loaded.",
                "Safe, and costing power and fuel. Worth leaning out once the rest of the tune is "
                + "settled, in small steps with the wideband watched.",
                numbers, errors.Count);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Mixture under load",
                "Fuelling under load holds target.",
                "The part of the map that can hurt the engine is being fuelled as asked, spikes "
                + "included.",
                numbers, errors.Count);
        }
    }

    /// <summary>
    /// Whether the closed loop was doing anything, and what it had to do.
    ///
    /// Two findings in one. A correction pinned at a hundred means the loop
    /// never engaged, which people routinely fail to notice — every reading in
    /// the log is then open loop and says exactly what the table says. And a
    /// correction consistently away from a hundred is the table being wrong by
    /// that much, which is a measurement of the VE error rather than a symptom.
    /// </summary>
    private static IEnumerable<LogInsight> ClosedLoop(Engine e)
    {
        if (e.Correction is not { } trim) yield break;

        var values = new List<double>();
        bool moved = false;
        double first = double.NaN;

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            double v = trim.At(i);
            if (double.IsNaN(v)) continue;

            if (double.IsNaN(first)) first = v;
            else if (Math.Abs(v - first) > 0.01) moved = true;

            values.Add(v);
        }

        if (values.Count < MinimumSamples) yield break;

        if (!moved)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Closed loop",
                $"The mixture correction never moved from {first:0.#}%.",
                "The closed loop did not engage anywhere in this log, so every reading here is the "
                + "fuel table on its own with nothing trimming it. That is the right condition for "
                + "judging the table — and the wrong one for concluding the car drives well, since "
                + "none of the correction that normally covers a table error was available.",
                $"{values.Count:N0} running samples, all at {first:0.##}%", values.Count);

            yield break;
        }

        (double mean, double error) = MeanAndStandardError(values);
        double bias = mean - 100;

        string numbers =
            $"mean {mean:0.0}% ± {error:0.0}, range {values.Min():0.0}–{values.Max():0.0}%, "
            + $"{values.Count:N0} samples";

        if (Math.Abs(bias) > 3 * error && Math.Abs(bias) > 2)
        {
            yield return new LogInsight(
                InsightLevel.Watch, "Closed loop",
                $"The closed loop is holding fuelling {Math.Abs(bias):0.0}% "
                + (bias > 0 ? "up" : "down") + " on average.",
                "A correction that sits to one side is the fuel table being wrong by that much: the "
                + "controller is covering for it on every drive, and has less room left to cover "
                + "for anything else. Moving the table by this amount is a measurement rather than "
                + "a guess.",
                numbers, values.Count);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Closed loop",
                "The closed loop is working and has little to do.",
                $"Correction averages {mean:0.0}%, which means the table is close enough that the "
                + "loop is trimming rather than propping it up.",
                numbers, values.Count);
        }
    }

    /// <summary>
    /// How much injector there is left.
    ///
    /// Duty cycle is the one number that says whether the fuel system can still
    /// answer. Past about 85 per cent an injector stops being linear, and at a
    /// hundred it is simply open — at which point more fuel cannot be delivered
    /// however lean the mixture goes, which is where engines are lost.
    /// </summary>
    private static IEnumerable<LogInsight> InjectorHeadroom(Engine e)
    {
        if (e.Duty is not { } duty) yield break;

        var values = new List<double>();

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            double v = duty.At(i);
            if (!double.IsNaN(v) && v >= 0) values.Add(v);
        }

        if (values.Count < MinimumSamples) yield break;

        double peak = values.Max();
        double high = Percentile(values, 99);

        string numbers =
            $"peak {peak:0.0}%, 99th percentile {high:0.0}%, median {Percentile(values, 50):0.0}%, "
            + $"{values.Count:N0} running samples";

        if (peak >= 95)
        {
            yield return new LogInsight(
                InsightLevel.Warning, "Injectors",
                $"Injector duty reached {peak:0.0}%.",
                "At this duty the injectors are effectively open and cannot deliver more fuel "
                + "however lean the mixture goes. Anything asking for more — a hotter day, more "
                + "boost, a longer pull — leans out with no warning from the fuel system. Larger "
                + "injectors or more fuel pressure.",
                numbers, values.Count);
        }
        else if (peak >= 85)
        {
            yield return new LogInsight(
                InsightLevel.Watch, "Injectors",
                $"Injector duty reached {peak:0.0}%, which is the top of the linear range.",
                "Above about 85% an injector's delivery stops being proportional to the pulse it "
                + "is given, so the fuel table stops meaning what it says. There is room here, but "
                + "not much.",
                numbers, values.Count);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Injectors",
                $"Injector duty peaked at {peak:0.0}%.",
                $"Comfortably inside the linear range, with about {100 - peak:0} points spare.",
                numbers, values.Count);
        }
    }

    /// <summary>
    /// Whether supply voltage moved enough to matter to the injectors.
    ///
    /// An injector's dead time — the part of the pulse spent opening rather than
    /// flowing — is a function of voltage, and steeply so below about twelve
    /// volts. A log whose voltage swings a long way is a log where the dead-time
    /// table is doing real work, and where getting it wrong shows up as a
    /// mixture error that follows the alternator rather than the throttle.
    /// </summary>
    private static IEnumerable<LogInsight> DeadTimeAndVoltage(Engine e)
    {
        if (e.Battery is not { } battery) yield break;

        var values = new List<double>();

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            double v = battery.At(i);
            if (!double.IsNaN(v) && v > 4) values.Add(v);
        }

        if (values.Count < MinimumSamples) yield break;

        double low = Percentile(values, 1);
        double high = Percentile(values, 99);
        double swing = high - low;

        string numbers =
            $"1st percentile {low:0.0} V, 99th {high:0.0} V, median {Percentile(values, 50):0.0} V, "
            + $"{values.Count:N0} running samples";

        if (low < 11.5)
        {
            yield return new LogInsight(
                InsightLevel.Watch, "Charging and dead time",
                $"Supply fell to {low:0.0} V while running.",
                "Injector dead time climbs steeply below about twelve volts, so at this supply the "
                + "dead-time table is deciding a large part of the delivered fuel. Low voltage also "
                + "weakens the spark. Worth checking the alternator and the earths before chasing "
                + "any mixture error.",
                numbers, values.Count);
        }
        else if (swing > 2)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Charging and dead time",
                $"Supply swung {swing:0.0} V across the log.",
                "A swing this wide means the dead-time table is doing real work. If mixture errors "
                + "in this log track the voltage rather than the load, that table is where to look "
                + "rather than the fuel table.",
                numbers, values.Count);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Charging and dead time",
                $"Supply held between {low:0.0} and {high:0.0} V.",
                "Steady enough that injector dead time is near constant, so a mixture error in this "
                + "log is the fuel table rather than the charging system.",
                numbers, values.Count);
        }
    }

    /// <summary>Knock, which is the one finding that is worth interrupting for.</summary>
    private static IEnumerable<LogInsight> Knock(Engine e)
    {
        if (e.Knock is not { } knock) yield break;

        int events = 0;
        double worst = 0;
        int running = 0;
        double worstRpm = double.NaN, worstMap = double.NaN;

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            running++;

            double retard = knock.At(i);
            if (double.IsNaN(retard) || retard <= 0.1) continue;

            events++;

            if (retard > worst)
            {
                worst = retard;
                worstRpm = e.Rpm?.At(i) ?? double.NaN;
                worstMap = e.Map?.At(i) ?? double.NaN;
            }
        }

        if (running < MinimumSamples) yield break;

        if (events == 0)
        {
            yield return new LogInsight(
                InsightLevel.Good, "Knock",
                "No timing was pulled for knock anywhere in this log.",
                "The knock detection reported nothing across every running sample. Worth noting "
                + "that this says the controller heard nothing, not that nothing happened — a "
                + "detection threshold set too high is silent for the same reason.",
                $"0 of {running:N0} running samples", running);

            yield break;
        }

        string where = double.IsNaN(worstRpm)
            ? ""
            : $", worst at {worstRpm:N0} rpm"
              + (double.IsNaN(worstMap) ? "" : $" and {worstMap:N0} {e.Map?.Units}");

        yield return new LogInsight(
            InsightLevel.Warning, "Knock",
            $"Timing was pulled for knock on {events:N0} samples, up to {worst:0.0}°.",
            "Knock destroys pistons and ring lands, and the retard is the controller already "
            + "having decided this was real. Find where in the map it happened before driving it "
            + "again: usually timing, sometimes mixture, sometimes fuel quality.",
            $"{events:N0} of {running:N0} running samples ({100.0 * events / running:0.0}%){where}",
            events);
    }

    /// <summary>
    /// Whether the engine reached temperature, and whether warmup enrichment
    /// let go when it did.
    ///
    /// Enrichment that never returns to a hundred is fuel being added forever,
    /// which shows up as a rich idle nobody can tune out of the table.
    /// </summary>
    private static IEnumerable<LogInsight> Warmup(Engine e)
    {
        if (e.Coolant is not { } clt) yield break;

        bool fahrenheit = Engine.Fahrenheit(clt);
        double warm = fahrenheit ? 160 : 71;
        string unit = fahrenheit ? "°F" : "°C";

        double highest = double.MinValue;
        int running = 0;

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            running++;
            double v = clt.At(i);
            if (!double.IsNaN(v)) highest = Math.Max(highest, v);
        }

        if (running < MinimumSamples || highest == double.MinValue) yield break;

        if (highest < warm)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Warmup",
                $"The engine never reached operating temperature — it peaked at {highest:N0} {unit}.",
                $"Nothing measured here describes a warm engine. Fuelling below {warm:N0} {unit} is "
                + "warmup enrichment on top of the table, so any mixture conclusion from this log "
                + "is about the warmup curve rather than the fuel table.",
                $"peak {highest:N0} {unit} over {running:N0} running samples", running);

            yield break;
        }

        if (e.Warmup is not { } enrichment) yield break;

        double lastWarm = double.NaN;

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i) || double.IsNaN(clt.At(i)) || clt.At(i) < warm) continue;

            double v = enrichment.At(i);
            if (!double.IsNaN(v)) lastWarm = v;
        }

        if (double.IsNaN(lastWarm)) yield break;

        if (lastWarm > 101)
        {
            yield return new LogInsight(
                InsightLevel.Watch, "Warmup",
                $"Warmup enrichment is still adding {lastWarm - 100:0.#}% at operating temperature.",
                "The warmup curve should reach 100% by the time the engine is hot, or it adds fuel "
                + "for ever and no amount of fuel-table work will lean out a warm idle. The last "
                + "point of the curve is the one to look at.",
                $"{lastWarm:0.#}% while above {warm:N0} {unit}", running);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Warmup",
                "Warmup enrichment let go once the engine was hot.",
                $"It reached {lastWarm:0.#}% at operating temperature, so warm fuelling is the "
                + "table rather than the warmup curve.",
                $"{lastWarm:0.#}% while above {warm:N0} {unit}, peak {highest:N0} {unit}", running);
        }
    }

    /// <summary>
    /// Whether the manifold ever pulled vacuum.
    ///
    /// A MAP sensor that never reads below ambient is not plumbed to the
    /// manifold, and every load number in the log is then wrong in a way that
    /// looks entirely plausible.
    /// </summary>
    private static IEnumerable<LogInsight> ManifoldAgainstAmbient(Engine e)
    {
        if (e.Map is not { } map) yield break;

        (double ambient, bool measured) = Atmosphere(e, map);
        if (ambient <= 0) yield break;

        double lowest = double.MaxValue, highest = double.MinValue;
        int running = 0;

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            double v = map.At(i);
            if (double.IsNaN(v)) continue;

            running++;
            lowest = Math.Min(lowest, v);
            highest = Math.Max(highest, v);
        }

        if (running < MinimumSamples) yield break;

        string numbers =
            $"{lowest:N0}–{highest:N0} {map.Units} against ambient {ambient:N0}, "
            + $"{running:N0} running samples";

        if (lowest > ambient * 0.92)
        {
            yield return new LogInsight(
                InsightLevel.Warning, "Manifold pressure",
                $"Manifold pressure never fell below {lowest:N0} {map.Units}, with ambient at "
                + $"{ambient:N0}.",
                "A running engine pulls vacuum at idle and on the overrun. A sensor that never sees "
                + "any is either not connected to the manifold or has failed — and on speed "
                + "density every fuelling and timing number in this log is read from it, so they "
                + "are all wrong together and all look reasonable.",
                numbers, running);
        }
        else if (highest > ambient * 1.05)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Manifold pressure",
                $"The engine saw boost, peaking {highest - ambient:N0} {map.Units} above ambient.",
                "Load above atmospheric is where fuelling and timing errors stop being academic. "
                + "The mixture-under-load finding above covers this region.",
                numbers, running);
        }
        else if (!measured)
        {
            // Ambient was taken from the highest reading here, so "the top of
            // the range is ambient" is not an observation — it is the
            // assumption, restated. On a boosted engine it is also wrong, and
            // this is exactly the log where it would be believed.
            yield return new LogInsight(
                InsightLevel.Unanswered, "Manifold pressure",
                $"Manifold pressure spans {lowest:N0} to {highest:N0} {map.Units}, and this log "
                + "cannot say whether any of that is boost.",
                "Nothing here records atmospheric pressure: there is no barometer channel, and "
                + "the engine is never stopped with the key on, which is the other way to read "
                + "one. Without it the highest reading and ambient are indistinguishable — a "
                + "turbo at 250 kPa and a naturally aspirated engine at sea level look identical. "
                + "Log the controller's barometer, or start the next log before cranking.",
                numbers, running);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Manifold pressure",
                $"Manifold pressure spans {lowest:N0} to {highest:N0} {map.Units}.",
                "Vacuum at the bottom and near ambient at the top, which is what a healthy "
                + "naturally aspirated engine reads.",
                numbers, running);
        }
    }

    /// <summary>
    /// What the boost channel is measured against.
    ///
    /// <para>
    /// A gauge pressure is a difference from something, and firmwares disagree
    /// about what. Against the barometer, a reading of nought means the throttle
    /// is open and the engine is breathing; against a fixed 101.3 kPa, the same
    /// nought means something else entirely — and at 84 kPa of altitude the two
    /// differ by two and a half psi, quietly, in every number derived from
    /// either.
    /// </para>
    /// <para>
    /// It does not have to be assumed. Regressing the derived channel against
    /// the raw one gives the slope, which says the units, and the intercept,
    /// which says the reference.
    /// </para>
    /// </summary>
    private static IEnumerable<LogInsight> PressureReference(Engine e)
    {
        if (e.Map is not { } map || e.Boost is not { } boost) yield break;

        var xs = new List<double>();
        var ys = new List<double>();

        for (int i = 0; i < e.Count; i++)
        {
            double m = map.At(i), b = boost.At(i);
            if (!double.IsNaN(m) && !double.IsNaN(b)) { xs.Add(m); ys.Add(b); }
        }

        if (xs.Count < MinimumSamples) yield break;

        double n = xs.Count, sx = xs.Sum(), sy = ys.Sum();
        double sxx = xs.Sum(v => v * v);
        double sxy = xs.Zip(ys).Sum(pair => pair.First * pair.Second);

        double denominator = (n * sxx) - (sx * sx);
        if (Math.Abs(denominator) < 1e-9) yield break;

        double slope = ((n * sxy) - (sx * sy)) / denominator;
        if (Math.Abs(slope) < 1e-9) yield break;

        double intercept = (sy - (slope * sx)) / n;
        double reference = -intercept / slope;

        // Plenty of firmware declares no unit for this channel at all, and a
        // sentence with a hole where the unit should be reads worse than one
        // written without it.
        string unit = boost.Units.Length > 0 ? " " + boost.Units : "";

        double ambient = Ambient(e, map);
        bool againstBaro = ambient > 0 && Math.Abs(reference - ambient) < 2;
        bool againstSeaLevel = Math.Abs(reference - 101.3) < 2;

        string numbers =
            $"slope {slope:0.0000}{unit} per {map.Units}, zero at {reference:N1} "
            + $"{map.Units}, barometer reads {ambient:N1}, {xs.Count:N0} samples";

        if (againstBaro && ambient > 0 && Math.Abs(ambient - 101.3) > 3)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Pressure reference",
                $"\"{boost.Name}\" is measured against the barometer, not against sea level.",
                $"Its zero sits at {reference:N1} {map.Units}, which is this log's own ambient "
                + $"rather than 101.3. Anything read from it assuming sea level is out by "
                + $"{Math.Abs((101.3 - reference) * slope):0.0}{unit} throughout — a "
                + "difference that is invisible in the trace and survives every average.",
                numbers, xs.Count);
        }
        else if (againstSeaLevel && ambient > 0 && Math.Abs(ambient - 101.3) > 3)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Pressure reference",
                $"\"{boost.Name}\" is measured against sea level, not against the barometer.",
                $"Its zero sits at 101.3 {map.Units} while the barometer reads {ambient:N1}, so at "
                + "this altitude it reads negative with the throttle wide open. Correct, and worth "
                + "knowing before comparing it with anything from another day or another place.",
                numbers, xs.Count);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Pressure reference",
                $"\"{boost.Name}\" is a plain gauge pressure with its zero at {reference:N1} "
                + $"{map.Units}.",
                "Checked rather than assumed: the reference is read off the log by regressing the "
                + "derived channel against the raw one, so nothing here rests on a guess about "
                + "which convention this firmware uses.",
                numbers, xs.Count);
        }
    }

    /// <summary>
    /// How steady the idle is, as a standard deviation rather than an
    /// impression.
    ///
    /// Hunting is the commonest thing a tuner is asked about and the hardest to
    /// judge from a trace, because a plot's vertical scale flatters or damns it
    /// depending on nothing at all.
    /// </summary>
    private static IEnumerable<LogInsight> IdleSteadiness(Engine e)
    {
        if (e.Rpm is not { } rpm) yield break;

        var idle = new List<double>();

        // How long the engine has been below the idle ceiling without leaving
        // it, in samples.
        int settled = 0;

        for (int i = 0; i < e.Count; i++)
        {
            double v = rpm.At(i);

            if (!e.Running(i) || double.IsNaN(v) || v >= 1500
                || (e.Throttle is { } tps && tps.At(i) > 3))
            {
                settled = 0;
                continue;
            }

            // Below 1,500 with the throttle shut is idle *or* the tail of an
            // overrun, and the two are nothing alike. Coming down from 4,000 the
            // engine sweeps through this band in a second or two, and every one
            // of those samples used to count as idle — so on any road log the
            // spread was dominated by decelerations, and a perfectly steady idle
            // was reported as hunting by a couple of hundred rpm.
            //
            // The comment here claimed since it was written that the overrun was
            // excluded by requiring the engine not to be dropping. It was not.
            // It is now — by waiting for the engine to have been down here a
            // while, rather than by discarding falling samples, which would
            // throw away half of a genuine hunt and hide the thing being looked
            // for.
            settled++;

            if (settled * Math.Max(e.Spacing, 0.001) < 2.0) continue;

            idle.Add(v);
        }

        if (idle.Count < MinimumSamples * 2)
        {
            yield return Unanswered(
                "Idle",
                "Not enough closed-throttle running to judge the idle.",
                $"{idle.Count} samples had the throttle shut below 1,500 rpm. Half a minute of "
                + "idling makes this measurable.", idle.Count);

            yield break;
        }

        double mean = idle.Average();
        double sd = Math.Sqrt(idle.Sum(v => (v - mean) * (v - mean)) / idle.Count);

        string numbers =
            $"{mean:N0} rpm ± {sd:N0} (standard deviation), {idle.Min():N0}–{idle.Max():N0}, "
            + $"{idle.Count:N0} closed-throttle samples";

        if (sd > 100)
        {
            yield return new LogInsight(
                InsightLevel.Watch, "Idle",
                $"The idle is hunting: {mean:N0} rpm, wandering ±{sd:N0}.",
                "A standard deviation this wide is a visible, audible hunt rather than ordinary "
                + "variation. Usual causes are idle control fighting the throttle stop, a vacuum "
                + "leak, or idle timing correction switched off so nothing damps a stumble.",
                numbers, idle.Count);
        }
        else if (sd > 50)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Idle",
                $"The idle wanders a little: {mean:N0} rpm ± {sd:N0}.",
                "Noticeable but not objectionable. Worth revisiting once the mixture at idle is "
                + "settled, since fuelling and idle stability chase each other.",
                numbers, idle.Count);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Idle",
                $"The idle is steady at {mean:N0} rpm.",
                $"It holds to within ±{sd:N0} rpm, which is as steady as a carburetted engine is "
                + "expected to be and better than most.",
                numbers, idle.Count);
        }
    }

    /// <summary>
    /// Channels that never moved.
    ///
    /// A sensor reading a constant is either not fitted or has failed, and a
    /// flat trace is easy to miss among forty others. Only channels that ought
    /// to move are reported.
    /// </summary>
    private static IEnumerable<LogInsight> StuckChannels(Engine e)
    {
        (string What, LogChannel? Channel)[] shouldMove =
        [
            ("engine speed", e.Rpm),
            ("manifold pressure", e.Map),
            ("throttle", e.Throttle),
            ("coolant temperature", e.Coolant),
            ("the wideband", e.Afr),
            ("battery voltage", e.Battery),
        ];

        var stuck = new List<string>();

        foreach ((string what, LogChannel? channel) in shouldMove)
        {
            if (channel is null) continue;

            double first = double.NaN;
            bool moved = false;
            int seen = 0;

            for (int i = 0; i < e.Count; i++)
            {
                double v = channel.At(i);
                if (double.IsNaN(v)) continue;

                seen++;
                if (double.IsNaN(first)) first = v;
                else if (Math.Abs(v - first) > 1e-9) { moved = true; break; }
            }

            if (seen >= MinimumSamples && !moved)
                stuck.Add($"{what} (\"{channel.Name}\") at {first:0.##} {channel.Units}".TrimEnd());
        }

        if (stuck.Count == 0) yield break;

        yield return new LogInsight(
            InsightLevel.Warning, "Sensors",
            $"{stuck.Count} channel{(stuck.Count == 1 ? "" : "s")} never changed value.",
            "A reading that never moves across a whole log is a sensor that is not connected, not "
            + "powered, or has failed — and anything the controller worked out from it is wrong "
            + "without looking wrong. " + string.Join("; ", stuck) + ".",
            string.Join("; ", stuck), e.Count);
    }

    /// <summary>
    /// Whether the log was recorded evenly.
    ///
    /// Gaps matter beyond tidiness: a rate that collapses during the interesting
    /// part is a log that did not record the interesting part, and every average
    /// above is then weighted towards whatever the link kept up with.
    /// </summary>
    private static IEnumerable<LogInsight> SampleRate(Engine e)
    {
        var gaps = new List<double>();

        for (int i = 1; i < e.Count; i++)
        {
            double dt = e.Log.Time.At(i) - e.Log.Time.At(i - 1);
            if (!double.IsNaN(dt) && dt > 0) gaps.Add(dt);
        }

        if (gaps.Count < MinimumSamples) yield break;

        double median = Percentile(gaps, 50);
        double worst = gaps.Max();
        double rate = median > 0 ? 1 / median : 0;

        // A gap of five times the usual spacing is a stall rather than jitter.
        int stalls = gaps.Count(g => g > median * 5);

        string numbers =
            $"{rate:0.#} Hz typical, longest gap {worst:0.###} s, "
            + $"{stalls:N0} gap{(stalls == 1 ? "" : "s")} over five times the usual spacing, "
            + $"{gaps.Count + 1:N0} samples";

        if (stalls > 0 && worst > 1)
        {
            yield return new LogInsight(
                InsightLevel.Watch, "Recording",
                $"The log stalled {stalls:N0} time{(stalls == 1 ? "" : "s")}, the longest for "
                + $"{worst:0.#} seconds.",
                "Whatever the engine did during those gaps was not recorded, so every average here "
                + "describes the parts the link kept up with. If the gaps line up with hard "
                + "acceleration, the most interesting samples are the missing ones.",
                numbers, gaps.Count + 1);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Recording",
                $"Recorded evenly at about {rate:0.#} Hz.",
                "No meaningful gaps, so the averages here are weighted the way the driving was.",
                numbers, gaps.Count + 1);
        }
    }

    /// <summary>
    /// How far behind the wideband is.
    ///
    /// A wideband reads the exhaust some distance and some milliseconds after
    /// the event that caused it. Tuning a table cell against a reading that
    /// belongs to a different cell is the commonest way to chase a fault that
    /// is not there, and the delay is measurable rather than a matter of taste.
    /// </summary>
    private static IEnumerable<LogInsight> MixtureDelay(Engine e)
    {
        if (e.Afr is not { } afr || e.Target is not { } target) yield break;
        if (e.Rpm is not { } rpm || e.Map is not { } map) yield break;
        if (e.Count < 200) yield break;

        // A grid to bin against, built from the log's own spread rather than
        // from a tune: the delay is a property of the exhaust and the sensor,
        // and asking for it should not require the controller's table.
        TuneTable grid = VeAnalysis.GridFrom(rpm, map, 8, 8, 0, e.Count - 1);

        double interval = SampleInterval(e);
        if (interval <= 0) yield break;

        DelaySearchResult found = WidebandDelay.Find(
            grid, rpm, map, afr, target, 0, e.Count - 1, interval);

        if (found.HasProblem || found.NoneIsPlausible)
        {
            yield return Unanswered(
                "Wideband delay",
                "This log cannot say how far behind the wideband reads.",
                found.Problem is { Length: > 0 } why
                    ? why
                    : "No delay fitted better than none did, which usually means the engine held "
                      + "too steady for the measurement to have anything to work on.",
                found.SamplesScored);

            yield break;
        }

        string numbers =
            $"{found.BestSeconds:0.00} s, credible between {found.LowSeconds:0.00} and "
            + $"{found.HighSeconds:0.00} s, over {found.SamplesScored:N0} scored samples";

        if (found.BestSeconds >= 0.15)
        {
            yield return new LogInsight(
                InsightLevel.Note, "Wideband delay",
                $"The wideband reads about {found.BestSeconds:0.00} s behind the engine.",
                "Worth aligning before reading anything cell by cell: at 3,000 rpm this is dozens "
                + "of engine cycles, so a mixture reading can belong to a different part of the "
                + "map than the one it appears against. VE Calibration can take this delay into "
                + "account.",
                numbers, found.SamplesScored);
        }
        else
        {
            yield return new LogInsight(
                InsightLevel.Good, "Wideband delay",
                $"The wideband is nearly in step, about {found.BestSeconds:0.00} s behind.",
                "Small enough that readings line up with the cells that caused them.",
                numbers, found.SamplesScored);
        }
    }

    /// <summary>The usual spacing between samples, in seconds.</summary>
    private static double SampleInterval(Engine e)
    {
        var gaps = new List<double>();

        for (int i = 1; i < e.Count; i++)
        {
            double dt = e.Log.Time.At(i) - e.Log.Time.At(i - 1);
            if (!double.IsNaN(dt) && dt > 0) gaps.Add(dt);
        }

        return gaps.Count == 0 ? 0 : Percentile(gaps, 50);
    }

    /// <summary>
    /// How much of the operating range the log actually visited.
    ///
    /// Every conclusion above is drawn from where the engine was, and a log that
    /// only idled cannot say anything about anything else. Said plainly, so a
    /// clean bill of health is not read as more than it is.
    /// </summary>
    private static IEnumerable<LogInsight> Coverage(Engine e)
    {
        if (e.Rpm is not { } rpm || e.Map is not { } map) yield break;

        int running = 0;
        var seen = new HashSet<(int, int)>();
        double topRpm = 0;

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            double r = rpm.At(i), m = map.At(i);
            if (double.IsNaN(r) || double.IsNaN(m)) continue;

            running++;
            topRpm = Math.Max(topRpm, r);

            // A coarse grid: five hundred rpm by twenty units of load, which is
            // about the resolution a tuner thinks in.
            seen.Add(((int)(r / 500), (int)(m / 20)));
        }

        if (running < MinimumSamples) yield break;

        double minutes = (e.Log.Time.At(e.Count - 1) - e.Log.Time.At(0)) / 60.0;

        yield return new LogInsight(
            seen.Count < 6 ? InsightLevel.Note : InsightLevel.Good,
            "Coverage",
            seen.Count < 6
                ? $"This log visited only {seen.Count} regions of the map."
                : $"This log covered {seen.Count} regions of the map.",
            seen.Count < 6
                ? "Everything above is drawn from a narrow slice of the engine's range. A clean "
                  + "result here does not say the rest of the map is clean — it says the rest was "
                  + "never visited."
                : "Enough spread that the findings above rest on more than one corner of the map.",
            $"{seen.Count} distinct 500 rpm × 20 {map.Units} regions, up to {topRpm:N0} rpm, "
            + $"{running:N0} running samples over {minutes:0.#} minutes",
            running);
    }

    // ----- the arithmetic ----------------------------------------------------

    /// <summary>
    /// A word about the filter itself, where it kept an implausible share.
    ///
    /// A steady-state stack on a varied drive keeps roughly two frames in five.
    /// Keeping almost none means it is rejecting the drive rather than the
    /// transients; keeping almost all means it is not filtering at all. Either
    /// way the reader should know before believing the number in front of it.
    /// </summary>
    private static string Survival(double kept) =>
        kept < 5
            ? " Only a twentieth of the running samples survived the steadiness test, so this "
              + "rests on a narrow slice of the drive."
            : kept > 90
                ? " Almost every running sample survived the steadiness test, which suggests the "
                  + "engine was held at one point throughout rather than driven."
                : "";

    private static LogInsight Unanswered(string topic, string title, string detail, int samples) =>
        new(InsightLevel.Unanswered, topic, title, detail, "", samples);

    /// <summary>
    /// The mean, and how well the mean is known.
    ///
    /// The standard error is the whole point: it is what turns "the average is
    /// 0.3 lean" into either "and that is twenty times the uncertainty" or "and
    /// the uncertainty is 0.4, so this says nothing".
    /// </summary>
    private static (double Mean, double StandardError) MeanAndStandardError(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (double.NaN, double.NaN);
        if (values.Count == 1) return (values[0], double.PositiveInfinity);

        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);

        return (mean, Math.Sqrt(variance / values.Count));
    }

    /// <summary>
    /// A percentile by linear interpolation between the two nearest samples.
    ///
    /// Used wherever one bad sample decides the answer — the lean excursion that
    /// hurts an engine is in the tail, and a mean is exactly the statistic that
    /// hides a tail.
    /// </summary>
    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return double.NaN;

        double[] sorted = [.. values.Order()];
        if (sorted.Length == 1) return sorted[0];

        double at = Math.Clamp(percentile, 0, 100) / 100.0 * (sorted.Length - 1);
        int below = (int)Math.Floor(at);
        int above = Math.Min(below + 1, sorted.Length - 1);

        return sorted[below] + ((sorted[above] - sorted[below]) * (at - below));
    }

    /// <summary>
    /// Atmospheric pressure, from the controller's own barometer where it logs
    /// one and from the highest manifold pressure seen otherwise.
    ///
    /// It has to come from the log rather than from a constant: this application
    /// is used at 84 kPa as readily as at sea level, and a boost threshold set
    /// at 101 would call every altitude engine boosted.
    /// </summary>
    private static double Ambient(Engine e, LogChannel map) => Atmosphere(e, map).Pressure;

    /// <summary>
    /// Where "loaded" begins when atmospheric is not known: the upper third of
    /// the manifold pressure this log actually saw while running.
    ///
    /// Relative rather than absolute, because without a real ambient figure
    /// there is no absolute to be had — and a threshold taken from the log's own
    /// spread still separates the pulls from the cruising, which is all this
    /// finding needs it to do.
    /// </summary>
    private static double LoadedFrom(Engine e, LogChannel map)
    {
        double lowest = double.MaxValue, highest = double.MinValue;

        for (int i = 0; i < e.Count; i++)
        {
            if (!e.Running(i)) continue;

            double v = map.At(i);
            if (double.IsNaN(v)) continue;

            lowest = Math.Min(lowest, v);
            highest = Math.Max(highest, v);
        }

        if (lowest > highest) return double.MaxValue;

        return lowest + ((highest - lowest) * 2 / 3);
    }

    /// <summary>
    /// Atmospheric pressure, and whether it was measured or merely assumed.
    ///
    /// <para>
    /// <b>The assumption is the dangerous half.</b> Falling back to the highest
    /// manifold pressure is sound on an engine that cannot make boost — at wide
    /// open throttle a naturally aspirated manifold sits at atmospheric — and
    /// nonsense on one that can, where the highest reading <em>is</em> the boost.
    /// Taken for ambient it makes a turbo log test <c>highest &gt; ambient *
    /// 1.05</c> against itself, which can never be true, so the engine is
    /// reported as a healthy naturally aspirated one; and it puts the
    /// loaded-mixture threshold at nine tenths of peak boost, excluding nearly
    /// all the running that check exists to judge.
    /// </para>
    /// <para>
    /// So it is now marked as an assumption, and whatever cannot be said
    /// honestly without a real figure declines to say it.
    /// </para>
    /// </summary>
    private static (double Pressure, bool Measured) Atmosphere(Engine e, LogChannel map)
    {
        // A barometer, in whatever it was logged in. The old test — greater than
        // fifty — was a bare-kilopascal assumption that silently threw away any
        // baro in psi (about 14.7) or bar (about 1.01), which is every log from
        // a controller set to imperial units.
        if (e.Baro is { } baro)
        {
            // Converted into the manifold's units rather than left in its own.
            // Every use of this compares it against manifold pressure, and a
            // barometer logged in psi beside a manifold logged in kPa reads as
            // 14.7 against 35 — so the engine is reported as one whose sensor
            // never sees vacuum, a fault it does not have.
            double perUnit = KilopascalsPer(baro.Units);
            double intoMap = perUnit / KilopascalsPer(map.Units);

            for (int i = 0; i < e.Count; i++)
            {
                double v = baro.At(i);
                if (double.IsNaN(v)) continue;

                // A plausible atmosphere anywhere a car is driven: the Dead Sea
                // reads about 106 kPa and the highest road in the world about 45.
                if (v * perUnit is > 40 and < 115) return (v * intoMap, true);
            }
        }

        // Failing that, the manifold with the engine stopped. A stationary
        // engine's manifold is open to the atmosphere through the throttle, so
        // this is a reading rather than an inference — and most logs start with
        // the key on before anything is cranked.
        if (e.Rpm is { } rpm)
        {
            double stopped = double.NaN;

            for (int i = 0; i < e.Count; i++)
            {
                double turning = rpm.At(i);
                if (double.IsNaN(turning) || turning > 50) continue;

                double v = map.At(i);
                if (!double.IsNaN(v) && (double.IsNaN(stopped) || v > stopped)) stopped = v;
            }

            if (!double.IsNaN(stopped) && stopped > 0) return (stopped, true);
        }

        double highest = 0;

        for (int i = 0; i < e.Count; i++)
        {
            double v = map.At(i);
            if (!double.IsNaN(v)) highest = Math.Max(highest, v);
        }

        return (highest, false);
    }

    /// <summary>Kilopascals per unit of whatever a pressure was logged in.</summary>
    private static double KilopascalsPer(string units) =>
        ChannelRoles.Simplify(units) switch
        {
            "psi" or "psig" or "psia" => TuningMath.KpaPerPsi,
            "bar" => 100,
            "mbar" or "hpa" => 0.1,
            "inhg" => 3.386389,
            _ => 1,
        };
}
