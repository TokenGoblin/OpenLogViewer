//! `Elm327Source` against a software car: the C# `Obd2Tests`, `Obd2AdapterTests`,
//! `Obd2BatchTests` (the half that needs a source) and the source half of `Obd2FaultTests`.
//!
//! The tests of the parsers and gauges alone (`ParseBatch`, `SupportedBy`, the fault decoders)
//! were ported in `olv-protocol` with the code they test and are not repeated; the ones here
//! that touch them are the ones that go through a source.

mod common;

use common::{BatchReply, FakeElm, ScriptedElm, piece};
use olv_core::ini::gauge_catalog::GaugeBand;
use olv_core::model::LiveSource;
use olv_core::units::UnitSystem;
use olv_core::{Error, Result};
use olv_live::connection::elm327_source::{BAUD_RATES, Elm327Source};
use olv_protocol::ms::obd2_adapters;
use olv_protocol::obd::elm327::{self, Elm327};
use olv_protocol::obd::obd2_gauges;
use olv_protocol::obd::obd2_pids;

/// A car answering the parameters most of them do.
fn car() -> FakeElm {
    let car = FakeElm::new();

    car.with(|s| {
        // Supported [01-20]: 0x01, 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11, with the last
        // bit set to say there is a further range.
        s.answers
            .insert(0x00, vec![0b1001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0001]);

        // Supported [21-40]: none of them, but another range follows.
        s.answers
            .insert(0x20, vec![0b0000_0000, 0b0000_0000, 0b0000_0000, 0b0000_0001]);

        // Supported [41-60]: 0x42 alone, and nothing after it.
        s.answers
            .insert(0x40, vec![0b0100_0000, 0b0000_0000, 0b0000_0000, 0b0000_0000]);

        s.answers.insert(0x01, vec![0x81, 0x07, 0x65, 0x04]); // MIL on, 1 code
        s.answers.insert(0x04, vec![0x7F]); // 49.8 % load
        s.answers.insert(0x05, vec![0x5A]); // 50 C
        s.answers.insert(0x0B, vec![0x64]); // 100 kPa
        s.answers.insert(0x0C, vec![0x1A, 0xF8]); // 1726 rpm
        s.answers.insert(0x0D, vec![0x40]); // 64 km/h
        s.answers.insert(0x0F, vec![0x46]); // 30 C
        s.answers.insert(0x11, vec![0x33]); // 20 %
        s.answers.insert(0x42, vec![0x37, 0x1A]); // 14.106 V
    });

    car
}

fn connect(car: &FakeElm) -> Elm327Source {
    Elm327Source::connect(Box::new(car.clone()), "", None).unwrap()
}

fn connect_error(car: &FakeElm) -> Error {
    match Elm327Source::connect(Box::new(car.clone()), "", None) {
        Ok(_) => panic!("the connection should have been refused"),
        Err(e) => e,
    }
}

fn at(source: &Elm327Source, row: &[f64], name: &str) -> f64 {
    let at = source.names().iter().position(|n| n == name);
    assert!(at.is_some(), "no channel called {name}");

    row[at.unwrap()]
}

fn close(actual: f64, expected: f64, tolerance: f64) {
    assert!(
        (actual - expected).abs() <= tolerance,
        "{actual} is not {expected} within {tolerance}"
    );
}

// ----- the adapter ----------------------------------------------------------

#[test]
fn the_adapter_is_put_into_the_mode_this_can_parse() {
    // Echo, linefeeds, spaces and headers all off, and auto-protocol on. Every one of them
    // changes the shape of a reply.
    let car = car();
    let _source = connect(&car);

    assert_eq!(car.received()[..6], ["ATZ", "ATE0", "ATL0", "ATS0", "ATH0", "ATSP0"]);
}

#[test]
fn the_protocol_search_is_spent_on_a_request_nobody_reads() {
    // The first request after ATSP0 is the one the adapter searches on, and it narrates the
    // search: "SEARCHING..." arrives ahead of the data, seconds can pass, and S, E, A, R, C
    // and H all pass a hex parser as digits. Spending it on an answer that is thrown away
    // leaves discovery talking to an adapter that has settled, which matters more now that
    // a pause can finish a read, since a search paused mid-word would end one on nothing but
    // the word.
    let car = car();
    let _source = connect(&car);

    // Twice, back to back: once discarded, then again for the capability mask that is
    // actually read.
    let received = car.received();
    let first = received.iter().position(|c| c == "0100").unwrap();

    assert_eq!(received.iter().filter(|c| *c == "0100").count(), 2);
    assert_eq!(received[first + 1], "0100");
}

#[test]
fn the_adapter_says_what_it_is() {
    assert_eq!(connect(&car()).adapter(), "ELM327 v1.5");
}

#[test]
fn the_echoed_command_is_not_mistaken_for_the_version() {
    // ATZ is answered while echo is still on, so what comes back is the command and then
    // the version.
    assert!(connect(&car()).adapter().starts_with("ELM327"));
}

