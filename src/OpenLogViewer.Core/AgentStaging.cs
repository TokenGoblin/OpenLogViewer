namespace OpenLogViewer.Core;

/// <summary>
/// Where an agent leaves a file for a person to open, instead of — or as well
/// as — writing to the ECU's working memory.
///
/// <para>
/// A second, independent output path. Not every change belongs in
/// <c>/tune/apply</c>: sometimes the point is to hand over a real
/// <c>.msq</c> or table CSV that a person imports themselves in TunerStudio,
/// reviews, or simply keeps. Nothing in here is gated the way a write to the
/// ECU is — no armed-writes check, no connection check — because nothing in
/// here can reach an engine. It writes a file, or it does not.
/// </para>
/// <para>
/// The path safety this needs — a bare file name, never anywhere but this
/// folder — is <see cref="SafeFolder"/>'s, shared with the definitions import
/// rather than written twice.
/// </para>
/// </summary>
public sealed class AgentStaging
{
    private readonly SafeFolder _folder;

    public AgentStaging(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _folder = new SafeFolder(workspace.Staging);
    }

    /// <summary>The folder itself. Created on first use, not up front.</summary>
    public string Folder => _folder.Folder;

    /// <summary>
    /// Writes <paramref name="content"/> under <paramref name="filename"/> and
    /// hands back the full path it landed at.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is empty, names something other than a plain file, or would
    /// resolve outside the staging folder.
    /// </exception>
    public string Stage(string filename, string content) => _folder.Write(filename, content);

    /// <summary>What is in the folder now, newest first.</summary>
    public IReadOnlyList<AgentStagedFile> ListStaged() =>
        [.. _folder.Files().Select(f => new AgentStagedFile(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc))];
}
