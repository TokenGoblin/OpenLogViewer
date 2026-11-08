//! Ported from `WidebandDelay.cs`. Owned by package `w3-analysis`.
//!
//! Finds how long the wideband takes to see the mixture that was metered, by asking the log.
//!
//! The idea is short. Samples landing in one cell of the table were all taken at about the
//! same operating point, so the mixtures they measured ought to agree with each other. Pair
//! each cell with a reading taken too early or too late and readings from neighbouring
//! conditions leak into it, and the cell's samples start to disagree. So sweep the delay,
//! measure how much the readings within each cell disagree, and take the delay where they
//! agree best.
//!
//! This is signal alignment rather than tuning: it says nothing about whether the tune is
//! right, only about which reading belongs to which moment.
//!
//! **It needs the engine to have changed.** Held at one operating point, every delay pairs a
//! cell with readings from the same conditions and they all score alike. That is the absence
//! of evidence either way, and it is reported as such rather than resolved into whichever
//! candidate noise happened to favour.

use super::histogram_table::nearest;
use super::ve_analysis::{dotnet_max, dotnet_min};
use crate::filters::log_filter::SampleMask;
use crate::fmt;
use crate::model::{LogChannel, TuneTable};

/// One candidate delay and how well the log fits under it.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct DelayCandidate {
    /// The delay in samples.
    pub samples: usize,
    /// The same delay as a time.
    pub seconds: f64,
    /// Mean squared disagreement within cells at this delay: the number being minimised.
    /// Lower means the readings that land in a cell agree with each other more closely.
    pub disagreement: f64,
}

/// What a search of the log concluded about the sensor's delay.
#[derive(Clone, Debug, PartialEq)]
pub struct DelaySearchResult {
    /// The delay that fitted best, in samples.
    pub best_samples: usize,
    pub best_seconds: f64,
    /// Every candidate tried, in order, for drawing or for inspection.
    pub curve: Vec<DelayCandidate>,
    /// How much better the best candidate is than no delay at all, as a percentage of the
    /// disagreement at zero.
    pub improvement_percent: f64,
    /// Samples every candidate was scored over: the same set for each.
    pub samples_scored: usize,
    /// The shortest delay that fit about as well as the best one.
    pub low_seconds: f64,
    /// The longest delay that fit about as well as the best one.
    pub high_seconds: f64,
    /// Why the answer should not be believed, where that can be said. Set means
    /// `best_samples` is not an answer, whatever it holds.
    pub problem: Option<String>,
}

impl DelaySearchResult {
    /// True when no delay at all fits as well as the best one does.
    pub fn none_is_plausible(&self) -> bool {
        self.low_seconds <= 0.0
    }

    /// True when the band is one candidate wide: a delay pinned down.
    pub fn is_precise(&self) -> bool {
        self.high_seconds - self.low_seconds < 1e-9
    }

    pub fn has_problem(&self) -> bool {
        self.problem.as_ref().is_some_and(|p| !p.is_empty())
    }
}

/// The longest delay worth considering. Past about a second this stops being a pipe and a
/// sensor and becomes a way of pairing a reading with an unrelated part of the drive.
pub const MAXIMUM_SECONDS: f64 = 1.0;

/// Most candidates to try. On a fast log stepping one sample at a time would mean hundreds of
/// passes for a resolution far finer than the quantity is known to; the step widens instead.
const MAXIMUM_CANDIDATES: usize = 40;

/// Samples the search needs before it will answer at all. Fewer than this and the differences
/// between candidates are noise being read as a curve.
const MINIMUM_SAMPLES: usize = 200;

/// How much the choice of delay must matter at all before any of it is believed: the whole
/// sweep's range, as a percentage of its starting point.
///
/// This is the sensitivity of the log to the question, and it is deliberately not "how much
/// better the best candidate is than none". An engine held at one operating point produces a
/// flat sweep, and an engine with a genuinely undelayed sensor also produces no improvement
/// over zero, but its sweep is not flat, it climbs steeply away from zero. Two per cent.
const MINIMUM_SENSITIVITY_PERCENT: f64 = 2.0;

