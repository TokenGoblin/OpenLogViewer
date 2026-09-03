using System.ComponentModel;
using ModelContextProtocol.Server;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// The five things here that reach a running engine, and one tool that says what
/// is standing in their way.
///
/// <para>
/// <b>None of these is a second code path.</b> Each calls the identical
/// view-model method the button calls, which asks
/// <see cref="IWriteConfirmation"/> immediately before the first byte goes out.
/// So a write triggered over MCP genuinely waits for a person to answer a dialog
/// in the running application before the tool call returns. That is correct
/// behaviour and not a problem to be worked around: the moment somebody writes a
/// second path "because the dialog is awkward over MCP", the gate is gone.
/// </para>
///
/// <para>
/// <b>What is never exposed.</b> The burn dialogs ask for the engine to be
/// stopped, because the ECU pauses while it writes flash. Nothing in software can
/// see whether an engine is running, and neither can an agent — so there is no
/// tool that acknowledges it, permanently and by design. The consequence is that
/// an MCP-triggered burn always needs a person to have answered that question.
/// Applying a saved tune to a controller is not here either; see
/// <see cref="TuneFileTools.PlanRestore"/>.
/// </para>
/// </summary>
[McpServerToolType]
public static class EcuWriteTools
{
    [McpServerTool]
    [Description(
        "Sends the changed cells of the open table to the controller. Takes effect immediately on "
        + "a running engine and is forgotten at the next power cycle unless burn_table_to_ecu "
        + "follows. A person must answer a confirmation dialog in the application before this "
        + "returns; if nobody is at the window, it waits. Call get_write_readiness first to see "
        + "whether it can succeed at all.")]
    public static Task<object> WriteTableToEcu(MainViewModel vm, IUiDispatcher dispatcher) =>
        Run(vm, dispatcher, vm.WriteTableToEcu);

    [McpServerTool]
    [Description(
        "Commits the page holding the open table to the controller's flash. Permanent — a power "
        + "cycle will not undo it. A person must answer a confirmation dialog, which also asks "
        + "for the engine to be stopped; there is no tool that can answer that on their behalf.")]
    public static Task<object> BurnTableToEcu(MainViewModel vm, IUiDispatcher dispatcher) =>
        Run(vm, dispatcher, vm.BurnTableToEcu);

    [McpServerTool]
    [Description(
        "Sends the changed settings to the controller. Takes effect immediately and is forgotten "
        + "at the next power cycle unless burn_settings_to_ecu follows. Confirmed by a person.")]
    public static Task<object> WriteSettingsToEcu(MainViewModel vm, IUiDispatcher dispatcher) =>
        Run(vm, dispatcher, vm.WriteSettingsToEcu);

    [McpServerTool]
    [Description(
        "Commits the pages that were written to flash — only those, because flash wears and a "
        + "burn stops the controller answering while it happens. Permanent, and confirmed by a "
        + "person with the engine stopped.")]
    public static Task<object> BurnSettingsToEcu(MainViewModel vm, IUiDispatcher dispatcher) =>
        Run(vm, dispatcher, vm.BurnSettingsToEcu);

    [McpServerTool]
    [Description(
        "Sends the moved points of the open curve to the controller. Both rows of a curve go "
        + "together or neither does. Nothing is burned, so a power cycle undoes it. Confirmed by "
        + "a person.")]
    public static Task<object> WriteCurveToEcu(MainViewModel vm, IUiDispatcher dispatcher) =>
        Run(vm, dispatcher, vm.WriteCurveToEcu);

