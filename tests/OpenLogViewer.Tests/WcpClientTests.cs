using System.Text;
using System.Text.Json;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The client, over the real link, against the fake firmware: detection, the
/// retry rules, every command, the events, the keep-alive — and an ELM327 session
/// sharing the same port the whole time.
/// </summary>
public class WcpClientTests : IDisposable
{
    private readonly FakeWcpDevice _device;
    private readonly WcpLink _link;
    private readonly WcpClient _client;

    public WcpClientTests() : this(manualClock: true)
    {
    }

    private WcpClientTests(bool manualClock)
    {
        _device = new FakeWcpDevice(Car(), manualClock);
        _link = new WcpLink(_device);
        _link.Open();
        _client = new WcpClient(_link) { Timeout = TimeSpan.FromMilliseconds(400) };
    }

    public void Dispose()
    {
        _client.Dispose();
        _link.Dispose();
    }

    internal static FakeElm Car() => FakeWcpDevice.Car();

    private static bool Eventually(Func<bool> condition, int ms = 2000)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(ms);

        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    // ----- detection -----------------------------------------------------------

    [Fact]
    public void TheCustomFirmwareIsDetected()
    {
        using WcpClient? client = WcpClient.Detect(_link);

        Assert.NotNull(client);
        Assert.Equal(1, client.Identity!.Protocol);
        Assert.Equal("v4.50p+wcp1", client.Identity.Firmware);
        Assert.True(client.Identity.Can("can-tx"));
        Assert.Equal("STN2120 v5.8.1", client.Identity.Obd);
    }