/// Sweeps the delay and reports which fits best.
///
/// `sample_interval` is seconds per sample, for reporting a time; `max_seconds` the longest
/// delay to consider (the C#'s default is [`MAXIMUM_SECONDS`]).
#[allow(clippy::too_many_arguments)]
pub fn find(
    table: &TuneTable,
    rpm: &LogChannel,
    load: &LogChannel,
    afr: &LogChannel,
    target: &LogChannel,
    first_sample: isize,
    last_sample: isize,
    sample_interval: f64,
    mask: Option<&SampleMask>,
    max_seconds: f64,
) -> DelaySearchResult {
    let length = rpm.len().min(load.len()).min(afr.len()).min(target.len()) as isize;
    let from = 0.max(first_sample.min(last_sample));
    let to = (length - 1).min(first_sample.max(last_sample));

    let longest: isize = if sample_interval > 0.0 {
        (dotnet_min(max_seconds, MAXIMUM_SECONDS) / sample_interval).round_ties_even() as i32 as isize
    } else {
        0
    };

    let longest = 0.max(longest.min(to - from));

    // Every candidate is scored over the same samples. Letting the window shrink as the delay
    // grows would score each candidate on a slightly different drive, and the comparison
    // between them is the whole answer.
    let last = to - longest;

    if longest == 0 || last < from {
        return nothing("This log is too short, or its samples too far apart, to look for a delay.".to_string());
    }

    let (from, last, longest) = (from as usize, last as usize, longest as usize);
    let rows = table.rows();

    // Cells do not move with the delay, only which reading is paired with them, so they are
    // worked out once.
    let mut cells = vec![-1isize; last - from + 1];
    let mut scored = 0usize;

    for i in from..=last {
        if mask.is_some_and(|m| !m.is_accepted(i)) {
            continue;
        }

        let (x, y, wanted) = (rpm.at(i), load.at(i), target.at(i));
        if x.is_nan() || y.is_nan() || wanted.is_nan() || wanted <= 0.0 {
            continue;
        }

        cells[i - from] = (nearest(&table.x.breakpoints, x) * rows + nearest(&table.y.breakpoints, y)) as isize;
        scored += 1;
    }

    if scored < MINIMUM_SAMPLES {
        return nothing(format!(
            "Only {} samples are usable here, which is too few to tell a delay from \
             noise. Widen the time range, or loosen the filters.",
            fmt::int(scored as i32, "N0")
        ));
    }

    let step = 1.max((longest as f64 / MAXIMUM_CANDIDATES as f64).ceil() as usize);
    let mut curve = Vec::new();

    let cell_count = table.columns() * rows;
    let mut sums = vec![0.0f64; cell_count];
    let mut squares = vec![0.0f64; cell_count];
    let mut counts = vec![0usize; cell_count];

    let mut delay = 0;
    while delay <= longest {
        sums.fill(0.0);
        squares.fill(0.0);
        counts.fill(0);

        for i in from..=last {
            let cell = cells[i - from];
            if cell < 0 {
                continue;
            }

            let at = i + delay;
            if mask.is_some_and(|m| !m.is_accepted(at)) {
                continue;
            }

            let measured = afr.at(at);
            if measured.is_nan() || measured <= 0.0 {
                continue;
            }

            let ratio = measured / target.at(i);
            let cell = cell as usize;

            sums[cell] += ratio;
            squares[cell] += ratio * ratio;
            counts[cell] += 1;
        }

        curve.push(DelayCandidate {
            samples: delay,
            seconds: delay as f64 * sample_interval,
            disagreement: disagreement(&sums, &squares, &counts),
        });

        delay += step;
    }

    judge(curve, scored)
}

