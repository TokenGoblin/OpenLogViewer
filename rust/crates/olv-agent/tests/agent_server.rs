//! The local API an agent talks to. Ported from `AgentServerTests`.
//!
//! The thing being guarded here is not the JSON. It is that a program which can move numbers
//! inside a running engine now listens on a socket, and that the two properties keeping that
//! reasonable (loopback only, and nothing written unless a person armed it) hold whatever the
//! caller does.

mod support;

use std::sync::Arc;
use std::time::{Duration, Instant};

use olv_agent::agent::agent_api::*;
use olv_agent::agent::agent_server::{AgentServer, AgentServerSettings};
use olv_core::ini::msq_ini::RealtimeType;
use olv_core::ini::tune_layout::TuneConstant;
use olv_core::tune::tune_compare::TuneDifference;
use olv_store::store::agent_activity_log::AgentActivityKind;
use serde_json::{Value, json};
use support::bench::{Bench, needed};
use support::http::{TOKEN, connect, get, next, post, raw, send, subscribe};

fn serve() -> (AgentServer, Arc<Bench>) {
    let bench = Arc::new(Bench::new());
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

fn body(value: Value) -> String {
    value.to_string()
}

// ----- the two properties that matter -------------------------------------------------

#[test]
fn it_listens_on_the_loopback_address_and_nowhere_else() {
    // The failure this forecloses is a "host" setting somebody turns into 0.0.0.0 to make
    // something work, which puts a socket that can write to an engine on whatever network the
    // laptop is on.
    let (server, _) = serve();

    assert!(server.address().starts_with("http://127.0.0.1:"));
}

#[test]
fn the_listening_socket_really_is_bound_to_loopback_only() {
    // Not just the address string: ask the operating system what the socket is bound to, by
    // trying the one thing a wildcard bind would allow. A socket on 0.0.0.0:port refuses a second
    // loopback bind of the same port; one on 127.0.0.1:port refuses it too, so tell them apart by
    // binding the same port on a *different* local address (127.0.0.2 is also loopback on every
    // platform that has it, and a 0.0.0.0 listener would conflict with it).
    let (server, _) = serve();

    let other = std::net::TcpListener::bind(("127.0.0.2", server.port()));

    // Where 127.0.0.2 does not exist at all (macOS) the bind fails for that reason, which also
    // proves nothing contrary; where it does, the bind succeeds only if we are not on 0.0.0.0.
    if let Ok(listener) = other {
        drop(listener);
    } else if cfg!(not(target_os = "macos")) {
        panic!("the port was taken on another local address, so the listener is not on 127.0.0.1 only");
    }
}

#[test]
fn nothing_is_answered_without_the_token() {
    let (server, _) = serve();

    let answer = raw(server.port(), "GET", "/state", &[], "");

    assert_eq!(answer.status, 401);
}

#[test]
fn nor_with_the_wrong_one() {
    let (server, _) = serve();

    let answer = raw(
        server.port(),
        "GET",
        "/state",
        &[("Authorization", "Bearer test-token-9876543210")],
        "",
    );

    assert_eq!(answer.status, 401);
}

#[test]
fn a_settings_record_with_no_token_is_refused_outright() {
    // Rather than starting a server nobody can use, or worse, one anybody can.
    for token in ["  ", ""] {
        let refused = AgentServer::new(
            Arc::new(Bench::new()),
            AgentServerSettings {
                port: 0,
                ..AgentServerSettings::new(token)
            },
        );

        assert!(matches!(refused, Err(olv_core::Error::Argument(_))));
    }
}

#[test]
fn a_write_is_refused_until_somebody_arms_it() {
    let (server, bench) = serve();

    let refused = post(
        server.port(),
        "/tune/set",
        &body(json!({"name": "revLimit", "value": 7000})),
    );

    assert_eq!(refused.status, 409);
    assert!(bench.with(|s| s.written.is_empty()));
    assert!(refused.body.contains("not armed"));
}

#[test]
fn and_goes_through_once_it_is() {
    let (server, bench) = serve();
    bench.arm();

    let answer = post(
        server.port(),
        "/tune/set",
        &body(json!({"name": "revLimit", "value": 7000})),
    );

    assert_eq!(answer.status, 200);
    assert_eq!(
        bench.with(|s| s.written.clone()),
        vec![("revLimit".to_string(), 7000.0)]
    );

    // And says plainly that it did not burn, because the difference between "the engine is
    // running this" and "the engine will keep running this" is the whole of what a power cycle
    // undoes.
    assert!(answer.body.contains("\"burned\":false"));
}

#[test]
fn a_rationale_and_confirm_dangerous_are_carried_to_the_bridge_and_echoed_back() {
    // The wire format is the audit trail for this phase: whatever an agent said as its reason
    // has to reach the bridge unchanged, and come back in the answer so it is visible without a
    // second call.
    let (server, bench) = serve();
    bench.arm();

    let answer = post(
        server.port(),
        "/tune/set",
        &body(json!({
            "name": "revLimit", "value": 7000,
            "rationale": "raising the limiter for a dyno pull", "confirmDangerous": true,
        })),
    );

    assert_eq!(answer.status, 200);
    assert_eq!(
        bench.with(|s| s.last_rationale.clone()),
        "raising the limiter for a dyno pull"
    );
    assert!(bench.with(|s| s.last_confirm_dangerous));

    assert!(
        answer
            .body
            .contains("\"rationale\":\"raising the limiter for a dyno pull\"")
    );
}

#[test]
fn a_table_set_also_carries_rationale_through() {
    let (server, bench) = serve();
    bench.arm();

    let answer = post(
        server.port(),
        "/table/set",
        &body(json!({"table": "VE Table", "column": 0, "row": 0, "value": 55, "rationale": "smoothing a flat spot"})),
    );

    assert_eq!(answer.status, 200);
    assert_eq!(bench.with(|s| s.last_rationale.clone()), "smoothing a flat spot");
    assert!(!bench.with(|s| s.last_confirm_dangerous));
    assert!(answer.body.contains("\"rationale\":\"smoothing a flat spot\""));
}

#[test]
fn a_table_set_reports_what_actually_landed_rather_than_echoing_the_request() {
    // The edit clamps a table cell to the firmware's declared range instead of refusing it: the
    // right call for a person scaling a whole table, wrong for an agent that asked for one exact
    // number and was told it landed. Found live, on a real Speeduino: a single-cell write
    // answered 200 with the requested value while the ECU silently held something else. The bench
    // clamps to 0-100, standing in for the real firmware's declared range.
    let (server, bench) = serve();
    bench.arm();

    let answer = post(
        server.port(),
        "/table/set",
        &body(json!({"table": "VE Table", "column": 0, "row": 0, "value": 999, "rationale": "clamp visibility check"})),
    );

    assert_eq!(answer.status, 200);
    assert!(answer.body.contains("\"requested\":999"));
    assert!(answer.body.contains("\"value\":100"));
    assert!(answer.body.contains("\"clamped\":true"));
}

#[test]
fn nothing_reachable_from_the_bridge_names_a_controller_command_or_a_burn() {
    // [ControllerCommands] on rusEFI includes ignition-coil-fire test commands, and burning is
    // deliberately human-only. Neither is modelled anywhere in this interface, and this makes
    // that a checked property: it fails the build the moment either kind of member is added,
    // rather than relying on nobody ever adding one. (The C# reflects over the interface; here
    // the trait's own source is read, which is the same list.)
    let source = include_str!("../src/agent/agent_api.rs");
    let trait_start = source.find("pub trait AgentBridge").expect("the trait");
    let body = &source[trait_start..];

    let methods: Vec<&str> = body
        .lines()
        .filter_map(|line| line.trim().strip_prefix("fn "))
        .filter_map(|rest| rest.split(['(', '<']).next())
        .collect();

    assert!(methods.len() >= 25, "found only {methods:?}");

    for name in methods {
        let lower = name.to_ascii_lowercase();

        assert!(
            !lower.contains("controllercommand") && !lower.contains("controller_command"),
            "{name} looks like a ControllerCommand, which must never be agent-reachable."
        );
        assert!(
            !lower.contains("cmd_") && !lower.starts_with("cmd"),
            "{name} looks like a raw controller command, which must never be agent-reachable."
        );
        assert!(
            !lower.contains("burn"),
            "{name} looks like a burn, which must stay human-only."
        );
    }
}

#[test]
fn there_is_no_way_to_burn() {
    // Not "it refuses"; there is no endpoint. A burn is permanent and the person who can see the
    // engine is the one who should press it.
    let (server, bench) = serve();
    bench.arm();

    for path in ["/burn", "/tune/burn", "/table/burn", "/ecu/burn"] {
        let answer = post(server.port(), path, &body(json!({"page": 0})));
        assert_eq!(answer.status, 404, "{path}");
    }
}

// ----- reading -----------------------------------------------------------------------------

#[test]
fn the_state_says_what_is_connected_and_whether_writing_is_armed() {
    let (server, _) = serve();

    let state = get(server.port(), "/state").json();

    assert_eq!(state["mode"], "live");
    assert_eq!(state["signature"], "TEST Format 0001.00");
    assert_eq!(state["writesArmed"], false);
}

#[test]
fn channels_carry_their_role_so_an_agent_need_not_guess_the_spelling() {
    // A rusEFI calls engine speed RPMValue and a MegaSquirt calls it rpm. An agent that has to
    // know that is an agent that works on one firmware.
    let (server, _) = serve();

    let channels = get(server.port(), "/channels").json();

    assert_eq!(channels[0]["name"], "RPM");
    assert_eq!(channels[0]["role"], "EngineSpeed");
}

#[test]
fn raw_channels_are_only_returned_when_asked() {
    let (server, _) = serve();

    assert_eq!(get(server.port(), "/channels").json().as_array().unwrap().len(), 2);
    assert_eq!(
        get(server.port(), "/channels?raw=true")
            .json()
            .as_array()
            .unwrap()
            .len(),
        3
    );
}

#[test]
fn wire_health_reports_what_the_bridge_says() {
    let (server, _) = serve();

    let health = get(server.port(), "/wire/health").json();

    assert_eq!(health["sampled"], 10);
    assert_eq!(health["failures"], 1);
    assert_eq!(health["successRate"], 0.9);
}

#[test]
fn wire_events_come_back_newest_first() {
    let (server, bench) = serve();
    bench.extra_event.store(true, std::sync::atomic::Ordering::SeqCst);

    let events = get(server.port(), "/wire/events").json();

    assert_eq!(events.as_array().unwrap().len(), 2);
    assert_eq!(events[0]["context"], "write page 3");
    assert_eq!(events[0]["origin"], "Agent");
}

#[test]
fn values_come_back_with_their_times() {
    let (server, _) = serve();

    let answer = get(server.port(), "/values?channel=RPM&seconds=5").json();

    assert_eq!(answer["count"], 3);
    assert_eq!(answer["times"].as_array().unwrap().len(), 3);
    assert_eq!(answer["values"][2], 5000);
}

#[test]
fn asking_for_values_without_naming_a_channel_says_what_is_missing() {
    let (server, _) = serve();

    let answer = get(server.port(), "/values");

    assert_eq!(answer.status, 400);
    assert!(answer.body.contains("channel"));
}

#[test]
fn a_table_comes_back_as_rows_of_numbers_with_its_axes() {
    let (server, _) = serve();

    let table = get(server.port(), "/table?name=VE%20Table").json();

    assert_eq!(table["columns"], 2);
    assert_eq!(table["xBins"][0], 800);

    // Row-major, so values[row][column] reads the way the grid looks.
    assert_eq!(table["values"][0][0], 40);
    assert_eq!(table["values"][0][1], 50);
}

#[test]
fn an_unknown_table_is_a_four_oh_four_rather_than_an_empty_one() {
    let (server, _) = serve();

    assert_eq!(get(server.port(), "/table?name=Nope").status, 404);
}

#[test]
fn the_root_says_what_there_is_to_ask() {
    let (server, _) = serve();

    let root = get(server.port(), "/").body;

    assert!(root.contains("/live/stream"));
    assert!(root.contains("/insights"));

    // The write-path limits are read once here, on connect, rather than learned by being
    // refused.
    assert!(root.contains("\"writeRateCount\""));
}

#[test]
fn limits_are_also_an_endpoint_of_their_own() {
    let (server, _) = serve();

    let body = get(server.port(), "/limits").body;

    assert!(body.contains("\"writeRateCount\":10"));
    assert!(body.contains("\"writeRateWindowSeconds\":5"));
    assert!(body.contains("\"maxChangeFractionOfRange\":0.5"));
    assert!(body.contains("\"burns\":false"));
}

// ----- the live stream -----------------------------------------------------------------------

fn names(list: &[&str]) -> Arc<[String]> {
    list.iter().map(|s| s.to_string()).collect()
}

/// Publishes until the subscriber has actually attached.
fn publish_until_seen(server: &AgentServer, names: &Arc<[String]>, values: &[f64]) {
    let deadline = Instant::now() + Duration::from_secs(2);
    while server.subscribers() == 0 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
    }

    for i in 0..20 {
        server.publish(f64::from(i) * 0.04, names, values);
        std::thread::sleep(Duration::from_millis(5));
    }
}

