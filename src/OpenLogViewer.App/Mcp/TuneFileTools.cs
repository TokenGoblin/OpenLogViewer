using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// Saved tunes on disk: opening one, saving one, comparing, and planning a
/// restore.
///
/// <para>
/// <b>There is no apply_restore, and there will not be one.</b> The application's
/// own command line makes the same choice — <c>--plan-restore</c> exists and
/// applies nothing — and the reasoning recorded there is that this is the largest
/// change the application can make to an engine, so it is not something to fall
/// out of a script. An agent is further from the engine than a script is, not
/// closer. <see cref="PlanRestore"/> returns everything needed to decide;
/// carrying it out is a person's move, from the Tools menu.
/// </para>
/// </summary>
[McpServerToolType]
public static class TuneFileTools
{
    [McpServerTool]
    [Description(
        "Opens a saved tune (.msq) into the session. It can be read and compared but not written "
        + "back — a tune from a file belongs to whatever saved it, and the write tools refuse it.")]
    public static Task<object> OpenSavedTune(
        [Description("Full path to the .msq file.")] string path,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!File.Exists(path)) return new { opened = false, reason = $"There is no file at {path}." };

            bool ok;

            try
            {
                ok = vm.OpenSavedTune(path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                return new { opened = false, reason = $"That could not be read as a tune: {e.Message}" };
            }

            return ok
                ? new
                {
                    opened = true,
                    source = vm.TuneSource,
                    detail = vm.TuneDetail,
                    tables = vm.EcuTables.Count,
                    fromFile = vm.TuneIsFromFile,
                    warning = vm.HasTuneWarning ? vm.TuneWarning : null,
                }
                : (object)new { opened = false, reason = vm.Status };
        });

    [McpServerTool]
    [Description("Writes the tune in hand to a .msq file, with no dialog.")]
    public static Task<object> SaveTuneToFile(
        [Description("Where to write it.")] string path,
        [Description("An optional note stored in the file.")] string comment = "",
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.CanSaveTune)
            {
                return new
                {
                    saved = false,
                    reason = vm.HasEcuTune
                        ? "This tune cannot be saved — it is a placeholder built from a definition."
                        : TuneTools.NoTuneRefusal,
                };
            }

            string said = vm.SaveTuneToFile(path, comment);

            return File.Exists(path)
                ? new { saved = true, path, message = said }
                : (object)new { saved = false, reason = said };
        });

    [McpServerTool]
    [Description("Says which settings a saved tune and the tune in hand disagree about, and by how much.")]
    public static Task<object> CompareWithSavedTune(
        [Description("Full path to the .msq to compare against.")] string path,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.HasEcuTune) return new { compared = false, reason = TuneTools.NoTuneRefusal };
            if (!File.Exists(path)) return new { compared = false, reason = $"There is no file at {path}." };

            string said = vm.CompareWithSavedTune(path);

            return new
            {
                compared = true,
                message = said,
                // Mine is the FILE and Theirs is the tune in hand: both callers
                // pass TuneCompare.Compare(file, ecu). Named here rather than
                // passed through, because "mine" reads like the ECU's and is not.
                differences = vm.TuneDifferences.Select(d => new
                {
                    setting = d.Name,
                    cellsDiffering = d.Cells,
                    inFile = d.MineShown,
                    inHand = d.TheirsShown,
                    summary = d.Summary,
                }).ToArray(),
            };
        });

    [McpServerTool]
    [Description(
        "Says what restoring a saved tune to the connected controller WOULD change, and changes "
        + "nothing. Returns the writes, the byte and page counts, what the file asks for that this "
        + "firmware does not have, and whether the signatures agree. "
        + "There is deliberately no tool that carries a restore out: it is the largest change this "
        + "application can make to an engine, and it stays a person's move from the Tools menu.")]
    public static Task<object> PlanRestore(
        [Description("Full path to the .msq to plan from.")] string path,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!File.Exists(path)) return new { planned = false, reason = $"There is no file at {path}." };

            string said = vm.PlanRestore(path);

            if (vm.PendingRestore is not { } plan)
                return new { planned = false, reason = said };

            return new
            {
                planned = true,
                message = said,
                summary = plan.Summary,
                empty = plan.IsEmpty,
                writes = plan.Writes.Count,
                bytes = plan.Bytes,
                pages = plan.Pages.ToArray(),
                signaturesAgree = plan.SignaturesAgree,
                fileSignature = plan.FileSignature,
                ecuSignature = plan.EcuSignature,
                complete = plan.Complete,
                shortfall = plan.Complete ? null : plan.Shortfall,
                missing = plan.Missing.Select(m => m.ToString()).ToArray(),
                rejected = plan.Rejected.Select(m => m.ToString()).ToArray(),
                differences = plan.Differences.Take(200).Select(d => new
                {
                    setting = d.Name,
                    cellsDiffering = d.Cells,
                    inFile = d.MineShown,
                    inEcu = d.TheirsShown,
                    summary = d.Summary,
                }).ToArray(),

                // Said explicitly, so an agent does not go looking for the tool
                // that finishes this.
                handoff = "Nothing has been changed. Applying this is a person's move: "
                          + "Tools ▸ Restore a saved tune to the ECU, in the application.",
            };
        });

    [McpServerTool]
    [Description("Discards a restore plan without applying it.")]
    public static Task<object> CancelRestore(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            vm.CancelRestore();

            return new { cancelled = true };
        });
}
