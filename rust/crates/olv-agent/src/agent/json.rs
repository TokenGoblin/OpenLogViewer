//! The JSON this API speaks, written and read the way the C#'s `System.Text.Json` does.
//!
//! Not a general JSON library: it is exactly the dialect the C# server's `JsonSerializerOptions`
//! produce and accept, so that a client written against the C# sees the same bytes.
//!
//! **Writing** ([`J`]):
//! - property order is the order given (the C# serialises in declaration order);
//! - a null property is left out (`DefaultIgnoreCondition = WhenWritingNull`);
//! - a double is its shortest round-trip text, `7000` not `7000.0`, `1E-07` not `1e-7`; and
//!   NaN and the infinities are the strings `"NaN"`, `"Infinity"` and `"-Infinity"`
//!   (`AllowNamedFloatingPointLiterals`; a bare `Infinity` would break a client's frame);
//! - strings are escaped as the default `JavaScriptEncoder` does: everything outside printable
//!   ASCII is `\uXXXX` (upper-case hex, surrogate pairs for anything beyond the BMP), and the
//!   ASCII characters `" & ' + < > \`` are `"` and so on, because that encoder will not
//!   leave HTML-significant characters bare. A backslash is `\\` and the usual control characters
//!   have their short escapes.
//! - a time is ISO 8601 with up to seven fractional digits and no trailing zeros, and a `Z`.
//!
//! **Reading** ([`parse`]): strict JSON (no comments, no trailing commas, depth 64), the last of
//! two duplicate properties wins, and a UTF-8 byte-order mark is skipped. Every node knows where
//! it ended, because the C#'s error text says (`Path: $.value | LineNumber: 0 |
//! BytePositionInLine: 23.`) and a client reading that text should find the same thing here.

use std::fmt::Write as _;
use std::time::SystemTime;

use olv_core::fmt as nf;

// ----- writing -----------------------------------------------------------------------------

/// A value to be written. Built in the order the properties should appear.
#[derive(Clone, Debug, PartialEq)]
pub enum J {
    /// Left out of an object; `null` nowhere else is ever needed.
    Null,
    Bool(bool),
    Int(i64),
    Num(f64),
    Str(String),
    Arr(Vec<J>),
    Obj(Vec<(String, J)>),
    /// Text that is already JSON (a number exactly as it was sent, or a literal `null` that must
    /// survive inside an object), written as it stands.
    Raw(String),
}

impl J {
    /// An object from `(name, value)` pairs; null values vanish when it is written.
    pub fn obj<const N: usize>(fields: [(&str, J); N]) -> J {
        J::Obj(fields.into_iter().map(|(k, v)| (k.to_string(), v)).collect())
    }

    pub fn s(text: impl Into<String>) -> J {
        J::Str(text.into())
    }

    pub fn nums(values: &[f64]) -> J {
        J::Arr(values.iter().map(|v| J::Num(*v)).collect())
    }

    pub fn strs<S: AsRef<str>>(values: &[S]) -> J {
        J::Arr(values.iter().map(|v| J::s(v.as_ref())).collect())
    }

    pub fn opt_num(value: Option<f64>) -> J {
        value.map_or(J::Null, J::Num)
    }

    /// A time as the C# writes a UTC `DateTime`.
    pub fn time(at: SystemTime) -> J {
        J::Str(iso(at))
    }

    /// Compact text, as `JsonSerializer.SerializeToUtf8Bytes` gives.
    pub fn text(&self) -> String {
        let mut out = String::new();
        self.write(&mut out);
        out
    }

    /// Compact text with the relaxed escaping the MCP server uses (`UnsafeRelaxedJsonEscaping`):
    /// everything is written as it is except quotes, backslashes and control characters, because
    /// the reader is a model rather than a web page.
    pub fn text_relaxed(&self) -> String {
        let mut out = String::new();
        self.write_with(&mut out, true);
        out
    }

    pub fn write(&self, out: &mut String) {
        self.write_with(out, false);
    }

