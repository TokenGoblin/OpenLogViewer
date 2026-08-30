using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The awkward cases a review found, each of which passed everything else in
/// this suite while being wrong.
/// </summary>
public class ReviewFixTests
{
    // ----- a scale worked out from a tune that is not filled in yet -----------

    private const string Scaled = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 64
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
           maf_range = bits,  U08, 0, [0:1], "650", "1300", "1950", "2600"
           mafflow   = array, U16, 2, [2], "g/sec", {0.01 * (maf_range + 1)}, 0, 0, 3000, 2
        """;

    [Fact]
    public void AScaleWrittenInTermsOfASettingIsWorkedOutAfterThatSettingArrives()
    {
        // The sum is done when a tune is built, and a tune being filled in from
        // a file is empty at that moment — so the range read as nought, the
        // scale came out four times too small, and the file's 1000 g/s no longer
        // fitted the field and was refused outright. The identical file laid
        // over a controller loaded perfectly, which is what made it hard to see.
        TuneLayout layout = TuneLayoutReader.Read(Scaled);

        var page = new byte[64];
        page[0] = 3;                                   // the 2600 g/s sensor, so scale 0.04
        page[2] = 0x61; page[3] = 0xA8;                // raw 25,000 = 1000.0 g/sec
        page[4] = 0xC3; page[5] = 0x50;                // raw 50,000 = 2000.0 g/sec

        EcuTune real = EcuTune.FromPages(layout, page);
        Assert.Equal(0.04, real.Constant("mafflow")!.Scale, 6);

        MsqFile file = MsqFile.Read(MsqWriter.Write(real, "firmware"));

        foreach (EcuTune? onto in new[] { real, null })
        {
            MsqLoad load = MsqApply.Load(layout, file, onto);

            Assert.Empty(load.Rejected);
            Assert.Equal(0.04, load.Tune.Constant("mafflow")!.Scale, 6);
            Assert.Equal([1000, 2000], load.Tune.Array("mafflow"));
        }
    }

    // ----- what a settings edit records --------------------------------------

    private const string Tenths = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 16
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
           advance = scalar, U08, 0, "deg", 0.1, 0, 0, 25, 1
        """;

    [Fact]
    public void AnEditRecordsWhatWasStoredRatherThanWhatWasAsked()
    {
        // A byte holding tenths cannot hold 1.24, so a confirmation list showing
        // "0 -> 1.24" names a number that will never reach the ECU.
        TuneLayout layout = TuneLayoutReader.Read(Tenths);
        var edit = new TuneSettingsEdit(EcuTune.FromPages(layout, new byte[16]));

        Assert.True(edit.Set("advance", 1.24));

        SettingChange change = Assert.Single(edit.Changes);
        Assert.Equal(1.2, change.Now, 6);
        Assert.Equal(1.2, edit.Value("advance"), 6);
    }

    [Fact]
    public void AnEditThatMovesNoByteIsNotAPendingChange()
    {
        // 5.44 and 5.4 are the same byte. Counting the first as a change lights
        // the Send button over a write that turns out to be empty — the phantom
        // the text path goes out of its way to avoid.
        TuneLayout layout = TuneLayoutReader.Read(Tenths);
        var tune = EcuTune.FromPages(layout, new byte[16]);
        tune.PokeInto(tune.Pages, tune.Constant("advance")!, 0, 5.4);

        var edit = new TuneSettingsEdit(tune);

        Assert.True(edit.Set("advance", 5.44));

        Assert.False(edit.HasChanges);
        Assert.Empty(edit.Writes());
    }

    // ----- option lists with a comment after them ----------------------------

    [Fact]
    public void ACommentAfterAListDoesNotSwallowAReferenceOrAddLabels()
    {
        // The comment came off after the split rather than before it, so "$pins"
        // followed by a comment failed the identifier test and stayed a literal,
        // and any comma inside the comment became further labels. Either
        // renumbers every option after it.
        var defines = IniDefines.Read("""
            #define pins = "Off", "Out A"
            #define allPins = "INVALID", $pins ; every pin, as we know them
            """);

        Assert.Equal(["INVALID", "Off", "Out A"], defines["allPins"]);
    }

    // ----- what a backup says about what it could not read -------------------

