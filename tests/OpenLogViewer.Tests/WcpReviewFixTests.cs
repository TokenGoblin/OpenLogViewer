using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The host's side of the review fixes of 2026-11-22 (<c>diagnosis/wcp-review-fixes.md</c>):
/// H1 to H6, the host part of F17, and the firmware behaviour the fixes add — each
/// against <see cref="FakeWcpDevice"/> as the fixed firmware is meant to behave.
/// </summary>
public class WcpReviewFixTests : IDisposable
{
    private readonly FakeWcpDevice _device;
    private readonly WcpLink _link;
    private readonly WcpClient _client;

    public WcpReviewFixTests()
    {
        _device = new FakeWcpDevice(FakeWcpDevice.Car());
        _link = new WcpLink(_device);
        _link.Open();
        _client = new WcpClient(_link) { Timeout = TimeSpan.FromMilliseconds(300) };
    }

    public void Dispose()
    {
        _client.Dispose();
        _link.Dispose();
    }

    private static bool Eventually(Func<bool> condition, int ms = 3000)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(ms);

        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    private static byte[] Script(int bytes) =>
        [.. Enumerable.Range(0, bytes).Select(i => (byte)('a' + i % 26))];

    // ----- H1: lua put after a lost answer -------------------------------------------

    [Fact]
    public void H1_AChunkWhoseAnswerWasLostIsAnsweredFromTheStore()
    {
        byte[] script = Script(3 * WcpClient.MaxLuaChunk);
        _device.LoseRepliesTo($"lua put s {WcpClient.MaxLuaChunk} ");

        Assert.Equal(script.Length, _client.LuaPut("s", script));

        Assert.Equal(script, _device.LuaScripts["s"]);
        Assert.Single(_device.Replayed);
        Assert.Equal(0, _client.LuaPutRecoveries);
    }

    [Fact]
    public void H1_WithoutTheStoreTheOverlapRefusalIsRecoveredByListing()
    {
        // A firmware without the reply store, or one whose store moved on: the
        // chunk landed, its OK was lost, and sending it again is an overlap.
        _device.ReplayStore = false;
        byte[] script = Script(3 * WcpClient.MaxLuaChunk);
        _device.LoseRepliesTo($"lua put s {WcpClient.MaxLuaChunk} ");

        Assert.Equal(script.Length, _client.LuaPut("s", script));

        Assert.Equal(script, _device.LuaScripts["s"]);
        Assert.Equal(1, _client.LuaPutRecoveries);
        Assert.Contains("lua list", _device.Commands);
    }

    [Fact]
    public void H1_TwoLostAnswersEndInATimeoutAndTheListingSaysItLanded()
    {
        byte[] script = Script(3 * WcpClient.MaxLuaChunk);
        _device.LoseRepliesTo($"lua put s {WcpClient.MaxLuaChunk} ", 2);

        Assert.Equal(script.Length, _client.LuaPut("s", script));

        Assert.Equal(script, _device.LuaScripts["s"]);
        Assert.Equal(1, _client.LuaPutRecoveries);
        Assert.Single(_device.Commands, c => c.StartsWith($"lua put s {WcpClient.MaxLuaChunk} ", StringComparison.Ordinal));
    }

    [Fact]
    public void H1_AChunkThatNeverArrivedIsSentAgain()
    {
        byte[] script = Script(3 * WcpClient.MaxLuaChunk);
        _device.DropRequestsTo($"lua put s {2 * WcpClient.MaxLuaChunk} ", 2);

        Assert.Equal(script.Length, _client.LuaPut("s", script));

        Assert.Equal(script, _device.LuaScripts["s"]);
        Assert.Equal(1, _client.LuaPutRecoveries);
    }

    // ----- H2: BUSY while booting, and a late !BOOT -------------------------------------

    [Fact]
    public void H2_HelloAnsweredBusyIsAskedAgain()
    {
        _device.BusyHellos = 3;

        using WcpClient? found = WcpClient.Detect(_link, TimeSpan.FromMilliseconds(300));

        Assert.NotNull(found);
        Assert.Equal(4, _device.Commands.Count(c => c == "hello"));
    }

