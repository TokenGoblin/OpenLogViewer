using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Runner and primary lengths, port sizes and plenum volume.
///
/// The arithmetic here is derived rather than fitted, which is worth something
/// only if it is actually checked against the world — so the centrepiece of this
/// file is <see cref="ExhaustLengthAgreesWithBell"/>, where the wave equation is
/// held against A. Graham Bell's published empirical formula across the range of
/// cams and engine speeds anybody would use. They agree within a few per cent
/// without sharing a single constant.
///
/// Everything else is either a textbook value (the speed of sound), an identity
/// that has to hold whichever way it is read (length to rpm and back), or a
/// direction the answer must move in (a smaller port must raise velocity). Very
/// few assertions here are against numbers typed by hand, deliberately: a
/// hand-rounded expectation is how a later arithmetic change gets absorbed by a
/// tolerance instead of being caught by it.
/// </summary>
public class ManifoldTuningTests
{
    // ----- the gas ---------------------------------------------------------------

    /// <summary>
    /// √(γRT) at 20 °C is the number in the front of every acoustics text: 343.2
    /// metres per second. If this is wrong nothing downstream can be right, since
    /// every length here is a speed multiplied by a time.
    /// </summary>
    [Fact]
    public void SpeedOfSoundInAirMatchesTheTextbook()
    {
        Assert.Equal(343.2, ManifoldTuning.SpeedOfSoundAir(20), 1);

        // And 0 °C is the other value everyone knows.
        Assert.Equal(331.3, ManifoldTuning.SpeedOfSoundAir(0), 1);
    }

    /// <summary>
    /// Exhaust is hot, so sound runs through it far faster — which is the whole
    /// reason a primary is twice the length of a runner on the same engine.
    /// </summary>
    [Fact]
    public void SoundRunsFasterInExhaustThanInIntakeCharge()
    {
        double intake = ManifoldTuning.SpeedOfSoundAir(30);
        double exhaust = ManifoldTuning.SpeedOfSoundExhaust(600);

        Assert.True(exhaust > intake * 1.5, $"intake {intake:N0}, exhaust {exhaust:N0}");
    }

    /// <summary>Temperature enters as a square root, so it is a weak lever.</summary>
    [Fact]
    public void SpeedOfSoundFollowsTheSquareRootOfAbsoluteTemperature()
    {
        // Doubling absolute temperature must multiply the speed by exactly √2.
        double cold = ManifoldTuning.SpeedOfSoundAir(-123.15);   // 150 K
        double hot = ManifoldTuning.SpeedOfSoundAir(26.85);      // 300 K

        Assert.Equal(Math.Sqrt(2), hot / cold, 3);
    }

    // ----- the wave equation -----------------------------------------------------

    /// <summary>
    /// The relation worked by hand. At 345 m/s, a 240° window and 6,000 rpm, one
    /// round trip wants 345 × 240 / (12 × 1 × 6000) = 1.15 m, and three round
    /// trips want a third of it.
    /// </summary>
    [Fact]
    public void TunedLengthIsSpeedTimesWindowOverTwelveTimesOrderTimesRpm()
    {
        Assert.Equal(1_150, ManifoldTuning.TunedLengthMm(345, 240, 1, 6_000), 0);
        Assert.Equal(383.33, ManifoldTuning.TunedLengthMm(345, 240, 3, 6_000), 2);
    }

    /// <summary>
    /// Length and engine speed are the same statement rearranged, so going out and
    /// back has to land exactly where it started — at every order.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void LengthAndRpmAreTheSameStatementBothWays(int order)
    {
        double a = ManifoldTuning.SpeedOfSoundAir(35);
        const double window = 235;
        const double rpm = 6_800;

        double length = ManifoldTuning.TunedLengthMm(a, window, order, rpm);
        double back = ManifoldTuning.TunedRpm(a, window, order, length);

        Assert.Equal(rpm, back, 6);
    }

    /// <summary>
    /// A higher order is a shorter pipe, in exact inverse proportion — order two
    /// is half of order one, not merely less than it.
    /// </summary>
    [Fact]
    public void EachOrderIsExactlyTheReciprocalOfTheLast()
    {
        double first = ManifoldTuning.TunedLengthMm(340, 240, 1, 7_000);

        for (int order = 2; order <= 6; order++)
            Assert.Equal(first / order, ManifoldTuning.TunedLengthMm(340, 240, order, 7_000), 8);
    }

    /// <summary>Tuning lower down means a longer pipe. The direction is the design.</summary>
    [Fact]
    public void TuningLowerWantsALongerPipe()
    {
        double low = ManifoldTuning.TunedLengthMm(345, 240, 3, 3_500);
        double high = ManifoldTuning.TunedLengthMm(345, 240, 3, 8_000);

        Assert.True(low > high, $"3,500 rpm gave {low:N0} mm, 8,000 rpm gave {high:N0} mm");
    }

    /// <summary>
    /// The exhaust window runs from the valve cracking open to overlap top dead
    /// centre: 90 + ED/2. A 280° cam opens 50° before bottom dead centre, so the
    /// wave has 230° to make its trip.
    /// </summary>
    [Fact]
    public void ExhaustWindowRunsFromValveOpeningToOverlap()
    {
        Assert.Equal(230, ManifoldTuning.ExhaustWaveWindowDeg(280), 6);
        Assert.Equal(215, ManifoldTuning.ExhaustWaveWindowDeg(250), 6);

        // A valve that opened after bottom dead centre would have no window at all.
        Assert.True(double.IsNaN(ManifoldTuning.ExhaustWaveWindowDeg(170)));
    }

