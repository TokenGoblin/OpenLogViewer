using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// What it costs to run a car, and what a different one would cost.
///
/// The arithmetic is miles over economy times price, which is not where anything
/// goes wrong. What goes wrong is the comparison: a gallon is a volume rather than
/// an amount of energy, so the same miles per gallon on two fuels is not the same
/// efficiency; and an electric car is billed at the meter rather than at the
/// battery. Both make the cheaper-looking option look cheaper than it is, and
/// both are what these tests are mostly about.
/// </summary>
public class RunningCostTests
{
    private static VehicleCost Car(
        FuelKind kind, double economy, double? price = null, double loss = 10) =>
        new(kind.ToString(), kind, economy, price ?? FuelPrices.For(kind), loss);

    // ----- the plain arithmetic ------------------------------------------------

    /// <summary>
    /// 12,000 miles at 30 mpg is 400 gallons; at $4.089 that is $1,635.60.
    /// Worked by hand so the code is checked against something, not itself.
    /// </summary>
    [Fact]
    public void AYearOfPetrolCostsWhatTheGallonsCost()
    {
        VehicleCost car = Car(FuelKind.Petrol, 30, price: 4.089);

        Assert.Equal(400, car.UnitsPer(12_000), 6);
        Assert.Equal(1635.60, RunningCosts.PerYear(car, 12_000), 2);
        Assert.Equal(4.089 / 30, car.CostPerMile, 6);
    }

    /// <summary>
    /// The week and the month are twelfths and fifty-two-and-a-bit-ths of the
    /// year, not four-week blocks — thirteen four-week months would overstate a
    /// year by 8%.
    /// </summary>
    [Fact]
    public void TheWeeklyAndMonthlyFiguresAddBackUpToTheYear()
    {
        VehicleCost car = Car(FuelKind.Petrol, 30, price: 4.089);

        double year = RunningCosts.PerYear(car, 12_000);

        Assert.Equal(year, RunningCosts.PerMonth(car, 12_000) * 12, 6);
        Assert.Equal(year, RunningCosts.PerWeek(car, 12_000) * (365.25 / 7), 6);
    }

    // ----- a gallon is a volume, not an amount of energy ------------------------

    /// <summary>
    /// The trap the whole calculator exists to avoid.
    ///
    /// E85 is a dollar a gallon cheaper than petrol, so at the same quoted mpg it
    /// looks like a large saving. It is not: the gallon holds three-quarters of
    /// the energy, so the same car does about a quarter fewer miles on it. Costed
    /// honestly the two are close, and this asserts both halves — that the naive
    /// comparison flatters E85, and that the realistic one does not.
    /// </summary>
    [Fact]
    public void E85AtAHonestEconomyIsNotTheBargainTheSameNumberSuggests()
    {
        VehicleCost petrol = Car(FuelKind.Petrol, 30, price: 4.089);

        // What somebody types when they copy the petrol figure across.
        VehicleCost pretend = Car(FuelKind.E85, 30, price: 3.133);

        // What the same car would actually do, energy for energy.
        double honest = 30 * (25.2 / 33.7);
        VehicleCost real = Car(FuelKind.E85, honest, price: 3.133);

        Assert.True(pretend.CostPerMile < petrol.CostPerMile * 0.8,
            "copied across, E85 looks more than 20% cheaper");

        // Costed properly it is within a few per cent — the cheaper gallon very
        // nearly cancels the smaller gallon.
        double ratio = real.CostPerMile / petrol.CostPerMile;
        Assert.InRange(ratio, 0.95, 1.10);
    }

    /// <summary>The same mistake, caught and reported rather than quietly costed.</summary>
    [Fact]
    public void AnE85EconomyCopiedFromThePetrolColumnIsNoticed()
    {
        VehicleCost petrol = Car(FuelKind.Petrol, 30);

        Assert.True(RunningCosts.LooksCopiedFromPetrol(Car(FuelKind.E85, 30), [petrol]));
        Assert.True(RunningCosts.LooksCopiedFromPetrol(Car(FuelKind.E85, 28), [petrol]));

        // A properly reduced figure is not flagged.
        Assert.False(RunningCosts.LooksCopiedFromPetrol(Car(FuelKind.E85, 22), [petrol]));
    }

    [Fact]
    public void NothingButE85IsFlaggedForIt()
    {
        VehicleCost petrol = Car(FuelKind.Petrol, 30);

        Assert.False(RunningCosts.LooksCopiedFromPetrol(Car(FuelKind.Diesel, 40), [petrol]));
        Assert.False(RunningCosts.LooksCopiedFromPetrol(Car(FuelKind.Electricity, 3.5), [petrol]));
    }

