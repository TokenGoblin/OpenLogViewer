using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// A connect that fails partway must not leave the port open. Only the
/// no-definition exit used to close it; a throw anywhere after a definition
/// matched and before the live session took the port left it held until a
/// finalizer ran, and the next attempt on the same COM port could not have it.
/// </summary>
public class ConnectFailureTests : IDisposable
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

        [OutputChannels]
        ochBlockSize = 8
        ochGetCommand = "r\x00\x07%2o%2c"
           rpm  = scalar, U16, 0, "rpm", 1, 0

        [Datalog]
           entry = rpm, "RPM", int, "%d"
        """;

    /// <summary>
    /// A real fake controller — it answers the identity, so the definition
    /// matches — that times out the first time the tune is asked for. A serial
    /// read timing out is ordinary, and the tune read does not catch it.
    /// </summary>
    private sealed class TimesOutOnTheTune(FakeController inner) : IEcuTransport
    {
        public bool IsOpen => inner.IsOpen;

        public void Open() => inner.Open();

        public void Close() => inner.Close();

        public void Write(ReadOnlySpan<byte> data)
        {
            // Framed as <length:2><payload>; a tune page read is 'r' for any
            // page but 7, which is the realtime block.
            if (data.Length >= 7 && data[2] == (byte)'r' && data[4] != 7)
                throw new TimeoutException("The operation has timed out.");

            inner.Write(data);
        }

        public int Read(Span<byte> buffer, TimeSpan timeout) => inner.Read(buffer, timeout);

        public void DiscardInput() => inner.DiscardInput();

        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public void AFailureAfterTheDefinitionMatchesClosesThePort()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.WriteDefinition(vm, "test.ini", Firmware);

        var board = new FakeController(Signature);
        var port = new TimesOutOnTheTune(board);

        Assert.Throws<TimeoutException>(() => vm.Connect(port, "COM-TEST"));

        Assert.False(port.IsOpen);
        Assert.False(vm.IsLive);
    }
}
