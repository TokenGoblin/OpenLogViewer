using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Finding the sensor's delay from the log.
///
/// The tests that matter are the ones that build a drive with a known delay in
/// it and check that the number comes back — and the ones that make sure a log
/// with nothing to learn from is reported as such rather than resolved into
/// whichever candidate the noise favoured.
/// </summary>
public class WidebandDelayTests
{
    private const double Interval = 0.05;   // 20 Hz

    private static TuneTable Grid(int columns = 8, int rows = 4)
    {
        var x = new double[columns];
        for (int i = 0; i < columns; i++) x[i] = 1000 + (i * 500);

        var y = new double[rows];
        for (int i = 0; i < rows; i++) y[i] = 30 + (i * 40);

        return new TuneTable(
            "VE", new TuneAxis("rpm", "RPM", x), new TuneAxis("map", "kPa", y),
            new double[columns, rows], "%");
    }

    /// <summary>
    /// A drive that moves around the map, where the mixture each operating point
    /// produces is a fixed property of that point — so a reading identifies the
    /// point that caused it — and the wideband reports it <paramref name="delay"/>
    /// samples later.
    /// </summary>
    private static (LogChannel Rpm, LogChannel Load, LogChannel Afr, LogChannel Target) Drive(
        int delay, int samples = 3000, int seed = 11, double noise = 0)
    {
        var rng = new Random(seed);
        var rpm = new double[samples];
        var load = new double[samples];
        var afr = new double[samples];
        var target = new double[samples];

        // The mixture each cell produces, fixed for the run.
        var trueAfr = new double[samples];

        double r = 2000, l = 60;

        for (int i = 0; i < samples; i++)
        {
            // Wander, with occasional sharp steps — the transients are what
            // carry the information a delay can be found from.
            if (i % 60 == 0)
            {
                r = 1200 + (rng.NextDouble() * 3000);
                l = 35 + (rng.NextDouble() * 120);
            }
            else
            {
                r += (rng.NextDouble() - 0.5) * 40;
                l += (rng.NextDouble() - 0.5) * 3;
            }

            rpm[i] = r;
            load[i] = l;
            target[i] = 13.5;

            // A mixture that depends on where the engine is, so which reading
            // belongs to which moment is answerable at all.
            trueAfr[i] = 12.0 + ((r - 1200) / 3000 * 2.0) + ((l - 35) / 120 * 1.5)
                         + ((rng.NextDouble() - 0.5) * noise);
        }

        // The sensor reports what happened `delay` samples ago.
        for (int i = 0; i < samples; i++) afr[i] = trueAfr[Math.Max(0, i - delay)];

        return (
            new LogChannel("RPM", "rpm", 0, rpm),
            new LogChannel("MAP", "kPa", 0, load),
            new LogChannel("AFR", "", 2, afr),
            new LogChannel("Target", "", 2, target));
    }

    private static DelaySearchResult Find(
        int delay, int samples = 3000, double noise = 0, SampleMask? mask = null)
    {
        (LogChannel rpm, LogChannel load, LogChannel afr, LogChannel target) =
            Drive(delay, samples, noise: noise);

        return WidebandDelay.Find(
            Grid(), rpm, load, afr, target, 0, samples - 1, Interval, mask);
    }

    // ----- finding a delay that is there ------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(10)]
    public void ADelayPutIntoTheLogIsFoundAgain(int delay)
    {
        DelaySearchResult result = Find(delay);

        Assert.False(result.HasProblem, result.Problem);
        Assert.Equal(delay, result.BestSamples);
        Assert.Equal(delay * Interval, result.BestSeconds, precision: 6);
    }

    [Fact]
    public void AnUndelayedSensorIsAnAnswerRatherThanAShrug()
    {
        // Zero improvement over zero delay and a flat log both look like "no
        // delay helps", but only one of them is uninformative. Here the sweep
        // climbs steeply away from zero, which is what says zero is the answer.
        DelaySearchResult result = Find(delay: 0);

        Assert.False(result.HasProblem, result.Problem);
        Assert.Equal(0, result.BestSamples);

        // It got worse as the delay grew, which is the evidence for none.
        Assert.True(result.Curve[^1].Disagreement > result.Curve[0].Disagreement);
    }

    [Fact]
    public void TheAnswerSurvivesNoiseOnTheReadings()
    {
        // A wideband is not a clean instrument. The alignment should still be
        // recoverable through a reading that wanders either side of the truth.
        DelaySearchResult result = Find(delay: 5, noise: 0.6);

        Assert.False(result.HasProblem, result.Problem);
        Assert.InRange(result.BestSamples, 4, 6);
    }