#[test]
fn the_stream_names_its_channels_once_and_then_sends_only_numbers() {
    // A rusEFI publishes 823 channels. At 25 Hz, repeating their names would be megabytes a
    // minute of spelling.
    let (server, _) = serve();
    let mut socket = subscribe(server.port()).expect("connects");

    let channels = names(&["RPM", "AFR"]);
    std::thread::scope(|scope| {
        scope.spawn(|| publish_until_seen(&server, &channels, &[3000.0, 13.2]));

        let schema = next(&mut socket);
        assert_eq!(schema["type"], "schema");
        assert_eq!(schema["channels"][0], "RPM");

        let frame = next(&mut socket);
        assert_eq!(frame["type"], "frame");
        assert_eq!(frame["v"][0], 3000);
        assert_eq!(frame["v"][1], 13.2);

        // No names in the frame at all.
        assert!(frame.get("channels").is_none());
    });
}

#[test]
fn an_agent_can_ask_for_just_the_channels_it_cares_about() {
    // The schema says what the frames after it carry, and is sent again whenever the selection
    // changes, so a filter asked for just after connecting takes effect at the next schema
    // rather than retroactively. An agent reads schemas as they come; this waits for the one it
    // asked for rather than assuming it beat the first frame.
    let (server, _) = serve();
    let mut socket = subscribe(server.port()).expect("connects");
    send(&mut socket, &json!({"channels": ["AFR"]}).to_string());

    let channels = names(&["RPM", "AFR", "CLT"]);
    std::thread::scope(|scope| {
        scope.spawn(|| publish_until_seen(&server, &channels, &[3000.0, 13.2, 88.0]));

        let mut columns = usize::MAX;

        for _ in 0..40 {
            let got = next(&mut socket);
            if got["type"] != "schema" {
                continue;
            }

            columns = got["channels"].as_array().unwrap().len();
            if columns == 1 {
                assert_eq!(got["channels"][0], "AFR");
                break;
            }
        }

        assert_eq!(columns, 1);

        let frame = next(&mut socket);
        assert_eq!(frame["type"], "frame");
        assert_eq!(frame["v"][0], 13.2);
    });
}

