//! Ported from `DialogCondition.cs`: deciding whether a setting applies to the
//! tune in hand.
//!
//! Almost every dialog in a firmware definition is conditional. "Window Sample
//! Type" is only meaningful with knock detection on and set to analogue; a second
//! VVT bank's settings mean nothing on an engine configured with one. The
//! conditions are written against the tune's own constants (`{ knk_option &&
//! (knk_option_an == 1) }`) so the same definition produces a different set of
//! screens for every tune it is used with.
//!
//! Not the same language as a calculated channel, and deliberately evaluated by
//! its own code. This one is C's: `&` and `|` are bitwise, `&&` and `||` are
//! logical, any non-zero value is true, and a comparison yields one or nought.
//! Feeding these to an expression parser built for arithmetic would quietly get
//! `status8 & 0x40` wrong, and the symptom would be a settings page missing the
//! field somebody went looking for.
//!
//! **What it does when it cannot decide is the important part.** A condition
//! naming a constant this firmware does not declare, or calling one of the
//! functions TunerStudio provides and this does not, cannot be judged either way.
//! The answer is then [`ConditionVerdict::Unknown`] and the caller shows the
//! field: an irrelevant setting on screen is untidy, while a hidden one is a
//! setting the user cannot reach and has no way of knowing exists.

/// What a condition came to.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ConditionVerdict {
    /// Show it.
    Shown,
    /// Hide it: the firmware says it does not apply to this tune.
    Hidden,
    /// The condition could not be worked out: it names something this does not
    /// have, or uses something this does not understand.
    Unknown,
}

/// Evaluates a condition against a tune's settings.
///
/// `condition` is the expression without its braces. `value` looks a constant up
/// by name, returning NaN for one this firmware does not have. A bit field reads
/// as nought or one; everything else as its number.
pub fn evaluate<F: Fn(&str) -> f64>(condition: &str, value: F) -> ConditionVerdict {
    if condition.trim().is_empty() {
        return ConditionVerdict::Shown;
    }

    // Malformed, or using something not understood. Either way this cannot say,
    // and saying so is better than guessing at it.
    match Parser::new(condition, &value).parse_all() {
        Ok(result) if result.is_nan() => ConditionVerdict::Unknown,
        Ok(result) if result != 0.0 => ConditionVerdict::Shown,
        Ok(_) => ConditionVerdict::Hidden,
        Err(Malformed) => ConditionVerdict::Unknown,
    }
}

/// The same expression as a number rather than as a verdict.
///
/// The two are not interchangeable. An expression in this language need not be a
/// test: `[OutputChannels]` declares arithmetic with it, and `engineLoad = {
/// fuelingLoad }` means the load, not whether there is any. Passing that through a
/// yes-or-no answer turns 63 kPa into 1, which then fails every comparison a
/// dialog condition makes against it and hides settings the tuner is meant to
/// reach.
///
/// Returns the value, or NaN where it cannot be worked out.
pub fn number<F: Fn(&str) -> f64>(expression: &str, value: F) -> f64 {
    if expression.trim().is_empty() {
        return f64::NAN;
    }

    Parser::new(expression, &value).parse_all().unwrap_or(f64::NAN)
}

/// Whether to put the thing on screen. Unknown counts as yes, for the reason given
/// above.
pub fn should_show<F: Fn(&str) -> f64>(condition: &str, value: F) -> bool {
    evaluate(condition, value) != ConditionVerdict::Hidden
}

/// The C#'s `FormatException`/`OverflowException`: text this cannot read.
#[derive(Debug)]
struct Malformed;

type Parsed = Result<f64, Malformed>;

/// A recursive-descent parser over C's precedence.
///
/// Written out rather than folded into the calculated-channel parser because the
/// two languages differ in ways that do not show up until they are wrong: bitwise
/// operators, C's truthiness, and no notion of a missing reading propagating. Here
/// a name that is not known makes the whole condition unknown rather than the
/// result NaN.
struct Parser<'a, F: Fn(&str) -> f64> {
    /// The text as UTF-16 units would see it: a character outside the basic plane
    /// is two units in the C#, neither of them a letter, a digit or a space, so it
    /// is held as one character that is none of those.
    text: Vec<char>,
    lookup: &'a F,
    at: usize,
    /// How deeply the parse has recursed, for [`MAXIMUM_DEPTH`].
    depth: usize,
}

