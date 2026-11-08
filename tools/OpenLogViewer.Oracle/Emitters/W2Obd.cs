using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

// Reference outputs for the realtime decoder, ELM327/OBD2 and Subaru SSM (package w2-obd).
// One class per file the Rust test reads: pids, codes, faults, parse, wire, ssm (all synthetic)
// and realtime (the real INIs in the local corpus).

internal static class ObdShared
{
    public static string Hex(IEnumerable<byte> bytes) => string.Concat(bytes.Select(b => b.ToString("X2")));

    public static string[] Bits(IEnumerable<double> values) => [.. values.Select(Exact.Bits)];

    /// <summary>The values a one-byte-per-step sweep of a PID is asked about.</summary>
    public static readonly byte[] Grid =
        [0, 1, 2, 3, 0x0F, 0x10, 0x1A, 0x3F, 0x40, 0x64, 0x7F, 0x80, 0x81, 0xA5, 0xF8, 0xFE, 0xFF];
}

/// <summary>Every PID's channels and limits, its formula over a grid of raw bytes, and the support masks.</summary>
public sealed class W2ObdPids : IEmitter
{
    public string Package => "w2-obd";

    public string Set => "pids";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach (Obd2Pid pid in Obd2Pids.All)
        {
            var inputs = new List<byte[]>();

            switch (pid.DataBytes)
            {
                case 1:
                    for (int a = 0; a < 256; a++) inputs.Add([(byte)a]);
                    break;
                case 2:
                    foreach (byte a in ObdShared.Grid)
                        foreach (byte b in ObdShared.Grid) inputs.Add([a, b]);
                    break;
                default:
                    for (int a = 0; a < 256; a++)
                    {
                        if (pid.DataBytes == 4 && pid.Pid != 0x01) continue;
                        inputs.Add([(byte)a, 0, 0, 0]);
                        inputs.Add([(byte)a, 0xFF, 0xFF, 0xFF]);
                    }

                    if (pid.Pid == 0x24)
                        foreach (byte a in ObdShared.Grid)
                            foreach (byte b in ObdShared.Grid)
                            {
                                inputs.Add([a, b, 0x12, 0x34]);
                                inputs.Add([a, b, 0xFF, 0xFF]);
                            }

                    break;
            }

            cases.Add(new
            {
                kind = "pid",
                pid = pid.Pid,
                data_bytes = pid.DataBytes,
                hot = pid.Hot,
                text = pid.ToString(),
                channels = pid.Channels.Select(c => new
                {
                    name = c.Name,
                    units = c.Units,
                    digits = c.Digits,
                    low = Exact.Bits(c.Low),
                    high = Exact.Bits(c.High),
                    low_danger = Exact.Bits(c.LowDanger),
                    low_warning = Exact.Bits(c.LowWarning),
                    high_warning = Exact.Bits(c.HighWarning),
                    high_danger = Exact.Bits(c.HighDanger),
                }).ToArray(),
                decodes = inputs.Select(d =>
                {
                    var into = new double[pid.Channels.Count];
                    pid.Decode(d, into);
                    return new { data = ObdShared.Hex(d), values = ObdShared.Bits(into) };
                }).ToArray(),
            });
        }

        // Support masks.
        byte[][] masks =
        [
            [0x00, 0x00, 0x00, 0x00], [0xFF, 0xFF, 0xFF, 0xFF], [0x80, 0x00, 0x00, 0x00],
            [0x00, 0x00, 0x00, 0x01], [0xBE, 0x3F, 0xA8, 0x13], [0x18, 0x3A, 0x80, 0x00],
            [0xFF, 0xFF, 0xFF], [],
        ];

        cases.Add(new
        {
            kind = "support",
            queries = Obd2Pids.SupportQueries.Select(q => (int)q).ToArray(),
            masks = masks.Select(m => ObdShared.Hex(m)).ToArray(),
            results = Obd2Pids.SupportQueries.SelectMany(q => masks.Select(m => new
            {
                query = (int)q,
                mask = ObdShared.Hex(m),
                supported = Obd2Pids.SupportedBy(q, m).Select(p => (int)p).ToArray(),
            })).ToArray(),
            // Plus the one that wraps past 0xFF.
            wrapped = Obd2Pids.SupportedBy(0xF0, [0xFF, 0xFF, 0xFF, 0xFF]).Select(p => (int)p).ToArray(),
            lengths = Enumerable.Range(0, 256).Select(p => Obd2Pids.DataBytesOf((byte)p)).ToArray(),
            known = Obd2Pids.Known(new byte[] { 0x0C, 0x05, 0x99, 0x01, 0x0C }).Select(p => (int)p.Pid).ToArray(),
        });

