//! Ported from `MsqFile.cs`. Owned by package `w2-definitions`.
//!
//! A saved tune: every setting an ECU held at the moment somebody saved it. The file
//! TunerStudio writes when you save, and the counterpart of a definition file: the INI says
//! what the settings are and where they live, and this says what they were set to.
//!
//! It carries the firmware's conditional symbols, and that is not a footnote. A definition is
//! written once and compiled many ways: the same file describes a Fahrenheit build and a
//! Celsius one, and picking the wrong branch scales every temperature wrongly while still
//! looking like a number. The tune knows which build it came from, so reading a tune's INI
//! with the tune's own symbols is the only way the two agree.
//!
//! Values are held as the file wrote them rather than parsed here. What a value means depends
//! on the constant behind it, and the constant is in the definition, which this does not have.

use crate::ini::msq_ini::SymbolSet;
use crate::model::safe_xml::{self, XmlElement};
use crate::model::{eq_ignore_case, tuning_text};
use crate::{Error, Result};
use std::collections::HashMap;
use std::path::Path;

/// `Dictionary<string, string>(StringComparer.Ordinal)`: exact keys, enumerated in insertion
/// order.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct OrdinalMap {
    entries: Vec<(String, String)>,
    index: HashMap<String, usize>,
}

impl OrdinalMap {
    /// `TryAdd`: the first one wins. Returns whether it was added.
    pub fn try_add(&mut self, key: &str, value: String) -> bool {
        if self.index.contains_key(key) {
            return false;
        }
        self.index.insert(key.to_string(), self.entries.len());
        self.entries.push((key.to_string(), value));
        true
    }

    /// Exact lookup.
    pub fn get(&self, key: &str) -> Option<&str> {
        self.index.get(key).map(|&i| self.entries[i].1.as_str())
    }

    pub fn contains_key(&self, key: &str) -> bool {
        self.index.contains_key(key)
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// `(name, value)` in the order the file wrote them.
    pub fn iter(&self) -> impl Iterator<Item = (&str, &str)> {
        self.entries.iter().map(|(k, v)| (k.as_str(), v.as_str()))
    }

    pub fn keys(&self) -> impl Iterator<Item = &str> {
        self.entries.iter().map(|(k, _)| k.as_str())
    }
}

/// A saved tune.
#[derive(Clone, Debug)]
pub struct MsqFile {
    /// The firmware signature, which says which definition this belongs to.
    pub signature: String,
    /// The firmware's own version string, as written (percent-encoded).
    pub firmware: String,
    /// Which program wrote it.
    pub author: String,
    /// Whatever the tuner typed about this tune.
    pub comment: String,
    /// When it was saved, as written. Left as text: the format is a Java date.
    pub written_at: String,
    /// How many pages the file says it holds.
    pub page_count: i32,
    /// The build's conditional symbols, for reading the definition with. Empty when the file
    /// names none, in which case a caller should keep whatever it would otherwise have used.
    pub symbols: SymbolSet,
    /// Every setting in the controller, by name, as written. Keyed exactly, not loosely:
    /// MS2Extra has both `MAFFlow` and `mafflow`, on different pages. Use [`MsqFile::value`]
    /// for a lookup that falls back to a loose match.
    pub values: OrdinalMap,
    /// Settings belonging to the tuning program rather than to the ECU.
    pub pc_variables: OrdinalMap,
}

impl Default for MsqFile {
    fn default() -> Self {
        Self {
            signature: String::new(),
            firmware: String::new(),
            author: String::new(),
            comment: String::new(),
            written_at: String::new(),
            page_count: 0,
            symbols: SymbolSet::ignore_case(Vec::<String>::new()),
            values: OrdinalMap::default(),
            pc_variables: OrdinalMap::default(),
        }
    }
}

impl MsqFile {
    /// What the file says a setting is, spelled exactly or else near enough.
    pub fn value(&self, name: &str) -> Option<&str> {
        if let Some(exact) = self.values.get(name) {
            return Some(exact);
        }

        self.values
            .iter()
            .find(|(written, _)| eq_ignore_case(written, name))
            .map(|(_, value)| value)
    }

    /// True when the file held no settings at all.
    pub fn is_empty(&self) -> bool {
        self.values.is_empty() && self.pc_variables.is_empty()
    }

