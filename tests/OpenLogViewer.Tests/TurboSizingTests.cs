using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Sizing a turbocharger to a power target.
///
/// Checked against the turbocharger maker's own published worked example rather
/// than against the formulas restated, because the whole value of this is
/// agreeing with the tool everyone already uses. A sizing that is plausibly
/// wrong sends somebody to buy the wrong turbocharger, and they find out months
/// later on a dyno.
/// </summary>
public class TurboSizingTests
{
    // Garrett's worked example, in their own numbers: 650 crank horsepower on a
    // 5.7 litre at 6,000 rpm, 80 per cent volumetric efficiency, a 130 °F
    // manifold, 11.5:1 and a BSFC of 0.46, with one psi of inlet depression and
    // two psi lost through the intercooler.
    private const double Hp = 650;
    private const double Afr = 11.5;
    private const double Bsfc = 0.46;
    private const double Litres = 5.7;
    private const double Rpm = 6000;
    private const double Ve = 80;
    private static readonly double ChargeC = (130 - 32) * 5.0 / 9;

    [Fact]
    public void TheAirflowIsTheMakersOwnFigure()
    {
        // 650 × 11.5 × 0.46 / 60 = 57.3 lb/min, which is what they print.
        Assert.Equal(57.3, TurboSizing.AirForHorsepower(Hp, Afr, Bsfc), 1);
    }

    [Fact]
    public void TheAirflowAgreesWithThePowerCalculationTurnedAround()
    {
        // The same statement as the airflow tab's, from the other end. If these
        // two ever disagree, one of the tabs is lying to somebody.
        double air = TurboSizing.AirForHorsepower(Hp, Afr, Bsfc);

        double back = TuningMath.HorsepowerFromAir(
            air, Fuel.Petrol, Afr / TuningMath.Stoichiometric(Fuel.Petrol), Bsfc);

        Assert.Equal(Hp, back, 6);
    }

    [Fact]
    public void TheManifoldPressureIsTheMakersOwnFigureToWithinTheirOwnRounding()
    {
        // They publish 26.025 psia, and this gives 25.91 — half a per cent
        // apart, which took some looking at before it was left alone.
        //
        // Their printed answer is off their own printed formula: putting their
        // inputs through their equation with their gas constant gives 25.89, not
        // 26.025. So the gap is rounding inside their worked example rather than
        // a disagreement about the physics, and half a per cent of manifold
        // pressure is far inside what the volumetric efficiency going into it is
        // known to.
        double air = TurboSizing.AirForHorsepower(Hp, Afr, Bsfc);
        double kpa = TurboSizing.ManifoldKpaFor(air, Litres, Rpm, Ve, ChargeC);

        Assert.InRange(kpa / TuningMath.KpaPerPsi / 26.025, 0.99, 1.01);
        Assert.InRange((kpa - TuningMath.AtmosphericKpa) / TuningMath.KpaPerPsi, 11.0, 11.5);
    }

    [Fact]
    public void TheManifoldPressureAlsoMatchesTheMakersImperialFormula()
    {
        // MAPreq = Wa × R × Tm / (VE × N/2 × Vd), R = 639.6, Tm in Rankine, Vd in
        // cubic inches. A second, independent route to the same number.
        double air = TurboSizing.AirForHorsepower(Hp, Afr, Bsfc);

        double cubicInches = Litres * TuningMath.CubicInchesPerLitre;
        double rankine = (ChargeC * 9 / 5) + 32 + 459.67;

        double imperial = air * 639.6 * rankine / (Ve / 100 * (Rpm / 2) * cubicInches);

        double metric = TurboSizing.ManifoldKpaFor(air, Litres, Rpm, Ve, ChargeC) / TuningMath.KpaPerPsi;

        // Within a tenth of a per cent — 25.891 against 25.912. The gap is the
        // maker's rounded gas constant against the exact one, and this is the
        // check that matters: two unit systems and two constants arriving at the
        // same number is worth more than either matching a printed figure.
        Assert.InRange(metric / imperial, 0.999, 1.001);
    }

