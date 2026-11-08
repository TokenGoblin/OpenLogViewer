//! Ported from `LogInsights.cs`. Owned by package `w4-insights`.
//!
//! What a datalog says about the engine that produced it.
//!
//! **Every finding here is arithmetic on the samples, not a rule of thumb.** A tuner can
//! already see the traces; what they cannot see is that the mixture under boost is four
//! tenths lean of target with a standard error of six hundredths, which is a real
//! difference, while the same four tenths at idle over nine samples is not. The whole value
//! of this is telling those two apart.
//!
//! So each finding carries what it rests on, and a finding that cannot be made safely is not
//! made. Three rules throughout:
//!
//! * A claim about a difference states its standard error. A mean of a handful of samples is
//!   not evidence and is reported as "not enough data" rather than as a small effect.
//! * Where outliers decide the answer, the median and the percentiles are used rather than
//!   the mean.
//! * Silence is a finding. A channel that is absent, or a condition never reached, is said
//!   out loud, because "no warning" and "never checked" look identical otherwise and only
//!   one of them is reassuring.

use std::cell::Cell;
use std::cmp::Ordering;
use std::collections::HashSet;
use std::fmt;

use crate::analysis::ve_analysis;
use crate::analysis::wideband_delay::{self, MAXIMUM_SECONDS};
use crate::base::tuning_math::KPA_PER_PSI;
use crate::channels::channel_roles::{self, ChannelRole};
use crate::fmt as nf;
use crate::model::{LogChannel, LogDocument};

/// How much attention a finding deserves.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum InsightLevel {
    /// Checked, and it is as it should be. Worth saying so.
    Good,
    /// Nothing to act on, but something a tuner would want to know.
    Note,
    /// Worth looking at before the next drive.
    Watch,
    /// Something that damages engines. Said first and said plainly.
    Warning,
    /// The log cannot answer this, usually a channel that is not in it.
    Unanswered,
}

impl InsightLevel {
    /// The C# enum's name, as `ToString()` gives it.
    pub fn name(self) -> &'static str {
        match self {
            Self::Good => "Good",
            Self::Note => "Note",
            Self::Watch => "Watch",
            Self::Warning => "Warning",
            Self::Unanswered => "Unanswered",
        }
    }
}

/// One thing a log has to say, with the arithmetic that says it.
#[derive(Clone, Debug, PartialEq)]
pub struct LogInsight {
    /// How much attention it deserves.
    pub level: InsightLevel,
    pub topic: String,
    /// The finding, in one line.
    pub title: String,
    /// What was measured and what it means.
    pub detail: String,
    /// The numbers behind it, so a tuner can disagree with the conclusion without having to
    /// take the measurement on trust.
    pub evidence: String,
    /// How many samples the finding rests on.
    pub samples: i32,
}

impl LogInsight {
    fn new(level: InsightLevel, topic: &str, title: String, detail: String, evidence: String, samples: i32) -> Self {
        Self {
            level,
            topic: topic.to_string(),
            title,
            detail,
            evidence,
            samples,
        }
    }
}

impl fmt::Display for LogInsight {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "[{}] {} — {}", self.level.name(), self.title, self.detail)
    }
}

/// Below this a mixture reading is not the engine's steady state.
const MINIMUM_SAMPLES: usize = 30;

/// Throttle movement, per cent a second, above which fuelling is transient.
const SETTLED_THROTTLE_RATE: f64 = 20.0;

// ----- number formatting (en-US, as the C# interpolation does) ----------------------------

fn f(v: f64, format: &str) -> String {
    nf::f64(v, format)
}

fn n0(v: usize) -> String {
    nf::int(v as i32, "N0")
}

fn n0f(v: f64) -> String {
    nf::f64(v, "N0")
}

/// A signed percentage as `{x:+0.0;−0.0;0.0}`.
fn signed(v: f64) -> String {
    nf::f64(v, "+0.0;−0.0;0.0")
}

fn plural(count: usize) -> &'static str {
    if count == 1 { "" } else { "s" }
}

fn count_i32(n: usize) -> i32 {
    n as i32
}

/// How far the fuelling was out, as a fraction: delivered against required.
///
/// **Not the difference between two AFR numbers.** A raw difference is in units of the
/// target, so a tenth at 12:1 under boost and a tenth at 16:1 on the overrun are the same
/// number and nothing like the same error. This is a ratio, which cancels the target.
///
/// **And the closed loop is folded back in**, which is the important part. Measured mixture
/// is what the map did plus whatever the loop rescued. The correction is divided out rather
/// than multiplied in: a loop pulling ten per cent out is covering a map ten per cent rich.
/// If a firmware turns out to report the reciprocal, this sign flips, so the assumption is
/// stated in the evidence rather than buried here.
///
/// Returns per cent: positive is rich, negative lean. NaN where unusable.
fn fuel_error(e: &Engine, at: usize, mixture_at: usize) -> f64 {
    let (Some(afr), Some(target)) = (e.afr, e.target) else {
        return f64::NAN;
    };

    let measured = afr.at(mixture_at);
    let wanted = target.at(at);

    if measured.is_nan() || wanted.is_nan() || measured <= 0.0 || wanted <= 0.0 {
        return f64::NAN;
    }

    let mut ratio = wanted / measured;

    if let Some(trim) = e.correction {
        let correction = trim.at(at);

        // A correction of nought would be the controller commanding no fuel at all, which is
        // a channel that is not what it appears to be.
        if !correction.is_nan() && correction > 1.0 {
            ratio *= 100.0 / correction;
        }
    }

    (ratio - 1.0) * 100.0
}

/// Everything a log has to say, most serious first.
pub fn from(log: &LogDocument) -> Vec<LogInsight> {
    if log.sample_count() < 2 {
        return vec![unanswered(
            "The log",
            "There is nothing in this log to measure.".into(),
            "A log needs at least a couple of samples.".into(),
            0,
        )];
    }

    let mut engine = Engine::new(log);
    engine.measure_delay();
    let e = &engine;

    let mut found = Vec::new();
    found.extend(mixture_against_target(e));
    found.extend(mixture_under_load(e));
    found.extend(closed_loop(e));
    found.extend(injector_headroom(e));
    found.extend(dead_time_and_voltage(e));
    found.extend(knock(e));
    found.extend(warmup(e));
    found.extend(manifold_against_ambient(e));
    found.extend(pressure_reference(e));
    found.extend(idle_steadiness(e));
    found.extend(stuck_channels(e));
    found.extend(sample_rate(e));
    found.extend(mixture_delay(e));
    found.extend(coverage(e));

    // Worst first, and within a level the best-evidenced first: a warning resting on two
    // thousand samples belongs above one resting on thirty. (A stable sort, as OrderBy is.)
    found.sort_by(|a, b| {
        rank(b.level)
            .cmp(&rank(a.level))
            .then_with(|| b.samples.cmp(&a.samples))
    });
    found
}

fn rank(level: InsightLevel) -> i32 {
    match level {
        InsightLevel::Warning => 4,
        InsightLevel::Watch => 3,
        InsightLevel::Note => 2,
        InsightLevel::Good => 1,
        InsightLevel::Unanswered => 0,
    }
}

// ----- the log, with the channels this needs already found -----------------------------

struct Engine<'a> {
    log: &'a LogDocument,
    count: usize,
    rpm: Option<&'a LogChannel>,
    map: Option<&'a LogChannel>,
    afr: Option<&'a LogChannel>,
    target: Option<&'a LogChannel>,
    throttle: Option<&'a LogChannel>,
    coolant: Option<&'a LogChannel>,
    duty: Option<&'a LogChannel>,
    battery: Option<&'a LogChannel>,
    knock: Option<&'a LogChannel>,
    correction: Option<&'a LogChannel>,
    warmup: Option<&'a LogChannel>,
    baro: Option<&'a LogChannel>,
    boost: Option<&'a LogChannel>,
    /// How many samples the mixture reads behind, as whole samples.
    ///
    /// The wideband measures the charge that was injected several frames ago. Ignoring it
    /// smears every conclusion across the boundary between one operating point and the next.
    delay: usize,
    spacing_cache: Cell<Option<f64>>,
}

impl<'a> Engine<'a> {
    fn new(log: &'a LogDocument) -> Self {
        let find = |role| channel_roles::find(log, role).map(|c| c.as_ref());
        Self {
            log,
            count: log.sample_count(),
            rpm: find(ChannelRole::EngineSpeed),
            map: find(ChannelRole::ManifoldPressure),
            afr: find(ChannelRole::Mixture),
            target: find(ChannelRole::MixtureTarget),
            throttle: find(ChannelRole::Throttle),
            coolant: find(ChannelRole::Coolant),
            duty: find(ChannelRole::InjectorDuty),
            battery: find(ChannelRole::BatteryVoltage),
            knock: find(ChannelRole::KnockRetard),
            correction: find(ChannelRole::MixtureCorrection),
            warmup: find(ChannelRole::WarmupCorrection),
            baro: find(ChannelRole::Barometric),
            boost: find(ChannelRole::Boost),
            delay: 0,
            spacing_cache: Cell::new(None),
        }
    }

    fn time(&self, i: usize) -> f64 {
        self.log.time().at(i)
    }

    /// Samples where the engine was turning fast enough to be running.
    fn running(&self, i: usize) -> bool {
        self.rpm.is_some_and(|rpm| rpm.at(i) > 400.0)
    }

    /// Samples where the throttle was still.
    ///
    /// Fuelling during a tip-in is accelerator enrichment rather than the table, and judging
    /// a table by it is judging the wrong thing. Where there is no throttle channel every
    /// sample counts, which is worse but is not a reason to say nothing.
    fn settled(&self, i: usize) -> bool {
        let Some(tps) = self.throttle else {
            return true;
        };
        if i < 1 {
            return true;
        }

        let dt = self.time(i) - self.time(i - 1);
        if dt <= 0.0 || dt.is_nan() {
            return true;
        }

        let rate = (tps.at(i) - tps.at(i - 1)).abs() / dt;

        !rate.is_nan() && rate < SETTLED_THROTTLE_RATE
    }

