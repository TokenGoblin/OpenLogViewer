using System.Net.Sockets;
using System.Text;
using System.Net.WebSockets;
using OpenLogViewer.Core;

namespace OpenLogViewer.Oracle;

/// <summary>
/// What the shipping <see cref="AgentServer"/> answers, byte for byte, over a fixed bridge.
///
/// The Rust server is checked against these: every case is a raw HTTP request and the
/// status, content type and body the C# gave back. The bridge here (<see cref="Bench"/>) is
/// the one the C# tests use with its clocks fixed, so the answers are deterministic; the Rust
/// test builds the same bridge from the same data. What is compared is the whole body text,
/// which catches a field renamed, a number printed differently, a character escaped
/// differently, an omitted null or a changed status as well as a changed value.
/// </summary>
public sealed class W5Agent : IEmitter
{
    public string Package => "w5-agent";

    public string Set => "wire";

    internal const string Token = "test-token-0123456789";

    internal static readonly DateTime Fixed = new(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

    internal sealed class Bench : IAgentBridge
    {
        public bool Armed { get; set; }

        /// <summary>The awkward data: characters that need escaping, numbers that need exact printing.</summary>
        public bool Rich { get; set; }

        /// <summary>A second, newer wire event ahead of the first.</summary>
        public bool ExtraEvent { get; set; }

        public string Brief { get; set; } = "";

        public AgentDefinitionNeed? Needed { get; set; }

        public List<string> Calls { get; } = [];

        public AgentState State() => new()
        {
            Mode = "live", Signature = "TEST Format 0001.00", Samples = 3, Seconds = 0.2, Rate = 25,
            Channels = 2, HasTune = true, WritesArmed = Armed,
        };

        public AgentLimits Limits() => new()
        {
            WriteRateCount = 10, WriteRateWindowSeconds = 5, MaxChangeFractionOfRange = 0.5,
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

        public IReadOnlyList<AgentWireEvent> WireEvents(int count)
        {
            Calls.Add($"events {count}");
            var all = new List<AgentWireEvent>();
            if (ExtraEvent) all.Add(new(2, Fixed, "write page 3", "Agent", 0, 5.5, "Ok", "None", ""));
            all.Add(Rich
                ? new(1, Fixed.AddSeconds(-1), "read realtime", "Human", 1, 3.1, "Failed", "Timeout", "no \"reply\" <in> time & more")
                : new(1, Fixed, "read realtime", "Human", 0, 3.1, "Ok", "None", ""));
            return [.. all.Take(count)];
        }

        public IReadOnlyList<double> Values(string channel, double seconds)
        {
            Calls.Add($"values {channel} {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return channel == "RPM" ? [800, 3000, Rich ? 5000.5 : 5000] : channel == "Lua: torque" ? [double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1e-7, 1e21, 123456789.125, -0.0] : [14.7, 13.2, 12.6];
        }

        public IReadOnlyList<double> Times(double seconds) => [0, 0.1, 0.2];

        public IReadOnlyList<AgentFinding> Insights() => Rich
            ?
            [
                new AgentFinding("Good", "Fuelling", "Mixture tracks target", "Within 2 %.") { Evidence = "n = 3; mean 14.2 \u00b1 0.3 \u2014 it\u0027s <fine> & \"good\" + more `x`" },
                new AgentFinding("Warning", "Knock", "No evidence", "none"),
            ]
            : [new AgentFinding("Good", "Fuelling", "Mixture tracks target", "Within 2 %.")];

        public IReadOnlyDictionary<string, double> TuneValues() => Rich
            ? new Dictionary<string, double> { ["crankingRPM"] = 300, ["revLimit"] = 6500, ["ratio"] = 0.1 + 0.2, ["big"] = 1e15, ["bad"] = double.NaN }
            : new Dictionary<string, double> { ["crankingRPM"] = 300, ["revLimit"] = 6500 };

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

        public string LastRationale { get; private set; } = "";

        public AgentRefusal? SetSetting(string name, double value, string rationale, bool confirmDangerous = false)
        {
            LastRationale = rationale;
            Calls.Add($"set {name} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)} [{rationale}] {confirmDangerous}");
            if (!Armed) return new AgentRefusal("writes are not armed", "Tick it in the application.");
            return null;
        }

        public AgentCellWrite SetTableCell(
            string table, int column, int row, double value, string rationale, bool confirmDangerous = false)
        {
            Calls.Add($"cell {table} {column} {row} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)} [{rationale}] {confirmDangerous}");
            if (!Armed) return new AgentCellWrite(new AgentRefusal("writes are not armed"), 0);
            double landed = Math.Clamp(value, 0, 100);
            return new AgentCellWrite(null, landed, table == "warn" ? "Warning: shares bytes" : null);
        }

        public string ProjectBrief() => Brief;

        public IReadOnlyList<string> Projects() => ["The E28"];

        public AgentRefusal? KeepTune(string note)
        {
            Calls.Add($"keep [{note}]");
            return Brief.Length == 0 ? new AgentRefusal("no project is open") : null;
        }

        public string CompareVersions(string from, string to)
        {
            Calls.Add($"compare [{from}] [{to}]");
            return Brief.Length == 0 ? "No project is open." : $"{from} to {to}: 2 settings differ.";
        }

        public AgentRefusal? RecordSitting(string note)
        {
            Calls.Add($"record [{note}]");
            return Brief.Length == 0 ? new AgentRefusal("no project is open") : null;
        }

        public AgentRefusal? NoteFix(string id, string title, string detail, string state, string change)
        {
            Calls.Add($"fix [{id}] [{title}] [{detail}] [{state}] [{change}]");
            return Brief.Length == 0 ? new AgentRefusal("no project is open") : null;
        }

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
            Calls.Add($"logfull {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} {decimate}");
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

        private static TuneConstant RevLimit => new()
        {
            Name = "revLimit", Page = 0, Offset = 2, Type = RealtimeType.U16, Units = "rpm",
        };

        public TuneProposalResult ProposeTune(
            IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells)
        {
            Calls.Add("propose " + string.Join(",", settings.Select(s => $"{s.Name}={s.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))
                      + " | " + string.Join(",", cells.Select(c => $"{c.Table}[{c.Column},{c.Row}]={c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")));

            return new TuneProposalResult(
                [
                    new TuneDifference("revLimit", RevLimit, 1, 7000, 6500),
                    new TuneDifference("name", RevLimit, 1, null, null) { MineText = "new \"name\"", TheirsText = "old" },
                    new TuneDifference("VE Table", RevLimit, 4, 61.5, null),
                ],
                ["foo: no such setting"], "3 settings would change.");
        }

        public TuneApplyResult ApplyTune(
            IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string note,
            bool confirmDangerous = false)
        {
            Calls.Add($"apply {settings.Count} {cells.Count} [{note}] {confirmDangerous}");

            if (!Armed) return new TuneApplyResult(new AgentRefusal("writes are not armed", "Tick it."), [], [], "");

            return new TuneApplyResult(
                null, [new TuneDifference("revLimit", RevLimit, 1, 7000, 6500)], ["x: too large"], "Applied 1 change.");
        }

        public AgentStageResult StageTune(
            IReadOnlyList<ProposedSetting> settings, IReadOnlyList<ProposedCell> cells, string filename)
        {
            Calls.Add($"stagetune [{filename}]");
            return filename == "bad"
                ? new AgentStageResult(new AgentRefusal("could not stage that file", "no"), null)
                : new AgentStageResult(null, new AgentStagedFile("tune-test.msq", "C:\\staged\\tune-test.msq", 42, Fixed));
        }

        public AgentStageResult StageTable(string name, string filename)
        {
            Calls.Add($"stagetable [{name}] [{filename}]");
            return name == "VE Table"
                ? new AgentStageResult(null, new AgentStagedFile("VE-Table-test.csv", "C:\\staged\\VE-Table-test.csv", 24, Fixed))
                : name == "locked"
                    ? new AgentStageResult(new AgentRefusal("could not write the staged file", "denied"), null)
                    : new AgentStageResult(new AgentRefusal("no such table", name), null);
        }

        public IReadOnlyList<AgentStagedFile> ListStaged() =>
            [new AgentStagedFile("tune-1.msq", "C:\\staged\\tune-1.msq", 100, Fixed)];

        public AgentDefinitionNeed? DefinitionNeeded() => Needed;

        public AgentDefinitionImported ImportDefinition(string path, string content, string source, string name)
        {
            Calls.Add($"import [{path}] [{content}] [{source}] [{name}]");
            return path == "wrong"
                ? new AgentDefinitionImported("it declares \"speeduino 202402\"", "", "", "", "")
                : new AgentDefinitionImported("", @"C:\defs\speeduino202501.ini", "speeduino202501.ini", "speeduino 202501", source);
        }
    }

    private sealed record Case(string Name, string[] Setup, string Method, string Target, string[][] Headers, string Body);

    public IReadOnlyList<object> Emit(EmitContext context)
    {
        string[][] auth = [["Authorization", "Bearer " + Token]];
        var cases = new List<Case>();

        void Get(string name, string target, params string[] setup) =>
            cases.Add(new Case(name, setup, "GET", target, auth, ""));

        void Post(string name, string target, string body, params string[] setup) =>
            cases.Add(new Case(name, setup, "POST", target, auth, body));

        Get("root", "/");
        Get("state", "/state");
        Get("state armed", "/state", "armed");
        Get("state trailing slash", "/state/");
        Get("limits", "/limits");
        Get("channels", "/channels");
        Get("channels raw true", "/channels?raw=true");
        Get("channels raw bare", "/channels?raw");
        Get("channels raw empty", "/channels?raw=");
        Get("channels raw 1", "/channels?raw=1");
        Get("channels raw YES", "/channels?raw=YES");
        Get("channels raw no", "/channels?raw=no");
        Get("wire health", "/wire/health");
        Get("wire events", "/wire/events", "rich", "events2");
        Get("wire events 1", "/wire/events?count=1", "rich", "events2");
        Get("wire events negative", "/wire/events?count=-5", "events2");
        Get("wire events huge", "/wire/events?count=9999", "rich", "events2");
        Get("wire events junk", "/wire/events?count=abc", "events2");
        Get("values", "/values?channel=RPM&seconds=5", "rich");
        Get("values no seconds", "/values?channel=AFR");
        Get("values missing", "/values");
        Get("values blank", "/values?channel=%20");
        Get("values negative seconds", "/values?channel=RPM&seconds=-3", "rich");
        Get("values exponent seconds", "/values?channel=RPM&seconds=1e1", "rich");
        Get("values junk seconds", "/values?channel=RPM&seconds=abc");
        Get("values non finite", "/values?channel=Lua%3A%20torque", "rich");
        Get("values plus is space", "/values?channel=Lua:+torque", "rich");
        Get("values repeated key", "/values?channel=RPM&channel=AFR");
        Get("insights", "/insights", "rich");
        Get("tune", "/tune", "rich");
        Get("tables", "/tables");
        Get("table", "/table?name=VE%20Table");
        Get("table missing", "/table");
        Get("table unknown", "/table?name=Nope");
        Get("project closed", "/project");
        Get("project open", "/project", "brief");
        Get("tune full", "/tune/full");
        Get("log full", "/log/full");
        Get("log full window", "/log/full?seconds=0.2&decimate=2");
        Get("log full bad decimate", "/log/full?decimate=0");
        Get("context", "/context", "brief");
        Get("stage list", "/stage");
        Get("definitions none", "/definitions/needed");
        Get("definitions needed", "/definitions/needed", "needed");
        Get("no such endpoint", "/nope");
        Get("no such endpoint nested", "/tune/burn");
        Get("burn is not an endpoint", "/burn");
        Get("case matters", "/State");
        Get("get on a post endpoint", "/tune/set");
        Get("stage table get", "/stage/table?name=VE%20Table");

        Post("set refused", "/tune/set", """{"name":"revLimit","value":7000}""");
        Post("set armed", "/tune/set", """{"name":"revLimit","value":7000,"rationale":"for \"dyno\" <pull>","confirmDangerous":true}""", "armed");
        Post("set armed minimal", "/tune/set", """{"name":"revLimit","value":7000.5}""", "armed");
        Post("set no name", "/tune/set", """{"value":1}""", "armed");
        Post("set string value", "/tune/set", """{"name":"x","value":"7"}""", "armed");
        Post("set nan value", "/tune/set", """{"name":"x","value":"NaN"}""", "armed");
        Post("set null value", "/tune/set", """{"name":"x","value":null}""", "armed");
        Post("set number name", "/tune/set", """{"name":5,"value":1}""", "armed");
        Post("set unknown props", "/tune/set", """{"name":"x","value":1,"extra":[1,2,{"a":null}]}""", "armed");
        Post("set pascal case props", "/tune/set", """{"Name":"x","Value":1}""", "armed");
        Post("set duplicate props", "/tune/set", """{"name":"a","name":"b","value":1,"value":2}""", "armed");
        Post("set empty body", "/tune/set", "");
        Post("set null body", "/tune/set", "null");
        Post("set array body", "/tune/set", "[]");
        Post("set number body", "/tune/set", "5");
        Post("set malformed", "/tune/set", """{"name":""");
        Post("set trailing comma", "/tune/set", """{"name":"x","value":1,}""", "armed");
        Post("set comment", "/tune/set", """{"name":"x", /* c */ "value":1}""", "armed");
        Post("set big integer value", "/tune/set", """{"name":"x","value":123456789012345678901234567890}""", "armed");
        Post("set confirm as string", "/tune/set", """{"name":"x","value":1,"confirmDangerous":"true"}""", "armed");
        Post("json open brace", "/tune/set", "{", "armed");
        Post("json key only", "/tune/set", "{\"name\"", "armed");
        Post("json after colon", "/tune/set", "{\"name\":", "armed");
        Post("json open string value", "/tune/set", "{\"name\":\"x\"", "armed");
        Post("json open string", "/tune/set", "{\"name\":\"x", "armed");
        Post("json invalid value", "/tune/set", "{\"name\":x}", "armed");
        Post("json single quotes", "/tune/set", "{'name':1}", "armed");
        Post("json trailing garbage", "/tune/set", "{} x", "armed");
        Post("json extra close", "/tune/set", "{\"name\":\"x\"}}", "armed");
        Post("json open array value", "/tune/set", "{\"name\":[1,2", "armed");
        Post("json truncated literal", "/tune/set", "{\"name\":nul", "armed");
        Post("json bad literal", "/tune/set", "{\"name\":nulx}", "armed");
        Post("json bad escape", "/tune/set", "{\"name\":\"q\"}", "armed");
        Post("json short unicode escape", "/tune/set", "{\"name\":\"a\\u00\"}", "armed");
        Post("json leading zero", "/tune/set", "{\"name\":\"x\",\"value\":01}", "armed");
        Post("json trailing dot", "/tune/set", "{\"name\":\"x\",\"value\":1.}", "armed");
        Post("json lone minus", "/tune/set", "{\"name\":\"x\",\"value\":-}", "armed");
        Post("json plus number", "/tune/set", "{\"name\":\"x\",\"value\":+1}", "armed");
        Post("json bom", "/tune/set", "\ufeff{\"name\":\"x\",\"value\":1}", "armed");
        Post("json multiline type error", "/tune/set", "{\u000a  \"name\": 5,\u000a  \"value\": 1\u000a}", "armed");
        Post("json multiline syntax error", "/tune/set", "{\u000a  \"name\": \"x\",\u000a  \"value\": ?\u000a}", "armed");
        Post("json missing comma", "/tune/set", "{\"name\":\"x\" \"value\":1}", "armed");
        Post("json unquoted key", "/tune/set", "{name:1}", "armed");
        Post("json nested type error", "/tune/set", "{\"name\":\"x\",\"value\":1,\"rationale\":{\"a\":1}}", "armed");
        Post("json value overflow", "/tune/set", "{\"name\":\"x\",\"value\":1e999}", "armed");
        Post("json value is object", "/tune/set", "{\"name\":\"x\",\"value\":{}}", "armed");
        Post("json value is array", "/tune/set", "{\"name\":\"x\",\"value\":[1]}", "armed");
        Post("json value true", "/tune/set", "{\"name\":\"x\",\"value\":true}", "armed");
        Post("json control char in string", "/tune/set", "{\"name\":\"a\u0009b\"}", "armed");
        Post("json infinity literal", "/tune/set", "{\"name\":\"x\",\"value\":\"-Infinity\"}", "armed");
        Post("json infinity lowercase", "/tune/set", "{\"name\":\"x\",\"value\":\"infinity\"}", "armed");
        Post("json nan bare", "/tune/set", "{\"name\":\"x\",\"value\":NaN}", "armed");
        Post("json deep", "/tune/set", "[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[", "armed");
        Post("json deep ok object", "/tune/set", "{\"name\":\"x\",\"value\":1,\"z\":[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]}", "armed");
        Post("cell refused", "/table/set", """{"table":"VE Table","column":0,"row":0,"value":55}""");
        Post("cell armed", "/table/set", """{"table":"VE Table","column":0,"row":0,"value":55,"rationale":"smoothing a flat spot"}""", "armed");
        Post("cell clamped", "/table/set", """{"table":"VE Table","column":1,"row":1,"value":999,"rationale":"clamp"}""", "armed");
        Post("cell warning", "/table/set", """{"table":"warn","column":1,"row":1,"value":5,"rationale":"w"}""", "armed");
        Post("cell fractional column", "/table/set", """{"table":"VE Table","column":1.5,"row":1,"value":5}""", "armed");
        Post("cell exponent column", "/table/set", """{"table":"VE Table","column":1e0,"row":1,"value":5}""", "armed");
        Post("cell huge column", "/table/set", """{"table":"VE Table","column":99999999999,"row":1,"value":5}""", "armed");
        Post("burn paths", "/tune/burn", """{"page":0}""", "armed");
        Post("keep without project", "/project/keep", """{"note":"n"}""");
        Post("keep", "/project/keep", """{"note":"n"}""", "brief");
        Post("record without project", "/project/record", """{"note":"n"}""");
        Post("record", "/project/record", """{"note":"after the VE change"}""", "brief");
        Post("fix", "/project/fix", """{"title":"Lean above 150 kPa","detail":"46% short","state":"open"}""", "brief");
        Post("fix without project", "/project/fix", """{"title":"x"}""");
        Post("compare", "/project/versions/compare", """{"from":"v1","to":"v2"}""", "brief");
        Post("compare no project", "/project/versions/compare", """{"from":"v1"}""");
        Post("compare get", "/project/versions/compare", "", "brief");
        Post("propose", "/tune/propose", """{"settings":[{"name":"revLimit","value":7000}],"cells":[{"table":"VE Table","column":1,"row":0,"value":3}]}""");
        Post("propose empty body", "/tune/propose", "");
        Post("propose null body", "/tune/propose", "null");
        Post("propose whitespace body", "/tune/propose", "  \r\n ");
        Post("propose empty object", "/tune/propose", "{}");
        Post("propose settings wrong type", "/tune/propose", """{"settings":5}""");
        Post("propose setting null entry", "/tune/propose", """{"settings":[null]}""");
        Post("propose malformed", "/tune/propose", "{");
        Post("apply refused", "/tune/apply", """{"settings":[{"name":"revLimit","value":7000}]}""");
        Post("apply", "/tune/apply", """{"settings":[{"name":"revLimit","value":7000}],"note":"dyno","confirmDangerous":true}""", "armed");
        Post("stage tune", "/stage/tune", "{}");
        Post("stage tune named", "/stage/tune", """{"filename":"mine.msq"}""");
        Post("stage tune refused", "/stage/tune", """{"filename":"bad"}""");
        Post("stage table", "/stage/table?name=VE%20Table", """{"filename":"x.csv"}""");
        Post("stage table no body", "/stage/table?name=VE%20Table", "");
        Post("stage table unknown", "/stage/table?name=Nope", "{}");
        Post("stage table failing", "/stage/table?name=locked", "{}");
        Post("stage table no name", "/stage/table", "{}");
        Post("stage table blank name", "/stage/table?name=%20", "{}");
        Post("import", "/definitions/import", """{"path":"C:\\downloads\\speeduino.ini","source":"https://example.invalid/x.ini"}""");
        Post("import refused", "/definitions/import", """{"path":"wrong"}""");
        Post("import empty body", "/definitions/import", "");
        Post("post to a get endpoint", "/state", "{}");

        // Authentication, which comes before routing.
        cases.Add(new Case("no token", [], "GET", "/state", [], ""));
        cases.Add(new Case("no token unknown path", [], "GET", "/nope", [], ""));
        cases.Add(new Case("wrong token", [], "GET", "/state", [["Authorization", "Bearer wrong-token-0123456789"]], ""));
        cases.Add(new Case("shorter token", [], "GET", "/state", [["Authorization", "Bearer x"]], ""));
        cases.Add(new Case("basic scheme", [], "GET", "/state", [["Authorization", "Basic " + Token]], ""));
        cases.Add(new Case("lowercase bearer", [], "GET", "/state", [["Authorization", "bearer " + Token]], ""));
        cases.Add(new Case("bearer with spaces", [], "GET", "/state", [["Authorization", "Bearer   " + Token + "  "]], ""));
        cases.Add(new Case("bare token header", [], "GET", "/state", [["Authorization", Token]], ""));
        cases.Add(new Case("token in query", [], "GET", "/state?token=" + Token, [], ""));
        cases.Add(new Case("wrong token in query", [], "GET", "/state?token=nope", [], ""));
        cases.Add(new Case("bad header good query", [], "GET", "/state?token=" + Token, [["Authorization", "Bearer nope"]], ""));
        cases.Add(new Case("good header bad query", [], "GET", "/state?token=nope", auth, ""));
        cases.Add(new Case("unauthorised post", [], "POST", "/tune/set", [], """{"name":"a","value":1}"""));
        cases.Add(new Case("host localhost", [], "GET", "/state", [["Host", "localhost:{port}"], .. auth], ""));
        cases.Add(new Case("host other", [], "GET", "/state", [["Host", "evil.example:{port}"], .. auth], ""));
        cases.Add(new Case("host loopback other port", [], "GET", "/state", [["Host", "127.0.0.1:1"], .. auth], ""));
        cases.Add(new Case("origin foreign", [], "GET", "/state", [["Origin", "http://evil.example"], .. auth], ""));

        var results = new List<object>();

        foreach (Case c in cases)
        {
            var bench = new Bench();
            if (c.Setup.Contains("armed")) bench.Armed = true;
            if (c.Setup.Contains("rich")) bench.Rich = true;
            if (c.Setup.Contains("events2")) bench.ExtraEvent = true;
            if (c.Setup.Contains("brief")) bench.Brief = "# The E28\n\n## Still open\n\n### lean-under-load \u2014 Lean above 150 kPa\n";
            if (c.Setup.Contains("needed"))
            {
                bench.Needed = new AgentDefinitionNeed(
                    ["Speeduino 2025.01.7", "speeduino 202501"],
                    "Speeduino", "speeduino 202501", "Speeduino 2025.01.7", "speeduino202501.ini",
                    [new AgentNearMiss("speeduino202402.ini", "speeduino 202402", @"C:\defs\speeduino202402.ini")],
                    @"C:\defs", "somewhere",
                    ["https://raw.githubusercontent.com/speeduino/speeduino/202501.7/reference/speeduino.ini"],
                    "fetch it");
            }

            using var server = new AgentServer(bench, new AgentServerSettings { Port = 0, Token = Token });
            server.Start();

            (int status, string contentType, string body) = Raw(server.Port, c);

            results.Add(new
            {
                name = c.Name,
                setup = c.Setup,
                method = c.Method,
                target = c.Target,
                headers = c.Headers,
                body = c.Body,
                status,
                content_type = contentType,
                response = body,
                calls = bench.Calls.ToArray(),
            });
        }

        results.Add(Stream());
        results.AddRange(Strings());

        return results;
    }

    /// <summary>A request on a raw socket, so the Host header and the rest are exactly what is asked for.</summary>
    private static (int Status, string ContentType, string Body) Raw(int port, Case c)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = 10_000;

        byte[] body = Encoding.UTF8.GetBytes(c.Body);
        var request = new StringBuilder();
        request.Append($"{c.Method} {c.Target} HTTP/1.1\r\n");

        if (!c.Headers.Any(h => h[0] == "Host")) request.Append($"Host: 127.0.0.1:{port}\r\n");

        foreach (string[] h in c.Headers) request.Append($"{h[0]}: {h[1].Replace("{port}", port.ToString())}\r\n");

        if (c.Method == "POST") request.Append($"Content-Length: {body.Length}\r\n");
        request.Append("Connection: close\r\n\r\n");

        NetworkStream stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes(request.ToString()));
        stream.Write(body);

        using var all = new MemoryStream();
        stream.CopyTo(all);
        string text = Encoding.UTF8.GetString(all.ToArray());

        int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = split < 0 ? text : text[..split];
        string content = split < 0 ? "" : text[(split + 4)..];

        string[] lines = head.Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1]);
        string type = lines.Skip(1)
            .Where(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[13..].Trim()).FirstOrDefault() ?? "";

        // Chunked answers are not expected (every reply sets a length); a short read says so.
        return (status, type, content);
    }

    /// <summary>One live frame with the awkward numbers, as the socket sends it.</summary>
    private static object Stream()
    {
        var bench = new Bench();
        using var server = new AgentServer(bench, new AgentServerSettings { Port = 0, Token = Token });
        server.Start();

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/live/stream"), CancellationToken.None).Wait();

        for (int i = 0; i < 500 && server.Subscribers == 0; i++) Thread.Sleep(10);

        server.Publish(1.5, ["RPM", "Lua: torque", "Neg", "Tiny", "Big"], [3000, double.NaN, double.NegativeInfinity, 1e-7, double.PositiveInfinity]);

        var messages = new List<string>();
        var buffer = new byte[64 * 1024];

        for (int i = 0; i < 2; i++)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            WebSocketReceiveResult got = socket.ReceiveAsync(buffer, cancel.Token).Result;
            messages.Add(Encoding.UTF8.GetString(buffer, 0, got.Count));
        }

        // A request that is not the JSON it expects is answered, not dropped.
        socket.SendAsync(Encoding.UTF8.GetBytes("not json"), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
        using var cancel2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WebSocketReceiveResult reply = socket.ReceiveAsync(buffer, cancel2.Token).Result;
        messages.Add(Encoding.UTF8.GetString(buffer, 0, reply.Count));

        return new { name = "stream", setup = Array.Empty<string>(), method = "WS", target = "/live/stream", messages = messages.ToArray() };
    }

    /// <summary>How the serialiser spells strings and numbers, through a bridge that returns them.</summary>
    private static IEnumerable<object> Strings() => [];
}
