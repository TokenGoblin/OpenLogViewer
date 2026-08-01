using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLogViewer.Core;

/// <summary>A named set of channels to plot together.</summary>
public sealed record ChannelPreset(string Name, IReadOnlyList<string> Channels)
{
    /// <summary>Short listing of the member channels, for tooltips.</summary>
    public string Summary => Channels.Count <= 6
        ? string.Join(", ", Channels)
        : $"{string.Join(", ", Channels.Take(6))}  +{Channels.Count - 6} more";
}

/// <summary>
/// Stores named channel selections on disk so they survive restarts.
///
/// Presets are matched by channel name rather than index, so one saved against
/// an MS3 log still applies to a different log that shares channel names. Names
/// that are not present are simply skipped.
/// </summary>
public sealed class PresetStore
{
    private const int MaxNameLength = 40;
    private const int MaxPresets = 100;

    // The file is plain text people will hand-edit, so read it forgivingly:
    // any property casing is accepted, and trailing commas or comments are fine.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<ChannelPreset> _presets = [];

    public PresetStore(string? path = null)
    {
        Path = path ?? DefaultPath;
        Reload();
    }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OpenLogViewer",
        "presets.json");

    public string Path { get; }

    public IReadOnlyList<ChannelPreset> Presets => _presets;

    /// <summary>
    /// Reads the file, tolerating absence and corruption. A malformed presets
    /// file must never stop the viewer from opening a log.
    /// </summary>
    public void Reload()
    {
        _presets.Clear();
        if (!File.Exists(Path)) return;

        try
        {
            var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(Path), Json);
            if (file?.Presets is null) return;

            foreach (StoredPreset stored in file.Presets)
            {
                string name = Clean(stored.Name);
                if (name.Length == 0 || stored.Channels is not { Count: > 0 }) continue;
                if (_presets.Any(p => Matches(p.Name, name))) continue;

                _presets.Add(new ChannelPreset(name, [.. stored.Channels.Where(c => !string.IsNullOrWhiteSpace(c))]));
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _presets.Clear();
        }
    }

    /// <summary>Adds a preset, replacing any existing one with the same name.</summary>
    public ChannelPreset Save(string name, IEnumerable<string> channels)
    {
        string clean = Clean(name);
        if (clean.Length == 0)
            throw new ArgumentException("A preset needs a name.", nameof(name));

        string[] list = channels
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (list.Length == 0)
            throw new ArgumentException("A preset needs at least one channel.", nameof(channels));

        var preset = new ChannelPreset(clean, list);
        int existing = _presets.FindIndex(p => Matches(p.Name, clean));

        if (existing >= 0) _presets[existing] = preset;
        else if (_presets.Count < MaxPresets) _presets.Add(preset);
        else throw new InvalidOperationException($"There is a limit of {MaxPresets} presets.");

        Persist();
        return preset;
    }

    public bool Delete(string name)
    {
        int index = _presets.FindIndex(p => Matches(p.Name, name));
        if (index < 0) return false;

        _presets.RemoveAt(index);
        Persist();
        return true;
    }

    public ChannelPreset? Find(string name) =>
        _presets.FirstOrDefault(p => Matches(p.Name, name));

    /// <summary>
    /// Writes via a temporary file so an interrupted save cannot leave the user
    /// with a truncated presets file.
    /// </summary>
    private void Persist()
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

        var file = new PresetFile
        {
            Version = 1,
            Presets = [.. _presets.Select(p => new StoredPreset { Name = p.Name, Channels = [.. p.Channels] })],
        };

        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
        File.Move(temp, Path, overwrite: true);
    }

    private static bool Matches(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims, collapses inner whitespace and caps the length.</summary>
    private static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        string collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxNameLength ? collapsed : collapsed[..MaxNameLength].TrimEnd();
    }

    private sealed class PresetFile
    {
        public int Version { get; set; }
        public List<StoredPreset>? Presets { get; set; }
    }

    private sealed class StoredPreset
    {
        public string? Name { get; set; }
        public List<string>? Channels { get; set; }
    }
}
