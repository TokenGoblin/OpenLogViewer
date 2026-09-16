using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>A definition already on this machine that nearly, but does not, match.</summary>
public sealed record DefinitionNearMiss(string Name, string Signature, string Path);

/// <summary>
/// What is missing when an ECU cannot be connected to, said precisely enough to
/// act on.
/// </summary>
/// <param name="Identity">Everything the ECU said about itself.</param>
/// <param name="Family">"Speeduino", "rusEFI", "MegaSquirt", or empty when unrecognised.</param>
/// <param name="Signature">The reply that looks like the signature a definition would declare.</param>
/// <param name="Version">The reply carrying a patch level, where the family has one.</param>
/// <param name="Filename">What the file is conventionally called, so it can be recognised.</param>
/// <param name="Nearby">Definitions on this machine that nearly match, nearest first.</param>
/// <param name="Folder">Where a definition has to end up.</param>
/// <param name="Where">Where this family's definitions come from, in words.</param>
/// <param name="Sources">
/// Addresses the file may be fetched from, nearest-first. <b>Empty is a
/// decision, not a gap</b> — see <see cref="DefinitionNeed.Describe"/>.
/// </param>
public sealed record DefinitionNeed(
    IReadOnlyList<string> Identity,
    string Family,
    string Signature,
    string Version,
    string Filename,
    IReadOnlyList<DefinitionNearMiss> Nearby,
    string Folder,
    string Where,
    IReadOnlyList<string> Sources);

/// <summary>
/// Working out what definition a connection needed, and where it comes from.
///
/// <para>
/// One place, because two callers ask the same question for different audiences:
/// the refusal a person reads when a connection fails, and the answer an agent
/// gets when it offers to go and find the file. Saying it twice would let the
/// two drift, and the agent's copy is the one carrying a licence decision.
/// </para>
/// <para>
/// <b>Speeduino and rusEFI publish their definitions under a licence that
/// permits fetching one; MegaSquirt's does not, and says so.</b> The MegaSquirt
/// licence restricts the definition specifically — "it is not permissable to use
/// the INI file to tune other hardware" — and scopes redistribution to media
/// accompanying a hardware sale. Its download path is also defended against
/// automation with a browser-agent check and a rotating token, which is a
/// sentence in itself. So for that family this returns no address at all and
/// says where to look instead. The decision lives here rather than in the agent,
/// because an agent asked to be helpful will otherwise find a way.
/// </para>
/// </summary>
public static partial class DefinitionNeeds
{
    /// <summary>What was needed, given what the ECU said and what is on this machine.</summary>
    public static DefinitionNeed Describe(
        IReadOnlyList<string> identity, IEnumerable<IniFile> catalogue, string folder)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(catalogue);

        string family = FamilyOf(identity);
        string signature = SignatureOf(identity, family);
        string version = VersionOf(identity, family, signature);