// ----- what the car supports ------------------------------------------------

#[test]
fn only_the_parameters_the_car_reports_are_asked_for() {
    // The alternative is trying all ninety-six and waiting out a NO DATA for each: a round
    // trip apiece on the slowest link in this application.
    let source = connect(&car());

    let pids: Vec<u8> = source.parameters().iter().map(|p| p.pid).collect();
    assert_eq!(pids, [0x01, 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11, 0x42]);
}

#[test]
fn the_car_is_not_asked_about_a_range_it_said_nothing_followed_in() {
    // The last bit of each mask means "and there is another range after this". Asking
    // anyway costs a round trip and answers NO DATA, which reads like a fault.
    let car = car();
    let _source = connect(&car);

    let received = car.received();
    assert!(received.contains(&"0100".to_string()));
    assert!(received.contains(&"0140".to_string()));

    // 0x60 would be the next range, and the 0140 mask does not claim one.
    assert!(!received.contains(&"0160".to_string()));
}

#[test]
fn a_car_that_supports_nothing_is_said_to_rather_than_connected_to() {
    let car = FakeElm::new();
    car.with(|s| s.answers.insert(0x00, vec![0, 0, 0, 0]));

    let e = connect_error(&car);

    assert!(e.to_string().to_lowercase().contains("ignition"));
    assert!(matches!(e, Error::Protocol(_)));
}

#[test]
fn an_adapter_at_the_wrong_speed_is_not_mistaken_for_a_key_left_out() {
    // The two failures need telling apart. A key turned off is fixed in a second; a wrong
    // baud rate is not, and waiting for an ignition that is already on would be waiting for
    // nothing. What separates them is that an adapter being sent noise answers with noise,
    // and noise is never its own name.
    let noise = FakeElm::new();
    noise.with(|s| s.garble = true);

    let e = connect_error(&noise);

    assert!(!e.to_string().to_lowercase().contains("ignition"));
    assert!(e.to_string().contains("answered as an OBD2 adapter"));
}

/// Verbatim from the test vehicle. Two modules answer 0100, the engine one reporting
/// twenty-four usable parameters and the other almost none, and which arrives first is not
/// fixed, so taking one of them gave 24 channels on one connection and 3 on the next, on the
/// same car a minute apart.
const TWO_MODULES: &str = "SEARCHING...\r4100BE3FA813\r410080000001\r\r";

#[test]
fn every_modules_capabilities_are_combined_rather_than_raced() {
    let masks = elm327::parse_all(TWO_MODULES, 0x00, 4);

    assert_eq!(masks.len(), 2);

    let rich = [0xBE, 0x3F, 0xA8, 0x13];
    let sparse = [0x80, 0x00, 0x00, 0x01];

    assert_eq!(masks[0], rich);
    assert_eq!(masks[1], sparse);

    // The sparse module alone would leave the car reporting one parameter.
    assert_eq!(
        obd2_pids::supported_by(0x00, &sparse)
            .iter()
            .filter(|&&p| p != 0x20)
            .count(),
        1
    );
    assert!(obd2_pids::supported_by(0x00, &rich).len() > 10);
}

#[test]
fn a_car_whose_second_module_answers_first_still_finds_everything() {
    // The failure as it actually happened: three channels instead of twenty-four, because
    // the uninteresting module got a word in first.
    let car = car();
    car.with(|s| s.extra_answers.insert(0x00, vec![vec![0x80, 0x00, 0x00, 0x01]]));

    let source = connect(&car);

    assert!(source.names().contains(&"RPM".to_string()));
    assert!(source.names().contains(&"Coolant".to_string()));
    assert!(
        source.parameters().len() > 5,
        "only found {}",
        source.parameters().len()
    );
}

#[test]
fn the_speeds_tried_start_with_the_one_a_genuine_adapter_ships_at() {
    // 38,400 is the ELM327's factory setting. Clones vary, which is why there is a list at
    // all.
    assert_eq!(BAUD_RATES[0], 38400);
    assert!(BAUD_RATES.contains(&115200));
    assert!(BAUD_RATES.contains(&9600));
}

// ----- decoding -------------------------------------------------------------

#[test]
fn the_standard_formulas_are_applied() {
    let mut source = connect(&car());
    let row = source.read().unwrap();

    assert_eq!(at(&source, &row, "RPM"), 1726.0);
    assert_eq!(at(&source, &row, "Speed"), 64.0);
    assert_eq!(at(&source, &row, "MAP"), 100.0);
    close(at(&source, &row, "Throttle Position"), 20.0, 0.05);
    close(at(&source, &row, "Engine Load"), 49.8, 0.05);
}

#[test]
fn searching_is_not_read_as_a_reading() {
    // Its letters are mostly hex digits (SEARCHING gives E, A and C) so run together with
    // the reply behind it they decode as a different number entirely. Only its own line
    // saves it.
    let car = car();
    car.with(|s| s.searching = true);

    let mut source = connect(&car);
    let row = source.read().unwrap();

    assert_eq!(at(&source, &row, "RPM"), 1726.0);
}

