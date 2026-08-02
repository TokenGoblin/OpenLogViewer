using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// The tune as it exists in the ECU right now, read out over the wire.
///
/// Better than a file for the job it does. A saved <c>.msq</c> is whatever was
/// last written to disk, which on a bench that has been poked at is not
/// necessarily what the controller is running; and rusEFI's own saved tunes
/// carry no tables at all, so for that firmware there is no file to fall back
/// on. Reading the pages settles it.
///
/// Read-only, as it happens — this class only fetches and decodes. Writing is a
/// separate path with a separate command.
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

    /// <summary>One element of a constant, scaled; null when it is out of reach.</summary>
    private double? Value(TuneConstant constant, int element)
    {
        if (constant.Page < 0 || constant.Page >= _pages.Length) return null;

        byte[] page = _pages[constant.Page];
        int at = constant.Offset + element * constant.ElementSize;

        if (at < 0 || at + constant.ElementSize > page.Length) return null;

        ReadOnlySpan<byte> bytes = page.AsSpan(at);

        double raw = constant.Type switch
        {
            RealtimeType.U08 => bytes[0],
            RealtimeType.S08 => (sbyte)bytes[0],
            RealtimeType.U16 => _little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
            RealtimeType.S16 => _little ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes),
            RealtimeType.U32 => _little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes),
            RealtimeType.S32 => _little ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes),
            RealtimeType.F32 => _little ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : BinaryPrimitives.ReadSingleBigEndian(bytes),
            _ => double.NaN,
        };

        if (!constant.IsBitField) return raw * constant.Scale + constant.Transform;

        int width = constant.BitHigh - constant.BitLow + 1;
        long mask = (1L << width) - 1;

        return ((long)raw >> constant.BitLow) & mask;
    }
}
