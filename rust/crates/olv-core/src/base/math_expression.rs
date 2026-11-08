//! Ported from `MathExpression.cs`: an arithmetic expression over log channels, such as
//! "AFR - AFR Target 1" or "RPM * Torque / 5252".
//!
//! Channel names are matched against the log's own names, longest first, so a name with
//! spaces in it needs no quoting: that is how ECUs name channels, and requiring brackets
//! around "AFR Target 1" would make the common case the awkward one.
//!
//! Missing readings propagate. A sample where an input is NaN yields NaN rather than a
//! number, including through comparisons: an unknown is not "false", and silently treating
//! it as one turns a dropout into a confident wrong answer.
//!
//! The text is held as UTF-16 code units, as the C# string is, so positions in errors and
//! name lengths agree with the C# for every input.

use std::fmt;

/// Raised when an expression cannot be parsed, with where it went wrong.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MathExpressionError {
    message: String,
    position: usize,
}

impl MathExpressionError {
    fn new(message: impl Into<String>, position: usize) -> Self {
        Self {
            message: message.into(),
            position,
        }
    }

    /// What went wrong.
    pub fn message(&self) -> &str {
        &self.message
    }

    /// Index into the expression text (in UTF-16 units), for pointing at the problem.
    pub fn position(&self) -> usize {
        self.position
    }
}

impl fmt::Display for MathExpressionError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for MathExpressionError {}

const FUNCTIONS: [&str; 14] = [
    "abs", "sqrt", "min", "max", "clamp", "floor", "ceil", "round", "log", "log10", "exp", "pow", "sign", "if",
];

/// Arity of each function: (min, max) arguments.
fn arity(name: &str) -> Option<(usize, usize)> {
    Some(match name.to_ascii_lowercase().as_str() {
        "abs" | "sqrt" | "floor" | "ceil" | "log" | "log10" | "exp" | "sign" => (1, 1),
        "round" => (1, 2),
        "min" | "max" => (2, 8),
        "pow" => (2, 2),
        "clamp" | "if" => (3, 3),
        _ => return None,
    })
}

#[derive(Clone, Copy)]
enum BinaryOp {
    Add,
    Sub,
    Mul,
    Div,
    Rem,
    Pow,
    Lt,
    Le,
    Gt,
    Ge,
    Eq,
    Ne,
    And,
    BitAnd,
    BitOr,
    Or,
}

enum Node {
    Constant(f64),
    Reference(usize),
    Negate(Box<Node>),
    Not(Box<Node>),
    Binary(BinaryOp, Box<Node>, Box<Node>),
    Call(&'static str, Vec<Node>),
}

/// A parsed expression, ready to evaluate against a row of channel values.
pub struct MathExpression {
    root: Node,
    references: Vec<String>,
}

impl MathExpression {
    /// Channel names the expression reads, in the order [`evaluate`](Self::evaluate)
    /// expects their values.
    pub fn references(&self) -> &[String] {
        &self.references
    }

    /// Names the parser recognises as functions, for the UI to offer.
    pub fn function_names() -> &'static [&'static str] {
        &FUNCTIONS
    }

    pub fn evaluate(&self, inputs: &[f64]) -> f64 {
        evaluate(&self.root, inputs)
    }

    pub fn parse<S: AsRef<str>>(
        text: &str,
        channel_names: impl IntoIterator<Item = S>,
    ) -> Result<MathExpression, MathExpressionError> {
        let mut parser = Parser::new(text, channel_names);
        let root = parser.parse_all()?;
        Ok(MathExpression {
            root,
            references: parser.references,
        })
    }

    /// Parses, reporting a failure as its message rather than as an error value.
    pub fn try_parse<S: AsRef<str>>(
        text: &str,
        channel_names: impl IntoIterator<Item = S>,
    ) -> Result<MathExpression, String> {
        Self::parse(text, channel_names).map_err(|e| e.message)
    }
}

// ----- parser -----------------------------------------------------------

/// How deeply an expression may nest before it is refused.
///
/// This parser calls itself once per precedence level, so a bracket costs a dozen or so
/// stack frames and a few thousand brackets cost the whole stack. A stack overflow takes the
/// process down where nothing can catch it. It is reachable from a file: firmware INIs carry
/// expressions in `[OutputChannels]`, they are downloaded from the internet, and this
/// application asks people to drop them into a folder of their own. A depth limit turns that
/// into an ordinary parse error.
///
/// Sixty-four is far past anything written on purpose. The deepest expression in any
/// firmware INI here nests three.
const MAXIMUM_DEPTH: usize = 64;

