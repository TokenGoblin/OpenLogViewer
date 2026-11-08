//! A bridge whose tune half is the real writer, over a fake controller, and whose other half is
//! the fixed bench. What the application's own bridge will be, minus the log and the project.

#![allow(dead_code)]

use std::sync::Arc;

use olv_agent::agent::agent_api::*;
use olv_agent::agent::agent_writer::AgentWriter;
use olv_core::model::TuneTable;

use super::bench::Bench;

pub struct WriterBridge {
    pub writer: Arc<AgentWriter>,
    pub rest: Bench,
}

impl AgentBridge for WriterBridge {
    fn state(&self) -> AgentState {
        AgentState {
            writes_armed: self.writer.is_armed(),
            ..self.rest.state()
        }
    }
    fn limits(&self) -> AgentLimits {
        self.writer.limits()
    }
    fn channels(&self, raw: bool) -> Vec<AgentChannel> {
        self.rest.channels(raw)
    }
    fn wire_health(&self) -> AgentWireHealth {
        self.rest.wire_health()
    }
    fn wire_events(&self, count: i32) -> Vec<AgentWireEvent> {
        self.rest.wire_events(count)
    }
    fn values(&self, channel: &str, seconds: f64) -> Vec<f64> {
        self.rest.values(channel, seconds)
    }
    fn times(&self, seconds: f64) -> Vec<f64> {
        self.rest.times(seconds)
    }
    fn insights(&self) -> Vec<AgentFinding> {
        self.rest.insights()
    }
    fn tune_values(&self) -> Vec<(String, f64)> {
        self.writer.tune_values()
    }
    fn table(&self, name: &str) -> Option<TuneTable> {
        self.writer.table(name)
    }
    fn table_names(&self) -> Vec<String> {
        self.writer.table_names()
    }
    fn project_brief(&self) -> String {
        self.rest.project_brief()
    }
    fn projects(&self) -> Vec<String> {
        self.rest.projects()
    }
    fn keep_tune(&self, note: &str) -> Option<AgentRefusal> {
        self.rest.keep_tune(note)
    }
    fn compare_versions(&self, from: &str, to: &str) -> String {
        self.rest.compare_versions(from, to)
    }
    fn record_sitting(&self, note: &str) -> Option<AgentRefusal> {
        self.rest.record_sitting(note)
    }
    fn note_fix(&self, a: &str, b: &str, c: &str, d: &str, e: &str) -> Option<AgentRefusal> {
        self.rest.note_fix(a, b, c, d, e)
    }
    fn set_setting(&self, name: &str, value: f64, rationale: &str, confirm: bool) -> Option<AgentRefusal> {
        self.writer.set_setting(name, value, rationale, confirm)
    }
    fn set_table_cell(&self, t: &str, c: i32, r: i32, v: f64, ra: &str, d: bool) -> AgentCellWrite {
        self.writer.set_table_cell(t, c, r, v, ra, d)
    }
    fn tune_full(&self) -> AgentTuneFull {
        self.writer.tune_full()
    }
    fn log_full(&self, seconds: f64, decimate: i32) -> AgentLogFull {
        self.rest.log_full(seconds, decimate)
    }
    fn context(&self) -> AgentContext {
        self.rest.context()
    }
    fn propose_tune(&self, s: &[ProposedSetting], c: &[ProposedCell]) -> TuneProposalResult {
        self.writer.propose_tune(s, c)
    }
    fn apply_tune(&self, s: &[ProposedSetting], c: &[ProposedCell], note: &str, d: bool) -> TuneApplyResult {
        self.writer.apply_tune(s, c, note, d)
    }
    fn stage_tune(&self, s: &[ProposedSetting], c: &[ProposedCell], filename: &str) -> AgentStageResult {
        self.writer.stage_tune(s, c, filename)
    }
    fn stage_table(&self, name: &str, filename: &str) -> AgentStageResult {
        self.writer.stage_table(name, filename)
    }
    fn list_staged(&self) -> Vec<AgentStagedFile> {
        self.writer.list_staged()
    }
    fn definition_needed(&self) -> Option<AgentDefinitionNeed> {
        self.rest.definition_needed()
    }
    fn import_definition(&self, p: &str, c: &str, s: &str, n: &str) -> AgentDefinitionImported {
        self.writer.import_definition(p, c, s, n, &[])
    }
}
