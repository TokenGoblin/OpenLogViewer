using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

// Reference outputs for the ECU connection (package w3-connection): every request EcuConnection
// puts on the wire, byte for byte, and how each one ends, for the three ECU families the
// TunerStudio protocol serves.
//
// Synthetic and committed: nothing here is personal data. The ECU is a model of one page of
// memory (below) behind a transport that records what it is sent, so the answer to "what does
// the C# send for a 20 byte write at 0x20 with a blocking factor of 16" is the C# itself
// running, and the Rust must send the same bytes.
//
// The page commands and the realtime commands are copied from the real INIs (Speeduino
// 202501.7, MS2Extra comms342h2, rusEFI uaEFI), because the byte layout comes from them.

internal enum Behaviour
{
    None,
    IgnoresWrites,
    RefusesWrites,
    RefusesBurn,
    BurnStatus04,
    SilentBurn,
}

/// <summary>A page of ECU memory behind the MegaSquirt-family commands, recording what it is sent.</summary>
internal sealed class ConnectionSim(bool little, Behaviour behaviour) : IEcuTransport
{
    private byte[] _pending = [];

    public byte[] Memory { get; } = [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 + 3))];

    public List<byte[]> Written { get; } = [];

    public bool IsOpen { get; private set; }

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose() => Close();

    public void DiscardInput() { }

    public void Write(ReadOnlySpan<byte> data)
    {
        byte[] framed = data.ToArray();
        Written.Add(framed);
        _pending = Respond(framed);
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int take = Math.Min(buffer.Length, _pending.Length);
        _pending.AsSpan(0, take).CopyTo(buffer);
        _pending = _pending[take..];

        return take;
    }

    private static byte[] Reply(byte status, ReadOnlySpan<byte> data) => MsProtocol.Frame([status, .. data]);

    private int Word(byte a, byte b) => little ? (b << 8) | a : (a << 8) | b;

    private byte[] Respond(byte[] framed)
    {
        byte[] p = framed[2..^4];

        switch ((char)p[0])
        {
            case 'S': return Reply(0x00, "speeduino 202501-dev"u8);
            case 'Q': return Reply(0x83, []);
            case 'V': return Reply(0x00, "Speeduino 2025.01"u8);

            case 'r' or 'p' or 'R':
            {
                int offset = Word(p[3], p[4]);
                int count = Word(p[5], p[6]);

                return Reply(0x00, Memory.AsSpan(offset, count));
            }

            case 'O':
            {
                int offset = Word(p[1], p[2]);
                int count = Word(p[3], p[4]);

                return Reply(0x00, Memory.AsSpan(offset, count));
            }

            case 'w' or 'M' or 'C':
            {
                if (behaviour == Behaviour.RefusesWrites) return Reply(0x83, []);

                int offset = Word(p[3], p[4]);
                int count = Word(p[5], p[6]);

                if (behaviour != Behaviour.IgnoresWrites) p.AsSpan(7, count).CopyTo(Memory.AsSpan(offset));

                return Reply(0x00, []);
            }

            case 'b' or 'B':
                return behaviour switch
                {
                    Behaviour.RefusesBurn => Reply(0x83, []),
                    Behaviour.BurnStatus04 => Reply(0x04, []),
                    Behaviour.SilentBurn => [],
                    _ => Reply(0x00, []),
                };

            default:
                throw new InvalidOperationException($"unknown command {p[0]:X2}");
        }
    }
}

/// <summary>Every request the connection sends for the three families, and how each ends.</summary>
public sealed class W3ConnectionRequests : IEmitter
{
    public string Package => "w3-connection";

    public string Set => "requests";

    private sealed record Family(
        string Name, bool Little, TunePage Page, string RealtimeIni, int RealtimeSize);

    private static string Hex(IEnumerable<byte> bytes) => string.Concat(bytes.Select(b => b.ToString("X2")));

    private static readonly byte[] Data4 = [0xDE, 0xAD, 0xBE, 0xEF];

    private static readonly byte[] Data20 = [.. Enumerable.Range(100, 20).Select(i => (byte)i)];

    private static IReadOnlyList<Family> Families =>
    [
        new(
            "speeduino", true,
            new TunePage
            {
                Index = 0, Size = 0x100, Identifier = @"\$tsCanId\x01", ReadCommand = "p%2i%2o%2c",
                ChunkWriteCommand = "M%2i%2o%2c%v", BurnCommand = "B%2i",
            },
            "[OutputChannels]\nochGetCommand = \"r\\$tsCanId\\x30%2o%2c\"\nochBlockSize = 300\n"
            + "[Constants]\nendianness = little\nblockingFactor = 121\n",
            300),
        new(
            "ms2", false,
            new TunePage
            {
                Index = 0, Size = 0x100, Identifier = @"\$tsCanId\x04", ReadCommand = @"r\$tsCanId\x04%2o%2c",
                ChunkWriteCommand = @"w\$tsCanId\x04%2o%2c%v", BurnCommand = @"b\$tsCanId\x04",
            },
            "[OutputChannels]\nochGetCommand = \"r\\$tsCanId\\x07%2o%2c\"\nochBlockSize = 212\n"
            + "[Constants]\nendianness = big\nblockingFactor = 256\n",
            212),
        new(
            "rusefi", true,
            new TunePage
            {
                Index = 3, Size = 0x100, Identifier = @"\x00\x03", ReadCommand = "R%2i%2o%2c",
                ChunkWriteCommand = "C%2i%2o%2c%v", BurnCommand = "B%2i",
            },
            "[OutputChannels]\nochGetCommand = \"O%2o%2c\"\nochBlockSize = 2200\n"
            + "[Constants]\nendianness = little\nblockingFactor = 1024\n",
            2200),
    ];

