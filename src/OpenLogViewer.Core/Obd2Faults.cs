namespace OpenLogViewer.Core;

/// <summary>
/// Which of the three lists a code was found in.
///
/// The standard keeps them apart for a reason and they mean quite different
/// things about the car, so they are not merged here either.
/// </summary>
public enum DtcState
{
    /// <summary>
    /// Confirmed — the fault happened often enough for the car to commit to it,
    /// and on an emissions monitor this is what lights the lamp.
    /// </summary>
    Stored,

    /// <summary>
    /// Seen once and not yet confirmed. Most monitors want the fault on two
    /// consecutive drive cycles before they will store it, so a pending code is
    /// either the beginning of a real fault or a one-off that will clear itself.
    /// </summary>
    Pending,

    /// <summary>
    /// Stored and not clearable. These were added so that a car could not be
    /// presented for an emissions test with its faults wiped minutes earlier: only
    /// the controller may erase one, and only after it has watched the monitor
    /// pass on its own.
    /// </summary>
    Permanent,
}

/// <summary>One fault code, and which list it came from.</summary>
public readonly record struct Dtc(string Code, DtcState State)
{
    /// <summary>The standard's meaning, or empty where there is none to give.</summary>
    public string Description => Obd2Codes.Describe(Code);

    public DtcAuthority Authority => Obd2Codes.Authority(Code);

    /// <summary>Powertrain, Chassis, Body or Network.</summary>
    public string System => Obd2Codes.System(Code);

    /// <summary>
    /// What can honestly be said about this code, which for a manufacturer's own
    /// number is that nobody outside the marque can read it.
    ///
    /// Deliberately not a guess. The maker owns those ranges and assigns them
    /// freely, so the same five characters mean unrelated things on two different
    /// cars — and a plausible-sounding description of the wrong one is how a
    /// person ends up buying a sensor they did not need.
    /// </summary>
    public string Detail => Description.Length > 0
        ? Description
        : Authority == DtcAuthority.Manufacturer
            ? "Defined by the vehicle manufacturer — the standard does not assign this number, "
              + "so it has to be looked up against the marque."
            : "In the standard's range, but not in the list carried here. Worth looking up.";

    public override string ToString() => Description.Length > 0 ? $"{Code}  {Description}" : Code;
}

/// <summary>
/// What the car reported when it was asked about its faults.
/// </summary>
/// <param name="Faults">Every code found, across all three lists.</param>
/// <param name="MilOn">Whether the car says the lamp is commanded on.</param>
/// <param name="ReportedCount">
/// How many confirmed faults the car counts, from PID 01. Worth keeping beside
/// the codes rather than deriving from them: a difference between the two means
/// one module answered the count and another did not answer mode 03, which is
/// the difference between "no faults" and "not everything was asked".
/// </param>
/// <param name="Protocol">What the adapter negotiated, in its own words.</param>
/// <param name="Trouble">What went wrong, where anything did.</param>
public sealed record FaultScan(
    IReadOnlyList<Dtc> Faults,
    bool MilOn,
    int ReportedCount,
    string Protocol,
    string Trouble = "")
{
    public IReadOnlyList<Dtc> Stored => [.. Faults.Where(f => f.State == DtcState.Stored)];

    public IReadOnlyList<Dtc> Pending => [.. Faults.Where(f => f.State == DtcState.Pending)];

    public IReadOnlyList<Dtc> Permanent => [.. Faults.Where(f => f.State == DtcState.Permanent)];

    public bool Clean => Faults.Count == 0 && !MilOn;

    /// <summary>
    /// Whether the count and the codes disagree.
    ///
    /// A car that says it has two faults and lists none of them has not been fully
    /// read — usually a module that answers PID 01 and ignores mode 03, or a scan
    /// that was cut short. Saying "no faults found" there would be a lie by
    /// omission, so this exists to stop that being said.
    /// </summary>
    public bool CountDisagrees => ReportedCount > Stored.Count;

    /// <summary>One line for a status bar.</summary>
    public string Summary
    {
        get
        {
            if (Trouble.Length > 0) return Trouble;

            var parts = new List<string>();
            if (Stored.Count > 0) parts.Add($"{Stored.Count} stored");
            if (Pending.Count > 0) parts.Add($"{Pending.Count} pending");
            if (Permanent.Count > 0) parts.Add($"{Permanent.Count} permanent");

            string lamp = MilOn ? "lamp on" : "lamp off";

            return parts.Count == 0
                ? $"No fault codes — {lamp}."
                : $"{string.Join(", ", parts)} — {lamp}.";
        }
    }
}

