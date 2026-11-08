//! The analysis package (histogram tables, scatter, VE analysis, wideband delay, power
//! estimate, CSV export, log comparison) against what the C# computes.
//!
//! The C# (`tools/OpenLogViewer.Oracle/Emitters/W3Analysis.cs`) flattens every result to a
//! canonical key/value text: doubles as IEEE bits, long text as an FNV-1a hash. This builds the
//! same text from the same inputs and compares key by key. Two sets: `synthetic` (logs built in
//! code, always runs) and `analysis` (the local corpus; returns early without one).

use olv_core::analysis::csv_export;
use olv_core::analysis::histogram_table::{HistogramInputs, HistogramStatistic, HistogramTable};
use olv_core::analysis::log_comparison;
use olv_core::analysis::power_estimate::{self, EngineSpec};
use olv_core::analysis::scatter_plot::{ScatterBins, ScatterPlot};
use olv_core::analysis::ve_analysis::{self, VeAnalysisResult, VeAnalysisSettings};
use olv_core::analysis::wideband_delay::{self, DelaySearchResult};
use olv_core::base::tuning_math::Fuel;
use olv_core::channels::channel_roles::{self, ChannelRole};
use olv_core::filters::log_filter::{FilterComparison, LogFilter, SampleFilter, SampleMask};
use olv_core::logs::log_reader_factory;
use olv_core::model::tune_axes::read_tables;
use olv_core::model::{Array2, LogChannel, LogDocument, TuneAxis, TuneTable, tuning_text};
use olv_core::{Error, Result};
use olv_oracle::{cases, corpus_cases, corpus_dir};
use serde::Deserialize;
use std::path::PathBuf;
use std::sync::Arc;

#[derive(Deserialize)]
struct Item {
    key: String,
    value: String,
}

#[derive(Deserialize)]
struct CorpusCase {
    file: String,
    error: Option<String>,
    tune: Option<String>,
    other: Option<String>,
    items: Option<Vec<Item>>,
}

#[derive(Deserialize)]
struct SyntheticCase {
    seed: i64,
    items: Vec<Item>,
}

type Items = Vec<(String, String)>;

// ---- canonical text -------------------------------------------------------------------------

fn b(v: f64) -> String {
    // .NET's double.NaN has the sign bit set; Rust's does not. Any NaN is the same value.
    if v.is_nan() {
        return "fff8000000000000".to_string();
    }
    format!("{:016x}", v.to_bits())
}

fn tf(v: bool) -> &'static str {
    if v { "True" } else { "False" }
}

fn fnv(s: &str) -> String {
    let mut h: u64 = 0xcbf29ce484222325;
    for byte in s.as_bytes() {
        h ^= u64::from(*byte);
        h = h.wrapping_mul(0x100000001b3);
    }
    format!("h:{h:016x}")
}

/// Short text as is, long text (over 300 UTF-16 units) as its hash.
fn fin(s: String) -> String {
    if s.encode_utf16().count() <= 300 { s } else { fnv(&s) }
}

fn opt(v: Option<f64>) -> String {
    v.map_or_else(|| "n".to_string(), b)
}

fn join<T>(v: impl IntoIterator<Item = T>, f: impl Fn(T) -> String) -> String {
    v.into_iter().map(f).collect::<Vec<_>>().join(",")
}

fn dig(t: &HistogramTable) -> String {
    let mut s = format!(
        "c{} r{} fromTune={} delta={} dev={}",
        t.columns,
        t.rows,
        tf(t.from_tune),
        tf(t.is_delta()),
        tf(t.shows_deviation())
    );
    s += &format!(" cc={}", join(&t.column_centers, |v| b(*v)));
    s += &format!(" rc={}", join(&t.row_centers, |v| b(*v)));
    s += " v=";
    for c in 0..t.columns {
        for r in 0..t.rows {
            s += &opt(t.values[(c, r)]);
            s.push(',');
        }
    }
    s += " n=";
    for c in 0..t.columns {
        for r in 0..t.rows {
            s += &format!("{},", t.counts[(c, r)]);
        }
    }
    s += &format!(
        " min={} max={} maxdev={} maxc={}",
        b(t.min_value),
        b(t.max_value),
        b(t.max_deviation()),
        t.max_count
    );
    s += &format!(
        " samples={} pop={} first={} last={} empty={}",
        t.sample_count,
        t.populated_cells,
        t.first_sample,
        t.last_sample,
        tf(t.is_empty())
    );
    s += &format!(
        " name={} digits={}",
        t.display_name.as_deref().unwrap_or("null"),
        t.display_digits.map_or_else(|| "null".to_string(), |d| d.to_string())
    );
    s += " fmt=";
    for c in 0..t.columns {
        for r in 0..t.rows {
            s += &t.format_cell(c, r);
            s.push('|');
        }
    }
    fin(s)
}

