using System.Globalization;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Starting and stopping a recording without ending the session.
///
/// These were the same act until now: a session recorded from connect to
/// disconnect and the file was named after the moment you happened to plug in.
/// That is the wrong unit. Somebody connects to check the link works, warms the
/// engine, finds the road, and only then wants a log — everything before that is
/// noise they have to cut off the front afterwards.
///
/// What is easy to get wrong here is not the file handling. It is the two
/// threads: the poll loop writes rows while the interface starts and stops the
/// writer underneath it, and a writer disposed between a null check and a write
/// throws on a background thread, which does not lose a row — it takes the
/// process down.
/// </summary>
public class LiveRecordingTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    private string TempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-rec-{Guid.NewGuid():N}.csv");
        _temp.Add(path);

        return path;
    }

    /// <summary>A source that answers instantly, so tests are not timing exercises.</summary>
    private sealed class Ticker : ILiveSource
    {
        private int _n;

        public IReadOnlyList<string> Names { get; } = ["RPM", "MAP"];

        public IReadOnlyList<string> Units { get; } = ["rpm", "kPa"];

        public IReadOnlyList<int> Digits { get; } = [0, 1];

        public int Retries => 0;

        public void Open() { }

        public double[] Read()
        {
            int n = Interlocked.Increment(ref _n);

            return [1000 + n, 50 + (n % 10)];
        }

        public void Recover() { }

        public void Dispose() { }
    }

    private static LiveSession Session(LiveSessionSettings? settings = null) =>
        new(new Ticker(), settings ?? new LiveSessionSettings { MaximumRate = 200 });

    private static void Until(LiveSession session, int samples)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.Status.Samples < samples && DateTime.UtcNow < deadline) Thread.Sleep(5);
    }

    private static void UntilRecorded(LiveSession session, int rows)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.RecordedRows < rows && DateTime.UtcNow < deadline) Thread.Sleep(5);
    }

    private static string[] Rows(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // ----- watching without writing -------------------------------------------

    /// <summary>
    /// A session with no path given watches and writes nothing. This is what the
    /// "record on connect" preference turns off, and it has to be genuinely off
    /// rather than writing somewhere nobody asked for.
    /// </summary>
    [Fact]
    public void ASessionCanRunWithoutRecordingAnything()
    {
        using LiveSession session = Session();
        session.Start();
        Until(session, 5);

        Assert.False(session.IsRecording);
        Assert.Null(session.RecordingPath);
        Assert.True(session.Status.Samples >= 5, "the session should still be collecting");
    }

    /// <summary>
    /// The whole point: the data keeps arriving whether or not any of it is being
    /// written, so the gauges and the plot do not depend on recording.
    /// </summary>
    [Fact]
    public void StoppingARecordingLeavesTheSessionRunning()
    {
        string path = TempFile();

        using LiveSession session = Session(
            new LiveSessionSettings { RecordingPath = path, MaximumRate = 200 });

        session.Start();
        UntilRecorded(session, 5);

        int atStop = session.Status.Samples;
        Assert.Equal(path, session.StopRecording());
        Assert.False(session.IsRecording);

        Until(session, atStop + 5);

        Assert.True(session.IsRunning, "the session should outlive the recording");
        Assert.True(session.Status.Samples > atStop, "samples should keep arriving");
    }

    // ----- starting mid-session -----------------------------------------------

    [Fact]
    public void RecordingCanBeStartedAfterTheSessionHasBeenRunningAWhile()
    {
        using LiveSession session = Session();
        session.Start();
        Until(session, 20);

        string path = TempFile();
        session.StartRecording(path);

        Assert.True(session.IsRecording);
        Assert.Equal(path, session.RecordingPath);

        UntilRecorded(session, 5);
        session.StopRecording();

        // Header, units, then rows.
        Assert.True(Rows(path).Length >= 7, "the recording should hold what arrived after it began");
    }

    /// <summary>
    /// The recording's clock starts where the recording does.
    ///
    /// A file that opens at t=418 s because that is when somebody pressed record
    /// is not a log of a run. Every tool that reads it, this one included, draws
    /// seven minutes of nothing before the first sample — and the person who
    /// pressed record at the start line gets a plot whose interesting part is a
    /// sliver at the right-hand edge.
    /// </summary>
    [Fact]
    public void TheRecordingsClockStartsWhereTheRecordingDoes()
    {
        using LiveSession session = Session();
        session.Start();

        // Let real time pass, so a session-relative timestamp would be visibly
        // non-zero rather than merely different.
        Thread.Sleep(400);
        Until(session, 20);

        string path = TempFile();
        session.StartRecording(path);
        UntilRecorded(session, 5);
        session.StopRecording();

        string[] rows = Rows(path);
        double first = double.Parse(rows[2].Split(',')[0], CultureInfo.InvariantCulture);

        Assert.True(first < 0.25, $"the first row should be near zero, was {first:F3} s");

        double last = double.Parse(rows[^1].Split(',')[0], CultureInfo.InvariantCulture);
        Assert.True(last >= first, "time should advance through the recording");
    }

    /// <summary>Only what arrived after record was pressed. The buffer is not flushed into it.</summary>
    [Fact]
    public void ARecordingHoldsWhatCameAfterItStarted()
    {
        using LiveSession session = Session();
        session.Start();
        Until(session, 40);

        string path = TempFile();
        session.StartRecording(path);
        UntilRecorded(session, 5);

        int recorded = session.RecordedRows;
        session.StopRecording();

        Assert.True(
            recorded < session.Status.Samples,
            "the recording should be shorter than the session that contains it");
    }

    // ----- more than one recording --------------------------------------------

    [Fact]
    public void TwoRecordingsFromOneSessionAreTwoSeparateFiles()
    {
        using LiveSession session = Session();
        session.Start();

        string first = TempFile();
        session.StartRecording(first);
        UntilRecorded(session, 4);
        session.StopRecording();

        string second = TempFile();
        session.StartRecording(second);
        UntilRecorded(session, 4);
        session.StopRecording();

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));

        // Each carries its own header and its own clock, so each opens as a log.
        foreach (string path in (string[])[first, second])
        {
            string[] rows = Rows(path);

            Assert.StartsWith("Time,", rows[0], StringComparison.Ordinal);
            Assert.True(
                double.Parse(rows[2].Split(',')[0], CultureInfo.InvariantCulture) < 0.25,
                $"{Path.GetFileName(path)} should start near zero");
        }
    }

    /// <summary>
    /// Starting a second recording without stopping the first closes the first
    /// properly rather than abandoning its handle. An abandoned writer is a file
    /// that stays locked and short.
    /// </summary>
    [Fact]
    public void StartingASecondRecordingClosesTheFirst()
    {
        using LiveSession session = Session();
        session.Start();

        string first = TempFile();
        session.StartRecording(first);
        UntilRecorded(session, 4);

        string second = TempFile();
        session.StartRecording(second);
        UntilRecorded(session, 4);

        Assert.Equal(second, session.RecordingPath);

        // Readable and complete while the session is still running, which it
        // would not be if the handle had merely been dropped.
        Assert.True(Rows(first).Length >= 3);

        session.StopRecording();
    }

    [Fact]
    public void StoppingWhenNothingIsRecordingIsNotAnError()
    {
        using LiveSession session = Session();
        session.Start();
        Until(session, 3);

        Assert.Null(session.StopRecording());
        Assert.Null(session.StopRecording());
    }

    /// <summary>
    /// After stopping, the session still knows where the file went — which is the
    /// one thing somebody wants to know immediately after pressing stop.
    /// </summary>
    [Fact]
    public void TheSessionRemembersWhereTheRecordingWent()
    {
        string path = TempFile();

        using LiveSession session = Session();
        session.Start();
        session.StartRecording(path);
        UntilRecorded(session, 3);
        session.StopRecording();

        Assert.Null(session.RecordingPath);
        Assert.Equal(path, session.LastRecordingPath);
    }

    // ----- the part that would crash the process ------------------------------

    /// <summary>
    /// Recording started and stopped repeatedly while the poll loop is writing.
    ///
    /// The failure this guards against is not a lost row. It is a writer disposed
    /// between the poll thread's null check and its write: that throws on a
    /// background thread, and an exception escaping one of those terminates the
    /// process rather than failing the session.
    /// </summary>
    [Fact]
    public void RecordingCanBeToggledWhileThePollLoopIsWriting()
    {
        using LiveSession session = Session();
        session.Start();
        Until(session, 5);

        for (int i = 0; i < 40; i++)
        {
            session.StartRecording(TempFile());
            Thread.Sleep(2);
            session.StopRecording();
        }

        Until(session, session.Status.Samples + 5);

        Assert.True(session.IsRunning, "the poll loop should have survived the churn");
        Assert.Null(session.Status.Error);
    }

    /// <summary>
    /// Every row a recording claims is a row that is actually in the file. The
    /// count is what the status line reports, so a count that outruns the file
    /// would be the application lying about what it saved.
    /// </summary>
    [Fact]
    public void TheRowCountMatchesWhatIsOnDisk()
    {
        string path = TempFile();

        using LiveSession session = Session();
        session.Start();
        session.StartRecording(path);
        UntilRecorded(session, 25);

        // Counted after stopping, not before. The poll thread is still writing
        // until the recording closes, so a count taken first is stale by however
        // many rows arrive in between — and it is the count after stopping that
        // the interface reports as "saved N rows", so that is the one that has
        // to match the file.
        session.StopRecording();
        int claimed = session.RecordedRows;

        // Two lines of header, then one line per row.
        Assert.Equal(claimed + 2, Rows(path).Length);
    }

    [Fact]
    public void EndingTheSessionClosesTheRecording()
    {
        string path = TempFile();

        using LiveSession session = Session(
            new LiveSessionSettings { RecordingPath = path, MaximumRate = 200 });

        session.Start();
        UntilRecorded(session, 4);
        session.Stop();

        Assert.False(session.IsRecording);

        // Openable for writing, which only holds if the handle was released.
        using var reopened = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.Length > 0);
    }
}
