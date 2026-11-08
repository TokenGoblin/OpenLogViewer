//! `EcuConnection`: the C# `EcuProtocolTests` that need a connection, `WireTraceTests`' wired
//! half, and byte-level tests of every request it sends for each ECU family.
//!
//! The framing tests of `EcuProtocolTests` (`ARequestIsLengthPayloadAndChecksum` and its seven
//! neighbours) belong to `olv_protocol::ms::ms_protocol` and are ported there under the same
//! names.

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use common::{FakeTransport, Misbehaviour, ecu_model, reply, reply_with_status};
use olv_core::ini::ini_catalog::{self, IniFile};
use olv_core::ini::msq_ini;
use olv_core::ini::tune_layout::TunePage;
use olv_core::model::{WireFailureKind, WireOrigin, WireOutcome, WireTrace};
use olv_live::connection::ecu_connection::{EcuConnection, EcuConnectionSettings, EcuError};
use olv_protocol::ms::ms_protocol;

fn hex(text: &str) -> Vec<u8> {
    (0..text.len() / 2)
        .map(|i| u8::from_str_radix(&text[i * 2..i * 2 + 2], 16).unwrap())
        .collect()
}

fn connection(transport: &FakeTransport) -> EcuConnection {
    EcuConnection::with_defaults(Box::new(transport.clone()))
}

fn with_settings(transport: &FakeTransport, settings: EcuConnectionSettings) -> EcuConnection {
    EcuConnection::new(Box::new(transport.clone()), settings)
}

// ----- connection ---------------------------------------------------------

#[test]
fn a_signature_is_read_back() {
    let transport = FakeTransport::new();
    transport.enqueue([reply(b"MS3 Format 0569.00 \0")]);

    let connection = connection(&transport);

    assert_eq!(connection.read_signature().unwrap(), "MS3 Format 0569.00");
    assert_eq!(connection.retries(), 0);
}

#[test]
fn a_corrupt_reply_is_asked_for_again() {
    // What a Bluetooth link does routinely. One bad reply should cost a retry, not the
    // session.
    let mut bad = reply(b"MS3");
    *bad.last_mut().unwrap() ^= 0xFF;

    let transport = FakeTransport::new();
    transport.enqueue([bad, reply(b"MS3")]);

    let connection = connection(&transport);

    assert_eq!(connection.read_signature().unwrap(), "MS3");
    assert_eq!(connection.retries(), 1);
}

#[test]
fn silence_is_also_retried_and_eventually_given_up_on() {
    let transport = FakeTransport::new(); // never replies
    let connection = with_settings(
        &transport,
        EcuConnectionSettings {
            retries: 2,
            timeout: Duration::from_millis(1),
            retry_pause: Duration::ZERO,
            ..EcuConnectionSettings::default()
        },
    );

    assert!(matches!(connection.read_signature(), Err(EcuError::Protocol(_))));
    assert_eq!(transport.written().len(), 3); // the attempt plus two retries
}

#[test]
fn the_input_is_cleared_before_every_attempt() {
    // The tail of an abandoned reply would otherwise be read as the head of the next one,
    // which fails in a way that looks like corruption.
    let transport = FakeTransport::new();
    transport.enqueue([reply(b"MS3")]);

    let connection = connection(&transport);
    connection.read_signature().unwrap();

    assert_eq!(transport.discards(), 1);
}

#[test]
fn a_realtime_block_comes_back_whole() {
    let mut block = vec![0u8; 512];
    block[6] = 0x0B;
    block[7] = 0xB8;

    let transport = FakeTransport::new();
    transport.enqueue([reply(&block)]);

    let connection = connection(&transport);
    let read = connection.read_realtime(512).unwrap();

    assert_eq!(read.len(), 512);
    assert_eq!(read[7], 0xB8);
}

// ----- INI matching -------------------------------------------------------

#[test]
fn an_ini_is_matched_on_the_signature_the_ecu_reports() {
    // Firmware versions move channels inside the block, so the wrong INI decodes every
    // channel from the wrong offset and still looks sane.
    let catalogue = [
        IniFile::new("a.ini", "MS3 Format 0592.13P"),
        IniFile::new("b.ini", "MS3 Format 0569.00"),
        IniFile::new("c.ini", "MS2Extra comms340vU"),
    ];

    assert_eq!(
        ini_catalog::match_signature("MS3 Format 0569.00", &catalogue)
            .unwrap()
            .path,
        "b.ini"
    );
}

#[test]
fn padding_and_spacing_do_not_stop_a_match() {
    let catalogue = [IniFile::new("a.ini", "MS3 Format 0569.00 ")];

    assert!(ini_catalog::match_signature("MS3 Format 0569.00", &catalogue).is_some());
    assert!(ini_catalog::match_signature("MS3  Format 0569.00", &catalogue).is_some());
}

#[test]
fn an_unknown_signature_matches_nothing() {
    // Better no session than one decoded against the wrong firmware.
    let catalogue = [IniFile::new("a.ini", "MS3 Format 0592.13P")];

    assert!(ini_catalog::match_signature("MS3 Format 0569.00", &catalogue).is_none());
    assert!(ini_catalog::match_signature("", &catalogue).is_none());
}

#[test]
fn a_nearby_version_is_not_treated_as_a_match() {
    // 0592.12P and 0592.13P differ by one character and by their layout.
    let catalogue = [IniFile::new("a.ini", "MS3 Format 0592.12P")];

    assert!(ini_catalog::match_signature("MS3 Format 0592.13P", &catalogue).is_none());
}

// ----- WireTraceTests: wired into a real connection -----------------------

