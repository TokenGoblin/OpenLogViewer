using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class HistogramTraceBackTests
{
    private static LogChannel Channel(string name, params double[] values) => new(name, "", 1, values);

    /// <summary>Two RPM columns at 1000 and 3000, one load row.</summary>
    private static HistogramTable Table(double[] rpm, SampleMask? mask = null) =>
        HistogramTable.Build(
            Channel("RPM", rpm),
            Channel("MAP", [.. Enumerable.Repeat(50.0, rpm.Length)]),
            Channel("AFR", [.. Enumerable.Repeat(14.0, rpm.Length)]),
            [1000, 3000], [50], 0, rpm.Length - 1, HistogramStatistic.Mean, mask);

    [Fact]
    public void ACellReportsTheSamplesThatLandedInIt()
    {
        HistogramTable table = Table([1000, 3000, 1000, 3000, 3000]);

        Assert.Equal([0, 2], table.SamplesIn(0, 0));
        Assert.Equal([1, 3, 4], table.SamplesIn(1, 0));
    }

    [Fact]
    public void SamplesAreGroupedIntoSeparateVisits()
    {
        // The engine passes through the same cell three times.
        HistogramTable table = Table([3000, 3000, 1000, 1000, 1000, 1000, 3000, 3000, 3000]);

        var visits = table.VisitsTo(1, 0);

        Assert.Equal(2, visits.Count);
        Assert.Equal((0, 1), visits[0]);
        Assert.Equal((6, 8), visits[1]);
    }

    [Fact]
    public void ASampleOrTwoOfNoiseDoesNotSplitAVisit()
    {
        // Index 2 briefly leaves the cell; that is one visit, not two.
        HistogramTable table = Table([3000, 3000, 1000, 3000, 3000]);

        var visits = table.VisitsTo(1, 0);

        Assert.Single(visits);
        Assert.Equal((0, 4), visits[0]);
    }

    [Fact]
    public void AWideGapDoesSplitAVisit()
    {
        HistogramTable table = Table([3000, 1000, 1000, 1000, 1000, 3000]);

        var visits = table.VisitsTo(1, 0);

        Assert.Equal(2, visits.Count);
    }

    [Fact]
    public void TheLongestVisitIsTheOneOffered()
    {
        HistogramTable table = Table([3000, 1000, 1000, 1000, 1000, 3000, 3000, 3000, 3000]);

        Assert.Equal((5, 8), table.LongestVisitTo(1, 0));
    }

    [Fact]
    public void AnEmptyCellHasNoVisits()
    {
        HistogramTable table = Table([1000, 1000]);

        Assert.Empty(table.VisitsTo(1, 0));
        Assert.Null(table.LongestVisitTo(1, 0));
    }

    [Fact]
    public void CellsOutsideTheTableAreRejected()
    {
        HistogramTable table = Table([1000, 3000]);

        Assert.Empty(table.SamplesIn(-1, 0));
        Assert.Empty(table.SamplesIn(0, 9));
    }

    [Fact]
    public void FilteredOutSamplesAreNotTracedBack()
    {
        var rpm = Channel("RPM", 3000, 3000, 3000);
        var map = Channel("MAP", 50, 50, 50);
        var afr = Channel("AFR", 14, 14, 14);
        var clt = Channel("CLT", 100, 180, 180);

        var doc = new LogDocument
        {
            FilePath = "x",
            Channels = [rpm, map, afr, clt],
            Time = Channel("Time", 0, 1, 2),
            FormatName = "test",
        };

        SampleMask warm = SampleFilter.Build(doc, [new LogFilter
        {
            Name = "warm", Channel = "CLT", Comparison = FilterComparison.AboveOrEqual, Low = 160,
        }]);

        HistogramTable table = HistogramTable.Build(
            rpm, map, afr, [1000, 3000], [50], 0, 2, HistogramStatistic.Mean, warm);

        // Sample 0 is cold, so the cell must not point back to it.
        Assert.Equal([1, 2], table.SamplesIn(1, 0));
    }

    [Fact]
    public void ASampleReportsTheCellItFallsIn()
    {
        HistogramTable table = Table([1000, 3000]);

        Assert.Equal((0, 0), table.CellOf(0));
        Assert.Equal((1, 0), table.CellOf(1));
        Assert.Null(table.CellOf(99));
    }

    [Fact]
    public void TraceBackSurvivesTheSampleWindow()
    {
        // Built over samples 2..4 only; earlier matches must not be reported.
        HistogramTable table = HistogramTable.Build(
            Channel("RPM", 3000, 3000, 3000, 3000, 3000),
            Channel("MAP", 50, 50, 50, 50, 50),
            Channel("AFR", 14, 14, 14, 14, 14),
            [1000, 3000], [50], 2, 4, HistogramStatistic.Mean);

        Assert.Equal([2, 3, 4], table.SamplesIn(1, 0));
    }

    // ----- the other direction: a marked span to the cells it reached --------

    [Fact]
    public void AMarkedSpanReportsTheCellsItPassedThrough()
    {
        HistogramTable table = Table([1000, 1000, 3000, 3000, 1000]);

        CellVisits visits = table.VisitedBy(0, 4);

        Assert.Equal(2, visits.Cells);
        Assert.Equal(5, visits.Samples);
        Assert.Equal(3, visits.Counts[0, 0]);
        Assert.Equal(2, visits.Counts[1, 0]);
        Assert.True(visits.Visited(0, 0));
        Assert.True(visits.Visited(1, 0));
    }

    [Fact]
    public void ASpanOverOneCellReachesOnlyThatCell()
    {
        HistogramTable table = Table([1000, 1000, 3000, 3000, 1000]);

        CellVisits visits = table.VisitedBy(2, 3);

        Assert.Equal(1, visits.Cells);
        Assert.False(visits.Visited(0, 0));
        Assert.True(visits.Visited(1, 0));
    }

    [Fact]
    public void TheSpanIsReadTheSameWayRoundWhicheverEndIsGivenFirst()
    {
        HistogramTable table = Table([1000, 1000, 3000, 3000, 1000]);

        Assert.Equal(table.VisitedBy(0, 4).Cells, table.VisitedBy(4, 0).Cells);
        Assert.Equal(table.VisitedBy(0, 4).Samples, table.VisitedBy(4, 0).Samples);
    }

    [Fact]
    public void SamplesAFilterExcludedAreCountedAsOutsideRatherThanPlaced()
    {
        // The table was built without them, so marking their cells would claim
        // evidence the table does not rest on.
        var mask = new SampleMask
        {
            Accepted = [true, true, false, false, false],
            FiltersApplied = true,
            Total = 5,
            PassCount = 2,
            UnknownChannels = [],
        };

        HistogramTable table = Table([1000, 1000, 3000, 3000, 1000], mask);

        CellVisits visits = table.VisitedBy(0, 4);

        Assert.Equal(1, visits.Cells);
        Assert.Equal(2, visits.Samples);
        Assert.Equal(3, visits.Outside);
    }

    [Fact]
    public void ASpanOutsideTheTablesWindowReachesNothingAndSaysSo()
    {
        // The table covers samples 0..2; the span is past its end.
        HistogramTable table = HistogramTable.Build(
            Channel("RPM", [1000, 3000, 1000, 3000, 1000]),
            Channel("MAP", [.. Enumerable.Repeat(50.0, 5)]),
            Channel("AFR", [.. Enumerable.Repeat(14.0, 5)]),
            [1000, 3000], [50], 0, 2, HistogramStatistic.Mean);

        CellVisits visits = table.VisitedBy(3, 4);

        Assert.True(visits.IsEmpty);
        Assert.Equal(0, visits.Samples);
        Assert.Equal(2, visits.Outside);
    }

    [Fact]
    public void ACellTracedBackToTheLogMarksThatSameCellComingForward()
    {
        // The round trip: click a cell, get its longest visit, mark that span,
        // and the span must land back in the cell it came from.
        HistogramTable table = Table([3000, 3000, 1000, 1000, 1000, 1000, 3000, 3000, 3000]);

        (int First, int Last) longest = table.LongestVisitTo(1, 0)!.Value;
        CellVisits visits = table.VisitedBy(longest.First, longest.Last);

        Assert.True(visits.Visited(1, 0));
        Assert.Equal(1, visits.Cells);
    }

    [Fact]
    public void VisitedIsFalseOutsideTheTableRatherThanThrowing()
    {
        CellVisits visits = Table([1000, 3000]).VisitedBy(0, 1);

        Assert.False(visits.Visited(-1, 0));
        Assert.False(visits.Visited(0, -1));
        Assert.False(visits.Visited(99, 0));
        Assert.False(visits.Visited(0, 99));
    }
}
