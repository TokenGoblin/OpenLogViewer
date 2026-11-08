//! Ported from `TuneLayout.cs`: the `[Constants]` section, the shape of the ECU's
//! settings.
//!
//! This is the other half of what an INI is for. The output channels say what the
//! ECU is doing; the constants say what it has been told to do: the tables, the
//! limits and every setting behind them. Reading them from the ECU rather than
//! from a saved file is what makes the tune on screen the tune that is actually
//! running.

use super::ini_defines;
use super::msq_ini::{self, RealtimeType, SymbolSet, default_symbols, grp};
use crate::fmt;
use crate::model::IgnoreCaseMap;
use regex::{Captures, Regex};
use std::sync::LazyLock;

/// One page of the ECU's settings, and how to ask for it.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TunePage {
    /// Position in the INI's page list, from zero.
    pub index: i32,
    pub size: i32,
    /// The bytes that name this page in a request: the INI's `pageIdentifier`,
    /// which is a byte string rather than a number and may contain the CAN id.
    pub identifier: String,
    /// Template for reading part of it, e.g. `R%2o%2c`.
    pub read_command: String,
    /// Template for writing a run of bytes into it, e.g. `C%2o%2c%v`. Empty when
    /// the firmware declares none.
    pub chunk_write_command: String,
    /// Template for committing the page to flash, e.g. `B` or `b%2i`.
    ///
    /// Separate from writing on purpose, and on the ECU as well as here: a write
    /// lands in the controller's working memory and is lost at the next power
    /// cycle, while a burn is permanent.
    pub burn_command: String,
}

/// One setting in the ECU's memory: where it lives and what the bytes mean.
///
/// The same idea as a realtime field, for a different block. A scalar is a lone
/// value, an array is a table or a set of breakpoints, and a bit field is one of
/// several choices packed into a byte.
#[derive(Clone, Debug, PartialEq)]
pub struct TuneConstant {
    pub name: String,
    pub page: i32,
    pub offset: i32,
    pub type_: RealtimeType,
    pub units: String,
    pub scale: f64,
    /// What is added to the stored number *before* scaling: `value = (raw +
    /// Transform) x Scale`, and back the other way `raw = value / Scale -
    /// Transform`.
    ///
    /// That order is not the obvious one and getting it round the wrong way is
    /// silent, so here is the evidence. Speeduino declares its coolant axis `U08,
    /// 1.8, -22.23, -40, 215, "F"` and the controller stores coolant as degrees C +
    /// 40. The bytes read off a real one are 0, 30, 60, 70, 85, 100. Adding first
    /// gives -40.0, 14.0, 68.0, 86.0, 113.0, 140.0 F, which is -40, -10, 20, 30, 45
    /// and 60 C, every one a round number, and -40 is where the two scales meet.
    /// Scaling first gives -22.2, 31.8, 85.8 and so on: no pattern, and a top end
    /// past the 215 the firmware declares.
    ///
    /// Three more say the same. MS2 declares a VE trim `S08, 0.09765625, 1024`
    /// reading 100% at rest, MS3's second fuel temperature is `S16, 0.05555, -320,
    /// "C"`, and rusEFI stores a fuel trim as a ratio, `F32, 100, -1, "%"`. Across
    /// four firmwares not one constant is decided the other way.
    pub transform: f64,
    /// The scale as the firmware wrote it, when it wrote an expression rather than
    /// a number, and empty otherwise.
    ///
    /// Held as text because what it comes to depends on the tune rather than on the
    /// definition: MS2's MAF curve is scaled `{0.01 * (maf_range + 1)}`, and
    /// `maf_range` is a setting. Until the tune is known, `scale` holds the
    /// fallback.
    pub scale_expression: String,
    /// The same for the translate.
    pub transform_expression: String,
    pub digits: i32,
    /// The range the firmware says this may hold, in the units it is displayed in.
    /// NaN where the file does not say.
    ///
    /// Worth having because it is much tighter than the datatype's, and it is the
    /// datatype that would otherwise be the only guard when a value is being
    /// written back. An ignition table stored as a signed 16-bit number at a tenth
    /// of a degree accepts -3,276 to 3,276 degrees of advance as far as the
    /// encoding is concerned; the firmware declares -10 to 60, which is the figure
    /// that means something.
    pub low: f64,
    pub high: f64,
    /// Columns, or 1 for a scalar and for a one-dimensional array.
    pub columns: i32,
    /// Rows, or 1 for a scalar.
    pub rows: i32,
    pub bit_low: i32,
    pub bit_high: i32,
    /// True for a setting holding characters rather than a number: a name the user
    /// gives an input. `columns` is its length in bytes.
    pub is_text: bool,
    /// What each value of a bit field means, in order from zero.
    ///
    /// The firmware names them ("Disabled", "Narrow Band", "Wide Band") and without
    /// the names the setting is a number between nought and three with nothing to
    /// say which is which.
    ///
    /// A slot the firmware has not used is spelled "INVALID", or left empty, and is
    /// a value the user must not be offered: it is padding to fill the bit width
    /// out to a power of two, not a choice.
    ///
    /// Every slot is kept, empty ones included, because the position in this list
    /// *is* the number the ECU stores. Dropping the unused ones would renumber
    /// every option after the gap.
    pub options: Vec<String>,
}

