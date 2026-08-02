using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>How one value is laid out inside a realtime block.</summary>
public enum RealtimeType
{
    U08,
    S08,
    U16,
    S16,
    U32,
    S32,

    /// <summary>An IEEE single. rusEFI publishes most of its channels this way.</summary>
    F32,
}

/// <summary>
/// One channel in the ECU's realtime block: where it sits, and how to turn the
/// bytes into a number.
///
/// The same idea as an MLG channel descriptor, except the ECU does not send it —
/// it lives in the firmware's INI file, which is why one is needed to read a
/// live connection at all.
/// </summary>
public sealed record RealtimeField
{
    public required string Name { get; init; }

    public string Units { get; init; } = "";

    public required RealtimeType Type { get; init; }

    /// <summary>Byte offset into the realtime block.</summary>
    public required int Offset { get; init; }

    public double Scale { get; init; } = 1;

    /// <summary>Added after scaling.</summary>
    public double Transform { get; init; }

    public int Digits { get; init; }

    /// <summary>Lowest bit of a packed field, or -1 when the field is a whole scalar.</summary>
    public int BitLow { get; init; } = -1;

    public int BitHigh { get; init; } = -1;

    public bool IsBitField => BitLow >= 0;

    public int Size => Type switch
    {
        RealtimeType.U08 or RealtimeType.S08 => 1,
        RealtimeType.U16 or RealtimeType.S16 => 2,
        _ => 4,
    };

    /// <summary>
    /// A float carries its own precision, so a scale of 1 says nothing about how
    /// many decimals to show. Three is what rusEFI's own datalog definition uses
    /// for most of them.
    /// </summary>
    public const int FloatDigits = 3;
}

/// <summary>A channel the INI derives from others rather than reading directly.</summary>
public sealed record RealtimeExpression(string Name, string Units, string Expression);

/// <summary>One line of the INI's datalog definition: internal name to log label.</summary>
public sealed record DatalogEntry(string Channel, string Label, int Digits);

/// <summary>Everything needed to ask an ECU for a realtime block and decode it.</summary>
public sealed record RealtimeLayout
{
    public required int BlockSize { get; init; }

    /// <summary>The INI's command template, e.g. "A" or "r\$tsCanId\x07%2o%2c".</summary>
    public required string GetCommand { get; init; }

    public required IReadOnlyList<RealtimeField> Fields { get; init; }

    /// <summary>Derived channels, in the order the INI declares them.</summary>
    public required IReadOnlyList<RealtimeExpression> Expressions { get; init; }

    /// <summary>Definitions that could not be read, for reporting rather than silence.</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    /// <summary>
    /// Byte order of the realtime block, from the INI's <c>endianness</c>.
    ///
    /// MegaSquirt runs on a Freescale S12 and is big-endian; rusEFI runs on an
    /// ARM and is little. It governs the offset and count in the request as much
    /// as the data in the reply — a firmware reads both the way its processor
    /// reads everything.
    /// </summary>
    public bool LittleEndian { get; init; }

    /// <summary>
    /// The most the firmware will put in one reply, from <c>blockingFactor</c>.
    /// Zero means the block is read whole.
    ///
    /// Worth honouring rather than treating as advice: asking a rusEFI for 1200
    /// bytes when it declares 1024 does not merely fail — the board drops off
    /// the USB bus and has to be replugged.
    /// </summary>
    public int BlockingFactor { get; init; }

    /// <summary>True when the command is the plain serial "A" rather than a CAN read.</summary>
    public bool UsesSimpleCommand => GetCommand.Trim('"').Equals("A", StringComparison.Ordinal);

    /// <summary>The request builder for this firmware's <see cref="GetCommand"/>.</summary>
    public RealtimeCommand Command => RealtimeCommand.Parse(GetCommand);
}

