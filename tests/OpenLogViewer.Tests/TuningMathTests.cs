using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The arithmetic behind the calculators.
///
/// Checked against figures a tuner would recognise rather than against the
/// formulas restated, because a formula copied wrongly into a test agrees with
/// the same formula copied wrongly into the code. A calculator that reads
/// plausibly and is out by ten per cent sizes an injector ten per cent small,
/// and the engine finds out before anyone else does.
/// </summary>
public class TuningMathTests
{
    // ----- mixture -------------------------------------------------------------

    [Fact]
    public void PetrolIsStoichiometricAtTheFamiliarNumber()
    {
        Assert.Equal(14.7, TuningMath.Stoichiometric(Fuel.Petrol));
        Assert.Equal(14.7, TuningMath.AfrFromLambda(1.0, Fuel.Petrol), 3);
        Assert.Equal(1.0, TuningMath.LambdaFromAfr(14.7, Fuel.Petrol), 6);
    }

    [Fact]
    public void E85LandsWhereThePublishedFigureDoes()
    {
        // Widely quoted as 9.7 to 9.8. Worth checking, because a volume average
        // of 14.7 and 9.0 gives 9.86 — near enough to look right and wrong for
        // the wrong reason. The two fuels mix by volume and burn by mass.
        double stoich = TuningMath.Stoichiometric(Fuel.E85);

        Assert.InRange(stoich, 9.6, 9.9);
    }

    [Fact]
    public void BlendsSitInOrderBetweenTheirConstituents()
    {
        double petrol = TuningMath.Stoichiometric(Fuel.Petrol);
        double ethanol = TuningMath.Stoichiometric(Fuel.Ethanol);

        double[] blends =
        [
            TuningMath.Stoichiometric(Fuel.E10),
            TuningMath.Stoichiometric(Fuel.E30),
            TuningMath.Stoichiometric(Fuel.E50),
            TuningMath.Stoichiometric(Fuel.E85),
        ];

        Assert.All(blends, b => Assert.InRange(b, ethanol, petrol));
        Assert.True(blends.Zip(blends.Skip(1)).All(p => p.Second < p.First),
            "more ethanol should mean a lower ratio");
    }

    [Fact]
    public void TheSameLambdaIsTheSameRichnessOnEveryFuel()
    {
        // The whole reason lambda is worth using. 0.85 is the same richness on
        // petrol and on E85; 12.5:1 is comfortable on one and lean enough to
        // hurt on the other.
        foreach (Fuel fuel in (Fuel[])[Fuel.Petrol, Fuel.E85, Fuel.Methanol, Fuel.Diesel])
        {
            double afr = TuningMath.AfrFromLambda(0.85, fuel);

            Assert.Equal(0.85, TuningMath.LambdaFromAfr(afr, fuel), 6);
        }
    }

    [Fact]
    public void TwelveAndAHalfToOneIsSafeOnPetrolAndLeanOnE85()
    {
        Assert.InRange(TuningMath.LambdaFromAfr(12.5, Fuel.Petrol), 0.84, 0.86);
        Assert.True(TuningMath.LambdaFromAfr(12.5, Fuel.E85) > 1.25,
            "12.5:1 on E85 should be well lean of stoichiometric");
    }

    // ----- pressure ------------------------------------------------------------

    [Fact]
    public void TenPsiOfBoostIsOneHundredAndSeventyKpaAbsolute()
    {
        // The distinction that catches people out: an ECU reports MAP
        // absolutely, so atmospheric is 101 kPa rather than zero.
        //
        // Ten psi of boost is 24.7 psi absolute, and 24.7 × 6.895 is 170.3 —
        // which is the check, since the same figure arrived at two ways is
        // worth more than the formula restated.
        double gauge = 10 * TuningMath.KpaPerPsi;
        double absolute = TuningMath.AbsoluteFromGauge(gauge);

        Assert.Equal(170.3, absolute, 1);
        Assert.Equal(24.7 * TuningMath.KpaPerPsi, absolute, 1);
        Assert.Equal(gauge, TuningMath.GaugeFromAbsolute(absolute), 6);
    }

    [Fact]
    public void AtmosphericIsZeroBoostAndAPressureRatioOfOne()
    {
        Assert.Equal(0, TuningMath.GaugeFromAbsolute(TuningMath.AtmosphericKpa), 6);
        Assert.Equal(1, TuningMath.PressureRatio(TuningMath.AtmosphericKpa), 6);
    }

    [Fact]
    public void OneBarOfBoostDoublesTheAirPacked()
    {
        double absolute = TuningMath.AbsoluteFromGauge(TuningMath.AtmosphericKpa);

        Assert.Equal(2, TuningMath.PressureRatio(absolute), 6);
    }

    // ----- airflow -------------------------------------------------------------

    [Fact]
    public void ATwoLitreAtSevenThousandDemandsAboutTwoHundredAndFiftyCfm()
    {
        // 122 cubic inches at 7,000 rpm and full volumetric efficiency is a
        // touch under 250 cfm — the figure a carburettor would be picked on.
        double cfm = TuningMath.CubicFeetPerMinute(2.0, 7000, 100);

        Assert.InRange(cfm, 240, 250);
    }

    [Fact]
    public void BoostScalesTheDemandByThePressureRatio()
    {
        double natural = TuningMath.CubicFeetPerMinute(2.0, 7000, 100);
        double boosted = TuningMath.CubicFeetPerMinute(2.0, 7000, 100, pressureRatio: 2);

        Assert.Equal(natural * 2, boosted, 6);
    }

