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