    fn write_with(&self, out: &mut String, relaxed: bool) {
        match self {
            J::Raw(text) => out.push_str(text),
            J::Null => out.push_str("null"),
            J::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
            J::Int(i) => {
                let _ = write!(out, "{i}");
            }
            J::Num(n) => write_number(out, *n),
            J::Str(s) if relaxed => write_string_relaxed(out, s),
            J::Str(s) => write_string(out, s),
            J::Arr(items) => {
                out.push('[');
                for (i, item) in items.iter().enumerate() {
                    if i > 0 {
                        out.push(',');
                    }
                    item.write_with(out, relaxed);
                }
                out.push(']');
            }
            J::Obj(fields) => {
                out.push('{');
                let mut first = true;
                for (name, value) in fields {
                    if matches!(value, J::Null) {
                        continue;
                    }
                    if !first {
                        out.push(',');
                    }
                    first = false;
                    if relaxed {
                        write_string_relaxed(out, name);
                    } else {
                        write_string(out, name);
                    }
                    out.push(':');
                    value.write_with(out, relaxed);
                }
                out.push('}');
            }
        }
    }
}

/// A double as `System.Text.Json` writes it.
pub fn write_number(out: &mut String, n: f64) {
    if n.is_nan() {
        out.push_str("\"NaN\"");
    } else if n == f64::INFINITY {
        out.push_str("\"Infinity\"");
    } else if n == f64::NEG_INFINITY {
        out.push_str("\"-Infinity\"");
    } else {
        // The invariant `ToString()`: the shortest text that reads back as the same double, in
        // scientific form (`1E-07`, `1E+21`) from 1E15 up and below 1E-5.
        out.push_str(&nf::f64_inv(n, ""));
    }
}

/// A string as the default `JavaScriptEncoder` writes it.
pub fn write_string(out: &mut String, s: &str) {
    out.push('"');

    for c in s.chars() {
        match c {
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            ' '..='~' if !matches!(c, '"' | '&' | '\'' | '+' | '<' | '>' | '`') => out.push(c),
            _ => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units) {
                    let _ = write!(out, "\\u{unit:04X}");
                }
            }
        }
    }

    out.push('"');
}

/// A string with the relaxed escaping: only what JSON itself requires.
pub fn write_string_relaxed(out: &mut String, s: &str) {
    out.push('"');

    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            c if (c as u32) < 0x20 || c == '\u{7f}' => {
                let _ = write!(out, "\\u{:04X}", c as u32);
            }
            c => out.push(c),
        }
    }

    out.push('"');
}

/// The C#'s round-trip text for a UTC `DateTime`: `2026-01-02T03:04:05.678Z`, with up to seven
/// fractional digits and none of the trailing zeros.
pub fn iso(at: SystemTime) -> String {
    let stamp = time::OffsetDateTime::from(at);
    let mut text = format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}",
        stamp.year(),
        u8::from(stamp.month()),
        stamp.day(),
        stamp.hour(),
        stamp.minute(),
        stamp.second()
    );

    // Ticks of 100 ns, as .NET counts them.
    let ticks = stamp.nanosecond() / 100;
    if ticks > 0 {
        let fraction = format!("{ticks:07}");
        text.push('.');
        text.push_str(fraction.trim_end_matches('0'));
    }

    text.push('Z');
    text
}

// ----- reading -----------------------------------------------------------------------------

/// A parsed value and where it ended.
#[derive(Clone, Debug)]
pub struct Node {
    pub value: Value,
    /// Byte offset just past the token (past the opening bracket, for an array or object: that
    /// is where `System.Text.Json` reports a value it could not convert).
    pub end: usize,
}

#[derive(Clone, Debug)]
pub enum Value {
    Null,
    Bool(bool),
    /// The number as written, so an integer can be told from a decimal.
    Number(String),
    Str(String),
    Array(Vec<Node>),
    Object(Vec<(String, Node)>),
}

impl Value {
    fn is_null(&self) -> bool {
        matches!(self, Value::Null)
    }
}

/// What `System.Text.Json` throws as `JsonException`, with its message.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct JsonError(pub String);

const MAX_DEPTH: usize = 64;

