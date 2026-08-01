using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class LogDocumentTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string Write(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, lines);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string p in _temp) File.Delete(p);
    }

    private LogDocument Build(params double[] times)
    {
        var lines = new List<string> { "Time,RPM" };
        lines.AddRange(times.Select((t, i) => $"{t.ToString(System.Globalization.CultureInfo.InvariantCulture)},{1500 + i}"));
        return LogReaderFactory.Load(Write([.. lines]));
    }

    [Fact]
    public void MedianIntervalIgnoresAPauseInLogging()
    {
        // Steady 0.1 s sampling with one 60 s pause part way through.
        LogDocument log = Build(0.0, 0.1, 0.2, 0.3, 60.3, 60.4, 60.5, 60.6);

        Assert.Equal(0.1, log.MedianSampleInterval, 3);
        Assert.Equal(1.0, log.GapThreshold, 3);
    }

    [Fact]
    public void GapThresholdSeparatesRealPausesFromJitter()
    {
        LogDocument log = Build(0.0, 0.1, 0.21, 0.29, 0.4, 0.5);

        // Ordinary jitter must stay below the threshold.
        double[] t = log.Time.Values;
        for (int i = 1; i < t.Length; i++)
            Assert.True(t[i] - t[i - 1] < log.GapThreshold);
    }

    [Fact]
    public void GapThresholdIsInfiniteWhenThereIsNoUsableInterval()
    {
        LogDocument log = Build(5.0, 5.0);

        Assert.True(double.IsPositiveInfinity(log.GapThreshold));
    }

    [Fact]
    public void ChannelRecordsWhereItsExtremesOccur()
    {
        string path = Write(
            "Time,RPM",
            "0.0,1500",
            "0.1,900",
            "0.2,6200",
            "0.3,3000");

        LogChannel rpm = LogReaderFactory.Load(path).FindChannel("RPM")!;

        Assert.Equal(6200, rpm.Max);
        Assert.Equal(2, rpm.MaxIndex);
        Assert.Equal(900, rpm.Min);
        Assert.Equal(1, rpm.MinIndex);
    }

    [Fact]
    public void ExtremeIndexesIgnoreGapsAndTakeTheFirstOccurrence()
    {
        string path = Write(
            "Time,RPM",
            "0.0,4000",
            "0.1,n/a",
            "0.2,4000",
            "0.3,1000",
            "0.4,1000");

        LogChannel rpm = LogReaderFactory.Load(path).FindChannel("RPM")!;

        Assert.Equal(0, rpm.MaxIndex);
        Assert.Equal(3, rpm.MinIndex);
    }

    [Fact]
    public void ExtremeIndexesAreUnsetForAnEmptyChannel()
    {
        var empty = new LogChannel("Nothing", "", 0, []);

        Assert.Equal(-1, empty.MinIndex);
        Assert.Equal(-1, empty.MaxIndex);
    }

    [Fact]
    public void DurationAndIndexLookupHandleALogThatStartsLate()
    {
        // Real dyno logs start the clock partway through a session.
        LogDocument log = Build(6310.743, 6310.81, 6310.877, 6310.944);

        Assert.Equal(0.201, log.Duration, 3);
        Assert.Equal(0, log.IndexAtTime(0));
        Assert.Equal(3, log.IndexAtTime(99999));
        Assert.Equal(1, log.IndexAtTime(6310.85));
    }
}
