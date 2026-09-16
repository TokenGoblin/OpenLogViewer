using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Deciding whether a file handed over is a firmware definition at all.
///
/// The bar the catalogue applies when scanning — "has a quoted signature line
/// somewhere near the top" — is right for a scan and useless as an acceptance
/// test, because the readers underneath are all deliberately forgiving and
/// return nothing rather than complaining. A truncated download passes it. What
/// these pin is that nothing gets <em>kept</em> on that basis, since the
/// definitions folder is scanned recursively and a bad file left in it is found
/// again for ever.
/// </summary>
public class DefinitionFileTests
{
    /// <summary>The smallest thing that is honestly a firmware definition.</summary>
    private const string Real = """
        [MegaTune]
           signature = "TEST Format 0001.00"

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
        ochBlockSize = 4
        ochGetCommand = "r\x00\x07%2o%2c"
           rpm = scalar, U16, 0, "rpm", 1, 0
        """;

    // ----- what is accepted ----------------------------------------------------

    [Fact]
    public void ARealDefinitionIsUsableAndReportsWhatItHolds()
    {
        DefinitionInspection found = DefinitionFile.Inspect(Real);

        Assert.True(found.IsUsable);
        Assert.Equal("", found.Problem);
        Assert.Equal("TEST Format 0001.00", found.Signature);
        Assert.Equal(1, found.Pages);
        Assert.True(found.Channels > 0);
    }

    // ----- what is refused, and why --------------------------------------------

    [Fact]
    public void AnEmptyFileIsRefused()
    {
        DefinitionInspection found = DefinitionFile.Inspect("   \n  \n");

        Assert.False(found.IsUsable);
        Assert.Contains("empty", found.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileDeclaringNoSignatureIsRefused()
    {
        DefinitionInspection found = DefinitionFile.Inspect("[Constants]\npage = 1\n");

        Assert.False(found.IsUsable);
        Assert.Contains("no signature", found.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithASignatureButNoSettingsPagesIsRefused()
    {
        // The case the catalogue's own bar cannot see: this passes Scan, matches
        // an ECU, and connects to a session with nothing in it.
        DefinitionInspection found = DefinitionFile.Inspect("""
            [MegaTune]
               signature = "TEST Format 0001.00"
            """);

        Assert.False(found.IsUsable);
        Assert.Equal("TEST Format 0001.00", found.Signature);
        Assert.Equal(0, found.Pages);
        Assert.Contains("no settings pages", found.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHtmlErrorPageSavedUnderTheWrongNameIsRefused()
    {
        // What a fetch of a missing release actually returns. GitHub serves a
        // 9-byte "Not Found" body; a mirror serves a page. Neither is a
        // definition, and neither declares a signature.
        DefinitionInspection found = DefinitionFile.Inspect(
            "<!DOCTYPE html><html><head><title>404</title></head><body>Not Found</body></html>");

        Assert.False(found.IsUsable);
    }

    [Fact]
    public void ATruncatedDefinitionIsRefusedEvenThoughItsSignatureSurvived()
    {
        // A download cut off after the first section. The signature is intact,
        // so every check short of actually reading the file is satisfied.
        string truncated = Real[..Real.IndexOf("[Constants]", StringComparison.Ordinal)];

        Assert.False(DefinitionFile.Inspect(truncated).IsUsable);
    }

    // ----- on disk -------------------------------------------------------------

    [Fact]
    public void AFileThatIsNotThereIsRefusedRatherThanThrowing()
    {
        DefinitionInspection found = DefinitionFile.InspectFile(
            Path.Combine(Path.GetTempPath(), $"olv-absent-{Guid.NewGuid():N}.ini"));

        Assert.False(found.IsUsable);
        Assert.Contains("no file", found.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealDefinitionOnDiskIsRead()
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-def-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, Real);

        try
        {
            Assert.True(DefinitionFile.InspectFile(path).IsUsable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ----- naming --------------------------------------------------------------

    [Fact]
    public void ADefinitionIsNamedAfterItsSignatureTheWayTunerStudioNamesThem()
    {
        Assert.Equal("speeduino202501.ini", DefinitionFile.NameFor("speeduino 202501"));
        Assert.Equal("MS3Format0592.13.ini", DefinitionFile.NameFor("MS3 Format 0592.13 "));
    }

    [Fact]
    public void ASignatureThatWouldNameNothingStillGetsAName()
    {
        Assert.Equal("definition.ini", DefinitionFile.NameFor("   "));
    }

    [Fact]
    public void ASignatureCannotSmuggleAPathIntoTheName()
    {
        Assert.DoesNotContain('/', DefinitionFile.NameFor("../../evil"));
        Assert.DoesNotContain('\\', DefinitionFile.NameFor("..\\..\\evil"));
    }
}
