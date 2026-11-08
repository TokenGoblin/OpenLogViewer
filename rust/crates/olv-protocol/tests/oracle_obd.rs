//! The OBD2, ELM327, SSM and realtime-decoder code against the C# reference
//! (rust/oracle/expected/w2-obd/*.json, and corpus/w2-obd/realtime.json where the real INIs
//! are on the machine).
//!
//! Doubles travel as IEEE bits and are compared as bits, NaN with NaN (the sign of a NaN is
//! not something either side promises). Everything else is compared exactly: every command
//! string sent to the adapter, every parsed byte, every code and message.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use olv_core::Result;
use olv_core::ini::msq_ini;
use olv_core::model::tuning_text;
use olv_protocol::ms::ms_protocol::EcuTransport;
use olv_protocol::obd::elm327::{self, Elm327};
use olv_protocol::obd::obd2_codes;
use olv_protocol::obd::obd2_faults::{self, Dtc, DtcState, FaultScan};
use olv_protocol::obd::obd2_gauges;
use olv_protocol::obd::obd2_pids;
use olv_protocol::obd::realtime_decoder::RealtimeDecoder;
use olv_protocol::obd::ssm::{self, SsmParameter};
use olv_protocol::obd::ssm_parameter_file as ssm_file;
use serde_json::Value;

// ----- helpers ------------------------------------------------------------------------

fn bits(hex: &str) -> f64 {
    f64::from_bits(u64::from_str_radix(hex, 16).expect("hex bits"))
}

#[track_caller]
fn same(actual: f64, expected_hex: &str, what: &str) {
    let expected = bits(expected_hex);
    assert!(
        (actual.is_nan() && expected.is_nan()) || actual.to_bits() == expected.to_bits(),
        "{what}: got {actual} ({:016x}), C# gave {expected} ({expected_hex})",
        actual.to_bits()
    );
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02X}")).collect()
}

fn unhex_str(s: &str) -> Vec<u8> {
    (0..s.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("hex"))
        .collect()
}

fn s(v: &Value) -> &str {
    v.as_str().unwrap_or_else(|| panic!("not a string: {v}"))
}

fn n(v: &Value) -> i64 {
    v.as_i64().unwrap_or_else(|| panic!("not an integer: {v}"))
}

fn b(v: &Value) -> bool {
    v.as_bool().unwrap_or_else(|| panic!("not a bool: {v}"))
}

fn list(v: &Value) -> &Vec<Value> {
    v.as_array().unwrap_or_else(|| panic!("not an array: {v}"))
}

fn strings(v: &Value) -> Vec<String> {
    list(v).iter().map(|x| s(x).to_string()).collect()
}

fn cases(set: &str) -> Vec<Value> {
    let cases: Vec<Value> = olv_oracle::cases("w2-obd", set);
    assert!(!cases.is_empty());
    cases
}

// ----- pids ---------------------------------------------------------------------------