#[test]
fn a_reply_to_a_different_question_is_refused() {
    // A reply that arrives after its timeout is answering the previous request. Taken at
    // face value it puts one channel's number on another channel's gauge, which is worse
    // than no reading.
    let mut data = [0u8; 4];

    assert!(elm327::try_parse("410D40", 0x0C, 2, &mut data).is_none());
    assert!(elm327::try_parse("410C1AF8", 0x0C, 2, &mut data).is_some());
}

#[test]
fn a_truncated_reply_is_refused_rather_than_padded_with_zero() {
    // Half of an RPM reply is not an RPM of half the value.
    let mut data = [0u8; 4];

    assert!(elm327::try_parse("410C1A", 0x0C, 2, &mut data).is_none());
}

#[test]
fn no_data_is_not_a_reading() {
    // "NO DATA" contains D, A and A, which are hex digits.
    let mut data = [0u8; 4];

    assert!(elm327::try_parse("NO DATA", 0x0C, 2, &mut data).is_none());
}

#[test]
fn one_request_answered_by_several_modules_takes_the_first_complete_reply() {
    // A car with more than one responding module answers once per module, one to a line.
    let mut data = [0u8; 2];

    assert!(elm327::try_parse("410C1AF8\r410C1AF9", 0x0C, 2, &mut data).is_some());
    assert_eq!(data[1], 0xF8);
}

#[test]
fn one_pid_can_carry_two_channels() {
    // 0x01 is the check-engine lamp and the number of stored codes at once.
    let mut source = connect(&car());

    // Rotated in, so it takes a few rounds to come round.
    let mut row = Vec::new();
    for _ in 0..source.parameters().len() {
        row = source.read().unwrap();
    }

    assert_eq!(at(&source, &row, "MIL"), 1.0);
    assert_eq!(at(&source, &row, "DTC Count"), 1.0);
}

#[test]
fn spaces_in_a_reply_are_tolerated_whichever_way_the_adapter_sends_them() {
    // ATS0 turns them off, but a clone that ignores it should not cost every reading in the
    // session.
    let (mut a, mut b) = ([0u8; 2], [0u8; 2]);

    assert!(elm327::try_parse("41 0C 1A F8", 0x0C, 2, &mut a).is_some());
    assert!(elm327::try_parse("410C1AF8", 0x0C, 2, &mut b).is_some());
    assert_eq!(a, b);
}

// ----- the shape of a session -----------------------------------------------

#[test]
fn a_channel_reads_as_nothing_until_it_has_answered() {
    // Not zero, which is a reading. A rotated channel has not been asked yet on the first
    // round, and a dial showing zero volts would be a fault report rather than an absence.
    let mut source = connect(&car());
    let first = source.read().unwrap();

    assert!(at(&source, &first, "Battery").is_nan());
    assert!(!at(&source, &first, "RPM").is_nan());
}

#[test]
fn the_fast_channels_are_asked_for_every_round() {
    // Otherwise the rev counter updates at the speed of the fuel level.
    let car = car();
    let mut source = connect(&car);

    source.read().unwrap();
    car.with(|s| s.received.clear());
    source.read().unwrap();

    let received = car.received();
    assert!(received.contains(&"010C".to_string()));
    assert!(received.contains(&"010D".to_string()));
    assert!(received.contains(&"0111".to_string()));
}

#[test]
fn a_slow_channel_holds_its_last_reading_between_turns() {
    let mut source = connect(&car());

    let mut row = Vec::new();
    for _ in 0..source.parameters().len() * 2 {
        row = source.read().unwrap();
    }

    close(at(&source, &row, "Battery"), 14.106, 0.0005);
}

#[test]
fn a_car_that_stops_answering_altogether_is_reported() {
    // One parameter falling silent is ordinary. Every one of them doing so is the key
    // having been turned off, and the session should say so rather than record a screenful
    // of unchanging numbers.
    let car = car();
    let mut source = connect(&car);
    source.read().unwrap();

    car.with(|s| s.answers.clear());

    let e = source.read().unwrap_err();
    assert!(matches!(e, Error::Protocol(_)));
    assert!(e.to_string().starts_with("The adapter answered nothing this round."));
    assert_eq!(source.retries(), 1);
}

// ----- gauges ---------------------------------------------------------------

#[test]
fn every_reported_parameter_gets_a_gauge() {
    let source = connect(&car());
    let gauges = obd2_gauges::for_pids(source.parameters());

    assert_eq!(gauges.len(), source.names().len());
    for g in &gauges {
        assert!(g.has_scale(), "{} has no range", g.title);
    }
}

fn gauge(title: &str) -> olv_core::ini::gauge_catalog::GaugeSpec {
    let gauges = obd2_gauges::for_pids(obd2_pids::all());
    let mut found: Vec<_> = gauges.into_iter().filter(|g| g.title == title).collect();

    assert_eq!(found.len(), 1, "{title}");
    found.remove(0)
}

