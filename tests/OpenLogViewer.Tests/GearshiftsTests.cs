using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading the gearbox off the gearshifts.
///
/// The one thing a log states about the gearing without a road speed sensor. The
/// car does not change speed while the clutch is down, so engine speed either
/// side of an upshift is in the ratio of the two gears — and on a car whose speed
/// sensor has never been wired, that is the only check there is on the ratios
/// somebody typed in.
/// </summary>
public class GearshiftsTests
{
    private static VehicleSpec Car(params double[] ratios) => new()
    {
        GearRatios = ratios.Length > 0 ? ratios : [3.83, 2.20, 1.40, 1.00, 0.81],
        FinalDrive = 3.25,
        Tyre = new Tyre(205, 60, 15),
    };

    /// <summary>
    /// A drive: accelerate in gear, change up, accelerate again.
    /// </summary>
    /// <param name="dawdle">
    /// Seconds spent off the throttle after the clutch is home. A driver who
    /// pauses lets engine speed keep falling past the moment the gears took over,
    /// and the step measures larger than the gearbox is.
    /// </param>
    /// <param name="passes">
    /// How many times to drive the same ladder. Twice, where a step has to be
    /// corroborated — a ratio only one shift ever produced is not reported as a
    /// measurement, so a single pass proves nothing about the checking.
    /// </param>
    private static LogDocument Drive(
        IReadOnlyList<double> ratios, IReadOnlyList<int> shiftsAt,
        double hz = 15, double dawdle = 0, int passes = 1)
    {
        List<double> times = [], rpms = [], tps = [];

        double t = 0;
        double rpm = 2200;
        int gear = shiftsAt[0];

        void Sample(double engine, double throttle)
        {
            times.Add(t);
            rpms.Add(engine);
            tps.Add(throttle);
            t += 1 / hz;
        }

        for (int pass = 0; pass < passes; pass++)
        {
            if (pass > 0)
        {
            // Slowing for the roundabout, and away again from the bottom. Far too
            // gently to look like a clutch coming out.
            while (rpm > 2200)
            {
                Sample(rpm, 2);
                rpm -= 500 / hz;
            }

            gear = shiftsAt[0];
        }

        foreach (int next in shiftsAt.Skip(1))
        {
            // Pulling.
            while (rpm < 5600)
            {
                Sample(rpm, 95);
                rpm += 700 / hz;
            }

            double before = rpm;
            double after = before * ratios[next - 1] / ratios[gear - 1];

            // The clutch: engine speed falls freely for about a third of a second.
            double falling = before;
            while (falling > after)
            {
                Sample(falling, 3);
                falling -= 4500 / hz;
            }

            // Home, and then however long the driver takes to get back on it.
            rpm = after;

            for (double d = 0; d < dawdle; d += 1 / hz)
            {
                Sample(rpm, 3);
                rpm -= 900 / hz;
            }

            gear = next;
        }

        while (rpm < 5600)
        {
            Sample(rpm, 95);
            rpm += 700 / hz;
        }
        }

        return new LogDocument
        {
            FilePath = "drive",
            Time = new LogChannel("Time", "s", 3, [.. times], preservePrecision: true),
            Channels =
            [
                new LogChannel("RPM", "rpm", 0, [.. rpms]),
                new LogChannel("TPS", "%", 1, [.. tps]),
            ],
            FormatName = "test",
        };
    }

    // ----- what a shift measures -----------------------------------------------------

    [Fact]
    public void AnUpshiftMeasuresTheRatioBetweenTheTwoGears()
    {
        double[] ratios = [3.83, 2.20, 1.40, 1.00, 0.81];

        LogDocument log = Drive(ratios, [1, 2, 3, 4]);

        IReadOnlyList<ShiftStep> shifts = Gearshifts.Find(log);

        Assert.Equal(3, shifts.Count);

        // 1 to 2, 2 to 3, 3 to 4 — and nothing about the tyres, the differential
        // or a road speed went into any of them.
        Assert.Equal(2.20 / 3.83, shifts[0].Ratio, 2);
        Assert.Equal(1.40 / 2.20, shifts[1].Ratio, 2);
        Assert.Equal(1.00 / 1.40, shifts[2].Ratio, 2);
    }

    [Fact]
    public void ADriverWhoDawdlesMakesTheStepLookBiggerThanItIs()
    {
        // The bias the tolerance exists to absorb, and it only goes one way:
        // engine speed keeps falling after the gears have taken over, so the
        // measured step always reads low. Worth knowing when one sits just under
        // a real ratio.
        double[] ratios = [3.83, 2.20, 1.40, 1.00, 0.81];

        double brisk = Gearshifts.Find(Drive(ratios, [1, 2]))[0].Ratio;

        IReadOnlyList<ShiftStep> slow = Gearshifts.Find(Drive(ratios, [1, 2], dawdle: 0.3));

        // A slow shift is either measured low or rejected outright; both are
        // better than being believed.
        if (slow.Count > 0) Assert.True(slow[0].Ratio < brisk);

        Assert.Equal(2.20 / 3.83, brisk, 2);
    }

