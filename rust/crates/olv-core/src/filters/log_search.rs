//! Ported from `LogSearch.cs`. Owned by package `w2-filters`.
//!
//! Finds where in a log a condition was true: "RPM > 4000 && TPS > 80", or anything else the
//! calculated-channel syntax can say. The same expression language, deliberately: a tuner who has
//! written one calculated channel already knows how to write a search, and a search that proves
//! useful can be pasted into a calculated channel or a filter without translation.
//!
//! What this adds over a filter is *where*. A filter answers which samples to count and throws the
//! rest away; this answers which moments of the drive to go and look at, and leaves the log alone.

use std::sync::Arc;

use super::log_filter::SampleMask;
use crate::base::math_expression::MathExpression;
use crate::model::{IgnoreCaseMap, LogChannel, LogDocument};

/// Samples that may fall short inside a run without ending it.
///
/// A signal sitting near its threshold crosses it repeatedly: RPM wandering about 4,000 against
/// "RPM > 4000" alternates true and false every few samples. Strictly consecutive runs would report
/// that as fifty separate findings when it is plainly one, so a brief dip below is bridged, as it is
/// when a table cell's visits are worked out.
pub const DEFAULT_GAP_TOLERANCE: usize = 2;

/// Where in a log a condition held.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LogSearchResult {
    runs: Vec<(usize, usize)>,
    matches: usize,
    unknown: usize,
    problem: Option<String>,
}

impl LogSearchResult {
    fn nothing(problem: Option<String>) -> Self {
        Self {
            runs: Vec::new(),
            matches: 0,
            unknown: 0,
            problem,
        }
    }

    /// Stretches of the log the condition held over, in order, as (first, last) sample indices.
    /// Consecutive matching samples are one run rather than many.
    pub fn runs(&self) -> &[(usize, usize)] {
        &self.runs
    }

    /// Samples that matched, which is not the same as the number of runs.
    pub fn matches(&self) -> usize {
        self.matches
    }

    /// Samples the condition could not be judged at, because a channel it names had no reading
    /// there.
    ///
    /// Counted apart from the misses. A comparison against a missing reading is not false, it is
    /// unanswerable, and quietly folding those into "did not match" would report a confident answer
    /// about a stretch of log that has nothing to say.
    pub fn unknown(&self) -> usize {
        self.unknown
    }

    /// Why the search could not be run, where that is the case.
    pub fn problem(&self) -> Option<&str> {
        self.problem.as_deref()
    }

    pub fn has_problem(&self) -> bool {
        self.problem.as_deref().is_some_and(|p| !p.is_empty())
    }

    pub fn is_empty(&self) -> bool {
        self.runs.is_empty()
    }

    /// The run holding a sample, or the next one after it, as its index. `None` past the last run.
    pub fn run_at_or_after(&self, sample: usize) -> Option<usize> {
        self.runs.iter().position(|&(_, last)| last >= sample)
    }
}

pub struct LogSearch;

impl LogSearch {
    /// [`find_with_tolerance`](Self::find_with_tolerance) at [`DEFAULT_GAP_TOLERANCE`].
    pub fn find(document: &LogDocument, condition: &str, mask: Option<&SampleMask>) -> LogSearchResult {
        Self::find_with_tolerance(document, condition, mask, DEFAULT_GAP_TOLERANCE)
    }

