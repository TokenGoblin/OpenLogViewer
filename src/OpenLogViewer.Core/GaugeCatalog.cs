using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>
/// One gauge as the firmware defines it: what to show, over what range, and
/// where the readings stop being good news.
/// </summary>
public sealed record GaugeSpec
{
    /// <summary>The name the INI gives this configuration, e.g. "CLTGauge".</summary>
    public required string Name { get; init; }

    /// <summary>The output channel it displays.</summary>
    public required string Channel { get; init; }

    public required string Title { get; init; }

    public string Units { get; init; } = "";

    /// <summary>Category from the preceding <c>gaugeCategory</c> line, for grouping.</summary>
    public string Category { get; init; } = "";

    public double Low { get; init; }

    public double High { get; init; }

    public double LowDanger { get; init; } = double.NaN;

    public double LowWarning { get; init; } = double.NaN;

    public double HighWarning { get; init; } = double.NaN;

    public double HighDanger { get; init; } = double.NaN;

    /// <summary>Decimals on the reading.</summary>
    public int ValueDigits { get; init; }

    /// <summary>Decimals on the scale labels.</summary>
    public int LabelDigits { get; init; }

    /// <summary>
    /// Whether there is a range to draw a dial over.
    ///
    /// False for the several gauges whose generator left the bounds at zero —
    /// "Fuel Cut Code" and "Knock Frequency Start" among them. They are kept
    /// rather than dropped, because the channel is worth seeing even when the
    /// firmware has not said what a normal value looks like; they show as a
    /// reading without a face.
    /// </summary>
    public bool HasScale => High > Low;

    /// <summary>
    /// Whether the warning and danger bands are usable.
    ///
    /// Generated INIs often fill all six numbers with the same pair, which reads
    /// as a danger band covering everything. A band is only believed when it is
    /// ordered the way its names say it should be.
    /// </summary>
    public bool HasBands =>
        HasScale
        && !double.IsNaN(LowDanger) && !double.IsNaN(LowWarning)
        && !double.IsNaN(HighWarning) && !double.IsNaN(HighDanger)
        && LowDanger <= LowWarning && LowWarning <= HighWarning && HighWarning <= HighDanger;

    /// <summary>
    /// Where a reading falls, for colouring.
    ///
    /// A limit sitting on the end of the scale means there is no limit at that
    /// end, not that everything past it is trouble. A throttle runs 0 to 100
    /// with its lower limits at 0, and read literally that paints a closed
    /// throttle as a fault.
    /// </summary>
    public GaugeBand BandFor(double value)
    {
        if (double.IsNaN(value)) return GaugeBand.Unknown;
        if (!HasBands) return GaugeBand.Normal;

        if (LowDanger > Low && value <= LowDanger) return GaugeBand.Danger;
        if (HighDanger < High && value >= HighDanger) return GaugeBand.Danger;

        if (LowWarning > Low && value <= LowWarning) return GaugeBand.Warning;
        if (HighWarning < High && value >= HighWarning) return GaugeBand.Warning;

        return GaugeBand.Normal;
    }

    /// <summary>Where along the dial a reading sits, 0 to 1, clamped to the face.</summary>
    public double Fraction(double value)
    {
        if (double.IsNaN(value) || High <= Low) return 0;

        return Math.Clamp((value - Low) / (High - Low), 0, 1);
    }
}

public enum GaugeBand
{
    Unknown,
    Normal,
    Warning,
    Danger,
}

/// <summary>
/// Reads the gauge definitions out of a firmware INI.
///
/// Worth taking from the file rather than inventing: the firmware already says
/// what every channel is called, what it is measured in, what range is sensible
/// and where the readings become a problem. rusEFI defines 276 of these across
/// 30 categories, with a redline that follows the tune's own rev limit. Guessing
/// any of it would produce a dial that looks right and is not.
/// </summary>
public static class GaugeCatalog
{
    /// <summary>
    /// Every gauge the INI defines, in file order.
    ///
    /// <paramref name="tuneSettings"/> resolves bounds written as expressions —
    /// a rev counter runs to <c>{rpmHardLimit + 2000}</c> and warns at
    /// <c>{rpmHardLimit - 500}</c>, so without the tune the dial has no scale.
    /// </summary>
    public static IReadOnlyList<GaugeSpec> Read(
        string iniText,
        IReadOnlyDictionary<string, double>? tuneSettings = null,
        IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        var gauges = new List<GaugeSpec>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string category = "";

        foreach (string raw in MsqIni.Section(iniText, "GaugeConfigurations", symbols ?? MsqIni.DefaultSymbols))
        {
            string line = MsqIni.Strip(raw);
            if (line.Length == 0) continue;

            int equals = line.IndexOf('=');
            if (equals < 0) continue;

            string name = line[..equals].Trim();
            string body = line[(equals + 1)..].Trim();

            if (name.Equals("gaugeCategory", StringComparison.OrdinalIgnoreCase))
            {
                category = Unquote(body);
                continue;
            }

            if (Parse(name, body, category, tuneSettings) is not { } gauge) continue;

            // A name defined twice — which happens where a Celsius and a
            // Fahrenheit variant both survive — takes its later definition, as
            // any later assignment would.
            if (byName.TryGetValue(gauge.Name, out int at)) gauges[at] = gauge;
            else
            {
                byName[gauge.Name] = gauges.Count;
                gauges.Add(gauge);
            }
        }

        return gauges;
    }

