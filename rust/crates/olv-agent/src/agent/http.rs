//! Just enough HTTP/1.1 for the agent API, on a plain `TcpStream`.
//!
//! The C# server is built on `HttpListener` so the application gains no dependency it did not
//! already have; this does the same job with `httparse` for the request head and nothing else.
//! It reads one request per connection and always answers `Connection: close`, which every
//! client of this API (the MCP server, `curl`, `HttpClient`, `requests`) handles.
//!
//! Bounded on every side, because this is the part that faces whatever else is running on the
//! machine: the head is capped, the body is capped, a stalled client is cut off by the socket
//! timeouts the server sets, and a request that is not valid HTTP is dropped rather than
//! guessed at.

use std::io::{self, Read, Write};

/// The most a request head (request line and headers) may take.
pub const MAX_HEAD: usize = 64 * 1024;

/// The most a request body may take. A definition file passed inline runs to half a megabyte;
/// nothing legitimate comes near this.
pub const MAX_BODY: usize = 32 * 1024 * 1024;

#[derive(Debug)]
pub enum HttpError {
    /// The client went away, or never finished sending the head.
    Closed,
    /// Not HTTP, or too big. Carries the status to answer with.
    Bad(u16, &'static str),
    Io(io::Error),
}

impl From<io::Error> for HttpError {
    fn from(e: io::Error) -> Self {
        match e.kind() {
            io::ErrorKind::UnexpectedEof
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::ConnectionAborted
            | io::ErrorKind::BrokenPipe
            | io::ErrorKind::TimedOut
            | io::ErrorKind::WouldBlock => HttpError::Closed,
            _ => HttpError::Io(e),
        }
    }
}

/// A request head, parsed.
#[derive(Debug, Clone)]
pub struct Head {
    pub method: String,
    /// The path as the client sent it, still percent-encoded, without the query.
    pub raw_path: String,
    pub raw_query: String,
    pub headers: Vec<(String, String)>,
}

impl Head {
    /// The first header of that name, compared without regard to case.
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }

    /// `HttpListenerRequest.IsWebSocketRequest`: an upgrade to `websocket`, asked for as such.
    pub fn is_websocket_request(&self) -> bool {
        let upgrade = self
            .header("Upgrade")
            .is_some_and(|v| v.trim().eq_ignore_ascii_case("websocket"));
        let connection = self.headers.iter().any(|(k, v)| {
            k.eq_ignore_ascii_case("Connection") && v.split(',').any(|t| t.trim().eq_ignore_ascii_case("upgrade"))
        });

        upgrade && connection
    }

    /// The path the way `Uri.AbsolutePath` hands it over, with the trailing slashes the server
    /// trims already gone; `/` when nothing is left.
    pub fn path(&self) -> String {
        let trimmed = normalize_path(&self.raw_path);
        let trimmed = trimmed.trim_end_matches('/');

        if trimmed.is_empty() {
            "/".to_string()
        } else {
            trimmed.to_string()
        }
    }

    /// `HttpListenerRequest.QueryString`.
    pub fn query(&self) -> Query {
        Query::parse(&self.raw_query)
    }
}

/// The query string as a `NameValueCollection`: repeated names join with a comma, and a piece
/// with no `=` is kept under no name at all (so `?raw` does not set `raw`).
#[derive(Debug, Default, Clone)]
pub struct Query {
    pairs: Vec<(Option<String>, String)>,
}

impl Query {
    pub fn parse(raw: &str) -> Query {
        let mut pairs: Vec<(Option<String>, String)> = Vec::new();

        for piece in raw.split('&') {
            if piece.is_empty() {
                continue;
            }

            match piece.split_once('=') {
                Some((key, value)) => pairs.push((Some(decode_query(key)), decode_query(value))),
                None => pairs.push((None, decode_query(piece))),
            }
        }

        Query { pairs }
    }