    private sealed record Scenario(
        string Name, string Op, Behaviour Behaviour = Behaviour.None, int Offset = 0, int Count = 0,
        int Blocking = 0, byte[]? Data = null, int Delay = 0, bool CanId5 = false);

    private static IReadOnlyList<Scenario> Scenarios =>
    [
        new("read_range", "read_range", Offset: 0x10, Count: 0x20),
        new("read_range_chunked", "read_range", Offset: 0x10, Count: 0x25, Blocking: 16),
        new("read_page", "read_page", Blocking: 64),
        new("read_range_other_can_id", "read_range", Offset: 0x10, Count: 0x20, CanId5: true),
        new("write_small", "write", Offset: 0x10, Data: Data4),
        new("write_chunked", "write", Offset: 0x20, Data: Data20, Blocking: 16),
        new("write_tight_blocking", "write", Offset: 0x00, Data: Data4, Blocking: 8),
        new("write_other_can_id", "write", Offset: 0x10, Data: Data4, CanId5: true),
        new("write_past_end", "write", Offset: 0xFE, Data: Data4),
        new("write_empty", "write", Offset: 0x10, Data: []),
        new("write_ignored", "write", Behaviour.IgnoresWrites, Offset: 0x10, Data: Data4),
        new("write_refused", "write", Behaviour.RefusesWrites, Offset: 0x10, Data: Data4),
        new("burn", "burn"),
        new("burn_status_04", "burn", Behaviour.BurnStatus04),
        new("burn_refused", "burn", Behaviour.RefusesBurn),
        new("burn_silent", "burn", Behaviour.SilentBurn),
        new("burn_other_can_id", "burn", CanId5: true),
        new("identity", "identity"),
        new("realtime", "realtime"),
        new("realtime_single_request", "realtime_single"),
    ];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach (Family family in Families)
        {
            foreach (Scenario scenario in Scenarios)
            {
                cases.Add(Run(family, scenario));
            }
        }

        return cases;
    }

    private static object Run(Family family, Scenario scenario)
    {
        var sim = new ConnectionSim(family.Little, scenario.Behaviour);
        var settings = new EcuConnectionSettings
        {
            Timeout = TimeSpan.FromMilliseconds(1),
            BurnTimeout = TimeSpan.FromMilliseconds(5),
            SettleFor = TimeSpan.FromMilliseconds(5),
            QuietFor = TimeSpan.FromMilliseconds(1),
            CanId = (byte)(scenario.CanId5 ? 5 : 0),
        };

        using var connection = new EcuConnection(sim, settings);

        string outcome = "ok";
        string? errorKind = null;
        string? message = null;
        bool? refused = null;
        string result = "";

        try
        {
            switch (scenario.Op)
            {
                case "read_range":
                    result = Hex(connection.ReadTunePageRange(
                        family.Page, scenario.Blocking, family.Little, scenario.Offset, scenario.Count));
                    break;

                case "read_page":
                    result = Hex(connection.ReadTunePage(family.Page, scenario.Blocking, family.Little));
                    break;

                case "write":
                    connection.WriteTunePage(
                        family.Page, scenario.Blocking, family.Little, scenario.Offset, scenario.Data!,
                        scenario.Delay);
                    result = Hex(sim.Memory.AsSpan(scenario.Offset, scenario.Data!.Length).ToArray());
                    break;

                case "burn":
                    connection.BurnPage(family.Page, family.Little);
                    break;

                case "identity":
                    result = string.Join("|", connection.ReadIdentity(TimeSpan.Zero));
                    break;

                case "realtime":
                case "realtime_single":
                {
                    RealtimeLayout layout = MsqIni.ReadOutputChannels(family.RealtimeIni);
                    connection.Use(layout, singleRequest: scenario.Op == "realtime_single");
                    result = Hex(connection.ReadRealtime(family.RealtimeSize));
                    break;
                }
            }
        }
        catch (EcuProtocolException e)
        {
            outcome = "error";
            errorKind = "protocol";
            message = e.Message;
            refused = e.Refused;
        }

        return new
        {
            family = family.Name,
            scenario = scenario.Name,
            op = scenario.Op,
            behaviour = scenario.Behaviour.ToString(),
            little_endian = family.Little,
            can_id = scenario.CanId5 ? 5 : 0,
            page = new
            {
                index = family.Page.Index,
                size = family.Page.Size,
                identifier = family.Page.Identifier,
                read_command = family.Page.ReadCommand,
                chunk_write_command = family.Page.ChunkWriteCommand,
                burn_command = family.Page.BurnCommand,
            },
            offset = scenario.Offset,
            count = scenario.Count,
            blocking = scenario.Blocking,
            data = scenario.Data is null ? null : Hex(scenario.Data),
            delay = scenario.Delay,
            realtime_ini = family.RealtimeIni,
            realtime_size = family.RealtimeSize,
            requests = sim.Written.Select(w => Hex(w)).ToArray(),
            outcome,
            error_kind = errorKind,
            message,
            refused,
            result,
            retries = connection.Retries,
        };
    }
}