    [Fact]
    public void ThePressureRatioIsTheMakersOwnFigure()
    {
        // Twelve psi of boost against a 14.7 atmosphere with one psi of
        // depression is 1.95; two psi through the intercooler makes it 2.0.
        TuningMath.Compressor bare = TuningMath.CompressorPressures(
            12 * TuningMath.KpaPerPsi, 14.7 * TuningMath.KpaPerPsi,
            inletLossKpa: TuningMath.KpaPerPsi);

        Assert.Equal(1.95, bare.Ratio, 2);

        // Their second example is not the same twelve psi — it uses the manifold
        // pressure the sizing produced, 26.025 psia, and adds two psi of
        // intercooler to it: (26.025 + 2) / 13.7, which they print as 2.0.
        TuningMath.Compressor withLoss = TuningMath.CompressorPressures(
            (26.025 - 14.7) * TuningMath.KpaPerPsi, 14.7 * TuningMath.KpaPerPsi,
            inletLossKpa: TuningMath.KpaPerPsi,
            chargeLossKpa: 2 * TuningMath.KpaPerPsi);

        Assert.Equal(2.0, withLoss.Ratio, 1);
    }

    [Fact]
    public void TheWholeExampleComesOutWhereTheMakerSaysItDoes()
    {
        TurboRequirement need = TurboSizing.Required(
            Hp, Afr, Bsfc, Litres, Rpm, Ve, ChargeC,
            barometricKpa: 14.7 * TuningMath.KpaPerPsi,
            inletLossKpa: TuningMath.KpaPerPsi,
            chargeLossKpa: 2 * TuningMath.KpaPerPsi);

        Assert.Equal(57.3, need.AirLbPerMinute, 1);
        Assert.InRange(need.BoostKpa / TuningMath.KpaPerPsi, 11.0, 11.5);
        Assert.InRange(need.PressureRatio, 1.98, 2.08);
    }

    // ----- how the requirement moves ------------------------------------------

    [Fact]
    public void MorePowerNeedsMoreAirAndMoreBoost()
    {
        TurboRequirement small = TurboSizing.Required(400, Afr, Bsfc, Litres, Rpm, Ve, ChargeC);
        TurboRequirement big = TurboSizing.Required(800, Afr, Bsfc, Litres, Rpm, Ve, ChargeC);

        Assert.Equal(2, big.AirLbPerMinute / small.AirLbPerMinute, 6);
        Assert.True(big.ManifoldKpa > small.ManifoldKpa);
        Assert.True(big.PressureRatio > small.PressureRatio);
    }

    [Fact]
    public void ABiggerEngineNeedsLessBoostForTheSamePower()
    {
        // The trade everyone knows and this puts a number on.
        TurboRequirement small = TurboSizing.Required(650, Afr, Bsfc, 2.0, Rpm, Ve, ChargeC);
        TurboRequirement big = TurboSizing.Required(650, Afr, Bsfc, 5.7, Rpm, Ve, ChargeC);

        Assert.Equal(small.AirLbPerMinute, big.AirLbPerMinute, 6);
        Assert.True(big.BoostKpa < small.BoostKpa);
    }

    [Fact]
    public void AHotterChargeNeedsMorePressureForTheSameAir()
    {
        TurboRequirement cool = TurboSizing.Required(Hp, Afr, Bsfc, Litres, Rpm, Ve, 30);
        TurboRequirement hot = TurboSizing.Required(Hp, Afr, Bsfc, Litres, Rpm, Ve, 70);

        Assert.True(hot.ManifoldKpa > cool.ManifoldKpa);

        // Density goes with absolute temperature, so forty degrees on a
        // three-hundred kelvin charge is worth about an eighth.
        Assert.InRange(hot.ManifoldKpa / cool.ManifoldKpa, 1.10, 1.16);
    }

    [Fact]
    public void AltitudeAsksMoreOfTheCompressorForTheSameEngine()
    {
        double high = TuningMath.BarometricKpa(5_000 * TuningMath.MetresPerFoot);

        TurboRequirement sea = TurboSizing.Required(
            Hp, Afr, Bsfc, Litres, Rpm, Ve, ChargeC, TuningMath.AtmosphericKpa);

        TurboRequirement mountain = TurboSizing.Required(
            Hp, Afr, Bsfc, Litres, Rpm, Ve, ChargeC, high);

        // The same air at the same manifold pressure, but drawn from thinner air,
        // so the compressor works harder for it.
        Assert.Equal(sea.ManifoldKpa, mountain.ManifoldKpa, 6);
        Assert.True(mountain.PressureRatio > sea.PressureRatio);
        Assert.True(mountain.BoostKpa > sea.BoostKpa);
    }

