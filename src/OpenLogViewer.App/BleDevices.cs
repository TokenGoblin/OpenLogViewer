using System.Diagnostics;
using System.Globalization;
using System.Management;
using Windows.Devices.Bluetooth.Advertisement;

namespace OpenLogViewer.App;

/// <summary>A paired Bluetooth Low Energy device: what it calls itself, and where.</summary>
public sealed record BleDevice(string Name, ulong Address)
{
    /// <summary>
    /// Whether this looks like an OBD2 adapter.
    ///
    /// The same guess made for the Classic ones, for the same reason: there is
    /// nothing else to go on. A BLE device publishes no profile that says "I am
    /// an ELM327" — the serial services these use are vendor numbers that mean
    /// whatever the maker decided.
    /// </summary>
    public bool IsObd2 => Names.Any(n => Name.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] Names =
        ["OBDII", "OBD2", "OBDLink", "ELM327", "Vgate", "V-LINK", "VEEPEAK", "Konnwei", "vLinker"];

    public string Label => $"{Name} (Bluetooth LE)";
}

/// <summary>
/// The paired Bluetooth LE devices, from the Windows device tree.
///
/// Read the same way the serial ports are, and for the same reason: this is
/// specific to the operating system in a way the protocol is not. A BLE device
/// is never a COM port, so it cannot appear in the port list however hard anyone
/// looks — which is exactly how a working dongle comes to look broken.
/// </summary>
public static class BleDevices
{
    /// <summary>Every paired BLE device this machine knows about.</summary>
    public static IReadOnlyList<BleDevice> All()
    {
        var found = new List<BleDevice>();

        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'BTHLE\\\\DEV_%'");

            foreach (ManagementBaseObject device in search.Get())
            {
                using (device)
                {
                    if (device["Name"] is not string name) continue;
                    if (device["PNPDeviceID"] is not string id) continue;

                    if (AddressIn(id) is not { } address) continue;
                    if (found.Any(d => d.Address == address)) continue;

                    found.Add(new BleDevice(name, address));
                }
            }
        }
        catch (Exception e)
        {
            // The device tree is unavailable or refuses. Costing the BLE entries
            // is not worth costing the connect menu.
            Debug.WriteLine($"Could not list the Bluetooth LE devices: {e.Message}");
        }

        return found;
    }

    /// <summary>The ones worth offering as OBD2 adapters.</summary>
    public static IReadOnlyList<BleDevice> Obd2Adapters() => [.. All().Where(d => d.IsObd2)];

    /// <summary>
    /// Addresses heard advertising, which is the one honest way to ask a
    /// Bluetooth LE device whether it is switched on.
    ///
    /// A paired device is listed by Windows whether or not it has power — being
    /// paired is a fact about this computer, not about the device — so the list
    /// alone says nothing. A powered one announces itself several times a second
    /// and is heard within a second or two; an unpowered one cannot be, which is
    /// exactly the distinction wanted.
    ///
    /// Nothing is connected to, so an adapter already talking to something else
    /// is not disturbed by being looked for. Active scanning, which asks each
    /// device it hears for its scan response — some announce almost nothing
    /// until asked, and this is about hearing them rather than about being quiet.
    ///
    /// Being heard proves a device is on. Not being heard does not prove the
    /// opposite, and callers must not say that it does: a device already
    /// connected to something stops advertising, so an adapter paired to a phone
    /// in the same car goes silent while being perfectly alive. Verified on this
    /// machine — a connected mouse is not heard.
    /// </summary>
    public static async Task<IReadOnlySet<ulong>> AdvertisingAsync(TimeSpan window)
    {
        var seen = new HashSet<ulong>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += (_, advertisement) =>
        {
            lock (seen) seen.Add(advertisement.BluetoothAddress);
        };

        try
        {
            watcher.Start();
            await Task.Delay(window).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // No radio, or it is switched off. Hearing nothing is the right
            // answer to that, and it is not worth an error of its own.
            Debug.WriteLine($"Could not scan for Bluetooth LE devices: {e.Message}");
        }
        finally
        {
            try { watcher.Stop(); } catch (Exception) { }
        }

        lock (seen) return new HashSet<ulong>(seen);
    }

    /// <summary>
    /// The radio address in a BLE instance id, or null.
    ///
    /// The id reads <c>BTHLE\DEV_B96975AA93F6\9&amp;306AB8F&amp;0&amp;B96975AA93F6</c> —
    /// the address is the twelve hex digits after DEV_, and it is what the
    /// Bluetooth APIs want in order to reach the device.
    /// </summary>
    internal static ulong? AddressIn(string deviceId)
    {
        const string marker = @"BTHLE\DEV_";

        int at = deviceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        int start = at + marker.Length;
        if (start + 12 > deviceId.Length) return null;

        string hex = deviceId.Substring(start, 12);

        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
            ? address
            : null;
    }
}