#[test]
fn only_the_parameters_with_a_convention_worth_having_are_banded() {
    // The standard supplies no limits, so these are a workshop manual's figures rather than
    // the car's. Which makes it the more important that they stop where the convention does:
    // nobody can say what a wrong road speed or a wrong manifold pressure is, and colouring
    // those would be asserting something no one has said.
    for name in [
        "Speed",
        "RPM",
        "MAP",
        "Engine Load",
        "Throttle Position",
        "Timing Advance",
        "Run Time",
        "Barometric Pressure",
        "Lambda",
    ] {
        assert!(!gauge(name).has_bands(), "{name} claims to know what a safe reading is");
    }

    for name in [
        "Coolant",
        "Battery",
        "Engine Oil Temp",
        "Fuel Level",
        "Long Fuel Trim B1",
        "MIL",
    ] {
        assert!(gauge(name).has_bands(), "{name} has no limits at all");
    }
}

#[test]
fn a_lit_malfunction_lamp_is_red() {
    // The one limit the standard does state. Its own word for the lamp being commanded on
    // is "malfunction", so this is the car talking rather than a convention about what is
    // probably bad.
    let mil = gauge("MIL");

    assert_eq!(mil.band_for(0.0), GaugeBand::Normal);
    assert_eq!(mil.band_for(1.0), GaugeBand::Danger);
}

#[test]
fn a_stored_code_shows_as_a_warning_and_none_as_normal() {
    let codes = gauge("DTC Count");

    assert_eq!(codes.band_for(0.0), GaugeBand::Normal);
    assert_eq!(codes.band_for(1.0), GaugeBand::Warning);
    assert_eq!(codes.band_for(6.0), GaugeBand::Warning);
}

#[test]
fn a_reading_falls_where_a_workshop_manual_would_put_it() {
    let cases = [
        ("Coolant", 90.0, GaugeBand::Normal),
        ("Coolant", 108.0, GaugeBand::Warning),
        ("Coolant", 120.0, GaugeBand::Danger),
        ("Battery", 14.1, GaugeBand::Normal),
        ("Battery", 12.0, GaugeBand::Warning),
        ("Battery", 11.0, GaugeBand::Danger),
        ("Battery", 15.6, GaugeBand::Danger),
        ("Fuel Level", 50.0, GaugeBand::Normal),
        ("Fuel Level", 10.0, GaugeBand::Warning),
        ("Fuel Level", 3.0, GaugeBand::Danger),
        ("Long Fuel Trim B1", 2.0, GaugeBand::Normal),
        ("Long Fuel Trim B1", -14.0, GaugeBand::Warning),
        ("Long Fuel Trim B1", 30.0, GaugeBand::Danger),
    ];

    for (title, reading, expected) in cases {
        assert_eq!(gauge(title).band_for(reading), expected, "{title} at {reading}");
    }
}

#[test]
fn the_limits_survive_being_shown_in_other_units() {
    // A coolant gauge in Fahrenheit keeping a Celsius redline would call 105 degrees an
    // emergency at 40 C.
    let imperial = gauge("Coolant").in_system(UnitSystem::Imperial);

    assert_eq!(imperial.band_for(194.0), GaugeBand::Normal); // 90 C
    assert_eq!(imperial.band_for(248.0), GaugeBand::Danger); // 120 C
}

#[test]
fn the_tachometer_is_drawn_to_something_an_engine_could_reach() {
    // The standard encodes RPM to 16,383.75, which is the counter's ceiling and no engine's.
    // A dial drawn to it leaves every real reading in the first quarter.
    let tacho = gauge("RPM");

    assert_eq!(tacho.high, obd2_pids::TACHOMETER_TOP);
    assert!((6000.0..=12000.0).contains(&tacho.high));
}

#[test]
fn the_front_page_is_a_dashboard_rather_than_the_whole_catalogue() {
    let named: Vec<String> = obd2_gauges::for_pids(obd2_pids::all())
        .into_iter()
        .map(|g| g.title)
        .collect();

    for name in obd2_gauges::FRONT_PAGE {
        assert!(named.iter().any(|n| n == name), "{name}");
    }
    assert!(obd2_gauges::FRONT_PAGE.len() < named.len());
}

// ----- Obd2AdapterTests ---------------------------------------------------------

#[test]
fn the_adapters_are_recognised() {
    // The one that caught this out. An OBDLink r2.6 advertises after the company rather
    // than the product, so every name being looked for missed it, and a working dongle got
    // probed as a MegaSquirt and reported as an unknown ECU.
    for name in [
        "ScanTool.net-5487",
        "OBDLink MX+",
        "OBDLink LX",
        "OBDLink CX",
        "OBDII",
        "OBD2 Adapter",
        "ELM327 v1.5",
        "Vgate iCar Pro",
        "vLinker MC+",
        "VEEPEAK OBDCheck",
        "Konnwei KW902",
        "obdlink mx",
    ] {
        assert!(obd2_adapters::looks_like_one(Some(name)), "{name} should be recognised");
    }
}

