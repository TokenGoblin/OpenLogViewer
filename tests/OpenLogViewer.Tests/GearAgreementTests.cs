using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The air route, and using it to work out a gear nothing measured.
///
/// The logs these were written against come from a car whose road speed sensor
/// has never been connected: the channel exists and reads a flat zero for
/// twenty-five minutes at a time. Road load cannot proceed without a gear and the
/// answer moves by a factor of eleven across the gearbox, so either something
/// recovers the gear or none of those recordings is usable.
///
/// The recovery works because the two routes are independent. Speed density has
/// never heard of the gearbox; road load cannot proceed without it. Only one gear
/// reconciles them.
/// </summary>
public class GearAgreementTests
{
    private static readonly Ambient Air = new(98.6, 25, 40);

    private static VehicleSpec Car() => new()
    {
        KerbMassKg = 1450,
        OccupantMassKg = 85,
        GearRatios = [3.72, 2.40, 1.77, 1.26, 1.00],
        FinalDrive = 3.25,
        Tyre = new Tyre(205, 60, 15),
        DragAreaM2 = 0.72,
        RollingResistance = 0.013,
        WheelInertiaKgM2 = 4.5,
        EngineInertiaKgM2 = 0.25,
        DrivetrainLossPercent = 15,
        Boosted = true,
    };

    private static EngineSpec Engine() => new()
    {
        Litres = 3.43,
        Cylinders = 6,
        Fuel = Fuel.Petrol,
        Bsfc = 0.50,
        VolumetricEfficiency = 95,
    };

    private const double Afr = 12.0;
    private const double ChargeCelsius = 40;

    /// <summary>Crank horsepower the engine is to make at a given engine speed.</summary>
    private static double CrankHp(double rpm)
    {
        double x = (rpm - 2500) / 3500;

        return 180 + (320 * x) - (140 * x * x);
    }

    /// <summary>
    /// A recording of a car that really made that power.
    ///
    /// Driven forward through the road-load equations so the acceleration is
    /// right, and with the manifold pressure worked backwards from the same
    /// figure so the air agrees with it. Both routes are then describing one
    /// engine rather than two, which is the only way the agreement between them
    /// means anything.
    /// </summary>
    /// <param name="richOpening">
    /// Whether the mixture dips rich for the first moment of the pull, the way an
    /// accelerator pump makes it. Off by default so most of these tests describe
    /// one thing at a time.
    /// </param>
    private static LogDocument Drive(
        VehicleSpec car, EngineSpec engine, int gear, double hz = 20, bool richOpening = false)
    {
        double rho = AirDensity.KgPerCubicMetre(Air);
        double kelvin = ChargeCelsius + 273.15;
        const double dt = 0.001;

        List<double> times = [], rpms = [], maps = [], mats = [], afrs = [], tps = [];

        void Write(double t, double rpm, double throttle)
        {
            // The pump shot: a great deal of extra fuel for a moment, which makes
            // no more power and simply goes out of the pipe. This arithmetic
            // cannot know that — it divides the air by the mixture — so it reads
            // the enrichment as power, and it does so exactly where a turbo has
            // least boost and the reading matters least.
            double mixture = richOpening && rpm < 3200 ? 9.5 : Afr;

            // The manifold pressure that would produce this crank power, from the
            // same arithmetic FromSpeedDensity uses, run backwards.
            double kpa = CrankHp(rpm) * Afr * engine.Bsfc * 574 * kelvin
                         / (132.27735731092654 * engine.Litres
                            * (engine.VolumetricEfficiency / 100) * rpm);

            times.Add(t);
            rpms.Add(rpm);
            maps.Add(kpa);
            mats.Add(ChargeCelsius);
            afrs.Add(mixture);
            tps.Add(throttle);
        }

        for (double t = 0; t < 3; t += 1 / hz) Write(t, 2500, 18);

        double now = 3;
        double speed = car.SpeedMsFromRpm(2500, gear);
        double clock = 0, next = 0;

        while (true)
        {
            double rpm = car.RpmFromSpeedMs(speed, gear);
            if (rpm > 6000) break;

            if (clock >= next - 1e-12)
            {
                Write(now + clock, rpm, 98);
                next += 1 / hz;
            }

            // Wheel power is crank power less the driveline's share.
            double wheelWatts = CrankHp(rpm) * RoadLoad.WattsPerHorsepower
                                * (1 - (car.DrivetrainLossPercent / 100));

            speed += RoadLoad.AccelerationMs2(car, gear, speed, wheelWatts, rho) * dt;
            clock += dt;
        }

        now += clock;
        for (double t = 0; t < 2; t += 1 / hz) Write(now + t, 6000 + (2 * t), 98);

        return new LogDocument
        {
            FilePath = "drive",
            Time = new LogChannel("Time", "s", 3, [.. times], preservePrecision: true),
            Channels =
            [
                new LogChannel("RPM", "rpm", 0, [.. rpms]),
                new LogChannel("MAP", "kPa", 1, [.. maps]),
                new LogChannel("MAT", "C", 1, [.. mats]),
                new LogChannel("AFR", "afr", 2, [.. afrs]),
                new LogChannel("TPS", "%", 1, [.. tps]),
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

    // ----- the air route on its own ------------------------------------------------

    [Fact]
    public void TheAirRouteGivesBackThePowerTheManifoldWasBuiltFor()
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);

        DynoCurve curve = DynoCurve.FromSpeedDensity(log, engine, OnePull(log, car), Air);

        Assert.False(curve.IsEmpty);
        Assert.Equal(PowerMethodKind.SpeedDensity, curve.Method);
        Assert.Equal(PowerReference.Crank, curve.Reference);

        double worst = curve.Drawn.Max(p => Math.Abs(p.Horsepower - CrankHp(p.Rpm)) / CrankHp(p.Rpm));

        // Two per cent rather than one: each rung averages a power curve that is
        // genuinely curved across a hundred rpm, so a little is lost to the
        // binning that was never lost by the arithmetic.
        Assert.True(worst < 0.02, $"worst rung was out by {worst:P2}");
    }

    [Fact]
    public void TheAirRouteKeepsBothEndsOfThePull()
    {
        // Road load throws away half a window at each end because its derivative
        // has support on one side only there. Nothing in this route is a
        // derivative — every reading comes from its own sample and no other — so
        // trimming it would discard good data, and on a turbocharged engine it
        // discards the top of the pull, which is where the boost is.
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);
        DynoPull pull = OnePull(log, car);

        DynoCurve fromAir = DynoCurve.FromSpeedDensity(log, engine, pull, Air);
        DynoCurve fromRoad = DynoCurve.FromRoadLoad(log, car, pull with
        {
            Gearing = pull.Gearing with { Gear = 3 },
            Speed = SpeedSource.EngineSpeedAndGear,
        }, Air);

        Assert.True(fromAir.ToRpm > fromRoad.ToRpm);
        Assert.True(fromAir.FromRpm < fromRoad.FromRpm);
    }

