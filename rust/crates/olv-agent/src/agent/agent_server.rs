//! Ported from `AgentServer.cs`. Owned by package `w5-agent`.
//!
//! A small local server so an agent can watch and question a live session.
//!
//! **Loopback only, always.** The listener is bound to 127.0.0.1 and there is no setting that
//! changes it. A tuning application that can write to an engine has no business accepting a
//! connection from another machine, and the way that mistake normally happens is a "host"
//! option that somebody sets to 0.0.0.0 to make something work.
//!
//! **HTTP for questions, one WebSocket for the answer that never stops.** Anything an agent asks
//! once (what is connected, what channels exist, what the insights say) is a request. Live data
//! is a subscription, pushed as the ECU produces it rather than polled, because polling a 25 Hz
//! source over HTTP is the wrong tool.
//!
//! Every request carries the token, as `Authorization: Bearer ...` or `?token=`. Binding to the
//! loopback address keeps other machines out; it does not keep out other programs on this one,
//! and a browser tab on any page can reach a localhost port. The token is what stands between a
//! web page you happened to open and a socket that can move numbers in an engine. It is compared
//! in constant time, and never written to a log, a message or a `Debug` print.
//!
//! One thread per connection, as the C# gives each connection its own task: a slow agent cannot
//! hold up the next, and a subscriber sitting on a socket for an hour does not stop anything else
//! being asked. A cap on connections (the C# has none) keeps a flood from becoming a flood of
//! threads.
//!
//! **Differences from the C# that make it stricter, never looser:**
//! - a request whose `Host` or `Origin` names anything but a loopback address is refused with 403
//!   before the token is even looked at. The C# accepts any `Host`; with a token required that is
//!   survivable, but it leaves DNS rebinding as a way for a web page to reach the socket, and
//!   nothing legitimate (the MCP server, `curl`, a script) sends such a header;
//! - connections, request heads, bodies and idle time are all bounded.

use std::io::{self, Read, Write};
use std::net::{Ipv4Addr, Shutdown, SocketAddr, TcpListener, TcpStream};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use olv_core::model::{TuneTable, WireOrigin};
use olv_core::{Error, Result, fmt as nf};
use olv_protocol::ms::live_subscriber::{LiveSubscriber, Outgoing, SubscribeRequest};
use olv_store::store::agent_activity_log::AgentActivityLog;
use tungstenite::Message;
use tungstenite::protocol::{Role, WebSocketConfig};

use super::agent_api::{
    AgentBridge, AgentRefusal, AgentStageResult, AgentTable, ProposedCell, ProposedSetting, describe_difference,
};
use super::http::{self, Head, HttpError};
use super::json::{self, J, JsonError, Reader};

/// How the agent API is set up, if it is set up at all.
#[derive(Clone)]
pub struct AgentServerSettings {
    /// The port to listen on. Zero asks the operating system for a free one, which is what the
    /// tests use.
    pub port: u16,

    /// The token every request must carry, as `Authorization: Bearer ...` or `?token=`.
    ///
    /// Not optional and not for show. Binding to the loopback address keeps other machines out;
    /// it does not keep out other programs on this one.
    pub token: String,

    /// How many live subscribers to allow at once.
    pub maximum_clients: usize,

    /// The longest a client may take to send a whole request, or to read the answer. A client
    /// that sends a byte at a time is cut off at this, not after each byte.
    pub request_timeout: Duration,
}

impl AgentServerSettings {
    /// The C#'s defaults: port 8765 and eight subscribers.
    pub fn new(token: impl Into<String>) -> Self {
        Self {
            port: 8765,
            token: token.into(),
            maximum_clients: 8,
            request_timeout: IO_TIMEOUT,
        }
    }
}

// Written by hand so that a settings value can be printed or logged without the token in it.
impl std::fmt::Debug for AgentServerSettings {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AgentServerSettings")
            .field("port", &self.port)
            .field("token", &"<redacted>")
            .field("maximum_clients", &self.maximum_clients)
            .field("request_timeout", &self.request_timeout)
            .finish()
    }
}

/// How many connections may be open at once, subscribers included.
const MAX_CONNECTIONS: usize = 128;

/// How long a client may take to send a head or a body, or to read an answer.
const IO_TIMEOUT: Duration = Duration::from_secs(30);

/// How often a subscriber's thread looks for a new frame and for a request from the agent.
const POLL: Duration = Duration::from_millis(10);

/// The largest message an agent may send on the stream. Its only request is a channel list.
const MAX_STREAM_MESSAGE: usize = 256 * 1024;

struct Shared {
    bridge: Arc<dyn AgentBridge>,
    token: String,
    maximum_clients: usize,
    request_timeout: Duration,
    stopping: AtomicBool,
    subscribers: Mutex<Vec<Arc<LiveSubscriber>>>,
    activity: AgentActivityLog,
    connections: AtomicUsize,
}

impl Shared {
    fn subscribers(&self) -> MutexGuard<'_, Vec<Arc<LiveSubscriber>>> {
        self.subscribers.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

pub struct AgentServer {
    shared: Arc<Shared>,
    port: u16,
    running: bool,
    accept: Option<JoinHandle<()>>,
}

impl AgentServer {
    /// Errors, as the C# throws, when the token is blank: rather than starting a server nobody
    /// can use, or worse, one anybody can.
    pub fn new(bridge: Arc<dyn AgentBridge>, settings: AgentServerSettings) -> Result<Self> {
        if settings.token.trim().is_empty() {
            return Err(Error::Argument(
                "The agent API cannot be served without a token.".to_string(),
            ));
        }

        Ok(Self {
            port: settings.port,
            shared: Arc::new(Shared {
                bridge,
                token: settings.token,
                maximum_clients: settings.maximum_clients,
                request_timeout: settings.request_timeout,
                stopping: AtomicBool::new(false),
                subscribers: Mutex::new(Vec::new()),
                activity: AgentActivityLog::new(),
                connections: AtomicUsize::new(0),
            }),
            running: false,
            accept: None,
        })
    }

