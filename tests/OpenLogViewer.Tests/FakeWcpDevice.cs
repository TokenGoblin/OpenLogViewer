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
/// a script here is bytes with a state, and <c>lua eval</c> (base64, contract 8.6) understands only
/// <c>print("…")</c>, <c>can.send(id, "…")</c> and <c>error("…")</c>. So this file is an
/// executable reading of <c>diagnosis/wican-claude-protocol.md</c>; where the
/// contract still leaves something open after its section 8, the choice made is marked <b>CHOICE</b> in a
/// comment, and those are the places the firmware should be checked against.
/// </para>
///
/// <para>
/// <b>After the review fixes of 2026-11-22.</b> Two firmware agents were fixing the
/// device while this was brought up to date, so it models the device as the fix
/// list (<c>diagnosis/wcp-review-fixes.md</c>) says it will be: a store of the last
/// eight final replies, answered again for a repeated request (F17); <c>lua put</c>
/// refusing an overlap (8.12); <c>hello</c> answered <c>ERR BUSY</c> while booting; a
/// 360-byte eval (8.11) and <c>ERR STATE timeout</c>; escaped script output (F11);
/// <c>stack_free</c> (F10); long settings cut with <c>~</c> and read whole with
/// <c>--b64</c>; staged <c>****</c> refused (F19); <c>ERR STATE changed</c> and a stage
/// dropped after 10 s of silence (F24), as is an unlock; the <c>x</c> stream filter;
/// arming refused <c>ERR STATE no-traffic</c> on a silent bus and <c>rx_recent</c> (F27);
/// and the transmit fault latch, <c>!DISARM fault &lt;reason&gt;</c> and
/// <c>can resume</c> (F26). The exact words are the host's guesses, kept in
/// <see cref="WcpWire"/>; the firmware agents' reports say what they really are.
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

    /// <summary>A car behind the chip: the same one the OBD2 tests use.</summary>
    public static FakeElm Car()
    {
        var car = new FakeElm();

        car.Answers[0x00] = [0b1001_1000, 0b0011_1010, 0b1000_0000, 0b0000_0001];
        car.Answers[0x20] = [0b0000_0000, 0b0000_0000, 0b0000_0000, 0b0000_0001];
        car.Answers[0x40] = [0b0100_0000, 0b0000_0000, 0b0000_0000, 0b0000_0000];

        car.Answers[0x01] = [0x00, 0x07, 0x65, 0x04];
        car.Answers[0x04] = [0x7F];
        car.Answers[0x05] = [0x5A];
        car.Answers[0x0B] = [0x64];
        car.Answers[0x0C] = [0x1A, 0xF8];
        car.Answers[0x0D] = [0x40];
        car.Answers[0x0F] = [0x46];
        car.Answers[0x11] = [0x33];
        car.Answers[0x42] = [0x37, 0x1A];

        return car;
    }

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

    /// <summary>
    /// Answer the next this-many <c>hello</c>s <c>ERR BUSY booting</c>, as a unit
    /// still coming up does (review item H2).
    /// </summary>
    public int BusyHellos { get; set; }

    /// <summary>Answer no WCP request at all, as a unit in the middle of restarting.</summary>
    public bool Silent { get; set; }

    /// <summary>
    /// Keep the last eight final replies and answer a repeated request from them
    /// without running it again (review item F17). On by default, as the fixed
    /// firmware is; off to stand for a firmware without the store, or one whose
    /// store has moved on.
    /// </summary>
    public bool ReplayStore { get; set; } = true;

    /// <summary>
    /// A car on the bus, sending all the time, while the controller runs: what
    /// arming's traffic check sees (review item F27). Off for a bus with nothing
    /// on it, where only frames given to <see cref="BusFrame(uint, bool, byte[])"/> count.
    /// </summary>
    public bool BusChatter { get; set; } = true;

    /// <summary>Fail the next WCP send with <c>ERR IO tx-timeout</c>, latching a transmit fault (review item F26).</summary>
    public bool FailNextSend { get; set; }

    /// <summary>
    /// Lose the reply — run or answered from the store — to the next this-many
    /// requests whose command starts with the given text.
    /// </summary>
    public void LoseRepliesTo(string commandPrefix, int count = 1)
    {
        lock (_gate) _loseTo = (commandPrefix, count);
    }

    /// <summary>
    /// Lose the next this-many requests whose command starts with the given text
    /// on the way in, before they run: a request the device never saw.
    /// </summary>
    public void DropRequestsTo(string commandPrefix, int count = 1)
    {
        lock (_gate) _dropTo = (commandPrefix, count);
    }

    private (string Prefix, int Count) _loseTo = ("", 0);
    private (string Prefix, int Count) _dropTo = ("", 0);

    private static bool Take(ref (string Prefix, int Count) rule, string command)
    {
        if (rule.Count <= 0 || !command.StartsWith(rule.Prefix, StringComparison.Ordinal)) return false;

        rule.Count--;
        return true;
    }

    /// <summary>Every well-formed request received, seq and command, without its CRC — including those not run.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>How log chunks are base64'd: 120 bytes to a line, or one string cut at 160 characters (contract 8.8).</summary>
    public bool Base64PerLine { get; set; } = true;

    /// <summary>
    /// Lines to send straight after the next ELM327 prompt, as WCP output queued
    /// during a reply is flushed (contract 8.2) — with the CR LF the firmware puts
    /// in front of a WCP line that does not follow an LF.
    /// </summary>
    public List<string> AfterElmPrompt { get; } = [];

    // ----- what the tests can read -----------------------------------------------

    /// <summary>Every WCP command run, without its seq or CRC, in order. A request answered from the reply store is not run, and is not here.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Every request answered from the reply store rather than run, without its CRC.</summary>
    public List<string> Replayed { get; } = [];

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

    /// <summary>The controller's bitrate, in kbit/s (contract 8.8: <c>can start 500</c>).</summary>
    public int Bitrate { get; private set; } = 500;

    public bool Streaming { get; private set; }

    public bool Armed { get; private set; }

    public bool Unlocked { get; private set; }

    public bool AutopidPaused { get; private set; }

    /// <summary>The latched transmit fault, or null (review item F26).</summary>
    public string? TxFault { get; private set; }

    private DateTime _lastRequest;

    /// <summary>The last eight (request, final line) pairs, newest last.</summary>
    private readonly List<(string Request, string Final)> _replies = [];
    public const int ReplyStoreSize = 8;

    /// <summary>When each frame of the last second was received, for <c>rx_recent</c>.</summary>
    private readonly Queue<DateTime> _rxTimes = new();

    /// <summary>What <see cref="BusChatter"/> stands for, in frames a second.</summary>
    public const int ChatterRate = 1200;

    /// <summary>Whether a script's sends hold the controller in normal mode.</summary>
    private bool _luaNormal;
    private DateTime _lastLuaTx;

    /// <summary>When the settings were staged from, by version: a change outside WCP since then refuses the save.</summary>
    private int _savedVersion;
    private int _stagedFromVersion;
    private DateTime _lastElm = DateTime.MinValue;
    private uint? _filterId;
    private uint _filterMask;
    private bool _filterExtended;
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
        ["sta_fallbacks"] = "[]",
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
    public void BusFrame(uint id, bool extended, params byte[] data) => BusFrame(id, extended, false, data.Length, data);

    /// <summary>A remote (RTR) frame: a DLC and no data, flagged <c>sr</c> or <c>xr</c> (contract 8.8).</summary>
    public void BusRemoteFrame(uint id, bool extended, int dlc) => BusFrame(id, extended, true, dlc, []);

    private void BusFrame(uint id, bool extended, bool remote, int dlc, byte[] data)
    {
        lock (_gate)
        {
            if (!CanRunning) return;

            _rx++;
            _rxTimes.Enqueue(Now);

            if (!Streaming) return;

            // CHOICE: a filter is for one kind of frame, standard or extended (the
            // x flag), as a TWAI acceptance filter is.
            if (_filterId is { } fid && (extended != _filterExtended || (id & _filterMask) != (fid & _filterMask))) return;

            string hex = Convert.ToHexString(data);
            string body = string.Create(CultureInfo.InvariantCulture,
                $"!CAN {(long)(Now - _bootedAt).TotalMilliseconds} {Id(id, extended)} {(extended ? "x" : "s")}{(remote ? "r" : "")} {dlc}{(hex.Length > 0 ? " " + hex : "")}");

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
    /// What the firmware's timer task does after <see cref="IdleDisarm"/> with no
    /// WCP request — which it counts as the host disconnecting: disarm, end an
    /// unlock, and drop the staged settings (review items F24 and H3).
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (Now - _lastRequest <= IdleDisarm) return;

            if (Armed) DisarmLocked("idle");

            Unlocked = false;
            DiscardStagedLocked();
        }
    }

    private void DiscardStagedLocked()
    {
        foreach (string k in Saved.Keys) Staged[k] = Saved[k];
        _stagedFromVersion = _savedVersion;
        _hasStage = false;
    }

    private bool _hasStage;

    /// <summary>
    /// The settings changed outside WCP — the web page, or another client — so a
    /// save of what was staged before would write over it (review item F24).
    /// </summary>
    public void ChangeConfigOutside(string key, string value)
    {
        lock (_gate)
        {
            Saved[key] = value;
            _savedVersion++;
        }
    }

    /// <summary>Frames received in the last second, as <c>rx_recent</c> reports them.</summary>
    private long RxRecentLocked()
    {
        if (!CanRunning) return 0;

        while (_rxTimes.Count > 0 && Now - _rxTimes.Peek() > TimeSpan.FromSeconds(1)) _rxTimes.Dequeue();

        return _rxTimes.Count + (BusChatter ? ChatterRate : 0);
    }

    /// <summary>
    /// A transmit failure latches a transmit inhibit (review item F26): WCP is
    /// disarmed with <c>!DISARM fault &lt;reason&gt;</c>, Lua transmit is suspended,
    /// and the controller goes back to listen-only, until <c>can resume</c> or
    /// <c>lua resume-tx</c>.
    /// </summary>
    public void LatchFault(string reason)
    {
        lock (_gate) LatchFaultLocked(reason);
    }

    private void LatchFaultLocked(string reason)
    {
        TxFault = reason;
        _luaNormal = false;

        if (Armed)
        {
            Armed = false;
            EmitLocked($"!DISARM {WcpWire.Fault} {reason}");
        }

        if (LuaTxSuspended is null)
        {
            LuaTxSuspended = reason;
            EmitLocked($"!LUA - tx suspended {reason}");
        }
    }

    /// <summary>Says <c>!BOOT</c> without anything else happening, as a unit that has just finished restarting does.</summary>
    public void AnnounceBoot()
    {
        lock (_gate) EmitLocked("!BOOT v4.50p+wcp1");
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
                _lastOut = reply[i];

                if (reply[i] == (byte)'>' && AfterElmPrompt.Count > 0)
                {
                    foreach (string body in AfterElmPrompt) EnqueueLineLocked(Wcp.Frame(body));
                    AfterElmPrompt.Clear();
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

        Requests.Add(body);

        if (Silent) return;

        if (Take(ref _dropTo, body[(space + 1)..])) return;

        // ERR CRC is never stored: the request was not run, and its seq may be
        // the one thing about it that was wrong.
        if (RejectWithCrc > 0)
        {
            RejectWithCrc--;
            EmitLocked($"#{seq}.ERR CRC");
            return;
        }

        // Any well-formed WCP request is a keep-alive, not only ping.
        _lastRequest = Now;

        string command = body[(space + 1)..];

        // F17: a request seen again is answered from the store, not run again.
        // CHOICE: the store is keyed on the whole request, seq and command, not
        // the seq alone — so a new host session that happens to reuse a seq for
        // something else is not handed another command's answer. The fix list
        // says "seq"; this is the safer reading, and worth asking the firmware for.
        if (ReplayStore && _replies.FindLastIndex(r => r.Request == body) is int at and >= 0)
        {
            Replayed.Add(body);

            string stored = _replies[at].Final;

            if (LoseReplies > 0) LoseReplies--;
            else if (!Take(ref _loseTo, command)) EmitLocked(stored);

            return;
        }

        Commands.Add(command);

        var reply = new Reply(seq);
        Run(command, reply);

        if (reply.Lines.LastOrDefault(l => !IsData(l)) is { } final && !reply.Busy)
        {
            _replies.Add((body, final));
            if (_replies.Count > ReplyStoreSize) _replies.RemoveAt(0);
        }

        if (LoseReplies > 0 || Take(ref _loseTo, command))
        {
            if (LoseReplies > 0) LoseReplies--;
            reply.Lines.Clear();
            reply.After.Clear();
        }

        foreach (string l in reply.Lines) EmitLocked(l, data: IsData(l));
        foreach (string l in reply.After) EmitLocked(l);

        if (reply.Reboot)
        {
            RebootLocked();

            // Contract 8.10: once, on Serial-B, after boot.
            EmitLocked("!BOOT v4.50p+wcp1");
        }
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

        /// <summary>A <c>BUSY</c> answer, which is not stored for replay: asking again is how it is meant to be met.</summary>
        public bool Busy { get; set; }

        public void Data(string text)
        {
            // Data lines are at most 190 characters before the CRC; longer text
            // is split across lines, wherever the split falls.
            for (int at = 0; at < Math.Max(1, text.Length); at += Wcp.MaxDataText)
                Lines.Add($"#{Seq}:{text.Substring(at, Math.Min(Wcp.MaxDataText, text.Length - at))}");
        }

        /// <summary>
        /// A setting, <c>key=value</c>, on one line however long: cut to fit, with a
        /// <c>~</c> to say so, never split into a line with no key.
        /// </summary>
        public void Setting(string key, string value)
        {
            string line = $"{key}={value}";

            if (line.Length > Wcp.MaxDataText) line = line[..(Wcp.MaxDataText - 1)] + WcpWire.Truncated;

            Lines.Add($"#{Seq}:{line}");
        }

        /// <summary>The final line, carrying the count of data lines before it (contract 8.3).</summary>
        public void Ok(string summary = "")
        {
            int n = Lines.Count(IsData);
            Lines.Add(summary.Length > 0 ? $"#{Seq}.OK n={n} {summary}" : $"#{Seq}.OK n={n}");
        }

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

    /// <summary>
    /// One WCP line onto the wire: CR LF first unless the last byte sent was an LF,
    /// so the line starts a line whatever came before it (contract 8.2).
    /// </summary>
    private void EnqueueLineLocked(string framed)
    {
        if (!IsOpen) return;

        string text = (_lastOut == (byte)'\n' ? "" : "\r\n") + framed + "\r\n";

        foreach (char c in text) _out.Enqueue((byte)c);
        _lastOut = (byte)'\n';
        Monitor.PulseAll(_gate);
    }

    /// <summary>The last byte sent, for the CR LF rule. An LF at power-up, so the first line needs none.</summary>
    private byte _lastOut = (byte)'\n';

    // ----- the commands -----------------------------------------------------------

    private void Run(string command, Reply r)
    {
        string[] words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0] : "";
        string sub = words.Length > 1 ? words[1] : "";

        switch (verb)
        {
            case "hello":
                if (BusyHellos > 0)
                {
                    BusyHellos--;
                    r.Busy = true;
                    r.Err(WcpWire.Busy, "booting");
                    return;
                }

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
        r.Data($"can={CanMode}");
        r.Data($"bitrate={Bitrate}");
        r.Data($"can_rx={_rx}");
        r.Data($"can_tx={_tx}");
        r.Data($"can_drop={_dropsTotal}");
        r.Data($"stream={(Streaming ? "on" : "off")}");
        r.Data($"armed={(Armed ? 1 : 0)}");
        r.Data($"tx_rate_cap={WcpTxRate}");
        r.Data($"autopid={AutopidState}");
        r.Data($"logger={(Saved["logger_status"] == "enable" ? "on" : "off")}");
        r.Data("sd=ok");
        r.Data("heap=182340");
        r.Data("psram=7864320");
        r.Ok();
    }

    private string Shown(string key, string value) => IsSecret(key) ? WcpWire.Masked : value;

    private string CanMode => !CanRunning ? "off" : Armed || _luaNormal ? "normal" : "listen";

    private void Config(string command, string sub, string[] words, Reply r)
    {
        switch (sub)
        {
            case "get" when words.Length == 2:
                foreach ((string k, string v) in Staged) r.Setting(k, Shown(k, v));
                r.Ok();
                return;

            case "get" when words.Length == 4 && words[3] == WcpWire.Base64Flag:
            {
                if (!Staged.TryGetValue(words[2], out string? whole))
                {
                    r.Err("ARGS", $"unknown key {words[2]}");
                    return;
                }

                // The whole value, as base64 lines of at most 160 characters.
                Base64Lines(r, Encoding.UTF8.GetBytes(Shown(words[2], whole)));
                r.Ok();
                return;
            }

            case "get" when words.Length == 3:
                if (!Staged.TryGetValue(words[2], out string? one))
                {
                    r.Err("ARGS", $"unknown key {words[2]}");
                    return;
                }

                r.Setting(words[2], Shown(words[2], one));
                r.Ok();
                return;

            case "get":
                r.Err("ARGS", $"config get [key [{WcpWire.Base64Flag}]]");
                return;

            case "secrets":
                if (!Unlocked)
                {
                    r.Err("DENIED", "config unlock first");
                    return;
                }

                foreach ((string k, string v) in Staged) r.Setting(k, v);
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

                string value = rest[(eq + 1)..];

                // F19: a masked password staged back as it was read would save the
                // mask as the password.
                if (value.Contains(WcpWire.Masked, StringComparison.Ordinal))
                {
                    r.Err("ARGS", $"{key}: contains {WcpWire.Masked}");
                    return;
                }

                // A stage starts as a copy of the settings as they are now, and
                // remembers which version that was (F24).
                if (!_hasStage)
                {
                    DiscardStagedLocked();
                    _hasStage = true;
                }

                Staged[key] = value;
                r.Ok("staged");
                return;
            }

            case "diff":
                foreach ((string k, string v) in Staged)
                {
                    if (Saved[k] == v) continue;

                    string line = $"{k}={Shown(k, Saved[k])} -> {Shown(k, v)}";
                    r.Lines.Add($"#{r.Seq}:{(line.Length > Wcp.MaxDataText ? line[..(Wcp.MaxDataText - 1)] + WcpWire.Truncated : line)}");
                }

                r.Ok();
                return;

            case "discard":
                DiscardStagedLocked();
                r.Ok("discarded");
                return;

            case "save":
                // F24: the settings on flash changed since this was staged from
                // them, so saving would undo that change.
                if (_stagedFromVersion != _savedVersion)
                {
                    r.Err("STATE", WcpWire.ConfigChanged);
                    return;
                }

                if (Validate(Staged) is { } why)
                {
                    // Nothing is written: a save that would fail the boot-time
                    // check is the one thing that could factory-reset the unit.
                    r.Err("ARGS", why);
                    return;
                }

                foreach ((string k, string v) in Staged) Saved[k] = v;
                _hasStage = false;

                // Section 7.3.5: a save with lua_can_tx=disable is a kill switch,
                // and 8.9: lua_can_tx and lua_tx_rate are re-read at the save
                // rather than at the reboot, so LuaSend reads Saved directly.
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
                if (!_hasStage)
                {
                    DiscardStagedLocked();
                    _hasStage = true;
                }

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

        // Contract 8.8: more than 3 KB is refused, not clamped.
        if (length > 3072)
        {
            r.Err("ARGS", "length: at most 3072");
            return;
        }

        int take = (int)Math.Max(0, Math.Min(length, file.Length - offset));
        byte[] chunk = take > 0 ? file.AsSpan((int)offset, take).ToArray() : [];

        if (Base64PerLine)
        {
            // 120 bytes is exactly 160 base64 characters, the most a line may
            // carry (contract 8.8), with no padding until the last line, so each
            // line decodes on its own.
            Base64Lines(r, chunk);
        }
        else
        {
            string b64 = Convert.ToBase64String(chunk);
            for (int at = 0; at < b64.Length; at += 160) r.Lines.Add($"#{r.Seq}:{b64.Substring(at, Math.Min(160, b64.Length - at))}");
        }

        r.Ok(string.Create(CultureInfo.InvariantCulture, $"{chunk.Length} {file.Length}"));
    }

    private static void Base64Lines(Reply r, byte[] bytes)
    {
        for (int at = 0; at < bytes.Length; at += 120)
            r.Lines.Add($"#{r.Seq}:{Convert.ToBase64String(bytes, at, Math.Min(120, bytes.Length - at))}");
    }

    private void Can(string sub, string[] words, Reply r)
    {
        switch (sub)
        {
            case "status":
                r.Data($"mode={CanMode}");
                r.Data($"bitrate={Bitrate}");
                r.Data($"running={(CanRunning ? 1 : 0)}");
                r.Data($"rx={_rx}");
                r.Data($"{WcpWire.RxRecent}={RxRecentLocked()}");
                r.Data($"tx={_tx}");
                r.Data($"drop={_dropsTotal}");
                r.Data("bus_errors=0");
                r.Data($"armed={(Armed ? 1 : 0)}");
                r.Data($"tx_rate_cap={WcpTxRate}");
                r.Ok();
                return;

            case "start":
                if (words.Length > 2)
                {
                    if (!int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out int rate) || rate < 1)
                    {
                        r.Err("ARGS", "bitrate in kbit/s, e.g. 500");
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

            case "resume":
                // F26: the one way back from a latched fault besides lua resume-tx.
                // CHOICE: it lifts the Lua suspension the latch imposed as well,
                // since the latch is one thing. Resuming with nothing latched is OK.
                if (TxFault is not null && LuaTxSuspended == TxFault) LuaTxSuspended = null;
                TxFault = null;
                r.Ok("resumed");
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

        // Review: with CAN off this answers ERR STATE (the firmware changes to match).
        if (!CanRunning)
        {
            r.Err("STATE", "can start first");
            return;
        }

        if (words.Length is 5 or 6)
        {
            bool x = words.Length == 6;

            if (x && words[5] != WcpWire.ExtendedFlag)
            {
                r.Err("ARGS", "can stream on <id> <mask> [x]");
                return;
            }

            if (!WcpClient.TryParseId(words[3], out uint id) || !uint.TryParse(words[4], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out uint mask))
            {
                r.Err("ARGS", "id and mask in hex");
                return;
            }

            if (!x && (id > 0x7FF || mask > 0x7FF))
            {
                r.Err("ARGS", "an id or mask over 7FF needs x");
                return;
            }

            _filterId = id;
            _filterMask = mask;
            _filterExtended = x;
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

        // F26: no way back from a latched fault but can resume.
        if (TxFault is not null)
        {
            r.Err("STATE", $"{WcpWire.Fault} {TxFault}");
            return;
        }

        // F27: going to normal mode on a bus with nothing heard is how a wrong
        // bitrate takes a bus down; arming on a silent bus is refused.
        if (!Armed && RxRecentLocked() == 0)
        {
            r.Err("STATE", WcpWire.NoTraffic);
            return;
        }

        bool was = Armed;
        Armed = true;

        r.Ok("armed");

        // !ARMED has no fields (contract 8.5). CHOICE: it is not repeated for an
        // arm while already armed.
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

        if (FailNextSend)
        {
            FailNextSend = false;
            r.Err("IO", "tx-timeout");
            LatchFaultAfter(r, "tx-timeout");
            return;
        }

        TransmitLocked(id, extended, Convert.FromHexString(hex), "wcp");
        r.Ok("sent");
    }

    /// <summary>The fault latch, its events after the reply's final line.</summary>
    private void LatchFaultAfter(Reply r, string reason)
    {
        TxFault = reason;
        _luaNormal = false;

        if (Armed)
        {
            Armed = false;
            r.After.Add($"!DISARM {WcpWire.Fault} {reason}");
        }

        if (LuaTxSuspended is null)
        {
            LuaTxSuspended = reason;
            r.After.Add($"!LUA - tx suspended {reason}");
        }
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

        // F25: back to listen-only after 10 s without a script sending.
        if (_luaNormal && Now - _lastLuaTx > TimeSpan.FromSeconds(10)) _luaNormal = false;

        // F27: the first send, the one that takes the controller to normal mode,
        // is refused on a bus with nothing heard.
        if (!_luaNormal && RxRecentLocked() == 0) return (false, WcpWire.NoTraffic);

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
        _luaNormal = true;
        _lastLuaTx = now;
        TransmitLocked(id, extended, data, script);
        return (true, "");
    }

    /// <summary>
    /// A running script prints, as <c>print(...)</c> on the device would: escaped,
    /// <c>&gt;</c> and every byte outside 0x20–0x7E as <c>\xHH</c> (review item F11).
    /// </summary>
    public void LuaPrint(string script, string text)
    {
        lock (_gate) EmitLocked($"!LUA {script} print {Wcp.Escape(Encoding.UTF8.GetBytes(text))}");
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

                foreach ((string n, string state) in LuaStates)
                    r.Data($"script={n} state={state} mem_kb=41 cb_max_ms=3 queue_drops=0 errors=0 {WcpWire.StackFree}=31744");

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

                // Contract 8.12: a chunk goes at the end, or at 0 to truncate;
                // neither a gap nor an overlap.
                if (offset != existing.Length)
                {
                    r.Err("ARGS", $"offset {offset} is not the end ({existing.Length})");
                    return;
                }

                byte[] file = new byte[Math.Max(existing.Length, offset + chunk.Length)];
                existing.CopyTo(file, 0);
                chunk.CopyTo(file, offset);
                LuaScripts[name] = file;

                // Contract 8.7: OK n=0 <file_total_bytes>.
                r.Ok(file.Length.ToString(CultureInfo.InvariantCulture));
                return;
            }

            case "get":
                if (!LuaScripts.TryGetValue(name, out byte[]? script))
                {
                    r.Err("NOTFOUND", name);
                    return;
                }

                Base64Lines(r, script);

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
            {
                byte[] code;

                try
                {
                    code = Convert.FromBase64String(words.Length == 3 ? words[2] : "");
                }
                catch (FormatException)
                {
                    r.Err("ARGS", "lua eval <base64>");
                    return;
                }

                if (code.Length == 0)
                {
                    r.Err("ARGS", "lua eval <base64>");
                    return;
                }

                // Contract 8.11: at most 360 bytes decoded.
                if (code.Length > WcpClient.MaxEvalBytes)
                {
                    r.Err("TOOLONG", $"code over {WcpClient.MaxEvalBytes} bytes; lua put and lua run");
                    return;
                }

                Eval(Encoding.UTF8.GetString(code), r);
                return;
            }

            case "resume-tx":
                // F26: also a way back from the latch, for Lua's side of it.
                LuaTxSuspended = null;
                TxFault = null;
                r.Ok("lua tx resumed");
                return;

            default:
                r.Err("ARGS", $"lua {sub}?");
                return;
        }
    }

    /// <summary>
    /// A very small reading of <c>lua eval</c>: statements split on <c>;</c> and line breaks, of
    /// which <c>print("…")</c>, <c>can.send(0xID, "\xNN…")</c> and <c>error("…")</c>
    /// do something. Enough to show a host what an eval's output and failure look
    /// like, and that an eval's sends obey the Lua transmit rules.
    /// </summary>
    private void Eval(string code, Reply r)
    {
        foreach (string statement in code.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A comment runs to the end of its line, which base64 lets a chunk have.
            int comment = statement.IndexOf("--", StringComparison.Ordinal);
            string raw = comment >= 0 ? statement[..comment].TrimEnd() : statement;

            if (raw == "while true do end")
            {
                // F9: past the time limit, the chunk is stopped and says so.
                r.Lines.RemoveAll(IsData);
                r.Err("STATE", WcpWire.LuaTimeout);
                return;
            }

            if (raw.StartsWith("print(\"", StringComparison.Ordinal) && raw.EndsWith("\")", StringComparison.Ordinal))
            {
                // F11: output escaped. The fake reads Lua's \xNN in a literal, so a
                // test can print bytes that must not reach the wire as they are.
                r.Data(Wcp.Escape(Encoding.UTF8.GetBytes(Wcp.Unescape(raw[7..^2]))));
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
                r.Data(sent ? "true" : Wcp.Escape(Encoding.UTF8.GetBytes($"nil\t{error}")));
            }
        }

        r.Ok();
    }

    private void RebootLocked()
    {
        Reboots++;

        // Back to boot state: staged changes gone, listen-only, unarmed, locked,
        // no script running (autostart is not modelled).
        DiscardStagedLocked();
        Armed = false;
        LuaStates.Clear();
        LuaTxSuspended = null;
        TxFault = null;
        _luaNormal = false;
        _replies.Clear();
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