#[test]
fn everything_else_is_not() {
    // Everything else paired to the same machine. Guessing wrong here is the same mistake in
    // the other direction: a MaxxECU routed into the OBD2 path finds nothing, having never
    // spoken OBD2 in its life.
    for name in [
        Some("MaxxECU_28xf7p"),
        Some("HP 720/725 Multi-Device Rechargeable Wireless Mouse"),
        Some("uaefi"),
        Some("Standard Serial over Bluetooth link"),
        Some("soundcore"),
        Some(""),
        Some("   "),
        None,
    ] {
        assert!(
            !obd2_adapters::looks_like_one(name),
            "{name:?} should not be taken for an adapter"
        );
    }
}

#[test]
fn any_of_several_names_will_do() {
    // A serial port has both a device name and a Windows description, and either may be the
    // one carrying the clue.
    assert!(obd2_adapters::looks_like_any(&[
        Some("Standard Serial over Bluetooth link"),
        Some("ScanTool.net-5487")
    ]));
    assert!(!obd2_adapters::looks_like_any(&[
        Some("Standard Serial over Bluetooth link"),
        Some("MaxxECU_28xf7p")
    ]));
    assert!(!obd2_adapters::looks_like_any(&[None, None]));
}

#[test]
fn an_obd_link_is_named_by_its_product_rather_than_its_claim() {
    // Measured against the real one: an OBDLink r2.6 answers ATI with "ELM327 v1.3a", a
    // version superseded a decade before it was built, and only STDI says what it actually
    // is.
    let fake = FakeElm::new();
    fake.with(|s| {
        s.elm327_name = "ELM327 v1.3a".to_string();
        s.product = "OBDLink r2.6".to_string();
        s.firmware = "STN1100 v2.2.2".to_string();
    });

    let mut elm = Elm327::new(Box::new(fake.clone()));
    elm.reset(None).unwrap();

    assert_eq!(elm.identify().unwrap(), "OBDLink r2.6 (STN1100 v2.2.2)");
}

#[test]
fn a_clone_is_still_named_by_whatever_it_does_answer() {
    // No ST commands, so there is nothing but the ELM327 claim to go on, and that has to
    // keep working, because it is most of what is out there.
    let fake = FakeElm::new();
    fake.with(|s| s.elm327_name = "ELM327 v2.1".to_string());

    let mut elm = Elm327::new(Box::new(fake.clone()));
    elm.reset(None).unwrap();

    assert_eq!(elm.identify().unwrap(), "ELM327 v2.1");
}

#[test]
fn refusing_the_extended_commands_is_not_mistaken_for_an_answer() {
    // An ELM327 answers anything it does not know with "?", and taking that for a product
    // name would put a question mark in the connect menu.
    let fake = FakeElm::new();
    fake.with(|s| s.elm327_name = "ELM327 v1.5".to_string());

    let mut elm = Elm327::new(Box::new(fake.clone()));
    elm.reset(None).unwrap();

    assert!(!elm.identify().unwrap().contains('?'));
    assert!(fake.received().contains(&"STDI".to_string()));
    assert!(fake.received().contains(&"STI".to_string()));
}

#[test]
fn a_partly_extended_adapter_uses_what_it_has() {
    // Firmware but no product name: worth reporting alongside the ELM327 claim rather than
    // throwing away.
    let fake = FakeElm::new();
    fake.with(|s| {
        s.elm327_name = "ELM327 v1.4".to_string();
        s.firmware = "STN2255 v5.6.4".to_string();
    });

    let mut elm = Elm327::new(Box::new(fake.clone()));
    elm.reset(None).unwrap();

    assert_eq!(elm.identify().unwrap(), "ELM327 v1.4 (STN2255 v5.6.4)");
}

#[test]
fn a_session_reports_the_product_and_not_the_claim() {
    // The whole point of the exercise: what the connect menu and the status bar end up
    // showing.
    let fake = FakeElm::new();
    fake.with(|s| {
        s.elm327_name = "ELM327 v1.3a".to_string();
        s.product = "OBDLink r2.6".to_string();
        s.firmware = "STN1100 v2.2.2".to_string();
        s.answers.insert(0x00, vec![0xBE, 0x3F, 0xA8, 0x13]);
        s.answers.insert(0x0C, vec![0x1A, 0xF8]);
    });

    let source = connect(&fake);

    assert_eq!(source.adapter(), "OBDLink r2.6 (STN1100 v2.2.2)");
    assert!(!source.parameters().is_empty());
}

#[test]
fn an_adapter_being_sent_noise_is_still_not_named() {
    // The check that this did not become a way to invent a name for a port that has nothing
    // on it: a garbled reply names nothing, and the failure has to stay recognisable as
    // "wrong port or wrong speed".
    let fake = FakeElm::new();
    fake.with(|s| s.garble = true);

    let mut elm = Elm327::new(Box::new(fake.clone()));

    assert_eq!(elm.reset(None).unwrap(), "");
}