    /// The port actually listening, which is settled once started.
    pub fn port(&self) -> u16 {
        self.port
    }

    pub fn is_running(&self) -> bool {
        self.running
    }

    /// Where an agent points itself.
    pub fn address(&self) -> String {
        format!("http://127.0.0.1:{}", self.port)
    }

    /// How many agents are watching the live stream.
    pub fn subscribers(&self) -> usize {
        self.shared.subscribers().len()
    }

    /// Every request an agent has made against this server, for a person at the machine to
    /// watch. Owned here rather than by the view model, because routing is the one chokepoint
    /// every request already passes through; the application only subscribes to it once this
    /// server starts.
    pub fn activity(&self) -> &AgentActivityLog {
        &self.shared.activity
    }

    /// Binds the loopback address and starts answering. Fails, as the C#'s `Start` does, when the
    /// port is taken.
    pub fn start(&mut self) -> Result<()> {
        if self.running {
            return Ok(());
        }

        // The address is a constant: loopback, never a setting.
        let listener = TcpListener::bind(SocketAddr::from((Ipv4Addr::LOCALHOST, self.port)))?;
        self.port = listener.local_addr()?.port();

        let shared = Arc::clone(&self.shared);
        self.accept = Some(
            thread::Builder::new()
                .name("olv-agent-accept".to_string())
                .spawn(move || accept_loop(listener, shared))?,
        );
        self.running = true;

        Ok(())
    }

    /// Hands each frame to every subscriber as it arrives.
    ///
    /// Called from whatever thread the live session polls on, and deliberately does not wait for
    /// anybody: a subscriber that cannot keep up is skipped for that frame rather than allowed to
    /// slow the poll. The ECU sets the pace here, not the slowest reader of it.
    ///
    /// `names` is compared by identity: hand the same `Arc` every call until the channel list
    /// changes.
    pub fn publish(&self, seconds: f64, names: &Arc<[String]>, values: &[f64]) {
        self.fan_out(false, seconds, names, values);
    }

    /// The same fan-out as [`publish`](Self::publish), for the full unfiltered channel set. A
    /// separate call rather than a parameter on one, because the two carry different schemas:
    /// a subscriber ignores whichever of the two it did not ask for.
    pub fn publish_raw(&self, seconds: f64, names: &Arc<[String]>, values: &[f64]) {
        self.fan_out(true, seconds, names, values);
    }

    fn fan_out(&self, raw: bool, seconds: f64, names: &Arc<[String]>, values: &[f64]) {
        let watching: Vec<Arc<LiveSubscriber>> = self.shared.subscribers().clone();

        for subscriber in watching {
            subscriber.offer(raw, seconds, names, values);
        }
    }

    /// Stops answering and closes every subscriber.
    pub fn dispose(&mut self) {
        if !self.running && self.shared.stopping.load(Ordering::SeqCst) {
            return;
        }

        self.shared.stopping.store(true, Ordering::SeqCst);

        // The accept loop is parked in `accept`; a connection to ourselves wakes it to see the
        // flag.
        if self.running {
            let _ = TcpStream::connect_timeout(
                &SocketAddr::from((Ipv4Addr::LOCALHOST, self.port)),
                Duration::from_secs(1),
            );
        }

        if let Some(handle) = self.accept.take() {
            let _ = handle.join();
        }

        // Each subscriber's thread sees the flag within one poll and closes its socket.
        self.running = false;
    }
}

impl Drop for AgentServer {
    fn drop(&mut self) {
        self.dispose();
    }
}

fn accept_loop(listener: TcpListener, shared: Arc<Shared>) {
    for incoming in listener.incoming() {
        if shared.stopping.load(Ordering::SeqCst) {
            return;
        }

        let Ok(stream) = incoming else {
            continue;
        };

        if shared.connections.fetch_add(1, Ordering::SeqCst) >= MAX_CONNECTIONS {
            shared.connections.fetch_sub(1, Ordering::SeqCst);
            drop(stream);
            continue;
        }

        let handler = Arc::clone(&shared);
        let spawned = thread::Builder::new()
            .name("olv-agent-conn".to_string())
            .spawn(move || {
                handle(&handler, stream);
                handler.connections.fetch_sub(1, Ordering::SeqCst);
            });

        if spawned.is_err() {
            shared.connections.fetch_sub(1, Ordering::SeqCst);
        }
    }
}

/// A socket that gives up at a fixed moment, however slowly the other end drips its bytes: the
/// read timeout alone restarts with every byte, which lets a client hold a thread for as long as
/// it likes at one byte a timeout.
struct Bounded<'a> {
    stream: &'a mut TcpStream,
    deadline: Instant,
}

impl<'a> Bounded<'a> {
    fn new(stream: &'a mut TcpStream, deadline: Instant) -> Self {
        Self { stream, deadline }
    }
}

impl Read for Bounded<'_> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let left = self.deadline.saturating_duration_since(Instant::now());

        if left.is_zero() {
            return Err(io::ErrorKind::TimedOut.into());
        }

        self.stream.set_read_timeout(Some(left))?;
        self.stream.read(buf)
    }
}

