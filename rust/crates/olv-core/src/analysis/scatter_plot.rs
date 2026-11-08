//! Ported from `ScatterPlot.cs`. Owned by package `w3-analysis`.
//!
//! Every sample of a log placed at its own X and Y, coloured by a third channel: the same
//! three channels the heat table bins, drawn without the binning.
//!
//! The table and this answer different questions about the same data. A table says what a
//! region of the map averaged, which is what you edit a tuning table against. It cannot say
//! whether the twelve samples behind a cell agreed with each other. That distinction is the
//! whole of what a scatter is for: structure and spread inside what a table has already
//! averaged away.
//!
//! **Overplotting is the thing that makes a naive scatter lie.** A drive is tens or hundreds
//! of thousands of samples, and an engine spends most of a drive in a small part of the map.
//! Drawn one mark per sample they land on top of one another thousands deep, and the colour
//! that survives is whichever sample was drawn last. So the points are aggregated onto the
//! display's own grid before anything is drawn, see [`ScatterPlot::bin`]. Every mark is then
//! the mean of what actually landed under it, and it carries the count and the spread behind
//! it.

use std::cmp::Ordering;
use std::sync::Arc;

use crate::filters::log_filter::SampleMask;
use crate::model::LogChannel;
use crate::{Error, Result};

pub struct ScatterPlot {
    /// X of each surviving point. Valid up to [`count`](Self::count).
    pub xs: Vec<f64>,
    pub ys: Vec<f64>,
    /// Z of each point: already the deviation from `z_compare` where one is set, taken per
    /// sample as the table takes it.
    pub zs: Vec<f64>,
    /// Index back into the log for each point, so a mark can be traced.
    pub samples: Vec<usize>,
    pub count: usize,
    /// Samples in range that were dropped because one of the three channels had no reading
    /// there. Reported rather than absorbed.
    pub dropped: usize,
    /// Samples excluded by the filters, for the same reason.
    pub filtered: usize,
    pub x: Arc<LogChannel>,
    pub y: Arc<LogChannel>,
    pub z: Arc<LogChannel>,
    /// When set, `zs` holds Z minus this channel.
    pub z_compare: Option<Arc<LogChannel>>,
    pub x_min: f64,
    pub x_max: f64,
    pub y_min: f64,
    pub y_max: f64,
    pub z_min: f64,
    pub z_max: f64,
}

impl ScatterPlot {
    pub fn is_delta(&self) -> bool {
        self.z_compare.is_some()
    }

    pub fn is_empty(&self) -> bool {
        self.count == 0
    }

    /// Largest deviation either side of zero, for a diverging scale that has to be symmetric
    /// about it.
    pub fn max_deviation(&self) -> f64 {
        self.z_min.abs().max(self.z_max.abs())
    }

    /// Collects the samples in a window that have a reading on all three channels and survive
    /// the filters. `first_sample` and `last_sample` are inclusive.
    pub fn build(
        x: &Arc<LogChannel>,
        y: &Arc<LogChannel>,
        z: &Arc<LogChannel>,
        first_sample: isize,
        last_sample: isize,
        mask: Option<&SampleMask>,
        z_compare: Option<&Arc<LogChannel>>,
    ) -> Self {
        let length = x.len().min(y.len().min(z.len())) as isize;
        let from = 0.max(first_sample.min(last_sample));
        let to = (length - 1).min(first_sample.max(last_sample));

        let capacity = 0.max(to - from + 1) as usize;
        let mut plot = Self {
            xs: Vec::with_capacity(capacity),
            ys: Vec::with_capacity(capacity),
            zs: Vec::with_capacity(capacity),
            samples: Vec::with_capacity(capacity),
            count: 0,
            dropped: 0,
            filtered: 0,
            x: Arc::clone(x),
            y: Arc::clone(y),
            z: Arc::clone(z),
            z_compare: z_compare.cloned(),
            x_min: 0.0,
            x_max: 0.0,
            y_min: 0.0,
            y_max: 0.0,
            z_min: 0.0,
            z_max: 0.0,
        };

        if from > to {
            plot
        } else {
            plot.collect(from as usize, to as usize, mask);
            plot
        }
    }

