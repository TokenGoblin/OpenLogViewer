//! Ported from `LiveSubscriber.cs`. Owned by package `w1-ms-maxx`.
//!
//! One agent watching the live stream.
//!
//! **The newest frame wins.** There is one slot, not a queue: a frame that arrives
//! while the last is still being written replaces it. That is the right trade for
//! this data: an agent asking "what is the engine doing" wants the current answer,
//! and a backlog of stale frames delivered late is worse than a gap. It also means a
//! subscriber can never make the poll thread wait, which is the property that keeps
//! the API from slowing down the thing it is watching.
//!
//! The channel names are sent once, as a schema, and each frame afterwards carries
//! only the numbers in that order. A rusEFI publishes 823 channels; at 25 Hz,
//! repeating their names would be about four megabytes a minute of nothing but
//! spelling.
//!
//! This is the sans-IO half. The C# class also owns a `WebSocket`, a read loop and
//! JSON serialisation; those stay with the agent API (a later package), which does:
//!
//! - call [`LiveSubscriber::offer`] on the poll thread, and wake its writer when that
//!   returns `true` (a wake-up that never queues more than one, like the C#'s
//!   one-slot semaphore);
//! - in the writer, call [`LiveSubscriber::take_next`] after each wake and send each
//!   returned [`Outgoing`] as one text message (camelCase JSON, named floating-point
//!   literals allowed so a `NaN` or infinity does not fail the whole frame);
//! - decode each incoming text message as [`SubscribeRequest`] and pass it to
//!   [`LiveSubscriber::apply_request`], or, if the JSON does not parse, send
//!   [`LiveSubscriber::invalid_request`].

use std::sync::{Arc, Mutex, MutexGuard};

use olv_core::model::eq_ignore_case;

/// What the agent said it wants. `None` fields were absent.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SubscribeRequest {
    pub channels: Option<Vec<String>>,
    pub raw: Option<bool>,
}

/// One message for the agent, in the shape the C# serialises:
/// `{"type":"schema","channels":[..]}`, `{"type":"frame","t":..,"v":[..],"skipped":..}`
/// and `{"type":"error","detail":".."}`.
#[derive(Clone, Debug, PartialEq)]
pub enum Outgoing {
    Schema { channels: Vec<String> },
    Frame { t: f64, v: Vec<f64>, skipped: i32 },
    Error { detail: String },
}

#[derive(Debug, Default)]
struct State {
    /// What the agent asked for, or `None` for everything.
    wanted: Option<Vec<String>>,

    /// Whether this subscriber watches the full, unfiltered channel set rather than
    /// the datalog-named one. A second stream rather than a filter on the same one,
    /// because the two have different schemas: [`LiveSubscriber::offer`] calls for
    /// the stream this subscriber is not watching are simply ignored.
    raw: bool,

    /// Resolved once per schema change rather than per frame. `known_names` is the
    /// C#'s reference-compared name list: `None` forces a resolve.
    indices: Vec<usize>,
    sent: Vec<String>,
    known_names: Option<Arc<[String]>>,

    pending_seconds: f64,
    pending: Option<Vec<f64>>,
    has: bool,

    /// Frames dropped because the agent was still reading the last one.
    skipped: i32,

    schema_sent: bool,
}

#[derive(Debug, Default)]
pub struct LiveSubscriber {
    state: Mutex<State>,
}

impl LiveSubscriber {
    pub fn new() -> Self {
        Self::default()
    }

    fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Takes a frame if there is room for one, and never blocks.
    ///
    /// Runs on the poll thread. Everything here is a lock over an array copy; the
    /// writing happens on this subscriber's own task.
    ///
    /// `names` is compared by identity (the C#'s `ReferenceEquals`), so the poll loop
    /// must hand the same `Arc` every call until the channel list changes. Returns
    /// `true` when the frame was stored and the writer should be woken; `false` when
    /// it was ignored (the other stream, or no channels wanted).
    pub fn offer(&self, raw: bool, seconds: f64, names: &Arc<[String]>, values: &[f64]) -> bool {
        let mut s = self.state();

        if raw != s.raw {
            return false;
        }
        let same = s.known_names.as_ref().is_some_and(|k| Arc::ptr_eq(k, names));
        if !same {
            resolve(&mut s, names);
        }

        if s.indices.is_empty() {
            return false;
        }

        let mut pending = s.pending.take().unwrap_or_else(|| vec![0.0; s.indices.len()]);

        for (i, &at) in s.indices.iter().enumerate() {
            pending[i] = values.get(at).copied().unwrap_or(f64::NAN);
        }
        s.pending = Some(pending);

        s.pending_seconds = seconds;
        if s.has {
            s.skipped += 1;
        }
        s.has = true;

        true
    }

    /// What the writer sends after being woken: the schema if it has not been sent
    /// since the columns last changed, then the newest frame. `None` when there is
    /// nothing new (the C# `continue`s and waits again).
    pub fn take_next(&self) -> Option<Vec<Outgoing>> {
        let mut s = self.state();

        if !s.has {
            return None;
        }
        let frame = s.pending.clone()?;

        let seconds = s.pending_seconds;
        let need_schema = !s.schema_sent;
        s.schema_sent = true;
        let skipped = s.skipped;
        s.skipped = 0;
        s.has = false;

        let mut out = Vec::with_capacity(2);
        if need_schema {
            out.push(Outgoing::Schema {
                channels: s.sent.clone(),
            });
        }
        out.push(Outgoing::Frame {
            t: seconds,
            v: frame,
            skipped,
        });

        Some(out)
    }

    /// Applies the one thing an agent can say: which channels it wants.
    pub fn apply_request(&self, request: &SubscribeRequest) {
        let mut s = self.state();

        if let Some(raw) = request.raw
            && raw != s.raw
        {
            s.raw = raw;

            // A different stream entirely, with its own schema.
            s.known_names = None;
        }

        if let Some(wanted) = &request.channels {
            // "*" or an empty list means everything.
            s.wanted = if wanted.is_empty() || wanted.iter().any(|w| w == "*") {
                None
            } else {
                Some(wanted.clone())
            };

            // Force the columns to be worked out again on the next frame, schema and
            // all.
            s.known_names = None;
        }
    }

    /// The reply to a message that was not valid JSON for a [`SubscribeRequest`].
    pub fn invalid_request() -> Outgoing {
        Outgoing::Error {
            detail: "expected {\"channels\":[\u{2026}],\"raw\":false}".to_string(),
        }
    }
}

/// Works out which columns this agent wants, once per name change.
fn resolve(s: &mut State, names: &Arc<[String]>) {
    s.known_names = Some(Arc::clone(names));

    let mut indices = Vec::new();
    let mut sent = Vec::new();

    for (i, name) in names.iter().enumerate() {
        if let Some(wanted) = &s.wanted
            && !wanted.iter().any(|w| eq_ignore_case(w, name))
        {
            continue;
        }

        indices.push(i);
        sent.push(name.clone());
    }

    s.indices = indices;
    s.sent = sent;
    s.pending = None;
    s.schema_sent = false;
}

#[cfg(test)]
mod tests {
    use super::*;

    fn names(list: &[&str]) -> Arc<[String]> {
        list.iter().map(|s| s.to_string()).collect()
    }

    fn ask(channels: Option<&[&str]>, raw: Option<bool>) -> SubscribeRequest {
        SubscribeRequest {
            channels: channels.map(|c| c.iter().map(|s| s.to_string()).collect()),
            raw,
        }
    }

