using OpenLogViewer.App;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Confirms the view model can be built and driven from a test at all. WPF
/// collection views are dispatcher-bound, so this establishes what the rest of
/// the suite can rely on.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void AViewModelCanBeConstructed()
    {
        var vm = new MainViewModel();

        Assert.NotNull(vm.Channels);
        Assert.NotNull(vm.ChannelView);
        Assert.Null(vm.Document);
    }

    [Fact]
    public void ALogCanBeLoadedAndPopulatesTheChannelList()
    {
        using var harness = new ViewModelHarness();
        var vm = new MainViewModel();

        vm.Load(harness.WriteTypicalLog());

        Assert.NotNull(vm.Document);
        Assert.NotEmpty(vm.Channels);
        Assert.Contains(vm.Channels, c => c.Name == "RPM");
    }
}
