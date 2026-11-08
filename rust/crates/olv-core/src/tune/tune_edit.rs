//! Ported from `TuneEdit.cs`. Owned by package `w4-tune`.
//!
//! A tuning table being changed, and what it was before.
//!
//! Editing a table is not editing a grid of numbers. What is on screen has to be turned back
//! into the bytes the ECU holds, held to the limits the firmware declares, and, most of all,
//! kept separable from what was read off the controller, so that at every moment it is clear
//! which cells have been touched and what they used to say. A tuner who cannot see what they
//! have changed cannot decide whether to send it.
//!
//! Nothing here writes anything. This produces the edited values and the bytes they encode to;
//! sending them is a separate, deliberate act.

use super::ecu_tune::{EcuTune, TuneWrite};
use crate::ini::tune_layout::TuneConstant;
use crate::model::{Array2, TuneTable};

/// `Math.Min` for doubles: a NaN on either side wins, and `-0.0` is below `0.0`. Rust's
/// `f64::min` ignores a NaN, which would turn a table with an unreadable cell into one that
/// previews as if it had none.
pub(crate) fn dotnet_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a < b || (a == b && a.is_sign_negative()) {
        a
    } else {
        b
    }
}

/// `Math.Max` for doubles; see [`dotnet_min`].
pub(crate) fn dotnet_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a > b || (a == b && a.is_sign_positive()) {
        a
    } else {
        b
    }
}

/// `Math.Clamp` for doubles. Rust's `f64::clamp` panics when the bounds are inverted or NaN,
/// where .NET only throws for inverted ones and otherwise compares, so a NaN bound simply never
/// binds. A caller that has checked its bounds (as the callers here do) gets the same answer.
pub(crate) fn dotnet_clamp(value: f64, min: f64, max: f64) -> f64 {
    if value < min {
        min
    } else if value > max {
        max
    } else {
        value
    }
}

/// A rectangle of cells, as a selection in a table.
///
/// Held as two corners rather than an origin and a size, because that is what dragging
/// produces and either corner may be the first one touched.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TuneSelection {
    pub from_column: i32,
    pub from_row: i32,
    pub to_column: i32,
    pub to_row: i32,
}

impl TuneSelection {
    pub fn new(from_column: i32, from_row: i32, to_column: i32, to_row: i32) -> Self {
        Self {
            from_column,
            from_row,
            to_column,
            to_row,
        }
    }

    pub fn cell(column: i32, row: i32) -> Self {
        Self::new(column, row, column, row)
    }

    pub fn left(&self) -> i32 {
        self.from_column.min(self.to_column)
    }

    pub fn right(&self) -> i32 {
        self.from_column.max(self.to_column)
    }

    pub fn top(&self) -> i32 {
        self.from_row.min(self.to_row)
    }

    pub fn bottom(&self) -> i32 {
        self.from_row.max(self.to_row)
    }

    pub fn columns(&self) -> i32 {
        self.right() - self.left() + 1
    }

    pub fn rows(&self) -> i32 {
        self.bottom() - self.top() + 1
    }

    pub fn count(&self) -> i32 {
        self.columns() * self.rows()
    }

    pub fn contains(&self, column: i32, row: i32) -> bool {
        column >= self.left() && column <= self.right() && row >= self.top() && row <= self.bottom()
    }

    /// The same selection held inside a table of this size.
    ///
    /// `Math.Clamp(x, 0, n - 1)` throws for an empty table; this holds to `[0, n - 1]` without
    /// the throw, which for an empty table selects nothing that exists.
    pub fn clamped_to(&self, columns: i32, rows: i32) -> TuneSelection {
        let hold = |v: i32, n: i32| v.max(0).min(n - 1);

        TuneSelection::new(
            hold(self.from_column, columns),
            hold(self.from_row, rows),
            hold(self.to_column, columns),
            hold(self.to_row, rows),
        )
    }
}

/// What a selection was and what it has become.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TuneChange {
    /// How many are selected.
    pub cells: i32,
    /// How many of those differ from the ECU's own values.
    pub changed: i32,
    /// Lowest value the ECU had here.
    pub from_low: f64,
    /// Highest it had.
    pub from_high: f64,
    /// Lowest it would become.
    pub to_low: f64,
    /// Highest it would become.
    pub to_high: f64,
    /// Smallest change across the selection, signed.
    pub delta_low: f64,
    /// Largest change, signed.
    pub delta_high: f64,
}

impl TuneChange {
    pub fn is_single(&self) -> bool {
        self.cells == 1
    }

    pub fn any(&self) -> bool {
        self.changed > 0
    }

    /// The value, where exactly one cell is selected.
    pub fn from(&self) -> f64 {
        self.from_low
    }

    pub fn to(&self) -> f64 {
        self.to_low
    }

    pub fn delta(&self) -> f64 {
        self.delta_low
    }