#[test]
fn a_new_connection_records_nothing_unless_given_a_trace() {
    let transport = FakeTransport::new();
    transport.enqueue([reply(b"MS3")]);

    let connection = connection(&transport);

    assert!(connection.trace().is_none());
    connection.read_signature().unwrap(); // must not fail for want of a trace
}

#[test]
fn a_successful_read_is_recorded_against_its_context() {
    let transport = FakeTransport::new();
    transport.enqueue([reply(b"MS3")]);

    let trace = Arc::new(WireTrace::default());
    let connection = connection(&transport).with_trace(Arc::clone(&trace));

    connection.read_signature().unwrap();

    let events = trace.recent(10);
    assert_eq!(events.len(), 1);
    assert_eq!(events[0].context, "read signature");
    assert_eq!(events[0].outcome, WireOutcome::Ok);
    assert_eq!(events[0].attempt, 0);
}

#[test]
fn a_retried_read_records_the_failure_then_the_success() {
    let mut bad = reply(b"MS3");
    *bad.last_mut().unwrap() ^= 0xFF;

    let transport = FakeTransport::new();
    transport.enqueue([bad, reply(b"MS3")]);

    let trace = Arc::new(WireTrace::default());
    let connection = connection(&transport).with_trace(Arc::clone(&trace));

    connection.read_signature().unwrap();

    let events = trace.recent(10);
    assert_eq!(events.len(), 2);

    let success = &events[0]; // newest first
    let failure = &events[1];

    assert_eq!(success.outcome, WireOutcome::Ok);
    assert_eq!(success.attempt, 1);

    assert_eq!(failure.outcome, WireOutcome::Failure);
    assert_eq!(failure.failure_kind, WireFailureKind::ChecksumMismatch);
    assert_eq!(failure.attempt, 0);
}

#[test]
fn agent_originated_writes_are_tagged_as_such_in_the_trace() {
    let transport = FakeTransport::new();
    transport.enqueue([reply(b"MS3")]);

    let trace = Arc::new(WireTrace::default());
    let connection = connection(&transport).with_trace(Arc::clone(&trace));

    {
        let _agent = WireOrigin::enter(WireOrigin::Agent);
        connection.read_signature().unwrap();
    }

    assert_eq!(trace.recent(1)[0].origin, WireOrigin::Agent);
}

// ----- WireTraceTests: the ring buffer itself -----------------------------

use olv_core::model::WireAttempt;

fn ok(context: &str) -> WireAttempt {
    WireAttempt {
        context: context.to_string(),
        ..WireAttempt::default()
    }
}

fn failing(context: &str, kind: WireFailureKind) -> WireAttempt {
    WireAttempt {
        context: context.to_string(),
        elapsed: Duration::from_millis(5),
        outcome: WireOutcome::Failure,
        failure_kind: kind,
        ..WireAttempt::default()
    }
}

#[test]
fn old_events_are_evicted_once_capacity_is_reached() {
    let trace = WireTrace::new(3);

    for i in 0..5 {
        trace.record(ok(&format!("call {i}")));
    }

    let recent = trace.recent(10);

    // Only the last three survive, newest first.
    assert_eq!(recent.len(), 3);
    assert_eq!(recent[0].context, "call 4");
    assert_eq!(recent[1].context, "call 3");
    assert_eq!(recent[2].context, "call 2");
}

#[test]
fn recent_asks_for_more_than_exist_returns_what_there_is() {
    let trace = WireTrace::default();
    trace.record(ok("only one"));

    assert_eq!(trace.recent(50).len(), 1);
}

#[test]
fn empty_trace_has_nothing_to_offer_and_a_clean_bill_of_health() {
    let trace = WireTrace::default();

    assert!(trace.recent(10).is_empty());

    let health = trace.health(200);
    assert_eq!(health.sampled, 0);
    assert_eq!(health.success_rate, 1.0);
    assert!(health.since_last_success.is_none());
}

#[test]
fn health_counts_failures_by_kind() {
    let trace = WireTrace::default();

    trace.record(ok("a"));
    trace.record(failing("b", WireFailureKind::ChecksumMismatch));
    trace.record(failing("c", WireFailureKind::Timeout));
    trace.record(failing("d", WireFailureKind::ChecksumMismatch));
    trace.record(ok("e"));

    let health = trace.health(200);
    let count = |kind: &str| health.failures_by_kind.iter().find(|(k, _)| k == kind).map(|(_, n)| *n);

    assert_eq!(health.sampled, 5);
    assert_eq!(health.failures, 3);
    assert!((health.success_rate - 0.4).abs() < 1e-5);
    assert_eq!(count("ChecksumMismatch"), Some(2));
    assert_eq!(count("Timeout"), Some(1));
    assert!(health.since_last_success.is_some());
}

#[test]
fn health_looks_only_at_the_requested_window() {
    let trace = WireTrace::default();

    for i in 0..10 {
        trace.record(failing(&format!("old {i}"), WireFailureKind::Timeout));
    }

    trace.record(ok("recent"));

    let health = trace.health(1);

    assert_eq!(health.sampled, 1);
    assert_eq!(health.failures, 0);
    assert_eq!(health.success_rate, 1.0);
}

#[test]
fn events_recorded_outside_any_scope_are_attributed_to_a_human() {
    let trace = WireTrace::default();
    trace.record(ok("unscoped"));

    assert_eq!(trace.recent(1)[0].origin, WireOrigin::Human);
}

#[test]
fn events_recorded_inside_an_agent_scope_are_attributed_to_the_agent() {
    let trace = WireTrace::default();

    {
        let _agent = WireOrigin::enter(WireOrigin::Agent);
        trace.record(ok("agent call"));
    }

    trace.record(ok("human call"));

    let recent = trace.recent(2);
    assert_eq!(recent[0].origin, WireOrigin::Human); // "human call", newest first
    assert_eq!(recent[1].origin, WireOrigin::Agent); // "agent call"
}

