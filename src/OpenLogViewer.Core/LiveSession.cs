using System.Diagnostics;
using System.Text;

namespace OpenLogViewer.Core;

public sealed record LiveSessionSettings
{
    /// <summary>Where to record. Null keeps the session in memory only.</summary>
    public string? RecordingPath { get; init; }

    /// <summary>
    /// Samples kept in memory. Older ones are dropped from the view but stay in
    /// the recording, so a long session cannot grow without bound while still
    /// leaving the whole run on disk.
    /// </summary>
    public int MaximumSamples { get; init; } = 500_000;

    /// <summary>Consecutive failures before the session gives up on the link.</summary>
    public int FailuresBeforeStopping { get; init; } = 20;
}

/// <summary>What a session has done so far, for the status line.</summary>
public sealed record LiveSessionStatus(
    int Samples, double Seconds, double Rate, int Retries, int Failures, string? Error)
{
    public bool Faulted => Error is not null;
}

/// <summary>
/// Polls an ECU and turns its realtime blocks into a log.
///
/// The result is an ordinary <see cref="LogDocument"/>, so everything already
/// built works on a live session unchanged: the plot, the heat table, filters,
/// calculated channels and VE Calibration. Channels take the names the firmware's
/// datalog definition gives them, which are the names a recorded log uses, so a
/// preset saved against a file applies to the ECU as well.
///
/// Recording is continuous rather than saved at the end. A live session ends
/// when a cable is pulled or a laptop sleeps at least as often as it ends by
/// being stopped, and losing a session to that is worse than the cost of
/// keeping a writer open.
/// </summary>
public sealed class LiveSession : IDisposable
{
    private readonly EcuConnection _connection;
    private readonly RealtimeDecoder _decoder;
    private readonly LiveSessionSettings _settings;

    private readonly int[] _sourceIndex;
    private readonly string[] _names;
    private readonly string[] _units;
    private readonly int[] _digits;

    private readonly List<float>[] _columns;
    private readonly List<double> _time = [];
    private readonly Lock _gate = new();

    private readonly Stopwatch _clock = new();
    private StreamWriter? _recorder;
    private Thread? _worker;
    private CancellationTokenSource? _cancel;

    private int _failures;
    private string? _error;
    private LogDocument? _snapshot;
    private int _snapshotAt = -1;

    public LiveSession(
        EcuConnection connection,
        RealtimeDecoder decoder,
        IReadOnlyList<DatalogEntry> datalog,
        LiveSessionSettings? settings = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _settings = settings ?? new LiveSessionSettings();

        // The datalog definition decides what a session carries and what each
        // channel is called. Taking every decoded value instead would give a few
        // hundred internal names no preset or filter would ever match.
        var indices = new List<int>();
        var names = new List<string>();
        var units = new List<string>();
        var digits = new List<int>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DatalogEntry entry in datalog)
        {
            int at = LastIndexOf(decoder.Names, entry.Channel);
            if (at < 0) continue;

            string label = entry.Label.Length > 0 ? entry.Label : entry.Channel;
            if (!taken.Add(label)) continue;

            indices.Add(at);
            names.Add(label);
            units.Add(decoder.Units[at]);
            digits.Add(entry.Digits);
        }

