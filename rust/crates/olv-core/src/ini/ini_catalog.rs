//! Ported from `IniCatalog.cs`: finding the INI that matches a connected ECU.
//!
//! This is not a convenience. Firmware versions move channels around inside the
//! realtime block, so decoding with the wrong INI does not fail: it reads every
//! channel from the wrong offset and produces numbers that look entirely
//! reasonable. Matching on the signature the ECU reports, and refusing when
//! nothing matches, is the only thing standing between a live session and
//! confident nonsense.

use std::collections::HashSet;
use std::io::{BufReader, Read};
use std::path::{Path, PathBuf};

/// An INI on disk and the firmware signature it declares.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct IniFile {
    pub path: String,
    pub signature: String,
}

impl IniFile {
    pub fn new(path: impl Into<String>, signature: impl Into<String>) -> Self {
        Self {
            path: path.into(),
            signature: signature.into(),
        }
    }

    /// The file's own name, without its folders.
    pub fn name(&self) -> String {
        file_name(&self.path)
    }
}

/// `Path.GetFileName`.
fn file_name(path: &str) -> String {
    Path::new(path)
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn join(base: &Path, parts: &[&str]) -> String {
    let mut path = PathBuf::from(base);
    for part in parts {
        path.push(part);
    }
    path.to_string_lossy().into_owned()
}

/// Everywhere a definition is likely to already be on this machine.
///
/// The last two kinds of entry earn their place: TunerStudio installs 97
/// firmware definitions alongside the program itself, not only the ones it later
/// downloads into the user's config folder, and the rusEFI console keeps every
/// definition it has fetched in its own cache. Searching only the user-config
/// folder found neither, which meant a machine with the right file sitting on it
/// was told to go and find one.
///
/// Nothing here is read at scan time beyond the signature, and a folder that
/// does not exist is skipped, so listing somewhere optimistically costs an
/// existence check and nothing else.
///
/// The C# asks Windows for the profile, Documents and Program Files folders. On
/// Windows the same environment supplies them; elsewhere the profile is `$HOME`,
/// Documents is `~/Documents`, and TunerStudio's install is looked for under
/// `/opt` and the profile, since there is no Program Files.
pub fn default_search_paths() -> Vec<String> {
    let home = std::env::home_dir().unwrap_or_default();
    let documents = home.join("Documents");

    let mut paths = vec![
        join(&home, &[".efiAnalytics", "TunerStudio", "config", "ecuDef"]),
        join(&documents, &["TunerStudioProjects"]),
        join(&home, &["OneDrive", "Documents", "TunerStudioProjects"]),
    ];

    // What TunerStudio ships with itself.
    paths.extend(tuner_studio_installs(&home));

    // The rusEFI console's own cache, keyed on the build hash.
    paths.push(join(&home, &[".rusEFI", "ini_database"]));

    paths
}

/// TunerStudio's own bundled definitions, wherever it was installed.
fn tuner_studio_installs(home: &Path) -> Vec<String> {
    let tail = ["EFIAnalytics", "TunerStudioMS", "config", "ecuDef"];
    let mut found = Vec::new();

    if cfg!(windows) {
        // Both program-files roots, because a 32-bit install on a 64-bit machine
        // lands in the x86 one and that is where it usually is.
        for variable in ["ProgramFiles(x86)", "ProgramFiles"] {
            if let Some(folder) = std::env::var_os(variable)
                && !folder.is_empty()
            {
                found.push(join(Path::new(&folder), &tail));
            }
        }
    } else {
        found.push(join(Path::new("/opt"), &tail));
        found.push(join(home, &tail));
    }

    found
}

/// Every readable INI under the given directories, with its signature. Files
/// without one are skipped rather than reported: a directory of TunerStudio
/// projects is full of INIs that are not firmware definitions.
///
/// `None` searches [`default_search_paths`].
pub fn scan(directories: Option<&[String]>) -> Vec<IniFile> {
    let defaults;
    let directories = match directories {
        Some(d) => d,
        None => {
            defaults = default_search_paths();
            &defaults
        }
    };

    let mut found = Vec::new();
    let mut seen: HashSet<String> = HashSet::new();

    for directory in directories {
        let root = Path::new(directory);
        if !root.is_dir() {
            continue;
        }

        let mut files = Vec::new();
        if enumerate_ini_files(root, &mut files).is_err() {
            continue;
        }

        for file in files {
            let path = file.to_string_lossy().into_owned();
            if !seen.insert(path.to_uppercase()) {
                continue;
            }
            if let Some(signature) = read_signature(&path)
                && !signature.is_empty()
            {
                found.push(IniFile { path, signature });
            }
        }
    }

    found
}

/// `Directory.EnumerateFiles(directory, "*.ini", SearchOption.AllDirectories)`.
fn enumerate_ini_files(directory: &Path, into: &mut Vec<PathBuf>) -> std::io::Result<()> {
    for entry in std::fs::read_dir(directory)? {
        let entry = entry?;
        let path = entry.path();
        if path.is_dir() {
            // A folder that cannot be read is skipped, as the C# does for the
            // ones it is not allowed into below the first level.
            let _ = enumerate_ini_files(&path, into);
        } else if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("ini")) {
            into.push(path);
        }
    }
    Ok(())
}