fn grid_text<T>(columns: usize, rows: usize, f: impl Fn(usize, usize) -> T, g: impl Fn(T) -> String) -> String {
    let mut s = String::new();
    for c in 0..columns {
        for r in 0..rows {
            s += &g(f(c, r));
            s.push(',');
        }
    }
    s
}

fn dig_ve(r: &VeAnalysisResult) -> String {
    let (cols, rows) = (r.table.columns(), r.table.rows());
    let mut s = format!(
        "cells={} thin={} used={} largest={} empty={} problem={}",
        r.cells_suggested,
        r.cells_thin,
        r.samples_used,
        b(r.largest_change_percent),
        tf(r.is_empty()),
        r.problem.as_deref().unwrap_or("null")
    );
    s += &format!(" s={}", grid_text(cols, rows, |c, r2| r.suggested[(c, r2)], opt));
    s += &format!(" ch={}", grid_text(cols, rows, |c, r2| r.change_percent[(c, r2)], opt));
    s += &format!(
        " n={}",
        grid_text(cols, rows, |c, r2| r.counts[(c, r2)], |v| v.to_string())
    );
    s += &format!(" w={}", grid_text(cols, rows, |c, r2| r.weight[(c, r2)], b));
    fin(s)
}

fn dig_delay(d: &DelaySearchResult) -> String {
    let mut s = format!(
        "best={} bestS={} imp={} scored={} low={} high={} none={} precise={} problem={}",
        d.best_samples,
        b(d.best_seconds),
        b(d.improvement_percent),
        d.samples_scored,
        b(d.low_seconds),
        b(d.high_seconds),
        tf(d.none_is_plausible()),
        tf(d.is_precise()),
        d.problem.as_deref().unwrap_or("null")
    );
    s += " curve=";
    for c in &d.curve {
        s += &format!("{}:{}:{},", c.samples, b(c.seconds), b(c.disagreement));
    }
    fin(s)
}

fn dig_power(p: &power_estimate::PowerEstimateResult) -> String {
    let mut s = String::new();
    for m in &p.methods {
        s += &format!("M[{}|{}]", m.name, m.basis);
        for c in &m.channels {
            s += &format!("C[{}|{}|{}|{}]", c.name, c.units, c.digits, c.expression);
        }
    }
    for u in &p.unavailable {
        s += &format!("U[{}|{}]", u.name, u.needs);
    }
    fin(s)
}

fn dig_scatter(p: &ScatterPlot, bins: &ScatterBins) -> String {
    let mut s = format!(
        "count={} dropped={} filtered={} delta={}",
        p.count,
        p.dropped,
        p.filtered,
        tf(p.is_delta())
    );
    s += &format!(
        " x={},{} y={},{} z={},{} dev={}",
        b(p.x_min),
        b(p.x_max),
        b(p.y_min),
        b(p.y_max),
        b(p.z_min),
        b(p.z_max),
        b(p.max_deviation())
    );
    s += &format!(" xs={}", join(p.xs.iter().take(p.count), |v| b(*v)));
    s += &format!(" ys={}", join(p.ys.iter().take(p.count), |v| b(*v)));
    s += &format!(" zs={}", join(p.zs.iter().take(p.count), |v| b(*v)));
    s += &format!(" ss={}", join(p.samples.iter().take(p.count), |v| v.to_string()));
    s += &format!(
        " bins={}x{} occ={} busy={} mean={},{} color={},{} clips={},{} extent={}",
        bins.columns,
        bins.rows,
        bins.occupied,
        bins.busiest,
        b(bins.mean_min),
        b(bins.mean_max),
        b(bins.color_low),
        b(bins.color_high),
        tf(bins.clips_low()),
        tf(bins.clips_high()),
        b(bins.mean_extent())
    );
    s += &format!(" cnt={}", join(&bins.counts, |v| v.to_string()));
    s += &format!(" means={}", join(&bins.means, |v| b(*v)));
    s += &format!(" lo={}", join(&bins.lowest, |v| b(*v)));
    s += &format!(" hi={}", join(&bins.highest, |v| b(*v)));
    fnv(&s)
}

