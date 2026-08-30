using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Editing the settings that are not tables.
///
/// The tests that matter are about bit fields sharing a byte. Getting that wrong
/// does not fail: the ECU takes the byte, the read-back matches what was sent,
/// and two options nobody touched have quietly changed.
/// </summary>
public class TuneSettingsEditTests
{
    /// <summary>
    /// A firmware with one page: a scalar, four options packed into one byte, a
    /// wider bit field, an array and a name.
    /// </summary>
    private static TuneLayout Layout() => new()
    {
        Pages = [new TunePage { Index = 0, Size = 32, Identifier = "", ReadCommand = "" }],
        Constants =
        [
            new TuneConstant
            {
                Name = "crankingRPM", Page = 0, Offset = 0, Type = RealtimeType.U16,
                Scale = 1, Low = 0, High = 10000,
            },
            // Four options in the byte at offset 2.
            new TuneConstant { Name = "optA", Page = 0, Offset = 2, Type = RealtimeType.U08, BitLow = 0, BitHigh = 0 },
            new TuneConstant { Name = "optB", Page = 0, Offset = 2, Type = RealtimeType.U08, BitLow = 1, BitHigh = 1 },
            new TuneConstant { Name = "optC", Page = 0, Offset = 2, Type = RealtimeType.U08, BitLow = 2, BitHigh = 2 },
            new TuneConstant { Name = "mode", Page = 0, Offset = 2, Type = RealtimeType.U08, BitLow = 4, BitHigh = 6 },

            new TuneConstant
            {
                Name = "dwell", Page = 0, Offset = 4, Type = RealtimeType.U08,
                Scale = 0.1, Low = 0, High = 12,
            },
            new TuneConstant
            {
                Name = "trims", Page = 0, Offset = 6, Type = RealtimeType.U08,
                Columns = 4, Scale = 1, Low = 0, High = 255,
            },
            new TuneConstant { Name = "alias", Page = 0, Offset = 12, Type = RealtimeType.U08, IsText = true, Columns = 8 },
        ],
    };

    private static EcuTune Tune(params (int At, byte Value)[] bytes)
    {
        var page = new byte[32];
        foreach ((int at, byte value) in bytes) page[at] = value;

        return EcuTune.FromPages(Layout(), page);
    }

    // ----- scalars ----------------------------------------------------------

    [Fact]
    public void ASettingReadsBackAsWhatItWasSetTo()
    {
        var edit = new TuneSettingsEdit(Tune());

        Assert.True(edit.Set("crankingRPM", 350));
        Assert.Equal(350, edit.Value("crankingRPM"));
        Assert.True(edit.HasChanges);
        Assert.Equal(1, edit.ChangedCount);
    }

    [Fact]
    public void ScaleAndTransformAreApplied()
    {
        // Dwell is stored in tenths, so 4.5 ms is 45 in the byte.
        var tune = Tune();
        var edit = new TuneSettingsEdit(tune);

        Assert.True(edit.Set("dwell", 4.5));
        Assert.Equal(4.5, edit.Value("dwell"), precision: 6);

        TuneWrite write = Assert.Single(edit.Writes());
        Assert.Equal(45, write.Data[0]);
    }

    [Fact]
    public void AValueOutsideWhatTheFirmwareDeclaresIsRefused()
    {
        // Far tighter than the storage allows, which is the point: a byte would
        // take 255 quite happily.
        var edit = new TuneSettingsEdit(Tune());

        Assert.False(edit.Set("dwell", 99));
        Assert.False(edit.Set("dwell", -1));
        Assert.False(edit.HasChanges);
    }

    [Fact]
    public void AValueThatIsNotANumberIsRefused()
    {
        var edit = new TuneSettingsEdit(Tune());

        Assert.False(edit.Set("crankingRPM", double.NaN));
        Assert.False(edit.Set("crankingRPM", double.PositiveInfinity));
    }

    [Fact]
    public void ASettingThisFirmwareDoesNotHaveIsRefused() =>
        Assert.False(new TuneSettingsEdit(Tune()).Set("noSuchThing", 1));

    // ----- bit fields, which is where the damage would be -------------------

