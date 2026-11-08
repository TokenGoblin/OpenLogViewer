//! Ported from `JsonSettingsFile.cs`: reads and writes the small JSON files in the
//! application's data folder that hold user settings.
//!
//! These are plain text people will hand-edit, so reads accept comments and trailing commas,
//! and a malformed file is treated as absent rather than allowed to stop the app.
//!
//! What `System.Text.Json`'s options did in the C#, and where each one lives here:
//!
//! - comments skipped and trailing commas allowed: done by [`read`] before parsing.
//! - indented output, nulls left out of objects: done by [`write`].
//! - camel-case property names and camel-case string enums: serde attributes on the settings
//!   types themselves (`#[serde(rename_all = "camelCase")]`), since serde has no global
//!   naming policy.
//! - case-insensitive property names on read: not available in serde; see the report. A type
//!   that must accept hand-edited casing needs `#[serde(alias = "...")]` on its fields.
//! - indentation is two spaces and line ends are `\n` (the C# wrote `Environment.NewLine`).

use serde::Serialize;
use serde::de::DeserializeOwned;
use serde_json::Value;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Removes `//` and `/* */` comments and trailing commas, leaving string contents alone, so
/// that strict JSON parsing accepts what `System.Text.Json` was configured to accept.
fn make_lenient_json_strict(text: &str) -> String {
    let chars: Vec<char> = text.chars().collect();
    let mut out = String::with_capacity(text.len());
    let mut i = 0;

    while i < chars.len() {
        let c = chars[i];
        match c {
            '"' => {
                out.push(c);
                i += 1;
                while i < chars.len() {
                    let s = chars[i];
                    out.push(s);
                    i += 1;
                    if s == '\\' && i < chars.len() {
                        out.push(chars[i]);
                        i += 1;
                    } else if s == '"' {
                        break;
                    }
                }
            }
            '/' if chars.get(i + 1) == Some(&'/') => {
                while i < chars.len() && chars[i] != '\n' {
                    i += 1;
                }
            }
            '/' if chars.get(i + 1) == Some(&'*') => {
                i += 2;
                while i < chars.len() && !(chars[i] == '*' && chars.get(i + 1) == Some(&'/')) {
                    i += 1;
                }
                i = (i + 2).min(chars.len());
                // A comment separates tokens, as whitespace does.
                out.push(' ');
            }
            ',' => {
                // A comma followed (past whitespace and comments) by a closer is trailing.
                let mut j = i + 1;
                loop {
                    while j < chars.len() && chars[j].is_whitespace() {
                        j += 1;
                    }
                    if chars.get(j) == Some(&'/') && chars.get(j + 1) == Some(&'/') {
                        while j < chars.len() && chars[j] != '\n' {
                            j += 1;
                        }
                    } else if chars.get(j) == Some(&'/') && chars.get(j + 1) == Some(&'*') {
                        j += 2;
                        while j < chars.len() && !(chars[j] == '*' && chars.get(j + 1) == Some(&'/')) {
                            j += 1;
                        }
                        j = (j + 2).min(chars.len());
                    } else {
                        break;
                    }
                }
                if !matches!(chars.get(j), Some('}' | ']')) {
                    out.push(',');
                }
                i += 1;
            }
            _ => {
                out.push(c);
                i += 1;
            }
        }
    }

    out
}

/// Reads a settings file. A missing, unreadable or malformed file is `None`.
pub fn read<T: DeserializeOwned>(path: &Path) -> Option<T> {
    if !path.is_file() {
        return None;
    }

    // File.ReadAllText: strips a byte-order mark, and replaces invalid UTF-8 rather than
    // failing.
    let bytes = std::fs::read(path).ok()?;
    let text = String::from_utf8_lossy(&bytes);
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);

    serde_json::from_str(&make_lenient_json_strict(text)).ok()
}

/// Removes `null` properties from objects, as `JsonIgnoreCondition.WhenWritingNull` does
/// (array elements are kept).
fn strip_null_properties(value: &mut Value) {
    match value {
        Value::Object(map) => {
            map.retain(|_, v| !v.is_null());
            for v in map.values_mut() {
                strip_null_properties(v);
            }
        }
        Value::Array(items) => {
            for v in items {
                strip_null_properties(v);
            }
        }
        _ => {}
    }
}

/// Writes via a temporary file, so an interrupted save cannot leave the user with a
/// truncated settings file.
pub fn write<T: Serialize>(path: &Path, value: &T) -> io::Result<()> {
    if let Some(directory) = path.parent()
        && !directory.as_os_str().is_empty()
    {
        std::fs::create_dir_all(directory)?;
    }

    // A scratch name of its own per write, rather than one shared "settings.json.tmp". Two
    // writers landing together (a second copy of the application, or a background thread
    // noting something while the window saves a preference) otherwise write into the same
    // file each is half way through, and what lands is not one of the two versions but a
    // splice of both.
    let mut temp_name = path.as_os_str().to_owned();
    temp_name.push(format!(".{}.tmp", unique_token()));
    let temp = PathBuf::from(temp_name);

    let result = (|| {
        let mut json = serde_json::to_value(value)?;
        strip_null_properties(&mut json);
        std::fs::write(&temp, serde_json::to_string_pretty(&json)?)?;
        replace(&temp, path)
    })();

    if result.is_err() {
        // A write that failed must not leave its scratch file behind as well; with a name of
        // its own, nothing else would ever clear it up. Nothing useful to do about a scratch
        // file that will not go.
        let _ = std::fs::remove_file(&temp);
    }

    result
}

