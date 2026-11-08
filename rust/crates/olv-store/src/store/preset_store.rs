//! Ported from `PresetStore.cs`. Owned by package `w1-stores`.
//!
//! Named channel selections, kept on disk so they survive restarts. Presets are matched by
//! channel name rather than index, so one saved against an MS3 log still applies to a different
//! log that shares channel names. Names that are not present are simply skipped.

use std::path::{Path, PathBuf};

use olv_core::base::json_settings_file;
use olv_core::model::eq_ignore_case;
use olv_core::{Error, Result};
use serde::{Deserialize, Serialize};

/// The longest name kept, in UTF-16 units as the C# counted it.
const MAX_NAME_LENGTH: usize = 40;

/// How many presets the store holds.
const MAX_PRESETS: usize = 100;

/// A named set of channels to plot together.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ChannelPreset {
    name: String,
    channels: Vec<String>,
}

impl ChannelPreset {
    pub fn new(name: impl Into<String>, channels: Vec<String>) -> Self {
        Self {
            name: name.into(),
            channels,
        }
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn channels(&self) -> &[String] {
        &self.channels
    }

    /// Short listing of the member channels, for tooltips.
    pub fn summary(&self) -> String {
        if self.channels.len() <= 6 {
            self.channels.join(", ")
        } else {
            format!("{}  +{} more", self.channels[..6].join(", "), self.channels.len() - 6)
        }
    }
}

#[derive(Debug)]
pub struct PresetStore {
    path: PathBuf,
    presets: Vec<ChannelPreset>,
}

/// The file as read. Hand-edited, so every property is optional and may be spelled either way.
#[derive(Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct PresetFile {
    #[serde(alias = "Presets")]
    presets: Option<Vec<Option<StoredPreset>>>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct StoredPreset {
    #[serde(alias = "Name")]
    name: Option<String>,
    #[serde(alias = "Channels")]
    channels: Option<Vec<Option<String>>>,
}

/// The file as written, in the C#'s property order.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct PresetFileOut<'a> {
    version: i32,
    presets: Vec<StoredPresetOut<'a>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct StoredPresetOut<'a> {
    name: &'a str,
    channels: &'a [String],
}

impl PresetStore {
    /// Opens the store at `path` and reads it. A missing or malformed file reads as no presets.
    pub fn new(path: PathBuf) -> Self {
        let mut store = Self {
            path,
            presets: Vec::new(),
        };
        store.reload();
        store
    }

