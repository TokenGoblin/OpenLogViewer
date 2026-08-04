using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// A whole build worked through at once.
///
/// The sums are each tested where they live, so what is checked here is what
/// only appears when they are put together: that every part of the list rests on
/// the same mixture and the same fuel consumption, that the margins are the only
/// thing separating a part from the bare requirement, and that changing one
/// input moves everything it should and nothing it should not.
///
/// A recipe whose turbocharger was sized at one set of assumptions and whose
/// injectors were sized at another produces a car that is short of one or the
/// other, and neither calculation on its own would say so.
/// </summary>
public class EngineRecipeTests
{
    /// <summary>A 2.0 litre four wanting 500 hp on petrol — the ordinary case.</summary>
    private static RecipeSpec Ordinary => new()
    {
        Litres = 2.0,
        Cylinders = 4,
        TargetHorsepower = 500,
        PeakTorqueRpm = 3500,
        PeakPowerRpm = 7000,
        Fuel = Fuel.Petrol,
        Lambda = 0.80,
        VolumetricEfficiency = 95,
        ChargeCelsius = 45,
    };

    // ----- everything rests on the same assumptions ---------------------------

    [Fact]
    public void EveryPartOfTheListIsSizedOnTheSameFuelConsumption()
    {
        // The whole point of doing this in one place. If the turbo were sized at
        // one BSFC and the injectors at another, the car would be short of one
        // of them and nothing would say which.
        Recipe recipe = EngineRecipe.Build(Ordinary);

        double bsfc = recipe.Bsfc;

        // Air, from the maker's own equation at this recipe's mixture and BSFC.
        Assert.Equal(
            TurboSizing.AirForHorsepower(500, recipe.Afr, bsfc),
            recipe.AirAtPeakPower, 6);

        // Injectors, from the same BSFC.
        Assert.Equal(
            TuningMath.InjectorPoundsPerHour(500, 4, bsfc, 85),
            recipe.InjectorLbHrEach, 6);

        // And the pump.
        Assert.Equal(
            TuningMath.FuelLitresPerHour(500, bsfc, Fuel.Petrol),
            recipe.FuelLitresPerHour, 6);
    }

