//! Ported from `MathChannel.cs`. Owned by package `w2-math-channels`.
//!
//! A channel the user defines rather than the logger records: "AFR Error" as AFR minus its
//! target, boost against a target, torque converted to power.

use std::sync::Arc;

use serde::{Deserialize, Serialize};

use crate::base::math_expression::MathExpression;
use crate::model::IgnoreCaseMap;
use crate::model::LogChannel;
use crate::model::LogDocument;

fn default_digits() -> i32 {
    2
}

fn default_enabled() -> bool {
    true
}

/// A definition, held by name and expression rather than by anything log-specific, so one
/// definition applies to every log that carries the channels it reads.
///
/// Every member has a default, though name and expression are required in practice: a
/// required member missing from the JSON would fail the whole document, so one hand-edited
/// entry with a typo would take every other definition with it. The store validates each
/// entry instead and drops only the one that is unusable.
///
/// The `System.Text.Json` options in the C# read property names without regard to case; the
/// PascalCase aliases keep a hand-edited file working.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MathChannel {
    #[serde(default, alias = "Name", alias = "NAME")]
    pub name: String,

    /// Units for display; purely a label, never used in the arithmetic.
    #[serde(default, alias = "Units", alias = "UNITS")]
    pub units: String,

    /// Decimal places for display.
    #[serde(default = "default_digits", alias = "Digits", alias = "DIGITS")]
    pub digits: i32,

    #[serde(default, alias = "Expression", alias = "EXPRESSION")]
    pub expression: String,

    #[serde(default = "default_enabled", alias = "Enabled", alias = "ENABLED")]
    pub enabled: bool,
}

impl Default for MathChannel {
    fn default() -> Self {
        Self {
            name: String::new(),
            units: String::new(),
            digits: 2,
            expression: String::new(),
            enabled: true,
        }
    }
}

/// Why a definition produced no channel for a particular log.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MathChannelProblem {
    pub name: String,
    pub reason: String,
}

/// Definitions applied to one log: what was built, and what was not.
#[derive(Clone, Debug)]
pub struct MathChannelResult {
    pub channels: Vec<Arc<LogChannel>>,
    pub problems: Vec<MathChannelProblem>,
}

/// Evaluates each enabled definition over a log.
///
/// Definitions are applied in order and each successful one joins the pool the rest can read,
/// so a calculated channel may build on an earlier one. A definition that cannot be evaluated
/// is reported rather than failed: one broken entry must not stop a log opening, and the user
/// needs to be told which it was.
pub fn build<'a>(document: &LogDocument, definitions: impl IntoIterator<Item = &'a MathChannel>) -> MathChannelResult {
    let mut built: Vec<Arc<LogChannel>> = Vec::new();
    let mut problems = Vec::new();

    // Names resolve against the log first, then anything already calculated.
    let mut available: IgnoreCaseMap<Arc<LogChannel>> = IgnoreCaseMap::new();
    for channel in document.channels() {
        available.insert(channel.name(), channel.clone());
    }
    available.insert(document.time().name(), document.time().clone());

    for definition in definitions {
        if !definition.enabled {
            continue;
        }

        // C# `Trim()` trims Unicode white space, as Rust's does.
        let name = definition.name.trim();
        if name.is_empty() {
            problems.push(MathChannelProblem {
                name: definition.name.clone(),
                reason: "The channel needs a name.".to_string(),
            });
            continue;
        }

        if available.contains_key(name) {
            problems.push(MathChannelProblem {
                name: name.to_string(),
                reason: "This log already has a channel with that name.".to_string(),
            });
            continue;
        }

        let expression = match MathExpression::try_parse(&definition.expression, available.keys()) {
            Ok(e) => e,
            Err(error) => {
                problems.push(MathChannelProblem {
                    name: name.to_string(),
                    reason: if error.is_empty() {
                        "The expression could not be read.".to_string()
                    } else {
                        error
                    },
                });
                continue;
            }
        };

        let channel = Arc::new(evaluate(
            name,
            definition,
            &expression,
            &available,
            document.sample_count(),
        ));
        built.push(channel.clone());
        available.insert(name, channel);
    }

    MathChannelResult {
        channels: built,
        problems,
    }
}

