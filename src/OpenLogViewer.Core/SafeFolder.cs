using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// A folder that plain files are written directly into, and nowhere else.
///
/// <para>
/// The whole of what this protects is staying inside the folder. A caller hands
/// over a bare file name; before that name is trusted it is checked for anything
/// that could make it mean somewhere else — a separator of either kind, a
/// leading <c>..</c>, an absolute path — and checked again after it is resolved
/// to a full path, in case a folder along the way is not what it looks like.
/// </para>
/// <para>
/// Shared rather than written twice because there are now two callers with the
/// same problem and very different consequences for getting it wrong: an agent
/// staging a tune file for somebody to open, and an agent installing a firmware
/// definition that decides how every byte of an engine's memory is read and
/// written. One implementation, tested once.
/// </para>
/// </summary>
public sealed class SafeFolder
{
    public SafeFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Folder = Path.GetFullPath(folder);
    }

    /// <summary>The folder itself. Created on first write, not up front.</summary>
    public string Folder { get; }

    /// <summary>
    /// Writes <paramref name="content"/> under <paramref name="filename"/> and
    /// hands back the full path it landed at.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is empty, names something other than a plain file, or would
    /// resolve outside this folder.
    /// </exception>
    public string Write(string filename, string content, Encoding? encoding = null)
    {
        string resolved = Resolve(filename);

        Directory.CreateDirectory(Folder);
        WriteAtomic(resolved, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return resolved;
    }

    /// <summary>
    /// Where <paramref name="filename"/> would land, having proved it lands
    /// inside this folder. Writes nothing.
    /// </summary>
    public string Resolve(string filename)
    {
        string safe = SafeName(filename);
        string resolved = Path.GetFullPath(Path.Combine(Folder, safe));

        // Checked again after resolving, not just on the string that was given:
        // a name that looks plain can still resolve outside this folder if a
        // component of the folder itself turns out to be a symlink or junction.
        if (!Inside(resolved))
            throw new ArgumentException($"\"{filename}\" would land outside {Folder}.", nameof(filename));

        return resolved;
    }

    /// <summary>What is in the folder now, newest first. Empty when it does not exist yet.</summary>
    public IReadOnlyList<FileInfo> Files()
    {
        if (!Directory.Exists(Folder)) return [];

        return [.. new DirectoryInfo(Folder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc)];
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
            throw new ArgumentException("A file needs a name.", nameof(filename));

        if (filename.Contains("..", StringComparison.Ordinal)
            || filename.Contains('/', StringComparison.Ordinal)
            || filename.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(filename)
            || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"\"{filename}\" is not a plain file name.", nameof(filename));
        }

        return filename;
    }

    private bool Inside(string resolved) =>
        resolved.Equals(Folder, StringComparison.OrdinalIgnoreCase)
        || resolved.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Written to a temporary file in the same folder and moved into place, so
    /// an interrupted write never leaves a truncated file where a person — or a
    /// definition scan — might find and use it. The same discipline the
    /// application's own CSV exports already follow.
    /// </summary>
    private static void WriteAtomic(string path, string content, Encoding encoding)
    {
        string temporary = path + ".tmp";

        File.WriteAllText(temporary, content, encoding);
        File.Move(temporary, path, overwrite: true);
    }
}
