//! Ported from `TuneAxes.cs`: table axes, and tables, read out of an MSQ tune.
//!
//! The C#'s `MsqTune` static class is the free functions here.

use super::safe_xml::{self, XmlElement};
use super::{Array2, IgnoreCaseMap};
use crate::fmt;

/// One axis of an ECU table: the breakpoints the ECU interpolates between.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneAxis {
    pub constant: String,
    pub units: String,
    pub breakpoints: Vec<f64>,
}

impl TuneAxis {
    /// Units fit to put in front of a person, or nothing.
    ///
    /// An INI may declare units as an expression rather than a word, because on
    /// some firmware the axis is not always the same quantity: MS2Extra writes the
    /// load axis as `{ bitStringValue( algorithmUnits , algorithm ) }`, kilopascals
    /// on speed density and per cent on alpha-N. Nothing here evaluates those, so
    /// showing one verbatim puts a line of INI source where a tuner expected "kPa".
    /// Empty is the right answer rather than a guess.
    pub fn plain_units(&self) -> String {
        plain(&self.units)
    }
}

/// Whether a declared unit is a word rather than something to evaluate. The
/// length limit counts UTF-16 units, as the C#'s `Length` does.
pub(crate) fn plain(units: &str) -> String {
    if units.trim().is_empty() {
        return String::new();
    }
    if units.contains(['{', '}', '(', ')']) || units.encode_utf16().count() > 12 {
        String::new()
    } else {
        units.trim().to_string()
    }
}

/// The breakpoints of one table in the tune, so a log can be binned onto the same
/// grid the ECU actually uses.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneAxisSet {
    pub name: String,
    pub x: TuneAxis,
    pub y: TuneAxis,
}

impl TuneAxisSet {
    /// Name and grid size, e.g. "VE table 1  (16×16)".
    pub fn label(&self) -> String {
        format!(
            "{}  ({}×{})",
            self.name,
            self.x.breakpoints.len(),
            self.y.breakpoints.len()
        )
    }
}

/// A table from the tune, values and all: what the ECU was actually running,
/// against which a log can be judged. `values` is `[column, row]`, row 0 lowest.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneTable {
    pub name: String,
    pub x: TuneAxis,
    pub y: TuneAxis,
    pub values: Array2<f64>,
    pub units: String,
}

impl TuneTable {
    pub fn columns(&self) -> usize {
        self.x.breakpoints.len()
    }

    pub fn rows(&self) -> usize {
        self.y.breakpoints.len()
    }

    pub fn label(&self) -> String {
        format!("{}  ({}×{})", self.name, self.columns(), self.rows())
    }

    pub fn axes(&self) -> TuneAxisSet {
        TuneAxisSet {
            name: self.name.clone(),
            x: self.x.clone(),
            y: self.y.clone(),
        }
    }
}

/// Tables worth offering, and the constants holding their axes. MegaSquirt
/// firmwares name these consistently: "f" for fuel, "s" for spark, "a" for the
/// AFR target table.
const KNOWN_TABLES: [(&str, &str, &str, Option<&str>); 11] = [
    ("VE table 1", "frpm_table1", "fmap_table1", Some("veTable1")),
    ("VE table 2", "frpm_table2", "fmap_table2", Some("veTable2")),
    ("VE table 3", "frpm_table3", "fmap_table3", Some("veTable3")),
    ("VE table 4", "frpm_table4", "fmap_table4", Some("veTable4")),
    ("Spark table 1", "srpm_table1", "smap_table1", Some("advanceTable1")),
    ("Spark table 2", "srpm_table2", "smap_table2", Some("advanceTable2")),
    ("Spark table 3", "srpm_table3", "smap_table3", Some("advanceTable3")),
    ("Spark table 4", "srpm_table4", "smap_table4", Some("advanceTable4")),
    ("AFR target 1", "arpm_table1", "amap_table1", Some("afrTable1")),
    ("AFR target 2", "arpm_table2", "amap_table2", Some("afrTable2")),
    (
        "Boost target",
        "boost_ctl_loadtarg_rpm_bins",
        "boost_ctl_pwmtarg_rpm_bins",
        None,
    ),
];