impl TuneConstant {
    /// A constant with the C#'s defaults: scale 1, no range, one cell, no bits.
    pub fn new(name: impl Into<String>, page: i32, offset: i32, type_: RealtimeType) -> Self {
        Self {
            name: name.into(),
            page,
            offset,
            type_,
            units: String::new(),
            scale: 1.0,
            transform: 0.0,
            scale_expression: String::new(),
            transform_expression: String::new(),
            digits: 0,
            low: f64::NAN,
            high: f64::NAN,
            columns: 1,
            rows: 1,
            bit_low: -1,
            bit_high: -1,
            is_text: false,
            options: Vec::new(),
        }
    }

    /// Whether the firmware stated a usable range.
    pub fn has_range(&self) -> bool {
        !self.low.is_nan() && !self.high.is_nan() && self.high > self.low
    }

    pub fn is_bit_field(&self) -> bool {
        self.bit_low >= 0
    }

    pub fn is_array(&self) -> bool {
        self.columns * self.rows > 1
    }

    pub fn element_size(&self) -> i32 {
        self.type_.size() as i32
    }

    /// Bytes this constant occupies.
    pub fn size(&self) -> i32 {
        self.element_size() * self.columns * self.rows
    }

    /// False for a setting that lives on this machine rather than in the ECU, which
    /// has no page to be written to.
    pub fn on_controller(&self) -> bool {
        self.page >= 0
    }

    pub fn has_options(&self) -> bool {
        !self.options.is_empty()
    }

    /// Whether a value names something the firmware actually does.
    pub fn is_valid_option(&self, value: i32) -> bool {
        value >= 0
            && (value as usize) < self.options.len()
            && !self.options[value as usize].is_empty()
            && !self.options[value as usize].eq_ignore_ascii_case("INVALID")
    }

    /// What this value is called, or the number where it has no name.
    pub fn option_name(&self, value: f64) -> String {
        let index = value.round_ties_even() as i32;

        if index >= 0 && (index as usize) < self.options.len() {
            self.options[index as usize].clone()
        } else {
            fmt::f64(value, "")
        }
    }
}

