//! Ported from `VeAnalysis.cs`. Owned by package `w3-analysis`.
//!
//! Compares logged AFR against the AFR the tune was asking for, and suggests a new fuel table.
//!
//! The reasoning is one line: the engine took in a known amount of air, the ECU metered fuel
//! for it using the VE number in the cell, and the wideband says what the mixture actually
//! came out as. If it came out richer than the target, the ECU thought there was more air than
//! there was, so the VE number is too high: scale it by measured over target.
//!
//! What makes this trustworthy is not the arithmetic but what it refuses to do. A cell backed
//! by a handful of samples, or one implying an implausible jump, is left alone and said to be
//! left alone.

use std::sync::Arc;

use super::histogram_table::{HistogramInputs, HistogramTable, nearest};
use crate::filters::log_filter::SampleMask;
use crate::fmt;
use crate::model::{Array2, LogChannel, TuneAxis, TuneTable};
use crate::{Error, Result};

#[derive(Clone, Debug, PartialEq)]
pub struct VeAnalysisSettings {
    /// Samples a cell needs before its suggestion is trusted. A cell crossed twice on the way
    /// to somewhere else says more about the transient than about the fuelling there.
    pub minimum_samples: i32,

    /// Largest change suggested for one pass, as a percentage of the current value. A cell
    /// read during an accel-enrichment event or a gear change can imply a correction far
    /// larger than the table is actually wrong by, and applying it whole turns one bad reading
    /// into a hole in the table.
    pub max_change_percent: f64,

    /// Fraction of the indicated correction to apply. Converging in steps is how this is done
    /// in practice: the measurement lags the change, so taking the whole of it tends to
    /// overshoot and oscillate.
    pub authority: f64,

    /// How many samples later the wideband reads the mixture that was metered now.
    ///
    /// Fuel metered on this revolution is burned, pushed out of the port, carried down the
    /// pipe to wherever the sensor is, and only then measured. Compared without accounting for
    /// it, every reading is credited to whatever the engine was doing a few hundred
    /// milliseconds too late. In samples rather than seconds because this is where the
    /// arithmetic happens. Zero by default.
    pub measurement_delay_samples: i32,

    /// How much data a cell needs before its correction is taken at face value.
    ///
    /// `minimum_samples` is a floor: below it a cell is not touched at all. Above it the
    /// correction is scaled by `n / (n + this)`, which is nought at no data, half at this many
    /// samples, and approaches the whole of it as the evidence mounts. Set to zero to take
    /// every correction whole.
    pub confidence_samples: i32,
}

impl Default for VeAnalysisSettings {
    fn default() -> Self {
        Self {
            minimum_samples: 12,
            max_change_percent: 15.0,
            authority: 1.0,
            measurement_delay_samples: 0,
            confidence_samples: 12,
        }
    }
}

/// What one pass of the analysis concluded, cell by cell.
#[derive(Clone, Debug)]
pub struct VeAnalysisResult {
    pub table: TuneTable,
    /// Suggested value per cell, or `None` where there was not enough data.
    pub suggested: Array2<Option<f64>>,
    /// Change from the current table, as a percentage. `None` where unchanged.
    pub change_percent: Array2<Option<f64>>,
    /// Samples behind each cell.
    pub counts: Array2<usize>,
    /// How far each cell's own measurement was trusted, 0 to 1: the factor its correction was
    /// scaled by for want of data.
    pub weight: Array2<f64>,
    /// Cells the analysis is prepared to change.
    pub cells_suggested: usize,
    /// Cells with samples but too few to trust.
    pub cells_thin: usize,
    pub samples_used: usize,
    /// Largest single change suggested, as a percentage.
    pub largest_change_percent: f64,
    /// Why the answer should not be believed, where that can be said: set when the two
    /// channels being compared are not the same quantity (measured AFR against a lambda
    /// target, most often).
    pub problem: Option<String>,
}

impl VeAnalysisResult {
    pub fn is_empty(&self) -> bool {
        self.cells_suggested == 0
    }

    pub fn has_problem(&self) -> bool {
        self.problem.as_ref().is_some_and(|p| !p.is_empty())
    }

