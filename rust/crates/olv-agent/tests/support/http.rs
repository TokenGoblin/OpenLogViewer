//! A client for the tests: raw HTTP over a `TcpStream`, so a test controls every header, and a
//! WebSocket over `tungstenite`.

#![allow(dead_code)]

use std::io::{Read, Write};
use std::net::TcpStream;
use std::time::Duration;

pub const TOKEN: &str = "test-token-0123456789";

#[derive(Debug, Clone)]
pub struct Answer {
    pub status: u16,
    pub content_type: String,
    pub body: String,
}

impl Answer {
    pub fn json(&self) -> serde_json::Value {
        serde_json::from_str(&self.body).unwrap_or_else(|e| panic!("not JSON ({e}): {}", self.body))
    }
}

/// One request, read to the end of the connection. `headers` replaces the defaults: a `Host`
/// among them is used as given, otherwise it is the real address.
pub fn raw(port: u16, method: &str, target: &str, headers: &[(&str, &str)], body: &str) -> Answer {
    let mut stream = TcpStream::connect(("127.0.0.1", port)).expect("connects");
    stream.set_read_timeout(Some(Duration::from_secs(10))).unwrap();

    let mut request = format!("{method} {target} HTTP/1.1\r\n");

    if !headers.iter().any(|(k, _)| k.eq_ignore_ascii_case("Host")) {
        request.push_str(&format!("Host: 127.0.0.1:{port}\r\n"));
    }

    for (k, v) in headers {
        request.push_str(&format!("{k}: {}\r\n", v.replace("{port}", &port.to_string())));
    }

    if method == "POST" {
        request.push_str(&format!("Content-Length: {}\r\n", body.len()));
    }
    request.push_str("Connection: close\r\n\r\n");

    stream.write_all(request.as_bytes()).unwrap();
    stream.write_all(body.as_bytes()).unwrap();

    let mut all = Vec::new();
    let _ = stream.read_to_end(&mut all);
    let text = String::from_utf8_lossy(&all).into_owned();

    let (head, content) = text.split_once("\r\n\r\n").unwrap_or((&text, ""));
    let mut lines = head.split("\r\n");
    let status = lines
        .next()
        .and_then(|l| l.split(' ').nth(1))
        .and_then(|s| s.parse().ok())
        .unwrap_or(0);
    let content_type = lines
        .find_map(|l| {
            let (k, v) = l.split_once(':')?;
            k.eq_ignore_ascii_case("content-type").then(|| v.trim().to_string())
        })
        .unwrap_or_default();

    Answer {
        status,
        content_type,
        body: content.to_string(),
    }
}

fn auth() -> [(&'static str, String); 1] {
    [("Authorization", format!("Bearer {TOKEN}"))]
}

pub fn get(port: u16, target: &str) -> Answer {
    let [(k, v)] = auth();
    raw(port, "GET", target, &[(k, &v)], "")
}

pub fn post(port: u16, target: &str, body: &str) -> Answer {
    let [(k, v)] = auth();
    raw(port, "POST", target, &[(k, &v)], body)
}

pub type Socket = tungstenite::WebSocket<TcpStream>;

/// Opens the live stream with the right token.
pub fn subscribe(port: u16) -> Result<Socket, tungstenite::Error> {
    connect(port, "/live/stream", Some(TOKEN))
}

pub fn connect(port: u16, path: &str, token: Option<&str>) -> Result<Socket, tungstenite::Error> {
    use tungstenite::client::IntoClientRequest;

    let stream = TcpStream::connect(("127.0.0.1", port)).expect("connects");
    stream.set_read_timeout(Some(Duration::from_secs(5))).unwrap();

    let mut request = format!("ws://127.0.0.1:{port}{path}").into_client_request()?;
    if let Some(token) = token {
        request
            .headers_mut()
            .insert("Authorization", format!("Bearer {token}").parse().unwrap());
    }

    let (socket, _) = tungstenite::client(request, stream).map_err(|e| match e {
        tungstenite::HandshakeError::Failure(e) => e,
        tungstenite::HandshakeError::Interrupted(_) => tungstenite::Error::ConnectionClosed,
    })?;

    Ok(socket)
}

/// The next text message, as JSON; panics after the socket's five-second timeout.
pub fn next(socket: &mut Socket) -> serde_json::Value {
    loop {
        match socket.read().expect("a message") {
            tungstenite::Message::Text(text) => {
                return serde_json::from_str(text.as_str()).expect("JSON");
            }
            tungstenite::Message::Close(_) => panic!("the socket closed"),
            _ => {}
        }
    }
}

/// The next text message exactly as it arrived.
pub fn next_text(socket: &mut Socket) -> String {
    loop {
        match socket.read().expect("a message") {
            tungstenite::Message::Text(text) => return text.as_str().to_string(),
            tungstenite::Message::Close(_) => panic!("the socket closed"),
            _ => {}
        }
    }
}

pub fn send(socket: &mut Socket, text: &str) {
    socket.send(tungstenite::Message::text(text)).expect("sends");
}
