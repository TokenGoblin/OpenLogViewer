namespace OpenLogViewer.Core;

/// <summary>Which authority defines what a code means.</summary>
public enum DtcAuthority
{
    /// <summary>SAE J2012 — the same meaning on every car ever built.</summary>
    Generic,

    /// <summary>
    /// The manufacturer's own. The number is theirs to assign, so the same digits
    /// mean one thing on a Ford and something unrelated on a Toyota.
    /// </summary>
    Manufacturer,
}

/// <summary>
/// What a diagnostic trouble code means.
///
/// Half of this is standardised and half of it is not, and the difference is the
/// whole point of the file. SAE J2012 fixes the meaning of the generic codes for
/// every vehicle sold — P0301 is a misfire on cylinder one on a Fiesta and on a
/// Ferrari — while the manufacturer-specific ranges are the maker's to assign,
/// so the same five characters mean unrelated things on two cars in the same
/// street.
///
/// So a manufacturer code gets no description here rather than a plausible one.
/// A guess would be worse than the silence: somebody reads P1131 as "HO2S
/// heater circuit", which it is on a Ford and is not on the car in front of them,
/// and goes and buys a sensor. What this can honestly say about those is which
/// manufacturer's book to open.
///
/// Even a generic description is the standard's definition and not a diagnosis.
/// "System too lean, bank 1" is the observation that made the light come on; the
/// cause is anything from a split hose to a tired pump, and the code cannot tell
/// which.
/// </summary>
public static class Obd2Codes
{
    /// <summary>
    /// Whether the number belongs to the standard or to the maker.
    ///
    /// The second character decides it, and the ranges are not symmetrical
    /// between the four systems — P2 is generic while C2, B2 and U2 are the
    /// manufacturer's, and P3 is split down the middle at P3400. Getting this
    /// wrong in the safe direction only costs a description; getting it wrong the
    /// other way puts a confident SAE meaning on a number that never had one.
    /// </summary>
    public static DtcAuthority Authority(string? code)
    {
        if (!IsWellFormed(code)) return DtcAuthority.Manufacturer;

        char system = char.ToUpperInvariant(code![0]);
        char second = code[1];

        return system switch
        {
            'P' => second switch
            {
                '0' or '2' => DtcAuthority.Generic,

                // P3000–P3399 are the maker's; P3400 up is the standard's — the
                // cylinder deactivation range.
                '3' => Number(code) >= 3400 ? DtcAuthority.Generic : DtcAuthority.Manufacturer,

                _ => DtcAuthority.Manufacturer,
            },

            // Chassis, body and network. Only the x0xxx range is the standard's;
            // 1 and 2 are the maker's, and 3 is reserved to the standard.
            'C' or 'B' or 'U' => second is '0' or '3' ? DtcAuthority.Generic : DtcAuthority.Manufacturer,

            _ => DtcAuthority.Manufacturer,
        };
    }

    /// <summary>What the code covers, from its first letter.</summary>
    public static string System(string? code) =>
        !IsWellFormed(code) ? "" : char.ToUpperInvariant(code![0]) switch
        {
            'P' => "Powertrain",
            'C' => "Chassis",
            'B' => "Body",
            _ => "Network",
        };

    /// <summary>
    /// The standard's meaning, or empty where there is none to give.
    ///
    /// Empty for a manufacturer code, and empty for a generic one this does not
    /// carry — J2012 defines a few thousand and no list here will be all of them.
    /// Both are reported as "no description" rather than as an error, because
    /// neither means the code is invalid: the car set it, and the number itself is
    /// what a workshop manual is looked up by.
    /// </summary>
    public static string Describe(string? code)
    {
        if (!IsWellFormed(code)) return "";
        if (Authority(code) == DtcAuthority.Manufacturer) return "";

        string key = code!.ToUpperInvariant();

        if (Descriptions.TryGetValue(key, out string? text)) return text;

        return Family(key);
    }

    /// <summary>A letter and four hex digits, which is all a code ever is.</summary>
    public static bool IsWellFormed(string? code)
    {
        if (code is not { Length: 5 }) return false;

        if (!"PCBUpcbu".Contains(code[0], StringComparison.Ordinal)) return false;

        for (int i = 1; i < 5; i++)
            if (Uri.IsHexDigit(code[i]) is false) return false;

        return true;
    }