#[test]
fn every_pid_decodes_like_the_csharp() {
    let mut checked = 0;

    for case in cases("pids") {
        match s(&case["kind"]) {
            "pid" => {
                let pid = n(&case["pid"]) as u8;
                let ours = obd2_pids::all()
                    .iter()
                    .find(|p| p.pid == pid)
                    .unwrap_or_else(|| panic!("pid {pid:02X} missing"));

                assert_eq!(ours.data_bytes as i64, n(&case["data_bytes"]), "{ours}");
                assert_eq!(ours.hot, b(&case["hot"]), "{ours}");
                assert_eq!(ours.to_string(), s(&case["text"]));

                let channels = list(&case["channels"]);
                assert_eq!(ours.channels.len(), channels.len(), "{ours}");
                for (c, expected) in ours.channels.iter().zip(channels) {
                    assert_eq!(c.name, s(&expected["name"]));
                    assert_eq!(c.units, s(&expected["units"]));
                    assert_eq!(i64::from(c.digits), n(&expected["digits"]));
                    same(c.low, s(&expected["low"]), "low");
                    same(c.high, s(&expected["high"]), "high");
                    same(c.low_danger, s(&expected["low_danger"]), "low_danger");
                    same(c.low_warning, s(&expected["low_warning"]), "low_warning");
                    same(c.high_warning, s(&expected["high_warning"]), "high_warning");
                    same(c.high_danger, s(&expected["high_danger"]), "high_danger");
                }

                for d in list(&case["decodes"]) {
                    let data = unhex_str(s(&d["data"]));
                    let mut into = vec![0.0; ours.channels.len()];
                    ours.decode(&data, &mut into).unwrap();

                    for (i, expected) in list(&d["values"]).iter().enumerate() {
                        same(into[i], s(expected), &format!("{ours} over {}", s(&d["data"])));
                    }
                    checked += 1;
                }
            }

            "support" => {
                let queries: Vec<u8> = list(&case["queries"]).iter().map(|q| n(q) as u8).collect();
                assert_eq!(queries, obd2_pids::SUPPORT_QUERIES);

                for r in list(&case["results"]) {
                    let got = obd2_pids::supported_by(n(&r["query"]) as u8, &unhex_str(s(&r["mask"])));
                    let expected: Vec<u8> = list(&r["supported"]).iter().map(|p| n(p) as u8).collect();
                    assert_eq!(got, expected, "query {} mask {}", n(&r["query"]), s(&r["mask"]));
                }

                let wrapped: Vec<u8> = list(&case["wrapped"]).iter().map(|p| n(p) as u8).collect();
                assert_eq!(obd2_pids::supported_by(0xF0, &[0xFF; 4]), wrapped);

                for (pid, expected) in list(&case["lengths"]).iter().enumerate() {
                    assert_eq!(obd2_pids::data_bytes_of(pid as u8) as i64, n(expected), "pid {pid:02X}");
                }

                let known: Vec<u8> = obd2_pids::known([0x0C, 0x05, 0x99, 0x01, 0x0C])
                    .iter()
                    .map(|p| p.pid)
                    .collect();
                let expected: Vec<u8> = list(&case["known"]).iter().map(|p| n(p) as u8).collect();
                assert_eq!(known, expected);
            }

            "gauges" => {
                let gauges = obd2_gauges::for_pids(obd2_pids::all());
                let expected = list(&case["gauges"]);
                assert_eq!(gauges.len(), expected.len());

                for (g, e) in gauges.iter().zip(expected) {
                    assert_eq!(g.name, s(&e["name"]));
                    assert_eq!(g.channel, s(&e["channel"]));
                    assert_eq!(g.title, s(&e["title"]));
                    assert_eq!(g.units, s(&e["units"]));
                    assert_eq!(g.category, s(&e["category"]));
                    same(g.low, s(&e["low"]), "gauge low");
                    same(g.high, s(&e["high"]), "gauge high");
                    same(g.low_danger, s(&e["low_danger"]), "gauge low_danger");
                    same(g.low_warning, s(&e["low_warning"]), "gauge low_warning");
                    same(g.high_warning, s(&e["high_warning"]), "gauge high_warning");
                    same(g.high_danger, s(&e["high_danger"]), "gauge high_danger");
                    assert_eq!(i64::from(g.value_digits), n(&e["value_digits"]));
                    assert_eq!(i64::from(g.label_digits), n(&e["label_digits"]));
                }

                assert_eq!(obd2_gauges::FRONT_PAGE.to_vec(), strings(&case["front_page"]));
                same(obd2_pids::TACHOMETER_TOP, s(&case["tachometer_top"]), "tachometer top");
            }

            other => panic!("unknown pids case {other}"),
        }
    }

    assert!(checked > 3000, "only {checked} decodes were checked");
}

// ----- codes --------------------------------------------------------------------------

#[test]
fn trouble_code_meanings_match_the_csharp() {
    let mut checked = 0;

    for case in cases("codes") {
        if case["kind"].as_str() == Some("named") {
            assert_eq!(obd2_codes::named() as i64, n(&case["named"]));
            continue;
        }

        let code = case["code"].as_str();
        let what = format!("{code:?}");

        assert_eq!(obd2_codes::is_well_formed(code), b(&case["well_formed"]), "{what}");
        let authority = match obd2_codes::authority(code) {
            obd2_codes::DtcAuthority::Generic => "Generic",
            obd2_codes::DtcAuthority::Manufacturer => "Manufacturer",
        };
        assert_eq!(authority, s(&case["authority"]), "{what}");
        assert_eq!(obd2_codes::system(code), s(&case["system"]), "{what}");
        assert_eq!(obd2_codes::describe(code), s(&case["describe"]), "{what}");

        if let Some(code) = code {
            let fault = Dtc::new(code, DtcState::Stored);
            assert_eq!(fault.detail(), s(&case["detail"]), "{what}");
            assert_eq!(fault.to_string(), s(&case["text"]), "{what}");
        }
        checked += 1;
    }

    assert!(checked > 3000, "only {checked} codes were checked");
}

