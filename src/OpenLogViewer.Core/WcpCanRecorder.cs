using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Writes a WiCAN's WCP CAN stream to a file, in the same shape as a
/// <see cref="CanCapture"/>, and stops by itself.
///
/// <para>
/// <b>The same file as an slcan capture, on purpose.</b> The header, the columns
/// and the per-identifier summary are <see cref="CanCapture"/>'s, and the frames
/// kept in memory are <see cref="SlcanFrame"/>s, so a stream recorded over WCP is
/// turned into a byte log by <see cref="CanByteLog"/> and opened in the viewer
/// exactly like a capture over Wi-Fi. Times are this computer's clock from the
/// start of the recording, as there; the device's own millisecond stamp is not
/// comparable with anything else the application records.
/// </para>
///
/// <para>
/// <b>Bounded, because something unattended will start it.</b> An agent that asks
/// for a stream and forgets about it should not fill a disk: the recording ends at
/// <see cref="MaxSeconds"/> or <see cref="MaxFrames"/>, whichever comes first, and
/// <see cref="StoppedBecause"/> says which.
/// </para>
///
/// <para>
/// Frames arrive on the link's reading thread, so writing one is kept to a line
/// of text into a buffered file and nothing else.
/// </para>
/// </summary>
public sealed class WcpCanRecorder : IDisposable
{
    private readonly WcpClient _client;
    private readonly Lock _gate = new();
    private readonly Dictionary<(uint, bool), CanIdSummary> _ids = [];
    private readonly List<SlcanFrame> _frames = [];
    private StreamWriter? _file;
    private DateTime _started;
    private DateTime? _stoppedAt;

    /// <summary>The most frames kept in memory for the byte log; the file still gets every one up to <see cref="MaxFrames"/>.</summary>
    public const int MaxFramesKept = CanCapture.MaxFramesKept;

    public WcpCanRecorder(WcpClient client, string path, double maxSeconds = 60, long maxFrames = 1_000_000)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);

        Path = path;
        MaxSeconds = maxSeconds;
        MaxFrames = maxFrames;
    }

    /// <summary>Where the frames are written, one per line.</summary>
    public string Path { get; }

    public double MaxSeconds { get; }

    public long MaxFrames { get; }

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _file is not null;
        }
    }

    public long FrameCount { get; private set; }

    /// <summary>Frames the device reported losing (<c>!DROP</c>) while this was recording.</summary>
    public long Dropped { get; private set; }

    /// <summary>Why it stopped: "asked", "time", "frames", or "link". Empty while it runs.</summary>
    public string StoppedBecause { get; private set; } = "";

    public double Elapsed => _started == default ? 0 : ((_stoppedAt ?? DateTime.UtcNow) - _started).TotalSeconds;

    /// <summary>Every identifier seen so far, busiest first.</summary>
    public IReadOnlyList<CanIdSummary> Ids
    {
        get
        {
            lock (_gate) return [.. _ids.Values.OrderByDescending(s => s.Count)];
        }
    }

    /// <summary>The frames kept in memory, in arrival order.</summary>
    public IReadOnlyList<SlcanFrame> Frames
    {
        get
        {
            lock (_gate) return [.. _frames];
        }
    }

    /// <summary>
    /// Opens the file and starts listening to the stream. The stream itself is
    /// started by the caller (<see cref="WcpClient.CanStart"/>,
    /// <see cref="WcpClient.CanStreamOn"/>), which is where a filter belongs.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_file is not null) throw new InvalidOperationException("Already recording.");

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            _file = new StreamWriter(Path, append: false, new UTF8Encoding(false));
            _file.WriteLine("Time,Id,Extended,Length,Data");

            _started = DateTime.UtcNow;
            _stoppedAt = null;
            StoppedBecause = "";
        }

        _client.CanFrame += OnFrame;
        _client.Dropped += OnDropped;
        _client.Disarmed += OnDisarmed;
    }

    private void OnDisarmed(string reason)
    {
        // A closed link ends the stream as surely as it ends the arming.
        if (reason == "link") Stop("link");
    }

    private void OnDropped(long n)
    {
        lock (_gate) Dropped += n;
    }

    private void OnFrame(WcpCanEvent e)
    {
        string? stop = null;

        lock (_gate)
        {
            if (_file is null) return;

            double seconds = (DateTime.UtcNow - _started).TotalSeconds;

            if (seconds > MaxSeconds)
            {
                stop = "time";
            }
            else
            {
                var frame = new SlcanFrame(seconds, e.Id, e.Extended, e.Remote, e.Data);
                FrameCount++;

                _file.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{frame.Seconds:0.0000},{frame.IdText},{(frame.Extended ? 1 : 0)},{frame.Data.Length},{Convert.ToHexString(frame.Data)}"));

                var key = (frame.Id, frame.Extended);
                if (!_ids.TryGetValue(key, out CanIdSummary? summary))
                    _ids[key] = summary = new CanIdSummary(frame.Id, frame.Extended);

                summary.Add(frame);

                if (_frames.Count < MaxFramesKept) _frames.Add(frame);

                if (FrameCount >= MaxFrames) stop = "frames";
            }
        }

        if (stop is not null) Stop(stop);
    }

    /// <summary>
    /// Checks the time limit, for a quiet bus where no frame arrives to notice it.
    /// True while still recording.
    /// </summary>
    public bool Poll()
    {
        if (IsRunning && Elapsed > MaxSeconds) Stop("time");
        return IsRunning;
    }

    /// <summary>Stops recording and closes the file. The stream on the device is left to the caller.</summary>
    public void Stop(string because = "asked")
    {
        _client.CanFrame -= OnFrame;
        _client.Dropped -= OnDropped;
        _client.Disarmed -= OnDisarmed;

        lock (_gate)
        {
            if (_file is null) return;

            _file.Flush();
            _file.Dispose();
            _file = null;
            _stoppedAt = DateTime.UtcNow;
            StoppedBecause = because;
        }
    }

    /// <summary>
    /// Writes the recording as a byte log the viewer can open — one channel per
    /// byte that moved, one per neighbouring pair (see <see cref="CanByteLog"/>).
    /// </summary>
    public void WriteByteLog(string path, double hz = 20) => CanByteLog.Write(Frames, path, hz);

    public void Dispose() => Stop();
}
