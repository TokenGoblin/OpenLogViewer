using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading one log against another.
///
/// The loop this exists for: change something, drive it again, find out what
/// moved. Doing that by eye across two windows is how a change gets credited with
/// an improvement that was really a warmer engine.
///
/// What makes it harder than subtracting one column from another is that the two
/// logs line up neither in time nor in content, and the failures from both are
/// quiet. A comparison matched by column position compares coolant against oil
/// pressure the moment a firmware update inserts a channel; a difference that
/// treats an empty cell as zero invents its largest numbers everywhere the second
/// run did not go.
/// </summary>
public class LogComparisonTests
{
    private static LogDocument Log(string name, params (string Name, float[] Values)[] channels) =>
        new()
        {
            FilePath = name,
            FormatName = "test",
            Channels = [.. channels.Select(c => LogChannel.Adopt(c.Name, "", 1, c.Values))],
            Time = new LogChannel(
                "Time", "s", 3,
                [.. Enumerable.Range(0, channels[0].Values.Length).Select(i => (double)i)],
                preservePrecision: true),
        };

    // ----- which channels line up ------------------------------------------------

    [Fact]
    public void ChannelsAreMatchedByNameAndTheOddOnesOutAreNamed()
    {
        LogDocument before = Log("a", ("RPM", [1000, 2000]), ("MAP", [50, 90]), ("Knock", [0, 0]));
        LogDocument after = Log("b", ("RPM", [1000, 2000]), ("MAP", [50, 90]), ("AFR", [14, 12]));

        ChannelOverlap overlap = LogComparison.Compare(before, after);

        Assert.Equal(["MAP", "RPM"], overlap.Shared);
        Assert.Equal(["Knock"], overlap.OnlyInFirst);
        Assert.Equal(["AFR"], overlap.OnlyInSecond);
        Assert.True(overlap.AnythingShared);
    }

    /// <summary>
    /// By name, not by position. A firmware update that inserts one channel shifts
    /// every column after it, and matching by index would then compare coolant
    /// against oil pressure without a word.
    /// </summary>
    [Fact]
    public void AnInsertedChannelDoesNotShiftTheMatching()
    {
        LogDocument before = Log("a", ("RPM", [1000]), ("CLT", [180]));
        LogDocument after = Log("b", ("RPM", [1000]), ("Oil Pressure", [45]), ("CLT", [190]));

        ChannelOverlap overlap = LogComparison.Compare(before, after);

        Assert.Equal(["CLT", "RPM"], overlap.Shared);
        Assert.Equal(["Oil Pressure"], overlap.OnlyInSecond);
    }

    [Fact]
    public void CaseDoesNotStopTwoChannelsMatching()
    {
        LogDocument before = Log("a", ("rpm", [1000]));
        LogDocument after = Log("b", ("RPM", [1000]));

        Assert.Single(LogComparison.Compare(before, after).Shared);
    }