#[test]
fn a_raw_subscriber_sees_only_publish_raw_frames() {
    // The filtered stream and the raw one are different subscriptions with different schemas; a
    // subscriber who asked for one must never see a frame meant for the other.
    let (server, _) = serve();
    let mut socket = subscribe(server.port()).expect("connects");
    send(&mut socket, &json!({"raw": true}).to_string());

    let deadline = Instant::now() + Duration::from_secs(2);
    while server.subscribers() == 0 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
    }

    let filtered = names(&["RPM"]);
    let raw_names = names(&["RPM", "internal_padding_1"]);

    std::thread::scope(|scope| {
        scope.spawn(|| {
            // Filtered publishes the raw subscriber must ignore, alongside the raw ones it
            // should see.
            for i in 0..40 {
                server.publish(f64::from(i) * 0.04, &filtered, &[3000.0]);
                server.publish_raw(f64::from(i) * 0.04, &raw_names, &[3000.0, 0.0]);
                std::thread::sleep(Duration::from_millis(5));
            }
        });

        // The subscribe message and the first filtered publish are a race, exactly like the
        // plain channel filter above, so this reads schemas until it sees the raw one (two
        // channels) rather than assuming the first schema already reflects it.
        let mut columns = 0;

        for _ in 0..60 {
            let got = next(&mut socket);
            if got["type"] != "schema" {
                continue;
            }

            columns = got["channels"].as_array().unwrap().len();
            if columns == 2 {
                break;
            }
        }

        assert_eq!(columns, 2);

        let frame = next(&mut socket);
        assert_eq!(frame["type"], "frame");
        assert_eq!(frame["v"][0], 3000);
        assert_eq!(frame["v"][1], 0);
    });
}

#[test]
fn publishing_never_waits_for_a_subscriber() {
    // The property that keeps the API from slowing down what it watches. A subscriber that
    // cannot keep up loses frames; the poll thread does not lose time. Ten thousand frames with
    // nobody reading them must still return promptly.
    let (server, _) = serve();
    let _socket = subscribe(server.port()).expect("connects");

    let channels = names(&["RPM"]);
    let clock = Instant::now();

    for i in 0..10_000 {
        server.publish(f64::from(i) * 0.04, &channels, &[f64::from(i)]);
    }

    assert!(
        clock.elapsed() < Duration::from_secs(2),
        "publishing 10,000 frames took {:?}",
        clock.elapsed()
    );
}

#[test]
fn a_new_frame_replaces_an_undelivered_one_rather_than_queueing_behind_it() {
    // An agent asking what the engine is doing wants the current answer. A backlog of stale
    // frames delivered late is worse than a gap, and the frame says how many it stood in for.
    let (server, _) = serve();
    let mut socket = subscribe(server.port()).expect("connects");

    let channels = names(&["RPM"]);
    let deadline = Instant::now() + Duration::from_secs(2);
    while server.subscribers() == 0 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
    }

    // Far faster than anything can read them.
    for i in 0..500 {
        server.publish(f64::from(i) * 0.04, &channels, &[f64::from(i)]);
    }

    let schema = next(&mut socket);
    assert_eq!(schema["type"], "schema");

    let frame = next(&mut socket);

    // Whatever arrives is recent rather than the first of five hundred, and the count of what it
    // replaced is carried with it.
    assert!(
        frame["skipped"].as_i64().unwrap() > 0,
        "the frame should say how many it stood in for: {frame}"
    );
}