/// The first of several candidate strings that matches an INI, with the INI it
/// matched.
///
/// An ECU is asked several questions about itself because no single one works
/// across firmware families. This is what turns the answers into an identity: the
/// signature is whichever reply a definition file recognises, and the rest are
/// build strings.
pub fn match_any<S: AsRef<str>>(
    candidates: impl IntoIterator<Item = S>,
    catalogue: &[IniFile],
) -> Option<(IniFile, String)> {
    for candidate in candidates {
        if let Some(ini) = match_signature(candidate.as_ref(), catalogue) {
            return Some((ini.clone(), candidate.as_ref().to_string()));
        }
    }

    None
}

/// The tune belonging to the TunerStudio project an INI came out of.
///
/// Worth finding, because some of what a gauge needs is kept nowhere else. A
/// MegaSquirt tachometer runs to `{rpmhigh}` and warns at `{rpmwarn}`, and those
/// are TunerStudio's variables rather than the firmware's: not in the ECU, not
/// derivable, and absent from a tune exported on its own. They live in the
/// project, two directories up from its copy of the firmware definition.
///
/// None when the INI is a plain firmware definition rather than part of a
/// project, which is the usual case for the ones under ecuDef.
pub fn project_tune_for(ini_path: &str) -> Option<String> {
    let configuration = Path::new(ini_path).parent()?;

    let name = configuration.file_name()?.to_string_lossy();
    if !name.eq_ignore_ascii_case("projectCfg") {
        return None;
    }

    let project = configuration.parent()?;
    let tune = project.join("CurrentTune.msq");

    tune.is_file().then(|| tune.to_string_lossy().into_owned())
}

/// The INI matching a signature, or none when none does.
pub fn match_signature<'a>(signature: &str, catalogue: &'a [IniFile]) -> Option<&'a IniFile> {
    let wanted = normalise(signature);
    if wanted.is_empty() {
        return None;
    }

    // Exact first. Only then the looser match, which exists because an ECU pads
    // its signature and an INI sometimes carries a trailing comment.
    catalogue
        .iter()
        .find(|i| normalise(&i.signature) == wanted)
        .or_else(|| catalogue.iter().find(|i| normalise(&i.signature).starts_with(&wanted)))
        .or_else(|| catalogue.iter().find(|i| wanted.starts_with(&normalise(&i.signature))))
}

/// The signature an INI declares. Only the head of the file is read: the
/// declaration is in the first section, and these run to a megabyte.
pub fn read_signature(path: &str) -> Option<String> {
    let file = std::fs::File::open(path).ok()?;
    let mut lines = LatinLines::new(BufReader::new(file));
    let mut head = Vec::new();
    for _ in 0..400 {
        match lines.next_line() {
            Ok(Some(line)) => head.push(line),
            Ok(None) => break,
            Err(_) => return None,
        }
    }

    signature_in_lines(head.iter().map(String::as_str))
}

/// Lines of a file read as Latin-1, ended by `\n`, `\r\n` or `\r`, as
/// `File.ReadLines` ends them.
struct LatinLines<R: Read> {
    bytes: std::io::Bytes<BufReader<R>>,
    pending_cr: bool,
}