/// <summary>
/// Reads the <c>[OutputChannels]</c> section of a MegaSquirt INI.
///
/// A live connection sends a block of bytes and nothing else — no names, no
/// scaling, no layout. All of that comes from the INI that matches the
/// firmware, which is why a live session needs one and why the wrong one
/// produces plausible-looking nonsense rather than an error.
/// </summary>
public static class MsqIni
{
    /// <summary>
    /// Conditional symbols assumed when none are given. Chosen to match how a
    /// log from the same firmware reads: Fahrenheit and a wideband reporting
    /// AFR, which is what the sample logs show.
    /// </summary>
    public static IReadOnlySet<string> DefaultSymbols { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CAN_COMMANDS" };

    private static readonly Regex Scalar = new(
        """^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*scalar\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*(?:,\s*"(?<units>[^"]*)"\s*)?(?:,\s*(?<scale>[-+0-9.eE]+)\s*)?(?:,\s*(?<transform>[-+0-9.eE]+)\s*)?""",
        RegexOptions.Compiled);

    private static readonly Regex Bits = new(
        """^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*bits\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*,\s*\[\s*(?<low>\d+)\s*:\s*(?<high>\d+)\s*\]""",
        RegexOptions.Compiled);

    private static readonly Regex Expression = new(
        """^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*\{(?<body>[^}]*)\}\s*(?:,\s*"(?<units>[^"]*)")?""",
        RegexOptions.Compiled);

    private static readonly Regex Setting = new(
        """^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*(?<value>.+?)\s*$""",
        RegexOptions.Compiled);

    private static readonly Regex DatalogEntry = new(
        """^\s*entry\s*=\s*(?<channel>[A-Za-z_][\w]*)\s*,\s*"(?<label>[^"]*)"\s*(?:,\s*(?<type>\w+))?\s*(?:,\s*"(?<format>[^"]*)")?""",
        RegexOptions.Compiled);

    /// <summary>
    /// The <c>[Datalog]</c> section: which channels a log carries and what they
    /// are called in it.
    ///
    /// This is what makes a live session line up with a recorded one. The
    /// realtime block names things internally — "boostpsig", "batteryVoltage" —
    /// while a log calls them "Boost psi" and "Batt V". Using the log's names
    /// means a preset, a filter or a calculated channel written against a file
    /// works unchanged against the ECU.
    /// </summary>
    public static IReadOnlyList<DatalogEntry> ReadDatalog(string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        var entries = new List<DatalogEntry>();

        foreach (string raw in Section(iniText, "Datalog", symbols ?? DefaultSymbols))
        {
            if (DatalogEntry.Match(Strip(raw)) is not { Success: true } match) continue;

            entries.Add(new DatalogEntry(
                match.Groups["channel"].Value,
                match.Groups["label"].Value.Trim(),
                DigitsFromFormat(match.Groups["format"].Value)));
        }

        return entries;
    }

    /// <summary>Decimal places out of a printf format such as "%.3f".</summary>
    private static int DigitsFromFormat(string format)
    {
        if (format.Length == 0) return 0;

        int dot = format.IndexOf('.');
        if (dot < 0 || dot + 1 >= format.Length) return 0;

        return char.IsAsciiDigit(format[dot + 1]) ? format[dot + 1] - '0' : 0;
    }

    /// <summary>
    /// Output channels that are nothing but another channel under a second name,
    /// mapped to what they actually refer to.
    ///
    /// A common idiom: Speeduino publishes the throttle position at
    /// <c>tps</c> and then declares <c>throttle = { tps }</c> so the rest of the
    /// file can say the readable one. Its front-page throttle gauge names
    /// <c>throttle</c>, while its datalog records <c>tps</c> — so anything that
    /// pairs a gauge with a recorded column by name alone loses that gauge, even
    /// though the value is right there under the other name.
    ///
    /// Only a bare identifier counts. <c>{ tps }</c> is the same reading; <c>{
    /// tps * 2 }</c> is a different one, and following that as though it were an
    /// alias would put a wrong number on a dial.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadAliases(
        string iniText, IReadOnlySet<string>? symbols = null)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (RealtimeExpression expression in ReadOutputChannels(iniText, symbols).Expressions)
            if (BareName.IsMatch(expression.Expression))
                aliases[expression.Name] = expression.Expression;