    [Fact]
    public void NonsenseAsksForNothing()
    {
        Assert.True(double.IsNaN(TurboSizing.AirForHorsepower(0, Afr, Bsfc)));
        Assert.True(double.IsNaN(TurboSizing.AirForHorsepower(Hp, 0, Bsfc)));
        Assert.True(double.IsNaN(TurboSizing.ManifoldKpaFor(50, 0, Rpm, Ve, 40)));
        Assert.True(double.IsNaN(TurboSizing.ManifoldKpaFor(50, Litres, 0, Ve, 40)));
        Assert.True(double.IsNaN(TurboSizing.ManifoldKpaFor(50, Litres, Rpm, 0, 40)));
    }

    // ----- the catalogue -------------------------------------------------------

    [Fact]
    public void EveryTurboInTheCatalogueIsDescribedCompletely()
    {
        Assert.NotEmpty(TurboSizing.Catalogue);

        foreach (Turbo turbo in TurboSizing.Catalogue)
        {
            Assert.False(string.IsNullOrWhiteSpace(turbo.Model));
            Assert.InRange(turbo.InducerMm, 30, 120);
            Assert.InRange(turbo.RatedHorsepower, 100, 3_000);

            // An exducer is always larger than the inducer it feeds.
            Assert.True(turbo.ExducerMm > turbo.InducerMm, $"{turbo.Model} has a wheel that shrinks");
        }

        Assert.Equal(
            TurboSizing.Catalogue.Count,
            TurboSizing.Catalogue.Select(t => t.Model).Distinct().Count());
    }

    [Fact]
    public void ABiggerWheelIsRatedForMorePower()
    {
        // Not strictly, since two frames share a wheel and differ in the turbine
        // — but across the range, a larger inducer never means a lower rating.
        Turbo[] byWheel = [.. TurboSizing.Catalogue.OrderBy(t => t.InducerMm)];

        for (int i = 1; i < byWheel.Length; i++)
            Assert.True(byWheel[i].RatedHorsepower >= byWheel[i - 1].RatedHorsepower,
                $"{byWheel[i].Model} has a bigger wheel than {byWheel[i - 1].Model} and a lower rating");
    }

    [Fact]
    public void FlowDerivedFromTheRatingLandsWhereReportedFlowFiguresDo()
    {
        // The check that deriving flow from the horsepower rating was reasonable
        // rather than convenient. These are figures reported independently for
        // the same turbochargers, and the derivation has to land near them.
        (string Model, double Reported)[] published =
        [
            ("G30-770", 69),
            ("G30-900", 81),
            ("G35-900", 82.5),
        ];

        foreach ((string model, double reported) in published)
        {
            Turbo turbo = TurboSizing.Catalogue.First(t => t.Model == model);

            Assert.InRange(turbo.MaxFlowLbPerMinute / reported, 0.94, 1.06);
        }
    }

    // ----- what to buy ---------------------------------------------------------

    [Fact]
    public void TheMakersOwnExampleIsOfferedASensibleTurbo()
    {
        // 57.3 lb/min. The smallest G that does it with room to spare is a
        // G30-770, which is the kind of turbocharger a 650 horsepower 5.7 gets.
        var suggestions = TurboSizing.Suggest(57.3);

        Assert.NotEmpty(suggestions);
        Assert.Equal("G30-770", suggestions[0].Turbo.Model);
        Assert.Equal(1, suggestions[0].Count);
    }

    [Fact]
    public void TheSmallestThatCanDoItComesFirst()
    {
        var suggestions = TurboSizing.Suggest(50);

        for (int i = 1; i < suggestions.Count; i++)
            Assert.True(
                suggestions[i].Turbo.MaxFlowLbPerMinute >= suggestions[i - 1].Turbo.MaxFlowLbPerMinute,
                "the list should climb, so the first is the one that spools soonest");
    }

    [Fact]
    public void NothingIsOfferedThatCannotPassTheAir()
    {
        foreach (double air in (double[])[20, 45, 60, 90, 120])
            foreach (TurboMatch match in TurboSizing.Suggest(air))
                Assert.True(
                    match.Turbo.MaxFlowLbPerMinute * match.Count >= air,
                    $"{match.Label} was offered for {air} lb/min and cannot pass it");
    }

    [Fact]
    public void HeadroomIsInsistedOnAndReported()
    {
        var suggestions = TurboSizing.Suggest(60, headroom: 0.20);

        Assert.All(suggestions, m => Assert.True(m.Headroom >= 0.20));

        // And more headroom means a bigger turbo is the smallest that qualifies.
        double relaxed = TurboSizing.Suggest(60, headroom: 0)[0].Turbo.MaxFlowLbPerMinute;
        double strict = TurboSizing.Suggest(60, headroom: 0.40)[0].Turbo.MaxFlowLbPerMinute;

        Assert.True(strict >= relaxed);
    }

