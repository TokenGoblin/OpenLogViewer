using System.Buffers.Binary;
using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

// Reference outputs for the live package (w4-live): what the MaxxECU tune code puts on the wire
// and what comes back, the table CRC the client computes, the guards on a write, the CAN ring
// decode, and the bytes of a recording.
//
// Synthetic and committed. The ECU is a model (ModelEcu below) behind a transport that records
// every byte it is sent, so the answer to "what does the C# send to write 4 bytes at 0xF2E3" is the
// C# running. The Rust has a model of the same ECU and must send the same bytes, refuse the same
// writes and end every scenario the same way.
//
// MaxxECU writes are live and permanent in one step: nothing here is ever pointed at a real ECU.

/// <summary>A MaxxECU in software: a 64 KB tune, a CAN ring, a drop counter, and a way to misbehave.</summary>
internal sealed class ModelEcu : IEcuTransport
{
    private byte[] _reply = [];
    private (int Offset, int Length)? _expecting;
    private readonly Queue<byte[]> _ring = new();

    public byte[] Tune { get; } = Pattern();

    public byte HeaderStatus { get; set; } = MaxxUsbProtocol.Ok;

    public bool AnswerHeader { get; set; } = true;

    public bool AnswerPayload { get; set; } = true;

    /// <summary>Requests to leave unanswered before answering normally.</summary>
    public int SilentReplies { get; set; }

    public ushort DropCounter { get; set; } = 0x1234;

    public List<byte[]> Written { get; } = [];

    public bool IsOpen { get; private set; }

    public static byte[] Pattern() => [.. Enumerable.Range(0, MaxxTune.BlobSize).Select(i => (byte)((i * 7) + (i >> 8)))];

    public void Enqueue(byte[] record) => _ring.Enqueue(record);

    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    public void Dispose() => Close();

    public void DiscardInput() => _reply = [];

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        int taken = Math.Min(buffer.Length, _reply.Length);
        _reply.AsSpan(0, taken).CopyTo(buffer);
        _reply = _reply[taken..];

        return taken;
    }

    public static byte[] Answer(byte[] data)
    {
        var reply = new byte[data.Length + MaxxUsbProtocol.ReplyOverhead];

        reply[0] = MaxxUsbProtocol.Ok;
        data.CopyTo(reply, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(1 + data.Length), MaxxProtocol.Crc32(reply.AsSpan(0, 1 + data.Length)));

        return reply;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        Written.Add(data.ToArray());

        if (_expecting is { } pending)
        {
            uint sent = BinaryPrimitives.ReadUInt32LittleEndian(data[pending.Length..]);

            if (sent == MaxxProtocol.Crc32(data[..pending.Length]))
            {
                data[..pending.Length].CopyTo(Tune.AsSpan(pending.Offset));
                _reply = AnswerPayload ? [MaxxUsbProtocol.Ok] : [];
            }
            else _reply = [(byte)MaxxWriteStatus.OutOfRange];

            _expecting = null;
            return;
        }

        byte command = data[1];
        int offset = data[2] | (data[3] << 8);
        int length = data[4] | (data[5] << 8);

        if (SilentReplies > 0)
        {
            SilentReplies--;
            _reply = [];
            return;
        }

        if (data[0] == MaxxUsbProtocol.Write)
        {
            if (!AnswerHeader) { _reply = []; return; }
            if (HeaderStatus != MaxxUsbProtocol.Ok) { _reply = [HeaderStatus]; return; }

            _expecting = (offset, length);
            _reply = [MaxxUsbProtocol.Ok];
            return;
        }

        byte[] body = command switch
        {
            MaxxTune.ReadTune => Tune.AsSpan(offset, length).ToArray(),
            0x37 => ChecksumBytes(offset, length),
            MaxxCan.Take => Ring(length),
            MaxxCan.Snapshot => Snapshot(offset, length),
            _ => new byte[length],
        };

        _reply = Answer(body);
    }

    private byte[] ChecksumBytes(int covers, int length)
    {
        var bytes = new byte[length];
        uint[] sums = MaxxTune.ChecksumsOf(Tune, covers);

        for (int i = 0; i < sums.Length && (i * 4) + 4 <= length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), sums[i]);

        return bytes;
    }

    private byte[] Ring(int length)
    {
        var bytes = new byte[length];

        for (int at = 0; at + MaxxCan.RecordLength <= length && _ring.Count > 0; at += MaxxCan.RecordLength)
            _ring.Dequeue().CopyTo(bytes, at);

        return bytes;
    }

    private byte[] Snapshot(int offset, int length)
    {
        var window = new byte[MaxxCan.SnapshotWindow];
        for (int i = 0; i < window.Length; i++) window[i] = (byte)(i * 3);
        BinaryPrimitives.WriteUInt16LittleEndian(window.AsSpan(MaxxCan.DropCountAt), DropCounter);

        return window.AsSpan(offset, length).ToArray();
    }
}

/// <summary>Shared by the W4 emitters.</summary>
internal static class W4
{
    public static string Hex(IEnumerable<byte> bytes) => string.Concat(bytes.Select(b => b.ToString("X2")));

    public static string Hex(uint value) => value.ToString("X8");

