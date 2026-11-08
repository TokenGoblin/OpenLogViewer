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
//! 2. **Armed.** Writing is off at start-up, off again on every connect and every disconnect,
//!    never persisted, and can only be turned on by [`AgentWriter::set_armed`], which no HTTP
//!    route calls. A person arms it; nothing an agent sends can. A connect also forgets the tune,
//!    so nothing is written until [`AgentWriter::set_tune`] supplies one read from *this*
//!    controller after the connect.
//! 3. **Connected, a real tune, read off the controller.** A placeholder (a definition file's
//!    all-noughts tune) and a tune opened from a file are both refused. The writer keeps using
//!    the layout that tune was read with (ranges, dangerous names, write commands) until the next
//!    connect: installing a definition does not change it.
//! 4. **Not running above idle, and the engine speed must be known.** Live RPM over 1,200
//!    refuses every write, and so does an RPM that is unknown, not a number, or older than a
//!    second, whenever a live session can exist. Only "no live session at all" lets a write past
//!    without a reading.
//! 5. **Not too fast.** Ten device writes in five seconds. A batch is capped at ten items, which
//!    is the whole allowance, and each device write it makes is counted.
//! 6. **Names that exist.** A setting the firmware does not declare, a table that is not there.
//! 7. **Dangerous constants.** A rev limiter, launch RPM, boost limit, or fuel or ignition cut
//!    needs `confirmDangerous` on the same call.
//! 8. **Not too large.** No single change may move a value by more than half of the range the
//!    firmware declares for it, measured from where the controller was *before the call* (so two
//!    half-steps in one batch are one whole step, and refused).
//! 9. **Only the agent's own bytes, and not over the person's.** What is sent is the byte runs
//!    where the new page differs from the held one, never the whole table or every pending edit;
//!    and a write to a page the person has unsent edits on is refused.
//!
//! Only then do bytes go out, and they go through the connection's read-back-and-verify. Nothing
//! here burns, and nothing here can: the type has no method that calls `burn_page`.
//!
//! Writes run under one lock, so two requests arriving together cannot interleave or race the
//! rate limit (the C# guards only the rate list). Reads do not take it: they are served from a
//! snapshot swapped in after each change, so a screen reading the tune never waits on a write to
//! the device.

use std::collections::{BTreeSet, HashSet};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, RwLock};
use std::time::{Duration, Instant, SystemTime};

use olv_core::Error;
use olv_core::analysis::csv_export;
use olv_core::base::safe_folder::SafeFolderError;
use olv_core::definitions::dangerous_constants;
use olv_core::definitions::definition_import;
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
use olv_live::live::maxx_tune::{self, MAXIMUM_WRITE, MaxxTable, MaxxWriteStatus, WRITE_CEILING};
use olv_live::live::maxx_tune_definitions::MaxxSettingDefinition;
use olv_live::live::maxx_usb_source::MaxxUsbSource;
use olv_store::store::workspace::Workspace;
use sha2::{Digest, Sha256};

use super::agent_api::{
    AgentCellWrite, AgentDefinitionImported, AgentLimits, AgentRefusal, AgentSetting, AgentStageResult,
    AgentStagedFile, AgentTable, AgentTuneFull, ProposedCell, ProposedSetting, TuneApplyResult, TuneProposalResult,
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

/// How old an engine speed may be and still count as "now". A reading older than this says
/// nothing about whether the engine is being revved this moment.
pub const MAX_RPM_AGE: Duration = Duration::from_secs(1);

/// Ten writes in five seconds is far beyond any sequence of settings a person reviewing each
/// change could keep up with, and far below what a loop gone wrong (a retried failure, a script
/// iterating every cell of a table with no pacing) produces in the same span. It exists to catch
/// a runaway agent, not to pace a careful one.
pub const RATE_LIMIT_COUNT: usize = 10;
pub const RATE_LIMIT_WINDOW: Duration = Duration::from_secs(5);

/// The most items one batch may carry. Equal to [`RATE_LIMIT_COUNT`] on purpose: a batch is one
/// call but it is not free, and since every device write it makes is counted against the rate
/// limit, ten items is exactly one window's allowance. A bigger change is several batches, with
/// the agent looking at what each did.
pub const MAX_BATCH_ITEMS: usize = 10;

/// The most bytes one batch may send to the device. Only changed bytes are sent, so this is far
/// beyond any honest batch (a cell is one or two bytes) and well short of rewriting a table.
pub const MAX_BATCH_BYTES: usize = 512;

/// A single change may not exceed this fraction of the range the firmware declares.
///
/// Half, rather than any smaller fraction, because a real tuning step sometimes legitimately is
/// large, and a tight cap would just teach an agent to split every honest edit into several
/// calls, which hides the total from this check rather than catching anything. What fifty per
/// cent of a declared range does catch is a value entered in the wrong units, a misplaced
/// decimal point, or an off-by-a-decade mistake.
pub const MAGNITUDE_FRACTION: f64 = 0.5;

/// What the live session knows about engine speed.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum RpmReading {
    /// The host says there is no live session. **The writer does not take its word for it**: a
    /// controller is connected whenever a write gets this far, and a connected controller may be
    /// running whatever the host's label says, so this is refused exactly like [`Unknown`](Self::Unknown).
    NoSession,
    /// A session exists but has no engine speed: no sample yet, or no engine-speed channel.
    Unknown,
    /// The latest engine speed and how long ago it was sampled.
    Sample { rpm: f64, age: Duration },
}

/// A firmware definition an agent staged, put to the person.
#[derive(Clone, Debug)]
pub struct DefinitionImportRequest<'a> {
    pub name: &'a str,
    /// What the file declares itself to be.
    pub signature: &'a str,
    pub source: &'a str,
    /// SHA-256 of the staged file, lower-case hex; what a yes is a yes to.
    pub sha256: &'a str,
    /// True when there was no ECU identity to check the file's signature against, so nothing says
    /// it is for the controller that is connected. A yes then means the person accepts that.
    pub unverified: bool,
}

/// What the application tells the writer that only it knows.
pub trait WriterHost: Send + Sync {
    /// The engine speed of the live session (not of whatever log is open).
    fn live_rpm(&self) -> RpmReading;

    /// The pages on which the person at the machine has edits they have not sent. An agent write
    /// to any of them is refused: it must not send, or overwrite, what the person has not
    /// decided to send.
    fn human_unsent_pages(&self) -> BTreeSet<i32>;

