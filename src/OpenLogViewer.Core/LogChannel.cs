namespace OpenLogViewer.Core;

/// <summary>
/// One decoded data channel: a dense column of samples aligned with
/// <see cref="LogDocument.Time"/>.
/// </summary>
public sealed class LogChannel
{
    public LogChannel(string name, string units, int digits, double[] values)
    {
        Name = name;
        Units = units;
        Digits = digits;
        Values = values;

        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        int minIndex = -1, maxIndex = -1;

        for (int i = 0; i < values.Length; i++)
        {
            double v = values[i];
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

    public double[] Values { get; }

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
    public double At(int index) =>
        (uint)index < (uint)Values.Length ? Values[index] : double.NaN;
}
