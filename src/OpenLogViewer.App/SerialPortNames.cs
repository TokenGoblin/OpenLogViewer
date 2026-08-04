using System.Diagnostics;
using System.Management;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>A COM port and what Windows knows about the device behind it.</summary>
public sealed record SerialPortInfo(string PortName, string Description, bool IsBluetooth)
{
    /// <summary>
    /// True for Windows' incoming Bluetooth port, which cannot reach an ECU.
    ///
    /// Pairing a serial-port-profile device produces two: an outgoing one bound
    /// to that device, and an incoming one that waits for something to dial into
    /// this machine. They are named identically and sit next to each other in
    /// any list, so the only way to tell them apart is that the incoming one is
    /// bound to no address at all.
    /// </summary>
    public bool IsIncoming { get; init; }

    /// <summary>
    /// Name of the Bluetooth device this port reaches, where there is one.
    ///
    /// The port is called "Standard Serial over Bluetooth link" whatever is on
    /// the other end, so this is the only thing that says which ECU it is — and
    /// a MaxxECU has to be told apart before connecting, because it speaks a
    /// different protocol entirely.
    /// </summary>
    public string DeviceName { get; init; } = "";

    /// <summary>
    /// Windows' identifier for the device behind this port.
    ///
    /// Steadier than the COM number, which Windows hands out and reuses: a USB
    /// adapter carries its serial in here, so the same board is recognisable
    /// after a replug even if it lands on a different port.
    /// </summary>
    public string DeviceId { get; init; } = "";

    /// <summary>
    /// What answered here last time, when anything has.
    ///
    /// Windows names the chip — "Arduino Mega 2560" — which says nothing about
    /// which ECU is on it. The ECU's own signature does, and it is only knowable
    /// by having asked once.
    /// </summary>
    public string KnownEcu { get; init; } = "";

    /// <summary>
    /// When this device was last connected to, or null if never.
    ///
    /// What puts the port you actually use at the top of the list. A workshop
    /// machine accumulates ports — two ECUs, a dongle, a printer, whatever else
    /// claims a COM number — and alphabetical order is no help at all in finding
    /// the one you reach for every day.
    /// </summary>
    public DateTimeOffset? LastUsed { get; init; }

    /// <summary>Whether anything has ever answered here.</summary>
    public bool IsKnown => KnownEcu.Length > 0;

    /// <summary>True when this port reaches a MaxxECU, which advertises as MaxxECU_&lt;serial&gt;.</summary>
    public bool IsMaxxEcu =>
        DeviceName.StartsWith("MaxxECU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this port looks like an OBD2 adapter.
    ///
    /// Guessed from the name, because there is nothing else to go on and the
    /// alternative is a wrong guess in the other direction: probing an adapter
    /// with TunerStudio commands finds nothing and reports an unknown ECU, which
    /// is a confusing thing to be told about a dongle that is working perfectly.
    /// A dongle calling itself something else still connects from the menu's
    /// OBD2 entry.
    ///
    /// The names live in the core, shared with the Bluetooth LE list, because
    /// this used to keep its own copy and the two had already drifted apart.
    /// </summary>
    public bool IsObd2 => Obd2Adapters.LooksLikeOne(DeviceName, Description);

    /// <summary>
    /// What to show in the connect menu.
    ///
    /// The device's own name where there is one, because that is the only thing
    /// that distinguishes two Bluetooth ports: every one of them is called
    /// "Standard Serial over Bluetooth link", so with two ECUs paired the menu
    /// offered two identical entries and picking the wrong one waited out a
    /// timeout to say nothing useful.
    /// </summary>
    public string Label => this switch
    {
        // What answered here beats what Windows calls the hardware. "Arduino
        // Mega 2560" names the chip a Speeduino happens to run on, which is not
        // what anyone is looking for in this list.
        { KnownEcu.Length: > 0 } => $"{PortName} — {KnownEcu}",
        { DeviceName.Length: > 0 } => $"{PortName} — {DeviceName} (Bluetooth)",
        { Description.Length: > 0 } => $"{PortName} — {Description}",
        _ => PortName,
    };
}

/// <summary>
/// Names the serial ports from the Windows device tree.
///
/// Lives in the application rather than the core because it is specific to this
/// operating system in a way the protocol is not, and because a port nobody can
/// describe still works perfectly well — this only ever adds a label.
/// </summary>
public static class SerialPortNames
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, Entry> _cached = [];
    private static DateTime _cachedAt = DateTime.MinValue;

