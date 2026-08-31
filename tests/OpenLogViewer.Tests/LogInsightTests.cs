using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// What a log is told to say about the engine that produced it.
///
/// The tests that matter are the ones about restraint: a finding made from nine
/// samples, or a warning raised on noise, is worse than no finding at all —
/// a tuner who is sent chasing something that is not there stops reading these.
/// </summary>
public class LogInsightTests
{
    private static LogChannel Channel(string name, string units, params double[] values) =>
        new(name, units, 2, values);

    /// <summary>
    /// A log of steady running: the given AFR against a 14.7 target, at 3,000
    /// rpm and 90 kPa, warm, throttle still, at 10 Hz.
    /// </summary>
    private static LogDocument Steady(
        double[] afr, double[]? target = null, double[]? map = null, double[]? rpm = null,
        double clt = 190, double[]? extra = null, string extraName = "", string extraUnits = "")
    {
        int n = afr.Length;

        var channels = new List<LogChannel>
        {
            Channel("RPM", "RPM", rpm ?? [.. Enumerable.Repeat(3000.0, n)]),
            Channel("MAP", "kPa", map ?? [.. Enumerable.Repeat(90.0, n)]),
            Channel("AFR", "AFR", afr),
            Channel("AFR Target 1", "AFR", target ?? [.. Enumerable.Repeat(14.7, n)]),
            Channel("TPS", "%", [.. Enumerable.Repeat(30.0, n)]),
            Channel("CLT", "°F", [.. Enumerable.Repeat(clt, n)]),
            Channel("Barometer", "kPa", [.. Enumerable.Repeat(101.0, n)]),
        };

        if (extra is not null) channels.Add(Channel(extraName, extraUnits, extra));

        return new LogDocument
        {
            FormatName = "test",
            FilePath = "test",
            Time = Channel("Time", "s", [.. Enumerable.Range(0, n).Select(i => i * 0.1)]),
            Channels = channels,
        };
    }