// ----- Obd2BatchTests: deciding whether to batch, and what it does to a round ---------

fn batch_car(batches: BatchReply, protocol: i32) -> FakeElm {
    let car = FakeElm::new();

    car.with(|s| {
        s.batches = batches;
        s.protocol_number = protocol;

        // Supported [01-20]: 0x04, 0x05, 0x0B, 0x0C, 0x0D, 0x0F, 0x11.
        s.answers
            .insert(0x00, vec![0b0001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0000]);

        s.answers.insert(0x04, vec![0x7F]); // 49.8 % load
        s.answers.insert(0x05, vec![0x5A]); // 50 C
        s.answers.insert(0x0B, vec![0x64]); // 100 kPa
        s.answers.insert(0x0C, vec![0x1A, 0xF8]); // 1726 rpm
        s.answers.insert(0x0D, vec![0x40]); // 64 km/h
        s.answers.insert(0x0F, vec![0x46]); // 30 C
        s.answers.insert(0x11, vec![0x33]); // 20 %
    });

    car
}

#[test]
fn a_parameter_of_unknown_length_is_never_asked_for_in_a_batch() {
    // Its group could not be located in the reply, so it would not merely be missing: it
    // would lose every group after it.
    let car = batch_car(BatchReply::All, 6);
    let mut elm = Elm327::new(Box::new(car.clone()));
    elm.reset(None).unwrap();

    elm.read_many(&[0x0C, 0xFE, 0x0D], &obd2_pids::data_bytes_of, None)
        .unwrap();

    assert!(car.received().contains(&"010C0D".to_string()));
}

#[test]
fn a_car_that_answers_several_at_once_is_asked_that_way_from_then_on() {
    let source = connect(&batch_car(BatchReply::All, 6));

    assert!(source.batching());
}

#[test]
fn one_parameter_coming_back_is_not_evidence_of_batching() {
    // The trap in probing this: an ordinary single-parameter reply to the first parameter
    // listed is indistinguishable from a batched reply that carried one. Two is the smallest
    // answer that can only have come from a car that understood the question.
    let source = connect(&batch_car(BatchReply::First, 6));

    assert!(!source.batching());
}

#[test]
fn a_legacy_bus_is_never_asked_for_several_at_once() {
    // J1850 and the ISO serial buses have no such request. What arrives is not refused, it
    // is malformed.
    let source = connect(&batch_car(BatchReply::All, 2));

    assert!(!source.batching());
}

#[test]
fn an_undetermined_protocol_is_not_treated_as_can() {
    // "Not slow" is not "is CAN": an unknown protocol is neither, and both answers have to
    // be no. An asleep ECU answering STOPPED is how a link comes to be unidentified at
    // exactly the wrong moment, and a CAN-only request sent on the strength of that reaches
    // a truck as a malformed one.
    let source = connect(&batch_car(BatchReply::All, 0));

    assert!(!source.batching());
}

#[test]
fn a_round_becomes_two_requests_instead_of_six() {
    let car = batch_car(BatchReply::All, 6);
    let mut source = connect(&car);

    car.with(|s| s.received.clear());
    source.read().unwrap();

    // One for the parameters a needle follows, one for as many of the rest as fit. Every
    // one of them carries several.
    let received = car.received();
    assert_eq!(received.len(), 2);
    for c in &received {
        assert!(c.len() > 4, "{c} asked for one parameter");
    }
}

#[test]
fn the_readings_are_the_same_ones_as_asking_singly() {
    let mut source = connect(&batch_car(BatchReply::All, 6));

    let row = source.read().unwrap();

    assert_eq!(at(&source, &row, "RPM"), 1726.0);
    assert_eq!(at(&source, &row, "MAP"), 100.0);
    assert_eq!(at(&source, &row, "Speed"), 64.0);
    assert_eq!(at(&source, &row, "Coolant"), 50.0);
}

#[test]
fn the_slow_parameters_stop_being_a_queue() {
    // One per round was what a round trip each forced. Six per round costs exactly the same
    // one request, so a fuel level or an oil temperature comes round in a fraction of the
    // time.
    let mut source = connect(&batch_car(BatchReply::All, 6));

    let row = source.read().unwrap();

    // Coolant and IAT both rotate, and both are here after a single round.
    assert_eq!(at(&source, &row, "Coolant"), 50.0);
    assert_eq!(at(&source, &row, "IAT"), 30.0);
}

#[test]
fn a_batch_that_answers_nothing_is_retried_one_at_a_time() {
    // The request failed, which says nothing about whether the car has these sensors, so the
    // round is asked again singly rather than concluded from, and the gauges do not blink
    // while batching is on probation.
    let car = batch_car(BatchReply::All, 6);
    let mut source = connect(&car);

    car.with(|s| s.batches = BatchReply::Refuse);
    let row = source.read().unwrap();

    assert_eq!(at(&source, &row, "RPM"), 1726.0);
}

