//! Ported from `Smoothing.cs`: smoothing a channel for the eye, without touching what it holds.
//!
//! **This is a way of drawing, not a way of measuring.** A smoothed AFR hides exactly the
//! single-sample lean excursion that damages a piston, so nothing that judges an engine may
//! see a smoothed number: the insights, the VE calibration, the histogram, the statistics
//! and every export read the channel as logged. What smoothing changes is the line on the
//! plot and the figure read off it.
//!
//! **A median, not an average.** Sensor noise arrives as spikes, and a mean smears each
//! spike across the whole window. A median throws the spike away and leaves the level and,
//! importantly, the edges: a step survives a median and is rounded off by a mean.
//!
//! Counted in samples rather than in seconds, deliberately. Noise of this kind is per
//! reading, whether they arrive at 1 Hz or 50.

/// How hard to smooth a trace, in the terms a person picks from.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SmoothingLevel {
    /// As logged.
    None = 0,
    /// Five samples: takes the fuzz off without moving anything.
    Light = 1,
    /// Fifteen: a noisy sensor becomes a readable line.
    Medium = 2,
    /// Fifty-one: the shape only, for a channel that is mostly noise.
    Strong = 3,
}

/// How many samples each level takes the median of.
pub fn window(level: SmoothingLevel) -> i32 {
    match level {
        SmoothingLevel::Light => 5,
        SmoothingLevel::Medium => 15,
        SmoothingLevel::Strong => 51,
        SmoothingLevel::None => 1,
    }
}

/// What to call a level on a menu.
pub fn name(level: SmoothingLevel) -> &'static str {
    match level {
        SmoothingLevel::Light => "Light",
        SmoothingLevel::Medium => "Medium",
        SmoothingLevel::Strong => "Strong",
        SmoothingLevel::None => "None",
    }
}

