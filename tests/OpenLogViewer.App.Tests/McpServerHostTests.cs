using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenLogViewer.App.Mcp;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The embedded MCP server, end to end: a real listener on a real loopback
/// socket, driven by the real MCP client SDK.
///
/// <para>
/// Unit-testing the logic each tool wraps is necessary and not sufficient. What
/// only this can prove is that a tool call reaches <em>the view model the window
/// is bound to</em> rather than a second, disconnected copy of it — the failure
/// that is silent, passes every other test, and shows up as an agent whose work
/// never appears on screen.
/// </para>
/// </summary>
public sealed class McpServerHostTests : IAsyncLifetime
{
    /// <summary>
    /// Fixed and well up the ephemeral range, so a developer running the
    /// application while the tests run does not collide with 7071.
    /// </summary>
    private const int Port = 58731;

    private readonly ViewModelHarness _harness = new();
    private readonly UiThread _ui = new();

    private McpServerHost _host = null!;
    private MainViewModel _vm = null!;
    private IUiDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        // Built on the UI thread and reached through the real WpfDispatcher, the
        // same way the application does it. An inline dispatcher would run tool
        // calls on the web server's thread, and the first one to touch the
        // channel list would throw — which is precisely what this arrangement
        // exists to keep from happening.
        _vm = _ui.Invoke(() => _harness.NewViewModel());
        _dispatcher = new WpfDispatcher(_ui.Dispatcher);
        _host = new McpServerHost();