        cases.Add(new
        {
            kind = "gauges",
            gauges = Obd2Gauges.For(Obd2Pids.All).Select(g => new
            {
                name = g.Name,
                channel = g.Channel,
                title = g.Title,
                units = g.Units,
                category = g.Category,
                low = Exact.Bits(g.Low),
                high = Exact.Bits(g.High),
                low_danger = Exact.Bits(g.LowDanger),
                low_warning = Exact.Bits(g.LowWarning),
                high_warning = Exact.Bits(g.HighWarning),
                high_danger = Exact.Bits(g.HighDanger),
                value_digits = g.ValueDigits,
                label_digits = g.LabelDigits,
            }).ToArray(),
            front_page = Obd2Gauges.FrontPage.ToArray(),
            tachometer_top = Exact.Bits(Obd2Pids.TachometerTop),
        });

        return cases;
    }
}

/// <summary>Trouble-code descriptions, authority and system, over a spread of codes.</summary>
public sealed class W2ObdCodes : IEmitter
{
    public string Package => "w2-obd";

    public string Set => "codes";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var codes = new List<string> { "", "P030", "P03011", "X0301", "P03G1", "p0301", "u0100", "c0035", "b1342" };

        foreach (char system in "PCBU")
            foreach (char second in "0123456789ABCDEF")
                foreach (char third in "0123456789ABCDEF")
                    foreach (string tail in new[] { "01", "61" })
                        codes.Add($"{system}{second}{third}{tail}");

        for (int n = 0; n <= 0x3FF; n++) codes.Add($"P0{n:X3}");
        for (int n = 0; n <= 0x0FF; n++) codes.Add($"P2{n:X3}");
        for (int n = 0; n <= 0x0FF; n++) codes.Add($"U0{n:X3}");

        var cases = new List<object>
        {
            new { kind = "named", named = Obd2Codes.Named },
        };

        foreach (string code in codes.Distinct())
        {
            var fault = new Dtc(code, DtcState.Stored);

            cases.Add(new
            {
                code,
                well_formed = Obd2Codes.IsWellFormed(code),
                authority = Obd2Codes.Authority(code).ToString(),
                system = Obd2Codes.System(code),
                describe = Obd2Codes.Describe(code),
                detail = fault.Detail,
                text = fault.ToString(),
            });
        }

        cases.Add(new
        {
            code = (string?)null,
            well_formed = Obd2Codes.IsWellFormed(null),
            authority = Obd2Codes.Authority(null).ToString(),
            system = Obd2Codes.System(null),
            describe = Obd2Codes.Describe(null),
            detail = "",
            text = "",
        });

        return cases;
    }
}

/// <summary>DTC byte pairs both ways, and fault-reply parsing over every shape seen off a car.</summary>
public sealed class W2ObdFaults : IEmitter
{
    public string Package => "w2-obd";

    public string Set => "faults";

    public static readonly string[] Replies =
    [
        "", "43", "4300", "43 00", "4301", "43 01 03 01", "43010301", "43 02 01 33 01 71", "4302013301710000",
        "43 01 03 01\r43 01 07 00\r", "43010301\r43010301\r", "47010301", "47 01 03 01", "NO DATA", "SEARCHING...",
        "SEARCHING...\r43 01 03 01\r", "7F 03 12", "44", "44\r", "4A 01 04 20", "0A 41 01",
        "00A\r0:43040133\r1:01340173019600\r", "00A\r0:430403010171\r1:04200135000000\r", "006\r0:430203010171\r",
        "1:04200135000000\r", "0:430403010171\r1:04200135", "00A\r0:430403010171\r", "00A\r0:430403010171\r\r\r1:04200135000000",
        "008\r1:04200135000000\r0:4304030101", "00A 0:430403010171", "012:430403010171", " 0:43 04 03 01 01 71",
        "0:43040301\r1:01710420013500\r2:0000", "00C\r0:43050301\r1:01710420\r2:0135\r",
        "43 03 01 33 01 34 01 35\r43 00 00 00 00 00 00\r", "43 01 33 01 34 01 35\r43 01 71 00 00 00 00\r",
        "43 00 00 00 00 00 00", "43 00 00 01 33", "4301013\r", "43 01 03 01 >", "43:01 03 01", "FFF\r0:43", "0:43\r1:",
        "003\r0:4301\r", "00A\r0:4304030101\r0:430403010171\r1:04200135000000\r", "43 C1 00 80 01 40 35",
    ];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        // DTC bytes both ways.
        byte[] lows = [0x00, 0x01, 0x0F, 0x10, 0x33, 0x7F, 0x80, 0xA5, 0xFF];
        var decodes = new List<object>();