    /// The suggestion as a table the heat view can draw: cells hold the change in percent, so
    /// the colour says which way and how far, and an untouched cell is empty rather than zero.
    #[allow(clippy::too_many_arguments)]
    pub fn as_change_table(
        &self,
        rpm: &Arc<LogChannel>,
        load: &Arc<LogChannel>,
        afr: &Arc<LogChannel>,
        target: &Arc<LogChannel>,
        first_sample: usize,
        last_sample: usize,
        mask: Option<&SampleMask>,
    ) -> Result<HistogramTable> {
        HistogramTable::from_cells(
            // Given the comparison channel so the scale diverges about zero: a cell wanting
            // less fuel and one wanting more must not shade alike.
            HistogramInputs::new(rpm, load, afr)
                .with_z_compare(Some(target))
                .with_mask(mask),
            &self.table.x.breakpoints,
            &self.table.y.breakpoints,
            &self.change_percent,
            &self.counts,
            first_sample,
            last_sample,
            Some(&format!("{} change, %", self.table.name)),
            Some(1),
        )
    }

    /// The suggested table itself, for reading the new numbers off.
    pub fn as_suggested_table(
        &self,
        rpm: &Arc<LogChannel>,
        load: &Arc<LogChannel>,
        afr: &Arc<LogChannel>,
        first_sample: usize,
        last_sample: usize,
        mask: Option<&SampleMask>,
    ) -> Result<HistogramTable> {
        HistogramTable::from_cells(
            // No comparison channel: a suggested VE is a magnitude, and a diverging scale
            // would imply a midpoint the number does not have.
            HistogramInputs::new(rpm, load, afr).with_mask(mask),
            &self.table.x.breakpoints,
            &self.table.y.breakpoints,
            &self.suggested,
            &self.counts,
            first_sample,
            last_sample,
            Some(&format!("suggested {}", self.table.name)),
            Some(1),
        )
    }
}

/// `Math.Max(double, double)`: NaN wins, and positive zero beats negative zero.
pub(crate) fn dotnet_max(a: f64, b: f64) -> f64 {
    if a > b || a.is_nan() {
        a
    } else if a == b {
        if a.is_sign_negative() { b } else { a }
    } else {
        b
    }
}

/// `Math.Min(double, double)`: NaN wins, and negative zero beats positive zero.
pub(crate) fn dotnet_min(a: f64, b: f64) -> f64 {
    if a < b || a.is_nan() {
        a
    } else if a == b {
        if a.is_sign_negative() { a } else { b }
    } else {
        b
    }
}

/// `Math.Clamp(double, double, double)`, which throws when the bounds are reversed and lets a
/// NaN through.
fn clamp(value: f64, min: f64, max: f64) -> Result<f64> {
    if min > max {
        return Err(Error::Argument(format!("{min} cannot be greater than {max}.")));
    }
    Ok(if value < min {
        min
    } else if value > max {
        max
    } else {
        value
    })
}

fn total(values: &[f64]) -> f64 {
    let mut sum = 0.0;
    for v in values {
        sum += v;
    }
    sum
}

/// The mean of a channel over a window, for saying what scale it is on.
fn average(channel: &LogChannel, from: usize, to: usize) -> f64 {
    let mut sum = 0.0;
    let mut count = 0;

    for i in from..=to {
        let value = channel.at(i);
        if value.is_nan() {
            continue;
        }

        sum += value;
        count += 1;
    }

    if count == 0 { 0.0 } else { sum / count as f64 }
}

/// A grid taken from the log itself, for when no tune supplies one.
///
/// The ECU's own breakpoints are better and are used whenever they can be had, because a table
/// binned onto them reads cell-for-cell against the table being tuned. But requiring them meant
/// this could not run at all on a log by itself, which is every MaxxECU, whose tune cannot be
/// read, and every log opened away from the car it came from.
///
/// The cells hold nothing. There is no current fuelling to scale, so the analysis reports how
/// far out each cell is and leaves suggesting a new number to a session that knows the old one.
///
/// # Errors
/// [`Error::Argument`] when `columns` or `rows` is zero.
pub fn grid_from(
    x: &LogChannel,
    y: &LogChannel,
    columns: usize,
    rows: usize,
    first_sample: isize,
    last_sample: isize,
    mask: Option<&SampleMask>,
) -> Result<TuneTable> {
    if columns < 1 {
        return Err(Error::Argument("columns must be at least 1".into()));
    }
    if rows < 1 {
        return Err(Error::Argument("rows must be at least 1".into()));
    }

    Ok(TuneTable {
        name: format!("{} × {}", x.name(), y.name()),
        x: TuneAxis {
            constant: x.name().to_string(),
            units: x.units().to_string(),
            breakpoints: breakpoints(x, columns, first_sample, last_sample, mask),
        },
        y: TuneAxis {
            constant: y.name().to_string(),
            units: y.units().to_string(),
            breakpoints: breakpoints(y, rows, first_sample, last_sample, mask),
        },
        values: Array2::new(columns, rows),
        units: "%".to_string(),
    })
}

