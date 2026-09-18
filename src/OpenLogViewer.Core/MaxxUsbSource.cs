namespace OpenLogViewer.Core;

/// <summary>
/// A live source over a MaxxECU's USB cable.
///
/// Its own source rather than a transport swapped underneath
/// <see cref="MaxxEcuSource"/>, because USB is a different protocol and not a
/// different wire — see <see cref="MaxxUsbProtocol"/>. Everything about the
/// conversation differs: there is no activation, no subscription, and the ECU is
/// asked for telemetry rather than pushing it.
///
/// The shape of a round is two requests. The first asks how many bytes are
/// waiting; the second takes them. What comes back is a list of channel ids with
/// their values, carrying only what has changed — so a round is a handful of
/// channels and not a block, and the reading this hands back is the accumulated
/// picture rather than what arrived in the last message.
///
/// That is also why the channel list has to be learnt rather than declared —
/// and why learning it is not enough on its own. Opening listens for a couple of
/// seconds and takes what arrives, but what arrives is only what moved, so the
/// list depends on what the engine was doing rather than on what the ECU has.
/// On a bench Race that is 136 channels on the first connect of the day and
/// fifty on the next, and listening longer does not help: the first number is a
/// queued backlog being drained and the plateau is reached in about a second.
/// <see cref="AlwaysLogged"/> is what stops that deciding which channels a log
/// has, because a channel that first appears later cannot be added — a log's
/// columns cannot change once it has rows.
/// </summary>
public sealed class MaxxUsbSource : ILiveSource
{
    private readonly IEcuTransport _transport;
    private readonly Dictionary<int, ushort> _values = [];
    private readonly byte[] _buffer = new byte[MaxxUsbProtocol.MaximumData + MaxxUsbProtocol.ReplyOverhead];

    private int[] _ids = [];
    private MaxxChannel[] _channels = [];

    /// <summary>How long one request waits for its reply.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How long to keep listening at connect before fixing the channel list.
    ///
    /// Two seconds because a longer wait buys nothing: measured against a bench
    /// Race, a raw listen had found 51 ids after one second, 55 after five, and
    /// 55 after sixty. Everything a channel-by-channel listen is going to hear,
    /// it hears at once.
    /// </summary>
    private static readonly TimeSpan LearnFor = TimeSpan.FromSeconds(2);

    public MaxxUsbSource(IEcuTransport transport, string? definitionsPath = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        DefinitionsPath = definitionsPath;
    }

    /// <summary>Where MTune's channel definitions are, when they are anywhere.</summary>
    public string? DefinitionsPath { get; }

    public IReadOnlyList<string> Names { get; private set; } = [];

    public IReadOnlyList<string> Units { get; private set; } = [];

    public IReadOnlyList<int> Digits { get; private set; } = [];

    /// <summary>Requests that had to be repeated, over the session.</summary>
    public int Retries { get; private set; }

    /// <summary>Rounds where the ECU had nothing new to say, which is not a fault.</summary>
    public int EmptyRounds { get; private set; }

    /// <summary>The channels this session is reading, in the order it reports them.</summary>
    public IReadOnlyList<MaxxChannel> Channels => _channels;

    public void Open()
    {
        _transport.Open();

        // Prove something is there before learning anything from it. A reply to
        // this is the whole of "a MaxxECU is on the other end", and without it
        // the first evidence of an empty cable would be an empty channel list,
        // which reads as an ECU with nothing to say rather than as no ECU.
        if (!Ask(MaxxUsbProtocol.AreYouThere(), 1, out _))
            throw new EcuProtocolException(
                "Nothing on this USB cable answered as a MaxxECU. The device is open and the "
                + "request went out, so check the ECU has 12 V — its USB chip powers up from the "
                + "cable alone, so it appears in the device list whether or not the ECU itself is "
                + "running.");

        Learn();
    }

    /// <summary>
    /// Channels that become columns whether or not the ECU has mentioned them.
    ///
    /// Learning the list by listening has one flaw, and it is not a small one: a
    /// MaxxECU sends a channel only when its value changes, so what is heard in
    /// the first two seconds is whatever happened to be moving. Connect with the
    /// engine off — which is the ordinary order, cable first and key second —
    /// and engine speed, coolant, lambda and ignition angle have all been still,
    /// so none of them is a column, and a log's columns cannot change once it
    /// has rows. Measured on a bench Race: fifty channels of input voltages and
    /// counters, and not one of the things anybody connects to see.
    ///
    /// These are the fourteen MaxxECU itself subscribes to for its Bluetooth
    /// dash — <see cref="MaxxProtocol.Subscribed"/>, so the two links agree on
    /// what matters — with throttle position added, which that set leaves out.
    /// A MaxxECU has all of them whatever is wired to it.
    ///
    /// One that has not been sent yet reads zero until the ECU first reports it,
    /// which is what any channel does between being discovered and its first
    /// update. Zero is wrong for a coolant temperature and right for engine
    /// speed, and it lasts only until the value moves — where being absent from
    /// the log lasts for the whole session.
    /// </summary>
    public static IReadOnlyList<int> AlwaysLogged { get; } =
        [.. MaxxProtocol.Subscribed.Select(c => c.Id).Append(ThrottlePosition).Order()];

