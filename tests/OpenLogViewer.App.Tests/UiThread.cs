using System.Windows.Threading;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// A real dispatcher on a real STA thread, for the tests that need the view model
/// to live somewhere a WPF binding would tolerate.
///
/// <para>
/// <see cref="ImmediateUiDispatcher"/> is enough for a tool whose work is
/// arithmetic, and not enough here. The view model's channel list is behind an
/// <c>ICollectionView</c>, and WPF refuses to let one be changed from a thread
/// other than the one it was created on — so running a tool inline on the web
/// server's thread throws, exactly as it would in the application if the
/// dispatcher were skipped. That is the failure the marshalling rule exists to
/// prevent, and a harness that cannot reproduce it cannot prove the rule is being
/// followed.
/// </para>
/// </summary>
public sealed class UiThread : IDisposable
{
    private readonly Thread _thread;

    public UiThread()
    {
        using var ready = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            // Captured before the loop starts, so the constructor cannot return
            // with a dispatcher nobody is pumping.
            Dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "test UI thread",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        ready.Wait();
    }

    public Dispatcher Dispatcher { get; private set; } = null!;

    /// <summary>Builds something on the UI thread and hands it back.</summary>
    public T Invoke<T>(Func<T> make) => Dispatcher.Invoke(make);

    public void Invoke(Action work) => Dispatcher.Invoke(work);

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