#[test]
fn leaving_the_scope_restores_what_came_before() {
    {
        let _outer = WireOrigin::enter(WireOrigin::Agent);
        {
            let _inner = WireOrigin::enter(WireOrigin::Human);
            assert_eq!(WireOrigin::current(), WireOrigin::Human);
        }

        assert_eq!(WireOrigin::current(), WireOrigin::Agent);
    }

    assert_eq!(WireOrigin::current(), WireOrigin::Human);
}

// ----- byte-level: every request, for each family -------------------------

/// A Speeduino page (INI 202501.7): little-endian, the page named by `%2i`.
fn speeduino_page() -> TunePage {
    TunePage {
        index: 0,
        size: 0x100,
        identifier: r"\$tsCanId\x01".to_string(),
        read_command: "p%2i%2o%2c".to_string(),
        chunk_write_command: "M%2i%2o%2c%v".to_string(),
        burn_command: "B%2i".to_string(),
    }
}

/// An MS2/MS3 page (MS2Extracomms342h2.ini): big-endian, the page named in the command.
fn ms2_page() -> TunePage {
    TunePage {
        index: 0,
        size: 0x100,
        identifier: r"\$tsCanId\x04".to_string(),
        read_command: r"r\$tsCanId\x04%2o%2c".to_string(),
        chunk_write_command: r"w\$tsCanId\x04%2o%2c%v".to_string(),
        burn_command: r"b\$tsCanId\x04".to_string(),
    }
}

/// A rusEFI page (rusefi_uaefi.ini): little-endian, page 3.
fn rusefi_page() -> TunePage {
    TunePage {
        index: 3,
        size: 0x100,
        identifier: r"\x00\x03".to_string(),
        read_command: "R%2i%2o%2c".to_string(),
        chunk_write_command: "C%2i%2o%2c%v".to_string(),
        burn_command: "B%2i".to_string(),
    }
}

const DATA: [u8; 4] = [0xDE, 0xAD, 0xBE, 0xEF];

fn model(little_endian: bool, behaviour: Misbehaviour) -> (FakeTransport, Arc<Mutex<Vec<u8>>>) {
    let memory = Arc::new(Mutex::new((0..=255u8).collect::<Vec<u8>>()));
    let transport = FakeTransport::responding(ecu_model(Arc::clone(&memory), little_endian, behaviour));
    (transport, memory)
}

#[test]
fn the_identity_commands_are_s_q_and_v_one_attempt_each() {
    let transport = FakeTransport::new();
    transport.enqueue([
        reply(b"speeduino 202501-dev"),
        reply_with_status(0x83, &[]),
        reply(b"Speeduino 2025.01"),
    ]);

    let connection = connection(&transport);
    let said = connection.read_identity().unwrap();

    assert_eq!(said, ["speeduino 202501-dev", "Speeduino 2025.01"]);
    assert_eq!(
        transport.written(),
        [hex("0001532060EFC3"), hex("000151CE6E8EEF"), hex("000156500A1B4C")]
    );
    // A refusal is not retried while probing, and nothing is counted as a retry.
    assert_eq!(connection.retries(), 0);
}

#[test]
fn a_reply_of_fewer_than_seven_characters_is_status_text_and_not_an_identity() {
    let transport = FakeTransport::new();
    transport.enqueue([reply(b"OK"), reply(b"MS3 Format 0569.00"), reply(b"MS3 Format 0569.00")]);

    let said = connection(&transport).read_identity().unwrap();

    // And a signature reported twice is listed once.
    assert_eq!(said, ["MS3 Format 0569.00"]);
}

#[test]
fn an_ecu_that_will_not_say_what_it_is_is_reopened_and_left_alone_to_boot() {
    // Opening a Speeduino's port resets the board; it is still in its bootloader when
    // asked straight away, and asking harder leaves it stuck there. The recovery is a
    // fresh port and then silence.
    let transport = FakeTransport::new();
    let connection = with_settings(
        &transport,
        EcuConnectionSettings {
            boot_wait: Duration::from_millis(1),
            identify_within: Duration::from_millis(40),
            timeout: Duration::from_millis(1),
            ..EcuConnectionSettings::default()
        },
    );

    // The board answers only after the second reopening.
    let before = transport.written().len();
    assert!(connection.read_identity().unwrap().is_empty());

    let (opens, closes) = transport.with(|s| (s.opens, s.closes));
    assert!(opens >= 1, "the port was never reopened");
    assert_eq!(opens, closes, "every reopening closes first");
    assert!(
        transport.written().len() > before + 3,
        "it asked again after each reopening"
    );
}

#[test]
fn identification_stops_at_the_first_round_that_gets_an_answer() {
    let transport = FakeTransport::new();
    // Round one: nothing. After a reopening: the board is up.
    transport.with(|s| s.repeating = None);

    let connection = with_settings(
        &transport,
        EcuConnectionSettings {
            boot_wait: Duration::from_millis(1),
            identify_within: Duration::from_secs(5),
            timeout: Duration::from_millis(1),
            ..EcuConnectionSettings::default()
        },
    );

    // Three silent questions, then (after the reopening) a rusEFI-style answer.
    transport.enqueue([Vec::new(), Vec::new(), Vec::new(), reply(b"rusEFI 2024.01.01")]);

    let said = connection.read_identity().unwrap();

    assert_eq!(said, ["rusEFI 2024.01.01"]);
    assert_eq!(transport.with(|s| s.opens), 1);
}