        for (int high = 0; high < 256; high++)
            foreach (byte low in lows)
            {
                string code = Obd2Faults.Decode((byte)high, low);
                (byte h, byte l) = Obd2Faults.Encode(code);
                decodes.Add(new { high, low = (int)low, code, enc_high = (int)h, enc_low = (int)l });
            }

        cases.Add(new { kind = "decode", decodes });

        // Encode, including the lenient shapes.
        var encodes = new List<object>();
        foreach (string code in new[] { "P0301", "p0301", "C0035", "B1342", "U0100", "P0A0F", "PA000", "PF000", "C3999", "UFFFF", "X1234", "", "P030", "P0G00" })
        {
            try
            {
                (byte h, byte l) = Obd2Faults.Encode(code);
                encodes.Add(new { code, ok = true, high = (int)h, low = (int)l });
            }
            catch (ArgumentException)
            {
                encodes.Add(new { code, ok = false, high = 0, low = 0 });
            }
        }

        cases.Add(new { kind = "encode", encodes });

        // Replies.
        foreach (string reply in Replies)
        {
            foreach (byte mode in new byte[] { 0x03, 0x04, 0x07, 0x0A })
            {
                cases.Add(new
                {
                    kind = "reply",
                    reply,
                    mode = (int)mode,
                    answered = Obd2Faults.Answered(reply, mode),
                    units = Obd2Faults.Units(reply, Obd2Faults.Echoes(mode)).Select(u => ObdShared.Hex(u)).ToArray(),
                    counted = Obd2Faults.Parse(reply, mode, DtcState.Stored, counted: true).Select(f => f.Code).ToArray(),
                    bare = Obd2Faults.Parse(reply, mode, DtcState.Pending, counted: false).Select(f => f.Code).ToArray(),
                });
            }
        }

        cases.Add(new { kind = "echoes", values = Enumerable.Range(0, 256).Select(m => (int)Obd2Faults.Echoes((byte)m)).ToArray() });

        // Summaries.
        FaultScan[] scans =
        [
            new([], false, 0, "CAN"),
            new([], true, 2, "CAN"),
            new([new Dtc("P0301", DtcState.Stored)], true, 1, "CAN"),
            new([new Dtc("P0301", DtcState.Stored), new Dtc("P0300", DtcState.Pending), new Dtc("P0420", DtcState.Permanent), new Dtc("P0171", DtcState.Stored)], false, 2, "x"),
            new([], false, 0, "CAN", "Trouble."),
        ];

        cases.Add(new
        {
            kind = "scans",
            scans = scans.Select(s => new
            {
                faults = s.Faults.Select(f => f.Code + "/" + f.State).ToArray(),
                mil_on = s.MilOn,
                reported_count = s.ReportedCount,
                protocol = s.Protocol,
                trouble = s.Trouble,
                clean = s.Clean,
                count_disagrees = s.CountDisagrees,
                summary = s.Summary,
                stored = s.Stored.Count,
                pending = s.Pending.Count,
                permanent = s.Permanent.Count,
            }).ToArray(),
        });

        return cases;
    }
}

/// <summary>The hex readers and mode-01 reply parsers over a spread of replies.</summary>
public sealed class W2ObdParse : IEmitter
{
    public string Package => "w2-obd";

    public string Set => "parse";

