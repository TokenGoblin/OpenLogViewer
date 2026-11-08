//! A Model Context Protocol server in front of a running OpenLogViewer.
//!
//! Ported from `tools/OpenLogViewer.Mcp/Program.cs`.
//!
//! The application already serves an HTTP API on the loopback address; this exists because Claude
//! Code and its relatives speak MCP over stdin and stdout rather than HTTP, and asking a model to
//! hold a bearer token and a port is a worse experience than handing it a set of named tools.
//!
//! It is a translator and nothing else. It holds no state, caches nothing, and knows nothing about
//! logs or tunes: every question is forwarded and every answer passed back as it came. That is
//! what keeps it from drifting away from the application it fronts.
//!
//! **Everything on stdout is protocol.** A stray `println!` corrupts the stream and the client
//! sees a server that died for no reason, so anything worth saying goes to stderr.
//!
//! The JSON-RPC here is the C#'s, line for line (protocol version `2024-11-05`, the same
//! handshake, the same errors), not the `rmcp` crate's: the clients that exist were written
//! against what the C# says, and a different handshake or error shape is a different server.
//!
//! One difference, in the direction of safety: the address is read from a file any local program
//! could have written, and the token goes to whatever it names. The C# sends it anywhere; this
//! refuses an address that is not the loopback one.

pub mod tools;

