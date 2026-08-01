using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Reader for delimited text logs (.msl, .csv, .txt, .log) — the export format
/// essentially every ECU and logger can produce.
///
/// Exports differ in almost every respect, so nothing is assumed from the file
/// extension. The reader detects, in order: text encoding, delimiter, header and
/// units rows, decimal separator, and the time base. That keeps one code path
/// working for TunerStudio, rusEFI, MaxxECU, Haltech, MoTeC, Link, AEM and
/// generic data-logger output.
/// </summary>
public sealed class DelimitedLogReader : ILogReader
{
    private static readonly char[] Delimiters = ['\t', ',', ';', '|'];

    /// <summary>Column names that identify a time base, normalised to lower case.</summary>
    private static readonly HashSet<string> TimeNames = new(StringComparer.Ordinal)
    {
        "time", "times", "time s", "timestamp", "time stamp", "elapsed", "elapsed time",
        "reltime", "rel time", "run time", "runtime", "seconds", "secs", "sec", "t",
        "zeit", "tid", "tempo", "session time", "log time", "abs time",
    };

    public string FormatName => "Delimited text";

    public bool CanRead(string path)
    {
        try
        {
            string[] lines = ReadLines(path, out _);
            return lines.Length >= 2 && DetectDelimiter(lines) is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public LogDocument Read(string path)
    {
        string[] lines = ReadLines(path, out Encoding encoding);
        if (lines.Length == 0) throw new LogFormatException("File is empty.");

        char delimiter = DetectDelimiter(lines)
            ?? throw new LogFormatException("No consistent column delimiter found.");

        var (headerIndex, dataIndex) = LocateHeader(lines, delimiter);
        string[] rawNames = SplitLine(lines[headerIndex], delimiter);
        int width = rawNames.Length;

        // A units row sits between the header and the data when the gap is > 1.
        string[] unitsRow = dataIndex - headerIndex >= 2
            ? SplitLine(lines[headerIndex + 1], delimiter)
            : [];

        bool decimalComma = UsesDecimalComma(lines, dataIndex, delimiter);

        var names = new string[width];
        var units = new string[width];
        for (int i = 0; i < width; i++)
        {
            // Units may be a separate row, or bracketed in the header itself.
            (names[i], string? inlineUnits) = SplitNameAndUnits(Clean(rawNames[i]));
            units[i] = i < unitsRow.Length && unitsRow[i].Trim().Length > 0
                ? Clean(unitsRow[i])
                : inlineUnits ?? "";

            if (names[i].Length == 0) names[i] = $"Column {i + 1}";
        }

        double[][] columns = ParseRows(lines, dataIndex, delimiter, width, decimalComma);

        var channels = new List<LogChannel>(width);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < width; i++)
        {
            // Logs really do repeat a name (MS3 emits "Fuel Consumption" twice,
            // in GPH and l/hr), and identical entries are unusable in the UI.
            string name = names[i];
            if (seen.TryGetValue(name, out int count))
            {
                seen[name] = ++count;
                name = units[i].Length > 0 ? $"{name} ({units[i]})" : $"{name} #{count}";
                if (seen.ContainsKey(name)) name = $"{names[i]} #{count}";
            }
            seen[name] = 1;

            channels.Add(new LogChannel(name, units[i], InferDigits(columns[i]), columns[i]));
        }

        int sampleCount = width > 0 ? columns[0].Length : 0;
        LogChannel time = ResolveTimeBase(channels, names, units, lines, dataIndex, delimiter, sampleCount);

        string[] preamble = lines.Take(headerIndex).ToArray();
        string? source = DetectSource(preamble, names);

        return new LogDocument
        {
            FilePath = path,
            Channels = channels,
            Time = time,
            Signature = preamble.Length > 0 ? Clean(preamble[0]) : null,
            CaptureInfo = preamble.Length > 1 ? Clean(preamble[1]) : null,
            RecordedAt = File.GetLastWriteTime(path),
            FormatName = FormatLabel(delimiter, source, encoding),
        };
    }

    // ----- text handling -----------------------------------------------------

    /// <summary>
    /// Reads the file as UTF-8, falling back to Latin-1. Older TunerStudio
    /// exports are ISO-8859-1, where a degree sign is a single invalid-UTF-8
    /// byte that would otherwise decode to a replacement character.
    /// </summary>
    private static string[] ReadLines(string path, out Encoding encoding)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return Split(encoding.GetString(StripBom(bytes)));
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.Latin1;
            return Split(encoding.GetString(bytes));
        }