    fn collect(&mut self, from: usize, to: usize, mask: Option<&SampleMask>) {
        let (mut x_min, mut x_max) = (f64::INFINITY, f64::NEG_INFINITY);
        let (mut y_min, mut y_max) = (f64::INFINITY, f64::NEG_INFINITY);
        let (mut z_min, mut z_max) = (f64::INFINITY, f64::NEG_INFINITY);

        for i in from..=to {
            if mask.is_some_and(|m| !m.is_accepted(i)) {
                self.filtered += 1;
                continue;
            }

            let (xv, yv, mut zv) = (self.x.at(i), self.y.at(i), self.z.at(i));
            if xv.is_nan() || yv.is_nan() || zv.is_nan() {
                self.dropped += 1;
                continue;
            }

            if let Some(compare) = &self.z_compare {
                let target = compare.at(i);
                if target.is_nan() {
                    self.dropped += 1;
                    continue;
                }
                zv -= target;
            }

            self.xs.push(xv);
            self.ys.push(yv);
            self.zs.push(zv);
            self.samples.push(i);

            if xv < x_min {
                x_min = xv;
            }
            if xv > x_max {
                x_max = xv;
            }
            if yv < y_min {
                y_min = yv;
            }
            if yv > y_max {
                y_max = yv;
            }
            if zv < z_min {
                z_min = zv;
            }
            if zv > z_max {
                z_max = zv;
            }
        }

        self.count = self.xs.len();
        if self.count == 0 {
            return;
        }

        self.x_min = x_min;
        self.x_max = x_max;
        self.y_min = y_min;
        self.y_max = y_max;
        self.z_min = z_min;
        self.z_max = z_max;
    }

    /// Aggregates the points onto a `columns` x `rows` grid spanning the data, which is how
    /// the view turns them into marks it can draw.
    ///
    /// The grid spans the observed range exactly, so the extreme samples land in the end
    /// blocks rather than half outside them.
    ///
    /// # Errors
    /// [`Error::Argument`] when `columns` or `rows` is zero, as the C# throws.
    pub fn bin(&self, columns: usize, rows: usize) -> Result<ScatterBins> {
        if columns < 1 {
            return Err(Error::Argument("columns must be at least 1".into()));
        }
        if rows < 1 {
            return Err(Error::Argument("rows must be at least 1".into()));
        }

        let mut bins = ScatterBins::new(columns, rows, self.x_min, self.x_max, self.y_min, self.y_max);
        if self.count == 0 {
            return Ok(bins);
        }

        for i in 0..self.count {
            bins.add(
                block_of(self.xs[i], self.x_min, self.x_max, columns),
                block_of(self.ys[i], self.y_min, self.y_max, rows),
                self.zs[i],
            );
        }

        Ok(bins.finish())
    }

    /// Every sample that landed in one block, in log order: what a click on a mark has to
    /// trace back to.
    ///
    /// Recomputed rather than stored: a grid the size of a window holds tens of thousands of
    /// blocks, nearly all of them never clicked.
    pub fn samples_in(&self, bins: &ScatterBins, column: usize, row: usize) -> Vec<usize> {
        let mut found = Vec::new();

        for i in 0..self.count {
            if block_of(self.xs[i], self.x_min, self.x_max, bins.columns) != column {
                continue;
            }
            if block_of(self.ys[i], self.y_min, self.y_max, bins.rows) != row {
                continue;
            }
            found.push(self.samples[i]);
        }

        found
    }

    /// Contiguous runs of samples, so a click on a mark is traced back the way a table cell
    /// is: an engine passes through the same point on the map many times in a drive, and the
    /// span from the first sample to the last would cover most of the recording.
    pub fn visits_among(samples: &[usize], gap_tolerance: usize) -> Vec<(usize, usize)> {
        let mut visits = Vec::new();
        let Some(&first) = samples.first() else {
            return visits;
        };

        let (mut start, mut previous) = (first, first);

        for &s in &samples[1..] {
            // A sample or two of noise should not split one visit in half.
            if s as isize - previous as isize <= gap_tolerance as isize + 1 {
                previous = s;
                continue;
            }

            visits.push((start, previous));
            start = s;
            previous = s;
        }

        visits.push((start, previous));
        visits
    }
}

/// Which block a value falls in. A channel that never moved has no range to divide, so
/// everything goes to the middle rather than to an edge, which would draw a flat channel as a
/// line pinned against one side.
pub(crate) fn block_of(value: f64, min: f64, max: f64, count: usize) -> usize {
    if max <= min {
        return count / 2;
    }

    let block = ((value - min) / (max - min) * count as f64) as isize;
    block.clamp(0, count as isize - 1) as usize
}