#[test]
fn a_stream_on_the_wrong_path_is_refused() {
    let (server, _) = serve();

    assert!(connect(server.port(), "/nope", Some(TOKEN)).is_err());
}

#[test]
fn a_stream_without_the_token_is_refused() {
    let (server, _) = serve();

    assert!(connect(server.port(), "/live/stream", None).is_err());
}

// ----- the project -----------------------------------------------------------------------------

#[test]
fn the_project_comes_back_as_prose_rather_than_as_fields() {
    // It is read the way a scratchpad is read. Handing a model a shape to reassemble, when the
    // useful thing is a paragraph saying what was tried and what happened, makes it do work that
    // has already been done.
    let (server, bench) = serve();
    bench.with(|s| s.brief = "# The E28\n\n## Still open\n\n### lean-under-load \u{2014} Lean above 150 kPa\n".into());

    let answer = get(server.port(), "/project").json();

    assert_eq!(answer["open"], true);
    assert!(answer["brief"].as_str().unwrap().contains("lean-under-load"));
}

#[test]
fn with_no_project_open_it_says_so_rather_than_inventing_one() {
    let (server, _) = serve();

    assert_eq!(get(server.port(), "/project").json()["open"], false);
}

#[test]
fn recording_a_sitting_needs_a_project_and_says_when_there_is_none() {
    let (server, bench) = serve();

    let refused = post(
        server.port(),
        "/project/record",
        &body(json!({"note": "after the VE change"})),
    );

    assert_eq!(refused.status, 409);
    assert!(bench.with(|s| s.sittings.is_empty()));
}

#[test]
fn and_goes_through_with_one() {
    let (server, bench) = serve();
    bench.with(|s| s.brief = "# The E28".into());

    let answer = post(
        server.port(),
        "/project/record",
        &body(json!({"note": "after the VE change"})),
    );

    assert_eq!(answer.status, 200);
    assert_eq!(
        bench.with(|s| s.sittings.clone()),
        vec!["after the VE change".to_string()]
    );
}

#[test]
fn noting_a_fix_needs_no_arming_because_it_touches_no_engine() {
    // The gate that matters is on writing to the ECU. Recording what is being worked on cannot
    // hurt anything, and an agent that has to ask permission to take notes will not take them.
    let (server, bench) = serve();
    bench.with(|s| s.brief = "# The E28".into());

    assert!(!bench.is_armed());

    let answer = post(
        server.port(),
        "/project/fix",
        &body(json!({"title": "Lean above 150 kPa", "detail": "46% short", "state": "open"})),
    );

    assert_eq!(answer.status, 200);
    assert!(bench.with(|s| s.noted[0].contains("Lean above 150 kPa")));
}

// ----- the activity indicator ------------------------------------------------------------------

#[test]
fn every_route_is_recorded_against_the_activity_log() {
    let (server, _) = serve();

    get(server.port(), "/state");

    let events = server.activity().recent(10);

    // A start and an end for the one request made.
    assert_eq!(events.len(), 2);
    assert!(events.iter().all(|e| e.route() == "/state"));
    assert!(events.iter().all(|e| e.kind() == AgentActivityKind::Read));
    assert!(!events[0].in_flight()); // newest first: the end
    assert!(events[1].in_flight()); // then the start
}

#[test]
fn a_write_route_is_recorded_as_such_even_when_it_is_refused() {
    // The activity log is about what an agent asked this application to do, not whether the
    // request was allowed through: a refused write attempt is still an agent trying to write, and
    // a person watching the indicator should see that just as plainly as a write that went
    // through.
    let (server, _) = serve();

    post(
        server.port(),
        "/tune/set",
        &body(json!({"name": "revLimit", "value": 7000})),
    );

    let writes: Vec<_> = server
        .activity()
        .recent(10)
        .into_iter()
        .filter(|e| e.route() == "/tune/set")
        .collect();

    assert!(!writes.is_empty());
    assert!(writes.iter().all(|e| e.kind() == AgentActivityKind::Write));
}

#[test]
fn a_request_that_fails_authentication_is_not_an_agent_doing_anything() {
    // The indicator is for what an agent asked the application to do. A refused knock at the
    // door is neither a read nor a write, and does not light it.
    let (server, _) = serve();

    raw(server.port(), "GET", "/state", &[], "");

    assert!(server.activity().recent(10).is_empty());
}

#[test]
fn and_the_answer_carries_the_project_back_so_an_agent_sees_the_result() {
    let (server, bench) = serve();
    bench.with(|s| s.brief = "# The E28".into());

    let answer = post(
        server.port(),
        "/project/fix",
        &body(json!({"title": "Lean", "state": "applied"})),
    )
    .json();

    assert!(answer["brief"].as_str().unwrap().contains("The E28"));
}

// ----- composition -----------------------------------------------------------------------------

#[test]
fn tune_full_carries_every_setting_with_its_metadata_and_every_table() {
    // The point of this endpoint is that a session never has to make the /tune plus /tables plus
    // one /table-per-name round trips itself.
    let (server, _) = serve();

    let full = get(server.port(), "/tune/full").json();

    let settings = &full["settings"];
    let cranking = &settings[0];
    assert_eq!(cranking["name"], "crankingRPM");
    assert_eq!(cranking["value"], 300);
    assert_eq!(cranking["units"], "rpm");
    assert_eq!(cranking["low"], 0);
    assert_eq!(cranking["high"], 1000);

    let ego_type = &settings[1];
    assert_eq!(ego_type["options"][2], "Wide Band");

    let tables = &full["tables"];
    assert_eq!(tables[0]["name"], "VE Table");
    assert_eq!(tables[0]["xBins"][0], 800);
    assert_eq!(tables[0]["values"][0][0], 40);
}

#[test]
fn log_full_returns_every_channel_sharing_one_time_column() {
    let (server, _) = serve();

    let full = get(server.port(), "/log/full").json();

    assert_eq!(full["times"].as_array().unwrap().len(), 5);
    assert_eq!(full["channels"].as_array().unwrap().len(), 2);
    assert_eq!(full["channels"][0]["name"], "RPM");
    assert_eq!(full["channels"][0]["values"].as_array().unwrap().len(), 5);
}

