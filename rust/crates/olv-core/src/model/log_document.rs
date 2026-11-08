//! Ported from `LogDocument.cs`.

use super::LogChannel;
use super::ignore_case::eq_ignore_case;
use std::sync::{Arc, OnceLock};
use std::time::SystemTime;

/// An annotation recorded alongside the samples, at `time` seconds.
#[derive(Clone, Debug, PartialEq)]
pub struct LogMarker {
    pub time: f64,
    pub text: String,
}

/// A fully decoded datalog: a time base plus a set of equal-length channels.
///
/// Immutable once built, as the C#'s `init`-only properties are: construct with
/// [`new`](Self::new) (the C#'s `required` members) and the `with_` methods.
/// Channels are shared (`Arc`), as C# references to them are.
#[derive(Debug)]
pub struct LogDocument {
    file_path: String,
    channels: Vec<Arc<LogChannel>>,
    time: Arc<LogChannel>,
    markers: Vec<LogMarker>,
    embedded_tune: Option<String>,
    unreadable_tune: Option<String>,
    signature: Option<String>,
    capture_info: Option<String>,
    recorded_at: Option<SystemTime>,
    format_name: String,
    median_interval: OnceLock<f64>,
}

impl LogDocument {
    pub fn new(file_path: &str, format_name: &str, channels: Vec<Arc<LogChannel>>, time: Arc<LogChannel>) -> Self {
        Self {
            file_path: file_path.to_string(),
            channels,
            time,
            markers: Vec::new(),
            embedded_tune: None,
            unreadable_tune: None,
            signature: None,
            capture_info: None,
            recorded_at: None,
            format_name: format_name.to_string(),
            median_interval: OnceLock::new(),
        }
    }

    pub fn with_markers(mut self, markers: Vec<LogMarker>) -> Self {
        self.markers = markers;
        self
    }

    pub fn with_embedded_tune(mut self, tune: Option<String>) -> Self {
        self.embedded_tune = tune;
        self
    }

    pub fn with_unreadable_tune(mut self, format: Option<String>) -> Self {
        self.unreadable_tune = format;
        self
    }

    pub fn with_signature(mut self, signature: Option<String>) -> Self {
        self.signature = signature;
        self
    }

    pub fn with_capture_info(mut self, info: Option<String>) -> Self {
        self.capture_info = info;
        self
    }

    pub fn with_recorded_at(mut self, at: Option<SystemTime>) -> Self {
        self.recorded_at = at;
        self
    }

    pub fn file_path(&self) -> &str {
        &self.file_path
    }

    pub fn channels(&self) -> &[Arc<LogChannel>] {
        &self.channels
    }

    /// The time base in seconds. Always present; synthesised from the sample index
    /// if the source file carried no usable time column.
    pub fn time(&self) -> &Arc<LogChannel> {
        &self.time
    }

    pub fn sample_count(&self) -> usize {
        self.time.len()
    }

    /// Whether a channel is the column serving as the time base.
    ///
    /// Matched by name, not only by reference. The time base is built from its own
    /// full-precision copy of the column, so it is never the same object as the
    /// entry in [`channels`](Self::channels), and a reference comparison quietly
    /// matches nothing, which is how the time column ended up offered as a
    /// plottable channel. A synthesised base is named "Sample" and matches no real
    /// channel, which is correct: there is none to exclude.
    pub fn is_time_base(&self, channel: &LogChannel) -> bool {
        std::ptr::eq(channel, Arc::as_ptr(&self.time)) || eq_ignore_case(channel.name(), self.time.name())
    }

    /// Annotations captured during logging, ordered by time.
    pub fn markers(&self) -> &[LogMarker] {
        &self.markers
    }

    /// The tune the log was recorded with, as MSQ XML, when the container embeds
    /// one. Its table axes can be used as histogram breakpoints.
    pub fn embedded_tune(&self) -> Option<&str> {
        self.embedded_tune.as_deref()
    }

    /// Set when the container carries a tune in a format this cannot read, naming
    /// the format, for saying so.
    ///
    /// Distinct from carrying no tune at all, because the two want opposite things
    /// said: a log with no tune can be given one, while telling the owner of an
    /// undecodable one to go and find a file that does not exist for their ECU is
    /// worse than saying nothing.
    pub fn unreadable_tune(&self) -> Option<&str> {
        self.unreadable_tune.as_deref()
    }

    /// ECU signature string, e.g. "MS2Extra comms342a2: MS2/Extra 3.4.2 release".
    pub fn signature(&self) -> Option<&str> {
        self.signature.as_deref()
    }

