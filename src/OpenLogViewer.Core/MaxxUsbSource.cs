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
/// That is also why the channel list has to be learnt rather than declared. The
/// first payload after a connection is the ECU's full state, so opening listens
/// until the channels stop being new and then fixes the list; a channel that
/// first appears later — one that had never moved — cannot be added, because a
/// log's columns cannot change once it has rows.
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
    /// How long to keep listening at connect before deciding the channel list is
    /// complete.
    ///
    /// The ECU sends its whole state in the first payloads and only changes
    /// after that, so this is long enough to have seen the state and short
    /// enough not to be a wait anybody notices.
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

        _ids = [.. _values.Keys.Order()];

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

    public double[] Read()
    {
        if (_ids.Length == 0) throw new InvalidOperationException("Open the connection first.");

        Round();

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

    public void Dispose() => _transport.Dispose();
}
