//! .NET number formatting and parsing, reproduced exactly.
//!
//! Wave 0, `w0-model`. Every number the C# turns into text with `ToString(format)`
//! or an interpolation hole (`{x:F1}`) goes through here, so the Rust prints the
//! same characters. `format!("{:.1}")` is not a substitute: .NET's custom formats
//! (`"0.#"`, `"0.00"`) first round to 15 significant digits and then round *half
//! up* on that decimal string, while its standard formats (`"F1"`, `"N2"`, `"G5"`)
//! round the exact binary value *half to even*. They disagree on values such as
//! 2.675 (`"0.00"` gives 2.68, `"F2"` gives 2.67) and 2.5 (`"0"` gives 3, `"F0"` 2).
//!
//! # Two cultures
//!
//! The C# formats with the current culture wherever it passes no provider, and
//! the machines it was proven on are en-US. On .NET 5+ (ICU) en-US is not
//! invariant: `F`, `N` and `P` default to **3** decimals, not 2; `P` prints `50%`,
//! not `50 %`; and the infinities are `∞` and `-∞`. So every entry point comes in
//! two forms: [`f64`] (en-US, for C# that passed no culture or `CurrentCulture`)
//! and [`f64_inv`] (for C# that passed `CultureInfo.InvariantCulture`). The same
//! for [`f32`], [`int`], [`long`] and the parsers. [`Culture`] and the `try_`
//! forms take the culture explicitly.
//!
//! # How
//!
//! The structure follows .NET's `Number.Formatting.cs` (.NET Core 3.0 and later):
//! a value becomes a decimal digit buffer with a scale, and one of the standard
//! formatters (`F`, `N`, `P`, `E`, `G`, `R`, `D`, `X`, `B`) or the custom-format
//! interpreter (`0`, `#`, `.`, `,`, `%`, `‰`, `E+0`, quotes, `\`, `;` sections)
//! lays it out. The digits come from Rust's exact formatting, which is correctly
//! rounded half to even as .NET's Dragon4/Grisu are, and from its shortest
//! round-trip output, which picks the same digits as .NET's.
//!
//! The plain forms panic where .NET throws `FormatException` (an `X` on a double,
//! an unknown letter), because a format string is code, not input; the `try_`
//! forms return the error. Not supported: the currency format `C`.

use crate::{Error, Result};

/// The culture a number is formatted or parsed in.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Culture {
    /// `CultureInfo.InvariantCulture`.
    Invariant,
    /// `CultureInfo.GetCultureInfo("en-US")` on .NET 5+ (ICU): the C#'s current
    /// culture on every machine it has run on.
    EnUs,
}

/// The parts of `NumberFormatInfo` the formatters read.
struct Info {
    /// `NumberDecimalDigits`: the precision of a bare `F` or `N`.
    number_decimals: i32,
    /// `PercentDecimalDigits`: the precision of a bare `P`.
    percent_decimals: i32,
    /// Between the number and `%` in a `P` format: patterns 0 (`n %`) or 1 (`n%`).
    percent_gap: &'static str,
    positive_infinity: &'static str,
    negative_infinity: &'static str,
}

impl Culture {
    fn info(self) -> Info {
        match self {
            Culture::Invariant => Info {
                number_decimals: 2,
                percent_decimals: 2,
                percent_gap: " ",
                positive_infinity: "Infinity",
                negative_infinity: "-Infinity",
            },
            Culture::EnUs => Info {
                number_decimals: 3,
                percent_decimals: 3,
                percent_gap: "",
                positive_infinity: "∞",
                negative_infinity: "-∞",
            },
        }
    }
}

/// `value.ToString(format)` for a C# `double`, in en-US (the C#'s current culture).
///
/// `""` is the default `ToString()`, the shortest text that reads back as the
/// same value (`"R"` is the same thing).
///
/// # Panics
/// On a format .NET refuses for a double (`X`, `D`, an unknown letter): a bug in
/// the calling code, as the `FormatException` is in the C#.
pub fn f64(value: f64, format: &str) -> String {
    try_f64(value, format, Culture::EnUs).unwrap_or_else(|e| panic!("{e}"))
}

/// `value.ToString(format, CultureInfo.InvariantCulture)` for a C# `double`.
///
/// # Panics
/// As [`f64`].
pub fn f64_inv(value: f64, format: &str) -> String {
    try_f64(value, format, Culture::Invariant).unwrap_or_else(|e| panic!("{e}"))
}

/// A C# `double` formatted in `culture`, or the error .NET throws.
pub fn try_f64(value: f64, format: &str, culture: Culture) -> Result<String> {
    format_float(value, Width::Double, format, &culture.info())
}

/// `value.ToString(format)` for a C# `float`, in en-US.
///
/// Differs from widening to `f64` only where .NET does: the shortest text is the
/// float's own (`0.1f` prints `0.1`, not `0.10000000149011612`), and custom
/// formats start from 7 significant digits instead of 15.
///
/// # Panics
/// As [`f64`].
pub fn f32(value: f32, format: &str) -> String {
    try_f32(value, format, Culture::EnUs).unwrap_or_else(|e| panic!("{e}"))
}

