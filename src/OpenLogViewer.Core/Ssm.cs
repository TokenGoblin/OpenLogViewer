using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>
/// One value an ECU will report over SSM: where it lives and what it means.
/// </summary>
/// <param name="Name">What to call the channel.</param>
/// <param name="Address">Where the first byte lives in the ECU's address space.</param>
/// <param name="Bytes">How many consecutive bytes make up the value, most significant first.</param>
/// <param name="Units">What the scaled value is in.</param>
/// <param name="Scale">Multiplied by the raw value.</param>
/// <param name="Offset">Added after scaling — so a temperature is raw minus 40.</param>
/// <param name="Digits">Decimals worth showing.</param>
/// <param name="Low">Bottom of the gauge, where one is wanted.</param>
/// <param name="High">Top of the gauge.</param>
public sealed record SsmParameter(
    string Name,
    int Address,
    int Bytes = 1,
    string Units = "",
    double Scale = 1,
    double Offset = 0,
    int Digits = 0,
    double Low = 0,
    double High = 255)
{
    /// <summary>Every address this parameter occupies, in order.</summary>
    public IEnumerable<int> Addresses => Enumerable.Range(Address, Math.Max(1, Bytes));

    /// <summary>
    /// The reading, from the raw bytes in the order the ECU returned them.
    ///
    /// Big-endian, which is what SSM is: engine speed's high byte lives at the
    /// lower address. Read the other way round a 900 rpm idle comes out at
    /// 13,830, which is wrong in a way nobody would fail to notice — but the same
    /// mistake on a two-byte temperature is quietly plausible.
    /// </summary>
    public double Read(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) return double.NaN;

        long value = 0;
        foreach (byte b in raw) value = (value << 8) | b;

        return (value * Scale) + Offset;
    }

    /// <summary>Whether this describes something a gauge could be drawn for.</summary>
    public bool IsUsable =>
        Name.Length > 0 && Address >= 0 && Bytes is > 0 and <= 4 && Scale != 0;
}

/// <summary>
/// Talking to a Subaru ECU in its own language, over CAN.
///
/// SSM is Subaru's diagnostic protocol and it reaches things OBD2 has no notion
/// of — what the ECU has learnt rather than what it is measuring. Knock
/// correction, fine knock learning, the ignition advance multiplier, fuelling
/// trims the standard does not expose: the values that say whether an engine is
/// happy, none of which appear in mode 01 at any speed.
///
/// The received wisdom is that an ELM327-compatible adapter cannot speak it, and
/// over the older K-line cars that is true — SSM frames itself there, with a
/// header, a length and a checksum the adapter will not produce. Over CAN the
/// ISO-TP layer does the framing, the command bytes alone are the payload, and a
/// 2014 Crosstrek answers an OBDLink r2.6 perfectly well. Confirmed on a running
/// car: engine speed read over SSM landed between two OBD2 readings taken either
/// side of it, and coolant returned the identical raw byte to PID 05.
///
/// One byte per request, and that is not a shortcut taken here. The ECU does not
/// implement the block read; a two-address request needs eight bytes and the
/// adapter will not send more than seven, with auto-formatting on or off; and
/// the extended send command that exists to solve exactly this is absent from
/// this firmware. Measured at 146 ms a byte, so eight values is about 0.85 Hz.
///
/// Which suits what SSM is for. These are learnt values that move over seconds
/// and minutes, not transients — for watching what an ECU has decided about a
/// drive it is ample, and for catching a misfire it is useless. Both are said
/// out loud rather than left to be discovered.
/// </summary>
public static class Ssm
{
    /// <summary>Read one or more addresses. The reply echoes <see cref="ReadEcho"/>.</summary>
    public const byte ReadAddresses = 0xA8;

    /// <summary>A positive answer to <see cref="ReadAddresses"/>.</summary>
    public const byte ReadEcho = 0xE8;

    /// <summary>Where the engine module listens.</summary>
    public const string EngineHeader = "7E0";

    /// <summary>And answers.</summary>
    public const string EngineReplies = "7E8";

    /// <summary>
    /// The most addresses one request can carry.
    ///
    /// One, on the hardware this was proven against, and the reason is worth
    /// keeping next to the number. The request is the command, a mandatory
    /// padding byte and three bytes per address, so two addresses is eight bytes
    /// where an ELM327 send caps at seven. Every route round it was tried on a
    /// live car and closed: the ECU refuses the block read, refuses a request
    /// without the padding byte, and the adapter refuses eight bytes whether or
    /// not it is formatting the frame itself.
    ///
    /// Left as a constant rather than inlined because a newer adapter would lift
    /// it, and when one does this is the only line that needs to change.
    /// </summary>
    public const int AddressesPerRequest = 1;

    /// <summary>The commands that put an adapter into a state where SSM can be spoken.</summary>
    public static IReadOnlyList<string> Setup { get; } =
    [
        $"ATSH{EngineHeader}",
        $"ATCRA{EngineReplies}",
        $"ATFCSH{EngineHeader}",
        "ATFCSD300000",
        "ATFCSM1",
    ];

    /// <summary>Puts an adapter back to ordinary OBD2 addressing.</summary>
    public static IReadOnlyList<string> Restore { get; } = ["ATCRA", "ATFCSM0", "ATSH7DF"];

    /// <summary>
    /// The request that reads a run of addresses.
    ///
    /// The padding byte after the command is not optional. Sent without it the
    /// ECU parses the request and refuses it for length — which is a useful thing
    /// to have learnt, since it proves the vehicle is reading the command rather
    /// than ignoring it.
    /// </summary>
    public static string ReadRequest(params int[] addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        if (addresses.Length == 0) throw new ArgumentException("No addresses.", nameof(addresses));

        var text = new System.Text.StringBuilder();
        text.Append(ReadAddresses.ToString("X2", CultureInfo.InvariantCulture));
        text.Append("00");

        foreach (int address in addresses)
        {
            if (address is < 0 or > 0xFFFFFF)
                throw new ArgumentOutOfRangeException(
                    nameof(addresses), address, "An SSM address is three bytes.");

            text.Append(address.ToString("X6", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// The data bytes out of a reply, or empty where it was not one.
    ///
    /// A positive answer is the echo followed by one byte per address asked for.
    /// A refusal is 0x7F, the command, and why — 0x13 for a length the ECU did
    /// not expect, 0x12 for a sub-function it does not support — and those are
    /// told apart from silence because they mean the vehicle is listening.
    /// </summary>
    public static byte[] ReadReply(string? reply, int expected)
    {
        if (string.IsNullOrWhiteSpace(reply)) return [];

        foreach (string line in reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Span<byte> bytes = stackalloc byte[32];
            int got = Elm327.Unhex(line, bytes);

            if (got < 1 + expected || bytes[0] != ReadEcho) continue;

            return bytes.Slice(1, expected).ToArray();
        }

        return [];
    }

    /// <summary>
    /// Whether a reply is the ECU refusing rather than the link being silent.
    ///
    /// Worth separating. A refusal proves SSM reached a module that understood
    /// it, which is the difference between "this car does not speak SSM" and
    /// "that address was wrong" — and only one of those is worth another attempt.
    /// </summary>
    public static bool Refused(string? reply) =>
        reply is not null
        && reply.Contains("7F", StringComparison.OrdinalIgnoreCase)
        && reply.Contains("A8", StringComparison.OrdinalIgnoreCase);
}