fn csv(write: impl FnOnce(&mut Vec<u8>) -> Result<()>) -> Result<String> {
    let mut buf = Vec::new();
    write(&mut buf)?;
    Ok(fnv(&String::from_utf8(buf).expect("csv is utf-8")))
}

fn thrown(e: &Error) -> String {
    let kind = match e {
        Error::Argument(_) => "ArgumentException",
        Error::InvalidOperation(_) => "InvalidOperationException",
        _ => "Exception",
    };
    format!("throws:{kind}")
}

// ---- the same analysis the emitter runs -----------------------------------------------------

fn items(log: &LogDocument, tune: Option<&TuneTable>, other: Option<&LogDocument>) -> Items {
    let mut out: Items = Vec::new();
    let mut add = |key: &str, f: &dyn Fn() -> Result<String>| {
        let value = f().unwrap_or_else(|e| thrown(&e));
        out.push((key.to_string(), value));
    };

    let find = |role| channel_roles::find(log, role).cloned();
    let rpm = find(ChannelRole::EngineSpeed);
    let map = find(ChannelRole::ManifoldPressure);
    let afr = find(ChannelRole::Mixture);
    let target = find(ChannelRole::MixtureTarget);
    let n = log.sample_count() as isize;
    let name = |c: &Option<Arc<LogChannel>>| c.as_ref().map_or(String::new(), |c| c.name().to_string());

    add("roles", &|| {
        Ok(format!(
            "{}|{}|{}|{}|n={n}",
            name(&rpm),
            name(&map),
            name(&afr),
            name(&target)
        ))
    });

    add("power.default", &|| {
        Ok(dig_power(&power_estimate::estimate(log, &EngineSpec::default())))
    });
    add("power.variant", &|| {
        let spec = EngineSpec {
            litres: 3.0,
            cylinders: 6,
            fuel: Fuel::E85,
            batch_injection: true,
            fuel_pressure_is_differential: true,
            drivetrain_loss_percent: 15.0,
            injector_cc_per_minute: 850.0,
            lambda: 0.8,
            volumetric_efficiency: 88.0,
            ..EngineSpec::default()
        };
        Ok(dig_power(&power_estimate::estimate(log, &spec)))
    });

    if let Some(other) = other {
        add("compare.overlap", &|| {
            let o = log_comparison::compare(log, other);
            Ok(fin(format!(
                "{}/{}/{}/{}",
                o.shared.join(","),
                o.only_in_first.join(","),
                o.only_in_second.join(","),
                o.summary()
            )))
        });
    }

    add("csv.log", &|| {
        let chans: Vec<Arc<LogChannel>> = log.channels().iter().rev().take(6).cloned().collect();
        csv(|w| csv_export::write_log(w, log, &chans, 3, 52))
    });

    let (Some(rpm), Some(map), Some(afr)) = (rpm, map, afr) else {
        return out;
    };

    let mask = SampleFilter::build(
        log,
        &[LogFilter::new("rpm", rpm.name(), FilterComparison::Above).with_low(1000.0)],
    );
    let mask = &mask;
    add("mask", &|| {
        let bits: String = mask.accepted().iter().map(|a| if *a { '1' } else { '0' }).collect();
        Ok(format!(
            "pass={} total={} applied={} h={}",
            mask.pass_count(),
            mask.total(),
            tf(mask.filters_applied()),
            fnv(&bits)
        ))
    });

    let inputs = || HistogramInputs::new(&rpm, &map, &afr);
    let stats = [
        ("Mean", HistogramStatistic::Mean),
        ("Min", HistogramStatistic::Min),
        ("Max", HistogramStatistic::Max),
        ("Count", HistogramStatistic::Count),
    ];

    for (tag, stat) in stats {
        add(&format!("auto.{tag}"), &|| {
            Ok(dig(&HistogramTable::build(inputs(), 12, 9, 0, n - 1, stat)?))
        });
        add(&format!("auto.masked.{tag}"), &|| {
            Ok(dig(&HistogramTable::build(
                inputs().with_mask(Some(mask)),
                12,
                9,
                0,
                n - 1,
                stat,
            )?))
        });
    }
    add("auto.window", &|| {
        Ok(dig(&HistogramTable::build(
            inputs().with_mask(Some(mask)),
            8,
            8,
            n / 3,
            n / 2,
            HistogramStatistic::Mean,
        )?))
    });
    add("auto.reversed", &|| {
        Ok(dig(&HistogramTable::build(
            inputs(),
            5,
            5,
            n / 2,
            n / 3,
            HistogramStatistic::Max,
        )?))
    });
    if let Some(target) = &target {
        add("auto.delta", &|| {
            Ok(dig(&HistogramTable::build(
                inputs().with_mask(Some(mask)).with_z_compare(Some(target)),
                10,
                10,
                0,
                n - 1,
                HistogramStatistic::Mean,
            )?))
        });
    }

    add("scatter", &|| {
        let p = ScatterPlot::build(&rpm, &map, &afr, 0, n - 1, Some(mask), target.as_ref());
        Ok(dig_scatter(&p, &p.bin(40, 30)?))
    });
    add("scatter.plain", &|| {
        let p = ScatterPlot::build(&rpm, &map, &afr, 0, n - 1, None, None);
        Ok(dig_scatter(&p, &p.bin(7, 5)?))
    });
    add("csv.points", &|| {
        let p = ScatterPlot::build(&rpm, &map, &afr, n / 4, n / 2, Some(mask), target.as_ref());
        csv(|w| csv_export::write_points(w, &p))
    });

    add("grid", &|| {
        let g = ve_analysis::grid_from(&rpm, &map, 10, 8, 0, n - 1, Some(mask))?;
        Ok(fin(format!(
            "{}|{}|{}|{}|{}|{}",
            g.name,
            join(&g.x.breakpoints, |v| b(*v)),
            join(&g.y.breakpoints, |v| b(*v)),
            g.x.units,
            g.y.units,
            g.units
        )))
    });
    add("ve.grid", &|| {
        let g = ve_analysis::grid_from(&rpm, &map, 10, 8, 0, n - 1, Some(mask))?;
        let t = target.as_ref().unwrap_or(&afr);
        Ok(dig_ve(&ve_analysis::analyse(
            &g,
            &rpm,
            &map,
            &afr,
            t,
            0,
            n - 1,
            Some(mask),
            None,
        )?))
    });

    let Some(tune) = tune else {
        return out;
    };

    let (xs, ys) = (&tune.x.breakpoints, &tune.y.breakpoints);
    for (tag, stat) in stats {
        add(&format!("tune.{tag}"), &|| {
            Ok(dig(&HistogramTable::build_on_breakpoints(
                inputs(),
                xs,
                ys,
                0,
                n - 1,
                stat,
            )?))
        });
        add(&format!("tune.masked.{tag}"), &|| {
            Ok(dig(&HistogramTable::build_on_breakpoints(
                inputs().with_mask(Some(mask)),
                xs,
                ys,
                0,
                n - 1,
                stat,
            )?))
        });
    }

    let busy = HistogramTable::build_on_breakpoints(
        inputs().with_mask(Some(mask)),
        xs,
        ys,
        0,
        n - 1,
        HistogramStatistic::Mean,
    )
    .expect("tune axes are not empty");
    add("trace", &|| {
        let (mut bc, mut br, mut best) = (0, 0, -1isize);
        for c in 0..busy.columns {
            for r in 0..busy.rows {
                if busy.counts[(c, r)] as isize > best {
                    best = busy.counts[(c, r)] as isize;
                    bc = c;
                    br = r;
                }
            }
        }

        let hits = busy.samples_in(bc, br);
        let visits = busy.visits_to(bc, br, 2);
        let longest = busy.longest_visit_to(bc, br);
        let v = busy.visited_by(n / 3, -5 + n / 2);
        let cell = usize::try_from(n / 2).ok().and_then(|s| busy.cell_of(s));
        Ok(fin(format!(
            "{bc},{br}|{}|{}|{}|{}-{}|cells={} samples={} outside={} counts={}|cell={},{}",
            hits.len(),
            fnv(&join(&hits, |v| v.to_string())),
            join(&visits, |(f, l)| format!("{f}-{l}")),
            longest.map_or(String::new(), |l| l.0.to_string()),
            longest.map_or(String::new(), |l| l.1.to_string()),
            v.cells,
            v.samples,
            v.outside,
            join(v.counts.as_slice(), |c| c.to_string()),
            cell.map_or(String::new(), |c| c.0.to_string()),
            cell.map_or(String::new(), |c| c.1.to_string()),
        )))
    });
    add("csv.table", &|| csv(|w| csv_export::write_table(w, &busy)));
    add("csv.counts", &|| csv(|w| csv_export::write_table_counts(w, &busy)));
    add("csv.tune", &|| csv(|w| csv_export::write_tune_table(w, tune)));

    if let Some(target) = &target {
        add("tune.delta", &|| {
            Ok(dig(&HistogramTable::build_on_breakpoints(
                inputs().with_mask(Some(mask)).with_z_compare(Some(target)),
                xs,
                ys,
                0,
                n - 1,
                HistogramStatistic::Mean,
            )?))
        });

        let settings = [
            ("default", VeAnalysisSettings::default()),
            (
                "delay3",
                VeAnalysisSettings {
                    measurement_delay_samples: 3,
                    minimum_samples: 3,
                    ..VeAnalysisSettings::default()
                },
            ),
            (
                "raw",
                VeAnalysisSettings {
                    minimum_samples: 1,
                    confidence_samples: 0,
                    max_change_percent: 40.0,
                    authority: 0.5,
                    ..VeAnalysisSettings::default()
                },
            ),
        ];
        for (tag, s) in &settings {
            add(&format!("ve.{tag}"), &|| {
                Ok(dig_ve(&ve_analysis::analyse(
                    tune,
                    &rpm,
                    &map,
                    &afr,
                    target,
                    0,
                    n - 1,
                    Some(mask),
                    Some(s),
                )?))
            });
            add(&format!("ve.{tag}.unmasked"), &|| {
                Ok(dig_ve(&ve_analysis::analyse(
                    tune,
                    &rpm,
                    &map,
                    &afr,
                    target,
                    0,
                    n - 1,
                    None,
                    Some(s),
                )?))
            });
        }

        add("ve.tables", &|| {
            let s = VeAnalysisSettings {
                minimum_samples: 2,
                ..VeAnalysisSettings::default()
            };
            let r = ve_analysis::analyse(tune, &rpm, &map, &afr, target, 0, n - 1, Some(mask), Some(&s))?;
            let last = (n - 1) as usize;
            let change = r.as_change_table(&rpm, &map, &afr, target, 0, last, Some(mask))?;
            let suggested = r.as_suggested_table(&rpm, &map, &afr, 0, last, Some(mask))?;
            Ok(fnv(&format!("{}/{}", dig(&change), dig(&suggested))))
        });

        let interval = log.median_sample_interval();
        add("delay", &|| {
            Ok(dig_delay(&wideband_delay::find(
                tune,
                &rpm,
                &map,
                &afr,
                target,
                0,
                n - 1,
                interval,
                Some(mask),
                1.0,
            )))
        });
        add("delay.unmasked", &|| {
            Ok(dig_delay(&wideband_delay::find(
                tune,
                &rpm,
                &map,
                &afr,
                target,
                0,
                n - 1,
                interval,
                None,
                0.5,
            )))
        });
    }

    if let Some(other) = other {
        let find2 = |role| channel_roles::find(other, role).cloned();
        if let (Some(r2), Some(m2), Some(a2)) = (
            find2(ChannelRole::EngineSpeed),
            find2(ChannelRole::ManifoldPressure),
            find2(ChannelRole::Mixture),
        ) {
            add("compare.diff", &|| {
                let second = HistogramTable::build_on_breakpoints(
                    HistogramInputs::new(&r2, &m2, &a2),
                    xs,
                    ys,
                    0,
                    other.sample_count() as isize - 1,
                    HistogramStatistic::Mean,
                )?;
                let first = HistogramTable::build_on_breakpoints(inputs(), xs, ys, 0, n - 1, HistogramStatistic::Mean)?;
                let d = log_comparison::difference(&first, &second)?;
                let (both, a, bb) = log_comparison::coverage(&first, &second);
                let s1 = log_comparison::summarise(&d, 1);
                let s2 = log_comparison::summarise(&d, 5);
                let sum = |s: &log_comparison::ComparisonSummary| {
                    format!(
                        "{},{},{},{},{}",
                        s.cells,
                        b(s.mean),
                        b(s.largest),
                        b(s.at_column),
                        b(s.at_row)
                    )
                };
                Ok(fnv(&format!("{}/{both},{a},{bb}/{}/{}", dig(&d), sum(&s1), sum(&s2))))
            });
        }
    }

    out
}