/// 32 hex digits that differ between writers, threads and calls (the C# used a GUID).
fn unique_token() -> String {
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or(Duration::ZERO)
        .as_nanos() as u64;

    format!(
        "{:016x}{:08x}{:08x}",
        nanos,
        std::process::id(),
        COUNTER.fetch_add(1, Ordering::Relaxed) as u32
    )
}

/// Attempts at the move before a save is reported failed.
const MOVE_ATTEMPTS: u32 = 12;

/// Moves the finished file into place, retrying briefly.
///
/// Two writers replacing the same destination collide even with a scratch file each: Windows
/// reports the sharing violation as an access error, and it is transient: what holds the
/// destination is the other writer's own move, finishing. Two copies of the application
/// running at once are enough to produce it.
///
/// Worth retrying rather than failing because of where the failure comes out. A failed save
/// is not only a lost preference: this is also how a dead link is written down, from a
/// background thread, in the middle of a recovery that the error would abandon.
fn replace(temp: &Path, path: &Path) -> io::Result<()> {
    let mut attempt = 1;
    loop {
        match std::fs::rename(temp, path) {
            Ok(()) => return Ok(()),
            Err(e) if attempt >= MOVE_ATTEMPTS => return Err(e),
            Err(_) => {
                // Longer each time. The collision clears in milliseconds, and a tight spin
                // against a file lock is a good way to extend one.
                std::thread::sleep(Duration::from_millis(u64::from(attempt) * 5));
                attempt += 1;
            }
        }
    }
}

/// Where a settings file lives in the per-user application data folder, under
/// `OpenLogViewer`.
///
/// The C# asked for `Environment.SpecialFolder.ApplicationData` (`%APPDATA%`, a Windows
/// location). The intent is ported per OS: `%APPDATA%` on Windows,
/// `~/Library/Application Support` on macOS, `$XDG_CONFIG_HOME` or `~/.config` elsewhere.
pub fn in_app_data(file_name: &str) -> PathBuf {
    app_data_folder().join("OpenLogViewer").join(file_name)
}

fn app_data_folder() -> PathBuf {
    let var = |name: &str| std::env::var_os(name).filter(|v| !v.is_empty());

    if let Some(appdata) = var("APPDATA") {
        return PathBuf::from(appdata);
    }

    let home = var("HOME")
        .or_else(|| var("USERPROFILE"))
        .map(PathBuf::from)
        .unwrap_or_default();

    if cfg!(target_os = "macos") {
        home.join("Library").join("Application Support")
    } else if let Some(xdg) = var("XDG_CONFIG_HOME") {
        PathBuf::from(xdg)
    } else {
        home.join(".config")
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::{Deserialize, Serialize};

    #[derive(Debug, PartialEq, Serialize, Deserialize)]
    #[serde(rename_all = "camelCase")]
    struct Sample {
        window_width: i32,
        #[serde(skip_serializing_if = "Option::is_none")]
        note: Option<String>,
        mode: Mode,
    }

    #[derive(Debug, PartialEq, Serialize, Deserialize)]
    #[serde(rename_all = "camelCase")]
    enum Mode {
        DarkMode,
    }

    fn scratch(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("olv-json-{}", unique_token()));
        std::fs::create_dir_all(&dir).unwrap();
        dir.join(name)
    }

    #[test]
    fn a_file_with_comments_and_trailing_commas_is_read() {
        let path = scratch("a.json");
        std::fs::write(
            &path,
            "\u{feff}{\n // hand edited\n \"windowWidth\": 800, /* x */\n \"note\": \"a//b, }\",\n \"mode\": \"darkMode\",\n}",
        )
        .unwrap();

        let read: Sample = read(&path).unwrap();
        assert_eq!(read.window_width, 800);
        assert_eq!(read.note.as_deref(), Some("a//b, }"));
    }

    #[test]
    fn a_missing_or_malformed_file_is_absent() {
        assert!(read::<Sample>(&scratch("missing.json")).is_none());

        let path = scratch("bad.json");
        std::fs::write(&path, "{ not json").unwrap();
        assert!(read::<Sample>(&path).is_none());
    }

    #[test]
    fn a_written_file_reads_back_and_leaves_no_scratch_file() {
        let path = scratch("nested/b.json");
        let value = Sample {
            window_width: 5,
            note: None,
            mode: Mode::DarkMode,
        };

        write(&path, &value).unwrap();

        assert_eq!(read::<Sample>(&path), Some(value));
        let text = std::fs::read_to_string(&path).unwrap();
        assert_eq!(text, "{\n  \"windowWidth\": 5,\n  \"mode\": \"darkMode\"\n}");
        assert_eq!(std::fs::read_dir(path.parent().unwrap()).unwrap().count(), 1);
    }

    #[test]
    fn nulls_are_left_out_of_objects_even_without_a_skip_attribute() {
        #[derive(Serialize)]
        struct Loose {
            kept: i32,
            dropped: Option<i32>,
        }
        let path = scratch("c.json");

        write(&path, &Loose { kept: 1, dropped: None }).unwrap();

        assert_eq!(std::fs::read_to_string(&path).unwrap(), "{\n  \"kept\": 1\n}");
    }

    #[test]
    fn the_app_data_path_ends_in_the_application_folder() {
        let path = in_app_data("settings.json");
        assert!(path.ends_with(Path::new("OpenLogViewer").join("settings.json")));
    }
}