    /// <summary>Throttle position, which the Bluetooth subscription has no room for.</summary>
    private const int ThrottlePosition = 19;

    /// <summary>
    /// Listens until the ECU stops naming channels it has not named before, then
    /// fixes that as the channel list.
    /// </summary>
    private void Learn()
    {
        DateTime deadline = DateTime.UtcNow + LearnFor;

        while (DateTime.UtcNow < deadline) Round();

        if (_values.Count == 0)
            throw new EcuProtocolException(
                "The MaxxECU answered but sent no channels, so there is nothing to log.");

        _ids = [.. _values.Keys.Union(AlwaysLogged).Order()];

        IReadOnlyDictionary<int, string> units = MaxxChannelTable.Units();
        IReadOnlyDictionary<int, MaxxChannelDefinition> defined =
            MaxxChannelDefinitions.Read(DefinitionsPath ?? MaxxGauges.FindDefinitions());

        _channels =
        [
            .. _ids.Select(id =>
            {
                MaxxChannelDefinition? known = defined.GetValueOrDefault(id);

                return new MaxxChannel(
                    id,
                    // Named from MTune where it is installed, and by number where
                    // it is not. A number is a poor name and an honest one; a
                    // guess from the value would be neither.
                    known?.Name ?? $"Channel {id}",
                    known?.Units ?? units.GetValueOrDefault(id, ""),
                    known?.Scale ?? 1,
                    known?.IsSigned ?? false,
                    known?.Digits ?? 0);
            }),
        ];

        Names = [.. _channels.Select(c => c.Name)];
        Units = [.. _channels.Select(c => c.Units)];
        Digits = [.. _channels.Select(c => c.Digits)];
    }

    /// <summary>
    /// Held by anything that puts bytes on the cable.
    ///
    /// A MaxxECU has one USB device and one conversation on it: a request goes
    /// out and the reply is read back, with nothing to say which request a reply
    /// belongs to. The poll loop runs that conversation continuously on its own
    /// thread, so a tune read or a write started from the interface would
    /// interleave with it — and what comes back is then a reply to somebody
    /// else's question, with a length and a checksum that happen to agree.
    /// </summary>
    private readonly Lock _cable = new();

    public double[] Read()
    {
        if (_ids.Length == 0) throw new InvalidOperationException("Open the connection first.");

        lock (_cable) Round();

        var reading = new double[_ids.Length];

        for (int i = 0; i < _ids.Length; i++)
        {
            MaxxChannel channel = _channels[i];
            ushort raw = _values.GetValueOrDefault(_ids[i]);

            reading[i] = (channel.IsSigned ? unchecked((short)raw) : raw) * channel.Scale;
        }

        return reading;
    }

    /// <summary>
    /// One exchange: ask what is waiting, then take it.
    ///
    /// Nothing waiting is the ordinary case between updates rather than a
    /// failure, so it is counted and passed over. The previous reading stands,
    /// which is right — a channel that has not changed still has its value.
    /// </summary>
    private void Round()
    {
        if (!Ask(MaxxUsbProtocol.AskWhatIsWaiting(), 2, out byte[] waiting)) return;

        int bytes = waiting[0] | (waiting[1] << 8);

        if (bytes == 0)
        {
            EmptyRounds++;
            return;
        }

        if (bytes > MaxxUsbProtocol.MaximumData) bytes = MaxxUsbProtocol.MaximumData;

        if (!Ask(MaxxUsbProtocol.AskFor(bytes), bytes, out byte[] payload)) return;

        // A payload whose ids do not ascend is not a list of channels, whatever
        // its length claimed. Taking it anyway writes plausible values against
        // channel numbers nobody sent.
        if (!MaxxUsbProtocol.LooksLikeTelemetry(payload)) return;

        MaxxUsbProtocol.ReadUpdates(payload, _values);
    }