struct Parser {
    text: Vec<u16>,
    /// Channel names, longest first, each with its UTF-16 form.
    channels: Vec<(String, Vec<u16>)>,
    references: Vec<String>,
    at: usize,
    depth: usize,
}

type Parse<T> = Result<T, MathExpressionError>;

/// A UTF-16 unit as a char, for messages and character classes; a lone surrogate is U+FFFD.
fn ch(unit: u16) -> char {
    char::from_u32(u32::from(unit)).unwrap_or('\u{FFFD}')
}

fn is_ascii_digit(unit: u16) -> bool {
    (u16::from(b'0')..=u16::from(b'9')).contains(&unit)
}

fn is_word_character(unit: u16) -> bool {
    is_ascii_digit(unit)
        || (u16::from(b'a')..=u16::from(b'z')).contains(&unit)
        || (u16::from(b'A')..=u16::from(b'Z')).contains(&unit)
        || unit == u16::from(b'_')
}

/// Ordinal ignore-case comparison of one unit, as .NET does it: upper-cased invariantly.
fn upper(unit: u16) -> u16 {
    let c = ch(unit);
    let mut it = c.to_uppercase();
    match (it.next(), it.next()) {
        (Some(u), None) if u.len_utf16() == 1 => u as u32 as u16,
        _ => unit,
    }
}

impl Parser {
    fn new<S: AsRef<str>>(text: &str, channel_names: impl IntoIterator<Item = S>) -> Self {
        // Longest first, so "AFR Target 1" wins over "AFR" at the same position. Without
        // this the tail of the name would be left behind and read as a syntax error.
        let mut channels: Vec<(String, Vec<u16>)> = channel_names
            .into_iter()
            .map(|n| n.as_ref().to_owned())
            .filter(|n| !n.is_empty())
            .map(|n| {
                let units = n.encode_utf16().collect();
                (n, units)
            })
            .collect();
        channels.sort_by_key(|c| std::cmp::Reverse(c.1.len()));

        Self {
            text: text.encode_utf16().collect(),
            channels,
            references: Vec::new(),
            at: 0,
            depth: 0,
        }
    }

    fn error(message: impl Into<String>, at: usize) -> MathExpressionError {
        MathExpressionError::new(message, at)
    }

    fn parse_all(&mut self) -> Parse<Node> {
        if self.text.iter().all(|&u| ch(u).is_whitespace()) {
            return Err(Self::error("The expression is empty.", 0));
        }

        let node = self.parse_ternary()?;
        self.skip_space();

        if self.at < self.text.len() {
            return Err(Self::error(
                format!("Unexpected '{}'.", ch(self.text[self.at])),
                self.at,
            ));
        }

        Ok(node)
    }

    /// C's conditional operator, at the lowest precedence. Firmware INI files are written in
    /// it ("rpm ? 60000.0 / rpm : 0") and it is the same thing as the "if" function,
    /// guarded branch and all.
    fn parse_ternary(&mut self) -> Parse<Node> {
        let condition = self.parse_or()?;
        if !self.take("?") {
            return Ok(condition);
        }

        let then = self.parse_ternary()?;
        if !self.take(":") {
            return Err(Self::error("A '?' needs a matching ':'.", self.at));
        }

        let otherwise = self.parse_ternary()?;
        Ok(Node::Call("if", vec![condition, then, otherwise]))
    }

    fn binary(op: BinaryOp, left: Node, right: Node) -> Node {
        Node::Binary(op, Box::new(left), Box::new(right))
    }

    fn parse_or(&mut self) -> Parse<Node> {
        let mut left = self.parse_and()?;
        while self.take("||") {
            let right = self.parse_and()?;
            left = Self::binary(BinaryOp::Or, left, right);
        }
        Ok(left)
    }

    fn parse_and(&mut self) -> Parse<Node> {
        let mut left = self.parse_bit_or()?;
        while self.take("&&") {
            let right = self.parse_bit_or()?;
            left = Self::binary(BinaryOp::And, left, right);
        }
        Ok(left)
    }

    fn parse_bit_or(&mut self) -> Parse<Node> {
        let mut left = self.parse_bit_and()?;

        // Not '||', which was taken by the caller: a single '|' here is the bitwise one, as
        // in the flag tests firmware INIs use.
        while self.peek(b'|', b'|') {
            let right = self.parse_bit_and()?;
            left = Self::binary(BinaryOp::BitOr, left, right);
        }
        Ok(left)
    }

