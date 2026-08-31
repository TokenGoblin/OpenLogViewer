using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The local API an agent talks to.
///
/// The thing being guarded here is not the JSON. It is that a program which can
/// move numbers inside a running engine now listens on a socket, and that the
/// two properties keeping that reasonable — loopback only, and nothing written
/// unless a person armed it — hold whatever the caller does.
/// </summary>
public class AgentServerTests : IDisposable
{
    private readonly List<IDisposable> _running = [];

    public void Dispose()
    {
        foreach (IDisposable d in _running) { try { d.Dispose(); } catch (Exception) { } }
    }

    private const string Token = "test-token-0123456789";

    /// <summary>A bridge that answers, and records what it was asked to write.</summary>
    private sealed class Bench : IAgentBridge
    {
        public bool Armed { get; set; }

        public List<(string Name, double Value)> Written { get; } = [];

        public List<(string Table, int Column, int Row, double Value)> Cells { get; } = [];

        public AgentState State() => new()
        {
            Mode = "live",
            Signature = "TEST Format 0001.00",
            Samples = 3,
            Seconds = 0.2,
            Rate = 25,
            Channels = 2,
            HasTune = true,
            WritesArmed = Armed,
        };

        public IReadOnlyList<AgentChannel> Channels() =>
        [
            new AgentChannel("RPM", "rpm", 0) { Role = "EngineSpeed" },
            new AgentChannel("AFR", "AFR", 2) { Role = "Mixture" },
        ];

        public IReadOnlyList<double> Values(string channel, double seconds) =>
            channel == "RPM" ? [800, 3000, 5000] : [14.7, 13.2, 12.6];

        public IReadOnlyList<double> Times(double seconds) => [0, 0.1, 0.2];

        public IReadOnlyList<AgentFinding> Insights() =>
            [new AgentFinding("Good", "Fuelling", "Mixture tracks target", "Within 2 %.")];

        public IReadOnlyDictionary<string, double> TuneValues() =>
            new Dictionary<string, double> { ["crankingRPM"] = 300, ["revLimit"] = 6500 };

        public IReadOnlyList<string> TableNames() => ["VE Table"];

        public TuneTable? Table(string name) =>
            name == "VE Table"
                ? new TuneTable(
                    "VE Table",
                    new TuneAxis("rpmBins", "rpm", [800, 3000]),
                    new TuneAxis("mapBins", "kPa", [30, 100]),
                    new double[,] { { 40, 60 }, { 50, 80 } },
                    "%")
                : null;

        public AgentRefusal? SetSetting(string name, double value)
        {
            if (!Armed) return new AgentRefusal("writes are not armed", "Tick it in the application.");

            Written.Add((name, value));
            return null;
        }

        public AgentRefusal? SetTableCell(string table, int column, int row, double value)
        {
            if (!Armed) return new AgentRefusal("writes are not armed");

            Cells.Add((table, column, row, value));
            return null;
        }
    }

