using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenLogViewer.Core;

/// <summary>Where an installed definition came from, kept beside it.</summary>
/// <param name="Name">The file, as it sits in the definitions folder.</param>
/// <param name="Signature">What it declares itself to be.</param>
/// <param name="Source">Where it was fetched from, or how it arrived.</param>
/// <param name="Sha256">The bytes that were accepted, so a later copy can be told apart.</param>
/// <param name="At">When it was installed.</param>
/// <param name="ForIdentity">What the ECU said, at the time this was fetched for it.</param>
public sealed record DefinitionProvenance(
    string Name,
    string Signature,
    string Source,
    string Sha256,
    DateTime At,
    IReadOnlyList<string> ForIdentity);

/// <summary>What importing a definition did, or why it was refused.</summary>
public sealed record DefinitionImportResult(
    string Problem, string Path, DefinitionProvenance? Provenance)
{
    public bool Accepted => Problem.Length == 0;
}

/// <summary>
/// Taking a definition somebody — or something — handed over, and deciding
/// whether to keep it.
///
/// <para>
/// <b>This is the one place in the application where an outside file becomes
/// part of how an engine is read and written.</b> A definition decides which
/// byte is the rev limiter; the dangerous-constant guard matches on names this
/// file declares; a write lands where this file says it should. So the checks
/// here are not hygiene, they are the gate, and they all run before anything
/// touches the definitions folder — that folder is scanned recursively on every
/// connection, so a bad file allowed into it is wrong for ever, not once.
/// </para>
/// <para>
/// What it does not do is decide whether the file is trustworthy in a larger
/// sense. It cannot: a definition is a text file, and a plausible one for the
/// wrong hardware looks exactly like a plausible one for the right hardware.
/// What it can do, and does, is refuse anything that does not declare the
/// signature the ECU actually reported, refuse anything that is not a usable
/// definition at all, and write down where the file came from so the question
/// can be asked later by somebody who can answer it.
/// </para>
/// </summary>
public static class DefinitionImport
{
    /// <summary>The record of everything installed this way, beside the definitions themselves.</summary>
    public const string Ledger = "imported-definitions.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Keeps <paramref name="text"/> as a definition, if it is one and if it is
    /// the one this ECU asked for.
    /// </summary>
    /// <param name="text">The definition itself.</param>
    /// <param name="folder">The definitions folder.</param>
    /// <param name="mustMatch">
    /// What the ECU reported. When any of these is the signature the file
    /// declares, it is kept; when none is, it is refused. Empty skips the check,
    /// for importing a definition with nothing plugged in.
    /// </param>
    /// <param name="source">Where it came from, for the record.</param>
    /// <param name="name">What to call it, or empty to name it after its signature.</param>
    public static DefinitionImportResult Keep(
        string text,
        string folder,
        IReadOnlyList<string> mustMatch,
        string source,
        string name = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(mustMatch);

        if (text.Length > DefinitionFile.LargestBytes)
            return new DefinitionImportResult("that is far larger than any firmware definition", "", null);

        DefinitionInspection found = DefinitionFile.Inspect(text);
        if (!found.IsUsable) return new DefinitionImportResult(found.Problem, "", null);

        // The check that matters. Exactly — not the looser matching a live
        // session is allowed, which would accept a definition declaring nothing
        // but "speeduino" and install it as the answer for every Speeduino from
        // now on.
        if (mustMatch.Count > 0 && !mustMatch.Any(said => IniCatalog.SameSignature(found.Signature, said)))
        {
            return new DefinitionImportResult(
                $"it declares \"{found.Signature}\", and the ECU reports "
                + $"\"{string.Join("\", \"", mustMatch)}\" — a definition for different firmware "
                + "decodes every channel from the wrong place without ever failing, so this is refused",
                "", null);
        }

        var definitions = new SafeFolder(folder);
        string filename = name.Length > 0 ? name : DefinitionFile.NameFor(found.Signature);

        string path;
        try
        {
            // Latin1, matching how a signature is read back out and how these
            // files are written by everyone who publishes them. A definition is
            // ASCII but for the odd degree sign, and round-tripping that through
            // UTF-8 would change bytes the parser reads back.
            path = definitions.Write(filename, text, Encoding.Latin1);
        }
        catch (ArgumentException e)
        {
            return new DefinitionImportResult(e.Message, "", null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefinitionImportResult($"it could not be written: {e.Message}", "", null);
        }

        var provenance = new DefinitionProvenance(
            Path.GetFileName(path),
            found.Signature,
            source,
            Sha256(text),
            DateTime.UtcNow,
            [.. mustMatch]);

        Record(folder, provenance);

        return new DefinitionImportResult("", path, provenance);
    }

    /// <summary>The same, for a file already on disk.</summary>
    public static DefinitionImportResult KeepFile(
        string path, string folder, IReadOnlyList<string> mustMatch, string source, string name = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);

        if (!file.Exists) return new DefinitionImportResult($"there is no file at {path}", "", null);

        if (file.Length > DefinitionFile.LargestBytes)
        {
            return new DefinitionImportResult(
                $"it is {file.Length / (1024 * 1024)} MB, far larger than any firmware definition", "", null);
        }

        string text;
        try
        {
            text = TuningText.Read(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefinitionImportResult($"it could not be read: {e.Message}", "", null);
        }

        return Keep(
            text, folder, mustMatch,
            source.Length > 0 ? source : path,
            name.Length > 0 ? name : Path.GetFileName(path));
    }

    /// <summary>Everything installed this way, newest first.</summary>
    public static IReadOnlyList<DefinitionProvenance> Imported(string folder)
    {
        string path = Path.Combine(folder, Ledger);

        try
        {
            if (!File.Exists(path)) return [];

            return JsonSerializer.Deserialize<List<DefinitionProvenance>>(File.ReadAllText(path), Json)
                   is { } kept
                ? [.. kept.OrderByDescending(p => p.At)]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // The record is a courtesy; losing it must never cost a definition.
            return [];
        }
    }

    /// <summary>Where a definition in use came from, when it was one of ours.</summary>
    public static DefinitionProvenance? Of(string folder, string name) =>
        Imported(folder).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void Record(string folder, DefinitionProvenance provenance)
    {
        try
        {
            List<DefinitionProvenance> kept =
            [
                provenance,
                .. Imported(folder).Where(p => !p.Name.Equals(provenance.Name, StringComparison.OrdinalIgnoreCase)),
            ];

            File.WriteAllText(Path.Combine(folder, Ledger), JsonSerializer.Serialize(kept, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The definition is the useful part; the record is a courtesy.
        }
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(text))).ToLowerInvariant();
}
