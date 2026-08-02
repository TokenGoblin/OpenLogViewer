using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>
/// Every named number a firmware definition can refer to, gathered in one place.
///
/// Gauge scales, warning bands and derived channels are all written as
/// expressions over names, and the names come from three unrelated places: the
/// tune's own constants, TunerStudio's per-project variables, and channels the
/// INI derives from other channels. A rev counter runs to
/// <c>{rpmHardLimit + 2000}</c> and a coolant gauge to
/// <c>{clt_exp ? 450 : 250}</c>, where <c>clt_exp</c> is stored in the tune as
/// the word "Normal" and only becomes a zero by way of a declaration elsewhere
/// in the INI.
///
/// Resolve none of that and the gauges still appear — with no scale, which is
/// worse than absent, because a dial with the wrong range looks like a reading.
/// </summary>
public static class TuningContext
{
    /// <summary>Declarations in <c>[PcVariables]</c>: <c>bits</c> label lists and plain scalars.</summary>
    private static readonly Regex BitsVariable = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*bits\s*,\s*[A-Z]\d\d\s*,\s*\[[^\]]*\]\s*,\s*(?<labels>.+)$""",
        RegexOptions.Compiled);

    /// <summary>
    /// Builds the lookup for one firmware and tune.
    ///
    /// Later sources do not overwrite earlier ones, so a value the tune states
    /// outright always beats one the INI would have derived.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Build(
        string? iniText, string? tuneXml, IReadOnlySet<string>? symbols = null,
        IReadOnlyDictionary<string, double>? fromEcu = null)
    {
        var known = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // The ECU first and unconditionally: what the controller is running
        // beats what a file last recorded, and a file may be stale or absent.
        if (fromEcu is not null)
            foreach ((string name, double value) in fromEcu)
                known[name] = value;

        foreach ((string name, double value) in MsqTune.ReadScalars(tuneXml))
            known.TryAdd(name, value);

        if (iniText is null or "") return known;

        symbols ??= MsqIni.DefaultSymbols;

        AddPcVariables(known, iniText, tuneXml, symbols);
        AddConstantExpressions(known, iniText, symbols);

        return known;
    }

    /// <summary>
    /// TunerStudio's own variables, whose values live in the tune and whose
    /// meaning lives in the INI.
    ///
    /// A <c>bits</c> variable is stored as one of its labels — "Normal" rather
    /// than 0 — so the declaration has to be read to turn the word back into the
    /// number the expressions do arithmetic on.
    /// </summary>
    private static void AddPcVariables(
        Dictionary<string, double> known, string iniText, string? tuneXml, IReadOnlySet<string> symbols)
    {
        Dictionary<string, string[]> labels = new(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in MsqIni.Section(iniText, "PcVariables", symbols))
        {
            if (BitsVariable.Match(MsqIni.Strip(raw)) is not { Success: true } match) continue;

            labels[match.Groups["name"].Value] =
            [
                .. match.Groups["labels"].Value
                    .Split(',')
                    .Select(l => l.Trim().Trim('"')),
            ];
        }

        foreach ((string name, string text) in ReadPcVariables(tuneXml))
        {
            // A number wins outright.
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                known.TryAdd(name, number);
                continue;
            }

            if (!labels.TryGetValue(name, out string[]? options)) continue;

            int at = Array.FindIndex(options, o => o.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) known.TryAdd(name, at);
        }
    }

    /// <summary>The <c>pcVariable</c> entries of a tune, as written.</summary>
    public static IReadOnlyDictionary<string, string> ReadPcVariables(string? msqXml)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(msqXml)) return values;

        XDocument document;
        try
        {
            document = XDocument.Parse(msqXml);
        }
        catch (System.Xml.XmlException)
        {
            return values;
        }

        foreach (XElement element in document.Descendants().Where(e => e.Name.LocalName == "pcVariable"))
        {
            string? name = element.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;

            values[name] = element.Value.Trim().Trim('"');
        }

        return values;
    }

    /// <summary>
    /// Channels the INI derives that turn out to be constants.
    ///
    /// <c>cltlowlim = { clt_exp ? -40 : -40 }</c> is declared beside the live
    /// channels but depends on nothing that moves, so it has a value before the
    /// engine is running — which is exactly when a gauge needs its scale.
    ///
    /// Repeated until nothing more resolves, because one can be written in terms
    /// of another.
    /// </summary>
    private static void AddConstantExpressions(
        Dictionary<string, double> known, string iniText, IReadOnlySet<string> symbols)
    {
        IReadOnlyList<RealtimeExpression> expressions =
            MsqIni.ReadOutputChannels(iniText, symbols).Expressions;

        // Three passes is plenty for the chains these files actually contain,
        // and it cannot loop on one that refers to itself.
        for (int pass = 0; pass < 3; pass++)
        {
            bool added = false;

            foreach (RealtimeExpression expression in expressions)
            {
                if (known.ContainsKey(expression.Name)) continue;
                if (Evaluate(expression.Expression, known) is not { } value || double.IsNaN(value)) continue;

                known[expression.Name] = value;
                added = true;
            }

            if (!added) break;
        }
    }

    /// <summary>
    /// An expression's value, or null when it names something not yet known.
    ///
    /// Deliberately strict: a missing name yields nothing rather than NaN, so a
    /// channel that genuinely depends on the engine running is left out instead
    /// of being recorded as a constant that happens to be not-a-number.
    /// </summary>
    public static double? Evaluate(string expression, IReadOnlyDictionary<string, double> known)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(known);

        if (!MathExpression.TryParse(expression, [.. known.Keys], out MathExpression? parsed, out _))
            return null;

        Span<double> arguments = parsed.References.Count == 0
            ? []
            : stackalloc double[parsed.References.Count];

        for (int i = 0; i < parsed.References.Count; i++)
        {
            if (!known.TryGetValue(parsed.References[i], out double value)) return null;
            arguments[i] = value;
        }

        return parsed.Evaluate(arguments);
    }
}
