using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class GaugeTests
{
    /// <summary>Shaped like a real INI: bounds by expression, a category, and a front page.</summary>
    private const string Ini = """
        [PcVariables]
        rpmhigh = scalar, U16, "rpm", 1, 0, 0, 30000, 0
        clt_exp = bits, U08, [0:0], "Normal", "Expanded"

        [OutputChannels]
        ochBlockSize = 8
        rpm     = scalar, U16, 0, "RPM",  1.0, 0.0
        coolant = scalar, S16, 2, "deg F", 0.1, 0.0
        clthighlim = { clt_exp ? 450 : 250 }

        [GaugeConfigurations]
        gaugeCategory = "Engine"
        tachometer = rpm, "Engine Speed", "RPM", 0, {rpmhigh}, 300, 600, {rpmhigh - 1500}, {rpmhigh - 500}, 0, 0
        cltGauge   = coolant, "Coolant Temp", "deg F", -40, {clthighlim}, -100, -100, 200, 220, 1, 1
        gaugeCategory = "Diagnostics"
        cutGauge   = fuelCutReason, "Fuel Cut Code", "code", 0.0,0.0, 0.0,0.0, 0.0,0.0, 0,0
        loadGauge  = fuelload, "Fuel Load", { bitStringValue(algorithmUnits, algorithm) }, 0, 400, 0, 20, 200, 400, 1, 0

        [FrontPage]
        gauge1 = tachometer
        gauge3 = cutGauge
        gauge2 = cltGauge
        """;

    private const string Tune = """
        <?xml version="1.0" encoding="ISO-8859-1"?>
        <msq xmlns="http://www.msefi.com/:msq">
        <page>
        <pcVariable name="rpmhigh">7000</pcVariable>
        <pcVariable name="clt_exp">"Normal"</pcVariable>
        <constant name="nCylinders">6</constant>
        </page>
        </msq>
        """;

    private static IReadOnlyList<GaugeSpec> Gauges() =>
        GaugeCatalog.Read(Ini, TuningContext.Build(Ini, Tune));

    private static GaugeSpec Named(string name) =>
        Assert.Single(Gauges(), g => g.Name == name);

    // ----- the fields -------------------------------------------------------

    [Fact]
    public void AGaugeCarriesItsChannelTitleAndUnits()
    {
        GaugeSpec clt = Named("cltGauge");

        Assert.Equal("coolant", clt.Channel);
        Assert.Equal("Coolant Temp", clt.Title);
        Assert.Equal("deg F", clt.Units);
        Assert.Equal(1, clt.ValueDigits);
    }

    [Fact]
    public void GaugesKeepTheCategoryTheyWereListedUnder()
    {
        Assert.Equal("Engine", Named("tachometer").Category);
        Assert.Equal("Diagnostics", Named("cutGauge").Category);
    }

    // ----- bounds that are expressions --------------------------------------

    [Fact]
    public void ABoundWrittenAsAnExpressionIsResolvedFromTheTune()
    {
        // The redline follows the engine, so it has to come out of the tune
        // rather than out of a constant in this program.
        GaugeSpec tacho = Named("tachometer");

        Assert.Equal(7000, tacho.High);
        Assert.Equal(5500, tacho.HighWarning);
        Assert.Equal(6500, tacho.HighDanger);
    }

    [Fact]
    public void AnEnumStoredAsItsLabelStillReachesTheArithmetic()
    {
        // clt_exp is written in the tune as the word "Normal"; only the INI's
        // declaration says that means zero, and only then does the coolant
        // gauge get a top of 250 rather than 450.
        Assert.Equal(250, Named("cltGauge").High);
    }

    [Fact]
    public void ADerivedChannelThatDependsOnNothingLiveIsAvailableAsABound()
    {
        IReadOnlyDictionary<string, double> known = TuningContext.Build(Ini, Tune);

        Assert.Equal(250, known["clthighlim"]);
    }

    [Fact]
    public void WithoutATuneTheExpressionBoundsSimplyDoNotResolve()
    {
        // Better than inventing a scale: a dial with the wrong range reads as a
        // measurement rather than as missing information.
        Assert.False(GaugeCatalog.Read(Ini).First(g => g.Name == "tachometer").HasScale);
    }

    // ----- bands ------------------------------------------------------------

    [Fact]
    public void AnOrderedSetOfLimitsCountsAsBands() => Assert.True(Named("tachometer").HasBands);

    [Fact]
    public void ReadingsAreSortedIntoTheirBand()
    {
        GaugeSpec tacho = Named("tachometer");

        Assert.Equal(GaugeBand.Normal, tacho.BandFor(3000));
        Assert.Equal(GaugeBand.Warning, tacho.BandFor(5800));
        Assert.Equal(GaugeBand.Danger, tacho.BandFor(6800));
        Assert.Equal(GaugeBand.Danger, tacho.BandFor(100));
        Assert.Equal(GaugeBand.Unknown, tacho.BandFor(double.NaN));
    }

    [Fact]
    public void LimitsFilledInWithTheSamePairAreNotTreatedAsBands()
    {
        // Generated INIs repeat one pair across all six numbers, which taken at
        // face value paints every reading as dangerous.
        GaugeSpec cut = Named("cutGauge");

        Assert.False(cut.HasBands);
        Assert.Equal(GaugeBand.Normal, cut.BandFor(3));
    }

    // ----- gauges without a face --------------------------------------------

    [Fact]
    public void AGaugeWithNoRangeIsKeptButHasNoScale()
    {
        // "Fuel Cut Code" is worth showing even though the firmware never said
        // what a normal one looks like.
        GaugeSpec cut = Named("cutGauge");

        Assert.False(cut.HasScale);
        Assert.Equal("Fuel Cut Code", cut.Title);
        Assert.Equal(0, cut.Fraction(500));
    }

    [Fact]
    public void AComputedUnitsLabelIsLeftBlankRatherThanPrinted() =>
        Assert.Equal("", Named("loadGauge").Units);

    // ----- the front page ---------------------------------------------------

    [Fact]
    public void TheFrontPageComesBackInPositionOrderNotFileOrder()
    {
        Assert.Equal(["tachometer", "cltGauge", "cutGauge"], GaugeCatalog.ReadFrontPage(Ini));
    }

    [Fact]
    public void AConditionalPicksOneGaugeForAPosition()
    {
        const string ini = """
            [FrontPage]
            #if LAMBDA
            gauge1 = lambda1Gauge
            #else
            gauge1 = afr1Gauge
            #endif
            """;

        Assert.Equal(["afr1Gauge"], GaugeCatalog.ReadFrontPage(ini));
        Assert.Equal(["lambda1Gauge"], GaugeCatalog.ReadFrontPage(ini, new HashSet<string> { "LAMBDA" }));
    }

    // ----- the dial ---------------------------------------------------------

    [Fact]
    public void ReadingsMapOntoTheFaceAndStayOnIt()
    {
        GaugeSpec tacho = Named("tachometer");

        Assert.Equal(0.5, tacho.Fraction(3500), 6);
        Assert.Equal(0, tacho.Fraction(-1000));
        Assert.Equal(1, tacho.Fraction(99999));
        Assert.Equal(0, tacho.Fraction(double.NaN));
    }
}