    fn measure_delay(&mut self) {
        let (Some(afr), Some(target)) = (self.afr, self.target) else {
            return;
        };
        let (Some(rpm), Some(map)) = (self.rpm, self.map) else {
            return;
        };
        if self.count < 200 {
            return;
        }

        let interval = self.spacing();
        if interval <= 0.0 {
            return;
        }

        let last = self.count as isize - 1;

        // A grid the log cannot fill means nothing to align against.
        let Ok(grid) = ve_analysis::grid_from(rpm, map, 8, 8, 0, last, None) else {
            return;
        };

        let found = wideband_delay::find(&grid, rpm, map, afr, target, 0, last, interval, None, MAXIMUM_SECONDS);

        if !found.has_problem() && !found.none_is_plausible() {
            self.delay = (found.best_seconds / interval).round_ties_even() as i32 as usize;
        }
    }

    /// The usual gap between samples, in seconds.
    fn spacing(&self) -> f64 {
        if let Some(s) = self.spacing_cache.get() {
            return s;
        }
        let s = sample_interval(self);
        self.spacing_cache.set(Some(s));
        s
    }

    /// Whether the engine held still either side of this sample.
    ///
    /// A rate of change catches a throttle being moved; it does not catch an engine that is
    /// oscillating about a point, which is just as unsteady and just as poisonous to a
    /// fuelling average. Half a second either side, which is the shortest window in which
    /// "steady" means anything.
    fn still(&self, i: usize) -> bool {
        let half = ((0.5 / self.spacing().max(1e-6)).round_ties_even() as i32).max(1) as usize;

        self.within(self.rpm, i, half, 120.0) && self.within(self.map, i, half, 4.0)
    }

    fn within(&self, channel: Option<&LogChannel>, at: usize, half: usize, allowed: f64) -> bool {
        let Some(c) = channel else {
            return true;
        };

        let from = at.saturating_sub(half);
        let to = (at + half).min(self.count - 1);

        let mut low = f64::MAX;
        let mut high = f64::MIN;

        for i in from..=to {
            let v = c.at(i);
            if v.is_nan() {
                continue;
            }

            low = low.min(v);
            high = high.max(v);
        }

        low > high || high - low <= allowed * 2.0
    }

    /// Whether the engine was warm *at this sample*.
    ///
    /// Per sample, not per log. Asking only whether the log ever got warm lets every sample
    /// from a cold start onward through, all of it carrying warmup enrichment on top of the
    /// table. Averaged in, that reads as a rich map and is nothing of the sort.
    fn warm(&self, i: usize) -> bool {
        let Some(clt) = self.coolant else {
            return true;
        };

        let now = clt.at(i);
        if now.is_nan() {
            return true;
        }

        now >= if fahrenheit(clt) { 160.0 } else { 71.0 }
    }
}

/// `Units.Contains('F', OrdinalIgnoreCase) && !Units.Contains('C', OrdinalIgnoreCase)`.
fn fahrenheit(channel: &LogChannel) -> bool {
    let units = channel.units();
    units.chars().any(|c| c.eq_ignore_ascii_case(&'F')) && !units.chars().any(|c| c.eq_ignore_ascii_case(&'C'))
}

// ----- the findings --------------------------------------------------------------------

/// Whether the mixture matched what was asked for, over the whole log.
///
/// Judged on the mean error against its own standard error, which is what separates a table
/// that is genuinely out from one that merely wanders.
fn mixture_against_target(e: &Engine) -> Vec<LogInsight> {
    let Some(afr) = e.afr else {
        return vec![unanswered(
            "Mixture",
            "No wideband reading in this log, so nothing here can judge fuelling.".into(),
            "Log an AFR or lambda channel and every mixture finding below becomes available.".into(),
            0,
        )];
    };

    if e.target.is_none() {
        return vec![unanswered(
            "Mixture",
            "No AFR target in this log, so the mixture cannot be judged against anything.".into(),
            format!(
                "\"{}\" is here but nothing says what it should have been. Add the target channel to the datalog.",
                afr.name()
            ),
            0,
        )];
    }

    let mut errors: Vec<f64> = Vec::new();
    let mut running = 0usize;

    for i in 0..e.count.saturating_sub(e.delay) {
        if !e.running(i) {
            continue;
        }

        running += 1;

        if !e.settled(i) || !e.warm(i) || !e.still(i) {
            continue;
        }

        // The mixture is read where the sensor saw it, the conditions where the fuel was
        // metered.
        let error = fuel_error(e, i, i + e.delay);
        if !error.is_nan() {
            errors.push(error);
        }
    }

    if errors.len() < MINIMUM_SAMPLES {
        return vec![unanswered(
            "Mixture",
            "Not enough settled running to judge the mixture.".into(),
            format!(
                "{} of {} running samples had the engine warm, the throttle still and speed and load holding steady either side. About {MINIMUM_SAMPLES} are needed before an average means anything.",
                errors.len(),
                n0(running)
            ),
            count_i32(errors.len()),
        )];
    }

    let (mean, error2) = mean_and_standard_error(&errors);

    // The median within the settled set rather than the mean: a wideband throws outliers on
    // transients that survive any filter, and one of them moves a mean and does not move a
    // median.
    let median = percentile(&errors, 50.0);
    let kept = 100.0 * errors.len() as f64 / running.max(1) as f64;

    let mut numbers = format!(
        "median {}% of required fuel, mean {} ± {} (standard error), {} of {} running samples kept ({}%)",
        signed(median),
        signed(mean),
        f(error2, "0.0"),
        n0(errors.len()),
        n0(running),
        f(kept, "0")
    );
    if e.delay > 0 {
        numbers += &format!(", mixture read {} samples late", e.delay);
    }
    if e.correction.is_some() {
        numbers += ", closed-loop correction divided back out";
    }

    // Three standard errors before a difference is called real, and two per cent of the fuel
    // required before it is called worth an afternoon.
    let real = median.abs() > 3.0 * error2;
    let matters = median.abs() > 2.0;

    if !real || !matters {
        return vec![LogInsight::new(
            InsightLevel::Good,
            "Mixture",
            "Fuelling is on target overall.".into(),
            format!(
                "The map delivers within {}% of the fuel asked for, which is {}{}",
                f(median.abs(), "0.0"),
                if real {
                    "a real difference but too small to chase."
                } else {
                    "within what the measurement can resolve."
                },
                survival(kept)
            ),
            numbers,
            count_i32(errors.len()),
        )];
    }

    let title = if median > 0.0 {
        format!("The map is delivering {}% more fuel than asked for.", f(median, "0.0"))
    } else {
        format!(
            "The map is delivering {}% less fuel than asked for.",
            f(median.abs(), "0.0")
        )
    };

    let mut detail = String::from(
        "Measured as delivered against required rather than as a difference between two AFR numbers, so cells with different targets can be counted together",
    );
    detail += if e.correction.is_some() {
        " — and with the closed loop's correction divided back out, so this is the map's own error rather than what is left after the loop rescued it."
    } else {
        "."
    };
    detail += if median > 0.0 {
        " Rich washes bores and hides the real error."
    } else {
        " Lean costs power and, held under load, costs pistons."
    };
    detail += &survival(kept);

    vec![LogInsight::new(
        InsightLevel::Watch,
        "Mixture",
        title,
        detail,
        numbers,
        count_i32(errors.len()),
    )]
}

/// The same question again, asked only where it can hurt.
///
/// A mixture error at idle is a curiosity. The same error at high load is what melts things,
/// and averaging the two together buries it.
fn mixture_under_load(e: &Engine) -> Vec<LogInsight> {
    let (Some(_), Some(_), Some(map)) = (e.afr, e.target, e.map) else {
        return vec![];
    };

    let (ambient, measured) = atmosphere(e, map);

    // Nine tenths of atmospheric is the right line when atmospheric is known. When it was
    // assumed from the highest reading in the log, that line lands at nine tenths of *peak
    // boost* and throws away nearly all the loaded running this exists to judge, so the load
    // is taken relatively instead, from the log's own spread.
    let threshold = if measured { ambient * 0.9 } else { loaded_from(e, map) };

    let mut errors: Vec<f64> = Vec::new();
    let mut highest = f64::MIN;

    for i in 0..e.count.saturating_sub(e.delay) {
        if !e.running(i) {
            continue;
        }

        let load = map.at(i);
        if load.is_nan() || load < threshold {
            continue;
        }

        let short = fuel_error(e, i, i + e.delay);
        if short.is_nan() {
            continue;
        }

        errors.push(short);
        highest = highest.max(load);
    }

    let place = if ambient > 0.0 {
        format!(
            "above {} {} — near and above atmospheric, which here reads {}",
            n0f(threshold),
            map.units(),
            n0f(ambient)
        )
    } else {
        "at high load".to_string()
    };

    if errors.len() < MINIMUM_SAMPLES {
        return vec![LogInsight::new(
            InsightLevel::Note,
            "Mixture under load",
            "Not enough high-load running in this log to judge it.".into(),
            format!(
                "Only {} samples were {place}. A log that never loads the engine cannot say whether it is safe when loaded — which is the part worth knowing.",
                errors.len()
            ),
            format!("{} samples above {} {}", errors.len(), n0f(threshold), map.units()),
            count_i32(errors.len()),
        )];
    }

    let (mean, error) = mean_and_standard_error(&errors);

    // Counted rather than taken as a percentile. A percentile can only find a spike that
    // affects more of the log than the percentile leaves out, and one excursion is what
    // damages a piston. So the samples that are dangerously lean are simply counted.
    //
    // Three of them before it is called a warning: one reading can be the wideband catching
    // a gear change or a misfire. Six per cent short of the fuel asked for is where a
    // mixture error stops costing power and starts costing pistons.
    const DANGEROUS: f64 = -6.0;

    let spikes = errors.iter().filter(|&&v| v < DANGEROUS).count();
    let worst = errors.iter().copied().fold(f64::NAN, min_linq);
    let middle = percentile(&errors, 50.0);

    let numbers = format!(
        "median {}%, mean {} ± {}, worst {}%, {} sample{} more than {}% short, {} samples, peak {} {}",
        signed(middle),
        signed(mean),
        f(error, "0.0"),
        signed(worst),
        n0(spikes),
        plural(spikes),
        f(DANGEROUS.abs(), "0"),
        n0(errors.len()),
        n0f(highest),
        map.units()
    );
    let samples = count_i32(errors.len());

    if spikes >= 3 {
        let lead = if middle > 0.5 {
            format!(
                "On average this region runs {}% rich, which is exactly why the average is the wrong statistic here: a",
                f(middle, "0.0")
            )
        } else {
            "Averages hide this. A".to_string()
        };
        vec![LogInsight::new(
            InsightLevel::Warning,
            "Mixture under load",
            format!(
                "Lean under load on {} samples, the worst {}% short of the fuel asked for.",
                n0(spikes),
                f(worst.abs(), "0.0")
            ),
            // Said with the average beside it, because the two routinely disagree and a
            // warning that contradicts the mean without explaining itself gets waved away.
            format!(
                "{lead} single lean excursion at high load is what damages a piston, and it does not need to be common to do it. Look at the highest-load cells of the fuel table, and at whether the fuel system holds pressure there."
            ),
            numbers,
            samples,
        )]
    } else if middle.abs() > 3.0 * error && middle < -2.0 {
        vec![LogInsight::new(
            InsightLevel::Warning,
            "Mixture under load",
            format!(
                "Delivering {}% less fuel than asked for where the engine is loaded.",
                f(middle.abs(), "0.0")
            ),
            "Consistently lean under load, rather than a spike. This is the region where a fuelling error turns into heat rather than into a slow car.".into(),
            numbers,
            samples,
        )]
    } else if middle.abs() > 3.0 * error && middle > 2.0 {
        vec![LogInsight::new(
            InsightLevel::Note,
            "Mixture under load",
            format!(
                "Delivering {}% more fuel than asked for where the engine is loaded.",
                f(middle, "0.0")
            ),
            "Safe, and costing power and fuel. Worth leaning out once the rest of the tune is settled, in small steps with the wideband watched.".into(),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Mixture under load",
            "Fuelling under load holds target.".into(),
            "The part of the map that can hurt the engine is being fuelled as asked, spikes included.".into(),
            numbers,
            samples,
        )]
    }
}

