using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class WorkspaceTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        }
    }

    private string TempFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-ws-{Guid.NewGuid():N}");
        _temp.Add(path);
        return path;
    }

    [Fact]
    public void TheDefaultSitsInTheProfileRatherThanDocuments()
    {
        // My Documents is redirected into OneDrive on most machines, which both
        // buries recordings and uploads every one of them as it is written.
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Workspace.Default,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(Workspace.Default.StartsWith(documents, StringComparison.OrdinalIgnoreCase),
            $"the default is inside {documents}");
    }

    [Fact]
    public void TheDefaultIsShallow()
    {
        // "Not ten folders deep" was the requirement. Profile plus one.
        int depth = Workspace.Default.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

        Assert.True(depth <= 4, $"{Workspace.Default} is {depth} levels deep");
        Assert.EndsWith(Workspace.DefaultFolderName, Workspace.Default);
    }

    [Fact]
    public void RecordingsAndExportsSitUnderTheOneFolder()
    {
        var workspace = new Workspace(@"C:\Tuning");

        Assert.Equal(@"C:\Tuning", workspace.Root);
        Assert.Equal(@"C:\Tuning\Logs", workspace.Logs);
        Assert.Equal(@"C:\Tuning\Exports", workspace.Exports);
    }

    [Fact]
    public void NothingChosenMeansTheDefault()
    {
        Assert.True(new Workspace(null).IsDefault);
        Assert.True(new Workspace("   ").IsDefault);
        Assert.False(new Workspace(@"C:\Tuning").IsDefault);
    }

    [Fact]
    public void ARecordingIsNamedForWhenItWasTaken()
    {
        // So a folder of them sorts into the order they happened.
        var workspace = new Workspace(TempFolder());

        string path = workspace.NewRecording(new DateTime(2026, 8, 1, 21, 5, 3));

        Assert.Equal("live-2026-08-01_21-05-03.csv", Path.GetFileName(path));
        Assert.Equal(workspace.Logs, Path.GetDirectoryName(path));
        Assert.True(Directory.Exists(workspace.Logs), "the folder was not created");
    }

    [Fact]
    public void AFolderThatCannotBeWrittenFallsBackRatherThanThrowing()
    {
        // An unplugged drive or a network share that is not there should cost
        // the setting, not the recording that was about to start.
        string created = Workspace.Ensure(@"\\?\Z:\nowhere\at\all\Logs");

        Assert.True(Directory.Exists(created));
        Assert.StartsWith(Workspace.Default, created, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AUsableFolderIsOneThatCanBeCreated()
    {
        Assert.True(Workspace.IsUsable(TempFolder()));
        Assert.False(Workspace.IsUsable(null));
        Assert.False(Workspace.IsUsable(""));
        Assert.False(Workspace.IsUsable(@"\\?\Z:\nowhere\at\all"));
    }

    // ----- the setting ------------------------------------------------------

    private string SettingsPath()
    {
        string folder = TempFolder();
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "settings.json");
    }

    [Fact]
    public void TheChosenFolderSurvivesARestart()
    {
        string path = SettingsPath();
        string chosen = TempFolder();

        new SettingsStore(path).SetDataFolder(chosen);

        Assert.Equal(chosen, new SettingsStore(path).DataFolder);
    }

    [Fact]
    public void SavingOneSettingDoesNotDropAnother()
    {
        // Each setter used to write only its own field, so whichever was saved
        // second erased the first.
        string path = SettingsPath();
        string chosen = TempFolder();

        var store = new SettingsStore(path);
        store.SetDataFolder(chosen);
        store.SetTheme("gruvbox");

        var reloaded = new SettingsStore(path);
        Assert.Equal(chosen, reloaded.DataFolder);
        Assert.Equal("gruvbox", reloaded.ThemeId);
    }

    [Fact]
    public void ClearingTheFolderGoesBackToTheDefault()
    {
        string path = SettingsPath();

        var store = new SettingsStore(path);
        store.SetDataFolder(TempFolder());
        store.SetDataFolder(null);

        Assert.Null(new SettingsStore(path).DataFolder);
        Assert.True(new Workspace(new SettingsStore(path).DataFolder).IsDefault);
    }

    // ----- ECU definitions --------------------------------------------------

    [Fact]
    public void TheDefinitionsFolderSitsBesideLogsAndExports()
    {
        var workspace = new Workspace(TempFolder());

        Assert.Equal(Path.Combine(workspace.Root, "ECU definitions"), workspace.Definitions);
    }

    [Fact]
    public void ItIsSearchedBeforeTunerStudiosOwnFolders()
    {
        // A file someone went to the trouble of putting there is a more
        // deliberate answer than one a tool cached at some point in the past.
        var workspace = new Workspace(TempFolder());

        Assert.Equal(workspace.Definitions, workspace.DefinitionSearchPaths[0]);
        Assert.True(workspace.DefinitionSearchPaths.Count > 1);
    }

    [Fact]
    public void CreatingItLeavesANoteSayingWhatItIsFor()
    {
        // An empty folder called "ECU definitions" tells someone almost nothing.
        var workspace = new Workspace(TempFolder());

        string folder = workspace.EnsureDefinitions();
        string note = Path.Combine(folder, "PUT ECU DEFINITION FILES HERE.txt");

        Assert.True(File.Exists(note));

        string text = File.ReadAllText(note);
        Assert.Contains("no names, no", text, StringComparison.Ordinal);
        Assert.Contains("Speeduino", text, StringComparison.Ordinal);
        Assert.Contains("never uses the internet", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoteNamesEveryThingTheEcuSaidAboutItself()
    {
        // No reply says which of them is the signature — a Speeduino answers
        // both "Speeduino 2024.02.2" and "speeduino 202402", and only the second
        // is what an INI declares. Naming one would be misleading half the time.
        var workspace = new Workspace(TempFolder());

        string folder = workspace.EnsureDefinitions(["Speeduino 2024.02.2", "speeduino 202402"]);
        string text = File.ReadAllText(Path.Combine(folder, "PUT ECU DEFINITION FILES HERE.txt"));

        Assert.Contains("Speeduino 2024.02.2", text, StringComparison.Ordinal);
        Assert.Contains("speeduino 202402", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoteFollowsWhicheverEcuWasPluggedInLast()
    {
        var workspace = new Workspace(TempFolder());

        workspace.EnsureDefinitions(["speeduino 202402"]);
        string folder = workspace.EnsureDefinitions(["MS3 Format 0569.00"]);

        string text = File.ReadAllText(Path.Combine(folder, "PUT ECU DEFINITION FILES HERE.txt"));

        Assert.Contains("MS3 Format 0569.00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("speeduino", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIniDroppedInIsFound()
    {
        // The whole point: a definition this machine did not already have.
        var workspace = new Workspace(TempFolder());
        string folder = workspace.EnsureDefinitions();

        File.WriteAllText(
            Path.Combine(folder, "speeduino.ini"),
            "[MegaTune]\nsignature = \"speeduino 202402\"\n");

        IniFile found = Assert.Single(IniCatalog.Scan([folder]));

        Assert.Equal("speeduino 202402", found.Signature);
    }

    [Fact]
    public void ASubFolderIsSearchedToo()
    {
        // So a whole firmware folder can be dropped in unopened.
        var workspace = new Workspace(TempFolder());
        string folder = workspace.EnsureDefinitions();
        string inner = Directory.CreateDirectory(Path.Combine(folder, "speeduino-202402")).FullName;

        File.WriteAllText(
            Path.Combine(inner, "speeduino.ini"),
            "[MegaTune]\nsignature = \"speeduino 202402\"\n");

        Assert.Single(IniCatalog.Scan([folder]));
    }

    // ----- what answered on which port --------------------------------------

    [Fact]
    public void WhatAnsweredOnAPortSurvivesARestart()
    {
        // The whole value is in knowing before connecting: Windows calls a
        // Speeduino "Arduino Mega 2560", and having to connect once to find out
        // otherwise defeats the point of remembering at all.
        string path = SettingsPath();

        new SettingsStore(path).SetKnownEcus(new Dictionary<string, string>
        {
            [@"USB\VID_2341&PID_0042\95730333837351C01221"] = "Speeduino 2025.01.7",
        });

        Assert.Equal(
            "Speeduino 2025.01.7",
            new SettingsStore(path).KnownEcus[@"USB\VID_2341&PID_0042\95730333837351C01221"]);
    }

    [Fact]
    public void ASettingsFileWrittenBeforeThereWasAnyOfThisReadsAsNoneKnown()
    {
        string path = SettingsPath();
        File.WriteAllText(path, """{"Version":1,"ThemeId":"gruvbox"}""");

        Assert.Empty(new SettingsStore(path).KnownEcus);
    }

    // ----- logging rate -----------------------------------------------------

    [Fact]
    public void TheLoggingRateDefaultsTo25() =>
        Assert.Equal(25, new SettingsStore(SettingsPath()).LiveRate);

    [Fact]
    public void TheLoggingRateSurvivesARestart()
    {
        string path = SettingsPath();
        new SettingsStore(path).SetLiveRate(100);

        Assert.Equal(100, new SettingsStore(path).LiveRate);
    }

    [Fact]
    public void TheLoggingRateIsSavedAlongsideEverythingElse()
    {
        string path = SettingsPath();

        var store = new SettingsStore(path);
        store.SetLiveRate(50);
        store.SetTheme("gruvbox");

        var reloaded = new SettingsStore(path);
        Assert.Equal(50, reloaded.LiveRate);
        Assert.Equal("gruvbox", reloaded.ThemeId);
    }

    [Fact]
    public void ASettingsFileWrittenBeforeThereWasARateTakesTheDefault()
    {
        // Not zero, which is what a missing number reads as and which would
        // uncap a session nobody asked to uncap.
        string path = SettingsPath();
        File.WriteAllText(path, """{"Version":1,"ThemeId":"gruvbox"}""");

        Assert.Equal(25, new SettingsStore(path).LiveRate);
    }

    [Fact]
    public void AnAbsurdRateIsBroughtBackIntoRange()
    {
        string path = SettingsPath();

        var store = new SettingsStore(path);
        store.SetLiveRate(0);
        Assert.Equal(1, store.LiveRate);

        store.SetLiveRate(100_000);
        Assert.Equal(SettingsStore.MaximumLiveRate, store.LiveRate);
    }
}