#[test]
fn log_full_respects_the_seconds_window() {
    let (server, _) = serve();

    // Only the last 0.2 s of a 0.4 s log: the last three samples.
    let full = get(server.port(), "/log/full?seconds=0.2").json();

    assert_eq!(full["times"].as_array().unwrap().len(), 3);
    assert_eq!(full["channels"][0]["values"].as_array().unwrap().len(), 3);
    assert_eq!(full["channels"][0]["values"][0], 3000);
}

#[test]
fn log_full_thins_by_sample_stride_when_decimate_is_given() {
    let (server, _) = serve();

    // Five samples, every other one kept: indices 0, 2, 4.
    let full = get(server.port(), "/log/full?decimate=2").json();

    let rpm = &full["channels"][0]["values"];
    assert_eq!(rpm.as_array().unwrap().len(), 3);
    assert_eq!(rpm[0], 800);
    assert_eq!(rpm[1], 3000);
    assert_eq!(rpm[2], 5000);
}

#[test]
fn context_bundles_the_project_state_tune_summary_insights_and_wire_health_in_one_call() {
    // The "read this first" call: a summary of the tune, not the full dump /tune/full gives, so a
    // fresh session is not made to pay for both.
    let (server, bench) = serve();
    bench.with(|s| s.brief = "# The E28\n\n## Still open\n".into());

    let context = get(server.port(), "/context").json();

    assert!(context["projectBrief"].as_str().unwrap().contains("The E28"));
    assert_eq!(context["state"]["mode"], "live");
    assert_eq!(context["tune"]["settingCount"], 2);
    assert_eq!(context["tune"]["tables"][0], "VE Table");
    assert_eq!(context["insights"].as_array().unwrap().len(), 1);
    assert_eq!(context["wireHealth"]["sampled"], 10);
}

// ----- propose / apply -------------------------------------------------------------------------

fn rev_limit() -> TuneConstant {
    let mut constant = TuneConstant::new("revLimit", 0, 2, RealtimeType::U16);
    constant.units = "rpm".to_string();
    constant
}

#[test]
fn proposing_needs_no_arming_because_nothing_reaches_the_ecu() {
    let (server, bench) = serve();
    bench.with(|s| {
        s.next_proposal = Some(TuneProposalResult {
            changes: vec![TuneDifference::new(
                "revLimit",
                rev_limit(),
                1,
                Some(7000.0),
                Some(6500.0),
            )],
            rejected: vec![],
            summary: "1 setting would change.".to_string(),
        });
    });

    assert!(!bench.is_armed());

    let answer = post(
        server.port(),
        "/tune/propose",
        &body(json!({"settings": [{"name": "revLimit", "value": 7000}]})),
    );

    assert_eq!(answer.status, 200);

    let got = answer.json();
    assert_eq!(got["changes"].as_array().unwrap().len(), 1);
    assert_eq!(got["changes"][0]["name"], "revLimit");
    assert_eq!(got["changes"][0]["after"], 7000);
    assert_eq!(got["changes"][0]["before"], 6500);

    let (settings, cells) = bench.with(|s| s.proposed[0].clone());
    assert_eq!((settings[0].name.as_str(), settings[0].value), ("revLimit", 7000.0));
    assert!(cells.is_empty());
}

#[test]
fn proposing_with_an_empty_body_asks_what_is_there_now() {
    // An empty batch is a real question ("what would nothing change"), not a malformed request,
    // so it must not be refused as one.
    let (server, _) = serve();

    assert_eq!(post(server.port(), "/tune/propose", &body(json!({}))).status, 200);
}

#[test]
fn applying_is_refused_the_same_way_a_single_write_is() {
    let (server, bench) = serve();

    let refused = post(
        server.port(),
        "/tune/apply",
        &body(json!({"settings": [{"name": "revLimit", "value": 7000}]})),
    );

    assert_eq!(refused.status, 409);
    assert!(refused.body.contains("not armed"));
    assert_eq!(bench.with(|s| s.applied.len()), 1);
}

#[test]
fn applying_goes_through_once_armed_and_says_what_changed() {
    let (server, bench) = serve();
    bench.with(|s| {
        s.next_apply = Some(TuneApplyResult {
            refusal: None,
            applied: vec![TuneDifference::new(
                "revLimit",
                rev_limit(),
                1,
                Some(7000.0),
                Some(6500.0),
            )],
            rejected: vec![],
            summary: "Applied 1 change.".to_string(),
        });
    });

    let answer = post(
        server.port(),
        "/tune/apply",
        &body(json!({
            "settings": [{"name": "revLimit", "value": 7000}],
            "note": "raising the limiter for the dyno pull",
        })),
    );

    assert_eq!(answer.status, 200);
    assert_eq!(answer.json()["applied"].as_array().unwrap().len(), 1);
    assert!(answer.body.contains("\"burned\":false"));

    assert_eq!(
        bench.with(|s| s.applied[0].2.clone()),
        "raising the limiter for the dyno pull"
    );
}

// ----- staging ---------------------------------------------------------------------------------

#[test]
fn staging_a_tune_needs_no_arming_either_and_says_where_it_landed() {
    let (server, bench) = serve();
    assert!(!bench.is_armed());

    let answer = post(server.port(), "/stage/tune", &body(json!({})));

    assert_eq!(answer.status, 200);
    let got = answer.json();
    assert_eq!(got["name"], "tune-test.msq");
    assert!(got["path"].as_str().unwrap().contains("staged"));
}

#[test]
fn staging_a_named_table_works() {
    let (server, bench) = serve();

    let answer = post(server.port(), "/stage/table?name=VE%20Table", &body(json!({})));

    assert_eq!(answer.status, 200);
    assert_eq!(
        bench.with(|s| s.staged_tables.clone()),
        vec![("VE Table".to_string(), String::new())]
    );
}

#[test]
fn staging_an_unknown_table_is_refused() {
    let (server, _) = serve();

    let answer = post(server.port(), "/stage/table?name=Nope", &body(json!({})));

    assert_eq!(answer.status, 404);
    assert!(answer.body.contains("no such table"));
}

#[test]
fn staging_a_table_without_naming_one_says_what_is_missing() {
    let (server, _) = serve();

    let answer = post(server.port(), "/stage/table", &body(json!({})));

    assert_eq!(answer.status, 400);
}