/// `value.ToString(format, CultureInfo.InvariantCulture)` for a C# `float`.
///
/// # Panics
/// As [`f64`].
pub fn f32_inv(value: f32, format: &str) -> String {
    try_f32(value, format, Culture::Invariant).unwrap_or_else(|e| panic!("{e}"))
}

/// A C# `float` formatted in `culture`, or the error .NET throws.
pub fn try_f32(value: f32, format: &str, culture: Culture) -> Result<String> {
    format_float(f64::from(value), Width::Single, format, &culture.info())
}

/// `value.ToString(format)` for a C# `int`, in en-US.
///
/// Also right for `byte`, `ushort`, `short` and `sbyte` widened to `i32`, except
/// `X` on a negative `short`/`sbyte`. `X` on a negative value prints the 32-bit
/// two's complement, as .NET does for an `int`; a C# `long` uses [`long`].
///
/// # Panics
/// On a format .NET refuses for an integer.
pub fn int(value: i32, format: &str) -> String {
    try_int(value, format, Culture::EnUs).unwrap_or_else(|e| panic!("{e}"))
}

/// `value.ToString(format, CultureInfo.InvariantCulture)` for a C# `int`.
///
/// # Panics
/// As [`int`].
pub fn int_inv(value: i32, format: &str) -> String {
    try_int(value, format, Culture::Invariant).unwrap_or_else(|e| panic!("{e}"))
}

/// A C# `int` formatted in `culture`, or the error .NET throws.
pub fn try_int(value: i32, format: &str, culture: Culture) -> Result<String> {
    format_integer(i64::from(value), 32, format, &culture.info())
}

/// `value.ToString(format)` for a C# `long` (and `uint`, widened), in en-US. `X` on
/// a negative value prints the 64-bit two's complement.
///
/// # Panics
/// On a format .NET refuses for an integer.
pub fn long(value: i64, format: &str) -> String {
    try_long(value, format, Culture::EnUs).unwrap_or_else(|e| panic!("{e}"))
}

/// `value.ToString(format, CultureInfo.InvariantCulture)` for a C# `long`.
///
/// # Panics
/// As [`long`].
pub fn long_inv(value: i64, format: &str) -> String {
    try_long(value, format, Culture::Invariant).unwrap_or_else(|e| panic!("{e}"))
}

/// A C# `long` formatted in `culture`, or the error .NET throws.
pub fn try_long(value: i64, format: &str, culture: Culture) -> Result<String> {
    format_integer(value, 64, format, &culture.info())
}

// ----- the digit buffer ---------------------------------------------------------

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Integer,
    Float,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Width {
    Double,
    Single,
}

/// .NET's `NumberBuffer`: the value is `0.d1d2d3… × 10^scale`. An empty digit
/// list is zero.
struct Number {
    negative: bool,
    digits: Vec<u8>,
    scale: i32,
    kind: Kind,
}

impl Number {
    /// The digit at `i`, or 0 past the end (the C#'s NUL terminator).
    fn dig(&self, i: usize) -> u8 {
        self.digits.get(i).copied().unwrap_or(0)
    }
}

fn bad_format() -> Error {
    Error::Format("Format specifier was invalid.".to_string())
}

/// .NET's `ParseFormatSpecifier`: a letter and an optional precision is a standard
/// format; anything else is custom, returned as `'\0'`. Empty is `'G'`.
fn parse_specifier(f: &[char]) -> Result<(char, i32)> {
    let c = f.first().copied().unwrap_or('\0');
    if c.is_ascii_alphabetic() {
        if f.len() == 1 {
            return Ok((c, -1));
        }
        let mut n: i32 = 0;
        let mut i = 1;
        while i < f.len() && f[i].is_ascii_digit() {
            if n >= 100_000_000 {
                return Err(bad_format());
            }
            n = n * 10 + (f[i] as i32 - '0' as i32);
            i += 1;
        }
        if i >= f.len() || f[i] == '\0' {
            return Ok((c, n));
        }
    }
    if f.is_empty() || c == '\0' {
        Ok(('G', -1))
    } else {
        Ok(('\0', -1))
    }
}

// ----- floating point ----------------------------------------------------------

