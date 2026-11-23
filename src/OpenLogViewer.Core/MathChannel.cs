namespace OpenLogViewer.Core;

/// <summary>
/// A channel the user defines rather than the logger records — "AFR Error" as
/// AFR minus its target, boost against a target, torque converted to power.
///
/// Held by name and expression rather than by anything log-specific, so one
/// definition applies to every log that carries the channels it reads.
/// </summary>
public sealed record MathChannel
{
    // Not `required`, though both are. A required member missing from the JSON
    // fails the whole document, so one hand-edited entry with a typo would take
    // every other definition with it. The store validates each entry instead and
    // drops only the one that is unusable.
    public string Name { get; init; } = "";

    /// <summary>Units for display; purely a label, never used in the arithmetic.</summary>
    public string Units { get; init; } = "";

    /// <summary>Decimal places for display.</summary>
    public int Digits { get; init; } = 2;

    public string Expression { get; init; } = "";

    public bool Enabled { get; init; } = true;
}

/// <summary>Why a definition produced no channel for a particular log.</summary>
public sealed record MathChannelProblem(string Name, string Reason);

/// <summary>Definitions applied to one log: what was built, and what was not.</summary>
public sealed record MathChannelResult(
    IReadOnlyList<LogChannel> Channels,
    IReadOnlyList<MathChannelProblem> Problems);