    /// <summary>Lays a table into a blob the way a MaxxECU does (the C# MaxxTuneTests' Compose).</summary>
    public static void Compose(
        byte[] blob, int config, int dataOffset, int xChannel, int yChannel, short[] x, short[] y, short[,] cells)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(config), (ushort)xChannel);
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(config + 0x02), (ushort)yChannel);
        blob[config + 0x06] = (byte)x.Length;
        blob[config + 0x07] = (byte)y.Length;
        blob[config + 0x08] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(config + 0x0B), (ushort)dataOffset);

        int cursor = MaxxTune.TableData + dataOffset;

        // A non-zero pad byte, so a checksum that skips it differs from one that covers it.
        blob[cursor] = 0x5A;
        cursor++;

        foreach (short value in x) { BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(cursor), value); cursor += 2; }
        foreach (short value in y) { BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(cursor), value); cursor += 2; }

        // Nine, written as nine.
        cursor += 9;

        for (int row = 0; row < y.Length; row++)
            for (int column = 0; column < x.Length; column++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(cursor), cells[row, column]);
                cursor += 2;
            }
    }

    public static readonly short[] Rpm = [200, 800, 1000, 1200, 1600, 2000, 2500, 3000, 3500, 4000, 4500, 5000, 5500, 6000, 6500, 7000];

    public static readonly short[] Kpa = [200, 400, 503, 600, 800, 999, 1199, 1399, 1599, 1799, 1999];

    public static byte[] WithVeTable(int config = 1578, int dataOffset = 496)
    {
        var blob = new byte[MaxxTune.BlobSize];
        var cells = new short[Kpa.Length, Rpm.Length];

        for (int row = 0; row < Kpa.Length; row++)
            for (int column = 0; column < Rpm.Length; column++)
                cells[row, column] = (short)(280 + (row * 80) + (column * 2));

        Compose(blob, config, dataOffset, 61, 20, Rpm, Kpa, cells);

        return blob;
    }

    public static byte[] WithSmallTable(int config = 400, int dataOffset = 100)
    {
        var blob = new byte[MaxxTune.BlobSize];
        short[] axis = [10, 20, 30, 40, 50, 60];
        var cells = new short[6, 6];

        for (int row = 0; row < 6; row++)
            for (int column = 0; column < 6; column++)
                cells[row, column] = (short)((row * 100) + column);

        Compose(blob, config, dataOffset, 61, 20, axis, axis, cells);

        return blob;
    }

    /// <summary>The exception a delegate throws, as (type name, message), or (null, null).</summary>
    public static (string? Kind, string? Message) Thrown(Action action)
    {
        try
        {
            action();
            return (null, null);
        }
        catch (Exception e)
        {
            return (e.GetType().Name, e.Message);
        }
    }
}

/// <summary>Every request the MaxxECU tune and CAN code builds, and what comes back.</summary>
public sealed class W4LiveRequests : IEmitter
{
    public string Package => "w4-live";

    public string Set => "maxx_requests";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        // The whole tune read: 256 requests, 256 bytes each, and the blob that comes back.
        {
            var ecu = new ModelEcu();
            var progress = new List<string>();
            byte[] blob = MaxxTune.Read(ecu, (done, total) => progress.Add($"{done}/{total}"));

            cases.Add(Case("read_all", ecu, W4.Hex(MaxxProtocol.Crc32(blob)), progress: progress, blob: blob.Length));
        }

        // A reply lost in the middle: the same request again, and the retry counted.
        {
            var ecu = new ModelEcu();
            int retries = 0;
            byte[] blob = MaxxTune.Read(ecu, onRetry: () => retries++);
            cases.Add(Case("read_all_clean_retries", ecu, W4.Hex(MaxxProtocol.Crc32(blob)), retries: retries));

            var flaky = new LossyEcu(lose: [3, 4]);
            int lost = 0;
            byte[] again = MaxxTune.Read(flaky, onRetry: () => lost++);
            cases.Add(Case("read_all_with_losses", flaky.Inner, W4.Hex(MaxxProtocol.Crc32(again)), retries: lost, discards: flaky.Discards));
        }

        // Silence: three tries at the first piece, then the exception.
        {
            var ecu = new ModelEcu { SilentReplies = int.MaxValue };
            var (kind, message) = W4.Thrown(() => MaxxTune.Read(ecu));
            cases.Add(Case("read_silent", ecu, "", kind: kind, message: message));
        }

        // Read requests across offsets and lengths, as verify asks for them.
        foreach ((int offset, int length) in new[] { (0, 1), (0, 256), (1, 4), (255, 2), (256, 256), (1000, 100), (0xF000, 256), (0xF1E7, 256), (0xF2E3, 4), (0xFFFF, 1), (65000, 536) })
        {
            var ecu = new ModelEcu();
            byte[] expected = offset + length <= MaxxTune.BlobSize ? ecu.Tune.AsSpan(offset, length).ToArray() : [];
            bool? result = null;
            string? kind = null;
            string? message = null;

            if (offset + length <= MaxxTune.BlobSize)
                (kind, message) = W4.Thrown(() => result = MaxxTune.Verify(ecu, offset, expected));

            cases.Add(Case($"verify_{offset}_{length}", ecu, result?.ToString() ?? "", kind: kind, message: message, discards: 0));

            // Wrong expectation: reads back and says no.
            if (offset + length <= MaxxTune.BlobSize && length > 0)
            {
                var other = new ModelEcu();
                byte[] wrong = [.. expected];
                wrong[0] ^= 1;
                cases.Add(Case($"verify_wrong_{offset}_{length}", other, MaxxTune.Verify(other, offset, wrong).ToString()));
            }
        }

