//! Ported from `FilterStore.cs`. Owned by package `w2-filters`.
//!
//! Persists the user's filter conditions. Filters are matched to channels by name, so a set
//! written against one log applies to any other that shares those channel names.
//!
//! **File format.** The same JSON the C# wrote: `{"version":1,"filters":[{"name":..,
//! "channel":..,"comparison":"aboveOrEqual","low":..,"high":..,"enabled":true}]}`, camelCase
//! names, the comparison as a camelCase string. Reading accepts any property casing, comments
//! and trailing commas, and a number for the comparison, as `System.Text.Json` did. Where the
//! C# refused a file (a missing `name`, `channel` or `comparison`, a value of the wrong type,
//! an unreadable version) the whole file reads as no filters, not just the entry at fault.

use std::path::{Path, PathBuf};

use serde_json::{Map, Number, Value};

use super::log_filter::{FilterComparison, LogFilter};
use crate::base::json_settings_file;
use crate::{Error, Result};

/// The most filters a user may keep. A bound on the file, so a hand-edited one cannot be read
/// into unbounded memory.
const MAX_FILTERS: usize = 60;

/// Persists the user's filter conditions.
pub struct FilterStore {
    filters: Vec<LogFilter>,
    path: PathBuf,
}

impl FilterStore {
    /// Opens the store at `path`, or at `filters.json` in the application data folder.
    pub fn new(path: Option<&Path>) -> Self {
        let path = path
            .map(Path::to_path_buf)
            .unwrap_or_else(|| json_settings_file::in_app_data("filters.json"));
        let mut store = Self {
            filters: Vec::new(),
            path,
        };
        store.reload();
        store
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn filters(&self) -> &[LogFilter] {
        &self.filters
    }

    /// Reads the file again. A missing or unreadable file, or one that does not fit the shape,
    /// leaves no filters.
    pub fn reload(&mut self) {
        self.filters.clear();

        let Some(root) = json_settings_file::read::<Value>(&self.path) else {
            return;
        };
        let Some(filters) = filters_from_json(&root) else {
            return;
        };

        for filter in filters {
            if filter.name().trim().is_empty() || filter.channel().trim().is_empty() {
                continue;
            }
            if self.filters.len() >= MAX_FILTERS {
                break;
            }
            self.filters.push(filter);
        }
    }

    /// Replaces every filter, keeping at most the limit, and saves.
    pub fn replace(&mut self, filters: Vec<LogFilter>) -> Result<()> {
        self.filters = filters.into_iter().take(MAX_FILTERS).collect();
        self.persist()
    }

    /// Adds a filter and saves. Refused at the limit.
    pub fn add(&mut self, filter: LogFilter) -> Result<()> {
        if self.filters.len() >= MAX_FILTERS {
            return Err(Error::InvalidOperation(format!(
                "There is a limit of {MAX_FILTERS} filters."
            )));
        }

        self.filters.push(filter);
        self.persist()
    }

    /// Removes the first filter equal to `filter` and saves. Returns false where none was.
    pub fn remove(&mut self, filter: &LogFilter) -> Result<bool> {
        let Some(index) = self.filters.iter().position(|f| f == filter) else {
            return Ok(false);
        };

        self.filters.remove(index);
        self.persist()?;
        Ok(true)
    }

    fn persist(&self) -> Result<()> {
        let mut root = Map::new();
        root.insert("version".into(), Value::from(1));
        root.insert(
            "filters".into(),
            Value::Array(self.filters.iter().map(filter_to_json).collect::<Result<Vec<_>>>()?),
        );

        json_settings_file::write(&self.path, &Value::Object(root)).map_err(Error::Io)
    }
}

/// A property of a JSON object, matched without regard to case (the last one wins when a file
/// repeats it).
fn property<'a>(object: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    object
        .iter()
        .rev()
        .find(|(k, _)| k.eq_ignore_ascii_case(name))
        .map(|(_, v)| v)
}

/// A double as `System.Text.Json` writes it: a whole number has no decimal point. A non-finite
/// value is refused, as the C# serializer refuses it.
fn double(v: f64) -> Result<Value> {
    if !v.is_finite() {
        return Err(Error::Json(format!("The specified value '{v}' is not valid JSON.")));
    }
    if v.fract() == 0.0 && v.abs() < 1e15 {
        Ok(Value::from(v as i64))
    } else {
        Ok(Number::from_f64(v).map_or(Value::Null, Value::Number))
    }
}