    /// <summary>
    /// The four digits as a number, or -1 where any of them is a letter.
    ///
    /// The digits are hex — a code can legitimately read P0A0F, and hybrids use
    /// that range — but every family generated below is numbered in decimal, the
    /// way the standard's tables are laid out. So anything with a letter in it
    /// falls out here and is left to the explicit table.
    /// </summary>
    private static int Number(string code)
    {
        int value = 0;

        for (int i = 1; i < 5; i++)
        {
            if (code[i] is < '0' or > '9') return -1;

            value = (value * 10) + (code[i] - '0');
        }

        return value;
    }

    /// <summary>
    /// Codes that come in runs, described by the rule that generates them.
    ///
    /// Several hundred of the standard's codes are one description with a
    /// cylinder, a bank or a sensor number in it, laid out at a fixed stride —
    /// misfires march one per cylinder from P0301, injector circuits three per
    /// cylinder from P0261, oxygen sensors six per sensor from P0130. Writing
    /// those out would be several hundred lines in which a single transposed digit
    /// puts "cylinder 6" on cylinder 7's code, and nothing would ever catch it.
    ///
    /// Generated from the stride instead, so the arithmetic is stated once and can
    /// be tested at both ends of each run.
    /// </summary>
    private static string Family(string code)
    {
        int n = Number(code);
        if (n < 0) return "";

        char system = char.ToUpperInvariant(code[0]);
        if (system != 'P') return "";

        // Misfires: P0301 up, one per cylinder. P0300 is the random one and is in
        // the table, being a different sentence rather than the same one with a
        // number in it.
        if (n is >= 301 and <= 312) return $"Cylinder {n - 300} misfire detected";

        // Injector circuit, open — one per cylinder from P0201.
        if (n is >= 201 and <= 212) return $"Injector circuit open — cylinder {n - 200}";

        // Injector drive: three codes per cylinder from P0261 — low, high, then
        // the contribution test that compares one cylinder's fuelling to the rest.
        if (n is >= 261 and <= 284)
        {
            int cylinder = ((n - 261) / 3) + 1;

            return ((n - 261) % 3) switch
            {
                0 => $"Cylinder {cylinder} injector circuit low",
                1 => $"Cylinder {cylinder} injector circuit high",
                _ => $"Cylinder {cylinder} contribution or balance fault",
            };
        }

        // Ignition coils: P0351 up, one per cylinder, lettered A–L in the
        // standard's own wording. The letter is the cylinder in firing order as
        // the manufacturer wired it, which is why both are worth saying.
        if (n is >= 351 and <= 362)
        {
            int cylinder = n - 350;

            return $"Ignition coil {(char)('A' + cylinder - 1)} primary or secondary circuit "
                   + $"— cylinder {cylinder}";
        }

        // Oxygen sensors: six codes per sensor, banks 1 and 2 twenty apart.
        if (n is >= 130 and <= 167) return OxygenSensor(n);

        return "";
    }

    /// <summary>
    /// One of the oxygen sensor codes, from where it sits in the run.
    ///
    /// The layout is six codes per sensor and three sensors per bank, bank 1
    /// starting at P0130 and bank 2 at P0150 — so the gap at P0148 and P0149 is
    /// real and has to fall through rather than being counted over.
    /// </summary>
    private static string OxygenSensor(int n)
    {
        int bank = n >= 150 ? 2 : 1;
        int offset = n - (bank == 1 ? 130 : 150);

        // Six per sensor, three sensors per bank — past that is the next bank's
        // range or the hole between them.
        if (offset is < 0 or > 17) return "";

        int sensor = (offset / 6) + 1;

        string what = (offset % 6) switch
        {
            0 => "circuit malfunction",
            1 => "circuit low voltage",
            2 => "circuit high voltage",
            3 => "circuit slow response",
            4 => "circuit — no activity detected",
            _ => "heater circuit malfunction",
        };

        return $"O2 sensor, bank {bank} sensor {sensor} — {what}";
    }

