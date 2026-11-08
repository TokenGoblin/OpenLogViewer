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
