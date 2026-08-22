using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// An ELM327 adapter and a car behind it, in software.
///
/// Faithful to the awkward parts rather than to a tidy idea of the protocol:
/// the adapter echoes commands until it is told not to, inserts spaces until it
/// is told not to, announces "SEARCHING..." on its own line before the first
/// reply, says "NO DATA" for a parameter this car does not have, and ends every
/// exchange with a "&gt;" prompt. Each of those has broken a real client.
/// </summary>
internal sealed class FakeElm : IEcuTransport
{
    private readonly Queue<byte> _out = new();
    private readonly StringBuilder _in = new();

    /// <summary>Data bytes this car answers with, by PID.</summary>
    public Dictionary<byte, byte[]> Answers { get; } = [];

    /// <summary>
    /// Further modules answering the same request, sent ahead of the main one.
    ///
    /// Real cars have several, each replying on its own line, and the order they
    /// arrive in is not fixed — which is exactly what broke a connection that had
    /// worked a minute earlier.
    /// </summary>
    public Dictionary<byte, List<byte[]>> ExtraAnswers { get; } = [];

    /// <summary>Confirmed faults this car will report to mode 03.</summary>
    public List<string> StoredCodes { get; } = [];

    /// <summary>Faults seen once, for mode 07.</summary>
    public List<string> PendingCodes { get; } = [];

    /// <summary>Faults mode 04 is not allowed to erase, for mode 0A.</summary>
    public List<string> PermanentCodes { get; } = [];

    /// <summary>
    /// Which OBD2 protocol this car is on, as ATDPN reports it.
    ///
    /// Six is CAN at 500 kbaud, which is every car built to the requirement; 1 to
    /// 5 are the older serial buses, whose fault replies are laid out differently
    /// and carry no count byte. The difference is not cosmetic — the same bytes
    /// decode to different codes — so the fake has to be able to be either.
    /// </summary>
    public int ProtocolNumber { get; set; } = 6;

    private bool Can => ProtocolNumber >= 6;

    /// <summary>Refuse mode 04, as most cars do with the engine running.</summary>
    public bool RefuseClear { get; init; }

    /// <summary>How many times mode 04 has been asked for.</summary>
    public int ClearRequests { get; private set; }

    /// <summary>What ATZ and ATI report, which every adapter answers with.</summary>
    public string Elm327Name { get; init; } = "ELM327 v1.5";

    /// <summary>
    /// What STDI reports, where the adapter has an STN chip in it — "OBDLink
    /// r2.6". Empty for the clones, which refuse the command.
    /// </summary>
    public string Product { get; init; } = "";

    /// <summary>What STI reports on the same devices — "STN1100 v2.2.2".</summary>
    public string Firmware { get; init; } = "";

    public bool Echo { get; private set; } = true;

    /// <summary>
    /// Answer ATE0 with OK and go on echoing anyway.
    ///
    /// Not a fault being invented: a Vgate iCar Pro does this. The command is
    /// acknowledged and ignored, so every reply arrives with the command that
    /// caused it on the front — "ATDPN" then "A6", "0100" then "4100BE3FA813" —
    /// and a client that runs the two together reads the protocol number as
    /// nonsense and every capability bitmap as a car supporting nothing, minutes
    /// after it was reporting rpm.
    /// </summary>
    public bool StickyEcho { get; init; }

    public bool Spaces { get; private set; } = true;

    /// <summary>Said once, before the first mode-01 reply, as a real one does.</summary>
    public bool Searching { get; set; } = true;

    /// <summary>
    /// Answer everything with rubbish, as an adapter does when the port is open
    /// at a speed it is not listening at. Note that it answers rather than falls
    /// silent — that is what makes a wrong baud rate hard to recognise.
    /// </summary>
    public bool Garble { get; init; }

    public List<string> Received { get; } = [];

    public bool IsOpen { get; private set; }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose() => Close();