    private static readonly string[] Replies =
    [
        "", "\r", ">", "410C1AF8", "41 0C 1A F8", "41 0C 1A F8\r\r>", "410c1af8", "SEARCHING...\r410C1AF8\r", "SEARCHING...\rNO DATA\r",
        "NO DATA", "410D1A", "410C1A", "410C", "41", "4", "410C1AF8\r410C0000\r", "410C0000\r410C1AF8", "4100BE3FA813", "4100BE3FA813\r410080000001\r",
        "41051E", "41 05", "410B64", "0:410C1AF8", "410C1AF80B640D40", "00D\r0:410C1AF80B64\r1:0D4011330441\r\r>",
        "410100040000\r008\r0:415C600C1AF8\r1:0D40\r\r>", "00D0:410C1AF80B641:0D4011330441", "410C1AF80B6455AAAA", "410C1AF80C1AF8", "410C1AF80B640D",
        "GG410C1AF8", "41-0C-1A-F8", "41\t0C\t1A\tF8", "4 1 0 C 1 A F 8", "410C1AF8ZZ", "41 0C 1A F", "41 0C 1A F8 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
        "410C1AF8 410C1AF8", "\n410C1AF8\n", "ATZ\rELM327 v1.5", "7F 01 12", "410C1AF8:", ":410C1AF8", "::", "410F46\r410F50",
    ];

    private static int Length(byte pid) => Obd2Pids.DataBytesOf(pid);

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach (string reply in Replies)
        {
            var tries = new List<object>();

            foreach ((byte pid, int bytes) in new[] { ((byte)0x0C, 2), ((byte)0x05, 1), ((byte)0x00, 4), ((byte)0x0B, 1), ((byte)0x0F, 1) })
            {
                var into = new byte[bytes];
                bool ok = Elm327.TryParse(reply, pid, bytes, into, out int count);
                var small = new byte[1];
                bool okSmall = Elm327.TryParse(reply, pid, bytes, small, out int smallCount);

                tries.Add(new
                {
                    pid = (int)pid,
                    bytes,
                    ok,
                    count,
                    data = ObdShared.Hex(into),
                    ok_small = okSmall,
                    count_small = smallCount,
                    all = Elm327.ParseAll(reply, pid, bytes).Select(a => ObdShared.Hex(a)).ToArray(),
                });
            }

            var unhex = new List<object>();
            foreach (int size in new[] { 0, 1, 2, 4, 64 })
            {
                var buffer = new byte[size];
                int got = Elm327.Unhex(reply, buffer);
                unhex.Add(new { size, got, data = ObdShared.Hex(buffer) });
            }

            byte[][] askedSets =
            [
                [0x0C, 0x0B, 0x0D], [0x0C, 0x0B, 0x0D, 0x11, 0x04], [0x01, 0x5C, 0x0C, 0x0D], [0x0C, 0x0B], [0x0C], [],
            ];

            cases.Add(new
            {
                reply,
                tries,
                unhex,
                batches = askedSets.Select(asked => new
                {
                    asked = ObdShared.Hex(asked),
                    answers = Elm327.ParseBatch(reply, asked, Length)
                        .Select(a => new { pid = (int)a.Pid, data = ObdShared.Hex(a.Data) }).ToArray(),
                }).ToArray(),
            });
        }

        return cases;
    }
}

/// <summary>
/// The commands sent to an adapter, and what comes out, against a table of canned replies that answers
/// instantly and ends every reply with the prompt. No clock is involved, so Rust and C# must agree exactly.
/// </summary>
public sealed class W2ObdWire : IEmitter
{
    public string Package => "w2-obd";

    public string Set => "wire";

    private sealed class Table(Dictionary<string, string> replies) : IEcuTransport
    {
        private readonly Queue<byte> _out = new();
        private readonly StringBuilder _in = new();

        public List<string> Sent { get; } = [];

        public bool IsOpen => true;

        public void Open() { }

        public void Close() { }

        public void Dispose() { }

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

                string command = _in.ToString();
                _in.Clear();
                Sent.Add(command);

