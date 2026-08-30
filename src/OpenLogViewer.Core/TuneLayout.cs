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

    /// <summary>
    /// What is added to the stored number <em>before</em> scaling:
    /// <c>value = (raw + Transform) × Scale</c>, and back the other way
    /// <c>raw = value ÷ Scale − Transform</c>.
    ///
    /// <para>
    /// That order is not the obvious one and getting it round the wrong way is
    /// silent, so here is the evidence. Speeduino declares its coolant axis
    /// <c>U08, 1.8, -22.23, -40, 215, "F"</c> and the controller stores coolant
    /// as °C + 40. The bytes read off a real one are 0, 30, 60, 70, 85, 100.
    /// Adding first gives −40.0, 14.0, 68.0, 86.0, 113.0, 140.0 °F — which is
    /// −40, −10, 20, 30, 45 and 60 °C, every one a round number, and −40 is
    /// where the two scales meet. Scaling first gives −22.2, 31.8, 85.8 and so
    /// on: no pattern, and a top end past the 215 the firmware declares.
    /// </para>
    /// <para>
    /// Three more say the same. MS2 declares a VE trim
    /// <c>S08, 0.09765625, 1024</c> reading 100% at rest — adding first gives
    /// exactly 100 at a raw zero, scaling first gives 1,024. MS3's second fuel
    /// temperature is <c>S16, 0.05555, -320, "°C"</c>, where scaling first puts
    /// a raw zero below absolute zero. rusEFI stores a fuel trim as a ratio,
    /// <c>F32, 100, -1, "%"</c>, so a ratio of 1.0 is nought per cent added and
    /// scaling first would call it 99. Across four firmwares not one constant
    /// is decided the other way.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// False for a setting that lives on this machine rather than in the ECU,
    /// which has no page to be written to.
    /// </summary>
    public bool OnController => Page >= 0;

    /// <summary>
    /// True for a setting holding characters rather than a number — a name the
    /// user gives an input. <see cref="Columns"/> is its length in bytes.
    /// </summary>
    public bool IsText { get; init; }

    /// <summary>
    /// What each value of a bit field means, in order from zero.
    ///
    /// The firmware names them — "Disabled", "Narrow Band", "Wide Band" — and
    /// without the names the setting is a number between nought and three with
    /// nothing to say which is which.
    ///
    /// A slot the firmware has not used is spelled "INVALID", or left empty, and
    /// is a value the user must not be offered: it is padding to fill the bit
    /// width out to a power of two, not a choice.
    ///
    /// Every slot is kept, empty ones included, because the position in this list
    /// <em>is</em> the number the ECU stores. Dropping the unused ones would
    /// renumber every option after the gap — offering "On" and writing the value
    /// that means something else.
    /// </summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    public bool HasOptions => Options.Count > 0;

    /// <summary>Whether a value names something the firmware actually does.</summary>
    public bool IsValidOption(int value) =>
        value >= 0 && value < Options.Count
        && Options[value].Length > 0
        && !Options[value].Equals("INVALID", StringComparison.OrdinalIgnoreCase);

    /// <summary>What this value is called, or the number where it has no name.</summary>
    public string OptionName(double value)
    {
        int index = (int)Math.Round(value);

        return index >= 0 && index < Options.Count
            ? Options[index]
            : value.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }
}

/// <summary>Everything needed to read an ECU's settings and make sense of them.</summary>
public sealed record TuneLayout
{
    public required IReadOnlyList<TunePage> Pages { get; init; }

    public required IReadOnlyList<TuneConstant> Constants { get; init; }

