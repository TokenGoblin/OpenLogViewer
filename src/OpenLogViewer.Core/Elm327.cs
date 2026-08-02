using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Talking to an ELM327 OBD2 adapter.
///
/// Unlike every other link here this one is text. A command is ASCII terminated
/// by a carriage return, a reply is ASCII hex, and the adapter signals that it
/// has finished and is ready for the next one by sending "&gt;". That prompt is the
/// only reliable end-of-reply marker — replies are not fixed length, "SEARCHING…"
/// can appear before the data, and a multi-line reply from a car with several
/// responding modules has no other terminator.
///
/// The AT commands are the adapter's own; anything else is passed to the car.
/// </summary>
public sealed class Elm327(IEcuTransport transport)
{
    private readonly IEcuTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));

    private readonly byte[] _one = new byte[1];

    /// <summary>
    /// How long to wait for the adapter to finish answering.
    ///
    /// Generous for a link this slow. A clone answers a known PID in 40–100 ms,
    /// but the first request after a reset can take seconds while it works out
    /// which of nine OBD2 protocols the car speaks.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>What a reset is given, which is longer than anything else.</summary>
    public TimeSpan ResetTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resets the adapter and puts it into the only mode this can parse.
    ///
    /// Order matters, and none of it is optional:
    /// <list type="bullet">
    /// <item><c>ATZ</c> — full reset, so a previous session's settings are gone.</item>
    /// <item><c>ATE0</c> — echo off. Left on, every reply is preceded by the
    /// command that caused it, which parses as leading hex.</item>
    /// <item><c>ATL0</c>, <c>ATS0</c> — no linefeeds, no spaces, so a reply is
    /// one run of hex digits.</item>
    /// <item><c>ATH0</c> — no headers, so the reply is the data and not the CAN
    /// addressing around it.</item>
    /// <item><c>ATSP0</c> — try each OBD2 protocol until one answers, rather than
    /// requiring the user to know which of them their car speaks.</item>
    /// </list>
    /// </summary>
    /// <returns>What the adapter calls itself, e.g. "ELM327 v1.5".</returns>
    public string Reset()
    {
        string identity = Send("ATZ", ResetTimeout);

        foreach (string command in Setup) Send(command, Timeout);

        return Clean(identity);
    }

    private static readonly string[] Setup = ["ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"];

    /// <summary>
    /// Sends one command and returns everything up to the prompt.
    ///
    /// Line breaks are kept. An adapter puts each response on its own line and
    /// says "SEARCHING..." on a line of its own while it works out the protocol —
    /// and the letters in that word are mostly hex digits, so run together with
    /// the reply that follows it they decode as a different reading. Keeping the
    /// lines apart is what makes them separable.
    /// </summary>
    public string Send(string command, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Anything still buffered belongs to the previous exchange — a reply that
        // arrived after its timeout, or the tail of one that was abandoned. Read
        // as the front of this answer it would decode as a different reading
        // altogether, which is worse than a slow one.
        _transport.DiscardInput();

        _transport.Write(Encoding.ASCII.GetBytes(command + "\r"));

        return ReadToPrompt(timeout);
    }

    private string ReadToPrompt(TimeSpan timeout)
    {
        var reply = new StringBuilder();
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            // One byte at a time, so this returns the moment the prompt arrives
            // rather than waiting out the timeout for a buffer that will never
            // fill. The transport blocks until at least one byte is there.
            if (_transport.Read(_one, remaining) != 1) break;

            char c = (char)_one[0];
            if (c == '>') break;

            reply.Append(c);
        }

        return reply.ToString();
    }

    /// <summary>
    /// Asks for one mode-01 parameter and returns its data bytes.
    ///
    /// False when the car did not answer with that parameter — "NO DATA" for one
    /// it does not support, "SEARCHING..." before the protocol is settled, an
    /// error, or silence. All of those are ordinary and none is worth an
    /// exception; the caller decides what a run of them means.
    /// </summary>
    public bool TryRead(byte pid, int dataBytes, Span<byte> into, out int count)
    {
        string reply = Send($"01{pid:X2}", Timeout);

        return TryParse(reply, pid, dataBytes, into, out count);
    }

    /// <summary>
    /// Pulls the data bytes out of a mode-01 reply.
    ///
    /// A positive answer echoes the mode with bit 6 set — 0x41 — then the PID,
    /// then the data. Both are checked rather than assumed: a reply that arrives
    /// late is answering the previous question, and taking it at face value would
    /// put one channel's number on another channel's gauge.
    ///
    /// Each line is tried on its own. A car with more than one responding module
    /// answers a single request several times over, one response per line, and
    /// the first complete one is as good as any; "SEARCHING..." and "NO DATA"
    /// simply fail to match and are passed over.
    /// </summary>
    public static bool TryParse(
        string reply, byte pid, int dataBytes, Span<byte> into, out int count)
    {
        ArgumentNullException.ThrowIfNull(reply);

        count = 0;

        foreach (string line in reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Span<byte> bytes = stackalloc byte[32];
            int got = Unhex(line, bytes);

            // The echoed mode and PID, plus every data byte that was asked for.
            if (got < 2 + dataBytes || bytes[0] != 0x41 || bytes[1] != pid) continue;
            if (dataBytes > into.Length) return false;

            bytes.Slice(2, dataBytes).CopyTo(into);
            count = dataBytes;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a run of hex digits into bytes, ignoring anything else.
    ///
    /// Tolerant on purpose. Adapters differ over whether they send "41 0C 1A F8"
    /// or "410C1AF8", clones prepend "SEARCHING..." unbidden, and a stray
    /// character on a Bluetooth link should cost a reading rather than a session.
    /// A trailing half-byte is dropped, being a reply that was cut short.
    /// </summary>
    public static int Unhex(string text, Span<byte> into)
    {
        ArgumentNullException.ThrowIfNull(text);

        int count = 0;
        int high = -1;

        foreach (char c in text)
        {
            int nibble = Nibble(c);
            if (nibble < 0) continue;

            if (high < 0)
            {
                high = nibble;
                continue;
            }

            if (count == into.Length) break;

            into[count++] = (byte)((high << 4) | nibble);
            high = -1;
        }

        return count;
    }

    private static int Nibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    /// <summary>
    /// What the adapter called itself, or empty if nothing recognisable did.
    ///
    /// A reset is answered while echo is still on, so the command comes back
    /// first and the version follows it — "ATZ\rELM327 v1.5". The version is
    /// found by looking for the name rather than by taking the last line, since
    /// clones pad the reply differently.
    ///
    /// Empty matters as much as the name does. It is how a wrong baud rate is
    /// recognised: an adapter being sent noise does not fall silent, it answers
    /// with noise, and the one thing that noise reliably is not is its own name.
    /// Every OBD2 adapter worth the name reports as an ELM327 — the better chips
    /// claim it too, for compatibility with everything already written.
    /// </summary>
    private static string Clean(string identity)
    {
        int at = identity.IndexOf("ELM", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";

        // To the end of that line: what follows is the prompt or the next reply.
        string text = identity[at..];
        int end = text.IndexOfAny(['\r', '\n']);

        return (end >= 0 ? text[..end] : text).Trim();
    }
}
