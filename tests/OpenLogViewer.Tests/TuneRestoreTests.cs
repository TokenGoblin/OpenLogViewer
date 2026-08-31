using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Working out what restoring a saved tune to a controller would do.
///
/// The largest thing this application can do to an engine, so the whole design
/// is that it is worked out and described before any of it happens.
/// </summary>
public class TuneRestoreTests
{
    private const string Ini = """
        [Constants]
        page = 1
        nPages = 2
        pageSize = 32, 32
        pageIdentifier = "\x01", "\x02"
        pageReadCommand = "r%2o%2c", "r%2o%2c"
        pageValueWrite  = "w%2o%2c%v", "w%2o%2c%v"
        burnCommand     = "b%2i", "b%2i"
           crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
           revLimit    = scalar, U16, 2, "rpm", 1, 0, 0, 10000, 0
           fanOn       = bits,   U08, 4, [0:0], "No", "Yes"

        page = 2
           idleTarget  = scalar, U16, 0, "rpm", 1, 0, 0, 3000, 0
           reserved    = scalar, U08, 4, "", 1, 0, 0, 255, 0
        """;

    private static TuneLayout Layout() => TuneLayoutReader.Read(Ini);

    private static EcuTune Tune(params (string Name, double Value)[] settings)
    {
        TuneLayout layout = Layout();
        var tune = EcuTune.FromPages(layout, new byte[32], new byte[32]);

        foreach ((string name, double value) in settings)
            Assert.True(tune.PokeInto(tune.Pages, tune.Constant(name)!, 0, value), name);

        return tune;
    }

    /// <summary>A saved tune stating exactly the settings named.</summary>
    private static MsqFile File_(string signature, params (string Name, string Value)[] settings)
    {
        string body = string.Join(
            "\n", settings.Select(s => $"<constant name=\"{s.Name}\">{s.Value}</constant>"));

        return MsqFile.Read($"""
            <msq xmlns="http://www.msefi.com/:msq">
            <versionInfo fileFormat="5.0" nPages="2" signature="{signature}"/>
            <page number="0" size="32">
            {body}
            </page>
            </msq>
            """);
    }

    // ----- what it would do ---------------------------------------------------

