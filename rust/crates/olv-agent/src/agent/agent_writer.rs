//! The agent's way to change an ECU, and every gate in front of it.
//!
//! Ported from the write half of `AgentCommands.cs` (`AgentSetSetting`, `AgentSetTableCell`,
//! `AgentApplyTune`, `GeneralRefusal` and the guardrails around them) and the two write paths it
//! calls in `MainViewModel` (`WriteSettingsToEcu`, `WriteTableToEcu` and their MaxxECU forms).
//! In the C# these live on the view model, which is not ported yet; they are here, in the
//! security layer, because `EcuConnection::write_tune_page` has no gate of its own and **no
//! agent request may reach it, or the MaxxECU's write command, except through this type**.
//!
//! # The gates, in the order a write meets them
//!
//! 1. **A rationale.** Blank is refused before anything else is even checked, so the guard
//!    cannot be routed around by leaving it empty.
//! 2. **Armed.** Writing is off at start-up, off again on every disconnect, never persisted, and
//!    can only be turned on by [`AgentWriter::set_armed`], which no HTTP route calls. A person
//!    arms it; nothing an agent sends can.
//! 3. **Connected, a real tune, read off the controller.** A placeholder (a definition file's
//!    all-noughts tune) and a tune opened from a file are both refused.
//! 4. **Not running above idle.** Live RPM over 1,200 refuses every write; no RPM to read does
//!    not.
//! 5. **Not too fast.** Ten writes in five seconds; a whole batch counts as one.
//! 6. **Names that exist.** A setting the firmware does not declare, a table that is not there.
//! 7. **Dangerous constants.** A rev limiter, launch RPM, boost limit, or fuel or ignition cut
//!    needs `confirmDangerous` on the same call.
//! 8. **Not too large.** No single change may move a value by more than half of the range the
//!    firmware declares for it.
//!
//! Only then do bytes go out, and they go through the connection's read-back-and-verify. Nothing
//! here burns, and nothing here can: the type has no method that calls `burn_page`.
//!
//! Everything runs under one lock, so two requests arriving together cannot interleave their
//! edits or race the rate limit (the C# guards only the rate list).

use std::collections::BTreeSet;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant, SystemTime};

use olv_core::analysis::csv_export;
use olv_core::base::safe_folder::SafeFolderError;
use olv_core::definitions::dangerous_constants;
use olv_core::fmt as nf;
use olv_core::ini::table_editor_reader::TableDefinition;
use olv_core::ini::tune_layout::{TuneConstant, TunePage};
use olv_core::model::{TuneTable, WireOrigin, eq_ignore_case};
use olv_core::tune::ecu_tune::{EcuTune, TuneWrite};
use olv_core::tune::msq_writer;
use olv_core::tune::tune_compare::{self, TuneDifference};
use olv_core::tune::tune_edit::{TuneEdit, TuneSelection};
use olv_core::tune::tune_settings_edit::TuneSettingsEdit;
use olv_live::connection::ecu_connection::EcuConnection;
use olv_live::live::maxx_tune::{self, MaxxWriteStatus};
use olv_live::live::maxx_tune_definitions::MaxxSettingDefinition;
use olv_live::live::maxx_usb_source::MaxxUsbSource;
use olv_store::store::workspace::Workspace;

use super::agent_api::{
    AgentCellWrite, AgentLimits, AgentRefusal, AgentSetting, AgentStageResult, AgentStagedFile, AgentTable,
    AgentTuneFull, ProposedCell, ProposedSetting, TuneApplyResult, TuneProposalResult,
};
use super::agent_staging::AgentStaging;

/// How far above idle the engine is allowed to be for an agent write to go through at all.
///
/// A running, warmed engine idles somewhere under a thousand rpm on virtually anything this
/// application talks to; twelve hundred leaves headroom for a fast idle or a cold start without
/// allowing any real throttle input through, since even a light prod of the pedal moves RPM well
/// past that within a second. The point is not to pick the exact edge of "idling": it is to
/// refuse the moment the car might be moving or under load, which is when a change lands
/// somewhere the agent cannot see the consequence of before it happens.
pub const IDLE_ADJACENT_RPM: f64 = 1200.0;

/// Ten writes in five seconds is far beyond any sequence of settings a person reviewing each
/// change could keep up with, and far below what a loop gone wrong (a retried failure, a script
/// iterating every cell of a table with no pacing) produces in the same span. It exists to catch
/// a runaway agent, not to pace a careful one.
pub const RATE_LIMIT_COUNT: usize = 10;
pub const RATE_LIMIT_WINDOW: Duration = Duration::from_secs(5);

/// A single change may not exceed this fraction of the range the firmware declares.
///
/// Half, rather than any smaller fraction, because a real tuning step sometimes legitimately is
/// large, and a tight cap would just teach an agent to split every honest edit into several
/// calls, which hides the total from this check rather than catching anything. What fifty per
/// cent of a declared range does catch is a value entered in the wrong units, a misplaced
/// decimal point, or an off-by-a-decade mistake.
pub const MAGNITUDE_FRACTION: f64 = 0.5;

/// What the application tells the writer that only it knows.
pub trait WriterHost: Send + Sync {
    /// The engine speed of the live session, or None when no session is running or it has not
    /// sampled one yet. (A session with no way to answer is "this cannot be answered", not "the
    /// engine is running".)
    fn live_rpm(&self) -> Option<f64>;

    /// Keeps the tune now in hand as a version of the open project, best effort: a successful
    /// write to the ECU must never be reported as failed because there was nowhere to record it.
    fn keep_tune(&self, note: &str);

    /// The time for a default staged file name, as `yyyy-MM-dd_HH-mm-ss`. The C# used the local
    /// clock; the standard library cannot say what zone that is, so the application (which knows)
    /// should override this. The default is UTC, which names the file just as uniquely.
    fn file_stamp(&self) -> String {
        let now = time::OffsetDateTime::now_utc();

        format!(
            "{:04}-{:02}-{:02}_{:02}-{:02}-{:02}",
            now.year(),
            u8::from(now.month()),
            now.day(),
            now.hour(),
            now.minute(),
            now.second()
        )
    }
}

