//! Ported from `EcuTune.cs`. Owned by package `w4-tune`.
//!
//! The tune as it exists in the ECU right now, read out over the wire.
//!
//! Better than a file for the job it does. A saved `.msq` is whatever was last written to
//! disk, which on a bench that has been poked at is not necessarily what the controller is
//! running; and rusEFI's own saved tunes carry no tables at all, so for that firmware there is
//! no file to fall back on. Reading the pages settles it.
//!
//! Nothing here reaches the ECU. This decodes the pages, encodes edited values back into
//! bytes, and records a write in the copy held here once one has been sent and read back.
//! Sending is the connection's job, and keeping the two apart is what lets an edit be built
//! and checked without anything having left the machine yet.
//!
//! # The connection
//!
//! The C# `EcuTune.Read` takes an `EcuConnection`, which lives in `olv-live`, and `olv-core`
//! cannot depend on it. [`TunePageIo`] is the small seam: the three things a tune asks of a
//! connection, with the connection's own signatures, so `olv-live` implements it by delegating.
//!
//! # Pages
//!
//! In the C#, `Pages` hands out the live byte arrays and `PokeInto(tune.Pages, ...)` writes
//! straight into the tune. Rust cannot hand out a borrow of the pages and also call `&self`
//! methods with it, so the page-taking methods ([`EcuTune::poke_into`] and friends) take the
//! page images as an argument, and [`EcuTune::pages_mut`] is how a caller reaches the tune's
//! own. [`EcuTune::poke_into_own`] is the one-call form for the common case.

use crate::Error;
use crate::ini::dialog_condition;
use crate::ini::msq_ini::RealtimeType;
use crate::ini::table_editor_reader::TableDefinition;
use crate::ini::tune_layout::{TuneConstant, TuneLayout, TunePage};
use crate::model::tune_axes::{TuneAxis, TuneTable};
use crate::model::{Array2, IgnoreCaseMap, eq_ignore_case};
use regex::Regex;
use std::cell::RefCell;
use std::collections::HashMap;
use std::sync::{Arc, LazyLock};

/// Bytes destined for a place in the ECU's memory.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TuneWrite {
    pub page: i32,
    pub offset: i32,
    pub data: Vec<u8>,
}

impl TuneWrite {
    pub fn new(page: i32, offset: i32, data: Vec<u8>) -> Self {
        Self { page, offset, data }
    }
}

/// What a tune asks of an ECU connection: read a page, write into one, burn one.
///
/// The signatures are those of `olv_live::connection::EcuConnection`, so the implementation
/// there is a delegation. Only [`read_tune_page`](Self::read_tune_page) is used by this
/// module (the C# `EcuTune.Read`); the other two are here so everything that sends a tune has
/// one trait to ask for.
pub trait TunePageIo {
    /// What a call can fail with.
    type Error: From<Error>;

    /// The C# `new EcuProtocolException(message)`: the connection's own protocol error, so a
    /// caller that tells a protocol failure from anything else still can.
    fn protocol_error(message: String) -> Self::Error;

    /// Reads one page of settings. `progress` is told the bytes read so far.
    fn read_tune_page(
        &self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        progress: Option<&mut dyn FnMut(i32)>,
    ) -> Result<Vec<u8>, Self::Error>;

    /// Writes bytes into a page and reads them back to prove it took. Does not burn.
    fn write_tune_page(
        &self,
        page: &TunePage,
        blocking_factor: i32,
        little_endian: bool,
        offset: i32,
        data: &[u8],
        inter_write_delay: i32,
    ) -> Result<(), Self::Error>;

    /// Commits a page to flash.
    fn burn_page(&self, page: &TunePage, little_endian: bool, after_burn_delay: i32) -> Result<(), Self::Error>;
}

/// The tune as it exists in the ECU right now. See the module docs.
#[derive(Clone, Debug)]
pub struct EcuTune {
    layout: Arc<TuneLayout>,
    pages: Vec<Vec<u8>>,
    little: bool,
    /// `OrdinalIgnoreCase` view: later definitions win.
    by_name: IgnoreCaseMap<Arc<TuneConstant>>,
    /// Ordinal view: later definitions win, names in the order first declared.
    exact: HashMap<String, Arc<TuneConstant>>,
    exact_order: Vec<String>,
}

