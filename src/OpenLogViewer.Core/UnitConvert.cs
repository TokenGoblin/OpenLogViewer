namespace OpenLogViewer.Core;

/// <summary>Which units to show readings in, whatever the ECU reports them in.</summary>
public enum UnitSystem
{
    /// <summary>Exactly what the ECU said, converted not at all.</summary>
    AsReported,

    Metric,

    Imperial,
}

/// <summary>
/// Showing a reading in the units someone thinks in.
///
/// An ECU reports what it reports and there is no arguing with it: OBD2 is
/// metric by standard, a MegaSquirt is whatever its tune was set to, and a car
/// with a mile-marked speedometer will happily report km/h all day. Converting
/// is a display decision and is treated as one — nothing here touches what is
/// recorded, so a log is always in the units its ECU used and reopening it later
/// cannot double-convert.
///
/// Only families where the conversion is exact and the unit is unambiguous.
/// Guessing at a unit string nobody defined would put a wrong number on a gauge,
/// which is worse than an unfamiliar one.
/// </summary>
public static class UnitConvert
{
    private enum Measure
    {
        None,
        Celsius, Fahrenheit,
        KilometresPerHour, MilesPerHour,
        Kilopascals, PoundsPerSquareInch,
    }

    /// <summary>A reading converted for display, or unchanged if it does not apply.</summary>
    public static double Value(double value, string units, UnitSystem to)
    {
        if (double.IsNaN(value)) return value;

        return (Identify(units), to) switch
        {
            (Measure.Celsius, UnitSystem.Imperial) => (value * 9 / 5) + 32,
            (Measure.Fahrenheit, UnitSystem.Metric) => (value - 32) * 5 / 9,
            (Measure.KilometresPerHour, UnitSystem.Imperial) => value / MilesToKilometres,
            (Measure.MilesPerHour, UnitSystem.Metric) => value * MilesToKilometres,
            (Measure.Kilopascals, UnitSystem.Imperial) => value / KilopascalsPerPsi,
            (Measure.PoundsPerSquareInch, UnitSystem.Metric) => value * KilopascalsPerPsi,
            _ => value,
        };
    }

    /// <summary>
    /// The other way: a number someone typed in the units they are being shown,
    /// back into the units the log is recorded in.
    ///
    /// Needed wherever a value is entered rather than displayed. Everything a
    /// log holds stays in the ECU's own units — a pinned scale included, so that
    /// it means the same thing whichever system is being shown — but a person
    /// reading an axis labelled °F and typing 220 into a box beside it means
    /// 220 °F, and storing that as 220 °C is a silent factor of two.
    ///
    /// Exactly inverts <see cref="Value"/>: same families, same constants, each
    /// conversion the algebraic reverse of its counterpart.
    /// </summary>
    public static double ToReported(double value, string units, UnitSystem from)
    {
        if (double.IsNaN(value)) return value;

        return (Identify(units), from) switch
        {
            (Measure.Celsius, UnitSystem.Imperial) => (value - 32) * 5 / 9,
            (Measure.Fahrenheit, UnitSystem.Metric) => (value * 9 / 5) + 32,
            (Measure.KilometresPerHour, UnitSystem.Imperial) => value * MilesToKilometres,
            (Measure.MilesPerHour, UnitSystem.Metric) => value / MilesToKilometres,
            (Measure.Kilopascals, UnitSystem.Imperial) => value * KilopascalsPerPsi,
            (Measure.PoundsPerSquareInch, UnitSystem.Metric) => value / KilopascalsPerPsi,
            _ => value,
        };
    }

    /// <summary>The unit label to show alongside it.</summary>
    public static string Label(string units, UnitSystem to)
    {
        Measure measure = Identify(units);

        return (measure, to) switch
        {
            (Measure.Celsius, UnitSystem.Imperial) => "°F",
            (Measure.Fahrenheit, UnitSystem.Metric) => "°C",
            (Measure.KilometresPerHour, UnitSystem.Imperial) => "mph",
            (Measure.MilesPerHour, UnitSystem.Metric) => "km/h",
            (Measure.Kilopascals, UnitSystem.Imperial) => "psi",
            (Measure.PoundsPerSquareInch, UnitSystem.Metric) => "kPa",
            _ => units,
        };
    }

    /// <summary>Whether a reading in these units would change at all.</summary>
    public static bool Converts(string units, UnitSystem to) =>
        !string.Equals(Label(units, to), units, StringComparison.Ordinal);

    /// <summary>Exact by definition: a mile is 1,609.344 metres.</summary>
    private const double MilesToKilometres = 1.609344;

    /// <summary>
    /// Also exact by definition, following from the pound-force and the inch.
    ///
    /// Absolute against absolute. A MegaSquirt reports manifold pressure as an
    /// absolute figure and this converts it to an absolute one — 100 kPa becomes
    /// 14.5 psi, not −0.2 psi of boost. Gauge pressure is a different quantity
    /// with a different zero, and turning one into the other silently would put
    /// an atmosphere of error on a boost reading.
    /// </summary>
    private const double KilopascalsPerPsi = 6.894757293168361;

    /// <summary>
    /// What a unit string is measuring, where that is beyond doubt.
    ///
    /// The doubtful cases are the point of this. "degrees" is ignition advance
    /// on every ECU here and must never be read as a temperature; a bare "°" is
    /// OBD2's timing advance, and the same. Speeduino writes the literal word
    /// "TEMP" as a placeholder standing for whichever scale its tune is set to,
    /// so it says nothing this could act on. All three fall through unconverted,
    /// which is the only safe answer when the unit is not stated.
    /// </summary>
    private static Measure Identify(string? units)
    {
        if (units is null) return Measure.None;

        string name = Simplify(units);

        return name switch
        {
            "c" or "degc" or "degreesc" or "celsius" or "centigrade" => Measure.Celsius,
            "f" or "degf" or "degreesf" or "fahrenheit" => Measure.Fahrenheit,
            "km/h" or "kmh" or "kph" or "kmph" or "kilometresperhour" => Measure.KilometresPerHour,
            "mph" or "mi/h" or "milesperhour" => Measure.MilesPerHour,
            "kpa" or "kilopascals" or "kilopascal" => Measure.Kilopascals,
            "psi" or "lbf/in2" or "poundspersquareinch" => Measure.PoundsPerSquareInch,
            _ => Measure.None,
        };
    }

    /// <summary>
    /// A unit string reduced to something comparable — lowercase, with the
    /// degree sign, spaces, dots and underscores taken out, because every INI
    /// writes these differently and all of "deg F", "°F" and "degF" appear in
    /// firmware shipped by the same people.
    /// </summary>
    private static string Simplify(string units)
    {
        Span<char> buffer = stackalloc char[units.Length];
        int at = 0;

        foreach (char c in units)
        {
            if (c is '°' or ' ' or '.' or '_' or '\t') continue;

            buffer[at++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..at]);
    }
}
