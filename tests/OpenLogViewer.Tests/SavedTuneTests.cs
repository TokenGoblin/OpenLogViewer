using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Saved tunes: reading an <c>.msq</c>, laying it over a firmware definition,
/// writing one back out, and saying what two tunes disagree about.
/// </summary>
public class SavedTuneTests
{
    /// <summary>
    /// A small firmware: a scalar, a scaled one, a bit field whose padding
    /// repeats a label, a text field and a table.
    /// </summary>
    private const string Ini = """
        #define pins = "Off", "INVALID", "Out A", "INVALID"

        [Constants]
        page = 1
        nPages = 1
        pageSize = 64
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
           crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
           dwell       = scalar, U08, 2, "ms", 0.0666, 0, 0, 12, 1
           coolant     = scalar, U08, 3, "F", 1.8, -22.23, -40, 215, 0
           pulseWidth  = scalar, U08, 6, "ms", 0.02, 0, 0, 5, 1
           fanPin      = bits,   U08, 4, [0:1], $pins
           fanOn       = bits,   U08, 4, [2:2], "No", "Yes"
           alias       = string, ASCII, 8, 6
           veTable     = array,  U08, 16, [2x2], "%", 0.5, 0, 0, 120, 1
        """;

    private static TuneLayout Layout() => TuneLayoutReader.Read(Ini);

    private static EcuTune Tune(params (string Name, double Value)[] settings)
    {
        TuneLayout layout = Layout();
        var tune = EcuTune.FromPages(layout, new byte[64]);

        foreach ((string name, double value) in settings)
            Assert.True(tune.PokeInto(tune.Pages, layout.Constants.Single(c => c.Name == name), 0, value));

        return tune;
    }

    // ----- reading a file ---------------------------------------------------

    [Fact]
    public void ATuneCarriesTheSymbolsOfTheBuildItCameFrom()
    {
        // The reason this matters: the same definition describes a Fahrenheit
        // build and a Celsius one, and the tune is the only thing that knows
        // which. Reading it with the wrong ones scales every temperature.
        MsqFile file = MsqFile.Read("""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <msq xmlns="http://www.msefi.com/:msq">
            <versionInfo fileFormat="5.0" nPages="1" signature="MS2Extra comms342a2"/>
            <page number="0" size="64"><constant digits="0" name="crankingRPM">600.0</constant></page>
            <settings>
            <setting name="FAHRENHEIT" value="FAHRENHEIT"/>
            <setting name="CAN_COMMANDS" value="CAN_COMMANDS"/>
            </settings>
            </msq>
            """);

        Assert.Equal("MS2Extra comms342a2", file.Signature);
        Assert.Equal(1, file.PageCount);
        Assert.Contains("FAHRENHEIT", file.Symbols);
        Assert.Equal("600.0", file.Values["crankingRPM"].Trim());
    }

    [Fact]
    public void TwoSettingsNamedTheSameButForTheirCaseAreTwoSettings()
    {
        // MS2Extra really does this: MAFFlow is a twelve-point flow curve on one
        // page and mafflow a sixty-four-point one on another, and its own saved
        // tunes store both. Merging them loses one.
        MsqFile file = MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="8">
            <constant name="MAFFlow">1.0</constant>
            <constant name="mafflow">2.0</constant>
            </page>
            </msq>
            """);

        Assert.Equal(2, file.Values.Count);
        Assert.Equal("1.0", file.Value("MAFFlow")!.Trim());
        Assert.Equal("2.0", file.Value("mafflow")!.Trim());
    }

    [Fact]
    public void ALooseMatchStillFindsASettingSpelledDifferently()
    {
        // Most firmwares are not careful about case, and a name that used to
        // resolve must keep resolving.
        MsqFile file = MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="8"><constant name="CrankingRPM">600.0</constant></page>
            </msq>
            """);