fn evaluate(
    name: &str,
    definition: &MathChannel,
    expression: &MathExpression,
    available: &IgnoreCaseMap<Arc<LogChannel>>,
    sample_count: usize,
) -> LogChannel {
    let sources: Vec<&Arc<LogChannel>> = expression
        .references()
        .iter()
        .map(|r| available.get(r).expect("a reference the parser took from the pool"))
        .collect();

    let mut values = vec![0.0f32; sample_count];
    let mut inputs = vec![0.0f64; sources.len()];

    for (i, slot) in values.iter_mut().enumerate() {
        for (input, source) in inputs.iter_mut().zip(&sources) {
            *input = source.at(i);
        }

        let result = expression.evaluate(&inputs);

        // A division by zero gives an infinity, which would take the channel's range with it
        // and flatten every real value against the axis. It is "could not be computed here",
        // which is what NaN already means.
        *slot = if result.is_finite() { result as f32 } else { f32::NAN };
    }

    LogChannel::adopt(name, &definition.units, definition.digits, values)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::units::UnitSystem;

    fn log() -> LogDocument {
        LogDocument::new(
            "x",
            "test",
            vec![
                Arc::new(LogChannel::new(
                    "RPM",
                    "RPM",
                    0,
                    &[800.0, 2000.0, 4000.0, 6000.0],
                    false,
                )),
                Arc::new(LogChannel::new("AFR", "AFR", 2, &[13.0, 14.7, 12.0, f64::NAN], false)),
                Arc::new(LogChannel::new(
                    "AFR Target 1",
                    "AFR",
                    2,
                    &[14.7, 14.7, 12.5, 12.5],
                    false,
                )),
            ],
            Arc::new(LogChannel::new("Time", "s", 3, &[0.0, 0.1, 0.2, 0.3], true)),
        )
    }

    fn definition(name: &str, expression: &str) -> MathChannel {
        MathChannel {
            name: name.to_string(),
            expression: expression.to_string(),
            units: "AFR".to_string(),
            digits: 2,
            enabled: true,
        }
    }

    fn near(a: f64, b: f64, places: i32) {
        assert!((a - b).abs() < 0.5 * 10f64.powi(-places), "{a} vs {b}");
    }

    #[test]
    fn a_definition_becomes_a_channel_over_the_whole_log() {
        let result = build(&log(), &[definition("AFR Error", "AFR - AFR Target 1")]);

        assert_eq!(result.channels.len(), 1);
        let channel = &result.channels[0];

        assert!(result.problems.is_empty());
        assert_eq!(channel.name(), "AFR Error");
        assert_eq!(channel.len(), 4);
        near(channel.at(0), -1.7, 4);
        near(channel.at(1), 0.0, 4);
        near(channel.at(2), -0.5, 4);
    }

    #[test]
    fn a_missing_reading_leaves_a_gap_rather_than_a_number() {
        // The plot draws NaN as a break. Filling it would invent data.
        let result = build(&log(), &[definition("AFR Error", "AFR - AFR Target 1")]);

        assert!(result.channels[0].at(3).is_nan());
    }

    #[test]
    fn the_time_base_can_be_read_like_any_other_channel() {
        let result = build(&log(), &[definition("Half", "Time / 2")]);

        near(result.channels[0].at(1), 0.05, 4);
    }

    #[test]
    fn a_calculated_channel_can_build_on_an_earlier_one() {
        let result = build(
            &log(),
            &[
                definition("AFR Error", "AFR - AFR Target 1"),
                definition("Error Percent", "AFR Error / AFR Target 1 * 100"),
            ],
        );

        assert_eq!(result.channels.len(), 2);
        near(result.channels[1].at(0), -1.7 / 14.7 * 100.0, 3);
    }

    #[test]
    fn a_definition_cannot_read_one_declared_after_it() {
        // Order is the only thing keeping this from being a cycle.
        let result = build(
            &log(),
            &[definition("Second", "First * 2"), definition("First", "RPM / 2")],
        );

        assert_eq!(result.channels.len(), 1);
        assert_eq!(result.channels[0].name(), "First");
        assert_eq!(result.problems.len(), 1);
        assert!(result.problems[0].reason.contains("First"));
    }

    #[test]
    fn a_definition_that_names_an_absent_channel_is_reported_not_thrown() {
        // A definition kept from another car must not stop this log opening.
        let result = build(&log(), &[definition("Power", "Torque * RPM / 5252")]);

        assert!(result.channels.is_empty());
        assert_eq!(result.problems.len(), 1);
        assert_eq!(result.problems[0].name, "Power");
        assert!(result.problems[0].reason.contains("Torque"));
    }

    #[test]
    fn one_broken_definition_does_not_stop_the_others() {
        let result = build(&log(), &[definition("Broken", "RPM +"), definition("Fine", "RPM * 2")]);

        assert_eq!(result.channels.len(), 1);
        assert_eq!(result.channels[0].name(), "Fine");
        assert_eq!(result.problems.len(), 1);
        assert_eq!(result.problems[0].name, "Broken");
    }

    #[test]
    fn a_name_already_in_the_log_is_refused() {
        // Two channels with one name are indistinguishable everywhere they are used: the
        // sidebar, the axis pickers, the filters.
        let result = build(&log(), &[definition("RPM", "RPM * 2")]);

        assert!(result.channels.is_empty());
        assert_eq!(result.problems.len(), 1);
        assert!(result.problems[0].reason.contains("already has"));
    }

    #[test]
    fn a_disabled_definition_is_skipped_silently() {
        let mut d = definition("AFR Error", "AFR - AFR Target 1");
        d.enabled = false;
        let result = build(&log(), &[d]);

        assert!(result.channels.is_empty());
        assert!(result.problems.is_empty());
    }

    #[test]
    fn an_infinite_result_becomes_a_gap() {
        // Dividing by a channel that reaches zero would otherwise give the channel an
        // infinite range and flatten every real value against it.
        let doc = LogDocument::new(
            "x",
            "test",
            vec![Arc::new(LogChannel::new("RPM", "RPM", 0, &[0.0, 1000.0], false))],
            Arc::new(LogChannel::new("Time", "s", 3, &[0.0, 0.1], true)),
        );

        let result = build(&doc, &[definition("Inverse", "1000 / RPM")]);

        assert!(result.channels[0].at(0).is_nan());
        near(result.channels[0].at(1), 1.0, 4);
        near(result.channels[0].max(), 1.0, 4);
    }

    #[test]
    fn the_units_and_precision_are_taken_from_the_definition() {
        let result = build(
            &log(),
            &[MathChannel {
                name: "Boost bar".to_string(),
                expression: "RPM / 1000".to_string(),
                units: "bar".to_string(),
                digits: 3,
                enabled: true,
            }],
        );

        assert_eq!(result.channels[0].units(), "bar");
        assert_eq!(result.channels[0].format(2.0, UnitSystem::AsReported), "2.000");
    }
}