/// The points of a [`ScatterPlot`] reduced to one mark per block of the display grid: what is
/// drawn, and what a hover reports.
pub struct ScatterBins {
    sums: Vec<f64>,
    pub columns: usize,
    pub rows: usize,
    pub x_min: f64,
    pub x_max: f64,
    pub y_min: f64,
    pub y_max: f64,
    /// Samples in each block, indexed `row * columns + column`.
    pub counts: Vec<usize>,
    /// Mean Z of each block. Meaningless where the count is zero.
    pub means: Vec<f64>,
    /// Lowest Z in each block.
    pub lowest: Vec<f64>,
    /// Highest Z in each block.
    pub highest: Vec<f64>,
    /// Blocks that got at least one sample: the marks to draw.
    pub occupied: usize,
    /// Most samples in any one block, which is what density is shaded against.
    pub busiest: usize,
    /// Full range of the block means: the extremes actually measured.
    pub mean_min: f64,
    pub mean_max: f64,
    /// The range colour is scaled over, which is deliberately not the full one: the ends are
    /// trimmed to the [`TRIM`] percentiles of the occupied blocks and anything past them
    /// saturates, so a few transient blocks do not own the whole ramp. Over blocks rather than
    /// over samples, because it is blocks that are being coloured.
    pub color_low: f64,
    pub color_high: f64,
}

impl ScatterBins {
    /// How much of each end is allowed to saturate. Two per cent: enough to discount the
    /// handful of blocks a transient leaves behind, small enough that a genuinely wide spread
    /// still scales over its own width.
    pub const TRIM: f64 = 0.02;

    fn new(columns: usize, rows: usize, x_min: f64, x_max: f64, y_min: f64, y_max: f64) -> Self {
        let n = columns * rows;
        Self {
            sums: vec![0.0; n],
            columns,
            rows,
            x_min,
            x_max,
            y_min,
            y_max,
            counts: vec![0; n],
            means: vec![0.0; n],
            lowest: vec![0.0; n],
            highest: vec![0.0; n],
            occupied: 0,
            busiest: 0,
            mean_min: 0.0,
            mean_max: 0.0,
            color_low: 0.0,
            color_high: 0.0,
        }
    }

    /// How far a bound must move before the move is worth reporting: comfortably above what
    /// the trim costs an evenly spread channel, which is about [`TRIM`](Self::TRIM) of its
    /// range.
    fn material(&self) -> f64 {
        (self.mean_max - self.mean_min) * 0.05
    }

    /// True when the scale stops meaningfully short of the lowest value measured.
    pub fn clips_low(&self) -> bool {
        self.color_low - self.mean_min > self.material()
    }

    /// True when the scale stops meaningfully short of the highest value measured.
    pub fn clips_high(&self) -> bool {
        self.mean_max - self.color_high > self.material()
    }

    /// Symmetric limit for a diverging scale, on whatever basis the bounds ended up on.
    pub fn mean_extent(&self) -> f64 {
        self.color_low.abs().max(self.color_high.abs())
    }

    pub fn index(&self, column: usize, row: usize) -> usize {
        row * self.columns + column
    }

    fn add(&mut self, column: usize, row: usize, z: f64) {
        let i = self.index(column, row);

        if self.counts[i] == 0 {
            self.lowest[i] = z;
            self.highest[i] = z;
        } else {
            if z < self.lowest[i] {
                self.lowest[i] = z;
            }
            if z > self.highest[i] {
                self.highest[i] = z;
            }
        }

        self.sums[i] += z;
        self.counts[i] += 1;
    }

    fn finish(mut self) -> Self {
        let (mut low, mut high) = (f64::INFINITY, f64::NEG_INFINITY);

        for i in 0..self.counts.len() {
            if self.counts[i] == 0 {
                continue;
            }

            self.means[i] = self.sums[i] / self.counts[i] as f64;
            self.occupied += 1;

            if self.counts[i] > self.busiest {
                self.busiest = self.counts[i];
            }
            if self.means[i] < low {
                low = self.means[i];
            }
            if self.means[i] > high {
                high = self.means[i];
            }
        }

        if self.occupied == 0 {
            return self;
        }

        self.mean_min = low;
        self.mean_max = high;

        self.color_low = self.mean_min;
        self.color_high = self.mean_max;

        // Trimmed only where the trim would actually discount a whole block. Below that it
        // has no outlier to remove and merely interpolates between the two extreme values.
        if self.occupied as f64 * Self::TRIM < 1.0 {
            return self;
        }

        let mut sorted: Vec<f64> = Vec::with_capacity(self.occupied);
        for i in 0..self.counts.len() {
            if self.counts[i] > 0 {
                sorted.push(self.means[i]);
            }
        }

        // `Array.Sort` on doubles puts NaN first.
        sorted.sort_by(|a, b| match (a.is_nan(), b.is_nan()) {
            (true, true) => Ordering::Equal,
            (true, false) => Ordering::Less,
            (false, true) => Ordering::Greater,
            (false, false) => a.partial_cmp(b).unwrap_or(Ordering::Equal),
        });

        let trimmed_low = percentile(&sorted, Self::TRIM);
        let trimmed_high = percentile(&sorted, 1.0 - Self::TRIM);

        // A trim that closes the range entirely has nothing left to scale over. The full
        // range is then the only scale there is.
        if trimmed_high <= trimmed_low {
            return self;
        }

        self.color_low = trimmed_low;
        self.color_high = trimmed_high;
        self
    }

