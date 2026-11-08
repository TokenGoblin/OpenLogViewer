//! The INI parsers against the C# on the real definitions in the corpus
//! (rust/oracle/expected/corpus/w1-ini/ini.json, gitignored).
//!
//! Per INI: the signature, pages, constants, PC variables, output channels,
//! datalog, option lists, tables, curves, dialogs and gauges, compared field by
//! field (doubles as IEEE bits). Per INI and tune: the lookup TuningContext
//! builds, the gauges that resolve against it and every dialog condition's
//! verdict, compared as a count and a hash of a canonical text.

use olv_core::ini::dialog_condition::{self, ConditionVerdict};
use olv_core::ini::gauge_catalog::{self, GaugeSpec};
use olv_core::ini::ini_catalog;
use olv_core::ini::ini_defines;
use olv_core::ini::msq_ini;
use olv_core::ini::table_editor_reader;
use olv_core::ini::tune_dialogs;
use olv_core::ini::tune_layout::{self, TuneConstant};
use olv_core::ini::tuning_context;
use olv_core::model::{IgnoreCaseMap, tuning_text};
use serde::Deserialize;
use std::fmt::Debug;

#[derive(Deserialize)]
struct Case {
    file: String,
    signature: Option<String>,
    signature_in_text: Option<String>,
    pages: Vec<Page>,
    constants: Vec<Constant>,
    pc_variables: Vec<Constant>,
    derived: Vec<Named>,
    little_endian: bool,
    blocking_factor: i32,
    inter_write_delay: i32,
    after_burn_delay: i32,
    realtime: Realtime,
    aliases: Vec<Alias>,
    datalog: Vec<Datalog>,
    defines: Vec<Define>,
    tables: Vec<Table>,
    curves: Vec<Curve>,
    menus: Vec<Menu>,
    dialogs: Vec<Dialog>,
    gauges: Vec<Gauge>,
    front_page: Vec<String>,
    known_count: usize,
    known_hash: String,
    with_tunes: Vec<WithTune>,
}

#[derive(Deserialize)]
struct Page {
    index: i32,
    size: i32,
    identifier: String,
    read_command: String,
    chunk_write_command: String,
    burn_command: String,
}

#[derive(Deserialize)]
struct Constant {
    name: String,
    page: i32,
    offset: i32,
    #[serde(rename = "type")]
    type_: String,
    units: String,
    scale: String,
    transform: String,
    scale_expression: String,
    transform_expression: String,
    digits: i32,
    low: String,
    high: String,
    columns: i32,
    rows: i32,
    bit_low: i32,
    bit_high: i32,
    is_text: bool,
    size: i32,
    options: Vec<String>,
}

#[derive(Deserialize)]
struct Named {
    name: String,
    expression: String,
}

#[derive(Deserialize)]
struct Realtime {
    block_size: i32,
    get_command: String,
    little_endian: bool,
    blocking_factor: i32,
    uses_simple_command: bool,
    fields: Vec<Field>,
    expressions: Vec<Expression>,
    skipped: Vec<String>,
}

#[derive(Deserialize)]
struct Field {
    name: String,
    units: String,
    #[serde(rename = "type")]
    type_: String,
    offset: i32,
    scale: String,
    transform: String,
    digits: i32,
    bit_low: i32,
    bit_high: i32,
}

#[derive(Deserialize)]
struct Expression {
    name: String,
    units: String,
    expression: String,
}

#[derive(Deserialize)]
struct Alias {
    name: String,
    target: String,
}

#[derive(Deserialize)]
struct Datalog {
    channel: String,
    label: String,
    digits: i32,
}

#[derive(Deserialize)]
struct Define {
    name: String,
    values: Vec<String>,
}

#[derive(Deserialize)]
struct Table {
    id: String,
    title: String,
    map: String,
    values: String,
    x_bins: String,
    y_bins: String,
    x_channel: String,
    y_channel: String,
}

#[derive(Deserialize)]
struct Curve {
    key: String,
    name: String,
    title: String,
    x_bins: String,
    y_bins: String,
    x_label: String,
    y_label: String,
    x_low: String,
    x_high: String,
    y_low: String,
    y_high: String,
    help: String,
}

