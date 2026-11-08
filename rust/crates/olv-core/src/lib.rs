//! OpenLogViewer's core: everything that is not I/O with an ECU or a window.
//!
//! Ported package by package from `src/OpenLogViewer.Core`; see `rust/PLAN.md`.
//! Each module below belongs to one package in `rust/packages.toml`, and only that
//! package's agent edits it.

pub mod base;
pub mod error;
pub mod fmt;
pub mod model;
pub mod units;

pub use error::{Error, Result};
pub mod calc;
pub mod channels;
pub mod ini;