    // ----- the cross-check that matters ------------------------------------------

    /// <summary>
    /// The wave equation against Bell's empirical primary length, across the whole
    /// range of cams and engine speeds anyone would build.
    ///
    /// This is the test the rest of the file exists to support. Bell's L =
    /// 850·ED/rpm − 3 is a fit to engines that were built, dynoed and sold; the
    /// calculation here is √(γRT) and a round trip, and shares no constant with
    /// it. They land within six per cent of each other everywhere in the range,
    /// which is not something two unrelated formulas do by accident.
    ///
    /// Six hundred degrees is the mean gas temperature along the primary, and it
    /// is not a fudge factor chosen to make this pass: solving Bell backwards for
    /// the temperature it implies gives 536 to 676 °C over these same six cases,
    /// and 600 sits in the middle of that.
    /// </summary>
    [Theory]
    [InlineData(280, 6_500)]
    [InlineData(270, 7_000)]
    [InlineData(300, 6_000)]
    [InlineData(250, 5_500)]
    [InlineData(290, 7_500)]
    [InlineData(260, 8_000)]
    public void ExhaustLengthAgreesWithBell(double duration, double rpm)
    {
        double a = ManifoldTuning.SpeedOfSoundExhaust(600);
        double window = ManifoldTuning.ExhaustWaveWindowDeg(duration);

        double derived = ManifoldTuning.TunedLengthMm(a, window, 2, rpm);
        double bell = ManifoldTuning.BellPrimaryLengthInches(duration, rpm) * 25.4;

        double error = Math.Abs(derived - bell) / bell;

        Assert.True(
            error < 0.07,
            $"{duration}° at {rpm} rpm: derived {derived:N0} mm against Bell's {bell:N0} mm "
            + $"— {error:P1} apart");
    }

    /// <summary>
    /// The agreement above is not an artefact of the temperature chosen. Solved
    /// backwards, Bell's constant implies a mean primary gas temperature in the
    /// same band a thermocouple down a header actually reads.
    /// </summary>
    [Theory]
    [InlineData(280, 6_500)]
    [InlineData(270, 7_000)]
    [InlineData(300, 6_000)]
    [InlineData(250, 5_500)]
    public void BellsConstantImpliesAPlausibleGasTemperature(double duration, double rpm)
    {
        double window = ManifoldTuning.ExhaustWaveWindowDeg(duration);
        double bellMm = ManifoldTuning.BellPrimaryLengthInches(duration, rpm) * 25.4;

        // Invert L = a·θ/(12·K·rpm) for the speed of sound, then √(γRT) for T.
        double speed = 12 * 2 * rpm * (bellMm / 1_000) / window;
        double kelvin = speed * speed / (ManifoldTuning.GammaExhaust * ManifoldTuning.GasConstantExhaust);

        Assert.InRange(kelvin - 273.15, 500, 700);
    }

    // ----- port velocity ---------------------------------------------------------

    /// <summary>
    /// Velocity by hand. A 500 cc cylinder filling completely at 7,000 rpm through
    /// a 240° window moves 6 × 7000 × 0.0005 = 21 m³ of crank-degree-seconds, and
    /// through 9.72 cm² that is about 90 m/s.
    /// </summary>
    [Fact]
    public void PortVelocityIsTheChargeDividedByTheWindowAndTheArea()
    {
        double velocity = ManifoldTuning.PortVelocity(500, 100, 7_000, 240, 972.2);

        Assert.Equal(90, velocity, 0);
    }

    /// <summary>
    /// Sizing a port for a velocity and then measuring the velocity of that port
    /// has to return the number asked for. The two are one equation.
    /// </summary>
    [Theory]
    [InlineData(85)]
    [InlineData(95)]
    [InlineData(105)]
    [InlineData(130)]
    public void SizingForAVelocityThenMeasuringItReturnsIt(double target)
    {
        double area = ManifoldTuning.AreaForVelocity(500, 100, 7_000, 240, target);
        double back = ManifoldTuning.PortVelocity(500, 100, 7_000, 240, area);

        Assert.Equal(target, back, 8);
    }

    /// <summary>
    /// A 2.0 litre four at 7,000 rpm wants a runner in the middle thirties of
    /// millimetres. This is the one place a real engine is asserted against,
    /// because it is the number a builder would recognise instantly as right or
    /// mad — production 2.0 litre runners are 33 to 38 mm.
    /// </summary>
    [Fact]
    public void ATwoLitreFourGetsARunnerARealEngineWouldRecognise()
    {
        double area = ManifoldTuning.AreaForVelocity(500, 100, 7_000, 240, 95);
        double diameter = ManifoldTuning.DiameterMm(area);

        Assert.InRange(diameter, 33, 38);
    }

    /// <summary>Area and diameter are each other's inverse, at any size.</summary>
    [Theory]
    [InlineData(28)]
    [InlineData(35.5)]
    [InlineData(48)]
    public void AreaAndDiameterRoundTrip(double diameter)
    {
        Assert.Equal(diameter, ManifoldTuning.DiameterMm(ManifoldTuning.AreaMm2(diameter)), 9);
    }

    /// <summary>A smaller port must move the gas faster. Nothing else is possible.</summary>
    [Fact]
    public void ASmallerPortRaisesVelocity()
    {
        double small = ManifoldTuning.PortVelocity(500, 100, 7_000, 240, ManifoldTuning.AreaMm2(32));
        double large = ManifoldTuning.PortVelocity(500, 100, 7_000, 240, ManifoldTuning.AreaMm2(42));

        Assert.True(small > large, $"32 mm gave {small:N0} m/s, 42 mm gave {large:N0} m/s");
    }

