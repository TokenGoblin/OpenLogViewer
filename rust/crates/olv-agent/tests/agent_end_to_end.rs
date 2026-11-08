//! An agent's request all the way to the controller's bytes: HTTP in, the server, the bridge, the
//! writer and its gates, a real `EcuConnection`, and a fake controller at the end.
//!
//! Every gate is tested on its own in `agent_writes.rs`; this proves they are the ones the wire
//! reaches. If a route were ever wired to a write path that skipped the writer, a refusal here
//! would still change the controller's bytes and these tests would say so.

mod support;

use std::sync::Arc;

use olv_agent::agent::agent_server::{AgentServer, AgentServerSettings};
use serde_json::{Value, json};
use support::bench::Bench;
use support::http::{TOKEN, get, post, raw};
use support::writer_bridge::WriterBridge;
use support::{Board, Rig, armed_rig, rig};

fn serve(rig: &Rig) -> AgentServer {
    let bridge = Arc::new(WriterBridge {
        writer: Arc::clone(&rig.writer),
        rest: Bench::new(),
    });

    let mut server = AgentServer::new(
        bridge,
        AgentServerSettings {
            port: 0,
            ..AgentServerSettings::new(TOKEN)
        },
    )
    .unwrap();
    server.start().unwrap();

    server
}

fn set(name: &str, value: f64, rationale: &str, confirm: bool) -> String {
    json!({"name": name, "value": value, "rationale": rationale, "confirmDangerous": confirm}).to_string()
}

/// Every write route, tried with the most permissive body an agent could send.
fn try_every_write(port: u16) -> Vec<(String, u16, Value)> {
    let attempts: Vec<(&str, Value)> = vec![
        (
            "/tune/set",
            json!({"name": "crankingRPM", "value": 310, "rationale": "r", "confirmDangerous": true}),
        ),
        (
            "/tune/set",
            json!({"name": "revLimit", "value": 6600, "rationale": "r", "confirmDangerous": true}),
        ),
        (
            "/table/set",
            json!({"table": "VE Table", "column": 0, "row": 0, "value": 10, "rationale": "r", "confirmDangerous": true}),
        ),
        (
            "/tune/apply",
            json!({
                "settings": [{"name": "crankingRPM", "value": 310}],
                "cells": [{"table": "VE Table", "column": 0, "row": 0, "value": 10}],
                "note": "r", "confirmDangerous": true,
            }),
        ),
    ];

    attempts
        .into_iter()
        .map(|(path, body)| {
            let answer = post(port, path, &body.to_string());
            (path.to_string(), answer.status, answer.json())
        })
        .collect()
}

#[test]
fn an_agent_that_tries_every_write_route_unarmed_changes_nothing() {
    let rig = rig("e2e-unarmed");
    let server = serve(&rig);

    for (path, status, body) in try_every_write(server.port()) {
        assert_eq!(status, 409, "{path}");
        assert_eq!(body["error"], "writes are not armed", "{path}");
    }

    assert!(
        rig.board.writes().is_empty(),
        "the controller must not have been written to"
    );
    assert_eq!(rig.board.page(), Board::new().page());
    assert_eq!(get(server.port(), "/state").json()["writesArmed"], false);
}

#[test]
fn only_the_person_arming_it_lets_the_same_requests_through() {
    let rig = rig("e2e-arm");
    let server = serve(&rig);

    rig.writer.set_armed(true);

    let answer = post(
        server.port(),
        "/tune/set",
        &set("crankingRPM", 310.0, "small nudge", false),
    );
    assert_eq!(answer.status, 200, "{}", answer.body);
    assert_eq!(answer.json()["burned"], false);
    assert_eq!(get(server.port(), "/state").json()["writesArmed"], true);

    assert_eq!(&rig.board.page()[0..2], &[0x01, 0x36]); // 310
    assert_eq!(rig.board.burns(), 0);
}

#[test]
fn disconnecting_closes_the_gate_again_on_the_wire() {
    let rig = armed_rig("e2e-disconnect");
    let server = serve(&rig);

    assert_eq!(
        post(server.port(), "/tune/set", &set("crankingRPM", 310.0, "first", false)).status,
        200
    );

    rig.writer.disconnect();

    let answer = post(server.port(), "/tune/set", &set("crankingRPM", 320.0, "second", false));
    assert_eq!(answer.status, 409);
    assert_eq!(answer.json()["error"], "writes are not armed");
    assert_eq!(rig.board.writes().len(), 1);
    assert_eq!(get(server.port(), "/state").json()["writesArmed"], false);
}

#[test]
fn a_dangerous_setting_needs_the_flag_on_the_wire_and_the_flag_is_per_call() {
    let rig = armed_rig("e2e-dangerous");
    let server = serve(&rig);

    let refused = post(server.port(), "/tune/set", &set("revLimit", 6600.0, "dyno", false));
    assert_eq!(refused.status, 409);
    assert_eq!(refused.json()["error"], "this setting needs confirmDangerous:true");
    assert_eq!(&rig.board.page()[2..4], &[0x19, 0x64]);

    let allowed = post(server.port(), "/tune/set", &set("revLimit", 6600.0, "dyno", true));
    assert_eq!(allowed.status, 200, "{}", allowed.body);
    assert_eq!(&rig.board.page()[2..4], &[0x19, 0xC8]);

    // The string "true" is not the boolean, and a number is not either: the body is refused as
    // malformed rather than read as consent.
    for flag in ["\"true\"", "1", "\"yes\""] {
        let body = format!("{{\"name\":\"revLimit\",\"value\":6700,\"rationale\":\"r\",\"confirmDangerous\":{flag}}}");
        let answer = post(server.port(), "/tune/set", &body);
        assert_eq!(answer.status, 400, "{flag}");
    }
    assert_eq!(&rig.board.page()[2..4], &[0x19, 0xC8]);
}

