using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Editing a curve: the half of tuning beside the tables.
///
/// A MicroSquirt declares 37 of these and points 23 of its 131 menu entries at
/// one; an MS3 declares 135. What makes a curve not a table is that both rows
/// are editable — it is a line you drag, and moving a point sideways is as
/// ordinary as moving it up.
/// </summary>
public class TuneCurveEditTests
{
    private const string Ini = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 64
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageValueWrite  = "w%2o%2c%v"
           wueBins = array, U08, 0, [4], "F", 1.0, -40, -40, 215, 0
           wuePct  = array, U08, 8, [4], "%", 1.0, 0, 0, 250, 0

        [CurveEditor]
           curve = warmup, "Warmup Enrichment"
              columnLabel = "Coolant", "Enrichment"
              xBins = wueBins, coolant
              yBins = wuePct
        """;

    private static TuneLayout Layout() => TuneLayoutReader.Read(Ini);

    /// <summary>A tune holding the given breakpoints and values.</summary>
    private static EcuTune Tune(double[] x, double[] y)
    {
        TuneLayout layout = Layout();
        var tune = EcuTune.FromPages(layout, new byte[64]);

        TuneConstant bins = layout.Constants.Single(c => c.Name == "wueBins");
        TuneConstant pct = layout.Constants.Single(c => c.Name == "wuePct");

        for (int i = 0; i < x.Length; i++)
        {
            Assert.True(tune.PokeInto(tune.Pages, bins, i, x[i]));
            Assert.True(tune.PokeInto(tune.Pages, pct, i, y[i]));
        }

        return tune;
    }

    private static TuneCurveEdit Edit(EcuTune tune) =>
        TuneCurveEdit.For(TuneCurveReader.Read(Ini)["warmup"], tune)!;

    // ----- reading ----------------------------------------------------------

    [Fact]
    public void ACurveComesOutWithItsPointsItsLabelsAndItsUnits()
    {
        TuneCurveEdit edit = Edit(Tune([-40, 0, 100, 200], [180, 150, 110, 100]));

        Assert.Equal(4, edit.Count);
        Assert.Equal("Warmup Enrichment", edit.Title);
        Assert.Equal("Coolant", edit.XLabel);
        Assert.Equal("Enrichment", edit.YLabel);
        Assert.Equal("F", edit.XUnits);
        Assert.Equal("%", edit.YUnits);

        Assert.Equal(
            [new CurvePoint(-40, 180), new CurvePoint(0, 150),
             new CurvePoint(100, 110), new CurvePoint(200, 100)],
            edit.Points);
    }

    [Fact]
    public void ACurveWhoseRowsDisagreeIsRefusedRatherThanHalfDrawn()
    {
        // A line whose points are in the wrong places is not a near miss.
        const string ragged = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 64
               bins = array, U08, 0, [4], "F", 1.0, 0, 0, 250, 0
               pct  = array, U08, 8, [6], "%", 1.0, 0, 0, 250, 0

            [CurveEditor]
               curve = odd, "Odd"
                  xBins = bins
                  yBins = pct
            """;

        TuneLayout layout = TuneLayoutReader.Read(ragged);

