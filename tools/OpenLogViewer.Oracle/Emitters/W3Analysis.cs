using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// The analysis package (histogram tables, scatter, VE analysis, wideband delay, power
/// estimate, CSV export, log comparison) run over a representative slice of the corpus logs
/// against the corpus tunes. Every result is flattened to a canonical key/value text: doubles
/// as IEEE bits, long values as an FNV-1a hash of the text. The Rust side builds the same
/// text from the same files and compares it key by key.
/// </summary>
public sealed class W3Analysis : IEmitter
{
    public string Package => "w3-analysis";

    public string Set => "analysis";

    public bool FromCorpus => true;

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();

        var logs = new List<string>();
        var all = context.CorpusFiles("logs").ToList();
        logs.AddRange(all.Where(p => p.EndsWith(".mlg", StringComparison.OrdinalIgnoreCase)));
        logs.AddRange(all.Where(p => p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).Where((_, i) => i % 9 == 0).Take(10));

        // Tunes that carry a VE table, in name order.
        var tunes = new List<(string File, TuneTable Table)>();
        foreach (string path in context.CorpusFiles("tunes").Where(p => p.EndsWith(".msq", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                string xml = TuningText.Read(path);
                TuneTable? ve = MsqTune.ReadTables(xml).FirstOrDefault(t => t.Name == "VE table 1");
                if (ve is not null && ve.Columns > 1 && ve.Rows > 1) tunes.Add((Path.GetFileName(path), ve));
            }
            catch
            {
            }
        }

        for (int k = 0; k < logs.Count; k++)
        {
            string file = Path.GetFileName(logs[k]);
            LogDocument log;
            try
            {
                log = LogReaderFactory.Load(logs[k]);
            }
            catch (Exception ex)
            {
                cases.Add(new { file, error = ex.GetType().Name });
                continue;
            }

            LogDocument? other = null;
            try
            {
                other = LogReaderFactory.Load(logs[(k + 1) % logs.Count]);
            }
            catch
            {
            }

            (string File, TuneTable Table)? tune = tunes.Count == 0 ? null : tunes[k % tunes.Count];
            List<(string, string)> items;
            try
            {
                items = W3Digest.Items(log, tune?.Table, other);
            }
            catch (Exception ex)
            {
                items = [("exception", ex.GetType().Name)];
            }

            cases.Add(new
            {
                file,
                tune = tune?.File,
                other = other is null ? null : Path.GetFileName(logs[(k + 1) % logs.Count]),
                items = items.Select(i => new { key = i.Item1, value = i.Item2 }).ToArray(),
            });
        }

        return cases;
    }
}

/// <summary>The same analysis over small logs built in code, so the edge cases run in CI.</summary>
public sealed class W3AnalysisSynthetic : IEmitter
{
    public string Package => "w3-analysis";

    public string Set => "synthetic";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        var cases = new List<object>();
        foreach (int seed in new[] { 1, 2, 3 })
        {
            LogDocument log = Build(seed, 3000);
            LogDocument other = Build(seed + 10, 2500);

            var table = new TuneTable(
                "VE table 1",
                new TuneAxis("rpmBins", "rpm", [1000, 2000, 3000, 4000, 5000, 6000]),
                new TuneAxis("mapBins", "kPa", [30, 50, 70, 90, 110]),
                Values(6, 5),
                "%");

            cases.Add(new
            {
                seed,
                items = W3Digest.Items(log, table, other).Select(i => new { key = i.Item1, value = i.Item2 }).ToArray(),
            });
        }

