//! The data model every other package builds on (Wave 0, `w0-model`).
//!
//! Ported from the C# files of the same names, one Rust file each. Two helpers
//! have no C# file of their own: [`IgnoreCaseMap`], for the C#'s
//! `Dictionary<string, T>(StringComparer.OrdinalIgnoreCase)`, and [`Array2`], for
//! its `T[,]`. Interfaces are traits: [`LogReader`], [`LiveSource`],
//! [`RawTelemetrySource`] and [`CanSource`].

mod array2;
mod can_frame;
mod ignore_case;
mod live_source;
mod log_channel;
mod log_document;
mod log_reader;
mod raw_telemetry_source;
mod realtime_command;
pub mod safe_xml;
pub mod trace_scale;
pub mod tune_axes;
pub mod tuning_text;
mod wire_trace;

pub use array2::Array2;
pub use can_frame::{CanFrame, CanSource, CanSourceKind};
pub use ignore_case::{IgnoreCaseMap, eq_ignore_case};
pub use live_source::LiveSource;
pub use log_channel::LogChannel;
pub use log_document::{LogDocument, LogMarker};
pub use log_reader::LogReader;
pub use raw_telemetry_source::RawTelemetrySource;
pub use realtime_command::RealtimeCommand;
pub use safe_xml::{XmlDocument, XmlElement, XmlNode};
pub use tune_axes::{TuneAxis, TuneAxisSet, TuneTable};
pub use wire_trace::{
    WireAttempt, WireEvent, WireFailureKind, WireHealthSnapshot, WireOrigin, WireOriginGuard, WireOutcome, WireTrace,
};