impl<R: Read> LatinLines<R> {
    fn new(reader: BufReader<R>) -> Self {
        Self {
            bytes: reader.bytes(),
            pending_cr: false,
        }
    }

    fn next_line(&mut self) -> std::io::Result<Option<String>> {
        let mut line = String::new();
        let mut any = false;

        loop {
            let Some(byte) = self.bytes.next() else {
                return Ok(any.then_some(line));
            };
            let byte = byte?;
            any = true;

            if self.pending_cr {
                // The \n of a \r\n belongs to the line already ended.
                self.pending_cr = false;
                if byte == b'\n' {
                    any = false;
                    continue;
                }
            }

            match byte {
                b'\n' => return Ok(Some(line)),
                b'\r' => {
                    self.pending_cr = true;
                    return Ok(Some(line));
                }
                b => line.push(b as char),
            }
        }
    }
}

/// The same declaration, read out of a definition already in hand rather than
/// off disk: for checking what a file says before deciding whether to keep it.
pub fn signature_in(text: &str) -> Option<String> {
    signature_in_lines(text.split('\n'))
}

fn signature_in_lines<'a>(lines: impl Iterator<Item = &'a str>) -> Option<String> {
    for line in lines.take(400) {
        let trimmed = line.trim_start();
        if !super::msq_ini::starts_with_ignore_case(trimmed, "signature") {
            continue;
        }

        let Some(equals) = trimmed.find('=') else {
            continue;
        };
        let Some(open) = trimmed[equals..].find('"').map(|i| i + equals) else {
            continue;
        };
        let Some(close) = trimmed[open + 1..].find('"').map(|i| i + open + 1) else {
            continue;
        };

        return Some(trimmed[open + 1..close].trim().to_string());
    }

    None
}

/// Whether a definition's declared signature is the same one an ECU reported:
/// exactly, with only the whitespace and case forgiveness [`match_signature`]
/// already allows.
///
/// Deliberately without [`match_signature`]'s two looser passes. Those exist so a
/// live session still starts when an ECU pads its reply or an INI carries a
/// trailing comment, and for that they are right. They are not right for deciding
/// whether to keep a file somebody just handed over: a definition declaring
/// nothing but `"speeduino"` is a prefix of every Speeduino signature there has
/// ever been, and accepting it would install a permanent wrong answer for every
/// future connection.
pub fn same_signature(declared: &str, reported: &str) -> bool {
    !normalise(declared).is_empty() && normalise(declared) == normalise(reported)
}

/// Every definition in the catalogue declaring the signature an ECU reported,
/// rather than only the first.
///
/// More than one is not a hypothetical. Speeduino's eight `202501.x` releases all
/// declare `speeduino 202501` and ship three different definitions between them,
/// one of which decodes `fanHyster` at a different scale. [`match_signature`]
/// takes whichever the directory scan reached first, which is not the same choice
/// on two machines. Callers that can say something useful about the ambiguity
/// should ask for all of them.
pub fn matching_all(signature: &str, catalogue: &[IniFile]) -> Vec<IniFile> {
    let wanted = normalise(signature);
    if wanted.is_empty() {
        return Vec::new();
    }

    catalogue
        .iter()
        .filter(|i| normalise(&i.signature) == wanted)
        .cloned()
        .collect()
}