    [Fact]
    public void AirflowInThePoundsPerMinuteACompressorMapUses()
    {
        // 250 cfm is a little over 19 lb/min, which is where a small
        // turbocharger's map is read.
        double cfm = TuningMath.CubicFeetPerMinute(2.0, 7000, 100);

        Assert.InRange(TuningMath.AirPoundsPerMinute(cfm), 18, 20);
    }

    [Fact]
    public void NothingIsDemandedByAnEngineThatIsNotTurning()
    {
        Assert.Equal(0, TuningMath.CubicFeetPerMinute(2.0, 0, 100));
        Assert.Equal(0, TuningMath.CubicFeetPerMinute(0, 7000, 100));
    }

    // ----- injectors -----------------------------------------------------------

    [Fact]
    public void FiveHundredHorsepowerOnFourInjectorsWantsAboutSevenHundredAndFiftyCc()
    {
        // A familiar sizing: 500 hp, four cylinders, boosted, 85 per cent duty.
        double lbHr = TuningMath.InjectorPoundsPerHour(500, 4, TuningMath.BoostedBsfc, 85);
        double cc = TuningMath.CcPerMinute(lbHr);

        Assert.InRange(lbHr, 85, 92);
        Assert.InRange(cc, 850, 940);
    }

    [Fact]
    public void SizingAndItsInverseAgree()
    {
        double lbHr = TuningMath.InjectorPoundsPerHour(400, 6, 0.55, 80);
        double power = TuningMath.SupportedHorsepower(lbHr, 6, 0.55, 80);

        Assert.Equal(400, power, 6);
    }

    [Fact]
    public void ConvertingBetweenPoundsAndCcRoundTrips()
    {
        foreach (Fuel fuel in (Fuel[])[Fuel.Petrol, Fuel.E85, Fuel.Methanol])
        {
            double cc = TuningMath.CcPerMinute(60, fuel);

            Assert.Equal(60, TuningMath.PoundsPerHourFromCc(cc, fuel), 6);
        }
    }

    [Fact]
    public void TheUsualConversionConstantIsNotUsedOnFuelItDoesNotFit()
    {
        // 10.5 cc per lb/hr quietly assumes petrol. On E85 it is out by enough
        // to matter, and in the direction that undersizes the injector.
        double petrol = TuningMath.CcPerMinute(60, Fuel.Petrol) / 60;
        double e85 = TuningMath.CcPerMinute(60, Fuel.E85) / 60;

        Assert.InRange(petrol, 10.0, 10.3);
        Assert.True(e85 < petrol, "denser fuel is fewer cc for the same mass");
    }

    [Fact]
    public void FlowFollowsTheSquareRootOfPressure()
    {
        // A 550 cc injector rated at three bar flows about 635 at four.
        double higher = TuningMath.FlowAtPressure(550, 300, 400);

        Assert.InRange(higher, 630, 640);

        // And unchanged at its rated pressure, which is the case worth being
        // certain of.
        Assert.Equal(550, TuningMath.FlowAtPressure(550, 300, 300), 6);
    }

    [Fact]
    public void AnInjectorHeldFullyOpenIsNotAssumed()
    {
        // Duty is an input rather than a constant, and asking for more flow at
        // a lower duty is the whole point of it.
        double at85 = TuningMath.InjectorPoundsPerHour(500, 4, 0.6, 85);
        double at100 = TuningMath.InjectorPoundsPerHour(500, 4, 0.6, 100);

        Assert.True(at85 > at100);
    }

    // ----- fuel pump -----------------------------------------------------------

    [Fact]
    public void FiveHundredHorsepowerDrinksAboutTwoHundredLitresAnHour()
    {
        double litres = TuningMath.FuelLitresPerHour(500, TuningMath.BoostedBsfc);

        Assert.InRange(litres, 180, 190);
    }

    [Fact]
    public void ThePumpIsChosenWithHeadroomOverWhatIsBurned()
    {
        double burned = TuningMath.FuelLitresPerHour(500, 0.6);
        double pump = TuningMath.PumpLitresPerHour(500, 0.6, headroomPercent: 20);

        Assert.Equal(burned * 1.2, pump, 6);
        Assert.True(pump > burned, "a pump at its limit whenever the engine is, is a pump that dies");
    }

    [Fact]
    public void EthanolNeedsMoreFuelByVolumeForTheSamePower()
    {
        // Which is why a pump sized for petrol is not a pump sized for E85.
        double petrol = TuningMath.FuelLitresPerHour(500, 0.6, Fuel.Petrol);
        double e85 = TuningMath.FuelLitresPerHour(500, 0.85, Fuel.E85);

        Assert.True(e85 > petrol * 1.2, $"E85 wanted {e85:F0} L/h against petrol's {petrol:F0}");
    }

    [Fact]
    public void NonsenseInputsProduceNothingRatherThanInfinity()
    {
        Assert.Equal(0, TuningMath.InjectorPoundsPerHour(500, 0, 0.6, 85));
        Assert.Equal(0, TuningMath.InjectorPoundsPerHour(500, 4, 0.6, 0));
        Assert.Equal(0, TuningMath.FuelLitresPerHour(0, 0.6));
        Assert.Equal(0, TuningMath.FlowAtPressure(550, 0, 400));
    }
}
