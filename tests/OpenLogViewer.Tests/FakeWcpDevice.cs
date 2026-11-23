using System.Globalization;
using System.Text;
using OpenLogViewer.Core;

namespace OpenLogViewer.Tests;

/// <summary>
/// A WiCAN Pro running the custom WCP firmware, in software — and, behind it, the
/// ELM327 chip and a car (a <see cref="FakeElm"/>).
///
/// <para>
/// <b>Meant to be read by the firmware's author as well as run by these tests.</b>
/// Every rule in the contract's "CAN, transmit" section (the light gate of
/// 2026-11-22) and its Lua transmit rules (section 7.3) is implemented here the
/// way the firmware must implement it, and <c>WcpFakeDeviceTests</c> asserts each
/// one against this class directly, speaking raw WCP lines. Lua itself is not:
/// a script here is bytes with a state, and <c>lua eval</c> understands only
/// <c>print("…")</c>, <c>can.send(id, "…")</c> and <c>error("…")</c>. So this file is an
/// executable reading of <c>diagnosis/wican-claude-protocol.md</c>; where the
/// contract left something open, the choice made is marked <b>CHOICE</b> in a
/// comment, and those are the places the firmware should be checked against.
/// </para>
///
/// <para>
/// <b>Time.</b> The clock is injectable. Tests that check a TTL or a token bucket
/// use a manual clock and <see cref="Advance"/>; tests of the host's keep-alive
/// use the real one, with a background tick that does what the firmware's timer
/// task would. The idle disarm is a property so those tests need not wait 10 s.
/// </para>
/// </summary>
internal sealed class FakeWcpDevice : IEcuTransport, IReadsAvailable
{
    private readonly object _gate = new();
    private readonly Queue<byte> _out = new();
    private readonly StringBuilder _in = new();
    private readonly Timer? _ticker;
    private DateTime _manualNow = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _bootedAt;

    /// <param name="manualClock">
    /// True for a clock that only moves when <see cref="Advance"/> says so. False
    /// for the real one, with a 20 ms tick enforcing the keep-alive and the TTL.
    /// </param>
    public FakeWcpDevice(FakeElm? elm = null, bool manualClock = true)
    {
        Elm = elm ?? new FakeElm();
        ManualClock = manualClock;
        _bootedAt = Now;

        Saved = new Dictionary<string, string>(DefaultConfig, StringComparer.Ordinal);
        Staged = new Dictionary<string, string>(Saved, StringComparer.Ordinal);

        if (!manualClock) _ticker = new Timer(_ => Tick(), null, 20, 20);
    }

    public FakeElm Elm { get; }

    public bool ManualClock { get; }

    public DateTime Now => ManualClock ? _manualNow : DateTime.UtcNow;

    /// <summary>Moves the manual clock on, and runs the timers as the firmware's would.</summary>
    public void Advance(TimeSpan by)
    {
        lock (_gate) _manualNow += by;
        Tick();
    }

    /// <summary>Moves the manual clock on without running the timers, as if the timer task were late.</summary>
    public void Skip(TimeSpan by)
    {
        lock (_gate) _manualNow += by;
    }

    // ----- what the tests can set ------------------------------------------------

    /// <summary>Behave as stock firmware: every line, <c>#</c> or not, goes to the ELM327 chip.</summary>
    public bool Stock { get; set; }

    /// <summary>Answer the next this-many requests <c>ERR CRC</c> without running them.</summary>
    public int RejectWithCrc { get; set; }

    /// <summary>Run the next this-many requests and lose their replies, as a dropped USB burst would.</summary>
    public int LoseReplies { get; set; }

    /// <summary>Lose the next this-many data lines whole, terminator and all.</summary>
    public int LoseDataLines { get; set; }

    /// <summary>Damage one byte of the next this-many data lines, so their CRC fails.</summary>
    public int DamageDataLines { get; set; }

    /// <summary>Hold the CAN stream in the ring, as when the USB side cannot keep up.</summary>
    public bool HoldStream { get; set; }

    /// <summary>How log chunks are base64'd: 150 bytes to a line, or one string cut at 200 characters.</summary>
    public bool Base64PerLine { get; set; } = true;

    /// <summary>Lines to slip in after the next CR of ELM327 output, to test interleaving.</summary>
    public List<string> BetweenElmLines { get; } = [];

    // ----- what the tests can read -----------------------------------------------

    /// <summary>Every WCP command run, without its seq or CRC, in order.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Every ELM327 line passed to the chip.</summary>
    public List<string> ElmLines { get; } = [];

    /// <summary>Every frame actually put on the bus.</summary>
    public List<(uint Id, bool Extended, byte[] Data)> Transmitted { get; } = [];

    public Dictionary<string, string> Saved { get; }

    public Dictionary<string, string> Staged { get; }

    /// <summary>Files on the SD card, by name.</summary>
    public Dictionary<string, byte[]> LogFiles { get; } = new(StringComparer.Ordinal);

    public int Reboots { get; private set; }

    // ----- state the contract defines --------------------------------------------

    public bool CanRunning { get; private set; }

    public int Bitrate { get; private set; } = 500_000;

    public bool Streaming { get; private set; }

    public bool Armed { get; private set; }

    public bool Unlocked { get; private set; }

    public bool AutopidPaused { get; private set; }