#[test]
fn the_rationale_cannot_be_omitted_by_any_spelling_of_the_body() {
    let rig = armed_rig("e2e-rationale");
    let server = serve(&rig);

    for body in [
        r#"{"name":"crankingRPM","value":310}"#,
        r#"{"name":"crankingRPM","value":310,"rationale":null}"#,
        r#"{"name":"crankingRPM","value":310,"rationale":""}"#,
        r#"{"name":"crankingRPM","value":310,"rationale":"   "}"#,
        r#"{"name":"crankingRPM","value":310,"Rationale":"wrong case is not the field"}"#,
    ] {
        let answer = post(server.port(), "/tune/set", body);
        assert_eq!(answer.status, 409, "{body}");
        assert_eq!(answer.json()["error"], "no rationale given", "{body}");
    }

    assert!(rig.board.writes().is_empty());
}

#[test]
fn the_rate_limit_holds_on_the_wire_and_a_refused_attempt_is_not_sent() {
    let rig = armed_rig("e2e-rate");
    let server = serve(&rig);

    for i in 0..10 {
        let answer = post(
            server.port(),
            "/tune/set",
            &set("crankingRPM", 301.0 + f64::from(i), "step", false),
        );
        assert_eq!(answer.status, 200, "{i}: {}", answer.body);
    }

    let eleventh = post(
        server.port(),
        "/tune/set",
        &set("crankingRPM", 320.0, "too soon", false),
    );
    assert_eq!(eleventh.status, 409);
    assert_eq!(eleventh.json()["error"], "too many writes in a short time");
    assert_eq!(rig.board.writes().len(), 10);
}

#[test]
fn a_value_that_is_not_a_number_never_reaches_the_controller() {
    let rig = armed_rig("e2e-nan");
    let server = serve(&rig);

    for value in ["\"NaN\"", "\"Infinity\"", "\"-Infinity\"", "1e999"] {
        let body = format!("{{\"name\":\"crankingRPM\",\"value\":{value},\"rationale\":\"r\"}}");
        let answer = post(server.port(), "/tune/set", &body);
        assert_eq!(answer.status, 409, "{value}: {}", answer.body);
    }

    assert!(rig.board.writes().is_empty());
    assert_eq!(&rig.board.page()[0..2], &[0x01, 0x2C]);
}

#[test]
fn the_idle_gate_holds_on_the_wire() {
    let rig = armed_rig("e2e-rpm");
    rig.host.set_rpm(Some(3500.0));
    let server = serve(&rig);

    let answer = post(server.port(), "/tune/set", &set("crankingRPM", 310.0, "r", false));

    assert_eq!(answer.status, 409);
    assert_eq!(answer.json()["error"], "the engine is running above idle");
    assert!(rig.board.writes().is_empty());
}

#[test]
fn propose_and_stage_work_unarmed_and_never_write_the_controller() {
    let rig = rig("e2e-readonly");
    let server = serve(&rig);

    let proposal = post(
        server.port(),
        "/tune/propose",
        &json!({"settings": [{"name": "crankingRPM", "value": 400}]}).to_string(),
    );
    assert_eq!(proposal.status, 200);
    assert_eq!(proposal.json()["changes"][0]["after"], 400);

    let staged = post(
        server.port(),
        "/stage/tune",
        &json!({"filename": "mine.msq"}).to_string(),
    );
    assert_eq!(staged.status, 200, "{}", staged.body);

    let listed = get(server.port(), "/stage").json();
    assert_eq!(listed["files"][0]["name"], "mine.msq");

    // And a staging name that tries to leave the folder is a 409, writes nothing, and says so.
    let escape = post(
        server.port(),
        "/stage/tune",
        &json!({"filename": "../escaped.msq"}).to_string(),
    );
    assert_eq!(escape.status, 409);
    assert_eq!(escape.json()["error"], "could not stage that file");
    assert!(!rig.workspace.root().join("escaped.msq").exists());

    assert!(rig.board.writes().is_empty());
}

#[test]
fn the_tune_the_agent_reads_is_the_one_in_hand() {
    let rig = rig("e2e-read");
    let server = serve(&rig);

    let tune = get(server.port(), "/tune").json();
    assert_eq!(tune["crankingRPM"], 300);
    assert_eq!(tune["revLimit"], 6500);

    let tables = get(server.port(), "/tables").json();
    assert_eq!(tables, json!(["VE Table", "Launch Table"]));

    let table = get(server.port(), "/table?name=VE%20Table").json();
    assert_eq!(table["columns"], 2);
    assert_eq!(table["values"][0][0], 0);
}

#[test]
fn an_unauthenticated_caller_cannot_even_probe_whether_writes_are_armed() {
    let rig = armed_rig("e2e-probe");
    let server = serve(&rig);

    let answer = raw(server.port(), "GET", "/state", &[], "");

    assert_eq!(answer.status, 401);
    assert!(!answer.body.contains("writesArmed"));
}