fn is_null_or_white_space(s: Option<&str>) -> bool {
    s.is_none_or(|s| s.trim().is_empty())
}

/// The tune's `<constant>` elements by name, ignoring case; a later duplicate
/// replaces an earlier one's element but keeps its key.
fn constants_by_name(root: &safe_xml::XmlDocument) -> IgnoreCaseMap<&XmlElement> {
    // The document carries a default namespace, so elements are matched on local
    // name rather than by a qualified lookup.
    let mut elements = IgnoreCaseMap::new();
    for element in root.descendants().filter(|e| e.local_name() == "constant") {
        if let Some(name) = element.attribute("name").filter(|n| !n.is_empty()) {
            elements.insert(name, element);
        }
    }
    elements
}

/// Every usable axis pair in the tune. A pair is skipped when either axis is
/// missing or its breakpoints are not usable.
pub fn read_axis_sets(msq_xml: Option<&str>) -> Vec<TuneAxisSet> {
    let Some(constants) = read_constants_from_xml(msq_xml) else {
        return Vec::new();
    };

    let mut sets = Vec::new();
    for (name, x, y, _) in KNOWN_TABLES {
        let Some(x_axis) = constants.get(x) else {
            continue;
        };
        let Some(y_axis) = constants.get(y) else {
            continue;
        };
        sets.push(TuneAxisSet {
            name: name.to_string(),
            x: x_axis.clone(),
            y: y_axis.clone(),
        });
    }
    sets
}

/// Tables whose values were readable as well as their axes: everything needed to
/// compare a log against what the ECU was actually running.
///
/// A table is only returned when its grid matches its axes. A mismatch means one
/// of the two was misread, and a suggestion built on a misaligned grid would be
/// confidently wrong about which cell needs changing.
pub fn read_tables(msq_xml: Option<&str>) -> Vec<TuneTable> {
    if is_null_or_white_space(msq_xml) {
        return Vec::new();
    }
    let Ok(document) = safe_xml::parse(msq_xml.unwrap_or_default()) else {
        return Vec::new();
    };

    let elements = constants_by_name(&document);
    let axes = read_constants(&elements);

    let mut tables = Vec::new();
    for (name, x, y, values) in KNOWN_TABLES {
        let Some(values) = values else {
            continue;
        };
        let (Some(x_axis), Some(y_axis)) = (axes.get(x), axes.get(y)) else {
            continue;
        };
        let Some(grid) = elements.get(values) else {
            continue;
        };
        let Some(cells) = parse_grid(grid, x_axis.breakpoints.len(), y_axis.breakpoints.len()) else {
            continue;
        };
        tables.push(TuneTable {
            name: name.to_string(),
            x: x_axis.clone(),
            y: y_axis.clone(),
            values: cells,
            units: grid.attribute("units").unwrap_or("").to_string(),
        });
    }
    tables
}

/// Reads a rectangular constant into `[column, row]`, row 0 lowest.
///
/// The rows are stored lowest-load first, matching the ascending axis, even
/// though a tuning app shows the table the other way up.
fn parse_grid(element: &XmlElement, columns: usize, rows: usize) -> Option<Array2<f64>> {
    let declared_columns = fmt::parse_i32(element.attribute("cols")?)?;
    let declared_rows = fmt::parse_i32(element.attribute("rows")?)?;

    let value = element.value();
    let tokens: Vec<&str> = value.split_whitespace().collect();
    if tokens.len() as i64 != i64::from(declared_columns) * i64::from(declared_rows) {
        return None;
    }

    // Axes are collapsed and validated before this point, so a grid that no
    // longer matches them cannot be lined up cell for cell.
    if i64::from(declared_columns) != columns as i64 || i64::from(declared_rows) != rows as i64 {
        return None;
    }

    let mut cells = Array2::new(columns, rows);
    for r in 0..rows {
        for c in 0..columns {
            cells[(c, r)] = fmt::parse_f64_inv(tokens[r * columns + c])?;
        }
    }
    Some(cells)
}

