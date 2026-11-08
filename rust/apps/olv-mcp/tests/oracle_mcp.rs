//! The MCP server's answers, compared character for character with the shipping C# server's.
//!
//! `W5Mcp.cs` in the oracle runs the C# server's own `Answer` (the code a stdin line reaches)
//! for the handshake, the whole tool list, and each of the 26 tools against the real C#
//! `AgentServer` over the fixed bench, recording the line it would print and what the bench was
//! asked. This runs the same requests through this server and the Rust `AgentServer` over the
//! same bench and compares both, so the translation of arguments into requests (the URLs, the
//! bodies, the lenient number and flag reading) is checked end to end, not just the shape of the
//! answer.

use std::sync::Arc;
use std::sync::atomic::Ordering;

use olv_agent::agent::agent_api::AgentDefinitionNeed;
use olv_agent::agent::agent_server::{AgentServer, AgentServerSettings};
use olv_agent::agent::testing::Bench;
use olv_mcp::McpServer;
use serde::Deserialize;

const TOKEN: &str = "test-token-0123456789";

#[derive(Deserialize)]
struct Case {
    name: String,
    method: String,
    #[serde(default)]
    parameters: Option<Parameters>,
    #[serde(default)]
    setup: Vec<String>,
    output: String,
    #[serde(default)]
    calls: Vec<String>,
}

#[derive(Deserialize)]
struct Parameters {
    name: String,
    #[serde(default)]
    arguments: String,
}

fn request_line(case: &Case) -> String {
    let params = match &case.parameters {
        None => String::new(),
        Some(p) if p.arguments.is_empty() => format!(",\"params\":{{\"name\":\"{}\"}}", p.name),
        Some(p) => format!(",\"params\":{{\"name\":\"{}\",\"arguments\":{}}}", p.name, p.arguments),
    };

    format!(
        "{{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"{}\"{params}}}",
        case.method
    )
}

fn start() -> (AgentServer, Arc<Bench>) {
    let bench = Arc::new(Bench::new());
    let mut server = AgentServer::new(
        bench.clone(),
        AgentServerSettings {
            port: 0,
            ..AgentServerSettings::new(TOKEN)
        },
    )
    .unwrap();
    server.start().unwrap();

    (server, bench)
}

fn details_for(server: &AgentServer, what: &str) -> std::path::PathBuf {
    let file = std::env::temp_dir().join(format!("olv-mcp-{what}-{}.json", std::process::id()));
    std::fs::write(
        &file,
        format!(
            "{{ \"address\": \"{}\", \"websocket\": \"x\", \"token\": \"{TOKEN}\" }}",
            server.address()
        ),
    )
    .unwrap();

    file
}

const CALL_STATE: &str =
    r#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"olv_state","arguments":{}}}"#;

