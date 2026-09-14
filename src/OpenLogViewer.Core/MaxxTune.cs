using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// One table as the ECU currently has it: where its axes and cells are, and how
/// big they are right now.
/// </summary>
/// <param name="Name">MTune's name for it, with the trailing "data" taken off.</param>
/// <param name="Columns">Cells across, which is the X axis length.</param>
/// <param name="Rows">Cells down.</param>
/// <param name="XChannel">The channel id the ECU indexes the columns by.</param>
/// <param name="YChannel">The same for the rows.</param>
/// <param name="XAxisAt">Offset of the first X breakpoint in the blob.</param>
/// <param name="YAxisAt">Offset of the first Y breakpoint.</param>
/// <param name="CellsAt">Offset of the first cell.</param>
public sealed record MaxxTable(
    string Name,
    int Columns,
    int Rows,
    int XChannel,
    int YChannel,
    int XAxisAt,
    int YAxisAt,
    int CellsAt);

/// <summary>
/// A MaxxECU's tune, read off the ECU over USB and shaped into the same
/// <see cref="TuneLayout"/> everything else here already works on.
///
/// The shaping is the point. A MaxxECU has no INI and its tune is not paged, so
/// the obvious approach would be a MaxxECU-shaped tune object beside the
/// existing one — and then a MaxxECU-shaped table view, compare, export and
/// edit beside each of those. Handing back a layout instead means the tables,
/// the heat colouring, the curve editor, comparing against a file and everything
/// else are the code that was already there, and a MaxxECU is simply another
/// ECU with a tune in it.
///
/// What the blob is, and how it is read, is in
/// <c>MAXXECU_USB_PROTOCOL_RECOVERED.md</c> and
/// <c>MAXXECU_TUNE_BLOB_DECODING.md</c> in the TCU repository. The short of it:
/// command <c>0x05</c> returns all 64 KB, 256 bytes at a time; settings sit at
/// the addresses MTune's definitions give; and each table's real shape is in a
/// 21-byte config struct at the address the definitions give for it, which
/// points at a record holding the axes and the cells.
/// </summary>
public static class MaxxTune
{
    /// <summary>The command that reads the tune.</summary>
    public const byte ReadTune = 0x05;

    /// <summary>
    /// The longest read it will answer.
    ///
    /// Not the protocol's 512. A 512-byte read of this command is refused
    /// outright, which is indistinguishable from the command not existing — and
    /// is why the tune looked unreachable for a while.
    /// </summary>
    public const int Piece = 256;

    /// <summary>How much of it there is.</summary>
    public const int BlobSize = 65536;

    /// <summary>
    /// Where table data begins, relative to the blob.
    ///
    /// The firmware's table-data base minus its tune base — 0x20010D51 −
    /// 0x2000C974. A table's <c>data offset</c> is counted from here.
    /// </summary>
    public const int TableData = 0x43DD;

    /// <summary>
    /// Bytes between a table's axes and its cells: two zero bytes, a u16 whose
    /// meaning is not known, a CRC-32 over everything before it, and one byte
    /// that belongs to the checksum after the cells.
    ///
    /// The single most important number here. Walk straight from the axes into
    /// the grid — which is what the firmware's evaluator reads like — and every
    /// cell is out by four and a half cells, which does not look wrong. It looks
    /// like a VE table with implausible numbers in it.
    /// </summary>
    public const int Preamble = 9;

    /// <summary>
    /// The one page a MaxxECU has, which is the whole blob.
    ///
    /// The identifier and the command templates are empty because they are
    /// TunerStudio's way of asking for a page and a MaxxECU is not asked that
    /// way — <see cref="Read"/> does the asking. The burn command is empty for a
    /// better reason: this ECU has no burn. A write to it is applied to the
    /// running tune and persisted by the ECU itself in one step, so there is
    /// nothing to send afterwards and nothing to withhold.
    /// </summary>
    public static TunePage Page { get; } = new()
    {
        Index = 0,
        Size = BlobSize,
        Identifier = "",
        ReadCommand = "",
    };

