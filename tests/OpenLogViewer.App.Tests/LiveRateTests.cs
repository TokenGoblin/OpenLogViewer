using System.IO;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

public class LiveRateTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ANewViewModelLogsAt25Hz()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.Equal(25, vm.LiveRate);
    }

    [Fact]
    public void ChoosingARateRecordsItAndItComesBackNextTime()
    {
        MainViewModel vm = _harness.NewViewModel(out string directory);

        vm.LiveRate = 100;

        Assert.Equal(100, new SettingsStore(Path.Combine(directory, "settings.json")).LiveRate);
    }

    [Fact]
    public void TheOfferedRatesIncludeTheDefaultAndSpanBothUses()
    {
        // Fuelling work wants the low end and transient work the high; a list
        // that omitted the default would leave no way back to it.
        Assert.Contains(SettingsStore.DefaultLiveRate, MainViewModel.LiveRates);
        Assert.True(MainViewModel.LiveRates.Min() <= 10);
        Assert.True(MainViewModel.LiveRates.Max() >= 100);
    }

    [Fact]
    public void ChangingTheRateSaysWhenItTakesEffect()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        vm.LiveRate = 50;

        Assert.Contains("50", vm.Hint);
    }

    [Fact]
    public void SettingTheSameRateAgainRaisesNothing()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.LiveRate = vm.LiveRate;

        Assert.Empty(raised);
    }
}