    /// `presets.json` in the per-user application folder.
    pub fn default_path() -> PathBuf {
        json_settings_file::in_app_data("presets.json")
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn presets(&self) -> &[ChannelPreset] {
        &self.presets
    }

    /// Reads the file, tolerating absence and corruption. A malformed presets file must never
    /// stop the viewer from opening a log.
    pub fn reload(&mut self) {
        self.presets.clear();

        let Some(file) = json_settings_file::read::<PresetFile>(&self.path) else {
            return;
        };
        let Some(stored) = file.presets else {
            return;
        };

        for preset in stored.into_iter().flatten() {
            let name = clean(preset.name.as_deref());
            let Some(channels) = preset.channels.filter(|c| !c.is_empty()) else {
                continue;
            };
            if name.is_empty() {
                continue;
            }
            if self.presets.iter().any(|p| eq_ignore_case(&p.name, &name)) {
                continue;
            }

            let channels = channels
                .into_iter()
                .flatten()
                .filter(|c| !c.trim().is_empty())
                .collect();
            self.presets.push(ChannelPreset::new(name, channels));
        }
    }

    /// Adds a preset, replacing any existing one with the same name (ignoring case).
    pub fn save(&mut self, name: &str, channels: &[&str]) -> Result<ChannelPreset> {
        let clean_name = clean(Some(name));
        if clean_name.is_empty() {
            return Err(Error::Argument("A preset needs a name.".to_string()));
        }

        let mut list: Vec<String> = Vec::new();
        for channel in channels.iter().filter(|c| !c.trim().is_empty()) {
            if !list.iter().any(|kept| eq_ignore_case(kept, channel)) {
                list.push((*channel).to_string());
            }
        }
        if list.is_empty() {
            return Err(Error::Argument("A preset needs at least one channel.".to_string()));
        }

        let preset = ChannelPreset::new(clean_name.as_str(), list);
        match self.presets.iter().position(|p| eq_ignore_case(&p.name, &clean_name)) {
            Some(index) => self.presets[index] = preset.clone(),
            None if self.presets.len() < MAX_PRESETS => self.presets.push(preset.clone()),
            None => {
                return Err(Error::InvalidOperation(format!(
                    "There is a limit of {MAX_PRESETS} presets."
                )));
            }
        }

        self.persist()?;
        Ok(preset)
    }

    /// Removes the preset with this name (ignoring case). Returns whether there was one.
    pub fn delete(&mut self, name: &str) -> Result<bool> {
        let Some(index) = self.position(name) else {
            return Ok(false);
        };

        self.presets.remove(index);
        self.persist()?;
        Ok(true)
    }

    /// The preset with this name (ignoring case), if there is one.
    pub fn find(&self, name: &str) -> Option<&ChannelPreset> {
        self.position(name).map(|index| &self.presets[index])
    }

    fn position(&self, name: &str) -> Option<usize> {
        self.presets.iter().position(|p| eq_ignore_case(&p.name, name))
    }

    fn persist(&self) -> Result<()> {
        let file = PresetFileOut {
            version: 1,
            presets: self
                .presets
                .iter()
                .map(|p| StoredPresetOut {
                    name: &p.name,
                    channels: &p.channels,
                })
                .collect(),
        };

        json_settings_file::write(&self.path, &file).map_err(Error::Io)
    }
}

/// Trims, collapses inner whitespace and caps the length at [`MAX_NAME_LENGTH`] UTF-16 units.
fn clean(name: Option<&str>) -> String {
    let Some(name) = name.filter(|n| !n.trim().is_empty()) else {
        return String::new();
    };

    let collapsed = name.split_whitespace().collect::<Vec<_>>().join(" ");

    // The cap is in UTF-16 units, as the C# counted it. A character that would cross the cap is
    // left out whole rather than split.
    let mut out = String::new();
    let mut units = 0;
    for ch in collapsed.chars() {
        let width = ch.len_utf16();
        if units + width > MAX_NAME_LENGTH {
            break;
        }
        units += width;
        out.push(ch);
    }

    out.trim_end().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::store::settings_store::test_support::Scratch;
    use std::fs;

    #[test]
    fn saved_presets_survive_a_reload() {
        let scratch = Scratch::new();
        let path = scratch.join("presets.json");
        let mut store = PresetStore::new(path.clone());
        store.save("Boost tuning", &["RPM", "MAP", "Boost psi"]).unwrap();

        let reopened = PresetStore::new(path);

        assert_eq!(reopened.presets().len(), 1);
        assert_eq!(reopened.presets()[0].name(), "Boost tuning");
        assert_eq!(reopened.presets()[0].channels(), ["RPM", "MAP", "Boost psi"]);
    }

    #[test]
    fn saving_the_same_name_replaces_rather_than_duplicates() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("presets.json"));
        store.save("Idle", &["RPM"]).unwrap();
        store.save("idle", &["RPM", "CLT"]).unwrap();

        assert_eq!(store.presets().len(), 1);
        assert_eq!(store.presets()[0].channels().len(), 2);
        // The name keeps the casing it was most recently saved with.
        assert_eq!(store.presets()[0].name(), "idle");
    }