/// Parses a whole document. Empty or whitespace-only text is an error, as in the C#.
pub fn parse(text: &str) -> Result<Node, JsonError> {
    let text = text.strip_prefix('\u{feff}').unwrap_or(text);
    let mut p = Parser {
        bytes: text.as_bytes(),
        at: 0,
        path: Vec::new(),
    };

    p.skip_space();
    if p.at >= p.bytes.len() {
        return Err(p.fail_at(
            0,
            "The input does not contain any JSON tokens. Expected the input to start with a valid \
             JSON token, when isFinalBlock is true.",
            "$".to_string(),
        ));
    }

    let node = p.value(0)?;

    p.skip_space();
    if p.at < p.bytes.len() {
        let c = p.char_at(p.at);
        return Err(p.fail_at(
            p.at,
            &format!("'{c}' is invalid after a single JSON value. Expected end of data."),
            "$".to_string(),
        ));
    }

    Ok(node)
}

struct Parser<'a> {
    bytes: &'a [u8],
    at: usize,
    path: Vec<String>,
}

impl Parser<'_> {
    fn skip_space(&mut self) {
        while self.at < self.bytes.len() && matches!(self.bytes[self.at], b' ' | b'\t' | b'\r' | b'\n') {
            self.at += 1;
        }
    }

    fn char_at(&self, at: usize) -> char {
        std::str::from_utf8(&self.bytes[at..])
            .ok()
            .and_then(|s| s.chars().next())
            .unwrap_or('?')
    }

    fn path_text(&self) -> String {
        let mut text = String::from("$");
        for part in &self.path {
            if part.starts_with('[') {
                text.push_str(part);
            } else {
                text.push('.');
                text.push_str(part);
            }
        }
        text
    }

    /// The message the C# gives: the reader's complaint, then where.
    fn fail_at(&self, position: usize, message: &str, path: String) -> JsonError {
        let line_start = self.bytes[..position.min(self.bytes.len())]
            .iter()
            .rposition(|b| *b == b'\n')
            .map_or(0, |i| i + 1);
        let line = self.bytes[..position.min(self.bytes.len())]
            .iter()
            .filter(|b| **b == b'\n')
            .count();

        JsonError(format!(
            "{message} Path: {path} | LineNumber: {line} | BytePositionInLine: {}.",
            position - line_start
        ))
    }

    fn fail(&self, message: &str) -> JsonError {
        self.fail_at(self.at, message, self.path_text())
    }

    fn value(&mut self, depth: usize) -> Result<Node, JsonError> {
        self.skip_space();

        let Some(&byte) = self.bytes.get(self.at) else {
            // Inside a property or an element the reader counts the open container, and says so;
            // only a document that is nothing but whitespace gets the plain "expected a value".
            return Err(self.fail(if self.path.is_empty() {
                "Expected a value, but instead reached end of data."
            } else {
                "Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed."
            }));
        };

        match byte {
            b'{' => self.object(depth),
            b'[' => self.array(depth),
            b'"' => {
                let text = self.string()?;
                Ok(Node {
                    value: Value::Str(text),
                    end: self.at,
                })
            }
            b't' => self.literal("true", Value::Bool(true)),
            b'f' => self.literal("false", Value::Bool(false)),
            b'n' => self.literal("null", Value::Null),
            b'-' | b'0'..=b'9' => self.number(),
            _ => {
                let c = self.char_at(self.at);
                Err(self.fail(&format!("'{c}' is an invalid start of a value.")))
            }
        }
    }

    fn literal(&mut self, word: &'static str, value: Value) -> Result<Node, JsonError> {
        let rest = &self.bytes[self.at..];

        if rest.starts_with(word.as_bytes()) {
            self.at += word.len();
            return Ok(Node { value, end: self.at });
        }

        // The C# names the whole of what it found where the literal should be.
        let found = rest
            .iter()
            .take_while(|b| !matches!(**b, b',' | b' ' | b'\t' | b'\r' | b'\n'))
            .count();
        let shown = String::from_utf8_lossy(&rest[..found]).to_string();
        let common = rest.iter().zip(word.bytes()).take_while(|(a, b)| *a == b).count();

        Err(self.fail_at(
            self.at + common,
            &format!("'{shown}' is an invalid JSON literal. Expected the literal '{word}'."),
            self.path_text(),
        ))
    }

    fn number(&mut self) -> Result<Node, JsonError> {
        let start = self.at;
        let b = self.bytes;
        let mut i = start;

        if b[i] == b'-' {
            i += 1;
            match b.get(i) {
                Some(b'0'..=b'9') => {}
                Some(&c) => {
                    return Err(self.fail_at(
                        i,
                        &format!(
                            "'{}' is invalid within a number, immediately after a sign character \
                             ('+' or '-'). Expected a digit ('0'-'9').",
                            c as char
                        ),
                        self.path_text(),
                    ));
                }
                None => {
                    return Err(self.fail_at(
                        i,
                        "Expected a digit ('0'-'9'), but instead reached end of data.",
                        self.path_text(),
                    ));
                }
            }
        }

        if b[i] == b'0' {
            i += 1;
            if let Some(next @ b'0'..=b'9') = b.get(i) {
                return Err(self.fail_at(
                    i,
                    &format!("Invalid leading zero before '{}'.", *next as char),
                    self.path_text(),
                ));
            }
        } else {
            while matches!(b.get(i), Some(b'0'..=b'9')) {
                i += 1;
            }
        }

        if b.get(i) == Some(&b'.') {
            i += 1;
            if !matches!(b.get(i), Some(b'0'..=b'9')) {
                let shown = b
                    .get(i)
                    .map_or("end of data".to_string(), |c| format!("'{}'", *c as char));
                return Err(self.fail_at(
                    i,
                    &format!(
                        "{shown} is invalid within a number, immediately after a decimal point \
                         ('.'). Expected a digit ('0'-'9')."
                    ),
                    self.path_text(),
                ));
            }
            while matches!(b.get(i), Some(b'0'..=b'9')) {
                i += 1;
            }
        }

        if matches!(b.get(i), Some(b'e' | b'E')) {
            i += 1;
            if matches!(b.get(i), Some(b'+' | b'-')) {
                i += 1;
            }
            if !matches!(b.get(i), Some(b'0'..=b'9')) {
                let shown = b
                    .get(i)
                    .map_or("end of data".to_string(), |c| format!("'{}'", *c as char));
                return Err(self.fail_at(
                    i,
                    &format!(
                        "{shown} is invalid within a number, immediately after an exponent \
                         ('e' or 'E'). Expected a digit ('0'-'9')."
                    ),
                    self.path_text(),
                ));
            }
            while matches!(b.get(i), Some(b'0'..=b'9')) {
                i += 1;
            }
        }

        self.at = i;
        Ok(Node {
            value: Value::Number(String::from_utf8_lossy(&b[start..i]).to_string()),
            end: i,
        })
    }

    fn string(&mut self) -> Result<String, JsonError> {
        // On the opening quote.
        self.at += 1;
        let mut out = String::new();

        loop {
            let Some(&byte) = self.bytes.get(self.at) else {
                return Err(self.fail("Expected end of string, but instead reached end of data."));
            };

            match byte {
                b'"' => {
                    self.at += 1;
                    return Ok(out);
                }
                b'\\' => {
                    self.at += 1;
                    let Some(&escape) = self.bytes.get(self.at) else {
                        return Err(self.fail("Expected end of string, but instead reached end of data."));
                    };
                    self.at += 1;
                    match escape {
                        b'"' => out.push('"'),
                        b'\\' => out.push('\\'),
                        b'/' => out.push('/'),
                        b'b' => out.push('\u{8}'),
                        b'f' => out.push('\u{c}'),
                        b'n' => out.push('\n'),
                        b'r' => out.push('\r'),
                        b't' => out.push('\t'),
                        b'u' => {
                            let unit = self.hex4()?;
                            if (0xD800..0xDC00).contains(&unit) && self.bytes[self.at..].starts_with(b"\\u") {
                                let save = self.at;
                                self.at += 2;
                                let low = self.hex4()?;
                                if (0xDC00..0xE000).contains(&low) {
                                    let c = 0x10000 + ((u32::from(unit) - 0xD800) << 10) + (u32::from(low) - 0xDC00);
                                    out.push(char::from_u32(c).unwrap_or('\u{fffd}'));
                                    continue;
                                }
                                self.at = save;
                            }
                            out.push(char::from_u32(u32::from(unit)).unwrap_or('\u{fffd}'));
                        }
                        other => {
                            return Err(self.fail_at(
                                self.at - 1,
                                &format!(
                                    "'{}' is an invalid escapable character within a JSON string. \
                                     The string should be correctly escaped.",
                                    other as char
                                ),
                                self.path_text(),
                            ));
                        }
                    }
                }
                0..=0x1f => {
                    return Err(self.fail(&format!(
                        "'0x{byte:02X}' is invalid within a JSON string. The string should be \
                         correctly escaped."
                    )));
                }
                _ => {
                    // A whole UTF-8 sequence at once, so the text stays valid.
                    let rest = &self.bytes[self.at..];
                    let width = match byte {
                        0xC0..=0xDF => 2,
                        0xE0..=0xEF => 3,
                        0xF0..=0xF7 => 4,
                        _ => 1,
                    }
                    .min(rest.len());
                    out.push_str(&String::from_utf8_lossy(&rest[..width]));
                    self.at += width;
                }
            }
        }
    }

    fn hex4(&mut self) -> Result<u16, JsonError> {
        let mut unit: u16 = 0;

        for _ in 0..4 {
            let Some(&b) = self.bytes.get(self.at) else {
                return Err(self.fail("Expected end of string, but instead reached end of data."));
            };
            let digit = match b {
                b'0'..=b'9' => b - b'0',
                b'a'..=b'f' => b - b'a' + 10,
                b'A'..=b'F' => b - b'A' + 10,
                _ => {
                    return Err(self.fail(&format!(
                        "'{}' is not a hex digit following '\\u' within a JSON string. The string \
                         should be correctly escaped.",
                        b as char
                    )));
                }
            };
            unit = (unit << 4) | u16::from(digit);
            self.at += 1;
        }

        Ok(unit)
    }

    fn array(&mut self, depth: usize) -> Result<Node, JsonError> {
        if depth >= MAX_DEPTH {
            return Err(self.fail("The maximum configured depth of 64 has been exceeded. Cannot read next JSON array."));
        }

        self.at += 1;
        let opened = self.at;
        let mut items = Vec::new();

        loop {
            self.skip_space();
            match self.bytes.get(self.at) {
                None => {
                    return Err(self.fail(
                        "Expected depth to be zero at the end of the JSON payload. There is an open \
                         JSON object or array that should be closed.",
                    ));
                }
                Some(b']') if items.is_empty() => {
                    self.at += 1;
                    break;
                }
                _ => {}
            }

            self.path.push(format!("[{}]", items.len()));
            let item = self.value(depth + 1);
            self.path.pop();
            items.push(item?);

            self.skip_space();
            match self.bytes.get(self.at) {
                Some(b',') => {
                    self.at += 1;
                    self.skip_space();
                    if self.bytes.get(self.at) == Some(&b']') {
                        return Err(self.fail(
                            "The JSON array contains a trailing comma at the end which is not \
                             supported in this mode. Change the reader options.",
                        ));
                    }
                }
                Some(b']') => {
                    self.at += 1;
                    break;
                }
                Some(_) => {
                    let c = self.char_at(self.at);
                    return Err(self.fail(&format!(
                        "'{c}' is invalid after a value. Expected either ',', '}}', or ']'."
                    )));
                }
                None => {
                    return Err(self.fail(
                        "Expected depth to be zero at the end of the JSON payload. There is an open \
                         JSON object or array that should be closed.",
                    ));
                }
            }
        }

        let _ = opened;
        // Reported by the C# just past the opening bracket, not past the close.
        Ok(Node {
            value: Value::Array(items),
            end: opened,
        })
    }

    fn object(&mut self, depth: usize) -> Result<Node, JsonError> {
        if depth >= MAX_DEPTH {
            return Err(
                self.fail("The maximum configured depth of 64 has been exceeded. Cannot read next JSON object.")
            );
        }

        self.at += 1;
        let opened = self.at;
        let mut fields: Vec<(String, Node)> = Vec::new();

        loop {
            self.skip_space();

            match self.bytes.get(self.at) {
                None => {
                    return Err(self.fail(
                        "Expected depth to be zero at the end of the JSON payload. There is an open \
                         JSON object or array that should be closed.",
                    ));
                }
                Some(b'}') if fields.is_empty() => {
                    self.at += 1;
                    break;
                }
                Some(b'"') => {}
                Some(_) => {
                    let c = self.char_at(self.at);
                    return Err(self.fail(&format!(
                        "'{c}' is an invalid start of a property name. Expected a '\"'."
                    )));
                }
            }

            let name = self.string()?;

            self.skip_space();
            if self.bytes.get(self.at) != Some(&b':') {
                return Err(match self.bytes.get(self.at) {
                    None => self.fail("Expected a value, but instead reached end of data."),
                    Some(_) => {
                        let c = self.char_at(self.at);
                        self.fail(&format!("'{c}' is invalid after a property name. Expected a ':'."))
                    }
                });
            }
            self.at += 1;

            self.path.push(name.clone());
            let value = self.value(depth + 1);
            self.path.pop();
            let value = value?;

            // A repeated property: the last one wins, as in the C#.
            match fields.iter_mut().find(|(k, _)| *k == name) {
                Some(slot) => slot.1 = value,
                None => fields.push((name, value)),
            }

            self.skip_space();
            match self.bytes.get(self.at) {
                Some(b',') => {
                    self.at += 1;
                    self.skip_space();
                    if self.bytes.get(self.at) == Some(&b'}') {
                        return Err(self.fail(
                            "The JSON object contains a trailing comma at the end which is not \
                             supported in this mode. Change the reader options.",
                        ));
                    }
                }
                Some(b'}') => {
                    self.at += 1;
                    break;
                }
                Some(_) => {
                    let c = self.char_at(self.at);
                    return Err(self.fail(&format!(
                        "'{c}' is invalid after a value. Expected either ',', '}}', or ']'."
                    )));
                }
                None => {
                    return Err(self.fail(
                        "Expected depth to be zero at the end of the JSON payload. There is an open \
                         JSON object or array that should be closed.",
                    ));
                }
            }
        }

        Ok(Node {
            value: Value::Object(fields),
            end: opened,
        })
    }
}

