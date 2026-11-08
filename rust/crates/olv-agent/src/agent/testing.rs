//! A canned bridge for tests: it answers fixed data and records what it was asked to write.
//!
//! The same data the C# server tests (`AgentServerTests.Bench`) and the oracle emitter
//! (`W5Agent.Bench`) use, with switches that mirror the oracle's setups, so one bench serves the
//! ported tests, the wire comparison against the C#, and the MCP server's tests. Only built with
//! the `testing` feature (the crate's own tests turn it on); nothing in the application uses it.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, SystemTime};

use super::agent_api::*;
use olv_core::fmt as nf;
use olv_core::ini::msq_ini::RealtimeType;
use olv_core::ini::tune_layout::TuneConstant;
use olv_core::model::{TuneAxis, TuneTable};
use olv_core::tune::tune_compare::TuneDifference;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(PoisonError::into_inner)
}

/// 2026-01-02 03:04:05.678 UTC, the oracle's fixed time.
pub fn fixed() -> SystemTime {
    SystemTime::UNIX_EPOCH + Duration::new(1_767_323_045, 678_000_000)
}

fn inv(v: f64) -> String {
    nf::f64_inv(v, "")
}

#[derive(Default)]
pub struct State {
    pub brief: String,
    pub needed: Option<AgentDefinitionNeed>,
    pub calls: Vec<String>,
    pub last_rationale: String,
    pub last_confirm_dangerous: bool,
    pub written: Vec<(String, f64)>,
    pub cells: Vec<(String, i32, i32, f64)>,
    pub sittings: Vec<String>,
    pub noted: Vec<String>,
    pub kept: Vec<String>,
    pub proposed: Vec<(Vec<ProposedSetting>, Vec<ProposedCell>)>,
    pub applied: Vec<(usize, usize, String, bool)>,
    pub staged_tune_filenames: Vec<String>,
    pub staged_tables: Vec<(String, String)>,
    pub staged: Vec<AgentStagedFile>,
    pub imported: Vec<(String, String, String, String)>,
    pub next_proposal: Option<TuneProposalResult>,
    pub next_apply: Option<TuneApplyResult>,
    pub next_stage: Option<AgentStageResult>,
    pub next_import: Option<AgentDefinitionImported>,
}

#[derive(Default)]
pub struct Bench {
    pub armed: AtomicBool,
    pub rich: AtomicBool,
    pub extra_event: AtomicBool,
    pub state: Mutex<State>,
}

impl Bench {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn arm(&self) {
        self.armed.store(true, Ordering::SeqCst);
    }

    pub fn is_armed(&self) -> bool {
        self.armed.load(Ordering::SeqCst)
    }

    pub fn with<R>(&self, f: impl FnOnce(&mut State) -> R) -> R {
        f(&mut lock(&self.state))
    }

    pub fn calls(&self) -> Vec<String> {
        lock(&self.state).calls.clone()
    }

    fn rich(&self) -> bool {
        self.rich.load(Ordering::SeqCst)
    }

    fn call(&self, text: String) {
        lock(&self.state).calls.push(text);
    }
}

fn rev_limit() -> TuneConstant {
    let mut constant = TuneConstant::new("revLimit", 0, 2, RealtimeType::U16);
    constant.units = "rpm".to_string();
    constant
}

fn ve_table() -> TuneTable {
    use olv_core::model::Array2;

    let mut values = Array2::new(2, 2);
    values[(0, 0)] = 40.0;
    values[(1, 0)] = 50.0;
    values[(0, 1)] = 60.0;
    values[(1, 1)] = 80.0;

    TuneTable {
        name: "VE Table".to_string(),
        x: TuneAxis {
            constant: "rpmBins".to_string(),
            units: "rpm".to_string(),
            breakpoints: vec![800.0, 3000.0],
        },
        y: TuneAxis {
            constant: "mapBins".to_string(),
            units: "kPa".to_string(),
            breakpoints: vec![30.0, 100.0],
        },
        values,
        units: "%".to_string(),
    }
}

