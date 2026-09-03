using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// Reading a vehicle's fault codes.
///
/// <para>
/// There is no tool that clears them, and there will not be one. Erasing DTCs
/// takes the freeze frame with it — the record of what the engine was doing when
/// the fault occurred, and the single most useful thing for working out an
/// intermittent — and resets the readiness monitors, which the car then has to
/// re-earn over a full drive cycle before it can pass an emissions test. That is
/// an irreversible change to a physical vehicle's compliance state, made on a
/// diagnosis an agent did not perform. It stays a button, behind the dialog in
/// <see cref="MainViewModel.ClearFaults"/>.
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
        + "There is deliberately no tool that clears codes; that stays a button in the window.")]
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
}