/// <summary>What came of asking the car to erase its faults.</summary>
/// <param name="Erased">Whether the car acknowledged the request.</param>
/// <param name="Message">What to tell the person who asked.</param>
/// <param name="Remaining">
/// Permanent codes, which mode 04 cannot touch. Read back afterwards rather than
/// assumed, since a car that has none is the ordinary case and saying so is worth
/// more than a warning about codes it does not have.
/// </param>
public sealed record FaultClear(bool Erased, string Message, IReadOnlyList<Dtc> Remaining);

/// <summary>
/// Reading and erasing a vehicle's fault codes over OBD2.
///
/// Four modes of the standard, and the interesting part is not any of them
/// individually:
///
/// <list type="bullet">
/// <item><c>03</c> — the confirmed faults, which are the ones lighting the lamp.</item>
/// <item><c>07</c> — pending, seen once and not yet believed.</item>
/// <item><c>0A</c> — permanent, which mode 04 is not allowed to erase.</item>
/// <item><c>04</c> — erase everything the controller is permitted to erase.</item>
/// </list>
///
/// The awkward part is that mode 03 is the first reply in this application long
/// enough not to fit in one CAN frame. Every mode-01 reading is seven bytes or
/// fewer and arrives on one line; three fault codes are ten bytes and arrive as
/// an ISO-TP sequence, which an ELM327 with headers off prints as a length and
/// then numbered fragments:
///
/// <code>
/// 00A
/// 0:43040133
/// 1:01340173019600
/// </code>
///
/// Run through the ordinary hex reader those digits decode into codes that are
/// not on the car — the "0:" alone contributes a nibble and shifts everything
/// after it. So this reassembles the fragments rather than reusing that path.
///
/// The other trap is the count byte. On CAN the reply carries the number of codes
/// straight after the mode echo; on the older serial protocols it does not, and
/// the pairs start immediately. Read the wrong way round, <c>43 02 01 33</c> is
/// either one fault (P0133) or two nonexistent ones — so the protocol is asked
/// for rather than assumed.
/// </summary>
public static class Obd2Faults
{
    /// <summary>Confirmed faults.</summary>
    public const byte StoredMode = 0x03;

    /// <summary>Erase faults, freeze frames and the readiness monitors.</summary>
    public const byte ClearMode = 0x04;

    /// <summary>Faults seen once and not yet confirmed.</summary>
    public const byte PendingMode = 0x07;

    /// <summary>Faults the controller alone may erase.</summary>
    public const byte PermanentMode = 0x0A;

    /// <summary>A positive reply echoes the mode with bit 6 set.</summary>
    public static byte Echoes(byte mode) => (byte)(mode + 0x40);

    /// <summary>
    /// Asks the car for every fault it will admit to.
    ///
    /// All three lists, always, even though most cars have nothing in two of them.
    /// A pending code is the useful one when a fault is intermittent — it is the
    /// evidence that something happened on the drive that has just finished — and
    /// a permanent code is the thing that explains why a car that was "cleared"
    /// still fails an emissions test.
    /// </summary>
    public static FaultScan Scan(Elm327 elm)
    {
        ArgumentNullException.ThrowIfNull(elm);

        string protocol = elm.ProtocolName();
        bool counted = elm.IsCan();

        var faults = new List<Dtc>();
        var trouble = new List<string>();

        foreach ((byte mode, DtcState state) in new[]
                 {
                     (StoredMode, DtcState.Stored),
                     (PendingMode, DtcState.Pending),
                     (PermanentMode, DtcState.Permanent),
                 })
        {
            string reply = Ask(elm, mode);

            if (Answered(reply, mode))
            {
                faults.AddRange(Parse(reply, mode, state, counted));
                continue;
            }

            // "NO DATA" is an answer, and on the older protocols it is the only
            // answer a clean car gives: where a CAN vehicle replies "43 00" to
            // say it has no faults, an ISO 9141 one does not reply at all and the
            // adapter reports that. It is also what any car says to a mode it
            // does not implement. None of those is a failure.
            if (NoData(reply)) continue;

            // Silence is. Told apart on purpose from the above, because reporting
            // the two the same way is how "no faults found" comes to mean "the
            // link was down".
            //
            // Except for permanent codes, which only exist on vehicles built to
            // the later requirement — an older car that ignores mode 0A is not a
            // fault and is not worth a warning on every scan.
            if (mode != PermanentMode) trouble.Add(Name(mode));
        }

        (bool mil, int count) = Lamp(elm);

        return new FaultScan(
            faults, mil, count, protocol,
            trouble.Count == 0
                ? ""
                : $"The vehicle did not answer the request for {string.Join(" or ", trouble)} codes. "
                  + "What is listed may not be all of it.");
    }

