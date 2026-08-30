using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>Bytes destined for a place in the ECU's memory.</summary>
public sealed record TuneWrite(int Page, int Offset, byte[] Data);

/// <summary>
/// The tune as it exists in the ECU right now, read out over the wire.
///
/// Better than a file for the job it does. A saved <c>.msq</c> is whatever was
/// last written to disk, which on a bench that has been poked at is not
/// necessarily what the controller is running; and rusEFI's own saved tunes
/// carry no tables at all, so for that firmware there is no file to fall back
/// on. Reading the pages settles it.
///
/// Nothing here reaches the ECU. This decodes the pages, encodes edited values
/// back into bytes, and records a write in the copy held here once one has been
/// sent and read back. Sending is <see cref="EcuConnection"/>'s job, and keeping
/// the two apart is what lets an edit be built and checked without anything
/// having left the machine yet.
/// </summary>
public sealed class EcuTune
{
    private readonly Dictionary<string, TuneConstant> _byName;
    private readonly byte[][] _pages;
    private readonly bool _little;

    private EcuTune(TuneLayout layout, byte[][] pages)
    {
        Layout = layout;
        _pages = pages;
        _little = layout.LittleEndian;

        _byName = new Dictionary<string, TuneConstant>(StringComparer.OrdinalIgnoreCase);

        // Later definitions win, matching how the rest of an INI is read.
        foreach (TuneConstant constant in layout.Constants) _byName[constant.Name] = constant;
    }

    public TuneLayout Layout { get; }

    /// <summary>The raw page images, in page order.</summary>
    public IReadOnlyList<byte[]> Pages => _pages;

    /// <summary>
    /// Reads every page from a connected ECU.
    ///
    /// <paramref name="progress"/> is reported in bytes, because this is not
    /// instant — rusEFI's settings are 22,960 bytes across 23 reads.
    /// </summary>
    public static EcuTune Read(EcuConnection connection, TuneLayout layout, Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Pages.Count == 0)
            throw new EcuProtocolException("This firmware definition declares no settings pages.");

        var images = new byte[layout.Pages.Count][];
        int done = 0;
        int total = layout.TotalSize;

        for (int i = 0; i < layout.Pages.Count; i++)
        {
            TunePage page = layout.Pages[i];

            images[i] = connection.ReadTunePage(page, layout.BlockingFactor, layout.LittleEndian, read =>
            {
                progress?.Invoke(done + read, total);
            });

            done += page.Size;
            progress?.Invoke(done, total);
        }