use std::io::{Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::{Path, PathBuf};
use std::time::Duration;

use olv_agent::agent::json::{self, J, Node, Value};
use olv_core::fmt as nf;
use olv_store::store::workspace::Workspace;

pub const PROTOCOL_VERSION: &str = "2024-11-05";

/// The C#'s `Http.Timeout`.
const TIMEOUT: Duration = Duration::from_secs(30);

/// Why a tool call did not produce an answer.
#[derive(Debug)]
enum Failure {
    /// The application did not answer (`HttpRequestException`).
    Unreachable(String),
    /// Anything else, said as the exception message.
    Other(String),
}

pub struct McpServer {
    details: PathBuf,
    address: String,
    token: String,
}

impl McpServer {
    /// `details` is where the application left its address and token. Overridable so a second
    /// instance, or a workspace somewhere else, can still be reached.
    pub fn new(details: impl Into<PathBuf>) -> Self {
        Self {
            details: details.into(),
            address: String::new(),
            token: String::new(),
        }
    }

    /// `agent-api.json` in the workspace folder.
    pub fn default_details() -> PathBuf {
        Workspace::default_path().join("agent-api.json")
    }

    pub fn details(&self) -> &Path {
        &self.details
    }

    /// One line of stdin: the line to print in answer, or None when nothing is owed.
    pub fn handle_line(&mut self, line: &str) -> Option<String> {
        if line.trim().is_empty() {
            return None;
        }

        let message = match json::parse(line) {
            Ok(node) => node,
            Err(e) => {
                eprintln!("unparseable: {}", e.0);
                return None;
            }
        };

        let Value::Object(fields) = &message.value else {
            return None;
        };

        let field = |name: &str| fields.iter().find(|(k, _)| k == name).map(|(_, n)| n);

        let method = match field("method").map(|n| &n.value) {
            Some(Value::Str(s)) => s.as_str(),
            _ => "",
        };

        // A notification has no id and takes no answer. "initialized" is the usual one, and
        // replying to it is a protocol error rather than a harmless extra.
        let id = match field("id") {
            Some(node) if !matches!(node.value, Value::Null) => node,
            _ => {
                if method == "notifications/cancelled" {
                    eprintln!("cancelled");
                }
                return None;
            }
        };

        let (key, payload) = self.answer(method, field("params"));

        Some(
            J::Obj(vec![
                (key.to_string(), payload),
                ("jsonrpc".to_string(), J::s("2.0")),
                ("id".to_string(), to_j(id)),
            ])
            .text_relaxed(),
        )
    }

    /// The `result` (or `error`) of one request.
    pub fn answer(&mut self, method: &str, parameters: Option<&Node>) -> (&'static str, J) {
        match method {
            "initialize" => (
                "result",
                J::obj([
                    ("protocolVersion", J::s(PROTOCOL_VERSION)),
                    ("capabilities", J::obj([("tools", J::Obj(vec![]))])),
                    (
                        "serverInfo",
                        J::obj([("name", J::s("openlogviewer")), ("version", J::s("1"))]),
                    ),
                ]),
            ),
            "ping" => ("result", J::Obj(vec![])),
            "tools/list" => ("result", J::obj([("tools", tools::tools())])),
            "tools/call" => ("result", self.call(parameters)),
            _ => (
                "error",
                J::obj([
                    ("code", J::Int(-32601)),
                    ("message", J::s(format!("no such method: {method}"))),
                ]),
            ),
        }
    }

    fn call(&mut self, parameters: Option<&Node>) -> J {
        let params = parameters.and_then(|n| match &n.value {
            Value::Object(fields) => Some(fields),
            _ => None,
        });

        let get = |name: &str| params.and_then(|f| f.iter().find(|(k, _)| k == name)).map(|(_, n)| n);

        let name = match get("name").map(|n| &n.value) {
            Some(Value::Str(s)) => s.clone(),
            _ => String::new(),
        };

        let arguments = Arguments(match get("arguments").map(|n| &n.value) {
            Some(Value::Object(fields)) => fields.as_slice(),
            _ => &[],
        });

        if let Err(problem) = self.locate() {
            return text(&problem, true);
        }

        match self.dispatch(&name, &arguments) {
            Ok((body, refused)) => text(&body, refused),
            Err(Failure::Unreachable(message)) => text(
                &format!(
                    "OpenLogViewer did not answer: {message}. Is it running with the agent API \
                     started? It is under Tools, \"Agent API\"."
                ),
                true,
            ),
            Err(Failure::Other(message)) => text(&message, true),
        }
    }

    fn dispatch(&mut self, name: &str, a: &Arguments<'_>) -> Result<(String, bool), Failure> {
        match name {
            "olv_state" => self.get("/state"),

            "olv_channels" => self.get(if a.boolean("raw") {
                "/channels?raw=true"
            } else {
                "/channels"
            }),

            "olv_insights" => self.get("/insights"),
            "olv_wire_health" => self.get("/wire/health"),

            "olv_wire_events" => {
                if a.has("count") {
                    let count = a.number("count") as i32;
                    self.get(&format!("/wire/events?count={count}"))
                } else {
                    self.get("/wire/events")
                }
            }

            "olv_tune" => self.get("/tune"),
            "olv_tables" => self.get("/tables"),
            "olv_project" => self.get("/project"),

            "olv_keep_tune" => {
                let body = J::obj([("note", J::s(a.text("note")?))]);
                self.post("/project/keep", &body)
            }

            "olv_compare_versions" => {
                let body = J::obj([("from", J::s(a.text("from")?)), ("to", J::s(a.text("to")?))]);
                self.post("/project/versions/compare", &body)
            }

            "olv_record_sitting" => {
                let body = J::obj([("note", J::s(a.text("note")?))]);
                self.post("/project/record", &body)
            }

            "olv_note_fix" => {
                let body = J::obj([
                    ("id", J::s(a.text("id")?)),
                    ("title", J::s(a.text("title")?)),
                    ("detail", J::s(a.text("detail")?)),
                    ("state", J::s(a.text("state")?)),
                    ("change", J::s(a.text("change")?)),
                ]);
                self.post("/project/fix", &body)
            }

            "olv_values" => {
                let channel = escape(&a.text("channel")?);
                let seconds = nf::f64(a.number("seconds"), "");
                self.get(&format!("/values?channel={channel}&seconds={seconds}"))
            }

            "olv_table" => {
                let name = escape(&a.text("name")?);
                self.get(&format!("/table?name={name}"))
            }

            "olv_set_setting" => {
                let body = J::obj([
                    ("name", J::s(a.text("name")?)),
                    ("value", J::Num(a.number("value"))),
                    ("rationale", J::s(a.text("rationale")?)),
                    ("confirmDangerous", J::Bool(a.boolean("confirmDangerous"))),
                ]);
                self.post("/tune/set", &body)
            }

            "olv_set_table_cell" => {
                let body = J::obj([
                    ("table", J::s(a.text("table")?)),
                    ("column", J::Int(i64::from(a.number("column") as i32))),
                    ("row", J::Int(i64::from(a.number("row") as i32))),
                    ("value", J::Num(a.number("value"))),
                    ("rationale", J::s(a.text("rationale")?)),
                    ("confirmDangerous", J::Bool(a.boolean("confirmDangerous"))),
                ]);
                self.post("/table/set", &body)
            }

            "olv_tune_full" => self.get("/tune/full"),

            "olv_log_full" => {
                let mut path = format!("/log/full?seconds={}", nf::f64(a.number("seconds"), ""));
                if a.has("decimate") {
                    path.push_str(&format!("&decimate={}", a.number("decimate") as i32));
                }
                self.get(&path)
            }

            "olv_context" => self.get("/context"),

            "olv_propose_tune" => {
                let body = J::obj([
                    ("settings", a.passthrough("settings")),
                    ("cells", a.passthrough("cells")),
                ]);
                self.post("/tune/propose", &body)
            }

            "olv_apply_tune" => {
                let body = J::obj([
                    ("settings", a.passthrough("settings")),
                    ("cells", a.passthrough("cells")),
                    ("note", J::s(a.text("note")?)),
                    ("confirmDangerous", J::Bool(a.boolean("confirmDangerous"))),
                ]);
                self.post("/tune/apply", &body)
            }

            "olv_stage_tune" => {
                let body = J::obj([
                    ("settings", a.passthrough("settings")),
                    ("cells", a.passthrough("cells")),
                    ("filename", J::s(a.text("filename")?)),
                ]);
                self.post("/stage/tune", &body)
            }

            "olv_stage_table" => {
                let name = escape(&a.text("name")?);
                let body = J::obj([("filename", J::s(a.text("filename")?))]);
                self.post(&format!("/stage/table?name={name}"), &body)
            }

            "olv_list_staged" => self.get("/stage"),

            "olv_definition_needed" => self.get("/definitions/needed"),

            "olv_import_definition" => {
                let body = J::obj([
                    ("path", J::s(a.text("path")?)),
                    ("content", J::s(a.text("content")?)),
                    ("source", J::s(a.text("source")?)),
                    ("name", J::s(a.text("name")?)),
                ]);
                self.post("/definitions/import", &body)
            }

            _ => Err(Failure::Other(format!("no such tool: {name}"))),
        }
    }

    // ----- finding the application -----------------------------------------------------------

    /// Reads the address and token the application left behind.
    fn locate(&mut self) -> Result<(), String> {
        if !self.address.is_empty() && !self.token.is_empty() {
            return Ok(());
        }

        let details = self.details.display().to_string();

        if !self.details.is_file() {
            return Err(format!(
                "No agent API is running: {details} is not there. Start OpenLogViewer and turn on \
                 Tools \u{2192} Agent API."
            ));
        }

        let content = std::fs::read_to_string(&self.details).map_err(|e| format!("Could not read {details}: {e}"))?;
        let found = json::parse(&content).map_err(|e| format!("Could not read {details}: {}", e.0))?;

        let string_of = |name: &str| match &found.value {
            Value::Object(fields) => fields
                .iter()
                .find(|(k, _)| k == name)
                .and_then(|(_, n)| match &n.value {
                    Value::Str(s) => Some(s.clone()),
                    _ => None,
                }),
            _ => None,
        };

        self.address = string_of("address").unwrap_or_default();
        self.token = string_of("token").unwrap_or_default();

        if !self.address.is_empty() && !self.token.is_empty() {
            return Ok(());
        }

        Err(format!("{details} does not name an address and a token."))
    }

    // ----- talking to it --------------------------------------------------------------------------

    fn get(&mut self, path: &str) -> Result<(String, bool), Failure> {
        self.send("GET", path, None)
    }

    fn post(&mut self, path: &str, body: &J) -> Result<(String, bool), Failure> {
        self.send("POST", path, Some(body.text_relaxed()))
    }

    /// The body, and whether the application refused.
    ///
    /// The second half matters more than it looks. A write refused because nobody armed writing
    /// comes back as a perfectly well-formed JSON object, and handing that to a model as an
    /// ordinary result invites it to read "writes are not armed" as a description of what it just
    /// did. MCP has a flag for exactly this; the refusal has to reach it.
    fn send(&mut self, method: &str, path: &str, body: Option<String>) -> Result<(String, bool), Failure> {
        let (host, port) = loopback_endpoint(&self.address)?;

        let mut stream = connect(&host, port).map_err(|e| Failure::Unreachable(e.to_string()))?;
        stream
            .set_read_timeout(Some(TIMEOUT))
            .and_then(|()| stream.set_write_timeout(Some(TIMEOUT)))
            .map_err(|e| Failure::Unreachable(e.to_string()))?;

        let mut request = format!(
            "{method} {} HTTP/1.1\r\nHost: {host}:{port}\r\nAuthorization: Bearer {}\r\n",
            ascii_target(path),
            self.token
        );

        if let Some(body) = &body {
            request.push_str(&format!(
                "Content-Type: application/json; charset=utf-8\r\nContent-Length: {}\r\n",
                body.len()
            ));
        }
        request.push_str("Connection: close\r\n\r\n");

        stream
            .write_all(request.as_bytes())
            .and_then(|()| stream.write_all(body.as_deref().unwrap_or("").as_bytes()))
            .and_then(|()| stream.flush())
            .map_err(|e| Failure::Unreachable(e.to_string()))?;

        let mut all = Vec::new();
        stream
            .read_to_end(&mut all)
            .map_err(|e| Failure::Unreachable(e.to_string()))?;

        parse_response(&all)
    }
}

/// `127.0.0.1:8765` out of `http://127.0.0.1:8765`, refusing anything that is not the loopback
/// address: the token would otherwise go to whatever a writable file said.
fn loopback_endpoint(address: &str) -> Result<(String, u16), Failure> {
    let rest = address
        .strip_prefix("http://")
        .ok_or_else(|| Failure::Other(format!("{address} is not an http:// address.")))?;
    let authority = rest.split('/').next().unwrap_or("");

    let (host, port) = match authority.rsplit_once(':') {
        Some((host, port)) => (host, port.parse::<u16>().ok()),
        None => (authority, Some(80)),
    };

    let loopback = host == "127.0.0.1" || host == "[::1]" || host.eq_ignore_ascii_case("localhost");

    match (loopback, port) {
        (true, Some(port)) => Ok((host.to_string(), port)),
        _ => Err(Failure::Other(format!(
            "Refusing to send the token to {address}: the agent API only ever listens on 127.0.0.1."
        ))),
    }
}

fn connect(host: &str, port: u16) -> std::io::Result<TcpStream> {
    let target = format!("{host}:{port}");
    let mut last = None;

    for addr in target.to_socket_addrs()? {
        match TcpStream::connect_timeout(&addr, TIMEOUT) {
            Ok(stream) => return Ok(stream),
            Err(e) => last = Some(e),
        }
    }

    Err(last.unwrap_or_else(|| std::io::Error::other("no address")))
}

/// A request target as ASCII: what the HTTP stack beneath the C# does with a path that holds
/// characters outside it.
fn ascii_target(path: &str) -> String {
    let mut out = String::with_capacity(path.len());

    for b in path.bytes() {
        if b.is_ascii() && b != b' ' {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }

    out
}

fn parse_response(raw: &[u8]) -> Result<(String, bool), Failure> {
    let split = raw
        .windows(4)
        .position(|w| w == b"\r\n\r\n")
        .ok_or_else(|| Failure::Unreachable("the response ended before its headers did".to_string()))?;

    let head = String::from_utf8_lossy(&raw[..split]).into_owned();
    let mut body = raw[split + 4..].to_vec();

    let mut lines = head.split("\r\n");
    let status: u16 = lines
        .next()
        .and_then(|l| l.split(' ').nth(1))
        .and_then(|s| s.parse().ok())
        .ok_or_else(|| Failure::Unreachable("the response had no status".to_string()))?;

    let chunked = lines.any(|l| {
        let lower = l.to_ascii_lowercase();
        lower.starts_with("transfer-encoding:") && lower.contains("chunked")
    });

    if chunked {
        body = dechunk(&body);
    }

    Ok((
        String::from_utf8_lossy(&body).into_owned(),
        !(200..300).contains(&status),
    ))
}

fn dechunk(mut rest: &[u8]) -> Vec<u8> {
    let mut out = Vec::new();

    while let Some(end) = rest.windows(2).position(|w| w == b"\r\n") {
        let size_text = String::from_utf8_lossy(&rest[..end]).into_owned();
        let size = usize::from_str_radix(size_text.split(';').next().unwrap_or("").trim(), 16).unwrap_or(0);

        rest = &rest[end + 2..];
        if size == 0 || rest.len() < size {
            break;
        }

        out.extend_from_slice(&rest[..size]);
        rest = rest.get(size + 2..).unwrap_or(&[]);
    }

    out
}

/// `Uri.EscapeDataString`: everything but the unreserved characters, as UTF-8 percent escapes.
pub fn escape(text: &str) -> String {
    let mut out = String::with_capacity(text.len());

    for b in text.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'.' | b'_' | b'~') {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }

    out
}

/// A tool result, which MCP carries as content rather than as a value.
fn text(body: &str, failed: bool) -> J {
    J::obj([
        (
            "content",
            J::Arr(vec![J::obj([("type", J::s("text")), ("text", J::s(body))])]),
        ),
        ("isError", J::Bool(failed)),
    ])
}

/// The arguments of one call, read the way the C# reads them: leniently, with a missing or
/// mistyped number as zero and a string that looks like one accepted.
struct Arguments<'a>(&'a [(String, Node)]);

