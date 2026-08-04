using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Estimating power from a log.
///
/// The expressions are not inspected — they are run. Every check below builds a
/// log whose numbers are known, hands the generated definitions to the same
/// evaluator the application uses, and reads the horsepower out the far end. An
/// expression that is right about its arithmetic and wrong about a unit, or that
/// references a channel by a name the parser cannot resolve, fails here in the
/// same way it would fail on a real log — which is the only way this could have
/// been checked honestly, since the whole feature is a string that has to survive
/// being parsed.
/// </summary>
public class PowerEstimateTests
{
    private static LogDocument Log(params (string Name, string Units, double[] Values)[] channels) =>
        new()
        {
            FilePath = "test.csv",
            FormatName = "CSV",
            Channels = [.. channels.Select(c => new LogChannel(c.Name, c.Units, 3, c.Values))],
            Time = new LogChannel("Time", "s", 3, [0, 1, 2], preservePrecision: true),
        };

    /// <summary>Builds the estimate's channels for real and hands back a reading from one.</summary>
    private static double Value(LogDocument log, EngineSpec spec, string channel, int sample = 0)
    {
        PowerEstimateResult estimate = PowerEstimate.For(log, spec);
        MathChannelResult built = MathChannelBuilder.Build(log, estimate.Channels);

        Assert.Empty(built.Problems);

        LogChannel? found = built.Channels.FirstOrDefault(c => c.Name == channel);

        Assert.NotNull(found);

        return found.At(sample);
    }

    /// <summary>
    /// A 2.0 litre at 7,000 rpm, wide open, at sea level on a 20 °C day.
    ///
    /// Chosen so the answer is checkable by hand: at full volumetric efficiency
    /// it swallows 8.43 kg of air a minute, which at 12.5:1 and a BSFC of 0.50
    /// is 178 horsepower.
    /// </summary>
    private static LogDocument WideOpen() => Log(
        ("RPM", "rpm", [7000, 7000, 7000]),
        ("MAP", "kPa", [101.325, 101.325, 101.325]),
        ("IAT", "C", [20, 20, 20]),
        ("AFR", "afr", [12.5, 12.5, 12.5]));

    private static readonly EngineSpec TwoLitre = new()
    {
        Litres = 2.0,
        Cylinders = 4,
        Bsfc = 0.50,
        VolumetricEfficiency = 100,
    };

    // ----- speed density -------------------------------------------------------

    [Fact]
    public void TheAirIsWhatTheGasLawSaysItIs()
    {
        // 8.43 kg/min, worked out independently: two litres, half of 7,000 rpm,
        // at 1.2044 kg/m³ — which is 101.325 kPa over 287 × 293.15 K.
        double airflow = Value(WideOpen(), TwoLitre, PowerEstimate.AirflowChannel);

        Assert.Equal(8.43, airflow, 2);
    }

    [Fact]
    public void ATwoLitreAtSevenThousandMakesAboutOneHundredAndEightyHorsepower()
    {
        double hp = Value(WideOpen(), TwoLitre, PowerEstimate.SpeedDensityChannel);

        Assert.Equal(178.4, hp, 1);
    }

    [Fact]
    public void TheEstimateAgreesWithTheCalculatorItShares()
    {
        // The airflow tab answers the same question through a different route —
        // cubic feet a minute at standard density, then pounds a minute. Getting
        // the same figure two ways is worth more than either on its own.
        double hp = Value(WideOpen(), TwoLitre, PowerEstimate.SpeedDensityChannel);

        double cfm = TuningMath.CubicFeetPerMinute(2.0, 7000, 100);
        double viaCalculator = TuningMath.HorsepowerFromAir(
            TuningMath.AirPoundsPerMinute(cfm), Fuel.Petrol, 0.85, 0.50);

        // Not identical: the calculator works at the standard 15 °C where this
        // log is a 20 °C day, which is worth about two per cent of the density.
        Assert.InRange(hp / viaCalculator, 0.97, 1.0);
    }