    private readonly record struct Entry(
        string Description, bool Bluetooth, bool Incoming, string DeviceName, string DeviceId);

    /// <summary>
    /// What answered on each device, by hardware id.
    ///
    /// Filled in by whoever connects; this class only knows what Windows says,
    /// and Windows does not know one Arduino from another.
    /// </summary>
    private static readonly Dictionary<string, string> Ecus =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each device was last connected to, by the same hardware id.</summary>
    private static readonly Dictionary<string, DateTimeOffset> Used =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Note that this ECU answered on the device behind a port, and when.</summary>
    public static void Remember(string portName, string signature)
    {
        string id = All().FirstOrDefault(p => p.PortName == portName)?.DeviceId ?? "";
        if (id.Length == 0 || signature.Length == 0) return;

        lock (Gate)
        {
            Ecus[id] = signature;
            Used[id] = DateTimeOffset.Now;
        }
    }

    /// <summary>Every device's last use, to be saved alongside the signatures.</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> LastUsed()
    {
        lock (Gate) return new Dictionary<string, DateTimeOffset>(Used, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Restore when each was last used.</summary>
    public static void RecallLastUsed(IReadOnlyDictionary<string, DateTimeOffset>? saved)
    {
        if (saved is null) return;

        lock (Gate)
            foreach ((string id, DateTimeOffset when) in saved) Used[id] = when;
    }

    /// <summary>Drops everything learnt about devices, for a clean slate.</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            Ecus.Clear();
            Used.Clear();
        }
    }

    /// <summary>Everything remembered so far, to be saved and handed back next time.</summary>
    public static IReadOnlyDictionary<string, string> Remembered()
    {
        lock (Gate) return new Dictionary<string, string>(Ecus, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Restore what a previous session learnt.</summary>
    public static void Recall(IReadOnlyDictionary<string, string>? saved)
    {
        if (saved is null) return;

        lock (Gate)
            foreach ((string id, string signature) in saved)
                Ecus[id] = signature;
    }

    /// <summary>
    /// How long a lookup is reused. The query costs about 200 ms, which is a
    /// visible stutter on a menu that is opened repeatedly while hunting for the
    /// right port; adapters do not come and go faster than this.
    /// </summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every COM port worth offering.
    ///
    /// Windows' incoming Bluetooth port is left out: it cannot reach an ECU, it
    /// is named identically to the one that can, and choosing it used to hang
    /// for the write timeout and then take the application down. If that would
    /// leave nothing at all, everything is shown instead — a wrong guess should
    /// not be able to hide the only port there is.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> Describe()
    {
        IReadOnlyList<SerialPortInfo> all = All();
        SerialPortInfo[] usable = [.. all.Where(p => !p.IsIncoming)];

        return usable.Length > 0 ? usable : all;
    }

    /// <summary>Every port, including the ones not worth offering.</summary>
    public static IReadOnlyList<SerialPortInfo> All()
    {
        IReadOnlyList<string> names = SerialEcuTransport.AvailablePorts();
        Dictionary<string, Entry> known = Known();
        Dictionary<string, string> ecus;
        Dictionary<string, DateTimeOffset> used;

        lock (Gate)
        {
            ecus = new Dictionary<string, string>(Ecus, StringComparer.OrdinalIgnoreCase);
            used = new Dictionary<string, DateTimeOffset>(Used, StringComparer.OrdinalIgnoreCase);
        }

        return
        [
            .. names.Select(name =>
            {
                Entry entry = known.GetValueOrDefault(name, new Entry("", false, false, "", ""));

                return new SerialPortInfo(name, entry.Description, entry.Bluetooth)
                {
                    IsIncoming = entry.Incoming,
                    DeviceName = entry.DeviceName,
                    DeviceId = entry.DeviceId,
                    KnownEcu = ecus.GetValueOrDefault(entry.DeviceId, ""),
                    LastUsed = used.TryGetValue(entry.DeviceId, out DateTimeOffset when)
                        ? when
                        : null,
                };
            }),
        ];
    }

    private static Dictionary<string, Entry> Known()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cachedAt < Freshness) return _cached;

            try
            {
                _cached = FromDeviceTree();
            }
            catch (Exception e)
            {
                // The device tree is unavailable or refuses. The ports are still
                // there and still work; they just go unlabelled.
                Debug.WriteLine($"Could not describe the serial ports: {e.Message}");
                _cached = [];
            }

            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    /// <summary>
    /// Port names to their description, and whether they are Bluetooth.
    ///
    /// Bluetooth is recognised by the enumerator rather than by the name: a
    /// paired serial-port-profile device attaches under BTHENUM whatever the
    /// module happens to be called.
    /// </summary>
    private static Dictionary<string, Entry> FromDeviceTree()
    {
        var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> bluetoothNames = BluetoothDeviceNames();

        using var search = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

        foreach (ManagementBaseObject device in search.Get())
        {
            using (device)
            {
                if (device["Name"] is not string name) continue;

                int open = name.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
                if (open < 0) continue;

                int close = name.IndexOf(')', open);
                if (close < 0) continue;

                string id = device["PNPDeviceID"] as string ?? "";
                bool bluetooth = id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);

                string reaches = bluetooth
                    ? bluetoothNames.GetValueOrDefault(AddressIn(id), "")
                    : "";

                found[name[(open + 1)..close]] = new Entry(
                    name[..open].Trim(), bluetooth, bluetooth && IsIncoming(id), reaches, id);
            }
        }

        return found;
    }

