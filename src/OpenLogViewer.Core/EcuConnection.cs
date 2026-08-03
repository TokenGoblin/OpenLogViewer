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

    /// <summary>
    /// One conversation at a time.
    ///
    /// A live session polls this from its own thread while the window is free to
    /// ask for something else — reading the tune, or writing a table back. Both
    /// are a request followed by its reply on a single serial line, and two of
    /// them interleaved is not two conversations but one ruined one: the poll
    /// reads the write's acknowledgement as its block of channels, and the write
    /// reads a block of channels as its acknowledgement.
    ///
    /// Reentrant, which matters: the write path reads its own bytes back through
    /// this same object to check them.
    /// </summary>
    private readonly Lock _gate = new();

    public void Open()
    {
        lock (_gate)
        {
        if (!_transport.IsOpen) _transport.Open();
        }
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
        lock (_gate)
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
    public IReadOnlyList<string> ReadIdentity() => ReadIdentity(Settings.IdentifyWithin);

    /// <summary>
    /// <paramref name="within"/> is how long to keep asking before concluding
    /// that nothing is there.
    ///
    /// Asking once is not enough, because opening a serial port asserts DTR and
    /// that resets some boards — a Speeduino on an Arduino Mega reboots into its
    /// bootloader and answers nothing for about two seconds. Probed immediately,
    /// it looks like an ECU that will not identify itself; given a moment, it
    /// says "speeduino 202402" straight away. Costs nothing on a board that was
    /// already awake, since the first question is answered.
    /// </summary>
    public IReadOnlyList<string> ReadIdentity(TimeSpan within)
    {
        lock (_gate)
        {
        DateTime deadline = DateTime.UtcNow + within;

        IReadOnlyList<string> said = AskWhatItIs();
        if (said.Count > 0) return said;

        // Nothing. Which may mean nothing is there, or may mean the board reset
        // when the port opened and was still in its bootloader when it was
        // asked.
        //
        // A Speeduino on an Arduino Mega does exactly that, and the failure is
        // not a matter of asking again: an Arduino bootloader listens for an
        // upload, and bytes arriving while it does leave it stuck there for
        // good. Measured — probed at 0.15 s, the board was still silent at 8
        // seconds, while a port opened and left alone for three answered
        // immediately.
        //
        // So the recovery is a fresh port and then silence, not persistence.
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                Reopen();
            }
            catch (Exception)
            {
                return said;
            }

            Thread.Sleep(Settings.BootWait);

            said = AskWhatItIs();
            if (said.Count > 0) return said;
        }

        return said;
        }
    }

    /// <summary>Asks each identity command once and collects what comes back.</summary>
    private List<string> AskWhatItIs()
    {
        var said = new List<string>(3);

        foreach (byte command in MsProtocol.IdentityCommands)
        {
            string text;
            try
            {
                // One attempt each, and no repeating a refusal: probing a rusEFI
                // with MegaSquirt's query command is refused by design, and an
                // ECU that is not answering yet will not answer harder.
                text = MsProtocol.ReadSignature(
                    Request([command], retryRefusals: false, attempts: 1));
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
    /// <param name="singleRequest">
    /// Ask for the whole realtime block in one reply, ignoring the blocking
    /// factor.
    ///
    /// Off by default because whether a firmware tolerates it is not something
    /// the INI says, and finding out the wrong way is expensive. Measured on two:
    /// an MS3 declaring 256 serves its whole 512-byte block quite happily, while
    /// a rusEFI declaring 1024 answers 1024 and, asked for 1280, stops replying
    /// and leaves the USB bus until it is replugged. So it is worth a third of
    /// the poll rate on one and unusable on the other, which makes it a choice
    /// rather than a default.
    /// </param>
    public void Use(RealtimeLayout layout, bool singleRequest = false)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _command = layout.Command;
        _littleEndian = layout.LittleEndian;
        _chunk = singleRequest ? 0 : layout.BlockingFactor;

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
    /// <summary>
    /// Asks for a realtime block in one reply regardless of the blocking factor.
    ///
    /// For finding out what a firmware will actually serve. Not the path a
    /// session uses — a rusEFI asked for more than it can send has been seen to
    /// leave the USB bus rather than refuse.
    /// </summary>
    public byte[] ReadRealtimeUnchunked(int size)
    {
        lock (_gate)
        {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        int wanted = 2 + 1 + size + 4;
        if (_buffer.Length < wanted) _buffer = new byte[wanted];

        return Request(_command.Build(0, size, Settings.CanId, _littleEndian));
        }
    }

    public byte[] ReadRealtime(int size)
    {
        lock (_gate)
        {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        if (!_command.TakesRange || _chunk <= 0 || size <= _chunk)
            return Request(_command.Build(0, size, Settings.CanId, _littleEndian), expected: size);

        var block = new byte[size];

        for (int at = 0; at < size;)
        {
            int wanted = Math.Min(_chunk, size - at);

            byte[] piece = Request(
                _command.Build(at, wanted, Settings.CanId, _littleEndian), expected: wanted);

            piece.AsSpan(0, wanted).CopyTo(block.AsSpan(at));
            at += wanted;
        }

        return block;
        }
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
    /// <summary>
    /// Reads one page of the ECU's settings.
    ///
    /// Chunked at the blocking factor like a realtime read, and for the harder
    /// reason: this is 22,960 bytes on a rusEFI where one reply holds 1,024.
    ///
    /// Still a read. The page write and burn commands are not implemented here.
    /// </summary>
    public byte[] ReadTunePage(
        TunePage page, int blockingFactor, bool littleEndian, Action<int>? progress = null)
    {
        lock (_gate)
        {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(page.Size, 1);

        if (!RealtimeCommand.Parse(page.ReadCommand).TakesRange)
            throw new EcuProtocolException(
                $"Page {page.Index} declares \"{page.ReadCommand}\", which cannot ask for part of a page.");

        return ReadTunePageRange(page, blockingFactor, littleEndian, 0, page.Size, progress);
        }
    }

    /// <summary>
    /// Writes bytes into a page and reads them back to prove it took.
    ///
    /// The read-back is not optional. A write is answered with an acknowledgement
    /// that says the command was understood, not that the right bytes landed at
    /// the right offset — and every way of getting that wrong produces an engine
    /// running on numbers nobody chose. Reading the same range back and comparing
    /// is the only thing that distinguishes a write that worked from one that
    /// went somewhere else.
    ///
    /// This does not burn. The bytes are in the controller's working memory and
    /// are gone at the next power cycle until <see cref="BurnPage"/> is called,
    /// which is a separate decision on the ECU as much as here.
    /// </summary>
    public void WriteTunePage(
        TunePage page, int blockingFactor, bool littleEndian, int offset, ReadOnlySpan<byte> data,
        int interWriteDelay = 0)
    {
        lock (_gate)
        {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (data.Length == 0) return;

        if (offset + data.Length > page.Size)
            throw new EcuProtocolException(
                $"That write ends at {offset + data.Length} of a {page.Size} byte page.");

        if (page.ChunkWriteCommand.Length == 0)
            throw new EcuProtocolException(
                $"Page {page.Index} declares no write command, so this firmware cannot be written to.");

        RealtimeCommand command = RealtimeCommand.Parse(page.ChunkWriteCommand);
        byte[] identifier = RealtimeCommand.Parse(page.Identifier).Build(0, 1, Settings.CanId, littleEndian);

        // The blocking factor bounds a write as it does a read; the payload has
        // to fit in one message along with its header.
        int chunk = blockingFactor > 0 ? Math.Min(blockingFactor - 8, data.Length) : data.Length;
        if (chunk < 1) chunk = data.Length;

        for (int at = 0; at < data.Length;)
        {
            int take = Math.Min(chunk, data.Length - at);

            Request(command.Build(
                offset + at, take, Settings.CanId, littleEndian, identifier, data.Slice(at, take)));

            at += take;

            // The pause the firmware asks for between writes, which TunerStudio
            // also observes. The controller is copying the last message into its
            // own memory, and the next one arriving underneath that is how a
            // write ends up half applied.
            if (interWriteDelay > 0 && at < data.Length) Thread.Sleep(interWriteDelay);
        }

        byte[] readBack = ReadTunePageRange(page, blockingFactor, littleEndian, offset, data.Length);

        if (!readBack.AsSpan().SequenceEqual(data))
            throw new EcuProtocolException(
                $"The ECU did not take that write: {data.Length} bytes were sent to offset {offset} "
                + "of page " + page.Index + " and reading the same range back gave something else. "
                + "Nothing has been burned, so a power cycle restores the ECU.");
        }
    }

    /// <summary>
    /// Commits a page to flash, making it survive a power cycle.
    ///
    /// Deliberately its own call. Everything up to here can be undone by turning
    /// the key off.
    /// </summary>
    public void BurnPage(TunePage page, bool littleEndian, int afterBurnDelay = 0)
    {
        lock (_gate)
        {
        ArgumentNullException.ThrowIfNull(page);

        if (page.BurnCommand.Length == 0)
            throw new EcuProtocolException($"Page {page.Index} declares no burn command.");

        byte[] identifier = RealtimeCommand.Parse(page.Identifier).Build(0, 1, Settings.CanId, littleEndian);

        Request(RealtimeCommand.Parse(page.BurnCommand)
            .Build(0, 1, Settings.CanId, littleEndian, identifier));

        // The wait the firmware asks for after a burn, which TunerStudio also
        // observes. Writing flash stops the controller answering for as long as
        // it takes, so the next request sent immediately afterwards goes into
        // silence — and desynchronises everything after it, which on a link
        // being polled at the same time is every reading until it is noticed.
        if (afterBurnDelay > 0) Thread.Sleep(afterBurnDelay);
        }
    }

    /// <summary>Reads part of a page, for verifying a write.</summary>
    public byte[] ReadTunePageRange(
        TunePage page, int blockingFactor, bool littleEndian, int offset, int count,
        Action<int>? progress = null)
    {
        lock (_gate)
        {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        RealtimeCommand command = RealtimeCommand.Parse(page.ReadCommand);
        byte[] identifier = RealtimeCommand.Parse(page.Identifier).Build(0, 1, Settings.CanId, littleEndian);

        int chunk = blockingFactor > 0 ? Math.Min(blockingFactor, count) : count;
        var image = new byte[count];

        int wanted = 2 + 1 + chunk + 4;
        if (_buffer.Length < wanted) _buffer = new byte[wanted];

        for (int at = 0; at < count;)
        {
            int take = Math.Min(chunk, count - at);
            byte[] piece = Request(
                command.Build(offset + at, take, Settings.CanId, littleEndian, identifier),
                expected: take);

            piece.AsSpan(0, take).CopyTo(image.AsSpan(at));
            at += take;

            progress?.Invoke(at);
        }

        return image;
        }
    }

    /// <summary>
    /// Sends one request and returns its data.
    ///
    /// <paramref name="expected"/> is how many bytes the reply should carry, or
    /// zero when anything is acceptable. Checked because a reply says nothing
    /// about which request it answers — there is no echo of the offset or the
    /// count — so a late one from a previous attempt is otherwise decoded as the
    /// answer to this one, at offsets it was never read from. That produces
    /// well-formed nonsense: a stationary car reading 230 km/h.
    /// </summary>
    private byte[] Request(
        ReadOnlySpan<byte> payload, bool retryRefusals = true, int expected = 0, int attempts = 0)
    {
        byte[] framed = MsProtocol.Frame(payload);
        EcuProtocolException? last = null;

        int tries = attempts > 0 ? attempts : Settings.Retries + 1;

        for (int attempt = 0; attempt < tries; attempt++)
        {
            if (attempt > 0) Retries++;

            try
            {
                _transport.DiscardInput();
                _transport.Write(framed);

                byte[] data = MsProtocol.Unframe(ReadFrame());

                if (expected > 0 && data.Length != expected)
                    throw new EcuProtocolException(
                        $"The reply carried {data.Length} bytes where {expected} were asked for; "
                        + "it belongs to an earlier request.");

                return data;
            }
            catch (EcuProtocolException e)
            {
                last = e;

                // Wait for the link to fall silent before trying again. A fixed
                // pause is not enough: over Bluetooth a reply can still be on its
                // way when the next request goes out, and rusEFI's command
                // handler has no queue — a request arriving mid-reply
                // desynchronises it permanently, and only a power cycle brings
                // the ECU back. Never more than one request outstanding.
                Settle();

                if (e.Refused && !retryRefusals) break;
            }
        }

        throw last ?? new EcuProtocolException("The ECU did not reply.");
    }

    /// <summary>
    /// Reads and throws away whatever is still arriving, until the link is quiet
    /// or the allowance runs out.
    /// </summary>
    private void Settle()
    {
        Span<byte> discard = stackalloc byte[256];
        DateTime deadline = DateTime.UtcNow + Settings.SettleFor;

        while (DateTime.UtcNow < deadline)
        {
            int got;

            try
            {
                got = _transport.Read(discard, Settings.QuietFor);
            }
            catch (Exception)
            {
                // A port whose device has gone cannot be drained, and saying so
                // is the caller's job rather than this one's.
                return;
            }

            if (got == 0) return;
        }
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

    /// <summary>How long to keep draining a link before giving up on it going quiet.</summary>
    public TimeSpan SettleFor { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Silence of this length counts as quiet.</summary>
    public TimeSpan QuietFor { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// How long to keep asking an ECU what it is before giving up.
    ///
    /// Long enough for a board that reboots when the port opens: an Arduino Mega
    /// spends about two seconds in its bootloader, and a Speeduino asked during
    /// that window looks like an ECU that will not identify itself.
    /// </summary>
    public TimeSpan IdentifyWithin { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Silence to leave a freshly opened port before speaking to it again.
    ///
    /// Long enough for an Arduino bootloader to give up waiting for an upload
    /// and start the firmware, which is a shade under two seconds on a Mega.
    /// </summary>
    public TimeSpan BootWait { get; init; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Settings for a link that is slower and less orderly than a cable.
    ///
    /// A Bluetooth SPP port is a virtual COM port, so nothing else about the
    /// connection changes — but a reply that would arrive in three milliseconds
    /// over USB can take hundreds, and one that is merely late is worse than one
    /// that never comes.
    /// </summary>
    public static EcuConnectionSettings Bluetooth { get; } = new()
    {
        Timeout = TimeSpan.FromMilliseconds(1000),
        SettleFor = TimeSpan.FromMilliseconds(1200),
        QuietFor = TimeSpan.FromMilliseconds(120),
        Retries = 2,
    };

    /// <summary>CAN id of the controller; 0 is the ECU itself.</summary>
    public byte CanId { get; init; }
}
