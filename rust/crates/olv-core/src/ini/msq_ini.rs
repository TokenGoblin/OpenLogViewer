//! Ported from `MsqIni.cs`: the `[OutputChannels]` section of a MegaSquirt INI,
//! and the line reader every other INI reader here shares.
//!
//! A live connection sends a block of bytes and nothing else: no names, no
//! scaling, no layout. All of that comes from the INI that matches the firmware,
//! which is why a live session needs one and why the wrong one produces
//! plausible-looking nonsense rather than an error.

use crate::fmt;
use crate::model::{IgnoreCaseMap, RealtimeCommand, eq_ignore_case};
use regex::{Captures, Regex};
use std::sync::{LazyLock, OnceLock};

/// How one value is laid out inside a realtime block.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum RealtimeType {
    #[default]
    U08,
    S08,
    U16,
    S16,
    U32,
    S32,
    /// An IEEE single. rusEFI publishes most of its channels this way.
    F32,
}

impl RealtimeType {
    /// `Enum.TryParse<RealtimeType>(text, ignoreCase: true, ...)` for the shapes an
    /// INI can spell: the seven names, in any case.
    pub fn try_parse(text: &str) -> Option<RealtimeType> {
        match text.to_ascii_uppercase().as_str() {
            "U08" => Some(RealtimeType::U08),
            "S08" => Some(RealtimeType::S08),
            "U16" => Some(RealtimeType::U16),
            "S16" => Some(RealtimeType::S16),
            "U32" => Some(RealtimeType::U32),
            "S32" => Some(RealtimeType::S32),
            "F32" => Some(RealtimeType::F32),
            _ => None,
        }
    }

    /// Bytes one value occupies.
    pub fn size(self) -> usize {
        match self {
            RealtimeType::U08 | RealtimeType::S08 => 1,
            RealtimeType::U16 | RealtimeType::S16 => 2,
            _ => 4,
        }
    }
}

/// One channel in the ECU's realtime block: where it sits, and how to turn the
/// bytes into a number.
#[derive(Clone, Debug, PartialEq)]
pub struct RealtimeField {
    pub name: String,
    pub units: String,
    pub type_: RealtimeType,
    /// Byte offset into the realtime block.
    pub offset: i32,
    pub scale: f64,
    /// Added after scaling.
    pub transform: f64,
    pub digits: i32,
    /// Lowest bit of a packed field, or -1 when the field is a whole scalar.
    pub bit_low: i32,
    pub bit_high: i32,
}

impl RealtimeField {
    /// A float carries its own precision, so a scale of 1 says nothing about how
    /// many decimals to show. Three is what rusEFI's own datalog definition uses
    /// for most of them.
    pub const FLOAT_DIGITS: i32 = 3;

    /// A field with the C#'s defaults: no units, scale 1, no bits.
    pub fn new(name: impl Into<String>, type_: RealtimeType, offset: i32) -> Self {
        Self {
            name: name.into(),
            units: String::new(),
            type_,
            offset,
            scale: 1.0,
            transform: 0.0,
            digits: 0,
            bit_low: -1,
            bit_high: -1,
        }
    }

    pub fn is_bit_field(&self) -> bool {
        self.bit_low >= 0
    }

    pub fn size(&self) -> usize {
        self.type_.size()
    }
}

/// A channel the INI derives from others rather than reading directly.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RealtimeExpression {
    pub name: String,
    pub units: String,
    pub expression: String,
}

/// One line of the INI's datalog definition: internal name to log label.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DatalogEntry {
    pub channel: String,
    pub label: String,
    pub digits: i32,
}