        return new DefinitionNeed(
            identity,
            family,
            signature,
            version,
            signature.Length > 0 ? DefinitionFile.NameFor(signature) : "",
            Nearby(signature, catalogue),
            folder,
            WhereFrom(family),
            SourcesFor(family, signature, version));
    }

    /// <summary>Which firmware family said this, from anything it said.</summary>
    public static string FamilyOf(IReadOnlyList<string> identity)
    {
        foreach (string said in identity)
        {
            if (said.Contains("speeduino", StringComparison.OrdinalIgnoreCase)) return "Speeduino";
            if (said.Contains("rusEFI", StringComparison.OrdinalIgnoreCase)) return "rusEFI";

            if (said.Contains("MS1", StringComparison.OrdinalIgnoreCase)
                || said.Contains("MS2", StringComparison.OrdinalIgnoreCase)
                || said.Contains("MS3", StringComparison.OrdinalIgnoreCase)
                || said.Contains("MegaSquirt", StringComparison.OrdinalIgnoreCase)
                || said.Contains("MicroSquirt", StringComparison.OrdinalIgnoreCase))
            {
                return "MegaSquirt";
            }
        }

        return "";
    }

    /// <summary>
    /// Which reply is the one a definition declares.
    ///
    /// No reply says which it is, so this is a judgement per family rather than
    /// a rule: a Speeduino answers both "Speeduino 2025.01.7" and
    /// "speeduino 202501" and only the second is ever written in an INI. Where
    /// nothing is recognised the longest reply is the better guess, since a
    /// signature carries more than a build string usually does.
    /// </summary>
    private static string SignatureOf(IReadOnlyList<string> identity, string family)
    {
        if (identity.Count == 0) return "";

        Func<string, bool> looksRight = family switch
        {
            "Speeduino" => s => SpeeduinoSignature().IsMatch(s),
            "rusEFI" => s => s.StartsWith("rusEFI", StringComparison.OrdinalIgnoreCase) && s.Contains('.'),
            "MegaSquirt" => s => s.Contains("Format", StringComparison.OrdinalIgnoreCase)
                                 || s.Contains("comms", StringComparison.OrdinalIgnoreCase),
            _ => _ => false,
        };

        return identity.FirstOrDefault(looksRight)
               ?? identity.OrderByDescending(s => s.Length).First();
    }

    /// <summary>
    /// The reply carrying a patch level, where the family has one the signature
    /// does not.
    ///
    /// This is not decoration. Speeduino's eight 202501.x releases all declare
    /// the same signature and ship three different definitions between them, so
    /// the signature alone cannot say which is wanted — the version can.
    /// </summary>
    private static string VersionOf(IReadOnlyList<string> identity, string family, string signature)
    {
        if (family != "Speeduino") return "";

        return identity.FirstOrDefault(s => SpeeduinoVersion().IsMatch(s) && s != signature) ?? "";
    }

    /// <summary>
    /// Definitions already here that nearly match, nearest first.
    ///
    /// The single most useful thing to tell somebody: "you have 202402, the
    /// board wants 202501" turns what reads as an unsupported ECU into a
    /// one-version errand. Ranked by how much of the signature they share, and
    /// anything sharing almost nothing is left out rather than padded in.
    /// </summary>
    private static IReadOnlyList<DefinitionNearMiss> Nearby(string signature, IEnumerable<IniFile> catalogue)
    {
        if (signature.Length == 0) return [];

        string wanted = Simplify(signature);

        return
        [
            .. catalogue
                .Select(i => (Ini: i, Shared: SharedPrefix(wanted, Simplify(i.Signature))))
                .Where(c => c.Shared >= 4 && !string.Equals(c.Ini.Signature, signature, StringComparison.Ordinal))
                .OrderByDescending(c => c.Shared)
                .ThenBy(c => c.Ini.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(c => new DefinitionNearMiss(c.Ini.Name, c.Ini.Signature, c.Ini.Path)),
        ];
    }

    private static string WhereFrom(string family) => family switch
    {
        "Speeduino" =>
            "Speeduino publishes its definition with each firmware release, and SpeedyLoader saves a "
            + "copy into your Downloads folder when it flashes. The file is the same one for every "
            + "board running that firmware.",

        "rusEFI" =>
            "rusEFI publishes a definition per board per build, and the rusEFI console keeps every one "
            + "it has fetched in .rusEFI\\ini_database.",

        "MegaSquirt" =>
            "MegaSquirt definitions ship inside the firmware download from msextra.com, and TunerStudio "
            + "installs around a hundred of them beside itself — which is where this looks first. If "
            + "yours is not among them you will need the definition from the firmware package you "
            + "flashed, and the variant matching your hardware: ms3.ini for a plain MS3, ms3pro.ini for "
            + "a Pro, and so on.",

        _ =>
            "A firmware definition comes from whoever wrote the firmware — usually inside the download "
            + "you flashed, or from the tuning software for that ECU.",
    };

    /// <summary>
    /// Where the file may be fetched from, and deliberately nothing for
    /// MegaSquirt. See the note on <see cref="DefinitionNeeds"/>.
    /// </summary>
    private static IReadOnlyList<string> SourcesFor(string family, string signature, string version) =>
        family switch
        {
            "Speeduino" => SpeeduinoSources(version, signature),
            "rusEFI" => RusEfiSources(signature),
            _ => [],
        };

    /// <summary>
    /// Built from the version rather than the signature, because the signature
    /// does not carry the patch level and several releases share one.
    /// "Speeduino 2025.01.7" names tag 202501.7: drop the word, then the first
    /// dot.
    /// </summary>
    private static IReadOnlyList<string> SpeeduinoSources(string version, string signature)
    {
        string tag = SpeeduinoTag(version);

        // No version to work from: the signature still names the series, which
        // is better than nothing, and its own tag exists.
        if (tag.Length == 0)
        {
            Match series = SpeeduinoSignature().Match(signature);
            if (!series.Success) return [];

            tag = series.Groups[1].Value;
        }

        return
        [
            $"https://raw.githubusercontent.com/speeduino/speeduino/{tag}/reference/speeduino.ini",
            $"https://speeduino.com/fw/{tag}.ini",
        ];
    }

    private static string SpeeduinoTag(string version)
    {
        Match parsed = SpeeduinoVersion().Match(version ?? "");
        if (!parsed.Success) return "";

        // "2025.01.7" -> "202501.7": only the first dot goes.
        string digits = parsed.Groups[1].Value;
        int first = digits.IndexOf('.');

        return first < 0 ? digits : digits.Remove(first, 1);
    }

    /// <summary>
    /// rusEFI's own lookup rule, which its console uses too: spaces and dots
    /// become path separators under the online index.
    /// </summary>
    private static IReadOnlyList<string> RusEfiSources(string signature)
    {
        if (!signature.StartsWith("rusEFI", StringComparison.OrdinalIgnoreCase)) return [];

        string[] parts = signature.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return [];

        // Only the white-label leads, and only it is lowercased — board names are
        // already lower and branch names are case-sensitive directories.
        parts[0] = parts[0].ToLowerInvariant();

        return [$"https://rusefi.com/online/ini/{string.Join('/', parts)}.ini"];
    }

    private static string Simplify(string text) =>
        new([.. text.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant)]);

    private static int SharedPrefix(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;

        return i;
    }

    /// <summary>"speeduino 202501" — the form an INI declares.</summary>
    [GeneratedRegex(@"^speeduino\s+(\d{6}[A-Za-z0-9.\-]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeeduinoSignature();

    /// <summary>"Speeduino 2025.01.7" — the form carrying the patch level.</summary>
    [GeneratedRegex(@"^speeduino\s+(\d{4}\.\d{2}(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeeduinoVersion();
}