/// Everything needed to read an ECU's settings and make sense of them.
#[derive(Clone, Debug)]
pub struct TuneLayout {
    pub pages: Vec<TunePage>,
    pub constants: Vec<TuneConstant>,
    /// Settings that live on this machine rather than in the ECU: gauge limits,
    /// axis maxima, the units a dialog prefers.
    ///
    /// Kept apart from `constants` rather than flagged among them, deliberately.
    /// Every one of these has a name and a scale exactly like a real constant and
    /// nothing about it says it is not one, but it has no page and no offset, so
    /// anything that treated it as one would be writing to whatever happens to sit
    /// at offset zero of page zero. Two lists cannot make that mistake; one list and
    /// a flag could.
    pub pc_variables: Vec<TuneConstant>,
    /// Values the firmware derives in `[OutputChannels]`, by name.
    ///
    /// Kept because a scale is sometimes stated in terms of one. A Speeduino scales
    /// every load axis by `{fuelLoadRes}`, which is not a setting at all but a line
    /// in `[OutputChannels]` reading `{ ((algorithm == 0) || (algorithm == 2)) ?
    /// 2.000 : 0.500 }`. A resolver that only knows about constants cannot find it,
    /// falls back to the declared scale of 1, and reads every one of those axes at
    /// half.
    pub derived: IgnoreCaseMap<String>,
    pub little_endian: bool,
    pub blocking_factor: i32,
    /// Milliseconds to pause between consecutive writes, as the firmware asks.
    ///
    /// MS2Extra declares one millisecond. Small, and declared for a reason: the
    /// controller is copying bytes into its own memory between messages, and the
    /// next request arriving underneath that is how a write ends up half applied.
    pub inter_write_delay: i32,
    /// Milliseconds to wait after a burn before speaking again.
    ///
    /// Ten on MS2Extra, where the file calls it the delay after the burn command.
    /// Writing flash stops the controller answering for as long as it takes, so a
    /// request sent immediately afterwards is a request sent into silence, and one
    /// that then desynchronises everything after it.
    pub after_burn_delay: i32,
}

impl TuneLayout {
    /// Total bytes across every page: what a full read costs.
    pub fn total_size(&self) -> i32 {
        self.pages.iter().map(|p| p.size).sum()
    }
}

fn re(pattern: &str) -> Regex {
    Regex::new(pattern).expect("a valid pattern")
}

