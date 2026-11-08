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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::filters::log_filter::{FilterComparison, LogFilter, SampleFilter};
    use crate::model::LogDocument;

    fn ch(name: &str, units: &str, digits: i32, values: &[f64]) -> Arc<LogChannel> {
        Arc::new(LogChannel::new(name, units, digits, values, false))
    }

    fn repeat(v: f64, n: usize) -> Vec<f64> {
        vec![v; n]
    }

    fn doc(channels: Vec<Arc<LogChannel>>) -> LogDocument {
        let n = channels[0].len();
        let time: Vec<f64> = (0..n).map(|i| i as f64 * 0.1).collect();
        LogDocument::new(
            "x",
            "test",
            channels,
            Arc::new(LogChannel::new("Time", "s", 2, &time, true)),
        )
    }

    fn warm_filter() -> LogFilter {
        LogFilter::new("warm", "CLT", FilterComparison::AboveOrEqual).with_low(160.0)
    }

    fn assert_close(expected: f64, actual: f64, places: i32) {
        let tolerance = 0.5 * 10f64.powi(-places);
        assert!((expected - actual).abs() <= tolerance, "{expected} vs {actual}");
    }

    // ---- HistogramDeltaTests -------------------------------------------------

    fn afr_channel(name: &str, values: &[f64]) -> Arc<LogChannel> {
        ch(name, "AFR", 2, values)
    }

    fn delta(
        z: &Arc<LogChannel>,
        target: &Arc<LogChannel>,
        statistic: HistogramStatistic,
        columns: usize,
        rows: usize,
    ) -> HistogramTable {
        let n = z.len();
        let x = ch("RPM", "RPM", 0, &repeat(1000.0, n));
        let y = ch("MAP", "kPa", 0, &repeat(50.0, n));
        HistogramTable::build(
            HistogramInputs::new(&x, &y, z).with_z_compare(Some(target)),
            columns,
            rows,
            0,
            n as isize - 1,
            statistic,
        )
        .unwrap()
    }

    fn delta_mean(z: &Arc<LogChannel>, target: &Arc<LogChannel>) -> HistogramTable {
        delta(z, target, HistogramStatistic::Mean, 1, 1)
    }

    #[test]
    fn cells_hold_the_difference_from_the_target() {
        let afr = afr_channel("AFR", &[13.0, 15.0]);
        let target = afr_channel("AFR Target", &[14.0, 14.0]);

        let table = delta_mean(&afr, &target);

        assert!(table.is_delta());
        assert_eq!("AFR Target", table.z_compare.as_ref().unwrap().name());
        assert_close(0.0, table.values[(0, 0)].unwrap(), 6); // mean of -1 and +1
    }

    #[test]
    fn the_deviation_is_taken_per_sample_not_between_the_means() {
        // Mean AFR 14 and mean target 14 would give zero error, hiding that the
        // engine was never actually on target.
        let afr = afr_channel("AFR", &[12.0, 16.0]);
        let target = afr_channel("AFR Target", &[14.0, 14.0]);

        let worst = delta(&afr, &target, HistogramStatistic::Max, 1, 1);
        let best = delta(&afr, &target, HistogramStatistic::Min, 1, 1);

        assert_close(2.0, worst.values[(0, 0)].unwrap(), 6);
        assert_close(-2.0, best.values[(0, 0)].unwrap(), 6);
    }

    #[test]
    fn a_sample_with_no_target_reading_is_skipped() {
        let afr = afr_channel("AFR", &[13.0, 15.0, 17.0]);
        let target = afr_channel("AFR Target", &[14.0, f64::NAN, 14.0]);

        let table = delta_mean(&afr, &target);

        assert_eq!(2, table.sample_count);
        assert_close(1.0, table.values[(0, 0)].unwrap(), 6); // mean of -1 and +3
    }

    #[test]
    fn the_scale_reaches_equally_either_side_of_zero() {
        // Two cells, one -0.5 and one +2.0. The scale must reach 2.0 both ways,
        // or an equal error each side of target would shade unequally.
        let x = ch("RPM", "RPM", 0, &[1000.0, 5000.0]);
        let y = ch("MAP", "kPa", 0, &[50.0, 50.0]);
        let afr = afr_channel("AFR", &[13.5, 16.0]);
        let target = afr_channel("AFR Target", &[14.0, 14.0]);

        let table = HistogramTable::build_on_breakpoints(
            HistogramInputs::new(&x, &y, &afr).with_z_compare(Some(&target)),
            &[1000.0, 5000.0],
            &[50.0],
            0,
            1,
            HistogramStatistic::Mean,
        )
        .unwrap();

        assert_close(-0.5, table.min_value, 6);
        assert_close(2.0, table.max_value, 6);
        assert_close(2.0, table.max_deviation(), 6);
    }

    #[test]
    fn deltas_carry_an_explicit_sign() {
        let target = afr_channel("AFR Target", &[14.0]);

        let lean = delta_mean(&afr_channel("AFR", &[16.0]), &target);
        assert_eq!("+2.00", lean.format_cell(0, 0));

        let rich = delta_mean(&afr_channel("AFR", &[12.0]), &target);
        assert!(rich.format_cell(0, 0).starts_with('-'));
    }

    #[test]
    fn count_ignores_the_comparison_entirely() {
        let afr = afr_channel("AFR", &[13.0, 15.0, 16.0]);
        let target = afr_channel("AFR Target", &[14.0, 14.0, 14.0]);

        let table = delta(&afr, &target, HistogramStatistic::Count, 1, 1);

        assert_eq!("3", table.format_cell(0, 0));
    }

    #[test]
    fn without_a_comparison_nothing_changes() {
        let afr = afr_channel("AFR", &[13.0, 15.0]);
        let x = ch("RPM", "RPM", 0, &[1000.0, 1000.0]);
        let y = ch("MAP", "kPa", 0, &[50.0, 50.0]);

        let table =
            HistogramTable::build(HistogramInputs::new(&x, &y, &afr), 1, 1, 0, 1, HistogramStatistic::Mean).unwrap();

        assert!(!table.is_delta());
        assert_close(14.0, table.values[(0, 0)].unwrap(), 6);
        assert_eq!("14.00", table.format_cell(0, 0));
    }

    #[test]
    fn delta_works_on_tune_breakpoints_and_with_filters() {
        let x = ch("RPM", "RPM", 0, &[1000.0, 1000.0, 3000.0, 3000.0]);
        let y = ch("MAP", "kPa", 0, &[50.0, 50.0, 50.0, 50.0]);
        let afr = afr_channel("AFR", &[13.0, 15.0, 12.0, 16.0]);
        let target = afr_channel("AFR Target", &[14.0, 14.0, 14.0, 14.0]);
        let clt = ch("CLT", "F", 0, &[100.0, 180.0, 100.0, 180.0]);

        let d = doc(vec![x.clone(), y.clone(), afr.clone(), target.clone(), clt]);
        let warm = SampleFilter::build(&d, &[warm_filter()]);

        let table = HistogramTable::build_on_breakpoints(
            HistogramInputs::new(&x, &y, &afr)
                .with_z_compare(Some(&target))
                .with_mask(Some(&warm)),
            &[1000.0, 3000.0],
            &[50.0],
            0,
            3,
            HistogramStatistic::Mean,
        )
        .unwrap();

        assert!(table.is_delta());
        assert_eq!(2, table.sample_count);
        assert_close(1.0, table.values[(0, 0)].unwrap(), 6); // 15 - 14, warm only
        assert_close(2.0, table.values[(1, 0)].unwrap(), 6); // 16 - 14, warm only
    }

    // ---- HistogramTableTests -------------------------------------------------

    fn plain(name: &str, values: &[f64]) -> Arc<LogChannel> {
        ch(name, "", 2, values)
    }

    fn build(
        x: &Arc<LogChannel>,
        y: &Arc<LogChannel>,
        z: &Arc<LogChannel>,
        columns: usize,
        rows: usize,
        statistic: HistogramStatistic,
    ) -> HistogramTable {
        HistogramTable::build(
            HistogramInputs::new(x, y, z),
            columns,
            rows,
            0,
            x.len() as isize - 1,
            statistic,
        )
        .unwrap()
    }

    #[test]
    fn samples_land_in_the_expected_cells() {
        // Two clusters: low/low and high/high.
        let x = plain("RPM", &[1000.0, 1000.0, 6000.0, 6000.0]);
        let y = plain("MAP", &[20.0, 20.0, 200.0, 200.0]);
        let z = plain("AFR", &[14.0, 15.0, 11.0, 12.0]);

        let table = build(&x, &y, &z, 2, 2, HistogramStatistic::Mean);

        assert_eq!(Some(14.5), table.values[(0, 0)]);
        assert_eq!(Some(11.5), table.values[(1, 1)]);
        // The off-diagonal cells were never visited.
        assert_eq!(None, table.values[(0, 1)]);
        assert_eq!(None, table.values[(1, 0)]);
        assert_eq!(2, table.populated_cells);
        assert_eq!(4, table.sample_count);
    }

    #[test]
    fn each_statistic_reduces_a_cell_correctly() {
        for (statistic, expected) in [
            (HistogramStatistic::Mean, 15.0),
            (HistogramStatistic::Min, 10.0),
            (HistogramStatistic::Max, 20.0),
            (HistogramStatistic::Count, 3.0),
        ] {
            let x = plain("RPM", &[1000.0, 1000.0, 1000.0]);
            let y = plain("MAP", &[50.0, 50.0, 50.0]);
            let z = plain("AFR", &[10.0, 15.0, 20.0]);

            let table = build(&x, &y, &z, 1, 1, statistic);

            assert_eq!(Some(expected), table.values[(0, 0)], "{statistic:?}");
        }
    }

    #[test]
    fn counts_are_tracked_independently_of_the_statistic() {
        let x = plain("RPM", &[1000.0, 1000.0, 6000.0]);
        let y = plain("MAP", &[50.0, 50.0, 50.0]);
        let z = plain("AFR", &[12.0, 13.0, 14.0]);

        let table = build(&x, &y, &z, 2, 1, HistogramStatistic::Mean);

        assert_eq!(2, table.counts[(0, 0)]);
        assert_eq!(1, table.counts[(1, 0)]);
        assert_eq!(2, table.max_count);
    }

    #[test]
    fn the_maximum_sample_lands_in_the_last_bin_rather_than_overflowing() {
        // (max - min) / step lands exactly on `bins` for the top sample.
        let x = plain("RPM", &[0.0, 10.0]);
        let y = plain("MAP", &[0.0, 10.0]);
        let z = plain("AFR", &[1.0, 2.0]);

        let table = build(&x, &y, &z, 4, 4, HistogramStatistic::Mean);

        assert_eq!(Some(1.0), table.values[(0, 0)]);
        assert_eq!(Some(2.0), table.values[(3, 3)]);
        assert_eq!(2, table.sample_count);
    }

    #[test]
    fn rows_and_columns_are_centred_on_their_bins() {
        let x = plain("RPM", &[0.0, 100.0]);
        let y = plain("MAP", &[0.0, 100.0]);
        let z = plain("AFR", &[1.0, 1.0]);

        let table = build(&x, &y, &z, 2, 2, HistogramStatistic::Mean);

        assert_eq!(vec![25.0, 75.0], table.column_centers);
        assert_eq!(vec![25.0, 75.0], table.row_centers);
    }

    #[test]
    fn samples_with_a_missing_reading_are_skipped() {
        let x = plain("RPM", &[1000.0, f64::NAN, 1000.0]);
        let y = plain("MAP", &[50.0, 50.0, f64::NAN]);
        let z = plain("AFR", &[12.0, 13.0, 14.0]);

        let table = build(&x, &y, &z, 1, 1, HistogramStatistic::Mean);

        assert_eq!(1, table.sample_count);
        assert_eq!(Some(12.0), table.values[(0, 0)]);
    }

    #[test]
    fn a_constant_axis_still_places_its_samples() {
        // A zero-width axis would otherwise divide by zero.
        let x = plain("RPM", &[800.0, 800.0, 800.0]);
        let y = plain("MAP", &[40.0, 40.0, 40.0]);
        let z = plain("AFR", &[14.0, 14.5, 15.0]);

        let table = build(&x, &y, &z, 4, 4, HistogramStatistic::Mean);

        assert_eq!(3, table.sample_count);
        assert_eq!(1, table.populated_cells);
    }

    #[test]
    fn only_the_requested_sample_window_is_binned() {
        let x = plain("RPM", &[1000.0, 2000.0, 3000.0, 4000.0]);
        let y = plain("MAP", &[50.0, 50.0, 50.0, 50.0]);
        let z = plain("AFR", &[10.0, 11.0, 12.0, 13.0]);

        let table =
            HistogramTable::build(HistogramInputs::new(&x, &y, &z), 1, 1, 1, 2, HistogramStatistic::Mean).unwrap();

        assert_eq!(2, table.sample_count);
        assert_eq!(Some(11.5), table.values[(0, 0)]);
    }

    #[test]
    fn an_empty_window_produces_an_empty_table() {
        let x = plain("RPM", &[1000.0]);
        let y = plain("MAP", &[50.0]);
        let z = plain("AFR", &[12.0]);

        let table =
            HistogramTable::build(HistogramInputs::new(&x, &y, &z), 4, 4, 5, 9, HistogramStatistic::Mean).unwrap();

        assert!(table.is_empty());
        assert_eq!(0, table.sample_count);
    }

    #[test]
    fn value_range_covers_only_populated_cells() {
        let x = plain("RPM", &[1000.0, 6000.0]);
        let y = plain("MAP", &[50.0, 50.0]);
        let z = plain("AFR", &[11.0, 17.0]);

        let table = build(&x, &y, &z, 4, 1, HistogramStatistic::Mean);

        assert_eq!(11.0, table.min_value);
        assert_eq!(17.0, table.max_value);
    }

    #[test]
    fn format_uses_the_z_channel_precision() {
        let z = ch("AFR", "AFR", 2, &[14.7, 14.7]);
        let x = plain("RPM", &[1000.0, 1000.0]);
        let y = plain("MAP", &[50.0, 50.0]);
        let table =
            HistogramTable::build(HistogramInputs::new(&x, &y, &z), 1, 1, 0, 1, HistogramStatistic::Mean).unwrap();

        assert_eq!("14.70", table.format_cell(0, 0));
    }

    #[test]
    fn a_table_needs_at_least_one_bin_per_axis() {
        // The C# theory also tries -3, which a usize cannot hold; zero stands for both.
        let x = plain("RPM", &[1000.0]);
        let y = plain("MAP", &[50.0]);
        let z = plain("AFR", &[12.0]);
        let inputs = HistogramInputs::new(&x, &y, &z);

        assert!(matches!(
            HistogramTable::build(inputs, 0, 4, 0, 0, HistogramStatistic::Mean),
            Err(Error::Argument(_))
        ));
        assert!(matches!(
            HistogramTable::build(inputs, 4, 0, 0, 0, HistogramStatistic::Mean),
            Err(Error::Argument(_))
        ));
    }

    // ---- HistogramTraceBackTests ---------------------------------------------

    fn trace(name: &str, values: &[f64]) -> Arc<LogChannel> {
        ch(name, "", 1, values)
    }

    /// Two RPM columns at 1000 and 3000, one load row.
    fn table(rpm: &[f64], mask: Option<&SampleMask>) -> HistogramTable {
        let n = rpm.len();
        let (x, y, z) = (
            trace("RPM", rpm),
            trace("MAP", &repeat(50.0, n)),
            trace("AFR", &repeat(14.0, n)),
        );
        HistogramTable::build_on_breakpoints(
            HistogramInputs::new(&x, &y, &z).with_mask(mask),
            &[1000.0, 3000.0],
            &[50.0],
            0,
            n as isize - 1,
            HistogramStatistic::Mean,
        )
        .unwrap()
    }

    #[test]
    fn a_cell_reports_the_samples_that_landed_in_it() {
        let t = table(&[1000.0, 3000.0, 1000.0, 3000.0, 3000.0], None);

        assert_eq!(vec![0, 2], t.samples_in(0, 0));
        assert_eq!(vec![1, 3, 4], t.samples_in(1, 0));
    }

    #[test]
    fn samples_are_grouped_into_separate_visits() {
        // The engine passes through the same cell three times.
        let t = table(
            &[3000.0, 3000.0, 1000.0, 1000.0, 1000.0, 1000.0, 3000.0, 3000.0, 3000.0],
            None,
        );

        let visits = t.visits_to(1, 0, 2);

        assert_eq!(2, visits.len());
        assert_eq!((0, 1), visits[0]);
        assert_eq!((6, 8), visits[1]);
    }

    #[test]
    fn a_sample_or_two_of_noise_does_not_split_a_visit() {
        // Index 2 briefly leaves the cell; that is one visit, not two.
        let t = table(&[3000.0, 3000.0, 1000.0, 3000.0, 3000.0], None);

        let visits = t.visits_to(1, 0, 2);

        assert_eq!(vec![(0, 4)], visits);
    }

    #[test]
    fn a_wide_gap_does_split_a_visit() {
        let t = table(&[3000.0, 1000.0, 1000.0, 1000.0, 1000.0, 3000.0], None);

        assert_eq!(2, t.visits_to(1, 0, 2).len());
    }

    #[test]
    fn the_longest_visit_is_the_one_offered() {
        let t = table(
            &[3000.0, 1000.0, 1000.0, 1000.0, 1000.0, 3000.0, 3000.0, 3000.0, 3000.0],
            None,
        );

        assert_eq!(Some((5, 8)), t.longest_visit_to(1, 0));
    }

    #[test]
    fn an_empty_cell_has_no_visits() {
        let t = table(&[1000.0, 1000.0], None);

        assert!(t.visits_to(1, 0, 2).is_empty());
        assert_eq!(None, t.longest_visit_to(1, 0));
    }

    #[test]
    fn cells_outside_the_table_are_rejected() {
        // The C# also probes -1; a usize index cannot be negative, so only the
        // too-large ones remain.
        let t = table(&[1000.0, 3000.0], None);

        assert!(t.samples_in(0, 9).is_empty());
        assert!(t.samples_in(9, 0).is_empty());
    }

    #[test]
    fn filtered_out_samples_are_not_traced_back() {
        let rpm = trace("RPM", &[3000.0, 3000.0, 3000.0]);
        let map = trace("MAP", &[50.0, 50.0, 50.0]);
        let afr = trace("AFR", &[14.0, 14.0, 14.0]);
        let clt = trace("CLT", &[100.0, 180.0, 180.0]);

        let d = doc(vec![rpm.clone(), map.clone(), afr.clone(), clt]);
        let warm = SampleFilter::build(&d, &[warm_filter()]);

        let t = HistogramTable::build_on_breakpoints(
            HistogramInputs::new(&rpm, &map, &afr).with_mask(Some(&warm)),
            &[1000.0, 3000.0],
            &[50.0],
            0,
            2,
            HistogramStatistic::Mean,
        )
        .unwrap();

        // Sample 0 is cold, so the cell must not point back to it.
        assert_eq!(vec![1, 2], t.samples_in(1, 0));
    }

    #[test]
    fn a_sample_reports_the_cell_it_falls_in() {
        let t = table(&[1000.0, 3000.0], None);

        assert_eq!(Some((0, 0)), t.cell_of(0));
        assert_eq!(Some((1, 0)), t.cell_of(1));
        assert_eq!(None, t.cell_of(99));
    }

    #[test]
    fn trace_back_survives_the_sample_window() {
        // Built over samples 2..4 only; earlier matches must not be reported.
        let (x, y, z) = (
            trace("RPM", &repeat(3000.0, 5)),
            trace("MAP", &repeat(50.0, 5)),
            trace("AFR", &repeat(14.0, 5)),
        );
        let t = HistogramTable::build_on_breakpoints(
            HistogramInputs::new(&x, &y, &z),
            &[1000.0, 3000.0],
            &[50.0],
            2,
            4,
            HistogramStatistic::Mean,
        )
        .unwrap();

        assert_eq!(vec![2, 3, 4], t.samples_in(1, 0));
    }

    // ----- the other direction: a marked span to the cells it reached --------

    #[test]
    fn a_marked_span_reports_the_cells_it_passed_through() {
        let t = table(&[1000.0, 1000.0, 3000.0, 3000.0, 1000.0], None);

        let visits = t.visited_by(0, 4);

        assert_eq!(2, visits.cells);
        assert_eq!(5, visits.samples);
        assert_eq!(3, visits.counts[(0, 0)]);
        assert_eq!(2, visits.counts[(1, 0)]);
        assert!(visits.visited(0, 0));
        assert!(visits.visited(1, 0));
    }

    #[test]
    fn a_span_over_one_cell_reaches_only_that_cell() {
        let t = table(&[1000.0, 1000.0, 3000.0, 3000.0, 1000.0], None);

        let visits = t.visited_by(2, 3);

        assert_eq!(1, visits.cells);
        assert!(!visits.visited(0, 0));
        assert!(visits.visited(1, 0));
    }

    #[test]
    fn the_span_is_read_the_same_way_round_whichever_end_is_given_first() {
        let t = table(&[1000.0, 1000.0, 3000.0, 3000.0, 1000.0], None);

        assert_eq!(t.visited_by(0, 4).cells, t.visited_by(4, 0).cells);
        assert_eq!(t.visited_by(0, 4).samples, t.visited_by(4, 0).samples);
    }

    #[test]
    fn samples_a_filter_excluded_are_counted_as_outside_rather_than_placed() {
        // The table was built without them, so marking their cells would claim
        // evidence the table does not rest on.
        let mask = SampleMask::new(vec![true, true, false, false, false], 2, 5, vec![], true);

        let t = table(&[1000.0, 1000.0, 3000.0, 3000.0, 1000.0], Some(&mask));

        let visits = t.visited_by(0, 4);

        assert_eq!(1, visits.cells);
        assert_eq!(2, visits.samples);
        assert_eq!(3, visits.outside);
    }

    #[test]
    fn a_span_outside_the_tables_window_reaches_nothing_and_says_so() {
        // The table covers samples 0..2; the span is past its end.
        let (x, y, z) = (
            trace("RPM", &[1000.0, 3000.0, 1000.0, 3000.0, 1000.0]),
            trace("MAP", &repeat(50.0, 5)),
            trace("AFR", &repeat(14.0, 5)),
        );
        let t = HistogramTable::build_on_breakpoints(
            HistogramInputs::new(&x, &y, &z),
            &[1000.0, 3000.0],
            &[50.0],
            0,
            2,
            HistogramStatistic::Mean,
        )
        .unwrap();

        let visits = t.visited_by(3, 4);

        assert!(visits.is_empty());
        assert_eq!(0, visits.samples);
        assert_eq!(2, visits.outside);
    }

    #[test]
    fn a_cell_traced_back_to_the_log_marks_that_same_cell_coming_forward() {
        // The round trip: click a cell, get its longest visit, mark that span,
        // and the span must land back in the cell it came from.
        let t = table(
            &[3000.0, 3000.0, 1000.0, 1000.0, 1000.0, 1000.0, 3000.0, 3000.0, 3000.0],
            None,
        );

        let (first, last) = t.longest_visit_to(1, 0).unwrap();
        let visits = t.visited_by(first as isize, last as isize);

        assert!(visits.visited(1, 0));
        assert_eq!(1, visits.cells);
    }

    #[test]
    fn visited_is_false_outside_the_table_rather_than_throwing() {
        let visits = table(&[1000.0, 3000.0], None).visited_by(0, 1);

        assert!(!visits.visited(-1, 0));
        assert!(!visits.visited(0, -1));
        assert!(!visits.visited(99, 0));
        assert!(!visits.visited(0, 99));
    }

    // ---- LogFilterTests (the histogram pair) ----------------------------------

    fn filter_log(channels: &[(&str, [f64; 4])]) -> LogDocument {
        doc(channels.iter().map(|(n, v)| ch(n, "", 1, v)).collect())
    }

    #[test]
    fn the_histogram_counts_only_samples_that_pass() {
        let log = filter_log(&[
            ("RPM", [1000.0, 1000.0, 1000.0, 1000.0]),
            ("MAP", [50.0, 50.0, 50.0, 50.0]),
            ("AFR", [10.0, 20.0, 10.0, 20.0]),
            ("CLT", [100.0, 100.0, 180.0, 180.0]),
        ]);

        let warm = SampleFilter::build(&log, &[warm_filter()]);
        let (x, y, z) = (
            log.find_channel("RPM").unwrap(),
            log.find_channel("MAP").unwrap(),
            log.find_channel("AFR").unwrap(),
        );

        let all = HistogramTable::build(HistogramInputs::new(x, y, z), 1, 1, 0, 3, HistogramStatistic::Mean).unwrap();
        let hot = HistogramTable::build(
            HistogramInputs::new(x, y, z).with_mask(Some(&warm)),
            1,
            1,
            0,
            3,
            HistogramStatistic::Mean,
        )
        .unwrap();

        assert_eq!(4, all.sample_count);
        assert_eq!(Some(15.0), all.values[(0, 0)]); // mean of 10,20,10,20

        assert_eq!(2, hot.sample_count);
        assert_eq!(Some(15.0), hot.values[(0, 0)]); // mean of the warm pair, 10 and 20
    }

    #[test]
    fn filtering_also_tightens_the_axes_onto_what_remains() {
        let log = filter_log(&[
            ("RPM", [500.0, 1000.0, 5000.0, 6000.0]),
            ("MAP", [20.0, 30.0, 90.0, 100.0]),
            ("AFR", [14.0, 14.0, 12.0, 12.0]),
            ("CLT", [100.0, 100.0, 180.0, 180.0]),
        ]);

        let warm = SampleFilter::build(&log, &[warm_filter()]);

        let hot = HistogramTable::build(
            HistogramInputs::new(
                log.find_channel("RPM").unwrap(),
                log.find_channel("MAP").unwrap(),
                log.find_channel("AFR").unwrap(),
            )
            .with_mask(Some(&warm)),
            2,
            1,
            0,
            3,
            HistogramStatistic::Mean,
        )
        .unwrap();

        // Axis spans 5000..6000, not 500..6000, so bin centres are 5250 and 5750.
        assert_eq!(vec![5250.0, 5750.0], hot.column_centers);
    }
}
