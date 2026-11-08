//! Ported from `MaxxTuneDefinitions.cs`. Owned by package `w4-live`.
//!
//! MTune's settings definitions: the file that turns a MaxxECU's tune blob from 64 KB of
//! numbers into named tables and settings.
//!
//! This is a TunerStudio INI in all but name and file format. It declares 9,975 settings,
//! each with a name, a type, a scale, the range the firmware will accept and (the part that
//! matters) a byte address in the blob. Nothing here is guessed: the addresses are the ECU's
//! own, published by the people who built it, and reading a tune with them is the same act as
//! reading a MegaSquirt with its INI.
//!
//! It ships with MTune and is absent on a machine that has never had it. That is the one thing
//! standing between a MaxxECU and its tune, which is why [`find`] is a search rather than a
//! constant.
//!
//! Two details the file will trip you on. Addresses are mostly implied: 658 of the settings
//! state one and the rest follow on from whatever came before, so the file has to be read in
//! order, from the top, with a running cursor: take only the ones that state an address and
//! everything else lands nowhere. And the whole file declares itself `Windows-1252`, which
//! .NET will not load by path without a code-page provider, so it is read as text first.

use std::path::{Path, PathBuf};

use olv_core::fmt;
use olv_core::ini::msq_ini::RealtimeType;
use olv_core::logs::maxx_gauges;
use olv_core::model::{safe_xml, tuning_text};

use super::maxx_tune::TABLE_CONFIG_SIZE;

/// One setting as MTune declares it: what it is called, where it lives and how to read it.
#[derive(Clone, Debug, PartialEq)]
pub struct MaxxSettingDefinition {
    /// MTune's own name, which is what a tuner will recognise.
    pub name: String,
    /// The declared type, verbatim; see [`is_table`](Self::is_table).
    pub kind: String,
    /// Byte offset into the tune blob.
    pub address: i32,
    /// Elements, or for a table the largest it may be. A table's real size is in the ECU
    /// rather than here, so this is a ceiling and not a shape.
    pub length: i32,
    /// Multiplies the stored number to give the value shown.
    pub scale: f64,
    /// The smallest value the firmware says this holds, or NaN.
    pub low: f64,
    /// The largest, or NaN.
    pub high: f64,
}

impl MaxxSettingDefinition {
    pub fn new(
        name: impl Into<String>,
        kind: impl Into<String>,
        address: i32,
        length: i32,
        scale: f64,
        low: f64,
        high: f64,
    ) -> Self {
        Self {
            name: name.into(),
            kind: kind.into(),
            address,
            length,
            scale,
            low,
            high,
        }
    }

    /// A table, whose axes and cells are found through the ECU rather than here.
    pub fn is_table(&self) -> bool {
        self.kind == "dynamicTable"
    }

    /// True where the stored number is signed.
    ///
    /// Taken from the declared minimum for the types that do not say. A list is written
    /// `type="list"` with no element type at all, so the only thing distinguishing an 18-point
    /// temperature curve running to -40 from one running to 65,496 is that the firmware says
    /// its minimum is below zero. Read unsigned, that curve's first four points come out at
    /// 6,513 degrees C.
    pub fn is_signed(&self) -> bool {
        match self.kind.as_str() {
            "int8" | "int16" | "int32" => true,
            "uint8" | "uint16" | "uint32" => false,
            _ => self.low < 0.0,
        }
    }

    /// Bytes one element occupies.
    pub fn element_size(&self) -> i32 {
        match self.kind.as_str() {
            "uint8" | "int8" | "string" => 1,
            "int32" | "uint32" => 4,
            _ => 2,
        }
    }

    /// What this is as the tune model spells types.
    pub fn type_(&self) -> RealtimeType {
        match (self.element_size(), self.is_signed()) {
            (1, true) => RealtimeType::S08,
            (1, false) => RealtimeType::U08,
            (4, true) => RealtimeType::S32,
            (4, false) => RealtimeType::U32,
            (_, true) => RealtimeType::S16,
            _ => RealtimeType::U16,
        }
    }