#[test]
fn speeduino_requests_are_byte_exact() {
    let (transport, _) = model(true, Misbehaviour::None);
    let connection = connection(&transport);
    let page = speeduino_page();

    connection
        .read_tune_page_range(&page, 0, true, 0x10, 0x20, None)
        .unwrap();
    connection.write_tune_page(&page, 0, true, 0x10, &DATA, 0).unwrap();
    connection.burn_page(&page, true, 0).unwrap();

    let written = transport.written();
    assert_eq!(written.len(), 4);
    assert_eq!(written[0], hex("00077000011000200089F955D8")); // read 0x20 at 0x10
    assert_eq!(written[1], hex("000B4D000110000400DEADBEEFDBD2FE3C")); // write 4 bytes at 0x10
    assert_eq!(&written[2][2..written[2].len() - 4], hex("70000110000400")); // ... and read them back
    assert_eq!(written[3], hex("0003420001FB58B02A")); // burn
}

#[test]
fn a_speeduino_write_is_read_back_with_the_same_page_offset_and_length() {
    let (transport, memory) = model(true, Misbehaviour::None);
    let connection = connection(&transport);

    connection
        .write_tune_page(&speeduino_page(), 0, true, 0x10, &DATA, 0)
        .unwrap();

    let written = transport.written();
    let payloads: Vec<&[u8]> = written.iter().map(|w| &w[2..w.len() - 4]).collect();

    assert_eq!(payloads.len(), 2);
    assert_eq!(payloads[0], hex("4D000110000400DEADBEEF"));
    assert_eq!(payloads[1], hex("70000110000400"));
    assert_eq!(&memory.lock().unwrap()[0x10..0x14], DATA);
}

#[test]
fn ms2_requests_are_byte_exact() {
    let (transport, _) = model(false, Misbehaviour::None);
    let connection = connection(&transport);
    let page = ms2_page();

    connection
        .read_tune_page_range(&page, 0, false, 0x10, 0x20, None)
        .unwrap();
    connection.write_tune_page(&page, 0, false, 0x10, &DATA, 0).unwrap();
    connection.burn_page(&page, false, 0).unwrap();

    let written = transport.written();
    assert_eq!(written.len(), 4);
    assert_eq!(written[0], hex("00077200040010002034533B04"));
    assert_eq!(written[1], hex("000B77000400100004DEADBEEF2EF4E1D8"));
    assert_eq!(&written[2][2..written[2].len() - 4], hex("72000400100004"));
    assert_eq!(written[3], hex("0003620004B37F0245"));
}

#[test]
fn rusefi_requests_are_byte_exact() {
    let (transport, _) = model(true, Misbehaviour::BurnStatus(0x04));
    let connection = connection(&transport);
    let page = rusefi_page();

    connection
        .read_tune_page_range(&page, 0, true, 0x10, 0x20, None)
        .unwrap();
    connection.write_tune_page(&page, 0, true, 0x10, &DATA, 0).unwrap();
    connection.burn_page(&page, true, 0).unwrap();

    let written = transport.written();
    assert_eq!(written.len(), 4);
    assert_eq!(written[0], hex("000752000310002000AB1B2E0D"));
    assert_eq!(written[1], hex("000B43000310000400DEADBEEFA752FDBC"));
    assert_eq!(&written[2][2..written[2].len() - 4], hex("52000310000400"));
    assert_eq!(written[3], hex("00034200031556D106"));
}

#[test]
fn the_can_id_goes_where_the_template_puts_it() {
    let (transport, _) = model(false, Misbehaviour::None);
    let connection = with_settings(
        &transport,
        EcuConnectionSettings {
            can_id: 5,
            ..EcuConnectionSettings::default()
        },
    );

    connection
        .read_tune_page_range(&ms2_page(), 0, false, 0, 4, None)
        .unwrap();
    connection.burn_page(&ms2_page(), false, 0).unwrap();

    let written = transport.written();
    assert_eq!(&written[0][2..written[0].len() - 4], hex("72050400000004"));
    assert_eq!(&written[1][2..written[1].len() - 4], hex("620504"));
}

#[test]
fn a_write_is_cut_into_chunks_eight_bytes_smaller_than_the_blocking_factor() {
    // The blocking factor bounds a write as it does a read; the payload has to fit in one
    // message along with its header.
    let (transport, memory) = model(true, Misbehaviour::None);
    let connection = connection(&transport);
    let data: Vec<u8> = (100..120).collect();

    connection
        .write_tune_page(&rusefi_page(), 16, true, 0x20, &data, 0)
        .unwrap();

    let written = transport.written();
    let payload = |i: usize| written[i][2..written[i].len() - 4].to_vec();

    // 20 bytes, 8 to a message: 8 at 0x20, 8 at 0x28, 4 at 0x30...
    assert_eq!(payload(0)[..7], hex("43000320000800"));
    assert_eq!(payload(0)[7..], data[..8]);
    assert_eq!(payload(1)[..7], hex("43000328000800"));
    assert_eq!(payload(1)[7..], data[8..16]);
    assert_eq!(payload(2)[..7], hex("43000330000400"));
    assert_eq!(payload(2)[7..], data[16..]);

    // ... then read back in blocks of 16: 16 at 0x20 and 4 at 0x30.
    assert_eq!(payload(3), hex("52000320001000"));
    assert_eq!(payload(4), hex("52000330000400"));
    assert_eq!(written.len(), 5);
    assert_eq!(&memory.lock().unwrap()[0x20..0x34], data.as_slice());
}

