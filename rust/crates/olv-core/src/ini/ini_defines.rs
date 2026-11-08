//! Ported from `IniDefines.cs`: named lists of option labels, which a firmware
//! writes once and refers to wherever the same choice appears.
//!
//! ```text
//!   #define loadSourceNames = "MAP", "TPS", "IMAP/EMAP", "INVALID", ...
//!   algorithm    = bits, U08, 37, [0:2], $loadSourceNames
//!   ignAlgorithm = bits, U08, 26, [4:6], $loadSourceNames
//! ```
//!
//! Without these a setting's value has no name. The reference is not a label:
//! left as written, "Load source" offers one choice called `$loadSourceNames` and
//! picking it writes nothing sensible. An MS3 definition points at one from 338 of
//! its bit fields and a Speeduino from 175, so this is most of what makes a
//! settings page readable.
//!
//! A definition may be built from others (`#define invalid_x16 = $invalid_x8,
//! $invalid_x8`) and the position of a label in the finished list is the number
//! the ECU stores, so expanding them is not a nicety. One that refers to itself is
//! left as it stands rather than expanded forever.

use super::msq_ini::{self, SymbolSet, default_symbols};
use crate::model::IgnoreCaseMap;
use regex::Regex;
use std::sync::LazyLock;

/// `#define name = "a", "b", $other`.
///
/// Only the form with a list after it. A bare `#define SYMBOL` is a switch for the
/// preprocessor and means something else entirely.
static DEFINE: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*#define\s+(?<name>\w+)\s*=\s*(?<values>.+)$").expect("pattern"));

/// Reads them, with references between them worked out.
///
/// `symbols` says which build this is. A firmware commonly defines the same list
/// twice (one set of pin names per board) inside `#if`, and the last one wins where
/// the preprocessor is ignored. That gives correctly numbered options carrying the
/// wrong labels, which is worse than none: the page looks right and reads wrong.
pub fn read(ini_text: &str, symbols: Option<&SymbolSet>) -> IgnoreCaseMap<Vec<String>> {
    let symbols = symbols.unwrap_or_else(|| default_symbols());

    let mut expanded: IgnoreCaseMap<Vec<String>> = IgnoreCaseMap::new();

    // Expanded as each one is met, against the lists that already stand.
    //
    // A firmware grows a list by redefining it in terms of itself:
    //
    //   #define PIN_DIGOUT_CANPWM = "CANPWM1", … "CANPWM8"
    //   #define PIN_DIGOUT_CANPWM = "INVALID", $PIN_DIGOUT1, $PIN_DIGOUT_CANPWM, …
    //
    // the second meaning the eight names just declared, exactly as a preprocessor
    // would read it. Collecting every definition first and resolving afterwards
    // makes the second one refer to itself, which the loop guard then leaves as the
    // literal "$PIN_DIGOUT_CANPWM": one bogus label where eight real ones belong,
    // so every pin after it is numbered seven short.
    for line in msq_ini::live(ini_text, symbols) {
        // Not msq_ini::strip: a label may contain a semicolon, and cutting at the
        // first one would take half the list with it.
        let Some(m) = DEFINE.captures(line.trim_end_matches('\r')) else {
            continue;
        };

        // The comment comes off before the list is split, not after. Left on, a
        // trailing "; every pin we know" makes "$pins" fail the identifier test and
        // stay a literal, and any comma inside the comment becomes further labels,
        // either of which renumbers every option after it.
        let list = expand(without_comment(&m["values"]), &expanded);
        expanded.insert(&m["name"], list);
    }

    expanded
}

/// Expands a list, splicing in any list it refers to.
///
/// A reference contributes all of its labels in order, because the position of a
/// label is the value it stands for.
pub fn expand(values: &str, defines: &IgnoreCaseMap<Vec<String>>) -> Vec<String> {
    let mut built = Vec::new();

    for part in split(values) {
        if let Some(name) = reference(part)
            && let Some(list) = defines.get(name)
        {
            built.extend(list.iter().cloned());
            continue;
        }

        // Not a reference, or one nothing defines: a label either way, and an
        // unknown reference is better shown than silently dropped, since dropping
        // it would renumber everything after it.
        built.push(unquote(part));
    }

    built
}

/// The list without any trailing comment, respecting quotes: a label may contain
/// a semicolon, and Speeduino's log separator is one.
fn without_comment(values: &str) -> &str {
    match index_of_comment(values) {
        Some(at) => &values[..at],
        None => values,
    }
}

/// The name a `$reference` points at, or none for a plain label.
fn reference(part: &str) -> Option<&str> {
    let trimmed = part.trim();
    let name = trimmed.strip_prefix('$')?;

    (!name.is_empty() && name.chars().all(|c| c.is_alphanumeric() || c == '_')).then_some(name)
}

/// Comma-separated, leaving commas inside quotes alone.
fn split(text: &str) -> Vec<&str> {
    let mut parts = Vec::new();
    let mut quoted = false;
    let mut start = 0;

    for (i, c) in text.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if c == ',' && !quoted {
            parts.push(&text[start..i]);
            start = i + 1;
        }
    }

    parts.push(&text[start..]);
    parts
}

