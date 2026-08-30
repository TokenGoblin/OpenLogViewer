using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The named option lists a firmware writes once and points at from everywhere
/// the same choice appears.
///
/// Found by connecting to a Speeduino: without these, "Load source" offered one
/// choice called <c>$loadSourceNames</c>. An MS3 points at one from 338 of its
/// bit fields and a Speeduino from 175.
/// </summary>
public class IniDefineTests
{
    [Fact]
    public void ANamedListIsRead()
    {
        var defines = IniDefines.Read("""
            #define loadSourceNames = "MAP", "TPS", "IMAP/EMAP", "INVALID"
            """);

        Assert.Equal(["MAP", "TPS", "IMAP/EMAP", "INVALID"], defines["loadSourceNames"]);
    }

    [Fact]
    public void OneListMayBeBuiltFromOthers()
    {
        // The position of a label is the number the ECU stores, so a reference
        // has to contribute all of its labels in order.
        var defines = IniDefines.Read("""
            #define invalid_x2 = "INVALID", "INVALID"
            #define invalid_x4 = $invalid_x2, $invalid_x2
            #define pins       = "Off", $invalid_x4
            """);

        Assert.Equal(4, defines["invalid_x4"].Count);
        Assert.Equal(["Off", "INVALID", "INVALID", "INVALID", "INVALID"], defines["pins"]);
    }

    [Fact]
    public void OneThatRefersToItselfIsLeftAsWritten()
    {
        // A definition with a mistake in it should be visible in the interface,
        // not expanded until the stack is gone.
        var defines = IniDefines.Read("""
            #define loop = "A", $loop
            """);

        Assert.Equal(["A", "$loop"], defines["loop"]);
    }

    [Fact]
    public void ABareDefineIsAPreprocessorSwitchAndNotAList()
    {
        var defines = IniDefines.Read("""
            #define CAN_COMMANDS
            #define real = "A", "B"
            """);

        Assert.DoesNotContain("CAN_COMMANDS", defines.Keys);
        Assert.Single(defines);
    }

    [Fact]
    public void ACommaInsideALabelIsPartOfIt()
    {
        var defines = IniDefines.Read("""
            #define odd = "One, then two", "Three"
            """);

        Assert.Equal(["One, then two", "Three"], defines["odd"]);
    }

    [Fact]
    public void ACommentAfterTheListIsNotALabel()
    {
        var defines = IniDefines.Read("""
            #define x = "A", "B" ; two of them
            """);

        Assert.Equal(["A", "B"], defines["x"]);
    }

    [Fact]
    public void ALabelMayContainASemicolon()
    {
        // Which is why this cannot use the ordinary comment stripper.
        var defines = IniDefines.Read("""
            #define x = "Set it; carefully", "B"
            """);

        Assert.Equal(["Set it; carefully", "B"], defines["x"]);
    }

    [Fact]
    public void AReferenceToNothingIsKeptRatherThanDropped()
    {
        // Dropping it would renumber every label after it, which is worse than
        // showing a name nobody defined.
        IReadOnlyList<string> list = IniDefines.Expand(
            "\"A\", $missing, \"C\"",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(3, list.Count);
        Assert.Equal("C", list[2]);
    }

    // ----- what this is for -------------------------------------------------

    [Fact]
    public void ABitFieldPointingAtAListGetsItsValuesNamed()
    {
        // Verified against a connected Speeduino, where "algorithm" reads zero
        // and means MAP.
        const string ini = """
            #define loadSourceNames = "MAP", "TPS", "IMAP/EMAP", "INVALID"

            [Constants]
            page = 1
            nPages = 1
            pageSize = 64
               algorithm = bits, U08, 37, [0:2], $loadSourceNames
            """;

        TuneConstant algorithm = TuneLayoutReader.Read(ini).Constants.Single(c => c.Name == "algorithm");

        Assert.Equal("MAP", algorithm.OptionName(0));
        Assert.Equal("TPS", algorithm.OptionName(1));
        Assert.True(algorithm.IsValidOption(2));
        Assert.False(algorithm.IsValidOption(3));
    }

    [Fact]
    public void ABitFieldMayMixItsOwnLabelsWithAList()
    {
        const string ini = """
            #define rest = "B", "C"

            [Constants]
            page = 1
            nPages = 1
            pageSize = 64
               mixed = bits, U08, 0, [0:1], "A", $rest
            """;

        TuneConstant mixed = TuneLayoutReader.Read(ini).Constants.Single(c => c.Name == "mixed");

        Assert.Equal(["A", "B", "C"], mixed.Options);
    }
}
