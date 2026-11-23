using System.Globalization;
using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>What MTune says one channel is.</summary>
public sealed record MaxxChannelDefinition(
    int Index, string Name, string Units, double Scale, bool IsSigned, int Digits);

/// <summary>
/// MTune's channel definitions, read in full.
///
/// <see cref="MaxxChannelTable"/> reads the same file for units alone, which is
/// all a log needs; a USB session needs the rest. It discovers its channels from
/// the ECU rather than being told them in advance — the stream names every value
/// by index — so it arrives holding a hundred-odd numbers and nothing else, and
/// this is what turns them into "Battery voltage, volts, two decimals".
///
/// Nothing here is invented. Where MTune is not installed the caller is left
/// with the index as the name, which is a poor label and an honest one; a guess
/// from the value would be neither.
/// </summary>
public static class MaxxChannelDefinitions
{
    private static readonly Lock Gate = new();
    private static string? _from;
    private static IReadOnlyDictionary<int, MaxxChannelDefinition>? _cached;

    /// <summary>
    /// Every channel MTune defines, by index, or empty where it is not
    /// installed.
    ///
    /// Read once per path: the file is a quarter of a megabyte and does not
    /// change while the program runs.
    /// </summary>
    public static IReadOnlyDictionary<int, MaxxChannelDefinition> Read(string? path)
    {
        lock (Gate)
        {
            if (_cached is not null && _from == path) return _cached;

            _from = path;
            return _cached = Parse(path);
        }
    }

    private static Dictionary<int, MaxxChannelDefinition> Parse(string? path)
    {
        var found = new Dictionary<int, MaxxChannelDefinition>();

        if (path is null) return found;

        XDocument document;

        try
        {
            // Read and decode here rather than by path: the file declares
            // Windows-1252, which .NET does not carry by default, and loading it
            // by path throws rather than coping.
            document = SafeXml.Parse(TuningText.Read(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or System.Xml.XmlException)
        {
            // No definitions is a smaller loss than refusing to connect.
            return found;
        }

        foreach (XElement item in document.Descendants()
                     .Where(e => e.Name.LocalName == "ECURealtimeDataItem"))
        {
            if (!int.TryParse(item.Attribute("index")?.Value, out int index)) continue;

            string name = item.Attribute("name")?.Value ?? "";
            if (name.Length == 0) continue;

            string units = item.Attribute("unit")?.Value
                           ?? MaxxChannelTable.UnitForClass(item.Attribute("unitClass")?.Value);

            // Some units are a placeholder the tune fills in, which is worse
            // than nothing on an axis.
            if (units.StartsWith('{')) units = "";

            double scale = Number(item.Attribute("scale")?.Value, 1);

            // "int16" is signed and "uint16" is not, and the one contains the
            // other — so the test has to be for the u, not for the int.
            string type = item.Attribute("type")?.Value ?? "";
            bool signed = !type.StartsWith('u');

            found[index] = new MaxxChannelDefinition(
                index, name, units, scale, signed, DigitsFor(scale));
        }

        return found;
    }

    /// <summary>
    /// How many decimals a channel's scale implies — 0.01 is two, 1 is none.
    ///
    /// Taken from the scale rather than chosen, because the scale is exactly the
    /// statement of how much resolution the value has. Showing more is inventing
    /// precision the ECU did not send.
    /// </summary>
    internal static int DigitsFor(double scale)
    {
        if (scale is <= 0 or >= 1) return 0;

        int digits = 0;

        while (scale < 1 && digits < 6)
        {
            scale *= 10;
            digits++;
        }

        return digits;
    }

    private static double Number(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
}
