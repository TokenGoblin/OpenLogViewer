//! Ported from `TableEditorReader.cs`: the `[TableEditor]` section.

use super::msq_ini::{self, SymbolSet, default_symbols, grp};
use regex::Regex;
use std::sync::LazyLock;

/// A table the firmware offers for editing: its title, the constants holding its
/// values and breakpoints, and the live channels its axes are indexed by.
///
/// That last part is the valuable one. `xBins = veRpmBins, RPMValue` says the VE
/// table's columns are the `veRpmBins` breakpoints and that the ECU looks the table
/// up by `RPMValue`, so binning a log against that channel reproduces exactly what
/// the controller did, rather than something close to it chosen by whoever set up
/// the analysis.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TableDefinition {
    pub id: String,
    pub title: String,
    /// The name of the same table's three-dimensional view, where it declares
    /// one. A menu may point at either ("VE Table" opens the grid and "VE Table 3D"
    /// the surface), so both names have to lead back to this.
    pub map: String,
    /// Constant holding the values.
    pub values: String,
    /// Constant holding the column breakpoints.
    pub x_bins: String,
    /// Constant holding the row breakpoints.
    pub y_bins: String,
    /// Output channel the ECU indexes the columns by, where the INI says.
    pub x_channel: String,
    pub y_channel: String,
}

impl TableDefinition {
    /// True for the fuel table, which is the one VE Calibration is about.
    pub fn looks_like_ve_table(&self) -> bool {
        let values = self.values.to_uppercase();
        let title = self.title.to_uppercase();
        values.contains("VETABLE") || title.contains("VE TABLE")
    }
}

// The closing quote of the title is left off the pattern: the title group already
// stops at it.
static TABLE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"^\s*table\s*=\s*(?<id>[A-Za-z_]\w*)\s*,\s*(?<map>[A-Za-z_]\w*)?\s*,\s*"(?<title>[^"]*)"#)
        .expect("pattern")
});

static BINS: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^\s*(?<axis>[xyz])Bins\s*=\s*(?<constant>[A-Za-z_]\w*)\s*(?:,\s*(?<channel>[A-Za-z_]\w*))?")
        .expect("pattern")
});

#[derive(Default)]
struct Pending {
    id: String,
    map: String,
    title: String,
    values: String,
    x_bins: String,
    y_bins: String,
    x_channel: String,
    y_channel: String,
}

impl Pending {
    /// A table is only complete once the next one starts or the section ends, since
    /// its parts are listed under it rather than on its own line.
    fn flush(&self, tables: &mut Vec<TableDefinition>) {
        if self.id.is_empty() || self.values.is_empty() || self.x_bins.is_empty() || self.y_bins.is_empty() {
            return;
        }

        tables.push(TableDefinition {
            id: self.id.clone(),
            title: if self.title.is_empty() {
                self.id.clone()
            } else {
                self.title.clone()
            },
            values: self.values.clone(),
            x_bins: self.x_bins.clone(),
            y_bins: self.y_bins.clone(),
            map: self.map.clone(),
            x_channel: self.x_channel.clone(),
            y_channel: self.y_channel.clone(),
        });
    }
}

pub fn read(ini_text: &str, symbols: Option<&SymbolSet>) -> Vec<TableDefinition> {
    let mut tables = Vec::new();
    let mut current = Pending::default();

    for raw in msq_ini::section(ini_text, "TableEditor", symbols.unwrap_or_else(|| default_symbols())) {
        let line = msq_ini::strip(&raw);
        if line.is_empty() {
            continue;
        }

        if let Some(start) = TABLE.captures(&line) {
            current.flush(&mut tables);

            current = Pending {
                id: grp(&start, "id").to_string(),
                map: grp(&start, "map").to_string(),
                title: grp(&start, "title").trim().to_string(),
                ..Pending::default()
            };
            continue;
        }

        let Some(bins) = BINS.captures(&line) else {
            continue;
        };

        let constant = grp(&bins, "constant").to_string();
        let channel = grp(&bins, "channel").to_string();

        match grp(&bins, "axis") {
            "x" => {
                current.x_bins = constant;
                current.x_channel = channel;
            }
            "y" => {
                current.y_bins = constant;
                current.y_channel = channel;
            }
            "z" => current.values = constant,
            _ => {}
        }
    }

    current.flush(&mut tables);

    tables
}

#[cfg(test)]
mod tests {
    use super::*;

    const INI: &str = "[TableEditor]\n\
        table = veTbl, veMap, \"VE Table\"\n\
        xBins = veRpmBins, rpm\n\
        yBins = veMapBins\n\
        zBins = veTable\n\
        table = sparkTbl, , \"\"\n\
        xBins = a\n\
        yBins = b\n\
        zBins = c\n\
        table = incomplete, , \"Nope\"\n\
        xBins = a\n";

    #[test]
    fn tables_are_read_with_their_axes_and_channels() {
        let tables = read(INI, None);

        assert_eq!(tables.len(), 2);
        assert_eq!(tables[0].id, "veTbl");
        assert_eq!(tables[0].map, "veMap");
        assert_eq!(tables[0].title, "VE Table");
        assert_eq!(tables[0].values, "veTable");
        assert_eq!(tables[0].x_bins, "veRpmBins");
        assert_eq!(tables[0].x_channel, "rpm");
        assert_eq!(tables[0].y_channel, "");
        assert!(tables[0].looks_like_ve_table());

        // A table without a title is called by its id.
        assert_eq!(tables[1].title, "sparkTbl");
        assert!(!tables[1].looks_like_ve_table());
    }
}