    /// Asks the person whether a firmware definition an agent staged may be installed. Only the
    /// person's yes installs it; the writer never answers this itself. The request carries the
    /// SHA-256 of the staged file, which is the only file the writer will install: if its bytes
    /// differ when the answer comes back, nothing is installed.
    fn confirm_definition_import(&self, request: &DefinitionImportRequest<'_>) -> bool;

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
#[derive(Clone)]
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
    target: Option<EcuTarget>,
    maxx_definitions: Vec<MaxxSettingDefinition>,
    maxx_tables: Vec<MaxxTable>,
    recent_writes: Vec<Instant>,
    batches_in_flight: u32,
    /// Bytes sent so far by the batch in flight.
    batch_bytes: usize,
    /// Counts connects and disconnects; a tune carries the one it was read after.
    generation: u64,
    /// The signature the connected controller reported; a tune must declare the same.
    signature: String,
    /// Pages a write may have partly reached. The held copy of them cannot be trusted until the
    /// tune is read again.
    stale_pages: BTreeSet<i32>,
}

/// What a run of device writes came to.
struct Sent {
    done: usize,
    bytes: usize,
    error: Option<String>,
}

pub struct AgentWriter {
    armed: AtomicBool,
    inner: Mutex<Inner>,
    /// What readers see: replaced, never edited, after every change. Taking it never waits for the
    /// device.
    snapshot: RwLock<Option<Arc<TuneInHand>>>,
    pages_written: Mutex<BTreeSet<i32>>,
    /// One definition import at a time, from staging to install.
    import_lock: Mutex<()>,
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
                target: None,
                maxx_definitions: Vec::new(),
                maxx_tables: Vec::new(),
                recent_writes: Vec::new(),
                batches_in_flight: 0,
                batch_bytes: 0,
                generation: 0,
                signature: String::new(),
                stale_pages: BTreeSet::new(),
            }),
            snapshot: RwLock::new(None),
            pages_written: Mutex::new(BTreeSet::new()),
            import_lock: Mutex::new(()),
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
            inner.batch_bytes = 0;
            inner
        })
    }

    /// Replaces what readers see with the tune as it now stands.
    fn publish(&self, inner: &Inner) {
        let fresh = inner.tune.clone().map(Arc::new);
        *self.snapshot.write().unwrap_or_else(|p| p.into_inner()) = fresh;
    }

    /// The tune as of the last change, without waiting for any write in flight.
    fn held(&self) -> Option<Arc<TuneInHand>> {
        self.snapshot.read().unwrap_or_else(|p| p.into_inner()).clone()
    }

    // ----- arming ------------------------------------------------------------------------

    /// Whether writing is allowed at this moment. False on start-up and after every connect and
    /// disconnect.
    pub fn is_armed(&self) -> bool {
        self.armed.load(Ordering::SeqCst)
    }

    /// The person at the machine turning writing on or off. **Not reachable from the agent
    /// API**: no route, no bridge method and no message calls it.
    pub fn set_armed(&self, armed: bool) {
        self.armed.store(armed, Ordering::SeqCst);
    }

    /// A controller is connected, and says its firmware is `signature`: the only place bytes can go.
    ///
    /// Disarms, and forgets the tune: a permission granted for the bench engine must not still be
    /// granted when the next thing plugged in is a car, and a tune read from the last controller
    /// says nothing about this one. Every write is refused until [`set_tune`](Self::set_tune)
    /// supplies a tune read after this call, and the person arms again.
    ///
    /// Returns the generation of this connection. A tune read before a later connect or disconnect
    /// carries an older one and is ignored, so a slow read that finishes after the controller has
    /// changed cannot put its tune in front of the new one.
    pub fn connect(&self, target: EcuTarget, signature: &str) -> u64 {
        self.set_armed(false);

        let mut inner = self.lock();
        inner.generation += 1;
        inner.target = Some(target);
        inner.signature = signature.trim().to_string();
        inner.tune = None;
        inner.stale_pages.clear();
        self.publish(&inner);
        let generation = inner.generation;
        drop(inner);

        self.pages_written.lock().unwrap_or_else(|p| p.into_inner()).clear();

        generation
    }

    /// The controller is gone. Disarms and forgets the tune, as [`connect`](Self::connect) does.
    pub fn disconnect(&self) {
        self.set_armed(false);

        let mut inner = self.lock();
        inner.generation += 1;
        inner.target = None;
        inner.signature.clear();
        inner.tune = None;
        inner.stale_pages.clear();
        self.publish(&inner);
        drop(inner);

        self.pages_written.lock().unwrap_or_else(|p| p.into_inner()).clear();
    }

    /// The generation of the connection now in force (see [`connect`](Self::connect)).
    pub fn generation(&self) -> u64 {
        self.lock().generation
    }

    /// The tune read after the connect that returned `generation`, or none.
    ///
    /// Ignored, with a note on stderr, and `false` returned, if `generation` is not the current
    /// one (the controller has changed since the read began), or if the tune declares a firmware
    /// other than the connected controller reported. Accepting one clears what a failed write left
    /// in doubt: this is the re-read.
    pub fn set_tune(&self, generation: u64, tune: Option<TuneInHand>) -> bool {
        let mut inner = self.lock();

        if generation != inner.generation {
            eprintln!(
                "olv-agent: ignored a tune read for connection {generation}; connection {} is in force",
                inner.generation
            );
            return false;
        }

        if let Some(held) = &tune
            && inner.target.is_some()
            && !eq_ignore_case(held.signature.trim(), &inner.signature)
        {
            eprintln!(
                "olv-agent: ignored a tune for \"{}\"; the connected controller reported \"{}\"",
                held.signature, inner.signature
            );
            return false;
        }

        inner.tune = tune;
        inner.stale_pages.clear();
        self.publish(&inner);

        true
    }

    /// Pages a failed write may have partly reached, which agent writes are refused until the tune
    /// is read again.
    pub fn stale_pages(&self) -> Vec<i32> {
        self.lock().stale_pages.iter().copied().collect()
    }

    /// The setting definitions MTune declares, for the overlap warning on a MaxxECU table write.
    pub fn set_maxx_definitions(&self, definitions: Vec<MaxxSettingDefinition>) {
        self.lock().maxx_definitions = definitions;
    }

    /// The MaxxECU's tables as the ECU holds them (`MaxxTuneModel::maps`). A table write needs
    /// them to work out the checksum the ECU keeps with each table; without them a MaxxECU table
    /// write is refused rather than sent without one.
    pub fn set_maxx_tables(&self, tables: Vec<MaxxTable>) {
        self.lock().maxx_tables = tables;
    }

    /// The pages an agent write has touched and nothing has burned, for the Burn button. (The
    /// connection tracks unburned pages itself too.)
    pub fn settings_pages_written(&self) -> Vec<i32> {
        self.pages_written
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .iter()
            .copied()
            .collect()
    }

    /// Always false: the writer keeps no pending edits. Each agent write is worked out from the
    /// held tune, sent, or dropped; a refused write leaves nothing behind for the next call to
    /// send.
    pub fn has_setting_changes(&self) -> bool {
        false
    }

    /// Runs a closure over the tune in hand; the app's own screens read it this way. Never waits
    /// for a write.
    pub fn with_tune<R>(&self, read: impl FnOnce(Option<&TuneInHand>) -> R) -> R {
        let held = self.held();
        read(held.as_deref())
    }

    // ----- what an agent may read ----------------------------------------------------------

    /// The tune as an agent reads it, or nothing when none was read.
    pub fn tune_values(&self) -> Vec<(String, f64)> {
        match self.held() {
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
        match self.held() {
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
        let held = self.held();

        let Some(held) = held.as_deref().filter(|h| !h.placeholder) else {
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
        let refused = self.set_setting_locked(&mut inner, None, name, value, rationale, confirm_dangerous);
        self.publish(&inner);

        refused
    }

    /// `baseline` is where the controller was before the call (a batch's snapshot); None means the
    /// tune as it stands.
    fn set_setting_locked(
        &self,
        inner: &mut Inner,
        baseline: Option<&EcuTune>,
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

        if let Some(driver) = scale_driver_refusal(held, name) {
            return Some(driver);
        }

        // Where the controller was before the call, not after whatever an earlier item of the same
        // batch moved it to: two half-steps in one batch are one whole step.
        let origin = baseline.unwrap_or(&held.tune);
        let before = TuneSettingsEdit::new(origin).original(origin, name, 0);

        // A new edit for every call, made from the tune as it stands, so what it holds is this
        // change and nothing else: no earlier failed attempt, no one else's pending edit.
        let mut edit = TuneSettingsEdit::new(&held.tune);

        if !edit.set(&held.tune, name, value, 0) {
            return Some(AgentRefusal::new(
                "the value would not go in",
                "It is out of the range the firmware declares, or the setting is not a number.",
            ));
        }

        if let Some(too_large) = magnitude_refusal(before, value, constant.low, constant.high) {
            return Some(too_large);
        }

        // Only the byte runs that differ from the held pages.
        let writes = edit.writes(&held.tune);

        if writes.is_empty() {
            return Some(AgentRefusal::new(
                "the write did not go through",
                "Nothing has been changed.",
            ));
        }

        if let Some(clash) = self.human_unsent_refusal(&writes) {
            return Some(clash);
        }

        if let Some(stale) = stale_refusal(inner, &writes) {
            return Some(stale);
        }

        if matches!(inner.target, Some(EcuTarget::Maxx(_)))
            && let Some(shared) = table_overlap_refusal(&inner.maxx_tables, &writes)
        {
            return Some(shared);
        }

        let sent = self.send_all(inner, &writes, false);

        match sent.error {
            None => None,
            Some(ref message) => Some(AgentRefusal::new(
                "the write did not go through",
                failure_text(inner.target.as_ref(), &sent, writes.len(), message),
            )),
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
        let result = self.set_table_cell_locked(
            &mut inner,
            None,
            table,
            column,
            row,
            value,
            rationale,
            confirm_dangerous,
        );
        self.publish(&inner);

        result
    }

    #[allow(clippy::too_many_arguments)]
    fn set_table_cell_locked(
        &self,
        inner: &mut Inner,
        baseline: Option<&EcuTune>,
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

        let Some(held) = inner.tune.as_ref() else {
            return AgentCellWrite::refused(AgentRefusal::new("no such table", name));
        };

        // By the name a person sees, which is the one the API hands out.
        let Some(table) = held
            .tune
            .tables(&held.definitions)
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

        if let Some(driver) = constant.as_ref().and_then(|c| scale_driver_refusal(held, &c.name)) {
            return AgentCellWrite::refused(driver);
        }

        // The cell as it was before the call (for a batch, before the batch).
        let before = match baseline {
            Some(origin) => baseline_cell(held, origin, &table, column, row),
            None => table.values[(column as usize, row as usize)],
        };

        if let Some(constant) = &constant
            && let Some(too_large) = magnitude_refusal(before, value, constant.low, constant.high)
        {
            return AgentCellWrite::refused(too_large);
        }

        let mut edit = TuneEdit::new(table.clone(), constant);
        edit.set(TuneSelection::cell(column, row), value);
        let landed = edit.get(column, row);

        let Some(encoded) = edit.encode(&held.tune) else {
            return AgentCellWrite::refused(AgentRefusal::new(
                "the write did not go through",
                "This table cannot be encoded for this firmware, so nothing was sent.",
            ));
        };

        // What is sent is where the encoded table differs from the held page, not the table.
        let mut warning = None;
        let writes = match inner.target.as_ref() {
            Some(EcuTarget::Maxx(_)) => match plan_maxx_table(held, &inner.maxx_tables, &table.name, &encoded) {
                Ok((record, runs)) => {
                    let note = overlap_warning(&inner.maxx_definitions, &record);
                    warning = note.strip_prefix(' ').filter(|n| !n.is_empty()).map(str::to_string);
                    runs
                }
                Err(why) => {
                    return AgentCellWrite::refused(AgentRefusal::new("the write did not go through", why));
                }
            },
            _ => diff_runs(held.tune.pages().get(encoded.page.max(0) as usize), &encoded),
        };

        if writes.is_empty() {
            return AgentCellWrite::refused(AgentRefusal::new(
                "the write did not go through",
                "Nothing has been changed.",
            ));
        }

        if let Some(clash) = self.human_unsent_refusal(&writes) {
            return AgentCellWrite::refused(clash);
        }

        if let Some(stale) = stale_refusal(inner, &writes) {
            return AgentCellWrite::refused(stale);
        }

        // A MaxxECU table is its cells and then the checksum, in that order. If the cells land and
        // the checksum does not, the ECU holds a table it distrusts, so the checksum is tried
        // twice.
        let maxx = matches!(inner.target, Some(EcuTarget::Maxx(_)));
        let sent = self.send_all(inner, &writes, maxx);

        match sent.error {
            None => AgentCellWrite::landed(landed, warning),
            Some(message) if maxx && sent.done > 0 => AgentCellWrite::refused(AgentRefusal::new(
                "the table's checksum is now invalid",
                format!(
                    "The cell was written to the MaxxECU but the table's checksum was not (${message}), so the ECU now \
                     holds a table whose checksum does not match its cells and will read it as nought until the \
                     checksum is written. There is no burn and no undo. Send the same cell again to write the \
                     checksum, and do not run the engine until a read of the table shows it is right."
                ),
            )),
            Some(message) => AgentCellWrite::refused(AgentRefusal::new(
                "the write did not go through",
                format!("The write failed: {message}"),
            )),
        }
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
                "Tick \"Allow agent writes\" in the application. It clears itself on connect and disconnect.",
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

    /// Refuses a write while live RPM reads above [`IDLE_ADJACENT_RPM`], and while it cannot be
    /// trusted.
    ///
    /// Read from the live session itself rather than from a log document: opening a saved log
    /// while connected replaces the document without ending the session, and judging the engine
    /// by the last row of somebody's old file gets it wrong in both directions.
    ///
    /// **Fails closed.** The C# let a write through when RPM could not be found, reasoning that the
    /// other gates keep a write off anything that is not a live controller. But a connected
    /// controller whose RPM channel is missing, stalled or stale is exactly one that may be
    /// running, so an unknown, non-finite or old reading refuses, and so does the host's claim that there
    /// is no session at all (the writer only gets here with a controller connected). Nothing
    /// passes without a fresh number.
    fn running_above_idle_refusal(&self) -> Option<AgentRefusal> {
        match self.host.live_rpm() {
            RpmReading::NoSession | RpmReading::Unknown => Some(AgentRefusal::new(
                "the engine speed is not known",
                "Writes are refused while the engine speed cannot be read, because the engine may be \
                 running. Wait for the live session to report it, or stop the engine.",
            )),
            RpmReading::Sample { rpm, age } => {
                if !rpm.is_finite() {
                    return Some(AgentRefusal::new(
                        "the engine speed is not a number",
                        "The last reading of RPM was not a usable number, so the engine may be running.",
                    ));
                }

                if age > MAX_RPM_AGE {
                    return Some(AgentRefusal::new(
                        "the engine speed is out of date",
                        format!(
                            "The last reading of RPM is {} seconds old, which says nothing about the engine now. \
                             Wait for the live session to catch up.",
                            nf::f64(age.as_secs_f64(), "0.0")
                        ),
                    ));
                }

                if rpm <= IDLE_ADJACENT_RPM {
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
        }
    }

    /// Refuses a write once too many have landed too quickly.
    ///
    /// Every device write counts, a batch's included; the batch cap ([`MAX_BATCH_ITEMS`]) is one
    /// window's allowance, so a batch fits in a window and the one after it waits. The check is
    /// made once for a batch (before it starts) and stands down for its items so a batch is not
    /// cut in half, which would leave half a table row moved. The magnitude, dangerous-constant
    /// and RPM guards still run on every single item regardless.
    fn rate_limit_refusal(&self, inner: &mut Inner) -> Option<AgentRefusal> {
        if inner.batches_in_flight > 0 {
            return None;
        }

        self.prune(inner);

        if inner.recent_writes.len() < RATE_LIMIT_COUNT {
            return None;
        }

        Some(self.too_many(inner))
    }

    fn too_many(&self, inner: &Inner) -> AgentRefusal {
        AgentRefusal::new(
            "too many writes in a short time",
            format!(
                "{} agent writes landed in the last {} seconds. Slow down, and check what each one \
                 did before sending the next.",
                inner.recent_writes.len(),
                nf::f64(self.window.as_secs_f64(), "0")
            ),
        )
    }

    fn prune(&self, inner: &mut Inner) {
        let now = Instant::now();
        let window = self.window;
        inner.recent_writes.retain(|at| now.duration_since(*at) <= window);
    }

    /// Refuses a write to a page the person has unsent edits on.
    fn human_unsent_refusal(&self, writes: &[TuneWrite]) -> Option<AgentRefusal> {
        let unsent = self.host.human_unsent_pages();
        if unsent.is_empty() {
            return None;
        }

        let clash: BTreeSet<i32> = writes.iter().map(|w| w.page).filter(|p| unsent.contains(p)).collect();

        if clash.is_empty() {
            return None;
        }

        let pages = clash.iter().map(i32::to_string).collect::<Vec<_>>().join(", ");

        Some(AgentRefusal::new(
            "there are unsent edits on that page",
            format!(
                "Page {pages} holds changes the person at the machine has not sent yet. An agent write \
                 must not send them or overwrite them. Ask the person to send or discard those edits first."
            ),
        ))
    }

    // ----- sending ------------------------------------------------------------------------------

    /// Sends the planned writes, in order, to the controller. **The only place an agent's bytes
    /// leave.** Each device write is counted against the rate limit as it is attempted. Stops at the
    /// first failure and says how far it got.
    fn send_all(&self, inner: &mut Inner, writes: &[TuneWrite], retry_last: bool) -> Sent {
        let fail = |message: String| Sent {
            done: 0,
            bytes: 0,
            error: Some(message),
        };

        let Some(target) = inner.target.clone() else {
            return fail("Not connected to an ECU.".to_string());
        };

        let planned: usize = writes.iter().map(|w| w.data.len()).sum();

        if inner.batches_in_flight > 0 && inner.batch_bytes + planned > MAX_BATCH_BYTES {
            return fail(format!(
                "The batch would send more than the {MAX_BATCH_BYTES} bytes one batch may, so this item was not sent."
            ));
        }

        // Before the first byte of any of it: a MaxxECU write past its ceiling is refused whole, not
        // after the pieces ahead of it have already landed permanently.
        if matches!(target, EcuTarget::Maxx(_))
            && let Err(why) = maxx_ceiling_check(writes)
        {
            return fail(why);
        }

        let (mut done, mut bytes) = (0, 0);

        for write in writes {
            // The last piece of a MaxxECU table is its checksum. The cells before it have landed for
            // good, so a second try is worth making before saying the table is left invalid.
            let tries = if retry_last && done > 0 && done + 1 == writes.len() {
                2
            } else {
                1
            };
            let mut result = Ok(());

            for _ in 0..tries {
                inner.recent_writes.push(Instant::now());
                if inner.recent_writes.len() > 1000 {
                    let excess = inner.recent_writes.len() - 1000;
                    inner.recent_writes.drain(..excess);
                }

                let Some(held) = inner.tune.as_ref() else {
                    return Sent {
                        done,
                        bytes,
                        error: Some("No tune has been read.".to_string()),
                    };
                };

                result = match &target {
                    EcuTarget::Maxx(source) => send_to_maxx(source, write),
                    EcuTarget::Pages(connection) => send_to_page(connection, &held.tune, write),
                };

                if result.is_ok() {
                    break;
                }
            }

            if let Err(failure) = result {
                // What may have partly landed cannot be trusted in the held copy until it is read.
                if failure.maybe_landed {
                    inner.stale_pages.insert(write.page);
                }

                return Sent {
                    done,
                    bytes,
                    error: Some(failure.message),
                };
            }

            let Some(held) = inner.tune.as_mut() else {
                return Sent {
                    done,
                    bytes,
                    error: Some("No tune has been read.".to_string()),
                };
            };

            // The copy held here has to move with the controller, or the next read-modify-write
            // is against stale bytes.
            held.tune.accept(write);

            if matches!(target, EcuTarget::Pages(_)) {
                self.pages_written
                    .lock()
                    .unwrap_or_else(|p| p.into_inner())
                    .insert(write.page);
            }

            done += 1;
            bytes += write.data.len();
            inner.batch_bytes += write.data.len();
        }

        Sent {
            done,
            bytes,
            error: None,
        }
    }

    // ----- propose / apply -------------------------------------------------------------------------

    /// Works out what a batch of settings and table cells would change, without sending
    /// anything.
    ///
    /// Answerable even when writes are not armed and even against a tune opened from a file,
    /// because nothing here can reach an ECU: a placeholder tune (all noughts, from a definition
    /// alone) is the one thing refused, since a diff against it would not mean anything.
    pub fn propose_tune(&self, settings: &[ProposedSetting], cells: &[ProposedCell]) -> TuneProposalResult {
        let held = self.held();

        let Some(held) = held.as_deref().filter(|h| !h.placeholder) else {
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

    /// Applies a batch for real: the usual gate once for the whole thing, then each setting and
    /// cell through the single-write paths in turn, the same machinery a lone call uses, so this is
    /// not a second way of writing to the ECU. Keeps a version of what landed on success, the way
    /// a person pressing "keep tune" would.
    ///
    /// A batch cannot be used to get past a limit a lone call has:
    /// - at most [`MAX_BATCH_ITEMS`] items and [`MAX_BATCH_BYTES`] sent bytes;
    /// - the same target named twice keeps only the last, and the earlier ones are reported as
    ///   superseded, so a batch is not a way to send several values to one place;
    /// - every item's size is judged from where the controller was before the batch;
    /// - every device write is counted against the rate limit.
    pub fn apply_tune(
        &self,
        settings: &[ProposedSetting],
        cells: &[ProposedCell],
        note: &str,
        confirm_dangerous: bool,
    ) -> TuneApplyResult {
        let _origin = WireOrigin::enter(WireOrigin::Agent);

        let mut guard = self.lock();
        let result = self.apply_locked(&mut guard, settings, cells, note, confirm_dangerous);
        self.publish(&guard);

        result
    }

    fn apply_locked(
        &self,
        inner: &mut Inner,
        settings: &[ProposedSetting],
        cells: &[ProposedCell],
        note: &str,
        confirm_dangerous: bool,
    ) -> TuneApplyResult {
        if note.trim().is_empty() {
            return TuneApplyResult::refused(AgentRefusal::new(
                "no rationale given",
                "Say in one line why this change is being made. It doubles as the version note.",
            ));
        }

        if let Some(refused) = self.general_refusal(inner) {
            return TuneApplyResult::refused(refused);
        }

        let (settings, cells, mut rejected) = dedupe(settings, cells);

        let items = settings.len() + cells.len();
        if items > MAX_BATCH_ITEMS {
            return TuneApplyResult::refused(AgentRefusal::new(
                "that batch is too large for one call",
                format!(
                    "{items} items were given, and one call may carry {MAX_BATCH_ITEMS}. Send the change in smaller \
                     batches, checking what each did."
                ),
            ));
        }

        // The batch needs room in the window for the writes it will make.
        self.prune(inner);
        // Counted in device writes, not items: on a MaxxECU a cell is two (the cell and the table's
        // checksum).
        let per_cell = if matches!(inner.target, Some(EcuTarget::Maxx(_))) {
            2
        } else {
            1
        };
        let device_writes = settings.len() + cells.len() * per_cell;
        if inner.recent_writes.len() + device_writes > RATE_LIMIT_COUNT {
            return TuneApplyResult::refused(self.too_many(inner));
        }

        let Some(held) = inner.tune.as_ref() else {
            return TuneApplyResult::refused(AgentRefusal::reason("no tune has been read"));
        };

        // The same working copy that answers /tune/propose, so what is about to be sent cannot
        // silently differ from what a caller who proposed first was shown.
        let tables = held.tune.tables(&held.definitions);
        let (working, mut more) = build_proposal(held, &tables, &settings, &cells);
        rejected.append(&mut more);
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

        // What the controller held before any of this went out: the baseline every item is
        // measured from, and what "applied" is measured against, so a table where three of five
        // cells took reports three, not the whole difference.
        let before = held.tune.clone();

        inner.batches_in_flight += 1;
        inner.batch_bytes = 0;

        for s in &settings {
            let known = inner
                .tune
                .as_ref()
                .is_some_and(|h| !s.name.trim().is_empty() && h.tune.constant(&s.name).is_some());
            if !known {
                continue;
            }

            if let Some(refused) =
                self.set_setting_locked(inner, Some(&before), &s.name, s.value, note, confirm_dangerous)
            {
                rejected.push(format!("{}: {}", s.name, refused.reason));
            }
        }

        for c in &cells {
            let known = inner.tune.as_ref().is_some_and(|h| {
                h.tune
                    .tables(&h.definitions)
                    .iter()
                    .any(|t| eq_ignore_case(&t.name, &c.table))
            });
            if !known {
                continue; // already rejected by build_proposal
            }

            let result = self.set_table_cell_locked(
                inner,
                Some(&before),
                &c.table,
                c.column,
                c.row,
                c.value,
                note,
                confirm_dangerous,
            );

            if let Some(refused) = result.refusal {
                rejected.push(format!("{}[{},{}]: {}", c.table, c.column, c.row, refused.reason));
            }
        }

        inner.batches_in_flight = inner.batches_in_flight.saturating_sub(1);
        inner.batch_bytes = 0;

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
        let held = self.held();

        let Some(held) = held.as_deref().filter(|h| !h.placeholder) else {
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

    // ----- firmware definitions -----------------------------------------------------------------------

    /// An agent's firmware definition: checked and **staged**, installed only if the person says so.
    ///
    /// A definition decides which byte of an engine is the rev limiter, what ranges are sane, which
    /// names are dangerous and how a write is sent. Letting an agent install one is letting it
    /// rewrite the gates in front of it. So this validates the file exactly as a person's import
    /// would (it must declare the signature the ECU reported, `identity`) and keeps it in the
    /// staging folder; it reaches the definitions folder only if
    /// [`WriterHost::confirm_definition_import`] returns true, which is the person's answer and
    /// never the writer's.
    ///
    /// - One import at a time holds a lock from staging to install, so two cannot swap files under
    ///   each other.
    /// - The person is shown the SHA-256 of the staged file, and what is installed is those bytes
    ///   and only those: a file that has changed on disk since is refused.
    /// - With no ECU `identity` there is nothing to check the signature against; the request says
    ///   so (`unverified`) and a yes then means the person accepts that.
    /// - `path` names a file in the staging folder or it names nothing: any other path gets one
    ///   answer, whether or not the file exists, so this cannot be used to look around the disk.
    ///
    /// Either way nothing changes in how writes are judged: the held tune keeps the layout it was
    /// read with until the next connect.
    pub fn import_definition(
        &self,
        path: &str,
        content: &str,
        source: &str,
        name: &str,
        identity: &[String],
    ) -> AgentDefinitionImported {
        let _one_at_a_time = self.import_lock.lock().unwrap_or_else(|p| p.into_inner());

        let refuse = |problem: String| AgentDefinitionImported {
            problem,
            path: String::new(),
            name: String::new(),
            signature: String::new(),
            source: source.to_string(),
        };

        let staging_root = self.workspace.staging();
        let staging = staging_root.join("definitions");
        let staging_text = staging.to_string_lossy().into_owned();

        if !path.is_empty() {
            let inside = std::fs::canonicalize(path)
                .ok()
                .zip(std::fs::canonicalize(&staging_root).ok())
                .is_some_and(|(file, root)| file.starts_with(root));

            if !inside {
                return refuse(format!(
                    "Only a file inside {} can be imported by path. Save it there, or pass its content.",
                    staging_root.display()
                ));
            }
        }

        let staged = if path.is_empty() {
            definition_import::keep(content, &staging_text, identity, source, name)
        } else {
            definition_import::keep_file(path, &staging_text, identity, source, name)
        };

        let staged = match staged {
            Ok(result) if result.accepted() => result,
            Ok(result) => return refuse(result.problem),
            Err(e) => return refuse(e.to_string()),
        };

        let Some(provenance) = staged.provenance.clone() else {
            return refuse("it could not be checked".to_string());
        };

        let Ok(bytes) = std::fs::read(&staged.path) else {
            return refuse("the staged file could not be read back".to_string());
        };
        let digest = sha256_hex(&bytes);

        let confirmed = self.host.confirm_definition_import(&DefinitionImportRequest {
            name: &provenance.name,
            signature: &provenance.signature,
            source: &provenance.source,
            sha256: &digest,
            unverified: identity.is_empty(),
        });

        if !confirmed {
            return refuse(format!(
                "it passed the checks and is staged at {} (SHA-256 {digest}), but a definition decides how \
                 every byte of the engine is read and written, so a person has to install it from the \
                 application. Nothing was installed.",
                staged.path
            ));
        }

        // Exactly the bytes that were shown: read again, and compared, before anything is kept.
        let Ok(again) = std::fs::read(&staged.path) else {
            return refuse("the staged file could not be read back".to_string());
        };

        if sha256_hex(&again) != digest {
            return refuse("the staged file changed after it was shown to you, so nothing was installed.".to_string());
        }

        let folder = match self.workspace.ensure_definitions(Some(identity)) {
            Ok(folder) => folder.to_string_lossy().into_owned(),
            Err(e) => return refuse(e.to_string()),
        };

        // Definitions are written and read as Latin-1; one byte, one character.
        let text: String = again.iter().map(|b| char::from(*b)).collect();

        match definition_import::keep(&text, &folder, identity, source, &provenance.name) {
            Ok(done) if done.accepted() => {
                let kept = done.provenance.unwrap_or(provenance);

                AgentDefinitionImported {
                    problem: String::new(),
                    path: done.path,
                    name: kept.name,
                    signature: kept.signature,
                    source: kept.source,
                }
            }
            Ok(done) => refuse(done.problem),
            Err(e) => refuse(e.to_string()),
        }
    }
}

/// Lower-case hex SHA-256.
fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect()
}

/// Why a write failed and whether it may have reached the controller anyway.
#[derive(Debug)]
#[doc(hidden)]
pub struct Failure {
    pub message: String,
    pub maybe_landed: bool,
}

impl Failure {
    /// Refused or never sent: nothing of it is on the controller.
    fn clean(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            maybe_landed: false,
        }
    }

    /// Could be anywhere between "not at all" and "all of it".
    fn doubtful(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            maybe_landed: true,
        }
    }
}

/// Refuses a write to a page a failed write may have partly reached: what the writer holds of it
/// is not known to be what the controller holds, so the next write would be worked out from a
/// guess. Reading the tune again (`set_tune`) clears it.
fn stale_refusal(inner: &Inner, writes: &[TuneWrite]) -> Option<AgentRefusal> {
    let pages: BTreeSet<i32> = writes
        .iter()
        .map(|w| w.page)
        .filter(|p| inner.stale_pages.contains(p))
        .collect();

    if pages.is_empty() {
        return None;
    }

    let list = pages.iter().map(i32::to_string).collect::<Vec<_>>().join(", ");

    Some(AgentRefusal::new(
        "that page needs reading again",
        format!(
            "An earlier write to page {list} may have partly reached the controller, so what is held of it \
             cannot be trusted. Ask the person to read the tune from the ECU again before writing there."
        ),
    ))
}

/// Refuses a MaxxECU write whose bytes fall inside a table's cells or its checksum, unless it is
/// that table's own write (which goes through `plan_maxx_table`, with the checksum recomputed): a
/// setting that overlaps a table changes cells the checksum covers and leaves it wrong, and the ECU
/// then reads the whole table as nought.
fn table_overlap_refusal(tables: &[MaxxTable], writes: &[TuneWrite]) -> Option<AgentRefusal> {
    for write in writes {
        let start = i64::from(write.offset);
        let end = start + write.data.len() as i64;

        for table in tables {
            // The byte in front of the grid is covered by the checksum too; the checksum follows.
            let from = table.cells_at as i64 - 1;
            let to = (table.cells_at + table.columns * table.rows * 2 + 4) as i64;

            if start < to && end > from {
                return Some(AgentRefusal::new(
                    "that setting shares bytes with a table",
                    format!(
                        "The write would land inside the table \"{}\" (or its checksum), which would leave the \
                         checksum wrong. Change the table through its own cells.",
                        table.name
                    ),
                ));
            }
        }
    }

    None
}

/// The names that stand for a value another constant is scaled or offset by, directly in its
/// scale or translate expression, or through a value the firmware derives (Speeduino scales every
/// load axis by `{fuelLoadRes}`, which is itself worked out from the `algorithm` setting).
fn scale_drivers(layout: &olv_core::ini::tune_layout::TuneLayout) -> HashSet<String> {
    fn words(expression: &str) -> Vec<String> {
        let mut found = Vec::new();
        let mut current = String::new();

        for c in expression.chars().chain(std::iter::once(' ')) {
            if c.is_alphanumeric() || c == '_' {
                current.push(c);
            } else if !current.is_empty() {
                if !current.starts_with(|d: char| d.is_ascii_digit()) {
                    found.push(current.to_lowercase());
                }
                current.clear();
            }
        }

        found
    }

    let mut drivers: HashSet<String> = HashSet::new();
    let mut queue: Vec<String> = layout
        .constants
        .iter()
        .chain(&layout.pc_variables)
        .flat_map(|c| [c.scale_expression.as_str(), c.transform_expression.as_str()])
        .filter(|e| !e.is_empty())
        .flat_map(words)
        .collect();

    while let Some(word) = queue.pop() {
        if !drivers.insert(word.clone()) {
            continue;
        }

        // A derived value stands for what it is worked out from.
        if let Some(expression) = layout
            .derived
            .iter()
            .find(|(k, _)| k.to_lowercase() == word)
            .map(|(_, v)| v)
        {
            queue.extend(words(expression));
        }
    }

    drivers
}

/// Refuses an agent write to a constant that other constants are scaled by: a small step there
/// rescales whole tables, and every cell then means something else. Not a matter of confirming; an
/// agent does not change these.
fn scale_driver_refusal(held: &TuneInHand, name: &str) -> Option<AgentRefusal> {
    if name.is_empty() || !scale_drivers(held.tune.layout()).contains(&name.to_lowercase()) {
        return None;
    }

    Some(AgentRefusal::new(
        "this setting rescales other settings",
        format!(
            "\"{name}\" appears in the scale of other settings in this firmware, so changing it changes what \
             every one of their values means, tables included. An agent may not change it; a person can."
        ),
    ))
}

/// Where a cell was before the call: read from the batch's baseline tune.
fn baseline_cell(held: &TuneInHand, baseline: &EcuTune, table: &TuneTable, column: i32, row: i32) -> f64 {
    let read = held
        .definitions
        .iter()
        .find(|d| d.title == table.name)
        .and_then(|d| baseline.table(&d.title, &d.values, &d.x_bins, &d.y_bins));

    match read {
        Some(old) => old.values[(column as usize, row as usize)],
        None => table.values[(column as usize, row as usize)],
    }
}

/// The last item for each target stands; the earlier ones are reported, not sent.
fn dedupe(
    settings: &[ProposedSetting],
    cells: &[ProposedCell],
) -> (Vec<ProposedSetting>, Vec<ProposedCell>, Vec<String>) {
    let mut rejected = Vec::new();

    let key = |s: &ProposedSetting| s.name.trim().to_lowercase();
    let kept_settings: Vec<ProposedSetting> = settings
        .iter()
        .enumerate()
        .filter(|(i, s)| {
            let later = !s.name.trim().is_empty() && settings[i + 1..].iter().any(|t| key(t) == key(s));
            if later {
                rejected.push(format!("{}: superseded by a later item for the same setting", s.name));
            }
            !later
        })
        .map(|(_, s)| s.clone())
        .collect();

    let same = |a: &ProposedCell, b: &ProposedCell| {
        a.column == b.column && a.row == b.row && a.table.trim().to_lowercase() == b.table.trim().to_lowercase()
    };
    let kept_cells: Vec<ProposedCell> = cells
        .iter()
        .enumerate()
        .filter(|(i, c)| {
            let later = cells[i + 1..].iter().any(|d| same(c, d));
            if later {
                rejected.push(format!(
                    "{}[{},{}]: superseded by a later item for the same cell",
                    c.table, c.column, c.row
                ));
            }
            !later
        })
        .map(|(_, c)| c.clone())
        .collect();

    (kept_settings, kept_cells, rejected)
}

/// The runs of bytes where `write` differs from the page the controller holds: the agent's own
/// change and nothing else. Whole when the page cannot be compared (the connection then refuses
/// it for being out of range).
pub(crate) fn diff_runs(held: Option<&Vec<u8>>, write: &TuneWrite) -> Vec<TuneWrite> {
    let (Some(page), Ok(start)) = (held, usize::try_from(write.offset)) else {
        return vec![write.clone()];
    };

    if start + write.data.len() > page.len() {
        return vec![write.clone()];
    }

    let mut runs = Vec::new();
    let mut at = 0;

    while at < write.data.len() {
        if page[start + at] == write.data[at] {
            at += 1;
            continue;
        }

        let from = at;
        while at < write.data.len() && page[start + at] != write.data[at] {
            at += 1;
        }

        runs.push(TuneWrite::new(
            write.page,
            write.offset + from as i32,
            write.data[from..at].to_vec(),
        ));
    }

    runs
}

/// A MaxxECU table write: the cells the edit produced, with the checksum the ECU keeps beside
/// them, cut to the runs that differ from what the ECU holds. Returns the whole record (for the
/// overlap warning) and the runs, ascending, so the checksum, which sits at the end, goes last.
///
/// Refuses, sending nothing, if the table is not one the ECU map knows, or the edit does not line
/// up with it: a table sent without a checksum that matches reads as nought to the ECU.
pub(crate) fn plan_maxx_table(
    held: &TuneInHand,
    tables: &[MaxxTable],
    name: &str,
    encoded: &TuneWrite,
) -> Result<(TuneWrite, Vec<TuneWrite>), String> {
    let Some(table) = tables.iter().find(|t| eq_ignore_case(&t.name, name)) else {
        return Err(
            "This MaxxECU table's checksum cannot be worked out (the table map is not loaded), so nothing was sent."
                .to_string(),
        );
    };

    let count = table.columns * table.rows;

    if usize::try_from(encoded.offset).ok() != Some(table.cells_at) || encoded.data.len() != count * 2 {
        return Err("The edit does not line up with the MaxxECU's table, so nothing was sent.".to_string());
    }

    let Some(page) = held.tune.pages().get(encoded.page.max(0) as usize) else {
        return Err("The tune holds no such page, so nothing was sent.".to_string());
    };

    let cells: Vec<i16> = encoded
        .data
        .as_chunks::<2>()
        .0
        .iter()
        .map(|b| i16::from_le_bytes(*b))
        .collect();

    let record = maxx_tune::record(page, table, &cells).map_err(|e| e.to_string())?;
    let whole = TuneWrite::new(encoded.page, encoded.offset, record);

    Ok((whole.clone(), diff_runs(Some(page), &whole)))
}

/// Refuses a MaxxECU write that would reach past the ceiling, before any piece of anything is
/// sent. (`MaxxTune::write` refuses it again, piece by piece, as the last line of defence.)
fn maxx_ceiling_check(writes: &[TuneWrite]) -> Result<(), String> {
    for write in writes {
        let reach = i64::from(write.offset) + write.data.len() as i64;

        if write.offset < 0 || reach > i64::from(WRITE_CEILING) {
            return Err(format!(
                "A MaxxECU will not be written above {WRITE_CEILING}, and this would reach {reach}."
            ));
        }
    }

    Ok(())
}

/// What a partly-done or failed run of settings writes says, as the C# words it.
fn failure_text(target: Option<&EcuTarget>, sent: &Sent, total: usize, message: &str) -> String {
    if sent.done == 0 {
        return format!("Nothing was sent: {message}");
    }

    match target {
        Some(EcuTarget::Maxx(_)) => format!(
            "{} of {total} writes reached the ECU ({} bytes), verified, and then this failed: {message} \
             Those {} are already applied and permanent \u{2014} there is no burn to skip and no power cycle \
             that undoes them.",
            sent.done,
            nf::int(sent.bytes as i32, "N0"),
            sent.done
        ),
        _ => format!(
            "{} of {total} writes reached the ECU ({} bytes) and then this failed: {message} Some settings \
             have changed and some have not. Nothing was burned, so turning the key off restores all of it.",
            sent.done,
            nf::int(sent.bytes as i32, "N0")
        ),
    }
}

/// One write to a page-protocol controller: through the connection's read-back-and-verify.
fn send_to_page(connection: &EcuConnection, tune: &EcuTune, write: &TuneWrite) -> Result<(), Failure> {
    let layout = tune.layout();

    let page: &TunePage = layout
        .pages
        .iter()
        .find(|p| p.index == write.page)
        .ok_or_else(|| Failure::clean(format!("The firmware declares no page {}.", write.page)))?;

    if page.chunk_write_command.is_empty() {
        return Err(Failure::clean(format!(
            "This firmware declares no way to write page {}.",
            write.page
        )));
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
        .map_err(|e| {
            // A controller that said no, or an argument refused before anything was sent, left
            // nothing behind. Anything else (a lost acknowledgement, a failed read-back) may have.
            if e.refused()
                || matches!(
                    e,
                    olv_live::connection::ecu_connection::EcuError::Other(Error::Argument(_))
                )
            {
                Failure::clean(e.message())
            } else {
                Failure::doubtful(e.message())
            }
        })
}

/// One write to a MaxxECU over USB, in pieces of at most [`MAXIMUM_WRITE`] bytes (all the ECU takes
/// at once), each read back before the next goes. There is no page here and no burn: the bytes land
/// straight at their offset in the one 64 KB blob, applied and permanent the moment the ECU
/// answers Ok. The write ceiling is checked for the whole write before any piece is sent
/// ([`maxx_ceiling_check`]) and again by `MaxxTune::write` for each piece.
#[doc(hidden)]
pub fn send_to_maxx(source: &MaxxUsbSource, write: &TuneWrite) -> Result<(), Failure> {
    let mut at = 0;

    while at < write.data.len() {
        let end = (at + MAXIMUM_WRITE).min(write.data.len());
        let offset = write.offset + at as i32;
        let piece = &write.data[at..end];

        let status = source.write_tune(offset, piece).map_err(|e| match e {
            Error::Argument(_) => Failure::clean(e.to_string()),
            other => Failure::doubtful(other.to_string()),
        })?;

        if status != MaxxWriteStatus::Ok {
            let message = format!(
                "The MaxxECU answered \"{}\" for the write at offset {offset}.",
                status_name(status)
            );

            // Refused outright, or never sent, leaves nothing; an unknown answer may not.
            return Err(match status {
                MaxxWriteStatus::NoAnswer
                | MaxxWriteStatus::UnknownCommand
                | MaxxWriteStatus::OutOfRange
                | MaxxWriteStatus::WriterBusy
                | MaxxWriteStatus::ReaderBusy
                | MaxxWriteStatus::FlashBusy => Failure::clean(message),
                _ => Failure::doubtful(message),
            });
        }

        if !source
            .verify_tune(offset, piece)
            .map_err(|e| Failure::doubtful(e.to_string()))?
        {
            return Err(Failure::doubtful(format!(
                "The MaxxECU took the write at offset {offset} but reads back something else now."
            )));
        }

        at = end;
    }

    Ok(())
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
