using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Finding the definition that belongs to a connected ECU — and, just as much,
/// refusing to pretend a near miss belongs to it.
/// </summary>
public class IniCatalogTests
{
    // ----- where it looks ------------------------------------------------------

    [Fact]
    public void TunerStudiosOwnInstalledDefinitionsAreSearched()
    {
        // TunerStudio installs ~97 firmware definitions beside the program, not
        // only the ones it later downloads into the user's config folder.
        // Searching just the config folder told people with the right file
        // already on their machine to go and find one.
        IReadOnlyList<string> paths = IniCatalog.DefaultSearchPaths;

        Assert.Contains(paths, p =>
            p.Contains("TunerStudioMS", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith("ecuDef", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheRusEfiConsolesCacheIsSearched()
    {
        Assert.Contains(IniCatalog.DefaultSearchPaths, p =>
            p.Contains(".rusEFI", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith("ini_database", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheUserConfigFolderIsStillSearchedFirst()
    {
        // Ours and TunerStudio's downloaded copies come before its bundled set:
        // a definition somebody fetched for their exact firmware beats one that
        // shipped with the installer years ago.
        IReadOnlyList<string> paths = IniCatalog.DefaultSearchPaths;

        int config = paths.ToList().FindIndex(p => p.Contains(".efiAnalytics", StringComparison.OrdinalIgnoreCase));
        int installed = paths.ToList().FindIndex(p => p.Contains("TunerStudioMS", StringComparison.OrdinalIgnoreCase));

        Assert.True(config >= 0 && installed > config, "the downloaded copies must be searched before the bundled ones");
    }

    // ----- reading a signature out of text -------------------------------------

    [Fact]
    public void ASignatureIsReadOutOfTextAsWellAsOffDisk()
    {
        Assert.Equal(
            "speeduino 202501",
            IniCatalog.SignatureIn("[MegaTune]\n   signature = \"speeduino 202501\"\n"));
    }

    [Fact]
    public void TextDeclaringNoSignatureReadsAsNone() =>
        Assert.Null(IniCatalog.SignatureIn("[Constants]\npage = 1\n"));

    // ----- exact, for deciding what to keep ------------------------------------

    [Fact]
    public void TheSameSignatureIsTheSameThroughPaddingAndCase()
    {
        // What Match already forgives, and all it forgives.
        Assert.True(IniCatalog.SameSignature("speeduino 202501", "speeduino  202501"));
        Assert.True(IniCatalog.SameSignature("MS3 Format 0592.13 ", "MS3 Format 0592.13"));
        Assert.True(IniCatalog.SameSignature("Speeduino 202501", "speeduino 202501"));
    }

    [Fact]
    public void APrefixIsNotTheSameSignature()
    {
        // The reason this exists. Match's looser passes let a live session start
        // when an ECU pads its reply; they must never let a definition declaring
        // nothing but "speeduino" be installed as the answer for every Speeduino
        // that will ever be plugged in.
        Assert.False(IniCatalog.SameSignature("speeduino", "speeduino 202501"));
        Assert.False(IniCatalog.SameSignature("speeduino 202501", "speeduino 202402"));
        Assert.False(IniCatalog.SameSignature("", "speeduino 202501"));
    }

    // ----- more than one candidate ---------------------------------------------

    [Fact]
    public void EveryDefinitionDeclaringTheSameSignatureIsReportedNotJustTheFirst()
    {
        // Speeduino's eight 202501.x releases all declare "speeduino 202501" and
        // ship three different definitions between them, one of which decodes
        // fanHyster at a different scale. Match takes whichever the scan reached
        // first, which is not the same choice on two machines.
        IniFile[] catalogue =
        [
            new("a.ini", "speeduino 202501"),
            new("b.ini", "speeduino 202501"),
            new("c.ini", "speeduino 202402"),
        ];

        IReadOnlyList<IniFile> all = IniCatalog.MatchingAll("speeduino 202501", catalogue);

        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, i => i.Path == "c.ini");
    }

    [Fact]
    public void MatchingAllIgnoresTheLooseningThatMatchAllows()
    {
        IniFile[] catalogue = [new("a.ini", "speeduino")];

        Assert.Empty(IniCatalog.MatchingAll("speeduino 202501", catalogue));

        // Match itself still forgives it, deliberately — that behaviour is not
        // being changed here, only kept away from decisions about what to keep.
        Assert.NotNull(IniCatalog.Match("speeduino 202501", catalogue));
    }
}