        Assert.Null(TuneCurveEdit.For(
            TuneCurveReader.Read(ragged)["odd"], EcuTune.FromPages(layout, new byte[64])));
    }

    [Fact]
    public void ACurveNamingConstantsThisFirmwareLacksIsRefused()
    {
        var curve = new TuneCurve("ghost", "Ghost", "noSuchX", "noSuchY", "", "");

        Assert.Null(TuneCurveEdit.For(curve, EcuTune.FromPages(Layout(), new byte[64])));
    }

    // ----- editing ----------------------------------------------------------

    [Fact]
    public void MovingAPointUpIsRecordedAgainstWhatTheEcuHolds()
    {
        TuneCurveEdit edit = Edit(Tune([-40, 0, 100, 200], [180, 150, 110, 100]));

        edit.SetY(1, 160);

        Assert.True(edit.HasChanges);
        Assert.Equal(1, edit.ChangedCount);
        Assert.True(edit.IsChanged(1));
        Assert.False(edit.IsChanged(0));
        Assert.Equal(150, edit.OriginalY(1));
    }

    [Fact]
    public void AValuePastWhatTheFirmwareAllowsStopsAtTheEdge()
    {
        // Clamped rather than refused, because this is a line being dragged: a
        // pointer past the top of the plot should stop at the top.
        TuneCurveEdit edit = Edit(Tune([-40, 0, 100, 200], [180, 150, 110, 100]));

        edit.SetY(0, 9999);

        Assert.Equal(250, edit.Y(0));
    }

    [Fact]
    public void ABreakpointCannotOvertakeItsNeighbours()
    {
        // The ECU interpolates between consecutive entries and takes the row as
        // ascending. One that overtakes makes the lookup read backwards over
        // that span — not an error anywhere, just a curve that does the opposite
        // of what it shows for one stretch.
        TuneCurveEdit edit = Edit(Tune([-40, 0, 100, 200], [180, 150, 110, 100]));

        edit.SetX(1, 150);
        Assert.Equal(100, edit.X(1));

        edit.SetX(1, -200);
        Assert.Equal(-40, edit.X(1));
    }

    [Fact]
    public void TheEndsAreFreeToMoveWithinTheFirmwaresRange()
    {
        TuneCurveEdit edit = Edit(Tune([-40, 0, 100, 200], [180, 150, 110, 100]));

        edit.SetX(3, 215);
        Assert.Equal(215, edit.X(3));

        edit.SetX(3, 9999);
        Assert.Equal(215, edit.X(3));
    }

    [Fact]
    public void AStraightLineIsDrawnAlongTheBreakpointsNotAlongThePositions()
    {
        // Breakpoints are spread unevenly — a warmup curve is dense where it is
        // cold — so interpolating by position would bend the line.
        TuneCurveEdit edit = Edit(Tune([0, 25, 75, 100], [100, 0, 0, 200]));

        Assert.True(edit.Interpolate(0, 3));

        Assert.Equal(125, edit.Y(1), 6);   // a quarter of the way along
        Assert.Equal(175, edit.Y(2), 6);   // three quarters
    }

    [Fact]
    public void InterpolatingNeedsSomethingInBetween()
    {
        TuneCurveEdit edit = Edit(Tune([-40, 0, 100, 200], [180, 150, 110, 100]));

        Assert.False(edit.Interpolate(1, 2));
        Assert.False(edit.Interpolate(2, 2));
        Assert.False(edit.Interpolate(-1, 3));
    }

    [Fact]
    public void RevertingPutsBothRowsBack()
    {
        TuneCurveEdit edit = Edit(Tune([-40, 0, 100, 200], [180, 150, 110, 100]));

        edit.SetY(1, 160);
        edit.SetX(1, 50);
        edit.Revert(1);

        Assert.False(edit.HasChanges);
        Assert.Equal(0, edit.X(1));
        Assert.Equal(150, edit.Y(1));
    }

    // ----- sending ----------------------------------------------------------

    [Fact]
    public void OnlyTheRowThatMovedIsWritten()
    {
        EcuTune tune = Tune([-40, 0, 100, 200], [180, 150, 110, 100]);
        TuneCurveEdit edit = Edit(tune);

        edit.SetY(1, 160);

        TuneWrite write = Assert.Single(edit.Encode(tune)!);
        Assert.Equal(8, write.Offset);          // the values, not the breakpoints
    }

    [Fact]
    public void MovingAPointSidewaysWritesTheBreakpointsToo()
    {
        // Sending values without the breakpoints they were drawn against leaves
        // the ECU interpolating new numbers over the old axis — a different
        // curve from the one on screen, and nothing would say so.
        EcuTune tune = Tune([-40, 0, 100, 200], [180, 150, 110, 100]);
        TuneCurveEdit edit = Edit(tune);

        edit.SetX(1, 50);
        edit.SetY(1, 160);

        IReadOnlyList<TuneWrite> writes = edit.Encode(tune)!;

        Assert.Equal(2, writes.Count);
        Assert.Contains(writes, w => w.Offset == 0);
        Assert.Contains(writes, w => w.Offset == 8);
    }

    [Fact]
    public void AnUntouchedCurveWritesNothing()
    {
        EcuTune tune = Tune([-40, 0, 100, 200], [180, 150, 110, 100]);

        Assert.Empty(Edit(tune).Encode(tune)!);
    }

    [Fact]
    public void WhatIsSentIsWhatComesBack()
    {
        TuneLayout layout = Layout();
        EcuTune tune = Tune([-40, 0, 100, 200], [180, 150, 110, 100]);
        TuneCurveEdit edit = Edit(tune);

        edit.SetY(2, 120);
        edit.SetX(2, 90);

        var copy = EcuTune.FromPages(layout, [.. tune.Pages.Select(p => p.ToArray())]);
        foreach (TuneWrite write in edit.Encode(tune)!) copy.Accept(write);

        Assert.Equal([-40, 0, 90, 200], copy.Array("wueBins"));
        Assert.Equal([180, 150, 120, 100], copy.Array("wuePct"));
    }
}
