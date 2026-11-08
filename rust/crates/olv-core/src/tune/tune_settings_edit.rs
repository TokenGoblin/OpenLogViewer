//! Ported from `TuneSettingsEdit.cs`. Owned by package `w4-tune`.
//!
//! Settings edited but not yet sent.
//!
//! The counterpart of [`TuneEdit`](super::tune_edit::TuneEdit) for everything that is not a
//! table: the eight hundred-odd scalars, bit fields and names that make up most of a tune.
//! Like that one, nothing here reaches the ECU: it decides what the bytes would become, and
//! sending them is somebody else's job.
//!
//! **It works on a copy of the pages rather than on a list of changes, and that is the whole
//! design.** Bit fields share bytes: on an MS3, four unrelated options can live in one.
//! Encoding each change against the ECU's original bytes and sending them in turn means the
//! second write carries the first field's old value and silently undoes it: two settings
//! changed, one applied, no error anywhere. Editing a copy and then asking which bytes differ
//! cannot produce that, because every edit lands on the same image the next one reads.
//!
//! # The tune
//!
//! The C# holds a reference to the [`EcuTune`] and reads its pages afresh on every call, while
//! the tune is changed underneath it as writes are accepted. Here the tune is an argument to
//! each call that needs it, so a caller can hold it however it likes and change it between
//! calls with [`EcuTune::accept`].

use super::ecu_tune::{EcuTune, TuneWrite};
use super::tune_restore::runs;
use crate::fmt;
use crate::ini::tune_layout::TuneConstant;
use crate::model::IgnoreCaseMap;

/// One setting, changed.
#[derive(Clone, Debug, PartialEq)]
pub struct SettingChange {
    /// The constant's name.
    pub name: String,
    /// What the ECU holds.
    pub was: f64,
    /// What it would be changed to.
    pub now: f64,
}

/// Settings edited but not yet sent. See the module docs.
#[derive(Clone, Debug)]
pub struct TuneSettingsEdit {
    working: Vec<Vec<u8>>,
    original: IgnoreCaseMap<f64>,

    /// Each setting's value the first time this session ever touches it.
    ///
    /// [`original`](Self::original) reads the tune's pages fresh every call, which is right for
    /// its own job (showing what the ECU holds against what an edit would make it) but wrong
    /// for remembering a sentinel: on a live connection, the pages are the poll loop's own
    /// buffer, and it has moved on to the nudged value within one poll tick of the write
    /// landing. Frozen here instead, on first contact, so a value out of the firmware's
    /// declared range can still be found and written back even after it has been edited away
    /// from and the ECU's own live memory no longer holds it.
    ever_observed: IgnoreCaseMap<f64>,

    // Ordered, because the change list is read back in the order things were touched and a
    // plain dictionary does not promise that.
    order: Vec<String>,
    changed: IgnoreCaseMap<f64>,
}

impl TuneSettingsEdit {
    pub fn new(tune: &EcuTune) -> Self {
        TuneSettingsEdit {
            working: tune.pages().to_vec(),
            original: IgnoreCaseMap::new(),
            ever_observed: IgnoreCaseMap::new(),
            order: Vec::new(),
            changed: IgnoreCaseMap::new(),
        }
    }

    /// Settings changed and not yet sent, in the order they were touched.
    pub fn changes(&self) -> Vec<SettingChange> {
        self.order
            .iter()
            .filter(|k| self.changed.contains_key(k))
            .map(|k| SettingChange {
                name: k.clone(),
                was: self.original.get(k).copied().unwrap_or(f64::NAN),
                now: self.changed.get(k).copied().unwrap_or(f64::NAN),
            })
            .collect()
    }

    /// What the ECU holds for a setting, as against what it would become.
    ///
    /// Read from the controller's own pages rather than the working copy, which is the whole
    /// point of it: a caller wanting to show was-and-now needs the two to come from different
    /// places.
    pub fn original(&self, tune: &EcuTune, name: &str, element: i32) -> f64 {
        tune.value_in(tune.pages(), name, element).unwrap_or(f64::NAN)
    }

