using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// Publishing an AI-authored overview of the open tune and log, and reading back
/// what a person chose to accept from it.
///
/// <para>
/// <b>Nothing here reaches the tune.</b> These four tools move
/// <see cref="MainViewModel.OverviewFindings"/> and nothing else — the same
/// separation <see cref="TuneFileTools.PlanRestore"/> keeps between proposing and
/// applying. Turning an accepted finding into an actual edit is the tools that
/// already exist and are already tested: <c>open_tune_table</c> /
/// <c>select_cells</c> / <c>edit_table</c> for a table cell, <c>open_settings_page</c>
/// / <c>set_setting</c> for a setting. Reaching a controller after that still needs
/// the write tools in <see cref="EcuWriteTools"/>, confirmed by a person exactly as
/// it always has been.
/// </para>
///
/// <para>
/// The loop this exists for: an agent reads the tune and log with the tools that
/// already exist, calls <see cref="PushOverviewReport"/> with what it found — which
/// opens the Overview window, so a diagnosis never happens invisibly — a person ticks
/// the changes they want, and the agent calls <see cref="GetOverviewSelections"/> to
/// see exactly what was picked before applying it and publishing the next revision.
/// </para>
/// </summary>
[McpServerToolType]
public static class OverviewTools
{
    [McpServerTool]
    [Description(
        "Publishes an AI-authored overview of the open tune and log: a headline, a paragraph of "
        + "summary, and a list of findings. Replaces whatever overview was there before — this is "
        + "not a merge — and opens the Overview window so a person can see it. Each finding is "
        + "{level: Warning/Watch/Note/Good, topic, title, detail, evidence (optional), change "
        + "(optional)}. A change is {kind: 'table_cell' or 'setting', tableName, column, row — for "
        + "table_cell; pageName, fieldLabel — for setting; currentValue, proposedValue, both as they "
        + "would be typed}. A finding with no change is shown as an observation with nothing to "
        + "apply. This does not touch the tune — see get_overview_selections.")]
    public static Task<object> PushOverviewReport(
        [Description("One line, e.g. 'Lean cruise, one timing cell out of trend'.")]
        string headline,
        [Description("A paragraph or two explaining what was found and why it matters.")]
        string summary,
        [Description("The findings. An empty list publishes a report with a headline and no findings.")]
        List<OverviewFindingInput> findings,
        MainViewModel vm,
        IWindowSource windows,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            var built = new List<OverviewFinding>(findings.Count);
            var warnings = new List<string>();

            foreach (OverviewFindingInput input in findings)
            {
                if (!Enum.TryParse(input.Level, ignoreCase: true, out InsightLevel level))
                {
                    return new
                    {
                        published = false,
                        reason = $"'{input.Level}' is not a level. Use Warning, Watch, Note or Good.",
                    };
                }

                OverviewChange? change = BuildChange(input.Change, input.Title, warnings);

                built.Add(new OverviewFinding(
                    Guid.NewGuid().ToString("N"),
                    level,
                    input.Topic,
                    input.Title,
                    input.Detail,
                    input.Evidence,
                    change));
            }

            vm.PublishOverview(headline, summary, built);

            // Best-effort: a headless test has no window, and the report still
            // needs to land on the view model for get_overview_report to answer.
            windows.Window?.ShowOverview();

            return new
            {
                published = true,
                revision = vm.OverviewRevision,
                findings = built.Count,
                withChanges = built.Count(f => f.HasChange),
                warnings,
            };
        });

    [McpServerTool]
    [Description("The overview as it stands, without generating anything new.")]
    public static Task<object> GetOverviewReport(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.HasOverview)
                return new { read = false, reason = "No overview has been published. Call push_overview_report." };

            return new
            {
                read = true,
                revision = vm.OverviewRevision,
                headline = vm.OverviewHeadline,
                summary = vm.OverviewSummary,
                findings = vm.OverviewFindings.Select(Describe).ToArray(),
            };
        });

    [McpServerTool]
    [Description(
        "Which findings a person has ticked to apply, with the change each one carries. This is how "
        + "an agent learns what was picked; it does not itself change the tune — apply each one with "
        + "open_tune_table/select_cells/edit_table or open_settings_page/set_setting, then call "
        + "push_overview_report again with the next revision.")]
    public static Task<object> GetOverviewSelections(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.HasOverview)
                return new { read = false, reason = "No overview has been published. Call push_overview_report." };

            OverviewFinding[] accepted = [.. vm.OverviewFindings.Where(f => f.Accepted)];

            return new
            {
                read = true,
                revision = vm.OverviewRevision,
                totalFindings = vm.OverviewFindings.Count,
                acceptedCount = accepted.Length,
                accepted = accepted.Select(Describe).ToArray(),
            };
        });

    [McpServerTool]
    [Description("Clears the overview, so a stale report is not left on screen while the next one is prepared.")]
    public static Task<object> ClearOverview(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            vm.ClearOverview();

            return new { cleared = true };
        });

    /// <summary>
    /// Builds a change from what was sent, or null with a warning appended — never a
    /// thrown exception, because a malformed change should not cost the rest of the
    /// findings in the same push.
    /// </summary>
    private static OverviewChange? BuildChange(
        OverviewChangeInput? input, string findingTitle, List<string> warnings)
    {
        if (input is null) return null;

        switch (input.Kind)
        {
            case OverviewChange.TableCellKind when input.TableName.Length > 0 && input.Column >= 0 && input.Row >= 0:
                return new OverviewChange(
                    OverviewChange.TableCellKind, input.TableName, input.Column, input.Row,
                    "", "", input.CurrentValue, input.ProposedValue);

            case OverviewChange.SettingKind when input.PageName.Length > 0 && input.FieldLabel.Length > 0:
                return new OverviewChange(
                    OverviewChange.SettingKind, "", -1, -1,
                    input.PageName, input.FieldLabel, input.CurrentValue, input.ProposedValue);

            default:
                warnings.Add(
                    $"'{findingTitle}': change kind '{input.Kind}' was missing what it needs, so it "
                    + "was kept as an observation with nothing to apply.");
                return null;
        }
    }

    private static object Describe(OverviewFinding finding) => new
    {
        id = finding.Id,
        level = finding.Level.ToString(),
        topic = finding.Topic,
        title = finding.Title,
        detail = finding.Detail,
        evidence = finding.Evidence,
        accepted = finding.Accepted,
        change = finding.Change is not { } change
            ? null
            : (object)new
            {
                kind = change.Kind,
                tableName = change.TableName,
                column = change.Column,
                row = change.Row,
                pageName = change.PageName,
                fieldLabel = change.FieldLabel,
                currentValue = change.CurrentValue,
                proposedValue = change.ProposedValue,
            },
    };
}

/// <summary>One finding as an agent sends it to <see cref="OverviewTools.PushOverviewReport"/>.</summary>
public sealed record OverviewFindingInput(
    string Level,
    string Topic,
    string Title,
    string Detail,
    string Evidence = "",
    OverviewChangeInput? Change = null);

/// <summary>The proposed change part of an <see cref="OverviewFindingInput"/>, if it has one.</summary>
public sealed record OverviewChangeInput(
    string Kind = "",
    string TableName = "",
    int Column = -1,
    int Row = -1,
    string PageName = "",
    string FieldLabel = "",
    string CurrentValue = "",
    string ProposedValue = "");