// ----- faults -------------------------------------------------------------------------

fn state_name(state: DtcState) -> &'static str {
    match state {
        DtcState::Stored => "Stored",
        DtcState::Pending => "Pending",
        DtcState::Permanent => "Permanent",
    }
}

fn codes_of(faults: &[Dtc]) -> Vec<String> {
    faults.iter().map(|f| f.code.clone()).collect()
}

fn faults_text(faults: &[Dtc]) -> Vec<String> {
    faults
        .iter()
        .map(|f| format!("{}/{}", f.code, state_name(f.state)))
        .collect()
}

/// Replies whose C# answer decodes codes that follow a padding pair.
const PADDING_DIVERGENCE: &[&str] = &["43 00 00 01 33"];

#[test]
fn fault_codes_and_replies_match_the_csharp() {
    let mut replies = 0;

    for case in cases("faults") {
        match s(&case["kind"]) {
            "decode" => {
                for d in list(&case["decodes"]) {
                    let (high, low) = (n(&d["high"]) as u8, n(&d["low"]) as u8);
                    let code = obd2_faults::decode(high, low);
                    assert_eq!(code, s(&d["code"]), "{high:02X}{low:02X}");

                    let (h, l) = obd2_faults::encode(&code).unwrap();
                    assert_eq!(
                        (i64::from(h), i64::from(l)),
                        (n(&d["enc_high"]), n(&d["enc_low"])),
                        "{code}"
                    );
                }
            }

            "encode" => {
                for e in list(&case["encodes"]) {
                    let code = s(&e["code"]);
                    match obd2_faults::encode(code) {
                        Ok((h, l)) => {
                            assert!(b(&e["ok"]), "{code:?} should have been refused");
                            assert_eq!((i64::from(h), i64::from(l)), (n(&e["high"]), n(&e["low"])), "{code}");
                        }
                        Err(_) => assert!(!b(&e["ok"]), "{code:?} should have been accepted"),
                    }
                }
            }

            "reply" => {
                let reply = s(&case["reply"]);
                let mode = n(&case["mode"]) as u8;
                let what = format!("{reply:?} mode {mode:02X}");

                assert_eq!(obd2_faults::answered(Some(reply), mode), b(&case["answered"]), "{what}");

                let units: Vec<String> = obd2_faults::units(reply, obd2_faults::echoes(mode))
                    .iter()
                    .map(|u| hex(u))
                    .collect();
                assert_eq!(units, strings(&case["units"]), "{what}");

                let counted = obd2_faults::parse(reply, mode, DtcState::Stored, true);
                let bare = obd2_faults::parse(reply, mode, DtcState::Pending, false);
                assert!(counted.iter().all(|f| f.state == DtcState::Stored));
                assert!(bare.iter().all(|f| f.state == DtcState::Pending));

                // INTENDED DIVERGENCE (CSHARP-DEFECTS.md, Obd2Faults.Parse): a zero pair is
                // padding and so is everything after it in its unit; the C# skipped only the
                // pair, so a reply with codes after padding decodes differently. Those replies
                // are not compared; `nothing_after_the_padding_is_a_code` (and the check just
                // below) pin the corrected reading.
                let (want_counted, want_bare) = (strings(&case["counted"]), strings(&case["bare"]));
                let diverges = codes_of(&counted) != want_counted || codes_of(&bare) != want_bare;
                if diverges {
                    assert!(PADDING_DIVERGENCE.contains(&reply), "{what}: differs from the C#");
                    eprintln!("skipped (padding defect): {what}");
                    assert!(codes_of(&counted).len() <= want_counted.len(), "{what}");
                    assert!(codes_of(&bare).len() <= want_bare.len(), "{what}");
                }

                replies += 1;
            }

            "echoes" => {
                for (mode, expected) in list(&case["values"]).iter().enumerate() {
                    assert_eq!(i64::from(obd2_faults::echoes(mode as u8)), n(expected));
                }
            }

            "scans" => {
                for e in list(&case["scans"]) {
                    let faults: Vec<Dtc> = strings(&e["faults"])
                        .iter()
                        .map(|f| {
                            let (code, state) = f.split_once('/').unwrap();
                            let state = match state {
                                "Stored" => DtcState::Stored,
                                "Pending" => DtcState::Pending,
                                _ => DtcState::Permanent,
                            };
                            Dtc::new(code, state)
                        })
                        .collect();

                    let scan = FaultScan {
                        faults,
                        mil_on: b(&e["mil_on"]),
                        reported_count: n(&e["reported_count"]) as i32,
                        protocol: s(&e["protocol"]).to_string(),
                        trouble: e["trouble"].as_str().unwrap_or_default().to_string(),
                    };

                    assert_eq!(scan.clean(), b(&e["clean"]));
                    assert_eq!(scan.count_disagrees(), b(&e["count_disagrees"]));
                    assert_eq!(scan.summary(), s(&e["summary"]));
                    assert_eq!(scan.stored().len() as i64, n(&e["stored"]));
                    assert_eq!(scan.pending().len() as i64, n(&e["pending"]));
                    assert_eq!(scan.permanent().len() as i64, n(&e["permanent"]));
                }
            }

            other => panic!("unknown faults case {other}"),
        }
    }

    assert!(replies > 150, "only {replies} replies were checked");
}