#[test]
fn listing_staged_files_reports_what_the_bridge_holds() {
    let (server, bench) = serve();
    bench.with(|s| {
        s.staged.push(AgentStagedFile {
            name: "tune-1.msq".to_string(),
            path: "C:\\staged\\tune-1.msq".to_string(),
            bytes: 100,
            written_at: std::time::SystemTime::UNIX_EPOCH,
        });
    });

    let answer = get(server.port(), "/stage").json();

    assert_eq!(answer["files"].as_array().unwrap().len(), 1);
    assert_eq!(answer["files"][0]["name"], "tune-1.msq");
}

// ----- firmware definitions ------------------------------------------------------------------------

#[test]
fn nothing_is_needed_when_the_last_connection_found_its_definition() {
    let (server, _) = serve();

    assert_eq!(get(server.port(), "/definitions/needed").json()["needed"], false);
}

#[test]
fn a_missing_definition_is_described_with_somewhere_to_get_it() {
    let (server, bench) = serve();
    bench.with(|s| s.needed = Some(needed()));

    let got = get(server.port(), "/definitions/needed").json();

    assert_eq!(got["needed"], true);

    let need = &got["need"];
    assert_eq!(need["family"], "Speeduino");
    assert_eq!(need["filename"], "speeduino202501.ini");
    assert!(need["sources"][0].as_str().unwrap().contains("202501.7"));
    assert_eq!(need["nearby"][0]["name"], "speeduino202402.ini");
}

#[test]
fn a_definition_is_handed_over_by_path_rather_than_by_its_contents() {
    // These run to half a megabyte. Passing one as a tool argument would push it through the
    // model's context to move a file.
    let (server, bench) = serve();

    let answer = post(
        server.port(),
        "/definitions/import",
        &body(json!({"path": "C:\\downloads\\speeduino.ini", "source": "https://example.invalid/x.ini"})),
    );

    assert_eq!(answer.status, 200);

    let (path, _, source, _) = bench.with(|s| s.imported[0].clone());
    assert_eq!(path, "C:\\downloads\\speeduino.ini");
    assert_eq!(source, "https://example.invalid/x.ini");

    let got = answer.json();
    assert_eq!(got["kept"], true);
    assert_eq!(got["signature"], "speeduino 202501");
}

#[test]
fn a_refused_definition_says_why_and_does_not_read_as_success() {
    let (server, bench) = serve();
    bench.with(|s| {
        s.next_import = Some(AgentDefinitionImported {
            problem: "it declares \"speeduino 202402\", and the ECU reports \"speeduino 202501\"".to_string(),
            ..AgentDefinitionImported::default()
        });
    });

    let answer = post(
        server.port(),
        "/definitions/import",
        &body(json!({"path": "C:\\downloads\\wrong.ini"})),
    );

    assert_eq!(answer.status, 409);
    assert!(answer.body.contains("202402"));
}

// ----- what this port adds: the security boundary, tried from outside -------------------------------

#[test]
fn the_token_can_arrive_as_a_header_or_in_the_query_and_either_alone_is_enough() {
    let (server, _) = serve();
    let port = server.port();

    assert_eq!(raw(port, "GET", &format!("/state?token={TOKEN}"), &[], "").status, 200);
    assert_eq!(
        raw(
            port,
            "GET",
            "/state",
            &[("Authorization", &format!("bearer {TOKEN}"))],
            ""
        )
        .status,
        200
    );
    assert_eq!(
        raw(
            port,
            "GET",
            "/state",
            &[("Authorization", &format!("Bearer   {TOKEN}  "))],
            ""
        )
        .status,
        200
    );
    assert_eq!(raw(port, "GET", "/state", &[("Authorization", TOKEN)], "").status, 401);
    assert_eq!(
        raw(
            port,
            "GET",
            "/state",
            &[("Authorization", &format!("Basic {TOKEN}"))],
            ""
        )
        .status,
        401
    );
    assert_eq!(raw(port, "GET", "/state?token=nope", &[], "").status, 401);
    assert_eq!(raw(port, "GET", "/state?token=", &[], "").status, 401);
    assert_eq!(raw(port, "GET", "/state?token", &[], "").status, 401);
}

#[test]
fn a_token_that_is_a_prefix_or_an_extension_of_the_real_one_is_refused() {
    let (server, _) = serve();
    let port = server.port();

    for wrong in [
        &TOKEN[..TOKEN.len() - 1],
        &format!("{TOKEN}0"),
        &TOKEN.to_uppercase(),
        "",
    ] {
        let header = format!("Bearer {wrong}");
        assert_eq!(
            raw(port, "GET", "/state", &[("Authorization", &header)], "").status,
            401,
            "{wrong}"
        );
    }
}

#[test]
fn authentication_comes_before_routing_so_an_unknown_path_leaks_nothing() {
    let (server, _) = serve();

    let unauthorised = raw(server.port(), "GET", "/nope", &[], "");
    let known = raw(server.port(), "GET", "/state", &[], "");

    assert_eq!(unauthorised.status, 401);
    assert_eq!(unauthorised.body, known.body, "a 404 would say which paths exist");
}

#[test]
fn a_write_without_the_token_never_reaches_the_bridge() {
    let (server, bench) = serve();
    bench.arm();

    let refused = raw(
        server.port(),
        "POST",
        "/tune/set",
        &[],
        &body(json!({"name": "revLimit", "value": 7000, "rationale": "x", "confirmDangerous": true})),
    );

    assert_eq!(refused.status, 401);
    assert!(bench.calls().is_empty(), "{:?}", bench.calls());
}

#[test]
fn a_request_naming_another_host_is_refused_even_with_the_token() {
    // DNS rebinding: a page on evil.example whose name now resolves to 127.0.0.1 sends this Host.
    // The C# answers it; this refuses it, before the token is even looked at.
    let (server, bench) = serve();
    bench.arm();
    let port = server.port();
    let auth = format!("Bearer {TOKEN}");

    for host in [
        "evil.example",
        "evil.example:{port}",
        "127.0.0.1.evil.example",
        "10.0.0.5:{port}",
        "[fe80::1]",
    ] {
        let answer = raw(
            port,
            "POST",
            "/tune/set",
            &[("Host", host), ("Authorization", &auth)],
            "{}",
        );
        assert_eq!(answer.status, 403, "{host}");
    }

    for host in ["127.0.0.1:{port}", "localhost:{port}", "LOCALHOST", "[::1]:{port}"] {
        let answer = raw(port, "GET", "/state", &[("Host", host), ("Authorization", &auth)], "");
        assert_eq!(answer.status, 200, "{host}");
    }

    assert!(bench.calls().is_empty());
}