    #[test]
    fn names_are_trimmed_and_inner_whitespace_collapsed() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("presets.json"));
        store.save("  Cold   start  ", &["CLT"]).unwrap();

        assert_eq!(store.presets()[0].name(), "Cold start");
    }

    #[test]
    fn duplicate_channels_are_dropped_within_a_preset() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("presets.json"));
        store.save("Dupes", &["RPM", "rpm", "MAP"]).unwrap();

        assert_eq!(store.presets()[0].channels().len(), 2);
    }

    #[test]
    fn a_preset_needs_a_name() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("presets.json"));

        for name in ["", "   "] {
            assert!(matches!(store.save(name, &["RPM"]), Err(Error::Argument(_))));
        }
    }

    #[test]
    fn a_preset_needs_at_least_one_channel() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("presets.json"));

        assert!(matches!(store.save("Empty", &[]), Err(Error::Argument(_))));
    }

    #[test]
    fn delete_removes_a_preset_and_persists() {
        let scratch = Scratch::new();
        let path = scratch.join("presets.json");
        let mut store = PresetStore::new(path.clone());
        store.save("Gone", &["RPM"]).unwrap();
        store.save("Kept", &["MAP"]).unwrap();

        assert!(store.delete("gone").unwrap());
        assert!(!store.delete("never existed").unwrap());

        let reopened = PresetStore::new(path);
        let names: Vec<&str> = reopened.presets().iter().map(ChannelPreset::name).collect();
        assert_eq!(names, ["Kept"]);
    }

    #[test]
    fn a_corrupt_file_is_ignored_rather_than_throwing() {
        // A malformed presets file must never stop the viewer from opening a log.
        let scratch = Scratch::new();
        let path = scratch.join("presets.json");
        fs::write(&path, "{ this is not json").unwrap();

        let mut store = PresetStore::new(path.clone());

        assert!(store.presets().is_empty());
        // And the store stays usable.
        store.save("Fresh", &["RPM"]).unwrap();
        assert_eq!(PresetStore::new(path).presets().len(), 1);
    }

    #[test]
    fn a_missing_file_is_not_an_error() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("nested/deep/presets.json"));

        assert!(store.presets().is_empty());
        store.save("Works", &["RPM"]).unwrap();
        assert_eq!(store.presets().len(), 1);
    }

    #[test]
    fn hand_edited_files_load_regardless_of_casing_or_trailing_commas() {
        // The file is hand-editable, so property casing must not matter.
        let cases = [
            r#"{"version":1,"presets":[{"name":"Boost","channels":["RPM","MAP"]}]}"#,
            r#"{"Version":1,"Presets":[{"Name":"Boost","Channels":["RPM","MAP"]}]}"#,
            r#"{"presets":[{"name":"Boost","channels":["RPM","MAP"],}],}"#,
        ];

        for json in cases {
            let scratch = Scratch::new();
            let path = scratch.join("presets.json");
            fs::write(&path, json).unwrap();

            let store = PresetStore::new(path);

            assert_eq!(store.presets().len(), 1, "{json}");
            assert_eq!(store.presets()[0].name(), "Boost");
            assert_eq!(store.presets()[0].channels(), ["RPM", "MAP"]);
        }
    }

    #[test]
    fn what_is_written_can_be_read_back() {
        let scratch = Scratch::new();
        let path = scratch.join("presets.json");
        let mut store = PresetStore::new(path.clone());
        store.save("Round trip", &["RPM", "MAP"]).unwrap();

        let json = fs::read_to_string(&path).unwrap();
        assert!(json.contains("\"presets\""));

        assert_eq!(PresetStore::new(path).presets().len(), 1);
    }

    #[test]
    fn find_is_case_insensitive() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("presets.json"));
        store.save("Wide Open Throttle", &["TPS"]).unwrap();

        assert!(store.find("wide open throttle").is_some());
        assert!(store.find("something else").is_none());
    }

    #[test]
    fn long_names_are_capped() {
        let scratch = Scratch::new();
        let mut store = PresetStore::new(scratch.join("presets.json"));
        store.save(&"x".repeat(200), &["RPM"]).unwrap();

        assert!(store.presets()[0].name().len() <= 40);
    }
}
