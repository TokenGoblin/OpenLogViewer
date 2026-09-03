using System.Collections.ObjectModel;

namespace OpenLogViewer.App;

/// <summary>
/// The AI-authored overview of the open tune and log: a headline, a summary, and the
/// findings behind them.
///
/// <para>
/// Kept beside the rest of the view model rather than in it because it is one
/// subject, the same reasoning as <see cref="MainViewModel.PlanRestore"/>'s file. This
/// state exists to be shown, not to be edited by the application — it is written
/// wholesale by <c>push_overview_report</c> over MCP, and the only thing that moves
/// afterwards is <see cref="OverviewFinding.Accepted"/>, from the checkbox in the
/// Overview window.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private string _overviewHeadline = "";
    private string _overviewSummary = "";
    private int _overviewRevision;

    public string OverviewHeadline
    {
        get => _overviewHeadline;
        private set => Set(ref _overviewHeadline, value);
    }

    public string OverviewSummary
    {
        get => _overviewSummary;
        private set => Set(ref _overviewSummary, value);
    }

    /// <summary>
    /// Bumped on every <see cref="PublishOverview"/>, so the window can say which
    /// revision a person is looking at without guessing from the findings alone.
    /// </summary>
    public int OverviewRevision
    {
        get => _overviewRevision;
        private set => Set(ref _overviewRevision, value);
    }

    public ObservableCollection<OverviewFinding> OverviewFindings { get; } = [];

    public bool HasOverview => OverviewFindings.Count > 0;

    public bool NoOverview => !HasOverview;

    /// <summary>
    /// Replaces the overview outright. Not a merge — a revised report says what the
    /// agent concludes now, and a stale finding left over from a fixed problem would
    /// be worse than an empty one.
    /// </summary>
    public void PublishOverview(string headline, string summary, IEnumerable<OverviewFinding> findings)
    {
        OverviewFindings.Clear();

        foreach (OverviewFinding finding in findings) OverviewFindings.Add(finding);

        OverviewHeadline = headline;
        OverviewSummary = summary;
        OverviewRevision++;

        Raise(nameof(HasOverview));
        Raise(nameof(NoOverview));
    }

    public void ClearOverview()
    {
        OverviewFindings.Clear();
        OverviewHeadline = "";
        OverviewSummary = "";
        OverviewRevision = 0;

        Raise(nameof(HasOverview));
        Raise(nameof(NoOverview));
    }
}