// ---- comparison -----------------------------------------------------------------------------

fn compare(got: &Items, want: &[Item]) -> Vec<String> {
    let mut failures = Vec::new();
    if got.len() != want.len() {
        failures.push(format!("{} items, C# gave {}", got.len(), want.len()));
    }
    for ((gk, gv), w) in got.iter().zip(want) {
        if *gk != w.key {
            failures.push(format!("key {gk} where the C# has {}", w.key));
        } else if *gv != w.value {
            failures.push(format!("{gk}: got {gv:?}, C# gave {:?}", w.value));
        }
    }
    failures
}

// ---- synthetic ------------------------------------------------------------------------------

fn synthetic_log(seed: i64, n: usize) -> LogDocument {
    let mut state = seed * 7919;
    let mut next = move || {
        state = (state.wrapping_mul(1103515245) + 12345) & 0x7FFFFFFF;
        state as f64 / 2147483648.0
    };

    let mut time = vec![0.0; n];
    let (mut rpm, mut map, mut afr, mut target) = (vec![0.0; n], vec![0.0; n], vec![0.0; n], vec![0.0; n]);
    let (mut iat, mut pulse, mut tps) = (vec![0.0; n], vec![0.0; n], vec![0.0; n]);

    for i in 0..n {
        time[i] = i as f64 * 0.02;
        rpm[i] = (800 + ((i * 13) % 5400)) as f64 + next() * 50.0;
        map[i] = (25 + ((i * 7) % 90)) as f64 + next() * 4.0;
        let t = 14.7 - ((i % 200) as f64 / 200.0 * 2.0);
        target[i] = t;
        let delayed = if i >= 5 { target[i - 5] } else { t };
        afr[i] = delayed * (1.0 + ((next() - 0.5) * 0.1));
        iat[i] = 30.0 + next() * 10.0;
        pulse[i] = 2.0 + (map[i] / 30.0) + next();
        tps[i] = ((i * 3) % 100) as f64;
    }
    for i in (0..n).step_by(97) {
        map[i] = f64::NAN;
    }
    for i in (5..n).step_by(211) {
        afr[i] = f64::NAN;
    }

    let ch = |name: &str, units: &str, digits: i32, v: &[f64], precise: bool| {
        Arc::new(LogChannel::new(name, units, digits, v, precise))
    };
    let time_channel = ch("Time", "s", 3, &time, true);
    let channels = vec![
        Arc::clone(&time_channel),
        ch("RPM", "rpm", 0, &rpm, false),
        ch("MAP", "kPa", 1, &map, false),
        ch("AFR", "AFR", 2, &afr, false),
        ch("AFR Target", "AFR", 2, &target, false),
        ch("IAT", "°C", 0, &iat, false),
        ch("Pulse Width", "ms", 2, &pulse, false),
        ch("TPS", "%", 0, &tps, false),
    ];
    LogDocument::new("synthetic.csv", "synthetic", channels, time_channel)
}