    private static string Name(byte mode) => mode switch
    {
        StoredMode => "stored",
        PendingMode => "pending",
        _ => "permanent",
    };

    /// <summary>
    /// Asks once, and again if nothing came back.
    ///
    /// Worth a retry in a way an ordinary reading is not: this happens when
    /// somebody presses a button, and an unanswered attempt reads as a car with
    /// no faults rather than as a question that went astray. A polled gauge that
    /// misses a sample gets another one immediately.
    /// </summary>
    private static string Ask(Elm327 elm, byte mode)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            string reply = elm.Send($"{mode:X2}", elm.ResetTimeout, settle: true);
            if (Answered(reply, mode)) return reply;

            // A car that means "I do not support this" says so at once and will
            // keep saying it. Only silence is worth asking about twice.
            if (Refused(reply)) return reply;
        }

        return "";
    }

    private const int Attempts = 3;

    /// <summary>
    /// A settled answer, as opposed to nothing arriving — either way there is no
    /// point asking twice.
    /// </summary>
    private static bool Refused(string reply) =>
        NoData(reply)
        || reply.Contains("7F", StringComparison.OrdinalIgnoreCase)
        || reply.Contains("UNABLE TO CONNECT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The adapter reporting that the car said nothing within its own timeout.
    ///
    /// For a fault mode this means the car has none, not that the question failed.
    /// A CAN vehicle with a clean bill of health answers "43 00" — the mode echo
    /// and a count of zero — but the older serial protocols have no count to send,
    /// so a car with nothing to report simply does not reply and the adapter says
    /// this instead. Treating it as a failure would put a warning on every scan of
    /// every healthy pre-CAN car.
    /// </summary>
    private static bool NoData(string reply) =>
        reply.Contains("NO DATA", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the reply contains a positive answer to the mode that was asked.</summary>
    public static bool Answered(string? reply, byte mode) =>
        reply is not null && Units(reply, Echoes(mode)).Count > 0;

    /// <summary>
    /// Whether the lamp is on, and how many faults the car counts.
    ///
    /// From PID 01, which is a mode-01 reading like any other and so goes through
    /// the ordinary path. Its top bit is the lamp and the remaining seven are the
    /// count — the same parameter the dashboard already carries as a gauge, asked
    /// here so that a scan is a complete answer on its own.
    /// </summary>
    private static (bool Mil, int Count) Lamp(Elm327 elm)
    {
        Span<byte> data = stackalloc byte[4];

        if (!elm.TryRead(0x01, 4, data, out int got) || got < 1) return (false, 0);

        return ((data[0] & 0x80) != 0, data[0] & 0x7F);
    }

    /// <summary>
    /// Erases everything the controller is allowed to erase.
    ///
    /// Far more than the name suggests, and worth saying so somewhere the caller
    /// can read it. Mode 04 clears the stored and pending codes, the freeze frame
    /// captured when the fault happened, the oxygen sensor test results and the
    /// readiness monitors — so a car cleared this morning cannot pass an emissions
    /// test this afternoon whatever its actual condition, because it now has no
    /// evidence that its monitors ever ran. The freeze frame is the loss that
    /// stings: it is the one record of what the engine was doing at the moment the
    /// fault occurred, and it is not recoverable.
    ///
    /// Permanent codes are untouched by design, which is what they are for.
    ///
    /// Most vehicles refuse this with the engine running.
    /// </summary>
    public static FaultClear Clear(Elm327 elm)
    {
        ArgumentNullException.ThrowIfNull(elm);

        string reply = elm.Send($"{ClearMode:X2}", elm.ResetTimeout, settle: true);

        if (!Answered(reply, ClearMode))
            return new FaultClear(
                false,
                Refused(reply)
                    ? "The vehicle refused to erase its faults. Most will not do this with the "
                      + "engine running — try again with the ignition on and the engine off."
                    : "The vehicle did not answer the erase request, so nothing has been cleared.",
                []);

        // Read back rather than assumed. Permanent codes survive this, and a
        // person who has just been told the car is clear and then fails a test on
        // a code that was there all along has been misled by the tool.
        string permanent = elm.Send($"{PermanentMode:X2}", elm.ResetTimeout, settle: true);

        IReadOnlyList<Dtc> remaining = Answered(permanent, PermanentMode)
            ? Parse(permanent, PermanentMode, DtcState.Permanent, elm.IsCan())
            : [];

        return new FaultClear(
            true,
            remaining.Count == 0
                ? "Erased. The stored and pending codes, the freeze frame and the readiness "
                  + "monitors are all gone — the car will need a full drive cycle before it can "
                  + "pass an emissions test."
                : $"Erased, but {remaining.Count} permanent code"
                  + $"{(remaining.Count == 1 ? " is" : "s are")} still set. Only the controller can "
                  + "clear those, and it will not do so until it has watched the monitor pass.",
            remaining);
    }

    /// <summary>
    /// The codes in one reply.
    ///
    /// Each responding module contributes its own list, and they are kept — two
    /// modules with the same fault is one fault, but two modules with different
    /// faults is two, and which module answers first is not fixed. Duplicates are
    /// collapsed for that reason and only that one.
    /// </summary>
    public static IReadOnlyList<Dtc> Parse(string reply, byte mode, DtcState state, bool counted)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var found = new List<Dtc>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (byte[] unit in Units(reply, Echoes(mode)))
        {
            // Past the echoed mode, and past the count where the protocol carries
            // one. Read the wrong way round the count becomes the first half of a
            // code that is not on the car.
            int at = counted ? 2 : 1;

            for (; at + 1 < unit.Length; at += 2)
            {
                // Replies are padded to the frame with zeros, and P0000 is not
                // an assigned code — so a zero pair is the padding and everything
                // after it in this unit is padding too.
                if (unit[at] == 0 && unit[at + 1] == 0) continue;

                string code = Decode(unit[at], unit[at + 1]);
                if (seen.Add(code)) found.Add(new Dtc(code, state));
            }
        }

        return found;
    }

    /// <summary>
    /// One code from its two bytes, per SAE J2012.
    ///
    /// The first two bits are the system, the next two the first digit, and the
    /// remaining twelve are three hex digits — so the four characters after the
    /// letter are not a decimal number and a code may legitimately read P0A0F.
    /// </summary>
    public static string Decode(byte high, byte low)
    {
        char system = (high >> 6) switch
        {
            0 => 'P',
            1 => 'C',
            2 => 'B',
            _ => 'U',
        };

        return $"{system}{(high >> 4) & 0x3}{high & 0x0F:X}{low:X2}";
    }

    /// <summary>The two bytes a code encodes to — the reverse of <see cref="Decode"/>.</summary>
    public static (byte High, byte Low) Encode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!Obd2Codes.IsWellFormed(code))
            throw new ArgumentException($"'{code}' is not a fault code.", nameof(code));

        string text = code.ToUpperInvariant();

        int system = text[0] switch { 'P' => 0, 'C' => 1, 'B' => 2, _ => 3 };
        int first = text[1] - '0';
        int second = Convert.ToInt32(text[2..3], 16);

        return ((byte)((system << 6) | (first << 4) | second), Convert.ToByte(text[3..5], 16));
    }

    /// <summary>
    /// Every complete answer in a reply, one per responding module, with the
    /// ISO-TP fragments already put back together.
    ///
    /// Three shapes have to come out the same way:
    ///
    /// <list type="bullet">
    /// <item>A single frame — one line of hex beginning with the echoed mode.</item>
    /// <item>A CAN reply too long for one frame — a line giving the total length,
    /// then numbered fragments, <c>0:</c> through <c>F:</c>.</item>
    /// <item>An older serial protocol — several lines, each beginning with the
    /// echoed mode and each carrying up to three codes.</item>
    /// </list>
    ///
    /// The fragment prefixes are the reason this cannot go through the ordinary
    /// hex reader, which ignores anything that is not a hex digit: "0:" is not
    /// ignored, the "0" is a digit, and one stray nibble shifts every byte after
    /// it by half a byte. Every code past the first would be wrong and none of
    /// them would look wrong.
    /// </summary>
    public static IReadOnlyList<byte[]> Units(string reply, byte echoed)
    {
        ArgumentNullException.ThrowIfNull(reply);

        string[] lines = reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        var units = new List<byte[]>();
        List<byte>? assembling = null;
        int declared = 0;

        // One buffer for the whole reply rather than one per line: a car with a
        // long fault list is a dozen lines, and a stack allocation apiece is how
        // a loop like this overflows.
        Span<byte> bytes = stackalloc byte[64];

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            if (Fragment(line) is { } fragment)
            {
                // Fragment zero starts a reply; a fragment arriving without one is
                // the tail of something whose beginning was lost, and half a reply
                // decodes into codes that were never set.
                if (fragment.Index == 0) assembling = [];
                if (assembling is null) continue;

                assembling.AddRange(fragment.Data);

                // The declared length is what says where the real data stops and
                // the last frame's padding begins.
                if (declared > 0 && assembling.Count >= declared)
                {
                    Take(units, assembling.Take(declared), echoed);
                    assembling = null;
                    declared = 0;
                }

                continue;
            }

            // A length header is only a length header when fragments follow it.
            // Nothing about the line itself says so — mode 04's whole reply is the
            // two characters "44", which read on its own is a perfectly good
            // declaration of 68 bytes that never arrive, and the erase would be
            // reported as unanswered every time.
            if (Length(line) is { } total && Fragment(Next(lines, i)) is not null)
            {
                declared = total;
                assembling = null;

                continue;
            }

            // An ordinary line: one module's whole answer.
            int got = Elm327.Unhex(line, bytes);

            if (got >= 1) Take(units, bytes[..got].ToArray(), echoed);
        }

        // A sequence that never reached its declared length is still worth what
        // arrived: the fragments are in order, so a truncated reply is a short
        // list of real codes rather than a wrong one.
        if (assembling is { Count: >= 2 }) Take(units, assembling, echoed);

        return units;
    }

    /// <summary>The next non-blank line, or empty at the end.</summary>
    private static string Next(string[] lines, int after)
    {
        for (int i = after + 1; i < lines.Length; i++)
            if (lines[i].Trim() is { Length: > 0 } line) return line;

        return "";
    }

    private static void Take(List<byte[]> units, IEnumerable<byte> bytes, byte echoed)
    {
        byte[] unit = [.. bytes];

        // The echo is checked rather than assumed. A reply that arrives late is
        // answering the previous question, and mode 07's answer read as mode 03's
        // would report a pending fault as a confirmed one.
        if (unit.Length >= 1 && unit[0] == echoed) units.Add(unit);
    }

    /// <summary>
    /// A numbered ISO-TP fragment — <c>0:43040133</c> — or null for anything else.
    /// </summary>
    private static (int Index, byte[] Data)? Fragment(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon is < 1 or > 2) return null;

        string index = line[..colon].Trim();
        if (index.Length != 1 || !Uri.IsHexDigit(index[0])) return null;

        Span<byte> bytes = stackalloc byte[32];
        int got = Elm327.Unhex(line[(colon + 1)..], bytes);

        return got == 0 ? null : (Convert.ToInt32(index, 16), bytes[..got].ToArray());
    }

    /// <summary>
    /// The total length that precedes a fragmented reply, or null.
    ///
    /// Unambiguous by shape: a real answer is at least a mode echo and one data
    /// byte, so four hex digits, and this is one to three. Nothing else on the
    /// line.
    /// </summary>
    private static int? Length(string line)
    {
        if (line.Length is < 1 or > 3) return null;

        foreach (char c in line)
            if (!Uri.IsHexDigit(c)) return null;

        return Convert.ToInt32(line, 16);
    }
}
