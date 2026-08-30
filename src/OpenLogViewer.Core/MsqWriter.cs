using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// Writes a tune out as an <c>.msq</c>.
///
/// <para>
/// The other half of reading one, and the thing that makes a controller's
/// settings yours rather than the controller's: a tune that exists only in an
/// ECU is one power supply away from being gone. It is also the format every
/// other tool in this world reads, so a tune saved here opens in TunerStudio and
/// can be sent to whoever is helping.
/// </para>
/// <para>
/// Values are written the way a person reads them, not as bytes, because that is
/// what the format is for — a file full of raw pages would say nothing without
/// the definition beside it, and the whole point of an MSQ is that a human and
/// another program can both make sense of it. A bit field is written as the name
/// of the option chosen, and a number with as many decimals as it takes to get
/// the same byte back — which is not the same as the decimals the firmware shows.
/// </para>
/// <para>
/// The conditional symbols go in too. A tune written without them is a tune that
/// cannot be read back correctly: the reader would have to guess which build it
/// came from, and guessing wrong scales every temperature in it.
/// </para>
/// </summary>
public static class MsqWriter
{
    /// <summary>What the format calls itself. Written as TunerStudio writes it.</summary>
    private const string FileFormat = "5.0";

    /// <summary>
    /// Turns a tune into the text of an MSQ.
    /// </summary>
    /// <param name="tune">The settings to write.</param>
    /// <param name="signature">
    /// The firmware signature, which is what tells a reader which definition this
    /// belongs to. Without it the file is a list of numbers nobody can place.
    /// </param>
    /// <param name="symbols">The build's conditional symbols.</param>
    /// <param name="comment">Whatever the tuner wants to say about it.</param>
    /// <param name="source">
    /// A file this was read from, if there was one. Its PC variables and its
    /// firmware string are carried across: they belong to the tune and are not
    /// in the controller, so they would otherwise be lost by the round trip
    /// through an ECU.
    /// </param>
    public static string Write(
        EcuTune tune,
        string signature,
        IReadOnlySet<string>? symbols = null,
        string comment = "",
        MsqFile? source = null)
    {
        ArgumentNullException.ThrowIfNull(tune);

        TuneLayout layout = tune.Layout;
        var text = new StringBuilder(1 << 20);

        text.Append("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?>\n");
        text.Append("<msq xmlns=\"http://www.msefi.com/:msq\">\n");

        text.Append("<bibliography author=\"").Append(Attribute(Author))
            .Append("\" tuneComment=\"").Append(Attribute(comment))
            .Append("\" writeDate=\"").Append(Attribute(Stamp()))
            .Append("\"/>\n");

        text.Append("<versionInfo fileFormat=\"").Append(FileFormat)
            .Append("\" firmwareInfo=\"").Append(Attribute(source?.Firmware ?? ""))
            .Append("\" nPages=\"").Append(layout.Pages.Count.ToString(CultureInfo.InvariantCulture))
            .Append("\" signature=\"").Append(Attribute(signature ?? ""))
            .Append("\"/>\n");

        // The settings that live on this machine rather than in the ECU. Written
        // first, in a page with no number, exactly as the format has them.
        if (source is { PcVariables.Count: > 0 })
        {
            text.Append("<page>\n");

            foreach ((string name, string value) in source.PcVariables)
                text.Append("<pcVariable name=\"").Append(Attribute(name)).Append("\">")
                    .Append(Escape(value)).Append("</pcVariable>\n");

            text.Append("</page>\n");
        }

        // Where a name is declared twice the later one wins, which is how it
        // resolves everywhere else. Spelled exactly: two settings differing only
        // in case are two settings, and MS2Extra really has a pair.
        var winners = layout.Constants
            .Where(c => c.OnController)
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (TunePage page in layout.Pages)
        {
            text.Append("<page number=\"").Append(page.Index.ToString(CultureInfo.InvariantCulture))
                .Append("\" size=\"").Append(page.Size.ToString(CultureInfo.InvariantCulture))
                .Append("\">\n");

            foreach (TuneConstant constant in layout.Constants)
            {
                if (constant.Page != page.Index) continue;
                if (!ReferenceEquals(winners.GetValueOrDefault(constant.Name), constant)) continue;

                Constant(text, tune, constant);
            }

            text.Append("</page>\n");
        }

        if (symbols is { Count: > 0 })
        {
            text.Append("<settings>\n");

            foreach (string symbol in symbols.OrderBy(s => s, StringComparer.Ordinal))
                text.Append("<setting name=\"").Append(Attribute(symbol))
                    .Append("\" value=\"").Append(Attribute(symbol)).Append("\"/>\n");

            text.Append("</settings>\n");
        }

        text.Append("</msq>\n");

        return text.ToString();
    }

