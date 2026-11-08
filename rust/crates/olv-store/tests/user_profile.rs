//! The C# `UserProfileTests`: what belongs to the person using this, and what ships with it.
//!
//! The line matters in both directions. Somebody's saved channel selections, their filters and
//! the ECUs they own are theirs, and must not travel with the application to anybody else. And
//! the application's own defaults must not depend on any of that, or a new install behaves
//! differently from a developer's machine and nobody can reproduce it.
//!
//! These live in `olv-store` because they use its `SettingsStore` and `PresetStore` together
//! with `olv-core`'s `FilterStore` and `MathChannelStore`.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use olv_core::filters::filter_store::FilterStore;
use olv_core::filters::log_filter::SampleFilter;
use olv_core::math::math_channel_store::MathChannelStore;
use olv_core::model::{IgnoreCaseMap, LogChannel, LogDocument};
use olv_core::units::UnitSystem;
use olv_store::store::preset_store::PresetStore;
use olv_store::store::settings_store::SettingsStore;
use time::{Duration, OffsetDateTime};

static NEXT: AtomicUsize = AtomicUsize::new(0);

/// Temp files removed when the set goes out of scope (the C#'s `IDisposable`).
#[derive(Default)]
struct Temp(Vec<PathBuf>);

impl Temp {
    fn file(&mut self, name: &str) -> PathBuf {
        let path = std::env::temp_dir().join(format!(
            "olv-{name}-{}-{}.json",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        self.0.push(path.clone());
        path
    }
}

impl Drop for Temp {
    fn drop(&mut self) {
        for p in &self.0 {
            let _ = std::fs::remove_file(p);
        }
    }
}

fn one(key: &str, value: &str) -> IgnoreCaseMap<String> {
    let mut map = IgnoreCaseMap::new();
    map.insert(key, value.to_string());
    map
}

fn times(entries: &[(&str, OffsetDateTime)]) -> IgnoreCaseMap<OffsetDateTime> {
    let mut map = IgnoreCaseMap::new();
    for (key, when) in entries {
        map.insert(key, *when);
    }
    map
}

// ----- a new install is empty ---------------------------------------------

/// Nothing is seeded. A first run has no presets, no filters and no calculated channels:
/// whatever is on the machine that built the release stays on it.
#[test]
fn a_fresh_profile_has_nothing_in_it() {
    let mut temp = Temp::default();

    assert!(PresetStore::new(temp.file("presets")).presets().is_empty());
    assert!(FilterStore::new(Some(&temp.file("filters"))).filters().is_empty());
    assert!(MathChannelStore::new(Some(&temp.file("math"))).channels().is_empty());

    let settings = SettingsStore::new(temp.file("settings"));

    assert!(settings.known_ecus().is_empty());
    assert!(settings.ecu_last_used().is_empty());
    assert_eq!(None, settings.recording_folder());
    assert_eq!(None, settings.data_folder());
    assert_eq!(None, settings.theme_id());
}

/// The suggested filters come from the log in front of you, not from a list somebody else
/// wrote. A log with different channels gets different suggestions, and they arrive switched
/// off either way.
#[test]
fn suggested_filters_come_from_the_log_rather_than_from_a_profile() {
    let document = LogDocument::new(
        "",
        "test",
        vec![
            Arc::new(LogChannel::adopt("CLT", "F", 0, vec![70.0, 120.0, 180.0])),
            Arc::new(LogChannel::adopt("RPM", "rpm", 0, vec![0.0, 2000.0, 4000.0])),
        ],
        Arc::new(LogChannel::new("Time", "s", 3, &[0.0, 1.0, 2.0], false)),
    );

    let suggested = SampleFilter::suggest(&document);

    assert!(!suggested.is_empty());
    for f in &suggested {
        assert!(!f.enabled(), "{} should arrive switched off", f.name());
        assert!(
            document.channels().iter().any(|c| c.name() == f.channel()),
            "{} names a channel the log lacks",
            f.channel()
        );
    }
}

// ----- devices are remembered, and can be forgotten -----------------------

#[test]
fn a_device_and_when_it_was_used_survive_a_restart() {
    let mut temp = Temp::default();
    let path = temp.file("settings");
    let when = OffsetDateTime::now_utc() - Duration::minutes(5);

    let settings = SettingsStore::new(path.clone());
    settings
        .set_known_ecus(one("USB\\VID_2341", "Speeduino 2025.01.7"))
        .unwrap();
    settings.set_ecu_last_used(times(&[("USB\\VID_2341", when)])).unwrap();

    let reopened = SettingsStore::new(path);

    assert_eq!(
        Some(&"Speeduino 2025.01.7".to_string()),
        reopened.known_ecus().get("USB\\VID_2341")
    );

    // To the second: the file stores a round-trip string, and the point of the value is
    // ordering rather than precision.
    assert_eq!(
        when.unix_timestamp(),
        reopened.ecu_last_used().get("USB\\VID_2341").unwrap().unix_timestamp()
    );
}

/// A settings file written before use times were recorded still loads, and its devices are
/// still remembered: they simply have no time, which sorts them below the ones that do.
#[test]
fn an_older_profile_without_use_times_still_loads() {
    let mut temp = Temp::default();
    let path = temp.file("settings");

    std::fs::write(
        &path,
        r#"{"version":1,"knownEcus":{"USB\\VID_067B":"MS2/Extra 3.4.1"}}"#,
    )
    .unwrap();

    let settings = SettingsStore::new(path);

    assert_eq!(
        Some(&"MS2/Extra 3.4.1".to_string()),
        settings.known_ecus().get("USB\\VID_067B")
    );
    assert!(settings.ecu_last_used().is_empty());
}

/// Forgetting takes the devices and nothing else. The reason to reach for it is a dongle that
/// has been sold, not a wish to lose an afternoon's filters.
#[test]
fn forgetting_devices_leaves_everything_else_alone() {
    let mut temp = Temp::default();
    let path = temp.file("settings");

    let settings = SettingsStore::new(path.clone());
    settings.set_known_ecus(one("USB\\VID_2341", "Speeduino")).unwrap();
    settings
        .set_ecu_last_used(times(&[("USB\\VID_2341", OffsetDateTime::now_utc())]))
        .unwrap();
    settings.set_theme(Some("gruvbox")).unwrap();
    settings.set_units(UnitSystem::Imperial).unwrap();
    settings.set_live_rate(5.0).unwrap();

    settings.forget_known_ecus().unwrap();

    assert!(settings.known_ecus().is_empty());
    assert!(settings.ecu_last_used().is_empty());

    // The preferences are not devices and are not swept up.
    assert_eq!(Some("gruvbox".to_string()), settings.theme_id());
    assert_eq!(UnitSystem::Imperial, settings.units());
    assert_eq!(5.0, settings.live_rate());

    // And it stuck.
    assert!(SettingsStore::new(path).known_ecus().is_empty());
}

#[test]
fn forgetting_when_nothing_is_remembered_is_harmless() {
    let mut temp = Temp::default();
    let settings = SettingsStore::new(temp.file("settings"));

    settings.forget_known_ecus().unwrap();
    settings.forget_known_ecus().unwrap();

    assert!(settings.known_ecus().is_empty());
}

/// Both halves go together. A signature with no time, or a time with no signature, is half a
/// memory, and the half that survived would put a device in a list that nothing else knows
/// anything about.
#[test]
fn forgetting_clears_both_halves_of_the_memory() {
    let mut temp = Temp::default();
    let path = temp.file("settings");

    let settings = SettingsStore::new(path.clone());
    let mut known = one("a", "one");
    known.insert("b", "two".to_string());
    settings.set_known_ecus(known).unwrap();
    settings
        .set_ecu_last_used(times(&[
            ("a", OffsetDateTime::now_utc()),
            ("b", OffsetDateTime::now_utc() - Duration::days(1)),
        ]))
        .unwrap();

    settings.forget_known_ecus().unwrap();

    let reopened = SettingsStore::new(path);

    assert!(reopened.known_ecus().is_empty());
    assert!(reopened.ecu_last_used().is_empty());
}