/// How deeply a condition may nest (brackets, runs of prefix operators, chained `?:`) before it
/// is called malformed. The parser recurses once per level and a definition is a file from the
/// internet, so without a limit a long enough run of `(` or `!` overflows the stack, which
/// nothing can catch. (The C# had no limit.) Far past anything written on purpose.
const MAXIMUM_DEPTH: usize = 64;

impl<'a, F: Fn(&str) -> f64> Parser<'a, F> {
    fn new(text: &str, lookup: &'a F) -> Self {
        Self {
            text: text
                .chars()
                .map(|c| if (c as u32) > 0xFFFF { '\u{FFFD}' } else { c })
                .collect(),
            lookup,
            at: 0,
            depth: 0,
        }
    }

    /// One level deeper, or [`Malformed`] past the limit. Undone by the caller.
    fn enter(&mut self) -> Result<(), Malformed> {
        self.depth += 1;
        if self.depth > MAXIMUM_DEPTH {
            self.depth -= 1;
            return Err(Malformed);
        }
        Ok(())
    }

    fn parse_all(&mut self) -> Parsed {
        let value = self.ternary()?;
        self.skip_space();

        if self.at < self.text.len() {
            return Err(Malformed);
        }

        Ok(value)
    }

    // Lowest precedence first, each level deferring to the next.

    /// `a ? b : c`: the lowest precedence of all, and right-associative, so
    /// `a ? b : c ? d : e` reads as `a ? b : (c ? d : e)`.
    ///
    /// Needed because a firmware's scale is sometimes a choice rather than a
    /// number. A Speeduino states its load-axis resolution as `{ ((algorithm == 0)
    /// || (algorithm == 2)) ? 2.000 : 0.500 }`. Without this the expression fails
    /// to parse, the scale falls back to the declared 1, and every load axis is
    /// read at half, on the way in as well as out, so restoring a tune TunerStudio
    /// had written would have doubled the fuel, AFR, VVT and dwell axes on the
    /// controller.
    ///
    /// Both arms are parsed whether or not they are taken, for the same reason the
    /// short-circuits below consume their operands: text left unread makes the
    /// caller declare the whole expression malformed.
    fn ternary(&mut self) -> Parsed {
        let condition = self.or()?;

        self.skip_space();
        if self.at >= self.text.len() || self.text[self.at] != '?' {
            return Ok(condition);
        }

        self.at += 1;
        self.enter()?;
        let when_true = self.ternary();
        self.depth -= 1;
        let when_true = when_true?;

        self.skip_space();
        if self.at >= self.text.len() || self.text[self.at] != ':' {
            return Err(Malformed);
        }

        self.at += 1;
        self.enter()?;
        let when_false = self.ternary();
        self.depth -= 1;
        let when_false = when_false?;

        // Unknown in, unknown out: a condition nothing could answer must not
        // quietly settle on the false arm.
        if condition.is_nan() {
            return Ok(f64::NAN);
        }

        Ok(if condition != 0.0 { when_true } else { when_false })
    }

    /// `a || b || c`, true as soon as any operand is.
    ///
    /// The short-circuit decides the answer but must not stop the parse. An operand
    /// that settles it leaves the remaining ones still to be read, and returning
    /// there abandons them; the caller then finds text it cannot account for and
    /// calls the whole condition malformed. So every operand is consumed and only
    /// the verdict short-circuits.
    fn or(&mut self) -> Parsed {
        let mut result = self.and()?;

        while self.take("||") {
            let right = self.and()?;

            // Once true, true: a later unknown cannot take that back.
            if truthy(result) == Some(true) || truthy(right) == Some(true) {
                result = 1.0;
            } else if result.is_nan() || right.is_nan() {
                result = f64::NAN;
            } else {
                result = 0.0;
            }
        }

        Ok(result)
    }

    /// And the mirror: false as soon as any operand is.
    fn and(&mut self) -> Parsed {
        let mut result = self.bit_or()?;

        while self.take("&&") {
            let right = self.bit_or()?;

            if truthy(result) == Some(false) || truthy(right) == Some(false) {
                result = 0.0;
            } else if result.is_nan() || right.is_nan() {
                result = f64::NAN;
            } else {
                result = 1.0;
            }
        }

        Ok(result)
    }

