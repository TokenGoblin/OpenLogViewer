//! `insights`: see `rust/packages.toml` for which package owns each file.

pub mod log_insights;

pub use log_insights::{InsightLevel, LogInsight, from as insights_from};