    [Fact]
    public void RestoringWhatTheEcuAlreadyHoldsDoesNothing()
    {
        // The property everything else rests on. If this wrote anything, every
        // restore would be writing bytes nobody asked it to.
        EcuTune ecu = Tune(("crankingRPM", 300), ("revLimit", 6500), ("idleTarget", 900));

        TuneRestorePlan plan = TuneRestore.Plan(
            ecu,
            File_("firmware", ("crankingRPM", "300.0"), ("revLimit", "6500.0"),
                              ("fanOn", "\"No\""), ("idleTarget", "900.0"), ("reserved", "0.0")));

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Writes);
        Assert.Empty(plan.Differences);
        Assert.Contains("already holds this tune", plan.Summary);
    }

    [Fact]
    public void OnlyTheSettingsThatDifferAreWritten()
    {
        EcuTune ecu = Tune(("crankingRPM", 300), ("revLimit", 6500), ("idleTarget", 900));

        TuneRestorePlan plan = TuneRestore.Plan(
            ecu,
            File_("firmware", ("crankingRPM", "300.0"), ("revLimit", "7000.0"),
                              ("fanOn", "\"No\""), ("idleTarget", "900.0"), ("reserved", "0.0")));

        TuneDifference difference = Assert.Single(plan.Differences);
        Assert.Equal("revLimit", difference.Name);
        Assert.Equal(7000, difference.Mine);
        Assert.Equal(6500, difference.Theirs);

        TuneWrite write = Assert.Single(plan.Writes);
        Assert.Equal(0, write.Page);
        Assert.Equal(2, write.Offset);
        Assert.Equal(2, write.Data.Length);
    }

    [Fact]
    public void ASettingTheFileNeverMentionedIsLeftAsTheEcuHasIt()
    {
        // The difference between a restore and a wrecked tune. A tune from a
        // neighbouring revision is missing a handful of constants, and writing
        // zeros over them because the file was silent is not restoring anything.
        EcuTune ecu = Tune(("crankingRPM", 300), ("revLimit", 6500), ("idleTarget", 900));

        TuneRestorePlan plan = TuneRestore.Plan(ecu, File_("firmware", ("crankingRPM", "400.0")));

        Assert.Contains(plan.Missing, m => m.Name == "revLimit");
        Assert.Contains(plan.Missing, m => m.Name == "idleTarget");

        // Only the one it named.
        Assert.Single(plan.Differences);
        Assert.Equal(6500, plan.Target.Scalar("revLimit"));
        Assert.Equal(900, plan.Target.Scalar("idleTarget"));
    }

    [Fact]
    public void ApplyingTheWritesLandsExactlyOnWhatWasPlanned()
    {
        TuneLayout layout = Layout();
        EcuTune ecu = Tune(("crankingRPM", 300), ("revLimit", 6500), ("idleTarget", 900));

        TuneRestorePlan plan = TuneRestore.Plan(
            ecu,
            File_("firmware", ("crankingRPM", "450.0"), ("revLimit", "7000.0"),
                              ("fanOn", "\"Yes\""), ("idleTarget", "1100.0"), ("reserved", "0.0")));

        var copy = EcuTune.FromPages(layout, [.. ecu.Pages.Select(p => p.ToArray())]);
        foreach (TuneWrite write in plan.Writes) copy.Accept(write);

        Assert.Equal(plan.Target.Pages[0], copy.Pages[0]);
        Assert.Equal(plan.Target.Pages[1], copy.Pages[1]);
        Assert.Empty(TuneCompare.Compare(copy, plan.Target));
    }

    [Fact]
    public void EveryPageThatWouldBeTouchedIsNamed()
    {
        EcuTune ecu = Tune(("crankingRPM", 300), ("idleTarget", 900));

        TuneRestorePlan plan = TuneRestore.Plan(
            ecu, File_("firmware", ("crankingRPM", "400.0"), ("idleTarget", "1000.0")));

        Assert.Equal([0, 1], plan.Pages);

        // Two bytes, not four. Both settings are sixteen bits and both changes
        // are small enough to leave the high byte alone — 300 to 400 rpm is
        // 0x012C to 0x0190 — so only the low byte of each is in a run. Writing
        // what differs rather than what was named is the whole point.
        Assert.Equal(2, plan.Bytes);
    }

    // ----- which firmware -----------------------------------------------------

    [Fact]
    public void AFileAndAControllerNamingDifferentFirmwaresIsFlagged()
    {
        EcuTune ecu = Tune(("crankingRPM", 300));

        TuneRestorePlan plan = TuneRestore.Plan(
            ecu, File_("some other firmware", ("crankingRPM", "400.0")), "firmware");

        Assert.False(plan.SignaturesAgree);
        Assert.Equal("some other firmware", plan.FileSignature);
        Assert.Equal("firmware", plan.EcuSignature);
    }

    [Fact]
    public void TheSameFirmwareBothSidesAgrees()
    {
        EcuTune ecu = Tune(("crankingRPM", 300));

        Assert.True(TuneRestore.Plan(
            ecu, File_("firmware", ("crankingRPM", "400.0")), "firmware").SignaturesAgree);
    }

    [Fact]
    public void NothingToCompareCountsAsAgreement()
    {
        // A file that names no firmware, or a controller that was never asked.
        // Not a mismatch to warn about; simply nothing said either way.
        EcuTune ecu = Tune(("crankingRPM", 300));

        Assert.True(TuneRestore.Plan(ecu, File_("", ("crankingRPM", "400.0")), "firmware").SignaturesAgree);
        Assert.True(TuneRestore.Plan(ecu, File_("firmware", ("crankingRPM", "400.0"))).SignaturesAgree);
    }

    // ----- what it says -------------------------------------------------------

    [Fact]
    public void TheSummarySaysHowMuchWouldChangeAndHowMuchWouldNot()
    {
        EcuTune ecu = Tune(("crankingRPM", 300), ("revLimit", 6500), ("idleTarget", 900));

        TuneRestorePlan plan = TuneRestore.Plan(ecu, File_("firmware", ("crankingRPM", "400.0")));

        Assert.Contains("1 setting would change", plan.Summary);
        Assert.Contains("not in the file", plan.Summary);
    }

    // ----- padding a controller chose for itself ------------------------------

    /// <summary>
    /// An INI with a text field, which is where a firmware's own padding lives.
    /// </summary>
    private const string TextIni = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 32
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageValueWrite  = "w%2o%2c%v"
        burnCommand     = "b%2i"
           label = string, ASCII, 0, 16
           revLimit = scalar, U16, 16, "rpm", 1, 0, 0, 10000, 0
        """;

    /// <summary>A tune whose text field is padded the way a controller pads it.</summary>
    private static EcuTune Padded(string text, params byte[] padding)
    {
        TuneLayout layout = TuneLayoutReader.Read(TextIni);
        var page = new byte[32];

        for (int i = 0; i < text.Length; i++) page[i] = (byte)text[i];
        for (int i = 0; i < padding.Length; i++) page[text.Length + i] = padding[i];

        return EcuTune.FromPages(layout, page);
    }

    private static MsqFile TextFile(string label) => MsqFile.Read($"""
        <msq xmlns="http://www.msefi.com/:msq">
        <versionInfo fileFormat="5.0" nPages="1" signature="firmware"/>
        <page number="0" size="32">
        <constant name="label">"{label}"</constant>
        <constant name="revLimit">0.0</constant>
        </page>
        </msq>
        """);

    [Fact]
    public void RestoringATuneToTheControllerItCameFromWouldSendNothing()
    {
        // Found on a rusEFI, whose 8000-byte Lua script field ends with the
        // newlines the script was written with. Reading a text field trims its
        // padding and writing one pads with nulls, so the tune saved off the
        // controller came back differing from it by exactly those two bytes —
        // and the plan plainly contradicted itself: "0 settings would change,
        // 2 bytes across 1 page".
        EcuTune ecu = Padded("print('hi')", (byte)'\n', (byte)'\n');

        TuneRestorePlan plan = TuneRestore.Plan(ecu, TextFile("print('hi')"), "firmware");

        Assert.Empty(plan.Differences);
        Assert.True(plan.IsEmpty, plan.Summary);
        Assert.Equal(0, plan.Bytes);
    }

    [Theory]
    [InlineData((byte)' ')]
    [InlineData((byte)'\n')]
    [InlineData((byte)'\r')]
    [InlineData((byte)'\t')]
    public void WhicheverCharacterAControllerPadsWith(byte pad)
    {
        // Trimming is what makes any of these vanish on the way out, so all of
        // them come back as a phantom write unless the bytes are left alone.
        EcuTune ecu = Padded("name", pad, pad, pad);

        Assert.True(TuneRestore.Plan(ecu, TextFile("name"), "firmware").IsEmpty);
    }

    [Fact]
    public void ButAGenuinelyDifferentNameStillGetsWritten()
    {
        // The fix must not turn into "text is never restored".
        EcuTune ecu = Padded("old name", (byte)'\n');

        TuneRestorePlan plan = TuneRestore.Plan(ecu, TextFile("new name"), "firmware");

        Assert.False(plan.IsEmpty);
        Assert.Single(plan.Differences);
        Assert.Equal("label", plan.Differences[0].Name);
    }

    [Fact]
    public void AndTheFieldStillEndsUpHoldingWhatTheFileSaid()
    {
        EcuTune ecu = Padded("old name", (byte)'\n');

        TuneRestorePlan plan = TuneRestore.Plan(ecu, TextFile("new name"), "firmware");

        Assert.Equal("new name", plan.Target.TextIn(plan.Target.Pages, "label"));
    }

    // ----- saying which name changed ------------------------------------------

    [Fact]
    public void ATextDifferenceNamesBothStringsRatherThanCountingCells()
    {
        // It used to read "label: 1 of 32 cells differ, first — against —",
        // because the strings were dropped and the field's width was taken for a
        // number of values. A name is the one setting whose value a person
        // recognises on sight; it should say which name.
        EcuTune ecu = Padded("old name", (byte)'\n');

        TuneDifference d = Assert.Single(
            TuneRestore.Plan(ecu, TextFile("new name"), "firmware").Differences);

        Assert.False(d.IsArray);
        Assert.Equal("\"new name\"", d.MineShown);
        Assert.Equal("\"old name\"", d.TheirsShown);
        Assert.Equal("label: \"new name\" against \"old name\"", d.Summary);
    }

    [Fact]
    public void AnEmptiedNameSaysSoRatherThanShowingNothing()
    {
        EcuTune ecu = Padded("was here");

        TuneDifference d = Assert.Single(
            TuneRestore.Plan(ecu, TextFile(""), "firmware").Differences);

        Assert.Equal("(blank)", d.MineShown);
        Assert.Equal("\"was here\"", d.TheirsShown);
    }

    // ----- a rejected setting must leave nothing behind ------------------------

    private const string ArrayIni = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 16
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageChunkWrite  = "w%2o%2c%v"
        burnCommand     = "b%2i"
           veTable  = array, U08, 0, [4], "%", 1, 0, 0, 255, 0
           revLimit = scalar, U16, 8, "rpm", 1, 0, 0, 10000, 0
        """;

    [Fact]
    public void ATableTheFileCannotStoreLeavesEveryCellAsItWas()
    {
        // Rejected means "not stored", and a restore trusts that when it decides
        // those bytes need no write. Written cell by cell, a value that will not
        // fit part way through left the cells before it already changed — so the
        // plan reported the setting untouched and would have sent half the
        // file's table and half the controller's to a running engine.
        TuneLayout layout = TuneLayoutReader.Read(ArrayIni);
        var ecu = EcuTune.FromPages(layout, new byte[16]);

        for (int i = 0; i < 4; i++)
            Assert.True(ecu.PokeInto(ecu.Pages, ecu.Constant("veTable")!, i, 50 + i));

        // The last cell is past what a U08 holds, so the constant is refused —
        // after the first three have already been written.
        MsqFile file = MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <versionInfo fileFormat="5.0" nPages="1" signature="firmware"/>
            <page number="0" size="16">
            <constant name="veTable">80.0 81.0 82.0 900.0</constant>
            <constant name="revLimit">6500.0</constant>
            </page>
            </msq>
            """);

        MsqLoad loaded = MsqApply.Load(layout, file, ecu);

        Assert.Contains(loaded.Rejected, c => c.Name == "veTable");

        // Every cell still holds what the controller held.
        double[] cells = loaded.Tune.Array("veTable") ?? [];
        Assert.Equal([50, 51, 52, 53], cells);
    }

    [Fact]
    public void AndTheRestoreThereforePlansNoWriteForIt()
    {
        TuneLayout layout = TuneLayoutReader.Read(ArrayIni);
        var ecu = EcuTune.FromPages(layout, new byte[16]);

        for (int i = 0; i < 4; i++) ecu.PokeInto(ecu.Pages, ecu.Constant("veTable")!, i, 50 + i);
        ecu.PokeInto(ecu.Pages, ecu.Constant("revLimit")!, 0, 6500);

        MsqFile file = MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <versionInfo fileFormat="5.0" nPages="1" signature="firmware"/>
            <page number="0" size="16">
            <constant name="veTable">80.0 81.0 82.0 900.0</constant>
            <constant name="revLimit">6500.0</constant>
            </page>
            </msq>
            """);

        TuneRestorePlan plan = TuneRestore.Plan(ecu, file, "firmware");

        // Nothing at all: the one setting that could be stored already matches,
        // and the one that could not was put back.
        Assert.True(plan.IsEmpty, plan.Summary);
        Assert.Equal(0, plan.Bytes);
    }

    // ----- a scale written as an expression -----------------------------------

    /// <summary>
    /// A firmware whose curve is scaled by another setting, which is the case
    /// the whole rescaling mechanism exists for.
    /// </summary>
    private const string ExpressionIni = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 16
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageChunkWrite  = "w%2o%2c%v"
        burnCommand     = "b%2i"
           maf_range = scalar, U08, 0, "", 1, 0, 0, 7, 0
           mafFlow   = array,  U08, 4, [4], "g/s", { 0.01 * (maf_range + 1) }, 0, 0, 100, 2
        """;

    [Fact]
    public void AWriteThatMovesAScaleReworksTheScalesThatDependOnIt()
    {
        // The scale is worked out when the tune is built, and a write is exactly
        // what moves the setting it is written against. Left alone, the curve
        // goes on dividing by the old scale — and the next cell edited is
        // *encoded* through it and sent to a running engine.
        TuneLayout layout = TuneLayoutReader.Read(ExpressionIni);
        var tune = EcuTune.FromPages(layout, new byte[16]);

        // maf_range 0, so the scale is 0.01: a raw 100 reads as 1.00.
        tune.PokeInto(tune.Pages, tune.Constant("mafFlow")!, 0, 1.0);
        Assert.Equal(1.0, tune.Array("mafFlow")![0], 6);

        // Now move maf_range to 3, the way a settings write would.
        TuneWrite? write = tune.EncodeArray("maf_range", [3]);
        Assert.NotNull(write);
        tune.Accept(write!);

        // The scale is now 0.04, so the same raw byte means four times as much.
        Assert.Equal(4.0, tune.Array("mafFlow")![0], 6);
    }

    [Fact]
    public void AndAFirmwareWithNoExpressionScalesIsLeftAlone()
    {
        // The rework is not free, and every write goes through here.
        TuneLayout layout = TuneLayoutReader.Read(ArrayIni);
        var tune = EcuTune.FromPages(layout, new byte[16]);

        tune.PokeInto(tune.Pages, tune.Constant("veTable")!, 0, 50);
        tune.Accept(tune.EncodeArray("revLimit", [6500])!);

        Assert.Equal(50, tune.Array("veTable")![0], 6);
        Assert.Equal(6500, tune.Scalar("revLimit"), 6);
    }

    [Fact]
    public void ANameTooLongForTheFieldIsReportedRatherThanQuietlyShortened()
    {
        // The complaint MsqApply already had for this was unreachable, because
        // PokeTextInto truncated and said it had worked. A tune from a build
        // with a wider field therefore counted the name applied while putting a
        // different name on the controller.
        TuneLayout layout = TuneLayoutReader.Read(TextIni);
        var ecu = EcuTune.FromPages(layout, new byte[32]);

        MsqLoad loaded = MsqApply.Load(layout, TextFile("a name far too long for sixteen"), ecu);

        MsqComplaint refused = Assert.Single(loaded.Rejected, c => c.Name == "label");
        Assert.Contains("does not fit", refused.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndTheRestorePlansNoWriteForIt()
    {
        // Which is the point: rejected has to mean the bytes were left alone,
        // or the plan and the bytes disagree and the plan is what gets sent.
        TuneLayout layout = TuneLayoutReader.Read(TextIni);
        var ecu = Padded("keep me", (byte)'\n');

        TuneRestorePlan plan =
            TuneRestore.Plan(ecu, TextFile("a name far too long for sixteen"), "firmware");

        Assert.True(plan.IsEmpty, plan.Summary);
        Assert.Equal("keep me", plan.Target.TextIn(plan.Target.Pages, "label"));
    }
}
