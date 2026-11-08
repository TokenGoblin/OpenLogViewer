//! Ported from `LogComparison.cs`. Owned by package `w3-analysis`.
//!
//! Two logs, read against each other.
//!
//! The comparison a tuner actually makes: change something, drive it again, and find out what
//! moved. Two things make it harder than subtracting one column from another. The logs do not
//! line up in time, and they do not line up in content either: a firmware update renames
//! channels, an export drops some, and a comparison that silently matched fifteen channels out
//! of two hundred would look like it had worked.
//!
//! So nothing here interpolates one log onto the other's timebase. What is compared instead is
//! the shape of each: the same cells of the same table, which is what the histogram was
//! already for.

use std::collections::HashSet;

use super::histogram_table::{HistogramInputs, HistogramTable};
use crate::model::{Array2, LogDocument};
use crate::{Error, Result};

/// What two logs have in common, and what only one of them has.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ChannelOverlap {
    /// Channels in both, by the name they share.
    pub shared: Vec<String>,
    /// Channels the first log has and the second does not.
    pub only_in_first: Vec<String>,
    /// And the other way round.
    pub only_in_second: Vec<String>,
}

impl ChannelOverlap {
    pub fn anything_shared(&self) -> bool {
        !self.shared.is_empty()
    }

    /// A sentence about how well the two match, because a comparison of two logs with almost
    /// nothing in common is worth being warned about rather than discovering by finding an
    /// empty plot.
    pub fn summary(&self) -> String {
        if self.shared.is_empty() {
            return "These two logs share no channel names at all, so there is nothing to compare. \
                    They are probably from different firmware, or one is an export with renamed \
                    columns."
                .to_string();
        }

        if self.only_in_first.is_empty() && self.only_in_second.is_empty() {
            return format!("Both logs carry the same {} channels.", self.shared.len());
        }

        let mut text = format!("{} channels in both", self.shared.len());
        if !self.only_in_first.is_empty() {
            text.push_str(&format!(", {} only in the first", self.only_in_first.len()));
        }
        if !self.only_in_second.is_empty() {
            text.push_str(&format!(", {} only in the second", self.only_in_second.len()));
        }
        text.push('.');
        text
    }
}

/// What a difference table adds up to.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ComparisonSummary {
    /// How many cells both runs visited with enough samples.
    pub cells: usize,
    /// The average change across them: whether the whole table moved.
    pub mean: f64,
    /// The biggest single change, signed.
    pub largest: f64,
    /// Where that was, on the first table's axes.
    pub at_column: f64,
    /// Likewise.
    pub at_row: f64,
}

impl ComparisonSummary {
    pub fn any(&self) -> bool {
        self.cells > 0
    }
}

/// An ordinal, case-insensitive ordering of names, as `StringComparer.OrdinalIgnoreCase` does
/// it: each UTF-16 unit upper-cased invariantly, then compared by value.
fn ordinal_ignore_case_key(s: &str) -> Vec<u16> {
    // Simple case mapping only: a character whose upper case is several characters (the
    // German sharp s) is left alone, as .NET's ordinal comparison leaves it.
    s.chars()
        .flat_map(|c| {
            let mut upper = c.to_uppercase();
            let mapped = match (upper.next(), upper.next()) {
                (Some(u), None) => u,
                _ => c,
            };
            let mut buf = [0u16; 2];
            mapped.encode_utf16(&mut buf).to_vec()
        })
        .collect()
}

fn ordered(mut names: Vec<String>) -> Vec<String> {
    names.sort_by_key(|n| ordinal_ignore_case_key(n));
    names
}

