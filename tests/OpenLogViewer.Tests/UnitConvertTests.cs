using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Showing a reading in units someone thinks in, without changing what it means.
/// </summary>
public class UnitConvertTests
{
    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    [InlineData(90, 194)]
    public void CelsiusBecomesFahrenheit(double c, double f)
    {
        Assert.Equal(f, UnitConvert.Value(c, "°C", UnitSystem.Imperial), 6);
        Assert.Equal(c, UnitConvert.Value(f, "°F", UnitSystem.Metric), 6);
    }

    [Fact]
    public void SpeedConvertsOnTheDefinedMile()
    {
        // A mile is 1,609.344 metres exactly, so this is not an approximation.
        Assert.Equal(62.137119, UnitConvert.Value(100, "km/h", UnitSystem.Imperial), 5);
        Assert.Equal(100, UnitConvert.Value(62.137119, "mph", UnitSystem.Metric), 4);
    }

    [Fact]
    public void PressureConvertsAbsoluteToAbsolute()
    {
        // A MegaSquirt reports manifold pressure as an absolute figure, so an
        // atmosphere is 14.5 psi rather than zero boost. Turning absolute into
        // gauge silently would put a whole atmosphere of error on a boost
        // reading, which on a turbocharged engine is the difference between
        // nothing and fifteen pounds.
        Assert.Equal(14.5038, UnitConvert.Value(100, "kPa", UnitSystem.Imperial), 3);
        Assert.Equal(100, UnitConvert.Value(14.5038, "psi", UnitSystem.Metric), 3);

        Assert.Equal("psi", UnitConvert.Label("kPa", UnitSystem.Imperial));
        Assert.Equal("kPa", UnitConvert.Label("psi", UnitSystem.Metric));
    }

    [Fact]
    public void TwoBarOfBoostReadsAsThirtyPsiEitherWay()
    {
        // 200 kPa absolute is one atmosphere of boost, which a boost gauge shows
        // as about 14.5 psi and an absolute reading as 29.
        Assert.Equal(29.0075, UnitConvert.Value(200, "kPa", UnitSystem.Imperial), 3);
    }

    [Fact]
    public void ConvertingToTheSystemAReadingIsAlreadyInChangesNothing()
    {
        Assert.Equal(90, UnitConvert.Value(90, "°C", UnitSystem.Metric));
        Assert.Equal(90, UnitConvert.Value(90, "°F", UnitSystem.Imperial));
        Assert.Equal(100, UnitConvert.Value(100, "km/h", UnitSystem.Metric));
    }

    [Fact]
    public void AsReportedIsUntouched()
    {
        // The one setting that cannot be wrong, and the default for that reason.
        Assert.Equal(90, UnitConvert.Value(90, "°C", UnitSystem.AsReported));
        Assert.Equal("°C", UnitConvert.Label("°C", UnitSystem.AsReported));
    }

    [Theory]
    [InlineData("°C")]
    [InlineData("C")]
    [InlineData("deg C")]
    [InlineData("degC")]
    [InlineData("celsius")]
    public void TheSameUnitWrittenAnyOfTheUsualWaysIsRecognised(string units)
    {
        // Firmware shipped by the same people writes all of these.
        Assert.Equal(212, UnitConvert.Value(100, units, UnitSystem.Imperial), 6);
    }

    // ----- what must never be converted --------------------------------------

    [Theory]
    [InlineData("degrees")]   // ignition advance, on every ECU here
    [InlineData("°")]         // OBD2's timing advance
    [InlineData("TEMP")]      // Speeduino's placeholder for "whichever the tune says"
    [InlineData("%")]
    [InlineData("rpm")]
    [InlineData("g/s")]
    [InlineData("V")]
    [InlineData("")]
    public void AUnitThatIsNotATemperatureOrASpeedIsLeftAlone(string units)
    {
        // The dangerous direction. Reading "degrees" of ignition advance as a
        // temperature would put 32 degrees of timing on a gauge that said zero,
        // and an unfamiliar unit is a far smaller problem than a wrong number.
        Assert.Equal(0, UnitConvert.Value(0, units, UnitSystem.Imperial));
        Assert.Equal(37, UnitConvert.Value(37, units, UnitSystem.Metric));
        Assert.Equal(units, UnitConvert.Label(units, UnitSystem.Imperial));
        Assert.False(UnitConvert.Converts(units, UnitSystem.Imperial));
    }

    [Fact]
    public void NothingToReadIsStillNothingToRead()
    {
        Assert.True(double.IsNaN(UnitConvert.Value(double.NaN, "°C", UnitSystem.Imperial)));
    }

    // ----- the gauge face ----------------------------------------------------

    private static GaugeSpec Coolant() => new()
    {
        Name = "cltGauge",
        Channel = "coolant",
        Title = "Coolant Temp",
        Units = "°C",
        Low = -40,
        High = 120,
        LowDanger = -20,
        LowWarning = 0,
        HighWarning = 100,
        HighDanger = 110,
    };

