using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Finding the channel that does a job, whatever its firmware called it.
///
/// The names here are the real ones from the three controllers to hand. Anything
/// that looks for a single spelling works on one make and silently does nothing
/// on the others — which is what the suggested filters used to do, offering none
/// at all on an OBD2 log.
/// </summary>
public class ChannelRoleTests
{
    private static LogDocument Log(params (string Name, string Units, double[] Values)[] channels)
    {
        var built = channels
            .Select(c => new LogChannel(c.Name, c.Units, 2, c.Values))
            .ToList();

        return new LogDocument
        {
            FilePath = "test.csv",
            FormatName = "CSV",
            Channels = built,
            Time = new LogChannel("Time", "s", 3, [0, 1, 2], preservePrecision: true),
        };
    }

    private static double[] Moving => [10, 20, 30];

    [Theory]
    [InlineData("CLT")]                            // MegaSquirt
    [InlineData("Coolant")]                        // OBD2
    [InlineData("Coolant temp")]                   // MaxxECU
    [InlineData("coolant_temp")]
    [InlineData("Engine Coolant Temperature")]
    public void TheCoolantIsFoundHoweverItIsSpelled(string name)
    {
        LogDocument doc = Log((name, "°C", Moving));

        Assert.Equal(name, ChannelRoles.Find(doc, ChannelRole.Coolant)?.Name);
    }

    [Theory]
    [InlineData("TPS")]
    [InlineData("Throttle position")]
    [InlineData("Throttle Position")]
    [InlineData("ThrottlePos")]
    public void TheThrottleIsFoundHoweverItIsSpelled(string name)
    {
        LogDocument doc = Log((name, "%", Moving));

        Assert.Equal(name, ChannelRoles.Find(doc, ChannelRole.Throttle)?.Name);
    }

    [Fact]
    public void ASensorsVoltageIsNotItsPosition()
    {
        // A MaxxECU logs "TPS input voltage" alongside the throttle. Filtering on
        // a sensor's raw volts rather than its position throws away the wrong
        // samples while looking like it worked.
        LogDocument doc = Log(
            ("TPS input voltage", "V", Moving),
            ("Throttle position", "%", Moving));

        Assert.Equal("Throttle position", ChannelRoles.Find(doc, ChannelRole.Throttle)?.Name);
    }

    [Fact]
    public void ALoadAxisIsNotAMixture()
    {
        // A MegaSquirt log holds both "AFR" and "AFR Load", the second being the
        // axis its fuel table is drawn against. Matching anything that merely
        // starts with "AFR" picked the wrong one — and it did, until a test said
        // otherwise.
        LogDocument doc = Log(
            ("AFR Load", "", Moving),
            ("AFR", "", [13.0, 14.7, 15.2]));

        Assert.Equal("AFR", ChannelRoles.Find(doc, ChannelRole.Mixture)?.Name);
    }

    [Fact]
    public void ABankNumberIsStillTheSameChannel()
    {
        LogDocument doc = Log(("Lambda A", "", [0.9, 1.0, 1.1]));

        Assert.Equal("Lambda A", ChannelRoles.Find(doc, ChannelRole.Mixture)?.Name);
    }

    [Fact]
    public void AChannelThatNeverMovesIsStillTheRightChannel()
    {
        // Identifying it and deciding whether to filter on it are different
        // questions. Looking past a flat coolant reading finds something that is
        // not the coolant.
        LogDocument doc = Log(("CLT", "°C", [90, 90, 90]));

        Assert.Equal("CLT", ChannelRoles.Find(doc, ChannelRole.Coolant)?.Name);
        Assert.DoesNotContain(SampleFilter.Suggest(doc), f => f.Channel == "CLT");
    }

    [Fact]
    public void NothingSuitableMeansNothing()
    {
        LogDocument doc = Log(("Battery", "V", Moving), ("Oil pressure", "kPa", Moving));

        Assert.Null(ChannelRoles.Find(doc, ChannelRole.Throttle));
        Assert.Null(ChannelRoles.Find(doc, ChannelRole.Coolant));
    }

    // ----- what gets suggested -------------------------------------------------

    [Fact]
    public void AnObd2LogGetsFiltersToo()
    {
        // It got none at all: its channels are "Coolant" and "Throttle Position"
        // and the suggestions looked for "CLT" and "TPS".
        LogDocument doc = Log(
            ("RPM", "rpm", [800, 3000, 5000]),
            ("Coolant", "°C", [40, 70, 90]),
            ("Throttle Position", "%", [0, 30, 80]));

        string[] names = [.. SampleFilter.Suggest(doc).Select(f => f.Name)];

        Assert.Contains("Engine running", names);
        Assert.Contains("Up to temperature", names);
        Assert.Contains("Off idle", names);
    }

