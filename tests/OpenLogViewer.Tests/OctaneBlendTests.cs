using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Octane blending, checked against measured fuels.
///
/// This is the calculator with the most room to be plausibly wrong, because
/// everybody already knows roughly what the answer looks like: ethanol raises
/// octane, the first splash is worth the most, and it runs cold. Any model that
/// does those three things reads correctly and can still be out by five points.
/// So the checks below are published RON measurements, not the formula restated.
/// </summary>
public class OctaneBlendTests
{
    /// <summary>
    /// Measured research octane numbers for ethanol blended into a blendstock of
    /// 88 RON, which is the case the literature reports most often.
    /// </summary>
    [Theory]
    [InlineData(0.10, 92.45)]
    [InlineData(0.15, 94.3)]
    [InlineData(0.30, 98.6)]
    public void EthanolBlendsMatchThePublishedMeasurements(double byVolume, double measuredRon)
    {
        // A blendstock quoted at 88 RON, expressed as the anti-knock index and
        // sensitivity this takes as inputs.
        const double Sensitivity = 8;
        const double BaseAki = 88 - (Sensitivity / 2);

        OctaneResult blend = OctaneBlend.Blend(BaseAki, Sensitivity, Blendstock.Ethanol, byVolume);

        Assert.Equal(measuredRon, blend.Ron, 0);
        Assert.True(Math.Abs(blend.Ron - measuredRon) < 0.3,
            $"E{byVolume * 100:N0} came out at {blend.Ron:F2} against a measured {measuredRon}");
    }

    [Fact]
    public void NeatAlcoholIsItsOwnOctaneWhateverItWasBlendedInto()
    {
        foreach (double grade in OctaneBlend.PumpGrades)
        {
            OctaneResult ethanol = OctaneBlend.Blend(grade, 8, Blendstock.Ethanol, 1.0);

            Assert.Equal(OctaneBlend.EthanolRon, ethanol.Ron, 6);
            Assert.Equal(OctaneBlend.EthanolMon, ethanol.Mon, 6);
            Assert.Equal(99.5, ethanol.AntiKnockIndex, 6);
        }
    }

    [Fact]
    public void NothingBlendedInLeavesTheFuelAloneEntirely()
    {
        OctaneResult neat = OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 0);

