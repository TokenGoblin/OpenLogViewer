using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Power from acceleration.
///
/// The test that carries this file is <see cref="APowerCurveSurvivesBeingDrivenAndMeasured"/>.
/// Everything else checks one term at a time; that one writes a power curve down,
/// works out what a car with that engine would actually have done on the road,
/// and then measures the result the way the application will measure a real log.
/// If the differentiation, the force model, the gearing or the units are wrong in
/// any way that matters, the curve does not come back.
/// </summary>
public class RoadLoadTests
{
    private static VehicleSpec Car() => new()
    {
        KerbMassKg = 1530,
        OccupantMassKg = 82,
        DragAreaM2 = 0.68,
        RollingResistance = 0.0125,
        WheelInertiaKgM2 = 4.5,
        EngineInertiaKgM2 = 0.20,
        GearRatios = [3.27, 2.05, 1.62, 1.35, 1.03, 0.84],
        FinalDrive = 4.10,
        Tyre = new Tyre(245, 40, 18),
    };

    /// <summary>A believable turbocharged curve, in watts against engine speed.</summary>
    private static double EngineWatts(double rpm)
    {
        double x = (rpm - 3000) / 3800;

        return 200_000 + (150_000 * x) - (80_000 * x * x);
    }

    // ----- the closed loop ------------------------------------------------------

    [Fact]
    public void APowerCurveSurvivesBeingDrivenAndMeasured()
    {
        VehicleSpec car = Car();
        const int gear = 4;
        const double hz = 20;
        double rho = AirDensity.KgPerCubicMetre(new Ambient(98.6, 27, 45));

        // Drive the car. The engine makes exactly the curve above; the road takes
        // what the road takes; the speed does whatever the arithmetic says. A
        // millisecond step keeps the integration error far below anything the
        // measurement below could notice.
        const double dt = 0.001;

        double speed = car.SpeedMsFromRpm(3000, gear);
        double t = 0;

        var times = new List<double>();
        var speeds = new List<double>();
        var rpms = new List<double>();

        double nextSample = 0;

        while (true)
        {
            double rpm = car.RpmFromSpeedMs(speed, gear);
            if (rpm > 6800) break;

            if (t >= nextSample - 1e-12)
            {
                times.Add(t);
                speeds.Add(speed);
                rpms.Add(rpm);
                nextSample += 1 / hz;
            }

            speed += RoadLoad.AccelerationMs2(car, gear, speed, EngineWatts(rpm), rho) * dt;
            t += dt;
        }

        Assert.True(speeds.Count > 60, $"the synthetic pull was only {speeds.Count} samples");

        // Now measure it, exactly as the application will: differentiate the
        // speed trace, put the acceleration through the force model, and read the
        // power back off.
        ChannelFit fit = RateOfChange.Fit([.. speeds], [.. times], 0.5);

        int edge = (int)(0.5 / 2 * hz) + 1;
        double worst = 0;

        for (int i = edge; i < speeds.Count - edge; i++)
        {
            double measured = RoadLoad.Watts(car, gear, speeds[i], fit.SlopePerSecond[i], rho);
            double truth = EngineWatts(rpms[i]);

            worst = Math.Max(worst, Math.Abs(measured - truth) / truth);
        }

        // Half a per cent. What is left is the fit rounding the curvature of a
        // power curve that is genuinely curved, not an error in the model.
        Assert.True(worst < 0.005, $"worst sample was out by {worst:P2}");
    }

    [Fact]
    public void TheSameRunInTheWrongGearReportsTheWrongPower()
    {
        // The single largest way a road dyno is misused, and the reason the gear
        // is asked for rather than assumed. Second gear carries far more engine
        // inertia than fourth, so believing a fourth gear pull was made in second
        // credits the engine with accelerating a mass that was not there.
        VehicleSpec car = Car();

        double second = car.EffectiveMassKg(2);
        double fourth = car.EffectiveMassKg(4);

        Assert.True(second > fourth);

        // The whole of the difference is the engine's inertia through the square
        // of the ratio; nothing else in the car changed.
        double r = car.WheelRadiusM;
        double expected =
            car.EngineInertiaKgM2 * ((Math.Pow(car.TotalRatio(2), 2) - Math.Pow(car.TotalRatio(4), 2)) / (r * r));

        Assert.Equal(expected, second - fourth, 6);

        // And it is worth nearly five per cent of the answer between two adjacent
        // gears — more than most of the changes anyone makes a pull to measure.
        // Between first and fourth it is seventeen.
        double error = (second / fourth) - 1;
        Assert.InRange(error, 0.03, 0.08);

        Assert.InRange((car.EffectiveMassKg(1) / fourth) - 1, 0.14, 0.20);
    }

