//! Ported from `LogFilter.cs`. Owned by package `w2-filters`.
//!
//! A condition a sample must satisfy to be counted. Filters exist so a table is built from data
//! worth reading: warmup, overrun and idle skew a fuel table badly, and a cell averaged over both
//! a cold start and a hot cruise describes neither.
//!
//! [`SampleFilter::build`] combines the enabled filters with AND. [`SampleFilter::suggest`]
//! offers the filters that most often ruin a fuel table, for the channels a log actually has,
//! all switched off.

use std::sync::Arc;

use crate::channels::channel_roles::{self, ChannelRole};
use crate::fmt;
use crate::model::{LogChannel, LogDocument};

/// How a filter's reading is judged against its bounds.
///
/// `Other` is a number the C# enum does not name. The C# reads such a number from a hand-edited
/// file without complaint and then matches every sample (its `_ => true` arm), so this does the
/// same rather than refusing the file.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FilterComparison {
    Above,
    AboveOrEqual,
    Below,
    BelowOrEqual,
    Between,
    Outside,
    Other(i32),
}

impl FilterComparison {
    /// Every name the file may use, in the C# enum's order.
    pub(crate) const NAMED: [FilterComparison; 6] = [
        Self::Above,
        Self::AboveOrEqual,
        Self::Below,
        Self::BelowOrEqual,
        Self::Between,
        Self::Outside,
    ];

    /// The C# enum's underlying integer, which is what a file holds when the name is not used.
    pub(crate) fn from_index(index: i32) -> Self {
        match index {
            0 => Self::Above,
            1 => Self::AboveOrEqual,
            2 => Self::Below,
            3 => Self::BelowOrEqual,
            4 => Self::Between,
            5 => Self::Outside,
            other => Self::Other(other),
        }
    }

    pub(crate) fn index(self) -> i32 {
        match self {
            Self::Above => 0,
            Self::AboveOrEqual => 1,
            Self::Below => 2,
            Self::BelowOrEqual => 3,
            Self::Between => 4,
            Self::Outside => 5,
            Self::Other(n) => n,
        }
    }

    /// The camelCase name the file uses, or `None` for a number the C# enum does not name.
    pub(crate) fn json_name(self) -> Option<&'static str> {
        Some(match self {
            Self::Above => "above",
            Self::AboveOrEqual => "aboveOrEqual",
            Self::Below => "below",
            Self::BelowOrEqual => "belowOrEqual",
            Self::Between => "between",
            Self::Outside => "outside",
            Self::Other(_) => return None,
        })
    }
}

/// A condition on one channel.
///
/// Filters are matched to channels by name, so a set written against one log applies to any
/// other that shares those channel names.
#[derive(Clone, Debug)]
pub struct LogFilter {
    name: String,
    channel: String,
    comparison: FilterComparison,
    low: f64,
    high: f64,
    enabled: bool,
}

impl LogFilter {
    /// A filter with both bounds at zero, switched on. Set the bounds with
    /// [`with_low`](Self::with_low) and [`with_high`](Self::with_high).
    pub fn new(name: &str, channel: &str, comparison: FilterComparison) -> Self {
        Self {
            name: name.to_string(),
            channel: channel.to_string(),
            comparison,
            low: 0.0,
            high: 0.0,
            enabled: true,
        }
    }

    pub fn with_low(mut self, low: f64) -> Self {
        self.low = low;
        self
    }

    pub fn with_high(mut self, high: f64) -> Self {
        self.high = high;
        self
    }

