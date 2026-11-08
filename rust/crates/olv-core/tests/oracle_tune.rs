//! The tune operations over every real tune in the corpus, against what the C# did with the
//! same files (rust/oracle/expected/corpus/w4-tune/tune.json, gitignored).
//!
//! Per tune, against the INI the C# chose for it (the one whose signature matches, else the
//! best of those that do not look like another firmware, as the application chooses):
//!
//! - the page bytes `msq_apply::load` builds from the file, with the load's counts and every
//!   complaint;
//! - the `.msq` `msq_writer` writes back, byte for byte (a hash and a length, plus the text
//!   itself when small), with the bibliography's author and date fixed, and whether reading it
//!   back gives the same pages;
//! - `tune_compare::compare` of the tune against itself and against the next tune that shares
//!   its INI;
//! - the `tune_restore::plan` of putting that tune onto this one, of a tune onto itself and
//!   against a mismatched signature;
//! - scripted edits and the bytes they encode to: a table scaled, nudged, interpolated and
//!   pushed far out of range; a curve dragged; a handful of settings of each kind changed.
//!
//! The Rust builds the same record the C# emitter does (`W4Tune.cs`) and the two are compared
//! whole; a mismatch names the first path that differs. Doubles are compared as IEEE bits.
//! Returns early when this machine has no corpus.

use olv_core::definitions::msq_file::MsqFile;
use olv_core::ini::table_editor_reader;
use olv_core::ini::tune_dialogs;
use olv_core::ini::tune_layout::{self, TuneLayout};
use olv_core::model::tuning_text;
use olv_core::tune::ecu_tune::{EcuTune, TuneWrite};
use olv_core::tune::msq_apply::{self, MsqLoad};
use olv_core::tune::msq_writer::{self, MsqStamp};
use olv_core::tune::tune_compare::{self, TuneDifference};
use olv_core::tune::tune_curve_edit::TuneCurveEdit;
use olv_core::tune::tune_edit::{TuneEdit, TuneSelection};
use olv_core::tune::tune_restore::{self, TuneRestorePlan};
use olv_core::tune::tune_settings_edit::TuneSettingsEdit;
use olv_oracle::{corpus_cases, corpus_dir};
use serde_json::{Map, Value, json};
use std::path::Path;

/// The comment given to every write, chosen to need escaping.
const COMMENT: &str = "round & trip <test> \"q\"";

