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

/// Finding the sensor's delay from the log.
///
/// The tests that matter are the ones that build a drive with a known delay in it and check
/// that the number comes back, and the ones that make sure a log with nothing to learn from is
/// reported as such rather than resolved into whichever candidate the noise favoured.
#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Array2, TuneAxis};

    const INTERVAL: f64 = 0.05; // 20 Hz

    /// .NET's `System.Random(seed)`: the Knuth subtractive generator the seeded constructor
    /// uses, so the synthetic drives match the C# tests' exactly.
    struct DotNetRandom {
        seed_array: [i32; 56],
        inext: usize,
        inextp: usize,
    }

    impl DotNetRandom {
        fn new(seed: i32) -> Self {
            const MBIG: i32 = i32::MAX;
            const MSEED: i32 = 161_803_398;
            let mut seed_array = [0i32; 56];
            let subtraction = if seed == i32::MIN { i32::MAX } else { seed.abs() };
            let mut mj = MSEED - subtraction;
            seed_array[55] = mj;
            let mut mk = 1;
            let mut ii = 0usize;
            for _ in 1..55 {
                ii += 21;
                if ii >= 55 {
                    ii -= 55;
                }
                seed_array[ii] = mk;
                mk = mj - mk;
                if mk < 0 {
                    mk += MBIG;
                }
                mj = seed_array[ii];
            }
            for _ in 1..5 {
                for i in 1..56usize {
                    let mut n = i + 30;
                    if n >= 55 {
                        n -= 55;
                    }
                    seed_array[i] = seed_array[i].wrapping_sub(seed_array[1 + n]);
                    if seed_array[i] < 0 {
                        seed_array[i] += MBIG;
                    }
                }
            }
            Self {
                seed_array,
                inext: 0,
                inextp: 21,
            }
        }

        fn next_double(&mut self) -> f64 {
            self.inext += 1;
            if self.inext >= 56 {
                self.inext = 1;
            }
            self.inextp += 1;
            if self.inextp >= 56 {
                self.inextp = 1;
            }
            let mut ret = self.seed_array[self.inext].wrapping_sub(self.seed_array[self.inextp]);
            if ret == i32::MAX {
                ret -= 1;
            }
            if ret < 0 {
                ret += i32::MAX;
            }
            self.seed_array[self.inext] = ret;
            f64::from(ret) * (1.0 / f64::from(i32::MAX))
        }
    }

    fn grid() -> TuneTable {
        let (columns, rows) = (8usize, 4usize);
        TuneTable {
            name: "VE".into(),
            x: TuneAxis {
                constant: "rpm".into(),
                units: "RPM".into(),
                breakpoints: (0..columns).map(|i| 1000.0 + (i as f64 * 500.0)).collect(),
            },
            y: TuneAxis {
                constant: "map".into(),
                units: "kPa".into(),
                breakpoints: (0..rows).map(|i| 30.0 + (i as f64 * 40.0)).collect(),
            },
            values: Array2::new(columns, rows),
            units: "%".into(),
        }
    }

    struct Drive {
        rpm: LogChannel,
        load: LogChannel,
        afr: LogChannel,
        target: LogChannel,
    }

    /// A drive that moves around the map, where the mixture each operating point produces is a
    /// fixed property of that point, so a reading identifies the point that caused it, and the
    /// wideband reports it `delay` samples later.
    fn drive(delay: usize, samples: usize, seed: i32, noise: f64) -> Drive {
        let mut rng = DotNetRandom::new(seed);
        let mut rpm = vec![0.0; samples];
        let mut load = vec![0.0; samples];
        let mut afr = vec![0.0; samples];
        let mut target = vec![0.0; samples];

        // The mixture each cell produces, fixed for the run.
        let mut true_afr = vec![0.0; samples];

        let (mut r, mut l) = (2000.0, 60.0);

        for i in 0..samples {
            // Wander, with occasional sharp steps: the transients are what carry the
            // information a delay can be found from.
            if i % 60 == 0 {
                r = 1200.0 + (rng.next_double() * 3000.0);
                l = 35.0 + (rng.next_double() * 120.0);
            } else {
                r += (rng.next_double() - 0.5) * 40.0;
                l += (rng.next_double() - 0.5) * 3.0;
            }

            rpm[i] = r;
            load[i] = l;
            target[i] = 13.5;

            // A mixture that depends on where the engine is, so which reading belongs to
            // which moment is answerable at all.
            true_afr[i] =
                12.0 + ((r - 1200.0) / 3000.0 * 2.0) + ((l - 35.0) / 120.0 * 1.5) + ((rng.next_double() - 0.5) * noise);
        }

        // The sensor reports what happened `delay` samples ago.
        for i in 0..samples {
            afr[i] = true_afr[i.saturating_sub(delay)];
        }

        Drive {
            rpm: LogChannel::new("RPM", "rpm", 0, &rpm, false),
            load: LogChannel::new("MAP", "kPa", 0, &load, false),
            afr: LogChannel::new("AFR", "", 2, &afr, false),
            target: LogChannel::new("Target", "", 2, &target, false),
        }
    }

    fn find_in(delay: usize, samples: usize, noise: f64, mask: Option<&SampleMask>) -> DelaySearchResult {
        let d = drive(delay, samples, 11, noise);
        find(
            &grid(),
            &d.rpm,
            &d.load,
            &d.afr,
            &d.target,
            0,
            samples as isize - 1,
            INTERVAL,
            mask,
            MAXIMUM_SECONDS,
        )
    }

    fn find_simple(delay: usize) -> DelaySearchResult {
        find_in(delay, 3000, 0.0, None)
    }

    fn assert_equal(expected: f64, actual: f64, precision: i32) {
        let scale = 10f64.powi(precision);
        assert_eq!(
            (expected * scale).round_ties_even(),
            (actual * scale).round_ties_even(),
            "expected {expected}, got {actual}"
        );
    }

    // ----- finding a delay that is there ------------------------------------

    #[test]
    fn a_delay_put_into_the_log_is_found_again() {
        for delay in [0usize, 2, 4, 6, 10] {
            let result = find_simple(delay);

            assert!(!result.has_problem(), "{delay}: {:?}", result.problem);
            assert_eq!(delay, result.best_samples);
            assert_equal(delay as f64 * INTERVAL, result.best_seconds, 6);
        }
    }

    #[test]
    fn an_undelayed_sensor_is_an_answer_rather_than_a_shrug() {
        // Zero improvement over zero delay and a flat log both look like "no delay helps", but
        // only one of them is uninformative. Here the sweep climbs steeply away from zero,
        // which is what says zero is the answer.
        let result = find_simple(0);

        assert!(!result.has_problem(), "{:?}", result.problem);
        assert_eq!(0, result.best_samples);

        // It got worse as the delay grew, which is the evidence for none.
        assert!(result.curve[result.curve.len() - 1].disagreement > result.curve[0].disagreement);
    }

    #[test]
    fn the_answer_survives_noise_on_the_readings() {
        // A wideband is not a clean instrument. The alignment should still be recoverable
        // through a reading that wanders either side of the truth.
        let result = find_in(5, 3000, 0.6, None);

        assert!(!result.has_problem(), "{:?}", result.problem);
        assert!((4..=6).contains(&result.best_samples));
    }

    #[test]
    fn a_delay_worth_finding_is_worth_reporting() {
        let result = find_simple(6);

        // The whole point of the number: it is much better than assuming none.
        assert!(
            result.improvement_percent > 20.0,
            "improvement was only {:.1}%",
            result.improvement_percent
        );
    }

    #[test]
    fn the_curve_is_reported_so_the_shape_can_be_seen() {
        let result = find_simple(4);

        assert!(!result.curve.is_empty());
        assert_eq!(0, result.curve[0].samples);

        // Ascending, one entry per candidate, and each carrying its own time.
        assert!(result.curve.windows(2).all(|p| p[1].samples > p[0].samples));
        for c in &result.curve {
            assert_equal(c.samples as f64 * INTERVAL, c.seconds, 6);
        }
    }

    #[test]
    fn the_minimum_is_a_real_minimum_rather_than_the_end_of_the_sweep() {
        let result = find_simple(5);

        let best = result
            .curve
            .iter()
            .take_while(|c| c.samples != result.best_samples)
            .count();

        assert!(best > 0, "the best candidate should not be the first");
        assert!(best < result.curve.len() - 1, "nor the last");

        // Disagreement rises either side of it, which is what makes it an answer.
        assert!(result.curve[best - 1].disagreement > result.curve[best].disagreement);
        assert!(result.curve[best + 1].disagreement > result.curve[best].disagreement);
    }

    // ----- refusing to answer -----------------------------------------------

    #[test]
    fn a_log_held_at_one_operating_point_cannot_say_and_does_not_pretend_to() {
        // Every delay pairs a cell with readings from the same conditions, so none can be told
        // from another. Returning the minimum of that sweep would be inventing a measurement.
        let samples = 3000;
        let mut rpm = vec![0.0; samples];
        let mut load = vec![0.0; samples];
        let mut afr = vec![0.0; samples];
        let target = vec![13.0; samples];

        let mut rng = DotNetRandom::new(3);
        for i in 0..samples {
            rpm[i] = 2500.0 + ((rng.next_double() - 0.5) * 20.0);
            load[i] = 80.0 + ((rng.next_double() - 0.5) * 2.0);
            afr[i] = 13.0 + ((rng.next_double() - 0.5) * 0.4);
        }

        let result = find(
            &grid(),
            &LogChannel::new("RPM", "rpm", 0, &rpm, false),
            &LogChannel::new("MAP", "kPa", 0, &load, false),
            &LogChannel::new("AFR", "", 2, &afr, false),
            &LogChannel::new("Target", "", 2, &target, false),
            0,
            samples as isize - 1,
            INTERVAL,
            None,
            MAXIMUM_SECONDS,
        );

        assert!(result.has_problem());
        assert!(result.problem.unwrap().contains("cannot say"));
    }

    #[test]
    fn too_few_samples_is_refused_rather_than_answered() {
        let result = find_in(4, 120, 0.0, None);

        assert!(result.has_problem());
        assert!(result.problem.unwrap().contains("too few"));
    }

    #[test]
    fn a_log_too_short_to_hold_the_sweep_is_refused() {
        let result = find(
            &grid(),
            &LogChannel::new("RPM", "rpm", 0, &[1000.0, 2000.0], false),
            &LogChannel::new("MAP", "kPa", 0, &[40.0, 50.0], false),
            &LogChannel::new("AFR", "", 2, &[13.0, 13.0], false),
            &LogChannel::new("Target", "", 2, &[13.0, 13.0], false),
            0,
            1,
            INTERVAL,
            None,
            MAXIMUM_SECONDS,
        );

        assert!(result.has_problem());
    }

    #[test]
    fn a_minimum_on_the_last_candidate_is_reported_as_suspect() {
        // A delay longer than the sweep looks like a fit that never stopped improving. That is
        // the edge of where we looked, not a minimum.
        let result = find_simple(30); // 1.5 s, past the 1.0 s ceiling

        assert!(result.has_problem());
        assert!(result.problem.unwrap().contains("still improving"));
    }

    #[test]
    fn the_sweep_stops_at_the_ceiling_however_long_the_log_is() {
        let result = find_in(4, 20000, 0.0, None);

        for c in &result.curve {
            assert!(
                c.seconds <= MAXIMUM_SECONDS + 1e-9,
                "candidate at {} s is past the ceiling",
                c.seconds
            );
        }
    }

    #[test]
    fn zero_sample_interval_is_refused_rather_than_divided_by() {
        let d = drive(4, 3000, 11, 0.0);

        let result = find(
            &grid(),
            &d.rpm,
            &d.load,
            &d.afr,
            &d.target,
            0,
            2999,
            0.0,
            None,
            MAXIMUM_SECONDS,
        );

        assert!(result.has_problem());
    }

    // ----- what it is and is not about --------------------------------------

    #[test]
    fn a_mistuned_engine_aligns_just_as_well() {
        // This is signal alignment, not tuning. Every cell is judged against its own readings,
        // never against the target, so how far out the tune is does not enter into it.
        let d = drive(5, 3000, 11, 0.0);

        let lean = LogChannel::new("Target", "", 2, &vec![9.0; d.target.len()], false);

        let tuned = find(
            &grid(),
            &d.rpm,
            &d.load,
            &d.afr,
            &d.target,
            0,
            2999,
            INTERVAL,
            None,
            MAXIMUM_SECONDS,
        );
        let mistuned = find(
            &grid(),
            &d.rpm,
            &d.load,
            &d.afr,
            &lean,
            0,
            2999,
            INTERVAL,
            None,
            MAXIMUM_SECONDS,
        );

        assert_eq!(tuned.best_samples, mistuned.best_samples);
    }

    #[test]
    fn filtered_samples_are_left_out_of_the_search() {
        // A sample the user excluded must not decide the delay, on either side of the pairing.
        let accepted: Vec<bool> = (0..3000).map(|i| i < 1500).collect();
        let mask = SampleMask::new(accepted, 1500, 3000, Vec::new(), true);

        let result = find_in(5, 3000, 0.0, Some(&mask));

        assert!(!result.has_problem(), "{:?}", result.problem);
        assert_eq!(5, result.best_samples);
        assert!(result.samples_scored <= 1500);
    }

    #[test]
    fn every_candidate_is_scored_over_the_same_samples() {
        // Otherwise each candidate is scored on a slightly different drive, and the comparison
        // between them is the whole answer.
        let result = find_simple(4);

        assert!(result.samples_scored > 0);
        assert!(result.curve.iter().all(|c| c.disagreement.is_finite()));
    }

    // ----- how wide the answer really is ------------------------------------

    #[test]
    fn the_band_covers_the_candidates_that_fit_about_as_well() {
        let result = find_simple(5);

        assert!(!result.has_problem(), "{:?}", result.problem);
        assert!(result.low_seconds <= result.best_seconds && result.best_seconds <= result.high_seconds);
    }

    #[test]
    fn a_clean_log_pins_the_delay_down() {
        // No noise on the readings, plenty of transients: the minimum is sharp and the band
        // should close on it.
        let result = find_simple(5);

        assert!(
            result.is_precise(),
            "band was {}..{} s",
            result.low_seconds,
            result.high_seconds
        );
        assert!(!result.none_is_plausible());
    }

    #[test]
    fn a_noisy_log_widens_the_band_rather_than_pretending_to_precision() {
        // The same delay, read through a noisier sensor. The answer is no longer a point, and
        // saying so is the difference between a measurement and a number that merely looks like
        // one.
        let clean = find_simple(5);
        let noisy = find_in(5, 3000, 2.5, None);

        assert!(
            noisy.high_seconds - noisy.low_seconds >= clean.high_seconds - clean.low_seconds,
            "noisy band {}..{}, clean {}..{}",
            noisy.low_seconds,
            noisy.high_seconds,
            clean.low_seconds,
            clean.high_seconds
        );
    }

    #[test]
    fn the_band_is_narrower_with_more_evidence() {
        // Tolerance falls as the square root of the sample count, so a longer drive should pin
        // the same delay down more tightly.
        let brief = find_in(5, 1200, 2.5, None);
        let ample = find_in(5, 12000, 2.5, None);

        assert!(
            ample.high_seconds - ample.low_seconds <= brief.high_seconds - brief.low_seconds,
            "12,000 samples gave {}..{}, 1,200 gave {}..{}",
            ample.low_seconds,
            ample.high_seconds,
            brief.low_seconds,
            brief.high_seconds
        );
    }

    #[test]
    fn an_undelayed_sensor_has_none_inside_its_band() {
        let result = find_simple(0);

        assert!(result.none_is_plausible());
        assert_eq!(0.0, result.low_seconds);
    }
}