static WORD: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"[A-Za-z_]\w*").expect("a valid pattern"));

impl EcuTune {
    fn new(layout: Arc<TuneLayout>, pages: Vec<Vec<u8>>) -> Self {
        let little = layout.little_endian;
        let mut tune = EcuTune {
            layout,
            pages,
            little,
            by_name: IgnoreCaseMap::new(),
            exact: HashMap::new(),
            exact_order: Vec::new(),
        };

        tune.reset_constants();
        tune.resolve();
        tune
    }

    /// Puts every constant back to the definition's own, later declarations winning.
    fn reset_constants(&mut self) {
        let layout = Arc::clone(&self.layout);
        self.by_name = IgnoreCaseMap::new();
        self.exact = HashMap::new();
        self.exact_order = Vec::new();

        for constant in &layout.constants {
            let shared = Arc::new(constant.clone());
            self.by_name.insert(&constant.name, Arc::clone(&shared));

            if self.exact.insert(constant.name.clone(), shared).is_none() {
                self.exact_order.push(constant.name.clone());
            }
        }
    }

    /// Works the expression scales out again, against the values as they now stand.
    ///
    /// Needed because the sum is done when the tune is built, and something that fills a tune
    /// in (laying a saved file over an empty set of pages) has by definition not filled it in
    /// yet at that moment. The setting a scale is written in terms of reads as nought, so the
    /// scale comes out wrong and every value stored through it with it.
    pub fn rescale(&mut self) {
        // From the definition's own constants rather than from the resolved copies, so a
        // second sum starts where the first one did.
        self.reset_constants();
        self.resolve();
    }

    /// True when this firmware states any scale as an expression.
    pub fn has_expression_scales(&self) -> bool {
        self.layout
            .constants
            .iter()
            .any(|c| !c.scale_expression.is_empty() || !c.transform_expression.is_empty())
    }

    /// Works out the scales a firmware wrote as expressions rather than numbers.
    ///
    /// A scale sometimes depends on a setting. MS2's MAF curve is scaled `{0.01 * (maf_range +
    /// 1)}` grammes a second, because the same 128 bytes have to cover a 650 g/s sensor and a
    /// 2,600 g/s one, and which it is is a setting three pages away. That cannot be worked out
    /// from the definition alone, so the layout keeps the text and this does the sum once the
    /// values exist to do it from. Left alone, the fallback is a scale of 1, which is out by a
    /// factor of the range and looks like an ordinary number the whole way.
    ///
    /// Resolved against constants whose own scale is a plain number, which settles the
    /// circularity rather than iterating at it: an expression naming something that is itself
    /// unresolved keeps the fallback. Every case seen rests on a bit field, which has no scale
    /// at all.
    ///
    /// Done once, when the tune is built. The scale is a property of the tune as it was read,
    /// so changing the setting behind one does not rescale a table under the person editing it;
    /// reading the tune again does.
    fn resolve(&mut self) {
        let pending: Vec<Arc<TuneConstant>> = self
            .exact_order
            .iter()
            .filter_map(|name| self.exact.get(name))
            .filter(|c| !c.scale_expression.is_empty() || !c.transform_expression.is_empty())
            .cloned()
            .collect();

        if pending.is_empty() {
            return;
        }

        // Guards against a derived value defined in terms of itself, directly or round a
        // ring. A definition can say anything; this has to survive it.
        let following: RefCell<Vec<String>> = RefCell::new(Vec::new());

        let mut resolved_all: Vec<(Arc<TuneConstant>, Arc<TuneConstant>)> = Vec::new();

        for constant in &pending {
            // An expression naming something that shares these very bytes is not a scale at
            // all: it is a second way of reading one setting. MS2 gives its injector test an
            // "approximate equivalent RPM" whose translate is {(120000/testint) -
            // (testint/0.128)}, over the same two bytes as testint, so that the pair displays
            // as a period and as a speed. Working that out reads correctly and then writes
            // nonsense: storing a value through it moves the bytes, which moves the constant
            // the translate was worked out from, which moves the translate. Left as declared.
            let expression = format!("{}{}", constant.scale_expression, constant.transform_expression);
            if self.overlapping(constant, &expression) {
                continue;
            }

            let lookup = |name: &str| self.plain(name, &following);
            let scale = sum(&constant.scale_expression, &lookup, constant.scale);
            let transform = sum(&constant.transform_expression, &lookup, constant.transform);

            if scale == constant.scale && transform == constant.transform {
                continue;
            }

            let mut resolved = (**constant).clone();
            resolved.scale = scale;
            resolved.transform = transform;
            resolved_all.push((Arc::clone(constant), Arc::new(resolved)));
        }

        // Applied after the sums are done. The C# swaps each in as it goes, but a resolved
        // copy keeps the expression text, so `plain` answers NaN for it either way and the
        // later sums cannot tell the difference.
        for (original, resolved) in resolved_all {
            let name = original.name.clone();

            if self.exact.get(&name).is_some_and(|c| Arc::ptr_eq(c, &original)) {
                self.exact.insert(name.clone(), Arc::clone(&resolved));
            }

            if self.by_name.get(&name).is_some_and(|c| Arc::ptr_eq(c, &original)) {
                self.by_name.insert(&name, resolved);
            }
        }
    }