/// Mean squared disagreement within cells: each cell's own spread about its own mean, pooled
/// over every cell and divided by the samples behind them.
///
/// A cell holding one sample contributes nothing: a single reading agrees with itself at
/// every delay, and counting it would dilute the measure with cells that cannot inform it.
fn disagreement(sums: &[f64], squares: &[f64], counts: &[usize]) -> f64 {
    let mut total = 0.0;
    let mut n = 0usize;

    for i in 0..counts.len() {
        if counts[i] < 2 {
            continue;
        }

        // Sum of squared deviations about the cell's mean.
        let deviation = squares[i] - (sums[i] * sums[i] / counts[i] as f64);

        // Rounding can carry this a hair below zero on a cell whose samples are identical.
        total += dotnet_max(0.0, deviation);
        n += counts[i];
    }

    if n == 0 { f64::NAN } else { total / n as f64 }
}

/// Reads the sweep, and decides whether it is an answer.
///
/// Three ways it is not. A curve with no usable figures in it at all. A curve so flat that the
/// delay makes no difference, which is what an engine held at one operating point produces.
/// And a minimum sitting on the last candidate, which is not a minimum but the edge of where we
/// looked.
///
/// A minimum at zero is none of those. It is the answer that the sensor's delay is shorter
/// than this log can resolve, reported as one so long as the sweep was sensitive enough for
/// that to mean something.
fn judge(curve: Vec<DelayCandidate>, scored: usize) -> DelaySearchResult {
    let mut best = curve[0];
    let mut highest = f64::NEG_INFINITY;

    for candidate in &curve {
        if !candidate.disagreement.is_finite() {
            continue;
        }

        if !best.disagreement.is_finite() || candidate.disagreement < best.disagreement {
            best = *candidate;
        }

        if candidate.disagreement > highest {
            highest = candidate.disagreement;
        }
    }

    let baseline = curve[0].disagreement;

    if !baseline.is_finite() || !best.disagreement.is_finite() || baseline <= 0.0 {
        let mut result = nothing("The readings in this log do not vary enough to find a delay from.".to_string());
        result.curve = curve;
        result.samples_scored = scored;
        return result;
    }

    // How far two of these figures can differ before the difference means something. Each is
    // a mean of squared deviations over `scored` samples, whose own sampling error is about
    // sqrt(2/n) of itself, so anything within that of the minimum fits indistinguishably well.
    let tolerance = best.disagreement * (2.0 / scored.max(1) as f64).sqrt();

    let (mut low, mut high) = (best.seconds, best.seconds);

    for candidate in &curve {
        if !candidate.disagreement.is_finite() {
            continue;
        }
        if candidate.disagreement > best.disagreement + tolerance {
            continue;
        }

        low = dotnet_min(low, candidate.seconds);
        high = dotnet_max(high, candidate.seconds);
    }

    // How much the answer depends on the delay at all. Flat means the log holds no evidence
    // either way, whatever its minimum happens to be.
    let sensitivity = (highest - best.disagreement) / baseline * 100.0;
    let at_the_edge = curve.len() > 1 && best.samples == curve[curve.len() - 1].samples;

    let mut result = DelaySearchResult {
        best_samples: best.samples,
        best_seconds: best.seconds,
        curve,
        improvement_percent: (baseline - best.disagreement) / baseline * 100.0,
        samples_scored: scored,
        low_seconds: low,
        high_seconds: high,
        problem: None,
    };

    if sensitivity < MINIMUM_SENSITIVITY_PERCENT {
        result.problem = Some(
            "Changing the delay barely changes the fit, so this log cannot say what it is. \
             That usually means it holds no sharp changes — every delay pairs a cell with \
             readings from the same conditions, so none of them can be told apart. Log a \
             few quick throttle changes and try again."
                .to_string(),
        );
        return result;
    }

    if at_the_edge {
        result.problem = Some(format!(
            "The fit was still improving at {} s, which is as far as this \
             looks. A delay that long is more than a sensor and a pipe, so this is \
             probably something else moving with time rather than a transport delay.",
            fmt::f64(best.seconds, "F2")
        ));
    }

    result
}

fn nothing(problem: String) -> DelaySearchResult {
    DelaySearchResult {
        best_samples: 0,
        best_seconds: 0.0,
        curve: Vec::new(),
        improvement_percent: 0.0,
        samples_scored: 0,
        low_seconds: 0.0,
        high_seconds: 0.0,
        problem: Some(problem),
    }
}
