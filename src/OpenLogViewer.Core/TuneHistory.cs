using System.Security.Cryptography;

namespace OpenLogViewer.Core;

/// <summary>
/// The tunes a project has been through, and what changed between them.
///
/// <para>
/// <b>Whole copies, not deltas.</b> A saved tune is 122 KB on a MegaSquirt and
/// 151 KB on a rusEFI, so two hundred versions is thirty megabytes — nothing
/// against what it buys. Deltas would save that and introduce the one failure
/// this must not have: a chain where an early corruption silently spoils
/// everything after it. Each version stands alone and can be opened by
/// TunerStudio without this program existing.
/// </para>
/// <para>
/// <b>Identity is the bytes, not the moment.</b> Reading the tune at the start
/// of two sessions that changed nothing gives one version, not two, and pressing
/// burn twice does not branch history. What is worth recording is that the
/// controller held something different, and a fingerprint is the only honest
/// test of that.
/// </para>
/// <para>
/// <b>What makes it worth more than a folder of files</b> is that a version
/// knows its parent, knows why it was made, knows which fixes it was meant to
/// address, and — through the sitting that names it — knows which logs were
/// recorded while the controller was running it. That last one is the join that
/// turns "the mixture is lean" into "the mixture is still lean after the change
/// that was supposed to fix it".
/// </para>
/// </summary>
public static class TuneHistory
{
    /// <summary>Where a project keeps its tunes.</summary>
    public const string Folder = "tunes";

    /// <summary>
    /// What a tune's bytes come to, for telling one version from another.
    ///
    /// Over the pages rather than over the written file, because a file carries
    /// a timestamp and a comment that move when nothing about the tune has.
    /// </summary>
    public static string Fingerprint(EcuTune tune)
    {
        ArgumentNullException.ThrowIfNull(tune);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (byte[] page in tune.Pages) hash.AppendData(page);

        return Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Records the tune as a version of the project, or returns the version
    /// already holding these bytes.
    ///
    /// <para>
    /// <paramref name="burned"/> is kept rather than worked out, and matters:
    /// a tune written but not burned is gone at the next power cycle, so a log
    /// recorded after one is evidence about a tune the controller may not be
    /// running any more.
    /// </para>
    /// </summary>
    public static (TuningProject Project, TuneVersion Version, bool IsNew) Capture(
        TuningProject project,
        string projectFolder,
        EcuTune tune,
        string signature,
        string note = "",
        IReadOnlyList<string>? addresses = null,
        bool burned = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(tune);

        string fingerprint = Fingerprint(tune);

        // The same bytes again. Worth updating what is known about it — a burn
        // of a version already recorded is news — but not worth a new version.
        if (project.Versions.FirstOrDefault(v => v.Fingerprint == fingerprint) is { } already)
        {
            TuneVersion updated = already with
            {
                Burned = already.Burned || burned,
                Note = note.Length > 0 && already.Note.Length == 0 ? note : already.Note,
            };

            return (project.With(updated), updated, false);
        }

        string id = project.NextVersionId();
        string file = Path.Combine(Folder, $"{id}.msq");

        Directory.CreateDirectory(Path.Combine(projectFolder, Folder));
        MsqWriter.Save(
            Path.Combine(projectFolder, file), tune, signature,
            comment: note.Length > 0 ? note : $"{project.Vehicle} {id}");

        var version = new TuneVersion
        {
            Id = id,
            Signature = signature,
            Fingerprint = fingerprint,

            // The newest is the parent, which is what a linear history means.
            // Nothing here branches: a tune is a single thing on a single
            // controller, and pretending otherwise would invite a merge, which
            // for a set of engine settings is not a thing anyone should be
            // offered.
            Parent = project.Versions.Count > 0 ? project.Versions[^1].Id : "",
            Note = note,
            Addresses = addresses ?? [],
            Burned = burned,
            File = file,
        };

        return (project.With(version), version, true);
    }

    /// <summary>Reads a version's tune back, or null where the file has gone.</summary>
    public static EcuTune? Read(
        TuningProject project, string projectFolder, string versionId, TuneLayout layout)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);

        if (project.Version(versionId) is not { } version) return null;

        string path = Path.Combine(projectFolder, version.File);
        if (!File.Exists(path)) return null;

        try
        {
            return MsqApply.Load(layout, MsqFile.ReadFile(path)).Tune;
        }
        catch (Exception e) when (e is IOException or LogFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// What changed between two versions, setting by setting.
    ///
    /// Through the values rather than the bytes, which is the only comparison
    /// that means anything to a person: two tunes can differ in bits no constant
    /// declares and be the same tune.
    /// </summary>
    public static VersionDifference? Compare(
        TuningProject project, string projectFolder, string from, string to, TuneLayout layout)
    {
        EcuTune? earlier = Read(project, projectFolder, from, layout);
        EcuTune? later = Read(project, projectFolder, to, layout);

        if (earlier is null || later is null) return null;

        return new VersionDifference(from, to, TuneCompare.Compare(later, earlier));
    }
}