impl AgentBridge for Bench {
    fn state(&self) -> AgentState {
        AgentState {
            mode: "live".to_string(),
            signature: "TEST Format 0001.00".to_string(),
            file: String::new(),
            samples: 3,
            seconds: 0.2,
            rate: 25.0,
            channels: 2,
            has_tune: true,
            writes_armed: self.is_armed(),
            error: String::new(),
        }
    }

    fn limits(&self) -> AgentLimits {
        AgentLimits::new(10, 5.0, 0.5)
    }

    fn channels(&self, raw: bool) -> Vec<AgentChannel> {
        if raw {
            vec![
                AgentChannel::new("RPM", "rpm", 0),
                AgentChannel::new("AFR", "AFR", 2),
                AgentChannel::new("internal_padding_1", "", 0),
            ]
        } else {
            vec![
                AgentChannel::new("RPM", "rpm", 0).with_role("EngineSpeed"),
                AgentChannel::new("AFR", "AFR", 2).with_role("Mixture"),
            ]
        }
    }

    fn wire_health(&self) -> AgentWireHealth {
        AgentWireHealth {
            sampled: 10,
            failures: 1,
            success_rate: 0.9,
            failures_by_kind: vec![("Timeout".to_string(), 1)],
            since_last_success_seconds: Some(0.4),
            longest_recent_ms: Some(12.0),
        }
    }

    fn wire_events(&self, count: i32) -> Vec<AgentWireEvent> {
        self.call(format!("events {count}"));

        let mut all = Vec::new();

        if self.extra_event.load(Ordering::SeqCst) {
            all.push(AgentWireEvent {
                sequence: 2,
                at: fixed(),
                context: "write page 3".to_string(),
                origin: "Agent".to_string(),
                attempt: 0,
                elapsed_ms: 5.5,
                outcome: "Ok".to_string(),
                failure_kind: "None".to_string(),
                detail: String::new(),
            });
        }

        all.push(if self.rich() {
            AgentWireEvent {
                sequence: 1,
                at: fixed() - Duration::from_secs(1),
                context: "read realtime".to_string(),
                origin: "Human".to_string(),
                attempt: 1,
                elapsed_ms: 3.1,
                outcome: "Failed".to_string(),
                failure_kind: "Timeout".to_string(),
                detail: "no \"reply\" <in> time & more".to_string(),
            }
        } else {
            AgentWireEvent {
                sequence: 1,
                at: fixed(),
                context: "read realtime".to_string(),
                origin: "Human".to_string(),
                attempt: 0,
                elapsed_ms: 3.1,
                outcome: "Ok".to_string(),
                failure_kind: "None".to_string(),
                detail: String::new(),
            }
        });

        all.truncate(usize::try_from(count).unwrap_or(0));
        all
    }

    fn values(&self, channel: &str, seconds: f64) -> Vec<f64> {
        self.call(format!("values {channel} {}", inv(seconds)));

        match channel {
            "RPM" => vec![800.0, 3000.0, if self.rich() { 5000.5 } else { 5000.0 }],
            "Lua: torque" => vec![
                f64::NAN,
                f64::INFINITY,
                f64::NEG_INFINITY,
                1e-7,
                1e21,
                123_456_789.125,
                -0.0,
            ],
            _ => vec![14.7, 13.2, 12.6],
        }
    }

    fn times(&self, _seconds: f64) -> Vec<f64> {
        vec![0.0, 0.1, 0.2]
    }

    fn insights(&self) -> Vec<AgentFinding> {
        if self.rich() {
            vec![
                AgentFinding::new("Good", "Fuelling", "Mixture tracks target", "Within 2 %.")
                    .with_evidence("n = 3; mean 14.2 \u{b1} 0.3 \u{2014} it's <fine> & \"good\" + more `x`"),
                AgentFinding::new("Warning", "Knock", "No evidence", "none"),
            ]
        } else {
            vec![AgentFinding::new(
                "Good",
                "Fuelling",
                "Mixture tracks target",
                "Within 2 %.",
            )]
        }
    }

    fn tune_values(&self) -> Vec<(String, f64)> {
        if self.rich() {
            vec![
                ("crankingRPM".to_string(), 300.0),
                ("revLimit".to_string(), 6500.0),
                ("ratio".to_string(), 0.1 + 0.2),
                ("big".to_string(), 1e15),
                ("bad".to_string(), f64::NAN),
            ]
        } else {
            vec![("crankingRPM".to_string(), 300.0), ("revLimit".to_string(), 6500.0)]
        }
    }