// ----- mode-01 reply parsing ----------------------------------------------------------

#[test]
fn reply_parsing_matches_the_csharp() {
    let length = |pid: u8| obd2_pids::data_bytes_of(pid);

    for case in cases("parse") {
        let reply = case["reply"].as_str().unwrap_or_default();
        let what = format!("{reply:?}");

        for t in list(&case["tries"]) {
            let pid = n(&t["pid"]) as u8;
            let bytes = n(&t["bytes"]) as usize;

            let mut into = vec![0u8; bytes];
            let got = elm327::try_parse(reply, pid, bytes, &mut into);
            assert_eq!(got.is_some(), b(&t["ok"]), "{what} pid {pid:02X}");
            assert_eq!(got.unwrap_or(0) as i64, n(&t["count"]), "{what} pid {pid:02X}");
            assert_eq!(hex(&into), s(&t["data"]), "{what} pid {pid:02X}");

            let mut small = [0u8; 1];
            let got = elm327::try_parse(reply, pid, bytes, &mut small);
            assert_eq!(got.is_some(), b(&t["ok_small"]), "{what} pid {pid:02X} small");
            assert_eq!(
                got.unwrap_or(0) as i64,
                n(&t["count_small"]),
                "{what} pid {pid:02X} small"
            );

            let all: Vec<String> = elm327::parse_all(reply, pid, bytes).iter().map(|a| hex(a)).collect();
            assert_eq!(all, strings(&t["all"]), "{what} pid {pid:02X} all");
        }

        for u in list(&case["unhex"]) {
            let size = n(&u["size"]) as usize;
            let mut buffer = vec![0u8; size];
            let got = elm327::unhex(reply, &mut buffer);
            assert_eq!(got as i64, n(&u["got"]), "{what} size {size}");
            assert_eq!(hex(&buffer), s(&u["data"]), "{what} size {size}");
        }

        for batch in list(&case["batches"]) {
            let asked = unhex_str(s(&batch["asked"]));
            let answers = elm327::parse_batch(reply, &asked, &length);
            let got: Vec<(i64, String)> = answers.iter().map(|a| (i64::from(a.pid), hex(&a.data))).collect();
            let expected: Vec<(i64, String)> = list(&batch["answers"])
                .iter()
                .map(|a| (n(&a["pid"]), s(&a["data"]).to_string()))
                .collect();
            assert_eq!(got, expected, "{what} asked {}", s(&batch["asked"]));
        }
    }
}

// ----- the commands on the wire -------------------------------------------------------

/// Answers instantly from a table and ends every reply with the prompt; unknown commands get
/// "?". The same fake the C# emitter used, so no clock is involved.
#[derive(Clone)]
struct Table(Arc<Mutex<TableState>>);

struct TableState {
    replies: Vec<(String, String)>,
    out: VecDeque<u8>,
    input: String,
    sent: Vec<String>,
}

