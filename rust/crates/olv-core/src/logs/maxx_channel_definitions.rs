//! Ported from `MaxxChannelDefinitions.cs`. Owned by package `w2-logs`.
//!
//! MTune's channel definitions, read in full.
//!
//! [`maxx_channel_table`](super::maxx_channel_table) reads the same file for
//! units alone, which is all a log needs; a USB session needs the rest. It
//! discovers its channels from the ECU rather than being told them in advance,
//! since the stream names every value by index, so it arrives holding a
//! hundred-odd numbers and nothing else, and this is what turns them into
//! "Battery voltage, volts, two decimals".
//!
//! Nothing here is invented. Where MTune is not installed the caller is left
//! with the index as the name, which is a poor label and an honest one; a guess
//! from the value would be neither.

use super::maxx_channel_table;
use crate::fmt;
use crate::model::{safe_xml, tuning_text};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

/// What MTune says one channel is.
#[derive(Clone, Debug, PartialEq)]
pub struct MaxxChannelDefinition {
    pub index: i32,
    pub name: String,
    pub units: String,
    pub scale: f64,
    pub is_signed: bool,
    pub digits: i32,
}

type Definitions = Arc<HashMap<i32, MaxxChannelDefinition>>;

static CACHE: Mutex<Option<(Option<PathBuf>, Definitions)>> = Mutex::new(None);

/// Every channel MTune defines, by index, or empty where it is not installed.
///
/// Read once per path: the file is a quarter of a megabyte and does not change
/// while the program runs.
pub fn read(path: Option<&Path>) -> Definitions {
    let mut cache = CACHE.lock().unwrap_or_else(|e| e.into_inner());
    if let Some((from, cached)) = cache.as_ref()
        && from.as_deref() == path
    {
        return Arc::clone(cached);
    }

    let parsed = Arc::new(parse(path));
    *cache = Some((path.map(Path::to_path_buf), Arc::clone(&parsed)));
    parsed
}

fn parse(path: Option<&Path>) -> HashMap<i32, MaxxChannelDefinition> {
    let mut found = HashMap::new();

    let Some(path) = path else {
        return found;
    };

    // Read and decode here rather than by path: the file declares Windows-1252.
    // No definitions is a smaller loss than refusing to connect.
    let Ok(document) = tuning_text::read(path).and_then(|t| safe_xml::parse(&t)) else {
        return found;
    };

    for item in document
        .descendants()
        .filter(|e| e.local_name() == "ECURealtimeDataItem")
    {
        let Some(index) = item.attribute("index").and_then(fmt::parse_i32) else {
            continue;
        };

        let name = item.attribute("name").unwrap_or("");
        if name.is_empty() {
            continue;
        }

        let mut units = match item.attribute("unit") {
            Some(u) => u,
            None => maxx_channel_table::unit_for_class(item.attribute("unitClass")),
        };

        // Some units are a placeholder the tune fills in, which is worse than
        // nothing on an axis.
        if units.starts_with('{') {
            units = "";
        }

        let scale = number(item.attribute("scale"), 1.0);

        // "int16" is signed and "uint16" is not, and the one contains the other,
        // so the test has to be for the u, not for the int.
        let ty = item.attribute("type").unwrap_or("");
        let is_signed = !ty.starts_with('u');

        found.insert(
            index,
            MaxxChannelDefinition {
                index,
                name: name.to_string(),
                units: units.to_string(),
                scale,
                is_signed,
                digits: digits_for(scale),
            },
        );
    }

    found
}

/// How many decimals a channel's scale implies: 0.01 is two, 1 is none.
///
/// Taken from the scale rather than chosen, because the scale is exactly the
/// statement of how much resolution the value has. Showing more is inventing
/// precision the ECU did not send.
pub fn digits_for(mut scale: f64) -> i32 {
    if scale <= 0.0 || scale >= 1.0 {
        return 0;
    }

    let mut digits = 0;
    while scale < 1.0 && digits < 6 {
        scale *= 10.0;
        digits += 1;
    }
    digits
}

fn number(text: Option<&str>, fallback: f64) -> f64 {
    text.and_then(fmt::parse_f64_inv).unwrap_or(fallback)
}
