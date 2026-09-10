using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Drawing a pull.
///
/// The test that carries this file is
/// <see cref="APowerCurveSurvivesTheWholeChain"/>. Everything upstream of here
/// has been tested a piece at a time; that one writes a power curve down, drives
/// a car with it, hands the recording to the application exactly as a person
/// would, and checks that the curve which comes out the far end is the one that
/// went in — through pull detection, gear inference, the derivative, the force
/// model, the binning and the peaks, with nothing skipped.
/// </summary>
public class DynoCurveTests
{
    private static readonly Ambient Air = new(98.6, 27, 45);

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
        Boosted = true,
    };

    /// <summary>A believable turbocharged curve, in wheel watts against engine speed.</summary>
    private static double TruthWatts(double rpm)
    {
        double x = (rpm - 3000) / 3800;

        return 200_000 + (150_000 * x) - (80_000 * x * x);
    }

    private static double TruthHp(double rpm) => TruthWatts(rpm) / RoadLoad.WattsPerHorsepower;

    /// <summary>
    /// Drives the car with a given power curve and writes down what a logger
    /// would have seen: a cruise, a pull, and a spell against the limiter.
    /// </summary>
    private static LogDocument Drive(
        VehicleSpec car, int gear = 4, double hz = 20,
        Func<double, double>? watts = null, double toRpm = 6800)
    {
        watts ??= TruthWatts;

        double rho = AirDensity.KgPerCubicMetre(Air);
        const double dt = 0.001;

        List<double> times = [], rpms = [], speeds = [], tps = [];

        // Cruising at part throttle before the pedal goes down.
        for (double t = 0; t < 3; t += 1 / hz)
        {
            times.Add(t);
            rpms.Add(3000);
            speeds.Add(car.SpeedMsFromRpm(3000, gear));
            tps.Add(16);
        }

        double now = 3;
        double speed = car.SpeedMsFromRpm(3000, gear);
        double clock = 0;
        double next = 0;

        while (true)
        {
            double rpm = car.RpmFromSpeedMs(speed, gear);
            if (rpm > toRpm) break;

            if (clock >= next - 1e-12)
            {
                times.Add(now + clock);
                rpms.Add(rpm);
                speeds.Add(speed);
                tps.Add(98);
                next += 1 / hz;
            }

            speed += RoadLoad.AccelerationMs2(car, gear, speed, watts(rpm), rho) * dt;
            clock += dt;
        }

        // And nudging the limiter afterwards, pedal still down.
        now += clock;

        for (double t = 0; t < 2; t += 1 / hz)
        {
            times.Add(now + t);
            rpms.Add(toRpm + (2 * t));
            speeds.Add(car.SpeedMsFromRpm(toRpm + (2 * t), gear));
            tps.Add(98);
        }

        return new LogDocument
        {
            FilePath = "drive",
            Time = new LogChannel("Time", "s", 3, [.. times], preservePrecision: true),
            Channels =
            [
                new LogChannel("RPM", "rpm", 0, [.. rpms]),
                new LogChannel("TPS", "%", 1, [.. tps]),
                new LogChannel("VSS", "km/h", 1, [.. speeds.Select(v => v * 3.6)]),
            ],
            FormatName = "test",
        };
    }

    private static DynoPull OnePull(LogDocument log, VehicleSpec car)
    {
        PullSearchResult found = DynoRun.Find(log, car);

        Assert.True(found.Pulls.Count == 1, $"expected one pull, got {found.Pulls.Count}: {found.Summary}");

        return found.Pulls[0];
    }

    // ----- the whole chain --------------------------------------------------------

    [Fact]
    public void APowerCurveSurvivesTheWholeChain()
    {
        VehicleSpec car = Car();
        LogDocument log = Drive(car);

        DynoCurve curve = DynoCurve.FromRoadLoad(log, car, OnePull(log, car), Air);

        Assert.False(curve.IsEmpty);
        Assert.Equal(PowerMethodKind.RoadLoad, curve.Method);

        double worst = 0;
        double worstAt = 0;

        foreach (DynoPoint p in curve.Drawn)
        {
            double error = Math.Abs(p.Horsepower - TruthHp(p.Rpm)) / TruthHp(p.Rpm);

            if (error <= worst) continue;

            worst = error;
            worstAt = p.Rpm;
        }

        Assert.True(worst < 0.01, $"worst rung was out by {worst:P2} at {worstAt:N0} rpm");

        // And the peak lands where the curve actually peaks, rather than on
        // whichever rung happened to hold a spike.
        double truePeakRpm = 6800;
        for (double r = 3000; r <= 6800; r += 10)
        {
            if (TruthHp(r) > TruthHp(truePeakRpm)) truePeakRpm = r;
        }

        Assert.InRange(curve.PeakPower.Rpm, truePeakRpm - 400, truePeakRpm + 400);
        Assert.Equal(TruthHp(curve.PeakPower.Rpm), curve.PeakPower.Horsepower, 1);
    }

    [Fact]
    public void PowerAndTorqueCrossWhereTheArithmeticSaysTheyMust()
    {
        // A check rather than a finding. The two are the same measurement scaled
        // by engine speed over 5,252, so they are equal exactly there — and a
        // sheet whose lines cross anywhere else has something wrong in it.
        VehicleSpec car = Car();
        LogDocument log = Drive(car);

        DynoCurve curve = DynoCurve.FromRoadLoad(log, car, OnePull(log, car), Air);

        Assert.Equal(RoadLoad.TorqueConstant, curve.CrossoverRpm, 0);
    }

    [Fact]
    public void TheCurveIsSteppedInEngineSpeedRatherThanInSamples()
    {
        // A pull is evenly spaced in time and not in engine speed: it climbs
        // fastest where the engine is strongest. Drawn straight from the samples
        // the rungs would bunch at the top, which is where the engine is doing
        // least.
        VehicleSpec car = Car();
        LogDocument log = Drive(car);

        DynoCurve curve = DynoCurve.FromRoadLoad(log, car, OnePull(log, car), Air);

        DynoPoint[] drawn = [.. curve.Drawn];

        for (int i = 1; i < drawn.Length; i++)
        {
            Assert.Equal(curve.RpmStep, drawn[i].Rpm - drawn[i - 1].Rpm);
            Assert.Equal(0, drawn[i].Rpm % curve.RpmStep);
        }
    }

    // ----- the ends ---------------------------------------------------------------

    [Fact]
    public void TrimmingTheEndsEarnsItsPlace()
    {
        // The fit needs readings either side of the sample it describes, and at
        // the two ends of a pull there are none on one side. Half a window either
        // end is exactly that region.
        VehicleSpec car = Car();
        LogDocument log = Drive(car);
        DynoPull pull = OnePull(log, car);

        DynoCurve trimmed = DynoCurve.FromRoadLoad(log, car, pull, Air);
        DynoCurve whole = DynoCurve.FromRoadLoad(
            log, car, pull, Air, settings: new DynoSettings { TrimWindows = 0 });

        Assert.True(whole.FromRpm < trimmed.FromRpm);

        double Worst(DynoCurve c) =>
            c.Drawn.Max(p => Math.Abs(p.Horsepower - TruthHp(p.Rpm)) / TruthHp(p.Rpm));

        Assert.True(
            Worst(whole) > Worst(trimmed),
            $"untrimmed was out by {Worst(whole):P2}, trimmed by {Worst(trimmed):P2} — "
            + "the trim is not paying for what it costs");
    }

    [Fact]
    public void APullBarelyLongerThanTheWindowIsDrawnRoughRatherThanNotAtAll()
    {
        // Trimming half a window from each end of a run that is only a window and
        // a half long would leave almost nothing. Coming back rough is more use
        // than coming back empty.
        VehicleSpec car = Car();
        LogDocument log = Drive(car, gear: 2, toRpm: 5200);

        DynoCurve curve = DynoCurve.FromRoadLoad(log, car, OnePull(log, car), Air);

        Assert.False(curve.IsEmpty);
    }

    // ----- the correction ----------------------------------------------------------

    [Fact]
    public void ACorrectionScalesTheWholeCurveAndSaysWhatItWas()
    {
        VehicleSpec car = Car();
        LogDocument log = Drive(car);
        DynoPull pull = OnePull(log, car);

        DynoCurve plain = DynoCurve.FromRoadLoad(log, car, pull, Air);
        DynoCurve sae = DynoCurve.FromRoadLoad(log, car, pull, Air, PowerCorrection.SaeJ1349);

        Assert.Equal(1, plain.CorrectionFactor);
        Assert.Equal(AirDensity.SaeJ1349(Air), sae.CorrectionFactor, 9);

        Assert.Equal(
            plain.PeakPower.Horsepower * sae.CorrectionFactor,
            sae.PeakPower.Horsepower,
            6);
    }

    [Fact]
    public void CorrectingABoostedEngineIsDoneIfAskedAndSaidOutLoud()
    {
        // Not refused — it is the person's figure to state however they like. But
        // a turbo has already closed its wastegate to cancel the thin day the
        // correction is compensating for, so the number is flattered and that
        // goes on the sheet.
        VehicleSpec car = Car();
        LogDocument log = Drive(car);

        DynoCurve curve = DynoCurve.FromRoadLoad(
            log, car, OnePull(log, car), Air, PowerCorrection.SaeJ1349);

        Assert.Contains(curve.Cautions, c => c.Contains("boosted engine", StringComparison.Ordinal));
    }

    // ----- provenance ---------------------------------------------------------------

    [Fact]
    public void ACurveSaysWhetherTheRoadLoadWasMeasuredOrEntered()
    {
        VehicleSpec guessed = Car();
        LogDocument log = Drive(guessed);
        DynoPull pull = OnePull(log, guessed);

        DynoCurve fromGuess = DynoCurve.FromRoadLoad(log, guessed, pull, Air);

        Assert.Contains("as entered", fromGuess.Basis, StringComparison.Ordinal);
        Assert.Contains(fromGuess.Cautions, c => c.Contains("coastdown", StringComparison.Ordinal));

        VehicleSpec measured = guessed with { RoadLoadMeasured = true };
        DynoCurve fromMeasurement = DynoCurve.FromRoadLoad(log, measured, pull, Air);

        Assert.Contains("measured", fromMeasurement.Basis, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fromMeasurement.Cautions, c => c.Contains("coastdown", StringComparison.Ordinal));
    }

    [Fact]
    public void HowMuchTheRoadLoadGuessMattersIsSaidInProportion()
    {
        // Measured on a real pull: in a low gear the air takes about three per
        // cent of the effort, so being forty per cent wrong about the drag area
        // moves the answer by one. Calling it "the largest guess in this figure"
        // there is simply false, and points away from the mass and the gear,
        // which between them are worth an order of magnitude more.
        //
        // Up in top gear at three times the speed it is a different story, and
        // the same sentence has to change with it.
        VehicleSpec car = Car();

        LogDocument low = Drive(car, gear: 2);
        LogDocument high = Drive(car, gear: 6);

        DynoCurve slow = DynoCurve.FromRoadLoad(low, car, OnePull(low, car), Air);
        DynoCurve fast = DynoCurve.FromRoadLoad(high, car, OnePull(high, car), Air);

        Assert.InRange(slow.AeroShare, 0.005, 0.10);
        Assert.True(fast.AeroShare > 0.20, $"top gear only reached {fast.AeroShare:P0}");

        Assert.Contains(slow.Cautions, c => c.Contains("barely signify", StringComparison.Ordinal));
        Assert.DoesNotContain(slow.Cautions, c => c.Contains("worth having", StringComparison.Ordinal));

        Assert.Contains(fast.Cautions, c => c.Contains("worth having", StringComparison.Ordinal));
        Assert.DoesNotContain(fast.Cautions, c => c.Contains("barely signify", StringComparison.Ordinal));

        // And being wrong about it costs what the share says it costs.
        double slowShift = Shift(low, car, 2);
        double fastShift = Shift(high, car, 6);

        Assert.True(slowShift < 0.03, $"a low-gear pull moved {slowShift:P1} on a big CdA change");
        Assert.True(fastShift > slowShift * 3, $"top gear moved only {fastShift:P1}");
    }

    /// <summary>How much peak power moves when the drag area is taken 40% higher.</summary>
    private static double Shift(LogDocument log, VehicleSpec car, int gear)
    {
        DynoPull pull = OnePull(log, car);

        double a = DynoCurve.FromRoadLoad(log, car, pull, Air).PeakPower.Horsepower;
        double b = DynoCurve
            .FromRoadLoad(log, car with { DragAreaM2 = car.DragAreaM2 * 1.4 }, pull, Air)
            .PeakPower.Horsepower;

        return Math.Abs(b - a) / a;
    }

    [Fact]
    public void AFitFromACoastdownMarksTheVehicleAsMeasured()
    {
        VehicleSpec car = Car();

        var fit = new CoastdownFit
        {
            DragAreaM2 = 0.71,
            RollingResistance = 0.0131,
            GradePercent = 0.4,
            Agreement = 0.998,
            Samples = 400,
            BothDirections = true,
            InGear = false,
            Cautions = [],
        };

        VehicleSpec after = fit.ApplyTo(car);

        Assert.True(after.RoadLoadMeasured);
        Assert.False(car.RoadLoadMeasured);
    }

    [Fact]
    public void APullsOwnFaultsAreCarriedOntoTheSheet()
    {
        // The lift is a property of the pull, and it has to travel with the
        // figure rather than being left behind in the pull list. A curve read
        // without it says the engine has a hole in it.
        VehicleSpec car = Car();
        LogDocument log = Drive(car, gear: 4);

        // A pull with no road speed at all, so the gear went unchecked.
        var blind = new LogDocument
        {
            FilePath = log.FilePath,
            Time = log.Time,
            Channels = [.. log.Channels.Where(c => c.Name != "VSS")],
            FormatName = log.FormatName,
        };

        DynoPull pull = OnePull(blind, car);

        Assert.Contains(PullFault.GearNotChecked, pull.Faults);

        // Without a measured gear there is no effective mass, so nothing is
        // drawn — and it says which of the two it was short of.
        DynoCurve curve = DynoCurve.FromRoadLoad(blind, car, pull, Air);

        Assert.True(curve.IsEmpty);
        Assert.Contains("gear is not known", curve.Basis, StringComparison.Ordinal);
    }

    // ----- two curves ----------------------------------------------------------------

    [Fact]
    public void TwoRunsSubtractRungByRung()
    {
        // The question the application exists for: change something, run it
        // again, find out what moved. Curves compare far more honestly than logs
        // do, because a curve is already indexed by engine speed.
        VehicleSpec car = Car();

        LogDocument before = Drive(car);
        LogDocument after = Drive(car, watts: r => TruthWatts(r) * 1.08);

        DynoCurve was = DynoCurve.FromRoadLoad(before, car, OnePull(before, car), Air);
        DynoCurve now = DynoCurve.FromRoadLoad(after, car, OnePull(after, car), Air);

        DynoComparison change = DynoComparison.Between(now, was);

        Assert.True(change.Any);

        // Eight per cent more power, so eight per cent more at the peak.
        Assert.Equal(was.PeakPower.Horsepower * 0.08, change.PeakPowerChange, 0);

        foreach (DynoDelta d in change.Steps)
        {
            Assert.True(d.Horsepower > 0, $"lost power at {d.Rpm:N0} rpm");
        }

        Assert.Contains("hp at the peak", change.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepOnlyOneCurveHasIsNotAChangeOfNothing()
    {
        // Filling a missing rung with zero would put a hole in the middle of a
        // difference and call it agreement.
        VehicleSpec car = Car();

        LogDocument full = Drive(car);
        LogDocument shorter = Drive(car, toRpm: 5600);

        DynoCurve a = DynoCurve.FromRoadLoad(full, car, OnePull(full, car), Air);
        DynoCurve b = DynoCurve.FromRoadLoad(shorter, car, OnePull(shorter, car), Air);

        DynoComparison change = DynoComparison.Between(a, b);

        Assert.True(change.Steps.Count < a.Drawn.Count());
        Assert.All(change.Steps, d => Assert.True(d.Rpm <= b.ToRpm));
    }

    [Fact]
    public void TwoCurvesSteppedDifferentlyRefuseToSubtract()
    {
        VehicleSpec car = Car();
        LogDocument log = Drive(car);
        DynoPull pull = OnePull(log, car);

        DynoCurve fine = DynoCurve.FromRoadLoad(log, car, pull, Air);
        DynoCurve coarse = DynoCurve.FromRoadLoad(
            log, car, pull, Air, settings: new DynoSettings { RpmStep = 250 });

        DynoComparison change = DynoComparison.Between(fine, coarse);

        Assert.False(change.Any);
        Assert.Contains("stepped differently", change.Summary, StringComparison.Ordinal);
    }

    // ----- reading it -----------------------------------------------------------------

    [Fact]
    public void AValueCanBeReadBetweenTheRungs()
    {
        VehicleSpec car = Car();
        LogDocument log = Drive(car);

        DynoCurve curve = DynoCurve.FromRoadLoad(log, car, OnePull(log, car), Air);

        double at = curve.FromRpm + 250;

        // Within a per cent: the reading is a straight line between two rungs of
        // a curve that is not straight, so it cannot be exact and should not be
        // asserted to be.
        Assert.InRange(curve.HorsepowerAt(at) / TruthHp(at), 0.99, 1.01);

        // And outside the curve there is no answer rather than the nearest one.
        Assert.True(double.IsNaN(curve.HorsepowerAt(curve.ToRpm + 500)));
        Assert.True(double.IsNaN(curve.HorsepowerAt(curve.FromRpm - 500)));
    }

    [Fact]
    public void TheTwoEndpointsReadAsThemselves()
    {
        // The top of a pull is the one figure anybody reads off by name, and the
        // combined edge test this replaces handed back the value at the bottom
        // for it — silently, because both ends went through one branch that only
        // ever returned the first rung.
        VehicleSpec car = Car();
        LogDocument log = Drive(car);

        DynoCurve curve = DynoCurve.FromRoadLoad(log, car, OnePull(log, car), Air);

        DynoPoint[] drawn = [.. curve.Drawn];

        Assert.Equal(drawn[0].Horsepower, curve.HorsepowerAt(curve.FromRpm), 9);
        Assert.Equal(drawn[^1].Horsepower, curve.HorsepowerAt(curve.ToRpm), 9);

        Assert.Equal(drawn[0].PoundFeet, curve.PoundFeetAt(curve.FromRpm), 9);
        Assert.Equal(drawn[^1].PoundFeet, curve.PoundFeetAt(curve.ToRpm), 9);

        // The two ends are not the same figure, which is the whole complaint.
        Assert.NotEqual(curve.HorsepowerAt(curve.FromRpm), curve.HorsepowerAt(curve.ToRpm), 3);
    }

    [Fact]
    public void ARungOfNoPowerIsNotMistakenForTheCrossover()
    {
        // Power is clamped at nought where the car was slowing, so a pull with a
        // lift in it has rungs reading zero — and zero horsepower is also zero
        // pound-feet, so their difference is zero there too. The crossover exists
        // to confirm the units, and reporting the lift as the crossover turns a
        // driver's foot into an apparent arithmetic fault.
        var rpm = new List<double>();
        var hp = new List<double>();

        for (int r = 3000; r <= 7000; r += 100)
        {
            rpm.Add(r);

            // A hole at 3,500 where the throttle came off.
            hp.Add(r is >= 3400 and <= 3600 ? 0 : TruthHp(Math.Min(r, 6800)));
        }

        DynoCurve curve = DynoCurve.Build(
            [.. rpm], [.. hp], PowerMethodKind.RoadLoad, "with a lift in it",
            PowerCorrection.None, 1);

        Assert.Equal(RoadLoad.TorqueConstant, curve.CrossoverRpm, 0);
    }

    [Fact]
    public void StepsNothingLandedInAreDescribedAsThatRatherThanAsThin()
    {
        // With the default of one reading per step, nothing is ever dropped for
        // being thin — so every gap is a step nothing landed in, and the note
        // used to read "fewer than 1 readings" and recommend the wrong remedy.
        var rpm = new List<double>();
        var hp = new List<double>();

        // 100 rpm steps asked of a log that only visited every 300.
        for (int r = 3000; r <= 6000; r += 300)
        {
            rpm.Add(r);
            hp.Add(TruthHp(r));
        }

        DynoCurve curve = DynoCurve.Build(
            [.. rpm], [.. hp], PowerMethodKind.RoadLoad, "sparse",
            PowerCorrection.None, 1);

        Assert.Contains(curve.Cautions, c => c.Contains("no reading land in them", StringComparison.Ordinal));
        Assert.DoesNotContain(curve.Cautions, c => c.Contains("fewer than 1", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyCurveHasNoPeakToReport()
    {
        DynoCurve nothing = DynoCurve.Build(
            [], [], PowerMethodKind.RoadLoad, "nothing", PowerCorrection.None, 1);

        Assert.True(nothing.IsEmpty);
        Assert.True(nothing.PeakPower.IsEmpty);
        Assert.True(double.IsNaN(nothing.CrossoverRpm));
    }

    [Fact]
    public void APowerReadingIsNeededForEveryEngineSpeed()
    {
        Assert.Throws<ArgumentException>(() => DynoCurve.Build(
            [3000, 3100], [200], PowerMethodKind.RoadLoad, "x", PowerCorrection.None, 1));
    }
}