/// Which channels the two share, matched by name.
///
/// By name and not by position, because position means nothing between two logs: a firmware
/// that adds one channel shifts every column after it, and a comparison matched by column would
/// compare coolant against oil pressure without noticing.
pub fn compare(first: &LogDocument, second: &LogDocument) -> ChannelOverlap {
    // `HashSet<string>(OrdinalIgnoreCase)` keeps the first spelling of each name.
    fn names(doc: &LogDocument) -> Vec<String> {
        let mut seen = HashSet::new();
        let mut out = Vec::new();
        for c in doc.channels() {
            if seen.insert(ordinal_ignore_case_key(c.name())) {
                out.push(c.name().to_string());
            }
        }
        out
    }

    let a = names(first);
    let b = names(second);
    let in_a: HashSet<Vec<u16>> = a.iter().map(|n| ordinal_ignore_case_key(n)).collect();
    let in_b: HashSet<Vec<u16>> = b.iter().map(|n| ordinal_ignore_case_key(n)).collect();

    ChannelOverlap {
        shared: ordered(
            a.iter()
                .filter(|n| in_b.contains(&ordinal_ignore_case_key(n)))
                .cloned()
                .collect(),
        ),
        only_in_first: ordered(
            a.iter()
                .filter(|n| !in_b.contains(&ordinal_ignore_case_key(n)))
                .cloned()
                .collect(),
        ),
        only_in_second: ordered(
            b.iter()
                .filter(|n| !in_a.contains(&ordinal_ignore_case_key(n)))
                .cloned()
                .collect(),
        ),
    }
}

/// One table subtracted from another, cell by cell.
///
/// The comparison that means something. Two logs cannot be lined up in time, but they can be
/// binned onto the same axes and read cell for cell.
///
/// A cell is only reported where both tables actually have data. Treating an empty cell as
/// zero would invent a difference the size of the whole reading everywhere the second run did
/// not go.
///
/// Returns a table on the first one's axes, holding the difference where both have data and
/// nothing where either does not. Counts are the smaller of the two, being how much evidence
/// the difference actually rests on.
///
/// # Errors
/// [`Error::Argument`] when the two tables are not the same size.
pub fn difference(first: &HistogramTable, second: &HistogramTable) -> Result<HistogramTable> {
    if first.columns != second.columns || first.rows != second.rows {
        return Err(Error::Argument(
            "Two tables can only be subtracted on identical axes. Bin both onto the same \
             breakpoints first. (Parameter 'second')"
                .to_string(),
        ));
    }

    let mut values = Array2::<Option<f64>>::new(first.columns, first.rows);
    let mut counts = Array2::<usize>::new(first.columns, first.rows);

    for c in 0..first.columns {
        for r in 0..first.rows {
            let (Some(a), Some(b)) = (first.values[(c, r)], second.values[(c, r)]) else {
                continue;
            };

            values[(c, r)] = Some(a - b);

            // The weaker of the two, because a difference is only as well evidenced as its
            // thinner side.
            counts[(c, r)] = first.counts[(c, r)].min(second.counts[(c, r)]);
        }
    }

    // Built through the same door a VE suggestion uses, so a difference is an ordinary table:
    // it shades by sample count, traces back to the log and exports like any other.
    HistogramTable::from_cells(
        HistogramInputs::new(&first.x, &first.y, &first.z),
        &first.column_centers,
        &first.row_centers,
        &values,
        &counts,
        first.first_sample,
        first.last_sample,
        Some(&format!("{} — change", first.z.name())),
        Some(first.z.digits()),
    )
}

/// How much of the two tables can actually be compared: `(both, only_first, only_second)`.
///
/// The number that says whether a comparison is worth reading. Two drives that overlap in a
/// tenth of the table have not been compared in any useful sense.
pub fn coverage(first: &HistogramTable, second: &HistogramTable) -> (usize, usize, usize) {
    let (mut both, mut only_a, mut only_b) = (0, 0, 0);

    for c in 0..first.columns.min(second.columns) {
        for r in 0..first.rows.min(second.rows) {
            let a = first.values[(c, r)].is_some();
            let b = second.values[(c, r)].is_some();

            if a && b {
                both += 1;
            } else if a {
                only_a += 1;
            } else if b {
                only_b += 1;
            }
        }
    }

    (both, only_a, only_b)
}