fn format_float(value: f64, width: Width, format: &str, info: &Info) -> Result<String> {
    // Checked before the format is even read, so an `X` on NaN prints "NaN".
    if value.is_nan() {
        return Ok("NaN".to_string());
    }
    if value.is_infinite() {
        return Ok(if value < 0.0 {
            info.negative_infinity
        } else {
            info.positive_infinity
        }
        .to_string());
    }

    let f: Vec<char> = format.chars().collect();
    let (fmt, mut precision) = parse_specifier(&f)?;
    if fmt == '\0' {
        precision = match width {
            Width::Double => 15,
            Width::Single => 7,
        };
    }

    // GetFloatingPointMaxDigitsAndPrecision
    let mut max_digits = precision;
    let significant = match fmt {
        '\0' => true,
        'E' | 'e' => {
            if precision == -1 {
                precision = 6;
            }
            precision += 1;
            true
        }
        'F' | 'f' | 'N' | 'n' => {
            if precision == -1 {
                precision = info.number_decimals;
            }
            false
        }
        'G' | 'g' => {
            if precision == 0 {
                precision = -1;
            }
            true
        }
        'P' | 'p' => {
            if precision == -1 {
                precision = info.percent_decimals;
            }
            precision += 2;
            false
        }
        'R' | 'r' => {
            precision = -1;
            true
        }
        'C' | 'c' => {
            return Err(Error::Format(
                "the currency format C is not supported by olv_core::fmt".to_string(),
            ));
        }
        _ => return Err(bad_format()),
    };

    let mut number = Number {
        negative: value.is_sign_negative(),
        digits: Vec::new(),
        scale: 0,
        kind: Kind::Float,
    };
    if value != 0.0 {
        let (digits, scale) = if !significant {
            fixed_digits(value.abs(), precision as usize)
        } else if precision == -1 {
            shortest_digits(value.abs(), width)
        } else {
            significant_digits(value.abs(), precision as usize)
        };
        number.digits = digits;
        number.scale = scale;
    }

    let mut out = String::new();
    if fmt != '\0' {
        if precision == -1 {
            let round_trip = match width {
                Width::Double => 17,
                Width::Single => 9,
            };
            max_digits = (number.digits.len() as i32).max(round_trip);
        }
        number_to_string(&mut out, &mut number, fmt, max_digits, info)?;
    } else {
        number_to_string_format(&mut out, &mut number, &f);
    }
    Ok(out)
}

/// Splits Rust's `d.ddde±x` scientific text into digits (trailing zeros dropped)
/// and a .NET scale.
fn from_scientific(text: &str) -> (Vec<u8>, i32) {
    let (mantissa, exponent) = text.split_once('e').unwrap_or((text, "0"));
    let mut digits: Vec<u8> = mantissa.bytes().filter(u8::is_ascii_digit).collect();
    while digits.last() == Some(&b'0') {
        digits.pop();
    }
    if digits.is_empty() {
        return (digits, 0);
    }
    let exponent: i32 = exponent.parse().unwrap_or(0);
    (digits, exponent + 1)
}

/// Shortest digits that read back as the same value: .NET's precision -1.
///
/// Where two shortest candidates are exactly as near as each other (the value's
/// exact decimal expansion is one digit longer and ends in 5, as 814.19830322265625
/// does), .NET's Dragon4 takes the even one (…62) and Rust's formatter the upper
/// (…63), so the tie is settled here.
fn shortest_digits(value: f64, width: Width) -> (Vec<u8>, i32) {
    let shortest = match width {
        Width::Double => from_scientific(&format!("{value:e}")),
        Width::Single => from_scientific(&format!("{:e}", value as f32)),
    };
    let n = shortest.0.len();

    // Cheap test first: a tie needs the next digit to round to a 5.
    let (next, _) = significant_digits(value, n + 1);
    if next.len() != n + 1 || next[n] != b'5' {
        return shortest;
    }
    // The value as stored, every digit of it (a double has at most 767).
    let (exact, exact_scale) = from_scientific(&format!("{value:.1100e}"));
    if exact.len() != n + 1 || exact[n] != b'5' {
        return shortest;
    }

    let mut even = exact[..n].to_vec();
    let mut scale = exact_scale;
    if (exact[n - 1] - b'0') % 2 == 1 {
        // The upper candidate: round the n digits up.
        let mut i = n;
        while i > 0 && even[i - 1] == b'9' {
            i -= 1;
        }
        even.truncate(i);
        if i > 0 {
            even[i - 1] += 1;
        } else {
            even = vec![b'1'];
            scale += 1;
        }
    }
    while even.last() == Some(&b'0') {
        even.pop();
    }

    // Both candidates are equally near, so both read back; check all the same,
    // since an asymmetric interval at a power of two could make one fall outside.
    let text = format!("0.{}e{scale}", even.iter().map(|&d| d as char).collect::<String>());
    let reads_back = match width {
        Width::Double => text.parse::<f64>().ok() == Some(value),
        Width::Single => text.parse::<f32>().ok() == Some(value as f32),
    };
    if reads_back { (even, scale) } else { shortest }
}

/// `count` significant digits, correctly rounded half to even.
fn significant_digits(value: f64, count: usize) -> (Vec<u8>, i32) {
    from_scientific(&format!("{:.*e}", count.saturating_sub(1), value))
}

/// Digits up to `places` after the decimal point, correctly rounded half to even.
fn fixed_digits(value: f64, places: usize) -> (Vec<u8>, i32) {
    let text = format!("{value:.places$}");
    let integer_len = text.find('.').unwrap_or(text.len());
    let all: Vec<u8> = text.bytes().filter(u8::is_ascii_digit).collect();
    let leading = all.iter().take_while(|&&d| d == b'0').count();
    let mut digits = all[leading..].to_vec();
    while digits.last() == Some(&b'0') {
        digits.pop();
    }
    if digits.is_empty() {
        return (digits, 0);
    }
    (digits, integer_len as i32 - leading as i32)
}

