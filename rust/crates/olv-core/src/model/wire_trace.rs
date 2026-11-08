//! Ported from `WireTrace.cs`: what actually went over the wire to an ECU.

use std::cell::Cell;
use std::collections::VecDeque;
use std::sync::Mutex;
use std::time::{Duration, SystemTime};

/// Why one attempt of a wire exchange failed. `None` is a success.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum WireFailureKind {
    #[default]
    None,
    Timeout,
    ShortReply,
    ChecksumMismatch,
    Malformed,
    Refused,
    TransportError,
}

impl WireFailureKind {
    /// The C#'s `ToString()`: the variant's name.
    pub fn name(self) -> &'static str {
        match self {
            WireFailureKind::None => "None",
            WireFailureKind::Timeout => "Timeout",
            WireFailureKind::ShortReply => "ShortReply",
            WireFailureKind::ChecksumMismatch => "ChecksumMismatch",
            WireFailureKind::Malformed => "Malformed",
            WireFailureKind::Refused => "Refused",
            WireFailureKind::TransportError => "TransportError",
        }
    }
}

/// Whether the code about to talk to the ECU is doing so on a human's behalf or
/// an agent's.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum WireOrigin {
    #[default]
    Human,
    Agent,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum WireOutcome {
    #[default]
    Ok,
    Failure,
}

/// One attempt of one request, as it actually happened.
#[derive(Clone, Debug, PartialEq)]
pub struct WireEvent {
    pub sequence: i64,
    /// Wall-clock time of the attempt (the C#'s `DateTime.UtcNow`).
    pub at: SystemTime,
    pub context: String,
    pub origin: WireOrigin,
    pub attempt: i32,
    pub elapsed: Duration,
    pub outcome: WireOutcome,
    pub failure_kind: WireFailureKind,
    pub detail: String,
    pub request_bytes: i32,
    pub reply_bytes: i32,
}

/// What [`WireTrace::record`] takes: the C#'s `Record` parameters, as a struct
/// so the C#'s defaults are `..Default::default()`.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct WireAttempt {
    pub context: String,
    pub attempt: i32,
    pub elapsed: Duration,
    pub outcome: WireOutcome,
    pub failure_kind: WireFailureKind,
    pub detail: String,
    pub request_bytes: i32,
    pub reply_bytes: i32,
}

/// A rollup over recent events, for "is the link healthy" rather than "what
/// happened at 10:04:12".
#[derive(Clone, Debug, PartialEq)]
pub struct WireHealthSnapshot {
    pub sampled: usize,
    pub failures: usize,
    pub success_rate: f64,
    /// Failures counted by [`WireFailureKind::name`], in the order each kind was
    /// first met newest-first, as the C#'s dictionary enumerates.
    pub failures_by_kind: Vec<(String, usize)>,
    pub since_last_success: Option<Duration>,
    pub longest_recent: Option<Duration>,
}

thread_local! {
    static AMBIENT: Cell<WireOrigin> = const { Cell::new(WireOrigin::Human) };
}

/// Marks whether the current thread of work is on a human's behalf or an agent's,
/// for anything recorded against [`WireTrace`] while it is active.
///
/// An ambient flag rather than a parameter threaded through every read and write,
/// because the distinction only matters at the two places code asks the ECU for
/// something (a UI command or an agent bridge call) and not to any of the
/// protocol code in between. The C# uses `AsyncLocal`, which follows an `await`
/// onto another thread; this is thread-local, so work handed to another thread
/// must enter the scope again there.
pub struct WireOriginGuard {
    previous: WireOrigin,
}

impl WireOrigin {
    /// The origin in force on this thread. Defaults to `Human`: an agent has to
    /// say otherwise.
    pub fn current() -> WireOrigin {
        AMBIENT.with(Cell::get)
    }

    /// `WireOriginScope.Enter`: in force until the guard is dropped.
    pub fn enter(origin: WireOrigin) -> WireOriginGuard {
        let previous = AMBIENT.with(|a| a.replace(origin));
        WireOriginGuard { previous }
    }
}

impl Drop for WireOriginGuard {
    fn drop(&mut self) {
        AMBIENT.with(|a| a.set(self.previous));
    }
}

/// Every request attempt to an ECU, its timing, and how it ended.
///
/// A bounded ring buffer rather than a file, because this is evidence for a
/// session in progress, not a permanent record, and never blocking the request
/// path it is watching matters more than keeping everything forever. Recording
/// takes a short lock and never waits on a reader for long.
#[derive(Debug)]
pub struct WireTrace {
    capacity: usize,
    state: Mutex<State>,
}

#[derive(Debug, Default)]
struct State {
    events: VecDeque<WireEvent>,
    sequence: i64,
    last_success: Option<SystemTime>,
}

impl Default for WireTrace {
    /// The C#'s default capacity, 2000.
    fn default() -> Self {
        Self::new(2000)
    }
}

