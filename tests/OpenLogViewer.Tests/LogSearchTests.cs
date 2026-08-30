using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class LogSearchTests
{
    private static LogDocument Log(params (string Name, double[] Values)[] channels)
    {
        int count = channels.Max(c => c.Values.Length);
        var time = new double[count];
        for (int i = 0; i < count; i++) time[i] = i * 0.1;

        return new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, time, preservePrecision: true),
            Channels = [.. channels.Select(c => new LogChannel(c.Name, "", 2, c.Values))],
            FormatName = "test",
        };
    }

    private static LogDocument Rpm(params double[] values) => Log(("RPM", values));

    // ----- finding ----------------------------------------------------------

    [Fact]
    public void AConditionFindsTheSamplesItHeldAt()
    {
        LogSearchResult found = LogSearch.Find(
            Rpm(1000, 5000, 5200, 1000, 1000), "RPM > 4000");

        Assert.False(found.HasProblem);
        Assert.Equal(2, found.Matches);
        Assert.Single(found.Runs);
        Assert.Equal((1, 2), found.Runs[0]);
    }

    [Fact]
    public void SeparateStretchesAreSeparateRuns()
    {
        LogSearchResult found = LogSearch.Find(
            Rpm(5000, 5000, 1000, 1000, 1000, 1000, 5000, 5000), "RPM > 4000");

        Assert.Equal(2, found.Runs.Count);
        Assert.Equal((0, 1), found.Runs[0]);
        Assert.Equal((6, 7), found.Runs[1]);
        Assert.Equal(4, found.Matches);
    }

    [Fact]
    public void ASignalChatteringAtItsThresholdIsOneFindingRatherThanFifty()
    {
        // RPM wandering about 4,000 crosses it every few samples. Reporting each
        // crossing separately would bury the one thing that happened.
        double[] rpm = [4100, 3900, 4100, 3900, 4100, 4100, 1000, 1000, 1000, 1000];

        LogSearchResult found = LogSearch.Find(Rpm(rpm), "RPM > 4000");

        Assert.Single(found.Runs);
        Assert.Equal((0, 5), found.Runs[0]);
    }

    [Fact]
    public void AWiderGapStillSeparatesTwoFindings()
    {
        double[] rpm = [4100, 1000, 1000, 1000, 1000, 4100];

        LogSearchResult found = LogSearch.Find(Rpm(rpm), "RPM > 4000");

        Assert.Equal(2, found.Runs.Count);
    }

    [Fact]
    public void TheToleranceCanBeTightenedToStrictlyConsecutiveSamples()
    {
        double[] rpm = [4100, 3900, 4100];

        LogSearchResult found = LogSearch.Find(Rpm(rpm), "RPM > 4000", gapTolerance: 0);

        Assert.Equal(2, found.Runs.Count);
    }

    [Fact]
    public void ConditionsCombineTheWayTheDoInACalculatedChannel()
    {
        // Wide open only where both hold: sample 0 has revs and throttle, 1 has
        // revs without throttle, 2 throttle without revs, and 6 both again.
        LogDocument log = Log(
            ("RPM", [5000, 5000, 1000, 1000, 1000, 1000, 5000]),
            ("TPS", [90, 10, 90, 10, 10, 10, 95]));

        LogSearchResult found = LogSearch.Find(log, "RPM > 4000 && TPS > 80");

        Assert.Equal(2, found.Matches);
        Assert.Equal(2, found.Runs.Count);
        Assert.Equal((0, 0), found.Runs[0]);
        Assert.Equal((6, 6), found.Runs[1]);
    }

    [Fact]
    public void ChannelNamesWithSpacesNeedNoQuoting()
    {
        LogDocument log = Log(("AFR Target 1", [14.7, 12.0, 14.7]));

        LogSearchResult found = LogSearch.Find(log, "AFR Target 1 < 13");

        Assert.Single(found.Runs);
        Assert.Equal((1, 1), found.Runs[0]);
    }

    [Fact]
    public void TheTimeChannelCanBeSearchedLikeAnyOther()
    {
        LogSearchResult found = LogSearch.Find(Rpm(1, 2, 3, 4, 5), "Time >= 0.3");

        Assert.Equal(2, found.Matches);
    }

    [Fact]
    public void ABareChannelReadsAsWhereverItIsNotZero()
    {
        LogSearchResult found = LogSearch.Find(Rpm(0, 0, 1500, 1500, 0), "RPM");

        Assert.Equal(2, found.Matches);
        Assert.Equal((2, 3), found.Runs[0]);
    }

    [Fact]
    public void ANegativeValueCountsAsMuchAsAPositiveOne()
    {
        // Not zero is the test, so a channel swinging either side of nothing is
        // found on both sides of it.
        LogSearchResult found = LogSearch.Find(Log(("Trim", [-5.0, 0, 5.0])), "Trim");

        Assert.Equal(2, found.Matches);
    }

    // ----- what it declines to answer ---------------------------------------

    [Fact]
    public void ASampleWithNoReadingIsUnknownRatherThanAMiss()
    {
        // A comparison against a reading that was never taken is unanswerable,
        // and folding it into "did not match" would report confidence about a
        // stretch of log that has nothing to say.
        LogSearchResult found = LogSearch.Find(
            Rpm(5000, double.NaN, double.NaN, 1000), "RPM > 4000");

        Assert.Equal(1, found.Matches);
        Assert.Equal(2, found.Unknown);
    }

    [Fact]
    public void AGapDoesNotJoinTwoRunsAcrossIt()
    {
        LogSearchResult found = LogSearch.Find(
            Rpm(5000, double.NaN, double.NaN, double.NaN, double.NaN, 5000), "RPM > 4000");

        Assert.Equal(2, found.Runs.Count);
    }

    [Fact]
    public void AConditionNamingAChannelTheLogLacksSaysSoRatherThanFindingNothing()
    {
        LogSearchResult found = LogSearch.Find(Rpm(1000), "Boost > 10");

        Assert.True(found.HasProblem);
        Assert.True(found.IsEmpty);
    }

    [Fact]
    public void AConditionThatDoesNotParseSaysSo()
    {
        LogSearchResult found = LogSearch.Find(Rpm(1000), "RPM >");

        Assert.True(found.HasProblem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyConditionIsNotAnError(string condition)
    {
        LogSearchResult found = LogSearch.Find(Rpm(1000), condition);

        Assert.False(found.HasProblem);
        Assert.True(found.IsEmpty);
    }

    [Fact]
    public void AnInfiniteResultIsNotAMatch()
    {
        // Dividing by zero is not a reading, whichever way it went.
        LogSearchResult found = LogSearch.Find(Log(("A", [1.0, 2.0]), ("B", [0.0, 1.0])), "A / B");

        Assert.Equal(1, found.Matches);
        Assert.Equal((1, 1), found.Runs[0]);
    }

    // ----- filters ----------------------------------------------------------

    [Fact]
    public void FilteredSamplesAreNotSearched()
    {
        // The filters say which part of the drive is under consideration, so a
        // search must not jump to a moment they exclude.
        LogDocument log = Rpm(5000, 5000, 5000, 5000);

        var mask = new SampleMask
        {
            Accepted = [false, false, true, true],
            FiltersApplied = true,
            Total = 4,
            PassCount = 2,
            UnknownChannels = [],
        };

        LogSearchResult found = LogSearch.Find(log, "RPM > 4000", mask);

        Assert.Equal(2, found.Matches);
        Assert.Equal((2, 3), found.Runs[0]);
    }

    // ----- stepping through -------------------------------------------------

    [Fact]
    public void TheRunHoldingOrFollowingASampleIsFound()
    {
        LogSearchResult found = LogSearch.Find(
            Rpm(5000, 1000, 1000, 1000, 5000, 1000, 1000, 1000, 5000), "RPM > 4000");

        Assert.Equal(3, found.Runs.Count);

        Assert.Equal(0, found.RunAtOrAfter(0));    // inside the first
        Assert.Equal(1, found.RunAtOrAfter(1));    // before the second
        Assert.Equal(1, found.RunAtOrAfter(4));    // inside the second
        Assert.Equal(2, found.RunAtOrAfter(5));
        Assert.Equal(-1, found.RunAtOrAfter(99));  // past the last
    }

    [Fact]
    public void NoRunsMeansNothingToStepTo() =>
        Assert.Equal(-1, LogSearch.Find(Rpm(1000), "RPM > 4000").RunAtOrAfter(0));
}
