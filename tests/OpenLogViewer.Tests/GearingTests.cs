using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Gearing, speed and engine speed.
///
/// The arithmetic is a chain of ratios and easy to get right; the tyre is the
/// part that is easy to get quietly wrong, so most of what follows is about the
/// circumference rather than about the ratios.
/// </summary>
public class GearingTests
{
    // ----- the tyre ------------------------------------------------------------

    [Theory]
    [InlineData("245/40R18", 245, 40, 18)]
    [InlineData("225/45ZR17", 225, 45, 17)]
    [InlineData("P205/55R16", 205, 55, 16)]
    [InlineData("315/30-19", 315, 30, 19)]
    [InlineData(" 245 / 40 r 18 ", 245, 40, 18)]
    public void ASidewallIsReadTheWayItIsWritten(
        string sidewall, double width, double aspect, double rim)
    {
        Assert.True(Gearing.TryParseTyre(sidewall, out Tyre tyre));

        Assert.Equal(width, tyre.WidthMm);
        Assert.Equal(aspect, tyre.AspectPercent);
        Assert.Equal(rim, tyre.RimInches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("26")]
    [InlineData("not a tyre")]
    [InlineData("245/40")]
    public void AnythingElseIsRefusedRatherThanGuessedAt(string? text)
    {
        Assert.False(Gearing.TryParseTyre(text, out _));
    }

    [Fact]
    public void ATyresDiameterIsTheRimPlusASidewallAtEachEnd()
    {
        // 245/40R18: an 18 inch rim is 457.2 mm, and the sidewall is 40 per cent
        // of 245, twice. 653.2 mm, which is 25.7 inches — the figure a catalogue
        // lists for this size.
        Assert.True(Gearing.TryParseTyre("245/40R18", out Tyre tyre));

        Assert.Equal(653.2, tyre.DiameterMm, 1);
        Assert.Equal(25.72, tyre.DiameterInches, 2);
    }

    [Fact]
    public void ARollingTyreCoversLessGroundThanItsGeometrySuggests()
    {
        // It squats under the car. A 245/40R18 measures 2,052 mm around and is
        // published at about 805 revolutions per mile, which is 1,999 mm of road
        // per turn — near enough three per cent short of the geometry.
        Assert.True(Gearing.TryParseTyre("245/40R18", out Tyre tyre));

        double geometric = Math.PI * tyre.DiameterMm;
        double rolling = Gearing.RollingCircumferenceMm(tyre.DiameterMm);

        Assert.Equal(2_052, geometric, 0);
        Assert.InRange(rolling, 1_980, 2_010);

        double revsPerMile = Gearing.MmPerMile / rolling;

        Assert.InRange(revsPerMile, 795, 815);

        // And with the deflection turned off it is exactly the geometry.
        Assert.Equal(geometric, Gearing.RollingCircumferenceMm(tyre.DiameterMm, 0), 6);
    }

    // ----- speed ---------------------------------------------------------------

    [Fact]
    public void TheSpeedAgreesWithTheConstantEverybodyQuotes()
    {
        // mph = rpm × diameter in inches / (ratio × final × 336). That rule
        // assumes the tyre rolls its full geometric circumference, so it should
        // agree exactly with this one when the deflection is set to nothing —
        // which is the check, since the two are worked out quite differently.
        Assert.True(Gearing.TryParseTyre("245/40R18", out Tyre tyre));

        double circumference = Gearing.RollingCircumferenceMm(tyre.DiameterMm, 0);

        foreach ((double rpm, double ratio, double final) in
            (( double, double, double)[])[(7_000, 0.756, 3.90), (3_000, 1.0, 4.10), (6_500, 3.545, 3.42)])
        {
            double classic = rpm * tyre.DiameterInches / (ratio * final * Gearing.ClassicMphConstant);

            Assert.Equal(classic, Gearing.Mph(rpm, ratio, final, circumference), 6);
        }
    }

    [Fact]
    public void AFamiliarCarSitsWhereAFamiliarCarSits()
    {
        // Sixth at 0.756 behind a 3.90 final on a 245/40R18 — a short-geared
        // sports six-speed, near enough a Subaru's. Overall 2.95, which puts it
        // just under 2,800 rpm at seventy: busy for a motorway, and exactly what
        // owners of that gearbox complain about.
        Assert.True(Gearing.TryParseTyre("245/40R18", out Tyre tyre));

        double circumference = Gearing.RollingCircumferenceMm(tyre.DiameterMm);

        Assert.InRange(Gearing.RpmAt(70, 0.756, 3.90, circumference), 2_700, 2_850);

        // A long-legged car is geared nearer half that. Same tyre, a 0.63 top
        // and a 3.15 final is under 2,000 at the same speed.
        Assert.InRange(Gearing.RpmAt(70, 0.63, 3.15, circumference), 1_800, 1_950);
    }

    [Fact]
    public void SpeedAndEngineSpeedAreEachOthersInverse()
    {
        double circumference = Gearing.RollingCircumferenceMm(650);

        foreach (double rpm in (double[])[1_000, 3_500, 7_200])
        {
            double mph = Gearing.Mph(rpm, 1.31, 3.90, circumference);

            Assert.Equal(rpm, Gearing.RpmAt(mph, 1.31, 3.90, circumference), 6);
        }
    }

    [Fact]
    public void KilometresAndMilesDescribeTheSameSpeed()
    {
        double circumference = Gearing.RollingCircumferenceMm(650);

        double mph = Gearing.Mph(6_000, 1.0, 3.90, circumference);
        double kph = Gearing.Kph(6_000, 1.0, 3.90, circumference);

        Assert.Equal(1.609344, kph / mph, 6);
    }

    [Fact]
    public void ATallerGearIsFasterAndATallerFinalDriveIsToo()
    {
        double circumference = Gearing.RollingCircumferenceMm(650);

        // A smaller ratio is a taller gear.
        Assert.True(
            Gearing.Mph(7_000, 0.756, 3.90, circumference) >
            Gearing.Mph(7_000, 1.310, 3.90, circumference));

        // And a smaller final drive is taller again.
        Assert.True(
            Gearing.Mph(7_000, 1.0, 3.42, circumference) >
            Gearing.Mph(7_000, 1.0, 4.10, circumference));

        // A bigger tyre is worth speed at the same engine speed, which is why a
        // speedometer reads low on oversized wheels.
        Assert.True(
            Gearing.Mph(7_000, 1.0, 3.90, Gearing.RollingCircumferenceMm(700)) >
            Gearing.Mph(7_000, 1.0, 3.90, circumference));
    }

    [Fact]
    public void TheDeflectionIsWorthThreePerCentOfTheSpeedometer()
    {
        // Which is the whole reason it is an input: ignoring it reads fast, and
        // reads fast by about as much as a factory speedometer already does.
        double geometric = Gearing.RollingCircumferenceMm(653.2, 0);
        double rolling = Gearing.RollingCircumferenceMm(653.2, 3);

        double fast = Gearing.Mph(7_000, 1.0, 3.90, geometric);
        double honest = Gearing.Mph(7_000, 1.0, 3.90, rolling);

        // Three per cent off the circumference is three per cent off the speed,
        // which is 1/0.97 — a shade over three per cent — the other way. The two
        // are not the same number and the difference is the reason to write it
        // down rather than to say "about three per cent" twice.
        Assert.Equal(0.03, 1 - (honest / fast), 6);
        Assert.Equal(0.030928, (fast / honest) - 1, 6);
    }

    // ----- the table -----------------------------------------------------------

    private static readonly double[] SixSpeed = [3.545, 2.048, 1.416, 1.059, 0.848, 0.756];

    [Fact]
    public void EveryGearIsFasterThanTheOneBeforeIt()
    {
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        var table = Gearing.Table(SixSpeed, 3.90, 7_000, circumference);

        Assert.Equal(6, table.Count);

        for (int i = 1; i < table.Count; i++)
        {
            Assert.True(table[i].Mph > table[i - 1].Mph, $"gear {i + 1} was not faster");
            Assert.True(table[i].MphPerThousandRpm > table[i - 1].MphPerThousandRpm);
            Assert.Equal(i + 1, table[i].Gear);
        }
    }

    [Fact]
    public void AnUpshiftDropsTheEngineByTheRatioBetweenTheGears()
    {
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        var table = Gearing.Table(SixSpeed, 3.90, 7_000, circumference);

        // First to second on this box is a wide step, so it drops furthest.
        Assert.Equal(7_000 * 2.048 / 3.545, table[0].RpmAfterUpshift, 6);
        Assert.InRange(table[0].RpmAfterUpshift, 4_000, 4_100);

        // Fifth to sixth is a short step and barely drops it at all.
        Assert.InRange(table[4].RpmAfterUpshift, 6_200, 6_300);

        // And nothing follows top gear.
        Assert.True(double.IsNaN(table[^1].RpmAfterUpshift));

        // The drop never lands above where it came from.
        foreach (Gearing.GearStep step in table)
            if (!double.IsNaN(step.RpmAfterUpshift))
                Assert.True(step.RpmAfterUpshift < 7_000);
    }

    [Fact]
    public void TheSpeedIsUnchangedAcrossAShift()
    {
        // The check that the drop is right: the car is going one speed, and
        // taking the next gear does not change that.
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        var table = Gearing.Table(SixSpeed, 3.90, 7_000, circumference);

        for (int i = 0; i + 1 < table.Count; i++)
        {
            double after = Gearing.Mph(
                table[i].RpmAfterUpshift, SixSpeed[i + 1], 3.90, circumference);

            Assert.Equal(table[i].Mph, after, 6);
        }
    }

    [Fact]
    public void TopSpeedIsTheTallestGearAtTheRedline()
    {
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        double top = Gearing.GearedTopSpeedMph(SixSpeed, 3.90, 7_000, circumference);
        var table = Gearing.Table(SixSpeed, 3.90, 7_000, circumference);

        Assert.Equal(table[^1].Mph, top, 6);
        Assert.InRange(top, 160, 200);
    }

    [Fact]
    public void ACruisingSpeedIsReportedInEveryGearWhenOneIsAsked()
    {
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        var without = Gearing.Table(SixSpeed, 3.90, 7_000, circumference);
        var with = Gearing.Table(SixSpeed, 3.90, 7_000, circumference, cruiseMph: 70);

        Assert.All(without, s => Assert.True(double.IsNaN(s.RpmAtCruise)));

        // Seventy in top is a cruise; seventy in first is well past the redline,
        // and saying so is more use than hiding it.
        Assert.InRange(with[^1].RpmAtCruise, 2_700, 2_850);
        Assert.True(with[0].RpmAtCruise > 7_000);

        // And a lower gear always turns faster at the same road speed.
        for (int i = 1; i < with.Count; i++)
            Assert.True(with[i].RpmAtCruise < with[i - 1].RpmAtCruise);
    }

    [Fact]
    public void GearsThatWereNotEnteredAreNotInvented()
    {
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        Assert.Equal(4, Gearing.Table([3.545, 2.048, 1.416, 1.059], 3.90, 7_000, circumference).Count);
        Assert.Empty(Gearing.Table([], 3.90, 7_000, circumference));
        Assert.Empty(Gearing.Table(SixSpeed, 0, 7_000, circumference));
        Assert.Empty(Gearing.Table(SixSpeed, 3.90, 0, circumference));
    }

    [Fact]
    public void NonsenseProducesNothingRatherThanADivisionByZero()
    {
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        Assert.True(double.IsNaN(Gearing.Mph(7_000, 0, 3.90, circumference)));
        Assert.True(double.IsNaN(Gearing.Mph(7_000, 1, 0, circumference)));
        Assert.True(double.IsNaN(Gearing.Mph(7_000, 1, 3.90, 0)));
        Assert.True(double.IsNaN(Gearing.RpmAt(0, 1, 3.90, circumference)));
        Assert.True(double.IsNaN(Gearing.RollingCircumferenceMm(0)));
    }

    [Fact]
    public void TheChartHasAHeadingAndARowPerGear()
    {
        double circumference = Gearing.RollingCircumferenceMm(653.2);

        var table = Gearing.Table(SixSpeed, 3.90, 7_000, circumference, cruiseMph: 70);

        string chart = Gearing.Chart(table, withCruise: true);
        string[] lines = chart.Split(Environment.NewLine);

        Assert.Equal(table.Count + 1, lines.Length);
        Assert.Contains("gear", lines[0]);
        Assert.Contains("3.545", chart);

        // Top gear has nothing to shift into, and the chart says so rather than
        // printing a number for it.
        Assert.Contains("—", lines[^1]);
    }
}
