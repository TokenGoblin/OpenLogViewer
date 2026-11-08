//! Ported from `TuneRestore.cs`. Owned by package `w4-tune`.
//!
//! What restoring a saved tune to a controller would actually do.
//!
//! Worked out and handed back before anything is sent, because this is the largest thing this
//! application can do to an engine and the only honest way to ask for it is to say what it
//! would change. "Restore this tune" is not a question anybody can answer; "change 47 settings,
//! one of them the rev limit, and leave 900 alone" is.
//!
//! **Only what differs is written, and only what the file actually carried.** The file is laid
//! over the controller's own bytes rather than over nothing, so a setting the file never
//! mentioned keeps whatever the ECU has, and then the two images are compared byte for byte, so
//! those settings produce no write at all. A tune saved from a neighbouring firmware revision
//! is missing a handful of constants, and the difference between leaving those alone and
//! writing zeros over them is the difference between a restore and a wrecked tune.

use super::ecu_tune::{EcuTune, TuneWrite};
use super::msq_apply::{self, MsqComplaint};
use super::tune_compare::{self, TuneDifference};
use crate::definitions::msq_file::MsqFile;
use crate::fmt;
use crate::model::eq_ignore_case;

/// What restoring a saved tune to a controller would actually do. See the module docs.
#[derive(Clone, Debug)]
pub struct TuneRestorePlan {
    /// The controller's pages as they would become.
    pub target: EcuTune,
    /// The bytes to send, gathered into runs.
    pub writes: Vec<TuneWrite>,
    /// Which settings would change, and to what. This is what a person is really being asked
    /// about.
    pub differences: Vec<TuneDifference>,
    /// Settings the firmware declares that the file never mentioned. Left exactly as the ECU
    /// has them, and counted so the shortfall is visible rather than discovered later.
    pub missing: Vec<MsqComplaint>,
    /// Settings the file gave a value this firmware could not store: an option name it no
    /// longer offers, a number outside its range. Left exactly as the ECU has them, and carried
    /// here because a plan that did not say so would describe a restore as complete when part
    /// of it was quietly dropped.
    pub rejected: Vec<MsqComplaint>,
    /// Names in the file this firmware has no constant for.
    pub unknown: Vec<String>,
    /// Which firmware the file says it is for.
    pub file_signature: String,
    /// Which firmware the controller answered with.
    pub ecu_signature: String,
}

impl TuneRestorePlan {
    /// Nothing to do: the controller already holds this tune.
    pub fn is_empty(&self) -> bool {
        self.writes.is_empty()
    }

    pub fn bytes(&self) -> i32 {
        self.writes.iter().map(|w| w.data.len() as i32).sum()
    }

    /// Pages this would touch, which are the pages a burn must commit.
    pub fn pages(&self) -> Vec<i32> {
        let mut pages: Vec<i32> = self.writes.iter().map(|w| w.page).collect();
        pages.sort_unstable();
        pages.dedup();
        pages
    }

    /// Whether the file and the controller agree about which firmware this is.
    ///
    /// A mismatch is not fatal on its own (a tune saved from revision 3.4.2 and a controller
    /// running 3.4.3 have almost everything in common) but it is the single fact most worth
    /// putting in front of somebody before they send eight hundred settings to an engine.
    pub fn signatures_agree(&self) -> bool {
        self.file_signature.is_empty()
            || self.ecu_signature.is_empty()
            || eq_ignore_case(&self.file_signature, &self.ecu_signature)
    }

    /// One line saying what this would do.
    pub fn summary(&self) -> String {
        if self.is_empty() {
            if self.complete() {
                "The ECU already holds this tune, setting for setting.".to_string()
            } else {
                format!("Nothing would change. {}", self.shortfall())
            }
        } else {
            let pages = self.pages().len();
            format!(
                "{} setting{} would change, {} bytes across {} page{}.{}",
                fmt::int(self.differences.len() as i32, "N0"),
                if self.differences.len() == 1 { "" } else { "s" },
                fmt::int(self.bytes(), "N0"),
                pages,
                if pages == 1 { "" } else { "s" },
                if self.complete() {
                    String::new()
                } else {
                    format!(" {}", self.shortfall())
                }
            )
        }
    }

    /// True when the file carried every setting, and all of them fitted.
    pub fn complete(&self) -> bool {
        self.missing.is_empty() && self.rejected.is_empty()
    }

    /// What the file did not manage to say, in words.
    ///
    /// Said whether or not anything would change: a file that turns out to carry nothing this
    /// firmware recognises produces no writes at all, and calling that "the ECU already holds
    /// this tune" is the opposite of the truth.
    pub fn shortfall(&self) -> String {
        let mut parts: Vec<String> = Vec::new();

        if !self.missing.is_empty() {
            parts.push(if self.missing.len() == 1 {
                "1 setting the firmware declares is not in the file and would be left as the ECU has it.".to_string()
            } else {
                format!(
                    "{} settings the firmware declares are not in the file and would be left as the ECU has them.",
                    fmt::int(self.missing.len() as i32, "N0")
                )
            });
        }

        if !self.rejected.is_empty() {
            parts.push(format!(
                "{} in the file could not be stored and would be left alone too.",
                fmt::int(self.rejected.len() as i32, "N0")
            ));
        }

        parts.join(" ")
    }
}

/// Plans a restore of a saved tune onto a controller.
///
/// `ecu` is the tune as read off the controller, `file` the saved tune to lay over it, and
/// `ecu_signature` what the controller answered when asked who it is (empty when unknown).
pub fn plan(ecu: &EcuTune, file: &MsqFile, ecu_signature: &str) -> TuneRestorePlan {
    // Over the controller's own bytes. Everything the file does not mention keeps what the ECU
    // has, which is what makes the byte comparison below produce no write for it.
    let load = msq_apply::load_shared(ecu.layout_arc().clone(), file, Some(ecu));

    TuneRestorePlan {
        writes: differing(ecu, &load.tune),
        differences: tune_compare::compare(&load.tune, ecu),
        missing: load.missing,
        rejected: load.rejected,
        unknown: load.unknown,
        file_signature: file.signature.clone(),
        ecu_signature: ecu_signature.to_string(),
        target: load.tune,
    }
}

/// The bytes that differ, gathered into runs.
///
/// The same shape a settings edit produces, and for the same reason: a write costs a round trip
/// and a run of four bytes is no dearer than one.
fn differing(ecu: &EcuTune, target: &EcuTune) -> Vec<TuneWrite> {
    let mut writes = Vec::new();

    for page in 0..ecu.pages().len().min(target.pages().len()) {
        runs(page as i32, &ecu.pages()[page], &target.pages()[page], &mut writes);
    }

    writes
}

/// Appends the runs of bytes where `now` differs from `was`.
///
/// Bytes beyond the shorter of the two never count as differing, but the run ending at the
/// shorter image's end is closed there, as the C# loop does.
pub(crate) fn runs(page: i32, was: &[u8], now: &[u8], writes: &mut Vec<TuneWrite>) {
    let mut start: Option<usize> = None;

    for i in 0..=now.len() {
        let differs = i < now.len() && i < was.len() && now[i] != was[i];

        if differs && start.is_none() {
            start = Some(i);
        } else if !differs && let Some(s) = start {
            writes.push(TuneWrite::new(page, s as i32, now[s..i].to_vec()));
            start = None;
        }
    }
}
