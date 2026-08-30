namespace OpenLogViewer.Core;

/// <summary>
/// What asking for several parameters at once has already cost an adapter.
///
/// Remembered between sessions, and it has to be. A dongle that cannot survive
/// a batched request does not refuse one — it answers, and then the link dies —
/// so learning costs a dropped session every time. Learning it once per adapter
/// is a few seconds of churn on one drive; relearning it every time the
/// application starts is a few seconds of blank gauges on every drive, for ever.
///
/// Keyed by whatever names the adapter: the address of a Wi-Fi dongle, or the
/// name it reports. That name is not unique to the device — every clone claims
/// to be an ELM327 — so this is a verdict on a kind of adapter rather than on
/// one particular unit, which is the right grain for what is being remembered.
/// </summary>
public interface IObd2BatchMemory
{
    /// <summary>Links that batching has demonstrably killed on this adapter.</summary>
    int DeathsOn(string adapter);

    /// <summary>Records another one.</summary>
    void Died(string adapter);
}

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
    private readonly bool[] _answered;
    private readonly int[] _hot;
    private readonly Dictionary<byte, int> _indexOf;

    private int _rotating;
    private int _batchMisses;
    private bool _batchGivenUp;
    private bool _diedBatching;

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
        _answered = new bool[pids.Count];
        _hot = [.. Enumerable.Range(0, pids.Count).Where(i => pids[i].Hot)];
        _indexOf = Enumerable.Range(0, pids.Count).ToDictionary(i => pids[i].Pid, i => i);
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

    /// <summary>
    /// Where it was reached, for the links that have such a thing.
    ///
    /// "192.168.0.10:35000" for a Wi-Fi dongle and empty for everything else. A
    /// port and a paired radio are already named by the thing that listed them;
    /// a Wi-Fi adapter is listed by nothing, so unless the session says which
    /// address answered there is nowhere to read it back off.
    /// </summary>
    public string Link { get; private init; } = "";

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
    /// Whether this car is being asked for several parameters at a time.
    ///
    /// Off until the car has been shown to do it, off for good if it stops, and
    /// never even tried on a bus that cannot: see <see cref="TryBatching"/>.
    /// Worth reporting, because it is the difference between a rev counter that
    /// moves and one that catches up.
    /// </summary>
    public bool Batching { get; private set; }

    /// <summary>
    /// Opens the adapter, asks the car what it supports, and builds the channels
    /// from the answer.
    ///
    /// Done before the session starts rather than inside <see cref="Open"/>,
    /// because which channels exist is only knowable by asking and a session
    /// needs to know its columns before it can record any.
    /// </summary>
    /// <param name="link">
    /// Where this adapter was reached, where that is a thing worth saying — the
    /// address of a Wi-Fi dongle, which is not discoverable and so is the only
    /// way back to the same device. Empty for a port or a radio, which name
    /// themselves.
    /// </param>
    /// <param name="memory">
    /// What batching has cost this adapter before, if anything is keeping track.
    /// Without one, every session finds out again the hard way.
    /// </param>
    public static Elm327Source Connect(
        IEcuTransport transport, string link = "", IObd2BatchMemory? memory = null)
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

        // Before anything whose answer is kept. The protocol search happens on
        // the first request the car ever sees, and it is narrated: this spends
        // it, so discovery is asked of an adapter that has already settled.
        if (reset.Length > 0) elm.WarmUp();

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

        var source = new Elm327Source(transport, elm, pids, adapter)
        {
            Supports = supported,
            Link = link,
            Memory = memory,
        };

        // The probe here is documented as being enough to kill one of these
        // dongles outright. Letting that escape leaks the transport and tells
        // the memory nothing, so the next connection makes the same mistake.
        try
        {
            source.TryBatching();
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            source.Dispose();
            throw;
        }

        return source;
    }

    /// <summary>What batching has cost this adapter before, where anything remembers.</summary>
    private IObd2BatchMemory? Memory { get; init; }

    /// <summary>
    /// What names this adapter for the purpose of remembering things about it.
    ///
    /// The address where there is one, since that is the device rather than the
    /// story it tells about itself: every clone answers to the name ELM327.
    /// </summary>
    private string Key => Link.Length > 0 ? Link : Adapter;

    /// <summary>
    /// Links batching may kill on one adapter before it is not tried again.
    ///
    /// Two rather than one, because a link can die for reasons that have nothing
    /// to do with the request that happened to be in flight — out of range, key
    /// off, a cable pulled — and condemning a capable adapter on one of those
    /// would cost the whole advantage permanently.
    /// </summary>
    public const int BatchDeathsBeforeGivingUp = 2;

    /// <summary>
    /// Finds out whether this car will answer several parameters at once, and
    /// turns it on if it will.
    ///
    /// Asked rather than assumed, and asked in a way that can only answer yes on
    /// evidence. **Two parameters must come back**: one proves nothing, because
    /// an ordinary single-parameter reply to the first one listed looks exactly
    /// like a batched reply that carried one.
    ///
    /// Only on a bus positively identified as CAN. Not "not identified as slow"
    /// — an unknown protocol is neither, and a request like this reaching a
    /// J1850 vehicle is a malformed one rather than a refused one. An asleep ECU
    /// answering <c>STOPPED</c> is how a link comes to be unidentified at
    /// exactly the wrong moment.
    /// </summary>
    private void TryBatching()
    {
        Batching = false;

        if (_batchGivenUp) return;

        // Not even asked, once an adapter has form. The probe is itself a
        // batched request — on the dongle this was measured against, that one
        // request is enough to kill the session — so probing something already
        // known to fail is not a cheap check, it is the whole cost of the thing
        // being checked for.
        if (Memory?.DeathsOn(Key) >= BatchDeathsBeforeGivingUp)
        {
            _batchGivenUp = true;
            return;
        }

        if (_elm.ProtocolNumber() < Elm327.FirstCanProtocol) return;

        byte[] probe = [.. Chosen(_hot.Length >= 2 ? _hot : [.. Enumerable.Range(0, _pids.Count)])];
        if (probe.Length < 2) return;

        // The probe is itself a batched request, so a link that dies during it
        // died batching — and this is the one place where that is certain rather
        // than inferred. Recorded before the exception leaves, or the next
        // connection probes again and dies again.
        try
        {
            Batching = _elm.ReadMany(probe, Obd2Pids.DataBytesOf).Count >= 2;
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            Batching = false;
            Condemn();
            throw;
        }
    }

    /// <summary>The parameter numbers behind a set of positions, at most a batch's worth.</summary>
    private IEnumerable<byte> Chosen(IReadOnlyList<int> indices) =>
        indices.Take(Elm327.MaxBatchPids).Select(i => _pids[i].Pid);

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
    /// <param name="memory">
    /// What batching has cost this adapter before, if anything is keeping track.
    /// </param>
    public static Elm327Source ConnectOnPort(string portName, IObd2BatchMemory? memory = null)
    {
        Exception? last = null;

        foreach (int baud in BaudRates)
        {
            var transport = new SerialEcuTransport(portName, baud) { OpenAttempts = 3 };

            try
            {
                return Connect(transport, memory: memory);
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
    /// Finds an adapter over Wi-Fi, at an address written "host" or "host:port".
    ///
    /// Nothing is searched for and nothing can be: a Wi-Fi dongle becomes no COM
    /// port, pairs with nothing and is listed nowhere, so an address is the only
    /// way to reach one. <see cref="WifiEcuTransport.KnownAddresses"/> is where
    /// the ones worth trying live.
    /// </summary>
    public static Elm327Source ConnectOverWifi(string address, IObd2BatchMemory? memory = null)
    {
        WifiEcuTransport transport = WifiEcuTransport.At(address);

        try
        {
            return Connect(transport, transport.Address, memory);
        }
        catch (Exception)
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The same, trying each address a Wi-Fi adapter is known to answer on.
    ///
    /// The first failure is the one reported rather than the last. A dongle that
    /// is there answers on the first address — it is the one a Vgate iCar Pro
    /// uses — so a run through the list ends with "nothing at 192.168.4.1", which
    /// names an address the user has never heard of and sends them looking in the
    /// wrong place.
    /// </summary>
    public static Elm327Source ConnectOverWifi(IObd2BatchMemory? memory = null)
    {
        Exception? first = null;

        foreach (string address in WifiEcuTransport.KnownAddresses)
        {
            try
            {
                return ConnectOverWifi(address, memory);
            }
            catch (Exception e)
            {
                first ??= e;
            }
        }

        throw first ?? new EcuProtocolException("No Wi-Fi OBD2 adapter answered.");
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
    /// The fast ones every time, and as many of the rest as a request will carry
    /// — one of them where the car answers a parameter at a time, six where it
    /// does not have to. A slow channel therefore holds its previous reading
    /// between updates, which is what a coolant temperature does anyway; what
    /// must not happen is the rev counter being dragged down to the speed of the
    /// fuel level, and that is why the two sets are asked for separately rather
    /// than as one long list.
    /// </summary>
    public double[] Read() => Watching(ReadRound);

    private double[] ReadRound()
    {
        bool answered = false;
        bool asked = false;

        // One request for the parameters a needle follows, and one for as many
        // of the rest as will fit — where the car allows it. A round that used
        // to be six exchanges for six readings is two for eleven, and the slow
        // channels stop being a queue.
        IReadOnlyList<int> due = NextRotating(Batching ? Elm327.MaxBatchPids : 1);

        if (_hot.Length > 0)
        {
            asked = true;
            answered |= PollGroup(_hot);
        }

        if (due.Count > 0)
        {
            asked = true;
            answered |= PollGroup(due);
        }

        // Every question went unanswered. One parameter falling silent is
        // ordinary; the whole car doing so is the link having gone.
        if (asked && !answered)
        {
            // Remembered across the death, because nothing else survives it. A
            // dongle killed by a batched request does not refuse one — it
            // answers, in full and on time, and then stops existing — so the
            // reply says nothing is wrong, and by the time anything is plainly
            // wrong there is no link left to ask. What was in flight is the only
            // evidence there will ever be, and Recover is where it is weighed.
            _diedBatching = Batching;

            Retries++;

            throw new EcuProtocolException(
                "The adapter answered nothing this round. If the engine was switched off, "
                + "most cars stop responding to OBD2 requests with the key out.");
        }

        return [.. _values];
    }

    /// <summary>
    /// Runs a round, remembering what was in flight if the link dies during it.
    ///
    /// <b>A link can die by throwing as well as by falling silent.</b> A socket
    /// that resets and a serial port that goes away both raise rather than
    /// answer nothing, and the branch that records the death sits after the read
    /// — so a dongle killed by a batched request was never blamed for it, the
    /// recovery kept batching on, and its proving read killed the fresh link
    /// again. That is the connect-probe-die loop this design exists to prevent.
    /// </summary>
    private T Watching<T>(Func<T> round)
    {
        bool batching = Batching;

        try
        {
            return round();
        }
        catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException)
        {
            _diedBatching = batching;
            throw;
        }
    }

    /// <summary>
    /// The next parameters due a turn, skipping any that have been given up on.
    ///
    /// <paramref name="wanted"/> is one when each costs its own round trip and a
    /// batch's worth when they do not — the same rotation either way, taken in
    /// larger bites.
    /// </summary>
    private IReadOnlyList<int> NextRotating(int wanted)
    {
        var due = new List<int>(wanted);

        for (int tried = 0; tried < _pids.Count && due.Count < wanted; tried++)
        {
            int at = _rotating;
            _rotating = (_rotating + 1) % _pids.Count;

            if (!_pids[at].Hot && _silences[at] < GiveUpAfter) due.Add(at);
        }

        return due;
    }

    /// <summary>
    /// Reads a set of parameters, in one request where the car allows it.
    ///
    /// A batch that comes back with nothing is a <em>request</em> that failed,
    /// which says nothing at all about whether the car has these sensors — so
    /// the round is retried one parameter at a time rather than concluded from.
    /// </summary>
    private bool PollGroup(IReadOnlyList<int> due)
    {
        bool batched = Batching && due.Count >= 2;

        if (batched && PollTogether(due, out IReadOnlyList<int> absent))
        {
            // A REPLY CARRYING ONLY SOME OF WHAT WAS ASKED FOR IS NOT A ROUND
            // THAT WORKED. The rest are asked for singly, exactly as they would
            // have been had this link never batched at all.
            //
            // Without that they are asked for once and then never again. A
            // parameter that has answered before is deliberately never retired
            // — see Missed, and the reading it protects — so nothing else in
            // here will ever come back to one that has stopped appearing in the
            // reply. It keeps its gauge, holding a value from minutes ago, and a
            // stale reading is indistinguishable on screen from a live one. A
            // reply cut short by a segmented or truncated frame is exactly how a
            // parameter comes to be absent from every batch while the car is
            // answering for it perfectly well.
            //
            // Bounded by what the reply left out: a car that answers the whole
            // batch pays nothing here, and one that answers none of it costs
            // what not batching would have cost anyway.
            foreach (int index in absent) Poll(index);

            return true;
        }

        bool answered = false;
        foreach (int index in due) answered |= Poll(index);

        // ONLY NOW IS THERE EVIDENCE ABOUT BATCHING, and only if these answered.
        //
        // A silent batch on its own says nothing about the request: it is also
        // what a link that has died looks like, and those are not rare — this
        // dongle stops answering anything, singles included, while the socket
        // goes on reporting itself connected. Counting that as a strike against
        // batching writes a permanent verdict against a capable adapter for
        // something it did not do.
        //
        // What tells them apart is the fallback. Singles answering where the
        // batch did not is the request being refused; singles falling silent too
        // is the link, and batching is a bystander.
        if (batched && answered) Blame();

        return answered;
    }

    /// <summary>
    /// Counts one demonstrated failure of batching, and gives it up on the third.
    ///
    /// <b>Given up for this link only, and not written down.</b> What this
    /// counts is a batch coming back empty while the singles answer — which is
    /// the <em>car</em> declining to be asked several things at once, with the
    /// adapter perfectly healthy. Recording that against the adapter would be
    /// blaming the wrong party, and the key these are filed under is a Wi-Fi
    /// address that is the same on every one of these dongles, so two drives in
    /// such a car would turn batching off for every vehicle thereafter.
    ///
    /// A death is different and is recorded: see <see cref="Condemn"/>.
    ///
    /// It also counts faster than it reads: a round asks two batched questions,
    /// so a car that declines them both is two of the three straight away. That
    /// is deliberate — one round of clear evidence is enough to stop paying for
    /// something this car does not do.
    /// </summary>
    private void Blame()
    {
        if (++_batchMisses < BatchMissesBeforeGivingUp) return;

        Batching = false;
        _batchGivenUp = true;
    }

    /// <summary>
    /// Gives batching up on this link for good, and writes the fact down.
    ///
    /// Not tried again on this link, including after a recovery. An adapter that
    /// has already dropped it once tends to do so again, and each attempt costs
    /// a wasted request and a probation.
    ///
    /// And written down, so the next session does not pay to find this out
    /// again — the finding out is what costs, since an adapter that cannot
    /// survive the request does not refuse it, it answers and then stops
    /// existing.
    /// </summary>
    private void Condemn()
    {
        Batching = false;
        _batchGivenUp = true;

        Memory?.Died(Key);
    }

    /// <summary>
    /// Asks for the lot in one request, and says whether anything came back.
    /// </summary>
    /// <param name="absent">
    /// What was asked for and was not in the reply. The caller reads these
    /// singly; they are deliberately not written off here, because a parameter
    /// missing from a batch has not been asked a question the car declined to
    /// answer — it has been asked one whose answer went astray on the way back.
    /// </param>
    private bool PollTogether(IReadOnlyList<int> due, out IReadOnlyList<int> absent)
    {
        absent = [];

        // Only what can be located in a reply. A batch of fewer than two is not
        // a batch, and — this is the part worth being careful about — it must
        // not be counted as one that went unanswered either: nothing was asked,
        // so nothing failed, and three of those would give up on batching for a
        // reason that never happened.
        byte[] asked = [.. Chosen(due).Where(p => Obd2Pids.DataBytesOf(p) > 0)];
        if (asked.Length < 2) return false;

        IReadOnlyList<Elm327.BatchAnswer> answers = _elm.ReadMany(asked, Obd2Pids.DataBytesOf);

        // Nothing at all. Whether that is evidence against batching is not
        // decided here — see the caller, which finds out by asking singly.
        if (answers.Count == 0) return false;

        _batchMisses = 0;

        foreach (Elm327.BatchAnswer answer in answers)
        {
            if (!_indexOf.TryGetValue(answer.Pid, out int index)) continue;

            Decode(index, answer.Data);
        }

        // Asked for and absent. Handed back rather than counted against here:
        // the caller asks each of them singly, and Poll already records the
        // silence if that comes to nothing too. Counting it in both places
        // retires a parameter in half the rounds it is meant to survive.
        var missing = new List<int>();

        foreach (byte pid in asked)
        {
            if (answers.Any(a => a.Pid == pid)) continue;
            if (_indexOf.TryGetValue(pid, out int index)) missing.Add(index);
        }

        absent = missing;

        return true;
    }

    /// <summary>Empty batched replies in a row before the idea is dropped.</summary>
    private const int BatchMissesBeforeGivingUp = 3;

    /// <summary>
    /// What a second reset gets, when the first one heard nothing at all.
    ///
    /// Short on purpose, because it is asking a narrower question. The first
    /// attempt has to be generous — this adapter's banner has been seen arriving
    /// well over a second late — but a link that has already failed to produce
    /// it once is being asked only whether anything is there, and a live one
    /// answers that in tens of milliseconds.
    /// </summary>
    private static readonly TimeSpan ConfirmingSilence = TimeSpan.FromSeconds(1);

    private bool Poll(int index)
    {
        Obd2Pid pid = _pids[index];
        Span<byte> data = stackalloc byte[8];

        if (!_elm.TryRead(pid.Pid, pid.DataBytes, data, out int got))
        {
            Missed(index);
            return false;
        }

        Decode(index, data[..got]);

        return true;
    }

    private void Decode(int index, ReadOnlySpan<byte> data)
    {
        Obd2Pid pid = _pids[index];

        _silences[index] = 0;
        _answered[index] = true;

        pid.Decode(data, _values.AsSpan(_at[index], pid.Channels.Count));
    }

    /// <summary>
    /// Notes that a parameter said nothing.
    ///
    /// Only one that has <em>never</em> answered is counted towards giving up on
    /// it. A parameter that has answered is a gauge somebody is watching, and
    /// the retirement is never undone — so a run of transient silence, a busy
    /// module or a moment of interference, would cost that channel for the rest
    /// of the drive. Measured elsewhere on the same protocol: a coolant
    /// temperature reading 97 °C, dropped thirty seconds after it was on screen.
    /// </summary>
    private void Missed(int index)
    {
        if (!_answered[index]) _silences[index]++;
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

        // A LINK THAT SAYS NOTHING TO A RESET IS DEAD, NOT SLOW. ATZ never
        // reaches the vehicle — an adapter answers it with its own banner,
        // whatever the car is doing — so silence here is not an ECU that has not
        // woken up, it is a session that is gone. The wedge this catches accepts
        // a connection and then delivers nothing for ever while the socket goes
        // on reporting itself connected, and a fresh one answers in a few tens
        // of milliseconds.
        //
        // Saying so at once matters more than it looks. Walking on into the
        // handshake spends the warm-up, the protocol question and a whole poll
        // round on a corpse — the better part of ten seconds of blank gauges —
        // before anything concludes what the first reply already said.
        if (_elm.Reset().Length == 0 && _elm.Reset(ConfirmingSilence).Length == 0)
            throw new EcuProtocolException(
                "The adapter accepted the connection and then said nothing at all, "
                + "which is a session that has died rather than an ECU that is slow. "
                + "Reconnecting opens a fresh one.");

        // A reset undoes the protocol search, and the adapter does not know what
        // the car speaks until it has spoken to it — asked before that, it
        // answers "undetermined", which is not a bus this may batch on. So the
        // warm-up comes first and the question after it, in that order.
        _elm.WarmUp();

        // NOT PROBED AGAIN, AND HERE THAT IS THE WHOLE POINT. The probe is
        // itself a batched request. Where a batched request is what killed the
        // link, one sent on the way back kills its replacement just as fast —
        // and the session becomes a loop of connect, probe, die, reconnect that
        // never ends and never concludes anything, because the one thing that
        // could tell it apart is a link with the batching left off.
        //
        // So a link that died with batching on comes back without it. Whether
        // this car can batch was settled when the session connected and a reset
        // does not change that answer; what is in doubt is the adapter, and the
        // only cheap way to ask about it is to stop.
        bool suspect = _diedBatching;
        _diedBatching = false;

        if (suspect) Batching = false;

        // Proves it. Opening and resetting say nothing about whether the car is
        // answering again.
        Read();

        // AND THAT READING IS THE EVIDENCE. Single requests answering on a link
        // that had gone silent under batched ones is the same discrimination
        // PollGroup makes inside a round — singles alive where the batch was not
        // — made across a reconnection instead, which is the only place it can
        // be made when the failure takes the socket with it.
        //
        // One is enough here where Blame wants three, because it is not the same
        // evidence and cannot be gathered three times. An empty batched reply
        // costs one request and is worth being unsure about; this costs a
        // session, a reconnection and a proving read, and asking for it again
        // means killing the link again — which is the cost this exists to stop
        // paying.
        //
        // A key turned off and on again at exactly the wrong moment produces
        // this shape once and is written down wrongly. That is what the memory's
        // own threshold is for: it takes BatchDeathsBeforeGivingUp separate
        // drives before an adapter stops being asked at all, so one bad verdict
        // costs nothing on its own.
        if (suspect) Condemn();
    }

    public void Dispose() => _transport.Dispose();
}