    [McpServerTool]
    [Description(
        "What is standing between the current state and a write, split by who can act on it. "
        + "`needsAcknowledgement` is work an agent may still be able to finish itself — connect, "
        + "open a table, change something. `remainingForOperator` is what only a person at the "
        + "window can settle. `handoff` says in one line whose move it is. Touches no hardware.")]
    public static Task<object> GetWriteReadiness(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            var agent = new List<object>();

            void Agent(string what, string remedy) => agent.Add(new { what, remedy });

            if (!vm.IsLive)
                Agent("No controller is connected.", "Call connect_serial with a port name.");

            if (!vm.HasEcuTune)
                Agent("No tune has been read.", "Connect to a controller, which reads its tune.");

            if (vm.TuneIsPlaceholder)
            {
                Agent(
                    "The tune is a placeholder built from a definition — every value reads as zero.",
                    "Connect to a controller and read its own tune.");
            }

            if (vm.TuneIsFromFile)
            {
                Agent(
                    "The tune was opened from a file rather than read off the controller.",
                    "Connect to a controller; use plan_restore to see what the file would change.");
            }

            if (vm.TableEdit is null)
                Agent("No table is open.", "Call open_tune_table.");
            else if (!vm.HasTableChanges)
                Agent("Nothing has been changed in the open table.", "Call select_cells then edit_table.");

            if (vm.SettingsChangedCount == 0 && vm.OpenDialog is not null)
                Agent("No setting has been changed.", "Call set_setting.");

            // Deliberately short, and deliberately not "every warning is the
            // operator's problem" — most of the list above names something an
            // agent can go and resolve, and calling those human-only tells it to
            // give up on work it could still do.
            var operatorOnly = new List<object>
            {
                new
                {
                    what = "A confirmation dialog in the running application.",
                    why = "Every write and burn asks before the first byte goes out. No tool "
                          + "answers it; that is the gate.",
                },
                new
                {
                    what = "For a burn: that the engine is stopped.",
                    why = "The controller pauses while it writes flash. Nothing in software can "
                          + "see whether an engine is running, so this is never exposed as a tool.",
                },
            };

            bool agentCanProceed = agent.Count == 0;

            return new
            {
                canWriteTable = vm.CanWriteTable,
                canWriteSettings = vm.CanWriteSettings,
                canBurn = vm.CanBurn,
                canBurnSettings = vm.CanBurnSettings,
                canWriteCurve = vm.CanWriteCurve,
                pending = new
                {
                    tableCells = vm.TableEdit?.ChangedCount ?? 0,
                    settings = vm.SettingsChangedCount,
                    settingsBytes = vm.SettingsBytesToWrite,
                    settingsPagesToWrite = vm.SettingsPagesToWrite,
                    settingsPagesWaitingToBurn = vm.SettingsPagesWritten,
                },
                needsAcknowledgement = agent,
                remainingForOperator = operatorOnly,
                handoff = agentCanProceed
                    ? "Everything an agent can settle is settled. The next move is a person's: "
                      + "call a write tool and answer the dialog it raises in the application."
                    : $"{agent.Count} thing{(agent.Count == 1 ? "" : "s")} an agent can still "
                      + "resolve — see needsAcknowledgement — before a person is needed.",
            };
        });

    /// <summary>
    /// Runs one write on the UI thread and returns what it said.
    ///
    /// <para>
    /// The call is marshalled and then simply awaited: the confirmation blocks
    /// the dispatcher until a person answers, which is precisely the intended
    /// behaviour and is why these tools can take arbitrarily long.
    /// </para>
    /// </summary>
    private static Task<object> Run(MainViewModel vm, IUiDispatcher dispatcher, Func<WriteResult> write) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            WriteResult result = write();

            // Taken from the view model, never inferred from its prose. Guessing
            // by looking for a prefix reported "No table is open." as a
            // successful write — a flag the tool descriptions tell an agent to
            // branch on, saying that bytes reached a running engine when nothing
            // had left the machine.
            bool declined = result.Message is "Nothing was sent." or "Nothing was burned.";

            return new
            {
                sent = result.Reached,
                declined,
                message = result.Message,
                pending = new
                {
                    tableCells = vm.TableEdit?.ChangedCount ?? 0,
                    settings = vm.SettingsChangedCount,
                    settingsPagesWaitingToBurn = vm.SettingsPagesWritten,
                },
            };
        });
}