    /// <summary>
    /// Two logs with nothing in common is reported rather than left to be
    /// discovered by finding an empty plot.
    /// </summary>
    [Fact]
    public void TwoUnrelatedLogsSaySoRatherThanComparingNothingQuietly()
    {
        LogDocument before = Log("a", ("RPM", [1000]));
        LogDocument after = Log("b", ("EngineSpeed", [1000]));

        ChannelOverlap overlap = LogComparison.Compare(before, after);

        Assert.Empty(overlap.Shared);
        Assert.False(overlap.AnythingShared);
        Assert.Contains("no channel names", overlap.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoIdenticalLogsSaySoTheShortWay()
    {
        LogDocument log = Log("a", ("RPM", [1000]), ("MAP", [50]));

        Assert.Contains("same 2 channels", LogComparison.Compare(log, log).Summary, StringComparison.Ordinal);
    }

    // ----- the difference table ---------------------------------------------------

    /// <summary>
    /// Two runs binned onto the same axes and subtracted. Sixteen samples in each,
    /// the second uniformly one unit richer, so every shared cell reads −1.
    /// </summary>
    private static (HistogramTable First, HistogramTable Second) TwoRuns(
        float offset = 1, int skipFrom = int.MaxValue)
    {
        float[] rpm = [1000, 1000, 3000, 3000];
        float[] map = [50, 150, 50, 150];
        float[] afr = [14, 13, 12, 11];

        LogChannel x = LogChannel.Adopt("RPM", "rpm", 0, rpm);
        LogChannel y = LogChannel.Adopt("MAP", "kPa", 0, map);

        HistogramTable a = HistogramTable.Build(
            x, y, LogChannel.Adopt("AFR", "", 1, afr),
            2, 2, 0, rpm.Length - 1, HistogramStatistic.Mean);

        // The second run, shifted, and optionally missing its last cells so the
        // "one side has no data" case can be exercised.
        float[] shifted = [.. afr.Select((v, i) => i >= skipFrom ? float.NaN : v + offset)];

        HistogramTable b = HistogramTable.Build(
            x, y, LogChannel.Adopt("AFR", "", 1, shifted),
            2, 2, 0, rpm.Length - 1, HistogramStatistic.Mean);

        return (a, b);
    }

    [Fact]
    public void SubtractingTwoTablesGivesTheChangeCellByCell()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns(offset: 1);

        HistogramTable change = LogComparison.Difference(after, before);

        for (int c = 0; c < change.Columns; c++)
            for (int r = 0; r < change.Rows; r++)
                Assert.Equal(1, change.Values[c, r]!.Value, 5);
    }

    /// <summary>
    /// The subtraction is the right way round: first minus second, so "after minus
    /// before" reads as the change that was made. Backwards it would report every
    /// improvement as a regression.
    /// </summary>
    [Fact]
    public void TheDifferenceIsFirstMinusSecond()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns(offset: 2);

        Assert.Equal(2, LogComparison.Difference(after, before).Values[0, 0]!.Value, 5);
        Assert.Equal(-2, LogComparison.Difference(before, after).Values[0, 0]!.Value, 5);
    }

    /// <summary>
    /// A cell only one run visited is left empty. Treating the other side as zero
    /// would invent a difference the size of the whole reading everywhere the
    /// second drive did not go — which on two real drives is most of the table,
    /// and would be the largest and most eye-catching numbers on it.
    /// </summary>
    [Fact]
    public void ACellOnlyOneRunVisitedIsLeftEmptyRatherThanTreatedAsZero()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns(offset: 1, skipFrom: 2);

        HistogramTable change = LogComparison.Difference(before, after);

        int filled = 0;

        for (int c = 0; c < change.Columns; c++)
            for (int r = 0; r < change.Rows; r++)
                if (change.Values[c, r] is not null) filled++;

