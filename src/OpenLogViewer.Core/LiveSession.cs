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

    /// <summary>Consecutive failures before the link is treated as lost.</summary>
    public int FailuresBeforeStopping { get; init; } = 20;

    /// <summary>
    /// Samples per second to aim for. Zero polls as fast as the link allows.
    ///
    /// A cap rather than a target, and it exists because the link is often much
    /// faster than the data is worth. A rusEFI over USB answers about 280 times
    /// a second, and taking it up on that writes 14 MB a minute and holds 3.2 KB
    /// of memory per sample across 823 channels — an hour of driving that no
    /// laptop finishes.
    ///
    /// Nothing is lost by slowing down. A wideband takes 100 ms or more to
    /// respond and the exhaust takes another 50 to 300 to reach it, so the
    /// mixture reading is band-limited to under 10 Hz before it arrives; 25
    /// samples a second is already well clear of it. Raise it for transient work
    /// — accel enrichment, knock, per-cylinder events — where the signal really
    /// does move that fast.
    /// </summary>
    public double MaximumRate { get; init; } = 25;

    /// <summary>
    /// How long to keep trying to get a lost link back before ending the
    /// session.
    ///
    /// Not zero, because the thing that most often ends a link is the key going
    /// off and on again. A session that dies from that loses everything after
    /// it for no better reason than the ECU rebooting, which is a thing ECUs do.
    /// Zero disables it and ends the session at the first loss.
    /// </summary>
    public TimeSpan ReconnectFor { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Wait between attempts to get the link back.</summary>
    public TimeSpan ReconnectEvery { get; init; } = TimeSpan.FromMilliseconds(750);
}