    [Fact]
    public void StockFirmwareIsRecognisedQuicklyByItsEcho()
    {
        // A stock unit hands the line to the ELM327 chip, which echoes it and
        // answers whatever it makes of it. That echo is the answer: no waiting
        // out a timeout on every connection to a stock unit.
        _device.Stock = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(WcpClient.Detect(_link, TimeSpan.FromSeconds(3)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"{clock.Elapsed}");
        Assert.Contains(_device.ElmLines, l => l.StartsWith("#1 hello", StringComparison.Ordinal));
    }

    [Fact]
    public void SilenceIsStockToo()
    {
        var silent = new SilentPort();

        Assert.Null(WcpClient.Detect(silent, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(2, silent.Sent.Count);
    }

    private sealed class SilentPort : IWcpPort
    {
        public List<string> Sent { get; } = [];

        public bool IsOpen => true;

        public void SendLine(string line) => Sent.Add(line);

        public event Action<string>? LineReceived { add { } remove { } }

        public event Action? Closed { add { } remove { } }
    }

    // ----- retries ---------------------------------------------------------------

    [Fact]
    public void ACrcRefusalIsRetriedOnceWithANewSeq()
    {
        _device.RejectWithCrc = 1;

        Assert.True(_client.Ping() >= 0);
        Assert.Equal(1, _client.Retries);
        Assert.Equal(["ping"], _device.Commands);
    }

    [Fact]
    public void TwoCrcRefusalsAreAnError()
    {
        _device.RejectWithCrc = 2;

        WcpException e = Assert.Throws<WcpException>(() => _client.Ping());
        Assert.Equal("CRC", e.Code);
    }

    [Fact]
    public void ALostReplyToAnIdempotentRequestIsRetried()
    {
        _device.LoseReplies = 1;

        Assert.Equal("0", _client.Status()["armed"]);
        Assert.Equal(["status", "status"], _device.Commands);
    }

    [Fact]
    public void ALostReplyToASendIsNeverRetried()
    {
        // The frame went out; only the "OK sent" was lost. A retry here would put
        // the frame on a moving car's bus twice.
        _client.CanStart();
        _client.CanArm();
        _device.LoseReplies = 1;

        Assert.Throws<TimeoutException>(() => _client.CanSend(0x7E0, [0x02, 0x01, 0x0C]));
        Assert.Single(_device.Transmitted);
    }

    [Fact]
    public void ALostReplyToALuaRunIsNotRetried()
    {
        _device.LuaScripts["a"] = "x"u8.ToArray();
        _device.LoseReplies = 1;

        Assert.Throws<TimeoutException>(() => _client.LuaRun("a"));
        Assert.Single(_device.Commands, "lua run a");
    }

    [Fact]
    public void ADamagedDataLineMeansTheWholeReplyIsAskedForAgain()
    {
        // Nothing in a reply counts its data lines, so a damaged one would
        // otherwise be a setting silently missing from the list.
        _device.DamageDataLines = 1;

        WcpValues config = _client.ConfigGet();

        Assert.Equal(FakeWcpDevice.DefaultConfig.Count, config.Pairs.Count);
        Assert.Equal(1, _client.Retries);
        Assert.Equal(1, _client.DamagedLines);
    }

    [Fact]
    public void ALateReplyIsNotTakenForTheNextOne()
    {
        // A reply for a seq nobody is waiting on, arriving first.
        _device.BetweenElmLines.Add("#999.OK stale");
        _link.Write("ATI\r"u8);

        Assert.True(Eventually(() => _client.StaleLines == 1));
        Assert.True(_client.Ping() >= 0);
    }

    // ----- session and settings ---------------------------------------------------

    [Fact]
    public void StatusIsKeyValuesInOrder()
    {
        WcpValues status = _client.Status();

        Assert.Equal("uptime_ms", status.Pairs[0].Key);
        Assert.Equal("12.6", status["volt"]);
        Assert.Equal("off", status["stream"]);
    }

    [Fact]
    public void PasswordsArriveMasked()
    {
        WcpValues config = _client.ConfigGet();

        Assert.Equal("****", config["sta_pass"]);
        Assert.Equal("****", _client.ConfigGet("ap_pass"));
        Assert.Equal("elm327", _client.ConfigGet("protocol"));
    }

    [Fact]
    public void SecretsAreRefusedUntilUnlocked()
    {
        WcpException e = Assert.Throws<WcpException>(() => _client.ConfigSecrets());
        Assert.Equal("DENIED", e.Code);

        _client.ConfigUnlock();
        Assert.Equal("homesecret", _client.ConfigSecrets()["sta_pass"]);
    }

    [Fact]
    public void StageDiffAndSave()
    {
        _client.ConfigSet("sta_ssid", "My Home Net");
        _client.ConfigSet("sta_pass", "x y z");

        IReadOnlyList<WcpConfigChange> diff = _client.ConfigDiff();

        Assert.Contains(new WcpConfigChange("sta_ssid", "", "My Home Net"), diff);
        Assert.Contains(new WcpConfigChange("sta_pass", "****", "****"), diff);

        Assert.Equal("saved; reboot to apply", _client.ConfigSave());
        Assert.Equal("My Home Net", _device.Saved["sta_ssid"]);
    }

    [Fact]
    public void ASaveThatWouldFailValidationSaysWhichKeyAndWritesNothing()
    {
        _client.ConfigSet("wifi_mode", "APStation");

        WcpException e = Assert.Throws<WcpException>(() => _client.ConfigSave());

        Assert.Equal("ARGS", e.Code);
        Assert.StartsWith("sta_ssid:", e.Message, StringComparison.Ordinal);
        Assert.Equal("AP", _device.Saved["wifi_mode"]);
    }

    [Fact]
    public void AValueWithALineBreakIsRefusedBeforeItIsSent()
    {
        Assert.Throws<WcpException>(() => _client.ConfigSet("sta_ssid", "a\rATZ"));
        Assert.Empty(_device.Commands);
    }

    [Fact]
    public void ConsoleOutputComesBackEvenWhenItFails()
    {
        Assert.Equal(["12.6V"], _client.Console("system --voltage").Output);

        WcpConsoleResult failed = _client.Console("no --such");
        Assert.False(failed.Ok);
        Assert.Equal(["unknown command: no --such"], failed.Output);
        Assert.StartsWith("UNKNOWN", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AutopidJsonIsJoinedBackAcrossLines()
    {
        string json = _client.AutopidData();

        Assert.True(json.Length > Wcp.MaxDataText, "The fake should have split it.");
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(812, doc.RootElement.GetProperty("ENGINE_RPM").GetInt32());

        Assert.Equal("88", _client.AutopidGet("COOLANT_TEMP"));
        Assert.Equal("NOTFOUND", Assert.Throws<WcpException>(() => _client.AutopidGet("NOPE")).Code);
    }

    [Fact]
    public void AutopidPausesAndResumes()
    {
        _client.AutopidPause();
        Assert.Equal("paused", _client.Status()["autopid"]);

        _client.AutopidResume();
        Assert.True(_device.AutopidPaused is false);
    }

    [Fact]
    public void ARebootForgetsTheArming()
    {
        _client.CanArm();
        _client.Reboot();

        Assert.False(_client.IsArmed);
        Assert.Equal(1, _device.Reboots);
    }

    // ----- logs -----------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALogIsPulledWholeInChunks(bool perLine)
    {
        _device.Base64PerLine = perLine;
        byte[] file = new byte[10_000];
        new Random(7).NextBytes(file);
        _device.LogFiles["2026-11-22.db"] = file;

        IReadOnlyList<WcpLogFile> list = _client.LogList();
        Assert.Equal(new WcpLogFile("2026-11-22.db", 10_000, "1763812800"), list.Single());

        using var into = new MemoryStream();
        var seen = new List<long>();

        Assert.Equal(10_000, _client.LogDownload("2026-11-22.db", into, (n, _) => seen.Add(n)));
        Assert.Equal(file, into.ToArray());
        Assert.Equal([3072, 6144, 9216, 10_000], seen);
    }

    [Fact]
    public void ALostBase64LineIsCaughtByTheByteCountAndAskedAgain()
    {
        _device.LogFiles["a.db"] = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];
        _device.LoseDataLines = 1;

        WcpLogChunk chunk = _client.LogRead("a.db", 0, 1000);

        Assert.Equal(1000, chunk.Data.Length);
        Assert.Equal(1, _client.Retries);
    }

    [Theory]
    [InlineData("../config.json")]
    [InlineData("a b.db")]
    [InlineData("sub/a.db")]
    public void ALogNameThatCouldNotBeInTheFolderIsRefused(string name)
    {
        Assert.Throws<WcpException>(() => _client.LogRead(name, 0, 10));
        Assert.Empty(_device.Commands);
    }

    // ----- CAN stream -------------------------------------------------------------

    [Fact]
    public void FramesAndDropsArriveAsEvents()
    {
        var frames = new List<WcpCanEvent>();
        long dropped = 0;
        _client.CanFrame += frames.Add;
        _client.Dropped += n => dropped += n;

        _client.CanStart(500_000);
        _client.CanStreamOn();

        _device.BusFrame(0x7E8, false, 0x41, 0x0C, 0x1A, 0xF8);
        _device.BusFrame(0x18DAF110, true);

        _device.HoldStream = true;
        for (int i = 0; i < FakeWcpDevice.RingCapacity + 3; i++) _device.BusFrame(0x100, false, 1);
        _device.ReleaseStream();

        Assert.True(Eventually(() => dropped == 3));
        Assert.Equal(2 + FakeWcpDevice.RingCapacity, frames.Count);

        Assert.Equal(0x7E8u, frames[0].Id);
        Assert.Equal([0x41, 0x0C, 0x1A, 0xF8], frames[0].Data);
        Assert.True(frames[1].Extended);
        Assert.Empty(frames[1].Data);
        Assert.Equal(3, _client.DroppedFrames);
    }

    [Fact]
    public void AStreamFilterIsSentAsIdAndMask()
    {
        _client.CanStart();
        _client.CanStreamOn(0x7E8, 0x7F8);

        Assert.Equal("can stream on 7E8 7F8", _device.Commands[^1]);
    }

    [Theory]
    [InlineData("1000 7E8 s 2 410C", true)]
    [InlineData("1000 7E8 s 0", true)]
    [InlineData("1000 7E8 s 3 410C", false)]
    [InlineData("1000 800 s 1 01", false)]
    [InlineData("1000 7E8 q 1 01", false)]
    [InlineData("1000 7E8 s 9 0102030405060708FF", false)]
    public void ACanEventIsReadStrictly(string fields, bool good) =>
        Assert.Equal(good, WcpClient.TryParseCan(fields, out _));

    // ----- CAN transmit -------------------------------------------------------------

    [Fact]
    public void ASendBeforeArmingIsRefusedHereWithoutARoundTrip()
    {
        WcpException e = Assert.Throws<WcpException>(() => _client.CanSend(0x7E0, [1]));

        Assert.Equal("NOTARMED", e.Code);
        Assert.Empty(_device.Commands);
    }

    [Fact]
    public void ArmSendDisarm()
    {
        var disarmed = new List<string>();
        _client.Disarmed += disarmed.Add;

        _client.CanStart();
        _client.CanArm();
        Assert.True(_client.IsArmed);

        Assert.Equal("sent", _client.CanSend(0x18DAF110, [1, 2, 3], extended: true));
        Assert.Equal("can send 18DAF110 x 010203", _device.Commands[^1]);

        _client.CanDisarm();
        Assert.False(_client.IsArmed);
        Assert.True(Eventually(() => disarmed.Contains("host")));
        Assert.Contains("18DAF110 x 010203 wcp", _client.CanTxLog().Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x800u, false)]
    [InlineData(0x20000000u, true)]
    public void AnIdentifierTooWideForItsKindIsRefusedHere(uint id, bool extended)
    {
        _client.CanArm();

        Assert.Equal("HOST", Assert.Throws<WcpException>(() => _client.CanSend(id, [1], extended)).Code);
        Assert.Equal(["can arm"], _device.Commands);
    }

    [Fact]
    public void TheRateCapIsSurfacedAndNotRetried()
    {
        _client.CanStart();
        _client.CanArm();

        for (int i = 0; i < FakeWcpDevice.WcpTxRate; i++) _client.CanSend(0x100, [1]);

        WcpException e = Assert.Throws<WcpException>(() => _client.CanSend(0x100, [1]));
        Assert.True(e.IsRateLimited);
        Assert.Equal(FakeWcpDevice.WcpTxRate, _device.Transmitted.Count);
    }

    [Fact]
    public void ADeviceThatSaysNotArmedIsBelieved()
    {
        // The !DISARM was lost; the refusal is the next-best evidence.
        _client.CanStart();
        _client.CanArm();
        _device.Skip(TimeSpan.FromSeconds(11));

        WcpException e = Assert.Throws<WcpException>(() => _client.CanSend(0x7E0, [1]));
        Assert.Equal("NOTARMED", e.Code);
        Assert.False(_client.IsArmed);
    }

    [Fact]
    public void ArmedEventsFromTheDeviceAreFollowed()
    {
        _client.CanArm();
        Assert.True(Eventually(() => _client.IsArmed));

        _device.Advance(TimeSpan.FromSeconds(11));
        Assert.True(Eventually(() => !_client.IsArmed));
    }

    [Fact]
    public void ClosingTheLinkForgetsTheArming()
    {
        string? why = null;
        _client.Disarmed += r => why = r;
        _client.CanArm();

        _link.Close();

        Assert.False(_client.IsArmed);
        Assert.Equal("link", why);
    }

    // ----- Lua -------------------------------------------------------------------

    [Fact]
    public void AScriptGoesUpInChunksAndComesBackTheSame()
    {
        string source = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"print('line {i}') -- é"));
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        Assert.True(bytes.Length > 3 * WcpClient.MaxLuaChunk);

        Assert.Equal(bytes.Length, _client.LuaPut("speedo", bytes));

        Assert.Equal(bytes, _device.LuaScripts["speedo"]);
        Assert.Equal(bytes, _client.LuaGet("speedo"));
        Assert.Equal(new WcpLuaFile("speedo", bytes.Length), _client.LuaList().Single());
        Assert.All(_device.Commands, c => Assert.True(Wcp.Request(65535, c).Length <= Wcp.MaxRequestBytes));
    }

    [Theory]
    [InlineData("speedo.lua")]
    [InlineData("../x")]
    [InlineData("two words")]
    [InlineData("-")]
    public void AScriptNameThatIsAPathOrTwoWordsIsRefused(string name)
    {
        Assert.Throws<WcpException>(() => _client.LuaPut(name, [1]));
        Assert.Empty(_device.Commands);
    }

    [Fact]
    public void ACompileErrorIsArgsWithTheMessage()
    {
        _client.LuaPut("bad", "local x\n@@syntax"u8);

        WcpException e = Assert.Throws<WcpException>(() => _client.LuaRun("bad"));
        Assert.Equal("ARGS", e.Code);
        Assert.Equal("bad:2: syntax error near '@@'", e.Message);
    }

    [Fact]
    public void LuaEventsArrive()
    {
        var events = new List<WcpLuaEvent>();
        _client.Lua += events.Add;

        _client.LuaPut("speedo", "x"u8);
        _client.LuaRun("speedo");
        _device.LuaPrint("speedo", "rpm is 812");
        _device.SuspendLuaTx("bus-off");
        _client.LuaStop();

        Assert.True(Eventually(() => events.Count == 4));
        Assert.Equal(new WcpLuaEvent("speedo", "state", "running"), events[0]);
        Assert.Equal(new WcpLuaEvent("speedo", "print", "rpm is 812"), events[1]);
        Assert.Equal(new WcpLuaEvent("-", "tx", "suspended bus-off"), events[2]);
        Assert.Equal(new WcpLuaEvent("speedo", "state", "stopped"), events[3]);
    }

    [Fact]
    public void LuaStatusReadsBothShapes()
    {
        _client.LuaPut("a", "x"u8);
        _client.LuaRun("a");

        WcpLuaStatus status = _client.LuaStatus();

        Assert.False(status.TransmitEnabled);
        Assert.Equal("on", status.Values["engine"]);
        Assert.Equal(new WcpLuaScript("a", "running", 41, 3, 0, 0), status.Scripts.Single());
    }

    [Fact]
    public void EvalReturnsItsOutputAndItsFailure()
    {
        Assert.Equal(["hello", "world"], _client.LuaEval("print(\"hello\"); print(\"world\")").Output);

        WcpConsoleResult failed = _client.LuaEval("error(\"boom\")");
        Assert.False(failed.Ok);
        Assert.Equal("ARGS eval:1: boom", failed.Error);
    }

    [Fact]
    public void EvalTakesOneLine()
    {
        Assert.Throws<WcpException>(() => _client.LuaEval("print('a')\nprint('b')"));
        Assert.Empty(_device.Commands);
    }

    // ----- sharing the port with ELM327 -------------------------------------------

    [Fact]
    public async Task AnObd2SessionAndWcpShareThePort()
    {
        // A CAN stream running, events slipped between the chip's lines, and WCP
        // requests from another thread — while an ELM327 session connects and
        // polls on the same link.
        _client.CanStart();
        _client.CanStreamOn();

        var frames = 0;
        _client.CanFrame += _ => Interlocked.Increment(ref frames);

        using var stop = new CancellationTokenSource();
        int answered = 0;

        Task wcp = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Assert.Equal("12.6", _client.Status()["volt"]);
                _device.BusFrame(0x2C4, false, 0x01, 0x02);
                Interlocked.Increment(ref answered);
            }
        });

