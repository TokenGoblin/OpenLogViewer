using OpenLogViewer.App.Mcp;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Waiting for asynchronous work on the window's thread.
///
/// <para>
/// Connecting over Wi-Fi is the only connect that is genuinely asynchronous, and
/// its tool used to discard the task. It answered before the socket was open, so
/// it always reported <c>connected: false</c> — and an agent that read that and
/// tried again got past the "already live" guard while the first attempt was
/// still in flight, putting two sockets on a dongle that accepts one.
/// </para>
/// <para>
/// The window path itself cannot be tested here — nothing in this project
/// instantiates a WPF window — so what is pinned is the mechanism underneath it:
/// that the whole of the work is waited for, and that nothing else is let onto
/// the thread while it runs.
/// </para>
/// </summary>
public class UiDispatcherTests
{
    [Fact]
    public async Task TheWholeOfTheWorkIsWaitedForAndNotJustItsFirstAwait()
    {
        var gate = new TaskCompletionSource();
        bool finished = false;

        var dispatcher = new SerializedUiDispatcher(new ImmediateUiDispatcher());

        Task<string> running = dispatcher.InvokeAsync(async () =>
        {
            await gate.Task;
            finished = true;
            return "done";
        });

        // Still going: the delegate is parked on its first await.
        Assert.False(running.IsCompleted);
        Assert.False(finished);

        gate.SetResult();

        Assert.Equal("done", await running);
        Assert.True(finished);
    }

    /// <summary>
    /// A connection that is still opening is exactly the state no other call may
    /// act on. Holding the lock only while the work is *started* is what let a
    /// retry slip past the guard.
    /// </summary>
    [Fact]
    public async Task NothingElseRunsWhileAsynchronousWorkIsStillGoing()
    {
        var gate = new TaskCompletionSource();
        var order = new List<string>();

        var dispatcher = new SerializedUiDispatcher(new ImmediateUiDispatcher());

        Task<string> first = dispatcher.InvokeAsync(async () =>
        {
            order.Add("first started");
            await gate.Task;
            order.Add("first finished");
            return "first";
        });

        Task<string> second = dispatcher.InvokeAsync(() =>
        {
            order.Add("second ran");
            return "second";
        });

        // The second is queued behind work that has not finished, not run
        // alongside it.
        Assert.False(second.IsCompleted);
        Assert.Equal(["first started"], order);

        gate.SetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(["first started", "first finished", "second ran"], order);
    }

    [Fact]
    public async Task AFailureInAsynchronousWorkStillReleasesTheLock()
    {
        var dispatcher = new SerializedUiDispatcher(new ImmediateUiDispatcher());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.InvokeAsync<string>(async () =>
            {
                // Thrown after the first await, which is where a socket that is
                // refused mid-handshake actually fails.
                await Task.Yield();
                throw new InvalidOperationException("the dongle refused");
            }));

        // A lock left held by a failed connection would wedge every later call.
        Assert.Equal("after", await dispatcher.InvokeAsync(() => "after"));
    }
}