/// <summary>What a session has done so far, for the status line.</summary>
public sealed record LiveSessionStatus(
    int Samples, double Seconds, double Rate, int Retries, int Failures,
    string? Error, bool Reconnecting = false)
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
    private readonly ILiveSource _source;
    private readonly LiveSessionSettings _settings;

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

    /// <summary>
    /// Guards the recorder alone.
    ///
    /// Separate from <c>_gate</c> deliberately. That one is held by every repaint
    /// — the plot asks for a snapshot continuously — and putting a file write
    /// behind it would make drawing wait on the disk. This one is only ever
    /// contended when somebody presses record, which is rare and brief.
    /// </summary>
    private readonly Lock _recorderGate = new();

    private double _recordingFrom;
    private int _recordedRows;

    private int _failures;
    private volatile bool _reconnecting;
    private string? _error;
    private LogDocument? _snapshot;
    private int _snapshotAt = -1;

    /// <summary>
    /// A session over any live source: the poll loop, the pacing, the recording
    /// and the reconnection are the same whatever is on the other end.
    /// </summary>
    public LiveSession(ILiveSource source, LiveSessionSettings? settings = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _settings = settings ?? new LiveSessionSettings();

        _names = [.. source.Names];
        _units = [.. source.Units];
        _digits = [.. source.Digits];
        _columns = [.. _names.Select(_ => new List<float>())];
    }

    /// <summary>A session over the TunerStudio protocol, which is most of them.</summary>
    public LiveSession(
        EcuConnection connection,
        RealtimeDecoder decoder,
        IReadOnlyList<DatalogEntry> datalog,
        LiveSessionSettings? settings = null)
        : this(new TunerStudioSource(connection, decoder, datalog), settings)
    {
    }

    /// <summary>Channels this session records, in order.</summary>
    public IReadOnlyList<string> Names => _names;

    public bool IsRunning => _worker is { IsAlive: true };

    /// <summary>The file being written now, or null when only watching.</summary>
    public string? RecordingPath { get; private set; }

    /// <summary>
    /// The last file written, whether or not it is still open.
    ///
    /// Kept so that stopping a recording does not make the session forget where
    /// it went — the thing somebody wants immediately after pressing stop is to
    /// know what the file was called.
    /// </summary>
    public string? LastRecordingPath { get; private set; }

    public bool IsRecording => _recorder is not null;

    /// <summary>Rows written to the current recording.</summary>
    public int RecordedRows => Volatile.Read(ref _recordedRows);

    /// <summary>Seconds covered by the current recording.</summary>
    public double RecordedSeconds
    {
        get
        {
            lock (_recorderGate)
                return _recorder is null ? 0 : Math.Max(0, _clock.Elapsed.TotalSeconds - _recordingFrom);
        }
    }

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
                    _source.Retries, _failures, _error, _reconnecting);
            }
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        if (_names.Length == 0) throw new InvalidOperationException("No channels to record.");

        _error = null;
        _failures = 0;
        _source.Open();

        _clock.Restart();

        // Before the poll thread, so the first row is recorded rather than being
        // a race against the writer being ready.
        if (_settings.RecordingPath is { Length: > 0 } path) StartRecording(path);

        _cancel = new CancellationTokenSource();
        _worker = new Thread(() => Poll(_cancel.Token))
        {
            IsBackground = true,
            Name = "ECU poll",
        };

        _worker.Start();
    }

    /// <summary>
    /// Begins writing to a file, whether or not the session is already running.
    ///
    /// Recording is separable from watching on purpose. Somebody connects to see
    /// whether the link works at all, warms the engine, finds the road, and only
    /// then wants a file — and everything before that is noise they would have to
    /// trim out of the front of the log afterwards. Deciding when a run starts is
    /// the difference between a log and a session.
    ///
    /// Replaces a recording already in progress, closing it properly first, so
    /// this cannot silently leave two writers on one session.
    /// </summary>
    /// <returns>The path being written to.</returns>
    public string StartRecording(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        StopRecording();

        string? directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

        // With a byte-order mark, matching the exporter. Excel reads a CSV
        // without one in the system codepage whatever is actually in it, so a
        // channel in °C arrives as Â°C — and a recording nobody can open in the
        // thing they open recordings in is a poor recording. Every reader this
        // application has copes with the mark, and so does anything else that
        // reads UTF-8.
        //
        // AutoFlush so a pulled cable costs at most the row in hand.
        var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true,
        };

        CsvExport.WriteHeader(writer, ["Time", .. _names], ["s", .. _units]);

        lock (_recorderGate)
        {
            _recorder = writer;

            // The recording's clock starts where the recording does, not where
            // the session did. A file that opens at t=418 s because that is when
            // somebody pressed record is not a log of a run — every tool that
            // reads it, this one included, would draw four hundred seconds of
            // nothing before the first sample.
            _recordingFrom = _clock.Elapsed.TotalSeconds;
            _recordedRows = 0;

            RecordingPath = path;
            LastRecordingPath = path;
        }

        return path;
    }

    /// <summary>
    /// Closes the recording, leaving the session running.
    ///
    /// The file is complete at this point rather than at disposal: every row was
    /// flushed as it was written, so what is on disk is already the whole of it.
    /// </summary>
    /// <returns>What was written, or null if nothing was being recorded.</returns>
    public string? StopRecording()
    {
        lock (_recorderGate)
        {
            if (_recorder is not { } writer) return null;

            _recorder = null;

            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Whatever reached disk is what the recording has; there is
                // nothing left to save it with.
            }

            string? path = RecordingPath;
            RecordingPath = null;

            return path;
        }
    }

    /// <summary>
    /// Ends the session. Safe to call after the link has already failed, which
    /// is the usual way it is reached: closing a recorder whose disk is full, or
    /// a port whose device has gone, both throw.
    /// </summary>
    public void Stop()
    {
        try { _cancel?.Cancel(); } catch (ObjectDisposedException) { }

        _worker?.Join(TimeSpan.FromSeconds(2));
        _worker = null;

        StopRecording();
    }

    private void Poll(CancellationToken token)
    {
        var row = new double[_names.Length + 1];
        var pace = new Pacer(_settings.MaximumRate);

        while (!token.IsCancellationRequested)
        {
            try
            {
                double[] values = _source.Read();
                double at = _clock.Elapsed.TotalSeconds;

                Append(at, values);
                _failures = 0;

                Record(at, values, row);
            }
            catch (Exception)
            {
                // Deliberately everything. This runs on a background thread, and
                // an exception that escapes one of those does not fail the
                // session — it terminates the process. A serial port whose
                // device has gone throws whatever it likes: ObjectDisposedException
                // from the closed handle, ArgumentOutOfRangeException from
                // setting a timeout on it, IOException from the read itself.
                // Which one you get depends on where the port was when it died,
                // so none of them can be treated as the signal.
                if (++_failures < _settings.FailuresBeforeStopping) continue;

                if (Recover(token)) continue;

                _error = _settings.ReconnectFor > TimeSpan.Zero
                    ? $"The ECU did not come back within {_settings.ReconnectFor.TotalSeconds:F0} seconds. " +
                      "The session so far is still here and its recording is complete."
                    : "The ECU stopped responding — switched off, or the cable pulled. " +
                      "The session so far is still here and its recording is complete.";

                break;
            }

            Announce();
            pace.WaitForNext(token);
        }

        Announce();
    }

    /// <summary>
    /// Holds the poll loop to a rate.
    ///
    /// Each period is due at a fixed distance from the last, not from whenever
    /// the previous one finished — so the reads a slow sample delays are made up
    /// afterwards and the average rate is the one asked for, even though Windows
    /// wakes a thread on a 15.6 ms tick and no individual period lands exactly.
    /// A long stall resets the schedule instead of being repaid, since a burst
    /// of catch-up reads after the link recovers is samples of nothing.
    /// </summary>
    private struct Pacer(double ratePerSecond)
    {
        private readonly TimeSpan _period = ratePerSecond > 0
            ? TimeSpan.FromSeconds(1 / ratePerSecond)
            : TimeSpan.Zero;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _due;

        public void WaitForNext(CancellationToken token)
        {
            if (_period <= TimeSpan.Zero) return;

            _due += _period;

            TimeSpan now = _clock.Elapsed;
            TimeSpan wait = _due - now;

            // Behind by more than a whole period: the link stalled, or the reads
            // are simply slower than the rate asked for. Either way, start
            // counting again from here.
            if (wait < -_period)
            {
                _due = now;
                return;
            }

            // Cancellation is waited on rather than slept through, so stopping a
            // session at 1 Hz does not take a second to notice.
            if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
        }
    }

    /// <summary>
    /// Tries to get a lost link back, and says whether polling can resume.
    ///
    /// The link has to be closed before it is reopened: a handle whose device
    /// has gone still reports itself open, so reopening alone does nothing and
    /// every read afterwards fails identically. That is also why the failure
    /// looks different the second time — the port is already shut by then, and
    /// throws a different exception on the way out.
    /// </summary>
    private bool Recover(CancellationToken token)
    {
        if (_settings.ReconnectFor <= TimeSpan.Zero) return false;

        _reconnecting = true;
        Announce();

        try
        {
            DateTime deadline = DateTime.UtcNow + _settings.ReconnectFor;

            while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (token.WaitHandle.WaitOne(_settings.ReconnectEvery)) break;

                try
                {
                    // Opening a port proves nothing — the adapter can enumerate
                    // before the ECU behind it is answering — so the source is
                    // expected to prove it with a reading of its own.
                    _source.Recover();

                    _failures = 0;
                    return true;
                }
                catch (Exception)
                {
                    // Still gone. Wait and try again until the window closes.
                }
            }

            return false;
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>Reporting a fault must not become one; a handler that throws is not our problem to inherit.</summary>
    private void Announce()
    {
        try
        {
            Updated?.Invoke(Status);
        }
        catch (Exception)
        {
            // Nothing useful to do, and rethrowing here kills the process.
        }
    }

    /// <summary>
    /// Writes one row, if anything is recording.
    ///
    /// Held under the recorder's own lock for the whole write. Recording can be
    /// stopped from another thread at any moment, and a writer disposed between
    /// the null check and the write would throw on a background thread — which
    /// does not fail the row, it terminates the process.
    ///
    /// Times are relative to where the recording began, so the file reads as a
    /// log of its own run rather than of the session that happened to contain it.
    /// </summary>
    private void Record(double at, double[] values, double[] row)
    {
        lock (_recorderGate)
        {
            if (_recorder is not { } recorder) return;

            row[0] = at - _recordingFrom;
            values.AsSpan(0, _names.Length).CopyTo(row.AsSpan(1));

            CsvExport.WriteRow(recorder, row);
            _recordedRows++;
        }
    }

    private void Append(double seconds, double[] values)
    {
        lock (_gate)
        {
            _time.Add(seconds);
            for (int i = 0; i < _columns.Length; i++) _columns[i].Add((float)values[i]);

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
                // The last recording rather than the current one, so stopping a
                // recording does not rename the document out from under the view.
                FilePath = RecordingPath ?? LastRecordingPath ?? "",
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

        try { _source.Dispose(); } catch (Exception) { /* the link may already be gone */ }
    }
}
