namespace OpenLogViewer.Core;

/// <summary>
/// A live source over an ELM327 OBD2 adapter — any compliant vehicle, with no
/// definition file and no aftermarket ECU.
///
/// This is the one link here where nothing has to be known in advance. The
/// parameter numbering, the scaling and the units are the same on every OBD2
/// vehicle by law, and the car itself reports which parameters it answers to, so
/// a connection produces named and scaled channels on a car nobody has ever
/// plugged this into.
///
/// What it costs is speed. Every other ECU here hands over its whole realtime
/// block in one exchange; OBD2 has no such thing, so each parameter is a
/// separate request and a separate wait. A row of readings takes a good part of
/// a second, against forty per second on a tuning cable. That is the protocol
/// and not this implementation: it is fine for watching a car and no use for
/// catching a misfire.
/// </summary>
public sealed class Elm327Source : ILiveSource
{
    private readonly IEcuTransport _transport;
    private readonly Elm327 _elm;
    private readonly IReadOnlyList<Obd2Pid> _pids;
    private readonly int[] _at;
    private readonly double[] _values;
    private readonly int[] _silences;

    private int _rotating;

    /// <summary>
    /// Consecutive non-answers before a parameter is left out of the rotation.
    ///
    /// The car said it supported these, so a few refusals are more likely to be a
    /// busy module than a mistake; but a parameter that never answers costs a
    /// round trip every cycle, and on this link that is the whole budget.
    /// </summary>
    private const int GiveUpAfter = 6;

    private Elm327Source(
        IEcuTransport transport, Elm327 elm, IReadOnlyList<Obd2Pid> pids, string adapter)
    {
        _transport = transport;
        _elm = elm;
        _pids = pids;
        _silences = new int[pids.Count];
        Adapter = adapter;

        var names = new List<string>();
        var units = new List<string>();
        var digits = new List<int>();
        _at = new int[pids.Count];

        for (int i = 0; i < pids.Count; i++)
        {
            _at[i] = names.Count;

            foreach (Obd2Channel channel in pids[i].Channels)
            {
                names.Add(channel.Name);
                units.Add(channel.Units);
                digits.Add(channel.Digits);
            }
        }

        Names = names;
        Units = units;
        Digits = digits;

        // Nothing has been read yet, and zero is a reading. A gauge shows an
        // empty face until its parameter has actually answered.
        _values = [.. names.Select(_ => double.NaN)];
    }

    /// <summary>What the adapter calls itself, e.g. "ELM327 v1.5".</summary>
    public string Adapter { get; }

    /// <summary>The parameters this car reported and this knows how to decode.</summary>
    public IReadOnlyList<Obd2Pid> Parameters => _pids;

    /// <summary>
    /// Every parameter the car said it supports, decodable or not.
    ///
    /// Kept because the two lists are not the same and the difference used to
    /// vanish without trace: the car enumerates what it will answer, this keeps
    /// the ones it has a decoder for, and the rest were dropped on the floor with
    /// nothing said. On a vehicle offering a channel this cannot read, that is a
    /// missing gauge that looks exactly like a car that never had it.
    /// </summary>
    public IReadOnlyList<byte> Supports { get; private init; } = [];

    /// <summary>
    /// What the car offers that this cannot yet read, in order.
    ///
    /// The list worth acting on: every one of these is a channel the vehicle is
    /// willing to report and that only wants a decoder writing. Anything above
    /// 0x60 also proves the discovery walk reached that far, which it did not
    /// before.
    /// </summary>
    public IReadOnlyList<byte> Undecoded
    {
        get
        {
            var known = new HashSet<byte>(Obd2Pids.All.Select(p => p.Pid));

            // The support queries themselves are not readings; a car listing
            // 0x20 is saying "ask me about the next range", which has already
            // happened by the time anyone reads this.
            var asked = new HashSet<byte>(Obd2Pids.SupportQueries);

            return [.. Supports.Where(p => !known.Contains(p) && !asked.Contains(p)).Distinct().Order()];
        }
    }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<string> Units { get; }

    public IReadOnlyList<int> Digits { get; }

    /// <summary>Requests that went unanswered, over the session.</summary>
    public int Retries { get; private set; }

