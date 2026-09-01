namespace OpenLogViewer.Core;

/// <summary>What a condition came to.</summary>
public enum ConditionVerdict
{
    /// <summary>Show it.</summary>
    Shown,

    /// <summary>Hide it: the firmware says it does not apply to this tune.</summary>
    Hidden,

    /// <summary>
    /// The condition could not be worked out — it names something this does not
    /// have, or uses something this does not understand.
    /// </summary>
    Unknown,
}

/// <summary>
/// Decides whether a setting applies to the tune in hand.
///
/// <para>
/// Almost every dialog in a firmware definition is conditional. "Window Sample
/// Type" is only meaningful with knock detection on and set to analogue; a
/// second VVT bank's settings mean nothing on an engine configured with one. The
/// conditions are written against the tune's own constants —
/// <c>{ knk_option &amp;&amp; (knk_option_an == 1) }</c> — so the same definition
/// produces a different set of screens for every tune it is used with.
/// </para>
/// <para>
/// Not the same language as a calculated channel, and deliberately evaluated by
/// its own code. This one is C's: <c>&amp;</c> and <c>|</c> are bitwise,
/// <c>&amp;&amp;</c> and <c>||</c> are logical, any non-zero value is true, and a
/// comparison yields one or nought. Feeding these to an expression parser built
/// for arithmetic would quietly get <c>status8 &amp; 0x40</c> wrong, and the
/// symptom would be a settings page missing the field somebody went looking for.
/// </para>
/// <para>
/// <b>What it does when it cannot decide is the important part.</b> A condition
/// naming a constant this firmware does not declare, or calling one of the
/// functions TunerStudio provides and this does not, cannot be judged either
/// way. The answer is then <see cref="ConditionVerdict.Unknown"/> and the caller
/// shows the field: an irrelevant setting on screen is untidy, while a hidden one
/// is a setting the user cannot reach and has no way of knowing exists. The
/// asymmetry is the whole reason for the third verdict.
/// </para>
/// </summary>
public static class DialogCondition
{
    /// <summary>
    /// Evaluates a condition against a tune's settings.
    /// </summary>
    /// <param name="condition">The expression, without its braces.</param>
    /// <param name="value">
    /// Looks a constant up by name, returning NaN for one this firmware does not
    /// have. A bit field reads as nought or one; everything else as its number.
    /// </param>
    public static ConditionVerdict Evaluate(string condition, Func<string, double> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(condition)) return ConditionVerdict.Shown;