    private static LogInsight? Topic(LogDocument log, string topic) =>
        LogInsights.From(log).FirstOrDefault(
            i => i.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase));

    private static double[] Repeat(double value, int n) => [.. Enumerable.Repeat(value, n)];

    // ----- restraint ---------------------------------------------------------

    [Fact]
    public void AHandfulOfSamplesIsNotEvidence()
    {
        // Nine samples badly lean would be a striking finding and a false one.
        LogInsight? mixture = Topic(Steady(Repeat(17.0, 9)), "Mixture");

        Assert.NotNull(mixture);
        Assert.Equal(InsightLevel.Unanswered, mixture!.Level);
        Assert.Contains("Not enough", mixture.Title);
    }

    [Fact]
    public void NoiseAroundTheTargetIsNotAFinding()
    {
        // Alternating either side of target: the mean is nought and the scatter
        // is real. A tool that called this an error would call everything one.
        double[] afr = [.. Enumerable.Range(0, 200).Select(i => 14.7 + (i % 2 == 0 ? 0.6 : -0.6))];

        LogInsight? mixture = Topic(Steady(afr), "Mixture");

        Assert.Equal(InsightLevel.Good, mixture!.Level);
        Assert.Contains("on target", mixture.Title);
    }

    [Fact]
    public void ASmallButRealErrorIsNotWorthChasing()
    {
        // Dead steady at a tenth lean: real beyond doubt, and below the
        // threshold where a tuner should spend an afternoon on it.
        LogInsight? mixture = Topic(Steady(Repeat(14.8, 200)), "Mixture");

        Assert.Equal(InsightLevel.Good, mixture!.Level);
    }

    [Fact]
    public void ARealErrorIsReportedWithItsUncertainty()
    {
        LogInsight? mixture = Topic(Steady(Repeat(15.7, 200)), "Mixture");

        Assert.Equal(InsightLevel.Watch, mixture!.Level);

        // Reported as a share of the fuel required rather than as a difference
        // between two AFR numbers, so cells with different targets can be
        // counted together.
        Assert.Contains("less fuel than asked for", mixture.Title);
        Assert.Contains("standard error", mixture.Evidence);
    }

    [Fact]
    public void RichIsSaidToBeRichRatherThanJustWrong()
    {
        LogInsight? mixture = Topic(Steady(Repeat(13.2, 200)), "Mixture");

        Assert.Equal(InsightLevel.Watch, mixture!.Level);
        Assert.Contains("more fuel than asked for", mixture.Title);
    }

    // ----- the map, against what the closed loop rescued ---------------------

    [Fact]
    public void TheClosedLoopsCorrectionIsFoldedBackIntoTheMapsError()
    {
        // The case that matters: the mixture is on target because the loop is
        // adding fuel, and the map underneath is badly lean. Read without
        // folding the correction back in, this looks perfect — and then
        // misbehaves the moment the loop drops out.
        //
        // Verified against a real MS3 log, where the mixture read 9% lean while
        // the loop added 8%, and the map was out by nineteen.
        LogDocument log = Steady(
            Repeat(14.7, 200), extra: Repeat(120.0, 200),
            extraName: "EGO cor1", extraUnits: "%");

        LogInsight? mixture = Topic(log, "Mixture");

        Assert.Equal(InsightLevel.Watch, mixture!.Level);
        Assert.Contains("less fuel than asked for", mixture.Title);

        // A loop adding a fifth is covering a map that is a sixth short:
        // 100/120 is 0.833, so 16.7% less than required.
        Assert.Contains("16.7%", mixture.Title);
        Assert.Contains("divided back out", mixture.Evidence);
    }

    [Fact]
    public void AMixtureOnTargetWithNoCorrectionIsAMapOnTarget()
    {
        LogDocument log = Steady(
            Repeat(14.7, 200), extra: Repeat(100.0, 200),
            extraName: "EGO cor1", extraUnits: "%");

        Assert.Equal(InsightLevel.Good, Topic(log, "Mixture")!.Level);
    }

    [Fact]
    public void ARichMixtureTheLoopIsPullingBackIsStillARichMap()
    {
        // Measured on target, loop removing a tenth: the map is a ninth rich.
        LogDocument log = Steady(
            Repeat(14.7, 200), extra: Repeat(90.0, 200),
            extraName: "EGO cor1", extraUnits: "%");

        LogInsight? mixture = Topic(log, "Mixture");

        Assert.Equal(InsightLevel.Watch, mixture!.Level);
        Assert.Contains("more fuel than asked for", mixture.Title);
        Assert.Contains("11.1%", mixture.Title);
    }

    // ----- what the boost channel is measured against ------------------------

    [Fact]
    public void TheBoostReferenceIsReadOffTheLogRatherThanAssumed()
    {
        // A gauge pressure is a difference from something and firmwares
        // disagree about what. At altitude the two conventions differ by two
        // and a half psi, silently, in everything derived from either.
        int n = 200;
        double[] map = [.. Enumerable.Range(0, n).Select(i => 30.0 + (i % 100))];
        double[] boost = [.. map.Select(m => (m - 84.0) * 0.145)];

        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "altitude.mlg",
            Time = Channel("Time", "s", [.. Enumerable.Range(0, n).Select(i => i * 0.1)]),
            Channels =
            [
                Channel("RPM", "RPM", Repeat(3000.0, n)),
                Channel("MAP", "kPa", map),
                Channel("Boost psi", "psi", boost),
                Channel("Barometer", "kPa", Repeat(84.0, n)),
            ],
        };

        LogInsight? reference = Topic(log, "Pressure reference");

        Assert.Equal(InsightLevel.Note, reference!.Level);
        Assert.Contains("against the barometer", reference.Title);
        Assert.Contains("2.5", reference.Detail);   // the psi anyone assuming sea level is out by
    }

    // ----- where it can hurt -------------------------------------------------

    [Fact]
    public void ALeanSpikeUnderLoadIsAWarningEvenWhenTheAverageIsRich()
    {
        // The case the percentile exists for: mostly rich, with one excursion in
        // twenty that is what actually damages a piston.
        double[] afr = [.. Enumerable.Range(0, 200).Select(i => i % 20 == 0 ? 16.5 : 13.5)];
        double[] map = Repeat(150.0, 200);

        LogInsight? load = Topic(Steady(afr, map: map), "Mixture under load");

        Assert.Equal(InsightLevel.Warning, load!.Level);
        Assert.Contains("Lean under load", load.Title);
        Assert.Contains("10 samples", load.Title);
        Assert.Contains("rich", load.Detail);   // says so, rather than contradicting itself
    }

    [Fact]
    public void HoldingTargetUnderLoadIsSaidToBeGood()
    {
        LogInsight? load = Topic(Steady(Repeat(12.5, 200), target: Repeat(12.5, 200),
                                        map: Repeat(150.0, 200)), "Mixture under load");

        Assert.Equal(InsightLevel.Good, load!.Level);
    }

    [Fact]
    public void ALogThatNeverLoadsTheEngineSaysSoRatherThanPassingIt()
    {
        // The important distinction: "nothing wrong under load" and "never went
        // under load" must not read the same.
        LogInsight? load = Topic(Steady(Repeat(14.7, 200), map: Repeat(35.0, 200)),
                                 "Mixture under load");

        Assert.Equal(InsightLevel.Note, load!.Level);
        Assert.Contains("Not enough high-load", load.Title);
    }

    // ----- the things that are simply facts ----------------------------------

    [Fact]
    public void AnInjectorAtFullDutyIsAWarning()
    {
        LogDocument log = Steady(
            Repeat(14.7, 200), extra: Repeat(97.0, 200), extraName: "DutyCycle1", extraUnits: "%");

        LogInsight? injectors = Topic(log, "Injectors");

        Assert.Equal(InsightLevel.Warning, injectors!.Level);
        Assert.Contains("97", injectors.Title);
    }

    [Fact]
    public void AnInjectorWithRoomToSpareIsSaidToBeGood()
    {
        LogDocument log = Steady(
            Repeat(14.7, 200), extra: Repeat(55.0, 200), extraName: "DutyCycle1", extraUnits: "%");

        Assert.Equal(InsightLevel.Good, Topic(log, "Injectors")!.Level);
    }

    [Fact]
    public void KnockIsAWarningAndSilenceIsNotQuiteAllClear()
    {
        LogDocument quiet = Steady(
            Repeat(14.7, 200), extra: Repeat(0.0, 200),
            extraName: "SPK: Knock retard", extraUnits: "deg");

        LogInsight? none = Topic(quiet, "Knock");
        Assert.Equal(InsightLevel.Good, none!.Level);

        // Said carefully: the controller heard nothing, which is not the same as
        // nothing having happened.
        Assert.Contains("heard nothing", none.Detail);

        double[] retard = [.. Enumerable.Range(0, 200).Select(i => i == 100 ? 4.0 : 0.0)];

        LogDocument knocking = Steady(
            Repeat(14.7, 200), extra: retard,
            extraName: "SPK: Knock retard", extraUnits: "deg");

        LogInsight? heard = Topic(knocking, "Knock");
        Assert.Equal(InsightLevel.Warning, heard!.Level);
        Assert.Contains("4.0", heard.Title);
    }

    [Fact]
    public void AClosedLoopPinnedAtAHundredIsSaidToHaveNeverEngaged()
    {
        // Found on a real session: every reading was open loop and nothing said
        // so, which makes the whole log mean something different.
        LogDocument log = Steady(
            Repeat(14.7, 200), extra: Repeat(100.0, 200), extraName: "EGO cor1", extraUnits: "%");

        LogInsight? loop = Topic(log, "Closed loop");

        Assert.Equal(InsightLevel.Note, loop!.Level);
        Assert.Contains("never moved", loop.Title);
    }

    [Fact]
    public void ACorrectionSittingToOneSideIsTheTableBeingWrongByThatMuch()
    {
        double[] trim = [.. Enumerable.Range(0, 200).Select(i => 108.0 + (i % 2 == 0 ? 0.4 : -0.4))];

        LogDocument log = Steady(
            Repeat(14.7, 200), extra: trim, extraName: "EGO cor1", extraUnits: "%");

        LogInsight? loop = Topic(log, "Closed loop");

        Assert.Equal(InsightLevel.Watch, loop!.Level);
        Assert.Contains("8", loop.Title);
    }

    [Fact]
    public void AnEngineThatNeverWarmedUpIsSaidToHaveNotWarmedUp()
    {
        LogInsight? warmup = Topic(Steady(Repeat(14.7, 200), clt: 120), "Warmup");

        Assert.Equal(InsightLevel.Note, warmup!.Level);
        Assert.Contains("never reached operating temperature", warmup.Title);
    }

    [Fact]
    public void AManifoldThatNeverPullsVacuumIsAWarning()
    {
        // A sensor reading atmospheric for ever is not plumbed in, and every
        // load number in the log is then wrong while looking reasonable.
        LogInsight? manifold = Topic(
            Steady(Repeat(14.7, 200), map: Repeat(101.0, 200)), "Manifold pressure");

        Assert.Equal(InsightLevel.Warning, manifold!.Level);
        Assert.Contains("never fell below", manifold.Title);
    }

    [Fact]
    public void AHuntingIdleIsMeasuredRatherThanEyeballed()
    {
        int n = 400;
        double[] rpm = [.. Enumerable.Range(0, n).Select(i => 800.0 + (i % 2 == 0 ? 220 : -220))];

        var channels = new List<LogChannel>
        {
            Channel("RPM", "RPM", rpm),
            Channel("MAP", "kPa", Repeat(40.0, n)),
            Channel("TPS", "%", Repeat(0.5, n)),
            Channel("CLT", "°F", Repeat(190.0, n)),
        };

        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "idle",
            Time = Channel("Time", "s", [.. Enumerable.Range(0, n).Select(i => i * 0.1)]),
            Channels = channels,
        };

        LogInsight? idle = Topic(log, "Idle");

        Assert.Equal(InsightLevel.Watch, idle!.Level);
        Assert.Contains("hunting", idle.Title);
        Assert.Contains("standard deviation", idle.Evidence);
    }

    [Fact]
    public void ASteadyIdleIsSaidToBeSteady()
    {
        int n = 400;
        double[] rpm = [.. Enumerable.Range(0, n).Select(i => 850.0 + (i % 2 == 0 ? 8 : -8))];

        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "idle",
            Time = Channel("Time", "s", [.. Enumerable.Range(0, n).Select(i => i * 0.1)]),
            Channels = [
                Channel("RPM", "RPM", rpm),
                Channel("MAP", "kPa", Repeat(40.0, n)),
                Channel("TPS", "%", Repeat(0.5, n)),
                Channel("CLT", "°F", Repeat(190.0, n)),
            ],
        };

        LogInsight? idle = Topic(log, "Idle");

        Assert.Equal(InsightLevel.Good, idle!.Level);
    }

    [Fact]
    public void AChannelThatNeverMovesIsAFailedSensor()
    {
        LogDocument log = Steady(Repeat(14.7, 200), rpm: Repeat(3000.0, 200));

        LogInsight? sensors = Topic(log, "Sensors");

        // MAP, TPS, CLT and the target are all flat in this fixture by design,
        // which is exactly what a stuck sensor looks like.
        Assert.NotNull(sensors);
        Assert.Equal(InsightLevel.Warning, sensors!.Level);
        Assert.Contains("never changed", sensors.Title);
    }

    [Fact]
    public void AGapInTheRecordingIsReportedBecauseItWeightsEverythingElse()
    {
        int n = 200;
        double[] time = [.. Enumerable.Range(0, n).Select(i => i < 100 ? i * 0.1 : (i * 0.1) + 30)];

        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "gappy",
            Time = Channel("Time", "s", time),
            Channels = [
                Channel("RPM", "RPM", Repeat(3000.0, n)),
                Channel("MAP", "kPa", Repeat(90.0, n)),
            ],
        };

        LogInsight? recording = Topic(log, "Recording");

        Assert.Equal(InsightLevel.Watch, recording!.Level);
        Assert.Contains("stalled", recording.Title);
    }

    // ----- saying nothing, out loud ------------------------------------------

    [Fact]
    public void ALogWithNoWidebandSaysSoRatherThanStayingSilent()
    {
        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "bare",
            Time = Channel("Time", "s", [.. Enumerable.Range(0, 100).Select(i => i * 0.1)]),
            Channels = [Channel("RPM", "RPM", Repeat(3000.0, 100))],
        };

        LogInsight? mixture = Topic(log, "Mixture");

        Assert.Equal(InsightLevel.Unanswered, mixture!.Level);
        Assert.Contains("No wideband", mixture.Title);
    }

    [Fact]
    public void ALogWithAWidebandButNoTargetSaysWhichIsMissing()
    {
        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "no target",
            Time = Channel("Time", "s", [.. Enumerable.Range(0, 100).Select(i => i * 0.1)]),
            Channels = [
                Channel("RPM", "RPM", Repeat(3000.0, 100)),
                Channel("AFR", "AFR", Repeat(14.7, 100)),
            ],
        };

        LogInsight? mixture = Topic(log, "Mixture");

        Assert.Equal(InsightLevel.Unanswered, mixture!.Level);
        Assert.Contains("No AFR target", mixture.Title);
    }

    [Fact]
    public void AnEmptyLogIsNotAnalysedAtAll()
    {
        var log = new LogDocument
        {
            FormatName = "test",
            FilePath = "empty",
            Time = Channel("Time", "s", 0),
            Channels = [Channel("RPM", "RPM", 0)],
        };

        LogInsight only = Assert.Single(LogInsights.From(log));

        Assert.Equal(InsightLevel.Unanswered, only.Level);
    }

    // ----- how they are presented --------------------------------------------

    [Fact]
    public void TheWorstFindingComesFirst()
    {
        double[] afr = [.. Enumerable.Range(0, 200).Select(i => i % 20 == 0 ? 16.5 : 13.5)];

        IReadOnlyList<LogInsight> found = LogInsights.From(Steady(afr, map: Repeat(150.0, 200)));

        Assert.NotEmpty(found);
        Assert.Equal(InsightLevel.Warning, found[0].Level);

        // And never worse after better.
        for (int i = 1; i < found.Count; i++)
            Assert.True(Rank(found[i - 1].Level) >= Rank(found[i].Level));
    }

    [Fact]
    public void EveryFindingCarriesTheNumbersBehindIt()
    {
        foreach (LogInsight found in LogInsights.From(Steady(Repeat(15.7, 200))))
        {
            if (found.Level == InsightLevel.Unanswered) continue;

            Assert.False(string.IsNullOrWhiteSpace(found.Evidence), found.Title);

            // A finding that says there was not enough of something rests on
            // exactly that little, and nought is the honest count.
            if (!found.Title.StartsWith("Not enough", StringComparison.Ordinal))
                Assert.True(found.Samples > 0, found.Title);
        }
    }

    private static int Rank(InsightLevel level) => level switch
    {
        InsightLevel.Warning => 4,
        InsightLevel.Watch => 3,
        InsightLevel.Note => 2,
        InsightLevel.Good => 1,
        _ => 0,
    };

    // ----- knowing atmospheric from guessing it -------------------------------

    /// <summary>A boosted pull, with whatever pressure channels are named.</summary>
    private static LogDocument Boosted(
        bool withBaro = false, double baro = 99, string baroUnits = "kPa", bool keyOnFirst = false)
    {
        const int n = 300;

        double Load(int i) => i < n / 3 ? 0.0 : i < 2 * n / 3 ? 1.0 : 0.2;

        var channels = new List<LogChannel>
        {
            // The first tenth is the key on and the engine stopped, which is
            // where a manifold reads atmospheric.
            new("RPM", "rpm", 0,
                [.. Enumerable.Range(0, n).Select(i =>
                    keyOnFirst && i < n / 10 ? 0.0 : 900 + (Load(i) * 5100))]),

            new("MAP", "kPa", 1,
                [.. Enumerable.Range(0, n).Select(i =>
                    keyOnFirst && i < n / 10 ? 99.0 : 35 + (Load(i) * 215))]),

            new("TPS", "%", 1, [.. Enumerable.Range(0, n).Select(i => Load(i) * 95)]),
            new("AFR", "AFR", 2, [.. Enumerable.Range(0, n).Select(i => Load(i) > 0.5 ? 12.4 : 14.5)]),
            new("AFR 1 Target", "AFR", 2, [.. Enumerable.Range(0, n).Select(i => Load(i) > 0.5 ? 11.8 : 14.5)]),
            new("CLT", "°F", 1, [.. Enumerable.Range(0, n).Select(i => 186 + (i / (double)n * 8))]),
        };

        if (withBaro)
            channels.Add(new LogChannel("Barometer", baroUnits, 1, [.. Enumerable.Repeat(baro, n)]));

        return new LogDocument
        {
            FormatName = "test",
            FilePath = "boost.mlg",
            Time = new LogChannel("Time", "s", 2, [.. Enumerable.Range(0, n).Select(i => i * 0.1)]),
            Channels = channels,
        };
    }

    [Fact]
    public void ABoostedLogWithNothingToReadAmbientFromSaysSoRatherThanCallingItHealthy()
    {
        // Taking the highest reading for ambient makes the boost test compare a
        // number with itself, which is never true — so a turbo at 250 kPa was
        // reported as "what a healthy naturally aspirated engine reads".
        IReadOnlyList<LogInsight> found = LogInsights.From(Boosted());

        LogInsight manifold = Assert.Single(found, f => f.Topic == "Manifold pressure");

        Assert.Equal(InsightLevel.Unanswered, manifold.Level);
        Assert.DoesNotContain("naturally aspirated engine reads", manifold.Detail,
                              StringComparison.Ordinal);
    }

    [Fact]
    public void WithABarometerItKnowsTheBoostForWhatItIs()
    {
        IReadOnlyList<LogInsight> found = LogInsights.From(Boosted(withBaro: true));

        LogInsight manifold = Assert.Single(found, f => f.Topic == "Manifold pressure");

        Assert.Equal(InsightLevel.Note, manifold.Level);
        Assert.Contains("boost", manifold.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(14.4, "psi")]
    [InlineData(0.99, "bar")]
    [InlineData(990, "mbar")]
    public void ABarometerIsReadInWhateverItWasLoggedIn(double reading, string units)
    {
        // The old test was "greater than fifty", a bare-kilopascal assumption
        // that discarded every baro from a controller set to imperial units.
        IReadOnlyList<LogInsight> found =
            LogInsights.From(Boosted(withBaro: true, baro: reading, baroUnits: units));

        Assert.Equal(InsightLevel.Note, Assert.Single(found, f => f.Topic == "Manifold pressure").Level);
    }

    [Fact]
    public void AndAStoppedEngineIsItselfABarometer()
    {
        // A stationary engine's manifold is open to the atmosphere through the
        // throttle, and most logs begin with the key on before cranking.
        IReadOnlyList<LogInsight> found = LogInsights.From(Boosted(keyOnFirst: true));

        Assert.Equal(InsightLevel.Note, Assert.Single(found, f => f.Topic == "Manifold pressure").Level);
    }

    [Fact]
    public void AndTheLoadedMixtureIsStillJudgedWithoutAnAmbientFigure()
    {
        // The threshold was nine tenths of ambient, which without a real figure
        // is nine tenths of peak boost — excluding almost all the loaded running
        // the check exists for, and reporting there was not enough of it.
        IReadOnlyList<LogInsight> found = LogInsights.From(Boosted());

        LogInsight loaded = Assert.Single(found, f => f.Topic == "Mixture under load");

        Assert.NotEqual(InsightLevel.Unanswered, loaded.Level);
    }
}
