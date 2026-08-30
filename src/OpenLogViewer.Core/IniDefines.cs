using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>
/// Named lists of option labels, which a firmware writes once and refers to
/// wherever the same choice appears.
///
/// <code>
///   #define loadSourceNames = "MAP", "TPS", "IMAP/EMAP", "INVALID", …
///   algorithm    = bits, U08, 37, [0:2], $loadSourceNames
///   ignAlgorithm = bits, U08, 26, [4:6], $loadSourceNames
/// </code>
///
/// <para>
/// Without these a setting's value has no name. The reference is not a label —
/// left as written, "Load source" offers one choice called
/// <c>$loadSourceNames</c> and picking it writes nothing sensible. An MS3
/// definition points at one from 338 of its bit fields and a Speeduino from 175,
/// so this is most of what makes a settings page readable.
/// </para>
/// <para>
/// A definition may be built from others — <c>#define invalid_x16 = $invalid_x8,
/// $invalid_x8</c> — and the position of a label in the finished list is the
/// number the ECU stores, so expanding them is not a nicety. One that refers to
/// itself is left as it stands rather than expanded forever.
/// </para>
/// </summary>
public static partial class IniDefines
{
    /// <summary>
    /// <c>#define name = "a", "b", $other</c>.
    ///
    /// Only the form with a list after it. A bare <c>#define SYMBOL</c> is a
    /// switch for the preprocessor and means something else entirely.
    /// </summary>
    [GeneratedRegex(@"^\s*#define\s+(?<name>\w+)\s*=\s*(?<values>.+)$")]
    private static partial Regex Define { get; }

    /// <summary>Reads them, with references between them worked out.</summary>
    /// <param name="iniText">The definition file.</param>
    /// <param name="symbols">
    /// Which build this is. A firmware commonly defines the same list twice —
    /// one set of pin names per board — inside <c>#if</c>, and the last one wins
    /// where the preprocessor is ignored. That gives correctly numbered options
    /// carrying the wrong labels, which is worse than none: the page looks right
    /// and reads wrong.
    /// </param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Read(
        string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        symbols ??= MsqIni.DefaultSymbols;

        var expanded = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        // Expanded as each one is met, against the lists that already stand.
        //
        // A firmware grows a list by redefining it in terms of itself:
        //
        //   #define PIN_DIGOUT_CANPWM = "CANPWM1", … "CANPWM8"
        //   #define PIN_DIGOUT_CANPWM = "INVALID", $PIN_DIGOUT1, $PIN_DIGOUT_CANPWM, …
        //
        // — the second meaning the eight names just declared, exactly as a
        // preprocessor would read it. Collecting every definition first and
        // resolving afterwards makes the second one refer to itself, which the
        // loop guard then leaves as the literal "$PIN_DIGOUT_CANPWM": one bogus
        // label where eight real ones belong, so every pin after it is numbered
        // seven short. On an MS3 thirteen output-pin settings read that list,
        // and picking a pin from one of them would have set a different pin.
        foreach (string line in MsqIni.Live(iniText, symbols))
        {
            // Not MsqIni.Strip: a label may contain a semicolon, and cutting at
            // the first one would take half the list with it.
            if (Define.Match(line.TrimEnd('\r')) is not { Success: true } match) continue;

            expanded[match.Groups["name"].Value] = Expand(match.Groups["values"].Value, expanded);
        }

        return expanded;
    }

    /// <summary>
    /// Expands a list, splicing in any list it refers to.
    ///
    /// A reference contributes all of its labels in order, because the position
    /// of a label is the value it stands for.
    /// </summary>
    public static IReadOnlyList<string> Expand(
        string values, IReadOnlyDictionary<string, IReadOnlyList<string>> defines)
    {
        ArgumentNullException.ThrowIfNull(defines);

        var built = new List<string>();

        foreach (string part in Split(values ?? ""))
        {
            if (Reference(part) is { } name && defines.TryGetValue(name, out IReadOnlyList<string>? list))
            {
                built.AddRange(list);
                continue;
            }

            // Not a reference, or one nothing defines — a label either way, and
            // an unknown reference is better shown than silently dropped, since
            // dropping it would renumber everything after it.
            built.Add(Unquote(part));
        }

        return built;
    }

    /// <summary>The name a <c>$reference</c> points at, or null for a plain label.</summary>
    private static string? Reference(string part)
    {
        string trimmed = part.Trim();

        return trimmed.Length > 1 && trimmed[0] == '$' && trimmed[1..].All(c => char.IsLetterOrDigit(c) || c == '_')
            ? trimmed[1..]
            : null;
    }

    /// <summary>Comma-separated, leaving commas inside quotes alone.</summary>
    private static IEnumerable<string> Split(string text)
    {
        bool quoted = false;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == ',' && !quoted)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        // A trailing comment on the last entry of a list.
        int comment = IndexOfComment(trimmed);
        if (comment >= 0) trimmed = trimmed[..comment].Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static int IndexOfComment(string text)
    {
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') quoted = !quoted;
            else if (text[i] == ';' && !quoted) return i;
        }

        return -1;
    }
}
