namespace OpenLogViewer.Core;

/// <summary>A name Tab can offer, and a few words about it — a channel's units, a setting's range.</summary>
public sealed record ConsoleName(string Name, string Note = "");

/// <summary>
/// The names that change from one ECU to the next, as they stand when Tab is
/// pressed. Supplied by the application; the completion itself knows nothing
/// about where they came from.
/// </summary>
public sealed record ConsoleNames
{
    public IReadOnlyList<ConsoleName> Tables { get; init; } = [];
    public IReadOnlyList<ConsoleName> Channels { get; init; } = [];
    public IReadOnlyList<ConsoleName> Settings { get; init; } = [];
    public IReadOnlyList<ConsoleName> Versions { get; init; } = [];

    public static ConsoleNames None { get; } = new();
}

/// <summary>One thing Tab could put in, as shown and as inserted.</summary>
public sealed record ConsoleSuggestion(string Text, string Note)
{
    /// <summary>Quoted when it has to be, so the line still splits into the words it was meant to.</summary>
    public string Insert => NeedsQuotes(Text) ? $"\"{Text}\"" : Text;

    internal static bool NeedsQuotes(string text) => text.Any(c => char.IsWhiteSpace(c) || c == '"');
}

/// <summary>
/// What Tab can do at one point in a line: the span of the line it would
/// replace, what could go there, and the rest of the command's usage from
/// that point on.
/// </summary>
public sealed record ConsoleCompletion(
    int Start,
    int Length,
    string Typed,
    IReadOnlyList<ConsoleSuggestion> Candidates,
    string Hint)
{
    public static ConsoleCompletion Nothing(int caret, string hint = "") => new(caret, 0, "", [], hint);

    /// <summary>
    /// The longest start every candidate shares, when it says more than was
    /// typed — what a shell fills in on the first Tab when there is more than
    /// one answer. Empty when there is nothing to add.
    /// </summary>
    public string SharedStart
    {
        get
        {
            if (Candidates.Count < 2) return "";

            string first = Candidates[0].Text;
            int length = first.Length;

            foreach (ConsoleSuggestion other in Candidates.Skip(1))
            {
                int i = 0;
                while (i < length && i < other.Text.Length
                       && char.ToUpperInvariant(first[i]) == char.ToUpperInvariant(other.Text[i])) i++;
                length = i;
            }

            // Only a true extension of what was typed. A candidate found by the
            // anywhere-in-the-name fallback shares no start with the typing.
            return length > Typed.Length && first.StartsWith(Typed, StringComparison.OrdinalIgnoreCase)
                ? first[..length]
                : "";
        }
    }

    /// <summary>The line with <paramref name="choice"/> put in, a space after it, and where the caret goes.</summary>
    public (string Line, int Caret) Accept(string line, ConsoleSuggestion choice)
    {
        string after = line[(Start + Length)..];
        string text = choice.Insert + (after.StartsWith(' ') ? "" : " ");

        return (line[..Start] + text + after, Start + choice.Insert.Length + 1);
    }

    /// <summary>The line with only <see cref="SharedStart"/> put in, left open for more typing.</summary>
    public (string Line, int Caret) AcceptShared(string line)
    {
        string shared = SharedStart;
        if (shared.Length == 0) return (line, Start + Length);

        // An opening quote now, if any candidate will need one, so the next
        // character typed lands inside it.
        string text = Candidates.Any(c => ConsoleSuggestion.NeedsQuotes(c.Text)) ? "\"" + shared : shared;

        return (line[..Start] + text + line[(Start + Length)..], Start + text.Length);
    }
}

/// <summary>
/// Tab completion for the console: what can go at the caret, judged from the
/// command catalog and the names the ECU has right now.
/// </summary>
public static class AgentConsoleCompletion
{
    private const string Dangerous = "--dangerous";

    /// <summary>One word of a line as typed, with where it sits and whether a quote is still open.</summary>
    internal readonly record struct Word(int Start, int End, string Text, bool Open);