    /// <summary>
    /// The dashboard the firmware suggests: <c>gauge1</c> to <c>gaugeN</c> of
    /// <c>[FrontPage]</c>, in position order, as gauge configuration names.
    /// </summary>
    public static IReadOnlyList<string> ReadFrontPage(string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        var positions = new SortedDictionary<int, string>();

        foreach (string raw in MsqIni.Section(iniText, "FrontPage", symbols ?? MsqIni.DefaultSymbols))
        {
            string line = MsqIni.Strip(raw);

            int equals = line.IndexOf('=');
            if (equals < 0) continue;

            string name = line[..equals].Trim();
            if (!name.StartsWith("gauge", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(name.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int at))
                continue;

            string value = Unquote(line[(equals + 1)..].Trim());
            if (value.Length > 0) positions[at] = value;
        }

        return [.. positions.Values];
    }

    private static GaugeSpec? Parse(
        string name, string body, string category, IReadOnlyDictionary<string, double>? settings)
    {
        string[] fields = SplitFields(body);

        // Channel, title, units and six bounds; the two digit counts are often
        // present but a gauge is usable without them.
        if (fields.Length < 9) return null;

        double low = Number(fields[3], settings);
        double high = Number(fields[4], settings);

        // A bound that will not resolve leaves the gauge without a face rather
        // than without an entry — see HasScale.
        if (double.IsNaN(low)) low = 0;
        if (double.IsNaN(high)) high = 0;

        return new GaugeSpec
        {
            Name = name,
            Channel = Unquote(fields[0]),
            Title = Unquote(fields[1]),
            Units = UnitsIn(fields[2]),
            Category = category,
            Low = low,
            High = high,
            LowDanger = Number(fields[5], settings),
            LowWarning = Number(fields[6], settings),
            HighWarning = Number(fields[7], settings),
            HighDanger = Number(fields[8], settings),
            ValueDigits = fields.Length > 9 ? Digits(fields[9]) : 0,
            LabelDigits = fields.Length > 10 ? Digits(fields[10]) : 0,
        };
    }

    /// <summary>
    /// Splits on commas that separate fields, ignoring those inside quotes or
    /// braces. A bound can be an expression, and an expression can call a
    /// function with arguments of its own.
    /// </summary>
    private static string[] SplitFields(string body)
    {
        var fields = new List<string>();
        int start = 0;
        int braces = 0;
        bool quoted = false;

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '{') braces++;
            else if (!quoted && c == '}') braces--;
            else if (c == ',' && !quoted && braces <= 0)
            {
                fields.Add(body[start..i].Trim());
                start = i + 1;
            }
        }

        fields.Add(body[start..].Trim());

        return [.. fields];
    }

    /// <summary>A bound: a plain number, or an expression over the tune's settings.</summary>
    private static double Number(string text, IReadOnlyDictionary<string, double>? settings)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return double.NaN;

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
            return plain;

        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}')) return double.NaN;
        if (settings is null) return double.NaN;

        return TuningContext.Evaluate(trimmed[1..^1].Trim(), settings) ?? double.NaN;
    }

    private static int Digits(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int digits)
            ? Math.Clamp(digits, 0, 6)
            : 0;

    /// <summary>
    /// The units label, or nothing when the INI computes it.
    ///
    /// A load gauge is labelled <c>{ bitStringValue(algorithmUnits, algorithm) }</c>
    /// — it says kPa or %, depending on how the engine measures load. Printing
    /// the expression instead is worse than printing nothing, so an unresolved
    /// one leaves the gauge unlabelled rather than showing its own source.
    /// </summary>
    private static string UnitsIn(string field)
    {
        string units = Unquote(field);

        return units.StartsWith('{') ? "" : units;
    }

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }
}
