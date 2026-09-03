using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// Reading and editing the tune held in memory.
///
/// <para>
/// Nothing here reaches a controller. Every edit moves the same
/// <see cref="TuneEdit"/> the keyboard moves and shows up on the Calibration tab;
/// only the tools in <see cref="EcuWriteTools"/> send anything, and those are
/// confirmed by a person.
/// </para>
/// </summary>
[McpServerToolType]
public static class TuneTools
{
    internal const string NoTuneRefusal =
        "No tune is loaded. Connect to a controller with connect_serial, or open one with "
        + "open_saved_tune.";

    internal const string NoTableRefusal =
        "No table is open. Call open_tune_table first.";

    [McpServerTool]
    [Description(
        "What tune is loaded and where it came from — read off a controller, opened from a file, "
        + "or a placeholder built from a definition with all-zero values. The last two cannot be "
        + "written back, and the reply says so.")]
    public static Task<object> GetTuneSummary(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() => new
        {
            loaded = vm.HasEcuTune,
            summary = vm.EcuTuneSummary,

            // The controller's own account of itself: port, firmware, build and
            // the definition matched to it. Deliberately not TuneSource, which
            // names the .msq opened to bin a log onto its table axes and reads
            // "none" on a perfectly good live tune.
            controller = vm.IsLive ? vm.LiveDetail : null,
            logTuneForAxes = new { source = vm.TuneSource, detail = vm.TuneDetail },
            fromFile = vm.TuneIsFromFile,
            placeholder = vm.TuneIsPlaceholder,
            warning = vm.HasTuneWarning ? vm.TuneWarning : null,
            connected = vm.IsLive,
            tables = vm.EcuTables.Count,
            settingsPages = vm.SettingsMenu.Count(e => e is { IsHeading: false, IsTable: false }),
            canWriteTable = vm.CanWriteTable,
            canBurn = vm.CanBurn,
        });

    [McpServerTool]
    [Description("Every table the loaded definition declares, with its size, units and axes.")]
    public static Task<object> ListTuneTables(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.HasEcuTune && vm.EcuTables.Count == 0)
                return new { listed = false, reason = NoTuneRefusal };

