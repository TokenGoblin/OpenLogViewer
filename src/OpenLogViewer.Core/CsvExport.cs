using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>
/// Writes logs and heat tables out as CSV.
///
/// Two things are deliberate. Numbers are always invariant-culture, so a file
/// written on a machine with a comma decimal separator still opens everywhere —
/// an export that only works on the machine that made it is worse than none.
/// And a written log reads back into this app: the header and units rows are the
/// shape <see cref="DelimitedLogReader"/> already detects, and a missing reading
/// is an empty cell, which it already decodes as one.
/// </summary>
public static class CsvExport
{
    /// <summary>
    /// Writes the time base followed by the given channels, over an inclusive
    /// sample range. Channels are written in the order supplied.
    /// </summary>
    public static void WriteLog(
        TextWriter writer, LogDocument document, IReadOnlyList<LogChannel> channels,
        int firstSample, int lastSample)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(channels);

        int first = Math.Max(0, firstSample);
        int last = Math.Min(document.SampleCount - 1, lastSample);

        // The time base leads and is never repeated, however the caller ordered
        // its list — a second Time column would make the file ambiguous.
        var columns = new List<LogChannel> { document.Time };
        columns.AddRange(channels.Where(c => !document.IsTimeBase(c)));

        writer.WriteLine(string.Join(',', columns.Select(c => Escape(c.Name))));
        writer.WriteLine(string.Join(',', columns.Select(c => Escape(c.Units))));

        var cells = new string[columns.Count];
        for (int i = first; i <= last; i++)
        {
            for (int c = 0; c < columns.Count; c++) cells[c] = Number(columns[c], i);
            writer.WriteLine(string.Join(',', cells));
        }
    }

    /// <summary>
    /// Writes a heat table in the shape a tuning table has: the X breakpoints
    /// across the top, the Y breakpoints down the side, highest row first so it
    /// matches what is on screen and what a tuning app expects. Cells that were
    /// never visited are left empty rather than written as zero, which would
    /// read as a real measurement of nothing.
    /// </summary>
    public static void WriteTable(TextWriter writer, HistogramTable table)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(table);

        var header = new List<string> { Escape($"{table.Y.Name} \\ {table.X.Name}") };
        header.AddRange(table.ColumnCenters.Select(Axis));
        writer.WriteLine(string.Join(',', header));

        for (int row = table.Rows - 1; row >= 0; row--)
        {
            var cells = new List<string> { Axis(table.RowCenters[row]) };

            for (int column = 0; column < table.Columns; column++)
            {
                double? value = table.Values[column, row];
                cells.Add(value is null
                    ? ""
                    : Round(value.Value).ToString("R", CultureInfo.InvariantCulture));
            }

            writer.WriteLine(string.Join(',', cells));
        }
    }

    /// <summary>
    /// Writes the header and units rows. Shared with the live recorder, so a
    /// session captured from an ECU is the same shape as an exported log and
    /// reopens the same way.
    /// </summary>
    public static void WriteHeader(
        TextWriter writer, IReadOnlyList<string> names, IReadOnlyList<string> units)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(names);

        writer.WriteLine(string.Join(',', names.Select(Escape)));
        writer.WriteLine(string.Join(',', names.Select((_, i) => Escape(i < units.Count ? units[i] : ""))));
    }

    /// <summary>One row of values, formatted as the exporter formats them.</summary>
    public static void WriteRow(TextWriter writer, ReadOnlySpan<double> values)
    {
        ArgumentNullException.ThrowIfNull(writer);

        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write(Number(values[i]));
        }

        writer.WriteLine();
    }

    /// <summary>
    /// The points of a scatter, one row per sample that survived: the sample's
    /// index in the log, then its X, Y and Z.
    ///
    /// The index is first because it is what makes the file answerable — a row
    /// here can be found again in a full-log export, or in the log itself, which
    /// a bare triple of numbers cannot. Samples are written in log order, which
    /// is not the order anything is drawn in, but is the only order that means
    /// something outside this window.
    /// </summary>
    public static void WritePoints(TextWriter writer, ScatterPlot points)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(points);

        // Named for what the column holds: with a comparison set, Z is a
        // deviation and labelling it with the channel's own name would be wrong.
        string z = points.IsDelta
            ? $"{points.Z.Name} − {points.ZCompare!.Name}"
            : points.Z.Name;

        WriteHeader(
            writer,
            ["Sample", points.X.Name, points.Y.Name, z],
            ["", points.X.Units, points.Y.Units, points.Z.Units]);

        for (int i = 0; i < points.Count; i++)
        {
            writer.Write(points.Samples[i].ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(Number(points.Xs[i]));
            writer.Write(',');
            writer.Write(Number(points.Ys[i]));
            writer.Write(',');
            writer.Write(Number(points.Zs[i]));
            writer.WriteLine();
        }
    }

    /// <summary>Sample counts per cell, in the same shape as <see cref="WriteTable"/>.</summary>
    public static void WriteTableCounts(TextWriter writer, HistogramTable table)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(table);

        var header = new List<string> { Escape($"{table.Y.Name} \\ {table.X.Name}") };
        header.AddRange(table.ColumnCenters.Select(Axis));
        writer.WriteLine(string.Join(',', header));

        for (int row = table.Rows - 1; row >= 0; row--)
        {
            var cells = new List<string> { Axis(table.RowCenters[row]) };
            for (int column = 0; column < table.Columns; column++)
                cells.Add(table.Counts[column, row].ToString(CultureInfo.InvariantCulture));

            writer.WriteLine(string.Join(',', cells));
        }
    }

    /// <summary>
    /// The shortest string that reads back as the same sample.
    ///
    /// Samples are held as float and would print seventeen digits of rounding
    /// error if formatted as the doubles they widen to — 13.399999618530273 for
    /// a reading of 13.4. The time base is held as double, but only some bases
    /// need it: one taken from a 32-bit field is a widened float and gets the
    /// same treatment, which is why this asks whether the value survives the
    /// narrowing rather than which column it came from.
    /// </summary>
    private static string Number(LogChannel channel, int index) => Number(channel.At(index));

    private static string Number(double value)
    {
        if (double.IsNaN(value)) return "";

        float narrowed = (float)value;
        return narrowed == value
            ? narrowed.ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Breakpoints are display values, so they are not worth full precision.</summary>
    private static string Axis(double value) =>
        Math.Round(value, 4).ToString("R", CultureInfo.InvariantCulture);

    private static double Round(double value) => Math.Round(value, 6);

    /// <summary>RFC 4180 quoting, for the channel names that contain a comma.</summary>
    private static string Escape(string field)
    {
        if (field.Length == 0) return field;
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0) return field;

        return $"\"{field.Replace("\"", "\"\"")}\"";
    }
}