#[test]
fn a_request_from_another_origin_is_refused_even_with_the_token() {
    let (server, _) = serve();
    let auth = format!("Bearer {TOKEN}");

    for origin in [
        "http://evil.example",
        "null",
        "https://evil.example:8443",
        "http://127.0.0.1.evil.example",
    ] {
        let answer = raw(
            server.port(),
            "GET",
            "/state",
            &[("Origin", origin), ("Authorization", &auth)],
            "",
        );
        assert_eq!(answer.status, 403, "{origin}");
    }

    for origin in ["http://127.0.0.1:3000", "http://localhost", "http://[::1]:8080"] {
        let answer = raw(
            server.port(),
            "GET",
            "/state",
            &[("Origin", origin), ("Authorization", &auth)],
            "",
        );
        assert_eq!(answer.status, 200, "{origin}");
    }
}

#[test]
fn a_stream_from_another_origin_is_refused_too() {
    use tungstenite::client::IntoClientRequest;

    let (server, _) = serve();
    let stream = std::net::TcpStream::connect(("127.0.0.1", server.port())).unwrap();

    let mut request = format!("ws://127.0.0.1:{}/live/stream", server.port())
        .into_client_request()
        .unwrap();
    request
        .headers_mut()
        .insert("Origin", "http://evil.example".parse().unwrap());
    request
        .headers_mut()
        .insert("Authorization", format!("Bearer {TOKEN}").parse().unwrap());

    assert!(tungstenite::client(request, stream).is_err());
    assert_eq!(server.subscribers(), 0);
}

#[test]
fn there_is_no_route_that_arms_writing() {
    // Arming is a person's act, in the application. Try every plausible way an agent might ask,
    // and then check the bridge still says it is not armed.
    let (server, bench) = serve();
    let port = server.port();

    for path in [
        "/arm",
        "/tune/arm",
        "/state/arm",
        "/limits/arm",
        "/writes/arm",
        "/armed",
        "/config",
        "/settings",
    ] {
        assert_eq!(
            post(port, path, &body(json!({"armed": true, "writesArmed": true}))).status,
            404,
            "{path}"
        );
    }

    post(port, "/state", &body(json!({"writesArmed": true})));
    post(
        port,
        "/tune/set",
        &body(json!({"name": "x", "value": 1, "armed": true, "writesArmed": true})),
    );
    get(port, "/state?writesArmed=true&armed=true");

    assert!(!bench.is_armed());
    assert_eq!(get(port, "/state").json()["writesArmed"], false);
}

#[test]
fn a_get_cannot_write_and_a_post_endpoint_refuses_other_methods() {
    let (server, bench) = serve();
    bench.arm();
    let port = server.port();
    let auth = format!("Bearer {TOKEN}");

    for path in [
        "/tune/set",
        "/table/set",
        "/tune/apply",
        "/project/fix",
        "/definitions/import",
    ] {
        for method in ["GET", "PUT", "DELETE", "PATCH"] {
            let answer = raw(port, method, path, &[("Authorization", &auth)], "");
            assert_eq!(answer.status, 405, "{method} {path}");
        }
    }

    assert!(bench.calls().is_empty(), "{:?}", bench.calls());
}

#[test]
fn a_write_route_reached_by_an_odd_path_is_still_the_same_route() {
    // The routing sees what `Uri.AbsolutePath` would: trailing slashes, escapes of plain letters
    // and backslashes all land on the real route, and so meet its gates.
    let (server, bench) = serve();

    for path in ["/tune/set/", "/tun%65/set", "/tune\\set", "/a/../tune/set"] {
        let answer = post(server.port(), path, &body(json!({"name": "revLimit", "value": 1})));
        assert_eq!(answer.status, 409, "{path}");
    }

    assert!(bench.with(|s| s.written.is_empty()));
    assert_eq!(bench.calls().len(), 4);
}

#[test]
fn a_null_entry_in_a_batch_is_the_c_sharps_generic_500_and_reaches_nothing() {
    let (server, bench) = serve();
    bench.arm();

    let answer = post(
        server.port(),
        "/tune/apply",
        &body(json!({"settings": [null], "note": "x"})),
    );

    assert_eq!(answer.status, 500);
    assert!(bench.with(|s| s.applied.is_empty()));
}

