using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Reads a MaxxECU log, which is a zip holding three things.
///
/// <list type="bullet">
///   <item><c>*.MaxxECU-Log</c> — tab-separated text, a header of channel names
///   and then a row per sample.</item>
///   <item><c>*.LogMetaData</c> — one line, <c>LogRate=</c> the seconds between
///   samples. There is no time column, so this is the only thing that says when
///   anything happened.</item>
///   <item><c>*.MaxxECU-save</c> — the tune that was running.</item>
/// </list>
///
/// Each header names its channel and gives MTune's index for it in brackets:
/// <c>Coolant temp [18]</c>. The index is what MTune's channel table is keyed
/// by, so units come from the same definitions the live gauges use rather than
/// being guessed from the name.
/// </summary>
public sealed class MaxxLogReader : ILogReader
{
    public string FormatName => "MaxxECU";

    /// <summary>
    /// True for a zip that contains a MaxxECU log.
    ///
    /// The contents are checked rather than the extension: these arrive as
    /// <c>.MaxxECU-Zip-log</c>, but a zip renamed by a browser or an email
    /// client is still the same file.
    /// </summary>
    public bool CanRead(string path)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            return FindLog(archive) is not null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public LogDocument Read(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);

        ZipArchiveEntry log = FindLog(archive)
            ?? throw new LogFormatException(
                $"'{Path.GetFileName(path)}' is a zip but holds no MaxxECU log.");

        // A zip says how large its contents are before any of it is read, and
        // that is worth asking. Compressed data expands by a factor of a
        // thousand or more, so a file small enough to arrive by email can ask
        // for more memory than the machine has — and unlike a large log, nothing
        // about the file on disk warns anyone it is about to happen. A real
        // 22 MB log declares 22 MB.
        //
        // The figure is what the archive claims rather than what it delivers, so
        // this catches the careless case rather than a determined one. Refusing
        // to start is still better than failing halfway through.
        if (log.Length > MaximumUncompressed)
            throw new LogFormatException(
                $"'{Path.GetFileName(path)}' expands to {log.Length / (1024 * 1024):N0} MB, "
                + $"past the {MaximumUncompressed / (1024 * 1024):N0} MB this will open. "
                + "A zip that small holding a log that large is more often corrupt than real.");

        double interval = ReadLogRate(archive);
        (List<string> names, List<int> ids, List<float[]> columns, int rows) = ReadColumns(log);

        if (names.Count == 0 || rows == 0)
            throw new LogFormatException($"'{Path.GetFileName(path)}' holds no samples.");

        IReadOnlyDictionary<int, string> units = MaxxChannelTable.Units();

        var channels = new List<LogChannel>(names.Count);
        for (int i = 0; i < names.Count; i++)
            channels.Add(LogChannel.Adopt(names[i], units.GetValueOrDefault(ids[i], ""), 3, columns[i]));

        // Built from the rate, because the file carries no time of its own. A
        // log with no rate would otherwise plot against sample number, which
        // looks like seconds and is not.
        var time = new double[rows];
        for (int i = 0; i < rows; i++) time[i] = i * interval;

        return new LogDocument
        {
            FilePath = path,
            Channels = channels,
            Time = new LogChannel("Time", "s", 3, time, preservePrecision: true),
            FormatName = FormatName,
            EmbeddedTune = null,
            RecordedAt = File.GetLastWriteTime(path),
        };
    }

    /// <summary>
    /// The largest log this will expand from an archive. Generous: the sample
    /// logs are tens of megabytes, and a day's continuous recording is not
    /// close to this.
    /// </summary>
    private const long MaximumUncompressed = 512L * 1024 * 1024;

    private static ZipArchiveEntry? FindLog(ZipArchive archive) =>
        archive.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".MaxxECU-Log", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Seconds between samples, from the metadata entry.
    ///
    /// Falls back to a hundredth of a second when it is missing or unreadable —
    /// wrong, but wrong by a constant, which keeps every trace the right shape
    /// and only mislabels the axis.
    /// </summary>
    private static double ReadLogRate(ZipArchive archive)
    {
        const double Fallback = 0.01;

        ZipArchiveEntry? meta = archive.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".LogMetaData", StringComparison.OrdinalIgnoreCase));

        if (meta is null) return Fallback;

        try
        {
            using var reader = new StreamReader(meta.Open(), Encoding.ASCII);

            while (reader.ReadLine() is { } line)
            {
                int equals = line.IndexOf('=');
                if (equals < 0) continue;

                if (!line[..equals].Trim().Equals("LogRate", StringComparison.OrdinalIgnoreCase)) continue;

                return double.TryParse(
                    line[(equals + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double rate) && rate > 0
                    ? rate
                    : Fallback;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return Fallback;
        }

        return Fallback;
    }

    /// <summary>
    /// Reads the header and every row.
    ///
    /// Grown into lists rather than sized up front: the row count is not stated
    /// anywhere and a 22 MB log holds several thousand of them, so counting
    /// first would mean reading the whole thing twice.
    /// </summary>
    private static (List<string> Names, List<int> Ids, List<float[]> Columns, int Rows)
        ReadColumns(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.ASCII);

        string? header = reader.ReadLine();
        if (header is null) return ([], [], [], 0);

        string[] titles = header.Split('\t');

        var names = new List<string>(titles.Length);
        var ids = new List<int>(titles.Length);
        var growing = new List<List<float>>(titles.Length);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The header ends with a tab, so the last field is empty.
        for (int i = 0; i < titles.Length; i++)
        {
            string title = titles[i].Trim();
            if (title.Length == 0) continue;

            (string name, int id) = SplitIndex(title);

            // Duplicate names would collide in every preset and filter.
            if (!taken.Add(name)) name = $"{name} ({i})";

            names.Add(name);
            ids.Add(id);
            growing.Add([]);
        }

        int columns = names.Count;
        int rows = 0;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;

            int at = 0;
            int start = 0;

            for (int i = 0; i <= line.Length && at < columns; i++)
            {
                if (i != line.Length && line[i] != '\t') continue;

                growing[at++].Add(Number(line.AsSpan(start, i - start)));
                start = i + 1;
            }

            // A short row leaves the rest of its channels unknown rather than
            // shifting everything after it up by one.
            while (at < columns) growing[at++].Add(float.NaN);

            rows++;
        }

        return (names, ids, [.. growing.Select(c => c.ToArray())], rows);
    }

    /// <summary>
    /// Splits "Coolant temp [18]" into its name and MTune's index for it.
    ///
    /// The index is worth keeping and the bracket is not: it is how the channel
    /// is looked up in MTune's definitions, and it is noise in a channel list.
    /// </summary>
    private static (string Name, int Id) SplitIndex(string title)
    {
        if (!title.EndsWith(']')) return (title, -1);

        int open = title.LastIndexOf('[');
        if (open <= 0) return (title, -1);

        return int.TryParse(
            title.AsSpan(open + 1, title.Length - open - 2),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? (title[..open].Trim(), id)
            : (title, -1);
    }

    private static float Number(ReadOnlySpan<char> text) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : float.NaN;
}
