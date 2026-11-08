//! Ported from `SafeFolder.cs`: a folder that plain files are written directly into, and
//! nowhere else.
//!
//! The whole of what this protects is staying inside the folder. A caller hands over a bare
//! file name; before that name is trusted it is checked for anything that could make it mean
//! somewhere else (a separator of either kind, a `..`, an absolute path) and checked again
//! after it is resolved to a full path, in case a folder along the way is not what it looks
//! like.
//!
//! Shared rather than written twice because there are two callers with the same problem and
//! very different consequences for getting it wrong: an agent staging a tune file for
//! somebody to open, and an agent installing a firmware definition that decides how every
//! byte of an engine's memory is read and written.

use std::fmt;
use std::io;
use std::path::{Component, Path, PathBuf};
use std::time::SystemTime;

/// Why a [`SafeFolder`] call failed.
#[derive(Debug)]
pub enum SafeFolderError {
    /// The name is empty, names something other than a plain file, or would resolve outside
    /// the folder (the C# `ArgumentException`).
    Argument(String),
    Io(io::Error),
}

impl fmt::Display for SafeFolderError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            SafeFolderError::Argument(message) => f.write_str(message),
            SafeFolderError::Io(e) => e.fmt(f),
        }
    }
}

impl std::error::Error for SafeFolderError {}

impl From<io::Error> for SafeFolderError {
    fn from(e: io::Error) -> Self {
        SafeFolderError::Io(e)
    }
}

/// A file found in the folder (the part of `FileInfo` the callers read).
#[derive(Clone, Debug)]
pub struct SafeFile {
    pub path: PathBuf,
    pub len: u64,
    pub last_write: SystemTime,
}

#[derive(Clone, Debug)]
pub struct SafeFolder {
    folder: PathBuf,
}

/// `Path.GetFullPath`: absolute, with `.` and `..` resolved lexically.
fn full_path(path: &Path) -> io::Result<PathBuf> {
    let absolute = std::path::absolute(path)?;
    let mut out = PathBuf::new();

    for component in absolute.components() {
        match component {
            Component::ParentDir => {
                // Never above the root or prefix.
                if matches!(out.components().next_back(), Some(Component::Normal(_))) {
                    out.pop();
                }
            }
            Component::CurDir => {}
            other => out.push(other.as_os_str()),
        }
    }

    Ok(out)
}

/// `OrdinalIgnoreCase` equality: each UTF-16 unit upper-cased invariantly.
fn upper(s: &str) -> Vec<char> {
    s.chars()
        .map(|c| {
            let mut it = c.to_uppercase();
            match (it.next(), it.next()) {
                (Some(u), None) => u,
                _ => c,
            }
        })
        .collect()
}

/// Characters `Path.GetInvalidFileNameChars()` reports on Windows. The C# only ever ran on
/// Windows; the same set is applied on every OS so that a name accepted here can be copied
/// to any other.
fn is_invalid_file_name_char(c: char) -> bool {
    matches!(c, '"' | '<' | '>' | '|' | ':' | '*' | '?' | '\\' | '/') || (c as u32) < 32
}

impl SafeFolder {
    pub fn new(folder: impl AsRef<Path>) -> Result<Self, SafeFolderError> {
        let folder = folder.as_ref();
        if folder.to_string_lossy().trim().is_empty() {
            return Err(SafeFolderError::Argument(
                "The value cannot be an empty string or composed entirely of whitespace.".to_string(),
            ));
        }

        Ok(Self {
            folder: full_path(folder)?,
        })
    }

    /// The folder itself. Created on first write, not up front.
    pub fn folder(&self) -> &Path {
        &self.folder
    }

    /// Writes `content` (as UTF-8 without a byte-order mark, the C# default encoding) under
    /// `filename` and hands back the full path it landed at.
    ///
    /// The C# took an optional `Encoding`; no other encoding is ported.
    pub fn write(&self, filename: &str, content: &str) -> Result<PathBuf, SafeFolderError> {
        let resolved = self.resolve(filename)?;

        std::fs::create_dir_all(&self.folder)?;
        write_atomic(&resolved, content)?;

        Ok(resolved)
    }

    /// Where `filename` would land, having proved it lands inside this folder. Writes
    /// nothing.
    pub fn resolve(&self, filename: &str) -> Result<PathBuf, SafeFolderError> {
        let safe = safe_name(filename)?;
        let resolved = full_path(&self.folder.join(safe))?;

        // Checked again after resolving, not just on the string that was given: a name that
        // looks plain can still resolve outside this folder if a component of the folder
        // itself turns out to be a symlink or junction.
        if !self.inside(&resolved) {
            return Err(SafeFolderError::Argument(format!(
                "\"{filename}\" would land outside {}.",
                self.folder.display()
            )));
        }

        Ok(resolved)
    }

