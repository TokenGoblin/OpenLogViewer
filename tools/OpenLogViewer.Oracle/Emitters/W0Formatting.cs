using System.Globalization;

namespace OpenLogViewer.Oracle;

/// <summary>
/// .NET's exact text for every number format the C# uses (rust/oracle/formats.txt,
/// written by `cargo xtask inventory`), across values chosen for the ways formatters
/// differ: halfway rounding, binary fractions that print long, floats widened to
/// double, the edges of the range, and NaN and the infinities.
///
/// Every case is written twice: once for the invariant culture, and once for
/// en-US, which is what the C# gets wherever it formats with the current culture
/// (no provider argument) on the machines it was proven on. The two differ in the
/// percent pattern and the infinity symbol, among others.
/// </summary>
public sealed class W0Formatting : IEmitter
{
    public string Package => "w0-model";

    public string Set => "fmt";

    private static readonly double[] Doubles =
    [
        0, -0.0, 1, -1, 0.5, 1.5, 2.5, -2.5, 0.05, 0.15, 0.25, 0.35, 0.45, 1.005, 2.675,
        14.7, 100.2, Math.PI, 1.0 / 3, 2.0 / 3, 1234.5678, -1234.5678, 999.95, 99.95, 9.995,
        0.001, 0.0001, 1e-5, 1e-7, 123456789.123, 1e15, 1e16, 1e21, -1e21,
        double.MaxValue, double.Epsilon, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        14.7f, 0.1f, 100.2f, 3.3f, 101.325f, -40f, 0.85f, 12345.678f,
    ];

    private static readonly long[] Integers =
    [
        0, 1, -1, 7, 15, 99, 255, 1000, 4095, 12345, -12345, 65535, 1234567, int.MaxValue, int.MinValue,
    ];

    private static readonly (string Name, CultureInfo Culture)[] Cultures =
    [
        ("invariant", CultureInfo.InvariantCulture),
        ("en-US", CultureInfo.GetCultureInfo("en-US")),
    ];

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        string[] formats = File.ReadAllLines(Path.Combine(context.Rust, "oracle", "formats.txt"))
            .Where(l => l.Length > 0).Prepend("").ToArray();
        var cases = new List<object>();

        foreach ((string culture, CultureInfo info) in Cultures)
        foreach (string format in formats)
        {
            foreach (double v in Doubles)
            {
                if (Try(() => v.ToString(format, info)) is { } text)
                    cases.Add(new { kind = "f64", culture, bits = Exact.Bits(v), format, text });
            }

            foreach (long n in Integers)
            {
                // Ints are formatted as the type they are in the C#, which for these
                // magnitudes is int unless they do not fit.
                string? text = n is >= int.MinValue and <= int.MaxValue
                    ? Try(() => ((int)n).ToString(format, info))
                    : Try(() => n.ToString(format, info));
                if (text is not null) cases.Add(new { kind = "int", culture, value = n, format, text });
            }
        }

        return cases;
    }

    /// <summary>A format the type does not support (X on a double) is not a case.</summary>
    private static string? Try(Func<string> f)
    {
        try { return f(); }
        catch (FormatException) { return null; }
    }
}
