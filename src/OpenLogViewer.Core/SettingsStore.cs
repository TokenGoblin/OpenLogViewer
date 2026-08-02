namespace OpenLogViewer.Core;

/// <summary>
/// Small preferences that outlive a session and belong to the person rather than
/// to any one log — currently just the chosen colour scheme.
/// </summary>
public sealed class SettingsStore
{
    public SettingsStore(string? path = null)
    {
        Path = path ?? JsonSettingsFile.InAppData("settings.json");
        Reload();
    }

    public string Path { get; }

    /// <summary>Identifier of the active theme, or null to take the app's default.</summary>
    public string? ThemeId { get; private set; }

    /// <summary>
    /// Where recordings and exports go, or null for the default. Kept as the
    /// folder the user chose rather than the folders beneath it, so moving the
    /// workspace moves everything at once.
    /// </summary>
    public string? DataFolder { get; private set; }

    public void Reload()
    {
        SettingsFile? file = JsonSettingsFile.Read<SettingsFile>(Path);
        ThemeId = string.IsNullOrWhiteSpace(file?.ThemeId) ? null : file.ThemeId.Trim();
        DataFolder = string.IsNullOrWhiteSpace(file?.DataFolder) ? null : file.DataFolder.Trim();
    }

    public void SetDataFolder(string? folder)
    {
        string? trimmed = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (trimmed == DataFolder) return;

        DataFolder = trimmed;
        Persist();
    }

    public void SetTheme(string? id)
    {
        string? trimmed = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        if (trimmed == ThemeId) return;

        ThemeId = trimmed;
        Persist();
    }

    /// <summary>
    /// Writes the whole file. Settings are saved together rather than one at a
    /// time, or saving the second would drop the first.
    /// </summary>
    private void Persist() => JsonSettingsFile.Write(Path, new SettingsFile
    {
        Version = 1,
        ThemeId = ThemeId,
        DataFolder = DataFolder,
    });

    private sealed class SettingsFile
    {
        public int Version { get; set; }
        public string? ThemeId { get; set; }
        public string? DataFolder { get; set; }
    }
}