#[derive(Deserialize)]
struct Menu {
    title: String,
    entries: Vec<Entry>,
}

#[derive(Deserialize)]
struct Entry {
    dialog: String,
    title: String,
    condition: String,
}

#[derive(Deserialize)]
struct Dialog {
    key: String,
    name: String,
    title: String,
    layout: String,
    help: String,
    items: Vec<Item>,
}

#[derive(Deserialize)]
struct Item {
    kind: String,
    label: String,
    target: String,
    condition: String,
    position: String,
}

#[derive(Deserialize)]
struct Gauge {
    name: String,
    channel: String,
    title: String,
    units: String,
    category: String,
    low: String,
    high: String,
    low_danger: String,
    low_warning: String,
    high_warning: String,
    high_danger: String,
    value_digits: i32,
    label_digits: i32,
}

#[derive(Deserialize)]
struct WithTune {
    tune: String,
    pc_variables: usize,
    known_count: usize,
    known_hash: String,
    conditions: usize,
    conditions_hash: String,
    gauges: usize,
    gauges_hash: String,
}

/// Mismatches, collected so one run reports many rather than the first.
struct Diffs {
    scope: String,
    found: Vec<String>,
}

impl Diffs {
    fn new(scope: &str) -> Self {
        Self {
            scope: scope.to_string(),
            found: Vec::new(),
        }
    }

    fn eq<T: PartialEq + Debug>(&mut self, what: impl FnOnce() -> String, got: T, want: T) {
        if got != want {
            self.found.push(format!("{}: {got:?} != {want:?}", what()));
        }
    }

    /// A double against the C#'s IEEE bits; any NaN equals any NaN.
    fn bits(&mut self, what: impl FnOnce() -> String, got: f64, want_hex: &str) {
        let want = f64::from_bits(u64::from_str_radix(want_hex, 16).expect("hex bits"));
        let same = (got.is_nan() && want.is_nan()) || got.to_bits() == want.to_bits();
        if !same {
            self.found.push(format!("{}: {got} != {want}", what()));
        }
    }

    fn finish(self) {
        assert!(
            self.found.is_empty(),
            "{}: {} mismatches, first:\n{}",
            self.scope,
            self.found.len(),
            self.found.iter().take(20).cloned().collect::<Vec<_>>().join("\n")
        );
    }
}

fn canon(v: f64) -> String {
    if v.is_nan() {
        "nan".to_string()
    } else {
        format!("{:016x}", v.to_bits())
    }
}

/// FNV-1a over the UTF-8 bytes, as the emitter's TextHash.
fn text_hash(text: &str) -> String {
    let mut h: u64 = 0xcbf29ce484222325;
    for b in text.bytes() {
        h ^= u64::from(b);
        h = h.wrapping_mul(0x100000001b3);
    }
    format!("{h:016x}")
}

fn known_text(known: &IgnoreCaseMap<f64>) -> String {
    let mut pairs: Vec<(&str, f64)> = known.iter().map(|(k, v)| (k, *v)).collect();
    pairs.sort_by(|a, b| a.0.cmp(b.0));

    let mut text = String::new();
    for (name, value) in pairs {
        text.push_str(&format!("{name}={}\n", canon(value)));
    }
    text
}

fn compare_constants(d: &mut Diffs, section: &str, got: &[TuneConstant], want: &[Constant]) {
    d.eq(|| format!("{section} count"), got.len(), want.len());

    for (i, (g, w)) in got.iter().zip(want).enumerate() {
        let at = |field: &str| format!("{section}[{i}] {} {field}", w.name);

        d.eq(|| at("name"), g.name.as_str(), w.name.as_str());
        d.eq(|| at("page"), g.page, w.page);
        d.eq(|| at("offset"), g.offset, w.offset);
        d.eq(|| at("type"), format!("{:?}", g.type_), w.type_.clone());
        d.eq(|| at("units"), g.units.as_str(), w.units.as_str());
        d.bits(|| at("scale"), g.scale, &w.scale);
        d.bits(|| at("transform"), g.transform, &w.transform);
        d.eq(
            || at("scale_expression"),
            g.scale_expression.as_str(),
            w.scale_expression.as_str(),
        );
        d.eq(
            || at("transform_expression"),
            g.transform_expression.as_str(),
            w.transform_expression.as_str(),
        );
        d.eq(|| at("digits"), g.digits, w.digits);
        d.bits(|| at("low"), g.low, &w.low);
        d.bits(|| at("high"), g.high, &w.high);
        d.eq(|| at("columns"), g.columns, w.columns);
        d.eq(|| at("rows"), g.rows, w.rows);
        d.eq(|| at("bit_low"), g.bit_low, w.bit_low);
        d.eq(|| at("bit_high"), g.bit_high, w.bit_high);
        d.eq(|| at("is_text"), g.is_text, w.is_text);
        d.eq(|| at("size"), g.size(), w.size);
        d.eq(|| at("options"), &g.options, &w.options);
    }
}

