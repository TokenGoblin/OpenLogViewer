using System.IO;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Opening a second log and reading the first against it.
///
/// The arithmetic is covered in the core tests. What is covered here is the part
/// that would be wrong in a way nobody notices: whether the second log is binned
/// onto the <em>same</em> axes as the first. Two logs binned independently pick
/// their own ranges from their own data, so their cells do not line up, and
/// subtracting them compares 2,400 rpm against 2,650 while looking perfectly
/// reasonable.
/// </summary>
public class CompareWorkflowTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A log whose AFR is offset by a known amount, so the difference has a right
    /// answer. The two also cover different rpm ranges, which is what forces the
    /// axes question.
    /// </summary>
    private string WriteLog(string name, double afrOffset, int topRpm)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-cmp-{name}-{Guid.NewGuid():N}.csv");
        _temp.Add(path);

        using var writer = new StreamWriter(path);
        writer.WriteLine("Time,RPM,MAP,AFR");
        writer.WriteLine("s,rpm,kPa,");

        int row = 0;

        for (int rpm = 1000; rpm <= topRpm; rpm += 250)
            foreach (int map in (int[])[40, 80, 120, 160])
            {
                writer.WriteLine(
                    $"{row++ * 0.1:F1},{rpm},{map},{12.5 + afrOffset:F2}");
            }

        return path;
    }

    private static MainViewModel NewViewModel()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"olv-cmpvm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        var settings = new SettingsStore(Path.Combine(folder, "settings.json"));
        settings.SetDataFolder(Path.Combine(folder, "workspace"));

        return new MainViewModel(
            new PresetStore(Path.Combine(folder, "presets.json")),
            new FilterStore(Path.Combine(folder, "filters.json")),
            settings,
            new MathChannelStore(Path.Combine(folder, "math.json")));
    }

    private MainViewModel Loaded(double offset, int topRpm, out string comparePath)
    {
        MainViewModel vm = NewViewModel();

        vm.Load(WriteLog("before", 0, 4000));
        comparePath = WriteLog("after", offset, topRpm);

        return vm;
    }

    // ----- opening one ------------------------------------------------------------

    [Fact]
    public void ASecondLogCanBeOpenedAndIsReported()
    {
        MainViewModel vm = Loaded(0.5, 4000, out string second);

        string outcome = vm.LoadComparison(second);

        Assert.True(vm.HasComparison);
        Assert.Contains("Comparing against", outcome, StringComparison.Ordinal);
        Assert.Equal(["AFR", "MAP", "RPM", "Time"], vm.Overlap!.Shared);
    }

    [Fact]
    public void ComparingWithNoLogOpenSaysToOpenOneFirst()
    {
        MainViewModel vm = NewViewModel();

        Assert.Contains("Open a log first", vm.LoadComparison("nowhere.csv"), StringComparison.Ordinal);
        Assert.False(vm.HasComparison);
    }

    /// <summary>A file that will not load costs the comparison and not the session.</summary>
    [Fact]
    public void AFileThatWillNotLoadIsReportedRatherThanThrown()
    {
        MainViewModel vm = Loaded(0, 4000, out _);

        string outcome = vm.LoadComparison(Path.Combine(Path.GetTempPath(), "does-not-exist.csv"));

        Assert.Contains("Could not open", outcome, StringComparison.Ordinal);
        Assert.False(vm.HasComparison);
        Assert.NotNull(vm.Document);
    }

    [Fact]
    public void ComparingCanBeStopped()
    {
        MainViewModel vm = Loaded(0.5, 4000, out string second);
        vm.LoadComparison(second);

        vm.ClearComparison();

        Assert.False(vm.HasComparison);
        Assert.Null(vm.CompareDocument);
    }

    // ----- the difference ------------------------------------------------------------

    /// <summary>
    /// The whole point, end to end: two logs half a point apart in AFR, and every
    /// shared cell of the difference reads that.
    /// </summary>
    [Fact]
    public void TheTableBecomesTheDifferenceBetweenTheTwoRuns()
    {
        MainViewModel vm = Loaded(0.5, 4000, out string second);
        vm.LoadComparison(second);

        vm.XAxis = vm.Channels.Single(c => c.Name == "RPM");
        vm.YAxis = vm.Channels.Single(c => c.Name == "MAP");
        vm.ZAxis = vm.Channels.Single(c => c.Name == "AFR");

        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        HistogramTable table = vm.Table!;

        int checkedCells = 0;

        for (int c = 0; c < table.Columns; c++)
            for (int r = 0; r < table.Rows; r++)
            {
                if (table.Values[c, r] is not { } value) continue;

                // The first log is 0.5 leaner, so first minus second is −0.5.
                Assert.Equal(-0.5, value, 3);
                checkedCells++;
            }

        Assert.True(checkedCells > 0, "the two runs should share cells");
    }

    /// <summary>
    /// The failure that would be invisible: if the second log were binned onto its
    /// own range rather than the first's, its cells would sit at different rpm and
    /// the subtraction would compare the wrong ones. Here the second run goes a
    /// thousand rpm further, so an independently binned table would land on
    /// entirely different centres.
    /// </summary>
    [Fact]
    public void TheSecondLogIsBinnedOntoTheFirstsAxesAndNotItsOwn()
    {
        MainViewModel vm = Loaded(0.5, 5000, out string second);
        vm.LoadComparison(second);

        vm.XAxis = vm.Channels.Single(c => c.Name == "RPM");
        vm.YAxis = vm.Channels.Single(c => c.Name == "MAP");
        vm.ZAxis = vm.Channels.Single(c => c.Name == "AFR");

        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        // The axes are the first log's — it stopped at 4,000, so nothing here
        // should be centred beyond that however far the second run went.
        Assert.True(vm.Table!.ColumnCenters[^1] <= 4000,
            $"axes ran to {vm.Table.ColumnCenters[^1]:N0}, which is the second log's range");

        // And the difference is still the offset, not an artefact of misalignment.
        for (int c = 0; c < vm.Table.Columns; c++)
            for (int r = 0; r < vm.Table.Rows; r++)
                if (vm.Table.Values[c, r] is { } value)
                    Assert.Equal(-0.5, value, 3);
    }

    /// <summary>Switching the difference off leaves the first log's own table.</summary>
    [Fact]
    public void TheDifferenceCanBeTurnedOffToSeeTheLogItself()
    {
        MainViewModel vm = Loaded(0.5, 4000, out string second);
        vm.LoadComparison(second);

        vm.XAxis = vm.Channels.Single(c => c.Name == "RPM");
        vm.YAxis = vm.Channels.Single(c => c.Name == "MAP");
        vm.ZAxis = vm.Channels.Single(c => c.Name == "AFR");

        vm.ShowDifference = false;
        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        // 12.5 as logged, not a difference near zero.
        double? first = vm.Table!.Values[0, 0];

        Assert.NotNull(first);
        Assert.Equal(12.5, first!.Value, 3);
    }

    /// <summary>
    /// Two logs with nothing in common is refused with a reason rather than
    /// accepted and then showing an empty table.
    /// </summary>
    [Fact]
    public void TwoUnrelatedLogsAreRefusedWithAReason()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(WriteLog("before", 0, 4000));

        string other = Path.Combine(Path.GetTempPath(), $"olv-odd-{Guid.NewGuid():N}.csv");
        _temp.Add(other);
        File.WriteAllText(other, "Alpha,Beta\n1,2\n3,4\n");

        string outcome = vm.LoadComparison(other);

        Assert.False(vm.HasComparison);
        Assert.Contains("nothing to compare", outcome, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The summary says how much of the table the two runs actually share.</summary>
    [Fact]
    public void TheSummarySaysWhatChangedAndOverHowMuchOfTheTable()
    {
        MainViewModel vm = Loaded(0.5, 4000, out string second);
        vm.LoadComparison(second);

        vm.XAxis = vm.Channels.Single(c => c.Name == "RPM");
        vm.YAxis = vm.Channels.Single(c => c.Name == "MAP");
        vm.ZAxis = vm.Channels.Single(c => c.Name == "AFR");

        vm.RebuildHistogram(0, vm.Document!.SampleCount - 1);

        Assert.Contains("cells in both runs", vm.CompareSummary, StringComparison.Ordinal);
        Assert.Contains("average change", vm.CompareSummary, StringComparison.Ordinal);
        Assert.Contains("difference against", vm.Hint, StringComparison.Ordinal);
    }
}