/// LINQ `Min()` as a fold: NaN as the starting accumulator takes the first value.
fn min_linq(acc: f64, v: f64) -> f64 {
    if acc.is_nan() { v } else { acc.min(v) }
}

/// LINQ `Max()` as a fold.
fn max_linq(acc: f64, v: f64) -> f64 {
    if acc.is_nan() { v } else { acc.max(v) }
}

/// Whether the closed loop was doing anything, and what it had to do.
///
/// Two findings in one. A correction pinned at a hundred means the loop never engaged, which
/// people routinely fail to notice. And a correction consistently away from a hundred is the
/// table being wrong by that much, which is a measurement of the VE error rather than a
/// symptom.
fn closed_loop(e: &Engine) -> Vec<LogInsight> {
    let Some(trim) = e.correction else {
        return vec![];
    };

    let mut values: Vec<f64> = Vec::new();
    let mut moved = false;
    let mut first = f64::NAN;

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        let v = trim.at(i);
        if v.is_nan() {
            continue;
        }

        if first.is_nan() {
            first = v;
        } else if (v - first).abs() > 0.01 {
            moved = true;
        }

        values.push(v);
    }

    if values.len() < MINIMUM_SAMPLES {
        return vec![];
    }

    if !moved {
        return vec![LogInsight::new(
            InsightLevel::Note,
            "Closed loop",
            format!("The mixture correction never moved from {}%.", f(first, "0.#")),
            "The closed loop did not engage anywhere in this log, so every reading here is the fuel table on its own with nothing trimming it. That is the right condition for judging the table — and the wrong one for concluding the car drives well, since none of the correction that normally covers a table error was available.".into(),
            format!("{} running samples, all at {}%", n0(values.len()), f(first, "0.##")),
            count_i32(values.len()),
        )];
    }

    let (mean, error) = mean_and_standard_error(&values);
    let bias = mean - 100.0;

    let numbers = format!(
        "mean {}% ± {}, range {}–{}%, {} samples",
        f(mean, "0.0"),
        f(error, "0.0"),
        f(values.iter().copied().fold(f64::NAN, min_linq), "0.0"),
        f(values.iter().copied().fold(f64::NAN, max_linq), "0.0"),
        n0(values.len())
    );

    if bias.abs() > 3.0 * error && bias.abs() > 2.0 {
        vec![LogInsight::new(
            InsightLevel::Watch,
            "Closed loop",
            format!(
                "The closed loop is holding fuelling {}% {} on average.",
                f(bias.abs(), "0.0"),
                if bias > 0.0 { "up" } else { "down" }
            ),
            "A correction that sits to one side is the fuel table being wrong by that much: the controller is covering for it on every drive, and has less room left to cover for anything else. Moving the table by this amount is a measurement rather than a guess.".into(),
            numbers,
            count_i32(values.len()),
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Closed loop",
            "The closed loop is working and has little to do.".into(),
            format!(
                "Correction averages {}%, which means the table is close enough that the loop is trimming rather than propping it up.",
                f(mean, "0.0")
            ),
            numbers,
            count_i32(values.len()),
        )]
    }
}

/// How much injector there is left.
///
/// Duty cycle is the one number that says whether the fuel system can still answer. Past
/// about 85 per cent an injector stops being linear, and at a hundred it is simply open.
fn injector_headroom(e: &Engine) -> Vec<LogInsight> {
    let Some(duty) = e.duty else {
        return vec![];
    };

    let mut values: Vec<f64> = Vec::new();

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        let v = duty.at(i);
        if !v.is_nan() && v >= 0.0 {
            values.push(v);
        }
    }

    if values.len() < MINIMUM_SAMPLES {
        return vec![];
    }

    let peak = values.iter().copied().fold(f64::NAN, max_linq);
    let high = percentile(&values, 99.0);

    let numbers = format!(
        "peak {}%, 99th percentile {}%, median {}%, {} running samples",
        f(peak, "0.0"),
        f(high, "0.0"),
        f(percentile(&values, 50.0), "0.0"),
        n0(values.len())
    );
    let samples = count_i32(values.len());

    if peak >= 95.0 {
        vec![LogInsight::new(
            InsightLevel::Warning,
            "Injectors",
            format!("Injector duty reached {}%.", f(peak, "0.0")),
            "At this duty the injectors are effectively open and cannot deliver more fuel however lean the mixture goes. Anything asking for more — a hotter day, more boost, a longer pull — leans out with no warning from the fuel system. Larger injectors or more fuel pressure.".into(),
            numbers,
            samples,
        )]
    } else if peak >= 85.0 {
        vec![LogInsight::new(
            InsightLevel::Watch,
            "Injectors",
            format!(
                "Injector duty reached {}%, which is the top of the linear range.",
                f(peak, "0.0")
            ),
            "Above about 85% an injector's delivery stops being proportional to the pulse it is given, so the fuel table stops meaning what it says. There is room here, but not much.".into(),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Injectors",
            format!("Injector duty peaked at {}%.", f(peak, "0.0")),
            format!(
                "Comfortably inside the linear range, with about {} points spare.",
                f(100.0 - peak, "0")
            ),
            numbers,
            samples,
        )]
    }
}

/// Whether supply voltage moved enough to matter to the injectors.
///
/// An injector's dead time is a function of voltage, and steeply so below about twelve volts.
fn dead_time_and_voltage(e: &Engine) -> Vec<LogInsight> {
    let Some(battery) = e.battery else {
        return vec![];
    };

    let mut values: Vec<f64> = Vec::new();

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        let v = battery.at(i);
        if !v.is_nan() && v > 4.0 {
            values.push(v);
        }
    }

    if values.len() < MINIMUM_SAMPLES {
        return vec![];
    }

    let low = percentile(&values, 1.0);
    let high = percentile(&values, 99.0);
    let swing = high - low;

    let numbers = format!(
        "1st percentile {} V, 99th {} V, median {} V, {} running samples",
        f(low, "0.0"),
        f(high, "0.0"),
        f(percentile(&values, 50.0), "0.0"),
        n0(values.len())
    );
    let samples = count_i32(values.len());

    if low < 11.5 {
        vec![LogInsight::new(
            InsightLevel::Watch,
            "Charging and dead time",
            format!("Supply fell to {} V while running.", f(low, "0.0")),
            "Injector dead time climbs steeply below about twelve volts, so at this supply the dead-time table is deciding a large part of the delivered fuel. Low voltage also weakens the spark. Worth checking the alternator and the earths before chasing any mixture error.".into(),
            numbers,
            samples,
        )]
    } else if swing > 2.0 {
        vec![LogInsight::new(
            InsightLevel::Note,
            "Charging and dead time",
            format!("Supply swung {} V across the log.", f(swing, "0.0")),
            "A swing this wide means the dead-time table is doing real work. If mixture errors in this log track the voltage rather than the load, that table is where to look rather than the fuel table.".into(),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Charging and dead time",
            format!("Supply held between {} and {} V.", f(low, "0.0"), f(high, "0.0")),
            "Steady enough that injector dead time is near constant, so a mixture error in this log is the fuel table rather than the charging system.".into(),
            numbers,
            samples,
        )]
    }
}

/// Knock, which is the one finding that is worth interrupting for.
fn knock(e: &Engine) -> Vec<LogInsight> {
    let Some(knock) = e.knock else {
        return vec![];
    };

    let mut events = 0usize;
    let mut worst = 0.0f64;
    let mut running = 0usize;
    let mut worst_rpm = f64::NAN;
    let mut worst_map = f64::NAN;

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        running += 1;

        let retard = knock.at(i);
        if retard.is_nan() || retard <= 0.1 {
            continue;
        }

        events += 1;

        if retard > worst {
            worst = retard;
            worst_rpm = e.rpm.map_or(f64::NAN, |r| r.at(i));
            worst_map = e.map.map_or(f64::NAN, |m| m.at(i));
        }
    }

    if running < MINIMUM_SAMPLES {
        return vec![];
    }

    if events == 0 {
        return vec![LogInsight::new(
            InsightLevel::Good,
            "Knock",
            "No timing was pulled for knock anywhere in this log.".into(),
            "The knock detection reported nothing across every running sample. Worth noting that this says the controller heard nothing, not that nothing happened — a detection threshold set too high is silent for the same reason.".into(),
            format!("0 of {} running samples", n0(running)),
            count_i32(running),
        )];
    }

    let place = if worst_rpm.is_nan() {
        String::new()
    } else {
        let mut s = format!(", worst at {} rpm", n0f(worst_rpm));
        if !worst_map.is_nan() {
            s += &format!(" and {} {}", n0f(worst_map), e.map.map_or("", |m| m.units()));
        }
        s
    };

    vec![LogInsight::new(
        InsightLevel::Warning,
        "Knock",
        format!(
            "Timing was pulled for knock on {} samples, up to {}°.",
            n0(events),
            f(worst, "0.0")
        ),
        "Knock destroys pistons and ring lands, and the retard is the controller already having decided this was real. Find where in the map it happened before driving it again: usually timing, sometimes mixture, sometimes fuel quality.".into(),
        format!(
            "{} of {} running samples ({}%){place}",
            n0(events),
            n0(running),
            f(100.0 * events as f64 / running as f64, "0.0")
        ),
        count_i32(events),
    )]
}

