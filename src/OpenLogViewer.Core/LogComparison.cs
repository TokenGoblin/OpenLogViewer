namespace OpenLogViewer.Core;

/// <summary>What two logs have in common, and what only one of them has.</summary>
/// <param name="Shared">Channels in both, by the name they share.</param>
/// <param name="OnlyInFirst">Channels the first log has and the second does not.</param>
/// <param name="OnlyInSecond">And the other way round.</param>
public sealed record ChannelOverlap(
    IReadOnlyList<string> Shared,
    IReadOnlyList<string> OnlyInFirst,
    IReadOnlyList<string> OnlyInSecond)
{
    public bool AnythingShared => Shared.Count > 0;

    /// <summary>
    /// A sentence about how well the two match, because a comparison of two logs
    /// with almost nothing in common is worth being warned about rather than
    /// discovering by finding an empty plot.
    /// </summary>
    public string Summary => Shared.Count switch
    {
        0 => "These two logs share no channel names at all, so there is nothing to compare. "
             + "They are probably from different firmware, or one is an export with renamed "
             + "columns.",

        _ when OnlyInFirst.Count == 0 && OnlyInSecond.Count == 0 =>
            $"Both logs carry the same {Shared.Count} channels.",

        _ => $"{Shared.Count} channels in both"
             + (OnlyInFirst.Count > 0 ? $", {OnlyInFirst.Count} only in the first" : "")
             + (OnlyInSecond.Count > 0 ? $", {OnlyInSecond.Count} only in the second" : "")
             + ".",
    };
}

