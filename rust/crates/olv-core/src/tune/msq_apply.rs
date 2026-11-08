//! Ported from `MsqApply.cs`. Owned by package `w4-tune`.
//!
//! A saved tune laid over a firmware definition: the settings as bytes, and an honest account
//! of what did not fit.
//!
//! **The account is the point.** A tune and a definition that disagree still produce a tune
//! object: every constant the file did not mention keeps the zero it started as, and a page
//! full of zeros looks exactly like a page of real settings. Sending one to a controller would
//! write those zeros. So a caller is given the shortfall rather than left to notice it, and
//! anything that can reach an ECU is expected to refuse a tune that did not load whole.

use super::ecu_tune::EcuTune;
use crate::definitions::msq_file::MsqFile;
use crate::fmt;
use crate::ini::tune_layout::{TuneConstant, TuneLayout};
use crate::model::{IgnoreCaseMap, eq_ignore_case};
use std::collections::HashMap;
use std::sync::Arc;

/// One setting in a saved tune that could not be put into the layout.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MsqComplaint {
    /// The constant's name.
    pub name: String,
    /// What went wrong, in words fit to show someone.
    pub reason: String,
}

impl MsqComplaint {
    pub fn new(name: impl Into<String>, reason: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            reason: reason.into(),
        }
    }
}

impl std::fmt::Display for MsqComplaint {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}: {}", self.name, self.reason)
    }
}

/// A saved tune laid over a firmware definition. See the module docs.
#[derive(Clone, Debug)]
pub struct MsqLoad {
    /// The settings, as pages of bytes.
    pub tune: EcuTune,
    /// How many constants were set from the file.
    pub applied: i32,
    /// Constants the firmware declares that the file never mentioned. These are the dangerous
    /// ones: each is a setting left at zero that looks like a setting.
    pub missing: Vec<MsqComplaint>,
    /// Constants the file gave a value this could not store: an option name the firmware does
    /// not offer, a number outside the range, the wrong number of cells for a table.
    pub rejected: Vec<MsqComplaint>,
    /// Names the file carries that this firmware has no constant for. Harmless in itself, and
    /// worth counting: a great many of them means the tune belongs to another firmware.
    pub unknown: Vec<String>,
}

impl MsqLoad {
    /// True when every setting the firmware declares came from the file.
    pub fn is_complete(&self) -> bool {
        self.missing.is_empty() && self.rejected.is_empty()
    }

    /// Whether this looks like a tune for some other firmware.
    ///
    /// Judged on the share of the definition that went unfilled rather than on the signature,
    /// because a signature is often close but not equal (a tune saved from revision 3.4.2
    /// opened against 3.4.3) and that case reads almost all of the file correctly. Half a
    /// definition left at zero does not.
    pub fn looks_like_another_firmware(&self) -> bool {
        let missing = self.missing.len() as i32;
        self.applied + missing > 0 && missing > (self.applied + missing) / 2
    }

    /// What to tell someone, in one line.
    pub fn summary(&self) -> String {
        if self.is_complete() {
            format!("{} settings read.", fmt::int(self.applied, "N0"))
        } else {
            format!(
                "{} settings read, {} the firmware declares were not in the file, {} could not be stored.{}",
                fmt::int(self.applied, "N0"),
                fmt::int(self.missing.len() as i32, "N0"),
                fmt::int(self.rejected.len() as i32, "N0"),
                if self.unknown.is_empty() {
                    String::new()
                } else {
                    format!(
                        " {} in the file are not in this firmware.",
                        fmt::int(self.unknown.len() as i32, "N0")
                    )
                }
            )
        }
    }
}

/// Where a name is declared twice the later one wins, which is how every other reader
/// resolves it. Spelled exactly: MS2Extra has two different settings called `MAFFlow` and
/// `mafflow`, on different pages, and merging them loses one of the two.
///
/// The result is, per name, the index in `constants` of the declaration that wins.
pub(crate) fn winners(constants: &[TuneConstant]) -> HashMap<&str, usize> {
    let mut winners = HashMap::new();
    for (i, constant) in constants.iter().enumerate() {
        if constant.on_controller() {
            winners.insert(constant.name.as_str(), i);
        }
    }
    winners
}