impl Arguments<'_> {
    fn node(&self, name: &str) -> Option<&Node> {
        self.0.iter().find(|(k, _)| k == name).map(|(_, n)| n)
    }

    fn has(&self, name: &str) -> bool {
        self.node(name).is_some()
    }

    /// A string, or empty when absent. Anything else is the exception `GetValue<string>()` throws.
    fn text(&self, name: &str) -> Result<String, Failure> {
        match self.node(name).map(|n| &n.value) {
            None | Some(Value::Null) => Ok(String::new()),
            Some(Value::Str(s)) => Ok(s.clone()),
            Some(other) => Err(Failure::Other(format!(
                "An element of type '{}' cannot be converted to a 'System.String'.",
                kind(other)
            ))),
        }
    }

    /// A number: a JSON number, a string that parses as one, or zero.
    fn number(&self, name: &str) -> f64 {
        match self.node(name).map(|n| &n.value) {
            Some(Value::Number(raw)) => raw.parse().unwrap_or(0.0),
            Some(Value::Str(s)) => nf::parse_f64(s).unwrap_or(0.0),
            _ => 0.0,
        }
    }

    /// A boolean: `true` or `false`, or a string that spells one.
    fn boolean(&self, name: &str) -> bool {
        match self.node(name).map(|n| &n.value) {
            Some(Value::Bool(b)) => *b,
            Some(Value::Str(s)) => s.trim().eq_ignore_ascii_case("true"),
            _ => false,
        }
    }

    /// An array argument, carried through to the request body as it was given rather than
    /// reinterpreted; an empty one when it was not an array.
    fn passthrough(&self, name: &str) -> J {
        match self.node(name) {
            Some(node) if matches!(node.value, Value::Array(_)) => to_j(node),
            _ => J::Arr(vec![]),
        }
    }
}

