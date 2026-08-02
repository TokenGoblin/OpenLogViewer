namespace OpenLogViewer.Core;

/// <summary>
/// Talks to an ECU: asks what it is, then reads its realtime block over and over.
///
/// Retries are built in rather than bolted on, because the link this is most
/// likely to be used over is Bluetooth, where a dropped or truncated reply is
/// ordinary rather than exceptional. A reply that fails its checksum is thrown
/// away and asked for again; only a run of consecutive failures ends the
/// session.
///
/// Read-only throughout. Nothing in this class can change anything in the ECU.
/// </summary>
public sealed class EcuConnection : IDisposable
{
    private readonly IEcuTransport _transport;
    private readonly byte[] _buffer = new byte[4096];

    public EcuConnection(IEcuTransport transport, EcuConnectionSettings? settings = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Settings = settings ?? new EcuConnectionSettings();
    }

    public EcuConnectionSettings Settings { get; }

    /// <summary>Replies thrown away for a bad checksum or a short read, over the session.</summary>
    public int Retries { get; private set; }

    public void Open()
    {
        if (!_transport.IsOpen) _transport.Open();
    }

    /// <summary>
    /// What the ECU says it is — the string an INI must match before anything is
    /// decoded. Firmware versions move channel offsets, so decoding a block with
    /// the wrong INI does not fail, it silently reads every channel from the
    /// wrong place.
    /// </summary>
    public string ReadSignature()
    {
        byte[] data = Request([MsProtocol.QuerySignature]);
        return MsProtocol.ReadSignature(data);
    }

    /// <summary>The longer build string, for display.</summary>
    public string ReadVersion()
    {
        byte[] data = Request([MsProtocol.QueryVersion]);
        return MsProtocol.ReadSignature(data);
    }

    /// <summary>Fetches one realtime block.</summary>
    public byte[] ReadRealtime(int size) => Request(MsProtocol.RealtimeRequest(size, Settings.CanId));

    private byte[] Request(ReadOnlySpan<byte> payload)
    {
        byte[] framed = MsProtocol.Frame(payload);
        EcuProtocolException? last = null;

        for (int attempt = 0; attempt <= Settings.Retries; attempt++)
        {
            if (attempt > 0) Retries++;

            try
            {
                _transport.DiscardInput();
                _transport.Write(framed);

                return MsProtocol.Unframe(ReadFrame());
            }
            catch (EcuProtocolException e)
            {
                last = e;

                // A partial reply may still be arriving; letting it land keeps it
                // from being read as the front of the next one.
                Thread.Sleep(Settings.RetryPause);
            }
        }

        throw last ?? new EcuProtocolException("The ECU did not reply.");
    }

    /// <summary>
    /// Reads one framed reply: the two-byte length, then exactly that many bytes
    /// plus the checksum.
    ///
    /// Read in two steps because the length is not known until the first two
    /// bytes arrive. Asking for a fixed guess instead either truncates a long
    /// reply or waits out the timeout on every short one.
    /// </summary>
    private ReadOnlySpan<byte> ReadFrame()
    {
        int header = _transport.Read(_buffer.AsSpan(0, 2), Settings.Timeout);
        if (header < 2) throw new EcuProtocolException("The ECU did not reply.");

        int length = (_buffer[0] << 8) | _buffer[1];
        if (length < 1 || 2 + length + 4 > _buffer.Length)
            throw new EcuProtocolException($"The ECU declared a {length} byte reply, which is not usable.");

        int wanted = length + 4;
        int body = _transport.Read(_buffer.AsSpan(2, wanted), Settings.Timeout);

        if (body < wanted)
            throw new EcuProtocolException(
                $"The reply stopped after {body} of {wanted} bytes.");

        return _buffer.AsSpan(0, 2 + wanted);
    }

    public void Dispose() => _transport.Dispose();
}

public sealed record EcuConnectionSettings
{
    /// <summary>How long to wait for a reply before giving up on it.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Extra attempts before a request is considered failed.</summary>
    public int Retries { get; init; } = 3;

    /// <summary>Settling time after a bad reply, so its tail does not corrupt the next.</summary>
    public TimeSpan RetryPause { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>CAN id of the controller; 0 is the ECU itself.</summary>
    public byte CanId { get; init; }
}
