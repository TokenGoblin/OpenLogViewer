using System.Linq;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Phase 3's write-time guardrails: a required rationale, a dangerous-constant
/// confirmation, a magnitude limit, a rate limit, and a refusal while the
/// engine is running above idle.
///
/// Driven the same way <see cref="ConnectedEcuTests"/> drives the write path
/// itself — a real <see cref="FakeController"/> behind a real connection —
/// because every one of these gates sits in front of that same path, and a
/// gate tested against a hand-built <c>EcuTune</c> would not prove it is
/// actually wired into the calls an agent makes.
/// </summary>
public class AgentGuardrailTests : IDisposable
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
        """;

    /// <summary>Connects a view model to a fake controller, armed for writing.</summary>
    private MainViewModel Connected(out FakeController board)
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.WriteDefinition(vm, "test.ini", Firmware);

        board = new FakeController(Signature);
        board.Page[0] = 0x01;
        board.Page[1] = 0x2C;   // crankingRPM = 300
        board.Page[2] = 0x19;
        board.Page[3] = 0x64;   // revLimit = 6500

        vm.Connect(board, "COM-TEST");
        vm.AgentWritesArmed = true;

        return vm;
    }

    private static AgentBridge Bridge(MainViewModel vm) => new(vm);

    /// <summary>Waits for the live session to have produced at least one sample.</summary>
    private static void WaitForALiveSample(MainViewModel vm)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !vm.RefreshLive()) Thread.Sleep(10);
    }

    // ----- a rationale is required ---------------------------------------------

    [Fact]
    public void AWriteWithNoRationaleIsRefusedAndNothingIsSent()
    {
        MainViewModel vm = Connected(out FakeController board);

        AgentRefusal? refused = Bridge(vm).SetSetting("crankingRPM", 310, "");

        Assert.NotNull(refused);
        Assert.Contains("no rationale", refused!.Reason, StringComparison.Ordinal);
        Assert.Equal(0x01, board.Page[0]);
        Assert.Equal(0x2C, board.Page[1]);
    }

    [Fact]
    public void ABlankRationaleIsTheSameAsNone()
    {
        MainViewModel vm = Connected(out _);

        AgentRefusal? refused = Bridge(vm).SetSetting("crankingRPM", 310, "   ");

        Assert.NotNull(refused);
        Assert.Contains("no rationale", refused!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableWriteAlsoNeedsARationale()
    {
        MainViewModel vm = Connected(out _);

        AgentRefusal? refused = Bridge(vm).SetTableCell("VE Table", 0, 0, 55, "").Refusal;

        Assert.NotNull(refused);
        Assert.Contains("no rationale", refused!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARationaleIsNotEnoughOnItsOwnIfWritesAreNotArmed()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.WriteDefinition(vm, "test.ini", Firmware);
        var board = new FakeController(Signature);
        vm.Connect(board, "COM-TEST");

        // Deliberately not armed.
        AgentRefusal? refused = Bridge(vm).SetSetting("crankingRPM", 310, "a good reason");

        Assert.NotNull(refused);
        Assert.Contains("not armed", refused!.Reason, StringComparison.Ordinal);
    }

    // ----- dangerous constants ---------------------------------------------------

    [Fact]
    public void ADangerousSettingIsRefusedWithoutConfirmation()
    {
        // "revLimit" matches DangerousRole.RevLimiter.
        MainViewModel vm = Connected(out FakeController board);

        AgentRefusal? refused = Bridge(vm).SetSetting("revLimit", 6600, "raising it for a dyno pull");

        Assert.NotNull(refused);
        Assert.Contains("confirmDangerous", refused!.Reason, StringComparison.Ordinal);
        Assert.Equal(0x19, board.Page[2]);
        Assert.Equal(0x64, board.Page[3]);
    }

    [Fact]
    public void ItGoesThroughOnceConfirmed()
    {
        MainViewModel vm = Connected(out FakeController board);

        AgentRefusal? refused = Bridge(vm).SetSetting(
            "revLimit", 6600, "raising it for a dyno pull", confirmDangerous: true);

        Assert.Null(refused);

        // 6600 rpm, big-endian, at the offset the firmware declares.
        Assert.Equal(0x19, board.Page[2]);
        Assert.Equal(0xC8, board.Page[3]);
    }

    [Fact]
    public void AnOrdinarySettingNeedsNoConfirmation()
    {
        MainViewModel vm = Connected(out _);

        Assert.Null(Bridge(vm).SetSetting("crankingRPM", 310, "small nudge"));
    }

    // ----- magnitude -------------------------------------------------------------

    [Fact]
    public void AChangeOverHalfTheDeclaredRangeIsRefused()
    {
        // crankingRPM's declared range is 0-10000; half of that is 5000, and
        // 300 to 6000 moves it by 5700.
        MainViewModel vm = Connected(out FakeController board);

        AgentRefusal? refused = Bridge(vm).SetSetting("crankingRPM", 6000, "a very large change");

        Assert.NotNull(refused);
        Assert.Contains("too large", refused!.Reason, StringComparison.Ordinal);
        Assert.Equal(0x01, board.Page[0]);
        Assert.Equal(0x2C, board.Page[1]);
    }

    [Fact]
    public void ARefusedMagnitudeLeavesNothingPendingBehindIt()
    {
        // A refusal must not silently arm the button for the next call to send.
        MainViewModel vm = Connected(out _);

        Bridge(vm).SetSetting("crankingRPM", 6000, "a very large change");

        Assert.False(vm.HasSettingChanges);
    }

    [Fact]
    public void AChangeWithinHalfTheRangeGoesThrough()
    {
        MainViewModel vm = Connected(out FakeController board);

        Assert.Null(Bridge(vm).SetSetting("crankingRPM", 3000, "a real tuning step"));

        Assert.Equal(0x0B, board.Page[0]);
        Assert.Equal(0xB8, board.Page[1]);
    }

    [Fact]
    public void ATableCellsMagnitudeIsJudgedAgainstTheFirmwaresDeclaredRange()
    {
        // The VE table's cells are declared 0-255; half of that is 127.5, so a
        // jump from nought to 200 is refused.
        MainViewModel vm = Connected(out _);

        AgentRefusal? refused = Bridge(vm).SetTableCell("VE Table", 0, 0, 200, "a big jump").Refusal;

        Assert.NotNull(refused);
        Assert.Contains("too large", refused!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASmallerTableCellChangeGoesThrough()
    {
        MainViewModel vm = Connected(out _);

        Assert.Null(Bridge(vm).SetTableCell("VE Table", 0, 0, 60, "smoothing a cell").Refusal);
    }

    // ----- rate limit --------------------------------------------------------------

    [Fact]
    public void TenWritesGoThroughAndTheEleventhIsRefused()
    {
        MainViewModel vm = Connected(out _);
        AgentBridge bridge = Bridge(vm);

        for (int i = 0; i < 10; i++)
        {
            // Starts at 301 rather than 300 (the value already on the
            // controller), and moves by one each time, so every call is a
            // real change rather than a no-op the ECU would refuse to send.
            AgentRefusal? refused = bridge.SetSetting("crankingRPM", 301 + i, $"step {i}");
            Assert.Null(refused);
        }

        AgentRefusal? eleventh = bridge.SetSetting("crankingRPM", 320, "one too many, too soon");

        Assert.NotNull(eleventh);
        Assert.Contains("too many writes", eleventh!.Reason, StringComparison.Ordinal);
    }

    // ----- RPM -----------------------------------------------------------------

    [Fact]
    public void AWriteIsRefusedWhileTheEngineIsRunningAboveIdle()
    {
        // The realtime bytes are set before connecting, rather than after,
        // so the very first poll already reports 3000 rpm -- otherwise the
        // live session's first sample or two would still be the fake's
        // all-noughts default and the test would race against its own poll
        // loop.
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.WriteDefinition(vm, "test.ini", Firmware);

        var board = new FakeController(Signature);
        board.Page[0] = 0x01;
        board.Page[1] = 0x2C;   // crankingRPM = 300
        board.Page[2] = 0x19;
        board.Page[3] = 0x64;   // revLimit = 6500

        // 3000 rpm, big-endian, at the offset the output-channel block
        // declares for "rpm".
        board.Realtime = [0x0B, 0xB8, 0, 0, 0, 0, 0, 0];

        vm.Connect(board, "COM-TEST");
        vm.AgentWritesArmed = true;

        WaitForALiveSample(vm);

        AgentRefusal? refused = Bridge(vm).SetSetting("crankingRPM", 310, "small nudge");

        Assert.NotNull(refused);
        Assert.Contains("running above idle", refused!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteGoesThroughAtIdle()
    {
        MainViewModel vm = Connected(out _);

        // All noughts, which is the fake's default: an idling or stationary engine.
        WaitForALiveSample(vm);

        Assert.Null(Bridge(vm).SetSetting("crankingRPM", 310, "small nudge"));
    }

    [Fact]
    public void AWriteIsNotRefusedForRpmWhenThereIsNoLiveDocumentAtAll()
    {
        // No RefreshLive has ever been called, so there is no document to read
        // RPM off at all -- this must not be mistaken for "the engine is
        // running", or every write over a fresh connection would be refused.
        MainViewModel vm = Connected(out _);

        Assert.Null(Bridge(vm).SetSetting("crankingRPM", 310, "small nudge"));
    }

    [Fact]
    public void ASavedLogOpenedWhileConnectedDoesNotDecideWhetherTheEngineIsRunning()
    {
        // Opening a file does not end the session, so for as long as the guard
        // read the window's current document it was judging the engine by the
        // last row of somebody's old log. A file ending at 6,000 rpm must not
        // block a write to a controller that is sitting at idle.
        MainViewModel vm = Connected(out _);
        WaitForALiveSample(vm);

        vm.Load(_harness.WriteCsv(("RPM", [6000, 6000, 6000])));

        Assert.Null(Bridge(vm).SetSetting("crankingRPM", 310, "small nudge"));
    }

    // ----- a batch counts as one action against the rate limit ------------------

    [Fact]
    public void ABatchLargerThanTheRateLimitStillAppliesWholly()
    {
        // The limit exists to catch an agent looping, not one deliberate change
        // that happens to move a lot of cells. Counting the items would leave
        // the first ten applied and the rest refused -- half a table row moved
        // on a running engine, which is the one outcome nobody asked for.
        MainViewModel vm = Connected(out FakeController board);
        WaitForALiveSample(vm);

        ProposedCell[] wholeTable =
        [
            new("VE Table", 0, 0, 41), new("VE Table", 1, 0, 42),
            new("VE Table", 0, 1, 43), new("VE Table", 1, 1, 44),
        ];

        // Twelve settings and four cells: sixteen items, well past the ten a
        // per-item count would allow.
        ProposedSetting[] many = [.. Enumerable.Range(0, 12).Select(i => new ProposedSetting("crankingRPM", 300 + i))];

        TuneApplyResult result = Bridge(vm).ApplyTune(many, wholeTable, "filling the table");

        Assert.Null(result.Refusal);
        Assert.DoesNotContain(result.Rejected, r => r.Contains("too many writes", StringComparison.Ordinal));

        // Every cell of the table landed, not just the ones before the tenth write.
        Assert.Equal(41, board.Page[16]);
        Assert.Equal(42, board.Page[17]);
        Assert.Equal(43, board.Page[18]);
        Assert.Equal(44, board.Page[19]);
    }

    [Fact]
    public void ButLoopingTheBatchEndpointStillTripsTheRateLimit()
    {
        // The batch standing the per-item limit down must not stand the limit
        // itself down: an agent calling apply in a loop is exactly what this
        // catches, and each call spends one of the ten.
        MainViewModel vm = Connected(out _);
        WaitForALiveSample(vm);

        AgentBridge bridge = Bridge(vm);
        TuneApplyResult? last = null;

        for (int i = 0; i < 12; i++)
            last = bridge.ApplyTune([new ProposedSetting("crankingRPM", 300 + i)], [], $"nudge {i}");

        Assert.NotNull(last!.Refusal);
        Assert.Contains("too many writes", last.Refusal!.Reason, StringComparison.Ordinal);
    }
}