        return new EcuTune(layout, images);
    }

    /// <summary>Builds one from page images already in hand, for a test or a saved capture.</summary>
    public static EcuTune FromPages(TuneLayout layout, params byte[][] pages) => new(layout, pages);

    /// <summary>Every scalar and bit field, by name — what an expression resolves against.</summary>
    public IReadOnlyDictionary<string, double> Scalars()
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (TuneConstant constant in Layout.Constants)
        {
            if (constant.IsArray) continue;
            if (Value(constant, 0) is { } value && !double.IsNaN(value)) values[constant.Name] = value;
        }

        return values;
    }

    /// <summary>One setting by name, or NaN when this firmware has no such thing.</summary>
    public double Scalar(string name) =>
        _byName.TryGetValue(name, out TuneConstant? constant) && !constant.IsArray
            ? Value(constant, 0) ?? double.NaN
            : double.NaN;

    /// <summary>An array's values in declaration order, or null when there is no such array.</summary>
    public double[]? Array(string name)
    {
        if (!_byName.TryGetValue(name, out TuneConstant? constant)) return null;

        int count = constant.Columns * constant.Rows;
        var values = new double[count];

        for (int i = 0; i < count; i++)
        {
            if (Value(constant, i) is not { } value) return null;
            values[i] = value;
        }

        return values;
    }

    /// <summary>
    /// A table with its axes, ready for the heat view and VE Calibration.
    ///
    /// Rows and columns follow the INI's own <c>[a x b]</c>: the first dimension
    /// runs along the X axis. Getting this the wrong way round transposes the
    /// table, which for a square one is silent.
    /// </summary>
    public TuneTable? Table(string name, string valuesConstant, string xConstant, string yConstant)
    {
        if (!_byName.TryGetValue(valuesConstant, out TuneConstant? values)) return null;

        double[]? cells = Array(valuesConstant);
        double[]? x = Array(xConstant);
        double[]? y = Array(yConstant);

        if (cells is null || x is null || y is null) return null;
        if (x.Length != values.Columns || y.Length != values.Rows) return null;

        var grid = new double[values.Columns, values.Rows];
        for (int row = 0; row < values.Rows; row++)
            for (int column = 0; column < values.Columns; column++)
                grid[column, row] = cells[row * values.Columns + column];

        _byName.TryGetValue(xConstant, out TuneConstant? xc);
        _byName.TryGetValue(yConstant, out TuneConstant? yc);

        return new TuneTable(
            name,
            new TuneAxis(xConstant, xc?.Units ?? "", x),
            new TuneAxis(yConstant, yc?.Units ?? "", y),
            grid,
            values.Units);
    }

    /// <summary>
    /// Every table the firmware offers that this tune can actually produce.
    ///
    /// Definitions that name a constant the layout does not have, or whose axes
    /// do not match the shape of the values, are left out rather than guessed
    /// at — a table assembled from mismatched parts is not a near miss.
    /// </summary>
    public IReadOnlyList<TuneTable> Tables(IEnumerable<TableDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var tables = new List<TuneTable>();

        foreach (TableDefinition definition in definitions)
            if (Table(definition.Title, definition.Values, definition.XBins, definition.YBins) is { } table)
                tables.Add(table);

        return tables;
    }

    /// <summary>
    /// Turns values back into the bytes an array occupies, ready to be written.
    ///
    /// The inverse of reading, and it has to round rather than truncate: a VE
    /// cell of 84.7 at a scale of 0.1 is 847, and truncating the 846.9999 that
    /// floating point actually produces would quietly drop a tenth off every
    /// cell in the table.
    ///
    /// Returns null when this firmware has no such array, when the count is
    /// wrong, or when a value will not fit the type — all of which would
    /// otherwise write something plausible into the wrong place.
    /// </summary>
    /// <summary>
    /// Records a write that the ECU has taken.
    ///
    /// Without this the copy held here still says what the controller said when
    /// it was read, and every table drawn from it would show the old values
    /// while the engine ran the new ones. Worse, the next edit would be built on
    /// stale bytes and would quietly undo this one.
    ///
    /// Called after the write has been acknowledged and read back, never before:
    /// this is a record of what happened, not a prediction of it.
    /// </summary>
    public void Accept(TuneWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (write.Page < 0 || write.Page >= _pages.Length) return;

        byte[] page = _pages[write.Page];
        if (write.Offset < 0 || write.Offset + write.Data.Length > page.Length) return;

        write.Data.CopyTo(page.AsSpan(write.Offset));
    }

    public TuneWrite? EncodeArray(string name, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!_byName.TryGetValue(name, out TuneConstant? constant)) return null;
        if (constant.IsBitField) return null;
        if (values.Count != constant.Columns * constant.Rows) return null;

        var data = new byte[constant.Size];

        for (int i = 0; i < values.Count; i++)
        {
            double scaled = (values[i] - constant.Transform) / constant.Scale;
            if (double.IsNaN(scaled) || double.IsInfinity(scaled)) return null;

            Span<byte> at = data.AsSpan(i * constant.ElementSize);

            if (!TryWrite(at, constant.Type, scaled, _little)) return null;
        }

        return new TuneWrite(constant.Page, constant.Offset, data);
    }

    /// <summary>
    /// A table's cells as bytes, in the row-major order the page holds them.
    /// </summary>
    public TuneWrite? EncodeTable(string valuesConstant, double[,] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (!_byName.TryGetValue(valuesConstant, out TuneConstant? constant)) return null;

        int columns = cells.GetLength(0);
        int rows = cells.GetLength(1);

        if (columns != constant.Columns || rows != constant.Rows) return null;

        var flat = new double[columns * rows];
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                flat[row * columns + column] = cells[column, row];

        return EncodeArray(valuesConstant, flat);
    }

    /// <summary>
    /// Writes one value, refusing anything the type cannot hold.
    ///
    /// A value out of range is a mistake somewhere upstream, and wrapping it
    /// into whatever it happens to become is the worst of the available
    /// responses.
    /// </summary>
    private static bool TryWrite(Span<byte> at, RealtimeType type, double value, bool little)
    {
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);

        switch (type)
        {
            case RealtimeType.U08:
                if (rounded is < 0 or > byte.MaxValue) return false;
                at[0] = (byte)rounded;
                return true;

            case RealtimeType.S08:
                if (rounded is < sbyte.MinValue or > sbyte.MaxValue) return false;
                at[0] = (byte)(sbyte)rounded;
                return true;

            case RealtimeType.U16:
                if (rounded is < 0 or > ushort.MaxValue) return false;
                Write16(at, (ushort)rounded, little);
                return true;

            case RealtimeType.S16:
                if (rounded is < short.MinValue or > short.MaxValue) return false;
                Write16(at, unchecked((ushort)(short)rounded), little);
                return true;

            case RealtimeType.U32:
                if (rounded is < 0 or > uint.MaxValue) return false;
                Write32(at, (uint)rounded, little);
                return true;

            case RealtimeType.S32:
                if (rounded is < int.MinValue or > int.MaxValue) return false;
                Write32(at, unchecked((uint)(int)rounded), little);
                return true;

            case RealtimeType.F32:
                // A float keeps its fraction; rounding it would be the bug.
                if (little) BinaryPrimitives.WriteSingleLittleEndian(at, (float)value);
                else BinaryPrimitives.WriteSingleBigEndian(at, (float)value);
                return true;

            default:
                return false;
        }
    }

    private static void Write16(Span<byte> at, ushort value, bool little)
    {
        if (little) BinaryPrimitives.WriteUInt16LittleEndian(at, value);
        else BinaryPrimitives.WriteUInt16BigEndian(at, value);
    }

    private static void Write32(Span<byte> at, uint value, bool little)
    {
        if (little) BinaryPrimitives.WriteUInt32LittleEndian(at, value);
        else BinaryPrimitives.WriteUInt32BigEndian(at, value);
    }

    /// <summary>One element of a constant, scaled; null when it is out of reach.</summary>
    /// <summary>The constant by that name, or null where this firmware has none.</summary>
    public TuneConstant? Constant(string name) =>
        name is not null && _byName.TryGetValue(name, out TuneConstant? constant) ? constant : null;

    /// <summary>
    /// One setting's value, read out of the given page images rather than this
    /// tune's own.
    ///
    /// So that an editor working on a copy can read back what it has changed. The
    /// decoding is identical; only where the bytes come from differs.
    /// </summary>
    public double? ValueIn(IReadOnlyList<byte[]> pages, string name, int element = 0) =>
        _byName.TryGetValue(name, out TuneConstant? constant) ? Value(pages, constant, element) : null;

    /// <summary>A text setting, trimmed of the padding the ECU stores it with.</summary>
    public string? TextIn(IReadOnlyList<byte[]> pages, string name)
    {
        if (!_byName.TryGetValue(name, out TuneConstant? constant) || !constant.IsText) return null;
        if (constant.Page < 0 || constant.Page >= pages.Count) return null;

        byte[] page = pages[constant.Page];
        int at = constant.Offset;
        int length = Math.Max(0, constant.Columns);

        if (at < 0 || at + length > page.Length) return null;

        // A fixed-width field, padded with nulls or spaces. Stops at the first
        // null, which is where the firmware stops reading it too.
        var text = new System.Text.StringBuilder(length);

        for (int i = 0; i < length; i++)
        {
            byte b = page[at + i];
            if (b == 0) break;

            text.Append((char)b);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Writes one setting into the given page images.
    ///
    /// <b>A bit field is read, modified and written back.</b> Several settings
    /// share a byte — an MS3 puts four unrelated options in one — so writing the
    /// field's value alone would zero its neighbours. Those neighbours are other
    /// people's settings and the damage is silent: the ECU takes the byte, the
    /// read-back matches what was sent, and two options nobody touched have
    /// changed.
    /// </summary>
    public bool PokeInto(IReadOnlyList<byte[]> pages, TuneConstant constant, int element, double value)
    {
        ArgumentNullException.ThrowIfNull(constant);
        ArgumentNullException.ThrowIfNull(pages);

        if (constant.Page < 0 || constant.Page >= pages.Count) return false;

        byte[] page = pages[constant.Page];
        int at = constant.Offset + (element * constant.ElementSize);

        if (at < 0 || at + constant.ElementSize > page.Length) return false;

        Span<byte> bytes = page.AsSpan(at);

        if (!constant.IsBitField)
        {
            double scaled = constant.Scale != 0 ? (value - constant.Transform) / constant.Scale : value;
            return TryWrite(bytes, constant.Type, scaled, _little);
        }

        // The storage unit as it stands, so everything but this field survives.
        long current = (long)(Raw(bytes, constant.Type, _little) ?? 0);

        int width = constant.BitHigh - constant.BitLow + 1;
        long mask = ((1L << width) - 1) << constant.BitLow;
        long placed = ((long)Math.Round(value) << constant.BitLow) & mask;

        return TryWrite(bytes, constant.Type, (current & ~mask) | placed, _little);
    }

    /// <summary>Writes a text setting, padded with nulls and never overrunning its field.</summary>
    public bool PokeTextInto(IReadOnlyList<byte[]> pages, TuneConstant constant, string value)
    {
        ArgumentNullException.ThrowIfNull(constant);
        ArgumentNullException.ThrowIfNull(pages);

        if (!constant.IsText || constant.Page < 0 || constant.Page >= pages.Count) return false;

        byte[] page = pages[constant.Page];
        int at = constant.Offset;
        int length = Math.Max(0, constant.Columns);

        if (at < 0 || at + length > page.Length) return false;

        for (int i = 0; i < length; i++)
        {
            char c = i < value.Length ? value[i] : ' ';

            // The field is ASCII. Anything outside it would be stored as some
            // other character entirely, so it is refused rather than mangled.
            if (c > 0x7F) return false;

            page[at + i] = (byte)c;
        }

        return true;
    }

    /// <summary>The undecoded number in these bytes, before scale or bit masking.</summary>
    private static double? Raw(ReadOnlySpan<byte> bytes, RealtimeType type, bool little) => type switch
    {
        RealtimeType.U08 => bytes[0],
        RealtimeType.S08 => (sbyte)bytes[0],
        RealtimeType.U16 => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
        RealtimeType.S16 => little ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes),
        RealtimeType.U32 => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes),
        RealtimeType.S32 => little ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes),
        _ => null,
    };

    private double? Value(IReadOnlyList<byte[]> pages, TuneConstant constant, int element)
    {
        if (constant.Page < 0 || constant.Page >= pages.Count) return null;

        byte[] page = pages[constant.Page];
        int at = constant.Offset + (element * constant.ElementSize);

        if (at < 0 || at + constant.ElementSize > page.Length) return null;

        return Decode(page.AsSpan(at), constant);
    }

    private double? Value(TuneConstant constant, int element) =>
        Value(_pages, constant, element);

    /// <summary>
    /// The number these bytes hold, scaled and bit-masked as the firmware says.
    ///
    /// The one place that is defined, shared by every reader. Two decoders that
    /// drifted apart would let an editor show one number and write another,
    /// which is the sort of thing nobody finds until an engine runs on it.
    /// </summary>
    private double? Decode(ReadOnlySpan<byte> bytes, TuneConstant constant)
    {
        if (Raw(bytes, constant.Type, _little) is not { } raw)
        {
            raw = constant.Type == RealtimeType.F32
                ? _little
                    ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
                    : BinaryPrimitives.ReadSingleBigEndian(bytes)
                : double.NaN;
        }

        if (!constant.IsBitField) return (raw * constant.Scale) + constant.Transform;

        int width = constant.BitHigh - constant.BitLow + 1;
        long mask = (1L << width) - 1;

        return ((long)raw >> constant.BitLow) & mask;
    }
}