    /// <summary>
    /// The generic codes worth naming, as SAE J2012 defines them.
    ///
    /// Not every one of them: the standard runs to several thousand numbers, most
    /// of which no one has ever seen set. These are the ones that actually turn a
    /// light on — the sensors, the fuelling, the misfire and emissions families,
    /// the boost and knock codes a tuner meets, and enough of the transmission and
    /// network ranges to recognise where a fault lives.
    ///
    /// Wording follows the standard rather than being improved on, so what is on
    /// screen matches what a manual will say when it is looked up. Where the
    /// standard's phrasing is genuinely opaque — "insufficient coolant temperature
    /// for closed loop fuel control" — the sense is kept and the sentence is not.
    /// </summary>
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Variable valve timing and the correlation checks.
        ["P0010"] = "Camshaft position actuator A circuit — bank 1",
        ["P0011"] = "Camshaft position — timing over-advanced or performance, bank 1",
        ["P0012"] = "Camshaft position — timing over-retarded, bank 1",
        ["P0013"] = "Camshaft position actuator B circuit — bank 1",
        ["P0014"] = "Camshaft position — timing over-advanced or performance, bank 1 exhaust",
        ["P0016"] = "Crankshaft and camshaft position correlation — bank 1 sensor A",
        ["P0017"] = "Crankshaft and camshaft position correlation — bank 1 sensor B",
        ["P0020"] = "Camshaft position actuator A circuit — bank 2",
        ["P0021"] = "Camshaft position — timing over-advanced or performance, bank 2",
        ["P0022"] = "Camshaft position — timing over-retarded, bank 2",

        // Fuelling under pressure.
        ["P0087"] = "Fuel rail or system pressure too low",
        ["P0088"] = "Fuel rail or system pressure too high",
        ["P0089"] = "Fuel pressure regulator performance",
        ["P0090"] = "Fuel pressure regulator control circuit",

        // Air metering.
        ["P0100"] = "Mass or volume air flow circuit malfunction",
        ["P0101"] = "Mass or volume air flow circuit range or performance",
        ["P0102"] = "Mass or volume air flow circuit low input",
        ["P0103"] = "Mass or volume air flow circuit high input",
        ["P0105"] = "Manifold absolute pressure circuit malfunction",
        ["P0106"] = "Manifold absolute pressure circuit range or performance",
        ["P0107"] = "Manifold absolute pressure circuit low input",
        ["P0108"] = "Manifold absolute pressure circuit high input",

        // Intake air temperature.
        ["P0110"] = "Intake air temperature circuit malfunction",
        ["P0111"] = "Intake air temperature circuit range or performance",
        ["P0112"] = "Intake air temperature circuit low input",
        ["P0113"] = "Intake air temperature circuit high input",

        // Coolant temperature.
        ["P0115"] = "Engine coolant temperature circuit malfunction",
        ["P0116"] = "Engine coolant temperature circuit range or performance",
        ["P0117"] = "Engine coolant temperature circuit low input",
        ["P0118"] = "Engine coolant temperature circuit high input",
        ["P0119"] = "Engine coolant temperature circuit intermittent",

        // Throttle and pedal.
        ["P0120"] = "Throttle or pedal position sensor A circuit malfunction",
        ["P0121"] = "Throttle or pedal position sensor A circuit range or performance",
        ["P0122"] = "Throttle or pedal position sensor A circuit low input",
        ["P0123"] = "Throttle or pedal position sensor A circuit high input",
        ["P0125"] = "Coolant too cold for closed loop fuel control",
        ["P0128"] = "Coolant thermostat — engine below its regulating temperature",

        // Mixture. The four that a tuner meets most.
        ["P0170"] = "Fuel trim malfunction — bank 1",
        ["P0171"] = "System too lean — bank 1",
        ["P0172"] = "System too rich — bank 1",
        ["P0173"] = "Fuel trim malfunction — bank 2",
        ["P0174"] = "System too lean — bank 2",
        ["P0175"] = "System too rich — bank 2",
        ["P0176"] = "Fuel composition sensor circuit malfunction",
        ["P0182"] = "Fuel temperature sensor A circuit low input",
        ["P0183"] = "Fuel temperature sensor A circuit high input",
        ["P0190"] = "Fuel rail pressure sensor circuit malfunction",
        ["P0191"] = "Fuel rail pressure sensor circuit range or performance",
        ["P0192"] = "Fuel rail pressure sensor circuit low input",
        ["P0193"] = "Fuel rail pressure sensor circuit high input",

        ["P0200"] = "Injector circuit malfunction",
        ["P0217"] = "Engine coolant over temperature",
        ["P0219"] = "Engine overspeed",
        ["P0221"] = "Throttle or pedal position sensor B circuit range or performance",
        ["P0222"] = "Throttle or pedal position sensor B circuit low input",
        ["P0223"] = "Throttle or pedal position sensor B circuit high input",

        // Fuel pump.
        ["P0230"] = "Fuel pump primary circuit malfunction",
        ["P0231"] = "Fuel pump secondary circuit low",
        ["P0232"] = "Fuel pump secondary circuit high",
        ["P0233"] = "Fuel pump secondary circuit intermittent",

