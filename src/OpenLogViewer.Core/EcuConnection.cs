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

    private byte[] _buffer = new byte[4096];
    private RealtimeCommand _command = RealtimeCommand.Default;
    private bool _littleEndian;
    private int _chunk;

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
    /// Closes and reopens the link.
    ///
    /// Needed after the device has gone: the handle is dead but still reports
    /// itself open, so reopening without closing first does nothing and every
    /// later read fails the same way.
    /// </summary>
    public void Reopen()
    {
        try
        {
            _transport.Close();
        }
        catch (Exception)
        {
            // Closing something already gone is not a reason not to try again.
        }

        _transport.Open();
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

    /// <summary>
    /// Everything the ECU will say about itself, best candidate first.
    ///
    /// Which reply is the signature and which is a build string depends on the
    /// firmware, and the caller cannot know the firmware until it has the
    /// signature. So all of them are collected and the INI catalogue decides:
    /// the signature is, operationally, whichever of these matches a definition
    /// file. Commands the ECU refuses are simply absent.
    /// </summary>
    public IReadOnlyList<string> ReadIdentity()
    {
        var said = new List<string>(3);

        foreach (byte command in MsProtocol.IdentityCommands)
        {
            string text;
            try
            {
                // Probing a rusEFI with MegaSquirt's query command is refused by
                // design, so that one is taken at its word rather than repeated.
                text = MsProtocol.ReadSignature(Request([command], retryRefusals: false));
            }
            catch (EcuProtocolException)
            {
                continue;
            }

            // Seven characters is what an INI expects of a signature; shorter
            // replies are status text rather than an identity.
            if (text.Length >= 7 && !said.Contains(text, StringComparer.Ordinal)) said.Add(text);
        }

        return said;
    }

    /// <summary>
    /// Adopts a firmware's own way of being asked for its realtime block.
    ///
    /// Called once the INI is known, which is after the signature has been read
    /// — until then the MegaSquirt page read is assumed, since that is what the
    /// identity commands need and they are the same on both families.
    /// </summary>
    public void Use(RealtimeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _command = layout.Command;
        _littleEndian = layout.LittleEndian;
        _chunk = layout.BlockingFactor;

        // Sized for the largest reply this firmware will send, which is one
        // chunk when it chunks and the whole block when it does not.
        int largest = _chunk > 0 && _command.TakesRange
            ? Math.Min(_chunk, layout.BlockSize)
            : layout.BlockSize;

        int wanted = 2 + 1 + largest + 4;
        if (_buffer.Length < wanted) _buffer = new byte[wanted];
    }

    /// <summary>
    /// Fetches one realtime block, in as many pieces as the firmware insists on.
    ///
    /// The blocking factor is a hard limit rather than a hint. A rusEFI asked
    /// for more than it declares does not answer with an error — it leaves the
    /// USB bus, and does not come back until the board is replugged. That rules
    /// out the obvious alternative of asking for the whole block and falling
    /// back on a refusal: the first attempt would end the session it was meant
    /// to start.
    ///
    /// So it is obeyed even where a firmware would have tolerated more. An MS3
    /// declares 256 and serves 512 quite happily, but the cost of not finding
    /// out the hard way is one extra round trip on a link whose time goes on
    /// the bytes rather than the turnaround. The one real loss is that a split
    /// sample is no longer taken at a single instant.
    /// </summary>
    public byte[] ReadRealtime(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        if (!_command.TakesRange || _chunk <= 0 || size <= _chunk)
            return Request(_command.Build(0, size, Settings.CanId, _littleEndian));

        var block = new byte[size];

        for (int at = 0; at < size;)
        {
            int wanted = Math.Min(_chunk, size - at);
            byte[] piece = Request(_command.Build(at, wanted, Settings.CanId, _littleEndian));

            if (piece.Length < wanted)
                throw new EcuProtocolException(
                    $"The ECU sent {piece.Length} of the {wanted} bytes asked for at offset {at}.");

            piece.AsSpan(0, wanted).CopyTo(block.AsSpan(at));
            at += wanted;
        }

        return block;
    }

    /// <summary>
    /// Sends one request and returns its data, retrying a reply that goes astray.
    ///
    /// <paramref name="retryRefusals"/> is true everywhere except while probing.
    /// A refusal the ECU meant will be meant again, so retrying one only spends
    /// the timeout — but a refusal is also what a desynchronised stream looks
    /// like, since the first stale byte lands where the status belongs. After a
    /// link has dropped, the retries are the thing that gets back into step, so
    /// only a caller expecting to be refused may skip them.
    /// </summary>
    private byte[] Request(ReadOnlySpan<byte> payload, bool retryRefusals = true)
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

                if (e.Refused && !retryRefusals) break;
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