        Assert.Equal("600.0", file.Value("crankingrpm")!.Trim());
    }

    [Fact]
    public void SomethingThatIsNotATuneIsRefusedRatherThanReadAsAnEmptyOne()
    {
        Assert.Throws<LogFormatException>(() => MsqFile.Read("<html><body>not a tune</body></html>"));
        Assert.Throws<LogFormatException>(() => MsqFile.Read("this is not xml at all <<<"));
    }

    // ----- laying it over a definition --------------------------------------

    [Fact]
    public void ValuesComeBackAsTheFileStatedThem()
    {
        MsqLoad load = MsqApply.Load(Layout(), MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="64">
            <constant name="crankingRPM">600.0</constant>
            <constant name="dwell">3.7296</constant>
            <constant name="coolant">68.0</constant>
            <constant name="fanPin">"Out A"</constant>
            <constant name="fanOn">"Yes"</constant>
            <constant name="alias">"CLT"</constant>
            <constant name="pulseWidth">1.12</constant>
            <constant cols="2" name="veTable" rows="2"> 40.0 41.0
             42.0 43.0 </constant>
            </page>
            </msq>
            """));

        EcuTune tune = load.Tune;

        Assert.True(load.IsComplete, load.Summary);
        Assert.Equal(600, tune.Scalar("crankingRPM"));
        Assert.Equal(3.7296, tune.Scalar("dwell"), 4);
        // Half a degree out, which is all a byte can hold: 68 °F is a raw 60.01
        // and the byte keeps 60.
        Assert.Equal(68.0, tune.Scalar("coolant"), 1);
        Assert.Equal(2, tune.Scalar("fanPin"));
        Assert.Equal(1, tune.Scalar("fanOn"));
        Assert.Equal("CLT", tune.TextIn(tune.Pages, "alias"));
        Assert.Equal([40.0, 41.0, 42.0, 43.0], tune.Array("veTable"));
    }

    [Fact]
    public void ASettingTheFileNeverMentionsIsReportedRatherThanLeftAtZero()
    {
        // The dangerous case, and the reason the report exists: a page of zeros
        // looks exactly like a page of settings, and sending one writes them.
        MsqLoad load = MsqApply.Load(Layout(), MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="64"><constant name="crankingRPM">600.0</constant></page>
            </msq>
            """));

        Assert.False(load.IsComplete);
        Assert.Contains(load.Missing, m => m.Name == "dwell");
        Assert.Equal(1, load.Applied);
        Assert.True(load.LooksLikeAnotherFirmware);
    }

    [Fact]
    public void AnOptionTheFirmwareDoesNotOfferIsRefusedRatherThanGuessedAt()
    {
        MsqLoad load = MsqApply.Load(Layout(), MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="64"><constant name="fanPin">"Out Z"</constant></page>
            </msq>
            """));

        Assert.Contains(load.Rejected, r => r.Name == "fanPin");
    }

    [Fact]
    public void ATableOfTheWrongShapeIsRefused()
    {
        MsqLoad load = MsqApply.Load(Layout(), MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="64">
            <constant cols="2" name="veTable" rows="2"> 40.0 41.0 42.0 </constant>
            </page>
            </msq>
            """));

        Assert.Contains(load.Rejected, r => r.Name == "veTable" && r.Reason.Contains('3'));
    }

    [Fact]
    public void NamesInTheFileThatThisFirmwareLacksAreCountedNotApplied()
    {
        MsqLoad load = MsqApply.Load(Layout(), MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="64">
            <constant name="crankingRPM">600.0</constant>
            <constant name="somethingElseEntirely">1.0</constant>
            </page>
            </msq>
            """));

        Assert.Contains("somethingElseEntirely", load.Unknown);
    }

    // ----- writing one back out ---------------------------------------------

    [Fact]
    public void ATuneSurvivesBeingWrittenAndReadBack()
    {
        EcuTune before = Tune(("crankingRPM", 600), ("dwell", 3.7296), ("coolant", 68), ("fanOn", 1));

        MsqLoad after = MsqApply.Load(
            Layout(), MsqFile.Read(MsqWriter.Write(before, "test firmware")));

        Assert.True(after.IsComplete, after.Summary);
        Assert.Equal(before.Pages[0], after.Tune.Pages[0]);
    }

    [Fact]
    public void ANumberIsWrittenWithEnoughDecimalsToGetTheSameByteBack()
    {
        // This one is stored in hundredths and asks for one decimal, so a raw 56
        // is 1.12 ms shown as "1.1" — and 1.1 read back is a raw 55. Writing
        // what is displayed rather than what is stored moves the setting a step
        // every time a tune goes through a file, which is drift nobody would
        // attribute to the tool. It was found on a real MS2 tune, where two
        // bytes of 7,168 came back one out.
        EcuTune tune = Tune(("pulseWidth", 1.12));

        Assert.Equal(56, tune.Pages[0][6]);

        string xml = MsqWriter.Write(tune, "test firmware");

        Assert.Contains("1.12", xml, StringComparison.Ordinal);
        Assert.Equal(56, MsqApply.Load(Layout(), MsqFile.Read(xml)).Tune.Pages[0][6]);
    }

    [Fact]
    public void ASettingThatNeedsNoMoreDecimalsGetsNoMore()
    {
        // The search stops at the firmware's own digits where those suffice, so
        // an ordinary file still reads like an ordinary file.
        Assert.Contains(">600.0<", MsqWriter.Write(Tune(("crankingRPM", 600)), "test firmware"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionLabelSharedWithAnotherValueIsWrittenAsItsNumber()
    {
        // Firmwares pad an option list out to the width of the field, so the
        // same word repeats: this one has "INVALID" at 1 and at 3. An
        // unconfigured pin really does read 3, and writing the word would
        // restore it as pin 1 — a real output, quietly assigned.
        EcuTune tune = Tune(("fanPin", 3));

        string xml = MsqWriter.Write(tune, "test firmware");

        Assert.DoesNotContain("\"INVALID\"", xml, StringComparison.Ordinal);
        Assert.Equal(3, MsqApply.Load(Layout(), MsqFile.Read(xml)).Tune.Scalar("fanPin"));
    }

    [Fact]
    public void AnOptionLabelThatNamesOnlyItselfIsWrittenAsTheLabel()
    {
        string xml = MsqWriter.Write(Tune(("fanPin", 2)), "test firmware");

        Assert.Contains("\"Out A\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignatureAndTheSymbolsAreWrittenSoTheFileCanBeReadBack()
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal) { "FAHRENHEIT", "CAN_COMMANDS" };

        MsqFile back = MsqFile.Read(MsqWriter.Write(Tune(), "speeduino 202501", symbols));

        Assert.Equal("speeduino 202501", back.Signature);
        Assert.Equal(["CAN_COMMANDS", "FAHRENHEIT"], back.Symbols.Order());
    }

    [Fact]
    public void LayingAFileOverAnEcuLeavesBitsNoConstantDeclaresAlone()
    {
        // A definition does not declare every bit it has. On a Speeduino that is
        // 81 bytes of 3,408, and starting a restore from zero would clear them.
        var controller = EcuTune.FromPages(Layout(), new byte[64]);
        controller.Pages[0][5] = 0xFF;                 // no constant is declared here
        controller.Pages[0][4] = 0b1000_0111;          // bit 7 belongs to nothing either

        string xml = MsqWriter.Write(Tune(("fanPin", 3), ("fanOn", 1)), "test firmware");
        MsqLoad load = MsqApply.Load(Layout(), MsqFile.Read(xml), controller);

        Assert.Equal(0xFF, load.Tune.Pages[0][5]);
        Assert.Equal(0b1000_0000, load.Tune.Pages[0][4] & 0b1000_0000);
        Assert.Equal(3, load.Tune.Scalar("fanPin"));
    }

    // ----- comparing two ------------------------------------------------------

    [Fact]
    public void ATuneComparedWithItselfDiffersInNothing()
    {
        Assert.Empty(TuneCompare.Compare(Tune(("crankingRPM", 600)), Tune(("crankingRPM", 600))));
    }

    [Fact]
    public void OneSettingMovedIsNamedWithBothValues()
    {
        TuneDifference difference = Assert.Single(
            TuneCompare.Compare(Tune(("crankingRPM", 400)), Tune(("crankingRPM", 300))));

        Assert.Equal("crankingRPM", difference.Name);
        Assert.Equal(400, difference.Mine);
        Assert.Equal(300, difference.Theirs);
        Assert.Contains("400 rpm", difference.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableSaysHowManyOfItsCellsDiffer()
    {
        // "3 of 256" and "all 256" are different situations, and the first
        // values alone do not tell them apart.
        TuneLayout layout = Layout();
        TuneConstant ve = layout.Constants.Single(c => c.Name == "veTable");

        var mine = EcuTune.FromPages(layout, new byte[64]);
        var theirs = EcuTune.FromPages(layout, new byte[64]);

        for (int i = 0; i < 4; i++) mine.PokeInto(mine.Pages, ve, i, 40);
        for (int i = 0; i < 4; i++) theirs.PokeInto(theirs.Pages, ve, i, i == 2 ? 45 : 40);

        TuneDifference difference = Assert.Single(TuneCompare.Compare(mine, theirs));

        Assert.Equal(1, difference.Cells);
        Assert.True(difference.IsArray);
        Assert.Contains("1 of 4 cells", difference.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ABitFieldIsComparedByItsOptionNames()
    {
        TuneDifference difference = Assert.Single(
            TuneCompare.Compare(Tune(("fanOn", 1)), Tune(("fanOn", 0))));

        Assert.Equal("Yes", difference.MineShown);
        Assert.Equal("No", difference.TheirsShown);
    }
}