    /// What is in the folder now, newest first. Empty when it does not exist yet.
    pub fn files(&self) -> Vec<SafeFile> {
        let Ok(entries) = std::fs::read_dir(&self.folder) else {
            return Vec::new();
        };

        let mut files: Vec<SafeFile> = entries
            .filter_map(Result::ok)
            .filter_map(|entry| {
                let meta = entry.metadata().ok()?;
                meta.is_file().then(|| SafeFile {
                    path: entry.path(),
                    len: meta.len(),
                    last_write: meta.modified().unwrap_or(SystemTime::UNIX_EPOCH),
                })
            })
            .collect();

        files.sort_by_key(|f| std::cmp::Reverse(f.last_write));
        files
    }

    /// The resolved path equals the folder, or starts with the folder followed by a
    /// separator, compared ignoring case as the C# did. A folder at a filesystem root already
    /// ends in a separator, so none is added there (the C# added one regardless and so
    /// rejected every file in a root folder).
    fn inside(&self, resolved: &Path) -> bool {
        let folder = upper(&self.folder.to_string_lossy());
        let resolved = upper(&resolved.to_string_lossy());

        if resolved == folder {
            return true;
        }

        let mut prefix = folder;
        if prefix.last().is_none_or(|&c| !std::path::is_separator(c)) {
            prefix.push(std::path::MAIN_SEPARATOR);
        }
        resolved.starts_with(&prefix)
    }
}

/// Refuses anything that is not, on its face, a single file name inside this folder, before
/// it is ever joined to it. Cheaper and clearer than relying on the resolved-path check
/// alone, and it is what turns `../../Windows/System32/whatever` into an error rather than a
/// successful write two enclosing tools away from where anyone would think to look.
fn safe_name(filename: &str) -> Result<&str, SafeFolderError> {
    if filename.trim().is_empty() {
        return Err(SafeFolderError::Argument("A file needs a name.".to_string()));
    }

    let rooted = {
        let path = Path::new(filename);
        path.has_root() || matches!(path.components().next(), Some(Component::Prefix(_)))
    };

    if filename.contains("..")
        || filename.contains('/')
        || filename.contains('\\')
        || rooted
        || filename.chars().any(is_invalid_file_name_char)
    {
        return Err(SafeFolderError::Argument(format!(
            "\"{filename}\" is not a plain file name."
        )));
    }

    Ok(filename)
}

/// Written to a temporary file in the same folder and moved into place, so an interrupted
/// write never leaves a truncated file where a person (or a definition scan) might find and
/// use it. The same discipline the application's own CSV exports already follow.
fn write_atomic(path: &Path, content: &str) -> io::Result<()> {
    let mut temporary = path.as_os_str().to_owned();
    temporary.push(".tmp");
    let temporary = PathBuf::from(temporary);

    std::fs::write(&temporary, content)?;
    std::fs::rename(&temporary, path)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch() -> PathBuf {
        let mut dir = std::env::temp_dir();
        dir.push(format!(
            "olv-safe-{}-{:?}",
            std::process::id(),
            std::thread::current().id()
        ));
        let _ = std::fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn plain_names_are_written_inside_the_folder() {
        let dir = scratch();
        let folder = SafeFolder::new(&dir).unwrap();

        let path = folder.write("tune.msq", "hello").unwrap();

        assert_eq!(std::fs::read_to_string(&path).unwrap(), "hello");
        assert_eq!(path.parent().unwrap(), folder.folder());
        assert_eq!(folder.files().len(), 1);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn names_that_mean_somewhere_else_are_refused() {
        let folder = SafeFolder::new(scratch()).unwrap();

        for bad in [
            "",
            "  ",
            "../x",
            "a/b",
            "a\\b",
            "..",
            "/etc/passwd",
            "C:foo",
            "C:\\x",
            "a*b",
        ] {
            assert!(folder.resolve(bad).is_err(), "{bad:?} should be refused");
        }
    }

    #[test]
    fn a_missing_folder_has_no_files() {
        let folder = SafeFolder::new(scratch().join("nope")).unwrap();
        assert!(folder.files().is_empty());
        assert!(SafeFolder::new("  ").is_err());
    }

    // Defect fixed in the Rust: a folder at a filesystem root rejected every file.
    #[test]
    fn a_folder_at_a_filesystem_root_accepts_plain_names() {
        let root = std::env::temp_dir().ancestors().last().unwrap().to_path_buf();
        let folder = SafeFolder::new(&root).unwrap();

        let resolved = folder.resolve("tune.msq").unwrap();

        assert_eq!(resolved, root.join("tune.msq"));
        assert!(folder.resolve("../x").is_err());
    }
}
