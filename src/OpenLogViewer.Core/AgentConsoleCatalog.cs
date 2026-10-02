using System.Text;

namespace OpenLogViewer.Core;

/// <summary>What one word after a console command stands for — which decides what Tab offers there.</summary>
public enum ConsoleArgumentKind
{
    /// <summary>A table the firmware declares.</summary>
    Table,

    /// <summary>A channel in the log or the live stream.</summary>
    Channel,

    /// <summary>A scalar setting in the tune.</summary>
    Setting,

    /// <summary>An agent-API route.</summary>
    Route,

    /// <summary>A tune version id from the project, such as v3.</summary>
    Version,

    /// <summary>One of a fixed set of words, listed on the argument.</summary>
    Keyword,

    /// <summary>Any number.</summary>
    Number,

    /// <summary>A whole number.</summary>
    Whole,

    /// <summary>Free text running to the end of the line — a rationale, say.</summary>
    Text,

    /// <summary>A JSON body running to the end of the line.</summary>
    Json,
}

/// <summary>One argument a console command takes.</summary>
public sealed record ConsoleArgument(
    string Name,
    ConsoleArgumentKind Kind,
    bool Optional = false,
    IReadOnlyList<string>? Keywords = null)
{
    public IReadOnlyList<string> Words => Keywords ?? [];

    /// <summary>How it reads in a usage line: <c>&lt;table&gt;</c>, <c>[seconds]</c>, <c>[on|off]</c>, <c>[why…]</c>.</summary>
    public string Usage
    {
        get
        {
            string inner = Kind switch
            {
                ConsoleArgumentKind.Keyword => string.Join('|', Words),
                ConsoleArgumentKind.Text => Name + "…",
                _ => Name,
            };

            return Optional ? $"[{inner}]" : Kind == ConsoleArgumentKind.Keyword ? inner : $"<{inner}>";
        }
    }
}

/// <summary>
/// One console command: its name, what it takes, and how it is described.
/// </summary>
public sealed record ConsoleVerb(
    string Name,
    string Summary,
    string Group,
    IReadOnlyList<ConsoleArgument> Arguments,
    bool IsWrite = false,
    IReadOnlyList<string>? Aliases = null)
{
    public IReadOnlyList<string> OtherNames => Aliases ?? [];

    /// <summary>The whole usage line, e.g. <c>cell &lt;table&gt; &lt;column&gt; &lt;row&gt; &lt;value&gt; [why…] [--dangerous]</c>.</summary>
    public string Usage
    {
        get
        {
            var usage = new StringBuilder(Name);
            foreach (ConsoleArgument argument in Arguments) usage.Append(' ').Append(argument.Usage);
            if (IsWrite) usage.Append(" [--dangerous]");
            return usage.ToString();
        }
    }
}

/// <summary>
/// Every command the console understands, in one place.
///
/// The parser checks a typed verb against this list before anything else, the
/// help text is printed from it, and the commands button and Tab completion
/// read it — so there is one list to keep honest, not four that drift. A verb
/// added here without a parser to go with it fails a test; a parser case
/// without an entry here is unreachable.
/// </summary>
public static class AgentConsoleCatalog
{
    public const string Reading = "Reading";
    public const string Writing = "Writing";
    public const string Anything = "Anything else";
    public const string Console = "The console";

    /// <summary>The groups, in the order they are shown, with a line under each heading where one helps.</summary>
    public static IReadOnlyList<(string Name, string Note)> Groups { get; } =
    [
        (Reading, ""),
        (Writing, "The same guardrails an agent meets: armed, rate-limited, range-checked."),
        (Anything, "Any route, for what the commands above do not cover."),
        (Console, ""),
    ];

    private static ConsoleArgument Arg(string name, ConsoleArgumentKind kind, bool optional = false) =>
        new(name, kind, optional);

    private static ConsoleArgument Words(bool optional, params string[] words) =>
        new("", ConsoleArgumentKind.Keyword, optional, words);

