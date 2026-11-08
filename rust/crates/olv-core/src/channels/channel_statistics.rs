//! Ported from `ChannelStatistics.cs`. Owned by package `w1-channels`.
//!
//! Summary of one channel over a span of samples.
//!
//! The question a tuner asks of a marked region is "what did this do here": how hot did it
//! get, how lean did it go, what did it average. A cursor gives one instant; this gives the
//! span.

use crate::model::LogChannel;

/// Min, max, mean and count of the readings in a span.
#[derive(Clone, Copy, Debug)]
pub struct ChannelStatistics {
    pub min: f64,
    pub max: f64,
    pub mean: f64,
    pub count: i32,
}

/// `double.Equals`: NaN equals NaN, as the C# record struct's equality does.
fn same(a: f64, b: f64) -> bool {
    a == b || (a.is_nan() && b.is_nan())
}

impl PartialEq for ChannelStatistics {
    fn eq(&self, other: &Self) -> bool {
        same(self.min, other.min)
            && same(self.max, other.max)
            && same(self.mean, other.mean)
            && self.count == other.count
    }
}

impl ChannelStatistics {
    pub const EMPTY: ChannelStatistics = ChannelStatistics {
        min: f64::NAN,
        max: f64::NAN,
        mean: f64::NAN,
        count: 0,
    };

    pub fn new(min: f64, max: f64, mean: f64, count: i32) -> Self {
        Self { min, max, mean, count }
    }

    pub fn has_data(&self) -> bool {
        self.count > 0
    }

    /// Difference between the extremes, or NaN when there is no data.
    pub fn span(&self) -> f64 {
        if self.count > 0 { self.max - self.min } else { f64::NAN }
    }

    /// Summarises samples `first..=last` inclusive. Missing readings are skipped rather than
    /// counted as zero, which would drag an average toward nothing.
    pub fn over(channel: &LogChannel, first: i32, last: i32) -> ChannelStatistics {
        let length = channel.len() as i64;
        let (first, last) = (i64::from(first), i64::from(last));
        let from = 0.max(first.min(last));
        let to = (length - 1).min(first.max(last));

        if length == 0 || from > to {
            return Self::EMPTY;
        }

        let (mut min, mut max, mut sum) = (f64::INFINITY, f64::NEG_INFINITY, 0.0);
        let mut count = 0;

        for i in from..=to {
            let v = channel.at(i as usize);
            if v.is_nan() {
                continue;
            }

            if v < min {
                min = v;
            }
            if v > max {
                max = v;
            }
            sum += v;
            count += 1;
        }

        if count == 0 {
            Self::EMPTY
        } else {
            Self::new(min, max, sum / f64::from(count), count)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn channel(values: &[f64]) -> LogChannel {
        LogChannel::new("RPM", "RPM", 1, values, false)
    }

    #[test]
    fn summarises_the_requested_span_only() {
        let rpm = channel(&[1000.0, 2000.0, 3000.0, 4000.0, 5000.0]);

        let stats = ChannelStatistics::over(&rpm, 1, 3);

        assert_eq!(stats.min, 2000.0);
        assert_eq!(stats.max, 4000.0);
        assert_eq!(stats.mean, 3000.0);
        assert_eq!(stats.count, 3);
        assert_eq!(stats.span(), 2000.0);
    }

    #[test]
    fn missing_readings_are_skipped_not_counted_as_zero() {
        // Treating a gap as zero would drag the mean toward nothing.
        let rpm = channel(&[1000.0, f64::NAN, 3000.0]);

        let stats = ChannelStatistics::over(&rpm, 0, 2);

        assert_eq!(stats.count, 2);
        assert_eq!(stats.mean, 2000.0);
        assert_eq!(stats.min, 1000.0);
    }

    #[test]
    fn a_backwards_span_is_read_the_same_way_round() {
        // Dragging right to left must give the same answer.
        let rpm = channel(&[1000.0, 2000.0, 3000.0, 4000.0]);

        assert_eq!(ChannelStatistics::over(&rpm, 1, 3), ChannelStatistics::over(&rpm, 3, 1));
    }

    #[test]
    fn the_span_is_clamped_to_the_channel() {
        let rpm = channel(&[1000.0, 2000.0, 3000.0]);

        let stats = ChannelStatistics::over(&rpm, -50, 500);

        assert_eq!(stats.count, 3);
        assert_eq!(stats.min, 1000.0);
        assert_eq!(stats.max, 3000.0);
    }

    #[test]
    fn a_single_sample_is_its_own_summary() {
        let rpm = channel(&[1000.0, 2500.0, 3000.0]);

        let stats = ChannelStatistics::over(&rpm, 1, 1);

        assert_eq!(stats.count, 1);
        assert_eq!(stats.min, 2500.0);
        assert_eq!(stats.max, 2500.0);
        assert_eq!(stats.mean, 2500.0);
        assert_eq!(stats.span(), 0.0);
    }

    #[test]
    fn all_missing_readings_reports_no_data() {
        let rpm = channel(&[f64::NAN, f64::NAN]);

        let stats = ChannelStatistics::over(&rpm, 0, 1);

        assert!(!stats.has_data());
        assert_eq!(stats.count, 0);
        assert!(stats.mean.is_nan());
    }

    #[test]
    fn an_empty_channel_reports_no_data() {
        assert!(!ChannelStatistics::over(&channel(&[]), 0, 10).has_data());
    }

    #[test]
    fn the_mean_is_not_skewed_by_order() {
        let rising = channel(&[10.0, 20.0, 30.0, 40.0]);
        let falling = channel(&[40.0, 30.0, 20.0, 10.0]);

        assert_eq!(
            ChannelStatistics::over(&rising, 0, 3).mean,
            ChannelStatistics::over(&falling, 0, 3).mean
        );
    }
}