/// Without white space, lower-cased the way `char.ToLowerInvariant` does it (one
/// character to one).
fn normalise(signature: &str) -> String {
    signature
        .chars()
        .filter(|c| !c.is_whitespace())
        .map(|c| {
            let mut lower = c.to_lowercase();
            match (lower.next(), lower.next()) {
                (Some(l), None) => l,
                _ => c,
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn contains_ic(text: &str, part: &str) -> bool {
        text.to_uppercase().contains(&part.to_uppercase())
    }

    fn ends_with_ic(text: &str, part: &str) -> bool {
        text.to_uppercase().ends_with(&part.to_uppercase())
    }

    // ----- where it looks -----------------------------------------------------

    /// TunerStudio installs ~97 firmware definitions beside the program, not only
    /// the ones it later downloads into the user's config folder. Searching just
    /// the config folder told people with the right file already on their machine
    /// to go and find one.
    #[test]
    fn tuner_studios_own_installed_definitions_are_searched() {
        let paths = default_search_paths();

        assert!(
            paths
                .iter()
                .any(|p| contains_ic(p, "TunerStudioMS") && ends_with_ic(p, "ecuDef"))
        );
    }

    #[test]
    fn the_rus_efi_consoles_cache_is_searched() {
        assert!(
            default_search_paths()
                .iter()
                .any(|p| contains_ic(p, ".rusEFI") && ends_with_ic(p, "ini_database"))
        );
    }

    /// Ours and TunerStudio's downloaded copies come before its bundled set: a
    /// definition somebody fetched for their exact firmware beats one that shipped
    /// with the installer years ago.
    #[test]
    fn the_user_config_folder_is_still_searched_first() {
        let paths = default_search_paths();

        let config = paths.iter().position(|p| contains_ic(p, ".efiAnalytics"));
        let installed = paths.iter().position(|p| contains_ic(p, "TunerStudioMS"));

        assert!(
            matches!((config, installed), (Some(c), Some(i)) if i > c),
            "the downloaded copies must be searched before the bundled ones"
        );
    }

    // ----- reading a signature out of text ------------------------------------

    #[test]
    fn a_signature_is_read_out_of_text_as_well_as_off_disk() {
        assert_eq!(
            signature_in("[MegaTune]\n   signature = \"speeduino 202501\"\n").as_deref(),
            Some("speeduino 202501")
        );
    }

    #[test]
    fn text_declaring_no_signature_reads_as_none() {
        assert_eq!(signature_in("[Constants]\npage = 1\n"), None);
    }

    // ----- exact, for deciding what to keep -----------------------------------

    /// What `match_signature` already forgives, and all it forgives.
    #[test]
    fn the_same_signature_is_the_same_through_padding_and_case() {
        assert!(same_signature("speeduino 202501", "speeduino  202501"));
        assert!(same_signature("MS3 Format 0592.13 ", "MS3 Format 0592.13"));
        assert!(same_signature("Speeduino 202501", "speeduino 202501"));
    }

    /// The reason this exists. The looser passes let a live session start when an
    /// ECU pads its reply; they must never let a definition declaring nothing but
    /// "speeduino" be installed as the answer for every Speeduino that will ever
    /// be plugged in.
    #[test]
    fn a_prefix_is_not_the_same_signature() {
        assert!(!same_signature("speeduino", "speeduino 202501"));
        assert!(!same_signature("speeduino 202501", "speeduino 202402"));
        assert!(!same_signature("", "speeduino 202501"));
    }

    // ----- more than one candidate --------------------------------------------

    /// Speeduino's eight 202501.x releases all declare "speeduino 202501" and ship
    /// three different definitions between them, one of which decodes fanHyster at
    /// a different scale. `match_signature` takes whichever the scan reached first,
    /// which is not the same choice on two machines.
    #[test]
    fn every_definition_declaring_the_same_signature_is_reported_not_just_the_first() {
        let catalogue = [
            IniFile::new("a.ini", "speeduino 202501"),
            IniFile::new("b.ini", "speeduino 202501"),
            IniFile::new("c.ini", "speeduino 202402"),
        ];

        let all = matching_all("speeduino 202501", &catalogue);

        assert_eq!(all.len(), 2);
        assert!(!all.iter().any(|i| i.path == "c.ini"));
    }

    #[test]
    fn matching_all_ignores_the_loosening_that_match_allows() {
        let catalogue = [IniFile::new("a.ini", "speeduino")];

        assert!(matching_all("speeduino 202501", &catalogue).is_empty());

        // Match itself still forgives it, deliberately: that behaviour is not
        // being changed here, only kept away from decisions about what to keep.
        assert!(match_signature("speeduino 202501", &catalogue).is_some());
    }

    #[test]
    fn match_any_returns_the_candidate_that_matched() {
        let catalogue = [IniFile::new("dir/a.ini", "speeduino 202501")];

        let (ini, candidate) = match_any(["build 7", "Speeduino 202501"], &catalogue).expect("a match");

        assert_eq!(ini.name(), "a.ini");
        assert_eq!(candidate, "Speeduino 202501");
        assert!(match_any(["nothing"], &catalogue).is_none());
    }

    #[test]
    fn project_tune_is_none_for_a_plain_definition() {
        assert_eq!(project_tune_for("some/ecuDef/speeduino.ini"), None);
    }
}