        static string[] Split(string text) =>
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[] StripBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b[3..] : b;

    /// <summary>
    /// Picks the delimiter that splits the most lines into the same column count.
    /// Counting occurrences alone is misleading: prose in the preamble is full of
    /// commas, and a tab-delimited file with quoted text would lose to them.
    /// </summary>
    private static char? DetectDelimiter(string[] lines)
    {
        char? best = null;
        int bestScore = 0;

        foreach (char candidate in Delimiters)
        {
            var counts = new Dictionary<int, int>();
            foreach (string line in lines.Take(200))
            {
                int fields = SplitLine(line, candidate).Length;
                if (fields >= 2) counts[fields] = counts.GetValueOrDefault(fields) + 1;
            }

            if (counts.Count == 0) continue;

            // Score by the largest run of same-width lines, weighted by width so a
            // 60-column match beats a 2-column coincidence.
            (int width, int runs) = counts.MaxBy(kv => kv.Value);
            int score = runs * Math.Min(width, 40);
            if (runs >= 3 && score > bestScore) { bestScore = score; best = candidate; }
        }

        return best;
    }

    /// <summary>Splits one line, honouring RFC 4180 double-quoted fields.</summary>
    private static string[] SplitLine(string line, char delimiter)
    {
        if (!line.Contains('"')) return line.Split(delimiter);

        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == delimiter && !quoted)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return [.. fields];
    }

    private static string Clean(string cell) => cell.Trim().Trim('"').Trim();

    /// <summary>
    /// Pulls units out of a bracketed suffix: "MAP (kPa)", "RPM [rpm]", "CLT {°C}".
    /// Common for exports with no dedicated units row.
    /// </summary>
    private static (string Name, string? Units) SplitNameAndUnits(string header)
    {
        if (header.Length < 3) return (header, null);

        char close = header[^1];
        char open = close switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
        if (open == '\0') return (header, null);

        int start = header.LastIndexOf(open);
        if (start <= 0) return (header, null);

        string name = header[..start].Trim();
        string units = header[(start + 1)..^1].Trim();

        // "Fuel Consumption (2)" is a disambiguator, not a unit.
        if (name.Length == 0 || units.Length == 0 || units.All(char.IsDigit))
            return (header, null);

        return (name, units);
    }

    // ----- structure detection ----------------------------------------------

    /// <summary>
    /// Returns the header row and the first data row, by finding the earliest row
    /// whose width is matched by predominantly numeric rows below it.
    /// </summary>
    private static (int Header, int Data) LocateHeader(string[] lines, char delimiter)
    {
        const int Window = 24;

        for (int i = 0; i < Math.Min(lines.Length, 200); i++)
        {
            string[] header = SplitLine(lines[i], delimiter);
            if (header.Length < 2) continue;

            // Allow an optional units row between the header and the first data row.
            for (int gap = 1; gap <= 2 && i + gap < lines.Length; gap++)
            {
                int data = i + gap;
                if (!IsNumericRow(lines[data], delimiter, header.Length)) continue;

                // Confirm against the rows that follow. Real logs interleave
                // annotations ("MARK …") and rows with dropped cells, so require
                // a numeric majority rather than an unbroken run.
                int numeric = 0, considered = 0;
                for (int r = data; r < Math.Min(lines.Length, data + Window); r++)
                {
                    if (lines[r].Length == 0) continue;
                    considered++;
                    if (IsNumericRow(lines[r], delimiter, header.Length)) numeric++;
                }

                if (considered > 0 && numeric * 2 >= considered) return (i, data);
            }
        }

        throw new LogFormatException("Could not locate a header row followed by numeric data.");
    }

    private static bool IsNumericRow(string line, char delimiter, int width)
    {
        string[] cells = SplitLine(line, delimiter);
        if (cells.Length < width || width == 0) return false;

        int values = 0;
        for (int i = 0; i < width; i++)
            if (LooksLikeValue(cells[i])) values++;

        return values >= width * 0.8;
    }

    /// <summary>
    /// Whether a cell reads as data rather than a label. A wall-clock timestamp
    /// counts: some exports use one as the time column, and rejecting it would
    /// stop the whole file being recognised.
    /// </summary>
    private static bool LooksLikeValue(string cell)
    {
        string s = Clean(cell);
        if (s.Length == 0) return true;
        if (LooksNumeric(s)) return true;

        // Require a date or time separator, so plain words are not read as dates.
        return s.Length >= 8
               && (s.Contains(':') || s.Contains('-') || s.Contains('/'))
               && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);
    }

    private static bool LooksNumeric(string cell)
    {
        string s = Clean(cell);
        if (s.Length == 0) return true;

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
               || double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Detects the European convention where "1234,5" is a decimal. Only possible
    /// when the comma is not itself the delimiter.
    /// </summary>
    private static bool UsesDecimalComma(string[] lines, int dataIndex, char delimiter)
    {
        if (delimiter == ',') return false;

        int commaDecimals = 0, dotDecimals = 0;
        for (int r = dataIndex; r < Math.Min(lines.Length, dataIndex + 40); r++)
            foreach (string cell in SplitLine(lines[r], delimiter))
            {
                string s = Clean(cell);
                if (s.Length == 0) continue;
                if (s.Contains(',') && double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) commaDecimals++;
                else if (s.Contains('.')) dotDecimals++;
            }

        return commaDecimals > dotDecimals;
    }

    // ----- parsing ----------------------------------------------------------

    private static double[][] ParseRows(
        string[] lines, int dataIndex, char delimiter, int width, bool decimalComma)
    {
        var buffers = new List<double>[width];
        for (int i = 0; i < width; i++) buffers[i] = new List<double>(lines.Length - dataIndex);

        for (int r = dataIndex; r < lines.Length; r++)
        {
            if (lines[r].Length == 0) continue;

            string[] cells = SplitLine(lines[r], delimiter);
            // Annotation rows ("MARK …") and ragged rows are skipped.
            if (cells.Length < width) continue;

            for (int c = 0; c < width; c++)
                buffers[c].Add(ParseCell(cells[c], decimalComma));
        }

        var columns = new double[width][];
        for (int i = 0; i < width; i++) columns[i] = [.. buffers[i]];
        return columns;
    }

    private static double ParseCell(string cell, bool decimalComma)
    {
        string s = Clean(cell);
        if (s.Length == 0) return double.NaN;
        if (decimalComma) s = s.Replace(".", "").Replace(',', '.');

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : double.NaN;
    }

    /// <summary>Infers a sensible display precision from the magnitude of the data.</summary>
    private static int InferDigits(double[] values)
    {
        foreach (double v in values)
        {
            if (double.IsNaN(v)) continue;
            if (v != Math.Floor(v)) return 2;
        }
        return 0;
    }

    // ----- time base --------------------------------------------------------

    private static LogChannel ResolveTimeBase(
        List<LogChannel> channels, string[] names, string[] units,
        string[] lines, int dataIndex, char delimiter, int sampleCount)
    {
        int index = FindTimeColumn(names, units);

        if (index >= 0)
        {
            LogChannel candidate = channels[index];
            double factor = TimeScale(units[index]);

            if (!candidate.IsFlat && IsMonotonic(candidate.Values))
            {
                if (Math.Abs(factor - 1) < double.Epsilon) return candidate;

                var scaled = new double[candidate.Values.Length];
                for (int i = 0; i < scaled.Length; i++) scaled[i] = candidate.Values[i] * factor;
                return new LogChannel(candidate.Name, "s", 3, scaled);
            }

            // The column may hold wall-clock strings rather than numbers.
            if (TryReadTimestamps(lines, dataIndex, delimiter, index, sampleCount, out double[] elapsed))
                return new LogChannel("Time", "s", 3, elapsed);
        }

        var synthetic = new double[sampleCount];
        for (int i = 0; i < sampleCount; i++) synthetic[i] = i;
        return new LogChannel("Sample", "#", 0, synthetic);
    }

    private static int FindTimeColumn(string[] names, string[] units)
    {
        for (int i = 0; i < names.Length; i++)
            if (TimeNames.Contains(ChannelClassifier.Normalise(names[i])))
                return i;

        // Fall back to the first column when its units are a time unit.
        for (int i = 0; i < units.Length; i++)
            if (TimeScale(units[i]) != 1 || units[i].Trim().ToLowerInvariant() is "s" or "sec")
                return i;

        return -1;
    }

    /// <summary>Multiplier converting a column's units into seconds.</summary>
    private static double TimeScale(string units) => units.Trim().ToLowerInvariant() switch
    {
        "ms" or "msec" or "millisecond" or "milliseconds" => 0.001,
        "us" or "usec" or "microsecond" or "microseconds" => 0.000001,
        "min" or "minute" or "minutes" => 60,
        _ => 1,
    };

    private static bool TryReadTimestamps(
        string[] lines, int dataIndex, char delimiter, int column, int sampleCount, out double[] elapsed)
    {
        elapsed = [];
        var stamps = new List<DateTime>(sampleCount);

        for (int r = dataIndex; r < lines.Length; r++)
        {
            if (lines[r].Length == 0) continue;
            string[] cells = SplitLine(lines[r], delimiter);
            if (cells.Length <= column) continue;

            if (!DateTime.TryParse(Clean(cells[column]), CultureInfo.InvariantCulture,
                                   DateTimeStyles.AllowWhiteSpaces, out DateTime stamp))
                return false;

            stamps.Add(stamp);
        }

        if (stamps.Count != sampleCount || stamps.Count == 0) return false;

        elapsed = new double[stamps.Count];
        for (int i = 0; i < stamps.Count; i++)
            elapsed[i] = (stamps[i] - stamps[0]).TotalSeconds;

        return IsMonotonic(elapsed) && elapsed[^1] > 0;
    }

    private static bool IsMonotonic(double[] values)
    {
        for (int i = 1; i < values.Length; i++)
            if (values[i] < values[i - 1]) return false;
        return true;
    }

    // ----- labelling --------------------------------------------------------

    /// <summary>
    /// Identifies the producing tool from its preamble. Purely cosmetic — it
    /// names the format in the UI and never changes how the file is parsed.
    /// </summary>
    private static string? DetectSource(string[] preamble, string[] names)
    {
        string blob = string.Join(' ', preamble).ToLowerInvariant();
        string columns = string.Join(' ', names).ToLowerInvariant();

        if (blob.Contains("tunerstudio") || blob.Contains("megasquirt")
            || blob.Contains("ms3 format") || blob.Contains("ms2extra")) return "TunerStudio";
        if (blob.Contains("rusefi")) return "rusEFI";
        if (blob.Contains("maxxecu") || blob.Contains("maxxtuner")) return "MaxxECU";
        if (blob.Contains("haltech")) return "Haltech";
        if (blob.Contains("motec") || blob.Contains("i2 pro")) return "MoTeC";
        if (blob.Contains("ecumaster")) return "ECUMaster";
        if (blob.Contains("aem") && blob.Contains("infinity")) return "AEM Infinity";
        if (blob.Contains("speeduino")) return "Speeduino";
        if (blob.Contains("holley")) return "Holley";
        if (blob.Contains("pcmlink") || blob.Contains("hp tuners")) return "HP Tuners";
        if (blob.Contains("g4+") || blob.Contains("pclink")) return "Link";
        if (columns.Contains("afr") && columns.Contains("rpm") && blob.Length == 0) return null;

        return null;
    }

    private static string FormatLabel(char delimiter, string? source, Encoding encoding)
    {
        string kind = delimiter switch
        {
            '\t' => "tab-delimited",
            ';' => "semicolon CSV",
            '|' => "pipe-delimited",
            _ => "CSV",
        };

        string label = source is null ? kind : $"{source} {kind}";
        return Equals(encoding, Encoding.Latin1) ? $"{label} (Latin-1)" : label;
    }
}
