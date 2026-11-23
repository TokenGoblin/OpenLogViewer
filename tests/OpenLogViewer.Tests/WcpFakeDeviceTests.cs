using System.Text;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The fake firmware against the contract, in raw WCP lines and with no client in
/// between.
///
/// <para>
/// These are the tests for the firmware's author to read. Each one is a rule from
/// <c>diagnosis/wican-claude-protocol.md</c> — the light transmit gate of section
/// 3, the settings rules, the Lua transmit rules of 7.3 — stated as what goes in
/// and what must come out. If the real firmware answers these lines the same way,
/// the host side here works against it; where it does not, one of the two is wrong
/// and the contract says which.
/// </para>
/// </summary>
public class WcpFakeDeviceTests : IDisposable
{
    private readonly FakeWcpDevice _device = new();
    private int _seq;

    public WcpFakeDeviceTests() => _device.Open();

    public void Dispose() => _device.Dispose();

    /// <summary>Sends one request and returns every line that came back, CRC checked and stripped.</summary>
    private List<string> Ask(string command)
    {
        _device.Write(Encoding.ASCII.GetBytes(Wcp.Request(++_seq, command) + "\r"));
        return Drain();
    }

    private List<string> Drain()
    {
        var text = new StringBuilder();
        byte[] buffer = new byte[4096];
        int got;

        while ((got = _device.Read(buffer, TimeSpan.Zero)) > 0) text.Append(Encoding.ASCII.GetString(buffer, 0, got));

        var lines = new List<string>();

        foreach (string raw in text.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(Wcp.TryUnframe(raw, out string body, out bool ok) && ok, $"Bad framing from the fake: {raw}");
            lines.Add(body);
        }

        return lines;
    }

    private static string B64(string code) => Convert.ToBase64String(Encoding.UTF8.GetBytes(code));

    private string Final(List<string> lines) => lines.Last(l => l.StartsWith($"#{_seq}.", StringComparison.Ordinal));

    private void Ok(string command)
    {
        string final = Final(Ask(command));
        Assert.True(final.StartsWith($"#{_seq}.OK", StringComparison.Ordinal), $"{command}: {final}");
    }

    // ----- framing ---------------------------------------------------------------

    [Fact]
    public void ABadCrcIsRefusedWithTheSeq()
    {
        _device.Write("#5 ping*00\r"u8);

        Assert.Equal(["#5.ERR CRC"], Drain());
    }

    [Fact]
    public void ABadCrcWithAnUnreadableSeqIsRefusedAsSeqZero()
    {
        _device.Write("#x ping*00\r"u8);

        Assert.Equal(["#0.ERR CRC"], Drain());
    }