    pub fn with_enabled(mut self, enabled: bool) -> Self {
        self.enabled = enabled;
        self
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    /// The channel this tests, matched by name so it can apply across logs.
    pub fn channel(&self) -> &str {
        &self.channel
    }

    pub fn comparison(&self) -> FilterComparison {
        self.comparison
    }

    pub fn low(&self) -> f64 {
        self.low
    }

    pub fn high(&self) -> f64 {
        self.high
    }

    pub fn enabled(&self) -> bool {
        self.enabled
    }

    /// Whether a reading satisfies the condition.
    pub fn accepts(&self, value: f64) -> bool {
        // A missing reading cannot be shown to satisfy the condition.
        if value.is_nan() {
            return false;
        }

        let (low, high) = (self.low, self.high);
        match self.comparison {
            FilterComparison::Above => value > low,
            FilterComparison::AboveOrEqual => value >= low,
            FilterComparison::Below => value < low,
            FilterComparison::BelowOrEqual => value <= low,
            FilterComparison::Between => value >= net_min(low, high) && value <= net_max(low, high),
            FilterComparison::Outside => value < net_min(low, high) || value > net_max(low, high),
            FilterComparison::Other(_) => true,
        }
    }

    /// Reads back as the condition itself, e.g. "CLT ≥ 160".
    pub fn describe(&self) -> String {
        let low = format_bound(self.low);
        let high = format_bound(self.high);
        let channel = &self.channel;

        match self.comparison {
            FilterComparison::Above => format!("{channel} > {low}"),
            FilterComparison::AboveOrEqual => format!("{channel} ≥ {low}"),
            FilterComparison::Below => format!("{channel} < {low}"),
            FilterComparison::BelowOrEqual => format!("{channel} ≤ {low}"),
            FilterComparison::Between => format!("{channel} {low}…{high}"),
            FilterComparison::Outside => format!("{channel} outside {low}…{high}"),
            FilterComparison::Other(_) => channel.clone(),
        }
    }
}

/// Record equality, as the C# `record` has it: every property, with doubles compared the way
/// `double.Equals` compares them (a NaN equals a NaN).
impl PartialEq for LogFilter {
    fn eq(&self, other: &Self) -> bool {
        self.name == other.name
            && self.channel == other.channel
            && self.comparison == other.comparison
            && same_double(self.low, other.low)
            && same_double(self.high, other.high)
            && self.enabled == other.enabled
    }
}

fn same_double(a: f64, b: f64) -> bool {
    a == b || (a.is_nan() && b.is_nan())
}

/// `Math.Min` for doubles: a NaN on either side gives NaN, unlike `f64::min`.
fn net_min(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a < b {
        a
    } else {
        b
    }
}

/// `Math.Max` for doubles: a NaN on either side gives NaN.
fn net_max(a: f64, b: f64) -> f64 {
    if a.is_nan() || b.is_nan() {
        f64::NAN
    } else if a > b {
        a
    } else {
        b
    }
}

/// A bound as [`LogFilter::describe`] shows it: a whole number without a point, anything else to
/// two places at most.
fn format_bound(v: f64) -> String {
    if v == v.floor() && v.abs() < 1e9 {
        (v as i64).to_string()
    } else {
        fmt::f64_inv(v, "0.##")
    }
}

/// Which samples survived a set of filters.
#[derive(Clone, Debug, PartialEq)]
pub struct SampleMask {
    accepted: Vec<bool>,
    pass_count: usize,
    total: usize,
    unknown_channels: Vec<String>,
    filters_applied: bool,
}

impl SampleMask {
    pub fn new(
        accepted: Vec<bool>,
        pass_count: usize,
        total: usize,
        unknown_channels: Vec<String>,
        filters_applied: bool,
    ) -> Self {
        Self {
            accepted,
            pass_count,
            total,
            unknown_channels,
            filters_applied,
        }
    }

    /// A mask that keeps every one of `count` samples and applies no filters.
    pub fn accept_all(count: usize) -> Self {
        Self::new(Vec::new(), count, count, Vec::new(), false)
    }

    /// Per-sample verdicts. Empty when no filters applied.
    pub fn accepted(&self) -> &[bool] {
        &self.accepted
    }

    pub fn pass_count(&self) -> usize {
        self.pass_count
    }

    pub fn total(&self) -> usize {
        self.total
    }

    /// Filters that named a channel this log does not have.
    pub fn unknown_channels(&self) -> &[String] {
        &self.unknown_channels
    }

    pub fn filters_applied(&self) -> bool {
        self.filters_applied
    }

