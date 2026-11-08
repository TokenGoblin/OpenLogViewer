//! The error type every `olv-core` function returns, and the one the crates above
//! it wrap or reuse.
//!
//! One variant per exception the C# throws or catches by type, so a port can map
//! `throw new X(...)` and `catch (X)` one to one:
//!
//! | C#                                                       | Rust                     |
//! |----------------------------------------------------------|--------------------------|
//! | `IOException`, `FileNotFoundException`                   | [`Error::Io`]            |
//! | `LogFormatException` (a log this code refuses)           | [`Error::LogFormat`]     |
//! | `FormatException` (text that is not a number, a bad format string) | [`Error::Format`] |
//! | `XmlException`                                           | [`Error::Xml`]           |
//! | `JsonException`                                          | [`Error::Json`]          |
//! | `MathExpressionException`                                | [`Error::Expression`]    |
//! | `EcuProtocolException`                                   | [`Error::Protocol`]      |
//! | `TimeoutException`                                       | [`Error::Timeout`]       |
//! | `ArgumentException` and its `Null`/`OutOfRange` kinds    | [`Error::Argument`]      |
//! | `InvalidOperationException`, `ObjectDisposedException`   | [`Error::InvalidOperation`] |
//!
//! The message is the C#'s message, where a test or a person reads it.

use std::fmt;

/// `Result` with [`Error`] as the default error.
pub type Result<T, E = Error> = std::result::Result<T, E>;

/// Everything that can go wrong in `olv-core`. See the module docs for the
/// mapping from the C#'s exceptions.
#[derive(Debug)]
#[non_exhaustive]
pub enum Error {
    /// Reading or writing a file or a device failed.
    Io(std::io::Error),
    /// A log file that is not in the format its reader expected.
    LogFormat(String),
    /// Text that is not a number, or a format string .NET would refuse.
    Format(String),
    /// XML that is not well-formed, or that `SafeXml` refuses.
    Xml(String),
    /// JSON that does not deserialize.
    Json(String),
    /// A math expression that does not parse; `position` is a character offset.
    Expression { message: String, position: usize },
    /// An ECU answered with something the protocol does not allow.
    Protocol(String),
    /// Nothing came back in time.
    Timeout(String),
    /// A caller passed an argument the function does not accept.
    Argument(String),
    /// The call is not valid in the object's current state.
    InvalidOperation(String),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Io(e) => write!(f, "{e}"),
            Error::LogFormat(m)
            | Error::Format(m)
            | Error::Xml(m)
            | Error::Json(m)
            | Error::Protocol(m)
            | Error::Timeout(m)
            | Error::Argument(m)
            | Error::InvalidOperation(m) => f.write_str(m),
            Error::Expression { message, position } => {
                write!(f, "{message} (at {position})")
            }
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Io(e) => Some(e),
            _ => None,
        }
    }
}

impl From<std::io::Error> for Error {
    fn from(e: std::io::Error) -> Self {
        Error::Io(e)
    }
}
