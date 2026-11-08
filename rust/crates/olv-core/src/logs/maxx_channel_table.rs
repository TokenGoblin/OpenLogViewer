//! Ported from `MaxxChannelTable.cs`. Owned by package `w2-logs`.
//!
//! MTune's channel definitions, keyed by the index a MaxxECU uses everywhere.
//!
//! The same file the live gauges take their ranges from. A log header names its
//! channels but gives no units; the index in brackets after each name is what
//! this is keyed by, so "Coolant temp [18]" can be labelled deg C from the
//! definitions rather than from a guess about the word "temp".

use super::maxx_gauges;
use crate::fmt;
use crate::model::{safe_xml, tuning_text};
use std::collections::HashMap;
use std::sync::OnceLock;

static UNITS: OnceLock<HashMap<i32, String>> = OnceLock::new();

/// Units by channel index, empty when MTune is not installed.
///
/// Read once: the file is a quarter of a megabyte and never changes while the
/// program is running.
pub fn units() -> &'static HashMap<i32, String> {
    UNITS.get_or_init(|| {
        let mut units = HashMap::new();
        let Some(path) = maxx_gauges::find_definitions() else {
            return units;
        };

        // Parsed from a decoded string rather than by path: the file declares
        // Windows-1252.
        // No units is a smaller loss than refusing to open the log.
        let Ok(document) = tuning_text::read(&path).and_then(|t| safe_xml::parse(&t)) else {
            return units;
        };

        for item in document
            .descendants()
            .filter(|e| e.local_name() == "ECURealtimeDataItem")
        {
            let Some(index) = item.attribute("index").and_then(fmt::parse_i32) else {
                continue;
            };

            let unit = match item.attribute("unit") {
                Some(u) => u,
                None => unit_for_class(item.attribute("unitClass")),
            };

            // Some are written as a placeholder the tune fills in, which is
            // worse than nothing on an axis.
            if !unit.is_empty() && !unit.starts_with('{') {
                units.insert(index, unit.to_string());
            }
        }

        units
    })
}

/// Units for the classes MTune names rather than spells out.
///
/// Temperature, pressure and speed are given as a class because MTune displays
/// them in whichever units the user has chosen. These are the ones the logs are
/// actually written in.
pub fn unit_for_class(unit_class: Option<&str>) -> &'static str {
    match unit_class {
        Some("temp") => "deg C",
        Some("abspressure") => "kPa",
        Some("pressure") => "kPa",
        Some("speed") => "km/h",
        _ => "",
    }
}
