using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Connecting to a Pro with and without the custom firmware, and recording its
/// CAN stream into the same files an slcan capture makes.
/// </summary>
public class WcpRecorderTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string p in _temp)
        {
            try
            {
                File.Delete(p);
            }
            catch (IOException)
            {
            }
        }
    }

    private string Temp(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-wcp-{Guid.NewGuid():N}{extension}");
        _temp.Add(path);
        return path;
    }

    [Fact]
    public void ACustomUnitGivesBothHalves()
    {
        var device = new FakeWcpDevice(WcpClientTests.Car());
        WicanProConnection pro = WicanPro.Connect(device);

        try
        {
            Assert.NotNull(pro.Wcp);
            Assert.Contains("RPM", pro.Source.Names);

            // The hello went before any ELM327 line.
            Assert.Equal("hello", device.Commands[0]);
            Assert.Equal("12.6", pro.Wcp.Status()["volt"]);
        }
        finally
        {
            pro.Wcp?.Dispose();
            pro.Source.Dispose();
        }
    }

    [Fact]
    public void AStockUnitIsPlainElm327()
    {
        var device = new FakeWcpDevice(WcpClientTests.Car()) { Stock = true };
        WicanProConnection pro = WicanPro.Connect(device, helloTimeout: TimeSpan.FromMilliseconds(300));

        try
        {
            Assert.Null(pro.Wcp);
            Assert.Contains("RPM", pro.Source.Names);
            Assert.NotEmpty(pro.Source.Read());
        }
        finally
        {
            pro.Source.Dispose();
        }
    }

    [Fact]
    public void AStreamIsRecordedAndBecomesAByteLog()
    {
        var device = new FakeWcpDevice();
        using var link = new WcpLink(device);
        link.Open();
        using var client = new WcpClient(link);

        string csv = Temp(".csv");
        using var recorder = new WcpCanRecorder(client, csv);

        client.CanStart();
        client.CanStreamOn();
        recorder.Start();

        for (int i = 0; i < 20; i++) device.BusFrame(0x2C4, false, (byte)i, 0x10, (byte)(i * 3));

        DateTime until = DateTime.UtcNow.AddSeconds(2);
        while (recorder.FrameCount < 20 && DateTime.UtcNow < until) Thread.Sleep(10);

        recorder.Stop();

        Assert.Equal(20, recorder.FrameCount);
        Assert.Equal("asked", recorder.StoppedBecause);

        string[] lines = File.ReadAllLines(csv);
        Assert.Equal("Time,Id,Extended,Length,Data", lines[0]);
        Assert.EndsWith(",0x2C4,0,3,131039", lines[^1], StringComparison.Ordinal);

        CanIdSummary id = recorder.Ids.Single();
        Assert.True(id.Changes[0]);
        Assert.False(id.Changes[1]);

        string log = Temp(".csv");
        recorder.WriteByteLog(log);
        Assert.Contains("2C4 b0", File.ReadLines(log).First(), StringComparison.Ordinal);
    }

    [Fact]
    public void ItStopsItselfAtTheFrameLimit()
    {
        var device = new FakeWcpDevice();
        using var link = new WcpLink(device);
        link.Open();
        using var client = new WcpClient(link);
        using var recorder = new WcpCanRecorder(client, Temp(".csv"), maxFrames: 5);

        client.CanStart();
        client.CanStreamOn();
        recorder.Start();

        for (int i = 0; i < 50; i++) device.BusFrame(0x100, false, (byte)i);

        DateTime until = DateTime.UtcNow.AddSeconds(2);
        while (recorder.IsRunning && DateTime.UtcNow < until) Thread.Sleep(10);

        Assert.False(recorder.IsRunning);
        Assert.Equal("frames", recorder.StoppedBecause);
        Assert.Equal(5, recorder.FrameCount);
    }

    [Fact]
    public void ItStopsItselfAtTheTimeLimitOnAQuietBus()
    {
        var device = new FakeWcpDevice();
        using var link = new WcpLink(device);
        link.Open();
        using var client = new WcpClient(link);
        using var recorder = new WcpCanRecorder(client, Temp(".csv"), maxSeconds: 0.05);

        recorder.Start();
        Thread.Sleep(100);

        Assert.False(recorder.Poll());
        Assert.Equal("time", recorder.StoppedBecause);
    }
}
