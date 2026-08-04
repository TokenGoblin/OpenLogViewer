namespace OpenLogViewer.Core;

/// <summary>
/// The vertical range a trace is drawn over.
///
/// Every trace is scaled to its own range, which is what lets a dozen channels
/// with wildly different units share a plot and still be readable. It has one
/// failure, and it is the first thing a new log shows you: a sensor sitting
/// almost perfectly still gets its last decimal place stretched to the full
/// height of the lane, and a manifold pressure holding 12.0 within a tenth is
/// drawn as a wall of noise.
///
/// It is not wrong — that really is the shape of the data — but it is a bad
/// answer to the question being asked. Somebody glancing at a log wants to know
/// whether a channel moved, and "it moved by one per cent of itself" should not
/// look identical to "it swung from idle to redline".
///
/// So a range is given a floor proportional to the channel's own magnitude.
/// Proportional rather than absolute because there is no unit here: a tenth is
/// nothing on a manifold pressure in kPa and everything on a lambda reading, and
/// the only scale available is the size of the numbers themselves.
/// </summary>
public static class TraceScale
{
    /// <summary>
    /// The smallest span a trace is drawn over, as a fraction of its own centre.
    ///
    /// Five per cent, which is a judgement and worth stating as one, along with
    /// what it actually does rather than what it sounds like it does.
    ///
    /// The floor widens a range; it never replaces it, so the effect tapers rather
    /// than switching. A pressure sensor jittering 0.15 either side of 12.0 is
    /// about one per cent of itself and ends up occupying a quarter of its lane
    /// instead of all of it. A lambda wandering 0.98 to 1.02 is four per cent —
    /// just inside the threshold — and still fills four fifths of the lane, which
    /// is the point: the closer a channel gets to really moving, the less this
    /// does to it.
    /// </summary>
    public const double SteadyFraction = 0.05;

    /// <summary>
    /// The range to draw a channel over, given the range it actually has.
    ///
    /// Returns the bottom of the drawn range and its height, centred on the data
    /// either way, so a channel held steady sits in the middle of its lane rather
    /// than along the bottom.
    /// </summary>
    /// <param name="holdSteady">
    /// False draws the true range, which is the honest raw view and what somebody
    /// chasing a small drift actually wants.
    /// </param>
    public static (double Min, double Range) For(double min, double max, bool holdSteady = true)
    {
        double range = max - min;

        // Perfectly constant, or nonsense. A unit either side of the value gives
        // a line through the middle rather than a divide by zero.
        if (double.IsNaN(range) || double.IsInfinity(range) || range <= 0)
            return (min - 0.5, 1);

        if (!holdSteady) return (min, range);

        double centre = (min + max) / 2;
        double floor = Math.Abs(centre) * SteadyFraction;

        // A channel centred on zero has no magnitude to be judged against, so it
        // keeps its own range. Anything else would be picking a scale out of the
        // air for data whose size is genuinely unknown.
        if (range >= floor || floor <= 0) return (min, range);

        return (centre - (floor / 2), floor);
    }

    /// <summary>
    /// Whether a channel is being drawn over more than it actually covers.
    ///
    /// Worth knowing rather than inferring, because the plot is quietly not
    /// showing the full height of the data — and a reader who is not told will
    /// eventually be surprised by it. The interface says so beside the channel.
    /// </summary>
    public static bool IsHeldSteady(double min, double max)
    {
        double range = max - min;

        if (double.IsNaN(range) || range <= 0) return false;

        return range < Math.Abs((min + max) / 2) * SteadyFraction;
    }
}
