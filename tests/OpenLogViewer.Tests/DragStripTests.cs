using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Quarter and eighth mile from power and weight.
///
/// These are correlations rather than physics, so the tests check them the way
/// correlations should be checked: against cars whose numbers everybody knows,
/// against each other, and for the properties that must hold whatever the
/// constants are — that the two directions invert, that more power is quicker,
/// and that the formulas bracket rather than contradict each other.
/// </summary>
public class DragStripTests
{
    private static DragFormula Hale => DragStrip.Formulas.First(f => f.Name == "Hale");
    private static DragFormula Huntington => DragStrip.Formulas.First(f => f.Name == "Huntington");

    [Fact]
    public void TheArithmeticIsCheckableByHandAtEightPoundsPerHorsepower()
    {
        // Chosen so the cube roots come out whole: 3,200 lb and 400 hp is eight
        // to one, whose cube root is two, and the reciprocal's is a half. So
        // Hale must give exactly its constants doubled and halved.
        Assert.Equal(5.825 * 2, DragStrip.QuarterEt(400, 3_200, Hale), 9);
        Assert.Equal(234 * 0.5, DragStrip.QuarterMph(400, 3_200, Hale), 9);

        Assert.Equal(11.65, DragStrip.QuarterEt(400, 3_200, Hale), 2);
        Assert.Equal(117, DragStrip.QuarterMph(400, 3_200, Hale), 0);
    }

    [Fact]
    public void TheThreeFormulasBracketEachOtherRatherThanDisagree()
    {
        // Hale describes a run that hooked up and Huntington one that did not,
        // so Hale must always be the quicker and the faster. The spread between
        // them is the launch, which is the point.
        foreach ((double hp, double lb) in ((double, double)[])[(300, 3_500), (500, 3_000), (1_000, 2_800)])
        {
            double quick = DragStrip.QuarterEt(hp, lb, Hale);
            double slow = DragStrip.QuarterEt(hp, lb, Huntington);

            Assert.True(quick < slow, "Hale should be the quicker of the two");
            Assert.InRange(slow / quick, 1.05, 1.10);

            Assert.True(DragStrip.QuarterMph(hp, lb, Hale) > DragStrip.QuarterMph(hp, lb, Huntington));
        }
    }

    [Theory]
    // Cars whose numbers are common knowledge, checked against the middle
    // formula. Wide bands on purpose: these are correlations, and a tighter
    // assertion would be pretending to a precision none of this has.
    [InlineData(3_600, 460, 12.0, 14.0, 105, 118)]   // a modern V8 muscle car
    [InlineData(3_200, 300, 13.0, 15.5, 95, 108)]    // an ordinary quick saloon
    [InlineData(2_800, 700, 9.0, 11.0, 130, 150)]    // something seriously built
    public void RealCarsLandWhereRealCarsLand(
        double weight, double hp, double etLow, double etHigh, double mphLow, double mphHigh)
    {
        Assert.InRange(DragStrip.QuarterEt(hp, weight, DragStrip.Default), etLow, etHigh);
        Assert.InRange(DragStrip.QuarterMph(hp, weight, DragStrip.Default), mphLow, mphHigh);
    }

    [Fact]
    public void MorePowerIsQuickerAndFasterAndMoreWeightIsNeither()
    {
        DragFormula f = DragStrip.Default;

        Assert.True(DragStrip.QuarterEt(600, 3_000, f) < DragStrip.QuarterEt(300, 3_000, f));
        Assert.True(DragStrip.QuarterMph(600, 3_000, f) > DragStrip.QuarterMph(300, 3_000, f));

        Assert.True(DragStrip.QuarterEt(400, 4_000, f) > DragStrip.QuarterEt(400, 3_000, f));
        Assert.True(DragStrip.QuarterMph(400, 4_000, f) < DragStrip.QuarterMph(400, 3_000, f));
    }

    [Fact]
    public void OnlyTheRatioOfPowerToWeightMatters()
    {
        // Which is the whole content of these formulas: double both and nothing
        // changes.
        DragFormula f = DragStrip.Default;

        Assert.Equal(DragStrip.QuarterEt(400, 3_200, f), DragStrip.QuarterEt(800, 6_400, f), 9);
        Assert.Equal(DragStrip.QuarterMph(400, 3_200, f), DragStrip.QuarterMph(800, 6_400, f), 9);
    }

    // ----- the eighth ----------------------------------------------------------

    [Fact]
    public void TheEighthIsAboutSixtyFivePerCentOfTheQuarter()
    {
        DragFormula f = DragStrip.Default;

        double quarter = DragStrip.QuarterEt(400, 3_200, f);
        double eighth = DragStrip.EighthEt(400, 3_200, f);

        Assert.Equal(quarter / DragStrip.QuarterOverEighthEt, eighth, 9);
        Assert.InRange(quarter / eighth, 1.55, 1.57);
    }

    [Fact]
    public void TheEighthIsNotHalfTheQuarterNorTheSquareRootOfTwo()
    {
        // Constant acceleration would put the ratio at 1.414. Real cars are past
        // 1.55 because the back half is covered with less acceleration left —
        // drag has grown and the gearing has run out. That gap is the whole
        // reason a conversion factor exists at all.
        Assert.True(DragStrip.QuarterOverEighthEt > Math.Sqrt(2));
        Assert.InRange(DragStrip.QuarterOverEighthEt - Math.Sqrt(2), 0.10, 0.20);
    }

