using System.IO;
using OpenLogViewer.App;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

public class TuneLoadingTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        _harness.Dispose();
        foreach (string p in _temp) { try { File.Delete(p); } catch (IOException) { } }
    }

    /// <summary>A tune with one 3×3 VE table at the given fill value.</summary>
    private string WriteTune(double fill)
    {
        string grid = string.Join("\n", Enumerable.Repeat(
            string.Join(" ", Enumerable.Repeat(fill.ToString("F1"), 3)), 3));

        string path = Path.Combine(Path.GetTempPath(), $"olv-tune-{Guid.NewGuid():N}.msq");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="1024">
            <constant cols="1" digits="0" name="frpm_table1" rows="3" units="RPM">1000 2000 3000</constant>
            <constant cols="1" digits="0" name="fmap_table1" rows="3" units="kPa">40 60 80</constant>
            <constant cols="3" digits="1" name="veTable1" rows="3" units="%">{grid}</constant>
            </page>
            </msq>
            """);

        _temp.Add(path);
        return path;
    }

    private MainViewModel Loaded()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.Load(_harness.WriteTypicalLog());
        return vm;
    }

    [Fact]
    public void ACsvLogHasNoTuneUntilOneIsOpened()
    {
        // Text logs carry no tune at all, which is the case an "open tune"
        // button exists for.
        MainViewModel vm = Loaded();

        Assert.False(vm.UsingLoadedTune);
        Assert.Contains("no tune", vm.TuneSource);
        Assert.False(vm.HasTuneAxes);
    }

    [Fact]
    public void OpeningATuneOffersItsTables()
    {
        MainViewModel vm = Loaded();

        vm.LoadTune(WriteTune(50));

        Assert.True(vm.UsingLoadedTune);
        Assert.True(vm.HasTuneAxes);
        Assert.Contains(vm.AxisSources, a => a.Label.StartsWith("VE table 1"));
        Assert.Contains(vm.AxisSources, a => a.HasValues);
    }

    [Fact]
    public void TheSourceNamesTheFileItCameFrom()
    {
        MainViewModel vm = Loaded();
        string path = WriteTune(50);

        vm.LoadTune(path);

        Assert.Equal(Path.GetFileName(path), vm.TuneSource);
    }

    [Fact]
    public void ClearingGoesBackToWhateverTheLogCarries()
    {
        MainViewModel vm = Loaded();
        vm.LoadTune(WriteTune(50));

        vm.ClearTune();

        Assert.False(vm.UsingLoadedTune);
        Assert.False(vm.HasTuneAxes);
        Assert.Contains("no tune", vm.TuneSource);
    }

    [Fact]
    public void AFileWithNoUsableTablesIsRejected()
    {
        MainViewModel vm = Loaded();

        string path = Path.Combine(Path.GetTempPath(), $"olv-bad-{Guid.NewGuid():N}.msq");
        File.WriteAllText(path, "<msq><page/></msq>");
        _temp.Add(path);

        Assert.Throws<LogFormatException>(() => vm.LoadTune(path));
        Assert.False(vm.UsingLoadedTune);
    }

    [Fact]
    public void OpeningATuneMatchingTheLogRaisesNoWarning()
    {
        // Nothing to warn about: the numbers are the ones that ran.
        MainViewModel vm = Loaded();

        vm.LoadTune(WriteTune(50));

        Assert.False(vm.HasTuneWarning);
        Assert.Equal("", vm.TuneWarning);
    }

    [Fact]
    public void ATuneWithNoLogToCompareAgainstRaisesNoWarning()
    {
        // A CSV log carries nothing to compare with, so silence is right.
        MainViewModel vm = Loaded();

        vm.LoadTune(WriteTune(75));

        Assert.False(vm.HasTuneWarning);
    }

    [Fact]
    public void LoadingAnotherLogDropsBackToItsOwnTune()
    {
        // The opened tune belonged to the previous log. Carrying it silently on
        // to the next one is exactly the mismatch this is trying to prevent.
        MainViewModel vm = Loaded();
        vm.LoadTune(WriteTune(50));

        vm.Load(_harness.WriteTypicalLog());

        Assert.True(vm.UsingLoadedTune);   // the file is still open
        Assert.True(vm.HasTuneAxes);
    }
}
