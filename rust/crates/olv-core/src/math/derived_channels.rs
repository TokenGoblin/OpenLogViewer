//! Ported from `DerivedChannels.cs`. Owned by package `w2-math-channels`.
//!
//! Values a firmware defines in terms of other values rather than reading from anywhere.
//!
//! `[OutputChannels]` holds two different things. Most of it names fields in the block the ECU
//! sends. The rest are written as an expression in braces:
//!
//! ```text
//!   isTps1Primary = { tps1_1AdcChannel != 0 }
//!   isEtbEnabled  = { isEtb1Enabled || isEtb2Enabled }
//! ```
//!
//! One may be defined in terms of another, so resolving one is an evaluation, and a definition
//! with a mistake could have two refer to each other, which is why the resolver keeps track of
//! what it is already working on.

use std::cell::RefCell;
use std::sync::OnceLock;

use regex::Regex;

use crate::ini::dialog_condition;
use crate::ini::msq_ini::{self, SymbolSet};
use crate::model::IgnoreCaseMap;

/// A name bound to an expression: `isSTFT = { ... }`. The whole of the braces is taken
/// rather than the first closing one, since an expression may contain a nested group.
fn definition() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"^\s*(?P<name>[A-Za-z_]\w*)\s*=\s*\{(?P<expression>.*)\}\s*$").expect("a valid pattern")
    })
}

/// Reads them out of `[OutputChannels]`. `symbols` of `None` is the default set.
pub fn read(ini_text: &str, symbols: Option<&SymbolSet>) -> IgnoreCaseMap<String> {
    let mut derived = IgnoreCaseMap::new();

    for raw in msq_ini::section(
        ini_text,
        "OutputChannels",
        symbols.unwrap_or_else(|| msq_ini::default_symbols()),
    ) {
        let line = msq_ini::strip(&raw);
        if line.is_empty() {
            continue;
        }

        let Some(caps) = definition().captures(&line) else {
            continue;
        };

        let expression = caps["expression"].trim();
        if expression.is_empty() {
            continue;
        }

        derived.insert(&caps["name"], expression.to_string());
    }

    derived
}

struct Resolver<'a, F: Fn(&str) -> f64> {
    derived: &'a IgnoreCaseMap<String>,
    lookup: F,
    visiting: RefCell<IgnoreCaseMap<()>>,
}

impl<F: Fn(&str) -> f64> Resolver<'_, F> {
    fn resolve(&self, name: &str) -> f64 {
        let direct = (self.lookup)(name);
        if !direct.is_nan() {
            return direct;
        }

        let Some(expression) = self.derived.get(name) else {
            return f64::NAN;
        };

        // Already being worked out: a definition where two refer to each other would
        // otherwise not terminate. Unknown is the honest answer.
        if !self.visiting.borrow_mut().try_add(name, ()) {
            return f64::NAN;
        }

        // As a number, not as a verdict: most of these are arithmetic rather than tests.
        let result = dialog_condition::number(expression, |n| self.resolve(n));
        self.visiting.borrow_mut().remove(name);
        result
    }
}

/// A lookup that answers for derived names too, by working them out.
///
/// Anything the underlying lookup can answer is answered by it, so a real field always wins
/// over a name that happens to match. With nothing derived, the lookup itself is returned.
pub fn resolving<'a>(
    derived: &'a IgnoreCaseMap<String>,
    lookup: impl Fn(&str) -> f64 + 'a,
) -> Box<dyn Fn(&str) -> f64 + 'a> {
    if derived.is_empty() {
        return Box::new(lookup);
    }

    // Per resolver rather than per call, since one derived value may be reached through
    // several others on the way to the same answer.
    let resolver = Resolver {
        derived,
        lookup,
        visiting: RefCell::new(IgnoreCaseMap::new()),
    };
    Box::new(move |name| resolver.resolve(name))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ini::dialog_condition::{ConditionVerdict, evaluate};
    use std::cell::Cell;
    use std::rc::Rc;

    const INI: &str = "[OutputChannels]
   ochBlockSize = 128
   rpm            = scalar, U16,  0, \"rpm\", 1, 0
   tps1_1AdcChannel = scalar, U08, 2, \"\", 1, 0

   isTps1Primary = { tps1_1AdcChannel != 0 }
   isEtb1Enabled = { etb1 == 1 }
   isEtb2Enabled = { etb2 == 1 }
   isEtbEnabled  = { isEtb1Enabled || isEtb2Enabled }
   blank         = {  }
