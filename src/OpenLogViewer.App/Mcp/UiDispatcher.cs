using System.Windows.Threading;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// Runs work on the thread that owns the window.
///
/// <para>
/// Every MCP tool call arrives on one of the web server's thread-pool threads,
/// and everything it touches — the observable collections the channel list is
/// built from, the view model's change notifications, the plot — belongs to the
/// UI thread. Reads are not exempt: a live session appends samples on a timer at
/// up to 200 Hz, so an unmarshalled read can catch a collection mid-append and
/// return a torn view of it or throw.
/// </para>
/// </summary>
public interface IUiDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> action);

    Task InvokeAsync(Action action);

    /// <summary>
    /// Runs work that is itself asynchronous on the window's thread, and waits
    /// for all of it rather than for the part before the first await.
    ///
    /// <para>
    /// Connecting over Wi-Fi is the one connect that is genuinely asynchronous,
    /// and without this its tool had to discard the task. It answered before the
    /// socket was open, so it always said <c>connected: false</c> — and an agent
    /// reading that and trying again got past the "already live" guard while the
    /// first attempt was still in flight, which puts two sockets on a dongle
    /// that accepts one.
    /// </para>
    /// <para>
    /// The caller's thread waits; the window's thread does not, so the work is
    /// free to await on it and its continuations still have a running loop to
    /// come back to.
    /// </para>
    /// </summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

/// <summary>
/// Lets one tool call onto the UI thread at a time.
///
/// <para>
/// Queueing on the dispatcher is not enough, because a confirmation dialog is a
/// modal message loop and a modal message loop keeps pumping that dispatcher. So
/// while a person stands looking at "Send 3 changed cells to the ECU?", every
/// other call an agent makes is dispatched and runs: it could disconnect the
/// controller, revert the table, or open a different one, and the write would
/// then go ahead against whatever was left — sending bytes that no longer match
/// the count in the question that was answered.
/// </para>
///
/// <para>
/// Holding a semaphore around the whole call keeps the others off the dispatcher
/// entirely, so the modal loop has nothing of ours to pump. The cost is that a
/// read waits behind a write that is waiting for a person, which is the correct
/// order: until that dialog is answered, there is no settled state to report.
/// </para>
/// </summary>
public sealed class SerializedUiDispatcher(IUiDispatcher inner) : IUiDispatcher, IDisposable
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<T> InvokeAsync<T>(Func<T> action)
    {
        await _oneAtATime.WaitAsync().ConfigureAwait(false);

        try
        {
            return await inner.InvokeAsync(action).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public async Task InvokeAsync(Action action)
    {
        await _oneAtATime.WaitAsync().ConfigureAwait(false);

        try
        {
            await inner.InvokeAsync(action).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Held for the whole of the asynchronous work, not just for starting it.
    /// A connection that is still opening is exactly the state no other tool
    /// call may act on, so this is the case the semaphore matters most for.
    /// </summary>
    public async Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        await _oneAtATime.WaitAsync().ConfigureAwait(false);

        try
        {
            return await inner.InvokeAsync(action).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public void Dispose() => _oneAtATime.Dispose();
}

/// <summary>The real one, over the WPF dispatcher the window was created on.</summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    /// <summary>
    /// Awaits the operation's <see cref="DispatcherOperation.Task"/> rather than
    /// the operation itself.
    ///
    /// <para>
    /// Awaiting the operation wraps anything the delegate throws in a
    /// <see cref="DispatcherOperationException"/>, so a tool's own structured
    /// refusal would reach the agent as an opaque protocol error about the
    /// dispatcher instead of as itself.
    /// </para>
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> action) => dispatcher.InvokeAsync(action).Task;

    public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;

    /// <summary>
    /// Two waits, and both are needed: the outer one is the delegate reaching
    /// the window's thread and returning a task, the inner one is that task
    /// running to completion. Awaiting only the first returns as soon as the
    /// work hits its first await, which is the whole defect this exists to fix.
    /// </summary>
    public async Task<T> InvokeAsync<T>(Func<Task<T>> action) =>
        await await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
}
