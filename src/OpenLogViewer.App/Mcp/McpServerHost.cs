using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// The local MCP server, which is off until somebody arms it.
///
/// <para>
/// Behind an interface so the toggle's own logic can be tested without binding a
/// real port.
/// </para>
/// </summary>
public interface IMcpServerHost : IAsyncDisposable
{
    /// <summary>Whether a listener is up.</summary>
    bool IsArmed { get; }

    /// <summary>The port it is on, or null when it is not armed.</summary>
    int? Port { get; }

    /// <summary>Whether a client is actually attached — see <see cref="McpClientActivity"/>.</summary>
    bool HasActiveClient { get; }

    /// <summary>
    /// Starts a listener on loopback. Idempotent: arming an armed server does
    /// nothing rather than binding a second port.
    /// </summary>
    /// <exception cref="IOException">The port is already in use.</exception>
    Task ArmAsync(McpServices services, int port);

    /// <summary>Stops it. Any client attached at that moment loses its connection.</summary>
    Task DisarmAsync();
}

/// <summary>
/// The live objects the tools act on, handed to the server when it is armed.
///
/// <para>
/// The single most important thing about this type is that it carries
/// <em>instances</em>. The application has no DI container to forward from — the
/// window creates its view model directly — so the server registers what it is
/// given by value. Registering the types instead would build a second view model
/// that the window has never heard of: every tool call would appear to succeed
/// and nothing would ever show up on screen.
/// </para>
/// </summary>
/// <param name="ViewModel">The one the window is bound to. Not a copy of it.</param>
/// <param name="Windows">
/// Where the few tools that draw the application find something to draw. An
/// abstraction rather than the window itself, so the server can be stood up and
/// driven end to end in a test with no window at all — those tools then refuse,
/// which is the honest answer.
/// </param>
/// <param name="Dispatcher">Captured on the UI thread, at composition time.</param>
public sealed record McpServices(
    MainViewModel ViewModel,
    IWindowSource Windows,
    IUiDispatcher Dispatcher);

/// <summary>Where a tool that needs to draw the application finds it.</summary>
public interface IWindowSource
{
    System.Windows.Window? Window { get; }
}

/// <summary>
/// The real one. Resolved when asked rather than held, because the window does
/// not exist yet when the view model that reaches it is built.
/// </summary>
public sealed class WindowSource(Func<System.Windows.Window?> resolve) : IWindowSource
{
    public System.Windows.Window? Window => resolve();
}

/// <summary>
/// Whether a client is attached, which takes two signals because MCP clients
/// differ.
/// </summary>
internal sealed class McpClientActivity
{
    /// <summary>
    /// How long after a call a client still counts as attached.
    ///
    /// <para>
    /// Kept short deliberately: a stale "connected" light is worse than a brief
    /// gap, because the whole point of the indicator is that it can be believed.
    /// </para>
    /// </summary>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(45);

    private int _inFlight;
    private long _lastRequestTicks;

    public bool IsActive
    {
        get
        {
            // A streaming client holds one request open for as long as it is
            // there, so the in-flight count answers for it outright.
            if (Volatile.Read(ref _inFlight) > 0) return true;

            // A client that posts a call and hangs up is attached in every sense
            // that matters but in flight for milliseconds, so recent traffic
            // counts too.
            long ticks = Interlocked.Read(ref _lastRequestTicks);

            return ticks > 0
                   && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < IdleWindow;
        }
    }

    public IDisposable BeginRequest()
    {
        Interlocked.Increment(ref _inFlight);

        return new Scope(this);
    }

    private sealed class Scope(McpClientActivity owner) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Exchange(ref owner._lastRequestTicks, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref owner._inFlight);
        }
    }
}

/// <summary>
/// Builds a fresh server on every arm and disposes it on every disarm.
///
/// <para>
/// Not one long-lived server that gets started and stopped: Kestrel's URLs are
/// fixed when the <see cref="WebApplication"/> is built, so a stopped one cannot
/// be re-pointed, and the application's own startup is unconditional, which is
/// incompatible with "off by default, one toggle to arm it".
/// </para>
/// </summary>
public sealed class McpServerHost : IMcpServerHost
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private WebApplication? _app;

    /// <summary>
    /// Made fresh on every arm, never reused.
    ///
    /// <para>
    /// It remembers when the last request was, and that memory outlasting a
    /// disarm is exactly the stale "connected" light this class exists to avoid:
    /// stop the server with an agent attached, start it again inside the idle
    /// window, and the status bar would announce a client that is not there.
    /// </para>
    /// </summary>
    private McpClientActivity? _activity;

    public bool IsArmed => _app is not null;

    public int? Port { get; private set; }

    public bool HasActiveClient => _app is not null && _activity is { IsActive: true };

    public async Task ArmAsync(McpServices services, int port)
    {
        // Serialised against disarm. Without this, a disarm issued while an arm
        // is still awaiting StartAsync sees _app still null, no-ops, and the
        // in-flight arm then completes anyway — leaving a listener running past
        // the point the caller believed it was stopped.
        await _lifecycle.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_app is not null) return;

            var activity = new McpClientActivity();

            WebApplicationBuilder builder = WebApplication.CreateBuilder();

            // One logging pipeline, not two. The application's is a line per
            // event into a temp file, because a WPF process has no console.
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new RunLogLoggerProvider());
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            // Forwarded, not rebuilt. See McpServices.
            builder.Services.AddSingleton(services.ViewModel);
            builder.Services.AddSingleton(services.Windows);
            builder.Services.AddSingleton(activity);

            // Wrapped so that only one tool call is on the UI thread at a time.
            // A confirmation dialog pumps the dispatcher while it waits for a
            // person, so without this every other call an agent makes runs
            // underneath the open dialog — see SerializedUiDispatcher.
            builder.Services.AddSingleton<IUiDispatcher>(
                new SerializedUiDispatcher(services.Dispatcher));

            builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

            // Loopback only, never a wildcard: nothing off this machine can
            // reach it, armed or not.
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            WebApplication app = builder.Build();

            // Ahead of MapMcp, so it wraps the long-lived event stream a
            // streaming client holds open as well as the individual calls.
            app.Use(async (context, next) =>
            {
                using (activity.BeginRequest()) await next(context).ConfigureAwait(false);
            });

            app.MapMcp();

            try
            {
                await app.StartAsync().ConfigureAwait(false);
            }
            catch
            {
                // Never leave a half-started application behind; the usual cause
                // is the port already being in use, and the caller says so.
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _app = app;
            _activity = activity;
            Port = port;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisarmAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_app is not { } app) return;

            // Cleared before the stop is awaited, so IsArmed reads false from
            // the instant disarming begins rather than when it finishes.
            _app = null;
            _activity = null;
            Port = null;

            try
            {
                // Bounded, because a client holding a streaming request open
                // would otherwise keep a shutdown waiting on it indefinitely,
                // and this is on the path the window closes down.
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                await app.StopAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // Stopping something already on its way down is not a failure,
                // and disarming must not be the thing that throws on the way out.
            }

            await app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisarmAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}

/// <summary>
/// Puts anything the server logs into the file the rest of the application
/// reports to, rather than starting a second one nobody knows to look at.
/// </summary>
internal sealed class RunLogLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RunLogLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class RunLogLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            string message = formatter(state, exception);
            App.Report($"MCP [{logLevel}] {category}: {message}");

            if (exception is not null) App.Report($"MCP {exception}");
        }
    }
}
