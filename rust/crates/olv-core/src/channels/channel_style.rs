//! Ported from `ChannelStyle.cs`. Owned by package `w1-channels`.
//!
//! Persists per-channel colours and scales, matched to channels by name, so a choice made on
//! one log applies to any other carrying that channel, the way presets and filters do.
//!
//! A pinned colour is worth understanding, because it opts out of something. Trace colours
//! are normally re-picked whenever the scheme changes, since a palette is only separable
//! against the background it was chosen for, and each scheme's palette has been checked for
//! lightness range, contrast and separation under colour-vision deficiency against its own
//! ground. A pinned colour is not re-picked and not re-checked: that is the whole point of
//! pinning one, and the cost is that a colour chosen on a dark scheme may sit poorly on a
//! light one. The picker offers the current scheme's palette first so the easy choice is
//! still a checked one.
//!
//! **File format.** The same JSON the C# wrote: `{"version":1,"channels":[{"channel":..,
//! "color":..,"min":..,"max":..,"smoothing":"none"}]}` with camelCase names, nulls left out
//! and the smoothing level as a camelCase string. Reading accepts any property casing and a
//! numeric smoothing level, as `System.Text.Json` did, and a file that does not fit the shape
//! reads as no styles at all.

use std::path::{Path, PathBuf};

use serde_json::{Map, Number, Value};

use crate::base::json_settings_file;
use crate::base::smoothing::SmoothingLevel;
use crate::model::IgnoreCaseMap;

/// What the user has pinned about one channel's appearance, overriding what the application
/// would choose for it.
///
/// Both parts are optional and independent: pinning a colour says nothing about the scale,
/// and pinning a scale says nothing about the colour.
#[derive(Clone, Debug, PartialEq)]
pub struct ChannelStyle {
    channel: String,
    color: Option<i32>,
    min: Option<f64>,
    max: Option<f64>,
    smoothing: SmoothingLevel,
}

impl ChannelStyle {
    /// A style for a channel (its name is how it is matched) with nothing pinned.
    pub fn new(channel: &str) -> Self {
        Self {
            channel: channel.to_string(),
            color: None,
            min: None,
            max: None,
            smoothing: SmoothingLevel::None,
        }
    }

    /// Packed 0xRRGGBB, or `None` to keep taking the palette's choice.
    pub fn with_color(mut self, color: Option<i32>) -> Self {
        self.color = color;
        self
    }

    /// Bottom of the fixed scale, or `None` for the channel's own range.
    pub fn with_min(mut self, min: Option<f64>) -> Self {
        self.min = min;
        self
    }

    /// Top of it.
    pub fn with_max(mut self, max: Option<f64>) -> Self {
        self.max = max;
        self
    }

    /// How hard to smooth the trace when it is drawn. A way of drawing rather than a change
    /// to the channel, so nothing that measures the engine sees it.
    pub fn with_smoothing(mut self, smoothing: SmoothingLevel) -> Self {
        self.smoothing = smoothing;
        self
    }

    /// The channel's name, which is how this is matched.
    pub fn channel(&self) -> &str {
        &self.channel
    }

    pub fn color(&self) -> Option<i32> {
        self.color
    }

    pub fn min(&self) -> Option<f64> {
        self.min
    }

    pub fn max(&self) -> Option<f64> {
        self.max
    }

    pub fn smoothing(&self) -> SmoothingLevel {
        self.smoothing
    }

    /// Whether the pinned bounds are usable. A pair that is missing an end, or whose ends are
    /// the wrong way round or equal, would divide by zero or invert the trace, so it is
    /// treated as no pin at all rather than trusted.
    pub fn has_range(&self) -> bool {
        matches!((self.min, self.max), (Some(low), Some(high))
            if low.is_finite() && high.is_finite() && high > low)
    }

    pub fn has_color(&self) -> bool {
        matches!(self.color, Some(0..=0xFFFFFF))
    }

    pub fn has_smoothing(&self) -> bool {
        self.smoothing != SmoothingLevel::None
    }

    /// Nothing pinned, so the entry is not worth keeping.
    pub fn is_empty(&self) -> bool {
        !self.has_range() && !self.has_color() && !self.has_smoothing()
    }