                string reply = replies.TryGetValue(command, out string? r) ? r : "?";
                foreach (char c in reply + "\r\r>") _out.Enqueue((byte)c);
            }
        }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            int n = 0;
            while (n < buffer.Length && _out.Count > 0) buffer[n++] = _out.Dequeue();
            return n;
        }
    }

    private static Dictionary<string, string> Base(string protocolNumber = "6", string protocolText = "ISO 15765-4 (CAN 11/500)") => new()
    {
        ["ATZ"] = "ATZ\rELM327 v1.5",
        ["ATE0"] = "OK",
        ["ATL0"] = "OK",
        ["ATS0"] = "OK",
        ["ATH0"] = "OK",
        ["ATSP0"] = "OK",
        ["ATI"] = "ELM327 v1.5",
        ["ATDP"] = "AUTO, " + protocolText,
        ["ATDPN"] = "A" + protocolNumber,
        ["0100"] = "SEARCHING...\r4100BE3FA813\r410080000001",
        ["0101"] = "410184 07E500".Replace(" ", ""),
        ["010C"] = "410C1AF8",
        ["0105"] = "41055A",
        ["010B"] = "410B56",
        ["0124"] = "412480001234",
        ["010C050B"] = "008\r0:410C1AF8055A\r1:0B56",
        ["010C0B"] = "410C1AF80B56",
        ["01050B"] = "41055A0B56",
        ["04"] = "44",
        ["03"] = "43 02 01 33 01 71",
        ["07"] = "47 01 04 20",
        ["0A"] = "4A 00",
    };

    private static (string Name, Dictionary<string, string> Replies, string[] Actions)[] Scenarios()
    {
        var stn = Base();
        stn["STDI"] = "OBDLink r2.6";
        stn["STI"] = "STN1100 v2.2.2";

        var stiOnly = Base();
        stiOnly["STI"] = "STN1100 v2.2.2";

        var echoing = Base();
        echoing["ATDPN"] = "ATDPN\rA6";
        echoing["STDI"] = "STDI\r?";
        echoing["ATI"] = "ATI\rELM327 v1.5";

        var serial = Base("3", "ISO 9141-2");
        serial["03"] = "43 01 33 01 71 01 96\r43 02 20 00 00 00";
        serial["07"] = "NO DATA";
        serial["0A"] = "NO DATA";
        serial["0101"] = "410100070000";

        var searching = Base("0", "SAE J1850 PWM");

        var fragmented = Base();
        fragmented["03"] = "00A\r0:43040133\r1:01340173019600";
        fragmented["07"] = "SEARCHING...\r47 00";
        fragmented["0A"] = "7F 0A 11";

        var silent = Base();
        silent["03"] = "UNABLE TO CONNECT";
        silent["07"] = "7F 07 11";
        silent["0A"] = "NO DATA";

        var refusing = Base();
        refusing["04"] = "7F 04 22";

        var clearing = Base();
        clearing["0A"] = "4A 01 04 20";
        clearing["03"] = "4300";

        var noisy = Base();
        noisy["ATZ"] = "þÃADF3°À";

        var quiet = Base();
        quiet["ATZ"] = "";

        return
        [
            ("can_clone", Base(), ["reset", "identify", "protocol_name", "protocol_number", "is_can", "warm_up", "read:0C:2", "read:05:1", "read:0B:1", "read:99:1", "read_all:00:4", "many:0C,05,0B", "many:0C,99,0B", "many:0C", "send:0101", "scan", "clear"]),
            ("stn", stn, ["reset", "identify", "protocol_name"]),
            ("stn_firmware_only", stiOnly, ["reset", "identify"]),
            ("sticky_echo", echoing, ["reset", "identify", "protocol_number", "protocol_name", "read:0C:2", "read_all:0C:2"]),
            ("serial", serial, ["reset", "protocol_number", "is_can", "scan", "clear", "read:01:4"]),
            ("searching", searching, ["reset", "protocol_number", "is_can", "protocol_number", "is_can"]),
            ("fragmented", fragmented, ["reset", "scan"]),
            ("silent", silent, ["reset", "scan"]),
            ("refusing", refusing, ["reset", "clear"]),
            ("clearing", clearing, ["reset", "clear"]),
            ("noisy", noisy, ["reset", "identify"]),
            ("quiet", quiet, ["reset"]),
        ];
    }

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach ((string name, Dictionary<string, string> replies, string[] actions) in Scenarios())
        {
            var table = new Table(replies);
            var elm = new Elm327(table);
            var results = new List<string>();

            foreach (string action in actions)
            {
                string[] parts = action.Split(':');

                results.Add(parts[0] switch
                {
                    "reset" => elm.Reset(),
                    "identify" => elm.Identify(),
                    "protocol_name" => elm.ProtocolName(),
                    "protocol_number" => elm.ProtocolNumber().ToString(),
                    "is_can" => elm.IsCan() ? "1" : "0",
                    "warm_up" => Do(elm.WarmUp),
                    "send" => elm.Send(parts[1], elm.Timeout),
                    "read" => Read(elm, parts),
                    "read_all" => string.Join(";", elm.ReadAll(Convert.ToByte(parts[1], 16), int.Parse(parts[2])).Select(a => ObdShared.Hex(a))),
                    "many" => string.Join(
                        ";",
                        elm.ReadMany([.. parts[1].Split(',').Select(p => Convert.ToByte(p, 16))], Obd2Pids.DataBytesOf)
                            .Select(a => $"{a.Pid:X2}={ObdShared.Hex(a.Data)}")),
                    "scan" => Describe(Obd2Faults.Scan(elm)),
                    "clear" => Describe(Obd2Faults.Clear(elm)),
                    _ => throw new InvalidOperationException(action),
                });
            }

            cases.Add(new { name, replies = replies.Select(r => new { command = r.Key, reply = r.Value }).ToArray(), actions, sent = table.Sent.ToArray(), results = results.ToArray() });
        }

        return cases;
    }

    private static string Do(Action action)
    {
        action();
        return "";
    }

    private static string Read(Elm327 elm, string[] parts)
    {
        var into = new byte[32];
        byte pid = Convert.ToByte(parts[1], 16);
        int bytes = int.Parse(parts[2]);

        return elm.TryRead(pid, bytes, into, out int count) ? ObdShared.Hex(into.Take(count)) : "none";
    }

    private static string Describe(FaultScan scan) =>
        string.Join(";", scan.Faults.Select(f => f.Code + "/" + f.State))
        + $"|mil={(scan.MilOn ? 1 : 0)}|count={scan.ReportedCount}|proto={scan.Protocol}|trouble={scan.Trouble}|summary={scan.Summary}";

    private static string Describe(FaultClear clear) =>
        $"{(clear.Erased ? 1 : 0)}|{clear.Message}|{string.Join(";", clear.Remaining.Select(f => f.Code + "/" + f.State))}";
}