    /// Free-form capture metadata recorded by the logging application.
    pub fn capture_info(&self) -> Option<&str> {
        self.capture_info.as_deref()
    }

    /// When the log was recorded, where the file says. The C#'s
    /// `DateTimeOffset` keeps the writer's offset; nothing ported so far reads it.
    pub fn recorded_at(&self) -> Option<SystemTime> {
        self.recorded_at
    }

    /// Source format label for display, e.g. "MLG v2" or "MSL".
    pub fn format_name(&self) -> &str {
        &self.format_name
    }

    pub fn duration(&self) -> f64 {
        let count = self.sample_count();
        if count == 0 {
            0.0
        } else {
            self.time.at(count - 1) - self.time.at(0)
        }
    }

    /// Typical spacing between consecutive samples. The median rather than the
    /// mean, so a pause in logging does not skew it.
    pub fn median_sample_interval(&self) -> f64 {
        *self.median_interval.get_or_init(|| self.compute_median_interval())
    }

    /// Time step beyond which two samples are considered separated by a gap in
    /// logging rather than normal jitter. Logs are routinely paused and resumed,
    /// and drawing straight through such a gap implies data that does not exist.
    pub fn gap_threshold(&self) -> f64 {
        let median = self.median_sample_interval();
        if median > 0.0 { median * 10.0 } else { f64::INFINITY }
    }

    fn compute_median_interval(&self) -> f64 {
        let count = self.time.len();
        if count < 3 {
            return 0.0;
        }

        // Very long logs are sampled rather than measured in full; the median is
        // stable either way and this keeps loading O(1) in practice.
        let stride = ((count - 1) / 20_000).max(1);
        let mut deltas = Vec::with_capacity((count - 1) / stride + 1);

        let mut i = stride;
        while i < count {
            let delta = (self.time.at(i) - self.time.at(i - stride)) / stride as f64;
            if delta > 0.0 {
                deltas.push(delta);
            }
            i += stride;
        }

        if deltas.is_empty() {
            return 0.0;
        }
        deltas.sort_by(f64::total_cmp);
        deltas[deltas.len() / 2]
    }

    /// The first channel with this name, ignoring case.
    pub fn find_channel(&self, name: &str) -> Option<&Arc<LogChannel>> {
        self.channels.iter().find(|c| eq_ignore_case(c.name(), name))
    }

    /// Index of the sample at or just before `seconds`. Binary search; the time
    /// base is monotonic by construction.
    pub fn index_at_time(&self, seconds: f64) -> usize {
        let count = self.time.len();
        if count == 0 {
            return 0;
        }

        let (mut lo, mut hi) = (0usize, count - 1);
        while lo < hi {
            let mid = (lo + hi).div_ceil(2);
            if self.time.at(mid) <= seconds {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        lo
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn document(times: &[f64]) -> LogDocument {
        let time = Arc::new(LogChannel::new("Time", "s", 3, times, true));
        let rpm = Arc::new(LogChannel::new("RPM", "rpm", 0, &vec![1.0; times.len()], false));
        let column = Arc::new(LogChannel::new("Time", "s", 3, times, false));
        LogDocument::new("x.msl", "MSL", vec![column, rpm], time)
    }

    #[test]
    fn the_time_column_is_recognised_by_name_not_reference() {
        let doc = document(&[0.0, 0.1, 0.2]);
        assert!(doc.is_time_base(&doc.channels()[0]));
        assert!(!doc.is_time_base(&doc.channels()[1]));
        assert!(doc.is_time_base(doc.time()));
        assert!(doc.find_channel("rpm").is_some());
    }

    #[test]
    fn a_pause_does_not_move_the_median_and_marks_a_gap() {
        let doc = document(&[0.0, 0.1, 0.2, 0.3, 5.0, 5.1]);
        assert!((doc.median_sample_interval() - 0.1).abs() < 1e-9);
        assert!((doc.gap_threshold() - 1.0).abs() < 1e-9);
        assert!((doc.duration() - 5.1).abs() < 1e-9);
    }

    #[test]
    fn index_at_time_is_the_sample_at_or_before() {
        let doc = document(&[0.0, 0.1, 0.2, 0.3]);
        assert_eq!(doc.index_at_time(0.25), 2);
        assert_eq!(doc.index_at_time(-1.0), 0);
        assert_eq!(doc.index_at_time(9.0), 3);
    }
}
