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

    /// <summary>Samples per second to record live, or zero for as fast as the link goes.</summary>
    public double LiveRate { get; private set; } = DefaultLiveRate;

    /// <summary>
    /// What a session records when nothing has been chosen. Well above what a
    /// wideband can actually resolve, and far below what a USB link will offer.
    /// </summary>
    public const double DefaultLiveRate = 25;

    public void Reload()
    {
        SettingsFile? file = JsonSettingsFile.Read<SettingsFile>(Path);
        ThemeId = string.IsNullOrWhiteSpace(file?.ThemeId) ? null : file.ThemeId.Trim();
        DataFolder = string.IsNullOrWhiteSpace(file?.DataFolder) ? null : file.DataFolder.Trim();

        // A missing or nonsensical rate takes the default rather than being
        // honoured: a zero read out of an older settings file would silently
        // uncap a session that was never asked to be uncapped.
        LiveRate = file?.LiveRate is { } rate && rate is > 0 and <= MaximumLiveRate
            ? rate
            : DefaultLiveRate;

        SingleRequestBlock = file?.SingleRequestBlock ?? false;
    }

    /// <summary>Above this the cap is meaningless — no link answers that fast.</summary>
    public const double MaximumLiveRate = 1000;

    /// <summary>
    /// Ask for the realtime block in one request rather than in blocking-factor
    /// pieces. Faster where the firmware allows it, fatal where it does not.
    /// </summary>
    public bool SingleRequestBlock { get; private set; }

    public void SetSingleRequestBlock(bool single)
    {
        if (single == SingleRequestBlock) return;

        SingleRequestBlock = single;
        Persist();
    }

    public void SetLiveRate(double rate)
    {
        double clamped = Math.Clamp(rate, 1, MaximumLiveRate);
        if (clamped == LiveRate) return;

        LiveRate = clamped;
        Persist();
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
        LiveRate = LiveRate,
        SingleRequestBlock = SingleRequestBlock,
    });

    private sealed class SettingsFile
    {
        public int Version { get; set; }
        public string? ThemeId { get; set; }
        public string? DataFolder { get; set; }
        public double? LiveRate { get; set; }
        public bool? SingleRequestBlock { get; set; }
    }
}
