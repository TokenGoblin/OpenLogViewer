using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
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

    public InsightsWindow(Func<LogDocument?> log)
    {
        _log = log;

        InitializeComponent();
        DataContext = this;

        Refresh();
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

        IReadOnlyList<LogInsight> found = LogInsights.From(log);
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

        Subheading =
            $"Measured from {log.SampleCount:N0} samples over {minutes:0.#} minutes. "
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
