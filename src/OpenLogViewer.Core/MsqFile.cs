using System.Globalization;
using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>
/// A saved tune: every setting an ECU held at the moment somebody saved it.
///
/// <para>
/// The file TunerStudio writes when you save, and the thing a tuner emails when
/// they say "here is your tune". It is the counterpart of a definition file:
/// the INI says what the settings are and where they live, and this says what
/// they were set to. Neither is useful without the other — an MSQ names its
/// constants and gives their values in the units a person reads, and says
/// nothing whatever about which byte any of them lives in.
/// </para>
/// <para>
/// <b>It carries the firmware's conditional symbols, and that is not a
/// footnote.</b> A definition is written once and compiled many ways: the same
/// file describes a Fahrenheit build and a Celsius one, and picking the wrong
/// branch scales every temperature wrongly while still looking like a number.
/// The tune knows which build it came from, and the five symbols in the file
/// this was written against are more than the one this application otherwise
/// assumes. Reading a tune's INI with the tune's own symbols is the only way
/// the two agree.
/// </para>
/// <para>
/// Values are held as the file wrote them rather than parsed here. What a value
/// means depends on the constant behind it — a bit field is stored as the label
/// of the option chosen, a name as a quoted string, everything else as the
/// number a person would read — and the constant is in the definition, which
/// this does not have.
/// </para>
/// </summary>
public sealed record MsqFile
{
    /// <summary>
    /// The firmware signature, which is what says <em>which</em> definition this
    /// belongs to. The same string an ECU answers with when asked who it is.
    /// </summary>
    public string Signature { get; init; } = "";

    /// <summary>The firmware's own version string, as written — percent-encoded.</summary>
    public string Firmware { get; init; } = "";

    /// <summary>Which program wrote it.</summary>
    public string Author { get; init; } = "";

    /// <summary>Whatever the tuner typed about this tune.</summary>
    public string Comment { get; init; } = "";

    /// <summary>When it was saved, as written. Left as text: the format is a Java date.</summary>
    public string WrittenAt { get; init; } = "";

    /// <summary>How many pages the file says it holds.</summary>
    public int PageCount { get; init; }

    /// <summary>
    /// The build's conditional symbols, for reading the definition with.
    ///
    /// Empty when the file names none, in which case a caller should keep
    /// whatever it would otherwise have used rather than reading the definition
    /// with no symbols at all.
    /// </summary>
    public IReadOnlySet<string> Symbols { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every setting in the controller, by name, as written.
    ///
    /// Keyed exactly, not loosely. MS2Extra has two different settings called
    /// <c>MAFFlow</c> and <c>mafflow</c> — a twelve-point flow curve and a
    /// sixty-four-point one, on different pages — and its own saved tunes store
    /// both. A dictionary that ignored case would keep one and lose the other.
    /// Use <see cref="Value"/> to look one up, which falls back to a loose match
    /// for the many firmwares that are not so careful.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// What the file says a setting is, spelled exactly or else near enough.
    /// </summary>
    public string? Value(string name)
    {
        if (name is null) return null;
        if (Values.TryGetValue(name, out string? exact)) return exact;

        foreach ((string written, string value) in Values)
            if (written.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;

        return null;
    }

    /// <summary>
    /// Settings belonging to the tuning program rather than to the ECU — gauge
    /// limits, the units a dialog prefers, the vehicle's weight.
    ///
    /// Kept apart for the same reason <see cref="TuneLayout.PcVariables"/> is:
    /// they have no page and no offset, and anything that treated one as a
    /// controller setting would be writing to whatever sits at offset zero.
    /// </summary>
    public IReadOnlyDictionary<string, string> PcVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True when the file held no settings at all.</summary>
    public bool IsEmpty => Values.Count == 0 && PcVariables.Count == 0;

    /// <summary>
    /// Reads one, or throws <see cref="LogFormatException"/> for a file that is
    /// not a tune.
    /// </summary>
    public static MsqFile Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;

        try
        {
            document = SafeXml.Parse(xml);
        }
        catch (System.Xml.XmlException e)
        {
            throw new LogFormatException($"This is not readable as XML: {e.Message}");
        }

        if (document.Root is not { } root || root.Name.LocalName != "msq")
            throw new LogFormatException("This is XML, but it is not a tune: no <msq> element.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var pc = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (XElement element in root.Descendants())
        {
            string? name = element.Attribute("name")?.Value;
            if (name is not { Length: > 0 }) continue;

            switch (element.Name.LocalName)
            {
                // First one wins. A file naming the same constant twice is
                // malformed, and taking the later would silently prefer whatever
                // happened to be written last.
                case "constant": values.TryAdd(name, element.Value); break;
                case "pcVariable": pc.TryAdd(name, element.Value); break;
                default: break;
            }
        }

        XElement? version = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "versionInfo");
        XElement? bibliography = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "bibliography");

        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (XElement setting in root.Descendants().Where(e => e.Name.LocalName == "setting"))
            if (setting.Attribute("name")?.Value is { Length: > 0 } symbol) symbols.Add(symbol);

        return new MsqFile
        {
            Signature = version?.Attribute("signature")?.Value ?? "",
            Firmware = version?.Attribute("firmwareInfo")?.Value ?? "",
            Author = bibliography?.Attribute("author")?.Value ?? "",
            Comment = bibliography?.Attribute("tuneComment")?.Value ?? "",
            WrittenAt = bibliography?.Attribute("writeDate")?.Value ?? "",
            PageCount = Whole(version?.Attribute("nPages")?.Value),
            Symbols = symbols,
            Values = values,
            PcVariables = pc,
        };
    }

    /// <summary>Reads one from disk, in whichever encoding it turns out to be.</summary>
    public static MsqFile ReadFile(string path) => Read(TuningText.Read(path));

    private static int Whole(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
