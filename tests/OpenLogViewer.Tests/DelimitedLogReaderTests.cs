using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class DelimitedLogReaderTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string Write(string extension, params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}{extension}");
        File.WriteAllLines(path, lines);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string p in _temp) File.Delete(p);
    }

    [Fact]
    public void ParsesTunerStudioMslWithSignatureAndUnitsRow()
    {
        string path = Write(".msl",
            "\"MS3 Format 0592.12P: test\"",
            "\"Capture Date: Fri Jul 31 10:00:00 EDT 2026\"",
            "Time\tRPM\tMAP\tAFR",
            "s\tRPM\tkPa\tAFR",
            "0.000\t1500\t45.0\t14.7",
            "0.020\t1600\t46.5\t14.6",
            "0.040\t1700\t48.0\t14.5");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("TunerStudio tab-delimited", log.FormatName);
        Assert.Equal(4, log.Channels.Count);
        Assert.Equal(3, log.SampleCount);
        Assert.Equal("MS3 Format 0592.12P: test", log.Signature);

        LogChannel map = log.FindChannel("MAP")!;
        Assert.Equal("kPa", map.Units);
        Assert.Equal(45.0, map.At(0), 3);
        Assert.Equal(48.0, map.Max, 3);
    }

    [Fact]
    public void ParsesCsvWithoutAUnitsRow()
    {
        string path = Write(".csv",
            "Time,RPM,MAP",
            "0.0,1500,45.0",
            "0.1,1600,46.0",
            "0.2,1700,47.0");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("CSV", log.FormatName);
        Assert.Equal(3, log.SampleCount);
        Assert.Equal(1700, log.FindChannel("RPM")!.Max);
        // With no preamble there is no signature to report.
        Assert.Null(log.Signature);
    }

    [Fact]
    public void SkipsRaggedAndAnnotationRows()
    {
        string path = Write(".msl",
            "Time\tRPM\tMAP",
            "s\tRPM\tkPa",
            "0.0\t1500\t45.0",
            "MARK - an annotation",
            "0.1\t1600\t46.0",
            "",
            "0.2\t1700\t47.0");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal(3, log.SampleCount);
        Assert.Equal([1500, 1600, 1700], log.FindChannel("RPM")!.ToArray());
    }

    [Fact]
    public void UsesTheTimeColumnAsTheTimeBase()
    {
        string path = Write(".csv",
            "Time,RPM",
            "0.0,1500",
            "0.5,1600",
            "1.0,1700");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("Time", log.Time.Name);
        Assert.Equal(1.0, log.Duration, 3);
    }

    [Fact]
    public void FallsBackToSampleIndexWhenTimeIsNotMonotonic()
    {
        string path = Write(".csv",
            "Time,RPM",
            "5.0,1500",
            "1.0,1600",
            "3.0,1700");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("Sample", log.Time.Name);
        Assert.Equal(3, log.SampleCount);
    }

    [Fact]
    public void NonNumericCellsBecomeNaNWithoutBreakingTheColumn()
    {
        string path = Write(".csv",
            "Time,RPM",
            "0.0,1500",
            "0.1,n/a",
            "0.2,1700");

        LogChannel rpm = LogReaderFactory.Load(path).FindChannel("RPM")!;

        Assert.True(double.IsNaN(rpm.At(1)));
        // Min/max must ignore the gap rather than propagate NaN.
        Assert.Equal(1500, rpm.Min);
        Assert.Equal(1700, rpm.Max);
    }

    [Fact]
    public void ReportsAClearErrorForUnrecognisedContent()
    {
        string path = Write(".txt", "this file", "has no tabular data at all");

        Assert.Throws<LogFormatException>(() => LogReaderFactory.Load(path));
    }

    [Fact]
    public void ParsesSemicolonSeparatedDecimalCommaExport()
    {
        // Common for European-locale exports.
        string path = Write(".csv",
            "Time;RPM;MAP",
            "0,000;1500;45,5",
            "0,100;1600;46,5",
            "0,200;1700;47,5");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("semicolon CSV", log.FormatName);
        Assert.Equal(3, log.SampleCount);
        Assert.Equal(45.5, log.FindChannel("MAP")!.At(0), 3);
        Assert.Equal(0.2, log.Duration, 3);
    }

    [Fact]
    public void HonoursQuotedFieldsContainingTheDelimiter()
    {
        string path = Write(".csv",
            "\"Time\",\"Engine Speed, rpm\",\"MAP\"",
            "0.0,1500,45.0",
            "0.1,1600,46.0",
            "0.2,1700,47.0");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal(3, log.Channels.Count);
        Assert.NotNull(log.FindChannel("Engine Speed, rpm"));
    }

    [Fact]
    public void ExtractsUnitsBracketedInTheHeader()
    {
        string path = Write(".csv",
            "Time (s),RPM (rpm),MAP [kPa],CLT {degC}",
            "0.0,1500,45.0,80.0",
            "0.1,1600,46.0,81.0",
            "0.2,1700,47.0,82.0");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("kPa", log.FindChannel("MAP")!.Units);
        Assert.Equal("rpm", log.FindChannel("RPM")!.Units);
        Assert.Equal("degC", log.FindChannel("CLT")!.Units);
        Assert.Equal(0.2, log.Duration, 3);
    }

    [Fact]
    public void DisambiguatesRepeatedChannelNames()
    {
        // MS3 emits "Fuel Consumption" twice, in different units.
        string path = Write(".msl",
            "Time\tFuel Consumption\tFuel Consumption",
            "s\tGPH\tl/hr",
            "0.0\t1.0\t3.8",
            "0.1\t1.1\t4.2",
            "0.2\t1.2\t4.5");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal(3, log.Channels.Count);
        Assert.Equal(3, log.Channels.Select(c => c.Name).Distinct().Count());
        Assert.Equal(4.5, log.FindChannel("Fuel Consumption (l/hr)")!.Max, 3);
    }

    [Fact]
    public void ConvertsAMillisecondTimeBaseToSeconds()
    {
        string path = Write(".csv",
            "Time,RPM",
            "0,1500",
            "500,1600",
            "1000,1700");

        // Units come from the bracketed header form here.
        string path2 = Write(".csv",
            "Time (ms),RPM",
            "0,1500",
            "500,1600",
            "1000,1700");

        Assert.Equal(1000, LogReaderFactory.Load(path).Duration, 3);
        Assert.Equal(1.0, LogReaderFactory.Load(path2).Duration, 3);
    }

    [Fact]
    public void BuildsATimeBaseFromWallClockTimestamps()
    {
        string path = Write(".csv",
            "Timestamp,RPM",
            "2026-07-31 10:00:00.000,1500",
            "2026-07-31 10:00:00.500,1600",
            "2026-07-31 10:00:01.000,1700");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("Time", log.Time.Name);
        Assert.Equal(1.0, log.Duration, 3);
    }

    [Fact]
    public void ReadsLatin1DegreeSymbols()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}.msl");
        File.WriteAllText(path, string.Join('\n',
            "Time\tCLT",
            "s\t°F",
            "0.0\t180.0",
            "0.1\t181.0",
            "0.2\t182.0"), System.Text.Encoding.Latin1);
        _temp.Add(path);

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("°F", log.FindChannel("CLT")!.Units);
        Assert.Contains("Latin-1", log.FormatName);
    }

    [Fact]
    public void AcceptsATimeBaseThatDoesNotStartAtZero()
    {
        // Real MS3 dyno logs start the clock partway through a session.
        string path = Write(".msl",
            "Time\tRPM",
            "s\tRPM",
            "2178.174\t780",
            "2178.274\t800",
            "2178.374\t820");

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal(2178.174, log.Time.At(0), 3);
        Assert.Equal(0.2, log.Duration, 3);
    }

    [Theory]
    [InlineData("\"MS3 Format 0435.16 : MS3 release\"", "TunerStudio tab-delimited")]
    [InlineData("\"MaxxECU log export, MaxxTuner 1.234\"", "MaxxECU tab-delimited")]
    [InlineData("\"rusEFI console log\"", "rusEFI tab-delimited")]
    [InlineData("\"Haltech NSP datalog\"", "Haltech tab-delimited")]
    public void NamesTheProducingToolFromThePreamble(string preamble, string expected)
    {
        string path = Write(".msl",
            preamble,
            "\"Capture Date: whenever\"",
            "Time\tRPM",
            "s\tRPM",
            "0.0\t1500",
            "0.1\t1600",
            "0.2\t1700");

        Assert.Equal(expected, LogReaderFactory.Load(path).FormatName);
    }
}
