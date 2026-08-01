using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Broad grouping for a channel. Declaration order is display order.
/// </summary>
public enum ChannelCategory
{
    Common,
    Engine,
    Air,
    Fuel,
    Ignition,
    Temperature,
    Idle,
    Electrical,
    Diagnostics,
    Other,
}

/// <summary>
/// Sorts ECU channel names into categories so a 179-channel log can be browsed.
///
/// Names vary between firmwares ("DutyCycle1", "Duty Cycle1", "duty_cycle_1"),
/// so names are normalised to lower-case space-separated tokens first. Matching
/// is then done on whole tokens, which keeps short keys like "ve" and "pw" from
/// matching inside unrelated words such as "valve" or "power".
/// </summary>
public static class ChannelClassifier
{
    /// <summary>The channels a tuner reaches for first, promoted above their natural category.</summary>
    private static readonly HashSet<string> CommonNames = new(StringComparer.Ordinal)
    {
        "rpm", "map", "tps", "afr", "afr 1", "afr 2", "lambda", "clt", "mat", "iat",
        "batt v", "boost psi", "boost", "ve 1", "pw", "pw 1", "spark advance",
        "advance", "dwell", "knock", "afr target 1", "afr 1 target", "target afr",
        "duty cycle 1", "ego cor 1", "accel enrich", "load", "afr load", "engine",
    };

    /// <summary>A leading namespace token and the system it assigns the channel to.</summary>
    private static readonly Dictionary<string, ChannelCategory> NamespaceOwners = new(StringComparer.Ordinal)
    {
        ["fuel"] = ChannelCategory.Fuel,
        ["seq"] = ChannelCategory.Fuel,
        ["inj"] = ChannelCategory.Fuel,
        ["injector"] = ChannelCategory.Fuel,
        ["spk"] = ChannelCategory.Ignition,
        ["spark"] = ChannelCategory.Ignition,
        ["ign"] = ChannelCategory.Ignition,
        ["knock"] = ChannelCategory.Ignition,
        ["idle"] = ChannelCategory.Idle,
    };

    /// <summary>
    /// Ordered: the first rule whose token matches wins, so more specific rules
    /// come first. Several channels legitimately touch two systems ("Injector
    /// timing", "SPK: Fuel cut retard", "VVT duty"), and the ordering is what
    /// decides which system owns them.
    /// </summary>
    private static readonly (ChannelCategory Category, string[] Tokens)[] Rules =
    [
        // Unambiguously spark-side, claimed before "fuel"/"duty" can grab them.
        (ChannelCategory.Ignition, ["spk", "spark", "dwell", "knock", "coil"]),
        (ChannelCategory.Idle, ["idle", "iac", "isv", "pwmidle"]),
        (ChannelCategory.Air, ["map", "tps", "boost", "baro", "barometer", "throttle", "vacuum", "wastegate", "vvt", "cam"]),
        (ChannelCategory.Fuel, ["afr", "ego", "lambda", "ve", "pw", "duty", "injector", "inj", "fuel", "accel", "enrich", "maf", "gammae", "warmup", "priming", "ase", "pulse", "squirt", "o2", "wideband", "mpg", "gallons"]),
        // Weaker spark words, only after fuel has had its say.
        (ChannelCategory.Ignition, ["advance", "retard", "timing", "ign", "ignition"]),
        (ChannelCategory.Temperature, ["clt", "mat", "iat", "temp", "egt", "coolant", "cht", "thermistor"]),
        (ChannelCategory.Electrical, ["batt", "battery", "volt", "volts", "adc", "gpio", "gpioadc", "current", "amps", "amp", "sensor"]),
        (ChannelCategory.Engine, ["rpm", "load", "torque", "speed", "gear", "vss", "crank", "sync", "engine", "revs", "power", "odometer", "trip", "miles", "distance"]),
        // "sec" covers SecL, which camel-splits to "sec l".
        (ChannelCategory.Diagnostics, ["error", "status", "cel", "can", "flag", "flags", "bit", "bits", "mainloop", "sec", "secl", "seconds", "counter", "count", "watchdog", "loop", "fault", "warning", "sdcard", "port"]),
    ];

    public static ChannelCategory Classify(string name, string units = "")
    {
        string normalised = Normalise(name);
        if (normalised.Length == 0) return ChannelCategory.Other;
        if (CommonNames.Contains(normalised)) return ChannelCategory.Common;

        string[] ordered = normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new HashSet<string>(ordered, StringComparer.Ordinal);

        // Firmwares namespace channels with a leading word ("Fuel: Baro cor",
        // "SPK: Fuel cut retard"). That prefix names the system the channel
        // belongs to, and outranks any keyword later in the name.
        if (ordered.Length > 1 && NamespaceOwners.TryGetValue(ordered[0], out ChannelCategory owner))
            return owner;

        foreach ((ChannelCategory category, string[] keys) in Rules)
            if (keys.Any(tokens.Contains))
                return category;

        // Firmwares sometimes run words together in all-caps ("TPSADC", "canin1"),
        // leaving a single token. Fall back to prefix matching: requiring the key
        // at the start of a token keeps "ase" from matching inside "phase", which
        // plain substring matching does.
        foreach ((ChannelCategory category, string[] keys) in Rules)
            if (keys.Any(k => k.Length >= 3 && tokens.Any(t => t.StartsWith(k, StringComparison.Ordinal))))
                return category;

        return ClassifyByUnits(units);
    }

    private static ChannelCategory ClassifyByUnits(string units) => units.Trim().ToLowerInvariant() switch
    {
        "°f" or "°c" or "degf" or "degc" => ChannelCategory.Temperature,
        "v" or "volts" or "mv" => ChannelCategory.Electrical,
        "bit" or "bits" => ChannelCategory.Diagnostics,
        "rpm" => ChannelCategory.Engine,
        "kpa" or "psi" or "bar" => ChannelCategory.Air,
        "afr" or "lambda" or "ms" => ChannelCategory.Fuel,
        _ => ChannelCategory.Other,
    };

    public static string DisplayName(ChannelCategory category) => category switch
    {
        ChannelCategory.Common => "Common",
        ChannelCategory.Engine => "Engine",
        ChannelCategory.Air => "Air & boost",
        ChannelCategory.Fuel => "Fuel",
        ChannelCategory.Ignition => "Ignition",
        ChannelCategory.Temperature => "Temperature",
        ChannelCategory.Idle => "Idle",
        ChannelCategory.Electrical => "Electrical",
        ChannelCategory.Diagnostics => "Diagnostics",
        _ => "Other",
    };

    /// <summary>
    /// Lower-cases and splits a channel name into space-separated tokens,
    /// breaking at camel-case humps and letter/digit boundaries so that
    /// "DutyCycle1", "Duty Cycle1" and "duty_cycle_1" all normalise alike.
    /// </summary>
    internal static string Normalise(string name)
    {
        var sb = new StringBuilder(name.Length + 8);

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }

            if (i > 0 && sb.Length > 0 && sb[^1] != ' ')
            {
                char previous = name[i - 1];
                bool hump = char.IsLower(previous) && char.IsUpper(c);
                bool letterDigit = char.IsLetter(previous) != char.IsLetter(c);
                if (hump || letterDigit) sb.Append(' ');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString().Trim();
    }
}