        // Checksums: the ECU's own, over several spans, and the refusals.
        foreach (int covers in new[] { MaxxTune.WriteCeiling, 62180, 65535, 4096, 4097, 1, 8192, 0, -1, 65536, 70000 })
        {
            var ecu = new ModelEcu();
            uint[] sums = [];
            var (kind, message) = W4.Thrown(() => sums = MaxxTune.Checksums(ecu, covers));
            cases.Add(Case($"checksums_{covers}", ecu, string.Join(",", sums.Select(W4.Hex)), kind: kind, message: message));
        }

        {
            var ecu = new ModelEcu { SilentReplies = 1 };
            uint[] sums = MaxxTune.Checksums(ecu);
            cases.Add(Case("checksums_unanswered", ecu, $"{sums.Length}"));
        }

        // The CAN ring: the requests for 1 to 15 frames, and the refusals.
        foreach (int frames in new[] { 1, 2, 4, 14, 15, 16, 0, -3 })
        {
            string hex = "";
            var (kind, message) = W4.Thrown(() => hex = W4.Hex(MaxxCan.Ask(frames)));
            cases.Add(new { name = $"can_ask_{frames}", requests = hex.Length > 0 ? new[] { hex } : Array.Empty<string>(), result = "", error_kind = kind, message, discards = 0, retries = 0, progress = Array.Empty<string>(), blob = 0 });
        }

        // The drop counter read, through the source that owns the cable.
        {
            var ecu = new ModelEcu { DropCounter = 0xFFF0 };
            using var source = new MaxxUsbSource(ecu);
            int? count = source.CanDropCount();
            cases.Add(Case("can_drop_count", ecu, count?.ToString() ?? "null"));
        }

        // Frames out of a ring holding more than one read's worth.
        {
            var ecu = new ModelEcu();
            for (int i = 0; i < 20; i++) ecu.Enqueue(CanRecord(0x100 + i, 1 + (i % 8), (uint)(1000 + i)));
            using var source = new MaxxUsbSource(ecu);
            IReadOnlyList<CanFrame> frames = source.ReadCanFrames(MaxxCan.MostPerRead);
            string result = string.Join(";", frames.Select(f => $"{f.Id:X}:{f.IsExtended}:{W4.Hex(f.Data)}:{f.At}"));
            cases.Add(Case("can_read_frames", ecu, result));
        }

        return cases;
    }

    public static byte[] CanRecord(int id, int length, uint at)
    {
        var record = new byte[MaxxCan.RecordLength];
        record[0] = (byte)(length & 0x0F);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), (uint)id);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(5), at);
        for (int i = 0; i < 8; i++) record[9 + i] = (byte)(0xA0 + id + i);

        return record;
    }

    private static object Case(
        string name, ModelEcu ecu, string result, string? kind = null, string? message = null,
        IReadOnlyList<string>? progress = null, int blob = 0, int retries = 0, int discards = 0) =>
        new
        {
            name,
            requests = ecu.Written.Select(w => W4.Hex(w)).ToArray(),
            result,
            error_kind = kind,
            message,
            discards,
            retries,
            progress = progress?.ToArray() ?? [],
            blob,
        };

    /// <summary>A model ECU that loses the replies to some requests (by count) and counts discards.</summary>
    private sealed class LossyEcu(int[] lose) : IEcuTransport
    {
        private int _requests;

        public ModelEcu Inner { get; } = new();

        public int Discards { get; private set; }

        public bool IsOpen => Inner.IsOpen;

        public void Open() => Inner.Open();

        public void Close() => Inner.Close();

        public void Dispose() => Inner.Dispose();

        public void Write(ReadOnlySpan<byte> data)
        {
            Inner.Write(data);
            if (lose.Contains(_requests)) Inner.DiscardInput();
            _requests++;
        }

        public int Read(Span<byte> buffer, TimeSpan timeout) => Inner.Read(buffer, timeout);

        public void DiscardInput()
        {
            Discards++;
            Inner.DiscardInput();
        }
    }
}

/// <summary>Every write scenario: what is sent, what is refused before it is sent, and how it ends.</summary>
public sealed class W4LiveWrites : IEmitter
{
    public string Package => "w4-live";

    public string Set => "maxx_writes";

    private sealed record Scenario(
        string Name, int Offset, int Length, byte Header = MaxxUsbProtocol.Ok, bool AnswerHeader = true,
        bool AnswerPayload = true);

