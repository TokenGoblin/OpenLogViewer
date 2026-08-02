using System.IO.Compression;
using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading a MaxxECU log, which is a zip of a tab-separated log, a metadata
/// line giving the sample interval, and the tune that was running.
/// </summary>
public class MaxxLogTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>Builds an archive shaped like the real thing.</summary>
    private string WriteLog(string body, string? metadata = "LogRate=0.0501985546875", bool withLog = true)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-maxx-{Guid.NewGuid():N}.MaxxECU-Zip-log");
        _temp.Add(path);

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (withLog)
            {
                ZipArchiveEntry log = archive.CreateEntry("log_2025-01-16_123737.MaxxECU-Log");
                using var writer = new StreamWriter(log.Open(), Encoding.ASCII);
                writer.Write(body);
            }

            if (metadata is not null)
            {
                ZipArchiveEntry meta = archive.CreateEntry("fileinfo01.LogMetaData");
                using var writer = new StreamWriter(meta.Open(), Encoding.ASCII);
                writer.Write(metadata);
            }

            archive.CreateEntry("V8 2025-01-16_123744.MaxxECU-save");
        }

        return path;
    }

    /// <summary>Header names carry MTune's channel index in brackets.</summary>
    private const string Body =
        "RPM [61]\tCoolant temp [18]\tMAP [20]\tLambda [5]\t\n"
        + "800\t85.5\t101.3\t0.98\t\n"
        + "1200\t86.1\t150.2\t0.85\t\n"
        + "3000\t87.0\t218.7\t0.61\t\n";

    [Fact]
    public void AMaxxEcuArchiveIsRecognised() => Assert.True(new MaxxLogReader().CanRead(WriteLog(Body)));

    [Fact]
    public void SomethingElseIsNot()
    {
        // A zip with no MaxxECU log in it, and a file that is not a zip at all.
        string notMaxx = Path.Combine(Path.GetTempPath(), $"olv-other-{Guid.NewGuid():N}.zip");
        _temp.Add(notMaxx);

        using (var archive = ZipFile.Open(notMaxx, ZipArchiveMode.Create))
            archive.CreateEntry("something.txt");

        Assert.False(new MaxxLogReader().CanRead(notMaxx));

        string text = Path.Combine(Path.GetTempPath(), $"olv-text-{Guid.NewGuid():N}.csv");
        _temp.Add(text);
        File.WriteAllText(text, "Time,RPM\n0,800\n");

        Assert.False(new MaxxLogReader().CanRead(text));
    }

    [Fact]
    public void TheFactoryPicksItUp()
    {
        LogDocument document = LogReaderFactory.Load(WriteLog(Body));

        Assert.Equal("MaxxECU", document.FormatName);
    }

    [Fact]
    public void ChannelsKeepTheirNamesWithoutTheIndex()
    {
        // The bracketed index is how MTune's definitions are keyed, and noise in
        // a channel list.
        LogDocument document = LogReaderFactory.Load(WriteLog(Body));

        Assert.Equal(["RPM", "Coolant temp", "MAP", "Lambda"], document.Channels.Select(c => c.Name));
    }

    [Fact]
    public void ValuesArriveAsRecorded()
    {
        LogDocument document = LogReaderFactory.Load(WriteLog(Body));

        Assert.Equal(3, document.SampleCount);
        Assert.Equal(800, document.FindChannel("RPM")!.At(0), 3);
        Assert.Equal(218.7, document.FindChannel("MAP")!.At(2), 3);
        Assert.Equal(0.61, document.FindChannel("Lambda")!.At(2), 3);
    }

    [Fact]
    public void TimeComesFromTheDeclaredRate()
    {
        // There is no time column; the metadata is the only thing that says when
        // anything happened.
        LogDocument document = LogReaderFactory.Load(WriteLog(Body));

        Assert.Equal(0, document.Time.At(0), 6);
        Assert.Equal(0.0501985546875, document.Time.At(1), 9);
        Assert.Equal(0.100397109375, document.Time.At(2), 9);
    }

    [Fact]
    public void AMissingRateStillOpens()
    {
        // Wrong by a constant keeps every trace the right shape and only
        // mislabels the axis, which beats refusing the file.
        LogDocument document = LogReaderFactory.Load(WriteLog(Body, metadata: null));

        Assert.Equal(3, document.SampleCount);
        Assert.True(document.Time.At(1) > document.Time.At(0));
    }

    [Fact]
    public void AShortRowLosesOnlyItsOwnChannels()
    {
        // Filling the gap keeps every later channel in its own column; dropping
        // it would shift them all up by one.
        const string ragged =
            "RPM [61]\tCoolant temp [18]\tMAP [20]\t\n"
            + "800\t85.5\t101.3\t\n"
            + "900\t86.0\t\n";

        LogDocument document = LogReaderFactory.Load(WriteLog(ragged));

        Assert.Equal(900, document.FindChannel("RPM")!.At(1), 3);
        Assert.Equal(86.0, document.FindChannel("Coolant temp")!.At(1), 3);
        Assert.True(double.IsNaN(document.FindChannel("MAP")!.At(1)));
    }

    [Fact]
    public void TwoChannelsOfTheSameNameStaySeparate()
    {
        // Names have to be unique: a preset or a filter matches on one.
        const string duplicated = "RPM [61]\tRPM [62]\t\n800\t810\t\n";

        LogDocument document = LogReaderFactory.Load(WriteLog(duplicated));

        Assert.Equal(2, document.Channels.Count);
        Assert.Equal(2, document.Channels.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void AnArchiveWithNoLogIsRefusedClearly()
    {
        string path = WriteLog(Body, withLog: false);

        Assert.False(new MaxxLogReader().CanRead(path));
        Assert.Throws<LogFormatException>(() => LogReaderFactory.Load(path));
    }

    [Fact]
    public void AnEmptyLogIsRefusedRatherThanOpenedEmpty()
    {
        Assert.Throws<LogFormatException>(
            () => LogReaderFactory.Load(WriteLog("RPM [61]\t\n")));
    }
}
