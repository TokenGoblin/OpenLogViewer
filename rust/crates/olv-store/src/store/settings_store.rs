//! Ported from `SettingsStore.cs`. Owned by package `w1-stores`.
//!
//! Small preferences that outlive a session and belong to the person rather than to any one
//! log: the colour scheme, the data folder, the logging rate, and what is known about each ECU
//! adapter. Kept in `settings.json` in the per-user application folder, under the property
//! names the C# wrote, so an existing install loads unchanged.
//!
//! Every setting sits behind one lock, and every setter writes the whole file: saving one
//! setting must not drop another, and a note written from a background thread must not land
//! in the middle of a save from the window.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard};

use olv_core::base::json_settings_file;
use olv_core::model::IgnoreCaseMap;
use olv_core::units::UnitSystem;
use olv_core::{Error, Result};
use serde::{Deserialize, Serialize};
use time::OffsetDateTime;
use time::format_description::well_known::Rfc3339;

/// Samples per second to record live when nothing has been chosen. Well above what a wideband
/// can resolve, and far below what a USB link will offer.
pub const DEFAULT_LIVE_RATE: f64 = 25.0;

/// Above this the cap is meaningless: no link answers that fast.
pub const MAXIMUM_LIVE_RATE: f64 = 1000.0;

/// The range a stored interface scale is honoured within.
pub const MINIMUM_UI_SCALE: f64 = 1.0;
pub const MAXIMUM_UI_SCALE: f64 = 3.0;

/// The values the file holds, as the C# read them. Every property is optional, so a file
/// missing any of them (or written before they existed) takes the defaults.
///
/// Read by the C# in a case-insensitive way; serde has no such option, so each property takes
/// its PascalCase spelling as an alias as well as the camelCase one it is written with.
#[derive(Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct SettingsFile {
    #[serde(alias = "ThemeId")]
    theme_id: Option<String>,
    #[serde(alias = "UiScale")]
    ui_scale: Option<f64>,
    #[serde(alias = "DataFolder")]
    data_folder: Option<String>,
    #[serde(alias = "LiveRate")]
    live_rate: Option<f64>,
    #[serde(alias = "SingleRequestBlock")]
    single_request_block: Option<bool>,
    #[serde(alias = "RecordOnConnect")]
    record_on_connect: Option<bool>,
    #[serde(alias = "RecordingFolder")]
    recording_folder: Option<String>,
    #[serde(alias = "KnownEcus")]
    known_ecus: Option<BTreeMap<String, String>>,
    // Round-trip strings rather than dates, so a hand-edited or differently-cultured file
    // cannot turn a timestamp into a wrong one.
    #[serde(alias = "EcuLastUsed")]
    ecu_last_used: Option<BTreeMap<String, String>>,
    #[serde(alias = "Units")]
    units: Option<String>,
    #[serde(alias = "Obd2BatchDeaths")]
    obd2_batch_deaths: Option<BTreeMap<String, i32>>,
}

/// The file as written. Properties come out in the order the C# wrote them, and null
/// properties are left out by `json_settings_file::write`.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct SettingsFileOut {
    version: i32,
    theme_id: Option<String>,
    ui_scale: Option<f64>,
    data_folder: Option<String>,
    live_rate: f64,
    single_request_block: bool,
    record_on_connect: bool,
    recording_folder: Option<String>,
    known_ecus: Option<BTreeMap<String, String>>,
    ecu_last_used: Option<BTreeMap<String, String>>,
    units: &'static str,
    obd2_batch_deaths: Option<BTreeMap<String, i32>>,
}

/// Everything the store holds, under the one lock.
#[derive(Clone, Debug)]
struct Settings {
    theme_id: Option<String>,
    ui_scale: f64,
    data_folder: Option<String>,
    live_rate: f64,
    single_request_block: bool,
    record_on_connect: bool,
    recording_folder: Option<String>,
    units: UnitSystem,
    ecu_last_used: IgnoreCaseMap<OffsetDateTime>,
    known_ecus: IgnoreCaseMap<String>,
    obd2_batch_deaths: IgnoreCaseMap<i32>,
}

#[derive(Debug)]
pub struct SettingsStore {
    path: PathBuf,
    state: Mutex<Settings>,
}

