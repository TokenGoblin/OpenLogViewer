using System.Buffers.Binary;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>Raised when a reply is missing, malformed, or fails its checksum.</summary>
public sealed class EcuProtocolException(string message) : Exception(message)
{
    /// <summary>
    /// True when the ECU understood the request and declined it, rather than the
    /// reply going astray. Worth telling apart: a refusal repeated is refused
    /// again, so retrying one only spends the timeout.
    /// </summary>
    public bool Refused { get; init; }
}

/// <summary>Somewhere to send bytes and read them back — a serial port, or a fake in a test.</summary>
public interface IEcuTransport : IDisposable
{
    bool IsOpen { get; }

    void Open();

    void Close();

    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// How long a write may block before the transport gives up on it.
    ///
    /// Adjustable because one operation needs far longer than the rest: a
    /// controller writing its flash stops servicing the link, so the bytes still
    /// buffered for it cannot be handed over and the write that delivered the
    /// burn command is itself what blocks. Everything else should fail fast —
    /// Windows' incoming Bluetooth port never accepts a write at all, and
    /// waiting on it is time spent learning nothing.
    ///
    /// Ignored by transports that have no such notion, which is why this has a
    /// default rather than being forced on every implementation.
    /// </summary>
    TimeSpan WriteTimeout
    {
        get => TimeSpan.Zero;
        set { }
    }

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
/// The commands named here are the reads: a signature, a version, a realtime
/// block. Write and burn are absent not because they are unsupported but because
/// they are not fixed — each firmware declares its own in its INI, and
/// <c>EcuConnection</c> parses them from there rather than assuming them. The
/// care they need is real either way: a wrong byte written to a running engine
/// is not a bug that can be undone.
/// </summary>
public static class MsProtocol
{
    /// <summary>Command that returns the firmware signature, e.g. "MS3 Format 0569.00".</summary>
    public const byte QuerySignature = (byte)'Q';

    /// <summary>Command that returns the longer version string.</summary>
    public const byte QueryVersion = (byte)'S';

    /// <summary>
    /// Every command that makes an ECU say what it is, in the order worth asking.
    ///
    /// There is no single one, and the same letter means different things on
    /// different firmware: MegaSquirt answers 'Q' with its signature and 'S'
    /// with a build string, while rusEFI answers 'S' with its signature, 'V'
    /// with a build string, and refuses 'Q' outright. Rather than deciding in
    /// advance which family is on the other end, ask all three and let the one
    /// that matches an INI settle it.
    /// </summary>
    public static ReadOnlySpan<byte> IdentityCommands => "SQV"u8;

    private const byte ReadCommand = (byte)'r';

    /// <summary>The page holding the realtime block.</summary>
    private const byte RealtimeTable = 7;

    /// <summary>
    /// The bit a reply's status byte sets to report a failure.
    ///
    /// Every error in this protocol carries it — underrun 0x80, CRC 0x82,
    /// unrecognised 0x83, out of range 0x84, framing 0x8D — and every way of
    /// saying yes does not: 0x00 acknowledges a request, 0x04 a burn, 0x07 a
    /// controller command.
    /// </summary>
    private const byte Failed = 0x80;

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

        // An error is marked by the high bit, not by being anything other than
        // zero. There is more than one way for this protocol to say yes: 0x00 is
        // a plain acknowledgement, 0x04 acknowledges a burn and 0x07 a
        // controller command, while every failure — underrun 0x80, CRC 0x82,
        // unrecognised 0x83, out of range 0x84, framing 0x8D — sets it.
        //
        // Insisting on 0x00 made a rusEFI's successful burn read as a refusal.
        // The board had written its flash, answered 0x04 to say so, and was told
        // the burn had been declined; the value was there after a reboot. A
        // write reported as failed is the one error worth going out of the way
        // to avoid, because the answer to it is to write again.
        if ((body[0] & Failed) != 0)
            throw new EcuProtocolException($"The ECU refused the request (status 0x{body[0]:X2}).")
            {
                Refused = true,
            };

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
