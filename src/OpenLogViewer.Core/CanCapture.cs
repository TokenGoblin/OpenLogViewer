using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>One frame seen on a CAN bus.</summary>
/// <param name="Seconds">When it arrived, from the start of the capture, by this computer's clock.</param>
public sealed record CanFrame(double Seconds, uint Id, bool Extended, bool Remote, byte[] Data)
{
    /// <summary>How the identifier is written: three hex digits for a standard one, eight for an extended one.</summary>
    public string IdText => Extended ? $"0x{Id:X8}" : $"0x{Id:X3}";
}

/// <summary>
/// The slcan (Lawicel) text protocol, which is what a MeatPi WiCAN speaks when its
/// protocol is set to "slcan" — and what CANable, CANUSB and a dozen others speak too.
///
/// Every frame is a line of ASCII hex ended by a carriage return:
/// <c>t</c> + 3-digit id + 1-digit length + data, or <c>T</c> + 8-digit id for an
/// extended one, with <c>r</c>/<c>R</c> for remote frames that carry no data. An
/// adapter told to (<c>Z1</c>) appends four more digits of millisecond timestamp,
/// which is tolerated and ignored: this computer's clock is what the rest of the
/// application lines samples up against.
/// </summary>
public static class Slcan
{
    /// <summary>
    /// The bitrate commands, <c>S0</c> to <c>S8</c>, by kilobits per second.
    /// A Toyota powertrain bus of this era runs at 500.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Bitrates { get; } = new Dictionary<int, string>
    {
        [10] = "S0", [20] = "S1", [50] = "S2", [100] = "S3", [125] = "S4",
        [250] = "S5", [500] = "S6", [800] = "S7", [1000] = "S8",
    };

    /// <summary>
    /// What is sent to start listening: close whatever channel was open, set the
    /// bitrate, then open in <em>listen-only</em> mode.
    ///
    /// Listen-only matters on a car. The adapter then never drives the bus at all —
    /// not even to acknowledge — so a wrong bitrate costs nothing but silence,
    /// where a normal-mode open at the wrong rate puts error frames on a bus the
    /// engine and the brakes share.
    /// </summary>
    public static IReadOnlyList<string> ListenCommands(int kbps) =>
        ["C", Bitrates.TryGetValue(kbps, out string? s)
            ? s
            : throw new ArgumentOutOfRangeException(nameof(kbps), kbps, "slcan has no command for that bitrate."),
         "L"];

