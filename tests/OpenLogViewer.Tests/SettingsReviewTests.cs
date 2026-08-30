using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The settings interface read against the awkward cases, each of which passed
/// every other test in this suite while being wrong.
/// </summary>
public class SettingsReviewTests
{
    // ----- option lists -------------------------------------------------------

    [Fact]
    public void ABitFieldNamingNothingOffersNoChoices()
    {
        // Both list splitters end by yielding whatever follows the last comma,
        // so an empty list came back as one blank label. A setting with one
        // blank option is drawn as a list to pick from with nothing in it —
        // it cannot be read or set, where a plain number box would have worked.
        const string ini = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 64
               reserved = bits, U08, 5, [0:1]
            """;

        TuneConstant reserved = TuneLayoutReader.Read(ini).Constants.Single(c => c.Name == "reserved");

        Assert.Empty(reserved.Options);
        Assert.False(reserved.HasOptions);
    }

    [Fact]
    public void ABlankLabelInsideAListIsStillKept()
    {
        // The opposite case, and the reason the fix is about the whole list
        // being empty rather than about dropping blanks: the position of a
        // label is the number the ECU stores, so a gap left out renumbers
        // every choice after it.
        const string ini = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 64
               mode = bits, U08, 5, [0:1], "Off", "", "On"
            """;

        TuneConstant mode = TuneLayoutReader.Read(ini).Constants.Single(c => c.Name == "mode");

        Assert.Equal(3, mode.Options.Count);
        Assert.Equal("On", mode.OptionName(2));
    }

    [Fact]
    public void ANamedListIsTakenFromTheBranchThisBuildUses()
    {
        // A firmware commonly writes the same list once per board. Read without
        // the preprocessor the last one wins whatever build this is, which
        // gives correctly numbered options carrying another board's labels —
        // worse than none, because the page looks right and reads wrong.
        const string ini = """
            #if CAN_COMMANDS
            #define pinNames = "CAN0", "CAN1"
            #else
            #define pinNames = "Serial0", "Serial1"
            #endif
            """;

        Assert.Equal(
            ["CAN0", "CAN1"],
            IniDefines.Read(ini, new HashSet<string>(StringComparer.Ordinal) { "CAN_COMMANDS" })["pinNames"]);

        Assert.Equal(
            ["Serial0", "Serial1"],
            IniDefines.Read(ini, new HashSet<string>(StringComparer.Ordinal))["pinNames"]);
    }

    // ----- curves -------------------------------------------------------------

    [Fact]
    public void ACurveDoesNotPickUpTheBinsOfATableBelowIt()
    {
        // [TableEditor] follows [CurveEditor] in every real definition and
        // spells its bin constants with the very same keys. Reading the whole
        // file left the last curve open past the end of its section, so each
        // table after it overwrote the curve's axes with its own.
        const string ini = """
            [CurveEditor]
               curve = warmupCurve, "Warmup enrichment"
                  columnLabel = "Coolant", "Enrichment"
                  xBins = wueBins, coolant
                  yBins = wueRates

            [TableEditor]
               table = veTable1Tbl, veTable1Map, "VE Table", 2
                  xBins = rpmBins, rpm
                  yBins = mapBins, map
                  zBins = veTable
            """;

        TuneCurve warmup = TuneCurveReader.Read(ini)["warmupCurve"];

        Assert.Equal("wueBins", warmup.XBins);
        Assert.Equal("wueRates", warmup.YBins);
        Assert.Equal("Coolant", warmup.XLabel);
    }

    [Fact]
    public void ACurveOnlyInTheOtherBuildIsNotRead()
    {
        const string ini = """
            [CurveEditor]
            #if CELSIUS
               curve = warmupC, "Warmup (C)"
                  xBins = wueBinsC
                  yBins = wueRates
            #else
               curve = warmupF, "Warmup (F)"
                  xBins = wueBinsF
                  yBins = wueRates
            #endif
            """;

        IReadOnlyDictionary<string, TuneCurve> curves =
            TuneCurveReader.Read(ini, new HashSet<string>(StringComparer.Ordinal) { "CELSIUS" });

        Assert.True(curves.ContainsKey("warmupC"));
        Assert.False(curves.ContainsKey("warmupF"));
    }