/// Whether the engine reached temperature, and whether warmup enrichment let go when it did.
///
/// Enrichment that never returns to a hundred is fuel being added forever, which shows up as
/// a rich idle nobody can tune out of the table.
fn warmup(e: &Engine) -> Vec<LogInsight> {
    let Some(clt) = e.coolant else {
        return vec![];
    };

    let fahr = fahrenheit(clt);
    let warm = if fahr { 160.0 } else { 71.0 };
    let unit = if fahr { "°F" } else { "°C" };

    let mut highest = f64::MIN;
    let mut running = 0usize;

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        running += 1;
        let v = clt.at(i);
        if !v.is_nan() {
            highest = highest.max(v);
        }
    }

    if running < MINIMUM_SAMPLES || highest == f64::MIN {
        return vec![];
    }

    if highest < warm {
        return vec![LogInsight::new(
            InsightLevel::Note,
            "Warmup",
            format!(
                "The engine never reached operating temperature — it peaked at {} {unit}.",
                n0f(highest)
            ),
            format!(
                "Nothing measured here describes a warm engine. Fuelling below {} {unit} is warmup enrichment on top of the table, so any mixture conclusion from this log is about the warmup curve rather than the fuel table.",
                n0f(warm)
            ),
            format!("peak {} {unit} over {} running samples", n0f(highest), n0(running)),
            count_i32(running),
        )];
    }

    let Some(enrichment) = e.warmup else {
        return vec![];
    };

    let mut last_warm = f64::NAN;

    for i in 0..e.count {
        if !e.running(i) || clt.at(i).is_nan() || clt.at(i) < warm {
            continue;
        }

        let v = enrichment.at(i);
        if !v.is_nan() {
            last_warm = v;
        }
    }

    if last_warm.is_nan() {
        return vec![];
    }

    if last_warm > 101.0 {
        vec![LogInsight::new(
            InsightLevel::Watch,
            "Warmup",
            format!(
                "Warmup enrichment is still adding {}% at operating temperature.",
                f(last_warm - 100.0, "0.#")
            ),
            "The warmup curve should reach 100% by the time the engine is hot, or it adds fuel for ever and no amount of fuel-table work will lean out a warm idle. The last point of the curve is the one to look at.".into(),
            format!("{}% while above {} {unit}", f(last_warm, "0.#"), n0f(warm)),
            count_i32(running),
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Warmup",
            "Warmup enrichment let go once the engine was hot.".into(),
            format!(
                "It reached {}% at operating temperature, so warm fuelling is the table rather than the warmup curve.",
                f(last_warm, "0.#")
            ),
            format!(
                "{}% while above {} {unit}, peak {} {unit}",
                f(last_warm, "0.#"),
                n0f(warm),
                n0f(highest)
            ),
            count_i32(running),
        )]
    }
}

/// Whether the manifold ever pulled vacuum.
///
/// A MAP sensor that never reads below ambient is not plumbed to the manifold, and every load
/// number in the log is then wrong in a way that looks entirely plausible.
fn manifold_against_ambient(e: &Engine) -> Vec<LogInsight> {
    let Some(map) = e.map else {
        return vec![];
    };

    let (ambient, measured) = atmosphere(e, map);
    if ambient <= 0.0 {
        return vec![];
    }

    let mut lowest = f64::MAX;
    let mut highest = f64::MIN;
    let mut running = 0usize;

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        let v = map.at(i);
        if v.is_nan() {
            continue;
        }

        running += 1;
        lowest = lowest.min(v);
        highest = highest.max(v);
    }

    if running < MINIMUM_SAMPLES {
        return vec![];
    }

    let units = map.units();
    let numbers = format!(
        "{}–{} {units} against ambient {}, {} running samples",
        n0f(lowest),
        n0f(highest),
        n0f(ambient),
        n0(running)
    );
    let samples = count_i32(running);

    if lowest > ambient * 0.92 {
        vec![LogInsight::new(
            InsightLevel::Warning,
            "Manifold pressure",
            format!(
                "Manifold pressure never fell below {} {units}, with ambient at {}.",
                n0f(lowest),
                n0f(ambient)
            ),
            "A running engine pulls vacuum at idle and on the overrun. A sensor that never sees any is either not connected to the manifold or has failed — and on speed density every fuelling and timing number in this log is read from it, so they are all wrong together and all look reasonable.".into(),
            numbers,
            samples,
        )]
    } else if highest > ambient * 1.05 {
        vec![LogInsight::new(
            InsightLevel::Note,
            "Manifold pressure",
            format!(
                "The engine saw boost, peaking {} {units} above ambient.",
                n0f(highest - ambient)
            ),
            "Load above atmospheric is where fuelling and timing errors stop being academic. The mixture-under-load finding above covers this region.".into(),
            numbers,
            samples,
        )]
    } else if !measured {
        // Ambient was taken from the highest reading here, so "the top of the range is
        // ambient" is not an observation, it is the assumption restated. On a boosted engine
        // it is also wrong, and this is exactly the log where it would be believed.
        vec![LogInsight::new(
            InsightLevel::Unanswered,
            "Manifold pressure",
            format!(
                "Manifold pressure spans {} to {} {units}, and this log cannot say whether any of that is boost.",
                n0f(lowest),
                n0f(highest)
            ),
            "Nothing here records atmospheric pressure: there is no barometer channel, and the engine is never stopped with the key on, which is the other way to read one. Without it the highest reading and ambient are indistinguishable — a turbo at 250 kPa and a naturally aspirated engine at sea level look identical. Log the controller's barometer, or start the next log before cranking.".into(),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Manifold pressure",
            format!("Manifold pressure spans {} to {} {units}.", n0f(lowest), n0f(highest)),
            "Vacuum at the bottom and near ambient at the top, which is what a healthy naturally aspirated engine reads.".into(),
            numbers,
            samples,
        )]
    }
}

/// What the boost channel is measured against.
///
/// A gauge pressure is a difference from something, and firmwares disagree about what. It
/// does not have to be assumed: regressing the derived channel against the raw one gives the
/// slope, which says the units, and the intercept, which says the reference.
fn pressure_reference(e: &Engine) -> Vec<LogInsight> {
    let (Some(map), Some(boost)) = (e.map, e.boost) else {
        return vec![];
    };

    let mut xs: Vec<f64> = Vec::new();
    let mut ys: Vec<f64> = Vec::new();

    for i in 0..e.count {
        let (m, b) = (map.at(i), boost.at(i));
        if !m.is_nan() && !b.is_nan() {
            xs.push(m);
            ys.push(b);
        }
    }

    if xs.len() < MINIMUM_SAMPLES {
        return vec![];
    }

    let n = xs.len() as f64;
    let sx = sum(xs.iter().copied());
    let sy = sum(ys.iter().copied());
    let sxx = sum(xs.iter().map(|v| v * v));
    let sxy = sum(xs.iter().zip(&ys).map(|(a, b)| a * b));

    let denominator = (n * sxx) - (sx * sx);
    if denominator.abs() < 1e-9 {
        return vec![];
    }

    let slope = ((n * sxy) - (sx * sy)) / denominator;
    if slope.abs() < 1e-9 {
        return vec![];
    }

    let intercept = (sy - (slope * sx)) / n;
    let reference = -intercept / slope;

    // Plenty of firmware declares no unit for this channel at all, and a sentence with a hole
    // where the unit should be reads worse than one written without it.
    let unit = if boost.units().is_empty() {
        String::new()
    } else {
        format!(" {}", boost.units())
    };

    let ambient = atmosphere(e, map).0;
    let against_baro = ambient > 0.0 && (reference - ambient).abs() < 2.0;
    let against_sea_level = (reference - 101.3).abs() < 2.0;
    let map_units = map.units();

    let numbers = format!(
        "slope {}{unit} per {map_units}, zero at {} {map_units}, barometer reads {}, {} samples",
        f(slope, "0.0000"),
        f(reference, "N1"),
        f(ambient, "N1"),
        n0(xs.len())
    );
    let samples = count_i32(xs.len());

    if against_baro && ambient > 0.0 && (ambient - 101.3).abs() > 3.0 {
        vec![LogInsight::new(
            InsightLevel::Note,
            "Pressure reference",
            format!(
                "\"{}\" is measured against the barometer, not against sea level.",
                boost.name()
            ),
            format!(
                "Its zero sits at {} {map_units}, which is this log's own ambient rather than 101.3. Anything read from it assuming sea level is out by {}{unit} throughout — a difference that is invisible in the trace and survives every average.",
                f(reference, "N1"),
                f(((101.3 - reference) * slope).abs(), "0.0")
            ),
            numbers,
            samples,
        )]
    } else if against_sea_level && ambient > 0.0 && (ambient - 101.3).abs() > 3.0 {
        vec![LogInsight::new(
            InsightLevel::Note,
            "Pressure reference",
            format!(
                "\"{}\" is measured against sea level, not against the barometer.",
                boost.name()
            ),
            format!(
                "Its zero sits at 101.3 {map_units} while the barometer reads {}, so at this altitude it reads negative with the throttle wide open. Correct, and worth knowing before comparing it with anything from another day or another place.",
                f(ambient, "N1")
            ),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Pressure reference",
            format!(
                "\"{}\" is a plain gauge pressure with its zero at {} {map_units}.",
                boost.name(),
                f(reference, "N1")
            ),
            "Checked rather than assumed: the reference is read off the log by regressing the derived channel against the raw one, so nothing here rests on a guess about which convention this firmware uses.".into(),
            numbers,
            samples,
        )]
    }
}

