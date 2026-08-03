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
        // Identifying the channel and deciding whether to filter on it are
        // separate questions. A coolant reading that never moves is still the
        // coolant, and looking past it would find something that is not — a
        // MegaSquirt log holds both "AFR" and "AFR Load", and skipping a flat
        // AFR offered a mixture filter on a load axis.
        LogChannel? Moving(ChannelRole role) =>
            ChannelRoles.Find(document, role) is { IsFlat: false } channel ? channel : null;

        if (Moving(ChannelRole.EngineSpeed) is { } rpm)
            yield return new LogFilter
            {
                Name = "Engine running",
                Channel = rpm.Name,
                Comparison = FilterComparison.AboveOrEqual,
                Low = 500,
                Enabled = false,
            };

        if (Moving(ChannelRole.Coolant) is { } clt)
        {
            // Operating temperature, in whatever unit the log uses. Judged by
            // the readings as well as the label, because plenty of firmware
            // writes a placeholder rather than a unit.
            bool fahrenheit =
                clt.Units.Contains('F', StringComparison.OrdinalIgnoreCase) || clt.Max > 130;

            yield return new LogFilter
            {
                Name = "Up to temperature",
                Channel = clt.Name,
                Comparison = FilterComparison.AboveOrEqual,
                Low = fahrenheit ? 160 : 70,
                Enabled = false,
            };
        }

        if (Moving(ChannelRole.Throttle) is { } tps)
        {
            // Idle is its own regime — the ECU is holding a speed rather than
            // metering for a demand — and averaging it into the low-load cells
            // of a fuel table describes neither.
            //
            // Named for what it does and no more. It is tempting to call this
            // "not on overrun", and it does not earn that: on the MaxxECU log
            // the samples that look like overrun sit at six to sixteen per cent
            // throttle and sail straight through. Use the fuel-cut filter for
            // that, which is the controller saying so rather than a guess.
            yield return new LogFilter
            {
                Name = "Off idle",
                Channel = tps.Name,
                Comparison = FilterComparison.Above,
                Low = 1,
                Enabled = false,
            };
        }

        if (Moving(ChannelRole.FuelCut) is { } cut)
        {
            // While the ECU is cutting fuel there is no fuelling to judge: the
            // exhaust is full of air the engine pumped through without burning,
            // and the wideband reports it faithfully. Those samples say nothing
            // about the table and would drag whichever cells they land in.
            //
            // Measured on the MaxxECU log: active in 70 samples of 6,466 — few,
            // but every one of them meaningless.
            yield return new LogFilter
            {
                Name = "Not cutting fuel",
                Channel = cut.Name,
                Comparison = FilterComparison.BelowOrEqual,
                Low = 0,
                Enabled = false,
            };
        }

        if (Moving(ChannelRole.Mixture) is { } mixture)
        {
            // The plausible range depends on which scale it is on, and the
            // readings say which far more reliably than the label: lambda sits
            // about 1, an air-fuel ratio about 14. Nine to twenty on a lambda
            // channel would accept everything and filter nothing.
            bool lambda = mixture.Max < 3;

            yield return new LogFilter
            {
                Name = lambda ? "Lambda in range" : "AFR in range",
                Channel = mixture.Name,
                Comparison = FilterComparison.Between,
                Low = lambda ? 0.6 : 9,
                High = lambda ? 1.4 : 20,
                Enabled = false,
            };
        }
    }
}
