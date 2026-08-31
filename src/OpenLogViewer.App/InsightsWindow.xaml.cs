using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>One finding, dressed for the screen.</summary>
public sealed class InsightItem(LogInsight insight)
{
    public LogInsight Insight { get; } = insight;

    public string Topic => Insight.Topic;

    public string Title => Insight.Title;

    public string Detail => Insight.Detail;

    public string Evidence => Insight.Evidence;

    public bool HasEvidence => Insight.Evidence.Length > 0;

    /// <summary>
    /// A word rather than a colour alone.
    ///
    /// Colour carries the level at a glance, and about one man in twelve cannot
    /// tell the two most important of these apart. The word is what actually
    /// says which it is.
    /// </summary>
    public string Badge => Insight.Level switch
    {
        InsightLevel.Warning => "WARNING",
        InsightLevel.Watch => "WATCH",
        InsightLevel.Note => "NOTE",
        InsightLevel.Good => "GOOD",
        _ => "NOT MEASURED",
    };

    public Brush Accent
    {
        get
        {
            Theme theme = ThemeManager.Current;

            Color colour = Insight.Level switch
            {
                InsightLevel.Warning => theme.Danger,
                InsightLevel.Watch => theme.Warning,
                InsightLevel.Note => theme.Accent,
                InsightLevel.Good => theme.Nominal,
                _ => theme.Muted,
            };

            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            return brush;
        }
    }
}

/// <summary>
/// What the log has to say about the engine, put in front of somebody.
///
/// <para>
/// The findings are ordered worst first and each carries the arithmetic behind
/// it. That last part is the point of the window: a tuner who cannot see why a
/// conclusion was reached has no way to disagree with it, and a tool they cannot
/// disagree with is one they either obey or ignore.
/// </para>
/// </summary>
public partial class InsightsWindow : Window, INotifyPropertyChanged
{
    private readonly Func<LogDocument?> _log;
    private readonly INotifyPropertyChanged? _watching;