    /// True when the sample should be counted.
    pub fn is_accepted(&self, index: usize) -> bool {
        !self.filters_applied || self.accepted[index]
    }
}

pub struct SampleFilter;

impl SampleFilter {
    /// Evaluates every enabled filter against each sample. Filters combine with AND: each one
    /// narrows the set further, which is what "throw these out" means when several conditions
    /// are on at once.
    ///
    /// A filter naming a channel the log does not have is reported and skipped, rather than
    /// silently rejecting every sample.
    pub fn build(document: &LogDocument, filters: &[LogFilter]) -> SampleMask {
        let count = document.sample_count();

        let mut active: Vec<(&LogFilter, &Arc<LogChannel>)> = Vec::new();
        let mut unknown: Vec<String> = Vec::new();

        for filter in filters.iter().filter(|f| f.enabled) {
            match document.find_channel(&filter.channel) {
                None => unknown.push(filter.channel.clone()),
                Some(channel) => active.push((filter, channel)),
            }
        }

        if active.is_empty() {
            return SampleMask::new(Vec::new(), count, count, unknown, false);
        }

        let mut accepted = vec![false; count];
        let mut passed = 0;

        for (i, slot) in accepted.iter_mut().enumerate() {
            let ok = active.iter().all(|(filter, channel)| filter.accepts(channel.at(i)));
            *slot = ok;
            if ok {
                passed += 1;
            }
        }

        SampleMask::new(accepted, passed, count, unknown, true)
    }

