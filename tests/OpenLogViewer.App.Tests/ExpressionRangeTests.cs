using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// A setting whose limits and units are expressions, the way rusEFI declares
/// 58 of them, seen through the agent API. Found on a real rusEFI:
/// <c>/tune/full</c> reported them with no range and with the expression's own
/// source as their units, because it listed the definition's constants rather
/// than the tune's resolved ones.
/// </summary>
public class ExpressionRangeTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private const string Signature = "TEST Format 0001.00";

    private const string Firmware = $$"""
        [MegaTune]
           signature = "{{Signature}}"

        [Constants]
        page = 1
        nPages = 1
        pageSize = 32
        pageIdentifier = "\$tsCanId\x01"
        pageReadCommand = "r%2i%2o%2c"
        pageChunkWrite  = "w%2i%2o%2c%v"
        burnCommand     = "b%2i"
           crankingRPM          = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
           useMetricOnInterface = bits, U32, 12, [1:1], "Imperial", "Metric"
           maxAcClt = scalar, S16, 20, {bitStringValue(unitsLabels, useMetricOnInterface)}, {useMetricOnInterface ? 1 : 1.8}, {useMetricOnInterface ? 0 : 17.77777}, {useMetricOnInterface ? 0 : 32}, {useMetricOnInterface ? 250 : 250 * 1.8 + 32}, 0

        [PcVariables]
           unitsLabels = bits, U08, [0:2], "F" , "C"

        [OutputChannels]
        ochBlockSize = 8
        ochGetCommand = "r\x00\x07%2o%2c"
           rpm  = scalar, U16, 0, "rpm", 1, 0

        [Datalog]
           entry = rpm, "RPM", int, "%d"
        """;

    private MainViewModel Connected(out FakeController board)
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.WriteDefinition(vm, "test.ini", Firmware);

        board = new FakeController(Signature);
        board.Page[0] = 0x01;
        board.Page[1] = 0x2C;   // crankingRPM = 300, so the tune is not all noughts
        board.Page[15] = 0x02;  // useMetricOnInterface, bit 1 of a big-endian U32: metric
        board.Page[20] = 0;     // maxAcClt = 240 C, big-endian
        board.Page[21] = 240;

        vm.Connect(board, "COM-TEST");

        return vm;
    }

    [Fact]
    public void TheFullTuneReportsTheWorkedOutRangeAndUnits()
    {
        MainViewModel vm = Connected(out _);

        AgentSetting setting = Assert.Single(
            new AgentBridge(vm).TuneFull().Settings, s => s.Name == "maxAcClt");

        Assert.Equal("C", setting.Units);
        Assert.Equal(0, setting.Low);
        Assert.Equal(250, setting.High);
    }

    [Fact]
    public void AndAWriteIsHeldToIt()
    {
        MainViewModel vm = Connected(out _);

        AgentRefusal? refused = new AgentBridge(vm).SetSetting("maxAcClt", 260, "a small step, but past the top of 0-250");

        Assert.NotNull(refused);
    }
}
