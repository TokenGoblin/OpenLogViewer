//! Ported from `TraceScale.cs`: the vertical range a trace is drawn over.
//!
//! Every trace is scaled to its own range, which is what lets a dozen channels
//! with wildly different units share a plot and still be readable. It has one
//! failure, and it is the first thing a new log shows you: a sensor sitting almost
//! perfectly still gets its last decimal place stretched to the full height of the
//! lane, and a manifold pressure holding 12.0 within a tenth is drawn as a wall of
//! noise.
//!
//! So a range is given a floor proportional to the channel's own magnitude.
//! Proportional rather than absolute because there is no unit here: a tenth is
//! nothing on a manifold pressure in kPa and everything on a lambda reading, and
//! the only scale available is the size of the numbers themselves.

/// The smallest span a trace is drawn over, as a fraction of its own centre.
///
/// Five per cent, a judgement. The floor widens a range; it never replaces it, so
/// the effect tapers rather than switching. A pressure sensor jittering 0.15
/// either side of 12.0 ends up occupying a quarter of its lane instead of all of
/// it. A lambda wandering 0.98 to 1.02 is four per cent, just inside the
/// threshold, and still fills four fifths of the lane.
pub const STEADY_FRACTION: f64 = 0.05;

/// The range to draw a channel over, given the range it actually has: the bottom
/// of the drawn range and its height, centred on the data either way.
///
/// `hold_steady` false draws the true range (the C#'s default is true), which is
/// the honest raw view and what somebody chasing a small drift actually wants.
pub fn range_for(min: f64, max: f64, hold_steady: bool) -> (f64, f64) {
    let range = max - min;

    // Perfectly constant, or nonsense. A unit either side of the value gives a
    // line through the middle rather than a divide by zero.
    if range.is_nan() || range.is_infinite() || range <= 0.0 {
        return (min - 0.5, 1.0);
    }

    if !hold_steady {
        return (min, range);
    }

    let centre = (min + max) / 2.0;
    let floor = centre.abs() * STEADY_FRACTION;

    // A channel centred on zero has no magnitude to be judged against, so it
    // keeps its own range.
    if range >= floor || floor <= 0.0 {
        return (min, range);
    }

    (centre - (floor / 2.0), floor)
}

/// Whether a channel is being drawn over more than it actually covers. The
/// interface says so beside the channel.
pub fn is_held_steady(min: f64, max: f64) -> bool {
    let range = max - min;

    if range.is_nan() || range <= 0.0 {
        return false;
    }

    range < ((min + max) / 2.0).abs() * STEADY_FRACTION
}

#[cfg(test)]
mod tests {
    use super::*;

    /// xunit's `Assert.Equal(expected, actual, precision)`: equal after rounding
    /// both to `places` decimals.
    fn equal_to(expected: f64, actual: f64, places: i32) {
        let k = 10f64.powi(places);
        assert_eq!(
            (expected * k).round_ties_even() / k,
            (actual * k).round_ties_even() / k,
            "{actual} is not {expected} to {places} places"
        );
    }

    /// The reading off a real log: 12.0 kPa either side of a tenth. Held steady it
    /// occupies a quarter of its lane instead of all of it.
    #[test]
    fn a_sensor_sitting_still_is_not_stretched_to_fill_its_lane() {
        let (min, range) = range_for(11.91, 12.06, true);

        assert!(is_held_steady(11.91, 12.06));

        // Five per cent of the 11.985 centre, against the 0.15 it really covers.
        equal_to(11.985 * STEADY_FRACTION, range, 9);
        equal_to(0.25, (12.06 - 11.91) / range, 2);

        // Centred, asserted as the property rather than a hand-rounded bound.
        equal_to(11.985, min + (range / 2.0), 9);
    }

    /// A lambda wandering 0.98 to 1.02 is nominally held and still fills four
    /// fifths of its lane: the taper is the whole design.
    #[test]
    fn something_barely_inside_the_threshold_is_barely_affected() {
        let (_, range) = range_for(0.98, 1.02, true);

        assert!(
            (1.02 - 0.98) / range > 0.75,
            "a channel this close to the threshold should still fill most of its lane"
        );
    }

    /// And something well clear of it is untouched entirely.
    #[test]
    fn real_movement_is_left_alone() {
        assert!(!is_held_steady(0.90, 1.10));

        let (min, range) = range_for(0.90, 1.10, true);

        equal_to(0.90, min, 6);
        equal_to(0.20, range, 6);
    }

    #[test]
    fn a_channel_swinging_widely_is_untouched() {
        let (min, range) = range_for(800.0, 7200.0, true);

        equal_to(800.0, min, 6);
        equal_to(6400.0, range, 6);
        assert!(!is_held_steady(800.0, 7200.0));
    }

    /// The raw view is still available, because somebody chasing a small drift
    /// wants precisely the shape this hides.
    #[test]
    fn the_true_range_can_still_be_asked() {
        let (min, range) = range_for(11.91, 12.06, false);

        equal_to(11.91, min, 6);
        equal_to(0.15, range, 6);
    }

    /// A perfectly constant channel has no range at all. A unit either side draws
    /// it through the middle rather than dividing by zero.
    #[test]
    fn a_perfectly_constant_channel_gets_a_line_through_the_middle() {
        let (min, range) = range_for(42.0, 42.0, true);

        equal_to(1.0, range, 6);
        equal_to(42.0, min + (range / 2.0), 6);

        // Not "held steady": there is nothing being hidden, it really is flat.
        assert!(!is_held_steady(42.0, 42.0));
    }

    /// A channel centred on zero keeps its own range: a fuel trim swinging ±0.5%
    /// about zero is meaningful and must not be flattened.
    #[test]
    fn a_channel_about_zero_keeps_its_own_range() {
        let (min, range) = range_for(-0.5, 0.5, true);

        equal_to(-0.5, min, 6);
        equal_to(1.0, range, 6);
        assert!(!is_held_steady(-0.5, 0.5));
    }

    /// Negative values are judged on their size, not their sign.
    #[test]
    fn a_steady_negative_channel_is_held_the_same_way() {
        assert!(is_held_steady(-20.06, -19.91));

        let (min, range) = range_for(-20.06, -19.91, true);

        equal_to(f64::abs(-19.985) * STEADY_FRACTION, range, 6);
        equal_to(-19.985, min + (range / 2.0), 6);
    }

    #[test]
    fn nonsense_gets_a_line_rather_than_an_exception() {
        for (min, max) in [(f64::NAN, 1.0), (1.0, f64::NAN), (f64::NEG_INFINITY, f64::INFINITY)] {
            let (_, range) = range_for(min, max, true);

            assert!(!range.is_nan());
            assert!(range > 0.0);
        }
    }

    /// The floor only ever widens a range, so a trace can never be drawn over less
    /// than it covers, which would clip the data off the top of the lane.
    #[test]
    fn the_floor_only_ever_widens_never_narrows() {
        for (lo, hi) in [
            (11.91, 12.06),
            (0.98, 1.02),
            (800.0, 7200.0),
            (-20.06, -19.91),
            (0.0, 100.0),
        ] {
            let (min, range) = range_for(lo, hi, true);

            assert!(range >= hi - lo, "{lo}..{hi} was narrowed to {range}");
            assert!(min <= lo, "{lo}..{hi} would clip the bottom");
            assert!(min + range >= hi, "{lo}..{hi} would clip the top");
        }
    }
}