    /// <summary>
    /// Opens the adapter, asks the car what it supports, and builds the channels
    /// from the answer.
    ///
    /// Done before the session starts rather than inside <see cref="Open"/>,
    /// because which channels exist is only knowable by asking and a session
    /// needs to know its columns before it can record any.
    /// </summary>
    public static Elm327Source Connect(IEcuTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        transport.Open();

        var elm = new Elm327(transport);
        string reset = elm.Reset();

        // Asked after the reset, so the answers arrive with the echo already off.
        // The name from the reset is kept as the fallback: it is what proves an
        // adapter answered at all, and Identify can come back empty on a device
        // that is being sent noise.
        string adapter = reset.Length > 0 ? elm.Identify() : "";
        if (adapter.Length == 0) adapter = reset;

        IReadOnlyList<byte> supported = Supported(elm);
        IReadOnlyList<Obd2Pid> pids = Obd2Pids.Known(supported);

        // Told apart deliberately. An adapter that named itself and a car that
        // listed nothing is a key turned off, which the user can fix in a second;
        // an adapter that did not name itself is the wrong port or the wrong
        // speed, and waiting for the ignition would be waiting for nothing.
        if (pids.Count == 0)
            throw new EcuProtocolException(
                adapter.Length > 0
                    ? $"{adapter} answered, but the vehicle listed no parameters. "
                      + "Check the ignition is on — most cars report nothing with the key out."
                    : "Nothing here answered as an OBD2 adapter.");

        return new Elm327Source(transport, elm, pids, adapter) { Supports = supported };
    }

    /// <summary>
    /// Speeds an OBD2 adapter might be listening at, in the order worth trying.
    ///
    /// Unlike every other link here, this one cannot be assumed. A genuine
    /// ELM327 leaves the factory at 38,400; clones ship at that, at 9,600, or at
    /// 115,200 depending on the batch; the fast USB ones run at 500,000. Get it
    /// wrong and the adapter is not silent — it answers with rubbish, which is a
    /// worse symptom than nothing.
    ///
    /// A Bluetooth adapter ignores the setting altogether, the speed being
    /// negotiated by the radio, so the first attempt is the only one for a paired
    /// dongle.
    /// </summary>
    public static IReadOnlyList<int> BaudRates { get; } = [38400, 115200, 9600, 500000];

    /// <summary>
    /// Finds an adapter on a port, whatever speed it is set to.
    ///
    /// Each speed costs a reset and a wait, so the order matters more than the
    /// list does; a wrong speed is recognised by the adapter failing to name
    /// itself, which is what it does when it is being sent noise.
    /// </summary>
    public static Elm327Source ConnectOnPort(string portName)
    {
        Exception? last = null;

        foreach (int baud in BaudRates)
        {
            var transport = new SerialEcuTransport(portName, baud) { OpenAttempts = 3 };

            try
            {
                return Connect(transport);
            }
            catch (Exception e)
            {
                last = e;
                transport.Dispose();
            }
        }

        throw last ?? new EcuProtocolException($"Nothing on {portName} answered as an OBD2 adapter.");
    }

    /// <summary>
    /// Every parameter the car says it supports, across every module that
    /// answers.
    ///
    /// Asked in three questions rather than found by trying all ninety-six and
    /// waiting for the ones that never answer. Each reply is a bitmask covering
    /// the thirty-two numbers after it, and the last bit of each says whether
    /// there is another range to ask about — so a car that stops at the first
    /// range is not asked twice.
    ///
    /// Every answer is combined rather than the first being taken. More than one
    /// module replies on most cars, each reporting only what it alone supports,
    /// and which one gets in first is not fixed. Measured on the test vehicle:
    /// the engine module answers BE3FA813 and something else answers 80000001,
    /// so taking whichever arrived first gave twenty-four channels on one
    /// connection and three on the next.
    /// </summary>
    private static IReadOnlyList<byte> Supported(Elm327 elm)
    {
        var supported = new List<byte>();

        foreach (byte query in Obd2Pids.SupportQueries)
        {
            IReadOnlyList<byte[]> masks = AskUntilAnswered(elm, query);
            if (masks.Count == 0) break;

            // "And there is a further range" — from any module, since the ranges
            // they cover need not be the same. Asking anyway when nobody claims
            // one costs a round trip and answers NO DATA, which reads like a
            // fault.
            bool more = false;

            foreach (byte[] mask in masks)
            {
                IReadOnlyList<byte> range = Obd2Pids.SupportedBy(query, mask);
                supported.AddRange(range);

                more |= range.Contains((byte)(query + 0x20));
            }

            if (!more) break;
        }

        return supported;
    }