    // ----- plenum and Helmholtz --------------------------------------------------

    /// <summary>
    /// Engelman's effective volume is the mean of the cylinder through its stroke.
    /// At 10:1 a 500 cc cylinder holds 55.6 cc at the top and 555.6 at the bottom,
    /// and the mean of those is 305.6.
    /// </summary>
    [Fact]
    public void EffectiveCylinderVolumeIsTheMeanThroughTheStroke()
    {
        double effective = ManifoldTuning.EffectiveCylinderVolumeCc(500, 10);

        double top = 500 / (10 - 1.0);
        double bottom = top + 500;

        Assert.Equal((top + bottom) / 2, effective, 9);
        Assert.Equal(305.56, effective, 2);
    }

    /// <summary>
    /// More compression is less gas to spring against, so the system resonates
    /// higher.
    /// </summary>
    [Fact]
    public void MoreCompressionRaisesTheResonance()
    {
        double low = ManifoldTuning.EffectiveCylinderVolumeCc(500, 8);
        double high = ManifoldTuning.EffectiveCylinderVolumeCc(500, 14);

        Assert.True(high < low, $"8:1 gave {low:N0} cc, 14:1 gave {high:N0} cc");
    }

    /// <summary>
    /// The Helmholtz frequency moves the way the physical picture says: a longer
    /// neck or a bigger volume is a lower note, a fatter neck a higher one.
    /// </summary>
    [Fact]
    public void HelmholtzMovesTheWayTheResonatorSaysItShould()
    {
        double baseline = ManifoldTuning.HelmholtzHz(345, 1_000, 400, 300);

        Assert.True(ManifoldTuning.HelmholtzHz(345, 1_000, 800, 300) < baseline, "longer neck");
        Assert.True(ManifoldTuning.HelmholtzHz(345, 1_000, 400, 600) < baseline, "bigger volume");
        Assert.True(ManifoldTuning.HelmholtzHz(345, 2_000, 400, 300) > baseline, "fatter neck");

        // Halving both area and length together leaves it exactly where it was.
        Assert.Equal(baseline, ManifoldTuning.HelmholtzHz(345, 500, 200, 300), 9);
    }

