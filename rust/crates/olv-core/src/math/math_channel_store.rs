//! Ported from `MathChannelStore.cs`. Owned by package `w2-math-channels`.
//!
//! Persists the user's calculated channels. Definitions reference channels by name, so a set
//! written against one log applies to any other that carries those names.
//!
//! The file is `math.json` in the application data folder, `{ "version": 1, "channels": [...] }`
//! with camel-case property names, exactly as the C# app writes and reads it.

use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use super::math_channel::MathChannel;
use crate::base::json_settings_file;
use crate::model::eq_ignore_case;
use crate::{Error, Result};

const MAX_CHANNELS: usize = 60;

#[derive(Serialize, Deserialize, Default)]
struct MathFile {
    #[serde(default, alias = "Version")]
    version: i32,
    #[serde(default, alias = "Channels")]
    channels: Option<Vec<MathChannel>>,
}

pub struct MathChannelStore {
    path: PathBuf,
    channels: Vec<MathChannel>,
}

impl MathChannelStore {
    /// Opens the store at `path`, or at `math.json` in the application data folder.
    pub fn new(path: Option<&Path>) -> Self {
        let path = path
            .map(Path::to_path_buf)
            .unwrap_or_else(|| json_settings_file::in_app_data("math.json"));
        let mut store = Self {
            path,
            channels: Vec::new(),
        };
        store.reload();
        store
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn channels(&self) -> &[MathChannel] {
        &self.channels
    }

    pub fn reload(&mut self) {
        self.channels.clear();

        let Some(file) = json_settings_file::read::<MathFile>(&self.path) else {
            return;
        };
        let Some(channels) = file.channels else {
            return;
        };

        for channel in channels {
            if channel.name.trim().is_empty() {
                continue;
            }
            if channel.expression.trim().is_empty() {
                continue;
            }
            if self.channels.len() >= MAX_CHANNELS {
                break;
            }

            self.channels.push(channel);
        }
    }

    pub fn add(&mut self, channel: MathChannel) -> Result<()> {
        if self.channels.len() >= MAX_CHANNELS {
            return Err(Error::InvalidOperation(format!(
                "There is a limit of {MAX_CHANNELS} calculated channels."
            )));
        }

        if self.channels.iter().any(|c| eq_ignore_case(&c.name, &channel.name)) {
            return Err(Error::InvalidOperation(format!(
                "A calculated channel called \"{}\" already exists.",
                channel.name
            )));
        }

        self.channels.push(channel);
        self.persist()
    }

    /// Swaps `existing` (found by value, as the C# record's equality does) for `replacement`.
    pub fn replace(&mut self, existing: &MathChannel, replacement: MathChannel) -> Result<()> {
        let Some(index) = self.channels.iter().position(|c| c == existing) else {
            return Ok(());
        };

        self.channels[index] = replacement;
        self.persist()
    }

    pub fn remove(&mut self, channel: &MathChannel) -> Result<bool> {
        let Some(index) = self.channels.iter().position(|c| c == channel) else {
            return Ok(false);
        };

        self.channels.remove(index);
        self.persist()?;
        Ok(true)
    }

    fn persist(&self) -> Result<()> {
        let file = MathFile {
            version: 1,
            channels: Some(self.channels.clone()),
        };
        json_settings_file::write(&self.path, &file)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    fn temp_file() -> (PathBuf, PathBuf) {
        static N: AtomicU64 = AtomicU64::new(0);
        let n = N.fetch_add(1, Ordering::Relaxed);
        let directory = std::env::temp_dir().join(format!("olv-math-{}-{n}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        (directory.join("math.json"), directory)
    }

    fn definition(name: &str, expression: &str) -> MathChannel {
        MathChannel {
            name: name.to_string(),
            expression: expression.to_string(),
            units: "AFR".to_string(),
            digits: 2,
            enabled: true,
        }
    }

    #[test]
    fn definitions_survive_a_restart() {
        let (path, dir) = temp_file();

        let mut store = MathChannelStore::new(Some(&path));
        store.add(definition("AFR Error", "AFR - AFR Target 1")).unwrap();

        let again = MathChannelStore::new(Some(&path));
        assert_eq!(again.channels().len(), 1);
        let reloaded = &again.channels()[0];
        assert_eq!(reloaded.name, "AFR Error");
        assert_eq!(reloaded.expression, "AFR - AFR Target 1");
        assert_eq!(reloaded.units, "AFR");
        assert!(reloaded.enabled);
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn a_duplicate_name_is_refused() {
        let (path, dir) = temp_file();
        let mut store = MathChannelStore::new(Some(&path));
        store.add(definition("AFR Error", "AFR - AFR Target 1")).unwrap();

        assert!(matches!(
            store.add(definition("afr error", "1")),
            Err(Error::InvalidOperation(_))
        ));
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn editing_replaces_in_place_and_keeps_the_order() {
        let (path, dir) = temp_file();
        let mut store = MathChannelStore::new(Some(&path));
        store.add(definition("First", "RPM")).unwrap();
        store.add(definition("Second", "AFR")).unwrap();

        let first = store.channels()[0].clone();
        store.replace(&first, definition("First", "RPM * 2")).unwrap();

        let reloaded = MathChannelStore::new(Some(&path));
        let names: Vec<&str> = reloaded.channels().iter().map(|c| c.name.as_str()).collect();
        assert_eq!(names, ["First", "Second"]);
        assert_eq!(reloaded.channels()[0].expression, "RPM * 2");
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn removing_persists() {
        let (path, dir) = temp_file();
        let mut store = MathChannelStore::new(Some(&path));
        store.add(definition("Gone", "RPM")).unwrap();

        let gone = store.channels()[0].clone();
        assert!(store.remove(&gone).unwrap());
        assert!(MathChannelStore::new(Some(&path)).channels().is_empty());
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn an_entry_with_no_expression_is_dropped_on_load() {
        // The file is meant to be hand-editable, so it has to survive a bad edit.
        let (path, dir) = temp_file();
        std::fs::write(
            &path,
            r#"{
  "version": 1,
  "channels": [
    { "name": "Good", "expression": "RPM * 2" },
    { "name": "NoExpression" },
    { "expression": "RPM" }
  ]
}"#,
        )
        .unwrap();

        let store = MathChannelStore::new(Some(&path));
        let names: Vec<&str> = store.channels().iter().map(|c| c.name.as_str()).collect();
        assert_eq!(names, ["Good"]);
        // Absent members take the C# defaults.
        assert_eq!(store.channels()[0].digits, 2);
        assert!(store.channels()[0].enabled);
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn a_missing_file_is_simply_no_channels() {
        let path = std::env::temp_dir().join(format!("olv-none-{}.json", std::process::id()));
        assert!(MathChannelStore::new(Some(&path)).channels().is_empty());
    }

    #[test]
    fn the_file_is_written_with_the_c_sharp_property_names() {
        let (path, dir) = temp_file();
        let mut store = MathChannelStore::new(Some(&path));
        store.add(definition("A", "RPM")).unwrap();

        let text = std::fs::read_to_string(&path).unwrap();
        for key in [
            "\"version\": 1",
            "\"channels\"",
            "\"name\"",
            "\"units\"",
            "\"digits\"",
            "\"expression\"",
            "\"enabled\"",
        ] {
            assert!(text.contains(key), "{key} missing from {text}");
        }
        std::fs::remove_dir_all(dir).ok();
    }
}