    /// The value for a name, or None when the name is not there. Several values for one name
    /// come back joined with commas, as .NET does.
    pub fn get(&self, name: &str) -> Option<String> {
        let mut found: Vec<&str> = Vec::new();

        for (key, value) in &self.pairs {
            if key.as_deref() == Some(name) {
                found.push(value);
            }
        }

        if found.is_empty() { None } else { Some(found.join(",")) }
    }
}

fn hex(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}

/// Percent-decoding as a query string wants it: `+` is a space, `%XX` is a byte, and the bytes
/// are read as UTF-8.
fn decode_query(text: &str) -> String {
    let bytes = text.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;

    while i < bytes.len() {
        match bytes[i] {
            b'+' => out.push(b' '),
            b'%' if i + 2 < bytes.len() && hex(bytes[i + 1]).is_some() && hex(bytes[i + 2]).is_some() => {
                out.push(hex(bytes[i + 1]).unwrap_or(0) << 4 | hex(bytes[i + 2]).unwrap_or(0));
                i += 2;
            }
            b => out.push(b),
        }
        i += 1;
    }

    String::from_utf8_lossy(&out).into_owned()
}

/// What `Uri.AbsolutePath` does to a path: backslashes are slashes, escapes of unreserved
/// characters are undone, and `.` and `..` segments are resolved. Other escapes stay as sent
/// (so `%2F` does not become a separator).
pub fn normalize_path(raw: &str) -> String {
    let bytes = raw.as_bytes();
    let mut decoded = String::with_capacity(raw.len());
    let mut i = 0;

    while i < bytes.len() {
        let b = bytes[i];

        if b == b'%'
            && i + 2 < bytes.len()
            && let (Some(h), Some(l)) = (hex(bytes[i + 1]), hex(bytes[i + 2]))
        {
            let value = h << 4 | l;
            if value.is_ascii_alphanumeric() || matches!(value, b'-' | b'.' | b'_' | b'~') {
                decoded.push(value as char);
            } else {
                decoded.push_str(&raw[i..i + 3]);
            }
            i += 3;
            continue;
        }

        decoded.push(if b == b'\\' { '/' } else { b as char });
        i += 1;
    }

    // Dot segments, as RFC 3986 section 5.2.4 removes them.
    let mut kept: Vec<&str> = Vec::new();
    let segments: Vec<&str> = decoded.split('/').collect();
    let last = segments.len().saturating_sub(1);

    for (i, segment) in segments.iter().enumerate() {
        match *segment {
            "." => {
                if i == last {
                    kept.push("");
                }
            }
            ".." => {
                if kept.len() > 1 {
                    kept.pop();
                }
                if i == last {
                    kept.push("");
                }
            }
            other => kept.push(other),
        }
    }

    let joined = kept.join("/");
    if joined.starts_with('/') {
        joined
    } else {
        format!("/{joined}")
    }
}

/// Reads and parses a request head. Returns it with any bytes that arrived after it (the start
/// of the body, or of a WebSocket frame).
pub fn read_head(stream: &mut (impl Read + ?Sized)) -> Result<(Head, Vec<u8>), HttpError> {
    let mut buffer: Vec<u8> = Vec::with_capacity(2048);
    let mut chunk = [0u8; 4096];

    loop {
        let read = stream.read(&mut chunk)?;
        if read == 0 {
            return Err(HttpError::Closed);
        }
        buffer.extend_from_slice(&chunk[..read]);

        let mut slots = [httparse::EMPTY_HEADER; 96];
        let mut request = httparse::Request::new(&mut slots);

        match request.parse(&buffer) {
            Ok(httparse::Status::Complete(used)) => {
                let method = request.method.unwrap_or("").to_string();
                let target = request.path.unwrap_or("").to_string();

                let headers = request
                    .headers
                    .iter()
                    .map(|h| (h.name.to_string(), String::from_utf8_lossy(h.value).trim().to_string()))
                    .collect();

                // An origin-form target is `/path?query`; a proxy-style one carries the whole
                // address, which `HttpListener` also accepts.
                let after_scheme = match target.find("://") {
                    Some(at) if !target.starts_with('/') => {
                        let rest = &target[at + 3..];
                        rest.find('/').map_or("/", |slash| &rest[slash..])
                    }
                    _ => target.as_str(),
                };

                let (path, query) = after_scheme.split_once('?').unwrap_or((after_scheme, ""));
                let query = query.split('#').next().unwrap_or("");
                let path = path.split('#').next().unwrap_or("");

                if !path.starts_with('/') {
                    return Err(HttpError::Bad(400, "Bad Request"));
                }

                return Ok((
                    Head {
                        method,
                        raw_path: path.to_string(),
                        raw_query: query.to_string(),
                        headers,
                    },
                    buffer[used..].to_vec(),
                ));
            }
            Ok(httparse::Status::Partial) => {
                if buffer.len() > MAX_HEAD {
                    return Err(HttpError::Bad(431, "Request Header Fields Too Large"));
                }
            }
            Err(_) => return Err(HttpError::Bad(400, "Bad Request")),
        }
    }
}

