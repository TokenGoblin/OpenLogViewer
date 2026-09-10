using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Finding the pulls in a log.
///
/// Every one of these builds a recording of a car doing something specific and
/// asks what was made of it. That matters more here than in most of the analysis:
/// the arithmetic downstream is happy to produce a curve from a gearshift, a
/// lift, or a pull in the wrong gear, and every one of those comes out looking
/// like an engine rather than like a mistake.
/// </summary>
public class DynoRunTests
{
    private static VehicleSpec Car() => new()
    {
        KerbMassKg = 1530,
        OccupantMassKg = 82,
        GearRatios = [3.27, 2.05, 1.62, 1.35, 1.03, 0.84],
        FinalDrive = 4.10,
        Tyre = new Tyre(245, 40, 18),
    };

    /// <summary>
    /// A log being written, a manoeuvre at a time.
    ///
    /// Engine speed is generated and road speed follows from it through the
    /// gearing, which is the way round a real car works and the way round that
    /// lets slip be introduced as a deliberate discrepancy between the two.
    /// </summary>
    private sealed class Recording
    {
        private readonly List<double> _t = [];
        private readonly List<double> _rpm = [];
        private readonly List<double> _speed = [];
        private readonly List<double> _tps = [];

        private double _now;

        /// <summary>Sitting still, or cruising, with engine speed going nowhere.</summary>
        public Recording Steady(double seconds, double hz, double rpm, double tps, int gear = 4)
        {
            VehicleSpec car = Car();

            for (double t = 0; t < seconds; t += 1 / hz)
            {
                _t.Add(_now + t);
                _rpm.Add(rpm);
                _speed.Add(car.SpeedMsFromRpm(rpm, gear));
                _tps.Add(tps);
            }

            _now += seconds;

            return this;
        }

        /// <summary>
        /// Engine speed climbing from one figure to another over a given time.
        /// </summary>
        /// <param name="slipAt">
        /// How much faster the engine is turning than the road speed implies, as a
        /// fraction. A locked driveline is zero throughout; a torque converter is
        /// largest at the bottom and falls away as it comes up to speed.
        /// </param>
        /// <param name="tpsAt">Throttle against progress through the pull, 0 to 1.</param>
        public Recording Pull(
            int gear, double fromRpm, double toRpm, double seconds, double hz,
            Func<double, double>? slipAt = null,
            Func<double, double>? tpsAt = null)
        {
            VehicleSpec car = Car();

            for (double t = 0; t < seconds; t += 1 / hz)
            {
                double progress = t / seconds;

                // Slightly decelerating, the way a real pull is: the first
                // thousand rpm go by faster than the last.
                double rpm = fromRpm + ((toRpm - fromRpm) * Math.Pow(progress, 0.85));
                double slip = slipAt?.Invoke(progress) ?? 0;

                _t.Add(_now + t);
                _rpm.Add(rpm);
                _speed.Add(car.SpeedMsFromRpm(rpm / (1 + slip), gear));
                _tps.Add(tpsAt?.Invoke(progress) ?? 98);
            }

            _now += seconds;

            return this;
        }

        /// <summary>
        /// Nudging against the limiter: the pedal still on the floor, engine
        /// speed creeping rather than climbing.
        ///
        /// A creep of a couple of rpm a second rather than a flat line, because
        /// that is what a car against its limiter actually does and because a
        /// perfectly flat line would be cut off by any test at all, including a
        /// comparison against nothing. What has to be cut here is a rise that is
        /// real but far too small to measure an engine by.
        /// </summary>
        public Recording Limiter(int gear, double rpm, double seconds, double hz, double creepPerSecond = 2)
        {
            VehicleSpec car = Car();

            for (double t = 0; t < seconds; t += 1 / hz)
            {
                double held = rpm + (creepPerSecond * t);

                _t.Add(_now + t);
                _rpm.Add(held);
                _speed.Add(car.SpeedMsFromRpm(held, gear));
                _tps.Add(98);
            }

            _now += seconds;

            return this;
        }

