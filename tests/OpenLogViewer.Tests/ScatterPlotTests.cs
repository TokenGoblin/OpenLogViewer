using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class ScatterPlotTests
{
    private static LogChannel Channel(string name, params double[] values) =>
        new(name, "", 2, values);

    private static ScatterPlot Build(
        LogChannel x, LogChannel y, LogChannel z,
        SampleMask? mask = null, LogChannel? compare = null) =>
        ScatterPlot.Build(x, y, z, 0, x.Length - 1, mask, compare);

    [Fact]
    public void EverySampleWithThreeReadingsBecomesAPoint()
    {
        var x = Channel("RPM", 1000, 2000, 3000);
        var y = Channel("MAP", 30, 60, 90);
        var z = Channel("AFR", 14.7, 13.2, 11.8);

        ScatterPlot plot = Build(x, y, z);

        Assert.Equal(3, plot.Count);
        Assert.Equal([1000, 2000, 3000], plot.Xs[..3]);
        Assert.Equal([30, 60, 90], plot.Ys[..3]);
        Assert.Equal(14.7, plot.Zs[0], precision: 4);
        Assert.Equal(13.2, plot.Zs[1], precision: 4);
        Assert.Equal(11.8, plot.Zs[2], precision: 4);
        Assert.Equal([0, 1, 2], plot.Samples[..3]);
    }

    [Fact]
    public void RangesCoverThePointsThatSurvived()
    {
        var x = Channel("RPM", 1000, 6500, 3000);
        var y = Channel("MAP", 90, 30, 60);
        var z = Channel("AFR", 11.8, 14.7, 13.2);

        ScatterPlot plot = Build(x, y, z);

        Assert.Equal(1000, plot.XMin);
        Assert.Equal(6500, plot.XMax);
        Assert.Equal(30, plot.YMin);
        Assert.Equal(90, plot.YMax);
        Assert.Equal(11.8, plot.ZMin, precision: 4);
        Assert.Equal(14.7, plot.ZMax, precision: 4);
    }

    [Fact]
    public void ASampleMissingAnyOfTheThreeIsDroppedAndCounted()
    {
        var x = Channel("RPM", 1000, double.NaN, 3000, 4000);
        var y = Channel("MAP", 30, 60, double.NaN, 90);
        var z = Channel("AFR", 14.7, 13.2, 11.8, double.NaN);

        ScatterPlot plot = Build(x, y, z);

        Assert.Equal(1, plot.Count);
        Assert.Equal(3, plot.Dropped);
        Assert.Equal(0, plot.Filtered);
    }

    [Fact]
    public void FilteredSamplesAreCountedApartFromMissingOnes()
    {
        var x = Channel("RPM", 1000, 2000, 3000, 4000);
        var y = Channel("MAP", 30, 60, 90, 100);
        var z = Channel("AFR", 14.7, double.NaN, 11.8, 12.0);

        var mask = new SampleMask
        {
            Accepted = [true, true, false, true],
            FiltersApplied = true,
            Total = 4,
            PassCount = 3,
            UnknownChannels = [],
        };

        ScatterPlot plot = Build(x, y, z, mask);

        Assert.Equal(2, plot.Count);
        Assert.Equal(1, plot.Filtered);
        Assert.Equal(1, plot.Dropped);
    }

    [Fact]
    public void ADeviationIsTakenPerSampleRatherThanFromTheMeans()
    {
        var x = Channel("RPM", 1000, 1000);
        var y = Channel("MAP", 50, 50);
        var z = Channel("AFR", 12.0, 14.0);
        var target = Channel("AFR Target", 13.0, 13.0);

        ScatterPlot plot = Build(x, y, z, compare: target);

        Assert.True(plot.IsDelta);
        Assert.Equal(-1.0, plot.Zs[0], precision: 4);
        Assert.Equal(1.0, plot.Zs[1], precision: 4);
        Assert.Equal(1.0, plot.MaxDeviation, precision: 4);
    }

    [Fact]
    public void ASampleWithNoTargetIsDroppedRatherThanComparedAgainstNothing()
    {
        var x = Channel("RPM", 1000, 1000);
        var y = Channel("MAP", 50, 50);
        var z = Channel("AFR", 12.0, 14.0);
        var target = Channel("AFR Target", 13.0, double.NaN);

        ScatterPlot plot = Build(x, y, z, compare: target);

        Assert.Equal(1, plot.Count);
        Assert.Equal(1, plot.Dropped);
    }

    [Fact]
    public void TheWindowIsHonouredAndClampedToTheShortestChannel()
    {
        var x = Channel("RPM", 1000, 2000, 3000, 4000, 5000);
        var y = Channel("MAP", 30, 40, 50, 60, 70);
        var z = Channel("AFR", 10, 11, 12);

        ScatterPlot plot = ScatterPlot.Build(x, y, z, 1, 99);

        Assert.Equal(2, plot.Count);
        Assert.Equal([1, 2], plot.Samples[..2]);
    }

    [Fact]
    public void AnEmptyWindowProducesAnEmptyPlotRatherThanThrowing()
    {
        var x = Channel("RPM", 1000);
        var y = Channel("MAP", 30);
        var z = Channel("AFR", 14.0);

        ScatterPlot plot = ScatterPlot.Build(x, y, z, 5, 9);

        Assert.True(plot.IsEmpty);
        Assert.Equal(0, plot.Bin(4, 4).Occupied);
    }

    // ----- binning ----------------------------------------------------------

    [Fact]
    public void PointsLandInTheBlockTheirPositionPutsThem()
    {
        var x = Channel("RPM", 0, 0, 100, 100);
        var y = Channel("MAP", 0, 100, 0, 100);
        var z = Channel("AFR", 1, 2, 3, 4);

        ScatterBins bins = Build(x, y, z).Bin(2, 2);

        Assert.Equal(4, bins.Occupied);
        Assert.Equal(1, bins.Means[bins.Index(0, 0)]);
        Assert.Equal(2, bins.Means[bins.Index(0, 1)]);
        Assert.Equal(3, bins.Means[bins.Index(1, 0)]);
        Assert.Equal(4, bins.Means[bins.Index(1, 1)]);
    }

    [Fact]
    public void ABlockHoldsTheMeanOfWhatLandedInItRatherThanTheLastToArrive()
    {
        // The whole point of binning: drawn one mark per sample, this block
        // would take the colour of whichever sample happened to be drawn last.
        var x = Channel("RPM", 1000, 1000, 1000, 1000);
        var y = Channel("MAP", 50, 50, 50, 50);
        var z = Channel("AFR", 10.0, 12.0, 14.0, 16.0);

        ScatterBins bins = Build(x, y, z).Bin(4, 4);
        int occupied = Array.FindIndex(bins.Counts, c => c > 0);

        Assert.Equal(1, bins.Occupied);
        Assert.Equal(4, bins.Counts[occupied]);
        Assert.Equal(13.0, bins.Means[occupied], precision: 4);
        Assert.Equal(4, bins.Busiest);
    }

    [Fact]
    public void ABlockReportsTheSpreadItsMeanHides()
    {
        // Six rich and six lean average to target. A table shades that cell as
        // if it were on target; the spread is the only thing that says otherwise.
        double[] readings = [11, 11, 11, 15, 15, 15];
        var x = Channel("RPM", [.. readings.Select(_ => 1000.0)]);
        var y = Channel("MAP", [.. readings.Select(_ => 50.0)]);
        var z = Channel("AFR", readings);

        ScatterBins bins = Build(x, y, z).Bin(2, 2);
        int occupied = Array.FindIndex(bins.Counts, c => c > 0);

        Assert.Equal(13.0, bins.Means[occupied], precision: 4);
        Assert.Equal(4.0, bins.SpreadIn(occupied % bins.Columns, occupied / bins.Columns), precision: 4);
    }

    [Fact]
    public void ASingleSampleBlockHasNoSpread()
    {
        var x = Channel("RPM", 1000, 6000);
        var y = Channel("MAP", 30, 90);
        var z = Channel("AFR", 14.0, 11.0);

        ScatterBins bins = Build(x, y, z).Bin(2, 2);

        Assert.Equal(0.0, bins.SpreadIn(0, 0));
        Assert.Equal(1, bins.Busiest);
    }

    [Fact]
    public void TheExtremeSamplesLandInsideTheGridRatherThanPastItsEdge()
    {
        var x = Channel("RPM", 500, 7000);
        var y = Channel("MAP", 10, 250);
        var z = Channel("AFR", 14.0, 11.0);

        ScatterBins bins = Build(x, y, z).Bin(8, 8);

        Assert.Equal(1, bins.Counts[bins.Index(0, 0)]);
        Assert.Equal(1, bins.Counts[bins.Index(7, 7)]);
        Assert.Equal(2, bins.Occupied);
    }

    [Fact]
    public void AFlatChannelIsPlacedInTheMiddleRatherThanPinnedToAnEdge()
    {
        var x = Channel("RPM", 1000, 2000, 3000);
        var y = Channel("MAP", 50, 50, 50);
        var z = Channel("AFR", 14, 13, 12);

        ScatterBins bins = Build(x, y, z).Bin(3, 3);

        for (int column = 0; column < 3; column++)
            Assert.Equal(1, bins.Counts[bins.Index(column, 1)]);
    }

    [Fact]
    public void BlockMeanRangeIsOverBlocksRatherThanOverSamples()
    {
        // One block averages 10 and 20 to 15, so no block is as extreme as the
        // rawest sample — colour is scaled over what is drawn.
        var x = Channel("RPM", 0, 0, 100);
        var y = Channel("MAP", 0, 0, 100);
        var z = Channel("AFR", 10, 20, 18);

        ScatterBins bins = Build(x, y, z).Bin(2, 2);

        Assert.Equal(15.0, bins.MeanMin, precision: 4);
        Assert.Equal(18.0, bins.MeanMax, precision: 4);
        Assert.Equal(10.0, Build(x, y, z).ZMin, precision: 4);
    }

    [Fact]
    public void BinningIsBoundedByTheGridRatherThanByTheLog()
    {
        // Fifty thousand samples over a grid of a hundred blocks.
        var rng = new Random(7);
        double[] xs = new double[50_000], ys = new double[50_000], zs = new double[50_000];
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = rng.NextDouble() * 7000;
            ys[i] = rng.NextDouble() * 250;
            zs[i] = 10 + (rng.NextDouble() * 6);
        }

        ScatterBins bins = Build(Channel("RPM", xs), Channel("MAP", ys), Channel("AFR", zs)).Bin(10, 10);

        Assert.Equal(100, bins.Occupied);
        Assert.Equal(50_000, bins.Counts.Sum());
    }

    // ----- the colour scale -------------------------------------------------

    /// <summary>
    /// A block grid where one block per column holds <paramref name="means"/> in
    /// order — one occupied block per value, which is what the trim works over.
    /// </summary>
    private static ScatterBins BinsHolding(params double[] means)
    {
        double[] xs = new double[means.Length], ys = new double[means.Length];
        for (int i = 0; i < means.Length; i++)
        {
            xs[i] = i;
            ys[i] = i;
        }

        return Build(Channel("RPM", xs), Channel("MAP", ys), Channel("AFR", means))
            .Bin(means.Length, means.Length);
    }

    [Fact]
    public void OneOutlierDoesNotOwnTheWholeColourScale()
    {
        // Ninety-eight blocks of closed-loop running and two transients. The
        // full range is 8.6 to 18; the scale the marks are drawn on must not be.
        var means = new List<double>();
        for (int i = 0; i < 98; i++) means.Add(14.5 + (i % 5) * 0.1);
        means.Add(8.6);
        means.Add(18.0);

        ScatterBins bins = BinsHolding([.. means]);

        Assert.Equal(8.6, bins.MeanMin, precision: 3);
        Assert.Equal(18.0, bins.MeanMax, precision: 3);

        // The scale now covers the band the engine actually ran in.
        Assert.True(bins.ColorLow > 13, $"low bound was {bins.ColorLow}");
        Assert.True(bins.ColorHigh < 15.5, $"high bound was {bins.ColorHigh}");
        Assert.True(bins.ClipsLow);
        Assert.True(bins.ClipsHigh);
    }

    [Fact]
    public void AGenuinelyWideSpreadIsNotTrimmedAwayToNothing()
    {
        // Values spread evenly across their range: the trim should take a
        // sliver, not reduce the scale to the middle of the data.
        double[] means = [.. Enumerable.Range(0, 100).Select(i => (double)i)];

        ScatterBins bins = BinsHolding(means);

        Assert.True(bins.ColorLow <= 3, $"low bound was {bins.ColorLow}");
        Assert.True(bins.ColorHigh >= 96, $"high bound was {bins.ColorHigh}");
    }

    [Fact]
    public void TooFewBlocksToTrimFallsBackToTheFullRange()
    {
        ScatterBins bins = BinsHolding(11.0, 15.0);

        Assert.Equal(bins.MeanMin, bins.ColorLow, precision: 6);
        Assert.Equal(bins.MeanMax, bins.ColorHigh, precision: 6);
        Assert.False(bins.ClipsLow);
        Assert.False(bins.ClipsHigh);
    }

    [Fact]
    public void AChannelThatBarelyMovedKeepsAScaleRatherThanCollapsing()
    {
        ScatterBins bins = BinsHolding(14.7, 14.7, 14.7, 14.7);

        Assert.False(bins.ClipsLow);
        Assert.False(bins.ClipsHigh);
        Assert.Equal(bins.ColorLow, bins.ColorHigh, precision: 6);
    }

    [Fact]
    public void TheScaleIsOverBlocksRatherThanWeightedBySampleCount()
    {
        // One block visited five hundred times at 14.0 and ninety-nine visited
        // once each across 10..19. Weighted by samples, idle would decide the
        // scale for the whole map; over blocks it is one block among a hundred.
        var xs = new List<double>();
        var ys = new List<double>();
        var zs = new List<double>();

        for (int i = 0; i < 500; i++)
        {
            xs.Add(0);
            ys.Add(0);
            zs.Add(14.0);
        }

        for (int i = 1; i < 100; i++)
        {
            xs.Add(i);
            ys.Add(i);
            zs.Add(10 + (i / 11.0));
        }

        ScatterBins bins = Build(
            Channel("RPM", [.. xs]), Channel("MAP", [.. ys]), Channel("AFR", [.. zs])).Bin(100, 100);

        Assert.Equal(500, bins.Busiest);
        Assert.True(bins.ColorHigh > 18, $"high bound was {bins.ColorHigh}");
    }

    [Fact]
    public void ADivergingScaleTakesItsReachFromTheTrimmedBoundsToo()
    {
        // A drive mostly within two points of target, plus one accel-enrichment
        // event either way. Untrimmed the reach is 20 and the whole drive draws
        // within a tenth of neutral.
        var means = new List<double>();
        for (int i = 0; i < 98; i++) means.Add(-2.0 + (i * 4.0 / 97));
        means.Add(-20.0);
        means.Add(20.0);

        ScatterBins bins = BinsHolding([.. means]);

        Assert.Equal(20.0, bins.MeanMax, precision: 3);
        Assert.True(bins.MeanExtent < 3.0, $"reach was {bins.MeanExtent}");
    }

    [Fact]
    public void ABoundTheTrimBarelyMovedIsNotReportedAsAClip()
    {
        // An evenly spread channel: the trim shaves a sliver off each end, which
        // is not a clip worth putting a ≥ in front of.
        double[] means = [.. Enumerable.Range(0, 100).Select(i => (double)i)];

        ScatterBins bins = BinsHolding(means);

        Assert.True(bins.ColorLow > bins.MeanMin, "the trim should still have moved the bound");
        Assert.False(bins.ClipsLow);
        Assert.False(bins.ClipsHigh);
    }

    [Fact]
    public void ATrimThatWouldCloseTheRangeIsNotTaken()
    {
        // Ninety-eight blocks on exactly one value: there is no middle to scale
        // over, so the full range stands and the two outliers keep their ends.
        var means = new List<double>();
        for (int i = 0; i < 98; i++) means.Add(0.1);
        means.Add(-9.0);
        means.Add(9.0);

        ScatterBins bins = BinsHolding([.. means]);

        Assert.Equal(-9.0, bins.ColorLow, precision: 3);
        Assert.Equal(9.0, bins.ColorHigh, precision: 3);
        Assert.False(bins.ClipsLow);
        Assert.False(bins.ClipsHigh);
    }

    // ----- tracing back -----------------------------------------------------

    [Fact]
    public void AblockTracesBackToTheSamplesThatMadeIt()
    {
        var x = Channel("RPM", 0, 100, 0, 100);
        var y = Channel("MAP", 0, 100, 0, 100);
        var z = Channel("AFR", 14, 11, 13, 12);

        ScatterPlot plot = Build(x, y, z);
        ScatterBins bins = plot.Bin(2, 2);

        Assert.Equal([0, 2], plot.SamplesIn(bins, 0, 0));
        Assert.Equal([1, 3], plot.SamplesIn(bins, 1, 1));
        Assert.Empty(plot.SamplesIn(bins, 0, 1));
    }

    [Fact]
    public void SamplesTraceBackThroughTheFilterToTheirOriginalIndex()
    {
        var x = Channel("RPM", 1000, 1000, 1000);
        var y = Channel("MAP", 50, 50, 50);
        var z = Channel("AFR", 14, 13, 12);

        var mask = new SampleMask
        {
            Accepted = [false, false, true],
            FiltersApplied = true,
            Total = 3,
            PassCount = 1,
            UnknownChannels = [],
        };

        ScatterPlot plot = Build(x, y, z, mask);
        ScatterBins bins = plot.Bin(2, 2);

        Assert.Equal([2], plot.SamplesIn(bins, 1, 1));
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2 }, 1)]
    [InlineData(new[] { 0, 1, 2, 40, 41 }, 2)]
    [InlineData(new[] { 0, 5, 10, 15 }, 4)]
    public void ContiguousSamplesGroupIntoVisits(int[] samples, int expected) =>
        Assert.Equal(expected, ScatterPlot.VisitsAmong(samples).Count);

    [Fact]
    public void AOneSampleGapDoesNotSplitAVisit()
    {
        // Two samples of noise inside a pull are not two separate visits.
        IReadOnlyList<(int First, int Last)> visits = ScatterPlot.VisitsAmong([10, 11, 13, 14]);

        Assert.Single(visits);
        Assert.Equal((10, 14), visits[0]);
    }

    [Fact]
    public void NoSamplesMeansNoVisits() =>
        Assert.Empty(ScatterPlot.VisitsAmong([]));
}