fn synthetic_table() -> TuneTable {
    let mut values = Array2::<f64>::new(6, 5);
    for c in 0..6 {
        for r in 0..5 {
            values[(c, r)] = (40 + c * 7) as f64 + r as f64 * 3.5;
        }
    }
    let axis = |constant: &str, units: &str, bps: &[f64]| TuneAxis {
        constant: constant.to_string(),
        units: units.to_string(),
        breakpoints: bps.to_vec(),
    };
    TuneTable {
        name: "VE table 1".to_string(),
        x: axis("rpmBins", "rpm", &[1000.0, 2000.0, 3000.0, 4000.0, 5000.0, 6000.0]),
        y: axis("mapBins", "kPa", &[30.0, 50.0, 70.0, 90.0, 110.0]),
        values,
        units: "%".to_string(),
    }
}

#[test]
fn the_synthetic_logs_analyse_as_the_csharp_does() {
    let all = cases::<SyntheticCase>("w3-analysis", "synthetic");
    assert!(!all.is_empty());
    let mut failures = Vec::new();
    for case in &all {
        let log = synthetic_log(case.seed, 3000);
        let other = synthetic_log(case.seed + 10, 2500);
        let got = items(&log, Some(&synthetic_table()), Some(&other));
        for f in compare(&got, &case.items) {
            failures.push(format!("seed {}: {f}", case.seed));
        }
    }
    assert!(failures.is_empty(), "{}", failures.join("\n"));
}