        // Boost. The pair worth knowing on a turbocharged car, in both directions.
        ["P0234"] = "Turbocharger or supercharger overboost",
        ["P0235"] = "Turbocharger boost sensor A circuit malfunction",
        ["P0236"] = "Turbocharger boost sensor A circuit range or performance",
        ["P0237"] = "Turbocharger boost sensor A circuit low",
        ["P0238"] = "Turbocharger boost sensor A circuit high",
        ["P0243"] = "Turbocharger wastegate solenoid A malfunction",
        ["P0245"] = "Turbocharger wastegate solenoid A low",
        ["P0246"] = "Turbocharger wastegate solenoid A high",
        ["P0299"] = "Turbocharger or supercharger underboost",

        ["P0300"] = "Random or multiple cylinder misfire detected",
        ["P0313"] = "Misfire detected with low fuel level",
        ["P0316"] = "Misfire detected on startup — first 1,000 revolutions",

        // Knock and the crank and cam sensors that feed it.
        ["P0325"] = "Knock sensor 1 circuit — bank 1",
        ["P0326"] = "Knock sensor 1 circuit range or performance — bank 1",
        ["P0327"] = "Knock sensor 1 circuit low input — bank 1",
        ["P0328"] = "Knock sensor 1 circuit high input — bank 1",
        ["P0330"] = "Knock sensor 2 circuit — bank 2",
        ["P0332"] = "Knock sensor 2 circuit low input — bank 2",
        ["P0333"] = "Knock sensor 2 circuit high input — bank 2",
        ["P0335"] = "Crankshaft position sensor A circuit malfunction",
        ["P0336"] = "Crankshaft position sensor A circuit range or performance",
        ["P0337"] = "Crankshaft position sensor A circuit low input",
        ["P0338"] = "Crankshaft position sensor A circuit high input",
        ["P0339"] = "Crankshaft position sensor A circuit intermittent",
        ["P0340"] = "Camshaft position sensor A circuit — bank 1",
        ["P0341"] = "Camshaft position sensor A circuit range or performance — bank 1",
        ["P0342"] = "Camshaft position sensor A circuit low input — bank 1",
        ["P0343"] = "Camshaft position sensor A circuit high input — bank 1",
        ["P0344"] = "Camshaft position sensor A circuit intermittent — bank 1",
        ["P0345"] = "Camshaft position sensor A circuit — bank 2",
        ["P0350"] = "Ignition coil primary or secondary circuit malfunction",

        // Exhaust gas recirculation.
        ["P0400"] = "Exhaust gas recirculation flow malfunction",
        ["P0401"] = "Exhaust gas recirculation flow insufficient",
        ["P0402"] = "Exhaust gas recirculation flow excessive",
        ["P0403"] = "Exhaust gas recirculation circuit malfunction",
        ["P0404"] = "Exhaust gas recirculation circuit range or performance",
        ["P0405"] = "Exhaust gas recirculation sensor A circuit low",
        ["P0406"] = "Exhaust gas recirculation sensor A circuit high",
        ["P0410"] = "Secondary air injection system malfunction",
        ["P0411"] = "Secondary air injection system — incorrect flow detected",

        // Catalyst.
        ["P0420"] = "Catalyst system efficiency below threshold — bank 1",
        ["P0421"] = "Warm-up catalyst efficiency below threshold — bank 1",
        ["P0430"] = "Catalyst system efficiency below threshold — bank 2",
        ["P0431"] = "Warm-up catalyst efficiency below threshold — bank 2",

        // Evaporative emissions — the family that most often turns a light on for
        // a reason that costs nothing to fix.
        ["P0440"] = "Evaporative emission control system malfunction",
        ["P0441"] = "Evaporative emission control system — incorrect purge flow",
        ["P0442"] = "Evaporative emission control system — small leak detected",
        ["P0443"] = "Evaporative emission control system purge valve circuit",
        ["P0446"] = "Evaporative emission control system vent control circuit",
        ["P0447"] = "Evaporative emission control system vent control circuit open",
        ["P0448"] = "Evaporative emission control system vent control circuit shorted",
        ["P0455"] = "Evaporative emission control system — large leak detected",
        ["P0456"] = "Evaporative emission control system — very small leak detected",
        ["P0457"] = "Evaporative emission control system leak — fuel cap loose or missing",

        // Road speed and idle.
        ["P0500"] = "Vehicle speed sensor malfunction",
        ["P0501"] = "Vehicle speed sensor range or performance",
        ["P0503"] = "Vehicle speed sensor intermittent or erratic",
        ["P0505"] = "Idle control system malfunction",
        ["P0506"] = "Idle control system — RPM lower than expected",
        ["P0507"] = "Idle control system — RPM higher than expected",

