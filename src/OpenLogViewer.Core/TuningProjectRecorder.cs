namespace OpenLogViewer.Core;

/// <summary>
/// Turning a log into a sitting in a project.
///
/// <para>
/// Separate from the store so that the thing which knows about insights does not
/// also have to know about files, and so this can be used from anywhere a log
/// exists — the window, a command line, or an agent asking for a log to be
/// recorded without opening it.
/// </para>
/// </summary>
public static class TuningProjectRecorder
{
    /// <summary>
    /// The sitting a log amounts to.
    ///
    /// Every finding is kept, not only the ones that complain. A run where
    /// nothing was wrong is evidence too — it is what a fix is verified against,
    /// and a project that records only bad days cannot show anything getting
    /// better.
    /// </summary>
    public static ProjectSession Sitting(
        LogDocument log, string signature = "", string note = "", string version = "")
    {
        ArgumentNullException.ThrowIfNull(log);

        return new ProjectSession
        {
            Log = log.FilePath is { Length: > 0 } path ? Path.GetFileName(path) : "",
            Signature = signature,
            Samples = log.Time.Length,
            Seconds = log.Time.Length > 0 ? log.Time.At(log.Time.Length - 1) : 0,
            Note = note,
            Version = version,
            Findings =
            [
                .. LogInsights.From(log).Select(i =>
                    new SessionFinding(i.Level.ToString(), i.Topic, i.Title) { Detail = i.Detail }),
            ],
        };
    }

    /// <summary>
    /// Records a sitting and raises a fix for anything warned about that is not
    /// already being tracked.
    ///
    /// <para>
    /// Matched on the finding's topic rather than on its wording, because the
    /// wording carries the numbers — "lean on 82 samples, worst 46.8 % short" —
    /// and those move every run while the problem stays the same. Matching on
    /// them would raise a fresh fix for the same fault every single time, which
    /// is how a tracker becomes noise nobody reads.
    /// </para>
    /// <para>
    /// Only warnings raise a fix. Something merely worth watching is recorded in
    /// the sitting and left there; a tracker that opens an item for every
    /// observation is a tracker that hides the three things actually wrong.
    /// </para>
    /// </summary>
    public static TuningProject Record(TuningProject project, ProjectSession sitting)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sitting);

        TuningProject updated = project.With(sitting);

        foreach (SessionFinding finding in sitting.Findings.Where(f => f.Level == "Warning"))
        {
            if (Tracking(updated, finding.Topic) is { } already)
            {
                // Seen again. Worth noting against the fix rather than raising a
                // second one — and worth noting on an applied fix especially,
                // because that is the evidence the change did not work.
                updated = updated.With(already with
                {
                    Evidence = [.. already.Evidence, $"{sitting.At:yyyy-MM-dd}: {finding.Title}"],
                });

                continue;
            }

            string id = updated.NewId(finding.Topic);

            updated = updated.With(new TuningFix
            {
                Id = id,
                Title = finding.Title,
                Detail = finding.Detail,
                State = FixState.Open,
                Raised = sitting.At,
                Evidence = [$"{sitting.At:yyyy-MM-dd}: first seen in {sitting.Log}"],
            });
        }

        return updated;
    }

    /// <summary>An open fix already covering this topic, if there is one.</summary>
    private static TuningFix? Tracking(TuningProject project, string topic) =>
        project.Open.FirstOrDefault(
            f => f.Id.StartsWith(Slug(topic), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the evidence now says a fix worked.
    ///
    /// <para>
    /// This is what the version on a sitting is for, and the question everybody
    /// actually asks. A fix is only answered by a log recorded <em>after</em> the
    /// change and <em>on the tune that carried it</em>: a run on the old version
    /// that happens to look clean proves nothing, and neither does a run on the
    /// new one from before it was written.
    /// </para>
    /// <para>
    /// It reports rather than decides. Moving a fix to verified is somebody's
    /// judgement — one clean run is not always enough, and this cannot know
    /// whether the drive exercised the part of the map in question. What it can
    /// do is say plainly what the record supports, which is the half that is
    /// otherwise reconstructed from memory.
    /// </para>
    /// </summary>
    public static string Verdict(TuningProject project, TuningFix fix)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(fix);

        // The version that claimed to address it, if any did.
        TuneVersion? change = project.Versions
            .LastOrDefault(v => v.Addresses.Contains(fix.Id, StringComparer.OrdinalIgnoreCase));

        if (change is null)
        {
            return fix.State == FixState.Applied
                ? "A change was recorded against this, but no tune version claims it — so there "
                  + "is nothing to say which logs came after it."
                : "Nothing has been changed for this yet.";
        }

        // Sittings on that version or later. Ordering by the version's own
        // position rather than by date, because a log can be imported long
        // after it was recorded.
        int madeAt = project.Versions.ToList().FindIndex(v => v.Id == change.Id);

        var after = project.Sessions
            .Where(s => s.Version.Length > 0)
            .Where(s => project.Versions.ToList().FindIndex(v => v.Id == s.Version) >= madeAt)
            .ToList();

        if (after.Count == 0)
            return $"{change.Id} was made for this and nothing has been recorded on it yet.";

        string topic = Slug(fix.Id);

        var complaining = after
            .Where(s => s.Findings.Any(f =>
                f.Level == "Warning" && Slug(f.Topic).StartsWith(topic, StringComparison.Ordinal)))
            .ToList();

        if (complaining.Count == 0)
        {
            return $"{after.Count} sitting{(after.Count == 1 ? "" : "s")} on {change.Id} or later, "
                   + "and none of them complained about this. That is what verified would rest on.";
        }

        return $"Still seen on {complaining.Count} of {after.Count} "
               + $"sitting{(after.Count == 1 ? "" : "s")} since {change.Id}. "
               + "The change has not settled it.";
    }

    private static string Slug(string topic)
    {
        var slug = new System.Text.StringBuilder();

        foreach (char c in topic ?? "")
        {
            if (char.IsLetterOrDigit(c)) slug.Append(char.ToLowerInvariant(c));
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');

            if (slug.Length >= 32) break;
        }

        return slug.ToString().Trim('-');
    }
}
