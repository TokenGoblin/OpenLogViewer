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
    /// What to show in the connect menu.
    ///
    /// A bare COM number is not enough to choose between three of them, and the
    /// one worth finding is often the Bluetooth module — which is otherwise
    /// indistinguishable from a tuning cable.
    /// </summary>
    public string Label => Description.Length > 0 ? $"{PortName} — {Description}" : PortName;
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

    private readonly record struct Entry(string Description, bool Bluetooth, bool Incoming);

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

        return
        [
            .. names.Select(name =>
            {
                Entry entry = known.GetValueOrDefault(name, new Entry("", false, false));

                return new SerialPortInfo(name, entry.Description, entry.Bluetooth)
                {
                    IsIncoming = entry.Incoming,
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

                found[name[(open + 1)..close]] =
                    new Entry(name[..open].Trim(), bluetooth, bluetooth && IsIncoming(id));
            }
        }

        return found;
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