#[test]
fn three_unanswered_batches_give_up_on_batching_and_not_on_the_channels() {
    let car = batch_car(BatchReply::All, 6);
    let mut source = connect(&car);

    car.with(|s| s.batches = BatchReply::Refuse);

    for _ in 0..3 {
        source.read().unwrap();
    }

    assert!(!source.batching());

    // The channels are all still being read, one at a time.
    let row = source.read().unwrap();

    assert_eq!(at(&source, &row, "RPM"), 1726.0);
    assert_eq!(at(&source, &row, "MAP"), 100.0);
}

#[test]
fn a_parameter_that_has_answered_is_never_given_up_on() {
    // The retirement is never undone, so a run of transient silence (a busy module, a moment
    // of interference) would cost the channel for the rest of the drive. Measured elsewhere
    // on this protocol: a coolant reading 97 C dropped thirty seconds after it was on screen.
    let car = batch_car(BatchReply::Refuse, 6);
    let mut source = connect(&car);

    let row = source.read().unwrap();
    assert_eq!(at(&source, &row, "Coolant"), 50.0);

    // The module goes quiet for a while, a good deal longer than the run that used to retire
    // a parameter, and then comes back.
    car.with(|s| s.answers.remove(&0x05));
    for _ in 0..16 {
        source.read().unwrap();
    }

    car.with(|s| s.answers.insert(0x05, vec![0x5F])); // 55 C

    let mut coolant = f64::NAN;
    for _ in 0..16 {
        if coolant == 55.0 {
            break;
        }
        let row = source.read().unwrap();
        coolant = at(&source, &row, "Coolant");
    }

    assert_eq!(coolant, 55.0);
}

#[test]
fn a_parameter_missing_from_the_reply_is_asked_for_on_its_own() {
    // A reply that carried some of what was asked for is not a round that worked. The rest
    // have to be asked again singly, because nothing else here will ever come back to them:
    // a parameter that has answered before is deliberately never retired, so one that stops
    // appearing in the batched reply simply keeps the gauge it had, showing a reading from
    // whenever the reply last carried it, and looking live the whole time.
    let car = batch_car(BatchReply::All, 6);
    let mut source = connect(&car);

    assert!(source.batching());
    let row = source.read().unwrap();
    assert_eq!(at(&source, &row, "Coolant"), 50.0);

    // The car goes on answering for coolant. Its answer just stops arriving as part of the
    // batch.
    car.with(|s| {
        s.omit_from_batch.insert(0x05);
        s.answers.insert(0x05, vec![0x5F]); // 55 C
    });

    let mut coolant = f64::NAN;
    for _ in 0..8 {
        if coolant == 55.0 {
            break;
        }
        let row = source.read().unwrap();
        coolant = at(&source, &row, "Coolant");
    }

    assert_eq!(coolant, 55.0);
    assert!(
        source.batching(),
        "batching was given up over a reply that mostly worked"
    );
}

// ----- the source half of Obd2FaultTests -----------------------------------------------

/// A scan is reachable from the live source, which is the only place the application can
/// reach an adapter that is already connected and polling.
#[test]
fn scans_through_the_live_source() {
    let fake = FakeElm::new();
    fake.with(|s| {
        s.answers.insert(0x00, vec![0x18, 0x3B, 0x80, 0x11]);
        s.answers.insert(0x01, vec![0x81, 0x07, 0xE5, 0x00]);
        s.answers.insert(0x0C, vec![0x1A, 0xF8]);
        s.answers.insert(0x05, vec![0x5A]);
        s.stored_codes.push("P0301".to_string());
    });

    let source = connect(&fake);

    let scan = source.read_faults().unwrap();

    let stored = scan.stored();
    assert_eq!(stored.iter().map(|f| f.code.as_str()).collect::<Vec<_>>(), ["P0301"]);
    assert_eq!(stored[0].description(), "Cylinder 1 misfire detected");
}

#[test]
fn clearing_faults_goes_through_the_same_gate_and_only_when_asked() {
    let fake = FakeElm::new();
    fake.with(|s| {
        s.answers.insert(0x00, vec![0x18, 0x3B, 0x80, 0x11]);
        s.answers.insert(0x01, vec![0x81, 0x07, 0xE5, 0x00]);
        s.answers.insert(0x0C, vec![0x1A, 0xF8]);
        s.stored_codes.push("P0301".to_string());
    });

    let mut source = connect(&fake);

    // Connecting, polling and scanning never erase anything.
    source.read().unwrap();
    source.read_faults().unwrap();
    assert_eq!(fake.with(|s| s.clear_requests), 0);

    let cleared = source.clear_faults().unwrap();
    assert!(cleared.erased);
    assert_eq!(fake.with(|s| s.clear_requests), 1);
}

mod gate {
    use super::*;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
    use std::time::Duration;

    /// A transport that passes everything through to a car until armed, and then stops
    /// inside the write and counts who is in there (`BlockingTransport`).
    struct Gated {
        car: FakeElm,
        armed: Arc<AtomicBool>,
        release: Arc<AtomicBool>,
        inside: Arc<AtomicI32>,
        deepest: Arc<AtomicI32>,
    }

