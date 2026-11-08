//! Ported from `AgentApi.cs`. Owned by package `w5-agent`.
//!
//! Everything the agent API is allowed to ask the application for: the shapes it answers with
//! and [`AgentBridge`], the one trait that is the whole of what an agent can reach.
//!
//! **Reading is always allowed; writing is not.** The write members return a refusal unless
//! somebody has armed writing in the application, and there is deliberately no burn: a burn is
//! permanent, and the one thing on the far side of this interface is a person who can see what
//! is about to happen. An agent may move a number in the controller's working memory, which the
//! key turning off undoes; making that survive is a decision for whoever is standing next to the
//! engine. `nothing_reachable_from_the_bridge_names_a_controller_command_or_a_burn` (in the
//! tests) reads this file and fails if a method of that kind is ever added.
//!
//! Each type has a `to_json` method that writes it as the C# serialises the same record
//! (camelCase, declaration order, nulls left out).

use std::time::SystemTime;

use olv_core::model::TuneTable;
use olv_core::tune::tune_compare::TuneDifference;

use super::json::J;

/// One channel, as an agent needs to know it before asking for values.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentChannel {
    pub name: String,
    pub units: String,
    pub digits: i32,
    /// The job it does, where one was recognised: "EngineSpeed", "Mixture".
    pub role: String,
}

impl AgentChannel {
    pub fn new(name: &str, units: &str, digits: i32) -> Self {
        Self {
            name: name.to_string(),
            units: units.to_string(),
            digits,
            role: String::new(),
        }
    }

    pub fn with_role(mut self, role: &str) -> Self {
        self.role = role.to_string();
        self
    }

    pub fn to_json(&self) -> J {
        J::obj([
            ("name", J::s(&self.name)),
            ("units", J::s(&self.units)),
            ("digits", J::Int(i64::from(self.digits))),
            ("role", J::s(&self.role)),
        ])
    }
}

/// What the application is doing right now.
#[derive(Clone, Debug, PartialEq, Default)]
pub struct AgentState {
    /// "live", "log" or "idle".
    pub mode: String,
    /// The firmware signature, on a live session.
    pub signature: String,
    /// The log's file name, when one is open.
    pub file: String,
    pub samples: i32,
    pub seconds: f64,
    /// Polls a second, on a live session.
    pub rate: f64,
    pub channels: i32,
    /// Whether a tune has been read off a controller.
    pub has_tune: bool,
    /// Whether writing is armed. False is the resting state and the answer after every
    /// disconnect.
    pub writes_armed: bool,
    pub error: String,
}

impl AgentState {
    pub fn to_json(&self) -> J {
        J::obj([
            ("mode", J::s(&self.mode)),
            ("signature", J::s(&self.signature)),
            ("file", J::s(&self.file)),
            ("samples", J::Int(i64::from(self.samples))),
            ("seconds", J::Num(self.seconds)),
            ("rate", J::Num(self.rate)),
            ("channels", J::Int(i64::from(self.channels))),
            ("hasTune", J::Bool(self.has_tune)),
            ("writesArmed", J::Bool(self.writes_armed)),
            ("error", J::s(&self.error)),
        ])
    }
}

/// A refusal, said in a way an agent can act on rather than guess at.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentRefusal {
    pub reason: String,
    pub detail: String,
}

impl AgentRefusal {
    pub fn new(reason: impl Into<String>, detail: impl Into<String>) -> Self {
        Self {
            reason: reason.into(),
            detail: detail.into(),
        }
    }

    /// A refusal with no detail.
    pub fn reason(reason: impl Into<String>) -> Self {
        Self::new(reason, "")
    }
}

/// What a table-cell write actually did.
///
/// Found live, on a real Speeduino: a cell out of the firmware's declared range is clamped
/// rather than refused, and an early version of this route reported the request back as though
/// it had landed unchanged. `value` is read straight from the edit that was just encoded and
/// sent, not from anything UI-bound.
///
/// `warning` is set when the write went through but is worth a second look: currently only a
/// MaxxECU table whose cells share bytes with a named setting on this ECU.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentCellWrite {
    pub refusal: Option<AgentRefusal>,
    pub value: f64,
    pub warning: Option<String>,
}