    /// <summary>
    /// Miles per gallon equivalent is what puts three fuels on one scale. A car
    /// doing 30 mpg on petrol and one doing 22.4 on E85 are the same efficiency,
    /// and only this figure says so.
    /// </summary>
    [Fact]
    public void MpgePutsDifferentFuelsOnOneScale()
    {
        Assert.Equal(30, Car(FuelKind.Petrol, 30).Mpge, 6);

        // 30 * 25.2/33.7 on E85 is the same energy per mile as 30 mpg on petrol.
        Assert.Equal(30, Car(FuelKind.E85, 30 * 25.2 / 33.7).Mpge, 6);

        // Diesel's gallon holds more energy, so covering 30 miles on one burns
        // more energy than covering 30 miles on a gallon of petrol — which comes
        // out as a *lower* equivalent economy, not a higher one. 30 mpg on diesel
        // is 26.8 MPGe. This is the direction that catches people out, and it
        // caught the first version of this test.
        Assert.Equal(30 * 33.7 / 37.7, Car(FuelKind.Diesel, 30).Mpge, 6);
        Assert.True(Car(FuelKind.Diesel, 30).Mpge < 30);

        // E85's gallon holds less, so the same 30 miles from one is less energy.
        Assert.True(Car(FuelKind.E85, 30).Mpge > 30);

        // 33.7 kWh is one gallon equivalent by definition, so 1 mi/kWh is 33.7.
        Assert.Equal(33.7, Car(FuelKind.Electricity, 1).Mpge, 6);
    }

    /// <summary>CNG is sold by the gallon equivalent, so its mpg needs no conversion.</summary>
    [Fact]
    public void CngComparesDirectlyBecauseItsUnitIsDefinedThatWay() =>
        Assert.Equal(30, Car(FuelKind.Cng, 30).Mpge, 6);

    // ----- billed at the meter, not at the battery -----------------------------

    /// <summary>
    /// Charging loses ten to fifteen per cent between the meter and the battery,
    /// and the meter is what sends the bill. Costing an electric car from what
    /// reaches the wheels makes it look about an eighth cheaper than it is.
    /// </summary>
    [Fact]
    public void AnElectricCarIsCostedAtTheMeter()
    {
        VehicleCost ev = Car(FuelKind.Electricity, 3.5, price: 0.1883, loss: 10);

        // 1/3.5 reaches the wheels; the meter bills that over 0.9.
        Assert.Equal(1 / 3.5 / 0.9, ev.UnitsPerMile, 9);
        Assert.Equal(1 / 3.5 / 0.9 * 0.1883, ev.CostPerMile, 9);

        VehicleCost ignored = Car(FuelKind.Electricity, 3.5, price: 0.1883, loss: 0);

        Assert.True(ev.CostPerMile > ignored.CostPerMile);
        Assert.Equal(1 / 0.9, ev.CostPerMile / ignored.CostPerMile, 6);
    }

    /// <summary>
    /// The loss belongs in the cost and not in the efficiency. MPGe is what
    /// arrives at the wheels, so charging is not charged for twice.
    /// </summary>
    [Fact]
    public void TheChargingLossDoesNotAlsoReduceTheEfficiencyFigure()
    {
        Assert.Equal(
            Car(FuelKind.Electricity, 3.5, loss: 0).Mpge,
            Car(FuelKind.Electricity, 3.5, loss: 15).Mpge,
            6);
    }

    [Fact]
    public void NothingThatDoesNotPlugInIsChargedAChargingLoss()
    {
        Assert.Equal(
            Car(FuelKind.Petrol, 30, loss: 0).CostPerMile,
            Car(FuelKind.Petrol, 30, loss: 50).CostPerMile,
            9);
    }

    // ----- comparing ------------------------------------------------------------

    [Fact]
    public void TheCheapestToRunIsTheOneWithTheLowestCostPerMile()
    {
        VehicleCost petrol = Car(FuelKind.Petrol, 30);
        VehicleCost diesel = Car(FuelKind.Diesel, 42);
        VehicleCost ev = Car(FuelKind.Electricity, 3.5);

        Assert.Equal(ev, RunningCosts.Cheapest([petrol, diesel, ev]));
    }

    [Fact]
    public void AnUnusableVehicleIsLeftOutOfTheComparisonRatherThanWinningIt()
    {
        VehicleCost petrol = Car(FuelKind.Petrol, 30);
        VehicleCost blank = Car(FuelKind.Diesel, 0);

        Assert.Equal(petrol, RunningCosts.Cheapest([petrol, blank]));
        Assert.Null(RunningCosts.Cheapest([blank]));
        Assert.Null(RunningCosts.Cheapest([]));
    }