// ----- integers ----------------------------------------------------------------

fn format_integer(value: i64, bits: u32, format: &str, info: &Info) -> Result<String> {
    if format.is_empty() {
        return Ok(value.to_string());
    }
    let f: Vec<char> = format.chars().collect();
    let (fmt, digits) = parse_specifier(&f)?;
    let upper = fmt.to_ascii_uppercase();

    let decimal = if upper == 'G' { digits < 1 } else { upper == 'D' };
    if decimal {
        let magnitude = value.unsigned_abs().to_string();
        let width = digits.max(0) as usize;
        let sign = if value < 0 { "-" } else { "" };
        return Ok(format!("{sign}{magnitude:0>width$}"));
    }
    if upper == 'X' || upper == 'B' {
        let raw = if bits == 32 {
            u64::from(value as i32 as u32)
        } else {
            value as u64
        };
        let width = digits.max(1) as usize;
        return Ok(match fmt {
            'X' => format!("{raw:0width$X}"),
            'x' => format!("{raw:0width$x}"),
            _ => format!("{raw:0width$b}"),
        });
    }

    // Integers keep their trailing zeros, as .NET's Int32ToNumber does: "R" and a
    // "G" with no precision size themselves on the digit count.
    let digits_text = if value == 0 {
        Vec::new()
    } else {
        value.unsigned_abs().to_string().into_bytes()
    };
    let mut number = Number {
        negative: value < 0,
        scale: digits_text.len() as i32,
        digits: digits_text,
        kind: Kind::Integer,
    };

    let mut out = String::new();
    if fmt != '\0' {
        number_to_string(&mut out, &mut number, fmt, digits, info)?;
    } else {
        number_to_string_format(&mut out, &mut number, &f);
    }
    Ok(out)
}

// ----- the standard formats ----------------------------------------------------

/// .NET's `NumberToString`.
fn number_to_string(out: &mut String, number: &mut Number, fmt: char, mut n_max: i32, info: &Info) -> Result<()> {
    let correctly_rounded = number.kind == Kind::Float;
    match fmt {
        'F' | 'f' => {
            if n_max < 0 {
                n_max = info.number_decimals;
            }
            round_number(number, number.scale + n_max, correctly_rounded);
            if number.negative {
                out.push('-');
            }
            format_fixed(out, number, n_max, false);
        }
        'N' | 'n' => {
            if n_max < 0 {
                n_max = info.number_decimals;
            }
            round_number(number, number.scale + n_max, correctly_rounded);
            // NumberNegativePattern 1 in both cultures: "-n".
            if number.negative {
                out.push('-');
            }
            format_fixed(out, number, n_max, true);
        }
        'E' | 'e' => {
            if n_max < 0 {
                n_max = 6;
            }
            n_max += 1;
            round_number(number, n_max, correctly_rounded);
            if number.negative {
                out.push('-');
            }
            format_scientific(out, number, n_max, fmt);
        }
        'G' | 'g' | 'R' | 'r' => {
            if n_max < 1 {
                n_max = number.digits.len() as i32;
            }
            round_number(number, n_max, correctly_rounded);
            if number.negative {
                out.push('-');
            }
            let exp_char = if fmt.is_ascii_uppercase() { 'E' } else { 'e' };
            format_general(out, number, n_max, exp_char);
        }
        'P' | 'p' => {
            if n_max < 0 {
                n_max = info.percent_decimals;
            }
            number.scale += 2;
            round_number(number, number.scale + n_max, correctly_rounded);
            // Invariant patterns 0 ("n %", "-n %"); en-US patterns 1 ("n%", "-n%").
            if number.negative {
                out.push('-');
            }
            format_fixed(out, number, n_max, true);
            out.push_str(info.percent_gap);
            out.push('%');
        }
        _ => return Err(bad_format()),
    }
    Ok(())
}

/// .NET's `RoundNumber`: keep `pos` digits, rounding half up on the decimal
/// digits unless they are already correctly rounded, and drop trailing zeros.
fn round_number(number: &mut Number, pos: i32, correctly_rounded: bool) {
    let mut i = 0usize;
    while (i as i32) < pos && i < number.digits.len() {
        i += 1;
    }
    let round_up = i as i32 == pos && {
        let digit = number.dig(i);
        digit != 0 && !correctly_rounded && digit >= b'5'
    };
    if round_up {
        while i > 0 && number.digits[i - 1] == b'9' {
            i -= 1;
        }
        if i > 0 {
            number.digits[i - 1] += 1;
        } else {
            number.scale += 1;
            number.digits[0] = b'1';
            i = 1;
        }
    } else {
        while i > 0 && number.digits[i - 1] == b'0' {
            i -= 1;
        }
    }
    if i == 0 {
        // A float keeps its sign through rounding to zero ("-0.00"); an integer
        // has no negative zero.
        if number.kind != Kind::Float {
            number.negative = false;
        }
        number.scale = 0;
    }
    number.digits.truncate(i);
}

