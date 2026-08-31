using System.Collections.ObjectModel;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// One settings dialog, built from the firmware's description of it and the tune
/// it is being applied to.
///
/// <para>
/// Dialogs embed one another. A page of settings is typically a handful of
/// panels, each of which is a dialog in its own right and may hold panels of its
/// own; MS3's throttle page is four deep. They are flattened into one list here
/// rather than nested in the view, because what a person wants is a page of
/// settings and what the file describes is a way of laying one out.
/// </para>
/// <para>
/// A panel may name itself, directly or through a chain, and a definition with a
/// mistake in it would otherwise take the program down rather than fail to draw
/// a dialog. Panels already on the stack are skipped.
/// </para>
/// </summary>
public sealed class SettingsDialog
{
    private readonly List<SettingRow> _rows = [];

    private SettingsDialog(string title, string help)
    {
        Title = title;
        Help = help;
    }

    public string Title { get; }

    public string Help { get; }

    public IReadOnlyList<SettingRow> Rows => _rows;

    /// <summary>Rows currently worth drawing, in order.</summary>
    public ObservableCollection<SettingRow> Visible { get; } = [];

    /// <summary>
    /// Curves this page holds, in the order they appear.
    ///
    /// A firmware puts a curve on a page the same way it puts a group of fields
    /// there — <c>panel = warmup_curve</c> — and the file gives no hint which of
    /// the two a name turns out to be. A panel naming something that is not a
    /// dialog used to be skipped in silence, which is why 14 of a MicroSquirt's
    /// pages and 88 of an MS3's opened with nothing but their help text on them.
    ///
    /// <para>
    /// Filtered by the panel's condition rather than fixed when the page is
    /// built, because a condition is a question about the tune and the tune
    /// moves while the page is open — turning a second fuel table on should
    /// bring its curve with it, and turning it off should take it away before
    /// anybody can send it.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Curves =>
        [.. _curves.Where(c => c.Shown).Select(c => c.Name)];

    private readonly List<(string Name, string Condition, bool Shown)> _curves = [];

    /// <summary>
    /// True when the firmware described something here that this cannot draw —
    /// a live graph or a status lamp. Said out loud rather than left out, so a
    /// partial page is not presented as the whole of it.
    /// </summary>
    public bool IsPartial => _rows.Any(r => r.Kind == SettingKind.NotShown);

    /// <summary>
    /// Builds a dialog, following its panels.
    /// </summary>
    /// <param name="name">The dialog to build.</param>
    /// <param name="ui">Everything the firmware described.</param>
    /// <param name="constants">Looks a constant up by name.</param>
    /// <param name="edit">The edit in progress, or null to show without editing.</param>
    /// <param name="title">
    /// What to head the page with when the dialog does not name itself, which is
    /// common: a dialog reached from a menu is titled by the menu entry, and its
    /// own title is left empty because TunerStudio puts that in the window's
    /// caption instead.
    /// </param>
    /// <param name="curves">
    /// The curves this firmware declares, so that a panel naming one is
    /// recognised as a curve rather than dropped as a dialog that is not there.
    /// </param>
    public static SettingsDialog? Build(
        string name,
        TuneInterface ui,
        Func<string, TuneConstant?> constants,
        TuneSettingsEdit? edit,
        string? title = null,
        IReadOnlySet<string>? curves = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(constants);

        if (ui.Find(name) is not { } root) return null;

        var built = new SettingsDialog(
            root.Title.Length > 0 ? root.Title : title ?? "", root.Help);
        built.Fill(root, ui, constants, edit, [], curves);

        return built;
    }

