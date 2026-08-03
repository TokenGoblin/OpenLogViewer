using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>One page of the ECU's settings, and how to ask for it.</summary>
public sealed record TunePage
{
    /// <summary>Position in the INI's page list, from zero.</summary>
    public required int Index { get; init; }

    public required int Size { get; init; }

    /// <summary>
    /// The bytes that name this page in a request — the INI's
    /// <c>pageIdentifier</c>, which is a byte string rather than a number and
    /// may contain the CAN id.
    /// </summary>
    public required string Identifier { get; init; }

    /// <summary>Template for reading part of it, e.g. <c>R%2o%2c</c>.</summary>
    public required string ReadCommand { get; init; }

    /// <summary>
    /// Template for writing a run of bytes into it, e.g. <c>C%2o%2c%v</c>.
    /// Empty when the firmware declares none.
    /// </summary>
    public string ChunkWriteCommand { get; init; } = "";

    /// <summary>
    /// Template for committing the page to flash, e.g. <c>B</c> or <c>b%2i</c>.
    ///
    /// Separate from writing on purpose, and on the ECU as well as here: a write
    /// lands in the controller's working memory and is lost at the next power
    /// cycle, while a burn is permanent.
    /// </summary>
    public string BurnCommand { get; init; } = "";
}

/// <summary>
/// One setting in the ECU's memory: where it lives and what the bytes mean.
///
/// The same idea as a realtime field, for a different block. A scalar is a lone
/// value, an array is a table or a set of breakpoints, and a bit field is one of
/// several choices packed into a byte.
/// </summary>
public sealed record TuneConstant
{
    public required string Name { get; init; }

    public required int Page { get; init; }

    public required int Offset { get; init; }

    public required RealtimeType Type { get; init; }

    public string Units { get; init; } = "";

    public double Scale { get; init; } = 1;

    public double Transform { get; init; }

    public int Digits { get; init; }

    /// <summary>
    /// The range the firmware says this may hold, in the units it is displayed
    /// in. NaN where the file does not say.
    ///
    /// Worth having because it is much tighter than the datatype's, and it is
    /// the datatype that would otherwise be the only guard when a value is being
    /// written back. An ignition table stored as a signed 16-bit number at a
    /// tenth of a degree accepts −3,276 to 3,276 degrees of advance as far as
    /// the encoding is concerned; the firmware declares −10 to 60, which is the
    /// figure that means something.
    /// </summary>
    public double Low { get; init; } = double.NaN;

    public double High { get; init; } = double.NaN;

    /// <summary>Whether the firmware stated a usable range.</summary>
    public bool HasRange => !double.IsNaN(Low) && !double.IsNaN(High) && High > Low;

    /// <summary>Columns, or 1 for a scalar and for a one-dimensional array.</summary>
    public int Columns { get; init; } = 1;

    /// <summary>Rows, or 1 for a scalar.</summary>
    public int Rows { get; init; } = 1;

    public int BitLow { get; init; } = -1;

    public int BitHigh { get; init; } = -1;

    public bool IsBitField => BitLow >= 0;

    public bool IsArray => Columns * Rows > 1;

    public int ElementSize => Type switch
    {
        RealtimeType.U08 or RealtimeType.S08 => 1,
        RealtimeType.U16 or RealtimeType.S16 => 2,
        _ => 4,
    };

    /// <summary>Bytes this constant occupies.</summary>
    public int Size => ElementSize * Columns * Rows;
}

/// <summary>Everything needed to read an ECU's settings and make sense of them.</summary>
public sealed record TuneLayout
{
    public required IReadOnlyList<TunePage> Pages { get; init; }

    public required IReadOnlyList<TuneConstant> Constants { get; init; }

    public bool LittleEndian { get; init; }

    public int BlockingFactor { get; init; }

    /// <summary>
    /// Milliseconds to pause between consecutive writes, as the firmware asks.
    ///
    /// MS2Extra declares one millisecond. Small, and declared for a reason: the
    /// controller is copying bytes into its own memory between messages, and the
    /// next request arriving underneath that is how a write ends up half
    /// applied.
    /// </summary>
    public int InterWriteDelay { get; init; }

    /// <summary>
    /// Milliseconds to wait after a burn before speaking again.
    ///
    /// Ten on MS2Extra, where the file calls it the delay after the burn
    /// command. Writing flash stops the controller answering for as long as it
    /// takes, so a request sent immediately afterwards is a request sent into
    /// silence — and one that then desynchronises everything after it.
    /// </summary>
    public int AfterBurnDelay { get; init; }