        /// <summary>
        /// Ordinary driving: engine speed wandering, throttle somewhere between a
        /// tenth and a half, for as long as you like.
        ///
        /// What a real log is nearly all of. It exists because the tests were
        /// written from logs that were nearly all pull, and a rule tuned on those
        /// broke on the first real recording it met.
        /// </summary>
        public Recording Traffic(double seconds, double hz, int gear, double topThrottle = 45)
        {
            VehicleSpec car = Car();

            for (double t = 0; t < seconds; t += 1 / hz)
            {
                double phase = Math.Sin((_now + t) * 0.7);
                double rpm = 2200 + (600 * phase);

                _t.Add(_now + t);
                _rpm.Add(rpm);
                _speed.Add(car.SpeedMsFromRpm(rpm, gear));
                _tps.Add(12 + ((topThrottle - 12) * (0.5 + (0.5 * phase))));
            }

            _now += seconds;

            return this;
        }

        /// <summary>A shift: the throttle off and engine speed falling for a moment.</summary>
        public Recording Shift(double fromRpm, double toRpm, double seconds, double hz, int intoGear)
        {
            VehicleSpec car = Car();

            for (double t = 0; t < seconds; t += 1 / hz)
            {
                double rpm = fromRpm + ((toRpm - fromRpm) * (t / seconds));

                _t.Add(_now + t);
                _rpm.Add(rpm);
                _speed.Add(car.SpeedMsFromRpm(fromRpm, intoGear - 1));
                _tps.Add(4);
            }

            _now += seconds;

            return this;
        }

        /// <summary>
        /// The finished log. The road speed is written out in whatever unit is
        /// asked for, with whatever label is asked for — including none.
        /// </summary>
        /// <param name="speedUnits">The unit the road speed is actually written in.</param>
        /// <param name="label">
        /// What the channel claims to be in. Defaults to the truth; pass an empty
        /// string for the very common log that records a speed and never says.
        /// </param>
        public LogDocument Build(
            string speedUnits = "km/h", bool withSpeed = true, bool withThrottle = true,
            string? label = null, double throttleScale = 1, string throttleUnits = "%")
        {
            double factor = speedUnits switch
            {
                "km/h" => 3.6,
                "mph" => 1 / 0.44704,
                _ => 1,
            };

            List<LogChannel> channels = [new LogChannel("RPM", "rpm", 0, [.. _rpm])];

            if (withThrottle)
            {
                channels.Add(new LogChannel(
                    "TPS", throttleUnits, 3, [.. _tps.Select(v => v * throttleScale)]));
            }

            if (withSpeed)
            {
                channels.Add(new LogChannel(
                    "VSS", label ?? speedUnits, 1,
                    [.. _speed.Select(v => v * factor)]));
            }

            return new LogDocument
            {
                FilePath = "synthetic",
                Time = new LogChannel("Time", "s", 3, [.. _t], preservePrecision: true),
                Channels = channels,
                FormatName = "test",
            };
        }
    }

    // ----- the ordinary case ----------------------------------------------------

