using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenLogViewer.Core;

/// <summary>
/// One line typed into the console, turned into the agent-API request it
/// stands for — or into something the console does itself, or a reason it
/// could not be understood.
///
/// The console has no verbs of its own that reach the engine. Every command is
/// a short spelling of an existing route, sent over the same socket an agent
/// uses, so whatever an agent is held to — armed writes, a rationale, the rate
/// limit, range checks, dangerous constants — a person typing here is held to
/// as well. A console that could do something the API cannot would be a
/// second write path, and the second one is always the one that drifts.
/// </summary>
public sealed record AgentConsoleCommand
{
    /// <summary>GET or POST, or empty when this is not a request.</summary>
    public string Method { get; init; } = "";

    /// <summary>The route and query, e.g. <c>/table?name=veTable1</c>.</summary>
    public string Target { get; init; } = "";

    /// <summary>The JSON body for a POST, or empty.</summary>
    public string Body { get; init; } = "";

    /// <summary>A command the console handles without asking the API: help, clear, api.</summary>
    public string Local { get; init; } = "";

    /// <summary>Whatever followed a local command.</summary>
    public string Argument { get; init; } = "";

    /// <summary>Why the line could not be understood, or empty.</summary>
    public string Error { get; init; } = "";

    public bool IsRequest => Method.Length > 0;

    /// <summary>What a write says about itself when the person typing gave no reason.</summary>
    public const string DefaultRationale = "typed in the OpenLogViewer console";

    /// <summary>The command list, printed from <see cref="AgentConsoleCatalog"/>.</summary>
    public static string Help => AgentConsoleCatalog.Help();

    public static AgentConsoleCommand Parse(string line)
    {
        // A POST body is JSON, whose own quotes and escapes mean nothing to
        // the word splitter — so it is taken from the line as typed, before
        // the splitter can object to an escaped quote inside it.
        if (line.TrimStart() is var trimmed
            && trimmed.StartsWith("post", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 4 || char.IsWhiteSpace(trimmed[4])))
        {
            return ParsePost(line);
        }

        List<string> words;

        try
        {
            words = Split(line);
        }
        catch (FormatException e)
        {
            return new AgentConsoleCommand { Error = e.Message };
        }

        if (words.Count == 0) return new AgentConsoleCommand { Error = "" };

        // The catalog decides what is a command; the switch below only says
        // what each one sends.
        if (AgentConsoleCatalog.Find(words[0]) is not { } known)
            return Bad($"No command \"{words[0]}\". Type help for the list.");

        string verb = known.Name;
        List<string> rest = words[1..];

        switch (verb)
        {
            case "help":
            case "clear":
            case "api":
            case "show":
                return new AgentConsoleCommand
                {
                    Local = verb,
                    Argument = string.Join(' ', rest).ToLowerInvariant(),
                };

            case "post": return ParsePost(line);

            case "state": return Get("/state");
            case "limits": return Get("/limits");
            case "channels": return Get(rest is ["raw", ..] ? "/channels?raw=true" : "/channels");
            case "insights": return Get("/insights");
            case "tune": return Get(rest is ["full", ..] ? "/tune/full" : "/tune");
            case "tables": return Get("/tables");
            case "context": return Get("/context");
            case "health": return Get("/wire/health");
            case "project": return Get("/project");
            case "staged": return Get("/stage");
            case "definition": return Get("/definitions/needed");

            case "wire":
                return rest is [{ } count, ..]
                    ? Whole(count) is { } n ? Get($"/wire/events?count={n}") : Bad($"\"{count}\" is not a count")
                    : Get("/wire/events");

            case "values":
            {
                if (rest.Count == 0) return Bad("values wants a channel: values RPM 10");

                string target = $"/values?channel={Uri.EscapeDataString(rest[0])}";
                if (rest.Count > 1)
                {
                    if (Number(rest[1]) is not { } seconds) return Bad($"\"{rest[1]}\" is not a number of seconds");
                    target += $"&seconds={Invariant(seconds)}";
                }

                return Get(target);
            }

            case "table":
                return rest.Count == 0
                    ? Bad("table wants a name: table veTable1 (tables lists them)")
                    : Get($"/table?name={Uri.EscapeDataString(string.Join(' ', rest))}");

            case "log":
            {
                double seconds = 0;
                if (rest.Count > 0)
                {
                    if (Number(rest[0]) is not { } given) return Bad($"\"{rest[0]}\" is not a number of seconds");
                    seconds = given;
                }

                string target = $"/log/full?seconds={Invariant(seconds)}";
                if (rest.Count > 1)
                {
                    if (Whole(rest[1]) is not { } every) return Bad($"\"{rest[1]}\" is not a whole number");
                    target += $"&decimate={every}";
                }

                return Get(target);
            }

            case "versions":
                return rest.Count < 2
                    ? Bad("versions wants two ids: versions v3 v4")
                    : Post("/project/versions/compare", new JsonObject { ["from"] = rest[0], ["to"] = rest[1] });

            case "set":
            {
                bool dangerous = Flagged(rest);
                if (rest.Count < 2) return Bad("set wants a setting and a value: set reqFuel 9.1 leaner at idle");
                if (Number(rest[1]) is not { } value) return Bad($"\"{rest[1]}\" is not a number");

                return Post("/tune/set", new JsonObject
                {
                    ["name"] = rest[0],
                    ["value"] = value,
                    ["rationale"] = Why(rest, 2),
                    ["confirmDangerous"] = dangerous,
                });
            }

            case "cell":
            {
                bool dangerous = Flagged(rest);
                if (rest.Count < 4) return Bad("cell wants a table, column, row and value: cell veTable1 4 6 72");
                if (Whole(rest[1]) is not { } column) return Bad($"\"{rest[1]}\" is not a column number");
                if (Whole(rest[2]) is not { } row) return Bad($"\"{rest[2]}\" is not a row number");
                if (Number(rest[3]) is not { } value) return Bad($"\"{rest[3]}\" is not a number");

                return Post("/table/set", new JsonObject
                {
                    ["table"] = rest[0],
                    ["column"] = column,
                    ["row"] = row,
                    ["value"] = value,
                    ["rationale"] = Why(rest, 4),
                    ["confirmDangerous"] = dangerous,
                });
            }

            case "get":
                return rest.Count == 0 ? Bad("get wants a route: get /state") : Get(Route(rest[0]));

            default:
                throw new InvalidOperationException(
                    $"\"{verb}\" is in the console catalog with nothing to say what it sends.");
        }
    }

