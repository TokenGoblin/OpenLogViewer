//! Ported from `TuneVersion.cs`. Owned by package `w4-tune`.
//!
//! One tune, as it stood at a moment worth keeping.
//!
//! The thing this replaces is a folder of files called `claude01.msq` through `claude7.msq`,
//! one of them spelled `claud02`, beside a `Before Fuel Cleanup.msq` and a `CurrentTune.msq`
//! that cannot tell you whether it is what the controller is running. Every one of those
//! filenames is somebody trying to record *why* in the only field the filesystem gave them.
//!
//! So a version carries the why. What it was for, which fixes it was meant to address, what it
//! came from, and whether it was ever committed to flash; and the tune itself sits beside it as
//! an ordinary `.msq` that TunerStudio can still open, because a version-control system nobody
//! else can read is a trap rather than a feature.

use super::tune_compare::TuneDifference;
use crate::fmt;
use std::time::{SystemTime, UNIX_EPOCH};

/// A moment with the offset from UTC it was recorded at: the C#'s `DateTimeOffset`, to the
/// second.
///
/// The offset is kept because [`TuneVersion::summary`] prints the wall clock of the place the
/// version was made, not UTC. std cannot ask the machine for its offset, so
/// [`VersionTime::now`] is UTC (offset 0); a caller that knows the offset builds the value
/// itself.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct VersionTime {
    /// Seconds since 1970-01-01T00:00:00Z.
    pub unix_seconds: i64,
    /// Minutes east of UTC.
    pub offset_minutes: i32,
}

impl VersionTime {
    pub fn now() -> Self {
        VersionTime {
            unix_seconds: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0),
            offset_minutes: 0,
        }
    }

    /// `yyyy-MM-dd HH:mm` on the wall clock of the offset it was recorded at.
    pub fn short(&self) -> String {
        let c = civil(self.unix_seconds + i64::from(self.offset_minutes) * 60);
        format!(
            "{:04}-{:02}-{:02} {:02}:{:02}",
            c.year, c.month, c.day, c.hour, c.minute
        )
    }
}

/// Calendar fields of a moment, in whatever zone the seconds were already shifted to.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct Civil {
    pub year: i64,
    pub month: u32,
    pub day: u32,
    pub hour: u32,
    pub minute: u32,
    pub second: u32,
    /// 0 is Sunday.
    pub weekday: u32,
}

/// The proleptic Gregorian calendar fields of `seconds` since the Unix epoch.
pub(crate) fn civil(seconds: i64) -> Civil {
    let days = seconds.div_euclid(86_400);
    let of_day = seconds.rem_euclid(86_400);

    // Howard Hinnant's days-to-civil.
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    let year = if month <= 2 { y + 1 } else { y };

    Civil {
        year,
        month,
        day,
        hour: (of_day / 3_600) as u32,
        minute: (of_day % 3_600 / 60) as u32,
        second: (of_day % 60) as u32,
        weekday: (days + 4).rem_euclid(7) as u32,
    }
}

/// One tune, as it stood at a moment worth keeping. See the module docs.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneVersion {
    /// Short and ordered ("v4") so it can be said aloud and sorted.
    pub id: String,
    pub at: VersionTime,
    /// The firmware this came off, so a version is never read against the wrong one.
    pub signature: String,
    /// What the bytes come to, so the same tune is never stored twice.
    ///
    /// Pressing burn twice, or reading the tune at the start of two sessions that changed
    /// nothing, should not make two versions. Identity is what the controller holds, not when
    /// somebody looked at it.
    pub fingerprint: String,
    /// The version this was made from, or empty for the first.
    pub parent: String,
    /// Why this exists, in the words of whoever made it.
    pub note: String,
    /// The fixes this version was made to address, by id.
    pub addresses: Vec<String>,
    /// Whether this was committed to flash.
    ///
    /// Worth recording rather than inferring: a tune written but not burned is gone at the next
    /// power cycle, so a log recorded after one is evidence about a tune the controller may no
    /// longer be running.
    pub burned: bool,
    /// The file, relative to the project's folder.
    pub file: String,
}

impl TuneVersion {
    /// A version with the C#'s defaults for everything but the three required fields: stamped
    /// now, no signature, no parent, no note, not burned.
    pub fn new(id: impl Into<String>, fingerprint: impl Into<String>, file: impl Into<String>) -> Self {
        TuneVersion {
            id: id.into(),
            at: VersionTime::now(),
            signature: String::new(),
            fingerprint: fingerprint.into(),
            parent: String::new(),
            note: String::new(),
            addresses: Vec::new(),
            burned: false,
            file: file.into(),
        }
    }

    /// How this reads in a list.
    pub fn summary(&self) -> String {
        format!(
            "{} \u{b7} {}{}{}",
            self.id,
            self.at.short(),
            if self.burned { " \u{b7} burned" } else { "" },
            if self.note.is_empty() {
                String::new()
            } else {
                format!(" \u{2014} {}", self.note)
            }
        )
    }
}

/// What changed between two versions, said in settings rather than in bytes.
#[derive(Clone, Debug)]
pub struct VersionDifference {
    /// The earlier version's id.
    pub from: String,
    /// The later one's.
    pub to: String,
    /// Every setting the two disagree about.
    pub differences: Vec<TuneDifference>,
}

impl VersionDifference {
    pub fn is_empty(&self) -> bool {
        self.differences.is_empty()
    }

    /// One line, for a list or a status bar.
    pub fn summary(&self) -> String {
        if self.is_empty() {
            format!("{} and {} hold the same tune.", self.from, self.to)
        } else {
            format!(
                "{} setting{} differ between {} and {}.",
                fmt::int(self.differences.len() as i32, "N0"),
                if self.differences.len() == 1 { "" } else { "s" },
                self.from,
                self.to
            )
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn version() -> TuneVersion {
        let mut v = TuneVersion::new("v4", "abc123", "tunes/v4.msq");
        v.at = VersionTime {
            unix_seconds: 1_794_000_000,
            offset_minutes: 0,
        };
        v
    }

    #[test]
    fn a_version_reads_as_its_id_its_moment_and_its_why() {
        let mut v = version();
        assert_eq!(v.summary(), "v4 \u{b7} 2026-11-06 21:20");

        v.burned = true;
        v.note = "leaner idle".to_string();
        assert_eq!(
            v.summary(),
            "v4 \u{b7} 2026-11-06 21:20 \u{b7} burned \u{2014} leaner idle"
        );
    }

    #[test]
    fn the_wall_clock_is_the_one_the_version_was_recorded_at() {
        let mut v = version();
        v.at.offset_minutes = -300;
        assert_eq!(v.at.short(), "2026-11-06 16:20");
    }

    #[test]
    fn what_changed_between_two_versions_is_said_in_settings() {
        let same = VersionDifference {
            from: "v1".into(),
            to: "v2".into(),
            differences: Vec::new(),
        };
        assert!(same.is_empty());
        assert_eq!(same.summary(), "v1 and v2 hold the same tune.");
    }
}
