using System.Net.Sockets;

namespace OpenLogViewer.Core;

/// <summary>
/// A Wi-Fi OBD2 adapter, presented as a byte stream.
///
/// The third radio these dongles come with, after Bluetooth Classic and BLE, and
/// the one that is invisible to everything else here: it becomes no COM port, it
/// pairs with nothing, and Windows lists it nowhere. A Vgate iCar Pro Wi-Fi
/// plugged into a car is simply a wireless access point — <c>V-LINK</c> — with a
/// TCP socket behind it, and the only way to find it is to know where to look.
///
/// What arrives over that socket is the same ASCII ELM327 conversation the other
/// two carry, so this is a byte stream and nothing else: <see cref="Elm327"/>
/// neither knows nor cares which radio it is talking over.
///
/// The one thing that is genuinely different is what has to be true before it
/// works. A Bluetooth adapter is reached from a computer that is doing whatever
/// else it likes; a Wi-Fi one requires this computer to leave its own network and
/// join the dongle's, which has no route to anything. Windows treats a network
/// with no internet as a mistake and quietly hops back to one that has some — so
/// a link that was up a moment ago fails with the machine apparently connected to
/// Wi-Fi, and the failure is on the other side of the room from its cause. That
/// is what <see cref="Explain"/> is for.
/// </summary>
public sealed class WifiEcuTransport : IEcuTransport
{
    private Socket? _socket;

    /// <summary>
    /// Set when the adapter closes the connection, which is not the same as its
    /// having nothing to say.
    ///
    /// A stream socket reports a closed peer by returning zero bytes, and it does
    /// so immediately and for ever after. Read as "no data yet" that spins the
    /// read loop at full speed until the timeout runs out, on every request, for
    /// the rest of the session.
    /// </summary>
    private bool _ended;

    public WifiEcuTransport(string host, int port = DefaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        Host = host.Trim();
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    /// <summary>How the endpoint is written, and what the menu and messages call it.</summary>
    public string Address => $"{Host}:{Port}";

    /// <summary>
    /// Where a Vgate iCar Pro Wi-Fi answers, and most of the clones with it.
    ///
    /// The dongle runs the access point and hands out addresses on it, so this is
    /// its own address on its own network rather than anything a router assigned;
    /// it is fixed in the firmware and the same on every one of them.
    /// </summary>
    public const string DefaultHost = "192.168.0.10";

    /// <summary>The port those adapters listen on.</summary>
    public const int DefaultPort = 35000;

    /// <summary>
    /// Endpoints worth trying, in order.
    ///
    /// Told apart deliberately, because they are known to different standards.
    /// The first is where a Vgate iCar Pro Wi-Fi answers — verified on a 2014
    /// Subaru, though by a different client on the same dongle rather than by
    /// this one. The second is what a number of other Wi-Fi ELM327 clones ship
    /// with; it is widely reported and has not been checked here, which is why it
    /// is second and not a default.
    ///
    /// Guessing an address is cheap in a way guessing a scaling factor is not: a
    /// wrong one refuses the connection in a second and says so, where a wrong
    /// scaling produces a gauge that is confidently incorrect. So trying both is
    /// reasonable and inventing either would not have been.
    /// </summary>
    public static IReadOnlyList<string> KnownAddresses { get; } =
    [
        $"{DefaultHost}:{DefaultPort}",
        $"192.168.4.1:{DefaultPort}",
    ];

    /// <summary>
    /// An endpoint written as "host" or "host:port", the port defaulting to
    /// <see cref="DefaultPort"/>.
    ///
    /// Anything that is not a number after the colon is part of the host, so a
    /// name with a colon in it is not silently truncated to something that does
    /// not resolve.
    /// </summary>
    public static WifiEcuTransport At(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        string text = address.Trim();
        int colon = text.LastIndexOf(':');

        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int port) && port is > 0 and <= 65535)
            return new WifiEcuTransport(text[..colon], port);

