using System.IO;
using OpenLogViewer.App.Mcp;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The AI Agent menu's switch: what it says, and that it never comes up armed.
/// </summary>
public class McpSettingsTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly FakeMcpServerHost _host = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// Polling is off: a test has no dispatcher loop running to tick a timer, and
    /// the connected state is set directly instead.
    /// </summary>
    private McpSettingsViewModel Switch() =>
        new(_host, () => new McpServices(_harness.NewViewModel(), new NoWindow(), new ImmediateUiDispatcher()),
            poll: false);

    [Fact]
    public void ItStartsOff()
    {
        McpSettingsViewModel mcp = Switch();

        Assert.False(mcp.IsArmed);
        Assert.False(mcp.IsClientConnected);
        Assert.False(mcp.IsVisible);
        Assert.Equal("AI agent access: OFF", mcp.TitleBarText);
        Assert.Equal(0, _host.Arms);
    }

    [Fact]
    public async Task NothingPersistsItAsOn()
    {
        // The rule, asserted rather than assumed: a second switch over a fresh
        // host is off, whatever the first one did. There is no setting that
        // changes this, and this test is what stops one being added quietly.
        McpSettingsViewModel first = Switch();
        await first.ToggleAsync();

        Assert.True(first.IsArmed);

        var second = new McpSettingsViewModel(
            new FakeMcpServerHost(),
            () => new McpServices(_harness.NewViewModel(), new NoWindow(), new ImmediateUiDispatcher()),
            poll: false);

        Assert.False(second.IsArmed);
    }

    [Fact]
    public async Task ArmingSaysWhereAClientShouldPoint()
    {
        McpSettingsViewModel mcp = Switch();

        await mcp.ToggleAsync();

        Assert.True(mcp.IsArmed);
        Assert.True(mcp.IsVisible);
        Assert.Equal(1, _host.Arms);
        Assert.Contains("127.0.0.1:7071", mcp.TitleBarText, StringComparison.Ordinal);
        Assert.Contains("waiting", mcp.TitleBarText, StringComparison.Ordinal);
        Assert.Contains("loopback only", mcp.StatusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AndDisarmingStopsIt()
    {
        McpSettingsViewModel mcp = Switch();

        await mcp.ToggleAsync();
        await mcp.ToggleAsync();

        Assert.False(mcp.IsArmed);
        Assert.Equal(1, _host.Disarms);
        Assert.Equal("AI agent access: OFF", mcp.TitleBarText);
    }

    [Fact]
    public void ArmedAndConnectedAreDifferentThings()
    {
        // A listener being up is worth seeing on its own, and saying an AI is
        // connected when none is would make the indicator worth ignoring. This
        // runs the real poll on a real dispatcher, because the whole point of the
        // second state is that something has to notice it.
        using var ui = new UiThread();

        McpSettingsViewModel mcp = ui.Invoke(() => new McpSettingsViewModel(
            _host,
            () => new McpServices(_harness.NewViewModel(), new NoWindow(), new ImmediateUiDispatcher())));

        ui.Invoke(() => mcp.ToggleAsync().GetAwaiter().GetResult());

        Assert.True(mcp.IsArmed);
        Assert.False(mcp.IsClientConnected);
        Assert.Contains("waiting", mcp.TitleBarText, StringComparison.Ordinal);
        Assert.DoesNotContain("CONNECTED", mcp.TitleBarText, StringComparison.Ordinal);

        _host.HasActiveClient = true;

        Assert.True(
            SpinWait.SpinUntil(() => mcp.IsClientConnected, TimeSpan.FromSeconds(5)),
            "the poll never noticed a client");

        Assert.Contains("AI CONNECTED OVER MCP", mcp.TitleBarText, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:7071", mcp.TitleBarText, StringComparison.Ordinal);

        ui.Invoke(() => mcp.ShutdownAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void AStreamingClientCountsWhileItHoldsARequestOpen()
    {
        // Two signals, because MCP clients differ. A streaming client holds one
        // request open the whole time it is attached, so the in-flight count
        // answers for it; a client that posts and hangs up is attached in every
        // sense that matters but in flight for milliseconds, so recent traffic
        // counts too.
        var activity = new McpClientActivity();

        Assert.False(activity.IsActive);

        using (activity.BeginRequest()) Assert.True(activity.IsActive);

        // Still active after the request closes, because the call just happened.
        Assert.True(activity.IsActive);
    }

    [Fact]
    public async Task AFailedArmSaysWhyAndStaysOff()
    {
        // The failure an operator actually hits: a second copy of the application
        // already holding the port.
        _host.ArmThrows = new IOException("address already in use");

        McpSettingsViewModel mcp = Switch();

        await mcp.ToggleAsync();

        Assert.False(mcp.IsArmed);
        Assert.Contains("Could not open AI agent access", mcp.StatusLine, StringComparison.Ordinal);
        Assert.Contains("already in use", mcp.StatusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownStopsTheListenerWhetherOrNotItWasSwitchedOff()
    {
        McpSettingsViewModel mcp = Switch();

        await mcp.ToggleAsync();
        await mcp.ShutdownAsync();

        Assert.False(mcp.IsArmed);
        Assert.False(_host.IsArmed);
        Assert.Equal(1, _host.Disarms);
    }
}
