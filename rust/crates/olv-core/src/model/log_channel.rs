//! Ported from `LogChannel.cs`.

use crate::fmt;
use crate::units::{self, UnitSystem};

/// One decoded data channel: a dense column of samples aligned with
/// [`LogDocument::time`](super::LogDocument::time).
///
/// Samples are held as 32-bit floats. No logger produces more precision than that
/// (the widest MLG field is an f32, and text logs come from short decimal
/// strings) and it halves what is by far the largest allocation: 179 channels over
/// 37,000 samples is 53 MB as doubles against 27 MB as floats.
///
/// The exception is a time base, which is cumulative. Over a long recording the
/// gap between representable floats grows past the interval between samples (at
/// ten hours it exceeds the 2.4 ms spacing of a 400 Hz logger) and time would stop
/// increasing. A channel constructed with `preserve_precision` keeps its doubles.
#[derive(Clone, Debug)]
pub struct LogChannel {
    name: String,
    units: String,
    digits: i32,
    values: Vec<f32>,
    precise: Option<Vec<f64>>,
    min: f64,
    max: f64,
    min_index: Option<usize>,
    max_index: Option<usize>,
}

impl LogChannel {
    /// A channel from doubles, narrowed to floats unless `preserve_precision`
    /// (the C# default is false), in which case the doubles are kept as well.
    pub fn new(name: &str, units: &str, digits: i32, values: &[f64], preserve_precision: bool) -> Self {
        let narrowed = values.iter().map(|&v| v as f32).collect();
        Self::build(
            name,
            units,
            digits,
            narrowed,
            preserve_precision.then(|| values.to_vec()),
        )
    }

    /// Builds a channel around a column a reader has already decoded, taking it
    /// over rather than copying. Readers use this to avoid staging a log as
    /// doubles first.
    pub fn adopt(name: &str, units: &str, digits: i32, samples: Vec<f32>) -> Self {
        Self::build(name, units, digits, samples, None)
    }

    fn build(name: &str, units: &str, digits: i32, samples: Vec<f32>, precise: Option<Vec<f64>>) -> Self {
        let mut channel = Self {
            name: name.to_string(),
            units: units.to_string(),
            digits,
            values: samples,
            precise,
            min: 0.0,
            max: 0.0,
            min_index: None,
            max_index: None,
        };

        let mut min = f64::INFINITY;
        let mut max = f64::NEG_INFINITY;

        // Measured from the stored samples, so the extremes are values the channel
        // can actually return rather than pre-rounding artefacts.
        for i in 0..channel.values.len() {
            let v = channel.at(i);
            if v.is_nan() {
                continue;
            }
            if v < min {
                min = v;
                channel.min_index = Some(i);
            }
            if v > max {
                max = v;
                channel.max_index = Some(i);
            }
        }

        if min.is_infinite() {
            min = 0.0;
            max = 0.0;
        }
        channel.min = min;
        channel.max = max;
        channel
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    /// Engineering units as declared by the logger, may be empty.
    pub fn units(&self) -> &str {
        &self.units
    }

    /// Decimal places the logger intends for display.
    pub fn digits(&self) -> i32 {
        self.digits
    }

    /// `Length`: the sample count.
    pub fn len(&self) -> usize {
        self.values.len()
    }

    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }

    /// The stored samples, for bulk reads that do not need a time base's precision.
    pub fn samples(&self) -> &[f32] {
        &self.values
    }

    pub fn min(&self) -> f64 {
        self.min
    }

    pub fn max(&self) -> f64 {
        self.max
    }

    /// Sample index of the first occurrence of [`min`](Self::min), or `None` if
    /// there is none (the C#'s -1).
    pub fn min_index(&self) -> Option<usize> {
        self.min_index
    }

    /// Sample index of the first occurrence of [`max`](Self::max), or `None`.
    pub fn max_index(&self) -> Option<usize> {
        self.max_index
    }

    /// True when every sample is identical, so the channel carries no signal.
    pub fn is_flat(&self) -> bool {
        self.max - self.min <= 0.0
    }

    /// A value in this channel's units and display precision (`F0` to `F6`), in the
    /// current culture as the C# formats it; NaN is an em dash.
    pub fn format(&self, value: f64, system: UnitSystem) -> String {
        if value.is_nan() {
            return "—".to_string();
        }
        let shown = units::value(value, &self.units, system);
        fmt::f64(shown, &format!("F{}", self.digits.clamp(0, 6)))
    }

    /// [`format`](Self::format) followed by the unit label, when there is one.
    pub fn format_with_units(&self, value: f64, system: UnitSystem) -> String {
        let shown = units::label(&self.units, system);
        if shown.is_empty() {
            self.format(value, system)
        } else {
            format!("{} {shown}", self.format(value, system))
        }
    }

    /// Sample at `index`, or NaN when out of range.
    pub fn at(&self, index: usize) -> f64 {
        if index >= self.values.len() {
            return f64::NAN;
        }
        match &self.precise {
            Some(p) => p[index],
            None => f64::from(self.values[index]),
        }
    }

    /// [`at`](Self::at) for an index the C# computes as a signed `int`, which may
    /// be negative: NaN then, as the C#'s unsigned range check gives.
    pub fn at_signed(&self, index: isize) -> f64 {
        usize::try_from(index).map_or(f64::NAN, |i| self.at(i))
    }

    /// Every sample widened to `f64` (through the time base's doubles where kept):
    /// the C# tests' `ChannelTestExtensions.ToArray`.
    pub fn to_vec(&self) -> Vec<f64> {
        (0..self.len()).map(|i| self.at(i)).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn extremes_skip_nan_and_remember_where_they_were() {
        let c = LogChannel::new("RPM", "rpm", 0, &[f64::NAN, 900.0, 3000.0, 900.0, 3000.0], false);
        assert_eq!((c.min(), c.max()), (900.0, 3000.0));
        assert_eq!((c.min_index(), c.max_index()), (Some(1), Some(2)));
        assert!(c.at(9).is_nan());
        assert!(c.at_signed(-1).is_nan());
    }

    #[test]
    fn an_empty_or_all_nan_channel_spans_zero() {
        let c = LogChannel::adopt("x", "", 1, vec![f32::NAN]);
        assert_eq!((c.min(), c.max(), c.min_index()), (0.0, 0.0, None));
        assert!(c.is_flat());
    }

    #[test]
    fn a_time_base_keeps_its_doubles() {
        let t = 36000.0025;
        let c = LogChannel::new("Time", "s", 3, &[t], true);
        assert_eq!(c.at(0), t);
        assert_ne!(LogChannel::new("Time", "s", 3, &[t], false).at(0), t);
    }

    #[test]
    fn formatting_uses_the_channel_digits_and_units() {
        let c = LogChannel::new("CLT", "°C", 1, &[90.0], false);
        assert_eq!(c.format(90.0, UnitSystem::Imperial), "194.0");
        assert_eq!(c.format_with_units(90.0, UnitSystem::Imperial), "194.0 °F");
        assert_eq!(c.format(f64::NAN, UnitSystem::Metric), "—");
    }
}
