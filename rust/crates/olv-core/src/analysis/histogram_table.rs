//! Ported from `HistogramTable.cs`. Owned by package `w3-analysis`.
//!
//! A two-dimensional binned summary of a log: samples are bucketed by two channels (say RPM
//! and MAP) and a third is aggregated within each bucket.
//!
//! This is how a tuner turns a drive into a table: the same shape as the VE or AFR table in
//! the ECU, so the log can be read against the tune it came from.

use std::sync::Arc;

use crate::filters::log_filter::SampleMask;
use crate::fmt;
use crate::model::{Array2, LogChannel};
use crate::units::UnitSystem;
use crate::{Error, Result};

/// The cells a stretch of the log passed through.
#[derive(Clone, Debug, PartialEq)]
pub struct CellVisits {
    /// Samples of the span in each cell, indexed `[column, row]`.
    pub counts: Array2<usize>,
    /// How many cells it reached at all.
    pub cells: usize,
    /// Samples of the span that landed in one.
    pub samples: usize,
    /// Samples of the span that landed in none: outside the window the table was built over,
    /// or excluded by a filter.
    pub outside: usize,
}

impl CellVisits {
    pub fn is_empty(&self) -> bool {
        self.cells == 0
    }

    /// The C#'s `Visited`, which takes signed indexes and answers false outside the grid.
    pub fn visited(&self, column: isize, row: isize) -> bool {
        match (usize::try_from(column), usize::try_from(row)) {
            (Ok(c), Ok(r)) => self.counts.get(c, r).is_some_and(|&n| n > 0),
            _ => false,
        }
    }
}

/// How the samples falling in one cell are reduced to a single number.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HistogramStatistic {
    Mean,
    Min,
    Max,
    Count,
}

/// The channels (and optional mask) a table is built from. Groups what the C# passes as
/// separate parameters.
#[derive(Clone, Copy)]
pub struct HistogramInputs<'a> {
    pub x: &'a Arc<LogChannel>,
    pub y: &'a Arc<LogChannel>,
    pub z: &'a Arc<LogChannel>,
    /// When set, cells hold Z minus this channel rather than Z itself.
    pub z_compare: Option<&'a Arc<LogChannel>>,
    pub mask: Option<&'a SampleMask>,
}

impl<'a> HistogramInputs<'a> {
    pub fn new(x: &'a Arc<LogChannel>, y: &'a Arc<LogChannel>, z: &'a Arc<LogChannel>) -> Self {
        Self {
            x,
            y,
            z,
            z_compare: None,
            mask: None,
        }
    }

    pub fn with_mask(mut self, mask: Option<&'a SampleMask>) -> Self {
        self.mask = mask;
        self
    }

    pub fn with_z_compare(mut self, z_compare: Option<&'a Arc<LogChannel>>) -> Self {
        self.z_compare = z_compare;
        self
    }
}

#[derive(Clone, Debug)]
pub struct HistogramTable {
    pub columns: usize,
    pub rows: usize,
    /// Bin centres along X, ascending.
    pub column_centers: Vec<f64>,
    /// Bin centres along Y, ascending. Row 0 is the lowest value.
    pub row_centers: Vec<f64>,
    /// Aggregated Z per cell, indexed `[column, row]`. `None` where no samples landed.
    pub values: Array2<Option<f64>>,
    /// Sample count per cell, indexed `[column, row]`.
    pub counts: Array2<usize>,
    pub x: Arc<LogChannel>,
    pub y: Arc<LogChannel>,
    pub z: Arc<LogChannel>,
    pub statistic: HistogramStatistic,
    /// True when the axes came from the tune rather than the data range.
    pub from_tune: bool,
    /// When set, cells hold Z minus this channel: "how far off target am I", which is the
    /// question being asked of a tuning table, rather than "what did it read".
    pub z_compare: Option<Arc<LogChannel>>,
    /// What the cells hold, when it is not the Z channel: "VE change, %".
    pub display_name: Option<String>,
    /// Decimal places for a computed table; `None` to use Z's own.
    pub display_digits: Option<i32>,
    /// Smallest aggregated value across populated cells.
    pub min_value: f64,
    /// Largest aggregated value across populated cells.
    pub max_value: f64,
    pub max_count: usize,
    /// Samples that landed in a cell; excludes rows with a missing reading.
    pub sample_count: usize,
    pub populated_cells: usize,
    /// Sample window the table was built over, for tracing cells back.
    pub first_sample: usize,
    pub last_sample: usize,
    mask: Option<SampleMask>,
}

