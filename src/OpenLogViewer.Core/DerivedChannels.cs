using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>
/// Values a firmware defines in terms of other values rather than reading from
/// anywhere.
///
/// <para>
/// <c>[OutputChannels]</c> holds two different things. Most of it names fields in
/// the block the ECU sends — an offset and a scale. The rest are written as an
/// expression in braces:
/// </para>
/// <code>
///   isTps1Primary = { tps1_1AdcChannel != 0 }
///   isEtbEnabled  = { isEtb1Enabled || isEtb2Enabled }
/// </code>
/// <para>
/// They exist so a dialog's conditions can be written once against a readable
/// name instead of repeating the test everywhere it is needed, and they are used
/// heavily for exactly that: rusEFI's throttle pages are gated on nothing else.
/// A definition declares between six and seventy-six of them.
/// </para>
/// <para>
/// One may be defined in terms of another, as the second line above is, so
/// resolving one is not a lookup but an evaluation — and a definition with a
/// mistake in it could have two refer to each other, which is why the resolver
/// keeps track of what it is already working on.
/// </para>
/// </summary>
public static partial class DerivedChannels
{
    /// <summary>
    /// A name bound to an expression: <c>isSTFT = { … }</c>.
    ///
    /// The whole of the braces is taken rather than the first closing one, since
    /// an expression may contain a nested group.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*\{(?<expression>.*)\}\s*$")]
    private static partial Regex Definition { get; }

    /// <summary>Reads them out of <c>[OutputChannels]</c>.</summary>
    public static IReadOnlyDictionary<string, string> Read(
        string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        var derived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in MsqIni.Section(iniText, "OutputChannels", symbols ?? MsqIni.DefaultSymbols))
        {
            string line = MsqIni.Strip(raw);
            if (line.Length == 0) continue;

            if (Definition.Match(line) is not { Success: true } match) continue;

            string expression = match.Groups["expression"].Value.Trim();
            if (expression.Length == 0) continue;

            derived[match.Groups["name"].Value] = expression;
        }

        return derived;
    }

    /// <summary>
    /// A lookup that answers for derived names too, by working them out.
    ///
    /// Anything the underlying lookup can answer is answered by it, so a real
    /// field always wins over a name that happens to match — a definition that
    /// declares both means the one the ECU actually sends.
    /// </summary>
    public static Func<string, double> Resolving(
        IReadOnlyDictionary<string, string> derived, Func<string, double> lookup)
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(lookup);

        if (derived.Count == 0) return lookup;

        // Per resolver rather than per call, since one derived value may be
        // reached through several others on the way to the same answer.
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        double Resolve(string name)
        {
            double direct = lookup(name);
            if (!double.IsNaN(direct)) return direct;

            if (!derived.TryGetValue(name, out string? expression)) return double.NaN;

            // Already being worked out: a definition where two refer to each
            // other would otherwise not terminate. Unknown is the honest answer
            // and the caller shows the field.
            if (!visiting.Add(name)) return double.NaN;

            try
            {
                return DialogCondition.Evaluate(expression, Resolve) switch
                {
                    ConditionVerdict.Shown => 1,
                    ConditionVerdict.Hidden => 0,
                    _ => double.NaN,
                };
            }
            finally
            {
                visiting.Remove(name);
            }
        }

        return Resolve;
    }
}
