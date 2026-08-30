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
}