    [Fact]
    public void H2_AUnitBusyPastTheWindowIsNotTakenForWcp()
    {
        _device.BusyHellos = 1000;

        Assert.Null(WcpClient.Detect(_link, TimeSpan.FromMilliseconds(300), busyWindow: TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public void H2_TheDetectTimeoutIsTwoAndAHalfSeconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2500), WcpClient.DetectTimeout);

        using WcpClient? found = WcpClient.Detect(_link);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), found!.Timeout);
    }

    [Fact]
    public void H2_AUnitThatWasRestartingIsFoundWhenItSaysBoot()
    {
        var device = new FakeWcpDevice(FakeWcpDevice.Car()) { Silent = true };
        WicanProConnection pro = WicanPro.Connect(device, helloTimeout: TimeSpan.FromMilliseconds(200));

        try
        {
            Assert.Null(pro.Wcp);
            WcpClient? raised = null;
            pro.WcpFound += c => raised = c;

            device.Silent = false;
            device.AnnounceBoot();

            Assert.True(Eventually(() => pro.Wcp is not null));
            Assert.Same(pro.Wcp, raised);
            Assert.Equal("v4.50p+wcp1", pro.Wcp!.Identity!.Firmware);
            Assert.Equal(1, pro.Redetections);

            // OBD2 goes on beside it, untouched.
            Assert.NotEmpty(pro.Source.Read());
        }
        finally
        {
            pro.Wcp?.Dispose();
            pro.Source.Dispose();
        }
    }

    // ----- F17: the same seq on a retry ---------------------------------------------

    [Fact]
    public void F17_ACrcRetryAndATimeoutRetryKeepTheirSeq()
    {
        _device.RejectWithCrc = 1;
        _client.Ping();

        _device.LoseReplies = 1;
        _client.Ping();

        Assert.Equal(4, _device.Requests.Count);
        Assert.Equal(_device.Requests[0], _device.Requests[1]);
        Assert.Equal(_device.Requests[2], _device.Requests[3]);
        Assert.NotEqual(_device.Requests[0], _device.Requests[2]);

        // The second ping ran once and was answered again from the store.
        Assert.Equal(["ping", "ping"], _device.Commands);
        Assert.Single(_device.Replayed);
    }

    [Fact]
    public void F17_ARetryForLostDataLinesTakesANewSeqSoItRunsAgain()
    {
        _device.LoseDataLines = 1;

        Assert.Equal(FakeWcpDevice.DefaultConfig.Count, _client.ConfigGet().Pairs.Count);

        Assert.Equal(2, _device.Requests.Count);
        Assert.NotEqual(_device.Requests[0], _device.Requests[1]);
        Assert.Empty(_device.Replayed);
    }

    [Fact]
    public void F17_AReplayWithoutItsDataLinesIsAskedForOnceMore()
    {
        // The whole reply lost; the store has only the final line, which says
        // nineteen lines were sent and brings none — so a new seq, and it runs again.
        _device.LoseReplies = 1;

        Assert.Equal("0", _client.Status()["armed"]);

        Assert.Equal(["status", "status"], _device.Commands);
        Assert.Single(_device.Replayed);
    }

    [Fact]
    public void F17_ARebootIsNeverRetried()
    {
        _device.LoseReplies = 1;

        Assert.Throws<TimeoutException>(() => _client.Reboot());
        Assert.Single(_device.Requests, r => r.EndsWith(" reboot", StringComparison.Ordinal));
    }

    [Fact]
    public void F17_AnEvalIsNeverRetried()
    {
        _device.LoseReplies = 1;

        Assert.Throws<TimeoutException>(() => _client.LuaEval("print(\"a\")"));
        Assert.Single(_device.Requests);
    }

    // ----- H6 ------------------------------------------------------------------------

    [Fact]
    public void H6_ThreeHundredAndSixtyBytesOfEvalFitAndOneMoreDoesNot()
    {
        Assert.Equal(360, WcpClient.MaxEvalBytes);

        string code = "print(\"ok\")\n--" + new string('x', 360 - 14);
        Assert.Equal(360, Encoding.UTF8.GetByteCount(code));
        Assert.Equal(["ok"], _client.LuaEval(code).Output);

        Assert.Equal("HOST", Assert.Throws<WcpException>(() => _client.LuaEval(code + "x")).Code);
    }

    // ----- the new firmware behaviour --------------------------------------------------

    [Fact]
    public void StreamingWithTheControllerOffIsAStateError()
    {
        WcpException e = Assert.Throws<WcpException>(() => _client.CanStreamOn());
        Assert.Equal("STATE", e.Code);
    }

    [Fact]
    public void AnExtendedFilterSaysSoWithX()
    {
        var frames = new List<WcpCanEvent>();
        _client.CanFrame += frames.Add;
        _client.CanStart();

        _client.CanStreamOn(0x18DAF110, 0x1FFFFFFF);
        Assert.Equal("can stream on 18DAF110 1FFFFFFF x", _device.Commands[^1]);

        _client.CanStreamOn(0x123, 0x7FF, extended: true);
        Assert.Equal("can stream on 00000123 000007FF x", _device.Commands[^1]);

        _device.BusFrame(0x123, false, 1);
        _device.BusFrame(0x123, true, 2);

        Assert.True(Eventually(() => frames.Count == 1));
        Assert.True(frames.Single().Extended);

        _client.CanStreamOn(0x7E8, 0x7F8);
        Assert.Equal("can stream on 7E8 7F8", _device.Commands[^1]);

        Assert.Equal("HOST", Assert.Throws<WcpException>(() => _client.CanStreamOn(0x800, 0x7FF, extended: false)).Code);
    }

    [Fact]
    public void ALongSettingIsReadWholeWithB64()
    {
        string json = "[" + string.Join(",", Enumerable.Range(0, 12).Select(i => $"{{\"ssid\":\"net{i}\",\"pass\":\"p{i}\"}}")) + "]";
        Assert.True(json.Length > Wcp.MaxDataText);

        _client.ConfigSet("sta_fallbacks", json);

        Assert.Equal(json, _client.ConfigGet("sta_fallbacks"));
        Assert.Equal(json, _client.ConfigGet()["sta_fallbacks"]);
        Assert.Contains($"config get sta_fallbacks {WcpWire.Base64Flag}", _device.Commands);
    }

    [Fact]
    public void AMaskedValueIsRefusedBeforeItIsSent()
    {
        WcpException e = Assert.Throws<WcpException>(() => _client.ConfigSet("sta_pass", "****"));

        Assert.Equal("HOST", e.Code);
        Assert.Empty(_device.Commands);
    }

    [Fact]
    public void ASaveOverSettingsThatChangedIsRefusedAndSaysWhy()
    {
        _client.ConfigSet("sta_ssid", "Garage");
        _device.ChangeConfigOutside("ap_ch", "11");

        WcpException e = Assert.Throws<WcpException>(() => _client.ConfigSave());

        Assert.Equal("STATE", e.Code);
        Assert.Contains("changed since these were staged", e.Message, StringComparison.Ordinal);
        Assert.Equal("", _device.Saved["sta_ssid"]);
    }

    [Fact]
    public void ArmingOnASilentBusIsRefusedAsNoTraffic()
    {
        _device.BusChatter = false;
        _client.CanStart();

        Assert.Equal(0, _client.CanStatus().Long(WcpWire.RxRecent));

        WcpException e = Assert.Throws<WcpException>(() => _client.CanArm());
        Assert.True(e.IsNoTraffic);
        Assert.False(_client.IsArmed);

        _device.BusFrame(0x7E8, false, 1);
        Assert.Equal(1, _client.CanStatus().Long(WcpWire.RxRecent));

        _client.CanArm();
        Assert.True(_client.IsArmed);

        _device.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, _client.CanStatus().Long(WcpWire.RxRecent));
    }

    [Fact]
    public void ATransmitFaultLatchesUntilCanResume()
    {
        string? why = null;
        _client.Disarmed += r => why = r;
        _client.CanStart();
        _client.CanArm();

        _device.FailNextSend = true;
        WcpException failed = Assert.Throws<WcpException>(() => _client.CanSend(0x7E0, [1]));
        Assert.Equal("IO", failed.Code);

        Assert.True(Eventually(() => _client.TransmitFault is not null));
        Assert.Equal("tx-timeout", _client.TransmitFault);
        Assert.Equal("fault tx-timeout", why);
        Assert.False(_client.IsArmed);
        Assert.Equal("listen", _client.CanStatus()["mode"]);

        WcpException refused = Assert.Throws<WcpException>(() => _client.CanArm());
        Assert.Equal("STATE", refused.Code);

        _client.CanResume();
        Assert.Null(_client.TransmitFault);
        Assert.Contains(WcpWire.CanResume, _device.Commands);

        _client.CanArm();
        Assert.Equal("sent", _client.CanSend(0x7E0, [1]));
    }

    [Theory]
    [InlineData("fault tx-timeout", true, "tx-timeout")]
    [InlineData("fault bus-off", true, "bus-off")]
    [InlineData("fault: tec", true, "tec")]
    [InlineData("fault", true, "unknown")]
    [InlineData("idle", false, "")]
    [InlineData("faulty", false, "")]
    public void AFaultReasonIsReadTolerantly(string reason, bool isFault, string fault)
    {
        Assert.Equal(isFault, WcpWire.IsFault(reason, out string got));
        Assert.Equal(fault, got);
    }

    [Fact]
    public void ScriptOutputIsUnescapedForWhoeverShowsIt()
    {
        var events = new List<WcpLuaEvent>();
        _client.Lua += events.Add;

        _device.LuaPrint("s", "rpm > 6000\tnow é");

        Assert.True(Eventually(() => events.Count == 1));
        Assert.Equal("rpm > 6000\tnow é", events[0].Text);

        Assert.Equal(["a>b"], _client.LuaEval("print(\"a\\x3Eb\")").Output);
    }

    [Fact]
    public void ALuaTimeoutSaysSo()
    {
        WcpConsoleResult r = _client.LuaEval("while true do end");

        Assert.False(r.Ok);
        Assert.StartsWith("STATE timeout", r.Error, StringComparison.Ordinal);
        Assert.Contains("time limit", r.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a>b", "a\\x3Eb")]
    [InlineData("tab\there", "tab\\x09here")]
    [InlineData("plain", "plain")]
    [InlineData("é", "\\xC3\\xA9")]
    public void EscapingRoundTrips(string text, string wire)
    {
        Assert.Equal(wire, Wcp.Escape(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, Wcp.Unescape(wire));
    }

    // ----- H3: the keep-alive, in real time ---------------------------------------------

    /// <summary>Real clock, and a 1 s idle disconnect.</summary>
    public sealed class KeepAlive : IDisposable
    {
        private readonly FakeWcpDevice _device = new(manualClock: false) { IdleDisarm = TimeSpan.FromMilliseconds(1000) };
        private readonly WcpLink _link;

        public KeepAlive()
        {
            _link = new WcpLink(_device);
            _link.Open();
        }

        public void Dispose() => _link.Dispose();

        [Fact]
        public void H3_PingsKeepAnUnlockAlive()
        {
            using var client = new WcpClient(_link) { PingInterval = TimeSpan.FromMilliseconds(100) };

            client.ConfigUnlock();
            Thread.Sleep(1500);

            Assert.True(_device.Unlocked);
            Assert.True(client.Pings >= 5, $"{client.Pings} pings");
            Assert.Equal("homesecret", client.ConfigSecrets()["sta_pass"]);
        }

        [Fact]
        public void H3_WithoutPingsTheUnlockEnds()
        {
            using var client = new WcpClient(_link) { PingInterval = TimeSpan.FromHours(1) };

            client.ConfigUnlock();
            Assert.True(Eventually(() => !_device.Unlocked));

            Assert.Equal("DENIED", Assert.Throws<WcpException>(() => client.ConfigSecrets()).Code);
        }

        [Fact]
        public void H3_PingsKeepStagedSettingsUntilSaved()
        {
            using var client = new WcpClient(_link) { PingInterval = TimeSpan.FromMilliseconds(100) };

            client.ConfigSet("sta_ssid", "Garage");
            Thread.Sleep(1500);

            Assert.Single(client.ConfigDiff());

            client.ConfigSave();
            Assert.False(client.HasStagedChanges);

            long pings = client.Pings;
            Thread.Sleep(400);
            Assert.Equal(pings, client.Pings);
        }

        [Fact]
        public void H3_WithoutPingsTheStageIsDropped()
        {
            using var client = new WcpClient(_link) { PingInterval = TimeSpan.FromHours(1) };

            client.ConfigSet("sta_ssid", "Garage");
            Assert.True(Eventually(() => _device.Staged["sta_ssid"] == ""));
        }
    }

    // ----- F17: no WCP while an ELM327 command waits for its prompt ------------------

    /// <summary>A port whose ELM327 answers come when the test says, and which records the order things were written in.</summary>
    private sealed class SlowChip : IEcuTransport, IReadsAvailable
    {
        private readonly object _gate = new();
        private readonly Queue<byte> _out = new();

        public List<string> Written { get; } = [];

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close()
        {
            lock (_gate)
            {
                IsOpen = false;
                Monitor.PulseAll(_gate);
            }
        }

        public void Dispose() => Close();

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_gate) Written.Add(Encoding.ASCII.GetString(data));
        }

        public void Answer(string text)
        {
            lock (_gate)
            {
                foreach (char c in text) _out.Enqueue((byte)c);
                Monitor.PulseAll(_gate);
            }
        }

        public int Read(Span<byte> buffer, TimeSpan timeout) => ReadAvailable(buffer, timeout);

        public int ReadAvailable(Span<byte> buffer, TimeSpan timeout)
        {
            DateTime until = DateTime.UtcNow + timeout;

            lock (_gate)
            {
                while (_out.Count == 0 && IsOpen)
                {
                    TimeSpan left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return 0;
                    Monitor.Wait(_gate, left);
                }

                int n = 0;
                while (n < buffer.Length && _out.Count > 0) buffer[n++] = _out.Dequeue();
                return n;
            }
        }

        public void DiscardInput()
        {
            lock (_gate) _out.Clear();
        }

        public int Count
        {
            get
            {
                lock (_gate) return Written.Count;
            }
        }
    }

    [Fact]
    public async Task F17_AWcpLineWaitsForTheElmPrompt()
    {
        var chip = new SlowChip();
        using var link = new WcpLink(chip);
        link.Open();

        link.Write("010C\r"u8);
        Assert.True(link.ElmAwaitingPrompt);

        Task send = Task.Run(() => link.SendLine(Wcp.Request(7, "ping")));

        Thread.Sleep(300);
        Assert.Equal(1, chip.Count);

        chip.Answer("41 0C 1A F8\r\r>");

        await send.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, chip.Count);
        Assert.StartsWith("#7 ping", chip.Written[1], StringComparison.Ordinal);
        Assert.Equal(1, link.HeldForElm);
    }

    [Fact]
    public void F17_APromptThatNeverComesHoldsWcpOnlySoLong()
    {
        var chip = new SlowChip();
        using var link = new WcpLink(chip);
        link.Open();

        link.Write("010C\r"u8);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        link.SendLine(Wcp.Request(7, "ping"));

        Assert.InRange(clock.Elapsed, WcpLink.ElmPromptWait - TimeSpan.FromMilliseconds(100), WcpLink.ElmPromptWait + TimeSpan.FromSeconds(1));
        Assert.Equal(2, chip.Count);
    }
}
