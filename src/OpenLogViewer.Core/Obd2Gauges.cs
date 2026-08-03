namespace OpenLogViewer.Core;

/// <summary>
/// Gauges for an OBD2 vehicle, from the standard's own definitions.
///
/// There is no firmware INI and no equivalent of one, and for once none is
/// needed: SAE J1979 fixes what each parameter means and the range its encoding
/// can hold, so the dials come from the same document the decode does.
///
/// The scales are the standard's. The warning and danger bands are not, because
/// the standard has none to give: it says what a value is and never what a safe
/// one would be, having no idea what this engine's coolant should read. Those
/// are conventions — the figures a workshop manual would use on an ordinary car
/// — and the gauge list says so rather than letting a coloured arc pass for the
/// car's own opinion.
///
/// The exception is the malfunction lamp, where the standard's own word for the
/// state is "malfunction". A lit lamp being red is the car talking, not this.
/// </summary>
public static class Obd2Gauges
{
    /// <summary>A gauge for every channel of every parameter the car reports.</summary>
    public static IReadOnlyList<GaugeSpec> For(IReadOnlyList<Obd2Pid> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);

        var gauges = new List<GaugeSpec>();

        foreach (Obd2Pid pid in pids)
            foreach (Obd2Channel channel in pid.Channels)
                gauges.Add(new GaugeSpec
                {
                    Name = channel.Name,
                    Channel = channel.Name,
                    Title = channel.Name,
                    Units = channel.Units,

                    // Named for the parameter as well, because a table of
                    // thirty of them is easier to search by the number a
                    // service manual quotes than by a name this chose.
                    Category = $"OBD2 · {pid}",
                    Low = channel.Low,
                    High = channel.High,
                    LowDanger = channel.LowDanger,
                    LowWarning = channel.LowWarning,
                    HighWarning = channel.HighWarning,
                    HighDanger = channel.HighDanger,
                    ValueDigits = channel.Digits,
                    LabelDigits = channel.Digits > 1 ? 1 : channel.Digits,
                });

        return gauges;
    }

    /// <summary>
    /// The handful worth putting on screen before anything is chosen.
    ///
    /// A car reporting thirty parameters would otherwise open as thirty dials,
    /// most of them diagnostic counters. These are the ones a driver watches, in
    /// the order a dashboard puts them.
    /// </summary>
    public static IReadOnlyList<string> FrontPage { get; } =
    [
        "RPM", "Speed", "Throttle Position", "Engine Load",
        "MAP", "Coolant", "IAT", "Battery",
    ];
}
