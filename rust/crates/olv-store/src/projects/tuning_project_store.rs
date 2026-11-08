//! Ported from `TuningProjectStore.cs`. Owned by package `w5-projects`.
//!
//! Where a vehicle's project lives, and how it reads.
//!
//! One folder per vehicle under the root, holding `project.json`. JSON because it has to be
//! written back reliably by a program; prose because that is how it is actually read.
//! [`TuningProjectStore::brief`] renders the whole thing as Markdown, which is what an assistant
//! is handed: a model that starts each session knowing nothing repeats work and re-diagnoses
//! problems already diagnosed.
//!
//! **File format.** The file is read and written by the C# app too, so the property names, their
//! casing, the defaults for missing properties and the time format are the C#'s. The C# reads
//! property names case-insensitively, so each property also accepts its PascalCase spelling as an
//! alias. The C# also writes the computed properties `open`, `latest`, `isOpen`, `warnings` and
//! `summary` (System.Text.Json serialises every getter); they are written here too and ignored on
//! read, as the C# ignores them.

use std::fmt::Write as _;
use std::path::{Path, PathBuf};

use olv_core::base::json_settings_file;
use olv_core::fmt as nf;
use olv_core::tune::tune_version::{TuneVersion, VersionTime};
use olv_core::{Error, Result};
use serde::{Deserialize, Serialize};
use time::OffsetDateTime;
use time::UtcOffset;
use time::format_description::well_known::Rfc3339;

use super::tuning_project::{FixState, ProjectSession, SessionFinding, TuningFix, TuningProject, cmp_ignore_case, day};

const PROJECT_FILE: &str = "project.json";

/// How many of the most recent sittings [`TuningProjectStore::brief`] renders by default.
pub const DEFAULT_BRIEF_SESSIONS: usize = 5;

/// Where projects are kept: one folder per vehicle.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TuningProjectStore {
    root: PathBuf,
}

impl TuningProjectStore {
    pub fn new(root: impl Into<PathBuf>) -> Self {
        TuningProjectStore { root: root.into() }
    }

    /// Where projects are kept.
    pub fn root(&self) -> &Path {
        &self.root
    }

    /// Every vehicle with a project, by name, ordered without regard to case.
    pub fn vehicles(&self) -> Vec<String> {
        let Ok(entries) = std::fs::read_dir(&self.root) else {
            return Vec::new();
        };

        let mut names: Vec<String> = entries
            .filter_map(|entry| entry.ok())
            .filter(|entry| entry.path().join(PROJECT_FILE).is_file())
            .filter(|entry| entry.path().is_dir())
            .filter_map(|entry| entry.file_name().into_string().ok())
            .collect();

        names.sort_by(|a, b| cmp_ignore_case(a, b));
        names
    }

    /// The file for a vehicle, whether or not it exists yet.
    pub fn path_for(&self, vehicle: &str) -> PathBuf {
        self.root.join(safe_folder_name(vehicle)).join(PROJECT_FILE)
    }

    /// Reads one, or `None` where there is none or the file will not read.
    pub fn read(&self, vehicle: &str) -> Option<TuningProject> {
        let file: ProjectFile = json_settings_file::read(&self.path_for(vehicle))?;
        file.into_project()
    }

    /// Writes one, creating its folder.
    pub fn write(&self, project: &TuningProject) -> Result<()> {
        json_settings_file::write(&self.path_for(&project.vehicle), &ProjectFile::from(project)).map_err(Error::Io)
    }

