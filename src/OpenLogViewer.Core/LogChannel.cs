namespace OpenLogViewer.Core;

/// <summary>
/// One decoded data channel: a dense column of samples aligned with
/// <see cref="LogDocument.Time"/>.
///
/// Samples are held as 32-bit floats. No logger produces more precision than
/// that — the widest MLG field is an f32, and text logs come from short decimal
/// strings — and it halves what is by far the largest allocation: 179 channels
/// over 37,000 samples is 53 MB as doubles against 27 MB as floats.
///
/// The exception is a time base, which is cumulative. Over a long recording the
/// gap between representable floats grows past the interval between samples —
/// at ten hours it exceeds the 2.4 ms spacing of a 400 Hz logger — and time
/// would stop increasing. A channel constructed with
/// <c>preservePrecision</c> keeps its doubles.
/// </summary>
public sealed class LogChannel
{
    private readonly float[] _values;
    private readonly double[]? _precise;

    public LogChannel(string name, string units, int digits, double[] values, bool preservePrecision = false)
        : this(name, units, digits, Narrow(values), preservePrecision ? values : null)
    {
    }

    /// <summary>
    /// Builds a channel around a column a reader has already decoded. The array
    /// is taken over rather than copied, so the caller must not write to it
    /// again. Readers use this to avoid staging a log as doubles first.
    /// </summary>
    public static LogChannel Adopt(string name, string units, int digits, float[] samples) =>
        new(name, units, digits, samples, null);

    private LogChannel(string name, string units, int digits, float[] samples, double[]? precise)
    {
        Name = name;
        Units = units;
        Digits = digits;

        _values = samples;
        _precise = precise;

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        int minIndex = -1, maxIndex = -1;

        // Measured from the stored samples, so the extremes are values the
        // channel can actually return rather than pre-rounding artefacts.
        for (int i = 0; i < _values.Length; i++)
        {
            double v = At(i);
            if (double.IsNaN(v)) continue;
            if (v < min) { min = v; minIndex = i; }
            if (v > max) { max = v; maxIndex = i; }
        }

        if (double.IsInfinity(min)) { min = 0; max = 0; }
        Min = min;
        Max = max;
        MinIndex = minIndex;
        MaxIndex = maxIndex;
    }

    public string Name { get; }

    /// <summary>Engineering units as declared by the logger, may be empty.</summary>
    public string Units { get; }

    /// <summary>Decimal places the logger intends for display.</summary>
    public int Digits { get; }

    public int Length => _values.Length;

    /// <summary>The stored samples, for bulk reads that do not need a time base's precision.</summary>
    public ReadOnlySpan<float> Samples => _values;

    public double Min { get; }

    public double Max { get; }

    /// <summary>Sample index of the first occurrence of <see cref="Min"/>, or -1 if empty.</summary>
    public int MinIndex { get; }

    /// <summary>Sample index of the first occurrence of <see cref="Max"/>, or -1 if empty.</summary>
    public int MaxIndex { get; }

    /// <summary>True when every sample is identical, so the channel carries no signal.</summary>
    public bool IsFlat => Max - Min <= 0;

    public string Format(double value) =>
        double.IsNaN(value) ? "—" : value.ToString("F" + Math.Clamp(Digits, 0, 6));

    public string FormatWithUnits(double value) =>
        Units.Length == 0 ? Format(value) : $"{Format(value)} {Units}";

    /// <summary>Sample at <paramref name="index"/>, or NaN when out of range.</summary>
    public double At(int index)
    {
        if ((uint)index >= (uint)_values.Length) return double.NaN;
        return _precise is not null ? _precise[index] : _values[index];
    }

    private static float[] Narrow(double[] values)
    {
        var narrowed = new float[values.Length];
        for (int i = 0; i < values.Length; i++) narrowed[i] = (float)values[i];
        return narrowed;
    }
}