    [Fact]
    public void AValueThatCannotBeReadIsLeftOutRatherThanWrittenAsZero()
    {
        // A float in erased flash reads as NaN. Writing "0.0" records a number
        // the ECU does not hold, and restoring that file puts a real zero over
        // it. Saying nothing is the truth, and a reader already knows to leave
        // alone anything a file does not mention.
        const string floats = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 16
            pageIdentifier = "\x01"
            pageReadCommand = "r%2o%2c"
               trim = scalar, F32, 0, "%", 1, 0
               idle = scalar, U08, 8, "rpm", 10, 0, 0, 2000, 0
            """;

        TuneLayout layout = TuneLayoutReader.Read(floats);

        var page = new byte[16];
        page[0] = page[1] = page[2] = page[3] = 0xFF;      // erased flash: NaN
        page[8] = 90;

        string xml = MsqWriter.Write(EcuTune.FromPages(layout, page), "firmware");

        Assert.DoesNotContain("\"trim\"", xml, StringComparison.Ordinal);
        Assert.Contains("\"idle\"", xml, StringComparison.Ordinal);

        // And reading it back reports the gap rather than inventing a value.
        MsqLoad load = MsqApply.Load(layout, MsqFile.Read(xml));
        Assert.Contains(load.Missing, m => m.Name == "trim");
    }

    // ----- what a restore plan says ------------------------------------------

    [Fact]
    public void APlanThatWouldChangeNothingStillSaysWhatTheFileLacked()
    {
        // "The ECU already holds this tune, setting for setting" is the opposite
        // of the truth when the file carried nothing this firmware recognises.
        TuneLayout layout = TuneLayoutReader.Read(Tenths);
        var ecu = EcuTune.FromPages(layout, new byte[16]);

        TuneRestorePlan plan = TuneRestore.Plan(ecu, MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <versionInfo nPages="1" signature="firmware"/>
            <page number="0" size="16"/>
            </msq>
            """));

        Assert.True(plan.IsEmpty);
        Assert.False(plan.Complete);
        Assert.DoesNotContain("already holds", plan.Summary);
        Assert.Contains("not in the file", plan.Summary);
    }

    [Fact]
    public void APlanCarriesWhatCouldNotBeStoredAsWellAsWhatWasAbsent()
    {
        TuneLayout layout = TuneLayoutReader.Read(Tenths);
        var ecu = EcuTune.FromPages(layout, new byte[16]);

        TuneRestorePlan plan = TuneRestore.Plan(ecu, MsqFile.Read("""
            <msq xmlns="http://www.msefi.com/:msq">
            <versionInfo nPages="1" signature="firmware"/>
            <page number="0" size="16">
            <constant name="advance">999.0</constant>
            </page>
            </msq>
            """));

        Assert.Contains(plan.Rejected, r => r.Name == "advance");
        Assert.False(plan.Complete);
        Assert.Contains("could not be stored", plan.Summary);
    }

    // ----- how far out, against how much to move -----------------------------

    [Fact]
    public void ALogWithNoTableReportsTheErrorItselfNotAShareOfIt()
    {
        // With no table there is nothing to suggest a change to, so what comes
        // back is the measurement. Scaling it by an authority that applies to no
        // table, and by a confidence the caller is handed separately, would tell
        // a tuner a cell running six per cent lean was three.
        // A table of zeros, which is the "no current fuelling" shape: a log
        // from a controller whose tune cannot be read.
        var empty = new TuneTable(
            "VE table 1",
            new TuneAxis("rpm", "RPM", [1000, 3000]),
            new TuneAxis("map", "kPa", [40, 80]),
            new double[2, 2],
            "%");

        // Twelve samples of a steady six per cent lean, and a confidence figure
        // set to the same twelve — so the weight is exactly a half, which is
        // what used to halve the number reported.
        double[] afr = [.. Enumerable.Repeat(15.582, 12)];
        double[] target = [.. Enumerable.Repeat(14.7, 12)];

        VeAnalysisResult result = VeAnalysis.Analyse(
            empty,
            new LogChannel("RPM", "", 2, [.. Enumerable.Repeat(1000.0, 12)]),
            new LogChannel("MAP", "", 2, [.. Enumerable.Repeat(40.0, 12)]),
            new LogChannel("AFR", "", 2, afr),
            new LogChannel("Target", "", 2, target),
            0, 11, null,
            new VeAnalysisSettings
            {
                Authority = 0.5, ConfidenceSamples = 12, MinimumSamples = 4,
            });

        Assert.Equal(6.0, result.ChangePercent[0, 0]!.Value, 1);
    }
}