/// How steady the idle is, as a standard deviation rather than an impression.
///
/// Hunting is the commonest thing a tuner is asked about and the hardest to judge from a
/// trace, because a plot's vertical scale flatters or damns it depending on nothing at all.
fn idle_steadiness(e: &Engine) -> Vec<LogInsight> {
    let Some(rpm) = e.rpm else {
        return vec![];
    };

    let mut idle: Vec<f64> = Vec::new();

    // How long the engine has been below the idle ceiling without leaving it, in samples.
    let mut settled = 0i32;

    for i in 0..e.count {
        let v = rpm.at(i);

        if !e.running(i) || v.is_nan() || v >= 1500.0 || e.throttle.is_some_and(|tps| tps.at(i) > 3.0) {
            settled = 0;
            continue;
        }

        // Below 1,500 with the throttle shut is idle *or* the tail of an overrun, and the two
        // are nothing alike. Coming down from 4,000 the engine sweeps through this band in a
        // second or two, and every one of those samples used to count as idle, so on any road
        // log the spread was dominated by decelerations and a perfectly steady idle was
        // reported as hunting by a couple of hundred rpm. It is now handled by waiting for
        // the engine to have been down here a while, rather than by discarding falling
        // samples, which would throw away half of a genuine hunt and hide the thing being
        // looked for.
        settled += 1;

        if f64::from(settled) * e.spacing().max(0.001) < 2.0 {
            continue;
        }

        idle.push(v);
    }

    if idle.len() < MINIMUM_SAMPLES * 2 {
        return vec![unanswered(
            "Idle",
            "Not enough closed-throttle running to judge the idle.".into(),
            format!(
                "{} samples had the throttle shut below 1,500 rpm. Half a minute of idling makes this measurable.",
                idle.len()
            ),
            count_i32(idle.len()),
        )];
    }

    let mean = average(&idle);
    let sd = (sum(idle.iter().map(|v| (v - mean) * (v - mean))) / idle.len() as f64).sqrt();

    let numbers = format!(
        "{} rpm ± {} (standard deviation), {}–{}, {} closed-throttle samples",
        n0f(mean),
        n0f(sd),
        n0f(idle.iter().copied().fold(f64::NAN, min_linq)),
        n0f(idle.iter().copied().fold(f64::NAN, max_linq)),
        n0(idle.len())
    );
    let samples = count_i32(idle.len());

    if sd > 100.0 {
        vec![LogInsight::new(
            InsightLevel::Watch,
            "Idle",
            format!("The idle is hunting: {} rpm, wandering ±{}.", n0f(mean), n0f(sd)),
            "A standard deviation this wide is a visible, audible hunt rather than ordinary variation. Usual causes are idle control fighting the throttle stop, a vacuum leak, or idle timing correction switched off so nothing damps a stumble.".into(),
            numbers,
            samples,
        )]
    } else if sd > 50.0 {
        vec![LogInsight::new(
            InsightLevel::Note,
            "Idle",
            format!("The idle wanders a little: {} rpm ± {}.", n0f(mean), n0f(sd)),
            "Noticeable but not objectionable. Worth revisiting once the mixture at idle is settled, since fuelling and idle stability chase each other.".into(),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Idle",
            format!("The idle is steady at {} rpm.", n0f(mean)),
            format!(
                "It holds to within ±{} rpm, which is as steady as a carburetted engine is expected to be and better than most.",
                n0f(sd)
            ),
            numbers,
            samples,
        )]
    }
}

/// Channels that never moved.
///
/// A sensor reading a constant is either not fitted or has failed, and a flat trace is easy
/// to miss among forty others. Only channels that ought to move are reported.
fn stuck_channels(e: &Engine) -> Vec<LogInsight> {
    let should_move: [(&str, Option<&LogChannel>); 6] = [
        ("engine speed", e.rpm),
        ("manifold pressure", e.map),
        ("throttle", e.throttle),
        ("coolant temperature", e.coolant),
        ("the wideband", e.afr),
        ("battery voltage", e.battery),
    ];

    let mut stuck: Vec<String> = Vec::new();

    for (what, channel) in should_move {
        let Some(channel) = channel else {
            continue;
        };

        let mut first = f64::NAN;
        let mut moved = false;
        let mut seen = 0usize;

        for i in 0..e.count {
            let v = channel.at(i);
            if v.is_nan() {
                continue;
            }

            seen += 1;
            if first.is_nan() {
                first = v;
            } else if (v - first).abs() > 1e-9 {
                moved = true;
                break;
            }
        }

        if seen >= MINIMUM_SAMPLES && !moved {
            stuck.push(
                format!(
                    "{what} (\"{}\") at {} {}",
                    channel.name(),
                    f(first, "0.##"),
                    channel.units()
                )
                .trim_end()
                .to_string(),
            );
        }
    }

    if stuck.is_empty() {
        return vec![];
    }

    vec![LogInsight::new(
        InsightLevel::Warning,
        "Sensors",
        format!("{} channel{} never changed value.", stuck.len(), plural(stuck.len())),
        format!(
            "A reading that never moves across a whole log is a sensor that is not connected, not powered, or has failed — and anything the controller worked out from it is wrong without looking wrong. {}.",
            stuck.join("; ")
        ),
        stuck.join("; "),
        count_i32(e.count),
    )]
}

/// Whether the log was recorded evenly.
///
/// Gaps matter beyond tidiness: a rate that collapses during the interesting part is a log
/// that did not record the interesting part, and every average above is then weighted towards
/// whatever the link kept up with.
fn sample_rate(e: &Engine) -> Vec<LogInsight> {
    let gaps = time_gaps(e);

    if gaps.len() < MINIMUM_SAMPLES {
        return vec![];
    }

    let median = percentile(&gaps, 50.0);
    let worst = gaps.iter().copied().fold(f64::NAN, max_linq);
    let rate = if median > 0.0 { 1.0 / median } else { 0.0 };

    // A gap of five times the usual spacing is a stall rather than jitter.
    let stalls = gaps.iter().filter(|&&g| g > median * 5.0).count();

    let numbers = format!(
        "{} Hz typical, longest gap {} s, {} gap{} over five times the usual spacing, {} samples",
        f(rate, "0.#"),
        f(worst, "0.###"),
        n0(stalls),
        plural(stalls),
        n0(gaps.len() + 1)
    );
    let samples = count_i32(gaps.len() + 1);

    if stalls > 0 && worst > 1.0 {
        vec![LogInsight::new(
            InsightLevel::Watch,
            "Recording",
            format!(
                "The log stalled {} time{}, the longest for {} seconds.",
                n0(stalls),
                plural(stalls),
                f(worst, "0.#")
            ),
            "Whatever the engine did during those gaps was not recorded, so every average here describes the parts the link kept up with. If the gaps line up with hard acceleration, the most interesting samples are the missing ones.".into(),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Recording",
            format!("Recorded evenly at about {} Hz.", f(rate, "0.#")),
            "No meaningful gaps, so the averages here are weighted the way the driving was.".into(),
            numbers,
            samples,
        )]
    }
}

/// How far behind the wideband is.
///
/// A wideband reads the exhaust some distance and some milliseconds after the event that
/// caused it. Tuning a table cell against a reading that belongs to a different cell is the
/// commonest way to chase a fault that is not there, and the delay is measurable.
fn mixture_delay(e: &Engine) -> Vec<LogInsight> {
    let (Some(afr), Some(target)) = (e.afr, e.target) else {
        return vec![];
    };
    let (Some(rpm), Some(map)) = (e.rpm, e.map) else {
        return vec![];
    };
    if e.count < 200 {
        return vec![];
    }

    let last = e.count as isize - 1;

    // A grid to bin against, built from the log's own spread rather than from a tune: the
    // delay is a property of the exhaust and the sensor, and asking for it should not require
    // the controller's table.
    let Ok(grid) = ve_analysis::grid_from(rpm, map, 8, 8, 0, last, None) else {
        return vec![];
    };

    let interval = sample_interval(e);
    if interval <= 0.0 {
        return vec![];
    }

    let found = wideband_delay::find(&grid, rpm, map, afr, target, 0, last, interval, None, MAXIMUM_SECONDS);

    if found.has_problem() || found.none_is_plausible() {
        return vec![unanswered(
            "Wideband delay",
            "This log cannot say how far behind the wideband reads.".into(),
            match &found.problem {
                Some(why) if !why.is_empty() => why.clone(),
                _ => "No delay fitted better than none did, which usually means the engine held too steady for the measurement to have anything to work on.".to_string(),
            },
            count_i32(found.samples_scored),
        )];
    }

    let numbers = format!(
        "{} s, credible between {} and {} s, over {} scored samples",
        f(found.best_seconds, "0.00"),
        f(found.low_seconds, "0.00"),
        f(found.high_seconds, "0.00"),
        n0(found.samples_scored)
    );
    let samples = count_i32(found.samples_scored);

    if found.best_seconds >= 0.15 {
        vec![LogInsight::new(
            InsightLevel::Note,
            "Wideband delay",
            format!("The wideband reads about {} s behind the engine.", f(found.best_seconds, "0.00")),
            "Worth aligning before reading anything cell by cell: at 3,000 rpm this is dozens of engine cycles, so a mixture reading can belong to a different part of the map than the one it appears against. VE Calibration can take this delay into account.".into(),
            numbers,
            samples,
        )]
    } else {
        vec![LogInsight::new(
            InsightLevel::Good,
            "Wideband delay",
            format!(
                "The wideband is nearly in step, about {} s behind.",
                f(found.best_seconds, "0.00")
            ),
            "Small enough that readings line up with the cells that caused them.".into(),
            numbers,
            samples,
        )]
    }
}

/// The positive, non-NaN gaps between consecutive time samples.
fn time_gaps(e: &Engine) -> Vec<f64> {
    let mut gaps = Vec::new();

    for i in 1..e.count {
        let dt = e.time(i) - e.time(i - 1);
        if !dt.is_nan() && dt > 0.0 {
            gaps.push(dt);
        }
    }

    gaps
}

/// The usual spacing between samples, in seconds.
fn sample_interval(e: &Engine) -> f64 {
    let gaps = time_gaps(e);

    if gaps.is_empty() { 0.0 } else { percentile(&gaps, 50.0) }
}

