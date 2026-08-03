using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Changing a table before any of it is sent.
///
/// The whole point of keeping this separate from the connection is that it can
/// be checked without an engine attached — and it is the half where a mistake
/// is silent, since a wrong number encodes to a perfectly valid byte.
/// </summary>
public class TuneEditTests
{
    /// <summary>A 4×3 fuel table, stored as bytes, 0 to 255 per the firmware.</summary>
    private static TuneEdit Fuel()
    {
        var values = new double[4, 3];
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 3; row++)
                values[column, row] = 50 + (column * 10) + row;

        var table = new TuneTable(
            "VE Table",
            new TuneAxis("rpmBins", "rpm", [800, 2000, 4000, 6000]),
            new TuneAxis("mapBins", "kPa", [30, 60, 100]),
            values,
            "%");

        var constant = new TuneConstant
        {
            Name = "veTable",
            Page = 0,
            Offset = 0,
            Type = RealtimeType.U08,
            Columns = 4,
            Rows = 3,
            Scale = 1,
            Low = 0,
            High = 255,
            Digits = 0,
        };

        return new TuneEdit(table, constant);
    }

    // ----- what was read stays available ------------------------------------

    [Fact]
    public void NothingIsChangedToBeginWith()
    {
        TuneEdit edit = Fuel();

        Assert.False(edit.HasChanges);
        Assert.Equal(0, edit.ChangedCount);
    }

    [Fact]
    public void WhatTheEcuSaidIsStillThereAfterAnEdit()
    {
        // A tuner who cannot see what they changed cannot decide whether to send
        // it, so the original has to survive alongside the edit.
        TuneEdit edit = Fuel();
        double before = edit[1, 1];

        edit.Set(TuneSelection.Cell(1, 1), 99);

        Assert.Equal(99, edit[1, 1]);
        Assert.Equal(before, edit.Original(1, 1));
        Assert.True(edit.IsChanged(1, 1));
        Assert.False(edit.IsChanged(0, 0));
        Assert.Equal(1, edit.ChangedCount);
    }

    [Fact]
    public void RevertingPutsEverythingBack()
    {
        TuneEdit edit = Fuel();
        edit.Scale(new TuneSelection(0, 0, 3, 2), 25);

        Assert.True(edit.HasChanges);

        edit.Revert();

        Assert.False(edit.HasChanges);
        Assert.Equal(edit.Original(2, 1), edit[2, 1]);
    }

    [Fact]
    public void PartOfATableCanBePutBackWithoutLosingTheRest()
    {
        TuneEdit edit = Fuel();
        edit.Set(TuneSelection.Cell(0, 0), 10);
        edit.Set(TuneSelection.Cell(3, 2), 20);

        edit.Revert(TuneSelection.Cell(0, 0));

        Assert.False(edit.IsChanged(0, 0));
        Assert.True(edit.IsChanged(3, 2));
    }

    // ----- the operations ----------------------------------------------------

    [Fact]
    public void ARectangleIsEditedTogether()
    {
        TuneEdit edit = Fuel();

        edit.Set(new TuneSelection(1, 0, 2, 1), 77);

        Assert.Equal(77, edit[1, 0]);
        Assert.Equal(77, edit[2, 1]);
        Assert.False(edit.IsChanged(0, 0));
        Assert.False(edit.IsChanged(3, 2));
        Assert.Equal(4, edit.ChangedCount);
    }

    [Fact]
    public void ASelectionDraggedBackwardsCoversTheSameCells()
    {
        // Either corner may be the one touched first.
        TuneEdit forwards = Fuel();
        TuneEdit backwards = Fuel();

        forwards.Set(new TuneSelection(1, 0, 2, 1), 77);
        backwards.Set(new TuneSelection(2, 1, 1, 0), 77);

        for (int column = 0; column < forwards.Columns; column++)
            for (int row = 0; row < forwards.Rows; row++)
                Assert.Equal(forwards[column, row], backwards[column, row]);
    }

    [Fact]
    public void ScalingIsTheOperationTuningIsDoneIn()
    {
        // A region reading four per cent lean is corrected by adding four per
        // cent to it, not by typing every cell.
        TuneEdit edit = Fuel();
        double before = edit[2, 1];

        edit.Scale(TuneSelection.Cell(2, 1), 4);

        Assert.Equal(before * 1.04, edit[2, 1], 6);
    }

    [Fact]
    public void AddingIsTheArrowKeyEdit()
    {
        TuneEdit edit = Fuel();
        double before = edit[0, 0];

        edit.Add(TuneSelection.Cell(0, 0), 1);
        Assert.Equal(before + 1, edit[0, 0], 6);

        edit.Add(TuneSelection.Cell(0, 0), -1);
        Assert.Equal(before, edit[0, 0], 6);
        Assert.False(edit.IsChanged(0, 0));
    }

    // ----- the firmware's limits ---------------------------------------------

    [Fact]
    public void AValuePastWhatTheFirmwareAllowsIsHeldAtTheLimit()
    {
        // Clamped rather than refused: scaling a whole table up should not fail
        // because one cell was already at the top. What must not happen is an
        // out-of-range value reaching the encoder, which would refuse the write
        // entirely and turn one bad cell into a table that cannot be sent.
        TuneEdit edit = Fuel();

        edit.Set(TuneSelection.Cell(0, 0), 5000);
        Assert.Equal(255, edit[0, 0]);

        edit.Set(TuneSelection.Cell(0, 1), -40);
        Assert.Equal(0, edit[0, 1]);
    }

    [Fact]
    public void ATableWithNoDeclaredRangeIsLeftToTheEncoder()
    {
        // Some firmware omits the limits. Inventing one would be worse than
        // letting the datatype decide, which the encoder already checks.
        var table = new TuneTable(
            "Unknown", new TuneAxis("x", "", [1, 2]), new TuneAxis("y", "", [1]),
            new double[2, 1], "");

        var edit = new TuneEdit(table);
        edit.Set(TuneSelection.Cell(0, 0), 5000);

        Assert.Equal(5000, edit[0, 0]);
        Assert.True(double.IsNaN(edit.Low));
    }

    [Fact]
    public void NonsenseNeverReachesACell()
    {
        TuneEdit edit = Fuel();

        edit.Scale(TuneSelection.Cell(0, 0), double.NaN);

        Assert.False(double.IsNaN(edit[0, 0]));
    }

    // ----- what it encodes to ------------------------------------------------

    [Fact]
    public void AnEditEncodesToTheBytesTheEcuWouldHold()
    {
        // The half that cannot be seen on screen, and the half where being wrong
        // is silent: a wrong number encodes to a perfectly valid byte.
        var layout = new TuneLayout
        {
            Pages = [new TunePage { Index = 0, Size = 12, Identifier = "", ReadCommand = "r" }],
            Constants =
            [
                new TuneConstant
                {
                    Name = "veTable", Page = 0, Offset = 0, Type = RealtimeType.U08,
                    Columns = 4, Rows = 3, Scale = 1, Low = 0, High = 255,
                },
            ],
        };

        EcuTune tune = EcuTune.FromPages(layout, new byte[12]);
        TuneEdit edit = Fuel();

        edit.Set(TuneSelection.Cell(0, 0), 100);

        TuneWrite write = Assert.IsType<TuneWrite>(edit.Encode(tune));

        Assert.Equal(0, write.Page);
        Assert.Equal(0, write.Offset);
        Assert.Equal(12, write.Data.Length);

        // Row-major, as the firmware stores it: cell (column 0, row 0) first.
        Assert.Equal(100, write.Data[0]);

        // And the cells that were not touched still carry what they read.
        Assert.Equal((byte)edit.Original(1, 0), write.Data[1]);
    }

    [Fact]
    public void ScalingByAPercentageAndBackChangesNothing()
    {
        // Floating-point dust must not leave a table claiming to be dirty while
        // encoding to the identical bytes — that reads as a write being ignored.
        TuneEdit edit = Fuel();
        var whole = new TuneSelection(0, 0, 3, 2);

        edit.Scale(whole, 10);
        edit.Scale(whole, -100.0 / 11);

        Assert.False(edit.HasChanges);
    }

    [Fact]
    public void TheSmallestUsefulStepComesFromHowTheValueIsStored()
    {
        // A table of whole bytes cannot hold half a percent, so nudging by less
        // would show a change the ECU rounds away.
        Assert.Equal(1, Fuel().Step);
    }
}