/// <summary>SSM requests, replies, parameters and the parameter file.</summary>
public sealed class W2ObdSsm : IEmitter
{
    public string Package => "w2-obd";

    public string Set => "ssm";

    private static readonly string[] Replies =
    [
        "", " ", "E80E", "E886", "E8 0E 36", "E80E36", "7FA813", "7FA812", "7f a8 13", "7F 22 11", "NO DATA", "?", "410C0E2B",
        "SEARCHING...\rE80E", "E8\rE80E", "E8", "7F", "A8", "7FA8", "E80E\rE90F", "xxE80Exx", "E8 0E 36\r",
    ];

    private static readonly string?[] Jsons =
    [
        null, "", "  ", "not json", "[]", "null", "{}", """{"parameters":[]}""", """{"parameters":null}""", """{"parameters":{}}""",
        """{"parameters":[{"name":"A","address":"0x10"}]}""",
        """{"Parameters":[{"NAME":"A","Address":"0X10","BYTES":2,"Units":" u ","Scale":0.5,"Offset":-1,"Digits":2,"Low":-5,"High":9.5,"Enabled":false}]}""",
        "// c\n{ /* c */ \"parameters\": [ { \"name\": \"A\", \"address\": \"10\", }, ], }",
        """{"version":"x","parameters":[{"name":"A","address":"10"}]}""",
        """{"version":null,"parameters":[{"name":"A","address":"10"}]}""",
        """{"version":1.5,"parameters":[{"name":"A","address":"10"}]}""",
        """{"version":2,"parameters":[{"name":"A","address":"10"}]}""",
        """{"parameters":[{"name":"A","address":10}]}""",
        """{"parameters":[{"name":"A","address":"10","bytes":"2"}]}""",
        """{"parameters":[{"name":"A","address":"10","bytes":2.0}]}""",
        """{"parameters":[{"name":"A","address":"10","bytes":1e0}]}""",
        """{"parameters":[{"name":"A","address":"10","bytes":2.5}]}""",
        """{"parameters":[{"name":"A","address":"10","bytes":99999999999}]}""",
        """{"parameters":[{"name":"A","address":"10","scale":"1"}]}""",
        """{"parameters":[{"name":"A","address":"10","scale":0}]}""",
        """{"parameters":[{"name":"A","address":"10","scale":-2}]}""",
        """{"parameters":[{"name":"A","address":"10","enabled":1}]}""",
        """{"parameters":[{"name":"A","address":"10","enabled":null,"bytes":null,"units":null,"scale":null}]}""",
        """{"parameters":[{"name":"A","address":"10"},{"name":"a","address":"11"},{"name":"B","address":"12"}]}""",
        """{"parameters":[{"name":"  ","address":"10"},{"name":"","address":"10"},{"name":" C ","address":"10"}]}""",
        """{"parameters":[{"name":"A","address":"zz"},{"name":"B","address":"0x1000000"},{"name":"C","address":"FFFFFF"},{"name":"D","address":" 0x 1F "}]}""",
        """{"parameters":[{"name":"A","address":"10","name":"B"}]}""",
        """{"parameters":[{"name":"A","address":"10","Name":"B"}]}""",
        """{"parameters":[{"name":"A","address":"10","extra":{"deep":[1,2,{"x":null}]}}],"_notes":["a","b"]}""",
        """{"parameters":[[]]}""",
        """{"parameters":["x"]}""",
        """{"parameters":[{"name":"A","address":"10","bytes":4},{"name":"B","address":"11","bytes":5},{"name":"C","address":"12","bytes":0},{"name":"D","address":"13","bytes":-1}]}""",
        """{"parameters":[{"name":"A","address":"10","high":1e5,"low":-1E-2,"offset":0.1,"scale":3}]}""",
        """{"parameters":[{"name":"A","address":"10","enabled":false},{"name":"B","address":"11","enabled":true}]}""",
        "﻿{\"parameters\":[{\"name\":\"A\",\"address\":\"10\"}]}",
    ];

