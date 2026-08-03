using System.Globalization;

namespace OpenLogViewer.Core;

/// <summary>Raised when an expression cannot be parsed, with where it went wrong.</summary>
public sealed class MathExpressionException(string message, int position)
    : Exception(message)
{
    /// <summary>Index into the expression text, for pointing at the problem.</summary>
    public int Position { get; } = position;
}

/// <summary>
/// An arithmetic expression over log channels — "AFR - AFR Target 1", or
/// "RPM * Torque / 5252".
///
/// Channel names are matched against the log's own names, longest first, so a
/// name with spaces in it needs no quoting: that is how ECUs name channels, and
/// requiring brackets around "AFR Target 1" would make the common case the
/// awkward one.
///
/// Missing readings propagate. A sample where an input is NaN yields NaN rather
/// than a number, including through comparisons — an unknown is not "false", and
/// silently treating it as one turns a dropout into a confident wrong answer.
/// </summary>
public sealed class MathExpression
{
    private static readonly string[] Functions =
    [
        "abs", "sqrt", "min", "max", "clamp", "floor", "ceil", "round",
        "log", "log10", "exp", "pow", "sign", "if",
    ];

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["e"] = Math.E,
    };

    private readonly Node _root;

    private MathExpression(Node root, string[] references)
    {
        _root = root;
        References = references;
    }

    /// <summary>
    /// Channel names the expression reads, in the order <see cref="Evaluate"/>
    /// expects their values.
    /// </summary>
    public IReadOnlyList<string> References { get; }

    /// <summary>Names the parser recognises as functions, for the UI to offer.</summary>
    public static IReadOnlyList<string> FunctionNames => Functions;

    public double Evaluate(ReadOnlySpan<double> inputs) => _root.Evaluate(inputs);

    public static MathExpression Parse(string text, IEnumerable<string> channelNames)
    {
        var parser = new Parser(text ?? "", channelNames);
        Node root = parser.ParseAll();
        return new MathExpression(root, parser.References);
    }

    public static bool TryParse(
        string text, IEnumerable<string> channelNames,
        out MathExpression? expression, out string? error)
    {
        try
        {
            expression = Parse(text, channelNames);
            error = null;
            return true;
        }
        catch (MathExpressionException e)
        {
            expression = null;
            error = e.Message;
            return false;
        }
    }

    // ----- parser -----------------------------------------------------------

    private sealed class Parser
    {
        private readonly string _text;
        private readonly string[] _channels;
        private readonly List<string> _references = [];
        private int _at;
        private int _depth;

        /// <summary>
        /// How deeply an expression may nest before it is refused.
        ///
        /// This parser calls itself once per precedence level, so a bracket
        /// costs a dozen or so stack frames and a few thousand brackets cost the
        /// whole stack. What that produces is not an exception: a stack overflow
        /// takes the process down where nothing can catch it, so there is no
        /// message, no log line and no chance to say which file did it — the
        /// application simply vanishes.
        ///
        /// It is reachable from a file. Firmware INIs carry expressions in
        /// [OutputChannels], they are downloaded from the internet, and this
        /// application asks people to drop them into a folder of their own. A
        /// depth limit turns that into an ordinary parse error, which callers
        /// already know how to report.
        ///
        /// Sixty-four is far past anything written on purpose. The deepest
        /// expression in any firmware INI here nests three.
        /// </summary>
        private const int MaximumDepth = 64;

        /// <summary>
        /// Counts one level of nesting in, and out again when the scope ends.
        /// </summary>
        private Nesting Deeper()
        {
            if (++_depth > MaximumDepth)
                throw Error($"The expression nests more than {MaximumDepth} levels deep.", _at);

            return new Nesting(this);
        }

        private readonly struct Nesting(Parser parser) : IDisposable
        {
            public void Dispose() => parser._depth--;
        }

        public Parser(string text, IEnumerable<string> channelNames)
        {
            _text = text;

            // Longest first, so "AFR Target 1" wins over "AFR" at the same
            // position. Without this the tail of the name would be left behind
            // and read as a syntax error.
            _channels = [.. channelNames.Where(n => n.Length > 0).OrderByDescending(n => n.Length)];
        }

        public string[] References => [.. _references];

        public Node ParseAll()
        {
            if (_text.Trim().Length == 0) throw Error("The expression is empty.", 0);

            Node node = ParseTernary();
            SkipSpace();

            if (_at < _text.Length)
                throw Error($"Unexpected '{_text[_at]}'.", _at);

            return node;
        }

        /// <summary>
        /// C's conditional operator, at the lowest precedence. Firmware INI files
        /// are written in it — "rpm ? 60000.0 / rpm : 0" — and it is the same
        /// thing as the "if" function, guarded branch and all.
        /// </summary>
        private Node ParseTernary()
        {
            Node condition = ParseOr();
            if (!Take("?")) return condition;

            Node then = ParseTernary();
            if (!Take(":")) throw Error("A '?' needs a matching ':'.", _at);

            return new Call("if", [condition, then, ParseTernary()]);
        }

        private Node ParseOr()
        {
            Node left = ParseAnd();
            while (Take("||")) left = new Binary("||", left, ParseAnd());
            return left;
        }

        private Node ParseAnd()
        {
            Node left = ParseBitOr();
            while (Take("&&")) left = new Binary("&&", left, ParseBitOr());
            return left;
        }

        private Node ParseBitOr()
        {
            Node left = ParseBitAnd();

            // Not '||', which was taken by the caller: a single '|' here is the
            // bitwise one, as in the flag tests firmware INIs use.
            while (Peek('|', '|')) left = new Binary("|", left, ParseBitAnd());
            return left;
        }

        private Node ParseBitAnd()
        {
            Node left = ParseComparison();
            while (Peek('&', '&')) left = new Binary("&", left, ParseComparison());
            return left;
        }

        /// <summary>Takes a single character operator, unless it is doubled.</summary>
        private bool Peek(char op, char doubled)
        {
            SkipSpace();
            if (_at >= _text.Length || _text[_at] != op) return false;
            if (_at + 1 < _text.Length && _text[_at + 1] == doubled) return false;

            _at++;
            return true;
        }

        private Node ParseComparison()
        {
            Node left = ParseAdditive();

            // Not chained: "a < b < c" reads as maths but means something else,
            // so it is better rejected by the trailing-input check than accepted
            // with a surprising meaning.
            foreach (string op in (string[])["<=", ">=", "==", "!=", "<", ">"])
                if (Take(op))
                    return new Binary(op, left, ParseAdditive());

            return left;
        }

        private Node ParseAdditive()
        {
            Node left = ParseTerm();
            while (true)
            {
                if (Take("+")) left = new Binary("+", left, ParseTerm());
                else if (Take("-")) left = new Binary("-", left, ParseTerm());
                else return left;
            }
        }

        private Node ParseTerm()
        {
            Node left = ParsePower();
            while (true)
            {
                if (Take("*")) left = new Binary("*", left, ParsePower());
                else if (Take("/")) left = new Binary("/", left, ParsePower());
                else if (Take("%")) left = new Binary("%", left, ParsePower());
                else return left;
            }
        }

        private Node ParsePower()
        {
            Node left = ParseUnary();

            // Right-associative, as in maths: 2^3^2 is 2^9.
            return Take("^") ? new Binary("^", left, ParsePower()) : left;
        }

        private Node ParseUnary()
        {
            if (Take("-")) return new Unary('-', ParseUnary());
            if (Take("!")) return new Unary('!', ParseUnary());
            if (Take("+")) return ParseUnary();
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            // Counted here because everything that recurses comes through it —
            // a bracketed group, a function's arguments, the arms of a ternary.
            // One place to count is one place to get right.
            using Nesting nesting = Deeper();

            SkipSpace();
            if (_at >= _text.Length) throw Error("The expression ends early.", _at);

            if (Take("("))
            {
                // The whole grammar inside, not merely the part above "or".
                // Starting lower left the conditional operator out, so "rpm ? 1
                // : 0" parsed on its own and "(rpm ? 1 : 0)" did not — the
                // bracket stopped at the '?' and reported a missing ')', which
                // says nothing about what was actually wrong. The same fault
                // reached every function argument, since those start here too.
                Node inner = ParseTernary();
                if (!Take(")")) throw Error("Missing ')'.", _at);
                return inner;
            }

            char c = _text[_at];
            if (char.IsAsciiDigit(c) || c == '.') return ParseNumber();

            // Channels first: a log is free to name a channel "min", and the
            // log's own names should win over the built-in list.
            if (MatchChannel() is { } channel) return channel;
            if (MatchWord() is { } word) return word;

            throw Error($"Unexpected '{c}'.", _at);
        }

        private Node ParseNumber()
        {
            int start = _at;
            while (_at < _text.Length && (char.IsAsciiDigit(_text[_at]) || _text[_at] == '.')) _at++;

            // Exponent form, so 1e-3 is a number rather than "1e" minus 3.
            if (_at < _text.Length && (_text[_at] == 'e' || _text[_at] == 'E'))
            {
                int mark = _at;
                _at++;
                if (_at < _text.Length && (_text[_at] == '+' || _text[_at] == '-')) _at++;

                if (_at < _text.Length && char.IsAsciiDigit(_text[_at]))
                    while (_at < _text.Length && char.IsAsciiDigit(_text[_at])) _at++;
                else
                    _at = mark;
            }

            string token = _text[start.._at];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw Error($"'{token}' is not a number.", start);

            return new Constant(value);
        }

        /// <summary>
        /// Matches the longest known channel name at the current position. Names
        /// run to a word boundary so "MAPX" cannot be read as the channel "MAP"
        /// followed by an unexplained X.
        /// </summary>
        private Node? MatchChannel()
        {
            foreach (string name in _channels)
            {
                if (_at + name.Length > _text.Length) continue;
                if (string.Compare(_text, _at, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;

                int after = _at + name.Length;
                if (after < _text.Length && IsWordCharacter(_text[after])) continue;

                _at = after;

                int index = _references.FindIndex(r => r.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    index = _references.Count;
                    _references.Add(name);
                }

                return new Reference(index);
            }

            return null;
        }

        private Node? MatchWord()
        {
            int start = _at;
            while (_at < _text.Length && IsWordCharacter(_text[_at])) _at++;
            if (_at == start) return null;

            string word = _text[start.._at];

            if (Constants.TryGetValue(word, out double constant)) return new Constant(constant);

            if (!Functions.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                _at = start;
                throw Error($"'{word}' is not a channel in this log, nor a function.", start);
            }

            if (!Take("(")) throw Error($"'{word}' needs its arguments in brackets.", _at);

            var args = new List<Node>();
            if (!Take(")"))
            {
                // Each argument is a whole expression, conditionals included:
                // "min(rpm ? 1 : 2, 3)" is ordinary and used to be refused.
                // Commas still separate them, because the conditional operator
                // binds tighter than a comma does.
                do { args.Add(ParseTernary()); } while (Take(","));
                if (!Take(")")) throw Error("Missing ')'.", _at);
            }

            Call.CheckArity(word, args.Count, start, _text);
            return new Call(word.ToLowerInvariant(), [.. args]);
        }

        private static bool IsWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

        private bool Take(string token)
        {
            SkipSpace();
            if (_at + token.Length > _text.Length) return false;
            if (string.CompareOrdinal(_text, _at, token, 0, token.Length) != 0) return false;

            // A lone '=' is almost certainly a mistyped '=='; refusing to take
            // '=' as the start of '==' keeps that error at the right character.
            _at += token.Length;
            return true;
        }

        private void SkipSpace()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++;
        }

        private static MathExpressionException Error(string message, int at) => new(message, at);
    }

    // ----- tree -------------------------------------------------------------

    private abstract class Node
    {
        public abstract double Evaluate(ReadOnlySpan<double> inputs);
    }

    private sealed class Constant(double value) : Node
    {
        public override double Evaluate(ReadOnlySpan<double> inputs) => value;
    }

    private sealed class Reference(int index) : Node
    {
        public override double Evaluate(ReadOnlySpan<double> inputs) =>
            index < inputs.Length ? inputs[index] : double.NaN;
    }

    private sealed class Unary(char op, Node operand) : Node
    {
        public override double Evaluate(ReadOnlySpan<double> inputs)
        {
            double v = operand.Evaluate(inputs);
            if (double.IsNaN(v)) return double.NaN;

            return op switch
            {
                '-' => -v,
                '!' => v == 0 ? 1 : 0,
                _ => double.NaN,
            };
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override double Evaluate(ReadOnlySpan<double> inputs)
        {
            double a = left.Evaluate(inputs);
            double b = right.Evaluate(inputs);

            // An unknown reading is not zero and not false. Propagating it keeps
            // a dropout visible as a gap instead of turning it into a value.
            if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;

            return op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => a / b,
                "%" => a % b,
                "^" => Math.Pow(a, b),
                "<" => a < b ? 1 : 0,
                "<=" => a <= b ? 1 : 0,
                ">" => a > b ? 1 : 0,
                ">=" => a >= b ? 1 : 0,
                "==" => a == b ? 1 : 0,
                "!=" => a != b ? 1 : 0,
                "&&" => a != 0 && b != 0 ? 1 : 0,
                // Bitwise, on the integer the value represents: firmware INIs
                // test packed flags this way.
                "&" => (long)a & (long)b,
                "|" => (long)a | (long)b,
                "||" => a != 0 || b != 0 ? 1 : 0,
                _ => double.NaN,
            };
        }
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        private static readonly Dictionary<string, (int Min, int Max)> Arity = new(StringComparer.OrdinalIgnoreCase)
        {
            ["abs"] = (1, 1), ["sqrt"] = (1, 1), ["floor"] = (1, 1), ["ceil"] = (1, 1),
            ["round"] = (1, 2), ["log"] = (1, 1), ["log10"] = (1, 1), ["exp"] = (1, 1),
            ["sign"] = (1, 1), ["min"] = (2, 8), ["max"] = (2, 8), ["pow"] = (2, 2),
            ["clamp"] = (3, 3), ["if"] = (3, 3),
        };

        public static void CheckArity(string name, int count, int at, string text)
        {
            if (!Arity.TryGetValue(name, out (int Min, int Max) allowed)) return;
            if (count >= allowed.Min && count <= allowed.Max) return;

            string wanted = allowed.Min == allowed.Max
                ? $"{allowed.Min}"
                : $"{allowed.Min} to {allowed.Max}";

            throw new MathExpressionException(
                $"'{name}' takes {wanted} argument{(allowed.Max == 1 ? "" : "s")}, not {count}.", at);
        }

        public override double Evaluate(ReadOnlySpan<double> inputs)
        {
            // Evaluated first for every function but "if", which must not compute
            // the branch it is not taking — that branch is often the one that
            // divides by zero.
            if (name == "if")
            {
                double condition = args[0].Evaluate(inputs);
                if (double.IsNaN(condition)) return double.NaN;
                return condition != 0 ? args[1].Evaluate(inputs) : args[2].Evaluate(inputs);
            }

            Span<double> values = stackalloc double[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                values[i] = args[i].Evaluate(inputs);
                if (double.IsNaN(values[i])) return double.NaN;
            }

            return name switch
            {
                "abs" => Math.Abs(values[0]),
                "sqrt" => Math.Sqrt(values[0]),
                "floor" => Math.Floor(values[0]),
                "ceil" => Math.Ceiling(values[0]),
                "round" => values.Length == 1
                    ? Math.Round(values[0])
                    : Math.Round(values[0], (int)Math.Clamp(values[1], 0, 15)),
                "log" => Math.Log(values[0]),
                "log10" => Math.Log10(values[0]),
                "exp" => Math.Exp(values[0]),
                "sign" => Math.Sign(values[0]),
                "pow" => Math.Pow(values[0], values[1]),
                "clamp" => Math.Clamp(values[0], values[1], values[2]),
                "min" => Reduce(values, Math.Min),
                "max" => Reduce(values, Math.Max),
                _ => double.NaN,
            };

            static double Reduce(Span<double> values, Func<double, double, double> combine)
            {
                double result = values[0];
                for (int i = 1; i < values.Length; i++) result = combine(result, values[i]);
                return result;
            }
        }
    }
}