impl AgentCellWrite {
    pub fn refused(refusal: AgentRefusal) -> Self {
        Self {
            refusal: Some(refusal),
            value: 0.0,
            warning: None,
        }
    }

    pub fn landed(value: f64, warning: Option<String>) -> Self {
        Self {
            refusal: None,
            value,
            warning,
        }
    }
}

/// The operating limits a write can run into, stated up front rather than learned by being
/// refused.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentLimits {
    /// Writes allowed inside `write_rate_window_seconds` before the next one is refused.
    pub write_rate_count: i32,
    pub write_rate_window_seconds: f64,
    /// A single write cannot move a setting by more than this fraction of its declared range.
    /// Refused rather than clamped, so a caller finds out rather than silently landing on the
    /// boundary.
    pub max_change_fraction_of_range: f64,
    /// True for every write this API can make. There is no route that burns.
    pub burns: bool,
    /// True: arming clears on every disconnect and is never persisted.
    pub writes_armed_clears_on_disconnect: bool,
    /// True: a setting `DangerousConstants` recognises is refused without `confirmDangerous:true`
    /// on the same call.
    pub dangerous_settings_need_confirmation: bool,
}

impl AgentLimits {
    /// The C#'s defaults for the two flags that are always true.
    pub fn new(count: i32, window_seconds: f64, fraction: f64) -> Self {
        Self {
            write_rate_count: count,
            write_rate_window_seconds: window_seconds,
            max_change_fraction_of_range: fraction,
            burns: false,
            writes_armed_clears_on_disconnect: true,
            dangerous_settings_need_confirmation: true,
        }
    }

    pub fn to_json(&self) -> J {
        J::obj([
            ("writeRateCount", J::Int(i64::from(self.write_rate_count))),
            ("writeRateWindowSeconds", J::Num(self.write_rate_window_seconds)),
            ("maxChangeFractionOfRange", J::Num(self.max_change_fraction_of_range)),
            ("burns", J::Bool(self.burns)),
            (
                "writesArmedClearsOnDisconnect",
                J::Bool(self.writes_armed_clears_on_disconnect),
            ),
            (
                "dangerousSettingsNeedConfirmation",
                J::Bool(self.dangerous_settings_need_confirmation),
            ),
        ])
    }
}

/// One entry from the wire trace, flattened for a reader that is not a window.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentWireEvent {
    pub sequence: i64,
    pub at: SystemTime,
    pub context: String,
    pub origin: String,
    pub attempt: i32,
    pub elapsed_ms: f64,
    pub outcome: String,
    pub failure_kind: String,
    pub detail: String,
}

impl AgentWireEvent {
    pub fn to_json(&self) -> J {
        J::obj([
            ("sequence", J::Int(self.sequence)),
            ("at", J::time(self.at)),
            ("context", J::s(&self.context)),
            ("origin", J::s(&self.origin)),
            ("attempt", J::Int(i64::from(self.attempt))),
            ("elapsedMs", J::Num(self.elapsed_ms)),
            ("outcome", J::s(&self.outcome)),
            ("failureKind", J::s(&self.failure_kind)),
            ("detail", J::s(&self.detail)),
        ])
    }
}

/// A rollup of recent wire activity, for "is the link healthy" at a glance.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentWireHealth {
    pub sampled: i32,
    pub failures: i32,
    pub success_rate: f64,
    /// By failure kind, in the order each kind was first met.
    pub failures_by_kind: Vec<(String, i32)>,
    pub since_last_success_seconds: Option<f64>,
    pub longest_recent_ms: Option<f64>,
}

impl AgentWireHealth {
    pub fn to_json(&self) -> J {
        J::obj([
            ("sampled", J::Int(i64::from(self.sampled))),
            ("failures", J::Int(i64::from(self.failures))),
            ("successRate", J::Num(self.success_rate)),
            (
                "failuresByKind",
                J::Obj(
                    self.failures_by_kind
                        .iter()
                        .map(|(k, n)| (k.clone(), J::Int(i64::from(*n))))
                        .collect(),
                ),
            ),
            ("sinceLastSuccessSeconds", J::opt_num(self.since_last_success_seconds)),
            ("longestRecentMs", J::opt_num(self.longest_recent_ms)),
        ])
    }
}

/// A definition already on this machine that nearly, but does not, match.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentNearMiss {
    pub name: String,
    pub signature: String,
    pub path: String,
}