    fn to_json(&self) -> Value {
        let mut map = Map::new();
        map.insert("channel".into(), Value::String(self.channel.clone()));
        if let Some(color) = self.color {
            map.insert("color".into(), Value::from(color));
        }
        if let Some(min) = self.min {
            map.insert("min".into(), double(min));
        }
        if let Some(max) = self.max {
            map.insert("max".into(), double(max));
        }
        map.insert("smoothing".into(), Value::String(smoothing_text(self.smoothing).into()));
        Value::Object(map)
    }
}

/// A double as `System.Text.Json` writes it: a whole number has no decimal point. (A
/// non-finite value cannot be written by either; it is left out as `null`.)
fn double(v: f64) -> Value {
    if v.is_finite() && v.fract() == 0.0 && v.abs() < 1e15 {
        Value::from(v as i64)
    } else {
        Number::from_f64(v).map_or(Value::Null, Value::Number)
    }
}

fn smoothing_text(level: SmoothingLevel) -> &'static str {
    match level {
        SmoothingLevel::None => "none",
        SmoothingLevel::Light => "light",
        SmoothingLevel::Medium => "medium",
        SmoothingLevel::Strong => "strong",
    }
}

fn smoothing_from_json(value: &Value) -> Option<SmoothingLevel> {
    match value {
        // Enum names parse without regard to case.
        Value::String(s) => match s.to_ascii_lowercase().as_str() {
            "none" => Some(SmoothingLevel::None),
            "light" => Some(SmoothingLevel::Light),
            "medium" => Some(SmoothingLevel::Medium),
            "strong" => Some(SmoothingLevel::Strong),
            _ => None,
        },
        Value::Number(n) => match n.as_i64()? {
            0 => Some(SmoothingLevel::None),
            1 => Some(SmoothingLevel::Light),
            2 => Some(SmoothingLevel::Medium),
            3 => Some(SmoothingLevel::Strong),
            // The C# would hold an undefined level here; this cannot, so the file is refused.
            _ => None,
        },
        _ => None,
    }
}

/// A property of a JSON object, matched without regard to case (the last one wins when a
/// file repeats it).
fn property<'a>(object: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    object
        .iter()
        .rev()
        .find(|(k, _)| k.eq_ignore_ascii_case(name))
        .map(|(_, v)| v)
}

/// One entry of the file, or `None` where `System.Text.Json` would have thrown.
fn style_from_json(value: &Value) -> Option<Option<ChannelStyle>> {
    let object = match value {
        Value::Object(o) => o,
        // A null element is skipped (the C# would fail on its null channel).
        Value::Null => return Some(None),
        _ => return None,
    };

    let channel = match property(object, "channel") {
        None | Some(Value::Null) => String::new(),
        Some(Value::String(s)) => s.clone(),
        Some(_) => return None,
    };

    let color = match property(object, "color") {
        None | Some(Value::Null) => None,
        Some(Value::Number(n)) => Some(i32::try_from(n.as_i64()?).ok()?),
        Some(_) => return None,
    };

    let bound = |name: &str| -> Option<Option<f64>> {
        match property(object, name) {
            None | Some(Value::Null) => Some(None),
            Some(Value::Number(n)) => Some(Some(n.as_f64()?)),
            Some(_) => None,
        }
    };

    let smoothing = match property(object, "smoothing") {
        None => SmoothingLevel::None,
        Some(v) => smoothing_from_json(v)?,
    };

    Some(Some(ChannelStyle {
        channel,
        color,
        min: bound("min")?,
        max: bound("max")?,
        smoothing,
    }))
}

/// The styles in a settings file's JSON, or `None` where it does not fit the shape.
fn styles_from_json(root: &Value) -> Option<Vec<ChannelStyle>> {
    let Value::Object(root) = root else {
        return None;
    };

    match property(root, "channels") {
        None | Some(Value::Null) => Some(Vec::new()),
        Some(Value::Array(items)) => {
            // Everything is parsed before anything is kept: a file the C# refuses is
            // refused whole.
            let mut styles = Vec::new();
            for item in items {
                if let Some(style) = style_from_json(item)? {
                    styles.push(style);
                }
            }
            Some(styles)
        }
        Some(_) => None,
    }
}

