using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Saying what definition was missing, precisely enough for somebody — or
/// something — to go and get it.
///
/// The addresses below are not invented: each was fetched and confirmed to
/// serve the definition it claims to. The Speeduino case is the bench board
/// this was written against, which reports both of the strings used here.
/// </summary>
public class DefinitionNeedTests
{
    private static DefinitionNeed Describe(params string[] identity) =>
        DefinitionNeeds.Describe(identity, [], @"C:\defs");

    // ----- which firmware said it ----------------------------------------------

    [Theory]
    [InlineData("Speeduino")]
    [InlineData("rusEFI")]
    [InlineData("MegaSquirt")]
    public void EachFamilyIsRecognisedFromWhatItSays(string family)
    {
        string[] said = family switch
        {
            "Speeduino" => ["Speeduino 2025.01.7", "speeduino 202501"],
            "rusEFI" => ["rusEFI master.2024.07.04.uaefi.1448555430"],
            _ => ["MS3 Format 0569.00"],
        };

        Assert.Equal(family, DefinitionNeeds.FamilyOf(said));
    }

    [Fact]
    public void AnEcuNobodyRecognisesIsNotGuessedAt()
    {
        DefinitionNeed need = Describe("SOMETHING ELSE 1.0");

        Assert.Equal("", need.Family);
        Assert.Empty(need.Sources);
    }

    // ----- Speeduino: the version carries what the signature cannot -------------

    [Fact]
    public void ASpeeduinoIsFetchedByItsVersionBecauseTheSignatureDoesNotPinThePatch()
    {
        // The board on the bench. Eight 202501.x releases declare "speeduino
        // 202501" and ship three different definitions between them, so the
        // signature alone would fetch a coin toss. "2025.01.7" names tag
        // 202501.7 exactly.
        DefinitionNeed need = Describe("Speeduino 2025.01.7", "speeduino 202501");

        Assert.Equal("Speeduino", need.Family);
        Assert.Equal("speeduino 202501", need.Signature);
        Assert.Equal("Speeduino 2025.01.7", need.Version);

        Assert.Equal(
            "https://raw.githubusercontent.com/speeduino/speeduino/202501.7/reference/speeduino.ini",
            need.Sources[0]);

        Assert.Equal("https://speeduino.com/fw/202501.7.ini", need.Sources[1]);
    }

    [Fact]
    public void ASpeeduinoThatOnlyGaveItsSignatureStillGetsTheSeriesTag()
    {
        // Worse than the above — this is the whole series rather than the exact
        // patch — but a definition from the right series beats none at all, and
        // the tag exists.
        DefinitionNeed need = Describe("speeduino 202402");

        Assert.Equal("", need.Version);
        Assert.Contains("/202402/reference/speeduino.ini", need.Sources[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpeeduinoFilenameIsTheOneTunerStudioWouldUse()
    {
        Assert.Equal("speeduino202501.ini", Describe("speeduino 202501").Filename);
    }

    // ----- rusEFI: the signature is the address --------------------------------

    [Fact]
    public void ARusEfiSignatureBecomesItsOwnLookupPath()
    {
        // rusEFI's own published rule, which its console follows: spaces and
        // dots become separators, and only the white-label is lowercased.
        DefinitionNeed need = Describe("rusEFI 2023.01.08.proteus_f4.snap_14905");

        Assert.Equal(
            "https://rusefi.com/online/ini/rusefi/2023/01/08/proteus_f4/snap_14905.ini",
            Assert.Single(need.Sources));
    }

    [Fact]
    public void ARusEfiBranchNameSurvivesIntoThePath()
    {
        DefinitionNeed need = Describe("rusEFI master.2024.07.04.uaefi.1448555430");

        Assert.Equal(
            "https://rusefi.com/online/ini/rusefi/master/2024/07/04/uaefi/1448555430.ini",
            Assert.Single(need.Sources));
    }

    // ----- MegaSquirt: no address, and that is the point -----------------------

    [Fact]
    public void AMegaSquirtIsNeverGivenAnAddressToFetchFrom()
    {
        // Its licence restricts the definition itself — "not permissable to use
        // the INI file to tune other hardware" — and scopes redistribution to
        // media accompanying a hardware sale. Its download path is defended
        // against automation besides. The refusal to name a source is a
        // decision, and it belongs here rather than in whatever is asking.
        DefinitionNeed need = Describe("MS3 Format 0569.00");

        Assert.Equal("MegaSquirt", need.Family);
        Assert.Empty(need.Sources);
    }

    [Fact]
    public void AMegaSquirtIsToldWhereToLookInstead()
    {
        DefinitionNeed need = Describe("MS3 Format 0569.00");

        Assert.Contains("TunerStudio", need.Where, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ms3pro.ini", need.Where, StringComparison.OrdinalIgnoreCase);
    }

    // ----- what is already here ------------------------------------------------

    [Fact]
    public void TheNearestDefinitionOnThisMachineIsNamed()
    {
        // The one sentence that turns "unsupported ECU" into "your definition is
        // one firmware version stale".
        IniFile[] here =
        [
            new(@"C:\defs\speeduino202402.ini", "speeduino 202402"),
            new(@"C:\defs\MS3Format0569.00.ini", "MS3 Format 0569.00"),
        ];

        DefinitionNeed need = DefinitionNeeds.Describe(
            ["Speeduino 2025.01.7", "speeduino 202501"], here, @"C:\defs");

        DefinitionNearMiss nearest = Assert.Single(need.Nearby);
        Assert.Equal("speeduino202402.ini", nearest.Name);
        Assert.Equal("speeduino 202402", nearest.Signature);
    }

    [Fact]
    public void SomethingSharingNothingIsNotOfferedAsNearby()
    {
        IniFile[] here = [new(@"C:\defs\MS3Format0569.00.ini", "MS3 Format 0569.00")];

        DefinitionNeed need = DefinitionNeeds.Describe(["speeduino 202501"], here, @"C:\defs");

        Assert.Empty(need.Nearby);
    }

    [Fact]
    public void AnEcuThatSaidNothingIsDescribedWithoutInventingAnything()
    {
        DefinitionNeed need = DefinitionNeeds.Describe([], [], @"C:\defs");

        Assert.Equal("", need.Signature);
        Assert.Equal("", need.Filename);
        Assert.Empty(need.Sources);
        Assert.Empty(need.Nearby);
    }
}