/// Bin centres spread over what the channel actually did in the window, matching how the heat
/// table lays its axes out, so the two agree about which cell a sample belongs to.
fn breakpoints(
    channel: &LogChannel,
    count: usize,
    first_sample: isize,
    last_sample: isize,
    mask: Option<&SampleMask>,
) -> Vec<f64> {
    let from = 0.max(first_sample.min(last_sample));
    let to = (channel.len() as isize - 1).min(first_sample.max(last_sample));

    let (mut low, mut high) = (f64::MAX, f64::MIN);

    let mut i = from;
    while i <= to {
        let index = i as usize;
        i += 1;
        if mask.is_some_and(|m| !m.is_accepted(index)) {
            continue;
        }

        let value = channel.at(index);
        if value.is_nan() {
            continue;
        }

        low = dotnet_min(low, value);
        high = dotnet_max(high, value);
    }

    if low > high {
        (low, high) = (0.0, 1.0);
    }
    if high - low < 1e-9 {
        high = low + 1.0;
    }

    let step = (high - low) / count as f64;

    (0..count).map(|i| low + ((i as f64 + 0.5) * step)).collect()
}

/// Runs the analysis over `first_sample..=last_sample`.
///
/// # Errors
/// [`Error::Argument`] when `settings.max_change_percent` is negative and a cell reaches the
/// clamp, as `Math.Clamp` throws in the C#.
#[allow(clippy::too_many_arguments)]
pub fn analyse(
    table: &TuneTable,
    rpm: &LogChannel,
    load: &LogChannel,
    afr: &LogChannel,
    target: &LogChannel,
    first_sample: isize,
    last_sample: isize,
    mask: Option<&SampleMask>,
    settings: Option<&VeAnalysisSettings>,
) -> Result<VeAnalysisResult> {
    let default_settings = VeAnalysisSettings::default();
    let settings = settings.unwrap_or(&default_settings);

    let columns = table.columns();
    let rows = table.rows();
    let mut sums = vec![0.0f64; columns * rows];
    let mut counts = Array2::<usize>::new(columns, rows);

    let length = rpm.len().min(load.len()).min(afr.len()).min(target.len()) as isize;
    let from = 0.max(first_sample.min(last_sample));
    let to = (length - 1).min(first_sample.max(last_sample));

    let delay = 0.max(settings.measurement_delay_samples) as isize;

    let mut used = 0;
    let mut i = from;
    while i <= to {
        let index = i as usize;
        i += 1;

        if mask.is_some_and(|m| !m.is_accepted(index)) {
            continue;
        }

        // The cell, and what the ECU was aiming for, are read at the moment the fuel was
        // metered. The wideband's answer to it arrives later, so that one reading, and only
        // that one, comes from further down the log.
        let measured_at = index as isize + delay;
        if measured_at > to {
            continue;
        }
        let measured_at = measured_at as usize;

        // The delayed sample has to survive the filters too, or a reading from a stretch the
        // user excluded would be credited to a cell.
        if mask.is_some_and(|m| !m.is_accepted(measured_at)) {
            continue;
        }

        let (x, y) = (rpm.at(index), load.at(index));
        let (measured, wanted) = (afr.at(measured_at), target.at(index));

        if x.is_nan() || y.is_nan() || measured.is_nan() || wanted.is_nan() {
            continue;
        }

        // A zero or negative target is not a target; dividing by it would produce a
        // correction from nothing.
        if wanted <= 0.0 || measured <= 0.0 {
            continue;
        }

        let column = nearest(&table.x.breakpoints, x);
        let row = nearest(&table.y.breakpoints, y);

        sums[column * rows + row] += measured / wanted;
        counts[(column, row)] += 1;
        used += 1;
    }

    // Before any of it is believed: are these two channels even the same quantity? A firmware
    // commonly logs both AFR and lambda, and a target in one against a measurement in the
    // other divides 12.5 by 0.9 and reports every cell as fifteen per cent lean: a full table
    // of confident nonsense, which is worse than an empty one because it looks like an answer.
    //
    // A real tune is out by tens of per cent at worst. Anything beyond a factor of two is not
    // a mistuned engine, it is two different scales.
    if used > 0 {
        let average_ratio = total(&sums) / used as f64;

        // Written out rather than as a range so a NaN average falls through, as `is < 0.5 or > 2`.
        if average_ratio < 0.5 || average_ratio > 2.0 {
            let (from_u, to_u) = (from as usize, to as usize);
            return Ok(VeAnalysisResult {
                table: table.clone(),
                suggested: Array2::new(columns, rows),
                change_percent: Array2::new(columns, rows),
                counts,
                weight: Array2::new(columns, rows),
                cells_suggested: 0,
                cells_thin: 0,
                samples_used: used,
                largest_change_percent: 0.0,
                problem: Some(format!(
                    "\"{}\" averages {} and \"{}\" averages {}, which are not the same kind of \
                     number — one is probably lambda and the other AFR. Comparing them would \
                     suggest a correction of hundreds of per cent. Pick a measured channel and \
                     a target on the same scale.",
                    afr.name(),
                    fmt::f64(average(afr, from_u, to_u), "G4"),
                    target.name(),
                    fmt::f64(average(target, from_u, to_u), "G4"),
                )),
            });
        }
    }

    let mut suggested = Array2::<Option<f64>>::new(columns, rows);
    let mut change = Array2::<Option<f64>>::new(columns, rows);
    let mut weight = Array2::<f64>::new(columns, rows);
    let (mut changed, mut thin) = (0, 0);
    let mut largest = 0.0f64;

    for c in 0..columns {
        for r in 0..rows {
            if counts[(c, r)] == 0 {
                continue;
            }

            if (counts[(c, r)] as i64) < i64::from(settings.minimum_samples) {
                thin += 1;
                continue;
            }

            let ratio = sums[c * rows + r] / counts[(c, r)] as f64;
            let current = table.values[(c, r)];

            // How much this cell's evidence is worth. Half at the confidence figure,
            // approaching the whole of it as samples accumulate, so a cell backed by a glance
            // moves less than one backed by a minute of steady running.
            let confidence = confidence(counts[(c, r)], settings.confidence_samples);
            weight[(c, r)] = confidence;

            // How far out the fuelling is, which is the whole of the answer and needs nothing
            // from the table. Mixture richer than asked for means the ECU thought there was
            // more air than there was, so the number in that cell is too high by this much.
            let error = (ratio - 1.0) * 100.0;

            // And how much of that to actually move, which is less: some of the authority the
            // caller allows, and less again where the cell rests on little evidence.
            let wanted = error * settings.authority * confidence;

            let mut percent = clamp(wanted, -settings.max_change_percent, settings.max_change_percent)?;

            // A new value for the cell only where there is an old one to scale. A log on its
            // own still says how far out each cell is, and that is the number a tuner acts on.
            if current > 0.0 {
                let proposed = current * (1.0 + percent / 100.0);

                suggested[(c, r)] = Some(proposed);
                percent = (proposed - current) / current * 100.0;
            } else {
                // The error itself, not a share of it. With no table there is nothing to
                // suggest a change to, so what is reported is the measurement.
                percent = clamp(error, -settings.max_change_percent, settings.max_change_percent)?;
            }

            change[(c, r)] = Some(percent);
            changed += 1;
            largest = dotnet_max(largest, percent.abs());
        }
    }

    Ok(VeAnalysisResult {
        table: table.clone(),
        suggested,
        change_percent: change,
        counts,
        weight,
        cells_suggested: changed,
        cells_thin: thin,
        samples_used: used,
        largest_change_percent: largest,
        problem: None,
    })
}

/// How far a cell's own measurement is trusted against the number already in it:
/// `n / (n + k)`, the usual way of shrinking an estimate towards a prior in proportion to how
/// much evidence stands behind it. `k` of zero (or less) means every correction is taken whole.
pub fn confidence(samples: usize, k: i32) -> f64 {
    if k <= 0 {
        1.0
    } else {
        samples as f64 / (samples as f64 + f64::from(k))
    }
}
