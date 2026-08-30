using System.Text.RegularExpressions;

namespace OpenLogViewer.Core;

/// <summary>
/// A table the firmware offers for editing: its title, the constants holding its
/// values and breakpoints, and the live channels its axes are indexed by.
///
/// That last part is the valuable one. <c>xBins = veRpmBins, RPMValue</c> says
/// the VE table's columns are the <c>veRpmBins</c> breakpoints and that the ECU
/// looks the table up by <c>RPMValue</c> — so binning a log against that channel
/// reproduces exactly what the controller did, rather than something close to it
/// chosen by whoever set up the analysis.
/// </summary>
public sealed record TableDefinition
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>
    /// The name of the same table's three-dimensional view, where it declares
    /// one. A menu may point at either — "VE Table" opens the grid and "VE Table
    /// 3D" the surface — so both names have to lead back to this.
    /// </summary>
    public string Map { get; init; } = "";

    /// <summary>Constant holding the values.</summary>
    public required string Values { get; init; }

    /// <summary>Constant holding the column breakpoints.</summary>
    public required string XBins { get; init; }

    /// <summary>Constant holding the row breakpoints.</summary>
    public required string YBins { get; init; }

    /// <summary>Output channel the ECU indexes the columns by, where the INI says.</summary>
    public string XChannel { get; init; } = "";

    public string YChannel { get; init; } = "";

    /// <summary>True for the fuel table, which is the one VE Calibration is about.</summary>
    public bool LooksLikeVeTable =>
        Values.Contains("veTable", StringComparison.OrdinalIgnoreCase)
        || Title.Contains("VE Table", StringComparison.OrdinalIgnoreCase)
        || Title.Contains("VE table", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads the <c>[TableEditor]</c> section.</summary>
public static class TableEditorReader
{
    // The closing quote of the title is left off the pattern: the title group
    // already stops at it, and a raw string literal cannot end with one.
    private static readonly Regex Table = new(
        """^\s*table\s*=\s*(?<id>[A-Za-z_]\w*)\s*,\s*(?<map>[A-Za-z_]\w*)?\s*,\s*"(?<title>[^"]*)""",
        RegexOptions.Compiled);

    private static readonly Regex Bins = new(
        """^\s*(?<axis>[xyz])Bins\s*=\s*(?<constant>[A-Za-z_]\w*)\s*(?:,\s*(?<channel>[A-Za-z_]\w*))?""",
        RegexOptions.Compiled);

    public static IReadOnlyList<TableDefinition> Read(string iniText, IReadOnlySet<string>? symbols = null)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        var tables = new List<TableDefinition>();

        string id = "";
        string map = "";
        string title = "";
        string values = "";
        string xBins = "";
        string yBins = "";
        string xChannel = "";
        string yChannel = "";

        foreach (string raw in MsqIni.Section(iniText, "TableEditor", symbols ?? MsqIni.DefaultSymbols))
        {
            string line = MsqIni.Strip(raw);
            if (line.Length == 0) continue;

            if (Table.Match(line) is { Success: true } start)
            {
                Flush();

                id = start.Groups["id"].Value;
                map = start.Groups["map"].Value;
                title = start.Groups["title"].Value.Trim();
                values = xBins = yBins = xChannel = yChannel = "";
                continue;
            }

            if (Bins.Match(line) is not { Success: true } bins) continue;

            string constant = bins.Groups["constant"].Value;
            string channel = bins.Groups["channel"].Success ? bins.Groups["channel"].Value : "";

            switch (bins.Groups["axis"].Value)
            {
                case "x": xBins = constant; xChannel = channel; break;
                case "y": yBins = constant; yChannel = channel; break;
                case "z": values = constant; break;
            }
        }

        Flush();

        return tables;

        // A table is only complete once the next one starts or the section ends,
        // since its parts are listed under it rather than on its own line.
        void Flush()
        {
            if (id.Length == 0 || values.Length == 0 || xBins.Length == 0 || yBins.Length == 0) return;

            tables.Add(new TableDefinition
            {
                Id = id,
                Title = title.Length > 0 ? title : id,
                Values = values,
                XBins = xBins,
                YBins = yBins,
                Map = map,
                XChannel = xChannel,
                YChannel = yChannel,
            });
        }
    }
}
