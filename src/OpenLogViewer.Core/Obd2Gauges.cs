namespace OpenLogViewer.Core;

/// <summary>
/// Gauges for an OBD2 vehicle, from the standard's own definitions.
///
/// There is no firmware INI and no equivalent of one, and for once none is
/// needed: SAE J1979 fixes what each parameter means and the range its encoding
/// can hold, so the dials come from the same document the decode does.
///
/// No warning or danger bands. The standard says what a value is and never what
/// a safe one would be — it has no idea what this engine's coolant should read —
/// and a scale painted green to the last degree would be asserting something
/// nobody has said.
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