/// What the application needs before it can decode a connected ECU.
///
/// An empty `sources` is an instruction, not a gap: MegaSquirt's definitions are licensed in a way
/// that does not permit fetching them, and `guidance` says where a person should look instead.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentDefinitionNeed {
    pub identity: Vec<String>,
    pub family: String,
    pub signature: String,
    pub version: String,
    pub filename: String,
    pub nearby: Vec<AgentNearMiss>,
    pub folder: String,
    pub where_: String,
    pub sources: Vec<String>,
    pub guidance: String,
}

impl AgentDefinitionNeed {
    pub fn to_json(&self) -> J {
        J::obj([
            ("identity", J::strs(&self.identity)),
            ("family", J::s(&self.family)),
            ("signature", J::s(&self.signature)),
            ("version", J::s(&self.version)),
            ("filename", J::s(&self.filename)),
            (
                "nearby",
                J::Arr(
                    self.nearby
                        .iter()
                        .map(|n| {
                            J::obj([
                                ("name", J::s(&n.name)),
                                ("signature", J::s(&n.signature)),
                                ("path", J::s(&n.path)),
                            ])
                        })
                        .collect(),
                ),
            ),
            ("folder", J::s(&self.folder)),
            ("where", J::s(&self.where_)),
            ("sources", J::strs(&self.sources)),
            ("guidance", J::s(&self.guidance)),
        ])
    }
}

/// What keeping a definition did, or why it was refused.
#[derive(Clone, Debug, PartialEq, Eq, Default)]
pub struct AgentDefinitionImported {
    pub problem: String,
    pub path: String,
    pub name: String,
    pub signature: String,
    pub source: String,
}

impl AgentDefinitionImported {
    pub fn accepted(&self) -> bool {
        self.problem.is_empty()
    }
}

/// One setting, with the metadata worth knowing before writing back to it.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentSetting {
    pub name: String,
    pub value: f64,
    pub units: String,
    /// What each value means, in order from zero. Empty where the firmware named none.
    pub options: Vec<String>,
    /// The firmware's own usable range, in its displayed units. None where it states none.
    pub low: Option<f64>,
    pub high: Option<f64>,
}

impl AgentSetting {
    pub fn to_json(&self) -> J {
        J::obj([
            ("name", J::s(&self.name)),
            ("value", J::Num(self.value)),
            ("units", J::s(&self.units)),
            ("options", J::strs(&self.options)),
            ("low", J::opt_num(self.low)),
            ("high", J::opt_num(self.high)),
        ])
    }
}

/// One table's cells and axes, flattened the same way a single `/table` answer is.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentTable {
    pub name: String,
    pub units: String,
    pub columns: i32,
    pub rows: i32,
    pub x_bins: Vec<f64>,
    pub y_bins: Vec<f64>,
    pub x_units: String,
    pub y_units: String,
    pub x_constant: String,
    pub y_constant: String,
    /// Row-major, so `values[row][column]` reads the way the grid looks.
    pub values: Vec<Vec<f64>>,
}

impl AgentTable {
    /// A table as numbers rather than as an object graph: the same flattening `/table` and
    /// `/tune/full` both use, so the two never drift apart.
    pub fn flatten(table: &TuneTable) -> AgentTable {
        let columns = table.columns();
        let rows = table.rows();
        let values = (0..rows)
            .map(|r| (0..columns).map(|c| table.values[(c, r)]).collect())
            .collect();

        AgentTable {
            name: table.name.clone(),
            units: table.units.clone(),
            columns: columns as i32,
            rows: rows as i32,
            x_bins: table.x.breakpoints.clone(),
            y_bins: table.y.breakpoints.clone(),
            x_units: table.x.units.clone(),
            y_units: table.y.units.clone(),
            x_constant: table.x.constant.clone(),
            y_constant: table.y.constant.clone(),
            values,
        }
    }

