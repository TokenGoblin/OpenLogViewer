namespace OpenLogViewer.Core;

/// <summary>Fills <paramref name="into"/> with this PID's channels, in order.</summary>
public delegate void Obd2Decode(ReadOnlySpan<byte> data, Span<double> into);

/// <summary>One thing a PID reports: a name, a unit, and the range it can hold.</summary>
public sealed record Obd2Channel(string Name, string Units, int Digits, double Low, double High);

/// <summary>
/// A mode-01 parameter: what to ask for, how much comes back, and what it means.
/// </summary>
public sealed record Obd2Pid
{
    public required byte Pid { get; init; }

    /// <summary>Data bytes the standard defines for this PID, after the echoed mode and PID.</summary>
    public required int DataBytes { get; init; }

    public required IReadOnlyList<Obd2Channel> Channels { get; init; }

    public required Obd2Decode Decode { get; init; }

    /// <summary>
    /// Asked for on every cycle rather than in rotation.
    ///
    /// One request is one round trip and a dongle answers a few dozen a second at
    /// best, so asking for everything every time would drag the headline gauges
    /// down to the speed of the slowest thing on the list. The handful here are
    /// the ones a needle is expected to follow.
    /// </summary>
    public bool Hot { get; init; }

    public override string ToString() => $"01{Pid:X2}";
}

/// <summary>
/// The standard OBD2 parameters this reads, per SAE J1979.
///
/// The great advantage over every other ECU here is that none of this needs a
/// definition file: the numbering, the scaling and the units are the same on
/// every compliant vehicle, which is the entire point of the standard. What
/// differs is which parameters a given car answers to, and the car itself says
/// so — <c>0100</c>, <c>0120</c> and <c>0140</c> each return a bitmask of the
/// thirty-two that follow.
///
/// Ranges are the standard's own, so a dial's ends mean what the encoding can
/// hold. No warning or danger bands: OBD2 describes what a value is and never
/// what a safe one would be, and painting a scale green up to the last kPa would
/// be inventing a limit the car never stated.
/// </summary>
public static class Obd2Pids
{
    /// <summary>
    /// A tachometer's top.
    ///
    /// The one place the standard's range is no use: RPM is encoded to
    /// 16,383.75, which is the counter's ceiling and not any engine's, and a dial
    /// drawn to it leaves every real reading in the first quarter. This is a
    /// judgement rather than a measurement — OBD2 offers no way to ask a car for
    /// its redline — so it is stated here rather than buried in the table.
    /// </summary>
    public const double TachometerTop = 8000;