    private static IReadOnlyList<Scenario> Scenarios()
    {
        int ceiling = MaxxTune.WriteCeiling;

        return
        [
            new("small", 1000, 4),
            new("at_zero", 0, 1),
            new("offset_255", 255, 2),
            new("max_length", 100, MaxxTune.MaximumWrite),
            new("one_too_long", 100, MaxxTune.MaximumWrite + 1),
            new("empty", 100, 0),
            new("negative_offset", -1, 4),
            new("top_of_the_tune", 0xF000, 256),
            new("ends_on_the_ceiling", ceiling - 4, 4),
            new("one_past_the_ceiling", ceiling - 3, 4),
            new("two_past_the_ceiling", ceiling - 2, 4),
            new("last_byte_allowed", ceiling - 1, 1),
            new("first_byte_refused", ceiling, 1),
            new("far_past_the_ceiling", 0xF800, 16),
            new("past_the_frame", 70000, 1),
            new("max_int_offset", int.MaxValue, 1),
            new("max_length_past_the_ceiling", ceiling - 100, 256),
            new("writer_busy", 1000, 4, Header: 0x40),
            new("out_of_range", 1000, 4, Header: 0x30),
            new("unknown_command", 1000, 4, Header: 0x20),
            new("reader_busy", 1000, 4, Header: 0x60),
            new("flash_busy", 1000, 4, Header: 0x61),
            new("unfamiliar_status", 1000, 4, Header: 0x55),
            new("status_zero", 1000, 4, Header: 0x00),
            new("status_one", 1000, 4, Header: 0x01),
            new("header_unanswered", 1000, 4, AnswerHeader: false),
            new("payload_unacknowledged", 1000, 4, AnswerPayload: false),
        ];
    }

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach (Scenario s in Scenarios())
        {
            var ecu = new ModelEcu { HeaderStatus = s.Header, AnswerHeader = s.AnswerHeader, AnswerPayload = s.AnswerPayload };
            byte[] data = [.. Enumerable.Range(0, Math.Max(0, s.Length)).Select(i => (byte)(0x30 + i + s.Offset))];

            string? status = null;
            var (kind, message) = W4.Thrown(() => status = MaxxTune.Write(ecu, s.Offset, data).ToString());

            cases.Add(new
            {
                name = s.Name,
                offset = s.Offset,
                length = s.Length,
                header = s.Header,
                answer_header = s.AnswerHeader,
                answer_payload = s.AnswerPayload,
                requests = ecu.Written.Select(w => W4.Hex(w)).ToArray(),
                status,
                status_value = status is null ? (int?)null : (int)Enum.Parse<MaxxWriteStatus>(status),
                error_kind = kind,
                message,
                tune_crc = W4.Hex(MaxxProtocol.Crc32(ecu.Tune)),
            });
        }

        return cases;
    }
}

/// <summary>Tables: found through their config struct, their record and its CRC, the pieces they go out in.</summary>
public sealed class W4LiveTables : IEmitter
{
    public string Package => "w4-live";

    public string Set => "maxx_tables";

    private sealed record Blob(string Name, byte[] Bytes, int Config, string Title);

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        // One table, shaped four ways, and the ways it is refused.
        var blobs = new List<Blob>
        {
            new("ve_16x11", W4.WithVeTable(), 1578, "VE Table 1 data"),
            new("small_6x6", W4.WithSmallTable(), 400, "Fuel ASE Table data"),
            new("ve_other_place", W4.WithVeTable(2000, 1500), 2000, "Boost Target data"),
            new("not_a_table", new byte[MaxxTune.BlobSize], 100, "Nothing data"),
            new("no_suffix", W4.WithSmallTable(), 400, "Spark Map"),
            new("upper_suffix", W4.WithSmallTable(), 400, "Spark MAP DATA"),
        };

        {
            byte[] broken = W4.WithVeTable();
            BinaryPrimitives.WriteInt16LittleEndian(broken.AsSpan(MaxxTune.TableData + 496 + 1 + (5 * 2)), -25795);
            blobs.Add(new("axis_does_not_climb", broken, 1578, "VE Table 1 data"));

            byte[] zero = W4.WithVeTable();
            zero[1578 + 0x06] = 0;
            blobs.Add(new("no_columns", zero, 1578, "VE Table 1 data"));

            byte[] wide = W4.WithVeTable();
            wide[1578 + 0x07] = 65;
            blobs.Add(new("too_many_rows", wide, 1578, "VE Table 1 data"));

            byte[] layers = W4.WithVeTable();
            layers[1578 + 0x08] = 2;
            blobs.Add(new("two_layers", layers, 1578, "VE Table 1 data"));

            byte[] past = W4.WithVeTable();
            BinaryPrimitives.WriteUInt16LittleEndian(past.AsSpan(1578 + 0x0B), 65000);
            blobs.Add(new("past_the_blob", past, 1578, "VE Table 1 data"));

            blobs.Add(new("negative_config", W4.WithVeTable(), -1, "VE Table 1 data"));
            blobs.Add(new("config_at_the_end", W4.WithVeTable(), MaxxTune.BlobSize - 20, "VE Table 1 data"));
        }

        foreach (Blob b in blobs)
        {
            MaxxTable? table = MaxxTune.TableAt(b.Bytes, b.Title, b.Config);

            if (table is null)
            {
                cases.Add(new { name = b.Name, found = false });
                continue;
            }

            int count = table.Columns * table.Rows;
            short[] cells = [.. Enumerable.Range(0, count).Select(i => (short)((i * 37) - 400))];
            byte[] record = MaxxTune.Record(b.Bytes, table, cells);
            IReadOnlyList<(int Offset, byte[] Data)> pieces = MaxxTune.WritesFor(b.Bytes, table, cells);

            cases.Add(new
            {
                name = b.Name,
                found = true,
                table_name = table.Name,
                columns = table.Columns,
                rows = table.Rows,
                x_channel = table.XChannel,
                y_channel = table.YChannel,
                x_axis_at = table.XAxisAt,
                y_axis_at = table.YAxisAt,
                cells_at = table.CellsAt,
                fits = MaxxTune.FitsInOneWrite(table),
                record = W4.Hex(record),
                record_crc = W4.Hex(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(count * 2))),
                pieces = pieces.Select(p => $"{p.Offset}:{W4.Hex(p.Data)}").ToArray(),
                wrong_size = W4.Thrown(() => MaxxTune.Record(b.Bytes, table, new short[3])).Kind,
            });
        }

        // The checksums over a tune the ECU is believed to hold, for every span a caller might ask.
        byte[] pattern = ModelEcu.Pattern();
        foreach (int covers in new[] { MaxxTune.WriteCeiling, 62180, 65536, 70000, 4096, 4097, 8192, 1, 743 })
        {
            cases.Add(new
            {
                name = $"checksums_of_{covers}",
                found = false,
                covers,
                sums = MaxxTune.ChecksumsOf(pattern, covers).Select(W4.Hex).ToArray(),
            });
        }

        cases.Add(new { name = "checksums_of_zero", found = false, covers = 0, error_kind = W4.Thrown(() => MaxxTune.ChecksumsOf(pattern, 0)).Kind });

        // The table CRC from a different pad byte and from the same cells: the pad is covered.
        {
            byte[] blob = W4.WithSmallTable();
            MaxxTable table = MaxxTune.TableAt(blob, "Fuel ASE Table data", 400)!;
            short[] cells = new short[36];

            foreach (byte pad in new byte[] { 0x00, 0x5A, 0xFF })
            {
                blob[table.CellsAt - 1] = pad;
                byte[] record = MaxxTune.Record(blob, table, cells);

                cases.Add(new
                {
                    name = $"pad_byte_{pad:X2}",
                    found = false,
                    record_crc = W4.Hex(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(72))),
                });
            }
        }

        return cases;
    }
}

