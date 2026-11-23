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

        // MeatPi's ESP32 dongle, which advertises its own name and an id.
        "WiCAN",
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

    /// <summary>
    /// The USB interface behind a MeatPi WiCAN Pro's ELM327 port.
    ///
    /// The Pro's USB-C is a WCH CH342 dual serial bridge (VID 1A86, PID 55D2).
    /// Interface 0 is the ESP32-S3's console; interface 2 is the ELM327 port, an
    /// STN2120 behind it, at 2,000,000 baud with DTR and RTS held off. Measured on
    /// a bench Pro (firmware v4.52, STN2120 v5.8.1, MIC3624 v2.3.22). A CH342 is
    /// a generic chip, so this matches the interface, not the brand.
    /// </summary>
    public const string WiCanProUsbInterface = "VID_1A86&PID_55D2&MI_02";

    /// <summary>The speed of that port: fixed in the firmware, not negotiated.</summary>
    public const int WiCanProUsbBaud = 2_000_000;

    /// <summary>Whether a Windows device id is a WiCAN Pro's ELM327 port.</summary>
    public static bool IsWiCanProUsb(string? deviceId) =>
        deviceId is not null && deviceId.Contains(WiCanProUsbInterface, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether any of several names does.</summary>
    public static bool LooksLikeOne(params string?[] names) =>
        names is not null && names.Any(LooksLikeOne);
}
