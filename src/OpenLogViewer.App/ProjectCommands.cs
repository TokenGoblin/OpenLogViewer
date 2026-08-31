using System.IO;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// The vehicle's project: what is wrong with the tune, what was tried, and what
/// happened.
/// </summary>
public partial class MainViewModel
{
    private TuningProject? _project;

    /// <summary>Where projects live, under the workspace.</summary>
    public TuningProjectStore Projects => new(Path.Combine(Workspace.Root, "Projects"));

    public TuningProject? Project => _project;

    public bool HasProject => _project is not null;

    public string ProjectSummary =>
        _project is not { } project
            ? "No project open. One keeps track of what is wrong with this tune and what has been tried."
            : $"{project.Vehicle} — {project.Sessions.Count} sitting"
              + $"{(project.Sessions.Count == 1 ? "" : "s")}, "
              + $"{project.Open.Count()} open fix{(project.Open.Count() == 1 ? "" : "es")}.";

    /// <summary>Opens a vehicle's project, making one if there is none.</summary>
    public string OpenProject(string vehicle)
    {
        if (string.IsNullOrWhiteSpace(vehicle)) return "A project needs a name for the vehicle.";

        try
        {
            _project = Projects.Read(vehicle) ?? new TuningProject
            {
                Vehicle = vehicle.Trim(),
                Signature = LiveSignature,
            };

            // The firmware is worth keeping current: a project opened against
            // the wrong car is otherwise only noticed by the numbers looking odd.
            if (LiveSignature.Length > 0 && _project.Signature != LiveSignature)
                _project = _project with { Signature = LiveSignature };

            Projects.Write(_project);

            Raise(nameof(Project));
            Raise(nameof(HasProject));
            Raise(nameof(ProjectSummary));

            return $"Project open: {ProjectSummary}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"That project could not be opened: {e.Message}";
        }
    }

    public void CloseProject()
    {
        _project = null;

        Raise(nameof(Project));
        Raise(nameof(HasProject));
        Raise(nameof(ProjectSummary));
    }