/// Where the bytes go. A MaxxECU over USB has no page protocol at all, so it is not an
/// `EcuConnection`; either way this is the only place an agent write is sent from.
#[derive(Clone)]
pub enum EcuTarget {
    Pages(Arc<EcuConnection>),
    Maxx(Arc<MaxxUsbSource>),
}

/// The tune the application holds and where it came from.
pub struct TuneInHand {
    pub tune: EcuTune,
    /// What the firmware declares as tables, for names and shapes.
    pub definitions: Vec<TableDefinition>,
    /// The firmware's signature, written into a staged `.msq`.
    pub signature: String,
    /// All noughts, from a definition file rather than off the controller.
    pub placeholder: bool,
    /// Opened from a saved file rather than read off the controller.
    pub from_file: bool,
}

struct Inner {
    tune: Option<TuneInHand>,
    settings_edit: Option<TuneSettingsEdit>,
    target: Option<EcuTarget>,
    maxx_definitions: Vec<MaxxSettingDefinition>,
    settings_pages_written: BTreeSet<i32>,
    recent_writes: Vec<Instant>,
    batches_in_flight: u32,
}

pub struct AgentWriter {
    armed: AtomicBool,
    inner: Mutex<Inner>,
    workspace: Workspace,
    host: Arc<dyn WriterHost>,
    window: Duration,
}

impl AgentWriter {
    pub fn new(workspace: Workspace, host: Arc<dyn WriterHost>) -> Self {
        Self::with_window_for_tests(workspace, host, RATE_LIMIT_WINDOW)
    }

