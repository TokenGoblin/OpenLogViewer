//! Ported from `TuneHistory.cs`. Owned by package `w5-projects`.
//!
//! The tunes a project has been through, and what changed between them.
//!
//! **Whole copies, not deltas.** A saved tune is about 120 to 150 KB, so two hundred versions is
//! tens of megabytes, which is nothing against what it buys. Deltas would introduce the one failure
//! this must not have: a chain where an early corruption silently spoils everything after it. Each
//! version stands alone and can be opened by TunerStudio without this program existing.
//!
//! **Identity is the bytes, not the moment.** Reading the tune at the start of two sessions that
//! changed nothing gives one version, not two, and pressing burn twice does not branch history.
//!
//! **What makes it worth more than a folder of files** is that a version knows its parent, knows
//! why it was made, knows which fixes it was meant to address, and, through the sitting that names
//! it, knows which logs were recorded while the controller was running it.

use std::path::{Path, PathBuf};

use olv_core::definitions::msq_file::MsqFile;
use olv_core::ini::tune_layout::TuneLayout;
use olv_core::tune::ecu_tune::EcuTune;
use olv_core::tune::msq_apply;
use olv_core::tune::msq_writer;
use olv_core::tune::tune_compare;
use olv_core::tune::tune_version::{TuneVersion, VersionDifference};
use olv_core::{Error, Result};
use sha2::{Digest, Sha256};

use super::tuning_project::TuningProject;

/// Where a project keeps its tunes, relative to the project's folder.
///
/// Written with a forward slash on every platform: Windows accepts it, and a file written on one
/// system resolves on another. [`version_path`] accepts either separator when reading.
pub const FOLDER: &str = "tunes";

/// What the tune's bytes come to, for telling one version from another: the first eight bytes of
/// SHA-256 over the pages, as sixteen lower-case hex digits.
///
/// Over the pages rather than over the written file, because a file carries a timestamp and a
/// comment that move when nothing about the tune has.
pub fn fingerprint(tune: &EcuTune) -> String {
    let mut hasher = Sha256::new();
    for page in tune.pages() {
        hasher.update(page);
    }

    hasher.finalize().iter().take(8).map(|b| format!("{b:02x}")).collect()
}

/// Records the tune as a version of the project, or returns the version already holding these
/// bytes. The bool is whether the version is new.
///
/// `burned` is kept rather than worked out, and matters: a tune written but not burned is gone at
/// the next power cycle, so a log recorded after one is evidence about a tune the controller may
/// not be running any more.
pub fn capture(
    project: TuningProject,
    project_folder: &Path,
    tune: &EcuTune,
    signature: &str,
    note: &str,
    addresses: &[&str],
    burned: bool,
) -> Result<(TuningProject, TuneVersion, bool)> {
    let fingerprint = fingerprint(tune);

    // The same bytes again. Worth updating what is known about it (a burn of a version already
    // recorded is news) but not worth a new version.
    if let Some(index) = project.versions.iter().position(|v| v.fingerprint == fingerprint) {
        let mut updated = project.versions[index].clone();
        updated.burned = updated.burned || burned;
        if !note.is_empty() && updated.note.is_empty() {
            updated.note = note.to_string();
        }

        let project = project.with_version(updated.clone());
        return Ok((project, updated, false));
    }

    let id = project.next_version_id();
    let file = format!("{FOLDER}/{id}.msq");

    std::fs::create_dir_all(project_folder.join(FOLDER)).map_err(Error::Io)?;

    let comment = if note.is_empty() {
        format!("{} {id}", project.vehicle)
    } else {
        note.to_string()
    };

    msq_writer::save(&project_folder.join(&file), tune, signature, None, &comment, None)?;

    let mut version = TuneVersion::new(id, fingerprint, file);
    version.signature = signature.to_string();

    // The newest is the parent, which is what a linear history means. Nothing here branches: a
    // tune is a single thing on a single controller, and pretending otherwise would invite a
    // merge, which for a set of engine settings is not a thing anyone should be offered.
    version.parent = project.versions.last().map(|v| v.id.clone()).unwrap_or_default();
    version.note = note.to_string();
    version.addresses = addresses.iter().map(|a| a.to_string()).collect();
    version.burned = burned;

    let project = project.with_version(version.clone());
    Ok((project, version, true))
}

