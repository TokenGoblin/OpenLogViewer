using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// A table cell holding a value outside its own declared range — a firmware
/// "disabled" sentinel — stays writable after it has been nudged away from.
///
/// Through the agent's <c>/table/set</c> path against a real connection,
/// because that is where it went wrong: every call selects the table, which
/// builds a new edit from what the ECU now holds, so an edit's own original
/// no longer remembers the sentinel by the second call.
/// </summary>
public class TableSentinelTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private const string Signature = "TEST Format 0001.00";

    // VE Table is declared 0-250 and its cell (0, 0) holds 255, just past the
    // range. Dwell map is declared 0-8 ms and its (0, 0) holds 25.5, far past
    // it - the shape of the real case on a bench Speeduino.
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
           veTable     = array,  U08, 16, [2x2], "%", 1, 0, 0, 250, 0
           rpmBins     = array,  U08, 20, [2],   "rpm", 100, 0, 0, 25500, 0
           mapBins     = array,  U08, 22, [2],   "kPa", 1, 0, 0, 255, 0
           dwellTable  = array,  U08, 24, [2x2], "ms", 0.1, 0, 0, 8, 1
           dwellRpm    = array,  U08, 28, [2],   "rpm", 100, 0, 0, 25500, 0
           dwellVolts  = array,  U08, 30, [2],   "V", 1, 0, 0, 255, 0

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
           table = dwellTbl, dwellMap, "Dwell map", 1
              xBins = dwellRpm, rpm
              yBins = dwellVolts, clt
              zBins = dwellTable
        """;

    private MainViewModel Connected(out FakeController board)
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.WriteDefinition(vm, "test.ini", Firmware);

        board = new FakeController(Signature);
        board.Page[0] = 0x01;
        board.Page[1] = 0x2C;   // crankingRPM = 300, so the tune is not all noughts
        board.Page[16] = 255;   // VE Table (0, 0): the sentinel
        board.Page[17] = 200;
        board.Page[18] = 50;
        board.Page[19] = 60;
        board.Page[24] = 255;   // Dwell map (0, 0): 25.5 ms against a declared 0-8

        vm.Connect(board, "COM-TEST");
        vm.AgentWritesArmed = true;

        return vm;
    }

    [Fact]
    public void ASentinelNudgedAwayFromCanBeWrittenBack()
    {
        MainViewModel vm = Connected(out FakeController board);
        var bridge = new AgentBridge(vm);

        AgentCellWrite away = bridge.SetTableCell("VE Table", 0, 0, 250, "nudging off the sentinel");

        Assert.Null(away.Refusal);
        Assert.Equal(250, board.Page[16]);

        AgentCellWrite back = bridge.SetTableCell("VE Table", 0, 0, 255, "putting the sentinel back");

        Assert.Null(back.Refusal);
        Assert.Equal(255, back.Value);
        Assert.Equal(255, board.Page[16]);
    }

    [Fact]
    public void AnOutOfRangeValueTheCellNeverHeldIsStillClamped()
    {
        MainViewModel vm = Connected(out FakeController board);

        // (1, 0) held 200, never 255 - close enough that the magnitude guard
        // lets it through, so what is being checked is the clamp.
        AgentCellWrite write = new AgentBridge(vm).SetTableCell("VE Table", 1, 0, 255, "out of range");

        Assert.Equal(250, write.Value);
        Assert.Equal(250, board.Page[17]);
    }

    [Fact]
    public void ASentinelFarOutOfRangeCanBeRestoredPastTheMagnitudeLimit()
    {
        MainViewModel vm = Connected(out FakeController board);
        var bridge = new AgentBridge(vm);

        // Asking for 24.5 lands on 8, the top of the declared range.
        AgentCellWrite away = bridge.SetTableCell("Dwell map", 0, 0, 24.5, "nudging off the sentinel");

        Assert.Null(away.Refusal);
        Assert.Equal(80, board.Page[24]);

        // 8 to 25.5 is far more than half of 0-8. Allowed only because 25.5 is
        // exactly what the cell held.
        AgentCellWrite back = bridge.SetTableCell("Dwell map", 0, 0, 25.5, "putting the sentinel back");

        Assert.Null(back.Refusal);
        Assert.Equal(255, board.Page[24]);
    }

    [Fact]
    public void ANearbyValueIsStillHeldToTheMagnitudeLimit()
    {
        MainViewModel vm = Connected(out FakeController board);
        var bridge = new AgentBridge(vm);

        bridge.SetTableCell("Dwell map", 0, 0, 24.5, "nudging off the sentinel");

        // 25.4 is not what the cell held, so the jump from 8 is judged as usual.
        AgentCellWrite write = bridge.SetTableCell("Dwell map", 0, 0, 25.4, "not quite the sentinel");

        Assert.NotNull(write.Refusal);
        Assert.Contains("too large", write.Refusal!.Reason, StringComparison.Ordinal);
        Assert.Equal(80, board.Page[24]);
    }
}