    pub fn to_json(&self) -> J {
        J::obj([
            ("name", J::s(&self.name)),
            ("units", J::s(&self.units)),
            ("columns", J::Int(i64::from(self.columns))),
            ("rows", J::Int(i64::from(self.rows))),
            ("xBins", J::nums(&self.x_bins)),
            ("yBins", J::nums(&self.y_bins)),
            ("xUnits", J::s(&self.x_units)),
            ("yUnits", J::s(&self.y_units)),
            ("xConstant", J::s(&self.x_constant)),
            ("yConstant", J::s(&self.y_constant)),
            ("values", J::Arr(self.values.iter().map(|r| J::nums(r)).collect())),
        ])
    }
}

/// The whole tune: every setting and every table, in one payload.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentTuneFull {
    pub settings: Vec<AgentSetting>,
    pub tables: Vec<AgentTable>,
}

impl AgentTuneFull {
    pub fn to_json(&self) -> J {
        J::obj([
            (
                "settings",
                J::Arr(self.settings.iter().map(AgentSetting::to_json).collect()),
            ),
            ("tables", J::Arr(self.tables.iter().map(AgentTable::to_json).collect())),
        ])
    }
}

/// One channel's samples, sharing the time column every other channel in the same answer does.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentChannelSamples {
    pub name: String,
    pub units: String,
    pub values: Vec<f64>,
}

/// Every channel of the log in hand, in one call.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentLogFull {
    pub times: Vec<f64>,
    pub channels: Vec<AgentChannelSamples>,
}

impl AgentLogFull {
    pub fn to_json(&self) -> J {
        J::obj([
            ("times", J::nums(&self.times)),
            (
                "channels",
                J::Arr(
                    self.channels
                        .iter()
                        .map(|c| {
                            J::obj([
                                ("name", J::s(&c.name)),
                                ("units", J::s(&c.units)),
                                ("values", J::nums(&c.values)),
                            ])
                        })
                        .collect(),
                ),
            ),
        ])
    }
}

/// How big the tune in hand is, without paying for the whole of it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentTuneSummary {
    pub setting_count: i32,
    pub tables: Vec<String>,
}

/// One insight, flattened for a reader that is not a window.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentFinding {
    pub level: String,
    pub topic: String,
    pub title: String,
    pub detail: String,
    pub evidence: String,
}

impl AgentFinding {
    pub fn new(level: &str, topic: &str, title: &str, detail: &str) -> Self {
        Self {
            level: level.to_string(),
            topic: topic.to_string(),
            title: title.to_string(),
            detail: detail.to_string(),
            evidence: String::new(),
        }
    }

    pub fn with_evidence(mut self, evidence: &str) -> Self {
        self.evidence = evidence.to_string();
        self
    }

    pub fn to_json(&self) -> J {
        J::obj([
            ("level", J::s(&self.level)),
            ("topic", J::s(&self.topic)),
            ("title", J::s(&self.title)),
            ("detail", J::s(&self.detail)),
            ("evidence", J::s(&self.evidence)),
        ])
    }
}

/// The "read this first" bundle for a session that has just started.
#[derive(Clone, Debug, PartialEq)]
pub struct AgentContext {
    pub project_brief: String,
    pub state: AgentState,
    pub tune: AgentTuneSummary,
    pub insights: Vec<AgentFinding>,
    pub wire_health: AgentWireHealth,
}

impl AgentContext {
    pub fn to_json(&self) -> J {
        J::obj([
            ("projectBrief", J::s(&self.project_brief)),
            ("state", self.state.to_json()),
            (
                "tune",
                J::obj([
                    ("settingCount", J::Int(i64::from(self.tune.setting_count))),
                    ("tables", J::strs(&self.tune.tables)),
                ]),
            ),
            (
                "insights",
                J::Arr(self.insights.iter().map(AgentFinding::to_json).collect()),
            ),
            ("wireHealth", self.wire_health.to_json()),
        ])
    }
}

/// One setting proposed to change, before anything is decided about it.
#[derive(Clone, Debug, PartialEq)]
pub struct ProposedSetting {
    pub name: String,
    pub value: f64,
}

/// One table cell proposed to change.
#[derive(Clone, Debug, PartialEq)]
pub struct ProposedCell {
    pub table: String,
    pub column: i32,
    pub row: i32,
    pub value: f64,
}

/// What a batch of proposed settings and cells would do, worked out without touching the tune
/// the application is looking at or the ECU behind it.
///
/// `rejected` are requested changes that could not go in: a name this firmware has no constant
/// for, a value out of its range, a cell outside a table's shape. Left out of `changes` rather
/// than silently dropped, so a batch that only partly makes sense says which part.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneProposalResult {
    pub changes: Vec<TuneDifference>,
    pub rejected: Vec<String>,
    pub summary: String,
}

