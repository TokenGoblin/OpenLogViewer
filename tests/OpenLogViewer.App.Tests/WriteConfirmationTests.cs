using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// That nothing reaches a controller or a vehicle without being confirmed.
///
/// <para>
/// The confirmations used to live in the click handlers, which meant the gate
/// held for the buttons and for nothing else: a scripted run, a test, and in due
/// course an MCP tool all called the same view-model methods and reached a
/// running engine with nothing asked. These tests are about the gate itself
/// rather than about what a write does once it is through — every other test
/// file answers yes and moves on.
/// </para>
/// </summary>
public class WriteConfirmationTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private MainViewModel Connected(out FakeController board)
    {
        MainViewModel vm = EcuFixture.Connected(_harness, out board);
        _harness.Confirmation.Answer = false;

        return vm;
    }

    /// <summary>One field changed, which is what makes a settings write possible.</summary>
    private static void ChangeASetting(MainViewModel vm)
    {
        vm.OpenMenuEntry = vm.SettingsMenu.First(e => e is { IsHeading: false, IsTable: false });
        vm.OpenDialog!.Rows.First(r => r.Label == "Cranking RPM").Value = "400";
    }

    /// <summary>One cell changed, which is what makes a table write possible.</summary>
    private static void ChangeACell(MainViewModel vm)
    {
        vm.SelectedEcuTable = vm.EcuTables.First(t => t.Name == "VE Table");
        vm.SelectedCells = TuneSelection.Cell(0, 0);
        vm.EditTable(TuneTableEdit.Add(5));
    }

    // ----- a refusal sends nothing --------------------------------------------

    [Fact]
    public void ADeclinedTableWriteSendsNothing()
    {
        MainViewModel vm = Connected(out FakeController board);
        ChangeACell(vm);

        byte[] before = [.. board.Page];

        WriteResult said = vm.WriteTableToEcu();

        Assert.Equal(before, board.Page);
        Assert.Contains("Nothing was sent", said.Message, StringComparison.Ordinal);

        // And the edit is still pending, so declining is not the same as
        // discarding what was typed.
        Assert.True(vm.HasTableChanges);
    }

    [Fact]
    public void ADeclinedTableBurnBurnsNothing()
    {
        MainViewModel vm = Connected(out FakeController board);
        ChangeACell(vm);

        WriteResult said = vm.BurnTableToEcu();

        Assert.Equal(0, board.Burns);
        Assert.Contains("Nothing was burned", said.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclinedSettingsWriteSendsNothing()
    {
        MainViewModel vm = Connected(out FakeController board);
        ChangeASetting(vm);

        byte[] before = [.. board.Page];

        WriteResult said = vm.WriteSettingsToEcu();

        Assert.Equal(before, board.Page);
        Assert.Contains("Nothing was sent", said.Message, StringComparison.Ordinal);

        // Nothing was sent, so nothing is waiting to be burned either — the
        // pending-pages list is what the Burn button is lit from.
        Assert.False(vm.CanBurnSettings);
        Assert.True(vm.HasSettingChanges);
    }

    [Fact]
    public void ADeclinedSettingsBurnBurnsNothing()
    {
        MainViewModel vm = EcuFixture.Connected(_harness, out FakeController board);

        // Sent with the confirmation answering yes, so there is a page pending.
        ChangeASetting(vm);
        vm.WriteSettingsToEcu();
        Assert.True(vm.CanBurnSettings);

        _harness.Confirmation.Answer = false;

        WriteResult said = vm.BurnSettingsToEcu();

        Assert.Equal(0, board.Burns);
        Assert.Contains("Nothing was burned", said.Message, StringComparison.Ordinal);

        // Still pending. A declined burn leaves the page exactly as it was, so
        // the button stays lit and the answer can be reconsidered.
        Assert.True(vm.CanBurnSettings);
    }

    [Fact]
    public void ADeclinedFaultEraseErasesNothing()
    {
        // No OBD2 vehicle here, so this asserts the order rather than the erase:
        // with nothing connected the confirmation is never reached at all, which
        // is why it is asked after the guard and not before it.
        MainViewModel vm = _harness.NewViewModel();
        _harness.Confirmation.Answer = false;

        Assert.Null(vm.ClearFaults());
        Assert.Empty(_harness.Confirmation.Asked);
    }

    // ----- what is actually asked ---------------------------------------------

    [Fact]
    public void ATableWriteSaysHowManyCellsAndThatItIsNotPermanent()
    {
        MainViewModel vm = EcuFixture.Connected(_harness, out _);
        ChangeACell(vm);

        vm.WriteTableToEcu();

        WriteRequest asked = Assert.Single(_harness.Confirmation.Asked);

        Assert.Equal(WriteKind.Table, asked.Kind);
        Assert.False(asked.Permanent);
        Assert.Contains("1 changed cell of VE Table", asked.Question, StringComparison.Ordinal);
        Assert.Contains("forgets it at the next power cycle", asked.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ABurnSaysItIsPermanentAndAsksForTheEngineToBeStopped()
    {
        MainViewModel vm = EcuFixture.Connected(_harness, out _);
        ChangeACell(vm);

        vm.BurnTableToEcu();

        WriteRequest asked = Assert.Single(_harness.Confirmation.Asked);

        Assert.Equal(WriteKind.TableBurn, asked.Kind);
        Assert.True(asked.Permanent);
        Assert.Contains("permanent", asked.Detail, StringComparison.OrdinalIgnoreCase);

        // The one thing no software here can check, which is why it is asked.
        Assert.Contains("engine stopped", asked.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsWriteSaysHowManyBytesAcrossHowManyPages()
    {
        MainViewModel vm = EcuFixture.Connected(_harness, out _);
        ChangeASetting(vm);

        vm.WriteSettingsToEcu();

        WriteRequest asked = Assert.Single(_harness.Confirmation.Asked);

        Assert.Equal(WriteKind.Settings, asked.Kind);
        Assert.Contains("1 changed setting", asked.Question, StringComparison.Ordinal);
        Assert.Contains("across 1 page", asked.Detail, StringComparison.Ordinal);
    }

    // ----- the ordering the move fixed ----------------------------------------

    [Fact]
    public void AWriteThatWouldBeRefusedAnywayIsNeverPutToAPerson()
    {
        // The fault the move fixed. The dialog used to be raised by the button,
        // before the view model had had a chance to refuse — so a person was
        // asked to confirm sending cells that were never going to be sent, and
        // answering yes changed nothing. Nothing here is connected.
        MainViewModel vm = _harness.NewViewModel();

        Assert.Equal("No table is open.", vm.WriteTableToEcu().Message);
        Assert.Empty(_harness.Confirmation.Asked);
    }

    [Fact]
    public void NorIsABurnWithNothingToBurn()
    {
        MainViewModel vm = EcuFixture.Connected(_harness, out _);

        Assert.Contains("nothing to burn", vm.BurnSettingsToEcu().Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_harness.Confirmation.Asked);
    }

    // ----- the default ---------------------------------------------------------

    [Fact]
    public void AViewModelBuiltWithoutAConfirmationRefusesEveryWrite()
    {
        // How a missed wiring fails: closed. The two ways this can go wrong are
        // not equal — a button that does nothing gets noticed, a silent write to
        // a running engine does not.
        var vm = new MainViewModel();

        Assert.IsType<DeniedWriteConfirmation>(DeniedWriteConfirmation.Instance);
        Assert.False(new DeniedWriteConfirmation().Confirm(
            new WriteRequest(WriteKind.Table, "anything?", "")));

        // Nothing is open here, so this refuses for that reason first; the point
        // is that it never reaches a controller either way.
        Assert.Equal("No table is open.", vm.WriteTableToEcu().Message);
    }
}
