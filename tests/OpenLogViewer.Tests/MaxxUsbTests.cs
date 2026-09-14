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

    // ----- what the ECU does not mention ---------------------------------------

    /// <summary>
    /// An ECU that answers, and sends only the handful of channels that happen to
    /// be moving — which is what a real one does with the engine off.
    /// </summary>
    private sealed class QuietEcu(params int[] sends) : IEcuTransport
    {
        private byte[] _reply = [];
        private bool _sent;

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data)
        {
            byte command = data[1];
            int length = data[4] | (data[5] << 8);

            _reply = command switch
            {
                MaxxUsbProtocol.Hello => Answer([0x00]),
                MaxxUsbProtocol.Waiting => Answer(Waiting()),
                MaxxUsbProtocol.Take => Answer(Telemetry(length)),
                _ => Answer(new byte[length]),
            };
        }

        /// <summary>The whole state once, then nothing, because nothing changes.</summary>
        private byte[] Waiting()
        {
            int bytes = _sent ? 0 : sends.Length * 4;

            return [(byte)(bytes & 0xFF), (byte)(bytes >> 8)];
        }

        private byte[] Telemetry(int length)
        {
            _sent = true;

            var payload = new byte[length];

            for (int i = 0; i < sends.Length && (i * 4) + 4 <= length; i++)
            {
                payload[i * 4] = (byte)(sends[i] & 0xFF);
                payload[(i * 4) + 1] = (byte)(sends[i] >> 8);
                payload[(i * 4) + 2] = 1;
            }

            return payload;
        }

        private static byte[] Answer(byte[] data)
        {
            var reply = new byte[data.Length + MaxxUsbProtocol.ReplyOverhead];

            reply[0] = MaxxUsbProtocol.Ok;
            data.CopyTo(reply, 1);
            BitConverter.GetBytes(MaxxProtocol.Crc32(reply.AsSpan(0, 1 + data.Length)))
                .CopyTo(reply, 1 + data.Length);

            return reply;
        }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            int taken = Math.Min(buffer.Length, _reply.Length);
            _reply.AsSpan(0, taken).CopyTo(buffer);
            _reply = _reply[taken..];

            return taken;
        }

        public void DiscardInput() => _reply = [];

        public void Dispose() => Close();
    }

    /// <summary>
    /// The channels anybody connects to see are columns even when the ECU has not
    /// mentioned them.
    ///
    /// They were not, and the cost was the whole session: a MaxxECU sends a
    /// channel only when its value changes, so connecting with the engine off —
    /// cable first, key second — learnt input voltages and counters and left out
    /// engine speed, coolant, lambda and ignition angle. A log's columns cannot
    /// change once it has rows, so they stayed out for the rest of the drive.
    /// </summary>
    [Fact]
    public void TheChannelsWorthLoggingAreColumnsEvenWhenNothingHasMoved()
    {
        // Three channels, as a bench ECU with nothing running offers: a raw
        // input voltage, manifold pressure and the battery.
        using var source = new MaxxUsbSource(new QuietEcu(0, 20, 21));
        source.Open();

        int[] found = [.. source.Channels.Select(c => c.Id)];

        Assert.Contains(61, found);   // RPM
        Assert.Contains(18, found);   // coolant
        Assert.Contains(5, found);    // lambda
        Assert.Contains(60, found);   // ignition angle
        Assert.Contains(19, found);   // throttle position

        Assert.All(MaxxUsbSource.AlwaysLogged, id => Assert.Contains(id, found));

        // And what the ECU did send is still there.
        Assert.Contains(0, found);
        Assert.Contains(20, found);
    }

    /// <summary>
    /// The core set is the one MaxxECU chose for its own Bluetooth dash, so a
    /// channel means the same thing over either link, with throttle position
    /// added because that set leaves it out.
    /// </summary>
    [Fact]
    public void TheCoreSetIsWhatBluetoothSubscribesToPlusTheThrottle()
    {
        Assert.All(MaxxProtocol.Subscribed, c => Assert.Contains(c.Id, MaxxUsbSource.AlwaysLogged));

        Assert.Contains(19, MaxxUsbSource.AlwaysLogged);
        Assert.Equal(MaxxProtocol.Subscribed.Count + 1, MaxxUsbSource.AlwaysLogged.Count);
    }
}