// ----- typed reading -----------------------------------------------------------------------

/// Reads fields of one request record the way `JsonSerializer.Deserialize<T>` does: a field that
/// is absent keeps its default, a wrong type is a [`JsonError`] naming the C# type and the
/// path, and properties nobody asked for are ignored.
pub struct Reader<'a> {
    node: &'a Node,
    /// The C# type name, as it appears in the error text.
    type_name: &'a str,
    /// Where this record sits, for the path in the error text.
    path: String,
    source: &'a str,
}

impl<'a> Reader<'a> {
    /// Checks that the document is an object (or the literal `null`, which the caller decides
    /// about) and starts reading it.
    pub fn new(source: &'a str, node: &'a Node, type_name: &'a str) -> Result<Reader<'a>, JsonError> {
        let reader = Reader {
            node,
            type_name,
            path: "$".to_string(),
            source,
        };

        match &node.value {
            Value::Object(_) | Value::Null => Ok(reader),
            _ => Err(reader.cannot_convert(node, "$")),
        }
    }

    fn cannot_convert(&self, node: &Node, path: &str) -> JsonError {
        self.cannot_convert_to(node, path, self.type_name)
    }

    fn cannot_convert_to(&self, node: &Node, path: &str, type_name: &str) -> JsonError {
        let source = self.source.strip_prefix('\u{feff}').unwrap_or(self.source).as_bytes();
        let end = node.end.min(source.len());
        let line = source[..end].iter().filter(|b| **b == b'\n').count();
        let line_start = source[..end].iter().rposition(|b| *b == b'\n').map_or(0, |i| i + 1);

        JsonError(format!(
            "The JSON value could not be converted to {type_name}. Path: {path} | LineNumber: {line} | \
             BytePositionInLine: {}.",
            end - line_start
        ))
    }

    /// True when the document was the literal `null`.
    pub fn is_null(&self) -> bool {
        self.node.value.is_null()
    }

    fn field(&self, name: &str) -> Option<&'a Node> {
        match &self.node.value {
            Value::Object(fields) => fields.iter().find(|(k, _)| k == name).map(|(_, n)| n),
            _ => None,
        }
    }

