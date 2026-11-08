//! Ported from `TuneCompare.cs`. Owned by package `w4-tune`.
//!
//! What two tunes disagree about, setting by setting.
//!
//! The question a tuner asks constantly and no log can answer: *is the ECU running what I think
//! it is?* A saved file and a controller drift apart the moment anybody changes something
//! without saving, and the difference is invisible: both are pages of plausible numbers.
//! Naming the settings that differ turns "I think this is the right tune" into knowing.
//!
//! Compared through the values rather than the bytes, deliberately. Two tunes can hold
//! different bytes and mean the same thing, because bits no constant declares differ freely
//! and a file cannot carry them; showing those as differences would bury the handful that
//! matter under scores that do not.

use super::ecu_tune::EcuTune;
use super::msq_apply::winners;
use crate::fmt;
use crate::ini::tune_layout::TuneConstant;

/// One setting that two tunes disagree about.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneDifference {
    /// The constant's name.
    pub name: String,
    /// Its declaration, for units and for naming its options.
    pub constant: TuneConstant,
    /// How many cells differ, which is 1 for anything that is not a table. Worth having on its
    /// own: "3 of 256 cells" and "all 256" are different situations and the first values alone
    /// do not tell them apart.
    pub cells: i32,
    /// The first tune's value at the first differing cell.
    pub mine: Option<f64>,
    /// The second tune's value there.
    pub theirs: Option<f64>,
    /// What each side holds where the setting is text rather than a number.
    ///
    /// Kept, because throwing it away left the one kind of setting whose value a person
    /// recognises on sight reading "1 of 32 cells differ, first — against —". A name is a name;
    /// it should say which name.
    pub mine_text: Option<String>,
    pub theirs_text: Option<String>,
}

impl TuneDifference {
    pub fn new(
        name: impl Into<String>,
        constant: TuneConstant,
        cells: i32,
        mine: Option<f64>,
        theirs: Option<f64>,
    ) -> Self {
        TuneDifference {
            name: name.into(),
            constant,
            cells,
            mine,
            theirs,
            mine_text: None,
            theirs_text: None,
        }
    }

    /// True for a table or a set of breakpoints, never for text, whose width is how many
    /// characters fit rather than how many values it holds.
    pub fn is_array(&self) -> bool {
        !self.constant.is_text && self.constant.columns * self.constant.rows > 1
    }

    /// What the first tune holds, named where the firmware names it.
    pub fn mine_shown(&self) -> String {
        match &self.mine_text {
            Some(t) => quoted(t),
            None => self.shown(self.mine),
        }
    }

    /// What the second holds.
    pub fn theirs_shown(&self) -> String {
        match &self.theirs_text {
            Some(t) => quoted(t),
            None => self.shown(self.theirs),
        }
    }

    /// One line describing the disagreement.
    pub fn summary(&self) -> String {
        if self.is_array() {
            format!(
                "{}: {} of {} cells differ, first {} against {}",
                self.name,
                fmt::int(self.cells, "N0"),
                fmt::int(self.constant.columns * self.constant.rows, "N0"),
                self.mine_shown(),
                self.theirs_shown()
            )
        } else {
            format!("{}: {} against {}", self.name, self.mine_shown(), self.theirs_shown())
        }
    }

    fn shown(&self, value: Option<f64>) -> String {
        let Some(v) = value else {
            return "\u{2014}".to_string();
        };

        let text = if self.constant.has_options() {
            self.constant.option_name(v)
        } else {
            fmt::f64(v, "0.####")
        };

        if !self.constant.units.is_empty() && !self.constant.has_options() {
            format!("{} {}", text, self.constant.units)
        } else {
            text
        }
    }
}

fn quoted(text: &str) -> String {
    if text.is_empty() {
        "(blank)".to_string()
    } else {
        format!("\"{text}\"")
    }
}

/// Every setting the two hold differently, in declaration order.
///
/// `mine` is usually the file and `theirs` the controller.
pub fn compare(mine: &EcuTune, theirs: &EcuTune) -> Vec<TuneDifference> {
    let mut differences = Vec::new();

    // The later declaration of a name is the one everything else resolves to, and two names
    // differing only in case are two settings.
    let winners = winners(&mine.layout().constants);

    for (i, constant) in mine.layout().constants.iter().enumerate() {
        if !constant.on_controller() {
            continue;
        }
        if winners.get(constant.name.as_str()) != Some(&i) {
            continue;
        }

        if constant.is_text {
            let a = mine.text_in(mine.pages(), &constant.name).unwrap_or_default();
            let b = theirs.text_in(theirs.pages(), &constant.name).unwrap_or_default();

            if a != b {
                let mut difference = TuneDifference::new(constant.name.clone(), constant.clone(), 1, None, None);
                difference.mine_text = Some(a);
                difference.theirs_text = Some(b);
                differences.push(difference);
            }

            continue;
        }

        let cells = (constant.columns * constant.rows).max(1);
        let mut differing = 0;
        let (mut first_mine, mut first_theirs) = (None, None);

        for i in 0..cells {
            let a = mine.value_in(mine.pages(), &constant.name, i);
            let b = theirs.value_in(theirs.pages(), &constant.name, i);

            if same(a, b) {
                continue;
            }

            if differing == 0 {
                (first_mine, first_theirs) = (a, b);
            }
            differing += 1;
        }

        if differing > 0 {
            differences.push(TuneDifference::new(
                constant.name.clone(),
                constant.clone(),
                differing,
                first_mine,
                first_theirs,
            ));
        }
    }

    differences
}

/// Whether two readings of the same setting mean the same thing.
///
/// Compared to within half a step of what the setting can actually store, because both sides
/// came from bytes through the same arithmetic and the only difference that can exist is one
/// the storage could hold. A plain equality on doubles would call two identical bytes
/// different.
fn same(a: Option<f64>, b: Option<f64>) -> bool {
    match (a, b) {
        (Some(a), Some(b)) => (a - b).abs() < 1e-9,
        (None, None) => true,
        _ => false,
    }
}