/// How much of the operating range the log actually visited.
///
/// Every conclusion above is drawn from where the engine was, and a log that only idled
/// cannot say anything about anything else. Said plainly, so a clean bill of health is not
/// read as more than it is.
fn coverage(e: &Engine) -> Vec<LogInsight> {
    let (Some(rpm), Some(map)) = (e.rpm, e.map) else {
        return vec![];
    };

    let mut running = 0usize;
    let mut seen: HashSet<(i32, i32)> = HashSet::new();
    let mut top_rpm = 0.0f64;

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        let (r, m) = (rpm.at(i), map.at(i));
        if r.is_nan() || m.is_nan() {
            continue;
        }

        running += 1;
        top_rpm = top_rpm.max(r);

        // A coarse grid: five hundred rpm by twenty units of load, which is about the
        // resolution a tuner thinks in.
        seen.insert(((r / 500.0) as i32, (m / 20.0) as i32));
    }

    if running < MINIMUM_SAMPLES {
        return vec![];
    }

    let minutes = (e.time(e.count - 1) - e.time(0)) / 60.0;
    let few = seen.len() < 6;

    vec![LogInsight::new(
        if few { InsightLevel::Note } else { InsightLevel::Good },
        "Coverage",
        if few {
            format!("This log visited only {} regions of the map.", seen.len())
        } else {
            format!("This log covered {} regions of the map.", seen.len())
        },
        if few {
            "Everything above is drawn from a narrow slice of the engine's range. A clean result here does not say the rest of the map is clean — it says the rest was never visited."
        } else {
            "Enough spread that the findings above rest on more than one corner of the map."
        }
        .into(),
        format!(
            "{} distinct 500 rpm × 20 {} regions, up to {} rpm, {} running samples over {} minutes",
            seen.len(),
            map.units(),
            n0f(top_rpm),
            n0(running),
            f(minutes, "0.#")
        ),
        count_i32(running),
    )]
}

// ----- the arithmetic ------------------------------------------------------------------

/// A word about the filter itself, where it kept an implausible share.
///
/// A steady-state stack on a varied drive keeps roughly two frames in five. Keeping almost
/// none means it is rejecting the drive rather than the transients; keeping almost all means
/// it is not filtering at all.
fn survival(kept: f64) -> String {
    if kept < 5.0 {
        " Only a twentieth of the running samples survived the steadiness test, so this rests on a narrow slice of the drive."
    } else if kept > 90.0 {
        " Almost every running sample survived the steadiness test, which suggests the engine was held at one point throughout rather than driven."
    } else {
        ""
    }
    .to_string()
}

fn unanswered(topic: &str, title: String, detail: String, samples: i32) -> LogInsight {
    LogInsight::new(InsightLevel::Unanswered, topic, title, detail, String::new(), samples)
}

/// LINQ `Sum`: a plain left-to-right sum, starting from zero.
fn sum(values: impl Iterator<Item = f64>) -> f64 {
    let mut total = 0.0;
    for v in values {
        total += v;
    }
    total
}

/// LINQ `Average` over a non-empty list.
fn average(values: &[f64]) -> f64 {
    sum(values.iter().copied()) / values.len() as f64
}

/// The mean, and how well the mean is known.
///
/// The standard error is the whole point: it is what turns "the average is 0.3 lean" into
/// either "and that is twenty times the uncertainty" or "and the uncertainty is 0.4, so this
/// says nothing".
fn mean_and_standard_error(values: &[f64]) -> (f64, f64) {
    if values.is_empty() {
        return (f64::NAN, f64::NAN);
    }
    if values.len() == 1 {
        return (values[0], f64::INFINITY);
    }

    let mean = average(values);
    let variance = sum(values.iter().map(|v| (v - mean) * (v - mean))) / (values.len() - 1) as f64;

    (mean, (variance / values.len() as f64).sqrt())
}

/// A percentile by linear interpolation between the two nearest samples.
///
/// Used wherever one bad sample decides the answer: the lean excursion that hurts an engine is
/// in the tail, and a mean is exactly the statistic that hides a tail.
fn percentile(values: &[f64], percentile: f64) -> f64 {
    if values.is_empty() {
        return f64::NAN;
    }

    let mut sorted = values.to_vec();
    // `Order()` puts NaN first.
    sorted.sort_by(|a, b| match (a.is_nan(), b.is_nan()) {
        (true, true) => Ordering::Equal,
        (true, false) => Ordering::Less,
        (false, true) => Ordering::Greater,
        _ => a.partial_cmp(b).unwrap_or(Ordering::Equal),
    });
    if sorted.len() == 1 {
        return sorted[0];
    }

    let at = percentile.clamp(0.0, 100.0) / 100.0 * (sorted.len() - 1) as f64;
    let below = at.floor() as usize;
    let above = (below + 1).min(sorted.len() - 1);

    sorted[below] + ((sorted[above] - sorted[below]) * (at - below as f64))
}

/// Where "loaded" begins when atmospheric is not known: the upper third of the manifold
/// pressure this log actually saw while running.
///
/// Relative rather than absolute, because without a real ambient figure there is no absolute
/// to be had, and a threshold taken from the log's own spread still separates the pulls from
/// the cruising, which is all this finding needs it to do.
fn loaded_from(e: &Engine, map: &LogChannel) -> f64 {
    let mut lowest = f64::MAX;
    let mut highest = f64::MIN;

    for i in 0..e.count {
        if !e.running(i) {
            continue;
        }

        let v = map.at(i);
        if v.is_nan() {
            continue;
        }

        lowest = lowest.min(v);
        highest = highest.max(v);
    }

    if lowest > highest {
        return f64::MAX;
    }

    lowest + ((highest - lowest) * 2.0 / 3.0)
}

/// Atmospheric pressure, and whether it was measured or merely assumed.
///
/// **The assumption is the dangerous half.** Falling back to the highest manifold pressure is
/// sound on an engine that cannot make boost, and nonsense on one that can, where the highest
/// reading *is* the boost. So it is marked as an assumption, and whatever cannot be said
/// honestly without a real figure declines to say it.
fn atmosphere(e: &Engine, map: &LogChannel) -> (f64, bool) {
    // A barometer, in whatever it was logged in.
    if let Some(baro) = e.baro {
        // Converted into the manifold's units rather than left in its own. Every use of this
        // compares it against manifold pressure, and a barometer logged in psi beside a
        // manifold logged in kPa reads as 14.7 against 35.
        let per_unit = kilopascals_per(baro.units());
        let into_map = per_unit / kilopascals_per(map.units());

        for i in 0..e.count {
            let v = baro.at(i);
            if v.is_nan() {
                continue;
            }

            // A plausible atmosphere anywhere a car is driven: the Dead Sea reads about 106
            // kPa and the highest road in the world about 45.
            let kpa = v * per_unit;
            if kpa > 40.0 && kpa < 115.0 {
                return (v * into_map, true);
            }
        }
    }

    // Failing that, the manifold with the engine stopped. A stationary engine's manifold is
    // open to the atmosphere through the throttle, so this is a reading rather than an
    // inference, and most logs start with the key on before anything is cranked.
    if let Some(rpm) = e.rpm {
        let mut stopped = f64::NAN;

        for i in 0..e.count {
            let turning = rpm.at(i);
            if turning.is_nan() || turning > 50.0 {
                continue;
            }

            let v = map.at(i);
            if !v.is_nan() && (stopped.is_nan() || v > stopped) {
                stopped = v;
            }
        }

        if !stopped.is_nan() && stopped > 0.0 {
            return (stopped, true);
        }
    }

    let mut highest = 0.0f64;

    for i in 0..e.count {
        let v = map.at(i);
        if !v.is_nan() {
            highest = highest.max(v);
        }
    }

    (highest, false)
}

/// Kilopascals per unit of whatever a pressure was logged in.
fn kilopascals_per(units: &str) -> f64 {
    match channel_roles::simplify(units).as_str() {
        "psi" | "psig" | "psia" => KPA_PER_PSI,
        "bar" => 100.0,
        "mbar" | "hpa" => 0.1,
        "inhg" => 3.386389,
        _ => 1.0,
    }
}

