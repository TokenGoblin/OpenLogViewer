//! Ported from `ILiveSource.cs`.

use super::RawTelemetrySource;
use crate::Result;

/// Something a live session can poll for a row of readings.
///
/// The poll loop is the same whatever is on the other end: pace it, buffer it,
/// write it to disk, notice when it stops and try to get it back. Only the way a
/// row is obtained differs: a MegaSquirt or rusEFI answers a request for a block
/// of memory, while a MaxxECU pushes frames of its own accord once it has been
/// told what to send.
///
/// `Send` because the session polls on its own thread. The C#'s `IDisposable` is
/// `Drop`.
pub trait LiveSource: Send {
    /// Channel names, in the order [`read`](Self::read) returns them.
    fn names(&self) -> &[String];

    fn units(&self) -> &[String];

    /// Display precision for each channel.
    fn digits(&self) -> &[i32];

    /// Opens the link, or fails.
    fn open(&mut self) -> Result<()>;

    /// Reads one row of values, one per name.
    ///
    /// Blocks until a reading is available or the attempt fails. An error is how a
    /// lost link is reported; the session counts failures and decides when to give
    /// up on it.
    fn read(&mut self) -> Result<Vec<f64>>;

    /// Puts a lost link back together, failing if it cannot.
    ///
    /// Called after a run of failures. Succeeding here is not enough to declare
    /// the link healthy: the session proves that with a read.
    fn recover(&mut self) -> Result<()>;

    /// Replies thrown away and asked for again, over the session.
    fn retries(&self) -> i32;

    /// The same source as a [`RawTelemetrySource`], when it is one: the C#'s
    /// `source as IRawTelemetrySource`, which a trait object cannot do by itself.
    fn raw_telemetry(&self) -> Option<&dyn RawTelemetrySource> {
        None
    }
}
