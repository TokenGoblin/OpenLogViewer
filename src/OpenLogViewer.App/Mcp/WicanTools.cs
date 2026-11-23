using System.ComponentModel;
using System.IO;
using System.Text;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// A MeatPi WiCAN Pro running the custom WCP firmware: its settings, console,
/// AutoPID values, SD logs, CAN stream, CAN transmit and Lua scripts.
///
/// <para>
/// <b>Only with the custom firmware.</b> Every tool here refuses, with a reason,
/// unless an OBD2 connection was made to a WiCAN Pro over USB and the unit
/// answered WCP's <c>hello</c>. A stock WiCAN is plain ELM327 and has none of this.
/// None of it has been run against a real unit yet: it was built against the
/// contract and a simulation of the firmware.
/// </para>
///
/// <para>
/// <b>What a person is asked, and what they are not.</b> Four things go through
/// the same <see cref="IWriteConfirmation"/> dialog as an ECU write, and the call
/// blocks until it is answered: saving settings to the unit's flash
/// (<c>wican_config_save</c>), arming CAN transmit (<c>wican_can_arm</c>, once per
/// arming), and uploading, running or evaluating Lua (<c>wican_lua_put</c>,
/// <c>wican_lua_run</c>, <c>wican_lua_eval</c>), because a script can transmit by
/// itself when <c>lua_can_tx</c> is enabled. <c>wican_can_send</c> does not ask:
/// it requires transmit to be armed, and the arming was the question. Stopping
/// things — <c>wican_can_disarm</c>, <c>wican_lua_stop</c> — never asks, because
/// the safe direction should never wait for a person.
/// </para>
///
/// <para>
/// <b>Threading.</b> The reads run off the interface thread: a WCP request can
/// take seconds, and a log download many of them, and the window should not
/// freeze while it waits. The gated calls run on it, because the dialog does.
/// </para>
/// </summary>
[McpServerToolType]
public static class WicanTools
{
    internal const string NotWican =
        "No WiCAN Pro with the custom WCP firmware is connected. Call connect_obd2 with the WiCAN Pro's "
        + "USB port; a stock WiCAN answers as plain ELM327 and offers none of these tools.";