    /// A writer with a different rate-limit window, for a test that has to see it expire.
    #[doc(hidden)]
    pub fn with_window_for_tests(workspace: Workspace, host: Arc<dyn WriterHost>, window: Duration) -> Self {
        Self {
            armed: AtomicBool::new(false),
            inner: Mutex::new(Inner {
                tune: None,
                settings_edit: None,
                target: None,
                maxx_definitions: Vec::new(),
                settings_pages_written: BTreeSet::new(),
                recent_writes: Vec::new(),
                batches_in_flight: 0,
            }),
            workspace,
            host,
            window,
        }
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|poisoned| {
            let mut inner = poisoned.into_inner();
            // A panic part-way through a batch must not leave the per-item rate limit stood
            // down for good.
            inner.batches_in_flight = 0;
            inner
        })
    }

    // ----- arming ------------------------------------------------------------------------

    /// Whether writing is allowed at this moment. False on start-up and after every disconnect.
    pub fn is_armed(&self) -> bool {
        self.armed.load(Ordering::SeqCst)
    }

    /// The person at the machine turning writing on or off. **Not reachable from the agent
    /// API**: no route, no bridge method and no message calls it.
    pub fn set_armed(&self, armed: bool) {
        self.armed.store(armed, Ordering::SeqCst);
    }

    /// A controller is connected: the only place bytes can go. Does not arm anything.
    pub fn connect(&self, target: EcuTarget) {
        self.lock().target = Some(target);
    }

    /// The controller is gone. Disarms: a permission granted for the bench engine must not still
    /// be granted when the next thing plugged in is a car.
    pub fn disconnect(&self) {
        self.set_armed(false);

        let mut inner = self.lock();
        inner.target = None;
        inner.settings_pages_written.clear();
    }

    /// The tune now in hand (or none). A new tune starts a new set of pending edits.
    pub fn set_tune(&self, tune: Option<TuneInHand>) {
        let mut inner = self.lock();
        inner.tune = tune;
        inner.settings_edit = None;
    }

    /// The setting definitions MTune declares, for the overlap warning on a MaxxECU table write.
    pub fn set_maxx_definitions(&self, definitions: Vec<MaxxSettingDefinition>) {
        self.lock().maxx_definitions = definitions;
    }

    /// The pages a settings write has touched and nothing has burned, as the C# keeps them for
    /// the Burn button. (The connection tracks unburned pages itself too.)
    pub fn settings_pages_written(&self) -> Vec<i32> {
        self.lock().settings_pages_written.iter().copied().collect()
    }

    /// Whether a settings edit is holding changes nobody has sent. A refused write must leave
    /// nothing behind for the next call to accidentally send.
    pub fn has_setting_changes(&self) -> bool {
        self.lock()
            .settings_edit
            .as_ref()
            .is_some_and(TuneSettingsEdit::has_changes)
    }

    /// Runs a closure over the tune in hand; the app's own screens read it this way.
    pub fn with_tune<R>(&self, read: impl FnOnce(Option<&TuneInHand>) -> R) -> R {
        read(self.lock().tune.as_ref())
    }

    // ----- what an agent may read ----------------------------------------------------------

    /// The tune as an agent reads it, or nothing when none was read.
    pub fn tune_values(&self) -> Vec<(String, f64)> {
        let inner = self.lock();

        match &inner.tune {
            Some(held) if !held.placeholder => held.tune.scalars().iter().map(|(k, v)| (k.to_string(), *v)).collect(),
            _ => Vec::new(),
        }
    }

    fn constants_of(held: &TuneInHand) -> Vec<TuneConstant> {
        held.tune
            .layout()
            .constants
            .iter()
            .filter(|c| !c.is_array())
            .cloned()
            .collect()
    }

    /// Every table the firmware declares and this tune can produce.
    pub fn tables(&self) -> Vec<TuneTable> {
        let inner = self.lock();
        Self::tables_of(&inner)
    }

    fn tables_of(inner: &Inner) -> Vec<TuneTable> {
        match &inner.tune {
            Some(held) => held.tune.tables(&held.definitions),
            None => Vec::new(),
        }
    }

    pub fn table(&self, name: &str) -> Option<TuneTable> {
        self.tables().into_iter().find(|t| eq_ignore_case(&t.name, name))
    }

    pub fn table_names(&self) -> Vec<String> {
        self.tables().into_iter().map(|t| t.name).collect()
    }

    /// Every scalar and every table in one payload, each setting with the units, options and
    /// range the firmware declares for it.
    pub fn tune_full(&self) -> AgentTuneFull {
        let inner = self.lock();

        let Some(held) = inner.tune.as_ref().filter(|h| !h.placeholder) else {
            return AgentTuneFull {
                settings: Vec::new(),
                tables: Vec::new(),
            };
        };

        let constants = Self::constants_of(held);
        let settings = held
            .tune
            .scalars()
            .iter()
            .map(
                |(name, value)| match constants.iter().find(|c| eq_ignore_case(&c.name, name)) {
                    Some(constant) => AgentSetting {
                        name: name.to_string(),
                        value: *value,
                        units: constant.units.clone(),
                        options: constant.options.clone(),
                        low: constant.has_range().then_some(constant.low),
                        high: constant.has_range().then_some(constant.high),
                    },
                    None => AgentSetting {
                        name: name.to_string(),
                        value: *value,
                        units: String::new(),
                        options: Vec::new(),
                        low: None,
                        high: None,
                    },
                },
            )
            .collect();

        let tables = held
            .tune
            .tables(&held.definitions)
            .iter()
            .map(AgentTable::flatten)
            .collect();

        AgentTuneFull { settings, tables }
    }

    /// The write-path limits, for a caller to read once rather than learn by being refused.
    pub fn limits(&self) -> AgentLimits {
        AgentLimits::new(RATE_LIMIT_COUNT as i32, self.window.as_secs_f64(), MAGNITUDE_FRACTION)
    }

    // ----- the guarded half: one setting ---------------------------------------------------

    /// Sets one setting, through the same path and the same gates a dialog edit uses.
    ///
    /// `rationale` is required: a write carrying no stated reason is refused before anything else
    /// is even checked, so the guard cannot be routed around by leaving it blank.
    /// `confirm_dangerous` only matters when `name` matches a dangerous constant.
    pub fn set_setting(
        &self,
        name: &str,
        value: f64,
        rationale: &str,
        confirm_dangerous: bool,
    ) -> Option<AgentRefusal> {
        // Anything this reaches on the wire is tagged as the agent's: what tells "the AI asked for
        // this" apart from a person clicking a button, since the two look identical otherwise.
        let _origin = WireOrigin::enter(WireOrigin::Agent);

        let mut inner = self.lock();
        self.set_setting_locked(&mut inner, name, value, rationale, confirm_dangerous)
    }

    fn set_setting_locked(
        &self,
        inner: &mut Inner,
        name: &str,
        value: f64,
        rationale: &str,
        confirm_dangerous: bool,
    ) -> Option<AgentRefusal> {
        if rationale.trim().is_empty() {
            return Some(AgentRefusal::new(
                "no rationale given",
                "Say in one line why this change is being made. It is echoed back with the write.",
            ));
        }

        if let Some(refused) = self.general_refusal(inner) {
            return Some(refused);
        }

        let Some(held) = inner.tune.as_ref() else {
            return Some(AgentRefusal::reason("no tune has been read"));
        };

        let Some(constant) = held.tune.constant(name).cloned() else {
            return Some(AgentRefusal::new(
                "no such setting",
                format!("This firmware declares no \"{name}\"."),
            ));
        };

        if let Some(dangerous) = dangerous_refusal(held, name, confirm_dangerous) {
            return Some(dangerous);
        }

        let Some(tune_ref) = inner.tune.as_ref().map(|h| &h.tune) else {
            return Some(AgentRefusal::reason("no tune has been read"));
        };
        let edit = inner
            .settings_edit
            .get_or_insert_with(|| TuneSettingsEdit::new(tune_ref));

        let before = edit.original(tune_ref, name, 0);

        if !edit.set(tune_ref, name, value, 0) {
            return Some(AgentRefusal::new(
                "the value would not go in",
                "It is out of the range the firmware declares, or the setting is not a number.",
            ));
        }

        if let Some(too_large) = magnitude_refusal(before, value, constant.low, constant.high) {
            // Taken back out of the pending edit: a refused write must leave nothing behind for
            // the next call to accidentally send.
            edit.revert(tune_ref, name, 0);
            return Some(too_large);
        }

        self.record_write(inner);

        let said = self.write_settings_to_ecu(inner);

        if said.to_ascii_lowercase().starts_with("sent") {
            None
        } else {
            Some(AgentRefusal::new("the write did not go through", said))
        }
    }

    // ----- the guarded half: one cell ------------------------------------------------------

    /// Sets one cell of one table, through the same path as an edit on screen.
    pub fn set_table_cell(
        &self,
        table: &str,
        column: i32,
        row: i32,
        value: f64,
        rationale: &str,
        confirm_dangerous: bool,
    ) -> AgentCellWrite {
        let _origin = WireOrigin::enter(WireOrigin::Agent);

        let mut inner = self.lock();
        self.set_table_cell_locked(&mut inner, table, column, row, value, rationale, confirm_dangerous)
    }

    #[allow(clippy::too_many_arguments)]
    fn set_table_cell_locked(
        &self,
        inner: &mut Inner,
        name: &str,
        column: i32,
        row: i32,
        value: f64,
        rationale: &str,
        confirm_dangerous: bool,
    ) -> AgentCellWrite {
        if rationale.trim().is_empty() {
            return AgentCellWrite::refused(AgentRefusal::new(
                "no rationale given",
                "Say in one line why this change is being made. It is echoed back with the write.",
            ));
        }

        if let Some(refused) = self.general_refusal(inner) {
            return AgentCellWrite::refused(refused);
        }

        // By the name a person sees, which is the one the API hands out.
        let Some(table) = Self::tables_of(inner)
            .into_iter()
            .find(|t| eq_ignore_case(&t.name, name))
        else {
            return AgentCellWrite::refused(AgentRefusal::new("no such table", name));
        };

        let (columns, rows) = (table.columns() as i32, table.rows() as i32);
        if column < 0 || column >= columns || row < 0 || row >= rows {
            return AgentCellWrite::refused(AgentRefusal::new(
                "that cell is not in the table",
                format!("\"{}\" is {columns} by {rows}.", table.name),
            ));
        }

        let Some(held) = inner.tune.as_ref() else {
            return AgentCellWrite::refused(AgentRefusal::reason("no tune has been read"));
        };

        // The constant actually behind the grid, where the firmware names one. Dangerous-constant
        // matching and the magnitude limit are judged against that, not the table's display
        // title, since a title like "VE Table" carries none of the firmware's own spelling of
        // anything.
        let constant = constant_for(held, &table);

        if let Some(dangerous) = dangerous_refusal(
            held,
            constant.as_ref().map_or("", |c| c.name.as_str()),
            confirm_dangerous,
        ) {
            return AgentCellWrite::refused(dangerous);
        }

        let before = table.values[(column as usize, row as usize)];

        if let Some(constant) = &constant
            && let Some(too_large) = magnitude_refusal(before, value, constant.low, constant.high)
        {
            return AgentCellWrite::refused(too_large);
        }

        let mut edit = TuneEdit::new(table.clone(), constant);
        edit.set(TuneSelection::cell(column, row), value);

        self.record_write(inner);

        let (said, landed) = self.write_table_to_ecu(inner, &edit, column, row);

        if !said.to_ascii_lowercase().starts_with("sent") {
            return AgentCellWrite::refused(AgentRefusal::new("the write did not go through", said));
        }

        // A MaxxECU table write appends " Warning: ..." to its own success message rather than
        // carrying a separate field. Split out here so an agent sees it as its own thing rather
        // than buried in prose meant for a person reading a status line.
        let warning = said.find(" Warning:").map(|at| said[at + 1..].to_string());

        AgentCellWrite::landed(landed, warning)
    }

    // ----- the gates every write meets first --------------------------------------------------

    /// The reasons a write is refused before anything about which setting or cell it names is
    /// even looked at, in the order worth hearing them.
    ///
    /// Kept ahead of resolving a table or constant name on purpose: whether writing is even
    /// possible right now (armed, connected, a real tune, the engine not mid-drive, not a runaway
    /// burst of calls) is a cheaper and more general question than "does this name exist", and a
    /// person reading a refusal should hear "writes are not armed" before "no such setting".
    fn general_refusal(&self, inner: &mut Inner) -> Option<AgentRefusal> {
        if !self.is_armed() {
            return Some(AgentRefusal::new(
                "writes are not armed",
                "Tick \"Allow agent writes\" in the application. It clears itself on disconnect.",
            ));
        }

        // A MaxxECU over USB has no EcuConnection at all, so this is "neither is live" rather
        // than "the one that matters is null", or every MaxxECU write would be refused as
        // unconnected while it plainly is.
        if inner.target.is_none() {
            return Some(AgentRefusal::reason("not connected to an ECU"));
        }

        // With no tune at all neither flag is set, and the C# reaches "no tune has been read" a
        // step later, in each caller, after the idle and rate checks.
        if inner.tune.as_ref().is_some_and(|held| held.placeholder) {
            return Some(AgentRefusal::new(
                "the tune in hand is a placeholder",
                "It came from a definition file rather than off the controller, and is all noughts.",
            ));
        }

        if inner.tune.as_ref().is_some_and(|held| held.from_file) {
            return Some(AgentRefusal::new(
                "the tune in hand came from a file",
                "Read the ECU's own tune before writing to it.",
            ));
        }

        if let Some(running) = self.running_above_idle_refusal() {
            return Some(running);
        }

        self.rate_limit_refusal(inner)
    }

    /// Refuses a write while live RPM reads above [`IDLE_ADJACENT_RPM`].
    ///
    /// Read from the live session itself rather than from a log document: opening a saved log
    /// while connected replaces the document without ending the session, and judging the engine
    /// by the last row of somebody's old file gets it wrong in both directions.
    ///
    /// Silently allows the write through when RPM cannot be found at all: a session with no
    /// engine-speed channel, or one that has not sampled yet, is not "the engine is running
    /// above idle", it is "this cannot be answered", and the other gates already keep a write off
    /// anything that is not a live, connected controller.
    fn running_above_idle_refusal(&self) -> Option<AgentRefusal> {
        let rpm = self.host.live_rpm()?;

        if !rpm.is_finite() || rpm <= IDLE_ADJACENT_RPM {
            return None;
        }

        Some(AgentRefusal::new(
            "the engine is running above idle",
            format!(
                "RPM reads {}, above the {} rpm guard. Writes are refused while the engine may be \
                 under load or being driven; try again at idle or with it off.",
                nf::f64(rpm, "0"),
                nf::f64(IDLE_ADJACENT_RPM, "0")
            ),
        ))
    }

    /// Refuses a write once too many have landed too quickly.
    ///
    /// **One batch apply counts as one write, not as its items.** The limit is checked once for
    /// the batch as a whole and then stands down for the items inside it, because the failure it
    /// guards against is an agent looping, not an agent making one deliberate change that
    /// happens to move twenty cells. Counting the items instead would let the first ten reach a
    /// running engine and refuse the rest, and half a table row moved is worse than either the
    /// whole row or none of it. The magnitude, dangerous-constant and RPM guards still run on
    /// every single item regardless.
    fn rate_limit_refusal(&self, inner: &mut Inner) -> Option<AgentRefusal> {
        if inner.batches_in_flight > 0 {
            return None;
        }

        let now = Instant::now();
        let window = self.window;
        inner.recent_writes.retain(|at| now.duration_since(*at) <= window);

        if inner.recent_writes.len() < RATE_LIMIT_COUNT {
            return None;
        }

        Some(AgentRefusal::new(
            "too many writes in a short time",
            format!(
                "{} agent writes landed in the last {} seconds. Slow down, and check what each one \
                 did before sending the next.",
                inner.recent_writes.len(),
                nf::f64(window.as_secs_f64(), "0")
            ),
        ))
    }

    /// Notes that a write actually reached the ECU, for the rate limit.
    fn record_write(&self, inner: &mut Inner) {
        // Inside a batch the batch itself has already been counted, and counting its items again
        // would spend the whole allowance on one deliberate action.
        if inner.batches_in_flight > 0 {
            return;
        }

        inner.recent_writes.push(Instant::now());

        // A generous backstop rather than a rolling trim on every call: nothing in normal use
        // gets near it, since the refusal already prunes anything outside the window.
        if inner.recent_writes.len() > 1000 {
            let excess = inner.recent_writes.len() - 1000;
            inner.recent_writes.drain(..excess);
        }
    }

    // ----- sending ------------------------------------------------------------------------------

    /// Sends the changed settings to the controller. The only place a setting write leaves.
    ///
    /// Unlike a table, this is several writes and they may span pages, so a failure part way
    /// through leaves some of them applied. That is reported as what it is rather than as "the
    /// write failed", because the two call for different things: one means try again, the other
    /// means find out what the ECU is now running before doing anything else.
    fn write_settings_to_ecu(&self, inner: &mut Inner) -> String {
        let Inner {
            tune,
            settings_edit,
            target,
            settings_pages_written,
            ..
        } = inner;

        let Some(edit) = settings_edit.as_mut() else {
            return "No tune has been read.".to_string();
        };

        let Some(target) = target.clone() else {
            return "Not connected to an ECU.".to_string();
        };

        let Some(held) = tune.as_mut() else {
            return "No tune has been read.".to_string();
        };

        if !edit.has_changes() {
            return "Nothing has been changed.".to_string();
        }

        let writes = edit.writes(&held.tune);
        if writes.is_empty() {
            return "Nothing has been changed.".to_string();
        }

        let settings = edit.changed_count();
        let (mut bytes, mut done) = (0usize, 0usize);

        let outcome: Result<(), String> = (|| {
            for write in &writes {
                match &target {
                    EcuTarget::Maxx(source) => send_to_maxx(source, write)?,
                    EcuTarget::Pages(connection) => {
                        send_to_page(connection, &held.tune, write)?;
                        settings_pages_written.insert(write.page);
                    }
                }

                // The copy held here has to move with the controller, or the next
                // read-modify-write is against stale bytes.
                held.tune.accept(write);

                bytes += write.data.len();
                done += 1;
            }

            Ok(())
        })();

        let said = match (outcome, &target) {
            (Ok(()), EcuTarget::Maxx(_)) => format!(
                "Sent {settings} setting{} ({} bytes) to the MaxxECU, verified. There is no burn \
                 step on this controller \u{2014} it is already running them, permanently, and a \
                 power cycle will not undo it.",
                if settings == 1 { "" } else { "s" },
                nf::int(bytes as i32, "N0")
            ),
            (Ok(()), EcuTarget::Pages(_)) => format!(
                "Sent {settings} setting{} ({} bytes) to the ECU. It is running them now, and will \
                 forget them at the next power cycle unless you burn them.",
                if settings == 1 { "" } else { "s" },
                nf::int(bytes as i32, "N0")
            ),
            (Err(message), EcuTarget::Maxx(_)) => {
                if done == 0 {
                    format!("Nothing was sent: {message}")
                } else {
                    format!(
                        "{done} of {} writes reached the ECU ({} bytes), verified, and then this \
                         failed: {message} Those {done} are already applied and permanent \u{2014} \
                         there is no burn to skip and no power cycle that undoes them.",
                        writes.len(),
                        nf::int(bytes as i32, "N0")
                    )
                }
            }
            (Err(message), EcuTarget::Pages(_)) => {
                if done == 0 {
                    format!("Nothing was sent: {message}")
                } else {
                    format!(
                        "{done} of {} writes reached the ECU ({} bytes) and then this failed: \
                         {message} Some settings have changed and some have not. Nothing was \
                         burned, so turning the key off restores all of it.",
                        writes.len(),
                        nf::int(bytes as i32, "N0")
                    )
                }
            }
        };

        // What the ECU took stops being a pending change; what it did not stays one. Which is
        // which is answered by comparing, not by counting the writes that got through.
        edit.reconcile(&held.tune);

        said
    }

    /// Sends one table edit to the controller. The only place a table write leaves. Returns the
    /// message and the value the cell holds afterwards (clamped into the firmware's range by the
    /// edit where it needed to be), read from the edit that was just encoded and sent rather
    /// than from anything on screen.
    fn write_table_to_ecu(&self, inner: &mut Inner, edit: &TuneEdit, column: i32, row: i32) -> (String, f64) {
        let landed = edit.get(column, row);

        let Inner {
            tune,
            settings_edit,
            target,
            maxx_definitions,
            ..
        } = inner;

        let Some(held) = tune.as_mut() else {
            return ("No tune has been read.".to_string(), landed);
        };

        // What the tune is comes before what is on screen: the same refusal the settings make,
        // and for the same reason. A placeholder tune is all zeros and a tune from a file belongs
        // to whatever saved it.
        if held.placeholder {
            return (
                "This is a firmware definition rather than a tune \u{2014} every value in it reads \
                 as zero, so nothing here may be sent to a controller."
                    .to_string(),
                landed,
            );
        }

        if held.from_file {
            return (
                "This tune was opened from a file rather than read off the controller, so it \
                 cannot be sent back. Read the ECU's own tune first."
                    .to_string(),
                landed,
            );
        }

        let Some(target) = target.clone() else {
            return ("Not connected to an ECU.".to_string(), landed);
        };

        if !edit.has_changes() {
            return ("Nothing has been changed.".to_string(), landed);
        }

        let Some(write) = edit.encode(&held.tune) else {
            return (
                "This table cannot be encoded for this firmware, so nothing was sent.".to_string(),
                landed,
            );
        };

        let cells = edit.changed_count();

        let sent: Result<(), String> = match &target {
            EcuTarget::Maxx(source) => send_to_maxx(source, &write),
            EcuTarget::Pages(connection) => send_to_page(connection, &held.tune, &write),
        };

        if let Err(message) = sent {
            return (format!("The write failed: {message}"), landed);
        }

        // The tune held in memory has to move with the ECU, or the next read-modify-write would
        // be against stale bytes and would undo this one.
        held.tune.accept(&write);

        // And so does the settings edit, which holds a copy of the pages taken when it was made
        // and works out what to send by comparing the two. Left behind, those table bytes read
        // as pending settings changes carrying the values from before this write, so the next
        // time anything is sent from a settings page it puts the table back, on a running
        // engine, with nothing reporting an error.
        if let Some(settings) = settings_edit.as_mut() {
            settings.accept(&write);
        }

        let said = match &target {
            EcuTarget::Pages(_) => format!(
                "Sent {cells} changed cell{} to the ECU. It is running this now, and will forget it \
                 at the next power cycle unless you burn it.",
                if cells == 1 { "" } else { "s" }
            ),
            EcuTarget::Maxx(_) => format!(
                "Sent {cells} changed cell{} ({} bytes) to the MaxxECU, verified. There is no burn \
                 step on this controller \u{2014} it is already running this, permanently, and a \
                 power cycle will not undo it.{}",
                if cells == 1 { "" } else { "s" },
                nf::int(write.data.len() as i32, "N0"),
                overlap_warning(maxx_definitions, &write)
            ),
        };

        (said, landed)
    }

    // ----- propose / apply -------------------------------------------------------------------------

    /// Works out what a batch of settings and table cells would change, without sending
    /// anything.
    ///
    /// Answerable even when writes are not armed and even against a tune opened from a file,
    /// because nothing here can reach an ECU: a placeholder tune (all noughts, from a definition
    /// alone) is the one thing refused, since a diff against it would not mean anything.
    pub fn propose_tune(&self, settings: &[ProposedSetting], cells: &[ProposedCell]) -> TuneProposalResult {
        let inner = self.lock();

        let Some(held) = inner.tune.as_ref().filter(|h| !h.placeholder) else {
            return TuneProposalResult {
                changes: Vec::new(),
                rejected: vec!["no tune has been read".to_string()],
                summary: "No tune has been read, so there is nothing to propose changes against.".to_string(),
            };
        };

        let tables = held.tune.tables(&held.definitions);
        let (working, rejected) = build_proposal(held, &tables, settings, cells);
        let changes = tune_compare::compare(&working, &held.tune);
        let summary = summarize_proposal(&changes, &rejected);

        TuneProposalResult {
            changes,
            rejected,
            summary,
        }
    }

    /// Applies the same shape of batch for real: the usual gate once for the whole thing, then
    /// each setting and cell through the single-write paths in turn, the same machinery a lone
    /// call uses, so this is not a second way of writing to the ECU. Keeps a version of what
    /// landed on success, the way a person pressing "keep tune" would.
    pub fn apply_tune(
        &self,
        settings: &[ProposedSetting],
        cells: &[ProposedCell],
        note: &str,
        confirm_dangerous: bool,
    ) -> TuneApplyResult {
        let _origin = WireOrigin::enter(WireOrigin::Agent);

        let mut inner = self.lock();
        let inner = &mut *inner;

        if note.trim().is_empty() {
            return TuneApplyResult::refused(AgentRefusal::new(
                "no rationale given",
                "Say in one line why this change is being made. It doubles as the version note.",
            ));
        }

        if let Some(refused) = self.general_refusal(inner) {
            return TuneApplyResult::refused(refused);
        }

        let Some(held) = inner.tune.as_ref() else {
            return TuneApplyResult::refused(AgentRefusal::reason("no tune has been read"));
        };

        // The same working copy that answers /tune/propose, so what is about to be sent cannot
        // silently differ from what a caller who proposed first was shown.
        let tables = held.tune.tables(&held.definitions);
        let (working, mut rejected) = build_proposal(held, &tables, settings, cells);
        let wanted = tune_compare::compare(&working, &held.tune);

        if wanted.is_empty() {
            let summary = if rejected.is_empty() {
                "Nothing would change; the ECU already holds these values.".to_string()
            } else {
                format!("Nothing valid to apply. {}", rejected.join(" "))
            };

            return TuneApplyResult {
                refusal: None,
                applied: Vec::new(),
                rejected,
                summary,
            };
        }

        // What the controller held before any of this went out, so what actually landed can be
        // measured rather than inferred. Inferring it from which calls returned null is wrong in
        // exactly the case that matters: a table where three of five cells took reports the
        // whole five-cell difference as applied, and then records that overstatement as the
        // version note.
        let before = EcuTune::from_pages(Arc::clone(held.tune.layout_arc()), held.tune.pages().to_vec());

        // One batch counts as one write.
        inner.recent_writes.push(Instant::now());
        inner.batches_in_flight += 1;

        for s in settings {
            let known = inner
                .tune
                .as_ref()
                .is_some_and(|h| !s.name.trim().is_empty() && h.tune.constant(&s.name).is_some());
            if !known {
                continue;
            }

            if let Some(refused) = self.set_setting_locked(inner, &s.name, s.value, note, confirm_dangerous) {
                rejected.push(format!("{}: {}", s.name, refused.reason));
            }
        }

        for c in cells {
            let known = Self::tables_of(inner).iter().any(|t| eq_ignore_case(&t.name, &c.table));
            if !known {
                continue; // already rejected by build_proposal
            }

            let result = self.set_table_cell_locked(inner, &c.table, c.column, c.row, c.value, note, confirm_dangerous);

            if let Some(refused) = result.refusal {
                rejected.push(format!("{}[{},{}]: {}", c.table, c.column, c.row, refused.reason));
            }
        }

        inner.batches_in_flight = inner.batches_in_flight.saturating_sub(1);

        // Measured against the bytes as they were, so a partly applied batch reports the part
        // that landed and nothing more.
        let applied = match &inner.tune {
            Some(now) => tune_compare::compare(&now.tune, &before),
            None => Vec::new(),
        };

        if !applied.is_empty() {
            let described = applied
                .iter()
                .map(TuneDifference::summary)
                .collect::<Vec<_>>()
                .join("; ");
            let kept = if note.trim().is_empty() {
                format!("Agent applied: {described}")
            } else {
                format!("{note} \u{2014} applied: {described}")
            };

            // Best effort: a successful write to the ECU must never be reported as failed
            // because there was nowhere to record it.
            self.host.keep_tune(&kept);
        }

        let summary = if applied.is_empty() {
            format!("Nothing was applied. {}", rejected.join(" "))
        } else {
            let head = format!(
                "Applied {} change{}.",
                nf::int(applied.len() as i32, "N0"),
                if applied.len() == 1 { "" } else { "s" }
            );

            if rejected.is_empty() {
                head
            } else {
                format!("{head} {}", rejected.join(" "))
            }
        };

        TuneApplyResult {
            refusal: None,
            applied,
            rejected,
            summary,
        }
    }

    // ----- staging ---------------------------------------------------------------------------------

    /// Writes a tune out as a real `.msq` in the staging folder: the tune in hand, or what a
    /// batch of proposed changes would make it. Not gated like a write: it reaches no
    /// controller. The only thing that can refuse it is a name that tries to leave the folder.
    pub fn stage_tune(&self, settings: &[ProposedSetting], cells: &[ProposedCell], filename: &str) -> AgentStageResult {
        let inner = self.lock();

        let Some(held) = inner.tune.as_ref().filter(|h| !h.placeholder) else {
            return AgentStageResult::refused(
                "no tune has been read",
                "There is nothing to stage. Connect to an ECU and read its tune, or open a saved one.",
            );
        };

        // The proposal's rejections are not surfaced here: a name that made no sense simply does
        // not move any bytes, and the file staged is still exactly what the rest of the batch
        // would produce.
        let xml = if settings.is_empty() && cells.is_empty() {
            msq_writer::write(&held.tune, &held.signature, None, "Staged by an agent.", None)
        } else {
            let tables = held.tune.tables(&held.definitions);
            let (working, _) = build_proposal(held, &tables, settings, cells);
            msq_writer::write(&working, &held.signature, None, "Staged by an agent.", None)
        };

        let name = if filename.trim().is_empty() {
            format!("tune-{}.msq", self.host.file_stamp())
        } else {
            filename.to_string()
        };

        self.stage(&name, &xml)
    }

    /// Writes one named table out as an import-ready CSV in the staging folder.
    pub fn stage_table(&self, name: &str, filename: &str) -> AgentStageResult {
        let Some(table) = self.table(name) else {
            return AgentStageResult::refused("no such table", name);
        };

        let file_name = if filename.trim().is_empty() {
            format!("{}-{}.csv", safe_stem(&table.name), self.host.file_stamp())
        } else {
            filename.to_string()
        };

        let mut text: Vec<u8> = Vec::new();
        if let Err(e) = csv_export::write_tune_table(&mut text, &table) {
            return AgentStageResult::refused("could not write the staged file", &e.to_string());
        }

        self.stage(&file_name, &String::from_utf8_lossy(&text))
    }

    fn stage(&self, name: &str, content: &str) -> AgentStageResult {
        let staging = match AgentStaging::new(&self.workspace) {
            Ok(s) => s,
            Err(e) => return stage_failure(&e),
        };

        match staging.stage(name, content) {
            Ok(path) => match std::fs::metadata(&path) {
                Ok(meta) => AgentStageResult::staged(AgentStagedFile {
                    name: path
                        .file_name()
                        .map(|n| n.to_string_lossy().into_owned())
                        .unwrap_or_default(),
                    path: path.to_string_lossy().into_owned(),
                    bytes: i64::try_from(meta.len()).unwrap_or(i64::MAX),
                    written_at: meta.modified().unwrap_or(SystemTime::UNIX_EPOCH),
                }),
                Err(e) => stage_failure(&SafeFolderError::Io(e)),
            },
            Err(e) => stage_failure(&e),
        }
    }

    /// What is sitting in the staging folder right now.
    pub fn list_staged(&self) -> Vec<AgentStagedFile> {
        match AgentStaging::new(&self.workspace) {
            Ok(staging) => staging.list_staged(),
            Err(_) => Vec::new(),
        }
    }
}

