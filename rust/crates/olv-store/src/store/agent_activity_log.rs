//! Ported from `AgentActivityLog.cs`. Owned by package `w1-stores`.
//!
//! Every request an agent has made against this application itself: reading something back,
//! changing something in the ECU's working memory, or writing a file for the person to pick up
//! later (the staging routes). This is what the application then does to the ECU on the agent's
//! behalf is a different log (the wire trace). With no in-app chat panel, this is the only place
//! "is anything talking to this program right now, and is it reading or changing something" can
//! be answered from, so a person watches this rather than a transcript.
//!
//! A bounded ring buffer, for the same reason as the wire trace: this is evidence for the session
//! in progress, and recording it must never make a request wait. One event is recorded when a
//! request starts and a second when it finishes, rather than one row mutated in place, because a
//! subscriber watching for "is anything in flight right now" needs to see both ends.

use std::collections::VecDeque;
use std::sync::Mutex;

use time::OffsetDateTime;

/// What one agent request is doing, in the sense a person watching for it cares about. This
/// describes intent before a request is handled, not how it went.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AgentActivityKind {
    Read,
    Write,
    Stage,
}

/// One request against the agent API, as it started or as it finished.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AgentActivityEvent {
    sequence: i64,
    at: OffsetDateTime,
    route: String,
    kind: AgentActivityKind,
    detail: String,
    in_flight: bool,
}

impl AgentActivityEvent {
    pub fn new(
        sequence: i64,
        at: OffsetDateTime,
        route: impl Into<String>,
        kind: AgentActivityKind,
        detail: impl Into<String>,
        in_flight: bool,
    ) -> Self {
        Self {
            sequence,
            at,
            route: route.into(),
            kind,
            detail: detail.into(),
            in_flight,
        }
    }

    pub fn sequence(&self) -> i64 {
        self.sequence
    }

    /// When the event was recorded, in UTC.
    pub fn at(&self) -> OffsetDateTime {
        self.at
    }

    pub fn route(&self) -> &str {
        &self.route
    }

    pub fn kind(&self) -> AgentActivityKind {
        self.kind
    }

    /// The phrase a person reads, or the bare route when it is not one this table knows.
    pub fn detail(&self) -> &str {
        &self.detail
    }

    pub fn in_flight(&self) -> bool {
        self.in_flight
    }
}

/// Route path, the kind it is, and the phrase a person reads. A flat table rather than anything
/// cleverer: routes are added a handful at a time, and a table anyone can append a line to is
/// easier to keep honest than a rule that infers intent from a path shape.
///
/// `/tune/set`, `/table/set` and `/tune/apply` are the writes: they are the only routes that
/// reach the ECU. `/project/...` routes change the tuning project's own notes, which matters to a
/// person but is not the "the AI just changed something in the engine" event this exists to call
/// out, so they stay reads here. Staging writes a file for somebody to pick up and never touches a
/// controller.
///
/// A route that reaches the ECU and is missing from here reads as a quiet
/// [`AgentActivityKind::Read`], which is exactly the event the indicator exists to make loud.
/// Anything added to the agent server's routes belongs here in the same change.
const ROUTES: &[(&str, AgentActivityKind, &str)] = &[
    ("/", AgentActivityKind::Read, "asking what this API offers"),
    ("/state", AgentActivityKind::Read, "checking connection state"),
    ("/channels", AgentActivityKind::Read, "listing channels"),
    ("/values", AgentActivityKind::Read, "reading channel history"),
    ("/insights", AgentActivityKind::Read, "reviewing the log"),
    ("/tune", AgentActivityKind::Read, "reading the tune"),
    ("/tables", AgentActivityKind::Read, "listing tables"),
    ("/table", AgentActivityKind::Read, "reading a table"),
    ("/tune/full", AgentActivityKind::Read, "reading the whole tune"),
    ("/log/full", AgentActivityKind::Read, "reading the whole log"),
    ("/context", AgentActivityKind::Read, "catching up on this vehicle"),
    (
        "/wire/health",
        AgentActivityKind::Read,
        "checking the wire trace's health",
    ),
    ("/wire/events", AgentActivityKind::Read, "reading the wire trace"),
    ("/live/stream", AgentActivityKind::Read, "watching the live stream"),
    ("/project", AgentActivityKind::Read, "reading the tuning project"),
    (
        "/project/versions/compare",
        AgentActivityKind::Read,
        "comparing tune versions",
    ),
    ("/project/record", AgentActivityKind::Read, "recording a sitting"),
    ("/project/keep", AgentActivityKind::Read, "keeping this tune version"),
    ("/project/fix", AgentActivityKind::Read, "noting a fix"),
    (
        "/tune/propose",
        AgentActivityKind::Read,
        "working out what a change would do",
    ),
    ("/tune/set", AgentActivityKind::Write, "writing to the ECU"),
    ("/table/set", AgentActivityKind::Write, "writing to the ECU"),
    (
        "/tune/apply",
        AgentActivityKind::Write,
        "applying a batch of changes to the ECU",
    ),
    ("/stage", AgentActivityKind::Read, "listing staged files"),
    ("/stage/tune", AgentActivityKind::Stage, "staging a tune file"),
    ("/stage/table", AgentActivityKind::Stage, "staging a table as CSV"),
    (
        "/definitions/needed",
        AgentActivityKind::Read,
        "checking which firmware definition is missing",
    ),
    (
        "/definitions/import",
        AgentActivityKind::Stage,
        "installing a firmware definition",
    ),
];