    [Theory]
    // The same air, described in whatever units the logger felt like.
    [InlineData("kPa", 101.325, "C", 20.0)]
    [InlineData("psi", 14.6959, "C", 20.0)]
    [InlineData("bar", 1.01325, "C", 20.0)]
    [InlineData("kPa", 101.325, "F", 68.0)]
    [InlineData("psi", 14.6959, "F", 68.0)]
    public void TheUnitsTheLoggerChoseDoNotChangeTheAnswer(
        string mapUnits, double mapValue, string iatUnits, double iatValue)
    {
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", mapUnits, [mapValue, mapValue, mapValue]),
            ("IAT", iatUnits, [iatValue, iatValue, iatValue]),
            ("AFR", "afr", [12.5, 12.5, 12.5]));

        Assert.Equal(178.4, Value(log, TwoLitre, PowerEstimate.SpeedDensityChannel), 0);
    }

    [Fact]
    public void ALambdaChannelIsNotMistakenForAnAirFuelRatio()
    {
        // 0.85 lambda is 12.5:1 on petrol, so this must land on the same answer
        // as the log that spelled it out. Reading 0.85 as a ratio would divide
        // the air by 0.85 and report fifteen times the power.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", "kPa", [101.325, 101.325, 101.325]),
            ("IAT", "C", [20, 20, 20]),
            ("Lambda", "lambda", [0.85034, 0.85034, 0.85034]));

        Assert.Equal(178.4, Value(log, TwoLitre, PowerEstimate.SpeedDensityChannel), 0);
    }

    [Fact]
    public void ABareMixtureChannelIsToldApartByItsValues()
    {
        // Plenty of firmware logs a mixture with no unit at all. Around one it is
        // lambda; around fifteen it is a ratio; nothing sensible is both.
        var lambda = new LogChannel("Mixture", "", 3, [0.85, 0.88, 0.90]);
        var ratio = new LogChannel("Mixture", "", 3, [12.5, 13.0, 13.2]);

        Assert.True(ChannelUnits.IsLambda(lambda));
        Assert.False(ChannelUnits.IsLambda(ratio));
    }

    [Fact]
    public void BoostLoggedAsGaugePressureIsRecognisedAndAddedBackOn()
    {
        // A channel that goes below zero is a gauge reading — an absolute
        // pressure has nowhere under vacuum to go. Treating 10 psi of boost as
        // an absolute 69 kPa would report a third of the real airflow.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", "psi", [-8, 0, 10]),
            ("IAT", "C", [20, 20, 20]),
            ("AFR", "afr", [12.5, 12.5, 12.5]));

        // The third sample is 10 psi of boost: 170.3 kPa absolute, which is 1.68
        // times the atmospheric case and so 1.68 times the power.
        double boosted = Value(log, TwoLitre, PowerEstimate.SpeedDensityChannel, sample: 2);
        double atmospheric = Value(log, TwoLitre, PowerEstimate.SpeedDensityChannel, sample: 1);

        Assert.Equal(178.4, atmospheric, 0);
        Assert.Equal(1.681, boosted / atmospheric, 2);
    }

    [Fact]
    public void ALoggedVolumetricEfficiencyIsUsedRatherThanTheAssumedOne()
    {
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", "kPa", [101.325, 101.325, 101.325]),
            ("IAT", "C", [20, 20, 20]),
            ("AFR", "afr", [12.5, 12.5, 12.5]),
            ("VE", "%", [50, 50, 50]));

        // Half the filling, half the air, half the power — and the spec's 100%
        // ignored in favour of what the ECU actually reported.
        Assert.Equal(89.2, Value(log, TwoLitre, PowerEstimate.SpeedDensityChannel), 1);
    }

    // ----- injectors -----------------------------------------------------------

    /// <summary>
    /// Four 1,000 cc injectors at 7,000 rpm on a 10.3 ms pulse, of which 0.3 ms
    /// is the injector opening: 58.33 per cent duty, 2,333 cc of petrol a minute,
    /// 229.9 lb/hr, and at a BSFC of 0.50 that is 460 horsepower.
    /// </summary>
    private static readonly EngineSpec BigInjectors = new()
    {
        Litres = 2.0,
        Cylinders = 4,
        Bsfc = 0.50,
        InjectorCcPerMinute = 1000,
        InjectorDeadTimeMs = 0.3,
    };

    [Fact]
    public void TheDutyIsWhatThePulseWidthAndTheEngineSpeedMake()
    {
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [10.3, 10.3, 10.3]));

        Assert.Equal(58.33, Value(log, BigInjectors, PowerEstimate.DutyChannel), 2);
    }

    [Fact]
    public void TheFuelAndThePowerFollowFromTheDuty()
    {
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [10.3, 10.3, 10.3]));

        Assert.Equal(229.9, Value(log, BigInjectors, PowerEstimate.FuelFlowChannel), 1);
        Assert.Equal(459.9, Value(log, BigInjectors, PowerEstimate.InjectorPowerChannel), 0);
    }

    [Fact]
    public void DeadTimeIsTakenOffAndNeverAddedBack()
    {
        // A millisecond of dead time at 7,000 rpm is six per cent of the cycle.
        // Leaving it out overstates the fuel, and therefore the engine.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [10.3, 10.3, 10.3]));

        double withDeadTime = Value(log, BigInjectors, PowerEstimate.InjectorPowerChannel);
        double without = Value(
            log, BigInjectors with { InjectorDeadTimeMs = 0 }, PowerEstimate.InjectorPowerChannel);

        Assert.True(without > withDeadTime);
        Assert.Equal(0.3 / 10.3, 1 - (withDeadTime / without), 3);
    }

    [Fact]
    public void APulseShorterThanTheDeadTimeIsNoFuelRatherThanNegativeFuel()
    {
        // Overrun: the ECU commands a pulse too short to open the injector. The
        // arithmetic would otherwise produce a negative duty and negative power,
        // which would drag the channel's whole axis down with it.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [0.1, 0.1, 0.1]));

        Assert.Equal(0, Value(log, BigInjectors, PowerEstimate.DutyChannel), 6);
        Assert.Equal(0, Value(log, BigInjectors, PowerEstimate.InjectorPowerChannel), 6);
    }

    [Fact]
    public void BatchInjectionIsWorthExactlyTwiceSequential()
    {
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [4, 4, 4]));

        double sequential = Value(log, BigInjectors, PowerEstimate.InjectorPowerChannel);
        double batch = Value(
            log, BigInjectors with { BatchInjection = true }, PowerEstimate.InjectorPowerChannel);

        Assert.Equal(2, batch / sequential, 6);
    }

    [Fact]
    public void ALoggedDutyCycleIsUsedInsteadOfWorkingOneOut()
    {
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("Injector duty", "%", [58.333, 58.333, 58.333]));

        // The same answer the pulse-width route reaches, which is the check that
        // the two ways in agree rather than merely both producing a number.
        Assert.Equal(459.9, Value(log, BigInjectors, PowerEstimate.InjectorPowerChannel), 0);
    }

    // ----- fuel pressure -------------------------------------------------------

    [Fact]
    public void FlowFollowsTheSquareRootOfThePressureAcrossTheInjector()
    {
        // Rated at 300 kPa and run at 400: the square root of 4/3 is 1.155, so a
        // 1,000 cc injector flows 1,155.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [10.3, 10.3, 10.3]),
            ("Fuel pressure", "kPa", [400, 400, 400]));

        Assert.Equal(1155, Value(
            log, BigInjectors with { FuelPressureIsDifferential = true },
            PowerEstimate.InjectorFlowChannel), 0);
    }

    [Fact]
    public void BoostEatsIntoTheRailPressureUnlessTheRegulatorFollowsIt()
    {
        // The subtlety worth having this feature for. The rail gauge reads 300
        // kPa; the manifold is at 170.3 absolute, which is 69 above the
        // atmosphere. So the injector is only working against 231, not 300, and
        // flows the square root of that fraction less.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [10.3, 10.3, 10.3]),
            ("MAP", "kPa", [170.3, 170.3, 170.3]),
            ("Fuel pressure", "kPa", [300, 300, 300]));

        double flow = Value(log, BigInjectors, PowerEstimate.InjectorFlowChannel);

        Assert.Equal(1000 * Math.Sqrt((300 + 101.325 - 170.3) / 300), flow, 0);
        Assert.InRange(flow, 870, 890);
    }

    [Fact]
    public void ARailThatCollapsesIsNotASourceOfImaginaryFuel()
    {
        // A pressure difference below zero cannot happen and a square root of it
        // is not a number. Clamped, so a bad sensor reading costs a sample rather
        // than the channel.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("PW", "ms", [10.3, 10.3, 10.3]),
            ("MAP", "kPa", [500, 500, 500]),
            ("Fuel pressure", "kPa", [100, 100, 100]));

        Assert.Equal(0, Value(log, BigInjectors, PowerEstimate.InjectorFlowChannel), 6);
    }

    // ----- the two together ----------------------------------------------------

    [Fact]
    public void TheTwoMethodsAgreeWhenTheInputsAreConsistent()
    {
        // The heart of it. An engine swallowing 8.43 kg of air a minute at 12.5:1
        // is burning 0.674 kg of fuel a minute, which is 905 cc of petrol —
        // 22.6 per cent duty on four 1,000 cc injectors at 7,000 rpm. Fed both,
        // the two independent estimates must land on the same horsepower.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", "kPa", [101.325, 101.325, 101.325]),
            ("IAT", "C", [20, 20, 20]),
            ("AFR", "afr", [12.5, 12.5, 12.5]),
            ("PW", "ms", [4.176, 4.176, 4.176]));

        var spec = new EngineSpec
        {
            Litres = 2.0,
            Cylinders = 4,
            Bsfc = 0.50,
            VolumetricEfficiency = 100,
            InjectorCcPerMinute = 1000,
            InjectorDeadTimeMs = 0.3,
        };

        double air = Value(log, spec, PowerEstimate.SpeedDensityChannel);
        double fuel = Value(log, spec, PowerEstimate.InjectorPowerChannel);

        Assert.Equal(178.4, air, 1);
        Assert.InRange(fuel / air, 0.99, 1.01);

        // And the spread channel says so.
        Assert.InRange(Value(log, spec, PowerEstimate.SpreadChannel), -1.5, 1.5);
    }

    [Fact]
    public void TheSpreadIsWhatSaysAVeTableIsWrong()
    {
        // The diagnostic. The injectors are counting real fuel; the speed density
        // figure believes a VE that is twenty per cent optimistic. The spread is
        // what makes that visible rather than leaving two plausible numbers.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", "kPa", [101.325, 101.325, 101.325]),
            ("IAT", "C", [20, 20, 20]),
            ("AFR", "afr", [12.5, 12.5, 12.5]),
            ("PW", "ms", [4.176, 4.176, 4.176]),
            ("VE", "%", [120, 120, 120]));

        var spec = new EngineSpec
        {
            Litres = 2.0, Cylinders = 4, Bsfc = 0.50,
            InjectorCcPerMinute = 1000, InjectorDeadTimeMs = 0.3,
        };

        double spread = Value(log, spec, PowerEstimate.SpreadChannel);

        // The injector figure sits about a sixth below the inflated air figure.
        Assert.InRange(spread, -20, -14);
    }

    // ----- mass air flow, torque and the wheels --------------------------------

    [Fact]
    public void AMeasuredAirflowIsUsedDirectly()
    {
        // 8.43 kg/min is 140.5 g/s, and the same 178 horsepower.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAF", "g/s", [140.5, 140.5, 140.5]),
            ("AFR", "afr", [12.5, 12.5, 12.5]));

        Assert.Equal(178.4, Value(log, TwoLitre, PowerEstimate.MafPowerChannel), 0);
    }

    [Fact]
    public void TorqueIsPowerAndEngineSpeedAndNothingElse()
    {
        double hp = Value(WideOpen(), TwoLitre, PowerEstimate.SpeedDensityChannel);
        double torque = Value(WideOpen(), TwoLitre, PowerEstimate.TorqueChannel);

        Assert.Equal(hp * 5252 / 7000, torque, 1);
        Assert.Equal(133.9, torque, 1);
    }

    [Fact]
    public void TheWheelFigureIsOnlyOfferedWhenALossIsGiven()
    {
        PowerEstimateResult none = PowerEstimate.For(WideOpen(), TwoLitre);

        Assert.DoesNotContain(none.Channels, c => c.Name == PowerEstimate.WheelChannel);

        // Against the crank figure this log actually produced rather than against
        // a rounded copy of it, so the check is the fifteen per cent and not the
        // rounding.
        double crank = Value(WideOpen(), TwoLitre, PowerEstimate.SpeedDensityChannel);
        double wheels = Value(
            WideOpen(), TwoLitre with { DrivetrainLossPercent = 15 }, PowerEstimate.WheelChannel);

        Assert.Equal(0.85, wheels / crank, 4);
    }

    // ----- what a log cannot support -------------------------------------------

    [Fact]
    public void AMethodIsNotOfferedWhenTheLogCannotFeedIt()
    {
        // An OBD2 log with no pulse width and no air meter: speed density only.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", "kPa", [101.325, 101.325, 101.325]),
            ("IAT", "C", [20, 20, 20]));

        PowerEstimateResult estimate = PowerEstimate.For(log, TwoLitre);

        Assert.Contains(estimate.Methods, m => m.Name == "Speed density");
        Assert.DoesNotContain(estimate.Methods, m => m.Name == "Injectors");
        Assert.DoesNotContain(estimate.Methods, m => m.Name == "Mass air flow");

        // And says what was wanted rather than going quiet about it.
        Assert.Contains(estimate.Unavailable, u => u.Name == "Injectors" && u.Needs.Contains("pulse"));
        Assert.Contains(estimate.Unavailable, u => u.Name == "Mass air flow");
    }

    [Fact]
    public void ALogWithNothingUsefulOffersNothingAndExplainsItself()
    {
        LogDocument log = Log(("Coolant", "C", [80, 80, 80]));

        PowerEstimateResult estimate = PowerEstimate.For(log, TwoLitre);

        Assert.Empty(estimate.Methods);
        Assert.Equal(3, estimate.Unavailable.Count);

        Assert.All(estimate.Unavailable, u => Assert.False(string.IsNullOrWhiteSpace(u.Needs)));

        // Named individually, so the user knows which sensor would unlock which.
        Assert.Contains(estimate.Unavailable, u => u.Needs.Contains("engine speed"));
    }

    [Fact]
    public void EveryGeneratedExpressionParsesOnTheLogItWasBuiltFor()
    {
        // The blanket check. Channel names with spaces in them, numbers formatted
        // for the current culture, a chain where each channel reads the one
        // before — any of these could break the parser, and all of them are
        // generated rather than written out.
        LogDocument log = Log(
            ("Engine Speed", "rpm", [7000, 3000, 1000]),
            ("Manifold Absolute Pressure", "kPa", [101.325, 60, 30]),
            ("Intake Air Temperature", "C", [20, 25, 30]),
            ("Air Fuel Ratio", "afr", [12.5, 14.7, 14.7]),
            ("Injector Pulse Width", "ms", [10.3, 4, 2]),
            ("Fuel Rail Pressure", "kPa", [300, 300, 300]),
            ("Mass Air Flow", "g/s", [140, 60, 20]));

        PowerEstimateResult estimate = PowerEstimate.For(log, BigInjectors);
        MathChannelResult built = MathChannelBuilder.Build(log, estimate.Channels);

        Assert.Empty(built.Problems);
        Assert.Equal(estimate.Channels.Count, built.Channels.Count);

        // And every one of them produced real numbers rather than a column of NaN.
        foreach (LogChannel channel in built.Channels)
            Assert.True(double.IsFinite(channel.At(0)), $"{channel.Name} produced nothing");
    }

    [Fact]
    public void NoGeneratedChannelCollidesWithOneTheLogAlreadyHas()
    {
        // The builder refuses a name the log already carries, and a collision
        // would silently cost whichever method owned it.
        LogDocument log = Log(
            ("RPM", "rpm", [7000, 7000, 7000]),
            ("MAP", "kPa", [101.325, 101.325, 101.325]),
            ("IAT", "C", [20, 20, 20]),
            ("PW", "ms", [10.3, 10.3, 10.3]));

        PowerEstimateResult estimate = PowerEstimate.For(log, BigInjectors);

        foreach (MathChannel channel in estimate.Channels)
            Assert.DoesNotContain(log.Channels, c =>
                c.Name.Equals(channel.Name, StringComparison.OrdinalIgnoreCase));

        // The names are distinct from each other too.
        Assert.Equal(
            estimate.Channels.Count,
            estimate.Channels.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
