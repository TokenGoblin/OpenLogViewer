using OpenLogViewer.Core;

// Console companion to the viewer: decodes a log and prints a summary.
// Doubles as the regression check for the readers.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: olv-dump <logfile> [more logs...]  [--channels]");
    return 1;
}

bool listChannels = args.Contains("--channels");
bool listCategories = args.Contains("--categories");
bool listTune = args.Contains("--tune");
string[] paths = args.Where(a => !a.StartsWith("--")).ToArray();
int failures = 0;

foreach (string path in paths)
{
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogDocument log = LogReaderFactory.Load(path);
        sw.Stop();

        Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
        Console.WriteLine($"  format    : {log.FormatName}");
        Console.WriteLine($"  channels  : {log.Channels.Count}");
        Console.WriteLine($"  samples   : {log.SampleCount:N0}");
        Console.WriteLine($"  duration  : {log.Duration:F2} s  ({log.Time.Name} base)");
        Console.WriteLine($"  decoded in: {sw.Elapsed.TotalMilliseconds:F0} ms");
        if (log.Signature is { Length: > 0 }) Console.WriteLine($"  signature : {log.Signature}");
        if (log.CaptureInfo is { Length: > 0 }) Console.WriteLine($"  capture   : {log.CaptureInfo}");
        if (log.RecordedAt is { } at) Console.WriteLine($"  recorded  : {at:yyyy-MM-dd HH:mm:ss}");

        int flat = log.Channels.Count(c => c.IsFlat);
        Console.WriteLine($"  flat/unused channels: {flat}");
        if (log.Markers.Count > 0)
        {
            Console.WriteLine($"  markers   : {log.Markers.Count}");
            foreach (LogMarker m in log.Markers.Take(3))
                Console.WriteLine($"     @{m.Time,8:F2}s  {m.Text}");
        }

        if (listTune)
        {
            Console.WriteLine();
            if (log.EmbeddedTune is not { Length: > 0 } tune)
            {
                Console.WriteLine("  no tune embedded in this log");
            }
            else
            {
                Console.WriteLine($"  embedded tune: {tune.Length:N0} chars");
                var sets = MsqTune.ReadAxisSets(tune);
                if (sets.Count == 0) Console.WriteLine("  no usable table axes found");

                foreach (TuneAxisSet set in sets)
                {
                    Console.WriteLine($"  {set.Label}");
                    Console.WriteLine($"      {set.X.Constant} [{set.X.Units}]: {string.Join(" ", set.X.Breakpoints)}");
                    Console.WriteLine($"      {set.Y.Constant} [{set.Y.Units}]: {string.Join(" ", set.Y.Breakpoints)}");
                }
            }
        }

        if (listCategories)
        {
            Console.WriteLine();
            foreach (var group in log.Channels
                         .Select(c => (Channel: c, Category: ChannelClassifier.Classify(c.Name, c.Units)))
                         .GroupBy(x => x.Category)
                         .OrderBy(g => (int)g.Key))
            {
                Console.WriteLine($"  {ChannelClassifier.DisplayName(group.Key)} ({group.Count()})");
                foreach (var chunk in group.Select(x => x.Channel.Name).Order().Chunk(4))
                    Console.WriteLine("      " + string.Join(", ", chunk));
            }
        }

        if (listChannels)
        {
            Console.WriteLine();
            Console.WriteLine($"  {"channel",-24}{"units",-8}{"min",14}{"max",14}");
            foreach (LogChannel c in log.Channels)
                Console.WriteLine($"  {Trim(c.Name, 23),-24}{Trim(c.Units, 7),-8}{c.Format(c.Min),14}{c.Format(c.Max),14}");
        }
        else
        {
            foreach (string name in new[] { "RPM", "MAP", "TPS", "AFR", "CLT", "MAT", "Batt V" })
            {
                LogChannel? c = log.FindChannel(name);
                if (c is not null)
                    Console.WriteLine($"    {c.Name,-10}{c.Units,-6} {c.Format(c.Min),10} .. {c.Format(c.Max),-10}");
            }
        }
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"\n=== {Path.GetFileName(path)} ===\n  FAILED: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