    private static AgentConsoleCommand ParsePost(string line)
    {
        string route = AfterWord(line, 1).TrimStart();
        int end = 0;
        while (end < route.Length && !char.IsWhiteSpace(route[end])) end++;

        if (end == 0) return Bad("post wants a route: post /tune/propose {…}");

        string json = route[end..].Trim();
        if (json.Length > 0)
        {
            try
            {
                JsonNode.Parse(json);
            }
            catch (System.Text.Json.JsonException e)
            {
                return Bad($"That body is not JSON: {e.Message}");
            }
        }

        return new AgentConsoleCommand { Method = "POST", Target = Route(route[..end]), Body = json };
    }

    private static AgentConsoleCommand Get(string target) => new() { Method = "GET", Target = target };

    private static AgentConsoleCommand Post(string target, JsonObject body) =>
        new() { Method = "POST", Target = target, Body = body.ToJsonString() };

    private static AgentConsoleCommand Bad(string why) => new() { Error = why };

    private static string Route(string typed) => typed.StartsWith('/') ? typed : "/" + typed;

    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && double.IsFinite(value)
            ? value
            : null;

    private static int? Whole(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    /// <summary>Takes <c>--dangerous</c> out of the words wherever it was typed.</summary>
    private static bool Flagged(List<string> words) =>
        words.RemoveAll(w => w is "--dangerous" or "--confirm-dangerous") > 0;

    private static string Why(List<string> words, int from) =>
        words.Count > from ? string.Join(' ', words[from..]) : DefaultRationale;

    /// <summary>The raw text after the first <paramref name="count"/> whitespace-separated words.</summary>
    private static string AfterWord(string line, int count)
    {
        int i = 0;
        for (int word = 0; word < count; word++)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
        }

        return i < line.Length ? line[i..] : "";
    }

    /// <summary>Splits on spaces, keeping anything in double quotes together.</summary>
    internal static List<string> Split(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool quoted = false, any = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) words.Add(word.ToString());
                word.Clear();
                any = false;
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }

        if (quoted) throw new FormatException("A quote was opened and never closed.");
        if (any) words.Add(word.ToString());

        return words;
    }
}
