using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class TuneAxesTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string p in _temp) File.Delete(p);
    }

    /// <summary>An MSQ shaped like the real thing, default namespace and all.</summary>
    private static string Msq(params (string Name, string Units, string Values)[] constants)
    {
        string body = string.Join("\n", constants.Select(c =>
            $"""<constant cols="1" digits="0" name="{c.Name}" rows="4" units="{c.Units}">{c.Values}</constant>"""));

        return $"""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <msq xmlns="http://www.msefi.com/:msq">
            <page>
            {body}
            </page>
            </msq>
            """;
    }

    // ----- breakpoint parsing ------------------------------------------------

    [Fact]
    public void AscendingBreakpointsAreRead()
    {
        double[]? axis = MsqTune.ParseBreakpoints("500.0 800.0 1100.0 1400.0");

        Assert.Equal([500, 800, 1100, 1400], axis!);
    }

    [Fact]
    public void PaddingRepeatsAtTheTopAreCollapsed()
    {
        // Firmwares pad an axis to the table width by repeating the top value;
        // kept as-is they would create zero-width bins.
        double[]? axis = MsqTune.ParseBreakpoints("500 800 1100 6500 7000 7000 7000");

        Assert.Equal([500, 800, 1100, 6500, 7000], axis!);
    }

    [Fact]
    public void RolledDozenAxesAreRejected()
    {
        // frpm_table2doz is stored out of order, not ascending; binning onto it
        // would silently scramble the table.
        double[]? axis = MsqTune.ParseBreakpoints("5200 5700 6100 6500 502 801 1101 1401");

        Assert.Null(axis);
    }

    [Theory]
    [InlineData("500 800")]              // too few points to be an axis
    [InlineData("500 500 500")]          // collapses to one point
    [InlineData("500 800 not-a-number")]
    [InlineData("")]
    public void UnusableAxesAreRejected(string text) =>
        Assert.Null(MsqTune.ParseBreakpoints(text));

    // ----- axis set discovery ------------------------------------------------

    [Fact]
    public void KnownTablePairsAreFoundThroughTheDefaultNamespace()
    {
        string msq = Msq(
            ("frpm_table1", "RPM", "500 800 1100 1400"),
            ("fmap_table1", "kPa", "30 50 70 100"),
            ("srpm_table1", "RPM", "700 1000 1300 1600"),
            ("smap_table1", "kPa", "20 40 60 80"));

        var sets = MsqTune.ReadAxisSets(msq);

        Assert.Equal(2, sets.Count);
        Assert.Equal("VE table 1", sets[0].Name);
        Assert.Equal([500, 800, 1100, 1400], sets[0].X.Breakpoints);
        Assert.Equal("kPa", sets[0].Y.Units);
        Assert.Equal("Spark table 1", sets[1].Name);
    }

    [Fact]
    public void APairMissingOneAxisIsSkipped()
    {
        string msq = Msq(("frpm_table1", "RPM", "500 800 1100 1400"));

        Assert.Empty(MsqTune.ReadAxisSets(msq));
    }

    [Fact]
    public void APairWithAnUnusableAxisIsSkipped()
    {
        string msq = Msq(
            ("frpm_table1", "RPM", "5200 5700 6100 502"),   // rolled
            ("fmap_table1", "kPa", "30 50 70 100"));

        Assert.Empty(MsqTune.ReadAxisSets(msq));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<msq><unclosed></msq>")]
    public void UnusableTuneTextYieldsNoAxes(string? xml) =>
        Assert.Empty(MsqTune.ReadAxisSets(xml));

    // ----- binning onto tune breakpoints -------------------------------------

    private static LogChannel Channel(string name, params double[] values) => new(name, "", 2, values);

    [Fact]
    public void SamplesGoToTheNearestBreakpoint()
    {
        // Breakpoints 1000/2000/3000: 1400 is nearer 1000, 1600 nearer 2000.
        var x = Channel("RPM", 1400, 1600, 2900);
        var y = Channel("MAP", 50, 50, 50);
        var z = Channel("AFR", 10, 20, 30);

        HistogramTable table = HistogramTable.Build(
            x, y, z, [1000, 2000, 3000], [50], 0, 2, HistogramStatistic.Mean);

        Assert.True(table.FromTune);
        Assert.Equal(10, table.Values[0, 0]);
        Assert.Equal(20, table.Values[1, 0]);
        Assert.Equal(30, table.Values[2, 0]);
    }

    [Fact]
    public void ValuesOutsideTheBreakpointsFallToTheNearestEnd()
    {
        // Nothing is discarded for being off the end of the tune's axis.
        var x = Channel("RPM", 100, 9000);
        var y = Channel("MAP", 50, 50);
        var z = Channel("AFR", 11, 12);

        HistogramTable table = HistogramTable.Build(
            x, y, z, [1000, 2000, 3000], [50], 0, 1, HistogramStatistic.Mean);

        Assert.Equal(11, table.Values[0, 0]);
        Assert.Equal(12, table.Values[2, 0]);
        Assert.Equal(2, table.SampleCount);
    }

    [Fact]
    public void UnevenBreakpointSpacingIsHonoured()
    {
        // A real RPM axis is tight at idle and wide up top. Uniform arithmetic
        // over the same range would put both of these in the wrong cell.
        double[] rpm = [500, 800, 1100, 1400, 2000, 2600, 3100, 6000];
        var x = Channel("RPM", 1300, 5000);
        var y = Channel("MAP", 40, 40);
        var z = Channel("AFR", 14, 12);

        HistogramTable table = HistogramTable.Build(
            x, y, z, rpm, [40], 0, 1, HistogramStatistic.Mean);

        Assert.Equal(14, table.Values[3, 0]);   // 1300 -> 1400
        Assert.Equal(12, table.Values[7, 0]);   // 5000 -> 6000
    }

    [Fact]
    public void AValueExactlyBetweenBreakpointsTakesTheLowerOne()
    {
        // 1250 is equidistant from 1100 and 1400; the choice is arbitrary but
        // must be consistent, or the same log would bin differently each run.
        var x = Channel("RPM", 1250);
        var y = Channel("MAP", 40);
        var z = Channel("AFR", 13);

        HistogramTable table = HistogramTable.Build(
            x, y, z, [1100, 1400], [40], 0, 0, HistogramStatistic.Mean);

        Assert.Equal(13, table.Values[0, 0]);
        Assert.Null(table.Values[1, 0]);
    }

    [Fact]
    public void TuneAxesCombineWithFilters()
    {
        var x = Channel("RPM", 1000, 1000, 2000, 2000);
        var y = Channel("MAP", 50, 50, 50, 50);
        var z = Channel("AFR", 10, 20, 30, 40);
        var clt = Channel("CLT", 100, 180, 100, 180);

        var doc = new LogDocument
        {
            FilePath = "x",
            Channels = [x, y, z, clt],
            Time = Channel("Time", 0, 1, 2, 3),
            FormatName = "test",
        };

        SampleMask warm = SampleFilter.Build(doc, [new LogFilter
        {
            Name = "warm", Channel = "CLT", Comparison = FilterComparison.AboveOrEqual, Low = 160,
        }]);

        HistogramTable table = HistogramTable.Build(
            x, y, z, [1000, 2000], [50], 0, 3, HistogramStatistic.Mean, warm);

        Assert.Equal(2, table.SampleCount);
        Assert.Equal(20, table.Values[0, 0]);
        Assert.Equal(40, table.Values[1, 0]);
    }

    // ----- extraction from a log --------------------------------------------

    [Fact]
    public void TheTuneIsRecoveredFromAnMlg()
    {
        string msq = Msq(
            ("frpm_table1", "RPM", "500 800 1100 1400"),
            ("fmap_table1", "kPa", "30 50 70 100"));

        var builder = new MlgBuilder()
            .Add(MlgDataType.F32, "Time", "s")
            .Add(MlgDataType.U16, "RPM", "RPM");

        string path = builder.BuildFile(5, (f, s) => f == 0 ? s : 1000 + s, embeddedTune: msq);
        _temp.Add(path);

        LogDocument log = LogReaderFactory.Load(path);

        Assert.NotNull(log.EmbeddedTune);
        Assert.Contains("frpm_table1", log.EmbeddedTune);

        var sets = MsqTune.ReadAxisSets(log.EmbeddedTune);
        Assert.Single(sets);
        Assert.Equal([500, 800, 1100, 1400], sets[0].X.Breakpoints);
    }

    [Fact]
    public void ALogWithNoTuneSimplyHasNone()
    {
        var builder = new MlgBuilder()
            .Add(MlgDataType.F32, "Time", "s")
            .Add(MlgDataType.U16, "RPM", "RPM");

        string path = builder.BuildFile(3, (f, s) => s);
        _temp.Add(path);

        Assert.Null(LogReaderFactory.Load(path).EmbeddedTune);
    }

    // ----- units an INI left as an expression --------------------------------

    [Theory]
    [InlineData("kPa", "kPa")]
    [InlineData("RPM", "RPM")]
    [InlineData("%", "%")]
    [InlineData("  deg  ", "deg")]
    public void APlainUnitIsShownAsItIs(string declared, string expected) =>
        Assert.Equal(expected, new TuneAxis("c", declared, [1]).PlainUnits);

    [Theory]
    // What MS2Extra actually declares for its load axis, because the axis is
    // kilopascals on speed density and per cent on alpha-N and the ECU decides
    // at runtime. Nothing here evaluates it, and printing it verbatim put a line
    // of INI source where a tuner expected "kPa".
    [InlineData("{ bitStringValue( algorithmUnits , algorithm ) }")]
    [InlineData("{ someExpression }")]
    [InlineData("bitStringValue(a, b)")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnythingToBeEvaluatedIsShownAsNothing(string declared) =>
        Assert.Equal("", new TuneAxis("c", declared, [1]).PlainUnits);

    [Fact]
    public void NoUnitBeatsAWrongOne()
    {
        // The breakpoints are still shown either way. A number with no unit
        // reads as an unlabelled axis; a number with an INI expression after it
        // reads as a bug, which is what it was.
        var axis = new TuneAxis("mapBins", "{ bitStringValue( algorithmUnits , algorithm ) }", [30, 100]);

        Assert.Empty(axis.PlainUnits);
        Assert.Equal(2, axis.Breakpoints.Length);
    }
}
