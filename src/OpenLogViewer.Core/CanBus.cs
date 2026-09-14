namespace OpenLogViewer.Core;

/// <summary>
/// What has been seen at one identifier.
/// </summary>
/// <param name="Id">The identifier.</param>
/// <param name="IsExtended">Whether it is a 29-bit one.</param>
/// <param name="Count">Frames seen.</param>
/// <param name="FirstAt">When the first arrived, in the source's microseconds.</param>
/// <param name="LastAt">When the most recent arrived.</param>
/// <param name="Data">The most recent payload.</param>
/// <param name="Changed">
/// Which bits have ever differed from the first frame seen at this identifier —
/// one mask byte per data byte, a set bit meaning that bit has moved.
///
/// Bits rather than bytes because that is how a bus is actually laid out: a
/// single byte often carries eight unrelated flags, and "this byte changes" says
/// nothing about which of them did. Watching one bit move when a switch is
/// pressed is the whole method.
/// </param>
/// <param name="Gap">
/// Mean microseconds between frames, or null where only one has been seen.
/// </param>
/// <param name="Jitter">
/// How far the widest gap strayed from that mean, which is what separates
/// something sent on a timer from something sent when an event happens.
/// </param>
public sealed record CanId(
    int Id,
    bool IsExtended,
    int Count,
    long FirstAt,
    long LastAt,
    byte[] Data,
    byte[] Changed,
    double? Gap,
    double Jitter)
{
    /// <summary>The identifier as it is written down.</summary>
    public string Label => IsExtended ? $"{Id:X8}" : $"{Id:X3}";

    /// <summary>Frames a second, or null until there are two to measure between.</summary>
    public double? Rate => Gap is > 0 ? 1_000_000d / Gap.Value : null;

    /// <summary>
    /// Whether this looks like it is sent on a timer rather than when something
    /// happens.
    ///
    /// A guess, and labelled as one on screen. Periodic traffic is most of a
    /// vehicle bus and the sporadic frames are usually the interesting ones — a
    /// door, a button, a fault — so it is worth sorting them apart even
    /// imperfectly.
    /// </summary>
    public bool LooksPeriodic => Count > 2 && Gap is > 0 && Jitter < Gap.Value * 0.25;

    /// <summary>Whether any bit here has ever moved.</summary>
    public bool EverChanged => Changed.Any(b => b != 0);
}

/// <summary>
/// Everything seen on a bus, and the questions somebody asks of it.
///
/// <para>
/// This is the half of a sniffer that is not the wire. An ECU — or an adapter —
/// hands over frames and nothing more; what makes those frames useful is done
/// here, the same way for every source, so a MaxxECU and a plain CAN adapter give
/// the same tool rather than two that resemble each other.
/// </para>
/// <para>
/// Built for the job of reading a bus nobody has documented. That work is mostly
/// one move repeated: take a note of what the bus is doing, make something
/// happen — press a button, turn a wheel, open a door — and ask what changed.
/// <see cref="Mark"/> and <see cref="SinceMark"/> are that move, and the rest of
/// this exists to make its answer short enough to read.
/// </para>
/// </summary>
public sealed class CanBus
{
    private readonly Dictionary<int, Tracked> _ids = [];
    private Dictionary<int, (int Count, byte[] Data)> _mark = [];

    /// <summary>Frames taken in, counting ones this has been told were lost.</summary>
    public int Seen { get; private set; }

    /// <summary>
    /// Frames the source says it lost, or null where it cannot say.
    ///
    /// Carried here so that anything showing the bus shows this beside it. A
    /// capture that quietly lost frames is worse than a short one: somebody
    /// concludes an identifier does not exist when in truth it was missed, and
    /// nothing about the display would have told them.
    /// </summary>
    public int? Dropped { get; set; }

    /// <summary>How many distinct identifiers have been seen.</summary>
    public int Count => _ids.Count;

    public void Add(CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        Seen++;

        if (_ids.TryGetValue(frame.Id, out Tracked? tracked)) tracked.Add(frame);
        else _ids[frame.Id] = new Tracked(frame);
    }

    public void Add(IEnumerable<CanFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        foreach (CanFrame frame in frames) Add(frame);
    }

    /// <summary>Everything seen, in identifier order.</summary>
    public IReadOnlyList<CanId> Ids() =>
        [.. _ids.Values.Select(t => t.Summary()).OrderBy(i => i.Id)];

    /// <summary>
    /// Takes a note of the bus as it stands, for <see cref="SinceMark"/> to
    /// compare against.
    /// </summary>
    public void Mark() =>
        _mark = _ids.ToDictionary(p => p.Key, p => (p.Value.Count, (byte[])p.Value.Data.Clone()));