    /// <summary>
    /// Settings that live on this machine rather than in the ECU: gauge limits,
    /// axis maxima, the units a dialog prefers.
    ///
    /// Kept apart from <see cref="Constants"/> rather than flagged among them,
    /// deliberately. Every one of these has a name and a scale exactly like a
    /// real constant and nothing about it says it is not one — but it has no
    /// page and no offset, so anything that treated it as one would be writing
    /// to whatever happens to sit at offset zero of page zero. Two lists cannot
    /// make that mistake; one list and a flag could.
    /// </summary>
    public IReadOnlyList<TuneConstant> PcVariables { get; init; } = [];

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
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*bits\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*,\s*\[\s*(?<low>\d+)\s*:\s*(?<high>\d+)\s*\](?:\s*,\s*(?<rest>.*))?$""",
        RegexOptions.Compiled);

    /// <summary>
    /// A text setting: <c>scriptTableName1 = string, ASCII, 2168, 16</c> on the
    /// controller, or <c>sensor01Alias = string, ASCII, 20</c> on this machine,
    /// where there is no offset to give. Names a user assigns to an input, so it
    /// is a setting like any other even though it holds no number.
    /// </summary>
    private static readonly Regex Text = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*string\s*,\s*(?<encoding>\w+)\s*,\s*(?<first>\d+)\s*(?:,\s*(?<second>\d+))?""",
        RegexOptions.Compiled);

    private static readonly Regex Setting = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>.+?)\s*$""",
        RegexOptions.Compiled);

    public static TuneLayout Read(string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);
        symbols ??= MsqIni.DefaultSymbols;

        // The named option lists, needed before any bit field is read: a great
        // many of them name their values by pointing at one of these.
        IReadOnlyDictionary<string, IReadOnlyList<string>> defines = IniDefines.Read(iniText, symbols);

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
                constants.Add(FromBits(bits, page, defines));
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

            if (Text.Match(line) is { Success: true } text)
            {
                if (FromText(text, page) is { } constant) constants.Add(constant);
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
            PcVariables = ReadPcVariables(iniText, symbols),
            LittleEndian = little,
            BlockingFactor = blocking,
            InterWriteDelay = interWrite,
            AfterBurnDelay = afterBurn,
        };
    }

    /// <summary>
    /// A PC variable's scalar form, which is a constant's without the offset —
    /// there is nowhere in the ECU for it to be at.
    /// </summary>
    private static readonly Regex PcScalar = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*scalar\s*,\s*(?<type>[A-Z]\d\d)\s*(?:,\s*(?<rest>.*))?$""",
        RegexOptions.Compiled);

    private static readonly Regex PcBits = new(
        """^\s*(?<name>[A-Za-z_]\w*)\s*=\s*bits\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*\[\s*(?<low>\d+)\s*:\s*(?<high>\d+)\s*\](?:\s*,\s*(?<rest>.*))?$""",
        RegexOptions.Compiled);

    /// <summary>
    /// The <c>[PcVariables]</c> section, which is written almost like
    /// <c>[Constants]</c> and means something quite different.
    ///
    /// Given a page of −1, so that anything reaching for a page gets an obviously
    /// wrong answer rather than a plausible one.
    /// </summary>
    private static IReadOnlyList<TuneConstant> ReadPcVariables(
        string iniText, IReadOnlySet<string> symbols)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> defines = IniDefines.Read(iniText, symbols);
        var variables = new List<TuneConstant>();

        foreach (string raw in MsqIni.Section(iniText, "PcVariables", symbols))
        {
            string line = MsqIni.Strip(raw);
            if (line.Length == 0) continue;

            if (PcBits.Match(line) is { Success: true } bits)
            {
                Enum.TryParse(bits.Groups["type"].Value, ignoreCase: true, out RealtimeType bitType);

                int low = Whole(bits.Groups["low"].Value);
                int high = Whole(bits.Groups["high"].Value);

                variables.Add(new TuneConstant
                {
                    Name = bits.Groups["name"].Value,
                    Page = -1,
                    Offset = 0,
                    Type = bitType,
                    BitLow = Math.Min(low, high),
                    BitHigh = Math.Max(low, high),
                    Options = Named(bits.Groups["rest"].Value, defines),
                });

                continue;
            }

            if (Text.Match(line) is { Success: true } text)
            {
                if (FromText(text, -1) is { } variable) variables.Add(variable);
                continue;
            }

            if (PcScalar.Match(line) is not { Success: true } scalar) continue;
            if (!Enum.TryParse(scalar.Groups["type"].Value, ignoreCase: true, out RealtimeType type)) continue;

            string[] rest = Fields(scalar.Groups["rest"].Value);

            variables.Add(new TuneConstant
            {
                Name = scalar.Groups["name"].Value,
                Page = -1,
                Offset = 0,
                Type = type,
                Units = rest.Length > 0 ? Unquote(rest[0]) : "",
                Scale = rest.Length > 1 ? Number(rest[1], 1) : 1,
                Transform = rest.Length > 2 ? Number(rest[2], 0) : 0,
                Low = rest.Length > 3 ? Number(rest[3], double.NaN) : double.NaN,
                High = rest.Length > 4 ? Number(rest[4], double.NaN) : double.NaN,
                Digits = rest.Length > 5 ? (int)Number(rest[5], 0) : 0,
            });
        }

        return variables;
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

    /// <summary>
    /// A text setting. On the controller both an offset and a length are given;
    /// on this machine there is no offset, so the one number present is the
    /// length.
    /// </summary>
    private static TuneConstant? FromText(Match match, int page)
    {
        bool hasOffset = match.Groups["second"].Success;

        // On the controller an offset is not optional. Defaulting it to zero
        // would read and write the first bytes of the page, which belong to
        // whichever constants really live there.
        if (page >= 0 && !hasOffset) return null;

        return new TuneConstant
        {
            Name = match.Groups["name"].Value,
            Page = page,
            Offset = hasOffset ? Whole(match.Groups["first"].Value) : 0,
            Type = RealtimeType.U08,
            IsText = true,
            Columns = Whole(match.Groups[hasOffset ? "second" : "first"].Value),
        };
    }

    private static TuneConstant FromBits(
        Match match, int page,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? defines = null)
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
            Options = Named(match.Groups["rest"].Value, defines),
        };
    }

    /// <summary>
    /// The labels for a bit field's values, or none where it has none.
    ///
    /// A great many bit fields name nothing — every reserved bit, and every flag
    /// a firmware leaves as a plain number. Both splitters end by yielding
    /// whatever follows the last comma, so an empty list comes back as one blank
    /// label rather than as nothing, and a setting with one blank option is
    /// drawn as a list to pick from with nothing in it: unreadable and
    /// unsettable, where a number box would have worked. Blank labels *within* a
    /// list are kept, because the position of a label is the value it stands for.
    ///
    /// A list of nothing but blanks goes the same way — Speeduino spells one
    /// reserved field <c>[1:7], ""</c>, which is a seven-bit number with one
    /// empty label. Nothing in such a list names a value, so there is nothing to
    /// lose by having none.
    /// </summary>
    private static IReadOnlyList<string> Named(
        string rest, IReadOnlyDictionary<string, IReadOnlyList<string>>? defines)
    {
        if (string.IsNullOrWhiteSpace(rest)) return [];

        IReadOnlyList<string> named = defines is null
            ? [.. Fields(rest).Select(Unquote)]
            : IniDefines.Expand(rest, defines);

        return named.All(string.IsNullOrEmpty) ? [] : named;
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
