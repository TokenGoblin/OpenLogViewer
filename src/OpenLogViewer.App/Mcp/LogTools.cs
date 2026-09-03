using System.ComponentModel;
using System.IO;
using ModelContextProtocol.Server;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Mcp;

/// <summary>Opening a log and reading what is in it.</summary>
[McpServerToolType]
public static class LogTools
{
    /// <summary>
    /// Shared so the wording and the check behind it cannot drift apart, and so
    /// the refusal names the tool that clears it.
    /// </summary>
    internal const string NoLogRefusal = "No log is open. Call open_log first.";

    /// <summary>
    /// The most samples any one call will return.
    ///
    /// <para>
    /// A datalog runs to hundreds of thousands of rows across a hundred channels;
    /// returning one whole is neither useful to read nor safe to serialise.
    /// </para>
    /// </summary>
    private const int MaxSamples = 4096;

    [McpServerTool]
    [Description(
        "Opens a datalog. Handles MegaSquirt and TunerStudio logs (.msl, .csv), MLG binary logs, "
        + "MaxxECU logs, and delimited text. Replaces whatever was open.")]
    public static Task<object> OpenLog(
        [Description("Full path to the log file.")] string path,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (!File.Exists(path)) return new { opened = false, reason = $"There is no file at {path}." };

            try
            {
                vm.Load(path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
            {
                return new { opened = false, reason = $"That could not be read as a log: {e.Message}" };
            }

            if (vm.Document is not { } document)
                return new { opened = false, reason = vm.Status };

            return new
            {
                opened = true,
                path = document.FilePath,
                format = document.FormatName,
                samples = document.SampleCount,
                seconds = Math.Round(document.Duration, 3),
                channels = document.Channels.Count,
                hasEmbeddedTune = document.EmbeddedTune is not null,
            };
        });

    [McpServerTool]
    [Description(
        "What the open log is: format, sample count, duration, rate, when it was recorded, and "
        + "whether it carries an embedded tune.")]
    public static Task<object> GetLogSummary(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is not { } document) return new { loaded = false, reason = NoLogRefusal };

            return new
            {
                loaded = true,
                path = document.FilePath,
                format = document.FormatName,
                samples = document.SampleCount,
                seconds = Math.Round(document.Duration, 3),
                medianInterval = Math.Round(document.MedianSampleInterval, 5),
                recordedAt = document.RecordedAt?.ToString("O"),
                signature = document.Signature,
                capture = document.CaptureInfo,
                channels = document.Channels.Count,
                markers = document.Markers.Count,
                hasEmbeddedTune = document.EmbeddedTune is not null,
                unreadableTune = document.UnreadableTune,
            };
        });

    [McpServerTool]
    [Description(
        "Every channel in the open log, with its units, category, whether it is plotted, whether "
        + "it never moves, and its range over the whole log.")]
    public static Task<object> ListChannels(MainViewModel vm, IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { listed = false, reason = NoLogRefusal };

            return new
            {
                listed = true,
                channels = vm.Channels.Select(c => new
                {
                    name = c.Name,
                    units = c.Units,
                    category = c.CategoryName,
                    plotted = c.IsVisible,
                    calculated = c.IsCalculated,
                    flat = c.IsFlat,
                    min = Round(c.Channel.Min),
                    max = Round(c.Channel.Max),
                }).ToArray(),
            };
        });

    [McpServerTool]
    [Description(
        "Minimum, maximum, mean and sample count for one channel. Over the current selection when "
        + "there is one, otherwise over the whole log — the reply says which.")]
    public static Task<object> GetChannelStatistics(
        [Description("Channel name, as list_channels reports it.")] string channel,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is not { } document) return new { read = false, reason = NoLogRefusal };

            if (document.FindChannel(channel) is not { } found)
                return new { read = false, reason = $"No channel called '{channel}'. Call list_channels." };

            (int first, int last) = vm.Selection ?? (0, document.SampleCount - 1);
            ChannelStatistics stats = ChannelStatistics.Over(found, first, last);

            return new
            {
                read = true,
                channel = found.Name,
                units = found.Units,
                over = vm.Selection is null ? "the whole log" : "the current selection",
                firstSample = first,
                lastSample = last,
                min = Round(stats.Min),
                max = Round(stats.Max),
                mean = Round(stats.Mean),
                count = stats.Count,
            };
        });

    [McpServerTool]
    [Description(
        "Raw samples from one or more channels. Time is always included. Capped at 4096 samples "
        + "per call; use `stride` to cover a longer span at lower resolution.")]
    public static Task<object> ReadSamples(
        [Description("Channel names. Empty means every plotted channel.")] string[] channels,
        [Description("First sample index. 0 is the start of the log.")] int first = 0,
        [Description("How many samples. Capped at 4096.")] int count = 256,
        [Description("Take every Nth sample. 1 is every one.")] int stride = 1,
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is not { } document) return new { read = false, reason = NoLogRefusal };

            int step = Math.Max(1, stride);

            // Bounds checked without adding: first + count wraps for a first near
            // int.MaxValue and would silently pass.
            if (first < 0 || first >= document.SampleCount)
                return new { read = false, reason = $"first must be between 0 and {document.SampleCount - 1}." };

            if (count <= 0) return new { read = false, reason = "count must be at least 1." };

            int available = (document.SampleCount - first + step - 1) / step;
            int taking = Math.Min(Math.Min(count, MaxSamples), available);

            LogChannel[] wanted = channels.Length == 0
                ? [.. vm.Channels.Where(c => c.IsVisible).Select(c => c.Channel)]
                : [.. channels.Select(document.FindChannel).OfType<LogChannel>()];

            if (channels.Length > 0 && wanted.Length != channels.Length)
            {
                string[] missing = [.. channels.Where(n => document.FindChannel(n) is null)];

                return new
                {
                    read = false,
                    reason = $"No channel called {string.Join(", ", missing.Select(m => $"'{m}'"))}. "
                             + "Call list_channels.",
                };
            }

            if (wanted.Length == 0)
                return new { read = false, reason = "No channels named and none are plotted." };

            var rows = new List<object>(taking);

            for (int i = 0; i < taking; i++)
            {
                int index = first + i * step;
                var row = new Dictionary<string, object?>
                {
                    ["sample"] = index,
                    ["time"] = Round(document.Time.At(index)),
                };

                foreach (LogChannel each in wanted) row[each.Name] = Round(each.At(index));

                rows.Add(row);
            }

            return new
            {
                read = true,
                first,
                stride = step,
                returned = rows.Count,
                truncated = taking < Math.Min(count, available),
                channels = wanted.Select(c => c.Name).ToArray(),
                samples = rows,
            };
        });

    [McpServerTool]
    [Description(
        "Finds where in the log a condition holds — for example \"RPM > 4000\" or "
        + "\"CLT > 100 and RPM < 2000\". Opens the find bar in the window and frames the first hit.")]
    public static Task<object> FindInLog(
        [Description("The condition, in the same syntax the Find bar takes.")] string condition,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { found = false, reason = NoLogRefusal };

            vm.FindCondition = condition;
            vm.Finding = true;

            bool ran = vm.RunFind();

            if (!ran || vm.Found is not { } result)
                return new { found = false, reason = vm.FindSummary };

            return new
            {
                found = true,
                summary = vm.FindSummary,
                matches = result.Matches,
                unknown = result.Unknown,
                problem = result.HasProblem ? result.Problem : null,
                runs = result.Runs
                    .Take(64)
                    .Select(r => new { first = r.First, last = r.Last })
                    .ToArray(),
            };
        });

    [McpServerTool]
    [Description(
        "Sets the sample range the analyses run over — the same thing dragging across the plot "
        + "does. Pass no arguments to clear it and go back to the whole log.")]
    public static Task<object> SetSelection(
        [Description("First sample index, or -1 to clear the selection.")] int first = -1,
        [Description("Last sample index.")] int last = -1,
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is not { } document) return new { set = false, reason = NoLogRefusal };

            if (first < 0 || last < 0)
            {
                vm.UpdateSelection(null);

                return new { set = true, selection = (object?)null };
            }

            if (first > last) (first, last) = (last, first);

            int top = document.SampleCount - 1;
            int from = Math.Clamp(first, 0, top);
            int to = Math.Clamp(last, 0, top);

            vm.UpdateSelection((from, to));

            return new
            {
                set = true,
                selection = new
                {
                    first = from,
                    last = to,
                    fromSeconds = Round(document.Time.At(from)),
                    toSeconds = Round(document.Time.At(to)),
                    clamped = from != first || to != last,
                },
            };
        });

    [McpServerTool]
    [Description("Writes the open log to CSV, with no dialog.")]
    public static Task<object> ExportLogCsv(
        [Description("Where to write the file.")] string path,
        [Description("Only the channels currently plotted.")] bool plottedOnly = false,
        MainViewModel vm = null!,
        IUiDispatcher dispatcher = null!) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { exported = false, reason = NoLogRefusal };

            try
            {
                vm.ExportLogCsv(path, plottedOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new { exported = false, reason = $"Could not write {path}: {e.Message}" };
            }

            return new { exported = true, path, scope = vm.ExportScope };
        });

    [McpServerTool]
    [Description(
        "Loads a second log to compare against, which is what puts difference traces on the plot "
        + "and fills the Compare options.")]
    public static Task<object> LoadComparison(
        [Description("Full path to the log to compare with.")] string path,
        MainViewModel vm,
        IUiDispatcher dispatcher) =>
        dispatcher.InvokeAsync<object>(() =>
        {
            if (vm.Document is null) return new { loaded = false, reason = NoLogRefusal };

            string said = vm.LoadComparison(path);

            return vm.HasComparison
                ? new { loaded = true, name = vm.CompareName, summary = vm.CompareSummary }
                : (object)new { loaded = false, reason = said };
        });

    /// <summary>
    /// Six places is beyond any sensor here and short of float noise, which is
    /// what stops a reply carrying 0.30000000000000004.
    /// </summary>
    private static double? Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 6) : null;
}