        return aliases;
    }

    private static readonly Regex BareName =
        new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RealtimeLayout ReadOutputChannels(string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);
        symbols ??= DefaultSymbols;

        var fields = new List<RealtimeField>();
        var expressions = new List<RealtimeExpression>();
        var skipped = new List<string>();

        int blockSize = 0;
        string command = "A";

        foreach (string raw in Section(iniText, "OutputChannels", symbols))
        {
            string line = Strip(raw);
            if (line.Length == 0) continue;

            if (Bits.Match(line) is { Success: true } bits)
            {
                fields.Add(FromBits(bits));
                continue;
            }

            if (Scalar.Match(line) is { Success: true } scalar)
            {
                if (FromScalar(scalar) is { } field) fields.Add(field);
                else skipped.Add(line);
                continue;
            }

            if (Expression.Match(line) is { Success: true } expression)
            {
                expressions.Add(new RealtimeExpression(
                    expression.Groups["name"].Value,
                    expression.Groups["units"].Value,
                    expression.Groups["body"].Value.Trim()));
                continue;
            }

            if (Setting.Match(line) is not { Success: true } setting) continue;

            switch (setting.Groups["name"].Value)
            {
                case "ochBlockSize":
                    blockSize = ParseSize(setting.Groups["value"].Value);
                    break;

                case "ochGetCommand":
                    command = setting.Groups["value"].Value.Trim().Trim('"');
                    break;
            }
        }

        // The declared size wins where it is given; otherwise the furthest field
        // decides, so a layout still decodes rather than refusing to start.
        int needed = fields.Count == 0 ? 0 : fields.Max(f => f.Offset + f.Size);

        (bool little, int blocking) = ReadConstants(iniText, symbols);

        return new RealtimeLayout
        {
            BlockSize = Math.Max(blockSize, needed),
            GetCommand = command,
            Fields = fields,
            Expressions = expressions,
            Skipped = skipped,
            LittleEndian = little,
            BlockingFactor = blocking,
        };
    }

    /// <summary>
    /// The two things in <c>[Constants]</c> that decide how the realtime block is
    /// asked for and read: its byte order, and how much of it fits in one reply.
    ///
    /// They live in a different section from the channels they govern, which is
    /// why they are easy to miss — and missing them is not a subtle failure. The
    /// wrong byte order turns every reading into a different plausible number,
    /// and the wrong reply size takes a rusEFI off the USB bus altogether.
    /// </summary>
    private static (bool LittleEndian, int BlockingFactor) ReadConstants(
        string iniText, IReadOnlySet<string> symbols)
    {
        bool little = false;
        int blocking = 0;

        foreach (string raw in Section(iniText, "Constants", symbols))
        {
            if (Setting.Match(Strip(raw)) is not { Success: true } setting) continue;

            switch (setting.Groups["name"].Value)
            {
                case "endianness":
                    little = setting.Groups["value"].Value.Trim()
                        .StartsWith("little", StringComparison.OrdinalIgnoreCase);
                    break;

                case "blockingFactor":
                    blocking = ParseSize(setting.Groups["value"].Value);
                    break;
            }
        }

        return (little, blocking);
    }

    /// <summary>
    /// Yields the lines of one section, with <c>#if</c> blocks resolved against
    /// the given symbols. Conditionals matter: they decide whether a temperature
    /// arrives in Celsius or Fahrenheit, and picking the wrong branch scales
    /// every reading of it wrongly while still looking like a number.
    /// </summary>
    internal static IEnumerable<string> Section(string text, string name, IReadOnlySet<string> symbols)
    {
        bool inside = false;

        // One entry per open #if: whether this branch is live, and whether any
        // branch of it has been taken yet.
        var branches = new Stack<(bool Live, bool Taken)>();

        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith('['))
            {
                inside = trimmed.StartsWith($"[{name}]", StringComparison.OrdinalIgnoreCase);
                if (!inside && branches.Count > 0) branches.Clear();
                continue;
            }

            if (!inside) continue;

            if (trimmed.StartsWith("#if", StringComparison.Ordinal))
            {
                bool live = Holds(trimmed[3..], symbols);
                branches.Push((live, live));
                continue;
            }

            if (trimmed.StartsWith("#elif", StringComparison.Ordinal))
            {
                if (branches.Count == 0) continue;

                (bool _, bool taken) = branches.Pop();
                bool live = !taken && Holds(trimmed[5..], symbols);
                branches.Push((live, taken || live));
                continue;
            }

            if (trimmed.StartsWith("#else", StringComparison.Ordinal))
            {
                if (branches.Count == 0) continue;

                (bool _, bool taken) = branches.Pop();
                branches.Push((!taken, true));
                continue;
            }

            if (trimmed.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (branches.Count > 0) branches.Pop();
                continue;
            }

            if (branches.All(b => b.Live)) yield return line;
        }
    }

    private static bool Holds(string condition, IReadOnlySet<string> symbols)
    {
        string symbol = condition.Trim();
        bool negated = symbol.StartsWith('!');
        if (negated) symbol = symbol[1..].Trim();

        return symbols.Contains(symbol) ^ negated;
    }

    internal static string Strip(string line)
    {
        int comment = line.IndexOf(';');
        return (comment >= 0 ? line[..comment] : line).TrimEnd();
    }

    private static RealtimeField FromBits(Match match)
    {
        int low = int.Parse(match.Groups["low"].Value, CultureInfo.InvariantCulture);
        int high = int.Parse(match.Groups["high"].Value, CultureInfo.InvariantCulture);

        return new RealtimeField
        {
            Name = match.Groups["name"].Value,
            Type = ParseType(match.Groups["type"].Value),
            Offset = int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture),
            BitLow = Math.Min(low, high),
            BitHigh = Math.Max(low, high),
            Units = "",
            Scale = 1,
        };
    }

    private static RealtimeField? FromScalar(Match match)
    {
        if (!TryParseType(match.Groups["type"].Value, out RealtimeType type)) return null;

        double scale = Number(match.Groups["scale"], 1);
        double transform = Number(match.Groups["transform"], 0);

        return new RealtimeField
        {
            Name = match.Groups["name"].Value,
            Units = match.Groups["units"].Value,
            Type = type,
            Offset = int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture),
            Scale = scale,
            Transform = transform,
            Digits = type == RealtimeType.F32 ? RealtimeField.FloatDigits : DigitsFor(scale),
        };
    }

    private static double Number(Group group, double fallback) =>
        group.Success && double.TryParse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : fallback;

    /// <summary>
    /// Display precision inferred from the scale, since the INI does not state
    /// one: a channel scaled by 0.1 is worth one decimal, and one scaled by 1 is
    /// a whole number.
    /// </summary>
    private static int DigitsFor(double scale)
    {
        double magnitude = Math.Abs(scale);
        if (magnitude == 0 || magnitude >= 1) return 0;

        int digits = (int)Math.Ceiling(-Math.Log10(magnitude));
        return Math.Clamp(digits, 0, 4);
    }

    private static int ParseSize(string value)
    {
        string text = value.Trim().Trim('{', '}').Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) ? size : 0;
    }

    private static RealtimeType ParseType(string text) =>
        TryParseType(text, out RealtimeType type) ? type : RealtimeType.U08;

    private static bool TryParseType(string text, out RealtimeType type) =>
        Enum.TryParse(text, ignoreCase: true, out type);
}
