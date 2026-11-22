using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// Reading a vehicle's fault codes.
///
/// <para>
/// Clearing them is a tool too, but only through the same dialog the Clear button
/// shows (<see cref="MainViewModel.ClearFaults"/>). Erasing DTCs takes the freeze
/// frame with it and resets the readiness monitors, which a car has to re-earn over
/// a full drive cycle before an emissions test. So the agent can ask, and a person
/// at the window decides: the call blocks until the dialog is answered, and a "No"
/// erases nothing. An agent cannot answer that dialog.
/// </para>
/// </summary>
[McpServerToolType]
public static class FaultTools
{
    [McpServerTool]
    [Description(
        "Reads the vehicle's diagnostic trouble codes: stored, pending and permanent, with the "
        + "warning light state and the protocol the adapter negotiated. Needs an OBD2 connection "
        + "— call connect_obd2, connect_obd2_wifi or connect_obd2_ble first. "
        + "To erase them, call clear_faults — which a person must confirm in the window.")]
    public static Task<object> ScanFaults(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.IsObd2Live)
            {
                return new
                {
                    scanned = false,
                    reason = vm.IsLive
                        ? "The connected controller is not an OBD2 vehicle, so it has no DTCs to read."
                        : LiveTools.NotLiveRefusal,
                };
            }

            FaultScan? scan;

            try
            {
                scan = vm.ScanFaults();
            }
            catch (Exception e) when (e is EcuProtocolException or TimeoutException)
            {
                return new { scanned = false, reason = $"The scan failed: {e.Message}" };
            }

            if (scan is null) return new { scanned = false, reason = LiveTools.NotLiveRefusal };

            return new
            {
                scanned = true,
                protocol = scan.Protocol,
                warningLightOn = scan.MilOn,
                clean = scan.Clean,
                reportedCount = scan.ReportedCount,

                // Said out loud rather than left to be worked out. A car that
                // reports two faults and lists none has not been fully read, and
                // "no faults found" there would be a lie by omission.
                countDisagrees = scan.CountDisagrees,
                trouble = scan.Trouble.Length == 0 ? null : scan.Trouble,
                stored = Describe(scan.Stored),
                pending = Describe(scan.Pending),
                permanent = Describe(scan.Permanent),
            };
        });

    private static object[] Describe(IReadOnlyList<Dtc> codes) =>
        [.. codes.Select(d => new { code = d.Code, description = d.Description })];

    [McpServerTool]
    [Description(
        "Erases the vehicle's stored and pending fault codes (OBD2 mode 04), and with them the "
        + "freeze frame and readiness monitors. Shows a confirmation dialog in the window and "
        + "waits: nothing is erased unless a person clicks OK there. Permanent codes cannot be "
        + "erased this way. Many vehicles refuse with the engine running — key on, engine off is "
        + "the reliable state. Call scan_faults first, so the dialog can say how many will go.")]
    public static Task<object> ClearFaults(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.IsObd2Live)
            {
                return new
                {
                    cleared = false,
                    reason = vm.IsLive
                        ? "The connected controller is not an OBD2 vehicle, so it has no DTCs to clear."
                        : LiveTools.NotLiveRefusal,
                };
            }

            FaultClear? result;

            try
            {
                result = vm.ClearFaults();
            }
            catch (Exception e) when (e is EcuProtocolException or TimeoutException)
            {
                return new { cleared = false, reason = $"The erase failed: {e.Message}" };
            }

            // Null with a vehicle connected is the dialog answered No.
            if (result is null)
                return new { cleared = false, reason = "Not confirmed in the window, so nothing was erased." };

            return new
            {
                cleared = result.Erased,
                message = result.Message,
                remaining = Describe(result.Remaining),
            };
        });
}
