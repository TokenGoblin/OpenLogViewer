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
        using var harness = new ViewModelHarness();
        MainViewModel vm = harness.NewViewModel();

        Assert.NotNull(vm.Channels);
        Assert.NotNull(vm.ChannelView);
        Assert.Null(vm.Document);
    }

    [Fact]
    public void ALogCanBeLoadedAndPopulatesTheChannelList()
    {
        using var harness = new ViewModelHarness();
        MainViewModel vm = harness.NewViewModel();

        vm.Load(harness.WriteTypicalLog());

        Assert.NotNull(vm.Document);
        Assert.NotEmpty(vm.Channels);
        Assert.Contains(vm.Channels, c => c.Name == "RPM");
    }

    [Fact]
    public void WhetherALogIsOpenIsSomethingTheViewModelWillSay()
    {
        // A menu item bound to a name the view model does not have fails
        // silently and leaves itself enabled, so a command that needs a log
        // stays clickable with none open and nothing reports it. This is the
        // property those bindings need to exist.
        using var harness = new ViewModelHarness();
        MainViewModel vm = harness.NewViewModel();

        Assert.False(vm.HasDocument);

        vm.Load(harness.WriteTypicalLog());

        Assert.True(vm.HasDocument);
    }
}
