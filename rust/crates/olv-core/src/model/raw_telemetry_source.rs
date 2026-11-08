//! Ported from `IRawTelemetrySource.cs`.

use std::sync::Arc;

/// Implemented by a [`LiveSource`](super::LiveSource) that decodes more than it
/// normally reports, and offered through its
/// [`raw_telemetry`](super::LiveSource::raw_telemetry).
///
/// `TunerStudioSource` decodes a firmware's entire realtime block but only ever
/// hands `read` the channels the firmware's own datalog definition names, a
/// human-curated subset. This is a second, optional view onto the same poll, for
/// whatever wants everything the firmware actually publishes, including a field
/// the datalog author never thought to log.
pub trait RawTelemetrySource {
    /// Every channel the firmware's `[OutputChannels]` section defines.
    fn raw_names(&self) -> &[String];

    fn raw_units(&self) -> &[String];

    /// The full decoded block from the most recent read, or `None` before the
    /// first one. Replaced wholesale each poll rather than mutated, so a reader on
    /// another thread never sees a half-written frame: hence a shared slice.
    fn last_raw_frame(&self) -> Option<Arc<[f64]>>;
}
