using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// A transport that replies from a script, so the retry and framing behaviour
/// can be exercised without an ECU — including the failures a real link produces
/// and a cable on a desk almost never does.
/// </summary>
internal sealed class FakeTransport : IEcuTransport
{
    private readonly Queue<byte[]> _replies = new();
    private byte[] _pending = [];

    /// <summary>Answered indefinitely once the queue runs dry, for a polling test.</summary>
    public byte[]? Repeating { get; set; }

    public List<byte[]> Written { get; } = [];

    public bool IsOpen { get; private set; }

    public int Discards { get; private set; }

    public void Enqueue(params byte[][] replies)
    {
        foreach (byte[] reply in replies) _replies.Enqueue(reply);
    }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Write(ReadOnlySpan<byte> data)
    {
        Written.Add(data.ToArray());
        _pending = _replies.Count > 0 ? _replies.Dequeue() : Repeating ?? [];
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int take = Math.Min(buffer.Length, _pending.Length);
        _pending.AsSpan(0, take).CopyTo(buffer);
        _pending = _pending[take..];

        return take;
    }

    public void DiscardInput() => Discards++;

    public void Dispose() => Close();
}

/// <summary>
/// A transport whose device has gone: every operation throws, the way a serial
/// port does once its USB adapter has been unplugged.
/// </summary>
internal sealed class ThrowingTransport(Exception thrown) : IEcuTransport
{
    public bool IsOpen => true;

    public bool ThrowOnClose { get; init; }

    public void Open() { }

    public void Close()
    {
        if (ThrowOnClose) throw thrown;
    }

    public void Write(ReadOnlySpan<byte> data) => throw thrown;

    public int Read(Span<byte> buffer, TimeSpan timeout) => throw thrown;

    public void DiscardInput() => throw thrown;

    public void Dispose() => Close();
}

public class EcuProtocolTests
{
    private static byte[] Reply(params byte[] data)
    {
        // <len:2><status 0><data><crc32:4>, the shape an ECU sends back.
        byte[] body = [0x00, .. data];

        var framed = new List<byte> { (byte)(body.Length >> 8), (byte)(body.Length & 0xFF) };
        framed.AddRange(body);

        uint crc = MsProtocol.Crc32(body);
        framed.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);