/// Receives each event as it is recorded. Called on whichever thread recorded it, which is the
/// agent's own request thread and not the interface thread.
type Subscriber = Box<dyn Fn(&AgentActivityEvent) + Send + Sync>;

#[derive(Default)]
struct LogState {
    events: VecDeque<AgentActivityEvent>,
    sequence: i64,
}

pub struct AgentActivityLog {
    capacity: usize,
    state: Mutex<LogState>,
    subscribers: Mutex<Vec<Subscriber>>,
}

impl AgentActivityLog {
    /// A log that keeps the most recent 500 events.
    pub fn new() -> Self {
        Self::with_capacity(500)
    }

    pub fn with_capacity(capacity: usize) -> Self {
        Self {
            capacity,
            state: Mutex::new(LogState::default()),
            subscribers: Mutex::new(Vec::new()),
        }
    }

    /// Adds a subscriber, called once for each event recorded from now on.
    ///
    /// Subscribers are called while the subscriber list is held, so one must not subscribe again
    /// from inside its own call.
    pub fn subscribe(&self, subscriber: impl Fn(&AgentActivityEvent) + Send + Sync + 'static) {
        self.subscribers().push(Box::new(subscriber));
    }

    /// Marks one request as started. Dropping the returned handle (or calling
    /// [`ActivityHandle::finish`]) records the matching end, so the whole of handling a request
    /// is wrapped in it. An unrecognised route is treated as a read, the safe assumption for a
    /// route the table has not caught up with yet.
    pub fn begin(&self, route: &str) -> ActivityHandle<'_> {
        let (kind, detail) = match ROUTES.iter().find(|(known, _, _)| *known == route) {
            Some(&(_, kind, phrase)) => (kind, phrase.to_string()),
            None => (AgentActivityKind::Read, route.to_string()),
        };

        self.record(route, kind, &detail, true);

        ActivityHandle {
            owner: self,
            route: route.to_string(),
            kind,
            detail,
            open: true,
        }
    }

    /// The most recent events, newest first.
    pub fn recent(&self, count: usize) -> Vec<AgentActivityEvent> {
        let state = self.state();
        state.events.iter().rev().take(count).cloned().collect()
    }

    fn record(&self, route: &str, kind: AgentActivityKind, detail: &str, in_flight: bool) {
        let event = {
            let mut state = self.state();
            state.sequence += 1;

            let event = AgentActivityEvent::new(
                state.sequence,
                OffsetDateTime::now_utc(),
                route,
                kind,
                detail,
                in_flight,
            );

            state.events.push_back(event.clone());
            while state.events.len() > self.capacity {
                state.events.pop_front();
            }

            event
        };

        for subscriber in self.subscribers().iter() {
            subscriber(&event);
        }
    }

    fn state(&self) -> std::sync::MutexGuard<'_, LogState> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn subscribers(&self) -> std::sync::MutexGuard<'_, Vec<Subscriber>> {
        self.subscribers.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

impl Default for AgentActivityLog {
    fn default() -> Self {
        Self::new()
    }
}

/// The open end of one request. Dropping it records the end; [`finish`](Self::finish) does so
/// early, and does nothing when called again.
pub struct ActivityHandle<'a> {
    owner: &'a AgentActivityLog,
    route: String,
    kind: AgentActivityKind,
    detail: String,
    open: bool,
}