    fn parse_bit_and(&mut self) -> Parse<Node> {
        let mut left = self.parse_comparison()?;
        while self.peek(b'&', b'&') {
            let right = self.parse_comparison()?;
            left = Self::binary(BinaryOp::BitAnd, left, right);
        }
        Ok(left)
    }

    /// Takes a single character operator, unless it is doubled.
    fn peek(&mut self, op: u8, doubled: u8) -> bool {
        self.skip_space();
        if self.at >= self.text.len() || self.text[self.at] != u16::from(op) {
            return false;
        }
        if self.at + 1 < self.text.len() && self.text[self.at + 1] == u16::from(doubled) {
            return false;
        }

        self.at += 1;
        true
    }

    fn parse_comparison(&mut self) -> Parse<Node> {
        let left = self.parse_additive()?;

        // Not chained: "a < b < c" reads as maths but means something else, so it is better
        // rejected by the trailing-input check than accepted with a surprising meaning.
        const OPS: [(&str, BinaryOp); 6] = [
            ("<=", BinaryOp::Le),
            (">=", BinaryOp::Ge),
            ("==", BinaryOp::Eq),
            ("!=", BinaryOp::Ne),
            ("<", BinaryOp::Lt),
            (">", BinaryOp::Gt),
        ];
        for (token, op) in OPS {
            if self.take(token) {
                let right = self.parse_additive()?;
                return Ok(Self::binary(op, left, right));
            }
        }

        Ok(left)
    }

    fn parse_additive(&mut self) -> Parse<Node> {
        let mut left = self.parse_term()?;
        loop {
            if self.take("+") {
                let right = self.parse_term()?;
                left = Self::binary(BinaryOp::Add, left, right);
            } else if self.take("-") {
                let right = self.parse_term()?;
                left = Self::binary(BinaryOp::Sub, left, right);
            } else {
                return Ok(left);
            }
        }
    }

    fn parse_term(&mut self) -> Parse<Node> {
        let mut left = self.parse_power()?;
        loop {
            if self.take("*") {
                let right = self.parse_power()?;
                left = Self::binary(BinaryOp::Mul, left, right);
            } else if self.take("/") {
                let right = self.parse_power()?;
                left = Self::binary(BinaryOp::Div, left, right);
            } else if self.take("%") {
                let right = self.parse_power()?;
                left = Self::binary(BinaryOp::Rem, left, right);
            } else {
                return Ok(left);
            }
        }
    }

    fn parse_power(&mut self) -> Parse<Node> {
        let left = self.parse_unary()?;

        // Right-associative, as in maths: 2^3^2 is 2^9.
        if self.take("^") {
            self.enter()?;
            let right = self.parse_power();
            self.depth -= 1;
            Ok(Self::binary(BinaryOp::Pow, left, right?))
        } else {
            Ok(left)
        }
    }

    /// A run of prefix operators recurses once per operator, so it is counted like a bracket.
    /// (The C# counted only brackets, so "-----...1" with enough minus signs overflowed the
    /// stack; so did a long run of `!` or `^`.)
    fn parse_unary(&mut self) -> Parse<Node> {
        let negate = self.take("-");
        let not = !negate && self.take("!");
        let plus = !negate && !not && self.take("+");
        if !(negate || not || plus) {
            return self.parse_primary();
        }

        self.enter()?;
        let operand = self.parse_unary();
        self.depth -= 1;
        let operand = operand?;

        Ok(if negate {
            Node::Negate(Box::new(operand))
        } else if not {
            Node::Not(Box::new(operand))
        } else {
            operand
        })
    }

    /// One more level of nesting, or the error that refuses it. The caller undoes it.
    fn enter(&mut self) -> Parse<()> {
        self.depth += 1;
        if self.depth > MAXIMUM_DEPTH {
            self.depth -= 1;
            return Err(Self::error(
                format!("The expression nests more than {MAXIMUM_DEPTH} levels deep."),
                self.at,
            ));
        }
        Ok(())
    }

    /// Counted here because everything that recurses comes through it: a bracketed group, a
    /// function's arguments, the arms of a ternary. One place to count is one place to get
    /// right.
    fn parse_primary(&mut self) -> Parse<Node> {
        self.enter()?;

        let result = self.parse_primary_inner();
        self.depth -= 1;
        result
    }