/// Builds the ECU's pages from a saved tune.
///
/// The file gives values in the units a person reads and this turns them back into bytes,
/// which is the same encoding a write to the controller uses; deliberately, so a tune opened
/// from a file and a tune read off an ECU are the same kind of thing and everything downstream
/// works on either.
///
/// A bit field is stored in the file as the *name* of the option chosen (`"Speed Density"`,
/// not 1) because the file has to be readable without the definition beside it. So the label is
/// looked up in the firmware's own list, and its position is the number. A label the firmware
/// does not offer is refused rather than guessed at: an option list that has shifted between
/// revisions is exactly the case where a guess stores the wrong setting and reports success.
///
/// `onto` is bytes to lay the file over, or `None` to start from nothing.
///
/// **Pass the controller's own pages when restoring a tune to it.** A definition does not
/// declare every bit it has: reserved bits, and bits a later firmware will use, belong to no
/// constant, so a file cannot carry them and starting from zero silently clears them. On a
/// Speeduino that is 72 bytes of the 3,408. Laying the file over what the ECU already holds
/// changes the settings the file names and leaves the rest exactly as they are, which is what
/// restoring a backup ought to mean.
pub fn load(layout: &TuneLayout, file: &MsqFile, onto: Option<&EcuTune>) -> MsqLoad {
    load_shared(Arc::new(layout.clone()), file, onto)
}

/// [`load`] for a layout already held in an `Arc`, which a restore has (`ecu.layout_arc()`).
pub fn load_shared(layout: Arc<TuneLayout>, file: &MsqFile, onto: Option<&EcuTune>) -> MsqLoad {
    let pages: Vec<Vec<u8>> = layout
        .pages
        .iter()
        .enumerate()
        .map(|(i, p)| match onto {
            Some(onto) if i < onto.pages().len() && onto.pages()[i].len() as i64 == i64::from(p.size) => {
                onto.pages()[i].clone()
            }
            _ => vec![0u8; p.size.max(0) as usize],
        })
        .collect();

    let mut tune = EcuTune::from_pages(Arc::clone(&layout), pages);

    let mut missing: Vec<MsqComplaint> = Vec::new();
    let mut rejected: Vec<MsqComplaint> = Vec::new();
    let mut applied = 0;

    // Twice, where a firmware states any scale in terms of a setting. Those sums are done when
    // the tune is built, and a tune being filled in from a file is empty at that moment, so a
    // scale written {0.01 * (maf_range + 1)} is worked out with the range reading nought, and
    // every value stored through it is wrong or refused outright. The first pass puts the
    // settings in, the sums are done again against them, and the second pass is the one that
    // counts.
    let passes = if tune.has_expression_scales() { 2 } else { 1 };

    // Applied in declaration order all the same, so that where two *differently* named
    // constants overlap in the controller's memory (the same firmware puts testint and testrpm
    // in one pair of bytes) the later of them is what the bytes end up holding, again matching
    // how the name resolves. A file states both and only one can be true.
    let winners = winners(&layout.constants);

    for pass in 0..passes {
        if pass > 0 {
            tune.rescale();
            missing.clear();
            rejected.clear();
            applied = 0;
        }

        // The pages move out of the tune for the pass, so they can be changed while the tune's
        // constants are consulted. Nothing in a pass reads the tune's own pages.
        let mut pages = tune.take_pages();

        for (i, constant) in layout.constants.iter().enumerate() {
            if !constant.on_controller() {
                continue;
            }
            if winners.get(constant.name.as_str()) != Some(&i) {
                continue;
            }

            let Some(written) = file.value(&constant.name) else {
                missing.push(MsqComplaint::new(constant.name.clone(), "not in the file"));
                continue;
            };

            // Through the tune rather than the layout: a scale the firmware wrote as an
            // expression is worked out once the values exist, and encoding with the declared
            // fallback while decoding with the worked-out one puts the two out by whatever the
            // expression came to. The tune's copy is the one everything else reads through.
            let used = tune.constant(&constant.name).unwrap_or(constant);

            match store(&tune, &mut pages, used, written) {
                Some(complaint) => rejected.push(complaint),
                None => applied += 1,
            }
        }

        tune.put_pages(pages);
    }

    // Names in the file with nothing to put them in. Counted rather than complained about one
    // by one: on a tune from a neighbouring revision there are a handful, and on a tune from
    // another firmware there are hundreds, and the difference is the useful part.
    let mut declared: IgnoreCaseMap<()> = IgnoreCaseMap::new();
    for constant in &layout.constants {
        declared.insert(&constant.name, ());
    }

    let unknown: Vec<String> = file
        .values
        .keys()
        .filter(|name| !declared.contains_key(name))
        .map(str::to_string)
        .collect();

    MsqLoad {
        tune,
        applied,
        missing,
        rejected,
        unknown,
    }
}

