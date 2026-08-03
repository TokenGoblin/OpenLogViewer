namespace OpenLogViewer.Core;

/// <summary>A job a channel does, whatever its firmware chose to call it.</summary>
public enum ChannelRole
{
    EngineSpeed,
    Coolant,
    Throttle,
    Mixture,
    MixtureTarget,
    ManifoldPressure,

    /// <summary>Whether the ECU is cutting fuel, however it says so.</summary>
    FuelCut,
}

/// <summary>
/// Finding the channel that does a particular job.
///
/// Every firmware names these differently and none of them is wrong: a
/// MegaSquirt logs CLT and TPS, a MaxxECU logs "Throttle position", an OBD2 car
/// logs "Coolant" and "Throttle Position". Anything that looks for one spelling
/// works on one make of controller and silently does nothing on the others —
/// which is what happened to the suggested filters, offering none at all on an
/// OBD2 log and no throttle filter on a MaxxECU.
///
/// Names are matched whole before they are matched loosely, and the units have
/// to suit the job. Without that, a MaxxECU's "TPS input voltage" is taken for
/// the throttle, and filtering on a sensor's raw volts rather than its position
/// throws away the wrong samples while looking like it worked.
/// </summary>
public static class ChannelRoles
{
    public static LogChannel? Find(LogDocument document, ChannelRole role)
    {
        ArgumentNullException.ThrowIfNull(document);

        string[] names = Aliases(role);

        // A whole-name match first. "Throttle position" should win over
        // "Throttle position sensor 2" wherever both exist.
        foreach (string alias in names)
            if (document.Channels.FirstOrDefault(
                    c => Simplify(c.Name) == alias && Suits(role, c)) is { } exact)
                return exact;

        foreach (string alias in names)
            if (document.Channels.FirstOrDefault(
                    c => Extends(Simplify(c.Name), alias) && Suits(role, c)) is { } near)
                return near;

        return null;
    }

    /// <summary>
    /// Whether a name is the alias with no more than a bank or sensor number
    /// after it — "afr1" or "lambdaa", but never "afrload".
    ///
    /// Matching any name that merely begins with the alias is too loose, and it
    /// showed: a MegaSquirt log holds both "AFR" and "AFR Load", the second
    /// being the load axis its fuel table is drawn against rather than a mixture
    /// at all. Suggesting a mixture filter on it would throw away every sample
    /// outside a load range, while reading as though it had filtered on AFR.
    /// </summary>
    private static bool Extends(string name, string alias)
    {
        if (!name.StartsWith(alias, StringComparison.Ordinal)) return false;

        string rest = name[alias.Length..];

        return rest.Length switch
        {
            0 => true,
            1 => char.IsAsciiDigit(rest[0]) || rest[0] is 'a' or 'b',
            2 => rest is "a1" or "a2" or "b1" or "b2" or "01" or "02",
            _ => false,
        };
    }

    /// <summary>
    /// Whether a channel's units are right for the job.
    ///
    /// The guard that keeps a MaxxECU's "TPS input voltage" from being taken for
    /// its throttle. An empty unit is allowed everywhere, because plenty of
    /// firmware declares none at all.
    /// </summary>
    private static bool Suits(ChannelRole role, LogChannel channel)
    {
        string units = Simplify(channel.Units);
        if (units.Length == 0) return true;

        return role switch
        {
            ChannelRole.EngineSpeed => units is "rpm",
            ChannelRole.Coolant => units is "c" or "f" or "degc" or "degf" or "temp" or "k",
            ChannelRole.Throttle => units is "%" or "%tps",
            ChannelRole.ManifoldPressure => units is "kpa" or "psi" or "bar" or "mbar" or "inhg",

            // A percentage on one controller, a bare code on another.
            ChannelRole.FuelCut => units is "%" or "code" or "bits",

            // A mixture is reported as a bare ratio as often as it is labelled,
            // and "AFR", "lambda" and ":1" are all in use.
            ChannelRole.Mixture or ChannelRole.MixtureTarget =>
                units is "afr" or "lambda" or ":1" or "ratio",

            _ => true,
        };
    }

    private static string[] Aliases(ChannelRole role) => role switch
    {
        ChannelRole.EngineSpeed => ["rpm", "enginespeed", "engspeed", "tachometer"],

        ChannelRole.Coolant =>
            ["clt", "coolant", "coolanttemp", "coolanttemperature", "enginecoolanttemperature",
             "ect", "watertemp", "wt"],

        ChannelRole.Throttle =>
            ["tps", "throttle", "throttleposition", "throttlepos", "tp", "relativethrottle", "pedal"],

        ChannelRole.Mixture => ["afr", "lambda", "afr1", "lambdaa", "wideband", "o2"],

        // Checked before the plain mixture names would match, since "afrtarget"
        // also starts with "afr".
        ChannelRole.MixtureTarget =>
            ["afrtarget", "lambdatarget", "targetafr", "targetlambda", "afrtgt", "egotarget"],

        ChannelRole.ManifoldPressure => ["map", "manifoldpressure", "manifoldabsolutepressure", "mapkpa"],

        // A cut is reported as a percentage on one controller and as a reason
        // code on another. Either way zero means it is not cutting, which is
        // all this needs to know.
        ChannelRole.FuelCut => ["fuelcut", "fuelcutreason", "fuelcutcode", "dfco", "decelfuelcut"],

        _ => [],
    };

    /// <summary>
    /// A name reduced to something comparable: lower case, with spaces,
    /// underscores, dots and hyphens taken out, since every firmware writes
    /// "Throttle position", "throttle_pos" and "ThrottlePos" for the same thing.
    /// </summary>
    internal static string Simplify(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        int at = 0;

        foreach (char c in text)
        {
            if (c is ' ' or '_' or '.' or '-' or '\t' or '°') continue;

            buffer[at++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..at]);
    }
}