    /// <summary>Every parameter this knows how to decode, in PID order.</summary>
    public static IReadOnlyList<Obd2Pid> All { get; } =
    [
        new()
        {
            Pid = 0x01, DataBytes = 4, Channels =
            [
                new("MIL", "", 0, 0, 1),
                new("DTC Count", "", 0, 0, 127),
            ],
            Decode = (d, v) =>
            {
                v[0] = (d[0] & 0x80) != 0 ? 1 : 0;
                v[1] = d[0] & 0x7F;
            },
        },
        Single(0x04, "Engine Load", "%", 1, 0, 100, d => d[0] * 100.0 / 255, hot: true),
        Single(0x05, "Coolant", "°C", 0, -40, 215, d => d[0] - 40.0),
        Single(0x06, "Short Fuel Trim B1", "%", 1, -100, 99.2, Trim),
        Single(0x07, "Long Fuel Trim B1", "%", 1, -100, 99.2, Trim),
        Single(0x08, "Short Fuel Trim B2", "%", 1, -100, 99.2, Trim),
        Single(0x09, "Long Fuel Trim B2", "%", 1, -100, 99.2, Trim),
        Single(0x0A, "Fuel Pressure", "kPa", 0, 0, 765, d => d[0] * 3.0),
        Single(0x0B, "MAP", "kPa", 0, 0, 255, d => d[0], hot: true),
        Single(0x0C, "RPM", "rpm", 0, 0, TachometerTop, d => Word(d) / 4.0, bytes: 2, hot: true),
        Single(0x0D, "Speed", "km/h", 0, 0, 255, d => d[0], hot: true),
        Single(0x0E, "Timing Advance", "°", 1, -64, 63.5, d => d[0] / 2.0 - 64),
        Single(0x0F, "IAT", "°C", 0, -40, 215, d => d[0] - 40.0),
        Single(0x10, "MAF", "g/s", 2, 0, 655.35, d => Word(d) / 100.0, bytes: 2),
        Single(0x11, "Throttle Position", "%", 1, 0, 100, d => d[0] * 100.0 / 255, hot: true),
        Single(0x1F, "Run Time", "s", 0, 0, 65535, Word, bytes: 2),
        Single(0x21, "Distance with MIL", "km", 0, 0, 65535, Word, bytes: 2),
        Single(0x23, "Fuel Rail Pressure", "kPa", 0, 0, 655350, d => Word(d) * 10.0, bytes: 2),

        // Four bytes: an equivalence ratio and the sensor voltage. Only the ratio
        // is taken — the voltage is diagnostic detail about the sensor rather
        // than a reading about the engine.
        Single(0x24, "Lambda", "", 3, 0, 2, d => Word(d) / 32768.0, bytes: 4),

        Single(0x2F, "Fuel Level", "%", 1, 0, 100, d => d[0] * 100.0 / 255),
        Single(0x31, "Distance Since Cleared", "km", 0, 0, 65535, Word, bytes: 2),
        Single(0x33, "Barometric Pressure", "kPa", 0, 0, 255, d => d[0]),
        Single(0x42, "Battery", "V", 2, 0, 65.535, d => Word(d) / 1000.0, bytes: 2),
        Single(0x43, "Absolute Load", "%", 1, 0, 25700, d => Word(d) * 100.0 / 255, bytes: 2),
        Single(0x45, "Relative Throttle", "%", 1, 0, 100, d => d[0] * 100.0 / 255),
        Single(0x46, "Ambient Air Temp", "°C", 0, -40, 215, d => d[0] - 40.0),
        Single(0x5C, "Engine Oil Temp", "°C", 0, -40, 210, d => d[0] - 40.0),
        Single(0x5E, "Fuel Rate", "L/h", 2, 0, 3276.75, d => Word(d) / 20.0, bytes: 2),
    ];

    /// <summary>
    /// The PIDs that report which PIDs a car supports.
    ///
    /// Each returns four bytes, one bit per parameter in the range that follows
    /// it, most significant bit first. Asking is far better than the alternative
    /// of trying every parameter and waiting for "NO DATA" — a round trip apiece,
    /// on the slowest link in this application.
    /// </summary>
    public static IReadOnlyList<byte> SupportQueries { get; } = [0x00, 0x20, 0x40];

    /// <summary>
    /// Reads a support bitmask into the PID numbers it stands for.
    ///
    /// <paramref name="query"/> is the PID that was asked; its reply covers the
    /// thirty-two numbers after it. Bit 31 of the first byte is the first of
    /// them, so <c>0100</c> answering 0x80 means the car supports 0x01.
    /// </summary>
    public static IReadOnlyList<byte> SupportedBy(byte query, ReadOnlySpan<byte> mask)
    {
        var supported = new List<byte>();
        if (mask.Length < 4) return supported;

        for (int bit = 0; bit < 32; bit++)
        {
            bool set = (mask[bit / 8] & (0x80 >> (bit % 8))) != 0;
            if (set) supported.Add((byte)(query + bit + 1));
        }

        return supported;
    }

    /// <summary>The parameters this knows about, out of a set the car reports.</summary>
    public static IReadOnlyList<Obd2Pid> Known(IEnumerable<byte> supported)
    {
        ArgumentNullException.ThrowIfNull(supported);

        var wanted = new HashSet<byte>(supported);
        return [.. All.Where(p => wanted.Contains(p.Pid))];
    }

    private static double Word(ReadOnlySpan<byte> d) => (256 * d[0]) + d[1];

    /// <summary>Fuel trim: zero is no correction, and either sign is meaningful.</summary>
    private static double Trim(ReadOnlySpan<byte> d) => (d[0] / 1.28) - 100;

    private delegate double Scalar(ReadOnlySpan<byte> data);

    private static Obd2Pid Single(
        byte pid, string name, string units, int digits, double low, double high,
        Scalar decode, int bytes = 1, bool hot = false) =>
        new()
        {
            Pid = pid,
            DataBytes = bytes,
            Channels = [new Obd2Channel(name, units, digits, low, high)],
            Decode = (d, v) => v[0] = decode(d),
            Hot = hot,
        };
}
