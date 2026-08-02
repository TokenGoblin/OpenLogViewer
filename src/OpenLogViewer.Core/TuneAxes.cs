using System.Globalization;
using System.Xml.Linq;

namespace OpenLogViewer.Core;

/// <summary>One axis of an ECU table: the breakpoints the ECU interpolates between.</summary>
public sealed record TuneAxis(string Constant, string Units, double[] Breakpoints);

/// <summary>
/// The breakpoints of one table in the tune, so a log can be binned onto the
/// same grid the ECU actually uses.
/// </summary>
public sealed record TuneAxisSet(string Name, TuneAxis X, TuneAxis Y)
{
    /// <summary>Name and grid size, e.g. "VE table 1  (16×16)".</summary>
    public string Label => $"{Name}  ({X.Breakpoints.Length}×{Y.Breakpoints.Length})";
}

/// <summary>
/// A table from the tune, values and all: what the ECU was actually running,
/// against which a log can be judged.
/// </summary>
public sealed record TuneTable(string Name, TuneAxis X, TuneAxis Y, double[,] Values, string Units)
{
    public int Columns => X.Breakpoints.Length;

    public int Rows => Y.Breakpoints.Length;

    public string Label => $"{Name}  ({Columns}×{Rows})";

    public TuneAxisSet Axes => new(Name, X, Y);
}

/// <summary>
/// Reads table axes out of an MSQ tune.
///
/// The axes are far more useful than uniform bins: a table binned onto the ECU's
/// own breakpoints can be read cell-for-cell against the table being tuned,
/// which uniform bins spanning the observed range never line up with.
/// </summary>
public static class MsqTune
{
    /// <summary>
    /// Tables worth offering, and the constants holding their axes. MegaSquirt
    /// firmwares name these consistently: "f" for fuel, "s" for spark, "a" for
    /// the AFR target table.
    /// </summary>
    private static readonly (string Name, string X, string Y, string? Values)[] KnownTables =
    [
        ("VE table 1", "frpm_table1", "fmap_table1", "veTable1"),
        ("VE table 2", "frpm_table2", "fmap_table2", "veTable2"),
        ("VE table 3", "frpm_table3", "fmap_table3", "veTable3"),
        ("VE table 4", "frpm_table4", "fmap_table4", "veTable4"),
        ("Spark table 1", "srpm_table1", "smap_table1", "advanceTable1"),
        ("Spark table 2", "srpm_table2", "smap_table2", "advanceTable2"),
        ("Spark table 3", "srpm_table3", "smap_table3", "advanceTable3"),
        ("Spark table 4", "srpm_table4", "smap_table4", "advanceTable4"),
        ("AFR target 1", "arpm_table1", "amap_table1", "afrTable1"),
        ("AFR target 2", "arpm_table2", "amap_table2", "afrTable2"),
        ("Boost target", "boost_ctl_loadtarg_rpm_bins", "boost_ctl_pwmtarg_rpm_bins", null),
    ];

    /// <summary>
    /// Every usable axis pair in the tune. A pair is skipped when either axis is
    /// missing or its breakpoints are not usable.
    /// </summary>
    public static IReadOnlyList<TuneAxisSet> ReadAxisSets(string? msqXml)
    {
        Dictionary<string, TuneAxis>? constants = ReadConstants(msqXml);
        if (constants is null) return [];

        var sets = new List<TuneAxisSet>();
        foreach ((string name, string x, string y, _) in KnownTables)
        {
            if (!constants.TryGetValue(x, out TuneAxis? xAxis)) continue;
            if (!constants.TryGetValue(y, out TuneAxis? yAxis)) continue;

            sets.Add(new TuneAxisSet(name, xAxis, yAxis));
        }

        return sets;
    }

    /// <summary>
    /// Tables whose values were readable as well as their axes — everything
    /// needed to compare a log against what the ECU was actually running.
    ///
    /// A table is only returned when its grid matches its axes. A mismatch means
    /// one of the two was misread, and a suggestion built on a misaligned grid
    /// would be confidently wrong about which cell needs changing.
    /// </summary>
    public static IReadOnlyList<TuneTable> ReadTables(string? msqXml)
    {
        if (string.IsNullOrWhiteSpace(msqXml)) return [];

        XDocument document;
        try
        {
            document = XDocument.Parse(msqXml);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        Dictionary<string, XElement> elements = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in document.Descendants().Where(e => e.Name.LocalName == "constant"))
        {
            string? name = element.Attribute("name")?.Value;
            if (name is { Length: > 0 }) elements[name] = element;
        }

        Dictionary<string, TuneAxis> axes = ReadConstants(elements);

        var tables = new List<TuneTable>();
        foreach ((string name, string x, string y, string? values) in KnownTables)
        {
            if (values is null) continue;
            if (!axes.TryGetValue(x, out TuneAxis? xAxis)) continue;
            if (!axes.TryGetValue(y, out TuneAxis? yAxis)) continue;
            if (!elements.TryGetValue(values, out XElement? grid)) continue;

            double[,]? cells = ParseGrid(grid, xAxis.Breakpoints.Length, yAxis.Breakpoints.Length);
            if (cells is null) continue;

            tables.Add(new TuneTable(
                name, xAxis, yAxis, cells, grid.Attribute("units")?.Value ?? ""));
        }

        return tables;
    }