/// What a route answers with.
struct Reply {
    status: u16,
    body: String,
}

impl Reply {
    fn ok(payload: J) -> Reply {
        Reply {
            status: 200,
            body: payload.text(),
        }
    }

    fn refuse(status: u16, reason: &str, detail: &str) -> Reply {
        Reply {
            status,
            body: J::obj([("error", J::s(reason)), ("detail", J::s(detail))]).text(),
        }
    }

    /// A refusal from the bridge: always a 409, with its reason and detail.
    fn conflict(refusal: &AgentRefusal) -> Reply {
        Reply::refuse(409, &refusal.reason, &refusal.detail)
    }
}

fn handle(shared: &Arc<Shared>, mut stream: TcpStream) {
    // Every request that reaches here is, by construction, an agent asking for something: this
    // is the one chokepoint every route and the WebSocket both pass through. Anything downstream
    // that ends up talking to an ECU is tagged accordingly in the wire trace, which is what tells
    // "the AI asked for this" apart from a person clicking a button, since the two look identical
    // on the wire otherwise.
    let _origin = WireOrigin::enter(WireOrigin::Agent);

    let _ = stream.set_read_timeout(Some(shared.request_timeout));
    let _ = stream.set_write_timeout(Some(shared.request_timeout));
    let _ = stream.set_nodelay(true);

    let outcome = catch_unwind(AssertUnwindSafe(|| serve(shared, &mut stream)));

    if let Err(panic) = outcome {
        // The C# catches any exception from a route and answers 500 with its message.
        let message = panic
            .downcast_ref::<String>()
            .cloned()
            .or_else(|| panic.downcast_ref::<&str>().map(|s| (*s).to_string()))
            .unwrap_or_else(|| "the application failed".to_string());

        let reply = Reply::refuse(500, "the application failed to answer", &message);
        let _ = http::respond_json(&mut stream, reply.status, &reply.body);
    }

    let _ = stream.shutdown(Shutdown::Both);
}

fn serve(shared: &Arc<Shared>, stream: &mut TcpStream) {
    let deadline = Instant::now() + shared.request_timeout;

    let (head, leftover) = match http::read_head(&mut Bounded::new(stream, deadline)) {
        Ok(parsed) => parsed,
        Err(HttpError::Bad(status, reason)) => {
            let reply = Reply::refuse(status, reason, "");
            let _ = http::respond_json(stream, reply.status, &reply.body);
            return;
        }
        Err(_) => return,
    };

    // Before anything else is looked at, including the token: a request that names some other
    // site as its Host or Origin is a web page reaching for this socket, and has no business here
    // however it came by the token.
    if let Some(why) = foreign(&head) {
        let reply = Reply::refuse(403, "forbidden", why);
        let _ = http::respond_json(stream, reply.status, &reply.body);
        return;
    }

    if !authorised(shared, &head) {
        let reply = Reply::refuse(
            401,
            "unauthorised",
            "Pass the token as an Authorization: Bearer header, or ?token=",
        );
        let _ = http::respond_json(stream, reply.status, &reply.body);
        return;
    }

    let path = head.path();

    if head.is_websocket_request() {
        if path == "/live/stream" {
            // Recorded for as long as the socket is open, rather than for the instant it was
            // accepted: an agent watching the engine for an hour is continuously reading it, and
            // an indicator that went dark a moment after it subscribed would say the opposite of
            // what is happening.
            let _watching = shared.activity.begin(&path);
            stream_live(shared, stream, &head, leftover);
        } else {
            let reply = Reply::refuse(404, "no such stream", &path);
            let _ = http::respond_json(stream, reply.status, &reply.body);
        }

        return;
    }

    // Wraps the whole of handling this request, so a person watching the activity log sees both
    // "a request for this route just arrived" and, once every return path has run, "and it just
    // finished".
    let reply = {
        let _activity = shared.activity.begin(&path);
        route(shared, &mut Bounded::new(stream, deadline), &head, leftover, &path)
    };

    let _ = http::respond_json(stream, reply.status, &reply.body);
}

// ----- who may ask -------------------------------------------------------------------------

/// Why a request is from somewhere other than this machine, or None when it is not.
fn foreign(head: &Head) -> Option<&'static str> {
    if let Some(host) = head.header("Host")
        && !is_loopback_authority(host)
    {
        return Some("The Host header does not name the loopback address.");
    }

    if let Some(origin) = head.header("Origin") {
        let authority = origin.split_once("://").map_or("", |(_, rest)| rest);
        let authority = authority.split('/').next().unwrap_or("");

        if !is_loopback_authority(authority) {
            return Some("The Origin header does not name the loopback address.");
        }
    }

    None
}

/// `127.0.0.1`, `localhost` or `[::1]`, with or without a port.
fn is_loopback_authority(authority: &str) -> bool {
    let authority = authority.trim();

    let host = if let Some(rest) = authority.strip_prefix('[') {
        match rest.split_once(']') {
            Some((inside, _)) => inside,
            None => return false,
        }
    } else {
        authority.split(':').next().unwrap_or("")
    };

    host == "127.0.0.1" || host == "::1" || host.eq_ignore_ascii_case("localhost")
}