#[test]
fn a_blocking_factor_too_small_for_a_header_sends_the_write_whole() {
    let (transport, _) = model(true, Misbehaviour::None);
    let connection = connection(&transport);

    connection
        .write_tune_page(&rusefi_page(), 8, true, 0, &DATA, 0)
        .unwrap();

    // 8 - 8 = 0, below one: the whole write in one message.
    let written = transport.written();
    assert_eq!(&written[0][2..written[0].len() - 4], hex("43000300000400DEADBEEF"));
}

#[test]
fn a_big_page_is_read_in_blocks_of_the_blocking_factor() {
    // 22,960 bytes on a rusEFI where one reply holds 1,024.
    let memory = Arc::new(Mutex::new(vec![0x5A; 22_960]));
    let transport = FakeTransport::responding(ecu_model(Arc::clone(&memory), true, Misbehaviour::None));
    let connection = connection(&transport);
    let page = TunePage {
        index: 0,
        size: 22_960,
        ..rusefi_page()
    };

    let mut seen = Vec::new();
    let mut progress = |at: i32| seen.push(at);
    let image = connection
        .read_tune_page(&page, 1024, true, Some(&mut progress))
        .unwrap();

    assert_eq!(image.len(), 22_960);
    assert_eq!(transport.written().len(), 23);
    assert_eq!(seen.first(), Some(&1024));
    assert_eq!(seen.last(), Some(&22_960));
    assert_eq!(seen.len(), 23);

    // The last block is the short one: 22,960 - 22 * 1,024 = 432 at 22,528 (0x5800), little-endian.
    let written = transport.written();
    let last = &written[22];
    assert_eq!(
        &last[2..last.len() - 4],
        hex("520003")
            .iter()
            .copied()
            .chain(hex("0058B001"))
            .collect::<Vec<u8>>()
    );
}

#[test]
fn a_page_whose_read_command_cannot_ask_for_part_of_it_is_refused() {
    let transport = FakeTransport::new();
    let connection = connection(&transport);
    let page = TunePage {
        read_command: "A".to_string(),
        ..ms2_page()
    };

    let error = connection.read_tune_page(&page, 0, false, None).unwrap_err();

    assert!(error.to_string().contains("which cannot ask for part of a page"));
    assert!(transport.written().is_empty());
}

#[test]
fn a_realtime_block_is_asked_for_in_pieces_when_the_firmware_declares_a_blocking_factor() {
    let ini = "[OutputChannels]\nochGetCommand = \"r\\$tsCanId\\x07%2o%2c\"\nochBlockSize = 40\n\
               [Constants]\nendianness = big\nblockingFactor = 16\n";
    let layout = msq_ini::read_output_channels(ini, None);

    let memory = Arc::new(Mutex::new((0..40u8).collect::<Vec<u8>>()));
    let transport = FakeTransport::responding(ecu_model(Arc::clone(&memory), false, Misbehaviour::None));
    let connection = connection(&transport);
    connection.use_layout(&layout, false);

    let block = connection.read_realtime(40).unwrap();

    assert_eq!(block, (0..40u8).collect::<Vec<u8>>());
    let written = transport.written();
    let payloads: Vec<&[u8]> = written.iter().map(|w| &w[2..w.len() - 4]).collect();
    assert_eq!(
        payloads,
        [hex("72000700000010"), hex("72000700100010"), hex("72000700200008")]
    );
}

#[test]
fn a_single_request_ignores_the_blocking_factor() {
    let ini = "[OutputChannels]\nochGetCommand = \"r\\$tsCanId\\x07%2o%2c\"\nochBlockSize = 40\n\
               [Constants]\nendianness = big\nblockingFactor = 16\n";
    let layout = msq_ini::read_output_channels(ini, None);

    let memory = Arc::new(Mutex::new((0..40u8).collect::<Vec<u8>>()));
    let transport = FakeTransport::responding(ecu_model(Arc::clone(&memory), false, Misbehaviour::None));
    let connection = connection(&transport);
    connection.use_layout(&layout, true);

    assert_eq!(connection.read_realtime(40).unwrap().len(), 40);

    let written = transport.written();
    assert_eq!(written.len(), 1);
    assert_eq!(&written[0][2..written[0].len() - 4], hex("72000700000028"));
}

#[test]
fn the_plain_a_command_asks_for_everything_at_once() {
    let ini = "[OutputChannels]\nochGetCommand = \"A\"\nochBlockSize = 40\n[Constants]\nblockingFactor = 16\n";
    let layout = msq_ini::read_output_channels(ini, None);

    let transport = FakeTransport::new();
    transport.enqueue([reply(&[7u8; 40])]);
    let connection = connection(&transport);
    connection.use_layout(&layout, false);

    assert_eq!(connection.read_realtime(40).unwrap(), vec![7u8; 40]);
    assert_eq!(transport.written(), [hex("000141D3D99E8B")]);
}

#[test]
fn the_ms3_realtime_request_is_byte_exact() {
    let transport = FakeTransport::new();
    transport.enqueue([reply(&[0u8; 512])]);

    connection(&transport).read_realtime(512).unwrap();

    assert_eq!(transport.written(), [hex("000772000700000200668DA0EE")]);
}

#[test]
fn a_reply_of_the_wrong_length_is_treated_as_belonging_to_an_earlier_request() {
    // There is no echo of the offset or the count, so a late reply from a previous
    // attempt is otherwise decoded as the answer to this one: a stationary car at 230 km/h.
    let transport = FakeTransport::new();
    transport.enqueue([
        reply(&[1u8; 10]),
        reply(&[1u8; 10]),
        reply(&[1u8; 10]),
        reply(&[1u8; 10]),
    ]);

    let trace = Arc::new(WireTrace::default());
    let connection = connection(&transport).with_trace(Arc::clone(&trace));

    let error = connection.read_realtime(16).unwrap_err();

    assert!(error.to_string().contains("belongs to an earlier request"));
    assert_eq!(connection.retries(), 3);
    assert!(
        trace
            .recent(10)
            .iter()
            .all(|e| e.failure_kind == WireFailureKind::Malformed)
    );
}

