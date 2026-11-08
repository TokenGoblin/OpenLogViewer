using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// What the shipping MCP server (<c>tools/OpenLogViewer.Mcp</c>) says, line for line: the
/// handshake, the tool list with every description, and what each tool returns when it is run
/// against the real <see cref="AgentServer"/> over the same fixed bench as <see cref="W5Agent"/>,
/// together with what the bench was asked. The Rust MCP server is checked against these.
///
/// The MCP server is an Exe with nothing public, so its private <c>Answer</c> is called by
/// reflection, which is exactly the code path a stdin line takes.
/// </summary>
public sealed class W5Mcp : IEmitter
{
    public string Package => "w5-agent";

    public string Set => "mcp";

    private const string Missing = "/nowhere/agent-api.json";

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        Type program = Assembly.Load("openlogviewer-mcp").GetType("OpenLogViewer.Mcp.Program")!;
        MethodInfo answer = program.GetMethod("Answer", BindingFlags.NonPublic | BindingFlags.Static)!;
        var compact = (JsonSerializerOptions)program.GetField("Compact", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        string Ask(string method, JsonObject? parameters, string details)
        {
            var task = (Task<JsonObject>)answer.Invoke(null, [method, parameters, details])!;
            JsonObject response = task.Result;
            response["jsonrpc"] = "2.0";
            response["id"] = 7;
            return response.ToJsonString(compact);
        }

        var cases = new List<object>();

        // The protocol itself.
        foreach (string method in new[] { "initialize", "ping", "tools/list", "nope/never", "resources/list" })
            cases.Add(new { name = method, method, parameters = (object?)null, output = Ask(method, null, Missing), calls = Array.Empty<string>() });

        // A tool call with no application to talk to.
        cases.Add(new
        {
            name = "no application",
            method = "tools/call",
            parameters = (object?)new { name = "olv_state", arguments = "{}" },
            output = Ask("tools/call", JsonNode.Parse("""{"name":"olv_state","arguments":{}}""")!.AsObject(), Missing),
            calls = Array.Empty<string>(),
        });

        // Every tool against a live server.
        var bench = new W5Agent.Bench();
        using var server = new AgentServer(bench, new AgentServerSettings { Port = 0, Token = W5Agent.Token });
        server.Start();

        string file = Path.Combine(Path.GetTempPath(), $"olv-oracle-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, $$"""{ "address": "{{server.Address}}", "websocket": "x", "token": "{{W5Agent.Token}}" }""");

        try
        {
            var calls = new (string Name, string Tool, string Arguments, string[] Setup)[]
            {
                ("state", "olv_state", "{}", []),
                ("channels", "olv_channels", "{}", []),
                ("channels raw", "olv_channels", """{"raw":true}""", []),
                ("channels raw string", "olv_channels", """{"raw":"true"}""", []),
                ("channels raw junk", "olv_channels", """{"raw":"maybe"}""", []),
                ("wire health", "olv_wire_health", "{}", []),
                ("wire events", "olv_wire_events", "{}", ["events2"]),
                ("wire events 1", "olv_wire_events", """{"count":1}""", ["events2"]),
                ("wire events fraction", "olv_wire_events", """{"count":1.9}""", ["events2"]),
                ("wire events string", "olv_wire_events", """{"count":"2"}""", ["events2"]),
                ("values", "olv_values", """{"channel":"RPM","seconds":5}""", []),
                ("values all", "olv_values", """{"channel":"AFR"}""", []),
                ("values fraction", "olv_values", """{"channel":"RPM","seconds":2.5}""", []),
                ("values spaced", "olv_values", """{"channel":"a b&c=d/é"}""", []),
                ("values non finite", "olv_values", """{"channel":"Lua: torque"}""", ["rich"]),
                ("values none", "olv_values", "{}", []),
                ("values number channel", "olv_values", """{"channel":5}""", []),
                ("insights", "olv_insights", "{}", ["rich"]),
                ("tune", "olv_tune", "{}", ["rich"]),
                ("tables", "olv_tables", "{}", []),
                ("table", "olv_table", """{"name":"VE Table"}""", []),
                ("table unknown", "olv_table", """{"name":"Nope"}""", []),
                ("project", "olv_project", "{}", ["brief"]),
                ("record", "olv_record_sitting", """{"note":"after the VE change"}""", ["brief"]),
                ("record no project", "olv_record_sitting", """{"note":"n"}""", []),
                ("keep", "olv_keep_tune", """{"note":"before the change"}""", ["brief"]),
                ("keep none", "olv_keep_tune", "{}", []),
                ("compare", "olv_compare_versions", """{"from":"v1","to":"v2"}""", ["brief"]),
                ("note fix", "olv_note_fix", """{"id":"f1","title":"Lean","detail":"46% short","state":"open","change":"VE +4"}""", ["brief"]),
                ("note fix partial", "olv_note_fix", """{"title":"Lean"}""", ["brief"]),
                ("set refused", "olv_set_setting", """{"name":"revLimit","value":7000,"rationale":"dyno"}""", []),
                ("set", "olv_set_setting", """{"name":"revLimit","value":7000,"rationale":"dyno","confirmDangerous":true}""", ["armed"]),
                ("set string value", "olv_set_setting", """{"name":"revLimit","value":"7000.5","rationale":"dyno"}""", ["armed"]),
                ("set junk value", "olv_set_setting", """{"name":"revLimit","value":"abc","rationale":"dyno"}""", ["armed"]),
                ("set string flag", "olv_set_setting", """{"name":"revLimit","value":1,"rationale":"x","confirmDangerous":"True"}""", ["armed"]),
                ("cell", "olv_set_table_cell", """{"table":"VE Table","column":1,"row":1,"value":55,"rationale":"smoothing"}""", ["armed"]),
                ("cell clamped", "olv_set_table_cell", """{"table":"VE Table","column":1,"row":1,"value":999,"rationale":"x"}""", ["armed"]),
                ("cell fractional index", "olv_set_table_cell", """{"table":"VE Table","column":1.7,"row":0.2,"value":5,"rationale":"x"}""", ["armed"]),
                ("cell refused", "olv_set_table_cell", """{"table":"VE Table","column":0,"row":0,"value":55,"rationale":"x"}""", []),
                ("tune full", "olv_tune_full", "{}", []),
                ("log full", "olv_log_full", "{}", []),
                ("log full window", "olv_log_full", """{"seconds":0.2,"decimate":2}""", []),
                ("log full decimate only", "olv_log_full", """{"decimate":3}""", []),
                ("context", "olv_context", "{}", ["brief"]),
                ("propose", "olv_propose_tune", """{"settings":[{"name":"revLimit","value":7000}],"cells":[{"table":"VE Table","column":1,"row":0,"value":3.5}]}""", []),
                ("propose none", "olv_propose_tune", "{}", []),
                ("propose passthrough", "olv_propose_tune", """{"settings":[{"value":1,"name":"a","extra":[true,null,{"k":"v"}]}],"cells":"not a list"}""", []),
                ("apply", "olv_apply_tune", """{"settings":[{"name":"revLimit","value":7000}],"note":"dyno","confirmDangerous":true}""", ["armed"]),
                ("apply refused", "olv_apply_tune", """{"settings":[{"name":"revLimit","value":7000}],"note":"dyno"}""", []),
                ("stage tune", "olv_stage_tune", """{"filename":"mine.msq"}""", []),
                ("stage tune default", "olv_stage_tune", "{}", []),
                ("stage tune refused", "olv_stage_tune", """{"filename":"bad"}""", []),
                ("stage table", "olv_stage_table", """{"name":"VE Table","filename":"x.csv"}""", []),
                ("stage table unknown", "olv_stage_table", """{"name":"Nope"}""", []),
                ("stage table spaced", "olv_stage_table", """{"name":"a b&c"}""", []),
                ("list staged", "olv_list_staged", "{}", []),
                ("definition none", "olv_definition_needed", "{}", []),
                ("definition needed", "olv_definition_needed", "{}", ["needed"]),
                ("import", "olv_import_definition", """{"path":"C:\\downloads\\x.ini","source":"https://example.invalid/x.ini","name":"x"}""", []),
                ("import refused", "olv_import_definition", """{"path":"wrong"}""", []),
                ("import content", "olv_import_definition", """{"content":"[MegaTune]\n"}""", []),
                ("no such tool", "olv_nope", "{}", []),
                ("no arguments at all", "olv_state", "", []),
            };

            foreach ((string name, string tool, string arguments, string[] setup) in calls)
            {
                bench.Armed = setup.Contains("armed");
                bench.Rich = setup.Contains("rich");
                bench.ExtraEvent = setup.Contains("events2");
                bench.Brief = setup.Contains("brief") ? "# The E28\n\n## Still open\n" : "";
                bench.Needed = setup.Contains("needed")
                    ? new AgentDefinitionNeed(
                        ["Speeduino 2025.01.7"], "Speeduino", "speeduino 202501", "Speeduino 2025.01.7",
                        "speeduino202501.ini", [], @"C:\defs", "somewhere", ["https://example.invalid/x.ini"], "fetch it")
                    : null;
                bench.Calls.Clear();

                JsonObject parameters = arguments.Length == 0
                    ? new JsonObject { ["name"] = tool }
                    : new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(arguments) };

                string output = Ask("tools/call", parameters, file);

                cases.Add(new
                {
                    name,
                    method = "tools/call",
                    parameters = (object?)new { name = tool, arguments },
                    setup,
                    output,
                    calls = bench.Calls.ToArray(),
                });
            }
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }

        return cases;
    }
}