    [Fact]
    public void APairIsOfferedWhereOneCannotDoIt()
    {
        // Past the largest single frame in the list, two of something is the
        // real answer rather than nothing at all.
        double beyond = TurboSizing.Catalogue.Max(t => t.MaxFlowLbPerMinute) * 1.5;

        var suggestions = TurboSizing.Suggest(beyond);

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, m => Assert.Equal(2, m.Count));
        Assert.All(suggestions, m => Assert.StartsWith("2 × ", m.Label));
    }

    [Fact]
    public void NothingAtAllIsOfferedForNothingAtAll()
    {
        Assert.Empty(TurboSizing.Suggest(0));
        Assert.Empty(TurboSizing.Suggest(double.NaN));
        Assert.Empty(TurboSizing.Suggest(-10));
    }

    // ----- changing fuel --------------------------------------------------------

    [Fact]
    public void MovingTheMixtureAndTheConsumptionTogetherBarelyChangesTheAir()
    {
        // The true and surprising answer, and the reason the fuel is a selector
        // rather than two boxes to edit by hand: a pound of air carries about as
        // much energy whichever fuel arrives with it, so the same power needs
        // very nearly the same air on any of them.
        double lambda = 11.5 / TuningMath.Stoichiometric(Fuel.Petrol);
        double petrol = TurboSizing.AirForHorsepower(Hp, 11.5, TurboSizing.RatedBsfc);

        foreach (Fuel fuel in (Fuel[])[Fuel.E30, Fuel.E85, Fuel.Ethanol])
        {
            double air = TurboSizing.AirForHorsepower(
                Hp,
                TuningMath.AfrFromLambda(lambda, fuel),
                TuningMath.SuggestedBsfc(fuel, TurboSizing.RatedBsfc));

            Assert.InRange(air / petrol, 0.98, 1.01);
        }

        // Methanol is the one that moves, and it moves the way the airflow tab
        // says it should: it carries the most energy per unit of air, so it
        // takes the least air for a given power.
        double methanol = TurboSizing.AirForHorsepower(
            Hp,
            TuningMath.AfrFromLambda(lambda, Fuel.Methanol),
            TuningMath.SuggestedBsfc(Fuel.Methanol, TurboSizing.RatedBsfc));

        Assert.InRange(methanol / petrol, 0.94, 0.97);
    }

    [Fact]
    public void MovingOnlyTheConsumptionAsksForAbsurdlyMoreAir()
    {
        // What the selector exists to prevent. Somebody who knows an alcohol
        // wants more fuel, and raises the consumption without richening the
        // ratio, asks for half again the air on E85 and more than twice it on
        // methanol — and buys a turbocharger two sizes too large.
        double petrol = TurboSizing.AirForHorsepower(Hp, 11.5, TurboSizing.RatedBsfc);

        double halfChanged = TurboSizing.AirForHorsepower(
            Hp, 11.5, TuningMath.SuggestedBsfc(Fuel.E85, TurboSizing.RatedBsfc));

        Assert.InRange(halfChanged / petrol, 1.4, 1.6);

        double worse = TurboSizing.AirForHorsepower(
            Hp, 11.5, TuningMath.SuggestedBsfc(Fuel.Methanol, TurboSizing.RatedBsfc));

        Assert.True(worse / petrol > 2);
    }

    [Fact]
    public void TheAirNeededAgreesWithThePowerTheAirflowTabWouldReport()
    {
        // The two tabs are the same statement from opposite ends, on every fuel.
        // If they ever disagree, one of them is sending somebody shopping with
        // the wrong number.
        //
        // Note which BSFC each side wants, because getting it wrong here is what
        // this check first caught. Sizing takes the fuel's own figure, since
        // that is what a person types into the box. HorsepowerFromAir takes the
        // petrol-basis one and scales it itself, so handing it an already-scaled
        // figure scales it twice — which showed up as E10 coming back four per
        // cent light.
        double lambda = 0.78;

        foreach (Fuel fuel in Enum.GetValues<Fuel>())
        {
            if (fuel == Fuel.Diesel) continue;

            double afr = TuningMath.AfrFromLambda(lambda, fuel);
            double ownBsfc = TuningMath.SuggestedBsfc(fuel, TurboSizing.RatedBsfc);

            double air = TurboSizing.AirForHorsepower(Hp, afr, ownBsfc);
            double back = TuningMath.HorsepowerFromAir(air, fuel, lambda, TurboSizing.RatedBsfc);

            Assert.Equal(Hp, back, 6);
        }
    }
}