    public static IReadOnlyList<ConsoleVerb> Verbs { get; } =
    [
        new("state", "what is open, and whether writes are armed", Reading, []),
        new("limits", "the write guardrails in force", Reading, []),
        new("channels", "every channel; raw for everything the firmware decodes", Reading, [Words(true, "raw")]),
        new("values", "one channel's samples, the newest N seconds of them", Reading,
            [Arg("channel", ConsoleArgumentKind.Channel), Arg("seconds", ConsoleArgumentKind.Number, true)]),
        new("insights", "what the log says about the engine", Reading, []),
        new("tune", "every setting by name; full adds units and range", Reading, [Words(true, "full")]),
        new("tables", "the tables this firmware declares", Reading, []),
        new("table", "one table's cells and axes", Reading, [Arg("table", ConsoleArgumentKind.Table)]),
        new("log", "the whole log, every channel", Reading,
            [Arg("seconds", ConsoleArgumentKind.Number, true), Arg("decimate", ConsoleArgumentKind.Whole, true)]),
        new("context", "the catch-up an agent reads first", Reading, []),
        new("wire", "the most recent requests sent to the ECU", Reading, [Arg("count", ConsoleArgumentKind.Whole, true)]),
        new("health", "how the link to the ECU is doing", Reading, []),
        new("project", "the tuning project for this vehicle", Reading, []),
        new("versions", "what changed between two tune versions", Reading,
            [Arg("from", ConsoleArgumentKind.Version), Arg("to", ConsoleArgumentKind.Version)]),
        new("staged", "files staged for somebody to pick up", Reading, []),
        new("definition", "which firmware definition is missing", Reading, []),

        new("set", "change one setting in working memory", Writing,
            [Arg("setting", ConsoleArgumentKind.Setting), Arg("value", ConsoleArgumentKind.Number),
             Arg("why", ConsoleArgumentKind.Text, true)],
            IsWrite: true),
        new("cell", "change one table cell in working memory", Writing,
            [Arg("table", ConsoleArgumentKind.Table), Arg("column", ConsoleArgumentKind.Whole),
             Arg("row", ConsoleArgumentKind.Whole), Arg("value", ConsoleArgumentKind.Number),
             Arg("why", ConsoleArgumentKind.Text, true)],
            IsWrite: true),

        new("get", "any route, e.g. get /values?channel=RPM&seconds=5", Anything,
            [Arg("route", ConsoleArgumentKind.Route)]),
        new("post", "any route with a JSON body", Anything,
            [Arg("route", ConsoleArgumentKind.Route), Arg("json", ConsoleArgumentKind.Json, true)]),

        new("api", "start or stop the agent API, or say what it is doing", Console, [Words(true, "on", "off")]),
        new("show", "narrow the list to one kind of request", Console,
            [Words(true, "all", "ai", "console", "writes")]),
        new("clear", "empty the window (the transcript is kept)", Console, [], Aliases: ["cls"]),
        new("help", "this list", Console, [], Aliases: ["?"]),
    ];

    /// <summary>The verb a typed word names, by name or alias, ignoring case.</summary>
    public static ConsoleVerb? Find(string word) =>
        Verbs.FirstOrDefault(v =>
            v.Name.Equals(word, StringComparison.OrdinalIgnoreCase)
            || v.OtherNames.Any(a => a.Equals(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The routes <c>get</c> and <c>post</c> can name. The same list the server
    /// gives at <c>GET /</c>; a test holds the two together.
    /// </summary>
    public static IReadOnlyList<string> Routes { get; } =
    [
        "/", "/state", "/limits", "/channels", "/values", "/insights", "/tune", "/tables", "/table",
        "/tune/set", "/table/set", "/wire/health", "/wire/events",
        "/project", "/project/record", "/project/fix", "/project/keep", "/project/versions/compare",
        "/tune/full", "/log/full", "/context", "/tune/propose", "/tune/apply",
        "/stage/tune", "/stage/table", "/stage", "/definitions/needed", "/definitions/import",
    ];

    /// <summary>The help text, printed from the list rather than kept beside it.</summary>
    public static string Help()
    {
        const int Column = 30;
        var help = new StringBuilder();

        foreach ((string group, string note) in Groups)
        {
            help.AppendLine(group);
            if (note.Length > 0) help.Append("  ").AppendLine(note);

            foreach (ConsoleVerb verb in Verbs.Where(v => v.Group == group))
            {
                string usage = verb.Usage;

                if (usage.Length < Column) help.Append("  ").Append(usage.PadRight(Column)).AppendLine(verb.Summary);
                else help.Append("  ").AppendLine(usage).Append(' ', Column + 2).AppendLine(verb.Summary);
            }

            help.AppendLine();
        }

        help.Append("Quote anything with spaces in it: table \"VE Table 1\". Tab completes names.");
        return help.ToString();
    }
}
