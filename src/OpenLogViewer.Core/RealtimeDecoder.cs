using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// Turns a realtime block from an ECU into channel values.
///
/// Byte order is whatever the INI declares, because it differs by firmware and
/// there is no way to tell from the bytes: MegaSquirt runs on a Freescale S12
/// and is big-endian, rusEFI runs on an ARM and is little. Read the wrong way
/// round, a block does not fail — every channel comes out a different number
/// that is still a number.
/// </summary>
public sealed class RealtimeDecoder
{
    private readonly RealtimeField[] _fields;
    private readonly MathExpression?[] _expressions;
    private readonly RealtimeExpression[] _declared;
    private readonly bool _littleEndian;

    private readonly Dictionary<string, double> _settings;

    /// <summary>
    /// Builds a decoder for one firmware layout.
    ///
    /// <paramref name="tuneSettings"/> supplies the tune's single-value settings.
    /// The firmware's derived channels are written in terms of the tune as well
    /// as the wire — duty cycle divides by <c>nCylinders</c> — so without them a
    /// large share of the channels cannot be computed at all.
    /// </summary>
    public RealtimeDecoder(RealtimeLayout layout, IReadOnlyDictionary<string, double>? tuneSettings = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        Layout = layout;
        _fields = [.. layout.Fields];
        _declared = [.. layout.Expressions];
        _littleEndian = layout.LittleEndian;
        _settings = tuneSettings is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(tuneSettings, StringComparer.OrdinalIgnoreCase);

        // Resolved once. An expression naming something neither the firmware nor
        // the tune publishes yields null and is reported rather than retried on
        // every sample.
        var known = new List<string>(_fields.Select(f => f.Name));
        known.AddRange(_settings.Keys);

        _expressions = new MathExpression?[_declared.Length];
        var unresolved = new List<string>();

        for (int i = 0; i < _declared.Length; i++)
        {
            if (MathExpression.TryParse(_declared[i].Expression, known, out MathExpression? parsed, out _))
            {
                _expressions[i] = parsed;
                known.Add(_declared[i].Name);
            }
            else
            {
                unresolved.Add(_declared[i].Name);
            }
        }

        UnresolvedExpressions = unresolved;
        Names = [.. _fields.Select(f => f.Name), .. Resolved().Select(e => e.Name)];
    }

    public RealtimeLayout Layout { get; }

    /// <summary>Channel names, aligned with what <see cref="Decode"/> returns.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Derived channels that could not be resolved against this firmware.</summary>
    public IReadOnlyList<string> UnresolvedExpressions { get; }

    public IEnumerable<RealtimeExpression> Resolved()
    {
        for (int i = 0; i < _declared.Length; i++)
            if (_expressions[i] is not null)
                yield return _declared[i];
    }

    /// <summary>Units for each entry of <see cref="Names"/>.</summary>
    public IReadOnlyList<string> Units =>
        [.. _fields.Select(f => f.Units), .. Resolved().Select(e => e.Units)];

    /// <summary>Display precision for each entry of <see cref="Names"/>.</summary>
    public IReadOnlyList<int> Digits =>
        [.. _fields.Select(f => f.Digits), .. Resolved().Select(_ => 2)];

    /// <summary>
    /// Decodes one block. A block shorter than the layout expects yields NaN for
    /// the fields that fall off the end rather than throwing — a truncated read
    /// on a flaky link should cost those channels, not the session.
    /// </summary>
    public double[] Decode(ReadOnlySpan<byte> block)
    {
        var values = new double[Names.Count];

        for (int i = 0; i < _fields.Length; i++) values[i] = Read(block, _fields[i], _littleEndian);

        int at = _fields.Length;
        Span<double> inputs = stackalloc double[16];

        for (int i = 0; i < _declared.Length; i++)
        {
            if (_expressions[i] is not { } expression) continue;

            IReadOnlyList<string> references = expression.References;
            Span<double> arguments = references.Count <= inputs.Length
                ? inputs[..references.Count]
                : new double[references.Count];

            for (int r = 0; r < references.Count; r++)
                arguments[r] = Value(references[r], values, at);

            values[at++] = expression.Evaluate(arguments);
        }

        return values;
    }

    /// <summary>
    /// A referenced value: from the block and the channels computed so far, or
    /// failing that from the tune, which does not change between samples.
    ///
    /// Searched backwards because an INI may define a name twice — three do in
    /// the MS3 firmware — and the later definition is the one in force, as it
    /// would be for any later assignment.
    /// </summary>
    private double Value(string name, double[] values, int limit)
    {
        for (int i = limit - 1; i >= 0; i--)
            if (Names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return values[i];

        return _settings.TryGetValue(name, out double setting) ? setting : double.NaN;
    }

    private static double Read(ReadOnlySpan<byte> block, RealtimeField field, bool little)
    {
        if (field.Offset + field.Size > block.Length) return double.NaN;

        ReadOnlySpan<byte> at = block[field.Offset..];

        double raw = field.Type switch
        {
            RealtimeType.U08 => at[0],
            RealtimeType.S08 => (sbyte)at[0],
            RealtimeType.U16 => little ? BinaryPrimitives.ReadUInt16LittleEndian(at) : BinaryPrimitives.ReadUInt16BigEndian(at),
            RealtimeType.S16 => little ? BinaryPrimitives.ReadInt16LittleEndian(at) : BinaryPrimitives.ReadInt16BigEndian(at),
            RealtimeType.U32 => little ? BinaryPrimitives.ReadUInt32LittleEndian(at) : BinaryPrimitives.ReadUInt32BigEndian(at),
            RealtimeType.S32 => little ? BinaryPrimitives.ReadInt32LittleEndian(at) : BinaryPrimitives.ReadInt32BigEndian(at),
            RealtimeType.F32 => little ? BinaryPrimitives.ReadSingleLittleEndian(at) : BinaryPrimitives.ReadSingleBigEndian(at),
            _ => double.NaN,
        };

        if (!field.IsBitField) return raw * field.Scale + field.Transform;

        // Packed flags: the INI names each run of bits separately, so the value
        // is the run shifted down to zero rather than the byte it came from.
        int width = field.BitHigh - field.BitLow + 1;
        long mask = (1L << width) - 1;

        return ((long)raw >> field.BitLow) & mask;
    }
}