/// Everything needed to ask an ECU for a realtime block and decode it.
#[derive(Clone, Debug, PartialEq)]
pub struct RealtimeLayout {
    pub block_size: i32,
    /// The INI's command template, e.g. `A` or `r\$tsCanId\x07%2o%2c`.
    pub get_command: String,
    pub fields: Vec<RealtimeField>,
    /// Derived channels, in the order the INI declares them.
    pub expressions: Vec<RealtimeExpression>,
    /// Definitions that could not be read, for reporting rather than silence.
    pub skipped: Vec<String>,
    /// Byte order of the realtime block, from the INI's `endianness`.
    pub little_endian: bool,
    /// The most the firmware will put in one reply, from `blockingFactor`. Zero
    /// means the block is read whole.
    pub blocking_factor: i32,
}

impl RealtimeLayout {
    /// True when the command is the plain serial "A" rather than a CAN read.
    pub fn uses_simple_command(&self) -> bool {
        self.get_command.trim_matches('"') == "A"
    }

    /// The request builder for this firmware's `get_command`.
    pub fn command(&self) -> RealtimeCommand {
        RealtimeCommand::parse(&self.get_command)
    }
}

/// A set of conditional-compilation symbols (`IReadOnlySet<string>`). The C#
/// default is case-insensitive; a caller's own `HashSet<string>` is ordinal.
#[derive(Clone, Debug)]
pub struct SymbolSet {
    items: Vec<String>,
    ignore_case: bool,
}

impl SymbolSet {
    /// A set compared ordinally, like `new HashSet<string> { … }`.
    pub fn ordinal<S: Into<String>>(items: impl IntoIterator<Item = S>) -> Self {
        Self {
            items: items.into_iter().map(Into::into).collect(),
            ignore_case: false,
        }
    }

    /// A set compared with `StringComparer.OrdinalIgnoreCase`.
    pub fn ignore_case<S: Into<String>>(items: impl IntoIterator<Item = S>) -> Self {
        Self {
            items: items.into_iter().map(Into::into).collect(),
            ignore_case: true,
        }
    }

    pub fn contains(&self, symbol: &str) -> bool {
        if self.ignore_case {
            self.items.iter().any(|s| eq_ignore_case(s, symbol))
        } else {
            self.items.iter().any(|s| s == symbol)
        }
    }

    pub fn len(&self) -> usize {
        self.items.len()
    }

    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }

    pub fn iter(&self) -> impl Iterator<Item = &str> {
        self.items.iter().map(String::as_str)
    }
}

impl<S: Into<String>> FromIterator<S> for SymbolSet {
    /// Collects into an ordinal set, as `HashSet<string>` would.
    fn from_iter<T: IntoIterator<Item = S>>(iter: T) -> Self {
        SymbolSet::ordinal(iter)
    }
}

/// Conditional symbols assumed when none are given. Chosen to match how a log
/// from the same firmware reads: Fahrenheit and a wideband reporting AFR, which
/// is what the sample logs show.
pub fn default_symbols() -> &'static SymbolSet {
    static DEFAULT: OnceLock<SymbolSet> = OnceLock::new();
    DEFAULT.get_or_init(|| SymbolSet::ignore_case(["CAN_COMMANDS"]))
}

fn re(pattern: &str) -> Regex {
    Regex::new(pattern).expect("a valid pattern")
}

static SCALAR: LazyLock<Regex> = LazyLock::new(|| {
    re(
        r#"^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*scalar\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*(?:,\s*"(?<units>[^"]*)"\s*)?(?:,\s*(?<scale>[-+0-9.eE]+)\s*)?(?:,\s*(?<transform>[-+0-9.eE]+)\s*)?"#,
    )
});

static BITS: LazyLock<Regex> = LazyLock::new(|| {
    re(
        r#"^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*bits\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*,\s*\[\s*(?<low>\d+)\s*:\s*(?<high>\d+)\s*\]"#,
    )
});