    private static readonly string?[] Addresses =
    [
        null, "", " ", "0", "0x0", "0x", "0X1f", "1f", "  1f  ", "0x 1F", "1 F", "FFFFFF", "0xFFFFFF", "1000000", "FFFFFFFF", "100000000", "123456789",
        "-1", "+1", "0x-1", "g", "00000000000000000000000001", "0x00000E", "8", " 1F", "1F ", "\t1F\n", "1_0",
    ];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        // Requests.
        int[][] requests = [[0x0E], [0x08], [0], [0xFFFFFF], [0x0E, 0x08], [0x1000000], [-1], [int.MinValue], [int.MaxValue], []];
        var reqs = new List<object>();

        foreach (int[] addresses in requests)
        {
            try
            {
                reqs.Add(new { addresses, ok = true, text = Ssm.ReadRequest(addresses) });
            }
            catch (ArgumentException)
            {
                reqs.Add(new { addresses, ok = false, text = "" });
            }
        }

        cases.Add(new
        {
            kind = "requests",
            requests = reqs,
            setup = Ssm.Setup.ToArray(),
            restore = Ssm.Restore.ToArray(),
            engine_header = Ssm.EngineHeader,
            engine_replies = Ssm.EngineReplies,
            addresses_per_request = Ssm.AddressesPerRequest,
        });

        // Replies.
        cases.Add(new
        {
            kind = "replies",
            replies = Replies.SelectMany(r => new[] { 0, 1, 2, 3 }.Select(expected => new
            {
                reply = r,
                expected,
                data = ObdShared.Hex(Ssm.ReadReply(r, expected)),
                refused = Ssm.Refused(r),
            })).Append(new { reply = (string)null!, expected = 1, data = ObdShared.Hex(Ssm.ReadReply(null, 1)), refused = Ssm.Refused(null) }).ToArray(),
        });

        // Parameters.
        var parameters = new List<SsmParameter>
        {
            new("Engine Speed", 0x0E, Bytes: 2, Scale: 0.25),
            new("Coolant", 0x08, Units: "°C", Offset: -40),
            new("Wide", 0x10, Bytes: 4, Scale: 0.001, Offset: 3.5),
            new("Zero", 0x10, Bytes: 0),
            new("Five", 0x10, Bytes: 5),
            new("", 0x10),
            new("Neg", -1),
            new("NoScale", 0x10, Scale: 0),
        };