// ----- writes and how they fail -------------------------------------------

#[test]
fn a_write_outside_the_page_is_refused_before_anything_is_sent() {
    let transport = FakeTransport::new();
    let connection = connection(&transport);
    let page = TunePage {
        size: 0x20,
        ..ms2_page()
    };

    let error = connection.write_tune_page(&page, 0, false, 0x1E, &DATA, 0).unwrap_err();

    assert_eq!(error.to_string(), "That write ends at 34 of a 32 byte page.");
    assert!(error.is_protocol());
    assert!(transport.written().is_empty());
}

#[test]
fn a_write_to_a_page_with_no_write_command_is_refused_before_anything_is_sent() {
    let transport = FakeTransport::new();
    let connection = connection(&transport);
    let page = TunePage {
        chunk_write_command: String::new(),
        ..ms2_page()
    };

    let error = connection.write_tune_page(&page, 0, false, 0, &DATA, 0).unwrap_err();

    assert_eq!(
        error.to_string(),
        "Page 0 declares no write command, so this firmware cannot be written to."
    );
    assert!(transport.written().is_empty());
}

#[test]
fn a_negative_offset_is_refused_before_anything_is_sent() {
    let transport = FakeTransport::new();
    let connection = connection(&transport);

    assert!(connection.write_tune_page(&ms2_page(), 0, false, -1, &DATA, 0).is_err());
    assert!(transport.written().is_empty());
}

#[test]
fn writing_nothing_sends_nothing() {
    let transport = FakeTransport::new();
    let connection = connection(&transport);

    connection.write_tune_page(&ms2_page(), 0, false, 0, &[], 0).unwrap();

    assert!(transport.written().is_empty());
}

#[test]
fn a_write_the_ecu_acknowledges_and_ignores_is_not_reported_as_having_worked() {
    // An acknowledgement only says the command was understood. Reading the range back is
    // the only thing that tells a write that worked from one that went somewhere else.
    let (transport, memory) = model(false, Misbehaviour::IgnoresWrites);
    let connection = connection(&transport);

    let error = connection
        .write_tune_page(&ms2_page(), 0, false, 0x10, &DATA, 0)
        .unwrap_err();

    assert!(
        error
            .to_string()
            .starts_with("The ECU did not take that write: 4 bytes were sent to offset 16 of page 0")
    );
    assert!(
        error
            .to_string()
            .ends_with("Nothing has been burned, so a power cycle restores the ECU.")
    );
    assert!(!error.refused());
    // And the burn was never asked for.
    assert!(transport.written().iter().all(|w| w[2] != b'b'));
    assert_eq!(memory.lock().unwrap()[0x10], 0x10);
}

#[test]
fn a_refused_write_is_retried_like_any_other_request_and_reported_as_refused() {
    let transport = FakeTransport::new();
    transport.with(|s| s.repeating = Some(reply_with_status(0x83, &[])));
    let connection = connection(&transport);

    let error = connection
        .write_tune_page(&ms2_page(), 0, false, 0, &DATA, 0)
        .unwrap_err();

    assert!(error.refused());
    // A refusal is also what a desynchronised stream looks like, so it is asked again:
    // the original attempt and three retries, and no read-back after.
    assert_eq!(transport.written().len(), 4);
    assert_eq!(connection.retries(), 3);
}

#[test]
fn a_dead_link_during_a_write_is_reported_and_not_read_back() {
    let transport = FakeTransport::new(); // never replies
    let connection = with_settings(
        &transport,
        EcuConnectionSettings {
            retries: 1,
            timeout: Duration::from_millis(1),
            ..EcuConnectionSettings::default()
        },
    );

    let error = connection
        .write_tune_page(&ms2_page(), 0, false, 0, &DATA, 0)
        .unwrap_err();

    assert_eq!(error.to_string(), "The ECU did not reply.");
    assert_eq!(transport.written().len(), 2);
}

// ----- burn ---------------------------------------------------------------

#[test]
fn a_burn_is_sent_once_and_the_write_timeout_is_widened_only_for_its_length() {
    let (transport, _) = model(true, Misbehaviour::None);
    let connection = connection(&transport);

    connection.burn_page(&speeduino_page(), true, 0).unwrap();

    assert_eq!(transport.written().len(), 1);
    // Widened to the burn allowance (8 s), then put straight back.
    assert_eq!(
        transport.with(|s| s.write_timeout_history.clone()),
        [Duration::from_secs(8), Duration::from_millis(500)]
    );
    assert_eq!(transport.with(|s| s.write_timeout), Duration::from_millis(500));
}

#[test]
fn a_rusefi_burn_acknowledged_with_status_04_is_a_burn_that_worked() {
    // Insisting on 0x00 made a successful burn read as a refusal: the board had written
    // its flash, answered 0x04 to say so, and was told the burn had been declined.
    let (transport, _) = model(true, Misbehaviour::BurnStatus(0x04));
    let connection = connection(&transport);

    connection.burn_page(&rusefi_page(), true, 0).unwrap();
}

