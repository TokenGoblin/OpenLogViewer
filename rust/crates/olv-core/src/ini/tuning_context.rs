//! Ported from `TuningContext.cs`: every named number a firmware definition can
//! refer to, gathered in one place.
//!
//! Gauge scales, warning bands and derived channels are all written as expressions
//! over names, and the names come from three unrelated places: the tune's own
//! constants, TunerStudio's per-project variables, and channels the INI derives
//! from other channels. A rev counter runs to `{rpmHardLimit + 2000}` and a coolant
//! gauge to `{clt_exp ? 450 : 250}`, where `clt_exp` is stored in the tune as the
//! word "Normal" and only becomes a zero by way of a declaration elsewhere in the
//! INI.
//!
//! Resolve none of that and the gauges still appear, with no scale, which is worse
//! than absent, because a dial with the wrong range looks like a reading.

use super::msq_ini::{self, SymbolSet, default_symbols};
use crate::base::math_expression::MathExpression;
use crate::fmt;
use crate::model::{IgnoreCaseMap, safe_xml, tune_axes};
use regex::Regex;
use std::sync::LazyLock;

/// Declarations in `[PcVariables]`: `bits` label lists and plain scalars.
static BITS_VARIABLE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*bits\s*,\s*[A-Z]\d\d\s*,\s*\[[^\]]*\]\s*,\s*(?<labels>.+)$")
        .expect("pattern")
});

/// Builds the lookup for one firmware and tune.
///
/// Later sources do not overwrite earlier ones, so a value the tune states
/// outright always beats one the INI would have derived.
pub fn build(
    ini_text: Option<&str>,
    tune_xml: Option<&str>,
    symbols: Option<&SymbolSet>,
    from_ecu: Option<&IgnoreCaseMap<f64>>,
) -> IgnoreCaseMap<f64> {
    let mut known: IgnoreCaseMap<f64> = IgnoreCaseMap::new();

    // The ECU first and unconditionally: what the controller is running beats what
    // a file last recorded, and a file may be stale or absent.
    if let Some(from_ecu) = from_ecu {
        for (name, value) in from_ecu.iter() {
            known.insert(name, *value);
        }
    }

    for (name, value) in tune_axes::read_scalars(tune_xml).iter() {
        known.try_add(name, *value);
    }

    let ini_text = match ini_text {
        None | Some("") => return known,
        Some(text) => text,
    };

    let symbols = symbols.unwrap_or_else(|| default_symbols());

    add_pc_variables(&mut known, ini_text, tune_xml, symbols);
    add_constant_expressions(&mut known, ini_text, symbols);

    known
}

/// TunerStudio's own variables, whose values live in the tune and whose meaning
/// lives in the INI.
///
/// A `bits` variable is stored as one of its labels ("Normal" rather than 0) so the
/// declaration has to be read to turn the word back into the number the expressions
/// do arithmetic on.
fn add_pc_variables(known: &mut IgnoreCaseMap<f64>, ini_text: &str, tune_xml: Option<&str>, symbols: &SymbolSet) {
    let mut labels: IgnoreCaseMap<Vec<String>> = IgnoreCaseMap::new();

    for raw in msq_ini::section(ini_text, "PcVariables", symbols) {
        let line = msq_ini::strip(&raw);
        let Some(m) = BITS_VARIABLE.captures(&line) else {
            continue;
        };

        labels.insert(
            &m["name"],
            m["labels"]
                .split(',')
                .map(|l| l.trim().trim_matches('"').to_string())
                .collect(),
        );
    }

    for (name, text) in read_pc_variables(tune_xml).iter() {
        // A number wins outright.
        if let Some(number) = fmt::parse_f64_inv(text) {
            known.try_add(name, number);
            continue;
        }

        let Some(options) = labels.get(name) else {
            continue;
        };

        if let Some(at) = options.iter().position(|o| crate::model::eq_ignore_case(o, text)) {
            known.try_add(name, at as f64);
        }
    }
}

/// The `pcVariable` entries of a tune, as written.
pub fn read_pc_variables(msq_xml: Option<&str>) -> IgnoreCaseMap<String> {
    let mut values = IgnoreCaseMap::new();

    let xml = match msq_xml {
        Some(xml) if !xml.trim().is_empty() => xml,
        _ => return values,
    };

    let Ok(document) = safe_xml::parse(xml) else {
        return values;
    };

    for element in document.descendants().filter(|e| e.local_name() == "pcVariable") {
        let Some(name) = element.attribute("name") else {
            continue;
        };
        if name.trim().is_empty() {
            continue;
        }

        values.insert(name, element.value().trim().trim_matches('"').to_string());
    }

    values
}

