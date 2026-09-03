using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// One thing an AI agent found while reviewing the open tune and log, published
/// through <c>push_overview_report</c>.
///
/// <para>
/// <see cref="Accepted"/> is the one mutable part — it is what the checkbox in the
/// Overview window moves, and what <c>get_overview_selections</c> reads back. Nothing
/// else here changes after construction; a revised finding is a new one, not an edit
/// to this one.
/// </para>
/// </summary>
public sealed class OverviewFinding(
    string id,
    InsightLevel level,
    string topic,
    string title,
    string detail,
    string evidence,
    OverviewChange? change) : ObservableObject
{
    public string Id { get; } = id;

    public InsightLevel Level { get; } = level;

    public string Topic { get; } = topic;

    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public string Evidence { get; } = evidence;

    public bool HasEvidence => Evidence.Length > 0;

    /// <summary>What this finding would change, or null for an observation with nothing to apply.</summary>
    public OverviewChange? Change { get; } = change;

    public bool HasChange => Change is not null;

    private bool _accepted;

    /// <summary>Whether a person has ticked this finding's proposed change to be applied.</summary>
    public bool Accepted
    {
        get => _accepted;
        set => Set(ref _accepted, value);
    }

    /// <summary>A word rather than a colour alone. Matches <c>InsightItem.Badge</c>.</summary>
    public string Badge => Level switch
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

            Color colour = Level switch
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
/// A single proposed edit an <see cref="OverviewFinding"/> carries: a table cell or a
/// setting, described as it would be typed rather than kept live against
/// <see cref="TuneEdit"/> — this is a description for a person and an agent to act
/// on, not something the application applies itself. See <c>OverviewTools</c>.
/// </summary>
public sealed record OverviewChange(
    string Kind,
    string TableName,
    int Column,
    int Row,
    string PageName,
    string FieldLabel,
    string CurrentValue,
    string ProposedValue)
{
    public const string TableCellKind = "table_cell";
    public const string SettingKind = "setting";

    /// <summary>One line for the checkbox label.</summary>
    public string Description => Kind switch
    {
        TableCellKind => $"{TableName}[{Column},{Row}]  {CurrentValue} → {ProposedValue}",
        SettingKind => $"{PageName} · {FieldLabel}  {CurrentValue} → {ProposedValue}",
        _ => $"{CurrentValue} → {ProposedValue}",
    };
}