    /// <summary>
    /// A design tuned by the wave equation lands in the band the documentation
    /// claims for the Helmholtz ratio. This is what makes the ratio usable as a
    /// cross-check rather than a number with no scale.
    /// </summary>
    [Theory]
    [InlineData(4_500, 7_000)]
    [InlineData(3_500, 6_500)]
    [InlineData(6_000, 9_000)]
    public void AWaveTunedDesignLandsInTheClaimedHelmholtzBand(double torqueRpm, double powerRpm)
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            PeakTorqueRpm = torqueRpm,
            PeakPowerRpm = powerRpm,
        });

        Assert.InRange(plan.Intake.HelmholtzRatio, 2.0, 5.5);
    }

    // ----- exhaust gas volume ----------------------------------------------------

    /// <summary>
    /// Hot gas takes up more room, which is why a primary is fatter than a runner.
    /// A charge that went in at 30 °C leaves at 600 °C and occupies roughly three
    /// times the volume — the ratio of the absolute temperatures, near enough.
    /// </summary>
    [Fact]
    public void ExhaustGasExpandsRoughlyWithAbsoluteTemperature()
    {
        double volume = ManifoldTuning.ExhaustVolumePerCycleCc(
            500, 100, TuningMath.AtmosphericKpa, 30, 600, TuningMath.AtmosphericKpa, 12.5);

        double expansion = (600 + 273.15) / (30 + 273.15);

        // The fuel adds about a twelfth of the air mass on top of the expansion.
        Assert.InRange(volume, 500 * expansion, 500 * expansion * 1.15);
    }

    /// <summary>Back pressure squeezes the gas back down, so a turbine's manifold flows less volume.</summary>
    [Fact]
    public void BackPressureShrinksTheGasVolume()
    {
        double open = ManifoldTuning.ExhaustVolumePerCycleCc(
            500, 100, TuningMath.AtmosphericKpa, 30, 600, TuningMath.AtmosphericKpa, 12.5);

        double behindTurbine = ManifoldTuning.ExhaustVolumePerCycleCc(
            500, 100, TuningMath.AtmosphericKpa, 30, 600, TuningMath.AtmosphericKpa * 2, 12.5);

        Assert.Equal(open / 2, behindTurbine, 6);
    }

    /// <summary>
    /// The exhaust velocity target is calibrated against headers that exist, so
    /// the calibration is what has to be defended: asked to size a primary for
    /// these engines, the calculator must land within a quarter inch of what they
    /// actually run.
    ///
    /// A quarter inch because that is the granularity headers are sold in — being
    /// closer than the next tube size up would be a false claim of precision, and
    /// being further out would put the answer on the wrong tube.
    /// </summary>
    [Theory]
    [InlineData(2.0, 4, 7_000, 41.3)]   // 2.0 four on 1.625 in
    [InlineData(5.7, 8, 6_000, 44.5)]   // 5.7 V8 on 1.75 in
    [InlineData(1.6, 4, 8_000, 38.1)]   // 1.6 four on 1.5 in
    [InlineData(6.2, 8, 6_500, 47.6)]   // 6.2 V8 on 1.875 in
    public void PrimarySizingLandsOnTheTubeSuchEnginesActuallyRun(
        double litres, int cylinders, double powerRpm, double realPrimaryMm)
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Litres = litres,
            Cylinders = cylinders,
            Goal = ManifoldGoal.Balanced,
            PeakPowerRpm = powerRpm,
            PeakTorqueRpm = powerRpm * 0.65,
        });

        Assert.InRange(plan.Exhaust.PrimaryDiameterMm, realPrimaryMm - 6.35, realPrimaryMm + 6.35);
    }

    /// <summary>
    /// And the velocities those real headers imply all fall inside the band the
    /// verdict calls ordinary. If this drifts, the bands and the targets have come
    /// apart from each other.
    /// </summary>
    [Theory]
    [InlineData(168)]
    [InlineData(190)]
    [InlineData(211)]
    public void RealHeaderVelocitiesReadAsOrdinary(double velocity) =>
        Assert.Contains("usual for a real header", ManifoldTuning.ExhaustVelocityVerdict(velocity), StringComparison.Ordinal);

    /// <summary>The exhaust verdict must not simply repeat the intake one — the bands differ.</summary>
    [Fact]
    public void IntakeAndExhaustVerdictsUseDifferentBands()
    {
        // 190 m/s is ordinary in a primary and choked in a runner.
        Assert.Contains("usual for a real header", ManifoldTuning.ExhaustVelocityVerdict(190), StringComparison.Ordinal);
        Assert.Contains("choked", ManifoldTuning.VelocityVerdict(190), StringComparison.Ordinal);
    }

    /// <summary>A collector is bigger than one primary and far smaller than four.</summary>
    [Fact]
    public void CollectorSitsBetweenOnePrimaryAndAllOfThem()
    {
        double collector = ManifoldTuning.CollectorDiameterMm(44, 4);

        Assert.True(collector > 44, $"collector {collector:N1} mm is not larger than a 44 mm primary");
        Assert.True(collector < 88, $"collector {collector:N1} mm is as large as all four together");

        // Twice the area is √2 times the diameter.
        Assert.Equal(44 * Math.Sqrt(2), collector, 6);
    }

    // ----- end correction --------------------------------------------------------

    /// <summary>
    /// A pipe behaves longer than it measures, so the length to build is shorter
    /// than the length calculated — never the other way round.
    /// </summary>
    [Fact]
    public void ThePipeToBuildIsShorterThanTheAcousticLength()
    {
        IReadOnlyList<TuningOrder> orders =
            ManifoldTuning.Orders(345, 240, 6_000, 40, ManifoldTuning.MaxPracticalRunnerMm);

        Assert.All(orders, o => Assert.True(
            o.LengthMm < o.EffectiveLengthMm,
            $"order {o.Order}: built {o.LengthMm:N1} mm against acoustic {o.EffectiveLengthMm:N1} mm"));

        // And the gap is the end correction, which is a fixed fraction of the bore.
        Assert.Equal(ManifoldTuning.FlangedEndCorrection * 40 / 2, ManifoldTuning.EndCorrectionMm(40), 9);
    }

    // ----- goals -----------------------------------------------------------------

    /// <summary>
    /// The three goals must actually differ in the direction they claim: spool
    /// wants a small fast port, race wants a big one, balanced sits between.
    /// </summary>
    [Fact]
    public void TheThreeGoalsDifferInTheDirectionTheyClaim()
    {
        double spool = ManifoldTuning.TargetIntakeVelocity(ManifoldGoal.QuickSpool);
        double balanced = ManifoldTuning.TargetIntakeVelocity(ManifoldGoal.Balanced);
        double race = ManifoldTuning.TargetIntakeVelocity(ManifoldGoal.HighRpmRace);

        Assert.True(spool > balanced && balanced > race);
    }

    /// <summary>
    /// A spool build tunes at the torque peak and a race build at the power peak,
    /// so the race engine gets the shorter runner and the smaller plenum multiple
    /// is the spool engine's.
    /// </summary>
    [Fact]
    public void SpoolTunesLowAndRaceTunesHigh()
    {
        const double torque = 3_500;
        const double power = 7_500;

        Assert.Equal(torque, ManifoldTuning.IntakeTuningRpm(ManifoldGoal.QuickSpool, torque, power));
        Assert.Equal(power, ManifoldTuning.IntakeTuningRpm(ManifoldGoal.HighRpmRace, torque, power));

        double middle = ManifoldTuning.IntakeTuningRpm(ManifoldGoal.Balanced, torque, power);
        Assert.InRange(middle, torque, power);

        Assert.True(
            ManifoldTuning.DefaultPlenumMultiple(ManifoldGoal.QuickSpool, Induction.Turbocharged)
            < ManifoldTuning.DefaultPlenumMultiple(ManifoldGoal.HighRpmRace, Induction.Turbocharged));
    }

    /// <summary>
    /// The whole point of the goals, end to end: a spool build must come out with
    /// a smaller, faster port than a race build on the same engine.
    /// </summary>
    [Fact]
    public void ASpoolBuildGetsASmallerPortThanARaceBuild()
    {
        ManifoldSpec engine = new() { PeakTorqueRpm = 3_500, PeakPowerRpm = 7_500 };

        ManifoldPlan spool = ManifoldTuning.Plan(engine with { Goal = ManifoldGoal.QuickSpool });
        ManifoldPlan race = ManifoldTuning.Plan(engine with { Goal = ManifoldGoal.HighRpmRace });

        Assert.True(
            spool.Intake.RunnerDiameterMm < race.Intake.RunnerDiameterMm,
            $"spool {spool.Intake.RunnerDiameterMm:N1} mm, race {race.Intake.RunnerDiameterMm:N1} mm");

        Assert.True(
            spool.Intake.PlenumVolumeCc < race.Intake.PlenumVolumeCc,
            $"spool plenum {spool.Intake.PlenumVolumeCc:N0} cc, race {race.Intake.PlenumVolumeCc:N0} cc");
    }

    // ----- choosing an order -----------------------------------------------------

    /// <summary>
    /// The recommendation has to be one that fits in a car when any of them does,
    /// and it should be the longest such — a lower order is a stronger pulse.
    /// </summary>
    [Fact]
    public void TheRecommendationIsTheLongestOneThatFits()
    {
        IReadOnlyList<TuningOrder> orders =
            ManifoldTuning.Orders(345, 240, 6_000, 38, 520);

        TuningOrder pick = ManifoldTuning.Recommend(orders, 520);

        Assert.True(pick.Practical);
        Assert.InRange(pick.LengthMm, ManifoldTuning.MinPracticalRunnerMm, 520);

        // Nothing longer than the pick may also have fitted.
        foreach (TuningOrder order in orders)
            if (order.Order < pick.Order)
                Assert.False(order.Practical, $"order {order.Order} fitted and was passed over");
    }

    /// <summary>
    /// A turbocharged build chosen to spool takes the shortest pipe that packages,
    /// where an atmospheric one takes the longest.
    ///
    /// The trade genuinely reverses. On an atmospheric engine the returning pulse
    /// is the only help there is, so the strongest one wins; under boost it is
    /// worth a few per cent against fifty or more from the compressor, while the
    /// pipe's volume is charge that has to be pressurised before any of that
    /// arrives. Same goal, opposite answer, and the induction is what decides it.
    /// </summary>
    [Fact]
    public void SpoolTakesTheShortRunnerWhereAtmosphericTakesTheLong()
    {
        ManifoldSpec engine = new()
        {
            Goal = ManifoldGoal.QuickSpool,
            PeakTorqueRpm = 3_500,
            PeakPowerRpm = 7_000,
        };

        ManifoldPlan atmospheric = ManifoldTuning.Plan(engine);

        ManifoldPlan boosted = ManifoldTuning.Plan(engine with
        {
            Induction = Induction.Turbocharged,
            ManifoldKpa = TuningMath.AtmosphericKpa + 150,
            ExhaustBackPressureKpa = TuningMath.AtmosphericKpa + 170,
        });

        Assert.True(
            boosted.Intake.Recommended.LengthMm < atmospheric.Intake.Recommended.LengthMm,
            $"turbo took {boosted.Intake.Recommended.LengthMm:N0} mm, "
            + $"atmospheric {atmospheric.Intake.Recommended.LengthMm:N0} mm");

        // Both still have to be buildable — this is a different pipe, not no pipe.
        Assert.True(boosted.Intake.Recommended.Practical);
        Assert.True(atmospheric.Intake.Recommended.Practical);
    }

    /// <summary>
    /// Preferring the shortest must still respect what packages, and must pick a
    /// higher order than preferring the longest does.
    /// </summary>
    [Fact]
    public void PreferringTheShortestStillOnlyTakesOneThatFits()
    {
        IReadOnlyList<TuningOrder> orders = ManifoldTuning.Orders(345, 240, 4_000, 34, 650);

        TuningOrder longest = ManifoldTuning.Recommend(orders, 650);
        TuningOrder shortest = ManifoldTuning.Recommend(orders, 650, preferShortest: true);

        Assert.True(shortest.Practical);
        Assert.True(longest.Practical);
        Assert.True(shortest.Order > longest.Order);
        Assert.True(shortest.LengthMm < longest.LengthMm);
        Assert.InRange(shortest.LengthMm, ManifoldTuning.MinPracticalRunnerMm, 650);
    }

    /// <summary>
    /// The tract is the plenum plus every runner, and it has to add up — this is
    /// the figure a turbo build is asked to judge spool on.
    /// </summary>
    [Fact]
    public void TheTractIsThePlenumPlusEveryRunner()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec { Cylinders = 4 });

        Assert.Equal(
            plan.Intake.PlenumVolumeCc + (plan.Intake.RunnerVolumeCc * 4),
            plan.Intake.TractVolumeCc,
            6);
    }

    /// <summary>
    /// A smaller plenum multiple really is a smaller plenum and a smaller tract.
    /// Obvious, and worth pinning because the multiple is now the user's dial.
    /// </summary>
    [Fact]
    public void ASmallerMultipleIsASmallerPlenumAndTract()
    {
        ManifoldSpec engine = new() { Induction = Induction.Turbocharged };

        ManifoldPlan small = ManifoldTuning.Plan(engine with { PlenumMultiple = 0.75 });
        ManifoldPlan large = ManifoldTuning.Plan(engine with { PlenumMultiple = 1.5 });

        Assert.Equal(small.Intake.PlenumVolumeCc * 2, large.Intake.PlenumVolumeCc, 6);
        Assert.True(small.Intake.TractVolumeCc < large.Intake.TractVolumeCc);
    }

    /// <summary>
    /// The honest qualifier, held to arithmetic: on a typical installation the
    /// manifold is about a quarter of what has to be pressurised, so trimming the
    /// plenum moves a couple of per cent of the total and no more.
    ///
    /// The 10 litres is an ordinary front-mount — a medium core and two runs of
    /// 60 mm pipe. If this ever stops being roughly true the note on the page
    /// claiming it should change with it.
    /// </summary>
    [Fact]
    public void TheManifoldIsAboutAQuarterOfWhatHasToBePressurised()
    {
        const double interCoolerAndPiping = 10_000;

        ManifoldSpec turbo = new()
        {
            Induction = Induction.Turbocharged,
            Goal = ManifoldGoal.QuickSpool,
            ManifoldKpa = TuningMath.AtmosphericKpa + 150,
            ExhaustBackPressureKpa = TuningMath.AtmosphericKpa + 170,
        };

        double share = ManifoldTuning.TractShareOfTypicalInstallation(
            ManifoldTuning.Plan(turbo).Intake.TractVolumeCc, interCoolerAndPiping);

        Assert.InRange(share, 0.15, 0.35);

        // And the whole plenum decision moves only a few per cent of the total.
        double lean = ManifoldTuning.Plan(turbo with { PlenumMultiple = 0.75 }).Intake.TractVolumeCc;
        double fat = ManifoldTuning.Plan(turbo with { PlenumMultiple = 0.90 }).Intake.TractVolumeCc;

        Assert.InRange((fat - lean) / (fat + interCoolerAndPiping), 0.005, 0.05);
    }

    /// <summary>Plenum advice reads differently boosted, because the volume costs differently.</summary>
    [Fact]
    public void PlenumAdviceDependsOnInduction()
    {
        Assert.NotEqual(
            ManifoldTuning.PlenumVerdict(0.75, Induction.Turbocharged),
            ManifoldTuning.PlenumVerdict(0.75, Induction.NaturallyAspirated));

        Assert.All(ManifoldTuning.PlenumMultiples, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(
                ManifoldTuning.PlenumVerdict(m, Induction.Turbocharged)));
            Assert.NotEqual("—", ManifoldTuning.PlenumVerdict(m, Induction.NaturallyAspirated));
        });

        Assert.Equal("—", ManifoldTuning.PlenumVerdict(0, Induction.Turbocharged));
    }

    /// <summary>
    /// Every step in the table has to say something different from its neighbours.
    ///
    /// This is the whole point of offering seven of them: a table where 0.75 and
    /// 1.00 carry identical words is worth less than no table, because it invites
    /// the reader to conclude the choice does not matter. The bands and the steps
    /// have to stay the same width as each other, and this is what says so.
    /// </summary>
    [Theory]
    [InlineData(Induction.NaturallyAspirated)]
    [InlineData(Induction.Turbocharged)]
    public void EveryPlenumStepReadsDifferentlyFromTheLast(Induction induction)
    {
        var seen = ManifoldTuning.PlenumMultiples
            .Select(m => ManifoldTuning.PlenumVerdict(m, induction))
            .ToList();

        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// And the multiples the goals actually pick must each be findable, because
    /// the table marks the row in force by matching on the number.
    /// </summary>
    [Fact]
    public void EveryGoalsOwnMultipleGetsItsOwnVerdict()
    {
        foreach (ManifoldGoal goal in Enum.GetValues<ManifoldGoal>())
        {
            foreach (Induction induction in Enum.GetValues<Induction>())
            {
                double multiple = ManifoldTuning.DefaultPlenumMultiple(goal, induction);

                Assert.InRange(multiple, 0.5, 2.0);
                Assert.NotEqual("—", ManifoldTuning.PlenumVerdict(multiple, induction));
            }
        }
    }

    /// <summary>
    /// A low torque peak wants a runner longer than any car can hold. The
    /// calculator must not silently hand back a metre of pipe as though it were
    /// buildable — it says so instead.
    /// </summary>
    [Fact]
    public void AnImpossiblyLowTuningPointIsFlaggedRatherThanQuietlyReturned()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Goal = ManifoldGoal.QuickSpool,
            PeakTorqueRpm = 1_800,
            PeakPowerRpm = 5_000,
            Induction = Induction.Turbocharged,
        });

        // Whatever it recommends, it may not claim a 1.5 m runner is practical.
        Assert.True(plan.Intake.Recommended.LengthMm <= ManifoldTuning.MaxPracticalRunnerMm
                    || !plan.Intake.Recommended.Practical);
    }

    // ----- the plan as a whole ---------------------------------------------------

    /// <summary>
    /// A believable 2.0 litre naturally aspirated build, checked against what such
    /// an engine actually carries. Every one of these bands is wide enough to
    /// admit real designs and narrow enough to catch an arithmetic slip.
    /// </summary>
    [Fact]
    public void ATwoLitreNaBuildComesOutTheSizeSuchEnginesAre()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Litres = 2.0,
            Cylinders = 4,
            Goal = ManifoldGoal.Balanced,
            PeakTorqueRpm = 4_500,
            PeakPowerRpm = 7_000,
            CompressionRatio = 11.0,
        });

        Assert.InRange(plan.Intake.RunnerDiameterMm, 32, 40);
        Assert.InRange(plan.Intake.Recommended.LengthMm, 150, 520);
        Assert.InRange(plan.Intake.PlenumVolumeCc, 2_000, 4_000);
        Assert.InRange(plan.Intake.VelocityAtPeakPower, 85, 105);

        Assert.InRange(plan.Exhaust.PrimaryDiameterMm, 36, 52);
        Assert.InRange(plan.Exhaust.Recommended.LengthMm, 400, 1_100);
        Assert.InRange(plan.Exhaust.CollectorDiameterMm, 50, 75);

        Assert.DoesNotContain(plan.Warnings, w => w.Severity == "stop");
    }

    /// <summary>
    /// A quick-spool turbo build, which is the case with the most ways to go
    /// wrong: it tunes at a low engine speed, where the wave equation wants a pipe
    /// longer than a car, and it sizes the exhaust against boosted mass flow
    /// behind a turbine rather than against atmosphere.
    ///
    /// The design it produces still has to be one somebody could weld.
    /// </summary>
    [Fact]
    public void AQuickSpoolTurboBuildIsStillSomethingYouCouldWeld()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Litres = 2.0,
            Cylinders = 4,
            Induction = Induction.Turbocharged,
            Goal = ManifoldGoal.QuickSpool,
            PeakTorqueRpm = 3_500,
            PeakPowerRpm = 7_000,
            ManifoldKpa = TuningMath.AtmosphericKpa + 150,
            ExhaustBackPressureKpa = TuningMath.AtmosphericKpa + 170,
            VolumetricEfficiency = 105,
        });

        // The runner has to package, even tuning as low as 3,500.
        Assert.True(plan.Intake.Recommended.Practical,
            $"recommended runner was {plan.Intake.Recommended.LengthMm:N0} mm");

        Assert.InRange(plan.Intake.Recommended.LengthMm,
            ManifoldTuning.MinPracticalRunnerMm, ManifoldTuning.MaxPracticalRunnerMm);

        // A spool build keeps the plenum small, because it is volume to fill.
        Assert.True(plan.Intake.PlenumMultiple <= 1.0);

        // Boosted, denser gas: the primary must not come out absurd either way.
        Assert.InRange(plan.Exhaust.PrimaryDiameterMm, 28, 50);
        Assert.True(plan.Exhaust.TotalPrimaryVolumeCc > 0);

        // And it must say that the header length is not the lever here.
        Assert.Contains(plan.Warnings, w => w.Text.Contains("turbine", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(plan.Warnings, w => w.Severity == "stop");
    }

    /// <summary>
    /// The note about runner length has to agree with the runner that was
    /// actually chosen.
    ///
    /// A short runner is a quick-spool choice rather than a turbocharged one: a
    /// boosted build aiming at the top end, or at a balance, is given the longest
    /// runner that packages. Saying "a shorter runner is chosen here" on every
    /// turbocharged engine describes somebody else's manifold two thirds of the
    /// time, and it reads as an explanation of the number printed beside it.
    /// </summary>
    [Theory]
    [InlineData(ManifoldGoal.QuickSpool, true)]
    [InlineData(ManifoldGoal.Balanced, false)]
    [InlineData(ManifoldGoal.HighRpmRace, false)]
    public void TheRunnerLengthNoteFollowsTheGoalRatherThanTheBoost(ManifoldGoal goal, bool shorter)
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Litres = 2.0,
            Cylinders = 4,
            Induction = Induction.Turbocharged,
            Goal = goal,
            PeakTorqueRpm = 3_500,
            PeakPowerRpm = 7_000,
            ManifoldKpa = TuningMath.AtmosphericKpa + 150,
            ExhaustBackPressureKpa = TuningMath.AtmosphericKpa + 170,
            VolumetricEfficiency = 105,
        });

        RecipeWarning note = Assert.Single(
            plan.Warnings, w => w.Text.Contains("plenum and runners come to", StringComparison.Ordinal));

        Assert.Equal(
            shorter, note.Text.Contains("a shorter runner is chosen here", StringComparison.Ordinal));
    }

    /// <summary>
    /// Boost makes the exhaust gas denser, so the same engine needs less pipe area
    /// behind a turbine than it does behind an open header. Getting this backwards
    /// would oversize every turbo manifold the calculator ever drew.
    /// </summary>
    [Fact]
    public void BoostedGasNeedsLessPrimaryAreaNotMore()
    {
        ManifoldSpec atmospheric = new()
        {
            ManifoldKpa = TuningMath.AtmosphericKpa,
            ExhaustBackPressureKpa = TuningMath.AtmosphericKpa,
        };

        // Twice the manifold pressure and twice the back pressure: the mass
        // doubles and the density doubles with it, so the volume flow is unchanged.
        ManifoldSpec boosted = atmospheric with
        {
            ManifoldKpa = TuningMath.AtmosphericKpa * 2,
            ExhaustBackPressureKpa = TuningMath.AtmosphericKpa * 2,
        };

        Assert.Equal(
            ManifoldTuning.Plan(atmospheric).Exhaust.PrimaryDiameterMm,
            ManifoldTuning.Plan(boosted).Exhaust.PrimaryDiameterMm,
            6);

        // Boost without the matching back pressure is more volume, so more pipe.
        ManifoldSpec freeFlowing = atmospheric with { ManifoldKpa = TuningMath.AtmosphericKpa * 2 };

        Assert.True(
            ManifoldTuning.Plan(freeFlowing).Exhaust.PrimaryDiameterMm
            > ManifoldTuning.Plan(atmospheric).Exhaust.PrimaryDiameterMm);
    }

    /// <summary>
    /// The exhaust is fatter than the intake on the same engine, because the gas
    /// coming out is three times the volume that went in. A calculator that got
    /// this backwards would be obvious on the bench and invisible in a unit test
    /// that only checked each side alone.
    /// </summary>
    [Fact]
    public void ThePrimaryIsFatterThanTheRunner()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec());

        Assert.True(
            plan.Exhaust.PrimaryDiameterMm > plan.Intake.RunnerDiameterMm,
            $"primary {plan.Exhaust.PrimaryDiameterMm:N1} mm, runner {plan.Intake.RunnerDiameterMm:N1} mm");
    }

    /// <summary>
    /// Volumes have to be the pipe they describe: π/4 · d² · L, in cc.
    /// </summary>
    [Fact]
    public void PipeVolumeIsTheCylinderItDescribes()
    {
        // A 40 mm pipe 500 mm long holds π/4 × 16 cm² × 50 cm = 628 cc.
        Assert.Equal(628.3, ManifoldTuning.PipeVolumeCc(500, 40), 1);

        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec());

        Assert.Equal(
            ManifoldTuning.PipeVolumeCc(plan.Intake.Recommended.LengthMm, plan.Intake.RunnerDiameterMm),
            plan.Intake.RunnerVolumeCc,
            9);

        Assert.Equal(plan.Exhaust.PrimaryVolumeCc * 4, plan.Exhaust.TotalPrimaryVolumeCc, 9);
    }

    // ----- what it refuses to do -------------------------------------------------

    /// <summary>
    /// Garbage in has to come back as NaN rather than as a number, because a
    /// length that silently reads zero is one somebody could cut a pipe to.
    /// </summary>
    [Fact]
    public void NonsenseInputsReturnNotANumber()
    {
        Assert.True(double.IsNaN(ManifoldTuning.TunedLengthMm(0, 240, 3, 6_000)));
        Assert.True(double.IsNaN(ManifoldTuning.TunedLengthMm(345, 0, 3, 6_000)));
        Assert.True(double.IsNaN(ManifoldTuning.TunedLengthMm(345, 240, 0, 6_000)));
        Assert.True(double.IsNaN(ManifoldTuning.TunedLengthMm(345, 240, 3, 0)));
        Assert.True(double.IsNaN(ManifoldTuning.TunedLengthMm(345, 240, -1, 6_000)));

        Assert.True(double.IsNaN(ManifoldTuning.PortVelocity(500, 100, 7_000, 240, 0)));
        Assert.True(double.IsNaN(ManifoldTuning.AreaForVelocity(500, 100, 7_000, 240, 0)));
        Assert.True(double.IsNaN(ManifoldTuning.EffectiveCylinderVolumeCc(500, 1)));
        Assert.True(double.IsNaN(ManifoldTuning.SpeedOfSound(-300, 1.4, 287)));
        Assert.True(double.IsNaN(ManifoldTuning.HelmholtzHz(345, 0, 400, 300)));
    }

    /// <summary>Nothing here may throw on a spec somebody could plausibly type.</summary>
    [Theory]
    [InlineData(0.6, 3)]
    [InlineData(2.0, 4)]
    [InlineData(6.2, 8)]
    [InlineData(0.0, 0)]
    public void PlanSurvivesWhateverItIsGiven(double litres, int cylinders)
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Litres = litres,
            Cylinders = cylinders,
        });

        Assert.NotNull(plan.Intake);
        Assert.NotNull(plan.Exhaust);
        Assert.NotNull(plan.Warnings);
    }

    [Fact]
    public void PlanRejectsANullSpec() =>
        Assert.Throws<ArgumentNullException>(() => ManifoldTuning.Plan(null!));

    // ----- warnings --------------------------------------------------------------

    /// <summary>
    /// A power peak below the torque peak is a typo, and every length depends on
    /// the pair — so it stops rather than notes.
    /// </summary>
    [Fact]
    public void PowerPeakBelowTorquePeakStops()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            PeakTorqueRpm = 7_000,
            PeakPowerRpm = 4_000,
        });

        Assert.Contains(plan.Warnings, w => w.Severity == "stop");
    }

    /// <summary>A turbo build is told the exhaust length is not the point.</summary>
    [Fact]
    public void ATurboBuildIsToldTheHeaderLengthIsInformationOnly()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Induction = Induction.Turbocharged,
            ManifoldKpa = TuningMath.AtmosphericKpa + 150,
            ExhaustBackPressureKpa = TuningMath.AtmosphericKpa + 170,
        });

        Assert.Contains(plan.Warnings, w => w.Text.Contains("turbine", StringComparison.OrdinalIgnoreCase));
        Assert.True(plan.Exhaust.TotalPrimaryVolumeCc > 0);
    }

    /// <summary>
    /// Boost typed against a naturally aspirated engine is a contradiction the
    /// exhaust sizing would otherwise swallow silently.
    /// </summary>
    [Fact]
    public void BoostOnANaturallyAspiratedEngineIsQueried()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Induction = Induction.NaturallyAspirated,
            ManifoldKpa = TuningMath.AtmosphericKpa + 100,
        });

        Assert.Contains(plan.Warnings, w => w.Text.Contains("naturally aspirated", StringComparison.Ordinal));
    }

    /// <summary>A port far too big for the engine gets said out loud.</summary>
    [Fact]
    public void AnOversizedPortIsCalledOut()
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec
        {
            Litres = 1.0,
            Cylinders = 4,
            PeakPowerRpm = 6_000,
            PeakTorqueRpm = 4_000,
            IntakeRunnerDiameterMm = 60,
        });

        Assert.Contains(plan.Warnings, w => w.Text.Contains("larger than the engine can use", StringComparison.Ordinal));
    }

    /// <summary>
    /// No warning may be produced with an empty message or an unknown severity —
    /// they are rendered straight into the window.
    /// </summary>
    [Theory]
    [InlineData(ManifoldGoal.QuickSpool, Induction.Turbocharged)]
    [InlineData(ManifoldGoal.Balanced, Induction.NaturallyAspirated)]
    [InlineData(ManifoldGoal.HighRpmRace, Induction.NaturallyAspirated)]
    public void EveryWarningIsWellFormed(ManifoldGoal goal, Induction induction)
    {
        ManifoldPlan plan = ManifoldTuning.Plan(new ManifoldSpec { Goal = goal, Induction = induction });

        Assert.All(plan.Warnings, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w.Text));
            Assert.Contains(w.Severity, (string[])["note", "watch", "stop"]);
            Assert.DoesNotContain("{", w.Text, StringComparison.Ordinal);
        });
    }
}