    /// Filters worth having on by default, for the channels a log actually has. These are the
    /// conditions that most often ruin a fuel table. All arrive switched off.
    pub fn suggest(document: &LogDocument) -> Vec<LogFilter> {
        // Identifying the channel and deciding whether to filter on it are separate questions. A
        // coolant reading that never moves is still the coolant, and looking past it would find
        // something that is not: a MegaSquirt log holds both "AFR" and "AFR Load", and skipping a
        // flat AFR offered a mixture filter on a load axis.
        let moving = |role: ChannelRole| -> Option<&Arc<LogChannel>> {
            channel_roles::find(document, role).filter(|channel| !channel.is_flat())
        };

        let mut suggested = Vec::new();

        if let Some(rpm) = moving(ChannelRole::EngineSpeed) {
            suggested.push(
                LogFilter::new("Engine running", rpm.name(), FilterComparison::AboveOrEqual)
                    .with_low(500.0)
                    .with_enabled(false),
            );
        }

        if let Some(clt) = moving(ChannelRole::Coolant) {
            // Operating temperature, in whatever unit the log uses. Judged by the readings as well
            // as the label, because plenty of firmware writes a placeholder rather than a unit.
            let fahrenheit = clt.units().chars().any(|c| c == 'F' || c == 'f') || clt.max() > 130.0;

            suggested.push(
                LogFilter::new("Up to temperature", clt.name(), FilterComparison::AboveOrEqual)
                    .with_low(if fahrenheit { 160.0 } else { 70.0 })
                    .with_enabled(false),
            );
        }

        if let Some(tps) = moving(ChannelRole::Throttle) {
            // Idle is its own regime: the ECU is holding a speed rather than metering for a demand,
            // and averaging it into the low-load cells of a fuel table describes neither.
            //
            // Named for what it does and no more. It is tempting to call this "not on overrun", and
            // it does not earn that: on the MaxxECU log the samples that look like overrun sit at
            // six to sixteen per cent throttle and sail straight through. Use the fuel-cut filter
            // for that, which is the controller saying so rather than a guess.
            suggested.push(
                LogFilter::new("Off idle", tps.name(), FilterComparison::Above)
                    .with_low(1.0)
                    .with_enabled(false),
            );
        }

        if let Some(cut) = moving(ChannelRole::FuelCut) {
            // While the ECU is cutting fuel there is no fuelling to judge: the exhaust is full of air
            // the engine pumped through without burning, and the wideband reports it faithfully.
            // Those samples say nothing about the table and would drag whichever cells they land in.
            //
            // Measured on the MaxxECU log: active in 70 samples of 6,466, few but every one meaningless.
            suggested.push(
                LogFilter::new("Not cutting fuel", cut.name(), FilterComparison::BelowOrEqual)
                    .with_low(0.0)
                    .with_enabled(false),
            );
        }

        if let Some(mixture) = moving(ChannelRole::Mixture) {
            // The plausible range depends on which scale it is on, and the readings say which far
            // more reliably than the label: lambda sits about 1, an air-fuel ratio about 14. Nine to
            // twenty on a lambda channel would accept everything and filter nothing.
            let lambda = mixture.max() < 3.0;
            let (name, low, high) = if lambda {
                ("Lambda in range", 0.6, 1.4)
            } else {
                ("AFR in range", 9.0, 20.0)
            };

            suggested.push(
                LogFilter::new(name, mixture.name(), FilterComparison::Between)
                    .with_low(low)
                    .with_high(high)
                    .with_enabled(false),
            );
        }

        suggested
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A log from (name, units, samples), with a time base of 0.1 s per sample.
    fn log(channels: &[(&str, &str, &[f64])]) -> LogDocument {
        let count = channels.iter().map(|c| c.2.len()).max().unwrap_or(0);
        let time: Vec<f64> = (0..count).map(|i| i as f64 * 0.1).collect();

        LogDocument::new(
            "test.csv",
            "CSV",
            channels
                .iter()
                .map(|(name, units, values)| Arc::new(LogChannel::new(name, units, 2, values, false)))
                .collect(),
            Arc::new(LogChannel::new("Time", "s", 3, &time, true)),
        )
    }

    fn filter(channel: &str, comparison: FilterComparison, low: f64, high: f64) -> LogFilter {
        LogFilter::new("test", channel, comparison)
            .with_low(low)
            .with_high(high)
    }

    fn suggested_on(document: &LogDocument, channel: &str) -> Vec<LogFilter> {
        SampleFilter::suggest(document)
            .into_iter()
            .filter(|f| f.channel() == channel)
            .collect()
    }

    #[test]
    fn comparisons_behave_as_written() {
        // Each row is (comparison, low, high, value, expected).
        let rows = [
            (FilterComparison::Above, 100.0, 0.0, 101.0, true),
            (FilterComparison::Above, 100.0, 0.0, 100.0, false),
            (FilterComparison::AboveOrEqual, 100.0, 0.0, 100.0, true),
            (FilterComparison::Below, 100.0, 0.0, 99.0, true),
            (FilterComparison::BelowOrEqual, 100.0, 0.0, 100.0, true),
            (FilterComparison::Between, 10.0, 20.0, 15.0, true),
            (FilterComparison::Between, 10.0, 20.0, 25.0, false),
            // Bounds given backwards.
            (FilterComparison::Between, 20.0, 10.0, 15.0, true),
            (FilterComparison::Outside, 10.0, 20.0, 25.0, true),
            (FilterComparison::Outside, 10.0, 20.0, 15.0, false),
        ];

        for (comparison, low, high, value, expected) in rows {
            assert_eq!(
                filter("X", comparison, low, high).accepts(value),
                expected,
                "{comparison:?} {low} {high} at {value}"
            );
        }
    }

    #[test]
    fn a_missing_reading_never_satisfies_a_filter() {
        // A sample that cannot be evaluated must not be quietly kept.
        assert!(!filter("X", FilterComparison::Above, 0.0, 0.0).accepts(f64::NAN));
        assert!(!filter("X", FilterComparison::Below, 1000.0, 0.0).accepts(f64::NAN));
    }

    #[test]
    fn only_samples_meeting_every_enabled_filter_survive() {
        let document = log(&[
            ("CLT", "", &[120.0, 150.0, 170.0, 180.0, 185.0]),
            ("RPM", "", &[0.0, 800.0, 2500.0, 3000.0, 400.0]),
        ]);

        let mask = SampleFilter::build(
            &document,
            &[
                filter("CLT", FilterComparison::AboveOrEqual, 160.0, 0.0),
                filter("RPM", FilterComparison::AboveOrEqual, 500.0, 0.0),
            ],
        );

        // Index 2 and 3 are the only ones warm AND running.
        assert!(mask.filters_applied());
        assert_eq!(mask.pass_count(), 2);
        assert_eq!(mask.accepted(), &[false, false, true, true, false]);
    }

    #[test]
    fn disabled_filters_are_ignored() {
        let document = log(&[("CLT", "", &[100.0, 120.0, 140.0])]);

        let mask = SampleFilter::build(
            &document,
            &[filter("CLT", FilterComparison::AboveOrEqual, 160.0, 0.0).with_enabled(false)],
        );

        assert!(!mask.filters_applied());
        assert_eq!(mask.pass_count(), 3);
        assert!(mask.is_accepted(0));
    }

    #[test]
    fn a_filter_naming_an_absent_channel_is_reported_not_applied() {
        // Silently rejecting every sample would look like a broken log.
        let document = log(&[("CLT", "", &[170.0, 175.0, 180.0])]);

        let mask = SampleFilter::build(&document, &[filter("EGT", FilterComparison::Above, 100.0, 0.0)]);

        assert_eq!(mask.unknown_channels(), ["EGT"]);
        assert!(!mask.filters_applied());
        assert_eq!(mask.pass_count(), 3);
    }

    #[test]
    fn no_filters_means_every_sample_counts() {
        let document = log(&[("CLT", "", &[1.0, 2.0, 3.0])]);

        let mask = SampleFilter::build(&document, &[]);

        assert_eq!(mask.pass_count(), 3);
        assert!(mask.is_accepted(0) && mask.is_accepted(1) && mask.is_accepted(2));
    }

    #[test]
    fn describe_reads_back_as_the_condition() {
        assert_eq!(
            filter("CLT", FilterComparison::AboveOrEqual, 160.0, 0.0).describe(),
            "CLT ≥ 160"
        );
        assert_eq!(
            filter("AFR", FilterComparison::Between, 9.0, 20.0).describe(),
            "AFR 9…20"
        );
        assert_eq!(filter("TPS", FilterComparison::Above, 1.5, 0.0).describe(), "TPS > 1.5");
    }

    #[test]
    fn suggested_filters_match_the_channels_the_log_has() {
        let document = log(&[("CLT", "", &[100.0, 150.0, 180.0]), ("RPM", "", &[0.0, 900.0, 2000.0])]);

        let suggested = SampleFilter::suggest(&document);

        assert!(suggested.iter().any(|f| f.channel() == "CLT"));
        assert!(suggested.iter().any(|f| f.channel() == "RPM"));
        assert!(!suggested.iter().any(|f| f.channel() == "TPS"));
        // Suggestions arrive switched off; the user opts in.
        assert!(suggested.iter().all(|f| !f.enabled()));
    }

    #[test]
    fn suggested_filters_come_from_the_log_rather_than_from_a_profile() {
        // Nothing is seeded. A log with different channels gets different suggestions, and they
        // arrive switched off either way.
        let document = log(&[
            ("CLT", "F", &[70.0, 120.0, 180.0]),
            ("RPM", "rpm", &[0.0, 2000.0, 4000.0]),
        ]);

        let suggested = SampleFilter::suggest(&document);

        assert!(!suggested.is_empty());
        assert!(suggested.iter().all(|f| !f.enabled()));
        assert!(
            suggested
                .iter()
                .all(|f| document.channels().iter().any(|c| c.name() == f.channel()))
        );
    }

    #[test]
    fn an_obd2_log_gets_filters_too() {
        // It got none at all: its channels are "Coolant" and "Throttle Position" and the
        // suggestions looked for "CLT" and "TPS".
        let document = log(&[
            ("RPM", "rpm", &[800.0, 3000.0, 5000.0]),
            ("Coolant", "°C", &[40.0, 70.0, 90.0]),
            ("Throttle Position", "%", &[0.0, 30.0, 80.0]),
        ]);

        let names: Vec<String> = SampleFilter::suggest(&document)
            .iter()
            .map(|f| f.name().to_string())
            .collect();

        assert!(names.iter().any(|n| n == "Engine running"));
        assert!(names.iter().any(|n| n == "Up to temperature"));
        assert!(names.iter().any(|n| n == "Off idle"));
    }

    #[test]
    fn a_mixture_range_follows_the_scale_it_is_on() {
        // Nine to twenty on a lambda channel accepts everything and filters nothing, which is
        // worse than no filter because it looks like one.
        let lambda = log(&[("Lambda", "", &[0.8, 1.0, 1.2])]);
        let on_lambda = suggested_on(&lambda, "Lambda");
        assert_eq!(on_lambda.len(), 1);
        assert_eq!(on_lambda[0].low(), 0.6);
        assert_eq!(on_lambda[0].high(), 1.4);

        let afr = log(&[("AFR", "", &[12.0, 14.7, 16.0])]);
        let on_afr = suggested_on(&afr, "AFR");
        assert_eq!(on_afr.len(), 1);
        assert_eq!(on_afr[0].low(), 9.0);
        assert_eq!(on_afr[0].high(), 20.0);
    }

    #[test]
    fn a_cut_is_offered_as_a_filter_of_its_own() {
        // While the ECU is cutting fuel there is no fuelling to judge. Active in 70 samples of the
        // 6,466 in the MaxxECU log, few, and every one of them meaningless.
        let document = log(&[("Fuel cut", "%", &[0.0, 0.0, 100.0])]);

        let cut: Vec<LogFilter> = SampleFilter::suggest(&document)
            .into_iter()
            .filter(|f| f.name() == "Not cutting fuel")
            .collect();

        assert_eq!(cut.len(), 1);
        assert_eq!(cut[0].comparison(), FilterComparison::BelowOrEqual);
        assert_eq!(cut[0].low(), 0.0);
    }

    #[test]
    fn every_suggestion_arrives_switched_off() {
        // They are offered, not applied. Silently throwing away samples would change every table on
        // screen without anyone asking.
        let document = log(&[
            ("RPM", "rpm", &[800.0, 3000.0, 5000.0]),
            ("CLT", "°C", &[40.0, 70.0, 90.0]),
            ("TPS", "%", &[0.0, 30.0, 80.0]),
        ]);

        assert!(SampleFilter::suggest(&document).iter().all(|f| !f.enabled()));
    }

    #[test]
    fn a_channel_that_never_moves_is_not_suggested() {
        // Identifying it and deciding whether to filter on it are different questions. Looking past
        // a flat coolant reading finds something that is not the coolant.
        let document = log(&[("CLT", "°C", &[90.0, 90.0, 90.0])]);

        assert_eq!(
            channel_roles::find(&document, ChannelRole::Coolant).map(|c| c.name().to_string()),
            Some("CLT".to_string())
        );
        assert!(!SampleFilter::suggest(&document).iter().any(|f| f.channel() == "CLT"));
    }

    #[test]
    fn an_infinite_bound_is_described_as_the_c_sharp_formats_it() {
        // An infinite bound is not a whole number, so it goes through the "0.##" path and reads
        // "Infinity" as .NET's invariant format writes it.
        assert_eq!(
            filter("X", FilterComparison::Above, f64::INFINITY, 0.0).describe(),
            "X > Infinity"
        );
    }

    #[test]
    fn an_undefined_comparison_matches_every_sample() {
        // A number the C# enum does not name is accepted from a file and matches everything, as the
        // C#'s default switch arm does.
        let f = LogFilter::new("x", "X", FilterComparison::Other(9)).with_low(1.0);
        assert!(f.accepts(-5.0));
        assert_eq!(f.describe(), "X");
    }

    #[test]
    fn a_nan_bound_fails_every_range_comparison() {
        // Math.Min and Math.Max propagate NaN, so a range with a NaN end accepts nothing.
        let f = filter("X", FilterComparison::Between, f64::NAN, 20.0);
        assert!(!f.accepts(15.0));
    }

    #[test]
    fn two_filters_equal_when_every_property_is_equal_and_nan_equals_nan() {
        // The C# record compares NaN bounds as equal (double.Equals), so Remove finds them.
        let a = filter("X", FilterComparison::Above, f64::NAN, 0.0);
        let b = filter("X", FilterComparison::Above, f64::NAN, 0.0);
        assert_eq!(a, b);
        assert_ne!(a, b.clone().with_enabled(false));
    }
}