/// Puts one written value into the pages, or says why it would not go.
fn store(tune: &EcuTune, pages: &mut [Vec<u8>], constant: &TuneConstant, written: &str) -> Option<MsqComplaint> {
    if constant.is_text {
        // Left exactly as they are when the field already says this. Reading a text field
        // trims its padding and writing one pads with nulls, so a controller that pads with
        // anything else (spaces, or the newlines a rusEFI leaves after a Lua script) comes out
        // differing from itself in bytes while agreeing in every value. That phantom
        // difference is what a restore plans a write for, so restoring a tune to the ECU it
        // was read from would send bytes and report that nothing had changed.
        if tune.text_in(pages, &constant.name).as_deref() == Some(unquote(written)) {
            return None;
        }

        return if tune.poke_text_into(pages, constant, unquote(written)) {
            None
        } else {
            Some(MsqComplaint::new(
                constant.name.clone(),
                "the name does not fit the field, or is not ASCII",
            ))
        };
    }

    let cells = (constant.columns * constant.rows).max(1);

    if cells > 1 {
        let tokens: Vec<&str> = written.split_whitespace().collect();

        if tokens.len() as i64 != i64::from(cells) {
            return Some(MsqComplaint::new(
                constant.name.clone(),
                format!(
                    "the file holds {} values where the firmware wants {}",
                    tokens.len(),
                    cells
                ),
            ));
        }

        // All of it or none of it.
        //
        // Written cell by cell, a value that will not fit part way through leaves every cell
        // before it already changed while the constant is reported as rejected, and rejected
        // means "not stored", which is exactly what a restore trusts when it decides those
        // bytes need no write. The bytes disagree, so the restore sends them: half the file's
        // table and half the controller's, to a running engine, under a plan that said the
        // setting would be left alone.
        let before = keep(pages, constant);

        // Row-major, which is how the file writes a grid and how the controller stores one.
        for (i, token) in tokens.iter().enumerate() {
            let Some(value) = number(token) else {
                put_back(pages, constant, &before);
                return Some(MsqComplaint::new(
                    constant.name.clone(),
                    format!("\"{token}\" is not a number"),
                ));
            };

            if !tune.poke_into(pages, constant, i as i32, value) {
                put_back(pages, constant, &before);
                return Some(MsqComplaint::new(
                    constant.name.clone(),
                    format!("{} will not fit at cell {}", fmt::f64(value, ""), i),
                ));
            }
        }

        return None;
    }

    let Some(single) = value(constant, written) else {
        return Some(MsqComplaint::new(
            constant.name.clone(),
            format!("the firmware does not offer {}", written.trim()),
        ));
    };

    // A single value is one poke, which either happens or does not, so there is nothing
    // part-written to undo.
    if tune.poke_into(pages, constant, 0, single) {
        None
    } else {
        Some(MsqComplaint::new(
            constant.name.clone(),
            format!("{} is outside what this setting can hold", fmt::f64(single, "")),
        ))
    }
}

/// The bytes a constant occupies now, so a failed write can undo itself.
fn keep(pages: &[Vec<u8>], constant: &TuneConstant) -> Vec<u8> {
    if constant.page < 0 || constant.page as usize >= pages.len() {
        return Vec::new();
    }

    let page = &pages[constant.page as usize];
    let at = constant.offset;
    let size = i64::from(constant.size()).min(page.len() as i64 - i64::from(at));

    if at < 0 || size <= 0 {
        Vec::new()
    } else {
        page[at as usize..(i64::from(at) + size) as usize].to_vec()
    }
}

/// Puts them back, exactly as they were.
fn put_back(pages: &mut [Vec<u8>], constant: &TuneConstant, before: &[u8]) {
    if before.is_empty() || constant.page < 0 || constant.page as usize >= pages.len() {
        return;
    }

    let at = constant.offset as usize;
    pages[constant.page as usize][at..at + before.len()].copy_from_slice(before);
}

/// The number behind a written value: a label's position for a bit field, the number itself
/// otherwise.
fn value(constant: &TuneConstant, written: &str) -> Option<f64> {
    let text = written.trim();

    if constant.has_options() && text.starts_with('"') {
        let label = unquote(text);

        for (i, option) in constant.options.iter().enumerate() {
            if eq_ignore_case(option, label) {
                return Some(i as f64);
            }
        }

        // Not offered. Refused rather than stored as a number, even when the label happens to
        // read as one: a firmware whose choices are spelled "1", "2", "4", "6", "8" numbers
        // them 0 to 4, and taking the label at face value would set eight cylinders to mean
        // four.
        return None;
    }

    number(text)
}

fn number(text: &str) -> Option<f64> {
    fmt::parse_f64_inv(text.trim()).filter(|v| v.is_finite())
}

fn unquote(text: &str) -> &str {
    let trimmed = text.trim();

    if trimmed.len() >= 2 && trimmed.starts_with('"') && trimmed.ends_with('"') {
        &trimmed[1..trimmed.len() - 1]
    } else {
        trimmed
    }
}
