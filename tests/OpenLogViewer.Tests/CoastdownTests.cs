using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Measuring the drag area and the rolling resistance instead of guessing them.
///
/// The tests are built the same way the road-load ones are: a car with known
/// coefficients is rolled down a hill of known gradient by the same arithmetic
/// the application uses, and then the recording is handed back to be measured. If
/// the fit, the neutral effective mass, the air density or the signs are wrong,
/// the coefficients that were written down do not come back.
///
/// The one that matters most is
/// <see cref="TwoDirectionsSeparateTheTyresFromTheHill"/>. Everything else here
/// is in service of it.
/// </summary>
public class CoastdownTests
{
    private const double TrueDragArea = 0.68;
    private const double TrueRolling = 0.0125;

    private static readonly Ambient Air = new(101.3, 20, 40);

    private static VehicleSpec Car(double gradePercent = 0) => new()
    {
        KerbMassKg = 1530,
        OccupantMassKg = 82,
        DragAreaM2 = TrueDragArea,
        RollingResistance = TrueRolling,
        WheelInertiaKgM2 = 4.5,
        EngineInertiaKgM2 = 0.20,
        GearRatios = [3.27, 2.05, 1.62, 1.35, 1.03, 0.84],
        FinalDrive = 4.10,
        Tyre = new Tyre(245, 40, 18),
        GradePercent = gradePercent,
    };

    /// <summary>
    /// Rolls the car and writes down what a logger would have seen.
    /// </summary>
    /// <param name="gearRolledIn">
    /// Zero for neutral, where the engine sits at idle while the car slows. Any
    /// other gear means the wheels are turning the engine, and engine braking —
    /// which is not modelled here — would in reality swamp everything being
    /// measured.
    /// </param>
    /// <param name="brakeDrag">Extra retarding force in newtons, for the run somebody brushed the brakes on.</param>
    private static LogDocument Coast(
        VehicleSpec car, double fromMs, double toMs, double hz = 20,
        int gearRolledIn = VehicleSpec.Neutral, double brakeDrag = 0,
        string speedUnits = "km/h", double neutralRpm = 820)
    {
        double rho = AirDensity.KgPerCubicMetre(Air);
        double mass = car.EffectiveMassKg(VehicleSpec.Neutral);

        const double dt = 0.001;

        double v = fromMs;
        double t = 0;
        double next = 0;

        List<double> times = [], speeds = [];

        while (v > toMs && t < 120)
        {
            if (t >= next - 1e-12)
            {
                times.Add(t);
                speeds.Add(v);
                next += 1 / hz;
            }

            v += (RoadLoad.AccelerationMs2(car, VehicleSpec.Neutral, v, 0, rho)
                  - (brakeDrag / mass)) * dt;
            t += dt;
        }

        double factor = speedUnits == "km/h" ? 3.6 : 1 / 0.44704;

        // Idle in neutral; turned by the wheels in gear.
        double[] rpm = [.. speeds.Select(s =>
            gearRolledIn == VehicleSpec.Neutral ? neutralRpm : car.RpmFromSpeedMs(s, gearRolledIn))];

        return new LogDocument
        {
            FilePath = "coast",
            Time = new LogChannel("Time", "s", 3, [.. times], preservePrecision: true),
            Channels =
            [
                new LogChannel("RPM", "rpm", 0, rpm),
                new LogChannel("TPS", "%", 1, [.. speeds.Select(_ => 2.0)]),
                new LogChannel("VSS", speedUnits, 1, [.. speeds.Select(s => s * factor)]),
            ],
            FormatName = "test",
        };
    }

    private static CoastdownRun Only(LogDocument log, VehicleSpec car)
    {
        CoastdownSearchResult found = Coastdown.Find(log, car);

        Assert.True(found.Runs.Count == 1, $"expected one coast, got {found.Runs.Count}: {found.Summary}");

        return found.Runs[0];
    }

    // ----- the closed loop --------------------------------------------------------

    [Fact]
    public void AFlatRoadGivesBackTheCoefficientsItWasRolledWith()
    {
        VehicleSpec car = Car();
        LogDocument log = Coast(car, fromMs: 45, toMs: 14);

        CoastdownFit fit = Coastdown.Fit(log, car, Air, Only(log, car));

        Assert.Equal(TrueDragArea, fit.DragAreaM2, 2);
        Assert.Equal(TrueRolling, fit.RollingResistance, 4);
        Assert.True(fit.Agreement > 0.999, $"agreement was only {fit.Agreement:P2}");

        // A single direction cannot know whether the road was level, so it says
        // so however level the road happened to be.
        Assert.False(fit.BothDirections);
        Assert.Contains(fit.Cautions, c => c.Contains("one direction", StringComparison.Ordinal));
    }

