using System.Globalization;
using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>
/// Gauges for a MaxxECU, from MTune's own channel definitions.
///
/// A MaxxECU has no firmware INI, so there is nothing equivalent to the
/// <c>[GaugeConfigurations]</c> the other ECUs publish. MTune ships the next
/// best thing and arguably a better one: <c>ecuRealtimeDataDefinitions.xml</c>
/// defines all 1,856 channels with a name, a unit, a scale and limits. Taking
/// the dial ranges from there means they come from the same place the decode
/// does rather than from anything invented here.
/// </summary>
public static class MaxxGauges
{
    /// <summary>Where MTune installs its channel table.</summary>
    public static IReadOnlyList<string> DefaultPaths { get; } =
    [
        @"C:\Program Files (x86)\MaxxECU MTune\ecuRealtimeDataDefinitions.xml",
        @"C:\Program Files\MaxxECU MTune\ecuRealtimeDataDefinitions.xml",
    ];

    /// <summary>The channel table, or null when MTune is not installed.</summary>
    public static string? FindDefinitions()
    {
        foreach (string path in DefaultPaths)
        {
            try
            {
                if (File.Exists(path)) return path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Keep looking.
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a gauge for each subscribed channel.
    ///
    /// Falls back to a bare readout where MTune is absent or says nothing
    /// useful, rather than inventing a range — a dial with a made-up scale reads
    /// as a measurement.
    /// </summary>
    public static IReadOnlyList<GaugeSpec> For(
        IReadOnlyList<MaxxChannel> channels, string? definitionsPath)
    {
        ArgumentNullException.ThrowIfNull(channels);

        Dictionary<int, (double Low, double High)> limits = ReadLimits(definitionsPath);
        var gauges = new List<GaugeSpec>(channels.Count);

        foreach (MaxxChannel channel in channels)
        {
            (double low, double high) = limits.GetValueOrDefault(channel.Id, (0, 0));

            gauges.Add(new GaugeSpec
            {
                Name = channel.Name,
                Channel = channel.Name,
                Title = channel.Name,
                Units = channel.Units,
                Category = "MaxxECU",
                Low = low,
                High = high,
                ValueDigits = channel.Digits,
                LabelDigits = channel.Digits > 1 ? 1 : channel.Digits,
            });
        }

        return gauges;
    }

    /// <summary>
    /// Each channel's limits by index.
    ///
    /// No warning or danger bands: MTune's minValue and maxValue are the range a
    /// channel can hold, not the range it should stay inside, and treating them
    /// as thresholds would paint every reading as safe right up to the limit.
    /// </summary>
    /// <summary>Why the limits could not be read, for saying so rather than silently having none.</summary>
    public static string Problem { get; private set; } = "";

    private static Dictionary<int, (double, double)> ReadLimits(string? path)
    {
        var limits = new Dictionary<int, (double, double)>();
        Problem = "";

        if (path is null)
        {
            Problem = "MTune is not installed, so there are no ranges to draw dials over.";
            return limits;
        }

        XDocument document;

        try
        {
            // Read the bytes and decode them here rather than letting the parser
            // do it. The file declares Windows-1252, which .NET does not carry
            // by default — loading it by path throws "System does not support
            // 'Windows-1252' encoding", and catching that quietly leaves every
            // gauge without a scale for no visible reason. Parsing from a string
            // ignores the declaration, and the tolerant decode is the same one
            // firmware INIs get.
            document = XDocument.Parse(TuningText.Read(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            Problem = $"MTune's channel definitions could not be read: {e.Message}";
            return limits;
        }

        foreach (XElement item in document.Descendants()
                     .Where(e => e.Name.LocalName == "ECURealtimeDataItem"))
        {
            if (!TryNumber(item.Attribute("index")?.Value, out double index)) continue;
            if (!TryNumber(item.Attribute("minValue")?.Value, out double low)) continue;
            if (!TryNumber(item.Attribute("maxValue")?.Value, out double high)) continue;

            if (high > low) limits[(int)index] = (low, high);
        }

        return limits;
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