    /// <summary>
    /// Saves it, in the encoding the tuning world writes these in.
    ///
    /// ISO-8859-1, which is what the declaration at the top says and what every
    /// other tool expects. A tune whose text will not fit in it — which would
    /// take a name in a script this application does not otherwise handle — is
    /// written as UTF-8 and says so, because a file that is honest about its
    /// encoding beats one that quietly replaces the characters it cannot spell.
    /// </summary>
    public static void Save(
        string path,
        EcuTune tune,
        string signature,
        IReadOnlySet<string>? symbols = null,
        string comment = "",
        MsqFile? source = null)
    {
        string xml = Write(tune, signature, symbols, comment, source);

        if (xml.All(c => c <= 0xFF))
        {
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(xml));
            return;
        }

        File.WriteAllBytes(
            path,
            new UTF8Encoding(false).GetBytes(
                xml.Replace("encoding=\"ISO-8859-1\"", "encoding=\"UTF-8\"", StringComparison.Ordinal)));
    }

    /// <summary>One constant, written the way a person reads it.</summary>
    private static void Constant(StringBuilder text, EcuTune tune, TuneConstant constant)
    {
        text.Append("<constant");

        if (constant.IsText)
        {
            text.Append(" name=\"").Append(Attribute(constant.Name)).Append("\">\"")
                .Append(Escape(tune.TextIn(tune.Pages, constant.Name) ?? ""))
                .Append("\"</constant>\n");

            return;
        }

        int columns = Math.Max(1, constant.Columns);
        int rows = Math.Max(1, constant.Rows);
        int cells = columns * rows;

        if (cells > 1)
            text.Append(" cols=\"").Append(columns.ToString(CultureInfo.InvariantCulture))
                .Append("\" digits=\"").Append(constant.Digits.ToString(CultureInfo.InvariantCulture))
                .Append('"');
        else if (!constant.HasOptions)
            text.Append(" digits=\"").Append(constant.Digits.ToString(CultureInfo.InvariantCulture))
                .Append('"');

        text.Append(" name=\"").Append(Attribute(constant.Name)).Append('"');

        if (cells > 1)
            text.Append(" rows=\"").Append(rows.ToString(CultureInfo.InvariantCulture)).Append('"');

        if (constant.Units.Length > 0)
            text.Append(" units=\"").Append(Attribute(constant.Units)).Append('"');

        text.Append('>');

        if (cells > 1)
        {
            // Row-major and laid out a row to a line, which is how the format
            // writes a grid and how a person reads one.
            text.Append('\n');

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                    text.Append(' ')
                        .Append(Number(tune.ValueIn(tune.Pages, constant.Name, (row * columns) + column),
                                       constant))
                        .Append(' ');

                text.Append('\n');
            }
        }
        else
        {
            text.Append(Single(tune, constant));
        }

        text.Append("</constant>\n");
    }

    /// <summary>
    /// A lone value: the name of the option for a bit field, else the number.
    ///
    /// <para>
    /// A label is only written when it says which value it is. Firmwares pad an
    /// option list out to the width of the field, so the same word appears many
    /// times over: Speeduino's pin list has "INVALID" at 1, 2, 54 and 63, and an
    /// unconfigured pin really does read 63. Writing the word and reading it
    /// back finds the first one, so a backup of a pin set to "none" restores as
    /// pin 1 — a real output, quietly assigned. The number cannot do that.
    /// </para>
    /// <para>
    /// The same for a value the firmware names not at all, which is a real thing
    /// to find in an ECU: a setting an older revision wrote, or one left blank.
    /// </para>
    /// </summary>
    private static string Single(EcuTune tune, TuneConstant constant)
    {
        double? value = tune.ValueIn(tune.Pages, constant.Name);

        if (constant.HasOptions && value is { } chosen)
        {
            int index = (int)Math.Round(chosen);

            if (index >= 0 && index < constant.Options.Count && Names(constant, index))
                return $"\"{Escape(constant.Options[index])}\"";
        }

        return Number(value, constant);
    }

    /// <summary>
    /// Whether this option's label points back at this option and no earlier one.
    ///
    /// Matched the way a reader matches it — ignoring case, first one wins — so
    /// that the question asked is the one that decides the round trip.
    /// </summary>
    private static bool Names(TuneConstant constant, int index)
    {
        string label = constant.Options[index];
        if (label.Length == 0) return false;

        for (int i = 0; i < index; i++)
            if (constant.Options[i].Equals(label, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>
    /// A number written with enough decimals to get the same byte back.
    ///
    /// <para>
    /// <b>Not the firmware's <c>digits</c>.</b> That says how many decimals to
    /// show a person, and it is routinely fewer than the storage needs: MS2
    /// keeps a dwell in sixty-sixths of a millisecond and asks for one decimal,
    /// so a raw 56 is 3.7296 ms shown as "3.7" — and 3.7 read back is a raw 55.
    /// Writing what is displayed rather than what is stored quietly moves
    /// settings by one step every time a tune goes through a file, which is
    /// exactly the sort of drift nobody attributes to the tool. TunerStudio's
    /// own files show the same: <c>digits="1"</c> beside a value of 3.7296.
    /// </para>
    /// <para>
    /// So the shortest form that reconstructs the stored number is used, found
    /// by trying. Most settings need one decimal and get one; the odd scale gets
    /// however many it takes.
    /// </para>
    /// </summary>
    private static string Number(double? value, TuneConstant constant)
    {
        if (value is not { } v || !double.IsFinite(v)) return "0.0";

        int wanted = Math.Clamp(Math.Max(constant.Digits, 1), 1, 9);

        for (int digits = wanted; digits <= 9; digits++)
        {
            string written = v.ToString("F" + digits, CultureInfo.InvariantCulture);

            if (double.TryParse(written, NumberStyles.Float, CultureInfo.InvariantCulture, out double back)
                && Stored(back, constant) == Stored(v, constant))
            {
                return written;
            }
        }

        // Nothing short enough reconstructs it, so write the number itself and
        // let the reader round as it must. Reached only for a scale so fine that
        // nine decimals will not separate two neighbouring bytes.
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The number that would actually be stored for a displayed value — the same
    /// arithmetic a write to the controller does, so that "does this round trip"
    /// is asked of the bytes rather than of the text.
    /// </summary>
    private static double Stored(double value, TuneConstant constant)
    {
        double scaled = constant.Scale != 0 ? (value / constant.Scale) - constant.Transform : value;

        return constant.Type == RealtimeType.F32 ? scaled : Math.Round(scaled);
    }

    private static string Stamp() =>
        DateTime.Now.ToString("ddd MMM dd HH:mm:ss ", CultureInfo.InvariantCulture)
        + TimeZoneInfo.Local.StandardName
        + DateTime.Now.ToString(" yyyy", CultureInfo.InvariantCulture);

    private static string Author => $"OpenLogViewer - {Version}";

    private static string Version =>
        typeof(MsqWriter).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Attribute(string text) =>
        Escape(text).Replace("\"", "&quot;", StringComparison.Ordinal);
}
