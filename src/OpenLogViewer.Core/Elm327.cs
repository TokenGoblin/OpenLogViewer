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
    /// One command at a time.
    ///
    /// Nothing needed this while the only traffic was the poll loop, which is a
    /// single thread asking one question after another. Fault scanning is asked
    /// for from the user interface while that loop is still running, and two
    /// commands written into the same adapter interleave into one stream: the
    /// scan reads the tail of a coolant temperature and the poll reads half a
    /// fault code. Both would parse. Neither would be true.
    /// </summary>
    private readonly Lock _gate = new();

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
    /// Silence, with an answer already in hand, that counts as the end of it.
    ///
    /// The prompt is supposed to be the only thing that means finished, and on a
    /// well-behaved adapter it is. A Vgate iCar Pro sends it on roughly 60–80 %
    /// of reads: measured against a live car, the rest ran out the whole window
    /// with a complete payload sitting in the buffer and nothing but the
    /// terminator missing. No window is long enough for a character that is not
    /// coming — lengthening one measured worse — so quiet has to be able to
    /// finish a reply too.
    ///
    /// **This must stay longer than the adapter's own response timeout.** An
    /// ELM327 waits `ATST` — about 205 ms by default — for the car before giving
    /// up and printing NO DATA, and a gap shorter than that fires while the
    /// adapter is still working. Ending a read early is not free: the datasheet's
    /// rule is to wait for the prompt, and a command sent before one arrives is
    /// answered "STOPPED", which ends the next read early in turn and sustains
    /// itself. The same mistake was made at 40 ms elsewhere and had to be raised.
    ///
    /// Nothing is lost by being generous, because this only ever runs on a read
    /// that would otherwise have waited out <see cref="Timeout"/> in full.
    /// </summary>
    public TimeSpan IdleGap { get; init; } = TimeSpan.FromMilliseconds(300);

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
    /// <param name="within">
    /// How long to allow, where the caller has reason to want something other
    /// than <see cref="ResetTimeout"/> — a second attempt after a first that
    /// heard nothing, which is asking a different question and deserves a
    /// shorter answer.
    /// </param>
    /// <returns>What the adapter calls itself, e.g. "ELM327 v1.5".</returns>
    public string Reset(TimeSpan? within = null)
    {
        string identity = Send("ATZ", within ?? ResetTimeout);

        // A reset undoes the protocol search along with everything else, and a
        // reconnection may land on a different car.
        _protocol = null;

        // NOTHING AT ALL came back — not a banner, not noise. Every ELM327
        // answers ATZ out of its own firmware without troubling the car, so this
        // is a link that has stopped existing rather than an adapter that is
        // slow, asleep, or set to the wrong speed. Configuring five options on
        // it costs a timeout apiece and changes nothing that is there.
        //
        // Noise is a different matter and still gets the full treatment: that is
        // what a wrong baud rate looks like, and the adapter is real.
        if (identity.Length == 0) return "";

        foreach (string command in Setup) Send(command, Timeout);

        return Clean(identity);
    }

    private static readonly string[] Setup = ["ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"];

    /// <summary>
    /// Asks the car something and throws the answer away.
    ///
    /// The first request after <c>ATSP0</c> is the one that triggers the protocol
    /// search, and the adapter narrates it: "SEARCHING..." arrives ahead of the
    /// data, seconds can pass, and the word is not inert — S, E, A, R, C and H
    /// all pass a hex parser as digits. Spending that on a request nobody reads
    /// makes every request after it an ordinary one, which matters more since a
    /// reply can now be finished by quiet: a pause in the middle of a protocol
    /// search would otherwise end a read on the word "SEARCHING" alone.
    /// </summary>
    public void WarmUp() => Send("0100", ResetTimeout);

    /// <summary>
    /// What the adapter actually is, which is not what it says it is.
    ///
    /// Every OBD2 adapter answers <c>ATI</c> with an ELM327 version, genuine or
    /// otherwise, because everything ever written for these expects one — an
    /// OBDLink r2.6 reports "ELM327 v1.3a", and the real ELM327 v1.3a it is
    /// claiming to be was superseded a decade before this device was built.
    ///
    /// The STN chips that OBDLink and a few others are built on answer two more
    /// questions that a clone does not: <c>STDI</c> with the product — "OBDLink
    /// r2.6" — and <c>STI</c> with the firmware, "STN1100 v2.2.2". So this both
    /// names the hardware properly and, by getting an answer at all, distinguishes
    /// a real one from a copy.
    ///
    /// Falls back to the ELM327 name, which is what an adapter without the
    /// extended command set has to offer. An unsupported command is answered with
    /// "?" rather than an error, so that is the thing to recognise.
    /// </summary>
    public string Identify()
    {
        string elm = Clean(Send("ATI", Timeout));

        string product = Extended("STDI");
        string firmware = Extended("STI");

        return (product.Length, firmware.Length, elm.Length) switch
        {
            ( > 0, > 0, _) => $"{product} ({firmware})",
            ( > 0, 0, _) => product,
            (0, > 0, > 0) => $"{elm} ({firmware})",
            (0, > 0, 0) => firmware,
            _ => elm,
        };
    }

    /// <summary>
    /// Which OBD2 protocol the adapter settled on, in its own words.
    ///
    /// "ISO 15765-4 (CAN 11/500)" on anything modern, and one of the older serial
    /// buses on a car from before CAN was mandated. Worth showing rather than
    /// hiding: it is the first thing that distinguishes a car whose replies will
    /// be short and fast from one whose will not.
    ///
    /// The "AUTO, " prefix the adapter adds after a protocol search is dropped —
    /// it says how the protocol was arrived at rather than what it is.
    /// </summary>
    public string ProtocolName()
    {
        string reply = Extended("ATDP");
        if (reply.Length == 0) return "";

        const string automatic = "AUTO,";

        return reply.StartsWith(automatic, StringComparison.OrdinalIgnoreCase)
            ? reply[automatic.Length..].Trim()
            : reply;
    }

    /// <summary>
    /// Whether the car is on CAN, which decides how a fault reply is laid out.
    ///
    /// Not a detail that can be skipped over. A mode 03 reply on CAN carries the
    /// number of codes immediately after the mode echo and on the older serial
    /// protocols it does not, so the same six bytes are one fault code or two
    /// depending on this answer — and the wrong reading produces codes that look
    /// perfectly plausible and are not on the car.
    ///
    /// <c>ATDPN</c> answers with the protocol number, prefixed "A" where it was
    /// found by searching: 1 to 5 are the J1850 and ISO serial buses, 6 upwards
    /// are the CAN variants. Assumed CAN when the adapter will not say, that being
    /// every vehicle built since the requirement came in.
    /// </summary>
    public bool IsCan() => ProtocolNumber() switch
    {
        >= FirstCanProtocol => true,
        >= 1 => false,

        // Unknown. Assumed CAN, that being every vehicle built since the
        // requirement came in — but only for reading a fault reply, where a
        // guess has to be made either way. Nothing that can simply not be done
        // may be gated on this; see <see cref="ProtocolNumber"/>.
        _ => true,
    };

    /// <summary>The lowest ELM327 protocol number that is a CAN variant.</summary>
    public const int FirstCanProtocol = 6;

    /// <summary>
    /// The protocol the adapter settled on, by number, or −1 where it will not
    /// say.
    ///
    /// Kept separate from <see cref="IsCan"/> because the two questions are not
    /// the same one, and treating them as the same has bitten. "Not slow" is not
    /// "is CAN": an unknown protocol is neither, and both answers must be no. An
    /// asleep ECU answering <c>STOPPED</c> parses as unknown, and a CAN-only
    /// request sent on the strength of that reaches a J1850 truck as a malformed
    /// one. So anything optional — batching, above all — is gated on a positive
    /// identification, and only the unavoidable guess falls back to CAN.
    ///
    /// <c>ATDPN</c> answers with the number, prefixed "A" where it was found by
    /// searching: 1 to 5 are the J1850 and ISO serial buses, 6 upwards are the
    /// CAN variants.
    /// </summary>
    public int ProtocolNumber()
    {
        if (_protocol is { } known) return known;

        string reply = Extended("ATDPN");

        // The trailing digit is the protocol; anything before it is the "A" that
        // says it was searched for rather than set.
        char number = reply.Length > 0 ? char.ToUpperInvariant(reply[^1]) : '\0';

        int protocol = number switch
        {
            >= '1' and <= '9' => number - '0',
            >= 'A' and <= 'C' => number - 'A' + 10,
            _ => -1,
        };

        // Protocol zero is "automatic, nothing decided yet" — the adapter has
        // been told to search and has not yet succeeded. That is not an answer
        // and must not be cached as one: a link that settles on ISO 9141 a moment
        // later would spend the rest of the session being read as CAN, which puts
        // a count byte where a fault code's first half is.
        if (protocol > 0) _protocol = protocol;

        return protocol;
    }

    private int? _protocol;

    /// <summary>
    /// One of the ST commands, or empty where the adapter has no such thing.
    ///
    /// An ELM327 answers an unknown command with "?", and a clone that never
    /// heard of these answers that or nothing at all. Either way there is no
    /// product name to be had, which is itself worth knowing.
    /// </summary>
    private string Extended(string command)
    {
        string reply = Send(command, Timeout);

        foreach (string line in reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string text = line.Trim();

            // The echo of the command comes back when echo is still on, and "?"
            // is how anything unrecognised is refused.
            if (text.Length == 0 || text == "?") continue;
            if (text.Equals(command, StringComparison.OrdinalIgnoreCase)) continue;
            if (text.Equals("OK", StringComparison.OrdinalIgnoreCase)) continue;

            return text;
        }

        return "";
    }

    /// <summary>
    /// Sends one command and returns everything up to the prompt.
    ///
    /// Line breaks are kept. An adapter puts each response on its own line and
    /// says "SEARCHING..." on a line of its own while it works out the protocol —
    /// and the letters in that word are mostly hex digits, so run together with
    /// the reply that follows it they decode as a different reading. Keeping the
    /// lines apart is what makes them separable.
    /// </summary>
    /// <param name="expectedBytes">
    /// How many bytes a well-formed answer to this carries, where that is known
    /// in advance — the echoed mode and parameter plus its data. Zero for
    /// anything whose length depends on the answer.
    ///
    /// This is what stops a late prompt costing the whole idle gap on every
    /// single read. Measured on a Vgate: the prompt trails the payload by about
    /// 210 ms, so a reply complete at 60 ms was not acted on until 260 — and
    /// with batching off, that gap <em>is</em> the poll cycle. Knowing the
    /// length turns the reply itself into the terminator.
    /// </param>
    public string Send(string command, TimeSpan timeout, bool settle = false, int expectedBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_gate)
        {
            // Anything still buffered belongs to the previous exchange — a reply
            // that arrived after its timeout, or the tail of one that was
            // abandoned. Read as the front of this answer it would decode as a
            // different reading altogether, which is worse than a slow one.
            _transport.DiscardInput();

            // Discarding is not enough after a read that timed out. A timeout
            // means the adapter was late, not absent, so its answer is still on
            // its way — and what has not arrived yet cannot be thrown away. It
            // lands in this read instead, and every reply stays one command
            // behind for the rest of the session.
            if (_resync) WaitForQuiet(TailWait);
            else if (settle) WaitForQuiet();

            _resync = false;

            _transport.Write(Encoding.ASCII.GetBytes(command + "\r"));

            return ReadToPrompt(command, timeout, expectedBytes);
        }
    }

    /// <summary>
    /// Set by a read that ended on its timeout, so the next one waits for the
    /// late answer to arrive and be thrown away before asking anything.
    /// </summary>
    private bool _resync;

    /// <summary>
    /// Waits until nothing more is arriving, throwing away whatever does.
    ///
    /// Discarding what is buffered is not enough on its own, because the tail of
    /// the previous reply may not have arrived yet to be discarded. The piece
    /// that matters is the "\r\r&gt;" on the end of it: still in flight when the
    /// next command goes out, it is read as <em>this</em> reply's prompt and cuts
    /// the answer off before the rest of it lands.
    ///
    /// That truncation is invisible on a single-module reply, which is complete
    /// by then anyway. It shows up on a car where several modules answer — the
    /// second one's line is the part that goes missing, and with it every
    /// parameter only that module supports. The same car reported 3, then 15,
    /// then 24 parameters on consecutive connections before this was here.
    ///
    /// Only worth its cost where the answer is not repeated. A poll that loses a
    /// sample gets another in half a second.
    /// </summary>
    /// <param name="firstByte">
    /// How long to wait for the late answer to <em>start</em> arriving, where
    /// there is reason to believe one is coming.
    ///
    /// Quiet alone does not cover the case a timeout leaves behind. The line is
    /// quiet at that moment precisely because the adapter has not answered yet —
    /// so a drain that gives up on the first silence proves nothing, and the
    /// answer lands in the next read regardless. Once anything arrives the
    /// ordinary quiet rule takes over.
    /// </param>
    private void WaitForQuiet(TimeSpan? firstByte = null)
    {
        TimeSpan wait = firstByte ?? QuietFor;

        // Bounded, because "wait for silence" is not a plan against something
        // that never goes silent. An adapter stuck repeating itself would
        // otherwise hold this loop for the life of the session, with nothing to
        // show for it: waiting is only worth doing while it can end.
        DateTime deadline = DateTime.UtcNow + DrainAtMost;

        while (DateTime.UtcNow < deadline && _transport.Read(_one, wait) == 1)
        {
            // Deliberately dropped: this is the previous exchange finishing.
            wait = QuietFor;
        }
    }

    /// <summary>Longest the line will be drained before giving up on it going quiet.</summary>
    public TimeSpan DrainAtMost { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Longest wait for an answer that missed its own read, before concluding
    /// that it really is not coming.
    ///
    /// Bounded, because an adapter that has genuinely gone silent must cost this
    /// once rather than on every command for the rest of the session. An answer
    /// later than this is not caught here at all — it is caught by the reply
    /// carrying the number of the parameter it answers, which is why a late one
    /// costs a reading rather than putting one channel's value on another's
    /// gauge.
    /// </summary>
    public TimeSpan TailWait { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long the line must be silent before it counts as settled.
    ///
    /// Longer than it needs to be for a tidy adapter, because the ones that need
    /// settling at all are the untidy ones: the tail of a Vgate's previous
    /// exchange has been measured arriving better than 80 ms behind the reply it
    /// belongs to. Draining costs this once per settled command, and only
    /// discovery and the recovery from a timeout pay it.
    /// </summary>
    public TimeSpan QuietFor { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Reads one reply: everything up to the prompt, or up to the point where
    /// the adapter plainly has nothing more to say.
    ///
    /// Three rules, and each of them was learnt from an adapter rather than from
    /// the datasheet:
    /// <list type="bullet">
    /// <item>A prompt always finishes a reply.</item>
    /// <item>Quiet finishes one too, but only with something in hand that is
    /// more than the command coming back. This adapter ignores <c>ATE0</c>,
    /// echoes, pauses, and only then sends the data — so an echo treated as an
    /// answer completes the read before the answer exists, and the answer then
    /// lands in the next command's window.</item>
    /// <item>Silence finishes nothing. A read that has received no answer waits
    /// out the whole timeout, because nothing arriving is not a short reply.</item>
    /// <item>And where the answer's length is known before it is asked for, the
    /// answer finishes itself — see <see cref="Complete"/>.</item>
    /// </list>
    /// </summary>
    private string ReadToPrompt(string command, TimeSpan timeout, int expectedBytes = 0)
    {
        var reply = new StringBuilder();
        DateTime deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            bool anything = HasPayload(reply);

            // One byte at a time, so this returns the moment the prompt arrives
            // rather than waiting out the timeout for a buffer that will never
            // fill. The transport blocks until at least one byte is there — for
            // the gap once an answer has started, and for the whole window while
            // there is still nothing to be quiet after.
            TimeSpan wait = anything && IdleGap > TimeSpan.Zero && IdleGap < remaining
                ? IdleGap
                : remaining;

            DateTime asked = DateTime.UtcNow;

            if (_transport.Read(_one, wait) != 1)
            {
                if (DateTime.UtcNow >= deadline) break;

                // Quiet. Finished, unless all that has arrived is the echo.
                if (anything && !IsOnlyEcho(reply, command)) return reply.ToString();

                // NOTHING, AND BACK EARLY. A read handed the whole remaining
                // window and returning empty before that window is up did not
                // wait at all: it has nothing left to wait for, which for a
                // socket is a far end that has closed. Going round again does
                // not sleep either — the next read comes back just as fast — so
                // it is a core spun flat out until the deadline, two seconds a
                // command and five on a reset, on exactly the failure these
                // adapters are known for.
                //
                // The same is true once an echo has arrived, and that case is
                // not caught by comparing the window: the wait is the idle gap
                // by then rather than the whole of what is left. So the test is
                // whether the read spent the time it was given. One that came
                // back in a fraction of it was not waiting on anything — a
                // closed socket returns instantly for ever — and going round
                // again only spins faster.
                if (wait == remaining) break;
                if (DateTime.UtcNow - asked < TimeSpan.FromTicks(wait.Ticks / 2)) break;

                continue;
            }

            char c = (char)_one[0];

            if (c == '>')
            {
                // A PROMPT WITH NOTHING BEFORE IT IS NOT AN ANSWER. It is the
                // one left over from the previous exchange, which finished on
                // the gap with its own prompt still in flight — so it could not
                // have been drained, because it had not been sent. Taken at face
                // value it completes this read with nothing in it and the real
                // answer arrives one command late, for ever. Reading on is safe:
                // the adapter sends another prompt after the real reply, and the
                // timeout still bounds the wait.
                if (!anything) continue;

                return reply.ToString();
            }

            reply.Append(c);

            // The whole answer is here, and nothing is owed but its terminator.
            if (expectedBytes > 0 && Complete(reply, command, expectedBytes)) return reply.ToString();
        }

        // Out of time. Whatever arrived is still worth returning — the caller
        // decides whether it parses — but the next command has to wait for the
        // late answer first.
        _resync = true;

        return reply.ToString();
    }

    /// <summary>
    /// Whether what has arrived is already a whole, well-formed answer of the
    /// length this request has to produce.
    ///
    /// The reply then terminates itself and the prompt becomes a formality —
    /// which is the difference between a read costing what the car took and a
    /// read costing the idle gap on top, on every single request.
    ///
    /// Three things have to hold, and each of them is what stops this firing
    /// where it must not:
    ///
    /// <list type="bullet">
    /// <item><b>It begins with 41.</b> A positive mode-01 answer does. "NO DATA"
    /// does not, and needs saying: it is six characters, exactly as long as a
    /// one-byte reply, so length alone would take it for one.</item>
    /// <item><b>It is nothing but hex.</b> A well-formed single-frame answer is.
    /// The moment anything else appears — the colon of a segment marker, a
    /// letter — this is not the reply the length was computed for, and counting
    /// on regardless would strike the length mid-reply and truncate it. That is
    /// not hypothetical: a second module's fragment ahead of a segmented answer
    /// does exactly that.</item>
    /// <item><b>The length is known at all.</b> Anything whose reply length
    /// depends on the answer passes zero and never comes here — a capability
    /// bitmap most of all, where a second module's answer arrives tens of
    /// milliseconds later and both are wanted.</item>
    /// </list>
    ///
    /// Every way of being wrong therefore ends in "not complete", which costs
    /// the idle gap and nothing else. None of them can end in a short read.
    /// </summary>
    private static bool Complete(StringBuilder reply, string command, int expectedBytes)
    {
        string text = reply.ToString().Trim();

        // Past the echo, which this adapter sends whatever it was told about
        // ATE0. Safe to step over because the caller knows what it asked.
        if (text.StartsWith(command, StringComparison.OrdinalIgnoreCase))
            text = text[command.Length..].Trim();

        int digits = 0;
        int first = -1;
        int second = -1;

        foreach (char c in text)
        {
            // Whether these are here at all is a setting the adapter may have
            // ignored, as it ignored the one about echo, so they say nothing
            // either way.
            if (char.IsWhiteSpace(c)) continue;

            if (Nibble(c) < 0) return false;

            if (first < 0) first = c;
            else if (second < 0) second = c;

            digits++;
        }

        return digits == expectedBytes * 2 && first == '4' && second == '1';
    }

    /// <summary>Whether anything but line endings and spaces has arrived.</summary>
    private static bool HasPayload(StringBuilder reply)
    {
        for (int i = 0; i < reply.Length; i++)
            if (!char.IsWhiteSpace(reply[i])) return true;

        return false;
    }

    /// <summary>
    /// Whether all that has come back is the command itself.
    ///
    /// Safe to strip precisely because the caller knows what it asked: an echo
    /// is the exact command and nothing else, so this cannot eat an answer.
    /// </summary>
    private static bool IsOnlyEcho(StringBuilder reply, string command)
    {
        if (command.Length == 0) return false;

        string text = reply.ToString().Trim();

        return text.Length == command.Length
               && text.Equals(command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asks for one mode-01 parameter and returns its data bytes.
    ///
    /// False when the car did not answer with that parameter — "NO DATA" for one
    /// it does not support, "SEARCHING..." before the protocol is settled, an
    /// error, or silence. All of those are ordinary and none is worth an
    /// exception; the caller decides what a run of them means.
    /// </summary>
    public bool TryRead(byte pid, int dataBytes, Span<byte> into, out int count, TimeSpan? within = null)
    {
        // The echoed mode and parameter, then its data — a length the standard
        // fixes, so the reply can end itself rather than waiting for a prompt
        // that may be 200 ms behind it.
        string reply = Send($"01{pid:X2}", within ?? Timeout, expectedBytes: 2 + dataBytes);

        return TryParse(reply, pid, dataBytes, into, out count);
    }

    /// <summary>
    /// Asks for one parameter and returns every module's answer.
    ///
    /// For a reading, the first answer will do — the modules are reporting the
    /// same number. For a capability mask they are not: each module reports what
    /// it alone supports, and taking one of them means the rest of the car goes
    /// unread. Which module answers first is not fixed, so this is also the
    /// difference between a connection that finds everything and one that finds
    /// almost nothing, on the same car a minute apart.
    /// </summary>
    public IReadOnlyList<byte[]> ReadAll(byte pid, int dataBytes, TimeSpan? within = null) =>
        ParseAll(Send($"01{pid:X2}", within ?? Timeout, settle: true), pid, dataBytes);

    /// <summary>One parameter's worth of a batched reply.</summary>
    public readonly record struct BatchAnswer(byte Pid, byte[] Data);

    /// <summary>
    /// Parameters one request may carry, which the standard puts at six.
    ///
    /// The cost of OBD2 is round trips rather than bytes, so this is the single
    /// biggest thing available to it: fifteen exchanges become three.
    /// </summary>
    public const int MaxBatchPids = 6;

    /// <summary>
    /// Asks for several parameters in one request.
    ///
    /// ISO 15765 only — a request like this reaches a J1850 vehicle as a
    /// malformed one, so the caller must have identified the bus positively
    /// rather than merely failed to identify it as slow.
    ///
    /// A parameter whose length this does not know is left out rather than
    /// asked for: a batched reply is a run of (parameter, data) with nothing to
    /// separate the groups, so one group of unknown length loses every group
    /// after it. Singly it does not matter, because there the length is the
    /// caller's to state.
    /// </summary>
    public IReadOnlyList<BatchAnswer> ReadMany(
        IReadOnlyList<byte> pids, Func<byte, int> lengthOf, TimeSpan? within = null)
    {
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(lengthOf);

        byte[] asked = [.. pids.Where(p => lengthOf(p) > 0).Distinct().Take(MaxBatchPids)];
        if (asked.Length < 2) return [];

        string command = "01" + string.Concat(asked.Select(p => p.ToString("X2")));

        // One 0x41 and then each parameter with its data. Only ever reached by a
        // reply that stayed in one frame, since a segmented one carries markers
        // and those stop the length being counted at all.
        int expected = 1 + asked.Sum(p => 1 + lengthOf(p));

        return ParseBatch(Send(command, within ?? Timeout, expectedBytes: expected), asked, lengthOf);
    }

    /// <summary>
    /// Pulls the parameters out of a batched reply.
    ///
    /// Harder than the single-parameter case in three ways, each of which has
    /// produced a real defect:
    ///
    /// <list type="bullet">
    /// <item>The reply is multi-frame, and an adapter prints those as a length
    /// header and segment-marked lines — "0:", "1:". Those markers are valid hex
    /// digits, so anything that strips non-hex characters and pairs the rest
    /// turns the "0" of "0:" into half a byte and shifts everything after it by
    /// a nibble. Each line, and each piece of a line, is therefore converted on
    /// its own, and a trailing half-byte is dropped with it — which is also what
    /// makes a three-digit length header harmless.</item>
    /// <item>There is nothing between the groups. A group is a parameter number
    /// and however many bytes that parameter is defined to carry, so the walk is
    /// only in step for as long as every number is recognised; one that is not
    /// stops it rather than guessing a length.</item>
    /// <item>More than one module answers, and the reply can be two responses
    /// run together. Anchoring on the first plausible start is not enough — on a
    /// live car a leading fragment came first, and taking it yielded one
    /// parameter of four, which reads as "batching does not work here" and costs
    /// the whole feature. So every candidate start is scored by how much of the
    /// reply it explains and the best one wins, earliest on a tie.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<BatchAnswer> ParseBatch(
        string reply, IReadOnlyList<byte> asked, Func<byte, int> lengthOf)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(lengthOf);

        byte[] bytes = Flatten(reply);
        IReadOnlyList<BatchAnswer> best = [];

        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            // The positive answer, followed by something that was actually asked
            // for. A lone 0x41 also occurs inside data, and any length header
            // sits in front of the real one.
            if (bytes[i] != 0x41 || !asked.Contains(bytes[i + 1])) continue;

            IReadOnlyList<BatchAnswer> walked = Walk(bytes, i + 1, asked, lengthOf);

            if (walked.Count > best.Count) best = walked;
            if (best.Count == asked.Count) break;
        }

        return best;
    }

    /// <summary>
    /// The hex of a reply, converted line by line and piece by piece so that a
    /// segment marker cannot shift the bytes after it.
    /// </summary>
    private static byte[] Flatten(string reply)
    {
        var bytes = new List<byte>(64);
        Span<byte> piece = stackalloc byte[64];

        foreach (string line in reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Split on the marker's colon rather than filtering it out
            // afterwards: by then its digit has already been paired with the
            // byte in front of it.
            foreach (string part in line.Split(':'))
            {
                int got = Unhex(part, piece);
                for (int i = 0; i < got; i++) bytes.Add(piece[i]);
            }
        }

        return [.. bytes];
    }

    /// <summary>
    /// Reads (parameter, data) groups from one starting point until they stop
    /// making sense.
    /// </summary>
    private static IReadOnlyList<BatchAnswer> Walk(
        byte[] bytes, int start, IReadOnlyList<byte> asked, Func<byte, int> lengthOf)
    {
        var found = new List<BatchAnswer>(MaxBatchPids);

        for (int at = start; at < bytes.Length && found.Count < asked.Count;)
        {
            byte pid = bytes[at];

            // Something nobody asked for: the padding at the end of a frame, or
            // a second module's answer beginning. Either way the groups have run
            // out, and reading on would be reading a length out of somebody
            // else's data.
            if (!asked.Contains(pid)) break;

            // Repeating itself, which is the same thing said differently.
            if (found.Any(a => a.Pid == pid)) break;

            int length = lengthOf(pid);
            if (length <= 0 || at + 1 + length > bytes.Length) break;

            found.Add(new BatchAnswer(pid, bytes[(at + 1)..(at + 1 + length)]));
            at += 1 + length;
        }

        return found;
    }

    /// <summary>Every complete answer to one request, one per responding module.</summary>
    public static IReadOnlyList<byte[]> ParseAll(string reply, byte pid, int dataBytes)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var answers = new List<byte[]>();

        foreach (string line in reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var data = new byte[dataBytes];

            if (TryParse(line, pid, dataBytes, data, out _)) answers.Add(data);
        }

        return answers;
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
