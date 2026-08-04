using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The vertical range a trace is drawn over.
///
/// Scaling every trace to its own range is what lets a dozen channels with
/// different units share a plot, and it has one failure that is the first thing a
/// new log shows: a sensor sitting almost still gets its last decimal stretched to
/// the full height of the lane. The real case that prompted this was a manifold
/// pressure holding 11.91 to 12.06 — a tenth and a half of movement, drawn as a
/// wall of noise indistinguishable from a channel swinging idle to redline.
/// </summary>
public class TraceScaleTests
{
    /// <summary>
    /// The reading off a real log: 12.0 kPa either side of a tenth. Held steady it
    /// occupies a quarter of its lane instead of all of it.
    /// </summary>
    [Fact]
    public void ASensorSittingStillIsNotStretchedToFillItsLane()
    {
        (double min, double range) = TraceScale.For(11.91, 12.06);

        Assert.True(TraceScale.IsHeldSteady(11.91, 12.06));

        // Five per cent of the 11.985 centre, against the 0.15 it really covers.
        Assert.Equal(11.985 * TraceScale.SteadyFraction, range, 9);
        Assert.Equal(0.25, (12.06 - 11.91) / range, 2);

        // Centred, so the trace runs through the middle of the lane rather than
        // along the bottom of it. Asserted as the property rather than as a
        // hand-rounded bound — five per cent of 11.985 is 0.59925, and writing
        // 0.6 into a test is how an arithmetic change gets hidden by a tolerance.
        Assert.Equal(11.985, min + (range / 2), 9);
    }

    /// <summary>
    /// The thing this must not break, and the reason the floor widens rather than
    /// replaces.
    ///
    /// A lambda wandering 0.98 to 1.02 is four per cent of itself — just inside
    /// the threshold, so it is nominally held — and still fills four fifths of its
    /// lane. That taper is the whole design: the closer a channel gets to really
    /// moving, the less this does to it, so there is no point at which a trace
    /// visibly snaps between two treatments.
    /// </summary>
    [Fact]
    public void SomethingBarelyInsideTheThresholdIsBarelyAffected()
    {
        (double _, double range) = TraceScale.For(0.98, 1.02);

        Assert.True((1.02 - 0.98) / range > 0.75,
            "a channel this close to the threshold should still fill most of its lane");
    }

    /// <summary>And something well clear of it is untouched entirely.</summary>
    [Fact]
    public void RealMovementIsLeftAlone()
    {
        Assert.False(TraceScale.IsHeldSteady(0.90, 1.10));

        (double min, double range) = TraceScale.For(0.90, 1.10);

        Assert.Equal(0.90, min, 6);
        Assert.Equal(0.20, range, 6);
    }

    [Fact]
    public void AChannelSwingingWidelyIsUntouched()
    {
        (double min, double range) = TraceScale.For(800, 7200);

        Assert.Equal(800, min, 6);
        Assert.Equal(6400, range, 6);
        Assert.False(TraceScale.IsHeldSteady(800, 7200));
    }

    /// <summary>
    /// The raw view is still available, because somebody chasing a small drift
    /// wants precisely the shape this hides.
    /// </summary>
    [Fact]
    public void TheTrueRangeCanStillBeAsked()
    {
        (double min, double range) = TraceScale.For(11.91, 12.06, holdSteady: false);

        Assert.Equal(11.91, min, 6);
        Assert.Equal(0.15, range, 6);
    }

    // ----- the awkward inputs ----------------------------------------------------

    /// <summary>
    /// A perfectly constant channel has no range at all. A unit either side draws
    /// it through the middle rather than dividing by zero.
    /// </summary>
    [Fact]
    public void APerfectlyConstantChannelGetsALineThroughTheMiddle()
    {
        (double min, double range) = TraceScale.For(42, 42);

        Assert.Equal(1, range, 6);
        Assert.Equal(42, min + (range / 2), 6);

        // Not "held steady" — there is nothing being hidden, it really is flat.
        Assert.False(TraceScale.IsHeldSteady(42, 42));
    }

    /// <summary>
    /// A channel centred on zero has no magnitude to be judged against, so it
    /// keeps its own range. Picking a scale for it would be inventing one for data
    /// whose size is genuinely unknown — a fuel trim swinging ±0.5% about zero is
    /// meaningful and must not be flattened.
    /// </summary>
    [Fact]
    public void AChannelAboutZeroKeepsItsOwnRange()
    {
        (double min, double range) = TraceScale.For(-0.5, 0.5);

        Assert.Equal(-0.5, min, 6);
        Assert.Equal(1, range, 6);
        Assert.False(TraceScale.IsHeldSteady(-0.5, 0.5));
    }

    /// <summary>Negative values are judged on their size, not their sign.</summary>
    [Fact]
    public void ASteadyNegativeChannelIsHeldTheSameWay()
    {
        Assert.True(TraceScale.IsHeldSteady(-20.06, -19.91));

        (double min, double range) = TraceScale.For(-20.06, -19.91);

        Assert.Equal(Math.Abs(-19.985) * TraceScale.SteadyFraction, range, 6);
        Assert.Equal(-19.985, min + (range / 2), 6);
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(1, double.NaN)]
    [InlineData(double.NegativeInfinity, double.PositiveInfinity)]
    public void NonsenseGetsALineRatherThanAnException(double min, double max)
    {
        (double _, double range) = TraceScale.For(min, max);

        Assert.False(double.IsNaN(range));
        Assert.True(range > 0);
    }

    /// <summary>
    /// A range exactly on the threshold is left alone. The floor only ever widens
    /// a range, so a trace can never be drawn over less than it covers — which
    /// would clip the data off the top of the lane.
    /// </summary>
    [Fact]
    public void TheFloorOnlyEverWidensNeverNarrows()
    {
        foreach ((double lo, double hi) in ((double, double)[])
                 [(11.91, 12.06), (0.98, 1.02), (800, 7200), (-20.06, -19.91), (0, 100)])
        {
            (double min, double range) = TraceScale.For(lo, hi);

            Assert.True(range >= hi - lo, $"{lo}..{hi} was narrowed to {range}");
            Assert.True(min <= lo, $"{lo}..{hi} would clip the bottom");
            Assert.True(min + range >= hi, $"{lo}..{hi} would clip the top");
        }
    }
}
