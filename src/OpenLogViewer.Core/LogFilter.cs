using System.Globalization;

namespace OpenLogViewer.Core;

public enum FilterComparison
{
    Above,
    AboveOrEqual,
    Below,
    BelowOrEqual,
    Between,
    Outside,
}

/// <summary>
/// A condition a sample must satisfy to be counted. Filters exist so a table is
/// built from data worth reading — warmup, overrun and idle skew a fuel table
/// badly, and a cell averaged over both a cold start and a hot cruise describes
/// neither.
/// </summary>
public sealed record LogFilter
{
    public required string Name { get; init; }

    /// <summary>Channel this tests, matched by name so it can apply across logs.</summary>
    public required string Channel { get; init; }

    public required FilterComparison Comparison { get; init; }

    public double Low { get; init; }

    public double High { get; init; }

    public bool Enabled { get; init; } = true;

    public bool Accepts(double value)
    {
        // A missing reading cannot be shown to satisfy the condition.
        if (double.IsNaN(value)) return false;

        return Comparison switch
        {
            FilterComparison.Above => value > Low,
            FilterComparison.AboveOrEqual => value >= Low,
            FilterComparison.Below => value < Low,
            FilterComparison.BelowOrEqual => value <= Low,
            FilterComparison.Between => value >= Math.Min(Low, High) && value <= Math.Max(Low, High),
            FilterComparison.Outside => value < Math.Min(Low, High) || value > Math.Max(Low, High),
            _ => true,
        };
    }

    /// <summary>Reads back as the condition itself, e.g. "CLT ≥ 160".</summary>
    public string Describe()
    {
        string low = Format(Low);
        string high = Format(High);

        return Comparison switch
        {
            FilterComparison.Above => $"{Channel} > {low}",
            FilterComparison.AboveOrEqual => $"{Channel} ≥ {low}",
            FilterComparison.Below => $"{Channel} < {low}",
            FilterComparison.BelowOrEqual => $"{Channel} ≤ {low}",
            FilterComparison.Between => $"{Channel} {low}…{high}",
            FilterComparison.Outside => $"{Channel} outside {low}…{high}",
            _ => Channel,
        };

        static string Format(double v) =>
            v == Math.Floor(v) && Math.Abs(v) < 1e9
                ? ((long)v).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

/// <summary>Which samples survived a set of filters.</summary>
public sealed class SampleMask
{
    public required bool[] Accepted { get; init; }

    public required int PassCount { get; init; }

    public required int Total { get; init; }

    /// <summary>Filters that named a channel this log does not have.</summary>
    public required IReadOnlyList<string> UnknownChannels { get; init; }

    public bool FiltersApplied { get; init; }

    public static SampleMask AcceptAll(int count) => new()
    {
        Accepted = [],
        PassCount = count,
        Total = count,
        UnknownChannels = [],
        FiltersApplied = false,
    };

    /// <summary>True when the sample should be counted.</summary>
    public bool this[int index] => !FiltersApplied || Accepted[index];
}

public static class SampleFilter
{
    /// <summary>
    /// Evaluates every enabled filter against each sample. Filters combine with
    /// AND: each one narrows the set further, which is what "throw these out"
    /// means when several conditions are on at once.
    ///
    /// A filter naming a channel the log does not have is reported and skipped,
    /// rather than silently rejecting every sample.
    /// </summary>
    public static SampleMask Build(LogDocument document, IEnumerable<LogFilter> filters)
    {
        int count = document.SampleCount;

        var active = new List<(LogFilter Filter, LogChannel Channel)>();
        var unknown = new List<string>();

        foreach (LogFilter filter in filters.Where(f => f.Enabled))
        {
            LogChannel? channel = document.FindChannel(filter.Channel);
            if (channel is null) unknown.Add(filter.Channel);
            else active.Add((filter, channel));
        }

        if (active.Count == 0)
        {
            return new SampleMask
            {
                Accepted = [],
                PassCount = count,
                Total = count,
                UnknownChannels = unknown,
                FiltersApplied = false,
            };
        }

        var accepted = new bool[count];
        int passed = 0;

        for (int i = 0; i < count; i++)
        {
            bool ok = true;
            foreach ((LogFilter filter, LogChannel channel) in active)
            {
                if (filter.Accepts(channel.At(i))) continue;
                ok = false;
                break;
            }

            accepted[i] = ok;
            if (ok) passed++;
        }

        return new SampleMask
        {
            Accepted = accepted,
            PassCount = passed,
            Total = count,
            UnknownChannels = unknown,
            FiltersApplied = true,
        };
    }

    /// <summary>
    /// Filters worth having on by default, for the channels a log actually has.
    /// These are the conditions that most often ruin a fuel table.
    /// </summary>
    public static IEnumerable<LogFilter> Suggest(LogDocument document)
    {
        if (document.FindChannel("CLT") is { } clt && !clt.IsFlat)
        {
            // Operating temperature, in whatever unit the log uses.
            bool fahrenheit = clt.Units.Contains('F', StringComparison.OrdinalIgnoreCase) || clt.Max > 130;
            yield return new LogFilter
            {
                Name = "Up to temperature",
                Channel = clt.Name,
                Comparison = FilterComparison.AboveOrEqual,
                Low = fahrenheit ? 160 : 70,
                Enabled = false,
            };
        }

        if (document.FindChannel("RPM") is { } rpm && !rpm.IsFlat)
        {
            yield return new LogFilter
            {
                Name = "Engine running",
                Channel = rpm.Name,
                Comparison = FilterComparison.AboveOrEqual,
                Low = 500,
                Enabled = false,
            };
        }

        if (document.FindChannel("TPS") is { } tps && !tps.IsFlat)
        {
            yield return new LogFilter
            {
                Name = "Off idle",
                Channel = tps.Name,
                Comparison = FilterComparison.Above,
                Low = 1,
                Enabled = false,
            };
        }

        if (document.FindChannel("AFR") is { } afr && !afr.IsFlat)
        {
            yield return new LogFilter
            {
                Name = "AFR in range",
                Channel = afr.Name,
                Comparison = FilterComparison.Between,
                Low = 9,
                High = 20,
                Enabled = false,
            };
        }
    }
}
