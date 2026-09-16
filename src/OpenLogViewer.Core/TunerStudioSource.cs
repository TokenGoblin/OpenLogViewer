namespace OpenLogViewer.Core;

/// <summary>
/// A live source over the TunerStudio protocol — MegaSquirt, rusEFI, anything
/// with a firmware INI.
///
/// Reads the realtime block and hands back only the channels the firmware's
/// datalog definition names, under the names it gives them. Taking every decoded
/// value instead would produce a few hundred internal names no preset or filter
/// would ever match — but the full decode is still done every poll regardless,
/// so it costs nothing extra to keep it around for whoever asks for it through
/// <see cref="IRawTelemetrySource"/>.
/// </summary>
public sealed class TunerStudioSource : ILiveSource, IRawTelemetrySource
{
    private readonly EcuConnection _connection;
    private readonly RealtimeDecoder _decoder;
    private readonly int[] _sourceIndex;
    private readonly string[] _names;
    private readonly string[] _units;
    private readonly int[] _digits;
    private double[]? _lastRawFrame;

    public TunerStudioSource(
        EcuConnection connection, RealtimeDecoder decoder, IReadOnlyList<DatalogEntry> datalog)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        ArgumentNullException.ThrowIfNull(datalog);

        var indices = new List<int>();
        var names = new List<string>();
        var units = new List<string>();
        var digits = new List<int>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DatalogEntry entry in datalog)
        {
            int at = LastIndexOf(decoder.Names, entry.Channel);
            if (at < 0) continue;

            string label = entry.Label.Length > 0 ? entry.Label : entry.Channel;
            if (!taken.Add(label)) continue;

            indices.Add(at);
            names.Add(label);
            units.Add(decoder.Units[at]);
            digits.Add(entry.Digits);
        }

        _sourceIndex = [.. indices];
        _names = [.. names];
        _units = [.. units];
        _digits = [.. digits];
    }

    public IReadOnlyList<string> Names => _names;

    public IReadOnlyList<string> Units => _units;

    public IReadOnlyList<int> Digits => _digits;

    public int Retries => _connection.Retries;

    /// <summary>Every channel the firmware's INI decodes, whether or not its datalog names it.</summary>
    public IReadOnlyList<string> RawNames => _decoder.Names;

    public IReadOnlyList<string> RawUnits => _decoder.Units;

    /// <summary>
    /// The full block from the most recent <see cref="Read"/>. Read and written
    /// through <see cref="Volatile"/> rather than a lock, matching the "never
    /// make the poll thread wait" rule everything else on this path follows —
    /// a reader on another thread gets either the previous frame or this one,
    /// never a partly written one, because the whole array is replaced rather
    /// than mutated.
    /// </summary>
    public double[]? LastRawFrame => Volatile.Read(ref _lastRawFrame);

    public void Open() => _connection.Open();

    public double[] Read()
    {
        double[] decoded = _decoder.Decode(_connection.ReadRealtime(_decoder.Layout.BlockSize));
        Volatile.Write(ref _lastRawFrame, decoded);

        var row = new double[_sourceIndex.Length];

        for (int i = 0; i < _sourceIndex.Length; i++) row[i] = decoded[_sourceIndex[i]];

        return row;
    }

    /// <summary>
    /// Closes and reopens the link, then proves it by reading a block.
    ///
    /// The port has to be closed before it is reopened: a handle whose device
    /// has gone still reports itself open, so reopening alone does nothing and
    /// every read afterwards fails identically. Opening also proves nothing on
    /// its own — an adapter can enumerate before the ECU behind it is answering.
    /// </summary>
    public void Recover()
    {
        _connection.Reopen();
        _connection.ReadRealtime(_decoder.Layout.BlockSize);
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Searched backwards because an INI may define a name twice — three do in
    /// the MS3 firmware — and the later definition is the one in force.
    /// </summary>
    private static int LastIndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = names.Count - 1; i >= 0; i--)
            if (names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }
}