/// Reads the body that goes with `head`: by `Content-Length`, or chunked. `already` is whatever
/// followed the head in the buffer that was read with it.
pub fn read_body(stream: &mut (impl Read + ?Sized), head: &Head, already: Vec<u8>) -> Result<Vec<u8>, HttpError> {
    let chunked = head
        .header("Transfer-Encoding")
        .is_some_and(|v| v.to_ascii_lowercase().contains("chunked"));

    if chunked {
        return read_chunked(stream, already);
    }

    let Some(length) = head.header("Content-Length") else {
        return Ok(Vec::new());
    };

    let length: usize = length.trim().parse().map_err(|_| HttpError::Bad(400, "Bad Request"))?;
    if length > MAX_BODY {
        return Err(HttpError::Bad(413, "Payload Too Large"));
    }

    let mut body = already;
    body.truncate(length);
    body.reserve(length.saturating_sub(body.len()));

    let mut chunk = [0u8; 16 * 1024];
    while body.len() < length {
        let want = chunk.len().min(length - body.len());
        let read = stream.read(&mut chunk[..want])?;
        if read == 0 {
            return Err(HttpError::Closed);
        }
        body.extend_from_slice(&chunk[..read]);
    }

    Ok(body)
}

fn read_chunked(stream: &mut (impl Read + ?Sized), mut pending: Vec<u8>) -> Result<Vec<u8>, HttpError> {
    let mut body = Vec::new();
    let mut at = 0;

    // Pulls more bytes until `ready` can find what it wants in what is buffered.
    fn fill(stream: &mut (impl Read + ?Sized), pending: &mut Vec<u8>) -> Result<(), HttpError> {
        let mut chunk = [0u8; 8192];
        let read = stream.read(&mut chunk)?;
        if read == 0 {
            return Err(HttpError::Closed);
        }
        pending.extend_from_slice(&chunk[..read]);
        Ok(())
    }

    loop {
        // The size line.
        let line_end = loop {
            if let Some(pos) = pending[at..].windows(2).position(|w| w == b"\r\n") {
                break at + pos;
            }
            if pending.len() - at > 1024 {
                return Err(HttpError::Bad(400, "Bad Request"));
            }
            fill(stream, &mut pending)?;
        };

        let size_text = String::from_utf8_lossy(&pending[at..line_end]).into_owned();
        let size_text = size_text.split(';').next().unwrap_or("").trim().to_string();
        let size = usize::from_str_radix(&size_text, 16).map_err(|_| HttpError::Bad(400, "Bad Request"))?;
        at = line_end + 2;

        if size == 0 {
            // Trailers, if any, are not read: the connection is closed after the answer.
            return Ok(body);
        }

        if body.len() + size > MAX_BODY {
            return Err(HttpError::Bad(413, "Payload Too Large"));
        }

        while pending.len() - at < size + 2 {
            fill(stream, &mut pending)?;
        }

        body.extend_from_slice(&pending[at..at + size]);
        at += size + 2;
    }
}

