using System.Globalization;
using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>
/// One setting as MTune declares it: what it is called, where it lives and how
/// to read it.
/// </summary>
/// <param name="Name">MTune's own name, which is what a tuner will recognise.</param>
/// <param name="Kind">The declared type, verbatim — see <see cref="MaxxSettingDefinition.IsTable"/>.</param>
/// <param name="Address">Byte offset into the tune blob.</param>
/// <param name="Length">
/// Elements, or for a table the largest it may be. A table's real size is in the
/// ECU rather than here, so this is a ceiling and not a shape.
/// </param>
/// <param name="Scale">Multiplies the stored number to give the value shown.</param>
/// <param name="Low">The smallest value the firmware says this holds, or NaN.</param>
/// <param name="High">The largest, or NaN.</param>
public sealed record MaxxSettingDefinition(
    string Name,
    string Kind,
    int Address,
    int Length,
    double Scale,
    double Low,
    double High)
{
    /// <summary>A table, whose axes and cells are found through the ECU rather than here.</summary>
    public bool IsTable => Kind is "dynamicTable";

    /// <summary>
    /// True where the stored number is signed.
    ///
    /// Taken from the declared minimum for the types that do not say. A list is
    /// written <c>type="list"</c> with no element type at all, so the only thing
    /// distinguishing an 18-point temperature curve running to −40 from one
    /// running to 65,496 is that the firmware says its minimum is below zero.
    /// Read unsigned, that curve's first four points come out at 6,513 °C.
    /// </summary>
    public bool IsSigned => Kind switch
    {
        "int8" or "int16" or "int32" => true,
        "uint8" or "uint16" or "uint32" => false,
        _ => Low < 0,
    };

    /// <summary>Bytes one element occupies.</summary>
    public int ElementSize => Kind switch
    {
        "uint8" or "int8" or "string" => 1,
        "int32" or "uint32" => 4,
        _ => 2,
    };

    /// <summary>What this is as the tune model spells types.</summary>
    public RealtimeType Type => (ElementSize, IsSigned) switch
    {
        (1, true) => RealtimeType.S08,
        (1, false) => RealtimeType.U08,
        (4, true) => RealtimeType.S32,
        (4, false) => RealtimeType.U32,
        (_, true) => RealtimeType.S16,
        _ => RealtimeType.U16,
    };

    /// <summary>
    /// Bytes the whole setting occupies in the blob's linear address space.
    ///
    /// A <c>dynamicTable</c> entry is the one exception: what sits at its
    /// address is a fixed-size config struct — how many columns and rows,
    /// where the axis and cell data actually live elsewhere in the blob — not
    /// the cells themselves, which is what <see cref="ElementSize"/> times
    /// <see cref="Length"/> would compute instead (a table's <c>length</c>
    /// attribute states the cell grid's own dimensions, e.g. "22,22", and
    /// falls through <see cref="ElementSize"/>'s switch to a default of 2, so
    /// the miscomputed size was 968 bytes for a 22×22 table against a real
    /// footprint of <see cref="MaxxTune.TableConfigSize"/>).
    ///
    /// This matters past the one table itself: <see cref="MaxxTuneDefinitions.Read"/>
    /// advances its cursor by every entry's <see cref="Size"/> to find the
    /// next implicit address, and two thirds of the file's settings rely on
    /// that cursor rather than stating an address of their own — so getting
    /// this wrong for one table silently mis-binds every setting after it
    /// until the next one that states its address outright.
    /// </summary>
    public int Size => IsTable ? MaxxTune.TableConfigSize : ElementSize * Length;
}

/// <summary>
/// MTune's settings definitions — the file that turns a MaxxECU's tune blob from
/// 64 KB of numbers into named tables and settings.
///
/// This is a TunerStudio INI in all but name and file format. It declares 9,975
/// settings, each with a name, a type, a scale, the range the firmware will
/// accept and — the part that matters — a byte address in the blob. Nothing here
/// is guessed: the addresses are the ECU's own, published by the people who
/// built it, and reading a tune with them is the same act as reading a
/// MegaSquirt with its INI.
///
/// It ships with MTune and is absent on a machine that has never had it. That is
/// the one thing standing between a MaxxECU and its tune, which is why
/// <see cref="Find"/> is a search rather than a constant.
///
/// Two details the file will trip you on. Addresses are mostly implied: 658 of
/// the settings state one and the rest follow on from whatever came before, so
/// the file has to be read in order, from the top, with a running cursor — take
/// only the ones that state an address and everything else lands nowhere. And
/// the whole file declares itself <c>Windows-1252</c>, which .NET will not load
/// by path without a code-page provider, so it is read as text first.
/// </summary>
public static class MaxxTuneDefinitions
{
    /// <summary>
    /// Where MTune keeps it, if MTune is installed.
    ///
    /// Beside the realtime definitions <see cref="MaxxGauges.FindDefinitions"/>
    /// looks for, and found the same way.
    /// </summary>
    public static string? Find()
    {
        string? realtime = MaxxGauges.FindDefinitions();

        if (realtime is null) return null;

        string beside = Path.Combine(
            Path.GetDirectoryName(realtime) ?? "", "ecuSettingsDefinitions.xml");

        return File.Exists(beside) ? beside : null;
    }

    /// <summary>
    /// Reads the file, in order, resolving the implied addresses as it goes.
    /// </summary>
    /// <returns>
    /// Every setting, in declaration order. Empty where the file is missing or
    /// unreadable — a MaxxECU without MTune installed still logs, it just has no
    /// tune to show.
    /// </returns>
    public static IReadOnlyList<MaxxSettingDefinition> Read(string? path)
    {
        if (path is null || !File.Exists(path)) return [];

        XDocument document;

        try
        {
            document = SafeXml.Parse(TuningText.Read(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Xml.XmlException)
        {
            return [];
        }

        var found = new List<MaxxSettingDefinition>();
        int cursor = 0;

        foreach (XElement element in document.Descendants()
                     .Where(e => e.Name.LocalName == "ECUSetting"))
        {
            string name = (string?)element.Attribute("name") ?? "";
            if (name.Length == 0) continue;

            string kind = (string?)element.Attribute("type") ?? "uint8";
            int length = Length((string?)element.Attribute("length"));

            var definition = new MaxxSettingDefinition(
                name,
                kind,
                At((string?)element.Attribute("address"), cursor),
                length,
                Number(element.Attribute("scale")) ?? 1,
                Number(element.Attribute("minValue")) ?? double.NaN,
                Number(element.Attribute("maxValue")) ?? double.NaN);

            found.Add(definition);

            // The cursor moves by what this setting occupies whether or not it
            // stated an address, because the next one may be relying on it.
            cursor = definition.Address + definition.Size;
        }

        return found;
    }

    /// <summary>
    /// The stated address, or where the last one finished.
    ///
    /// "seq" says so outright; most say nothing at all, which means the same.
    /// </summary>
    private static int At(string? address, int cursor) =>
        address is not null && int.TryParse(address, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int given)
            ? given
            : cursor;

    /// <summary>
    /// How many elements. A table writes its two dimensions as "22,22", and the
    /// product is what it occupies at most.
    /// </summary>
    private static int Length(string? text)
    {
        if (text is null) return 1;

        int total = 1;

        foreach (string part in text.Split(','))
            total *= int.TryParse(part.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int n) && n > 0
                ? n
                : 1;

        return total;
    }

    private static double? Number(XAttribute? attribute) =>
        attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
}