    [Fact]
    public void OneDirectionOnASlopeChargesTheHillToTheTyres()
    {
        // The reason a coastdown is run both ways. Gravity along the road is
        // indistinguishable from rolling resistance in a single run, and one and
        // a half per cent of gradient is larger than the entire true figure.
        VehicleSpec hill = Car(gradePercent: 1.5);
        LogDocument log = Coast(hill, 45, 14);

        VehicleSpec unaware = Car();
        CoastdownFit fit = Coastdown.Fit(log, unaware, Air, Only(log, unaware));

        // The drag area survives — gravity does not depend on speed.
        Assert.Equal(TrueDragArea, fit.DragAreaM2, 2);

        // The rolling resistance does not: it has picked up the whole gradient
        // and come back more than twice what it should be.
        Assert.InRange(fit.RollingResistance, TrueRolling * 2, TrueRolling * 2.5);

        // And it fits beautifully while being wrong, which is why agreement is
        // not a measure of correctness.
        Assert.True(fit.Agreement > 0.999);
    }

    [Fact]
    public void TwoDirectionsSeparateTheTyresFromTheHill()
    {
        // The headline. The hill reverses sign between the two runs and the
        // tyres do not, so the average of the intercepts is the rolling
        // resistance and half their difference is the gradient.
        VehicleSpec up = Car(gradePercent: 1.5);
        VehicleSpec down = Car(gradePercent: -1.5);

        LogDocument outward = Coast(up, 45, 14);
        LogDocument back = Coast(down, 45, 14);

        VehicleSpec unaware = Car();

        // The two runs are separate recordings, so each is regressed against its
        // own log and the pair combined.
        CoastdownFit one = Coastdown.Fit(outward, unaware, Air, Only(outward, unaware));
        CoastdownFit two = Coastdown.Fit(back, unaware, Air, Only(back, unaware));

        CoastdownFit paired = Coastdown.Combine(one, two);

        Assert.True(paired.BothDirections);
        Assert.Equal(TrueDragArea, paired.DragAreaM2, 2);
        Assert.Equal(TrueRolling, paired.RollingResistance, 4);

        // And the road itself falls out, in the direction the first run went.
        Assert.Equal(1.5, paired.GradePercent, 2);
    }

    [Fact]
    public void TheGradeIsReportedInTheDirectionOfTheFirstRun()
    {
        VehicleSpec up = Car(gradePercent: 1.5);
        VehicleSpec down = Car(gradePercent: -1.5);

        VehicleSpec unaware = Car();

        LogDocument a = Coast(up, 45, 14);
        LogDocument b = Coast(down, 45, 14);

        CoastdownFit upFirst = Coastdown.Combine(
            Coastdown.Fit(a, unaware, Air, Only(a, unaware)),
            Coastdown.Fit(b, unaware, Air, Only(b, unaware)));

        CoastdownFit downFirst = Coastdown.Combine(
            Coastdown.Fit(b, unaware, Air, Only(b, unaware)),
            Coastdown.Fit(a, unaware, Air, Only(a, unaware)));

        Assert.Equal(1.5, upFirst.GradePercent, 2);
        Assert.Equal(-1.5, downFirst.GradePercent, 2);

        // Whichever way round, the car comes out the same.
        Assert.Equal(upFirst.RollingResistance, downFirst.RollingResistance, 6);
        Assert.Equal(upFirst.DragAreaM2, downFirst.DragAreaM2, 6);
    }

    [Fact]
    public void ASlopeIsCalledOutEvenThoughItHasBeenTakenOut()
    {
        // Having removed the hill, say that there was one. A stretch this far off
        // level is worth knowing about: it is the difference between a figure
        // somebody could have taken one-way and one they could not.
        VehicleSpec unaware = Car();

        LogDocument a = Coast(Car(gradePercent: 1.5), 45, 14);
        LogDocument b = Coast(Car(gradePercent: -1.5), 45, 14);

        CoastdownFit paired = Coastdown.Combine(
            Coastdown.Fit(a, unaware, Air, Only(a, unaware)),
            Coastdown.Fit(b, unaware, Air, Only(b, unaware)));

        Assert.Equal(TrueRolling, paired.RollingResistance, 4);
        Assert.Contains(paired.Cautions, c => c.Contains("uphill or down", StringComparison.Ordinal));

        // And the one-direction warning is gone, because it no longer applies.
        Assert.DoesNotContain(paired.Cautions, c => c.Contains("one direction", StringComparison.Ordinal));
    }

