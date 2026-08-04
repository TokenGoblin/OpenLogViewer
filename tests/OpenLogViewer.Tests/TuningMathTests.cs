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
    public void MassWeightingABlendIsACorrectionOfUnderOnePerCent()
    {
        // Pinned because the reason for doing it this way was once written down
        // as a difference of half a ratio, and it is a twentieth of one. The
        // derivation is right; the size of it is not an argument for anything,
        // and a calculator that oversells its own precision is not trustworthy
        // about the rest.
        foreach (double byVolume in (double[])[0.10, 0.30, 0.50, 0.85])
        {
            Fuel fuel = byVolume switch
            {
                0.10 => Fuel.E10,
                0.30 => Fuel.E30,
                0.50 => Fuel.E50,
                _ => Fuel.E85,
            };

            double byMass = TuningMath.Stoichiometric(fuel);
            double byVolumeAverage = (byVolume * 9.0) + ((1 - byVolume) * 14.7);

            Assert.InRange(Math.Abs(byMass - byVolumeAverage) / byMass, 0, 0.01);
        }
    }

    [Fact]
    public void ADensityIsPublishedForEveryFuelAndBlendsFallBetween()
    {
        foreach (Fuel fuel in Enum.GetValues<Fuel>())
            Assert.True(TuningMath.Density(fuel) > 0, $"{fuel} has no density");

        Assert.Equal(0.7824, TuningMath.Density(Fuel.E85), 4);
        Assert.InRange(
            TuningMath.Density(Fuel.E50),
            TuningMath.Density(Fuel.Petrol),
            TuningMath.Density(Fuel.Ethanol));
    }

    // ----- energy and BSFC -----------------------------------------------------

    [Fact]
    public void E85WantsHalfAgainThePetrolBsfcAndMethanolTwice()
    {
        // The figure that decides the injector, and the one this window used to
        // get wrong: it offered "0.75 to 0.85 on E85 where petrol would be 0.60"
        // — a scaling of 1.25 to 1.42 where the energy content says 1.49 — and
        // offered the same sentence to anyone who picked methanol, which wants
        // more than twice.
        Assert.InRange(TuningMath.SuggestedBsfc(Fuel.E85, 0.60), 0.86, 0.92);
        Assert.InRange(TuningMath.SuggestedBsfc(Fuel.Methanol, 0.60), 1.25, 1.35);
        Assert.Equal(0.60, TuningMath.SuggestedBsfc(Fuel.Petrol, 0.60), 6);
    }

    [Fact]
    public void MethanolIsNotGivenEthanolsAdviceEvenThoughBothAreAlcohols()
    {
        // Following E85's figure on methanol sizes the injector at not much
        // over two thirds of what it needs, which is the failure this exists to
        // prevent.
        double e85 = TuningMath.SuggestedBsfc(Fuel.E85, 0.60);
        double methanol = TuningMath.SuggestedBsfc(Fuel.Methanol, 0.60);

        Assert.True(methanol > e85 * 1.3,
            $"methanol wanted {methanol:F2} against E85's {e85:F2} — too close to be right");
    }

    [Fact]
    public void GaseousFuelsCarryMoreEnergyAndWantALowerBsfc()
    {
        // The direction that a sentence written around ethanol would get wrong:
        // not every alternative fuel needs more of it.
        Assert.True(TuningMath.SuggestedBsfc(Fuel.Lpg, 0.60) < 0.60);
        Assert.True(TuningMath.SuggestedBsfc(Fuel.Cng, 0.60) < 0.60);
    }

    [Fact]
    public void DieselIsAnsweredByItsEngineRatherThanByScalingPetrol()
    {
        // Diesel fuel is within two per cent of petrol's energy per kilogram, so
        // scaling would hand back 0.61 for an engine that runs near 0.36.
        Assert.Equal(TuningMath.DieselBsfc, TuningMath.SuggestedBsfc(Fuel.Diesel, 0.60), 6);
        Assert.InRange(TuningMath.SuggestedBsfc(Fuel.Diesel, 0.60), 0.30, 0.42);
    }

    [Fact]
    public void EveryFuelIsAdvisedWithItsOwnFigureRatherThanAnothersSentence()
    {
        // The failure this replaced: one sentence quoting E85's BSFC was shown
        // for every fuel but petrol and diesel. Each fuel's advice should now
        // contain the figure that fuel actually wants.
        foreach (Fuel fuel in Enum.GetValues<Fuel>())
        {
            string guidance = TuningMath.BsfcGuidance(fuel);

            Assert.False(string.IsNullOrWhiteSpace(guidance), $"{fuel} has no guidance");

            if (fuel == Fuel.Petrol) continue;

            double want = TuningMath.SuggestedBsfc(fuel, TuningMath.BoostedBsfc);

            Assert.Contains(want.ToString("N2"), guidance);

            // And the short line beside the box agrees with the long one below it.
            Assert.Contains(
                fuel == Fuel.Diesel ? TuningMath.DieselBsfc.ToString("N2") : want.ToString("N2"),
                TuningMath.BsfcHint(fuel));
        }
    }

    [Fact]
    public void NoFuelIsToldToUseTheFigureThatBelongsToAnother()
    {
        // Specifically: methanol must not be handed E85's 0.89, which is what
        // choosing methanol used to produce.
        string methanol = TuningMath.BsfcGuidance(Fuel.Methanol);

        Assert.Contains("1.31", methanol);
        Assert.DoesNotContain("0.89", methanol);
        Assert.DoesNotContain("E85", methanol);
    }

    [Fact]
    public void TheGasesAreToldTheyNeedLessRatherThanMore()
    {
        // A sentence generalised from ethanol says every alternative fuel needs
        // more. LPG and CNG need less, and neither is injected as a liquid.
        foreach (Fuel fuel in (Fuel[])[Fuel.Lpg, Fuel.Cng])
        {
            string guidance = TuningMath.BsfcGuidance(fuel);

            Assert.Contains("less", guidance);
            Assert.Contains("gas", guidance);
        }
    }

    [Fact]
    public void TheDieselWarningSaysTheSizingDoesNotDescribeIt()
    {
        // Cc per minute at three bar is not how a common-rail injector is
        // specified, and the tab will happily print one anyway.
        string guidance = TuningMath.BsfcGuidance(Fuel.Diesel);

        Assert.Contains("0.36", guidance);
        Assert.Contains("does not describe", guidance);
    }

    [Fact]
    public void BsfcScalesInStepWithHowMuchEnergyTheFuelIsShortOf()
    {
        // The two are the same statement, and this is the check that they stay
        // that way if either table is edited.
        foreach (Fuel fuel in Enum.GetValues<Fuel>())
        {
            if (fuel == Fuel.Diesel) continue;

            double energyShare = TuningMath.EnergyMjPerKg(fuel) / TuningMath.EnergyMjPerKg(Fuel.Petrol);

            Assert.Equal(0.60 / energyShare, TuningMath.SuggestedBsfc(fuel, 0.60), 6);
        }
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
    public void TheBoostFigureQuotedInTheProseIsTheOneTheCodeProduces()
    {
        // The window and the comments both promise "170 kPa, not 69" to anyone
        // reading them, and one of them said 169. A calculator whose own
        // explanation is out by a kilopascal invites doubt about the rest of it.
        Assert.Equal(170, TuningMath.AbsoluteFromGauge(10 * TuningMath.KpaPerPsi), 0);
    }

    [Fact]
    public void AltitudeIsAnArgumentBecauseItIsNotAConstant()
    {
        // A mile up, atmospheric is nearer 83 kPa. The same ten psi on the gauge
        // is then 152 kPa absolute against sea level's 170, and a pressure ratio
        // of 1.83 against 1.68 — which is a different turbocharger.
        const double Mile = 83.0;

        double gauge = 10 * TuningMath.KpaPerPsi;
        double absolute = TuningMath.AbsoluteFromGauge(gauge, Mile);

        Assert.Equal(152, absolute, 0);
        Assert.InRange(TuningMath.PressureRatio(absolute, Mile), 1.8, 1.9);
        Assert.InRange(TuningMath.PressureRatio(TuningMath.AbsoluteFromGauge(gauge)), 1.6, 1.7);
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

    // ----- altitude ------------------------------------------------------------

    [Fact]
    public void BarometricPressureMatchesThePublishedAltitudeTable()
    {
        // The standard atmosphere is tabulated everywhere, so this is checked
        // against the table rather than against the formula restated. Feet,
        // because that is how altitude is signposted where turbochargers are
        // argued about.
        (double Feet, double Kpa)[] published =
        [
            (0, 101.325),
            (1_000, 97.72),
            (2_500, 92.50),
            (5_000, 84.31),
            (7_500, 76.71),
            (10_000, 69.68),
            (14_000, 59.53),
        ];

        foreach ((double feet, double kpa) in published)
        {
            double metres = feet * TuningMath.MetresPerFoot;

            Assert.Equal(kpa, TuningMath.BarometricKpa(metres), 1);
        }
    }

    [Fact]
    public void AltitudeAndBarometricAreEachOthersInverse()
    {
        foreach (double feet in (double[])[0, 1_000, 5_280, 10_000])
        {
            double metres = feet * TuningMath.MetresPerFoot;

            Assert.Equal(metres, TuningMath.AltitudeMetres(TuningMath.BarometricKpa(metres)), 6);
        }
    }

    [Fact]
    public void SeaLevelIsTheDefaultAndIsTheStandardAtmosphere()
    {
        Assert.Equal(TuningMath.AtmosphericKpa, TuningMath.BarometricKpa(0), 6);
        Assert.Equal(0, TuningMath.AltitudeMetres(TuningMath.AtmosphericKpa), 6);
    }

    [Fact]
    public void AMileUpIsTheEightyThreeKilopascalsQuotedInTheProse()
    {
        // Denver, and the figure the boost documentation promises.
        Assert.Equal(83.4, TuningMath.BarometricKpa(5_280 * TuningMath.MetresPerFoot), 1);
    }

    // ----- compressor ----------------------------------------------------------

    [Fact]
    public void ThePressureRatioMatchesTheTurbochargerMakersWorkedExample()
    {
        // Garrett's own sizing example, which is the reason to trust this at
        // all: 12 psi of boost at sea level, 2 psi lost through the intercooler
        // and 1 psi through the filter, comes out at 2.09.
        //
        // 28.7 psia over 13.7 psia. Checked in psi as well as kPa, because
        // arriving at the same answer through a different unit is worth more
        // than the formula restated.
        TuningMath.Compressor c = TuningMath.CompressorPressures(
            12 * TuningMath.KpaPerPsi,
            TuningMath.AtmosphericKpa,
            inletLossKpa: 1 * TuningMath.KpaPerPsi,
            chargeLossKpa: 2 * TuningMath.KpaPerPsi);

        // 2.0952 against his 2.0949: the difference is that he rounds the
        // atmosphere to a flat 14.7 psia where this uses 101.325 kPa exactly,
        // and the two agree to four significant figures.
        Assert.InRange(c.Ratio, 2.09, 2.10);
        Assert.Equal(13.7, c.InletKpa / TuningMath.KpaPerPsi, 1);
        Assert.Equal(28.7, c.OutletKpa / TuningMath.KpaPerPsi, 1);

        // And the defaults are those same conventional losses, so the worked
        // example is what the tab shows before anything is typed into it.
        Assert.Equal(
            c.Ratio,
            TuningMath.CompressorPressures(
                12 * TuningMath.KpaPerPsi,
                TuningMath.BarometricKpa(0),
                TuningMath.TypicalInletLossKpa,
                TuningMath.TypicalChargeLossKpa).Ratio,
            6);
    }

    [Fact]
    public void LeavingTheLossesOutFlattersTheTurbocharger()
    {
        // Both losses push the ratio up. A compressor picked without them is
        // picked on a smaller number than it will actually be asked for, which
        // is the direction that runs out of map.
        double bare = TuningMath.CompressorPressures(12 * TuningMath.KpaPerPsi).Ratio;
        double honest = TuningMath.CompressorPressures(
            12 * TuningMath.KpaPerPsi,
            inletLossKpa: TuningMath.TypicalInletLossKpa,
            chargeLossKpa: TuningMath.TypicalChargeLossKpa).Ratio;

        Assert.Equal(1.82, bare, 2);
        Assert.True(honest > bare, $"losses should raise the ratio, but {honest:F2} <= {bare:F2}");
        Assert.InRange(honest, 2.05, 2.15);

        // Fifteen per cent, which is the difference between a compressor sitting
        // in the middle of its map and one against the edge of it.
        Assert.InRange((honest / bare) - 1, 0.13, 0.17);
    }

    [Fact]
    public void TheSameBoostAsksMoreOfACompressorHigherUp()
    {
        // A gauge reads against whatever the engine is breathing, so the same
        // number on the dial is a bigger job in the mountains. This is the whole
        // reason altitude is an input.
        double sea = TuningMath.CompressorPressures(
            12 * TuningMath.KpaPerPsi,
            TuningMath.BarometricKpa(0),
            TuningMath.TypicalInletLossKpa,
            TuningMath.TypicalChargeLossKpa).Ratio;

        double mountain = TuningMath.CompressorPressures(
            12 * TuningMath.KpaPerPsi,
            TuningMath.BarometricKpa(5_000 * TuningMath.MetresPerFoot),
            TuningMath.TypicalInletLossKpa,
            TuningMath.TypicalChargeLossKpa).Ratio;

        Assert.InRange(sea, 2.05, 2.15);
        Assert.InRange(mountain, 2.3, 2.45);
        Assert.True(mountain > sea * 1.1, "five thousand feet should be well over a tenth more");
    }

    [Fact]
    public void ACompressorCannotBreatheWhatIsNotThere()
    {
        // An inlet loss larger than the air available is nonsense, not a
        // negative pressure ratio.
        Assert.True(double.IsNaN(
            TuningMath.CompressorPressures(100, TuningMath.AtmosphericKpa, inletLossKpa: 200).Ratio));
    }

    [Fact]
    public void TheTwoPressureRatiosMoveInOppositeDirectionsWithAltitude()
    {
        // Both are called the pressure ratio and they are not the same number.
        // The compressor's is against the air it breathes and rises as you
        // climb; the charge density is against sea level and falls, because the
        // same boost on the gauge is less absolute pressure in the manifold.
        //
        // Conflating them — dividing the charge density by local barometric —
        // cancels the altitude out and overstates the air by a fifth.
        double boost = 10 * TuningMath.KpaPerPsi;
        double high = TuningMath.BarometricKpa(5_000 * TuningMath.MetresPerFoot);

        double compressorSea = TuningMath.CompressorPressures(boost).Ratio;
        double compressorHigh = TuningMath.CompressorPressures(boost, high).Ratio;

        double chargeSea = TuningMath.ChargeDensityRatio(TuningMath.AbsoluteFromGauge(boost));
        double chargeHigh = TuningMath.ChargeDensityRatio(TuningMath.AbsoluteFromGauge(boost, high));

        Assert.True(compressorHigh > compressorSea, "the compressor is asked for more up high");
        Assert.True(chargeHigh < chargeSea, "and yet there is less air in the manifold");

        // The size of the mistake, if the charge density were taken against the
        // local air like the compressor's is.
        double wrong = TuningMath.PressureRatio(TuningMath.AbsoluteFromGauge(boost, high), high);

        Assert.InRange(wrong / chargeHigh, 1.15, 1.25);
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
    public void TheSameBoostIsLessAirHigherUp()
    {
        // The consequence a tuner would recognise, and the reason the altitude
        // has to reach the airflow tab rather than only the compressor one: the
        // gauge still says ten psi in the mountains and the engine still makes
        // less power, because absolute pressure is what it breathes.
        static double AirAt(double feet)
        {
            double baro = TuningMath.BarometricKpa(feet * TuningMath.MetresPerFoot);
            double map = TuningMath.AbsoluteFromGauge(10 * TuningMath.KpaPerPsi, baro);

            return TuningMath.AirPoundsPerMinute(
                TuningMath.CubicFeetPerMinute(2.0, 7000, 95, TuningMath.ChargeDensityRatio(map)));
        }

        double sea = AirAt(0);
        double mountain = AirAt(5_000);

        Assert.InRange(sea, 29, 31);
        Assert.InRange(mountain, 26, 28);
        Assert.InRange(1 - (mountain / sea), 0.08, 0.12);
    }

    [Fact]
    public void NothingIsDemandedByAnEngineThatIsNotTurning()
    {
        Assert.Equal(0, TuningMath.CubicFeetPerMinute(2.0, 0, 100));
        Assert.Equal(0, TuningMath.CubicFeetPerMinute(0, 7000, 100));
    }

    // ----- power from air ------------------------------------------------------

    [Fact]
    public void TheFamiliarTenHorsepowerPerPoundOfAirComesOut()
    {
        // The rule of thumb everyone quotes, recovered from the arithmetic
        // rather than written in as a constant — which also pins what it
        // quietly assumes, since it is only true at these figures.
        double hp = TuningMath.HorsepowerFromAir(1, Fuel.Petrol, 0.85, 0.48);

        Assert.Equal(10.0, hp, 1);

        // And the turbocharger maker's 9.5, which assumes a richer mixture and
        // a worse BSFC. Both are "the" figure, depending on who is quoting it.
        Assert.Equal(9.5, TuningMath.HorsepowerFromAir(1, Fuel.Petrol, 0.78, 0.55), 1);
    }

    [Fact]
    public void ThirtyPoundsOfAirAMinuteIsAboutThreeHundredHorsepower()
    {
        // A figure a tuner would recognise: a 2.0 at 30 lb/min is a 300 hp car.
        double hp = TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0.85, TuningMath.FullThrottleBsfc);

        Assert.InRange(hp, 280, 300);
    }

    [Fact]
    public void TheSameAirIsNearlyTheSamePowerOnEveryFuel()
    {
        // The answer most of a workshop would argue with, and the reason to
        // compute it rather than assert it: a pound of air carries about as much
        // energy whichever fuel arrives with it, so at one lambda and one
        // efficiency the alcohols are worth a few per cent and no more.
        double petrol = TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0.85, 0.50);
        double ethanol = TuningMath.HorsepowerFromAir(30, Fuel.Ethanol, 0.85, 0.50);
        double methanol = TuningMath.HorsepowerFromAir(30, Fuel.Methanol, 0.85, 0.50);

        Assert.InRange((ethanol / petrol) - 1, 0.00, 0.02);
        Assert.InRange((methanol / petrol) - 1, 0.03, 0.06);

        // Ordered, and all three within five per cent of each other.
        Assert.True(methanol > ethanol && ethanol > petrol);
        Assert.InRange(methanol / petrol, 1.0, 1.05);
    }

    [Fact]
    public void ThePowerComparisonIsWhatTheEnergyPerPoundOfAirSays()
    {
        // The two are the same statement: power per unit air tracks the energy
        // each fuel brings with a unit of air at stoichiometric. Checked against
        // that independently, so a mistake in one would not agree with the other.
        foreach (Fuel fuel in (Fuel[])[Fuel.Petrol, Fuel.E85, Fuel.Ethanol, Fuel.Methanol])
        {
            double perAir = TuningMath.EnergyMjPerKg(fuel) / TuningMath.Stoichiometric(fuel);
            double petrolPerAir =
                TuningMath.EnergyMjPerKg(Fuel.Petrol) / TuningMath.Stoichiometric(Fuel.Petrol);

            double hp = TuningMath.HorsepowerFromAir(30, fuel, 0.85, 0.50);
            double petrolHp = TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0.85, 0.50);

            Assert.Equal(perAir / petrolPerAir, hp / petrolHp, 6);
        }
    }

    [Fact]
    public void RicherAndMoreEfficientBothMakeMorePowerFromTheSameAir()
    {
        double baseline = TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0.85, 0.50);

        Assert.True(TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0.80, 0.50) > baseline);
        Assert.True(TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0.85, 0.45) > baseline);
    }

    [Fact]
    public void PowerFromNoAirOrNoMixtureIsNothing()
    {
        Assert.Equal(0, TuningMath.HorsepowerFromAir(0, Fuel.Petrol, 0.85, 0.50));
        Assert.Equal(0, TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0, 0.50));
        Assert.Equal(0, TuningMath.HorsepowerFromAir(30, Fuel.Petrol, -1, 0.50));
        Assert.Equal(0, TuningMath.HorsepowerFromAir(30, Fuel.Petrol, 0.85, 0));
    }

    [Fact]
    public void EstimatingPowerAndSizingAPumpUseTheSameNumberDifferently()
    {
        // Deliberately different defaults. Sizing is pessimistic on purpose,
        // because oversizing a pump costs money and undersizing one costs an
        // engine; estimating power the same way would understate the engine.
        Assert.True(TuningMath.FullThrottleBsfc < TuningMath.BoostedBsfc);
        Assert.InRange(TuningMath.FullThrottleBsfc, 0.45, 0.55);
    }

    // ----- carrying a BSFC across a change of fuel ------------------------------

    [Fact]
    public void MovingAFigureBetweenFuelsAndBackLeavesItWhereItStarted()
    {
        foreach (Fuel fuel in Enum.GetValues<Fuel>())
        {
            if (fuel == Fuel.Diesel) continue;

            double moved = TuningMath.SuggestedBsfc(fuel, 0.52);

            Assert.Equal(0.52, TuningMath.PetrolEquivalentBsfc(fuel, moved), 6);
        }
    }

    [Fact]
    public void ChangingFuelKeepsAnAspiratedChoiceAspirated()
    {
        // Somebody who typed 0.48 means "naturally aspirated", and switching to
        // E85 should give them E85's aspirated figure rather than its boosted
        // one. Replacing the box instead of scaling it loses that.
        double petrolEquivalent = TuningMath.PetrolEquivalentBsfc(Fuel.Petrol, 0.48);
        double onE85 = TuningMath.SuggestedBsfc(Fuel.E85, petrolEquivalent);

        Assert.Equal(TuningMath.SuggestedBsfc(Fuel.E85, TuningMath.NaturallyAspiratedBsfc), onE85, 6);
        Assert.InRange(onE85, 0.68, 0.74);
        Assert.True(onE85 < TuningMath.SuggestedBsfc(Fuel.E85, TuningMath.BoostedBsfc));
    }

    [Fact]
    public void DieselHasNoPetrolEquivalentToCarryAcross()
    {
        Assert.True(double.IsNaN(TuningMath.PetrolEquivalentBsfc(Fuel.Diesel, 0.36)));
        Assert.True(double.IsNaN(TuningMath.PetrolEquivalentBsfc(Fuel.Petrol, 0)));
    }

    [Fact]
    public void TheLegendAgreesWithTheArithmeticItSitsNextTo()
    {
        // A printed table that drifts from the calculator beside it is worse
        // than no table, so it is generated from the same function.
        string legend = TuningMath.BsfcLegend(Fuel.E85);

        foreach (Fuel fuel in Enum.GetValues<Fuel>())
        {
            Assert.Contains(TuningMath.ShortName(fuel), legend);

            if (fuel == Fuel.Diesel) continue;

            Assert.Contains(
                TuningMath.SuggestedBsfc(fuel, TuningMath.BoostedBsfc).ToString("N2"), legend);
        }

        Assert.Contains(TuningMath.DieselBsfc.ToString("N2"), legend);

        // And it marks the fuel that is actually selected.
        Assert.Contains("▸ E85", legend);
        Assert.DoesNotContain("▸ Petrol", legend);
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
        double petrol = TuningMath.CcPerMinute(60, Fuel.Petrol) / 60;
        double e85 = TuningMath.CcPerMinute(60, Fuel.E85) / 60;

        Assert.InRange(petrol, 10.0, 10.3);
        Assert.True(e85 < petrol, "denser fuel is fewer cc for the same mass");
    }

    [Fact]
    public void TheConventionalConstantErrsLargeOnEthanolRatherThanSmall()
    {
        // Pinned because this was documented backwards. 10.5 cc/min per lb/hr is
        // a density of 0.72; every fuel here is denser, so the constant always
        // asks for more cc than the mass needs. It oversizes an injector on
        // ethanol — it does not undersize one.
        //
        // What undersizes an injector on ethanol is the BSFC, which is a much
        // larger error in the genuinely dangerous direction.
        foreach (Fuel fuel in (Fuel[])[Fuel.Petrol, Fuel.E85, Fuel.Ethanol, Fuel.Methanol])
        {
            double honest = TuningMath.CcPerMinute(60, fuel) / 60;

            Assert.True(10.5 > honest,
                $"10.5 should be the larger figure on {fuel}, but the honest one was {honest:F2}");
        }

        // And the size of it, which was written down as five per cent.
        double e85 = TuningMath.CcPerMinute(60, Fuel.E85) / 60;

        Assert.InRange((10.5 / e85) - 1, 0.07, 0.11);
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

    // ----- pumps as they are sold ----------------------------------------------

    [Fact]
    public void EveryPumpInTheCatalogueIsDescribedCompletely()
    {
        // The catalogue is the part of this file that goes stale on its own, so
        // what can be checked mechanically is: nothing blank, nothing zero, and
        // a rating pressure on every entry — the figure that makes the flow
        // figure mean anything.
        Assert.NotEmpty(TuningMath.Pumps);

        foreach (TuningMath.FuelPump pump in TuningMath.Pumps)
        {
            Assert.False(string.IsNullOrWhiteSpace(pump.Maker));
            Assert.False(string.IsNullOrWhiteSpace(pump.Part));
            Assert.InRange(pump.LitresPerHour, 100, 1_000);
            Assert.InRange(pump.RatedPsi, 30, 60);
        }

        // Both makers the tab names, and part numbers that are not duplicated.
        Assert.Contains(TuningMath.Pumps, p => p.Maker == "Walbro");
        Assert.Contains(TuningMath.Pumps, p => p.Maker == "AEM");
        Assert.Equal(
            TuningMath.Pumps.Count,
            TuningMath.Pumps.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public void APumpMakesLessThanItsRatingAtAnyPressureAboveIt()
    {
        // The mistake the suggestion exists to stop: the number on the box is
        // measured at about 40 psi, and a boosted rail is nowhere near that.
        TuningMath.FuelPump walbro255 =
            TuningMath.Pumps.First(p => p.Part == "GSS342");

        Assert.Equal(255, TuningMath.PumpFlowAtPressure(walbro255, walbro255.RatedPsi), 6);

        // Published curves put the 255 near 190 L/h at 70 psi and near 150 at
        // 87 — checked against those rather than against the model restated.
        Assert.InRange(TuningMath.PumpFlowAtPressure(walbro255, 70), 180, 205);
        Assert.InRange(TuningMath.PumpFlowAtPressure(walbro255, 87), 140, 165);

        // And more than its rating below it, which is the other direction.
        Assert.True(TuningMath.PumpFlowAtPressure(walbro255, 30) > 255);
    }

    [Fact]
    public void PumpsInParallelAreWorthLessThanTheSumOfThem()
    {
        // They share a line, a filter and a regulator. Counting two as exactly
        // twice one is the optimistic direction, which is the wrong one.
        Assert.Equal(400, TuningMath.ParallelFlow(400, 1), 6);
        Assert.True(TuningMath.ParallelFlow(400, 2) < 800);
        Assert.InRange(TuningMath.ParallelFlow(400, 2), 740, 780);
        Assert.InRange(TuningMath.ParallelFlow(400, 3), 1_100, 1_180);
    }

    [Fact]
    public void AModestPetrolCarIsOfferedOneOrdinaryPump()
    {
        // 400 hp on petrol at 43.5 psi is a single 255's job, and the answer
        // should say so rather than reaching for something exotic.
        double needed = TuningMath.PumpLitresPerHour(400, TuningMath.BoostedBsfc);

        var picks = TuningMath.SuggestPumps(needed, 43.5, alcoholSafe: false);

        Assert.NotEmpty(picks);
        Assert.Equal(1, picks[0].Count);
        Assert.True(picks[0].DeliveredLitresPerHour >= needed);
    }

    [Fact]
    public void NoPumpIsOfferedThatCannotDeliverWhatWasAskedOfIt()
    {
        foreach (double hp in (double[])[300, 500, 700, 900])
        foreach (double rail in (double[])[43.5, 60, 75])
        {
            double needed = TuningMath.PumpLitresPerHour(hp, TuningMath.BoostedBsfc);

            foreach (var pick in TuningMath.SuggestPumps(needed, rail, alcoholSafe: false))
            {
                Assert.True(pick.DeliveredLitresPerHour >= needed,
                    $"{pick.Pump.Name} × {pick.Count} was offered for {needed:F0} L/h at {rail} psi "
                    + $"but delivers {pick.DeliveredLitresPerHour:F0}");

                Assert.InRange(pick.Count, 1, TuningMath.MostPumpsWorthWiring);
            }
        }
    }

    [Fact]
    public void AlcoholOnlyEverGetsPumpsRatedForIt()
    {
        // A pump not rated for ethanol will pass fuel for a while and then fail,
        // which is the worst way to find out. E85 is not a filter to get wrong.
        double needed = TuningMath.PumpLitresPerHour(500, 0.89, Fuel.E85);

        var picks = TuningMath.SuggestPumps(needed, 50, alcoholSafe: true);

        Assert.NotEmpty(picks);
        Assert.All(picks, p => Assert.True(p.Pump.AlcoholSafe, $"{p.Pump.Name} is not rated for it"));

        Assert.True(TuningMath.NeedsAlcoholSafePump(Fuel.E85));
        Assert.True(TuningMath.NeedsAlcoholSafePump(Fuel.Methanol));
        Assert.False(TuningMath.NeedsAlcoholSafePump(Fuel.Petrol));
    }

    [Fact]
    public void FewestPumpsComesFirstAndTheLeastWastefulAfterThat()
    {
        double needed = TuningMath.PumpLitresPerHour(600, TuningMath.BoostedBsfc, Fuel.E85);

        var picks = TuningMath.SuggestPumps(needed, 55, alcoholSafe: true);

        Assert.NotEmpty(picks);

        for (int i = 1; i < picks.Count; i++)
        {
            Assert.True(picks[i].Count >= picks[i - 1].Count, "counts should not go back down");

            if (picks[i].Count == picks[i - 1].Count)
                Assert.True(picks[i].DeliveredLitresPerHour >= picks[i - 1].DeliveredLitresPerHour);
        }
    }

    [Fact]
    public void SomethingRidiculousIsToldToStopBuyingInTankPumps()
    {
        // A thousand horsepower on methanol at a high rail is past what three
        // in-tank pumps do, and the honest answer is a different kind of pump
        // rather than a fourth of the same one.
        double needed = TuningMath.PumpLitresPerHour(
            1_500, TuningMath.SuggestedBsfc(Fuel.Methanol, TuningMath.BoostedBsfc), Fuel.Methanol);

        Assert.Empty(TuningMath.SuggestPumps(needed, 75, alcoholSafe: true));
    }

    [Fact]
    public void NothingIsSuggestedWithoutAPressureToSuggestItAt()
    {
        Assert.Empty(TuningMath.SuggestPumps(300, 0, alcoholSafe: false));
        Assert.Empty(TuningMath.SuggestPumps(0, 43.5, alcoholSafe: false));
        Assert.Empty(TuningMath.SuggestPumps(double.NaN, 43.5, alcoholSafe: false));
    }

    [Fact]
    public void RaisingTheRailCostsPumpsAndCanCostAWholeExtraOne()
    {
        // Twenty psi of boost sits on top of the base pressure, and the pump
        // feels all of it. The same engine can need two pumps at 63 psi where it
        // needed one at 43.
        double needed = TuningMath.PumpLitresPerHour(600, TuningMath.BoostedBsfc);

        var atBase = TuningMath.SuggestPumps(needed, 43.5, alcoholSafe: false);
        var atBoost = TuningMath.SuggestPumps(needed, 63.5, alcoholSafe: false);

        Assert.NotEmpty(atBase);
        Assert.NotEmpty(atBoost);
        Assert.True(atBoost[0].Count >= atBase[0].Count);
    }

    [Fact]
    public void NonsenseInputsProduceNothingRatherThanInfinity()
    {
        Assert.Equal(0, TuningMath.InjectorPoundsPerHour(500, 0, 0.6, 85));
        Assert.Equal(0, TuningMath.InjectorPoundsPerHour(500, 4, 0.6, 0));
        Assert.Equal(0, TuningMath.FuelLitresPerHour(0, 0.6));
        Assert.Equal(0, TuningMath.FlowAtPressure(550, 0, 400));
    }

    [Fact]
    public void NothingAtOrBelowZeroIsAMixture()
    {
        // A minus sign typed into the lambda box used to come back as an
        // air-fuel ratio and a verdict of "safe".
        Assert.True(double.IsNaN(TuningMath.AfrFromLambda(-1, Fuel.Petrol)));
        Assert.True(double.IsNaN(TuningMath.AfrFromLambda(0, Fuel.Petrol)));
        Assert.True(double.IsNaN(TuningMath.LambdaFromAfr(-14.7, Fuel.Petrol)));
        Assert.True(double.IsNaN(TuningMath.LambdaFromAfr(0, Fuel.Petrol)));
    }

    // ----- the unit conversions nothing else covers ----------------------------

    [Fact]
    public void TheAirflowUnitsAreThreeViewsOfOneNumber()
    {
        // 250 cfm, spelled three ways. Checked against the conversions rather
        // than against the code, since a mistyped constant here reads perfectly.
        double cfm = 250;

        // A cubic foot is 28.3168 litres, so 250 a minute is 424.75 m³/h.
        Assert.Equal(424.75, TuningMath.CubicMetresPerHour(cfm), 1);

        // Standard air is 0.0765 lb/ft³, so 250 cfm is 19.1 lb/min.
        Assert.Equal(19.12, TuningMath.AirPoundsPerMinute(cfm), 1);
    }

    [Fact]
    public void APoundOfFuelAnHourIsTheSameQuantityWhicheverWayItIsWritten()
    {
        // 60 lb/hr on petrol: 27.2 kg/hr, 36.5 litres an hour, 609 cc a minute.
        Assert.Equal(609, TuningMath.CcPerMinute(60, Fuel.Petrol), 0);

        // The pump calculation should agree with it — 60 lb/hr is what 100 hp
        // at a BSFC of 0.60 burns, and 609 cc/min is 36.5 litres an hour.
        Assert.Equal(36.5, TuningMath.FuelLitresPerHour(100, 0.60, Fuel.Petrol), 1);
    }

    // ----- temperature ---------------------------------------------------------

    [Theory]
    // The fixed points, and the one place the two scales cross.
    [InlineData(32, 0)]
    [InlineData(212, 100)]
    [InlineData(-40, -40)]
    [InlineData(68, 20)]
    [InlineData(130, 54.44444)]
    public void FahrenheitAndCelsiusAreTheSameTemperature(double f, double c)
    {
        Assert.Equal(c, TuningMath.CelsiusFromFahrenheit(f), 3);
        Assert.Equal(f, TuningMath.FahrenheitFromCelsius(c), 3);
    }

    [Fact]
    public void TheOffsetIsAppliedBeforeTheFactorAndNotAfter()
    {
        // The mistake this exists to prevent. Scaling first and then adding
        // gives 88 for a 40 degree day rather than 104 — a number that looks
        // like a temperature and is sixteen degrees out.
        Assert.Equal(104, TuningMath.FahrenheitFromCelsius(40), 6);
        Assert.NotEqual(88, TuningMath.FahrenheitFromCelsius(40), 6);

        foreach (double c in (double[])[-40, 0, 25, 55, 100])
            Assert.Equal(c, TuningMath.CelsiusFromFahrenheit(TuningMath.FahrenheitFromCelsius(c)), 9);
    }

    // ----- gallons -------------------------------------------------------------

    [Fact]
    public void AGallonIsTheGallonsOwnDefinition()
    {
        // 3.785411784 litres exactly, so the reciprocal is taken from that rather
        // than typed as a rounded 0.2642 — these figures end up compared against
        // a pump's printed rating.
        Assert.Equal(1 / 3.785411784, TuningMath.UsGallonsPerLitre, 12);
        Assert.Equal(3.785411784, 1 / TuningMath.UsGallonsPerLitre, 9);
    }

    [Fact]
    public void LitresAnHourBecomeGallonsAMinute()
    {
        // A pump rated 255 L/h is a shade over a gallon a minute, which is the
        // figure the larger and mechanical pumps are quoted in.
        Assert.Equal(1.123, TuningMath.GallonsPerMinute(255), 3);

        // And the two gallon figures agree with each other.
        foreach (double litresPerHour in (double[])[100, 183, 255, 450])
            Assert.Equal(
                litresPerHour * TuningMath.UsGallonsPerLitre / 60,
                TuningMath.GallonsPerMinute(litresPerHour),
                9);
    }

    [Fact]
    public void ThePumpFiguresAgreeAcrossEveryUnitTheyAreQuotedIn()
    {
        // 500 hp at a BSFC of 0.5 burns 152 L/h of petrol; with a fifth in hand
        // that is 183 L/h, 48.3 US gallons an hour, and 0.80 a minute.
        double pump = TuningMath.PumpLitresPerHour(500, 0.50);

        Assert.Equal(183, pump, 0);
        Assert.Equal(48.3, pump * TuningMath.UsGallonsPerLitre, 1);
        Assert.Equal(0.80, TuningMath.GallonsPerMinute(pump), 2);
    }
}