    fn parse_primary_inner(&mut self) -> Parse<Node> {
        self.skip_space();
        if self.at >= self.text.len() {
            return Err(Self::error("The expression ends early.", self.at));
        }

        if self.take("(") {
            // The whole grammar inside, not merely the part above "or". Starting lower left
            // the conditional operator out, so "rpm ? 1 : 0" parsed on its own and
            // "(rpm ? 1 : 0)" did not. The same fault reached every function argument.
            let inner = self.parse_ternary()?;
            if !self.take(")") {
                return Err(Self::error("Missing ')'.", self.at));
            }
            return Ok(inner);
        }

        let c = self.text[self.at];
        if is_ascii_digit(c) || c == u16::from(b'.') {
            return self.parse_number();
        }

        // Channels first: a log is free to name a channel "min", and the log's own names
        // should win over the built-in list.
        if let Some(channel) = self.match_channel() {
            return Ok(channel);
        }
        if let Some(word) = self.match_word()? {
            return Ok(word);
        }

        Err(Self::error(format!("Unexpected '{}'.", ch(c)), self.at))
    }

    fn parse_number(&mut self) -> Parse<Node> {
        let start = self.at;
        let len = self.text.len();
        while self.at < len && (is_ascii_digit(self.text[self.at]) || self.text[self.at] == 46) {
            self.at += 1;
        }

        // Exponent form, so 1e-3 is a number rather than "1e" minus 3.
        if self.at < len && (self.text[self.at] == 101 || self.text[self.at] == 69) {
            let mark = self.at;
            self.at += 1;
            if self.at < len && (self.text[self.at] == 43 || self.text[self.at] == 45) {
                self.at += 1;
            }

            if self.at < len && is_ascii_digit(self.text[self.at]) {
                while self.at < len && is_ascii_digit(self.text[self.at]) {
                    self.at += 1;
                }
            } else {
                self.at = mark;
            }
        }

        let token: String = self.text[start..self.at].iter().map(|&u| ch(u)).collect();
        match token.parse::<f64>() {
            Ok(value) => Ok(Node::Constant(value)),
            Err(_) => Err(Self::error(format!("'{token}' is not a number."), start)),
        }
    }

    /// Matches the longest known channel name at the current position. Names run to a word
    /// boundary so "MAPX" cannot be read as the channel "MAP" followed by an unexplained X.
    fn match_channel(&mut self) -> Option<Node> {
        let mut found: Option<(usize, String)> = None;

        for (name, units) in &self.channels {
            let n = units.len();
            if self.at + n > self.text.len() {
                continue;
            }
            let matches = self.text[self.at..self.at + n]
                .iter()
                .zip(units)
                .all(|(&a, &b)| a == b || upper(a) == upper(b));
            if !matches {
                continue;
            }

            let after = self.at + n;
            if after < self.text.len() && is_word_character(self.text[after]) {
                continue;
            }

            found = Some((after, name.clone()));
            break;
        }

        let (after, name) = found?;
        self.at = after;

        let index = match self.references.iter().position(|r| ordinal_ignore_case_eq(r, &name)) {
            Some(i) => i,
            None => {
                self.references.push(name);
                self.references.len() - 1
            }
        };

        Some(Node::Reference(index))
    }

    fn match_word(&mut self) -> Parse<Option<Node>> {
        let start = self.at;
        while self.at < self.text.len() && is_word_character(self.text[self.at]) {
            self.at += 1;
        }
        if self.at == start {
            return Ok(None);
        }

        let word: String = self.text[start..self.at].iter().map(|&u| ch(u)).collect();

        if word.eq_ignore_ascii_case("pi") {
            return Ok(Some(Node::Constant(std::f64::consts::PI)));
        }
        if word.eq_ignore_ascii_case("e") {
            return Ok(Some(Node::Constant(std::f64::consts::E)));
        }

        let Some(function) = FUNCTIONS.iter().find(|f| f.eq_ignore_ascii_case(&word)) else {
            self.at = start;
            return Err(Self::error(
                format!("'{word}' is not a channel in this log, nor a function."),
                start,
            ));
        };

        if !self.take("(") {
            return Err(Self::error(
                format!("'{word}' needs its arguments in brackets."),
                self.at,
            ));
        }

        let mut args = Vec::new();
        if !self.take(")") {
            // Each argument is a whole expression, conditionals included:
            // "min(rpm ? 1 : 2, 3)" is ordinary. Commas still separate them, because the
            // conditional operator binds tighter than a comma does.
            loop {
                args.push(self.parse_ternary()?);
                if !self.take(",") {
                    break;
                }
            }
            if !self.take(")") {
                return Err(Self::error("Missing ')'.", self.at));
            }
        }

        check_arity(&word, args.len(), start)?;
        Ok(Some(Node::Call(function, args)))
    }