    [Fact]
    public void ARoadTooSteepToCoastDownCannotBeMeasuredOnAtAll()
    {
        // Not a defect — the physics. Going down a two and a half per cent slope
        // gravity very nearly cancels the road load, so the car hardly slows: it
        // gives up decelerating at about 33 metres a second and simply keeps
        // rolling. There is no speed range left to separate the tyres from the
        // air with, and no amount of patience produces one.
        //
        // Which means the pair can never be completed on such a road, and the
        // uphill half on its own is exactly the measurement that would be wrong.
        VehicleSpec unaware = Car();

        CoastdownSearchResult downhill =
            Coastdown.Find(Coast(Car(gradePercent: -2.5), 45, 14), unaware);

        Assert.Empty(downhill.Runs);
        Assert.Contains("separate rolling resistance from drag", downhill.Summary, StringComparison.Ordinal);

        // The uphill run is perfectly findable, which is the trap: one direction
        // is available, and it is the one that lies.
        CoastdownSearchResult uphill =
            Coastdown.Find(Coast(Car(gradePercent: 2.5), 45, 14), unaware);

        Assert.Single(uphill.Runs);
    }

    // ----- what makes a run unusable ---------------------------------------------

    [Fact]
    public void CoastingInGearIsRecognisedAndRefused()
    {
        // Engine braking is far larger than either coefficient being measured.
        // The ratio of engine speed to road speed gives it away: in gear the
        // wheels turn the engine and it holds constant, where in neutral the
        // engine idles and the ratio climbs the whole way down.
        VehicleSpec car = Car();
        LogDocument log = Coast(car, 45, 14, gearRolledIn: 5);

        CoastdownRun run = Only(log, car);

        Assert.True(run.InGear);
        Assert.Equal(5, run.Gear);

        CoastdownFit fit = Coastdown.Fit(log, car, Air, run);

        Assert.Contains(fit.Cautions, c => c.Contains("not neutral", StringComparison.Ordinal));
    }

    [Fact]
    public void ANeutralCoastIsNotMistakenForAGearItHappensToMatch()
    {
        // What the steadiness test is actually for, and why matching on the value
        // of the ratio is not enough on its own.
        //
        // In neutral the engine holds whatever speed it holds while the car
        // slows, so engine speed over road speed climbs the whole way down —
        // and somewhere on the way it passes through the ratio of a real gear.
        // Here the engine is set so that the middle of the coast lands exactly on
        // top gear. On the value alone this reads as a car in sixth; on the
        // steadiness it obviously is not, because a car in gear holds the ratio
        // to a fraction of a per cent and this one sweeps across half of itself.
        VehicleSpec car = Car();

        double held = car.RpmFromSpeedMs(20, 6);

        LogDocument log = Coast(car, 26, 15, neutralRpm: held);

        CoastdownRun run = Only(log, car);

        Assert.False(run.InGear);
    }

    [Fact]
    public void ANarrowSpeedRangeCannotSeparateTheTwoCoefficients()
    {
        // Rolling resistance is flat with speed and drag grows with its square,
        // so telling them apart means watching the total change as the square
        // changes. From 38 to 32 there is barely any change to watch.
        VehicleSpec car = Car();
        CoastdownSearchResult found = Coastdown.Find(Coast(car, 45, 39), car);

        Assert.Empty(found.Runs);
        Assert.Contains("separate rolling resistance from drag", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASteadyBrakeDragIsIndistinguishableFromGrip()
    {
        // Worth knowing what the agreement figure does not catch. Two hundred
        // newtons of brake held throughout is constant with speed, exactly like
        // rolling resistance, so it lands wholly in the intercept and the fit is
        // as straight as a clean one. The drag area is untouched and the rolling
        // resistance doubles, with nothing in the numbers to say so. Only the
        // driver knows, which is why the instruction is off everything.
        VehicleSpec car = Car();
        LogDocument log = Coast(car, 45, 14, brakeDrag: 200);

        CoastdownFit fit = Coastdown.Fit(log, car, Air, Only(log, car));

        Assert.True(fit.RollingResistance > TrueRolling * 1.5);
        Assert.Equal(TrueDragArea, fit.DragAreaM2, 2);
    }

    // ----- what the log has to have ----------------------------------------------

    [Fact]
    public void WithoutARoadSpeedChannelThereIsNothingToMeasure()
    {
        var log = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2, 0.3], preservePrecision: true),
            Channels = [new LogChannel("RPM", "rpm", 0, [3000, 2900, 2800, 2700])],
            FormatName = "test",
        };