fn filter_to_json(filter: &LogFilter) -> Result<Value> {
    let comparison = match filter.comparison().json_name() {
        Some(name) => Value::String(name.into()),
        None => Value::from(filter.comparison().index()),
    };

    let mut map = Map::new();
    map.insert("name".into(), Value::String(filter.name().into()));
    map.insert("channel".into(), Value::String(filter.channel().into()));
    map.insert("comparison".into(), comparison);
    map.insert("low".into(), double(filter.low())?);
    map.insert("high".into(), double(filter.high())?);
    map.insert("enabled".into(), Value::Bool(filter.enabled()));
    Ok(Value::Object(map))
}

/// The comparison from its name (ignoring case, as the C# enum converter does) or its number.
fn comparison_from_json(value: &Value) -> Option<FilterComparison> {
    match value {
        Value::String(text) => FilterComparison::NAMED
            .into_iter()
            .find(|c| c.json_name().is_some_and(|name| name.eq_ignore_ascii_case(text))),
        Value::Number(n) => i32::try_from(n.as_i64()?).ok().map(FilterComparison::from_index),
        _ => None,
    }
}

/// The filters in a settings file's JSON, or `None` where the file does not fit the shape.
fn filters_from_json(root: &Value) -> Option<Vec<LogFilter>> {
    let Value::Object(root) = root else {
        return None;
    };

    // The version is read and not used, but a value that will not read as an int refuses the file.
    match property(root, "version") {
        None => {}
        Some(Value::Number(n)) if n.as_i64().is_some_and(|v| i32::try_from(v).is_ok()) => {}
        Some(_) => return None,
    }

    match property(root, "filters") {
        None | Some(Value::Null) => Some(Vec::new()),
        Some(Value::Array(items)) => {
            // Everything is parsed before anything is kept: a file the C# refuses is refused whole.
            let mut filters = Vec::new();
            for item in items {
                if let Some(filter) = filter_from_json(item)? {
                    filters.push(filter);
                }
            }
            Some(filters)
        }
        Some(_) => None,
    }
}

