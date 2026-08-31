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

    /// <summary>Charge temperature, which sets how much a given manifold pressure weighs.</summary>
    IntakeAir,

    /// <summary>Ambient pressure, where the ECU records it.</summary>
    Barometric,

    /// <summary>How long an injector is held open, before dead time is taken off.</summary>
    InjectorPulseWidth,

    /// <summary>The same as a proportion of the time available, where the ECU works it out.</summary>
    InjectorDuty,

    /// <summary>Rail pressure, which decides what an injector actually flows.</summary>
    FuelPressure,

    /// <summary>Measured air mass, on a car that has a meter for it.</summary>
    MassAirFlow,

    /// <summary>How completely each stroke fills, where the ECU reports it.</summary>
    VolumetricEfficiency,

    /// <summary>Road speed.</summary>
    VehicleSpeed,

    /// <summary>Ignition timing as finally commanded, after every correction.</summary>
    SparkAdvance,

    /// <summary>Timing taken away because knock was heard. Zero when none was.</summary>
    KnockRetard,

    /// <summary>Supply voltage, which sets how long an injector takes to open.</summary>
    BatteryVoltage,

    /// <summary>
    /// The closed-loop trim: how much the controller is moving fuelling to hit
    /// its target. A hundred per cent means it is leaving the table alone.
    /// </summary>
    MixtureCorrection,

    /// <summary>Extra fuel for a cold engine, as a percentage.</summary>
    WarmupCorrection,

    /// <summary>Manifold pressure above atmospheric, where the controller reports it separately.</summary>
    Boost,
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

            ChannelRole.IntakeAir => units is "c" or "f" or "degc" or "degf" or "temp" or "k",

            ChannelRole.Barometric or ChannelRole.FuelPressure =>
                units is "kpa" or "psi" or "psig" or "bar" or "mbar" or "inhg" or "hpa",

            // Guarded tightly, because a pulse width in milliseconds and an
            // injector's duty in per cent are both "how much fuel" and reading
            // one as the other is out by two orders of magnitude.
            ChannelRole.InjectorPulseWidth => units is "ms" or "msec" or "s" or "sec" or "us",
            ChannelRole.InjectorDuty => units is "%" or "percent" or "pct",

            ChannelRole.MassAirFlow =>
                units is "g/s" or "gs" or "kg/h" or "kgh" or "lb/min" or "lbmin" or "g/min" or "kg/min",

            // "ratio" among them because that is how a rusEFI labels veValue,
            // which is nonetheless the same 0–120 figure everyone else calls a
            // percentage rather than a fraction of one.
            ChannelRole.VolumetricEfficiency => units is "%" or "percent" or "pct" or "ratio",

            ChannelRole.VehicleSpeed => units is "km/h" or "kmh" or "kph" or "mph" or "m/s",

            ChannelRole.SparkAdvance or ChannelRole.KnockRetard =>
                units is "deg" or "degrees" or "btdc" or "degbtdc" or "°",

            ChannelRole.BatteryVoltage => units is "v" or "volts" or "volt",

            // Both are multipliers the controller reports as a percentage, and
            // both sit near a hundred rather than near nought.
            ChannelRole.MixtureCorrection or ChannelRole.WarmupCorrection =>
                units is "%" or "percent" or "pct",

            ChannelRole.Boost => units is "psi" or "kpa" or "bar" or "mbar" or "psig",

            _ => true,
        };
    }

    /// <summary>
    /// The names a firmware gives a job, most specific first.
    ///
    /// <para>
    /// rusEFI suffixes its primary sensors with "Value" — RPMValue, MAPValue,
    /// TPSValue, AFRValue — which no amount of loose matching reaches, since
    /// "value" is five characters and a bank number is one. Every one of those
    /// had to be spelled out, and until they were, a rusEFI matched six of these
    /// twenty-one roles: no engine speed, no throttle, no manifold pressure and
    /// no mixture, which between them are most of what the insights, the
    /// suggested filters and the VE calibration are built on.
    /// </para>
    /// </summary>
    private static string[] Aliases(ChannelRole role) => role switch
    {
        ChannelRole.EngineSpeed => ["rpm", "rpmvalue", "enginespeed", "engspeed", "tachometer"],

        ChannelRole.Coolant =>
            ["clt", "coolant", "coolanttemp", "coolanttemperature", "enginecoolanttemperature",
             "ect", "watertemp", "wt"],

        ChannelRole.Throttle =>
            ["tps", "tpsvalue", "throttle", "throttleposition", "throttlepos", "tp", "relativethrottle",
             "pedal", "throttlepedalposition"],

        ChannelRole.Mixture => ["afr", "afrvalue", "lambda", "lambdavalue", "afr1", "lambdaa", "wideband", "o2"],

        // Checked before the plain mixture names would match, since "afrtarget"
        // also starts with "afr".
        ChannelRole.MixtureTarget =>
            ["afrtarget", "afrtarget1", "afr1target", "lambdatarget", "targetafr", "targetlambda",
             "afrtgt", "afrtgt1", "egotarget", "lambdatarget1"],

        ChannelRole.ManifoldPressure => ["map", "mapvalue", "manifoldpressure", "manifoldabsolutepressure", "mapkpa"],

        // A cut is reported as a percentage on one controller and as a reason
        // code on another. Either way zero means it is not cutting, which is
        // all this needs to know.
        ChannelRole.FuelCut => ["fuelcut", "fuelcutreason", "fuelcutcode", "dfco", "decelfuelcut"],

        ChannelRole.IntakeAir =>
            // The bare word last, which is all a rusEFI calls it. Safe only
            // because the units have to be a temperature: the same board logs
            // "intake" pressures and valve positions under names starting the
            // same way, and every one of them is kept out by that guard.
            ["iat", "mat", "intakeairtemperature", "intakeair", "intaketemp", "chargetemp",
             "airtemp", "manifoldairtemp", "act", "inlettemp", "intake"],

        ChannelRole.Barometric => ["baro", "baropressure", "barometricpressure", "barometer", "ambientpressure"],

        // What the engine is actually being given, after every correction —
        // which is the number that decides whether it knocks, rather than what
        // any one table asked for.
        ChannelRole.SparkAdvance =>
            // A rusEFI logs the advance twice, before its corrections and after.
            // This role is the figure actually commanded, so the corrected one
            // is named and "baseIgnitionAdvance" deliberately is not.
            ["sparkadvance", "spksparkadvance", "advance", "correctedignitionadvance",
             "ignitionadvance", "timing", "ignadvance", "spkadvance", "sparkangle",
             "timingadvance", "ignitiontiming"],

        ChannelRole.KnockRetard =>
            // Deliberately not the bare word "knock": a great many firmwares log
            // a raw knock-sensor input under that name, and a sensor reading is
            // not degrees taken away. rusEFI's own retard is "m_knockRetard",
            // whose sibling "m_knockLevel" is exactly such a sensor reading and
            // is kept out by the units having to be degrees.
            ["knockretard", "spkknockretard", "knockcorrection", "knockrtd",
             "totalknockretard", "mknockretard"],

        ChannelRole.BatteryVoltage =>
            ["battv", "batteryvoltage", "batt", "battery", "vbatt", "voltage", "supplyvoltage"],

        // Bank one where a controller logs each separately, the banks being the
        // same on any engine this could describe.
        ChannelRole.MixtureCorrection =>
            ["egocor1", "egocor", "egocorrection", "closedloopcorrection", "o2correction",
             "shorttermfueltrim", "stft", "lambdacorrection", "gego"],

        ChannelRole.WarmupCorrection =>
            ["fuelwarmupcor", "warmupcor", "warmupenrichment", "wue", "warmupcorrection",
             "fuelwarmup", "warmup"],

        ChannelRole.Boost => ["boostpsi", "boost", "boostpressure", "manifoldgaugepressure"],

        // "pw" alone is what a MegaSquirt calls it; the rest spell it out. Bank
        // one is taken where a controller logs each bank separately, the two
        // being the same on any engine this could describe.
        ChannelRole.InjectorPulseWidth =>
            ["pw", "pulsewidth", "injpw", "injectorpulsewidth", "injectorpw", "injpulsewidth",
             "fuelpw", "pulsewidth1", "actuallastinjection"],

        ChannelRole.InjectorDuty =>
            ["dutycycle", "injectorduty", "injduty", "duty", "idc", "injectordutycycle"],

        // The low-pressure side first: on a port-injected engine that is the
        // rail, and on a direct-injected one it is still the pump feeding it,
        // which is the pressure a pulse width can be reasoned about against.
        ChannelRole.FuelPressure =>
            ["fuelpressure", "fuelpress", "fuelrailpressure", "railpressure", "fp", "fuelp",
             "lowfuelpressure", "highfuelpressure"],

        ChannelRole.MassAirFlow =>
            ["maf", "massairflow", "airflow", "airmassflow", "mafflow", "mafmeasured"],

        // "vevalue" rather than anything looser: a rusEFI also logs
        // "veTableYAxis", which is the load the table is looked up on and not a
        // filling efficiency at all.
        ChannelRole.VolumetricEfficiency =>
            ["ve", "vevalue", "volumetricefficiency", "vecurrent", "vetable"],

        ChannelRole.VehicleSpeed =>
            ["vss", "vehiclespeed", "vehiclespeedkph", "speed", "roadspeed", "gpsspeed"],

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
            // A colon and a slash among them: firmware groups its channels with
            // a prefix — "SPK: Knock retard", "Fuel: Warmup cor" — and that
            // colon is a heading rather than part of the name. Left in, every
            // one of those channels is invisible to every alias.
            if (c is ' ' or '_' or '.' or '-' or '\t' or '°' or ':' or '/') continue;

            buffer[at++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..at]);
    }
}