    [Fact]
    public void ASavingIsTheDifferenceOverTheYear()
    {
        VehicleCost petrol = Car(FuelKind.Petrol, 30, price: 4.089);
        VehicleCost ev = Car(FuelKind.Electricity, 3.5, price: 0.1883, loss: 10);

        double saving = RunningCosts.AnnualSaving(ev, petrol, 12_000);

        Assert.Equal(petrol.CostPer(12_000) - ev.CostPer(12_000), saving, 6);
        Assert.True(saving > 0);
    }

    /// <summary>
    /// The question a running-cost figure alone cannot answer. A car saving $600 a
    /// year that costs $8,000 more has saved nobody anything for thirteen years.
    /// </summary>
    [Fact]
    public void PaybackIsTheExtraPriceOverTheAnnualSaving()
    {
        VehicleCost petrol = Car(FuelKind.Petrol, 30, price: 4.089);
        VehicleCost ev = Car(FuelKind.Electricity, 3.5, price: 0.1883);

        double saving = RunningCosts.AnnualSaving(ev, petrol, 12_000);
        double years = RunningCosts.YearsToPayBack(ev, petrol, 12_000, 8_000);

        Assert.Equal(8_000 / saving, years, 6);
    }

    /// <summary>
    /// Never paying back is an answer rather than a failure, and NaN is how it is
    /// said — a large number would read as "eventually".
    /// </summary>
    [Fact]
    public void SomethingThatNeverPaysBackSaysSo()
    {
        VehicleCost thirsty = Car(FuelKind.Petrol, 15, price: 4.089);
        VehicleCost thirstier = Car(FuelKind.Petrol, 12, price: 4.089);

        Assert.True(double.IsNaN(RunningCosts.YearsToPayBack(thirstier, thirsty, 12_000, 5_000)));
        Assert.True(double.IsNaN(RunningCosts.YearsToPayBack(thirsty, thirstier, 12_000, 0)));
    }

    // ----- the starting prices --------------------------------------------------

    /// <summary>
    /// Every fuel has a price and a note, and the capture date is carried with
    /// them. A price with no date on it is a price nobody can judge the age of.
    /// </summary>
    [Fact]
    public void EveryFuelHasAPriceASourceAndADate()
    {
        Assert.All(Fuels.All, f =>
        {
            Assert.True(FuelPrices.For(f.Kind) > 0, $"{f.Name} has no starting price");
            Assert.False(string.IsNullOrWhiteSpace(f.Note), $"{f.Name} has no note");
            Assert.False(string.IsNullOrWhiteSpace(f.Unit));
        });

        Assert.Contains("2026", FuelPrices.Source, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2026, 8, 4), FuelPrices.CapturedOn);
    }

    /// <summary>A hybrid burns petrol, so it is priced as petrol.</summary>
    [Fact]
    public void AHybridIsPricedAsPetrolBecauseThatIsWhatItBurns()
    {
        Assert.Equal(FuelPrices.For(FuelKind.Petrol), FuelPrices.For(FuelKind.Hybrid));
        Assert.Equal(
            Fuels.For(FuelKind.Petrol).KwhPerUnit,
            Fuels.For(FuelKind.Hybrid).KwhPerUnit);
    }

    /// <summary>
    /// A plug-in hybrid is not modelled, and the note says so rather than letting
    /// somebody assume the hybrid entry covers it.
    /// </summary>
    [Fact]
    public void ThePlugInHybridGapIsStatedRatherThanLeftToBeDiscovered() =>
        Assert.Contains("plug-in", Fuels.For(FuelKind.Hybrid).Note, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(FuelKind.Petrol, "mpg")]
    [InlineData(FuelKind.Diesel, "mpg")]
    [InlineData(FuelKind.Cng, "mpg")]
    [InlineData(FuelKind.Electricity, "mi/kWh")]
    public void EconomyIsQuotedInTheUnitThatFuelUses(FuelKind kind, string expected) =>
        Assert.Equal(expected, Fuels.For(kind).EfficiencyUnit);

    // ----- nothing typed yet ----------------------------------------------------

    [Fact]
    public void AVehicleWithNoEconomyCostsNothingRatherThanInfinity()
    {
        VehicleCost blank = Car(FuelKind.Petrol, 0);

        Assert.False(blank.IsUsable);
        Assert.True(double.IsNaN(blank.CostPerMile));
        Assert.True(double.IsNaN(blank.Mpge));
        Assert.True(double.IsNaN(RunningCosts.PerYear(blank, 12_000)));
    }

    [Fact]
    public void NoMileageMeansNoAnnualFigure() =>
        Assert.True(double.IsNaN(RunningCosts.PerYear(Car(FuelKind.Petrol, 30), 0)));
}