/// <summary>The layout, tables and settings pages the tune is shaped into, in one pass.</summary>
public sealed class W4LiveBuild : IEmitter
{
    public string Package => "w4-live";

    public string Set => "maxx_build";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        byte[] ve = W4.WithVeTable();

        MaxxSettingDefinition[] veOnly = [new("VE Table 1 data", "dynamicTable", 1578, 22 * 22, 0.1, 0, 2500)];

        MaxxSettingDefinition[] settings =
        [
            new("IATSensor Input Voltage", "list", 100, 18, 0.001, 0, 5),
            new("IATSensor Predef", "uint16", 200, 1, 1, 0, 100),
            new("Fuel Injmethod", "uint8", 300, 1, 1, 0, 3),
            new("Fuel Trim", "int16", 310, 1, 0.1, -50, 50),
            new("Fuel Trim", "int16", 312, 1, 0.1, -50, 50),
            new("VE Table 1 data", "dynamicTable", 1578, 484, 0.1, 0, 2500),
            new("VE Table 1 X", "int16", 9000, 1, 1, -30000, 30000),
            new("Script One", "miniScript", 4000, 1, 1, double.NaN, double.NaN),
            new("Name Text", "string", 4100, 16, 1, double.NaN, double.NaN),
            new("Wide Word", "uint32", 5000, 2, 1, 0, 100000),
            new("Signed Byte", "int8", 5100, 1, 1, -100, 100),
            new("Curve", "list", 5200, 4, 1, -40, 100),
            new("Off The End", "uint16", 65535, 1, 1, 0, 1),
            new("1stSensor Thing", "uint8", 6000, 1, 1, 0, 1),
            new("zeta lower", "uint8", 6001, 1, 1, 0, 1),
            new("Alpha one", "uint8", 6002, 1, 1, 0, 1),
            new("alpha Two", "uint8", 6003, 1, 1, 0, 1),
        ];

        var channels = new Dictionary<int, MaxxChannelDefinition>
        {
            [61] = new(61, "RPM", "rpm", 1, true, 0),
            [20] = new(20, "MAP", "kPa", 0.1, true, 1),
        };

        cases.Add(Summary("ve_only", ve, veOnly, null));
        cases.Add(Summary("ve_with_channels", ve, veOnly, channels));
        cases.Add(Summary("settings_and_a_table", ve, settings, channels));

        {
            var blob = new byte[MaxxTune.BlobSize];
            short[] axis = [100, 200];
            var cells = new short[2, 2];
            W4.Compose(blob, 400, 100, 61, 20, axis, axis, cells);
            W4.Compose(blob, 500, 300, 61, 20, axis, axis, cells);

            cases.Add(Summary("tables_sharing_a_name", blob,
            [
                new MaxxSettingDefinition("Boost Table data", "dynamicTable", 400, 4, 0.1, 0, 300),
                new MaxxSettingDefinition("Boost Table data", "dynamicTable", 500, 4, 0.1, 0, 300),
            ], null));
        }

        cases.Add(Summary("a_long_subsystem", new byte[MaxxTune.BlobSize],
            [.. Enumerable.Range(0, 130).Select(i => new MaxxSettingDefinition($"GPO Thing {i}", "uint8", 1000 + i, 1, 1, 0, 255))], null));

        cases.Add(Summary("a_long_list", new byte[MaxxTune.BlobSize],
            [new MaxxSettingDefinition("Long List", "list", 1000, 64, 0.01, 0, 5), new MaxxSettingDefinition("Long List Two", "list", 1200, 64, 0.01, 0, 5)], null));

        // Which named settings a write would also touch.
        var overlap = new List<object>();
        foreach ((int offset, int length) in new[] { (100, 3), (98, 2), (99, 2), (5000, 4), (5004, 1), (21831, 50), (0, 65536), (310, 1), (311, 2), (1578, 21), (1599, 4) })
            overlap.Add(new { offset, length, hits = MaxxTune.SettingsOverlapping(settings, offset, length).ToArray() });

        cases.Add(new { name = "overlap", kind = "overlap", overlap });