        return new WifiEcuTransport(text);
    }

    /// <summary>
    /// How long to wait for the socket to open.
    ///
    /// Long enough to cross a link that is up and short enough that finding out
    /// it is not does not feel like a hang. A dongle that is not there answers in
    /// milliseconds — nothing on its network refuses the connection — so this is
    /// really the allowance for one that is there and busy.
    /// </summary>
    public TimeSpan ConnectWithin { get; init; } = TimeSpan.FromSeconds(5);

    public bool IsOpen => _socket is { Connected: true } && !_ended;

    public void Open()
    {
        if (IsOpen) return;

        Close();

        // Dual-stack, so an address literal of either family works without this
        // having to know which it was given.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            // An ELM327 command is a handful of bytes and every one of them is
            // waited on. Nagle would hold each request back looking for company
            // that is not coming, which on a link already limited to one request
            // at a time is pure latency.
            NoDelay = true,
        };

        try
        {
            // On the thread pool, as with the BLE adapter: this is called from a
            // click handler, and awaiting on the interface thread and then
            // blocking on the result deadlocks the window rather than connecting.
            using var deadline = new CancellationTokenSource(ConnectWithin);

            Task.Run(
                async () => await socket.ConnectAsync(Host, Port, deadline.Token).ConfigureAwait(false),
                deadline.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            throw new IOException(Explain(e), e);
        }

        _socket = socket;
        _ended = false;
    }

    /// <summary>
    /// Why a Wi-Fi adapter did not answer, in terms of the three things that are
    /// actually wrong when it does not.
    ///
    /// Worth the words. The underlying message is "No connection could be made
    /// because the target machine actively refused it" or a bare cancellation,
    /// and neither says the thing the user needs to do — which is nearly always
    /// to look at which network this computer is on.
    /// </summary>
    private string Explain(Exception cause) =>
        $"Nothing answered at {Address}. "
        + (cause is OperationCanceledException
            ? "The connection was still being attempted after "
              + $"{ConnectWithin.TotalSeconds:0.#} seconds, which is what a network with no "
              + "dongle on it looks like.\n\n"
            : $"{cause.Message}\n\n")
        + "A Wi-Fi dongle is its own access point rather than something on your network, so:\n\n"
        + "• join its Wi-Fi first — a Vgate iCar Pro publishes V-LINK — and check Windows has "
        + "stayed on it. A network with no internet gets dropped for one that has some, often "
        + "within seconds of joining it.\n"
        + "• close any phone app that is using the dongle. These accept one connection at a "
        + "time, and a second one is refused rather than queued.\n"
        + "• check the ignition is on, and that the adapter's own address is the one above.";

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_socket is not { } socket)
            throw new InvalidOperationException("The adapter is not open.");

        try
        {
            // A stream socket may take fewer bytes than it was offered, and a
            // command that arrives without its carriage return is one the adapter
            // waits for the rest of rather than one it refuses.
            for (int at = 0; at < data.Length;)
                at += socket.Send(data[at..], SocketFlags.None);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            throw new IOException($"The adapter at {Address} would not take the request: {e.Message}", e);
        }
    }

    /// <summary>
    /// Fills the buffer, or returns fewer bytes if the timeout passes first —
    /// the same contract as the serial port, so nothing above here has to know
    /// which it has.
    /// </summary>
    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (_socket is not { } socket)
            throw new InvalidOperationException("The adapter is not open.");

        int total = 0;
        DateTime deadline = DateTime.UtcNow + timeout;

        while (total < buffer.Length && !_ended)
        {
            int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) break;

            try
            {
                // The socket's own blocking read rather than polling Available:
                // this returns the moment the prompt arrives, which is what makes
                // an exchange cost what the car took rather than what the timeout
                // allows.
                socket.ReceiveTimeout = remaining;

                int got = socket.Receive(buffer[total..], SocketFlags.None);

                // Zero bytes from a stream socket is the far end having closed,
                // for ever, rather than a quiet moment.
                if (got == 0)
                {
                    _ended = true;
                    break;
                }

                total += got;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut)
            {
                break;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // The link has gone. Reported as what was read rather than as an
                // exception, because that is what a serial port whose adapter has
                // been unplugged does — and the caller above already treats a
                // round of silence as a link to recover.
                _ended = true;
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Drops anything already arrived.
    ///
    /// Whatever is buffered belongs to the previous exchange — a reply that came
    /// after its timeout, or the tail of an abandoned one — and read as the front
    /// of the next answer it decodes as a different reading altogether.
    /// </summary>
    public void DiscardInput()
    {
        if (_socket is not { } socket) return;

        Span<byte> discard = stackalloc byte[512];

        try
        {
            while (socket.Available > 0)
            {
                socket.ReceiveTimeout = 1;

                if (socket.Receive(discard, SocketFlags.None) == 0)
                {
                    _ended = true;
                    return;
                }
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Hygiene before a request. A link that cannot be drained will say so
            // again on the read that follows, where it can be dealt with properly.
        }
    }

    public void Close()
    {
        try
        {
            // Shut down first so the adapter sees the connection go rather than
            // being left holding it. These take one client at a time, and one
            // that is dropped without notice keeps the slot for a minute or so —
            // which presents as a dongle that refuses the next connection for no
            // visible reason.
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // A socket whose far end has already gone cannot be closed politely.
        }

        try
        {
            _socket?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _socket = null;
        _ended = false;
    }

    public void Dispose() => Close();
}