    private DateTime _lastRequest;
    private DateTime _lastElm = DateTime.MinValue;
    private uint? _filterId;
    private uint _filterMask;
    private readonly Queue<string> _ring = new();
    private long _drops;
    private long _dropsTotal;
    private long _rx;
    private long _tx;
    private readonly List<string> _txLog = [];

    /// <summary>The WCP transmit cap: 200 frames a second overall, for runaway protection only.</summary>
    private readonly TokenBucket _overall = new(WcpTxRate);

    /// <summary>The Lua transmit cap, <c>lua_tx_rate</c> across all scripts; rebuilt when the rate changes.</summary>
    private TokenBucket _luaBucket = new(100);
    private double _luaRate = 100;
    private long _luaTx;
    private long _luaTxDrop;

    public const int RingCapacity = 512;
    public const int WcpTxRate = 200;
    public const int MaxLuaTxRate = 1000;
    public const int TxLogLength = 32;

    /// <summary>No WCP request for this long disarms. Ten seconds in the contract; shortened by real-time tests.</summary>
    public TimeSpan IdleDisarm { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Stored scripts, by name, as <c>/littlefs/lua/&lt;name&gt;.lua</c> holds them.</summary>
    public Dictionary<string, byte[]> LuaScripts { get; } = new(StringComparer.Ordinal);

    /// <summary>Each loaded script's state: running, stopped or error.</summary>
    public Dictionary<string, string> LuaStates { get; } = new(StringComparer.Ordinal);

    /// <summary>Why Lua transmit is suspended (bus-off), or null.</summary>
    public string? LuaTxSuspended { get; private set; }

    /// <summary>
    /// A subset of a v4.50p <c>config.json</c>, enough for validation and masking
    /// to mean something. Values are strings, as in the file.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultConfig { get; } = new Dictionary<string, string>
    {
        ["wifi_mode"] = "AP",
        ["ap_ch"] = "6",
        ["ap_pass"] = "@meatpi#",
        ["sta_ssid"] = "",
        ["sta_pass"] = "homesecret",
        ["can_datarate"] = "500K",
        ["can_mode"] = "normal",
        ["port_type"] = "tcp",
        ["port"] = "35000",
        ["protocol"] = "elm327",
        ["ble_pass"] = "123456",
        ["ble_status"] = "disable",
        ["sleep_status"] = "disable",
        ["sleep_volt"] = "13.1",
        ["logger_status"] = "disable",
        ["lua_can_tx"] = "disable",
        ["lua_tx_rate"] = "100",
        ["lua_autostart"] = "",
        ["lua_mem_kb"] = "256",
        ["lua_cb_ms"] = "50",
    };

    /// <summary>Which keys are passwords. Never sent in the clear except by <c>config secrets</c> after <c>config unlock</c>.</summary>
    public static bool IsSecret(string key) => key.EndsWith("_pass", StringComparison.Ordinal);

    public Dictionary<string, string> Autopid { get; } = new(StringComparer.Ordinal)
    {
        ["ENGINE_RPM"] = "812",
        ["COOLANT_TEMP"] = "88",
        ["VEHICLE_SPEED"] = "0",
        ["INTAKE_AIR_TEMP"] = "31",
        ["FUEL_TANK_LEVEL"] = "54.1",
        ["CONTROL_MODULE_VOLTAGE"] = "14.2",
        ["THROTTLE_POSITION"] = "14.9",
        ["MAF_AIR_FLOW_RATE"] = "3.42",
        ["SHORT_TERM_FUEL_TRIM_BANK_1"] = "-1.6",
        ["LONG_TERM_FUEL_TRIM_BANK_1"] = "3.1",
    };

    // ----- IEcuTransport ---------------------------------------------------------

    public bool IsOpen { get; private set; }

    public void Open()
    {
        lock (_gate)
        {
            IsOpen = true;
            _lastRequest = Now;
        }
    }

    /// <summary>
    /// The host going away. An unlock lasts "until disconnect or reboot". Arming
    /// is not ended here: the light gate ends on <c>can disarm</c>, a reboot or
    /// 10 s of silence, and Serial-B has no DTR line to see a disconnect by — so
    /// the idle timer is what notices, as it would on the real unit.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            IsOpen = false;
            Unlocked = false;
            Streaming = false;
            _out.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        _ticker?.Dispose();
        Close();
    }

    public void DiscardInput()
    {
        lock (_gate) _out.Clear();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!IsOpen) throw new InvalidOperationException("The port is not open.");

        var lines = new List<string>();

        lock (_gate)
        {
            foreach (byte b in data)
            {
                if (b is (byte)'\r' or (byte)'\n')
                {
                    if (_in.Length > 0) lines.Add(_in.ToString());
                    _in.Clear();
                }
                else
                {
                    _in.Append((char)b);
                }
            }
        }