    fn bit_or(&mut self) -> Parsed {
        let mut left = self.bit_xor()?;

        while !self.peek("||") && self.take("|") {
            let right = self.bit_xor()?;
            left = bitwise(left, right, |a, b| a | b);
        }

        Ok(left)
    }

    fn bit_xor(&mut self) -> Parsed {
        let mut left = self.bit_and()?;

        while self.take("^") {
            let right = self.bit_and()?;
            left = bitwise(left, right, |a, b| a ^ b);
        }

        Ok(left)
    }

    fn bit_and(&mut self) -> Parsed {
        let mut left = self.equality()?;

        while !self.peek("&&") && self.take("&") {
            let right = self.equality()?;
            left = bitwise(left, right, |a, b| a & b);
        }

        Ok(left)
    }

    fn equality(&mut self) -> Parsed {
        let mut left = self.relational()?;

        loop {
            if self.take("==") {
                let right = self.relational()?;
                left = compare(left, right, |a, b| a == b);
            } else if self.take("!=") {
                let right = self.relational()?;
                left = compare(left, right, |a, b| a != b);
            } else {
                return Ok(left);
            }
        }
    }

    fn relational(&mut self) -> Parsed {
        let mut left = self.additive()?;

        loop {
            // The two-character forms first, or ">=" is read as ">" and a stray
            // "=".
            if self.take(">=") {
                let right = self.additive()?;
                left = compare(left, right, |a, b| a >= b);
            } else if self.take("<=") {
                let right = self.additive()?;
                left = compare(left, right, |a, b| a <= b);
            } else if self.take(">") {
                let right = self.additive()?;
                left = compare(left, right, |a, b| a > b);
            } else if self.take("<") {
                let right = self.additive()?;
                left = compare(left, right, |a, b| a < b);
            } else {
                return Ok(left);
            }
        }
    }

    fn additive(&mut self) -> Parsed {
        let mut left = self.multiplicative()?;

        loop {
            if self.take("+") {
                let right = self.multiplicative()?;
                left = arithmetic(left, right, |a, b| a + b);
            } else if self.take("-") {
                let right = self.multiplicative()?;
                left = arithmetic(left, right, |a, b| a - b);
            } else {
                return Ok(left);
            }
        }
    }

    fn multiplicative(&mut self) -> Parsed {
        let mut left = self.unary()?;

        loop {
            if self.take("*") {
                let right = self.unary()?;
                left = arithmetic(left, right, |a, b| a * b);
            } else if self.take("/") {
                let right = self.unary()?;
                left = divide(left, right);
            } else if self.take("%") {
                let right = self.unary()?;
                left = modulo(left, right);
            } else {
                return Ok(left);
            }
        }
    }

    fn unary(&mut self) -> Parsed {
        self.skip_space();

        if self.take("!") {
            let value = self.nested_unary()?;
            return Ok(if value.is_nan() {
                f64::NAN
            } else if value == 0.0 {
                1.0
            } else {
                0.0
            });
        }

        if self.take("~") {
            let value = self.nested_unary()?;
            return Ok(if value.is_nan() {
                f64::NAN
            } else {
                !(value as i64) as f64
            });
        }

        if self.take("-") {
            let value = self.nested_unary()?;
            return Ok(if value.is_nan() { f64::NAN } else { -value });
        }

        if self.take("+") {
            return self.nested_unary();
        }

        self.primary()
    }

    /// The operand of a prefix operator, one level deeper.
    fn nested_unary(&mut self) -> Parsed {
        self.enter()?;
        let value = self.unary();
        self.depth -= 1;
        value
    }

    fn primary(&mut self) -> Parsed {
        self.skip_space();

        if self.at >= self.text.len() {
            return Err(Malformed);
        }

        if self.take("(") {
            // The whole grammar inside the brackets, ternary included. The C# started at
            // `or`, so "(a ? b : c)" was malformed and came out Unknown while the same
            // ternary without brackets worked.
            self.enter()?;
            let value = self.ternary();
            self.depth -= 1;
            let value = value?;
            self.skip_space();

            if !self.take(")") {
                return Err(Malformed);
            }

            return Ok(value);
        }

        let c = self.text[self.at];

        if c.is_ascii_digit() {
            return self.number();
        }

        if c.is_alphabetic() || c == '_' {
            return self.name();
        }

        Err(Malformed)
    }