/// Reads a version's tune back. `Ok(None)` where the version is unknown or its file has gone or
/// cannot be read as a tune; an error only where the C# would also have let one escape.
pub fn read(
    project: &TuningProject,
    project_folder: &Path,
    version_id: &str,
    layout: &TuneLayout,
) -> Result<Option<EcuTune>> {
    let Some(version) = project.version(version_id) else {
        return Ok(None);
    };

    let path = version_path(project_folder, &version.file);
    if !path.is_file() {
        return Ok(None);
    }

    match MsqFile::read_file(&path) {
        Ok(file) => Ok(Some(msq_apply::load(layout, &file, None).tune)),
        // What the C# caught: a file that went missing or cannot be read, and a file that is not a
        // tune. Anything else (malformed XML) propagates, as it did in the C#.
        Err(Error::Io(_) | Error::LogFormat(_)) => Ok(None),
        Err(e) => Err(e),
    }
}

/// What changed between two versions, setting by setting.
///
/// Through the values rather than the bytes, which is the only comparison that means anything to a
/// person: two tunes can differ in bits no constant declares and be the same tune.
pub fn compare(
    project: &TuningProject,
    project_folder: &Path,
    from: &str,
    to: &str,
    layout: &TuneLayout,
) -> Result<Option<VersionDifference>> {
    let earlier = read(project, project_folder, from, layout)?;
    let later = read(project, project_folder, to, layout)?;

    let (Some(earlier), Some(later)) = (earlier, later) else {
        return Ok(None);
    };

    Ok(Some(VersionDifference {
        from: from.to_string(),
        to: to.to_string(),
        differences: tune_compare::compare(&later, &earlier),
    }))
}

/// The file a stored version path names, under the project folder.
///
/// The C# stored `tunes\v1.msq` on Windows. Both separators are split on, so that file resolves
/// anywhere, and the path never becomes absolute.
fn version_path(project_folder: &Path, stored: &str) -> PathBuf {
    stored
        .split(['/', '\\'])
        .filter(|part| !part.is_empty())
        .fold(project_folder.to_path_buf(), |path, part| path.join(part))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::projects::tuning_project::{FixState, ProjectSession, SessionFinding, TuningFix};
    use crate::projects::tuning_project_recorder as recorder;
    use olv_core::ini::tune_layout;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicU64, Ordering};

    const INI: &str = "[Constants]
page = 1
nPages = 1
pageSize = 16
pageIdentifier = \"\\x01\"
pageReadCommand = \"r%2o%2c\"
pageChunkWrite  = \"w%2o%2c%v\"
burnCommand     = \"b%2i\"
   crankingRPM = scalar, U16, 0, \"rpm\", 1, 0, 0, 10000, 0
   revLimit    = scalar, U16, 2, \"rpm\", 1, 0, 0, 10000, 0
   veTable     = array,  U08, 4, [4], \"%\", 1, 0, 0, 255, 0