        _device.BetweenElmLines.Add("!CAN 5 7E8 s 1 41");

        using Elm327Source source = Elm327Source.Connect(_link);

        double[] values = [];
        for (int i = 0; i < 5; i++) values = source.Read();

        stop.Cancel();
        await wcp;

        int rpm = source.Names.ToList().IndexOf("RPM");
        Assert.Equal(1726, values[rpm], 1);
        Assert.True(answered > 0);
        Assert.True(frames > 0);
        Assert.Equal(0, _client.DamagedLines);
    }

    // ----- the keep-alive, in real time ---------------------------------------------

    /// <summary>Real clock, and an idle disarm short enough to test in a second or so.</summary>
    public sealed class KeepAlive : IDisposable
    {
        private readonly FakeWcpDevice _device = new(manualClock: false) { IdleDisarm = TimeSpan.FromMilliseconds(500) };
        private readonly WcpLink _link;

        public KeepAlive()
        {
            _link = new WcpLink(_device);
            _link.Open();
        }

        public void Dispose() => _link.Dispose();

        [Fact]
        public void PingsKeepItArmed()
        {
            using var client = new WcpClient(_link) { PingInterval = TimeSpan.FromMilliseconds(100) };

            client.CanArm();
            Thread.Sleep(1500);

            Assert.True(_device.Armed);
            Assert.True(client.IsArmed);
            Assert.True(client.Pings >= 5, $"{client.Pings} pings");
        }

        [Fact]
        public void WithoutPingsTheDeviceDisarmsAndTheClientHears()
        {
            using var client = new WcpClient(_link) { PingInterval = TimeSpan.FromHours(1) };
            string? why = null;
            client.Disarmed += r => why = r;

            client.CanArm();

            Assert.True(Eventually(() => why is not null, 3000));
            Assert.Equal("idle", why);
            Assert.False(client.IsArmed);
            Assert.False(_device.Armed);
        }

        [Fact]
        public void PingsStopOnceDisarmed()
        {
            using var client = new WcpClient(_link) { PingInterval = TimeSpan.FromMilliseconds(50) };

            client.CanArm();
            Thread.Sleep(300);
            client.CanDisarm();

            long pings = client.Pings;
            Thread.Sleep(300);

            Assert.Equal(pings, client.Pings);
        }

        [Fact]
        public void DisposingDisarms()
        {
            var client = new WcpClient(_link) { PingInterval = TimeSpan.FromMilliseconds(100) };
            client.CanArm();

            client.Dispose();

            Assert.False(_device.Armed);
        }
    }
}
