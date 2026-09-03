using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The half of the view model that only exists while an ECU is attached.
///
/// <para>
/// Untested until now, because the only way in built its own serial port. That
/// is where three code reviews running found the same defect: a piece of state
/// wired into the path being worked on and not into its siblings — a write gate
/// raised in three places and not the fourth, a page struck off a pending list
/// on one exit and not the other. None of them could have failed a test,
/// because none of this could be reached.
/// </para>
/// </summary>
public class ConnectedEcuTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();



    /// <summary>Connects a view model to a fake controller and returns both.</summary>
    private MainViewModel Connected(out FakeController board, string? tune = null) =>
        EcuFixture.Connected(_harness, out board, tune);

    /// <summary>
    /// One field of one settings page, reached the way the window reaches it:
    /// pick the menu entry, which opens the dialog, then find the row.
    /// </summary>
    private static SettingRow Row(MainViewModel vm, string label)
    {
        if (vm.OpenDialog is null)
            vm.OpenMenuEntry = vm.SettingsMenu.First(e => e is { IsHeading: false, IsTable: false });

        return vm.OpenDialog!.Rows.First(r => r.Label == label);
    }

    // ----- that the seam is the real path -------------------------------------

    [Fact]
    public void ConnectingReadsTheTuneAndBuildsTheSettingsMenu()
    {
        MainViewModel vm = Connected(out _);

        Assert.True(vm.IsLive);
        Assert.True(vm.HasEcuTune);
        Assert.False(vm.TuneIsPlaceholder);
        Assert.True(vm.HasSettingsPages);

        // Read off the controller rather than assumed: these are the bytes the
        // fake was given, decoded through the firmware's own declarations.
        Assert.Equal("300", Row(vm, "Cranking RPM").Value);
        Assert.Equal("6500", Row(vm, "Rev limit").Value);
    }

    [Fact]
    public void AndTheTableTheFirmwareDeclares()
    {
        MainViewModel vm = Connected(out _);

        Assert.Contains(vm.EcuTables, t => t.Name == "VE Table");
    }

    [Fact]
    public void ATuneReadOffAControllerMayBeWrittenAndBurned()
    {
        MainViewModel vm = Connected(out _);

        // Burning is gated on the connection and on the tune being a real one,
        // so it is available the moment there is something to commit.
        Assert.True(vm.CanBurn);

        // The other three additionally want something pending, and nothing has
        // been touched yet.
        Assert.False(vm.CanWriteTable);
        Assert.False(vm.CanWriteSettings);
        Assert.False(vm.CanBurnSettings);
    }

    [Fact]
    public void ChangingASettingOpensTheGateThatSendsIt()
    {
        MainViewModel vm = Connected(out _);

        Row(vm, "Cranking RPM").Value = "400";

        Assert.True(vm.HasSettingChanges);
        Assert.True(vm.CanWriteSettings);
        Assert.Equal(1, vm.SettingsChangedCount);
    }

    // ----- what a burn leaves behind ------------------------------------------

    /// <summary>Changes one setting and sends it, leaving a page to be burned.</summary>
    private static void WriteASetting(MainViewModel vm)
    {
        Row(vm, "Cranking RPM").Value = "400";
        Assert.True(vm.CanWriteSettings);

        WriteResult said = vm.WriteSettingsToEcu();

        Assert.True(vm.CanBurnSettings, $"nothing was left to burn; the write said.Message: {said.Message}");
    }

    [Fact]
    public void AConfirmedBurnClearsThePageAndSaysSo()
    {
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        WriteResult said = vm.BurnSettingsToEcu();

        Assert.Equal(1, board.Burns);
        Assert.False(vm.CanBurnSettings);
        Assert.Contains("survive a power cycle", said.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABurnThatWentQuietIsNotOfferedAgain()
    {
        // The defect this file was written for. A controller stops answering
        // while it writes flash, so an unconfirmed burn may well have committed
        // the page — and leaving it on the pending list keeps the Burn button
        // lit over exactly that page. Pressing it then spends a second erase on
        // something already in flash, which is what striking each page off as it
        // lands exists to prevent.
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        board.Burning = BurnBehaviour.GoesQuiet;
        WriteResult said = vm.BurnSettingsToEcu();

        Assert.Equal(1, board.Burns);
        Assert.False(vm.CanBurnSettings);
        Assert.Contains("may still have completed", said.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AndIsNeverCalledAFailure()
    {
        // It read "The burn failed: ... It may still have completed", which
        // contradicts itself inside one sentence.
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        board.Burning = BurnBehaviour.GoesQuiet;

        Assert.DoesNotContain("burn failed", vm.BurnSettingsToEcu().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABurnTheControllerRefusedStaysOnOffer()
    {
        // The opposite case, and the reason the two are told apart. A controller
        // that answered has said.Message it did not burn, so the page is still pending
        // and burning it again is the right thing to do.
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        board.Burning = BurnBehaviour.Refuses;
        WriteResult said = vm.BurnSettingsToEcu();

        Assert.True(vm.CanBurnSettings);
        Assert.DoesNotContain("may still have completed", said.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedBurnCanBeRetriedAndThenSucceeds()
    {
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        board.Burning = BurnBehaviour.Refuses;
        vm.BurnSettingsToEcu();

        board.Burning = BurnBehaviour.Confirms;
        vm.BurnSettingsToEcu();

        Assert.False(vm.CanBurnSettings);
        Assert.NotNull(board.Flash);
    }

    // ----- and that a write actually reaches the controller -------------------

    [Fact]
    public void ASettingSentLandsInTheControllersOwnBytes()
    {
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        // 400 rpm, big-endian, at the offset the firmware declares.
        Assert.Equal(0x01, board.Page[0]);
        Assert.Equal(0x90, board.Page[1]);
    }

    [Fact]
    public void ABurnCommitsWhatTheControllerHoldsRatherThanWhatWasAskedFor()
    {
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        vm.BurnSettingsToEcu();

        Assert.NotNull(board.Flash);
        Assert.Equal(board.Page, board.Flash);
    }

    // ----- disconnecting ------------------------------------------------------

    [Fact]
    public void DisconnectingShutsEveryGateAgain()
    {
        MainViewModel vm = Connected(out _);
        Assert.True(vm.CanBurn);

        vm.Disconnect();

        Assert.False(vm.IsLive);
        Assert.False(vm.CanBurn);
        Assert.False(vm.CanWriteTable);
        Assert.False(vm.CanWriteSettings);
        Assert.False(vm.CanBurnSettings);
    }
}
