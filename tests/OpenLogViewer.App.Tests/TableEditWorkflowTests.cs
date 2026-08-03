using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Editing a table through the view model, short of sending it.
///
/// The sending itself needs an ECU. Everything up to it does not, and everything
/// up to it is where a mistake is silent — a wrong cell looks exactly like a
/// right one until an engine runs on it.
/// </summary>
public class TableEditWorkflowTests
{
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

    private static MainViewModel WithTable(out TuneTable table)
    {
        var vm = new MainViewModel();
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
        Assert.Contains("Not connected", vm.WriteTableToEcu(), StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("Not connected", vm.BurnTableToEcu(), StringComparison.OrdinalIgnoreCase);
    }
}