    fn table(&self, name: &str) -> Option<TuneTable> {
        (name == "VE Table").then(ve_table)
    }

    fn table_names(&self) -> Vec<String> {
        vec!["VE Table".to_string()]
    }

    fn project_brief(&self) -> String {
        lock(&self.state).brief.clone()
    }

    fn projects(&self) -> Vec<String> {
        vec!["The E28".to_string()]
    }

    fn keep_tune(&self, note: &str) -> Option<AgentRefusal> {
        self.call(format!("keep [{note}]"));
        let mut state = lock(&self.state);

        if state.brief.is_empty() {
            return Some(AgentRefusal::reason("no project is open"));
        }

        state.kept.push(note.to_string());
        None
    }

    fn compare_versions(&self, from: &str, to: &str) -> String {
        self.call(format!("compare [{from}] [{to}]"));

        if lock(&self.state).brief.is_empty() {
            "No project is open.".to_string()
        } else {
            format!("{from} to {to}: 2 settings differ.")
        }
    }

    fn record_sitting(&self, note: &str) -> Option<AgentRefusal> {
        self.call(format!("record [{note}]"));
        let mut state = lock(&self.state);

        if state.brief.is_empty() {
            return Some(AgentRefusal::reason("no project is open"));
        }

        state.sittings.push(note.to_string());
        None
    }

    fn note_fix(&self, id: &str, title: &str, detail: &str, state: &str, change: &str) -> Option<AgentRefusal> {
        self.call(format!("fix [{id}] [{title}] [{detail}] [{state}] [{change}]"));
        let mut held = lock(&self.state);

        if held.brief.is_empty() {
            return Some(AgentRefusal::reason("no project is open"));
        }

        held.noted.push(format!("{id}/{title}/{state}"));
        None
    }

    fn set_setting(&self, name: &str, value: f64, rationale: &str, confirm_dangerous: bool) -> Option<AgentRefusal> {
        self.call(format!(
            "set {name} {} [{rationale}] {}",
            inv(value),
            if confirm_dangerous { "True" } else { "False" }
        ));

        let mut state = lock(&self.state);
        state.last_rationale = rationale.to_string();
        state.last_confirm_dangerous = confirm_dangerous;

        if !self.is_armed() {
            return Some(AgentRefusal::new("writes are not armed", "Tick it in the application."));
        }

        state.written.push((name.to_string(), value));
        None
    }

    fn set_table_cell(
        &self,
        table: &str,
        column: i32,
        row: i32,
        value: f64,
        rationale: &str,
        confirm_dangerous: bool,
    ) -> AgentCellWrite {
        self.call(format!(
            "cell {table} {column} {row} {} [{rationale}] {}",
            inv(value),
            if confirm_dangerous { "True" } else { "False" }
        ));

        let mut state = lock(&self.state);
        state.last_rationale = rationale.to_string();
        state.last_confirm_dangerous = confirm_dangerous;

        if !self.is_armed() {
            return AgentCellWrite::refused(AgentRefusal::reason("writes are not armed"));
        }

        // Clamped to 0-100, standing in for the edit's hold on the firmware's declared range:
        // enough for a test to tell "landed what was asked" apart from "landed something else".
        let landed = value.clamp(0.0, 100.0);
        state.cells.push((table.to_string(), column, row, landed));

        AgentCellWrite::landed(landed, (table == "warn").then(|| "Warning: shares bytes".to_string()))
    }