    [Fact]
    public void AMixtureRangeFollowsTheScaleItIsOn()
    {
        // Nine to twenty on a lambda channel accepts everything and filters
        // nothing, which is worse than no filter because it looks like one.
        LogDocument lambda = Log(("Lambda", "", [0.8, 1.0, 1.2]));
        LogFilter onLambda = Assert.Single(SampleFilter.Suggest(lambda), f => f.Channel == "Lambda");

        Assert.Equal(0.6, onLambda.Low);
        Assert.Equal(1.4, onLambda.High);

        LogDocument afr = Log(("AFR", "", [12.0, 14.7, 16.0]));
        LogFilter onAfr = Assert.Single(SampleFilter.Suggest(afr), f => f.Channel == "AFR");

        Assert.Equal(9, onAfr.Low);
        Assert.Equal(20, onAfr.High);
    }

    [Fact]
    public void ACutIsOfferedAsAFilterOfItsOwn()
    {
        // While the ECU is cutting fuel there is no fuelling to judge. Active in
        // 70 samples of the 6,466 in the MaxxECU log — few, and every one of
        // them meaningless.
        LogDocument doc = Log(("Fuel cut", "%", [0, 0, 100]));

        LogFilter cut = Assert.Single(SampleFilter.Suggest(doc), f => f.Name == "Not cutting fuel");

        Assert.Equal(FilterComparison.BelowOrEqual, cut.Comparison);
        Assert.Equal(0, cut.Low);
    }

    [Fact]
    public void EverySuggestionArrivesSwitchedOff()
    {
        // They are offered, not applied. Silently throwing away samples would
        // change every table on screen without anyone asking.
        LogDocument doc = Log(
            ("RPM", "rpm", [800, 3000, 5000]),
            ("CLT", "°C", [40, 70, 90]),
            ("TPS", "%", [0, 30, 80]));

        Assert.All(SampleFilter.Suggest(doc), f => Assert.False(f.Enabled));
    }

    // ----- rusEFI ------------------------------------------------------------

    /// <summary>
    /// The real names off a uaEFI board running rusEFI master.2024.11.17, with
    /// their real units, taken from the 823 channels it actually logs.
    ///
    /// Before these were added a rusEFI matched six of the twenty-one roles: no
    /// engine speed, no throttle, no manifold pressure and no mixture. The
    /// insights, the suggested filters and the VE calibration are all built on
    /// those four, so on a rusEFI log they found nothing and said so as though
    /// the log were the problem.
    /// </summary>
    private static LogDocument RusEfi() => Log(
        ("RPMValue", "RPM", [800, 3000, 5000]),
        ("coolant", "deg C", [40, 70, 90]),
        ("TPSValue", "%", [0, 30, 80]),
        ("AFRValue", "AFR", [14.7, 13.2, 12.6]),
        ("targetAFR", "ratio", [14.7, 13.0, 12.5]),
        ("MAPValue", "kPa", [35, 80, 98]),
        ("intake", "deg C", [20, 30, 40]),
        ("baroPressure", "kPa", [99, 99, 99]),
        ("actualLastInjection", "ms", [2.1, 6.4, 9.8]),
        ("injectorDutyCycle", "%", [8, 30, 60]),
        ("lowFuelPressure", "kpa", [300, 310, 300]),
        ("mafMeasured", "kg/h", [8, 90, 250]),
        ("veValue", "ratio", [45, 85, 96]),
        ("vehicleSpeedKph", "kph", [0, 40, 90]),
        ("correctedIgnitionAdvance", "deg", [12, 24, 20]),
        ("m_knockRetard", "deg", [0, 0, 2]),
        ("VBatt", "V", [13.8, 14.1, 14.0]),
        ("Gego", "%", [100, 102, 98]),
        ("fuelCutReason", "code", [0, 0, 0]),

        // The ones that must not be picked up.
        ("baseIgnitionAdvance", "deg", [14, 26, 24]),
        ("veTableYAxis", "%", [30, 70, 95]),
        ("m_knockLevel", "Volts", [0.2, 0.4, 1.1]),
        ("rawMap", "V", [0.5, 2.0, 3.9]),
        ("instantRpm", "rpm", [790, 3010, 4990]));

    [Theory]
    [InlineData(ChannelRole.EngineSpeed, "RPMValue")]
    [InlineData(ChannelRole.Coolant, "coolant")]
    [InlineData(ChannelRole.Throttle, "TPSValue")]
    [InlineData(ChannelRole.Mixture, "AFRValue")]
    [InlineData(ChannelRole.MixtureTarget, "targetAFR")]
    [InlineData(ChannelRole.ManifoldPressure, "MAPValue")]
    [InlineData(ChannelRole.IntakeAir, "intake")]
    [InlineData(ChannelRole.Barometric, "baroPressure")]
    [InlineData(ChannelRole.InjectorPulseWidth, "actualLastInjection")]
    [InlineData(ChannelRole.InjectorDuty, "injectorDutyCycle")]
    [InlineData(ChannelRole.FuelPressure, "lowFuelPressure")]
    [InlineData(ChannelRole.MassAirFlow, "mafMeasured")]
    [InlineData(ChannelRole.VolumetricEfficiency, "veValue")]
    [InlineData(ChannelRole.VehicleSpeed, "vehicleSpeedKph")]
    [InlineData(ChannelRole.KnockRetard, "m_knockRetard")]
    [InlineData(ChannelRole.BatteryVoltage, "VBatt")]
    [InlineData(ChannelRole.MixtureCorrection, "Gego")]
    [InlineData(ChannelRole.FuelCut, "fuelCutReason")]
    public void ArusEfiChannelIsFoundForItsRole(ChannelRole role, string expected) =>
        Assert.Equal(expected, ChannelRoles.Find(RusEfi(), role)?.Name);

