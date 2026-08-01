using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class HistogramDeltaTests
{
    private static LogChannel Channel(string name, params double[] values) => new(name, "AFR", 2, values);

    private static HistogramTable Delta(
        LogChannel z, LogChannel target,
        HistogramStatistic statistic = HistogramStatistic.Mean,
        int columns = 1, int rows = 1)
    {
        int n = z.Values.Length;
        var x = new LogChannel("RPM", "RPM", 0, [.. Enumerable.Repeat(1000.0, n)]);
        var y = new LogChannel("MAP", "kPa", 0, [.. Enumerable.Repeat(50.0, n)]);

        return HistogramTable.Build(
            x, y, z, columns, rows, 0, n - 1, statistic, mask: null, zCompare: target);
    }

    [Fact]
    public void CellsHoldTheDifferenceFromTheTarget()
    {
        var afr = Channel("AFR", 13.0, 15.0);
        var target = Channel("AFR Target", 14.0, 14.0);

        HistogramTable table = Delta(afr, target);

        Assert.True(table.IsDelta);
        Assert.Equal("AFR Target", table.ZCompare!.Name);
        Assert.Equal(0.0, table.Values[0, 0]!.Value, 6);   // mean of -1 and +1
    }

    [Fact]
    public void TheDeviationIsTakenPerSampleNotBetweenTheMeans()
    {
        // Mean AFR 14 and mean target 14 would give zero error, hiding that the
        // engine was never actually on target.
        var afr = Channel("AFR", 12.0, 16.0);
        var target = Channel("AFR Target", 14.0, 14.0);

        HistogramTable worst = Delta(afr, target, HistogramStatistic.Max);
        HistogramTable best = Delta(afr, target, HistogramStatistic.Min);

        Assert.Equal(2.0, worst.Values[0, 0]!.Value, 6);
        Assert.Equal(-2.0, best.Values[0, 0]!.Value, 6);
    }

    [Fact]
    public void ASampleWithNoTargetReadingIsSkipped()
    {
        var afr = Channel("AFR", 13.0, 15.0, 17.0);
        var target = Channel("AFR Target", 14.0, double.NaN, 14.0);

        HistogramTable table = Delta(afr, target);

        Assert.Equal(2, table.SampleCount);
        Assert.Equal(1.0, table.Values[0, 0]!.Value, 6);   // mean of -1 and +3
    }

    [Fact]
    public void TheScaleReachesEquallyEitherSideOfZero()
    {
        // Two cells, one -0.5 and one +2.0. The scale must reach 2.0 both ways,
        // or an equal error each side of target would shade unequally.
        var x = new LogChannel("RPM", "RPM", 0, [1000, 5000]);
        var y = new LogChannel("MAP", "kPa", 0, [50, 50]);
        var afr = Channel("AFR", 13.5, 16.0);
        var target = Channel("AFR Target", 14.0, 14.0);

        HistogramTable table = HistogramTable.Build(
            x, y, afr, [1000, 5000], [50], 0, 1, HistogramStatistic.Mean, null, target);

        Assert.Equal(-0.5, table.MinValue, 6);
        Assert.Equal(2.0, table.MaxValue, 6);
        Assert.Equal(2.0, table.MaxDeviation, 6);
    }

    [Fact]
    public void DeltasCarryAnExplicitSign()
    {
        var afr = Channel("AFR", 16.0);
        var target = Channel("AFR Target", 14.0);

        HistogramTable lean = Delta(afr, target);
        Assert.Equal("+2.00", lean.Format(0, 0));

        HistogramTable rich = Delta(Channel("AFR", 12.0), target);
        Assert.StartsWith("-", rich.Format(0, 0));
    }

    [Fact]
    public void CountIgnoresTheComparisonEntirely()
    {
        var afr = Channel("AFR", 13.0, 15.0, 16.0);
        var target = Channel("AFR Target", 14.0, 14.0, 14.0);

        HistogramTable table = Delta(afr, target, HistogramStatistic.Count);

        Assert.Equal("3", table.Format(0, 0));
    }

    [Fact]
    public void WithoutAComparisonNothingChanges()
    {
        var afr = Channel("AFR", 13.0, 15.0);
        var x = new LogChannel("RPM", "RPM", 0, [1000, 1000]);
        var y = new LogChannel("MAP", "kPa", 0, [50, 50]);

        HistogramTable table = HistogramTable.Build(
            x, y, afr, 1, 1, 0, 1, HistogramStatistic.Mean);

        Assert.False(table.IsDelta);
        Assert.Equal(14.0, table.Values[0, 0]!.Value, 6);
        Assert.Equal("14.00", table.Format(0, 0));
    }

    [Fact]
    public void DeltaWorksOnTuneBreakpointsAndWithFilters()
    {
        var x = new LogChannel("RPM", "RPM", 0, [1000, 1000, 3000, 3000]);
        var y = new LogChannel("MAP", "kPa", 0, [50, 50, 50, 50]);
        var afr = Channel("AFR", 13.0, 15.0, 12.0, 16.0);
        var target = Channel("AFR Target", 14.0, 14.0, 14.0, 14.0);
        var clt = new LogChannel("CLT", "F", 0, [100, 180, 100, 180]);

        var doc = new LogDocument
        {
            FilePath = "x",
            Channels = [x, y, afr, target, clt],
            Time = new LogChannel("Time", "s", 2, [0, 1, 2, 3]),
            FormatName = "test",
        };

        SampleMask warm = SampleFilter.Build(doc, [new LogFilter
        {
            Name = "warm", Channel = "CLT", Comparison = FilterComparison.AboveOrEqual, Low = 160,
        }]);

        HistogramTable table = HistogramTable.Build(
            x, y, afr, [1000, 3000], [50], 0, 3, HistogramStatistic.Mean, warm, target);

        Assert.True(table.IsDelta);
        Assert.Equal(2, table.SampleCount);
        Assert.Equal(1.0, table.Values[0, 0]!.Value, 6);   // 15 - 14, warm only
        Assert.Equal(2.0, table.Values[1, 0]!.Value, 6);   // 16 - 14, warm only
    }
}