    /// The project as prose, for whoever has to read it, usually a model.
    ///
    /// Ordered by what is useful to know first rather than by what happened first: what the car
    /// is, what is still wrong, what was tried, and only then the history. Sittings are trimmed to
    /// the most recent `sessions`; the whole history is in the file and can be asked for.
    pub fn brief(project: &TuningProject, sessions: usize) -> String {
        let mut text = String::new();

        let _ = writeln!(text, "# {}", project.vehicle);
        text.push('\n');

        if !project.engine.is_empty() {
            let _ = writeln!(text, "**Engine** {}", project.engine);
        }
        if !project.signature.is_empty() {
            let _ = writeln!(text, "**Firmware** {}", project.signature);
        }

        let _ = writeln!(text, "**Started** {}", day(project.started));
        let open_count = project.open().count();
        let _ = writeln!(
            text,
            "**Sittings** {}   **Open fixes** {}",
            project.sessions.len(),
            open_count
        );

        if !project.notes.is_empty() {
            text.push('\n');
            let _ = writeln!(text, "{}", project.notes);
        }

        // What is still wrong, first, because it is what the next change should be about.
        let mut open: Vec<&TuningFix> = project.open().collect();
        open.sort_by_key(|f| f.raised.unix_seconds);

        text.push('\n');
        text.push_str("## Still open\n\n");

        if open.is_empty() {
            text.push_str("Nothing outstanding.\n");
        } else {
            for fix in open {
                describe(&mut text, fix);
            }
        }

        let mut settled: Vec<&TuningFix> = project.fixes.iter().filter(|f| !f.is_open()).collect();
        // Descending, with unsettled fixes last, as `OrderByDescending` over a nullable orders them.
        settled.sort_by_key(|f| std::cmp::Reverse(f.settled.map(|t| t.unix_seconds)));

        if !settled.is_empty() {
            text.push('\n');
            text.push_str("## Settled\n\n");

            for fix in settled {
                let _ = write!(text, "- **{}** \u{2014} {} ({})", fix.id, fix.title, fix.state.name());

                if !fix.change.is_empty() {
                    let _ = write!(text, ". {}", fix.change);
                }

                text.push('\n');
            }
        }

        if !project.versions.is_empty() {
            text.push('\n');
            text.push_str("## Tunes\n\n");

            // Newest first: what the controller is most likely running is what somebody needs
            // before anything else here.
            for version in project.versions.iter().rev().take(8) {
                let _ = write!(text, "- **{}** {}", version.id, day(version.at));

                if version.burned {
                    text.push_str(", burned");
                }
                if !version.note.is_empty() {
                    let _ = write!(text, " \u{2014} {}", version.note);
                }
                if !version.addresses.is_empty() {
                    let _ = write!(text, " (for {})", version.addresses.join(", "));
                }

                text.push('\n');
            }

            if project.versions.len() > 8 {
                let _ = writeln!(text, "*{} earlier tunes are in the file.*", project.versions.len() - 8);
            }
        }

        text.push('\n');
        text.push_str("## Recent sittings\n\n");

        if project.sessions.is_empty() {
            text.push_str("None recorded.\n");
            return text;
        }

        for sitting in project.sessions.iter().rev().take(sessions) {
            let _ = write!(text, "### {}", sitting.at.short());

            if !sitting.log.is_empty() {
                let _ = write!(text, " \u{2014} {}", sitting.log);
            }
            if !sitting.version.is_empty() {
                let _ = write!(text, " \u{b7} on {}", sitting.version);
            }

            text.push('\n');
            let _ = write!(
                text,
                "{} samples over {} s",
                nf::int(sitting.samples, "N0"),
                nf::f64(sitting.seconds, "N0")
            );

            if !sitting.signature.is_empty() {
                let _ = write!(text, ", {}", sitting.signature);
            }

            text.push_str(".\n");

            if !sitting.note.is_empty() {
                text.push('\n');
                let _ = writeln!(text, "{}", sitting.note);
            }

            // Only what asked for attention. A sitting where everything was fine is worth one
            // line saying so, not thirteen.
            let worth: Vec<&SessionFinding> = sitting
                .findings
                .iter()
                .filter(|f| f.level == "Warning" || f.level == "Watch")
                .collect();

            text.push('\n');

            if worth.is_empty() {
                text.push_str("Nothing flagged.\n");
            } else {
                for finding in worth {
                    let _ = writeln!(
                        text,
                        "- **{}** {} \u{2014} {}",
                        finding.level, finding.topic, finding.title
                    );
                }
            }

            text.push('\n');
        }

        if project.sessions.len() > sessions {
            let _ = writeln!(
                text,
                "*{} earlier sittings are in the file.*",
                project.sessions.len() - sessions
            );
        }

        text
    }
}