    /// Evaluates `condition` at every sample and groups the matches into runs.
    ///
    /// `mask` holds the filters. A sample the user has filtered out is not searched: the filters say
    /// which part of the drive is under consideration, and a search that jumped to a moment they
    /// exclude would be answering a question that was not asked.
    pub fn find_with_tolerance(
        document: &LogDocument,
        condition: &str,
        mask: Option<&SampleMask>,
        gap_tolerance: usize,
    ) -> LogSearchResult {
        if condition.trim().is_empty() {
            return LogSearchResult::nothing(None);
        }

        let mut available: IgnoreCaseMap<Arc<LogChannel>> = IgnoreCaseMap::new();
        for channel in document.channels() {
            available.insert(channel.name(), Arc::clone(channel));
        }
        let time = document.time();
        available.insert(time.name(), Arc::clone(time));

        let names: Vec<&str> = available.keys().collect();
        let expression = match MathExpression::try_parse(condition, names) {
            Ok(expression) => expression,
            Err(error) => return LogSearchResult::nothing(Some(error)),
        };

        // The parser only names channels it was given, so every reference is in `available`.
        let sources: Vec<Arc<LogChannel>> = expression
            .references()
            .iter()
            .map(|name| {
                Arc::clone(
                    available
                        .get(name)
                        .expect("the parser names only the channels it was given"),
                )
            })
            .collect();

        let count = document.sample_count();
        let mut runs: Vec<(usize, usize)> = Vec::new();
        let mut matches = 0;
        let mut unknown = 0;
        let mut start: Option<usize> = None;
        let mut previous = 0;
        let mut inputs = vec![0.0; sources.len()];

        for i in 0..count {
            if let Some(mask) = mask
                && !mask.is_accepted(i)
            {
                continue;
            }

            for (slot, source) in inputs.iter_mut().zip(&sources) {
                *slot = source.at(i);
            }

            let value = expression.evaluate(&inputs);

            if value.is_nan() {
                unknown += 1;
                continue;
            }

            // Anything that is not zero counts, so a bare channel name reads as "wherever this is not
            // nothing" and a comparison reads as itself. Infinities are not a reading, whatever their
            // sign.
            if !value.is_finite() || value == 0.0 {
                continue;
            }

            matches += 1;

            match start {
                None => {
                    start = Some(i);
                    previous = i;
                }
                Some(_) if i - previous <= gap_tolerance + 1 => {
                    previous = i;
                }
                Some(first) => {
                    runs.push((first, previous));
                    start = Some(i);
                    previous = i;
                }
            }
        }

        if let Some(first) = start {
            runs.push((first, previous));
        }

        LogSearchResult {
            runs,
            matches,
            unknown,
            problem: None,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A log from (name, samples), with a time base of 0.1 s per sample.
    fn log(channels: &[(&str, &[f64])]) -> LogDocument {
        let count = channels.iter().map(|c| c.1.len()).max().unwrap_or(0);
        let time: Vec<f64> = (0..count).map(|i| i as f64 * 0.1).collect();

        LogDocument::new(
            "x",
            "test",
            channels
                .iter()
                .map(|(name, values)| Arc::new(LogChannel::new(name, "", 2, values, false)))
                .collect(),
            Arc::new(LogChannel::new("Time", "s", 3, &time, true)),
        )
    }

    fn rpm(values: &[f64]) -> LogDocument {
        log(&[("RPM", values)])
    }

    // ----- finding ----------------------------------------------------------

    #[test]
    fn a_condition_finds_the_samples_it_held_at() {
        let found = LogSearch::find(&rpm(&[1000.0, 5000.0, 5200.0, 1000.0, 1000.0]), "RPM > 4000", None);

        assert!(!found.has_problem());
        assert_eq!(found.matches(), 2);
        assert_eq!(found.runs(), [(1, 2)]);
    }

    #[test]
    fn separate_stretches_are_separate_runs() {
        let found = LogSearch::find(
            &rpm(&[5000.0, 5000.0, 1000.0, 1000.0, 1000.0, 1000.0, 5000.0, 5000.0]),
            "RPM > 4000",
            None,
        );

        assert_eq!(found.runs(), [(0, 1), (6, 7)]);
        assert_eq!(found.matches(), 4);
    }

    #[test]
    fn a_signal_chattering_at_its_threshold_is_one_finding_rather_than_fifty() {
        // RPM wandering about 4,000 crosses it every few samples. Reporting each crossing separately
        // would bury the one thing that happened.
        let rpm_values = [
            4100.0, 3900.0, 4100.0, 3900.0, 4100.0, 4100.0, 1000.0, 1000.0, 1000.0, 1000.0,
        ];

        let found = LogSearch::find(&rpm(&rpm_values), "RPM > 4000", None);

        assert_eq!(found.runs(), [(0, 5)]);
    }

    #[test]
    fn a_wider_gap_still_separates_two_findings() {
        let rpm_values = [4100.0, 1000.0, 1000.0, 1000.0, 1000.0, 4100.0];

        let found = LogSearch::find(&rpm(&rpm_values), "RPM > 4000", None);

        assert_eq!(found.runs().len(), 2);
    }

    #[test]
    fn the_tolerance_can_be_tightened_to_strictly_consecutive_samples() {
        let found = LogSearch::find_with_tolerance(&rpm(&[4100.0, 3900.0, 4100.0]), "RPM > 4000", None, 0);

        assert_eq!(found.runs().len(), 2);
    }

    #[test]
    fn conditions_combine_the_way_they_do_in_a_calculated_channel() {
        // Wide open only where both hold: sample 0 has revs and throttle, 1 has revs without throttle,
        // 2 throttle without revs, and 6 both again.
        let document = log(&[
            ("RPM", &[5000.0, 5000.0, 1000.0, 1000.0, 1000.0, 1000.0, 5000.0]),
            ("TPS", &[90.0, 10.0, 90.0, 10.0, 10.0, 10.0, 95.0]),
        ]);

        let found = LogSearch::find(&document, "RPM > 4000 && TPS > 80", None);

        assert_eq!(found.matches(), 2);
        assert_eq!(found.runs(), [(0, 0), (6, 6)]);
    }

    #[test]
    fn channel_names_with_spaces_need_no_quoting() {
        let document = log(&[("AFR Target 1", &[14.7, 12.0, 14.7])]);

        let found = LogSearch::find(&document, "AFR Target 1 < 13", None);

        assert_eq!(found.runs(), [(1, 1)]);
    }

    #[test]
    fn the_time_channel_can_be_searched_like_any_other() {
        let found = LogSearch::find(&rpm(&[1.0, 2.0, 3.0, 4.0, 5.0]), "Time >= 0.3", None);

        assert_eq!(found.matches(), 2);
    }

    #[test]
    fn a_bare_channel_reads_as_wherever_it_is_not_zero() {
        let found = LogSearch::find(&rpm(&[0.0, 0.0, 1500.0, 1500.0, 0.0]), "RPM", None);

        assert_eq!(found.matches(), 2);
        assert_eq!(found.runs()[0], (2, 3));
    }

    #[test]
    fn a_negative_value_counts_as_much_as_a_positive_one() {
        // Not zero is the test, so a channel swinging either side of nothing is found on both sides
        // of it.
        let found = LogSearch::find(&log(&[("Trim", &[-5.0, 0.0, 5.0])]), "Trim", None);

        assert_eq!(found.matches(), 2);
    }

    // ----- what it declines to answer ---------------------------------------

    #[test]
    fn a_sample_with_no_reading_is_unknown_rather_than_a_miss() {
        // A comparison against a reading that was never taken is unanswerable, and folding it into
        // "did not match" would report confidence about a stretch of log that has nothing to say.
        let found = LogSearch::find(&rpm(&[5000.0, f64::NAN, f64::NAN, 1000.0]), "RPM > 4000", None);

        assert_eq!(found.matches(), 1);
        assert_eq!(found.unknown(), 2);
    }

    #[test]
    fn a_gap_does_not_join_two_runs_across_it() {
        let found = LogSearch::find(
            &rpm(&[5000.0, f64::NAN, f64::NAN, f64::NAN, f64::NAN, 5000.0]),
            "RPM > 4000",
            None,
        );

        assert_eq!(found.runs().len(), 2);
    }

    #[test]
    fn a_condition_naming_a_channel_the_log_lacks_says_so_rather_than_finding_nothing() {
        let found = LogSearch::find(&rpm(&[1000.0]), "Boost > 10", None);

        assert!(found.has_problem());
        assert!(found.is_empty());
    }

    #[test]
    fn a_condition_that_does_not_parse_says_so() {
        let found = LogSearch::find(&rpm(&[1000.0]), "RPM >", None);

        assert!(found.has_problem());
    }

    #[test]
    fn an_empty_condition_is_not_an_error() {
        // Both the empty string and a blank one.
        for condition in ["", "   "] {
            let found = LogSearch::find(&rpm(&[1000.0]), condition, None);

            assert!(!found.has_problem());
            assert!(found.is_empty());
        }
    }

    #[test]
    fn an_infinite_result_is_not_a_match() {
        // Dividing by zero is not a reading, whichever way it went.
        let found = LogSearch::find(&log(&[("A", &[1.0, 2.0]), ("B", &[0.0, 1.0])]), "A / B", None);

        assert_eq!(found.matches(), 1);
        assert_eq!(found.runs()[0], (1, 1));
    }

    // ----- filters ----------------------------------------------------------

    #[test]
    fn filtered_samples_are_not_searched() {
        // The filters say which part of the drive is under consideration, so a search must not jump
        // to a moment they exclude.
        let document = rpm(&[5000.0, 5000.0, 5000.0, 5000.0]);
        let mask = SampleMask::new(vec![false, false, true, true], 2, 4, Vec::new(), true);

        let found = LogSearch::find(&document, "RPM > 4000", Some(&mask));

        assert_eq!(found.matches(), 2);
        assert_eq!(found.runs()[0], (2, 3));
    }

    // ----- stepping through -------------------------------------------------

    #[test]
    fn the_run_holding_or_following_a_sample_is_found() {
        let found = LogSearch::find(
            &rpm(&[5000.0, 1000.0, 1000.0, 1000.0, 5000.0, 1000.0, 1000.0, 1000.0, 5000.0]),
            "RPM > 4000",
            None,
        );

        assert_eq!(found.runs().len(), 3);

        assert_eq!(found.run_at_or_after(0), Some(0)); // inside the first
        assert_eq!(found.run_at_or_after(1), Some(1)); // before the second
        assert_eq!(found.run_at_or_after(4), Some(1)); // inside the second
        assert_eq!(found.run_at_or_after(5), Some(2));
        assert_eq!(found.run_at_or_after(99), None); // past the last
    }

    #[test]
    fn no_runs_means_nothing_to_step_to() {
        assert_eq!(
            LogSearch::find(&rpm(&[1000.0]), "RPM > 4000", None).run_at_or_after(0),
            None
        );
    }
}