        // Oil, air conditioning and charging.
        ["P0520"] = "Engine oil pressure sensor or switch circuit malfunction",
        ["P0521"] = "Engine oil pressure sensor or switch range or performance",
        ["P0522"] = "Engine oil pressure sensor or switch low voltage",
        ["P0523"] = "Engine oil pressure sensor or switch high voltage",
        ["P0532"] = "Air conditioning refrigerant pressure sensor circuit low",
        ["P0533"] = "Air conditioning refrigerant pressure sensor circuit high",
        ["P0560"] = "System voltage malfunction",
        ["P0562"] = "System voltage low",
        ["P0563"] = "System voltage high",

        // The controller itself.
        ["P0600"] = "Serial communication link malfunction",
        ["P0601"] = "Internal control module memory checksum error",
        ["P0602"] = "Control module programming error",
        ["P0603"] = "Internal control module keep-alive memory error",
        ["P0604"] = "Internal control module random access memory error",
        ["P0605"] = "Internal control module read-only memory error",
        ["P0606"] = "Control module processor fault",
        ["P0620"] = "Generator control circuit malfunction",
        ["P0622"] = "Generator field F control circuit malfunction",
        ["P0627"] = "Fuel pump control circuit open",
        ["P0628"] = "Fuel pump control circuit low",
        ["P0629"] = "Fuel pump control circuit high",

        // Transmission.
        ["P0700"] = "Transmission control system malfunction",
        ["P0701"] = "Transmission control system range or performance",
        ["P0703"] = "Brake switch B circuit malfunction",
        ["P0705"] = "Transmission range sensor circuit malfunction",
        ["P0706"] = "Transmission range sensor circuit range or performance",
        ["P0710"] = "Transmission fluid temperature sensor circuit malfunction",
        ["P0715"] = "Input or turbine speed sensor circuit malfunction",
        ["P0720"] = "Output speed sensor circuit malfunction",
        ["P0725"] = "Engine speed input circuit malfunction",
        ["P0730"] = "Incorrect gear ratio",
        ["P0740"] = "Torque converter clutch circuit malfunction",
        ["P0741"] = "Torque converter clutch circuit — stuck off or performance",
        ["P0743"] = "Torque converter clutch circuit electrical",
        ["P0750"] = "Shift solenoid A malfunction",
        ["P0755"] = "Shift solenoid B malfunction",
        ["P0760"] = "Shift solenoid C malfunction",

        // The lean and rich codes that only apply at one end of the load range,
        // which is what tells them apart from P0171 and P0172.
        ["P2187"] = "System too lean at idle — bank 1",
        ["P2188"] = "System too rich at idle — bank 1",
        ["P2189"] = "System too lean at idle — bank 2",
        ["P2190"] = "System too rich at idle — bank 2",
        ["P2195"] = "O2 sensor signal biased or stuck lean — bank 1 sensor 1",
        ["P2196"] = "O2 sensor signal biased or stuck rich — bank 1 sensor 1",
        ["P2197"] = "O2 sensor signal biased or stuck lean — bank 2 sensor 1",
        ["P2198"] = "O2 sensor signal biased or stuck rich — bank 2 sensor 1",

        // Throttle actuator control — the drive-by-wire family.
        ["P2101"] = "Throttle actuator control motor circuit range or performance",
        ["P2111"] = "Throttle actuator control system — stuck open",
        ["P2112"] = "Throttle actuator control system — stuck closed",
        ["P2119"] = "Throttle actuator control throttle body range or performance",
        ["P2135"] = "Throttle or pedal position sensor A and B correlation",
        ["P2138"] = "Pedal position sensor D and E correlation",

        // Network. A U code is usually a symptom of something else having stopped
        // answering rather than a fault in what reported it.
        ["U0100"] = "Lost communication with the engine control module",
        ["U0101"] = "Lost communication with the transmission control module",
        ["U0121"] = "Lost communication with the ABS control module",
        ["U0140"] = "Lost communication with the body control module",
        ["U0155"] = "Lost communication with the instrument panel cluster",
        ["U0164"] = "Lost communication with the climate control module",
        ["U0300"] = "Internal control module software incompatibility",
        ["U0401"] = "Invalid data received from the engine control module",
    };

    /// <summary>How many generic codes are named outright, for the about box and the tests.</summary>
    public static int Named => Descriptions.Count;
}