";

    fn read_ini() -> IgnoreCaseMap<String> {
        read(INI, None)
    }

    fn resolver(known: &[(&str, f64)]) -> Box<dyn Fn(&str) -> f64> {
        let mut values = IgnoreCaseMap::new();
        for (n, v) in known {
            values.insert(n, *v);
        }
        // Leaked so the boxed resolver can outlive the call; fine in a test.
        let derived: &'static IgnoreCaseMap<String> = Box::leak(Box::new(read_ini()));
        resolving(derived, move |n| values.get(n).copied().unwrap_or(f64::NAN))
    }

    // ----- reading them -----

    #[test]
    fn only_the_braced_definitions_are_taken_as_derived() {
        let derived = read_ini();

        // The ordinary fields have an offset and are read from the ECU.
        assert!(!derived.contains_key("rpm"));
        assert!(!derived.contains_key("tps1_1AdcChannel"));

        assert!(derived.contains_key("isTps1Primary"));
        assert_eq!(derived.get("isTps1Primary").unwrap(), "tps1_1AdcChannel != 0");
    }

    #[test]
    fn an_empty_definition_is_not_one() {
        assert!(!read_ini().contains_key("blank"));
    }

    #[test]
    fn names_are_matched_without_regard_to_case() {
        assert!(read_ini().contains_key("ISETBENABLED"));
    }

    // ----- resolving them -----

    #[test]
    fn a_derived_value_is_worked_out_from_the_ones_behind_it() {
        let resolve = resolver(&[("tps1_1AdcChannel", 3.0)]);
        assert_eq!(resolve("isTps1Primary"), 1.0);
        assert_eq!(resolver(&[("tps1_1AdcChannel", 0.0)])("isTps1Primary"), 0.0);
    }

    #[test]
    fn one_may_be_defined_in_terms_of_another() {
        // isEtbEnabled is isEtb1Enabled or isEtb2Enabled, and neither is a real field.
        assert_eq!(resolver(&[("etb1", 1.0), ("etb2", 0.0)])("isEtbEnabled"), 1.0);
        assert_eq!(resolver(&[("etb1", 0.0), ("etb2", 1.0)])("isEtbEnabled"), 1.0);
        assert_eq!(resolver(&[("etb1", 0.0), ("etb2", 0.0)])("isEtbEnabled"), 0.0);
    }

    #[test]
    fn a_real_field_wins_over_a_derived_name_of_the_same_name() {
        // A definition declaring both means the one the ECU actually sends.
        let resolve = resolver(&[("isTps1Primary", 42.0), ("tps1_1AdcChannel", 0.0)]);
        assert_eq!(resolve("isTps1Primary"), 42.0);
    }

    #[test]
    fn something_neither_real_nor_derived_is_still_unknown() {
        assert!(resolver(&[])("noSuchThing").is_nan());
    }

    #[test]
    fn a_derived_value_resting_on_something_unknown_is_unknown() {
        // etb1 is not supplied, so isEtb1Enabled cannot be judged, and neither can the one
        // built on it, since the other half is false.
        assert!(resolver(&[("etb2", 0.0)])("isEtbEnabled").is_nan());
    }

    #[test]
    fn knowing_one_half_still_settles_it() {
        // etb2 on makes isEtbEnabled true whatever etb1 turns out to be.
        assert_eq!(resolver(&[("etb2", 1.0)])("isEtbEnabled"), 1.0);
    }

    #[test]
    fn two_that_refer_to_each_other_do_not_hang_the_program() {
        let circular = "[OutputChannels]\n   a = { b }\n   b = { a }\n";
        let derived = read(circular, None);
        let resolve = resolving(&derived, |_| f64::NAN);
        assert!(resolve("a").is_nan());
    }

    #[test]
    fn one_reached_twice_on_the_way_to_an_answer_is_still_resolved() {
        // The guard is against a cycle, not against a value being used more than once.
        let shared = "[OutputChannels]\n   base  = { x == 1 }\n   left  = { base }\n   right = { base }\n   both  = { left && right }\n";
        let derived = read(shared, None);
        let resolve = resolving(&derived, |n| if n == "x" { 1.0 } else { f64::NAN });
        assert_eq!(resolve("both"), 1.0);
    }

    #[test]
    fn a_firmware_with_none_of_these_costs_nothing() {
        // The same lookup back, not a wrapper around it: the lookup is called directly, once.
        let calls = Rc::new(Cell::new(0));
        let c = calls.clone();
        let derived = IgnoreCaseMap::new();
        let resolve = resolving(&derived, move |_| {
            c.set(c.get() + 1);
            1.0
        });
        assert_eq!(resolve("anything"), 1.0);
        assert_eq!(calls.get(), 1);
    }

    // ----- what it does for a real condition -----

    #[test]
    fn a_condition_written_against_a_derived_name_can_now_be_judged() {
        let resolve = resolver(&[("etb1", 1.0), ("etb2", 0.0)]);

        assert_eq!(
            evaluate(
                "isEtbEnabled && isTps1Primary",
                resolver(&[("etb1", 1.0), ("etb2", 0.0), ("tps1_1AdcChannel", 3.0)])
            ),
            ConditionVerdict::Shown
        );
        assert_eq!(evaluate("isEtbEnabled", resolve), ConditionVerdict::Shown);
    }

    // ----- from SettingsReviewTests -----

    fn map(pairs: &[(&str, &str)]) -> IgnoreCaseMap<String> {
        let mut m = IgnoreCaseMap::new();
        for (k, v) in pairs {
            m.insert(k, v.to_string());
        }
        m
    }

    #[test]
    fn a_derived_channel_keeps_its_number_rather_than_becoming_one() {
        // These are arithmetic far more often than they are tests. Passing one through a
        // yes-or-no answer turned 63 kPa into 1.
        let derived = map(&[("engineLoad", "fuelingLoad"), ("loadOver100", "engineLoad > 100")]);
        let lookup = resolving(&derived, |n| if n == "fuelingLoad" { 63.0 } else { f64::NAN });

        assert_eq!(lookup("engineLoad"), 63.0);
        assert_eq!(lookup("loadOver100"), 0.0);
        assert!(lookup("nothingKnowsThis").is_nan());
    }

    #[test]
    fn a_derived_channel_that_refers_to_itself_still_answers_nothing() {
        let derived = map(&[("a", "b + 1"), ("b", "a + 1")]);
        assert!(resolving(&derived, |_| f64::NAN)("a").is_nan());
    }
}