/// The next digit, or `'0'` once they run out (without moving past the end).
fn next_digit(number: &Number, cur: &mut usize) -> char {
    let d = number.dig(*cur);
    if d != 0 {
        *cur += 1;
        d as char
    } else {
        '0'
    }
}

fn format_fixed(out: &mut String, number: &Number, mut n_max: i32, grouped: bool) {
    let mut dig_pos = number.scale;
    let mut cur = 0usize;

    if dig_pos > 0 {
        let integer: Vec<char> = (0..dig_pos).map(|_| next_digit(number, &mut cur)).collect();
        if grouped {
            // NumberGroupSizes { 3 } in both cultures.
            for (i, c) in integer.iter().enumerate() {
                if i > 0 && (integer.len() - i).is_multiple_of(3) {
                    out.push(',');
                }
                out.push(*c);
            }
        } else {
            out.extend(integer);
        }
        dig_pos = 0;
    } else {
        out.push('0');
    }

    if n_max > 0 {
        out.push('.');
        if dig_pos < 0 {
            let zeroes = (-dig_pos).min(n_max);
            for _ in 0..zeroes {
                out.push('0');
            }
            n_max -= zeroes;
        }
        while n_max > 0 {
            out.push(next_digit(number, &mut cur));
            n_max -= 1;
        }
    }
}

fn format_scientific(out: &mut String, number: &Number, mut n_max: i32, exp_char: char) {
    let mut cur = 0usize;
    out.push(next_digit(number, &mut cur));
    // "E0" has no decimal point.
    if n_max != 1 {
        out.push('.');
    }
    loop {
        n_max -= 1;
        if n_max <= 0 {
            break;
        }
        out.push(next_digit(number, &mut cur));
    }
    let e = if number.digits.is_empty() { 0 } else { number.scale - 1 };
    format_exponent(out, e, exp_char, 3, true);
}

fn format_general(out: &mut String, number: &Number, n_max: i32, exp_char: char) {
    let mut dig_pos = number.scale;
    let mut scientific = false;
    if dig_pos > n_max || dig_pos < -3 {
        dig_pos = 1;
        scientific = true;
    }

    let mut cur = 0usize;
    if dig_pos > 0 {
        loop {
            out.push(next_digit(number, &mut cur));
            dig_pos -= 1;
            if dig_pos <= 0 {
                break;
            }
        }
    } else {
        out.push('0');
    }

    if cur < number.digits.len() || dig_pos < 0 {
        out.push('.');
        while dig_pos < 0 {
            out.push('0');
            dig_pos += 1;
        }
        while cur < number.digits.len() {
            out.push(number.digits[cur] as char);
            cur += 1;
        }
    }

    if scientific {
        format_exponent(out, number.scale - 1, exp_char, 2, true);
    }
}

fn format_exponent(out: &mut String, value: i32, exp_char: char, min_digits: usize, positive_sign: bool) {
    out.push(exp_char);
    if value < 0 {
        out.push('-');
    } else if positive_sign {
        out.push('+');
    }
    let magnitude = value.unsigned_abs();
    out.push_str(&format!("{magnitude:0min_digits$}"));
}

// ----- custom formats ----------------------------------------------------------

/// .NET's `FindSection`: where the `section`th `;`-separated part of a custom
/// format starts, or 0 when it has none (a missing or empty section means the
/// first is used).
fn find_section(f: &[char], mut section: i32) -> usize {
    if section == 0 {
        return 0;
    }
    let mut src = 0usize;
    loop {
        if src >= f.len() {
            return 0;
        }
        let ch = f[src];
        src += 1;
        match ch {
            '\'' | '"' => skip_quoted(f, &mut src, ch),
            '\\' => {
                if src < f.len() && f[src] != '\0' {
                    src += 1;
                }
            }
            ';' => {
                section -= 1;
                if section != 0 {
                    continue;
                }
                if src < f.len() && f[src] != '\0' && f[src] != ';' {
                    return src;
                }
                return 0;
            }
            '\0' => return 0,
            _ => {}
        }
    }
}

/// Moves past a quoted literal, closing quote included.
fn skip_quoted(f: &[char], src: &mut usize, quote: char) {
    while *src < f.len() && f[*src] != '\0' {
        let c = f[*src];
        *src += 1;
        if c == quote {
            break;
        }
    }
}

/// Inserts a group separator after a digit when one is due there.
fn group_separator(out: &mut String, separators: &[i32], ctr: &mut i32, dig_pos: i32) {
    if dig_pos > 1 && *ctr >= 0 && dig_pos == separators[*ctr as usize] + 1 {
        out.push(',');
        *ctr -= 1;
    }
}