        foreach (string line in lines) Receive(line);
    }

    public int Read(Span<byte> buffer, TimeSpan timeout) => ReadAvailable(buffer, timeout);

    public int ReadAvailable(Span<byte> buffer, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        lock (_gate)
        {
            while (_out.Count == 0 && IsOpen)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return 0;
                Monitor.Wait(_gate, left);
            }

            int n = 0;
            while (n < buffer.Length && _out.Count > 0) buffer[n++] = _out.Dequeue();
            return n;
        }
    }

    // ----- the bus ----------------------------------------------------------------

    /// <summary>
    /// A frame appears on the bus. Counted if the controller is running, and
    /// streamed if a stream is on and the filter passes it. While
    /// <see cref="HoldStream"/> is set it waits in the ring; a full ring counts a
    /// drop rather than blocking anything, as the contract requires of the TWAI task.
    /// </summary>
    public void BusFrame(uint id, bool extended, params byte[] data)
    {
        lock (_gate)
        {
            if (!CanRunning) return;

            _rx++;

            if (!Streaming) return;
            if (_filterId is { } fid && (id & _filterMask) != (fid & _filterMask)) return;

            string hex = Convert.ToHexString(data);
            string body = string.Create(CultureInfo.InvariantCulture,
                $"!CAN {(long)(Now - _bootedAt).TotalMilliseconds} {Id(id, extended)} {(extended ? "x" : "s")} {data.Length}{(hex.Length > 0 ? " " + hex : "")}");

            if (HoldStream)
            {
                if (_ring.Count >= RingCapacity)
                {
                    _drops++;
                    _dropsTotal++;
                }
                else
                {
                    _ring.Enqueue(body);
                }

                return;
            }

            FlushRingLocked();
            EmitLocked(body);
        }
    }

    /// <summary>Lets the ring drain, as when the USB side catches up.</summary>
    public void ReleaseStream()
    {
        lock (_gate)
        {
            HoldStream = false;
            FlushRingLocked();
        }
    }

    private void FlushRingLocked()
    {
        while (_ring.Count > 0) EmitLocked(_ring.Dequeue());

        if (_drops > 0)
        {
            EmitLocked(string.Create(CultureInfo.InvariantCulture, $"!DROP {_drops}"));
            _drops = 0;
        }
    }

    private static string Id(uint id, bool extended) =>
        extended ? id.ToString("X8", CultureInfo.InvariantCulture) : id.ToString("X3", CultureInfo.InvariantCulture);

    // ----- the firmware's timers --------------------------------------------------

    /// <summary>
    /// What the firmware's timer task does: disarm after <see cref="IdleDisarm"/>
    /// with no WCP request.
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (Armed && Now - _lastRequest > IdleDisarm) DisarmLocked("idle");
        }
    }

    private void DisarmLocked(string reason)
    {
        Armed = false;
        EmitLocked($"!DISARM {reason}");
    }

    // ----- receiving a line --------------------------------------------------------

    private void Receive(string line)
    {
        // Timers first, so a request that arrives after the idle time lapsed
        // finds the unit already disarmed rather than reviving it.
        Tick();

        if (Stock || line[0] != '#')
        {
            ToElm(line);
            return;
        }

        lock (_gate) HandleWcp(line);
    }

    private void ToElm(string line)
    {
        byte[] reply = new byte[8192];
        int got;

        lock (_gate)
        {
            ElmLines.Add(line);
            _lastElm = Now;

            Elm.Write(Encoding.ASCII.GetBytes(line + "\r"));
            got = Elm.Read(reply, TimeSpan.Zero);

            for (int i = 0; i < got; i++)
            {
                _out.Enqueue(reply[i]);

                if (reply[i] == (byte)'\r' && BetweenElmLines.Count > 0)
                {
                    foreach (string body in BetweenElmLines) EnqueueLineLocked(Wcp.Frame(body));
                    BetweenElmLines.Clear();
                }
            }

            Monitor.PulseAll(_gate);
        }
    }

    private void HandleWcp(string line)
    {
        if (line.Length > Wcp.MaxRequestBytes)
        {
            EmitLocked($"#{SeqOf(line)}.ERR TOOLONG request over {Wcp.MaxRequestBytes} bytes");
            return;
        }

        if (!Wcp.TryUnframe(line, out string body, out bool crcOk) || !crcOk)
        {
            EmitLocked($"#{SeqOf(line)}.ERR CRC");
            return;
        }

        int space = body.IndexOf(' ');
        if (space < 2 || !int.TryParse(body.AsSpan(1, space - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int seq)
            || seq is < 1 or > 65535)
        {
            EmitLocked("#0.ERR ARGS bad seq");
            return;
        }

        if (RejectWithCrc > 0)
        {
            RejectWithCrc--;
            EmitLocked($"#{seq}.ERR CRC");
            return;
        }

        // Any well-formed WCP request is a keep-alive, not only ping.
        _lastRequest = Now;

        string command = body[(space + 1)..];
        Commands.Add(command);

        var reply = new Reply(seq);
        Run(command, reply);

        if (LoseReplies > 0)
        {
            LoseReplies--;
            reply.Lines.Clear();
            reply.After.Clear();
        }

        foreach (string l in reply.Lines) EmitLocked(l, data: IsData(l));
        foreach (string l in reply.After) EmitLocked(l);

        if (reply.Reboot) RebootLocked();
    }

    private static bool IsData(string body)
    {
        int i = 1;
        while (i < body.Length && char.IsAsciiDigit(body[i])) i++;
        return i < body.Length && body[i] == ':';
    }

    private static string SeqOf(string line)
    {
        int i = 1;
        while (i < line.Length && i < 7 && char.IsAsciiDigit(line[i])) i++;
        return i > 1 && int.TryParse(line.AsSpan(1, i - 1), out int seq) && seq is >= 1 and <= 65535
            ? seq.ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private sealed class Reply(int seq)
    {
        public int Seq { get; } = seq;

        public List<string> Lines { get; } = [];

        /// <summary>Events that follow the final line.</summary>
        public List<string> After { get; } = [];

        public bool Reboot { get; set; }

        public void Data(string text)
        {
            // Data lines are at most 200 characters before the CRC; longer text
            // is split across lines, wherever the split falls.
            for (int at = 0; at < Math.Max(1, text.Length); at += Wcp.MaxDataText)
                Lines.Add($"#{Seq}:{text.Substring(at, Math.Min(Wcp.MaxDataText, text.Length - at))}");
        }

        public void Ok(string summary = "") => Lines.Add(summary.Length > 0 ? $"#{Seq}.OK {summary}" : $"#{Seq}.OK");

        public void Err(string code, string message = "") =>
            Lines.Add(message.Length > 0 ? $"#{Seq}.ERR {code} {message}" : $"#{Seq}.ERR {code}");
    }

    private void EmitLocked(string body, bool data = false)
    {
        if (data && LoseDataLines > 0)
        {
            LoseDataLines--;
            return;
        }

        string framed = Wcp.Frame(body);

        if (data && DamageDataLines > 0)
        {
            DamageDataLines--;

            // A byte lost from the middle, the failure the CRC exists for.
            framed = framed.Remove(framed.Length / 2, 1);
        }

        EnqueueLineLocked(framed);
    }

    private void EnqueueLineLocked(string framed)
    {
        if (!IsOpen) return;

        foreach (char c in framed + "\r\n") _out.Enqueue((byte)c);
        Monitor.PulseAll(_gate);
    }

    // ----- the commands -----------------------------------------------------------

    private void Run(string command, Reply r)
    {
        string[] words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0] : "";
        string sub = words.Length > 1 ? words[1] : "";

        switch (verb)
        {
            case "hello":
                r.Data($"wcp={Wcp.Version}");
                r.Data("fw=v4.50p+wcp1");
                r.Data("device=wican_pro_f412fa6c");
                r.Data("obd=STN2120 v5.8.1");
                r.Data("caps=config,console,autopid,log,can,can-tx");
                r.Ok();
                return;

            case "ping":
                r.Ok(((long)(Now - _bootedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                return;

            case "status":
                Status(r);
                return;

            case "reboot":
                r.Ok();
                r.Reboot = true;
                return;

            case "config":
                Config(command, sub, words, r);
                return;

            case "console":
                ConsoleCommand(command.Length > 8 ? command[8..] : "", r);
                return;

            case "autopid":
                AutopidCommand(sub, words, r);
                return;

            case "log":
                Log(sub, words, r);
                return;

            case "can":
                Can(sub, words, r);
                return;

            case "lua":
                LuaCommand(command, sub, words, r);
                return;

            default:
                r.Err("UNKNOWN", verb);
                return;
        }
    }

    private string AutopidState =>
        AutopidPaused || Now - _lastElm < TimeSpan.FromSeconds(5) ? "paused" : "running";

    private void Status(Reply r)
    {
        r.Data($"uptime_ms={(long)(Now - _bootedAt).TotalMilliseconds}");
        r.Data("volt=12.6");
        r.Data("wifi=disconnected");
        r.Data("ip=192.168.0.10");
        r.Data("ssid=WiCAN_f412fa6c");
        r.Data($"can={(CanRunning ? "listen" : "off")}");
        r.Data($"bitrate={Bitrate}");
        r.Data($"can_rx={_rx}");
        r.Data($"can_tx={_tx}");
        r.Data($"can_drop={_dropsTotal}");
        r.Data($"stream={(Streaming ? "on" : "off")}");
        r.Data($"armed={(Armed ? 1 : 0)}");

        // CHOICE: the contract still lists armed_ttl_s, though the light gate has
        // no TTL; reported as the seconds left before the idle disarm.
        r.Data($"armed_ttl_s={(Armed ? Math.Max(0, (int)Math.Ceiling((IdleDisarm - (Now - _lastRequest)).TotalSeconds)) : 0)}");
        r.Data($"autopid={AutopidState}");
        r.Data($"logger={(Saved["logger_status"] == "enable" ? "on" : "off")}");
        r.Data("sd=ok");
        r.Data("heap=182340");
        r.Data("psram=7864320");
        r.Ok();
    }

    private string Shown(string key, string value) => IsSecret(key) ? "****" : value;

    private void Config(string command, string sub, string[] words, Reply r)
    {
        switch (sub)
        {
            case "get" when words.Length == 2:
                foreach ((string k, string v) in Staged) r.Data($"{k}={Shown(k, v)}");
                r.Ok();
                return;

            case "get":
                if (!Staged.TryGetValue(words[2], out string? one))
                {
                    r.Err("ARGS", $"unknown key {words[2]}");
                    return;
                }

                r.Data($"{words[2]}={Shown(words[2], one)}");
                r.Ok();
                return;

            case "secrets":
                if (!Unlocked)
                {
                    r.Err("DENIED", "config unlock first");
                    return;
                }

                foreach ((string k, string v) in Staged) r.Data($"{k}={v}");
                r.Ok();
                return;

            case "unlock":
                Unlocked = true;
                r.Ok("secrets readable until disconnect or reboot");
                return;

            case "set":
            {
                // The value is the rest of the line, spaces and all.
                string rest = command.Length > "config set ".Length ? command["config set ".Length..] : "";
                int eq = rest.IndexOf('=');

                if (eq <= 0)
                {
                    r.Err("ARGS", "expected key=value");
                    return;
                }

                string key = rest[..eq];

                if (!Staged.ContainsKey(key))
                {
                    r.Err("ARGS", $"unknown key {key}");
                    return;
                }

                Staged[key] = rest[(eq + 1)..];
                r.Ok("staged");
                return;
            }

            case "diff":
                foreach ((string k, string v) in Staged)
                    if (Saved[k] != v) r.Data($"{k}={Shown(k, Saved[k])} -> {Shown(k, v)}");

                r.Ok();
                return;

            case "discard":
                foreach (string k in Saved.Keys) Staged[k] = Saved[k];
                r.Ok("discarded");
                return;

            case "save":
                if (Validate(Staged) is { } why)
                {
                    // Nothing is written: a save that would fail the boot-time
                    // check is the one thing that could factory-reset the unit.
                    r.Err("ARGS", why);
                    return;
                }

                foreach ((string k, string v) in Staged) Saved[k] = v;

                // Section 7.3.5: a save with lua_can_tx=disable is a kill switch.
                // CHOICE: lua_can_tx and lua_tx_rate take effect at the save, not
                // the reboot — the kill switch only means anything if they do.
                if (Saved["lua_can_tx"] == "disable") StopAllLuaLocked(r);

                r.Ok("saved; reboot to apply");
                if (words.Length > 2 && words[2] == "--reboot") r.Reboot = true;
                return;

            default:
                r.Err("ARGS", $"config {sub}?");
                return;
        }
    }

    /// <summary>
    /// The boot-time validation, as a function the save path can share. Returns
    /// "<c>key: why</c>" for the first problem, or null.
    /// </summary>
    public static string? Validate(IReadOnlyDictionary<string, string> c)
    {
        if (c["wifi_mode"] is not ("AP" or "APStation")) return "wifi_mode: must be AP or APStation";
        if (c["wifi_mode"] == "APStation" && c["sta_ssid"].Length == 0) return "sta_ssid: required in APStation mode";
        if (!int.TryParse(c["ap_ch"], out int ch) || ch is < 1 or > 13) return "ap_ch: 1 to 13";
        if (c["ap_pass"].Length is < 8 or > 63) return "ap_pass: 8 to 63 characters";
        if (!int.TryParse(c["port"], out int port) || port is < 1 or > 65535) return "port: 1 to 65535";
        if (c["port_type"] is not ("tcp" or "udp")) return "port_type: tcp or udp";
        if (c["can_mode"] is not ("normal" or "silent")) return "can_mode: normal or silent";
        if (c["can_datarate"] is not ("5K" or "10K" or "20K" or "25K" or "50K" or "100K" or "125K" or "250K" or "500K" or "800K" or "1000K"))
            return "can_datarate: not a supported rate";
        if (c["protocol"] is not ("elm327" or "slcan" or "savvycan" or "realdash66" or "auto_pid"))
            return "protocol: not a known protocol";
        if (c["ble_pass"].Length != 6 || !c["ble_pass"].All(char.IsAsciiDigit)) return "ble_pass: six digits";
        if (!double.TryParse(c["sleep_volt"], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v is < 10 or > 15)
            return "sleep_volt: 10 to 15";
        if (c["logger_status"] is not ("enable" or "disable")) return "logger_status: enable or disable";
        if (c["lua_can_tx"] is not ("enable" or "disable")) return "lua_can_tx: enable or disable";
        if (!int.TryParse(c["lua_tx_rate"], out int rate) || rate is < 1 or > MaxLuaTxRate) return $"lua_tx_rate: 1 to {MaxLuaTxRate}";

        return null;
    }

    private void ConsoleCommand(string line, Reply r)
    {
        switch (line.Trim())
        {
            case "system --voltage":
                r.Data("12.6V");
                r.Ok();
                return;

            case "wifi --status":
                r.Data("mode: AP");
                r.Data("ap: WiCAN_f412fa6c, 0 clients");
                r.Ok();
                return;

            case "sdcard --info":
                r.Data("mounted: yes");
                r.Data("free: 15.1 GB");
                r.Ok();
                return;

            default:
                r.Data($"unknown command: {line.Trim()}");
                r.Err("UNKNOWN", "console command not found");
                return;
        }
    }

    private void AutopidCommand(string sub, string[] words, Reply r)
    {
        switch (sub)
        {
            case "data":
                r.Data("{" + string.Join(",", Autopid.Select(p => $"\"{p.Key}\":{p.Value}")) + "}");
                r.Ok();
                return;

            case "get" when words.Length > 2:
                if (!Autopid.TryGetValue(words[2], out string? value))
                {
                    r.Err("NOTFOUND", words[2]);
                    return;
                }

                r.Data($"{words[2]}={value}");
                r.Ok();
                return;

            case "pause":
                AutopidPaused = true;
                r.Ok();
                return;

            case "resume":
                AutopidPaused = false;
                r.Ok();
                return;

            default:
                r.Err("ARGS", $"autopid {sub}?");
                return;
        }
    }

    private void Log(string sub, string[] words, Reply r)
    {
        switch (sub)
        {
            case "status":
                r.Data($"enabled={(Saved["logger_status"] == "enable" ? 1 : 0)}");
                r.Data($"file={LogFiles.Keys.LastOrDefault() ?? ""}");
                r.Data($"size={LogFiles.Values.LastOrDefault()?.Length ?? 0}");
                r.Data("sd_free=16212254720");
                r.Ok();
                return;

            case "list":
                foreach ((string name, byte[] bytes) in LogFiles) r.Data($"{name} {bytes.Length} 1763812800");
                r.Ok();
                return;

            case "read":
                LogRead(words, r);
                return;

            case "enable":
            case "disable":
                Staged["logger_status"] = sub;
                r.Ok("staged; config save and reboot to apply");
                return;

            default:
                r.Err("ARGS", $"log {sub}?");
                return;
        }
    }

    private void LogRead(string[] words, Reply r)
    {
        if (words.Length != 5
            || !long.TryParse(words[3], NumberStyles.None, CultureInfo.InvariantCulture, out long offset)
            || !int.TryParse(words[4], NumberStyles.None, CultureInfo.InvariantCulture, out int length))
        {
            r.Err("ARGS", "log read <name> <offset> <length>");
            return;
        }

        if (!LogFiles.TryGetValue(words[2], out byte[]? file))
        {
            r.Err("NOTFOUND", words[2]);
            return;
        }

        // CHOICE: more than 3 KB is clamped, not refused. The reply's byte count
        // says how much came, so a host asking for more is told.
        int take = (int)Math.Max(0, Math.Min(Math.Min(length, 3072), file.Length - offset));
        byte[] chunk = take > 0 ? file.AsSpan((int)offset, take).ToArray() : [];

        if (Base64PerLine)
        {
            // 150 bytes is exactly 200 base64 characters, with no padding until
            // the last line, so each line decodes on its own.
            for (int at = 0; at < chunk.Length; at += 150)
                r.Lines.Add($"#{r.Seq}:{Convert.ToBase64String(chunk, at, Math.Min(150, chunk.Length - at))}");
        }
        else if (chunk.Length > 0)
        {
            r.Data(Convert.ToBase64String(chunk));
        }

        r.Ok(string.Create(CultureInfo.InvariantCulture, $"{chunk.Length} {file.Length}"));
    }

    private void Can(string sub, string[] words, Reply r)
    {
        switch (sub)
        {
            case "status":
                r.Data($"mode={(CanRunning ? "listen" : "off")}");
                r.Data($"bitrate={Bitrate}");
                r.Data($"running={(CanRunning ? 1 : 0)}");
                r.Data($"rx={_rx}");
                r.Data($"tx={_tx}");
                r.Data($"drop={_dropsTotal}");
                r.Data("bus_errors=0");
                r.Data($"armed={(Armed ? 1 : 0)}");

                // CHOICE: the contract still lists allow=<ids>; the light gate
                // allows any identifier, so this says so.
                r.Data("allow=any");
                r.Data($"tx_rate={WcpTxRate}");
                r.Ok();
                return;

            case "start":
                if (words.Length > 2)
                {
                    if (!int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out int rate) || rate < 1)
                    {
                        r.Err("ARGS", "bitrate in bit/s");
                        return;
                    }

                    Bitrate = rate;
                }

                // Listen-only: starting the controller never makes it transmit.
                CanRunning = true;
                r.Ok($"listen {Bitrate}");
                return;

            case "stop":
                if (Saved["protocol"] is "slcan" or "savvycan")
                {
                    r.Err("STATE", $"{Saved["protocol"]} needs the controller");
                    return;
                }

                CanRunning = false;
                Streaming = false;
                r.Ok();
                return;

            case "stream":
                Stream(words, r);
                return;

            case "arm":
                Arm(words, r);
                return;

            case "disarm":
                // CHOICE: disarming when not armed is OK, not NOTARMED — the host
                // asked for the state the unit is already in. The event goes only
                // when something was armed.
                r.Ok();
                if (Armed)
                {
                    Armed = false;
                    r.After.Add("!DISARM host");
                }

                return;

            case "send":
                Send(words, r);
                return;

            case "txlog":
                foreach (string line in _txLog) r.Data(line);
                r.Ok();
                return;

            default:
                r.Err("ARGS", $"can {sub}?");
                return;
        }
    }

    private void Stream(string[] words, Reply r)
    {
        if (words.Length > 2 && words[2] == "off")
        {
            Streaming = false;
            r.Ok();
            return;
        }

        if (words.Length < 3 || words[2] != "on")
        {
            r.Err("ARGS", "can stream on [id mask] | off");
            return;
        }

        if (!CanRunning)
        {
            r.Err("STATE", "can start first");
            return;
        }

        if (words.Length == 5)
        {
            if (!WcpClient.TryParseId(words[3], out uint id) || !uint.TryParse(words[4], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out uint mask))
            {
                r.Err("ARGS", "id and mask in hex");
                return;
            }

            _filterId = id;
            _filterMask = mask;
        }
        else if (words.Length == 3)
        {
            _filterId = null;
        }
        else
        {
            r.Err("ARGS", "can stream on [id mask]");
            return;
        }

        Streaming = true;
        r.Ok();
    }

    /// <summary>
    /// <c>can arm</c> — rule 2: transmit on, for any identifier, until
    /// <c>can disarm</c>, a reboot, or <see cref="IdleDisarm"/> without a request.
    /// </summary>
    private void Arm(string[] words, Reply r)
    {
        if (words.Length != 2)
        {
            r.Err("ARGS", "can arm takes no arguments");
            return;
        }

        bool was = Armed;
        Armed = true;

        r.Ok("armed");

        // CHOICE: !ARMED carries no fields now there is no TTL or allowlist, and
        // is not repeated for an arm while already armed.
        if (!was) r.After.Add("!ARMED");
    }

    /// <summary>
    /// <c>can send &lt;id_hex&gt; [x] &lt;data_hex&gt;</c> — rules 3, 4 and 6: armed, a
    /// well-formed frame, then the 200 frames/s cap.
    /// </summary>
    private void Send(string[] words, Reply r)
    {
        if (!Armed)
        {
            r.Err("NOTARMED", "can arm first");
            return;
        }

        if (words.Length < 3 || !WcpClient.TryParseId(words[2], out uint id))
        {
            r.Err("ARGS", "can send <id_hex> [x] <data_hex>");
            return;
        }

        bool extended = words.Length > 3 && words[3] == "x";
        string hex = words.Length > (extended ? 4 : 3) ? words[extended ? 4 : 3] : "";

        if (words.Length > (extended ? 5 : 4))
        {
            r.Err("ARGS", "data is one hex string, no spaces");
            return;
        }

        if (!extended && id > 0x7FF)
        {
            r.Err("ARGS", "id over 7FF needs x");
            return;
        }

        if (hex.Length % 2 != 0 || hex.Length > 16 || !hex.All(char.IsAsciiHexDigit))
        {
            r.Err("ARGS", "0 to 8 data bytes in hex");
            return;
        }

        // CHOICE: a send while the controller is stopped is refused; arming does
        // not start it.
        if (!CanRunning)
        {
            r.Err("STATE", "can start first");
            return;
        }

        DateTime now = Now;

        if (!_overall.Has(now))
        {
            r.Err("DENIED", "rate");
            return;
        }

        _overall.Take(now);
        TransmitLocked(id, extended, Convert.FromHexString(hex), "wcp");
        r.Ok("sent");
    }

    private void TransmitLocked(uint id, bool extended, byte[] data, string by)
    {
        Transmitted.Add((id, extended, data));
        _tx++;

        _txLog.Add($"{(long)(Now - _bootedAt).TotalMilliseconds} {Id(id, extended)} {(extended ? "x" : "s")} {Convert.ToHexString(data)} {by}");
        if (_txLog.Count > TxLogLength) _txLog.RemoveAt(0);
    }

    // ----- Lua ----------------------------------------------------------------------

    /// <summary>
    /// A script calls <c>can.send(id, data, ext)</c> — section 7.3. Not subject to
    /// WCP arming: scripts run unattended. Instead, in this order: the master
    /// switch, a bus-off suspension, the controller running, then the
    /// <c>lua_tx_rate</c> bucket shared by every script.
    /// </summary>
    /// <returns>True and empty, or false and the error string the script would get.</returns>
    public (bool Sent, string Error) LuaSend(string script, uint id, byte[] data, bool extended = false)
    {
        lock (_gate) return LuaSendLocked(script, id, data, extended);
    }

    private (bool Sent, string Error) LuaSendLocked(string script, uint id, byte[] data, bool extended)
    {
        if (Saved["lua_can_tx"] != "enable") return (false, "lua_can_tx disabled");
        if (LuaTxSuspended is not null) return (false, "tx suspended");
        if (!CanRunning) return (false, "can stopped");
        if (data.Length > 8 || (!extended && id > 0x7FF)) return (false, "bad frame");

        double rate = double.Parse(Saved["lua_tx_rate"], CultureInfo.InvariantCulture);

        if (rate != _luaRate)
        {
            _luaRate = rate;
            _luaBucket = new TokenBucket(rate);
        }

        DateTime now = Now;

        if (!_luaBucket.Has(now))
        {
            _luaTxDrop++;
            return (false, "rate");
        }

        _luaBucket.Take(now);
        _luaTx++;
        TransmitLocked(id, extended, data, script);
        return (true, "");
    }

    /// <summary>A running script prints, as <c>print(...)</c> on the device would.</summary>
    public void LuaPrint(string script, string text)
    {
        lock (_gate) EmitLocked($"!LUA {script} print {text}");
    }

    /// <summary>The controller went bus-off: section 7.3.4 suspends all Lua transmit until <c>lua resume-tx</c>.</summary>
    public void SuspendLuaTx(string reason)
    {
        lock (_gate)
        {
            LuaTxSuspended = reason;
            EmitLocked($"!LUA - tx suspended {reason}");
        }
    }

    private void StopAllLuaLocked(Reply r)
    {
        foreach (string name in LuaStates.Where(p => p.Value == "running").Select(p => p.Key).ToList())
        {
            LuaStates[name] = "stopped";
            r.After.Add($"!LUA {name} state stopped");
        }
    }

    private void LuaCommand(string command, string sub, string[] words, Reply r)
    {
        string name = words.Length > 2 ? words[2] : "";

        switch (sub)
        {
            case "status":
            {
                string tx = LuaTxSuspended is not null ? "suspended" : Saved["lua_can_tx"] == "enable" ? "enabled" : "disabled";
                r.Data("engine=on");
                r.Data($"tx={tx}");
                r.Data($"tx_count={_luaTx}");
                r.Data($"tx_drop={_luaTxDrop}");

                foreach ((string n, string state) in LuaStates) r.Data($"{n} {state} 41 3 0 0");

                r.Ok();
                return;
            }

            case "list":
                foreach ((string n, byte[] bytes) in LuaScripts) r.Data($"{n} {bytes.Length}");
                r.Ok();
                return;

            case "put":
            {
                if (words.Length != 5 || !WcpClient.IsScriptName(name)
                    || !int.TryParse(words[3], NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
                {
                    r.Err("ARGS", "lua put <name> <offset> <base64>");
                    return;
                }

                byte[] chunk;

                try
                {
                    chunk = Convert.FromBase64String(words[4]);
                }
                catch (FormatException)
                {
                    r.Err("ARGS", "not base64");
                    return;
                }

                byte[] existing = offset == 0 ? [] : LuaScripts.GetValueOrDefault(name, []);

                // CHOICE: a chunk may rewrite what is there or extend it, but not
                // leave a gap.
                if (offset > existing.Length)
                {
                    r.Err("ARGS", $"offset {offset} past the end ({existing.Length})");
                    return;
                }

                byte[] file = new byte[Math.Max(existing.Length, offset + chunk.Length)];
                existing.CopyTo(file, 0);
                chunk.CopyTo(file, offset);
                LuaScripts[name] = file;

                // CHOICE: OK <bytes> is the file's size after this chunk.
                r.Ok(file.Length.ToString(CultureInfo.InvariantCulture));
                return;
            }

            case "get":
                if (!LuaScripts.TryGetValue(name, out byte[]? script))
                {
                    r.Err("NOTFOUND", name);
                    return;
                }

                for (int at = 0; at < script.Length; at += 150)
                    r.Lines.Add($"#{r.Seq}:{Convert.ToBase64String(script, at, Math.Min(150, script.Length - at))}");

                r.Ok();
                return;

            case "delete":
                if (!LuaScripts.Remove(name))
                {
                    r.Err("NOTFOUND", name);
                    return;
                }

                if (LuaStates.Remove(name, out string? was) && was == "running") r.After.Add($"!LUA {name} state stopped");

                r.Ok();
                return;

            case "run":
            {
                if (!LuaScripts.TryGetValue(name, out byte[]? source))
                {
                    r.Err("NOTFOUND", name);
                    return;
                }

                string text = Encoding.UTF8.GetString(source);
                int bad = text.IndexOf("@@syntax", StringComparison.Ordinal);

                if (bad >= 0)
                {
                    int lineNo = text[..bad].Count(c => c == '\n') + 1;
                    r.Err("ARGS", $"{name}:{lineNo}: syntax error near '@@'");
                    return;
                }

                LuaStates[name] = "running";
                r.Ok();
                r.After.Add($"!LUA {name} state running");
                return;
            }

            case "stop":
                if (words.Length == 2)
                {
                    StopAllLuaLocked(r);
                    r.Ok("all stopped");
                    return;
                }

                if (LuaStates.TryGetValue(name, out string? st) && st == "running")
                {
                    LuaStates[name] = "stopped";
                    r.After.Add($"!LUA {name} state stopped");
                }

                r.Ok();
                return;

            case "eval":
                Eval(command.Length > "lua eval ".Length ? command["lua eval ".Length..] : "", r);
                return;

            case "resume-tx":
                LuaTxSuspended = null;
                r.Ok("lua tx resumed");
                return;

            default:
                r.Err("ARGS", $"lua {sub}?");
                return;
        }
    }

    /// <summary>
    /// A very small reading of <c>lua eval</c>: statements split on <c>;</c>, of
    /// which <c>print("…")</c>, <c>can.send(0xID, "\xNN…")</c> and <c>error("…")</c>
    /// do something. Enough to show a host what an eval's output and failure look
    /// like, and that an eval's sends obey the Lua transmit rules.
    /// </summary>
    private void Eval(string code, Reply r)
    {
        foreach (string raw in code.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("print(\"", StringComparison.Ordinal) && raw.EndsWith("\")", StringComparison.Ordinal))
            {
                r.Data(raw[7..^2]);
            }
            else if (raw.StartsWith("error(\"", StringComparison.Ordinal))
            {
                r.Err("ARGS", $"eval:1: {raw[7..raw.LastIndexOf('"')]}");
                return;
            }
            else if (raw.StartsWith("can.send(0x", StringComparison.Ordinal))
            {
                int comma = raw.IndexOf(',');
                uint id = uint.Parse(raw[11..comma], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                string lit = raw[(raw.IndexOf('"') + 1)..raw.LastIndexOf('"')];
                byte[] data = [.. lit.Split("\\x", StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => byte.Parse(h, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))];

                (bool sent, string error) = LuaSendLocked("eval", id, data, false);
                r.Data(sent ? "true" : $"nil\t{error}");
            }
        }

        r.Ok();
    }

    private void RebootLocked()
    {
        Reboots++;

        // Back to boot state: staged changes gone, listen-only, unarmed, locked,
        // no script running (autostart is not modelled).
        foreach (string k in Saved.Keys) Staged[k] = Saved[k];
        Armed = false;
        LuaStates.Clear();
        LuaTxSuspended = null;
        Unlocked = false;
        Streaming = false;
        CanRunning = false;
        AutopidPaused = false;
    }

    /// <summary>
    /// A token bucket of <c>rate</c> tokens a second, holding at most <c>rate</c>.
    /// CHOICE: the burst equals the rate — 200 WCP frames may go at once, then one
    /// every 5 ms.
    /// </summary>
    private sealed class TokenBucket(double perSecond)
    {
        private readonly double _rate = perSecond;
        private double _tokens = perSecond;
        private DateTime _at = DateTime.MinValue;

        private void Refill(DateTime now)
        {
            if (_at != DateTime.MinValue) _tokens = Math.Min(_rate, _tokens + (now - _at).TotalSeconds * _rate);
            _at = now;
        }

        public bool Has(DateTime now)
        {
            Refill(now);
            return _tokens >= 1;
        }

        public void Take(DateTime now)
        {
            Refill(now);
            _tokens -= 1;
        }
    }
}
