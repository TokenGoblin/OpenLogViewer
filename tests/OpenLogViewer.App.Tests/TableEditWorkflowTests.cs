using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Editing a table through the view model, short of sending it.
///
/// The sending itself needs an ECU. Everything up to it does not, and everything
/// up to it is where a mistake is silent — a wrong cell looks exactly like a
/// right one until an engine runs on it.
/// </summary>
public class TableEditWorkflowTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static TuneTable Fuel()
    {
        var values = new double[4, 3];
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 3; row++)
                values[column, row] = 60 + column + row;

        return new TuneTable(
            "VE Table",
            new TuneAxis("rpmBins", "rpm", [800, 2000, 4000, 6000]),
            new TuneAxis("mapBins", "kPa", [30, 60, 100]),
            values,
            "%");
    }

    private MainViewModel WithTable(out TuneTable table)
    {
        MainViewModel vm = _harness.NewViewModel();
        table = Fuel();
        vm.SelectedEcuTable = table;

        return vm;
    }

    [Fact]
    public void ChoosingATableOpensItForEditing()
    {
        MainViewModel vm = WithTable(out TuneTable table);

        Assert.NotNull(vm.TableEdit);
        Assert.Equal(table.Name, vm.TableEdit!.Name);
        Assert.False(vm.HasTableChanges);
    }

    [Fact]
    public void TheKeyboardChangesWhatIsSelectedAndNothingElse()
    {
        MainViewModel vm = WithTable(out _);

        vm.SelectedCells = new TuneSelection(1, 0, 2, 1);
        vm.EditTable(TuneTableEdit.Add(5));

        Assert.True(vm.TableEdit!.IsChanged(1, 0));
        Assert.True(vm.TableEdit.IsChanged(2, 1));
        Assert.False(vm.TableEdit.IsChanged(0, 0));
        Assert.False(vm.TableEdit.IsChanged(3, 2));
    }

    [Fact]
    public void ScalingActsOnTheWholeSelection()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = new TuneSelection(0, 0, 3, 2);

        double before = vm.TableEdit![0, 0];
        vm.EditTable(TuneTableEdit.Scale(10));

        Assert.Equal(before * 1.1, vm.TableEdit[0, 0], 6);
        Assert.Equal(12, vm.TableEdit.ChangedCount);
    }

    [Fact]
    public void EscapePutsBackOnlyWhatIsSelected()
    {
        MainViewModel vm = WithTable(out _);

        vm.SelectedCells = TuneSelection.Cell(0, 0);
        vm.EditTable(TuneTableEdit.Add(5));

        vm.SelectedCells = TuneSelection.Cell(3, 2);
        vm.EditTable(TuneTableEdit.Add(5));

        vm.SelectedCells = TuneSelection.Cell(0, 0);
        vm.EditTable(TuneTableEdit.RevertSelection());

        Assert.False(vm.TableEdit!.IsChanged(0, 0));
        Assert.True(vm.TableEdit.IsChanged(3, 2));
    }

    [Fact]
    public void RevertingClearsEverything()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = new TuneSelection(0, 0, 3, 2);
        vm.EditTable(TuneTableEdit.Add(5));

        vm.RevertTable();

        Assert.False(vm.HasTableChanges);
    }

    [Fact]
    public void MovingToAnotherTableDoesNotCarryAnUnsentChange()
    {
        // A pending change on a table nobody is looking at is a change nobody
        // can see, and it would be sent by a later click meant for something
        // else.
        MainViewModel vm = WithTable(out _);
        vm.EditTable(TuneTableEdit.Add(5));
        Assert.True(vm.HasTableChanges);

        vm.SelectedEcuTable = Fuel() with { Name = "Ignition" };

        Assert.False(vm.HasTableChanges);
        Assert.Equal("Ignition", vm.TableEdit!.Name);
    }

    [Fact]
    public void TheHeaderSaysWhatIsSelectedAndHowMuchIsUnsent()
    {
        MainViewModel vm = WithTable(out _);

        vm.SelectedCells = TuneSelection.Cell(1, 0);
        Assert.Contains("2000", vm.TableEditSummary, StringComparison.Ordinal);
        Assert.Contains("no changes", vm.TableEditSummary, StringComparison.Ordinal);

        vm.EditTable(TuneTableEdit.Add(1));
        Assert.Contains("1 cell changed, not sent", vm.TableEditSummary, StringComparison.Ordinal);

        vm.SelectedCells = new TuneSelection(0, 0, 1, 1);
        Assert.Contains("2×2 cells", vm.TableEditSummary, StringComparison.Ordinal);
    }

    // ----- refusing to write -------------------------------------------------

    [Fact]
    public void NothingCanBeSentWithoutAConnection()
    {
        // The button is disabled, but the guard is what actually matters: a
        // disabled button is a hint, not a rule.
        MainViewModel vm = WithTable(out _);
        vm.EditTable(TuneTableEdit.Add(5));

        Assert.False(vm.CanWriteTable);
        Assert.Contains("Not connected", vm.WriteTableToEcu().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnchangedTableIsNotWorthSending()
    {
        MainViewModel vm = WithTable(out _);

        Assert.False(vm.CanWriteTable);
    }

    [Fact]
    public void BurningWithoutAConnectionIsRefusedAndSaysWhy()
    {
        // Burning is permanent, so the refusal has to name the reason rather
        // than fail quietly — the connection is checked before anything else,
        // since without one none of the later questions can be answered.
        MainViewModel vm = WithTable(out _);

        Assert.Contains("Not connected", vm.BurnTableToEcu().Message, StringComparison.OrdinalIgnoreCase);
    }

    // ----- the tools on the page --------------------------------------------

    [Fact]
    public void TheButtonsRaiseTheSameRequestsTheKeyboardDoes()
    {
        // The buttons exist because the keys are not discoverable, not because
        // they do anything different. Anything else and the two drift apart.
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = TuneSelection.Cell(1, 1);

        double before = vm.TableEdit![1, 1];

        vm.EditTable(TuneTableEdit.Add(vm.TableNudge));
        Assert.Equal(before + vm.TableNudge, vm.TableEdit[1, 1], 6);

        vm.EditTable(TuneTableEdit.Add(-vm.TableNudge));
        Assert.Equal(before, vm.TableEdit[1, 1], 6);
        Assert.False(vm.HasTableChanges);
    }

    [Fact]
    public void SettingACellPutsExactlyThatValueInIt()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = new TuneSelection(0, 0, 1, 1);

        vm.EditTable(TuneTableEdit.Set(77.5));

        Assert.Equal(77.5, vm.TableEdit![0, 0], 6);
        Assert.Equal(77.5, vm.TableEdit[1, 1], 6);
        Assert.NotEqual(77.5, vm.TableEdit[3, 2]);
    }

    [Fact]
    public void InterpolatingStraightensTheMiddleOfASelection()
    {
        MainViewModel vm = WithTable(out _);

        // The top row runs 60, 61, 62, 63. Pull the middle two about.
        vm.SelectedCells = TuneSelection.Cell(1, 0);
        vm.EditTable(TuneTableEdit.Set(20));
        vm.SelectedCells = TuneSelection.Cell(2, 0);
        vm.EditTable(TuneTableEdit.Set(90));

        vm.SelectedCells = new TuneSelection(0, 0, 3, 0);
        vm.EditTable(TuneTableEdit.Interpolate());

        Assert.Equal(61, vm.TableEdit![1, 0], 6);
        Assert.Equal(62, vm.TableEdit[2, 0], 6);
        Assert.False(vm.TableEdit.IsChanged(1, 0));
        Assert.False(vm.TableEdit.IsChanged(2, 0));
    }

    [Fact]
    public void InterpolatingASelectionWithNoMiddleSaysSoRatherThanDoingNothingQuietly()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = TuneSelection.Cell(1, 1);

        vm.EditTable(TuneTableEdit.Interpolate());

        Assert.False(vm.HasTableChanges);
        Assert.Contains("three cells", vm.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNudgeFollowsTheTablesOwnStorageStep()
    {
        // Anything finer than the firmware's resolution is rounded away by the
        // ECU, which reads as the write having been ignored.
        MainViewModel vm = _harness.NewViewModel();

        vm.SelectedEcuTable = Fuel();

        Assert.Equal(vm.TableEdit!.Step, vm.TableNudge, 6);
    }

    // ----- what it would become ---------------------------------------------

    [Fact]
    public void TheReadoutShowsWhatTheCellWouldBecome()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = TuneSelection.Cell(1, 1);

        Assert.False(vm.HasTableChangePreview);

        vm.EditTable(TuneTableEdit.Add(4));

        Assert.True(vm.HasTableChangePreview);
        Assert.Equal("62", vm.TableChangeFrom);
        Assert.Equal("66", vm.TableChangeTo);
        Assert.Contains("+4", vm.TableChangeDelta);
        Assert.Contains("not sent", vm.TableChangeState);
    }

    [Fact]
    public void AnUntouchedCellShowsItsValueAndNoArrow()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = TuneSelection.Cell(2, 1);

        Assert.False(vm.HasTableChangePreview);
        Assert.Equal(vm.TableChangeFrom, vm.TableChangeTo);
        Assert.Equal("", vm.TableChangeDelta);
        Assert.Equal("unchanged", vm.TableChangeState);
    }

    [Fact]
    public void ASelectionShowsTheRangeItWouldBecome()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = new TuneSelection(0, 0, 3, 0);

        vm.EditTable(TuneTableEdit.Add(2));

        // The top row is 60..63, so it becomes 62..65 with every cell moving
        // by the same amount.
        Assert.Equal("60–63", vm.TableChangeFrom);
        Assert.Equal("62–65", vm.TableChangeTo);
        Assert.Contains("every cell", vm.TableChangeDelta);
    }

    [Fact]
    public void ScalingASelectionShowsThatTheCellsMovedByDifferentAmounts()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = new TuneSelection(0, 0, 3, 0);

        vm.EditTable(TuneTableEdit.Scale(10));

        Assert.DoesNotContain("every cell", vm.TableChangeDelta);
        Assert.Contains("to", vm.TableChangeDelta);
    }

    [Fact]
    public void TheReadoutFollowsTheSelectionWithoutAnythingBeingEdited()
    {
        // Moving the cursor has to update the readout, or it describes the cell
        // that was selected two clicks ago.
        MainViewModel vm = WithTable(out _);

        vm.SelectedCells = TuneSelection.Cell(0, 0);
        string first = vm.TableChangeFrom;
        string firstWhere = vm.TableChangeWhere;

        vm.SelectedCells = TuneSelection.Cell(3, 2);

        Assert.NotEqual(first, vm.TableChangeFrom);
        Assert.NotEqual(firstWhere, vm.TableChangeWhere);
    }

    [Fact]
    public void TheReadoutNamesWhereTheCellIs()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = TuneSelection.Cell(1, 1);

        Assert.Contains("2000", vm.TableChangeWhere);
        Assert.Contains("60", vm.TableChangeWhere);

        vm.SelectedCells = new TuneSelection(0, 0, 1, 1);

        Assert.Contains("2×2", vm.TableChangeWhere);
    }

    [Fact]
    public void RevertingTheSelectionLeavesTheReadoutSayingUnchanged()
    {
        MainViewModel vm = WithTable(out _);
        vm.SelectedCells = TuneSelection.Cell(1, 1);

        vm.EditTable(TuneTableEdit.Add(4));
        Assert.True(vm.HasTableChangePreview);

        vm.EditTable(TuneTableEdit.RevertSelection());

        Assert.False(vm.HasTableChangePreview);
        Assert.Equal("unchanged", vm.TableChangeState);
    }
}