    /// A setting's value for an expression to use, or NaN when it cannot be had.
    fn plain(&self, name: &str, following: &RefCell<Vec<String>>) -> f64 {
        if let Some(constant) = self.find(name) {
            if !constant.scale_expression.is_empty() || !constant.transform_expression.is_empty() {
                return f64::NAN;
            }

            return self.value(constant, 0).unwrap_or(f64::NAN);
        }

        // Not a setting. It may still be something the firmware works out for itself: a
        // Speeduino scales every load axis by {fuelLoadRes}, which is a line in
        // [OutputChannels] reading { ((algorithm == 0) || (algorithm == 2)) ? 2.000 : 0.500 }.
        //
        // Without this the expression cannot be evaluated, the scale falls back to the
        // declared 1, and the fuel, AFR, VVT and dwell axes are all read at half, on the way
        // in as much as out, so a tune saved by TunerStudio and restored through here would
        // have doubled every one of them on the controller.
        let Some(expression) = self.layout.derived.get(name) else {
            return f64::NAN;
        };

        if following.borrow().iter().any(|f| eq_ignore_case(f, name)) {
            return f64::NAN;
        }
        following.borrow_mut().push(name.to_string());

        let result = dialog_condition::number(expression.trim_matches(['{', '}', ' ']), |n| self.plain(n, following));

        let mut list = following.borrow_mut();
        if let Some(at) = list.iter().position(|f| eq_ignore_case(f, name)) {
            list.remove(at);
        }

        result
    }

    /// Whether an expression names a constant occupying any of the same bytes.
    ///
    /// Matched on words rather than parsed, which is enough: a name that appears in the text
    /// and turns out to overlap is exactly the case being avoided, and one that appears
    /// without overlapping costs nothing to check.
    fn overlapping(&self, constant: &Arc<TuneConstant>, expression: &str) -> bool {
        for word in WORD.find_iter(expression) {
            let Some(other) = self.find_arc(word.as_str()) else {
                continue;
            };
            if other.page != constant.page || Arc::ptr_eq(other, constant) {
                continue;
            }

            if other.offset < constant.offset + constant.size() && constant.offset < other.offset + other.size() {
                return true;
            }
        }

        false
    }

    /// The layout this tune was built against.
    pub fn layout(&self) -> &TuneLayout {
        &self.layout
    }

    /// The layout, shared.
    pub fn layout_arc(&self) -> &Arc<TuneLayout> {
        &self.layout
    }

    /// The raw page images, in page order.
    pub fn pages(&self) -> &[Vec<u8>] {
        &self.pages
    }

    /// The page images, to change in place: the C# hands out the live arrays.
    pub fn pages_mut(&mut self) -> &mut [Vec<u8>] {
        &mut self.pages
    }

    /// Moves the page images out, leaving none, so a caller can change them while still
    /// calling this tune's `&self` methods. Put back with [`put_pages`](Self::put_pages).
    pub(crate) fn take_pages(&mut self) -> Vec<Vec<u8>> {
        std::mem::take(&mut self.pages)
    }

    pub(crate) fn put_pages(&mut self, pages: Vec<Vec<u8>>) {
        self.pages = pages;
    }

