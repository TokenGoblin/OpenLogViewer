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

    private const string Signature = "TEST Format 0001.00";

    /// <summary>
    /// A page holding a number, a text field and a table, which between them are
    /// the three things anything here can be asked to write.
    /// </summary>
    private const string Firmware = $"""
        [MegaTune]
           signature = "{Signature}"

        [Constants]
        page = 1
        nPages = 1
        pageSize = 32
        pageIdentifier = "\$tsCanId\x01"
        pageReadCommand = "r%2i%2o%2c"
        pageChunkWrite  = "w%2i%2o%2c%v"
        burnCommand     = "b%2i"
           crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
           revLimit    = scalar, U16, 2, "rpm", 1, 0, 0, 10000, 0
           vehicleName = string, ASCII, 4, 12
           veTable     = array,  U08, 16, [2x2], "%", 1, 0, 0, 255, 0
           rpmBins     = array,  U08, 20, [2],   "rpm", 100, 0, 0, 25500, 0
           mapBins     = array,  U08, 22, [2],   "kPa", 1, 0, 0, 255, 0

        [OutputChannels]
        ochBlockSize = 8
        ochGetCommand = "r\x00\x07%2o%2c"
           rpm  = scalar, U16, 0, "rpm", 1, 0
           clt  = scalar, U16, 2, "deg C", 1, 0

        [Datalog]
           entry = rpm, "RPM", int, "%d"
           entry = clt, "CLT", int, "%d"

        [TableEditor]
           table = veTableTbl, veTableMap, "VE Table", 1
              xBins = rpmBins, rpm
              yBins = mapBins, clt
              zBins = veTable

        [UserDefined]
           dialog = engine, "Engine", yAxis
              field = "Cranking RPM", crankingRPM
              field = "Rev limit", revLimit
              field = "Vehicle", vehicleName

        [Menu]
           menu = "&Engine"
              subMenu = engine, "Engine"
              subMenu = veTableTbl, "VE Table"
        """;

    /// <summary>Connects a view model to a fake controller and returns both.</summary>
    private MainViewModel Connected(out FakeController board, string? tune = null)
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.WriteDefinition(vm, "test.ini", Firmware);

        board = new FakeController(Signature);

        // Something other than noughts, so a tune read off it is distinguishable
        // from the placeholder a definition alone produces.
        board.Page[0] = 0x01;
        board.Page[1] = 0x2C;   // crankingRPM = 300
        board.Page[2] = 0x19;
        board.Page[3] = 0x64;   // revLimit = 6500
        foreach ((char c, int i) in (tune ?? "Bench").Select((c, i) => (c, i)))
            board.Page[4 + i] = (byte)c;

        vm.Connect(board, "COM-TEST");

        return vm;
    }

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

        string said = vm.WriteSettingsToEcu();

        Assert.True(vm.CanBurnSettings, $"nothing was left to burn; the write said: {said}");
    }

    [Fact]
    public void AConfirmedBurnClearsThePageAndSaysSo()
    {
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        string said = vm.BurnSettingsToEcu();

        Assert.Equal(1, board.Burns);
        Assert.False(vm.CanBurnSettings);
        Assert.Contains("survive a power cycle", said, StringComparison.OrdinalIgnoreCase);
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
        string said = vm.BurnSettingsToEcu();

        Assert.Equal(1, board.Burns);
        Assert.False(vm.CanBurnSettings);
        Assert.Contains("may still have completed", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AndIsNeverCalledAFailure()
    {
        // It read "The burn failed: ... It may still have completed", which
        // contradicts itself inside one sentence.
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        board.Burning = BurnBehaviour.GoesQuiet;

        Assert.DoesNotContain("burn failed", vm.BurnSettingsToEcu(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABurnTheControllerRefusedStaysOnOffer()
    {
        // The opposite case, and the reason the two are told apart. A controller
        // that answered has said it did not burn, so the page is still pending
        // and burning it again is the right thing to do.
        MainViewModel vm = Connected(out FakeController board);
        WriteASetting(vm);

        board.Burning = BurnBehaviour.Refuses;
        string said = vm.BurnSettingsToEcu();

        Assert.True(vm.CanBurnSettings);
        Assert.DoesNotContain("may still have completed", said, StringComparison.Ordinal);
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

    // ----- the agent API ------------------------------------------------------

    [Fact]
    public void ConnectingFeedsTheAgentStreamWhateverWasConnectedTo()
    {
        // Written because the first attempt at this hooked the stream at one of
        // the four places a session is created and not the other three — a
        // serial ECU, an OBD2 adapter, a Subaru over SSM and a MaxxECU. The
        // assignment now does it, so this asserts against the funnel rather than
        // against one path through it.
        MainViewModel vm = Connected(out _);

        Assert.True(vm.IsLive);

        // Every creation goes through the property that attaches the handler;
        // if any path assigned the field directly this would be the one place
        // it showed.
        Assert.Contains(
            "Live = new LiveSession(",
            System.IO.File.ReadAllText(SourceOf("MainViewModel.cs")),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "_live = new LiveSession(",
            System.IO.File.ReadAllText(SourceOf("MainViewModel.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>The repository's copy of a source file, for the check above.</summary>
    private static string SourceOf(string name)
    {
        var here = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(here.FullName, "src")))
            here = here.Parent;

        Assert.NotNull(here);

        return System.IO.Path.Combine(here!.FullName, "src", "OpenLogViewer.App", name);
    }

    [Fact]
    public void AgentWritesAreNotArmedUntilSomebodyArmsThem()
    {
        MainViewModel vm = Connected(out _);

        Assert.False(vm.AgentWritesArmed);

        AgentRefusal? refused = new AgentBridge(vm).SetSetting("crankingRPM", 400);

        Assert.NotNull(refused);
        Assert.Contains("not armed", refused!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArmedAgentCanChangeASettingAndItReachesTheController()
    {
        MainViewModel vm = Connected(out FakeController board);
        vm.AgentWritesArmed = true;

        Assert.Null(new AgentBridge(vm).SetSetting("crankingRPM", 400));

        // 400 rpm, big-endian, at the offset the firmware declares — the same
        // bytes a person changing it in the dialog would have sent.
        Assert.Equal(0x01, board.Page[0]);
        Assert.Equal(0x90, board.Page[1]);
    }

    [Fact]
    public void AndNothingItDoesIsEverBurned()
    {
        MainViewModel vm = Connected(out FakeController board);
        vm.AgentWritesArmed = true;

        new AgentBridge(vm).SetSetting("crankingRPM", 400);

        Assert.Equal(0, board.Burns);
        Assert.Null(board.Flash);
    }

    [Fact]
    public void DisconnectingDisarmsTheAgent()
    {
        // The permission belongs to the session, not to the socket. The same
        // laptop meets a bench engine one afternoon and a car the next.
        MainViewModel vm = Connected(out _);
        vm.AgentWritesArmed = true;

        vm.Disconnect();

        Assert.False(vm.AgentWritesArmed);
    }

    [Fact]
    public void TheBridgeReportsWhatIsConnectedAndNamesTheRoles()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        AgentState state = bridge.State();

        Assert.Equal("live", state.Mode);
        Assert.True(state.HasTune);
        Assert.False(state.WritesArmed);
        Assert.Equal("TEST Format 0001.00", state.Signature);
    }

    [Fact]
    public void AndNamesTheRoleOfEachChannelSoOneAgentWorksAcrossFirmwares()
    {
        // The role is what lets an agent written against one controller work on
        // another: a rusEFI calls engine speed RPMValue and a MegaSquirt calls
        // it rpm. Asked over a log, because a headless test has no repaint to
        // populate the document from a live session.
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.Load(_harness.WriteTypicalLog());

        IReadOnlyList<AgentChannel> channels = new AgentBridge(vm).Channels();

        Assert.NotEmpty(channels);
        Assert.Contains(channels, c => c.Role == "EngineSpeed");
        Assert.Contains(channels, c => c.Role == "Coolant");
    }

    [Fact]
    public void AWriteToASettingTheFirmwareDoesNotHaveIsRefusedByName()
    {
        MainViewModel vm = Connected(out _);
        vm.AgentWritesArmed = true;

        AgentRefusal? refused = new AgentBridge(vm).SetSetting("noSuchThing", 1);

        Assert.NotNull(refused);
        Assert.Contains("no such setting", refused!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACellOutsideTheTableIsRefusedWithItsSize()
    {
        MainViewModel vm = Connected(out _);
        vm.AgentWritesArmed = true;

        AgentRefusal? refused = new AgentBridge(vm).SetTableCell("VE Table", 99, 0, 50);

        Assert.NotNull(refused);
        Assert.Contains("not in the table", refused!.Reason, StringComparison.Ordinal);
    }
}
