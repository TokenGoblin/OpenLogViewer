//! Ported from `Workspace.cs`. Owned by package `w1-stores`.
//!
//! Where the app keeps the files a person goes looking for: the recordings it makes and the
//! exports it writes. One folder, shallow, named after the app, with everything under it, so a
//! tuner can find a log without being told a path.
//!
//! Deliberately not the documents folder. That is redirected into OneDrive on many machines,
//! which puts recordings deep in a synced folder and uploads each one as it is written. The
//! user profile is not redirected and is one level shorter.
//!
//! Settings stay in the per-user application data folder (see `settings_store`). This is for
//! the files the user owns, not the ones the app owns.

use std::fs;
use std::path::{Component, Path, PathBuf};

use olv_core::{Error, Result};
use time::PrimitiveDateTime;

/// Folder name used under the user's profile when nothing is chosen.
pub const DEFAULT_FOLDER_NAME: &str = "OpenLogViewer";

/// The note left in the definitions folder. Named so that it does not need explaining.
const DEFINITIONS_NOTE_NAME: &str = "PUT ECU DEFINITION FILES HERE.txt";

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Workspace {
    root: PathBuf,
}

impl Workspace {
    /// A workspace at `root`, or at the default location when `root` is absent or blank.
    pub fn new(root: Option<&Path>) -> Self {
        let root = match root {
            Some(r) if !r.to_string_lossy().trim().is_empty() => full_path(r),
            _ => Self::default_path(),
        };

        Self { root }
    }