/// What a log is told to say about the engine that produced it.
///
/// The tests that matter are the ones about restraint: a finding made from nine samples, or a
/// warning raised on noise, is worse than no finding at all, because a tuner who is sent
/// chasing something that is not there stops reading these.
#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    fn channel(name: &str, units: &str, values: &[f64]) -> LogChannel {
        LogChannel::new(name, units, 2, values, false)
    }

    fn document(path: &str, time: LogChannel, channels: Vec<LogChannel>) -> LogDocument {
        LogDocument::new(
            path,
            "test",
            channels.into_iter().map(Arc::new).collect(),
            Arc::new(time),
        )
    }

    fn ticks(n: usize) -> Vec<f64> {
        (0..n).map(|i| i as f64 * 0.1).collect()
    }

    fn repeat(value: f64, n: usize) -> Vec<f64> {
        vec![value; n]
    }

    /// Optional pieces of the steady fixture.
    struct Steady {
        target: Option<Vec<f64>>,
        map: Option<Vec<f64>>,
        rpm: Option<Vec<f64>>,
        clt: f64,
        extra: Option<(Vec<f64>, &'static str, &'static str)>,
    }

    impl Default for Steady {
        fn default() -> Self {
            Self {
                target: None,
                map: None,
                rpm: None,
                clt: 190.0,
                extra: None,
            }
        }
    }

    /// A log of steady running: the given AFR against a 14.7 target, at 3,000 rpm and 90 kPa,
    /// warm, throttle still, at 10 Hz.
    fn steady(afr: Vec<f64>, o: Steady) -> LogDocument {
        let n = afr.len();
        let mut channels = vec![
            channel("RPM", "RPM", &o.rpm.unwrap_or_else(|| repeat(3000.0, n))),
            channel("MAP", "kPa", &o.map.unwrap_or_else(|| repeat(90.0, n))),
            channel("AFR", "AFR", &afr),
            channel("AFR Target 1", "AFR", &o.target.unwrap_or_else(|| repeat(14.7, n))),
            channel("TPS", "%", &repeat(30.0, n)),
            channel("CLT", "°F", &repeat(o.clt, n)),
            channel("Barometer", "kPa", &repeat(101.0, n)),
        ];
        if let Some((values, name, units)) = &o.extra {
            channels.push(channel(name, units, values));
        }
        document("test", channel("Time", "s", &ticks(n)), channels)
    }

    fn with_extra(afr: Vec<f64>, values: Vec<f64>, name: &'static str, units: &'static str) -> LogDocument {
        steady(
            afr,
            Steady {
                extra: Some((values, name, units)),
                ..Steady::default()
            },
        )
    }

    fn topic(log: &LogDocument, topic: &str) -> Option<LogInsight> {
        from(log).into_iter().find(|i| i.topic.eq_ignore_ascii_case(topic))
    }

    fn single(found: Vec<LogInsight>, topic: &str) -> LogInsight {
        let mut matching: Vec<_> = found.into_iter().filter(|f| f.topic == topic).collect();
        assert_eq!(matching.len(), 1, "expected exactly one {topic} finding");
        matching.remove(0)
    }

    // ----- restraint ---------------------------------------------------------

    #[test]
    fn a_handful_of_samples_is_not_evidence() {
        // Nine samples badly lean would be a striking finding and a false one.
        let mixture = topic(&steady(repeat(17.0, 9), Steady::default()), "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Unanswered);
        assert!(mixture.title.contains("Not enough"));
    }

    #[test]
    fn noise_around_the_target_is_not_a_finding() {
        // Alternating either side of target: the mean is nought and the scatter is real. A
        // tool that called this an error would call everything one.
        let afr = (0..200).map(|i| 14.7 + if i % 2 == 0 { 0.6 } else { -0.6 }).collect();

        let mixture = topic(&steady(afr, Steady::default()), "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Good);
        assert!(mixture.title.contains("on target"));
    }

    #[test]
    fn a_small_but_real_error_is_not_worth_chasing() {
        // Dead steady at a tenth lean: real beyond doubt, and below the threshold where a
        // tuner should spend an afternoon on it.
        let mixture = topic(&steady(repeat(14.8, 200), Steady::default()), "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Good);
    }

    #[test]
    fn a_real_error_is_reported_with_its_uncertainty() {
        let mixture = topic(&steady(repeat(15.7, 200), Steady::default()), "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Watch);

        // Reported as a share of the fuel required rather than as a difference between two AFR
        // numbers, so cells with different targets can be counted together.
        assert!(mixture.title.contains("less fuel than asked for"));
        assert!(mixture.evidence.contains("standard error"));
    }

    #[test]
    fn rich_is_said_to_be_rich_rather_than_just_wrong() {
        let mixture = topic(&steady(repeat(13.2, 200), Steady::default()), "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Watch);
        assert!(mixture.title.contains("more fuel than asked for"));
    }

    // ----- the map, against what the closed loop rescued ---------------------

    #[test]
    fn the_closed_loops_correction_is_folded_back_into_the_maps_error() {
        // The case that matters: the mixture is on target because the loop is adding fuel, and
        // the map underneath is badly lean. Read without folding the correction back in, this
        // looks perfect, and then misbehaves the moment the loop drops out.
        //
        // Verified against a real MS3 log, where the mixture read 9% lean while the loop added
        // 8%, and the map was out by nineteen.
        let log = with_extra(repeat(14.7, 200), repeat(120.0, 200), "EGO cor1", "%");

        let mixture = topic(&log, "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Watch);
        assert!(mixture.title.contains("less fuel than asked for"));

        // A loop adding a fifth is covering a map that is a sixth short: 100/120 is 0.833, so
        // 16.7% less than required.
        assert!(mixture.title.contains("16.7%"));
        assert!(mixture.evidence.contains("divided back out"));
    }

    #[test]
    fn a_mixture_on_target_with_no_correction_is_a_map_on_target() {
        let log = with_extra(repeat(14.7, 200), repeat(100.0, 200), "EGO cor1", "%");

        assert_eq!(topic(&log, "Mixture").unwrap().level, InsightLevel::Good);
    }

    #[test]
    fn a_rich_mixture_the_loop_is_pulling_back_is_still_a_rich_map() {
        // Measured on target, loop removing a tenth: the map is a ninth rich.
        let log = with_extra(repeat(14.7, 200), repeat(90.0, 200), "EGO cor1", "%");

        let mixture = topic(&log, "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Watch);
        assert!(mixture.title.contains("more fuel than asked for"));
        assert!(mixture.title.contains("11.1%"));
    }

    // ----- what the boost channel is measured against ------------------------

    #[test]
    fn the_boost_reference_is_read_off_the_log_rather_than_assumed() {
        // A gauge pressure is a difference from something and firmwares disagree about what. At
        // altitude the two conventions differ by two and a half psi, silently, in everything
        // derived from either.
        let n = 200;
        let map: Vec<f64> = (0..n).map(|i| 30.0 + (i % 100) as f64).collect();
        let boost: Vec<f64> = map.iter().map(|m| (m - 84.0) * 0.145).collect();

        let log = document(
            "altitude.mlg",
            channel("Time", "s", &ticks(n)),
            vec![
                channel("RPM", "RPM", &repeat(3000.0, n)),
                channel("MAP", "kPa", &map),
                channel("Boost psi", "psi", &boost),
                channel("Barometer", "kPa", &repeat(84.0, n)),
            ],
        );

        let reference = topic(&log, "Pressure reference").unwrap();

        assert_eq!(reference.level, InsightLevel::Note);
        assert!(reference.title.contains("against the barometer"));
        // The psi anyone assuming sea level is out by.
        assert!(reference.detail.contains("2.5"));
    }

    // ----- where it can hurt -------------------------------------------------

    #[test]
    fn a_lean_spike_under_load_is_a_warning_even_when_the_average_is_rich() {
        // The case the percentile exists for: mostly rich, with one excursion in twenty that is
        // what actually damages a piston.
        let afr = (0..200).map(|i| if i % 20 == 0 { 16.5 } else { 13.5 }).collect();
        let log = steady(
            afr,
            Steady {
                map: Some(repeat(150.0, 200)),
                ..Steady::default()
            },
        );

        let load = topic(&log, "Mixture under load").unwrap();

        assert_eq!(load.level, InsightLevel::Warning);
        assert!(load.title.contains("Lean under load"));
        assert!(load.title.contains("10 samples"));
        // Says so, rather than contradicting itself.
        assert!(load.detail.contains("rich"));
    }

    #[test]
    fn holding_target_under_load_is_said_to_be_good() {
        let log = steady(
            repeat(12.5, 200),
            Steady {
                target: Some(repeat(12.5, 200)),
                map: Some(repeat(150.0, 200)),
                ..Steady::default()
            },
        );

        assert_eq!(topic(&log, "Mixture under load").unwrap().level, InsightLevel::Good);
    }

    #[test]
    fn a_log_that_never_loads_the_engine_says_so_rather_than_passing_it() {
        // The important distinction: "nothing wrong under load" and "never went under load"
        // must not read the same.
        let log = steady(
            repeat(14.7, 200),
            Steady {
                map: Some(repeat(35.0, 200)),
                ..Steady::default()
            },
        );

        let load = topic(&log, "Mixture under load").unwrap();

        assert_eq!(load.level, InsightLevel::Note);
        assert!(load.title.contains("Not enough high-load"));
    }

    // ----- the things that are simply facts ----------------------------------

    #[test]
    fn an_injector_at_full_duty_is_a_warning() {
        let log = with_extra(repeat(14.7, 200), repeat(97.0, 200), "DutyCycle1", "%");

        let injectors = topic(&log, "Injectors").unwrap();

        assert_eq!(injectors.level, InsightLevel::Warning);
        assert!(injectors.title.contains("97"));
    }

    #[test]
    fn an_injector_with_room_to_spare_is_said_to_be_good() {
        let log = with_extra(repeat(14.7, 200), repeat(55.0, 200), "DutyCycle1", "%");

        assert_eq!(topic(&log, "Injectors").unwrap().level, InsightLevel::Good);
    }

    #[test]
    fn knock_is_a_warning_and_silence_is_not_quite_all_clear() {
        let quiet = with_extra(repeat(14.7, 200), repeat(0.0, 200), "SPK: Knock retard", "deg");

        let none = topic(&quiet, "Knock").unwrap();
        assert_eq!(none.level, InsightLevel::Good);

        // Said carefully: the controller heard nothing, which is not the same as nothing having
        // happened.
        assert!(none.detail.contains("heard nothing"));

        let retard = (0..200).map(|i| if i == 100 { 4.0 } else { 0.0 }).collect();
        let knocking = with_extra(repeat(14.7, 200), retard, "SPK: Knock retard", "deg");

        let heard = topic(&knocking, "Knock").unwrap();
        assert_eq!(heard.level, InsightLevel::Warning);
        assert!(heard.title.contains("4.0"));
    }

    #[test]
    fn a_closed_loop_pinned_at_a_hundred_is_said_to_have_never_engaged() {
        // Found on a real session: every reading was open loop and nothing said so, which makes
        // the whole log mean something different.
        let log = with_extra(repeat(14.7, 200), repeat(100.0, 200), "EGO cor1", "%");

        let looped = topic(&log, "Closed loop").unwrap();

        assert_eq!(looped.level, InsightLevel::Note);
        assert!(looped.title.contains("never moved"));
    }

    #[test]
    fn a_correction_sitting_to_one_side_is_the_table_being_wrong_by_that_much() {
        let trim = (0..200).map(|i| 108.0 + if i % 2 == 0 { 0.4 } else { -0.4 }).collect();
        let log = with_extra(repeat(14.7, 200), trim, "EGO cor1", "%");

        let looped = topic(&log, "Closed loop").unwrap();

        assert_eq!(looped.level, InsightLevel::Watch);
        assert!(looped.title.contains('8'));
    }

    #[test]
    fn an_engine_that_never_warmed_up_is_said_to_have_not_warmed_up() {
        let log = steady(
            repeat(14.7, 200),
            Steady {
                clt: 120.0,
                ..Steady::default()
            },
        );

        let warmup = topic(&log, "Warmup").unwrap();

        assert_eq!(warmup.level, InsightLevel::Note);
        assert!(warmup.title.contains("never reached operating temperature"));
    }

    #[test]
    fn a_manifold_that_never_pulls_vacuum_is_a_warning() {
        // A sensor reading atmospheric for ever is not plumbed in, and every load number in the
        // log is then wrong while looking reasonable.
        let log = steady(
            repeat(14.7, 200),
            Steady {
                map: Some(repeat(101.0, 200)),
                ..Steady::default()
            },
        );

        let manifold = topic(&log, "Manifold pressure").unwrap();

        assert_eq!(manifold.level, InsightLevel::Warning);
        assert!(manifold.title.contains("never fell below"));
    }

    fn idle_log(rpm: Vec<f64>) -> LogDocument {
        let n = rpm.len();
        document(
            "idle",
            channel("Time", "s", &ticks(n)),
            vec![
                channel("RPM", "RPM", &rpm),
                channel("MAP", "kPa", &repeat(40.0, n)),
                channel("TPS", "%", &repeat(0.5, n)),
                channel("CLT", "°F", &repeat(190.0, n)),
            ],
        )
    }

    #[test]
    fn a_hunting_idle_is_measured_rather_than_eyeballed() {
        let rpm = (0..400)
            .map(|i| 800.0 + if i % 2 == 0 { 220.0 } else { -220.0 })
            .collect();

        let idle = topic(&idle_log(rpm), "Idle").unwrap();

        assert_eq!(idle.level, InsightLevel::Watch);
        assert!(idle.title.contains("hunting"));
        assert!(idle.evidence.contains("standard deviation"));
    }

    #[test]
    fn a_steady_idle_is_said_to_be_steady() {
        let rpm = (0..400).map(|i| 850.0 + if i % 2 == 0 { 8.0 } else { -8.0 }).collect();

        assert_eq!(topic(&idle_log(rpm), "Idle").unwrap().level, InsightLevel::Good);
    }

    #[test]
    fn a_channel_that_never_moves_is_a_failed_sensor() {
        let log = steady(
            repeat(14.7, 200),
            Steady {
                rpm: Some(repeat(3000.0, 200)),
                ..Steady::default()
            },
        );

        // MAP, TPS, CLT and the target are all flat in this fixture by design, which is exactly
        // what a stuck sensor looks like.
        let sensors = topic(&log, "Sensors").unwrap();
        assert_eq!(sensors.level, InsightLevel::Warning);
        assert!(sensors.title.contains("never changed"));
    }

    #[test]
    fn a_gap_in_the_recording_is_reported_because_it_weights_everything_else() {
        let n = 200;
        let time: Vec<f64> = (0..n)
            .map(|i| {
                if i < 100 {
                    i as f64 * 0.1
                } else {
                    (i as f64 * 0.1) + 30.0
                }
            })
            .collect();

        let log = document(
            "gappy",
            channel("Time", "s", &time),
            vec![
                channel("RPM", "RPM", &repeat(3000.0, n)),
                channel("MAP", "kPa", &repeat(90.0, n)),
            ],
        );

        let recording = topic(&log, "Recording").unwrap();

        assert_eq!(recording.level, InsightLevel::Watch);
        assert!(recording.title.contains("stalled"));
    }

    // ----- saying nothing, out loud ------------------------------------------

    #[test]
    fn a_log_with_no_wideband_says_so_rather_than_staying_silent() {
        let log = document(
            "bare",
            channel("Time", "s", &ticks(100)),
            vec![channel("RPM", "RPM", &repeat(3000.0, 100))],
        );

        let mixture = topic(&log, "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Unanswered);
        assert!(mixture.title.contains("No wideband"));
    }

    #[test]
    fn a_log_with_a_wideband_but_no_target_says_which_is_missing() {
        let log = document(
            "no target",
            channel("Time", "s", &ticks(100)),
            vec![
                channel("RPM", "RPM", &repeat(3000.0, 100)),
                channel("AFR", "AFR", &repeat(14.7, 100)),
            ],
        );

        let mixture = topic(&log, "Mixture").unwrap();

        assert_eq!(mixture.level, InsightLevel::Unanswered);
        assert!(mixture.title.contains("No AFR target"));
    }

    #[test]
    fn an_empty_log_is_not_analysed_at_all() {
        let log = document("empty", channel("Time", "s", &[]), vec![channel("RPM", "RPM", &[])]);

        let found = from(&log);

        assert_eq!(found.len(), 1);
        assert_eq!(found[0].level, InsightLevel::Unanswered);
    }

    // ----- how they are presented --------------------------------------------

    #[test]
    fn the_worst_finding_comes_first() {
        let afr = (0..200).map(|i| if i % 20 == 0 { 16.5 } else { 13.5 }).collect();
        let log = steady(
            afr,
            Steady {
                map: Some(repeat(150.0, 200)),
                ..Steady::default()
            },
        );

        let found = from(&log);

        assert!(!found.is_empty());
        assert_eq!(found[0].level, InsightLevel::Warning);

        // And never worse after better.
        for i in 1..found.len() {
            assert!(rank(found[i - 1].level) >= rank(found[i].level));
        }
    }

    #[test]
    fn every_finding_carries_the_numbers_behind_it() {
        for found in from(&steady(repeat(15.7, 200), Steady::default())) {
            if found.level == InsightLevel::Unanswered {
                continue;
            }

            assert!(!found.evidence.trim().is_empty(), "{}", found.title);

            // A finding that says there was not enough of something rests on exactly that
            // little, and nought is the honest count.
            if !found.title.starts_with("Not enough") {
                assert!(found.samples > 0, "{}", found.title);
            }
        }
    }

    // ----- knowing atmospheric from guessing it -------------------------------

    /// A boosted pull, with whatever pressure channels are named.
    fn boosted(baro: Option<(f64, &str)>, key_on_first: bool) -> LogDocument {
        const N: usize = 300;

        let load = |i: usize| -> f64 {
            if i < N / 3 {
                0.0
            } else if i < 2 * N / 3 {
                1.0
            } else {
                0.2
            }
        };
        let col = |g: &dyn Fn(usize) -> f64| -> Vec<f64> { (0..N).map(g).collect() };

        // The first tenth is the key on and the engine stopped, which is where a manifold reads
        // atmospheric.
        let mut channels = vec![
            channel(
                "RPM",
                "rpm",
                &col(&|i| {
                    if key_on_first && i < N / 10 {
                        0.0
                    } else {
                        900.0 + (load(i) * 5100.0)
                    }
                }),
            ),
            channel(
                "MAP",
                "kPa",
                &col(&|i| {
                    if key_on_first && i < N / 10 {
                        99.0
                    } else {
                        35.0 + (load(i) * 215.0)
                    }
                }),
            ),
            channel("TPS", "%", &col(&|i| load(i) * 95.0)),
            channel("AFR", "AFR", &col(&|i| if load(i) > 0.5 { 12.4 } else { 14.5 })),
            channel(
                "AFR 1 Target",
                "AFR",
                &col(&|i| if load(i) > 0.5 { 11.8 } else { 14.5 }),
            ),
            channel("CLT", "°F", &col(&|i| 186.0 + (i as f64 / N as f64 * 8.0))),
        ];

        if let Some((value, units)) = baro {
            channels.push(channel("Barometer", units, &repeat(value, N)));
        }

        document("boost.mlg", channel("Time", "s", &ticks(N)), channels)
    }

    #[test]
    fn a_boosted_log_with_nothing_to_read_ambient_from_says_so_rather_than_calling_it_healthy() {
        // Taking the highest reading for ambient makes the boost test compare a number with
        // itself, which is never true, so a turbo at 250 kPa was reported as "what a healthy
        // naturally aspirated engine reads".
        let manifold = single(from(&boosted(None, false)), "Manifold pressure");

        assert_eq!(manifold.level, InsightLevel::Unanswered);
        assert!(!manifold.detail.contains("naturally aspirated engine reads"));
    }

    #[test]
    fn with_a_barometer_it_knows_the_boost_for_what_it_is() {
        let manifold = single(from(&boosted(Some((99.0, "kPa")), false)), "Manifold pressure");

        assert_eq!(manifold.level, InsightLevel::Note);
        assert!(manifold.title.to_lowercase().contains("boost"));
    }

    fn assert_baro_is_read_in(reading: f64, units: &str) {
        // The old test was "greater than fifty", a bare-kilopascal assumption that discarded
        // every baro from a controller set to imperial units.
        let found = from(&boosted(Some((reading, units)), false));

        assert_eq!(single(found, "Manifold pressure").level, InsightLevel::Note, "{units}");
    }

    #[test]
    fn a_barometer_is_read_in_whatever_it_was_logged_in() {
        assert_baro_is_read_in(14.4, "psi");
        assert_baro_is_read_in(0.99, "bar");
        assert_baro_is_read_in(990.0, "mbar");
    }

    #[test]
    fn and_a_stopped_engine_is_itself_a_barometer() {
        // A stationary engine's manifold is open to the atmosphere through the throttle, and
        // most logs begin with the key on before cranking.
        let found = from(&boosted(None, true));

        assert_eq!(single(found, "Manifold pressure").level, InsightLevel::Note);
    }

    #[test]
    fn and_the_loaded_mixture_is_still_judged_without_an_ambient_figure() {
        // The threshold was nine tenths of ambient, which without a real figure is nine tenths
        // of peak boost, excluding almost all the loaded running the check exists for, and
        // reporting there was not enough of it.
        let loaded = single(from(&boosted(None, false)), "Mixture under load");

        assert_ne!(loaded.level, InsightLevel::Unanswered);
    }

    #[test]
    fn a_deceleration_is_not_counted_as_idle_hunting() {
        // Below 1,500 with the throttle shut is idle *or* the tail of an overrun, and the two
        // are nothing alike: coming down from 4,000 the engine sweeps the band in a second or
        // two. Every one of those samples used to count as idle, so on any road log the spread
        // was dominated by decelerations and a perfectly steady idle read as hunting.
        const N: usize = 600;

        // Half the log: repeated pulls, then closed-throttle decel sweeps down through the
        // idle band. The other half: a rock-steady 850 rpm idle.
        let rpm = |i: usize| -> f64 {
            if i >= N / 2 {
                return 850.0 + if i.is_multiple_of(3) { 4.0 } else { -4.0 };
            }

            let in_cycle = (i % 100) as f64;

            if in_cycle < 50.0 {
                1200.0 + (in_cycle * 56.0)
            } else {
                4000.0 - ((in_cycle - 50.0) * 64.0)
            }
        };
        let tps = |i: usize| -> f64 {
            if i >= N / 2 {
                0.0
            } else if i % 100 < 50 {
                80.0
            } else {
                0.0
            }
        };

        let col = |g: &dyn Fn(usize) -> f64| -> Vec<f64> { (0..N).map(g).collect() };

        let log = document(
            "decel.mlg",
            channel("Time", "s", &ticks(N)),
            vec![
                channel("RPM", "rpm", &col(&rpm)),
                channel("TPS", "%", &col(&tps)),
                channel("CLT", "°F", &repeat(190.0, N)),
                channel("MAP", "kPa", &col(&|i| if tps(i) > 10.0 { 160.0 } else { 35.0 })),
            ],
        );

        let idle = single(from(&log), "Idle");

        // The idle here is steady within a few rpm. Counting the sweeps made it read as
        // hundreds.
        assert_ne!(idle.level, InsightLevel::Watch);
        assert_ne!(idle.level, InsightLevel::Warning);
    }
}