    [Fact]
    public void ACleanPullIsFoundWithItsGearMeasuredRatherThanAssumed()
    {
        LogDocument log = new Recording()
            .Steady(seconds: 4, hz: 20, rpm: 3000, tps: 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Steady(seconds: 3, hz: 20, rpm: 6800, tps: 5)
            .Build();

        PullSearchResult found = DynoRun.Find(log, Car());

        DynoPull pull = Assert.Single(found.Pulls);

        Assert.True(pull.IsClean, $"faults: {string.Join(", ", pull.Faults)}");
        Assert.Equal(4, pull.Gearing.Gear);
        Assert.Equal(SpeedSource.EngineSpeedAndGear, pull.Speed);

        // The cruise either side is outside it. The run is cut on engine speed
        // rising and then narrowed to where the pedal was actually down, because
        // the fit that finds the rise sees the pull coming and would otherwise
        // start the curve a quarter of a second early.
        Assert.InRange(pull.StartRpm, 2950, 3200);
        Assert.InRange(pull.EndRpm, 6500, 6900);
        Assert.InRange(pull.SampleRateHz, 19, 21);
    }

    [Fact]
    public void AGearshiftEndsOnePullAndStartsAnother()
    {
        // Nothing here knows what a gearshift is. Engine speed falls during one,
        // and that is enough — the same cut handles a lift, a downshift and a
        // limiter without any of them being named.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 3)
            .Pull(gear: 3, fromRpm: 3000, toRpm: 6800, seconds: 5, hz: 20)
            .Shift(fromRpm: 6800, toRpm: 5100, seconds: 0.5, hz: 20, intoGear: 4)
            .Pull(gear: 4, fromRpm: 5100, toRpm: 6800, seconds: 4, hz: 20)
            .Build();

        PullSearchResult found = DynoRun.Find(log, Car());

        Assert.Equal(2, found.Pulls.Count);
        Assert.Equal(3, found.Pulls[0].Gearing.Gear);
        Assert.Equal(4, found.Pulls[1].Gearing.Gear);
    }

    // ----- the things that make a curve a lie -----------------------------------

    [Fact]
    public void ALiftIsFlaggedAndLocatedOnTheRevCounter()
    {
        // Reported against engine speed because that is the axis the curve is
        // drawn on: a notch is only explicable once it can be seen to line up
        // with the moment the throttle came off.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20,
                  tpsAt: p => p is > 0.55 and < 0.62 ? 78 : 98)
            .Build();

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.Contains(PullFault.ThrottleLifted, pull.Faults);
        Assert.InRange(pull.LiftAtRpm, 5200, 6200);

        // It is still offered rather than hidden. The person may well want to see
        // it, and being told why is more use than being told nothing.
        Assert.False(pull.IsClean);
    }