    /// The change as a proportion of what was there, where that means anything.
    ///
    /// NaN from a cell that held zero, which a spark table's cells legitimately do: a
    /// percentage of nothing is not a number, and showing it as an infinite increase would be
    /// worse than showing nothing.
    pub fn percent(&self) -> f64 {
        if self.is_single() && self.from().abs() > 1e-9 {
            (self.to() - self.from()) / self.from() * 100.0
        } else {
            f64::NAN
        }
    }

    /// Whether every changed cell moved by the same amount.
    pub fn uniform(&self) -> bool {
        (self.delta_high - self.delta_low).abs() < 1e-9
    }
}

/// A tuning table being changed, and what it was before. See the module docs.
#[derive(Clone, Debug)]
pub struct TuneEdit {
    original: Array2<f64>,
    values: Array2<f64>,
    table: TuneTable,
    constant: Option<TuneConstant>,
}

impl TuneEdit {
    pub fn new(table: TuneTable, constant: Option<TuneConstant>) -> Self {
        TuneEdit {
            original: table.values.clone(),
            values: table.values.clone(),
            table,
            constant,
        }
    }

    pub fn table(&self) -> &TuneTable {
        &self.table
    }

    /// The firmware's description of the cells, where one is known.
    pub fn constant(&self) -> Option<&TuneConstant> {
        self.constant.as_ref()
    }

    pub fn name(&self) -> &str {
        &self.table.name
    }

    pub fn units(&self) -> &str {
        &self.table.units
    }

    pub fn columns(&self) -> i32 {
        self.values.get_length(0) as i32
    }

    pub fn rows(&self) -> i32 {
        self.values.get_length(1) as i32
    }

    /// Decimals to show, from the firmware's own declaration.
    pub fn digits(&self) -> i32 {
        self.constant.as_ref().map_or(0, |c| c.digits)
    }

    /// The smallest change worth making, from the firmware's storage.
    ///
    /// A table stored as whole bytes cannot hold half a percent, so nudging by less than one
    /// raw count would show a change on screen that the ECU rounds away, which reads as the
    /// write having been ignored.
    pub fn step(&self) -> f64 {
        match &self.constant {
            Some(c) if c.scale > 0.0 => c.scale.abs(),
            _ => 1.0,
        }
    }

    pub fn low(&self) -> f64 {
        match &self.constant {
            Some(c) if c.has_range() => c.low,
            _ => f64::NAN,
        }
    }

    pub fn high(&self) -> f64 {
        match &self.constant {
            Some(c) if c.has_range() => c.high,
            _ => f64::NAN,
        }
    }

    /// The cell's value now (the C# indexer).
    pub fn get(&self, column: i32, row: i32) -> f64 {
        self.values[(column as usize, row as usize)]
    }

    pub fn original(&self, column: i32, row: i32) -> f64 {
        self.original[(column as usize, row as usize)]
    }

    /// Whether a cell differs from what was read off the ECU.
    ///
    /// Compared against a fraction of the storage step rather than exactly: scaling a table by
    /// a percentage and scaling it back leaves floating-point dust that encodes to the
    /// identical byte, and reporting that as a change would mean a table that says it is dirty
    /// and writes nothing.
    pub fn is_changed(&self, column: i32, row: i32) -> bool {
        (self.get(column, row) - self.original(column, row)).abs() > self.step() / 1000.0
    }

    pub fn has_changes(&self) -> bool {
        for column in 0..self.columns() {
            for row in 0..self.rows() {
                if self.is_changed(column, row) {
                    return true;
                }
            }
        }

        false
    }

    pub fn changed_count(&self) -> i32 {
        let mut count = 0;

        for column in 0..self.columns() {
            for row in 0..self.rows() {
                if self.is_changed(column, row) {
                    count += 1;
                }
            }
        }

        count
    }

    /// Sets every selected cell to one value.
    pub fn set(&mut self, selection: TuneSelection, value: f64) {
        self.apply(selection, |_| value);
    }

    /// Adds to every selected cell: the arrow-key edit.
    pub fn add(&mut self, selection: TuneSelection, delta: f64) {
        self.apply(selection, |current| current + delta);
    }

    /// Scales every selected cell by a percentage.
    ///
    /// The operation tuning is actually done in: a region reading four per cent lean is
    /// corrected by adding four per cent to it, not by typing sixteen numbers.
    pub fn scale(&mut self, selection: TuneSelection, percent: f64) {
        self.apply(selection, |current| current * (1.0 + (percent / 100.0)));
    }

