using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLogViewer.Core;

/// <summary>
/// Reads and writes the small JSON files under %APPDATA% that hold user
/// settings. These are plain text people will hand-edit, so reads accept any
/// property casing, comments and trailing commas, and a malformed file is
/// treated as absent rather than allowed to stop the app.
/// </summary>
internal static class JsonSettingsFile
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes via a temporary file, so an interrupted save cannot leave the user
    /// with a truncated settings file.
    /// </summary>
    public static void Write<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

        // A scratch name of its own per write, rather than one shared
        // "settings.json.tmp". Two writers landing together — a second copy of
        // the application, or a background thread noting something while the
        // window saves a preference — otherwise write into the same file each is
        // half way through, and what lands is not one of the two versions but a
        // splice of both.
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            Replace(temp, path);
        }
        catch (Exception)
        {
            // A write that failed must not leave its scratch file behind as
            // well; with a name of its own, nothing else would ever clear it up.
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // Nothing useful to do about a scratch file that will not go.
            }

            throw;
        }
    }

    /// <summary>Attempts at the move before a save is reported failed.</summary>
    private const int MoveAttempts = 12;

    /// <summary>
    /// Moves the finished file into place, retrying briefly.
    ///
    /// Two writers replacing the same destination collide even with a scratch
    /// file each: Windows reports the sharing violation as
    /// <see cref="UnauthorizedAccessException"/>, and it is transient — what
    /// holds the destination is the other writer's own move, finishing. Two
    /// copies of the application running at once are enough to produce it.
    ///
    /// Worth retrying rather than throwing because of where the throw comes out.
    /// A failed save is not only a lost preference: this is also how a dead link
    /// is written down, from a background thread, in the middle of a recovery
    /// that the exception would abandon.
    /// </summary>
    private static void Replace(string temp, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e)
                when (e is UnauthorizedAccessException or IOException && attempt < MoveAttempts)
            {
                // Longer each time. The collision clears in milliseconds, and a
                // tight spin against a file lock is a good way to extend one.
                Thread.Sleep(attempt * 5);
            }
        }
    }

    public static string InAppData(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenLogViewer",
        fileName);
}