/// .NET's `NumberToStringFormat`: the custom-format interpreter. The symbols it
/// writes (`-`, `.`, `,`, `%`, `‰`) are the same in both cultures.
fn number_to_string_format(out: &mut String, number: &mut Number, f: &[char]) {
    let at = |i: usize| -> char { f.get(i).copied().unwrap_or('\0') };

    let mut section = find_section(
        f,
        if number.digits.is_empty() {
            2
        } else if number.negative {
            1
        } else {
            0
        },
    );

    let mut digit_count;
    let mut decimal_pos;
    let mut first_digit;
    let mut last_digit;
    let mut scientific;
    let mut thousand_seps;
    let mut thousand_count = 0i32;

    loop {
        digit_count = 0i32;
        decimal_pos = -1i32;
        first_digit = 0x7FFF_FFFFi32;
        last_digit = 0i32;
        scientific = false;
        thousand_seps = false;
        let mut thousand_pos = -1i32;
        let mut scale_adjust = 0i32;
        let mut src = section;

        while src < f.len() && f[src] != '\0' && f[src] != ';' {
            let ch = f[src];
            src += 1;
            match ch {
                '#' => digit_count += 1,
                '0' => {
                    if first_digit == 0x7FFF_FFFF {
                        first_digit = digit_count;
                    }
                    digit_count += 1;
                    last_digit = digit_count;
                }
                '.' => {
                    if decimal_pos < 0 {
                        decimal_pos = digit_count;
                    }
                }
                ',' => {
                    if digit_count > 0 && decimal_pos < 0 {
                        if thousand_pos >= 0 {
                            if thousand_pos == digit_count {
                                thousand_count += 1;
                                continue;
                            }
                            thousand_seps = true;
                        }
                        thousand_pos = digit_count;
                        thousand_count = 1;
                    }
                }
                '%' => scale_adjust += 2,
                '\u{2030}' => scale_adjust += 3,
                '\'' | '"' => skip_quoted(f, &mut src, ch),
                '\\' => {
                    if src < f.len() && f[src] != '\0' {
                        src += 1;
                    }
                }
                'E' | 'e'
                    if at(src) == '0' || (src + 1 < f.len() && matches!(f[src], '+' | '-') && f[src + 1] == '0') =>
                {
                    loop {
                        src += 1;
                        if !(src < f.len() && f[src] == '0') {
                            break;
                        }
                    }
                    scientific = true;
                }
                _ => {}
            }
        }

        if decimal_pos < 0 {
            decimal_pos = digit_count;
        }

        if thousand_pos >= 0 {
            if thousand_pos == decimal_pos {
                scale_adjust -= thousand_count * 3;
            } else {
                thousand_seps = true;
            }
        }

        if !number.digits.is_empty() {
            number.scale += scale_adjust;
            let pos = if scientific {
                digit_count
            } else {
                number.scale + digit_count - decimal_pos
            };
            round_number(number, pos, false);
            if number.digits.is_empty() {
                let zero_section = find_section(f, 2);
                if zero_section != section {
                    section = zero_section;
                    continue;
                }
            }
        } else {
            if number.kind != Kind::Float {
                number.negative = false;
            }
            number.scale = 0;
        }
        break;
    }

    first_digit = if first_digit < decimal_pos {
        decimal_pos - first_digit
    } else {
        0
    };
    last_digit = if last_digit > decimal_pos {
        decimal_pos - last_digit
    } else {
        0
    };
    let (mut dig_pos, mut adjust) = if scientific {
        (decimal_pos, 0)
    } else {
        (number.scale.max(decimal_pos), number.scale - decimal_pos)
    };
    let mut src = section;

    // Where group separators go, counted in digits left of the decimal point, for
    // NumberGroupSizes { 3 }: precomputed because the loop below runs forwards.
    let mut separators: Vec<i32> = Vec::new();
    if thousand_seps {
        let total_digits = dig_pos + if adjust < 0 { adjust } else { 0 };
        let num_digits = first_digit.max(total_digits);
        let mut group_total = 3;
        while num_digits > group_total {
            separators.push(group_total);
            group_total += 3;
        }
    }
    let mut ctr = separators.len() as i32 - 1;

    let start_len = out.len();
    if number.negative && section == 0 && number.scale != 0 {
        out.push('-');
    }

    let mut decimal_written = false;
    let mut cur = 0usize;

    while src < f.len() && f[src] != '\0' && f[src] != ';' {
        let ch = f[src];
        src += 1;

        if adjust > 0 && matches!(ch, '#' | '0' | '.') {
            while adjust > 0 {
                out.push(next_digit(number, &mut cur));
                if thousand_seps {
                    group_separator(out, &separators, &mut ctr, dig_pos);
                }
                dig_pos -= 1;
                adjust -= 1;
            }
        }

        match ch {
            '#' | '0' => {
                let shown = if adjust < 0 {
                    adjust += 1;
                    (dig_pos <= first_digit).then_some('0')
                } else {
                    let d = number.dig(cur);
                    if d != 0 {
                        cur += 1;
                        Some(d as char)
                    } else {
                        (dig_pos > last_digit).then_some('0')
                    }
                };
                if let Some(c) = shown {
                    out.push(c);
                    if thousand_seps {
                        group_separator(out, &separators, &mut ctr, dig_pos);
                    }
                }
                dig_pos -= 1;
            }
            '.' => {
                // Repeated decimal points are not echoed. One is written when the
                // format has trailing zeros, or digits remain.
                if dig_pos == 0
                    && !decimal_written
                    && (last_digit < 0 || (decimal_pos < digit_count && number.dig(cur) != 0))
                {
                    out.push('.');
                    decimal_written = true;
                }
            }
            '\u{2030}' => out.push('\u{2030}'),
            '%' => out.push('%'),
            ',' => {}
            '\'' | '"' => {
                while src < f.len() && f[src] != '\0' && f[src] != ch {
                    out.push(f[src]);
                    src += 1;
                }
                if src < f.len() && f[src] != '\0' {
                    src += 1;
                }
            }
            '\\' => {
                if src < f.len() && f[src] != '\0' {
                    out.push(f[src]);
                    src += 1;
                }
            }
            'E' | 'e' => {
                if !scientific {
                    out.push(ch);
                    if src < f.len() {
                        if matches!(f[src], '+' | '-') {
                            out.push(f[src]);
                            src += 1;
                        }
                        while src < f.len() && f[src] == '0' {
                            out.push(f[src]);
                            src += 1;
                        }
                    }
                    continue;
                }
                let mut positive_sign = false;
                let mut i = 0usize;
                if at(src) == '0' {
                    // "E0" formats the same as "E-0".
                    i += 1;
                } else if src + 1 < f.len() && f[src] == '+' && f[src + 1] == '0' {
                    positive_sign = true;
                } else if !(src + 1 < f.len() && f[src] == '-' && f[src + 1] == '0') {
                    out.push(ch);
                    continue;
                }
                loop {
                    src += 1;
                    if !(src < f.len() && f[src] == '0') {
                        break;
                    }
                    i += 1;
                }
                let exp = if number.digits.is_empty() {
                    0
                } else {
                    number.scale - decimal_pos
                };
                format_exponent(out, exp, ch, i.min(10), positive_sign);
                scientific = false;
            }
            other => out.push(other),
        }
    }

    if number.negative && section == 0 && number.scale == 0 && out.len() > start_len {
        out.insert(start_len, '-');
    }
}

