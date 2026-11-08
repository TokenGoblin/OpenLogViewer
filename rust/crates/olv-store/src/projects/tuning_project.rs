//! Ported from `TuningProject.cs`. Owned by package `w5-projects`.
//!
//! One thing wrong with a tune, and what has been done about it; one sitting with the car; and
//! everything known about one vehicle's tune across every sitting.
//!
//! The analysis is the easy half. A log says the mixture is lean above 150 kPa; what it cannot
//! say is that this was known three weeks ago, four per cent was added to the top of the VE
//! table, and it got better but not right. That half lives in somebody's head and is gone by the
//! next session, and it is exactly the half an assistant needs in order to be useful rather than
//! to start over each time. So a fix carries its reasoning, and its evidence is a list rather
//! than a field: the useful record of a fix is the sequence, and overwriting one note with the
//! next throws away the only evidence that a change is working at all.

use olv_core::tune::tune_version::{TuneVersion, VersionTime};
use std::cmp::Ordering;

/// Where a fix has got to.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum FixState {
    /// Seen in the data, nothing changed yet.
    #[default]
    #[serde(alias = "Open")]
    Open,
    /// A change was made. Whether it worked is not yet known.
    #[serde(alias = "Applied")]
    Applied,
    /// A later log says the change did what it was meant to.
    #[serde(alias = "Verified")]
    Verified,
    /// Dropped: wrong diagnosis, or not worth chasing.
    #[serde(alias = "Abandoned")]
    Abandoned,
}

impl FixState {
    /// The C# enum's name, lower-cased as the brief writes it.
    pub fn name(self) -> &'static str {
        match self {
            FixState::Open => "open",
            FixState::Applied => "applied",
            FixState::Verified => "verified",
            FixState::Abandoned => "abandoned",
        }
    }
}

/// One thing wrong with a tune, and what has been done about it.
#[derive(Clone, Debug, PartialEq)]
pub struct TuningFix {
    /// Short and stable, so it can be referred to across sessions.
    pub id: String,
    /// What is wrong, in one line.
    pub title: String,
    /// The reasoning: what was seen, and what it is thought to mean.
    pub detail: String,
    pub state: FixState,
    /// What was actually changed, once something was.
    pub change: String,
    /// What has been observed about it since, newest last.
    pub evidence: Vec<String>,
    pub raised: VersionTime,
    /// When it was verified or abandoned; `None` while it is still live.
    pub settled: Option<VersionTime>,
}

impl TuningFix {
    /// An open fix raised now, with nothing yet changed or observed.
    pub fn new(id: impl Into<String>, title: impl Into<String>) -> Self {
        TuningFix {
            id: id.into(),
            title: title.into(),
            detail: String::new(),
            state: FixState::Open,
            change: String::new(),
            evidence: Vec::new(),
            raised: VersionTime::now(),
            settled: None,
        }
    }

    /// Seen or tried, but not yet settled.
    pub fn is_open(&self) -> bool {
        matches!(self.state, FixState::Open | FixState::Applied)
    }
}

/// An insight as the project keeps it: the words, without the machinery.
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct SessionFinding {
    /// The C# `InsightLevel` name: "Good", "Note", "Watch", "Warning" or "Unanswered".
    pub level: String,
    pub topic: String,
    pub title: String,
    pub detail: String,
}

impl SessionFinding {
    pub fn new(level: impl Into<String>, topic: impl Into<String>, title: impl Into<String>) -> Self {
        SessionFinding {
            level: level.into(),
            topic: topic.into(),
            title: title.into(),
            detail: String::new(),
        }
    }
}

/// One sitting with the car: a log looked at, and what it said.
#[derive(Clone, Debug, PartialEq)]
pub struct ProjectSession {
    pub at: VersionTime,
    /// The log's file name, not its path: paths move and mean nothing later.
    pub log: String,
    pub signature: String,
    pub samples: i32,
    pub seconds: f64,
    /// What the insights said, flattened to text.
    pub findings: Vec<SessionFinding>,
    /// Anything a person or an agent wanted to record about the sitting.
    pub note: String,
    /// The tune the controller was running, by version id. This is the join the whole thing turns
    /// on: without it, "still lean" and "lean again after the change" are the same sentence.
    pub version: String,
}