impl Table {
    fn new(replies: Vec<(String, String)>) -> Self {
        Self(Arc::new(Mutex::new(TableState {
            replies,
            out: VecDeque::new(),
            input: String::new(),
            sent: Vec::new(),
        })))
    }

    fn sent(&self) -> Vec<String> {
        self.0.lock().unwrap().sent.clone()
    }
}

impl EcuTransport for Table {
    fn is_open(&self) -> bool {
        true
    }

    fn open(&mut self) -> Result<()> {
        Ok(())
    }

    fn close(&mut self) {}

    fn write(&mut self, data: &[u8]) -> Result<()> {
        let mut st = self.0.lock().unwrap();
        for &byte in data {
            if byte != b'\r' {
                st.input.push(char::from(byte));
                continue;
            }

            let command = std::mem::take(&mut st.input);
            st.sent.push(command.clone());

            let reply = st
                .replies
                .iter()
                .find(|(c, _)| *c == command)
                .map_or("?".to_string(), |(_, r)| r.clone());
            for c in format!("{reply}\r\r>").chars() {
                st.out.push_back(c as u8);
            }
        }
        Ok(())
    }

    fn read(&mut self, buffer: &mut [u8], _timeout: Duration) -> Result<usize> {
        let mut st = self.0.lock().unwrap();
        let mut count = 0;
        while count < buffer.len() {
            let Some(byte) = st.out.pop_front() else { break };
            buffer[count] = byte;
            count += 1;
        }
        Ok(count)
    }

    fn discard_input(&mut self) {
        self.0.lock().unwrap().out.clear();
    }
}

fn describe_scan(scan: &FaultScan) -> String {
    format!(
        "{}|mil={}|count={}|proto={}|trouble={}|summary={}",
        faults_text(&scan.faults).join(";"),
        u8::from(scan.mil_on),
        scan.reported_count,
        scan.protocol,
        scan.trouble,
        scan.summary()
    )
}

#[test]
fn the_commands_sent_and_what_comes_back_match_the_csharp() {
    for case in cases("wire") {
        let name = s(&case["name"]);

        let replies: Vec<(String, String)> = list(&case["replies"])
            .iter()
            .map(|r| (s(&r["command"]).to_string(), s(&r["reply"]).to_string()))
            .collect();

        let table = Table::new(replies);
        let mut elm = Elm327::new(Box::new(table.clone()));
        let mut results = Vec::new();

        for action in strings(&case["actions"]) {
            let parts: Vec<&str> = action.split(':').collect();

            let result = match parts[0] {
                "reset" => elm.reset(None).unwrap(),
                "identify" => elm.identify().unwrap(),
                "protocol_name" => elm.protocol_name().unwrap(),
                "protocol_number" => elm.protocol_number().unwrap().to_string(),
                "is_can" => (if elm.is_can().unwrap() { "1" } else { "0" }).to_string(),
                "warm_up" => {
                    elm.warm_up().unwrap();
                    String::new()
                }
                "send" => {
                    let timeout = elm.timeout;
                    elm.send(parts[1], timeout, false, 0).unwrap()
                }
                "read" => {
                    let pid = u8::from_str_radix(parts[1], 16).unwrap();
                    let bytes: usize = parts[2].parse().unwrap();
                    let mut into = [0u8; 32];
                    match elm.try_read(pid, bytes, &mut into, None).unwrap() {
                        Some(count) => hex(&into[..count]),
                        None => "none".to_string(),
                    }
                }
                "read_all" => {
                    let pid = u8::from_str_radix(parts[1], 16).unwrap();
                    let bytes: usize = parts[2].parse().unwrap();
                    elm.read_all(pid, bytes, None)
                        .unwrap()
                        .iter()
                        .map(|a| hex(a))
                        .collect::<Vec<_>>()
                        .join(";")
                }
                "many" => {
                    let pids: Vec<u8> = parts[1]
                        .split(',')
                        .map(|p| u8::from_str_radix(p, 16).unwrap())
                        .collect();
                    elm.read_many(&pids, &obd2_pids::data_bytes_of, None)
                        .unwrap()
                        .iter()
                        .map(|a| format!("{:02X}={}", a.pid, hex(&a.data)))
                        .collect::<Vec<_>>()
                        .join(";")
                }
                "scan" => describe_scan(&obd2_faults::scan(&mut elm).unwrap()),
                "clear" => {
                    let clear = obd2_faults::clear(&mut elm).unwrap();
                    format!(
                        "{}|{}|{}",
                        u8::from(clear.erased),
                        clear.message,
                        faults_text(&clear.remaining).join(";")
                    )
                }
                other => panic!("unknown action {other}"),
            };

            results.push(result);
        }

        assert_eq!(table.sent(), strings(&case["sent"]), "{name}: commands sent");
        assert_eq!(results, strings(&case["results"]), "{name}: results");
    }
}