static EXPRESSION: LazyLock<Regex> =
    LazyLock::new(|| re(r#"^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*\{(?<body>[^}]*)\}\s*(?:,\s*"(?<units>[^"]*)")?"#));

static SETTING: LazyLock<Regex> = LazyLock::new(|| re(r"^\s*(?<name>[A-Za-z_][\w]*)\s*=\s*(?<value>.+?)\s*$"));

static DATALOG_ENTRY: LazyLock<Regex> = LazyLock::new(|| {
    re(
        r#"^\s*entry\s*=\s*(?<channel>[A-Za-z_][\w]*)\s*,\s*"(?<label>[^"]*)"\s*(?:,\s*(?<type>\w+))?\s*(?:,\s*"(?<format>[^"]*)")?"#,
    )
});

static BARE_NAME: LazyLock<Regex> = LazyLock::new(|| re(r"^[A-Za-z_]\w*$"));

/// A named group's text, or empty when it did not take part in the match.
pub(crate) fn grp<'a>(captures: &'a Captures<'_>, name: &str) -> &'a str {
    captures.name(name).map_or("", |m| m.as_str())
}

/// The `[Datalog]` section: which channels a log carries and what they are
/// called in it.
///
/// This is what makes a live session line up with a recorded one. The realtime
/// block names things internally ("boostpsig", "batteryVoltage") while a log
/// calls them "Boost psi" and "Batt V". Using the log's names means a preset, a
/// filter or a calculated channel written against a file works unchanged
/// against the ECU.
pub fn read_datalog(ini_text: &str, symbols: Option<&SymbolSet>) -> Vec<DatalogEntry> {
    let mut entries = Vec::new();

    for raw in section(ini_text, "Datalog", symbols.unwrap_or_else(|| default_symbols())) {
        let line = strip(&raw);
        let Some(m) = DATALOG_ENTRY.captures(&line) else {
            continue;
        };

        entries.push(DatalogEntry {
            channel: grp(&m, "channel").to_string(),
            label: grp(&m, "label").trim().to_string(),
            digits: digits_from_format(grp(&m, "format")),
        });
    }

    entries
}

/// Decimal places out of a printf format such as "%.3f".
fn digits_from_format(format: &str) -> i32 {
    if format.is_empty() {
        return 0;
    }
    let Some(dot) = format.find('.') else {
        return 0;
    };
    match format[dot + 1..].chars().next() {
        Some(c) if c.is_ascii_digit() => c as i32 - '0' as i32,
        _ => 0,
    }
}

/// Output channels that are nothing but another channel under a second name,
/// mapped to what they actually refer to.
///
/// A common idiom: Speeduino publishes the throttle position at `tps` and then
/// declares `throttle = { tps }` so the rest of the file can say the readable
/// one. Only a bare identifier counts: `{ tps }` is the same reading; `{ tps * 2 }`
/// is a different one.
pub fn read_aliases(ini_text: &str, symbols: Option<&SymbolSet>) -> IgnoreCaseMap<String> {
    let mut aliases = IgnoreCaseMap::new();

    for expression in read_output_channels(ini_text, symbols).expressions {
        if BARE_NAME.is_match(&expression.expression) {
            aliases.insert(&expression.name, expression.expression);
        }
    }

    aliases
}

pub fn read_output_channels(ini_text: &str, symbols: Option<&SymbolSet>) -> RealtimeLayout {
    let symbols = symbols.unwrap_or_else(|| default_symbols());

    let mut fields = Vec::new();
    let mut expressions = Vec::new();
    let mut skipped = Vec::new();

    let mut block_size = 0;
    let mut command = "A".to_string();

    for raw in section(ini_text, "OutputChannels", symbols) {
        let line = strip(&raw);
        if line.is_empty() {
            continue;
        }

        if let Some(bits) = BITS.captures(&line) {
            fields.push(from_bits(&bits));
            continue;
        }

        if let Some(scalar) = SCALAR.captures(&line) {
            match from_scalar(&scalar) {
                Some(field) => fields.push(field),
                None => skipped.push(line.clone()),
            }
            continue;
        }

        if let Some(expression) = EXPRESSION.captures(&line) {
            expressions.push(RealtimeExpression {
                name: grp(&expression, "name").to_string(),
                units: grp(&expression, "units").to_string(),
                expression: grp(&expression, "body").trim().to_string(),
            });
            continue;
        }

        let Some(setting) = SETTING.captures(&line) else {
            continue;
        };

        match grp(&setting, "name") {
            "ochBlockSize" => block_size = parse_size(grp(&setting, "value")),
            "ochGetCommand" => {
                command = grp(&setting, "value").trim().trim_matches('"').to_string();
            }
            _ => {}
        }
    }

    // The declared size wins where it is given; otherwise the furthest field
    // decides, so a layout still decodes rather than refusing to start.
    let needed = fields.iter().map(|f| f.offset + f.size() as i32).max().unwrap_or(0);

    let (little, blocking) = read_constants(ini_text, symbols);

    RealtimeLayout {
        block_size: block_size.max(needed),
        get_command: command,
        fields,
        expressions,
        skipped,
        little_endian: little,
        blocking_factor: blocking,
    }
}

/// The two things in `[Constants]` that decide how the realtime block is asked
/// for and read: its byte order, and how much of it fits in one reply.
///
/// They live in a different section from the channels they govern, which is why
/// they are easy to miss, and missing them is not a subtle failure. The wrong
/// byte order turns every reading into a different plausible number, and the
/// wrong reply size takes a rusEFI off the USB bus altogether.
fn read_constants(ini_text: &str, symbols: &SymbolSet) -> (bool, i32) {
    let mut little = false;
    let mut blocking = 0;

    for raw in section(ini_text, "Constants", symbols) {
        let line = strip(&raw);
        let Some(setting) = SETTING.captures(&line) else {
            continue;
        };

        match grp(&setting, "name") {
            "endianness" => {
                little = starts_with_ignore_case(grp(&setting, "value").trim(), "little");
            }
            "blockingFactor" => blocking = parse_size(grp(&setting, "value")),
            _ => {}
        }
    }

    (little, blocking)
}

/// `StartsWith(prefix, StringComparison.OrdinalIgnoreCase)`.
pub(crate) fn starts_with_ignore_case(text: &str, prefix: &str) -> bool {
    let mut t = text.chars();
    prefix.chars().all(|p| match t.next() {
        Some(c) => eq_ignore_case(c.encode_utf8(&mut [0; 4]), p.encode_utf8(&mut [0; 4])),
        None => false,
    })
}

/// Yields the lines of one section, with `#if` blocks resolved against the given
/// symbols. Conditionals matter: they decide whether a temperature arrives in
/// Celsius or Fahrenheit, and picking the wrong branch scales every reading of it
/// wrongly while still looking like a number.
pub(crate) fn section(text: &str, name: &str, symbols: &SymbolSet) -> Vec<String> {
    resolve(text, Some(name), symbols)
}

/// The same, for the whole file rather than one section of it.
///
/// A firmware's `#define`d option lists sit above the first heading and so belong
/// to no section at all, while still being written once per build inside `#if`,
/// which [`section`] cannot reach.
pub(crate) fn live(text: &str, symbols: &SymbolSet) -> Vec<String> {
    resolve(text, None, symbols)
}

fn resolve(text: &str, name: Option<&str>, symbols: &SymbolSet) -> Vec<String> {
    let mut lines = Vec::new();
    let mut inside = name.is_none();

    // One entry per open #if: whether this branch is live, and whether any branch
    // of it has been taken yet.
    let mut branches: Vec<(bool, bool)> = Vec::new();

    for line in text.split('\n') {
        let trimmed = line.trim();

        if let Some(name) = name
            && trimmed.starts_with('[')
        {
            inside = starts_with_ignore_case(trimmed, &format!("[{name}]"));
            if !inside && !branches.is_empty() {
                branches.clear();
            }
            continue;
        }

        if !inside {
            continue;
        }

        if let Some(condition) = trimmed.strip_prefix("#if") {
            let live = holds(condition, symbols);
            branches.push((live, live));
            continue;
        }

        if let Some(condition) = trimmed.strip_prefix("#elif") {
            let Some((_, taken)) = branches.pop() else {
                continue;
            };
            let live = !taken && holds(condition, symbols);
            branches.push((live, taken || live));
            continue;
        }

        if trimmed.starts_with("#else") {
            let Some((_, taken)) = branches.pop() else {
                continue;
            };
            branches.push((!taken, true));
            continue;
        }

        if trimmed.starts_with("#endif") {
            branches.pop();
            continue;
        }

        if branches.iter().all(|b| b.0) {
            lines.push(line.to_string());
        }
    }

    lines
}

fn holds(condition: &str, symbols: &SymbolSet) -> bool {
    let mut symbol = condition.trim();
    let negated = symbol.starts_with('!');
    if negated {
        symbol = symbol[1..].trim();
    }

    symbols.contains(symbol) ^ negated
}

/// The line without its trailing comment.
///
/// A semicolon inside quotes is part of a label rather than the start of a
/// comment. Speeduino's log separator is spelled `= bits, U08, 116, [0:1], ";",
/// ",", "tab", "space"`: cutting at the first semicolon whatever is around it
/// leaves that setting with a single option consisting of one quote mark, which
/// is neither readable nor settable. Being quote-aware can only ever keep more of
/// a line, and only where the file itself opened a quote.
pub(crate) fn strip(line: &str) -> String {
    let mut quoted = false;

    for (i, c) in line.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if c == ';' && !quoted {
            return line[..i].trim_end().to_string();
        }
    }

    line.trim_end().to_string()
}

fn from_bits(m: &Captures<'_>) -> RealtimeField {
    let low = whole(grp(m, "low"));
    let high = whole(grp(m, "high"));

    let mut field = RealtimeField::new(
        grp(m, "name"),
        RealtimeType::try_parse(grp(m, "type")).unwrap_or_default(),
        whole(grp(m, "offset")),
    );
    field.bit_low = low.min(high);
    field.bit_high = low.max(high);
    field
}

fn from_scalar(m: &Captures<'_>) -> Option<RealtimeField> {
    let type_ = RealtimeType::try_parse(grp(m, "type"))?;

    let scale = number(m.name("scale").map(|g| g.as_str()), 1.0);
    let transform = number(m.name("transform").map(|g| g.as_str()), 0.0);

    let mut field = RealtimeField::new(grp(m, "name"), type_, whole(grp(m, "offset")));
    field.units = grp(m, "units").to_string();
    field.scale = scale;
    field.transform = transform;
    field.digits = if type_ == RealtimeType::F32 {
        RealtimeField::FLOAT_DIGITS
    } else {
        digits_for(scale)
    };
    Some(field)
}

/// `int.Parse` of a run of digits the pattern already matched.
fn whole(text: &str) -> i32 {
    fmt::parse_i32(text).unwrap_or(0)
}

fn number(group: Option<&str>, fallback: f64) -> f64 {
    group.and_then(fmt::parse_f64_inv).unwrap_or(fallback)
}

/// Display precision inferred from the scale, since the INI does not state one: a
/// channel scaled by 0.1 is worth one decimal, and one scaled by 1 is a whole
/// number.
fn digits_for(scale: f64) -> i32 {
    let magnitude = scale.abs();
    if magnitude == 0.0 || magnitude >= 1.0 {
        return 0;
    }

    let digits = (-magnitude.log10()).ceil() as i32;
    digits.clamp(0, 4)
}

fn parse_size(value: &str) -> i32 {
    let text = value.trim().trim_matches(['{', '}']).trim();
    fmt::parse_i32(text).unwrap_or(0)
}
