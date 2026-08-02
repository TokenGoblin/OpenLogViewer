using System.Text;
namespace OpenLogViewer.Core;

/// <summary>An INI on disk and the firmware signature it declares.</summary>
public sealed record IniFile(string Path, string Signature)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Finds the INI that matches a connected ECU.
///
/// This is not a convenience. Firmware versions move channels around inside the
/// realtime block, so decoding with the wrong INI does not fail — it reads every
/// channel from the wrong offset and produces numbers that look entirely
/// reasonable. Matching on the signature the ECU reports, and refusing when
/// nothing matches, is the only thing standing between a live session and
/// confident nonsense.
/// </summary>
public static class IniCatalog
{
    /// <summary>Where TunerStudio keeps its firmware definitions and projects.</summary>
    public static IReadOnlyList<string> DefaultSearchPaths
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            return
            [
                Path.Combine(home, ".efiAnalytics", "TunerStudio", "config", "ecuDef"),
                Path.Combine(documents, "TunerStudioProjects"),
                Path.Combine(home, "OneDrive", "Documents", "TunerStudioProjects"),
            ];
        }
    }

    /// <summary>
    /// Every readable INI under the given directories, with its signature.
    /// Files without one are skipped rather than reported: a directory of
    /// TunerStudio projects is full of INIs that are not firmware definitions.
    /// </summary>
    public static IReadOnlyList<IniFile> Scan(IEnumerable<string>? directories = null)
    {
        var found = new List<IniFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in directories ?? DefaultSearchPaths)
        {
            if (!Directory.Exists(directory)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.ini", SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!seen.Add(file)) continue;
                if (ReadSignature(file) is { Length: > 0 } signature)
                    found.Add(new IniFile(file, signature));
            }
        }

        return found;
    }

    /// <summary>
    /// The first of several candidate strings that matches an INI, with the INI
    /// it matched.
    ///
    /// An ECU is asked several questions about itself because no single one
    /// works across firmware families. This is what turns the answers into an
    /// identity: the signature is whichever reply a definition file recognises,
    /// and the rest are build strings.
    /// </summary>
    public static (IniFile Ini, string Signature)? MatchAny(
        IEnumerable<string> candidates, IEnumerable<IniFile> catalogue)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // Materialised because it is walked once per candidate, and Scan returns
        // a list only by convention.
        IniFile[] files = [.. catalogue];

        foreach (string candidate in candidates)
            if (Match(candidate, files) is { } ini)
                return (ini, candidate);

        return null;
    }

    /// <summary>
    /// The tune belonging to the TunerStudio project an INI came out of.
    ///
    /// Worth finding, because some of what a gauge needs is kept nowhere else.
    /// A MegaSquirt tachometer runs to <c>{rpmhigh}</c> and warns at
    /// <c>{rpmwarn}</c>, and those are TunerStudio's variables rather than the
    /// firmware's — not in the ECU, not derivable, and absent from a tune
    /// exported on its own. They live in the project, two directories up from
    /// its copy of the firmware definition.
    ///
    /// Null when the INI is a plain firmware definition rather than part of a
    /// project, which is the usual case for the ones under ecuDef.
    /// </summary>
    public static string? ProjectTuneFor(string iniPath)
    {
        ArgumentNullException.ThrowIfNull(iniPath);

        try
        {
            string? configuration = Path.GetDirectoryName(iniPath);
            if (configuration is null) return null;

            if (!Path.GetFileName(configuration).Equals("projectCfg", StringComparison.OrdinalIgnoreCase))
                return null;

            string? project = Path.GetDirectoryName(configuration);
            if (project is null) return null;

            string tune = Path.Combine(project, "CurrentTune.msq");

            return File.Exists(tune) ? tune : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The INI matching a signature, or null when none does.</summary>
    public static IniFile? Match(string signature, IEnumerable<IniFile> catalogue)
    {
        ArgumentNullException.ThrowIfNull(signature);

        string wanted = Normalise(signature);
        if (wanted.Length == 0) return null;

        // Exact first. Only then the looser match, which exists because an ECU
        // pads its signature and an INI sometimes carries a trailing comment.
        return catalogue.FirstOrDefault(i => Normalise(i.Signature) == wanted)
               ?? catalogue.FirstOrDefault(i => Normalise(i.Signature).StartsWith(wanted, StringComparison.Ordinal))
               ?? catalogue.FirstOrDefault(i => wanted.StartsWith(Normalise(i.Signature), StringComparison.Ordinal));
    }

    /// <summary>
    /// The signature an INI declares. Only the head of the file is read — the
    /// declaration is in the first section, and these run to a megabyte.
    /// </summary>
    public static string? ReadSignature(string path)
    {
        try
        {
            foreach (string line in File.ReadLines(path, Encoding.Latin1).Take(400))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("signature", StringComparison.OrdinalIgnoreCase)) continue;

                int equals = trimmed.IndexOf('=');
                if (equals < 0) continue;

                int open = trimmed.IndexOf('"', equals);
                if (open < 0) continue;

                int close = trimmed.IndexOf('"', open + 1);
                if (close < 0) continue;

                return trimmed[(open + 1)..close].Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string Normalise(string signature) =>
        new([.. signature.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant)]);
}
