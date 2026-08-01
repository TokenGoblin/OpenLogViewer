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
}
