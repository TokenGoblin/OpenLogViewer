using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>
/// MTune's channel definitions, keyed by the index a MaxxECU uses everywhere.
///
/// The same file the live gauges take their ranges from. A log header names its
/// channels but gives no units; the index in brackets after each name is what
/// this is keyed by, so "Coolant temp [18]" can be labelled °C from the
/// definitions rather than from a guess about the word "temp".
/// </summary>
public static class MaxxChannelTable
{
    private static readonly Lock Gate = new();
    private static IReadOnlyDictionary<int, string>? _units;

    /// <summary>
    /// Units by channel index, empty when MTune is not installed.
    ///
    /// Read once: the file is a quarter of a megabyte and never changes while
    /// the program is running.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Units()
    {
        lock (Gate)
        {
            if (_units is not null) return _units;

            var units = new Dictionary<int, string>();
            string? path = MaxxGauges.FindDefinitions();

            if (path is null) return _units = units;

            try
            {
                // Parsed from a decoded string rather than by path: the file
                // declares Windows-1252, which .NET does not carry by default.
                XDocument document = XDocument.Parse(TuningText.Read(path));

                foreach (XElement item in document.Descendants()
                             .Where(e => e.Name.LocalName == "ECURealtimeDataItem"))
                {
                    if (!int.TryParse(item.Attribute("index")?.Value, out int index)) continue;

                    string unit = item.Attribute("unit")?.Value
                                  ?? UnitForClass(item.Attribute("unitClass")?.Value);

                    // Some are written as a placeholder the tune fills in, which
                    // is worse than nothing on an axis.
                    if (unit.Length > 0 && !unit.StartsWith('{')) units[index] = unit;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // No units is a smaller loss than refusing to open the log.
            }

            return _units = units;
        }
    }

    /// <summary>
    /// Units for the classes MTune names rather than spells out.
    ///
    /// Temperature, pressure and speed are given as a class because MTune
    /// displays them in whichever units the user has chosen. These are the ones
    /// the logs are actually written in.
    /// </summary>
    private static string UnitForClass(string? unitClass) => unitClass switch
    {
        "temp" => "deg C",
        "abspressure" => "kPa",
        "pressure" => "kPa",
        "speed" => "km/h",
        _ => "",
    };
}
