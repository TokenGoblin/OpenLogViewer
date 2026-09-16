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

        public IReadOnlyList<AgentChannel> Channels(bool raw = false) => raw
            ?
            [
                new AgentChannel("RPM", "rpm", 0),
                new AgentChannel("AFR", "AFR", 2),
                new AgentChannel("internal_padding_1", "", 0),
            ]
            :
            [
                new AgentChannel("RPM", "rpm", 0) { Role = "EngineSpeed" },
                new AgentChannel("AFR", "AFR", 2) { Role = "Mixture" },
            ];

        public AgentWireHealth WireHealth() =>
            new(Sampled: 10, Failures: 1, SuccessRate: 0.9,
                FailuresByKind: new Dictionary<string, int> { ["Timeout"] = 1 },
                SinceLastSuccessSeconds: 0.4, LongestRecentMs: 12);

        public List<AgentWireEvent> Events { get; } =
        [
            new(1, DateTime.UtcNow, "read realtime", "Human", 0, 3.1, "Ok", "None", ""),
        ];

        public IReadOnlyList<AgentWireEvent> WireEvents(int count) => [.. Events.Take(count)];

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

        public string Brief { get; set; } = "";

        public List<string> Sittings { get; } = [];

        public List<string> Noted { get; } = [];

        public string ProjectBrief() => Brief;

        public IReadOnlyList<string> Projects() => ["The E28"];

        public List<string> Kept { get; } = [];

        public AgentRefusal? KeepTune(string note)
        {
            if (Brief.Length == 0) return new AgentRefusal("no project is open");

            Kept.Add(note);
            return null;
        }

        public string CompareVersions(string from, string to) =>
            Brief.Length == 0 ? "No project is open." : $"{from} to {to}: 2 settings differ.";

        public AgentRefusal? RecordSitting(string note)
        {
            if (Brief.Length == 0) return new AgentRefusal("no project is open");

            Sittings.Add(note);
            return null;
        }

        public AgentRefusal? NoteFix(string id, string title, string detail, string state, string change)
        {
            if (Brief.Length == 0) return new AgentRefusal("no project is open");

            Noted.Add($"{id}/{title}/{state}");
            return null;
        }

        // ----- composition -----------------------------------------------------

        public AgentTuneFull TuneFull() => new(
            [
                new AgentSetting("crankingRPM", 300, "rpm") { Low = 0, High = 1000 },
                new AgentSetting("egoType", 1, "") { Options = ["Off", "Narrow Band", "Wide Band"] },
            ],
            [
                new AgentTable(
                    "VE Table", "%", 2, 2,
                    [800, 3000], [30, 100], "rpm", "kPa", "rpmBins", "mapBins",
                    [new double[] { 40, 60 }, new double[] { 50, 80 }]),
            ]);

        public AgentLogFull LogFull(double seconds, int decimate)
        {
            double[] times = [0, 0.1, 0.2, 0.3, 0.4];
            double[] rpm = [800, 1500, 3000, 4200, 5000];
            double[] afr = [14.7, 14.5, 13.2, 12.9, 12.6];

            int from = 0;

            if (seconds > 0)
            {
                double until = times[^1] - seconds;
                for (int i = times.Length - 1; i >= 0; i--)
                {
                    if (times[i] < until) { from = i + 1; break; }
                }
            }

            static IReadOnlyList<double> Window(double[] values, int from, int stride)
            {
                IEnumerable<double> tailed = values.Skip(from);
                return stride <= 1 ? [.. tailed] : [.. tailed.Where((_, i) => i % stride == 0)];
            }

            return new AgentLogFull(
                Window(times, from, decimate),
                [
                    new AgentChannelSamples("RPM", "rpm", Window(rpm, from, decimate)),
                    new AgentChannelSamples("AFR", "AFR", Window(afr, from, decimate)),
                ]);
        }

        public AgentContext Context() => new(
            ProjectBrief(), State(), new AgentTuneSummary(TuneValues().Count, TableNames()),
            Insights(), WireHealth());
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
    public async Task RawChannelsAreOnlyReturnedWhenAsked()
    {
        (_, _, HttpClient client) = Serve();

        using JsonDocument normal = JsonDocument.Parse(await client.GetStringAsync("/channels"));
        Assert.Equal(2, normal.RootElement.GetArrayLength());

        using JsonDocument raw = JsonDocument.Parse(await client.GetStringAsync("/channels?raw=true"));
        Assert.Equal(3, raw.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task WireHealthReportsWhatTheBridgeSays()
    {
        (_, _, HttpClient client) = Serve();

        using JsonDocument health = JsonDocument.Parse(await client.GetStringAsync("/wire/health"));

        Assert.Equal(10, health.RootElement.GetProperty("sampled").GetInt32());
        Assert.Equal(1, health.RootElement.GetProperty("failures").GetInt32());
        Assert.Equal(0.9, health.RootElement.GetProperty("successRate").GetDouble());
    }

    [Fact]
    public async Task WireEventsComeBackNewestFirst()
    {
        (_, Bench bench, HttpClient client) = Serve();
        bench.Events.Insert(0, new AgentWireEvent(2, DateTime.UtcNow, "write page 3", "Agent", 0, 5.5, "Ok", "None", ""));

        using JsonDocument events = JsonDocument.Parse(await client.GetStringAsync("/wire/events"));

        Assert.Equal(2, events.RootElement.GetArrayLength());
        Assert.Equal("write page 3", events.RootElement[0].GetProperty("context").GetString());
        Assert.Equal("Agent", events.RootElement[0].GetProperty("origin").GetString());
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
    public async Task ARawSubscriberSeesOnlyPublishRawFrames()
    {
        // The filtered stream and the raw one are different subscriptions with
        // different schemas; a subscriber who asked for one must never see a
        // frame meant for the other.
        (AgentServer server, _, _) = Serve();

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        _running.Add(socket);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/live/stream"), CancellationToken.None);

        byte[] ask = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { raw = true }));
        await socket.SendAsync(ask, WebSocketMessageType.Text, true, CancellationToken.None);

        for (int i = 0; i < 200 && server.Subscribers == 0; i++) await Task.Delay(10);

        // Filtered publishes the raw subscriber must ignore, alongside the raw
        // ones it should see.
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                server.Publish(i * 0.04, ["RPM"], [3000]);
                server.PublishRaw(i * 0.04, ["RPM", "internal_padding_1"], [3000, 0]);
                await Task.Delay(5);
            }
        });

        // The subscribe message and the first filtered publish are a race —
        // exactly like the plain channel filter above — so this reads schemas
        // until it sees the raw one (two channels) rather than assuming the
        // first schema already reflects it.
        int columns = -1;

        for (int message = 0; message < 60 && columns != 2; message++)
        {
            using JsonDocument got = await Next(socket);
            if (got.RootElement.GetProperty("type").GetString() != "schema") continue;

            columns = got.RootElement.GetProperty("channels").GetArrayLength();
        }

        Assert.Equal(2, columns);

        using JsonDocument frame = await Next(socket);
        Assert.Equal("frame", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(3000, frame.RootElement.GetProperty("v")[0].GetDouble());
        Assert.Equal(0, frame.RootElement.GetProperty("v")[1].GetDouble());
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

    // ----- the project --------------------------------------------------------

    [Fact]
    public async Task TheProjectComesBackAsProseRatherThanAsFields()
    {
        // It is read the way a scratchpad is read. Handing a model a shape to
        // reassemble, when the useful thing is a paragraph saying what was tried
        // and what happened, makes it do work that has already been done.
        (_, Bench bench, HttpClient client) = Serve();
        bench.Brief = "# The E28\n\n## Still open\n\n### lean-under-load — Lean above 150 kPa\n";

        using JsonDocument answer = JsonDocument.Parse(await client.GetStringAsync("/project"));

        Assert.True(answer.RootElement.GetProperty("open").GetBoolean());
        Assert.Contains("lean-under-load",
                        answer.RootElement.GetProperty("brief").GetString()!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoProjectOpenItSaysSoRatherThanInventingOne()
    {
        (_, _, HttpClient client) = Serve();

        using JsonDocument answer = JsonDocument.Parse(await client.GetStringAsync("/project"));

        Assert.False(answer.RootElement.GetProperty("open").GetBoolean());
    }

    [Fact]
    public async Task RecordingASittingNeedsAProjectAndSaysWhenThereIsNone()
    {
        (_, Bench bench, HttpClient client) = Serve();

        HttpResponseMessage refused = await client.PostAsync(
            "/project/record", Body(new { note = "after the VE change" }));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(bench.Sittings);
    }

    [Fact]
    public async Task AndGoesThroughWithOne()
    {
        (_, Bench bench, HttpClient client) = Serve();
        bench.Brief = "# The E28";

        HttpResponseMessage answer = await client.PostAsync(
            "/project/record", Body(new { note = "after the VE change" }));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("after the VE change", bench.Sittings.Single());
    }

    [Fact]
    public async Task NotingAFixNeedsNoArmingBecauseItTouchesNoEngine()
    {
        // The gate that matters is on writing to the ECU. Recording what is
        // being worked on cannot hurt anything, and an agent that has to ask
        // permission to take notes will not take them.
        (_, Bench bench, HttpClient client) = Serve();
        bench.Brief = "# The E28";

        Assert.False(bench.State().WritesArmed);

        HttpResponseMessage answer = await client.PostAsync(
            "/project/fix",
            Body(new { title = "Lean above 150 kPa", detail = "46% short", state = "open" }));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("Lean above 150 kPa", bench.Noted.Single(), StringComparison.Ordinal);
    }

    // ----- the activity indicator -----------------------------------------------

    [Fact]
    public async Task EveryRouteIsRecordedAgainstTheActivityLog()
    {
        (AgentServer server, _, HttpClient client) = Serve();

        await client.GetStringAsync("/state");

        IReadOnlyList<AgentActivityEvent> events = server.Activity.Recent(10);

        // A start and an end for the one request made.
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("/state", e.Route));
        Assert.All(events, e => Assert.Equal(AgentActivityKind.Read, e.Kind));
        Assert.False(events[0].InFlight);   // newest first: the end
        Assert.True(events[1].InFlight);    // then the start
    }

    [Fact]
    public async Task AWriteRouteIsRecordedAsSuchEvenWhenItIsRefused()
    {
        // The activity log is about what an agent asked this application to
        // do, not whether the request was allowed through — a refused write
        // attempt is still an agent trying to write, and a person watching the
        // indicator should see that just as plainly as a write that went
        // through.
        (AgentServer server, _, HttpClient client) = Serve();

        await client.PostAsync("/tune/set", Body(new { name = "revLimit", value = 7000 }));

        AgentActivityEvent[] writes = [.. server.Activity.Recent(10).Where(e => e.Route == "/tune/set")];
        Assert.NotEmpty(writes);
        Assert.All(writes, e => Assert.Equal(AgentActivityKind.Write, e.Kind));
    }

    [Fact]
    public async Task AndTheAnswerCarriesTheProjectBackSoAnAgentSeesTheResult()
    {
        (_, Bench bench, HttpClient client) = Serve();
        bench.Brief = "# The E28";

        HttpResponseMessage answer = await client.PostAsync(
            "/project/fix", Body(new { title = "Lean", state = "applied" }));

        using JsonDocument got = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());

        Assert.Contains("The E28", got.RootElement.GetProperty("brief").GetString()!,
                        StringComparison.Ordinal);
    }

    // ----- composition ---------------------------------------------------------

    [Fact]
    public async Task TuneFullCarriesEverySettingWithItsMetadataAndEveryTable()
    {
        // The point of this endpoint is that a session never has to make the
        // /tune plus /tables plus one /table-per-name round trips itself.
        (_, _, HttpClient client) = Serve();

        using JsonDocument full = JsonDocument.Parse(await client.GetStringAsync("/tune/full"));

        JsonElement settings = full.RootElement.GetProperty("settings");
        JsonElement cranking = settings[0];
        Assert.Equal("crankingRPM", cranking.GetProperty("name").GetString());
        Assert.Equal(300, cranking.GetProperty("value").GetDouble());
        Assert.Equal("rpm", cranking.GetProperty("units").GetString());
        Assert.Equal(0, cranking.GetProperty("low").GetDouble());
        Assert.Equal(1000, cranking.GetProperty("high").GetDouble());

        JsonElement egoType = settings[1];
        Assert.Equal("Wide Band", egoType.GetProperty("options")[2].GetString());

        JsonElement tables = full.RootElement.GetProperty("tables");
        Assert.Equal("VE Table", tables[0].GetProperty("name").GetString());
        Assert.Equal(800, tables[0].GetProperty("xBins")[0].GetDouble());
        Assert.Equal(40, tables[0].GetProperty("values")[0][0].GetDouble());
    }

    [Fact]
    public async Task LogFullReturnsEveryChannelSharingOneTimeColumn()
    {
        (_, _, HttpClient client) = Serve();

        using JsonDocument full = JsonDocument.Parse(await client.GetStringAsync("/log/full"));

        JsonElement times = full.RootElement.GetProperty("times");
        JsonElement channels = full.RootElement.GetProperty("channels");

        Assert.Equal(5, times.GetArrayLength());
        Assert.Equal(2, channels.GetArrayLength());
        Assert.Equal("RPM", channels[0].GetProperty("name").GetString());
        Assert.Equal(5, channels[0].GetProperty("values").GetArrayLength());
    }

    [Fact]
    public async Task LogFullRespectsTheSecondsWindow()
    {
        (_, _, HttpClient client) = Serve();

        // Only the last 0.2 s of a 0.4 s log — the last three samples.
        using JsonDocument full =
            JsonDocument.Parse(await client.GetStringAsync("/log/full?seconds=0.2"));

        JsonElement channels = full.RootElement.GetProperty("channels");
        Assert.Equal(3, full.RootElement.GetProperty("times").GetArrayLength());
        Assert.Equal(3, channels[0].GetProperty("values").GetArrayLength());
        Assert.Equal(3000, channels[0].GetProperty("values")[0].GetDouble());
    }

    [Fact]
    public async Task LogFullThinsBySampleStrideWhenDecimateIsGiven()
    {
        (_, _, HttpClient client) = Serve();

        // Five samples, every other one kept: indices 0, 2, 4.
        using JsonDocument full =
            JsonDocument.Parse(await client.GetStringAsync("/log/full?decimate=2"));

        JsonElement rpm = full.RootElement.GetProperty("channels")[0].GetProperty("values");
        Assert.Equal(3, rpm.GetArrayLength());
        Assert.Equal(800, rpm[0].GetDouble());
        Assert.Equal(3000, rpm[1].GetDouble());
        Assert.Equal(5000, rpm[2].GetDouble());
    }

    [Fact]
    public async Task ContextBundlesTheProjectStateTuneSummaryInsightsAndWireHealthInOneCall()
    {
        // The "read this first" call — a summary of the tune, not the full dump
        // /tune/full gives, so a fresh session is not made to pay for both.
        (_, Bench bench, HttpClient client) = Serve();
        bench.Brief = "# The E28\n\n## Still open\n";

        using JsonDocument context = JsonDocument.Parse(await client.GetStringAsync("/context"));

        Assert.Contains("The E28", context.RootElement.GetProperty("projectBrief").GetString()!,
                        StringComparison.Ordinal);
        Assert.Equal("live", context.RootElement.GetProperty("state").GetProperty("mode").GetString());

        JsonElement tune = context.RootElement.GetProperty("tune");
        Assert.Equal(2, tune.GetProperty("settingCount").GetInt32());
        Assert.Equal("VE Table", tune.GetProperty("tables")[0].GetString());

        Assert.Equal(1, context.RootElement.GetProperty("insights").GetArrayLength());
        Assert.Equal(10, context.RootElement.GetProperty("wireHealth").GetProperty("sampled").GetInt32());
    }
}
