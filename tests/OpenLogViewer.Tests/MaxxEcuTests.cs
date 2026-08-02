using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The MaxxECU protocol, checked against a recording of a real one.
///
/// <c>maxxecu-opening.bin</c> is 10,007 bytes captured verbatim off a MaxxECU
/// Race after its activation handshake, and <c>maxxecu-steady.bin</c> is 1,200
/// bytes of the stream that follows. Testing the reader against those rather
/// than against frames of my own construction is the point: a parser and a fake
/// written by the same hand agree with each other whether or not either matches
/// the hardware.
/// </summary>
public class MaxxEcuTests
{
    private static byte[] Capture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.Exists(path) ? File.ReadAllBytes(path) : [];
    }

    private static byte[] Opening => Capture("maxxecu-opening.bin");

    private static byte[] Steady => Capture("maxxecu-steady.bin");

    private static List<MaxxFrame> ReadAll(byte[] stream, out MaxxFrameReader reader)
    {
        reader = new MaxxFrameReader();
        reader.Feed(stream);

        var frames = new List<MaxxFrame>();
        while (reader.TryTake(out MaxxFrame? frame) && frame is not null) frames.Add(frame);

        return frames;
    }

    // ----- the recording ----------------------------------------------------

    [Fact]
    public void TheCaptureIsThere()
    {
        Assert.Equal(10007, Opening.Length);
        Assert.Equal(1200, Steady.Length);
    }

    [Fact]
    public void TheRecordingYieldsExactlyTheFramesItContains()
    {
        // 324 frames out of 10,007 bytes with 7 left over — so all but seven
        // bytes of the recording are accounted for. Exact rather than "enough",
        // because the recording never changes and an off-by-a-few means frames
        // are being swallowed, which is how the first version of this reader
        // failed: quietly, at two thirds of them.
        List<MaxxFrame> frames = ReadAll(Opening, out MaxxFrameReader reader);

        Assert.Equal(324, frames.Count);
        Assert.Equal(7, reader.Discarded);
    }

    [Fact]
    public void NoFrameClaimsMoreThanTheProtocolAllows() =>
        Assert.All(ReadAll(Opening, out _), f => Assert.InRange(f.Length, 0, MaxxProtocol.MaximumFrame));

    [Fact]
    public void TheMessageTypesAndCountsMatchTheRecording()
    {
        Dictionary<byte, int> counts = ReadAll(Opening, out _)
            .GroupBy(f => f.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(160, counts[0x01]);   // heartbeats and readings
        Assert.Equal(137, counts[0x03]);   // indexed table dump
        Assert.Equal(1, counts[0x04]);
        Assert.Equal(8, counts[0x06]);
        Assert.Equal(1, counts[0x07]);     // ASCII labels
        Assert.Equal(8, counts[0x09]);
        Assert.Equal(8, counts[0x10]);
        Assert.Equal(1, counts[0x11]);     // ASCII labels
    }

    [Fact]
    public void TheLabelFramesReallyContainLabels()
    {
        // 379 bytes of ASCII in one frame and 68 in another. If the payload were
        // being sliced at the wrong offset these would be noise rather than
        // words — and the 379-byte one is the frame a cruder reader loses,
        // because it is long enough to contain plenty that looks like framing.
        List<MaxxFrame> frames = ReadAll(Opening, out _);

        string first = System.Text.Encoding.ASCII.GetString(
            Assert.Single(frames, f => f.Type == 0x07).Payload);

        string second = System.Text.Encoding.ASCII.GetString(
            Assert.Single(frames, f => f.Type == 0x11).Payload);

        Assert.Contains("Boostlevel", first, StringComparison.Ordinal);
        Assert.Contains("Switch", second, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeartbeatCarriesTheDocumentedValue()
    {
        // Type 0x01 with a two-byte payload is 88 13 = 5000, the rev limit, and
        // is not a reading. Decoding it as telemetry would read far past it.
        MaxxFrame beat = ReadAll(Opening, out _).First(f => f.Type == 0x01 && f.Length == 2);

        Assert.Equal(5000, beat.Payload[0] | (beat.Payload[1] << 8));
    }

    // ----- reassembly -------------------------------------------------------

    [Fact]
    public void AStreamSplitIntoAwkwardPiecesParsesTheSame()
    {
        // Bluetooth serial does not preserve message boundaries, so this is the
        // normal case rather than an edge one.
        List<MaxxFrame> whole = ReadAll(Opening, out _);

        var reader = new MaxxFrameReader();
        var pieces = new List<MaxxFrame>();

        for (int at = 0; at < Opening.Length; at += 7)
        {
            reader.Feed(Opening.AsSpan(at, Math.Min(7, Opening.Length - at)));
            while (reader.TryTake(out MaxxFrame? frame) && frame is not null) pieces.Add(frame);
        }

        Assert.Equal(whole.Count, pieces.Count);
        Assert.Equal(whole[0].Payload, pieces[0].Payload);
        Assert.Equal(whole[^1].Payload, pieces[^1].Payload);

        // The bug this catches: bounding the buffer on every append threw away
        // all but the tail of a large read, so one big feed found 66 frames
        // where the same bytes in sevens found 324.
        Assert.Equal(324, pieces.Count);
    }

    [Fact]
    public void TwoFramesInOneReadAreBothFound()
    {
        List<MaxxFrame> frames = ReadAll(Steady, out _);

        Assert.True(frames.Count > 1);
    }

    [Fact]
    public void RubbishBeforeAFrameIsSkippedAndCounted()
    {
        var reader = new MaxxFrameReader();
        reader.Feed([0xDE, 0xAD, 0xBE, 0xEF]);
        reader.Feed(Steady);

        Assert.True(reader.TryTake(out MaxxFrame? frame));
        Assert.NotNull(frame);
        Assert.Equal(4, reader.Discarded);
    }

    [Fact]
    public void APartialFrameWaitsForTheRest()
    {
        var reader = new MaxxFrameReader();
        reader.Feed(Steady.AsSpan(0, 6));

        Assert.False(reader.TryTake(out MaxxFrame? frame));
        Assert.Null(frame);
    }

    [Fact]
    public void AStreamThatNeverYieldsAFrameDoesNotGrowForever()
    {
        var reader = new MaxxFrameReader();

        for (int i = 0; i < 100; i++) reader.Feed(new byte[256]);

        Assert.False(reader.TryTake(out _));
        Assert.True(reader.Discarded > 0);
    }

    // ----- the subscription and its decode ----------------------------------

    [Fact]
    public void TheSubscriptionMatchesTheTableItIsDecodedWith()
    {
        // These two define the telemetry layout between them. Disagreeing would
        // decode every channel after the disagreement as its neighbour — well
        // formed, plausible, wrong.
        Assert.True(MaxxProtocol.Verify());
    }

    [Fact]
    public void TheSubscriptionFrameIsWellFormed()
    {
        Assert.True(MaxxFrameReader.Read(MaxxProtocol.Subscription, out MaxxFrame? frame, out int used));
        Assert.NotNull(frame);

        Assert.Equal(0x13, frame.Type);
        Assert.Equal(28, frame.Length);
        Assert.Equal(MaxxProtocol.Subscription.Length, used);
    }

    [Fact]
    public void TheActivationIsThreeCompleteFrames()
    {
        List<MaxxFrame> frames = ReadAll([.. MaxxProtocol.Activation], out MaxxFrameReader reader);

        Assert.Equal(3, frames.Count);
        Assert.Equal(0, reader.Discarded);
        Assert.Equal([0x18, 0x15, 0x13], frames.Select(f => f.Type));
    }

    [Fact]
    public void TelemetryDecodesToItsChannels()
    {
        // A frame built to the documented layout: RPM 3000, IAT 21.0, CLT 85.0,
        // MAP 101.3, battery 13.80, lambda 0.980.
        var payload = new byte[MaxxProtocol.TelemetryLength];
        Put(payload, 0, 3000);
        Put(payload, 1, 210);
        Put(payload, 2, 850);
        Put(payload, 3, 1013);
        Put(payload, 4, 1380);
        Put(payload, 5, 980);

        Span<double> values = stackalloc double[MaxxProtocol.Subscribed.Count];
        Assert.True(MaxxProtocol.TryDecode(new MaxxFrame(0x01, payload), values));

        Assert.Equal(3000, values[0]);
        Assert.Equal(21.0, values[1], 4);
        Assert.Equal(85.0, values[2], 4);
        Assert.Equal(101.3, values[3], 4);
        Assert.Equal(13.80, values[4], 4);
        Assert.Equal(0.980, values[5], 4);
    }

    [Fact]
    public void ASignedChannelGoesNegative()
    {
        // Intake air temperature below freezing, and ignition angle after top
        // dead centre. Read unsigned, −10 °C becomes 6553.5.
        var payload = new byte[MaxxProtocol.TelemetryLength];
        Put(payload, 1, unchecked((ushort)-100));
        Put(payload, 9, unchecked((ushort)-50));

        Span<double> values = stackalloc double[MaxxProtocol.Subscribed.Count];
        Assert.True(MaxxProtocol.TryDecode(new MaxxFrame(0x01, payload), values));

        Assert.Equal(-10.0, values[1], 4);
        Assert.Equal(-5.0, values[9], 4);
    }

    [Fact]
    public void TheShortHeartbeatIsNotDecodedAsTelemetry()
    {
        // Type 0x01 arrives with two lengths, and the two-byte one is not a
        // reading. Length is part of a message's identity here.
        Span<double> values = stackalloc double[MaxxProtocol.Subscribed.Count];

        Assert.False(MaxxProtocol.TryDecode(new MaxxFrame(0x01, [0x88, 0x13]), values));
    }

    [Fact]
    public void AnotherMessageTypeIsNotDecodedAsTelemetry()
    {
        Span<double> values = stackalloc double[MaxxProtocol.Subscribed.Count];
        var payload = new byte[MaxxProtocol.TelemetryLength];

        Assert.False(MaxxProtocol.TryDecode(new MaxxFrame(0x03, payload), values));
    }

    private static void Put(byte[] payload, int index, ushort value)
    {
        payload[index * 2] = (byte)value;
        payload[index * 2 + 1] = (byte)(value >> 8);
    }
}
