//! Ported from `DefinitionFile.cs`. Owned by package `w2-definitions`.
//!
//! Deciding whether a file somebody handed over is a firmware definition at all.
//!
//! This exists because the catalogue's own bar is far too low to trust for anything but
//! scanning. `ini_catalog::scan` keeps any file with a quoted `signature` line in its first
//! four hundred, which is right for a scan and badly wrong as an acceptance test: a truncated
//! download, an HTML error page saved with the wrong extension, or a definition for a
//! different product will all pass it, match an ECU, and produce a live session with no
//! channels, no tune, and no error anywhere.
//!
//! So a file is measured by what it actually yields: a real definition describes at least one
//! settings page. Checked before the file is kept, never after, since the definitions folder
//! is scanned recursively and a bad file rejected into that folder would be a permanent one.

use crate::ini::{ini_catalog, msq_ini, tune_layout};
use crate::model::tuning_text;
use crate::{Error, Result};
use std::path::Path;

/// What looking at a candidate definition found, before anything is kept.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DefinitionInspection {
    /// What it declares itself to be, or empty when it declares nothing.
    pub signature: String,
    /// Settings pages the firmware describes; zero means this is not a definition.
    pub pages: usize,
    /// Realtime channels it decodes.
    pub channels: usize,
    /// Why it cannot be used, or empty when it can.
    pub problem: String,
}

impl DefinitionInspection {
    fn refused(signature: &str, pages: usize, channels: usize, problem: impl Into<String>) -> Self {
        Self {
            signature: signature.to_string(),
            pages,
            channels,
            problem: problem.into(),
        }
    }

    pub fn is_usable(&self) -> bool {
        self.problem.is_empty()
    }
}

/// The largest definition worth reading. MS3's run past a megabyte and rusEFI's past half of
/// one; ten is far beyond any of them and still far short of anything that would hurt to hold
/// in memory.
pub const LARGEST_BYTES: u64 = 10 * 1024 * 1024;

/// What `text` is, and whether it can be used at all.
pub fn inspect(text: &str) -> DefinitionInspection {
    if text.trim().is_empty() {
        return DefinitionInspection::refused("", 0, 0, "the file is empty");
    }

    let signature = ini_catalog::signature_in(text).unwrap_or_default();

    if signature.is_empty() {
        return DefinitionInspection::refused(
            "",
            0,
            0,
            "it declares no signature, so nothing can say which firmware it belongs to",
        );
    }

    // The readers are forgiving and return nothing on rubbish, so the C#'s catch of
    // LogFormat/Format/Argument exceptions here has nothing to catch in this port.
    let layout = tune_layout::read(text, Some(msq_ini::default_symbols()));
    let realtime = msq_ini::read_output_channels(text, None);

    let pages = layout.pages.len();
    let channels = realtime.fields.len();

    // The same bar BestDefinitionFor has always used. A definition that describes no settings
    // pages is not one, whatever it calls itself.
    if pages == 0 {
        return DefinitionInspection::refused(
            &signature,
            0,
            channels,
            "it describes no settings pages, so it is not a firmware definition \
             \u{2014} a truncated download or a file saved under the wrong name looks like this",
        );
    }

    DefinitionInspection::refused(&signature, pages, channels, "")
}

/// Inspects a file on disk, refusing one too large to be a definition before reading it into
/// memory.
///
/// Fails with [`Error::Argument`] for a blank path, as the C# throws.
pub fn inspect_file(path: &str) -> Result<DefinitionInspection> {
    if path.trim().is_empty() {
        return Err(Error::Argument(
            "The value cannot be an empty string or composed entirely of whitespace.".to_string(),
        ));
    }

    let file = Path::new(path);
    let Some(length) = std::fs::metadata(file).ok().filter(|m| m.is_file()).map(|m| m.len()) else {
        return Ok(DefinitionInspection::refused(
            "",
            0,
            0,
            format!("there is no file at {path}"),
        ));
    };

    if length > LARGEST_BYTES {
        return Ok(DefinitionInspection::refused(
            "",
            0,
            0,
            format!(
                "it is {} MB, which is far larger than any firmware definition",
                length / (1024 * 1024)
            ),
        ));
    }

    match tuning_text::read(file) {
        Ok(text) => Ok(inspect(&text)),
        Err(Error::Io(e)) => Ok(DefinitionInspection::refused(
            "",
            0,
            0,
            format!("it could not be read: {e}"),
        )),
        Err(other) => Err(other),
    }
}

/// Characters `Path.GetInvalidFileNameChars()` reports on Windows, applied on every OS so a
/// name made here can be copied to any other.
fn is_invalid_file_name_char(c: char) -> bool {
    matches!(c, '"' | '<' | '>' | '|' | ':' | '*' | '?' | '\\' | '/') || (c as u32) < 32
}