    /// <summary>The connected unit, looked up on the interface thread where the view model lives.</summary>
    private static Task<WicanDevice?> Device(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync(() => vm.IsWicanLive ? vm.Wican : null);

    /// <summary>
    /// Runs a read against the unit off the interface thread, turning a protocol
    /// failure into a structured refusal rather than a protocol error.
    /// </summary>
    private static async Task<object> Read(
        MainViewModel vm, IUiDispatcher dispatcher, Func<WicanDevice, object> read)
    {
        WicanDevice? wican = await Device(vm, dispatcher);
        if (wican is null) return new { ok = false, reason = NotWican };

        try
        {
            return await Task.Run(() => read(wican));
        }
        catch (Exception e) when (e is WcpException or TimeoutException or IOException or InvalidDataException
                                      or InvalidOperationException or WcpNotSupportedException or ArgumentException)
        {
            return new { ok = false, reason = e.Message, code = (e as WcpException)?.Code };
        }
    }

    private static object Outcome(WicanOutcome o) =>
        new { ok = o.Done, declined = o.Declined, message = o.Message, output = o.Output };

    private static object Values(WcpValues values) =>
        values.Pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    // ----- session -----------------------------------------------------------

    [McpServerTool]
    [Description(
        "The WiCAN Pro's status: firmware, supply voltage, Wi-Fi, CAN controller mode and counters, whether the "
        + "CAN stream is on, whether transmit is armed, AutoPID, the SD logger, memory. Needs an OBD2 "
        + "connection to a WiCAN Pro over USB running the custom WCP firmware. Touches nothing.")]
    public static Task<object> WicanStatus(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w => new
        {
            ok = true,
            firmware = w.Identity?.Firmware,
            device = w.Identity?.Device,
            obdChip = w.Identity?.Obd,
            capabilities = w.Identity?.Capabilities,
            armedHere = w.Client.IsArmed,
            status = Values(w.Client.Status()),
        });

    // ----- settings ----------------------------------------------------------

    [McpServerTool]
    [Description(
        "Reads the WiCAN's settings (its config.json, as staged in RAM): every key, or one. Passwords are "
        + "always shown as ****; there is no tool that reveals them.")]
    public static Task<object> WicanConfigGet(
        MainViewModel vm, IUiDispatcher dispatcher,
        [Description("One setting's key, e.g. 'protocol'. Omit for all of them.")] string? key = null) =>
        Read(vm, dispatcher, w =>
        {
            if (string.IsNullOrWhiteSpace(key)) return new { ok = true, settings = Values(w.Client.ConfigGet()) };

            string? value = w.Client.ConfigGet(key.Trim());
            return new { ok = value is not null, settings = (object)new Dictionary<string, string?> { [key.Trim()] = value } };
        });

    [McpServerTool]
    [Description(
        "Stages one setting change in the WiCAN's RAM copy of its settings. Nothing reaches its flash until "
        + "wican_config_save, which a person confirms; wican_config_diff shows what is staged and "
        + "wican_config_discard drops it. Values are strings, as in config.json.")]
    public static Task<object> WicanConfigSet(
        [Description("The key, e.g. 'sta_ssid'.")] string key,
        [Description("The new value. May contain spaces; not line breaks.")] string value,
        MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w =>
        {
            w.Client.ConfigSet(key.Trim(), value);
            return new { ok = true, staged = key.Trim(), changes = Diff(w) };
        });

    [McpServerTool]
    [Description("Lists the WiCAN's staged setting changes, old and new. Passwords are masked.")]
    public static Task<object> WicanConfigDiff(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w => new { ok = true, changes = Diff(w) });

    [McpServerTool]
    [Description("Drops every staged setting change on the WiCAN. Nothing is written.")]
    public static Task<object> WicanConfigDiscard(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w =>
        {
            w.Client.ConfigDiscard();
            return new { ok = true };
        });

    private static object[] Diff(WicanDevice w) =>
        [.. w.Client.ConfigDiff().Select(c => new { key = c.Key, from = c.Old, to = c.New })];

    [McpServerTool]
    [Description(
        "Saves the WiCAN's staged settings to its flash. Shows a confirmation dialog listing every change and "
        + "waits: nothing is saved unless a person clicks OK. The WiCAN validates them as it does at boot and "
        + "refuses a save that would fail (the reason names the key). Settings take effect after a restart; "
        + "reboot=true restarts it, which ends the connection.")]
    public static Task<object> WicanConfigSave(
        MainViewModel vm, IUiDispatcher dispatcher,
        [Description("Restart the WiCAN after saving.")] bool reboot = false) =>
        dispatcher.InvokeAsync(() => Outcome(vm.WicanConfigSave(reboot)));

    // ----- console and AutoPID -----------------------------------------------

    [McpServerTool]
    [Description(
        "Runs one of the WiCAN firmware's own console commands — 'wifi --status', 'sdcard --info', "
        + "'system --voltage' and the rest — and returns what it printed. Console commands can change the unit, "
        + "so use the specific tools where one exists.")]
    public static Task<object> WicanConsole(
        [Description("The console command line.")] string command, MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w =>
        {
            WcpConsoleResult r = w.Client.Console(command);
            return new { ok = r.Ok, output = r.Output, error = r.Error.Length > 0 ? r.Error : null };
        });

    [McpServerTool]
    [Description(
        "Reads the WiCAN's AutoPID values — what it is polling from the car by itself — as its JSON object, "
        + "or one value by name.")]
    public static Task<object> WicanAutopid(
        MainViewModel vm, IUiDispatcher dispatcher,
        [Description("One value's name, e.g. 'ENGINE_RPM'. Omit for all.")] string? name = null) =>
        Read(vm, dispatcher, w => string.IsNullOrWhiteSpace(name)
            ? new { ok = true, json = (string?)w.Client.AutopidData(), value = (string?)null }
            : new { ok = true, json = (string?)null, value = w.Client.AutopidGet(name.Trim()) });

    // ----- logs --------------------------------------------------------------

    [McpServerTool]
    [Description("Lists the files in the WiCAN's SD card logger folder, with sizes, and the logger's status.")]
    public static Task<object> WicanLogList(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w => new
        {
            ok = true,
            logger = Values(w.Client.LogStatus()),
            files = w.Client.LogList().Select(f => new { name = f.Name, bytes = f.Size, modified = f.Modified?.ToString("o") }),
        });

    [McpServerTool]
    [Description(
        "Downloads one SD card log file from the WiCAN to this computer, a chunk at a time (about 3 KB a "
        + "request, so a large file takes a while). Returns where it was saved.")]
    public static Task<object> WicanLogRead(
        [Description("The file's name, as wican_log_list gives it.")] string name,
        MainViewModel vm, IUiDispatcher dispatcher,
        [Description("Where to save it. Omit to save into the workspace's logs folder under the same name.")] string? path = null) =>
        Read(vm, dispatcher, w =>
        {
            string target = string.IsNullOrWhiteSpace(path)
                ? Path.Combine(Workspace.Ensure(vm.Workspace.Logs), "wican-" + Path.GetFileName(name))
                : Path.GetFullPath(path);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            long bytes;

            using (var file = new FileStream(target, FileMode.Create, FileAccess.Write))
                bytes = w.Client.LogDownload(name, file);

            return new { ok = true, path = target, bytes };
        });

    // ----- CAN ---------------------------------------------------------------

    [McpServerTool]
    [Description(
        "The WiCAN's CAN controller: mode, bitrate, frames received and sent, drops, bus errors, whether "
        + "transmit is armed, and whether a recording from wican_can_stream is running.")]
    public static Task<object> WicanCanStatus(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w => new
        {
            ok = true,
            can = Values(w.Client.CanStatus()),
            armedHere = w.Client.IsArmed,
            recording = w.Capture is { } c
                ? new { running = c.IsRunning, path = c.Path, frames = c.FrameCount, dropped = c.Dropped, seconds = Math.Round(c.Elapsed, 1), stoppedBecause = c.StoppedBecause }
                : null,
        });

    [McpServerTool]
    [Description(
        "Records the CAN bus through the WiCAN to a file. action='start' starts the controller listen-only "
        + "(it never transmits), turns the stream on and records until 'stop' or a limit — maxSeconds "
        + "(default 60, at most 3600) or maxFrames (default 1,000,000). action='stop' ends it and writes a "
        + "byte log beside it (name-bytes.csv) that open_log can open: one channel per byte that moved. "
        + "A busy 500 kbit/s bus is near what the USB link carries, so give a filter: id and mask in hex "
        + "pass frames where (frame & mask) == (id & mask).")]
    public static Task<object> WicanCanStream(
        [Description("'start' or 'stop'.")] string action,
        MainViewModel vm, IUiDispatcher dispatcher,
        [Description("Where to write the raw frames (CSV). Omit for the workspace's logs folder.")] string? path = null,
        [Description("Stop after this many seconds.")] double maxSeconds = 60,
        [Description("Stop after this many frames.")] long maxFrames = 1_000_000,
        [Description("Filter identifier, hex, e.g. '7E8'. Needs mask.")] string? id = null,
        [Description("Filter mask, hex, e.g. '7F8'. Needs id.")] string? mask = null) =>
        Read(vm, dispatcher, w =>
        {
            switch (action?.Trim().ToLowerInvariant())
            {
                case "start":
                {
                    if (id is null != mask is null) return new { ok = false, reason = "A filter needs both id and mask, or neither." };

                    uint? filterId = null, filterMask = null;

                    if (id is not null)
                    {
                        if (!WcpClient.TryParseId(id, out uint i) || !WcpClient.TryParseId(mask!, out uint m))
                            return new { ok = false, reason = "id and mask are hex, e.g. '7E8' and '7F8'." };

                        filterId = i;
                        filterMask = m;
                    }

                    string target = string.IsNullOrWhiteSpace(path)
                        ? Path.Combine(Workspace.Ensure(vm.Workspace.Logs), $"wican-can-{DateTime.Now:yyyyMMdd-HHmmss}.csv")
                        : Path.GetFullPath(path);

                    WcpCanRecorder r = w.StartCapture(
                        target, Math.Clamp(maxSeconds, 0.1, 3600), Math.Clamp(maxFrames, 1, 10_000_000), filterId, filterMask);

                    return (object)new { ok = true, recording = true, path = r.Path, maxSeconds = r.MaxSeconds, maxFrames = r.MaxFrames };
                }

                case "stop":
                {
                    WcpCanRecorder? r = w.StopCapture();
                    if (r is null) return new { ok = false, reason = "No recording was started." };

                    return new
                    {
                        ok = true,
                        recording = false,
                        path = r.Path,
                        frames = r.FrameCount,
                        identifiers = r.Ids.Count,
                        dropped = r.Dropped,
                        seconds = Math.Round(r.Elapsed, 1),
                        stoppedBecause = r.StoppedBecause,
                        byteLog = w.LastByteLog,
                    };
                }

                default:
                    return new { ok = false, reason = "action is 'start' or 'stop'." };
            }
        });

    [McpServerTool]
    [Description(
        "Arms CAN transmit on the WiCAN: any identifier, up to 200 frames a second, until wican_can_disarm, a "
        + "restart, or 10 s with nothing from this computer (this application keeps it alive while connected). "
        + "Shows a confirmation dialog — once per arming — and waits: nothing is armed unless a person clicks OK. "
        + "After that, wican_can_send does not ask again. Never arm on a moving vehicle.")]
    public static Task<object> WicanCanArm(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync(() => Outcome(vm.WicanCanArm()));

    [McpServerTool]
    [Description("Disarms CAN transmit on the WiCAN at once. Never asks.")]
    public static Task<object> WicanCanDisarm(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w =>
        {
            w.Client.CanDisarm();
            return new { ok = true };
        });

    [McpServerTool]
    [Description(
        "Sends one CAN frame through the WiCAN. Requires transmit to be armed (wican_can_arm, which a person "
        + "confirmed); this call does not ask again. Never retried: a frame is sent at most once. The WiCAN "
        + "caps transmit at 200 frames a second. The controller must be running — wican_can_stream start, "
        + "or the console — and it returns to listen-only after each frame.")]
    public static Task<object> WicanCanSend(
        [Description("The identifier in hex, e.g. '7E0' or '18DAF110'.")] string id,
        [Description("0 to 8 data bytes in hex, e.g. '02010C'. Empty for none.")] string data,
        MainViewModel vm, IUiDispatcher dispatcher,
        [Description("A 29-bit identifier. Required for one above 7FF.")] bool extended = false)
    {
        if (!WcpClient.TryParseId(id ?? "", out uint frameId))
            return Task.FromResult<object>(new { ok = false, declined = false, message = $"'{id}' is not a hex identifier." });

        byte[] bytes;

        try
        {
            bytes = Convert.FromHexString((data ?? "").Replace(" ", "", StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return Task.FromResult<object>(new { ok = false, declined = false, message = $"'{data}' is not hex bytes." });
        }

        return dispatcher.InvokeAsync(() => Outcome(vm.WicanCanSend(frameId, bytes, extended)));
    }

    [McpServerTool]
    [Description("The last 32 frames the WiCAN transmitted, from WCP and from Lua scripts, as it lists them.")]
    public static Task<object> WicanCanTxlog(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w => new { ok = true, frames = w.Client.CanTxLog() });

    // ----- Lua ---------------------------------------------------------------

    [McpServerTool]
    [Description(
        "The WiCAN's Lua engine: whether scripts may transmit on the CAN bus (tx: enabled, disabled or "
        + "suspended — set by the lua_can_tx setting), transmit counts, and each loaded script's state.")]
    public static Task<object> WicanLuaStatus(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w =>
        {
            WcpLuaStatus s = w.Client.LuaStatus();
            return new
            {
                ok = true,
                engine = Values(s.Values),
                transmitEnabled = s.TransmitEnabled,
                scripts = s.Scripts.Select(x => new { name = x.Name, state = x.State, memKb = x.MemKb, callbackMaxMs = x.CallbackMaxMs, queueDrops = x.QueueDrops, errors = x.Errors }),
            };
        });

    [McpServerTool]
    [Description("Lists the Lua scripts stored on the WiCAN, with sizes.")]
    public static Task<object> WicanLuaList(MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w => new
        {
            ok = true,
            scripts = w.Client.LuaList().Select(f => new { name = f.Name, bytes = f.Size }),
        });

    [McpServerTool]
    [Description("Reads one stored Lua script's source from the WiCAN.")]
    public static Task<object> WicanLuaGet(
        [Description("The script's name, without '.lua'.")] string name, MainViewModel vm, IUiDispatcher dispatcher) =>
        Read(vm, dispatcher, w => new { ok = true, name, source = Encoding.UTF8.GetString(w.Client.LuaGet(name)) });

    [McpServerTool]
    [Description(
        "Uploads a Lua script to the WiCAN (replacing one of the same name). Shows a confirmation dialog naming "
        + "the script, its size and whether lua_can_tx lets scripts transmit, and waits: nothing is uploaded "
        + "unless a person clicks OK. Uploading does not run it; wican_lua_run does.")]
    public static Task<object> WicanLuaPut(
        [Description("The script's name: letters, digits, '_' or '-', without '.lua'.")] string name,
        [Description("The Lua source.")] string source,
        MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync(() => Outcome(vm.WicanLuaPut(name, Encoding.UTF8.GetBytes(source ?? ""))));

    [McpServerTool]
    [Description(
        "Loads and starts a stored Lua script on the WiCAN now. Shows a confirmation dialog naming the script, "
        + "its size and whether lua_can_tx lets it transmit, and waits for a person. A compile error comes back "
        + "as the reason. The script keeps running after this application disconnects.")]
    public static Task<object> WicanLuaRun(
        [Description("The script's name.")] string name, MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync(() => Outcome(vm.WicanLuaRun(name)));

    [McpServerTool]
    [Description(
        "Stops one Lua script, or every script when no name is given — the kill switch, which also stops all Lua "
        + "transmit at once. Never asks.")]
    public static Task<object> WicanLuaStop(
        MainViewModel vm, IUiDispatcher dispatcher,
        [Description("The script to stop. Omit to stop all of them.")] string? name = null) =>
        Read(vm, dispatcher, w =>
        {
            w.Client.LuaStop(string.IsNullOrWhiteSpace(name) ? null : name.Trim());
            return new { ok = true, stopped = string.IsNullOrWhiteSpace(name) ? "all" : name.Trim() };
        });

    [McpServerTool]
    [Description(
        "Runs a chunk of Lua on the WiCAN in a scratch VM (5 s limit) and returns what it printed — the quick "
        + "way to poke at the unit. About 360 bytes of code fit in one request; upload anything longer with "
        + "wican_lua_put and run it. Shows a confirmation dialog with the code and "
        + "whether lua_can_tx lets it transmit, and waits for a person.")]
    public static Task<object> WicanLuaEval(
        [Description("The Lua code.")] string code, MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync(() => Outcome(vm.WicanLuaEval(code)));
}