    impl olv_protocol::ms::ms_protocol::EcuTransport for Gated {
        fn is_open(&self) -> bool {
            self.car.is_open()
        }

        fn open(&mut self) -> Result<()> {
            self.car.open()
        }

        fn close(&mut self) {
            self.car.close();
        }

        fn write(&mut self, data: &[u8]) -> Result<()> {
            if self.armed.load(Ordering::SeqCst) {
                let depth = self.inside.fetch_add(1, Ordering::SeqCst) + 1;
                self.deepest.fetch_max(depth, Ordering::SeqCst);

                let started = std::time::Instant::now();
                while !self.release.load(Ordering::SeqCst) && started.elapsed() < Duration::from_secs(5) {
                    std::thread::sleep(Duration::from_millis(2));
                }

                self.inside.fetch_sub(1, Ordering::SeqCst);
            }

            self.car.write(data)
        }

        fn read(&mut self, buffer: &mut [u8], timeout: Duration) -> Result<usize> {
            self.car.read(buffer, timeout)
        }

        fn discard_input(&mut self) {
            self.car.discard_input();
        }
    }

    /// A scan is asked for from the user interface while the session is still polling, so
    /// two threads reach the same adapter. Written into the same port they interleave into
    /// one stream: the scan reads the tail of a coolant temperature and the poll reads half a
    /// fault code, and both parse.
    ///
    /// The transport blocks inside the write, so a second command that was allowed through
    /// would be sitting in there alongside the first. Depth is measured rather than timed: it
    /// can never exceed one while the gate holds, and would reliably reach two without it.
    #[test]
    fn sends_one_command_at_a_time() {
        let fake = FakeElm::new();
        fake.with(|s| {
            s.answers.insert(0x00, vec![0x18, 0x3B, 0x80, 0x11]);
            s.answers.insert(0x0C, vec![0x1A, 0xF8]);
            s.answers.insert(0x05, vec![0x5A]);
        });

        let armed = Arc::new(AtomicBool::new(false));
        let release = Arc::new(AtomicBool::new(false));
        let inside = Arc::new(AtomicI32::new(0));
        let deepest = Arc::new(AtomicI32::new(0));

        let transport = Gated {
            car: fake,
            armed: Arc::clone(&armed),
            release: Arc::clone(&release),
            inside: Arc::clone(&inside),
            deepest: Arc::clone(&deepest),
        };

        let mut source = Elm327Source::connect(Box::new(transport), "", None).unwrap();
        let faults = source.faults();

        armed.store(true, Ordering::SeqCst);

        let polling = std::thread::spawn(move || {
            let _ = source.read();
            source
        });

        let started = std::time::Instant::now();
        while inside.load(Ordering::SeqCst) == 0 {
            assert!(
                started.elapsed() < Duration::from_secs(5),
                "the first command never arrived"
            );
            std::thread::sleep(Duration::from_millis(2));
        }

        let scanning = std::thread::spawn(move || faults.scan());

        // Long enough for the second to have got in if it were going to. A short wait can
        // only make this test pass wrongly, never fail wrongly.
        std::thread::sleep(Duration::from_millis(300));
        assert_eq!(deepest.load(Ordering::SeqCst), 1);

        release.store(true, Ordering::SeqCst);
        let _source = polling.join().unwrap();
        let _ = scanning.join().unwrap();

        assert_eq!(deepest.load(Ordering::SeqCst), 1);
    }
}

// ----- ScriptedElm: the habits of a real adapter -----------------------------------------

#[test]
fn a_scripted_adapter_is_reachable_from_this_crate() {
    // The wedge tests below drive `Elm327` through these; this is the cheapest check that
    // the copy of the double works at all.
    let wire = ScriptedElm::new(|_| vec![piece(5, "410C1AF8\r\r>")]);
    let mut elm = Elm327::new(Box::new(wire));
    let mut data = [0u8; 8];

    assert!(elm.try_read(0x0C, 2, &mut data, None).unwrap().is_some());
}

/// The adapter lock is taken for each of the two resets alone, as the C# took its lock per
/// send, so a fault scan from another thread is not shut out for the whole of a recovery.
/// (The gap between the resets is microseconds, so this checks that recovery and scans
/// running together both finish and neither is lost; the lock scope itself is by inspection.)
#[test]
fn a_recovery_and_a_fault_scan_on_two_threads_both_finish() {
    let fake = FakeElm::new();
    fake.with(|s| {
        s.answers.insert(0x00, vec![0x18, 0x3B, 0x80, 0x11]);
        s.answers.insert(0x0C, vec![0x1A, 0xF8]);
        s.answers.insert(0x05, vec![0x5A]);
    });

    let mut source = connect(&fake);
    let faults = source.faults();

    let scanning = std::thread::spawn(move || (0..5).all(|_| faults.scan().is_ok()));

    source.recover().unwrap();

    assert!(scanning.join().unwrap());
}