    /// <param name="log">The log to measure, read afresh on every refresh.</param>
    /// <param name="watching">
    /// Something that says when the log has changed, so this follows it rather
    /// than waiting to be asked.
    ///
    /// The button remains, because a live session grows without ever replacing
    /// the document — but nobody should have to press it to stop looking at
    /// findings from a log they closed.
    /// </param>
    public InsightsWindow(Func<LogDocument?> log, INotifyPropertyChanged? watching = null)
    {
        _log = log;
        _watching = watching;

        InitializeComponent();
        DataContext = this;

        if (_watching is not null)
        {
            _watching.PropertyChanged += OnSourceChanged;
            Closed += (_, _) => _watching.PropertyChanged -= OnSourceChanged;
        }

        Refresh();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Document)) Soon();
    }

    /// <summary>
    /// How often the findings are worked out again while a session is running.
    ///
    /// <para>
    /// A live session hands over a whole new document about five times a second
    /// — the snapshot is rebuilt whenever the sample count moves — and each one
    /// used to re-measure the entire log on the UI thread: the wideband delay
    /// search across every sample, and fourteen more full passes behind it. On a
    /// long recording that is a large fraction of a second's work, five times a
    /// second, on the thread that draws.
    /// </para>
    /// <para>
    /// Throttled rather than waited out, because a live log never falls quiet.
    /// Something arranged to run once the data stopped arriving would never run
    /// at all. Five seconds is far more often than an engine changes its mind
    /// about anything measured here.
    /// </para>
    /// </summary>
    private static readonly TimeSpan NoOftenerThan = TimeSpan.FromSeconds(5);

    private DateTime _measuredAt = DateTime.MinValue;
    private DispatcherTimer? _due;

    /// <summary>Measures again, but no more often than is any use.</summary>
    private void Soon()
    {
        if (_due is not null) return;

        TimeSpan since = DateTime.UtcNow - _measuredAt;

        if (since >= NoOftenerThan) { Refresh(); return; }

        _due = new DispatcherTimer { Interval = NoOftenerThan - since };
        _due.Tick += (_, _) =>
        {
            _due?.Stop();
            _due = null;
            Refresh();
        };

        _due.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public List<InsightItem> Findings { get; private set; } = [];

    public string Heading { get; private set; } = "";

    public string Subheading { get; private set; } = "";

    public string Tally { get; private set; } = "";

    /// <summary>Measures the log again, which matters while a session is live.</summary>
    public void Refresh()
    {
        LogDocument? log = _log();

        if (log is null || log.SampleCount == 0)
        {
            Findings = [];
            Heading = "No log is open.";
            Subheading = "Open a datalog, or connect to an ECU and record one, and every finding "
                         + "below is measured from it.";
            Tally = "";

            Raise(nameof(Findings));
            Raise(nameof(Heading));
            Raise(nameof(Subheading));
            Raise(nameof(Tally));

            return;
        }

        // Measured off the drawing thread. A snapshot is a fresh, finished
        // document — a live session builds a new one rather than growing the
        // old — so nothing here is being read while something else writes it.
        //
        // Only the newest answer is kept: a run started before this one is
        // stale by the time it lands, and applying it would put older findings
        // on screen than the ones already there.
        long mine = ++_generation;

        Task.Run(() => LogInsights.From(log)).ContinueWith(
            done =>
            {
                if (mine != Interlocked.Read(ref _generation)) return;
                if (done.IsFaulted || done.Result is not { } measured) return;

                Dispatcher.Invoke(() => Apply(log, measured));
            },
            TaskScheduler.Default);
    }

    private long _generation;

    /// <summary>Puts a finished measurement on screen. Runs on the UI thread.</summary>
    private void Apply(LogDocument log, IReadOnlyList<LogInsight> found)
    {
        _measuredAt = DateTime.UtcNow;

        Findings = [.. found.Select(f => new InsightItem(f))];

        double minutes = log.SampleCount > 1
            ? (log.Time.At(log.SampleCount - 1) - log.Time.At(0)) / 60.0
            : 0;

        int warnings = found.Count(f => f.Level == InsightLevel.Warning);
        int watch = found.Count(f => f.Level == InsightLevel.Watch);

        Heading = warnings > 0
            ? $"{warnings} thing{(warnings == 1 ? "" : "s")} worth stopping for."
            : watch > 0
                ? $"Nothing dangerous, {watch} thing{(watch == 1 ? "" : "s")} worth a look."
                : "Nothing here looks wrong.";

        // The log's own name, so a window left open beside a different one is
        // obviously showing the wrong thing rather than quietly showing it.
        string name = Path.GetFileName(log.FilePath);

        Subheading =
            (name.Length > 0 ? $"{name} — " : "")
            + $"measured from {log.SampleCount:N0} samples over {minutes:0.#} minutes. "
            + "Every finding says what it rests on; where a log cannot answer something, it says "
            + "that rather than guessing.";

        Tally = string.Join(
            " · ",
            new[]
            {
                warnings > 0 ? $"{warnings} warning{(warnings == 1 ? "" : "s")}" : "",
                watch > 0 ? $"{watch} to watch" : "",
                $"{found.Count(f => f.Level == InsightLevel.Note)} notes",
                $"{found.Count(f => f.Level == InsightLevel.Good)} good",
                $"{found.Count(f => f.Level == InsightLevel.Unanswered)} not measured",
            }.Where(s => s.Length > 0));

        Raise(nameof(Findings));
        Raise(nameof(Heading));
        Raise(nameof(Subheading));
        Raise(nameof(Tally));
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// Every finding as text, for pasting into the forum thread where somebody
    /// is being asked for help.
    /// </summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();

        text.AppendLine(Heading);
        text.AppendLine(Subheading);
        text.AppendLine();

        foreach (InsightItem item in Findings)
        {
            text.AppendLine($"[{item.Badge}] {item.Topic}: {item.Title}");
            text.AppendLine($"    {item.Detail}");

            if (item.HasEvidence) text.AppendLine($"    ({item.Evidence})");

            text.AppendLine();
        }

        try
        {
            Clipboard.SetText(text.ToString());
            App.Report($"{Findings.Count} findings copied.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process holds the clipboard, which Windows does not queue
            // for. Worth saying rather than looking like it worked.
            App.Report("The clipboard was busy — nothing was copied.");
        }
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
