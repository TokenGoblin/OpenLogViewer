using System.Buffers.Binary;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>Raised when a reply is missing, malformed, or fails its checksum.</summary>
public sealed class EcuProtocolException(string message) : Exception(message);

/// <summary>Somewhere to send bytes and read them back — a serial port, or a fake in a test.</summary>
public interface IEcuTransport : IDisposable
{
    bool IsOpen { get; }

    void Open();

    void Close();

    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Reads until <paramref name="count"/> bytes arrive or the timeout passes.
    /// Returns what it got, which may be short.
    /// </summary>
    int Read(Span<byte> buffer, TimeSpan timeout);

    void DiscardInput();
}

/// <summary>
/// The serial protocol MegaSquirt 2 and 3 speak to TunerStudio.
///
/// Every request is framed as a length, a payload and a CRC32; every reply comes
/// back as a length, a status byte, the data and a CRC32. The framing is worth
/// having even on a USB cable — it is the only thing that catches a truncated or
/// corrupted block, which is the normal failure on a Bluetooth link.
///
/// Only reads are implemented here, deliberately. This software has no reason to
/// write to an ECU, and a wrong byte written to a running engine is not a bug
/// that can be undone.
/// </summary>
public static class MsProtocol
{
    /// <summary>Command that returns the firmware signature, e.g. "MS3 Format 0569.00".</summary>
    public const byte QuerySignature = (byte)'Q';

    /// <summary>Command that returns the longer version string.</summary>
    public const byte QueryVersion = (byte)'S';

    private const byte ReadCommand = (byte)'r';

    /// <summary>The page holding the realtime block.</summary>
    private const byte RealtimeTable = 7;

    /// <summary>Reply status byte meaning the request was understood.</summary>
    private const byte Ok = 0x00;

    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var framed = new byte[2 + payload.Length + 4];

        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)payload.Length);
        payload.CopyTo(framed.AsSpan(2));
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(2 + payload.Length), Crc32(payload));

        return framed;
    }

    /// <summary>
    /// Pulls the data out of a reply, checking its length and checksum.
    ///
    /// The checksum is the point of the exercise: a short read on a flaky link
    /// gives bytes that decode into perfectly plausible readings, and only the
    /// CRC tells you they are not real.
    /// </summary>
    public static byte[] Unframe(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 7)
            throw new EcuProtocolException($"Reply was {reply.Length} bytes; a framed reply is at least 7.");

        int length = BinaryPrimitives.ReadUInt16BigEndian(reply);
        if (length < 1 || 2 + length + 4 > reply.Length)
            throw new EcuProtocolException(
                $"Reply declares {length} bytes but {reply.Length - 6} arrived.");

        ReadOnlySpan<byte> body = reply.Slice(2, length);
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(2 + length, 4));
        uint actual = Crc32(body);

        if (declared != actual)
            throw new EcuProtocolException(
                $"Reply failed its checksum ({actual:X8} against {declared:X8}); the link dropped bytes.");

        if (body[0] != Ok)
            throw new EcuProtocolException($"The ECU refused the request (status 0x{body[0]:X2}).");

        return body[1..].ToArray();
    }

    /// <summary>Builds a read of the realtime page. No other page is ever asked for.</summary>
    public static byte[] RealtimeRequest(int size, byte canId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        return
        [
            ReadCommand, canId, RealtimeTable,
            0, 0,
            (byte)(size >> 8), (byte)(size & 0xFF),
        ];
    }

    /// <summary>Trims the padding an ECU puts on its signature.</summary>
    public static string ReadSignature(ReadOnlySpan<byte> data) =>
        Encoding.ASCII.GetString(data).TrimEnd('\0', ' ', '\r', '\n');

    /// <summary>CRC-32, as used by Ethernet and Zip; the one MegaSquirt frames with.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }

        return ~crc;
    }
}