    [Fact]
    public void TheMixtureIsTheOneAskedForOnTheFuelChosen()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary);

        Assert.Equal(0.80 * TuningMath.Stoichiometric(Fuel.Petrol), recipe.Afr, 6);
        Assert.Equal(TuningMath.FullThrottleBsfc, recipe.Bsfc, 6);
    }

    [Fact]
    public void AnOrdinaryBuildComesOutWithOrdinaryParts()
    {
        // 500 hp from a 2.0 is a real, common target. The parts it asks for
        // should be the parts such a car actually runs — this is the check that
        // the whole chain lands somewhere a tuner would recognise rather than
        // merely being arithmetically consistent.
        Recipe recipe = EngineRecipe.Build(Ordinary);

        Assert.InRange(recipe.AirAtPeakPower, 45, 55);       // lb/min
        Assert.InRange(recipe.InjectorCcEach, 700, 800);     // cc/min each
        Assert.InRange(recipe.PumpLitresPerHour, 170, 200);  // L/h

        // Thirty psi, which surprised the expectation written here first and is
        // right: 250 hp per litre is a great deal to ask of two litres, and the
        // cars that do it run about this. The boost is the price of the
        // displacement not being there, which is the trade the tool is for.
        Assert.InRange(recipe.BoostKpa / TuningMath.KpaPerPsi, 26, 34);
        Assert.InRange(recipe.PressureRatio, 3.0, 3.7);

        Assert.NotEmpty(recipe.Turbos);
        Assert.NotEmpty(recipe.Pumps);
    }

    [Fact]
    public void ThePartsSuggestedCanActuallyDoTheJob()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary);

        Assert.All(recipe.Turbos, t =>
            Assert.True(t.Turbo.MaxFlowLbPerMinute * t.Count >= recipe.AirAtPeakPower));

        Assert.All(recipe.Pumps, p =>
            Assert.True(p.DeliveredLitresPerHour >= recipe.PumpLitresPerHour));
    }

    // ----- the two engine speeds ----------------------------------------------

    [Fact]
    public void TheTorquePeakSetsTheOtherEndOfTheFlowRange()
    {
        // Half the engine speed at the same boost is half the air. That lower
        // figure is what says whether a compressor is too large, and it is
        // invisible in a flow taken at peak power alone.
        Recipe recipe = EngineRecipe.Build(Ordinary with { PeakTorqueRpm = 3500, PeakPowerRpm = 7000 });

        Assert.Equal(recipe.AirAtPeakPower / 2, recipe.AirAtPeakTorque, 6);
    }

    [Fact]
    public void AVeryLowTorquePeakIsFlaggedAsTooMuchTurboToSpool()
    {
        // The failure this exists to catch: a turbocharger with plenty of flow
        // left at the power peak, sitting off the left of its map where the
        // engine actually spends its time.
        Recipe recipe = EngineRecipe.Build(Ordinary with { PeakTorqueRpm = 1200 });

        Assert.Contains(recipe.Warnings, w =>
            w.Severity == "watch" && w.Text.Contains("left of its map"));
    }

    [Fact]
    public void ASensibleTorquePeakIsNotFlagged()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary);

        Assert.DoesNotContain(recipe.Warnings, w => w.Text.Contains("left of its map"));
    }

    [Fact]
    public void AnImpossibleOrderOfPeaksIsPointedOut()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary with { PeakTorqueRpm = 7000, PeakPowerRpm = 6000 });

        Assert.Contains(recipe.Warnings, w => w.Text.Contains("no engine does"));
    }

    // ----- changing the fuel changes the parts, coherently --------------------

    [Fact]
    public void E85NeedsTheSameAirButMoreOfEverythingFuel()
    {
        // The result the airflow and turbo tabs already establish, arriving here
        // as a parts list: the same air, and about half again the fuel.
        Recipe petrol = EngineRecipe.Build(Ordinary);
        Recipe e85 = EngineRecipe.Build(Ordinary with { Fuel = Fuel.E85 });

        Assert.InRange(e85.AirAtPeakPower / petrol.AirAtPeakPower, 0.98, 1.01);

        Assert.InRange(e85.InjectorCcEach / petrol.InjectorCcEach, 1.35, 1.55);
        Assert.InRange(e85.PumpLitresPerHour / petrol.PumpLitresPerHour, 1.35, 1.55);
    }

    [Fact]
    public void AnAlcoholOnlyGetsPumpsRatedForIt()
    {
        Recipe e85 = EngineRecipe.Build(Ordinary with { Fuel = Fuel.E85 });

        Assert.NotEmpty(e85.Pumps);
        Assert.All(e85.Pumps, p => Assert.True(p.Pump.AlcoholSafe));
    }

    [Fact]
    public void MethanolAsksForFarMoreFuelAndBarelyMoreAir()
    {
        Recipe petrol = EngineRecipe.Build(Ordinary);
        Recipe methanol = EngineRecipe.Build(Ordinary with { Fuel = Fuel.Methanol });

        Assert.InRange(methanol.AirAtPeakPower / petrol.AirAtPeakPower, 0.93, 1.0);
        Assert.True(methanol.InjectorCcEach > petrol.InjectorCcEach * 1.9);
    }

    // ----- how the build moves -------------------------------------------------

    [Fact]
    public void MorePowerMovesEveryPartOfTheList()
    {
        Recipe modest = EngineRecipe.Build(Ordinary with { TargetHorsepower = 300 });
        Recipe wild = EngineRecipe.Build(Ordinary with { TargetHorsepower = 700 });

        Assert.True(wild.AirAtPeakPower > modest.AirAtPeakPower);
        Assert.True(wild.BoostKpa > modest.BoostKpa);
        Assert.True(wild.InjectorCcEach > modest.InjectorCcEach);
        Assert.True(wild.PumpLitresPerHour > modest.PumpLitresPerHour);
    }

    [Fact]
    public void ABiggerEngineWantsLessBoostAndTheSameFuel()
    {
        // The trade the tool is for: displacement buys the same power on less
        // pressure, and the fuel system does not care where the air came from.
        Recipe small = EngineRecipe.Build(Ordinary with { Litres = 2.0 });
        Recipe large = EngineRecipe.Build(Ordinary with { Litres = 4.0 });

        Assert.True(large.BoostKpa < small.BoostKpa);
        Assert.Equal(small.AirAtPeakPower, large.AirAtPeakPower, 6);
        Assert.Equal(small.InjectorCcEach, large.InjectorCcEach, 6);
    }

    [Fact]
    public void ATargetAnEngineCanBreatheNaturallySaysSo()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary with { Litres = 8.0, TargetHorsepower = 300 });

        Assert.True(recipe.BoostKpa <= 0);
        Assert.Contains(recipe.Warnings, w => w.Text.Contains("no boost at all"));
    }

    [Fact]
    public void TheRailRisesWithTheBoostSoThePumpIsJudgedAgainstIt()
    {
        // A manifold-referenced regulator holds the difference across the
        // injector steady, which is what makes the injector sizing hold under
        // boost — and means the pump sees the base pressure plus all of it.
        Recipe recipe = EngineRecipe.Build(Ordinary);

        Assert.True(recipe.RailUnderBoostPsi > 43.5);
        Assert.Equal(
            43.5 + (recipe.BoostKpa / TuningMath.KpaPerPsi),
            recipe.RailUnderBoostPsi, 6);
    }

    // ----- what it says about the engine itself -------------------------------

    [Fact]
    public void ARidiculousEngineSpeedIsStopped()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary with { PeakPowerRpm = 11_000 });

        Assert.Contains(recipe.Warnings, w => w.Severity == "stop" && w.Text.Contains("piston speed"));
    }

    [Fact]
    public void AnOrdinaryEngineSpeedIsNotStopped()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary with { PeakPowerRpm = 6500 });

        Assert.DoesNotContain(recipe.Warnings, w => w.Text.Contains("piston speed"));
    }

    [Fact]
    public void AbsurdBoostIsStoppedAndOrdinaryBoostIsNot()
    {
        Assert.Contains(
            EngineRecipe.Build(Ordinary with { TargetHorsepower = 1_400 }).Warnings,
            w => w.Severity == "stop" && w.Text.Contains("boost"));

        Assert.DoesNotContain(
            EngineRecipe.Build(Ordinary with { TargetHorsepower = 350 }).Warnings,
            w => w.Severity == "stop");
    }

    [Fact]
    public void SpecificOutputIsReportedAndFlaggedWhenItIsHeroic()
    {
        Recipe ordinary = EngineRecipe.Build(Ordinary);

        Assert.Equal(250, ordinary.SpecificOutput, 6);

        Recipe heroic = EngineRecipe.Build(Ordinary with { TargetHorsepower = 800 });

        Assert.Equal(400, heroic.SpecificOutput, 6);
        Assert.Contains(heroic.Warnings, w => w.Text.Contains("per litre"));
    }

    [Fact]
    public void PumpPetrolAtHighBoostIsMentionedAndE85IsNot()
    {
        Recipe petrol = EngineRecipe.Build(Ordinary with { TargetHorsepower = 650 });

        Assert.Contains(petrol.Warnings, w => w.Text.Contains("E85"));

        Recipe e85 = EngineRecipe.Build(Ordinary with { TargetHorsepower = 650, Fuel = Fuel.E85 });

        Assert.DoesNotContain(e85.Warnings, w => w.Text.Contains("E85 buys knock margin"));
    }

    [Fact]
    public void EveryWarningSaysHowMuchItMattersAndWhy()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary with { TargetHorsepower = 1_200, PeakPowerRpm = 11_000 });

        Assert.NotEmpty(recipe.Warnings);

        foreach (RecipeWarning warning in recipe.Warnings)
        {
            Assert.Contains(warning.Severity, (string[])["note", "watch", "stop"]);
            Assert.True(warning.Text.Length > 40, "a warning nobody can act on is noise");
        }
    }

    [Fact]
    public void NonsenseProducesNothingRatherThanAParTsList()
    {
        Recipe recipe = EngineRecipe.Build(Ordinary with { TargetHorsepower = 0 });

        Assert.True(double.IsNaN(recipe.AirAtPeakPower));
        Assert.Empty(recipe.Turbos);
    }

    // ----- the margins ---------------------------------------------------------

    [Fact]
    public void BothTheInjectorsAndThePumpAreSizedWithMarginAndBothAreSettable()
    {
        // The two are bought in different units — a duty limit for injectors,
        // headroom for a pump — so it is easy for one to end up with margin and
        // the other without, and for nothing to say so. Both must move.
        RecipeSpec tight = Ordinary with { InjectorDutyLimit = 100, PumpHeadroomPercent = 0 };

        Recipe bare = EngineRecipe.Build(tight);
        Recipe spared = EngineRecipe.Build(tight with { InjectorDutyLimit = 80, PumpHeadroomPercent = 25 });

        // A quarter more pump, and a quarter more injector — 100/80.
        Assert.Equal(1.25, spared.PumpLitresPerHour / bare.PumpLitresPerHour, 6);
        Assert.Equal(1.25, spared.InjectorCcEach / bare.InjectorCcEach, 6);
    }

    [Fact]
    public void WithNoMarginAtAllThePartsAreExactlyWhatIsBurned()
    {
        // The check that the margins are margins rather than baked-in fudge: turn
        // both off and the parts come out at the bare requirement.
        Recipe recipe = EngineRecipe.Build(
            Ordinary with { InjectorDutyLimit = 100, PumpHeadroomPercent = 0 });

        Assert.Equal(recipe.FuelLitresPerHour, recipe.PumpLitresPerHour, 6);

        // Four injectors at full duty carry the whole fuel flow between them.
        double totalLbHr = recipe.InjectorLbHrEach * 4;

        Assert.Equal(500 * recipe.Bsfc, totalLbHr, 6);
    }

    [Fact]
    public void TheTwoMarginsAreIndependentOfEachOther()
    {
        // Changing one must not quietly move the other, which is what sharing a
        // single figure between them would do.
        Recipe baseline = EngineRecipe.Build(Ordinary);
        Recipe pumpOnly = EngineRecipe.Build(Ordinary with { PumpHeadroomPercent = 40 });
        Recipe injectorOnly = EngineRecipe.Build(Ordinary with { InjectorDutyLimit = 70 });

        Assert.Equal(baseline.InjectorCcEach, pumpOnly.InjectorCcEach, 6);
        Assert.True(pumpOnly.PumpLitresPerHour > baseline.PumpLitresPerHour);

        Assert.Equal(baseline.PumpLitresPerHour, injectorOnly.PumpLitresPerHour, 6);
        Assert.True(injectorOnly.InjectorCcEach > baseline.InjectorCcEach);
    }

    [Fact]
    public void TheDefaultMarginsAreCloseEnoughToEachOtherToBeCoherent()
    {
        // Eighty-five per cent duty is about eighteen per cent of injector spare;
        // twenty per cent headroom is twenty per cent of pump spare. They need
        // not be identical, but a fuel system whose two halves carry wildly
        // different margins has a weakest part nobody chose.
        var spec = new RecipeSpec();

        double injectorSpare = (100 / spec.InjectorDutyLimit) - 1;
        double pumpSpare = spec.PumpHeadroomPercent / 100;

        Assert.InRange(injectorSpare, 0.15, 0.20);
        Assert.InRange(pumpSpare, 0.15, 0.25);
        Assert.InRange(Math.Abs(injectorSpare - pumpSpare), 0, 0.06);
    }
}