static SCALAR: LazyLock<Regex> = LazyLock::new(|| {
    re(r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*scalar\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*(?:,\s*(?<rest>.*))?$")
});

static ARRAY: LazyLock<Regex> = LazyLock::new(|| {
    re(
        r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*array\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*,\s*\[\s*(?<cols>\d+)\s*(?:x\s*(?<rows>\d+)\s*)?\]\s*(?:,\s*(?<rest>.*))?$",
    )
});

static BITS: LazyLock<Regex> = LazyLock::new(|| {
    re(
        r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*bits\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*(?<offset>\d+)\s*,\s*\[\s*(?<low>\d+)\s*:\s*(?<high>\d+)\s*\](?:\s*,\s*(?<rest>.*))?$",
    )
});

/// A text setting: `scriptTableName1 = string, ASCII, 2168, 16` on the controller,
/// or `sensor01Alias = string, ASCII, 20` on this machine, where there is no offset
/// to give. Names a user assigns to an input, so it is a setting like any other
/// even though it holds no number.
static TEXT: LazyLock<Regex> = LazyLock::new(|| {
    re(r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*string\s*,\s*(?<encoding>\w+)\s*,\s*(?<first>\d+)\s*(?:,\s*(?<second>\d+))?")
});

static SETTING: LazyLock<Regex> = LazyLock::new(|| re(r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*(?<value>.+?)\s*$"));

/// A PC variable's scalar form, which is a constant's without the offset: there is
/// nowhere in the ECU for it to be at.
static PC_SCALAR: LazyLock<Regex> =
    LazyLock::new(|| re(r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*scalar\s*,\s*(?<type>[A-Z]\d\d)\s*(?:,\s*(?<rest>.*))?$"));

static PC_BITS: LazyLock<Regex> = LazyLock::new(|| {
    re(
        r"^\s*(?<name>[A-Za-z_]\w*)\s*=\s*bits\s*,\s*(?<type>[A-Z]\d\d)\s*,\s*\[\s*(?<low>\d+)\s*:\s*(?<high>\d+)\s*\](?:\s*,\s*(?<rest>.*))?$",
    )
});

pub fn read(ini_text: &str, symbols: Option<&SymbolSet>) -> TuneLayout {
    let symbols = symbols.unwrap_or_else(|| default_symbols());

    // The named option lists, needed before any bit field is read: a great many of
    // them name their values by pointing at one of these.
    let defines = ini_defines::read(ini_text, Some(symbols));

    let mut constants = Vec::new();

    let mut pages = 0;
    let mut little = false;
    let mut blocking = 0;
    let mut page = 0;
    let mut inter_write = 0;
    let mut after_burn = 0;

    let mut sizes: Vec<i32> = Vec::new();
    let mut identifiers: Vec<String> = Vec::new();
    let mut read_commands: Vec<String> = Vec::new();
    let mut chunk_writes: Vec<String> = Vec::new();
    let mut burns: Vec<String> = Vec::new();

    for raw in msq_ini::section(ini_text, "Constants", symbols) {
        let line = msq_ini::strip(&raw);
        if line.is_empty() {
            continue;
        }

        if let Some(bits) = BITS.captures(&line) {
            constants.push(from_bits(&bits, page, Some(&defines)));
            continue;
        }

        if let Some(array) = ARRAY.captures(&line) {
            if let Some(constant) = from_array(&array, page) {
                constants.push(constant);
            }
            continue;
        }

        if let Some(scalar) = SCALAR.captures(&line) {
            if let Some(constant) = from_scalar(&scalar, page) {
                constants.push(constant);
            }
            continue;
        }

        if let Some(text) = TEXT.captures(&line) {
            if let Some(constant) = from_text(&text, page) {
                constants.push(constant);
            }
            continue;
        }

        let Some(setting) = SETTING.captures(&line) else {
            continue;
        };

        let value = grp(&setting, "value");

        match grp(&setting, "name") {
            // Constants after this line belong to the page it names. Pages are
            // numbered from one in the file and from zero everywhere else, which is
            // a good way to read a table off by 1024 bytes.
            "page" => page = (whole(value) - 1).max(0),

            "nPages" => pages = whole(value),
            "pageSize" => sizes = list(value).iter().map(|s| whole(s)).collect(),
            "pageIdentifier" => identifiers = list(value).iter().map(|s| unquote(s)).collect(),
            "pageReadCommand" => read_commands = list(value).iter().map(|s| unquote(s)).collect(),
            "pageChunkWrite" => chunk_writes = list(value).iter().map(|s| unquote(s)).collect(),
            "burnCommand" => burns = list(value).iter().map(|s| unquote(s)).collect(),
            "blockingFactor" => blocking = whole(value),

            // Timings the firmware asks for and TunerStudio observes. Both are
            // about giving a controller time to finish what it was told to do
            // before it is told anything else.
            "interWriteDelay" => inter_write = whole(value),
            "pageActivationDelay" => after_burn = whole(value),

            "endianness" => {
                little = msq_ini::starts_with_ignore_case(value.trim(), "little");
            }

            _ => {}
        }
    }

    if pages == 0 {
        pages = (sizes.len() as i32).max(1);
    }

    let mut built = Vec::with_capacity(pages.max(0) as usize);
    for i in 0..pages {
        let size = at_size(&sizes, i);
        if size <= 0 {
            continue;
        }

        built.push(TunePage {
            index: i,
            size,
            identifier: at(&identifiers, i).cloned().unwrap_or_default(),
            read_command: at(&read_commands, i).cloned().unwrap_or_default(),
            chunk_write_command: at(&chunk_writes, i).cloned().unwrap_or_default(),
            burn_command: at(&burns, i).cloned().unwrap_or_default(),
        });
    }

    let mut derived = IgnoreCaseMap::new();
    for expression in msq_ini::read_output_channels(ini_text, Some(symbols)).expressions {
        derived.insert(&expression.name, expression.expression);
    }

    TuneLayout {
        pages: built,
        constants,
        pc_variables: read_pc_variables(ini_text, symbols, &defines),
        derived,
        little_endian: little,
        blocking_factor: blocking,
        inter_write_delay: inter_write,
        after_burn_delay: after_burn,
    }
}

/// The `[PcVariables]` section, which is written almost like `[Constants]` and
/// means something quite different.
///
/// Given a page of -1, so that anything reaching for a page gets an obviously wrong
/// answer rather than a plausible one.
fn read_pc_variables(ini_text: &str, symbols: &SymbolSet, defines: &IgnoreCaseMap<Vec<String>>) -> Vec<TuneConstant> {
    let mut variables = Vec::new();

    for raw in msq_ini::section(ini_text, "PcVariables", symbols) {
        let line = msq_ini::strip(&raw);
        if line.is_empty() {
            continue;
        }

        if let Some(bits) = PC_BITS.captures(&line) {
            let bit_type = RealtimeType::try_parse(grp(&bits, "type")).unwrap_or_default();

            let low = whole(grp(&bits, "low"));
            let high = whole(grp(&bits, "high"));

            let mut variable = TuneConstant::new(grp(&bits, "name"), -1, 0, bit_type);
            variable.bit_low = low.min(high);
            variable.bit_high = low.max(high);
            variable.options = named(grp(&bits, "rest"), Some(defines));
            variables.push(variable);

            continue;
        }

        if let Some(text) = TEXT.captures(&line) {
            if let Some(variable) = from_text(&text, -1) {
                variables.push(variable);
            }
            continue;
        }

        let Some(scalar) = PC_SCALAR.captures(&line) else {
            continue;
        };
        let Some(type_) = RealtimeType::try_parse(grp(&scalar, "type")) else {
            continue;
        };

        let rest = fields(grp(&scalar, "rest"));

        let mut variable = TuneConstant::new(grp(&scalar, "name"), -1, 0, type_);
        apply_scalar_fields(&mut variable, &rest);
        variables.push(variable);
    }

    variables
}

/// The value for one page, from a list that may hold one entry for all of them. An
/// INI states a per-page list where the pages differ and a single value where they
/// do not.
fn at<T>(values: &[T], index: i32) -> Option<&T> {
    match values.len() {
        0 => None,
        1 => Some(&values[0]),
        n => {
            if index >= 0 && (index as usize) < n {
                Some(&values[index as usize])
            } else {
                None
            }
        }
    }
}

fn at_size(values: &[i32], index: i32) -> i32 {
    at(values, index).copied().unwrap_or(0)
}

/// Units, scale, translate, their expressions, the limits and the digits: the
/// fields a scalar and an array share, in the order the INI writes them.
fn apply_scalar_fields(constant: &mut TuneConstant, rest: &[String]) {
    constant.units = if !rest.is_empty() {
        unquote(&rest[0])
    } else {
        String::new()
    };
    constant.scale = if rest.len() > 1 { number(&rest[1], 1.0) } else { 1.0 };
    constant.transform = if rest.len() > 2 { number(&rest[2], 0.0) } else { 0.0 };
    constant.scale_expression = expression(rest, 1);
    constant.transform_expression = expression(rest, 2);

    // Fields four and five are the firmware's own limits, in displayed units. Read
    // rather than skipped over: they are what an edit should be held to, and they
    // are stricter than the datatype.
    constant.low = if rest.len() > 3 {
        number(&rest[3], f64::NAN)
    } else {
        f64::NAN
    };
    constant.high = if rest.len() > 4 {
        number(&rest[4], f64::NAN)
    } else {
        f64::NAN
    };
    constant.digits = if rest.len() > 5 {
        number(&rest[5], 0.0) as i32
    } else {
        0
    };
}

fn from_scalar(m: &Captures<'_>, page: i32) -> Option<TuneConstant> {
    let type_ = RealtimeType::try_parse(grp(m, "type"))?;

    let rest = fields(grp(m, "rest"));

    let mut constant = TuneConstant::new(grp(m, "name"), page, whole(grp(m, "offset")), type_);
    apply_scalar_fields(&mut constant, &rest);
    Some(constant)
}

fn from_array(m: &Captures<'_>, page: i32) -> Option<TuneConstant> {
    let type_ = RealtimeType::try_parse(grp(m, "type"))?;

    let rest = fields(grp(m, "rest"));

    // "[16x16]" is columns by rows; "[16]" is a single run, which is what a set of
    // axis breakpoints is.
    let columns = whole(grp(m, "cols"));
    let rows = if m.name("rows").is_some() {
        whole(grp(m, "rows"))
    } else {
        1
    };

    if columns < 1 || rows < 1 {
        return None;
    }

    let mut constant = TuneConstant::new(grp(m, "name"), page, whole(grp(m, "offset")), type_);
    constant.columns = columns;
    constant.rows = rows;
    apply_scalar_fields(&mut constant, &rest);
    Some(constant)
}

/// A text setting. On the controller both an offset and a length are given; on this
/// machine there is no offset, so the one number present is the length.
fn from_text(m: &Captures<'_>, page: i32) -> Option<TuneConstant> {
    let has_offset = m.name("second").is_some();

    // On the controller an offset is not optional. Defaulting it to zero would read
    // and write the first bytes of the page, which belong to whichever constants
    // really live there.
    if page >= 0 && !has_offset {
        return None;
    }

    let mut constant = TuneConstant::new(
        grp(m, "name"),
        page,
        if has_offset { whole(grp(m, "first")) } else { 0 },
        RealtimeType::U08,
    );
    constant.is_text = true;
    constant.columns = whole(grp(m, if has_offset { "second" } else { "first" }));
    Some(constant)
}

fn from_bits(m: &Captures<'_>, page: i32, defines: Option<&IgnoreCaseMap<Vec<String>>>) -> TuneConstant {
    let type_ = RealtimeType::try_parse(grp(m, "type")).unwrap_or_default();

    let low = whole(grp(m, "low"));
    let high = whole(grp(m, "high"));

    let mut constant = TuneConstant::new(grp(m, "name"), page, whole(grp(m, "offset")), type_);
    constant.bit_low = low.min(high);
    constant.bit_high = low.max(high);
    constant.options = named(grp(m, "rest"), defines);
    constant
}

/// The labels for a bit field's values, or none where it has none.
///
/// A great many bit fields name nothing: every reserved bit, and every flag a
/// firmware leaves as a plain number. Both splitters end by yielding whatever
/// follows the last comma, so an empty list comes back as one blank label rather
/// than as nothing, and a setting with one blank option is drawn as a list to pick
/// from with nothing in it: unreadable and unsettable, where a number box would
/// have worked. Blank labels *within* a list are kept, because the position of a
/// label is the value it stands for.
///
/// A list of nothing but blanks goes the same way: Speeduino spells one reserved
/// field `[1:7], ""`, which is a seven-bit number with one empty label. Nothing in
/// such a list names a value, so there is nothing to lose by having none.
fn named(rest: &str, defines: Option<&IgnoreCaseMap<Vec<String>>>) -> Vec<String> {
    if rest.trim().is_empty() {
        return Vec::new();
    }

    let named: Vec<String> = match defines {
        None => fields(rest).iter().map(|s| unquote(s)).collect(),
        Some(defines) => ini_defines::expand(rest, defines),
    };

    if named.iter().all(String::is_empty) {
        Vec::new()
    } else {
        named
    }
}

/// The field as written, when it was written as an expression rather than a
/// number, and nothing otherwise.
///
/// A firmware states a scale in terms of another setting where the meaning of a
/// table depends on how it is configured: MS2's MAF curve is `{0.01 * (maf_range +
/// 1)}` grammes a second, because the same bytes cover a 650 g/s sensor and a 2,600
/// g/s one. Parsed as a number that fails and falls back to a scale of 1, which is
/// out by a factor of the range and looks like an ordinary number the whole way.
fn expression(rest: &[String], index: usize) -> String {
    if index < rest.len() && rest[index].trim_start().starts_with('{') {
        rest[index].trim().to_string()
    } else {
        String::new()
    }
}

/// Splits a comma list, respecting quotes and braces.
fn fields(text: &str) -> Vec<String> {
    let mut parts = Vec::new();
    let mut start = 0;
    let mut braces = 0_i32;
    let mut quoted = false;

    for (i, c) in text.char_indices() {
        if c == '"' {
            quoted = !quoted;
        } else if !quoted && c == '{' {
            braces += 1;
        } else if !quoted && c == '}' {
            braces -= 1;
        } else if c == ',' && !quoted && braces <= 0 {
            parts.push(text[start..i].trim().to_string());
            start = i + 1;
        }
    }

    parts.push(text[start..].trim().to_string());

    parts
}

fn list(value: &str) -> Vec<String> {
    fields(value).into_iter().filter(|f| !f.is_empty()).collect()
}

fn whole(text: &str) -> i32 {
    fmt::parse_i32(text.trim()).unwrap_or(0)
}

fn number(text: &str, fallback: f64) -> f64 {
    fmt::parse_f64_inv(text.trim()).unwrap_or(fallback)
}

fn unquote(text: &str) -> String {
    let trimmed = text.trim();

    if trimmed.len() >= 2 && trimmed.starts_with('"') && trimmed.ends_with('"') {
        trimmed[1..trimmed.len() - 1].to_string()
    } else {
        trimmed.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const INI: &str = "[Constants]\n\
        endianness = big\n\
        nPages = 2\n\
        pageSize = 128, 64\n\
        pageIdentifier = \"\\$tsCanId\\x01\", \"\\$tsCanId\\x02\"\n\
        burnCommand = \"b%2i\"\n\
        page = 1\n\
        veTable = array, U08, 0, [2x2], \"%\", 1.0, 0.0, 0, 255, 0\n\
        rpmBins = array, U16, 4, [3], \"RPM\", 1.0, 0.0\n\
        reqFuel = scalar, U08, 12, \"ms\", 0.1, 0.0, 0.0, 25.5, 1\n\
        mode = bits, U08, 13, [0:1], \"Off\", \"On\", \"INVALID\"\n\
        name = string, ASCII, 20, 8\n\
        page = 2\n\
        other = scalar, S16, 0, \"x\", {0.01 * (range + 1)}, 5\n\
        [PcVariables]\n\
        gauge = scalar, U08, \"x\", 1, 0\n\
        [OutputChannels]\n\
        fuelLoadRes = { 1 }\n";

    #[test]
    fn pages_constants_and_pc_variables_are_read() {
        let layout = read(INI, None);

        assert_eq!(layout.pages.len(), 2);
        assert_eq!(layout.pages[1].size, 64);
        assert_eq!(layout.pages[0].burn_command, "b%2i");
        assert_eq!(layout.total_size(), 192);
        assert!(!layout.little_endian);

        let ve = layout.constants.iter().find(|c| c.name == "veTable").unwrap();
        assert_eq!((ve.page, ve.columns, ve.rows, ve.size()), (0, 2, 2, 4));
        assert!(ve.is_array() && ve.has_range());

        let mode = layout.constants.iter().find(|c| c.name == "mode").unwrap();
        assert_eq!(mode.options, ["Off", "On", "INVALID"]);
        assert!(mode.is_valid_option(1) && !mode.is_valid_option(2));
        assert_eq!(mode.option_name(7.0), "7");

        let text = layout.constants.iter().find(|c| c.name == "name").unwrap();
        assert!(text.is_text);
        assert_eq!((text.offset, text.columns), (20, 8));

        let other = layout.constants.iter().find(|c| c.name == "other").unwrap();
        assert_eq!(other.page, 1);
        assert_eq!(other.scale, 1.0);
        assert_eq!(other.scale_expression, "{0.01 * (range + 1)}");

        assert_eq!(layout.pc_variables.len(), 1);
        assert_eq!(layout.pc_variables[0].page, -1);
        assert!(!layout.pc_variables[0].on_controller());
        assert_eq!(layout.derived.get("FUELLOADRES").map(String::as_str), Some("1"));
    }
}
