//! Ported from `ILogReader.cs`. The C#'s `LogFormatException` is
//! [`Error::LogFormat`](crate::Error::LogFormat).

use super::LogDocument;
use crate::Result;
use std::path::Path;

/// One log file format. Readers hold no state between files, so they are shared
/// across threads.
pub trait LogReader: Send + Sync {
    /// Human-readable format name used in the UI.
    fn format_name(&self) -> &str;

    /// Sniffs content (not just the extension) to decide if this reader applies.
    fn can_read(&self, path: &Path) -> bool;

    /// Reads the whole file. A file this reader refuses is
    /// [`Error::LogFormat`](crate::Error::LogFormat); one it cannot open is
    /// [`Error::Io`](crate::Error::Io).
    fn read(&self, path: &Path) -> Result<LogDocument>;
}