#[test]
fn a_burn_the_ecu_refuses_is_reported_as_refused_and_not_as_unconfirmed() {
    // A controller that answered with a refusal has told us it did not burn, and saying it
    // might have would send somebody to power-cycle a car over a burn that demonstrably
    // did not happen.
    let (transport, _) = model(true, Misbehaviour::RefusesBurn);
    let connection = connection(&transport);

    let error = connection.burn_page(&speeduino_page(), true, 0).unwrap_err();

    assert!(error.refused());
    assert_eq!(error.to_string(), "The ECU refused the request (status 0x83).");
    assert_eq!(transport.written().len(), 1);
    assert_eq!(connection.retries(), 0);
    assert_eq!(transport.with(|s| s.write_timeout), Duration::from_millis(500));
}

#[test]
fn a_burn_the_ecu_never_answers_is_reported_as_unconfirmed_and_never_resent() {
    // The ECU may well have done the work and gone quiet doing it; re-sending would spend a
    // flash erase to learn nothing, and calling it a failure is what sends somebody to
    // write it again.
    let (transport, _) = model(true, Misbehaviour::SilentBurn);
    let connection = with_settings(
        &transport,
        EcuConnectionSettings {
            burn_timeout: Duration::from_millis(5),
            ..EcuConnectionSettings::default()
        },
    );

    let error = connection.burn_page(&speeduino_page(), true, 0).unwrap_err();
    let message = error.to_string();

    assert!(!error.refused());
    assert!(message.starts_with("The ECU did not confirm the burn of page 0: The ECU did not reply."));
    assert!(message.contains("It may still have completed"));
    assert!(message.contains("read the tune back"));
    assert_eq!(transport.written().len(), 1, "a burn is sent once");
    assert_eq!(connection.retries(), 0);
    assert_eq!(transport.with(|s| s.write_timeout), Duration::from_millis(500));
}

#[test]
fn a_page_with_no_burn_command_cannot_be_burned() {
    let transport = FakeTransport::new();
    let connection = connection(&transport);
    let page = TunePage {
        burn_command: String::new(),
        ..rusefi_page()
    };

    let error = connection.burn_page(&page, true, 0).unwrap_err();

    assert_eq!(error.to_string(), "Page 3 declares no burn command.");
    assert!(transport.written().is_empty());
    assert!(transport.with(|s| s.write_timeout_history.is_empty()));
}

#[test]
fn the_pause_after_a_burn_is_taken_and_the_pause_between_writes_too() {
    let (transport, _) = model(true, Misbehaviour::None);
    let connection = connection(&transport);
    let data: Vec<u8> = (0..24).collect();

    let started = std::time::Instant::now();
    connection
        .write_tune_page(&rusefi_page(), 16, true, 0, &data, 30)
        .unwrap();
    let writing = started.elapsed();

    // 24 bytes, 8 to a message: three messages, two pauses between them.
    assert!(writing >= Duration::from_millis(60), "paused {writing:?}");

    let started = std::time::Instant::now();
    connection.burn_page(&rusefi_page(), true, 40).unwrap();
    assert!(started.elapsed() >= Duration::from_millis(40));
}

// ----- one conversation at a time -----------------------------------------