    /// <summary>
    /// Reads a rectangular constant into [column, row], row 0 lowest.
    ///
    /// The rows are stored lowest-load first, matching the ascending axis, even
    /// though a tuning app shows the table the other way up.
    /// </summary>
    private static double[,]? ParseGrid(XElement element, int columns, int rows)
    {
        if (!int.TryParse(element.Attribute("cols")?.Value, out int declaredColumns)) return null;
        if (!int.TryParse(element.Attribute("rows")?.Value, out int declaredRows)) return null;

        string[] tokens = element.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != declaredColumns * declaredRows) return null;

        // Axes are collapsed and validated before this point, so a grid that no
        // longer matches them cannot be lined up cell for cell.
        if (declaredColumns != columns || declaredRows != rows) return null;

        var cells = new double[columns, rows];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
        {
            if (!double.TryParse(tokens[r * columns + c], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return null;

            cells[c, r] = v;
        }

        return cells;
    }

    /// <summary>
    /// The tune's single-value settings, by name.
    ///
    /// Needed to read a live connection: the firmware's derived channels are
    /// written in terms of the tune as well as the realtime block — duty cycle
    /// divides by <c>nCylinders</c>, injector timing tests <c>twoStroke</c> —
    /// and none of those arrive over the wire.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ReadScalars(string? msqXml)
    {
        var scalars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(msqXml)) return scalars;

        XDocument document;
        try
        {
            document = XDocument.Parse(msqXml);
        }
        catch (System.Xml.XmlException)
        {
            return scalars;
        }

        foreach (XElement element in document.Descendants().Where(e => e.Name.LocalName == "constant"))
        {
            string? name = element.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Tables and arrays declare their shape; a setting is a lone value.
            if (element.Attribute("cols") is not null || element.Attribute("rows") is not null) continue;

            string text = element.Value.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                scalars[name] = value;
        }

        return scalars;
    }

    /// <summary>Parses the one-dimensional numeric constants that can serve as axes.</summary>
    private static Dictionary<string, TuneAxis>? ReadConstants(string? msqXml)
    {
        if (string.IsNullOrWhiteSpace(msqXml)) return null;

        XDocument document;
        try
        {
            document = XDocument.Parse(msqXml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        // The document carries a default namespace, so elements are matched on
        // local name rather than by a qualified lookup.
        Dictionary<string, XElement> elements = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement element in document.Descendants().Where(e => e.Name.LocalName == "constant"))
        {
            string? name = element.Attribute("name")?.Value;
            if (name is { Length: > 0 }) elements[name] = element;
        }

        return ReadConstants(elements);
    }

    private static Dictionary<string, TuneAxis> ReadConstants(Dictionary<string, XElement> elements)
    {
        var constants = new Dictionary<string, TuneAxis>(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, XElement element) in elements)
        {
            // Axes are single-column arrays; tables and scalars are not axes.
            if (element.Attribute("cols")?.Value != "1") continue;

            double[]? breakpoints = ParseBreakpoints(element.Value);
            if (breakpoints is null) continue;

            constants[name] = new TuneAxis(name, element.Attribute("units")?.Value ?? "", breakpoints);
        }

        return constants;
    }

    /// <summary>
    /// Turns an axis constant's text into usable breakpoints, or null if it is
    /// not a usable axis.
    ///
    /// Two quirks have to be handled. Firmwares pad an axis out to the table's
    /// full size by repeating the top value, which would create zero-width bins;
    /// consecutive duplicates are collapsed. And the "dozen" variants are stored
    /// rolled rather than in order (5200, 5700, 6100, 6500, 502, 801 …), so
    /// anything that is not ascending after collapsing is rejected outright.
    /// </summary>
    internal static double[]? ParseBreakpoints(string text)
    {
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3) return null;

        var values = new List<double>(tokens.Length);
        foreach (string token in tokens)
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return null;

            if (values.Count > 0 && Math.Abs(v - values[^1]) < 1e-9) continue;
            values.Add(v);
        }

        if (values.Count < 3) return null;

        for (int i = 1; i < values.Count; i++)
            if (values[i] <= values[i - 1])
                return null;

        return [.. values];
    }
}
