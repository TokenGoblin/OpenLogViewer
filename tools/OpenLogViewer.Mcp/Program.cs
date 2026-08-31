using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenLogViewer.Mcp;

/// <summary>
/// A Model Context Protocol server in front of a running OpenLogViewer.
///
/// <para>
/// The application already serves an HTTP API on the loopback address; this
/// exists because Claude Code and its relatives speak MCP over stdin and stdout
/// rather than HTTP, and asking a model to hold a bearer token and a port is a
/// worse experience than handing it a set of named tools.
/// </para>
/// <para>
/// It is a translator and nothing else. It holds no state, caches nothing, and
/// knows nothing about logs or tunes — every question is forwarded and every
/// answer passed back as it came. That is what keeps it from drifting away from
/// the application it fronts.
/// </para>
/// <para>
/// <b>Everything on stdout is protocol.</b> A stray Console.WriteLine corrupts
/// the stream and the client sees a server that died for no reason, so anything
/// worth saying goes to stderr.
/// </para>
/// </summary>
internal static class Program
{
    private const string ProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,

        // Relaxed, because everything here goes to a model rather than into a
        // web page: a reply spelling every quote 0022 is correct JSON and
        // needlessly hard to read.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static string _address = "";
    private static string _token = "";

    private static async Task<int> Main(string[] args)
    {
        // Where the application left its address and token. Overridable so a
        // second instance, or a workspace somewhere else, can still be reached.
        string details = args.Length > 0
            ? args[0]
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "OpenLogViewer", "agent-api.json");

        Console.Error.WriteLine($"openlogviewer-mcp: reading {details}");