fn stage_failure(e: &SafeFolderError) -> AgentStageResult {
    match e {
        SafeFolderError::Argument(message) => AgentStageResult::refused("could not stage that file", message),
        SafeFolderError::Io(io) => AgentStageResult::refused("could not write the staged file", &io.to_string()),
    }
}

/// A table name made safe to open a file with, for the default staged name.
fn safe_stem(name: &str) -> String {
    name.chars()
        .map(|c| {
            if c == ' ' {
                '-'
            } else if matches!(c, '"' | '<' | '>' | '|' | ':' | '*' | '?' | '\\' | '/') || (c as u32) < 32 {
                '_'
            } else {
                c
            }
        })
        .collect()
}

/// The firmware's description of a table's cells, for its limits and precision.
fn constant_for(held: &TuneInHand, table: &TuneTable) -> Option<TuneConstant> {
    let definition = held.definitions.iter().find(|d| d.title == table.name)?;

    if definition.values.is_empty() {
        return None;
    }

    held.tune
        .layout()
        .constants
        .iter()
        .find(|c| c.name == definition.values)
        .cloned()
}

/// Refuses a write to a constant `DangerousConstants` recognises unless `confirm_dangerous` says
/// otherwise.
///
/// Checked once the constant actually behind the write is known (a scalar's own name, or the
/// constant behind a table's cells) rather than as part of the general refusal, since it needs
/// that name to judge anything at all.
fn dangerous_refusal(held: &TuneInHand, touched: &str, confirm_dangerous: bool) -> Option<AgentRefusal> {
    if touched.is_empty() || confirm_dangerous {
        return None;
    }

    let role = dangerous_constants::find(held.tune.layout(), touched)?;

    Some(AgentRefusal::new(
        "this setting needs confirmDangerous:true",
        format!(
            "\"{touched}\" matches the {} guard \u{2014} a setting whose job is to stop the engine \
             hurting itself. Pass confirmDangerous:true if this change is intentional.",
            role.name()
        ),
    ))
}

