using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>
/// Reading a channel in the units the arithmetic wants, whatever the logger
/// wrote it in.
///
/// The quiet destroyer of anything computed across logs. A MegaSquirt logs MAP in
/// kilopascals and an American tune logs boost in psi; intake air is degrees C on
/// one controller and F on another. An estimate that assumes one of them is not
/// slightly wrong on the others — it is out by a factor of seven, or by forty
/// degrees of absolute temperature, and it produces a horsepower figure that
/// looks like a number rather than like a mistake.
///
/// Each method returns a fragment of expression rather than a value, because the
/// conversion has to happen inside the calculated channel where every sample goes
/// through it, not once on a summary.
/// </summary>
public static class ChannelUnits
{
    /// <summary>
    /// A pressure channel, in kilopascals.
    ///
    /// Absolute or gauge is not converted here and cannot be: the units say
    /// "psi", never "psi above atmosphere". That distinction belongs to whoever
    /// knows what the channel means.
    /// </summary>
    public static string ToKilopascals(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Scale(channel, Simplify(channel.Units) switch
        {
            "kpa" or "" => 1,
            "psi" or "psig" or "psia" => TuningMath.KpaPerPsi,
            "bar" => 100,
            "mbar" or "millibar" => 0.1,
            "inhg" or "hg" => 3.386389,
            "hpa" => 0.1,
            "pa" => 0.001,
            _ => 1,
        });
    }

    /// <summary>
    /// A temperature channel, in degrees Celsius.
    ///
    /// Fahrenheit needs an offset as well as a factor, which is why this cannot
    /// be a single multiplier like the rest.
    /// </summary>
    public static string ToCelsius(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        string name = Reference(channel);

        return Simplify(channel.Units) switch
        {
            "f" or "degf" or "fahrenheit" => $"(({name} - 32) * 5 / 9)",
            "k" or "kelvin" => $"({name} - 273.15)",
            _ => name,
        };
    }

    /// <summary>A temperature channel, in kelvin, which is what gas density needs.</summary>
    public static string ToKelvin(LogChannel channel) => $"({ToCelsius(channel)} + 273.15)";

    /// <summary>A mass flow channel, in grams per second.</summary>
    public static string ToGramsPerSecond(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Scale(channel, Simplify(channel.Units) switch
        {
            "g/s" or "gs" or "gps" or "" => 1,
            "kg/h" or "kgh" or "kg/hr" => 1000.0 / 3600,
            "kg/min" or "kgmin" => 1000.0 / 60,
            "lb/min" or "lbmin" or "lbsmin" => 453.59237 / 60,
            "lb/h" or "lbh" or "lb/hr" or "lbhr" => 453.59237 / 3600,
            "g/min" or "gmin" => 1.0 / 60,
            _ => 1,
        });
    }

    /// <summary>A time channel, in milliseconds — injector pulse widths are logged in both.</summary>
    public static string ToMilliseconds(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Scale(channel, Simplify(channel.Units) switch
        {
            "ms" or "msec" or "millisecond" or "milliseconds" or "" => 1,
            "s" or "sec" or "second" or "seconds" => 1000,
            "us" or "usec" or "microsecond" or "microseconds" => 0.001,
            _ => 1,
        });
    }

    /// <summary>
    /// A proportion channel as a fraction of one, whether it was logged as a
    /// percentage or already as a fraction.
    ///
    /// Told apart by the unit and not by the values, because a duty cycle that
    /// happens to sit under 1% all log is still a percentage.
    /// </summary>
    public static string ToFraction(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Scale(channel, Simplify(channel.Units) is "%" or "percent" or "pct" ? 0.01 : 1);
    }

