using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The view-model half of the AI activity indicator — everything downstream of
/// <see cref="AgentActivityLog"/> itself, which <c>AgentServerTests</c> already
/// covers on the <c>OpenLogViewer.Core</c> side.
///
/// <see cref="MainViewModel.ApplyAgentActivity"/> is exercised directly rather
/// than through a live <see cref="AgentServer"/> subscription, because the real
/// path hands the update to a WPF dispatcher that nothing here pumps — the
/// same reason <c>ApplyAgentActivity</c> exists as its own, separately callable
/// step rather than being inlined into the event handler.
/// </summary>
public class AgentActivityIndicatorTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static AgentActivityEvent Read(string route = "/state", bool inFlight = true) =>
        new(1, DateTime.UtcNow, route, AgentActivityKind.Read, "checking connection state", inFlight);

    private static AgentActivityEvent Write(bool inFlight = true) =>
        new(1, DateTime.UtcNow, "/tune/set", AgentActivityKind.Write, "writing to the ECU", inFlight);

    [Fact]
    public void NothingIsActiveBeforeAnyAgentRequestArrives()
    {
        MainViewModel vm = _harness.NewViewModel();

        Assert.False(vm.AgentIsActive);
        Assert.False(vm.AgentIsWritingToEcu);
        Assert.Equal("", vm.AgentLastAction);
    }

    [Fact]
    public void AReadMarksTheIndicatorActiveButNotWriting()
    {
        MainViewModel vm = _harness.NewViewModel();

        vm.ApplyAgentActivity(Read());

        Assert.True(vm.AgentIsActive);
        Assert.False(vm.AgentIsWritingToEcu);
        Assert.Equal("checking connection state", vm.AgentLastAction);
    }

    [Fact]
    public void AWriteMarksBothTheGeneralAndTheEcuSpecificIndicator()
    {
        MainViewModel vm = _harness.NewViewModel();

        vm.ApplyAgentActivity(Write());

        Assert.True(vm.AgentIsActive);
        Assert.True(vm.AgentIsWritingToEcu);
        Assert.Equal("writing to the ECU", vm.AgentLastAction);
    }

    [Fact]
    public void AReadAfterAWriteLeavesTheEcuIndicatorAloneUntilItDecays()
    {
        // A write that just finished is still "recently writing" — a read
        // arriving a moment later must not clear that early. It also must not
        // extend it: only another write restarts the write decay.
        MainViewModel vm = _harness.NewViewModel();

        vm.ApplyAgentActivity(Write(inFlight: false));
        Assert.True(vm.AgentIsWritingToEcu);

        vm.ApplyAgentActivity(Read());
        Assert.True(vm.AgentIsWritingToEcu);
    }

    [Fact]
    public async Task TheIndicatorGoesQuietAgainAfterTheDecayWindow()
    {
        MainViewModel vm = _harness.NewViewModel();

        vm.ApplyAgentActivity(Write(inFlight: false));
        Assert.True(vm.AgentIsActive);
        Assert.True(vm.AgentIsWritingToEcu);

        // Comfortably past the 1.5 s decay window without hard-coding it here.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (vm.AgentIsActive || vm.AgentIsWritingToEcu))
            await Task.Delay(50);

        Assert.False(vm.AgentIsActive);
        Assert.False(vm.AgentIsWritingToEcu);
    }

    [Fact]
    public void StoppingTheAgentApiClearsTheIndicatorImmediately()
    {
        MainViewModel vm = _harness.NewViewModel();
        vm.StartAgentApi(port: 0);

        vm.ApplyAgentActivity(Write());
        Assert.True(vm.AgentIsActive);

        vm.StopAgentApi();

        Assert.False(vm.AgentIsActive);
        Assert.False(vm.AgentIsWritingToEcu);
        Assert.Equal("", vm.AgentLastAction);
    }
}
