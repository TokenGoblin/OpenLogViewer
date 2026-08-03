using System.Globalization;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class CsvExportTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string p in _temp) { try { File.Delete(p); } catch (IOException) { } }
    }

    private string Temp()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-export-{Guid.NewGuid():N}.csv");
        _temp.Add(path);
        return path;
    }

    private LogDocument Log(params (string Name, string Units, double[] Values)[] channels)
    {
        int rows = channels[0].Values.Length;
        var lines = new List<string> { string.Join(',', new[] { "Time" }.Concat(channels.Select(c => c.Name))) };
        lines.Add(string.Join(',', new[] { "s" }.Concat(channels.Select(c => c.Units))));

        for (int r = 0; r < rows; r++)
        {
            var cells = new List<string> { (r * 0.1).ToString(CultureInfo.InvariantCulture) };
            cells.AddRange(channels.Select(c => double.IsNaN(c.Values[r])
                ? ""
                : c.Values[r].ToString(CultureInfo.InvariantCulture)));
            lines.Add(string.Join(',', cells));
        }

        string path = Path.Combine(Path.GetTempPath(), $"olv-src-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        _temp.Add(path);
        return LogReaderFactory.Load(path);
    }

    private string Write(LogDocument doc, IReadOnlyList<LogChannel> channels, int first, int last)
    {
        var writer = new StringWriter { NewLine = "\n" };
        CsvExport.WriteLog(writer, doc, channels, first, last);
        return writer.ToString();
    }

    [Fact]
    public void TheTimeBaseLeadsAndTheHeaderCarriesUnits()
    {
        LogDocument doc = Log(("RPM", "RPM", [1500, 1600]), ("MAP", "kPa", [45, 46]));

        string[] lines = Write(doc, doc.Channels, 0, 1).Split('\n');

        Assert.Equal("Time,RPM,MAP", lines[0]);
        Assert.Equal("s,RPM,kPa", lines[1]);
        Assert.Equal("0,1500,45", lines[2]);
        Assert.Equal("0.1,1600,46", lines[3]);
    }

    [Fact]
    public void ALogWithNoUsableTimeColumnKeepsThatColumnAsData()
    {
        // A single sample cannot establish a time base, so one is synthesised and
        // the file's own Time column is ordinary data — excluding it would drop
        // the column the log actually carried.
        LogDocument doc = Log(("AFR", "AFR", [13.4]));

        Assert.Equal("Sample", doc.Time.Name);
        Assert.Equal("Sample,Time,AFR", Write(doc, doc.Channels, 0, 0).Split('\n')[0]);
    }

    [Fact]
    public void TheTimeColumnIsNeverWrittenTwice()
    {
        // Callers pass "the channels", and the document's own list contains the
        // time base; a second Time column would make the file ambiguous.
        LogDocument doc = Log(("RPM", "RPM", [1500, 1600]));

        string header = Write(doc, [doc.Time, .. doc.Channels], 0, 1).Split('\n')[0];

        Assert.Equal("Time,RPM", header);
    }

    [Fact]
    public void OnlyTheRequestedSampleRangeIsWritten()
    {
        LogDocument doc = Log(("RPM", "RPM", [1000, 2000, 3000, 4000]));

        string[] lines = Write(doc, doc.Channels, 1, 2).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);           // header, units, two samples
        Assert.EndsWith("2000", lines[2]);
        Assert.EndsWith("3000", lines[3]);
    }

    [Fact]
    public void ARangeRunningPastTheLogIsClamped()
    {
        LogDocument doc = Log(("RPM", "RPM", [1000, 2000]));

        string[] lines = Write(doc, doc.Channels, -5, 900).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void AMissingReadingIsAnEmptyCellRatherThanNaN()
    {
        // "NaN" would be read back as a label and could break the header
        // detection of whatever opens the file.
        LogDocument doc = Log(("RPM", "RPM", [1000, double.NaN]));

        string[] lines = Write(doc, doc.Channels, 0, 1).Split('\n');

        Assert.Equal("0.1,", lines[3]);
    }

    [Fact]
    public void SamplesAreWrittenAtTheirOwnPrecisionNotTheirFloatError()
    {
        // Samples are stored as float. Formatting the widened double would write
        // 13.399999618530273 for a reading of 13.4.
        LogDocument doc = Log(("AFR", "AFR", [13.4, 14.7]));

        string[] lines = Write(doc, doc.Channels, 0, 1).Split('\n');

        Assert.Equal("0,13.4", lines[2]);
        Assert.Equal("0.1,14.7", lines[3]);
    }

    [Fact]
    public void TheTimeBaseKeepsItsFullPrecision()
    {
        // The time base is the one column held as double, because it accumulates.
        LogDocument doc = Log(("RPM", "RPM", [1000, 2000, 3000]));

        string body = Write(doc, doc.Channels, 0, 2);

        Assert.Contains("0.2,", body);
    }

    [Fact]
    public void ATimeBaseTakenFromA32BitFieldPrintsShort()
    {
        // MLG holds Time as an f32. The base keeps it as a double so long logs
        // stay monotonic, but the value is only a widened float and would print
        // as 0.006000000052154064.
        var doc = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0f, 0.006f, 0.073f], preservePrecision: true),
            Channels = [new LogChannel("RPM", "RPM", 0, [1000, 1100, 1200])],
            FormatName = "test",
        };

        string[] lines = Write(doc, doc.Channels, 0, 2).Split('\n');

        Assert.Equal("0.006,1100", lines[3]);
        Assert.Equal("0.073,1200", lines[4]);
    }

    [Fact]
    public void ATimeBaseThatNeedsFullPrecisionKeepsIt()
    {
        // A text log's clock can carry more than a float holds; narrowing it
        // would make a long recording stop advancing.
        var doc = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [6310.743, 6310.81], preservePrecision: true),
            Channels = [new LogChannel("RPM", "RPM", 0, [1000, 1100])],
            FormatName = "test",
        };

        string[] lines = Write(doc, doc.Channels, 0, 1).Split('\n');

        Assert.Equal("6310.743,1000", lines[2]);
        Assert.Equal("6310.81,1100", lines[3]);
    }

    [Fact]
    public void NumbersAreInvariantWhateverTheMachineIsSetTo()
    {
        // A file written on a comma-decimal machine has to open elsewhere. The
        // separator is a comma too, so getting this wrong also shifts columns.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            LogDocument doc = Log(("AFR", "AFR", [13.4, 14.7]));
            string[] lines = Write(doc, doc.Channels, 0, 1).Split('\n');

            Assert.Equal("0,13.4", lines[2]);
            Assert.Equal("0.1,14.7", lines[3]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AChannelNameContainingACommaIsQuoted()
    {
        var doc = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 2, [0, 0.1], preservePrecision: true),
            Channels = [new LogChannel("Fuel, total", "l", 1, [1, 2])],
            FormatName = "test",
        };

        Assert.Equal("Time,\"Fuel, total\"", Write(doc, doc.Channels, 0, 1).Split('\n')[0]);
    }

    [Fact]
    public void AnExportedLogReadsBackIntoTheApp()
    {
        // The whole point of writing a header and a units row: the file is a log
        // this app can open, not just something a spreadsheet accepts.
        LogDocument original = Log(
            ("RPM", "RPM", [1500, 1600, 1700]),
            ("AFR", "AFR", [13.4, 14.7, 12.1]));

        string path = Temp();
        using (var writer = new StreamWriter(path)) CsvExport.WriteLog(writer, original, original.Channels, 0, 2);

        LogDocument reloaded = LogReaderFactory.Load(path);

        Assert.Equal(3, reloaded.SampleCount);
        Assert.Equal(["Time", "RPM", "AFR"], reloaded.Channels.Select(c => c.Name));
        Assert.Equal("AFR", reloaded.FindChannel("AFR")!.Units);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(original.FindChannel("RPM")!.At(i), reloaded.FindChannel("RPM")!.At(i), 4);
            Assert.Equal(original.FindChannel("AFR")!.At(i), reloaded.FindChannel("AFR")!.At(i), 4);
            Assert.Equal(original.Time.At(i), reloaded.Time.At(i), 6);
        }
    }

    [Fact]
    public void AGapInLoggingSurvivesTheRoundTrip()
    {
        // Gap detection is derived from the time column, so a reload has to see
        // the same intervals or a paused log would come back as continuous.
        var doc = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2, 60.2, 60.3], preservePrecision: true),
            Channels = [new LogChannel("RPM", "RPM", 0, [1000, 1100, 1200, 1300, 1400])],
            FormatName = "test",
        };

        string path = Temp();
        using (var writer = new StreamWriter(path)) CsvExport.WriteLog(writer, doc, doc.Channels, 0, 4);

        LogDocument reloaded = LogReaderFactory.Load(path);

        Assert.Equal(doc.MedianSampleInterval, reloaded.MedianSampleInterval, 6);
        Assert.Equal(doc.GapThreshold, reloaded.GapThreshold, 6);
    }

    // ----- heat table -------------------------------------------------------

    private static HistogramTable Table()
    {
        var x = new LogChannel("RPM", "RPM", 0, [1000, 1000, 5000, 5000]);
        var y = new LogChannel("MAP", "kPa", 0, [40, 40, 90, 90]);
        var z = new LogChannel("AFR", "AFR", 2, [14.0, 15.0, 11.0, 12.0]);

        return HistogramTable.Build(x, y, z, 2, 2, 0, 3, HistogramStatistic.Mean);
    }

    private static string[] TableLines(Action<StringWriter> write)
    {
        var writer = new StringWriter { NewLine = "\n" };
        write(writer);
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void TheTableIsWrittenHighestRowFirst()
    {
        // Matching what is on screen, and what a tuning app expects when the
        // block is pasted in.
        string[] lines = TableLines(w => CsvExport.WriteTable(w, Table()));

        Assert.Equal(3, lines.Length);
        Assert.Equal("MAP \\ RPM,2000,4000", lines[0]);
        Assert.StartsWith("77.5,", lines[1]);      // the higher MAP band
        Assert.StartsWith("52.5,", lines[2]);
    }

    [Fact]
    public void CellsHoldTheirValuesAndEmptyOnesStayEmpty()
    {
        // Writing an unvisited cell as 0 would read as a measurement of nothing.
        string[] lines = TableLines(w => CsvExport.WriteTable(w, Table()));

        Assert.Equal("77.5,,11.5", lines[1]);
        Assert.Equal("52.5,14.5,", lines[2]);
    }

    [Fact]
    public void CountsAreWrittenInTheSameShape()
    {
        string[] lines = TableLines(w => CsvExport.WriteTableCounts(w, Table()));

        Assert.Equal("MAP \\ RPM,2000,4000", lines[0]);
        Assert.Equal("77.5,0,2", lines[1]);
        Assert.Equal("52.5,2,0", lines[2]);
    }

    [Fact]
    public void TableNumbersAreAlsoInvariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("77.5,,11.5", TableLines(w => CsvExport.WriteTable(w, Table()))[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ARecordingCarriesAByteOrderMarkAndStillReadsBack()
    {
        // Excel reads a CSV without one in the system codepage whatever is
        // actually in it, so a channel in °C arrives as Â°C — and OBD2 made
        // degree signs ordinary. The mark fixes that, and must not cost the
        // application's own reader, which is the half worth asserting.
        string path = Path.Combine(Path.GetTempPath(), $"olv-bom-{Guid.NewGuid():N}.csv");
        _temp.Add(path);

        var session = new LiveSession(
            new FixedSource(["Coolant"], ["°C"], [0]),
            new LiveSessionSettings { RecordingPath = path, MaximumRate = 0 });

        session.Start();
        Thread.Sleep(120);
        session.Stop();
        session.Dispose();

        byte[] bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length > 3);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);

        LogDocument reopened = LogReaderFactory.Load(path);
        LogChannel coolant = Assert.Single(reopened.Channels, c => c.Name == "Coolant");

        // The name is the real check: a byte-order mark read as data would make
        // the first column "﻿Time" and nothing would match by name again.
        Assert.Equal("°C", coolant.Units);
        Assert.Contains(reopened.Channels, c => c.Name == "Time");
    }

    /// <summary>A source that answers instantly with the same row, for a recording test.</summary>
    private sealed class FixedSource(string[] names, string[] units, double[] row) : ILiveSource
    {
        public IReadOnlyList<string> Names => names;

        public IReadOnlyList<string> Units => units;

        public IReadOnlyList<int> Digits => [.. names.Select(_ => 1)];

        public int Retries => 0;

        public void Open() { }

        public double[] Read() => [.. row];

        public void Recover() { }

        public void Dispose() { }
    }
}
