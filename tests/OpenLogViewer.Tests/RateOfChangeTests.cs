using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Differentiating a logged channel.
///
/// The tests that matter here are the ones about noise and about spacing, since
/// those are the two ways the naive answer is wrong and the two reasons this
/// class exists at all. The exactness tests come first only because a method
/// that cannot differentiate a straight line has no business being handed a real
/// log.
/// </summary>
public class RateOfChangeTests
{
    private static (double[] Values, double[] Times) Sample(
        Func<double, double> f, double from, double to, double hz)
    {
        var times = new List<double>();
        var values = new List<double>();

        for (double t = from; t <= to + 1e-9; t += 1 / hz)
        {
            times.Add(t);
            values.Add(f(t));
        }

        return ([.. values], [.. times]);
    }

    /// <summary>Samples away from the ends, where the window is two-sided.</summary>
    private static IEnumerable<int> Interior(int count, double hz, double window)
    {
        int edge = (int)Math.Ceiling(window / 2 * hz) + 1;

        for (int i = edge; i < count - edge; i++) yield return i;
    }

    // ----- it has to get the easy ones exactly right ----------------------------

    [Fact]
    public void AStraightLineHasTheSlopeItWasBuiltWith()
    {
        (double[] v, double[] t) = Sample(x => 3 + (7 * x), 0, 5, 20);

        ChannelFit fit = RateOfChange.Fit(v, t, 0.5);

        // Every sample, including the two ends: a one-sided fit to a straight
        // line is still exactly that line.
        for (int i = 0; i < v.Length; i++)
        {
            Assert.Equal(7, fit.SlopePerSecond[i], 9);
            Assert.Equal(v[i], fit.Value[i], 9);
        }
    }

    [Fact]
    public void ACurveIsDifferentiatedExactlyBecauseTheFitIsACurve()
    {
        // A straight line fitted to this would have its slope pulled towards the
        // chord — which is the flattening that would take the peak off a dyno
        // curve. A quadratic fit to a quadratic is exact everywhere.
        (double[] v, double[] t) = Sample(x => 2 + (3 * x) + (4 * x * x), 0, 5, 20);

        ChannelFit fit = RateOfChange.Fit(v, t, 0.5);

        foreach (int i in Interior(v.Length, 20, 0.5))
        {
            Assert.Equal(3 + (8 * t[i]), fit.SlopePerSecond[i], 6);
        }
    }

    // ----- the reason it exists -------------------------------------------------

    [Fact]
    public void QuantisedSpeedIsDifferentiatedFarBetterThanBySubtraction()
    {
        // A road speed sensor reporting whole miles an hour, on a car
        // accelerating at a steady 5 m/s². One mile an hour is 0.447 m/s, and at
        // 20 Hz subtracting one reading from the next turns that step into nine
        // metres per second squared of acceleration that never happened.
        const double step = 0.44704;
        const double truth = 5;
        const double hz = 20;

        (double[] v, double[] t) = Sample(x => Math.Round((10 + (truth * x)) / step) * step, 0, 6, hz);

        double[] fitted = RateOfChange.PerSecond(v, t, 0.5);

        double naive = 0, fine = 0;
        int counted = 0;

        foreach (int i in Interior(v.Length, hz, 0.5))
        {
            naive += Math.Abs(((v[i + 1] - v[i]) / (t[i + 1] - t[i])) - truth);
            fine += Math.Abs(fitted[i] - truth);
            counted++;
        }

        naive /= counted;
        fine /= counted;

        Assert.True(fine < naive / 5, $"fitted was off by {fine:N3}, differencing by {naive:N3}");
        Assert.True(fine < 0.8, $"fitted was off by {fine:N3} m/s²");
    }

    [Fact]
    public void UnevenTimestampsGiveTheSameAnswerAsEvenOnes()
    {
        // The failure this class was written to avoid. Precomputed coefficients
        // that assume even spacing, handed the jitter a real log has, return a
        // derivative scaled by whatever the spacing really was.
        Func<double, double> f = x => 12 + (4 * x) + (0.5 * x * x);

        (double[] even, double[] tEven) = Sample(f, 0, 6, 25);

        var tJittered = new double[tEven.Length];
        var jittered = new double[tEven.Length];

        for (int i = 0; i < tEven.Length; i++)
        {
            // ±30% of the interval, which is worse than most logs and not much
            // worse than a controller under load.
            tJittered[i] = tEven[i] + (0.012 * Math.Sin(i * 2.4));
            jittered[i] = f(tJittered[i]);
        }

        double[] a = RateOfChange.PerSecond(even, tEven, 0.5);
        double[] b = RateOfChange.PerSecond(jittered, tJittered, 0.5);

        foreach (int i in Interior(even.Length, 25, 0.5))
        {
            Assert.Equal(4 + tEven[i], a[i], 5);
            Assert.Equal(4 + tJittered[i], b[i], 5);
        }
    }