fn describe(text: &mut String, fix: &TuningFix) {
    let _ = writeln!(text, "### {} \u{2014} {}", fix.id, fix.title);
    text.push('\n');
    let _ = writeln!(text, "*{}, raised {}*", fix.state.name(), day(fix.raised));

    if !fix.detail.is_empty() {
        text.push('\n');
        let _ = writeln!(text, "{}", fix.detail);
    }

    if !fix.change.is_empty() {
        text.push('\n');
        let _ = writeln!(text, "**Changed** {}", fix.change);
    }

    if !fix.evidence.is_empty() {
        text.push('\n');
        text.push_str("**Since**\n\n");

        for note in &fix.evidence {
            let _ = writeln!(text, "- {note}");
        }
    }

    text.push('\n');
}

/// A folder name from a vehicle name, without the characters Windows refuses and without a path
/// in it. The set is the Windows one on every platform, so a project is named the same wherever
/// it is opened.
fn safe_folder_name(vehicle: &str) -> String {
    if vehicle.trim().is_empty() {
        return "vehicle".to_string();
    }

    let clean: String = vehicle
        .chars()
        .map(|c| if is_refused_in_file_name(c) { '-' } else { c })
        .collect();

    let name = clean.trim().trim_matches('.');

    if name.is_empty() {
        "vehicle".to_string()
    } else {
        name.to_string()
    }
}

fn is_refused_in_file_name(c: char) -> bool {
    (c as u32) < 0x20 || matches!(c, '"' | '<' | '>' | '|' | ':' | '*' | '?' | '\\' | '/')
}

/// Parses an ISO 8601 date-time with its offset, as `System.Text.Json` writes `DateTimeOffset`.
fn time_from_text(text: &str) -> Option<VersionTime> {
    let parsed = OffsetDateTime::parse(text, &Rfc3339).ok()?;

    Some(VersionTime {
        unix_seconds: parsed.unix_timestamp(),
        offset_minutes: parsed.offset().whole_seconds() / 60,
    })
}

/// Writes a moment as `System.Text.Json` does: ISO 8601 with a `+hh:mm` offset, never `Z`. Whole
/// seconds only, since that is all a [`VersionTime`] holds.
fn time_to_text(at: VersionTime) -> String {
    let offset = UtcOffset::from_whole_seconds(at.offset_minutes * 60).unwrap_or(UtcOffset::UTC);

    OffsetDateTime::from_unix_timestamp(at.unix_seconds)
        .ok()
        .map(|t| t.to_offset(offset))
        .and_then(|t| t.format(&Rfc3339).ok())
        .map(|s| match s.strip_suffix('Z') {
            Some(prefix) => format!("{prefix}+00:00"),
            None => s,
        })
        .unwrap_or_default()
}

