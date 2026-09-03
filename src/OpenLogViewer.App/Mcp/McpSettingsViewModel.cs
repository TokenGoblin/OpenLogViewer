using System.IO;
using System.Windows.Threading;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// The AI Agent menu's one switch, and what the window says about it.
///
/// <para>
/// Off at every launch and never remembered. There is no setting to make it
/// start armed, deliberately: an armed server accepts calls from anything able to
/// reach that port on this machine, and silently resuming that after an unrelated
/// restart is a bigger surprise than one extra click per session is worth
/// avoiding.
/// </para>
/// </summary>
public sealed class McpSettingsViewModel : ObservableObject
{
    /// <summary>
    /// Just a default. The number is only meaningful in that a client has to be
    /// pointed at the same one, which is why the window shows it.
    /// </summary>
    public const int DefaultPort = 7071;

    private readonly IMcpServerHost _host;
    private readonly Func<McpServices> _services;
    private readonly DispatcherTimer? _poll;

    private bool _isArmed;
    private bool _isClientConnected;
    private bool _busy;
    private string _statusLine = "No AI agent can reach this window.";

    /// <param name="host">The server. Faked in tests, so no port is bound.</param>
    /// <param name="services">
    /// The live objects to hand it, resolved when arming rather than held, so the
    /// window does not have to exist before this does.
    /// </param>
    /// <param name="poll">
    /// Whether to watch for a client on a timer. Off in tests, which have no
    /// dispatcher running to tick one.
    /// </param>
    public McpSettingsViewModel(IMcpServerHost host, Func<McpServices> services, bool poll = true)
    {
        _host = host;
        _services = services;

        if (!poll) return;

        // Polled rather than pushed: the signal expires on a timer anyway, so
        // something has to re-ask regardless. A second is well below noticeable
        // and the work is a field read.
        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _poll.Tick += (_, _) => IsClientConnected = _host.HasActiveClient;
        _poll.Start();
    }

    /// <summary>Whether the listener is up.</summary>
    public bool IsArmed
    {
        get => _isArmed;
        private set
        {
            if (!Set(ref _isArmed, value)) return;

            Raise(nameof(TitleBarText));
            Raise(nameof(IndicatorText));
            Raise(nameof(IsVisible));
        }
    }

    /// <summary>
    /// Whether an agent is actually talking to it.
    ///
    /// <para>
    /// Deliberately separate from <see cref="IsArmed"/>. A listener being up is
    /// worth seeing on its own, and saying an AI is connected when none is would
    /// make the indicator useless.
    /// </para>
    /// </summary>
    public bool IsClientConnected
    {
        get => _isClientConnected;
        private set
        {
            if (!Set(ref _isClientConnected, value)) return;

            Raise(nameof(TitleBarText));
            Raise(nameof(IndicatorText));
        }
    }

    /// <summary>The port in use, or the default before anything is armed.</summary>
    public int Port => _host.Port ?? DefaultPort;

    /// <summary>The full sentence, shown as the menu item's tooltip.</summary>
    public string StatusLine
    {
        get => _statusLine;
        private set => Set(ref _statusLine, value);
    }

    /// <summary>Whether the indicator has anything to say.</summary>
    public bool IsVisible => IsArmed;

    /// <summary>
    /// What the window title carries, so the state is legible when the window is
    /// not focused.
    /// </summary>
    public string TitleBarText => (IsArmed, IsClientConnected) switch
    {
        (false, _) => "AI agent access: OFF",
        (true, true) => $"AI CONNECTED OVER MCP · 127.0.0.1:{Port}",
        (true, false) => $"AI agent access ON, waiting · 127.0.0.1:{Port}",
    };

    /// <summary>
    /// The same thing for the status bar, where "OFF" would be noise — the row
    /// is hidden entirely until something is armed.
    /// </summary>
    public string IndicatorText => IsClientConnected
        ? $"● AI CONNECTED OVER MCP · 127.0.0.1:{Port}"
        : $"○ AI agent access ON, waiting · 127.0.0.1:{Port}";

    /// <summary>
    /// Arms or disarms. Reports what happened rather than throwing: the caller is
    /// a menu item.
    /// </summary>
    public async Task ToggleAsync()
    {
        // A second click while the first is still starting or stopping would
        // otherwise race the lifecycle lock inside the host and leave the
        // checkbox disagreeing with the listener.
        if (_busy) return;

        _busy = true;

        try
        {
            if (IsArmed)
            {
                await _host.DisarmAsync().ConfigureAwait(true);

                IsArmed = false;
                IsClientConnected = false;
                StatusLine = "No AI agent can reach this window.";

                return;
            }

            try
            {
                await _host.ArmAsync(_services(), DefaultPort).ConfigureAwait(true);

                IsArmed = true;
                StatusLine =
                    $"An AI agent may connect to http://127.0.0.1:{Port}/ — loopback only, so "
                    + "nothing off this machine can reach it. Unchecking this, or closing the "
                    + "application, stops it at once.";
            }
            catch (Exception e)
            {
                IsArmed = false;

                // Caught broadly, and deliberately. The failure an operator will
                // actually hit is the port already being in use, usually a second
                // copy of this application — but a bind can fail as a
                // SocketException, and a mistake in the tool wiring surfaces when
                // the container is built. This is called from an async void menu
                // handler, and the application's unhandled-exception hook logs
                // without marking anything handled, so anything that escapes here
                // closes the window from a menu click. A status line is the right
                // outcome for all of them.
                StatusLine = $"Could not open AI agent access on port {DefaultPort}: {e.Message}";
                App.Report($"MCP could not be armed: {e}");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Stops the listener on the way out, whether or not it was switched off
    /// first.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _poll?.Stop();

        await _host.DisarmAsync().ConfigureAwait(false);

        IsArmed = false;
        IsClientConnected = false;
    }
}