/// Channels the INI derives that turn out to be constants.
///
/// `cltlowlim = { clt_exp ? -40 : -40 }` is declared beside the live channels but
/// depends on nothing that moves, so it has a value before the engine is running,
/// which is exactly when a gauge needs its scale.
///
/// Repeated until nothing more resolves, because one can be written in terms of
/// another.
fn add_constant_expressions(known: &mut IgnoreCaseMap<f64>, ini_text: &str, symbols: &SymbolSet) {
    let expressions = msq_ini::read_output_channels(ini_text, Some(symbols)).expressions;

    // Three passes is plenty for the chains these files actually contain, and it
    // cannot loop on one that refers to itself.
    for _ in 0..3 {
        let mut added = false;

        for expression in &expressions {
            if known.contains_key(&expression.name) {
                continue;
            }
            let Some(value) = evaluate(&expression.expression, known) else {
                continue;
            };
            if value.is_nan() {
                continue;
            }

            known.insert(&expression.name, value);
            added = true;
        }

        if !added {
            break;
        }
    }
}

/// An expression's value, or none when it names something not yet known.
///
/// Deliberately strict: a missing name yields nothing rather than NaN, so a channel
/// that genuinely depends on the engine running is left out instead of being
/// recorded as a constant that happens to be not-a-number.
pub fn evaluate(expression: &str, known: &IgnoreCaseMap<f64>) -> Option<f64> {
    let parsed = MathExpression::try_parse(expression, known.keys()).ok()?;

    let mut arguments = Vec::with_capacity(parsed.references().len());
    for reference in parsed.references() {
        arguments.push(*known.get(reference)?);
    }

    Some(parsed.evaluate(&arguments))
}

#[cfg(test)]
mod tests {
    use super::*;

    const INI: &str = "[PcVariables]\n\
        clt_exp = bits, U08, [0:0], \"Normal\", \"Expanded\"\n\
        [OutputChannels]\n\
        ochBlockSize = 2\n\
        clthighlim = { clt_exp ? 450 : 250 }\n\
        other = { clthighlim + nCylinders }\n";

    const TUNE: &str = "<?xml version=\"1.0\"?><msq><page>\
        <pcVariable name=\"clt_exp\">\"Normal\"</pcVariable>\
        <pcVariable name=\"rpmhigh\">7000</pcVariable>\
        <constant name=\"nCylinders\">6</constant></page></msq>";

    #[test]
    fn labels_become_numbers_and_derived_constants_resolve() {
        let known = build(Some(INI), Some(TUNE), None, None);

        assert_eq!(known.get("clt_exp"), Some(&0.0));
        assert_eq!(known.get("rpmhigh"), Some(&7000.0));
        assert_eq!(known.get("clthighlim"), Some(&250.0));
        // Written in terms of another derived channel: resolved on a later pass.
        assert_eq!(known.get("other"), Some(&256.0));
    }

    #[test]
    fn the_ecu_beats_the_tune() {
        let mut ecu = IgnoreCaseMap::new();
        ecu.insert("nCylinders", 8.0);

        let known = build(None, Some(TUNE), None, Some(&ecu));

        assert_eq!(known.get("nCylinders"), Some(&8.0));
    }

    #[test]
    fn a_missing_name_is_none_rather_than_nan() {
        assert_eq!(evaluate("nothing + 1", &IgnoreCaseMap::new()), None);
    }

    // ----- UntrustedInputTests ----------------------------------------------
    //
    // What happens when a file was not written by a friend. Every input here
    // arrives from somewhere else (a tune emailed by a tuner, a datalog from a
    // forum, a firmware .ini downloaded from the internet and dropped into the
    // definitions folder this application asks people to fill). None of it can be
    // assumed well-meaning, and the failure mode that matters is not a wrong
    // reading but a process that stops existing.

    /// Three levels expand to ten thousand characters from a few hundred bytes, and
    /// each further level multiplies by ten. Ten of them is a gigabyte, which is a
    /// crash rather than a slow load.
    #[test]
    fn an_entity_bomb_is_refused_rather_than_expanded() {
        let bomb = "<?xml version=\"1.0\"?>\n\
            <!DOCTYPE msq [\n\
              <!ENTITY a \"aaaaaaaaaa\">\n\
              <!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">\n\
              <!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">\n\
              <!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">\n\
            ]>\n\
            <msq>&d;</msq>";