    /// Reads one, or fails with [`Error::LogFormat`] for a file that is not a tune.
    pub fn read(xml: &str) -> Result<MsqFile> {
        let document = match safe_xml::parse(xml) {
            Ok(document) => document,
            Err(Error::Xml(message)) => {
                return Err(Error::LogFormat(format!("This is not readable as XML: {message}")));
            }
            Err(other) => return Err(other),
        };

        let root = document.root();
        if root.local_name() != "msq" {
            return Err(Error::LogFormat(
                "This is XML, but it is not a tune: no <msq> element.".to_string(),
            ));
        }

        let mut values = OrdinalMap::default();
        let mut pc = OrdinalMap::default();

        for element in root.descendants() {
            let Some(name) = element.attribute("name").filter(|n| !n.is_empty()) else {
                continue;
            };

            // First one wins. A file naming the same constant twice is malformed, and taking
            // the later would silently prefer whatever happened to be written last.
            match element.local_name() {
                "constant" => {
                    values.try_add(name, element.value());
                }
                "pcVariable" => {
                    pc.try_add(name, element.value());
                }
                _ => {}
            }
        }

        let version = root.descendants().find(|e| e.local_name() == "versionInfo");
        let bibliography = root.descendants().find(|e| e.local_name() == "bibliography");

        let mut symbols: Vec<String> = Vec::new();
        for setting in root.descendants().filter(|e| e.local_name() == "setting") {
            if let Some(symbol) = setting.attribute("name").filter(|n| !n.is_empty()) {
                symbols.push(symbol.to_string());
            }
        }

        let text =
            |e: Option<&XmlElement>, attribute: &str| e.and_then(|e| e.attribute(attribute)).unwrap_or("").to_string();

        Ok(MsqFile {
            signature: text(version, "signature"),
            firmware: text(version, "firmwareInfo"),
            author: text(bibliography, "author"),
            comment: text(bibliography, "tuneComment"),
            written_at: text(bibliography, "writeDate"),
            page_count: whole(version.and_then(|v| v.attribute("nPages"))),
            symbols: SymbolSet::ignore_case(symbols),
            values,
            pc_variables: pc,
        })
    }

    /// Reads one from disk, in whichever encoding it turns out to be.
    pub fn read_file(path: &Path) -> Result<MsqFile> {
        Self::read(&tuning_text::read(path)?)
    }
}

/// `int.TryParse(text, NumberStyles.Integer, InvariantCulture)`, or zero.
fn whole(text: Option<&str>) -> i32 {
    text.and_then(crate::fmt::parse_i32).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_header_values_and_symbols() {
        let xml = r#"<?xml version="1.0"?>
<msq>
  <bibliography author="me" tuneComment="hi" writeDate="today"/>
  <versionInfo signature="speeduino 202501" firmwareInfo="fw" nPages=" 15 "/>
  <page number="1">
    <constant name="a">1</constant>
    <constant name="a">2</constant>
    <constant name="MAFFlow">x</constant>
    <constant name="mafflow">y</constant>
    <pcVariable name="p">3</pcVariable>
    <constant>nameless</constant>
    <constant name="">empty</constant>
  </page>
  <setting name="LAMBDA"/>
</msq>"#;
        let msq = MsqFile::read(xml).unwrap();
        assert_eq!(msq.signature, "speeduino 202501");
        assert_eq!(msq.page_count, 15);
        assert_eq!(msq.values.get("a"), Some("1"));
        assert_eq!(msq.values.len(), 3);
        assert_eq!(msq.value("MAFFLOW"), Some("x"));
        assert_eq!(msq.value("mafflow"), Some("y"));
        assert!(msq.symbols.contains("lambda"));
        assert_eq!(msq.pc_variables.get("p"), Some("3"));
        assert!(!msq.is_empty());
    }

    #[test]
    fn refuses_what_is_not_a_tune() {
        assert!(matches!(MsqFile::read("<html/>"), Err(Error::LogFormat(m)) if m.contains("no <msq>")));
        assert!(
            matches!(MsqFile::read("<msq>"), Err(Error::LogFormat(m)) if m.starts_with("This is not readable as XML"))
        );
    }

    #[test]
    fn a_bad_page_count_is_zero() {
        let msq = MsqFile::read(r#"<msq><versionInfo nPages="x"/></msq>"#).unwrap();
        assert_eq!(msq.page_count, 0);
        assert!(msq.is_empty());
    }
}