fn compare_gauges(d: &mut Diffs, got: &[GaugeSpec], want: &[Gauge]) {
    d.eq(|| "gauges count".to_string(), got.len(), want.len());

    for (g, w) in got.iter().zip(want) {
        let at = |field: &str| format!("gauge {} {field}", w.name);

        d.eq(|| at("name"), g.name.as_str(), w.name.as_str());
        d.eq(|| at("channel"), g.channel.as_str(), w.channel.as_str());
        d.eq(|| at("title"), g.title.as_str(), w.title.as_str());
        d.eq(|| at("units"), g.units.as_str(), w.units.as_str());
        d.eq(|| at("category"), g.category.as_str(), w.category.as_str());
        d.bits(|| at("low"), g.low, &w.low);
        d.bits(|| at("high"), g.high, &w.high);
        d.bits(|| at("low_danger"), g.low_danger, &w.low_danger);
        d.bits(|| at("low_warning"), g.low_warning, &w.low_warning);
        d.bits(|| at("high_warning"), g.high_warning, &w.high_warning);
        d.bits(|| at("high_danger"), g.high_danger, &w.high_danger);
        d.eq(|| at("value_digits"), g.value_digits, w.value_digits);
        d.eq(|| at("label_digits"), g.label_digits, w.label_digits);
    }
}

fn gauge_text(gauges: &[GaugeSpec]) -> String {
    let mut text = String::new();
    for g in gauges {
        text.push_str(&format!(
            "{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}|{}\n",
            g.name,
            g.channel,
            g.title,
            g.units,
            g.category,
            canon(g.low),
            canon(g.high),
            canon(g.low_danger),
            canon(g.low_warning),
            canon(g.high_warning),
            canon(g.high_danger),
            g.value_digits,
            g.label_digits
        ));
    }
    text
}

fn verdict_name(v: ConditionVerdict) -> &'static str {
    match v {
        ConditionVerdict::Shown => "Shown",
        ConditionVerdict::Hidden => "Hidden",
        ConditionVerdict::Unknown => "Unknown",
    }
}