    fn at(&self, name: &str) -> String {
        format!("{}.{name}", self.path)
    }

    /// `string?`: absent or null is `None`; anything but a string is an error.
    pub fn string(&self, name: &str) -> Result<Option<String>, JsonError> {
        match self.field(name) {
            None => Ok(None),
            Some(node) => match &node.value {
                Value::Null => Ok(None),
                Value::Str(s) => Ok(Some(s.clone())),
                _ => Err(self.cannot_convert(node, &self.at(name))),
            },
        }
    }

    /// `double`: absent is zero, null is an error, and the three named literals are accepted as
    /// strings (`AllowNamedFloatingPointLiterals`). An overflowing literal is infinity.
    pub fn double(&self, name: &str) -> Result<f64, JsonError> {
        match self.field(name) {
            None => Ok(0.0),
            Some(node) => number(node).ok_or_else(|| self.cannot_convert(node, &self.at(name))),
        }
    }

    /// `int`: an integer literal in range; a fraction or an exponent is an error.
    pub fn int(&self, name: &str) -> Result<i32, JsonError> {
        match self.field(name) {
            None => Ok(0),
            Some(node) => match &node.value {
                Value::Number(text) => text
                    .parse::<i32>()
                    .map_err(|_| self.cannot_convert(node, &self.at(name))),
                _ => Err(self.cannot_convert(node, &self.at(name))),
            },
        }
    }

