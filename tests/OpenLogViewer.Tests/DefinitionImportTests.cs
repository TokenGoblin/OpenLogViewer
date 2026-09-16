using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The gate every definition from outside has to pass.
///
/// This is the one place an outside file becomes part of how an engine is read
/// and written — a definition says which byte is the rev limiter, and the
/// dangerous-constant guard matches on names it declares. So what is pinned
/// here is that nothing reaches the definitions folder without declaring the
/// signature the ECU actually reported and parsing as a real definition, and
/// that a refusal leaves nothing behind: that folder is scanned recursively for
/// ever after.
/// </summary>
public class DefinitionImportTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        }
    }

    private string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"olv-defs-{Guid.NewGuid():N}");
        _temp.Add(folder);

        return folder;
    }

    private const string Speeduino = """
        [MegaTune]
           signature = "speeduino 202501"

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

    // ----- what is kept --------------------------------------------------------

    [Fact]
    public void ADefinitionMatchingWhatTheEcuSaidIsKept()
    {
        string folder = NewFolder();

        DefinitionImportResult result = DefinitionImport.Keep(
            Speeduino, folder, ["Speeduino 2025.01.7", "speeduino 202501"],
            "https://raw.githubusercontent.com/speeduino/speeduino/202501.7/reference/speeduino.ini");

        Assert.True(result.Accepted, result.Problem);
        Assert.True(File.Exists(result.Path));
        Assert.Equal("speeduino202501.ini", Path.GetFileName(result.Path));

        // And it is found by the very next scan, which is the point of keeping it.
        Assert.NotNull(IniCatalog.Match("speeduino 202501", IniCatalog.Scan([folder])));
    }

    [Fact]
    public void WhereItCameFromIsWrittenDown()
    {
        string folder = NewFolder();
        const string source = "https://speeduino.com/fw/202501.7.ini";

        DefinitionImport.Keep(Speeduino, folder, ["speeduino 202501"], source);

        DefinitionProvenance kept = Assert.Single(DefinitionImport.Imported(folder));

        Assert.Equal("speeduino202501.ini", kept.Name);
        Assert.Equal("speeduino 202501", kept.Signature);
        Assert.Equal(source, kept.Source);
        Assert.Equal(64, kept.Sha256.Length);
        Assert.Contains("speeduino 202501", kept.ForIdentity);
    }

    [Fact]
    public void ProvenanceCanBeLookedUpByTheNameInUse()
    {
        string folder = NewFolder();
        DefinitionImport.Keep(Speeduino, folder, ["speeduino 202501"], "somewhere");

        Assert.NotNull(DefinitionImport.Of(folder, "speeduino202501.ini"));
        Assert.Null(DefinitionImport.Of(folder, "something-else.ini"));
    }

    [Fact]
    public void ImportingWithNothingPluggedInSkipsTheSignatureCheck()
    {
        // Preparing before a drive, with no ECU to ask.
        string folder = NewFolder();

        Assert.True(DefinitionImport.Keep(Speeduino, folder, [], "a file I had").Accepted);
    }

    // ----- what is refused, and that nothing is left behind --------------------

    [Fact]
    public void ADefinitionForDifferentFirmwareIsRefused()
    {
        string folder = NewFolder();

        DefinitionImportResult result = DefinitionImport.Keep(
            Speeduino, folder, ["speeduino 202402"], "wherever");

        Assert.False(result.Accepted);
        Assert.Contains("wrong place", result.Problem, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }

    [Fact]
    public void ADefinitionDeclaringOnlyAPrefixIsRefused()
    {
        // "speeduino" is a prefix of every Speeduino signature there has ever
        // been. A live session forgives that; installing it must not, or it
        // becomes the answer for every future connection.
        string folder = NewFolder();
        string loose = Speeduino.Replace("speeduino 202501", "speeduino", StringComparison.Ordinal);

        Assert.False(DefinitionImport.Keep(loose, folder, ["speeduino 202501"], "wherever").Accepted);
    }

    [Fact]
    public void SomethingThatIsNotADefinitionIsRefusedBeforeItIsWritten()
    {
        string folder = NewFolder();

        DefinitionImportResult result = DefinitionImport.Keep(
            "<html><body>404 Not Found</body></html>", folder, [], "a bad fetch");

        Assert.False(result.Accepted);
        Assert.Equal("", result.Path);
        Assert.Empty(DefinitionImport.Imported(folder));
    }

    [Fact]
    public void ATruncatedDefinitionIsRefusedEvenWithTheRightSignature()
    {
        string folder = NewFolder();
        string truncated = Speeduino[..Speeduino.IndexOf("[Constants]", StringComparison.Ordinal)];

        DefinitionImportResult result = DefinitionImport.Keep(
            truncated, folder, ["speeduino 202501"], "an interrupted download");

        Assert.False(result.Accepted);
        Assert.Contains("no settings pages", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameThatTriesToLeaveTheFolderIsRefused()
    {
        string folder = NewFolder();

        DefinitionImportResult result = DefinitionImport.Keep(
            Speeduino, folder, [], "wherever", name: @"..\..\evil.ini");

        Assert.False(result.Accepted);
        Assert.Equal("", result.Path);
    }

    // ----- from disk -----------------------------------------------------------

    [Fact]
    public void AFileOnDiskIsImportedAndKeepsItsOwnName()
    {
        string folder = NewFolder();
        string source = Path.Combine(Path.GetTempPath(), $"olv-src-{Guid.NewGuid():N}.ini");
        File.WriteAllText(source, Speeduino);

        try
        {
            DefinitionImportResult result = DefinitionImport.KeepFile(
                source, folder, ["speeduino 202501"], "https://example.invalid/speeduino.ini");

            Assert.True(result.Accepted, result.Problem);
            Assert.Equal(Path.GetFileName(source), Path.GetFileName(result.Path));
            Assert.Equal("https://example.invalid/speeduino.ini", result.Provenance!.Source);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void AFileThatIsNotThereIsRefusedRatherThanThrowing()
    {
        DefinitionImportResult result = DefinitionImport.KeepFile(
            Path.Combine(Path.GetTempPath(), $"olv-absent-{Guid.NewGuid():N}.ini"),
            NewFolder(), [], "");

        Assert.False(result.Accepted);
        Assert.Contains("no file", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportingTheSameDefinitionAgainReplacesItsRecordRatherThanDoubling()
    {
        string folder = NewFolder();

        DefinitionImport.Keep(Speeduino, folder, ["speeduino 202501"], "first");
        DefinitionImport.Keep(Speeduino, folder, ["speeduino 202501"], "second");

        DefinitionProvenance only = Assert.Single(DefinitionImport.Imported(folder));
        Assert.Equal("second", only.Source);
    }
}
