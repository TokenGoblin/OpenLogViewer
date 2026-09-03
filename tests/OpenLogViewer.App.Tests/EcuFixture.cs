namespace OpenLogViewer.App.Tests;

/// <summary>
/// A view model attached to a fake controller, shared by the tests that need one.
///
/// <para>
/// Extracted from <see cref="ConnectedEcuTests"/> when the write confirmation
/// grew its own tests: both files need a controller that answers, and a second
/// copy of a firmware definition is a second thing to keep in step.
/// </para>
/// </summary>
internal static class EcuFixture
{
    public const string Signature = "TEST Format 0001.00";

    /// <summary>
    /// A page holding a number, a text field and a table, which between them are
    /// the three things anything here can be asked to write.
    /// </summary>
    public const string Firmware = $"""
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
    public static MainViewModel Connected(
        ViewModelHarness harness, out FakeController board, string? tune = null)
    {
        MainViewModel vm = harness.NewViewModel(out _);
        harness.WriteDefinition(vm, "test.ini", Firmware);

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
}