    [Fact]
    public void AnEighthMileCarLandsWhereAnEighthMileCarLands()
    {
        // 400 hp in 3,200 lb: a shade under eight seconds and about ninety at
        // the eighth, which is what such a car runs.
        Assert.InRange(DragStrip.EighthEt(400, 3_200, DragStrip.Default), 7.5, 8.5);
        Assert.InRange(DragStrip.EighthMph(400, 3_200, DragStrip.Default), 85, 95);
    }

    // ----- reading a timeslip --------------------------------------------------

    [Fact]
    public void PowerAndTrapSpeedInvertEachOther()
    {
        foreach (DragFormula formula in DragStrip.Formulas)
        {
            double mph = DragStrip.QuarterMph(450, 3_400, formula);

            Assert.Equal(450, DragStrip.HorsepowerFromTrap(mph, 3_400, formula), 6);

            double et = DragStrip.QuarterEt(450, 3_400, formula);

            Assert.Equal(450, DragStrip.HorsepowerFromEt(et, 3_400, formula), 6);
        }
    }

    [Fact]
    public void APerfectRunIsReadAsCostingNothing()
    {
        // A slip whose time is exactly what its trap deserved: the launch cost
        // nothing and both readings of power agree.
        DragFormula f = DragStrip.Default;

        double mph = DragStrip.QuarterMph(500, 3_300, f);
        double et = DragStrip.QuarterEt(500, 3_300, f);

        SlipReading reading = DragStrip.Read(mph, et, 3_300, f);

        Assert.Equal(500, reading.PowerFromTrap, 6);
        Assert.Equal(500, reading.PowerFromEt, 6);
        Assert.Equal(0, reading.LaunchCost, 6);
    }

    [Fact]
    public void ASpunLaunchIsReadAsTimeLostRatherThanPowerMissing()
    {
        // The reason this exists. The car trapped what a 500 hp car traps, so it
        // has 500 hp — but it took half a second longer than that trap deserved,
        // and that half second is the start line rather than the engine.
        DragFormula f = DragStrip.Default;

        double mph = DragStrip.QuarterMph(500, 3_300, f);
        double deserved = DragStrip.QuarterEt(500, 3_300, f);

        SlipReading reading = DragStrip.Read(mph, deserved + 0.5, 3_300, f);

        // The trap still says 500. The time, read on its own, says far less.
        Assert.Equal(500, reading.PowerFromTrap, 6);
        Assert.True(reading.PowerFromEt < 460,
            "reading power from a spun run's time understates it badly");

        Assert.Equal(0.5, reading.LaunchCost, 6);
        Assert.True(reading.LaunchCost > DragStrip.LaunchWorthMentioning);
    }

    [Fact]
    public void AGoodLaunchShowsAsTimeGainedRatherThanPowerFound()
    {
        DragFormula f = DragStrip.Default;

        double mph = DragStrip.QuarterMph(500, 3_300, f);
        double deserved = DragStrip.QuarterEt(500, 3_300, f);

        SlipReading reading = DragStrip.Read(mph, deserved - 0.4, 3_300, f);

        Assert.Equal(500, reading.PowerFromTrap, 6);
        Assert.True(reading.PowerFromEt > 500);
        Assert.Equal(-0.4, reading.LaunchCost, 6);
    }

    [Fact]
    public void TrapSpeedIsTheLessSensitiveOfTheTwoToPower()
    {
        // Why the trap is read for power and the time is not: speed goes with
        // the cube root, so ten per cent more power is only three per cent more
        // speed — but it is three per cent that a bad launch cannot take away,
        // where the time carries the launch in it for good.
        DragFormula f = DragStrip.Default;

        double mphRatio = DragStrip.QuarterMph(550, 3_300, f) / DragStrip.QuarterMph(500, 3_300, f);

        Assert.InRange(mphRatio, 1.02, 1.04);
        Assert.Equal(Math.Cbrt(1.1), mphRatio, 6);
    }

    [Fact]
    public void NonsenseIsNotAQuarterMile()
    {
        DragFormula f = DragStrip.Default;

        Assert.True(double.IsNaN(DragStrip.QuarterEt(0, 3_200, f)));
        Assert.True(double.IsNaN(DragStrip.QuarterEt(400, 0, f)));
        Assert.True(double.IsNaN(DragStrip.QuarterMph(-100, 3_200, f)));
        Assert.True(double.IsNaN(DragStrip.HorsepowerFromTrap(0, 3_200, f)));
        Assert.True(double.IsNaN(DragStrip.HorsepowerFromEt(0, 3_200, f)));
    }

    [Fact]
    public void EveryFormulaIsNamedAndDescribed()
    {
        Assert.Equal(3, DragStrip.Formulas.Count);

        foreach (DragFormula formula in DragStrip.Formulas)
        {
            Assert.False(string.IsNullOrWhiteSpace(formula.Name));
            Assert.False(string.IsNullOrWhiteSpace(formula.Note));
            Assert.InRange(formula.EtConstant, 5, 7);
            Assert.InRange(formula.MphConstant, 200, 250);
        }

        Assert.Contains(DragStrip.Default, DragStrip.Formulas);
    }
}