/// `char.ToUpperInvariant`, one character to one, else unchanged: what ordinal ignore-case
/// ordering compares.
fn upper_char(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

/// Persists per-channel colours and scales, matched to channels by name.
pub struct ChannelStyleStore {
    styles: IgnoreCaseMap<ChannelStyle>,
    path: PathBuf,
}

impl ChannelStyleStore {
    /// A generous ceiling. This is a file of deliberate choices, not a cache (nothing writes
    /// to it without the user asking) but a bound keeps a corrupt or hand-edited file from
    /// being read into unbounded memory.
    const MAX_STYLES: usize = 500;

    /// Opens the store at `path`, or at `channels.json` in the application data folder.
    pub fn new(path: Option<&Path>) -> Self {
        let path = path
            .map(Path::to_path_buf)
            .unwrap_or_else(|| json_settings_file::in_app_data("channels.json"));
        let mut store = Self {
            styles: IgnoreCaseMap::new(),
            path,
        };
        store.reload();
        store
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn styles(&self) -> Vec<&ChannelStyle> {
        self.styles.values().collect()
    }

    pub fn reload(&mut self) {
        self.styles = IgnoreCaseMap::new();

        let Some(root) = json_settings_file::read::<Value>(&self.path) else {
            return;
        };
        let Some(styles) = styles_from_json(&root) else {
            return;
        };

        for style in styles {
            if style.channel.trim().is_empty() || style.is_empty() {
                continue;
            }
            if self.styles.len() >= Self::MAX_STYLES {
                break;
            }

            let key = style.channel.clone();
            self.styles.insert(&key, style);
        }
    }

    /// What is pinned for a channel, or `None` where nothing is.
    pub fn for_channel(&self, channel: &str) -> Option<&ChannelStyle> {
        self.styles.get(channel)
    }

    /// Pins a colour, leaving any pinned scale alone. `None` clears it.
    ///
    /// Returns false where the store is full and this is a channel it does not already hold.
    pub fn set_color(&mut self, channel: &str, color: Option<i32>) -> crate::Result<bool> {
        self.update(channel, |s| s.with_color(color))
    }

    /// Pins a scale, leaving any pinned colour alone. Either bound `None` clears it, since
    /// half a scale is not one.
    pub fn set_range(&mut self, channel: &str, min: Option<f64>, max: Option<f64>) -> crate::Result<bool> {
        self.update(channel, |s| s.with_min(min).with_max(max))
    }

    /// Remembers how hard to smooth this channel's trace, leaving the rest alone.
    pub fn set_smoothing(&mut self, channel: &str, level: SmoothingLevel) -> crate::Result<bool> {
        self.update(channel, |s| s.with_smoothing(level))
    }

    /// Unpins everything for a channel, putting it back to automatic.
    pub fn clear(&mut self, channel: &str) -> crate::Result<()> {
        if channel.trim().is_empty() {
            return Ok(());
        }
        if self.styles.remove(channel).is_none() {
            return Ok(());
        }

        self.persist()
    }

    /// Applies a change, or reports that the store is full.
    ///
    /// Refused rather than failed. Every call here comes from a menu click, and an error
    /// out of a click handler is the application closing, which is a great deal worse than
    /// a colour not being remembered, and a strange thing to have the five hundred and first
    /// pinned channel do. (A file that cannot be written is still an error, as in the C#.)
    fn update(&mut self, channel: &str, change: impl FnOnce(ChannelStyle) -> ChannelStyle) -> crate::Result<bool> {
        if channel.trim().is_empty() {
            return Ok(false);
        }

        let existing = self
            .for_channel(channel)
            .cloned()
            .unwrap_or_else(|| ChannelStyle::new(channel));
        let updated = change(existing);

        // An entry that pins nothing is removed rather than stored, so clearing both halves
        // leaves the file as it was before either was set.
        if updated.is_empty() {
            self.styles.remove(channel);
        } else if self.styles.len() < Self::MAX_STYLES || self.styles.contains_key(channel) {
            self.styles.insert(channel, updated);
        } else {
            return Ok(false);
        }

        self.persist()?;

        Ok(true)
    }

    fn persist(&self) -> crate::Result<()> {
        // OrderBy(s => s.Channel, StringComparer.OrdinalIgnoreCase): stable, by upper-cased
        // UTF-16 units.
        let mut ordered: Vec<&ChannelStyle> = self.styles.values().collect();
        ordered.sort_by_cached_key(|s| {
            s.channel
                .chars()
                .map(upper_char)
                .collect::<String>()
                .encode_utf16()
                .collect::<Vec<u16>>()
        });

        let mut file = Map::new();
        file.insert("version".into(), Value::from(1));
        file.insert(
            "channels".into(),
            Value::Array(ordered.iter().map(|s| s.to_json()).collect()),
        );

        json_settings_file::write(&self.path, &Value::Object(file))?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    /// A scratch folder per test, removed afterwards.
    struct Scratch {
        dir: PathBuf,
    }

    impl Scratch {
        fn new() -> Self {
            static COUNTER: AtomicU32 = AtomicU32::new(0);
            let dir = std::env::temp_dir().join(format!(
                "olv-style-{}-{}",
                std::process::id(),
                COUNTER.fetch_add(1, Ordering::Relaxed)
            ));
            let _ = std::fs::remove_dir_all(&dir);
            Self { dir }
        }

        fn path(&self) -> PathBuf {
            self.dir.join("channels.json")
        }

        fn store(&self) -> ChannelStyleStore {
            ChannelStyleStore::new(Some(&self.path()))
        }

        fn write(&self, text: &str) {
            std::fs::create_dir_all(&self.dir).unwrap();
            std::fs::write(self.path(), text).unwrap();
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.dir);
        }
    }

    // ----- the record -------------------------------------------------------

    #[test]
    fn only_a_usable_pair_counts_as_a_range() {
        for (min, max, usable) in [
            (0.0, 100.0, true),
            (-40.0, 40.0, true),
            (100.0, 0.0, false), // the wrong way round
            (50.0, 50.0, false), // no width to divide by
        ] {
            let style = ChannelStyle::new("RPM").with_min(Some(min)).with_max(Some(max));
            assert_eq!(style.has_range(), usable, "{min} {max}");
        }
    }

    #[test]
    fn half_a_scale_is_not_a_scale() {
        assert!(!ChannelStyle::new("RPM").with_min(Some(0.0)).has_range());
        assert!(!ChannelStyle::new("RPM").with_max(Some(8000.0)).has_range());
    }

    #[test]
    fn a_non_finite_bound_is_rejected() {
        let nan = ChannelStyle::new("RPM").with_min(Some(f64::NAN)).with_max(Some(8000.0));
        let inf = ChannelStyle::new("RPM")
            .with_min(Some(0.0))
            .with_max(Some(f64::INFINITY));
        assert!(!nan.has_range());
        assert!(!inf.has_range());
    }

    #[test]
    fn an_entry_pinning_nothing_is_empty() {
        assert!(ChannelStyle::new("RPM").is_empty());
        assert!(!ChannelStyle::new("RPM").with_color(Some(0x3366FF)).is_empty());
        assert!(
            !ChannelStyle::new("RPM")
                .with_min(Some(0.0))
                .with_max(Some(8000.0))
                .is_empty()
        );
    }

    // ----- the store --------------------------------------------------------

    #[test]
    fn a_pinned_colour_survives_a_new_store() {
        let s = Scratch::new();
        s.store().set_color("RPM", Some(0x3366FF)).unwrap();

        assert_eq!(s.store().for_channel("RPM").and_then(|x| x.color()), Some(0x3366FF));
    }

    #[test]
    fn a_pinned_scale_survives_a_new_store() {
        let s = Scratch::new();
        s.store().set_range("RPM", Some(0.0), Some(8000.0)).unwrap();

        let store = s.store();
        let style = store.for_channel("RPM");

        assert_eq!(style.and_then(|x| x.min()), Some(0.0));
        assert_eq!(style.and_then(|x| x.max()), Some(8000.0));
        assert!(style.unwrap().has_range());
    }

    #[test]
    fn colour_and_scale_are_pinned_independently() {
        let s = Scratch::new();
        let mut store = s.store();
        store.set_color("RPM", Some(0x3366FF)).unwrap();
        store.set_range("RPM", Some(0.0), Some(8000.0)).unwrap();

        // Clearing one must not take the other with it.
        store.set_color("RPM", None).unwrap();

        let reread = s.store();
        let style = reread.for_channel("RPM").unwrap();

        assert!(!style.has_color());
        assert!(style.has_range());
    }

    #[test]
    fn clearing_both_halves_removes_the_entry_entirely() {
        let s = Scratch::new();
        let mut store = s.store();
        store.set_color("RPM", Some(0x3366FF)).unwrap();
        store.set_range("RPM", Some(0.0), Some(8000.0)).unwrap();

        store.set_color("RPM", None).unwrap();
        store.set_range("RPM", None, None).unwrap();

        assert!(s.store().for_channel("RPM").is_none());
        assert!(s.store().styles().is_empty());
    }

    #[test]
    fn channels_are_matched_without_regard_to_case() {
        let s = Scratch::new();
        s.store().set_range("Coolant Temp", Some(60.0), Some(120.0)).unwrap();

        assert!(s.store().for_channel("COOLANT TEMP").is_some());
        assert!(s.store().for_channel("coolant temp").is_some());
    }

    #[test]
    fn clear_puts_a_channel_back_to_automatic() {
        let s = Scratch::new();
        let mut store = s.store();
        store.set_color("RPM", Some(0x3366FF)).unwrap();
        store.set_range("RPM", Some(0.0), Some(8000.0)).unwrap();

        store.clear("RPM").unwrap();

        assert!(s.store().for_channel("RPM").is_none());
    }

    #[test]
    fn a_name_that_was_never_pinned_has_no_style() {
        let s = Scratch::new();
        assert!(s.store().for_channel("Nothing here").is_none());
    }

    #[test]
    fn a_missing_file_is_an_empty_store_rather_than_a_failure() {
        let s = Scratch::new();
        let store = s.store();

        assert!(store.styles().is_empty());
        assert!(!s.path().exists());
    }

    #[test]
    fn a_garbled_file_is_read_as_no_styles_rather_than_failing() {
        let s = Scratch::new();
        s.write("{ this is not json");

        assert!(s.store().styles().is_empty());
    }

    #[test]
    fn an_entry_with_no_channel_name_is_skipped_on_read() {
        let s = Scratch::new();
        s.write(
            r#"
            { "Version": 1, "Channels": [
              { "Channel": "", "Color": 255 },
              { "Channel": "RPM", "Color": 255 }
            ] }
            "#,
        );

        assert_eq!(s.store().styles().len(), 1);
        assert!(s.store().for_channel("RPM").is_some());
    }

    #[test]
    fn an_entry_pinning_nothing_is_skipped_on_read() {
        // Otherwise a hand-edited file could carry rows that mean nothing and would be
        // written straight back out again.
        let s = Scratch::new();
        s.write(r#"{ "Version": 1, "Channels": [ { "Channel": "RPM" } ] }"#);

        assert!(s.store().styles().is_empty());
    }

    #[test]
    fn a_pinned_range_the_wrong_way_round_is_dropped_on_read() {
        // Written by hand rather than by the app, which refuses the pair. An entry that can
        // never be honoured pins nothing, so it is dropped like any other empty one rather
        // than kept as dead weight in the file.
        let s = Scratch::new();
        s.write(r#"{ "Version": 1, "Channels": [ { "Channel": "RPM", "Min": 8000, "Max": 0 } ] }"#);

        assert!(s.store().for_channel("RPM").is_none());
    }

    #[test]
    fn a_colour_outside_twenty_four_bits_is_not_honoured() {
        // Colours are packed 0xRRGGBB; anything else came from a hand-edited file and would
        // be unpacked into a colour nobody chose.
        assert!(!ChannelStyle::new("RPM").with_color(Some(-1)).has_color());
        assert!(!ChannelStyle::new("RPM").with_color(Some(0x1000000)).has_color());
        assert!(ChannelStyle::new("RPM").with_color(Some(0xFFFFFF)).has_color());
        assert!(ChannelStyle::new("RPM").with_color(Some(0)).has_color());
    }

    // ----- the file format (not in the C# tests: guards byte compatibility) ---

    #[test]
    fn the_file_is_written_in_the_shape_the_csharp_wrote() {
        let s = Scratch::new();
        let mut store = s.store();
        store.set_range("rpm", Some(0.0), Some(8000.5)).unwrap();
        store.set_color("Boost", Some(255)).unwrap();
        store.set_smoothing("Boost", SmoothingLevel::Medium).unwrap();

        let text = std::fs::read_to_string(s.path()).unwrap();
        assert_eq!(
            text,
            "{\n  \"version\": 1,\n  \"channels\": [\n    {\n      \"channel\": \"Boost\",\n      \"color\": 255,\n      \"smoothing\": \"medium\"\n    },\n    {\n      \"channel\": \"rpm\",\n      \"min\": 0,\n      \"max\": 8000.5,\n      \"smoothing\": \"none\"\n    }\n  ]\n}"
        );

        let reread = s.store();
        assert_eq!(
            reread.for_channel("BOOST").map(|x| x.smoothing()),
            Some(SmoothingLevel::Medium)
        );
    }
}