    /// Reads every page from a connected ECU.
    ///
    /// `progress` is reported in bytes, because this is not instant: rusEFI's settings are
    /// 22,960 bytes across 23 reads.
    pub fn read<C: TunePageIo + ?Sized>(
        connection: &C,
        layout: Arc<TuneLayout>,
        mut progress: Option<&mut dyn FnMut(i32, i32)>,
    ) -> Result<EcuTune, C::Error> {
        if layout.pages.is_empty() {
            return Err(C::protocol_error(
                "This firmware definition declares no settings pages.".to_string(),
            ));
        }

        let mut images = Vec::with_capacity(layout.pages.len());
        let mut done = 0;
        let total = layout.total_size();

        for page in &layout.pages {
            let image = {
                let progress = &mut progress;
                let mut report = |read: i32| {
                    if let Some(p) = progress.as_mut() {
                        p(done + read, total);
                    }
                };
                connection.read_tune_page(page, layout.blocking_factor, layout.little_endian, Some(&mut report))?
            };
            images.push(image);

            done += page.size;
            if let Some(p) = progress.as_mut() {
                p(done, total);
            }
        }

        Ok(EcuTune::new(layout, images))
    }

    /// Builds one from page images already in hand, for a test or a saved capture.
    pub fn from_pages(layout: impl Into<Arc<TuneLayout>>, pages: Vec<Vec<u8>>) -> Self {
        EcuTune::new(layout.into(), pages)
    }

    /// The constant of that name: the one spelled exactly so, or failing that one spelled the
    /// same but cased differently.
    ///
    /// **Exact first, and it matters.** MS2Extra declares two different settings whose names
    /// differ only in case: `MAFFlow` is a twelve-point flow curve on page 4 and `mafflow` a
    /// sixty-four-point one on page 6, and its own saved tunes store both. Matching without
    /// regard to case merges them, so one of the two curves cannot be read at all and editing
    /// it would write to the other's bytes on another page entirely, which nothing downstream
    /// would catch.
    ///
    /// The loose match is kept as a fallback because the rest of an INI is not careful about
    /// case: a dialog or a table editor may spell a constant differently from its declaration,
    /// and those have always resolved.
    fn find(&self, name: &str) -> Option<&TuneConstant> {
        self.find_arc(name).map(|c| &**c)
    }

    fn find_arc(&self, name: &str) -> Option<&Arc<TuneConstant>> {
        self.exact.get(name).or_else(|| self.by_name.get(name))
    }

    /// Every scalar and bit field, by name: what an expression resolves against.
    pub fn scalars(&self) -> IgnoreCaseMap<f64> {
        let mut values = IgnoreCaseMap::new();

        for constant in &self.layout.constants {
            if constant.is_array() {
                continue;
            }
            if let Some(value) = self.value(constant, 0)
                && !value.is_nan()
            {
                values.insert(&constant.name, value);
            }
        }

        values
    }

    /// One setting by name, or NaN when this firmware has no such thing.
    pub fn scalar(&self, name: &str) -> f64 {
        match self.find(name) {
            Some(constant) if !constant.is_array() => self.value(constant, 0).unwrap_or(f64::NAN),
            _ => f64::NAN,
        }
    }

    /// An array's values in declaration order, or `None` when there is no such array.
    pub fn array(&self, name: &str) -> Option<Vec<f64>> {
        let constant = self.find(name)?;

        let count = (constant.columns * constant.rows).max(0);
        let mut values = Vec::with_capacity(count as usize);

        for i in 0..count {
            values.push(self.value(constant, i)?);
        }

        Some(values)
    }

    /// A table with its axes, ready for the heat view and VE Calibration.
    ///
    /// Rows and columns follow the INI's own `[a x b]`: the first dimension runs along the X
    /// axis. Getting this the wrong way round transposes the table, which for a square one is
    /// silent.
    pub fn table(&self, name: &str, values_constant: &str, x_constant: &str, y_constant: &str) -> Option<TuneTable> {
        let values = self.find(values_constant)?;

        let cells = self.array(values_constant)?;
        let x = self.array(x_constant)?;
        let y = self.array(y_constant)?;

        if x.len() as i64 != i64::from(values.columns) || y.len() as i64 != i64::from(values.rows) {
            return None;
        }

        let columns = values.columns as usize;
        let rows = values.rows as usize;
        let mut grid: Array2<f64> = Array2::new(columns, rows);
        for row in 0..rows {
            for column in 0..columns {
                grid[(column, row)] = cells[row * columns + column];
            }
        }

        let xc = self.find(x_constant);
        let yc = self.find(y_constant);

        Some(TuneTable {
            name: name.to_string(),
            x: TuneAxis {
                constant: x_constant.to_string(),
                units: xc.map(|c| c.units.clone()).unwrap_or_default(),
                breakpoints: x,
            },
            y: TuneAxis {
                constant: y_constant.to_string(),
                units: yc.map(|c| c.units.clone()).unwrap_or_default(),
                breakpoints: y,
            },
            values: grid,
            units: values.units.clone(),
        })
    }

