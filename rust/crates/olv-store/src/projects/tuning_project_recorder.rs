//! Ported from `TuningProjectRecorder.cs`. Owned by package `w5-projects`.
//!
//! Turning a log into a sitting in a project. Kept apart from the store so that the thing which
//! knows about insights does not also have to know about files, and so this can be used from
//! anywhere a log exists.

use olv_core::insights::insights_from;
use olv_core::model::LogDocument;
use olv_core::tune::tune_version::TuneVersion;

use super::tuning_project::{
    FixState, ProjectSession, SessionFinding, TuningFix, TuningProject, day, eq_ignore_case, folded, slug,
};

/// The sitting a log amounts to.
///
/// Every finding is kept, not only the ones that complain. A run where nothing was wrong is
/// evidence too: it is what a fix is verified against, and a project that records only bad days
/// cannot show anything getting better.
pub fn sitting(log: &LogDocument, signature: &str, note: &str, version: &str) -> ProjectSession {
    let time = log.time();
    let samples = time.len();

    ProjectSession {
        log: file_name_of(log.file_path()),
        signature: signature.to_string(),
        samples: i32::try_from(samples).unwrap_or(i32::MAX),
        seconds: if samples > 0 { time.at(samples - 1) } else { 0.0 },
        note: note.to_string(),
        version: version.to_string(),
        findings: insights_from(log)
            .into_iter()
            .map(|i| SessionFinding {
                level: i.level.name().to_string(),
                topic: i.topic,
                title: i.title,
                detail: i.detail,
            })
            .collect(),
        ..ProjectSession::default()
    }
}

/// Records a sitting and raises a fix for anything warned about that is not already tracked.
///
/// Matched on the finding's topic rather than on its wording, because the wording carries the
/// numbers ("lean on 82 samples, worst 46.8 % short") and those move every run while the problem
/// stays the same. Matching on them would raise a fresh fix for the same fault every time.
///
/// Only warnings raise a fix. Something merely worth watching is recorded in the sitting and left
/// there; a tracker that opens an item for every observation hides the three things actually
/// wrong.
pub fn record(project: &TuningProject, sitting: &ProjectSession) -> TuningProject {
    let mut updated = project.clone().with_session(sitting.clone());

    for finding in sitting.findings.iter().filter(|f| f.level == "Warning") {
        if let Some(already) = tracking(&updated, &finding.topic).cloned() {
            // Seen again. Worth noting against the fix rather than raising a second one, and
            // worth noting on an applied fix especially: that is the evidence the change did not
            // work.
            let mut next = already;
            next.evidence.push(format!("{}: {}", day(sitting.at), finding.title));
            updated = updated.with_fix(next);
            continue;
        }

        let id = updated.new_id(&finding.topic);

        updated = updated.with_fix(TuningFix {
            id,
            title: finding.title.clone(),
            detail: finding.detail.clone(),
            state: FixState::Open,
            change: String::new(),
            evidence: vec![format!("{}: first seen in {}", day(sitting.at), sitting.log)],
            raised: sitting.at,
            settled: None,
        });
    }

    updated
}

/// An open fix already covering this topic, if there is one.
fn tracking<'a>(project: &'a TuningProject, topic: &str) -> Option<&'a TuningFix> {
    let prefix = folded(&slug(topic));
    project.open().find(|f| folded(&f.id).starts_with(&prefix))
}

/// Whether the evidence now says a fix worked.
///
/// This is what the version on a sitting is for. A fix is only answered by a log recorded after
/// the change and on the tune that carried it: a run on the old version that happens to look
/// clean proves nothing, and neither does a run on the new one from before it was written.
///
/// It reports rather than decides. Moving a fix to verified is somebody's judgement: one clean
/// run is not always enough, and this cannot know whether the drive exercised the part of the map
/// in question.
pub fn verdict(project: &TuningProject, fix: &TuningFix) -> String {
    // The version that claimed to address it, if any did.
    let Some((made_at, change)) = project
        .versions
        .iter()
        .enumerate()
        .rev()
        .find(|(_, v)| v.addresses.iter().any(|a| eq_ignore_case(a, &fix.id)))
    else {
        return if fix.state == FixState::Applied {
            "A change was recorded against this, but no tune version claims it \u{2014} so there \
             is nothing to say which logs came after it."
                .to_string()
        } else {
            "Nothing has been changed for this yet.".to_string()
        };
    };

    // Sittings on that version or later. Ordered by the version's own position rather than by
    // date, because a log can be imported long after it was recorded.
    let after: Vec<&ProjectSession> = project
        .sessions
        .iter()
        .filter(|s| !s.version.is_empty())
        .filter(|s| position_of(project, &s.version).is_some_and(|i| i >= made_at))
        .collect();

    if after.is_empty() {
        return format!(
            "{} was made for this and nothing has been recorded on it yet.",
            change.id
        );
    }

    let topic = slug(&fix.id);

    let complaining = after
        .iter()
        .filter(|s| {
            s.findings
                .iter()
                .any(|f| f.level == "Warning" && slug(&f.topic).starts_with(&topic))
        })
        .count();

    let plural = if after.len() == 1 { "" } else { "s" };

    if complaining == 0 {
        return format!(
            "{} sitting{plural} on {} or later, and none of them complained about this. \
             That is what verified would rest on.",
            after.len(),
            change.id
        );
    }

    format!(
        "Still seen on {complaining} of {} sitting{plural} since {}. The change has not settled it.",
        after.len(),
        change.id
    )
}

fn position_of(project: &TuningProject, id: &str) -> Option<usize> {
    project.versions.iter().position(|v: &TuneVersion| v.id == id)
}

/// The last segment of a path, split on either separator so that a path written on one system
/// reads the same on another.
fn file_name_of(path: &str) -> String {
    if path.is_empty() {
        return String::new();
    }

    path.rsplit(['/', '\\']).next().unwrap_or_default().to_string()
}
