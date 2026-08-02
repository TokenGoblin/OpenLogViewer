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
    /// Definitions are applied in order and each successful one joins the pool
    /// the rest can read, so a calculated channel may build on an earlier one.
    /// A definition that cannot be evaluated is reported rather than thrown:
    /// one broken entry must not stop a log opening, and the user needs to be
    /// told which it was.
    /// </summary>
    public static MathChannelResult Build(LogDocument document, IEnumerable<MathChannel> definitions)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(definitions);

        var built = new List<LogChannel>();
        var problems = new List<MathChannelProblem>();

        // Names resolve against the log first, then anything already calculated.
        var available = new Dictionary<string, LogChannel>(StringComparer.OrdinalIgnoreCase);
        foreach (LogChannel channel in document.Channels) available[channel.Name] = channel;
        available[document.Time.Name] = document.Time;

        foreach (MathChannel definition in definitions)
        {
            if (!definition.Enabled) continue;

            string name = definition.Name.Trim();
            if (name.Length == 0)
            {
                problems.Add(new MathChannelProblem(definition.Name, "The channel needs a name."));
                continue;
            }

            if (available.ContainsKey(name))
            {
                problems.Add(new MathChannelProblem(
                    name, "This log already has a channel with that name."));
                continue;
            }

            if (!MathExpression.TryParse(definition.Expression, available.Keys, out MathExpression? expression, out string? error))
            {
                problems.Add(new MathChannelProblem(name, error ?? "The expression could not be read."));
                continue;
            }

            LogChannel channel = Evaluate(name, definition, expression!, available, document.SampleCount);
            built.Add(channel);
            available[name] = channel;
        }

        return new MathChannelResult(built, problems);
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
