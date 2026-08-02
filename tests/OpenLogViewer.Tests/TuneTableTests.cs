using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class TuneTableTests
{
    /// <summary>
    /// A tune shaped like the real thing: a default namespace, one-column
    /// constants for the axes, and a rectangular one for the table.
    /// </summary>
    private static string Msq(string xAxis, string yAxis, string grid, int cols = 3, int rows = 3) => $"""
        <?xml version="1.0" encoding="ISO-8859-1"?>
        <msq xmlns="http://www.msefi.com/:msq">
        <page number="0" size="1024">
        <constant cols="1" digits="0" name="frpm_table1" rows="{cols}" units="RPM">{xAxis}</constant>
        <constant cols="1" digits="0" name="fmap_table1" rows="{rows}" units="kPa">{yAxis}</constant>
        <constant cols="{cols}" digits="1" name="veTable1" rows="{rows}" units="%">{grid}</constant>
        </page>
        </msq>
        """;

    private static TuneTable? Ve(string msq) =>
        MsqTune.ReadTables(msq).FirstOrDefault(t => t.Name == "VE table 1");

    [Fact]
    public void TheGridAndItsAxesAreReadTogether()
    {
        TuneTable table = Ve(Msq(
            "1000 2000 3000",
            "40 60 80",
            """
            10 11 12
            20 21 22
            30 31 32
            """))!;

        Assert.Equal([1000, 2000, 3000], table.X.Breakpoints);
        Assert.Equal([40, 60, 80], table.Y.Breakpoints);
        Assert.Equal(3, table.Columns);
        Assert.Equal(3, table.Rows);
        Assert.Equal("%", table.Units);
    }

    [Fact]
    public void RowZeroIsTheLowestLoad()
    {
        // The file stores rows lowest-first, matching the ascending axis, even
        // though a tuning app draws the table the other way up. Getting this
        // backwards would suggest changes to the mirror image of the right cells.
        TuneTable table = Ve(Msq(
            "1000 2000 3000",
            "40 60 80",
            """
            10 11 12
            20 21 22
            30 31 32
            """))!;

        Assert.Equal(10, table.Values[0, 0]);    // lowest RPM, lowest load
        Assert.Equal(12, table.Values[2, 0]);    // highest RPM, lowest load
        Assert.Equal(30, table.Values[0, 2]);    // lowest RPM, highest load
        Assert.Equal(32, table.Values[2, 2]);
    }

    [Fact]
    public void ATableWhoseGridDoesNotMatchItsAxesIsRefused()
    {
        // A mismatch means one of the two was misread. A suggestion built on a
        // misaligned grid would be confidently wrong about which cell to change.
        Assert.Null(Ve(Msq("1000 2000 3000", "40 60 80", "1 2 3 4 5 6", cols: 3, rows: 2)));
    }

    [Fact]
    public void AGridWithTheWrongNumberOfValuesIsRefused() =>
        Assert.Null(Ve(Msq("1000 2000 3000", "40 60 80", "1 2 3 4 5")));

    [Fact]
    public void AGridWithAnUnreadableValueIsRefused() =>
        Assert.Null(Ve(Msq("1000 2000 3000", "40 60 80", "1 2 3 4 x 6 7 8 9")));

    [Fact]
    public void ATableWithNoAxesIsNotReturned()
    {
        // Axes that fail the ascending check leave the grid with nothing to
        // line up against.
        Assert.Null(Ve(Msq("3000 2000 1000", "40 60 80", "1 2 3 4 5 6 7 8 9")));
    }

    [Fact]
    public void MalformedXmlIsNoTablesRatherThanAThrow() =>
        Assert.Empty(MsqTune.ReadTables("<msq><not closed"));

    [Fact]
    public void AbsentTuneIsNoTables()
    {
        Assert.Empty(MsqTune.ReadTables(null));
        Assert.Empty(MsqTune.ReadTables(""));
    }

    [Fact]
    public void TheLabelNamesTheGridSize()
    {
        TuneTable table = Ve(Msq("1000 2000 3000", "40 60 80", "1 2 3 4 5 6 7 8 9"))!;

        Assert.Equal("VE table 1  (3×3)", table.Label);
    }

    [Fact]
    public void ReadingTablesDoesNotDisturbReadingAxes()
    {
        // Both are offered from the same tune; the axis list must still contain
        // tables whose values could not be read.
        string msq = Msq("1000 2000 3000", "40 60 80", "1 2 3 4 5");

        Assert.Empty(MsqTune.ReadTables(msq));
        Assert.Contains(MsqTune.ReadAxisSets(msq), s => s.Name == "VE table 1");
    }
}