// ----- parsing -----------------------------------------------------------------

/// The characters .NET's number parser skips around a number: space and `\t`
/// through `\r`.
fn is_white(c: char) -> bool {
    c == ' ' || ('\t'..='\r').contains(&c)
}

/// Trims what .NET's parser allows around a number: white space either side, and
/// trailing NULs.
fn trim_number(s: &str) -> &str {
    s.trim_start_matches(is_white)
        .trim_end_matches('\0')
        .trim_end_matches(is_white)
}

/// `double.TryParse(s, NumberStyles.Float, out v)` in en-US, which is what a C#
/// `double.TryParse(s, out v)` (no culture) gets: it differs from invariant only
/// in the infinity symbol, `∞`.
pub fn parse_f64(s: &str) -> Option<f64> {
    try_parse_f64(s, Culture::EnUs)
}

/// `double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)`.
pub fn parse_f64_inv(s: &str) -> Option<f64> {
    try_parse_f64(s, Culture::Invariant)
}

/// `double.TryParse` with `NumberStyles.Float` in `culture`.
///
/// Accepts white space around, a leading sign, digits with an optional decimal
/// point (`5.` and `.5` both read), and an exponent; no thousands separators. The
/// culture's infinity symbols and `NaN`, signed or not, in any case. Correctly
/// rounded, and out-of-range values become infinities, as in .NET Core 3.0+.
pub fn try_parse_f64(s: &str, culture: Culture) -> Option<f64> {
    let t = trim_number(s);
    if let Some(v) = parse_decimal(t) {
        return Some(v);
    }
    let info = culture.info();
    let (sign, rest) = match t.as_bytes().first() {
        Some(b'-') => (-1.0, &t[1..]),
        Some(b'+') => (1.0, &t[1..]),
        _ => (1.0, t),
    };
    if t.eq_ignore_ascii_case(info.negative_infinity) {
        Some(f64::NEG_INFINITY)
    } else if rest.eq_ignore_ascii_case(info.positive_infinity) {
        Some(sign * f64::INFINITY)
    } else if rest.eq_ignore_ascii_case("NaN") {
        // .NET's double.NaN, sign bit and all.
        Some(f64::from_bits(0xFFF8_0000_0000_0000))
    } else {
        None
    }
}

/// `[sign] digits [. digits] [e [sign] digits]` with at least one mantissa digit.
fn parse_decimal(t: &str) -> Option<f64> {
    let b = t.as_bytes();
    let mut i = 0;
    if matches!(b.first(), Some(b'+' | b'-')) {
        i += 1;
    }
    let int_start = i;
    while i < b.len() && b[i].is_ascii_digit() {
        i += 1;
    }
    let mut mantissa_digits = i - int_start;
    if i < b.len() && b[i] == b'.' {
        i += 1;
        let frac_start = i;
        while i < b.len() && b[i].is_ascii_digit() {
            i += 1;
        }
        mantissa_digits += i - frac_start;
    }
    if mantissa_digits == 0 {
        return None;
    }
    if i < b.len() && (b[i] == b'e' || b[i] == b'E') {
        let mut j = i + 1;
        if matches!(b.get(j), Some(b'+' | b'-')) {
            j += 1;
        }
        let exp_start = j;
        while j < b.len() && b[j].is_ascii_digit() {
            j += 1;
        }
        // An 'e' with no digits after it is not consumed, so it is left over and
        // the parse fails, as .NET's does.
        if j > exp_start {
            i = j;
        }
    }
    if i != b.len() {
        return None;
    }
    t.parse::<f64>().ok()
}

