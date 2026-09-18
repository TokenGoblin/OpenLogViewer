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
/// A MaxxECU's tune, as everything above the protocol needs it.
///
/// The four parts come out of one pass because they all have to agree about
/// what things are called — see <see cref="MaxxTune.Build"/>.
/// </summary>
/// <param name="Layout">The tune model's view of it: every constant and its place.</param>
/// <param name="Tables">Which constants make up each table, for the table editor.</param>
/// <param name="Maps">
/// The tables as the ECU holds them — where the cells are, and how big. This is
/// what a write needs, and it carries the same names the layout uses.
/// </param>
/// <param name="Pages">
/// Somewhere to show the settings. Invented here rather than read from the
/// firmware, which describes none.
/// </param>
public sealed record MaxxTuneModel(
    TuneLayout Layout,
    IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<MaxxTable> Maps,
    TuneInterface Pages);

/// <summary>
/// What the ECU said about a write. The numbers are the firmware's own status
/// bytes, so an unfamiliar one can be reported as itself rather than as
/// "failed".
/// </summary>
public enum MaxxWriteStatus
{
    /// <summary>It took the bytes.</summary>
    Ok = 0x80,

    /// <summary>
    /// Nothing came back, and nothing had been sent yet.
    ///
    /// The header goes out first and is acknowledged on its own, so silence
    /// there means the payload never left. The tune is untouched.
    /// </summary>
    NoAnswer = 0,

    /// <summary>
    /// The payload went out and was never acknowledged, so <b>whether it landed
    /// is not known</b>.
    ///
    /// This is the one status that must not be reported as a failure. The ECU
    /// applies a write as it arrives and saves it itself; an acknowledgement
    /// lost on the way back looks exactly like a write that never happened, and
    /// the difference is a tune that is permanently changed. Anything receiving
    /// this has to go and look — <see cref="MaxxTune.Checksums"/> is the cheap
    /// way — before telling anybody what happened.
    /// </summary>
    Uncertain = 1,

    /// <summary>
    /// The command was not recognised. Also what the ECU answers a write to a
    /// command that does not exist, which is how MTune's handshake `0x3B` was
    /// found not to be a command at all.
    /// </summary>
    UnknownCommand = 0x20,

    /// <summary>The offset or the length is out of range.</summary>
    OutOfRange = 0x30,

    /// <summary>Refused: something else is writing.</summary>
    WriterBusy = 0x40,

    /// <summary>Refused: something else is reading.</summary>
    ReaderBusy = 0x60,

