using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Engine size, piston speed and compression.
///
/// Checked against engines that exist, because every one of these is a short
/// formula that is easy to write down slightly wrong and impossible to catch by
/// reading — a displacement out by a factor of the number of cylinders, or a
/// piston speed missing its factor of two, both look entirely reasonable.
/// </summary>
public class EngineGeometryTests
{
    // ----- displacement --------------------------------------------------------

    [Theory]
    // Bore, stroke, cylinders, and the badge the engine actually wears.
    [InlineData(86.0, 86.0, 4, 1_998)]     // the archetypal square two litre
    [InlineData(92.0, 86.0, 4, 2_287)]
    [InlineData(101.6, 88.4, 8, 5_733)]    // a 350 cubic inch V8
    [InlineData(84.0, 90.0, 6, 2_993)]
    [InlineData(70.0, 64.0, 3, 739)]
    public void DisplacementComesOutAtTheNumberOnTheBadge(
        double bore, double stroke, int cylinders, double expectedCc)
    {
        double cc = EngineGeometry.DisplacementCc(bore, stroke, cylinders);

        Assert.Equal(expectedCc, cc, 0);
    }

    [Fact]
    public void ASmallBlockChevroletIsThreeHundredAndFiftyCubicInches()
    {
        // 4.00 inch bore, 3.48 inch stroke, eight of them. The check is that it
        // comes back out in the units it is famous in.
        double cc = EngineGeometry.DisplacementCc(4.00 * 25.4, 3.48 * 25.4, 8);

        Assert.InRange(EngineGeometry.CubicInches(cc), 349, 351);
        Assert.InRange(cc, 5_720, 5_745);
    }

    [Fact]
    public void OneCylinderIsTheWholeEngineDividedByItsCylinders()
    {
        double swept = EngineGeometry.SweptVolumeCc(86, 86);

        Assert.Equal(swept * 4, EngineGeometry.DisplacementCc(86, 86, 4), 6);
        Assert.Equal(499.6, swept, 1);
    }

    [Fact]
    public void BoreOverStrokeSaysWhichWayRoundTheEngineIs()
    {
        Assert.Equal(1, EngineGeometry.BoreToStroke(86, 86), 6);
        Assert.True(EngineGeometry.BoreToStroke(92, 86) > 1, "a big bore and short stroke is oversquare");
        Assert.True(EngineGeometry.BoreToStroke(84, 90) < 1, "a small bore and long stroke is undersquare");
    }

    // ----- piston speed --------------------------------------------------------

    [Fact]
    public void MeanPistonSpeedCountsTheStrokeTwicePerRevolution()
    {
        // 86 mm at 7,000 rpm: the piston covers 172 mm per turn, 7,000 times a
        // minute, which is 20 metres a second. Worked from the distance rather
        // than from the formula, since the factor of two is the thing that goes
        // missing.
        double metresPerMinute = 2 * 0.086 * 7_000;

        Assert.Equal(metresPerMinute / 60, EngineGeometry.MeanPistonSpeed(86, 7_000), 6);
        Assert.Equal(20.07, EngineGeometry.MeanPistonSpeed(86, 7_000), 2);
    }

    [Fact]
    public void TheOldRuleOfFourThousandFeetAMinuteIsTwentyMetresASecond()
    {
        // The two conventions for the same limit, so the conversion is worth
        // pinning: a street engine is usually said to want to stay under 4,000
        // ft/min, which is 20.3 m/s.
        double ftMin = EngineGeometry.MeanPistonSpeedFeetPerMinute(86, 7_000);

        Assert.Equal(3_950, ftMin, 0);
        Assert.Equal(20.3, 4_000 / (EngineGeometry.FeetPerMetre * 60), 1);
    }