impl SettingsStore {
    /// Reads the file at `path`. A missing or malformed file reads as all defaults.
    pub fn new(path: PathBuf) -> Self {
        let settings = Settings::from_file(json_settings_file::read::<SettingsFile>(&path));

        Self {
            path,
            state: Mutex::new(settings),
        }
    }

    /// `settings.json` in the per-user application folder.
    pub fn default_path() -> PathBuf {
        json_settings_file::in_app_data("settings.json")
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Reads the file again, discarding what is held.
    pub fn reload(&self) {
        let mut state = self.state();
        *state = Settings::from_file(json_settings_file::read::<SettingsFile>(&self.path));
    }

    /// Identifier of the active theme, or `None` for the app's default.
    pub fn theme_id(&self) -> Option<String> {
        self.state().theme_id.clone()
    }

    /// How much larger than drawn the interface is shown: 1 is as designed.
    pub fn ui_scale(&self) -> f64 {
        self.state().ui_scale
    }

    /// Where recordings and exports go, or `None` for the default. Kept as the folder the user
    /// chose rather than the folders beneath it, so moving the workspace moves everything.
    pub fn data_folder(&self) -> Option<String> {
        self.state().data_folder.clone()
    }

    /// Samples per second to record live, or zero for as fast as the link goes.
    pub fn live_rate(&self) -> f64 {
        self.state().live_rate
    }

    /// Whether connecting starts a recording straight away. Off by default: connecting watches,
    /// and recording is asked for.
    pub fn record_on_connect(&self) -> bool {
        self.state().record_on_connect
    }

    /// Where the last recording was saved by hand, so the next Save As opens there.
    pub fn recording_folder(&self) -> Option<String> {
        self.state().recording_folder.clone()
    }

    /// Ask for the realtime block in one request rather than in blocking-factor pieces. Faster
    /// where the firmware allows it, fatal where it does not.
    pub fn single_request_block(&self) -> bool {
        self.state().single_request_block
    }

    /// Which units to show readings in. Defaults to whatever the ECU reports, because that is the
    /// only setting that cannot be wrong.
    pub fn units(&self) -> UnitSystem {
        self.state().units
    }

    /// What answered on each serial device, keyed by its hardware id.
    ///
    /// Remembered between sessions because it is most wanted before connecting: Windows calls a
    /// Speeduino "Arduino Mega 2560", which names the chip and not the ECU. The hardware id
    /// rather than the COM number, so a replug onto a different port is still recognised.
    pub fn known_ecus(&self) -> IgnoreCaseMap<String> {
        self.state().known_ecus.clone()
    }

    /// When each remembered device was last connected to, by the same hardware id. A device with
    /// no entry here was remembered before anyone was counting.
    pub fn ecu_last_used(&self) -> IgnoreCaseMap<OffsetDateTime> {
        self.state().ecu_last_used.clone()
    }

    /// Links that asking for several parameters at once has killed, by adapter. Kept between
    /// sessions because the finding out is what costs: learnt once it is free from then on.
    pub fn obd2_batch_deaths(&self) -> IgnoreCaseMap<i32> {
        self.state().obd2_batch_deaths.clone()
    }

    pub fn set_units(&self, units: UnitSystem) -> Result<()> {
        let mut state = self.state();
        if units == state.units {
            return Ok(());
        }

        state.units = units;
        self.persist(&state)
    }

    /// Replaces the whole map of when each device was last used.
    pub fn set_ecu_last_used(&self, used: IgnoreCaseMap<OffsetDateTime>) -> Result<()> {
        let mut state = self.state();
        state.ecu_last_used = used;
        self.persist(&state)
    }

    /// Forgets every device this has connected to.
    ///
    /// Both maps together, because a signature without a time and a time without a signature are
    /// each half a memory. Presets, filters and calculated channels are deliberately untouched:
    /// those are somebody's work. This is for when a dongle has been sold and its name keeps
    /// turning up in a list.
    pub fn forget_known_ecus(&self) -> Result<()> {
        let mut state = self.state();
        if state.known_ecus.is_empty() && state.ecu_last_used.is_empty() {
            return Ok(());
        }

        state.known_ecus = IgnoreCaseMap::new();
        state.ecu_last_used = IgnoreCaseMap::new();
        self.persist(&state)
    }

    pub fn set_known_ecus(&self, known: IgnoreCaseMap<String>) -> Result<()> {
        let mut state = self.state();
        let unchanged = known.len() == state.known_ecus.len()
            && known
                .iter()
                .all(|(key, value)| state.known_ecus.get(key) == Some(value));
        if unchanged {
            return Ok(());
        }

        state.known_ecus = known;
        self.persist(&state)
    }

    /// Notes that batching cost this adapter another link.
    ///
    /// Read, copied, swapped and written as one act under the lock. This is the one setter
    /// called from the poll thread, so it is also where two running at once would lose a death.
    pub fn record_obd2_batch_death(&self, adapter: &str) -> Result<()> {
        if adapter.trim().is_empty() {
            return Ok(());
        }

        let mut state = self.state();
        let deaths = state.obd2_batch_deaths.get(adapter).copied().unwrap_or(0) + 1;
        state.obd2_batch_deaths.insert(adapter, deaths);
        self.persist(&state)
    }

    pub fn set_single_request_block(&self, single: bool) -> Result<()> {
        let mut state = self.state();
        if single == state.single_request_block {
            return Ok(());
        }

        state.single_request_block = single;
        self.persist(&state)
    }

    pub fn set_record_on_connect(&self, record: bool) -> Result<()> {
        let mut state = self.state();
        if record == state.record_on_connect {
            return Ok(());
        }

        state.record_on_connect = record;
        self.persist(&state)
    }

    pub fn set_recording_folder(&self, folder: Option<&str>) -> Result<()> {
        let mut state = self.state();
        let trimmed = non_blank(folder);
        if trimmed == state.recording_folder {
            return Ok(());
        }

        state.recording_folder = trimmed;
        self.persist(&state)
    }

    /// Sets the logging rate, clamped to 1 through [`MAXIMUM_LIVE_RATE`].
    pub fn set_live_rate(&self, rate: f64) -> Result<()> {
        let mut state = self.state();
        let clamped = rate.clamp(1.0, MAXIMUM_LIVE_RATE);
        if clamped == state.live_rate {
            return Ok(());
        }

        state.live_rate = clamped;
        self.persist(&state)
    }

    pub fn set_data_folder(&self, folder: Option<&str>) -> Result<()> {
        let mut state = self.state();
        let trimmed = non_blank(folder);
        if trimmed == state.data_folder {
            return Ok(());
        }

        state.data_folder = trimmed;
        self.persist(&state)
    }

    /// Sets the interface scale. A value that is not finite reads as 1, and anything else is
    /// clamped into [`MINIMUM_UI_SCALE`] through [`MAXIMUM_UI_SCALE`].
    pub fn set_ui_scale(&self, scale: f64) -> Result<()> {
        let mut state = self.state();
        let clamped = if scale.is_finite() {
            scale.clamp(MINIMUM_UI_SCALE, MAXIMUM_UI_SCALE)
        } else {
            1.0
        };
        if clamped == state.ui_scale {
            return Ok(());
        }

        state.ui_scale = clamped;
        self.persist(&state)
    }

    pub fn set_theme(&self, id: Option<&str>) -> Result<()> {
        let mut state = self.state();
        let trimmed = non_blank(id);
        if trimmed == state.theme_id {
            return Ok(());
        }

        state.theme_id = trimmed;
        self.persist(&state)
    }

    fn state(&self) -> MutexGuard<'_, Settings> {
        // A panic in another setter must not stop every later save; the data is still whole, as
        // each write replaces the file in one move.
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Writes the whole file. Settings are saved together rather than one at a time, or saving
    /// the second would drop the first.
    fn persist(&self, state: &Settings) -> Result<()> {
        json_settings_file::write(&self.path, &SettingsFileOut::from_settings(state)).map_err(Error::Io)
    }
}

impl Settings {
    /// What the C# `ReadFile` produced from the file, or defaults when there was none.
    fn from_file(file: Option<SettingsFile>) -> Self {
        let file = file.unwrap_or_default();

        // A hand-edited 0 or 40 would leave the window unusable on the next start, with no way
        // back to the menu that fixes it.
        let ui_scale = file
            .ui_scale
            .filter(|scale| (MINIMUM_UI_SCALE..=MAXIMUM_UI_SCALE).contains(scale))
            .unwrap_or(1.0);

        // A missing or nonsensical rate takes the default rather than being honoured: a zero read
        // out of an older file would silently uncap a session nobody asked to uncap.
        let live_rate = file
            .live_rate
            .filter(|rate| *rate > 0.0 && *rate <= MAXIMUM_LIVE_RATE)
            .unwrap_or(DEFAULT_LIVE_RATE);

        Self {
            theme_id: non_blank(file.theme_id.as_deref()),
            ui_scale,
            data_folder: non_blank(file.data_folder.as_deref()),
            live_rate,
            single_request_block: file.single_request_block.unwrap_or(false),
            // Absent takes the default (off), not the old behaviour of always recording.
            record_on_connect: file.record_on_connect.unwrap_or(false),
            recording_folder: non_blank(file.recording_folder.as_deref()),
            units: parse_units(file.units.as_deref()),
            ecu_last_used: file
                .ecu_last_used
                .unwrap_or_default()
                .iter()
                .filter_map(|(key, value)| OffsetDateTime::parse(value, &Rfc3339).ok().map(|at| (key.as_str(), at)))
                .fold(IgnoreCaseMap::new(), |mut map, (key, at)| {
                    map.insert(key, at);
                    map
                }),
            known_ecus: string_map(file.known_ecus.unwrap_or_default()),
            obd2_batch_deaths: file.obd2_batch_deaths.unwrap_or_default().iter().fold(
                IgnoreCaseMap::new(),
                |mut map, (key, count)| {
                    map.insert(key, *count);
                    map
                },
            ),
        }
    }
}

impl SettingsFileOut {
    fn from_settings(state: &Settings) -> Self {
        Self {
            version: 1,
            theme_id: state.theme_id.clone(),
            // A scale of one is the default, and is not written.
            ui_scale: (state.ui_scale != 1.0).then_some(state.ui_scale),
            data_folder: state.data_folder.clone(),
            live_rate: state.live_rate,
            single_request_block: state.single_request_block,
            record_on_connect: state.record_on_connect,
            recording_folder: state.recording_folder.clone(),
            known_ecus: non_empty(&state.known_ecus, |map| {
                map.iter()
                    .map(|(key, value)| (key.to_string(), value.clone()))
                    .collect()
            }),
            ecu_last_used: non_empty(&state.ecu_last_used, |map| {
                map.iter().map(|(key, at)| (key.to_string(), round_trip(at))).collect()
            }),
            units: units_name(state.units),
            obd2_batch_deaths: non_empty(&state.obd2_batch_deaths, |map| {
                map.iter().map(|(key, count)| (key.to_string(), *count)).collect()
            }),
        }
    }
}

/// The C# `string.IsNullOrWhiteSpace(x) ? null : x.Trim()`.
fn non_blank(value: Option<&str>) -> Option<String> {
    value.map(str::trim).filter(|text| !text.is_empty()).map(str::to_string)
}

/// A map of text values as an [`IgnoreCaseMap`], in the order the file gave.
fn string_map(map: BTreeMap<String, String>) -> IgnoreCaseMap<String> {
    let mut out = IgnoreCaseMap::new();
    for (key, value) in map {
        out.insert(&key, value);
    }
    out
}

/// `null` when there is nothing to write, as the C# left the property out.
fn non_empty<V, T>(
    map: &IgnoreCaseMap<V>,
    build: impl FnOnce(&IgnoreCaseMap<V>) -> BTreeMap<String, T>,
) -> Option<BTreeMap<String, T>> {
    (!map.is_empty()).then(|| build(map))
}

/// The C# `Enum.TryParse` on the stored name: the enum's names (case-sensitive), or its numbers,
/// with anything else taking the default.
fn parse_units(text: Option<&str>) -> UnitSystem {
    match text.map(str::trim) {
        Some("Metric" | "1") => UnitSystem::Metric,
        Some("Imperial" | "2") => UnitSystem::Imperial,
        _ => UnitSystem::AsReported,
    }
}

fn units_name(units: UnitSystem) -> &'static str {
    match units {
        UnitSystem::AsReported => "AsReported",
        UnitSystem::Metric => "Metric",
        UnitSystem::Imperial => "Imperial",
    }
}