        byte[][] raws =
        [
            [], [0], [0x86], [0xFF], [0x0E, 0x36], [0x36, 0x0E], [0xFF, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF], [0x80, 0, 0, 0], [1, 2, 3, 4, 5], [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
            [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x11],
        ];

        cases.Add(new
        {
            kind = "parameters",
            parameters = parameters.Select(p => new
            {
                name = p.Name,
                address = p.Address,
                bytes = p.Bytes,
                units = p.Units,
                usable = p.IsUsable,
                addresses = p.Addresses.Take(8).ToArray(),
                reads = raws.Select(raw => new { raw = ObdShared.Hex(raw), value = Exact.Bits(p.Read(raw)) }).ToArray(),
            }).ToArray(),
        });

        // Addresses.
        cases.Add(new
        {
            kind = "addresses",
            addresses = Addresses.Select(a => new { text = a, value = ParseAddress(a) }).ToArray(),
        });

        // Files.
        cases.Add(new
        {
            kind = "files",
            name = SsmParameterFile.Name,
            template = SsmParameterFile.Template.Replace("\r\n", "\n"),
            files = Jsons.Select(json => new
            {
                json,
                read = SsmParameterFile.Read(json).Select(Describe).ToArray(),
                enabled = SsmParameterFile.Enabled(json).Select(p => p.Name).ToArray(),
            }).ToArray(),
        });

        return cases;
    }

    // Internal in Core; the oracle is not a friend assembly, so it is reached by reflection.
    private static int? ParseAddress(string? text) =>
        (int?)typeof(SsmParameterFile)
            .GetMethod("ParseAddress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [text]);

    private static object Describe(SsmParameter p) => new
    {
        name = p.Name,
        address = p.Address,
        bytes = p.Bytes,
        units = p.Units,
        scale = Exact.Bits(p.Scale),
        offset = Exact.Bits(p.Offset),
        digits = p.Digits,
        low = Exact.Bits(p.Low),
        high = Exact.Bits(p.High),
        enabled = p.Enabled,
    };
}

/// <summary>
/// The realtime decoder against every INI in the local corpus: names, units, digits and unresolved
/// expressions, then values from a few deterministic blocks. Settings are the INI's own constants, offered
/// to the expression parser in the order the C# dictionary enumerates them.
/// </summary>
public sealed class W2ObdRealtime : IEmitter
{
    public string Package => "w2-obd";

    public string Set => "realtime";

    public bool FromCorpus => true;

    private static byte[] Block(int size, string kind)
    {
        var block = new byte[size];
        ulong state = 0x9E3779B97F4A7C15;

        for (int i = 0; i < size; i++)
        {
            switch (kind)
            {
                case "zero": block[i] = 0; break;
                case "ones": block[i] = 0xFF; break;
                case "ramp": block[i] = (byte)i; break;
                default:
                    state ^= state << 13;
                    state ^= state >> 7;
                    state ^= state << 17;
                    block[i] = (byte)(state >> 24);
                    break;
            }
        }

        return block;
    }

    private static string Value(double v) => double.IsNaN(v) ? "nan" : Exact.Bits(v);

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach (string path in context.CorpusFiles("definitions"))
        {
            if (!path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;

            string text = TuningText.Read(path);
            RealtimeLayout layout = MsqIni.ReadOutputChannels(text);
            IReadOnlyDictionary<string, double> known = TuningContext.Build(text, null);

            var decoder = new RealtimeDecoder(layout, known);
            var bare = new RealtimeDecoder(layout);

            var blocks = new List<object>();
            int size = Math.Max(layout.BlockSize, 1);

            foreach (string kind in new[] { "zero", "ones", "ramp", "random", "random2" })
            {
                byte[] block = kind == "random2" ? Block(size, "random").Select(b => (byte)(b ^ 0x5A)).ToArray() : Block(size, kind);

                foreach ((string label, byte[] bytes) in new[] { ("full", block), ("half", block[..(block.Length / 2)]), ("empty", Array.Empty<byte>()) })
                {
                    blocks.Add(new
                    {
                        kind,
                        length = label,
                        block = ObdShared.Hex(bytes),
                        values = decoder.Decode(bytes).Select(Value).ToArray(),
                        bare_values = bare.Decode(bytes).Select(Value).ToArray(),
                    });
                }
            }

            cases.Add(new
            {
                file = Path.GetFileName(path),
                block_size = layout.BlockSize,
                little_endian = layout.LittleEndian,
                settings = known.Select(k => new { name = k.Key, value = Exact.Bits(k.Value) }).ToArray(),
                names = decoder.Names.ToArray(),
                units = decoder.Units.ToArray(),
                digits = decoder.Digits.ToArray(),
                unresolved = decoder.UnresolvedExpressions.ToArray(),
                bare_names = bare.Names.ToArray(),
                bare_unresolved = bare.UnresolvedExpressions.ToArray(),
                blocks,
            });
        }

        return cases;
    }
}
