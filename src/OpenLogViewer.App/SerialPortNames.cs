using System.Diagnostics;
using System.Management;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>A COM port and what Windows knows about the device behind it.</summary>
public sealed record SerialPortInfo(string PortName, string Description, bool IsBluetooth)
{
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
    private static Dictionary<string, (string Description, bool Bluetooth)> _cached = [];
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// How long a lookup is reused. The query costs about 200 ms, which is a
    /// visible stutter on a menu that is opened repeatedly while hunting for the
    /// right port; adapters do not come and go faster than this.
    /// </summary>
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<SerialPortInfo> Describe()
    {
        IReadOnlyList<string> names = SerialEcuTransport.AvailablePorts();
        Dictionary<string, (string Description, bool Bluetooth)> known = Known();

        return
        [
            .. names.Select(name =>
            {
                (string description, bool bluetooth) = known.GetValueOrDefault(name, ("", false));
                return new SerialPortInfo(name, description, bluetooth);
            }),
        ];
    }

    private static Dictionary<string, (string, bool)> Known()
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
    private static Dictionary<string, (string, bool)> FromDeviceTree()
    {
        var found = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

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

                bool bluetooth = device["PNPDeviceID"] is string id
                    && id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);

                found[name[(open + 1)..close]] = (name[..open].Trim(), bluetooth);
            }
        }

        return found;
    }
}