/// Refuses a single change bigger than half of the range the firmware itself declares for a
/// setting.
///
/// Silently allows the change when there is no declared range, or the prior value could not be
/// read: there is nothing to judge proportion against, and the setting's own in-range check has
/// already bounded what the new value can be.
fn magnitude_refusal(before: f64, after: f64, low: f64, high: f64) -> Option<AgentRefusal> {
    if before.is_nan() {
        return None;
    }

    let range = high - low;
    if !range.is_finite() || range <= 0.0 {
        return None;
    }

    let delta = (after - before).abs();
    let limit = range * MAGNITUDE_FRACTION;

    if delta <= limit {
        return None;
    }

    Some(AgentRefusal::new(
        "that change is too large for one write",
        format!(
            "Moving this by {} is more than half of its declared {}\u{2013}{} range in a single call. \
             Make the change in smaller steps, checking what happened after each one.",
            nf::f64(delta, "0.###"),
            nf::f64(low, "0.###"),
            nf::f64(high, "0.###")
        ),
    ))
}

/// One write to a page-protocol controller: through the connection's read-back-and-verify.
fn send_to_page(connection: &EcuConnection, tune: &EcuTune, write: &TuneWrite) -> Result<(), String> {
    let layout = tune.layout();

    let page: &TunePage = layout
        .pages
        .iter()
        .find(|p| p.index == write.page)
        .ok_or_else(|| format!("The firmware declares no page {}.", write.page))?;

    if page.chunk_write_command.is_empty() {
        return Err(format!("This firmware declares no way to write page {}.", write.page));
    }

    connection
        .write_tune_page(
            page,
            layout.blocking_factor,
            layout.little_endian,
            write.offset,
            &write.data,
            layout.inter_write_delay,
        )
        .map_err(|e| e.message())
}

