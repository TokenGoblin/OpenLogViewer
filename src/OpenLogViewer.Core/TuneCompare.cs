namespace OpenLogViewer.Core;

/// <summary>One setting that two tunes disagree about.</summary>
/// <param name="Name">The constant's name.</param>
/// <param name="Constant">Its declaration, for units and for naming its options.</param>
/// <param name="Cells">
/// How many cells differ, which is 1 for anything that is not a table. Worth
/// having on its own: "3 of 256 cells" and "all 256" are different situations
/// and the first values alone do not tell them apart.
/// </param>
/// <param name="Mine">The first tune's value at the first differing cell.</param>
/// <param name="Theirs">The second tune's value there.</param>
public sealed record TuneDifference(
    string Name, TuneConstant Constant, int Cells, double? Mine, double? Theirs)
{
    /// <summary>
    /// What each side holds where the setting is text rather than a number.
    ///
    /// Kept, because throwing it away left the one kind of setting whose value a
    /// person recognises on sight reading "1 of 32 cells differ, first — against
    /// —". A name is a name; it should say which name.
    /// </summary>
    public string? MineText { get; init; }

    public string? TheirsText { get; init; }

    /// <summary>
    /// True for a table or a set of breakpoints — never for text, whose width is
    /// how many characters fit rather than how many values it holds.
    /// </summary>
    public bool IsArray => !Constant.IsText && Constant.Columns * Constant.Rows > 1;

    /// <summary>What the first tune holds, named where the firmware names it.</summary>
    public string MineShown => MineText is { } t ? Quoted(t) : Shown(Mine);

    /// <summary>What the second holds.</summary>
    public string TheirsShown => TheirsText is { } t ? Quoted(t) : Shown(Theirs);

    private static string Quoted(string text) => text.Length == 0 ? "(blank)" : $"\"{text}\"";

    /// <summary>One line describing the disagreement.</summary>
    public string Summary =>
        IsArray
            ? $"{Name}: {Cells:N0} of {Constant.Columns * Constant.Rows:N0} cells differ, "
              + $"first {MineShown} against {TheirsShown}"
            : $"{Name}: {MineShown} against {TheirsShown}";

    private string Shown(double? value)
    {
        if (value is not { } v) return "—";

        string text = Constant.HasOptions
            ? Constant.OptionName(v)
            : v.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);

        return Constant.Units.Length > 0 && !Constant.HasOptions ? $"{text} {Constant.Units}" : text;
    }
}

/// <summary>
/// What two tunes disagree about, setting by setting.
///
/// <para>
/// The question a tuner asks constantly and no log can answer: <em>is the ECU
/// running what I think it is?</em> A saved file and a controller drift apart
/// the moment anybody changes something without saving, and the difference is
/// invisible — both are pages of plausible numbers. Naming the settings that
/// differ turns "I think this is the right tune" into knowing.
/// </para>
/// <para>
/// Compared through the values rather than the bytes, deliberately. Two tunes
/// can hold different bytes and mean the same thing, because bits no constant
/// declares differ freely and a file cannot carry them; showing those as
/// differences would bury the handful that matter under scores that do not.
/// </para>
/// </summary>
public static class TuneCompare
{
    /// <summary>
    /// Every setting the two hold differently, in declaration order.
    /// </summary>
    /// <param name="mine">Usually the file.</param>
    /// <param name="theirs">Usually the controller.</param>
    public static IReadOnlyList<TuneDifference> Compare(EcuTune mine, EcuTune theirs)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(theirs);

        var differences = new List<TuneDifference>();

        // The later declaration of a name is the one everything else resolves
        // to, and two names differing only in case are two settings.
        var winners = mine.Layout.Constants
            .Where(c => c.OnController)
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (TuneConstant constant in mine.Layout.Constants)
        {
            if (!constant.OnController) continue;
            if (!ReferenceEquals(winners.GetValueOrDefault(constant.Name), constant)) continue;

            if (constant.IsText)
            {
                string a = mine.TextIn(mine.Pages, constant.Name) ?? "";
                string b = theirs.TextIn(theirs.Pages, constant.Name) ?? "";

                if (a != b)
                {
                    differences.Add(new TuneDifference(constant.Name, constant, 1, null, null)
                    {
                        MineText = a,
                        TheirsText = b,
                    });
                }

                continue;
            }

            int cells = Math.Max(1, constant.Columns * constant.Rows);
            int differing = 0;
            double? firstMine = null, firstTheirs = null;

            for (int i = 0; i < cells; i++)
            {
                double? a = mine.ValueIn(mine.Pages, constant.Name, i);
                double? b = theirs.ValueIn(theirs.Pages, constant.Name, i);

                if (Same(a, b)) continue;

                if (differing == 0) (firstMine, firstTheirs) = (a, b);
                differing++;
            }

            if (differing > 0)
                differences.Add(new TuneDifference(constant.Name, constant, differing, firstMine, firstTheirs));
        }

        return differences;
    }

    /// <summary>
    /// Whether two readings of the same setting mean the same thing.
    ///
    /// Compared to within half a step of what the setting can actually store,
    /// because both sides came from bytes through the same arithmetic and the
    /// only difference that can exist is one the storage could hold. A plain
    /// equality on doubles would call two identical bytes different.
    /// </summary>
    private static bool Same(double? a, double? b)
    {
        if (a is null || b is null) return a is null && b is null;

        return Math.Abs(a.Value - b.Value) < 1e-9;
    }
}
