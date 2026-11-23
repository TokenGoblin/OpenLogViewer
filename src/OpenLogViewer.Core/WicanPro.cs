namespace OpenLogViewer.Core;

/// <summary>
/// A WiCAN Pro reached over USB: its ELM327 session, and — where the unit runs
/// the custom firmware — the WCP client sharing the same port.
///
/// <para>
/// <b>The control lane can turn up late.</b> A unit that was restarting when the
/// connection was made says nothing to <c>hello</c> and is taken for stock
/// firmware. When it finishes booting it says <c>!BOOT</c> (contract 8.10), and
/// that is taken as the cue to ask again (review item H2): if it answers,
/// <see cref="Wcp"/> is set and <see cref="WcpFound"/> raised. Nothing else
/// changes — the OBD2 session carries on as it was.
/// </para>
/// </summary>
public sealed class WicanProConnection
{
    private readonly Lock _gate = new();
    private readonly TimeSpan? _helloTimeout;
    private WcpClient? _wcp;
    private bool _watching;
    private int _asking;
    private int _redetections;

    /// <param name="source">The OBD2 session, exactly as on any other adapter.</param>
    /// <param name="link">The port both of them use.</param>
    /// <param name="wcp">The control lane, or null on stock firmware — for now.</param>
    /// <param name="helloTimeout">The wait for a later <c>hello</c>; <see cref="WcpClient.DetectTimeout"/> by default.</param>
    public WicanProConnection(Elm327Source source, WcpLink link, WcpClient? wcp, TimeSpan? helloTimeout = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Link = link ?? throw new ArgumentNullException(nameof(link));
        _wcp = wcp;
        _helloTimeout = helloTimeout;

        if (wcp is null)
        {
            _watching = true;
            link.LineReceived += OnLine;
            link.Closed += StopWatching;
        }
    }

    /// <summary>The OBD2 session.</summary>
    public Elm327Source Source { get; }

    /// <summary>The port both of them use.</summary>
    public WcpLink Link { get; }

    /// <summary>The control lane, or null while none has answered.</summary>
    public WcpClient? Wcp
    {
        get
        {
            lock (_gate) return _wcp;
        }
    }

    /// <summary>
    /// The custom firmware answered after a <c>!BOOT</c>, on a connection that had
    /// taken it for stock. Raised on a worker thread.
    /// </summary>
    public event Action<WcpClient>? WcpFound;

    /// <summary>Times <c>hello</c> was asked again after a <c>!BOOT</c>.</summary>
    public int Redetections => Volatile.Read(ref _redetections);

    private void OnLine(string raw)
    {
        // Only a well-formed !BOOT: on a stock unit this line never comes, and
        // nothing else here is worth a hello.
        if (!WcpLine.TryParse(raw, out WcpLine line, out _) || line.Kind != WcpLineKind.Event || line.Code != "BOOT") return;

        lock (_gate)
        {
            if (!_watching || _wcp is not null) return;
        }

        // On the reading thread now, and a hello waits for lines only it delivers.
        if (Interlocked.Exchange(ref _asking, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                Interlocked.Increment(ref _redetections);
                WcpClient? found = WcpClient.Detect(Link, _helloTimeout);
                if (found is null) return;

                lock (_gate)
                {
                    if (!_watching || _wcp is not null)
                    {
                        found.Dispose();
                        return;
                    }

                    _wcp = found;
                }

                StopWatching();

                try
                {
                    WcpFound?.Invoke(found);
                }
                catch (Exception)
                {
                    // A subscriber's failure is its own; the client is found either way.
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The link went while asking: nothing to find.
            }
            finally
            {
                Interlocked.Exchange(ref _asking, 0);
            }
        });
    }

    /// <summary>Stops listening for a late <c>!BOOT</c>, as when the session ends.</summary>
    public void StopWatching()
    {
        lock (_gate)
        {
            if (!_watching) return;
            _watching = false;
        }

        Link.LineReceived -= OnLine;
        Link.Closed -= StopWatching;
    }
}

/// <summary>
/// Connecting to a MeatPi WiCAN Pro's USB ELM327 port, with WCP if it is there.
///
/// <para>
/// <b>The order matters.</b> <c>hello</c> is asked first, before the ELM327
/// conversation starts, and the ELM327 side is cleared after it. A stock unit
/// passes the line to its chip, which answers it with "?" and a prompt — bytes that,
/// left in the buffer, would be read as the answer to the first OBD2 command. The
/// ELM327 reset that follows clears them anyway; clearing them here as well means
/// that does not depend on it.
/// </para>
///
/// <para>
/// <b>A stock unit is not slowed down much.</b> Its chip echoes the request, and an
/// echoed request is recognised as "no WCP here" the moment it arrives. Only a unit
/// whose chip has echo switched off costs the hello's timeout, twice. A unit that
/// answers <c>ERR BUSY</c> is booting, not stock, and is asked again for up to 10 s.
/// </para>
///
/// <para>
/// The link stays in place on stock firmware too. It passes ELM327 through
/// untouched, and the session should not depend on which firmware answered — and
/// it is how a later <c>!BOOT</c> is heard (see <see cref="WicanProConnection"/>).
/// </para>
/// </summary>
public static class WicanPro
{
    /// <summary>Opens the Pro's port by name: 2,000,000 baud, DTR and RTS held off, and a large driver buffer for the CAN stream.</summary>
    public static WicanProConnection ConnectOnPort(string portName, IObd2BatchMemory? memory = null)
    {
        var serial = new SerialEcuTransport(portName, Obd2Adapters.WiCanProUsbBaud)
        {
            OpenAttempts = 3,
            AssertControlLines = false,
            ReadBufferBytes = 1 << 20,
        };

        return Connect(serial, memory);
    }

    /// <summary>The same over a transport already chosen, so the whole path can be run against a fake.</summary>
    public static WicanProConnection Connect(
        IEcuTransport transport, IObd2BatchMemory? memory = null, TimeSpan? helloTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var link = new WcpLink(transport);
        WcpClient? wcp = null;

        try
        {
            link.Open();

            wcp = WcpClient.Detect(link, helloTimeout);

            link.DiscardInput();

            Elm327Source source = Elm327Source.Connect(link, memory: memory);
            return new WicanProConnection(source, link, wcp, helloTimeout);
        }
        catch (Exception)
        {
            wcp?.Dispose();
            link.Dispose();
            throw;
        }
    }
}