/// One write to a MaxxECU over USB, read back before it counts. There is no page here and no
/// burn: the bytes land straight at their offset in the one 64 KB blob, applied and permanent
/// the moment the ECU answers Ok. The write ceiling (`0xF2E7`) is enforced by
/// `MaxxTune::write`, below this, and a write past it comes back as the error text.
fn send_to_maxx(source: &MaxxUsbSource, write: &TuneWrite) -> Result<(), String> {
    let status = source
        .write_tune(write.offset, &write.data)
        .map_err(|e| e.to_string())?;

    if status != MaxxWriteStatus::Ok {
        return Err(format!(
            "The MaxxECU answered \"{}\" for the write at offset {}.",
            status_name(status),
            write.offset
        ));
    }

    if !source
        .verify_tune(write.offset, &write.data)
        .map_err(|e| e.to_string())?
    {
        return Err(format!(
            "The MaxxECU took the write at offset {} but reads back something else now.",
            write.offset
        ));
    }

    Ok(())
}

/// The enum member's name as `ToString()` gives it; an unnamed value is its number.
fn status_name(status: MaxxWriteStatus) -> String {
    match status {
        MaxxWriteStatus::Ok => "Ok".to_string(),
        MaxxWriteStatus::NoAnswer => "NoAnswer".to_string(),
        MaxxWriteStatus::Uncertain => "Uncertain".to_string(),
        MaxxWriteStatus::UnknownCommand => "UnknownCommand".to_string(),
        MaxxWriteStatus::OutOfRange => "OutOfRange".to_string(),
        MaxxWriteStatus::WriterBusy => "WriterBusy".to_string(),
        MaxxWriteStatus::ReaderBusy => "ReaderBusy".to_string(),
        MaxxWriteStatus::FlashBusy => "FlashBusy".to_string(),
        MaxxWriteStatus::Other(byte) => byte.to_string(),
    }
}

