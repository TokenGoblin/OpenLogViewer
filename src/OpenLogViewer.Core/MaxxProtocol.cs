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
/// 1,181 of 1,181 captured frames. The activation and subscription messages are
/// replayed verbatim because their checksum algorithm is unidentified — an
/// exhaustive search over every 16-bit CRC parameterisation, and the usual
/// sum and Fletcher variants, failed to reproduce it. That is a real limit
/// rather than laziness: it means the channel list cannot be chosen, only
/// picked from the subscriptions that were captured.
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
