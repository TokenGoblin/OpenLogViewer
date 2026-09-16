namespace OpenLogViewer.Core;

/// <summary>What looking at a candidate definition found, before anything is kept.</summary>
/// <param name="Signature">What it declares itself to be, or empty when it declares nothing.</param>
/// <param name="Pages">Settings pages the firmware describes — zero means this is not a definition.</param>
/// <param name="Channels">Realtime channels it decodes.</param>
/// <param name="Problem">Why it cannot be used, or empty when it can.</param>
public sealed record DefinitionInspection(string Signature, int Pages, int Channels, string Problem)
{
    public bool IsUsable => Problem.Length == 0;
}

/// <summary>
/// Deciding whether a file somebody handed over is a firmware definition at all.
///
/// <para>
/// This exists because the catalogue's own bar is far too low to trust for
/// anything but scanning. <see cref="IniCatalog.Scan"/> keeps any file with a
/// quoted <c>signature</c> line in its first four hundred, which is right for a
/// scan — a folder of TunerStudio projects is full of INIs that are not firmware
/// definitions, and reading each one properly to find out would be slow. It is
/// badly wrong as an acceptance test. A truncated download, an HTML error page
/// saved with the wrong extension, or a definition for a different product will
/// all pass it, match an ECU, and produce a live session with <b>no channels, no
/// tune, and no error anywhere</b> — because the readers below are all
/// deliberately forgiving and simply return nothing on rubbish.
/// </para>
/// <para>
/// So a file is measured by what it actually yields, using the same predicate
/// the saved-tune path has used to pick a definition since before any of this:
/// a real definition describes at least one settings page. Checked <b>before</b>
/// the file is kept, never after — the definitions folder is scanned
/// recursively, so anything left lying in it is found again on every future
/// connection, and a bad file rejected into that folder would be a permanent
/// one.
/// </para>
/// </summary>
public static class DefinitionFile
{
    /// <summary>
    /// The largest definition worth reading. MS3's run past a megabyte and
    /// rusEFI's past half of one; ten is far beyond any of them and still far
    /// short of anything that would hurt to hold in memory.
    /// </summary>
    public const int LargestBytes = 10 * 1024 * 1024;

    /// <summary>What <paramref name="text"/> is, and whether it can be used at all.</summary>
    public static DefinitionInspection Inspect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Trim().Length == 0) return new DefinitionInspection("", 0, 0, "the file is empty");

        string signature = IniCatalog.SignatureIn(text) ?? "";

        if (signature.Length == 0)
        {
            return new DefinitionInspection(
                "", 0, 0,
                "it declares no signature, so nothing can say which firmware it belongs to");
        }

        TuneLayout layout;
        RealtimeLayout realtime;

        try
        {
            layout = TuneLayoutReader.Read(text, MsqIni.DefaultSymbols);
            realtime = MsqIni.ReadOutputChannels(text);
        }
        catch (Exception e) when (e is LogFormatException or FormatException or ArgumentException)
        {
            return new DefinitionInspection(signature, 0, 0, $"it could not be read: {e.Message}");
        }

        int pages = layout.Pages.Count;
        int channels = realtime.Fields.Count;

        // The same bar BestDefinitionFor has always used. A definition that
        // describes no settings pages is not one, whatever it calls itself.
        if (pages == 0)
        {
            return new DefinitionInspection(
                signature, 0, channels,
                "it describes no settings pages, so it is not a firmware definition "
                + "— a truncated download or a file saved under the wrong name looks like this");
        }

        return new DefinitionInspection(signature, pages, channels, "");
    }

    /// <summary>
    /// Inspects a file on disk, refusing one too large to be a definition before
    /// reading it into memory.
    /// </summary>
    public static DefinitionInspection InspectFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);

        if (!file.Exists) return new DefinitionInspection("", 0, 0, $"there is no file at {path}");

        if (file.Length > LargestBytes)
        {
            return new DefinitionInspection(
                "", 0, 0,
                $"it is {file.Length / (1024 * 1024)} MB, which is far larger than any firmware definition");
        }

        try
        {
            return Inspect(TuningText.Read(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DefinitionInspection("", 0, 0, $"it could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// The name to keep a definition under: the signature with its spaces
    /// removed, which is the convention TunerStudio's own cache follows and so
    /// the name anybody looking for it will recognise.
    /// </summary>
    public static string NameFor(string signature)
    {
        char[] invalid = Path.GetInvalidFileNameChars();

        string stem = new([
            .. (signature ?? "").Where(c => !char.IsWhiteSpace(c) && !invalid.Contains(c)),
        ]);

        return stem.Length == 0 ? "definition.ini" : stem + ".ini";
    }
}
