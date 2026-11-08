//! `insights::log_insights::from` over every log in the local corpus, against what the C#
//! `LogInsights.From` said of the same file
//! (rust/oracle/expected/corpus/w4-insights/insights.json).
//!
//! Every finding is compared exactly: kind, severity, title, detail, the evidence text with
//! the arithmetic in it, the sample count, and the one-line rendering. Order matters too, as
//! the findings come out worst first. Returns early when this machine has no corpus.

use olv_core::insights::log_insights;
use olv_core::logs::log_reader_factory;
use olv_oracle::{corpus_cases, corpus_dir};
use serde::Deserialize;

#[derive(Deserialize)]
struct Case {
    file: String,
    error: Option<String>,
    message: Option<String>,
    findings: Option<Vec<Finding>>,
}

#[derive(Deserialize)]
struct Finding {
    kind: String,
    severity: String,
    title: String,
    detail: String,
    evidence: String,
    samples: i32,
    text: String,
}

#[test]
fn every_corpus_log_is_told_the_same_things_as_the_csharp_tells() {
    let Some(cases) = corpus_cases::<Case>("w4-insights", "insights") else {
        return;
    };
    assert!(!cases.is_empty());

    let mut compared = 0usize;
    let mut failures = Vec::new();

    for case in &cases {
        if let Some(error) = &case.error {
            failures.push(format!(
                "{}: the C# threw {error}: {}",
                case.file,
                case.message.as_deref().unwrap_or("")
            ));
            continue;
        }

        let path = corpus_dir().join("logs").join(&case.file);
        let log = log_reader_factory::load(&path).unwrap_or_else(|e| panic!("{}: {e}", case.file));
        let got = log_insights::from(&log);
        let want = case.findings.as_ref().expect("findings");

        if got.len() != want.len() {
            failures.push(format!(
                "{}: {} findings, the C# made {}",
                case.file,
                got.len(),
                want.len()
            ));
            continue;
        }

        for (n, (g, w)) in got.iter().zip(want).enumerate() {
            let at = format!("{} finding {n} ({})", case.file, w.kind);
            let checks: [(&str, String, &str); 7] = [
                ("kind", g.topic.clone(), &w.kind),
                ("severity", g.level.name().to_string(), &w.severity),
                ("title", g.title.clone(), &w.title),
                ("detail", g.detail.clone(), &w.detail),
                ("evidence", g.evidence.clone(), &w.evidence),
                ("samples", g.samples.to_string(), &w.samples.to_string()),
                ("text", g.to_string(), &w.text),
            ];
            for (what, have, expected) in checks {
                if have != expected {
                    failures.push(format!("{at}: {what}\n  rust: {have}\n  c#:   {expected}"));
                }
            }
            compared += 1;
        }
    }

    eprintln!("{} logs, {compared} findings compared", cases.len());
    assert!(
        failures.is_empty(),
        "{} mismatches:\n{}",
        failures.len(),
        failures.join("\n")
    );
}
