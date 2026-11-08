//! The C# `EcuConnection`'s requests, replayed.
//!
//! `tools/OpenLogViewer.Oracle/Emitters/W3Connection.cs` runs the shipping C# against a model of
//! one page of ECU memory for the three families the TunerStudio protocol serves (Speeduino, MS2,
//! rusEFI) and writes every request it sent, byte for byte, and how each scenario ended. The Rust
//! runs the same scenarios against the same model and must send the same bytes and say the same
//! thing when it fails.

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use common::{FakeTransport, Misbehaviour, ecu_model};
use olv_core::ini::msq_ini;
use olv_core::ini::tune_layout::TunePage;
use olv_live::connection::ecu_connection::{EcuConnection, EcuConnectionSettings, EcuError};
use serde::Deserialize;

#[derive(Deserialize)]
struct PageCase {
    index: i32,
    size: i32,
    identifier: String,
    read_command: String,
    chunk_write_command: String,
    burn_command: String,
}

#[derive(Deserialize)]
struct Case {
    family: String,
    scenario: String,
    op: String,
    behaviour: String,
    little_endian: bool,
    can_id: u8,
    page: PageCase,
    offset: i32,
    count: i32,
    blocking: i32,
    data: Option<String>,
    delay: i32,
    realtime_ini: String,
    realtime_size: i32,
    requests: Vec<String>,
    outcome: String,
    error_kind: Option<String>,
    message: Option<String>,
    refused: Option<bool>,
    result: String,
    retries: i32,
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02X}")).collect()
}

fn unhex(text: &str) -> Vec<u8> {
    (0..text.len() / 2)
        .map(|i| u8::from_str_radix(&text[i * 2..i * 2 + 2], 16).unwrap())
        .collect()
}

fn behaviour(name: &str) -> Misbehaviour {
    match name {
        "None" => Misbehaviour::None,
        "IgnoresWrites" => Misbehaviour::IgnoresWrites,
        "RefusesWrites" => Misbehaviour::RefusesWrites,
        "RefusesBurn" => Misbehaviour::RefusesBurn,
        "BurnStatus04" => Misbehaviour::BurnStatus(0x04),
        "SilentBurn" => Misbehaviour::SilentBurn,
        other => panic!("the oracle has a behaviour this test does not know: {other}"),
    }
}

#[test]
fn every_request_the_connection_sends_matches_the_csharp_byte_for_byte() {
    let cases: Vec<Case> = olv_oracle::cases("w3-connection", "requests");
    assert_eq!(cases.len(), 60, "three families, twenty scenarios");

    for case in cases {
        let what = format!("{} / {}", case.family, case.scenario);

        let page = TunePage {
            index: case.page.index,
            size: case.page.size,
            identifier: case.page.identifier.clone(),
            read_command: case.page.read_command.clone(),
            chunk_write_command: case.page.chunk_write_command.clone(),
            burn_command: case.page.burn_command.clone(),
        };

        let memory = Arc::new(Mutex::new(
            (0..4096usize).map(|i| (i * 7 + 3) as u8).collect::<Vec<u8>>(),
        ));
        let transport = FakeTransport::responding(ecu_model(
            Arc::clone(&memory),
            case.little_endian,
            behaviour(&case.behaviour),
        ));
        let connection = EcuConnection::new(
            Box::new(transport.clone()),
            EcuConnectionSettings {
                timeout: Duration::from_millis(1),
                burn_timeout: Duration::from_millis(5),
                settle_for: Duration::from_millis(5),
                quiet_for: Duration::from_millis(1),
                can_id: case.can_id,
                ..EcuConnectionSettings::default()
            },
        );

        let data = case.data.as_deref().map(unhex);

        let outcome: Result<String, EcuError> = match case.op.as_str() {
            "read_range" => connection
                .read_tune_page_range(&page, case.blocking, case.little_endian, case.offset, case.count, None)
                .map(|d| hex(&d)),
            "read_page" => connection
                .read_tune_page(&page, case.blocking, case.little_endian, None)
                .map(|d| hex(&d)),
            "write" => {
                let data = data.clone().unwrap();
                connection
                    .write_tune_page(&page, case.blocking, case.little_endian, case.offset, &data, case.delay)
                    .map(|()| hex(&memory.lock().unwrap()[case.offset as usize..case.offset as usize + data.len()]))
            }
            "burn" => connection
                .burn_page(&page, case.little_endian, 0)
                .map(|()| String::new()),
            "identity" => connection
                .read_identity_within(Duration::ZERO)
                .map(|said| said.join("|")),
            "realtime" | "realtime_single" => {
                let layout = msq_ini::read_output_channels(&case.realtime_ini, None);
                connection.use_layout(&layout, case.op == "realtime_single");
                connection.read_realtime(case.realtime_size).map(|d| hex(&d))
            }
            other => panic!("the oracle has an operation this test does not know: {other}"),
        };

        let sent: Vec<String> = transport.written().iter().map(|w| hex(w)).collect();
        assert_eq!(sent, case.requests, "{what}: the bytes sent");
        assert_eq!(connection.retries(), case.retries, "{what}: retries");

        match (case.outcome.as_str(), outcome) {
            ("ok", Ok(result)) => assert_eq!(result, case.result, "{what}: the result"),
            ("error", Err(error)) => {
                assert_eq!(case.error_kind.as_deref(), Some("protocol"), "{what}");
                assert!(error.is_protocol(), "{what}: {error}");
                assert_eq!(error.refused(), case.refused.unwrap(), "{what}: refused");
                assert_eq!(error.to_string(), case.message.clone().unwrap(), "{what}: the message");
            }
            (expected, got) => panic!("{what}: the C# ended {expected}, the Rust gave {got:?}"),
        }
    }
}
