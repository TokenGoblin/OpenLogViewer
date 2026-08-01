using System.IO;
using System.Linq;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The channel list is where a user finds anything, and where most of this
/// project's user-visible defects have been.
/// </summary>
public class ChannelListTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}");

    private MainViewModel NewViewModel() => new(
        new PresetStore(Path.Combine(_settings, "presets.json")),
        new FilterStore(Path.Combine(_settings, "filters.json")));

    private List<ChannelItem> Listed(MainViewModel vm) => [.. vm.ChannelView.Cast<ChannelItem>()];

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_settings)) Directory.Delete(_settings, recursive: true);
    }

    [Fact]
    public void ConstantChannelsAreHiddenByDefault()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        Assert.Contains(vm.Channels, c => c.Name == "Dead Channel");
        Assert.DoesNotContain(Listed(vm), c => c.Name == "Dead Channel");
    }

    [Fact]
    public void SearchingFindsAConstantChannelThatIsOtherwiseHidden()
    {
        // Regression: a wideband pinned at one value is exactly what needs
        // noticing, and searching for it by name used to return nothing.
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        vm.Search = "Dead";

        Assert.Contains(Listed(vm), c => c.Name == "Dead Channel");
    }

    [Fact]
    public void TheSummaryReportsHowManyAreWithheld()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        Assert.Contains("hidden", vm.FilterSummary);
    }

    [Fact]
    public void TurningOffHideUnusedRevealsEverything()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        vm.HideUnused = false;

        Assert.Equal(vm.Channels.Count, Listed(vm).Count);
    }

    [Fact]
    public void AConstantChannelStaysListedWhilePlotted()
    {
        // Otherwise a trace could be left on the plot with no way to switch off.
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        ChannelItem dead = vm.Channels.First(c => c.Name == "Dead Channel");
        dead.IsVisible = true;
        vm.Search = "";

        Assert.Contains(Listed(vm), c => c.Name == "Dead Channel");
    }

    [Fact]
    public void TheUsualChannelsArePlottedOnLoad()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        var plotted = vm.Channels.Where(c => c.IsVisible).Select(c => c.Name).ToList();

        Assert.Contains("RPM", plotted);
        Assert.Contains("MAP", plotted);
        Assert.Contains("AFR", plotted);
    }

    [Fact]
    public void PlottedChannelsGetDistinctColours()
    {
        // Assigning by list position used to give the default set near-identical
        // shades, because the channels a tuner picks are scattered alphabetically.
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        var colours = vm.Channels.Where(c => c.IsVisible).Select(c => c.Color).ToList();

        Assert.Equal(colours.Count, colours.Distinct().Count());
    }

    [Fact]
    public void ClearingAllHidesEvenChannelsTheFilterIsWithholding()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        vm.Channels.First(c => c.Name == "Dead Channel").IsVisible = true;
        vm.SetAllVisible(false);

        Assert.DoesNotContain(vm.Channels, c => c.IsVisible);
    }

    [Fact]
    public void ABulkChangeRaisesOneUpdateNotOnePerChannel()
    {
        // Without batching, switching a preset on a 179-channel log redrew the
        // plot once per channel.
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        int updates = 0;
        vm.PlotInvalidated += () => updates++;

        vm.SetAllVisible(false);

        Assert.Equal(1, updates);
    }

    [Fact]
    public void LoadingASecondLogReplacesTheChannelList()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());

        vm.Load(_harness.WriteCsv(("Boost", [1.0, 2.0, 3.0]), ("EGT", [700.0, 800.0, 900.0])));

        Assert.DoesNotContain(vm.Channels, c => c.Name == "RPM");
        Assert.Contains(vm.Channels, c => c.Name == "EGT");
    }
}
