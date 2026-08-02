namespace OpenLogViewer.Core;

/// <summary>
/// A live source over a MaxxECU's Bluetooth link.
///
/// Unlike the TunerStudio path, nothing is requested per sample: the ECU is told
/// once what to send and then pushes frames of its own accord. So a read here
/// waits for the next telemetry frame rather than asking for one.
///
/// Two things have to happen first, in order, and neither is optional.
///
/// A MaxxECU that has not seen an mDash session since it was powered on accepts
/// a Bluetooth socket, reports itself connected, and sends nothing at all — for
/// as long as you care to wait. The activation frames unlock it. Confirmed on a
/// cold ECU: two seconds of silence before, 17,544 bytes in twelve seconds
/// after.
///
/// Then it has to be told which channels to send. Activation alone produces
/// configuration and label dumps and never a reading.
/// </summary>
public sealed class MaxxEcuSource : ILiveSource
{
    private readonly IEcuTransport _transport;
    private readonly MaxxFrameReader _reader = new();
    private readonly byte[] _buffer = new byte[4096];
    private readonly double[] _values = new double[MaxxProtocol.Subscribed.Count];

    /// <summary>How long to wait for a reading before calling it a failure.</summary>
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    public MaxxEcuSource(IEcuTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));

        if (!MaxxProtocol.Verify())
            throw new EcuProtocolException(
                "The MaxxECU subscription does not match the table it is decoded with, "
                + "so every channel after the disagreement would read as its neighbour.");

        Names = [.. MaxxProtocol.Subscribed.Select(c => c.Name)];
        Units = [.. MaxxProtocol.Subscribed.Select(c => c.Units)];
        Digits = [.. MaxxProtocol.Subscribed.Select(c => c.Digits)];
    }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<string> Units { get; }

    public IReadOnlyList<int> Digits { get; }

    /// <summary>
    /// Always zero: nothing is ever asked for twice here.
    ///
    /// The ECU pushes frames once it has been told what to send, so there is no
    /// request to repeat. Counting the configuration and label frames that
    /// arrive alongside the readings would report a healthy link as one making
    /// hundreds of retries, which is what it did.
    /// </summary>
    public int Retries => 0;

    /// <summary>Frames that were not readings — normal traffic, not failures.</summary>
    public int OtherFrames { get; private set; }

    public void Open()
    {
        _transport.Open();
        Arm();
    }

    /// <summary>
    /// Sends the activation and the subscription.
    ///
    /// Both are replayed byte for byte, because their checksum algorithm is
    /// unidentified — so they cannot be composed, only repeated. The activation
    /// was captured identically across nine separate sessions, which is what
    /// makes repeating it sound rather than a guess.
    /// </summary>
    private void Arm()
    {
        _transport.Write(MaxxProtocol.Activation);
        Thread.Sleep(300);
        _transport.Write(MaxxProtocol.Subscription);
    }

    public double[] Read()
    {
        DateTime deadline = DateTime.UtcNow + FrameTimeout;

        while (DateTime.UtcNow < deadline)
        {
            // Anything already reassembled, before waiting on the link again:
            // one read can carry several frames.
            while (_reader.TryTake(out MaxxFrame? frame) && frame is not null)
            {
                if (MaxxProtocol.TryDecode(frame, _values)) return [.. _values];

                // Configuration, labels and heartbeats all arrive on the same
                // link and are perfectly normal.
                OtherFrames++;
            }

            int got = _transport.Read(_buffer, TimeSpan.FromMilliseconds(200));
            if (got > 0) _reader.Feed(_buffer.AsSpan(0, got));
        }

        throw new EcuProtocolException(
            $"No reading arrived within {FrameTimeout.TotalSeconds:N0} seconds.");
    }

    /// <summary>
    /// Reopens the link and arms it again.
    ///
    /// The ECU forgets its subscription when the socket goes, so coming back
    /// means repeating both messages — reconnecting without them gives a healthy
    /// socket and silence, which is the same symptom as never having connected.
    /// </summary>
    public void Recover()
    {
        try
        {
            _transport.Close();
        }
        catch (Exception)
        {
            // A link whose device has gone cannot be closed politely.
        }

        _transport.Open();
        Arm();

        // Proves it: opening and arming are not evidence that anything is coming
        // back.
        Read();
    }

    public void Dispose() => _transport.Dispose();
}