";

    /// A directory under the system temp folder, removed when dropped.
    struct Scratch(PathBuf);

    impl Scratch {
        fn new() -> Self {
            static COUNTER: AtomicU64 = AtomicU64::new(0);
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0);
            Scratch(std::env::temp_dir().join(format!(
                "olv-versions-{}-{nanos}-{}",
                std::process::id(),
                COUNTER.fetch_add(1, Ordering::Relaxed)
            )))
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn layout() -> TuneLayout {
        tune_layout::read(INI, None)
    }

    fn tune(cranking: i32, rev: i32, ve: i32) -> EcuTune {
        let layout = Arc::new(layout());
        let mut tune = EcuTune::from_pages(layout.clone(), vec![vec![0u8; 16]]);

        tune.poke_into_own(named(&layout, "crankingRPM"), 0, f64::from(cranking));
        tune.poke_into_own(named(&layout, "revLimit"), 0, f64::from(rev));

        for i in 0..4 {
            tune.poke_into_own(named(&layout, "veTable"), i, f64::from(ve));
        }

        tune
    }

    fn named<'a>(layout: &'a TuneLayout, name: &str) -> &'a olv_core::ini::tune_layout::TuneConstant {
        layout.constants.iter().find(|c| c.name == name).unwrap()
    }

    fn tune_default() -> EcuTune {
        tune(300, 6500, 50)
    }

    fn project() -> TuningProject {
        TuningProject::new("The E28")
    }

    // ----- identity -------------------------------------------------------------------------

    #[test]
    fn the_same_tune_recorded_twice_is_one_version() {
        // Reading the tune at the start of two sessions that changed nothing, or pressing burn
        // twice, must not branch history. What is worth recording is that the controller held
        // something different.
        let folder = Scratch::new();

        let (project, first, was_new) = capture(
            project(),
            &folder.0,
            &tune_default(),
            "firmware",
            "baseline",
            &[],
            false,
        )
        .unwrap();

        assert!(was_new);

        let (project, again, second) =
            capture(project, &folder.0, &tune_default(), "firmware", "", &[], false).unwrap();

        assert!(!second);
        assert_eq!(first.id, again.id);
        assert_eq!(project.versions.len(), 1);
    }

    #[test]
    fn a_different_tune_is_a_new_version() {
        let folder = Scratch::new();

        let (project, _, _) = capture(
            project(),
            &folder.0,
            &tune_default(),
            "firmware",
            "baseline",
            &[],
            false,
        )
        .unwrap();

        let (project, second, was_new) = capture(
            project,
            &folder.0,
            &tune(300, 7000, 50),
            "firmware",
            "raised the limiter",
            &[],
            false,
        )
        .unwrap();

        assert!(was_new);
        assert_eq!(project.versions.len(), 2);
        assert_eq!(second.id, "v2");
        assert_eq!(second.parent, "v1");
    }

    #[test]
    fn burning_a_version_already_recorded_is_news_without_being_a_new_version() {
        // Written and then burned is the ordinary sequence, and it is one tune.
        let folder = Scratch::new();

        let (project, _, _) = capture(project(), &folder.0, &tune_default(), "firmware", "", &[], false).unwrap();
        assert!(!project.latest().unwrap().burned);

        let (project, burned, was_new) =
            capture(project, &folder.0, &tune_default(), "firmware", "", &[], true).unwrap();

        assert!(!was_new);
        assert!(burned.burned);
        assert_eq!(project.versions.len(), 1);
    }

    // ----- what is kept ---------------------------------------------------------------------

    #[test]
    fn a_version_is_an_ordinary_msq_anyone_else_can_open() {
        // A version-control system nobody else can read is a trap. The file beside the metadata is
        // the same .msq TunerStudio takes.
        let folder = Scratch::new();

        let (_, version, _) = capture(
            project(),
            &folder.0,
            &tune_default(),
            "firmware",
            "baseline",
            &[],
            false,
        )
        .unwrap();

        let path = folder.0.join(&version.file);
        assert!(path.is_file());

        let read = MsqFile::read_file(&path).unwrap();
        assert_eq!(read.signature, "firmware");
        assert_eq!(read.value("revLimit"), Some("6500.0"));
    }

    #[test]
    fn and_reads_back_as_the_tune_it_was() {
        let folder = Scratch::new();

        let (project, version, _) =
            capture(project(), &folder.0, &tune(300, 7200, 50), "firmware", "", &[], false).unwrap();

        let back = read(&project, &folder.0, &version.id, &layout()).unwrap();

        assert_eq!(back.expect("tune reads back").scalar("revLimit"), 7200.0);
    }

    #[test]
    fn a_version_whose_file_has_gone_reads_as_nothing_rather_than_throwing() {
        let folder = Scratch::new();

        let (project, version, _) = capture(project(), &folder.0, &tune_default(), "firmware", "", &[], false).unwrap();

        std::fs::remove_file(folder.0.join(&version.file)).unwrap();

        assert!(read(&project, &folder.0, &version.id, &layout()).unwrap().is_none());
    }

    // ----- what changed ---------------------------------------------------------------------

    #[test]
    fn two_versions_are_compared_by_setting_rather_than_by_bytes() {
        // Which is the only comparison that means anything to a person: two tunes can differ in
        // bits no constant declares and be the same tune.
        let folder = Scratch::new();

        let (project, _, _) = capture(project(), &folder.0, &tune_default(), "firmware", "", &[], false).unwrap();
        let (project, _, _) = capture(project, &folder.0, &tune(300, 7000, 54), "firmware", "", &[], false).unwrap();

        let diff = compare(&project, &folder.0, "v1", "v2", &layout())
            .unwrap()
            .expect("both versions read");

        assert!(diff.differences.iter().any(|d| d.name == "revLimit"));
        assert!(diff.differences.iter().any(|d| d.name == "veTable"));
        assert!(diff.summary().contains("differ"));
    }

    #[test]
    fn and_say_nothing_changed_when_nothing_did() {
        let folder = Scratch::new();

        let (project, _, _) = capture(project(), &folder.0, &tune_default(), "firmware", "", &[], false).unwrap();

        let diff = compare(&project, &folder.0, "v1", "v1", &layout())
            .unwrap()
            .expect("version reads");

        assert!(diff.is_empty());
        assert!(diff.summary().contains("same tune"));
    }

    // ----- the whole point ------------------------------------------------------------------

    #[test]
    fn a_fix_is_answered_by_logs_recorded_on_the_tune_that_carried_the_change() {
        // The question everybody actually asks, and the reason a sitting names its version. A run
        // on the old tune that happens to look clean proves nothing, and neither does one on the
        // new tune from before it existed.
        let folder = Scratch::new();
        let mut project = project();

        let (next, before, _) =
            capture(project, &folder.0, &tune_default(), "firmware", "baseline", &[], false).unwrap();
        project = next;

        // Something is wrong, and it is being tracked.
        project = project.with_fix(TuningFix {
            state: FixState::Applied,
            change: "VE +4% above 150 kPa".to_string(),
            ..TuningFix::new("mixture-under-load", "Lean above 150 kPa")
        });

        assert!(
            recorder::verdict(&project, project.fix("mixture-under-load").unwrap())
                .contains("no tune version claims it")
        );

        // The change is made, and the version says what it was for.
        let (next, after, _) = capture(
            project,
            &folder.0,
            &tune(300, 6500, 54),
            "firmware",
            "VE +4% up top",
            &["mixture-under-load"],
            true,
        )
        .unwrap();
        project = next;

        assert!(
            recorder::verdict(&project, project.fix("mixture-under-load").unwrap())
                .contains("nothing has been recorded on it yet")
        );

        // A clean run, on the new tune.
        project = project.with_session(ProjectSession {
            log: "after.mlg".to_string(),
            version: after.id.clone(),
            findings: vec![SessionFinding::new(
                "Good",
                "Mixture under load",
                "Fuelling holds target",
            )],
            ..ProjectSession::default()
        });

        let verdict = recorder::verdict(&project, project.fix("mixture-under-load").unwrap());

        assert!(verdict.contains("none of them complained"));
        assert_ne!(before.id, after.id);
    }

    #[test]
    fn and_still_says_so_when_the_change_did_not_work() {
        let folder = Scratch::new();
        let mut project = project();

        (project, _, _) = capture(project, &folder.0, &tune_default(), "firmware", "", &[], false).unwrap();

        project = project.with_fix(TuningFix {
            state: FixState::Applied,
            ..TuningFix::new("mixture-under-load", "Lean above 150 kPa")
        });

        let (next, after, _) = capture(
            project,
            &folder.0,
            &tune(300, 6500, 54),
            "firmware",
            "",
            &["mixture-under-load"],
            false,
        )
        .unwrap();
        project = next;

        project = project.with_session(ProjectSession {
            log: "after.mlg".to_string(),
            version: after.id.clone(),
            findings: vec![SessionFinding::new("Warning", "Mixture under load", "Still 12% short")],
            ..ProjectSession::default()
        });

        assert!(recorder::verdict(&project, project.fix("mixture-under-load").unwrap()).contains("has not settled it"));
    }

    #[test]
    fn a_clean_run_on_the_old_tune_proves_nothing() {
        // The trap this exists to close. Without the version on the sitting, any clean log after
        // the change looks like evidence for it.
        let folder = Scratch::new();
        let mut project = project();

        let (next, old, _) = capture(project, &folder.0, &tune_default(), "firmware", "", &[], false).unwrap();
        project = next;

        project = project.with_fix(TuningFix {
            state: FixState::Applied,
            ..TuningFix::new("mixture-under-load", "Lean above 150 kPa")
        });

        let (next, _, _) = capture(
            project,
            &folder.0,
            &tune(300, 6500, 54),
            "firmware",
            "",
            &["mixture-under-load"],
            false,
        )
        .unwrap();
        project = next;

        // Recorded afterwards, but on the tune from before the change.
        project = project.with_session(ProjectSession {
            log: "old-tune.mlg".to_string(),
            version: old.id.clone(),
            findings: vec![SessionFinding::new("Good", "Mixture under load", "Looks fine")],
            ..ProjectSession::default()
        });

        assert!(
            recorder::verdict(&project, project.fix("mixture-under-load").unwrap())
                .contains("nothing has been recorded on it yet")
        );
    }
}