fn bits(v: f64) -> String {
    if v.is_nan() {
        "nan".to_string()
    } else {
        format!("{:016x}", v.to_bits())
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// FNV-1a over bytes.
fn fnv(bytes: &[u8]) -> String {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for &b in bytes {
        h ^= u64::from(b);
        h = h.wrapping_mul(0x100_0000_01b3);
    }
    format!("{h:016x}")
}

/// Drops the nulls the C# omits.
fn strip(value: Value) -> Value {
    match value {
        Value::Object(map) => Value::Object(
            map.into_iter()
                .filter(|(_, v)| !v.is_null())
                .map(|(k, v)| (k, strip(v)))
                .collect::<Map<_, _>>(),
        ),
        Value::Array(items) => Value::Array(items.into_iter().map(strip).collect()),
        other => other,
    }
}

/// The first place two records differ, as a path, or None.
fn first_difference(path: &str, rust: &Value, csharp: &Value) -> Option<String> {
    match (rust, csharp) {
        (Value::Object(a), Value::Object(b)) => {
            for (k, av) in a {
                match b.get(k) {
                    None => return Some(format!("{path}.{k}: Rust has it, the C# does not")),
                    Some(bv) => {
                        if let Some(d) = first_difference(&format!("{path}.{k}"), av, bv) {
                            return Some(d);
                        }
                    }
                }
            }
            b.keys()
                .find(|k| !a.contains_key(*k))
                .map(|k| format!("{path}.{k}: the C# has it, Rust does not"))
        }
        (Value::Array(a), Value::Array(b)) => {
            for (i, (av, bv)) in a.iter().zip(b).enumerate() {
                if let Some(d) = first_difference(&format!("{path}[{i}]"), av, bv) {
                    return Some(d);
                }
            }
            (a.len() != b.len()).then(|| format!("{path}: Rust has {} items, the C# {}", a.len(), b.len()))
        }
        _ => (rust != csharp).then(|| {
            let show = |v: &Value| {
                let s = v.to_string();
                if s.len() > 300 { format!("{}...", &s[..300]) } else { s }
            };
            format!("{path}: Rust {}, the C# {}", show(rust), show(csharp))
        }),
    }
}

struct Loaded {
    msq: MsqFile,
    layout: TuneLayout,
    load: MsqLoad,
}

fn load_against(corpus: &Path, file: &str, ini: &str) -> Loaded {
    let msq = MsqFile::read_file(&corpus.join("tunes").join(file)).unwrap_or_else(|e| panic!("{file}: {e}"));
    let ini_text = tuning_text::read(&corpus.join("definitions").join(ini)).unwrap_or_else(|e| panic!("{ini}: {e}"));
    let layout = tune_layout::read(&ini_text, Some(&msq.symbols));
    let load = msq_apply::load(&layout, &msq, None);
    Loaded { msq, layout, load }
}

fn write_record(w: &TuneWrite) -> Value {
    json!({
        "page": w.page,
        "offset": w.offset,
        "length": w.data.len(),
        "hash": fnv(&w.data),
        "hex": (w.data.len() <= 4096).then(|| hex(&w.data)),
    })
}

fn difference(d: &TuneDifference) -> Value {
    json!({
        "name": d.name,
        "cells": d.cells,
        "mine": d.mine.map(bits),
        "theirs": d.theirs.map(bits),
        "mine_text": d.mine_text,
        "theirs_text": d.theirs_text,
        "mine_shown": d.mine_shown(),
        "theirs_shown": d.theirs_shown(),
        "is_array": d.is_array(),
        "summary": d.summary(),
    })
}

fn strings(items: &[olv_core::tune::msq_apply::MsqComplaint]) -> Vec<String> {
    items.iter().map(ToString::to_string).collect()
}

fn plan_record(plan: &TuneRestorePlan) -> Value {
    json!({
        "is_empty": plan.is_empty(),
        "bytes": plan.bytes(),
        "pages": plan.pages(),
        "summary": plan.summary(),
        "shortfall": plan.shortfall(),
        "complete": plan.complete(),
        "signatures_agree": plan.signatures_agree(),
        "file_signature": plan.file_signature,
        "ecu_signature": plan.ecu_signature,
        "writes": plan.writes.iter().map(write_record).collect::<Vec<_>>(),
        "differences": plan.differences.iter().map(difference).collect::<Vec<_>>(),
        "missing": strings(&plan.missing),
        "rejected": strings(&plan.rejected),
        "unknown": plan.unknown,
        "target": plan.target.pages().iter().map(|p| fnv(p)).collect::<Vec<_>>(),
    })
}

fn text_record(text: &str) -> Value {
    json!({
        "length": text.len(),
        "hash": fnv(text.as_bytes()),
        "lines": text.matches('\n').count(),
        "text": (text.chars().count() <= 4096).then(|| text.to_string()),
    })
}

/// The bibliography's author and date as the C# emitter fixed them.
fn stamp() -> MsqStamp {
    MsqStamp {
        author: "AUTHOR".to_string(),
        write_date: "DATE".to_string(),
    }
}

#[allow(clippy::too_many_arguments)]
fn table_record(
    kind: &str,
    name: &str,
    edit: &TuneEdit,
    tune: &EcuTune,
    selection: TuneSelection,
    interpolated: Option<bool>,
) -> Value {
    let change = edit.preview(selection);
    let write = edit.encode(tune);

    let mut values = Vec::new();
    for row in 0..edit.rows() {
        for column in 0..edit.columns() {
            values.push(bits(edit.get(column, row)));
        }
    }

    json!({
        "kind": kind,
        "name": name,
        "interpolated": interpolated,
        "changed": edit.changed_count(),
        "has_changes": edit.has_changes(),
        "step": bits(edit.step()),
        "preview": {
            "cells": change.cells,
            "changed": change.changed,
            "from_low": bits(change.from_low),
            "from_high": bits(change.from_high),
            "to_low": bits(change.to_low),
            "to_high": bits(change.to_high),
            "delta_low": bits(change.delta_low),
            "delta_high": bits(change.delta_high),
            "percent": bits(change.percent()),
            "uniform": change.uniform(),
        },
        "values": values,
        "write": write.as_ref().map(write_record),
    })
}

/// Changes a handful of settings of each kind and records what would be sent.
fn settings_record(tune: &EcuTune) -> Value {
    let mut edit = TuneSettingsEdit::new(tune);
    let mut results = Vec::new();

    let (mut scalars, mut bit_fields, mut texts) = (0, 0, 0);

    for constant in &tune.layout().constants {
        if !constant.on_controller() {
            continue;
        }
        if tune.constant(&constant.name).is_none() {
            continue;
        }

        if constant.is_text {
            texts += 1;
            if texts > 2 {
                continue;
            }
            let ok = edit.set_text(tune, &constant.name, "OLV");
            results.push(json!({ "name": constant.name, "kind": "text", "ok": ok }));
        } else if constant.is_bit_field() {
            bit_fields += 1;
            if bit_fields > 6 {
                continue;
            }
            let target = if constant.has_options() {
                1.min(constant.options.len() as i32 - 1)
            } else {
                1
            };
            let ok = edit.set(tune, &constant.name, f64::from(target), 0);
            results.push(json!({ "name": constant.name, "kind": "bits", "target": target, "ok": ok }));
        } else if !constant.is_array() {
            scalars += 1;
            if scalars > 8 {
                continue;
            }
            let target = if constant.has_range() {
                (constant.low + constant.high) / 2.0
            } else {
                edit.original(tune, &constant.name, 0) + 1.0
            };
            let ok = edit.set(tune, &constant.name, target, 0);
            results.push(json!({ "name": constant.name, "kind": "scalar", "target": bits(target), "ok": ok }));
        }
    }

    // One that is refused, so the refusal is on record.
    let refused = edit.set(tune, "noSuchSettingAnywhere", 1.0, 0);

    json!({
        "kind": "settings",
        "results": results,
        "refused": refused,
        "changed": edit.changed_count(),
        "changes": edit.changes().iter().map(|c| json!({
            "name": c.name,
            "was": bits(c.was),
            "now": bits(c.now),
        })).collect::<Vec<_>>(),
        "writes": edit.writes(tune).iter().map(write_record).collect::<Vec<_>>(),
        "bytes": edit.bytes_to_write(tune),
        "pages": edit.pages_to_write(tune),
    })
}

fn describe(corpus: &Path, file: &str, ini: &str, other_file: &str) -> Value {
    let one = load_against(corpus, file, ini);
    let other = load_against(corpus, other_file, ini);
    let ini_text = tuning_text::read(&corpus.join("definitions").join(ini)).expect("ini");

    let tune = &one.load.tune;
    let signature = one.msq.signature.as_str();

    // ----- the page bytes ----------------------------------------------------------------

    let load_record = json!({
        "applied": one.load.applied,
        "missing": strings(&one.load.missing),
        "rejected": strings(&one.load.rejected),
        "unknown": one.load.unknown,
        "is_complete": one.load.is_complete(),
        "looks_like_another_firmware": one.load.looks_like_another_firmware(),
        "summary": one.load.summary(),
        "pages": tune.pages().iter().map(|p| hex(p)).collect::<Vec<_>>(),
    });

    // ----- the .msq written back ---------------------------------------------------------

    let with_source = msq_writer::write_stamped(
        tune,
        signature,
        Some(&one.msq.symbols),
        COMMENT,
        Some(&one.msq),
        &stamp(),
    );
    let bare = msq_writer::write_stamped(tune, signature, None, "", None, &stamp());

    let again = msq_apply::load(&one.layout, &MsqFile::read(&with_source).expect("reads back"), None);
    let mut differing = 0;
    for (p, page) in tune.pages().iter().enumerate() {
        for (b, byte) in page.iter().enumerate() {
            if again.tune.pages()[p][b] != *byte {
                differing += 1;
            }
        }
    }

    let saved: Vec<u8> = if with_source.chars().all(|c| (c as u32) <= 0xFF) {
        with_source.chars().map(|c| c as u32 as u8).collect()
    } else {
        with_source
            .replace("encoding=\"ISO-8859-1\"", "encoding=\"UTF-8\"")
            .into_bytes()
    };

    let msq_record = json!({
        "with_source": text_record(&with_source),
        "bare": text_record(&bare),
        "round_trip_differing_bytes": differing,
        "round_trip_applied": again.applied,
        "round_trip_complete": again.is_complete(),
        "saved_hash": fnv(&saved),
    });

    // ----- compare -----------------------------------------------------------------------

    let theirs = &other.load.tune;
    let compare_record = json!({
        "other": other_file,
        "with_self": tune_compare::compare(tune, tune).iter().map(difference).collect::<Vec<_>>(),
        "with_other": tune_compare::compare(tune, theirs).iter().map(difference).collect::<Vec<_>>(),
        "other_with_this": tune_compare::compare(theirs, tune).iter().map(difference).collect::<Vec<_>>(),
    });

    // ----- restore -----------------------------------------------------------------------

    let restore_record = json!({
        "other": other_file,
        "plan": plan_record(&tune_restore::plan(tune, &other.msq, signature)),
        "onto_itself": plan_record(&tune_restore::plan(tune, &one.msq, signature)),
        "mismatched": plan_record(&tune_restore::plan(tune, &other.msq, "some other firmware")),
    });

    // ----- scripted edits ----------------------------------------------------------------

    let mut edits: Vec<Value> = Vec::new();

    let definitions = table_editor_reader::read(&ini_text, Some(&one.msq.symbols));
    let mut tables = 0;
    for definition in &definitions {
        let Some(table) = tune.table(
            &definition.title,
            &definition.values,
            &definition.x_bins,
            &definition.y_bins,
        ) else {
            continue;
        };
        tables += 1;
        if tables > 4 {
            break;
        }

        let constant = tune.constant(&definition.values).cloned();
        let (c, r) = (table.columns() as i32, table.rows() as i32);
        let all = TuneSelection::new(0, 0, c - 1, r - 1);

        let mut scaled = TuneEdit::new(table.clone(), constant.clone());
        scaled.scale(all, 5.0);
        edits.push(table_record(
            "table_scale_5_percent",
            &definition.values,
            &scaled,
            tune,
            all,
            None,
        ));

        let mut nudged = TuneEdit::new(table.clone(), constant.clone());
        let cell = TuneSelection::cell(1.min(c - 1), 1.min(r - 1));
        let step = nudged.step();
        nudged.add(cell, step * 3.0);
        edits.push(table_record(
            "table_nudge_cell",
            &definition.values,
            &nudged,
            tune,
            cell,
            None,
        ));

        let mut smoothed = TuneEdit::new(table.clone(), constant.clone());
        smoothed.scale(TuneSelection::new(0, 0, c - 1, 0), 10.0);
        smoothed.scale(TuneSelection::new(0, r - 1, c - 1, r - 1), -10.0);
        let interpolated = smoothed.interpolate(all);
        edits.push(table_record(
            "table_interpolate",
            &definition.values,
            &smoothed,
            tune,
            all,
            Some(interpolated),
        ));

        let mut overshoot = TuneEdit::new(table, constant);
        overshoot.set(all, 1e9);
        edits.push(table_record(
            "table_set_huge",
            &definition.values,
            &overshoot,
            tune,
            all,
            None,
        ));
    }

    let mut curves = 0;
    for curve in tune_dialogs::read_curves(&ini_text, Some(&one.msq.symbols)).values() {
        let Some(mut edit) = TuneCurveEdit::for_curve(curve, tune) else {
            continue;
        };
        curves += 1;
        if curves > 4 {
            break;
        }

        let last = edit.count() as i32 - 1;
        let mid = (last / 2) as usize;
        edit.set_y(last / 2, edit.y(mid) * 1.1);
        edit.add_y(0, 1.0);
        edit.scale_y(last, 90.0);
        let interpolated = edit.interpolate(0, last);
        edit.set_x(last / 2, edit.x(mid) + 1.0);

        let writes = edit.encode(tune);
        edits.push(json!({
            "kind": "curve",
            "name": curve.name,
            "count": edit.count(),
            "interpolated": interpolated,
            "changed": edit.changed_count(),
            "x": edit.points().iter().map(|p| bits(p.x)).collect::<Vec<_>>(),
            "y": edit.points().iter().map(|p| bits(p.y)).collect::<Vec<_>>(),
            "writes": writes.as_ref().map(|w| w.iter().map(write_record).collect::<Vec<_>>()),
        }));
    }

    edits.push(settings_record(tune));

    json!({
        "file": file,
        "ini": ini,
        "signature": signature,
        "page_sizes": one.layout.pages.iter().map(|p| p.size).collect::<Vec<_>>(),
        "constants": one.layout.constants.len(),
        "load": load_record,
        "msq": msq_record,
        "compare": compare_record,
        "restore": restore_record,
        "edits": edits,
    })
}

#[test]
fn every_corpus_tune_is_handled_the_same_as_the_csharp_handles_it() {
    let Some(cases) = corpus_cases::<Value>("w4-tune", "tune") else {
        return;
    };
    assert!(!cases.is_empty());

    let corpus = corpus_dir();
    let mut compared = 0usize;
    let mut failures = Vec::new();

    for case in &cases {
        let file = case["file"].as_str().expect("file");

        if let Some(error) = case.get("error") {
            failures.push(format!("{file}: the C# threw {error}: {}", case["message"]));
            continue;
        }

        let Some(ini) = case.get("ini").and_then(Value::as_str) else {
            // No definition in the corpus fits it, so there was nothing to apply it to.
            continue;
        };

        let other = case["compare"]["other"].as_str().expect("other");
        let got = strip(describe(&corpus, file, ini, other));

        match first_difference(file, &got, case) {
            None => compared += 1,
            Some(difference) => failures.push(difference),
        }
    }

    eprintln!("{compared} of {} corpus tunes compared whole with the C#", cases.len());
    assert!(
        failures.is_empty(),
        "{} of {} tunes differ from the C#:\n{}",
        failures.len(),
        cases.len(),
        failures.join("\n")
    );
    assert!(compared > 0, "no tune had a definition to be applied to");
}
