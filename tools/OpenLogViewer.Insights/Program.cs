using OpenLogViewer.Core;

namespace OpenLogViewer.Insights;

/// <summary>
/// The insights, without the window.
///
/// <para>
/// Everything the Insights pane says about a log, from a command line: no
/// display, no ECU, nothing to click. That makes the analysis usable in the
/// places it is most wanted and least available — over a folder of logs at once,
/// from a script after a drive, and by an assistant that has a file and no way
/// to open a program.
/// </para>
/// <para>
/// It can also record what it found into the vehicle's project, which is what
/// turns a pile of one-off analyses into a record of whether anything is getting
/// better.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // The findings are full of degree signs, plus-or-minus and the lambda
        // character, and a Windows console defaults to a code page that has
        // none of them — so "2.0°" arrives as "2.0?" and "± 0.3" as "� 0.3".
        // The numbers survive; it is only ever the units that come out wrong,
        // which is the half somebody is least likely to notice is wrong.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch (IOException) { /* redirected somewhere that will not take it */ }

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }

        string? vehicle = Value(args, "--project");
        string? note = Value(args, "--note");
        bool asJson = args.Contains("--json");
        bool quiet = args.Contains("--quiet");

        string[] logs = [.. Expand(Loose(args))];

        if (logs.Length == 0)
        {
            Console.Error.WriteLine("No logs given. Pass files, or a folder to read every log in it.");
            return 2;
        }

        var store = new TuningProjectStore(
            Value(args, "--projects")
            ?? Path.Combine(new Workspace().Root, "Projects"));

        TuningProject? project = vehicle is null
            ? null
            : store.Read(vehicle) ?? new TuningProject { Vehicle = vehicle };

        int worst = 0;

        foreach (string path in logs)
        {
            try
            {
                LogDocument log = LogReaderFactory.Load(path);
                IReadOnlyList<LogInsight> found = LogInsights.From(log);

                if (!quiet)
                {
                    if (asJson) Json(path, log, found);
                    else Print(path, log, found, logs.Length > 1);
                }

                // The exit code is the worst thing found, so a script can act on
                // it without parsing anything: 0 nothing wrong, 1 something to
                // watch, 2 a warning.
                worst = Math.Max(worst, Severity(found));

                if (project is not null)
                {
                    project = TuningProjectRecorder.Record(
                        project, TuningProjectRecorder.Sitting(log, "", note ?? ""));
                }
            }
            catch (Exception e) when (e is IOException or LogFormatException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}");
                worst = Math.Max(worst, 2);
            }
        }

        if (project is not null)
        {
            store.Write(project);

            if (!quiet)
            {
                Console.WriteLine();
                Console.WriteLine($"Recorded into {store.PathFor(project.Vehicle)}");
                Console.WriteLine($"{project.Open.Count()} fix(es) open.");
            }
        }

        return worst;
    }

    /// <summary>Switches that swallow the token after them.</summary>
    private static readonly string[] TakesAValue = ["--project", "--projects", "--note"];

    /// <summary>
    /// The arguments that are logs: everything that is neither a switch nor the
    /// value of one.
    ///
    /// Taking every token without a leading dash is not enough, and it showed —
    /// <c>--note "baseline before any changes"</c> was read as a request to
    /// analyse a log of that name, which then reported "Log file not found" and
    /// set the exit code to 2 on a run that had otherwise gone perfectly.
    /// </summary>
    private static IEnumerable<string> Loose(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (TakesAValue.Contains(args[i], StringComparer.OrdinalIgnoreCase)) i++;
                continue;
            }

            yield return args[i];
        }
    }

    /// <summary>Files as given, and every log inside any folder given.</summary>
    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (string found in Directory
                             .EnumerateFiles(path)
                             .Where(f => LogReaderFactory.OpenFileFilter
                                 .Contains(Path.GetExtension(f), StringComparison.OrdinalIgnoreCase))
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    yield return found;
                }

                continue;
            }

            yield return path;
        }
    }

    /// <summary>The worst level found, as an exit code a script can branch on.</summary>
    private static int Severity(IReadOnlyList<LogInsight> found)
    {
        if (found.Any(f => f.Level == InsightLevel.Warning)) return 2;
        if (found.Any(f => f.Level == InsightLevel.Watch)) return 1;

        return 0;
    }

    private static void Print(
        string path, LogDocument log, IReadOnlyList<LogInsight> found, bool many)
    {
        if (many) Console.WriteLine();

        Console.WriteLine(Path.GetFileName(path));
        Console.WriteLine(new string('-', Path.GetFileName(path).Length));
        Console.WriteLine(
            $"{log.Time.Length:N0} samples over {Seconds(log):N0} s, {log.Channels.Count} channels");
        Console.WriteLine();

        // Worst first. Somebody reading a wall of output stops early, and what
        // they stop before should be the part that did not need them.
        foreach (LogInsight insight in found.OrderBy(f => Order(f.Level)))
        {
            Console.WriteLine($"[{insight.Level,-11}] {insight.Topic}");
            Console.WriteLine($"  {insight.Title}");

            if (insight.Detail.Length > 0) Console.WriteLine($"  {insight.Detail}");
            if (insight.Evidence.Length > 0) Console.WriteLine($"  {insight.Evidence}");

            Console.WriteLine();
        }
    }

    private static int Order(InsightLevel level) => level switch
    {
        InsightLevel.Warning => 0,
        InsightLevel.Watch => 1,
        InsightLevel.Note => 2,
        InsightLevel.Good => 3,
        _ => 4,
    };

    private static void Json(string path, LogDocument log, IReadOnlyList<LogInsight> found) =>
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new
            {
                log = Path.GetFileName(path),
                samples = log.Time.Length,
                seconds = Seconds(log),
                channels = log.Channels.Count,
                findings = found.Select(f => new
                {
                    level = f.Level.ToString(),
                    topic = f.Topic,
                    title = f.Title,
                    detail = f.Detail,
                    evidence = f.Evidence,
                }),
            },
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

    private static double Seconds(LogDocument log) =>
        log.Time.Length > 0 ? log.Time.At(log.Time.Length - 1) : 0;

    private static string? Value(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);

        return at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[at + 1]
            : null;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            olv-insights — what a datalog says about the engine that produced it.

              olv-insights <log|folder> [more…] [options]

            Reads MLG, MSL and CSV logs. Needs no ECU, no display and no network.

            Options
              --project <vehicle>   also record what was found into that vehicle's
                                    project: keeps the findings, raises a fix for
                                    anything newly warned about, and notes a repeat
                                    against the fix already tracking it
              --note <text>         what this sitting was, kept with it
              --projects <folder>   where projects live (default: the workspace)
              --json                machine-readable output
              --quiet               say nothing; use the exit code

            Exit code
              0  nothing wrong        1  something worth watching        2  a warning

            Examples
              olv-insights drive.mlg
              olv-insights ~/OpenLogViewer/Logs --project "The E28" --note "after VE +4%"
              olv-insights drive.mlg --json | jq '.findings[] | select(.level=="Warning")'
            """);
    }
}