#[test]
fn the_handshake_the_tool_list_and_every_tool_are_the_csharps() {
    let cases: Vec<Case> = olv_oracle::cases("w5-agent", "mcp");
    assert!(cases.len() >= 60, "{} cases", cases.len());

    let mut failures: Vec<String> = Vec::new();

    // Before there is an application at all.
    let mut nowhere = McpServer::new("/nowhere/agent-api.json");

    for case in cases
        .iter()
        .filter(|c| c.parameters.is_none() || c.name == "no application")
    {
        let got = nowhere.handle_line(&request_line(case)).unwrap_or_default();

        if got != case.output {
            failures.push(format!("{}:\n   ours: {got}\n   C#:   {}", case.name, case.output));
        }
    }

    // Every tool, against a live server.
    let (server, bench) = start();
    let file = details_for(&server, "oracle");
    let mut mcp = McpServer::new(&file);

    for case in cases
        .iter()
        .filter(|c| c.parameters.is_some() && c.name != "no application")
    {
        let has = |s: &str| case.setup.iter().any(|x| x == s);

        bench.armed.store(has("armed"), Ordering::SeqCst);
        bench.rich.store(has("rich"), Ordering::SeqCst);
        bench.extra_event.store(has("events2"), Ordering::SeqCst);
        bench.with(|s| {
            s.brief = if has("brief") {
                "# The E28\n\n## Still open\n".to_string()
            } else {
                String::new()
            };
            s.needed = has("needed").then(|| AgentDefinitionNeed {
                identity: vec!["Speeduino 2025.01.7".to_string()],
                family: "Speeduino".to_string(),
                signature: "speeduino 202501".to_string(),
                version: "Speeduino 2025.01.7".to_string(),
                filename: "speeduino202501.ini".to_string(),
                nearby: vec![],
                folder: "C:\\defs".to_string(),
                where_: "somewhere".to_string(),
                sources: vec!["https://example.invalid/x.ini".to_string()],
                guidance: "fetch it".to_string(),
            });
            s.calls.clear();
        });

        let got = mcp.handle_line(&request_line(case)).unwrap_or_default();
        let calls = bench.calls();

        if got != case.output {
            failures.push(format!("{}:\n   ours: {got}\n   C#:   {}", case.name, case.output));
        }
        if calls != case.calls {
            failures.push(format!(
                "{}: the bench was asked {calls:?}, the C# asked {:?}",
                case.name, case.calls
            ));
        }
    }

    let _ = std::fs::remove_file(&file);

    assert!(
        failures.is_empty(),
        "{} differ:\n{}",
        failures.len(),
        failures.join("\n")
    );
}

#[test]
fn a_refused_write_is_flagged_as_an_error_so_a_model_does_not_read_it_as_done() {
    let (server, _) = start();
    let file = details_for(&server, "refused");

    let mut mcp = McpServer::new(&file);
    let line = r#"{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"olv_set_setting","arguments":{"name":"revLimit","value":7000,"rationale":"x"}}}"#;
    let answer = mcp.handle_line(line).unwrap();

    assert!(answer.contains("\"isError\":true"), "{answer}");
    assert!(answer.contains("writes are not armed"));

    let _ = std::fs::remove_file(&file);
}

#[test]
fn the_token_is_never_sent_anywhere_but_the_loopback_address() {
    let file = std::env::temp_dir().join(format!("olv-mcp-evil-{}.json", std::process::id()));
    std::fs::write(
        &file,
        r#"{"address":"http://evil.example:8765","token":"secret-token"}"#,
    )
    .unwrap();

    let mut mcp = McpServer::new(&file);
    let answer = mcp.handle_line(CALL_STATE).unwrap();

    assert!(answer.contains("Refusing to send the token"), "{answer}");
    assert!(answer.contains("\"isError\":true"));
    assert!(!answer.contains("secret-token"), "the token must never be echoed");

    let _ = std::fs::remove_file(&file);
}

#[test]
fn an_application_that_is_not_listening_is_said_so_plainly() {
    // Bind and drop to find a port nothing is on.
    let port = std::net::TcpListener::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let file = std::env::temp_dir().join(format!("olv-mcp-down-{}.json", std::process::id()));
    std::fs::write(
        &file,
        format!("{{\"address\":\"http://127.0.0.1:{port}\",\"token\":\"t\"}}"),
    )
    .unwrap();

    let mut mcp = McpServer::new(&file);
    let answer = mcp.handle_line(CALL_STATE).unwrap();

    assert!(answer.contains("OpenLogViewer did not answer:"), "{answer}");
    assert!(answer.contains("\"isError\":true"));

    let _ = std::fs::remove_file(&file);
}

#[test]
fn a_details_file_that_names_no_token_is_refused() {
    let file = std::env::temp_dir().join(format!("olv-mcp-notoken-{}.json", std::process::id()));
    std::fs::write(&file, r#"{"address":"http://127.0.0.1:1"}"#).unwrap();

    let mut mcp = McpServer::new(&file);
    let answer = mcp.handle_line(CALL_STATE).unwrap();

    assert!(answer.contains("does not name an address and a token."), "{answer}");

    let _ = std::fs::remove_file(&file);
}
