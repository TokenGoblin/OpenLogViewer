using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class ChannelStatisticsTests
{
    private static LogChannel Channel(params double[] values) => new("RPM", "RPM", 1, values);

    [Fact]
    public void SummarisesTheRequestedSpanOnly()
    {
        LogChannel rpm = Channel(1000, 2000, 3000, 4000, 5000);

        ChannelStatistics stats = ChannelStatistics.Over(rpm, 1, 3);

        Assert.Equal(2000, stats.Min);
        Assert.Equal(4000, stats.Max);
        Assert.Equal(3000, stats.Mean);
        Assert.Equal(3, stats.Count);
        Assert.Equal(2000, stats.Span);
    }

    [Fact]
    public void MissingReadingsAreSkippedNotCountedAsZero()
    {
        // Treating a gap as zero would drag the mean toward nothing.
        LogChannel rpm = Channel(1000, double.NaN, 3000);

        ChannelStatistics stats = ChannelStatistics.Over(rpm, 0, 2);

        Assert.Equal(2, stats.Count);
        Assert.Equal(2000, stats.Mean);
        Assert.Equal(1000, stats.Min);
    }

    [Fact]
    public void ABackwardsSpanIsReadTheSameWayRound()
    {
        // Dragging right to left must give the same answer.
        LogChannel rpm = Channel(1000, 2000, 3000, 4000);

        Assert.Equal(ChannelStatistics.Over(rpm, 1, 3), ChannelStatistics.Over(rpm, 3, 1));
    }

    [Fact]
    public void TheSpanIsClampedToTheChannel()
    {
        LogChannel rpm = Channel(1000, 2000, 3000);

        ChannelStatistics stats = ChannelStatistics.Over(rpm, -50, 500);

        Assert.Equal(3, stats.Count);
        Assert.Equal(1000, stats.Min);
        Assert.Equal(3000, stats.Max);
    }

    [Fact]
    public void ASingleSampleIsItsOwnSummary()
    {
        LogChannel rpm = Channel(1000, 2500, 3000);

        ChannelStatistics stats = ChannelStatistics.Over(rpm, 1, 1);

        Assert.Equal(1, stats.Count);
        Assert.Equal(2500, stats.Min);
        Assert.Equal(2500, stats.Max);
        Assert.Equal(2500, stats.Mean);
        Assert.Equal(0, stats.Span);
    }

    [Fact]
    public void AllMissingReadingsReportsNoData()
    {
        LogChannel rpm = Channel(double.NaN, double.NaN);

        ChannelStatistics stats = ChannelStatistics.Over(rpm, 0, 1);

        Assert.False(stats.HasData);
        Assert.Equal(0, stats.Count);
        Assert.True(double.IsNaN(stats.Mean));
    }

    [Fact]
    public void AnEmptyChannelReportsNoData()
    {
        Assert.False(ChannelStatistics.Over(Channel(), 0, 10).HasData);
    }

    [Fact]
    public void TheMeanIsNotSkewedByOrder()
    {
        LogChannel rising = Channel(10, 20, 30, 40);
        LogChannel falling = Channel(40, 30, 20, 10);

        Assert.Equal(
            ChannelStatistics.Over(rising, 0, 3).Mean,
            ChannelStatistics.Over(falling, 0, 3).Mean);
    }
}