    [Fact]
    public void ADelayWorthFindingIsWorthReporting()
    {
        DelaySearchResult result = Find(delay: 6);

        // The whole point of the number: it is much better than assuming none.
        Assert.True(result.ImprovementPercent > 20,
            $"improvement was only {result.ImprovementPercent:F1}%");
    }

    [Fact]
    public void TheCurveIsReportedSoTheShapeCanBeSeen()
    {
        DelaySearchResult result = Find(delay: 4);

        Assert.NotEmpty(result.Curve);
        Assert.Equal(0, result.Curve[0].Samples);

        // Ascending, one entry per candidate, and each carrying its own time.
        Assert.True(result.Curve.Zip(result.Curve.Skip(1)).All(p => p.Second.Samples > p.First.Samples));
        Assert.All(result.Curve, c => Assert.Equal(c.Samples * Interval, c.Seconds, precision: 6));
    }

    [Fact]
    public void TheMinimumIsARealMinimumRatherThanTheEndOfTheSweep()
    {
        DelaySearchResult result = Find(delay: 5);

        int best = result.Curve.TakeWhile(c => c.Samples != result.BestSamples).Count();

        Assert.True(best > 0, "the best candidate should not be the first");
        Assert.True(best < result.Curve.Count - 1, "nor the last");

        // Disagreement rises either side of it, which is what makes it an answer.
        Assert.True(result.Curve[best - 1].Disagreement > result.Curve[best].Disagreement);
        Assert.True(result.Curve[best + 1].Disagreement > result.Curve[best].Disagreement);
    }

    // ----- refusing to answer -----------------------------------------------