            return new
            {
                listed = true,
                tables = vm.EcuTables.Select(t => new
                {
                    name = t.Name,
                    units = t.Units,
                    columns = t.X.Breakpoints.Length,
                    rows = t.Y.Breakpoints.Length,
                    xAxis = new { name = t.X.Constant, units = t.X.Units },
                    yAxis = new { name = t.Y.Constant, units = t.Y.Units },
                }).ToArray(),
            };
        });

    [McpServerTool]
    [Description(
        "Opens a table by name and returns every cell in engineering units. Also opens it on the "
        + "application's own Calibration tab, so nothing an agent inspects happens invisibly.")]
    public static Task<object> OpenTuneTable(
        [Description("Table name, as list_tune_tables reports it.")] string name,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.EcuTables.FirstOrDefault(t => t.Name == name) is not { } table)
            {
                return vm.EcuTables.Count == 0
                    ? new { opened = false, reason = NoTuneRefusal }
                    : (object)new
                    {
                        opened = false,
                        reason = $"No table called '{name}'. Call list_tune_tables.",
                    };
            }

            // Shown as well as returned. One line, and it is what makes an armed
            // server watchable rather than spooky.
            vm.Mode = WorkspaceMode.Calibration;
            vm.SelectedEcuTable = table;

            return vm.TableEdit is null
                ? new { opened = false, reason = $"'{name}' could not be opened for editing." }
                : Describe(vm);
        });

    [McpServerTool]
    [Description("The open table's cells, axes and pending changes, without reopening it.")]
    public static Task<object> GetTableCells(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
            vm.TableEdit is null ? new { opened = false, reason = NoTableRefusal } : Describe(vm));

    [McpServerTool]
    [Description(
        "Selects the cells that edit_table acts on, by column and row index. One cell when only "
        + "the first pair is given. Indices are zero-based and clamped to the table.")]
    public static Task<object> SelectCells(
        [Description("First column index.")] int fromColumn,
        [Description("First row index.")] int fromRow,
        [Description("Last column index. Defaults to fromColumn.")] int toColumn = -1,
        [Description("Last row index. Defaults to fromRow.")] int toRow = -1,
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.TableEdit is not { } edit) return new { selected = false, reason = NoTableRefusal };

            var wanted = new TuneSelection(
                fromColumn, fromRow,
                toColumn < 0 ? fromColumn : toColumn,
                toRow < 0 ? fromRow : toRow);

            TuneSelection clamped = wanted.ClampedTo(edit.Columns, edit.Rows);
            vm.SelectedCells = clamped;

            return new
            {
                selected = true,
                left = clamped.Left,
                top = clamped.Top,
                right = clamped.Right,
                bottom = clamped.Bottom,
                cells = clamped.Count,
                clamped = !clamped.Equals(wanted),
                summary = vm.TableEditSummary,
            };
        });

    [McpServerTool]
    [Description(
        "Applies an edit to the selected cells of the open table. Operations: set, add, scale "
        + "(per cent), interpolate, revert. This is the same operation the keyboard and the "
        + "toolbar buttons raise, so it shows on the Calibration tab. Nothing is sent to a "
        + "controller — write_table_to_ecu does that, and asks a person first. "
        + "CHECK THE CLAMPED COUNT in the reply: a clamped cell is a value you did not get, and "
        + "on an ignition table it can be a value moving the opposite way to the one intended.")]
    public static Task<object> EditTable(
        [Description("set, add, scale, interpolate or revert.")] string operation,
        [Description("The value, delta, or per cent. Ignored by interpolate and revert.")]
        double amount = 0,
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.TableEdit is not { } edit) return new { edited = false, reason = NoTableRefusal };

            TuneSelection area = vm.SelectedCells.ClampedTo(edit.Columns, edit.Rows);

            // Read before and after rather than trusting the request: what a cell
            // became is the only honest answer once a firmware's declared range
            // has had its say.
            double[,] before = edit.Values;

            // Matched on the word, never on the resulting Kind. TuneEditKind.Add
            // is the enum's zero value, so "is this default?" is true for a
            // perfectly good nudge — which silently refused the commonest table
            // edit there is while every other operation worked.
            TuneTableEdit? change = operation.ToLowerInvariant() switch
            {
                "set" => TuneTableEdit.Set(amount),
                "add" => TuneTableEdit.Add(amount),
                "scale" => TuneTableEdit.Scale(amount),
                "interpolate" => TuneTableEdit.Interpolate(),
                "revert" => TuneTableEdit.RevertSelection(),
                _ => null,
            };

            if (change is not { } wanted)
            {
                return new
                {
                    edited = false,
                    reason = $"'{operation}' is not an operation. "
                             + "Use set, add, scale, interpolate or revert.",
                };
            }

            vm.EditTable(wanted);

            double[,] after = edit.Values;
            int moved = 0, clamped = 0;

            for (int c = area.Left; c <= area.Right; c++)
            {
                for (int r = area.Top; r <= area.Bottom; r++)
                {
                    if (Math.Abs(after[c, r] - before[c, r]) > 1e-9) moved++;

                    double asked = operation.ToLowerInvariant() switch
                    {
                        "set" => amount,
                        "add" => before[c, r] + amount,
                        "scale" => before[c, r] * (1 + amount / 100),
                        _ => after[c, r],
                    };

                    if (Math.Abs(after[c, r] - asked) > 1e-6) clamped++;
                }
            }

            return new
            {
                edited = true,
                operation,
                cells = area.Count,
                moved,
                clamped,
                clampedNote = clamped > 0
                    ? $"{clamped} cell{(clamped == 1 ? "" : "s")} hit the firmware's declared "
                      + "range and did not take the value asked for."
                    : null,
                changedInTable = edit.ChangedCount,
                summary = vm.TableEditSummary,
            };
        });

    [McpServerTool]
    [Description("Puts every changed cell of the open table back to what the controller holds.")]
    public static Task<object> RevertTable(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.TableEdit is null) return new { reverted = false, reason = NoTableRefusal };

            vm.RevertTable();

            return new { reverted = true, summary = vm.TableEditSummary };
        });

    [McpServerTool]
    [Description(
        "The settings pages the loaded definition declares — the dialogs the Calibration tab "
        + "offers.")]
    public static Task<object> ListSettingsPages(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!vm.HasSettingsPages) return new { listed = false, reason = NoTuneRefusal };

            return new
            {
                listed = true,
                pages = vm.SettingsMenu
                    .Where(e => e is { IsHeading: false, IsTable: false })
                    .Select(e => new { name = e.Title })
                    .ToArray(),
            };
        });

    [McpServerTool]
    [Description(
        "Opens a settings page and returns its fields with their current values, units and "
        + "whether each has been changed but not sent.")]
    public static Task<object> OpenSettingsPage(
        [Description("Page name, as list_settings_pages reports it.")] string name,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            SettingsMenuEntry? entry = vm.SettingsMenu.FirstOrDefault(
                e => e is { IsHeading: false, IsTable: false }
                     && string.Equals(e.Title, name, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return vm.HasSettingsPages
                    ? new { opened = false, reason = $"No settings page called '{name}'. Call list_settings_pages." }
                    : (object)new { opened = false, reason = NoTuneRefusal };
            }

            vm.Mode = WorkspaceMode.Calibration;
            vm.OpenMenuEntry = entry;

            return vm.OpenDialog is not { } dialog
                ? new { opened = false, reason = $"'{name}' has no fields this can show." }
                : (object)new
                {
                    opened = true,
                    page = entry.Title,
                    fields = dialog.Rows.Select(r => new
                    {
                        label = r.Label,
                        value = r.Value,
                        original = r.Original,
                        units = r.Units,
                        editable = r.IsEditable,
                        changed = r.IsChanged,
                        options = r.Options.Count == 0 ? null : r.Options.ToArray(),
                    }).ToArray(),
                };
        });

    [McpServerTool]
    [Description(
        "Sets one field on the open settings page. In memory only — write_settings_to_ecu sends "
        + "it, and asks a person first.")]
    public static Task<object> SetSetting(
        [Description("Field label, as open_settings_page reports it.")] string label,
        [Description("The new value, as it would be typed into the box.")] string value,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.OpenDialog is not { } dialog)
                return new { set = false, reason = "No settings page is open. Call open_settings_page first." };

            SettingRow? row = dialog.Rows.FirstOrDefault(
                r => string.Equals(r.Label, label, StringComparison.OrdinalIgnoreCase));

            if (row is null)
                return new { set = false, reason = $"No field called '{label}' on this page." };

            if (!row.IsEditable)
                return new { set = false, reason = $"'{row.Label}' is a reading the firmware will not let you change." };

            row.Value = value;

            return new
            {
                set = true,
                label = row.Label,
                // Read back rather than echoed: the row may have clamped or
                // reformatted what it was given.
                value = row.Value,
                changed = row.IsChanged,
                pendingSettings = vm.SettingsChangedCount,
                bytesToWrite = vm.SettingsBytesToWrite,
            };
        });

    [McpServerTool]
    [Description("Puts every changed setting back to what the controller holds.")]
    public static Task<object> RevertSettings(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            vm.RevertSettings();

            return new { reverted = true, pendingSettings = vm.SettingsChangedCount };
        });

    /// <summary>The open table, its axes and what is pending on it.</summary>
    private static object Describe(MainViewModel vm)
    {
        TuneEdit edit = vm.TableEdit!;
        var rows = new List<object>(edit.Rows);

        for (int r = 0; r < edit.Rows; r++)
        {
            var values = new List<double>(edit.Columns);
            var changed = new List<int>();

            for (int c = 0; c < edit.Columns; c++)
            {
                values.Add(Math.Round(edit[c, r], 6));
                if (edit.IsChanged(c, r)) changed.Add(c);
            }

            rows.Add(new
            {
                row = r,
                y = Math.Round(edit.Table.Y.Breakpoints[r], 6),
                values,
                changedColumns = changed.Count == 0 ? null : changed.ToArray(),
            });
        }

        return new
        {
            opened = true,
            name = edit.Name,
            units = edit.Units,
            columns = edit.Columns,
            rows = edit.Rows,
            digits = edit.Digits,
            xAxis = new
            {
                name = edit.Table.X.Constant,
                units = edit.Table.X.Units,
                breakpoints = edit.Table.X.Breakpoints.Select(b => Math.Round(b, 6)).ToArray(),
            },
            yAxis = new
            {
                name = edit.Table.Y.Constant,
                units = edit.Table.Y.Units,
                breakpoints = edit.Table.Y.Breakpoints.Select(b => Math.Round(b, 6)).ToArray(),
            },
            changedCells = edit.ChangedCount,
            summary = vm.TableEditSummary,
            cells = rows,
        };
    }
}