/// The tune's single-value settings, by name.
///
/// Needed to read a live connection: the firmware's derived channels are written
/// in terms of the tune as well as the realtime block (duty cycle divides by
/// `nCylinders`, injector timing tests `twoStroke`) and none of those arrive over
/// the wire.
pub fn read_scalars(msq_xml: Option<&str>) -> IgnoreCaseMap<f64> {
    let mut scalars = IgnoreCaseMap::new();
    if is_null_or_white_space(msq_xml) {
        return scalars;
    }
    let Ok(document) = safe_xml::parse(msq_xml.unwrap_or_default()) else {
        return scalars;
    };

    for element in document.descendants().filter(|e| e.local_name() == "constant") {
        let name = element.attribute("name");
        if is_null_or_white_space(name) {
            continue;
        }

        // Tables and arrays declare their shape; a setting is a lone value.
        if element.attribute("cols").is_some() || element.attribute("rows").is_some() {
            continue;
        }

        if let Some(value) = fmt::parse_f64_inv(element.value().trim()) {
            scalars.insert(name.unwrap_or_default(), value);
        }
    }
    scalars
}

/// Parses the one-dimensional numeric constants that can serve as axes.
fn read_constants_from_xml(msq_xml: Option<&str>) -> Option<IgnoreCaseMap<TuneAxis>> {
    if is_null_or_white_space(msq_xml) {
        return None;
    }
    let document = safe_xml::parse(msq_xml.unwrap_or_default()).ok()?;
    let elements = constants_by_name(&document);
    Some(read_constants(&elements))
}

fn read_constants(elements: &IgnoreCaseMap<&XmlElement>) -> IgnoreCaseMap<TuneAxis> {
    let mut constants = IgnoreCaseMap::new();

    for (name, element) in elements.iter() {
        // Axes are single-column arrays; tables and scalars are not axes.
        if element.attribute("cols") != Some("1") {
            continue;
        }

        let Some(breakpoints) = parse_breakpoints(&element.value()) else {
            continue;
        };

        constants.insert(
            name,
            TuneAxis {
                constant: name.to_string(),
                units: element.attribute("units").unwrap_or("").to_string(),
                breakpoints,
            },
        );
    }
    constants
}

