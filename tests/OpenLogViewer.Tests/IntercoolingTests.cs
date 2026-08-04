using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Cooling a charge, by a core and by spraying something into it.
///
/// The air side is textbook and checkable by hand. The spray side is where the
/// mistakes live, and they are all unit conversions: nozzles are sold in cc per
/// minute and the thermodynamics is in pounds; water-methanol is mixed by volume
/// and behaves by mass; and methanol is a fuel as well as a coolant, so a spray
/// sized purely for temperature quietly moves the mixture.
/// </summary>
public class IntercoolingTests
{
    // ----- what the compressor does --------------------------------------------

    /// <summary>
    /// 20 psi on a standard day is a pressure ratio of 2.36, and at 70% efficiency
    /// that is about 300 °F out of the compressor. Worked by hand: 70 °F is
    /// 529.67 R, the ideal rise is 529.67 × (2.36^0.2857 − 1) = 149.6 °F, and a
    /// compressor at 0.70 costs 149.6/0.70 = 213.7 — so 283.7 °F.
    /// </summary>
    [Fact]
    public void CompressorOutletIsTheIdealRiseDividedByEfficiency()
    {
        double pr = ChargeAir.PressureRatio(20);

        Assert.Equal(2.3609, pr, 3);

        double outlet = ChargeAir.CompressorOutletF(70, pr, 0.70);

        Assert.InRange(outlet, 280, 288);

        // A perfect compressor would only get to about 220.
        Assert.True(ChargeAir.CompressorOutletF(70, pr, 1.0) < outlet - 50);
    }