    [Fact]
    public void ChangingOneOptionLeavesTheOthersInItsByteAlone()
    {
        // All four set: 0b0111_0111. Turning optB off must clear exactly one bit.
        var edit = new TuneSettingsEdit(Tune((2, 0b0111_0111)));

        Assert.True(edit.Set("optB", 0));

        Assert.Equal(1, edit.Value("optA"));
        Assert.Equal(0, edit.Value("optB"));
        Assert.Equal(1, edit.Value("optC"));
        Assert.Equal(7, edit.Value("mode"));

        TuneWrite write = Assert.Single(edit.Writes());
        Assert.Equal(0b0111_0101, write.Data[0]);
    }

    [Fact]
    public void TwoOptionsInOneByteBothSurviveBeingChanged()
    {
        // The failure this class exists to prevent. Encoded separately against
        // the ECU's original bytes, the second write would carry the first
        // field's old value and undo it -- with no error anywhere.
        var edit = new TuneSettingsEdit(Tune((2, 0)));

        Assert.True(edit.Set("optA", 1));
        Assert.True(edit.Set("optC", 1));

        Assert.Equal(1, edit.Value("optA"));
        Assert.Equal(1, edit.Value("optC"));

        // And they leave as one write, because they are one byte.
        TuneWrite write = Assert.Single(edit.Writes());
        Assert.Equal(0b0000_0101, write.Data[0]);
    }

    [Fact]
    public void AWideBitFieldWritesOnlyItsOwnBits()
    {
        // mode is bits 4..6. Setting it must not touch the options below it nor
        // bit 7 above it.
        var edit = new TuneSettingsEdit(Tune((2, 0b1000_0111)));

        Assert.True(edit.Set("mode", 5));

        Assert.Equal(5, edit.Value("mode"));
        Assert.Equal(1, edit.Value("optA"));
        Assert.Equal(1, edit.Value("optB"));
        Assert.Equal(1, edit.Value("optC"));

        Assert.Equal(0b1101_0111, edit.Writes()[0].Data[0]);
    }

    [Fact]
    public void AValueTooWideForTheFieldDoesNotSpillIntoItsNeighbours()
    {
        // mode is three bits. Eight does not fit, and must not become one with a
        // carry into bit 7.
        var edit = new TuneSettingsEdit(Tune((2, 0)));

        edit.Set("mode", 8);

        Assert.Equal(0, edit.Value("mode"));
        Assert.Equal(0, edit.Value("optA"));
        Assert.Empty(edit.Writes());
    }

    // ----- arrays and text --------------------------------------------------

    [Fact]
    public void OneElementOfAnArrayCanBeChangedOnItsOwn()
    {
        var edit = new TuneSettingsEdit(Tune());

        Assert.True(edit.Set("trims", 12, element: 2));

        Assert.Equal(0, edit.Value("trims", 0));
        Assert.Equal(12, edit.Value("trims", 2));

        TuneWrite write = Assert.Single(edit.Writes());
        Assert.Equal(8, write.Offset);          // offset 6, third element
        Assert.Equal(12, write.Data[0]);
    }

    [Fact]
    public void ATextSettingRoundTrips()
    {
        var edit = new TuneSettingsEdit(Tune());

        Assert.True(edit.SetText("alias", "Coolant"));
        Assert.Equal("Coolant", edit.Text("alias"));
        Assert.True(edit.HasChanges);
    }

    [Fact]
    public void TextIsNotAllowedToOverrunItsField()
    {
        // Eight bytes. A longer name is truncated rather than writing over
        // whatever setting follows it.
        var edit = new TuneSettingsEdit(Tune());

        Assert.True(edit.SetText("alias", "MuchTooLongToFit"));
        // Eight characters, filling the field exactly and stopping there.
        Assert.Equal("MuchTooL", edit.Text("alias"));
        Assert.All(edit.Writes(), w => Assert.True(w.Offset + w.Data.Length <= 20));
    }

    [Fact]
    public void TextOutsideAsciiIsRefusedRatherThanMangled()
    {
        var edit = new TuneSettingsEdit(Tune());

        Assert.False(edit.SetText("alias", "Kühlmittel"));
    }

    [Fact]
    public void ANumberCannotBeWrittenToATextSettingNorTheReverse()
    {
        var edit = new TuneSettingsEdit(Tune());

        Assert.False(edit.Set("alias", 5));
        Assert.False(edit.SetText("crankingRPM", "350"));
    }