        try
        {
            var parser = new Parser(condition, value);
            double result = parser.ParseAll();

            if (double.IsNaN(result)) return ConditionVerdict.Unknown;

            return result != 0 ? ConditionVerdict.Shown : ConditionVerdict.Hidden;
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            // Malformed, or using something not understood. Either way this
            // cannot say, and saying so is better than guessing at it.
            return ConditionVerdict.Unknown;
        }
    }

    /// <summary>
    /// The same expression as a number rather than as a verdict.
    ///
    /// The two are not interchangeable. An expression in this language need not
    /// be a test — <c>[OutputChannels]</c> declares arithmetic with it, and
    /// <c>engineLoad = { fuelingLoad }</c> means the load, not whether there is
    /// any. Passing that through a yes-or-no answer turns 63 kPa into 1, which
    /// then fails every comparison a dialog condition makes against it and hides
    /// settings the tuner is meant to reach.
    /// </summary>
    /// <returns>The value, or NaN where it cannot be worked out.</returns>
    public static double Number(string expression, Func<string, double> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(expression)) return double.NaN;

        try
        {
            return new Parser(expression, value).ParseAll();
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// Whether to put the thing on screen. Unknown counts as yes, for the reason
    /// given above.
    /// </summary>
    public static bool ShouldShow(string condition, Func<string, double> value) =>
        Evaluate(condition, value) != ConditionVerdict.Hidden;

    /// <summary>
    /// A recursive-descent parser over C's precedence.
    ///
    /// Written out rather than folded into the calculated-channel parser because
    /// the two languages differ in ways that do not show up until they are wrong:
    /// bitwise operators, C's truthiness, and no notion of a missing reading
    /// propagating — here a name that is not known makes the whole condition
    /// unknown rather than the result NaN.
    /// </summary>
    private sealed class Parser(string text, Func<string, double> lookup)
    {
        private int _at;

        public double ParseAll()
        {
            double value = Ternary();
            SkipSpace();

            if (_at < text.Length) throw new FormatException($"unexpected '{text[_at]}'");

            return value;
        }

        // Lowest precedence first, each level deferring to the next.

        /// <summary>
        /// <c>a ? b : c</c> — the lowest precedence of all, and
        /// right-associative, so <c>a ? b : c ? d : e</c> reads as
        /// <c>a ? b : (c ? d : e)</c>.
        ///
        /// <para>
        /// Needed because a firmware's scale is sometimes a choice rather than a
        /// number. A Speeduino states its load-axis resolution as
        /// <c>{ ((algorithm == 0) || (algorithm == 2)) ? 2.000 : 0.500 }</c>.
        /// Without this the expression fails to parse, the scale falls back to
        /// the declared 1, and every load axis is read at half — on the way in
        /// as well as out, so restoring a tune TunerStudio had written would
        /// have doubled the fuel, AFR, VVT and dwell axes on the controller.
        /// </para>
        /// <para>
        /// Both arms are parsed whether or not they are taken, for the same
        /// reason the short-circuits below consume their operands: text left
        /// unread makes the caller declare the whole expression malformed.
        /// </para>
        /// </summary>
        private double Ternary()
        {
            double condition = Or();

            SkipSpace();
            if (_at >= text.Length || text[_at] != '?') return condition;

            _at++;
            double whenTrue = Ternary();

            SkipSpace();
            if (_at >= text.Length || text[_at] != ':')
                throw new FormatException("a ? without its :");

            _at++;
            double whenFalse = Ternary();

            // Unknown in, unknown out: a condition nothing could answer must not
            // quietly settle on the false arm.
            if (double.IsNaN(condition)) return double.NaN;

            return condition != 0 ? whenTrue : whenFalse;
        }

        /// <summary>
        /// <c>a || b || c</c>, true as soon as any operand is.
        ///
        /// The short-circuit decides the answer but must not stop the parse. An
        /// operand that settles it leaves the remaining ones still to be read,
        /// and returning there abandons them — the caller then finds text it
        /// cannot account for and calls the whole condition malformed. So every
        /// operand is consumed and only the verdict short-circuits.
        /// </summary>
        private double Or()
        {
            double result = And();

            while (Take("||"))
            {
                double right = And();

                // Once true, true: a later unknown cannot take that back.
                if (Truthy(result) == true || Truthy(right) == true) result = 1;
                else if (double.IsNaN(result) || double.IsNaN(right)) result = double.NaN;
                else result = 0;
            }

            return result;
        }

        /// <summary>And the mirror: false as soon as any operand is.</summary>
        private double And()
        {
            double result = BitOr();

            while (Take("&&"))
            {
                double right = BitOr();

                if (Truthy(result) == false || Truthy(right) == false) result = 0;
                else if (double.IsNaN(result) || double.IsNaN(right)) result = double.NaN;
                else result = 1;
            }

            return result;
        }

        private double BitOr()
        {
            double left = BitXor();

            while (!Peek("||") && Take("|")) left = Bitwise(left, BitXor(), (a, b) => a | b);

            return left;
        }

        private double BitXor()
        {
            double left = BitAnd();

            while (Take("^")) left = Bitwise(left, BitAnd(), (a, b) => a ^ b);

            return left;
        }

        private double BitAnd()
        {
            double left = Equality();

            while (!Peek("&&") && Take("&")) left = Bitwise(left, Equality(), (a, b) => a & b);

            return left;
        }

        private double Equality()
        {
            double left = Relational();

            while (true)
            {
                if (Take("==")) left = Compare(left, Relational(), (a, b) => a == b);
                else if (Take("!=")) left = Compare(left, Relational(), (a, b) => a != b);
                else return left;
            }
        }

        private double Relational()
        {
            double left = Additive();

            while (true)
            {
                // The two-character forms first, or ">=" is read as ">" and a
                // stray "=".
                if (Take(">=")) left = Compare(left, Additive(), (a, b) => a >= b);
                else if (Take("<=")) left = Compare(left, Additive(), (a, b) => a <= b);
                else if (Take(">")) left = Compare(left, Additive(), (a, b) => a > b);
                else if (Take("<")) left = Compare(left, Additive(), (a, b) => a < b);
                else return left;
            }
        }

        private double Additive()
        {
            double left = Multiplicative();

            while (true)
            {
                if (Take("+")) left = Arithmetic(left, Multiplicative(), (a, b) => a + b);
                else if (Take("-")) left = Arithmetic(left, Multiplicative(), (a, b) => a - b);
                else return left;
            }
        }

        private double Multiplicative()
        {
            double left = Unary();

            while (true)
            {
                if (Take("*")) left = Arithmetic(left, Unary(), (a, b) => a * b);
                else if (Take("/")) left = Divide(left, Unary());
                else if (Take("%")) left = Modulo(left, Unary());
                else return left;
            }
        }

        private double Unary()
        {
            SkipSpace();

            if (Take("!"))
            {
                double value = Unary();
                return double.IsNaN(value) ? double.NaN : value == 0 ? 1 : 0;
            }

            if (Take("~"))
            {
                double value = Unary();
                return double.IsNaN(value) ? double.NaN : ~(long)value;
            }

            if (Take("-"))
            {
                double value = Unary();
                return double.IsNaN(value) ? double.NaN : -value;
            }

            if (Take("+")) return Unary();

            return Primary();
        }

        private double Primary()
        {
            SkipSpace();

            if (_at >= text.Length) throw new FormatException("ended early");

            if (Take("("))
            {
                double value = Or();
                SkipSpace();

                if (!Take(")")) throw new FormatException("missing ')'");

                return value;
            }

            if (char.IsAsciiDigit(text[_at])) return Number();

            if (char.IsLetter(text[_at]) || text[_at] == '_') return Name();

            throw new FormatException($"unexpected '{text[_at]}'");
        }

        private double Number()
        {
            int start = _at;

            // Bit masks are written in hex, and a great many conditions are a
            // mask against a status byte.
            if (text[_at] == '0' && _at + 1 < text.Length && (text[_at + 1] is 'x' or 'X'))
            {
                _at += 2;
                int digits = _at;

                while (_at < text.Length && Uri.IsHexDigit(text[_at])) _at++;

                if (_at == digits) throw new FormatException("empty hex literal");

                // Too wide to hold is malformed rather than fatal: the third
                // verdict exists so that anything unparseable shows the field.
                return ulong.TryParse(
                    text[digits.._at], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out ulong hex)
                    ? hex
                    : throw new FormatException("hex literal too wide");
            }

            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.')) _at++;

            return double.TryParse(
                text[start.._at], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
                ? value
                : throw new FormatException($"bad number '{text[start.._at]}'");
        }

        private double Name()
        {
            int start = _at;

            // Dots and brackets are part of a name here: a condition may test
            // array.boardHasRTC, or one element of an array.
            while (_at < text.Length
                   && (char.IsLetterOrDigit(text[_at]) || text[_at] is '_' or '.' or '[' or ']'))
            {
                _at++;
            }

            string name = text[start.._at];
            SkipSpace();

            // A call to something TunerStudio provides and this does not. The
            // arguments are skipped so the rest of the expression still parses,
            // and the result is unknown, which shows the field.
            if (_at < text.Length && text[_at] == '(')
            {
                SkipBalanced();
                return double.NaN;
            }

            return lookup(name);
        }

        /// <summary>Steps over a bracketed group, whatever is inside it.</summary>
        private void SkipBalanced()
        {
            int depth = 0;

            do
            {
                if (text[_at] == '(') depth++;
                else if (text[_at] == ')') depth--;

                _at++;
            }
            while (_at < text.Length && depth > 0);

            if (depth > 0) throw new FormatException("missing ')'");
        }

        // ----- the operators themselves -------------------------------------

        /// <summary>Nought is false, anything else true, and unknown neither.</summary>
        private static bool? Truthy(double value) =>
            double.IsNaN(value) ? null : value != 0;

        private static double Compare(double left, double right, Func<double, double, bool> test) =>
            double.IsNaN(left) || double.IsNaN(right) ? double.NaN : test(left, right) ? 1 : 0;

        private static double Arithmetic(double left, double right, Func<double, double, double> apply) =>
            double.IsNaN(left) || double.IsNaN(right) ? double.NaN : apply(left, right);

        private static double Divide(double left, double right) =>
            double.IsNaN(left) || double.IsNaN(right) || right == 0 ? double.NaN : left / right;

        private static double Modulo(double left, double right) =>
            double.IsNaN(left) || double.IsNaN(right) || right == 0 ? double.NaN : left % right;

        /// <summary>
        /// A bitwise operator, over whole numbers. Conditions mask status bytes
        /// with values that are integers by nature, so the operands are narrowed
        /// rather than refused.
        /// </summary>
        private static double Bitwise(double left, double right, Func<long, long, long> apply) =>
            double.IsNaN(left) || double.IsNaN(right)
                ? double.NaN
                : apply((long)left, (long)right);

        // ----- reading along the text ---------------------------------------

        private void SkipSpace()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at])) _at++;
        }

        private bool Peek(string token)
        {
            SkipSpace();
            return text.AsSpan(_at).StartsWith(token, StringComparison.Ordinal);
        }

        private bool Take(string token)
        {
            if (!Peek(token)) return false;

            _at += token.Length;
            return true;
        }
    }
}
