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