        await _host.ArmAsync(new McpServices(_vm, new NoWindow(), _dispatcher), Port);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _ui.Dispose();
        _harness.Dispose();
    }

    private static Task<McpClient> ConnectAsync() =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{Port}/"),
        }));

    /// <summary>The text of a tool's reply, which is JSON this app's tools return.</summary>
    private static JsonElement Payload(CallToolResult result)
    {
        Assert.True(result.IsError is not true, Text(result));

        return JsonDocument.Parse(Text(result)).RootElement;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    // ----- the listener --------------------------------------------------------

    [Fact]
    public void ArmingStartsAListener()
    {
        Assert.True(_host.IsArmed);
        Assert.Equal(Port, _host.Port);
    }

    [Fact]
    public async Task AndDisarmingStopsIt()
    {
        await _host.DisarmAsync();

        Assert.False(_host.IsArmed);
        Assert.Null(_host.Port);

        await Assert.ThrowsAnyAsync<Exception>(ConnectAsync);
    }

    [Fact]
    public async Task ArmingAnArmedServerIsANoOpRatherThanASecondListener()
    {
        // Idempotent, because the menu item can be clicked twice before the first
        // arm has finished.
        await _host.ArmAsync(new McpServices(_vm, new NoWindow(), _dispatcher), Port);

        Assert.True(_host.IsArmed);
        Assert.Equal(Port, _host.Port);
    }

    [Fact]
    public async Task ReArmingDoesNotInheritTheLastSessionsClient()
    {
        // The activity tracker remembers when the last request was, and that
        // memory outlasting a disarm is the stale "connected" light the whole
        // indicator exists to avoid: stop the server with an agent attached,
        // start it again inside the 45-second idle window, and the status bar
        // would announce a client that is not there.
        await using (McpClient client = await ConnectAsync())
        {
            await client.ListToolsAsync();
        }

        Assert.True(_host.HasActiveClient);

        await _host.DisarmAsync();
        await _host.ArmAsync(new McpServices(_vm, new NoWindow(), _dispatcher), Port);

        Assert.True(_host.IsArmed);
        Assert.False(_host.HasActiveClient);
    }

    // ----- a real client -------------------------------------------------------

    [Fact]
    public async Task ARealClientCanListTheTools()
    {
        await using McpClient client = await ConnectAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync();

        Assert.NotEmpty(tools);

        // A handful by name, so a tool file dropping out of assembly scanning is
        // a failure rather than a smaller list nobody counts.
        foreach (string expected in
                 new[] { "get_app_state", "open_log", "list_channels", "get_tune_summary", "scan_faults" })
        {
            Assert.Contains(tools, t => t.Name == expected);
        }
    }

    [Fact]
    public async Task AToolCallReachesTheHarnessOwnViewModel()
    {
        // The proof that this is not a second, disconnected copy. The log is
        // opened through the server, over a socket, and asserted against the
        // exact instance this test created.
        string log = _harness.WriteTypicalLog();

        await using McpClient client = await ConnectAsync();

        JsonElement reply = Payload(await client.CallToolAsync(
            "open_log", new Dictionary<string, object?> { ["path"] = log }));

        Assert.True(reply.GetProperty("opened").GetBoolean());

        Assert.NotNull(_vm.Document);
        Assert.Equal(log, _vm.Document!.FilePath);
        Assert.NotEmpty(_vm.Channels);
    }

    [Fact]
    public async Task AndAnEditMadeInProcessIsVisibleToTheClient()
    {
        // The same seam from the other direction: what the window does, an agent
        // sees.
        string log = _harness.WriteTypicalLog();
        _ui.Invoke(() => _vm.Load(log));

        await using McpClient client = await ConnectAsync();

        JsonElement state = Payload(await client.CallToolAsync("get_app_state"));

        Assert.True(state.GetProperty("log").GetProperty("loaded").GetBoolean());
        Assert.Equal(_vm.Document!.SampleCount, state.GetProperty("log").GetProperty("samples").GetInt32());
    }

    [Fact]
    public async Task ARefusalIsAStructuredAnswerRatherThanAProtocolError()
    {
        // Nothing is loaded, so this is the "not yet" case. It must come back as
        // a normal result carrying a reason an agent can act on — an exception
        // would reach it as an opaque protocol error instead.
        await using McpClient client = await ConnectAsync();

        CallToolResult result = await client.CallToolAsync("get_log_summary");

        Assert.True(result.IsError is not true);

        JsonElement reply = Payload(result);

        Assert.False(reply.GetProperty("loaded").GetBoolean());
        Assert.Contains("open_log", reply.GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    // ----- what must never be there --------------------------------------------

    [Fact]
    public async Task NoToolAppliesARestoreOrClearsFaultCodes()
    {
        // A test asserting the absence of a tool looks odd right up until
        // somebody adds one by reflex. Restoring a saved tune is the largest
        // change this application can make to an engine — the command line
        // deliberately has no flag for it either — and clearing DTCs resets a
        // vehicle's emissions readiness on a diagnosis an agent did not perform.
        await using McpClient client = await ConnectAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync();

        Assert.DoesNotContain(tools, t => t.Name is "apply_restore" or "clear_faults");

        // The planning half is present, and says whose move the rest is.
        Assert.Contains(tools, t => t.Name == "plan_restore");
    }

    [Fact]
    public async Task EveryRegisteredToolResolvesItsDependencies()
    {
        // The test that pays for this file. A service a tool needs but ArmAsync
        // forgot to pass fails at CALL time — not at compile time and not at arm
        // time — so calling every tool once, even with deliberately useless
        // arguments against a fixture with nothing loaded, is what catches it.
        // A structured refusal is not an error; only a resolution failure is.
        await using McpClient client = await ConnectAsync();

        IList<McpClientTool> tools = await client.ListToolsAsync();
        var failed = new List<string>();

        foreach (McpClientTool tool in tools)
        {
            // Skipped: it would open a real serial port, and on a machine with a
            // controller attached a test run must not reach for it.
            if (tool.Name.StartsWith("connect_", StringComparison.Ordinal)) continue;

            try
            {
                CallToolResult result = await client.CallToolAsync(tool.Name, Arguments(tool));

                if (result.IsError is true) failed.Add($"{tool.Name}: {Text(result)}");
            }
            catch (Exception e)
            {
                failed.Add($"{tool.Name}: {e.Message}");
            }
        }

        Assert.True(failed.Count == 0, string.Join("\n", failed));
    }

    /// <summary>
    /// Something for every required argument. The values are deliberately
    /// meaningless — the assertion is "did not fail to resolve", and a tool that
    /// refuses a nonsense path has answered correctly.
    /// </summary>
    private static Dictionary<string, object?> Arguments(McpClientTool tool)
    {
        var arguments = new Dictionary<string, object?>();

        if (!tool.JsonSchema.TryGetProperty("properties", out JsonElement properties))
            return arguments;

        JsonElement[] required = tool.JsonSchema.TryGetProperty("required", out JsonElement r)
            ? [.. r.EnumerateArray()]
            : [];

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (!required.Any(each => each.GetString() == property.Name)) continue;

            string kind = property.Value.TryGetProperty("type", out JsonElement type)
                ? type.ValueKind == JsonValueKind.Array
                    ? type.EnumerateArray().First().GetString() ?? "string"
                    : type.GetString() ?? "string"
                : "string";

            arguments[property.Name] = kind switch
            {
                "integer" => 0,
                "number" => 0d,
                "boolean" => false,
                "array" => Array.Empty<string>(),
                _ => "nothing-by-this-name",
            };
        }

        return arguments;
    }
}