        return cases;
    }

    private static object Summary(
        string name, byte[] blob, MaxxSettingDefinition[] definitions, IReadOnlyDictionary<int, MaxxChannelDefinition>? channels)
    {
        MaxxTuneModel model = MaxxTune.Build(blob, definitions, channels);

        return new
        {
            name,
            kind = "build",
            constants = model.Layout.Constants.Select(c => new
            {
                c.Name,
                c.Page,
                c.Offset,
                type = c.Type.ToString(),
                scale = Exact.Bits(c.Scale),
                c.Units,
                c.Digits,
                low = Exact.Bits(c.Low),
                high = Exact.Bits(c.High),
                c.Columns,
                c.Rows,
            }).ToArray(),
            pages = model.Layout.Pages.Select(p => new { p.Index, p.Size, p.Identifier, p.ReadCommand, p.ChunkWriteCommand, p.BurnCommand }).ToArray(),
            little_endian = model.Layout.LittleEndian,
            blocking_factor = model.Layout.BlockingFactor,
            tables = model.Tables.Select(t => new { t.Id, t.Title, t.Map, t.Values, t.XBins, t.YBins, t.XChannel, t.YChannel }).ToArray(),
            maps = model.Maps.Select(m => new { m.Name, m.Columns, m.Rows, m.XChannel, m.YChannel, m.XAxisAt, m.YAxisAt, m.CellsAt }).ToArray(),
            dialogs = model.Pages.Dialogs.Select(d => new
            {
                key = d.Key,
                d.Value.Name,
                d.Value.Title,
                d.Value.Layout,
                items = d.Value.Items.Select(i => new { kind = i.Kind.ToString(), i.Label, i.Target, i.Condition, i.Position }).ToArray(),
            }).ToArray(),
            menus = model.Pages.Menus.Select(m => new { m.Title, entries = m.Entries.Select(e => new { e.Dialog, e.Title, e.Condition }).ToArray() }).ToArray(),
        };
    }
}

/// <summary>MTune's settings definitions file, read in order with its implied addresses.</summary>
public sealed class W4LiveDefinitions : IEmitter
{
    public string Package => "w4-live";

    public string Set => "maxx_definitions";

    // LF whatever the checkout: a raw literal takes its line endings from the source file,
    // which git gives CRLF on Windows, so the committed reference would drift by platform.
    private static readonly string Xml = RawXml.ReplaceLineEndings("\n");

    private const string RawXml = """
        <ECUSettingsDefinitions version="1">
          <ECUSetting name="First"  type="uint16" length="1" address="100" scale="0.1" minValue="0" maxValue="100" />
          <ECUSetting name="Second" type="uint16" length="1" address="seq" />
          <ECUSetting name="Third"  type="uint8"  length="4" />
          <ECUSetting name="Fourth" type="uint16" length="1" />
          <ECUSetting name="Fifth"  type="uint16" length="1" address="900" />
          <Group>
            <ECUSetting name="Nested List" type="list" length="18" scale="0.001" minValue="-40.5" maxValue="125" />
            <ECUSetting name="A Table" type="dynamicTable" length="22,22" scale="0.1" minValue="0" maxValue="2500" />
            <ECUSetting name="After The Table" type="int32" />
          </Group>
          <ECUSetting name="" type="uint8" />
          <ECUSetting type="uint8" />
          <ECUSetting name="Bad Numbers" type="int16" length="x,3,-2, 5 " address=" 2000 " scale="abc" minValue="1e2" maxValue=" 7 " />
          <ECUSetting name="No Type" length="2" />
          <ECUSetting name="A String" type="string" length="16" />
          <ECUSetting name="Negative Address" type="int8" address="-4" />
          <ECUSetting name="Big Length" type="uint32" length="100000,100000" />
          <ECUSetting name="Unsigned Low" type="uint16" minValue="-5" />
          <ECUSetting name="Dotted" type="list" length="3" scale="1.5e-2" minValue=".5" maxValue="5." />
        </ECUSettingsDefinitions>
        """;

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-oracle-w4-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, Xml);

        try
        {
            IReadOnlyList<MaxxSettingDefinition> read = MaxxTuneDefinitions.Read(path);

            return
            [
                new
                {
                    name = "sample",
                    xml = Xml,
                    settings = read.Select(d => new
                    {
                        d.Name,
                        d.Kind,
                        d.Address,
                        d.Length,
                        scale = Exact.Bits(d.Scale),
                        low = Exact.Bits(d.Low),
                        high = Exact.Bits(d.High),
                        is_table = d.IsTable,
                        is_signed = d.IsSigned,
                        element_size = d.ElementSize,
                        type = d.Type.ToString(),
                        size = d.Size,
                    }).ToArray(),
                },
                new
                {
                    name = "missing",
                    xml = "",
                    settings = MaxxTuneDefinitions.Read(Path.Combine(Path.GetTempPath(), "olv-no-such-definitions.xml")).Select(d => new
                    {
                        d.Name, d.Kind, d.Address, d.Length, scale = "", low = "", high = "", is_table = false, is_signed = false, element_size = 0, type = "", size = 0,
                    }).ToArray(),
                },
            ];
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The CAN ring decode and the wrap-safe distance between two readings of the drop counter.</summary>
public sealed class W4LiveCan : IEmitter
{
    public string Package => "w4-live";

    public string Set => "maxx_can";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        // Replies built from records with every shape of length and flag.
        var rng = new Random(0x4D61);
        var reply = new List<byte>();

        foreach (byte first in new byte[] { 0x08, 0x18, 0x00, 0x03, 0x0F, 0x1F, 0x01, 0x10, 0x00 })
        {
            var record = new byte[MaxxCan.RecordLength];
            rng.NextBytes(record);
            record[0] = first;
            reply.AddRange(record);
        }

        // An all-zero slot, a zero-length frame with an identifier, and a half record on the end.
        reply.AddRange(new byte[MaxxCan.RecordLength]);
        var quiet = new byte[MaxxCan.RecordLength];
        quiet[1] = 0x23;
        quiet[2] = 0x01;
        reply.AddRange(quiet);
        reply.AddRange([1, 2, 3]);

        foreach (int take in new[] { reply.Count, 17, 34, 5, 0 })
        {
            IReadOnlyList<CanFrame> frames = MaxxCan.Frames(reply.Take(take).ToArray());

            cases.Add(new
            {
                name = $"frames_{take}",
                kind = "frames",
                reply = W4.Hex(reply.Take(take)),
                frames = frames.Select(f => new { f.Id, extended = f.IsExtended, data = W4.Hex(f.Data), f.At }).ToArray(),
                since = Array.Empty<object>(),
            });
        }

        var since = new List<object>();
        foreach ((int last, int now) in new[] { (0, 0), (10, 25), (65530, 4), (65535, 0), (0, 65535), (65535, 65535), (100, 99), (0, 65536), (70000, 5), (-1, 3) })
            since.Add(new { last, now, distance = MaxxCan.Since(last, now) });

        cases.Add(new { name = "since", kind = "since", reply = "", frames = Array.Empty<object>(), since });

        // Constants the firmware fixes.
        cases.Add(new
        {
            name = "constants",
            kind = "constants",
            reply = $"{MaxxCan.MostPerRead},{MaxxCan.RecordLength},{MaxxCan.DropCountAt},{MaxxCan.SnapshotLimit},{MaxxCan.SnapshotWindow},{MaxxTune.WriteCeiling},{MaxxTune.MaximumWrite},{MaxxTune.Piece},{MaxxTune.TableData},{MaxxTune.Preamble},{MaxxTune.TableConfigSize}",
            frames = Array.Empty<object>(),
            since = Array.Empty<object>(),
        });

        // Is the analyzer armed: from a tune and definitions.
        var armed = new List<object>();
        MaxxSettingDefinition[] with = [new("CAN Analyzer Enable", "uint8", 52658, 1, 1, 0, 1)];
        var source = new MaxxCanSource(new MaxxUsbSource(new ModelEcu()));
        foreach (byte value in new byte[] { 0, 1, 7 })
        {
            var tune = new byte[MaxxTune.BlobSize];
            tune[52658] = value;
            armed.Add(new { value, armed = source.IsArmed(tune, with), without_definitions = source.IsArmed(tune, []) });
        }

        armed.Add(new { value = 1, armed = source.IsArmed(new byte[100], with), without_definitions = false });
        cases.Add(new { name = "armed", kind = "armed", reply = "", frames = Array.Empty<object>(), since = Array.Empty<object>(), armed });

        return cases;
    }
}