impl WireTrace {
    pub fn new(capacity: usize) -> Self {
        Self {
            capacity,
            state: Mutex::new(State::default()),
        }
    }

    fn state(&self) -> std::sync::MutexGuard<'_, State> {
        // A panic elsewhere while holding the lock leaves nothing half-written
        // that matters to a diagnostic log, so carry on with it.
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Records one attempt, stamped now and with this thread's [`WireOrigin`].
    pub fn record(&self, attempt: WireAttempt) {
        let mut state = self.state();
        state.sequence += 1;
        let event = WireEvent {
            sequence: state.sequence,
            at: SystemTime::now(),
            context: attempt.context,
            origin: WireOrigin::current(),
            attempt: attempt.attempt,
            elapsed: attempt.elapsed,
            outcome: attempt.outcome,
            failure_kind: attempt.failure_kind,
            detail: attempt.detail,
            request_bytes: attempt.request_bytes,
            reply_bytes: attempt.reply_bytes,
        };

        if event.outcome == WireOutcome::Ok {
            state.last_success = Some(event.at);
        }
        state.events.push_back(event);

        while state.events.len() > self.capacity {
            state.events.pop_front();
        }
    }

    /// The most recent events, newest first.
    pub fn recent(&self, count: usize) -> Vec<WireEvent> {
        let state = self.state();
        state.events.iter().rev().take(count).cloned().collect()
    }

    /// A health rollup over the last `window` events (the C#'s default is 200).
    pub fn health(&self, window: usize) -> WireHealthSnapshot {
        let recent = self.recent(window);

        let mut failures = 0usize;
        let mut longest = Duration::ZERO;
        let mut by_kind: Vec<(String, usize)> = Vec::new();

        for e in &recent {
            if e.elapsed > longest {
                longest = e.elapsed;
            }

            if e.outcome != WireOutcome::Failure {
                continue;
            }

            failures += 1;
            let key = e.failure_kind.name();
            match by_kind.iter_mut().find(|(k, _)| k == key) {
                Some((_, n)) => *n += 1,
                None => by_kind.push((key.to_string(), 1)),
            }
        }

        // A clock stepped backwards gives zero rather than the C#'s negative span.
        let since_last_success = self
            .state()
            .last_success
            .map(|at| SystemTime::now().duration_since(at).unwrap_or_default());

        let success_rate = if recent.is_empty() {
            1.0
        } else {
            1.0 - failures as f64 / recent.len() as f64
        };

        WireHealthSnapshot {
            sampled: recent.len(),
            failures,
            success_rate,
            failures_by_kind: by_kind,
            since_last_success,
            longest_recent: (!recent.is_empty()).then_some(longest),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn failure(kind: WireFailureKind, ms: u64) -> WireAttempt {
        WireAttempt {
            context: "read".into(),
            attempt: 1,
            elapsed: Duration::from_millis(ms),
            outcome: WireOutcome::Failure,
            failure_kind: kind,
            ..Default::default()
        }
    }

    #[test]
    fn the_buffer_is_bounded_and_newest_first() {
        let trace = WireTrace::new(3);
        for i in 0..5 {
            trace.record(WireAttempt {
                attempt: i,
                ..Default::default()
            });
        }
        let recent = trace.recent(10);
        assert_eq!(recent.iter().map(|e| e.attempt).collect::<Vec<_>>(), [4, 3, 2]);
        assert_eq!(recent[0].sequence, 5);
    }

    #[test]
    fn health_counts_failures_by_kind() {
        let trace = WireTrace::default();
        assert_eq!(trace.health(200).success_rate, 1.0);
        assert_eq!(trace.health(200).longest_recent, None);

        trace.record(WireAttempt::default());
        trace.record(failure(WireFailureKind::Timeout, 50));
        trace.record(failure(WireFailureKind::ShortReply, 10));
        trace.record(failure(WireFailureKind::Timeout, 20));

        let health = trace.health(200);
        assert_eq!((health.sampled, health.failures), (4, 3));
        assert_eq!(health.success_rate, 0.25);
        assert_eq!(
            health.failures_by_kind,
            [("Timeout".to_string(), 2), ("ShortReply".to_string(), 1)]
        );
        assert_eq!(health.longest_recent, Some(Duration::from_millis(50)));
        assert!(health.since_last_success.is_some());
    }

    #[test]
    fn the_origin_scope_marks_events_and_restores() {
        let trace = WireTrace::default();
        {
            let _agent = WireOrigin::enter(WireOrigin::Agent);
            trace.record(WireAttempt::default());
        }
        trace.record(WireAttempt::default());
        let recent = trace.recent(2);
        assert_eq!(recent[1].origin, WireOrigin::Agent);
        assert_eq!(recent[0].origin, WireOrigin::Human);
    }
}