fn unquote(text: &str) -> String {
    let mut trimmed = text.trim();

    // A trailing comment on the last entry of a list.
    if let Some(comment) = index_of_comment(trimmed) {
        trimmed = trimmed[..comment].trim();
    }

    if trimmed.len() >= 2 && trimmed.starts_with('"') && trimmed.ends_with('"') {
        trimmed[1..trimmed.len() - 1].to_string()
    } else {
        trimmed.to_string()
    }
}

fn index_of_comment(text: &str) -> Option<usize> {
    let mut quoted = false;

    for (i, c) in text.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if c == ';' && !quoted {
            return Some(i);
        }
    }

    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ini::tune_layout;

    fn strings(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn a_named_list_is_read() {
        let defines = read(
            r#"#define loadSourceNames = "MAP", "TPS", "IMAP/EMAP", "INVALID""#,
            None,
        );

        assert_eq!(
            defines.get("loadSourceNames"),
            Some(&strings(&["MAP", "TPS", "IMAP/EMAP", "INVALID"]))
        );
    }

    /// The position of a label is the number the ECU stores, so a reference has to
    /// contribute all of its labels in order.
    #[test]
    fn one_list_may_be_built_from_others() {
        let defines = read(
            "#define invalid_x2 = \"INVALID\", \"INVALID\"\n\
             #define invalid_x4 = $invalid_x2, $invalid_x2\n\
             #define pins       = \"Off\", $invalid_x4",
            None,
        );

        assert_eq!(defines.get("invalid_x4").map(Vec::len), Some(4));
        assert_eq!(
            defines.get("pins"),
            Some(&strings(&["Off", "INVALID", "INVALID", "INVALID", "INVALID"]))
        );
    }

    /// A definition with a mistake in it should be visible in the interface, not
    /// expanded until the stack is gone.
    #[test]
    fn one_that_refers_to_itself_is_left_as_written() {
        let defines = read(r#"#define loop = "A", $loop"#, None);

        assert_eq!(defines.get("loop"), Some(&strings(&["A", "$loop"])));
    }

    #[test]
    fn a_bare_define_is_a_preprocessor_switch_and_not_a_list() {
        let defines = read("#define CAN_COMMANDS\n#define real = \"A\", \"B\"", None);

        assert!(!defines.contains_key("CAN_COMMANDS"));
        assert_eq!(defines.len(), 1);
    }

    #[test]
    fn a_comma_inside_a_label_is_part_of_it() {
        let defines = read(r#"#define odd = "One, then two", "Three""#, None);

        assert_eq!(defines.get("odd"), Some(&strings(&["One, then two", "Three"])));
    }

    #[test]
    fn a_comment_after_the_list_is_not_a_label() {
        let defines = read(r#"#define x = "A", "B" ; two of them"#, None);

        assert_eq!(defines.get("x"), Some(&strings(&["A", "B"])));
    }

    /// Which is why this cannot use the ordinary comment stripper.
    #[test]
    fn a_label_may_contain_a_semicolon() {
        let defines = read(r#"#define x = "Set it; carefully", "B""#, None);

        assert_eq!(defines.get("x"), Some(&strings(&["Set it; carefully", "B"])));
    }

    /// Dropping it would renumber every label after it, which is worse than
    /// showing a name nobody defined.
    #[test]
    fn a_reference_to_nothing_is_kept_rather_than_dropped() {
        let list = expand(r#""A", $missing, "C""#, &IgnoreCaseMap::new());

        assert_eq!(list.len(), 3);
        assert_eq!(list[2], "C");
    }

    // ----- what this is for ---------------------------------------------------

    /// Verified against a connected Speeduino, where "algorithm" reads zero and
    /// means MAP.
    #[test]
    fn a_bit_field_pointing_at_a_list_gets_its_values_named() {
        let ini = "#define loadSourceNames = \"MAP\", \"TPS\", \"IMAP/EMAP\", \"INVALID\"\n\
                   \n\
                   [Constants]\n\
                   page = 1\n\
                   nPages = 1\n\
                   pageSize = 64\n\
                   \x20  algorithm = bits, U08, 37, [0:2], $loadSourceNames";

        let layout = tune_layout::read(ini, None);
        let algorithm = layout
            .constants
            .iter()
            .find(|c| c.name == "algorithm")
            .expect("algorithm");

        assert_eq!(algorithm.option_name(0.0), "MAP");
        assert_eq!(algorithm.option_name(1.0), "TPS");
        assert!(algorithm.is_valid_option(2));
        assert!(!algorithm.is_valid_option(3));
    }

    #[test]
    fn a_bit_field_may_mix_its_own_labels_with_a_list() {
        let ini = "#define rest = \"B\", \"C\"\n\
                   \n\
                   [Constants]\n\
                   page = 1\n\
                   nPages = 1\n\
                   pageSize = 64\n\
                   \x20  mixed = bits, U08, 0, [0:1], \"A\", $rest";

        let layout = tune_layout::read(ini, None);
        let mixed = layout.constants.iter().find(|c| c.name == "mixed").expect("mixed");

        assert_eq!(mixed.options, strings(&["A", "B", "C"]));
    }
}