    [Fact]
    public void TheWholeFaceMovesWithTheReading()
    {
        // A dial drawn in Fahrenheit keeping a redline set in Celsius would call
        // 100 degrees an emergency.
        GaugeSpec imperial = Coolant().In(UnitSystem.Imperial);

        Assert.Equal("°F", imperial.Units);
        Assert.Equal(-40, imperial.Low, 6);
        Assert.Equal(248, imperial.High, 6);
        Assert.Equal(212, imperial.HighWarning, 6);
        Assert.Equal(230, imperial.HighDanger, 6);
    }

    [Fact]
    public void AReadingFallsInTheSameBandWhicheverWayItIsShown()
    {
        // The real test of it: converting the face and the reading together must
        // not change what the gauge is saying.
        GaugeSpec metric = Coolant();
        GaugeSpec imperial = metric.In(UnitSystem.Imperial);

        foreach (double celsius in (double[])[-30, -10, 20, 90, 105, 115])
            Assert.Equal(
                metric.BandFor(celsius),
                imperial.BandFor(UnitConvert.Value(celsius, "°C", UnitSystem.Imperial)));
    }

    [Fact]
    public void AGaugeInUnitsThisDoesNotKnowIsHandedBackUntouched()
    {
        GaugeSpec advance = Coolant() with { Units = "degrees" };

        Assert.Same(advance, advance.In(UnitSystem.Imperial));
    }

    [Fact]
    public void AFacelessGaugeStaysFaceless()
    {
        // Converting zero to zero must not accidentally invent a range.
        GaugeSpec unknown = Coolant() with { Low = 0, High = 0 };

        Assert.False(unknown.In(UnitSystem.Imperial).HasScale);
    }

    // ----- the channel list and the plot -------------------------------------

    [Fact]
    public void AChannelReadsInTheChosenSystem()
    {
        var channel = new LogChannel("Coolant", "°C", 0, [0, 50, 100]);

        Assert.Equal("100 °C", channel.FormatWithUnits(100));
        Assert.Equal("212 °F", channel.FormatWithUnits(100, UnitSystem.Imperial));
        Assert.Equal("100 °C", channel.FormatWithUnits(100, UnitSystem.Metric));
    }

    [Fact]
    public void ChannelsInOtherUnitsAreUnaffectedBySwitching()
    {
        var rpm = new LogChannel("RPM", "rpm", 0, [0, 3000]);

        Assert.Equal("3000 rpm", rpm.FormatWithUnits(3000, UnitSystem.Imperial));
    }

    // ----- back the other way -----------------------------------------------

    [Theory]
    [InlineData("C", UnitSystem.Imperial, 212.0, 100.0)]
    [InlineData("C", UnitSystem.Imperial, 32.0, 0.0)]
    [InlineData("F", UnitSystem.Metric, 100.0, 212.0)]
    [InlineData("kph", UnitSystem.Imperial, 100.0, 160.9344)]
    [InlineData("mph", UnitSystem.Metric, 160.9344, 100.0)]
    [InlineData("kpa", UnitSystem.Imperial, 14.503773773020923, 100.0)]
    public void ATypedValueComesBackInTheUnitsTheLogUses(
        string units, UnitSystem from, double typed, double expected) =>
        Assert.Equal(expected, UnitConvert.ToReported(typed, units, from), precision: 6);

    [Theory]
    [InlineData("C", UnitSystem.Imperial, 87.5)]
    [InlineData("F", UnitSystem.Metric, 187.5)]
    [InlineData("kph", UnitSystem.Imperial, 137.5)]
    [InlineData("mph", UnitSystem.Metric, 87.5)]
    [InlineData("kpa", UnitSystem.Imperial, 137.5)]
    [InlineData("psi", UnitSystem.Metric, 17.5)]
    [InlineData("rpm", UnitSystem.Imperial, 4500)]
    [InlineData("", UnitSystem.Metric, 12.5)]
    public void ShowingAValueAndTypingItBackGivesTheSameNumber(
        string units, UnitSystem system, double raw)
    {
        // The round trip a pinned scale makes: seeded into the editor in the
        // units on screen, typed back, stored raw. A conversion that did not
        // invert exactly would move a pinned range every time it was reopened.
        double shown = UnitConvert.Value(raw, units, system);

        Assert.Equal(raw, UnitConvert.ToReported(shown, units, system), precision: 9);
    }

    [Fact]
    public void AsReportedConvertsNeitherWay()
    {
        Assert.Equal(90.0, UnitConvert.Value(90.0, "C", UnitSystem.AsReported));
        Assert.Equal(90.0, UnitConvert.ToReported(90.0, "C", UnitSystem.AsReported));
    }

    [Fact]
    public void AMissingReadingStaysMissingComingBack() =>
        Assert.True(double.IsNaN(UnitConvert.ToReported(double.NaN, "C", UnitSystem.Imperial)));
}
