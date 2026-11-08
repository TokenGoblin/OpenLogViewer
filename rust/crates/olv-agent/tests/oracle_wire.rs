//! The server's answers, compared byte for byte with the shipping C# `AgentServer`'s.
//!
//! `tools/OpenLogViewer.Oracle/Emitters/W5Agent.cs` starts the real C# server over a fixed bench
//! bridge and records, for each of 160 raw requests, the status, content type and whole body it
//! answered, and what the bridge was asked. This replays the same requests against this server,
//! over the same bench data (`support/bench.rs`), and compares all of it. That catches a field
//! renamed, a number printed differently, a character escaped differently, a null that should
//! have been omitted, a changed status, a different call reaching the bridge.
//!
//! **Deliberate differences**, each tightening and each tested on its own in `agent_server.rs`:
//! the C# answers a request naming any `Host` or `Origin`; this refuses the ones that are not
//! loopback (403), and a token in the query string is taken only on the live stream. Those cases
//! are listed in [`REFUSED_HERE`] and [`QUERY_TOKEN_REFUSED`] and checked against that instead.
//! The `detail` text of a JSON *syntax* error is `System.Text.Json`'s own wording; the common
//! ones are reproduced and the rest are compared by status and error only ([`WORDING`]).

mod support;

use std::collections::HashSet;
use std::sync::Arc;

use olv_agent::agent::agent_server::{AgentServer, AgentServerSettings};
use serde::Deserialize;
use support::bench::{Bench, needed};
use support::http::{TOKEN, next_text, raw, send, subscribe};

#[derive(Deserialize)]
struct Case {
    name: String,
    #[serde(default)]
    setup: Vec<String>,
    method: String,
    target: String,
    #[serde(default)]
    headers: Vec<Vec<String>>,
    #[serde(default)]
    body: String,
    #[serde(default)]
    status: u16,
    #[serde(default)]
    content_type: String,
    #[serde(default)]
    response: String,
    #[serde(default)]
    calls: Vec<String>,
    #[serde(default)]
    messages: Vec<String>,
}

/// Requests the C# answers 200 that this server refuses with 403, on purpose.
const REFUSED_HERE: [&str; 2] = ["host other", "origin foreign"];

/// Requests the C# answers 200 on the strength of a token in the query string, which only the live
/// stream accepts here: refused with 401 (intended divergence, review item 7).
const QUERY_TOKEN_REFUSED: [&str; 2] = ["token in query", "bad header good query"];

/// Syntax errors whose wording `System.Text.Json` owns and this does not copy to the letter.
///
/// - `json open array value` and `json deep`: `System.Text.Json` converts as it reads, so it
///   reports the property that cannot be an array (or the top level that cannot be a list)
///   before it reaches the syntax error further on. This parses the whole document first, so it
///   reports the syntax error. Both are 400 with the same `error`; only the detail differs.
const WORDING: [&str; 2] = ["json open array value", "json deep"];

fn start(setup: &[String]) -> (AgentServer, Arc<Bench>) {
    let bench = Arc::new(Bench::new());

    if setup.iter().any(|s| s == "armed") {
        bench.arm();
    }
    if setup.iter().any(|s| s == "rich") {
        bench.rich.store(true, std::sync::atomic::Ordering::SeqCst);
    }
    if setup.iter().any(|s| s == "events2") {
        bench.extra_event.store(true, std::sync::atomic::Ordering::SeqCst);
    }
    if setup.iter().any(|s| s == "brief") {
        bench.with(|s| {
            s.brief = "# The E28\n\n## Still open\n\n### lean-under-load \u{2014} Lean above 150 kPa\n".to_string();
        });
    }
    if setup.iter().any(|s| s == "needed") {
        bench.with(|s| s.needed = Some(needed()));
    }

    let mut server = AgentServer::new(
        bench.clone(),
        AgentServerSettings {
            port: 0,
            ..AgentServerSettings::new(TOKEN)
        },
    )
    .expect("a server");
    server.start().expect("starts");

    (server, bench)
}

#[test]
fn every_answer_is_the_one_the_csharp_server_gave() {
    let cases: Vec<Case> = olv_oracle::cases("w5-agent", "wire");
    let refused_here: HashSet<&str> = REFUSED_HERE.into_iter().collect();
    let query_refused: HashSet<&str> = QUERY_TOKEN_REFUSED.into_iter().collect();
    let wording: HashSet<&str> = WORDING.into_iter().collect();

    let mut checked = 0;
    let mut failures: Vec<String> = Vec::new();

    for case in cases.iter().filter(|c| c.method != "WS") {
        let (server, bench) = start(&case.setup);

        let headers: Vec<(&str, &str)> = case.headers.iter().map(|h| (h[0].as_str(), h[1].as_str())).collect();

        let got = raw(server.port(), &case.method, &case.target, &headers, &case.body);
        checked += 1;

        if refused_here.contains(case.name.as_str()) {
            if got.status != 403 {
                failures.push(format!(
                    "{}: expected this server to refuse with 403, got {}",
                    case.name, got.status
                ));
            }
            continue;
        }

        if query_refused.contains(case.name.as_str()) {
            if got.status != 401 {
                failures.push(format!(
                    "{}: expected 401 for a query token off the stream, got {}",
                    case.name, got.status
                ));
            }
            continue;
        }

        let mut problems = Vec::new();

        if got.status != case.status {
            problems.push(format!("status {} but the C# said {}", got.status, case.status));
        }
        if got.content_type != case.content_type {
            problems.push(format!(
                "content type {:?} but the C# said {:?}",
                got.content_type, case.content_type
            ));
        }
        if got.body != case.response && !wording.contains(case.name.as_str()) {
            problems.push(format!("body\n   ours: {}\n   C#:   {}", got.body, case.response));
        }
        if bench.calls() != case.calls {
            problems.push(format!(
                "bridge calls {:?} but the C# made {:?}",
                bench.calls(),
                case.calls
            ));
        }

        if !problems.is_empty() {
            failures.push(format!("{}: {}", case.name, problems.join("; ")));
        }
    }

    assert!(checked >= 150, "only {checked} cases were replayed");
    assert!(
        failures.is_empty(),
        "{} of {checked} differ:\n{}",
        failures.len(),
        failures.join("\n")
    );
}

#[test]
fn the_live_stream_sends_the_same_messages_as_the_csharp() {
    let case = olv_oracle::cases::<Case>("w5-agent", "wire")
        .into_iter()
        .find(|c| c.method == "WS")
        .expect("the stream case");

    let (server, _) = start(&[]);
    let mut socket = subscribe(server.port()).expect("connects");

    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
    while server.subscribers() == 0 && std::time::Instant::now() < deadline {
        std::thread::sleep(std::time::Duration::from_millis(10));
    }

    let names: Arc<[String]> = ["RPM", "Lua: torque", "Neg", "Tiny", "Big"]
        .iter()
        .map(|s| s.to_string())
        .collect();
    server.publish(1.5, &names, &[3000.0, f64::NAN, f64::NEG_INFINITY, 1e-7, f64::INFINITY]);

    let schema = next_text(&mut socket);
    let frame = next_text(&mut socket);

    send(&mut socket, "not json");
    let error = next_text(&mut socket);

    assert_eq!(vec![schema, frame, error], case.messages);
}