/// <summary>
/// Two logs, read against each other.
///
/// The comparison a tuner actually makes: change something, drive it again, and
/// find out what moved. Everything here exists because doing that by eye across
/// two windows is how a change gets credited with an improvement that was really
/// a warmer engine or a different gear.
///
/// Two things make it harder than subtracting one column from another. The logs
/// do not line up in time — they are different lengths, started at different
/// moments, and the interesting parts are not at the same offset. And they do not
/// line up in <em>content</em> either: a firmware update renames channels, an
/// export drops some, and a comparison that silently matched fifteen channels out
/// of two hundred would look like it had worked.
///
/// So nothing here interpolates one log onto the other's timebase. Comparing at
/// the same clock time assumes the two runs were the same run, which is exactly
/// what is being tested. What is compared instead is the shape of each: the same
/// cells of the same table, which is what the histogram was already for.
/// </summary>
public static class LogComparison
{
    /// <summary>
    /// Which channels the two share, matched by name.
    ///
    /// By name and not by position, because position means nothing between two
    /// logs — a firmware that adds one channel shifts every column after it, and
    /// a comparison matched by column would compare coolant against oil pressure
    /// without noticing.
    /// </summary>
    public static ChannelOverlap Compare(LogDocument first, LogDocument second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var a = new HashSet<string>(
            first.Channels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        var b = new HashSet<string>(
            second.Channels.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        return new ChannelOverlap(
            [.. a.Intersect(b, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            [.. a.Except(b, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            [.. b.Except(a, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// One table subtracted from another, cell by cell.
    ///
    /// The comparison that means something. Two logs cannot be lined up in time,
    /// but they can be binned onto the same axes and read cell for cell — "at 3,000
    /// rpm and 150 kPa the mixture is now 0.4 richer than it was" is a statement
    /// about the change, and it does not care that one run was longer or started
    /// in a different gear.
    ///
    /// A cell is only reported where both tables actually have data. Treating an
    /// empty cell as zero would invent a difference the size of the whole reading
    /// everywhere the second run did not go — which on any two real drives is most
    /// of the table, and would be the largest and most eye-catching numbers on it.
    /// </summary>
    /// <returns>
    /// A table on the first one's axes, holding the difference where both have
    /// data and nothing where either does not. Counts are the smaller of the two,
    /// being how much evidence the difference actually rests on.
    /// </returns>
    public static HistogramTable Difference(HistogramTable first, HistogramTable second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (first.Columns != second.Columns || first.Rows != second.Rows)
            throw new ArgumentException(
                "Two tables can only be subtracted on identical axes. Bin both onto the same "
                + "breakpoints first.", nameof(second));

        var values = new double?[first.Columns, first.Rows];
        var counts = new int[first.Columns, first.Rows];

        for (int c = 0; c < first.Columns; c++)
            for (int r = 0; r < first.Rows; r++)
            {
                double? a = first.Values[c, r];
                double? b = second.Values[c, r];

                if (a is null || b is null) continue;

                values[c, r] = a.Value - b.Value;

                // The weaker of the two, because a difference is only as well
                // evidenced as its thinner side.
                counts[c, r] = Math.Min(first.Counts[c, r], second.Counts[c, r]);
            }

        // Built through the same door a VE suggestion uses, so a difference is an
        // ordinary table: it shades by sample count, traces back to the log and
        // exports like any other.
        return HistogramTable.FromCells(
            first.X, first.Y, first.Z,
            first.ColumnCenters, first.RowCenters,
            values, counts,
            first.FirstSample, first.LastSample,
            displayName: $"{first.Z.Name} — change",
            displayDigits: first.Z.Digits);
    }

    /// <summary>
    /// How much of the two tables can actually be compared.
    ///
    /// The number that says whether a comparison is worth reading. Two drives that
    /// overlap in a tenth of the table have not been compared in any useful sense,
    /// and the difference table will look sparse for a reason worth stating rather
    /// than leaving to be inferred from the gaps.
    /// </summary>
    public static (int Both, int OnlyFirst, int OnlySecond) Coverage(
        HistogramTable first, HistogramTable second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        int both = 0, onlyA = 0, onlyB = 0;

        for (int c = 0; c < Math.Min(first.Columns, second.Columns); c++)
            for (int r = 0; r < Math.Min(first.Rows, second.Rows); r++)
            {
                bool a = first.Values[c, r] is not null;
                bool b = second.Values[c, r] is not null;

                if (a && b) both++;
                else if (a) onlyA++;
                else if (b) onlyB++;
            }

        return (both, onlyA, onlyB);
    }

    /// <summary>
    /// What changed, in one line, from a difference table.
    ///
    /// Worth computing rather than leaving the eye to find it in a grid: the
    /// average tells you whether the whole table moved, and the largest single
    /// change tells you whether something moved a lot somewhere specific. Those
    /// are different findings and a coloured table shows them equally.
    /// </summary>
    public static ComparisonSummary Summarise(HistogramTable difference, int minimumSamples = 1)
    {
        ArgumentNullException.ThrowIfNull(difference);

        double total = 0;
        int cells = 0;
        double biggest = 0;
        int atColumn = -1, atRow = -1;

        for (int c = 0; c < difference.Columns; c++)
            for (int r = 0; r < difference.Rows; r++)
            {
                if (difference.Values[c, r] is not { } value) continue;
                if (difference.Counts[c, r] < minimumSamples) continue;

                total += value;
                cells++;

                if (Math.Abs(value) <= Math.Abs(biggest)) continue;

                biggest = value;
                atColumn = c;
                atRow = r;
            }

        return new ComparisonSummary(
            cells,
            cells > 0 ? total / cells : double.NaN,
            cells > 0 ? biggest : double.NaN,
            atColumn >= 0 ? difference.ColumnCenters[atColumn] : double.NaN,
            atRow >= 0 ? difference.RowCenters[atRow] : double.NaN);
    }
}

/// <summary>What a difference table adds up to.</summary>
/// <param name="Cells">How many cells both runs visited with enough samples.</param>
/// <param name="Mean">The average change across them — whether the whole table moved.</param>
/// <param name="Largest">The biggest single change, signed.</param>
/// <param name="AtColumn">Where that was, on the first table's axes.</param>
/// <param name="AtRow">Likewise.</param>
public sealed record ComparisonSummary(
    int Cells, double Mean, double Largest, double AtColumn, double AtRow)
{
    public bool Any => Cells > 0;
}