impl ActivityHandle<'_> {
    /// Records the end of the request, once.
    pub fn finish(&mut self) {
        if !self.open {
            return;
        }

        self.open = false;
        self.owner.record(&self.route, self.kind, &self.detail, false);
    }
}

impl Drop for ActivityHandle<'_> {
    fn drop(&mut self) {
        self.finish();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;

    // ----- the ring buffer itself ------------------------------------------------

    #[test]
    fn old_events_are_evicted_once_capacity_is_reached() {
        let log = AgentActivityLog::with_capacity(3);

        for i in 0..5 {
            log.begin(&format!("/route{i}")).finish();
        }

        // Two events per begin/finish pair, so five calls leave ten events; only the last three
        // of those survive.
        assert_eq!(log.recent(10).len(), 3);
    }

    #[test]
    fn recent_asks_for_more_than_exist_returns_what_there_is() {
        let log = AgentActivityLog::new();
        log.begin("/state").finish();

        // One begin/finish pair is two events: the start and the end.
        assert_eq!(log.recent(50).len(), 2);
    }

    #[test]
    fn an_empty_log_has_nothing_to_offer() {
        let log = AgentActivityLog::new();

        assert!(log.recent(10).is_empty());
    }

    // ----- begin/end and in-flight -----------------------------------------------

    #[test]
    fn begin_records_an_in_flight_event_immediately() {
        let log = AgentActivityLog::new();

        let _handle = log.begin("/state");

        let events = log.recent(10);
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].route(), "/state");
        assert!(events[0].in_flight());
    }

    #[test]
    fn finishing_records_a_matching_event_that_is_no_longer_in_flight() {
        let log = AgentActivityLog::new();

        log.begin("/state").finish();

        let events = log.recent(10);
        assert_eq!(events.len(), 2);

        let ended = &events[0]; // newest first
        let started = &events[1];

        assert!(!ended.in_flight());
        assert!(started.in_flight());
        assert_eq!(started.route(), ended.route());
        assert_eq!(started.kind(), ended.kind());
    }

    #[test]
    fn finishing_twice_records_the_end_only_once() {
        let log = AgentActivityLog::new();

        let mut handle = log.begin("/state");
        handle.finish();
        handle.finish();

        assert_eq!(log.recent(10).len(), 2);
    }

    // ----- read vs write classification ------------------------------------------

    #[test]
    fn routes_that_reach_the_ecu_are_classified_as_writes() {
        for route in ["/tune/set", "/table/set"] {
            let log = AgentActivityLog::new();

            let _handle = log.begin(route);

            assert_eq!(log.recent(1)[0].kind(), AgentActivityKind::Write, "{route}");
        }
    }

    #[test]
    fn every_other_known_route_is_classified_as_a_read() {
        let routes = [
            "/state",
            "/channels",
            "/insights",
            "/tune",
            "/tables",
            "/table",
            "/wire/health",
            "/wire/events",
            "/project",
            "/project/record",
            "/project/keep",
            "/project/fix",
        ];

        for route in routes {
            let log = AgentActivityLog::new();

            let _handle = log.begin(route);

            assert_eq!(log.recent(1)[0].kind(), AgentActivityKind::Read, "{route}");
        }
    }

    #[test]
    fn an_unrecognised_route_is_treated_as_a_read_rather_than_throwing() {
        let log = AgentActivityLog::new();

        let _handle = log.begin("/some/future/route");

        let event = &log.recent(1)[0];
        assert_eq!(event.kind(), AgentActivityKind::Read);
        assert_eq!(event.detail(), "/some/future/route");
    }

    #[test]
    fn known_routes_carry_a_human_phrase_rather_than_the_bare_path() {
        let log = AgentActivityLog::new();

        let _handle = log.begin("/tune/set");

        assert_eq!(log.recent(1)[0].detail(), "writing to the ECU");
    }

    // ----- notifying a subscriber ------------------------------------------------

    #[test]
    fn changed_fires_once_for_the_start_and_once_for_the_end() {
        let log = AgentActivityLog::new();
        let seen = Arc::new(Mutex::new(Vec::new()));

        let sink = Arc::clone(&seen);
        log.subscribe(move |event| sink.lock().unwrap().push(event.in_flight()));

        log.begin("/state").finish();

        assert_eq!(*seen.lock().unwrap(), [true, false]);
    }
}