    // ----- one term at a time ---------------------------------------------------

    [Fact]
    public void AtASteadySpeedAllTheEffortGoesIntoTheRoadAndTheAir()
    {
        VehicleSpec car = Car();
        double rho = 1.225;
        double speed = 30;                       // about 67 mph

        RoadForces f = RoadLoad.Forces(car, 4, speed, accelerationMs2: 0, rho);

        Assert.Equal(0, f.Inertia);
        Assert.Equal(0, f.Grade, 9);

        // Worked independently: half the density, times the drag area, times the
        // speed squared.
        Assert.Equal(0.5 * 1.225 * 0.68 * 30 * 30, f.Aero, 6);
        Assert.Equal(0.0125 * car.MassKg * RoadLoad.Gravity, f.Rolling, 6);
    }

    [Fact]
    public void TheAirTakesAGrowingShareAsTheCarGoesFaster()
    {
        // Which is what says whether a pull is measuring the engine or measuring
        // the drag area somebody typed in.
        VehicleSpec car = Car();

        double slow = RoadLoad.Forces(car, 4, 20, 3, 1.225).AeroShare;
        double fast = RoadLoad.Forces(car, 4, 60, 3, 1.225).AeroShare;

        // The numbers are the point, not just the ordering: even at a hundred
        // and thirty miles an hour under load the air is a fifth of the effort,
        // so the drag area that was guessed at is worth a fifth of what it looks.
        Assert.True(fast > slow);
        Assert.InRange(slow, 0.01, 0.05);
        Assert.InRange(fast, 0.15, 0.30);
    }

    [Fact]
    public void AHillIsWorthEnoughToNotice()
    {
        // One per cent of gradient, at a hundred miles an hour, on this car: near
        // enough ten horsepower, which is larger than a good many of the changes
        // people make a pull to measure.
        VehicleSpec flat = Car();
        VehicleSpec uphill = Car() with { GradePercent = 1 };

        double speed = 44.7;                     // 100 mph

        double a = RoadLoad.Horsepower(flat, 4, speed, 2, 1.225);
        double b = RoadLoad.Horsepower(uphill, 4, speed, 2, 1.225);

        Assert.InRange(b - a, 7, 13);
    }

    [Fact]
    public void SlowingDownIsNotNegativePower()
    {
        // A lift mid-pull would otherwise put a spike through the chart. The pull
        // ought to be rejected instead; this is the second line.
        VehicleSpec car = Car();

        Assert.Equal(0, RoadLoad.Watts(car, 4, 40, accelerationMs2: -5, densityKgM3: 1.225));
    }

    [Fact]
    public void AStoppedCarHasNoPowerToReport()
    {
        Assert.True(double.IsNaN(RoadLoad.Watts(Car(), 4, 0, 3, 1.225)));
    }

    // ----- gears the car does not have ------------------------------------------