    /// Every table the firmware offers that this tune can actually produce.
    ///
    /// Definitions that name a constant the layout does not have, or whose axes do not match
    /// the shape of the values, are left out rather than guessed at: a table assembled from
    /// mismatched parts is not a near miss.
    pub fn tables(&self, definitions: &[TableDefinition]) -> Vec<TuneTable> {
        definitions
            .iter()
            .filter_map(|d| self.table(&d.title, &d.values, &d.x_bins, &d.y_bins))
            .collect()
    }

    /// Records a write that the ECU has taken.
    ///
    /// Without this the copy held here still says what the controller said when it was read,
    /// and every table drawn from it would show the old values while the engine ran the new
    /// ones. Worse, the next edit would be built on stale bytes and would quietly undo this one.
    ///
    /// Called after the write has been acknowledged and read back, never before: this is a
    /// record of what happened, not a prediction of it.
    pub fn accept(&mut self, write: &TuneWrite) {
        if write.page < 0 || write.page as usize >= self.pages.len() {
            return;
        }

        let page = &mut self.pages[write.page as usize];
        if write.offset < 0 || i64::from(write.offset) + write.data.len() as i64 > page.len() as i64 {
            return;
        }

        let at = write.offset as usize;
        page[at..at + write.data.len()].copy_from_slice(&write.data);

        // A scale stated as an expression is worked out from the settings, and this is the
        // moment a setting moved. Left alone, a firmware writing {0.01 * (maf_range + 1)}
        // keeps the scale it had when the tune was first read: move maf_range from 0 to 3 and
        // the curve goes on dividing by 0.01 where the ECU now means 0.04, so it shows a
        // quarter of the truth; and worse, the next cell edited is *encoded* through the stale
        // scale and sent to a running engine.
        //
        // Here rather than after each kind of write because there are seven places a write is
        // accepted, across settings, tables, curves and a restore. Six of them remembering is
        // the shape of defect this codebase has produced three times already.
        if self.has_expression_scales() {
            self.rescale();
        }
    }

    /// Turns values back into the bytes an array occupies, ready to be written.
    ///
    /// The inverse of reading, and it has to round rather than truncate: a VE cell of 84.7 at
    /// a scale of 0.1 is 847, and truncating the 846.9999 that floating point actually
    /// produces would quietly drop a tenth off every cell in the table.
    ///
    /// Returns `None` when this firmware has no such array, when the count is wrong, or when a
    /// value will not fit the type: all of which would otherwise write something plausible
    /// into the wrong place.
    pub fn encode_array(&self, name: &str, values: &[f64]) -> Option<TuneWrite> {
        let constant = self.find(name)?;
        if constant.is_bit_field() {
            return None;
        }
        if values.len() as i64 != i64::from(constant.columns) * i64::from(constant.rows) {
            return None;
        }

        let mut data = vec![0u8; constant.size().max(0) as usize];
        let element = constant.element_size() as usize;

        for (i, value) in values.iter().enumerate() {
            let scaled = (value / constant.scale) - constant.transform;
            if scaled.is_nan() || scaled.is_infinite() {
                return None;
            }

            let at = i * element;
            if !try_write(&mut data[at..], constant.type_, scaled, self.little) {
                return None;
            }
        }

        Some(TuneWrite::new(constant.page, constant.offset, data))
    }

    /// A table's cells as bytes, in the row-major order the page holds them.
    pub fn encode_table(&self, values_constant: &str, cells: &Array2<f64>) -> Option<TuneWrite> {
        let constant = self.find(values_constant)?;

        let columns = cells.get_length(0);
        let rows = cells.get_length(1);

        if columns as i64 != i64::from(constant.columns) || rows as i64 != i64::from(constant.rows) {
            return None;
        }

        let mut flat = vec![0.0; columns * rows];
        for row in 0..rows {
            for column in 0..columns {
                flat[row * columns + column] = cells[(column, row)];
            }
        }

        self.encode_array(values_constant, &flat)
    }