    // ----- what would be sent -----------------------------------------------

    [Fact]
    public void NothingChangedMeansNothingToSend()
    {
        var edit = new TuneSettingsEdit(Tune((0, 1), (2, 0b0101)));

        Assert.Empty(edit.Writes());
        Assert.Equal(0, edit.BytesToWrite);
        Assert.False(edit.HasChanges);
    }

    [Fact]
    public void SettingAValueBackToWhatItWasIsNotAChange()
    {
        // Otherwise the header offers to send bytes identical to the ones the
        // ECU already holds.
        var edit = new TuneSettingsEdit(Tune((4, 45)));

        edit.Set("dwell", 6.0);
        Assert.True(edit.HasChanges);

        edit.Set("dwell", 4.5);
        Assert.False(edit.HasChanges);
        Assert.Empty(edit.Writes());
    }

    [Fact]
    public void NeighbouringChangedBytesLeaveAsOneWrite()
    {
        // A write costs a round trip, and four bytes cost no more than one.
        var edit = new TuneSettingsEdit(Tune());

        edit.Set("trims", 1, element: 0);
        edit.Set("trims", 2, element: 1);
        edit.Set("trims", 3, element: 2);

        TuneWrite write = Assert.Single(edit.Writes());
        Assert.Equal(6, write.Offset);
        Assert.Equal([1, 2, 3], write.Data);
    }

    [Fact]
    public void SeparateRegionsLeaveAsSeparateWrites()
    {
        var edit = new TuneSettingsEdit(Tune());

        edit.Set("crankingRPM", 300);
        edit.Set("trims", 9, element: 0);

        Assert.Equal(2, edit.Writes().Count);
    }

    [Fact]
    public void TheChangeListSaysWhatWasAndWhatWouldBe()
    {
        var edit = new TuneSettingsEdit(Tune((0, 0), (1, 100)));

        edit.Set("crankingRPM", 250);

        SettingChange change = Assert.Single(edit.Changes);

        Assert.Equal("crankingRPM", change.Name);
        Assert.Equal(100, change.Was);
        Assert.Equal(250, change.Now);
    }

    // ----- putting it back --------------------------------------------------

    [Fact]
    public void OneSettingCanBePutBack()
    {
        var edit = new TuneSettingsEdit(Tune((4, 45)));

        edit.Set("dwell", 6.0);
        edit.Revert("dwell");

        Assert.Equal(4.5, edit.Value("dwell"), precision: 6);
        Assert.False(edit.HasChanges);
        Assert.Empty(edit.Writes());
    }

    [Fact]
    public void PuttingOneOptionBackLeavesItsNeighboursAsTheyNowAre()
    {
        // The revert restores that field from the ECU's bytes, not the whole
        // byte -- a neighbour edited since must keep its new value.
        var edit = new TuneSettingsEdit(Tune((2, 0)));

        edit.Set("optA", 1);
        edit.Set("optC", 1);
        edit.Revert("optA");

        Assert.Equal(0, edit.Value("optA"));
        Assert.Equal(1, edit.Value("optC"));
        Assert.Equal(1, edit.ChangedCount);
    }

    [Fact]
    public void EverythingCanBePutBackAtOnce()
    {
        var edit = new TuneSettingsEdit(Tune((0, 1), (2, 0b0101), (4, 45)));

        edit.Set("crankingRPM", 999);
        edit.Set("optB", 1);
        edit.Set("dwell", 2.0);

        edit.RevertAll();

        Assert.False(edit.HasChanges);
        Assert.Empty(edit.Writes());
        Assert.Equal(4.5, edit.Value("dwell"), precision: 6);
        Assert.Equal(1, edit.Value("optA"));
        Assert.Equal(0, edit.Value("optB"));
    }

    [Fact]
    public void APcVariableIsNotSomethingToWriteToTheEcu()
    {
        // It has no page, so writing one would land at offset zero of page zero.
        var layout = Layout() with
        {
            PcVariables = [new TuneConstant { Name = "rpmhigh", Page = -1, Offset = 0, Type = RealtimeType.U16 }],
        };

        var edit = new TuneSettingsEdit(EcuTune.FromPages(layout, new byte[32]));

        Assert.False(edit.Set("rpmhigh", 8000));
        Assert.Empty(edit.Writes());
    }
}
