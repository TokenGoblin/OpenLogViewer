using System.IO;
using System.Linq;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Presets, filters and the histogram, driven the way the UI drives them.
/// </summary>
public class AnalysisWorkflowTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}");

    private MainViewModel NewViewModel() => new(
        new PresetStore(Path.Combine(_settings, "presets.json")),
        new FilterStore(Path.Combine(_settings, "filters.json")));

    private MainViewModel Loaded()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());
        return vm;
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_settings)) Directory.Delete(_settings, recursive: true);
    }

    // ----- presets ----------------------------------------------------------

    [Fact]
    public void APresetRestoresExactlyTheChannelsItNamed()
    {
        MainViewModel vm = Loaded();
        vm.SetAllVisible(false);
        vm.Channels.First(c => c.Name == "RPM").IsVisible = true;
        vm.Channels.First(c => c.Name == "CLT").IsVisible = true;

        Assert.True(vm.SavePreset("Warmup"));

        vm.SetAllVisible(false);
        vm.ApplyPreset(vm.Presets.Single(p => p.Name == "Warmup"));

        Assert.Equal(["CLT", "RPM"], vm.Channels.Where(c => c.IsVisible).Select(c => c.Name).Order());
    }

    [Fact]
    public void APresetNamingNothingInThisLogLeavesTheSelectionAlone()
    {
        // Otherwise a preset from another ECU would silently blank the plot.
        MainViewModel vm = Loaded();
        vm.SetAllVisible(false);
        vm.Channels.First(c => c.Name == "RPM").IsVisible = true;
        vm.SavePreset("Just RPM");

        var foreign = new ChannelPreset("Ford", ["NotHere1", "NotHere2"]);
        vm.ApplyPreset(foreign);

        Assert.Equal(["RPM"], vm.Channels.Where(c => c.IsVisible).Select(c => c.Name));
        Assert.Contains("no channel in this log", vm.Hint);
    }

    [Fact]
    public void APartialMatchReportsWhatWasMissing()
    {
        MainViewModel vm = Loaded();

        vm.ApplyPreset(new ChannelPreset("Mixed", ["RPM", "NotHere"]));

        Assert.Equal(["RPM"], vm.Channels.Where(c => c.IsVisible).Select(c => c.Name));
        Assert.Contains("not in this log", vm.Hint);
    }

    [Fact]
    public void SavingWithNothingPlottedIsRefused()
    {
        MainViewModel vm = Loaded();
        vm.SetAllVisible(false);

        Assert.False(vm.SavePreset("Empty"));
        Assert.Empty(vm.Presets);
    }

    [Fact]
    public void PresetsSurviveANewViewModel()
    {
        MainViewModel vm = Loaded();
        vm.SavePreset("Kept");

        Assert.Contains(NewViewModel().Presets, p => p.Name == "Kept");
    }

    // ----- filters ----------------------------------------------------------

    [Fact]
    public void FiltersAreSuggestedForTheChannelsTheLogHasAndArriveOff()
    {
        MainViewModel vm = Loaded();

        Assert.Contains(vm.Filters, f => f.Filter.Channel == "CLT");
        Assert.Contains(vm.Filters, f => f.Filter.Channel == "RPM");
        Assert.All(vm.Filters, f => Assert.False(f.Enabled));
    }

    [Fact]
    public void ASavedFilterIsNotDuplicatedByASuggestion()
    {
        MainViewModel vm = Loaded();
        vm.Filters.First(f => f.Filter.Channel == "CLT").Enabled = true;

        MainViewModel reopened = NewViewModel();
        reopened.Load(_harness.WriteTypicalLog());

        Assert.Single(reopened.Filters.Where(f => f.Filter.Channel == "CLT"));
    }

    [Fact]
    public void EnablingAFilterShrinksTheTable()
    {
        MainViewModel vm = Loaded();
        vm.ShowHistogram = true;
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);
        int before = vm.Table!.SampleCount;

        vm.Filters.First(f => f.Filter.Channel == "CLT").Enabled = true;
        vm.RebuildHistogram(0, vm.Document.SampleCount - 1);

        Assert.True(vm.Table!.SampleCount < before);
        Assert.Contains("excluded by filters", vm.Hint);
    }

    // ----- histogram --------------------------------------------------------

    [Fact]
    public void TheHistogramDefaultsToTheAxesATunerExpects()
    {
        MainViewModel vm = Loaded();

        Assert.Equal("RPM", vm.XAxis?.Name);
        Assert.Equal("MAP", vm.YAxis?.Name);
        Assert.Equal("AFR", vm.ZAxis?.Name);
    }

    [Fact]
    public void AConstantChannelCanBeComparedAgainstButNotUsedAsAnAxis()
    {
        // A target is very often a fixed value; excluding it from the comparison
        // list hid the main use of the delta view.
        MainViewModel vm = Loaded();

        Assert.DoesNotContain(vm.AxisChannels, c => c.Name == "AFR Target");
        Assert.Contains(vm.CompareOptions, o => o.Channel?.Name == "AFR Target");
    }

    [Fact]
    public void ComparingAgainstATargetProducesSignedDeviations()
    {
        MainViewModel vm = Loaded();
        vm.ZCompare = vm.CompareOptions.First(o => o.Channel?.Name == "AFR Target");
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        Assert.True(vm.Table!.IsDelta);
        Assert.True(vm.Table.ShowsDeviation);
        // AFR runs 13.0-15.0 against a 14.7 target, so both signs appear.
        Assert.True(vm.Table.MinValue < 0);
    }

    [Fact]
    public void CountingSamplesIsNotTreatedAsADeviation()
    {
        // Counts are a magnitude even with a comparison set; shading them on a
        // diverging scale would put "no samples" at the neutral midpoint.
        MainViewModel vm = Loaded();
        vm.ZCompare = vm.CompareOptions.First(o => o.Channel?.Name == "AFR Target");
        vm.StatCount = true;
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        Assert.True(vm.Table!.IsDelta);
        Assert.False(vm.Table.ShowsDeviation);
    }

    [Fact]
    public void ACellTracesBackToTheSamplesThatBuiltIt()
    {
        MainViewModel vm = Loaded();
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);
        HistogramTable table = vm.Table!;

        (int Column, int Row) populated = default;
        for (int c = 0; c < table.Columns && populated == default; c++)
            for (int r = 0; r < table.Rows; r++)
                if (table.Counts[c, r] > 0) { populated = (c, r); break; }

        var visits = table.VisitsTo(populated.Column, populated.Row);

        Assert.NotEmpty(visits);
        Assert.All(table.SamplesIn(populated.Column, populated.Row),
            i => Assert.Equal(populated, table.CellOf(i)));
    }

    [Fact]
    public void SwitchingToTheHistogramDoesNotDisturbThePlottedChannels()
    {
        MainViewModel vm = Loaded();
        var before = vm.Channels.Where(c => c.IsVisible).Select(c => c.Name).ToList();

        vm.ShowHistogram = true;

        Assert.Equal(before, vm.Channels.Where(c => c.IsVisible).Select(c => c.Name));
    }
}