    /// The same, for a text setting.
    pub fn original_text(&self, tune: &EcuTune, name: &str) -> String {
        tune.text_in(tune.pages(), name).unwrap_or_default()
    }

    pub fn changed_count(&self) -> usize {
        self.changed.len()
    }

    pub fn has_changes(&self) -> bool {
        !self.changed.is_empty()
    }

    /// What a setting would become, or what it is where it is untouched.
    pub fn value(&self, tune: &EcuTune, name: &str, element: i32) -> f64 {
        tune.value_in(&self.working, name, element).unwrap_or(f64::NAN)
    }

    /// Text settings, which hold a name rather than a number.
    pub fn text(&self, tune: &EcuTune, name: &str) -> String {
        tune.text_in(&self.working, name).unwrap_or_default()
    }

    /// Changes a setting, or reports why it cannot be.
    ///
    /// Values are held to the range the firmware declares, which is far tighter than the
    /// storage allows: the same discipline the table editor applies, and for the same reason.
    ///
    /// The one exception is putting back exactly what the ECU already held: a live value can
    /// sit outside its own declared range (a firmware "disabled" sentinel like 255 against a
    /// declared 0-250 is a real, observed case, not a hypothetical) and the range check must
    /// not make that value unwritable. Anything else out of range is still refused; only a
    /// value this exact constant was actually read as gets the exception.
    pub fn set(&mut self, tune: &EcuTune, name: &str, value: f64, element: i32) -> bool {
        let Some(constant) = tune.constant(name) else {
            return false;
        };
        if !constant.on_controller() {
            return false;
        }
        if constant.is_text {
            return false;
        }
        if !value.is_finite() {
            return false;
        }

        // Captured before anything else touches this key, so a sentinel is remembered from the
        // one moment it is guaranteed to still be there: the first time this session lays eyes
        // on it.
        let observed_key = key(name, element);
        let ever_observed = match self.ever_observed.get(&observed_key) {
            Some(v) => *v,
            None => {
                let seen = self.original(tune, name, element);
                self.ever_observed.insert(&observed_key, seen);
                seen
            }
        };

        if constant.has_range()
            && (value < constant.low || value > constant.high)
            && (ever_observed.is_nan() || (ever_observed - value).abs() > 1e-9)
        {
            return false;
        }

        let Some(before) = tune.value_in(&self.working, name, element) else {
            return false;
        };

        if !tune.poke_into(&mut self.working, constant, element, value) {
            return false;
        }

        // What landed, not what was asked for. A byte holding tenths cannot hold 1.24, and
        // recording the request would put a number in the confirmation list that will never
        // reach the ECU; and worse, a request of 5.4 onto a byte already holding 5 moves
        // nothing at all while still counting as a change, which is the
        // lit-Send-button-that-writes-nothing that text settings go out of their way to avoid.
        let stored = tune.value_in(&self.working, name, element).unwrap_or(value);

        self.remember(&key(name, element), before, stored);
        true
    }

    /// Changes a text setting.
    pub fn set_text(&mut self, tune: &EcuTune, name: &str, value: &str) -> bool {
        let Some(constant) = tune.constant(name) else {
            return false;
        };
        if !constant.on_controller() || !constant.is_text {
            return false;
        }

        if !tune.poke_text_into(&mut self.working, constant, value) {
            return false;
        }

        // Judged by the bytes, not by comparing the string against the one that was here a
        // moment ago. Two ways that goes wrong: typing a field back to what the ECU holds
        // leaves it counted as a pending change that writes nothing (the Send button lit, then
        // "nothing has been changed" and the phantom stuck, because the reconcile that would
        // clear it never runs). And re-setting a field to the value it already has, which a
        // text box does on every focus change, drops a record whose bytes really do still
        // differ, so a genuine pending write greys the button out. Comparing images is also
        // what makes a field the controller pads with spaces come out right, where reading it
        // back gives a trimmed string that no longer says what is stored.
        //
        // Text has no number to record, so the change is noted by name alone and the count is
        // what the header reports.
        if !self.differs(tune, constant) {
            self.forget(name);
        } else {
            self.original.try_add(name, f64::NAN);
            if !self.changed.contains_key(name) {
                self.order.push(name.to_string());
            }
            self.changed.insert(name, f64::NAN);
        }

        true
    }

