using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class EcuTuneTests
{
    /// <summary>A small firmware definition with the shape of a real one.</summary>
    private const string Ini = """
        [Constants]
        endianness     = little
        nPages         = 1
        pageSize       = 64
        pageIdentifier = "\x00\x00"
        pageReadCommand = "R%2o%2c"
        blockingFactor = 16

        page = 1
        rpmHardLimit = scalar, U16, 0, "rpm", 1, 0, 0, 20000, 0
        displacement = scalar, F32, 4, "L", 1, 0, 0, 10, 3
        twoStroke    = bits,   U08, 8, [0:0], "four", "two"
        veTable      = array,  U08, 16, [4x3], "%", 0.5, 0, 0, 999, 1
        veRpmBins    = array,  U16, 32, [4], "RPM", 1, 0, 0, 18000, 0
        veLoadBins   = array,  U08, 40, [3], "kPa", 1, 0, 0, 255, 0

        [TableEditor]
        table = veTableTbl, veTableMap, "VE Table", 1
          xBins = veRpmBins, RPMValue
          yBins = veLoadBins, veTableYAxis
          zBins = veTable
        table = brokenTbl, brokenMap, "Missing Its Axes", 1
          xBins = noSuchThing, RPMValue
          yBins = veLoadBins, veTableYAxis
          zBins = veTable
        """;

    private static TuneLayout Layout() => TuneLayoutReader.Read(Ini);

    /// <summary>A page image with known values at the declared offsets.</summary>
    private static byte[] Page()
    {
        var page = new byte[64];

        BitConverter.GetBytes((ushort)5500).CopyTo(page, 0);
        BitConverter.GetBytes(3.378f).CopyTo(page, 4);
        page[8] = 0b0000_0001;

        // 4 columns by 3 rows, row-major, at half a percent each.
        byte[] cells = [20, 40, 60, 80, 100, 120, 140, 160, 180, 200, 220, 240];
        cells.CopyTo(page, 16);

        BitConverter.GetBytes((ushort)600).CopyTo(page, 32);
        BitConverter.GetBytes((ushort)2000).CopyTo(page, 34);
        BitConverter.GetBytes((ushort)4000).CopyTo(page, 36);
        BitConverter.GetBytes((ushort)6000).CopyTo(page, 38);

        page[40] = 30;
        page[41] = 60;
        page[42] = 90;

        return page;
    }

    private static EcuTune Tune() => EcuTune.FromPages(Layout(), Page());

    // ----- the layout -------------------------------------------------------

    [Fact]
    public void ThePageIsReadWithItsSizeCommandAndIdentifier()
    {
        TunePage page = Assert.Single(Layout().Pages);

        Assert.Equal(64, page.Size);
        Assert.Equal("R%2o%2c", page.ReadCommand);
        Assert.Equal(@"\x00\x00", page.Identifier);
    }

    [Fact]
    public void PagesAreNumberedFromZeroEvenThoughTheFileCountsFromOne()
    {
        // "page = 1" introduces the first page. Reading it as index 1 puts every
        // constant on a page that does not exist.
        Assert.All(Layout().Constants, c => Assert.Equal(0, c.Page));
    }

    [Fact]
    public void TheByteOrderAndBlockingFactorComeFromTheSameSection()
    {
        TuneLayout layout = Layout();

        Assert.True(layout.LittleEndian);
        Assert.Equal(16, layout.BlockingFactor);
    }

    [Fact]
    public void ScalarsArraysAndBitFieldsAreAllRead()
    {
        IReadOnlyList<TuneConstant> constants = Layout().Constants;

        Assert.Equal(6, constants.Count);
        Assert.False(Named(constants, "rpmHardLimit").IsArray);
        Assert.True(Named(constants, "twoStroke").IsBitField);

        TuneConstant ve = Named(constants, "veTable");
        Assert.True(ve.IsArray);
        Assert.Equal(4, ve.Columns);
        Assert.Equal(3, ve.Rows);
        Assert.Equal(12, ve.Size);
    }

    [Fact]
    public void NothingReachesPastTheEndOfItsPage()
    {
        TuneLayout layout = Layout();

        Assert.All(layout.Constants, c => Assert.True(c.Offset + c.Size <= layout.Pages[c.Page].Size));
    }

    // ----- decoding ---------------------------------------------------------

    [Fact]
    public void ScalarsDecodeWithTheirScale()
    {
        EcuTune tune = Tune();

        Assert.Equal(5500, tune.Scalar("rpmHardLimit"));
        Assert.Equal(3.378, tune.Scalar("displacement"), 4);
        Assert.Equal(1, tune.Scalar("twoStroke"));
    }

    [Fact]
    public void AnAbsentSettingIsNotANumber() => Assert.True(double.IsNaN(Tune().Scalar("nothingLikeThis")));

    [Fact]
    public void ArraysAreScaledToo()
    {
        double[] cells = Assert.IsType<double[]>(Tune().Array("veTable"));

        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120], cells);
    }

    [Fact]
    public void ABigEndianFirmwareDecodesTheOtherWayRound()
    {
        // Little-endian bytes 5500 read big-endian are 32021 — a number, and the
        // wrong one, which is why this comes from the INI.
        EcuTune tune = EcuTune.FromPages(Layout() with { LittleEndian = false }, Page());

        Assert.NotEqual(5500, tune.Scalar("rpmHardLimit"));
    }

    // ----- tables -----------------------------------------------------------

    [Fact]
    public void ATableComesBackWithItsAxesTheRightWayRound()
    {
        // The first dimension runs along X. Transposed, a square table is wrong
        // in a way nothing catches.
        TuneTable table = Assert.Single(Tune().Tables(TableEditorReader.Read(Ini)));

        Assert.Equal("VE Table", table.Name);
        Assert.Equal(4, table.Columns);
        Assert.Equal(3, table.Rows);

        Assert.Equal([600, 2000, 4000, 6000], table.X.Breakpoints);
        Assert.Equal([30, 60, 90], table.Y.Breakpoints);

        // Row-major in the page: row 0 is 10,20,30,40.
        Assert.Equal(10, table.Values[0, 0]);
        Assert.Equal(40, table.Values[3, 0]);
        Assert.Equal(90, table.Values[0, 2]);
        Assert.Equal(120, table.Values[3, 2]);
    }

    [Fact]
    public void ATableNamingSomethingAbsentIsLeftOutRatherThanAssembled()
    {
        // Two are declared; the second names an axis this firmware has not got.
        // Half a table is not a near miss.
        IReadOnlyList<TableDefinition> definitions = TableEditorReader.Read(Ini);

        Assert.Equal(2, definitions.Count);
        Assert.Single(Tune().Tables(definitions));
    }

    [Fact]
    public void TheAxisChannelsAreCarriedThrough()
    {
        // What the ECU indexes the table by, which is what a log has to be
        // binned against to reproduce its decisions.
        TableDefinition ve = definitions().First(d => d.LooksLikeVeTable);

        Assert.Equal("RPMValue", ve.XChannel);
        Assert.Equal("veTableYAxis", ve.YChannel);
        Assert.Equal("veTable", ve.Values);

        static IReadOnlyList<TableDefinition> definitions() => TableEditorReader.Read(Ini);
    }

    [Fact]
    public void TheTuneSuppliesTheContextGaugeScalesAreWrittenAgainst()
    {
        // The whole reason for reading it at connect: a rev counter runs to
        // {rpmHardLimit + 2000} and has no scale until something says what that
        // is.
        const string gauges = """
            [GaugeConfigurations]
            RPMGauge = RPMValue, "RPM", "RPM", 0, {rpmHardLimit + 2000}, 200, 350, {rpmHardLimit - 500}, {rpmHardLimit}, 0, 0
            """;

        IReadOnlyDictionary<string, double> context =
            TuningContext.Build(Ini, null, fromEcu: Tune().Scalars());

        GaugeSpec rpm = Assert.Single(GaugeCatalog.Read(gauges, context));

        Assert.True(rpm.HasScale);
        Assert.Equal(7500, rpm.High);
        Assert.Equal(5500, rpm.HighDanger);
    }

    [Fact]
    public void WhatTheEcuSaysBeatsWhatAFileSays()
    {
        // A saved tune can be stale; the controller cannot be.
        const string stale = """
            <?xml version="1.0"?>
            <msq><page><constant name="rpmHardLimit">3000</constant></page></msq>
            """;

        IReadOnlyDictionary<string, double> context =
            TuningContext.Build(Ini, stale, fromEcu: Tune().Scalars());

        Assert.Equal(5500, context["rpmHardLimit"]);
    }

    // ----- the request ------------------------------------------------------

    [Fact]
    public void APageIdentifierIsSubstitutedForItsPlaceholder()
    {
        // "%2i" is not a number but the page's own identifier bytes.
        byte[] identifier = RealtimeCommand.Parse(@"\$tsCanId\x04").Build(0, 1, canId: 2);
        byte[] request = RealtimeCommand.Parse("r%2i%2o%2c").Build(0, 1024, canId: 2, page: identifier);

        Assert.Equal<byte[]>([(byte)'r', 2, 0x04, 0, 0, 0x04, 0x00], request);
    }

    [Fact]
    public void ATemplateWithNoPlaceholderIsUnaffectedByAnIdentifier()
    {
        byte[] request = RealtimeCommand.Parse("R%2o%2c")
            .Build(0, 1024, littleEndian: true, page: [0xAA, 0xBB]);

        Assert.Equal<byte[]>([(byte)'R', 0, 0, 0x00, 0x04], request);
    }

    private static TuneConstant Named(IReadOnlyList<TuneConstant> constants, string name) =>
        Assert.Single(constants, c => c.Name == name);

    // ----- a scale the firmware works out for itself ---------------------------

    /// <summary>
    /// A Speeduino, in miniature. Its load axes are scaled by fuelLoadRes, which
    /// is not a setting at all but a line in [OutputChannels] choosing between
    /// two numbers depending on the fuel algorithm.
    /// </summary>
    private const string DerivedScaleIni = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 32
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageChunkWrite  = "w%2o%2c%v"
        burnCommand     = "b%2i"
           algorithm    = bits,  U08, 0, [0:2], "MAP", "TPS", "IMAP", "MAF"
           fuelLoadBins = array, U08, 4, [4], "kPa", {fuelLoadRes}, 0.0, 0.0, 511, 1

        [OutputChannels]
        ochBlockSize = 4
        ochGetCommand = "A"
           rpm = scalar, U16, 0, "rpm", 1, 0
           fuelLoadRes = { ((algorithm == 0) || (algorithm == 2)) ? 2.000 : 0.500 }
        """;

    [Fact]
    public void AScaleTheFirmwareDerivesIsWorkedOutRatherThanFallenBackFrom()
    {
        // Found on a real Speeduino: every load axis read at half, because the
        // scale is {fuelLoadRes} and the resolver only knew about settings. It
        // fell back to the declared 1 and TunerStudio used 2, so the same tune
        // looked like two — and restoring one would have doubled the axis.
        TuneLayout layout = TuneLayoutReader.Read(DerivedScaleIni);
        var tune = EcuTune.FromPages(layout, new byte[32]);

        // algorithm 0 is MAP, so the resolution is 2.
        tune.PokeInto(tune.Pages, tune.Constant("algorithm")!, 0, 0);
        tune.Rescale();

        Assert.Equal(2.0, tune.Constant("fuelLoadBins")!.Scale, 6);

        // A raw 5 therefore means 10, which is what TunerStudio shows.
        tune.Pages[0][4] = 5;
        Assert.Equal(10, tune.ValueIn(tune.Pages, "fuelLoadBins", 0));
    }

    [Fact]
    public void AndFollowsTheSettingItDependsOn()
    {
        TuneLayout layout = TuneLayoutReader.Read(DerivedScaleIni);
        var tune = EcuTune.FromPages(layout, new byte[32]);

        // algorithm 1 is TPS, so the other arm: half.
        tune.PokeInto(tune.Pages, tune.Constant("algorithm")!, 0, 1);
        tune.Rescale();

        Assert.Equal(0.5, tune.Constant("fuelLoadBins")!.Scale, 6);
    }

    [Fact]
    public void AndEncodesThroughItToo()
    {
        // The half that would have corrupted a controller. Writing 10 with the
        // scale resolved stores raw 5; with the fallback it stored raw 10.
        TuneLayout layout = TuneLayoutReader.Read(DerivedScaleIni);
        var tune = EcuTune.FromPages(layout, new byte[32]);

        tune.PokeInto(tune.Pages, tune.Constant("algorithm")!, 0, 0);
        tune.Rescale();

        Assert.True(tune.PokeInto(tune.Pages, tune.Constant("fuelLoadBins")!, 0, 10));
        Assert.Equal(5, tune.Pages[0][4]);
    }

    [Fact]
    public void ADerivedValueDefinedInTermsOfItselfDoesNotHang()
    {
        // A definition can say anything, and this has to survive it.
        string circular = DerivedScaleIni.Replace(
            "fuelLoadRes = { ((algorithm == 0) || (algorithm == 2)) ? 2.000 : 0.500 }",
            "fuelLoadRes = { fuelLoadRes + 1 }",
            StringComparison.Ordinal);

        TuneLayout layout = TuneLayoutReader.Read(circular);
        var tune = EcuTune.FromPages(layout, new byte[32]);

        tune.Rescale();

        // Falls back to the declared scale rather than looping.
        Assert.Equal(1.0, tune.Constant("fuelLoadBins")!.Scale, 6);
    }
}
