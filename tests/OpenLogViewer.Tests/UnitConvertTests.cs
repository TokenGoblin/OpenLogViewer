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
    [InlineData("kPa")]
    [InlineData("rpm")]
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
}