        return cases;
    }

    private static double[,] Values(int columns, int rows)
    {
        var v = new double[columns, rows];
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < rows; r++)
                v[c, r] = 40 + (c * 7) + (r * 3.5);
        return v;
    }

    /// <summary>Deterministic pseudo-random channels; integer arithmetic only, so Rust reproduces it.</summary>
    public static LogDocument Build(int seed, int n)
    {
        long state = seed * 7919L;
        double Next()
        {
            state = ((state * 1103515245L) + 12345L) & 0x7FFFFFFFL;
            return state / 2147483648.0;
        }

        var time = new double[n];
        var rpm = new double[n];
        var map = new double[n];
        var afr = new double[n];
        var target = new double[n];
        var iat = new double[n];
        var pulse = new double[n];
        var tps = new double[n];

        for (int i = 0; i < n; i++)
        {
            time[i] = i * 0.02;
            rpm[i] = 800 + ((i * 13) % 5400) + (Next() * 50);
            map[i] = 25 + (((i * 7) % 90)) + (Next() * 4);
            double t = 14.7 - ((i % 200) / 200.0 * 2);
            target[i] = t;
            // A delayed copy of the target, with noise, so the delay search has something to find.
            afr[i] = (i >= 5 ? target[i - 5] : t) * (1 + ((Next() - 0.5) * 0.1));
            iat[i] = 30 + (Next() * 10);
            pulse[i] = 2 + (map[i] / 30) + Next();
            tps[i] = (i * 3) % 100;
        }

        for (int i = 0; i < n; i += 97) map[i] = double.NaN;
        for (int i = 5; i < n; i += 211) afr[i] = double.NaN;

        LogChannel Ch(string name, string units, int digits, double[] v, bool precise = false) =>
            new(name, units, digits, v, precise);

        LogChannel timeChannel = Ch("Time", "s", 3, time, true);
        return new LogDocument
        {
            FilePath = "synthetic.csv",
            FormatName = "synthetic",
            Time = timeChannel,
            Channels =
            [
                timeChannel,
                Ch("RPM", "rpm", 0, rpm),
                Ch("MAP", "kPa", 1, map),
                Ch("AFR", "AFR", 2, afr),
                Ch("AFR Target", "AFR", 2, target),
                Ch("IAT", "°C", 0, iat),
                Ch("Pulse Width", "ms", 2, pulse),
                Ch("TPS", "%", 0, tps),
            ],
        };
    }
}

/// <summary>Everything is flattened to short text so Rust can compare it key by key.</summary>
public static class W3Digest
{
    public static string B(double v) => Exact.Bits(v);