/// Turns an axis constant's text into usable breakpoints, or `None` if it is not
/// a usable axis.
///
/// Two quirks have to be handled. Firmwares pad an axis out to the table's full
/// size by repeating the top value, which would create zero-width bins;
/// consecutive duplicates are collapsed. And the "dozen" variants are stored
/// rolled rather than in order (5200, 5700, 6100, 6500, 502, 801 …), so anything
/// that is not ascending after collapsing is rejected outright.
pub(crate) fn parse_breakpoints(text: &str) -> Option<Vec<f64>> {
    let tokens: Vec<&str> = text.split_whitespace().collect();
    if tokens.len() < 3 {
        return None;
    }

    let mut values: Vec<f64> = Vec::with_capacity(tokens.len());
    for token in tokens {
        let v = fmt::parse_f64_inv(token)?;
        if let Some(&last) = values.last()
            && (v - last).abs() < 1e-9
        {
            continue;
        }
        values.push(v);
    }

    if values.len() < 3 {
        return None;
    }

    if values.windows(2).any(|w| w[1] <= w[0]) {
        return None;
    }

    Some(values)
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A tune shaped like the real thing: a default namespace, one-column
    /// constants for the axes, and a rectangular one for the table.
    fn msq(x_axis: &str, y_axis: &str, grid: &str, cols: i32, rows: i32) -> String {
        format!(
            r#"<?xml version="1.0" encoding="ISO-8859-1"?>
<msq xmlns="http://www.msefi.com/:msq">
<page number="0" size="1024">
<constant cols="1" digits="0" name="frpm_table1" rows="{cols}" units="RPM">{x_axis}</constant>
<constant cols="1" digits="0" name="fmap_table1" rows="{rows}" units="kPa">{y_axis}</constant>
<constant cols="{cols}" digits="1" name="veTable1" rows="{rows}" units="%">{grid}</constant>
</page>
</msq>"#
        )
    }

    fn msq3(x_axis: &str, y_axis: &str, grid: &str) -> String {
        msq(x_axis, y_axis, grid, 3, 3)
    }

    fn ve(msq: &str) -> Option<TuneTable> {
        read_tables(Some(msq)).into_iter().find(|t| t.name == "VE table 1")
    }

    const GRID: &str = "\n10 11 12\n20 21 22\n30 31 32\n";

    #[test]
    fn the_grid_and_its_axes_are_read_together() {
        let table = ve(&msq3("1000 2000 3000", "40 60 80", GRID)).unwrap();

        assert_eq!(table.x.breakpoints, [1000.0, 2000.0, 3000.0]);
        assert_eq!(table.y.breakpoints, [40.0, 60.0, 80.0]);
        assert_eq!(table.columns(), 3);
        assert_eq!(table.rows(), 3);
        assert_eq!(table.units, "%");
    }

    #[test]
    fn row_zero_is_the_lowest_load() {
        // The file stores rows lowest-first, matching the ascending axis, even
        // though a tuning app draws the table the other way up. Getting this
        // backwards would suggest changes to the mirror image of the right cells.
        let table = ve(&msq3("1000 2000 3000", "40 60 80", GRID)).unwrap();

        assert_eq!(table.values[(0, 0)], 10.0); // lowest RPM, lowest load
        assert_eq!(table.values[(2, 0)], 12.0); // highest RPM, lowest load
        assert_eq!(table.values[(0, 2)], 30.0); // lowest RPM, highest load
        assert_eq!(table.values[(2, 2)], 32.0);
    }

    #[test]
    fn a_table_whose_grid_does_not_match_its_axes_is_refused() {
        // A mismatch means one of the two was misread. A suggestion built on a
        // misaligned grid would be confidently wrong about which cell to change.
        assert!(ve(&msq("1000 2000 3000", "40 60 80", "1 2 3 4 5 6", 3, 2)).is_none());
    }

    #[test]
    fn a_grid_with_the_wrong_number_of_values_is_refused() {
        assert!(ve(&msq3("1000 2000 3000", "40 60 80", "1 2 3 4 5")).is_none());
    }

    #[test]
    fn a_grid_with_an_unreadable_value_is_refused() {
        assert!(ve(&msq3("1000 2000 3000", "40 60 80", "1 2 3 4 x 6 7 8 9")).is_none());
    }

    #[test]
    fn a_table_with_no_axes_is_not_returned() {
        // Axes that fail the ascending check leave the grid with nothing to line
        // up against.
        assert!(ve(&msq3("3000 2000 1000", "40 60 80", "1 2 3 4 5 6 7 8 9")).is_none());
    }

    #[test]
    fn malformed_xml_is_no_tables_rather_than_a_throw() {
        assert!(read_tables(Some("<msq><not closed")).is_empty());
    }

    #[test]
    fn absent_tune_is_no_tables() {
        assert!(read_tables(None).is_empty());
        assert!(read_tables(Some("")).is_empty());
    }

    #[test]
    fn the_label_names_the_grid_size() {
        let table = ve(&msq3("1000 2000 3000", "40 60 80", "1 2 3 4 5 6 7 8 9")).unwrap();

        assert_eq!(table.label(), "VE table 1  (3×3)");
    }

    #[test]
    fn reading_tables_does_not_disturb_reading_axes() {
        // Both are offered from the same tune; the axis list must still contain
        // tables whose values could not be read.
        let msq = msq3("1000 2000 3000", "40 60 80", "1 2 3 4 5");

        assert!(read_tables(Some(&msq)).is_empty());
        assert!(read_axis_sets(Some(&msq)).iter().any(|s| s.name == "VE table 1"));
    }

    #[test]
    fn padded_axes_collapse_and_scalars_skip_arrays() {
        assert_eq!(
            parse_breakpoints("500 1000 1500 1500 1500"),
            Some(vec![500.0, 1000.0, 1500.0])
        );
        let scalars = read_scalars(Some(
            r#"<msq><constant name="nCylinders">4</constant><constant name="t" cols="1">1 2 3</constant></msq>"#,
        ));
        assert_eq!(scalars.get("NCYLINDERS"), Some(&4.0));
        assert!(!scalars.contains_key("t"));
        assert_eq!(plain("{ bitStringValue(a, b) }"), "");
        assert_eq!(plain(" kPa "), "kPa");
    }
}
