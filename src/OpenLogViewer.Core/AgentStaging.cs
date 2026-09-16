using System.Text;

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
/// <b>The one rule that matters is staying inside the folder.</b> A caller
/// hands over a bare file name; before that name is trusted it is checked for
/// anything that could make it mean somewhere else — a separator of either
/// kind, a leading <c>..</c>, an absolute path — and checked again after it is
/// resolved to a full path, in case a folder along the way is not what it
/// looks like. Path safety, not the ECU's write gates, is the whole of what
/// this class is protecting.
/// </para>
/// </summary>
public sealed class AgentStaging
{
    public AgentStaging(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        Folder = System.IO.Path.GetFullPath(workspace.Staging);
    }

    /// <summary>The folder itself. Created on first use, not up front.</summary>
    public string Folder { get; }

    /// <summary>
    /// Writes <paramref name="content"/> under <paramref name="filename"/> and
    /// hands back the full path it landed at.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is empty, names something other than a plain file, or would
    /// resolve outside the staging folder.
    /// </exception>
    public string Stage(string filename, string content)
    {
        string safe = SafeName(filename);

        Directory.CreateDirectory(Folder);

        string path = System.IO.Path.Combine(Folder, safe);
        string resolved = System.IO.Path.GetFullPath(path);

        // Checked again after resolving, not just on the string that was given:
        // a name that looks plain can still resolve outside this folder if a
        // component of the folder itself turns out to be a symlink or junction.
        if (!Inside(resolved))
            throw new ArgumentException($"\"{filename}\" would land outside the staging folder.", nameof(filename));

        WriteAtomic(resolved, content);
        return resolved;
    }

    /// <summary>What is in the folder now, newest first.</summary>
    public IReadOnlyList<AgentStagedFile> ListStaged()
    {
        if (!Directory.Exists(Folder)) return [];

        return
        [
            .. new DirectoryInfo(Folder).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new AgentStagedFile(f.Name, f.FullName, f.Length, f.LastWriteTimeUtc)),
        ];
    }

    /// <summary>
    /// Refuses anything that is not, on its face, a single file name inside this
    /// folder — before it is ever joined to it. Cheaper and clearer than relying
    /// on the resolved-path check alone, and it is what turns
    /// <c>../../Windows/System32/whatever</c> into an error rather than a
    /// successful write two enclosing tools away from where anyone would think
    /// to look.
    /// </summary>
    private static string SafeName(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            throw new ArgumentException("A staged file needs a name.", nameof(filename));

        if (filename.Contains("..", StringComparison.Ordinal)
            || filename.Contains('/', StringComparison.Ordinal)
            || filename.Contains('\\', StringComparison.Ordinal)
            || System.IO.Path.IsPathRooted(filename)
            || filename.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"\"{filename}\" is not a plain file name.", nameof(filename));
        }

        return filename;
    }

    private bool Inside(string resolved) =>
        resolved.Equals(Folder, StringComparison.OrdinalIgnoreCase)
        || resolved.StartsWith(
            Folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Written to a temporary file in the same folder and moved into place, so
    /// an interrupted stage never leaves a truncated file where a person might
    /// find and open it. The same discipline the application's own CSV exports
    /// already use.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        string temporary = path + ".tmp";

        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }
}