/// The name to keep a definition under: the signature with its spaces removed, which is the
/// convention TunerStudio's own cache follows and so the name anybody looking for it will
/// recognise.
pub fn name_for(signature: &str) -> String {
    let stem: String = signature
        .chars()
        .filter(|&c| !c.is_whitespace() && !is_invalid_file_name_char(c))
        .collect();

    if stem.is_empty() {
        "definition.ini".to_string()
    } else {
        format!("{stem}.ini")
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    /// The smallest thing that is honestly a firmware definition.
    pub(crate) const REAL: &str = r#"[MegaTune]
   signature = "TEST Format 0001.00"

[Constants]
page = 1
nPages = 1
pageSize = 32
pageIdentifier = "\$tsCanId\x01"
pageReadCommand = "r%2i%2o%2c"
pageChunkWrite  = "w%2i%2o%2c%v"
burnCommand     = "b%2i"
   crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0

[OutputChannels]
ochBlockSize = 4
ochGetCommand = "r\x00\x07%2o%2c"
   rpm = scalar, U16, 0, "rpm", 1, 0
"#;

    /// A fresh path in the temp folder.
    pub(crate) fn temp_path(prefix: &str) -> std::path::PathBuf {
        static COUNTER: AtomicU32 = AtomicU32::new(0);
        std::env::temp_dir().join(format!(
            "{prefix}-{}-{}",
            std::process::id(),
            COUNTER.fetch_add(1, Ordering::Relaxed)
        ))
    }

    // ----- what is accepted -------------------------------------------------------

    #[test]
    fn a_real_definition_is_usable_and_reports_what_it_holds() {
        let found = inspect(REAL);

        assert!(found.is_usable());
        assert_eq!(found.problem, "");
        assert_eq!(found.signature, "TEST Format 0001.00");
        assert_eq!(found.pages, 1);
        assert!(found.channels > 0);
    }

    // ----- what is refused, and why -----------------------------------------------

    #[test]
    fn an_empty_file_is_refused() {
        let found = inspect("   \n  \n");

        assert!(!found.is_usable());
        assert!(found.problem.contains("empty"));
    }

    #[test]
    fn a_file_declaring_no_signature_is_refused() {
        let found = inspect("[Constants]\npage = 1\n");

        assert!(!found.is_usable());
        assert!(found.problem.contains("no signature"));
    }

    #[test]
    fn a_file_with_a_signature_but_no_settings_pages_is_refused() {
        // The case the catalogue's own bar cannot see: this passes Scan, matches an ECU, and
        // connects to a session with nothing in it.
        let found = inspect("[MegaTune]\n   signature = \"TEST Format 0001.00\"\n");

        assert!(!found.is_usable());
        assert_eq!(found.signature, "TEST Format 0001.00");
        assert_eq!(found.pages, 0);
        assert!(found.problem.contains("no settings pages"));
    }

    #[test]
    fn an_html_error_page_saved_under_the_wrong_name_is_refused() {
        // What a fetch of a missing release actually returns. GitHub serves a 9-byte "Not
        // Found" body; a mirror serves a page. Neither is a definition, and neither declares
        // a signature.
        let found = inspect("<!DOCTYPE html><html><head><title>404</title></head><body>Not Found</body></html>");

        assert!(!found.is_usable());
    }

    #[test]
    fn a_truncated_definition_is_refused_even_though_its_signature_survived() {
        // A download cut off after the first section. The signature is intact, so every check
        // short of actually reading the file is satisfied.
        let truncated = &REAL[..REAL.find("[Constants]").unwrap()];

        assert!(!inspect(truncated).is_usable());
    }

    // ----- on disk ----------------------------------------------------------------

    #[test]
    fn a_file_that_is_not_there_is_refused_rather_than_throwing() {
        let absent = temp_path("olv-absent").with_extension("ini");
        let found = inspect_file(absent.to_str().unwrap()).unwrap();

        assert!(!found.is_usable());
        assert!(found.problem.contains("no file"));
    }

    #[test]
    fn a_real_definition_on_disk_is_read() {
        let path = temp_path("olv-def").with_extension("ini");
        std::fs::write(&path, REAL).unwrap();

        let usable = inspect_file(path.to_str().unwrap()).map(|f| f.is_usable());
        let _ = std::fs::remove_file(&path);

        assert!(usable.unwrap());
    }

    // ----- naming -----------------------------------------------------------------

    #[test]
    fn a_definition_is_named_after_its_signature_the_way_tuner_studio_names_them() {
        assert_eq!(name_for("speeduino 202501"), "speeduino202501.ini");
        assert_eq!(name_for("MS3 Format 0592.13 "), "MS3Format0592.13.ini");
    }

    #[test]
    fn a_signature_that_would_name_nothing_still_gets_a_name() {
        assert_eq!(name_for("   "), "definition.ini");
    }

    #[test]
    fn a_signature_cannot_smuggle_a_path_into_the_name() {
        assert!(!name_for("../../evil").contains('/'));
        assert!(!name_for("..\\..\\evil").contains('\\'));
    }
}