// ---- corpus ---------------------------------------------------------------------------------

fn sorted_files(kind: &str) -> Vec<PathBuf> {
    let Ok(read) = std::fs::read_dir(corpus_dir().join(kind)) else {
        return Vec::new();
    };
    let mut files: Vec<PathBuf> = read.filter_map(|e| e.ok().map(|e| e.path())).collect();
    // Ordinal, as the emitter orders them.
    files.sort_by(|a, b| a.file_name().cmp(&b.file_name()));
    files
}

fn has_ext(p: &std::path::Path, ext: &str) -> bool {
    p.extension().is_some_and(|e| e.eq_ignore_ascii_case(ext))
}

#[test]
fn every_corpus_log_analyses_as_the_csharp_does() {
    let Some(all) = corpus_cases::<CorpusCase>("w3-analysis", "analysis") else {
        return;
    };
    assert!(!all.is_empty());

    let files = sorted_files("logs");
    let mut logs: Vec<PathBuf> = files.iter().filter(|p| has_ext(p, "mlg")).cloned().collect();
    logs.extend(
        files
            .iter()
            .filter(|p| has_ext(p, "csv"))
            .enumerate()
            .filter(|(i, _)| i % 9 == 0)
            .map(|(_, p)| p.clone())
            .take(10),
    );
    if logs.len() != all.len()
        || logs
            .iter()
            .zip(&all)
            .any(|(p, c)| p.file_name().unwrap().to_string_lossy() != c.file)
    {
        // The expectations came from a different corpus.
        return;
    }

    let mut tunes: Vec<(String, TuneTable)> = Vec::new();
    for path in sorted_files("tunes").into_iter().filter(|p| has_ext(p, "msq")) {
        let Ok(xml) = tuning_text::read(&path) else { continue };
        if let Some(ve) = read_tables(Some(&xml))
            .into_iter()
            .find(|t| t.name == "VE table 1")
            .filter(|t| t.columns() > 1 && t.rows() > 1)
        {
            tunes.push((path.file_name().unwrap().to_string_lossy().into_owned(), ve));
        }
    }

    let mut failures = Vec::new();
    let mut matched = 0;
    for (k, case) in all.iter().enumerate() {
        let log = match log_reader_factory::load(&logs[k]) {
            Ok(log) => log,
            Err(_) => {
                if case.error.is_some() {
                    matched += 1;
                } else {
                    failures.push(format!("{}: the Rust refused a log the C# read", case.file));
                }
                continue;
            }
        };
        if case.error.is_some() {
            failures.push(format!("{}: the C# refused it and the Rust read it", case.file));
            continue;
        }

        let other = log_reader_factory::load(&logs[(k + 1) % logs.len()]).ok();
        let tune = (!tunes.is_empty()).then(|| &tunes[k % tunes.len()]);
        if tune.map(|t| t.0.as_str()) != case.tune.as_deref() {
            failures.push(format!(
                "{}: tune {:?}, C# used {:?}",
                case.file,
                tune.map(|t| &t.0),
                case.tune
            ));
            continue;
        }
        assert_eq!(other.is_some(), case.other.is_some());

        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            items(&log, tune.map(|t| &t.1), other.as_ref())
        }));
        let Ok(got) = result else {
            failures.push(format!("{}: panicked", case.file));
            continue;
        };
        let problems = compare(&got, case.items.as_deref().unwrap_or(&[]));
        if problems.is_empty() {
            matched += 1;
        } else {
            failures.extend(problems.into_iter().map(|p| format!("{}: {p}", case.file)));
        }
    }

    assert!(
        failures.is_empty(),
        "{matched} matched; failures:\n{}",
        failures.join("\n")
    );
    assert_eq!(matched, all.len());
}

#[allow(dead_code)]
fn _unused(_: &SampleMask) {}