    [Fact]
    public void TheSparkTakenIsTheCorrectedOneRatherThanTheBase()
    {
        // rusEFI logs both. The role is the timing actually commanded, which is
        // the number that decides whether it knocks; the base figure is what a
        // table asked for before any correction.
        Assert.Equal(
            "correctedIgnitionAdvance",
            ChannelRoles.Find(RusEfi(), ChannelRole.SparkAdvance)?.Name);
    }

    [Fact]
    public void TheLoadAxisIsNotMistakenForFillingEfficiency()
    {
        // "veTableYAxis" is the load a rusEFI looks the table up on, in per
        // cent, sitting right beside the efficiency itself.
        Assert.Equal("veValue", ChannelRoles.Find(RusEfi(), ChannelRole.VolumetricEfficiency)?.Name);
    }

    [Fact]
    public void AKnockSensorReadingIsNotDegreesTakenAway()
    {
        // "m_knockLevel" is volts off the sensor and sits beside the retard.
        Assert.Equal("m_knockRetard", ChannelRoles.Find(RusEfi(), ChannelRole.KnockRetard)?.Name);
    }

    [Fact]
    public void ArusEfiAnswersEveryRoleItActuallyHas()
    {
        LogDocument doc = RusEfi();

        var unmatched = Enum.GetValues<ChannelRole>()
            .Where(r => ChannelRoles.Find(doc, r) is null)
            .ToList();

        // Boost and the warmup correction, and only those. This board reports no
        // gauge pressure separately — the manifold pressure is the whole story —
        // and its warmup figure is a multiplier around one rather than a
        // percentage around a hundred, so taking it for this role would report a
        // cold engine as running 99 % lean of where it is.
        Assert.Equal([ChannelRole.WarmupCorrection, ChannelRole.Boost], unmatched);
    }

    // ----- units, after Simplify has had them ---------------------------------

    /// <summary>
    /// Every unit spelling this recognises, put through the same reduction the
    /// comparison uses.
    ///
    /// The reduction strips ":" and "/" so that a firmware's channel *names*
    /// match — "SPK: Knock retard" is invisible to every alias otherwise — but
    /// it is shared with the *unit* tables, where those characters carry
    /// meaning. Three entries were left unreachable by it: ":1" for a mixture,
    /// "g/min" and "kg/min" for a mass flow, and "m/s" for a road speed. Each
    /// read as a spelling that is accepted and was silently not.
    /// </summary>
    [Theory]
    [InlineData(ChannelRole.Mixture, "AFR", ":1")]
    [InlineData(ChannelRole.Mixture, "AFR", "ratio")]
    [InlineData(ChannelRole.MixtureTarget, "AFR Target", ":1")]
    [InlineData(ChannelRole.MassAirFlow, "MAF", "g/s")]
    [InlineData(ChannelRole.MassAirFlow, "MAF", "kg/h")]
    [InlineData(ChannelRole.MassAirFlow, "MAF", "kg/min")]
    [InlineData(ChannelRole.MassAirFlow, "MAF", "g/min")]
    [InlineData(ChannelRole.MassAirFlow, "MAF", "lb/min")]
    [InlineData(ChannelRole.VehicleSpeed, "VSS", "km/h")]
    [InlineData(ChannelRole.VehicleSpeed, "VSS", "mph")]
    [InlineData(ChannelRole.VehicleSpeed, "VSS", "m/s")]
    [InlineData(ChannelRole.ManifoldPressure, "MAP", "kPa")]
    [InlineData(ChannelRole.InjectorPulseWidth, "PW", "ms")]
    public void AUnitSpellingThisClaimsToAcceptIsActuallyAccepted(
        ChannelRole role, string name, string units)
    {
        LogDocument doc = Log((name, units, Moving));

        Assert.Equal(name, ChannelRoles.Find(doc, role)?.Name);
    }

    [Fact]
    public void AnObd2FuelTrimIsNotTakenForAClosedLoopMultiplier()
    {
        // A trim is a percentage around nought and this role is a multiplier
        // around a hundred. Reading one as the other multiplies a measured fuel
        // error by thirty and calls a healthy loop 98 % down.
        LogDocument doc = Log(("STFT", "%", [-1.5, 0, 3.0]));

        Assert.Null(ChannelRoles.Find(doc, ChannelRole.MixtureCorrection));
    }

    [Fact]
    public void ButAControllersOwnClosedLoopCorrectionStillIs()
    {
        LogDocument doc = Log(("EGO cor1", "%", [98, 100, 102]));

        Assert.Equal("EGO cor1", ChannelRoles.Find(doc, ChannelRole.MixtureCorrection)?.Name);
    }
}