impl TuneProposalResult {
    /// Nothing in the batch would change anything.
    pub fn is_empty(&self) -> bool {
        self.changes.is_empty()
    }
}

/// What applying a proposal did, or why it was refused before anything was attempted.
/// `refusal` is set exactly when nothing was sent.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneApplyResult {
    pub refusal: Option<AgentRefusal>,
    /// What was actually sent and taken, in the same shape a proposal reports.
    pub applied: Vec<TuneDifference>,
    /// Requested changes that did not go in, whether caught before sending or refused by the
    /// ECU itself.
    pub rejected: Vec<String>,
    pub summary: String,
}

impl TuneApplyResult {
    pub fn refused(refusal: AgentRefusal) -> Self {
        Self {
            refusal: Some(refusal),
            applied: Vec::new(),
            rejected: Vec::new(),
            summary: String::new(),
        }
    }
}

/// One file sitting in the staging folder.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentStagedFile {
    pub name: String,
    pub path: String,
    pub bytes: i64,
    pub written_at: SystemTime,
}

impl AgentStagedFile {
    pub fn to_json(&self) -> J {
        J::obj([
            ("name", J::s(&self.name)),
            ("path", J::s(&self.path)),
            ("bytes", J::Int(self.bytes)),
            ("writtenAt", J::time(self.written_at)),
        ])
    }
}

/// What staging a file did, or why there was nothing to stage.
///
/// Reuses the shape of a write refusal even though staging is never gated the way a write is:
/// the two situations both come down to "here is why nothing was written".
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentStageResult {
    pub refusal: Option<AgentRefusal>,
    pub file: Option<AgentStagedFile>,
}

impl AgentStageResult {
    pub fn refused(reason: &str, detail: &str) -> Self {
        Self {
            refusal: Some(AgentRefusal::new(reason, detail)),
            file: None,
        }
    }

    pub fn staged(file: AgentStagedFile) -> Self {
        Self {
            refusal: None,
            file: Some(file),
        }
    }
}

/// One changed setting or table, flattened the way a table already is.
pub fn describe_difference(d: &TuneDifference) -> J {
    J::obj([
        ("name", J::s(&d.name)),
        ("units", J::s(&d.constant.units)),
        ("cells", J::Int(i64::from(d.cells))),
        (
            "before",
            match (&d.theirs_text, d.theirs) {
                (Some(text), _) => J::s(text),
                (None, theirs) => J::opt_num(theirs),
            },
        ),
        (
            "after",
            match (&d.mine_text, d.mine) {
                (Some(text), _) => J::s(text),
                (None, mine) => J::opt_num(mine),
            },
        ),
        ("summary", J::s(d.summary())),
    ])
}

/// Everything the agent API is allowed to ask the application for.
///
/// A trait rather than a reference to the view model, so the server can be built and tested
/// without a window, and so that the whole of what an agent can reach is one list. Anything not
/// on here is not reachable, which is a property worth being able to check by reading rather
/// than by tracing calls.
///
/// Implementations are called from the server's connection threads, several at once.
pub trait AgentBridge: Send + Sync {
    /// What is loaded or connected, and whether writing is armed.
    fn state(&self) -> AgentState;

    /// The write-path limits: rate, per-write magnitude, and the rest of it.
    fn limits(&self) -> AgentLimits;

    /// Every channel available, live or from the log in hand. `raw` asks for every channel a
    /// connected ECU's firmware decodes, whether or not its own datalog definition names it.
    fn channels(&self, raw: bool) -> Vec<AgentChannel>;

    /// What went over the wire recently, and how healthy the link looks.
    fn wire_health(&self) -> AgentWireHealth;

    /// The most recent wire events, newest first.
    fn wire_events(&self, count: i32) -> Vec<AgentWireEvent>;

    /// The samples of one channel, newest last. `seconds` of zero means all of them.
    fn values(&self, channel: &str, seconds: f64) -> Vec<f64>;

    /// The time column matching [`values`](Self::values).
    fn times(&self, seconds: f64) -> Vec<f64>;