#[test]
fn every_definition_in_the_corpus_reads_as_the_csharp_reads_it() {
    let Some(cases) = olv_oracle::corpus_cases::<Case>("w1-ini", "ini") else {
        return;
    };
    assert!(!cases.is_empty());

    let corpus = olv_oracle::corpus_dir();

    for case in &cases {
        let path = corpus.join("definitions").join(&case.file);
        let text = tuning_text::read(&path).expect("definition");
        let mut d = Diffs::new(&case.file);

        // ----- catalogue ------------------------------------------------------
        d.eq(
            || "signature".to_string(),
            ini_catalog::read_signature(&path.to_string_lossy()),
            case.signature.clone(),
        );
        d.eq(
            || "signature_in_text".to_string(),
            ini_catalog::signature_in(&text),
            case.signature_in_text.clone(),
        );

        // ----- [Constants] and [PcVariables] ----------------------------------
        let layout = tune_layout::read(&text, None);

        d.eq(|| "pages count".to_string(), layout.pages.len(), case.pages.len());
        for (g, w) in layout.pages.iter().zip(&case.pages) {
            let at = |f: &str| format!("page {} {f}", w.index);
            d.eq(|| at("index"), g.index, w.index);
            d.eq(|| at("size"), g.size, w.size);
            d.eq(|| at("identifier"), g.identifier.as_str(), w.identifier.as_str());
            d.eq(|| at("read_command"), g.read_command.as_str(), w.read_command.as_str());
            d.eq(
                || at("chunk_write_command"),
                g.chunk_write_command.as_str(),
                w.chunk_write_command.as_str(),
            );
            d.eq(|| at("burn_command"), g.burn_command.as_str(), w.burn_command.as_str());
        }

        compare_constants(&mut d, "constants", &layout.constants, &case.constants);
        compare_constants(&mut d, "pc_variables", &layout.pc_variables, &case.pc_variables);

        let derived: Vec<(&str, &str)> = layout.derived.iter().map(|(k, v)| (k, v.as_str())).collect();
        let want_derived: Vec<(&str, &str)> = case
            .derived
            .iter()
            .map(|n| (n.name.as_str(), n.expression.as_str()))
            .collect();
        d.eq(|| "derived".to_string(), derived, want_derived);

        d.eq(|| "little_endian".to_string(), layout.little_endian, case.little_endian);
        d.eq(
            || "blocking_factor".to_string(),
            layout.blocking_factor,
            case.blocking_factor,
        );
        d.eq(
            || "inter_write_delay".to_string(),
            layout.inter_write_delay,
            case.inter_write_delay,
        );
        d.eq(
            || "after_burn_delay".to_string(),
            layout.after_burn_delay,
            case.after_burn_delay,
        );

        // ----- [OutputChannels] -----------------------------------------------
        let rt = msq_ini::read_output_channels(&text, None);
        let w = &case.realtime;

        d.eq(|| "rt block_size".to_string(), rt.block_size, w.block_size);
        d.eq(
            || "rt get_command".to_string(),
            rt.get_command.as_str(),
            w.get_command.as_str(),
        );
        d.eq(|| "rt little_endian".to_string(), rt.little_endian, w.little_endian);
        d.eq(
            || "rt blocking_factor".to_string(),
            rt.blocking_factor,
            w.blocking_factor,
        );
        d.eq(
            || "rt uses_simple_command".to_string(),
            rt.uses_simple_command(),
            w.uses_simple_command,
        );
        d.eq(|| "rt fields count".to_string(), rt.fields.len(), w.fields.len());
        for (g, w) in rt.fields.iter().zip(&w.fields) {
            let at = |f: &str| format!("field {} {f}", w.name);
            d.eq(|| at("name"), g.name.as_str(), w.name.as_str());
            d.eq(|| at("units"), g.units.as_str(), w.units.as_str());
            d.eq(|| at("type"), format!("{:?}", g.type_), w.type_.clone());
            d.eq(|| at("offset"), g.offset, w.offset);
            d.bits(|| at("scale"), g.scale, &w.scale);
            d.bits(|| at("transform"), g.transform, &w.transform);
            d.eq(|| at("digits"), g.digits, w.digits);
            d.eq(|| at("bit_low"), g.bit_low, w.bit_low);
            d.eq(|| at("bit_high"), g.bit_high, w.bit_high);
        }
        d.eq(
            || "rt expressions count".to_string(),
            rt.expressions.len(),
            w.expressions.len(),
        );
        for (g, w) in rt.expressions.iter().zip(&w.expressions) {
            let at = |f: &str| format!("expression {} {f}", w.name);
            d.eq(|| at("name"), g.name.as_str(), w.name.as_str());
            d.eq(|| at("units"), g.units.as_str(), w.units.as_str());
            d.eq(|| at("expression"), g.expression.as_str(), w.expression.as_str());
        }
        d.eq(|| "rt skipped".to_string(), &rt.skipped, &w.skipped);

        let want_aliases: Vec<(&str, &str)> = case
            .aliases
            .iter()
            .map(|a| (a.name.as_str(), a.target.as_str()))
            .collect();
        let alias_map = msq_ini::read_aliases(&text, None);
        d.eq(|| "aliases count".to_string(), alias_map.len(), want_aliases.len());
        for (name, target) in &want_aliases {
            d.eq(
                || format!("alias {name}"),
                alias_map.get(name).map(String::as_str),
                Some(*target),
            );
        }

        // ----- [Datalog] ------------------------------------------------------
        let datalog = msq_ini::read_datalog(&text, None);
        d.eq(|| "datalog count".to_string(), datalog.len(), case.datalog.len());
        for (g, w) in datalog.iter().zip(&case.datalog) {
            let at = |f: &str| format!("datalog {} {f}", w.channel);
            d.eq(|| at("channel"), g.channel.as_str(), w.channel.as_str());
            d.eq(|| at("label"), g.label.as_str(), w.label.as_str());
            d.eq(|| at("digits"), g.digits, w.digits);
        }

        // ----- #define lists -------------------------------------------------
        let defines = ini_defines::read(&text, None);
        d.eq(|| "defines count".to_string(), defines.len(), case.defines.len());
        for (g, w) in defines.iter().zip(&case.defines) {
            d.eq(|| format!("define {} name", w.name), g.0, w.name.as_str());
            d.eq(|| format!("define {}", w.name), g.1, &w.values);
        }

        // ----- [TableEditor] and [CurveEditor] --------------------------------
        let tables = table_editor_reader::read(&text, None);
        d.eq(|| "tables count".to_string(), tables.len(), case.tables.len());
        for (g, w) in tables.iter().zip(&case.tables) {
            let at = |f: &str| format!("table {} {f}", w.id);
            d.eq(|| at("id"), g.id.as_str(), w.id.as_str());
            d.eq(|| at("title"), g.title.as_str(), w.title.as_str());
            d.eq(|| at("map"), g.map.as_str(), w.map.as_str());
            d.eq(|| at("values"), g.values.as_str(), w.values.as_str());
            d.eq(|| at("x_bins"), g.x_bins.as_str(), w.x_bins.as_str());
            d.eq(|| at("y_bins"), g.y_bins.as_str(), w.y_bins.as_str());
            d.eq(|| at("x_channel"), g.x_channel.as_str(), w.x_channel.as_str());
            d.eq(|| at("y_channel"), g.y_channel.as_str(), w.y_channel.as_str());
        }

        let curves = tune_dialogs::read_curves(&text, None);
        d.eq(|| "curves count".to_string(), curves.len(), case.curves.len());
        for (g, w) in curves.iter().zip(&case.curves) {
            let at = |f: &str| format!("curve {} {f}", w.key);
            d.eq(|| at("key"), g.0, w.key.as_str());
            let c = g.1;
            d.eq(|| at("name"), c.name.as_str(), w.name.as_str());
            d.eq(|| at("title"), c.title.as_str(), w.title.as_str());
            d.eq(|| at("x_bins"), c.x_bins.as_str(), w.x_bins.as_str());
            d.eq(|| at("y_bins"), c.y_bins.as_str(), w.y_bins.as_str());
            d.eq(|| at("x_label"), c.x_label.as_str(), w.x_label.as_str());
            d.eq(|| at("y_label"), c.y_label.as_str(), w.y_label.as_str());
            d.eq(|| at("x_low"), c.x_low.as_str(), w.x_low.as_str());
            d.eq(|| at("x_high"), c.x_high.as_str(), w.x_high.as_str());
            d.eq(|| at("y_low"), c.y_low.as_str(), w.y_low.as_str());
            d.eq(|| at("y_high"), c.y_high.as_str(), w.y_high.as_str());
            d.eq(|| at("help"), c.help.as_str(), w.help.as_str());
        }

        // ----- [Menu] and [UserDefined] ---------------------------------------
        let ui = tune_dialogs::read_interface(&text, None);
        d.eq(|| "menus count".to_string(), ui.menus.len(), case.menus.len());
        for (g, w) in ui.menus.iter().zip(&case.menus) {
            let at = |f: &str| format!("menu {} {f}", w.title);
            d.eq(|| at("title"), g.title.as_str(), w.title.as_str());
            d.eq(|| at("entries count"), g.entries.len(), w.entries.len());
            for (ge, we) in g.entries.iter().zip(&w.entries) {
                d.eq(|| at("dialog"), ge.dialog.as_str(), we.dialog.as_str());
                d.eq(|| at("entry title"), ge.title.as_str(), we.title.as_str());
                d.eq(|| at("entry condition"), ge.condition.as_str(), we.condition.as_str());
            }
        }

        d.eq(|| "dialogs count".to_string(), ui.dialogs.len(), case.dialogs.len());
        for (g, w) in ui.dialogs.iter().zip(&case.dialogs) {
            let at = |f: &str| format!("dialog {} {f}", w.key);
            d.eq(|| at("key"), g.0, w.key.as_str());
            let dialog = g.1;
            d.eq(|| at("name"), dialog.name.as_str(), w.name.as_str());
            d.eq(|| at("title"), dialog.title.as_str(), w.title.as_str());
            d.eq(|| at("layout"), dialog.layout.as_str(), w.layout.as_str());
            d.eq(|| at("help"), dialog.help.as_str(), w.help.as_str());
            d.eq(|| at("items count"), dialog.items.len(), w.items.len());
            for (gi, wi) in dialog.items.iter().zip(&w.items) {
                d.eq(|| at("item kind"), format!("{:?}", gi.kind), wi.kind.clone());
                d.eq(|| at("item label"), gi.label.as_str(), wi.label.as_str());
                d.eq(|| at("item target"), gi.target.as_str(), wi.target.as_str());
                d.eq(|| at("item condition"), gi.condition.as_str(), wi.condition.as_str());
                d.eq(|| at("item position"), gi.position.as_str(), wi.position.as_str());
            }
        }

        // ----- gauges and the lookup they resolve against ---------------------
        let known = tuning_context::build(Some(&text), None, None, None);
        d.eq(|| "known count".to_string(), known.len(), case.known_count);
        d.eq(
            || "known hash".to_string(),
            text_hash(&known_text(&known)),
            case.known_hash.clone(),
        );

        let gauges = gauge_catalog::read(&text, Some(&known), None);
        compare_gauges(&mut d, &gauges, &case.gauges);
        d.eq(
            || "front page".to_string(),
            &gauge_catalog::read_front_page(&text, None),
            &case.front_page,
        );

        // ----- against every tune ---------------------------------------------
        for w in &case.with_tunes {
            let xml = tuning_text::read(&corpus.join("tunes").join(&w.tune)).expect("tune");
            let at = |f: &str| format!("{} {f}", w.tune);

            let known = tuning_context::build(Some(&text), Some(&xml), None, None);
            d.eq(
                || at("pc_variables"),
                tuning_context::read_pc_variables(Some(&xml)).len(),
                w.pc_variables,
            );
            d.eq(|| at("known count"), known.len(), w.known_count);
            d.eq(
                || at("known hash"),
                text_hash(&known_text(&known)),
                w.known_hash.clone(),
            );

            let lookup = |name: &str| known.get(name).copied().unwrap_or(f64::NAN);
            let mut verdicts = String::new();
            let judge = |condition: &'_ str, out: &mut String, seen: &mut Vec<String>| {
                if condition.is_empty() || seen.iter().any(|s| s == condition) {
                    return;
                }
                seen.push(condition.to_string());
                out.push_str(&format!(
                    "{condition}|{}|{}\n",
                    verdict_name(dialog_condition::evaluate(condition, lookup)),
                    canon(dialog_condition::number(condition, lookup))
                ));
            };
            let mut seen_owned: Vec<String> = Vec::new();
            for menu in &ui.menus {
                for entry in &menu.entries {
                    judge(&entry.condition, &mut verdicts, &mut seen_owned);
                }
            }
            for (_, dialog) in ui.dialogs.iter() {
                for item in &dialog.items {
                    judge(&item.condition, &mut verdicts, &mut seen_owned);
                }
            }
            d.eq(|| at("conditions"), seen_owned.len(), w.conditions);
            d.eq(
                || at("conditions hash"),
                text_hash(&verdicts),
                w.conditions_hash.clone(),
            );

            let gauges = gauge_catalog::read(&text, Some(&known), None);
            d.eq(|| at("gauges"), gauges.len(), w.gauges);
            d.eq(
                || at("gauges hash"),
                text_hash(&gauge_text(&gauges)),
                w.gauges_hash.clone(),
            );
        }

        d.finish();
    }
}