    /// <summary>
    /// Reads the whole blob.
    ///
    /// Against a bench Race this takes about four seconds and comes back
    /// identically on a second read, which is how it was told apart from the
    /// regions that look like settings and are live data.
    /// </summary>
    /// <param name="transport">An open link to the ECU.</param>
    /// <param name="progress">Called with bytes read and bytes total.</param>
    public static byte[] Read(IEcuTransport transport, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var blob = new byte[BlobSize];
        var reply = new byte[MaxxUsbProtocol.ReplyLength(Piece)];
        var timeout = TimeSpan.FromMilliseconds(600);

        for (int at = 0; at < BlobSize; at += Piece)
        {
            byte[]? piece = null;

            for (int attempt = 0; attempt < 3 && piece is null; attempt++)
            {
                if (attempt > 0) transport.DiscardInput();

                transport.Write(MaxxUsbProtocol.Request(MaxxUsbProtocol.Read, ReadTune, at, Piece));

                int got = transport.Read(reply, timeout);

                if (got == reply.Length
                    && MaxxUsbProtocol.TryReadReply(reply, Piece, out byte[] data))
                    piece = data;
            }

            if (piece is null)
                throw new EcuProtocolException(
                    $"The MaxxECU stopped answering {Piece} bytes into its tune at offset {at}. "
                    + "Nothing has been changed on the ECU — this is a read.");

            piece.CopyTo(blob, at);
            progress?.Invoke(at + Piece, BlobSize);
        }

        return blob;
    }

    /// <summary>
    /// Works out a table's current shape from its 21-byte config struct.
    ///
    /// The struct is where the per-tune part lives: MTune's definitions say
    /// where each table's struct is and how big it may get, and the struct says
    /// how big it actually is and where its data went. Returns null for a table
    /// this ECU is not using, which is what counts outside 1..64 mean.
    /// </summary>
    public static MaxxTable? TableAt(ReadOnlySpan<byte> blob, string name, int config)
    {
        if (config < 0 || config + 21 > blob.Length) return null;

        int columns = blob[config + 0x06];
        int rows = blob[config + 0x07];
        int layers = blob[config + 0x08];

        if (columns is < 1 or > 64 || rows is < 1 or > 64 || layers > 1) return null;

        int record = TableData + BinaryPrimitives.ReadUInt16LittleEndian(blob[(config + 0x0B)..]);
        int x = record + 1;
        int y = x + (columns * 2);
        int cells = y + (rows * 2) + Preamble;

        if (cells + (columns * rows * 2) > blob.Length) return null;

        return new MaxxTable(
            Display(name),
            columns,
            rows,
            BinaryPrimitives.ReadUInt16LittleEndian(blob[config..]) & 0x7FF,
            BinaryPrimitives.ReadUInt16LittleEndian(blob[(config + 0x02)..]) & 0x7FF,
            x,
            y,
            cells);
    }

    /// <summary>
    /// Every table this ECU is using.
    /// </summary>
    public static IReadOnlyList<MaxxTable> Tables(
        ReadOnlySpan<byte> blob, IReadOnlyList<MaxxSettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var found = new List<MaxxTable>();

        foreach (MaxxSettingDefinition definition in definitions)
        {
            if (!definition.IsTable) continue;
            if (TableAt(blob, definition.Name, definition.Address) is { } table) found.Add(table);
        }

        return found;
    }