#[test]
fn a_panic_in_the_bridge_is_a_500_not_a_dead_server() {
    struct Panicky(Bench);

    impl AgentBridge for Panicky {
        fn state(&self) -> AgentState {
            panic!("the bridge fell over");
        }
        fn limits(&self) -> AgentLimits {
            self.0.limits()
        }
        fn channels(&self, raw: bool) -> Vec<AgentChannel> {
            self.0.channels(raw)
        }
        fn wire_health(&self) -> AgentWireHealth {
            self.0.wire_health()
        }
        fn wire_events(&self, count: i32) -> Vec<AgentWireEvent> {
            self.0.wire_events(count)
        }
        fn values(&self, channel: &str, seconds: f64) -> Vec<f64> {
            self.0.values(channel, seconds)
        }
        fn times(&self, seconds: f64) -> Vec<f64> {
            self.0.times(seconds)
        }
        fn insights(&self) -> Vec<AgentFinding> {
            self.0.insights()
        }
        fn tune_values(&self) -> Vec<(String, f64)> {
            self.0.tune_values()
        }
        fn table(&self, name: &str) -> Option<olv_core::model::TuneTable> {
            self.0.table(name)
        }
        fn table_names(&self) -> Vec<String> {
            self.0.table_names()
        }
        fn project_brief(&self) -> String {
            self.0.project_brief()
        }
        fn projects(&self) -> Vec<String> {
            self.0.projects()
        }
        fn keep_tune(&self, note: &str) -> Option<AgentRefusal> {
            self.0.keep_tune(note)
        }
        fn compare_versions(&self, from: &str, to: &str) -> String {
            self.0.compare_versions(from, to)
        }
        fn record_sitting(&self, note: &str) -> Option<AgentRefusal> {
            self.0.record_sitting(note)
        }
        fn note_fix(&self, a: &str, b: &str, c: &str, d: &str, e: &str) -> Option<AgentRefusal> {
            self.0.note_fix(a, b, c, d, e)
        }
        fn set_setting(&self, n: &str, v: f64, r: &str, c: bool) -> Option<AgentRefusal> {
            self.0.set_setting(n, v, r, c)
        }
        fn set_table_cell(&self, t: &str, c: i32, r: i32, v: f64, ra: &str, d: bool) -> AgentCellWrite {
            self.0.set_table_cell(t, c, r, v, ra, d)
        }
        fn tune_full(&self) -> AgentTuneFull {
            self.0.tune_full()
        }
        fn log_full(&self, s: f64, d: i32) -> AgentLogFull {
            self.0.log_full(s, d)
        }
        fn context(&self) -> AgentContext {
            self.0.context()
        }
        fn propose_tune(&self, s: &[ProposedSetting], c: &[ProposedCell]) -> TuneProposalResult {
            self.0.propose_tune(s, c)
        }
        fn apply_tune(&self, s: &[ProposedSetting], c: &[ProposedCell], n: &str, d: bool) -> TuneApplyResult {
            self.0.apply_tune(s, c, n, d)
        }
        fn stage_tune(&self, s: &[ProposedSetting], c: &[ProposedCell], f: &str) -> AgentStageResult {
            self.0.stage_tune(s, c, f)
        }
        fn stage_table(&self, n: &str, f: &str) -> AgentStageResult {
            self.0.stage_table(n, f)
        }
        fn list_staged(&self) -> Vec<AgentStagedFile> {
            self.0.list_staged()
        }
        fn definition_needed(&self) -> Option<AgentDefinitionNeed> {
            self.0.definition_needed()
        }
        fn import_definition(&self, p: &str, c: &str, s: &str, n: &str) -> AgentDefinitionImported {
            self.0.import_definition(p, c, s, n)
        }
    }

    let mut server = AgentServer::new(
        Arc::new(Panicky(Bench::new())),
        AgentServerSettings {
            port: 0,
            ..AgentServerSettings::new(TOKEN)
        },
    )
    .unwrap();
    server.start().unwrap();

    let answer = get(server.port(), "/state");
    assert_eq!(answer.status, 500);
    assert!(answer.body.contains("the bridge fell over"));

    // And the next request is answered as usual.
    assert_eq!(get(server.port(), "/limits").status, 200);
}

#[test]
fn too_many_subscribers_get_a_503_and_the_limit_frees_when_one_leaves() {
    let bench = Arc::new(Bench::new());
    let mut server = AgentServer::new(
        bench,
        AgentServerSettings {
            port: 0,
            maximum_clients: 2,
            ..AgentServerSettings::new(TOKEN)
        },
    )
    .unwrap();
    server.start().unwrap();

    let first = subscribe(server.port()).expect("first");
    let _second = subscribe(server.port()).expect("second");

    let deadline = Instant::now() + Duration::from_secs(2);
    while server.subscribers() < 2 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
    }

    match subscribe(server.port()) {
        Err(tungstenite::Error::Http(response)) => assert_eq!(response.status().as_u16(), 503),
        other => panic!("expected a 503, got {:?}", other.map(|_| ())),
    }

    drop(first);

    let deadline = Instant::now() + Duration::from_secs(5);
    while server.subscribers() > 1 && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(20));
    }

    assert!(subscribe(server.port()).is_ok(), "a place should have freed");
}

#[test]
fn a_request_that_is_not_json_for_the_stream_is_answered_not_dropped() {
    let (server, _) = serve();
    let mut socket = subscribe(server.port()).expect("connects");

    send(&mut socket, "not json");
    let reply = next(&mut socket);

    assert_eq!(reply["type"], "error");
    assert_eq!(reply["detail"], "expected {\"channels\":[\u{2026}],\"raw\":false}");

    // And the stream carries on: a good request after a bad one is still honoured.
    send(&mut socket, &json!({"channels": ["AFR"]}).to_string());
    let channels = names(&["RPM", "AFR"]);
    std::thread::scope(|scope| {
        scope.spawn(|| publish_until_seen(&server, &channels, &[1.0, 2.0]));

        let mut seen = None;
        for _ in 0..40 {
            let got = next(&mut socket);
            if got["type"] == "schema" && got["channels"].as_array().unwrap().len() == 1 {
                seen = Some(got);
                break;
            }
        }
        assert!(seen.is_some());
    });
}

#[test]
fn stopping_the_server_closes_the_port_and_its_subscribers() {
    let (mut server, _) = serve();
    let port = server.port();
    let mut socket = subscribe(port).expect("connects");

    server.dispose();
    assert!(!server.is_running());

    // The subscriber's socket ends, and nothing answers on the port any more.
    let ended = (0..100).any(|_| {
        std::thread::sleep(Duration::from_millis(20));
        socket.read().is_err()
    });
    assert!(ended, "the subscriber should have been closed");
    assert!(std::net::TcpStream::connect(("127.0.0.1", port)).is_err());
}

#[test]
fn an_oversized_or_malformed_request_is_refused_not_buffered_forever() {
    let (server, _) = serve();
    let port = server.port();

    // A declared body past the cap is refused before it is read.
    let big = raw(
        port,
        "POST",
        "/tune/set",
        &[
            ("Authorization", &format!("Bearer {TOKEN}")),
            ("Content-Length", "99999999999"),
        ],
        "",
    );
    assert!(matches!(big.status, 400 | 413), "{}", big.status);

    // Not HTTP at all.
    use std::io::{Read, Write};
    let mut stream = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
    stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    stream.write_all(b"\x16\x03\x01 this is not http\r\n\r\n").unwrap();
    let mut reply = String::new();
    let _ = stream.read_to_string(&mut reply);
    assert!(reply.is_empty() || reply.starts_with("HTTP/1.1 400"), "{reply}");
}

#[test]
fn the_settings_never_print_the_token() {
    let settings = AgentServerSettings::new("super-secret-token-value");

    assert!(!format!("{settings:?}").contains("super-secret-token-value"));
}