        Assert.Equal(91, neat.AntiKnockIndex, 6);
        Assert.Equal(95, neat.Ron, 6);
        Assert.Equal(87, neat.Mon, 6);
        Assert.Equal(OctaneBlend.PetrolHov, neat.HeatOfVaporisationKjPerKg, 6);
        Assert.Equal(0, neat.EthanolByVolume, 6);
    }

    [Fact]
    public void TenPerCentByVolumeIsTwentyPerCentByMolecule()
    {
        // The whole explanation of the curve everyone calls non-linear: ethanol
        // is a small, dense molecule, so a splash of it by volume is a great deal
        // of it by count, and octane follows the count.
        OctaneResult e10 = OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 0.10);

        Assert.InRange(e10.AlcoholMoleFraction, 0.20, 0.22);

        // Methanol is smaller still, so the same volume is more molecules again.
        OctaneResult m10 = OctaneBlend.Blend(91, 8, Blendstock.Methanol, 0.10);

        Assert.True(m10.AlcoholMoleFraction > e10.AlcoholMoleFraction);
        Assert.InRange(m10.AlcoholMoleFraction, 0.26, 0.30);
    }

    [Fact]
    public void TheFirstSplashIsWorthFarMoreThanTheLast()
    {
        // The observation the tab exists to explain, stated as a fact about the
        // numbers rather than as a formula.
        double at0 = OctaneBlend.Blend(87, 8, Blendstock.Ethanol, 0).AntiKnockIndex;
        double at10 = OctaneBlend.Blend(87, 8, Blendstock.Ethanol, 0.10).AntiKnockIndex;
        double at90 = OctaneBlend.Blend(87, 8, Blendstock.Ethanol, 0.90).AntiKnockIndex;
        double at100 = OctaneBlend.Blend(87, 8, Blendstock.Ethanol, 1.0).AntiKnockIndex;

        double first10 = at10 - at0;
        double last10 = at100 - at90;

        Assert.True(first10 > last10 * 4,
            $"the first tenth was worth {first10:F1} points and the last {last10:F1}");
    }

    [Fact]
    public void OctaneRisesWithEveryDropOfAlcoholAndWithEveryGradeOfBase()
    {
        foreach (Blendstock stock in Enum.GetValues<Blendstock>())
        {
            double previous = double.MinValue;

            foreach (double f in OctaneBlend.ChartFractions)
            {
                double aki = OctaneBlend.Blend(87, 8, stock, f).AntiKnockIndex;

                Assert.True(aki > previous, $"{stock} went backwards at {f:P0}");
                previous = aki;
            }
        }

        // And a better base is still a better blend, at any fraction short of neat.
        foreach (double f in (double[])[0.10, 0.30, 0.50, 0.85])
            Assert.True(
                OctaneBlend.Blend(93, 8, Blendstock.Ethanol, f).AntiKnockIndex >
                OctaneBlend.Blend(87, 8, Blendstock.Ethanol, f).AntiKnockIndex);
    }

    [Fact]
    public void MixingE85WithPumpPetrolIsNotMixingInEthanol()
    {
        // The mistake worth catching: half a tank of E85 leaves a mixture that is
        // 42.5 per cent ethanol, and the octane follows the ethanol rather than
        // the label on the pump.
        OctaneResult half = OctaneBlend.Blend(91, 8, Blendstock.E85, 0.50);

        Assert.Equal(0.425, half.EthanolByVolume, 6);
        Assert.Equal(
            OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 0.425).AntiKnockIndex,
            half.AntiKnockIndex,
            6);

        // And a tank of nothing but E85 is 85 per cent, not 100.
        Assert.Equal(0.85, OctaneBlend.Blend(91, 8, Blendstock.E85, 1.0).EthanolByVolume, 6);
    }

    [Fact]
    public void HalfATankOfE85InNinetyOneIsAboutNinetySevenOctane()
    {
        // The question every forum thread is actually asking. E85 mixed half and
        // half with 91 comes out in the high nineties, which is why people do it.
        double aki = OctaneBlend.Blend(91, 8, Blendstock.E85, 0.50).AntiKnockIndex;

        Assert.InRange(aki, 95, 99);
    }

    // ----- charge cooling ------------------------------------------------------

    [Fact]
    public void EthanolIsAboutTwoAndAHalfTimesPetrolToEvaporate()
    {
        // Per kilogram of fuel, which is the comparison usually quoted.
        double petrol = OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 0).HeatOfVaporisationKjPerKg;
        double ethanol = OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 1).HeatOfVaporisationKjPerKg;
        double methanol = OctaneBlend.Blend(91, 8, Blendstock.Methanol, 1).HeatOfVaporisationKjPerKg;

        Assert.Equal(OctaneBlend.PetrolHov, petrol, 6);
        Assert.InRange(ethanol / petrol, 2.3, 2.5);
        Assert.InRange(methanol / petrol, 3.0, 3.3);
    }

    [Fact]
    public void PerPoundOfAirTheAlcoholsAreWorthFarMoreThanThat()
    {
        // The comparison that actually describes the charge: an alcohol needs
        // more fuel for the same air, so the cooling per unit of air multiplies
        // the per-kilogram figure again. Ethanol lands near four times petrol
        // and methanol near seven, which is why methanol engines run the timing
        // they do.
        double petrol = OctaneBlend.PetrolCoolingKjPerKgAir;
        double ethanol = OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 1).CoolingKjPerKgAir;
        double methanol = OctaneBlend.Blend(91, 8, Blendstock.Methanol, 1).CoolingKjPerKgAir;

        Assert.InRange(petrol, 22, 26);
        Assert.InRange(ethanol / petrol, 3.7, 4.3);
        Assert.InRange(methanol / petrol, 6.5, 7.5);

        // And it is the larger multiple of the two, on every alcohol.
        Assert.True(
            ethanol / petrol >
            OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 1).HeatOfVaporisationKjPerKg
            / OctaneBlend.PetrolHov);
    }

    [Fact]
    public void E85CoolsTheChargeAboutThreeTimesAsHardAsPetrol()
    {
        OctaneResult e85 = OctaneBlend.Blend(91, 8, Blendstock.E85, 1.0);

        Assert.InRange(e85.CoolingKjPerKgAir / OctaneBlend.PetrolCoolingKjPerKgAir, 3.0, 3.6);
    }

    [Fact]
    public void CoolingRisesWithEveryDropToo()
    {
        double previous = 0;

        foreach (double f in OctaneBlend.ChartFractions)
        {
            double cooling = OctaneBlend.Blend(91, 8, Blendstock.Methanol, f).CoolingKjPerKgAir;

            Assert.True(cooling > previous, $"cooling went backwards at {f:P0}");
            previous = cooling;
        }
    }

    // ----- sensitivity ---------------------------------------------------------

    [Fact]
    public void SensitivitySplitsAnOctaneNumberIntoTheTwoItIsMadeOf()
    {
        OctaneResult blend = OctaneBlend.Blend(91, 10, Blendstock.Ethanol, 0);

        Assert.Equal(96, blend.Ron, 6);
        Assert.Equal(86, blend.Mon, 6);
        Assert.Equal(91, blend.AntiKnockIndex, 6);
    }

    [Fact]
    public void TheBaseSensitivityCancelsOutOfTheBlendedIndexEntirely()
    {
        // Not an approximation: splitting a grade into RON and MON adds half the
        // sensitivity to one and takes it off the other, and averaging them back
        // together cancels it exactly. So the chart does not rest on the one
        // figure here that nobody actually knows.
        foreach (double f in (double[])[0.10, 0.30, 0.50, 0.85, 1.0])
        {
            double narrow = OctaneBlend.Blend(91, 6, Blendstock.Ethanol, f).AntiKnockIndex;
            double wide = OctaneBlend.Blend(91, 12, Blendstock.Ethanol, f).AntiKnockIndex;

            Assert.Equal(narrow, wide, 9);
        }

        // It does still decide the two numbers the index is made of.
        Assert.NotEqual(
            OctaneBlend.Blend(91, 6, Blendstock.Ethanol, 0.30).Ron,
            OctaneBlend.Blend(91, 12, Blendstock.Ethanol, 0.30).Ron,
            3);
    }

    [Fact]
    public void BlendingAlcoholInMakesTheFuelMoreSensitiveAsWellAsHigherOctane()
    {
        // Ethanol's own sensitivity is 19 against pump petrol's 8, so a blend
        // ends up more sensitive than what it started as. Worth knowing rather
        // than worth ignoring: a boosted or direct-injected engine runs closer
        // to the RON end than to the MON end, so the same index is worth more
        // there on the sensitive fuel.
        OctaneResult neat = OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 0);
        double petrolSensitivity = neat.Ron - neat.Mon;

        double previous = petrolSensitivity;

        foreach (double f in (double[])[0.10, 0.30, 0.50, 0.85, 1.0])
        {
            OctaneResult blend = OctaneBlend.Blend(91, 8, Blendstock.Ethanol, f);
            double sensitivity = blend.Ron - blend.Mon;

            Assert.True(sensitivity >= previous, $"sensitivity fell at {f:P0}");
            previous = sensitivity;
        }

        Assert.Equal(8, petrolSensitivity, 6);
        Assert.Equal(19, previous, 6);
    }

    // ----- the chart -----------------------------------------------------------

    [Fact]
    public void TheChartHasARowPerGradeAndAgreesWithTheArithmetic()
    {
        string chart = OctaneBlend.Chart(Blendstock.Ethanol);

        string[] lines = chart.Split(Environment.NewLine);

        Assert.Equal(OctaneBlend.PumpGrades.Count + 1, lines.Length);

        foreach (double grade in OctaneBlend.PumpGrades)
            Assert.Contains($"{grade:N0}", chart);

        // The neat-petrol column is the grade itself, unchanged.
        foreach (double grade in OctaneBlend.PumpGrades)
            Assert.Equal(
                grade,
                OctaneBlend.Blend(grade, OctaneBlend.TypicalSensitivity, Blendstock.Ethanol, 0)
                    .AntiKnockIndex,
                6);
    }

    [Fact]
    public void EveryChartCellIsAPlausibleOctaneNumber()
    {
        foreach (Blendstock stock in Enum.GetValues<Blendstock>())
        foreach (double grade in OctaneBlend.PumpGrades)
        foreach (double f in OctaneBlend.ChartFractions)
        {
            double aki = OctaneBlend.Blend(grade, 8, stock, f).AntiKnockIndex;

            Assert.InRange(aki, grade - 0.001, 110);
        }
    }

    [Fact]
    public void NonsenseInputsDoNotProduceAnOctaneNumber()
    {
        Assert.Equal(default, OctaneBlend.Blend(double.NaN, 8, Blendstock.Ethanol, 0.3));
        Assert.Equal(default, OctaneBlend.Blend(91, double.NaN, Blendstock.Ethanol, 0.3));
        Assert.Equal(default, OctaneBlend.Blend(91, 8, Blendstock.Ethanol, double.NaN));

        // And a fraction outside nought to one is clamped rather than believed.
        Assert.Equal(
            OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 1.0),
            OctaneBlend.Blend(91, 8, Blendstock.Ethanol, 3.0));
    }
}
