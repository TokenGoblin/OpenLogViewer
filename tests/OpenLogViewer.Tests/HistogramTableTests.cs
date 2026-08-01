using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class HistogramTableTests
{
    private static LogChannel Channel(string name, params double[] values) =>
        new(name, "", 2, values);

    private static HistogramTable Build(
        LogChannel x, LogChannel y, LogChannel z,
        int columns = 2, int rows = 2,
        HistogramStatistic statistic = HistogramStatistic.Mean) =>
        HistogramTable.Build(x, y, z, columns, rows, 0, x.Values.Length - 1, statistic);

    [Fact]
    public void SamplesLandInTheExpectedCells()
    {
        // Two clusters: low/low and high/high.
        var x = Channel("RPM", 1000, 1000, 6000, 6000);
        var y = Channel("MAP", 20, 20, 200, 200);
        var z = Channel("AFR", 14.0, 15.0, 11.0, 12.0);

        HistogramTable table = Build(x, y, z);

        Assert.Equal(14.5, table.Values[0, 0]);
        Assert.Equal(11.5, table.Values[1, 1]);
        // The off-diagonal cells were never visited.
        Assert.Null(table.Values[0, 1]);
        Assert.Null(table.Values[1, 0]);
        Assert.Equal(2, table.PopulatedCells);
        Assert.Equal(4, table.SampleCount);
    }

    [Theory]
    [InlineData(HistogramStatistic.Mean, 15.0)]
    [InlineData(HistogramStatistic.Min, 10.0)]
    [InlineData(HistogramStatistic.Max, 20.0)]
    [InlineData(HistogramStatistic.Count, 3.0)]
    public void EachStatisticReducesACellCorrectly(HistogramStatistic statistic, double expected)
    {
        var x = Channel("RPM", 1000, 1000, 1000);
        var y = Channel("MAP", 50, 50, 50);
        var z = Channel("AFR", 10.0, 15.0, 20.0);

        HistogramTable table = Build(x, y, z, columns: 1, rows: 1, statistic: statistic);

        Assert.Equal(expected, table.Values[0, 0]);
    }

    [Fact]
    public void CountsAreTrackedIndependentlyOfTheStatistic()
    {
        var x = Channel("RPM", 1000, 1000, 6000);
        var y = Channel("MAP", 50, 50, 50);
        var z = Channel("AFR", 12.0, 13.0, 14.0);

        HistogramTable table = Build(x, y, z, columns: 2, rows: 1);

        Assert.Equal(2, table.Counts[0, 0]);
        Assert.Equal(1, table.Counts[1, 0]);
        Assert.Equal(2, table.MaxCount);
    }

    [Fact]
    public void TheMaximumSampleLandsInTheLastBinRatherThanOverflowing()
    {
        // (max - min) / step lands exactly on `bins` for the top sample.
        var x = Channel("RPM", 0, 10);
        var y = Channel("MAP", 0, 10);
        var z = Channel("AFR", 1, 2);

        HistogramTable table = Build(x, y, z, columns: 4, rows: 4);

        Assert.Equal(1, table.Values[0, 0]);
        Assert.Equal(2, table.Values[3, 3]);
        Assert.Equal(2, table.SampleCount);
    }

    [Fact]
    public void RowsAndColumnsAreCentredOnTheirBins()
    {
        var x = Channel("RPM", 0, 100);
        var y = Channel("MAP", 0, 100);
        var z = Channel("AFR", 1, 1);

        HistogramTable table = Build(x, y, z, columns: 2, rows: 2);

        Assert.Equal([25, 75], table.ColumnCenters);
        Assert.Equal([25, 75], table.RowCenters);
    }

    [Fact]
    public void SamplesWithAMissingReadingAreSkipped()
    {
        var x = Channel("RPM", 1000, double.NaN, 1000);
        var y = Channel("MAP", 50, 50, double.NaN);
        var z = Channel("AFR", 12.0, 13.0, 14.0);

        HistogramTable table = Build(x, y, z, columns: 1, rows: 1);

        Assert.Equal(1, table.SampleCount);
        Assert.Equal(12.0, table.Values[0, 0]);
    }

    [Fact]
    public void AConstantAxisStillPlacesItsSamples()
    {
        // A zero-width axis would otherwise divide by zero.
        var x = Channel("RPM", 800, 800, 800);
        var y = Channel("MAP", 40, 40, 40);
        var z = Channel("AFR", 14.0, 14.5, 15.0);

        HistogramTable table = Build(x, y, z, columns: 4, rows: 4);

        Assert.Equal(3, table.SampleCount);
        Assert.Equal(1, table.PopulatedCells);
    }

    [Fact]
    public void OnlyTheRequestedSampleWindowIsBinned()
    {
        var x = Channel("RPM", 1000, 2000, 3000, 4000);
        var y = Channel("MAP", 50, 50, 50, 50);
        var z = Channel("AFR", 10, 11, 12, 13);

        HistogramTable table = HistogramTable.Build(x, y, z, 1, 1, 1, 2, HistogramStatistic.Mean);

        Assert.Equal(2, table.SampleCount);
        Assert.Equal(11.5, table.Values[0, 0]);
    }

    [Fact]
    public void AnEmptyWindowProducesAnEmptyTable()
    {
        var x = Channel("RPM", 1000);
        var y = Channel("MAP", 50);
        var z = Channel("AFR", 12);

        HistogramTable table = HistogramTable.Build(x, y, z, 4, 4, 5, 9, HistogramStatistic.Mean);

        Assert.True(table.IsEmpty);
        Assert.Equal(0, table.SampleCount);
    }

    [Fact]
    public void ValueRangeCoversOnlyPopulatedCells()
    {
        var x = Channel("RPM", 1000, 6000);
        var y = Channel("MAP", 50, 50);
        var z = Channel("AFR", 11.0, 17.0);

        HistogramTable table = Build(x, y, z, columns: 4, rows: 1);

        Assert.Equal(11.0, table.MinValue);
        Assert.Equal(17.0, table.MaxValue);
    }

    [Fact]
    public void FormatUsesTheZChannelPrecision()
    {
        var z = new LogChannel("AFR", "AFR", 2, [14.7, 14.7]);
        HistogramTable table = HistogramTable.Build(
            Channel("RPM", 1000, 1000), Channel("MAP", 50, 50), z, 1, 1, 0, 1, HistogramStatistic.Mean);

        Assert.Equal("14.70", table.Format(0, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ATableNeedsAtLeastOneBinPerAxis(int bins)
    {
        var x = Channel("RPM", 1000);
        var y = Channel("MAP", 50);
        var z = Channel("AFR", 12);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistogramTable.Build(x, y, z, bins, 4, 0, 0, HistogramStatistic.Mean));
    }
}