    public void DiscardInput() => _out.Clear();

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b != (byte)'\r')
            {
                _in.Append((char)b);
                continue;
            }

            Answer(_in.ToString().Trim());
            _in.Clear();
        }
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int count = 0;
        while (count < buffer.Length && _out.Count > 0) buffer[count++] = _out.Dequeue();

        return count;
    }

    private void Answer(string command)
    {
        Received.Add(command);

        if (Garble)
        {
            // Framing errors at the wrong speed turn the reply into whatever the
            // bits happen to land on. Deliberately hex-ish: rubbish that parses
            // as nothing would be too easy to reject.
            Say("þÃADF3°À");
            Prompt();
            return;
        }

        if (Echo) Say(command);

        switch (command.ToUpperInvariant())
        {
            case "ATZ": Echo = true; Spaces = true; Say(Elm327Name); break;
            case "ATI": Say(Elm327Name); break;
            case "ATE0": Echo = StickyEcho; Say("OK"); break;
            case "ATS0": Spaces = false; Say("OK"); break;

            // Both forms of "which protocol did you settle on". The "A" says it
            // was found by searching rather than set, and it is on the front of
            // the number rather than instead of it.
            case "ATDP": Say($"AUTO, {ProtocolDescription}"); break;
            case "ATDPN": Say($"A{ProtocolNumber:X}"); break;

            // The ST commands, which only the STN chips behind an OBDLink have.
            // A plain ELM327 and every clone answer "?" — which is the thing that
            // tells one from the other, so the fake has to refuse them properly
            // rather than not recognise them at all.
            case "STDI": Say(Product.Length > 0 ? Product : "?"); break;
            case "STI": Say(Firmware.Length > 0 ? Firmware : "?"); break;
            case var st when st.StartsWith("ST", StringComparison.Ordinal): Say("?"); break;

            case var at when at.StartsWith("AT", StringComparison.Ordinal): Say("OK"); break;

            case "03": Faults(StoredCodes, 0x43); break;
            case "07": Faults(PendingCodes, 0x47); break;
            case "0A": Faults(PermanentCodes, 0x4A); break;
            case "04": Erase(); break;

            default: Mode01(command); break;
        }

        Prompt();
    }

    private string ProtocolDescription => ProtocolNumber switch
    {
        1 => "SAE J1850 PWM",
        2 => "SAE J1850 VPW",
        3 => "ISO 9141-2",
        4 => "ISO 14230-4 KWP (5 baud init)",
        5 => "ISO 14230-4 KWP (fast init)",
        7 => "ISO 15765-4 (CAN 29/500)",
        _ => "ISO 15765-4 (CAN 11/500)",
    };

    /// <summary>
    /// Erases what the standard allows, which is not everything.
    ///
    /// Permanent codes survive on purpose — that is the whole reason the mode
    /// exists — and a fake that wiped them would let a bug through that only a
    /// car built after 2010 could catch.
    /// </summary>
    private void Erase()
    {
        ClearRequests++;

        if (RefuseClear)
        {
            // A negative response: mode, then the code for conditions not correct.
            Say(Hex([0x7F, 0x04, 0x22]));
            return;
        }

        StoredCodes.Clear();
        PendingCodes.Clear();

        Say(Hex([0x44]));
    }

    /// <summary>
    /// Answers one of the fault modes the way the real protocols do.
    ///
    /// The three shapes a client has to cope with, chosen the way a real link
    /// chooses them rather than by a flag:
    ///
    /// <list type="bullet">
    /// <item>CAN, short enough to fit — one line, with a count byte.</item>
    /// <item>CAN, too long — a total length, then fragments "0:", "1:" and so on,
    /// six data bytes in the first and seven in the rest.</item>
    /// <item>Anything older — one line per three codes, no count, zero-padded.</item>
    /// </list>
    /// </summary>
    private void Faults(List<string> codes, byte echoed)
    {
        if (Searching)
        {
            Say("SEARCHING...");
            Searching = false;
        }

        byte[] pairs = [.. codes.SelectMany(c =>
        {
            (byte high, byte low) = Obd2Faults.Encode(c);
            return new[] { high, low };
        })];

        if (!Can)
        {
            // Three codes to a message, padded out to a full six data bytes —
            // and no count anywhere, which is the whole difficulty.
            if (pairs.Length == 0)
            {
                Say("NO DATA");
                return;
            }

            for (int at = 0; at < pairs.Length; at += 6)
            {
                byte[] message = new byte[7];
                message[0] = echoed;
                pairs.AsSpan(at, Math.Min(6, pairs.Length - at)).CopyTo(message.AsSpan(1));

                Say(Hex(message));
            }

            return;
        }

        byte[] payload = [echoed, (byte)codes.Count, .. pairs];

        // Seven bytes is what one CAN frame holds. Anything more is ISO-TP, and
        // an adapter with headers off prints it as a length and numbered pieces.
        if (payload.Length <= 7)
        {
            Say(Hex(payload));
            return;
        }

        Say(payload.Length.ToString("X3"));

        // Six data bytes in the first frame — the other two are the length, which
        // has already been printed on its own line — and seven in each one after.
        int taken = Math.Min(6, payload.Length);
        Say($"0:{Hex(payload[..taken])}");

        for (int frame = 1; taken < payload.Length; frame++)
        {
            byte[] piece = new byte[7];
            int size = Math.Min(7, payload.Length - taken);
            payload.AsSpan(taken, size).CopyTo(piece);

            Say($"{frame:X}:{Hex(piece)}");
            taken += size;
        }
    }

    /// <summary>
    /// How this car answers a request carrying more than one parameter.
    ///
    /// ISO 15765 allows six at a time and turns fifteen round trips into three,
    /// but not every car and not every adapter obliges. <see cref="First"/> is
    /// the one worth having a name for: a car that ignores the extra parameters
    /// and answers the first as though it had been asked on its own, which is
    /// exactly what a batched reply carrying one looks like.
    ///
    /// Settable rather than fixed, because an adapter that batched at the probe
    /// and stopped afterwards is a case the client has to survive.
    /// </summary>
    public enum BatchReply
    {
        /// <summary>NO DATA, as a car that does not understand the request.</summary>
        Refuse,

        /// <summary>Only the first parameter listed, in the ordinary shape.</summary>
        First,

        /// <summary>Every parameter it has, as one run of groups.</summary>
        All,
    }

    /// <summary>What this car does with a request for several parameters.</summary>
    public BatchReply Batches { get; set; } = BatchReply.Refuse;

    /// <summary>
    /// Parameters left out of a batched reply, which this car still answers for
    /// when it is asked about them on their own.
    ///
    /// A different thing from a parameter the car does not have, which is absent
    /// from both. This is an answer that went astray on the way back — a
    /// segmented reply that lost its tail, or a second module's line arriving
    /// after the window — and the sensor is there and reading the whole time.
    /// </summary>
    public HashSet<byte> OmitFromBatch { get; } = [];

    private void Mode01(string command)
    {
        if (command.Length < 4
            || command.Length % 2 != 0
            || !command.StartsWith("01", StringComparison.OrdinalIgnoreCase))
        {
            Say("?");
            return;
        }

        var pids = new List<byte>();

        for (int at = 2; at < command.Length; at += 2)
        {
            if (!byte.TryParse(
                    command.AsSpan(at, 2), System.Globalization.NumberStyles.HexNumber, null, out byte one))
            {
                Say("?");
                return;
            }

            pids.Add(one);
        }

        if (Searching)
        {
            Say("SEARCHING...");
            Searching = false;
        }

        if (pids.Count > 1)
        {
            AnswerBatch(pids);
            return;
        }

        byte pid = pids[0];

        // Ahead of the real one, so anything that takes the first answer takes
        // the wrong one.
        if (ExtraAnswers.TryGetValue(pid, out List<byte[]>? others))
            foreach (byte[] other in others) Say(Hex([0x41, pid, .. other]));

        if (!Answers.TryGetValue(pid, out byte[]? data))
        {
            if (others is not { Count: > 0 }) Say("NO DATA");
            return;
        }

        Say(Hex([0x41, pid, .. data]));
    }

    /// <summary>
    /// Answers a request that carried several parameters.
    ///
    /// One 0x41 and then a run of (parameter, data) with nothing between the
    /// groups — which is what makes the reply only readable by somebody who
    /// knows how long each parameter is. Parameters this car does not have are
    /// simply absent, exactly as a single request would be answered NO DATA.
    /// </summary>
    private void AnswerBatch(List<byte> pids)
    {
        if (Batches == BatchReply.Refuse)
        {
            Say("NO DATA");
            return;
        }

        var payload = new List<byte> { 0x41 };

        foreach (byte pid in Batches == BatchReply.First ? pids.Take(1) : pids)
        {
            if (OmitFromBatch.Contains(pid)) continue;
            if (!Answers.TryGetValue(pid, out byte[]? data)) continue;

            payload.Add(pid);
            payload.AddRange(data);
        }

        if (payload.Count == 1)
        {
            Say("NO DATA");
            return;
        }

        SayFrames([.. payload]);
    }

    /// <summary>
    /// Prints a payload the way an adapter with headers off does: one line while
    /// it fits in a CAN frame, and beyond that a total length followed by
    /// numbered segments — "0:" carrying six bytes and each one after it seven.
    ///
    /// The segment markers are the point. Their digits are valid hex, so a
    /// reader that strips non-hex characters and pairs what is left shifts every
    /// byte after the first marker by half a byte.
    /// </summary>
    private void SayFrames(byte[] payload)
    {
        if (payload.Length <= 7)
        {
            Say(Hex(payload));
            return;
        }

        Say(payload.Length.ToString("X3"));

        int taken = Math.Min(6, payload.Length);
        Say($"0:{Hex(payload[..taken])}");

        for (int frame = 1; taken < payload.Length; frame++)
        {
            int size = Math.Min(7, payload.Length - taken);

            Say($"{frame:X}:{Hex(payload[taken..(taken + size)])}");
            taken += size;
        }
    }

    private string Hex(byte[] bytes)
    {
        IEnumerable<string> pairs = bytes.Select(b => b.ToString("X2"));
        return Spaces ? string.Join(' ', pairs) : string.Concat(pairs);
    }

    private void Say(string line)
    {
        foreach (char c in line) _out.Enqueue((byte)c);
        _out.Enqueue((byte)'\r');
    }

    private void Prompt()
    {
        _out.Enqueue((byte)'\r');
        _out.Enqueue((byte)'>');
    }
}
