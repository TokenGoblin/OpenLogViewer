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

    public void Reload()
    {
        SettingsFile? file = JsonSettingsFile.Read<SettingsFile>(Path);
        ThemeId = string.IsNullOrWhiteSpace(file?.ThemeId) ? null : file.ThemeId.Trim();
    }

    public void SetTheme(string? id)
    {
        string? trimmed = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        if (trimmed == ThemeId) return;

        ThemeId = trimmed;
        JsonSettingsFile.Write(Path, new SettingsFile { Version = 1, ThemeId = ThemeId });
    }

    private sealed class SettingsFile
    {
        public int Version { get; set; }
        public string? ThemeId { get; set; }
    }
}