/// The window a build is clamped to: `(from, to)` as signed values, since `to` is -1 for an
/// empty channel.
fn window(length: usize, first_sample: isize, last_sample: isize) -> (isize, isize) {
    let from = 0.max(first_sample.min(last_sample));
    let to = (length as isize - 1).min(first_sample.max(last_sample));
    (from, to)
}

fn accepts(mask: Option<&SampleMask>, index: usize) -> bool {
    mask.is_none_or(|m| m.is_accepted(index))
}

impl HistogramTable {
    fn blank(columns: usize, rows: usize, inputs: &HistogramInputs, statistic: HistogramStatistic) -> Self {
        Self {
            columns,
            rows,
            column_centers: vec![0.0; columns],
            row_centers: vec![0.0; rows],
            values: Array2::new(columns, rows),
            counts: Array2::new(columns, rows),
            x: Arc::clone(inputs.x),
            y: Arc::clone(inputs.y),
            z: Arc::clone(inputs.z),
            statistic,
            from_tune: false,
            z_compare: inputs.z_compare.cloned(),
            display_name: None,
            display_digits: None,
            min_value: 0.0,
            max_value: 0.0,
            max_count: 0,
            sample_count: 0,
            populated_cells: 0,
            first_sample: 0,
            last_sample: 0,
            mask: None,
        }
    }

    pub fn is_delta(&self) -> bool {
        self.z_compare.is_some()
    }

    /// Whether cells hold a signed deviation, which is what makes a diverging scale the right
    /// one. Counting samples is a magnitude even when a comparison channel is set.
    pub fn shows_deviation(&self) -> bool {
        self.is_delta() && self.statistic != HistogramStatistic::Count
    }

    /// Largest deviation either side of zero. A delta scale has to be symmetric about zero, or
    /// equal errors in opposite directions would be shaded with different intensities.
    pub fn max_deviation(&self) -> f64 {
        self.min_value.abs().max(self.max_value.abs())
    }

    pub fn is_empty(&self) -> bool {
        self.populated_cells == 0
    }

    /// Sample indices that landed in one cell, in order.
    ///
    /// Recomputed rather than stored: a per-sample cell index would cost memory on every table
    /// for a lookup used only when a cell is clicked.
    pub fn samples_in(&self, column: usize, row: usize) -> Vec<usize> {
        let mut hits = Vec::new();
        if column >= self.columns || row >= self.rows {
            return hits;
        }

        for i in self.first_sample..=self.last_sample {
            if !accepts(self.mask.as_ref(), i) {
                continue;
            }

            let (xv, yv, zv) = (self.x.at(i), self.y.at(i), self.z.at(i));
            if xv.is_nan() || yv.is_nan() || zv.is_nan() {
                continue;
            }
            if self.z_compare.as_ref().is_some_and(|c| c.at(i).is_nan()) {
                continue;
            }

            if nearest(&self.column_centers, xv) == column && nearest(&self.row_centers, yv) == row {
                hits.push(i);
            }
        }

        hits
    }

    /// The cell's samples grouped into visits. An engine passes through the same RPM and load
    /// many times in a drive, so a cell is almost never one stretch of the log.
    pub fn visits_to(&self, column: usize, row: usize, gap_tolerance: usize) -> Vec<(usize, usize)> {
        let mut visits = Vec::new();
        let mut start: Option<usize> = None;
        let mut previous = 0;

        for i in self.samples_in(column, row) {
            let Some(s) = start else {
                start = Some(i);
                previous = i;
                continue;
            };

            // A sample or two of noise should not split one visit in half.
            if i - previous <= gap_tolerance + 1 {
                previous = i;
                continue;
            }

            visits.push((s, previous));
            start = Some(i);
            previous = i;
        }

        if let Some(s) = start {
            visits.push((s, previous));
        }
        visits
    }