    /// The findings the Insights window shows, as text.
    fn insights(&self) -> Vec<AgentFinding>;

    /// Every setting in the tune, in order, or nothing when none has been read.
    fn tune_values(&self) -> Vec<(String, f64)>;

    /// One table by name, with its axes.
    fn table(&self, name: &str) -> Option<TuneTable>;

    /// The names of every table the firmware declares.
    fn table_names(&self) -> Vec<String>;

    // ----- the project ------------------------------------------------------------------

    /// The vehicle's project as prose, or an empty string when none is open.
    fn project_brief(&self) -> String;

    /// Every vehicle that has a project, whether or not one is open.
    fn projects(&self) -> Vec<String>;

    /// Keeps the tune in hand as a version of the open project. Not a write to the ECU and not
    /// gated like one: this records what the controller already holds.
    fn keep_tune(&self, note: &str) -> Option<AgentRefusal>;

    /// What changed between two versions, setting by setting, or a message naming which of them
    /// could not be read.
    fn compare_versions(&self, from: &str, to: &str) -> String;

    /// Records the log in hand as a sitting.
    fn record_sitting(&self, note: &str) -> Option<AgentRefusal>;

    /// Adds a fix, or moves one already there. Deliberately not a write to the ECU: this changes
    /// the record of what is being worked on, which is safe.
    fn note_fix(&self, id: &str, title: &str, detail: &str, state: &str, change: &str) -> Option<AgentRefusal>;

    // ----- the guarded half --------------------------------------------------------------

    /// Puts one setting into the controller's working memory. `rationale` is required;
    /// `confirm_dangerous` has to be true for a rev limiter, a launch RPM, a boost or fuel/ignition
    /// cut. Returns None when it was done, or a refusal saying why not. Nothing here burns.
    fn set_setting(&self, name: &str, value: f64, rationale: &str, confirm_dangerous: bool) -> Option<AgentRefusal>;

    /// Puts one cell of one table into the controller's working memory. A cell out of the
    /// firmware's declared range is clamped into it rather than refused, so the value that lands
    /// is part of what this returns.
    fn set_table_cell(
        &self,
        table: &str,
        column: i32,
        row: i32,
        value: f64,
        rationale: &str,
        confirm_dangerous: bool,
    ) -> AgentCellWrite;

    // ----- composition ---------------------------------------------------------------------

    /// Every scalar and every table in one payload.
    fn tune_full(&self) -> AgentTuneFull;

    /// Every channel's samples in one call, windowed by `seconds` as `values` is, and then
    /// thinned by `decimate` (one sample in every that many; one for zero or one).
    fn log_full(&self, seconds: f64, decimate: i32) -> AgentLogFull;

    /// One call bundling what is worth reading before anything else.
    fn context(&self) -> AgentContext;

    // ----- propose / apply -------------------------------------------------------------------

    /// Works out what a batch would change, on a private copy of the tune in hand.
    fn propose_tune(&self, settings: &[ProposedSetting], cells: &[ProposedCell]) -> TuneProposalResult;

    /// Applies the same shape of batch for real, through the per-setting and per-cell write
    /// paths: no second write implementation.
    fn apply_tune(
        &self,
        settings: &[ProposedSetting],
        cells: &[ProposedCell],
        note: &str,
        confirm_dangerous: bool,
    ) -> TuneApplyResult;

    // ----- staging --------------------------------------------------------------------------

    /// Writes a tune out as a real `.msq` in the staging folder. Reaches no controller.
    fn stage_tune(&self, settings: &[ProposedSetting], cells: &[ProposedCell], filename: &str) -> AgentStageResult;

    /// Writes one named table out as a CSV a person can import in TunerStudio.
    fn stage_table(&self, name: &str, filename: &str) -> AgentStageResult;

    /// What is sitting in the staging folder right now.
    fn list_staged(&self) -> Vec<AgentStagedFile>;

    // ----- firmware definitions -----------------------------------------------------------------

    /// What the last connection attempt needed and could not find, or None.
    fn definition_needed(&self) -> Option<AgentDefinitionNeed>;

    /// Keeps a definition, having checked it is one and that it declares the signature this ECU
    /// reported.
    fn import_definition(&self, path: &str, content: &str, source: &str, name: &str) -> AgentDefinitionImported;
}