        CoastdownSearchResult found = Coastdown.Find(log, Car());

        Assert.Empty(found.Runs);
        Assert.Contains("no road speed channel", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnlabelledSpeedChannelCannotBeGuessedAtOnACoast()
    {
        // A pull can have its speed units worked out from the gearing, because
        // only one reading lands the ratio on a real gear. A coast cannot: in
        // neutral the ratio matches nothing by design, so there is nothing to
        // check a candidate against.
        VehicleSpec car = Car();
        LogDocument labelled = Coast(car, 45, 14);

        LogChannel speed = labelled.FindChannel("VSS")!;

        var bare = new LogDocument
        {
            FilePath = labelled.FilePath,
            Time = labelled.Time,
            Channels =
            [
                .. labelled.Channels.Where(c => c.Name != "VSS"),
                new LogChannel("VSS", "", 1, [.. Enumerable.Range(0, speed.Length).Select(speed.At)]),
            ],
            FormatName = labelled.FormatName,
        };

        CoastdownSearchResult found = Coastdown.Find(bare, car);

        Assert.Empty(found.Runs);
        Assert.Contains("does not say what it is in", found.Summary, StringComparison.Ordinal);

        // Told outright, it works — which is how a pull elsewhere in the same log
        // pays for the coast.
        CoastdownSearchResult told = Coastdown.Find(bare, car, speedToMetresPerSecond: 1 / 3.6);

        Assert.Single(told.Runs);
    }

    [Theory]
    [InlineData("km/h")]
    [InlineData("mph")]
    public void ADeclaredSpeedUnitIsUsedAsDeclared(string units)
    {
        VehicleSpec car = Car();
        LogDocument log = Coast(car, 45, 14, speedUnits: units);

        CoastdownFit fit = Coastdown.Fit(log, car, Air, Only(log, car));

        Assert.Equal(TrueDragArea, fit.DragAreaM2, 2);
        Assert.Equal(TrueRolling, fit.RollingResistance, 4);
    }

    // ----- using the answer -------------------------------------------------------

    [Fact]
    public void ApplyingAFitReplacesTheGuessesAndLeavesTheHillBehind()
    {
        VehicleSpec up = Car(gradePercent: 1.5);
        VehicleSpec down = Car(gradePercent: -1.5);

        // A vehicle whose two coefficients are wrong, as an unmeasured one is.
        VehicleSpec guessed = Car() with { DragAreaM2 = 0.90, RollingResistance = 0.020 };

        LogDocument a = Coast(up, 45, 14);
        LogDocument b = Coast(down, 45, 14);

        CoastdownFit paired = Coastdown.Combine(
            Coastdown.Fit(a, guessed, Air, Only(a, guessed)),
            Coastdown.Fit(b, guessed, Air, Only(b, guessed)));

        VehicleSpec measured = paired.ApplyTo(guessed);

        Assert.Equal(TrueDragArea, measured.DragAreaM2, 2);
        Assert.Equal(TrueRolling, measured.RollingResistance, 4);

        // The gradient describes the road the coast was done on, not the car. A
        // pull made somewhere else must not have that hill subtracted from it.
        Assert.Equal(0, measured.GradePercent);
        Assert.Equal(1.5, paired.GradePercent, 2);
    }

    [Fact]
    public void AFitThatCameToNothingChangesNothing()
    {
        VehicleSpec car = Car();

        var nothing = new CoastdownFit
        {
            DragAreaM2 = double.NaN,
            RollingResistance = double.NaN,
            GradePercent = double.NaN,
            Agreement = double.NaN,
            Samples = 0,
            BothDirections = false,
            Cautions = [],
        };

        Assert.False(nothing.Usable);
        Assert.Same(car, nothing.ApplyTo(car));
    }
}