/// A note appended to a MaxxECU table write's own result, not a refusal: a table's cell address
/// comes from the ECU's own live config struct rather than the definitions file's address cursor
/// every setting uses, and nothing cross-checks the two (found on a real bench Race, where an
/// unconfigured user table's cells began one byte into an unrelated setting's own declared
/// address). What this write also touched is worth knowing even though it went through.
fn overlap_warning(definitions: &[MaxxSettingDefinition], write: &TuneWrite) -> String {
    let overlapping = maxx_tune::settings_overlapping(definitions, write.offset, write.data.len() as i32);

    if overlapping.is_empty() {
        return String::new();
    }

    let mut names = overlapping
        .iter()
        .take(5)
        .map(|n| format!("\"{n}\""))
        .collect::<Vec<_>>()
        .join(", ");

    if overlapping.len() > 5 {
        names.push_str(&format!(", and {} more", overlapping.len() - 5));
    }

    format!(
        " Warning: this table's cells share bytes with a named setting on this ECU ({names}) \u{2014} \
         the table may never have been sized here, and this write may have changed that setting's \
         value along with the cell you asked for."
    )
}

/// Lays a batch of proposed settings and table cells over a private copy of the tune in hand, and
/// says which of them could not go in.
///
/// **The copy is what makes this safe to call before writes are armed.** `EcuTune::from_pages`
/// over cloned page bytes shares nothing with the tune in hand, so every edit below, through the
/// same `TuneSettingsEdit` and `TuneEdit` a real edit uses, lands on bytes nobody else is
/// looking at.
fn build_proposal(
    held: &TuneInHand,
    tables: &[TuneTable],
    settings: &[ProposedSetting],
    cells: &[ProposedCell],
) -> (EcuTune, Vec<String>) {
    let mut working = EcuTune::from_pages(Arc::clone(held.tune.layout_arc()), held.tune.pages().to_vec());
    let mut rejected: Vec<String> = Vec::new();

    if !settings.is_empty() {
        let mut edit = TuneSettingsEdit::new(&working);

        for s in settings {
            if s.name.trim().is_empty() {
                rejected.push("a setting with no name".to_string());
                continue;
            }

            if working.constant(&s.name).is_none() {
                rejected.push(format!("{}: no such setting", s.name));
                continue;
            }

            if !edit.set(&working, &s.name, s.value, 0) {
                rejected.push(format!(
                    "{}: {} would not go in \u{2014} out of range, or not a number",
                    s.name,
                    nf::f64(s.value, "")
                ));
            }
        }

        for write in edit.writes(&working) {
            working.accept(&write);
        }
    }

    for c in cells {
        let Some(shape) = tables.iter().find(|t| eq_ignore_case(&t.name, &c.table)) else {
            rejected.push(format!("{}: no such table", c.table));
            continue;
        };

        let (columns, rows) = (shape.columns() as i32, shape.rows() as i32);
        if c.column < 0 || c.column >= columns || c.row < 0 || c.row >= rows {
            rejected.push(format!(
                "{}[{},{}]: not in the table ({columns} by {rows})",
                c.table, c.column, c.row
            ));
            continue;
        }

        let Some(constant) = constant_for(held, shape) else {
            rejected.push(format!("{}: this table cannot be edited for this firmware", c.table));
            continue;
        };

        // Read fresh off the working copy rather than off what is on screen, so an earlier item
        // in this same batch sharing bytes with this table is seen by the next one.
        let Some(fresh) = working.table(&shape.name, &constant.name, &shape.x.constant, &shape.y.constant) else {
            rejected.push(format!("{}: could not be read off the working copy", c.table));
            continue;
        };

        let mut cell_edit = TuneEdit::new(fresh, Some(constant));
        cell_edit.set(TuneSelection::cell(c.column, c.row), c.value);

        if let Some(write) = cell_edit.encode(&working) {
            working.accept(&write);
        }
    }

    (working, rejected)
}

fn summarize_proposal(changes: &[TuneDifference], rejected: &[String]) -> String {
    let head = if changes.is_empty() {
        "Nothing would change.".to_string()
    } else {
        format!(
            "{} setting{} would change.",
            nf::int(changes.len() as i32, "N0"),
            if changes.len() == 1 { "" } else { "s" }
        )
    };

    if rejected.is_empty() {
        head
    } else {
        format!("{head} {}", rejected.join(" "))
    }
}