    /// <summary>
    /// Whether a mixture channel is lambda rather than an air-fuel ratio.
    ///
    /// The unit decides where it says so. Where it does not — and plenty of
    /// firmware logs a bare number — the values do: lambda lives around one and
    /// an air-fuel ratio around fifteen, and nothing sensible is ambiguous
    /// between them. Guessing from a name would not work, since both are called
    /// "AFR" by somebody.
    /// </summary>
    public static bool IsLambda(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        switch (Simplify(channel.Units))
        {
            case "lambda" or "l": return true;
            case "afr" or ":1" or "ratio": return false;
        }

        // A typical value, taken as the middle of the range rather than the mean
        // so that a few samples of a sensor warming up cannot swing it.
        double middle = Typical(channel);

        return double.IsFinite(middle) && middle < 5;
    }

    /// <summary>A mixture channel expressed as an air-fuel ratio on the given fuel.</summary>
    public static string ToAirFuelRatio(LogChannel channel, Fuel fuel) =>
        IsLambda(channel)
            ? $"({Reference(channel)} * {Number(TuningMath.Stoichiometric(fuel))})"
            : Reference(channel);

    /// <summary>
    /// A representative value from a channel, for deciding what it holds.
    ///
    /// The midpoint of the values actually present, ignoring the ones that are
    /// not readings at all.
    /// </summary>
    internal static double Typical(LogChannel channel)
    {
        double low = double.PositiveInfinity;
        double high = double.NegativeInfinity;

        for (int i = 0; i < channel.Length; i++)
        {
            double v = channel.At(i);
            if (!double.IsFinite(v)) continue;

            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        return double.IsFinite(low) && double.IsFinite(high) ? (low + high) / 2 : double.NaN;
    }

    /// <summary>A channel by name, parenthesised where the name could run into what follows.</summary>
    public static string Reference(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Name;
    }

    /// <summary>
    /// One plausible reading of an unlabelled speed channel.
    /// </summary>
    /// <param name="Unit">What it would be called.</param>
    /// <param name="ToMetresPerSecond">What to multiply a reading by.</param>
    public readonly record struct SpeedUnit(string Unit, double ToMetresPerSecond);

    /// <summary>
    /// The units a road speed is plausibly logged in, commonest first.
    ///
    /// Offered as a list because a great many logs record a speed and say nothing
    /// about what it is in — and unlike a pressure or a temperature, the values
    /// cannot settle it: seventy is a perfectly ordinary reading in either miles
    /// or kilometres an hour. What can settle it is the gearing, which is known.
    /// Only one of these makes engine speed over road speed land on a gear the
    /// car actually has, so the caller tries each and keeps the one that fits.
    /// </summary>
    public static IReadOnlyList<SpeedUnit> SpeedUnits { get; } =
    [
        new("mph", 0.44704),
        new("km/h", 1 / 3.6),
        new("m/s", 1),
        new("kn", 0.514444),
        new("ft/s", 0.3048),
    ];

    /// <summary>
    /// What to multiply a speed channel by to get metres a second, or NaN where
    /// the channel does not say.
    ///
    /// NaN rather than a guess, deliberately. Assuming miles an hour on a log
    /// that was in kilometres understates every road speed by a factor of 1.6,
    /// which understates the power by the same and looks entirely believable
    /// while doing it.
    /// </summary>
    public static double SpeedToMetresPerSecond(LogChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Simplify(channel.Units) switch
        {
            "mph" or "mih" or "milesperhour" => 0.44704,
            "kph" or "kmh" or "kmph" or "kilometresperhour" => 1 / 3.6,
            "ms" or "mpers" or "metrespersecond" => 1,
            "kn" or "kt" or "kts" or "knot" or "knots" => 0.514444,
            "fts" or "fps" => 0.3048,
            _ => double.NaN,
        };
    }

    private static string Scale(LogChannel channel, double factor) =>
        Math.Abs(factor - 1) < 1e-12
            ? Reference(channel)
            : $"({Reference(channel)} * {Number(factor)})";

    /// <summary>
    /// A number as the expression parser will read it.
    ///
    /// Invariant, always: a decimal comma on a European machine turns one number
    /// into two arguments, and the expression either fails to parse or quietly
    /// means something else.
    /// </summary>
    public static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string Simplify(string units) => ChannelRoles.Simplify(units);
}