    private void Fill(
        TuneDialog dialog,
        TuneInterface ui,
        Func<string, TuneConstant?> constants,
        TuneSettingsEdit? edit,
        HashSet<string> visiting,
        IReadOnlySet<string>? curves)
    {
        if (!visiting.Add(dialog.Name)) return;

        foreach (DialogItem item in dialog.Items)
        {
            if (item.Kind == DialogItemKind.Panel)
            {
                // A dialog of that name first, then a curve — the same order
                // the settings menu resolves a name in. Asking the other way
                // round would mean a firmware declaring both got its fields on
                // one route and its plot on the other.
                if (ui.Find(item.Target) is not { } panel)
                {
                    // A panel naming a curve. Noted and carried up rather than
                    // drawn here, because a curve is a plot and these rows are a
                    // list of fields.
                    if (curves is not null && curves.Contains(item.Target)
                        && !_curves.Any(c =>
                               c.Name.Equals(item.Target, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Kept with its condition, which gates a curve exactly as
                        // it gates a field. Dropped, a curve behind
                        // { afrTable2Enabled } is drawn and editable while the
                        // fields beside it are correctly hidden — and can be sent
                        // to an ECU whose firmware has just said it does not
                        // apply. Shown until the first Refresh judges it.
                        _curves.Add((item.Target, item.Condition, true));
                    }

                    continue;
                }

                // A panel carries its own condition, which gates everything it
                // holds. Rather than track that separately, it is pushed onto
                // each row the panel contributes — so hiding a panel hides its
                // contents and nothing else has to know about panels at all.
                int before = _rows.Count;
                Fill(panel, ui, constants, edit, visiting, curves);

                if (item.HasCondition)
                {
                    for (int i = before; i < _rows.Count; i++)
                        _rows[i] = Gated(_rows[i], item.Condition, constants, edit);
                }

                continue;
            }

            _rows.Add(new SettingRow(item, constants(item.TargetConstant), edit));
        }

        visiting.Remove(dialog.Name);
    }

    /// <summary>
    /// The same row, additionally gated by the panel's condition.
    ///
    /// The two are combined with "and", which is what nesting means: a field
    /// inside a hidden panel is hidden whatever its own condition says.
    /// </summary>
    private static SettingRow Gated(
        SettingRow row, string condition, Func<string, TuneConstant?> constants, TuneSettingsEdit? edit)
    {
        DialogItem item = row.Item;

        string combined = item.HasCondition
            ? $"({item.Condition}) && ({condition})"
            : condition;

        return new SettingRow(item with { Condition = combined }, constants(item.TargetConstant), edit);
    }

    /// <summary>
    /// Re-evaluates every condition and rebuilds the visible list.
    ///
    /// Run after any edit, because conditions are written against the tune's own
    /// settings: turning knock detection on is meant to reveal the four fields
    /// that configure it, and it would be strange if it did not until the page
    /// were reopened.
    /// </summary>
    public void Refresh(Func<string, double> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        foreach (SettingRow row in _rows) row.Refresh(lookup);

        // Curves are gated the same way, and re-judged here for the same reason
        // rows are: the condition asks about the tune, and the tune moves.
        for (int i = 0; i < _curves.Count; i++)
        {
            (string name, string condition, _) = _curves[i];

            _curves[i] = (name, condition,
                          condition.Length == 0 || DialogCondition.ShouldShow(condition, lookup));
        }

        Visible.Clear();

        SettingRow? previous = null;

        foreach (SettingRow row in _rows)
        {
            if (!row.IsVisible) continue;
            if (row.Kind == SettingKind.NotShown) continue;

            // A run of blank captions is what is left when a group of settings
            // is hidden, and a page of empty lines reads as a broken one.
            bool blank = row.Kind == SettingKind.Caption && row.Label.Length == 0;
            if (blank && (previous is null || IsBlank(previous))) continue;

            Visible.Add(row);
            previous = row;
        }

        // And the same at the end.
        while (Visible.Count > 0 && IsBlank(Visible[^1])) Visible.RemoveAt(Visible.Count - 1);
    }

    private static bool IsBlank(SettingRow row) =>
        row.Kind == SettingKind.Caption && row.Label.Length == 0;
}