    /// <summary>
    /// Builds the layout, the table list and the names to show them under.
    ///
    /// Depends on the blob, which a layout normally does not: a table's shape and
    /// the whereabouts of its cells are in the ECU rather than in the
    /// definitions, so the layout describes this tune as it is now rather than
    /// the firmware in general. Reading the tune again rebuilds it.
    /// </summary>
    public static (TuneLayout Layout, IReadOnlyList<TableDefinition> Tables) Build(
        ReadOnlySpan<byte> blob,
        IReadOnlyList<MaxxSettingDefinition> definitions,
        IReadOnlyDictionary<int, MaxxChannelDefinition>? channels = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var constants = new List<TuneConstant>();
        var tables = new List<TableDefinition>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (MaxxSettingDefinition definition in definitions)
        {
            if (definition.IsTable)
            {
                if (TableAt(blob, definition.Name, definition.Address) is not { } table) continue;

                string name = Unique(table.Name, taken);

                constants.Add(Axis($"{name} X", table.XAxisAt, table.Columns, table.XChannel, channels));
                constants.Add(Axis($"{name} Y", table.YAxisAt, table.Rows, table.YChannel, channels));

                constants.Add(new TuneConstant
                {
                    Name = name,
                    Page = 0,
                    Offset = table.CellsAt,
                    Type = RealtimeType.S16,
                    Scale = definition.Scale,
                    Digits = MaxxChannelDefinitions.DigitsFor(definition.Scale),
                    Low = definition.Low,
                    High = definition.High,
                    Columns = table.Columns,
                    Rows = table.Rows,
                });

                tables.Add(new TableDefinition
                {
                    Id = name,
                    Title = name,
                    Values = name,
                    XBins = $"{name} X",
                    YBins = $"{name} Y",
                    XChannel = ChannelName(table.XChannel, channels),
                    YChannel = ChannelName(table.YChannel, channels),
                });

                continue;
            }

            // Scripts and text are not numbers and have no place among settings
            // that can be edited as such.
            if (definition.Kind is "miniScript" or "userScript" or "string" or "table") continue;
            if (definition.Address + definition.Size > BlobSize) continue;

            constants.Add(new TuneConstant
            {
                Name = Unique(definition.Name, taken),
                Page = 0,
                Offset = definition.Address,
                Type = definition.Type,
                Scale = definition.Scale,
                Digits = MaxxChannelDefinitions.DigitsFor(definition.Scale),
                Low = definition.Low,
                High = definition.High,
                Columns = definition.Length,
            });
        }

        var layout = new TuneLayout
        {
            Pages = [Page],
            Constants = constants,
            LittleEndian = true,

            // A MaxxECU is written a range at a time rather than a page at a
            // time, and the firmware caps a write payload at 531 bytes.
            BlockingFactor = 512,
        };

        return (layout, tables);
    }

    /// <summary>
    /// An axis — a row of breakpoints counted in whatever the channel on that
    /// axis is counted in.
    ///
    /// Which is not the table's own scale, and that is the trap: a VE table is
    /// scaled by a tenth because its cells are percent, while its columns are
    /// RPM and stored whole. Scale the axis like the table and a 5,000 RPM
    /// breakpoint reads 500. The channel the ECU indexes the axis by is in the
    /// config struct, and MTune's realtime definitions give that channel's own
    /// scale and units — RPM at 1, MAP at a tenth — so the axis is scaled by the
    /// thing it actually measures.
    /// </summary>
    private static TuneConstant Axis(
        string name,
        int offset,
        int count,
        int channel,
        IReadOnlyDictionary<int, MaxxChannelDefinition>? channels)
    {
        MaxxChannelDefinition? measured = channels?.GetValueOrDefault(channel);

        return new TuneConstant
        {
            Name = name,
            Page = 0,
            Offset = offset,
            Type = RealtimeType.S16,
            Scale = measured?.Scale ?? 1,
            Units = measured?.Units ?? "",
            Digits = measured?.Digits ?? 0,
            Columns = count,
        };
    }

    /// <summary>
    /// What to call a table.
    ///
    /// MTune names the thing that holds the numbers — "VE Table 1 data" — which
    /// is accurate about the file and wrong on a tab.
    /// </summary>
    private static string Display(string name) =>
        name.EndsWith(" data", StringComparison.OrdinalIgnoreCase)
            ? name[..^" data".Length]
            : name;

    /// <summary>
    /// Keeps names distinct.
    ///
    /// The definitions repeat a few, and a constant is looked up by name — so a
    /// repeat would quietly shadow the first and put one table's numbers under
    /// another's title.
    /// </summary>
    private static string Unique(string name, HashSet<string> taken)
    {
        if (taken.Add(name)) return name;

        for (int n = 2; ; n++)
        {
            string candidate = $"{name} ({n})";
            if (taken.Add(candidate)) return candidate;
        }
    }

    /// <summary>
    /// What the ECU indexes an axis by, named from the realtime definitions.
    ///
    /// The same numbering the telemetry uses, so a table's axis and a logged
    /// channel that share an id are the same quantity — which is what lets a log
    /// be laid over a table.
    /// </summary>
    private static string ChannelName(
        int id, IReadOnlyDictionary<int, MaxxChannelDefinition>? channels) =>
        channels?.GetValueOrDefault(id)?.Name ?? "";
}
