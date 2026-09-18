using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>One message from or to a MaxxECU.</summary>
public sealed record MaxxFrame(byte Type, byte[] Payload)
{
    /// <summary>Payload length, which the frame states after the payload rather than before it.</summary>
    public int Length => Payload.Length;
}

/// <summary>
/// The binary stream a MaxxECU speaks over Bluetooth.
///
/// Nothing to do with the TunerStudio protocol the MegaSquirt and rusEFI paths
/// use — different framing, no signature to ask for, and no notion of reading
/// memory. It carries telemetry and nothing else, which is why a MaxxECU gets
/// gauges and logging but no calibration.
///
/// Reverse-engineered against a MaxxECU Race; the framing below was verified on
/// 1,181 of 1,181 captured frames.
///
/// The checksum used to be the limit here — an exhaustive search over every
/// 16-bit CRC parameterisation failed to reproduce it, so the activation and
/// subscription messages could only be replayed and the channel list could not
/// be chosen. It is identified now, and it was hiding in plain sight: see
/// <see cref="Checksum"/>. The captured messages below are kept as captured
/// anyway, because a constant recorded off the wire is evidence and a constant
/// this file computes is only a claim — the tests then check that composing them
/// reproduces the recording byte for byte.
///
/// The same framing carries over every link a MaxxECU offers, because the
/// Bluetooth and Wi-Fi gateway adds nothing of its own to an application
/// payload: it only attaches and strips a per-transport header. So a USB cable
/// and a paired Bluetooth port speak exactly what is below, with no difference
/// for this code to know about.
/// </summary>
public static class MaxxProtocol
{
    /// <summary>Marks the start of every frame, after the type byte.</summary>
    public static ReadOnlySpan<byte> Magic => [0x77, 0xAA, 0x77];

    /// <summary>Ends every frame, in both directions.</summary>
    public static ReadOnlySpan<byte> Trailer => [0xCC, 0x77, 0xAA, 0x44];

    /// <summary>Bytes a frame carries besides its payload: type, magic, length, checksum, trailer.</summary>
    public const int Overhead = 1 + 3 + 2 + 2 + 4;

    /// <summary>Largest frame seen is 391 bytes; this is well clear of it.</summary>
    public const int MaximumFrame = 512;

    /// <summary>The message type carrying subscribed channel values.</summary>
    public const byte Telemetry = 0x01;

    /// <summary>Asks the ECU which firmware it is running.</summary>
    public const byte VersionRequest = 0x15;

    /// <summary>Asks for the channel names, in the newer of the two forms.</summary>
    public const byte NamesRequest = 0x18;

    /// <summary>Tells the ECU which channels to stream.</summary>
    public const byte RealtimeSetup = 0x13;

    /// <summary>
    /// The ECU's own numbering runs 0–2047: the dispatcher masks every channel
    /// id to eleven bits, so a larger number is not refused, it subscribes to a
    /// different channel.
    /// </summary>
    public const int HighestChannelId = 0x7FF;

    /// <summary>
    /// The checksum every frame carries, in both directions.
    ///
    /// This is the STM32's CRC peripheral rather than anything a host would
    /// reach for: CRC-32/MPEG-2 — polynomial 0x04C11DB7, initialised to all
    /// ones, neither input nor output reflected, no final XOR — of which only
    /// the low sixteen bits are kept. It is computed over the frame from its
    /// first byte up to but not including the checksum itself, so the type, the
    /// magic, the payload and the length are all covered and the trailer is not.
    ///
    /// The detail that hides it from a search over checksum algorithms is that
    /// the peripheral consumes <em>words</em>, not bytes. The frame's bytes are
    /// read back four at a time as little-endian 32-bit values, which reverses
    /// each group of four before it reaches the polynomial, and a body that is
    /// not a whole number of words is padded with zeroes to the next one. An
    /// exhaustive sweep of 16-bit CRC parameterisations was never going to find
    /// that, which is why this used to be recorded here as unidentified and the
    /// activation and subscription frames could only be replayed.
    ///
    /// Confirmed against every frame of both recordings — 411 of 411, spanning
    /// bodies of every alignment but one — and against the four frames this file
    /// had already captured to replay.
    /// </summary>
    public static ushort Checksum(ReadOnlySpan<byte> body) => (ushort)Crc32(body);