    /// <summary>Total bytes across every page — what a full read costs.</summary>
    public int TotalSize => Pages.Sum(p => p.Size);
}

/// <summary>
/// Reads the <c>[Constants]</c> section: the shape of the ECU's settings.
///
/// This is the other half of what an INI is for. The output channels say what
/// the ECU is doing; the constants say what it has been told to do — the tables,
/// the limits and every setting behind them. Reading them from the ECU rather
/// than from a saved file is what makes the tune on screen the tune that is
/// actually running.
/// </summary>
public static class TuneLayoutReader
{
    private static readonly Regex Scalar = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*scalar\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*(?:,\s*(?<rest>.*))?$""",
        RegexOptions.Compiled);

    private static readonly Regex Array = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*array\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*,\s*\[\s*(?<cols>\d+)\s*(?:x\s*(?<rows>\d+)\s*)?\]\s*(?:,\s*(?<rest>.*))?$""",
        RegexOptions.Compiled);

    private static readonly Regex Bits = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*bits\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*,\s*\[\s*(?<low>\d+)\s*:\s*(?<high>\d+)\s*\]""",
        RegexOptions.Compiled);

    private static readonly Regex Setting = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>.+?)\s*$""",
        RegexOptions.Compiled);

    public static TuneLayout Read(string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);
        symbols ??= MsqIni.DefaultSymbols;

        var constants = new List<TuneConstant>();

        int pages = 0;
        bool little = false;
        int blocking = 0;
        int page = 0;
        int interWrite = 0;
        int afterBurn = 0;

        List<int> sizes = [];
        List<string> identifiers = [];
        List<string> readCommands = [];
        List<string> chunkWrites = [];
        List<string> burns = [];

        foreach (string raw in MsqIni.Section(iniText, "Constants", symbols))
        {
            string line = MsqIni.Strip(raw);
            if (line.Length == 0) continue;

            if (Bits.Match(line) is { Success: true } bits)
            {
                constants.Add(FromBits(bits, page));
                continue;
            }

            if (Array.Match(line) is { Success: true } array)
            {
                if (FromArray(array, page) is { } constant) constants.Add(constant);
                continue;
            }

            if (Scalar.Match(line) is { Success: true } scalar)
            {
                if (FromScalar(scalar, page) is { } constant) constants.Add(constant);
                continue;
            }

            if (Setting.Match(line) is not { Success: true } setting) continue;

            string value = setting.Groups["value"].Value;

            switch (setting.Groups["name"].Value)
            {
                // Constants after this line belong to the page it names. Pages
                // are numbered from one in the file and from zero everywhere
                // else, which is a good way to read a table off by 1024 bytes.
                case "page":
                    page = Math.Max(0, Whole(value) - 1);
                    break;

                case "nPages": pages = Whole(value); break;
                case "pageSize": sizes = [.. List(value).Select(Whole)]; break;
                case "pageIdentifier": identifiers = [.. List(value).Select(Unquote)]; break;
                case "pageReadCommand": readCommands = [.. List(value).Select(Unquote)]; break;
                case "pageChunkWrite": chunkWrites = [.. List(value).Select(Unquote)]; break;
                case "burnCommand": burns = [.. List(value).Select(Unquote)]; break;
                case "blockingFactor": blocking = Whole(value); break;

                // Timings the firmware asks for and TunerStudio observes. Both
                // are about giving a controller time to finish what it was told
                // to do before it is told anything else.
                case "interWriteDelay": interWrite = Whole(value); break;
                case "pageActivationDelay": afterBurn = Whole(value); break;

                case "endianness":
                    little = value.Trim().StartsWith("little", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        if (pages == 0) pages = Math.Max(sizes.Count, 1);

        var built = new List<TunePage>(pages);
        for (int i = 0; i < pages; i++)
        {
            int size = At(sizes, i);
            if (size <= 0) continue;

            built.Add(new TunePage
            {
                Index = i,
                Size = size,
                Identifier = At(identifiers, i) ?? "",
                ReadCommand = At(readCommands, i) ?? "",
                ChunkWriteCommand = At(chunkWrites, i) ?? "",
                BurnCommand = At(burns, i) ?? "",
            });
        }

        return new TuneLayout
        {
            Pages = built,
            Constants = constants,
            LittleEndian = little,
            BlockingFactor = blocking,
            InterWriteDelay = interWrite,
            AfterBurnDelay = afterBurn,
        };
    }

    /// <summary>
    /// The value for one page, from a list that may hold one entry for all of
    /// them. An INI states a per-page list where the pages differ and a single
    /// value where they do not.
    /// </summary>
    private static T? At<T>(List<T> values, int index) =>
        values.Count == 0 ? default
        : values.Count == 1 ? values[0]
        : index < values.Count ? values[index]
        : default;

    private static int At(List<int> values, int index) =>
        values.Count == 0 ? 0
        : values.Count == 1 ? values[0]
        : index < values.Count ? values[index]
        : 0;

    private static TuneConstant? FromScalar(Match match, int page)
    {
        if (!Enum.TryParse(match.Groups["type"].Value, ignoreCase: true, out RealtimeType type)) return null;

        string[] rest = Fields(match.Groups["rest"].Value);

        return new TuneConstant
        {
            Name = match.Groups["name"].Value,
            Page = page,
            Offset = Whole(match.Groups["offset"].Value),
            Type = type,
            Units = rest.Length > 0 ? Unquote(rest[0]) : "",
            Scale = rest.Length > 1 ? Number(rest[1], 1) : 1,
            Transform = rest.Length > 2 ? Number(rest[2], 0) : 0,

            // Fields four and five are the firmware's own limits, in displayed
            // units. Read rather than skipped over: they are what an edit should
            // be held to, and they are stricter than the datatype.
            Low = rest.Length > 3 ? Number(rest[3], double.NaN) : double.NaN,
            High = rest.Length > 4 ? Number(rest[4], double.NaN) : double.NaN,
            Digits = rest.Length > 5 ? (int)Number(rest[5], 0) : 0,
        };
    }

    private static TuneConstant? FromArray(Match match, int page)
    {
        if (!Enum.TryParse(match.Groups["type"].Value, ignoreCase: true, out RealtimeType type)) return null;

        string[] rest = Fields(match.Groups["rest"].Value);

        // "[16x16]" is columns by rows; "[16]" is a single run, which is what a
        // set of axis breakpoints is.
        int columns = Whole(match.Groups["cols"].Value);
        int rows = match.Groups["rows"].Success ? Whole(match.Groups["rows"].Value) : 1;

        if (columns < 1 || rows < 1) return null;

        return new TuneConstant
        {
            Name = match.Groups["name"].Value,
            Page = page,
            Offset = Whole(match.Groups["offset"].Value),
            Type = type,
            Columns = columns,
            Rows = rows,
            Units = rest.Length > 0 ? Unquote(rest[0]) : "",
            Scale = rest.Length > 1 ? Number(rest[1], 1) : 1,
            Transform = rest.Length > 2 ? Number(rest[2], 0) : 0,

            // Fields four and five are the firmware's own limits, in displayed
            // units. Read rather than skipped over: they are what an edit should
            // be held to, and they are stricter than the datatype.
            Low = rest.Length > 3 ? Number(rest[3], double.NaN) : double.NaN,
            High = rest.Length > 4 ? Number(rest[4], double.NaN) : double.NaN,
            Digits = rest.Length > 5 ? (int)Number(rest[5], 0) : 0,
        };
    }

    private static TuneConstant FromBits(Match match, int page)
    {
        Enum.TryParse(match.Groups["type"].Value, ignoreCase: true, out RealtimeType type);

        int low = Whole(match.Groups["low"].Value);
        int high = Whole(match.Groups["high"].Value);

        return new TuneConstant
        {
            Name = match.Groups["name"].Value,
            Page = page,
            Offset = Whole(match.Groups["offset"].Value),
            Type = type,
            BitLow = Math.Min(low, high),
            BitHigh = Math.Max(low, high),
        };
    }

    /// <summary>Splits a comma list, respecting quotes and braces.</summary>
    private static string[] Fields(string text)
    {
        var fields = new List<string>();
        int start = 0;
        int braces = 0;
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '{') braces++;
            else if (!quoted && c == '}') braces--;
            else if (c == ',' && !quoted && braces <= 0)
            {
                fields.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        fields.Add(text[start..].Trim());

        return [.. fields];
    }

    private static IEnumerable<string> List(string value) => Fields(value).Where(f => f.Length > 0);

    private static int Whole(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static double Number(string text, double fallback) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }
}