    fn number(&mut self) -> Parsed {
        let start = self.at;

        // Bit masks are written in hex, and a great many conditions are a mask
        // against a status byte.
        if self.text[self.at] == '0' && self.at + 1 < self.text.len() && matches!(self.text[self.at + 1], 'x' | 'X') {
            self.at += 2;
            let digits = self.at;

            while self.at < self.text.len() && self.text[self.at].is_ascii_hexdigit() {
                self.at += 1;
            }

            if self.at == digits {
                return Err(Malformed);
            }

            // Too wide to hold is malformed rather than fatal: the third verdict
            // exists so that anything unparseable shows the field.
            let hex: String = self.text[digits..self.at].iter().collect();
            return u64::from_str_radix(&hex, 16).map(|v| v as f64).map_err(|_| Malformed);
        }

        while self.at < self.text.len() && (self.text[self.at].is_ascii_digit() || self.text[self.at] == '.') {
            self.at += 1;
        }

        let text: String = self.text[start..self.at].iter().collect();
        crate::fmt::parse_f64_inv(&text).ok_or(Malformed)
    }

    fn name(&mut self) -> Parsed {
        let start = self.at;

        // Dots and brackets are part of a name here: a condition may test
        // array.boardHasRTC, or one element of an array.
        while self.at < self.text.len()
            && (self.text[self.at].is_alphanumeric() || matches!(self.text[self.at], '_' | '.' | '[' | ']'))
        {
            self.at += 1;
        }

        let name: String = self.text[start..self.at].iter().collect();
        self.skip_space();

        // A call to something TunerStudio provides and this does not. The
        // arguments are skipped so the rest of the expression still parses, and the
        // result is unknown, which shows the field.
        if self.at < self.text.len() && self.text[self.at] == '(' {
            self.skip_balanced()?;
            return Ok(f64::NAN);
        }

        Ok((self.lookup)(&name))
    }

    /// Steps over a bracketed group, whatever is inside it.
    fn skip_balanced(&mut self) -> Result<(), Malformed> {
        let mut depth = 0_i32;

        loop {
            if self.text[self.at] == '(' {
                depth += 1;
            } else if self.text[self.at] == ')' {
                depth -= 1;
            }

            self.at += 1;

            if !(self.at < self.text.len() && depth > 0) {
                break;
            }
        }

        if depth > 0 {
            return Err(Malformed);
        }
        Ok(())
    }

    // ----- reading along the text -------------------------------------------

    fn skip_space(&mut self) {
        while self.at < self.text.len() && self.text[self.at].is_whitespace() {
            self.at += 1;
        }
    }

    fn peek(&mut self, token: &str) -> bool {
        self.skip_space();
        let mut rest = self.text[self.at..].iter();
        token.chars().all(|t| rest.next() == Some(&t))
    }

    fn take(&mut self, token: &str) -> bool {
        if !self.peek(token) {
            return false;
        }

        self.at += token.chars().count();
        true
    }
}

// ----- the operators themselves ---------------------------------------------

/// Nought is false, anything else true, and unknown neither.
fn truthy(value: f64) -> Option<bool> {
    if value.is_nan() { None } else { Some(value != 0.0) }
}

fn compare(left: f64, right: f64, test: fn(f64, f64) -> bool) -> f64 {
    if left.is_nan() || right.is_nan() {
        f64::NAN
    } else if test(left, right) {
        1.0
    } else {
        0.0
    }
}

fn arithmetic(left: f64, right: f64, apply: fn(f64, f64) -> f64) -> f64 {
    if left.is_nan() || right.is_nan() {
        f64::NAN
    } else {
        apply(left, right)
    }
}

fn divide(left: f64, right: f64) -> f64 {
    if left.is_nan() || right.is_nan() || right == 0.0 {
        f64::NAN
    } else {
        left / right
    }
}

fn modulo(left: f64, right: f64) -> f64 {
    if left.is_nan() || right.is_nan() || right == 0.0 {
        f64::NAN
    } else {
        left % right
    }
}