    [Fact]
    public void ASlippingConverterIsCaughtAndTheRoadSpeedIsUsedInstead()
    {
        // The case that makes an automatic safe. Engine speed is not proportional
        // to road speed while the converter is slipping, so working the car's
        // speed out from the tachometer is simply wrong — worst at the bottom of
        // the pull, where the slip is greatest.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20,
                  slipAt: p => 0.10 * (1 - p))
            .Build();

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.Contains(PullFault.RatioDrifted, pull.Faults);
        Assert.Equal(SpeedSource.VehicleSpeedChannel, pull.Speed);
        Assert.True(pull.Gearing.SpreadPercent > 5, $"spread was only {pull.Gearing.SpreadPercent:N1}%");
    }

    [Fact]
    public void ALockedDrivelineKeepsItsRatioAndKeepsTheBetterSignal()
    {
        // The other half of the test above: with nothing slipping, the ratio holds
        // to a fraction of a per cent and engine speed is used, because it is
        // finer grained and faster sampled than any road speed sensor.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build();

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.True(pull.Gearing.SpreadPercent < 0.5);
        Assert.Equal(SpeedSource.EngineSpeedAndGear, pull.Speed);
    }

    [Fact]
    public void AThrottleThatNeverOpenedMeansNoneOfItWasAPull()
    {
        // Brisk part-throttle acceleration makes a perfectly convincing curve,
        // and it is not a dyno pull. A throttle channel that stayed shut is
        // positive evidence of that, unlike having no channel at all.
        LogDocument log = new Recording()
            .Steady(4, 20, 1500, 8, gear: 2)
            .Pull(gear: 4, fromRpm: 2000, toRpm: 5000, seconds: 8, hz: 20, tpsAt: _ => 42)
            .Build();

        PullSearchResult found = DynoRun.Find(log, Car());

        Assert.Empty(found.Pulls);
        Assert.Contains("never went far enough open", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void OneLiftedRunAmongOpenOnesIsRejectedRatherThanOffered()
    {
        // Here the log does reach full throttle elsewhere, so the reference is
        // trustworthy and a run that never got near it can be discarded on the
        // evidence rather than on an absolute threshold.
        LogDocument log = new Recording()
            .Steady(2, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Steady(2, 20, 3000, 12, gear: 3)
            .Pull(gear: 3, fromRpm: 3000, toRpm: 6000, seconds: 6, hz: 20, tpsAt: _ => 45)
            .Build();

        PullSearchResult found = DynoRun.Find(log, Car());

        DynoPull only = Assert.Single(found.Pulls);
        Assert.Equal(4, only.Gearing.Gear);
    }

    [Fact]
    public void ARunSampledTooSlowlyIsOfferedButSaidToBeThin()
    {
        LogDocument log = new Recording()
            .Steady(4, 5, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 5)
            .Build();

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.Contains(PullFault.TooSlowlySampled, pull.Faults);
    }

    // ----- the units ------------------------------------------------------------

    [Theory]
    [InlineData("km/h")]
    [InlineData("mph")]
    public void ADeclaredSpeedUnitIsUsedAsDeclared(string units)
    {
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build(units);

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.Equal(4, pull.Gearing.Gear);
        Assert.True(pull.Gearing.ErrorPercent < 1);
    }

    [Theory]
    [InlineData("km/h")]
    [InlineData("mph")]
    public void AnUnlabelledSpeedChannelHasItsUnitWorkedOutFromTheGearing(string actual)
    {
        // Plenty of logs record a speed and never say what it is in, and the
        // values cannot settle it — seventy is ordinary in either. The gearbox
        // can: only one reading makes the ratio land on a gear the car has.
        // Getting it wrong would be a factor of 1.6 on every road speed, and so
        // on every horsepower, while looking entirely believable.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build(speedUnits: actual, label: "");

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.Equal(4, pull.Gearing.Gear);
        Assert.Equal(actual, pull.Gearing.SpeedUnit);
        Assert.True(pull.IsClean, $"faults: {string.Join(", ", pull.Faults)}");
    }

    [Fact]
    public void TwoSpeedUnitsThatBothFitAreRefusedRatherThanChosenBetween()
    {
        // Miles and kilometres an hour differ by 1.609. This gearbox steps from
        // first to second by 3.27/2.05, which is 1.595 — nine parts in a thousand
        // away. So a second-gear pull on an unlabelled mph log also fits "first
        // gear, kilometres an hour", and the gap between the two readings is
        // smaller than the error an entered tyre size routinely carries.
        //
        // Here the log is written in mph in second, and read by a vehicle whose
        // rolling deflection is half a per cent out — which is nothing, and is
        // enough. The false reading now fits *better* than the true one, so
        // picking the nearer does not merely risk the wrong answer, it returns
        // it: every road speed 1.61 times out, and effective mass taken in first
        // instead of second on top.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 2)
            .Pull(gear: 2, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build(speedUnits: "mph", label: "");

        VehicleSpec slightlyOff = Car() with { RollingDeflectionPercent = 3.5 };

        DynoPull pull = Assert.Single(DynoRun.Find(log, slightlyOff).Pulls);

        Assert.True(pull.Gearing.Ambiguous);
        Assert.False(pull.Gearing.Recognised);
        Assert.Contains(PullFault.SpeedUnitAmbiguous, pull.Faults);

        // Labelling the channel settles it, which is the remedy the fault names.
        LogDocument labelled = new Recording()
            .Steady(4, 20, 3000, 18, gear: 2)
            .Pull(gear: 2, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build(speedUnits: "mph");

        DynoPull told = Assert.Single(DynoRun.Find(labelled, slightlyOff).Pulls);

        Assert.False(told.Gearing.Ambiguous);
        Assert.Equal(2, told.Gearing.Gear);
    }

    [Fact]
    public void AnUnlabelledChannelIsStillReadWhereOnlyOneUnitFits()
    {
        // The refusal has to be narrow enough to leave the ordinary case working.
        // A fourth-gear pull has no rival reading: nothing else this gearbox does
        // is 1.609 away from fourth.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build(speedUnits: "mph", label: "");

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car() with { RollingDeflectionPercent = 3.5 }).Pulls);

        Assert.False(pull.Gearing.Ambiguous);
        Assert.Equal(4, pull.Gearing.Gear);
        Assert.Equal("mph", pull.Gearing.SpeedUnit);
    }

    [Fact]
    public void AGearboxThatDoesNotMatchTheLogIsSaidToNotMatch()
    {
        // Somebody has entered the wrong final drive, or the wrong tyre. Rather
        // than picking the nearest gear and producing a curve that is wrong by a
        // steady factor, it says the ratio matched nothing.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build();

        VehicleSpec wrong = Car() with { FinalDrive = 2.4, GearRatios = [1.0] };

        DynoPull pull = Assert.Single(DynoRun.Find(log, wrong).Pulls);

        Assert.False(pull.Gearing.Recognised);
        Assert.Contains(PullFault.GearNotRecognised, pull.Faults);
    }

    // ----- what the log does not have -------------------------------------------

    [Fact]
    public void WithNoRoadSpeedTheGearIsTakenOnTrustAndSaidToBe()
    {
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build(withSpeed: false);

        PullSearchResult found = DynoRun.Find(log, Car());
        DynoPull pull = Assert.Single(found.Pulls);

        Assert.Contains(PullFault.GearNotChecked, pull.Faults);
        Assert.Contains("taken on trust", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoThrottleTheSearchSaysWhyItCannotTellAPullFromAnAccelerationd()
    {
        LogDocument log = new Recording()
            .Steady(4, 20, 900, 3, gear: 1)
            .Pull(gear: 4, fromRpm: 2000, toRpm: 5000, seconds: 8, hz: 20, tpsAt: _ => 42)
            .Build(withThrottle: false);

        PullSearchResult found = DynoRun.Find(log, Car());

        // Without a throttle channel a part-throttle run cannot be told from a
        // full one, so it is offered rather than discarded — and the summary says
        // what could not be checked.
        Assert.NotEmpty(found.Pulls);
        Assert.Contains("no throttle channel", DynoRun.Find(
            new Recording().Steady(4, 20, 900, 3, gear: 1).Build(withThrottle: false),
            Car()).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogWithNoEngineSpeedSaysSoRatherThanReturningNothing()
    {
        var log = new LogDocument
        {
            FilePath = "x",
            Time = new LogChannel("Time", "s", 3, [0, 0.1, 0.2, 0.3], preservePrecision: true),
            Channels = [new LogChannel("CLT", "C", 0, [80, 81, 82, 83])],
            FormatName = "test",
        };

        PullSearchResult found = DynoRun.Find(log, Car());

        Assert.Empty(found.Pulls);
        Assert.Contains("no engine speed channel", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlatStretchOnTheLimiterIsNotPartOfThePull()
    {
        // The case the wide-open trim cannot catch, because the pedal is still on
        // the floor: engine speed stops climbing, so there is no acceleration
        // left to measure, and every sample of it would read as a car making
        // almost no power at all. Requiring a real rate of climb rather than
        // merely a positive one is what cuts it off — two rpm a second is
        // genuinely a rise, and genuinely nothing to measure an engine by.
        LogDocument log = new Recording()
            .Steady(2, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 6, hz: 20)
            .Limiter(gear: 4, rpm: 6800, seconds: 2.5, hz: 20)
            .Build();

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        // The pull runs from 2 s to 8 s; the limiter holds until 10.5. Ending
        // anywhere past about 8.5 means the flat stretch was measured.
        Assert.InRange(pull.EndSeconds, 7.0, 8.5);
        Assert.InRange(pull.EndRpm, 6400, 6850);
    }

    [Fact]
    public void AnIdleIsNotAPullHoweverLongItLastsFor()
    {
        // Engine speed fitted over half a second at a steady idle comes out with
        // a slope of a few parts in a quadrillion, on either side of zero. A
        // comparison against nought would glue the idle onto the front of the
        // pull that follows and take the start of the curve from there.
        LogDocument log = new Recording()
            .Steady(seconds: 30, hz: 20, rpm: 880, tps: 3, gear: 1)
            .Build();

        Assert.Empty(DynoRun.Find(log, Car()).Pulls);
    }

    [Fact]
    public void ARunShorterThanOneFittingWindowIsNotOfferedAtAll()
    {
        // Below a single window there is no derivative to be had, so there is
        // nothing to offer and nothing to caveat.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 2)
            .Pull(gear: 2, fromRpm: 3000, toRpm: 4200, seconds: 0.35, hz: 20)
            .Steady(3, 20, 4200, 4, gear: 2)
            .Build();

        PullSearchResult found = DynoRun.Find(log, Car());

        Assert.Empty(found.Pulls);
        Assert.Contains("brief", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatIsShortButUsableIsOfferedWithThatSaidAgainstIt()
    {
        // Long enough to differentiate, short enough to be worth a warning. It
        // is the person's judgement whether to trust it, and dropping it
        // silently tells them nothing — the same policy a thinly sampled run
        // already gets.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 2)
            .Pull(gear: 2, fromRpm: 3000, toRpm: 5200, seconds: 1.3, hz: 20)
            .Steady(3, 20, 5200, 4, gear: 2)
            .Build();

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.Contains(PullFault.TooShort, pull.Faults);
        Assert.Equal(2, pull.Gearing.Gear);
    }

    [Fact]
    public void ALogSampledTooSlowlyToDifferentiateSaysSoRatherThanBlamingTheDriving()
    {
        // Three samples a second leaves a half-second window with nothing either
        // side of the sample being fitted, so every slope comes back unknown and
        // no run ever looks like it is rising. Reporting that as "nothing here
        // has engine speed rising for long enough" blames the driving for a
        // property of the recording — and an OBD2 session over a dongle, which
        // this application itself produces, lands squarely in it.
        LogDocument log = new Recording()
            .Steady(4, 3, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 8, hz: 3)
            .Build();

        PullSearchResult found = DynoRun.Find(log, Car());

        Assert.Empty(found.Pulls);
        Assert.Contains("too slow to differentiate", found.Summary, StringComparison.Ordinal);
        Assert.Contains("window", found.Summary, StringComparison.Ordinal);

        // And it is a property of the window rather than of the log: widen the
        // window and the same recording yields its pull.
        PullSearchResult wider = DynoRun.Find(log, Car(), new PullSettings { WindowSeconds = 2.0 });

        Assert.Single(wider.Pulls);
    }

    [Fact]
    public void ASpeedChannelThatCarriesNothingMeansTheGearWasNotChecked()
    {
        // A car with no receiver logs its GPS speed as a flat zero all session.
        // Every sample is discarded as too slow to divide by, so there is no
        // ratio — which is not the same as a ratio that matched no gear, and
        // saying so would send somebody looking at their gearbox figures.
        var recording = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20);

        LogDocument built = recording.Build();

        var dead = new LogDocument
        {
            FilePath = built.FilePath,
            Time = built.Time,
            Channels =
            [
                .. built.Channels.Where(c => c.Name != "VSS"),
                new LogChannel("VSS", "km/h", 1, new double[built.SampleCount]),
            ],
            FormatName = built.FormatName,
        };

        DynoPull pull = Assert.Single(DynoRun.Find(dead, Car()).Pulls);

        Assert.Contains(PullFault.GearNotChecked, pull.Faults);
        Assert.DoesNotContain(PullFault.GearNotRecognised, pull.Faults);
        Assert.False(pull.Gearing.Checked);
    }

    [Fact]
    public void AThrottleLoggedAsAFractionIsStillReadAsAThrottle()
    {
        // Some firmware reports proportions as nought to one. Compared against a
        // threshold of seventy it tops out at 0.98, looks like a pedal that never
        // moved, and the whole log is confidently refused.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20)
            .Build(throttleScale: 0.01, throttleUnits: "");

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.True(pull.IsClean, $"faults: {string.Join(", ", pull.Faults)}");
        Assert.Equal(4, pull.Gearing.Gear);
    }

    [Fact]
    public void FullThrottleIsFoundOnALogThatIsMostlyOrdinaryDriving()
    {
        // The shape of a real recording, and the shape none of these tests had.
        //
        // Seven minutes of driving with one short burst in it. The burst is under
        // one per cent of the samples, so a ninety-ninth percentile lands in the
        // middle of the traffic — which is exactly what happened on the first
        // real log this met: a throttle reaching a true 100% was read as topping
        // out at 59, and the whole recording was refused as never having gone
        // near full throttle.
        //
        // The reference is now the highest reading a handful of others
        // corroborate, which resists the noisy sample the percentile was guarding
        // against without assuming the log is mostly pull.
        LogDocument log = new Recording()
            .Traffic(seconds: 380, hz: 15, gear: 3)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 4800, seconds: 3.5, hz: 15)
            .Traffic(seconds: 60, hz: 15, gear: 3)
            .Build();

        LogChannel tps = log.FindChannel("TPS")!;
        int wideOpen = Enumerable.Range(0, tps.Length).Count(i => tps.At(i) > 90);

        Assert.True(
            wideOpen < log.SampleCount / 100,
            $"the burst is {wideOpen * 100.0 / log.SampleCount:N1}% of the log, which is not the "
            + "case this test is about");

        PullSearchResult found = DynoRun.Find(log, Car());

        DynoPull pull = Assert.Single(found.Pulls);

        Assert.Equal(4, pull.Gearing.Gear);
        Assert.InRange(pull.StartRpm, 2900, 3300);
    }

    [Fact]
    public void APercentageThrottleThatNeverOpenedIsNotReadAsAFraction()
    {
        // The other side of the fraction rule, and the one it used to get exactly
        // backwards. A throttle in per cent on a log where the pedal never left
        // the bottom tops out around one — indistinguishable, by that number
        // alone, from a fraction at full throttle.
        //
        // Read as a fraction it becomes 120%, sails past the threshold, and sets
        // full throttle at 1.16% — after which every brush of the pedal is a dyno
        // pull and the log's gentlest acceleration is offered as one. The window
        // is now tight enough to tell them apart: a fraction at full throttle
        // reads within a few per cent of one, and anything above that is a
        // percentage.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 60, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 5000, seconds: 8, hz: 20, tpsAt: _ => 120)
            .Build(throttleScale: 0.01, throttleUnits: "");

        PullSearchResult found = DynoRun.Find(log, Car());

        Assert.Empty(found.Pulls);
        Assert.Contains("never went far enough open", found.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ALiftIsFoundInAFractionalThrottleToo()
    {
        // The tolerance has to be scaled along with the reference, or four per
        // cent of throttle becomes four whole units and nothing is ever a lift.
        LogDocument log = new Recording()
            .Steady(4, 20, 3000, 18, gear: 4)
            .Pull(gear: 4, fromRpm: 3000, toRpm: 6800, seconds: 7, hz: 20,
                  tpsAt: p => p is > 0.55 and < 0.62 ? 78 : 98)
            .Build(throttleScale: 0.01, throttleUnits: "");

        DynoPull pull = Assert.Single(DynoRun.Find(log, Car()).Pulls);

        Assert.Contains(PullFault.ThrottleLifted, pull.Faults);
    }

    // ----- what it says ----------------------------------------------------------

    [Fact]
    public void EveryFaultHasSomethingToSayAboutItself()
    {
        // The failure a hand-written list of messages actually has.
        foreach (PullFault fault in Enum.GetValues<PullFault>())
        {
            string said = DynoRun.Describe(fault);

            Assert.False(string.IsNullOrWhiteSpace(said));
            Assert.NotEqual(fault.ToString(), said);
        }
    }
}