    [Fact]
    public void TheSameCarAtTwoSampleRatesMakesTheSameAcceleration()
    {
        // The reason the window is stated in seconds. A window counted in samples
        // would look at half a second of road at 10 Hz and a tenth of a second at
        // 50, and the two logs would disagree about the same car.
        Func<double, double> f = x => 20 + (6 * x) - (0.4 * x * x);

        (double[] slow, double[] tSlow) = Sample(f, 0, 6, 10);
        (double[] fast, double[] tFast) = Sample(f, 0, 6, 50);

        double[] a = RateOfChange.PerSecond(slow, tSlow, 0.5);
        double[] b = RateOfChange.PerSecond(fast, tFast, 0.5);

        // At three seconds in, which both logs carry.
        int i = Array.FindIndex(tSlow, x => Math.Abs(x - 3) < 1e-9);
        int j = Array.FindIndex(tFast, x => Math.Abs(x - 3) < 1e-9);

        Assert.Equal(6 - (0.8 * 3), a[i], 5);
        Assert.Equal(a[i], b[j], 5);
    }

    // ----- holes in the log -----------------------------------------------------

    [Fact]
    public void AWindowDoesNotReachAcrossAGap()
    {
        // The gap has to be shorter than the window for this to test anything —
        // a hole longer than the window is out of reach whether or not anybody
        // checks for it, which is how the first version of this test managed to
        // pass with the check removed.
        //
        // So: a third of a second missing, inside a window of one second, with a
        // different slope and a jump in value either side. Fitting through it
        // would draw a line across the part nobody recorded and report its slope
        // with the same confidence as any other.
        var times = new List<double>();
        var values = new List<double>();

        for (double t = 0; t <= 1.0001; t += 0.05)
        {
            times.Add(t);
            values.Add(t * 2);
        }

        for (double t = 1.35; t <= 2.3501; t += 0.05)
        {
            times.Add(t);
            values.Add(50 + ((t - 1.35) * 20));
        }

        int last = times.FindLastIndex(x => x <= 1.0001);

        double[] fenced = RateOfChange.PerSecond(
            [.. values], [.. times], windowSeconds: 1.0, gapThreshold: 0.15);

        Assert.Equal(2, fenced[last], 6);
        Assert.Equal(20, fenced[last + 1], 6);

        // And with no threshold set, the fit does reach across — which is what
        // makes the assertions above worth making.
        double[] unfenced = RateOfChange.PerSecond(
            [.. values], [.. times], windowSeconds: 1.0, gapThreshold: 0);

        Assert.True(
            unfenced[last] > 20,
            $"without the fence the slope at the edge was {unfenced[last]:N1}, not the nonsense it should have been");
    }

    [Fact]
    public void AMissingReadingStaysMissingAndDoesNotPoisonItsNeighbours()
    {
        (double[] v, double[] t) = Sample(x => 5 * x, 0, 4, 20);

        int hole = 40;
        v[hole] = double.NaN;

        ChannelFit fit = RateOfChange.Fit(v, t, 0.5);

        Assert.True(double.IsNaN(fit.SlopePerSecond[hole]));
        Assert.True(double.IsNaN(fit.Value[hole]));

        // The samples either side are fitted from what is there, with the hole
        // passed over rather than counted as a zero.
        Assert.Equal(5, fit.SlopePerSecond[hole - 1], 6);
        Assert.Equal(5, fit.SlopePerSecond[hole + 1], 6);
    }

    [Fact]
    public void TimeRunningBackwardsBreaksTheRunRatherThanFittingThroughIt()
    {
        // A log stitched from two sessions. Without the break, the end of the
        // first would be fitted against the start of the second.
        double[] times = [0, 0.1, 0.2, 0.3, 0.0, 0.1, 0.2, 0.3];
        double[] values = [0, 1, 2, 3, 50, 55, 60, 65];

        double[] slope = RateOfChange.PerSecond(times: times, values: values, windowSeconds: 0.5);

        Assert.Equal(10, slope[3], 6);
        Assert.Equal(50, slope[4], 6);
    }

    // ----- edges ----------------------------------------------------------------

    [Fact]
    public void AnEmptyChannelFitsToNothingRatherThanThrowing()
    {
        ChannelFit fit = RateOfChange.Fit([], [], 0.5);

        Assert.Empty(fit.Value);
        Assert.Empty(fit.SlopePerSecond);
    }

    [Fact]
    public void OneSampleHasNoSlopeToReport()
    {
        ChannelFit fit = RateOfChange.Fit([4], [0], 0.5);

        Assert.True(double.IsNaN(fit.SlopePerSecond[0]));
    }

    [Fact]
    public void AChannelAndItsTimeBaseMustBeTheSameLength()
    {
        Assert.Throws<ArgumentException>(() => RateOfChange.Fit([1, 2, 3], [0, 1], 0.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void TheWindowHasToBeAPositiveTime(double window)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RateOfChange.Fit([1, 2, 3], [0, 1, 2], window));
    }
}