    /// The longest visit to a cell, which is the one worth looking at first.
    pub fn longest_visit_to(&self, column: usize, row: usize) -> Option<(usize, usize)> {
        let visits = self.visits_to(column, row, 2);
        let mut best = *visits.first()?;
        for &(first, last) in &visits {
            if last - first > best.1 - best.0 {
                best = (first, last);
            }
        }
        Some(best)
    }

    /// The cell a sample falls in, or `None` when it is excluded.
    pub fn cell_of(&self, sample: usize) -> Option<(usize, usize)> {
        if sample < self.first_sample || sample > self.last_sample {
            return None;
        }
        if !accepts(self.mask.as_ref(), sample) {
            return None;
        }

        let (xv, yv) = (self.x.at(sample), self.y.at(sample));
        if xv.is_nan() || yv.is_nan() {
            return None;
        }

        Some((nearest(&self.column_centers, xv), nearest(&self.row_centers, yv)))
    }

    /// Which cells a stretch of the log passed through, and how many samples it left in each.
    ///
    /// The other direction of the trace-back: mark a pull, and find out which cells of the
    /// table it exercised. `first_sample` and `last_sample` are inclusive, in either order.
    pub fn visited_by(&self, first_sample: isize, last_sample: isize) -> CellVisits {
        let mut counts = Array2::<usize>::new(self.columns, self.rows);
        let from = first_sample.min(last_sample);
        let to = first_sample.max(last_sample);

        let (mut cells, mut samples, mut outside) = (0, 0, 0);

        for i in from..=to {
            // Counted rather than ignored: a span that lands mostly outside marks almost
            // nothing, and looking like a bug is the alternative to saying so.
            let cell = usize::try_from(i).ok().and_then(|s| self.cell_of(s));
            let Some((column, row)) = cell else {
                outside += 1;
                continue;
            };

            if counts[(column, row)] == 0 {
                cells += 1;
            }
            counts[(column, row)] += 1;
            samples += 1;
        }

        CellVisits {
            counts,
            cells,
            samples,
            outside,
        }
    }

    /// Bins samples `first_sample..=last_sample` into `columns` x `rows` bins. Axis ranges
    /// come from that window rather than the whole log, so zooming into one pull re-scales
    /// the table around it.
    ///
    /// # Errors
    /// [`Error::Argument`] when `columns` or `rows` is zero, as the C# throws
    /// `ArgumentOutOfRangeException`.
    pub fn build(
        inputs: HistogramInputs,
        columns: usize,
        rows: usize,
        first_sample: isize,
        last_sample: isize,
        statistic: HistogramStatistic,
    ) -> Result<Self> {
        if columns < 1 {
            return Err(Error::Argument("columns must be at least 1".into()));
        }
        if rows < 1 {
            return Err(Error::Argument("rows must be at least 1".into()));
        }

        let length = inputs.x.len().min(inputs.y.len().min(inputs.z.len()));
        let (from, to) = window(length, first_sample, last_sample);

        let mut table = Self::blank(columns, rows, &inputs, statistic);
        if from > to {
            return Ok(table.finish());
        }
        let (from, to) = (from as usize, to as usize);

        // Axes are scaled to the samples that survive filtering, so excluding warmup or idle
        // also tightens the axis onto the data that remains.
        let (x_min, x_step) = axis(inputs.x, from, to, columns, inputs.mask);
        let (y_min, y_step) = axis(inputs.y, from, to, rows, inputs.mask);

        for i in 0..columns {
            table.column_centers[i] = x_min + (i as f64 + 0.5) * x_step;
        }
        for i in 0..rows {
            table.row_centers[i] = y_min + (i as f64 + 0.5) * y_step;
        }

        Ok(table.accumulate(&inputs, from, to, statistic))
    }