/// The reason phrase for the statuses this server uses.
pub fn reason(status: u16) -> &'static str {
    match status {
        101 => "Switching Protocols",
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Unknown",
    }
}

/// Writes one JSON answer and leaves the connection to be closed.
pub fn respond_json(stream: &mut impl Write, status: u16, body: &str) -> io::Result<()> {
    let head = format!(
        "HTTP/1.1 {status} {}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {}\r\n\
         Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n",
        reason(status),
        body.len()
    );

    stream.write_all(head.as_bytes())?;
    stream.write_all(body.as_bytes())?;
    stream.flush()
}

/// An answer with no body, such as the 503 for too many subscribers.
pub fn respond_empty(stream: &mut impl Write, status: u16) -> io::Result<()> {
    let head = format!(
        "HTTP/1.1 {status} {}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        reason(status)
    );

    stream.write_all(head.as_bytes())?;
    stream.flush()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_query_is_read_like_a_name_value_collection() {
        let q = Query::parse("channel=RPM&channel=AFR&raw&flag=&x=a%20b+c&%E2%80%94=1");
        assert_eq!(q.get("channel").as_deref(), Some("RPM,AFR"));
        assert_eq!(q.get("raw"), None, "a bare name sets nothing");
        assert_eq!(q.get("flag").as_deref(), Some(""));
        assert_eq!(q.get("x").as_deref(), Some("a b c"));
        assert_eq!(q.get("\u{2014}").as_deref(), Some("1"));
        assert_eq!(q.get("missing"), None);
    }

    #[test]
    fn a_path_is_normalised_as_uri_absolute_path_does() {
        assert_eq!(normalize_path("/sta%74e"), "/state");
        assert_eq!(normalize_path("/tune\\set"), "/tune/set");
        assert_eq!(normalize_path("/a/../tune/./set"), "/tune/set");
        assert_eq!(normalize_path("/tune%2Fset"), "/tune%2Fset");
        assert_eq!(normalize_path("/../state"), "/state");
    }

    #[test]
    fn the_path_drops_trailing_slashes_and_is_never_empty() {
        let head = |p: &str| Head {
            method: "GET".into(),
            raw_path: p.into(),
            raw_query: String::new(),
            headers: vec![],
        };
        assert_eq!(head("/state/").path(), "/state");
        assert_eq!(head("/").path(), "/");
        assert_eq!(head("//").path(), "/");
    }

    #[test]
    fn a_head_and_the_start_of_a_body_are_split() {
        let raw = b"POST /tune/set?x=1 HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 5\r\n\r\nhel";
        let mut cursor = io::Cursor::new(raw.to_vec());
        let (head, rest) = read_head(&mut cursor).unwrap();
        assert_eq!(head.method, "POST");
        assert_eq!(head.raw_query, "x=1");
        assert_eq!(rest, b"hel");

        let mut more = io::Cursor::new(b"lo".to_vec());
        assert_eq!(read_body(&mut more, &head, rest).unwrap(), b"hello");
    }

    #[test]
    fn a_chunked_body_is_joined() {
        let head = Head {
            method: "POST".into(),
            raw_path: "/".into(),
            raw_query: String::new(),
            headers: vec![("Transfer-Encoding".into(), "chunked".into())],
        };
        let mut rest = io::Cursor::new(b"3\r\nabc\r\n2\r\nde\r\n0\r\n\r\n".to_vec());
        assert_eq!(read_body(&mut rest, &head, Vec::new()).unwrap(), b"abcde");
    }

    #[test]
    fn an_oversized_declared_body_is_refused_before_it_is_read() {
        let head = Head {
            method: "POST".into(),
            raw_path: "/".into(),
            raw_query: String::new(),
            headers: vec![("Content-Length".into(), (MAX_BODY + 1).to_string())],
        };
        assert!(matches!(
            read_body(&mut io::empty(), &head, Vec::new()),
            Err(HttpError::Bad(413, _))
        ));
    }
}