    /// <summary>
    /// Sends a request and reads its reply, once more if the first goes astray.
    ///
    /// The link is a cable rather than a radio, so a lost reply is rare — but
    /// the two requests of a round are not independent, and a failed first one
    /// must not be followed by a read of a length nobody agreed.
    /// </summary>
    private bool Ask(ReadOnlySpan<byte> request, int dataLength, out byte[] data)
    {
        int wanted = MaxxUsbProtocol.ReplyLength(dataLength);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                Retries++;
                _transport.DiscardInput();
            }

            _transport.Write(request);

            int got = _transport.Read(_buffer.AsSpan(0, wanted), ReplyTimeout);

            if (got == wanted && MaxxUsbProtocol.TryReadReply(_buffer.AsSpan(0, got), dataLength, out data))
                return true;
        }

        data = [];
        return false;
    }

    /// <summary>
    /// Reopens the link.
    ///
    /// The channel list is kept rather than learnt again: a log's columns cannot
    /// change once it has rows, so a reconnection that discovered a different
    /// set would have nowhere to put it.
    /// </summary>
    public void Recover()
    {
        lock (_cable)
        {
            try
            {
                _transport.Close();
            }
            catch (Exception)
            {
                // A device that has gone cannot be closed politely.
            }

            _transport.Open();

            if (!Ask(MaxxUsbProtocol.AreYouThere(), 1, out _))
                throw new EcuProtocolException("The MaxxECU did not answer after the link came back.");
        }
    }

    /// <summary>
    /// Reads the ECU's tune without ending the session.
    ///
    /// The poll loop is held off for the four seconds this takes, which is a
    /// gap in the log rather than a fault: the reading either side of it is the
    /// same reading, because a MaxxECU sends only what changed and the changes
    /// wait.
    /// </summary>
    public byte[] ReadTune()
    {
        lock (_cable) return MaxxTune.Read(_transport);
    }

    /// <summary>
    /// Puts bytes into the ECU's tune, between two rounds of the poll loop.
    ///
    /// <b>Permanent when it lands</b> — see <see cref="MaxxTune.Write"/>. This
    /// only serialises it against the telemetry; everything about what is safe to
    /// send is the caller's to know.
    /// </summary>
    public MaxxWriteStatus WriteTune(int offset, ReadOnlySpan<byte> data)
    {
        lock (_cable) return MaxxTune.Write(_transport, offset, data);
    }

    /// <summary>Reads a run of the tune back, to check a write took.</summary>
    public bool VerifyTune(int offset, ReadOnlySpan<byte> expected)
    {
        lock (_cable) return MaxxTune.Verify(_transport, offset, expected);
    }

    /// <summary>
    /// Asks the ECU to checksum its own tune — the cheap way to ask whether
    /// anything moved that should not have.
    /// </summary>
    public uint[] TuneChecksums()
    {
        lock (_cable) return MaxxTune.Checksums(_transport);
    }

    /// <summary>
    /// Empties the ECU's CAN ring, between two rounds of the poll loop.
    ///
    /// The ring gives up what it returns, so a reply this drops is a frame
    /// nobody sees again — which is why the failure here is silence rather than
    /// a retry. Asking a second time would not fetch the same frames, it would
    /// fetch the next ones, and the gap would go unrecorded.
    /// </summary>
    public IReadOnlyList<CanFrame> ReadCanFrames(int most)
    {
        int wanted = most * MaxxCan.RecordLength;
        var reply = new byte[MaxxUsbProtocol.ReplyLength(wanted)];

        lock (_cable)
        {
            _transport.Write(MaxxCan.Ask(most));

            return _transport.Read(reply, ReplyTimeout) == reply.Length
                   && MaxxUsbProtocol.TryReadReply(reply, wanted, out byte[] data)
                ? MaxxCan.Frames(data)
                : [];
        }
    }

    /// <summary>
    /// How many frames the ECU says it has lost, across its receive buffer and
    /// the analyzer ring together.
    ///
    /// Lives in the runtime snapshot rather than the tune, so it is read with the
    /// command that returns that — and it is the number that decides whether a
    /// capture can be called complete.
    /// </summary>
    public int? CanDropCount()
    {
        var reply = new byte[MaxxUsbProtocol.ReplyLength(2)];

        lock (_cable)
        {
            _transport.Write(MaxxUsbProtocol.Request(
                MaxxUsbProtocol.Read, MaxxCan.Snapshot, MaxxCan.DropCountAt, 2));

            return _transport.Read(reply, ReplyTimeout) == reply.Length
                   && MaxxUsbProtocol.TryReadReply(reply, 2, out byte[] data)
                ? data[0] | (data[1] << 8)
                : null;
        }
    }

    public void Dispose() => _transport.Dispose();
}