fn authorised(shared: &Shared, head: &Head) -> bool {
    // Repeated headers arrive joined with a comma, as `HttpListenerRequest.Headers` does.
    let joined: Vec<&str> = head
        .headers
        .iter()
        .filter(|(k, _)| k.eq_ignore_ascii_case("Authorization"))
        .map(|(_, v)| v.as_str())
        .collect();

    if !joined.is_empty() {
        let header = joined.join(", ");

        if header.len() >= 7
            && header.is_char_boundary(7)
            && header.as_bytes()[..7].eq_ignore_ascii_case(b"Bearer ")
            && same(Some(header[7..].trim()), &shared.token)
        {
            return true;
        }
    }

    same(head.query().get("token").as_deref(), &shared.token)
}

/// Compared without giving away where two tokens start to differ.
///
/// A plain string comparison stops at the first wrong character, and the time it took says how
/// much of a guess was right. Over a loopback socket that is a thin channel, but it costs nothing
/// to close.
fn same(offered: Option<&str>, expected: &str) -> bool {
    let Some(offered) = offered else {
        return false;
    };

    if offered.len() != expected.len() {
        return false;
    }

    let mut differences: u8 = 0;
    for (a, b) in offered.bytes().zip(expected.bytes()) {
        differences |= a ^ b;
    }

    std::hint::black_box(differences) == 0
}

// ----- request bodies ------------------------------------------------------------------------

/// The body of a POST, read on demand: a route that refuses before it needs one never reads it.
struct BodySource<'a> {
    stream: &'a mut dyn Read,
    head: &'a Head,
    leftover: Option<Vec<u8>>,
}

/// A parsed body that keeps its source text, because the error text quotes positions in it.
struct Parsed {
    text: String,
    node: json::Node,
}

impl Parsed {
    /// The body as the record `type_name`; a body that is not an object is a `JsonError`.
    fn reader(&self, type_name: &'static str) -> std::result::Result<Reader<'_>, JsonError> {
        Reader::new(&self.text, &self.node, type_name)
    }
}

impl BodySource<'_> {
    fn text(&mut self) -> std::result::Result<String, Reply> {
        if !self.head.method.eq_ignore_ascii_case("POST") {
            return Err(Reply::refuse(405, "that endpoint wants a POST", &self.head.method));
        }

        let leftover = self.leftover.take().unwrap_or_default();

        match http::read_body(self.stream, self.head, leftover) {
            Ok(bytes) => Ok(String::from_utf8_lossy(&bytes).into_owned()),
            Err(HttpError::Bad(status, reason)) => Err(Reply::refuse(status, reason, "")),
            Err(_) => Err(Reply::refuse(400, "the body could not be read", "")),
        }
    }

    /// `ReadBody<T>`: a body is required, and `null` is as good as none.
    fn required(&mut self) -> std::result::Result<Parsed, Reply> {
        let text = self.text()?;

        match json::parse(&text) {
            Ok(node) if matches!(node.value, json::Value::Null) => Err(Reply::refuse(400, "the body was empty", "")),
            Ok(node) => Ok(Parsed { text, node }),
            Err(e) => Err(json_refusal(&e)),
        }
    }

    /// `ReadOptionalBody<T>`: an empty body, or the literal `null`, means "nothing was supplied",
    /// which is itself an answer. Every field of every shape this is used for is optional.
    fn optional(&mut self) -> std::result::Result<Parsed, Reply> {
        let text = self.text()?;
        let text = if text.trim().is_empty() { "{}".to_string() } else { text };

        match json::parse(&text) {
            Ok(node) if matches!(node.value, json::Value::Null) => {
                let empty = "{}".to_string();
                let node = json::parse(&empty).map_err(|e| json_refusal(&e))?;
                Ok(Parsed { text: empty, node })
            }
            Ok(node) => Ok(Parsed { text, node }),
            Err(e) => Err(json_refusal(&e)),
        }
    }
}

fn json_refusal(e: &JsonError) -> Reply {
    Reply::refuse(400, "the body was not the JSON this expects", &e.0)
}

// ----- the routes --------------------------------------------------------------------------

const ENDPOINTS: [&str; 28] = [
    "GET /state",
    "GET /limits",
    "GET /channels?raw=",
    "GET /values?channel=&seconds=",
    "GET /insights",
    "GET /tune",
    "GET /tables",
    "GET /table?name=",
    "POST /tune/set",
    "POST /table/set",
    "WS /live/stream",
    "GET /wire/health",
    "GET /wire/events?count=",
    "GET /project",
    "POST /project/record",
    "POST /project/fix",
    "POST /project/keep",
    "POST /project/versions/compare",
    "GET /tune/full",
    "GET /log/full?seconds=&decimate=",
    "GET /context",
    "POST /tune/propose",
    "POST /tune/apply",
    "POST /stage/tune",
    "POST /stage/table?name=",
    "GET /stage",
    "GET /definitions/needed",
    "POST /definitions/import",
];

/// Unwraps a body or returns its refusal from `route`.
macro_rules! body_of {
    ($call:expr) => {
        match $call {
            Ok(parsed) => parsed,
            Err(reply) => return reply,
        }
    };
}

/// Unwraps a typed read or answers 400 with what the JSON reader said.
macro_rules! typed {
    ($call:expr) => {
        match $call {
            Ok(value) => value,
            Err(e) => return json_refusal(&e),
        }
    };
}