    /// Fills the inside of a selection from its edges.
    ///
    /// The operation for smoothing a table after a few cells have been pulled about: pick a
    /// region whose ends are right, and let everything between them be a straight line rather
    /// than a staircase. The corners are left exactly as they are, so interpolating twice
    /// changes nothing the second time.
    ///
    /// One formula covers all three shapes. A selection one row tall has nothing to vary down
    /// it, so the vertical term falls out and it is a straight line along the row; one column
    /// wide is the same the other way; anything larger is bilinear between the four corners.
    ///
    /// Returns false where there was nothing between the ends to fill: a selection two cells
    /// across is all corners, and reporting that as done would leave the user believing
    /// something happened.
    pub fn interpolate(&mut self, selection: TuneSelection) -> bool {
        let area = selection.clamped_to(self.columns(), self.rows());

        if area.columns() < 3 && area.rows() < 3 {
            return false;
        }

        let cell = |edit: &TuneEdit, c: i32, r: i32| edit.values[(c as usize, r as usize)];

        let top_left = cell(self, area.left(), area.top());
        let top_right = cell(self, area.right(), area.top());
        let bottom_left = cell(self, area.left(), area.bottom());
        let bottom_right = cell(self, area.right(), area.bottom());

        for column in area.left()..=area.right() {
            let across = if area.columns() > 1 {
                f64::from(column - area.left()) / f64::from(area.columns() - 1)
            } else {
                0.0
            };

            let top = top_left + ((top_right - top_left) * across);
            let bottom = bottom_left + ((bottom_right - bottom_left) * across);

            for row in area.top()..=area.bottom() {
                let down = if area.rows() > 1 {
                    f64::from(row - area.top()) / f64::from(area.rows() - 1)
                } else {
                    0.0
                };

                let held = self.hold(top + ((bottom - top) * down));
                self.values[(column as usize, row as usize)] = held;
            }
        }

        true
    }

    /// What the selected cells were and what they have become.
    ///
    /// The question a tuner is actually asking before sending anything: not "how much did I
    /// change it by" but "what is it going to be". Those are the same question only if you can
    /// remember what it said before, and by the fourth nudge nobody can.
    pub fn preview(&self, selection: TuneSelection) -> TuneChange {
        let area = selection.clamped_to(self.columns(), self.rows());

        let (mut from_low, mut from_high) = (f64::INFINITY, f64::NEG_INFINITY);
        let (mut to_low, mut to_high) = (f64::INFINITY, f64::NEG_INFINITY);
        let (mut delta_low, mut delta_high) = (f64::INFINITY, f64::NEG_INFINITY);
        let mut changed = 0;

        for column in area.left()..=area.right() {
            for row in area.top()..=area.bottom() {
                let was = self.original(column, row);
                let now = self.get(column, row);

                from_low = dotnet_min(from_low, was);
                from_high = dotnet_max(from_high, was);
                to_low = dotnet_min(to_low, now);
                to_high = dotnet_max(to_high, now);
                delta_low = dotnet_min(delta_low, now - was);
                delta_high = dotnet_max(delta_high, now - was);

                if self.is_changed(column, row) {
                    changed += 1;
                }
            }
        }

        TuneChange {
            cells: area.count(),
            changed,
            from_low,
            from_high,
            to_low,
            to_high,
            delta_low,
            delta_high,
        }
    }

    /// Puts every cell back to what the ECU said.
    pub fn revert_all(&mut self) {
        self.values = self.original.clone();
    }

    /// Puts the selected cells back, leaving other edits alone.
    pub fn revert(&mut self, selection: TuneSelection) {
        let area = selection.clamped_to(self.columns(), self.rows());

        for column in area.left()..=area.right() {
            for row in area.top()..=area.bottom() {
                self.values[(column as usize, row as usize)] = self.original[(column as usize, row as usize)];
            }
        }
    }

    fn apply(&mut self, selection: TuneSelection, change: impl Fn(f64) -> f64) {
        let area = selection.clamped_to(self.columns(), self.rows());

        for column in area.left()..=area.right() {
            for row in area.top()..=area.bottom() {
                let at = (column as usize, row as usize);
                let held = self.hold(change(self.values[at]));
                self.values[at] = held;
            }
        }
    }

    /// Holds a value inside what the firmware allows.
    ///
    /// Clamped rather than refused: a tuner scaling a whole table up by ten per cent should
    /// not have the operation rejected because one cell was already at the limit. What must not
    /// happen is a value going out past the range and being written: the encoder would refuse
    /// it and the whole write would fail, which turns a small mistake in one cell into a table
    /// that cannot be sent at all.
    fn hold(&self, value: f64) -> f64 {
        if value.is_nan() || value.is_infinite() {
            return 0.0;
        }

        match &self.constant {
            Some(c) if c.has_range() => dotnet_clamp(value, c.low, c.high),
            _ => value,
        }
    }

    /// The edited cells, for drawing.
    pub fn values(&self) -> Array2<f64> {
        self.values.clone()
    }

    /// The edited table, so the same view can draw it.
    pub fn as_table(&self) -> TuneTable {
        TuneTable {
            values: self.values(),
            ..self.table.clone()
        }
    }

    /// The bytes this would write, or `None` when the firmware's table cannot be encoded: a
    /// value out of range, or a constant this INI does not describe.
    pub fn encode(&self, tune: &EcuTune) -> Option<TuneWrite> {
        let constant = self.constant.as_ref()?;
        tune.encode_table(&constant.name, &self.values)
    }
}