/// What changed, in one line, from a difference table.
///
/// The average tells you whether the whole table moved, and the largest single change tells you
/// whether something moved a lot somewhere specific. Those are different findings and a
/// coloured table shows them equally. `minimum_samples` is the C#'s default of 1 when not given.
pub fn summarise(difference: &HistogramTable, minimum_samples: usize) -> ComparisonSummary {
    let mut total = 0.0;
    let mut cells = 0usize;
    let mut biggest = 0.0f64;
    let mut at: Option<(usize, usize)> = None;

    for c in 0..difference.columns {
        for r in 0..difference.rows {
            let Some(value) = difference.values[(c, r)] else {
                continue;
            };
            if difference.counts[(c, r)] < minimum_samples {
                continue;
            }

            total += value;
            cells += 1;

            if value.abs() <= biggest.abs() {
                continue;
            }

            biggest = value;
            at = Some((c, r));
        }
    }

    ComparisonSummary {
        cells,
        mean: if cells > 0 { total / cells as f64 } else { f64::NAN },
        largest: if cells > 0 { biggest } else { f64::NAN },
        at_column: at.map_or(f64::NAN, |(c, _)| difference.column_centers[c]),
        at_row: at.map_or(f64::NAN, |(_, r)| difference.row_centers[r]),
    }
}

#[cfg(test)]
mod tests {
    //! Reading one log against another. The loop this exists for: change something, drive it
    //! again, find out what moved. The logs line up neither in time nor in content, and the
    //! failures from both are quiet.

    use std::sync::Arc;

    use super::*;
    use crate::analysis::histogram_table::HistogramStatistic;
    use crate::model::LogChannel;

    fn log(name: &str, channels: &[(&str, &[f32])]) -> LogDocument {
        let n = channels[0].1.len();
        let time: Vec<f64> = (0..n).map(|i| i as f64).collect();
        LogDocument::new(
            name,
            "test",
            channels
                .iter()
                .map(|(c, v)| Arc::new(LogChannel::adopt(c, "", 1, v.to_vec())))
                .collect(),
            Arc::new(LogChannel::new("Time", "s", 3, &time, true)),
        )
    }

    fn adopt(name: &str, units: &str, digits: i32, v: &[f32]) -> Arc<LogChannel> {
        Arc::new(LogChannel::adopt(name, units, digits, v.to_vec()))
    }

    fn close(expected: f64, actual: f64) {
        assert!((expected - actual).abs() <= 5e-6, "{expected} vs {actual}");
    }

    fn strings(v: &[&str]) -> Vec<String> {
        v.iter().map(|s| s.to_string()).collect()
    }

    // ----- which channels line up ------------------------------------------------

    #[test]
    fn channels_are_matched_by_name_and_the_odd_ones_out_are_named() {
        let before = log(
            "a",
            &[
                ("RPM", &[1000.0, 2000.0]),
                ("MAP", &[50.0, 90.0]),
                ("Knock", &[0.0, 0.0]),
            ],
        );
        let after = log(
            "b",
            &[
                ("RPM", &[1000.0, 2000.0]),
                ("MAP", &[50.0, 90.0]),
                ("AFR", &[14.0, 12.0]),
            ],
        );

        let overlap = compare(&before, &after);

        assert_eq!(strings(&["MAP", "RPM"]), overlap.shared);
        assert_eq!(strings(&["Knock"]), overlap.only_in_first);
        assert_eq!(strings(&["AFR"]), overlap.only_in_second);
        assert!(overlap.anything_shared());
    }

    /// By name, not by position. A firmware update that inserts one channel shifts every column
    /// after it, and matching by index would then compare coolant against oil pressure without a
    /// word.
    #[test]
    fn an_inserted_channel_does_not_shift_the_matching() {
        let before = log("a", &[("RPM", &[1000.0]), ("CLT", &[180.0])]);
        let after = log("b", &[("RPM", &[1000.0]), ("Oil Pressure", &[45.0]), ("CLT", &[190.0])]);

        let overlap = compare(&before, &after);

        assert_eq!(strings(&["CLT", "RPM"]), overlap.shared);
        assert_eq!(strings(&["Oil Pressure"]), overlap.only_in_second);
    }

    #[test]
    fn case_does_not_stop_two_channels_matching() {
        let before = log("a", &[("rpm", &[1000.0])]);
        let after = log("b", &[("RPM", &[1000.0])]);

        assert_eq!(1, compare(&before, &after).shared.len());
    }

    /// Two logs with nothing in common is reported rather than left to be discovered by finding
    /// an empty plot.
    #[test]
    fn two_unrelated_logs_say_so_rather_than_comparing_nothing_quietly() {
        let before = log("a", &[("RPM", &[1000.0])]);
        let after = log("b", &[("EngineSpeed", &[1000.0])]);

        let overlap = compare(&before, &after);

        assert!(overlap.shared.is_empty());
        assert!(!overlap.anything_shared());
        assert!(overlap.summary().to_lowercase().contains("no channel names"));
    }