    /// <summary>
    /// The rise depends on pressure ratio, not on boost — which is why the second
    /// ten psi costs less heat than the first.
    /// </summary>
    [Fact]
    public void TheSecondTenPsiCostsLessHeatThanTheFirst()
    {
        double first = ChargeAir.CompressorOutletF(70, ChargeAir.PressureRatio(10), 0.72) - 70;
        double toTwenty = ChargeAir.CompressorOutletF(70, ChargeAir.PressureRatio(20), 0.72) - 70;

        double second = toTwenty - first;

        Assert.True(second < first, $"first ten cost {first:N0} °F, second {second:N0}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void AnImpossibleEfficiencyIsRefused(double efficiency) =>
        Assert.True(double.IsNaN(ChargeAir.CompressorOutletF(70, 2.0, efficiency)));

    // ----- the heat, and the core ----------------------------------------------

    /// <summary>
    /// 40 lb/min cooled by 150 °F is 40 × 0.24 × 150 = 1,440 BTU a minute, which
    /// is 86,400 an hour — about 25 kW, and a useful sanity check on how much work
    /// an intercooler is really doing.
    /// </summary>
    [Fact]
    public void TheHeatLoadIsMassFlowTimesSpecificHeatTimesTheDrop()
    {
        Assert.Equal(1440, ChargeAir.HeatLoadBtuPerMin(40, 150), 6);
        Assert.Equal(86_400, ChargeAir.HeatLoadBtuPerMin(40, 150) * 60, 6);
    }

    /// <summary>
    /// Effectiveness is the share of the available difference taken. A 70% core
    /// with 285 °F in and 90 °F ambient leaves 285 − 0.7 × 195 = 148.5 °F.
    /// </summary>
    [Fact]
    public void EffectivenessIsTheShareOfTheAvailableDropTaken()
    {
        Assert.Equal(148.5, ChargeAir.OutletF(285, 90, 0.70), 6);

        // A perfect core would reach ambient; a useless one changes nothing.
        Assert.Equal(90, ChargeAir.OutletF(285, 90, 1.0), 6);
        Assert.Equal(285, ChargeAir.OutletF(285, 90, 0), 6);
    }

    [Fact]
    public void EffectivenessCanBeReadBackFromMeasuredTemperatures() =>
        Assert.Equal(0.70, ChargeAir.EffectivenessFrom(285, 148.5, 90), 6);

    /// <summary>
    /// The point of the whole exercise: density goes as absolute temperature, so
    /// 285 °F down to 148.5 is 744.67/608.17 — about 22% more air through the same
    /// valves at the same pressure.
    /// </summary>
    [Fact]
    public void CoolingBuysDensityInProportionToAbsoluteTemperature()
    {
        double ratio = ChargeAir.DensityRatio(285, 148.5);

        Assert.Equal((285 + 459.67) / (148.5 + 459.67), ratio, 9);
        Assert.InRange(ratio, 1.20, 1.25);
    }

    [Fact]
    public void ACoresLoadingIsTheHeatSpreadOverItsSize()
    {
        var core = new IntercoolerCore(24, 12, 3);

        Assert.Equal(288, core.FrontalAreaSqIn, 6);
        Assert.Equal(864, core.VolumeCuIn, 6);
        Assert.Equal(1440.0 / 864, core.LoadingPerCuIn(1440), 6);

        // Half the core is twice the loading, which is the comparison it is for.
        var half = new IntercoolerCore(24, 12, 1.5);
        Assert.Equal(2 * core.LoadingPerCuIn(1440), half.LoadingPerCuIn(1440), 6);
    }

    /// <summary>
    /// The material argument, settled by arithmetic. A half-millimetre aluminium
    /// wall is well under one per cent of the resistance between the two air
    /// streams, so doubling its conductivity cannot buy anything worth having.
    /// </summary>
    [Fact]
    public void TheMetalIsNotWhatIsStoppingTheHeat()
    {
        CoreMaterial aluminium = CoreMaterials.All[0];
        CoreMaterial copper = CoreMaterials.All[1];

        double alShare = CoreMaterials.MetalShareOfResistance(aluminium);

        Assert.True(alShare < 0.01, $"aluminium wall is {alShare:P2} of the resistance");

        // Copper roughly halves an already negligible share.
        double gain = alShare - CoreMaterials.MetalShareOfResistance(copper);

        Assert.True(gain < 0.005, $"copper would buy {gain:P2} of the total resistance");
    }

    [Fact]
    public void EveryMaterialSaysWhatItIsActuallyForRatherThanClaimingCooling() =>
        Assert.All(CoreMaterials.All, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Note));
            Assert.True(m.ConductivityWmK > 0);
            Assert.True(m.RelativeDensity > 0);
        });

    // ----- the sprayables --------------------------------------------------------

    /// <summary>
    /// The published figures, converted once and checked here so a slip in the
    /// constant cannot pass. Water 2,260 kJ/kg, methanol 1,100, ethanol 850,
    /// at 0.4299 BTU/lb per kJ/kg.
    /// </summary>
    [Fact]
    public void TheLatentHeatsMatchThePublishedFigures()
    {
        Assert.Equal(2260 * Sprayables.BtuPerLbPerKjPerKg, Sprayables.Water.LatentBtuPerLb, 1);
        Assert.Equal(1100 * Sprayables.BtuPerLbPerKjPerKg, Sprayables.Methanol.LatentBtuPerLb, 1);

        // Water absorbs a little over twice what methanol does, which is the whole
        // reason a blend is mostly water.
        Assert.InRange(Sprayables.Water.LatentBtuPerLb / Sprayables.Methanol.LatentBtuPerLb, 2.0, 2.1);
    }

    /// <summary>
    /// The conversion people get wrong. Fifty-fifty is sold by volume, and
    /// methanol is a fifth lighter than water — so it is 44% methanol by mass, and
    /// the cooling has to be worked out from the mass fraction.
    /// </summary>
    [Fact]
    public void FiftyFiftyByVolumeIsNotFiftyFiftyByWeight()
    {
        Sprayable mix = Sprayables.FiftyFifty;

        Assert.Equal(0.442, mix.CombustibleFraction, 3);
        Assert.Equal(0.896, mix.DensityGPerCc, 3);

        // Mass-weighted, so it lands nearer water than a volume average would.
        double byMass = (0.442 * Sprayables.Methanol.LatentBtuPerLb)
                        + (0.558 * Sprayables.Water.LatentBtuPerLb);

        Assert.Equal(byMass, mix.LatentBtuPerLb, 1);

        double byVolumeWrongly = (0.5 * Sprayables.Methanol.LatentBtuPerLb)
                                 + (0.5 * Sprayables.Water.LatentBtuPerLb);

        Assert.True(mix.LatentBtuPerLb > byVolumeWrongly,
            "taking the volume fraction understates the cooling");
    }

    [Fact]
    public void TheEndsOfTheBlendRangeAreTheNeatFluids()
    {
        Assert.Equal(Sprayables.Water.LatentBtuPerLb, Sprayables.Blend("w", 0).LatentBtuPerLb, 6);
        Assert.Equal(Sprayables.Methanol.LatentBtuPerLb, Sprayables.Blend("m", 1).LatentBtuPerLb, 6);
    }

    /// <summary>
    /// Nozzles are sold in cc/min and the physics is in pounds. 1,000 cc/min of
    /// water is 2.2 lb/min; the same nozzle flowing methanol is only 1.75, because
    /// a cc of methanol weighs less.
    /// </summary>
    [Fact]
    public void ANozzleRatingIsAVolumeAndTheThermodynamicsIsAMass()
    {
        Assert.Equal(1000 / 453.59237, Sprayables.Water.LbPerMinFromCcPerMin(1000), 6);
        Assert.Equal(2.2046, Sprayables.Water.LbPerMinFromCcPerMin(1000), 3);

        Assert.True(Sprayables.Methanol.LbPerMinFromCcPerMin(1000)
                    < Sprayables.Water.LbPerMinFromCcPerMin(1000));

        // And back again, so a nozzle can be chosen from a mass flow.
        Assert.Equal(1000, Sprayables.Water.CcPerMinFromLbPerMin(
            Sprayables.Water.LbPerMinFromCcPerMin(1000)), 6);
    }

    // ----- sizing the spray -------------------------------------------------------

    /// <summary>
    /// The headline calculation, by hand. 40 lb/min of air dropped 100 °F is 960
    /// BTU a minute. Water at 70 °F entering a 250 °F charge removes 971.6 latent
    /// plus 180 sensible = 1,151.6 BTU a pound, so 0.834 lb/min — and at 75%
    /// actually evaporating in the charge, 1.11 lb/min, which is about 504 cc/min.
    /// </summary>
    [Fact]
    public void SizingASprayForAKnownDropAgreesWithTheArithmeticByHand()
    {
        double lb = ChemicalIntercooling.FlowLbPerMin(
            airLbPerMin: 40, dropF: 100, Sprayables.Water, chargeF: 250, liquidF: 70,
            evaporated: 0.75);

        double perPound = 971.6 + (1.0 * (250 - 70));

        Assert.Equal(960 / perPound / 0.75, lb, 6);
        Assert.InRange(lb, 1.10, 1.12);

        double cc = ChemicalIntercooling.FlowCcPerMin(
            40, 100, Sprayables.Water, 250, 70, 0.75);

        Assert.InRange(cc, 495, 515);
    }

    /// <summary>
    /// Water needs the least of it, because it absorbs the most per pound. E85
    /// needs roughly three times as much for the same cooling, which is why it is
    /// a fuel with a cooling side effect rather than a coolant.
    /// </summary>
    [Fact]
    public void WaterNeedsTheLeastFlowAndE85TheMost()
    {
        double Flow(Sprayable f) =>
            ChemicalIntercooling.FlowLbPerMin(40, 100, f, 250);

        Assert.True(Flow(Sprayables.Water) < Flow(Sprayables.FiftyFifty));
        Assert.True(Flow(Sprayables.FiftyFifty) < Flow(Sprayables.Methanol));
        Assert.True(Flow(Sprayables.Methanol) < Flow(Sprayables.E85));

        Assert.InRange(Flow(Sprayables.E85) / Flow(Sprayables.Water), 2.0, 3.5);
    }

    /// <summary>The question the other way round: what a nozzle already fitted buys.</summary>
    [Fact]
    public void TheDropAGivenNozzleBuysIsTheInverseOfSizingOne()
    {
        double cc = ChemicalIntercooling.FlowCcPerMin(40, 100, Sprayables.FiftyFifty, 250);
        double drop = ChemicalIntercooling.DropFFor(cc, 40, Sprayables.FiftyFifty, 250);

        Assert.Equal(100, drop, 6);
    }

    /// <summary>
    /// Not all of it evaporates where it is wanted, and assuming it does undersizes
    /// the nozzle. Half evaporating needs twice the flow.
    /// </summary>
    [Fact]
    public void WhatDoesNotEvaporateInTheChargeStillHasToBeSprayed()
    {
        double all = ChemicalIntercooling.FlowLbPerMin(40, 100, Sprayables.Water, 250, evaporated: 1.0);
        double half = ChemicalIntercooling.FlowLbPerMin(40, 100, Sprayables.Water, 250, evaporated: 0.5);

        Assert.Equal(2 * all, half, 6);
    }

    /// <summary>
    /// The sensible heating is not nothing: cold water into a hot charge is worth
    /// another sixth of the latent heat, and leaving it out oversizes the nozzle.
    /// </summary>
    [Fact]
    public void TheLiquidsOwnWarmingCountsToo()
    {
        double cold = Sprayables.Water.CoolingBtuPerLb(40, 300);
        double warm = Sprayables.Water.CoolingBtuPerLb(200, 300);

        Assert.True(cold > warm);
        Assert.Equal(971.6 + 260, cold, 6);

        // Liquid hotter than the charge contributes no sensible cooling rather
        // than a negative amount.
        Assert.Equal(971.6, Sprayables.Water.CoolingBtuPerLb(300, 250), 6);
    }

    // ----- the half a cooling calculation forgets ---------------------------------

    /// <summary>
    /// Methanol is fuel. A pound of it carries 8,640 BTU against petrol's 18,400,
    /// so it replaces a little under half a pound — and an engine given both
    /// without the petrol being taken out runs rich, then leans out hard the moment
    /// the spray stops.
    /// </summary>
    [Fact]
    public void TheCombustiblePartDisplacesPetrolAndIsReported()
    {
        double displaced = ChemicalIntercooling.PetrolDisplacedLbPerMin(2.0, Sprayables.Methanol);

        Assert.Equal(2.0 * 8_640 / 18_400, displaced, 6);
        Assert.InRange(displaced, 0.93, 0.95);
    }

    [Fact]
    public void WaterDisplacesNoFuelBecauseItDoesNotBurn() =>
        Assert.Equal(0, ChemicalIntercooling.PetrolDisplacedLbPerMin(2.0, Sprayables.Water));

    [Fact]
    public void AFiftyFiftyBlendDisplacesLessThanNeatMethanol() =>
        Assert.True(
            ChemicalIntercooling.PetrolDisplacedLbPerMin(2.0, Sprayables.FiftyFifty)
            < ChemicalIntercooling.PetrolDisplacedLbPerMin(2.0, Sprayables.Methanol));

    [Fact]
    public void SprayIsAlsoExpressedAsAShareOfFuelBecauseControllersAre() =>
        Assert.Equal(30, ChemicalIntercooling.PercentOfFuel(1.5, 5.0), 6);

    // ----- tank and duration -------------------------------------------------------

    /// <summary>
    /// 500 cc/min is 30 litres an hour — about 7.9 US gallons, so a five gallon
    /// tank is barely half an hour at full flow. Worth knowing before a track day.
    /// </summary>
    [Fact]
    public void TankLifeIsShorterThanPeopleExpect()
    {
        Assert.Equal(30, ChemicalIntercooling.LitresPerHour(500), 6);
        Assert.InRange(ChemicalIntercooling.GallonsPerHour(500), 7.8, 8.0);

        double minutes = ChemicalIntercooling.TankMinutes(5, 500);

        Assert.InRange(minutes, 36, 39);
    }

    // ----- nothing typed yet --------------------------------------------------------

    [Fact]
    public void NoAirflowMeansNoAnswerRatherThanInfinity()
    {
        Assert.True(double.IsNaN(ChargeAir.HeatLoadBtuPerMin(0, 100)));
        Assert.True(double.IsNaN(ChemicalIntercooling.FlowLbPerMin(0, 100, Sprayables.Water, 250)));
        Assert.True(double.IsNaN(ChemicalIntercooling.DropFFor(500, 0, Sprayables.Water, 250)));
        Assert.True(double.IsNaN(new IntercoolerCore(0, 0, 0).LoadingPerCuIn(1440)));
    }

    [Fact]
    public void EverySprayableSaysWhatItIsForAndWhatItCosts() =>
        Assert.All(Sprayables.All, s =>
        {
            Assert.True(s.LatentBtuPerLb > 0, $"{s.Name} has no latent heat");
            Assert.True(s.DensityGPerCc > 0, $"{s.Name} has no density");
            Assert.False(string.IsNullOrWhiteSpace(s.Note), $"{s.Name} has no note");
        });
}