/// One entry of the file, `Some(None)` for a null element (skipped), or `None` where
/// `System.Text.Json` would have thrown.
fn filter_from_json(value: &Value) -> Option<Option<LogFilter>> {
    let object = match value {
        Value::Object(o) => o,
        // The C# would fail on the null element in Reload. Skipping it is the nearest reading
        // that does not take the application down.
        Value::Null => return Some(None),
        _ => return None,
    };

    // Required properties: missing is a refusal, null reads as blank (and is then skipped).
    let required_text = |name: &str| -> Option<String> {
        match property(object, name) {
            None => None,
            Some(Value::Null) => Some(String::new()),
            Some(Value::String(s)) => Some(s.clone()),
            Some(_) => None,
        }
    };

    let name = required_text("name")?;
    let channel = required_text("channel")?;
    let comparison = comparison_from_json(property(object, "comparison")?)?;

    // Optional properties take the C# defaults when missing, but a null is a refusal: the C#
    // types are not nullable.
    let number = |name: &str| -> Option<f64> {
        match property(object, name) {
            None => Some(0.0),
            Some(Value::Number(n)) => n.as_f64(),
            Some(_) => None,
        }
    };
    let low = number("low")?;
    let high = number("high")?;

    let enabled = match property(object, "enabled") {
        None => true,
        Some(Value::Bool(b)) => *b,
        Some(_) => return None,
    };

    Some(Some(
        LogFilter::new(&name, &channel, comparison)
            .with_low(low)
            .with_high(high)
            .with_enabled(enabled),
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    /// A file in the temp folder, removed when the test ends.
    struct Scratch(PathBuf);

    impl Scratch {
        fn new(name: &str) -> Self {
            static COUNTER: AtomicU64 = AtomicU64::new(0);
            let n = COUNTER.fetch_add(1, Ordering::Relaxed);
            Self(std::env::temp_dir().join(format!("olv-filters-{name}-{}-{n}.json", std::process::id())))
        }

        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    fn filter(name: &str, channel: &str) -> LogFilter {
        LogFilter::new(name, channel, FilterComparison::AboveOrEqual).with_low(160.0)
    }

    #[test]
    fn a_new_store_without_a_file_is_empty() {
        let scratch = Scratch::new("absent");
        assert!(FilterStore::new(Some(scratch.path())).filters().is_empty());
    }

    #[test]
    fn filters_survive_a_restart_in_the_c_sharp_shape() {
        let scratch = Scratch::new("round-trip");

        let mut store = FilterStore::new(Some(scratch.path()));
        store.add(filter("Up to temperature", "CLT").with_high(0.0)).unwrap();
        store
            .add(
                LogFilter::new("Lambda in range", "Lambda", FilterComparison::Between)
                    .with_low(0.6)
                    .with_high(1.4)
                    .with_enabled(false),
            )
            .unwrap();

        let text = std::fs::read_to_string(scratch.path()).unwrap();
        assert!(text.contains("\"version\": 1"));
        assert!(text.contains("\"comparison\": \"aboveOrEqual\""));
        assert!(text.contains("\"low\": 160"));
        assert!(text.contains("\"enabled\": false"));

        let reopened = FilterStore::new(Some(scratch.path()));
        assert_eq!(reopened.filters().len(), 2);
        assert_eq!(reopened.filters()[1].name(), "Lambda in range");
        assert_eq!(reopened.filters()[1].high(), 1.4);
        assert!(!reopened.filters()[1].enabled());
    }

    #[test]
    fn a_hand_edited_file_with_other_casing_and_comments_reads() {
        let scratch = Scratch::new("lenient");
        std::fs::write(
            scratch.path(),
            r#"{
              // written by hand
              "Version": 1,
              "FILTERS": [
                { "NAME": "Idle", "Channel": "TPS", "Comparison": "above", "Low": 1, },
              ],
            }"#,
        )
        .unwrap();

        let store = FilterStore::new(Some(scratch.path()));

        assert_eq!(store.filters().len(), 1);
        assert_eq!(store.filters()[0].name(), "Idle");
        assert_eq!(store.filters()[0].comparison(), FilterComparison::Above);
        assert!(store.filters()[0].enabled());
    }

    #[test]
    fn one_entry_missing_a_required_property_refuses_the_whole_file() {
        // As System.Text.Json does with a required property: the file reads as absent, so one bad
        // entry costs every filter, not just itself.
        let scratch = Scratch::new("refused");
        std::fs::write(
            scratch.path(),
            r#"{"version":1,"filters":[
                {"name":"Good","channel":"CLT","comparison":"above","low":1},
                {"name":"Bad","channel":"CLT","low":1}
            ]}"#,
        )
        .unwrap();

        assert!(FilterStore::new(Some(scratch.path())).filters().is_empty());
    }

    #[test]
    fn blank_names_are_skipped_and_the_count_is_capped() {
        let scratch = Scratch::new("cap");
        let mut entries: Vec<String> = vec![r#"{"name":"  ","channel":"CLT","comparison":"above"}"#.into()];
        entries.extend((0..70).map(|i| format!(r#"{{"name":"f{i}","channel":"CLT","comparison":"above"}}"#)));
        std::fs::write(
            scratch.path(),
            format!(r#"{{"version":1,"filters":[{}]}}"#, entries.join(",")),
        )
        .unwrap();

        let store = FilterStore::new(Some(scratch.path()));

        assert_eq!(store.filters().len(), 60);
        assert_eq!(store.filters()[0].name(), "f0");
    }

    #[test]
    fn add_is_refused_at_the_limit() {
        let scratch = Scratch::new("limit");
        let mut store = FilterStore::new(Some(scratch.path()));
        for i in 0..MAX_FILTERS {
            store.add(filter(&format!("f{i}"), "CLT")).unwrap();
        }

        let error = store.add(filter("one too many", "CLT")).unwrap_err();

        assert!(matches!(error, Error::InvalidOperation(m) if m == "There is a limit of 60 filters."));
    }

    #[test]
    fn remove_takes_the_first_equal_filter_and_says_whether_it_did() {
        let scratch = Scratch::new("remove");
        let mut store = FilterStore::new(Some(scratch.path()));
        store.add(filter("a", "CLT")).unwrap();
        store.add(filter("b", "CLT")).unwrap();

        assert!(store.remove(&filter("a", "CLT")).unwrap());
        assert!(!store.remove(&filter("a", "CLT")).unwrap());
        assert_eq!(store.filters().len(), 1);
        assert_eq!(FilterStore::new(Some(scratch.path())).filters()[0].name(), "b");
    }

    #[test]
    fn a_non_finite_bound_is_refused_on_save() {
        // The C# serializer throws on NaN; the filter is still in memory, as in the C#.
        let scratch = Scratch::new("nan");
        let mut store = FilterStore::new(Some(scratch.path()));

        let result = store.add(LogFilter::new("x", "CLT", FilterComparison::Above).with_low(f64::NAN));

        assert!(matches!(result, Err(Error::Json(_))));
        assert_eq!(store.filters().len(), 1);
    }
}