impl Default for ProjectSession {
    fn default() -> Self {
        ProjectSession {
            at: VersionTime::now(),
            log: String::new(),
            signature: String::new(),
            samples: 0,
            seconds: 0.0,
            findings: Vec::new(),
            note: String::new(),
            version: String::new(),
        }
    }
}

impl ProjectSession {
    /// The findings worth acting on, which is what a summary leads with.
    pub fn warnings(&self) -> usize {
        self.findings
            .iter()
            .filter(|f| f.level == "Warning" || f.level == "Watch")
            .count()
    }
}

/// Everything known about one vehicle's tune, across every sitting.
#[derive(Clone, Debug, PartialEq)]
pub struct TuningProject {
    /// What the car is called. Also its folder name.
    pub vehicle: String,
    pub engine: String,
    /// The firmware last seen on it, for spotting a project opened against the wrong car.
    pub signature: String,
    /// Anything that stays true: fuel, injectors, what the car is for.
    pub notes: String,
    pub sessions: Vec<ProjectSession>,
    pub fixes: Vec<TuningFix>,
    /// Every tune this project has been through, oldest first.
    pub versions: Vec<TuneVersion>,
    pub started: VersionTime,
}

impl TuningProject {
    /// An empty project for a vehicle, started now.
    pub fn new(vehicle: impl Into<String>) -> Self {
        TuningProject {
            vehicle: vehicle.into(),
            engine: String::new(),
            signature: String::new(),
            notes: String::new(),
            sessions: Vec::new(),
            fixes: Vec::new(),
            versions: Vec::new(),
            started: VersionTime::now(),
        }
    }

    /// Adds a sitting, newest last.
    pub fn with_session(mut self, session: ProjectSession) -> Self {
        self.sessions.push(session);
        self
    }

    /// Adds a fix, or replaces the one with the same id. Replacing rather than appending, so that
    /// an agent told to move a fix from Open to Applied does not leave two of it.
    pub fn with_fix(mut self, fix: TuningFix) -> Self {
        self.fixes.retain(|f| !eq_ignore_case(&f.id, &fix.id));
        self.fixes.push(fix);
        self
    }

    /// Adds a version, or replaces the one with the same id.
    ///
    /// A replacement keeps its place, so the list stays oldest first: `latest` and the parent of
    /// the next version are the newest *recorded*, not the last one touched. (The C# moved the
    /// replaced version to the end, so re-burning an old version made it "latest".)
    pub fn with_version(mut self, version: TuneVersion) -> Self {
        match self.versions.iter().position(|v| eq_ignore_case(&v.id, &version.id)) {
            Some(index) => self.versions[index] = version,
            None => self.versions.push(version),
        }
        self
    }

    /// Fixes still live: seen or tried, not yet settled, in stored order.
    pub fn open(&self) -> impl Iterator<Item = &TuningFix> + '_ {
        self.fixes.iter().filter(|f| f.is_open())
    }

    /// The fix by that id, however it was capitalised.
    pub fn fix(&self, id: &str) -> Option<&TuningFix> {
        self.fixes.iter().find(|f| eq_ignore_case(&f.id, id))
    }

    /// The version by that id, however it was capitalised.
    pub fn version(&self, id: &str) -> Option<&TuneVersion> {
        self.versions.iter().find(|v| eq_ignore_case(&v.id, id))
    }

    /// The newest version, which is what the controller most likely holds.
    pub fn latest(&self) -> Option<&TuneVersion> {
        self.versions.last()
    }

    /// The next id in sequence: v1, v2, and so on.
    pub fn next_version_id(&self) -> String {
        let mut highest: i64 = 0;

        for version in &self.versions {
            // The C# requires more than one character, a leading v or V, and an integer after it.
            if let Some(rest) = version.id.strip_prefix(['v', 'V'])
                && let Some(n) = parse_int(rest)
            {
                highest = highest.max(n);
            }
        }

        format!("v{}", highest + 1)
    }

    /// An id nothing else is using, made from the title.
    ///
    /// Words rather than a number, because these are read aloud in notes and referred to by an
    /// agent across sessions: "lean-under-load" survives being quoted from memory and "fix-7"
    /// does not.
    pub fn new_id(&self, title: &str) -> String {
        let mut basis = slug(title);
        if basis.is_empty() {
            basis = "fix".to_string();
        }

        if self.fix(&basis).is_none() {
            return basis;
        }

        let mut n = 2u64;
        loop {
            let candidate = format!("{basis}-{n}");
            if self.fix(&candidate).is_none() {
                return candidate;
            }
            n += 1;
        }
    }
}