    fn take(&mut self, token: &str) -> bool {
        self.skip_space();
        let bytes = token.as_bytes();
        if self.at + bytes.len() > self.text.len() {
            return false;
        }
        if !self.text[self.at..self.at + bytes.len()]
            .iter()
            .zip(bytes)
            .all(|(&a, &b)| a == u16::from(b))
        {
            return false;
        }

        self.at += bytes.len();
        true
    }

    fn skip_space(&mut self) {
        while self.at < self.text.len() && ch(self.text[self.at]).is_whitespace() {
            self.at += 1;
        }
    }
}

fn ordinal_ignore_case_eq(a: &str, b: &str) -> bool {
    let a: Vec<u16> = a.encode_utf16().collect();
    let b: Vec<u16> = b.encode_utf16().collect();
    a.len() == b.len() && a.iter().zip(&b).all(|(&x, &y)| x == y || upper(x) == upper(y))
}

fn check_arity(name: &str, count: usize, at: usize) -> Parse<()> {
    let Some((min, max)) = arity(name) else {
        return Ok(());
    };
    if count >= min && count <= max {
        return Ok(());
    }

    let wanted = if min == max {
        format!("{min}")
    } else {
        format!("{min} to {max}")
    };

    Err(MathExpressionError::new(
        format!(
            "'{name}' takes {wanted} argument{}, not {count}.",
            if max == 1 { "" } else { "s" }
        ),
        at,
    ))
}

// ----- tree -------------------------------------------------------------

fn bool_value(b: bool) -> f64 {
    if b { 1.0 } else { 0.0 }
}

/// `Math.Min`/`Math.Max` on doubles, which order negative zero before positive zero.
fn dotnet_min(a: f64, b: f64) -> f64 {
    if a < b || a.is_nan() {
        a
    } else if a == b {
        if a.is_sign_negative() { a } else { b }
    } else {
        b
    }
}

fn dotnet_max(a: f64, b: f64) -> f64 {
    if a > b || a.is_nan() {
        a
    } else if a == b {
        if a.is_sign_negative() { b } else { a }
    } else {
        b
    }
}

/// `Math.Round(value)`: to the nearest integer, halves to even.
fn round_even(value: f64) -> f64 {
    value.round_ties_even()
}

/// `Math.Round(value, digits)` as .NET Core implements it for 0 to 15 digits.
fn round_digits(value: f64, digits: usize) -> f64 {
    const POWERS: [f64; 16] = [
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
    ];
    if value.abs() < 1e16 {
        let power10 = POWERS[digits];
        let scaled = value * power10;
        round_even(scaled) / power10
    } else {
        value
    }
}