    /// Bytes the whole setting occupies in the blob's linear address space.
    ///
    /// A `dynamicTable` entry is the one exception: what sits at its address is a fixed-size
    /// config struct (how many columns and rows, where the axis and cell data actually live
    /// elsewhere in the blob), not the cells themselves, which is what
    /// [`element_size`](Self::element_size) times [`length`](Self::length) would compute
    /// instead (a table's `length` attribute states the cell grid's own dimensions, e.g.
    /// "22,22", and falls through `element_size`'s match to a default of 2, so the
    /// miscomputed size was 968 bytes for a 22x22 table against a real footprint of
    /// [`TABLE_CONFIG_SIZE`]).
    ///
    /// This matters past the one table itself: [`read`] advances its cursor by every entry's
    /// size to find the next implicit address, and two thirds of the file's settings rely on
    /// that cursor rather than stating an address of their own, so getting this wrong for one
    /// table silently mis-binds every setting after it until the next one that states its
    /// address outright.
    pub fn size(&self) -> i32 {
        if self.is_table() {
            TABLE_CONFIG_SIZE as i32
        } else {
            self.element_size().wrapping_mul(self.length)
        }
    }
}

/// Where MTune keeps it, if MTune is installed.
///
/// Beside the realtime definitions [`maxx_gauges::find_definitions`] looks for, and found the
/// same way.
pub fn find() -> Option<PathBuf> {
    let realtime = maxx_gauges::find_definitions()?;

    let beside = realtime
        .parent()
        .map(Path::to_path_buf)
        .unwrap_or_default()
        .join("ecuSettingsDefinitions.xml");

    beside.is_file().then_some(beside)
}

/// Reads the file, in order, resolving the implied addresses as it goes.
///
/// Returns every setting, in declaration order. Empty where the file is missing or
/// unreadable: a MaxxECU without MTune installed still logs, it just has no tune to show.
pub fn read(path: Option<&Path>) -> Vec<MaxxSettingDefinition> {
    let Some(path) = path else {
        return Vec::new();
    };
    if !path.is_file() {
        return Vec::new();
    }

    // IOException, UnauthorizedAccessException and XmlException are all "no definitions".
    let Ok(document) = tuning_text::read(path).and_then(|text| safe_xml::parse(&text)) else {
        return Vec::new();
    };

    let mut found = Vec::new();
    let mut cursor: i32 = 0;

    for element in document.descendants().filter(|e| e.local_name() == "ECUSetting") {
        let name = element.attribute("name").unwrap_or("");
        if name.is_empty() {
            continue;
        }

        let kind = element.attribute("type").unwrap_or("uint8");

        let definition = MaxxSettingDefinition {
            name: name.to_string(),
            kind: kind.to_string(),
            address: at(element.attribute("address"), cursor),
            length: length(element.attribute("length")),
            scale: number(element.attribute("scale")).unwrap_or(1.0),
            low: number(element.attribute("minValue")).unwrap_or(f64::NAN),
            high: number(element.attribute("maxValue")).unwrap_or(f64::NAN),
        };

        // The cursor moves by what this setting occupies whether or not it stated an address,
        // because the next one may be relying on it.
        cursor = definition.address.wrapping_add(definition.size());

        found.push(definition);
    }

    found
}

/// The stated address, or where the last one finished.
///
/// "seq" says so outright; most say nothing at all, which means the same.
fn at(address: Option<&str>, cursor: i32) -> i32 {
    address.and_then(fmt::parse_i32).unwrap_or(cursor)
}

/// How many elements. A table writes its two dimensions as "22,22", and the product is what it
/// occupies at most.
fn length(text: Option<&str>) -> i32 {
    let Some(text) = text else {
        return 1;
    };

    let mut total: i32 = 1;

    for part in text.split(',') {
        let n = fmt::parse_i32(part.trim()).filter(|&n| n > 0).unwrap_or(1);
        total = total.wrapping_mul(n);
    }

    total
}

fn number(attribute: Option<&str>) -> Option<f64> {
    attribute.and_then(fmt::parse_f64_inv)
}