/// A moving median of the values, the same length as the input.
///
/// The window shrinks at the two ends rather than being padded, so the first and last
/// samples are the median of what there is either side of them. A padded end invents data
/// and bends the line towards whatever was invented.
///
/// A sample that is itself missing (NaN) stays missing, so the pen still lifts across a gap
/// in the log. Missing samples inside a window are passed over rather than counted.
pub fn median(values: &[f64], window: i32) -> Vec<f64> {
    if window <= 1 {
        return values.to_vec();
    }

    let mut smoothed = vec![0.0; values.len()];
    let half = (window / 2) as usize;
    // The span scanned runs from i - half to i + half: window + 1 samples for an even window.
    let mut buffer: Vec<f64> = Vec::with_capacity((window as usize + 1).min(values.len() + 1));

    for i in 0..values.len() {
        if values[i].is_nan() {
            smoothed[i] = f64::NAN;
            continue;
        }

        let from = i.saturating_sub(half);
        let to = (values.len() - 1).min(i.saturating_add(half));

        buffer.clear();
        buffer.extend(values[from..=to].iter().copied().filter(|v| !v.is_nan()));
        let count = buffer.len();

        if count == 0 {
            smoothed[i] = f64::NAN;
            continue;
        }

        buffer.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));

        smoothed[i] = if count % 2 == 1 {
            buffer[count / 2]
        } else {
            (buffer[(count / 2) - 1] + buffer[count / 2]) / 2.0
        };
    }

    smoothed
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The whole reason this is a median. An average of five would pull every one of the
    /// five samples around the spike a fifth of the way towards it.
    #[test]
    fn a_single_spike_is_removed_rather_than_spread() {
        let flat = [10.0, 10.0, 10.0, 100.0, 10.0, 10.0, 10.0];
        assert_eq!(median(&flat, 5), vec![10.0; 7]);
    }

    /// A median keeps an edge where an average makes a ramp of it.
    #[test]
    fn a_step_survives_rather_than_being_rounded_off() {
        let step = [0.0, 0.0, 0.0, 0.0, 0.0, 10.0, 10.0, 10.0, 10.0, 10.0];
        let smoothed = median(&step, 5);
        assert_eq!(smoothed[3], 0.0);
        assert_eq!(smoothed[6], 10.0);
    }

    /// Padding an end bends the line towards whatever was invented, at exactly the places
    /// people read a trace's start and finish.
    #[test]
    fn the_window_shrinks_at_the_ends_rather_than_inventing_data() {
        let rising = [1.0, 2.0, 3.0, 4.0, 5.0];
        let smoothed = median(&rising, 5);
        assert_eq!(smoothed[0], 2.0); // the median of 1, 2, 3
        assert_eq!(smoothed[smoothed.len() - 1], 4.0); // the median of 3, 4, 5
    }

    #[test]
    fn a_missing_sample_stays_missing_so_the_pen_still_lifts() {
        let gapped = [10.0, 10.0, f64::NAN, 10.0, 10.0];
        let smoothed = median(&gapped, 3);
        assert!(smoothed[2].is_nan());
        assert_eq!(smoothed[0], 10.0);
    }

    #[test]
    fn missing_samples_inside_a_window_thin_the_evidence_rather_than_poison_it() {
        let mostly_gone = [f64::NAN, f64::NAN, 7.0, f64::NAN, f64::NAN];
        assert_eq!(median(&mostly_gone, 5)[2], 7.0);
    }

    #[test]
    fn no_smoothing_leaves_every_sample_where_it_was() {
        let noisy = [3.0, 100.0, 3.0, 100.0, 3.0];
        assert_eq!(median(&noisy, 1), noisy.to_vec());
        assert_eq!(median(&noisy, window(SmoothingLevel::None)), noisy.to_vec());
    }

    /// Noise of this kind is per reading, so a window stated in time smooths nothing on a
    /// 1 Hz log and destroys a 50 Hz one.
    #[test]
    fn the_levels_are_counted_in_samples_rather_than_seconds() {
        assert_eq!(window(SmoothingLevel::None), 1);
        assert_eq!(window(SmoothingLevel::Light), 5);
        assert_eq!(window(SmoothingLevel::Medium), 15);
        assert_eq!(window(SmoothingLevel::Strong), 51);
    }

    #[test]
    fn an_even_window_takes_the_mean_of_the_middle_pair() {
        let values = [1.0, 2.0, 3.0, 4.0];
        assert!((median(&values, 4)[1] - 2.5).abs() < 5e-7);
    }

    /// The one that matters. A smoothed AFR hides exactly the single-sample lean
    /// excursion that damages a piston, so nothing that judges an engine may ever see
    /// one. Smoothing produces a new series; this pins that the log itself is never
    /// rewritten.
    #[test]
    fn the_insights_read_the_channel_as_logged_whatever_is_smoothed() {
        use crate::model::{LogChannel, LogDocument};
        use std::sync::Arc;

        let n = 200;
        let afr: Vec<f64> = (0..n).map(|i| if i == 100 { 17.5 } else { 13.5 }).collect();
        let time: Vec<f64> = (0..n).map(|i| f64::from(i) * 0.1).collect();
        let flat = |v: f64| vec![v; n as usize];

        let log = LogDocument::new(
            "spike.mlg",
            "test",
            vec![
                Arc::new(LogChannel::new("RPM", "RPM", 2, &flat(3000.0), false)),
                Arc::new(LogChannel::new("MAP", "kPa", 2, &flat(150.0), false)),
                Arc::new(LogChannel::new("AFR", "AFR", 2, &afr, false)),
                Arc::new(LogChannel::new("AFR Target 1", "AFR", 2, &flat(13.5), false)),
                Arc::new(LogChannel::new("CLT", "°F", 2, &flat(190.0), false)),
            ],
            Arc::new(LogChannel::new("Time", "s", 2, &time, false)),
        );

        // The spike is one sample in two hundred: a median of five erases it.
        assert_eq!(median(&afr, 5)[100], 13.5);

        // And the log still holds it, so anything measuring finds it.
        assert_eq!(log.find_channel("AFR").unwrap().at(100), 17.5);
    }

    /// The scratch buffer was sized to the window, but the span scanned is window + 1
    /// samples; every even window threw on any input long enough to reach a full one.
    #[test]
    fn an_even_window_does_not_run_off_the_end_of_its_buffer() {
        for w in [2, 4, 6, 16] {
            let values: Vec<f64> = (0..40).map(f64::from).collect();
            let smoothed = median(&values, w);
            assert_eq!(smoothed.len(), values.len());
            assert!(smoothed.iter().all(|v| !v.is_nan()));
        }
    }
}