fn evaluate(node: &Node, inputs: &[f64]) -> f64 {
    match node {
        Node::Constant(v) => *v,
        Node::Reference(index) => inputs.get(*index).copied().unwrap_or(f64::NAN),
        Node::Negate(operand) => {
            let v = evaluate(operand, inputs);
            if v.is_nan() { f64::NAN } else { -v }
        }
        Node::Not(operand) => {
            let v = evaluate(operand, inputs);
            if v.is_nan() { f64::NAN } else { bool_value(v == 0.0) }
        }
        Node::Binary(op, left, right) => {
            let a = evaluate(left, inputs);
            let b = evaluate(right, inputs);

            // An unknown reading is not zero and not false. Propagating it keeps a dropout
            // visible as a gap instead of turning it into a value.
            if a.is_nan() || b.is_nan() {
                return f64::NAN;
            }

            match op {
                BinaryOp::Add => a + b,
                BinaryOp::Sub => a - b,
                BinaryOp::Mul => a * b,
                BinaryOp::Div => a / b,
                BinaryOp::Rem => a % b,
                BinaryOp::Pow => a.powf(b),
                BinaryOp::Lt => bool_value(a < b),
                BinaryOp::Le => bool_value(a <= b),
                BinaryOp::Gt => bool_value(a > b),
                BinaryOp::Ge => bool_value(a >= b),
                BinaryOp::Eq => bool_value(a == b),
                BinaryOp::Ne => bool_value(a != b),
                BinaryOp::And => bool_value(a != 0.0 && b != 0.0),
                // Bitwise, on the integer the value represents: firmware INIs test packed
                // flags this way.
                BinaryOp::BitAnd => ((a as i64) & (b as i64)) as f64,
                BinaryOp::BitOr => ((a as i64) | (b as i64)) as f64,
                BinaryOp::Or => bool_value(a != 0.0 || b != 0.0),
            }
        }
        Node::Call(name, args) => {
            // Evaluated first for every function but "if", which must not compute the branch
            // it is not taking: that branch is often the one that divides by zero.
            if *name == "if" {
                let condition = evaluate(&args[0], inputs);
                if condition.is_nan() {
                    return f64::NAN;
                }
                return if condition != 0.0 {
                    evaluate(&args[1], inputs)
                } else {
                    evaluate(&args[2], inputs)
                };
            }

            let mut values = Vec::with_capacity(args.len());
            for arg in args {
                let v = evaluate(arg, inputs);
                if v.is_nan() {
                    return f64::NAN;
                }
                values.push(v);
            }

            match *name {
                "abs" => values[0].abs(),
                "sqrt" => values[0].sqrt(),
                "floor" => values[0].floor(),
                "ceil" => values[0].ceil(),
                "round" => {
                    if values.len() == 1 {
                        round_even(values[0])
                    } else {
                        // (int)Math.Clamp(values[1], 0, 15): truncated towards zero.
                        let digits = values[1].clamp(0.0, 15.0) as usize;
                        round_digits(values[0], digits)
                    }
                }
                "log" => values[0].ln(),
                "log10" => values[0].log10(),
                "exp" => values[0].exp(),
                "sign" => {
                    // Math.Sign: -1, 0 or 1 (NaN was returned above).
                    if values[0] > 0.0 {
                        1.0
                    } else if values[0] < 0.0 {
                        -1.0
                    } else {
                        0.0
                    }
                }
                "pow" => values[0].powf(values[1]),
                "clamp" => {
                    // Math.Clamp throws when min > max; there is no exception to raise
                    // here, so the sample is unknown instead.
                    let (value, min, max) = (values[0], values[1], values[2]);
                    if min > max {
                        f64::NAN
                    } else if value < min {
                        min
                    } else if value > max {
                        max
                    } else {
                        value
                    }
                }
                "min" => values[1..].iter().fold(values[0], |r, &v| dotnet_min(r, v)),
                "max" => values[1..].iter().fold(values[0], |r, &v| dotnet_max(r, v)),
                _ => f64::NAN,
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const CHANNELS: [&str; 7] = ["RPM", "MAP", "AFR", "AFR Target 1", "CLT", "Boost psi", "Fuel: total"];

    fn eval(text: &str, values: &[(&str, f64)]) -> f64 {
        let expression = MathExpression::parse(text, CHANNELS).unwrap();

        let inputs: Vec<f64> = expression
            .references()
            .iter()
            .map(|name| {
                values
                    .iter()
                    .find(|(c, _)| c.eq_ignore_ascii_case(name))
                    .map_or(f64::NAN, |(_, v)| *v)
            })
            .collect();

        expression.evaluate(&inputs)
    }

    fn error_for(text: &str) -> Option<String> {
        MathExpression::try_parse(text, CHANNELS).err()
    }

    /// xunit's `Assert.Equal(expected, actual, precision)`: equal once both are rounded.
    fn assert_equal(expected: f64, actual: f64, precision: usize) {
        let scale = 10f64.powi(precision as i32);
        assert_eq!(
            (expected * scale).round(),
            (actual * scale).round(),
            "expected {expected}, got {actual}"
        );
    }

    // Defect fixed in the Rust: only bracket nesting was depth-limited, so a long run of
    // prefix operators or `^` overflowed the stack (an abort nothing can catch).
    #[test]
    fn long_runs_of_prefix_operators_and_powers_are_refused_not_overflowed() {
        let run = 100_000;
        for text in [
            format!("{}1", "-".repeat(run)),
            format!("{}1", "!".repeat(run)),
            format!("{}1", "+".repeat(run)),
            format!("{}1", "-!".repeat(run)),
            format!("1{}", "^1".repeat(run)),
        ] {
            let message = error_for(&text).expect("a hostile run must be a parse error");
            assert!(message.contains("nests more than"), "{message}");
        }
        // Sensible use is untouched.
        assert_eq!(eval("--1", &[]), 1.0);
        assert_eq!(eval("2^3^2", &[]), 512.0);
    }

    // The C#'s `Math.Clamp` throws when min > max; the Rust yields an unknown sample.
    #[test]
    fn clamp_with_reversed_bounds_is_unknown_not_a_crash() {
        assert!(eval("clamp(1, 5, 0)", &[]).is_nan());
        assert_eq!(eval("clamp(9, 0, 5)", &[]), 5.0);
    }

    #[test]
    fn arithmetic_follows_the_usual_precedence() {
        for (text, expected) in [
            ("1 + 2", 3.0),
            ("2 * 3 + 1", 7.0),
            ("1 + 2 * 3", 7.0),
            ("(1 + 2) * 3", 9.0),
            ("10 / 4", 2.5),
            ("10 % 3", 1.0),
            ("-5 + 2", -3.0),
            ("--5", 5.0),
            ("2 ^ 10", 1024.0),
            ("1e-3", 0.001),
            ("0.5", 0.5),
        ] {
            assert_equal(expected, eval(text, &[]), 9);
        }
    }

    #[test]
    fn power_is_right_associative() {
        assert_equal(512.0, eval("2 ^ 3 ^ 2", &[]), 9); // 2^9, not (2^3)^2
    }

    /// ECUs name channels this way. Requiring brackets would make the common case the
    /// awkward one.
    #[test]
    fn a_channel_name_with_spaces_needs_no_quoting() {
        let v = eval("AFR - AFR Target 1", &[("AFR", 13.0), ("AFR Target 1", 14.0)]);
        assert_equal(-1.0, v, 9);
    }

    /// "AFR Target 1" starts with "AFR"; taking the short one would leave " Target 1"
    /// behind as a syntax error.
    #[test]
    fn the_longest_matching_channel_name_wins() {
        let expression = MathExpression::parse("AFR Target 1", CHANNELS).unwrap();
        assert_eq!(expression.references(), ["AFR Target 1"]);
    }

    #[test]
    fn a_channel_name_is_not_matched_inside_a_longer_word() {
        assert!(error_for("MAPX + 1").unwrap().contains("MAPX"));
    }

    /// MS3 emits names like "Fuel: total".
    #[test]
    fn punctuation_in_a_channel_name_is_handled() {
        let v = eval("Fuel: total * 2", &[("Fuel: total", 21.0)]);
        assert_equal(42.0, v, 9);
    }

    #[test]
    fn each_channel_is_listed_once_however_often_it_appears() {
        let expression = MathExpression::parse("RPM + RPM * RPM", CHANNELS).unwrap();

        assert_eq!(expression.references(), ["RPM"]);
        assert_equal(12.0, expression.evaluate(&[3.0]), 9);
    }

    #[test]
    fn functions_evaluate() {
        for (text, expected) in [
            ("abs(-3)", 3.0),
            ("sqrt(16)", 4.0),
            ("min(3, 1, 2)", 1.0),
            ("max(3, 1, 2)", 3.0),
            ("clamp(9, 0, 5)", 5.0),
            ("floor(1.8)", 1.0),
            ("ceil(1.2)", 2.0),
            ("round(1.5)", 2.0),
            ("round(1.234, 2)", 1.23),
            ("sign(-9)", -1.0),
            ("pow(2, 8)", 256.0),
            ("exp(0)", 1.0),
            ("log10(1000)", 3.0),
        ] {
            assert_equal(expected, eval(text, &[]), 9);
        }
    }

    #[test]
    fn comparisons_yield_one_or_zero() {
        for (text, expected) in [
            ("1 < 2", 1.0),
            ("2 < 1", 0.0),
            ("2 >= 2", 1.0),
            ("2 != 2", 0.0),
            ("1 && 0", 0.0),
            ("1 || 0", 1.0),
            ("!0", 1.0),
        ] {
            assert_equal(expected, eval(text, &[]), 9);
        }
    }

    #[test]
    fn if_picks_a_branch() {
        assert_equal(10.0, eval("if(RPM > 500, 10, 20)", &[("RPM", 800.0)]), 9);
        assert_equal(20.0, eval("if(RPM > 500, 10, 20)", &[("RPM", 100.0)]), 9);
    }

    /// Guarding a division is the main reason to reach for "if"; evaluating both branches
    /// would defeat it.
    #[test]
    fn if_does_not_evaluate_the_branch_it_does_not_take() {
        assert_equal(0.0, eval("if(RPM == 0, 0, 100 / RPM)", &[("RPM", 0.0)]), 9);
    }

    #[test]
    fn constants_are_available() {
        assert_equal(std::f64::consts::PI, eval("pi", &[]), 9);
    }

    #[test]
    fn the_conditional_operator_works_like_if() {
        for (text, expected) in [
            ("1 ? 10 : 20", 10.0),
            ("0 ? 10 : 20", 20.0),
            ("RPM > 500 ? 1 : 2", 1.0),
            ("1 ? 2 ? 3 : 4 : 5", 3.0),
        ] {
            assert_equal(expected, eval(text, &[("RPM", 800.0)]), 9);
        }
    }

    /// Firmware INIs are written this way ("rpm ? 60000.0 / rpm : 0") and it only works if
    /// the untaken branch is not evaluated.
    #[test]
    fn the_conditional_operator_also_guards_its_branches() {
        assert_equal(0.0, eval("RPM ? 60000 / RPM : 0", &[("RPM", 0.0)]), 9);
    }

    #[test]
    fn a_conditional_without_its_colon_is_rejected() {
        assert!(error_for("1 ? 2").unwrap().contains(':'));
    }

    #[test]
    fn bitwise_operators_work_on_the_integer_value() {
        for (text, expected) in [("6 & 3", 2.0), ("6 | 1", 7.0), ("5 & 1", 1.0)] {
            assert_equal(expected, eval(text, &[]), 9);
        }
    }

    /// "1 && 0" must stay a logical and, not parse as "1 & (& 0)".
    #[test]
    fn a_single_ampersand_is_not_taken_from_a_logical_and() {
        assert_equal(0.0, eval("1 && 0", &[]), 9);
        assert_equal(1.0, eval("1 || 0", &[]), 9);
    }

    /// C's precedence, which is what firmware INIs are written against: "6 & 3 == 2" is
    /// 6 & (3 == 2), not (6 & 3) == 2. Worth pinning, because the other reading gives 1 here
    /// and would quietly change a flag test.
    #[test]
    fn bitwise_and_binds_looser_than_comparison() {
        assert_equal(0.0, eval("6 & 3 == 2", &[]), 9);
        assert_equal(1.0, eval("(6 & 3) == 2", &[]), 9);
    }

    // ----- missing readings -------------------------------------------------

    #[test]
    fn a_missing_reading_propagates_through_arithmetic() {
        assert!(eval("RPM + 1", &[]).is_nan());
    }

    /// Returning 0 would turn a dropout into a confident "no", and "if" would then take a
    /// branch on the strength of a reading that was never taken.
    #[test]
    fn a_missing_reading_is_not_treated_as_false_by_a_comparison() {
        assert!(eval("RPM > 500", &[]).is_nan());
        assert!(eval("if(RPM > 500, 1, 2)", &[]).is_nan());
    }

    #[test]
    fn a_missing_reading_propagates_through_functions() {
        assert!(eval("max(RPM, 1000)", &[]).is_nan());
    }

    // ----- errors -----------------------------------------------------------

    #[test]
    fn an_unknown_name_is_reported_by_name() {
        assert!(error_for("Torque * 2").unwrap().contains("Torque"));
    }

    #[test]
    fn malformed_expressions_are_rejected() {
        for text in [
            "",
            "   ",
            "1 +",
            "(1 + 2",
            "1 + 2)",
            "* 3",
            "abs",
            "abs(1, 2)",
            "clamp(1, 2)",
            "RPM RPM",
        ] {
            assert!(
                MathExpression::try_parse(text, CHANNELS).is_err(),
                "{text:?} should be rejected"
            );
        }
    }

    #[test]
    fn the_wrong_argument_count_says_how_many_are_wanted() {
        let error = error_for("clamp(1, 2)").unwrap();

        assert!(error.contains("clamp"));
        assert!(error.contains('3'));
    }

    #[test]
    fn parse_reports_where_the_problem_is() {
        let thrown = MathExpression::parse("RPM + + * 2", CHANNELS).err().unwrap();

        assert!(thrown.position() > 0);
    }

    /// A log is free to name a channel "min"; its own names should win.
    #[test]
    fn a_log_channel_wins_over_a_function_of_the_same_name() {
        let expression = MathExpression::parse("min + 1", ["min"]).unwrap();

        assert_eq!(expression.references(), ["min"]);
        assert_equal(4.0, expression.evaluate(&[3.0]), 9);
    }

    #[test]
    fn case_does_not_matter_when_naming_a_channel() {
        let expression = MathExpression::parse("rpm * 2", CHANNELS).unwrap();

        assert_eq!(expression.references(), ["RPM"]); // reported as the log spells it
        assert_equal(200.0, expression.evaluate(&[100.0]), 9);
    }

    /// Not a C# test: the nesting limit exists so a hostile INI is a parse error rather
    /// than a stack overflow.
    #[test]
    fn deep_nesting_is_an_error_not_a_crash() {
        let text = format!("{}1{}", "(".repeat(100), ")".repeat(100));
        assert!(error_for(&text).unwrap().contains("nests more than"));
    }
}
