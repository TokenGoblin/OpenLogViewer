using System.IO;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Propose, apply and stage, driven the way an agent actually reaches them: by
/// name and value over <see cref="IAgentBridge"/>, against a real
/// <see cref="FakeController"/> rather than a hand-built <see cref="EcuTune"/>.
///
/// <para>
/// Propose has to prove a negative that a unit test on <see cref="EcuTune"/>
/// alone cannot: that working out a diff never moves a byte the controller
/// holds. Apply has to prove the opposite — that what was proposed is exactly
/// what gets sent — and that nothing here ever reaches a burn. Staging has to
/// prove that a hypothetical tune written to disk is not the live one.
/// </para>
/// </summary>
public class AgentTuneWorkflowTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private const string Signature = "TEST Format 0001.00";

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
    private MainViewModel Connected(out FakeController board)
    {
        MainViewModel vm = _harness.NewViewModel();
        _harness.WriteDefinition(vm, "test.ini", Firmware);

        board = new FakeController(Signature);

        // Something other than noughts, so a tune read off it is distinguishable
        // from the placeholder a definition alone produces.
        board.Page[0] = 0x01;
        board.Page[1] = 0x2C;   // crankingRPM = 300
        board.Page[2] = 0x19;
        board.Page[3] = 0x64;   // revLimit = 6500

        vm.Connect(board, "COM-TEST");

        return vm;
    }

    // ----- propose: never touches the ECU -------------------------------------

    [Fact]
    public void ProposingNeedsNoArmingAndMovesNothingOnTheController()
    {
        MainViewModel vm = Connected(out FakeController board);
        var bridge = new AgentBridge(vm);

        Assert.False(vm.AgentWritesArmed);

        TuneProposalResult result = bridge.ProposeTune([new ProposedSetting("crankingRPM", 400)], []);

        TuneDifference change = Assert.Single(result.Changes);
        Assert.Equal("crankingRPM", change.Name);
        Assert.Equal(400, change.Mine);
        Assert.Equal(300, change.Theirs);

        // Nothing went over the wire: the byte the controller holds is the one
        // it started with, not the one that was proposed.
        Assert.Equal(0x01, board.Page[0]);
        Assert.Equal(0x2C, board.Page[1]);
    }

    [Fact]
    public void ProposingAnUnknownSettingIsRejectedByNameRatherThanThrowing()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        TuneProposalResult result = bridge.ProposeTune([new ProposedSetting("noSuchThing", 1)], []);

        Assert.Empty(result.Changes);
        Assert.Contains(result.Rejected, r => r.Contains("no such setting", StringComparison.Ordinal));
    }

    [Fact]
    public void ProposingACellOutsideTheTableIsRejectedWithItsSize()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        TuneProposalResult result = bridge.ProposeTune([], [new ProposedCell("VE Table", 99, 0, 50)]);

        Assert.Empty(result.Changes);
        Assert.Contains(result.Rejected, r => r.Contains("not in the table", StringComparison.Ordinal));
    }

    [Fact]
    public void ProposingATableCellReportsTheDifferenceAgainstTheLiveTable()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        TuneProposalResult result = bridge.ProposeTune([], [new ProposedCell("VE Table", 0, 0, 55)]);

        TuneDifference change = Assert.Single(result.Changes);
        Assert.Equal(55, change.Mine);
        Assert.Equal(0, change.Theirs); // the board's veTable bytes default to nought
    }

    // ----- apply: the same write path a lone call already uses -----------------

    [Fact]
    public void ApplyingIsRefusedUntilWritesAreArmedAndNothingMoves()
    {
        MainViewModel vm = Connected(out FakeController board);
        var bridge = new AgentBridge(vm);

        TuneApplyResult result = bridge.ApplyTune([new ProposedSetting("crankingRPM", 400)], [], "testing");

        Assert.NotNull(result.Refusal);
        Assert.Contains("not armed", result.Refusal!.Reason, StringComparison.Ordinal);
        Assert.Equal(0x2C, board.Page[1]);
    }

    [Fact]
    public void ApplyingIsRefusedWithNoRationaleEvenWhenArmed()
    {
        MainViewModel vm = Connected(out FakeController board);
        vm.AgentWritesArmed = true;
        var bridge = new AgentBridge(vm);

        TuneApplyResult result = bridge.ApplyTune([new ProposedSetting("crankingRPM", 400)], [], "");

        Assert.NotNull(result.Refusal);
        Assert.Contains("no rationale", result.Refusal!.Reason, StringComparison.Ordinal);
        Assert.Equal(0x2C, board.Page[1]);
    }

    [Fact]
    public void ApplyingOnceArmedReachesTheControllerAndReportsWhatChanged()
    {
        MainViewModel vm = Connected(out FakeController board);
        vm.AgentWritesArmed = true;
        var bridge = new AgentBridge(vm);

        TuneApplyResult result = bridge.ApplyTune([new ProposedSetting("crankingRPM", 400)], [], "testing");

        Assert.Null(result.Refusal);
        Assert.Single(result.Applied);

        // 400 rpm, big-endian, at the offset the firmware declares — the same
        // bytes a person changing it in the dialog would have sent.
        Assert.Equal(0x01, board.Page[0]);
        Assert.Equal(0x90, board.Page[1]);
    }

    [Fact]
    public void ApplyingNeverBurns()
    {
        MainViewModel vm = Connected(out FakeController board);
        vm.AgentWritesArmed = true;
        var bridge = new AgentBridge(vm);

        bridge.ApplyTune([new ProposedSetting("crankingRPM", 400)], [], "testing");

        Assert.Equal(0, board.Burns);
        Assert.Null(board.Flash);
    }

    [Fact]
    public void ApplyingASettingAndACellTogetherAppliesBoth()
    {
        MainViewModel vm = Connected(out FakeController board);
        vm.AgentWritesArmed = true;
        var bridge = new AgentBridge(vm);

        TuneApplyResult result = bridge.ApplyTune(
            [new ProposedSetting("revLimit", 7000)], [new ProposedCell("VE Table", 0, 0, 55)], "testing",
            confirmDangerous: true);

        Assert.Null(result.Refusal);
        Assert.Equal(2, result.Applied.Count);
        Assert.Equal(0x1B, board.Page[2]);
        Assert.Equal(0x58, board.Page[3]); // 7000 = 0x1B58
        Assert.Equal(55, board.Page[16]);  // veTable[0,0]
    }

    [Fact]
    public void ApplyingWithNothingValidRefusesNoWriteButSaysWhyOnEach()
    {
        MainViewModel vm = Connected(out _);
        vm.AgentWritesArmed = true;
        var bridge = new AgentBridge(vm);

        TuneApplyResult result = bridge.ApplyTune([new ProposedSetting("noSuchThing", 1)], [], "testing");

        Assert.Null(result.Refusal);
        Assert.Empty(result.Applied);
        Assert.NotEmpty(result.Rejected);
    }

    [Fact]
    public void ApplyingWithNoProjectOpenStillWritesAndDoesNotFailOnThatAccount()
    {
        // Bookkeeping must never turn a successful write into a reported
        // failure, the same rule KeepBurnedTune already follows for a burn.
        MainViewModel vm = Connected(out FakeController board);
        vm.AgentWritesArmed = true;
        var bridge = new AgentBridge(vm);

        TuneApplyResult result = bridge.ApplyTune([new ProposedSetting("crankingRPM", 400)], [], "testing");

        Assert.Null(result.Refusal);
        Assert.Single(result.Applied);
        Assert.Null(vm.Project);
    }

    [Fact]
    public void ApplyingWithAProjectOpenKeepsAVersionDescribingWhatWasApplied()
    {
        MainViewModel vm = Connected(out _);
        vm.OpenProject("Bench");
        vm.AgentWritesArmed = true;
        var bridge = new AgentBridge(vm);

        bridge.ApplyTune([new ProposedSetting("crankingRPM", 400)], [], "raising it for the dyno pull");

        TuneVersion kept = Assert.Single(vm.Project!.Versions);
        Assert.Contains("raising it for the dyno pull", kept.Note, StringComparison.Ordinal);
        Assert.Contains("crankingRPM", kept.Note, StringComparison.Ordinal);
    }

    // ----- staging: a second, independent path, never gated ------------------

    [Fact]
    public void StagingTheTuneInHandNeedsNoArmingAndWritesAnMsqUnderTheWorkspace()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        Assert.False(vm.AgentWritesArmed);

        AgentStageResult result = bridge.StageTune([], [], "");

        Assert.Null(result.Refusal);
        Assert.NotNull(result.File);
        Assert.True(File.Exists(result.File!.Path));
        Assert.StartsWith(vm.Workspace.Staging, result.File.Path, StringComparison.OrdinalIgnoreCase);

        string content = File.ReadAllText(result.File.Path);
        Assert.Contains("crankingRPM", content, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingWithProposedChangesStagesTheHypotheticalTuneNotTheLiveOne()
    {
        MainViewModel vm = Connected(out FakeController board);
        var bridge = new AgentBridge(vm);

        AgentStageResult result = bridge.StageTune(
            [new ProposedSetting("crankingRPM", 999)], [], "hypothetical.msq");

        Assert.Null(result.Refusal);
        string content = File.ReadAllText(result.File!.Path);
        Assert.Contains("999", content, StringComparison.Ordinal);

        // The live tune, and the controller behind it, never moved.
        Assert.Equal(0x01, board.Page[0]);
        Assert.Equal(0x2C, board.Page[1]);
        Assert.False(vm.AgentWritesArmed);
    }

    [Fact]
    public void StagingWithNoTuneInHandIsRefusedRatherThanWritingAnEmptyFile()
    {
        MainViewModel vm = _harness.NewViewModel();
        var bridge = new AgentBridge(vm);

        AgentStageResult result = bridge.StageTune([], [], "");

        Assert.NotNull(result.Refusal);
        Assert.Null(result.File);
    }

    [Fact]
    public void StagingATableWritesACsvUnderTheWorkspace()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        AgentStageResult result = bridge.StageTable("VE Table", "");

        Assert.Null(result.Refusal);
        Assert.True(File.Exists(result.File!.Path));
        Assert.StartsWith(vm.Workspace.Staging, result.File.Path, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("VE Table,", File.ReadAllText(result.File.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void StagingAnUnknownTableIsRefusedByName()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        AgentStageResult result = bridge.StageTable("Nope", "");

        Assert.NotNull(result.Refusal);
        Assert.Contains("no such table", result.Refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ListingStagedFilesShowsEverythingJustWritten()
    {
        MainViewModel vm = Connected(out _);
        var bridge = new AgentBridge(vm);

        bridge.StageTune([], [], "one.msq");
        bridge.StageTable("VE Table", "two.csv");

        IReadOnlyList<AgentStagedFile> staged = bridge.ListStaged();

        Assert.Contains(staged, f => f.Name == "one.msq");
        Assert.Contains(staged, f => f.Name == "two.csv");
    }
}