    [Fact]
    public void AnOverLongRequestIsTooLong()
    {
        _device.Write(Encoding.ASCII.GetBytes("#9 config set note=" + new string('a', 600) + "*00\r"));

        Assert.StartsWith("#9.ERR TOOLONG", Drain().Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCommandIsUnknown() =>
        Assert.StartsWith($"#{_seq + 1}.ERR UNKNOWN", Final(Ask("frobnicate")), StringComparison.Ordinal);

    [Fact]
    public void ElmLinesStillReachTheChip()
    {
        _device.Write("ATI\r"u8);

        byte[] buffer = new byte[256];
        string reply = Encoding.ASCII.GetString(buffer, 0, _device.Read(buffer, TimeSpan.Zero));

        Assert.Contains("ELM327 v1.5", reply, StringComparison.Ordinal);
        Assert.Equal(["ATI"], _device.ElmLines);
    }

    [Fact]
    public void HelloSaysWcp1()
    {
        List<string> lines = Ask("hello");

        Assert.Contains($"#{_seq}:wcp=1", lines);
        Assert.Contains(lines, l => l.StartsWith($"#{_seq}:fw=", StringComparison.Ordinal) && l.EndsWith("+wcp1", StringComparison.Ordinal));
    }

    // ----- section 8, the clarifications ----------------------------------------------

    [Fact]
    public void S8_3_OkCountsItsDataLines()
    {
        List<string> lines = Ask("hello");

        Assert.Equal($"#{_seq}.OK n=5", lines.Last());
        Assert.Equal(5, lines.Count(l => l.StartsWith($"#{_seq}:", StringComparison.Ordinal)));
        string sent = Final(PrepareAndSend());
        Assert.Equal($"#{_seq}.OK n=0 sent", sent);
    }

    private List<string> PrepareAndSend()
    {
        Ok("can start");
        Ok("can arm");
        return Ask("can send 7E0 01");
    }

    [Fact]
    public void S8_2_WcpWaitsForThePromptAndStartsOnANewLine()
    {
        // An event due during an ELM327 reply goes after its prompt, and with
        // CR LF in front of it, since the prompt is not followed by an LF.
        _device.AfterElmPrompt.Add("!DROP 1");
        _device.Write("ATI\r"u8);

        byte[] buffer = new byte[512];
        string wire = Encoding.ASCII.GetString(buffer, 0, _device.Read(buffer, TimeSpan.Zero));

        Assert.EndsWith($">\r\n{Wcp.Frame("!DROP 1")}\r\n", wire, StringComparison.Ordinal);
        Assert.Equal(1, wire.Count(c => c == '>'));
    }

    [Fact]
    public void S8_2_NoExtraLineBreakAfterAnLf()
    {
        _device.Write(Encoding.ASCII.GetBytes(Wcp.Request(1, "ping") + "\r" + Wcp.Request(2, "ping") + "\r"));

        byte[] buffer = new byte[512];
        string wire = Encoding.ASCII.GetString(buffer, 0, _device.Read(buffer, TimeSpan.Zero));

        Assert.DoesNotContain("\r\n\r\n", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void S8_5_TheGateReportsArmedAndTheCapAndNothingElse()
    {
        List<string> status = Ask("status");
        Assert.Contains($"#{_seq}:tx_rate_cap=200", status);
        Assert.DoesNotContain(status, l => l.Contains("armed_ttl_s", StringComparison.Ordinal));

        List<string> can = Ask("can status");
        Assert.Contains($"#{_seq}:tx_rate_cap=200", can);
        Assert.Contains($"#{_seq}:armed=0", can);
        Assert.DoesNotContain(can, l => l.Contains("allow=", StringComparison.Ordinal));

        Assert.Contains("!ARMED", Ask("can arm"));
    }

    [Fact]
    public void S8_6_EvalIsBase64AndAtMost4KB()
    {
        List<string> lines = Ask("lua eval " + B64("print(\"one\")\nprint(\"two\")"));
        Assert.Contains($"#{_seq}:one", lines);
        Assert.Contains($"#{_seq}:two", lines);

        Assert.StartsWith($"#{_seq + 1}.ERR ARGS", Final(Ask("lua eval print(1)")), StringComparison.Ordinal);
    }

    [Fact]
    public void S8_8_LuaStatusIsKeyValueWithAScriptLineEach()
    {
        _device.LuaScripts["a"] = "x"u8.ToArray();
        Ok("lua run a");

        List<string> lines = Ask("lua status");

        Assert.Contains($"#{_seq}:script=a state=running mem_kb=41 cb_max_ms=3 queue_drops=0 errors=0", lines);
        Assert.All(lines.Where(l => l.StartsWith($"#{_seq}:", StringComparison.Ordinal)), l => Assert.Contains('=', l));
    }

    [Fact]
    public void S8_8_Base64LinesAreAtMost160Characters()
    {
        _device.LogFiles["a.db"] = new byte[3000];
        _device.Base64PerLine = false;

        List<string> data = Ask("log read a.db 0 3000").Where(l => l.StartsWith($"#{_seq}:", StringComparison.Ordinal)).ToList();

        Assert.All(data, l => Assert.True(l.Length - $"#{_seq}:".Length <= 160));
    }

    [Fact]
    public void S8_8_ALogReadOver3072IsArgs()
    {
        _device.LogFiles["a.db"] = new byte[5000];

        Assert.StartsWith($"#{_seq + 1}.ERR ARGS", Final(Ask("log read a.db 0 3073")), StringComparison.Ordinal);
    }

    [Fact]
    public void S8_8_LogListGivesUnixSeconds()
    {
        _device.LogFiles["a.db"] = [1, 2, 3];

        Assert.Matches(@"^#\d+:a\.db 3 \d+$", Ask("log list").First(l => l.Contains("a.db", StringComparison.Ordinal)));
    }

    [Fact]
    public void S8_8_CanStartTakesKbps()
    {
        Ok("can start 250");

        Assert.Contains($"#{_seq + 1}:bitrate=250", Ask("status"));
    }

    [Fact]
    public void S8_8_ARemoteFrameIsFlaggedR()
    {
        Ok("can start");
        Ok("can stream on");

        _device.BusRemoteFrame(0x7DF, false, 8);
        _device.BusRemoteFrame(0x18DAF110, true, 0);

        List<string> lines = Drain();
        Assert.Matches(@"^!CAN \d+ 7DF sr 8$", lines[0]);
        Assert.Matches(@"^!CAN \d+ 18DAF110 xr 0$", lines[1]);
    }

    [Fact]
    public void S8_9_LuaTransmitSettingsAreLiveAfterTheSaveWithoutAReboot()
    {
        Ok("can start");
        Ok("config set lua_can_tx=enable");
        Ok("config save");

        Assert.Equal(0, _device.Reboots);
        Assert.True(_device.LuaSend("a", 0x100, [1]).Sent);
    }

    [Fact]
    public void S8_10_ARebootSaysBoot()
    {
        List<string> lines = Ask("reboot");

        Assert.Equal("!BOOT v4.50p+wcp1", lines.Last());
    }

    // ----- CAN transmit: the light gate (section 3) --------------------------------

    [Fact]
    public void Rule1_OffByDefault()
    {
        Assert.Contains($"#{_seq + 1}:armed=0", Ask("status"));

        Ok("can start");
        Assert.StartsWith($"#{_seq + 1}.ERR NOTARMED", Final(Ask("can send 7E0 0201")), StringComparison.Ordinal);
        Assert.Empty(_device.Transmitted);
    }

    [Fact]
    public void Rule2_ArmTakesNoArguments()
    {
        Assert.StartsWith($"#{_seq + 1}.ERR ARGS", Final(Ask("can arm 30 7E0")), StringComparison.Ordinal);
        Assert.False(_device.Armed);
    }

    [Fact]
    public void Rule2_ArmAllowsAnyIdentifier()
    {
        Ok("can start");

        List<string> armed = Ask("can arm");
        Assert.Contains("!ARMED", armed);

        Ok("can send 7E0 0201");
        Ok("can send 123 AABB");
        Ok("can send 18DAF110 x 0102030405060708");

        Assert.Equal([0x7E0u, 0x123u, 0x18DAF110u], _device.Transmitted.Select(t => t.Id));
        Assert.True(_device.Transmitted[2].Extended);
    }

    [Fact]
    public void Rule2_TenSecondsWithoutARequestDisarms()
    {
        Ok("can arm");

        _device.Advance(TimeSpan.FromSeconds(9.9));
        Assert.True(_device.Armed);

        _device.Advance(TimeSpan.FromSeconds(0.2));
        Assert.False(_device.Armed);
        Assert.Equal(["!DISARM idle"], Drain());
    }

    [Fact]
    public void Rule2_AnyRequestCountsAsTheKeepAlive()
    {
        Ok("can arm");

        for (int i = 0; i < 5; i++)
        {
            _device.Advance(TimeSpan.FromSeconds(6));
            Ok(i % 2 == 0 ? "ping" : "status");
        }

        Assert.True(_device.Armed);
    }

    [Fact]
    public void Rule2_ARequestAfterTheIdleTimeDoesNotRevive()
    {
        Ok("can arm");
        Ok("can start");

        // No timer tick in between: the lapse is found when the request arrives,
        // and the request must not count as the keep-alive that saves it.
        _device.Skip(TimeSpan.FromSeconds(11));

        List<string> lines = Ask("can send 7E0 01");

        Assert.Contains("!DISARM idle", lines);
        Assert.StartsWith($"#{_seq}.ERR NOTARMED", Final(lines), StringComparison.Ordinal);
        Assert.Empty(_device.Transmitted);
    }

    [Fact]
    public void Rule2_ARebootDisarms()
    {
        Ok("can arm");
        Ok("reboot");

        Assert.False(_device.Armed);
        Assert.Contains($"#{_seq + 1}:armed=0", Ask("status"));
    }

    [Fact]
    public void Rule3_TwoHundredFramesASecondOverall()
    {
        Ok("can start");
        Ok("can arm");

        for (int i = 0; i < FakeWcpDevice.WcpTxRate; i++) Ok($"can send {0x100 + i % 50:X3} 01");

        Assert.Equal($"#{_seq + 1}.ERR DENIED rate", Final(Ask("can send 7E0 01")));
        Assert.Equal(FakeWcpDevice.WcpTxRate, _device.Transmitted.Count);

        // The bucket refills: a second later, another full allowance.
        _device.Advance(TimeSpan.FromSeconds(1));
        Ok("can send 7E0 01");
    }

    [Fact]
    public void Rule3_NoPerIdentifierLimit()
    {
        Ok("can start");
        Ok("can arm");

        for (int i = 0; i < 50; i++) Ok("can send 7E0 01");

        Assert.Equal(50, _device.Transmitted.Count);
    }

    [Theory]
    [InlineData("can send 7E0 010203040506070809")]
    [InlineData("can send 7E0 0")]
    [InlineData("can send 7E0 GG")]
    [InlineData("can send 800 01")]
    [InlineData("can send 7E0 01 02")]
    public void Rule4_AMalformedFrameIsRefused(string command)
    {
        Ok("can start");
        Ok("can arm");

        Assert.StartsWith($"#{_seq + 1}.ERR ARGS", Final(Ask(command)), StringComparison.Ordinal);
        Assert.Empty(_device.Transmitted);
    }

    [Fact]
    public void Rule4_ZeroBytesIsAFrame()
    {
        Ok("can start");
        Ok("can arm");
        Ok("can send 7E0");

        Assert.Empty(_device.Transmitted.Single().Data);
    }

    [Fact]
    public void Rule5_DisarmSaysHost()
    {
        Ok("can arm");

        List<string> lines = Ask("can disarm");

        Assert.Equal("!DISARM host", lines.Last());
        Assert.False(_device.Armed);
    }

    [Fact]
    public void Rule6_TransmitsAreCountedAndTheLast32Kept()
    {
        Ok("can start");
        Ok("can arm");

        for (int i = 0; i < 40; i++) Ok($"can send {i:X3} {i:X2}");

        Assert.Contains($"#{_seq + 1}:can_tx=40", Ask("status"));

        List<string> log = Ask("can txlog").Where(l => l.Contains(':', StringComparison.Ordinal)).ToList();
        Assert.Equal(32, log.Count);
        Assert.Contains(" 008 s 08 wcp", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public void StartingTheControllerIsListenOnly()
    {
        Ok("can start 500000");

        Assert.Contains($"#{_seq + 1}:can=listen", Ask("status"));
    }

    // ----- the CAN stream ----------------------------------------------------------

    [Fact]
    public void TheStreamCarriesFramesAndFiltersThem()
    {
        Ok("can start");
        Ok("can stream on 7E8 7F8");

        _device.BusFrame(0x7E8, false, 0x41, 0x0C);
        _device.BusFrame(0x2C4, false, 0x01);

        List<string> lines = Drain();
        Assert.Single(lines);
        Assert.Matches(@"^!CAN \d+ 7E8 s 2 410C$", lines[0]);
    }

    [Fact]
    public void AFullRingCountsDropsAndSaysSo()
    {
        Ok("can start");
        Ok("can stream on");

        _device.HoldStream = true;
        for (int i = 0; i < FakeWcpDevice.RingCapacity + 7; i++) _device.BusFrame(0x100, false, 1);

        _device.ReleaseStream();
        List<string> lines = Drain();

        Assert.Equal(FakeWcpDevice.RingCapacity, lines.Count(l => l.StartsWith("!CAN", StringComparison.Ordinal)));
        Assert.Equal("!DROP 7", lines.Last());
    }

    // ----- settings ------------------------------------------------------------------

    [Fact]
    public void PasswordsAreMaskedEverywhereButSecrets()
    {
        Assert.Contains($"#{_seq + 1}:sta_pass=****", Ask("config get"));
        Assert.Contains($"#{_seq + 1}:ap_pass=****", Ask("config get ap_pass"));

        Ok("config set sta_pass=another one");
        Assert.Contains($"#{_seq + 1}:sta_pass=**** -> ****", Ask("config diff"));

        Assert.DoesNotContain(Ask("config get"), l => l.Contains("homesecret", StringComparison.Ordinal));
    }

    [Fact]
    public void SecretsNeedAnUnlockInTheSameSession()
    {
        Assert.StartsWith($"#{_seq + 1}.ERR DENIED", Final(Ask("config secrets")), StringComparison.Ordinal);

        Ok("config unlock");
        Assert.Contains($"#{_seq + 1}:sta_pass=homesecret", Ask("config secrets"));

        // A disconnect ends the session, and the unlock with it.
        _device.Close();
        _device.Open();
        Assert.StartsWith($"#{_seq + 1}.ERR DENIED", Final(Ask("config secrets")), StringComparison.Ordinal);
    }

    [Fact]
    public void AValueIsTheRestOfTheLineSpacesAndStarsIncluded()
    {
        Ok("config set sta_ssid=My Home *Net*");

        Assert.Equal("My Home *Net*", _device.Staged["sta_ssid"]);
    }

    [Fact]
    public void AnUnknownKeyIsArgs() =>
        Assert.StartsWith($"#{_seq + 1}.ERR ARGS", Final(Ask("config set no_such_key=1")), StringComparison.Ordinal);

    [Fact]
    public void ASaveThatWouldFailBootValidationWritesNothing()
    {
        Ok("config set port=99999");

        Assert.Equal($"#{_seq + 1}.ERR ARGS port: 1 to 65535", Final(Ask("config save")));
        Assert.Equal("35000", _device.Saved["port"]);
    }

    [Fact]
    public void NothingTouchesFlashUntilSave()
    {
        Ok("config set port=35001");
        Assert.Equal("35000", _device.Saved["port"]);

        Ok("config save");
        Assert.Equal("35001", _device.Saved["port"]);
    }

    [Fact]
    public void DiscardDropsTheStagedChanges()
    {
        Ok("config set port=35001");
        Ok("config discard");

        Assert.DoesNotContain(Ask("config diff"), l => l.Contains(':', StringComparison.Ordinal));
    }

    // ----- Lua transmit (section 7.3) -------------------------------------------------

    private void EnableLuaTx(string rate = "100")
    {
        Ok("can start");
        Ok("config set lua_can_tx=enable");
        Ok($"config set lua_tx_rate={rate}");
        Ok("config save");
    }

    [Fact]
    public void Lua1_OffByDefault()
    {
        Ok("can start");

        Assert.Equal((false, "lua_can_tx disabled"), _device.LuaSend("speedo", 0x7E0, [1]));
        Assert.Empty(_device.Transmitted);
    }

    [Fact]
    public void Lua1_EnabledAllowsAnyIdentifierWithoutArming()
    {
        EnableLuaTx();

        Assert.False(_device.Armed);
        Assert.Equal((true, ""), _device.LuaSend("speedo", 0x123, [1]));
        Assert.Equal((true, ""), _device.LuaSend("speedo", 0x18DAF110, [1], extended: true));
    }

    [Fact]
    public void Lua2_TheRateCapIsSharedAndCounted()
    {
        EnableLuaTx("5");

        for (int i = 0; i < 5; i++) Assert.True(_device.LuaSend(i % 2 == 0 ? "a" : "b", 0x100, [1]).Sent);

        Assert.Equal((false, "rate"), _device.LuaSend("a", 0x100, [1]));
        Assert.Contains($"#{_seq + 1}:tx_drop=1", Ask("lua status"));
    }

    [Fact]
    public void Lua2_TheRateIsCappedAtAThousand()
    {
        Ok("config set lua_tx_rate=1001");

        Assert.StartsWith($"#{_seq + 1}.ERR ARGS lua_tx_rate", Final(Ask("config save")), StringComparison.Ordinal);
    }

    [Fact]
    public void Lua3_LuaTransmitsShareTheTxLogTaggedWithTheScript()
    {
        EnableLuaTx();
        _device.LuaSend("speedo", 0x2C4, [0xAB]);

        Assert.Contains(Ask("can txlog"), l => l.EndsWith(" 2C4 s AB speedo", StringComparison.Ordinal));
    }

    [Fact]
    public void Lua4_BusOffSuspendsUntilResumed()
    {
        EnableLuaTx();

        _device.SuspendLuaTx("bus-off");
        Assert.Equal(["!LUA - tx suspended bus-off"], Drain());
        Assert.Equal((false, "tx suspended"), _device.LuaSend("a", 0x100, [1]));
        Assert.Contains($"#{_seq + 1}:tx=suspended", Ask("lua status"));

        Ok("lua resume-tx");
        Assert.True(_device.LuaSend("a", 0x100, [1]).Sent);
    }

    [Fact]
    public void Lua5_StopIsTheKillSwitch()
    {
        _device.LuaScripts["a"] = "print('a')"u8.ToArray();
        _device.LuaScripts["b"] = "print('b')"u8.ToArray();
        Ok("lua run a");
        Ok("lua run b");

        List<string> lines = Ask("lua stop");

        Assert.Contains("!LUA a state stopped", lines);
        Assert.Contains("!LUA b state stopped", lines);
        Assert.All(_device.LuaStates.Values, s => Assert.Equal("stopped", s));
    }

    [Fact]
    public void Lua5_SavingWithTransmitDisabledStopsEveryScript()
    {
        EnableLuaTx();
        _device.LuaScripts["a"] = "x"u8.ToArray();
        Ok("lua run a");

        Ok("config set lua_can_tx=disable");
        Assert.Contains("!LUA a state stopped", Ask("config save"));
    }

    [Fact]
    public void LuaPutTruncatesAtOffsetZeroAndReportsTheSize()
    {
        // Contract 8.7: OK n=0 <file_total_bytes>.
        Assert.Equal($"#{_seq + 1}.OK n=0 5", Final(Ask($"lua put s 0 {Convert.ToBase64String("hello"u8)}")));
        Assert.Equal($"#{_seq + 1}.OK n=0 11", Final(Ask($"lua put s 5 {Convert.ToBase64String(" world"u8)}")));
        Assert.Equal($"#{_seq + 1}.OK n=0 2", Final(Ask($"lua put s 0 {Convert.ToBase64String("hi"u8)}")));

        Assert.Equal("hi"u8.ToArray(), _device.LuaScripts["s"]);
    }

    [Fact]
    public void LuaRunReportsACompileErrorAsArgs()
    {
        _device.LuaScripts["bad"] = "local x = 1\n@@syntax"u8.ToArray();

        Assert.Equal($"#{_seq + 1}.ERR ARGS bad:2: syntax error near '@@'", Final(Ask("lua run bad")));
    }

    [Fact]
    public void AnEvalsSendsObeyTheLuaRulesNotTheArming()
    {
        Ok("can start");
        Ok("can arm");

        // Armed for WCP, but lua_can_tx is off: the eval's send is refused.
        Assert.Contains($"#{_seq + 1}:nil\tlua_can_tx disabled", Ask("lua eval " + B64("can.send(0x7E0, \"\\x01\")")));
    }
}