    /// Bins onto breakpoints supplied by the caller, normally the axes of the table in the
    /// ECU, so each cell here corresponds to a cell being tuned. Samples are assigned to the
    /// nearest breakpoint, which is how a value between two rows is attributed in a tuning
    /// table.
    ///
    /// # Errors
    /// [`Error::Argument`] when either breakpoint list is empty, as the C# throws.
    pub fn build_on_breakpoints(
        inputs: HistogramInputs,
        column_breakpoints: &[f64],
        row_breakpoints: &[f64],
        first_sample: isize,
        last_sample: isize,
        statistic: HistogramStatistic,
    ) -> Result<Self> {
        if column_breakpoints.is_empty() {
            return Err(Error::Argument("columnBreakpoints must not be empty".into()));
        }
        if row_breakpoints.is_empty() {
            return Err(Error::Argument("rowBreakpoints must not be empty".into()));
        }

        let length = inputs.x.len().min(inputs.y.len().min(inputs.z.len()));
        let (from, to) = window(length, first_sample, last_sample);

        let mut table = Self::blank(column_breakpoints.len(), row_breakpoints.len(), &inputs, statistic);
        table.from_tune = true;
        table.column_centers.copy_from_slice(column_breakpoints);
        table.row_centers.copy_from_slice(row_breakpoints);

        Ok(if from > to {
            table.finish()
        } else {
            table.accumulate(&inputs, from as usize, to as usize, statistic)
        })
    }

    /// A table whose cells are already known: a suggestion derived from a tune and a log,
    /// rather than an aggregate of samples.
    ///
    /// Counts carry over from the table the suggestion came from, so the view can still shade
    /// by how much data backs each cell.
    ///
    /// # Errors
    /// [`Error::Argument`] when either centre list is empty, as the C# throws.
    #[allow(clippy::too_many_arguments)]
    pub fn from_cells(
        inputs: HistogramInputs,
        column_centers: &[f64],
        row_centers: &[f64],
        values: &Array2<Option<f64>>,
        counts: &Array2<usize>,
        first_sample: usize,
        last_sample: usize,
        display_name: Option<&str>,
        display_digits: Option<i32>,
    ) -> Result<Self> {
        if column_centers.is_empty() {
            return Err(Error::Argument("columnCenters must not be empty".into()));
        }
        if row_centers.is_empty() {
            return Err(Error::Argument("rowCenters must not be empty".into()));
        }

        let mut table = Self::blank(
            column_centers.len(),
            row_centers.len(),
            &inputs,
            HistogramStatistic::Mean,
        );
        table.from_tune = true;
        table.first_sample = first_sample;
        table.last_sample = last_sample;
        table.display_name = display_name.map(str::to_string);
        table.display_digits = display_digits;
        table.mask = inputs.mask.cloned();
        table.column_centers.copy_from_slice(column_centers);
        table.row_centers.copy_from_slice(row_centers);

        // `Array.Copy` of a rectangular array copies in storage order.
        let rows = table.rows;
        for (i, v) in values.as_slice().iter().enumerate() {
            table.values[(i / rows, i % rows)] = *v;
        }
        for (i, n) in counts.as_slice().iter().enumerate() {
            table.counts[(i / rows, i % rows)] = *n;
        }

        Ok(table.finish())
    }