    /// <summary>Refused: the flash path is busy.</summary>
    FlashBusy = 0x61,
}

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
    /// <param name="onRetry">
    /// Called before each attempt past the first, for a caller keeping its own
    /// count — <see cref="MaxxUsbSource"/> folds these into the same
    /// <see cref="MaxxUsbSource.Retries"/> its telemetry and configuration
    /// requests count against, so a flaky tune read shows up the same way a
    /// flaky poll does rather than going unseen.
    /// </param>
    public static byte[] Read(IEcuTransport transport, Action<int, int>? progress = null, Action? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var blob = new byte[BlobSize];
        var reply = new byte[MaxxUsbProtocol.ReplyLength(Piece)];
        var timeout = TimeSpan.FromMilliseconds(600);

        for (int at = 0; at < BlobSize; at += Piece)
        {
            byte[] request = MaxxUsbProtocol.Request(MaxxUsbProtocol.Read, ReadTune, at, Piece);

            if (!MaxxUsbProtocol.TryAsk(transport, request, reply, Piece, timeout, attempts: 3, out byte[] piece, onRetry))
                throw new EcuProtocolException(
                    $"The MaxxECU stopped answering {Piece} bytes into its tune at offset {at}. "
                    + "Nothing has been changed on the ECU — this is a read.");

            piece.CopyTo(blob, at);
            progress?.Invoke(at + Piece, BlobSize);
        }

        return blob;
    }

    /// <summary>
    /// The highest offset the ECU will write to. Above this it answers 0x30.
    /// </summary>
    public const int WriteCeiling = 0xF2E7;

    /// <summary>
    /// The most the ECU will take in one write.
    ///
    /// The firmware refuses a payload of 531 or more outright. This stays at the
    /// 256 the read uses, because a write is the half with no undo and there is
    /// nothing to be gained by being close to a limit.
    /// </summary>
    public const int MaximumWrite = 256;

    /// <summary>
    /// Sends a run of bytes into the ECU's tune.
    ///
    /// <para>
    /// <b>There is no burn, and so there is no undo.</b> Every other controller
    /// this program writes to follows the MegaSquirt convention, where a write
    /// lands in working memory and is lost at the next power cycle unless it is
    /// burned — which makes turning the key off the way out of a mistake. A
    /// MaxxECU does not work that way: the write goes into the running tune with
    /// interrupts disabled and the ECU queues its own persist to an external
    /// store, so it is applied and permanent in one step. Anything calling this
    /// must have read the range first and kept the original bytes, because the
    /// ECU will not keep them and nothing else will either.
    /// </para>
    /// <para>
    /// A write is two exchanges. The header goes out and is acknowledged on its
    /// own; only then does the payload follow, with its own checksum over it. The
    /// ECU answers each with a status byte — see <see cref="MaxxWriteStatus"/>.
    /// </para>
    /// <para>
    /// Recovered from the firmware rather than from the wire: no capture of MTune
    /// writing a tune exists, so the command and the field order here are what
    /// the 1.151 dispatcher reads, cross-checked against the two writes MTune's
    /// connect handshake does send. See <c>MAXXECU_USB_PROTOCOL_RECOVERED.md</c>.
    /// </para>
    /// <para>
    /// The offset goes where a read's offset goes, which was not obvious and is
    /// now settled. The dispatcher appears to take the offset from byte 4 and the
    /// length from byte 2 — backwards — because it is not handed the packet at
    /// all: the packet handler copies the header into a state struct whose
    /// <c>+0x04</c> offset is filled from wire bytes 2–3 and whose <c>+0x02</c>
    /// length comes from bytes 4–5. Read and write then address one and the same
    /// buffer, so the addresses in MTune's definitions are write addresses too.
    /// </para>
    /// </summary>
    /// <param name="transport">An open link to the ECU.</param>
    /// <param name="offset">Where in the tune to put them.</param>
    /// <param name="data">The bytes, at most <see cref="MaximumWrite"/> of them.</param>
    /// <returns>What the ECU said about it.</returns>
    public static MaxxWriteStatus Write(IEcuTransport transport, int offset, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfZero(data.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, MaximumWrite);

        if (offset + data.Length > WriteCeiling)
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"A MaxxECU will not be written above {WriteCeiling}, and this would reach "
                + $"{offset + data.Length}.");

        var status = new byte[1];
        var timeout = TimeSpan.FromMilliseconds(600);

        transport.Write(MaxxUsbProtocol.Request(MaxxUsbProtocol.Write, ReadTune, offset, data.Length));

        if (transport.Read(status, timeout) != 1) return MaxxWriteStatus.NoAnswer;
        if (status[0] != MaxxUsbProtocol.Ok) return (MaxxWriteStatus)status[0];

        // The payload and its own checksum, which the ECU checks before it takes
        // any of it. Sent as one write so the two cannot be separated on the wire
        // by anything that reads from the same device.
        var payload = new byte[data.Length + 4];
        data.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(data.Length), MaxxProtocol.Crc32(data));

        transport.Write(payload);

        // From here the bytes are gone. A missing acknowledgement says nothing
        // about whether the ECU took them, and on this controller "took them"
        // means applied and saved — so this is Uncertain and not a failure.
        if (transport.Read(status, timeout) != 1) return MaxxWriteStatus.Uncertain;

        return status[0] == MaxxUsbProtocol.Ok ? MaxxWriteStatus.Ok : (MaxxWriteStatus)status[0];
    }

    /// <summary>
    /// Builds the bytes that put a table's cells into the ECU: the grid, and the
    /// checksum the firmware checks it against.
    ///
    /// <para>
    /// A cell cannot be written on its own. Each table record carries a CRC-32
    /// over its cells that the firmware verifies every time it evaluates the
    /// table, so cells written without it are cells the ECU has been told not to
    /// trust. The checksum covers the pad byte in front of the grid as well as
    /// the grid itself, which is why the blob is needed here and not just the new
    /// values.
    /// </para>
    /// <para>
    /// The result is written in one go at <see cref="MaxxTable.CellsAt"/>, so the
    /// ECU never sees a grid without the checksum that goes with it — but only
    /// while it fits in one write. A table whose cells run past
    /// <see cref="MaximumWrite"/> minus four cannot be sent atomically, and
    /// <see cref="FitsInOneWrite"/> is how to ask before starting.
    /// </para>
    /// </summary>
    /// <param name="blob">The tune as it currently stands.</param>
    /// <param name="table">Which table, as <see cref="TableAt"/> found it.</param>
    /// <param name="cells">
    /// The new grid, row by row with the column changing fastest — the order the
    /// ECU stores it in.
    /// </param>
    public static byte[] Record(ReadOnlySpan<byte> blob, MaxxTable table, ReadOnlySpan<short> cells)
    {
        ArgumentNullException.ThrowIfNull(table);

        int count = table.Columns * table.Rows;

        if (cells.Length != count)
            throw new ArgumentException(
                $"{table.Name} is {table.Columns} by {table.Rows}, which is {count} cells, "
                + $"and {cells.Length} were given.",
                nameof(cells));

        if (table.CellsAt < 1 || table.CellsAt + (count * 2) > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(table), "That table is not inside this tune.");

        var record = new byte[(count * 2) + 4];

        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(i * 2), cells[i]);

        // The checksum covers the byte in front of the grid as well, which is the
        // last of the nine between the axes and the cells.
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(count * 2),
            MaxxProtocol.Crc32([blob[table.CellsAt - 1], .. record.AsSpan(0, count * 2)]));

        return record;
    }

    /// <summary>
    /// Whether a table's cells and their checksum go in one write.
    ///
    /// A 6 × 6 does; a 16 × 11 VE table does not, at four bytes per cell pair
    /// plus the checksum against a 256-byte limit. It is worth knowing which,
    /// because a table sent in one write is never briefly inconsistent and one
    /// sent in several is — see <see cref="WritesFor"/>.
    /// </summary>
    public static bool FitsInOneWrite(MaxxTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return (table.Columns * table.Rows * 2) + 4 <= MaximumWrite;
    }

    /// <summary>
    /// Splits a table's record into the writes that carry it, checksum last.
    ///
    /// <para>
    /// A record longer than <see cref="MaximumWrite"/> cannot arrive at once, and
    /// between the first piece landing and the last the ECU holds a grid its
    /// stored checksum disagrees with. That was worth refusing until the
    /// firmware said what it does about it, and it turns out to do very little:
    /// the evaluator recomputes the checksum every time it evaluates a table and,
    /// on a mismatch, returns nought for that one evaluation and ticks a counter
    /// — the channels MTune calls <c>Table error counter</c> and
    /// <c>Table error last</c>. Nothing latches, nothing limps, and the table is
    /// not zeroed; the next evaluation after the last piece lands reads normally
    /// again.
    /// </para>
    /// <para>
    /// So the window is real and brief, and what matters is that it is as short
    /// as it can be and ends definitively. The checksum sits at the end of the
    /// record, so sending the pieces in order puts it in the last one: the table
    /// disagrees with itself from the first write until the last byte of the last
    /// write, and not a moment longer.
    /// </para>
    /// <para>
    /// A cold nought for one evaluation is harmless on a bench and is not
    /// something to do to an engine under load. Whoever calls this should say so
    /// to whoever is about to press the button.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(int Offset, byte[] Data)> WritesFor(
        ReadOnlySpan<byte> blob, MaxxTable table, ReadOnlySpan<short> cells)
    {
        byte[] record = Record(blob, table, cells);
        var pieces = new List<(int, byte[])>();

        for (int at = 0; at < record.Length; at += MaximumWrite)
        {
            int length = Math.Min(MaximumWrite, record.Length - at);

            pieces.Add((table.CellsAt + at, record.AsSpan(at, length).ToArray()));
        }

        return pieces;
    }

    /// <summary>
    /// How much of the tune the ECU's own checksums cover, in one 4 KB piece
    /// each.
    /// </summary>
    public const int ChecksumChunk = 4096;

    /// <summary>
    /// Asks the ECU to checksum its own tune, a 4 KB chunk at a time.
    ///
    /// <para>
    /// Command <c>0x37</c>, which is not a data read: the byte count goes in the
    /// <em>offset</em> field, and what comes back is one CRC-32 per 4 KB of the
    /// live tune — the same tune <c>0x05</c> reads, at the same base. MTune asks
    /// it to cover 62,180 bytes, which is sixteen chunks and the 64 bytes its
    /// capture showed.
    /// </para>
    /// <para>
    /// What it is for here is checking a write. Reading the range back says the
    /// bytes that were sent arrived; this says nothing else moved, anywhere in
    /// the tune, which on a controller with no undo is the more useful question
    /// — and it costs one exchange rather than 256.
    /// </para>
    /// <para>
    /// It reads the <b>live</b> tune, so a table sent in several writes will not
    /// match anything until the last piece has landed. Ask afterwards, not
    /// between.
    /// </para>
    /// </summary>
    /// <returns>One checksum per chunk, or empty if the ECU did not answer.</returns>
    public static uint[] Checksums(IEcuTransport transport, int covers = WriteCeiling)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(covers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(covers, ushort.MaxValue);

        int chunks = ((covers + ChecksumChunk - 1) / ChecksumChunk) * 4;
        var reply = new byte[MaxxUsbProtocol.ReplyLength(chunks)];

        transport.Write(MaxxUsbProtocol.Request(MaxxUsbProtocol.Read, 0x37, covers, chunks));

        if (transport.Read(reply, TimeSpan.FromMilliseconds(600)) != reply.Length
            || !MaxxUsbProtocol.TryReadReply(reply, chunks, out byte[] data))
        {
            // Whatever did arrive is left in the driver's buffer otherwise, and
            // the next request reads its reply starting in the middle of this
            // one. That turns a successful write into "the ECU read back
            // something else", which is a false alarm about the worst thing this
            // program can report.
            transport.DiscardInput();

            return [];
        }

        var checksums = new uint[chunks / 4];

        for (int i = 0; i < checksums.Length; i++)
            checksums[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));

        return checksums;
    }

    /// <summary>
    /// The same checksums, worked out here, for a tune we believe the ECU has.
    ///
    /// Confirmed against a bench Race: all sixteen agreed with the ECU's own,
    /// including the last chunk, which is short rather than a whole 4,096 and so
    /// is not something that agrees by chance. That check covered the 62,180
    /// bytes MTune asks for, where the last chunk is 740; the default here is the
    /// write ceiling, 62,183, where it is 743.
    /// </summary>
    public static uint[] ChecksumsOf(ReadOnlySpan<byte> blob, int covers = WriteCeiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(covers);

        if (covers > blob.Length) covers = blob.Length;

        var checksums = new uint[(covers + ChecksumChunk - 1) / ChecksumChunk];

        for (int i = 0; i < checksums.Length; i++)
        {
            int at = i * ChecksumChunk;

            checksums[i] = MaxxProtocol.Crc32(blob.Slice(at, Math.Min(ChecksumChunk, covers - at)));
        }

        return checksums;
    }

    /// <summary>
    /// Reads back what was just written and says whether it took.
    ///
    /// Worth doing on every write and not only when something looks wrong. The
    /// ECU acknowledging a write says it accepted the bytes, not that they are
    /// what is now in the tune — and on a controller with no burn to withhold,
    /// reading back is the only confirmation there is.
    /// </summary>
    public static bool Verify(IEcuTransport transport, int offset, ReadOnlySpan<byte> expected)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var reply = new byte[MaxxUsbProtocol.ReplyLength(expected.Length)];
        var timeout = TimeSpan.FromMilliseconds(600);

        // Start from a quiet line. This is called after a write, and after
        // something has already gone wrong often enough to be worth assuming:
        // a stray byte left over from an earlier exchange shifts this reply and
        // reports the tune as wrong when it is right.
        transport.DiscardInput();

        transport.Write(MaxxUsbProtocol.Request(
            MaxxUsbProtocol.Read, ReadTune, offset, expected.Length));

        return transport.Read(reply, timeout) == reply.Length
               && MaxxUsbProtocol.TryReadReply(reply, expected.Length, out byte[] got)
               && got.AsSpan().SequenceEqual(expected);
    }

    /// <summary>
    /// Bytes a <c>dynamicTable</c> entry's config struct occupies at its own
    /// address — not the cells, which live elsewhere in the blob and are
    /// found through the struct rather than stored where it is. Shared with
    /// <see cref="MaxxSettingDefinition.Size"/>, which is what a table
    /// actually needs to advance the definitions file's implicit-address
    /// cursor past — the struct that is really there, not the cell grid.
    /// </summary>
    public const int TableConfigSize = 21;

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
        if (config < 0 || config + TableConfigSize > blob.Length) return null;

        int columns = blob[config + 0x06];
        int rows = blob[config + 0x07];
        int layers = blob[config + 0x08];

        if (columns is < 1 or > 64 || rows is < 1 or > 64 || layers > 1) return null;

        int record = TableData + BinaryPrimitives.ReadUInt16LittleEndian(blob[(config + 0x0B)..]);
        int x = record + 1;
        int y = x + (columns * 2);
        int cells = y + (rows * 2) + Preamble;

        if (cells + (columns * rows * 2) > blob.Length) return null;

        // Both axes have to climb, or this is not a table.
        //
        // The counts and the data offset come out of a struct read at an address
        // the definitions gave, and two thirds of those addresses are not stated
        // in the file — they follow on from whatever was declared before, so a
        // single drift puts a struct on bytes that are not one. What comes back
        // then is not obviously wrong: a plausible size, an offset inside the
        // blob, and a grid that draws. On this ECU it produced a "VVT Intake PID
        // D Gain Table" of 15 by 39 whose columns ran 1,000 … 8,000 and then
        // 0, 27, −25,795.
        //
        // Breakpoints are the check because a tuning axis is a series of
        // increasing thresholds — RPM, pressure, temperature, seconds — and
        // nothing indexes a table by a number that goes backwards. It costs the
        // eleven unused tables that sit at two by two with zero axes, which are
        // not worth a tab either.
        if (!Climbs(blob, x, columns) || !Climbs(blob, y, rows)) return null;

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
    /// Whether a run of breakpoints strictly increases. One of them always does.
    /// </summary>
    private static bool Climbs(ReadOnlySpan<byte> blob, int at, int count)
    {
        for (int i = 1; i < count; i++)
            if (BinaryPrimitives.ReadInt16LittleEndian(blob[(at + (i * 2))..])
                <= BinaryPrimitives.ReadInt16LittleEndian(blob[(at + ((i - 1) * 2))..]))
                return false;

        return true;
    }

    /// <summary>
    /// Every named setting whose declared bytes overlap writing
    /// <paramref name="length"/> bytes at <paramref name="offset"/>. Empty
    /// where nothing does.
    ///
    /// <para>
    /// A table's cell address comes from the ECU's own live config struct
    /// (<see cref="TableAt"/>), while every setting's address comes from the
    /// definitions file's implicit cursor (<see cref="MaxxTuneDefinitions.Read"/>)
    /// — two independent mechanisms that never cross-check each other, because
    /// neither one is aware the other exists. <see cref="Climbs"/> catches one
    /// symptom of a table resolving to memory that is not really its own —
    /// breakpoints that do not increase — but a table can resolve to bytes that
    /// pass that check and still collide: found on a real ECU, where an
    /// unconfigured 5×5 user table's cells began one byte into a completely
    /// unrelated expansion-module setting's own declared address.
    /// </para>
    /// <para>
    /// <b>Reported, not refused.</b> An earlier version of this refused any
    /// such write outright, and excluded "Expmod " settings from the check —
    /// the definitions file declares all 1,201 of them whether or not any
    /// module is actually installed, and on a bench Race with none, three
    /// separate real tables collided with one. That exclusion was not
    /// enough: a second, unrelated pattern ("USERAIN ", user-configurable
    /// analog inputs — also declared whether or not anything is wired to
    /// them) immediately produced the same false refusal on VE Table 1.
    /// Nothing here can reliably tell "this setting is inert because its
    /// hardware is not installed" from "this setting matters" by name alone,
    /// and a growing exclusion list built one discovered pattern at a time is
    /// not a guardrail anyone should trust. What every caller actually needs
    /// is to know what a write would also touch and judge for itself — a
    /// human reading a sent-cell confirmation, or an agent deciding whether
    /// to ask before touching that table again.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SettingsOverlapping(
        IReadOnlyList<MaxxSettingDefinition> definitions, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var hits = new List<string>();

        foreach (MaxxSettingDefinition d in definitions)
        {
            if (d.Kind is "miniScript" or "userScript" or "string" or "table") continue;
            if (offset < d.Address + d.Size && offset + length > d.Address) hits.Add(d.Name);
        }

        return hits;
    }

    /// <summary>
    /// Builds the layout, the table list, the tables as the ECU holds them and
    /// the settings pages — all in one pass, because they all have to agree
    /// about names.
    ///
    /// <para>
    /// Depends on the blob, which a layout normally does not: a table's shape and
    /// the whereabouts of its cells are in the ECU rather than in the
    /// definitions, so the layout describes this tune as it is now rather than
    /// the firmware in general. Reading the tune again rebuilds it.
    /// </para>
    /// <para>
    /// One pass and not three because a constant is found by name and the names
    /// are not all distinct in the file. Working them out separately gave three
    /// different answers to what a thing is called: a table that had to be
    /// renamed for the layout kept its original name in the list used to send it,
    /// so it could never be sent; and a settings page referred to a name the
    /// layout had given to something else, so its fields read another setting's
    /// bytes. Whatever a thing is called here, it is called that everywhere.
    /// </para>
    /// </summary>
    public static MaxxTuneModel Build(
        ReadOnlySpan<byte> blob,
        IReadOnlyList<MaxxSettingDefinition> definitions,
        IReadOnlyDictionary<int, MaxxChannelDefinition>? channels = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var constants = new List<TuneConstant>();
        var tables = new List<TableDefinition>();
        var maps = new List<MaxxTable>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new SettingsPages();

        foreach (MaxxSettingDefinition definition in definitions)
        {
            if (definition.IsTable)
            {
                if (TableAt(blob, definition.Name, definition.Address) is not { } table) continue;

                string name = Unique(table.Name, taken);

                // The table under the name everything else will use for it.
                maps.Add(table with { Name = name });

                // The axis names go through the same claim as the table's own.
                // They are constants like any other and are looked up by name, so
                // a setting the definitions happen to call "VE Table 1 X" would
                // otherwise be declared later and quietly replace the axis — and
                // the table would read its breakpoints out of that setting's
                // bytes, at that setting's scale.
                string x = Unique($"{name} X", taken);
                string y = Unique($"{name} Y", taken);

                constants.Add(Axis(x, table.XAxisAt, table.Columns, table.XChannel, channels));
                constants.Add(Axis(y, table.YAxisAt, table.Rows, table.YChannel, channels));

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
                    XBins = x,
                    YBins = y,
                    XChannel = ChannelName(table.XChannel, channels),
                    YChannel = ChannelName(table.YChannel, channels),
                });

                continue;
            }

            // Scripts and text are not numbers and have no place among settings
            // that can be edited as such.
            if (definition.Kind is "miniScript" or "userScript" or "string" or "table") continue;
            if (definition.Address + definition.Size > BlobSize) continue;

            string setting = Unique(definition.Name, taken);

            constants.Add(new TuneConstant
            {
                Name = setting,
                Page = 0,
                Offset = definition.Address,
                Type = definition.Type,
                Scale = definition.Scale,
                Digits = MaxxChannelDefinitions.DigitsFor(definition.Scale),
                Low = definition.Low,
                High = definition.High,
                Columns = definition.Length,
            });

            // The page this setting goes on, under the name the layout just gave
            // it rather than the one the file used.
            pages.Add(definition, setting);
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

        return new MaxxTuneModel(layout, tables, maps, pages.Build());
    }

    /// <summary>
    /// Settings to a page, at most.
    ///
    /// MTune's definitions have no pages, so these are invented — and an
    /// invented page has to be short. The largest subsystem here declares 1,201
    /// settings and four more declare over 500, which as one page each is a
    /// scroll nobody finds anything in and a list nobody wants built.
    /// </summary>
    private const int PerPage = 60;

    /// <summary>
    /// Invents a settings interface for a MaxxECU, so its settings can be looked
    /// at rather than only decoded.
    ///
    /// <para>
    /// Everything else here reads the interface from the firmware: an INI says
    /// which dialog holds which field, what to call it and when it applies.
    /// MTune's definitions say none of that — a name, a type, a scale, a range
    /// and an address, and nothing about presentation. So reading a MaxxECU's
    /// tune left 8,752 settings decoded, addressable by name, and reachable by
    /// nobody.
    /// </para>
    /// <para>
    /// What the names do carry is the subsystem, as the first word:
    /// <c>IATSensor</c>, <c>Fuel</c>, <c>Ign</c>, <c>Boost</c>. That is enough to
    /// group them, and the file is written subsystem by subsystem, so keeping the
    /// declared order keeps related settings together inside a group as well.
    /// Pages break when the subsystem changes or when one gets long, and the
    /// menus are initial letters, because 298 subsystems is too many headings for
    /// a list with no filter on it.
    /// </para>
    /// <para>
    /// None of this is the firmware's opinion and it should not pretend to be. It
    /// is a way of finding a setting, not a tuning workflow: MTune's own pages
    /// group by what somebody is doing, and nothing in these files says what that
    /// grouping is.
    /// </para>
    /// </summary>
    private sealed class SettingsPages
    {
        private readonly Dictionary<string, TuneDialog> _dialogs = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedDictionary<string, List<MenuEntry>> _byLetter = new(StringComparer.Ordinal);
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DialogItem> _items = [];

        private string _group = "";
        private int _part = 1;

        /// <summary>
        /// Puts a setting on a page, under the name the layout gave it.
        /// </summary>
        /// <param name="definition">What the file says about it.</param>
        /// <param name="name">
        /// What the layout calls the constant — which is not always what the file
        /// calls the setting, because the file repeats a few names and a constant
        /// is found by name. Referring to the file's name here would point a
        /// field at whatever else claimed it.
        /// </param>
        public void Add(MaxxSettingDefinition definition, string name)
        {
            string prefix = Prefix(definition.Name);

            if (!prefix.Equals(_group, StringComparison.OrdinalIgnoreCase))
            {
                Finish();
                _group = prefix;
                _part = 1;
            }
            else if (_items.Count >= PerPage) Finish();

            // A list gets a row per point rather than one row.
            //
            // A field with no subscript addresses the first element, so an
            // eighteen-point sensor calibration would appear as a single box
            // reading 4.660 — which is not the setting, it is a fifth of one of
            // its points, with nothing to say so. There are only 290 lists here
            // and none is longer than 64, so every point can simply be shown.
            if (definition.Length > 1)
            {
                for (int i = 0; i < definition.Length; i++)
                {
                    if (_items.Count >= PerPage) Finish();

                    _items.Add(new DialogItem(
                        DialogItemKind.Field, $"{name} [{i}]", $"{name}[{i}]"));
                }

                return;
            }

            _items.Add(new DialogItem(DialogItemKind.Field, name, name));
        }

        public TuneInterface Build()
        {
            Finish();

            return new TuneInterface
            {
                Menus = [.. _byLetter.Select(letter => new TuneMenu(letter.Key, letter.Value))],
                Dialogs = _dialogs,
            };
        }

        private void Finish()
        {
            if (_items.Count == 0) return;

            // Numbered only where a subsystem needed more than one page, so one
            // that fits is not called "Fuel 1" for no reason.
            string title = Unique(_part > 1 ? $"{_group} {_part}" : _group, _taken);
            string name = $"maxx:{title}";

            _dialogs[name] = new TuneDialog(name, title, "yAxis", [.. _items]);

            string letter = char.IsLetter(_group[0])
                ? char.ToUpperInvariant(_group[0]).ToString()
                : "#";

            if (!_byLetter.TryGetValue(letter, out List<MenuEntry>? entries))
                _byLetter[letter] = entries = [];

            entries.Add(new MenuEntry(name, title));
            _items.Clear();
            _part++;
        }
    }

    /// <summary>The subsystem a setting belongs to: the first word of its name.</summary>
    private static string Prefix(string name)
    {
        int space = name.IndexOf(' ', StringComparison.Ordinal);

        return space > 0 ? name[..space] : name;
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