// ----- SSM ----------------------------------------------------------------------------

fn params() -> Vec<SsmParameter> {
    let make = |name: &str, address: i32, f: &dyn Fn(&mut SsmParameter)| {
        let mut p = SsmParameter::new(name, address);
        f(&mut p);
        p
    };

    vec![
        make("Engine Speed", 0x0E, &|p| {
            p.bytes = 2;
            p.scale = 0.25;
        }),
        make("Coolant", 0x08, &|p| {
            p.units = "\u{b0}C".to_string();
            p.offset = -40.0;
        }),
        make("Wide", 0x10, &|p| {
            p.bytes = 4;
            p.scale = 0.001;
            p.offset = 3.5;
        }),
        make("Zero", 0x10, &|p| p.bytes = 0),
        make("Five", 0x10, &|p| p.bytes = 5),
        make("", 0x10, &|_| {}),
        make("Neg", -1, &|_| {}),
        make("NoScale", 0x10, &|p| p.scale = 0.0),
    ]
}

#[test]
fn ssm_requests_replies_parameters_and_files_match_the_csharp() {
    for case in cases("ssm") {
        match s(&case["kind"]) {
            "requests" => {
                for r in list(&case["requests"]) {
                    let addresses: Vec<i32> = list(&r["addresses"]).iter().map(|a| n(a) as i32).collect();
                    match ssm::read_request(&addresses) {
                        Ok(text) => {
                            assert!(b(&r["ok"]), "{addresses:?} should have been refused");
                            assert_eq!(text, s(&r["text"]), "{addresses:?}");
                        }
                        Err(_) => assert!(!b(&r["ok"]), "{addresses:?} should have been accepted"),
                    }
                }

                let setup: Vec<String> = ssm::SETUP.iter().map(|x| x.to_string()).collect();
                assert_eq!(setup, strings(&case["setup"]));
                let restore: Vec<String> = ssm::RESTORE.iter().map(|x| x.to_string()).collect();
                assert_eq!(restore, strings(&case["restore"]));
                assert_eq!(ssm::ENGINE_HEADER, s(&case["engine_header"]));
                assert_eq!(ssm::ENGINE_REPLIES, s(&case["engine_replies"]));
                assert_eq!(ssm::ADDRESSES_PER_REQUEST as i64, n(&case["addresses_per_request"]));
            }

            "replies" => {
                for r in list(&case["replies"]) {
                    let reply = r["reply"].as_str();
                    let expected = n(&r["expected"]) as usize;
                    assert_eq!(
                        hex(&ssm::read_reply(reply, expected)),
                        s(&r["data"]),
                        "{reply:?} x{expected}"
                    );
                    assert_eq!(ssm::refused(reply), b(&r["refused"]), "{reply:?}");
                }
            }

            "parameters" => {
                let ours = params();
                let theirs = list(&case["parameters"]);
                assert_eq!(ours.len(), theirs.len());

                for (p, e) in ours.iter().zip(theirs) {
                    assert_eq!(p.name, s(&e["name"]));
                    assert_eq!(i64::from(p.address), n(&e["address"]));
                    assert_eq!(i64::from(p.bytes), n(&e["bytes"]));
                    assert_eq!(p.units, s(&e["units"]));
                    assert_eq!(p.is_usable(), b(&e["usable"]), "{}", p.name);

                    let addresses: Vec<i64> = p.addresses().take(8).map(i64::from).collect();
                    let expected: Vec<i64> = list(&e["addresses"]).iter().map(n).collect();
                    assert_eq!(addresses, expected, "{}", p.name);

                    for read in list(&e["reads"]) {
                        let raw = unhex_str(s(&read["raw"]));
                        same(
                            p.read(&raw),
                            s(&read["value"]),
                            &format!("{} over {}", p.name, s(&read["raw"])),
                        );
                    }
                }
            }

            "addresses" => {
                for a in list(&case["addresses"]) {
                    let text = a["text"].as_str();
                    let got = ssm_file::parse_address(text).map(i64::from);
                    assert_eq!(got, a["value"].as_i64(), "{text:?}");
                }
            }

            "files" => {
                assert_eq!(ssm_file::NAME, s(&case["name"]));
                assert_eq!(ssm_file::template(), s(&case["template"]));

                for f in list(&case["files"]) {
                    let json = f["json"].as_str();
                    let what = format!("{json:?}");

                    let read = ssm_file::read(json);
                    let expected = list(&f["read"]);
                    assert_eq!(read.len(), expected.len(), "{what}");

                    for (p, e) in read.iter().zip(expected) {
                        assert_eq!(p.name, s(&e["name"]), "{what}");
                        assert_eq!(i64::from(p.address), n(&e["address"]), "{what}");
                        assert_eq!(i64::from(p.bytes), n(&e["bytes"]), "{what}");
                        assert_eq!(p.units, s(&e["units"]), "{what}");
                        same(p.scale, s(&e["scale"]), "scale");
                        same(p.offset, s(&e["offset"]), "offset");
                        assert_eq!(i64::from(p.digits), n(&e["digits"]), "{what}");
                        same(p.low, s(&e["low"]), "low");
                        same(p.high, s(&e["high"]), "high");
                        assert_eq!(p.enabled, b(&e["enabled"]), "{what}");
                    }

                    let enabled: Vec<String> = ssm_file::enabled(json).into_iter().map(|p| p.name).collect();
                    assert_eq!(enabled, strings(&f["enabled"]), "{what}");
                }
            }

            other => panic!("unknown ssm case {other}"),
        }
    }
}