        using var input = Console.OpenStandardInput();
        using var reader = new StreamReader(input);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0) continue;

            JsonNode? message;

            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException e)
            {
                Console.Error.WriteLine($"unparseable: {e.Message}");
                continue;
            }

            if (message is not JsonObject request) continue;

            JsonNode? id = request["id"];
            string method = request["method"]?.GetValue<string>() ?? "";

            // A notification has no id and takes no answer. "initialized" is the
            // usual one, and replying to it is a protocol error rather than a
            // harmless extra.
            if (id is null)
            {
                if (method == "notifications/cancelled") Console.Error.WriteLine("cancelled");
                continue;
            }

            JsonObject response = await Answer(method, request["params"] as JsonObject, details)
                .ConfigureAwait(false);

            response["jsonrpc"] = "2.0";
            response["id"] = id.DeepClone();

            Console.WriteLine(response.ToJsonString(Compact));
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<JsonObject> Answer(string method, JsonObject? parameters, string details)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "openlogviewer",
                            ["version"] = "1",
                        },
                    },
                };

            case "ping":
                return new JsonObject { ["result"] = new JsonObject() };

            case "tools/list":
                return new JsonObject { ["result"] = new JsonObject { ["tools"] = Tools() } };

            case "tools/call":
                return await Call(parameters, details).ConfigureAwait(false);

            default:
                return Failure(-32601, $"no such method: {method}");
        }
    }

    /// <summary>
    /// What the model is offered.
    ///
    /// Described in terms of an engine rather than of HTTP, because the useful
    /// thing to tell a model is what a channel means and when a number is
    /// trustworthy — not that this is a GET.
    /// </summary>
    private static JsonArray Tools() =>
    [
        Tool("olv_state",
             "What OpenLogViewer has open: whether it is watching a live ECU or a saved log, "
             + "the firmware, how many samples and channels, and whether writing is armed. "
             + "Ask this first; everything else depends on what is loaded.",
             new JsonObject()),

        Tool("olv_channels",
             "Every channel available, with its units and the job it does. The role — EngineSpeed, "
             + "Mixture, ManifoldPressure and so on — is the reliable way to find a channel, "
             + "because each firmware spells them differently: a rusEFI calls engine speed "
             + "RPMValue and a MegaSquirt calls it rpm.",
             new JsonObject()),

        Tool("olv_values",
             "The samples of one channel, oldest first, with the matching times. On a live "
             + "session give a number of seconds to get only the newest; leave it out for the lot.",
             new JsonObject
             {
                 ["channel"] = Field("string", "The channel name, exactly as olv_channels gives it."),
                 ["seconds"] = Field("number", "How many seconds back from the newest sample. Omit for all of them."),
             },
             "channel"),

        Tool("olv_insights",
             "What the log says about the engine: fuelling against target, lean excursions under "
             + "load, closed-loop behaviour, sensor faults, and what looks healthy. Each finding "
             + "carries the arithmetic behind it so it can be argued with.",
             new JsonObject()),

        Tool("olv_tune",
             "Every setting in the tune read off the controller, by name. Empty unless an ECU is "
             + "connected and its tune has been read.",
             new JsonObject()),

        Tool("olv_tables",
             "The names of the tables this firmware declares — the VE table, the ignition table "
             + "and the rest.",
             new JsonObject()),

        Tool("olv_table",
             "One table: its cells as rows, and the axes they are looked up on.",
             new JsonObject
             {
                 ["name"] = Field("string", "The table name, as olv_tables gives it."),
             },
             "name"),

        Tool("olv_project",
             "The tuning project for this vehicle: what is wrong with the tune, what has already "
             + "been tried, and what happened. READ THIS FIRST on any tuning question — it is the "
             + "part no log can tell you, and without it you will re-diagnose problems that were "
             + "diagnosed weeks ago and re-suggest changes that were already tried and did not work.",
             new JsonObject()),

        Tool("olv_record_sitting",
             "Records the log in hand against the project: keeps every finding, raises a fix for "
             + "anything newly warned about, and notes a repeat against the fix already tracking "
             + "it. Do this once per log you analyse, so the project shows whether things are "
             + "getting better.",
             new JsonObject
             {
                 ["note"] = Field("string", "What this sitting was, in your words. What was changed before it, what you were testing."),
             }),

        Tool("olv_note_fix",
             "Adds a fix to the project or moves one along. Give an id to change an existing one, "
             + "or leave it out to raise a new one. This changes the record of what is being "
             + "worked on and touches no engine, so it needs no arming. Move a fix to applied when "
             + "a change has been made and to verified only when a later log shows it worked.",
             new JsonObject
             {
                 ["id"] = Field("string", "The fix to change, as olv_project gives it. Omit to raise a new one."),
                 ["title"] = Field("string", "What is wrong, in one line. Required for a new fix."),
                 ["detail"] = Field("string", "The reasoning: what was seen and what you think it means."),
                 ["state"] = Field("string", "open, applied, verified or abandoned."),
                 ["change"] = Field("string", "What was actually changed in the tune."),
             }),

        Tool("olv_set_setting",
             "Changes one setting in the ECU's WORKING MEMORY. Refused unless the person at the "
             + "machine has ticked \"Allow agent writes\". It is never burned, so turning the key "
             + "off undoes it. Read the value first and say what you are changing and why.",
             new JsonObject
             {
                 ["name"] = Field("string", "The setting's name, as olv_tune gives it."),
                 ["value"] = Field("number", "The new value, in the units the firmware declares."),
             },
             "name", "value"),

        Tool("olv_set_table_cell",
             "Changes one cell of one table in the ECU's WORKING MEMORY. Refused unless writing "
             + "is armed, and never burned. Columns and rows are counted from zero.",
             new JsonObject
             {
                 ["table"] = Field("string", "The table name."),
                 ["column"] = Field("integer", "Column index, from zero."),
                 ["row"] = Field("integer", "Row index, from zero."),
                 ["value"] = Field("number", "The new value."),
             },
             "table", "column", "row", "value"),
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties,
                                   params string[] required) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r!)]),
            },
        };

    private static JsonObject Field(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static async Task<JsonObject> Call(JsonObject? parameters, string details)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? "";
        JsonObject arguments = parameters?["arguments"] as JsonObject ?? [];

        if (!Located(details, out string problem)) return Text(problem, failed: true);

        try
        {
            (string Body, bool Refused) answer = name switch
            {
                "olv_state" => await Get("/state").ConfigureAwait(false),
                "olv_channels" => await Get("/channels").ConfigureAwait(false),
                "olv_insights" => await Get("/insights").ConfigureAwait(false),
                "olv_tune" => await Get("/tune").ConfigureAwait(false),
                "olv_tables" => await Get("/tables").ConfigureAwait(false),
                "olv_project" => await Get("/project").ConfigureAwait(false),

                "olv_record_sitting" => await Post("/project/record", new JsonObject
                {
                    ["note"] = Text(arguments, "note"),
                }).ConfigureAwait(false),

                "olv_note_fix" => await Post("/project/fix", new JsonObject
                {
                    ["id"] = Text(arguments, "id"),
                    ["title"] = Text(arguments, "title"),
                    ["detail"] = Text(arguments, "detail"),
                    ["state"] = Text(arguments, "state"),
                    ["change"] = Text(arguments, "change"),
                }).ConfigureAwait(false),

                "olv_values" => await Get(
                    $"/values?channel={Uri.EscapeDataString(Text(arguments, "channel"))}"
                    + $"&seconds={Number(arguments, "seconds")}").ConfigureAwait(false),

                "olv_table" => await Get(
                    $"/table?name={Uri.EscapeDataString(Text(arguments, "name"))}").ConfigureAwait(false),

                "olv_set_setting" => await Post("/tune/set", new JsonObject
                {
                    ["name"] = Text(arguments, "name"),
                    ["value"] = Number(arguments, "value"),
                }).ConfigureAwait(false),

                "olv_set_table_cell" => await Post("/table/set", new JsonObject
                {
                    ["table"] = Text(arguments, "table"),
                    ["column"] = (int)Number(arguments, "column"),
                    ["row"] = (int)Number(arguments, "row"),
                    ["value"] = Number(arguments, "value"),
                }).ConfigureAwait(false),

                _ => throw new InvalidOperationException($"no such tool: {name}"),
            };

            return Text(answer.Body, failed: answer.Refused);
        }
        catch (HttpRequestException e)
        {
            return Text(
                $"OpenLogViewer did not answer: {e.Message}. Is it running with the agent API "
                + "started? It is under Tools, \"Agent API\".",
                failed: true);
        }
        catch (Exception e)
        {
            return Text(e.Message, failed: true);
        }
    }

    /// <summary>Reads the address and token the application left behind.</summary>
    private static bool Located(string details, out string problem)
    {
        problem = "";

        if (_address.Length > 0 && _token.Length > 0) return true;

        if (!File.Exists(details))
        {
            problem = $"No agent API is running: {details} is not there. Start OpenLogViewer and "
                      + "turn on Tools → Agent API.";
            return false;
        }

        try
        {
            JsonNode? found = JsonNode.Parse(File.ReadAllText(details));

            _address = found?["address"]?.GetValue<string>() ?? "";
            _token = found?["token"]?.GetValue<string>() ?? "";
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            problem = $"Could not read {details}: {e.Message}";
            return false;
        }

        if (_address.Length != 0 && _token.Length != 0) return true;

        problem = $"{details} does not name an address and a token.";
        return false;
    }

    /// <summary>
    /// The body, and whether the application refused.
    ///
    /// The second half matters more than it looks. A write refused because
    /// nobody armed writing comes back as a perfectly well-formed JSON object,
    /// and handing that to a model as an ordinary result invites it to read
    /// "writes are not armed" as a description of what it just did. MCP has a
    /// flag for exactly this; the refusal has to reach it.
    /// </summary>
    private static async Task<(string Body, bool Refused)> Send(HttpRequestMessage request)
    {
        request.Headers.Authorization = new("Bearer", _token);

        using HttpResponseMessage answer = await Http.SendAsync(request).ConfigureAwait(false);

        string body = await answer.Content.ReadAsStringAsync().ConfigureAwait(false);

        return (body, !answer.IsSuccessStatusCode);
    }

    private static Task<(string Body, bool Refused)> Get(string path) =>
        Send(new HttpRequestMessage(HttpMethod.Get, _address + path));

    private static Task<(string Body, bool Refused)> Post(string path, JsonObject body) =>
        Send(new HttpRequestMessage(HttpMethod.Post, _address + path)
        {
            Content = new StringContent(body.ToJsonString(Compact), System.Text.Encoding.UTF8,
                                        "application/json"),
        });

    private static string Text(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() ?? "";

    private static double Number(JsonObject arguments, string name)
    {
        JsonNode? node = arguments[name];

        if (node is null) return 0;

        try { return node.GetValue<double>(); }
        catch (Exception) { return double.TryParse(node.ToString(), out double parsed) ? parsed : 0; }
    }

    /// <summary>A tool result, which MCP carries as content rather than as a value.</summary>
    private static JsonObject Text(string body, bool failed = false) => new()
    {
        ["result"] = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = body },
            },
            ["isError"] = failed,
        },
    };

    private static JsonObject Failure(int code, string message) => new()
    {
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