    /// The default location: a single folder in the user's profile.
    pub fn default_path() -> PathBuf {
        // The C# asked for `SpecialFolder.UserProfile`, which is `%USERPROFILE%` on Windows
        // and the home folder elsewhere. `home_dir` is the same answer on all three.
        std::env::home_dir().unwrap_or_default().join(DEFAULT_FOLDER_NAME)
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    /// Live recordings.
    pub fn logs(&self) -> PathBuf {
        self.root.join("Logs")
    }

    /// Anything written by an export.
    pub fn exports(&self) -> PathBuf {
        self.root.join("Exports")
    }

    /// Firmware definition files the user has supplied.
    ///
    /// A live connection cannot be decoded without the INI matching the ECU's signature, and
    /// the usual place to find one is wherever TunerStudio put it. That covers most people and
    /// not everyone: a Speeduino owner who tunes on another machine, anyone who has never
    /// installed TunerStudio, or a firmware too new for the copy on disk.
    pub fn definitions(&self) -> PathBuf {
        self.root.join("ECU definitions")
    }

    /// Where an agent leaves a file for a person to open (a full tune, a single table). It
    /// never reaches a controller, so it needs none of the gates a write does.
    pub fn staging(&self) -> PathBuf {
        self.root.join("AgentStaging")
    }

    /// Every place a firmware definition might be, ours first.
    ///
    /// Ahead of TunerStudio's own folders deliberately: a file the user went to the trouble of
    /// putting here is a more deliberate answer than one a tool cached in the past. The
    /// TunerStudio folders come from the INI catalog, passed in by the caller.
    pub fn definition_search_paths(&self, catalog_defaults: &[PathBuf]) -> Vec<PathBuf> {
        let mut paths = Vec::with_capacity(catalog_defaults.len() + 1);
        paths.push(self.definitions());
        paths.extend(catalog_defaults.iter().cloned());
        paths
    }

    /// Creates the definitions folder and leaves a note in it saying what it is for.
    ///
    /// An empty folder called "ECU definitions" tells someone almost nothing. The note names
    /// the signature their ECU actually reported, which is the one piece of information that
    /// makes finding the right file possible.
    pub fn ensure_definitions(&self, identity: Option<&[String]>) -> Result<PathBuf> {
        let folder = Self::ensure(&self.definitions())?;

        // Rewritten each time, so the note names whichever ECU was last plugged in rather than
        // the first one ever tried. The folder is the useful part; the note is a courtesy, so a
        // failure to write it is not an error.
        let _ = fs::write(folder.join(DEFINITIONS_NOTE_NAME), readme_text(identity));

        Ok(folder)
    }

    /// A path for a new recording, named for when it was taken so a folder of them sorts into
    /// the order they happened.
    pub fn new_recording(&self, at: PrimitiveDateTime) -> Result<PathBuf> {
        let logs = Self::ensure(&self.logs())?;
        let name = format!(
            "live-{:04}-{:02}-{:02}_{:02}-{:02}-{:02}.csv",
            at.year(),
            at.month() as u8,
            at.day(),
            at.hour(),
            at.minute(),
            at.second()
        );

        Ok(logs.join(name))
    }

    /// Whether the workspace is the default location, ignoring case and a trailing separator.
    pub fn is_default(&self) -> bool {
        trimmed(&self.root).eq_ignore_ascii_case(&trimmed(&Self::default_path()))
    }

    /// Creates a folder if it is missing and hands back its path, or falls back to the default
    /// location when the chosen one cannot be written.
    ///
    /// A folder that has gone (an unplugged drive, a share that is not there) should cost the
    /// setting, not the recording that was about to start.
    pub fn ensure(folder: &Path) -> Result<PathBuf> {
        if fs::create_dir_all(folder).is_ok() {
            return Ok(folder.to_path_buf());
        }

        let name = folder.file_name().unwrap_or_default();
        let fallback = Self::default_path().join(name);
        fs::create_dir_all(&fallback).map_err(Error::Io)?;

        Ok(fallback)
    }

    /// Whether a folder can be chosen: it has to exist, or be creatable.
    pub fn is_usable(folder: Option<&str>) -> bool {
        let Some(folder) = folder.filter(|f| !f.trim().is_empty()) else {
            return false;
        };

        // The C# asked for `Directory.CreateDirectory` and then `Directory.Exists`, and so does
        // this: a path that exists as a file is not a usable folder.
        fs::create_dir_all(folder).is_ok() && Path::new(folder).is_dir()
    }
}

/// The C# `Path.TrimEndingDirectorySeparator`, as text.
fn trimmed(path: &Path) -> String {
    path.to_string_lossy()
        .trim_end_matches(std::path::is_separator)
        .to_string()
}

/// `Path.GetFullPath`: absolute, with `.` and `..` resolved lexically.
fn full_path(path: &Path) -> PathBuf {
    let absolute = std::path::absolute(path).unwrap_or_else(|_| path.to_path_buf());
    let mut out = PathBuf::new();

    for component in absolute.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                // Never above the root or prefix.
                if matches!(out.components().next_back(), Some(Component::Normal(_))) {
                    out.pop();
                }
            }
            other => out.push(other.as_os_str()),
        }
    }

    out
}

/// The note in the definitions folder, as the C# wrote it.
fn readme_text(identity: Option<&[String]>) -> String {
    format!(
        "ECU definition files (.ini)
===========================

Put your ECU's TunerStudio definition file in this folder and
OpenLogViewer will find it. Sub-folders are searched too, so you can
drop a whole firmware folder in here if that is easier.

Why it is needed
----------------
A live ECU sends a block of raw numbers and nothing else - no names, no
units, no scaling. All of that lives in the .ini for that exact firmware
build. Using the wrong one does not fail; it reads every channel from
the wrong place and shows numbers that look perfectly reasonable. So
OpenLogViewer will not connect until it finds the file matching the
signature your ECU reports{reported}

Where to get one
----------------
  MegaSquirt   in the firmware download from msextra.com, or already on
               this machine if TunerStudio is installed
  Speeduino    in the Speeduino firmware download, or from SpeedyLoader
  rusEFI       published with each build; the rusEFI console can also
               save the one matching your board

If TunerStudio is installed, its own copies are searched automatically:
  %USERPROFILE%\\.efiAnalytics\\TunerStudio\\config\\ecuDef
  Documents\\TunerStudioProjects

Nothing is downloaded. OpenLogViewer never uses the internet - this
folder is how you give it a definition it does not already have.",
        reported = reported(identity)
    )
}