// ----- the file -------------------------------------------------------------------------------

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ProjectFile {
    #[serde(alias = "Vehicle")]
    vehicle: String,
    #[serde(default, alias = "Engine")]
    engine: String,
    #[serde(default, alias = "Signature")]
    signature: String,
    #[serde(default, alias = "Notes")]
    notes: String,
    #[serde(default, alias = "Sessions")]
    sessions: Vec<SessionFile>,
    #[serde(default, alias = "Fixes")]
    fixes: Vec<FixFile>,
    #[serde(default, alias = "Versions")]
    versions: Vec<VersionFile>,
    #[serde(default, alias = "Started")]
    started: Option<String>,
    #[serde(default, skip_deserializing)]
    open: Vec<FixFile>,
    #[serde(default, skip_deserializing)]
    latest: Option<VersionFile>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SessionFile {
    #[serde(default, alias = "At")]
    at: Option<String>,
    #[serde(default, alias = "Log")]
    log: String,
    #[serde(default, alias = "Signature")]
    signature: String,
    #[serde(default, alias = "Samples")]
    samples: i32,
    #[serde(default, alias = "Seconds")]
    seconds: f64,
    #[serde(default, alias = "Findings")]
    findings: Vec<FindingFile>,
    #[serde(default, alias = "Note")]
    note: String,
    #[serde(default, alias = "Version")]
    version: String,
    #[serde(default, skip_deserializing)]
    warnings: usize,
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
struct FindingFile {
    #[serde(default, alias = "Level")]
    level: String,
    #[serde(default, alias = "Topic")]
    topic: String,
    #[serde(default, alias = "Title")]
    title: String,
    #[serde(default, alias = "Detail")]
    detail: String,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct FixFile {
    #[serde(alias = "Id")]
    id: String,
    #[serde(alias = "Title")]
    title: String,
    #[serde(default, alias = "Detail")]
    detail: String,
    #[serde(default, alias = "State")]
    state: FixState,
    #[serde(default, alias = "Change")]
    change: String,
    #[serde(default, alias = "Evidence")]
    evidence: Vec<String>,
    #[serde(default, alias = "Raised")]
    raised: Option<String>,
    #[serde(default, alias = "Settled")]
    settled: Option<String>,
    #[serde(default, skip_deserializing)]
    is_open: bool,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct VersionFile {
    #[serde(alias = "Id")]
    id: String,
    #[serde(default, alias = "At")]
    at: Option<String>,
    #[serde(default, alias = "Signature")]
    signature: String,
    #[serde(alias = "Fingerprint")]
    fingerprint: String,
    #[serde(default, alias = "Parent")]
    parent: String,
    #[serde(default, alias = "Note")]
    note: String,
    #[serde(default, alias = "Addresses")]
    addresses: Vec<String>,
    #[serde(default, alias = "Burned")]
    burned: bool,
    #[serde(alias = "File")]
    file: String,
    #[serde(default, skip_deserializing)]
    summary: String,
}

impl ProjectFile {
    /// `None` where the C# would have thrown while deserialising (a bad date, a bad state), which
    /// the C# reports as an absent project.
    fn into_project(self) -> Option<TuningProject> {
        Some(TuningProject {
            vehicle: self.vehicle,
            engine: self.engine,
            signature: self.signature,
            notes: self.notes,
            sessions: self
                .sessions
                .into_iter()
                .map(SessionFile::into_session)
                .collect::<Option<Vec<_>>>()?,
            fixes: self
                .fixes
                .into_iter()
                .map(FixFile::into_fix)
                .collect::<Option<Vec<_>>>()?,
            versions: self
                .versions
                .into_iter()
                .map(VersionFile::into_version)
                .collect::<Option<Vec<_>>>()?,
            started: match self.started {
                Some(text) => time_from_text(&text)?,
                None => VersionTime::now(),
            },
        })
    }
}

impl SessionFile {
    fn into_session(self) -> Option<ProjectSession> {
        Some(ProjectSession {
            at: match self.at {
                Some(text) => time_from_text(&text)?,
                None => VersionTime::now(),
            },
            log: self.log,
            signature: self.signature,
            samples: self.samples,
            seconds: self.seconds,
            findings: self
                .findings
                .into_iter()
                .map(|f| SessionFinding {
                    level: f.level,
                    topic: f.topic,
                    title: f.title,
                    detail: f.detail,
                })
                .collect(),
            note: self.note,
            version: self.version,
        })
    }
}

impl FixFile {
    fn into_fix(self) -> Option<TuningFix> {
        Some(TuningFix {
            id: self.id,
            title: self.title,
            detail: self.detail,
            state: self.state,
            change: self.change,
            evidence: self.evidence,
            raised: match self.raised {
                Some(text) => time_from_text(&text)?,
                None => VersionTime::now(),
            },
            settled: match self.settled {
                Some(text) => Some(time_from_text(&text)?),
                None => None,
            },
        })
    }
}

impl VersionFile {
    fn into_version(self) -> Option<TuneVersion> {
        let mut version = TuneVersion::new(self.id, self.fingerprint, self.file);
        version.at = match self.at {
            Some(text) => time_from_text(&text)?,
            None => VersionTime::now(),
        };
        version.signature = self.signature;
        version.parent = self.parent;
        version.note = self.note;
        version.addresses = self.addresses;
        version.burned = self.burned;
        Some(version)
    }
}

impl From<&TuningProject> for ProjectFile {
    fn from(project: &TuningProject) -> Self {
        ProjectFile {
            vehicle: project.vehicle.clone(),
            engine: project.engine.clone(),
            signature: project.signature.clone(),
            notes: project.notes.clone(),
            sessions: project.sessions.iter().map(SessionFile::from).collect(),
            fixes: project.fixes.iter().map(FixFile::from).collect(),
            versions: project.versions.iter().map(VersionFile::from).collect(),
            started: Some(time_to_text(project.started)),
            open: project.open().map(FixFile::from).collect(),
            latest: project.latest().map(VersionFile::from),
        }
    }
}

impl From<&ProjectSession> for SessionFile {
    fn from(session: &ProjectSession) -> Self {
        SessionFile {
            at: Some(time_to_text(session.at)),
            log: session.log.clone(),
            signature: session.signature.clone(),
            samples: session.samples,
            seconds: session.seconds,
            findings: session
                .findings
                .iter()
                .map(|f| FindingFile {
                    level: f.level.clone(),
                    topic: f.topic.clone(),
                    title: f.title.clone(),
                    detail: f.detail.clone(),
                })
                .collect(),
            note: session.note.clone(),
            version: session.version.clone(),
            warnings: session.warnings(),
        }
    }
}

impl From<&TuningFix> for FixFile {
    fn from(fix: &TuningFix) -> Self {
        FixFile {
            id: fix.id.clone(),
            title: fix.title.clone(),
            detail: fix.detail.clone(),
            state: fix.state,
            change: fix.change.clone(),
            evidence: fix.evidence.clone(),
            raised: Some(time_to_text(fix.raised)),
            settled: fix.settled.map(time_to_text),
            is_open: fix.is_open(),
        }
    }
}

impl From<&TuneVersion> for VersionFile {
    fn from(version: &TuneVersion) -> Self {
        VersionFile {
            id: version.id.clone(),
            at: Some(time_to_text(version.at)),
            signature: version.signature.clone(),
            fingerprint: version.fingerprint.clone(),
            parent: version.parent.clone(),
            note: version.note.clone(),
            addresses: version.addresses.clone(),
            burned: version.burned,
            file: version.file.clone(),
            summary: version.summary(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::projects::tuning_project_recorder as recorder;
    use olv_core::model::{LogChannel, LogDocument};
    use olv_core::tune::tune_version::TuneVersion;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicU64, Ordering};

    /// A directory under the system temp folder, removed when dropped.
    struct Scratch(PathBuf);

    impl Scratch {
        fn new() -> Self {
            static COUNTER: AtomicU64 = AtomicU64::new(0);
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0);
            let dir = std::env::temp_dir().join(format!(
                "olv-projects-{}-{nanos}-{}",
                std::process::id(),
                COUNTER.fetch_add(1, Ordering::Relaxed)
            ));
            Scratch(dir)
        }

        fn store(&self) -> TuningProjectStore {
            TuningProjectStore::new(self.0.clone())
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn project(vehicle: &str) -> TuningProject {
        TuningProject {
            engine: "M30B35".to_string(),
            signature: "MS3 Format 0592.12P".to_string(),
            ..TuningProject::new(vehicle)
        }
    }

    /// A log shaped like a real pull: idle, then load, then back.
    ///
    /// Everything moves, deliberately. A log whose every channel holds one value is flagged,
    /// correctly, as a rig full of dead sensors, so a fixture built that way tests the wrong thing.
    fn log(afr: f64) -> LogDocument {
        const SAMPLES: usize = 400;

        // A third idling, a third climbing into load, a third back down.
        let load = |i: usize| -> f64 {
            if i < SAMPLES / 3 {
                0.0
            } else if i < 2 * SAMPLES / 3 {
                1.0
            } else {
                0.2
            }
        };
        let series = |f: &dyn Fn(usize) -> f64| -> Vec<f64> { (0..SAMPLES).map(f).collect() };
        let channel = |name: &str, units: &str, digits: i32, values: Vec<f64>| {
            Arc::new(LogChannel::new(name, units, digits, &values, false))
        };

        let time = channel("Time", "s", 2, series(&|i| i as f64 * 0.1));

        let channels = vec![
            channel("RPM", "rpm", 0, series(&|i| 850.0 + (load(i) * 4200.0))),
            channel("MAP", "kPa", 1, series(&|i| 32.0 + (load(i) * 148.0))),
            channel("TPS", "%", 1, series(&|i| load(i) * 92.0)),
            // Only under load does the mixture take the value being tested; at idle it sits on
            // target, which is what a real log does.
            channel("AFR", "AFR", 2, series(&|i| if load(i) > 0.5 { afr } else { 14.6 })),
            channel(
                "AFR 1 Target",
                "AFR",
                2,
                series(&|i| if load(i) > 0.5 { 13.0 } else { 14.6 }),
            ),
            channel(
                "CLT",
                "\u{b0}F",
                1,
                series(&|i| 188.0 + (i as f64 / SAMPLES as f64) * 6.0),
            ),
            channel("MAT", "\u{b0}F", 1, series(&|i| 95.0 + (load(i) * 14.0))),
            channel("Batt V", "v", 2, series(&|i| 13.8 + (load(i) * 0.4))),
        ];

        LogDocument::new("session.mlg", "test", channels, time)
    }

    // ----- keeping it -----------------------------------------------------------------------

    #[test]
    fn a_project_survives_being_written_and_read_back() {
        let scratch = Scratch::new();
        let store = scratch.store();

        let project = project("The E28").with_fix(TuningFix::new("lean-under-load", "Lean above 150 kPa"));

        store.write(&project).unwrap();

        let back = store.read("The E28").expect("project reads back");
        assert_eq!(back.engine, "M30B35");
        assert_eq!(
            back.fix("lean-under-load").map(|f| f.title.as_str()),
            Some("Lean above 150 kPa")
        );
    }

    #[test]
    fn a_vehicle_with_no_project_reads_as_nothing_rather_than_throwing() {
        let scratch = Scratch::new();
        assert!(scratch.store().read("A car nobody has").is_none());
    }

    #[test]
    fn a_name_with_a_slash_in_it_does_not_become_a_path() {
        // "5VZ-FE / 4Runner" is a perfectly reasonable thing to call a project and would otherwise
        // put the file two folders away.
        let scratch = Scratch::new();
        let store = scratch.store();

        store.write(&project("4Runner / spare")).unwrap();

        assert!(store.vehicles().contains(&"4Runner - spare".to_string()));
    }

    #[test]
    fn every_vehicle_with_a_project_is_listed() {
        let scratch = Scratch::new();
        let store = scratch.store();

        store.write(&project("The E28")).unwrap();
        store.write(&project("The 4Runner")).unwrap();

        assert_eq!(store.vehicles(), vec!["The 4Runner", "The E28"]);
    }

    // ----- fixes ----------------------------------------------------------------------------

    #[test]
    fn changing_a_fix_replaces_it_rather_than_leaving_two() {
        let mut project = project("The E28").with_fix(TuningFix::new("lean", "Lean above 150 kPa"));

        let fix = project.fix("lean").unwrap().clone();
        project = project.with_fix(TuningFix {
            state: FixState::Applied,
            change: "VE +4% above 150 kPa".to_string(),
            ..fix
        });

        assert_eq!(project.fixes.len(), 1);
        assert_eq!(project.fix("lean").unwrap().state, FixState::Applied);
        assert_eq!(project.fix("lean").unwrap().change, "VE +4% above 150 kPa");
    }

    #[test]
    fn an_id_is_made_of_words_so_it_can_be_quoted_from_memory() {
        // These are referred to across sessions and read aloud in notes. "lean-under-load"
        // survives that and "fix-7" does not.
        let project = project("The E28");

        assert_eq!(project.new_id("Lean under load"), "lean-under-load");
    }

    #[test]
    fn and_is_not_handed_out_twice() {
        let project = project("The E28").with_fix(TuningFix::new("lean-under-load", "First"));

        assert_eq!(project.new_id("Lean under load"), "lean-under-load-2");
    }

    #[test]
    fn open_means_seen_or_tried_but_not_yet_settled() {
        let mut fixes = Vec::new();
        for (id, state) in [
            ("a", FixState::Open),
            ("b", FixState::Applied),
            ("c", FixState::Verified),
            ("d", FixState::Abandoned),
        ] {
            fixes.push(TuningFix {
                state,
                ..TuningFix::new(id, id.to_uppercase())
            });
        }

        let mut project = project("The E28");
        for fix in fixes {
            project = project.with_fix(fix);
        }

        let mut open: Vec<&str> = project.open().map(|f| f.id.as_str()).collect();
        open.sort_unstable();
        assert_eq!(open, ["a", "b"]);
    }

    // ----- recording a sitting --------------------------------------------------------------

    #[test]
    fn a_sitting_keeps_every_finding_including_the_good_ones() {
        // A run where nothing was wrong is evidence too: it is what a fix gets verified against,
        // and a project recording only bad days cannot show anything getting better.
        let sitting = recorder::sitting(&log(13.0), "MS3", "", "");

        assert!(!sitting.findings.is_empty());
        assert_eq!(sitting.log, "session.mlg");
        assert_eq!(sitting.samples, 400);
        assert!(sitting.findings.iter().any(|f| f.level == "Good"));
    }

    #[test]
    fn a_warning_raises_a_fix_the_first_time_it_is_seen() {
        let project = recorder::record(&project("The E28"), &recorder::sitting(&log(16.5), "MS3", "", ""));

        assert!(project.open().next().is_some());
        assert!(project.open().all(|f| !f.evidence.is_empty()));
    }

    #[test]
    fn the_same_fault_seen_again_is_not_raised_twice() {
        // The wording carries the numbers, and those move every run while the fault stays the
        // same. Matching on them would open a fresh item for the same problem every time.
        let mut project = project("The E28");

        project = recorder::record(&project, &recorder::sitting(&log(16.5), "", "", ""));
        let after = project.open().count();

        project = recorder::record(&project, &recorder::sitting(&log(16.9), "", "", ""));

        assert_eq!(project.open().count(), after);
    }

    #[test]
    fn but_it_is_noted_against_the_fix_that_is_already_open() {
        // Which, on an applied fix, is the evidence the change did not work.
        let project = recorder::record(&project("The E28"), &recorder::sitting(&log(16.5), "", "", ""));

        let first = project.open().next().unwrap().clone();
        let project = project.with_fix(TuningFix {
            state: FixState::Applied,
            change: "VE +4%".to_string(),
            ..first.clone()
        });

        let project = recorder::record(&project, &recorder::sitting(&log(16.4), "", "", ""));

        let now = project.fix(&first.id).unwrap();

        assert!(now.evidence.len() > first.evidence.len());
        assert_eq!(now.state, FixState::Applied);
    }

    #[test]
    fn a_fix_settled_is_no_longer_added_to() {
        let project = recorder::record(&project("The E28"), &recorder::sitting(&log(16.5), "", "", ""));

        let first = project.open().next().unwrap().clone();
        let project = project.with_fix(TuningFix {
            state: FixState::Abandoned,
            settled: Some(VersionTime::now()),
            ..first.clone()
        });

        let project = recorder::record(&project, &recorder::sitting(&log(16.5), "", "", ""));

        // A new one is raised instead, because the old one was closed deliberately and reopening it
        // behind somebody's back would be worse.
        assert!(project.open().next().is_some());
        assert!(project.open().all(|f| f.id != first.id));
    }

    // ----- how it reads ---------------------------------------------------------------------

    #[test]
    fn the_brief_leads_with_what_is_still_wrong() {
        // A reader who stops after the first screen should still have the part that changes what
        // they do next.
        let mut open = TuningFix::new("lean-under-load", "Lean above 150 kPa");
        open.state = FixState::Applied;
        open.change = "VE +4% above 150 kPa".to_string();
        open.evidence = vec![
            "2026-08-01: 46% short".to_string(),
            "2026-08-20: 12% short, better".to_string(),
        ];

        let mut settled = TuningFix::new("iat-sensor", "IAT never moves");
        settled.state = FixState::Verified;
        settled.settled = Some(VersionTime::now());

        let project = project("The E28").with_fix(open).with_fix(settled);

        let brief = TuningProjectStore::brief(&project, DEFAULT_BRIEF_SESSIONS);

        assert!(brief.contains("# The E28"));
        assert!(brief.contains("## Still open"));

        // The open one, with its history, before the settled one.
        assert!(brief.find("lean-under-load").unwrap() < brief.find("iat-sensor").unwrap());

        assert!(brief.contains("VE +4% above 150 kPa"));
        assert!(brief.contains("12% short, better"));
    }

    #[test]
    fn a_sitting_where_nothing_was_flagged_says_so_in_one_line() {
        let project = project("The E28").with_session(recorder::sitting(&log(13.0), "", "", ""));

        let brief = TuningProjectStore::brief(&project, DEFAULT_BRIEF_SESSIONS);

        assert!(brief.contains("Nothing flagged."));
    }

    #[test]
    fn and_a_project_with_nothing_outstanding_says_that_too() {
        assert!(
            TuningProjectStore::brief(&project("The E28"), DEFAULT_BRIEF_SESSIONS).contains("Nothing outstanding.")
        );
    }

    #[test]
    fn only_the_recent_sittings_are_rendered_and_the_rest_are_counted() {
        // Forty sittings in front of a model buries the three fixes that are actually open. The
        // whole history stays in the file.
        let mut project = project("The E28");
        for _ in 0..12 {
            project = project.with_session(recorder::sitting(&log(13.0), "", "", ""));
        }

        let brief = TuningProjectStore::brief(&project, 3);

        assert!(brief.contains("9 earlier sittings are in the file."));
    }

    // ----- the shape the command line depends on --------------------------------------------

    #[test]
    fn recording_the_same_log_twice_keeps_both_sittings_and_one_fix() {
        // What the offline tool does when it is run over a folder, or run again after a change
        // that did not work: the history grows, the tracker does not.
        let mut project = project("The E28");

        project = recorder::record(&project, &recorder::sitting(&log(16.5), "", "", ""));
        project = recorder::record(&project, &recorder::sitting(&log(16.5), "", "", ""));

        assert_eq!(project.sessions.len(), 2);
        assert_eq!(project.open().count(), 1);

        // And the second time is written down against the fix, which is the evidence that whatever
        // was tried has not worked yet.
        assert!(project.open().next().unwrap().evidence.len() >= 2);
    }

    #[test]
    fn a_note_is_kept_with_the_sitting_it_belongs_to() {
        let sitting = recorder::sitting(&log(13.0), "MS3", "after VE +4% above 150 kPa", "");

        assert_eq!(sitting.note, "after VE +4% above 150 kPa");
    }

    // ----- the file's own format -------------------------------------------------------------

    #[test]
    fn a_version_read_back_keeps_its_offset() {
        let scratch = Scratch::new();
        let store = scratch.store();

        let mut project = project("The E28");
        let mut version = TuneVersion::new("v1", "abc", "tunes/v1.msq");
        version.at = VersionTime {
            unix_seconds: 1_794_000_000,
            offset_minutes: 120,
        };
        project = project.with_version(version);

        store.write(&project).unwrap();

        let back = store.read("The E28").unwrap();
        assert_eq!(back.version("v1").unwrap().at.offset_minutes, 120);
        assert_eq!(back.version("v1").unwrap().at.unix_seconds, 1_794_000_000);
    }
}