    [Fact]
    public void ALogHeldAtOneOperatingPointCannotSayAndDoesNotPretendTo()
    {
        // Every delay pairs a cell with readings from the same conditions, so
        // none can be told from another. Returning the minimum of that sweep
        // would be inventing a measurement.
        int samples = 3000;
        var rpm = new double[samples];
        var load = new double[samples];
        var afr = new double[samples];
        var target = new double[samples];

        var rng = new Random(3);
        for (int i = 0; i < samples; i++)
        {
            rpm[i] = 2500 + ((rng.NextDouble() - 0.5) * 20);
            load[i] = 80 + ((rng.NextDouble() - 0.5) * 2);
            afr[i] = 13.0 + ((rng.NextDouble() - 0.5) * 0.4);
            target[i] = 13.0;
        }

        DelaySearchResult result = WidebandDelay.Find(
            Grid(), new LogChannel("RPM", "rpm", 0, rpm), new LogChannel("MAP", "kPa", 0, load),
            new LogChannel("AFR", "", 2, afr), new LogChannel("Target", "", 2, target),
            0, samples - 1, Interval);

        Assert.True(result.HasProblem);
        Assert.Contains("cannot say", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void TooFewSamplesIsRefusedRatherThanAnswered()
    {
        DelaySearchResult result = Find(delay: 4, samples: 120);

        Assert.True(result.HasProblem);
        Assert.Contains("too few", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogTooShortToHoldTheSweepIsRefused()
    {
        DelaySearchResult result = WidebandDelay.Find(
            Grid(), new LogChannel("RPM", "rpm", 0, [1000, 2000]),
            new LogChannel("MAP", "kPa", 0, [40, 50]),
            new LogChannel("AFR", "", 2, [13, 13]), new LogChannel("Target", "", 2, [13, 13]),
            0, 1, Interval);

        Assert.True(result.HasProblem);
    }

    [Fact]
    public void AMinimumOnTheLastCandidateIsReportedAsSuspect()
    {
        // A delay longer than the sweep looks like a fit that never stopped
        // improving. That is the edge of where we looked, not a minimum.
        DelaySearchResult result = Find(delay: 30);   // 1.5 s, past the 1.0 s ceiling

        Assert.True(result.HasProblem);
        Assert.Contains("still improving", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSweepStopsAtTheCeilingHoweverLongTheLogIs()
    {
        DelaySearchResult result = Find(delay: 4, samples: 20000);

        Assert.All(result.Curve, c => Assert.True(
            c.Seconds <= WidebandDelay.MaximumSeconds + 1e-9,
            $"candidate at {c.Seconds} s is past the ceiling"));
    }

    [Fact]
    public void ZeroSampleIntervalIsRefusedRatherThanDividedBy()
    {
        (LogChannel rpm, LogChannel load, LogChannel afr, LogChannel target) = Drive(4);

        DelaySearchResult result = WidebandDelay.Find(
            Grid(), rpm, load, afr, target, 0, 2999, 0);

        Assert.True(result.HasProblem);
    }

    // ----- what it is and is not about --------------------------------------

    [Fact]
    public void AMistunedEngineAlignsJustAsWell()
    {
        // This is signal alignment, not tuning. Every cell is judged against its
        // own readings, never against the target, so how far out the tune is
        // does not enter into it.
        (LogChannel rpm, LogChannel load, LogChannel afr, LogChannel target) = Drive(5);

        var lean = new LogChannel(
            "Target", "", 2, [.. Enumerable.Repeat(9.0, target.Length)]);

        DelaySearchResult tuned = WidebandDelay.Find(
            Grid(), rpm, load, afr, target, 0, 2999, Interval);

        DelaySearchResult mistuned = WidebandDelay.Find(
            Grid(), rpm, load, afr, lean, 0, 2999, Interval);

        Assert.Equal(tuned.BestSamples, mistuned.BestSamples);
    }

    [Fact]
    public void FilteredSamplesAreLeftOutOfTheSearch()
    {
        // A sample the user excluded must not decide the delay, on either side
        // of the pairing.
        var accepted = new bool[3000];
        for (int i = 0; i < accepted.Length; i++) accepted[i] = i < 1500;

        var mask = new SampleMask
        {
            Accepted = accepted,
            FiltersApplied = true,
            Total = 3000,
            PassCount = 1500,
            UnknownChannels = [],
        };

        DelaySearchResult result = Find(delay: 5, mask: mask);

        Assert.False(result.HasProblem, result.Problem);
        Assert.Equal(5, result.BestSamples);
        Assert.True(result.SamplesScored <= 1500);
    }

    [Fact]
    public void EveryCandidateIsScoredOverTheSameSamples()
    {
        // Otherwise each candidate is scored on a slightly different drive, and
        // the comparison between them is the whole answer.
        DelaySearchResult result = Find(delay: 4);

        Assert.True(result.SamplesScored > 0);
        Assert.All(result.Curve, c => Assert.True(double.IsFinite(c.Disagreement)));
    }

    // ----- how wide the answer really is ------------------------------------

    [Fact]
    public void TheBandCoversTheCandidatesThatFitAboutAsWell()
    {
        DelaySearchResult result = Find(delay: 5);

        Assert.False(result.HasProblem, result.Problem);
        Assert.InRange(result.BestSeconds, result.LowSeconds, result.HighSeconds);
    }

    [Fact]
    public void ACleanLogPinsTheDelayDown()
    {
        // No noise on the readings, plenty of transients: the minimum is sharp
        // and the band should close on it.
        DelaySearchResult result = Find(delay: 5);

        Assert.True(result.IsPrecise, $"band was {result.LowSeconds}..{result.HighSeconds} s");
        Assert.False(result.NoneIsPlausible);
    }

    [Fact]
    public void ANoisyLogWidensTheBandRatherThanPretendingToPrecision()
    {
        // The same delay, read through a noisier sensor. The answer is no longer
        // a point, and saying so is the difference between a measurement and a
        // number that merely looks like one.
        DelaySearchResult clean = Find(delay: 5);
        DelaySearchResult noisy = Find(delay: 5, noise: 2.5);

        Assert.True(
            noisy.HighSeconds - noisy.LowSeconds >= clean.HighSeconds - clean.LowSeconds,
            $"noisy band {noisy.LowSeconds}..{noisy.HighSeconds}, "
            + $"clean {clean.LowSeconds}..{clean.HighSeconds}");
    }

    [Fact]
    public void TheBandIsNarrowerWithMoreEvidence()
    {
        // Tolerance falls as the square root of the sample count, so a longer
        // drive should pin the same delay down more tightly.
        DelaySearchResult brief = Find(delay: 5, samples: 1200, noise: 2.5);
        DelaySearchResult ample = Find(delay: 5, samples: 12000, noise: 2.5);

        Assert.True(
            ample.HighSeconds - ample.LowSeconds <= brief.HighSeconds - brief.LowSeconds,
            $"12,000 samples gave {ample.LowSeconds}..{ample.HighSeconds}, "
            + $"1,200 gave {brief.LowSeconds}..{brief.HighSeconds}");
    }

    [Fact]
    public void AnUndelayedSensorHasNoneInsideItsBand()
    {
        DelaySearchResult result = Find(delay: 0);

        Assert.True(result.NoneIsPlausible);
        Assert.Equal(0, result.LowSeconds);
    }
}
