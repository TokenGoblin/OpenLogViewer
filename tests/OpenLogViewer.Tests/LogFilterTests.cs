using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class LogFilterTests : IDisposable
{
    private readonly List<string> _temp = [];

    private LogDocument Log(params (string Name, double[] Values)[] channels)
    {
        var lines = new List<string> { string.Join(',', new[] { "Time" }.Concat(channels.Select(c => c.Name))) };
        int rows = channels[0].Values.Length;
        for (int r = 0; r < rows; r++)
        {
            var cells = new List<string> { (r * 0.1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            cells.AddRange(channels.Select(c => c.Values[r].ToString(System.Globalization.CultureInfo.InvariantCulture)));
            lines.Add(string.Join(',', cells));
        }

        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        _temp.Add(path);
        return LogReaderFactory.Load(path);
    }

    public void Dispose()
    {
        foreach (string p in _temp) File.Delete(p);
    }

    private static LogFilter Filter(string channel, FilterComparison comparison, double low, double high = 0) =>
        new() { Name = "test", Channel = channel, Comparison = comparison, Low = low, High = high };

    [Theory]
    [InlineData(FilterComparison.Above, 100, 0, 101, true)]
    [InlineData(FilterComparison.Above, 100, 0, 100, false)]
    [InlineData(FilterComparison.AboveOrEqual, 100, 0, 100, true)]
    [InlineData(FilterComparison.Below, 100, 0, 99, true)]
    [InlineData(FilterComparison.BelowOrEqual, 100, 0, 100, true)]
    [InlineData(FilterComparison.Between, 10, 20, 15, true)]
    [InlineData(FilterComparison.Between, 10, 20, 25, false)]
    [InlineData(FilterComparison.Between, 20, 10, 15, true)]   // bounds given backwards
    [InlineData(FilterComparison.Outside, 10, 20, 25, true)]
    [InlineData(FilterComparison.Outside, 10, 20, 15, false)]
    public void ComparisonsBehaveAsWritten(
        FilterComparison comparison, double low, double high, double value, bool expected) =>
        Assert.Equal(expected, Filter("X", comparison, low, high).Accepts(value));

    [Fact]
    public void AMissingReadingNeverSatisfiesAFilter()
    {
        // A sample that cannot be evaluated must not be quietly kept.
        Assert.False(Filter("X", FilterComparison.Above, 0).Accepts(double.NaN));
        Assert.False(Filter("X", FilterComparison.Below, 1000).Accepts(double.NaN));
    }

    [Fact]
    public void OnlySamplesMeetingEveryEnabledFilterSurvive()
    {
        LogDocument log = Log(
            ("CLT", [120, 150, 170, 180, 185]),
            ("RPM", [0, 800, 2500, 3000, 400]));

        SampleMask mask = SampleFilter.Build(log, [
            Filter("CLT", FilterComparison.AboveOrEqual, 160),
            Filter("RPM", FilterComparison.AboveOrEqual, 500),
        ]);

        // Index 2 and 3 are the only ones warm AND running.
        Assert.True(mask.FiltersApplied);
        Assert.Equal(2, mask.PassCount);
        Assert.Equal([false, false, true, true, false], mask.Accepted);
    }

    [Fact]
    public void DisabledFiltersAreIgnored()
    {
        LogDocument log = Log(("CLT", [100, 120, 140]));

        SampleMask mask = SampleFilter.Build(log, [
            Filter("CLT", FilterComparison.AboveOrEqual, 160) with { Enabled = false },
        ]);

        Assert.False(mask.FiltersApplied);
        Assert.Equal(3, mask.PassCount);
        Assert.True(mask[0]);
    }

    [Fact]
    public void AFilterNamingAnAbsentChannelIsReportedNotApplied()
    {
        // Silently rejecting every sample would look like a broken log.
        LogDocument log = Log(("CLT", [170, 175, 180]));

        SampleMask mask = SampleFilter.Build(log, [Filter("EGT", FilterComparison.Above, 100)]);

        Assert.Equal(["EGT"], mask.UnknownChannels);
        Assert.False(mask.FiltersApplied);
        Assert.Equal(3, mask.PassCount);
    }

    [Fact]
    public void NoFiltersMeansEverySampleCounts()
    {
        LogDocument log = Log(("CLT", [1, 2, 3]));

        SampleMask mask = SampleFilter.Build(log, []);

        Assert.Equal(3, mask.PassCount);
        Assert.True(mask[0] && mask[1] && mask[2]);
    }

    [Fact]
    public void TheHistogramCountsOnlySamplesThatPass()
    {
        LogDocument log = Log(
            ("RPM", [1000, 1000, 1000, 1000]),
            ("MAP", [50, 50, 50, 50]),
            ("AFR", [10, 20, 10, 20]),
            ("CLT", [100, 100, 180, 180]));

        SampleMask warm = SampleFilter.Build(log, [Filter("CLT", FilterComparison.AboveOrEqual, 160)]);

        HistogramTable all = HistogramTable.Build(
            log.FindChannel("RPM")!, log.FindChannel("MAP")!, log.FindChannel("AFR")!,
            1, 1, 0, 3, HistogramStatistic.Mean);

        HistogramTable hot = HistogramTable.Build(
            log.FindChannel("RPM")!, log.FindChannel("MAP")!, log.FindChannel("AFR")!,
            1, 1, 0, 3, HistogramStatistic.Mean, warm);

        Assert.Equal(4, all.SampleCount);
        Assert.Equal(15.0, all.Values[0, 0]);   // mean of 10,20,10,20

        Assert.Equal(2, hot.SampleCount);
        Assert.Equal(15.0, hot.Values[0, 0]);   // mean of the warm pair, 10 and 20
    }

    [Fact]
    public void FilteringAlsoTightensTheAxesOntoWhatRemains()
    {
        LogDocument log = Log(
            ("RPM", [500, 1000, 5000, 6000]),
            ("MAP", [20, 30, 90, 100]),
            ("AFR", [14, 14, 12, 12]),
            ("CLT", [100, 100, 180, 180]));

        SampleMask warm = SampleFilter.Build(log, [Filter("CLT", FilterComparison.AboveOrEqual, 160)]);

        HistogramTable hot = HistogramTable.Build(
            log.FindChannel("RPM")!, log.FindChannel("MAP")!, log.FindChannel("AFR")!,
            2, 1, 0, 3, HistogramStatistic.Mean, warm);

        // Axis spans 5000..6000, not 500..6000, so bin centres are 5250 and 5750.
        Assert.Equal([5250, 5750], hot.ColumnCenters);
    }

    [Fact]
    public void DescribeReadsBackAsTheCondition()
    {
        Assert.Equal("CLT ≥ 160", Filter("CLT", FilterComparison.AboveOrEqual, 160).Describe());
        Assert.Equal("AFR 9…20", Filter("AFR", FilterComparison.Between, 9, 20).Describe());
        Assert.Equal("TPS > 1.5", Filter("TPS", FilterComparison.Above, 1.5).Describe());
    }

    [Fact]
    public void SuggestedFiltersMatchTheChannelsTheLogHas()
    {
        LogDocument log = Log(
            ("CLT", [100, 150, 180]),
            ("RPM", [0, 900, 2000]));

        var suggested = SampleFilter.Suggest(log).ToList();

        Assert.Contains(suggested, f => f.Channel == "CLT");
        Assert.Contains(suggested, f => f.Channel == "RPM");
        Assert.DoesNotContain(suggested, f => f.Channel == "TPS");
        // Suggestions arrive switched off; the user opts in.
        Assert.All(suggested, f => Assert.False(f.Enabled));
    }
}
