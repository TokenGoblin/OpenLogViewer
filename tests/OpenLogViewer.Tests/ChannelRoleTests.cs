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
}
