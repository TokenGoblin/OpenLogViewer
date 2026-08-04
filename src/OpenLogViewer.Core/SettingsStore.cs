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
    /// Whether connecting starts a recording straight away.
    ///
    /// Off by default: connecting watches, and recording is asked for. A session
    /// is opened far more often to see whether the link works, to read a gauge or
    /// to check a change than to capture anything — and every one of those used to
    /// leave a file behind, so the recordings folder filled with runs nobody
    /// wanted and the one that mattered had to be found among them.
    ///
    /// The cost of this default is a run somebody meant to capture and did not.
    /// That is a real cost, and it is why the state is stated wherever a session
    /// is: the toolbar button, the status bar and the hint all say which of the
    /// two is happening, rather than leaving it to be inferred from silence.
    /// </summary>
    public bool RecordOnConnect { get; private set; }

    /// <summary>
    /// Where the last recording was saved by hand, so the next Save As opens
    /// where the last one went rather than back at the workspace every time.
    /// </summary>
    public string? RecordingFolder { get; private set; }

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

        // Absent takes the default rather than the old behaviour. A settings file
        // written before this was a choice came from a version that always
        // recorded, so this does change what happens to an existing install — and
        // that is the intent, not an oversight. Anyone who wants it back ticks it
        // once and it persists.
        RecordOnConnect = file?.RecordOnConnect ?? false;

        RecordingFolder = string.IsNullOrWhiteSpace(file?.RecordingFolder)
            ? null
            : file.RecordingFolder.Trim();

        Units = Enum.TryParse(file?.Units, out UnitSystem units) ? units : UnitSystem.AsReported;

        KnownEcus = file?.KnownEcus is { Count: > 0 } known
            ? new Dictionary<string, string>(known, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What answered on each serial device, keyed by its hardware id.
    ///
    /// Remembered between sessions because it is most wanted before connecting:
    /// Windows calls a Speeduino "Arduino Mega 2560", which names the chip and
    /// not the ECU, and having to connect once to find out defeats the purpose.
    /// The hardware id rather than the COM number, so a replug that lands on a
    /// different port is still recognised.
    /// </summary>
    public IReadOnlyDictionary<string, string> KnownEcus { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which units to show readings in. Defaults to whatever the ECU reports,
    /// because that is the only setting that cannot be wrong.
    /// </summary>
    public UnitSystem Units { get; private set; } = UnitSystem.AsReported;

    public void SetUnits(UnitSystem units)
    {
        if (units == Units) return;

        Units = units;
        Persist();
    }

    public void SetKnownEcus(IReadOnlyDictionary<string, string> known)
    {
        if (known.Count == KnownEcus.Count &&
            known.All(e => KnownEcus.GetValueOrDefault(e.Key) == e.Value))
            return;

        KnownEcus = new Dictionary<string, string>(known, StringComparer.OrdinalIgnoreCase);
        Persist();
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

    public void SetRecordOnConnect(bool record)
    {
        if (record == RecordOnConnect) return;

        RecordOnConnect = record;
        Persist();
    }

    public void SetRecordingFolder(string? folder)
    {
        string? trimmed = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        if (trimmed == RecordingFolder) return;

        RecordingFolder = trimmed;
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
        RecordOnConnect = RecordOnConnect,
        RecordingFolder = RecordingFolder,
        KnownEcus = KnownEcus.Count > 0 ? new Dictionary<string, string>(KnownEcus) : null,
        Units = Units.ToString(),
    });

    private sealed class SettingsFile
    {
        public int Version { get; set; }
        public string? ThemeId { get; set; }
        public string? DataFolder { get; set; }
        public double? LiveRate { get; set; }
        public bool? SingleRequestBlock { get; set; }
        public bool? RecordOnConnect { get; set; }
        public string? RecordingFolder { get; set; }
        public Dictionary<string, string>? KnownEcus { get; set; }
        public string? Units { get; set; }
    }
}