    // ----- derived channels ---------------------------------------------------

    [Fact]
    public void ADerivedChannelKeepsItsNumberRatherThanBecomingOne()
    {
        // These are arithmetic far more often than they are tests. Passing one
        // through a yes-or-no answer turned 63 kPa into 1, and a dialog
        // condition comparing it against a threshold then hid settings the
        // tuner is meant to reach.
        var derived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engineLoad"] = "fuelingLoad",
            ["loadOver100"] = "engineLoad > 100",
        };

        Func<string, double> lookup = DerivedChannels.Resolving(
            derived, name => name == "fuelingLoad" ? 63 : double.NaN);

        Assert.Equal(63, lookup("engineLoad"));
        Assert.Equal(0, lookup("loadOver100"));
        Assert.True(double.IsNaN(lookup("nothingKnowsThis")));
    }

    [Fact]
    public void ADerivedChannelThatRefersToItselfStillAnswersNothing()
    {
        var derived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = "b + 1",
            ["b"] = "a + 1",
        };

        Assert.True(double.IsNaN(DerivedChannels.Resolving(derived, _ => double.NaN)("a")));
    }

    // ----- found by reading the real definition files -------------------------

    [Fact]
    public void ARedefinedListMeansTheOneItHadAMomentAgo()
    {
        // How a firmware grows a list. MS3 declares eight CANPWM pins and then
        // redefines the same name as "INVALID", the old list, and some more.
        // Gathering every definition first and resolving afterwards makes that
        // second one refer to itself, and the loop guard then leaves the literal
        // "$PIN_DIGOUT_CANPWM" standing where eight labels belong — so every pin
        // after it is numbered seven short and choosing one sets another.
        var defines = IniDefines.Read("""
            #define PIN_DIGOUT_CANPWM = "CANPWM1", "CANPWM2"
            #define PIN_DIGOUT_CANPWM = "INVALID", $PIN_DIGOUT_CANPWM, "CANOUT1"
            """);

        Assert.Equal(["INVALID", "CANPWM1", "CANPWM2", "CANOUT1"], defines["PIN_DIGOUT_CANPWM"]);
    }

    [Fact]
    public void ARealSelfReferenceIsStillLeftAsWritten()
    {
        // With nothing declared before it there is no earlier meaning to take,
        // and a definition with a mistake in it should be visible rather than
        // expanded until the stack is gone.
        Assert.Equal(["A", "$loop"], IniDefines.Read("""
            #define loop = "A", $loop
            """)["loop"]);
    }

    [Fact]
    public void ASemicolonInsideALabelIsNotTheStartOfAComment()
    {
        // Speeduino's log separator, verbatim. Cutting at the first semicolon
        // whatever is around it left this setting with one option consisting of
        // a single quote mark — neither readable nor settable.
        const string ini = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 128
               onboard_log_csv_separator = bits, U08, 116, [0:1], ";", ",", "tab", "space"
            """;

        TuneConstant separator =
            TuneLayoutReader.Read(ini).Constants.Single(c => c.Name == "onboard_log_csv_separator");

        Assert.Equal([";", ",", "tab", "space"], separator.Options);
    }

    [Fact]
    public void ARealCommentIsStillCutOff()
    {
        const string ini = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 128
               mode = bits, U08, 5, [0:1], "Off", "On" ; two of them
            """;

        Assert.Equal(["Off", "On"], TuneLayoutReader.Read(ini).Constants.Single(c => c.Name == "mode").Options);
    }

    [Fact]
    public void AListOfNothingButBlanksIsNoListAtAll()
    {
        // Speeduino spells one reserved field with a single empty label, which
        // is a seven-bit number wearing a dropdown with nothing in it.
        const string ini = """
            [Constants]
            page = 1
            nPages = 1
            pageSize = 256
               unused10_182 = bits, U08, 183, [1:7], ""
            """;

        Assert.Empty(TuneLayoutReader.Read(ini).Constants.Single(c => c.Name == "unused10_182").Options);
    }
}