fn route(shared: &Shared, stream: &mut dyn Read, head: &Head, leftover: Vec<u8>, path: &str) -> Reply {
    let bridge = shared.bridge.as_ref();
    let query = head.query();
    let mut body = BodySource {
        stream,
        head,
        leftover: Some(leftover),
    };

    match path {
        "/" => Reply::ok(J::obj([
            ("application", J::s("OpenLogViewer")),
            ("api", J::Int(1)),
            ("endpoints", J::strs(&ENDPOINTS)),
            // Read once on connect rather than learned by being refused: see GET /limits for
            // what each field means.
            ("limits", bridge.limits().to_json()),
        ])),

        "/state" => Reply::ok(bridge.state().to_json()),

        "/limits" => Reply::ok(bridge.limits().to_json()),

        "/channels" => {
            let channels = bridge.channels(flag(query.get("raw").as_deref()));
            Reply::ok(J::Arr(channels.iter().map(|c| c.to_json()).collect()))
        }

        "/wire/health" => Reply::ok(bridge.wire_health().to_json()),

        "/wire/events" => {
            let events = bridge.wire_events(wire_event_count(query.get("count").as_deref()));
            Reply::ok(J::Arr(events.iter().map(|e| e.to_json()).collect()))
        }

        "/values" => {
            let Some(channel) = query.get("channel").filter(|c| !c.trim().is_empty()) else {
                return Reply::refuse(400, "no channel named", "pass ?channel=");
            };

            let seconds = seconds(query.get("seconds").as_deref());
            let values = bridge.values(&channel, seconds);

            Reply::ok(J::obj([
                ("channel", J::s(&channel)),
                ("count", J::Int(values.len() as i64)),
                ("times", J::nums(&bridge.times(seconds))),
                ("values", J::nums(&values)),
            ]))
        }

        "/insights" => Reply::ok(J::Arr(bridge.insights().iter().map(|f| f.to_json()).collect())),

        "/tune" => Reply::ok(J::Obj(
            bridge
                .tune_values()
                .into_iter()
                .map(|(name, value)| (name, J::Num(value)))
                .collect(),
        )),

        "/tables" => Reply::ok(J::strs(&bridge.table_names())),

        "/project" => Reply::ok(J::obj([
            ("open", J::Bool(!bridge.project_brief().is_empty())),
            ("vehicles", J::strs(&bridge.projects())),
            ("brief", J::s(bridge.project_brief())),
        ])),

        "/project/versions/compare" => {
            let doc = body_of!(body.required());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+CompareVersions"));
            let from = typed!(r.string("from"));
            let to = typed!(r.string("to"));

            let changed = bridge.compare_versions(from.as_deref().unwrap_or(""), to.as_deref().unwrap_or(""));

            Reply::ok(J::obj([
                ("from", from.map_or(J::Null, J::Str)),
                ("to", to.map_or(J::Null, J::Str)),
                ("changed", J::s(changed)),
            ]))
        }

        "/project/keep" | "/project/record" => {
            let doc = body_of!(body.required());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+RecordSitting"));
            let note = typed!(r.string("note")).unwrap_or_default();

            let (refused, key) = if path == "/project/keep" {
                (bridge.keep_tune(&note), "kept")
            } else {
                (bridge.record_sitting(&note), "recorded")
            };

            if let Some(refused) = refused {
                return Reply::conflict(&refused);
            }

            Reply::ok(J::obj([(key, J::Bool(true)), ("brief", J::s(bridge.project_brief()))]))
        }

        "/project/fix" => {
            let doc = body_of!(body.required());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+NoteFix"));
            let id = typed!(r.string("id")).unwrap_or_default();
            let title = typed!(r.string("title")).unwrap_or_default();
            let detail = typed!(r.string("detail")).unwrap_or_default();
            let state = typed!(r.string("state")).unwrap_or_default();
            let change = typed!(r.string("change")).unwrap_or_default();

            if let Some(refused) = bridge.note_fix(&id, &title, &detail, &state, &change) {
                return Reply::conflict(&refused);
            }

            Reply::ok(J::obj([
                ("noted", J::Bool(true)),
                ("brief", J::s(bridge.project_brief())),
            ]))
        }

        "/table" => {
            let Some(name) = query.get("name").filter(|n| !n.trim().is_empty()) else {
                return Reply::refuse(400, "no table named", "pass ?name=");
            };

            match bridge.table(&name) {
                Some(table) => Reply::ok(flatten(&table)),
                None => Reply::refuse(404, "no such table", &name),
            }
        }

        "/tune/set" => {
            let doc = body_of!(body.required());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+SetSetting"));
            let name = typed!(r.string("name"));
            let value = typed!(r.double("value"));
            let rationale = typed!(r.string("rationale"));
            let confirm = typed!(r.boolean("confirmDangerous"));

            let refused = bridge.set_setting(
                name.as_deref().unwrap_or(""),
                value,
                rationale.as_deref().unwrap_or(""),
                confirm,
            );

            if let Some(refused) = refused {
                return Reply::conflict(&refused);
            }

            Reply::ok(J::obj([
                ("written", name.map_or(J::Null, J::Str)),
                ("value", J::Num(value)),
                ("burned", J::Bool(false)),
                ("rationale", J::s(rationale.unwrap_or_default())),
            ]))
        }

        "/table/set" => {
            let doc = body_of!(body.required());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+SetCell"));
            let table = typed!(r.string("table"));
            let column = typed!(r.int("column"));
            let row = typed!(r.int("row"));
            let value = typed!(r.double("value"));
            let rationale = typed!(r.string("rationale"));
            let confirm = typed!(r.boolean("confirmDangerous"));

            let result = bridge.set_table_cell(
                table.as_deref().unwrap_or(""),
                column,
                row,
                value,
                rationale.as_deref().unwrap_or(""),
                confirm,
            );

            if let Some(refused) = &result.refusal {
                return Reply::conflict(refused);
            }

            // A table cell is held inside the firmware's declared range rather than refused for
            // being outside it, which is sensible for a person scaling a whole table and wrong
            // for a caller who asked for one exact number and was told it landed when it may
            // have been clamped to something else instead. `value` is what was actually encoded
            // and sent, so a silent clamp is visible here rather than only discoverable by
            // reading the table again.
            Reply::ok(J::obj([
                ("written", table.map_or(J::Null, J::Str)),
                ("column", J::Int(i64::from(column))),
                ("row", J::Int(i64::from(row))),
                ("value", J::Num(result.value)),
                ("requested", J::Num(value)),
                ("clamped", J::Bool((result.value - value).abs() > 1e-9)),
                ("burned", J::Bool(false)),
                ("rationale", J::s(rationale.unwrap_or_default())),
                ("warning", result.warning.map_or(J::Null, J::Str)),
            ]))
        }

        "/tune/full" => Reply::ok(bridge.tune_full().to_json()),

        "/log/full" => {
            let seconds = seconds(query.get("seconds").as_deref());
            let decimate = decimate(query.get("decimate").as_deref());

            Reply::ok(bridge.log_full(seconds, decimate).to_json())
        }

        "/context" => Reply::ok(bridge.context().to_json()),

        "/tune/propose" | "/tune/apply" | "/stage/tune" => {
            let doc = body_of!(body.optional());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+TuneProposal"));
            let proposal = typed!(Proposal::read(&r));

            // A null entry in either list would throw inside the C# before anything was asked of
            // the bridge, and the caller gets the generic 500.
            let Some((settings, cells)) = proposal.items() else {
                return Reply::refuse(
                    500,
                    "the application failed to answer",
                    "Object reference not set to an instance of an object.",
                );
            };

            match path {
                "/tune/propose" => {
                    let result = bridge.propose_tune(&settings, &cells);

                    Reply::ok(J::obj([
                        (
                            "changes",
                            J::Arr(result.changes.iter().map(describe_difference).collect()),
                        ),
                        ("rejected", J::strs(&result.rejected)),
                        ("summary", J::s(result.summary)),
                    ]))
                }
                "/tune/apply" => {
                    let result = bridge.apply_tune(
                        &settings,
                        &cells,
                        proposal.note.as_deref().unwrap_or(""),
                        proposal.confirm_dangerous,
                    );

                    if let Some(refused) = &result.refusal {
                        return Reply::conflict(refused);
                    }

                    Reply::ok(J::obj([
                        (
                            "applied",
                            J::Arr(result.applied.iter().map(describe_difference).collect()),
                        ),
                        ("rejected", J::strs(&result.rejected)),
                        ("summary", J::s(result.summary)),
                        ("burned", J::Bool(false)),
                    ]))
                }
                _ => {
                    let result = bridge.stage_tune(&settings, &cells, proposal.filename.as_deref().unwrap_or(""));

                    stage_reply(result, false)
                }
            }
        }

        "/stage/table" => {
            let Some(name) = query.get("name").filter(|n| !n.trim().is_empty()) else {
                return Reply::refuse(400, "no table named", "pass ?name=");
            };

            let doc = body_of!(body.optional());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+StageTableBody"));
            let filename = typed!(r.string("filename")).unwrap_or_default();

            stage_reply(bridge.stage_table(&name, &filename), true)
        }

        "/stage" => Reply::ok(J::obj([(
            "files",
            J::Arr(bridge.list_staged().iter().map(|f| f.to_json()).collect()),
        )])),

        "/definitions/needed" => match bridge.definition_needed() {
            None => Reply::ok(J::obj([("needed", J::Bool(false))])),
            Some(need) => Reply::ok(J::obj([("needed", J::Bool(true)), ("need", need.to_json())])),
        },

        "/definitions/import" => {
            let doc = body_of!(body.required());
            let r = typed!(doc.reader("OpenLogViewer.Core.AgentServer+ImportDefinition"));
            let file = typed!(r.string("path")).unwrap_or_default();
            let content = typed!(r.string("content")).unwrap_or_default();
            let source = typed!(r.string("source")).unwrap_or_default();
            let name = typed!(r.string("name")).unwrap_or_default();

            let kept = bridge.import_definition(&file, &content, &source, &name);

            if !kept.accepted() {
                return Reply::refuse(409, "that definition was not kept", &kept.problem);
            }

            Reply::ok(J::obj([
                ("kept", J::Bool(true)),
                ("name", J::s(kept.name)),
                ("signature", J::s(kept.signature)),
                ("path", J::s(kept.path)),
                ("source", J::s(kept.source)),
            ]))
        }

        _ => Reply::refuse(404, "no such endpoint", path),
    }
}