    /// <summary>
    /// Paired Bluetooth device names by address.
    ///
    /// The serial port is called the same thing whatever it reaches, so the
    /// device behind it has to be looked up separately. Its address is in the
    /// port's own instance id.
    /// </summary>
    private static Dictionary<string, string> BluetoothDeviceNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var search = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'BTHENUM%'");

        foreach (ManagementBaseObject paired in search.Get())
        {
            using (paired)
            {
                if (paired["Name"] is not string name) continue;
                if (paired["PNPDeviceID"] is not string id) continue;

                // The serial-port entries are the ones to skip: they carry the
                // address but not the device's name.
                if (name.Contains("(COM", StringComparison.OrdinalIgnoreCase)) continue;

                string address = AddressIn(id);
                if (address.Length > 0) names.TryAdd(address, name);
            }
        }

        return names;
    }

    /// <summary>The Bluetooth address embedded in an instance id, or empty.</summary>
    private static string AddressIn(string deviceId)
    {
        foreach (string part in deviceId.Split('\\', '&', '_'))
            if (part.Length == 12 && part.All(Uri.IsHexDigit) && part.Any(c => c != '0'))
                return part;

        return "";
    }

    /// <summary>
    /// Whether a Bluetooth port is the incoming one.
    ///
    /// The outgoing port carries the paired device's address in its instance id
    ///   …&amp;0&amp;<b>01B6EC10F00D</b>_C00000000
    /// while the incoming one, being bound to nothing in particular, carries
    ///   …&amp;0&amp;<b>000000000000</b>_00000000
    ///
    /// Read as "has no remote address" rather than by matching a shape, so a
    /// layout that differs slightly errs towards offering the port.
    /// </summary>
    internal static bool IsIncoming(string deviceId)
    {
        int last = deviceId.LastIndexOf('&');
        if (last < 0 || last + 1 >= deviceId.Length) return false;

        string tail = deviceId[(last + 1)..];
        int underscore = tail.IndexOf('_');
        string address = underscore >= 0 ? tail[..underscore] : tail;

        return address.Length >= 12 && address.All(c => c == '0');
    }
}