    /// <summary>
    /// What has happened since the mark: identifiers that were not there before,
    /// and identifiers whose data is not what it was.
    ///
    /// <para>
    /// The point of the whole exercise. Mark the bus, make one thing happen, and
    /// this is the short list of what that thing touched — instead of two
    /// thousand frames a second of everything else the car is saying.
    /// </para>
    /// <para>
    /// Identifiers that merely arrived again are not news and are left out; a
    /// periodic frame repeating unchanged is the bus idling, not a result.
    /// </para>
    /// </summary>
    public IReadOnlyList<CanChange> SinceMark()
    {
        var changes = new List<CanChange>();

        foreach ((int id, Tracked tracked) in _ids)
        {
            if (!_mark.TryGetValue(id, out (int Count, byte[] Data) was))
            {
                changes.Add(new CanChange(tracked.Summary(), IsNew: true, Moved: []));
                continue;
            }

            byte[] moved = Difference(was.Data, tracked.Data);

            if (moved.Any(b => b != 0))
                changes.Add(new CanChange(tracked.Summary(), IsNew: false, Moved: moved));
        }

        return [.. changes.OrderByDescending(c => c.IsNew).ThenBy(c => c.Id.Id)];
    }

    /// <summary>Forgets everything, for starting a capture again.</summary>
    public void Clear()
    {
        _ids.Clear();
        _mark = [];
        Seen = 0;
        Dropped = null;
    }

    /// <summary>
    /// Which bits differ between two payloads, as one mask byte per data byte.
    ///
    /// Frames at one identifier can change length — rare, and real — so this
    /// compares what both have and treats bytes only one of them has as having
    /// moved entirely.
    /// </summary>
    internal static byte[] Difference(ReadOnlySpan<byte> was, ReadOnlySpan<byte> now)
    {
        var moved = new byte[Math.Max(was.Length, now.Length)];

        for (int i = 0; i < moved.Length; i++)
        {
            byte before = i < was.Length ? was[i] : (byte)0;
            byte after = i < now.Length ? now[i] : (byte)0;

            moved[i] = (byte)(before ^ after);
        }

        return moved;
    }

    /// <summary>One identifier as it accumulates, which the record above is a view of.</summary>
    private sealed class Tracked
    {
        private readonly bool _extended;
        private readonly long _first;
        private readonly byte[] _changed;
        private byte[] _wasFirst;

        private long _last;
        private double _gapTotal;
        private double _gapWidest;
        private double _gapNarrowest = double.MaxValue;

        public Tracked(CanFrame frame)
        {
            Id = frame.Id;
            _extended = frame.IsExtended;
            _first = _last = frame.At;
            _wasFirst = frame.Data;
            Data = frame.Data;
            _changed = new byte[8];
            Count = 1;
        }

        public int Id { get; }

        public int Count { get; private set; }

        public byte[] Data { get; private set; }

        public void Add(CanFrame frame)
        {
            byte[] moved = Difference(_wasFirst, frame.Data);

            for (int i = 0; i < moved.Length && i < _changed.Length; i++) _changed[i] |= moved[i];

            // Gaps are measured against the previous frame rather than averaged
            // over the whole run, so a bus that goes quiet and comes back reads
            // as one long gap instead of quietly halving its rate.
            double gap = frame.At - _last;

            if (gap > 0)
            {
                _gapTotal += gap;
                _gapWidest = Math.Max(_gapWidest, gap);
                _gapNarrowest = Math.Min(_gapNarrowest, gap);
            }

            _last = frame.At;
            Data = frame.Data;
            Count++;
        }

        public CanId Summary()
        {
            double? gap = Count > 1 ? _gapTotal / (Count - 1) : null;

            return new CanId(
                Id,
                _extended,
                Count,
                _first,
                _last,
                Data,
                [.. _changed.Take(Math.Max(Data.Length, _wasFirst.Length))],
                gap,

                // The spread of the gaps rather than a deviation, because what
                // matters is whether they are all much the same, and one late
                // frame should show.
                Count > 2 && _gapNarrowest < double.MaxValue ? _gapWidest - _gapNarrowest : 0);
        }
    }
}

/// <summary>Something the bus did that was not simply carrying on.</summary>
/// <param name="Id">The identifier, as it now stands.</param>
/// <param name="IsNew">True when it had not been seen at all before the mark.</param>
/// <param name="Moved">
/// Which bits differ from the mark, one mask byte per data byte. Empty for a new
/// identifier, where everything is new and nothing is a change.
/// </param>
public sealed record CanChange(CanId Id, bool IsNew, byte[] Moved);