    #[test]
    fn everything_is_sent_by_default_with_the_schema_first_and_once() {
        let sub = LiveSubscriber::new();
        let n = names(&["RPM", "MAP"]);

        assert!(sub.offer(false, 1.5, &n, &[3000.0, 90.0]));
        assert_eq!(
            sub.take_next().unwrap(),
            vec![
                Outgoing::Schema {
                    channels: vec!["RPM".into(), "MAP".into()]
                },
                Outgoing::Frame {
                    t: 1.5,
                    v: vec![3000.0, 90.0],
                    skipped: 0
                },
            ]
        );
        assert!(sub.take_next().is_none());

        assert!(sub.offer(false, 1.6, &n, &[3100.0, 91.0]));
        assert_eq!(
            sub.take_next().unwrap(),
            vec![Outgoing::Frame {
                t: 1.6,
                v: vec![3100.0, 91.0],
                skipped: 0
            }]
        );
    }

    #[test]
    fn the_newest_frame_wins_and_the_skips_are_counted() {
        let sub = LiveSubscriber::new();
        let n = names(&["A"]);

        sub.offer(false, 1.0, &n, &[1.0]);
        sub.offer(false, 2.0, &n, &[2.0]);
        sub.offer(false, 3.0, &n, &[3.0]);

        let out = sub.take_next().unwrap();
        assert_eq!(
            out[1],
            Outgoing::Frame {
                t: 3.0,
                v: vec![3.0],
                skipped: 2
            }
        );
    }

    #[test]
    fn a_channel_filter_is_case_insensitive_and_missing_values_are_nan() {
        let sub = LiveSubscriber::new();
        sub.apply_request(&ask(Some(&["map", "Nope"]), None));
        let n = names(&["RPM", "MAP"]);

        assert!(sub.offer(false, 0.0, &n, &[3000.0, 90.0]));
        let out = sub.take_next().unwrap();
        assert_eq!(
            out[0],
            Outgoing::Schema {
                channels: vec!["MAP".into()]
            }
        );

        // A values row shorter than the names gives NaN, not a panic.
        sub.apply_request(&ask(Some(&["MAP"]), None));
        assert!(sub.offer(false, 0.0, &n, &[3000.0]));
        match &sub.take_next().unwrap()[1] {
            Outgoing::Frame { v, .. } => assert!(v[0].is_nan()),
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn star_or_empty_means_everything() {
        for all in [&["*"][..], &[][..]] {
            let sub = LiveSubscriber::new();
            sub.apply_request(&ask(Some(&["RPM"]), None));
            sub.apply_request(&ask(Some(all), None));
            let n = names(&["RPM", "MAP"]);
            sub.offer(false, 0.0, &n, &[1.0, 2.0]);
            match &sub.take_next().unwrap()[0] {
                Outgoing::Schema { channels } => assert_eq!(channels.len(), 2),
                other => panic!("{other:?}"),
            }
        }
    }

    #[test]
    fn the_other_stream_is_ignored_and_switching_streams_resends_the_schema() {
        let sub = LiveSubscriber::new();
        let n = names(&["A"]);

        assert!(!sub.offer(true, 0.0, &n, &[1.0]));
        assert!(sub.take_next().is_none());

        sub.offer(false, 0.0, &n, &[1.0]);
        assert_eq!(sub.take_next().unwrap().len(), 2);

        sub.apply_request(&ask(None, Some(true)));
        assert!(!sub.offer(false, 0.0, &n, &[1.0]));
        assert!(sub.offer(true, 0.0, &n, &[1.0]));
        assert_eq!(sub.take_next().unwrap().len(), 2);
    }

    #[test]
    fn a_new_names_list_resolves_again_and_nothing_wanted_stores_nothing() {
        let sub = LiveSubscriber::new();
        sub.apply_request(&ask(Some(&["Z"]), None));
        let n = names(&["A"]);
        assert!(!sub.offer(false, 0.0, &n, &[1.0]));
        assert!(sub.take_next().is_none());

        let n2 = names(&["Z"]);
        assert!(sub.offer(false, 0.0, &n2, &[5.0]));
    }

    #[test]
    fn a_bad_request_is_answered_with_the_documented_error() {
        assert_eq!(
            LiveSubscriber::invalid_request(),
            Outgoing::Error {
                detail: "expected {\"channels\":[\u{2026}],\"raw\":false}".into()
            }
        );
    }
}