    /// Puts one setting back to what the ECU holds.
    pub fn revert(&mut self, tune: &EcuTune, name: &str, element: i32) {
        let key = key(name, element);
        if !self.changed.contains_key(&key) {
            return;
        }

        if let Some(constant) = tune.constant(name)
            && constant.on_controller()
        {
            // From the ECU's own bytes, not from the remembered number: a bit field's
            // neighbours may have been edited since, and they must stay as they now are.
            //
            // A text field goes back byte for byte rather than through its string, because
            // reading one gives it trimmed and writing one pads with nulls. A name the
            // controller stores padded with spaces would otherwise come back differing from
            // what is on it: the change record cleared while the bytes still say otherwise,
            // which is a write nothing admits to.
            if constant.is_text {
                tune.restore_text_into(&mut self.working, constant);
            } else if let Some(was) = tune.value_in(tune.pages(), name, element) {
                tune.poke_into(&mut self.working, constant, element, was);
            }
        }

        self.forget(&key);
    }

    /// Takes a write the controller has already accepted from somewhere else.
    ///
    /// A table is edited through [`TuneEdit`](super::tune_edit::TuneEdit), not through this,
    /// but both work on the same pages. When a table write lands, the controller and
    /// [`EcuTune`] both move and the copy held here does not, so the bytes it still holds
    /// differ from the ECU's, and [`writes`](Self::writes) reports them as settings waiting to
    /// be sent. They carry the values from before the table write, so sending them would put
    /// the table back.
    ///
    /// Called after the write is acknowledged, never before: this records what happened rather
    /// than predicting it, exactly as [`EcuTune::accept`] does.
    pub fn accept(&mut self, write: &TuneWrite) {
        if write.page < 0 || write.page as usize >= self.working.len() {
            return;
        }

        let page = &mut self.working[write.page as usize];
        if write.offset < 0 || i64::from(write.offset) + write.data.len() as i64 > page.len() as i64 {
            return;
        }

        let at = write.offset as usize;
        page[at..at + write.data.len()].copy_from_slice(&write.data);
    }

    /// Puts everything back.
    pub fn revert_all(&mut self, tune: &EcuTune) {
        for (i, working) in self.working.iter_mut().enumerate() {
            if let Some(source) = tune.pages().get(i) {
                let n = source.len().min(working.len());
                working[..n].copy_from_slice(&source[..n]);
            }
        }

        self.changed = IgnoreCaseMap::new();
        self.original = IgnoreCaseMap::new();
        self.order.clear();
    }

    /// The bytes that differ from the ECU's, gathered into runs.
    ///
    /// Computed by comparing the images rather than accumulated as edits are made, which is
    /// what makes two settings sharing a byte come out as one write carrying both. Neighbouring
    /// changed bytes are joined into a single write, since a write costs a round trip and a run
    /// of four bytes is no dearer than one.
    pub fn writes(&self, tune: &EcuTune) -> Vec<TuneWrite> {
        let mut writes = Vec::new();

        for (page, now) in self.working.iter().enumerate() {
            let was: &[u8] = tune.pages().get(page).map_or(&[], |p| p.as_slice());
            runs(page as i32, was, now, &mut writes);
        }

        writes
    }

