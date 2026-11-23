using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// The protocol a MaxxECU speaks over its USB cable.
///
/// Not the same protocol as the Bluetooth one, which is the thing that made USB
/// look impossible for a while: the framing shares no byte with it. There is no
/// <c>77 AA 77</c> magic, no trailer, no activation to replay, and the ECU
/// answers nothing at all if you send it the Bluetooth conversation — which it
/// does not, at any baud rate, with any handshake, silently.
///
/// What the two do share is the checksum, and that is what says they are one
/// controller's work: <see cref="MaxxProtocol.Crc32"/>, the STM32's own CRC
/// peripheral fed as little-endian words. Bluetooth keeps its low sixteen bits,
/// USB keeps all thirty-two.
///
/// This one is the better protocol of the two. It is a memory interface rather
/// than a subscription — every request names an offset and a length — so it can
/// read and write rather than only listen, and its telemetry carries the channel
/// id beside every value instead of relying on both ends having agreed a layout
/// in advance. Nothing here has to be told what to expect.
///
/// Recovered by watching MTune 1.161 connect to a MaxxECU Race over USB, and
/// checked against that recording: 4,038 of 4,038 request checksums and 3,560 of
/// 3,560 reply checksums.
/// </summary>
public static class MaxxUsbProtocol
{
    /// <summary>
    /// The rate the ECU's UART runs at.
    ///
    /// There is nothing to discover here and no negotiating: MTune sets 9,600
    /// and then immediately 921,600 on every open, and the second is the one
    /// that matters. At any other rate the ECU is silent, which is
    /// indistinguishable from an ECU that is not there.
    /// </summary>
    public const int BaudRate = 921600;

    /// <summary>A request reads from the ECU.</summary>
    public const byte Read = 0x00;

    /// <summary>A request writes to it, with the payload following the header.</summary>
    public const byte Write = 0x01;

    /// <summary>
    /// How many bytes of telemetry are waiting. Always read as two bytes, and
    /// the answer is the length to ask <see cref="Take"/> for.
    /// </summary>
    public const byte Waiting = 0x13;

    /// <summary>Takes the waiting telemetry.</summary>
    public const byte Take = 0x12;

    /// <summary>
    /// What MTune sends while it is looking for an ECU, once per poll, reading a
    /// single byte. An answer to this is the whole of "something is there".
    /// </summary>
    public const byte Hello = 0x16;

    /// <summary>The status byte of a reply that worked.</summary>
    public const byte Ok = 0x80;

    /// <summary>Every request is this long: a six-byte header and its checksum.</summary>
    public const int RequestLength = 10;

    /// <summary>A reply carries a status byte and a checksum besides its data.</summary>
    public const int ReplyOverhead = 1 + 4;

    /// <summary>
    /// The largest reply seen, which is a 512-byte block read. Requests for more
    /// were never observed, so this is also as much as is asked for.
    /// </summary>
    public const int MaximumData = 512;

    /// <summary>
    /// Builds a request.
    ///
    /// <c>[direction][command][offset u16][length u16][crc32]</c>, all
    /// little-endian, with the checksum over the six header bytes.
    /// </summary>
    public static byte[] Request(byte direction, byte command, int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, ushort.MaxValue);

        var request = new byte[RequestLength];

        request[0] = direction;
        request[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), (ushort)length);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(6), MaxxProtocol.Crc32(request.AsSpan(0, 6)));

        return request;
    }

    /// <summary>Asks how much telemetry is waiting.</summary>
    public static byte[] AskWhatIsWaiting() => Request(Read, Waiting, 0, 2);

    /// <summary>Asks for that many bytes of it.</summary>
    public static byte[] AskFor(int bytes) => Request(Read, Take, 0, bytes);

    /// <summary>The probe that finds out whether a MaxxECU is on the other end.</summary>
    public static byte[] AreYouThere() => Request(Read, Hello, 0, 1);

    /// <summary>How long a reply to a request for this many bytes should be.</summary>
    public static int ReplyLength(int dataLength) => dataLength + ReplyOverhead;

    /// <summary>
    /// Checks a reply and hands back its data.
    ///
    /// Refuses rather than trusting: a reply carries no echo of the request that
    /// produced it, so a stale one from an earlier round is otherwise decoded as
    /// the answer to this one. The length and the checksum together are all
    /// there is to go on, and both are checked.
    /// </summary>
    public static bool TryReadReply(ReadOnlySpan<byte> reply, int dataLength, out byte[] data)
    {
        data = [];

        if (dataLength < 0 || reply.Length != ReplyLength(dataLength)) return false;
        if (reply[0] != Ok) return false;

        ReadOnlySpan<byte> covered = reply[..(1 + dataLength)];

        if (MaxxProtocol.Crc32(covered) != BinaryPrimitives.ReadUInt32LittleEndian(reply[(1 + dataLength)..]))
            return false;

        data = reply.Slice(1, dataLength).ToArray();
        return true;
    }

    /// <summary>
    /// Reads the telemetry payload into <paramref name="into"/>, returning how
    /// many channels it carried.
    ///
    /// The payload is a run of four-byte pairs — a channel id and its raw value,
    /// both 16-bit little-endian — and it carries only the channels whose value
    /// has moved since the last one. So this updates rather than replaces, and
    /// a payload of sixty bytes is fifteen channels that changed and not the
    /// whole of what the ECU knows.
    ///
    /// The ids are the same ones MTune's channel definitions use and the same
    /// ones the Bluetooth path subscribes by, so a value means the same thing
    /// whichever cable it arrived over.
    /// </summary>
    public static int ReadUpdates(ReadOnlySpan<byte> payload, IDictionary<int, ushort> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        int taken = 0;

        for (int at = 0; at + 4 <= payload.Length; at += 4)
        {
            into[BinaryPrimitives.ReadUInt16LittleEndian(payload[at..])] =
                BinaryPrimitives.ReadUInt16LittleEndian(payload[(at + 2)..]);

            taken++;
        }

        return taken;
    }

    /// <summary>
    /// Whether a payload looks like telemetry at all.
    ///
    /// The ids in one arrive in ascending order and never repeat — true of all
    /// 1,765 payloads in the recording — so a payload that breaks either rule is
    /// not a list of channels, whatever its length says. Cheap, and the one
    /// check that catches a stream read at the wrong offset, which otherwise
    /// produces channel numbers that exist and values that are nonsense.
    /// </summary>
    public static bool LooksLikeTelemetry(ReadOnlySpan<byte> payload)
    {
        if (payload.Length % 4 != 0) return false;

        int previous = -1;

        for (int at = 0; at + 4 <= payload.Length; at += 4)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(payload[at..]);
            if (id <= previous) return false;

            previous = id;
        }

        return true;
    }
}
