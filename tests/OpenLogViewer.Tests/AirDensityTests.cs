using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The air, and what the published standards do about it.
///
/// Both correction factors are checked by finding the day each one is defined
/// against and asserting it returns exactly one there. That is the only test of a
/// transcribed formula worth much: a constant mistyped anywhere in either of them
/// moves the answer off unity on its own reference day.
/// </summary>
public class AirDensityTests
{
    [Fact]
    public void AStandardDayOfDryAirWeighsWhatTheTablesSay()
    {
        // 1.225 kg/m³ at one atmosphere and 15 °C is the figure every aerodynamic
        // calculation in the world starts from.
        var air = new Ambient(TuningMath.AtmosphericKpa, 15, HumidityPercent: 0);

        Assert.Equal(1.225, AirDensity.KgPerCubicMetre(air), 3);
    }

    [Fact]
    public void DampAirIsLighterThanDryAirAndNotHeavier()
    {
        // The one everybody has backwards. Water vapour is lighter than the
        // nitrogen and oxygen it displaces, so a muggy day is thinner — which is
        // why both standards are defined against the dry part of the pressure.
        var dry = new Ambient(101.3, 30, 0);
        var wet = new Ambient(101.3, 30, 100);

        Assert.True(AirDensity.KgPerCubicMetre(wet) < AirDensity.KgPerCubicMetre(dry));

        // And by about a per cent at that temperature, which is worth a couple of
        // horsepower on a big engine and is not worth panicking about.
        double difference = 1 - (AirDensity.KgPerCubicMetre(wet) / AirDensity.KgPerCubicMetre(dry));
        Assert.InRange(difference, 0.005, 0.02);
    }

    [Fact]
    public void SaturationPressureMatchesTheSteamTables()
    {
        // 2.339 kPa at 20 °C, 7.385 at 40. Magnus is a fit rather than the
        // steam tables themselves, so it is held to a quarter of a per cent
        // rather than to the last figure — which is what it is good for over the
        // range a car is used in, and far tighter than anything downstream needs.
        Assert.InRange(AirDensity.SaturationVapourPressureKpa(20) / 2.339, 0.9975, 1.0025);
        Assert.InRange(AirDensity.SaturationVapourPressureKpa(40) / 7.385, 0.9975, 1.0025);
    }

    // ----- the standards --------------------------------------------------------

    [Fact]
    public void SaeIsExactlyOneOnTheDayItIsDefinedAgainst()
    {
        // 990 mbar of dry air at 25 °C.
        var reference = new Ambient(99.0, 25, HumidityPercent: 0);

        Assert.Equal(1.0, AirDensity.SaeJ1349(reference), 9);
    }

    [Fact]
    public void DinIsExactlyOneOnTheDayItIsDefinedAgainst()
    {
        // 1013 mbar of dry air at 20 °C — a fuller, cooler day than SAE's.
        var reference = new Ambient(101.3, 20, HumidityPercent: 0);

        Assert.Equal(1.0, AirDensity.Din70020(reference), 9);
    }

    [Fact]
    public void DinAlwaysReadsHigherThanSaeOnTheSameDay()
    {
        // Two reasons pushing the same way: DIN references a fuller and cooler
        // standard day, and it makes no allowance for friction not falling with
        // the air. A car advertised in DIN and dynoed in SAE has not lost
        // anything.
        foreach (var air in new[]
        {
            new Ambient(101.3, 15, 40),
            new Ambient(98.6, 27, 45),
            new Ambient(92.0, 33, 20),
        })
        {
            Assert.True(
                AirDensity.Din70020(air) > AirDensity.SaeJ1349(air),
                $"DIN did not exceed SAE at {air}");
        }
    }

    [Fact]
    public void ThinnerAirAsksForABiggerCorrection()
    {
        var sea = new Ambient(101.3, 20, 40);
        var mountain = new Ambient(84.0, 20, 40);

        Assert.True(AirDensity.SaeJ1349(mountain) > AirDensity.SaeJ1349(sea));
    }

    [Fact]
    public void TheStandardStopsBeingValidBeforeTheArithmeticStops()
    {
        // SAE declares itself good between 0.93 and 1.07 and says nothing about
        // beyond. A mile and a half up on a hot day is outside it, and answering
        // anyway without saying so is how a figure ends up quoted against a
        // standard that disclaims it.
        Assert.True(AirDensity.WithinSaeLimits(new Ambient(101.3, 20, 40)));
        Assert.False(AirDensity.WithinSaeLimits(new Ambient(78.0, 35, 20)));
    }

    // ----- what gets applied ----------------------------------------------------

    [Fact]
    public void ABoostedEngineIsNotCorrectedByDefault()
    {
        // It held the manifold pressure the tuner asked for whatever the sky was
        // doing, so scaling its figure up credits it for a handicap the turbo
        // already cancelled.
        var ordinary = new Ambient(98.6, 27, 45);

        Assert.Equal(PowerCorrection.None, AirDensity.Recommended(boosted: true, ordinary));
        Assert.Equal(PowerCorrection.SaeJ1349, AirDensity.Recommended(boosted: false, ordinary));
    }

    [Fact]
    public void NoCorrectionMeansMultiplyingByOneRatherThanBranching()
    {
        Assert.Equal(1, AirDensity.Factor(PowerCorrection.None, new Ambient(84, 35, 10)));
    }

    [Fact]
    public void WhatWasNotAppliedIsStillSaidOutLoud()
    {
        // A corrected figure with nothing beside it saying so is the commonest
        // way a dyno number misleads, so an uncorrected one still reports what
        // the standard would have said.
        string said = AirDensity.Describe(PowerCorrection.None, new Ambient(98.6, 27, 45));

        Assert.Contains("Uncorrected", said, StringComparison.Ordinal);
        Assert.Contains("SAE J1349 would have been", said, StringComparison.Ordinal);

        string applied = AirDensity.Describe(PowerCorrection.Din70020, new Ambient(98.6, 27, 45));

        Assert.Contains("DIN 70020", applied, StringComparison.Ordinal);
        Assert.Contains("kg/m³", applied, StringComparison.Ordinal);
    }

    [Fact]
    public void ABarometerReadingBelowTheVapourPressureIsRefusedRatherThanDecoded()
    {
        // Not a damp day: a bad reading, or a sensor reporting gauge pressure.
        // Left alone it would make the dry pressure negative and the correction
        // with it.
        var nonsense = new Ambient(1.0, 40, 100);

        Assert.Equal(0, AirDensity.DryPressureKpa(nonsense));
        Assert.True(double.IsNaN(AirDensity.SaeJ1349(nonsense)));
    }
}