    /// The constant by that name, or `None` where this firmware has none.
    pub fn constant(&self, name: &str) -> Option<&TuneConstant> {
        self.find(name)
    }

    /// One setting's value, read out of the given page images rather than this tune's own.
    ///
    /// So that an editor working on a copy can read back what it has changed. The decoding is
    /// identical; only where the bytes come from differs.
    pub fn value_in(&self, pages: &[Vec<u8>], name: &str, element: i32) -> Option<f64> {
        self.find(name).and_then(|c| self.value_from(pages, c, element))
    }

    /// A text setting, trimmed of the padding the ECU stores it with.
    pub fn text_in(&self, pages: &[Vec<u8>], name: &str) -> Option<String> {
        let constant = self.find(name)?;
        if !constant.is_text {
            return None;
        }
        if constant.page < 0 || constant.page as usize >= pages.len() {
            return None;
        }

        let page = &pages[constant.page as usize];
        let at = constant.offset;
        let length = constant.columns.max(0);

        if at < 0 || i64::from(at) + i64::from(length) > page.len() as i64 {
            return None;
        }

        // A fixed-width field, padded with nulls or spaces. Stops at the first null, which is
        // where the firmware stops reading it too.
        let mut text = String::with_capacity(length as usize);

        for i in 0..length {
            let b = page[(at + i) as usize];
            if b == 0 {
                break;
            }

            text.push(b as char);
        }

        Some(text.trim_end().to_string())
    }

    /// Writes one setting into the given page images.
    ///
    /// **A bit field is read, modified and written back.** Several settings share a byte (an
    /// MS3 puts four unrelated options in one), so writing the field's value alone would zero
    /// its neighbours. Those neighbours are other people's settings and the damage is silent:
    /// the ECU takes the byte, the read-back matches what was sent, and two options nobody
    /// touched have changed.
    pub fn poke_into(&self, pages: &mut [Vec<u8>], constant: &TuneConstant, element: i32, value: f64) -> bool {
        if constant.page < 0 || constant.page as usize >= pages.len() {
            return false;
        }

        // Inside this constant, not merely inside the page. Without it an index past the end
        // walks into whatever setting is declared next, which is somebody else's and is
        // overwritten in silence.
        if element < 0 || i64::from(element) >= i64::from((constant.columns * constant.rows).max(1)) {
            return false;
        }

        let page = &mut pages[constant.page as usize];
        let at = i64::from(constant.offset) + i64::from(element) * i64::from(constant.element_size());

        if at < 0 || at + i64::from(constant.element_size()) > page.len() as i64 {
            return false;
        }

        let bytes = &mut page[at as usize..];

        if !constant.is_bit_field() {
            let scaled = if constant.scale != 0.0 {
                (value / constant.scale) - constant.transform
            } else {
                value
            };
            return try_write(bytes, constant.type_, scaled, self.little);
        }

        let width = constant.bit_high - constant.bit_low + 1;
        let limit: i64 = 1i64.wrapping_shl(width as u32).wrapping_sub(1);
        let wanted = value.round_ties_even() as i64;

        // Refused rather than masked. Masking stores 3 for a 7 and reports success, so the
        // change list says one thing and the bytes another; and a bit field carries no
        // declared range, so nothing upstream would have caught it either.
        if wanted < 0 || wanted > limit {
            return false;
        }

        // The storage unit as it stands, so everything but this field survives.
        let current = raw(bytes, constant.type_, self.little).unwrap_or(0.0) as i64;
        let mask = limit.wrapping_shl(constant.bit_low as u32);

        let combined = (current & !mask) | wanted.wrapping_shl(constant.bit_low as u32);
        try_write(bytes, constant.type_, combined as f64, self.little)
    }

    /// [`poke_into`](Self::poke_into) on this tune's own pages: the C#'s
    /// `tune.PokeInto(tune.Pages, ...)`.
    pub fn poke_into_own(&mut self, constant: &TuneConstant, element: i32, value: f64) -> bool {
        let mut pages = self.take_pages();
        let ok = self.poke_into(&mut pages, constant, element, value);
        self.put_pages(pages);
        ok
    }