/// The answer to a staging request: the file, or why not. A table that is not there is a 404.
fn stage_reply(result: AgentStageResult, table: bool) -> Reply {
    if let Some(refused) = &result.refusal {
        let status = if table && refused.reason == "no such table" {
            404
        } else {
            409
        };
        return Reply::refuse(status, &refused.reason, &refused.detail);
    }

    match result.file {
        Some(file) => Reply::ok(file.to_json()),
        None => Reply::refuse(500, "the application failed to answer", "No file was staged."),
    }
}

/// A table as numbers rather than as an object graph.
fn flatten(table: &TuneTable) -> J {
    AgentTable::flatten(table).to_json()
}

/// The settings, cells and extras a propose, apply or stage request shares. Every field is
/// optional, because "nothing here has changed" and "stage the tune as it stands" are themselves
/// valid requests rather than malformed ones.
struct Proposal {
    settings: Option<Vec<Option<ProposedSetting>>>,
    cells: Option<Vec<Option<ProposedCell>>>,
    note: Option<String>,
    filename: Option<String>,
    confirm_dangerous: bool,
}

impl Proposal {
    fn read(r: &Reader<'_>) -> std::result::Result<Proposal, JsonError> {
        const SETTING_LIST: &str =
            "System.Collections.Generic.List`1[OpenLogViewer.Core.AgentServer+TuneChangeSetting]";
        const CELL_LIST: &str = "System.Collections.Generic.List`1[OpenLogViewer.Core.AgentServer+TuneChangeCell]";

        let settings = match r.records(
            "settings",
            SETTING_LIST,
            "OpenLogViewer.Core.AgentServer+TuneChangeSetting",
        )? {
            None => None,
            Some(items) => {
                let mut out = Vec::with_capacity(items.len());
                for item in items {
                    out.push(match item {
                        None => None,
                        Some(item) => Some(ProposedSetting {
                            name: item.string("name")?.unwrap_or_default(),
                            value: item.double("value")?,
                        }),
                    });
                }
                Some(out)
            }
        };

        let cells = match r.records("cells", CELL_LIST, "OpenLogViewer.Core.AgentServer+TuneChangeCell")? {
            None => None,
            Some(items) => {
                let mut out = Vec::with_capacity(items.len());
                for item in items {
                    out.push(match item {
                        None => None,
                        Some(item) => Some(ProposedCell {
                            table: item.string("table")?.unwrap_or_default(),
                            column: item.int("column")?,
                            row: item.int("row")?,
                            value: item.double("value")?,
                        }),
                    });
                }
                Some(out)
            }
        };

        Ok(Proposal {
            settings,
            cells,
            note: r.string("note")?,
            filename: r.string("filename")?,
            confirm_dangerous: r.boolean("confirmDangerous")?,
        })
    }

