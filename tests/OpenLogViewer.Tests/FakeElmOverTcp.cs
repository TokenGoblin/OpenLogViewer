using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenLogViewer.Tests;

/// <summary>
/// A <see cref="FakeElm"/> on the end of a TCP socket, the way a Wi-Fi dongle is.
///
/// Worth the socket rather than testing the transport against a stub. What is
/// different about this link is not the conversation — that is the same ASCII
/// ELM327 exchange the other radios carry — it is that the bytes arrive split
/// across segments at the network's convenience, that a closed connection reads
/// as an endless supply of nothing, and that a refused one is a different failure
/// from a silent one. None of those exist unless there is a real socket.
///
/// One client at a time, as the adapters themselves are.
/// </summary>
internal sealed class FakeElmOverTcp : IDisposable
{
    private readonly FakeElm _car;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;

    public FakeElmOverTcp(FakeElm car, bool closeImmediately = false)
    {
        _car = car;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _serving = Task.Run(() =>
        {
            if (closeImmediately) HangUp();
            else Serve();
        });
    }

    /// <summary>The port the operating system handed out, since a fixed one collides.</summary>
    public int Port { get; }

    /// <summary>The endpoint as the transport is asked for it.</summary>
    public string Address => $"127.0.0.1:{Port}";

    /// <summary>Accepts a connection and drops it, as a dongle already in use does.</summary>
    private void HangUp()
    {
        using Socket client = _listener.AcceptSocket();
        client.Close();
    }

    private void Serve()
    {
        using Socket client = _listener.AcceptSocket();
        client.NoDelay = true;

        var arriving = new byte[512];
        var leaving = new byte[512];

        while (!_stopping.IsCancellationRequested)
        {
            // Five milliseconds, in microseconds. Long enough not to spin, short
            // enough that the exchange costs what the fake takes rather than what
            // this poll interval does.
            if (client.Poll(5_000, SelectMode.SelectRead))
            {
                int got;

                try
                {
                    got = client.Receive(arriving);
                }
                catch (SocketException)
                {
                    return;
                }

                if (got == 0) return;

                _car.Write(arriving.AsSpan(0, got));
            }

            int sending = _car.Read(leaving, TimeSpan.Zero);
            if (sending == 0) continue;

            try
            {
                // In pieces, deliberately. A reply that always arrived whole
                // would never exercise a read that has to come back for the rest
                // of it, which is the ordinary case on a real link and the one
                // that truncates a multi-module answer when it is got wrong.
                for (int at = 0; at < sending;)
                {
                    int take = Math.Min(7, sending - at);
                    client.Send(leaving.AsSpan(at, take));
                    at += take;
                }
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        // Waited on rather than abandoned: a server still holding the port while
        // the next test binds one is a failure in whichever test runs next.
        _serving.Wait(TimeSpan.FromSeconds(5));
        _stopping.Dispose();
    }

    /// <summary>An address nothing is listening on, for the failure cases.</summary>
    public static string ClosedAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return $"127.0.0.1:{port}";
    }

    /// <summary>How long something took, for the tests that are about time.</summary>
    public static TimeSpan TimeOf(Action work)
    {
        var clock = Stopwatch.StartNew();
        work();

        return clock.Elapsed;
    }
}
