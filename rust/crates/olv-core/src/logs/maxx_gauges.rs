//! Ported from `MaxxGauges.cs`. Owned by package `w2-logs`.
//!
//! Gauges for a MaxxECU, from MTune's own channel definitions.
//!
//! A MaxxECU has no firmware INI, so there is nothing equivalent to the
//! `[GaugeConfigurations]` the other ECUs publish. MTune ships the next best
//! thing and arguably a better one: `ecuRealtimeDataDefinitions.xml` defines all
//! 1,856 channels with a name, a unit, a scale and limits. Taking the dial
//! ranges from there means they come from the same place the decode does rather
//! than from anything invented here.
//!
//! **Not ported:** `MaxxGauges.For`, which takes `olv_protocol::ms::MaxxChannel`.
//! `olv-protocol` depends on `olv-core`, so naming that type here would make a
//! dependency cycle. It belongs wherever both are visible; it only needs
//! [`read_limits`] from this module.

use crate::fmt;
use crate::model::{safe_xml, tuning_text};
use crate::{Error, Result};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

/// Where MTune installs its channel table.
pub const DEFAULT_PATHS: [&str; 2] = [
    r"C:\Program Files (x86)\MaxxECU MTune\ecuRealtimeDataDefinitions.xml",
    r"C:\Program Files\MaxxECU MTune\ecuRealtimeDataDefinitions.xml",
];

/// The channel table, or `None` when MTune is not installed.
pub fn find_definitions() -> Option<PathBuf> {
    DEFAULT_PATHS
        .iter()
        .map(PathBuf::from)
        .find(|p| p.try_exists().unwrap_or(false))
}

static PROBLEM: Mutex<String> = Mutex::new(String::new());

fn set_problem(text: String) {
    if let Ok(mut p) = PROBLEM.lock() {
        *p = text;
    }
}

/// Why the limits could not be read, for saying so rather than silently having
/// none. Empty when the last [`read_limits`] succeeded.
pub fn problem() -> String {
    PROBLEM.lock().map(|p| p.clone()).unwrap_or_default()
}

/// Each channel's limits by index.
///
/// No warning or danger bands: MTune's minValue and maxValue are the range a
/// channel can hold, not the range it should stay inside, and treating them as
/// thresholds would paint every reading as safe right up to the limit.
pub fn read_limits(path: Option<&Path>) -> HashMap<i32, (f64, f64)> {
    let mut limits = HashMap::new();
    set_problem(String::new());

    let Some(path) = path else {
        set_problem("MTune is not installed, so there are no ranges to draw dials over.".to_string());
        return limits;
    };

    // Read the bytes and decode them here rather than letting the parser do it.
    // The file declares Windows-1252, which the C# runtime does not carry by
    // default; the tolerant decode is the same one firmware INIs get.
    let parsed: Result<_> = tuning_text::read(path).and_then(|t| safe_xml::parse(&t));
    let document = match parsed {
        Ok(d) => d,
        Err(e @ (Error::Io(_) | Error::Xml(_))) => {
            set_problem(format!("MTune's channel definitions could not be read: {e}"));
            return limits;
        }
        Err(e) => {
            set_problem(format!("MTune's channel definitions could not be read: {e}"));
            return limits;
        }
    };

    for item in document
        .descendants()
        .filter(|e| e.local_name() == "ECURealtimeDataItem")
    {
        let Some(index) = try_number(item.attribute("index")) else {
            continue;
        };
        let Some(low) = try_number(item.attribute("minValue")) else {
            continue;
        };
        let Some(high) = try_number(item.attribute("maxValue")) else {
            continue;
        };

        if high > low {
            limits.insert(index as i32, (low, high));
        }
    }

    limits
}

fn try_number(text: Option<&str>) -> Option<f64> {
    fmt::parse_f64_inv(text?)
}
