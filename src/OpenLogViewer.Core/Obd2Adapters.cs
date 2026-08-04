namespace OpenLogViewer.Core;

/// <summary>
/// Recognising an OBD2 dongle by the name it advertises.
///
/// A guess, and unavoidably one. Neither Bluetooth Classic nor BLE publishes
/// anything that says "I am an ELM327" — Classic offers the serial port profile,
/// which a MegaSquirt over Bluetooth offers too, and BLE offers vendor service
/// numbers that mean whatever the maker decided. The name is all there is.
///
/// Getting it wrong is not cosmetic. A dongle that goes unrecognised is probed
/// with TunerStudio commands instead, finds nothing, and reports an unknown ECU
/// — which is a confusing thing to be told about hardware that is working
/// perfectly. A real one did exactly that: an OBDLink advertises as
/// "ScanTool.net-5487", after the company rather than the product, and matched
/// none of the names being looked for.
///
/// Here rather than beside either radio because both were looking for the same
/// thing from their own copy of the list, and the copies had already drifted —
/// one knew about vLinker and the other did not.
/// </summary>
public static class Obd2Adapters
{
    /// <summary>
    /// Fragments of names that mean "this is an OBD2 adapter".
    ///
    /// Matched anywhere in the name, case-insensitively, because these arrive
    /// with a serial number or a model suffix attached far more often than not.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        // Generic, and what most of the cheap ones call themselves.
        "OBDII",
        "OBD2",
        "OBD-II",
        "ELM327",

        // OBDLink, and the two other names the same company ships under. The
        // product is called one thing and advertises as another: an OBDLink r2.6
        // comes up as "ScanTool.net-5487".
        "OBDLink",
        "ScanTool",
        "OBDSol",
        "ElmScan",
        "STN11",
        "STN21",

        // The rest of the common aftermarket ones.
        "Vgate",
        "vLinker",
        "V-LINK",
        "VEEPEAK",
        "Konnwei",
        "Carista",
        "LELink",
        "Viecar",
        "iCar",
    ];

    /// <summary>
    /// Whether a name looks like an OBD2 adapter.
    ///
    /// False for null and blank on purpose: an unnamed device is not evidence of
    /// anything, and guessing it is an adapter would route a MegaSquirt into the
    /// OBD2 path, which is the same mistake in the other direction.
    /// </summary>
    public static bool LooksLikeOne(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether any of several names does.</summary>
    public static bool LooksLikeOne(params string?[] names) =>
        names is not null && names.Any(LooksLikeOne);
}