    /// Writes a text setting, padded with nulls, or refuses it whole.
    ///
    /// Refuses rather than truncates: a caller told "written" when half the name was kept has
    /// been told something false, and every count of what was applied is then wrong in the
    /// same direction.
    pub fn poke_text_into(&self, pages: &mut [Vec<u8>], constant: &TuneConstant, value: &str) -> bool {
        if !constant.is_text || constant.page < 0 || constant.page as usize >= pages.len() {
            return false;
        }

        let page = &mut pages[constant.page as usize];
        let at = constant.offset;
        let length = constant.columns.max(0);

        if at < 0 || i64::from(at) + i64::from(length) > page.len() as i64 {
            return false;
        }

        // Both checked before anything is written. Rejecting half way through leaves the
        // field partly overwritten while the caller is told nothing changed, and the bytes go
        // to the ECU on the next send regardless.
        if value.chars().any(|c| c as u32 > 0x7F) {
            return false;
        }

        // Too long is refused rather than cut short. Silently keeping the first sixteen
        // characters and reporting success is worse than refusing: the caller counts it
        // stored, so a tune restored from a build with a wider field reports the name applied
        // and puts a different name on the controller; and a person who types past the end
        // sees it accepted and finds it shortened afterwards. `poke_into` refuses a value its
        // field cannot hold; this is the same rule for the same reason.
        //
        // The value is ASCII by now, so its byte length is the C#'s UTF-16 `Length`.
        if value.len() as i64 > i64::from(length) {
            return false;
        }

        // Padded with nulls, which is what the firmware stores and what `text_in` reads back.
        // Padding with spaces would make writing a name back to the one it already has
        // produce a difference, and so a write.
        let bytes = value.as_bytes();
        for i in 0..length as usize {
            page[at as usize + i] = bytes.get(i).copied().unwrap_or(0);
        }

        true
    }

    /// Puts a text field back to the bytes the controller holds, exactly.
    ///
    /// Not the same as reading it and writing it again: reading trims the padding off and
    /// writing pads with nulls, so a name a firmware stores padded with spaces would come back
    /// differing from the one on the ECU while claiming to be identical to it.
    pub fn restore_text_into(&self, pages: &mut [Vec<u8>], constant: &TuneConstant) -> bool {
        if !constant.is_text || constant.page < 0 || constant.page as usize >= pages.len() {
            return false;
        }
        if constant.page as usize >= self.pages.len() {
            return false;
        }

        let source = &self.pages[constant.page as usize];
        let page = &mut pages[constant.page as usize];

        let at = constant.offset;
        let length = constant.columns.max(0);
        let end = i64::from(at) + i64::from(length);

        if at < 0 || end > page.len() as i64 || end > source.len() as i64 {
            return false;
        }

        let (at, length) = (at as usize, length as usize);
        page[at..at + length].copy_from_slice(&source[at..at + length]);

        true
    }

    fn value_from(&self, pages: &[Vec<u8>], constant: &TuneConstant, element: i32) -> Option<f64> {
        if constant.page < 0 || constant.page as usize >= pages.len() {
            return None;
        }

        let page = &pages[constant.page as usize];
        let at = i64::from(constant.offset) + i64::from(element) * i64::from(constant.element_size());

        if at < 0 || at + i64::from(constant.element_size()) > page.len() as i64 {
            return None;
        }

        Some(self.decode(&page[at as usize..], constant))
    }

    fn value(&self, constant: &TuneConstant, element: i32) -> Option<f64> {
        self.value_from(&self.pages, constant, element)
    }

    /// The number these bytes hold, scaled and bit-masked as the firmware says.
    ///
    /// The one place that is defined, shared by every reader. Two decoders that drifted apart
    /// would let an editor show one number and write another, which is the sort of thing
    /// nobody finds until an engine runs on it.
    fn decode(&self, bytes: &[u8], constant: &TuneConstant) -> f64 {
        let raw = match raw(bytes, constant.type_, self.little) {
            Some(raw) => raw,
            None if constant.type_ == RealtimeType::F32 => {
                let four = [bytes[0], bytes[1], bytes[2], bytes[3]];
                f64::from(if self.little {
                    f32::from_le_bytes(four)
                } else {
                    f32::from_be_bytes(four)
                })
            }
            None => f64::NAN,
        };

        if !constant.is_bit_field() {
            return (raw + constant.transform) * constant.scale;
        }

        let width = constant.bit_high - constant.bit_low + 1;
        let mask = 1i64.wrapping_shl(width as u32).wrapping_sub(1);

        ((raw as i64).wrapping_shr(constant.bit_low as u32) & mask) as f64
    }
}