    /// `bool`: `true` or `false` only; absent is false.
    pub fn boolean(&self, name: &str) -> Result<bool, JsonError> {
        match self.field(name) {
            None => Ok(false),
            Some(node) => match &node.value {
                Value::Bool(b) => Ok(*b),
                _ => Err(self.cannot_convert(node, &self.at(name))),
            },
        }
    }

    /// `bool?`.
    pub fn opt_boolean(&self, name: &str) -> Result<Option<bool>, JsonError> {
        match self.field(name) {
            None => Ok(None),
            Some(node) => match &node.value {
                Value::Null => Ok(None),
                Value::Bool(b) => Ok(Some(*b)),
                _ => Err(self.cannot_convert(node, &self.at(name))),
            },
        }
    }

    /// `string[]?`.
    pub fn strings(&self, name: &str) -> Result<Option<Vec<String>>, JsonError> {
        match self.field(name) {
            None => Ok(None),
            Some(node) => match &node.value {
                Value::Null => Ok(None),
                Value::Array(items) => {
                    let mut out = Vec::with_capacity(items.len());
                    for (i, item) in items.iter().enumerate() {
                        match &item.value {
                            // A null element of a string array is a null string.
                            Value::Null => out.push(String::new()),
                            Value::Str(s) => out.push(s.clone()),
                            _ => {
                                return Err(self.cannot_convert_to(
                                    item,
                                    &format!("{}[{i}]", self.at(name)),
                                    "System.String",
                                ));
                            }
                        }
                    }
                    Ok(Some(out))
                }
                _ => Err(self.cannot_convert_to(node, &self.at(name), "System.String[]")),
            },
        }
    }