    /// How much of the ECU this would rewrite, for saying so before it is sent.
    pub fn bytes_to_write(&self, tune: &EcuTune) -> i32 {
        self.writes(tune).iter().map(|w| w.data.len() as i32).sum()
    }

    /// Pages this would write to, which are the pages a burn would have to commit.
    pub fn pages_to_write(&self, tune: &EcuTune) -> Vec<i32> {
        let mut pages: Vec<i32> = self.writes(tune).iter().map(|w| w.page).collect();
        pages.sort_unstable();
        pages.dedup();
        pages
    }

    /// Drops the changes the ECU has taken, keeping the ones it has not.
    ///
    /// Called after sending. A send is not one write but several, and one of them failing
    /// leaves the earlier ones applied, so what is still outstanding cannot be "all of it" or
    /// "none of it". Asking which settings still differ from the controller answers it exactly,
    /// and needs no record of which writes got through.
    pub fn reconcile(&mut self, tune: &EcuTune) {
        for k in self.order.clone() {
            if !self.changed.contains_key(&k) {
                continue;
            }

            let (name, element) = split(&k);

            // A text setting has no number to compare, so it is judged by its bytes instead,
            // for the reason `set_text` gives.
            let settled = match tune.constant(name) {
                Some(text) if text.is_text => !self.differs(tune, text),
                _ => same(
                    tune.value_in(&self.working, name, element),
                    tune.value_in(tune.pages(), name, element),
                ),
            };

            if !settled {
                continue;
            }

            self.forget(&k);
        }
    }

    /// Whether a text field's bytes stand apart from the controller's.
    ///
    /// The same question [`writes`](Self::writes) asks of the whole image, narrowed to one
    /// field, and the only honest way to ask it of text, since the string a field reads back as
    /// is not what it holds.
    fn differs(&self, tune: &EcuTune, constant: &TuneConstant) -> bool {
        if constant.page < 0 || constant.page as usize >= self.working.len() {
            return false;
        }

        let now = &self.working[constant.page as usize];
        let Some(was) = tune.pages().get(constant.page as usize) else {
            // The C# would index out of range here; there is nothing to differ from.
            return false;
        };

        let at = constant.offset;
        let length = constant.columns.max(0);
        let end = i64::from(at) + i64::from(length);

        if at < 0 || end > now.len() as i64 || end > was.len() as i64 {
            return false;
        }

        let (at, end) = (at as usize, end as usize);
        now[at..end] != was[at..end]
    }

    fn remember(&mut self, key: &str, before: f64, after: f64) {
        self.original.try_add(key, before);

        // Back to where it started is not a change. Otherwise a value nudged up and down again
        // would still be counted, and the header would offer to send bytes identical to the
        // ones already there.
        let started = self.original.get(key).copied().unwrap_or(f64::NAN);
        if (started - after).abs() < 1e-12 {
            self.forget(key);
        } else {
            if !self.changed.contains_key(key) {
                self.order.push(key.to_string());
            }
            self.changed.insert(key, after);
        }
    }

    /// Removes a key from the change list, the originals and the order.
    fn forget(&mut self, key: &str) {
        self.changed.remove(key);
        self.original.remove(key);
        if let Some(at) = self.order.iter().position(|k| k == key) {
            self.order.remove(at);
        }
    }
}

fn same(a: Option<f64>, b: Option<f64>) -> bool {
    match (a, b) {
        (Some(a), Some(b)) => (a - b).abs() < 1e-12,
        (None, None) => true,
        _ => false,
    }
}

fn split(key: &str) -> (&str, i32) {
    if let Some(open) = key.find('[')
        && open > 0
        && key.ends_with(']')
        && let Some(element) = fmt::parse_i32(&key[open + 1..key.len() - 1])
    {
        return (&key[..open], element);
    }

    (key, 0)
}

fn key(name: &str, element: i32) -> String {
    if element == 0 {
        name.to_string()
    } else {
        format!("{name}[{element}]")
    }
}
