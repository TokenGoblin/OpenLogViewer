namespace OpenLogViewer.Core;

/// <summary>
/// Where the app keeps the files a user goes looking for: recordings it makes
/// and the exports it writes.
///
/// One folder, shallow, and named after the app, with everything under it. The
/// point is that a tuner in a garage can find a log without being told a path.
///
/// Deliberately not "My Documents". That is redirected into OneDrive on most
/// machines now, which puts recordings four levels deep and syncs every one of
/// them to the cloud as it is being written — a long session is tens of
/// megabytes of continuous upload, over whatever connection the car is near.
/// The user profile is not redirected and is one level shorter.
///
/// Settings stay where settings belong, under AppData. This is for the files
/// the user owns, not the ones the app owns.
/// </summary>
public sealed class Workspace
{
    /// <summary>Folder name used under the user's profile when nothing is chosen.</summary>
    public const string DefaultFolderName = "OpenLogViewer";

    public Workspace(string? root = null) =>
        Root = string.IsNullOrWhiteSpace(root) ? Default : Path.GetFullPath(root);

    /// <summary>The default location: a single folder in the user's profile.</summary>
    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        DefaultFolderName);

    public string Root { get; }

    /// <summary>Live recordings.</summary>
    public string Logs => Path.Combine(Root, "Logs");

    /// <summary>Anything written by Export.</summary>
    public string Exports => Path.Combine(Root, "Exports");

    /// <summary>
    /// Firmware definition files the user has supplied.
    ///
    /// A live connection cannot be decoded without the INI matching the ECU's
    /// signature, and the usual place to find one is wherever TunerStudio put
    /// it. That covers most people and not everyone: a Speeduino owner who
    /// tunes on another machine, anyone who has never installed TunerStudio, or
    /// a firmware too new for the copy on disk. This is somewhere obvious to put
    /// one, named so that it does not need explaining.
    /// </summary>
    public string Definitions => Path.Combine(Root, "ECU definitions");

    /// <summary>
    /// Every place a firmware definition might be, ours first.
    ///
    /// Ahead of TunerStudio's own folders deliberately: a file the user went to
    /// the trouble of putting here is a more deliberate answer than one a tool
    /// cached at some point in the past.
    /// </summary>
    public IReadOnlyList<string> DefinitionSearchPaths =>
        [Definitions, .. IniCatalog.DefaultSearchPaths];

    /// <summary>
    /// Creates the definitions folder and leaves a note in it saying what it is
    /// for.
    ///
    /// An empty folder called "ECU definitions" tells someone almost nothing.
    /// The note names the signature their ECU actually reported, which is the
    /// one piece of information that makes finding the right file possible.
    /// </summary>
    public string EnsureDefinitions(IReadOnlyList<string>? identity = null)
    {
        string folder = Ensure(Definitions);
        string readme = Path.Combine(folder, "PUT ECU DEFINITION FILES HERE.txt");

        try
        {
            // Rewritten each time, so the note names whichever ECU was last
            // plugged in rather than the first one ever tried.
            File.WriteAllText(readme, ReadmeText(identity));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The folder is the useful part; the note is a courtesy.
        }

        return folder;
    }

    /// <summary>
    /// All of them, not a guess at which is the signature.
    ///
    /// An ECU is asked several questions about itself and no reply says which
    /// it is: a Speeduino answers "Speeduino 2024.02.2" and "speeduino 202402",
    /// and only the second is what an INI declares. Naming one would be right
    /// half the time and misleading the rest, so both are listed and whichever
    /// matches settles it.
    /// </summary>
    private static string Reported(IReadOnlyList<string>? identity) =>
        identity is null or { Count: 0 }
            ? ""
            : ", one of which is:\n\n" + string.Join("\n", identity.Select(t => $"    {t}"));

    private static string ReadmeText(IReadOnlyList<string>? identity) =>
        $"""
        ECU definition files (.ini)
        ===========================

        Put your ECU's TunerStudio definition file in this folder and
        OpenLogViewer will find it. Sub-folders are searched too, so you can
        drop a whole firmware folder in here if that is easier.

        Why it is needed
        ----------------
        A live ECU sends a block of raw numbers and nothing else - no names, no
        units, no scaling. All of that lives in the .ini for that exact firmware
        build. Using the wrong one does not fail; it reads every channel from
        the wrong place and shows numbers that look perfectly reasonable. So
        OpenLogViewer will not connect until it finds the file matching the
        signature your ECU reports{Reported(identity)}

        Where to get one
        ----------------
          MegaSquirt   in the firmware download from msextra.com, or already on
                       this machine if TunerStudio is installed
          Speeduino    in the Speeduino firmware download, or from SpeedyLoader
          rusEFI       published with each build; the rusEFI console can also
                       save the one matching your board

        If TunerStudio is installed, its own copies are searched automatically:
          %USERPROFILE%\.efiAnalytics\TunerStudio\config\ecuDef
          Documents\TunerStudioProjects

        Nothing is downloaded. OpenLogViewer never uses the internet - this
        folder is how you give it a definition it does not already have.
        """;

    public bool IsDefault => string.Equals(
        Path.TrimEndingDirectorySeparator(Root),
        Path.TrimEndingDirectorySeparator(Default),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A path for a new recording, named for when it was taken so a folder of
    /// them sorts into the order they happened.
    /// </summary>
    public string NewRecording(DateTime at) =>
        Path.Combine(Ensure(Logs), $"live-{at:yyyy-MM-dd_HH-mm-ss}.csv");

    /// <summary>
    /// Creates a folder if it is missing and hands back its path, or falls back
    /// to the default location when the chosen one cannot be written.
    ///
    /// A folder that has gone — an unplugged drive, a network share that is not
    /// there — should cost the setting, not the recording that was about to
    /// start.
    /// </summary>
    public static string Ensure(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            string fallback = Path.Combine(Default, Path.GetFileName(folder));
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>Whether a folder can be chosen: it has to exist, or be creatable.</summary>
    public static bool IsUsable(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        try
        {
            Directory.CreateDirectory(folder);
            return Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
