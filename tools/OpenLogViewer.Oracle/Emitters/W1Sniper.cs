using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// What the C# Sniper stack puts on the wire: complete HEFI messages (marker, byte swap and CRC),
/// the fragmenter's frames, and the frames SniperSession sends for Open and requests against a
/// scripted transport. Pins the Rust to the C#'s bytes, not to the Rust's own helpers.
/// </summary>
public sealed class W1Sniper : IEmitter
{
    public string Package => "w1-sniper";

    public string Set => "wire";

    private static readonly byte[][] Payloads =
    [
        [], [0xAA], [0xAA, 0xBB, 0xCC], [0xAA, 0xBB, 0xCC, 0xDD], [1, 2, 3, 4, 5],
        [1, 2, 3, 4, 5, 6, 7, 8], [.. Enumerable.Range(0, 12).Select(i => (byte)(i * 7))],
        [.. Enumerable.Range(0, 17).Select(i => (byte)(255 - i))],
    ];

    private static readonly (uint, uint, uint)[] Commands =
    [
        (0, 0, 0), (0x01020304, 0x05060708, 0x090a0b0c), (0x45, 0, 0), (0x40000A01, 0, 0x1008),
        (0x40000a02, 0xFFFFFFFF, 0x80000000),
    ];

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    private sealed class Scripted : ISniperCanTransport
    {
        public bool IsOpen { get; private set; }
        public List<SniperCanFrame> Sent { get; } = [];
        public List<uint> Bitrates { get; } = [];
        public Queue<SniperCanFrame> Incoming { get; } = new();
        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void Send(SniperCanFrame frame) => Sent.Add(frame);
        public SniperCanFrame? Receive(TimeSpan timeout) => Incoming.Count > 0 ? Incoming.Dequeue() : null;
        public void SetBitrate(uint bitsPerSecond) => Bitrates.Add(bitsPerSecond);
        public uint GetHardwareVersion() => 1;
        public void Dispose() => Close();
    }

    private static object Frames(IEnumerable<SniperCanFrame> frames) =>
        frames.Select(f => new { can_id = f.CanId, data = Hex(f.Data) }).ToArray();

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach (var (c0, c1, c2) in Commands)
        {
            foreach (byte[] payload in Payloads)
            {
                byte[] message = SniperHefi.BuildMessage(c0, c1, c2, payload);
                cases.Add(new
                {
                    kind = "message",
                    cmd0 = c0, cmd1 = c1, cmd2 = c2,
                    payload = Hex(payload),
                    message = Hex(message),
                    fragments = SniperHefi.FragmentForSend(message).Select(Hex).ToArray(),
                });
            }
        }

        foreach (uint nodeReply in new uint[] { 0, 1, 5, 0x7FF })
        {
            var t = new Scripted();
            t.Incoming.Enqueue(new SniperCanFrame(SniperCan.SimpleId((int)nodeReply, SniperCan.SimpleCommand.NodeAck), []));
            using var session = new SniperSession(t, ownsTransport: false);
            session.Open(TimeSpan.FromMilliseconds(200));
            var openSent = t.Sent.ToList();
            int node = session.EcuNode;

            // Each request kind against a fresh reply: header (marker + length) then payload.
            var requests = new (string Name, Func<byte[]> Run)[]
            {
                ("device_info", () => session.ReadDeviceInfo()),
                ("telemetry", () => session.PollTelemetryRaw()),
                ("calibration_read", () => session.ReadCalibrationBlock(0x40000a11)),
                ("request_with_payload", () => session.SendRequest(0x40000a02, 1, 2, [9, 8, 7, 6, 5])),
            };

            var reqCases = new List<object>();
            foreach (var (name, run) in requests)
            {
                t.Sent.Clear();
                byte[] header = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, SniperHefi.BlockReplyMarker);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 4 + 10);
                uint rid = SniperCan.BulkId(0, 1);
                t.Incoming.Enqueue(new SniperCanFrame(rid, header));
                t.Incoming.Enqueue(new SniperCanFrame(rid, [1, 2, 3, 4, 5, 6, 7, 8]));
                t.Incoming.Enqueue(new SniperCanFrame(rid, [9, 10]));
                byte[] reply = run();
                reqCases.Add(new { name, sent = Frames(t.Sent), reply = Hex(reply) });
            }

            cases.Add(new
            {
                kind = "session",
                node_reply = nodeReply,
                open_bitrates = t.Bitrates.ToArray(),
                open_sent = Frames(openSent),
                ecu_node = node,
                requests = reqCases,
            });
        }

        return cases;
    }
}
