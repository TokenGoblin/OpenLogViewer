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
