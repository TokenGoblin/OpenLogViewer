using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class VeAnalysisTests
{
    /// <summary>A 2×2 table at a flat 50% VE, on round breakpoints.</summary>
    private static TuneTable Table(double fill = 50)
    {
        var values = new double[2, 2];
        for (int c = 0; c < 2; c++)
        for (int r = 0; r < 2; r++)
            values[c, r] = fill;

        return new TuneTable(
            "VE table 1",
            new TuneAxis("rpm", "RPM", [1000, 3000]),
            new TuneAxis("map", "kPa", [40, 80]),
            values,
            "%");
    }

    private static LogChannel Channel(string name, params double[] values) => new(name, "", 2, values);

    /// <summary>Every sample lands in the low/low cell unless said otherwise.</summary>
    private static VeAnalysisResult Analyse(
        double[] afr, double[] target, VeAnalysisSettings? settings = null,
        double[]? rpm = null, double[]? map = null)
    {
        int n = afr.Length;
        LogChannel x = Channel("RPM", rpm ?? [.. Enumerable.Repeat(1000.0, n)]);
        LogChannel y = Channel("MAP", map ?? [.. Enumerable.Repeat(40.0, n)]);

        return VeAnalysis.Analyse(
            Table(), x, y, Channel("AFR", afr), Channel("Target", target),
            0, n - 1, null, settings ?? new VeAnalysisSettings { MinimumSamples = 1 });
    }

    [Fact]
    public void RicherThanTargetAsksForLessFuel()
    {
        // Measured 12.6 against a target of 14.0 means the ECU metered fuel for
        // more air than there was, so the VE number is too high.
        VeAnalysisResult result = Analyse([12.6], [14.0]);

        Assert.Equal(45.0, result.Suggested[0, 0]!.Value, 4);   // 50 × 12.6/14
        Assert.Equal(-10.0, result.ChangePercent[0, 0]!.Value, 4);
    }

    [Fact]
    public void LeanerThanTargetAsksForMoreFuel()
    {
        VeAnalysisResult result = Analyse([15.4], [14.0]);

        Assert.Equal(55.0, result.Suggested[0, 0]!.Value, 4);
        Assert.Equal(10.0, result.ChangePercent[0, 0]!.Value, 4);
    }

    [Fact]
    public void OnTargetLeavesTheCellWhereItIs()
    {
        VeAnalysisResult result = Analyse([14.0, 14.0], [14.0, 14.0]);

        Assert.Equal(50.0, result.Suggested[0, 0]!.Value, 4);
        Assert.Equal(0.0, result.ChangePercent[0, 0]!.Value, 4);
    }

    [Fact]
    public void TheCorrectionIsAveragedPerSampleNotBetweenTheMeans()
    {
        // Mean AFR 14 against mean target 14 would say the cell is perfect, which
        // would hide that it was never actually on target.
        VeAnalysisResult result = Analyse([12.6, 15.4], [14.0, 14.0]);

        // mean of 0.9 and 1.1 is 1.0 — here they really do cancel, and that is
        // the honest answer for a cell that averaged correct.
        Assert.Equal(50.0, result.Suggested[0, 0]!.Value, 4);
    }

    [Fact]
    public void ACellWithTooFewSamplesIsLeftAloneAndCounted()
    {
        // Two crossings on the way somewhere else say more about the transient
        // than about the fuelling there.
        VeAnalysisResult result = Analyse(
            [12.6, 12.6], [14.0, 14.0], new VeAnalysisSettings { MinimumSamples = 5 });

        Assert.Null(result.Suggested[0, 0]);
        Assert.Equal(0, result.CellsSuggested);
        Assert.Equal(1, result.CellsThin);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void ALargeCorrectionIsClampedRatherThanDropped()
    {
        // A cell read during an enrichment event can imply a correction far
        // bigger than the table is wrong by. It should still move, just not all
        // the way at once.
        VeAnalysisResult result = Analyse(
            [7.0], [14.0], new VeAnalysisSettings { MinimumSamples = 1, MaxChangePercent = 15 });

        Assert.Equal(42.5, result.Suggested[0, 0]!.Value, 4);   // 50 − 15%
        Assert.Equal(-15.0, result.ChangePercent[0, 0]!.Value, 4);
        Assert.Equal(15.0, result.LargestChangePercent, 4);
    }

    [Fact]
    public void AuthorityScalesHowMuchOfTheCorrectionIsTaken()
    {
        // Converging in steps: the measurement lags the change, so taking the
        // whole of it tends to overshoot.
        VeAnalysisResult result = Analyse(
            [12.6], [14.0], new VeAnalysisSettings { MinimumSamples = 1, Authority = 0.5 });

        Assert.Equal(47.5, result.Suggested[0, 0]!.Value, 4);   // half of −10%
    }

    [Fact]
    public void SamplesLandInTheNearestCell()
    {
        // The ECU interpolates, but the number a tuner changes is the nearest one.
        VeAnalysisResult result = Analyse(
            [12.6, 15.4], [14.0, 14.0], rpm: [1100, 2900], map: [45, 75]);

        Assert.Equal(45.0, result.Suggested[0, 0]!.Value, 4);
        Assert.Equal(55.0, result.Suggested[1, 1]!.Value, 4);
        Assert.Equal(2, result.CellsSuggested);
    }

    [Fact]
    public void AMissingReadingIsSkippedRatherThanCountedAsZero()
    {
        VeAnalysisResult result = Analyse([12.6, double.NaN], [14.0, 14.0]);

        Assert.Equal(1, result.SamplesUsed);
        Assert.Equal(45.0, result.Suggested[0, 0]!.Value, 4);
    }

    [Fact]
    public void AZeroOrNegativeTargetIsNotATarget()
    {
        // Dividing by it would manufacture a correction out of nothing.
        VeAnalysisResult result = Analyse([12.6, 12.6], [0, -1]);

        Assert.Equal(0, result.SamplesUsed);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void ACellTheLogNeverVisitedIsUntouched()
    {
        VeAnalysisResult result = Analyse([12.6], [14.0]);

        Assert.Null(result.Suggested[1, 1]);
        Assert.Null(result.ChangePercent[1, 1]);
        Assert.Equal(0, result.Counts[1, 1]);
    }

    [Fact]
    public void FiltersRestrictWhichSamplesCount()
    {
        var doc = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2, 0.3], preservePrecision: true),
            Channels = [new LogChannel("CLT", "F", 0, [100, 100, 180, 180])],
            FormatName = "test",
        };

        SampleMask warm = SampleFilter.Build(doc, [new LogFilter
        {
            Name = "warm", Channel = "CLT", Comparison = FilterComparison.AboveOrEqual, Low = 160,
        }]);

        VeAnalysisResult result = VeAnalysis.Analyse(
            Table(),
            Channel("RPM", 1000, 1000, 1000, 1000),
            Channel("MAP", 40, 40, 40, 40),
            Channel("AFR", 7.0, 7.0, 12.6, 12.6),      // cold samples wildly rich
            Channel("Target", 14, 14, 14, 14),
            0, 3, warm, new VeAnalysisSettings { MinimumSamples = 1 });

        Assert.Equal(2, result.SamplesUsed);
        Assert.Equal(45.0, result.Suggested[0, 0]!.Value, 4);   // only the warm pair
    }

    [Fact]
    public void AZeroCellCannotBeScaled()
    {
        // Multiplying zero by anything stays zero, so there is nothing to suggest.
        var table = Table(fill: 0);

        VeAnalysisResult result = VeAnalysis.Analyse(
            table, Channel("RPM", 1000), Channel("MAP", 40),
            Channel("AFR", 12.6), Channel("Target", 14),
            0, 0, null, new VeAnalysisSettings { MinimumSamples = 1 });

        Assert.Null(result.Suggested[0, 0]);
    }

    // ----- rendering --------------------------------------------------------

    [Fact]
    public void TheChangeTableDivergesAboutZero()
    {
        // A cell wanting less fuel and one wanting more must not shade alike.
        VeAnalysisResult result = Analyse(
            [12.6, 15.4], [14.0, 14.0], rpm: [1000, 3000], map: [40, 80]);

        HistogramTable table = result.AsChangeTable(
            Channel("RPM", 1000, 3000), Channel("MAP", 40, 80), Channel("AFR", 12.6, 15.4),
            Channel("Target", 14, 14), 0, 1);

        Assert.True(table.ShowsDeviation);
        Assert.Equal(-10.0, table.MinValue, 4);
        Assert.Equal(10.0, table.MaxValue, 4);
        Assert.Equal("+10.0", table.Format(1, 1));
    }

    [Fact]
    public void TheSuggestedTableIsAMagnitudeNotADeviation()
    {
        // A VE number has no midpoint to diverge about.
        VeAnalysisResult result = Analyse([12.6], [14.0]);

        HistogramTable table = result.AsSuggestedTable(
            Channel("RPM", 1000), Channel("MAP", 40), Channel("AFR", 12.6), 0, 0);

        Assert.False(table.ShowsDeviation);
        Assert.Equal("45.0", table.Format(0, 0));
    }

    [Fact]
    public void AComputedTableNamesItselfRatherThanItsZChannel()
    {
        VeAnalysisResult result = Analyse([12.6], [14.0]);

        HistogramTable change = result.AsChangeTable(
            Channel("RPM", 1000), Channel("MAP", 40), Channel("AFR", 12.6),
            Channel("Target", 14), 0, 0);

        Assert.Equal("VE table 1 change, %", change.DisplayName);
    }

    [Fact]
    public void TheChangeTableKeepsTheCountsBehindEachCell()
    {
        // How much data backs a suggestion is the first thing worth knowing.
        VeAnalysisResult result = Analyse([12.6, 12.6, 12.6], [14.0, 14.0, 14.0]);

        HistogramTable table = result.AsChangeTable(
            Channel("RPM", 1000, 1000, 1000), Channel("MAP", 40, 40, 40),
            Channel("AFR", 12.6, 12.6, 12.6), Channel("Target", 14, 14, 14), 0, 2);

        Assert.Equal(3, table.Counts[0, 0]);
        Assert.Equal(3, table.MaxCount);
    }
}

