using System.IO;
using System.Linq;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Presets, filters, the histogram and the scatter, driven the way the UI
/// drives them.
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

        Assert.Single(reopened.Filters, f => f.Filter.Channel == "CLT");
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

    // ----- a marked span, read on the table ---------------------------------

    [Fact]
    public void NothingIsMarkedOnTheTableUntilASpanIsMarkedOnThePlot()
    {
        MainViewModel vm = Loaded();
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        Assert.Null(vm.VisitedCells);
    }

    [Fact]
    public void AMarkedSpanNamesTheCellsItReached()
    {
        MainViewModel vm = Loaded();
        vm.UpdateSelection((0, 9));
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        Assert.NotNull(vm.VisitedCells);
        Assert.False(vm.VisitedCells!.IsEmpty);
        Assert.Equal(10, vm.VisitedCells.Samples);
        Assert.Contains("reached", vm.Hint);
    }

    [Fact]
    public void AShorterSpanReachesNoMoreCellsThanALongerOne()
    {
        MainViewModel vm = Loaded();
        int last = vm.Document is null ? 0 : vm.Document.SampleCount - 1;

        vm.UpdateSelection((0, last));
        vm.RebuildHistogram(0, last);
        int whole = vm.VisitedCells!.Cells;

        vm.UpdateSelection((0, last / 4));
        vm.RebuildHistogram(0, last);

        Assert.True(vm.VisitedCells!.Cells <= whole);
    }

    [Fact]
    public void ClearingTheSpanClearsTheMarking()
    {
        MainViewModel vm = Loaded();
        vm.UpdateSelection((0, 9));
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        vm.UpdateSelection(null);
        vm.RebuildHistogram(0, vm.Document.SampleCount - 1);

        Assert.Null(vm.VisitedCells);
    }

    [Fact]
    public void TheStatusLineStillReportsTheTableAsWellAsTheSpan()
    {
        // The span is appended to what the table said, not swapped for it: how
        // many samples the table rests on is still true and still the thing that
        // explains a sparse table.
        MainViewModel vm = Loaded();
        vm.UpdateSelection((0, 9));
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        Assert.Contains("samples across", vm.Hint);
        Assert.Contains("reached", vm.Hint);
    }

    // ----- scatter ----------------------------------------------------------

    [Fact]
    public void TheThreeViewsAreExclusiveAndThePlotIsTheDefault()
    {
        MainViewModel vm = Loaded();

        Assert.True(vm.ShowLog);
        Assert.False(vm.ShowHistogram);
        Assert.False(vm.ShowScatter);

        vm.ShowScatter = true;

        Assert.True(vm.ShowScatter);
        Assert.False(vm.ShowLog);
        Assert.False(vm.ShowHistogram);
    }

    [Fact]
    public void TheScatterAndTheTableShareTheirAxesAndTheirPanel()
    {
        // Picking RPM against MAP once should survive switching between the two
        // readings of it — they are one setting, not two.
        MainViewModel vm = Loaded();
        vm.ShowHistogram = true;

        Assert.True(vm.ShowAxisPanel);
        (string? x, string? y, string? z) = (vm.XAxis?.Name, vm.YAxis?.Name, vm.ZAxis?.Name);

        vm.ShowScatter = true;

        Assert.True(vm.ShowAxisPanel);
        Assert.Equal((x, y, z), (vm.XAxis?.Name, vm.YAxis?.Name, vm.ZAxis?.Name));
    }

    [Fact]
    public void TheScatterKeepsEverySampleWhereTheTableWouldHaveBinnedThem()
    {
        MainViewModel vm = Loaded();
        vm.ShowScatter = true;
        vm.RebuildScatter(0, vm.Document!.SampleCount - 1);

        Assert.Equal(vm.Document.SampleCount, vm.Points!.Count);
        Assert.Equal(0, vm.Points.Dropped);
    }

    [Fact]
    public void EnablingAFilterShrinksTheScatterAndSaysByHowMuch()
    {
        MainViewModel vm = Loaded();
        vm.ShowScatter = true;
        vm.RebuildScatter(0, vm.Document!.SampleCount - 1);
        int before = vm.Points!.Count;

        vm.Filters.First(f => f.Filter.Channel == "CLT").Enabled = true;
        vm.RebuildScatter(0, vm.Document.SampleCount - 1);

        Assert.True(vm.Points!.Count < before);
        Assert.Equal(before - vm.Points.Count, vm.Points.Filtered);
        Assert.Contains("excluded by filters", vm.Hint);
    }

    [Fact]
    public void ComparingAgainstATargetGivesTheScatterSignedDeviations()
    {
        MainViewModel vm = Loaded();
        vm.ZCompare = vm.CompareOptions.First(o => o.Channel?.Name == "AFR Target");
        vm.ShowScatter = true;
        vm.RebuildScatter(0, vm.Document!.SampleCount - 1);

        Assert.True(vm.Points!.IsDelta);
        Assert.True(vm.Points.ZMin < 0);
        Assert.True(vm.Points.ZMax > 0);
    }

    [Fact]
    public void AMarkTracesBackToTheSamplesThatMadeIt()
    {
        MainViewModel vm = Loaded();
        vm.ShowScatter = true;
        vm.RebuildScatter(0, vm.Document!.SampleCount - 1);

        ScatterPlot points = vm.Points!;
        ScatterBins bins = points.Bin(64, 64);

        int occupied = Array.FindIndex(bins.Counts, c => c > 0);
        (int column, int row) = (occupied % bins.Columns, occupied / bins.Columns);

        IReadOnlyList<int> traced = points.SamplesIn(bins, column, row);

        Assert.Equal(bins.Counts[occupied], traced.Count);
        Assert.NotEmpty(ScatterPlot.VisitsAmong(traced));

        // Every sample it traced back really does fall inside that block, so a
        // click cannot frame a stretch of log that had nothing to do with the
        // mark that was clicked.
        double xStep = (bins.XMax - bins.XMin) / bins.Columns;
        double yStep = (bins.YMax - bins.YMin) / bins.Rows;

        Assert.All(traced, i =>
        {
            Assert.InRange(
                points.X.At(i),
                bins.XMin + (column * xStep) - 1e-6,
                bins.XMin + ((column + 1) * xStep) + 1e-6);

            Assert.InRange(
                points.Y.At(i),
                bins.YMin + (row * yStep) - 1e-6,
                bins.YMin + ((row + 1) * yStep) + 1e-6);
        });
    }

    [Fact]
    public void SwitchingToTheScatterDoesNotDisturbThePlottedChannels()
    {
        MainViewModel vm = Loaded();
        var before = vm.Channels.Where(c => c.IsVisible).Select(c => c.Name).ToList();

        vm.ShowScatter = true;

        Assert.Equal(before, vm.Channels.Where(c => c.IsVisible).Select(c => c.Name));
    }
}