    fn accumulate(mut self, inputs: &HistogramInputs, from: usize, to: usize, statistic: HistogramStatistic) -> Self {
        let (columns, rows) = (self.columns, self.rows);

        self.first_sample = from;
        self.last_sample = to;
        self.mask = inputs.mask.cloned();

        let mask = inputs.mask;
        let mut sums = vec![0.0f64; columns * rows];
        let mut mins = vec![0.0f64; columns * rows];
        let mut maxes = vec![0.0f64; columns * rows];

        for i in from..=to {
            if !accepts(mask, i) {
                continue;
            }

            let (xv, yv, mut zv) = (inputs.x.at(i), inputs.y.at(i), inputs.z.at(i));
            if xv.is_nan() || yv.is_nan() || zv.is_nan() {
                continue;
            }

            if let Some(compare) = &self.z_compare {
                // The deviation is taken per sample and then aggregated, so the mean of the
                // error is reported rather than the error of the means.
                let target = compare.at(i);
                if target.is_nan() {
                    continue;
                }
                zv -= target;
            }

            let column = nearest(&self.column_centers, xv);
            let row = nearest(&self.row_centers, yv);
            let flat = column * rows + row;

            if self.counts[(column, row)] == 0 {
                mins[flat] = zv;
                maxes[flat] = zv;
            } else {
                if zv < mins[flat] {
                    mins[flat] = zv;
                }
                if zv > maxes[flat] {
                    maxes[flat] = zv;
                }
            }

            sums[flat] += zv;
            self.counts[(column, row)] += 1;
            self.sample_count += 1;
        }

        for c in 0..columns {
            for r in 0..rows {
                let count = self.counts[(c, r)];
                if count == 0 {
                    continue;
                }
                let flat = c * rows + r;
                self.values[(c, r)] = Some(match statistic {
                    HistogramStatistic::Mean => sums[flat] / count as f64,
                    HistogramStatistic::Min => mins[flat],
                    HistogramStatistic::Max => maxes[flat],
                    HistogramStatistic::Count => count as f64,
                });
            }
        }

        self.finish()
    }

    fn finish(mut self) -> Self {
        let (mut min, mut max) = (f64::INFINITY, f64::NEG_INFINITY);
        let (mut populated, mut max_count) = (0, 0);

        for c in 0..self.columns {
            for r in 0..self.rows {
                if self.counts[(c, r)] > max_count {
                    max_count = self.counts[(c, r)];
                }
                let Some(v) = self.values[(c, r)] else { continue };

                populated += 1;
                if v < min {
                    min = v;
                }
                if v > max {
                    max = v;
                }
            }
        }

        self.populated_cells = populated;
        self.max_count = max_count;
        self.min_value = if populated == 0 { 0.0 } else { min };
        self.max_value = if populated == 0 { 0.0 } else { max };
        self
    }

    /// Formats a cell for display, using the Z channel's own precision.
    pub fn format_cell(&self, column: usize, row: usize) -> String {
        self.values[(column, row)].map_or_else(String::new, |v| self.format(v))
    }

    /// Formats a value on this table's scale. Deltas carry an explicit sign, so the direction
    /// of an error is readable without consulting the colour.
    pub fn format(&self, value: f64) -> String {
        if self.statistic == HistogramStatistic::Count {
            return fmt::int(value as i32, "N0");
        }

        // A computed table holds something other than Z (a percentage change, a suggested VE
        // number), so Z's own precision would be the wrong one.
        let text = match self.display_digits {
            Some(digits) => fmt::f64(value, &format!("F{}", digits.clamp(0, 6))),
            None => self.z.format(value, UnitSystem::AsReported),
        };

        if self.is_delta() && value > 0.0 {
            format!("+{text}")
        } else {
            text
        }
    }
}

/// Axis origin and bin width over the sample window. A channel that never changes would give
/// a zero-width axis, so it is widened to keep every sample inside the table.
fn axis(channel: &LogChannel, from: usize, to: usize, bins: usize, mask: Option<&SampleMask>) -> (f64, f64) {
    let (mut min, mut max) = (f64::INFINITY, f64::NEG_INFINITY);
    for i in from..=to {
        if !accepts(mask, i) {
            continue;
        }

        let v = channel.at(i);
        if v.is_nan() {
            continue;
        }
        if v < min {
            min = v;
        }
        if v > max {
            max = v;
        }
    }

    if min.is_infinite() {
        min = 0.0;
        max = 1.0;
    }
    if max - min <= 0.0 {
        min -= 0.5;
        max += 0.5;
    }

    (min, (max - min) / bins as f64)
}

/// Index of the closest breakpoint. Ties and out-of-range values fall to the nearest end, so
/// every sample lands somewhere rather than being dropped.
pub(crate) fn nearest(centers: &[f64], value: f64) -> usize {
    let mut best = 0;
    let mut best_distance = (value - centers[0]).abs();

    for (i, center) in centers.iter().enumerate().skip(1) {
        let distance = (value - center).abs();
        if distance >= best_distance {
            continue;
        }
        best_distance = distance;
        best = i;
    }

    best
}
