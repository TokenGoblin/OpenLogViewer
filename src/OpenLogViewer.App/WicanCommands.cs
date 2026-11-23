using System.Globalization;
using System.IO;
using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.App;

/// <summary>
/// What a WiCAN operation did.
/// </summary>
/// <param name="Done">Whether the request reached the unit and it said yes.</param>
/// <param name="Declined">Whether a person answered No in the window, so nothing was sent.</param>
/// <param name="Message">What to show a person, and the reason when it did not happen.</param>
/// <param name="Output">What it printed, for the operations that print.</param>
public sealed record WicanOutcome(bool Done, bool Declined, string Message, IReadOnlyList<string>? Output = null)
{
    public static WicanOutcome Refused(string why) => new(false, false, why);

    public static readonly WicanOutcome NotConfirmed =
        new(false, true, "Not confirmed in the window, so nothing was sent.");
}

/// <summary>
/// The WiCAN Pro's custom-firmware features, as the view model offers them.
///
/// <para>
/// <b>Where the gate is.</b> Five things here change the unit or can reach the
/// vehicle's bus, and every one asks <see cref="IWriteConfirmation"/> first, after
/// its own checks and immediately before the request goes out — the same rule
/// every ECU write here follows, and for the same reason: the question quotes what
/// will actually be sent, and an agent calling these over MCP waits for a person.
/// </para>
/// <list type="bullet">
/// <item><see cref="WicanConfigSave"/> — writes the unit's flash.</item>
/// <item><see cref="WicanCanArm"/> — once per arming. Individual sends are not asked
/// about again, which is the owner's decision recorded in the contract: the gate is
/// deliberately light, and the question is the moment to be sure.</item>
/// <item><see cref="WicanCanResume"/> — clears a transmit fault latch, which is what
/// lets transmit be armed again after the bus refused it.</item>
/// <item><see cref="WicanLuaPut"/>, <see cref="WicanLuaRun"/>, <see cref="WicanLuaEval"/> —
/// a script can transmit on the bus by itself when <c>lua_can_tx</c> is enabled,
/// with no arming and nobody watching, so the question says whether it is.</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel
{
    private WicanDevice? _wican;

    /// <summary>
    /// A WiCAN Pro connection that answered as stock firmware, watched for a
    /// <c>!BOOT</c> after which the custom firmware may answer after all (a unit
    /// that was restarting when it was connected). Null otherwise.
    /// </summary>
    private WicanProConnection? _wicanPending;
    private string _wicanPendingPort = "";

    /// <summary>The WiCAN Pro's custom firmware, while an OBD2 session is connected through one. Null otherwise.</summary>
    public WicanDevice? Wican
    {
        get
        {
            AdoptLateWcp();
            return _wican;
        }
    }

    /// <summary>Whether a WiCAN Pro with the custom firmware is connected.</summary>
    public bool IsWicanLive => Wican is not null && IsLive;

    /// <summary>
    /// Connects to a WiCAN Pro over a transport already chosen: the WCP probe,
    /// then the ELM327 session on the same link. The seam the tests drive, and
    /// the path <see cref="ConnectObd2(string)"/> takes for a Pro's USB port.
    /// </summary>
    public void ConnectObd2WicanPro(IEcuTransport transport, string port, TimeSpan? helloTimeout = null) =>
        StartWicanPro(WicanPro.Connect(transport, BatchMemory, helloTimeout), port);

    private void StartWicanPro(WicanProConnection pro, string port)
    {
        StartObd2(pro.Source, port, pro.Wcp);

        if (pro.Wcp is null)
        {
            // Taken over when next asked for, on the asking thread (see Wican),
            // rather than from the worker thread that finds it.
            _wicanPending = pro;
            _wicanPendingPort = port;
        }
    }

    /// <summary>
    /// Takes over a control lane that answered after the session started, on the
    /// thread asking — the interface thread — rather than the one that found it.
    /// </summary>
    private void AdoptLateWcp()
    {
        if (_wicanPending is not { } pending || _wican is not null) return;
        if (pending.Wcp is not { } late) return;

        _wicanPending = null;

        if (_live is null || !ReferenceEquals(_obd2, pending.Source))
        {
            late.Dispose();
            return;
        }

        _wican = new WicanDevice(late, _wicanPendingPort);
    }

    /// <summary>Stops watching for a late control lane, disposing one that answered and was never taken over.</summary>
    private void ForgetWicanPending()
    {
        if (_wicanPending is not { } pending) return;

        _wicanPending = null;
        pending.StopWatching();

        if (_wican is null) pending.Wcp?.Dispose();
    }

    private const string NoWican =
        "No WiCAN Pro with the custom WCP firmware is connected. Connect one over USB with connect_obd2; "
        + "a stock WiCAN answers as plain ELM327 and has none of this.";

    private static bool IsWcpFailure(Exception e) =>
        e is WcpException or TimeoutException or IOException or InvalidDataException or InvalidOperationException
            or WcpNotSupportedException;

    // ----- settings ----------------------------------------------------------

    /// <summary>
    /// Saves the unit's staged settings to its flash, after a person has seen the
    /// list. Nothing staged is nothing to ask about, and nothing is sent.
    /// </summary>
    public WicanOutcome WicanConfigSave(bool reboot = false)
    {
        if (Wican is not { } wican) return WicanOutcome.Refused(NoWican);

        IReadOnlyList<WcpConfigChange> diff;

        try
        {
            diff = wican.Client.ConfigDiff();
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused($"Could not read the staged changes: {e.Message}");
        }

        if (diff.Count == 0) return WicanOutcome.Refused("Nothing is staged, so there is nothing to save.");

        var detail = new StringBuilder();

        foreach (WcpConfigChange c in diff)
            detail.AppendLine(CultureInfo.InvariantCulture, $"    {c.Key}: {Shown(c.Old)} → {Shown(c.New)}");

        detail.AppendLine();
        detail.Append("The WiCAN checks the settings the way it does at boot and refuses a save that would fail, ");
        detail.Append("so this cannot leave it factory-resetting. ");
        detail.Append(reboot
            ? "It then restarts, which ends this connection."
            : "They take effect at its next restart.");

        if (diff.Any(c => c.Key == "lua_can_tx" && c.New == "enable"))
        {
            detail.Append("\n\nThis ENABLES CAN transmit from Lua scripts. Once saved, a script on the unit can send ");
            detail.Append("frames on the vehicle's bus by itself, on any identifier, with no arming and nobody asked.");
        }

        if (!_confirm.Confirm(new WriteRequest(
                WriteKind.DeviceSettingsSave,
                $"Save {diff.Count} changed setting{(diff.Count == 1 ? "" : "s")} to the WiCAN's flash"
                + (reboot ? " and restart it?" : "?"),
                detail.ToString())))
        {
            return WicanOutcome.NotConfirmed;
        }

        try
        {
            return new WicanOutcome(true, false, wican.Client.ConfigSave(reboot));
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused($"The WiCAN did not save: {e.Message}");
        }

        static string Shown(string value) => value.Length == 0 ? "(empty)" : value;
    }

    // ----- CAN transmit ------------------------------------------------------

    /// <summary>
    /// Arms CAN transmit on the unit, once a person has agreed. The one question
    /// this gate asks: every send after it goes without one, until it disarms.
    /// </summary>
    public WicanOutcome WicanCanArm()
    {
        if (Wican is not { } wican) return WicanOutcome.Refused(NoWican);

        if (wican.Client.IsArmed) return new WicanOutcome(true, false, "Already armed.");

        if (wican.Client.TransmitFault is { } latched)
            return WicanOutcome.Refused(
                $"Transmit is latched off after a bus fault ({latched}). Find out why first; wican_can_resume clears "
                + "the latch, which a person confirms, and then it can be armed again.");

        if (!_confirm.Confirm(new WriteRequest(
                WriteKind.CanArm,
                "Arm CAN transmit on the WiCAN, for any identifier, until it is disarmed?",
                "Identifiers: any. Time limit: none — it stays armed until it is disarmed, the WiCAN restarts, "
                + "or 10 s pass with nothing from this computer. Up to 200 frames a second.\n\n"
                + "Once armed, this application — or an AI agent working through it — can put frames on the "
                + "vehicle's CAN bus without asking again for each one.\n\n"
                + "Do not arm on a vehicle that is moving. The bus at the OBD port reaches the modules that run "
                + "the engine, the brakes and the steering, and a wrong frame can make them misbehave while the "
                + "car is being driven.")))
        {
            return WicanOutcome.NotConfirmed;
        }

        try
        {
            wican.Client.CanArm();
            return new WicanOutcome(true, false, "Armed. Every send from here on goes without asking, until disarmed.");
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused(e is WcpException { IsNoTraffic: true }
                ? "The WiCAN refused to arm: it has received no CAN frames cleanly in the last second, which is its "
                  + "check that the bitrate is right before it lets the controller drive the bus. Is the controller "
                  + "started (wican_can_stream start) at the vehicle's bitrate, and the ignition on?"
                : $"The WiCAN did not arm: {e.Message}");
        }
    }

    /// <summary>
    /// Clears a latched transmit fault (<c>can resume</c>), once a person has agreed.
    /// It does not arm, but it is what lets transmit back on after the bus said
    /// something was wrong, so it is asked like an arming.
    /// </summary>
    public WicanOutcome WicanCanResume()
    {
        if (Wican is not { } wican) return WicanOutcome.Refused(NoWican);

        string fault = wican.Client.TransmitFault ?? "none seen by this application";

        if (!_confirm.Confirm(new WriteRequest(
                WriteKind.CanResume,
                "Clear the WiCAN's transmit fault latch, so CAN transmit can be armed again?",
                $"The latched fault: {fault}.\n\n"
                + "The WiCAN latched transmit off because the bus refused its frames — a transmit timeout, bus-off, "
                + "error-passive, or the error counter climbing. That usually means a wrong bitrate, a bus with nothing "
                + "to acknowledge, or frames a module objects to. Clearing it does not arm transmit; it lets it be armed "
                + "again, and also lifts the suspension of Lua transmit.\n\n"
                + "Do not do this on a vehicle that is moving.")))
        {
            return WicanOutcome.NotConfirmed;
        }

        try
        {
            wican.Client.CanResume();
            return new WicanOutcome(true, false, "Fault latch cleared. Transmit is still disarmed; arm it again to send.");
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused($"The WiCAN did not clear the latch: {e.Message}");
        }
    }

    /// <summary>
    /// Sends one frame. Not confirmed: the arming was. Refused when not armed, so
    /// a send can never be the thing that arms.
    /// </summary>
    public WicanOutcome WicanCanSend(uint id, byte[] data, bool extended = false)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (Wican is not { } wican) return WicanOutcome.Refused(NoWican);

        if (!wican.Client.IsArmed)
            return WicanOutcome.Refused("Transmit is not armed. Arm it first (wican_can_arm), which a person confirms.");

        try
        {
            return new WicanOutcome(true, false, wican.Client.CanSend(id, data, extended));
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused(e is WcpException { IsRateLimited: true }
                ? "Refused by the WiCAN's 200 frames/s cap. Send more slowly."
                : $"Not sent: {e.Message}");
        }
    }

    // ----- Lua ---------------------------------------------------------------

    /// <summary>Uploads a script. Asked first, because a script uploaded can be run — and autostarted.</summary>
    public WicanOutcome WicanLuaPut(string name, byte[] script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (Wican is not { } wican) return WicanOutcome.Refused(NoWican);
        if (!WcpClient.IsScriptName(name)) return WicanOutcome.Refused($"'{name}' is not a script name: letters, digits, '_' or '-', without '.lua'.");
        if (script.Length == 0) return WicanOutcome.Refused("The script is empty.");

        bool autostart;

        try
        {
            autostart = (wican.Client.ConfigGet("lua_autostart") ?? "")
                .Split(',', StringSplitOptions.TrimEntries).Contains(name, StringComparer.Ordinal);
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            autostart = false;
        }

        string detail = LuaTransmitDetail(wican.LuaTransmitState())
                        + (autostart
                            ? $"\n\n'{name}' is listed in lua_autostart, so it will run by itself at the WiCAN's next restart."
                            : "\n\nUploading does not run it.");

        if (!_confirm.Confirm(new WriteRequest(
                WriteKind.LuaUpload,
                $"Upload the Lua script '{name}' ({script.Length:N0} bytes) to the WiCAN?",
                detail)))
        {
            return WicanOutcome.NotConfirmed;
        }

        try
        {
            long size = wican.Client.LuaPut(name, script);
            return new WicanOutcome(true, false, $"Uploaded {name}, {size:N0} bytes.");
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused($"The upload failed: {e.Message}");
        }
    }

    /// <summary>Starts a stored script now.</summary>
    public WicanOutcome WicanLuaRun(string name)
    {
        if (Wican is not { } wican) return WicanOutcome.Refused(NoWican);
        if (!WcpClient.IsScriptName(name)) return WicanOutcome.Refused($"'{name}' is not a script name.");

        WcpLuaFile? file;

        try
        {
            file = wican.Client.LuaList().FirstOrDefault(f => f.Name == name);
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused($"Could not list the scripts: {e.Message}");
        }

        if (file is null) return WicanOutcome.Refused($"The WiCAN has no script named '{name}'.");

        if (!_confirm.Confirm(new WriteRequest(
                WriteKind.LuaRun,
                $"Run the Lua script '{name}' ({file.Size:N0} bytes) on the WiCAN now?",
                LuaTransmitDetail(wican.LuaTransmitState())
                + "\n\nIt keeps running after this application disconnects, until it is stopped or the WiCAN restarts.")))
        {
            return WicanOutcome.NotConfirmed;
        }

        try
        {
            wican.Client.LuaRun(name);
            return new WicanOutcome(true, false, $"{name} is running.");
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused(e is WcpException { Code: "ARGS" }
                ? $"It did not compile: {e.Message}"
                : $"It did not start: {e.Message}");
        }
    }

    /// <summary>Runs a chunk of Lua in a scratch VM on the unit, and returns what it printed.</summary>
    public WicanOutcome WicanLuaEval(string code)
    {
        if (Wican is not { } wican) return WicanOutcome.Refused(NoWican);
        if (string.IsNullOrWhiteSpace(code)) return WicanOutcome.Refused("There is no code to run.");

        int size = Encoding.UTF8.GetByteCount(code);

        // Refused before the question rather than after it: the code travels as
        // base64, and what does not fit in one request should not be asked about.
        if (size > WcpClient.MaxEvalBytes)
            return WicanOutcome.Refused(
                $"That code is {size:N0} bytes, too long for one request (at most {WcpClient.MaxEvalBytes} fit). "
                + "Upload it with wican_lua_put and run it.");
        string shown = code.Length > 300 ? code[..300] + "…" : code;

        if (!_confirm.Confirm(new WriteRequest(
                WriteKind.LuaRun,
                $"Run this Lua code ({size:N0} bytes) on the WiCAN now?",
                $"    {shown}\n\n" + LuaTransmitDetail(wican.LuaTransmitState())
                + "\n\nIt runs once, in a scratch VM, for at most 5 s.")))
        {
            return WicanOutcome.NotConfirmed;
        }

        try
        {
            WcpConsoleResult result = wican.Client.LuaEval(code);
            return new WicanOutcome(result.Ok, false, result.Ok ? "Ran." : $"It failed: {result.Error}", result.Output);
        }
        catch (Exception e) when (IsWcpFailure(e))
        {
            return WicanOutcome.Refused($"It did not run: {e.Message}");
        }
    }

    /// <summary>What a Lua question says about transmit, by the unit's own report of it.</summary>
    private static string LuaTransmitDetail(string tx) => tx switch
    {
        "disabled" =>
            "lua_can_tx is disabled, so a script cannot transmit on the CAN bus. It can still read the bus "
            + "and write to the SD card.",
        "suspended" =>
            "lua_can_tx is enabled but Lua transmit is suspended after a bus fault; it stays off until "
            + "lua resume-tx. While it is lifted, a script can transmit on any identifier.",
        "enabled" =>
            "lua_can_tx is ENABLED: a script can transmit on the vehicle's CAN bus by itself, on any identifier, "
            + "with no arming and nobody asked. Do not do this on a vehicle that is moving.",
        _ =>
            "Whether lua_can_tx is enabled could not be read. Assume a script can transmit on the vehicle's "
            + "CAN bus, and do not do this on a vehicle that is moving.",
    };
}