        return [.. framed];
    }

    // ----- framing ----------------------------------------------------------

    [Fact]
    public void ARequestIsLengthPayloadAndChecksum()
    {
        byte[] framed = MsProtocol.Frame([(byte)'Q']);

        Assert.Equal(7, framed.Length);
        Assert.Equal(0x00, framed[0]);
        Assert.Equal(0x01, framed[1]);
        Assert.Equal((byte)'Q', framed[2]);
    }

    [Fact]
    public void AReplyUnframesToItsData() =>
        Assert.Equal("MS3"u8.ToArray(), MsProtocol.Unframe(Reply("MS3"u8.ToArray())));

    [Fact]
    public void AReplyWithABadChecksumIsRefused()
    {
        // The whole point of the framing. A truncated reply decodes into
        // perfectly plausible readings, and only the checksum says otherwise.
        byte[] reply = Reply("MS3"u8.ToArray());
        reply[^1] ^= 0xFF;

        EcuProtocolException thrown = Assert.Throws<EcuProtocolException>(() => MsProtocol.Unframe(reply));
        Assert.Contains("checksum", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AReplyShorterThanItsDeclaredLengthIsRefused()
    {
        byte[] reply = Reply(new byte[20]);

        Assert.Throws<EcuProtocolException>(() => MsProtocol.Unframe(reply.AsSpan(0, 12).ToArray()));
    }

    [Fact]
    public void ARefusalFromTheEcuIsReported()
    {
        byte[] body = [0x83, 0x01];
        var framed = new List<byte> { 0x00, (byte)body.Length };
        framed.AddRange(body);

        uint crc = MsProtocol.Crc32(body);
        framed.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);

        EcuProtocolException thrown = Assert.Throws<EcuProtocolException>(() => MsProtocol.Unframe([.. framed]));
        Assert.Contains("refused", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRealtimeRequestOnlyEverAsksForTheRealtimePage()
    {
        // Table 7. Nothing here can be pointed at a tune page.
        byte[] request = MsProtocol.RealtimeRequest(512);

        Assert.Equal((byte)'r', request[0]);
        Assert.Equal(7, request[2]);
        Assert.Equal(0, request[3]);
        Assert.Equal(0, request[4]);
        Assert.Equal(0x02, request[5]);
        Assert.Equal(0x00, request[6]);
    }

    [Fact]
    public void ASignatureLosesItsPadding() =>
        Assert.Equal("MS3 Format 0569.00", MsProtocol.ReadSignature("MS3 Format 0569.00 \0"u8));

    [Fact]
    public void TheChecksumMatchesTheKnownValue() =>
        Assert.Equal(0xCBF43926u, MsProtocol.Crc32("123456789"u8));

    // ----- connection -------------------------------------------------------

    [Fact]
    public void ASignatureIsReadBack()
    {
        var transport = new FakeTransport();
        transport.Enqueue(Reply(Encoding.ASCII.GetBytes("MS3 Format 0569.00 \0")));

        using var connection = new EcuConnection(transport);

        Assert.Equal("MS3 Format 0569.00", connection.ReadSignature());
        Assert.Equal(0, connection.Retries);
    }

    [Fact]
    public void ACorruptReplyIsAskedForAgain()
    {
        // What a Bluetooth link does routinely. One bad reply should cost a
        // retry, not the session.
        byte[] bad = Reply("MS3"u8.ToArray());
        bad[^1] ^= 0xFF;

        var transport = new FakeTransport();
        transport.Enqueue(bad, Reply("MS3"u8.ToArray()));

        using var connection = new EcuConnection(transport);

        Assert.Equal("MS3", connection.ReadSignature());
        Assert.Equal(1, connection.Retries);
    }

    [Fact]
    public void SilenceIsAlsoRetriedAndEventuallyGivenUpOn()
    {
        var transport = new FakeTransport();   // never replies
        using var connection = new EcuConnection(transport, new EcuConnectionSettings
        {
            Retries = 2,
            Timeout = TimeSpan.FromMilliseconds(1),
            RetryPause = TimeSpan.Zero,
        });

        Assert.Throws<EcuProtocolException>(() => connection.ReadSignature());
        Assert.Equal(3, transport.Written.Count);   // the attempt plus two retries
    }

    [Fact]
    public void TheInputIsClearedBeforeEveryAttempt()
    {
        // The tail of an abandoned reply would otherwise be read as the head of
        // the next one, which fails in a way that looks like corruption.
        var transport = new FakeTransport();
        transport.Enqueue(Reply("MS3"u8.ToArray()));

        using var connection = new EcuConnection(transport);
        connection.ReadSignature();

        Assert.Equal(1, transport.Discards);
    }

    [Fact]
    public void ARealtimeBlockComesBackWhole()
    {
        var block = new byte[512];
        block[6] = 0x0B;
        block[7] = 0xB8;

        var transport = new FakeTransport();
        transport.Enqueue(Reply(block));

        using var connection = new EcuConnection(transport);
        byte[] read = connection.ReadRealtime(512);

        Assert.Equal(512, read.Length);
        Assert.Equal(0xB8, read[7]);
    }

    // ----- INI matching -----------------------------------------------------

    [Fact]
    public void AnIniIsMatchedOnTheSignatureTheEcuReports()
    {
        // Firmware versions move channels inside the block, so the wrong INI
        // decodes every channel from the wrong offset and still looks sane.
        IniFile[] catalogue =
        [
            new("a.ini", "MS3 Format 0592.13P"),
            new("b.ini", "MS3 Format 0569.00"),
            new("c.ini", "MS2Extra comms340vU"),
        ];

        Assert.Equal("b.ini", IniCatalog.Match("MS3 Format 0569.00", catalogue)!.Path);
    }

    [Fact]
    public void PaddingAndSpacingDoNotStopAMatch()
    {
        IniFile[] catalogue = [new("a.ini", "MS3 Format 0569.00 ")];

        Assert.NotNull(IniCatalog.Match("MS3 Format 0569.00", catalogue));
        Assert.NotNull(IniCatalog.Match("MS3  Format 0569.00", catalogue));
    }

    [Fact]
    public void AnUnknownSignatureMatchesNothing()
    {
        // Better no session than one decoded against the wrong firmware.
        IniFile[] catalogue = [new("a.ini", "MS3 Format 0592.13P")];

        Assert.Null(IniCatalog.Match("MS3 Format 0569.00", catalogue));
        Assert.Null(IniCatalog.Match("", catalogue));
    }

    [Fact]
    public void ANearbyVersionIsNotTreatedAsAMatch()
    {
        // 0592.12P and 0592.13P differ by one character and by their layout.
        IniFile[] catalogue = [new("a.ini", "MS3 Format 0592.12P")];

        Assert.Null(IniCatalog.Match("MS3 Format 0592.13P", catalogue));
    }
}
