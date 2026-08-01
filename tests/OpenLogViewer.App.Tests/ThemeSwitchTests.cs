using System.IO;
using System.Windows.Media;
using OpenLogViewer.App;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

public class ThemeSwitchTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ANewViewModelStartsOnTheDefaultTheme()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        // Only the view model is asserted on: ThemeManager is process-wide, so a
        // test class running in parallel could have set it in between.
        Assert.Equal(ThemeCatalog.DefaultId, vm.SelectedTheme.Id);
    }

    [Fact]
    public void ChoosingAThemeRecordsItAndItComesBackNextTime()
    {
        MainViewModel vm = _harness.NewViewModel(out string directory);
        string settings = Path.Combine(directory, "settings.json");

        vm.SelectedTheme = ThemeCatalog.Find("gruvbox");

        Assert.Equal("gruvbox", new SettingsStore(settings).ThemeId);
        Assert.Equal("gruvbox", new MainViewModel(
            new PresetStore(Path.Combine(directory, "presets.json")),
            new FilterStore(Path.Combine(directory, "filters.json")),
            new SettingsStore(settings)).SelectedTheme.Id);
    }

    [Fact]
    public void SwitchingThemeRepaintsTheTraces()
    {
        // A palette is chosen against one background. Keeping the old colours
        // would carry traces tuned for a dark ground onto a light one.
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.Load(_harness.WriteTypicalLog());

        Color[] before = [.. vm.Channels.Select(c => c.Color)];
        vm.SelectedTheme = ThemeCatalog.Find("daylight");
        Color[] after = [.. vm.Channels.Select(c => c.Color)];

        Assert.NotEqual(before, after);
        Assert.All(after, c => Assert.Contains(c, ThemeCatalog.Find("daylight").Series));
    }

    [Fact]
    public void PlottedChannelsGetTheFirstAndMostSeparatedColours()
    {
        // Entries next to each other in the palette are the ones checked to stay
        // apart, so what is on screen must take them in order.
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.Load(_harness.WriteTypicalLog());

        vm.SelectedTheme = ThemeCatalog.Find("nord");
        Color[] palette = ThemeCatalog.Find("nord").Series;

        Color[] plotted = [.. vm.Channels.Where(c => c.IsVisible).Select(c => c.Color)];

        Assert.NotEmpty(plotted);
        Assert.Equal(palette.Take(plotted.Length), plotted);
    }

    [Fact]
    public void NoTwoPlottedChannelsShareAColourWhileThePaletteHasRoom()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.Load(_harness.WriteTypicalLog());
        vm.SelectedTheme = ThemeCatalog.Find("tokyo-night");

        var plotted = vm.Channels.Where(c => c.IsVisible).Select(c => c.Color).ToList();

        Assert.True(plotted.Count <= ThemeCatalog.Find("tokyo-night").Series.Length);
        Assert.Equal(plotted.Count, plotted.Distinct().Count());
    }

    [Fact]
    public void PreviewingAThemeLeavesTheStoredPreferenceAlone()
    {
        // The --theme switch is for one-off runs such as a screenshot; it must
        // not overwrite what the user chose.
        MainViewModel vm = _harness.NewViewModel(out string directory);
        string settings = Path.Combine(directory, "settings.json");

        vm.SelectedTheme = ThemeCatalog.Find("paper");
        vm.PreviewTheme("monokai");

        Assert.Equal("monokai", vm.SelectedTheme.Id);
        Assert.Equal("paper", new SettingsStore(settings).ThemeId);
    }

    [Fact]
    public void PreviewingAnUnknownThemeFallsBackRatherThanThrowing()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        vm.PreviewTheme("not-a-theme");

        Assert.Equal(ThemeCatalog.DefaultId, vm.SelectedTheme.Id);
    }

    [Fact]
    public void ReselectingTheCurrentThemeIsANoOp()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.Load(_harness.WriteTypicalLog());
        vm.SelectedTheme = ThemeCatalog.Find("one-dark");

        Color[] before = [.. vm.Channels.Select(c => c.Color)];
        vm.SelectedTheme = ThemeCatalog.Find("one-dark");
        Color[] after = [.. vm.Channels.Select(c => c.Color)];

        Assert.Equal(before, after);
    }

    [Fact]
    public void ThemeSurvivesLoadingAnotherLog()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.SelectedTheme = ThemeCatalog.Find("solarized-dark");

        vm.Load(_harness.WriteTypicalLog());

        Assert.Equal("solarized-dark", vm.SelectedTheme.Id);
        Assert.All(vm.Channels.Select(c => c.Color),
            c => Assert.Contains(c, ThemeCatalog.Find("solarized-dark").Series));
    }
}

