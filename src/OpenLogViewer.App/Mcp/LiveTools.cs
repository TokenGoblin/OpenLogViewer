using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// Connecting to a controller or a vehicle, and what comes back while attached.
///
/// <para>
/// No tool here scans for something to attach to and attaches to it. Every one
/// takes a port or an address, because a MicroSquirt in a running car and a
/// Speeduino on a bench look identical over a COM port, and choosing between
/// them is not a decision to make on somebody's behalf.
/// </para>
/// </summary>
[McpServerToolType]
public static class LiveTools
{
    internal const string NotLiveRefusal =
        "Not connected. Call connect_serial, connect_obd2, connect_obd2_wifi, connect_obd2_ble, "
        + "connect_ssm or connect_maxxecu first.";

    private const string AlreadyLiveRefusal =
        "Already connected. Call disconnect first.";

    [McpServerTool]
    [Description(
        "Every serial port on this machine, with the name Windows gives it — which is the "
        + "difference between picking a Bluetooth module and guessing.")]
    public static Task<object> ListSerialPorts(IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() => new
        {
            listed = true,
            ports = SerialPortNames.Describe().Select(p => new
            {
                port = p.PortName,
                description = p.Description,
                bluetooth = p.IsBluetooth,
            }).ToArray(),
        });

    [McpServerTool]
    [Description(
        "Bluetooth LE adapters this machine knows about. A great many OBD2 dongles are BLE-only "
        + "and never appear as a serial port, so they will not be in list_serial_ports.")]
    public static Task<object> ListBleAdapters(IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            try
            {
                return new
                {
                    listed = true,
                    adapters = BleDevices.All().Select(d => new
                    {
                        name = d.Name,
                        address = d.Address.ToString("X12"),
                        looksLikeObd2 = d.IsObd2,
                    }).ToArray(),
                };
            }
            catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
            {
                return new { listed = false, reason = $"Bluetooth LE is not available: {e.Message}" };
            }
        });

    [McpServerTool]
    [Description(
        "Connects to a MegaSquirt, MicroSquirt, rusEFI or Speeduino over a serial port, and reads "
        + "its tune. Note that opening the port resets some boards — a Speeduino restarts, losing "
        + "anything written but not burned.")]
    public static Task<object> ConnectSerial(
        [Description("Port name, for example COM8.")] string port,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        Connect(vm, dispatcher, () => vm.Connect(port), $"connect to {port}");

    [McpServerTool]
    [Description("Connects to an OBD2 vehicle through an ELM327 adapter on a serial port.")]
    public static Task<object> ConnectObd2(
        [Description("Port name, for example COM5.")] string port,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        Connect(vm, dispatcher, () => vm.ConnectObd2(port), $"connect to the adapter on {port}");

    [McpServerTool]
    [Description(
        "Connects to an OBD2 vehicle through a Wi-Fi ELM327 dongle. Leave the address empty to "
        + "try the ones these adapters are known to use.")]
    public static Task<object> ConnectObd2Wifi(
        [Description("Address and port, for example 192.168.0.10:35000. Empty to search.")]
        string address = "",
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        Connect(vm, dispatcher, () => vm.ConnectObd2Wifi(address),
            address.Length == 0 ? "find a Wi-Fi adapter" : $"connect to {address}");

    [McpServerTool]
    [Description("Connects to an OBD2 vehicle through a Bluetooth LE dongle, by adapter name.")]
    public static Task<object> ConnectObd2Ble(
        [Description("Adapter name, as list_ble_adapters reports it.")] string name,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.IsLive) return new { connected = false, reason = AlreadyLiveRefusal };

            BleDevice? adapter = BleDevices.All().FirstOrDefault(
                d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

            if (adapter is null)
                return new { connected = false, reason = $"No Bluetooth LE adapter called '{name}'. Call list_ble_adapters." };

            try
            {
                vm.ConnectObd2Ble(adapter);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException)
            {
                return new { connected = false, reason = e.Message };
            }

            return Describe(vm);
        });

    [McpServerTool]
    [Description("Connects to a Subaru over its own SSM protocol, which is a deliberate choice rather than something guessed from the adapter.")]
    public static Task<object> ConnectSsm(
        [Description("Port name, for example COM10.")] string port,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        Connect(vm, dispatcher, () => vm.ConnectSsm(port), $"connect over SSM on {port}");

    [McpServerTool]
    [Description("Connects to a MaxxECU over a serial port.")]
    public static Task<object> ConnectMaxxEcu(
        [Description("Port name.")] string port,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        Connect(vm, dispatcher, () => vm.ConnectMaxxEcu(port), $"connect to the MaxxECU on {port}");

    [McpServerTool]
    [Description("Closes the live connection, and the recording with it if one is running.")]
    public static Task<object> Disconnect(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.IsLive) return new { disconnected = false, reason = "Nothing is connected." };

            vm.Disconnect();

            return new { disconnected = true };
        });

    [McpServerTool]
    [Description("Whether a controller or vehicle is attached, how the link is behaving, and what it is offering.")]
    public static Task<object> GetLiveStatus(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() => new
        {
            running = vm.IsLive,
            healthy = vm.LiveHealthy,
            status = vm.LiveStatus,
            detail = vm.LiveDetail,
            obd2 = vm.IsObd2Live,
            rate = vm.LiveRate,
            channels = vm.LiveChannelNames.ToArray(),
            undecoded = vm.IsObd2Live ? vm.Obd2Gaps : null,
            recording = vm.IsRecording,
            recordingPath = vm.IsRecording ? vm.RecordingPath : null,
        });

    [McpServerTool]
    [Description("One snapshot of every live channel's current value.")]
    public static Task<object> ReadLiveChannels(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.IsLive) return new { read = false, reason = NotLiveRefusal };

            return new
            {
                read = true,
                at = DateTimeOffset.Now.ToString("O"),
                channels = vm.Channels.Select(c => new
                {
                    name = c.Name,
                    units = c.Units,
                    value = c.Value,
                }).ToArray(),
            };
        });

    [McpServerTool]
    [Description("Starts recording the live session to a file. Leave the path empty to use the workspace's suggested name.")]
    public static Task<object> StartRecording(
        [Description("Where to write it. Empty for the suggested path.")] string path = "",
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.IsLive) return new { started = false, reason = NotLiveRefusal };
            if (vm.IsRecording) return new { started = false, reason = "Already recording." };

            string said = vm.StartRecording(path.Length == 0 ? vm.SuggestedRecordingPath() : path);

            return vm.IsRecording
                ? new { started = true, path = vm.RecordingPath, message = said }
                : (object)new { started = false, reason = said };
        });

    [McpServerTool]
    [Description("Stops the recording and returns where it was written.")]
    public static Task<object> StopRecording(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.IsRecording) return new { stopped = false, reason = "Nothing is being recorded." };

            string said = vm.StopRecording();

            return new { stopped = true, path = vm.RecordingPath, message = said, summary = vm.RecordingSummary };
        });

    /// <summary>
    /// Decides whether to connect and connects in one dispatcher turn.
    ///
    /// <para>
    /// Atomic on purpose: another call queued on the UI thread — a second tool,
    /// or the Connect menu — must not be able to interleave between the check and
    /// the attempt, which would open two ports.
    /// </para>
    /// </summary>
    private static Task<object> Connect(
        MainViewModel vm, IUiDispatcher dispatcher, Action connect, string what) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.IsLive) return new { connected = false, reason = AlreadyLiveRefusal };

            try
            {
                connect();
            }
            catch (Exception e)
                when (e is IOException or InvalidOperationException or TimeoutException
                          or UnauthorizedAccessException or EcuProtocolException)
            {
                return new { connected = false, reason = $"Could not {what}: {e.Message}" };
            }

            return vm.IsLive
                ? Describe(vm)
                : (object)new { connected = false, reason = vm.LiveStatus };
        });

    private static object Describe(MainViewModel vm) => new
    {
        connected = true,
        status = vm.LiveStatus,
        detail = vm.LiveDetail,
        healthy = vm.LiveHealthy,
        obd2 = vm.IsObd2Live,
        channels = vm.LiveChannelNames.ToArray(),
        tune = new
        {
            loaded = vm.HasEcuTune,
            source = vm.TuneSource,
            tables = vm.EcuTables.Count,
            placeholder = vm.TuneIsPlaceholder,
        },
    };
}