fn kind(value: &Value) -> &'static str {
    match value {
        Value::Null => "Null",
        Value::Bool(true) => "True",
        Value::Bool(false) => "False",
        Value::Number(_) => "Number",
        Value::Str(_) => "String",
        Value::Array(_) => "Array",
        Value::Object(_) => "Object",
    }
}

/// A parsed node back to JSON, exactly as it came: numbers keep their text, nulls stay inside
/// objects.
fn to_j(node: &Node) -> J {
    match &node.value {
        Value::Null => J::Raw("null".to_string()),
        Value::Bool(b) => J::Bool(*b),
        Value::Number(raw) => J::Raw(raw.clone()),
        Value::Str(s) => J::Str(s.clone()),
        Value::Array(items) => J::Arr(items.iter().map(to_j).collect()),
        Value::Object(fields) => J::Obj(fields.iter().map(|(k, v)| (k.clone(), to_j(v))).collect()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn data_is_escaped_the_way_uri_escape_data_string_does() {
        assert_eq!(escape("VE Table"), "VE%20Table");
        assert_eq!(escape("a b&c=d/\u{e9}"), "a%20b%26c%3Dd%2F%C3%A9");
        assert_eq!(escape("Lua: torque-1.5_~"), "Lua%3A%20torque-1.5_~");
    }

    #[test]
    fn the_token_only_ever_goes_to_the_loopback_address() {
        assert!(loopback_endpoint("http://127.0.0.1:8765").is_ok());
        assert!(loopback_endpoint("http://localhost:8765").is_ok());
        assert!(loopback_endpoint("http://[::1]:8765").is_ok());

        for bad in [
            "http://evil.example:8765",
            "http://10.0.0.5:8765",
            "https://127.0.0.1:8765",
            "http://127.0.0.1.evil.example:8765",
            "ftp://127.0.0.1",
            "",
        ] {
            assert!(loopback_endpoint(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn a_notification_gets_no_answer_and_a_request_does() {
        let mut mcp = McpServer::new("/nowhere/agent-api.json");

        assert_eq!(
            mcp.handle_line(r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#),
            None
        );
        assert_eq!(mcp.handle_line(r#"{"jsonrpc":"2.0","id":null,"method":"ping"}"#), None);
        assert_eq!(mcp.handle_line(""), None);
        assert_eq!(mcp.handle_line("not json"), None);
        assert_eq!(mcp.handle_line("[1,2]"), None);

        assert_eq!(
            mcp.handle_line(r#"{"jsonrpc":"2.0","id":"abc","method":"ping"}"#)
                .as_deref(),
            Some(r#"{"result":{},"jsonrpc":"2.0","id":"abc"}"#)
        );
    }

    #[test]
    fn a_response_is_split_into_status_and_body() {
        let ok = parse_response(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}").unwrap();
        assert_eq!(ok, ("{}".to_string(), false));

        let refused = parse_response(b"HTTP/1.1 409 Conflict\r\n\r\n{\"error\":\"x\"}").unwrap();
        assert_eq!(refused, ("{\"error\":\"x\"}".to_string(), true));

        let chunked =
            parse_response(b"HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n2\r\nde\r\n0\r\n\r\n")
                .unwrap();
        assert_eq!(chunked.0, "abcde");
    }
}