    /// <summary>Writes whatever the project now holds.</summary>
    private string Keep(TuningProject project)
    {
        _project = project;

        try
        {
            Projects.Write(project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"The project could not be saved: {e.Message}";
        }

        Raise(nameof(Project));
        Raise(nameof(ProjectSummary));

        return "";
    }

    /// <summary>The folder holding this project's file and its tunes.</summary>
    private string ProjectFolder =>
        Path.GetDirectoryName(Projects.PathFor(_project?.Vehicle ?? "")) ?? Projects.Root;

    /// <summary>
    /// Keeps the tune as a version because it has just been burned.
    ///
    /// <para>
    /// A burn is the natural moment to record one: it is the point at which a
    /// change stops being something in working memory and becomes what the
    /// controller will run tomorrow. Left to a button nobody presses, a project
    /// ends up with a history full of holes exactly where the important changes
    /// were.
    /// </para>
    /// <para>
    /// Silent when there is no project — this must never turn a successful burn
    /// into an error message about bookkeeping. It reports through the hint, so
    /// somebody who has a project open sees the version it was given.
    /// </para>
    /// </summary>
    private void KeepBurnedTune()
    {
        if (_project is null || _ecuTune is null || TuneIsPlaceholder) return;

        try
        {
            string said = KeepTune("burned to flash", burned: true);

            if (said.StartsWith("Kept", StringComparison.Ordinal)) Hint = said;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The burn happened. Failing to write it down is not a reason to
            // tell somebody their burn went wrong.
            Hint = $"Burned, but the project could not record it: {e.Message}";
        }
    }

    /// <summary>
    /// Keeps the tune in hand as a version of the project.
    ///
    /// The other half of a sitting: a sitting says what the log showed, a
    /// version says what the controller was running while it did. Without both,
    /// a finding belongs to a date rather than to a tune.
    /// </summary>
    public string KeepTune(string note = "", bool burned = false)
    {
        if (_project is not { } project) return "No project is open.";

        if (_ecuTune is not { } tune || TuneIsPlaceholder)
        {
            return "There is no tune to keep. Connect to an ECU and read its tune, or open a "
                   + "saved one.";
        }

        (TuningProject updated, TuneVersion version, bool isNew) = TuneHistory.Capture(
            project, ProjectFolder, tune, _ecuSignature, note, burned: burned);

        if (Keep(updated) is { Length: > 0 } failed) return failed;

        return isNew
            ? $"Kept as {version.Id}. {updated.Versions.Count} version"
              + $"{(updated.Versions.Count == 1 ? "" : "s")} in this project."
            : $"That is already {version.Id} — the tune has not changed since it was kept.";
    }

    /// <summary>
    /// Records the log in hand as a sitting.
    ///
    /// The insights are run here rather than taken from the window, so this is
    /// the same answer whether the Insights pane was ever opened or not.
    /// </summary>
    public string RecordSitting(string note = "")
    {
        if (_project is not { } project) return "No project is open.";
        if (Document is not { } log) return "There is no log to record.";

        // Against the tune the controller is running, where one is known. This
        // is the join: a finding belongs to a tune rather than to a date, and
        // without it "still lean" and "lean again after the change" read the
        // same.
        ProjectSession sitting = TuningProjectRecorder.Sitting(
            log, LiveSignature, note, project.Latest?.Id ?? "");
        int before = project.Open.Count();

        TuningProject updated = TuningProjectRecorder.Record(project, sitting);

        if (Keep(updated) is { Length: > 0 } failed) return failed;

        int raised = updated.Open.Count() - before;

        return $"Recorded {sitting.Findings.Count} findings from {sitting.Log}"
               + (raised > 0 ? $", raising {raised} new fix{(raised == 1 ? "" : "es")}." : ".")
               + $" {updated.Open.Count()} open in total.";
    }

    // ----- what the agent bridge calls ---------------------------------------

    internal string ProjectBrief() =>
        _project is { } project ? TuningProjectStore.Brief(project) : "";

    internal IReadOnlyList<string> ProjectNames() => Projects.Vehicles();

    internal AgentRefusal? AgentKeepTune(string note)
    {
        if (_project is null) return new AgentRefusal("no project is open");

        string said = KeepTune(note);

        return said.StartsWith("Kept", StringComparison.Ordinal)
               || said.Contains("already", StringComparison.OrdinalIgnoreCase)
            ? null
            : new AgentRefusal("the tune was not kept", said);
    }

    /// <summary>
    /// What two versions disagree about, in words.
    ///
    /// Rendered here rather than handed over as a shape, because what an agent
    /// wants from a diff is the same thing a person does: which settings moved
    /// and by how much, in the firmware's own units.
    /// </summary>
    /// <summary>
    /// What two versions disagree about, in words — the same answer the window
    /// and an assistant both get, deliberately.
    /// </summary>
    public string CompareVersions(string from, string to)
    {
        if (_project is not { } project) return "No project is open.";
        if (_tuneLayout is not { } layout)
            return "No firmware definition is loaded to read them through.";

        if (TuneHistory.Compare(project, ProjectFolder, from, to, layout) is not { } diff)
            return $"One of {from} and {to} is not a version of this project, or its file has gone.";

        if (diff.IsEmpty) return diff.Summary;

        var text = new System.Text.StringBuilder(diff.Summary);
        text.AppendLine();

        foreach (TuneDifference d in diff.Differences.Take(200))
            text.Append("- ").AppendLine(d.Summary);

        if (diff.Differences.Count > 200)
            text.Append("…and ").Append(diff.Differences.Count - 200).AppendLine(" more.");

        return text.ToString();
    }

    internal AgentRefusal? AgentRecordSitting(string note)
    {
        if (_project is null)
        {
            return new AgentRefusal(
                "no project is open",
                "Open one for this vehicle first, so there is somewhere to record it.");
        }

        if (Document is null) return new AgentRefusal("there is no log to record");

        string said = RecordSitting(note);

        return said.StartsWith("Recorded", StringComparison.Ordinal)
            ? null
            : new AgentRefusal("the sitting was not recorded", said);
    }

    /// <summary>
    /// Adds or moves a fix.
    ///
    /// Left deliberately permissive — it needs no arming, because changing the
    /// record of what is being worked on cannot hurt an engine. The gate that
    /// matters is on writing to the ECU, and this is not that.
    /// </summary>
    internal AgentRefusal? AgentNoteFix(
        string id, string title, string detail, string state, string change)
    {
        if (_project is not { } project) return new AgentRefusal("no project is open");

        if (!Enum.TryParse(state, ignoreCase: true, out FixState wanted))
        {
            if (state.Length > 0)
            {
                return new AgentRefusal(
                    "no such state",
                    "Use open, applied, verified or abandoned.");
            }

            wanted = FixState.Open;
        }

        TuningFix? existing = id.Length > 0 ? project.Fix(id) : null;

        if (id.Length > 0 && existing is null)
            return new AgentRefusal("no fix by that name", id);

        if (existing is null && title.Length == 0)
            return new AgentRefusal("a new fix needs a title");

        TuningFix fix = existing is null
            ? new TuningFix
            {
                Id = project.NewId(title),
                Title = title,
                Detail = detail,
                State = wanted,
            }
            : existing with
            {
                Title = title.Length > 0 ? title : existing.Title,
                Detail = detail.Length > 0 ? detail : existing.Detail,
                State = wanted,
                Change = change.Length > 0 ? change : existing.Change,

                // What changed and when is the whole record. An update that
                // silently replaces the last note loses the sequence that shows
                // whether anything is improving.
                Evidence = detail.Length > 0 && existing.Detail != detail
                    ? [.. existing.Evidence, $"{DateTimeOffset.Now:yyyy-MM-dd}: {detail}"]
                    : existing.Evidence,
            };

        // Settled once, and not un-settled by a later touch that forgot to say so.
        if (fix.State is FixState.Verified or FixState.Abandoned && fix.Settled is null)
            fix = fix with { Settled = DateTimeOffset.Now };
        else if (fix.IsOpen) fix = fix with { Settled = null };

        return Keep(project.With(fix)) is { Length: > 0 } failed
            ? new AgentRefusal("the project could not be saved", failed)
            : null;
    }
}