    [Fact]
    public void RealEnginesLandInTheBandsTheirReputationsSuggest()
    {
        // A diesel that never sees 4,500 rpm.
        Assert.InRange(EngineGeometry.MeanPistonSpeed(95, 4_500), 13, 15);

        // A road performance engine at its redline.
        Assert.InRange(EngineGeometry.MeanPistonSpeed(86, 7_000), 19, 21);

        // A racing V8: a short stroke spun very hard indeed.
        Assert.InRange(EngineGeometry.MeanPistonSpeed(82.5, 9_000), 24, 26);

        // And a Formula One V8 of the 20,000 rpm era, which is off the end of
        // anything a production part does.
        Assert.InRange(EngineGeometry.MeanPistonSpeed(39.7, 20_000), 25, 28);
    }

    [Fact]
    public void ShorteningTheStrokeBuysEngineSpeedAtTheSamePistonSpeed()
    {
        // The whole reason an oversquare engine revs: the limit is at the rings,
        // not on the tachometer.
        double atLimit = EngineGeometry.MeanPistonSpeed(86, 7_000);

        Assert.Equal(atLimit, EngineGeometry.MeanPistonSpeed(43, 14_000), 6);
    }

    [Fact]
    public void EveryPistonSpeedGetsAVerdictAndTheyGetSternerAsItRises()
    {
        Assert.Equal("—", EngineGeometry.PistonSpeedVerdict(0));

        string[] verdicts =
        [
            EngineGeometry.PistonSpeedVerdict(10),
            EngineGeometry.PistonSpeedVerdict(16),
            EngineGeometry.PistonSpeedVerdict(20),
            EngineGeometry.PistonSpeedVerdict(24),
            EngineGeometry.PistonSpeedVerdict(30),
        ];

        Assert.Equal(verdicts.Length, verdicts.Distinct().Count());
        Assert.All(verdicts, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    // ----- compression ---------------------------------------------------------

    [Fact]
    public void AnOrdinaryRoadEngineComesOutAtAnOrdinaryCompressionRatio()
    {
        // 86 × 86, a 42 cc chamber, an 87 mm gasket a millimetre thick, half a
        // millimetre of deck and a 5 cc dish. That is a normal set of parts and
        // it should give a normal answer, near ten to one.
        double swept = EngineGeometry.SweptVolumeCc(86, 86);
        double clearance = EngineGeometry.ClearanceVolumeCc(
            boreMm: 86, chamberCc: 42, gasketBoreMm: 87, gasketThicknessMm: 1.0,
            deckClearanceMm: 0.5, pistonCc: 5);

        double ratio = EngineGeometry.CompressionRatio(swept, clearance);

        Assert.InRange(clearance, 54, 57);
        Assert.InRange(ratio, 9.5, 10.5);
    }

    [Fact]
    public void ADomeRaisesTheRatioAndADishLowersIt()
    {
        double swept = EngineGeometry.SweptVolumeCc(86, 86);

        static double Clearance(double pistonCc) => EngineGeometry.ClearanceVolumeCc(
            86, 42, 87, 1.0, 0.5, pistonCc);

        double dished = EngineGeometry.CompressionRatio(swept, Clearance(5));
        double flat = EngineGeometry.CompressionRatio(swept, Clearance(0));
        double domed = EngineGeometry.CompressionRatio(swept, Clearance(-5));

        Assert.True(domed > flat, "a dome fills the chamber and raises the ratio");
        Assert.True(flat > dished, "a dish adds space and lowers it");
    }

    [Fact]
    public void APistonOutOfTheHoleTakesVolumeAwayRatherThanAddingIt()
    {
        // The sign that gets typed wrong. Negative deck clearance means the crown
        // comes past the deck at top dead centre, which raises compression.
        double swept = EngineGeometry.SweptVolumeCc(86, 86);

        static double Ratio(double deck) => EngineGeometry.CompressionRatio(
            EngineGeometry.SweptVolumeCc(86, 86),
            EngineGeometry.ClearanceVolumeCc(86, 42, 87, 1.0, deck, 5));

        Assert.True(Ratio(-0.5) > Ratio(0), "out of the hole should raise it");
        Assert.True(Ratio(0) > Ratio(0.5), "down the hole should lower it");

        // And the size of it: half a millimetre on an 86 mm bore is 2.9 cc.
        Assert.Equal(2.9, EngineGeometry.CylinderVolumeCc(86, 0.5), 1);
        _ = swept;
    }

    [Fact]
    public void AThickerGasketLowersCompressionByItsOwnVolume()
    {
        double thin = EngineGeometry.ClearanceVolumeCc(86, 42, 87, 0.8, 0.5, 5);
        double thick = EngineGeometry.ClearanceVolumeCc(86, 42, 87, 1.8, 0.5, 5);

        // A millimetre more gasket on an 87 mm bore is 5.9 cc more chamber.
        Assert.Equal(EngineGeometry.CylinderVolumeCc(87, 1.0), thick - thin, 6);
        Assert.Equal(5.9, thick - thin, 1);
    }

    [Fact]
    public void TheRatioAndTheClearanceItNeedsAreEachOthersInverse()
    {
        double swept = EngineGeometry.SweptVolumeCc(86, 86);

        foreach (double ratio in (double[])[8.5, 10, 11.5, 14])
        {
            double clearance = EngineGeometry.ClearanceForRatio(swept, ratio);

            Assert.Equal(ratio, EngineGeometry.CompressionRatio(swept, clearance), 6);
        }
    }

    [Fact]
    public void ARatioIsRefusedRatherThanInventedFromNonsense()
    {
        double swept = EngineGeometry.SweptVolumeCc(86, 86);

        // No clearance volume is not infinite compression, it is a typing error.
        Assert.True(double.IsNaN(EngineGeometry.CompressionRatio(swept, 0)));
        Assert.True(double.IsNaN(EngineGeometry.CompressionRatio(swept, -3)));
        Assert.True(double.IsNaN(EngineGeometry.CompressionRatio(0, 50)));

        Assert.True(double.IsNaN(EngineGeometry.ClearanceForRatio(swept, 1)));
        Assert.True(double.IsNaN(EngineGeometry.SweptVolumeCc(0, 86)));
        Assert.True(double.IsNaN(EngineGeometry.DisplacementCc(86, 86, 0)));
        Assert.True(double.IsNaN(EngineGeometry.MeanPistonSpeed(86, 0)));
    }

    // ----- compression against boost -------------------------------------------

    [Fact]
    public void TenToOneOnFifteenPsiIndexesLikeTwentyToOneWithout()
    {
        // The comparison the index exists to make. Fifteen psi is a pressure
        // ratio just over two, so it roughly doubles the figure.
        double map = TuningMath.AbsoluteFromGauge(15 * TuningMath.KpaPerPsi);

        double index = EngineGeometry.BoostedCompressionIndex(10, map);

        Assert.InRange(index, 19, 21);

        // With no boost at all it is the static ratio and nothing more.
        Assert.Equal(10, EngineGeometry.BoostedCompressionIndex(10, TuningMath.AtmosphericKpa), 6);
    }

    [Fact]
    public void LessCompressionBuysBoostAndTheIndexSaysHowMuch()
    {
        // Why a boosted build drops the compression: 8.5:1 on 20 psi indexes
        // about the same as 10.5:1 on 13, which is the trade being made.
        double lower = EngineGeometry.BoostedCompressionIndex(
            8.5, TuningMath.AbsoluteFromGauge(20 * TuningMath.KpaPerPsi));

        double higher = EngineGeometry.BoostedCompressionIndex(
            10.5, TuningMath.AbsoluteFromGauge(13 * TuningMath.KpaPerPsi));

        Assert.InRange(Math.Abs(lower - higher), 0, 1.5);
    }

    [Fact]
    public void TheIndexFollowsAltitudeTheWayTheEngineDoes()
    {
        // A mile up there is less air, so the same static engine is working the
        // cylinder less hard — which is exactly why it makes less power there.
        double high = TuningMath.BarometricKpa(5_280 * TuningMath.MetresPerFoot);

        double atSea = EngineGeometry.BoostedCompressionIndex(10, TuningMath.AtmosphericKpa);
        double upHigh = EngineGeometry.BoostedCompressionIndex(10, high);

        Assert.True(upHigh < atSea);
    }
}
