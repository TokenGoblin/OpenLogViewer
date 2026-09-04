using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>One setting in a saved tune that could not be put into the layout.</summary>
/// <param name="Name">The constant's name.</param>
/// <param name="Reason">What went wrong, in words fit to show someone.</param>
public sealed record MsqComplaint(string Name, string Reason)
{
    public override string ToString() => $"{Name}: {Reason}";
}

/// <summary>
/// A saved tune laid over a firmware definition: the settings as bytes, and an
/// honest account of what did not fit.
///
/// <para>
/// <b>The account is the point.</b> A tune and a definition that disagree still
/// produce a tune object — every constant the file did not mention keeps the
/// zero it started as, and a page full of zeros looks exactly like a page of
/// real settings. Sending one to a controller would write those zeros. So a
/// caller is given the shortfall rather than left to notice it, and anything
/// that can reach an ECU is expected to refuse a tune that did not load whole.
/// </para>
/// </summary>
/// <param name="Tune">The settings, as pages of bytes.</param>
/// <param name="Applied">How many constants were set from the file.</param>
/// <param name="Missing">
/// Constants the firmware declares that the file never mentioned. These are the
/// dangerous ones: each is a setting left at zero that looks like a setting.
/// </param>
/// <param name="Rejected">
/// Constants the file gave a value this could not store — an option name the
/// firmware does not offer, a number outside the range, the wrong number of
/// cells for a table.
/// </param>
/// <param name="Unknown">
/// Names the file carries that this firmware has no constant for. Harmless in
/// itself, and worth counting: a great many of them means the tune belongs to
/// another firmware.
/// </param>
public sealed record MsqLoad(
    EcuTune Tune,
    int Applied,
    IReadOnlyList<MsqComplaint> Missing,
    IReadOnlyList<MsqComplaint> Rejected,
    IReadOnlyList<string> Unknown)
{
    /// <summary>True when every setting the firmware declares came from the file.</summary>
    public bool IsComplete => Missing.Count == 0 && Rejected.Count == 0;

    /// <summary>
    /// Whether this looks like a tune for some other firmware.
    ///
    /// Judged on the share of the definition that went unfilled rather than on
    /// the signature, because a signature is often close but not equal — a tune
    /// saved from revision 3.4.2 opened against 3.4.3 — and that case reads
    /// almost all of the file correctly. Half a definition left at zero does
    /// not.
    /// </summary>
    public bool LooksLikeAnotherFirmware =>
        Applied + Missing.Count > 0 && Missing.Count > (Applied + Missing.Count) / 2;

    /// <summary>What to tell someone, in one line.</summary>
    public string Summary =>
        IsComplete
            ? $"{Applied:N0} settings read."
            : $"{Applied:N0} settings read, {Missing.Count:N0} the firmware declares were not in "
              + $"the file, {Rejected.Count:N0} could not be stored."
              + (Unknown.Count > 0 ? $" {Unknown.Count:N0} in the file are not in this firmware." : "");
}