    /// <summary>
    /// The whole 32-bit result, of which <see cref="Checksum"/> keeps the low
    /// half.
    ///
    /// Both halves are used, by different links. The Bluetooth framing has a
    /// 16-bit checksum field and throws the top away; the USB framing keeps all
    /// thirty-two. It is the same arithmetic over the same bytes either way,
    /// which is the single strongest sign that the two are one controller's work
    /// rather than two protocols that happen to share a cable.
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> body)
    {
        uint crc = 0xFFFFFFFF;

        for (int at = 0; at < body.Length; at += 4)
        {
            uint word = 0;

            for (int b = 0; b < 4 && at + b < body.Length; b++)
                word |= (uint)body[at + b] << (8 * b);

            crc ^= word;

            for (int bit = 0; bit < 32; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }

        return crc;
    }

    /// <summary>
    /// Builds a frame around a payload.
    ///
    /// What <see cref="Checksum"/> buys: until it was identified the only frames
    /// that could be sent were ones that had been recorded coming out of MTune,
    /// which is why the channel list below is a fixed fourteen rather than a
    /// choice. Anything the ECU understands can now be composed.
    /// </summary>
    public static byte[] Frame(byte type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumFrame - Overhead)
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"A {payload.Length} byte payload makes a frame longer than the {MaximumFrame} "
                + "bytes this protocol carries.");

        var frame = new byte[Overhead + payload.Length];

        frame[0] = type;
        Magic.CopyTo(frame.AsSpan(1));
        payload.CopyTo(frame.AsSpan(4));

        // The length sits after the payload rather than before it, which is what
        // makes the stream awkward to parse forwards. See MaxxFrameReader.
        BinaryPrimitives.WriteUInt16LittleEndian(
            frame.AsSpan(4 + payload.Length), (ushort)payload.Length);

        BinaryPrimitives.WriteUInt16LittleEndian(
            frame.AsSpan(6 + payload.Length), Checksum(frame.AsSpan(0, 6 + payload.Length)));

        Trailer.CopyTo(frame.AsSpan(8 + payload.Length));

        return frame;
    }

    /// <summary>
    /// Builds a subscription asking for these channels, in this order.
    ///
    /// A telemetry frame lists the values in subscription order and says nothing
    /// about which channel each one is, so whoever sends this owns the decode.
    /// </summary>
    public static byte[] Subscribe(IEnumerable<int> channelIds)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        int[] ids = [.. channelIds];

        if (ids.Length == 0)
            throw new ArgumentException(
                "A subscription with no channels leaves the ECU streaming nothing.",
                nameof(channelIds));

        foreach (int id in ids)
            if (id is < 0 or > HighestChannelId)
                throw new ArgumentOutOfRangeException(
                    nameof(channelIds),
                    $"Channel {id} is outside the 0–{HighestChannelId} the ECU numbers. It would "
                    + "not be refused — the dispatcher masks it to eleven bits and subscribes to "
                    + "whatever that lands on.");

        var payload = new byte[ids.Length * 2];

        for (int i = 0; i < ids.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 2), (ushort)ids[i]);

        return Frame(RealtimeSetup, payload);
    }

    /// <summary>
    /// What wakes the ECU up.
    ///
    /// A MaxxECU that has not seen an mDash session since it was powered on
    /// accepts a Bluetooth socket, reports itself connected, and sends nothing
    /// at all — indefinitely. These three frames unlock it.
    ///
    /// Replayed byte for byte. They were captured nine times across separate
    /// sessions and were identical every time: no session token, no sequence
    /// number, no challenge. That is what makes replaying them sound rather
    /// than a guess.
    ///
    /// The three are, in order: ask for the channel names, ask for the firmware
    /// version, and subscribe to the rev limit. Nothing in any of them is
    /// specific to Bluetooth — they are ordinary commands for the main
    /// controller, which is the same controller a USB cable reaches.
    /// </summary>
    public static ReadOnlySpan<byte> Activation =>
    [
        0x18, 0x77, 0xAA, 0x77, 0x00, 0x00, 0x80, 0x2D, 0xCC, 0x77, 0xAA, 0x44,
        0x15, 0x77, 0xAA, 0x77, 0x00, 0x00, 0x88, 0xCE, 0xCC, 0x77, 0xAA, 0x44,
        0x13, 0x77, 0xAA, 0x77, 0xE7, 0x01, 0x02, 0x00, 0xBF, 0x6C, 0xCC, 0x77, 0xAA, 0x44,
    ];

    /// <summary>
    /// Asks for the fourteen channels below, in this order.
    ///
    /// Activation alone only makes the ECU talk; it sends configuration and
    /// label dumps and never a reading. Values arrive only for channels that
    /// were subscribed to, and the reply lists them in subscription order — so
    /// this frame and <see cref="Subscribed"/> define the telemetry layout
    /// between them, and disagreeing would decode every channel after the
    /// disagreement as its neighbour.
    /// </summary>
    public static ReadOnlySpan<byte> Subscription =>
    [
        0x13, 0x77, 0xAA, 0x77,
        0x3D, 0x00, 0x11, 0x00, 0x12, 0x00, 0x14, 0x00, 0x15, 0x00, 0x05, 0x00, 0xE7, 0x01,
        0x09, 0x00, 0x6C, 0x00, 0x3C, 0x00, 0x40, 0x00, 0x59, 0x00, 0xF6, 0x00, 0x85, 0x03,
        0x1C, 0x00,
        0xEA, 0x4A,
        0xCC, 0x77, 0xAA, 0x44,
    ];

    /// <summary>
    /// The channels <see cref="Subscription"/> asks for, in order.
    ///
    /// A channel's offset in a telemetry frame is twice its position here, so
    /// the two must agree; <see cref="Verify"/> checks that rather than trusting
    /// it. Names, units and scales are MTune's own.
    /// </summary>
    public static IReadOnlyList<MaxxChannel> Subscribed { get; } =
    [
        new(61, "RPM", "RPM", 1, false, 0),
        new(17, "IAT", "deg C", 0.1, true, 1),
        new(18, "CLT", "deg C", 0.1, true, 1),
        new(20, "MAP", "kPa", 0.1, false, 1),
        new(21, "Battery", "V", 0.01, false, 2),
        new(5, "Lambda", "", 0.001, false, 3),
        new(487, "Rev limit", "RPM", 1, false, 0),
        new(9, "User AIN1", "", 0.1, true, 1),
        new(108, "Error count", "", 1, false, 0),
        new(60, "Ignition angle", "BTDC", 0.1, true, 1),
        new(64, "Fuel duty", "%", 0.1, false, 1),
        new(89, "Speed", "km/h", 0.1, false, 1),
        new(246, "Ethanol", "%", 0.1, false, 1),
        new(901, "Torque", "Nm", 1, true, 0),
    ];

    /// <summary>Payload length of a telemetry frame for <see cref="Subscribed"/>.</summary>
    public static int TelemetryLength => Subscribed.Count * 2;

    /// <summary>
    /// Checks the subscription frame against the channel table it is decoded
    /// with, so a mismatch is a refusal to start rather than confident nonsense.
    /// </summary>
    public static bool Verify()
    {
        ReadOnlySpan<byte> frame = Subscription;

        if (frame.Length != Overhead + TelemetryLength) return false;

        if (MaxxFrameReader.Read(frame, out MaxxFrame? sent, out _) is not true || sent is null) return false;
        if (sent.Type != 0x13 || sent.Length != TelemetryLength) return false;

        for (int i = 0; i < Subscribed.Count; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(sent.Payload.AsSpan(i * 2)) != Subscribed[i].Id)
                return false;

        return true;
    }

    /// <summary>
    /// Decodes a telemetry frame into one value per subscribed channel.
    ///
    /// Refuses a frame of the wrong length rather than reading past its payload.
    /// Type 0x01 arrives with two different lengths — a two-byte heartbeat as
    /// well as the reading block — so length is part of a message's identity
    /// here, not merely its size.
    /// </summary>
    public static bool TryDecode(MaxxFrame frame, Span<double> values)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Type != Telemetry || frame.Length != TelemetryLength) return false;
        if (values.Length < Subscribed.Count) return false;

        for (int i = 0; i < Subscribed.Count; i++)
        {
            ReadOnlySpan<byte> at = frame.Payload.AsSpan(i * 2);

            double raw = Subscribed[i].IsSigned
                ? BinaryPrimitives.ReadInt16LittleEndian(at)
                : BinaryPrimitives.ReadUInt16LittleEndian(at);

            values[i] = raw * Subscribed[i].Scale;
        }

        return true;
    }
}

/// <summary>One subscribed channel: what it is called and how to scale it.</summary>
public sealed record MaxxChannel(
    int Id, string Name, string Units, double Scale, bool IsSigned, int Digits);
