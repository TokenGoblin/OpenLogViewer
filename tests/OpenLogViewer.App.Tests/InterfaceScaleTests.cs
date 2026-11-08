using System.IO;
using System.Windows;
using System.Windows.Media;
using OpenLogViewer.App;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

public class InterfaceScaleTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void AnyValueLandsOnAnOfferedStep()
    {
        Assert.Equal(1.0, InterfaceScale.Snap(0.2));
        Assert.Equal(1.5, InterfaceScale.Snap(1.45));
        Assert.Equal(2.0, InterfaceScale.Snap(9));
        Assert.Equal(1.0, InterfaceScale.Snap(double.NaN));
    }

    [Fact]
    public void ZoomingStepsThroughTheSizesAndStopsAtTheEnds()
    {
        Assert.Equal(1.15, InterfaceScale.Larger(1.0));
        Assert.Equal(2.0, InterfaceScale.Larger(2.0));
        Assert.Equal(1.25, InterfaceScale.Smaller(1.5));
        Assert.Equal(1.0, InterfaceScale.Smaller(1.0));
    }

    [Fact]
    public void PublishingWritesAScaleTheXamlCanUse()
    {
        var resources = new ResourceDictionary();
        InterfaceScale.Publish(resources, 1.5);

        var transform = Assert.IsType<ScaleTransform>(resources["UiScale"]);
        Assert.Equal(1.5, transform.ScaleX);
        Assert.Equal(1.5, transform.ScaleY);
        Assert.True(transform.IsFrozen);
    }

    [Fact]
    public void ChoosingASizeRecordsItAndItComesBackNextTime()
    {
        MainViewModel vm = _harness.NewViewModel(out string directory);
        string settings = Path.Combine(directory, "settings.json");

        vm.UiScale = 1.5;

        // Only the store is asserted on: InterfaceScale is process-wide, so a
        // test running in parallel could have moved it in between.
        Assert.Equal(1.5, new SettingsStore(settings).UiScale);
    }

    [Fact]
    public void ASizeBetweenStepsIsStoredAsTheNearestStep()
    {
        MainViewModel vm = _harness.NewViewModel(out string directory);

        vm.UiScale = 1.6;

        Assert.Equal(1.5, new SettingsStore(Path.Combine(directory, "settings.json")).UiScale);
    }

    [Fact]
    public void PreviewingASizeLeavesTheStoredPreferenceAlone()
    {
        MainViewModel vm = _harness.NewViewModel(out string directory);
        string settings = Path.Combine(directory, "settings.json");

        vm.UiScale = 1.25;
        vm.PreviewUiScale(2.0);

        Assert.Equal(1.25, new SettingsStore(settings).UiScale);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("40")]
    [InlineData("-1")]
    public void AStoredSizeThatWouldMakeTheWindowUnusableIsIgnored(string stored)
    {
        // A hand-edited file must not leave the window too small or too large
        // to reach the menu that puts it back.
        string directory = Path.Combine(Path.GetTempPath(), "olv-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "settings.json");

        try
        {
            File.WriteAllText(settings, $$"""{ "Version": 1, "UiScale": {{stored}} }""");
            Assert.Equal(1, new SettingsStore(settings).UiScale);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ASettingsFileFromBeforeZoomExistedStartsAtActualSize()
    {
        string directory = Path.Combine(Path.GetTempPath(), "olv-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "settings.json");

        try
        {
            File.WriteAllText(settings, """{ "Version": 1, "ThemeId": "nord" }""");
            Assert.Equal(1, new SettingsStore(settings).UiScale);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