/// .NET's `DateTimeOffset.ToString("O")`: seven fractional digits and the offset with a sign,
/// so the C# reads what this writes and this reads what the C# wrote.
fn round_trip(at: &OffsetDateTime) -> String {
    let offset = at.offset();
    let (hours, minutes, _) = offset.as_hms();
    let sign = if offset.is_negative() { '-' } else { '+' };

    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:07}{}{:02}:{:02}",
        at.year(),
        at.month() as u8,
        at.day(),
        at.hour(),
        at.minute(),
        at.second(),
        at.nanosecond() / 100,
        sign,
        hours.unsigned_abs(),
        minutes.unsigned_abs(),
    )
}

#[cfg(test)]
pub(crate) mod test_support {
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::{SystemTime, UNIX_EPOCH};

    /// A folder under the system temp directory, removed when dropped. Each test gets its own,
    /// so tests running in parallel never share a file.
    pub(crate) struct Scratch(PathBuf);

    impl Scratch {
        pub(crate) fn new() -> Self {
            static COUNTER: AtomicU64 = AtomicU64::new(0);

            let nanos = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos();
            let dir = std::env::temp_dir().join(format!(
                "olv-store-{nanos}-{}-{}",
                std::process::id(),
                COUNTER.fetch_add(1, Ordering::Relaxed)
            ));
            std::fs::create_dir_all(&dir).expect("a scratch folder");

            Self(dir)
        }

