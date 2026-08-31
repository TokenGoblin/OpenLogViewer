using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Where a vehicle's project lives, and how it reads.
///
/// <para>
/// One folder per vehicle under the workspace, holding <c>project.json</c>. JSON
/// because it has to be written back reliably by a program; prose because that
/// is how it is actually read. <see cref="Brief"/> renders the whole thing as
/// Markdown, which is what an assistant is handed — the same role a scratchpad
/// or a CLAUDE.md plays, and for the same reason: a model that starts each
/// session knowing nothing repeats work and re-diagnoses problems already
/// diagnosed.
/// </para>
/// </summary>
public sealed class TuningProjectStore(string root)
{
    private readonly string _root = root ?? throw new ArgumentNullException(nameof(root));

    /// <summary>Where projects are kept.</summary>
    public string Root => _root;

    /// <summary>Every vehicle with a project, by name.</summary>
    public IReadOnlyList<string> Vehicles()
    {
        if (!Directory.Exists(_root)) return [];

        return
        [
            .. Directory.EnumerateDirectories(_root)
                .Where(d => File.Exists(Path.Combine(d, "project.json")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The file for a vehicle, whether or not it exists yet.</summary>
    public string PathFor(string vehicle) =>
        Path.Combine(_root, Safe(vehicle), "project.json");

    /// <summary>Reads one, or null where there is none.</summary>
    public TuningProject? Read(string vehicle) =>
        JsonSettingsFile.Read<TuningProject>(PathFor(vehicle));

    /// <summary>Writes one, creating its folder.</summary>
    public void Write(TuningProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        string path = PathFor(project.Vehicle);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonSettingsFile.Write(path, project);
    }

    /// <summary>
    /// A folder name from a vehicle name, without the characters Windows
    /// refuses and without a path in it.
    /// </summary>
    private static string Safe(string vehicle)
    {
        if (string.IsNullOrWhiteSpace(vehicle)) return "vehicle";

        var clean = new StringBuilder(vehicle.Length);

        foreach (char c in vehicle)
            clean.Append(Path.GetInvalidFileNameChars().Contains(c) ? '-' : c);

        string name = clean.ToString().Trim().Trim('.');

        return name.Length == 0 ? "vehicle" : name;
    }

    /// <summary>
    /// The project as prose, for whoever has to read it — usually a model.
    ///
    /// <para>
    /// Ordered by what is useful to know first rather than by what happened
    /// first: what the car is, what is still wrong, what was tried, and only
    /// then the history. A reader who stops after the first screen should still
    /// have the part that changes what they do next.
    /// </para>
    /// <para>
    /// Sessions are trimmed to the most recent few. The whole history is in the
    /// file and can be asked for; putting forty sittings in front of a model
    /// buries the three fixes that are actually open.
    /// </para>
    /// </summary>
    public static string Brief(TuningProject project, int sessions = 5)
    {
        ArgumentNullException.ThrowIfNull(project);

        var text = new StringBuilder();

        text.Append("# ").AppendLine(project.Vehicle);
        text.AppendLine();

        if (project.Engine.Length > 0) text.Append("**Engine** ").AppendLine(project.Engine);
        if (project.Signature.Length > 0) text.Append("**Firmware** ").AppendLine(project.Signature);

        text.Append("**Started** ").AppendLine(project.Started.ToString("yyyy-MM-dd"));
        text.Append("**Sittings** ").Append(project.Sessions.Count)
            .Append("   **Open fixes** ").AppendLine(project.Open.Count().ToString());

        if (project.Notes.Length > 0)
        {
            text.AppendLine();
            text.AppendLine(project.Notes);
        }

        // What is still wrong, first, because it is what the next change should
        // be about.
        TuningFix[] open = [.. project.Open.OrderBy(f => f.Raised)];

        text.AppendLine();
        text.AppendLine("## Still open");
        text.AppendLine();

        if (open.Length == 0)
        {
            text.AppendLine("Nothing outstanding.");
        }
        else
        {
            foreach (TuningFix fix in open) Describe(text, fix);
        }

        TuningFix[] settled = [.. project.Fixes.Where(f => !f.IsOpen).OrderByDescending(f => f.Settled)];

        if (settled.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Settled");
            text.AppendLine();

            foreach (TuningFix fix in settled)
            {
                text.Append("- **").Append(fix.Id).Append("** — ").Append(fix.Title)
                    .Append(" (").Append(fix.State.ToString().ToLowerInvariant()).Append(')');

                if (fix.Change.Length > 0) text.Append(". ").Append(fix.Change);

                text.AppendLine();
            }
        }

        text.AppendLine();
        text.AppendLine("## Recent sittings");
        text.AppendLine();

        if (project.Sessions.Count == 0)
        {
            text.AppendLine("None recorded.");
            return text.ToString();
        }

        foreach (ProjectSession sitting in project.Sessions.TakeLast(sessions).Reverse())
        {
            text.Append("### ").Append(sitting.At.ToString("yyyy-MM-dd HH:mm"));

            if (sitting.Log.Length > 0) text.Append(" — ").Append(sitting.Log);

            text.AppendLine();
            text.Append(sitting.Samples.ToString("N0")).Append(" samples over ")
                .Append(sitting.Seconds.ToString("N0")).Append(" s");

            if (sitting.Signature.Length > 0) text.Append(", ").Append(sitting.Signature);

            text.AppendLine(".");

            if (sitting.Note.Length > 0)
            {
                text.AppendLine();
                text.AppendLine(sitting.Note);
            }

            // Only what asked for attention. A sitting where everything was fine
            // is worth one line saying so, not thirteen.
            SessionFinding[] worth =
                [.. sitting.Findings.Where(f => f.Level is "Warning" or "Watch")];

            text.AppendLine();

            if (worth.Length == 0)
            {
                text.AppendLine("Nothing flagged.");
            }
            else
            {
                foreach (SessionFinding finding in worth)
                {
                    text.Append("- **").Append(finding.Level).Append("** ")
                        .Append(finding.Topic).Append(" — ").AppendLine(finding.Title);
                }
            }

            text.AppendLine();
        }

        if (project.Sessions.Count > sessions)
        {
            text.Append('*').Append(project.Sessions.Count - sessions)
                .AppendLine(" earlier sittings are in the file.*");
        }

        return text.ToString();
    }

    private static void Describe(StringBuilder text, TuningFix fix)
    {
        text.Append("### ").Append(fix.Id).Append(" — ").AppendLine(fix.Title);
        text.AppendLine();
        text.Append("*").Append(fix.State.ToString().ToLowerInvariant())
            .Append(", raised ").Append(fix.Raised.ToString("yyyy-MM-dd")).AppendLine("*");

        if (fix.Detail.Length > 0)
        {
            text.AppendLine();
            text.AppendLine(fix.Detail);
        }

        if (fix.Change.Length > 0)
        {
            text.AppendLine();
            text.Append("**Changed** ").AppendLine(fix.Change);
        }

        if (fix.Evidence.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("**Since**");

            foreach (string note in fix.Evidence) text.Append("- ").AppendLine(note);
        }

        text.AppendLine();
    }
}