/// <summary>
/// The bytes a recording is made of: the C# <c>LiveSession</c>, run over a scripted source, writes
/// its file, and the file is read back. The time column depends on the clock, so it is masked;
/// everything else (the byte-order mark, the header and units rows, number formatting, quoting,
/// line endings, empty cells) is compared exactly.
/// </summary>
public sealed class W4LiveRecording : IEmitter
{
    public string Package => "w4-live";

    public string Set => "recording";

    public const int Rows = 14;

    private sealed class Scripted(string[] names, string[] units, Func<int, double[]> row) : ILiveSource
    {
        private int _n;

        public IReadOnlyList<string> Names => names;

        public IReadOnlyList<string> Units => units;

        public IReadOnlyList<int> Digits => [.. names.Select(_ => 1)];

        public int Retries => 0;

        public void Open() { }

        public double[] Read() => row(++_n);

        public void Recover() { }

        public void Dispose() { }
    }

    private sealed record Script(string Name, string[] Names, string[] Units, Func<int, double[]> Row);

    private static IReadOnlyList<Script> Scripts() =>
    [
        new("engine",
            ["RPM", "Coolant, \"main\"", "Lambda", "Ign"],
            ["rpm", "°C", "", "°BTDC"],
            n => [1000 + (n * 37.5), 85.1 + (n * 0.1), n % 5 == 0 ? double.NaN : 0.95 + (n * 0.0001), -12.25 - n]),
        new("integers_and_extremes",
            ["A", "B", "C"],
            ["x", "y", "z"],
            n => [n, -n, n % 3 == 0 ? double.PositiveInfinity : double.MaxValue / n]),
        new("one_channel",
            ["Coolant"],
            ["°C"],
            _ => [0]),
        new("small_and_large",
            ["Tiny", "Huge", "Third"],
            ["a", "b"],
            n => [1e-7 * n, 12345678901.5 * n, 1.0 / (n + 2)]),
    ];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        foreach (Script script in Scripts())
        {
            string path = Path.Combine(Path.GetTempPath(), $"olv-oracle-w4-{Guid.NewGuid():N}.csv");

            try
            {
                using (var session = new LiveSession(
                    new Scripted(script.Names, script.Units, script.Row),
                    new LiveSessionSettings { RecordingPath = path, MaximumRate = 0 }))
                {
                    session.Start();

                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (session.RecordedRows < Rows + 5 && DateTime.UtcNow < deadline) Thread.Sleep(2);

                    session.Stop();
                }

                byte[] bytes = File.ReadAllBytes(path);

                // The head: everything up to and including the second line break.
                int breaks = 0;
                int head = 0;
                for (; head < bytes.Length && breaks < 2; head++)
                    if (bytes[head] == (byte)'\n') breaks++;

                // Line endings are written with the platform's newline, and the Rust writes CRLF on
                // every platform (the only one the C# ever ran on was Windows). So the reference
                // is normalised to CRLF, and the same file regenerates the same on any machine.
                string head_text = Encoding.UTF8.GetString(bytes, 0, head).Replace("\r\n", "\n").Replace("\n", "\r\n");
                byte[] head_bytes = Encoding.UTF8.GetBytes(head_text);

                string[] lines = Encoding.UTF8.GetString(bytes, head, bytes.Length - head)
                    .Split('\n', StringSplitOptions.None);

                // Whole lines only (the last may be empty), time masked, and exactly the rows asked for.
                string[] rows =
                [
                    .. lines.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(Rows)
                        .Select(l => "T" + l[l.IndexOf(',')..]),
                ];

                cases.Add(new
                {
                    name = script.Name,
                    names = script.Names,
                    units = script.Units,
                    head_hex = W4.Hex(head_bytes),
                    rows,
                    row_count = rows.Length,
                });
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        return cases;
    }
}

/// <summary>The MTune capture of a MaxxECU Race over USB, replayed through the source and the tune commands.</summary>
public sealed class W4LiveReplay : IEmitter
{
    public string Package => "w4-live";