        assert!(matches!(safe_xml::parse(bomb), Err(crate::Error::Xml(_))));
    }

    /// Measured rather than assumed, and asserted so it stays true: a document
    /// naming a local file must not be able to read it.
    #[test]
    fn an_external_entity_reads_no_file() {
        let canary = std::env::temp_dir().join(format!("olv-canary-{}.txt", std::process::id()));
        std::fs::write(&canary, "CANARY").unwrap();

        let path = canary.to_string_lossy().replace('\\', "/");
        let xxe = format!(
            "<?xml version=\"1.0\"?>\n<!DOCTYPE msq [ <!ENTITY leak SYSTEM \"file:///{path}\"> ]>\n<msq><page><pcVariable name=\"rpmhigh\">&leak;</pcVariable></page></msq>"
        );

        // Either refused outright or read with nothing in it. What must not happen
        // is the file's contents arriving in the document.
        if let Ok(document) = safe_xml::parse(&xxe) {
            assert!(!document.root().value().contains("CANARY"));
        }

        let _ = std::fs::remove_file(&canary);
    }

    #[test]
    fn an_ordinary_tune_still_loads() {
        let tune = "<?xml version=\"1.0\"?>\n<msq><page><pcVariable name=\"rpmhigh\">9000</pcVariable></page></msq>";

        assert_eq!(
            read_pc_variables(Some(tune)).get("rpmhigh").map(String::as_str),
            Some("9000")
        );
    }

    /// Ignoring the definition rather than prohibiting it: prohibiting would reject
    /// this file, which is perfectly ordinary and which some writers emit.
    #[test]
    fn a_document_type_on_its_own_is_harmless_and_accepted() {
        let declared = "<?xml version=\"1.0\"?><!DOCTYPE msq>\n<msq><page><pcVariable name=\"rpmhigh\">9000</pcVariable></page></msq>";

        assert_eq!(
            read_pc_variables(Some(declared)).get("rpmhigh").map(String::as_str),
            Some("9000")
        );
    }

    /// This one was not theoretical. Before the depth limit, ten thousand brackets
    /// exited with 0xC00000FD, a stack overflow, which takes the process down where
    /// nothing can catch it: no message, no log line, no way to say which file did
    /// it.
    ///
    /// Reachable from a firmware .ini's [OutputChannels], which are downloaded and
    /// which this application invites people to install.
    #[test]
    fn an_expression_cannot_nest_deeply_enough_to_end_the_process() {
        let nested = format!("{}rpm{}", "(".repeat(10_000), ")".repeat(10_000));

        let error = MathExpression::try_parse(&nested, ["rpm"]).err().expect("refused");
        assert!(error.to_lowercase().contains("deep"), "{error}");
    }

    /// The deepest in any firmware INI here nests three.
    #[test]
    fn expressions_people_actually_write_are_unaffected() {
        let parsed = MathExpression::try_parse(
            "rpm ? (60000.0 / (rpm * (twoStroke == 1 ? 1 : 2))) : 0",
            ["rpm", "twoStroke"],
        );

        assert!(parsed.is_ok(), "{:?}", parsed.err());
    }

    /// It was not. A bracket and a function argument each began parsing below the
    /// conditional operator, so "rpm ? 1 : 0" parsed on its own and "(rpm ? 1 : 0)"
    /// did not, reported as a missing ')', which says nothing about what was wrong.
    /// Found by an audit rather than by anyone hitting it, which is the only reason
    /// it is not still there.
    #[test]
    fn a_conditional_is_allowed_wherever_a_value_is() {
        for expression in [
            "(rpm ? 1 : 0)",
            "(rpm ? 1 : 0) * 2",
            "1 + (twoStroke == 1 ? 1 : 2)",
            "min(rpm ? 1 : 2, 3)",
        ] {
            let parsed = MathExpression::try_parse(expression, ["rpm", "twoStroke"]);
            assert!(parsed.is_ok(), "{expression} -> {:?}", parsed.err());
        }
    }

    /// Parsing it is not enough; the arms have to end up the right way round.
    #[test]
    fn a_bracketed_conditional_evaluates_to_what_it_says() {
        let parsed = MathExpression::try_parse("(rpm ? 10 : 20) + 1", ["rpm"]).expect("parses");

        assert_eq!(parsed.evaluate(&[1.0]), 11.0);
        assert_eq!(parsed.evaluate(&[0.0]), 21.0);
    }

    /// Length is not depth: a chain of operators is parsed as a loop and costs no
    /// stack at all, so the limit must not catch it.
    #[test]
    fn a_long_expression_is_not_mistaken_for_a_deep_one() {
        let chain = vec!["rpm"; 5_000].join(" + ");

        let parsed = MathExpression::try_parse(&chain, ["rpm"]);
        assert!(parsed.is_ok(), "{:?}", parsed.err());
    }
}