/// What an expression comes to, or the fallback where it cannot be worked out. A scale of
/// nought is refused: it would divide by zero on the way back and make every value in the
/// table unwritable.
fn sum(expression: &str, lookup: &dyn Fn(&str) -> f64, fallback: f64) -> f64 {
    if expression.is_empty() {
        return fallback;
    }

    let body = expression.trim_start_matches('{').trim_end_matches('}');
    let value = dialog_condition::number(body, lookup);

    if value.is_finite() && value != 0.0 {
        value
    } else {
        fallback
    }
}

/// The undecoded number in these bytes, before scale or bit masking.
fn raw(bytes: &[u8], type_: RealtimeType, little: bool) -> Option<f64> {
    let two = || [bytes[0], bytes[1]];
    let four = || [bytes[0], bytes[1], bytes[2], bytes[3]];

    Some(match type_ {
        RealtimeType::U08 => f64::from(bytes[0]),
        RealtimeType::S08 => f64::from(bytes[0] as i8),
        RealtimeType::U16 => f64::from(if little {
            u16::from_le_bytes(two())
        } else {
            u16::from_be_bytes(two())
        }),
        RealtimeType::S16 => f64::from(if little {
            i16::from_le_bytes(two())
        } else {
            i16::from_be_bytes(two())
        }),
        RealtimeType::U32 => f64::from(if little {
            u32::from_le_bytes(four())
        } else {
            u32::from_be_bytes(four())
        }),
        RealtimeType::S32 => f64::from(if little {
            i32::from_le_bytes(four())
        } else {
            i32::from_be_bytes(four())
        }),
        RealtimeType::F32 => return None,
    })
}

/// Writes one value, refusing anything the type cannot hold.
///
/// A value out of range is a mistake somewhere upstream, and wrapping it into whatever it
/// happens to become is the worst of the available responses.
fn try_write(at: &mut [u8], type_: RealtimeType, value: f64, little: bool) -> bool {
    // Half away from zero, as `Math.Round(value, MidpointRounding.AwayFromZero)`: Rust's
    // `round` is that.
    let rounded = value.round();

    match type_ {
        RealtimeType::U08 => {
            if rounded < 0.0 || rounded > f64::from(u8::MAX) {
                return false;
            }
            at[0] = rounded as u8;
            true
        }
        RealtimeType::S08 => {
            if rounded < f64::from(i8::MIN) || rounded > f64::from(i8::MAX) {
                return false;
            }
            at[0] = (rounded as i8) as u8;
            true
        }
        RealtimeType::U16 => {
            if rounded < 0.0 || rounded > f64::from(u16::MAX) {
                return false;
            }
            write16(at, rounded as u16, little);
            true
        }
        RealtimeType::S16 => {
            if rounded < f64::from(i16::MIN) || rounded > f64::from(i16::MAX) {
                return false;
            }
            write16(at, (rounded as i16) as u16, little);
            true
        }
        RealtimeType::U32 => {
            if rounded < 0.0 || rounded > f64::from(u32::MAX) {
                return false;
            }
            write32(at, rounded as u32, little);
            true
        }
        RealtimeType::S32 => {
            if rounded < f64::from(i32::MIN) || rounded > f64::from(i32::MAX) {
                return false;
            }
            write32(at, (rounded as i32) as u32, little);
            true
        }
        RealtimeType::F32 => {
            // A float keeps its fraction; rounding it would be the bug.
            write32(at, (value as f32).to_bits(), little);
            true
        }
    }
}

fn write16(at: &mut [u8], value: u16, little: bool) {
    let bytes = if little {
        value.to_le_bytes()
    } else {
        value.to_be_bytes()
    };
    at[..2].copy_from_slice(&bytes);
}

fn write32(at: &mut [u8], value: u32, little: bool) {
    let bytes = if little {
        value.to_le_bytes()
    } else {
        value.to_be_bytes()
    };
    at[..4].copy_from_slice(&bytes);
}