    /// How far apart the readings inside one block were. Zero for a block one sample deep.
    ///
    /// Worth reporting because it is the one thing a mean hides: a block whose samples ran
    /// from rich to lean shades identically to one that sat on target throughout.
    pub fn spread_in(&self, column: usize, row: usize) -> f64 {
        let i = self.index(column, row);
        if self.counts[i] == 0 {
            0.0
        } else {
            self.highest[i] - self.lowest[i]
        }
    }
}

/// Linear-interpolated percentile of an ascending array.
fn percentile(ascending: &[f64], fraction: f64) -> f64 {
    if ascending.len() == 1 {
        return ascending[0];
    }

    let position = fraction * (ascending.len() - 1) as f64;
    let index = position as usize;
    if index >= ascending.len() - 1 {
        return ascending[ascending.len() - 1];
    }

    let f = position - index as f64;
    ascending[index] + ((ascending[index + 1] - ascending[index]) * f)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn channel(name: &str, values: &[f64]) -> Arc<LogChannel> {
        Arc::new(LogChannel::new(name, "", 2, values, false))
    }

    /// xunit's `Assert.Equal(expected, actual, precision)`.
    fn near(expected: f64, actual: f64, precision: i32) {
        let scale = 10f64.powi(precision);
        assert_eq!(
            (expected * scale).round_ties_even(),
            (actual * scale).round_ties_even(),
            "{expected} vs {actual}"
        );
    }

    fn build(
        x: &Arc<LogChannel>,
        y: &Arc<LogChannel>,
        z: &Arc<LogChannel>,
        mask: Option<&SampleMask>,
        compare: Option<&Arc<LogChannel>>,
    ) -> ScatterPlot {
        ScatterPlot::build(x, y, z, 0, x.len() as isize - 1, mask, compare)
    }

    fn mask(accepted: Vec<bool>) -> SampleMask {
        let total = accepted.len();
        let pass = accepted.iter().filter(|&&a| a).count();
        SampleMask::new(accepted, pass, total, Vec::new(), true)
    }

    #[test]
    fn every_sample_with_three_readings_becomes_a_point() {
        let x = channel("RPM", &[1000.0, 2000.0, 3000.0]);
        let y = channel("MAP", &[30.0, 60.0, 90.0]);
        let z = channel("AFR", &[14.7, 13.2, 11.8]);

        let plot = build(&x, &y, &z, None, None);

        assert_eq!(plot.count, 3);
        assert_eq!(plot.xs[..3], [1000.0, 2000.0, 3000.0]);
        assert_eq!(plot.ys[..3], [30.0, 60.0, 90.0]);
        near(14.7, plot.zs[0], 4);
        near(13.2, plot.zs[1], 4);
        near(11.8, plot.zs[2], 4);
        assert_eq!(plot.samples[..3], [0, 1, 2]);
    }

    #[test]
    fn ranges_cover_the_points_that_survived() {
        let x = channel("RPM", &[1000.0, 6500.0, 3000.0]);
        let y = channel("MAP", &[90.0, 30.0, 60.0]);
        let z = channel("AFR", &[11.8, 14.7, 13.2]);

        let plot = build(&x, &y, &z, None, None);

        assert_eq!(plot.x_min, 1000.0);
        assert_eq!(plot.x_max, 6500.0);
        assert_eq!(plot.y_min, 30.0);
        assert_eq!(plot.y_max, 90.0);
        near(11.8, plot.z_min, 4);
        near(14.7, plot.z_max, 4);
    }

    #[test]
    fn a_sample_missing_any_of_the_three_is_dropped_and_counted() {
        let n = f64::NAN;
        let x = channel("RPM", &[1000.0, n, 3000.0, 4000.0]);
        let y = channel("MAP", &[30.0, 60.0, n, 90.0]);
        let z = channel("AFR", &[14.7, 13.2, 11.8, n]);

        let plot = build(&x, &y, &z, None, None);

        assert_eq!(plot.count, 1);
        assert_eq!(plot.dropped, 3);
        assert_eq!(plot.filtered, 0);
    }

    #[test]
    fn filtered_samples_are_counted_apart_from_missing_ones() {
        let x = channel("RPM", &[1000.0, 2000.0, 3000.0, 4000.0]);
        let y = channel("MAP", &[30.0, 60.0, 90.0, 100.0]);
        let z = channel("AFR", &[14.7, f64::NAN, 11.8, 12.0]);
        let m = mask(vec![true, true, false, true]);

        let plot = build(&x, &y, &z, Some(&m), None);

        assert_eq!(plot.count, 2);
        assert_eq!(plot.filtered, 1);
        assert_eq!(plot.dropped, 1);
    }

    #[test]
    fn a_deviation_is_taken_per_sample_rather_than_from_the_means() {
        let x = channel("RPM", &[1000.0, 1000.0]);
        let y = channel("MAP", &[50.0, 50.0]);
        let z = channel("AFR", &[12.0, 14.0]);
        let target = channel("AFR Target", &[13.0, 13.0]);

        let plot = build(&x, &y, &z, None, Some(&target));

        assert!(plot.is_delta());
        near(-1.0, plot.zs[0], 4);
        near(1.0, plot.zs[1], 4);
        near(1.0, plot.max_deviation(), 4);
    }

    #[test]
    fn a_sample_with_no_target_is_dropped_rather_than_compared_against_nothing() {
        let x = channel("RPM", &[1000.0, 1000.0]);
        let y = channel("MAP", &[50.0, 50.0]);
        let z = channel("AFR", &[12.0, 14.0]);
        let target = channel("AFR Target", &[13.0, f64::NAN]);

        let plot = build(&x, &y, &z, None, Some(&target));

        assert_eq!(plot.count, 1);
        assert_eq!(plot.dropped, 1);
    }

    #[test]
    fn the_window_is_honoured_and_clamped_to_the_shortest_channel() {
        let x = channel("RPM", &[1000.0, 2000.0, 3000.0, 4000.0, 5000.0]);
        let y = channel("MAP", &[30.0, 40.0, 50.0, 60.0, 70.0]);
        let z = channel("AFR", &[10.0, 11.0, 12.0]);

        let plot = ScatterPlot::build(&x, &y, &z, 1, 99, None, None);

        assert_eq!(plot.count, 2);
        assert_eq!(plot.samples[..2], [1, 2]);
    }

    #[test]
    fn an_empty_window_produces_an_empty_plot_rather_than_throwing() {
        let x = channel("RPM", &[1000.0]);
        let y = channel("MAP", &[30.0]);
        let z = channel("AFR", &[14.0]);

        let plot = ScatterPlot::build(&x, &y, &z, 5, 9, None, None);

        assert!(plot.is_empty());
        assert_eq!(plot.bin(4, 4).unwrap().occupied, 0);
    }

    // ----- binning ----------------------------------------------------------

    #[test]
    fn points_land_in_the_block_their_position_puts_them() {
        let x = channel("RPM", &[0.0, 0.0, 100.0, 100.0]);
        let y = channel("MAP", &[0.0, 100.0, 0.0, 100.0]);
        let z = channel("AFR", &[1.0, 2.0, 3.0, 4.0]);

        let bins = build(&x, &y, &z, None, None).bin(2, 2).unwrap();

        assert_eq!(bins.occupied, 4);
        assert_eq!(bins.means[bins.index(0, 0)], 1.0);
        assert_eq!(bins.means[bins.index(0, 1)], 2.0);
        assert_eq!(bins.means[bins.index(1, 0)], 3.0);
        assert_eq!(bins.means[bins.index(1, 1)], 4.0);
    }

    #[test]
    fn a_block_holds_the_mean_of_what_landed_in_it_rather_than_the_last_to_arrive() {
        // The whole point of binning: drawn one mark per sample, this block would take the
        // colour of whichever sample happened to be drawn last.
        let x = channel("RPM", &[1000.0; 4]);
        let y = channel("MAP", &[50.0; 4]);
        let z = channel("AFR", &[10.0, 12.0, 14.0, 16.0]);

        let bins = build(&x, &y, &z, None, None).bin(4, 4).unwrap();
        let occupied = bins.counts.iter().position(|&c| c > 0).unwrap();

        assert_eq!(bins.occupied, 1);
        assert_eq!(bins.counts[occupied], 4);
        near(13.0, bins.means[occupied], 4);
        assert_eq!(bins.busiest, 4);
    }

    #[test]
    fn a_block_reports_the_spread_its_mean_hides() {
        // Six rich and six lean average to target. A table shades that cell as if it were on
        // target; the spread is the only thing that says otherwise.
        let readings = [11.0, 11.0, 11.0, 15.0, 15.0, 15.0];
        let x = channel("RPM", &[1000.0; 6]);
        let y = channel("MAP", &[50.0; 6]);
        let z = channel("AFR", &readings);

        let bins = build(&x, &y, &z, None, None).bin(2, 2).unwrap();
        let occupied = bins.counts.iter().position(|&c| c > 0).unwrap();

        near(13.0, bins.means[occupied], 4);
        near(4.0, bins.spread_in(occupied % bins.columns, occupied / bins.columns), 4);
    }

    #[test]
    fn a_single_sample_block_has_no_spread() {
        let x = channel("RPM", &[1000.0, 6000.0]);
        let y = channel("MAP", &[30.0, 90.0]);
        let z = channel("AFR", &[14.0, 11.0]);

        let bins = build(&x, &y, &z, None, None).bin(2, 2).unwrap();

        assert_eq!(bins.spread_in(0, 0), 0.0);
        assert_eq!(bins.busiest, 1);
    }

    #[test]
    fn the_extreme_samples_land_inside_the_grid_rather_than_past_its_edge() {
        let x = channel("RPM", &[500.0, 7000.0]);
        let y = channel("MAP", &[10.0, 250.0]);
        let z = channel("AFR", &[14.0, 11.0]);

        let bins = build(&x, &y, &z, None, None).bin(8, 8).unwrap();

        assert_eq!(bins.counts[bins.index(0, 0)], 1);
        assert_eq!(bins.counts[bins.index(7, 7)], 1);
        assert_eq!(bins.occupied, 2);
    }

    #[test]
    fn a_flat_channel_is_placed_in_the_middle_rather_than_pinned_to_an_edge() {
        let x = channel("RPM", &[1000.0, 2000.0, 3000.0]);
        let y = channel("MAP", &[50.0, 50.0, 50.0]);
        let z = channel("AFR", &[14.0, 13.0, 12.0]);

        let bins = build(&x, &y, &z, None, None).bin(3, 3).unwrap();

        for column in 0..3 {
            assert_eq!(bins.counts[bins.index(column, 1)], 1);
        }
    }

    #[test]
    fn block_mean_range_is_over_blocks_rather_than_over_samples() {
        // One block averages 10 and 20 to 15, so no block is as extreme as the rawest sample:
        // colour is scaled over what is drawn.
        let x = channel("RPM", &[0.0, 0.0, 100.0]);
        let y = channel("MAP", &[0.0, 0.0, 100.0]);
        let z = channel("AFR", &[10.0, 20.0, 18.0]);

        let bins = build(&x, &y, &z, None, None).bin(2, 2).unwrap();

        near(15.0, bins.mean_min, 4);
        near(18.0, bins.mean_max, 4);
        near(10.0, build(&x, &y, &z, None, None).z_min, 4);
    }

    #[test]
    fn binning_is_bounded_by_the_grid_rather_than_by_the_log() {
        // Fifty thousand samples over a grid of a hundred blocks.
        let mut state: u64 = 7;
        let mut next = move || {
            state = state
                .wrapping_mul(6364136223846793005)
                .wrapping_add(1442695040888963407);
            (state >> 11) as f64 / (1u64 << 53) as f64
        };
        let n = 50_000;
        let (mut xs, mut ys, mut zs) = (Vec::new(), Vec::new(), Vec::new());
        for _ in 0..n {
            xs.push(next() * 7000.0);
            ys.push(next() * 250.0);
            zs.push(10.0 + next() * 6.0);
        }

        let bins = build(
            &channel("RPM", &xs),
            &channel("MAP", &ys),
            &channel("AFR", &zs),
            None,
            None,
        )
        .bin(10, 10)
        .unwrap();

        assert_eq!(bins.occupied, 100);
        assert_eq!(bins.counts.iter().sum::<usize>(), 50_000);
    }

    // ----- the colour scale -------------------------------------------------

    /// A block grid where one block per column holds `means` in order: one occupied block per
    /// value, which is what the trim works over.
    fn bins_holding(means: &[f64]) -> ScatterBins {
        let xs: Vec<f64> = (0..means.len()).map(|i| i as f64).collect();
        build(
            &channel("RPM", &xs),
            &channel("MAP", &xs),
            &channel("AFR", means),
            None,
            None,
        )
        .bin(means.len(), means.len())
        .unwrap()
    }

    #[test]
    fn one_outlier_does_not_own_the_whole_colour_scale() {
        // Ninety-eight blocks of closed-loop running and two transients. The full range is
        // 8.6 to 18; the scale the marks are drawn on must not be.
        let mut means: Vec<f64> = (0..98).map(|i| 14.5 + (i % 5) as f64 * 0.1).collect();
        means.push(8.6);
        means.push(18.0);

        let bins = bins_holding(&means);

        near(8.6, bins.mean_min, 3);
        near(18.0, bins.mean_max, 3);

        // The scale now covers the band the engine actually ran in.
        assert!(bins.color_low > 13.0, "low bound was {}", bins.color_low);
        assert!(bins.color_high < 15.5, "high bound was {}", bins.color_high);
        assert!(bins.clips_low());
        assert!(bins.clips_high());
    }

    #[test]
    fn a_genuinely_wide_spread_is_not_trimmed_away_to_nothing() {
        // Values spread evenly across their range: the trim should take a sliver, not reduce
        // the scale to the middle of the data.
        let means: Vec<f64> = (0..100).map(f64::from).collect();

        let bins = bins_holding(&means);

        assert!(bins.color_low <= 3.0, "low bound was {}", bins.color_low);
        assert!(bins.color_high >= 96.0, "high bound was {}", bins.color_high);
    }

    #[test]
    fn too_few_blocks_to_trim_falls_back_to_the_full_range() {
        let bins = bins_holding(&[11.0, 15.0]);

        near(bins.mean_min, bins.color_low, 6);
        near(bins.mean_max, bins.color_high, 6);
        assert!(!bins.clips_low());
        assert!(!bins.clips_high());
    }

    #[test]
    fn a_channel_that_barely_moved_keeps_a_scale_rather_than_collapsing() {
        let bins = bins_holding(&[14.7, 14.7, 14.7, 14.7]);

        assert!(!bins.clips_low());
        assert!(!bins.clips_high());
        near(bins.color_low, bins.color_high, 6);
    }

    #[test]
    fn the_scale_is_over_blocks_rather_than_weighted_by_sample_count() {
        // One block visited five hundred times at 14.0 and ninety-nine visited once each
        // across 10..19. Weighted by samples, idle would decide the scale for the whole map;
        // over blocks it is one block among a hundred.
        let (mut xs, mut zs) = (Vec::new(), Vec::new());
        for _ in 0..500 {
            xs.push(0.0);
            zs.push(14.0);
        }
        for i in 1..100 {
            xs.push(f64::from(i));
            zs.push(10.0 + (f64::from(i) / 11.0));
        }

        let bins = build(
            &channel("RPM", &xs),
            &channel("MAP", &xs),
            &channel("AFR", &zs),
            None,
            None,
        )
        .bin(100, 100)
        .unwrap();

        assert_eq!(bins.busiest, 500);
        assert!(bins.color_high > 18.0, "high bound was {}", bins.color_high);
    }

    #[test]
    fn a_diverging_scale_takes_its_reach_from_the_trimmed_bounds_too() {
        // A drive mostly within two points of target, plus one accel-enrichment event either
        // way. Untrimmed the reach is 20 and the whole drive draws within a tenth of neutral.
        let mut means: Vec<f64> = (0..98).map(|i| -2.0 + (f64::from(i) * 4.0 / 97.0)).collect();
        means.push(-20.0);
        means.push(20.0);

        let bins = bins_holding(&means);

        near(20.0, bins.mean_max, 3);

        // Both sides: a reach of zero would also be "less than 3", and is the one value that
        // draws every mark neutral however far off target it is.
        assert!((1.0..=3.0).contains(&bins.mean_extent()), "{}", bins.mean_extent());
    }

    #[test]
    fn a_bound_the_trim_barely_moved_is_not_reported_as_a_clip() {
        // An evenly spread channel: the trim shaves a sliver off each end, which is not a clip
        // worth putting a >= in front of.
        let means: Vec<f64> = (0..100).map(f64::from).collect();

        let bins = bins_holding(&means);

        assert!(
            bins.color_low > bins.mean_min,
            "the trim should still have moved the bound"
        );
        assert!(!bins.clips_low());
        assert!(!bins.clips_high());
    }

    #[test]
    fn a_trim_that_would_close_the_range_is_not_taken() {
        // Ninety-eight blocks on exactly one value: there is no middle to scale over, so the
        // full range stands and the two outliers keep their ends.
        let mut means = vec![0.1; 98];
        means.push(-9.0);
        means.push(9.0);

        let bins = bins_holding(&means);

        near(-9.0, bins.color_low, 3);
        near(9.0, bins.color_high, 3);
        near(9.0, bins.mean_extent(), 3);
        assert!(!bins.clips_low());
        assert!(!bins.clips_high());
    }

    #[test]
    fn a_diverging_scale_has_a_reach_on_every_path_that_settles_the_bounds() {
        // The bounds are settled and returned early on three different paths. A reach left
        // behind on any of them draws every mark on a delta scatter at the neutral end of the
        // scale, target or not.
        let mut closes = vec![0.5; 98];
        closes.push(-9.0);
        closes.push(9.0);
        let sets: Vec<Vec<f64>> = vec![
            vec![-4.0, 4.0],                                          // too few blocks to trim
            closes,                                                   // the trim closes the range
            (0..100).map(|i| -5.0 + (f64::from(i) / 10.0)).collect(), // trimmed normally
        ];

        for means in sets {
            let bins = bins_holding(&means);

            assert!(
                bins.mean_extent() > 0.0,
                "reach was {} for {} blocks",
                bins.mean_extent(),
                means.len()
            );
            near(bins.color_low.abs().max(bins.color_high.abs()), bins.mean_extent(), 6);
        }
    }

    #[test]
    fn the_reach_always_matches_the_bounds_colour_is_scaled_over() {
        // Whatever the distribution, the diverging scale and the sequential one must describe
        // the same numbers, or the legend and the marks disagree.
        let means: Vec<f64> = (0..60).map(|i| f64::from(i - 30)).collect();
        let bins = bins_holding(&means);

        near(bins.color_low.abs().max(bins.color_high.abs()), bins.mean_extent(), 6);
    }

    // ----- tracing back -----------------------------------------------------

    #[test]
    fn a_block_traces_back_to_the_samples_that_made_it() {
        let x = channel("RPM", &[0.0, 100.0, 0.0, 100.0]);
        let y = channel("MAP", &[0.0, 100.0, 0.0, 100.0]);
        let z = channel("AFR", &[14.0, 11.0, 13.0, 12.0]);

        let plot = build(&x, &y, &z, None, None);
        let bins = plot.bin(2, 2).unwrap();

        assert_eq!(plot.samples_in(&bins, 0, 0), [0, 2]);
        assert_eq!(plot.samples_in(&bins, 1, 1), [1, 3]);
        assert!(plot.samples_in(&bins, 0, 1).is_empty());
    }

    #[test]
    fn samples_trace_back_through_the_filter_to_their_original_index() {
        let x = channel("RPM", &[1000.0; 3]);
        let y = channel("MAP", &[50.0; 3]);
        let z = channel("AFR", &[14.0, 13.0, 12.0]);
        let m = mask(vec![false, false, true]);

        let plot = build(&x, &y, &z, Some(&m), None);
        let bins = plot.bin(2, 2).unwrap();

        assert_eq!(plot.samples_in(&bins, 1, 1), [2]);
    }

    #[test]
    fn contiguous_samples_group_into_visits() {
        for (samples, expected) in [(vec![0, 1, 2], 1), (vec![0, 1, 2, 40, 41], 2), (vec![0, 5, 10, 15], 4)] {
            assert_eq!(ScatterPlot::visits_among(&samples, 2).len(), expected);
        }
    }

    #[test]
    fn a_one_sample_gap_does_not_split_a_visit() {
        // Two samples of noise inside a pull are not two separate visits.
        let visits = ScatterPlot::visits_among(&[10, 11, 13, 14], 2);

        assert_eq!(visits, [(10, 14)]);
    }

    #[test]
    fn no_samples_means_no_visits() {
        assert!(ScatterPlot::visits_among(&[], 2).is_empty());
    }
}
