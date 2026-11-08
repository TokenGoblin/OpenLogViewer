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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::filters::log_filter::{FilterComparison, LogFilter, SampleFilter};
    use crate::model::LogDocument;

    /// `Assert.Equal(expected, actual, precision)`: equal once both are rounded.
    fn assert_equal(expected: f64, actual: f64, precision: i32) {
        let scale = 10f64.powi(precision);
        assert_eq!(
            (expected * scale).round_ties_even(),
            (actual * scale).round_ties_even(),
            "expected {expected}, got {actual}"
        );
    }

    /// A 2x2 table at a flat 50% VE, on round breakpoints.
    fn table(fill: f64) -> TuneTable {
        let mut values = Array2::<f64>::new(2, 2);
        for c in 0..2 {
            for r in 0..2 {
                values[(c, r)] = fill;
            }
        }
        TuneTable {
            name: "VE table 1".into(),
            x: TuneAxis {
                constant: "rpm".into(),
                units: "RPM".into(),
                breakpoints: vec![1000.0, 3000.0],
            },
            y: TuneAxis {
                constant: "map".into(),
                units: "kPa".into(),
                breakpoints: vec![40.0, 80.0],
            },
            values,
            units: "%".into(),
        }
    }

    fn channel(name: &str, values: &[f64]) -> Arc<LogChannel> {
        Arc::new(LogChannel::new(name, "", 2, values, false))
    }

    fn repeat(v: f64, n: usize) -> Vec<f64> {
        vec![v; n]
    }

    fn unweighted() -> VeAnalysisSettings {
        VeAnalysisSettings {
            minimum_samples: 1,
            confidence_samples: 0,
            ..Default::default()
        }
    }

    /// Every sample lands in the low/low cell unless said otherwise.
    ///
    /// Confidence weighting is off here. These are tests of the ratio arithmetic and of where a
    /// sample lands, on one or two samples apiece; leaving the shrinkage on would scale every
    /// expected number by how thin the evidence is. It has its own tests below.
    fn run(
        afr: &[f64],
        target: &[f64],
        settings: Option<VeAnalysisSettings>,
        rpm: Option<&[f64]>,
        map: Option<&[f64]>,
    ) -> VeAnalysisResult {
        let n = afr.len();
        let x = channel("RPM", &rpm.map_or_else(|| repeat(1000.0, n), <[f64]>::to_vec));
        let y = channel("MAP", &map.map_or_else(|| repeat(40.0, n), <[f64]>::to_vec));
        let settings = settings.unwrap_or_else(unweighted);
        analyse(
            &table(50.0),
            &x,
            &y,
            &channel("AFR", afr),
            &channel("Target", target),
            0,
            n as isize - 1,
            None,
            Some(&settings),
        )
        .unwrap()
    }

    fn analyse_simple(afr: &[f64], target: &[f64]) -> VeAnalysisResult {
        run(afr, target, None, None, None)
    }

    #[test]
    fn richer_than_target_asks_for_less_fuel() {
        // Measured 12.6 against a target of 14.0 means the ECU metered fuel for more air than
        // there was, so the VE number is too high.
        let result = analyse_simple(&[12.6], &[14.0]);
        assert_equal(45.0, result.suggested[(0, 0)].unwrap(), 4);
        assert_equal(-10.0, result.change_percent[(0, 0)].unwrap(), 4);
    }

    #[test]
    fn leaner_than_target_asks_for_more_fuel() {
        let result = analyse_simple(&[15.4], &[14.0]);
        assert_equal(55.0, result.suggested[(0, 0)].unwrap(), 4);
        assert_equal(10.0, result.change_percent[(0, 0)].unwrap(), 4);
    }

    #[test]
    fn on_target_leaves_the_cell_where_it_is() {
        let result = analyse_simple(&[14.0, 14.0], &[14.0, 14.0]);
        assert_equal(50.0, result.suggested[(0, 0)].unwrap(), 4);
        assert_equal(0.0, result.change_percent[(0, 0)].unwrap(), 4);
    }

    #[test]
    fn the_correction_is_averaged_per_sample_not_between_the_means() {
        // Mean AFR 14 against mean target 14 would say the cell is perfect, which would hide
        // that it was never actually on target.
        let result = analyse_simple(&[12.6, 15.4], &[14.0, 14.0]);

        // Mean of 0.9 and 1.1 is 1.0: here they really do cancel, and that is the honest
        // answer for a cell that averaged correct.
        assert_equal(50.0, result.suggested[(0, 0)].unwrap(), 4);
    }

    #[test]
    fn a_cell_with_too_few_samples_is_left_alone_and_counted() {
        // Two crossings on the way somewhere else say more about the transient than about the
        // fuelling there.
        let result = run(
            &[12.6, 12.6],
            &[14.0, 14.0],
            Some(VeAnalysisSettings {
                minimum_samples: 5,
                ..Default::default()
            }),
            None,
            None,
        );

        assert!(result.suggested[(0, 0)].is_none());
        assert_eq!(0, result.cells_suggested);
        assert_eq!(1, result.cells_thin);
        assert!(result.is_empty());
    }

    #[test]
    fn a_large_correction_is_clamped_rather_than_dropped() {
        // A cell read during an enrichment event can imply a correction far bigger than the
        // table is wrong by. It should still move, just not all the way at once.
        let result = run(
            &[7.0],
            &[14.0],
            Some(VeAnalysisSettings {
                minimum_samples: 1,
                max_change_percent: 15.0,
                confidence_samples: 0,
                ..Default::default()
            }),
            None,
            None,
        );

        assert_equal(42.5, result.suggested[(0, 0)].unwrap(), 4); // 50 - 15%
        assert_equal(-15.0, result.change_percent[(0, 0)].unwrap(), 4);
        assert_equal(15.0, result.largest_change_percent, 4);
    }

    #[test]
    fn authority_scales_how_much_of_the_correction_is_taken() {
        // Converging in steps: the measurement lags the change, so taking the whole of it
        // tends to overshoot.
        let result = run(
            &[12.6],
            &[14.0],
            Some(VeAnalysisSettings {
                minimum_samples: 1,
                authority: 0.5,
                confidence_samples: 0,
                ..Default::default()
            }),
            None,
            None,
        );

        assert_equal(47.5, result.suggested[(0, 0)].unwrap(), 4); // half of -10%
    }

    #[test]
    fn samples_land_in_the_nearest_cell() {
        // The ECU interpolates, but the number a tuner changes is the nearest one.
        let result = run(
            &[12.6, 15.4],
            &[14.0, 14.0],
            None,
            Some(&[1100.0, 2900.0]),
            Some(&[45.0, 75.0]),
        );

        assert_equal(45.0, result.suggested[(0, 0)].unwrap(), 4);
        assert_equal(55.0, result.suggested[(1, 1)].unwrap(), 4);
        assert_eq!(2, result.cells_suggested);
    }

    #[test]
    fn a_missing_reading_is_skipped_rather_than_counted_as_zero() {
        let result = analyse_simple(&[12.6, f64::NAN], &[14.0, 14.0]);

        assert_eq!(1, result.samples_used);
        assert_equal(45.0, result.suggested[(0, 0)].unwrap(), 4);
    }

    #[test]
    fn a_zero_or_negative_target_is_not_a_target() {
        // Dividing by it would manufacture a correction out of nothing.
        let result = analyse_simple(&[12.6, 12.6], &[0.0, -1.0]);

        assert_eq!(0, result.samples_used);
        assert!(result.is_empty());
    }

    #[test]
    fn a_cell_the_log_never_visited_is_untouched() {
        let result = analyse_simple(&[12.6], &[14.0]);

        assert!(result.suggested[(1, 1)].is_none());
        assert!(result.change_percent[(1, 1)].is_none());
        assert_eq!(0, result.counts[(1, 1)]);
    }

    #[test]
    fn filters_restrict_which_samples_count() {
        let doc = LogDocument::new(
            "x",
            "test",
            vec![Arc::new(LogChannel::new(
                "CLT",
                "F",
                0,
                &[100.0, 100.0, 180.0, 180.0],
                false,
            ))],
            Arc::new(LogChannel::new("Time", "s", 3, &[0.0, 0.1, 0.2, 0.3], true)),
        );

        let warm = SampleFilter::build(
            &doc,
            &[LogFilter::new("warm", "CLT", FilterComparison::AboveOrEqual).with_low(160.0)],
        );

        let result = analyse(
            &table(50.0),
            &channel("RPM", &[1000.0; 4]),
            &channel("MAP", &[40.0; 4]),
            &channel("AFR", &[7.0, 7.0, 12.6, 12.6]), // cold samples wildly rich
            &channel("Target", &[14.0; 4]),
            0,
            3,
            Some(&warm),
            Some(&unweighted()),
        )
        .unwrap();

        assert_eq!(2, result.samples_used);
        assert_equal(45.0, result.suggested[(0, 0)].unwrap(), 4); // only the warm pair
    }

    #[test]
    fn a_zero_cell_cannot_be_scaled() {
        // Multiplying zero by anything stays zero, so there is nothing to suggest.
        let result = analyse(
            &table(0.0),
            &channel("RPM", &[1000.0]),
            &channel("MAP", &[40.0]),
            &channel("AFR", &[12.6]),
            &channel("Target", &[14.0]),
            0,
            0,
            None,
            Some(&unweighted()),
        )
        .unwrap();

        assert!(result.suggested[(0, 0)].is_none());
    }

    // ----- rendering --------------------------------------------------------

    #[test]
    fn the_change_table_diverges_about_zero() {
        // A cell wanting less fuel and one wanting more must not shade alike.
        let result = run(
            &[12.6, 15.4],
            &[14.0, 14.0],
            None,
            Some(&[1000.0, 3000.0]),
            Some(&[40.0, 80.0]),
        );

        let table = result
            .as_change_table(
                &channel("RPM", &[1000.0, 3000.0]),
                &channel("MAP", &[40.0, 80.0]),
                &channel("AFR", &[12.6, 15.4]),
                &channel("Target", &[14.0, 14.0]),
                0,
                1,
                None,
            )
            .unwrap();

        assert!(table.shows_deviation());
        assert_equal(-10.0, table.min_value, 4);
        assert_equal(10.0, table.max_value, 4);
        assert_eq!("+10.0", table.format_cell(1, 1));
    }

    #[test]
    fn the_suggested_table_is_a_magnitude_not_a_deviation() {
        // A VE number has no midpoint to diverge about.
        let result = analyse_simple(&[12.6], &[14.0]);

        let table = result
            .as_suggested_table(
                &channel("RPM", &[1000.0]),
                &channel("MAP", &[40.0]),
                &channel("AFR", &[12.6]),
                0,
                0,
                None,
            )
            .unwrap();

        assert!(!table.shows_deviation());
        assert_eq!("45.0", table.format_cell(0, 0));
    }

    #[test]
    fn a_computed_table_names_itself_rather_than_its_z_channel() {
        let result = analyse_simple(&[12.6], &[14.0]);

        let change = result
            .as_change_table(
                &channel("RPM", &[1000.0]),
                &channel("MAP", &[40.0]),
                &channel("AFR", &[12.6]),
                &channel("Target", &[14.0]),
                0,
                0,
                None,
            )
            .unwrap();

        assert_eq!(Some("VE table 1 change, %"), change.display_name.as_deref());
    }

    #[test]
    fn the_change_table_keeps_the_counts_behind_each_cell() {
        // How much data backs a suggestion is the first thing worth knowing.
        let result = analyse_simple(&[12.6; 3], &[14.0; 3]);

        let table = result
            .as_change_table(
                &channel("RPM", &[1000.0; 3]),
                &channel("MAP", &[40.0; 3]),
                &channel("AFR", &[12.6; 3]),
                &channel("Target", &[14.0; 3]),
                0,
                2,
                None,
            )
            .unwrap();

        assert_eq!(3, table.counts[(0, 0)]);
        assert_eq!(3, table.max_count);
    }

    // ----- the wideband reads late ------------------------------------------

    #[test]
    fn the_delay_credits_a_reading_to_the_cell_that_caused_it_not_the_one_it_arrived_in() {
        // The engine steps from the low cell to the high cell at sample 3, and the wideband
        // shows the low cell's rich mixture two samples later, after the step. Read without a
        // delay, that rich reading is evidence against the high cell, which never produced it.
        let rpm = [1000.0, 1000.0, 1000.0, 3000.0, 3000.0, 3000.0];
        let map = [40.0; 6];
        let afr = [14.0, 14.0, 14.0, 12.0, 12.0, 14.0];
        let target = [14.0; 6];

        let settings = unweighted();
        let go = |s: &VeAnalysisSettings| {
            analyse(
                &table(50.0),
                &channel("RPM", &rpm),
                &channel("MAP", &map),
                &channel("AFR", &afr),
                &channel("Target", &target),
                0,
                5,
                None,
                Some(s),
            )
            .unwrap()
        };

        let late = go(&settings);

        // Without the shift, the high cell wears the low cell's rich reading.
        assert!(
            late.change_percent[(1, 0)].is_some_and(|v| v < 0.0),
            "expected the high cell to look rich"
        );

        let shifted = go(&VeAnalysisSettings {
            measurement_delay_samples: 2,
            ..settings
        });

        // With it, the richness lands on the cell that was running when the fuel was metered,
        // and the high cell is left reading on target.
        assert!(
            shifted.change_percent[(0, 0)].is_some_and(|v| v < 0.0),
            "expected the low cell to take the correction"
        );
        assert!(
            shifted.change_percent[(1, 0)].is_none_or(|v| v > -0.001),
            "the high cell should no longer look rich, was {:?}",
            shifted.change_percent[(1, 0)]
        );
    }

    #[test]
    fn the_delay_shifts_the_measurement_and_not_the_target() {
        // The target is what the ECU was aiming for when it metered the fuel, so it belongs to
        // the same moment as the cell. Shifting it too would compare a reading against a target
        // from a different moment.
        let result = run(
            &[14.0, 14.0, 12.0],
            &[14.0, 10.0, 10.0],
            Some(VeAnalysisSettings {
                minimum_samples: 1,
                confidence_samples: 0,
                measurement_delay_samples: 2,
                ..Default::default()
            }),
            None,
            None,
        );

        // Sample 0: target 14.0 (from sample 0), measured 12.0 (from sample 2). 12/14 is
        // 14.3% rich, clamped to the 15% limit, so it survives whole.
        assert!(result.change_percent[(0, 0)].is_some());
        assert_equal(-14.29, result.change_percent[(0, 0)].unwrap(), 1);
    }

    #[test]
    fn samples_with_no_reading_left_to_pair_with_are_dropped() {
        // The last samples of a window have no later reading to be judged by.
        let result = run(
            &[13.0; 5],
            &[14.0; 5],
            Some(VeAnalysisSettings {
                minimum_samples: 1,
                measurement_delay_samples: 2,
                ..Default::default()
            }),
            None,
            None,
        );

        // Five samples, two of which have nothing to pair with.
        assert_eq!(3, result.samples_used);
    }

    #[test]
    fn a_delay_of_zero_leaves_the_answer_exactly_as_it_was() {
        let afr = [12.6, 12.7, 12.5, 12.6];
        let target = [14.0; 4];

        let settings = unweighted();
        let without = run(&afr, &target, Some(settings.clone()), None, None);
        let zero = run(
            &afr,
            &target,
            Some(VeAnalysisSettings {
                measurement_delay_samples: 0,
                ..settings
            }),
            None,
            None,
        );

        assert_eq!(without.change_percent[(0, 0)], zero.change_percent[(0, 0)]);
        assert_eq!(without.samples_used, zero.samples_used);
    }

    #[test]
    fn a_negative_delay_is_treated_as_none() {
        let result = run(
            &[12.6, 12.6],
            &[14.0, 14.0],
            Some(VeAnalysisSettings {
                minimum_samples: 1,
                measurement_delay_samples: -5,
                ..Default::default()
            }),
            None,
            None,
        );

        assert_eq!(2, result.samples_used);
    }

    // ----- how much a thin cell is trusted ----------------------------------

    #[test]
    fn confidence_rises_with_evidence() {
        // (k, samples, expected)
        for (k, samples, expected) in [
            (0, 12, 1.0),  // switched off
            (12, 0, 0.0),  // no evidence at all
            (12, 12, 0.5), // at the threshold, half believed
            (12, 36, 0.75),
            (12, 228, 0.95),
        ] {
            assert_equal(expected, confidence(samples, k), 4);
        }
    }

    #[test]
    fn a_cell_backed_by_more_data_moves_further_than_one_backed_by_less() {
        // Both cells read exactly 10% rich. Under the old cliff they moved identically the
        // moment each cleared the threshold, though one is a measurement and the other a
        // glance.
        let mut rpm = repeat(1000.0, 4);
        rpm.extend(repeat(3000.0, 40));

        let result = analyse(
            &table(50.0),
            &channel("RPM", &rpm),
            &channel("MAP", &repeat(40.0, 44)),
            &channel("AFR", &repeat(12.6, 44)),
            &channel("Target", &repeat(14.0, 44)),
            0,
            43,
            None,
            Some(&VeAnalysisSettings {
                minimum_samples: 4,
                confidence_samples: 12,
                ..Default::default()
            }),
        )
        .unwrap();

        let thin = result.change_percent[(0, 0)].unwrap().abs();
        let solid = result.change_percent[(1, 0)].unwrap().abs();

        assert!(solid > thin, "solid cell moved {solid}, thin cell moved {thin}");
        assert!(result.weight[(1, 0)] > result.weight[(0, 0)]);
    }

    #[test]
    fn confidence_of_zero_restores_the_old_all_or_nothing_behaviour() {
        let result = run(&[12.6; 20], &[14.0; 20], Some(unweighted()), None, None);

        // The full 10% error, undiminished.
        assert_equal(-10.0, result.change_percent[(0, 0)].unwrap(), 1);
        assert_equal(1.0, result.weight[(0, 0)], 6);
    }

    #[test]
    fn by_default_a_thin_cell_is_shrunk_towards_the_number_it_already_holds() {
        // What a user gets without touching anything. One sample reading 10% rich is not
        // evidence for a 10% change, and the default says so.
        let result = analyse(
            &table(50.0),
            &channel("RPM", &[1000.0]),
            &channel("MAP", &[40.0]),
            &channel("AFR", &[12.6]),
            &channel("Target", &[14.0]),
            0,
            0,
            None,
            Some(&VeAnalysisSettings {
                minimum_samples: 1,
                ..Default::default()
            }),
        )
        .unwrap();

        let moved = result.change_percent[(0, 0)].unwrap().abs();

        assert!(moved > 0.0, "a suggestion should still be made");
        assert!(moved < 2.0, "one sample should barely move the cell, moved {moved}%");
        assert_equal(confidence(1, 12), result.weight[(0, 0)], 6);
    }

    #[test]
    fn weight_is_zero_for_a_cell_nothing_landed_in() {
        let result = analyse_simple(&[12.6], &[14.0]);

        assert_equal(0.0, result.weight[(1, 1)], 6);
    }

    #[test]
    fn the_floor_still_holds_below_the_minimum() {
        // Weighting softens the cliff; it does not remove the floor, which is a stated safety
        // property rather than a default.
        let result = run(
            &[12.6; 3],
            &[14.0; 3],
            Some(VeAnalysisSettings {
                minimum_samples: 10,
                ..Default::default()
            }),
            None,
            None,
        );

        assert!(result.change_percent[(0, 0)].is_none());
        assert_eq!(1, result.cells_thin);
        assert_eq!(0, result.cells_suggested);
    }

    // ----- VeWithoutATuneTests ----------------------------------------------

    type Four = (Arc<LogChannel>, Arc<LogChannel>, Arc<LogChannel>, Arc<LogChannel>);

    /// A run where the mixture is ten per cent rich everywhere: lambda 0.9 against a target
    /// of 1.0.
    fn rich(measured: f64, samples: usize) -> Four {
        let mut rpm = vec![0.0; samples];
        let mut load = vec![0.0; samples];
        for i in 0..samples {
            rpm[i] = 1000.0 + (i % 20) as f64 * 200.0;
            load[i] = 30.0 + (i % 10) as f64 * 15.0;
        }
        (
            Arc::new(LogChannel::new("RPM", "rpm", 0, &rpm, false)),
            Arc::new(LogChannel::new("MAP", "kPa", 0, &load, false)),
            Arc::new(LogChannel::new("Lambda", "", 3, &repeat(measured, samples), false)),
            Arc::new(LogChannel::new("Lambda target", "", 3, &repeat(1.0, samples), false)),
        )
    }

    fn cells(values: &Array2<Option<f64>>) -> Vec<f64> {
        values.as_slice().iter().flatten().copied().collect()
    }

    /// Confidence weighting off, for the tests that assert an exact correction.
    fn no_confidence() -> VeAnalysisSettings {
        VeAnalysisSettings {
            confidence_samples: 0,
            ..Default::default()
        }
    }

    #[test]
    fn a_grid_can_be_taken_from_the_log_itself() {
        let (rpm, load, _, _) = rich(0.9, 400);

        let grid = grid_from(&rpm, &load, 8, 6, 0, 399, None).unwrap();

        assert_eq!(8, grid.columns());
        assert_eq!(6, grid.rows());

        // Spread over what the channel actually did, and in order.
        assert!(grid.x.breakpoints[0] >= rpm.min());
        assert!(grid.x.breakpoints[grid.x.breakpoints.len() - 1] <= rpm.max());
        assert!(grid.x.breakpoints.windows(2).all(|p| p[1] > p[0]));
    }

    #[test]
    fn a_mixture_rich_of_target_asks_for_less_fuel() {
        // The direction is the thing worth being certain of. Lambda under target is richer than
        // asked for, which means the ECU thought there was more air than there was, which
        // means the number in the cell is too high.
        let (rpm, load, lambda, target) = rich(0.9, 400);

        let grid = grid_from(&rpm, &load, 4, 4, 0, 399, None).unwrap();
        let result = analyse(
            &grid,
            &rpm,
            &load,
            &lambda,
            &target,
            0,
            399,
            None,
            Some(&no_confidence()),
        )
        .unwrap();

        assert!(!result.is_empty());

        let changes = cells(&result.change_percent);
        assert!(!changes.is_empty());
        for c in &changes {
            assert!(*c < 0.0, "a rich cell asked for {c:.1}%");
            assert_equal(-10.0, *c, 1);
        }
    }

    #[test]
    fn a_mixture_lean_of_target_asks_for_more_fuel() {
        let (rpm, load, lambda, target) = rich(1.08, 400);

        let grid = grid_from(&rpm, &load, 4, 4, 0, 399, None).unwrap();
        let result = analyse(
            &grid,
            &rpm,
            &load,
            &lambda,
            &target,
            0,
            399,
            None,
            Some(&no_confidence()),
        )
        .unwrap();
        let changes = cells(&result.change_percent);

        assert!(!changes.is_empty());
        for c in changes {
            assert_equal(8.0, c, 1);
        }
    }

    #[test]
    fn with_no_current_values_there_is_no_suggested_value() {
        // Only an honest answer is available: how far out the cell is. Inventing a new number
        // would mean inventing the old one.
        let (rpm, load, lambda, target) = rich(0.9, 400);

        let grid = grid_from(&rpm, &load, 4, 4, 0, 399, None).unwrap();
        let result = analyse(&grid, &rpm, &load, &lambda, &target, 0, 399, None, None).unwrap();

        assert!(result.suggested.as_slice().iter().all(Option::is_none));
        assert!(result.change_percent.as_slice().iter().any(Option::is_some));
    }

    #[test]
    fn with_the_tunes_own_values_the_new_number_is_given_too() {
        // The better case, unchanged: a grid carrying the current fuelling gets a value to
        // write as well as a percentage.
        let (rpm, load, lambda, target) = rich(0.9, 400);

        let mut grid = grid_from(&rpm, &load, 4, 4, 0, 399, None).unwrap();
        for c in 0..4 {
            for r in 0..4 {
                grid.values[(c, r)] = 80.0;
            }
        }

        let result = analyse(
            &grid,
            &rpm,
            &load,
            &lambda,
            &target,
            0,
            399,
            None,
            Some(&no_confidence()),
        )
        .unwrap();
        let suggested = cells(&result.suggested);

        assert!(!suggested.is_empty());
        for v in suggested {
            assert_equal(72.0, v, 1);
        }
    }

    #[test]
    fn measuring_in_afr_against_a_lambda_target_is_refused() {
        // Found on a real log. A MaxxECU logs both AFR and lambda, nothing about the names says
        // they are on different scales, and dividing 12.5 by 0.9 reports every cell as fifteen
        // per cent lean: a full table of confident nonsense, which is worse than an empty one
        // because it looks like an answer.
        let (rpm, load, _, target) = rich(0.9, 400);

        let afr = LogChannel::new("AFR", "", 2, &repeat(13.2, 400), false);

        let grid = grid_from(&rpm, &load, 4, 4, 0, 399, None).unwrap();
        let result = analyse(&grid, &rpm, &load, &afr, &target, 0, 399, None, None).unwrap();

        assert!(result.has_problem());
        assert!(result.is_empty());

        // And it names both channels, because "pick a different one" is no use without saying
        // which two disagree.
        let problem = result.problem.unwrap();
        assert!(problem.contains("AFR"));
        assert!(problem.contains("Lambda target"));
    }

    #[test]
    fn a_badly_mistuned_engine_is_still_analysed() {
        // The guard must not catch a real answer. Twenty-five per cent out is a genuinely poor
        // tune and well inside what this should act on.
        let (rpm, load, lambda, target) = rich(1.25, 400);

        let grid = grid_from(&rpm, &load, 4, 4, 0, 399, None).unwrap();
        let settings = VeAnalysisSettings {
            max_change_percent: 30.0,
            confidence_samples: 0,
            ..Default::default()
        };
        let result = analyse(&grid, &rpm, &load, &lambda, &target, 0, 399, None, Some(&settings)).unwrap();

        assert!(!result.has_problem());
        assert!(!result.is_empty());
    }

    #[test]
    fn a_cell_crossed_twice_is_not_enough_to_act_on() {
        // What makes this trustworthy is what it refuses to do.
        let (rpm, load, lambda, target) = rich(0.9, 8);

        let grid = grid_from(&rpm, &load, 8, 8, 0, 7, None).unwrap();
        let settings = VeAnalysisSettings {
            minimum_samples: 12,
            ..Default::default()
        };
        let result = analyse(&grid, &rpm, &load, &lambda, &target, 0, 7, None, Some(&settings)).unwrap();

        assert!(result.is_empty());
        assert!(result.cells_thin > 0);
    }

    #[test]
    fn one_pass_never_asks_for_more_than_it_is_allowed() {
        // A cell read during an accel-enrichment event can imply a correction far larger than
        // the table is actually wrong by.
        let (rpm, load, lambda, target) = rich(0.4, 400);

        let grid = grid_from(&rpm, &load, 4, 4, 0, 399, None).unwrap();
        let settings = VeAnalysisSettings {
            max_change_percent: 15.0,
            confidence_samples: 0,
            ..Default::default()
        };
        let result = analyse(&grid, &rpm, &load, &lambda, &target, 0, 399, None, Some(&settings)).unwrap();

        for c in cells(&result.change_percent) {
            assert!(c.abs() <= 15.0001, "asked for {c:.1}%");
        }
    }
}