    fn tune_full(&self) -> AgentTuneFull {
        AgentTuneFull {
            settings: vec![
                AgentSetting {
                    name: "crankingRPM".to_string(),
                    value: 300.0,
                    units: "rpm".to_string(),
                    options: vec![],
                    low: Some(0.0),
                    high: Some(1000.0),
                },
                AgentSetting {
                    name: "egoType".to_string(),
                    value: 1.0,
                    units: String::new(),
                    options: vec!["Off".to_string(), "Narrow Band".to_string(), "Wide Band".to_string()],
                    low: None,
                    high: None,
                },
            ],
            tables: vec![AgentTable {
                name: "VE Table".to_string(),
                units: "%".to_string(),
                columns: 2,
                rows: 2,
                x_bins: vec![800.0, 3000.0],
                y_bins: vec![30.0, 100.0],
                x_units: "rpm".to_string(),
                y_units: "kPa".to_string(),
                x_constant: "rpmBins".to_string(),
                y_constant: "mapBins".to_string(),
                values: vec![vec![40.0, 60.0], vec![50.0, 80.0]],
            }],
        }
    }

    fn log_full(&self, seconds: f64, decimate: i32) -> AgentLogFull {
        self.call(format!("logfull {} {decimate}", inv(seconds)));

        let times = [0.0, 0.1, 0.2, 0.3, 0.4];
        let rpm = [800.0, 1500.0, 3000.0, 4200.0, 5000.0];
        let afr = [14.7, 14.5, 13.2, 12.9, 12.6];

        let mut from = 0;

        if seconds > 0.0 {
            let until = times[times.len() - 1] - seconds;
            for i in (0..times.len()).rev() {
                if times[i] < until {
                    from = i + 1;
                    break;
                }
            }
        }

        let window = |values: &[f64]| -> Vec<f64> {
            let tailed = values.iter().skip(from).copied();

            if decimate <= 1 {
                tailed.collect()
            } else {
                tailed
                    .enumerate()
                    .filter(|(i, _)| i % (decimate as usize) == 0)
                    .map(|(_, v)| v)
                    .collect()
            }
        };

        AgentLogFull {
            times: window(&times),
            channels: vec![
                AgentChannelSamples {
                    name: "RPM".to_string(),
                    units: "rpm".to_string(),
                    values: window(&rpm),
                },
                AgentChannelSamples {
                    name: "AFR".to_string(),
                    units: "AFR".to_string(),
                    values: window(&afr),
                },
            ],
        }
    }

    fn context(&self) -> AgentContext {
        AgentContext {
            project_brief: self.project_brief(),
            state: self.state(),
            tune: AgentTuneSummary {
                setting_count: self.tune_values().len() as i32,
                tables: self.table_names(),
            },
            insights: self.insights(),
            wire_health: self.wire_health(),
        }
    }

    fn propose_tune(&self, settings: &[ProposedSetting], cells: &[ProposedCell]) -> TuneProposalResult {
        let settings_text = settings
            .iter()
            .map(|s| format!("{}={}", s.name, inv(s.value)))
            .collect::<Vec<_>>()
            .join(",");
        let cells_text = cells
            .iter()
            .map(|c| format!("{}[{},{}]={}", c.table, c.column, c.row, inv(c.value)))
            .collect::<Vec<_>>()
            .join(",");
        self.call(format!("propose {settings_text} | {cells_text}"));

        let mut state = lock(&self.state);
        state.proposed.push((settings.to_vec(), cells.to_vec()));

        if let Some(next) = &state.next_proposal {
            return next.clone();
        }

        let mut text = TuneDifference::new("name", rev_limit(), 1, None, None);
        text.mine_text = Some("new \"name\"".to_string());
        text.theirs_text = Some("old".to_string());

        TuneProposalResult {
            changes: vec![
                TuneDifference::new("revLimit", rev_limit(), 1, Some(7000.0), Some(6500.0)),
                text,
                TuneDifference::new("VE Table", rev_limit(), 4, Some(61.5), None),
            ],
            rejected: vec!["foo: no such setting".to_string()],
            summary: "3 settings would change.".to_string(),
        }
    }

    fn apply_tune(
        &self,
        settings: &[ProposedSetting],
        cells: &[ProposedCell],
        note: &str,
        confirm_dangerous: bool,
    ) -> TuneApplyResult {
        self.call(format!(
            "apply {} {} [{note}] {}",
            settings.len(),
            cells.len(),
            if confirm_dangerous { "True" } else { "False" }
        ));

        let mut state = lock(&self.state);
        state
            .applied
            .push((settings.len(), cells.len(), note.to_string(), confirm_dangerous));

        if let Some(next) = &state.next_apply {
            return next.clone();
        }

        if !self.is_armed() {
            return TuneApplyResult::refused(AgentRefusal::new("writes are not armed", "Tick it."));
        }

        TuneApplyResult {
            refusal: None,
            applied: vec![TuneDifference::new(
                "revLimit",
                rev_limit(),
                1,
                Some(7000.0),
                Some(6500.0),
            )],
            rejected: vec!["x: too large".to_string()],
            summary: "Applied 1 change.".to_string(),
        }
    }