    /// <summary>
    /// Reads one line as a frame. False for anything that is not one — the bare
    /// carriage return an adapter answers a command with, its bell for an error,
    /// a version string, noise.
    /// </summary>
    public static bool TryParse(string line, double seconds, out CanFrame frame)
    {
        frame = null!;
        line = line.Trim();
        if (line.Length < 5) return false;

        char kind = line[0];
        bool extended = kind is 'T' or 'R';
        bool remote = kind is 'r' or 'R';
        if (kind is not ('t' or 'T' or 'r' or 'R')) return false;

        int idDigits = extended ? 8 : 3;
        if (line.Length < 1 + idDigits + 1) return false;

        if (!uint.TryParse(line.AsSpan(1, idDigits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
            return false;

        if (extended ? id > 0x1FFFFFFF : id > 0x7FF) return false;

        char lengthDigit = line[1 + idDigits];
        if (lengthDigit is < '0' or > '8') return false;
        int length = lengthDigit - '0';

        int dataAt = 2 + idDigits;
        int dataDigits = remote ? 0 : length * 2;

        // Exactly the data, or the data and a four-digit timestamp. Anything else is
        // a line that arrived torn, and a torn frame decoded is a wrong reading.
        int rest = line.Length - dataAt;
        if (rest != dataDigits && rest != dataDigits + 4) return false;

        byte[] data = new byte[remote ? 0 : length];

        for (int i = 0; i < data.Length; i++)
        {
            if (!byte.TryParse(line.AsSpan(dataAt + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i]))
                return false;
        }

        if (rest == dataDigits + 4
            && !ushort.TryParse(line.AsSpan(dataAt + dataDigits, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return false;

        frame = new CanFrame(seconds, id, extended, remote, data);
        return true;
    }
}

/// <summary>What is known about one identifier after a capture, or during one.</summary>
public sealed class CanIdSummary
{
    internal CanIdSummary(uint id, bool extended)
    {
        Id = id;
        Extended = extended;
    }

    public uint Id { get; }

    public bool Extended { get; }

    public string IdText => Extended ? $"0x{Id:X8}" : $"0x{Id:X3}";

    public long Count { get; internal set; }

    public double First { get; internal set; }

    public double Last { get; internal set; }

    public int Length { get; internal set; }

    public byte[] LastData { get; internal set; } = [];

    /// <summary>Which byte positions have ever held more than one value — the ones carrying something.</summary>
    public bool[] Changes { get; } = new bool[8];

    /// <summary>Frames a second over the time this id has been seen.</summary>
    public double Rate => Last > First ? (Count - 1) / (Last - First) : 0;

    internal void Add(CanFrame frame)
    {
        if (Count == 0)
        {
            First = frame.Seconds;
        }
        else
        {
            for (int i = 0; i < Math.Min(frame.Data.Length, LastData.Length); i++)
                if (frame.Data[i] != LastData[i]) Changes[i] = true;
        }

        Count++;
        Last = frame.Seconds;
        Length = Math.Max(Length, frame.Data.Length);
        LastData = frame.Data;
    }
}

/// <summary>
/// Listens to a CAN bus through an slcan adapter and writes every frame to a file.
///
/// The fast way to see a car, where the car allows it. OBD2 is a question and an
/// answer, one at a time, on a bus the engine computer is also busy on; what this
/// reads instead is what the modules already broadcast to each other — revs,
/// throttle, speed — tens of times a second each, without being asked anything.
/// The price is that nothing is labelled: which identifier carries revs, and in
/// which bytes, is for the capture to reveal (see <see cref="CanByteLog"/>).
///
/// Only possible on a CAN car with the bus reachable at the OBD plug (pins 6 and
/// 14). A K-line car has no broadcasts to hear, and some cars put a gateway in
/// front of the plug that passes diagnostics and nothing else — which presents as
/// a capture that opens fine and stays empty.
/// </summary>
public sealed class CanCapture : IDisposable
{
    private readonly IEcuTransport _transport;
    private readonly int _kbps;
    private readonly object _gate = new();
    private readonly Dictionary<(uint, bool), CanIdSummary> _ids = [];
    private readonly List<CanFrame> _frames = [];
    private StreamWriter? _file;
    private Thread? _thread;
    private volatile bool _stop;
    private DateTime _started;

    /// <summary>
    /// Kept in memory as well as written, so the byte log can be built at the end
    /// without reading the file back. About 60 bytes each; the cap is several
    /// minutes of a busy bus, and past it the file still gets everything.
    /// </summary>
    public const int MaxFramesKept = 5_000_000;

    public CanCapture(IEcuTransport transport, string path, int kbps = 500)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = Slcan.ListenCommands(kbps);

        Path = path;
        _kbps = kbps;
    }

    /// <summary>Where the raw frames are written, one per line.</summary>
    public string Path { get; }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Why the capture stopped, when it stopped on its own.</summary>
    public string? Error { get; private set; }

    public long FrameCount { get; private set; }

    /// <summary>Lines that came in and were not frames: replies, errors, torn lines.</summary>
    public long OtherLines { get; private set; }

    /// <summary>Whether listen-only had to be given up for a normal open (see <see cref="Start"/>).</summary>
    public bool FellBackToNormalOpen { get; private set; }

    public double Elapsed => _started == default ? 0 : ((_stoppedAt ?? DateTime.UtcNow) - _started).TotalSeconds;

    private DateTime? _stoppedAt;

    /// <summary>Every identifier seen so far, busiest first.</summary>
    public IReadOnlyList<CanIdSummary> Ids
    {
        get
        {
            lock (_gate) return [.. _ids.Values.OrderByDescending(s => s.Count)];
        }
    }

    /// <summary>The frames kept in memory, in arrival order.</summary>
    public IReadOnlyList<CanFrame> Frames
    {
        get
        {
            lock (_gate) return [.. _frames];
        }
    }

    /// <summary>
    /// Opens the adapter, starts listening, and returns. Frames are read on a thread
    /// of their own until <see cref="Stop"/>.
    ///
    /// If listen-only produces nothing for a few seconds the adapter is opened
    /// normally instead (<c>O</c>). Not every slcan firmware implements <c>L</c>,
    /// and one that does not answers it with a bell and stays closed. A normal open
    /// still sends nothing of its own; it only acknowledges what it hears.
    /// </summary>
    public void Start()
    {
        if (IsRunning) throw new InvalidOperationException("Already capturing.");

        _transport.Open();

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        _file = new StreamWriter(Path, append: false, new UTF8Encoding(false));
        _file.WriteLine("Time,Id,Extended,Length,Data");

        foreach (string command in Slcan.ListenCommands(_kbps)) Send(command);

        _started = DateTime.UtcNow;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "CAN capture" };
        _thread.Start();
    }

    private void Send(string command)
    {
        _transport.Write(Encoding.ASCII.GetBytes(command + "\r"));
        Thread.Sleep(50);
    }

    private void Run()
    {
        var line = new StringBuilder();
        byte[] buffer = new byte[4096];
        DateTime quietSince = DateTime.UtcNow;
        long flushedAt = 0;

        try
        {
            while (!_stop)
            {
                int got = _transport.Read(buffer, TimeSpan.FromMilliseconds(200));

                if (got == 0)
                {
                    if (!_transport.IsOpen)
                    {
                        Error = "The adapter closed the connection.";
                        break;
                    }

                    if (FrameCount == 0 && !FellBackToNormalOpen && (DateTime.UtcNow - quietSince).TotalSeconds > 3)
                    {
                        FellBackToNormalOpen = true;
                        Send("C");
                        Send("O");
                    }

                    continue;
                }

                double now = (DateTime.UtcNow - _started).TotalSeconds;

                for (int i = 0; i < got; i++)
                {
                    char c = (char)buffer[i];

                    // A bell is an slcan "no": end of a reply, and nothing to keep.
                    if (c is '\r' or '\n' or '\a')
                    {
                        if (line.Length > 0) Take(line.ToString(), now);
                        line.Clear();
                    }
                    else if (line.Length < 64)
                    {
                        line.Append(c);
                    }
                }

                if (FrameCount - flushedAt > 500)
                {
                    _file?.Flush();
                    flushedAt = FrameCount;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Error = e.Message;
        }
    }

    private void Take(string text, double now)
    {
        if (!Slcan.TryParse(text, now, out CanFrame frame))
        {
            OtherLines++;
            return;
        }

        FrameCount++;

        _file?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{frame.Seconds:0.0000},{frame.IdText},{(frame.Extended ? 1 : 0)},{frame.Data.Length},{Convert.ToHexString(frame.Data)}"));

        lock (_gate)
        {
            var key = (frame.Id, frame.Extended);
            if (!_ids.TryGetValue(key, out CanIdSummary? summary))
                _ids[key] = summary = new CanIdSummary(frame.Id, frame.Extended);

            summary.Add(frame);

            if (_frames.Count < MaxFramesKept) _frames.Add(frame);
        }
    }

    /// <summary>
    /// Stops listening and closes the adapter. The channel is closed (<c>C</c>)
    /// first, so the next program to use the dongle does not find it still open.
    /// </summary>
    public void Stop()
    {
        _stop = true;
        _stoppedAt ??= DateTime.UtcNow;
        _thread?.Join(TimeSpan.FromSeconds(2));

        try
        {
            if (_transport.IsOpen) _transport.Write("C\r"u8);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
        }

        _transport.Close();

        _file?.Flush();
        _file?.Dispose();
        _file = null;
    }

    /// <summary>
    /// A plain-text summary of every identifier: how often, how long, which bytes
    /// moved, and what they last held. The first thing to read after a capture.
    /// </summary>
    public string Summary()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{FrameCount} frames, {Ids.Count} identifiers, over {Elapsed:0.0} s at {_kbps} kbit/s{(FellBackToNormalOpen ? " (listen-only gave nothing; opened normally)" : " (listen-only)")}");
        text.AppendLine();
        text.AppendLine("Id          Count    Hz   Len  Moving bytes  Last data");

        foreach (CanIdSummary id in Ids)
        {
            string moving = string.Concat(Enumerable.Range(0, 8).Select(i => i < id.Length ? (id.Changes[i] ? "x" : ".") : " "));
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{id.IdText,-10} {id.Count,7} {id.Rate,6:0.0}  {id.Length,2}   {moving}      {Convert.ToHexString(id.LastData)}");
        }

        return text.ToString();
    }

    public void Dispose()
    {
        if (IsRunning || _file is not null) Stop();
        _transport.Dispose();
    }
}

/// <summary>
/// Turns a capture into an ordinary log the viewer can open: one channel for each
/// byte that moved, and one for each pair of neighbouring bytes read as a 16-bit
/// big-endian number, sampled at a fixed rate with each value held until the next
/// frame replaces it.
///
/// This is how an unlabelled bus gets labelled. Rev the engine during the capture,
/// open this, and the channel whose trace climbs and falls with the pedal is the
/// revs — Toyota sends its numbers big-endian, high byte first, which is why the
/// pairs are read that way round.
/// </summary>
public static class CanByteLog
{
    public static void Write(IReadOnlyList<CanFrame> frames, string path, double hz = 20)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hz, 0);

        if (frames.Count == 0) throw new InvalidOperationException("The capture holds no frames.");

        // Which byte positions moved, per identifier.
        var moving = new Dictionary<(uint, bool), bool[]>();
        var last = new Dictionary<(uint, bool), byte[]>();

        foreach (CanFrame f in frames)
        {
            var key = (f.Id, f.Extended);
            if (!moving.TryGetValue(key, out bool[]? m)) moving[key] = m = new bool[8];

            if (last.TryGetValue(key, out byte[]? prior))
                for (int i = 0; i < Math.Min(prior.Length, f.Data.Length); i++)
                    if (prior[i] != f.Data[i]) m[i] = true;

            last[key] = f.Data;
        }

        // The columns: (id, extended, first byte, width in bytes).
        var columns = new List<(uint Id, bool Ext, int At, int Width, string Name)>();

        foreach (var ((id, ext), m) in moving.OrderBy(p => p.Key.Item1))
        {
            string name = ext ? $"{id:X8}" : $"{id:X3}";

            for (int i = 0; i < 8; i++)
                if (m[i]) columns.Add((id, ext, i, 1, $"{name} b{i}"));

            for (int i = 0; i < 7; i++)
                if (m[i] || m[i + 1]) columns.Add((id, ext, i, 2, $"{name} w{i}"));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException("Nothing on the bus changed during the capture, so there is nothing to plot.");

        // Each identifier starts at its first value rather than blank, so a slow one
        // does not open the log with a gap that reads as a dropout.
        var current = new Dictionary<(uint, bool), byte[]>();
        foreach (CanFrame f in frames) current.TryAdd((f.Id, f.Extended), f.Data);

        double step = 1 / hz;
        double end = frames[^1].Seconds;
        int next = 0;

        using var file = new StreamWriter(path, append: false, new UTF8Encoding(false));
        file.WriteLine("Time," + string.Join(",", columns.Select(c => c.Name)));

        var row = new StringBuilder();

        for (double t = frames[0].Seconds; t <= end + 1e-9; t += step)
        {
            while (next < frames.Count && frames[next].Seconds <= t)
            {
                current[(frames[next].Id, frames[next].Extended)] = frames[next].Data;
                next++;
            }

            row.Clear();
            row.Append(t.ToString("0.000", CultureInfo.InvariantCulture));

            foreach (var c in columns)
            {
                row.Append(',');

                if (current.TryGetValue((c.Id, c.Ext), out byte[]? data) && c.At + c.Width <= data.Length)
                {
                    int value = c.Width == 1 ? data[c.At] : (data[c.At] << 8) | data[c.At + 1];
                    row.Append(value.ToString(CultureInfo.InvariantCulture));
                }
            }

            file.WriteLine(row);
        }
    }
}