    #[test]
    fn two_identical_logs_say_so_the_short_way() {
        let l = log("a", &[("RPM", &[1000.0]), ("MAP", &[50.0])]);

        assert!(compare(&l, &l).summary().contains("same 2 channels"));
    }

    // ----- the difference table ---------------------------------------------------

    /// Two runs binned onto the same axes and subtracted. The second uniformly `offset` units
    /// richer, so every shared cell reads -offset.
    fn two_runs(offset: f32, skip_from: usize) -> (HistogramTable, HistogramTable) {
        let rpm = [1000.0f32, 1000.0, 3000.0, 3000.0];
        let map = [50.0f32, 150.0, 50.0, 150.0];
        let afr = [14.0f32, 13.0, 12.0, 11.0];

        let x = adopt("RPM", "rpm", 0, &rpm);
        let y = adopt("MAP", "kPa", 0, &map);

        let a = HistogramTable::build(
            HistogramInputs::new(&x, &y, &adopt("AFR", "", 1, &afr)),
            2,
            2,
            0,
            rpm.len() as isize - 1,
            HistogramStatistic::Mean,
        )
        .unwrap();

        // The second run, shifted, and optionally missing its last cells so the "one side has
        // no data" case can be exercised.
        let shifted: Vec<f32> = afr
            .iter()
            .enumerate()
            .map(|(i, v)| if i >= skip_from { f32::NAN } else { v + offset })
            .collect();

        let b = HistogramTable::build(
            HistogramInputs::new(&x, &y, &adopt("AFR", "", 1, &shifted)),
            2,
            2,
            0,
            rpm.len() as isize - 1,
            HistogramStatistic::Mean,
        )
        .unwrap();

        (a, b)
    }

    #[test]
    fn subtracting_two_tables_gives_the_change_cell_by_cell() {
        let (before, after) = two_runs(1.0, usize::MAX);

        let change = difference(&after, &before).unwrap();

        for c in 0..change.columns {
            for r in 0..change.rows {
                close(1.0, change.values[(c, r)].unwrap());
            }
        }
    }

    /// The subtraction is the right way round: first minus second, so "after minus before" reads
    /// as the change that was made. Backwards it would report every improvement as a regression.
    #[test]
    fn the_difference_is_first_minus_second() {
        let (before, after) = two_runs(2.0, usize::MAX);

        close(2.0, difference(&after, &before).unwrap().values[(0, 0)].unwrap());
        close(-2.0, difference(&before, &after).unwrap().values[(0, 0)].unwrap());
    }

    /// A cell only one run visited is left empty. Treating the other side as zero would invent a
    /// difference the size of the whole reading everywhere the second drive did not go.
    #[test]
    fn a_cell_only_one_run_visited_is_left_empty_rather_than_treated_as_zero() {
        let (before, after) = two_runs(1.0, 2);

        let change = difference(&before, &after).unwrap();

        let mut filled = 0;
        for c in 0..change.columns {
            for r in 0..change.rows {
                if change.values[(c, r)].is_some() {
                    filled += 1;
                }
            }
        }

        assert!(filled > 0, "the cells both runs visited should still be compared");
        assert!(
            filled < change.columns * change.rows,
            "the unvisited ones should be empty"
        );
    }

    /// A difference rests on the thinner of its two sides, so that is the count carried.
    #[test]
    fn the_count_is_the_weaker_of_the_two_sides() {
        let (before, after) = two_runs(1.0, usize::MAX);

        let change = difference(&before, &after).unwrap();

        for c in 0..change.columns {
            for r in 0..change.rows {
                assert_eq!(before.counts[(c, r)].min(after.counts[(c, r)]), change.counts[(c, r)]);
            }
        }
    }