/// Every reply the ECU gave about itself, not a guess at which is the signature.
///
/// An ECU is asked several questions and no reply says which is the signature: a Speeduino
/// answers "Speeduino 2024.02.2" and "speeduino 202402", and only the second is what an INI
/// declares. Naming one would be right half the time, so all of them are listed.
fn reported(identity: Option<&[String]>) -> String {
    match identity {
        Some(items) if !items.is_empty() => {
            let lines: Vec<String> = items.iter().map(|t| format!("    {t}")).collect();
            format!(", one of which is:\n\n{}", lines.join("\n"))
        }
        _ => String::new(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::store::settings_store::test_support::Scratch;
    use time::{Date, Month, Time};

    fn at(year: i32, month: Month, day: u8, hour: u8, minute: u8, second: u8) -> PrimitiveDateTime {
        PrimitiveDateTime::new(
            Date::from_calendar_date(year, month, day).unwrap(),
            Time::from_hms(hour, minute, second).unwrap(),
        )
    }

    #[test]
    fn the_default_sits_in_the_profile_rather_than_documents() {
        // The documents folder is redirected into OneDrive on most machines, which both buries
        // recordings and uploads every one of them as it is written.
        let home = std::env::home_dir().expect("a home folder");
        let default = Workspace::default_path();

        assert!(default.starts_with(&home), "{default:?} is not under {home:?}");
        assert!(!default.starts_with(home.join("Documents")));
        assert!(!default.starts_with(home.join("OneDrive")));
    }

    #[test]
    fn the_default_is_shallow() {
        // "Not ten folders deep" was the requirement. Profile plus one.
        let default = Workspace::default_path();
        let depth = default
            .components()
            .filter(|c| matches!(c, Component::Normal(_) | Component::Prefix(_)))
            .count();

        assert!(depth <= 4, "{default:?} is {depth} levels deep");
        assert_eq!(default.file_name().unwrap(), DEFAULT_FOLDER_NAME);
    }

    #[test]
    fn recordings_and_exports_sit_under_the_one_folder() {
        let scratch = Scratch::new();
        let root = scratch.join("Tuning");
        let workspace = Workspace::new(Some(&root));

        assert_eq!(workspace.root(), root);
        assert_eq!(workspace.logs(), root.join("Logs"));
        assert_eq!(workspace.exports(), root.join("Exports"));
    }

    #[test]
    fn nothing_chosen_means_the_default() {
        let scratch = Scratch::new();

        assert!(Workspace::new(None).is_default());
        assert!(Workspace::new(Some(Path::new("   "))).is_default());
        assert!(!Workspace::new(Some(&scratch.join("Tuning"))).is_default());
    }

    #[test]
    fn a_recording_is_named_for_when_it_was_taken() {
        // So a folder of them sorts into the order they happened.
        let scratch = Scratch::new();
        let workspace = Workspace::new(Some(&scratch.join("ws")));

        let path = workspace.new_recording(at(2026, Month::August, 1, 21, 5, 3)).unwrap();

        assert_eq!(path.file_name().unwrap(), "live-2026-08-01_21-05-03.csv");
        assert_eq!(path.parent().unwrap(), workspace.logs());
        assert!(workspace.logs().is_dir(), "the folder was not created");
    }

    #[test]
    fn a_folder_that_cannot_be_written_falls_back_rather_than_throwing() {
        // An unplugged drive or a share that is not there should cost the setting, not the
        // recording that was about to start. A file where a folder should be cannot be
        // created into on any of the three systems, so it stands in for the missing drive.
        let scratch = Scratch::new();
        fs::write(scratch.join("blocker"), "").unwrap();

        let created = Workspace::ensure(&scratch.join("blocker").join("Logs")).unwrap();

        assert!(created.is_dir());
        assert!(created.starts_with(Workspace::default_path()));
    }

    #[test]
    fn a_usable_folder_is_one_that_can_be_created() {
        let scratch = Scratch::new();
        fs::write(scratch.join("blocker"), "").unwrap();
        let temp = scratch.join("fresh");
        let temp = temp.to_str().unwrap();

        assert!(Workspace::is_usable(Some(temp)));
        assert!(!Workspace::is_usable(None));
        assert!(!Workspace::is_usable(Some("")));
        assert!(!Workspace::is_usable(Some(
            scratch.join("blocker").join("nowhere").to_str().unwrap()
        )));
    }

    #[test]
    #[ignore = "needs w1-ini (IniCatalog.DefaultSearchPaths is not ported yet)"]
    fn it_is_searched_before_tunerstudios_own_folders() {
        // A file someone went to the trouble of putting there is a more deliberate answer than
        // one a tool cached at some point in the past.
        let scratch = Scratch::new();
        let workspace = Workspace::new(Some(&scratch.join("ws")));

        let paths = workspace.definition_search_paths(&[]);

        assert_eq!(paths[0], workspace.definitions());
        assert!(paths.len() > 1);
    }

    #[test]
    fn the_definitions_folder_sits_beside_logs_and_exports() {
        let scratch = Scratch::new();
        let workspace = Workspace::new(Some(&scratch.join("ws")));

        assert_eq!(workspace.definitions(), workspace.root().join("ECU definitions"));
    }

    #[test]
    fn creating_it_leaves_a_note_saying_what_it_is_for() {
        // An empty folder called "ECU definitions" tells someone almost nothing.
        let scratch = Scratch::new();
        let workspace = Workspace::new(Some(&scratch.join("ws")));

        let folder = workspace.ensure_definitions(None).unwrap();
        let note = folder.join(DEFINITIONS_NOTE_NAME);

        let text = fs::read_to_string(&note).unwrap();
        assert!(text.contains("no names, no"));
        assert!(text.contains("Speeduino"));
        assert!(text.contains("never uses the internet"));
    }

    #[test]
    fn the_note_names_every_thing_the_ecu_said_about_itself() {
        // No reply says which of them is the signature, so naming one would mislead half the time.
        let scratch = Scratch::new();
        let workspace = Workspace::new(Some(&scratch.join("ws")));

        let folder = workspace
            .ensure_definitions(Some(&[
                "Speeduino 2024.02.2".to_string(),
                "speeduino 202402".to_string(),
            ]))
            .unwrap();
        let text = fs::read_to_string(folder.join(DEFINITIONS_NOTE_NAME)).unwrap();

        assert!(text.contains("Speeduino 2024.02.2"));
        assert!(text.contains("speeduino 202402"));
    }

    #[test]
    fn the_note_follows_whichever_ecu_was_plugged_in_last() {
        let scratch = Scratch::new();
        let workspace = Workspace::new(Some(&scratch.join("ws")));

        workspace
            .ensure_definitions(Some(&["speeduino 202402".to_string()]))
            .unwrap();
        let folder = workspace
            .ensure_definitions(Some(&["MS3 Format 0569.00".to_string()]))
            .unwrap();

        let text = fs::read_to_string(folder.join(DEFINITIONS_NOTE_NAME)).unwrap();
        assert!(text.contains("MS3 Format 0569.00"));
        assert!(!text.contains("speeduino"));
    }

    #[test]
    #[ignore = "needs w1-ini (IniCatalog.Scan is not ported yet)"]
    fn an_ini_dropped_in_is_found() {
        // The whole point: a definition this machine did not already have.
    }

    #[test]
    #[ignore = "needs w1-ini (IniCatalog.Scan is not ported yet)"]
    fn a_sub_folder_is_searched_too() {
        // So a whole firmware folder can be dropped in unopened.
    }
}
