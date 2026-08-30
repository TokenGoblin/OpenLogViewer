using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class MlgReaderTests : IDisposable
{
    private readonly List<string> _temp = [];

    private string File_(MlgBuilder b, int n, Func<int, int, double> raw,
        (int, string)[]? markers = null, int? declared = null)
    {
        string path = b.BuildFile(n, raw, markers, declared);
        _temp.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string p in _temp) File.Delete(p);
    }

    private static MlgBuilder Basic() => new MlgBuilder()
        .Add(MlgDataType.F32, "Time", "s", digits: 3)
        .Add(MlgDataType.U16, "RPM", "RPM")
        .Add(MlgDataType.S16, "MAP", "kPa", scale: 0.1f, digits: 1);

    [Fact]
    public void ReadsChannelMetadataAndSamples()
    {
        string path = File_(Basic(), 10, (f, s) => f switch
        {
            0 => s * 0.05,
            1 => 1000 + s * 100,
            _ => 820,
        });

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("MLG v2", log.FormatName);
        Assert.Equal(3, log.Channels.Count);
        Assert.Equal(10, log.SampleCount);
        Assert.Equal(["Time", "RPM", "MAP"], log.Channels.Select(c => c.Name));
        Assert.Equal("kPa", log.FindChannel("MAP")!.Units);
        Assert.Equal("TEST ECU signature", log.Signature);
    }

    [Fact]
    public void AppliesScaleAndTransform()
    {
        // MAP is s16 scaled by 0.1, so a raw 820 must decode to 82.0 kPa.
        string path = File_(Basic(), 4, (f, s) => f == 2 ? 820 : 0);

        LogChannel map = LogReaderFactory.Load(path).FindChannel("MAP")!;

        Assert.Equal(82.0, map.At(0), 3);
        Assert.Equal("82.0", map.Format(map.At(0)));
    }

    [Fact]
    public void ATransformIsAddedBeforeTheScaleAndNotAfter()
    {
        // The declaration is MS2's VE trim, copied out of one of the user's own
        // logs: scale 0.009765625, transform 10240, in per cent. A trim that is
        // doing nothing sits at a raw zero and must read 100%.
        //
        // Adding after the scale instead reads 10,240% — which is what this log
        // really did show before, and what made the ordering worth settling.
        // See TuneConstant.Transform for the four firmwares that settle it.
        var b = new MlgBuilder()
            .Add(MlgDataType.F32, "Time", "s")
            .Add(MlgDataType.S16, "VE Trim 1", "%", scale: 0.009765625f, transform: 10240f);

        string path = File_(b, 3, (f, s) => f == 1 ? 0 : s);

        LogChannel trim = LogReaderFactory.Load(path).FindChannel("VE Trim 1")!;

        Assert.Equal(100.0, trim.At(0), 3);
    }

    [Fact]
    public void FlagByteKeepsFollowingChannelsAligned()
    {
        // A type-16 descriptor is unnamed but still consumes one payload byte.
        // Ignoring it would shift every channel after it by one byte.
        var b = new MlgBuilder()
            .Add(MlgDataType.F32, "Time", "s")
            .Add(MlgDataType.U16, "RPM", "RPM")
            .Add(MlgDataType.Bits, "", "bits")
            .Add(MlgDataType.S16, "MAP", "kPa", scale: 0.1f, digits: 1);

        string path = File_(b, 5, (f, s) => f switch
        {
            0 => s,
            1 => 3000,
            2 => 0b1010_0101,
            _ => 950,
        });

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal(3000, log.FindChannel("RPM")!.At(0));
        Assert.Equal(95.0, log.FindChannel("MAP")!.At(0), 3);
        Assert.Equal(0b1010_0101, log.FindChannel("Flags 1")!.At(0));
    }

    [Fact]
    public void WalksInterleavedMarkerRecords()
    {
        // Markers are a different length to samples, so a fixed stride would
        // desynchronise everything after the first one.
        string path = File_(Basic(), 20, (f, s) => f switch
        {
            0 => s * 0.1,
            1 => 2000 + s,
            _ => 800,
        }, markers: [(5, "MARK 000 - test marker"), (12, "MARK 001 - second")]);

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal(20, log.SampleCount);
        Assert.Equal(2, log.Markers.Count);
        Assert.Equal("MARK 000 - test marker", log.Markers[0].Text);
        Assert.Equal("MARK 001 - second", log.Markers[1].Text);

        // Samples after a marker must still decode correctly.
        Assert.Equal(2019, log.FindChannel("RPM")!.At(19));
        Assert.Equal(1.9, log.Time.At(19), 3);
    }

    [Fact]
    public void ThrowsWhenDeclaredPayloadDisagreesWithChannelTable()
    {
        string path = File_(Basic(), 5, (f, s) => 0, declared: 999);

        var ex = Assert.Throws<LogFormatException>(() => LogReaderFactory.Load(path));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void RejectsNonMlgContent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}.mlg");
        File.WriteAllBytes(path, [0, 1, 2, 3, 4, 5, 6, 7]);
        _temp.Add(path);

        Assert.Throws<LogFormatException>(() => LogReaderFactory.Load(path));
    }

    [Fact]
    public void TimeChannelIsUsedAsTheTimeBase()
    {
        string path = File_(Basic(), 6, (f, s) => f == 0 ? s * 0.25 : 0);

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("Time", log.Time.Name);
        Assert.Equal(1.25, log.Duration, 3);
    }

    [Fact]
    public void FallsBackToSampleIndexWhenTimeIsFlat()
    {
        string path = File_(Basic(), 6, (f, s) => 0);

        LogDocument log = LogReaderFactory.Load(path);

        Assert.Equal("Sample", log.Time.Name);
        Assert.Equal(5, log.Time.At(5));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.24, 0)]
    [InlineData(0.25, 1)]
    [InlineData(1.1, 4)]
    [InlineData(99, 5)]
    public void IndexAtTimeFindsTheSampleAtOrBeforeATimestamp(double seconds, int expected)
    {
        string path = File_(Basic(), 6, (f, s) => f == 0 ? s * 0.25 : 0);

        Assert.Equal(expected, LogReaderFactory.Load(path).IndexAtTime(seconds));
    }
}