/// `int.TryParse(s, out v)` (`NumberStyles.Integer`, either culture): white space
/// around, an optional sign, decimal digits; `None` on overflow.
pub fn parse_i32(s: &str) -> Option<i32> {
    parse_i64(s).and_then(|v| i32::try_from(v).ok())
}

/// `long.TryParse(s, out v)` (`NumberStyles.Integer`, either culture).
pub fn parse_i64(s: &str) -> Option<i64> {
    let t = trim_number(s);
    let digits = t.strip_prefix(['+', '-']).unwrap_or(t);
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    t.parse::<i64>().ok()
}

/// `byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)`:
/// white space around, hex digits, no sign and no `0x`.
pub fn parse_hex_u8(s: &str) -> Option<u8> {
    let t = trim_number(s);
    if t.is_empty() || !t.bytes().all(|b| b.is_ascii_hexdigit()) {
        return None;
    }
    let v = t.trim_start_matches('0');
    if v.len() > 2 {
        return None;
    }
    u8::from_str_radix(if v.is_empty() { "0" } else { v }, 16).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn custom_and_standard_formats_round_differently() {
        assert_eq!(f64_inv(2.675, "0.00"), "2.68");
        assert_eq!(f64_inv(2.675, "F2"), "2.67");
        assert_eq!(f64_inv(2.5, "0"), "3");
        assert_eq!(f64_inv(2.5, "F0"), "2");
    }

    #[test]
    fn en_us_differs_from_invariant_in_percent_and_default_decimals() {
        assert_eq!(f64(0.6666, "P0"), "67%");
        assert_eq!(f64_inv(0.6666, "P0"), "67 %");
        assert_eq!(f64(1.5, "F"), "1.500");
        assert_eq!(f64_inv(1.5, "F"), "1.50");
        assert_eq!(f64(f64::NEG_INFINITY, "F1"), "-∞");
    }

    #[test]
    fn floats_print_their_own_shortest_text() {
        assert_eq!(f32(0.1, ""), "0.1");
        assert_eq!(f64(f64::from(0.1f32), ""), "0.10000000149011612");
        assert_eq!(f32(12345.678, "0.###"), "12345.68");
    }

    #[test]
    fn grouping_sections_and_literals() {
        assert_eq!(f64(1234567.891, "#,##0"), "1,234,568");
        assert_eq!(f64(-5.0, "0.0;(0.0)"), "(5.0)");
        assert_eq!(f64(0.0, "0.0;(0.0);zero"), "zero");
        assert_eq!(f64(12.5, "0.0' kPa'"), "12.5 kPa");
        assert_eq!(f64(0.256, "0%"), "26%");
        assert_eq!(f64(1234.0, "0.00E+00"), "1.23E+03");
        assert_eq!(f64(-0.001, "0.#"), "-0");
    }

    #[test]
    fn x_on_a_double_is_refused_but_nan_is_not() {
        assert!(try_f64(1.0, "X2", Culture::EnUs).is_err());
        assert_eq!(f64(f64::NAN, "X2"), "NaN");
    }

    #[test]
    fn integers_in_hex_and_with_precision() {
        assert_eq!(int(-1, "X"), "FFFFFFFF");
        assert_eq!(long(-1, "X"), "FFFFFFFFFFFFFFFF");
        assert_eq!(int(255, "x4"), "00ff");
        assert_eq!(int(-42, "D5"), "-00042");
        assert_eq!(int(1000, "R"), "1000");
    }

    #[test]
    fn parsing_follows_number_styles_float() {
        assert_eq!(parse_f64_inv("  1.5e3 "), Some(1500.0));
        assert_eq!(parse_f64_inv(".5"), Some(0.5));
        assert_eq!(parse_f64_inv("5."), Some(5.0));
        assert_eq!(parse_f64_inv("-infinity"), Some(f64::NEG_INFINITY));
        assert_eq!(parse_f64("-Infinity"), None);
        assert_eq!(parse_f64("∞"), Some(f64::INFINITY));
        assert!(parse_f64_inv("nan").is_some_and(f64::is_nan));
        assert_eq!(parse_f64_inv("inf"), None);
        assert_eq!(parse_f64_inv("1e"), None);
        assert_eq!(parse_f64_inv("1,000"), None);
        assert_eq!(parse_f64_inv(""), None);
        assert_eq!(parse_i32(" -12 "), Some(-12));
        assert_eq!(parse_i32("2147483648"), None);
        assert_eq!(parse_hex_u8("0F"), Some(15));
        assert_eq!(parse_hex_u8("G1"), None);
    }
}