    [Fact]
    public void NeutralIsAStateRatherThanAnUnknownGear()
    {
        // With the driveline disconnected the engine's inertia genuinely is not
        // being accelerated. That is the condition a coastdown is measured in,
        // so it has to be expressible — and distinguishable from not knowing.
        VehicleSpec car = Car();

        double neutral = car.EffectiveMassKg(VehicleSpec.Neutral);

        Assert.True(double.IsFinite(neutral));
        Assert.True(neutral < car.EffectiveMassKg(6));

        // Exactly the mass plus the wheels, with nothing for the engine.
        Assert.Equal(car.MassKg + (car.WheelInertiaKgM2 / (car.WheelRadiusM * car.WheelRadiusM)),
                     neutral, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(-2)]
    public void AGearTheCarDoesNotHaveIsRefusedRatherThanAnswered(int gear)
    {
        // The tempting answer is the neutral figure, since there is no ratio to
        // square. It understates effective mass by four per cent in the tallest
        // gear and twenty-two in the lowest, and a power figure that much low
        // reads as a number rather than as a mistake. Pull detection hands out a
        // gear of zero whenever the ratio matched nothing, so a caller really can
        // arrive here with a gear the car has not got.
        Assert.True(double.IsNaN(Car().EffectiveMassKg(gear)));
        Assert.True(double.IsNaN(Car().MassFactor(gear)));
    }

    [Fact]
    public void NeutralDoesNotShareItsNumberWithAGearNobodyRecognised()
    {
        // The two meanings must not collide. Pull detection reports zero when the
        // ratio matched no gear the car has, and that must be refused; neutral is
        // a real state and must be answered. While both were zero, the one case
        // the refusal existed for was the one case that quietly got the neutral
        // figure instead — understating effective mass by up to a fifth and
        // reporting a power figure that much low as though it were fine.
        VehicleSpec car = Car();

        Assert.NotEqual(0, VehicleSpec.Neutral);

        Assert.True(double.IsFinite(car.EffectiveMassKg(VehicleSpec.Neutral)));
        Assert.True(double.IsNaN(car.EffectiveMassKg(0)));
    }

    [Fact]
    public void AnUnknownGearMakesThePowerUnknownRatherThanNought()
    {
        // The clamp that keeps a lift from reading as negative power reads NaN as
        // "not greater than zero" and would hand back a confident nought. Missing
        // readings propagate; they are not quietly zero.
        double watts = RoadLoad.Watts(Car(), gear: 99, speedMs: 40, accelerationMs2: 3, densityKgM3: 1.225);

        Assert.True(double.IsNaN(watts), $"got {watts}");
        Assert.True(double.IsNaN(RoadLoad.Horsepower(Car(), 99, 40, 3, 1.225)));
    }

    // ----- the constants --------------------------------------------------------

    [Fact]
    public void TheTorqueConstantIsThe5252EverySheetIsPrintedWith()
    {
        // Computed from the definition rather than transcribed, so this agrees to
        // all its figures instead of to four.
        Assert.Equal(5252.113, RoadLoad.TorqueConstant, 3);
    }

    [Fact]
    public void PowerAndTorqueCrossAt5252AndNowhereElse()
    {
        // Not a coincidence and not a convention: it is what the constant means,
        // and a sheet where the two curves cross anywhere else has arithmetic
        // wrong in it.
        double rpm = RoadLoad.TorqueConstant;

        Assert.Equal(400, RoadLoad.PoundFeet(400, rpm), 6);

        Assert.True(RoadLoad.PoundFeet(400, rpm - 1000) > 400);
        Assert.True(RoadLoad.PoundFeet(400, rpm + 1000) < 400);
    }

    [Fact]
    public void AHorsepowerIsTheMechanicalOneAndNotTheMetricOne()
    {
        // They differ by about one and a half per cent, which is most of the gap
        // between two figures for the same car quoted either side of the Atlantic.
        Assert.Equal(745.7, RoadLoad.WattsPerHorsepower, 1);
        Assert.True(RoadLoad.WattsPerHorsepower > 735.5);
    }

    [Fact]
    public void ACrankFigureIsAWheelFigureDividedByAnOpinion()
    {
        VehicleSpec car = Car() with { DrivetrainLossPercent = 15 };

        Assert.Equal(400 / 0.85, RoadLoad.AtCrank(400, car), 6);

        // With no loss declared, nothing is added — the honest default.
        Assert.Equal(400, RoadLoad.AtCrank(400, Car()), 9);
    }

    // ----- the inverse ----------------------------------------------------------

    [Fact]
    public void TheForwardAndBackwardArithmeticAgreeWithEachOther()
    {
        VehicleSpec car = Car();
        double rho = 1.18;

        double accel = RoadLoad.AccelerationMs2(car, 3, speedMs: 35, wheelWatts: 260_000, rho);
        double watts = RoadLoad.Watts(car, 3, 35, accel, rho);

        Assert.Equal(260_000, watts, 4);
    }

    [Fact]
    public void CoastingWithNoPowerIsTheRoadLoadAndNothingElse()
    {
        // What a coastdown measures, and the reason the inverse is public.
        VehicleSpec car = Car();

        double accel = RoadLoad.AccelerationMs2(car, 4, speedMs: 40, wheelWatts: 0, densityKgM3: 1.225);

        Assert.True(accel < 0);

        double expected =
            -(RoadLoad.AeroForce(car.DragAreaM2, 1.225, 40)
              + RoadLoad.RollingForce(car.MassKg, car.RollingResistance, 0))
            / car.EffectiveMassKg(4);

        Assert.Equal(expected, accel, 9);
    }
}