/// <summary>Laying a saved tune over a firmware definition.</summary>
public static class MsqApply
{
    /// <summary>
    /// Builds the ECU's pages from a saved tune.
    ///
    /// <para>
    /// The file gives values in the units a person reads and this turns them
    /// back into bytes, which is the same encoding a write to the controller
    /// uses — deliberately, so a tune opened from a file and a tune read off an
    /// ECU are the same kind of thing and everything downstream works on either.
    /// </para>
    /// <para>
    /// A bit field is stored in the file as the <em>name</em> of the option
    /// chosen — <c>"Speed Density"</c>, not 1 — because the file has to be
    /// readable without the definition beside it. So the label is looked up in
    /// the firmware's own list, and its position is the number. A label the
    /// firmware does not offer is refused rather than guessed at: an option list
    /// that has shifted between revisions is exactly the case where a guess
    /// stores the wrong setting and reports success.
    /// </para>
    /// </summary>
    /// <param name="onto">
    /// Bytes to lay the file over, or null to start from nothing.
    ///
    /// <b>Pass the controller's own pages when restoring a tune to it.</b> A
    /// definition does not declare every bit it has: reserved bits, and bits a
    /// later firmware will use, belong to no constant, so a file cannot carry
    /// them and starting from zero silently clears them. On a Speeduino that is
    /// 72 bytes of the 3,408. Laying the file over what the ECU already holds
    /// changes the settings the file names and leaves the rest exactly as they
    /// are, which is what restoring a backup ought to mean.
    /// </param>
    public static MsqLoad Load(TuneLayout layout, MsqFile file, EcuTune? onto = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(file);

        byte[][] pages =
        [
            .. layout.Pages.Select((p, i) =>
                onto is not null && i < onto.Pages.Count && onto.Pages[i].Length == p.Size
                    ? onto.Pages[i].ToArray()
                    : new byte[p.Size]),
        ];

        var tune = EcuTune.FromPages(layout, pages);

        var missing = new List<MsqComplaint>();
        var rejected = new List<MsqComplaint>();
        int applied = 0;

        // Twice, where a firmware states any scale in terms of a setting. Those
        // sums are done when the tune is built, and a tune being filled in from
        // a file is empty at that moment — so a scale written
        // {0.01 * (maf_range + 1)} is worked out with the range reading nought,
        // and every value stored through it is wrong or refused outright. The
        // first pass puts the settings in, the sums are done again against them,
        // and the second pass is the one that counts.
        int passes = tune.HasExpressionScales ? 2 : 1;

        for (int pass = 0; pass < passes; pass++)
        {
            if (pass > 0)
            {
                tune.Rescale();
                missing.Clear();
                rejected.Clear();
                applied = 0;
            }

        // Where a name is declared twice the later one wins, which is how every
        // other reader resolves it. Spelled exactly: MS2Extra has two different
        // settings called MAFFlow and mafflow, on different pages, and merging
        // them loses one of the two.
        //
        // Applied in declaration order all the same, so that where two
        // *differently* named constants overlap in the controller's memory — the
        // same firmware puts testint and testrpm in one pair of bytes — the
        // later of them is what the bytes end up holding, again matching how the
        // name resolves. A file states both and only one can be true.
        Dictionary<string, TuneConstant> winners = layout.Constants
            .Where(c => c.OnController)
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (TuneConstant constant in layout.Constants)
        {
            if (!constant.OnController) continue;
            if (!ReferenceEquals(winners.GetValueOrDefault(constant.Name), constant)) continue;

            if (file.Value(constant.Name) is not { } written)
            {
                missing.Add(new MsqComplaint(constant.Name, "not in the file"));
                continue;
            }

            // Through the tune rather than the layout: a scale the firmware
            // wrote as an expression is worked out once the values exist, and
            // encoding with the declared fallback while decoding with the
            // worked-out one puts the two out by whatever the expression came
            // to. The tune's copy is the one everything else reads through.
            TuneConstant use = tune.Constant(constant.Name) ?? constant;

            if (Store(tune, pages, use, written) is { } complaint) rejected.Add(complaint);
            else applied++;
            }
        }

        // Names in the file with nothing to put them in. Counted rather than
        // complained about one by one: on a tune from a neighbouring revision
        // there are a handful, and on a tune from another firmware there are
        // hundreds, and the difference is the useful part.
        var unknown = new List<string>();

        var declared = new HashSet<string>(
            layout.Constants.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        foreach (string name in file.Values.Keys)
            if (!declared.Contains(name)) unknown.Add(name);

        return new MsqLoad(tune, applied, missing, rejected, unknown);
    }

    /// <summary>Puts one written value into the pages, or says why it would not go.</summary>
    private static MsqComplaint? Store(
        EcuTune tune, IReadOnlyList<byte[]> pages, TuneConstant constant, string written)
    {
        if (constant.IsText)
        {
            // Left exactly as they are when the field already says this. Reading
            // a text field trims its padding and writing one pads with nulls, so
            // a controller that pads with anything else — spaces, or the
            // newlines a rusEFI leaves after a Lua script — comes out differing
            // from itself in bytes while agreeing in every value. That phantom
            // difference is what a restore plans a write for, so restoring a
            // tune to the ECU it was read from would send bytes and report that
            // nothing had changed.
            if (string.Equals(tune.TextIn(pages, constant.Name), Unquote(written), StringComparison.Ordinal))
                return null;

            return tune.PokeTextInto(pages, constant, Unquote(written))
                ? null
                : new MsqComplaint(constant.Name, "the name does not fit the field, or is not ASCII");
        }

        int cells = Math.Max(1, constant.Columns * constant.Rows);

        if (cells > 1)
        {
            string[] tokens = written.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length != cells)
                return new MsqComplaint(
                    constant.Name, $"the file holds {tokens.Length} values where the firmware wants {cells}");

            // All of it or none of it.
            //
            // A cell that will not fit used to be reported after the cells before
            // it had already been poked in, and the caller records the complaint
            // and carries on — so the half-written image was the one a restore
            // then byte-diffed against the controller. One out-of-range cell in a
            // fuel table produced real writes for the rest of that table, while
            // the plan told the person that constant "would be left alone". That
            // is the worst way for this to be wrong: the reassurance and the
            // damage came from the same run.
            byte[]? before = Snapshot(pages, constant);

            // Row-major, which is how the file writes a grid and how the
            // controller stores one.
            for (int i = 0; i < cells; i++)
            {
                MsqComplaint? complaint =
                    !Number(tokens[i], out double value)
                        ? new MsqComplaint(constant.Name, $"\"{tokens[i]}\" is not a number")
                        : !tune.PokeInto(pages, constant, i, value)
                            ? new MsqComplaint(constant.Name, $"{value} will not fit at cell {i}")
                            : null;

                if (complaint is null) continue;

                Restore(pages, constant, before);

                return complaint;
            }

            return null;
        }

        if (Value(constant, written) is not { } single)
            return new MsqComplaint(constant.Name, $"the firmware does not offer {written.Trim()}");

        return tune.PokeInto(pages, constant, 0, single)
            ? null
            : new MsqComplaint(constant.Name, $"{single} is outside what this setting can hold");
    }

