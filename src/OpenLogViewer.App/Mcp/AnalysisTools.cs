using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>
/// The analyses the Log workspace offers: the histogram, the scatter, the VE
/// comparison, the findings and the power estimate.
/// </summary>
[McpServerToolType]
public static class AnalysisTools
{
    [McpServerTool]
    [Description(
        "Builds the histogram over the open log and switches the window to it. Axes are channel "
        + "names; leave them empty to keep whatever the application chose. The reply carries the "
        + "cells, so get_histogram_table is only needed to re-read it later.")]
    public static Task<object> BuildHistogram(
        [Description("X-axis channel. Empty keeps the current one.")] string xAxis = "",
        [Description("Y-axis channel. Empty keeps the current one.")] string yAxis = "",
        [Description("Z-axis channel — the value in each cell. Empty keeps the current one.")]
        string zAxis = "",
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { built = false, reason = LogTools.NoLogRefusal };

            vm.Mode = WorkspaceMode.Log;
            vm.LogView = LogView.Histogram;

            int built = vm.AnalysisBuilds;

            // Setting an axis raises HistogramInvalidated, which the window
            // answers by rebuilding over the range it is actually showing — the
            // whole log, or the zoomed span when "only the zoomed time range" is
            // ticked. Letting it do that is what stops the grid on screen and the
            // cells returned here being different numbers under one heading.
            if (Assign(vm, xAxis, yAxis, zAxis) is { } problem) return problem;

            // Nothing answered, so there is no window listening: build it over
            // the whole log, which is the only range available without one.
            if (vm.AnalysisBuilds == built) vm.RebuildHistogram(0, vm.Document.SampleCount - 1);

            return vm.Table is null
                ? new { built = false, reason = "The histogram could not be built — check the axes." }
                : DescribeHistogram(vm);
        });

    [McpServerTool]
    [Description("The histogram as it stands: axes, breakpoints, per-cell values and visit counts.")]
    public static Task<object> GetHistogramTable(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
            vm.Table is null
                ? new { built = false, reason = "No histogram has been built. Call build_histogram." }
                : DescribeHistogram(vm));

    [McpServerTool]
    [Description(
        "One histogram cell in detail: its value, how many samples fell in it, and where its axes "
        + "put it. The sample count is the part that matters — a cell averaged from three samples "
        + "and one averaged from three hundred look identical in the grid.")]
    public static Task<object> GetHistogramCell(
        [Description("Column index of the cell.")] int column,
        [Description("Row index of the cell.")] int row,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Table is not { } table)
                return new { described = false, reason = "No histogram has been built. Call build_histogram." };

            if (column < 0 || column >= table.Columns || row < 0 || row >= table.Rows)
            {
                return new
                {
                    described = false,
                    reason = $"Cell out of range: this histogram is {table.Columns}×{table.Rows}.",
                };
            }

            return new
            {
                described = true,
                column,
                row,
                x = Math.Round(table.ColumnCenters[column], 6),
                y = Math.Round(table.RowCenters[row], 6),
                xAxis = table.X.Name,
                yAxis = table.Y.Name,
                zAxis = table.Z.Name,
                units = table.Z.Units,
                value = table.Values[column, row] is { } v ? Math.Round(v, 6) : (double?)null,
                count = table.Counts[column, row],
                visited = vm.VisitedCells?.Visited(column, row),
                formatted = table.Format(column, row),
            };
        });

    [McpServerTool]
    [Description(
        "Builds the scatter plot over the open log and switches the window to it.")]
    public static Task<object> BuildScatter(
        [Description("X-axis channel. Empty keeps the current one.")] string xAxis = "",
        [Description("Y-axis channel. Empty keeps the current one.")] string yAxis = "",
        [Description("Colour-by channel. Empty keeps the current one.")] string zAxis = "",
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { built = false, reason = LogTools.NoLogRefusal };

            vm.Mode = WorkspaceMode.Log;
            vm.LogView = LogView.Scatter;

            int built = vm.AnalysisBuilds;

            if (Assign(vm, xAxis, yAxis, zAxis) is { } problem) return problem;

            if (vm.AnalysisBuilds == built) vm.RebuildScatter(0, vm.Document.SampleCount - 1);

            return vm.Points is not { } points
                ? new { built = false, reason = "The scatter could not be built — check the axes." }
                : (object)new
                {
                    built = true,
                    xAxis = vm.XAxis?.Name,
                    yAxis = vm.YAxis?.Name,
                    zAxis = vm.ZAxis?.Name,
                    points = points.Count,
                    range = Range(vm),
                };
        });

    [McpServerTool]
    [Description(
        "Runs the VE analysis: what the fuel table would have to become for the log's measured "
        + "AFR to have hit its target. Needs a histogram whose axes match a fuel table.")]
    public static Task<object> RunVeAnalysis(
        [Description(
            "Ignore cells with fewer samples than this. 0 keeps whatever the last call set — "
            + "it does not mean 'no filter'. The reply says which value was used.")]
        int minimumSamples = 0,
        [Description("Cap how far any one cell may move, in per cent. 0 keeps the current cap.")]
        double maxChange = 0,
        [Description("Show the suggested values rather than the deltas.")] bool showSuggested = false,
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { ran = false, reason = LogTools.NoLogRefusal };

            if (!vm.VeAvailable)
            {
                return new
                {
                    ran = false,
                    reason = "The VE analysis needs a histogram with both axes set. "
                             + "Call build_histogram first.",
                };
            }

            if (minimumSamples > 0) vm.VeMinimumSamples = minimumSamples;
            if (maxChange > 0) vm.VeMaxChange = maxChange;

            vm.VeShowSuggested = showSuggested;
            vm.VeAnalyze = true;

            return vm.VeResult is not { } result
                ? new { ran = false, reason = vm.VeSummary }
                : (object)new
                {
                    ran = true,
                    summary = vm.VeSummary,
                    minimumSamples = vm.VeMinimumSamples,
                    maxChange = vm.VeMaxChange,
                    delaySeconds = vm.VeDelaySeconds,
                    delayNote = vm.VeDelayNote,
                    // Counted from the grid rather than reported by the result,
                    // which carries the arrays and no totals.
                    cells = result.Counts.Length,
                    withData = Count(result.Suggested),
                    changed = Count(result.ChangePercent),
                };
        });

    [McpServerTool]
    [Description(
        "Works out the lag between a change in fuelling and the wideband reading it produces, "
        + "which is what stops the VE analysis blaming the wrong cell.")]
    public static Task<object> FindVeDelay(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { ran = false, reason = LogTools.NoLogRefusal };

            vm.FindVeDelay();

            return new
            {
                ran = true,
                seconds = vm.VeDelaySeconds,
                samples = vm.VeDelaySamples,
                finding = vm.HasVeDelayFinding ? vm.VeDelayFinding : null,
                note = vm.VeDelayNote,
            };
        });

    [McpServerTool]
    [Description("The findings for the open log — what the application noticed without being asked.")]
    public static Task<object> GetInsights(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is not { } document) return new { read = false, reason = LogTools.NoLogRefusal };

            IReadOnlyList<LogInsight> findings = LogInsights.From(document);

            return new
            {
                read = true,
                count = findings.Count,
                findings = findings.Select(f => new
                {
                    level = f.Level.ToString(),
                    topic = f.Topic,
                    title = f.Title,
                    detail = f.Detail,

                    // Kept, because every finding here is arithmetic on the
                    // samples rather than a rule of thumb, and the evidence is
                    // what makes that checkable instead of trusted.
                    evidence = f.Evidence,
                    samples = f.Samples,
                }).ToArray(),
            };
        });

    /// <summary>
    /// Points an axis at a named channel, or explains why it could not.
    ///
    /// <para>
    /// Returns null when everything asked for was assigned, so the caller can
    /// carry on; an object is a refusal ready to return.
    /// </para>
    /// </summary>
    private static object? Assign(MainViewModel vm, string x, string y, string z)
    {
        foreach ((string name, Action<ChannelItem> set) in new (string, Action<ChannelItem>)[]
                 {
                     (x, c => vm.XAxis = c),
                     (y, c => vm.YAxis = c),
                     (z, c => vm.ZAxis = c),
                 })
        {
            if (name.Length == 0) continue;

            ChannelItem? item = vm.Channels.FirstOrDefault(
                c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            if (item is null)
                return new { built = false, reason = $"No channel called '{name}'. Call list_channels." };

            set(item);
        }

        return null;
    }

    /// <summary>
    /// Which samples the grid in hand covers.
    ///
    /// <para>
    /// Said out loud, because it is not always the whole log: with "only the
    /// zoomed time range" ticked the window builds over what the plot is showing,
    /// and a reader told only the cell values would have no way to know.
    /// </para>
    /// </summary>
    private static object Range(MainViewModel vm)
    {
        int samples = vm.Document?.SampleCount ?? 0;
        (int first, int last) = vm.AnalysisRange ?? (0, samples - 1);

        return new
        {
            firstSample = first,
            lastSample = last,
            wholeLog = first <= 0 && last >= samples - 1,
            zoomedOnly = vm.HistogramZoomOnly,
        };
    }

    /// <summary>How many cells of a sparse grid actually hold a value.</summary>
    private static int Count(double?[,] grid)
    {
        int found = 0;

        foreach (double? cell in grid)
            if (cell is not null) found++;

        return found;
    }

    private static object DescribeHistogram(MainViewModel vm)
    {
        HistogramTable table = vm.Table!;
        var rows = new List<object>(table.Rows);

        for (int r = 0; r < table.Rows; r++)
        {
            var values = new List<double?>(table.Columns);
            var counts = new List<int>(table.Columns);

            for (int c = 0; c < table.Columns; c++)
            {
                values.Add(table.Values[c, r] is { } v ? Math.Round(v, 6) : null);
                counts.Add(table.Counts[c, r]);
            }

            rows.Add(new { row = r, y = Math.Round(table.RowCenters[r], 6), values, counts });
        }

        return new
        {
            built = true,
            range = Range(vm),
            columns = table.Columns,
            rows = table.Rows,
            statistic = table.Statistic.ToString(),
            fromTune = table.FromTune,
            isDelta = table.IsDelta,
            xAxis = new { name = table.X.Name, units = table.X.Units, centers = table.ColumnCenters },
            yAxis = new { name = table.Y.Name, units = table.Y.Units, centers = table.RowCenters },
            zAxis = new { name = table.Z.Name, units = table.Z.Units },
            cells = rows,
        };
    }
}