    private (AgentServer Server, Bench Bench, HttpClient Client) Serve()
    {
        var bench = new Bench();
        var server = new AgentServer(bench, new AgentServerSettings { Port = 0, Token = Token });
        server.Start();
        _running.Add(server);

        var client = new HttpClient { BaseAddress = new Uri(server.Address) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        _running.Add(client);

        return (server, bench, client);
    }

    // ----- the two properties that matter -------------------------------------

    [Fact]
    public void ItListensOnTheLoopbackAddressAndNowhereElse()
    {
        // The failure this forecloses is a "host" setting somebody turns into
        // 0.0.0.0 to make something work, which puts a socket that can write to
        // an engine on whatever network the laptop is on.
        (AgentServer server, _, _) = Serve();

        Assert.StartsWith("http://127.0.0.1:", server.Address, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingIsAnsweredWithoutTheToken()
    {
        (AgentServer server, _, _) = Serve();

        using var bare = new HttpClient { BaseAddress = new Uri(server.Address) };
        HttpResponseMessage answer = await bare.GetAsync("/state");

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
    }

    [Fact]
    public async Task NorWithTheWrongOne()
    {
        (AgentServer server, _, _) = Serve();

        using var wrong = new HttpClient { BaseAddress = new Uri(server.Address) };
        wrong.DefaultRequestHeaders.Authorization = new("Bearer", "test-token-9876543210");

        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/state")).StatusCode);
    }

    [Fact]
    public void ASettingsRecordWithNoTokenIsRefusedOutright()
    {
        // Rather than starting a server nobody can use, or worse, one anybody can.
        Assert.Throws<ArgumentException>(() =>
            new AgentServer(new Bench(), new AgentServerSettings { Port = 0, Token = "  " }));
    }

    [Fact]
    public async Task AWriteIsRefusedUntilSomebodyArmsIt()
    {
        (_, Bench bench, HttpClient client) = Serve();

        HttpResponseMessage refused = await client.PostAsync(
            "/tune/set", Body(new { name = "revLimit", value = 7000 }));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(bench.Written);
        Assert.Contains("not armed", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AndGoesThroughOnceItIs()
    {
        (_, Bench bench, HttpClient client) = Serve();
        bench.Armed = true;

        HttpResponseMessage answer = await client.PostAsync(
            "/tune/set", Body(new { name = "revLimit", value = 7000 }));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(("revLimit", 7000), bench.Written.Single());

        // And says plainly that it did not burn, because the difference between
        // "the engine is running this" and "the engine will keep running this"
        // is the whole of what a power cycle undoes.
        Assert.Contains("\"burned\":false", await answer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThereIsNoWayToBurn()
    {
        // Not "it refuses"; there is no endpoint. A burn is permanent and the
        // person who can see the engine is the one who should press it.
        (_, Bench bench, HttpClient client) = Serve();
        bench.Armed = true;

        foreach (string path in new[] { "/burn", "/tune/burn", "/table/burn", "/ecu/burn" })
        {
            HttpResponseMessage answer = await client.PostAsync(path, Body(new { page = 0 }));
            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        }
    }

    // ----- reading ------------------------------------------------------------

    [Fact]
    public async Task TheStateSaysWhatIsConnectedAndWhetherWritingIsArmed()
    {
        (_, _, HttpClient client) = Serve();

        using JsonDocument state = JsonDocument.Parse(await client.GetStringAsync("/state"));

        Assert.Equal("live", state.RootElement.GetProperty("mode").GetString());
        Assert.Equal("TEST Format 0001.00", state.RootElement.GetProperty("signature").GetString());
        Assert.False(state.RootElement.GetProperty("writesArmed").GetBoolean());
    }

    [Fact]
    public async Task ChannelsCarryTheirRoleSoAnAgentNeedNotGuessTheSpelling()
    {
        // A rusEFI calls engine speed RPMValue and a MegaSquirt calls it rpm.
        // An agent that has to know that is an agent that works on one firmware.
        (_, _, HttpClient client) = Serve();

        using JsonDocument channels = JsonDocument.Parse(await client.GetStringAsync("/channels"));

        JsonElement first = channels.RootElement[0];
        Assert.Equal("RPM", first.GetProperty("name").GetString());
        Assert.Equal("EngineSpeed", first.GetProperty("role").GetString());
    }

    [Fact]
    public async Task ValuesComeBackWithTheirTimes()
    {
        (_, _, HttpClient client) = Serve();

        using JsonDocument answer =
            JsonDocument.Parse(await client.GetStringAsync("/values?channel=RPM&seconds=5"));

        Assert.Equal(3, answer.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(3, answer.RootElement.GetProperty("times").GetArrayLength());
        Assert.Equal(5000, answer.RootElement.GetProperty("values")[2].GetDouble());
    }

    [Fact]
    public async Task AskingForValuesWithoutNamingAChannelSaysWhatIsMissing()
    {
        (_, _, HttpClient client) = Serve();

        HttpResponseMessage answer = await client.GetAsync("/values");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("channel", await answer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATableComesBackAsRowsOfNumbersWithItsAxes()
    {
        (_, _, HttpClient client) = Serve();

        using JsonDocument table =
            JsonDocument.Parse(await client.GetStringAsync("/table?name=VE%20Table"));

        Assert.Equal(2, table.RootElement.GetProperty("columns").GetInt32());
        Assert.Equal(800, table.RootElement.GetProperty("xBins")[0].GetDouble());

        // Row-major, so values[row][column] reads the way the grid looks.
        Assert.Equal(40, table.RootElement.GetProperty("values")[0][0].GetDouble());
        Assert.Equal(50, table.RootElement.GetProperty("values")[0][1].GetDouble());
    }

    [Fact]
    public async Task AnUnknownTableIsAFourOhFourRatherThanAnEmptyOne()
    {
        (_, _, HttpClient client) = Serve();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/table?name=Nope")).StatusCode);
    }

    [Fact]
    public async Task TheRootSaysWhatThereIsToAsk()
    {
        (_, _, HttpClient client) = Serve();

        string root = await client.GetStringAsync("/");

        Assert.Contains("/live/stream", root, StringComparison.Ordinal);
        Assert.Contains("/insights", root, StringComparison.Ordinal);
    }


    // ----- the live stream ----------------------------------------------------

    private async Task<ClientWebSocket> Subscribe(AgentServer server, params string[] channels)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        _running.Add(socket);

        await socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/live/stream"), CancellationToken.None);

        if (channels.Length > 0)
        {
            byte[] ask = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { channels }));
            await socket.SendAsync(ask, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        return socket;
    }

    private static async Task<JsonDocument> Next(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        WebSocketReceiveResult got = await socket.ReceiveAsync(buffer, cancel.Token);

        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, got.Count));
    }

    /// <summary>Publishes until the subscriber has actually attached.</summary>
    private static async Task PublishUntilSeen(AgentServer server, string[] names, double[] values)
    {
        for (int i = 0; i < 200 && server.Subscribers == 0; i++) await Task.Delay(10);

        for (int i = 0; i < 20; i++)
        {
            server.Publish(i * 0.04, names, values);
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TheStreamNamesItsChannelsOnceAndThenSendsOnlyNumbers()
    {
        // A rusEFI publishes 823 channels. At 25 Hz, repeating their names would
        // be megabytes a minute of spelling.
        (AgentServer server, _, _) = Serve();
        using ClientWebSocket socket = await Subscribe(server);

        string[] names = ["RPM", "AFR"];
        _ = Task.Run(() => PublishUntilSeen(server, names, [3000, 13.2]));

        using JsonDocument schema = await Next(socket);
        Assert.Equal("schema", schema.RootElement.GetProperty("type").GetString());
        Assert.Equal("RPM", schema.RootElement.GetProperty("channels")[0].GetString());

        using JsonDocument frame = await Next(socket);
        Assert.Equal("frame", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(3000, frame.RootElement.GetProperty("v")[0].GetDouble());
        Assert.Equal(13.2, frame.RootElement.GetProperty("v")[1].GetDouble());

        // No names in the frame at all.
        Assert.False(frame.RootElement.TryGetProperty("channels", out _));
    }

    [Fact]
    public async Task AnAgentCanAskForJustTheChannelsItCaresAbout()
    {
        // The schema says what the frames after it carry, and is sent again
        // whenever the selection changes -- so a filter asked for just after
        // connecting takes effect at the next schema rather than retroactively.
        // An agent reads schemas as they come; this waits for the one it asked
        // for rather than assuming it beat the first frame.
        (AgentServer server, _, _) = Serve();
        using ClientWebSocket socket = await Subscribe(server, "AFR");

        string[] names = ["RPM", "AFR", "CLT"];
        _ = Task.Run(() => PublishUntilSeen(server, names, [3000, 13.2, 88]));

        int columns = -1;

        for (int message = 0; message < 40 && columns != 1; message++)
        {
            using JsonDocument got = await Next(socket);

            if (got.RootElement.GetProperty("type").GetString() != "schema") continue;

            JsonElement channels = got.RootElement.GetProperty("channels");
            columns = channels.GetArrayLength();

            if (columns == 1) Assert.Equal("AFR", channels[0].GetString());
        }

        Assert.Equal(1, columns);

        using JsonDocument frame = await Next(socket);
        Assert.Equal("frame", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(13.2, frame.RootElement.GetProperty("v")[0].GetDouble());
    }

    [Fact]
    public void PublishingNeverWaitsForASubscriber()
    {
        // The property that keeps the API from slowing down what it watches. A
        // subscriber that cannot keep up loses frames; the poll thread does not
        // lose time. Ten thousand frames with nobody reading them must still
        // return promptly.
        (AgentServer server, _, _) = Serve();

        string[] names = ["RPM"];
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < 10_000; i++) server.Publish(i * 0.04, names, [i]);

        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 2000,
                    $"publishing 10,000 frames took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ANewFrameReplacesAnUndeliveredOneRatherThanQueueingBehindIt()
    {
        // An agent asking what the engine is doing wants the current answer. A
        // backlog of stale frames delivered late is worse than a gap, and the
        // frame says how many it stood in for.
        (AgentServer server, _, _) = Serve();
        using ClientWebSocket socket = await Subscribe(server);

        string[] names = ["RPM"];
        for (int i = 0; i < 200 && server.Subscribers == 0; i++) await Task.Delay(10);

        // Far faster than anything can read them.
        for (int i = 0; i < 500; i++) server.Publish(i * 0.04, names, [i]);

        using JsonDocument schema = await Next(socket);
        Assert.Equal("schema", schema.RootElement.GetProperty("type").GetString());

        using JsonDocument frame = await Next(socket);

        // Whatever arrives is recent rather than the first of five hundred, and
        // the count of what it replaced is carried with it.
        Assert.True(frame.RootElement.GetProperty("skipped").GetInt32() > 0,
                    "the frame should say how many it stood in for");
    }

    [Fact]
    public async Task AStreamOnTheWrongPathIsRefused()
    {
        (AgentServer server, _, _) = Serve();

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        _running.Add(socket);

        await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/nope"), CancellationToken.None));
    }

    [Fact]
    public async Task AStreamWithoutTheTokenIsRefused()
    {
        (AgentServer server, _, _) = Serve();

        var socket = new ClientWebSocket();
        _running.Add(socket);

        await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            socket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{server.Port}/live/stream"), CancellationToken.None));
    }

    private static StringContent Body(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}