        Assert.True(filled > 0, "the cells both runs visited should still be compared");
        Assert.True(filled < change.Columns * change.Rows, "the unvisited ones should be empty");
    }

    /// <summary>
    /// A difference rests on the thinner of its two sides, so that is the count
    /// carried — otherwise a cell with two hundred samples before and two after
    /// would shade as well evidenced.
    /// </summary>
    [Fact]
    public void TheCountIsTheWeakerOfTheTwoSides()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns();

        HistogramTable change = LogComparison.Difference(before, after);

        for (int c = 0; c < change.Columns; c++)
            for (int r = 0; r < change.Rows; r++)
                Assert.Equal(
                    Math.Min(before.Counts[c, r], after.Counts[c, r]),
                    change.Counts[c, r]);
    }

    [Fact]
    public void TablesOnDifferentAxesCannotBeSubtracted()
    {
        (HistogramTable before, _) = TwoRuns();

        float[] rpm = [1000, 2000, 3000];

        HistogramTable wider = HistogramTable.Build(
            LogChannel.Adopt("RPM", "rpm", 0, rpm),
            LogChannel.Adopt("MAP", "kPa", 0, [50, 100, 150]),
            LogChannel.Adopt("AFR", "", 1, [14, 13, 12]),
            3, 3, 0, 2, HistogramStatistic.Mean);

        ArgumentException e = Assert.Throws<ArgumentException>(
            () => LogComparison.Difference(before, wider));

        Assert.Contains("identical axes", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A difference is an ordinary table, so everything downstream works on it.</summary>
    [Fact]
    public void ADifferenceIsAnOrdinaryTable()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns();

        HistogramTable change = LogComparison.Difference(after, before);

        Assert.Equal(before.Columns, change.Columns);
        Assert.Equal(before.ColumnCenters, change.ColumnCenters);
        Assert.Equal(before.RowCenters, change.RowCenters);
        Assert.Contains("change", change.DisplayName!, StringComparison.OrdinalIgnoreCase);
    }

    // ----- how much of it can actually be compared ---------------------------------

    /// <summary>
    /// Two drives overlapping in a tenth of the table have not really been
    /// compared, and the sparse result deserves an explanation rather than being
    /// left to look like a bug.
    /// </summary>
    [Fact]
    public void CoverageSaysHowMuchTheTwoRunsActuallyShare()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns(offset: 1, skipFrom: 2);

        (int both, int onlyFirst, int onlySecond) = LogComparison.Coverage(before, after);

        Assert.True(both > 0);
        Assert.True(onlyFirst > 0, "the first run went somewhere the second did not");
        Assert.Equal(0, onlySecond);
    }

    // ----- what changed, in one line -------------------------------------------------

    [Fact]
    public void TheSummaryGivesTheAverageMoveAndTheBiggestOne()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns(offset: 1);

        ComparisonSummary summary = LogComparison.Summarise(LogComparison.Difference(after, before));

        Assert.True(summary.Any);
        Assert.Equal(4, summary.Cells);
        Assert.Equal(1, summary.Mean, 5);
        Assert.Equal(1, summary.Largest, 5);
    }

    /// <summary>
    /// The average and the largest are different findings. A table that moved a
    /// little everywhere and one that moved a lot in one corner are not the same
    /// result, and a coloured grid shows them alike.
    /// </summary>
    [Fact]
    public void OneBigMoveIsReportedSeparatelyFromTheAverage()
    {
        float[] rpm = [1000, 1000, 3000, 3000];
        float[] map = [50, 150, 50, 150];

        LogChannel x = LogChannel.Adopt("RPM", "rpm", 0, rpm);
        LogChannel y = LogChannel.Adopt("MAP", "kPa", 0, map);

        HistogramTable before = HistogramTable.Build(
            x, y, LogChannel.Adopt("AFR", "", 1, [14, 14, 14, 14]),
            2, 2, 0, 3, HistogramStatistic.Mean);

        // Three cells unchanged, one moved a long way.
        HistogramTable after = HistogramTable.Build(
            x, y, LogChannel.Adopt("AFR", "", 1, [14, 14, 14, 10]),
            2, 2, 0, 3, HistogramStatistic.Mean);

        HistogramTable change = LogComparison.Difference(after, before);
        ComparisonSummary summary = LogComparison.Summarise(change);

        Assert.Equal(-1, summary.Mean, 5);
        Assert.Equal(-4, summary.Largest, 5);

        // Reported at the bin's centre, not at the sample that landed in it: two
        // bins across 1,000–3,000 rpm makes the upper one 2,000–3,000, centred on
        // 2,500. Asserted against the table's own axes so this says "the top-right
        // cell" rather than restating a number.
        Assert.Equal(change.ColumnCenters[^1], summary.AtColumn, 5);
        Assert.Equal(change.RowCenters[^1], summary.AtRow, 5);
    }

    /// <summary>
    /// A cell resting on one sample is not evidence of a change, and the threshold
    /// is what stops the biggest reported move being the noisiest one.
    /// </summary>
    [Fact]
    public void ThinCellsCanBeHeldOutOfTheSummary()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns(offset: 1);

        Assert.False(LogComparison.Summarise(
            LogComparison.Difference(after, before), minimumSamples: 99).Any);
    }

    [Fact]
    public void NothingInCommonSummarisesToNothingRatherThanZero()
    {
        (HistogramTable before, HistogramTable after) = TwoRuns(offset: 1, skipFrom: 0);

        ComparisonSummary summary = LogComparison.Summarise(LogComparison.Difference(before, after));

        Assert.False(summary.Any);
        Assert.True(double.IsNaN(summary.Mean));
    }
}