    public static string Fnv(string s)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 0x100000001b3;
        }
        return "h:" + h.ToString("x16");
    }

    /// <summary>Short text as is, long text as its hash.</summary>
    public static string Fin(string s) => s.Length <= 300 ? s : Fnv(s);

    private static string Opt(double? v) => v is { } d ? B(d) : "n";

    public static string Dig(HistogramTable t)
    {
        var sb = new StringBuilder();
        sb.Append($"c{t.Columns} r{t.Rows} fromTune={t.FromTune} delta={t.IsDelta} dev={t.ShowsDeviation}");
        sb.Append(" cc=").Append(string.Join(",", t.ColumnCenters.Select(B)));
        sb.Append(" rc=").Append(string.Join(",", t.RowCenters.Select(B)));
        sb.Append(" v=");
        for (int c = 0; c < t.Columns; c++)
            for (int r = 0; r < t.Rows; r++)
                sb.Append(Opt(t.Values[c, r])).Append(',');
        sb.Append(" n=");
        for (int c = 0; c < t.Columns; c++)
            for (int r = 0; r < t.Rows; r++)
                sb.Append(t.Counts[c, r]).Append(',');
        sb.Append($" min={B(t.MinValue)} max={B(t.MaxValue)} maxdev={B(t.MaxDeviation)} maxc={t.MaxCount}");
        sb.Append($" samples={t.SampleCount} pop={t.PopulatedCells} first={t.FirstSample} last={t.LastSample} empty={t.IsEmpty}");
        sb.Append(" name=").Append(t.DisplayName ?? "null").Append(" digits=").Append(t.DisplayDigits?.ToString() ?? "null");
        sb.Append(" fmt=");
        for (int c = 0; c < t.Columns; c++)
            for (int r = 0; r < t.Rows; r++)
                sb.Append(t.Format(c, r)).Append('|');
        return Fin(sb.ToString());
    }

    private static string Ints(IEnumerable<int> v) => string.Join(",", v);

    private static string DigVe(VeAnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"cells={r.CellsSuggested} thin={r.CellsThin} used={r.SamplesUsed} largest={B(r.LargestChangePercent)} empty={r.IsEmpty} problem={r.Problem ?? "null"}");
        sb.Append(" s=");
        for (int c = 0; c < r.Table.Columns; c++)
            for (int rr = 0; rr < r.Table.Rows; rr++)
                sb.Append(Opt(r.Suggested[c, rr])).Append(',');
        sb.Append(" ch=");
        for (int c = 0; c < r.Table.Columns; c++)
            for (int rr = 0; rr < r.Table.Rows; rr++)
                sb.Append(Opt(r.ChangePercent[c, rr])).Append(',');
        sb.Append(" n=");
        for (int c = 0; c < r.Table.Columns; c++)
            for (int rr = 0; rr < r.Table.Rows; rr++)
                sb.Append(r.Counts[c, rr]).Append(',');
        sb.Append(" w=");
        for (int c = 0; c < r.Table.Columns; c++)
            for (int rr = 0; rr < r.Table.Rows; rr++)
                sb.Append(B(r.Weight[c, rr])).Append(',');
        return Fin(sb.ToString());
    }

    private static string DigDelay(DelaySearchResult d)
    {
        var sb = new StringBuilder();
        sb.Append($"best={d.BestSamples} bestS={B(d.BestSeconds)} imp={B(d.ImprovementPercent)} scored={d.SamplesScored} low={B(d.LowSeconds)} high={B(d.HighSeconds)} none={d.NoneIsPlausible} precise={d.IsPrecise} problem={d.Problem ?? "null"}");
        sb.Append(" curve=");
        foreach (DelayCandidate c in d.Curve) sb.Append($"{c.Samples}:{B(c.Seconds)}:{B(c.Disagreement)},");
        return Fin(sb.ToString());
    }

    private static string DigPower(PowerEstimateResult p)
    {
        var sb = new StringBuilder();
        foreach (PowerMethod m in p.Methods)
        {
            sb.Append($"M[{m.Name}|{m.Basis}]");
            foreach (MathChannel c in m.Channels) sb.Append($"C[{c.Name}|{c.Units}|{c.Digits}|{c.Expression}]");
        }
        foreach (UnavailableMethod u in p.Unavailable) sb.Append($"U[{u.Name}|{u.Needs}]");
        return Fin(sb.ToString());
    }

    private static string DigScatter(ScatterPlot p, ScatterBins b)
    {
        var sb = new StringBuilder();
        sb.Append($"count={p.Count} dropped={p.Dropped} filtered={p.Filtered} delta={p.IsDelta}");
        sb.Append($" x={B(p.XMin)},{B(p.XMax)} y={B(p.YMin)},{B(p.YMax)} z={B(p.ZMin)},{B(p.ZMax)} dev={B(p.MaxDeviation)}");
        sb.Append(" xs=").Append(string.Join(",", p.Xs.Take(p.Count).Select(B)));
        sb.Append(" ys=").Append(string.Join(",", p.Ys.Take(p.Count).Select(B)));
        sb.Append(" zs=").Append(string.Join(",", p.Zs.Take(p.Count).Select(B)));
        sb.Append(" ss=").Append(string.Join(",", p.Samples.Take(p.Count)));
        sb.Append($" bins={b.Columns}x{b.Rows} occ={b.Occupied} busy={b.Busiest} mean={B(b.MeanMin)},{B(b.MeanMax)} color={B(b.ColorLow)},{B(b.ColorHigh)} clips={b.ClipsLow},{b.ClipsHigh} extent={B(b.MeanExtent)}");
        sb.Append(" cnt=").Append(Ints(b.Counts));
        sb.Append(" means=").Append(string.Join(",", b.Means.Select(B)));
        sb.Append(" lo=").Append(string.Join(",", b.Lowest.Select(B)));
        sb.Append(" hi=").Append(string.Join(",", b.Highest.Select(B)));
        return Fnv(sb.ToString());
    }

    private static string Csv(Action<StringWriter> write)
    {
        using var w = new StringWriter();
        write(w);
        return Fnv(w.ToString());
    }

    public static List<(string, string)> Items(LogDocument log, TuneTable? tune, LogDocument? other)
    {
        var items = new List<(string, string)>();
        void Add(string key, Func<string> f)
        {
            try
            {
                items.Add((key, f()));
            }
            catch (Exception ex)
            {
                items.Add((key, "throws:" + ex.GetType().Name));
            }
        }

        LogChannel? rpm = ChannelRoles.Find(log, ChannelRole.EngineSpeed);
        LogChannel? map = ChannelRoles.Find(log, ChannelRole.ManifoldPressure);
        LogChannel? afr = ChannelRoles.Find(log, ChannelRole.Mixture);
        LogChannel? target = ChannelRoles.Find(log, ChannelRole.MixtureTarget);
        int n = log.SampleCount;

        items.Add(("roles", $"{rpm?.Name}|{map?.Name}|{afr?.Name}|{target?.Name}|n={n}"));

        Add("power.default", () => DigPower(PowerEstimate.For(log, new EngineSpec())));
        Add("power.variant", () => DigPower(PowerEstimate.For(log, new EngineSpec
        {
            Litres = 3.0,
            Cylinders = 6,
            Fuel = Fuel.E85,
            BatchInjection = true,
            FuelPressureIsDifferential = true,
            DrivetrainLossPercent = 15,
            InjectorCcPerMinute = 850,
            Lambda = 0.8,
            VolumetricEfficiency = 88,
        })));

        if (other is not null)
            Add("compare.overlap", () =>
            {
                ChannelOverlap o = LogComparison.Compare(log, other);
                return Fin($"{string.Join(",", o.Shared)}/{string.Join(",", o.OnlyInFirst)}/{string.Join(",", o.OnlyInSecond)}/{o.Summary}");
            });

        Add("csv.log", () => Csv(w => CsvExport.WriteLog(w, log, log.Channels.Reverse().Take(6).ToList(), 3, 52)));

        if (rpm is null || map is null || afr is null) return items;

        SampleMask mask = SampleFilter.Build(log, [new LogFilter { Name = "rpm", Channel = rpm.Name, Comparison = FilterComparison.Above, Low = 1000 }]);
        Add("mask", () => $"pass={mask.PassCount} total={mask.Total} applied={mask.FiltersApplied} h={Fnv(string.Concat(mask.Accepted.Select(a => a ? '1' : '0')))}");

        // Auto-axis tables.
        foreach (HistogramStatistic stat in Enum.GetValues<HistogramStatistic>())
        {
            Add($"auto.{stat}", () => Dig(HistogramTable.Build(rpm, map, afr, 12, 9, 0, n - 1, stat)));
            Add($"auto.masked.{stat}", () => Dig(HistogramTable.Build(rpm, map, afr, 12, 9, 0, n - 1, stat, mask)));
        }
        Add("auto.window", () => Dig(HistogramTable.Build(rpm, map, afr, 8, 8, n / 3, n / 2, HistogramStatistic.Mean, mask)));
        Add("auto.reversed", () => Dig(HistogramTable.Build(rpm, map, afr, 5, 5, n / 2, n / 3, HistogramStatistic.Max)));
        if (target is not null)
            Add("auto.delta", () => Dig(HistogramTable.Build(rpm, map, afr, 10, 10, 0, n - 1, HistogramStatistic.Mean, mask, target)));

        Add("scatter", () =>
        {
            ScatterPlot p = ScatterPlot.Build(rpm, map, afr, 0, n - 1, mask, target);
            return DigScatter(p, p.Bin(40, 30));
        });
        Add("scatter.plain", () =>
        {
            ScatterPlot p = ScatterPlot.Build(rpm, map, afr, 0, n - 1);
            return DigScatter(p, p.Bin(7, 5));
        });
        Add("csv.points", () => Csv(w => CsvExport.WritePoints(w, ScatterPlot.Build(rpm, map, afr, n / 4, n / 2, mask, target))));

        // Onto the tune's own axes.
        Add("grid", () =>
        {
            TuneTable g = VeAnalysis.GridFrom(rpm, map, 10, 8, 0, n - 1, mask);
            return Fin($"{g.Name}|{string.Join(",", g.X.Breakpoints.Select(B))}|{string.Join(",", g.Y.Breakpoints.Select(B))}|{g.X.Units}|{g.Y.Units}|{g.Units}");
        });
        Add("ve.grid", () => DigVe(VeAnalysis.Analyse(VeAnalysis.GridFrom(rpm, map, 10, 8, 0, n - 1, mask), rpm, map, afr, target ?? afr, 0, n - 1, mask)));

        if (tune is null) return items;

        double[] xs = tune.X.Breakpoints, ys = tune.Y.Breakpoints;
        foreach (HistogramStatistic stat in Enum.GetValues<HistogramStatistic>())
        {
            Add($"tune.{stat}", () => Dig(HistogramTable.Build(rpm, map, afr, xs, ys, 0, n - 1, stat)));
            Add($"tune.masked.{stat}", () => Dig(HistogramTable.Build(rpm, map, afr, xs, ys, 0, n - 1, stat, mask)));
        }

        HistogramTable busy = HistogramTable.Build(rpm, map, afr, xs, ys, 0, n - 1, HistogramStatistic.Mean, mask);
        Add("trace", () =>
        {
            int bc = 0, br = 0, best = -1;
            for (int c = 0; c < busy.Columns; c++)
                for (int r = 0; r < busy.Rows; r++)
                    if (busy.Counts[c, r] > best) { best = busy.Counts[c, r]; bc = c; br = r; }

            IReadOnlyList<int> hits = busy.SamplesIn(bc, br);
            var visits = busy.VisitsTo(bc, br);
            var longest = busy.LongestVisitTo(bc, br);
            CellVisits v = busy.VisitedBy(n / 3, -5 + n / 2);
            var cell = busy.CellOf(n / 2);
            return Fin($"{bc},{br}|{hits.Count}|{Fnv(Ints(hits))}|{string.Join(",", visits.Select(x => $"{x.First}-{x.Last}"))}|{longest?.First}-{longest?.Last}|cells={v.Cells} samples={v.Samples} outside={v.Outside} counts={Ints(v.Counts.Cast<int>())}|cell={cell?.Column},{cell?.Row}");
        });
        Add("csv.table", () => Csv(w => CsvExport.WriteTable(w, busy)));
        Add("csv.counts", () => Csv(w => CsvExport.WriteTableCounts(w, busy)));
        Add("csv.tune", () => Csv(w => CsvExport.WriteTuneTable(w, tune)));

        if (target is not null)
        {
            Add("tune.delta", () => Dig(HistogramTable.Build(rpm, map, afr, xs, ys, 0, n - 1, HistogramStatistic.Mean, mask, target)));

            foreach ((string tag, VeAnalysisSettings s) in new (string, VeAnalysisSettings)[]
            {
                ("default", new VeAnalysisSettings()),
                ("delay3", new VeAnalysisSettings { MeasurementDelaySamples = 3, MinimumSamples = 3 }),
                ("raw", new VeAnalysisSettings { MinimumSamples = 1, ConfidenceSamples = 0, MaxChangePercent = 40, Authority = 0.5 }),
            })
            {
                Add($"ve.{tag}", () => DigVe(VeAnalysis.Analyse(tune, rpm, map, afr, target, 0, n - 1, mask, s)));
                Add($"ve.{tag}.unmasked", () => DigVe(VeAnalysis.Analyse(tune, rpm, map, afr, target, 0, n - 1, null, s)));
            }

            Add("ve.tables", () =>
            {
                VeAnalysisResult r = VeAnalysis.Analyse(tune, rpm, map, afr, target, 0, n - 1, mask, new VeAnalysisSettings { MinimumSamples = 2 });
                return Fnv(Dig(r.AsChangeTable(rpm, map, afr, target, 0, n - 1, mask)) + "/" + Dig(r.AsSuggestedTable(rpm, map, afr, 0, n - 1, mask)));
            });

            double interval = log.MedianSampleInterval;
            Add("delay", () => DigDelay(WidebandDelay.Find(tune, rpm, map, afr, target, 0, n - 1, interval, mask)));
            Add("delay.unmasked", () => DigDelay(WidebandDelay.Find(tune, rpm, map, afr, target, 0, n - 1, interval, null, 0.5)));
        }

        if (other is not null)
        {
            LogChannel? r2 = ChannelRoles.Find(other, ChannelRole.EngineSpeed);
            LogChannel? m2 = ChannelRoles.Find(other, ChannelRole.ManifoldPressure);
            LogChannel? a2 = ChannelRoles.Find(other, ChannelRole.Mixture);
            if (r2 is not null && m2 is not null && a2 is not null)
            {
                Add("compare.diff", () =>
                {
                    HistogramTable second = HistogramTable.Build(r2, m2, a2, xs, ys, 0, other.SampleCount - 1, HistogramStatistic.Mean);
                    HistogramTable first = HistogramTable.Build(rpm, map, afr, xs, ys, 0, n - 1, HistogramStatistic.Mean);
                    HistogramTable d = LogComparison.Difference(first, second);
                    var (both, a, b) = LogComparison.Coverage(first, second);
                    ComparisonSummary s1 = LogComparison.Summarise(d);
                    ComparisonSummary s2 = LogComparison.Summarise(d, 5);
                    return Fnv($"{Dig(d)}/{both},{a},{b}/{s1.Cells},{B(s1.Mean)},{B(s1.Largest)},{B(s1.AtColumn)},{B(s1.AtRow)}/{s2.Cells},{B(s2.Mean)},{B(s2.Largest)},{B(s2.AtColumn)},{B(s2.AtRow)}");
                });
            }
        }

        return items;
    }
}
