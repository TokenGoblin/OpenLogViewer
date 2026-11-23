using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// The framing of WCP, the WiCAN Claude Protocol: a line-based control lane that
/// a custom MeatPi WiCAN Pro firmware runs on the same USB port as its ELM327.
///
/// <para>
/// The contract lives in <c>diagnosis/wican-claude-protocol.md</c>, outside this
/// repository, and that file wins any disagreement with this one. In short: a
/// line the host sends that starts with <c>#</c> is a WCP request rather than an
/// ELM327 command, and everything the firmware sends back for WCP starts with
/// <c>#</c> (a reply) or <c>!</c> (an event) — two characters ELM327 output never
/// begins a line with. Every WCP line, both ways, ends in <c>*HH</c>: a CRC-8
/// over everything before the star.
/// </para>
///
/// <para>
/// <b>Why a checksum on a USB cable.</b> Not for the cable. The WiCAN's Serial-B
/// is a UART behind a CH342 with no flow control, and the ESP32 stops servicing
/// it for milliseconds at a time while it writes flash — bytes are lost, not
/// corrupted, and a line with a byte missing from the middle reads as a perfectly
/// plausible line saying something else. "<c>can send 7E0 0102</c>" with a digit
/// dropped is a different frame to a different module. The CRC is what turns
/// that into a refusal.
/// </para>
///
/// <para>
/// <b>Written against a simulation only.</b> At the time of writing no unit
/// running the custom firmware existed; this was built and tested against
/// <c>FakeWcpDevice</c>, which implements the contract, and nothing here has
/// spoken to real hardware.
/// </para>
/// </summary>
public static class Wcp
{
    /// <summary>The protocol version this host speaks, which <c>hello</c> must report as <c>wcp=1</c>.</summary>
    public const int Version = 1;

    /// <summary>
    /// The longest request the firmware accepts, in bytes, before it answers
    /// <c>ERR TOOLONG</c>. Checked here as well so an over-long request is refused
    /// before it is sent rather than after a round trip.
    /// </summary>
    public const int MaxRequestBytes = 512;

    /// <summary>
    /// The longest a data line's text is, before its CRC. The firmware splits
    /// anything longer across lines; recorded here because the fake has to do
    /// the same, and because base64 is chunked to fit it.
    /// </summary>
    public const int MaxDataText = 200;

    /// <summary>
    /// CRC-8/SMBUS: polynomial 0x07, initial value 0, no reflection, no final XOR.
    ///
    /// The plainest CRC-8 there is, chosen because the firmware side computes it
    /// byte by byte in a few lines of C with no table. Its check value — the CRC
    /// of the ASCII string "123456789" — is 0xF4.
    /// </summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;

        foreach (byte b in data)
        {
            crc ^= b;

            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }

        return crc;
    }

    /// <summary>The same over a line's text, which is ASCII on the wire.</summary>
    public static byte Crc8(string text) => Crc8(Encoding.ASCII.GetBytes(text));

    /// <summary>
    /// A line with its CRC appended: <c>body*HH</c>, two uppercase hex digits and
    /// no terminator. The terminator is the transport's business.
    /// </summary>
    public static string Frame(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return $"{body}*{Crc8(body):X2}";
    }

    /// <summary>
    /// A request, framed: <c>#&lt;seq&gt; &lt;command&gt;*HH</c>.
    ///
    /// Refuses a command carrying a line break, because a CR inside one ends the
    /// line on the firmware side and the rest arrives as a second line — which,
    /// not beginning with <c>#</c>, goes to the ELM327 chip as a command.
    /// </summary>
    public static string Request(int seq, string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(seq, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seq, 65535);

        if (command.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException("A WCP command cannot contain a line break.", nameof(command));

        if (command.Any(c => c > 0x7E || (c < 0x20 && c != '\t')))
            throw new ArgumentException("A WCP command is printable ASCII only.", nameof(command));

        string line = Frame($"#{seq} {command.Trim()}");

        if (line.Length + 1 > MaxRequestBytes)
            throw new ArgumentException(
                $"That request is {line.Length + 1} bytes; the firmware takes at most {MaxRequestBytes}.",
                nameof(command));

        return line;
    }

    /// <summary>
    /// Splits <c>body*HH</c> into its body and checks the CRC.
    ///
    /// <para>
    /// The CRC is the text after the <em>last</em> star, not the first. The
    /// contract says "every byte before the <c>*</c>", and a masked password is
    /// <c>****</c>: <c>#3:sta_pass=*****2B</c> carries five stars, and splitting
    /// on the first one checks a CRC over <c>#3:sta_pass=</c> and fails a line that
    /// was perfectly good. The firmware has to do the same with a request such as
    /// <c>config set note=a*b</c>.
    /// </para>
    /// </summary>
    /// <returns>
    /// False when the line has no well-formed <c>*HH</c> tail at all — which
    /// means it is not a WCP line, as distinct from a WCP line that arrived damaged.
    /// </returns>
    public static bool TryUnframe(string line, out string body, out bool crcOk)
    {
        body = "";
        crcOk = false;

        if (line is null) return false;

        int star = line.LastIndexOf('*');
        if (star < 0 || star != line.Length - 3) return false;

        if (!byte.TryParse(line.AsSpan(star + 1, 2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out byte stated))
            return false;

        body = line[..star];
        crcOk = Crc8(body) == stated;
        return true;
    }
}

/// <summary>What one WCP line from the device is.</summary>
public enum WcpLineKind
{
    /// <summary><c>#&lt;seq&gt;:&lt;text&gt;</c> — part of a reply, zero or more before its final line.</summary>
    Data,

    /// <summary><c>#&lt;seq&gt;.OK [summary]</c> — the request succeeded and its reply is complete.</summary>
    Ok,

    /// <summary><c>#&lt;seq&gt;.ERR &lt;CODE&gt; &lt;message&gt;</c> — it failed, and the reply is complete.</summary>
    Error,

    /// <summary><c>!&lt;TYPE&gt; &lt;fields&gt;</c> — something the device says without being asked.</summary>
    Event,

    /// <summary>
    /// <c>#&lt;seq&gt; &lt;command&gt;</c> — a request, which a host should never
    /// receive from the firmware. It does receive one from a <em>stock</em> unit:
    /// stock firmware passes the line to the ELM327 chip, and the chip echoes it.
    /// Recognised so it can be ignored rather than mistaken for a reply.
    /// </summary>
    Request,
}

/// <summary>
/// One WCP line from the device, parsed and checked.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="Seq">
/// The request it answers, for a reply. Zero for an event, and zero on the one
/// reply that cannot know: <c>#0.ERR CRC</c> to a request whose seq did not survive.
/// </param>
/// <param name="Text">
/// The data line's text; the OK summary; the error's message; or the event's
/// fields after its type.
/// </param>
/// <param name="Code">The error code (<c>CRC</c>, <c>DENIED</c>, …) or the event type (<c>CAN</c>, <c>DROP</c>, …).</param>
public sealed record WcpLine(WcpLineKind Kind, int Seq, string Text, string Code = "")
{
    /// <summary>
    /// Reads one line as WCP. False for a line that is not well-formed WCP, and
    /// for one whose CRC is wrong; <paramref name="damaged"/> tells them apart.
    /// </summary>
    /// <param name="line">The line without its terminator.</param>
    /// <param name="damaged">
    /// True when the line had a <c>*HH</c> tail but the CRC disagreed — a WCP line
    /// that lost or changed a byte on the way, which is worth counting. False when
    /// it simply was not WCP.
    /// </param>
    public static bool TryParse(string line, out WcpLine parsed, out bool damaged)
    {
        parsed = null!;
        damaged = false;

        if (string.IsNullOrEmpty(line) || line[0] is not ('#' or '!')) return false;

        if (!Wcp.TryUnframe(line, out string body, out bool crcOk)) return false;

        if (!crcOk)
        {
            damaged = true;
            return false;
        }

        if (body[0] == '!')
        {
            int space = body.IndexOf(' ');
            string type = space < 0 ? body[1..] : body[1..space];
            if (type.Length == 0) return false;

            parsed = new WcpLine(WcpLineKind.Event, 0, space < 0 ? "" : body[(space + 1)..], type);
            return true;
        }

        int digits = 1;
        while (digits < body.Length && char.IsAsciiDigit(body[digits])) digits++;

        if (digits == 1 || digits > 6
            || !int.TryParse(body.AsSpan(1, digits - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int seq)
            || seq > 65535)
            return false;

        string rest = body[digits..];

        if (rest.StartsWith(':'))
        {
            parsed = new WcpLine(WcpLineKind.Data, seq, rest[1..]);
            return true;
        }

        if (rest == ".OK" || rest.StartsWith(".OK ", StringComparison.Ordinal))
        {
            parsed = new WcpLine(WcpLineKind.Ok, seq, rest.Length > 4 ? rest[4..] : "");
            return true;
        }

        if (rest == ".ERR" || rest.StartsWith(".ERR ", StringComparison.Ordinal))
        {
            string after = rest.Length > 5 ? rest[5..] : "";
            int space = after.IndexOf(' ');
            string code = space < 0 ? after : after[..space];
            string message = space < 0 ? "" : after[(space + 1)..];

            parsed = new WcpLine(WcpLineKind.Error, seq, message, code.Length > 0 ? code : "INTERNAL");
            return true;
        }

        if (rest.StartsWith(' ') && seq > 0)
        {
            parsed = new WcpLine(WcpLineKind.Request, seq, rest[1..]);
            return true;
        }

        return false;
    }

    /// <summary>Whether this ends a reply.</summary>
    public bool IsFinal => Kind is WcpLineKind.Ok or WcpLineKind.Error;
}

/// <summary>
/// Hands out sequence numbers: 1 to 65535, then round again, never 0.
///
/// Zero is excluded because the firmware uses it for "I could not read your
/// number" (<c>#0.ERR CRC</c>), so a request numbered 0 could not be told apart
/// from that.
/// </summary>
public sealed class WcpSequence
{
    private readonly Lock _gate = new();
    private int _last;

    /// <param name="last">The number before the first one handed out, for a test that wants to see the wrap.</param>
    public WcpSequence(int last = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(last, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(last, 65535);

        _last = last;
    }

    public int Next()
    {
        lock (_gate)
        {
            _last = _last >= 65535 ? 1 : _last + 1;
            return _last;
        }
    }
}
