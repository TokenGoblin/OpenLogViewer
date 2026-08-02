using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class LiveSessionTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    private const string Ini = """
        [OutputChannels]
        ochBlockSize = 8
        seconds  = scalar, U16, 0, "s",   1.000, 0.0
        rpm      = scalar, U16, 2, "RPM", 1.000, 0.0
        map      = scalar, S16, 4, "kPa", 0.100, 0.0
        internal = scalar, U16, 6, "x",   1.000, 0.0

        [Datalog]
        entry = rpm,     "RPM", int,   "%d"
        entry = map,     "MAP", float, "%.1f"
        """;

    private static RealtimeDecoder Decoder() =>
        new(MsqIni.ReadOutputChannels(Ini));

    /// <summary>A framed reply carrying one realtime block.</summary>
    private static byte[] Block(int rpm, int mapTenths)
    {
        byte[] data =
        [
            0, 0,
            (byte)(rpm >> 8), (byte)(rpm & 0xFF),
            (byte)(mapTenths >> 8), (byte)(mapTenths & 0xFF),
            0, 0,
        ];

        byte[] body = [0x00, .. data];
        var framed = new List<byte> { (byte)(body.Length >> 8), (byte)(body.Length & 0xFF) };
        framed.AddRange(body);

        uint crc = MsProtocol.Crc32(body);
        framed.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);

        return [.. framed];
    }

    private string TempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-live-{Guid.NewGuid():N}.csv");
        _temp.Add(path);
        return path;
    }

    private static LiveSession Session(
        IEcuTransport transport, LiveSessionSettings? settings = null)
    {
        var connection = new EcuConnection(transport, new EcuConnectionSettings
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            RetryPause = TimeSpan.Zero,
        });

        return new LiveSession(connection, Decoder(), MsqIni.ReadDatalog(Ini), settings);
    }

    /// <summary>Runs until the session has at least this many samples, or gives up.</summary>
    private static void Until(LiveSession session, int samples)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Status.Samples < samples && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }

    [Fact]
    public void OnlyTheDatalogChannelsAreRecordedAndUnderTheirLogNames()
    {
        // The block has four fields; the datalog names two of them. Recording
        // everything would give internal names no preset would ever match.
        using LiveSession session = Session(new FakeTransport());

        Assert.Equal(["RPM", "MAP"], session.Names);
    }

    [Fact]
    public void PollingFillsTheSessionAndItSnapshotsAsAnOrdinaryLog()
    {
        var transport = new FakeTransport { Repeating = Block(3000, 1000) };
        using LiveSession session = Session(transport);

        session.Start();
        Until(session, 5);
        session.Stop();

        LogDocument document = session.Snapshot();

        Assert.True(document.SampleCount >= 5);
        Assert.Equal(["RPM", "MAP"], document.Channels.Select(c => c.Name));
        Assert.Equal("Live", document.FormatName);
        Assert.Equal(3000, document.FindChannel("RPM")!.At(0), 3);
        Assert.Equal(100.0, document.FindChannel("MAP")!.At(0), 3);
    }

    [Fact]
    public void TheTimeBaseAdvancesAndIsUsable()
    {
        var transport = new FakeTransport { Repeating = Block(1000, 500) };
        using LiveSession session = Session(transport);

        session.Start();
        Until(session, 6);
        session.Stop();

        LogDocument document = session.Snapshot();

        Assert.True(document.Duration > 0);
        Assert.True(document.Time.At(1) > document.Time.At(0));
        Assert.True(document.MedianSampleInterval > 0);
    }

    [Fact]
    public void ASnapshotIsReusedUntilMoreSamplesArrive()
    {
        // The plot asks on every repaint; rebuilding a few hundred columns for a
        // document that has not changed would cost more than drawing it.
        var transport = new FakeTransport { Repeating = Block(1000, 500) };
        using LiveSession session = Session(transport);

        session.Start();
        Until(session, 3);
        session.Stop();

        Assert.Same(session.Snapshot(), session.Snapshot());
    }

    [Fact]
    public void TheRecordingReopensAsALog()
    {
        // A session that cannot be reopened is not much of a record.
        string path = TempFile();
        var transport = new FakeTransport { Repeating = Block(2500, 800) };

        using (LiveSession session = Session(transport, new LiveSessionSettings { RecordingPath = path }))
        {
            session.Start();
            Until(session, 5);
            session.Stop();
        }

        LogDocument reopened = LogReaderFactory.Load(path);

        Assert.True(reopened.SampleCount >= 5);
        Assert.Equal(["Time", "RPM", "MAP"], reopened.Channels.Select(c => c.Name));
        Assert.Equal("RPM", reopened.FindChannel("RPM")!.Units);
        Assert.Equal("kPa", reopened.FindChannel("MAP")!.Units);
        Assert.Equal(2500, reopened.FindChannel("RPM")!.At(0), 3);
        Assert.Equal(80.0, reopened.FindChannel("MAP")!.At(0), 3);
    }

    [Fact]
    public void TheRecordingIsWrittenAsItGoesRatherThanAtTheEnd()
    {
        // A pulled cable or a sleeping laptop should cost the row in hand, not
        // the session.
        string path = TempFile();
        var transport = new FakeTransport { Repeating = Block(1200, 400) };

        using LiveSession session = Session(transport, new LiveSessionSettings { RecordingPath = path });
        session.Start();
        Until(session, 5);

        // Read while it is still running, without stopping it.
        long size = new FileInfo(path).Length;
        Assert.True(size > 0, "nothing had reached disk while the session was running");

        session.Stop();
    }

    [Fact]
    public void ARunOfFailuresEndsTheSessionAndSaysWhy()
    {
        var transport = new FakeTransport();   // never replies
        using LiveSession session = Session(transport, new LiveSessionSettings
        {
            FailuresBeforeStopping = 3,
        });

        session.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(20);

        Assert.False(session.IsRunning);
        Assert.True(session.Status.Faulted);
        Assert.Contains("Lost the ECU", session.Status.Error);
    }

    [Fact]
    public void OneBadBlockDoesNotEndTheSession()
    {
        // Ordinary on a radio link. The connection retries beneath this, so the
        // session should not even see it.
        byte[] corrupt = Block(3000, 1000);
        corrupt[^1] ^= 0xFF;

        var transport = new FakeTransport { Repeating = Block(3000, 1000) };
        transport.Enqueue(corrupt);

        using LiveSession session = Session(transport);
        session.Start();
        Until(session, 4);
        session.Stop();

        Assert.False(session.Status.Faulted);
        Assert.True(session.Status.Samples >= 4);
    }

    [Fact]
    public void TheViewIsTrimmedButTheRecordingIsNot()
    {
        // A long session must not grow without bound in memory, and must still
        // leave the whole run on disk.
        string path = TempFile();
        var transport = new FakeTransport { Repeating = Block(1000, 500) };

        using LiveSession session = Session(transport, new LiveSessionSettings
        {
            RecordingPath = path,
            MaximumSamples = 4,
        });

        session.Start();
        Until(session, 12);
        session.Stop();

        Assert.Equal(4, session.Snapshot().SampleCount);
        Assert.True(LogReaderFactory.Load(path).SampleCount >= 10);
    }

    [Fact]
    public void TheEcuGoingAwayEndsTheSessionRatherThanTheProcess()
    {
        // Switching an ECU off takes its USB adapter with it, and a serial port
        // whose device has gone throws whatever it likes. The poll loop runs on
        // a background thread, where anything escaping terminates the process —
        // so this must be caught however unlikely the type looks.
        var transport = new ThrowingTransport(new ObjectDisposedException("SerialPort"));

        using LiveSession session = Session(transport, new LiveSessionSettings
        {
            FailuresBeforeStopping = 50,
        });

        session.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(20);

        Assert.False(session.IsRunning);
        Assert.True(session.Status.Faulted);

        // Stopped at once rather than after fifty tries: a handle that no longer
        // exists will not start existing again.
        Assert.Contains("stopped responding", session.Status.Error);
    }

    [Theory]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(NotSupportedException))]
    public void NoExceptionFromTheTransportEscapesThePollThread(Type kind)
    {
        var transport = new ThrowingTransport((Exception)Activator.CreateInstance(kind)!);

        using LiveSession session = Session(transport, new LiveSessionSettings
        {
            FailuresBeforeStopping = 3,
        });

        session.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(20);

        Assert.False(session.IsRunning);
        Assert.True(session.Status.Faulted);
    }

    [Fact]
    public void StoppingWorksAfterTheLinkHasAlreadyFailed()
    {
        // The usual way Stop is reached. Closing a port whose device has gone
        // throws, and a disconnect that throws leaves the app wedged.
        var transport = new ThrowingTransport(new ObjectDisposedException("SerialPort"))
        {
            ThrowOnClose = true,
        };

        LiveSession session = Session(transport, new LiveSessionSettings { FailuresBeforeStopping = 2 });
        session.Start();
        Thread.Sleep(200);

        session.Stop();
        session.Dispose();
    }

    [Fact]
    public void ASessionWithNoRecordingPathKeepsNoFile()
    {
        var transport = new FakeTransport { Repeating = Block(1000, 500) };
        using LiveSession session = Session(transport);

        session.Start();
        Until(session, 3);
        session.Stop();

        Assert.Null(session.RecordingPath);
        Assert.True(session.Snapshot().SampleCount >= 3);
    }
}