    fn stage_tune(&self, _settings: &[ProposedSetting], _cells: &[ProposedCell], filename: &str) -> AgentStageResult {
        self.call(format!("stagetune [{filename}]"));

        let mut state = lock(&self.state);
        state.staged_tune_filenames.push(filename.to_string());

        if let Some(next) = &state.next_stage {
            return next.clone();
        }

        if filename == "bad" {
            return AgentStageResult::refused("could not stage that file", "no");
        }

        AgentStageResult::staged(AgentStagedFile {
            name: "tune-test.msq".to_string(),
            path: "C:\\staged\\tune-test.msq".to_string(),
            bytes: 42,
            written_at: fixed(),
        })
    }

    fn stage_table(&self, name: &str, filename: &str) -> AgentStageResult {
        self.call(format!("stagetable [{name}] [{filename}]"));
        lock(&self.state)
            .staged_tables
            .push((name.to_string(), filename.to_string()));

        match name {
            "VE Table" => AgentStageResult::staged(AgentStagedFile {
                name: "VE-Table-test.csv".to_string(),
                path: "C:\\staged\\VE-Table-test.csv".to_string(),
                bytes: 24,
                written_at: fixed(),
            }),
            "locked" => AgentStageResult::refused("could not write the staged file", "denied"),
            _ => AgentStageResult::refused("no such table", name),
        }
    }

    fn list_staged(&self) -> Vec<AgentStagedFile> {
        let state = lock(&self.state);

        if state.staged.is_empty() {
            vec![AgentStagedFile {
                name: "tune-1.msq".to_string(),
                path: "C:\\staged\\tune-1.msq".to_string(),
                bytes: 100,
                written_at: fixed(),
            }]
        } else {
            state.staged.clone()
        }
    }

    fn definition_needed(&self) -> Option<AgentDefinitionNeed> {
        lock(&self.state).needed.clone()
    }

    fn import_definition(&self, path: &str, content: &str, source: &str, name: &str) -> AgentDefinitionImported {
        self.call(format!("import [{path}] [{content}] [{source}] [{name}]"));

        let mut state = lock(&self.state);
        state.imported.push((
            path.to_string(),
            content.to_string(),
            source.to_string(),
            name.to_string(),
        ));

        if let Some(next) = &state.next_import {
            return next.clone();
        }

        if path == "wrong" {
            return AgentDefinitionImported {
                problem: "it declares \"speeduino 202402\"".to_string(),
                ..AgentDefinitionImported::default()
            };
        }

        AgentDefinitionImported {
            problem: String::new(),
            path: "C:\\defs\\speeduino202501.ini".to_string(),
            name: "speeduino202501.ini".to_string(),
            signature: "speeduino 202501".to_string(),
            source: source.to_string(),
        }
    }
}

pub fn needed() -> AgentDefinitionNeed {
    AgentDefinitionNeed {
        identity: vec!["Speeduino 2025.01.7".to_string(), "speeduino 202501".to_string()],
        family: "Speeduino".to_string(),
        signature: "speeduino 202501".to_string(),
        version: "Speeduino 2025.01.7".to_string(),
        filename: "speeduino202501.ini".to_string(),
        nearby: vec![AgentNearMiss {
            name: "speeduino202402.ini".to_string(),
            signature: "speeduino 202402".to_string(),
            path: "C:\\defs\\speeduino202402.ini".to_string(),
        }],
        folder: "C:\\defs".to_string(),
        where_: "somewhere".to_string(),
        sources: vec![
            "https://raw.githubusercontent.com/speeduino/speeduino/202501.7/reference/speeduino.ini".to_string(),
        ],
        guidance: "fetch it".to_string(),
    }
}