/// A bitwise operator, over whole numbers. Conditions mask status bytes with
/// values that are integers by nature, so the operands are narrowed rather than
/// refused.
fn bitwise(left: f64, right: f64, apply: fn(i64, i64) -> i64) -> f64 {
    if left.is_nan() || right.is_nan() {
        f64::NAN
    } else {
        apply(left as i64, right as i64) as f64
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A firmware with a few settings in it. Anything else is unknown.
    fn lookup(name: &str) -> f64 {
        match name.to_ascii_lowercase().as_str() {
            "knk_option" => 1.0,
            "knk_option_an" => 1.0,
            "off" => 0.0,
            "algorithm" => 6.0,
            "status8" => 64.0,
            "ncylinders" => 8.0,
            _ => f64::NAN,
        }
    }

    fn verdict(condition: &str) -> ConditionVerdict {
        evaluate(condition, lookup)
    }

    fn shown(condition: &str) -> bool {
        verdict(condition) == ConditionVerdict::Shown
    }

    fn hidden(condition: &str) -> bool {
        verdict(condition) == ConditionVerdict::Hidden
    }

    fn nan(_: &str) -> f64 {
        f64::NAN
    }

    // ----- the basics -------------------------------------------------------

    #[test]
    fn no_condition_means_always_shown() {
        assert_eq!(verdict(""), ConditionVerdict::Shown);
        assert_eq!(verdict("   "), ConditionVerdict::Shown);
    }

    #[test]
    fn comparisons_and_truthiness_follow_c() {
        for (condition, expected) in [
            ("knk_option", true), // non-zero is true, C-style
            ("off", false),
            ("!knk_option", false),
            ("!off", true),
            ("algorithm == 6", true),
            ("algorithm != 6", false),
            ("algorithm > 5", true),
            ("algorithm >= 6", true),
            ("algorithm < 6", false),
            ("algorithm <= 6", true),
        ] {
            assert_eq!(shown(condition), expected, "{condition}");
        }
    }

    #[test]
    fn logical_operators_combine_conditions() {
        for (condition, expected) in [
            ("knk_option && knk_option_an", true),
            ("knk_option && off", false),
            ("off || knk_option", true),
            ("off || off", false),
            ("knk_option && (knk_option_an == 1)", true),
            ("!(off || off)", true),
        ] {
            assert_eq!(shown(condition), expected, "{condition}");
        }
    }

    // ----- the bitwise half, which is where a general parser goes wrong -----

    /// status8 is 0x40. Masking it with 0x40 leaves 0x40, which is true; masking
    /// with 0x03 leaves nothing, which is false. A parser treating & as "and" would
    /// call both of them true, because both operands are non-zero.
    #[test]
    fn single_ampersand_is_bitwise_rather_than_logical() {
        assert!(shown("status8 & 0x40"));
        assert!(hidden("status8 & 0x03"));
    }

    /// Equality binds tighter than &, so this is (status8 & 0x40) == 0 only because
    /// of the brackets the firmware wrote.
    #[test]
    fn a_masked_comparison_reads_as_it_does_in_c() {
        assert!(hidden("(status8 & 0x40) == 0"));
        assert!(shown("(status8 & 0x03) == 0"));
    }

    #[test]
    fn hex_literals_are_read() {
        for (condition, expected) in [("0x40", true), ("0X40", true), ("0x00", false)] {
            assert_eq!(shown(condition), expected, "{condition}");
        }
    }

    #[test]
    fn the_other_bitwise_operators_work() {
        assert!(shown("(status8 | 0x01) == 0x41"));
        assert!(shown("(status8 ^ 0x40) == 0"));
        // 8 is 0b1000, so it meets a mask of 8 and misses one of 4.
        assert!(shown("(nCylinders & 0x08) == 8"));
        assert!(hidden("(nCylinders & 0x04) == 4"));
    }

    /// "off || knk_option" must not be read as a bitwise or of "off" and a stray
    /// "| knk_option", and likewise for &&.
    #[test]
    fn doubled_operators_are_not_mistaken_for_single_ones() {
        assert!(shown("off || knk_option"));
        assert!(hidden("knk_option && off"));
    }

    // ----- what it does when it cannot say ----------------------------------

    /// This firmware has no such setting. Neither answer is right, and the caller
    /// shows the field rather than hiding a setting nobody can then reach.
    #[test]
    fn a_condition_naming_something_unknown_is_not_decided() {
        assert_eq!(verdict("notAThing"), ConditionVerdict::Unknown);
        assert_eq!(verdict("notAThing == 2"), ConditionVerdict::Unknown);
        assert!(should_show("notAThing", lookup));
    }

    /// TunerStudio provides functions this does not. Skipping the call and
    /// reporting unknown keeps the rest of the expression parseable.
    #[test]
    fn a_call_to_something_unsupported_is_not_decided() {
        assert_eq!(
            verdict("arrayValue( array.boardHasRTC, pinLayout ) > 0"),
            ConditionVerdict::Unknown
        );
        assert_eq!(verdict("bitStringValue( portLabels, x )"), ConditionVerdict::Unknown);
    }

    #[test]
    fn nonsense_is_not_decided_rather_than_throwing() {
        assert_eq!(verdict("algorithm =="), ConditionVerdict::Unknown);
        assert_eq!(verdict("(((("), ConditionVerdict::Unknown);
        assert_eq!(verdict("@£$"), ConditionVerdict::Unknown);
        assert_eq!(verdict("algorithm 6"), ConditionVerdict::Unknown);
    }

    /// False and anything is false; true or anything is true. A condition half of
    /// which cannot be judged is still decidable when the other half decides it,
    /// which keeps a field from appearing merely because some unrelated firmware
    /// option was not recognised.
    #[test]
    fn knowing_one_half_is_enough_when_it_settles_the_answer() {
        assert_eq!(verdict("off && notAThing"), ConditionVerdict::Hidden);
        assert_eq!(verdict("knk_option || notAThing"), ConditionVerdict::Shown);

        // And when it does not settle it, the answer is unknown.
        assert_eq!(verdict("knk_option && notAThing"), ConditionVerdict::Unknown);
        assert_eq!(verdict("off || notAThing"), ConditionVerdict::Unknown);
    }

    /// Deciding the answer early must not stop the parse. Two operands hid this:
    /// the early exit happened once everything had been read anyway. Three or more
    /// is where it bites, and three or more is what real definitions are full of.
    #[test]
    fn short_circuiting_does_not_abandon_the_rest_of_the_expression() {
        assert_eq!(verdict("off && knk_option && knk_option"), ConditionVerdict::Hidden);
        assert_eq!(verdict("off && notAThing && knk_option"), ConditionVerdict::Hidden);
        assert_eq!(verdict("knk_option || off || off"), ConditionVerdict::Shown);
        assert_eq!(verdict("knk_option || notAThing || off"), ConditionVerdict::Shown);

        // Four, mixed, and bracketed, as they really come.
        assert_eq!(
            verdict("off && (algorithm == 6) && knk_option && knk_option_an"),
            ConditionVerdict::Hidden
        );
    }

    // ----- shapes taken from real definitions -------------------------------

    #[test]
    fn real_conditions_parse() {
        for condition in [
            "knk_option && (knk_option_an == 1)",
            "((algorithm == 6) || (algorithm == 6))",
            "!(off == 1 || ( !(algorithm == 2 || algorithm == 5 || algorithm == 6)))",
            "nCylinders > 4 && nCylinders <= 12",
        ] {
            assert_ne!(verdict(condition), ConditionVerdict::Unknown, "{condition}");
        }
    }

    #[test]
    fn a_name_may_carry_dots_and_brackets() {
        let lookup2 = |name: &str| match name {
            "psEnabled[0]" => 1.0,
            "array.thing" => 0.0,
            _ => f64::NAN,
        };

        assert_eq!(evaluate("psEnabled[0]", lookup2), ConditionVerdict::Shown);
        assert_eq!(evaluate("array.thing", lookup2), ConditionVerdict::Hidden);
    }

    #[test]
    fn arithmetic_is_available_even_though_it_is_rare() {
        assert!(shown("nCylinders / 2 == 4"));
        assert!(shown("nCylinders - 8 == 0"));
        assert!(shown("nCylinders * 2 == 16"));
        assert!(shown("nCylinders + 1 == 9"));
    }

    /// An infinity compares in ways that look like answers.
    #[test]
    fn dividing_by_nothing_is_not_decided_rather_than_infinite() {
        assert_eq!(verdict("nCylinders / off > 1"), ConditionVerdict::Unknown);
    }

    #[test]
    fn precedence_matches() {
        // && binds tighter than ||, so this is (0 && 0) || 1 and not 0 && (0 || 1).
        assert!(shown("off && off || knk_option"));

        // Comparison binds tighter than &&.
        assert!(shown("algorithm == 6 && knk_option == 1"));

        // Unary ! binds tightest.
        assert!(hidden("!knk_option && knk_option"));
    }

    // ----- a ? b : c ---------------------------------------------------------

    #[test]
    fn a_ternary_chooses_its_arm() {
        for (expression, expected) in [
            ("1 ? 2 : 3", 2.0),
            ("0 ? 2 : 3", 3.0),
            ("(2 > 1) ? 10 : 20", 10.0),
            ("(1 > 2) ? 10 : 20", 20.0),
        ] {
            assert_eq!(number(expression, nan), expected, "{expression}");
        }
    }

    /// The scale for every load axis on a Speeduino. Without a ternary the whole
    /// expression failed to parse, the scale fell back to the declared 1, and the
    /// axes were read at half.
    #[test]
    fn the_real_one_off_a_speeduino() {
        let expression = "((algorithm == 0) || (algorithm == 2)) ? 2.000 : 0.500";
        let with = |algorithm: f64| move |n: &str| if n == "algorithm" { algorithm } else { f64::NAN };

        assert_eq!(number(expression, with(0.0)), 2.0);
        assert_eq!(number(expression, with(2.0)), 2.0);
        assert_eq!(number(expression, with(1.0)), 0.5);
    }

    /// New defect, found while fixing the others and fixed the same way: nothing limited the
    /// nesting, so a long run of `(`, `!` or `?` overflowed the stack. Now it is malformed
    /// (Unknown), and sensible nesting is untouched.
    #[test]
    fn hostile_nesting_is_malformed_not_a_stack_overflow() {
        let run = 100_000;
        for text in [
            format!("{}1{}", "(".repeat(run), ")".repeat(run)),
            format!("{}1", "!".repeat(run)),
            format!("{}1", "-".repeat(run)),
            format!("{}1", "1 ? 1 : ".repeat(run)),
        ] {
            assert!(number(&text, nan).is_nan());
            assert_eq!(evaluate(&text, nan), ConditionVerdict::Unknown);
        }

        assert_eq!(number("((((1 + 2))))", nan), 3.0);
        assert_eq!(number("!!!!1", nan), 1.0);
    }

    /// Defect fixed in the Rust: a bracketed ternary evaluated its group with `or`, so it
    /// was malformed (and the verdict Unknown) while the bare one worked.
    #[test]
    fn a_parenthesised_ternary_is_evaluated_like_a_bare_one() {
        assert_eq!(number("(1 ? 2 : 3)", nan), 2.0);
        assert_eq!(number("(0 ? 2 : 3) + 10", nan), 13.0);
        assert_eq!(number("((1 > 2) ? 10 : 20)", nan), 20.0);
        assert_eq!(evaluate("(0 ? 1 : 0)", nan), ConditionVerdict::Hidden);
        assert_eq!(evaluate("(1 ? 1 : 0) && 1", nan), ConditionVerdict::Shown);
    }

    /// a ? b : c ? d : e is a ? b : (c ? d : e), as it is in C.
    #[test]
    fn it_is_right_associative() {
        assert_eq!(number("0 ? 1 : 1 ? 3 : 4", nan), 3.0);
        assert_eq!(number("0 ? 1 : 0 ? 3 : 4", nan), 4.0);
    }

    /// 1 + 1 ? ... reads as (1 + 1) ? ..., and the arms take whole expressions.
    #[test]
    fn it_binds_looser_than_everything_else() {
        assert_eq!(number("1 + 1 ? 3 + 4 : 9", nan), 7.0);
    }

    /// Rather than quietly settling on the false arm, which would pick a scale on
    /// no evidence.
    #[test]
    fn a_condition_nothing_can_answer_stays_unanswered() {
        assert!(number("nothing ? 2 : 5", nan).is_nan());
    }

    #[test]
    fn a_question_mark_without_its_colon_is_malformed_rather_than_guessed() {
        assert!(number("1 ? 2", nan).is_nan());
    }
}