    [Fact]
    public void LiftingOffIsNotAGearshift()
    {
        // It looks like one and is not. Close the throttle at five and a half
        // thousand and engine braking drags engine speed down as fast as a clutch
        // would, then the car settles to a steady coast — a sharp fall of about
        // the right size, in about the right time, with no gear change anywhere.
        //
        // What tells them apart is what happens next. After a shift the driver is
        // back on the throttle; after a lift they are not.
        List<double> times = [], rpms = [], tps = [];

        double t = 0;
        const double hz = 15;

        void Sample(double rpm, double throttle)
        {
            times.Add(t);
            rpms.Add(rpm);
            tps.Add(throttle);
            t += 1 / hz;
        }

        for (double r = 3000; r < 5600; r += 700 / hz) Sample(r, 95);

        // Off it: engine braking, fast.
        for (double r = 5600; r > 3400; r -= 4200 / hz) Sample(r, 2);

        // And coasting, still off it.
        for (int i = 0; i < 60; i++) Sample(3400, 2);

        var log = new LogDocument
        {
            FilePath = "lift",
            Time = new LogChannel("Time", "s", 3, [.. times], preservePrecision: true),
            Channels =
            [
                new LogChannel("RPM", "rpm", 0, [.. rpms]),
                new LogChannel("TPS", "%", 1, [.. tps]),
            ],
            FormatName = "test",
        };

        Assert.Empty(Gearshifts.Find(log));
    }

    // ----- checking the gearbox somebody entered -------------------------------------

    [Fact]
    public void AGearboxThatMakesTheseStepsIsAccepted()
    {
        double[] ratios = [3.83, 2.20, 1.40, 1.00, 0.81];

        // Driven twice so each step is corroborated; one shift is not a
        // measurement.
        LogDocument log = Drive(ratios, [1, 2, 3, 4], passes: 2);

        GearboxCheck check = Gearshifts.Check(log, Car(ratios));

        Assert.True(check.Consistent, check.Summary);
        Assert.Contains("accounts for every shift", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AGearboxThatCannotMakeTheseStepsIsRefused()
    {
        // The mistake this exists to catch, and it is the mistake that was made:
        // a dogleg five-speed was entered for a car with an overdrive one. Their
        // gears step quite differently, and every horsepower figure worked out
        // from a gear rests on which it is.
        double[] driven = [3.83, 2.20, 1.40, 1.00, 0.81];
        double[] entered = [3.72, 2.40, 1.77, 1.26, 1.00];

        LogDocument log = Drive(driven, [1, 2, 3, 4], passes: 2);

        GearboxCheck check = Gearshifts.Check(log, Car(entered));

        Assert.False(check.Consistent);
        Assert.True(check.WorstMismatch > Gearshifts.Tolerance);
        Assert.Contains("does not account for", check.Summary, StringComparison.Ordinal);
        Assert.Contains("Every power figure", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void OneShiftOfAKindIsNotAMeasurement()
    {
        // A single shift can be mismeasured by a driver taking their time. A
        // ratio nothing corroborates is not reported as one.
        double[] ratios = [3.83, 2.20, 1.40, 1.00, 0.81];

        GearboxCheck check = Gearshifts.Check(Drive(ratios, [1, 2]), Car(ratios));

        Assert.Single(check.Shifts);
        Assert.Empty(check.MeasuredSteps);
        Assert.False(check.Measured);
    }

    [Fact]
    public void ALogWithNoShiftsInItSaysSoRatherThanApproving()
    {
        // Silence is not agreement. A log where nobody changed gear says nothing
        // about the gearbox, and must not read as though it had checked.
        double[] ratios = [3.83, 2.20, 1.40, 1.00, 0.81];

        LogDocument steady = Drive(ratios, [3]);

        GearboxCheck check = Gearshifts.Check(steady, Car(ratios));

        Assert.False(check.Measured);
        Assert.False(check.Consistent);
        Assert.Contains("No gearshift", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogWithNoEngineSpeedHasNoShiftsToFind()
    {
        var bare = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2, 0.3], preservePrecision: true),
            Channels = [new LogChannel("CLT", "C", 0, [80, 81, 82, 83])],
            FormatName = "test",
        };

        Assert.Empty(Gearshifts.Find(bare));
    }
}
