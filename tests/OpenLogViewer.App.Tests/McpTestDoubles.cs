using System.Windows;
using OpenLogViewer.App.Mcp;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Runs the work inline instead of marshalling it to a real dispatcher.
///
/// <para>
/// The real <see cref="WpfDispatcher"/> is tested separately, once, on its own.
/// Everything else is about what a tool does, not about which thread it did it
/// on, and a test that needs a running dispatcher loop to call a tool is a test
/// nobody writes a second of.
/// </para>
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

    public Task InvokeAsync(Action action)
    {
        action();

        return Task.CompletedTask;
    }
}

/// <summary>No window, which is what a headless test has.</summary>
public sealed class NoWindow : IWindowSource
{
    public Window? Window => null;
}

/// <summary>
/// Records arm and disarm without binding a port, so the toggle's own logic can
/// be tested.
/// </summary>
public sealed class FakeMcpServerHost : IMcpServerHost
{
    public bool IsArmed { get; private set; }

    public int? Port { get; private set; }

    public bool HasActiveClient { get; set; }

    public int Arms { get; private set; }

    public int Disarms { get; private set; }

    /// <summary>Set to make arming fail the way a taken port does.</summary>
    public Exception? ArmThrows { get; set; }

    public Task ArmAsync(McpServices services, int port)
    {
        Arms++;

        if (ArmThrows is { } problem) throw problem;

        IsArmed = true;
        Port = port;

        return Task.CompletedTask;
    }

    public Task DisarmAsync()
    {
        Disarms++;
        IsArmed = false;
        Port = null;
        HasActiveClient = false;

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
