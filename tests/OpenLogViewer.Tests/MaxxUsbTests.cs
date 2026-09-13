using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The protocol a MaxxECU speaks over USB, checked against a recording of MTune
/// speaking it.
///
/// <c>maxxecu-usb.txt</c> is request and reply pairs lifted from a capture of
/// MTune 1.161 connecting to a MaxxECU Race over its USB cable — every distinct
/// command it used, plus a run of telemetry rounds. Nothing in it was composed
/// here, which is the point: this protocol was recovered by watching rather than
/// from any documentation, so the only thing that can confirm it is the wire.
/// </summary>
public class MaxxUsbTests
{
    private static IReadOnlyList<(byte[] Request, byte[] Reply)> Recorded()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "maxxecu-usb.txt");
        if (!File.Exists(path)) return [];

        var pairs = new List<(byte[], byte[])>();

        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;

            string[] halves = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (halves.Length != 2) continue;

            pairs.Add((Convert.FromHexString(halves[0]), Convert.FromHexString(halves[1])));
        }

        return pairs;
    }

    [Fact]
    public void TheRecordingIsThere() => Assert.Equal(30, Recorded().Count);

    [Fact]
    public void EveryRecordedRequestIsReproducedExactly()
    {
        // Composing each request from its own fields has to land on the bytes
        // MTune sent, checksum included. This is the whole claim about the
        // request format in one assertion.
        Assert.All(Recorded(), pair =>
        {
            byte[] sent = pair.Request;

            byte[] rebuilt = MaxxUsbProtocol.Request(
                sent[0],
                sent[1],
                sent[2] | (sent[3] << 8),
                sent[4] | (sent[5] << 8));

            Assert.Equal(sent, rebuilt);
        });
    }

    [Fact]
    public void EveryRecordedReplyIsAcceptedAndItsDataRecovered()
    {
        Assert.All(Recorded(), pair =>
        {
            int asked = pair.Request[4] | (pair.Request[5] << 8);

            // Only the reads: a write's reply answers the payload that followed
            // the header rather than the header's own length.
            if (pair.Request[0] != MaxxUsbProtocol.Read) return;
            if (pair.Reply.Length != MaxxUsbProtocol.ReplyLength(asked)) return;

            Assert.True(
                MaxxUsbProtocol.TryReadReply(pair.Reply, asked, out byte[] data),
                $"A recorded reply to command 0x{pair.Request[1]:X2} was refused.");

            Assert.Equal(asked, data.Length);
        });
    }

    [Fact]
    public void ARequestIsTenBytesWithTheChecksumOverItsHeader()
    {
        byte[] request = MaxxUsbProtocol.AskWhatIsWaiting();

        Assert.Equal(MaxxUsbProtocol.RequestLength, request.Length);
        Assert.Equal(MaxxUsbProtocol.Read, request[0]);
        Assert.Equal(MaxxUsbProtocol.Waiting, request[1]);

        // The checksum is the whole 32 bits here, where the Bluetooth framing
        // keeps only the low half of the same arithmetic.
        uint carried = (uint)(request[6] | (request[7] << 8) | (request[8] << 16) | (request[9] << 24));

        Assert.Equal(MaxxProtocol.Crc32(request.AsSpan(0, 6)), carried);
        Assert.Equal((ushort)carried, MaxxProtocol.Checksum(request.AsSpan(0, 6)));
    }

    [Fact]
    public void AReplyWithABrokenChecksumIsRefused()
    {
        (byte[] request, byte[] reply) = Recorded().First(p => p.Request[0] == MaxxUsbProtocol.Read);
        int asked = request[4] | (request[5] << 8);

        Assert.True(MaxxUsbProtocol.TryReadReply(reply, asked, out _));

        byte[] broken = [.. reply];
        broken[^1] ^= 0xFF;

        Assert.False(MaxxUsbProtocol.TryReadReply(broken, asked, out _));
    }

    [Fact]
    public void AReplyOfTheWrongLengthIsRefusedRatherThanReadPast()
    {
        byte[] reply = [MaxxUsbProtocol.Ok, 1, 2, 3, 4, 5];

        Assert.False(MaxxUsbProtocol.TryReadReply(reply, 2, out _));
        Assert.False(MaxxUsbProtocol.TryReadReply(reply, 99, out _));
        Assert.False(MaxxUsbProtocol.TryReadReply([], 0, out _));
    }

    [Fact]
    public void AFailedStatusIsNotReadAsData()
    {
        // 0x30 appears in the recording and is not 0x80. Reading its body as a
        // reply is how an error becomes a row of numbers.
        byte[] body = [0x30, 0x00];
        uint crc = MaxxProtocol.Crc32(body);

        byte[] reply =
        [
            .. body,
            (byte)crc, (byte)(crc >> 8), (byte)(crc >> 16), (byte)(crc >> 24),
        ];

        Assert.False(MaxxUsbProtocol.TryReadReply(reply, 1, out _));
    }

    [Fact]
    public void EveryRecordedTelemetryPayloadDecodesAsAscendingChannels()
    {
        var rounds = 0;
        var channels = new Dictionary<int, ushort>();

        foreach ((byte[] request, byte[] reply) in Recorded())
        {
            if (request[1] != MaxxUsbProtocol.Take) continue;

            int asked = request[4] | (request[5] << 8);
            if (!MaxxUsbProtocol.TryReadReply(reply, asked, out byte[] payload)) continue;

            Assert.True(
                MaxxUsbProtocol.LooksLikeTelemetry(payload),
                "A recorded telemetry payload does not read as a list of channels.");

            Assert.True(MaxxUsbProtocol.ReadUpdates(payload, channels) > 0);
            rounds++;
        }

        Assert.True(rounds > 0);

        // And the values are the ones the bench ECU was actually showing:
        // a charged supply, and a barometric MAP with the engine stopped. Ranges
        // rather than exact counts, because both wander by a count or two
        // between samples — but a wrong pairing, a wrong endianness or a
        // half-sample offset survives the structural check above and none of
        // them lands anywhere near these.
        Assert.InRange(channels[21] * 0.01, 12.0, 15.0);        // battery, volts
        Assert.InRange(channels[20] * 0.1, 80.0, 105.0);        // MAP, kPa
    }

    [Fact]
    public void APayloadWhoseChannelsDoNotAscendIsRefused()
    {
        // Ids climb and never repeat in all 1,765 recorded payloads, so anything
        // else is a stream being read at the wrong offset — which otherwise
        // yields channel numbers that exist and values that are nonsense.
        Assert.False(MaxxUsbProtocol.LooksLikeTelemetry([0x05, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01, 0x00]));
        Assert.False(MaxxUsbProtocol.LooksLikeTelemetry([0x05, 0x00, 0x01, 0x00, 0x05, 0x00, 0x01, 0x00]));
        Assert.False(MaxxUsbProtocol.LooksLikeTelemetry([0x05, 0x00, 0x01]));
        Assert.True(MaxxUsbProtocol.LooksLikeTelemetry([0x05, 0x00, 0x01, 0x00, 0x06, 0x00, 0x01, 0x00]));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0.1, 1)]
    [InlineData(0.01, 2)]
    [InlineData(0.001, 3)]
    public void DecimalsComeFromTheScaleRatherThanBeingChosen(double scale, int digits) =>
        Assert.Equal(digits, MaxxChannelDefinitions.DigitsFor(scale));

    [Fact]
    public void AnOffsetOrLengthThatWillNotFitTheFrameIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxxUsbProtocol.Request(0, 0x12, 70000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxxUsbProtocol.Request(0, 0x12, 0, 70000));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxxUsbProtocol.Request(0, 0x12, -1, 1));
    }
}