    /// `List<T>?` of records: the items, each `None` where the JSON said null.
    pub fn records(
        &self,
        name: &str,
        list_type: &'a str,
        item_type: &'a str,
    ) -> Result<Option<Vec<Option<Reader<'a>>>>, JsonError> {
        match self.field(name) {
            None => Ok(None),
            Some(node) => match &node.value {
                Value::Null => Ok(None),
                Value::Array(items) => {
                    let mut out = Vec::with_capacity(items.len());
                    for (i, item) in items.iter().enumerate() {
                        let path = format!("{}[{i}]", self.at(name));
                        match &item.value {
                            Value::Null => out.push(None),
                            Value::Object(_) => out.push(Some(Reader {
                                node: item,
                                type_name: item_type,
                                path,
                                source: self.source,
                            })),
                            _ => return Err(self.cannot_convert_to(item, &path, item_type)),
                        }
                    }
                    Ok(Some(out))
                }
                _ => Err(self.cannot_convert_to(node, &self.at(name), list_type)),
            },
        }
    }
}

/// A JSON number, or one of the three quoted names, as a double.
fn number(node: &Node) -> Option<f64> {
    match &node.value {
        Value::Number(text) => text.parse::<f64>().ok(),
        Value::Str(s) => match s.as_str() {
            "NaN" => Some(f64::NAN),
            "Infinity" => Some(f64::INFINITY),
            "-Infinity" => Some(f64::NEG_INFINITY),
            _ => None,
        },
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn doubles_are_written_the_way_system_text_json_writes_them() {
        let cases = [
            (7000.0, "7000"),
            (5000.5, "5000.5"),
            (0.1 + 0.2, "0.30000000000000004"),
            (1e15, "1000000000000000"),
            (1e-7, "1E-07"),
            (1e21, "1E+21"),
            (-0.0, "-0"),
            (123456789.125, "123456789.125"),
            (1.2345678901234568e29, "1.2345678901234568E+29"),
            (f64::NAN, "\"NaN\""),
            (f64::INFINITY, "\"Infinity\""),
            (f64::NEG_INFINITY, "\"-Infinity\""),
        ];

        for (value, expected) in cases {
            assert_eq!(J::Num(value).text(), expected, "{value:e}");
        }
    }

    #[test]
    fn strings_are_escaped_as_the_default_javascript_encoder_does() {
        assert_eq!(
            J::s("n=3; \u{b1} \u{2014} it's <a> & \"b\" + `c` \\ \n\t/").text(),
            "\"n=3; \\u00B1 \\u2014 it\\u0027s \\u003Ca\\u003E \\u0026 \\u0022b\\u0022 \\u002B \
             \\u0060c\\u0060 \\\\ \\n\\t/\""
        );
        // Beyond the BMP: a surrogate pair, upper-case hex.
        assert_eq!(J::s("\u{1F600}").text(), "\"\\uD83D\\uDE00\"");
        assert_eq!(J::s("\u{1}\u{7f}").text(), "\"\\u0001\\u007F\"");
    }

    #[test]
    fn a_null_property_is_left_out_and_order_is_kept() {
        let j = J::obj([("b", J::Int(1)), ("a", J::Null), ("c", J::Bool(true))]);
        assert_eq!(j.text(), "{\"b\":1,\"c\":true}");
    }

    #[test]
    fn times_trim_trailing_zeros_and_end_in_z() {
        let at = |secs: u64, nanos: u32| SystemTime::UNIX_EPOCH + std::time::Duration::new(secs, nanos);
        assert_eq!(iso(at(1_767_323_045, 678_000_000)), "2026-01-02T03:04:05.678Z");
        assert_eq!(iso(at(1_767_323_045, 0)), "2026-01-02T03:04:05Z");
        assert_eq!(iso(at(1_767_323_045, 123_456_700)), "2026-01-02T03:04:05.1234567Z");
    }

    #[test]
    fn parse_keeps_the_last_duplicate_and_reports_where_a_value_ended() {
        let node = parse(r#"{"a":1,"a":2,"b":"x"}"#).unwrap();
        let Value::Object(fields) = &node.value else { panic!() };
        assert_eq!(fields.len(), 2);
        assert!(matches!(&fields[0].1.value, Value::Number(n) if n == "2"));
        assert_eq!(fields[1].1.end, 20);
    }

    #[test]
    fn syntax_errors_say_what_system_text_json_says() {
        assert_eq!(
            parse("").unwrap_err().0,
            "The input does not contain any JSON tokens. Expected the input to start with a valid JSON \
             token, when isFinalBlock is true. Path: $ | LineNumber: 0 | BytePositionInLine: 0."
        );
        assert_eq!(
            parse(r#"{"name":x}"#).unwrap_err().0,
            "'x' is an invalid start of a value. Path: $.name | LineNumber: 0 | BytePositionInLine: 8."
        );
        assert_eq!(
            parse(r#"{"name":"x","value":1,}"#).unwrap_err().0,
            "The JSON object contains a trailing comma at the end which is not supported in this mode. \
             Change the reader options. Path: $ | LineNumber: 0 | BytePositionInLine: 22."
        );
        assert_eq!(
            parse("{} x").unwrap_err().0,
            "'x' is invalid after a single JSON value. Expected end of data. Path: $ | LineNumber: 0 | \
             BytePositionInLine: 3."
        );
    }

    #[test]
    fn a_bom_is_skipped_and_depth_is_limited() {
        assert!(parse("\u{feff}{}").is_ok());
        assert!(parse(&"[".repeat(70)).is_err());
    }
}