    #[test]
    fn tables_on_different_axes_cannot_be_subtracted() {
        let (before, _) = two_runs(1.0, usize::MAX);

        let (x, y, z) = (
            adopt("RPM", "rpm", 0, &[1000.0, 2000.0, 3000.0]),
            adopt("MAP", "kPa", 0, &[50.0, 100.0, 150.0]),
            adopt("AFR", "", 1, &[14.0, 13.0, 12.0]),
        );
        let wider =
            HistogramTable::build(HistogramInputs::new(&x, &y, &z), 3, 3, 0, 2, HistogramStatistic::Mean).unwrap();

        let Err(Error::Argument(message)) = difference(&before, &wider) else {
            panic!("expected an argument error");
        };

        assert!(message.to_lowercase().contains("identical axes"));
    }

    /// A difference is an ordinary table, so everything downstream works on it.
    #[test]
    fn a_difference_is_an_ordinary_table() {
        let (before, after) = two_runs(1.0, usize::MAX);

        let change = difference(&after, &before).unwrap();

        assert_eq!(before.columns, change.columns);
        assert_eq!(before.column_centers, change.column_centers);
        assert_eq!(before.row_centers, change.row_centers);
        assert!(
            change
                .display_name
                .as_deref()
                .unwrap()
                .to_lowercase()
                .contains("change")
        );
    }

    // ----- how much of it can actually be compared ---------------------------------

    /// Two drives overlapping in a tenth of the table have not really been compared, and the
    /// sparse result deserves an explanation rather than being left to look like a bug.
    #[test]
    fn coverage_says_how_much_the_two_runs_actually_share() {
        let (before, after) = two_runs(1.0, 2);

        let (both, only_first, only_second) = coverage(&before, &after);

        assert!(both > 0);
        assert!(only_first > 0, "the first run went somewhere the second did not");
        assert_eq!(0, only_second);
    }

    // ----- what changed, in one line -------------------------------------------------

    #[test]
    fn the_summary_gives_the_average_move_and_the_biggest_one() {
        let (before, after) = two_runs(1.0, usize::MAX);

        let s = summarise(&difference(&after, &before).unwrap(), 1);

        assert!(s.any());
        assert_eq!(4, s.cells);
        close(1.0, s.mean);
        close(1.0, s.largest);
    }

    /// The average and the largest are different findings. A table that moved a little everywhere
    /// and one that moved a lot in one corner are not the same result.
    #[test]
    fn one_big_move_is_reported_separately_from_the_average() {
        let rpm = [1000.0f32, 1000.0, 3000.0, 3000.0];
        let map = [50.0f32, 150.0, 50.0, 150.0];

        let x = adopt("RPM", "rpm", 0, &rpm);
        let y = adopt("MAP", "kPa", 0, &map);

        let before = HistogramTable::build(
            HistogramInputs::new(&x, &y, &adopt("AFR", "", 1, &[14.0, 14.0, 14.0, 14.0])),
            2,
            2,
            0,
            3,
            HistogramStatistic::Mean,
        )
        .unwrap();

        // Three cells unchanged, one moved a long way.
        let after = HistogramTable::build(
            HistogramInputs::new(&x, &y, &adopt("AFR", "", 1, &[14.0, 14.0, 14.0, 10.0])),
            2,
            2,
            0,
            3,
            HistogramStatistic::Mean,
        )
        .unwrap();

        let change = difference(&after, &before).unwrap();
        let s = summarise(&change, 1);

        close(-1.0, s.mean);
        close(-4.0, s.largest);

        // Reported at the bin's centre, not at the sample that landed in it. Asserted against
        // the table's own axes so this says "the top-right cell" rather than restating a number.
        close(*change.column_centers.last().unwrap(), s.at_column);
        close(*change.row_centers.last().unwrap(), s.at_row);
    }

    /// A cell resting on one sample is not evidence of a change, and the threshold is what stops
    /// the biggest reported move being the noisiest one.
    #[test]
    fn thin_cells_can_be_held_out_of_the_summary() {
        let (before, after) = two_runs(1.0, usize::MAX);

        assert!(!summarise(&difference(&after, &before).unwrap(), 99).any());
    }

    #[test]
    fn nothing_in_common_summarises_to_nothing_rather_than_zero() {
        let (before, after) = two_runs(1.0, 0);

        let s = summarise(&difference(&before, &after).unwrap(), 1);

        assert!(!s.any());
        assert!(s.mean.is_nan());
    }
}