        pub(crate) fn join(&self, name: &str) -> PathBuf {
            self.0.join(name)
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::test_support::Scratch;
    use super::*;
    use std::fs;

    fn settings_path(scratch: &Scratch) -> PathBuf {
        scratch.join("settings.json")
    }

    #[test]
    fn the_chosen_folder_survives_a_restart() {
        let scratch = Scratch::new();
        let path = settings_path(&scratch);
        let chosen = scratch.join("chosen");

        SettingsStore::new(path.clone())
            .set_data_folder(Some(chosen.to_str().unwrap()))
            .unwrap();

        assert_eq!(SettingsStore::new(path).data_folder().as_deref(), chosen.to_str());
    }

    #[test]
    fn saving_one_setting_does_not_drop_another() {
        // Each setter used to write only its own field, so whichever was saved second erased the
        // first.
        let scratch = Scratch::new();
        let path = settings_path(&scratch);
        let chosen = scratch.join("chosen");

        let store = SettingsStore::new(path.clone());
        store.set_data_folder(Some(chosen.to_str().unwrap())).unwrap();
        store.set_theme(Some("gruvbox")).unwrap();

        let reloaded = SettingsStore::new(path);
        assert_eq!(reloaded.data_folder().as_deref(), chosen.to_str());
        assert_eq!(reloaded.theme_id().as_deref(), Some("gruvbox"));
    }

    #[test]
    fn clearing_the_folder_goes_back_to_the_default() {
        let scratch = Scratch::new();
        let path = settings_path(&scratch);

        let store = SettingsStore::new(path.clone());
        store
            .set_data_folder(Some(scratch.join("chosen").to_str().unwrap()))
            .unwrap();
        store.set_data_folder(None).unwrap();

        let reopened = SettingsStore::new(path);
        assert_eq!(reopened.data_folder(), None);
        assert!(crate::store::workspace::Workspace::new(reopened.data_folder().as_deref().map(Path::new)).is_default());
    }

    #[test]
    fn what_answered_on_a_port_survives_a_restart() {
        // The whole value is in knowing before connecting: Windows calls a Speeduino "Arduino Mega
        // 2560", and having to connect once to find out defeats the point of remembering at all.
        let scratch = Scratch::new();
        let path = settings_path(&scratch);
        const DEVICE: &str = r"USB\VID_2341&PID_0042\95730333837351C01221";

        let mut known = IgnoreCaseMap::new();
        known.insert(DEVICE, "Speeduino 2025.01.7".to_string());
        SettingsStore::new(path.clone()).set_known_ecus(known).unwrap();

        assert_eq!(
            SettingsStore::new(path).known_ecus().get(DEVICE).map(String::as_str),
            Some("Speeduino 2025.01.7")
        );
    }

    #[test]
    fn a_settings_file_written_before_there_was_any_of_this_reads_as_none_known() {
        let scratch = Scratch::new();
        let path = settings_path(&scratch);
        fs::write(&path, r#"{"Version":1,"ThemeId":"gruvbox"}"#).unwrap();

        assert!(SettingsStore::new(path).known_ecus().is_empty());
    }

    #[test]
    fn the_logging_rate_defaults_to_25() {
        let scratch = Scratch::new();

        assert_eq!(SettingsStore::new(settings_path(&scratch)).live_rate(), 25.0);
    }

    #[test]
    fn the_logging_rate_survives_a_restart() {
        let scratch = Scratch::new();
        let path = settings_path(&scratch);
        SettingsStore::new(path.clone()).set_live_rate(100.0).unwrap();

        assert_eq!(SettingsStore::new(path).live_rate(), 100.0);
    }

    #[test]
    fn the_logging_rate_is_saved_alongside_everything_else() {
        let scratch = Scratch::new();
        let path = settings_path(&scratch);

        let store = SettingsStore::new(path.clone());
        store.set_live_rate(50.0).unwrap();
        store.set_theme(Some("gruvbox")).unwrap();

        let reloaded = SettingsStore::new(path);
        assert_eq!(reloaded.live_rate(), 50.0);
        assert_eq!(reloaded.theme_id().as_deref(), Some("gruvbox"));
    }

    #[test]
    fn a_settings_file_written_before_there_was_a_rate_takes_the_default() {
        // Not zero, which is what a missing number reads as and which would uncap a session nobody
        // asked to uncap.
        let scratch = Scratch::new();
        let path = settings_path(&scratch);
        fs::write(&path, r#"{"Version":1,"ThemeId":"gruvbox"}"#).unwrap();

        assert_eq!(SettingsStore::new(path).live_rate(), 25.0);
    }

    #[test]
    fn an_absurd_rate_is_brought_back_into_range() {
        let scratch = Scratch::new();
        let store = SettingsStore::new(settings_path(&scratch));

        store.set_live_rate(0.0).unwrap();
        assert_eq!(store.live_rate(), 1.0);

        store.set_live_rate(100_000.0).unwrap();
        assert_eq!(store.live_rate(), MAXIMUM_LIVE_RATE);
    }

    #[test]
    fn a_death_noted_while_something_else_is_saving_is_not_lost() {
        // The one setting written from somewhere other than the interface thread. A batched request
        // killing a link is noticed by the poll thread, and the note lands in the middle of whatever
        // the window is saving: the count is read, copied and swapped while another write does the
        // same, and both go through one scratch file on the way to disk.
        let scratch = Scratch::new();
        let path = settings_path(&scratch);
        let store = SettingsStore::new(path.clone());

        const ADAPTER: &str = "192.168.0.10:35000";
        const THREADS: i32 = 8;
        const EACH: i32 = 25;

        std::thread::scope(|scope| {
            for worker in 0..THREADS {
                let store = &store;
                scope.spawn(move || {
                    for note in 0..EACH {
                        store.record_obd2_batch_death(ADAPTER).unwrap();

                        // And one of them standing in for the window, saving something of its own
                        // the whole time.
                        if worker == 0 {
                            let theme = if note % 2 == 0 { "gruvbox" } else { "nord" };
                            store.set_theme(Some(theme)).unwrap();
                        }
                    }
                });
            }
        });

        assert_eq!(store.obd2_batch_deaths().get(ADAPTER), Some(&(THREADS * EACH)));
        assert_eq!(
            SettingsStore::new(path).obd2_batch_deaths().get(ADAPTER),
            Some(&(THREADS * EACH))
        );
    }

    #[test]
    fn two_copies_of_the_application_saving_at_once_leave_a_file_that_still_reads() {
        // Nothing coordinates between processes and nothing needs to: last one wins is a fine
        // answer for a preference. What is not fine is a settings file left half written, which
        // one shared "settings.json.tmp" produces with two writers in it at once.
        let scratch = Scratch::new();
        let path = settings_path(&scratch);

        let first = SettingsStore::new(path.clone());
        let second = SettingsStore::new(path.clone());

        std::thread::scope(|scope| {
            scope.spawn(|| {
                for i in 0..60 {
                    first.set_theme(Some(&format!("gruvbox-{i}"))).unwrap();
                }
            });
            scope.spawn(|| {
                for i in 0..60 {
                    second.set_theme(Some(&format!("nord-{i}"))).unwrap();
                }
            });
        });

        let theme = SettingsStore::new(path).theme_id().expect("a theme");
        assert!(
            theme.starts_with("gruvbox-") || theme.starts_with("nord-"),
            "the settings file came back holding {theme:?}"
        );

        // And nothing left behind: a scratch name is only an improvement if it is also cleared up.
        let leftovers = fs::read_dir(scratch.join(""))
            .unwrap()
            .filter(|entry| entry.as_ref().unwrap().path().extension().is_some_and(|e| e == "tmp"))
            .count();
        assert_eq!(leftovers, 0);
    }
}