    public static ConsoleCompletion Complete(string line, int caret, ConsoleNames names)
    {
        caret = Math.Clamp(caret, 0, line.Length);
        string before = line[..caret];
        List<Word> words = Words(before);

        // Is the caret inside the last word, or after a space with a new one
        // still to start?
        bool fresh = words.Count == 0 || (!words[^1].Open && words[^1].End < caret);
        Word current = fresh ? new Word(caret, caret, "", false) : words[^1];
        int index = fresh ? words.Count : words.Count - 1;

        if (index == 0)
        {
            return Offer(current, AgentConsoleCatalog.Verbs.Select(v => new ConsoleSuggestion(v.Name, v.Summary)), "");
        }

        if (AgentConsoleCatalog.Find(words[0].Text) is not { } verb) return ConsoleCompletion.Nothing(caret);

        // Flags can sit anywhere, so they don't count towards which argument
        // the caret is on.
        List<Word> arguments = words.Skip(1).Take(index - 1).ToList();
        bool dangerousTyped = words.Skip(1).Any(w => w.Text == Dangerous);
        int position = arguments.Count(w => !w.Text.StartsWith("--", StringComparison.Ordinal));

        if (current.Text.StartsWith('-'))
        {
            return verb.IsWrite && !dangerousTyped
                ? Offer(current, [new ConsoleSuggestion(Dangerous, "confirm a change to a dangerous constant")], "")
                : ConsoleCompletion.Nothing(caret);
        }

        string hint = Hint(verb, position, dangerousTyped);

        if (position >= verb.Arguments.Count) return ConsoleCompletion.Nothing(caret, hint);

        ConsoleArgument argument = verb.Arguments[position];

        // Numbers, free text and a post's JSON body have nothing to offer but the hint.
        IEnumerable<ConsoleSuggestion>? offered = argument.Kind switch
        {
            ConsoleArgumentKind.Keyword => argument.Words.Select(w => new ConsoleSuggestion(w, "")),
            ConsoleArgumentKind.Table => Suggest(names.Tables),
            ConsoleArgumentKind.Channel => Suggest(names.Channels),
            ConsoleArgumentKind.Setting => Suggest(names.Settings),
            ConsoleArgumentKind.Version => Suggest(names.Versions),
            ConsoleArgumentKind.Route => AgentConsoleCatalog.Routes.Select(r => new ConsoleSuggestion(r, "")),
            _ => null,
        };

        return offered is null
            ? new ConsoleCompletion(current.Start, caret - current.Start, current.Text, [], hint)
            : Offer(current, offered, hint);
    }

    private static IEnumerable<ConsoleSuggestion> Suggest(IReadOnlyList<ConsoleName> names) =>
        names.Select(n => new ConsoleSuggestion(n.Name, n.Note));

    /// <summary>
    /// Everything that starts with what was typed, ignoring case — or, when
    /// nothing does, everything that has it somewhere, so "ign" still finds
    /// "Ignition Advance Table".
    /// </summary>
    private static ConsoleCompletion Offer(Word current, IEnumerable<ConsoleSuggestion> all, string hint)
    {
        List<ConsoleSuggestion> pool = all.DistinctBy(s => s.Text, StringComparer.OrdinalIgnoreCase).ToList();
        string typed = current.Text;

        List<ConsoleSuggestion> found =
            [.. pool.Where(s => s.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase))];

        if (found.Count == 0 && typed.Length > 0)
            found = [.. pool.Where(s => s.Text.Contains(typed, StringComparison.OrdinalIgnoreCase))];

        return new ConsoleCompletion(current.Start, current.End - current.Start, typed, found, hint);
    }

    /// <summary>The usage still to come, from the argument at <paramref name="position"/> on.</summary>
    private static string Hint(ConsoleVerb verb, int position, bool dangerousTyped)
    {
        IEnumerable<string> rest = verb.Arguments.Skip(position).Select(a => a.Usage);
        if (verb.IsWrite && !dangerousTyped) rest = rest.Append("[--dangerous]");

        return string.Join(' ', rest);
    }

    /// <summary>
    /// The words of a line with their spans. A word opened with a quote runs to
    /// the closing quote, or to the end of the line if there is none yet —
    /// which is exactly the case Tab has to handle, mid-way through typing a
    /// quoted name.
    /// </summary>
    internal static List<Word> Words(string line)
    {
        var words = new List<Word>();
        int i = 0;

        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            int start = i;
            var text = new System.Text.StringBuilder();
            bool quoted = false;

            while (i < line.Length && (quoted || !char.IsWhiteSpace(line[i])))
            {
                if (line[i] == '"') quoted = !quoted;
                else text.Append(line[i]);
                i++;
            }

            words.Add(new Word(start, i, text.ToString(), quoted));
        }

        return words;
    }
}