    /// The two lists as the bridge takes them; None when either held a null entry.
    fn items(&self) -> Option<(Vec<ProposedSetting>, Vec<ProposedCell>)> {
        let settings = match &self.settings {
            None => Vec::new(),
            Some(items) => items.iter().cloned().collect::<Option<Vec<_>>>()?,
        };
        let cells = match &self.cells {
            None => Vec::new(),
            Some(items) => items.iter().cloned().collect::<Option<Vec<_>>>()?,
        };

        Some((settings, cells))
    }
}

// ----- query-string values ---------------------------------------------------------------------

/// The seconds of a window: `double.TryParse(text, NumberStyles.Float, Invariant)`, then never
/// below zero; anything that does not parse is zero, which means all of it.
fn seconds(text: Option<&str>) -> f64 {
    match text.and_then(parse_float) {
        Some(v) if v.is_nan() => v,
        Some(v) => v.max(0.0),
        None => 0.0,
    }
}

/// `NumberStyles.Float` in the invariant culture: optional white space either side, an optional
/// sign, digits with an optional point and exponent, or the names Infinity and NaN. No thousands
/// separators.
fn parse_float(text: &str) -> Option<f64> {
    let trimmed = text.trim_matches(|c: char| matches!(c, '\t' | '\n' | '\u{b}' | '\u{c}' | '\r' | ' '));
    let unsigned = trimmed.strip_prefix(['+', '-']).unwrap_or(trimmed);

    if unsigned.is_empty() {
        return None;
    }

    if unsigned.chars().next().is_some_and(|c| c.is_ascii_alphabetic()) {
        let negative = trimmed.starts_with('-');

        return if unsigned.eq_ignore_ascii_case("infinity") {
            Some(if negative { f64::NEG_INFINITY } else { f64::INFINITY })
        } else if unsigned.eq_ignore_ascii_case("nan") {
            Some(f64::NAN)
        } else {
            None
        };
    }

    if !unsigned
        .bytes()
        .all(|b| b.is_ascii_digit() || matches!(b, b'.' | b'e' | b'E' | b'+' | b'-'))
    {
        return None;
    }

    trimmed.parse::<f64>().ok()
}

/// A query-string flag, in any of the spellings somebody hand-writing a request actually uses.
///
/// An exact match on "true" alone is a trap: `?raw=1` and `?raw=True` plainly mean yes, and
/// answering them with the curated channel set, the opposite of what was asked, with no error to
/// say so, is worse than refusing them would be. (A bare `?raw` with no `=` is not seen as the
/// name `raw` at all, as in the C#, and so is not a yes.)
fn flag(text: Option<&str>) -> bool {
    text.is_some_and(|t| t.is_empty() || t.eq_ignore_ascii_case("true") || t == "1" || t.eq_ignore_ascii_case("yes"))
}

/// How many wire events to hand back, defaulting to a screenful and capped at 500.
fn wire_event_count(text: Option<&str>) -> i32 {
    text.and_then(nf::parse_i32).map_or(100, |n| n.clamp(0, 500))
}

/// The stride to thin a full log by: 1 (every sample) unless a caller asks for wider spacing,
/// which is the only size control a JSON dump of a whole log has.
fn decimate(text: Option<&str>) -> i32 {
    text.and_then(nf::parse_i32).filter(|n| *n > 1).unwrap_or(1)
}

// ----- the live stream -----------------------------------------------------------------------

/// A socket with some bytes already taken off it: whatever followed the request head.
struct Prefixed {
    prefix: io::Cursor<Vec<u8>>,
    inner: TcpStream,
}