public static class MathChannelBuilder
{
    /// <summary>
    /// Evaluates each enabled definition over a log.
    ///
    /// A definition that cannot be evaluated is reported rather than thrown: one
    /// broken entry must not stop a log opening, and the user needs to be told
    /// which it was.
    ///
    /// <para>
    /// Definitions are placed by repeated passes rather than in list order, and
    /// each one that builds joins the pool the rest can read. Order used to
    /// decide it, which made a set of definitions that all refer to one another
    /// work or fail depending on the sequence they happened to be saved in —
    /// invisible in the file and unfixable from the editor, which appends.
    /// </para>
    /// </summary>
    public static MathChannelResult Build(LogDocument document, IEnumerable<MathChannel> definitions)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definitions);

        var built = new List<LogChannel>();

        // Names resolve against the log first, then anything already calculated.
        var available = new Dictionary<string, LogChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (LogChannel channel in document.Channels) available[channel.Name] = channel;
        available[document.Time.Name] = document.Time;

        List<Pending> pending = [.. definitions.Where(d => d.Enabled).Select(d => new Pending(d))];

        // Round after round until one goes by with nothing new placed. A
        // definition that failed only because it reads a channel not built yet
        // gets another go once that one exists; anything still refusing when the
        // rounds stop is genuinely unbuildable, and a cycle simply never places.
        bool progress = true;
        while (progress)
        {
            progress = false;

            foreach (Pending p in pending)
            {
                if (p.Settled) continue;

                if (p.Name.Length == 0)
                {
                    p.Fail("The channel needs a name.", final: true);
                    continue;
                }

                if (available.ContainsKey(p.Name))
                {
                    p.Fail("This log already has a channel with that name.", final: true);
                    continue;
                }

                if (!MathExpression.TryParse(
                        p.Definition.Expression, available.Keys,
                        out MathExpression? expression, out string? error))
                {
                    // Not final: another definition may yet supply what this reads.
                    p.Fail(error ?? "The expression could not be read.", final: false);
                    continue;
                }

                LogChannel channel = Evaluate(
                    p.Name, p.Definition, expression!, available, document.SampleCount);

                built.Add(channel);
                available[p.Name] = channel;
                p.Succeed();
                progress = true;
            }
        }

        var problems = new List<MathChannelProblem>();
        foreach (Pending p in pending)
        {
            if (p.Reason is null) continue;
            problems.Add(new MathChannelProblem(p.Definition.Name, Explain(p, pending)));
        }

        return new MathChannelResult(built, problems);
    }

    /// <summary>
    /// Why a definition did not build, said in terms of the definition that
    /// actually broke rather than the symptom.
    ///
    /// A chain fails at its root, and the parser can only complain about the
    /// name in front of it. With "Airflow (speed density)" gone, "Power (speed
    /// density)" reports the missing airflow correctly — but "Torque (est)",
    /// which reads the power, matches the log's own shorter "Power" channel and
    /// reports "Unexpected '('" from the bracket left over in the name. That is
    /// true, unhelpful, and points two definitions away from the cause.
    /// </summary>
    private static string Explain(Pending failed, List<Pending> all)
    {
        foreach (Pending other in all)
        {
            if (ReferenceEquals(other, failed) || other.Reason is null || other.Name.Length == 0)
                continue;

            // Matched as literal text, which is how the parser matches a channel
            // name too — names carry spaces and brackets and are not tokens.
            if (failed.Definition.Expression.Contains(other.Name, StringComparison.OrdinalIgnoreCase))
                return $"It reads \"{other.Name}\", which could not be built: {other.Reason}";
        }

        return failed.Reason!;
    }

    /// <summary>
    /// The definitions that read a channel by this name, so that removing or
    /// renaming one can say what it takes with it.
    ///
    /// Matched as literal text against each expression, which is how the parser
    /// resolves a channel name: names carry spaces and brackets, so there is no
    /// token to compare. Bounded at both ends the way the parser bounds them, so
    /// removing "Power" does not claim "Power (speed density)" reads it.
    /// </summary>
    public static IReadOnlyList<MathChannel> Dependents(
        string name, IEnumerable<MathChannel> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        string wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return [];

        MathChannel[] all = [.. definitions];

        // Longer names that contain this one. MathExpression resolves the
        // longest name that fits, so text reading "Power (speed density)" is not
        // a use of "Power" — and without this, removing the short one would
        // claim it breaks every definition that reads the long one.
        string[] longer =
        [
            .. all.Select(d => d.Name.Trim())
                  .Where(n => n.Length > wanted.Length
                              && n.Contains(wanted, StringComparison.OrdinalIgnoreCase)),
        ];

        return
        [
            .. all.Where(d =>
                !d.Name.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)
                && Reads(d.Expression, wanted, longer)),
        ];
    }

    private static bool Reads(string expression, string name, string[] longer)
    {
        if (string.IsNullOrEmpty(expression)) return false;

        int at = 0;
        while ((at = expression.IndexOf(name, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int after = at + name.Length;

            // A name runs to a word boundary, so "MAP" is not found inside
            // "MAPX" — the same rule MathExpression matches channels by.
            bool bounded =
                (at == 0 || !IsWordCharacter(expression[at - 1]))
                && (after >= expression.Length || !IsWordCharacter(expression[after]));

            if (bounded && !CoveredByLonger(expression, at, after, longer)) return true;

            at = after;
        }

        return false;
    }

    /// <summary>Whether a longer channel name spans this stretch of the expression.</summary>
    private static bool CoveredByLonger(string expression, int from, int to, string[] longer)
    {
        foreach (string other in longer)
        {
            int at = 0;
            while ((at = expression.IndexOf(other, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (at <= from && at + other.Length >= to) return true;
                at++;
            }
        }

        return false;
    }

    private static bool IsWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>One definition part-way through being placed.</summary>
    private sealed class Pending(MathChannel definition)
    {
        public MathChannel Definition { get; } = definition;

        public string Name { get; } = definition.Name.Trim();

        /// <summary>Why it last refused, or null once it has been built.</summary>
        public string? Reason { get; private set; }

        /// <summary>Whether it is finished with — built, or refused for good.</summary>
        public bool Settled { get; private set; }

        public void Fail(string reason, bool final)
        {
            Reason = reason;
            Settled = final;
        }

        public void Succeed()
        {
            Reason = null;
            Settled = true;
        }
    }

    private static LogChannel Evaluate(
        string name, MathChannel definition, MathExpression expression,
        Dictionary<string, LogChannel> available, int sampleCount)
    {
        LogChannel[] sources = [.. expression.References.Select(r => available[r])];

        var values = new float[sampleCount];
        Span<double> inputs = sources.Length <= 16 ? stackalloc double[sources.Length] : new double[sources.Length];

        for (int i = 0; i < sampleCount; i++)
        {
            for (int s = 0; s < sources.Length; s++) inputs[s] = sources[s].At(i);

            double result = expression.Evaluate(inputs);

            // A division by zero gives an infinity, which would take the channel's
            // range with it and flatten every real value against the axis. It is
            // "could not be computed here", which is what NaN already means.
            values[i] = double.IsFinite(result) ? (float)result : float.NaN;
        }

        return LogChannel.Adopt(name, definition.Units, definition.Digits, values);
    }
}