mod gate {
    use super::*;
    use olv_core::Result;
    use olv_protocol::ms::ms_protocol::EcuTransport;
    use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};

    /// A transport that stops inside the write and counts who is in there.
    struct Blocking {
        inside: Arc<AtomicI32>,
        deepest: Arc<AtomicI32>,
        release: Arc<AtomicBool>,
        reply: Vec<u8>,
        pending: Vec<u8>,
    }

    impl EcuTransport for Blocking {
        fn is_open(&self) -> bool {
            true
        }

        fn open(&mut self) -> Result<()> {
            Ok(())
        }

        fn close(&mut self) {}

        fn write(&mut self, _data: &[u8]) -> Result<()> {
            let depth = self.inside.fetch_add(1, Ordering::SeqCst) + 1;
            self.deepest.fetch_max(depth, Ordering::SeqCst);

            let started = std::time::Instant::now();
            while !self.release.load(Ordering::SeqCst) && started.elapsed() < Duration::from_secs(5) {
                std::thread::sleep(Duration::from_millis(2));
            }

            self.inside.fetch_sub(1, Ordering::SeqCst);
            self.pending = self.reply.clone();
            Ok(())
        }

        fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
            let take = buffer.len().min(self.pending.len());
            buffer[..take].copy_from_slice(&self.pending[..take]);
            self.pending.drain(..take);
            Ok(take)
        }

        fn discard_input(&mut self) {}
    }

    /// A live session polls from its own thread while the window asks for something else.
    /// Two requests interleaved on one serial line are one ruined conversation, not two:
    /// the poll reads the write's acknowledgement as its block of channels.
    #[test]
    fn a_second_conversation_waits_for_the_first() {
        let inside = Arc::new(AtomicI32::new(0));
        let deepest = Arc::new(AtomicI32::new(0));
        let release = Arc::new(AtomicBool::new(false));

        let transport = Blocking {
            inside: Arc::clone(&inside),
            deepest: Arc::clone(&deepest),
            release: Arc::clone(&release),
            reply: reply(b"MS3 Format 0569.00"),
            pending: Vec::new(),
        };
        let connection = Arc::new(EcuConnection::with_defaults(Box::new(transport)));

        let first = {
            let c = Arc::clone(&connection);
            std::thread::spawn(move || c.read_signature())
        };

        while inside.load(Ordering::SeqCst) == 0 {
            std::thread::sleep(Duration::from_millis(2));
        }

        let second = {
            let c = Arc::clone(&connection);
            std::thread::spawn(move || c.read_version())
        };

        // Long enough for the second to have got in if it were going to.
        std::thread::sleep(Duration::from_millis(300));
        assert_eq!(deepest.load(Ordering::SeqCst), 1);

        release.store(true, Ordering::SeqCst);
        assert_eq!(first.join().unwrap().unwrap(), "MS3 Format 0569.00");
        assert_eq!(second.join().unwrap().unwrap(), "MS3 Format 0569.00");
        assert_eq!(deepest.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn the_retry_count_can_be_read_while_a_conversation_is_in_progress() {
        let release = Arc::new(AtomicBool::new(false));
        let inside = Arc::new(AtomicI32::new(0));

        let transport = Blocking {
            inside: Arc::clone(&inside),
            deepest: Arc::new(AtomicI32::new(0)),
            release: Arc::clone(&release),
            reply: reply(b"MS3"),
            pending: Vec::new(),
        };
        let connection = Arc::new(EcuConnection::with_defaults(Box::new(transport)));

        let busy = {
            let c = Arc::clone(&connection);
            std::thread::spawn(move || c.read_signature())
        };

        while inside.load(Ordering::SeqCst) == 0 {
            std::thread::sleep(Duration::from_millis(2));
        }

        // The C# property takes no lock, and a status bar must not wait on the wire.
        assert_eq!(connection.retries(), 0);

        release.store(true, Ordering::SeqCst);
        busy.join().unwrap().unwrap();
    }
}

#[test]
fn the_bluetooth_settings_are_slower_and_retry_less() {
    let bluetooth = EcuConnectionSettings::bluetooth();
    let default = EcuConnectionSettings::default();

    assert_eq!(bluetooth.timeout, Duration::from_millis(1000));
    assert_eq!(bluetooth.settle_for, Duration::from_millis(1200));
    assert_eq!(bluetooth.quiet_for, Duration::from_millis(120));
    assert_eq!(bluetooth.retries, 2);
    assert_eq!(bluetooth.burn_timeout, default.burn_timeout);
    assert_eq!(default.timeout, Duration::from_millis(500));
    assert_eq!(default.retries, 3);
    assert_eq!(default.burn_timeout, Duration::from_secs(8));
}

#[test]
fn a_transport_error_is_wrapped_retried_and_traced_as_a_transport_error() {
    use olv_core::Result;
    use olv_protocol::ms::ms_protocol::EcuTransport;

    // The transport raises an IOException where the port itself misbehaves rather than the
    // ECU; catching only the protocol exception let that escape the retry loop unwrapped.
    struct Failing;

    impl EcuTransport for Failing {
        fn is_open(&self) -> bool {
            true
        }
        fn open(&mut self) -> Result<()> {
            Ok(())
        }
        fn close(&mut self) {}
        fn write(&mut self, _data: &[u8]) -> Result<()> {
            Err(olv_core::Error::Io(std::io::Error::other(
                "The semaphore timeout period has expired.",
            )))
        }
        fn read(&mut self, _buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
            Err(olv_core::Error::Io(std::io::Error::other("gone")))
        }
        fn discard_input(&mut self) {}
    }

    let trace = Arc::new(WireTrace::default());
    let connection = EcuConnection::with_defaults(Box::new(Failing)).with_trace(Arc::clone(&trace));

    let error = connection.read_signature().unwrap_err();

    assert!(error.is_protocol());
    assert_eq!(
        error.to_string(),
        "The link to the ECU failed: The semaphore timeout period has expired."
    );
    assert_eq!(connection.retries(), 3);

    let events = trace.recent(10);
    assert_eq!(events.len(), 4);
    assert!(events.iter().all(|e| e.failure_kind == WireFailureKind::TransportError));
    assert!(
        events
            .iter()
            .all(|e| e.request_bytes == ms_protocol::frame(b"Q").len() as i32)
    );
}

#[test]
fn the_signature_query_is_q_and_the_version_query_is_s() {
    let transport = FakeTransport::new();
    transport.enqueue([reply(b"MS3 Format 0569.00"), reply(b"MS3 build 1")]);

    let connection = connection(&transport);
    assert_eq!(connection.read_signature().unwrap(), "MS3 Format 0569.00");
    assert_eq!(connection.read_version().unwrap(), "MS3 build 1");

    assert_eq!(transport.written(), [hex("000151CE6E8EEF"), hex("0001532060EFC3")]);
}

#[test]
fn an_unchunked_read_ignores_the_blocking_factor_and_makes_room_for_the_reply() {
    // For finding out what a firmware will actually serve. Not the path a session uses: a
    // rusEFI asked for more than it can send has been seen to leave the USB bus.
    let ini = r#"[OutputChannels]
ochGetCommand = "r\$tsCanId\x07%2o%2c"
ochBlockSize = 40
[Constants]
endianness = big
blockingFactor = 16
"#;
    let layout = msq_ini::read_output_channels(ini, None);

    let memory = Arc::new(Mutex::new((0..40u8).collect::<Vec<u8>>()));
    let transport = FakeTransport::responding(ecu_model(Arc::clone(&memory), false, Misbehaviour::None));
    let connection = connection(&transport);
    connection.use_layout(&layout, false);

    // Larger than anything the buffer was sized for: 600 bytes asked of a 40 byte memory
    // would panic the model, so ask for exactly what it holds, and for more than the factor.
    let block = connection.read_realtime_unchunked(40).unwrap();

    assert_eq!(block.len(), 40);
    assert_eq!(transport.written().len(), 1);
    assert_eq!(&transport.written()[0][2..9], hex("72000700000028"));
}

#[test]
fn a_realtime_read_of_nothing_is_refused_before_anything_is_sent() {
    let transport = FakeTransport::new();
    let connection = connection(&transport);

    assert!(connection.read_realtime(0).is_err());
    assert!(connection.read_realtime_unchunked(0).is_err());
    assert!(transport.written().is_empty());
}