    /// <summary>
    /// The bytes a constant occupies, or null where it does not sit in the pages
    /// at all — in which case nothing can have been written and there is nothing
    /// to put back.
    /// </summary>
    private static byte[]? Snapshot(IReadOnlyList<byte[]> pages, TuneConstant constant)
    {
        if (constant.Page < 0 || constant.Page >= pages.Count) return null;

        byte[] page = pages[constant.Page];

        if (constant.Offset < 0 || constant.Offset + constant.Size > page.Length) return null;

        return page[constant.Offset..(constant.Offset + constant.Size)];
    }

    /// <summary>Puts a snapshot back, leaving the constant as it was found.</summary>
    private static void Restore(IReadOnlyList<byte[]> pages, TuneConstant constant, byte[]? before)
    {
        if (before is null) return;

        before.CopyTo(pages[constant.Page].AsSpan(constant.Offset));
    }

    /// <summary>
    /// The number behind a written value: a label's position for a bit field,
    /// the number itself otherwise.
    /// </summary>
    private static double? Value(TuneConstant constant, string written)
    {
        string text = written.Trim();

        if (constant.HasOptions && text.StartsWith('"'))
        {
            string label = Unquote(text);

            for (int i = 0; i < constant.Options.Count; i++)
                if (constant.Options[i].Equals(label, StringComparison.OrdinalIgnoreCase)) return i;

            // Not offered. Refused rather than stored as a number, even when the
            // label happens to read as one: a firmware whose choices are spelled
            // "1", "2", "4", "6", "8" numbers them 0 to 4, and taking the label
            // at face value would set eight cylinders to mean four.
            return null;
        }

        return Number(text, out double value) ? value : null;
    }

    private static bool Number(string text, out double value) =>
        double.TryParse(
            text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }
}