impl Read for Prefixed {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        let taken = self.prefix.read(buf)?;
        if taken > 0 {
            return Ok(taken);
        }
        self.inner.read(buf)
    }
}

impl Write for Prefixed {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        self.inner.write(buf)
    }

    fn flush(&mut self) -> io::Result<()> {
        self.inner.flush()
    }
}

/// One agent watching: the handshake, then frames out and requests in until either side goes.
fn stream_live(shared: &Arc<Shared>, stream: &mut TcpStream, head: &Head, leftover: Vec<u8>) {
    let subscriber = Arc::new(LiveSubscriber::new());

    {
        let mut watching = shared.subscribers();

        if watching.len() >= shared.maximum_clients {
            drop(watching);
            let _ = http::respond_empty(stream, 503);
            return;
        }

        watching.push(Arc::clone(&subscriber));
    }

    let outcome = (|| -> io::Result<()> {
        let key = head
            .header("Sec-WebSocket-Key")
            .map(str::trim)
            .filter(|k| !k.is_empty());
        let version = head.header("Sec-WebSocket-Version").map(str::trim);

        let Some(key) = key.filter(|_| version == Some("13")) else {
            let reply = Reply::refuse(400, "bad websocket handshake", "");
            return http::respond_json(stream, reply.status, &reply.body);
        };

        let accept = tungstenite::handshake::derive_accept_key(key.as_bytes());
        let response = format!(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\
             Sec-WebSocket-Accept: {accept}\r\n\r\n"
        );
        stream.write_all(response.as_bytes())?;
        stream.flush()?;

        stream.set_read_timeout(Some(POLL))?;

        let config = WebSocketConfig::default()
            .max_message_size(Some(MAX_STREAM_MESSAGE))
            .max_frame_size(Some(MAX_STREAM_MESSAGE));
        let socket = Prefixed {
            prefix: io::Cursor::new(leftover),
            inner: stream.try_clone()?,
        };
        let mut ws = tungstenite::WebSocket::from_raw_socket(socket, Role::Server, Some(config));

        run_subscriber(shared, &subscriber, &mut ws);

        let _ = ws.close(None);
        let _ = ws.flush();

        Ok(())
    })();

    let _ = outcome;

    let mut watching = shared.subscribers();
    watching.retain(|s| !Arc::ptr_eq(s, &subscriber));
}

fn run_subscriber(shared: &Shared, subscriber: &LiveSubscriber, ws: &mut tungstenite::WebSocket<Prefixed>) {
    while !shared.stopping.load(Ordering::SeqCst) {
        if let Some(messages) = subscriber.take_next() {
            for message in messages {
                if ws.send(Message::text(outgoing_json(&message).text())).is_err() {
                    return;
                }
            }
        }

        // Also the only way a half-open socket is noticed: without a read in flight, an agent
        // that vanished is discovered on the next write, which on an idle session may be a long
        // time.
        match ws.read() {
            Ok(Message::Text(text)) => answer_request(subscriber, ws, text.as_str()),
            Ok(Message::Binary(bytes)) => {
                answer_request(subscriber, ws, &String::from_utf8_lossy(&bytes));
            }
            Ok(Message::Close(_)) => return,
            Ok(_) => {}
            Err(tungstenite::Error::Io(e))
                if matches!(e.kind(), io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut) => {}
            Err(_) => return,
        }
    }
}

/// Listens for the one thing an agent can say: which channels it wants.
fn answer_request(subscriber: &LiveSubscriber, ws: &mut tungstenite::WebSocket<Prefixed>, text: &str) {
    match read_subscribe(text) {
        Ok(Some(request)) => subscriber.apply_request(&request),
        Ok(None) => {}
        Err(()) => {
            let reply = outgoing_json(&LiveSubscriber::invalid_request());
            let _ = ws.send(Message::text(reply.text()));
        }
    }
}

/// `Deserialize<Subscribe>`: a `channels` list and a `raw` flag, both optional. A JSON `null` is
/// no request at all; anything that is not that shape is a `JsonException`, which the C# turns
/// into the error reply.
fn read_subscribe(text: &str) -> std::result::Result<Option<SubscribeRequest>, ()> {
    let node = json::parse(text).map_err(|_| ())?;

    if matches!(node.value, json::Value::Null) {
        return Ok(None);
    }

    let reader = Reader::new(text, &node, "OpenLogViewer.Core.LiveSubscriber+Subscribe").map_err(|_| ())?;

    Ok(Some(SubscribeRequest {
        channels: reader.strings("channels").map_err(|_| ())?,
        raw: reader.opt_boolean("raw").map_err(|_| ())?,
    }))
}

/// One message for the agent, in the shape the C# serialises: NaN and the infinities travel as
/// the strings `"NaN"`, `"Infinity"` and `"-Infinity"`, because one bare `Infinity` is not JSON
/// and breaks a client's whole frame.
pub fn outgoing_json(message: &Outgoing) -> J {
    match message {
        Outgoing::Schema { channels } => J::obj([("type", J::s("schema")), ("channels", J::strs(channels))]),
        Outgoing::Frame { t, v, skipped } => J::obj([
            ("type", J::s("frame")),
            ("t", J::Num(*t)),
            ("v", J::nums(v)),
            ("skipped", J::Int(i64::from(*skipped))),
        ]),
        Outgoing::Error { detail } => J::obj([("type", J::s("error")), ("detail", J::s(detail))]),
    }
}
