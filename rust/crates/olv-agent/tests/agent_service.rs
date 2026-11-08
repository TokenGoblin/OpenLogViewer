//! Starting the API the way the application does: a fresh token, the loopback server, and the file
//! an agent reads to find both. And what the server does with a client that will not finish.

mod support;

use std::io::{Read, Write};
use std::net::TcpStream;
use std::sync::Arc;
use std::time::{Duration, Instant};

use olv_agent::agent::agent_server::{AgentServer, AgentServerSettings};
use olv_agent::agent::agent_service::AgentService;
use olv_store::store::workspace::Workspace;
use support::Scratch;
use support::bench::Bench;
use support::http::{TOKEN, raw};

fn token_in(file: &std::path::Path) -> (serde_json::Value, String) {
    let text = std::fs::read_to_string(file).expect("the token file");
    (serde_json::from_str(&text).expect("JSON"), text)
}

#[test]
fn starting_leaves_the_address_and_a_token_that_opens_the_door() {
    let scratch = Scratch::new("service");
    let workspace = Workspace::new(Some(&scratch.0));

    let (service, message) = AgentService::start(Arc::new(Bench::new()), workspace.clone(), 0).expect("starts");

    let file = scratch.0.join("agent-api.json");
    let (json, text) = token_in(&file);

    let port = service.server().port();
    assert_eq!(json["address"], format!("http://127.0.0.1:{port}"));
    assert_eq!(json["websocket"], format!("ws://127.0.0.1:{port}/live/stream"));

    let token = json["token"].as_str().unwrap().to_string();
    assert_eq!(token.len(), 64);

    // The sentence the application shows says where the token is, never what it is.
    assert!(message.contains(&format!("http://127.0.0.1:{port}")));
    assert!(message.contains("agent-api.json"));
    assert!(!message.contains(&token), "{message}");
    assert!(!format!("{service:?}").contains(&token));
    assert!(text.contains(&token));

    // The token in the file is the one the server wants, and a wrong one is not.
    let header = format!("Bearer {token}");
    assert_eq!(
        raw(port, "GET", "/state", &[("Authorization", &header)], "").status,
        200
    );
    assert_eq!(
        raw(port, "GET", "/state", &[("Authorization", "Bearer nope")], "").status,
        401
    );

    // And the API is not armed by being switched on.
    let state: serde_json::Value = raw(port, "GET", "/state", &[("Authorization", &header)], "").json();
    assert_eq!(state["writesArmed"], false);
}

#[test]
fn every_start_makes_a_new_token_and_stopping_removes_the_file_and_the_port() {
    let scratch = Scratch::new("service-twice");
    let workspace = Workspace::new(Some(&scratch.0));
    let file = scratch.0.join("agent-api.json");

    let first = {
        let (service, _) = AgentService::start(Arc::new(Bench::new()), workspace.clone(), 0).unwrap();
        let (json, _) = token_in(&file);
        let port = service.server().port();
        drop(service);

        assert!(!file.exists(), "a stale file would point an agent at a dead port");
        assert!(TcpStream::connect(("127.0.0.1", port)).is_err());

        json["token"].as_str().unwrap().to_string()
    };

    let (service, _) = AgentService::start(Arc::new(Bench::new()), workspace, 0).unwrap();
    let (json, _) = token_in(&file);
    assert_ne!(json["token"].as_str().unwrap(), first);
    drop(service);
}

#[test]
fn a_port_that_is_taken_is_said_so_and_leaves_no_file_behind() {
    let scratch = Scratch::new("service-taken");
    let holder = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = holder.local_addr().unwrap().port();

    let failed = AgentService::start(Arc::new(Bench::new()), Workspace::new(Some(&scratch.0)), port);

    let message = failed.expect_err("the port is taken");
    assert!(message.starts_with("The agent API could not start:"), "{message}");
    assert!(
        message.ends_with("Another program may already have that port."),
        "{message}"
    );
    assert!(!scratch.0.join("agent-api.json").exists());
}

#[test]
fn the_token_file_is_in_the_workspace_folder_the_mcp_server_reads() {
    // `olv-mcp` with no argument reads `<home>/OpenLogViewer/agent-api.json`, which is the default
    // workspace's root: the same place this writes.
    assert_eq!(
        olv_agent::agent::token_file::path(&Workspace::new(None)),
        Workspace::default_path().join("agent-api.json")
    );
}

#[test]
fn a_client_that_drips_its_request_a_byte_at_a_time_is_cut_off() {
    let mut server = AgentServer::new(
        Arc::new(Bench::new()),
        AgentServerSettings {
            port: 0,
            request_timeout: Duration::from_millis(400),
            ..AgentServerSettings::new(TOKEN)
        },
    )
    .unwrap();
    server.start().unwrap();

    let mut stream = TcpStream::connect(("127.0.0.1", server.port())).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();

    let started = Instant::now();
    let mut cut = false;

    // A byte every 100 ms would never trip a per-read timeout of 400 ms, and never finish.
    for byte in b"GET /state HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Slow: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" {
        if stream.write_all(&[*byte]).is_err() {
            cut = true;
            break;
        }
        std::thread::sleep(Duration::from_millis(100));
    }

    let mut rest = Vec::new();
    let _ = stream.read_to_end(&mut rest);

    assert!(
        cut || rest.is_empty() || started.elapsed() < Duration::from_secs(8),
        "the server should have hung up"
    );
    assert!(
        started.elapsed() < Duration::from_secs(8),
        "it held the connection for {:?}",
        started.elapsed()
    );

    // And the server is still answering everybody else.
    let header = format!("Bearer {TOKEN}");
    assert_eq!(
        raw(server.port(), "GET", "/state", &[("Authorization", &header)], "").status,
        200
    );
}

#[test]
fn a_flood_of_idle_connections_does_not_stop_the_door_being_answered() {
    let mut server = AgentServer::new(
        Arc::new(Bench::new()),
        AgentServerSettings {
            port: 0,
            request_timeout: Duration::from_millis(500),
            ..AgentServerSettings::new(TOKEN)
        },
    )
    .unwrap();
    server.start().unwrap();

    // Far more than the cap, all silent.
    let idle: Vec<TcpStream> = (0..300)
        .filter_map(|_| TcpStream::connect(("127.0.0.1", server.port())).ok())
        .collect();
    assert!(!idle.is_empty());

    // After the silent ones time out, a real request is answered.
    std::thread::sleep(Duration::from_millis(900));
    let header = format!("Bearer {TOKEN}");
    assert_eq!(
        raw(server.port(), "GET", "/state", &[("Authorization", &header)], "").status,
        200
    );
}