/// Parses an integer the way .NET's `int.TryParse` with the default `NumberStyles.Integer` does:
/// optional surrounding white space and an optional sign, ASCII digits only.
fn parse_int(text: &str) -> Option<i64> {
    let trimmed = text.trim_matches(|c| matches!(c, '\t'..='\r' | ' '));
    trimmed.parse::<i64>().ok()
}

/// The case-folding .NET's `OrdinalIgnoreCase` applies: each character's simple upper-case
/// mapping, where it has exactly one.
pub(crate) fn fold(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

/// `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`.
pub(crate) fn eq_ignore_case(a: &str, b: &str) -> bool {
    a.chars().map(fold).eq(b.chars().map(fold))
}

/// `string.Compare(a, b, StringComparison.OrdinalIgnoreCase)`.
pub(crate) fn cmp_ignore_case(a: &str, b: &str) -> Ordering {
    a.chars().map(fold).cmp(b.chars().map(fold))
}

/// The text folded the way [`eq_ignore_case`] compares it, for `starts_with`.
pub(crate) fn folded(text: &str) -> String {
    text.chars().map(fold).collect()
}

/// A short, stable, lower-case slug of some text: letters and digits kept, every other run
/// becoming one hyphen, no leading or trailing hyphen, and at most 32 UTF-16 units before the
/// trim.
///
/// The C# walks UTF-16 units and asks `char.IsLetterOrDigit` of each, so a surrogate never counts
/// as a letter; the same is done here.
pub(crate) fn slug(text: &str) -> String {
    let mut out = String::new();
    let mut length = 0usize;

    for unit in text.encode_utf16() {
        match char::from_u32(u32::from(unit)) {
            Some(c) if c.is_alphanumeric() => {
                out.extend(c.to_lowercase().take(1));
                length += 1;
            }
            _ => {
                if !out.is_empty() && !out.ends_with('-') {
                    out.push('-');
                    length += 1;
                }
            }
        }

        if length >= 32 {
            break;
        }
    }

    out.trim_matches('-').to_string()
}

/// The calendar day of a moment, `yyyy-MM-dd`, on the wall clock it was recorded at.
pub(crate) fn day(at: VersionTime) -> String {
    at.short().split(' ').next().unwrap_or_default().to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slug_keeps_letters_and_digits_and_joins_the_rest_with_hyphens() {
        assert_eq!(slug("Lean under load"), "lean-under-load");
        assert_eq!(slug("  --VE +4% @ 150 kPa!! "), "ve-4-150-kpa");
        assert_eq!(slug("!!!"), "");
    }

    #[test]
    fn slug_stops_at_thirty_two_units() {
        let long = "a".repeat(40);
        assert_eq!(slug(&long).len(), 32);
    }

    #[test]
    fn a_surrogate_pair_is_not_a_letter() {
        // .NET's IsLetterOrDigit is false for each surrogate half.
        assert_eq!(slug("a\u{1F600}b"), "a-b");
    }

    #[test]
    fn version_ids_count_past_the_highest_and_ignore_the_rest() {
        let mut project = TuningProject::new("car");
        for id in ["v1", "V7", "x9", "v 3", "v+2", "vx"] {
            project = project.with_version(TuneVersion::new(id, "f", "tunes/x.msq"));
        }

        assert_eq!(project.next_version_id(), "v8");
    }

    // Defect fixed in the Rust: replacing a version moved it to the end of the list.
    #[test]
    fn replacing_a_version_keeps_its_place_so_latest_stays_the_newest() {
        let mut old = TuneVersion::new("v1", "f1", "tunes/v1.msq");
        let newer = TuneVersion::new("v2", "f2", "tunes/v2.msq");
        let project = TuningProject::new("car").with_version(old.clone()).with_version(newer);

        old.burned = true;
        let project = project.with_version(old);

        let ids: Vec<&str> = project.versions.iter().map(|v| v.id.as_str()).collect();
        assert_eq!(ids, ["v1", "v2"]);
        assert_eq!(project.latest().unwrap().id, "v2");
        assert!(project.version("V1").unwrap().burned);
        assert_eq!(project.next_version_id(), "v3");
    }

    #[test]
    fn fix_lookup_ignores_case() {
        let project = TuningProject::new("car").with_fix(TuningFix::new("Lean", "x"));
        assert!(project.fix("LEAN").is_some());
    }
}
