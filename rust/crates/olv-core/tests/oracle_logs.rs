//! Every log in the local corpus, read through `logs::log_reader_factory::load`,
//! against what the C# `LogReaderFactory` made of the same file
//! (rust/oracle/expected/corpus/w2-logs/logs.json).
//!
//! Everything is compared: the document's facts, and per channel its statistics
//! and the FNV-1a hash of every f32 sample, so one sample decoded differently
//! fails the log. A file the C# refuses must be refused here with the same text.
//! Returns early when this machine has no corpus.

use olv_core::Error;
use olv_core::logs::log_reader_factory;
use olv_oracle::{close, corpus_cases, corpus_dir, fnv1a_f32, number};
use serde::Deserialize;
use serde_json::Value;
use std::time::UNIX_EPOCH;

#[derive(Deserialize)]
struct Case {
    file: String,
    error: Option<String>,
    message: Option<String>,
    format_name: Option<String>,
    sample_count: Option<usize>,
    duration: Option<Value>,
    median_sample_interval: Option<Value>,
    signature: Option<String>,
    capture_info: Option<String>,
    recorded_at: Option<String>,
    has_embedded_tune: Option<bool>,
    unreadable_tune: Option<String>,
    time_channel: Option<String>,
    markers: Option<Vec<Marker>>,
    channels: Option<Vec<Chan>>,
}

#[derive(Deserialize)]
struct Marker {
    time: Value,
    text: String,
}

#[derive(Deserialize)]
struct Chan {
    name: String,
    units: String,
    digits: i32,
    length: usize,
    min: Value,
    max: Value,
    min_index: i64,
    max_index: i64,
    is_flat: bool,
    hash: String,
    first: Vec<Value>,
}

#[test]
fn every_corpus_log_reads_as_the_csharp_does() {
    let Some(cases) = corpus_cases::<Case>("w2-logs", "logs") else {
        return;
    };
    assert!(!cases.is_empty());

    let dir = corpus_dir().join("logs");
    let mut failures = Vec::new();
    let mut matched = 0;

    for case in &cases {
        let path = dir.join(&case.file);
        if !path.exists() {
            // The expectations came from a different corpus; nothing to compare.
            return;
        }
        let result = std::panic::catch_unwind(|| check(case, &path));
        match result {
            Ok(Ok(())) => matched += 1,
            Ok(Err(why)) => failures.push(format!("{}: {why}", case.file)),
            Err(_) => failures.push(format!("{}: panicked", case.file)),
        }
    }

    assert!(
        failures.is_empty(),
        "{matched} matched; failures:\n{}",
        failures.join("\n")
    );
    assert_eq!(matched, cases.len());
}

fn check(case: &Case, path: &std::path::Path) -> Result<(), String> {
    let loaded = log_reader_factory::load(path);

    if case.error.is_some() {
        let Err(e) = loaded else {
            return Err("the C# refused it and the Rust read it".to_string());
        };
        let expected = case.message.as_deref().unwrap_or("");
        let got = e.to_string();
        return if got == expected && matches!(e, Error::LogFormat(_) | Error::Io(_)) {
            Ok(())
        } else {
            Err(format!("refused with {got:?}, C# said {expected:?}"))
        };
    }

    let log = loaded.map_err(|e| format!("refused: {e}"))?;

    macro_rules! eq {
        ($got:expr, $want:expr, $what:expr) => {
            if $got != $want {
                return Err(format!("{}: got {:?}, C# gave {:?}", $what, $got, $want));
            }
        };
    }

    eq!(Some(log.format_name()), case.format_name.as_deref(), "format_name");
    eq!(Some(log.sample_count()), case.sample_count, "sample_count");
    eq!(log.signature(), case.signature.as_deref(), "signature");
    eq!(log.capture_info(), case.capture_info.as_deref(), "capture_info");
    eq!(
        Some(log.embedded_tune().is_some()),
        case.has_embedded_tune,
        "has_embedded_tune"
    );
    eq!(
        log.unreadable_tune(),
        case.unreadable_tune.as_deref(),
        "unreadable_tune"
    );
    eq!(Some(log.time().name()), case.time_channel.as_deref(), "time_channel");

    let tol = |a: f64, e: &Value, what: &str| -> Result<(), String> {
        let want = number(e);
        if close(a, want, 1e-12, 0.0) || a == want {
            Ok(())
        } else {
            Err(format!("{what}: got {a}, C# gave {want}"))
        }
    };
    tol(log.duration(), case.duration.as_ref().unwrap(), "duration")?;
    tol(
        log.median_sample_interval(),
        case.median_sample_interval.as_ref().unwrap(),
        "median_sample_interval",
    )?;

    // The instant, not the offset it was written with.
    let want_at = case.recorded_at.as_deref().map(|s| {
        time::OffsetDateTime::parse(s, &time::format_description::well_known::Rfc3339)
            .unwrap_or_else(|e| panic!("{s}: {e}"))
            .unix_timestamp_nanos()
    });
    let got_at = log
        .recorded_at()
        .map(|t| t.duration_since(UNIX_EPOCH).expect("after the epoch").as_nanos() as i128);
    eq!(got_at, want_at, "recorded_at");

    let markers = case.markers.as_ref().unwrap();
    eq!(log.markers().len(), markers.len(), "marker count");
    for (g, w) in log.markers().iter().zip(markers) {
        eq!(g.text.as_str(), w.text.as_str(), "marker text");
        tol(g.time, &w.time, "marker time")?;
    }

    let channels = case.channels.as_ref().unwrap();
    eq!(log.channels().len(), channels.len(), "channel count");
    for (g, w) in log.channels().iter().zip(channels) {
        let n = &w.name;
        eq!(g.name(), w.name.as_str(), "channel name");
        eq!(g.units(), w.units.as_str(), format!("{n} units"));
        eq!(g.digits(), w.digits, format!("{n} digits"));
        eq!(g.len(), w.length, format!("{n} length"));
        tol(g.min(), &w.min, &format!("{n} min"))?;
        tol(g.max(), &w.max, &format!("{n} max"))?;
        eq!(
            g.min_index().map_or(-1, |i| i as i64),
            w.min_index,
            format!("{n} min_index")
        );
        eq!(
            g.max_index().map_or(-1, |i| i as i64),
            w.max_index,
            format!("{n} max_index")
        );
        eq!(g.is_flat(), w.is_flat, format!("{n} is_flat"));
        eq!(
            format!("{:016x}", fnv1a_f32(g.samples())),
            w.hash.to_lowercase(),
            format!("{n} sample hash")
        );
        for (i, first) in w.first.iter().enumerate() {
            // The C# writes a float[] as the float's own shortest text, so compare as floats.
            let want = number(first);
            let got = g.samples()[i];
            let ok = (got.is_nan() && want.is_nan()) || got == want as f32;
            if !ok {
                return Err(format!("{n} first[{i}]: got {got}, C# gave {want}"));
            }
        }
    }

    Ok(())
}