        _sourceIndex = [.. indices];
        _names = [.. names];
        _units = [.. units];
        _digits = [.. digits];
        _columns = [.. _names.Select(_ => new List<float>())];
    }

    /// <summary>Channels this session records, in order.</summary>
    public IReadOnlyList<string> Names => _names;

    public bool IsRunning => _worker is { IsAlive: true };

    public string? RecordingPath { get; private set; }

    /// <summary>Raised after each block, on the polling thread.</summary>
    public event Action<LiveSessionStatus>? Updated;

    public LiveSessionStatus Status
    {
        get
        {
            lock (_gate)
            {
                double seconds = _time.Count > 0 ? _time[^1] : 0;
                return new LiveSessionStatus(
                    _time.Count, seconds,
                    seconds > 0 ? _time.Count / seconds : 0,
                    _connection.Retries, _failures, _error);
            }
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        if (_names.Length == 0) throw new InvalidOperationException("No channels to record.");

        _error = null;
        _failures = 0;
        _connection.Open();

        if (_settings.RecordingPath is { Length: > 0 } path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

            // No BOM, matching the exporter; AutoFlush so a pulled cable costs
            // at most the row in hand.
            _recorder = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            CsvExport.WriteHeader(_recorder, ["Time", .. _names], ["s", .. _units]);
            RecordingPath = path;
        }

        _clock.Restart();
        _cancel = new CancellationTokenSource();
        _worker = new Thread(() => Poll(_cancel.Token))
        {
            IsBackground = true,
            Name = "ECU poll",
        };

        _worker.Start();
    }

    public void Stop()
    {
        _cancel?.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(2));
        _worker = null;

        _recorder?.Flush();
        _recorder?.Dispose();
        _recorder = null;
    }

    private void Poll(CancellationToken token)
    {
        int size = _decoder.Layout.BlockSize;
        var row = new double[_names.Length + 1];

        while (!token.IsCancellationRequested)
        {
            try
            {
                double[] values = _decoder.Decode(_connection.ReadRealtime(size));
                double at = _clock.Elapsed.TotalSeconds;

                Append(at, values);
                _failures = 0;

                row[0] = at;
                for (int i = 0; i < _sourceIndex.Length; i++) row[i + 1] = values[_sourceIndex[i]];
                if (_recorder is { } recorder) CsvExport.WriteRow(recorder, row);
            }
            catch (Exception e) when (e is EcuProtocolException or IOException or InvalidOperationException
                                          or UnauthorizedAccessException)
            {
                // One bad block is ordinary on a radio link; a run of them means
                // the ECU is gone, and pretending otherwise just fills the log
                // with nothing.
                if (++_failures < _settings.FailuresBeforeStopping) continue;

                _error = $"Lost the ECU after {_failures} failed reads: {e.Message}";
                break;
            }

            Updated?.Invoke(Status);
        }

        Updated?.Invoke(Status);
    }

    private void Append(double seconds, double[] values)
    {
        lock (_gate)
        {
            _time.Add(seconds);
            for (int i = 0; i < _sourceIndex.Length; i++)
                _columns[i].Add((float)values[_sourceIndex[i]]);

            // Trimmed from the front, so the view stays bounded on a long session
            // while the recording on disk keeps everything.
            int excess = _time.Count - _settings.MaximumSamples;
            if (excess <= 0) return;

            _time.RemoveRange(0, excess);
            foreach (List<float> column in _columns) column.RemoveRange(0, excess);
        }
    }

    /// <summary>
    /// The session so far as an ordinary document.
    ///
    /// Cached against the sample count: the plot asks for this on every repaint,
    /// and copying a few hundred columns each time for a document that has not
    /// changed would cost more than drawing it.
    /// </summary>
    public LogDocument Snapshot()
    {
        lock (_gate)
        {
            if (_snapshot is not null && _snapshotAt == _time.Count) return _snapshot;

            var channels = new List<LogChannel>(_names.Length);
            for (int i = 0; i < _names.Length; i++)
                channels.Add(LogChannel.Adopt(_names[i], _units[i], _digits[i], [.. _columns[i]]));

            _snapshotAt = _time.Count;
            _snapshot = new LogDocument
            {
                FilePath = RecordingPath ?? "",
                Channels = channels,
                Time = new LogChannel("Time", "s", 3, [.. _time], preservePrecision: true),
                FormatName = "Live",
                RecordedAt = DateTimeOffset.Now,
            };

            return _snapshot;
        }
    }

    private static int LastIndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = names.Count - 1; i >= 0; i--)
            if (names[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }

    public void Dispose()
    {
        Stop();
        _connection.Dispose();
    }
}