    [Fact]
    public void TheAirRouteSaysWhatItCouldNotFind()
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);
        DynoPull pull = OnePull(log, car);

        var withoutManifold = new LogDocument
        {
            FilePath = log.FilePath,
            Time = log.Time,
            Channels = [.. log.Channels.Where(c => c.Name != "MAP")],
            FormatName = log.FormatName,
        };

        DynoCurve nothing = DynoCurve.FromSpeedDensity(withoutManifold, engine, pull, Air);

        Assert.True(nothing.IsEmpty);
        Assert.Contains("manifold pressure", nothing.Basis, StringComparison.Ordinal);
    }

    // ----- a controller's VE table is not a volumetric efficiency ------------------

    [Fact]
    public void AVeTableThatReachesPastAHundredIsRefusedAsOne()
    {
        // Real, and it cost a lot. A MegaSquirt computes its pulse width from a
        // table it calls VE, but the scale is set by the injector calibration
        // rather than by physics, and on a turbocharged log it reaches 136.5%.
        // A cylinder cannot fill to 136% of itself. Believed, it inflates the air
        // by more than a third and every horsepower with it — which is how a 3.4
        // litre six came to be credited with 658.
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);
        DynoPull pull = OnePull(log, car);

        var withTable = new LogDocument
        {
            FilePath = log.FilePath,
            Time = log.Time,
            Channels =
            [
                .. log.Channels,
                new LogChannel("VE1", "%", 1,
                    [.. Enumerable.Range(0, log.SampleCount).Select(i => 60 + (i * 80.0 / log.SampleCount))]),
            ],
            FormatName = log.FormatName,
        };

        DynoCurve curve = DynoCurve.FromSpeedDensity(withTable, engine, pull, Air);

        // The assumed figure was used, so the answer is the one it was built for.
        double worst = curve.Drawn.Max(p => Math.Abs(p.Horsepower - CrankHp(p.Rpm)) / CrankHp(p.Rpm));

        Assert.True(worst < 0.02, $"the table was believed after all — out by {worst:P2}");
        Assert.Contains(curve.Cautions, c => c.Contains("fuelling table", StringComparison.Ordinal));
    }

    [Fact]
    public void AFillingChannelThatStaysPossibleIsUsedAndSaidToHaveBeen()
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);

        var withVe = new LogDocument
        {
            FilePath = log.FilePath,
            Time = log.Time,
            Channels =
            [
                .. log.Channels,
                new LogChannel("VE1", "%", 1,
                    [.. Enumerable.Repeat(95.0, log.SampleCount)]),
            ],
            FormatName = log.FormatName,
        };

        DynoCurve curve = DynoCurve.FromSpeedDensity(withVe, engine, OnePull(withVe, car), Air);

        Assert.Contains("filling from VE1", curve.Basis, StringComparison.Ordinal);
        Assert.Contains(curve.Cautions, c => c.Contains("question about the tune", StringComparison.Ordinal));
    }

    // ----- crank and wheels are different quantities --------------------------------

    [Fact]
    public void TwoCurvesQuotedAtDifferentEndsOfTheDrivelineRefuseToSubtract()
    {
        // Fifteen per cent of the answer, and it would land silently on whatever
        // was being tested.
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);
        DynoPull pull = OnePull(log, car);

        DynoCurve wheels = DynoCurve.FromRoadLoad(log, car, pull with
        {
            Gearing = pull.Gearing with { Gear = 3 },
            Speed = SpeedSource.EngineSpeedAndGear,
        }, Air);

        DynoCurve crank = DynoCurve.FromSpeedDensity(log, engine, pull, Air);

        DynoComparison refused = DynoComparison.Between(wheels, crank);

        Assert.False(refused.Any);
        Assert.Contains("crank", refused.Summary, StringComparison.Ordinal);
        Assert.Contains("wheels", refused.Summary, StringComparison.Ordinal);

        // Restated, they subtract.
        DynoComparison allowed = DynoComparison.Between(wheels.At(PowerReference.Crank, car), crank);

        Assert.True(allowed.Any);
    }

    [Fact]
    public void RestatingACurveAtTheOtherEndIsReversible()
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);

        DynoCurve wheels = DynoCurve.FromRoadLoad(log, car, OnePull(log, car) with
        {
            Gearing = new GearFit(3, double.NaN, double.NaN, double.NaN, ""),
            Speed = SpeedSource.EngineSpeedAndGear,
        }, Air);

        DynoCurve there = wheels.At(PowerReference.Crank, car);
        DynoCurve back = there.At(PowerReference.Wheels, car);

        Assert.Equal(PowerReference.Crank, there.Reference);
        Assert.True(there.PeakPower.Horsepower > wheels.PeakPower.Horsepower);
        Assert.Equal(wheels.PeakPower.Horsepower, back.PeakPower.Horsepower, 6);
    }

    // ----- the gear ------------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheGearIsRecoveredFromTheTwoRoutesAgreeing(int gear)
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear);

        DynoPull pull = OnePull(log, car);

        // Nothing measured it: the road speed channel does not exist here, which
        // is the situation on every log this was written for.
        Assert.Contains(PullFault.GearNotChecked, pull.Faults);

        GearVerdict verdict = GearAgreement.Resolve(log, car, engine, pull, Air);

        Assert.True(verdict.Resolved, verdict.Summary);
        Assert.Equal(gear, verdict.Gear);

        // And it is not a close-run thing: the neighbours are the better part of
        // twice as far out, which is what makes the answer safe despite resting
        // on an assumed volumetric efficiency and an assumed fuel consumption.
        GearCandidate[] ranked = [.. verdict.Candidates.OrderBy(c => c.Off)];

        Assert.True(ranked[1].Off > ranked[0].Off * 2, $"the runner-up was {ranked[1].OffPercent:P0} out");
    }

    [Fact]
    public void TheRecoveredGearIsMarkedAsWorkedOutRatherThanMeasured()
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);

        DynoPull pull = OnePull(log, car);
        GearVerdict verdict = GearAgreement.Resolve(log, car, engine, pull, Air);

        DynoPull settled = GearAgreement.Apply(pull, verdict);

        Assert.Equal(3, settled.Gearing.Gear);
        Assert.Contains(PullFault.GearFromAgreement, settled.Faults);
        Assert.DoesNotContain(PullFault.GearNotChecked, settled.Faults);

        // Which is the point: it now draws, and it says how it knows.
        DynoCurve curve = DynoCurve.FromRoadLoad(log, car, settled, Air);

        Assert.False(curve.IsEmpty);
        Assert.Contains(curve.Cautions, c => c.Contains("worked out rather than measured", StringComparison.Ordinal));
    }

    [Fact]
    public void ACloseRatioGearboxNeedsTheTwoRoutesToAgreeMoreTightly()
    {
        // The gearbox sets the resolution, and it is not the same at both ends of
        // one. On a wide box the neighbouring gear implies well over twice the
        // power and a rough agreement still picks the right one; on a close box it
        // implies a tenth more, and the same rough agreement picks nothing.
        //
        // So the same disagreement — here a fuel consumption a fifth away from the
        // one the log was built with — settles it on one gearbox and settles
        // nothing on the other. A single threshold could not do that.
        VehicleSpec wide = Car();
        VehicleSpec close = Car() with { GearRatios = [2.00, 1.90, 1.80, 1.70] };

        EngineSpec engine = Engine();
        EngineSpec wrong = engine with { Bsfc = engine.Bsfc * 1.2 };

        LogDocument onWide = Drive(wide, engine, gear: 2);
        LogDocument onClose = Drive(close, engine, gear: 2);

        Assert.True(
            GearAgreement.Resolve(onWide, wide, wrong, OnePull(onWide, wide), Air).Resolved,
            "a wide gearbox should still be callable with the fuel consumption a fifth out");

        GearVerdict muddle =
            GearAgreement.Resolve(onClose, close, wrong, OnePull(onClose, close), Air);

        Assert.False(muddle.Resolved);
        Assert.Contains("only puts", muddle.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBottomOfATurbochargedPullIsLeftOutOfTheComparison()
    {
        // Where the two routes are held against each other decides whether the
        // answer survives an ordinary pull.
        //
        // At the bottom there is little boost, little power, and an accelerator
        // pump dumping fuel that makes none of it — which this arithmetic reads
        // as power, because all it can do is divide the air by the mixture.
        // Averaging that in buries the part of the pull where both routes are
        // actually saying something, and it is enough on its own to lose the gear.
        VehicleSpec car = Car();
        EngineSpec engine = Engine();

        LogDocument clean = Drive(car, engine, gear: 3);
        LogDocument pumped = Drive(car, engine, gear: 3, richOpening: true);

        Assert.Equal(3, GearAgreement.Resolve(clean, car, engine, OnePull(clean, car), Air).Gear);

        GearVerdict despiteThePump =
            GearAgreement.Resolve(pumped, car, engine, OnePull(pumped, car), Air);

        Assert.True(despiteThePump.Resolved, despiteThePump.Summary);
        Assert.Equal(3, despiteThePump.Gear);
    }

    [Fact]
    public void AWrongDisplacementIsIndistinguishableFromAWrongGear()
    {
        // A limitation to know rather than a defect to fix, and it is written down
        // here because the arithmetic cannot write it down anywhere else.
        //
        // The displacement and the gear do the same thing to this comparison: both
        // scale one route against the other. So telling it a 3.4 litre six is a
        // 1.6 does not produce a refusal — it produces a confident answer that is
        // the wrong gear. What separates the two in practice is not the maths: a
        // displacement is something the person knows, and the gear is the thing
        // they have come here without.
        VehicleSpec car = Car();
        LogDocument log = Drive(car, Engine(), gear: 3);

        GearVerdict honest = GearAgreement.Resolve(log, car, Engine(), OnePull(log, car), Air);
        GearVerdict lied = GearAgreement.Resolve(
            log, car, Engine() with { Litres = 1.6 }, OnePull(log, car), Air);

        Assert.Equal(3, honest.Gear);

        Assert.True(lied.Resolved, "a wrong displacement does not announce itself");
        Assert.NotEqual(3, lied.Gear);
    }

    [Fact]
    public void APullTooThinToDrawIsTooThinToSettleAGearFrom()
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);

        DynoPull pull = OnePull(log, car) with { Faults = [PullFault.TooNarrow] };

        GearVerdict verdict = GearAgreement.Resolve(log, car, engine, pull, Air);

        Assert.False(verdict.Resolved);
        Assert.Contains("not enough to settle a gear", verdict.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedVerdictChangesNothingAboutThePull()
    {
        VehicleSpec car = Car();
        EngineSpec engine = Engine();
        LogDocument log = Drive(car, engine, gear: 3);

        DynoPull thin = OnePull(log, car) with { Faults = [PullFault.TooNarrow] };

        GearVerdict none = GearAgreement.Resolve(log, car, engine, thin, Air);

        Assert.False(none.Resolved);
        Assert.Same(thin, GearAgreement.Apply(thin, none));
    }
}