    public string Set => "maxx_replay";

    /// <summary>Answers a request with the reply recorded under the next pair whose request is the same bytes.</summary>
    private sealed class Replay : IEcuTransport
    {
        private readonly List<(byte[] Request, byte[] Reply)> _pairs;
        private int _cursor;
        private byte[] _reply = [];

        public Replay(string path)
        {
            _pairs = [];

            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith('#') || line.Length == 0) continue;

                string[] halves = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (halves.Length == 2) _pairs.Add((Convert.FromHexString(halves[0]), Convert.FromHexString(halves[1])));
            }
        }

        public List<byte[]> Requests { get; } = [];

        public int Pairs => _pairs.Count;

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Dispose() => Close();

        public void DiscardInput() => _reply = [];

        public void Write(ReadOnlySpan<byte> data)
        {
            Requests.Add(data.ToArray());

            _reply = [];

            for (int i = 0; i < _pairs.Count; i++)
            {
                int at = (_cursor + i) % _pairs.Count;

                if (_pairs[at].Request.AsSpan().SequenceEqual(data))
                {
                    _cursor = at + 1;
                    _reply = _pairs[at].Reply;
                    break;
                }
            }
        }

        public int Read(Span<byte> buffer, TimeSpan timeout)
        {
            int taken = Math.Min(buffer.Length, _reply.Length);
            _reply.AsSpan(0, taken).CopyTo(buffer);
            _reply = _reply[taken..];

            return taken;
        }
    }

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        string fixture = FindFixture();
        var cases = new List<object>();

        // The source learns its channels from the recording, named by number (a definitions path
        // that does not exist, so nothing on this machine changes the answer).
        {
            var replay = new Replay(fixture);
            using var source = new MaxxUsbSource(replay, Path.Combine(Path.GetTempPath(), "olv-no-such-definitions.xml"));
            source.Open();

            cases.Add(new
            {
                name = "source_channels",
                pairs = replay.Pairs,
                ids = source.Channels.Select(c => c.Id).ToArray(),
                names = source.Names.ToArray(),
                scales = source.Channels.Select(c => Exact.Bits(c.Scale)).ToArray(),
                signed = source.Channels.Select(c => c.IsSigned).ToArray(),
                digits = source.Digits.ToArray(),
                first_requests = replay.Requests.Take(6).Select(r => W4.Hex(r)).ToArray(),
                always_logged = MaxxUsbSource.AlwaysLogged.ToArray(),
                tune_checksums = Array.Empty<string>(),
                verified = Array.Empty<string>(),
            });
        }

        // The tune commands against the recorded replies.
        {
            var replay = new Replay(fixture);
            uint[] sums = MaxxTune.Checksums(replay, 62180);
            var verified = new List<string>();

            foreach (int offset in new[] { 0xF000, 0xF100 })
            {
                byte[] request = MaxxUsbProtocol.Request(MaxxUsbProtocol.Read, MaxxTune.ReadTune, offset, 256);
                byte[] recorded = [];

                foreach (string line in File.ReadLines(fixture))
                {
                    string[] halves = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (halves.Length == 2 && Convert.FromHexString(halves[0]).AsSpan().SequenceEqual(request))
                    {
                        MaxxUsbProtocol.TryReadReply(Convert.FromHexString(halves[1]), 256, out recorded);
                        break;
                    }
                }

                var again = new Replay(fixture);
                byte[] wrong = [.. recorded];
                wrong[0] ^= 1;
                verified.Add($"{offset:X4}:{recorded.Length}:{MaxxTune.Verify(again, offset, recorded)}:{MaxxTune.Verify(new Replay(fixture), offset, wrong)}");
            }

            cases.Add(new
            {
                name = "tune_commands",
                pairs = replay.Pairs,
                ids = Array.Empty<int>(),
                names = Array.Empty<string>(),
                scales = Array.Empty<string>(),
                signed = Array.Empty<bool>(),
                digits = Array.Empty<int>(),
                first_requests = replay.Requests.Select(r => W4.Hex(r)).ToArray(),
                always_logged = Array.Empty<int>(),
                tune_checksums = sums.Select(W4.Hex).ToArray(),
                verified,
            });
        }

        return cases;
    }

    private static string FindFixture()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string path = Path.Combine(d.FullName, "tests", "OpenLogViewer.Tests", "Fixtures", "maxxecu-usb.txt");
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException("maxxecu-usb.txt not found above the oracle tool");
    }
}