// ----- the realtime decoder against the real INIs --------------------------------------

#[test]
fn the_realtime_decoder_matches_the_csharp_on_the_real_inis() {
    let Some(cases) = olv_oracle::corpus_cases::<Value>("w2-obd", "realtime") else {
        return;
    };

    for case in &cases {
        let file = s(&case["file"]);
        let path = olv_oracle::corpus_dir().join("definitions").join(file);
        let text = tuning_text::read(&path).unwrap_or_else(|e| panic!("{file}: {e}"));

        let layout = msq_ini::read_output_channels(&text, None);
        assert_eq!(i64::from(layout.block_size), n(&case["block_size"]), "{file}");
        assert_eq!(layout.little_endian, b(&case["little_endian"]), "{file}");

        let settings: Vec<(String, f64)> = list(&case["settings"])
            .iter()
            .map(|e| (s(&e["name"]).to_string(), bits(s(&e["value"]))))
            .collect();

        let decoder = RealtimeDecoder::new(layout.clone(), Some(&settings));
        let bare = RealtimeDecoder::new(layout, None);

        assert_eq!(decoder.names(), strings(&case["names"]), "{file}: names");
        assert_eq!(decoder.units(), strings(&case["units"]), "{file}: units");
        let digits: Vec<i32> = list(&case["digits"]).iter().map(|d| n(d) as i32).collect();
        assert_eq!(decoder.digits(), digits, "{file}: digits");
        assert_eq!(
            decoder.unresolved_expressions(),
            strings(&case["unresolved"]),
            "{file}: unresolved"
        );
        assert_eq!(bare.names(), strings(&case["bare_names"]), "{file}: bare names");
        assert_eq!(
            bare.unresolved_expressions(),
            strings(&case["bare_unresolved"]),
            "{file}: bare unresolved"
        );

        for block in list(&case["blocks"]) {
            let bytes = unhex_str(s(&block["block"]));
            let what = format!("{file} {} {}", s(&block["kind"]), s(&block["length"]));

            for (decoder, key) in [(&decoder, "values"), (&bare, "bare_values")] {
                let values = decoder.decode(&bytes);
                let expected = list(&block[key]);
                assert_eq!(values.len(), expected.len(), "{what} {key}");

                for (i, (v, e)) in values.iter().zip(expected).enumerate() {
                    let e = s(e);
                    if e == "nan" {
                        assert!(v.is_nan(), "{what} {key}: {} gave {v}, C# NaN", decoder.names()[i]);
                    } else {
                        same(*v, e, &format!("{what} {key}: {}", decoder.names()[i]));
                    }
                }
            }
        }
    }
}