    /// <summary>
    /// Asks for a capability mask, and keeps asking.
    ///
    /// Worth being stubborn about in a way an ordinary reading is not. This runs
    /// once, and its answer decides every channel the session will ever have — a
    /// range that goes unanswered on the one attempt it gets silently costs every
    /// parameter above it for as long as the session lasts. A reading that fails
    /// costs one sample and comes round again in half a second.
    ///
    /// Measured before this was here: the same car and the same dongle, minutes
    /// apart, reported 12, then 24, then 15 parameters.
    ///
    /// The long timeout is for the same reason the retries are. This is the first
    /// thing the car is ever asked, and the adapter answers it with
    /// "SEARCHING..." while it works through the nine OBD2 protocols looking for
    /// the one this vehicle speaks — seconds, where a settled link answers in
    /// milliseconds.
    /// </summary>
    private static IReadOnlyList<byte[]> AskUntilAnswered(Elm327 elm, byte query)
    {
        for (int attempt = 1; attempt < DiscoveryAttempts; attempt++)
        {
            IReadOnlyList<byte[]> masks = elm.ReadAll(query, 4, elm.ResetTimeout);
            if (masks.Count > 0) return masks;

            // Long enough for an adapter still settling the protocol to finish.
            // Asking again immediately tends to get the same silence.
            Thread.Sleep(400);
        }

        return elm.ReadAll(query, 4, elm.ResetTimeout);
    }

    /// <summary>Times a capability mask is asked for before it is believed absent.</summary>
    private const int DiscoveryAttempts = 4;

    /// <summary>
    /// Reopens the adapter if it is not already open.
    ///
    /// Ordinarily a no-op: <see cref="Connect"/> has already done the work, and
    /// it had to, because the channels come from the car's answer.
    /// </summary>
    public void Open()
    {
        if (_transport.IsOpen) return;

        _transport.Open();
        _elm.Reset();
    }

    /// <summary>
    /// Polls one round and returns every channel.
    ///
    /// The fast ones every time; one of the rest per round. A slow channel
    /// therefore holds its previous reading between updates, which is what a
    /// coolant temperature does anyway — and asking for all of them every round
    /// would drag the rev counter down to the speed of the fuel level.
    /// </summary>
    public double[] Read()
    {
        bool answered = false;
        bool asked = false;

        for (int i = 0; i < _pids.Count; i++)
        {
            if (!_pids[i].Hot) continue;

            asked = true;
            answered |= Poll(i);
        }

        if (NextRotating() is { } next)
        {
            asked = true;
            answered |= Poll(next);
        }

        // Every question went unanswered. One parameter falling silent is
        // ordinary; the whole car doing so is the link having gone.
        if (asked && !answered)
        {
            Retries++;

            throw new EcuProtocolException(
                "The adapter answered nothing this round. If the engine was switched off, "
                + "most cars stop responding to OBD2 requests with the key out.");
        }

        return [.. _values];
    }

    /// <summary>The next parameter due a turn, skipping any that have given up.</summary>
    private int? NextRotating()
    {
        for (int tried = 0; tried < _pids.Count; tried++)
        {
            int at = _rotating;
            _rotating = (_rotating + 1) % _pids.Count;

            if (!_pids[at].Hot && _silences[at] < GiveUpAfter) return at;
        }

        return null;
    }

    private bool Poll(int index)
    {
        Obd2Pid pid = _pids[index];
        Span<byte> data = stackalloc byte[8];

        if (!_elm.TryRead(pid.Pid, pid.DataBytes, data, out int got))
        {
            _silences[index]++;
            return false;
        }

        _silences[index] = 0;
        pid.Decode(data[..got], _values.AsSpan(_at[index], pid.Channels.Count));

        return true;
    }

    /// <summary>
    /// Asks the car what it is complaining about.
    ///
    /// Safe to call while the session is polling. Every command goes through the
    /// adapter's own gate, so a scan and a round of readings take turns rather
    /// than interleaving — the poll pauses for as long as this takes, which on a
    /// car with codes to report is a second or two.
    /// </summary>
    public FaultScan ReadFaults() => Obd2Faults.Scan(_elm);

    /// <summary>
    /// Asks the car to erase them.
    ///
    /// Separated from the scan by more than convenience: this is the one thing
    /// this application can ask a standard vehicle to do that changes it, and what
    /// it erases goes well beyond the codes — see <see cref="Obd2Faults.Clear"/>.
    /// </summary>
    public FaultClear ClearFaults() => Obd2Faults.Clear(_elm);

    /// <summary>
    /// Puts the link back together.
    ///
    /// The adapter forgets its settings when the port goes, and one left in its
    /// default state echoes commands and inserts spaces — so a reconnection
    /// without a reset produces replies that parse as different readings rather
    /// than none. The channels are kept as they are: they are this session's
    /// columns, and a car that now reports a different set is a different
    /// session.
    /// </summary>
    public void Recover()
    {
        try
        {
            _transport.Close();
        }
        catch (Exception)
        {
            // A link whose adapter has gone cannot be closed politely.
        }

        _transport.Open();
        _elm.Reset();

        // Proves it. Opening and resetting say nothing about whether the car is
        // answering again.
        Read();
    }

    public void Dispose() => _transport.Dispose();
}
