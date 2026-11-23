using System.Globalization;
using System.Text;

namespace OpenLogViewer.Core;

/// <summary>
/// A WCP request the device answered with <c>ERR</c>, or one this host refused to
/// send because the device would have.
/// </summary>
/// <param name="code">
/// The device's error code (<c>DENIED</c>, <c>ARGS</c>, <c>NOTARMED</c>, …), or
/// <c>HOST</c> for a request refused here before anything was sent.
/// </param>
public sealed class WcpException(string code, string message, string command = "")
    : Exception(message.Length > 0 ? message : $"The WiCAN refused it ({code}).")
{
    /// <summary>What the device said was wrong, as its code.</summary>
    public string Code { get; } = code;

    /// <summary>The command that was refused, without its seq and CRC.</summary>
    public string Command { get; } = command;

    /// <summary>
    /// Refused by one of the firmware's transmit rules — not armed, not on the
    /// allowlist, too fast. Worth telling apart from a fault, because repeating it
    /// is refused again for the same reason.
    /// </summary>
    public bool IsSafetyRefusal => Code is "DENIED" or "NOTARMED";

    /// <summary>The rate limit specifically: the one refusal that waiting fixes.</summary>
    public bool IsRateLimited => Code == "DENIED" && Message.StartsWith("rate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>ERR STATE no-traffic</c>: an arm, or a script's first send, refused because
    /// no frame was received cleanly in the last second — the firmware's check that
    /// the bitrate is right before it lets the controller drive the bus.
    /// </summary>
    public bool IsNoTraffic => Code == "STATE" && WcpWire.Says(Message, WcpWire.NoTraffic);

    /// <summary><c>ERR STATE can-off</c>: an arm or a stream refused because the controller is not running.</summary>
    public bool IsCanOff => Code == "STATE" && WcpWire.Says(Message, WcpWire.CanOff);

    /// <summary><c>ERR STATE timeout</c>: Lua that ran past the device's time limit and was stopped.</summary>
    public bool IsLuaTimeout => Code == "STATE" && WcpWire.Says(Message, WcpWire.LuaTimeout);

    /// <summary><c>ERR STATE changed</c>: a save refused because the device's settings changed since they were staged.</summary>
    public bool IsConfigChanged => Code == "STATE" && WcpWire.Says(Message, WcpWire.ConfigChanged);
}

/// <summary>
/// The custom firmware's protocol is not there: a stock unit, or something that
/// is not a WiCAN at all.
/// </summary>
public sealed class WcpNotSupportedException(string message) : Exception(message);

/// <summary>One complete reply: its data lines and how it finished.</summary>
/// <param name="Summary">The text after <c>OK n=&lt;count&gt;</c>, or the error's message.</param>
public sealed record WcpReply(int Seq, string Command, bool Ok, string Code, string Summary, IReadOnlyList<string> Data);

/// <summary>
/// <c>key=value</c> lines, in the order the device sent them.
///
/// Kept ordered because the device's order is the order a person reads a status
/// page in; a dictionary alone would shuffle it.
/// </summary>
public sealed class WcpValues
{
    private readonly Dictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public WcpValues(IEnumerable<string> lines)
    {
        var pairs = new List<KeyValuePair<string, string>>();

        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..];

            pairs.Add(new KeyValuePair<string, string>(key, value));
            _byKey[key] = value;
        }

        Pairs = pairs;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Pairs { get; }

    /// <summary>The value, or empty when the device did not send that key.</summary>
    public string this[string key] => _byKey.TryGetValue(key, out string? v) ? v : "";

    public bool Has(string key) => _byKey.ContainsKey(key);

    /// <summary>A number, or null when the key is missing or is not one.</summary>
    public long? Long(string key) =>
        long.TryParse(this[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

    public IReadOnlyDictionary<string, string> ToDictionary() =>
        Pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What <c>hello</c> reports.</summary>
/// <param name="Protocol">The WCP version, 1 for this host.</param>
/// <param name="Firmware">e.g. "v4.50p+wcp1".</param>
/// <param name="Device">The unit's own id.</param>
/// <param name="Obd">The OBD chip behind it, e.g. "STN2120 v5.8.1".</param>
/// <param name="Capabilities">What this firmware build offers: config, console, autopid, log, can, can-tx.</param>
public sealed record WcpHello(int Protocol, string Firmware, string Device, string Obd, IReadOnlyList<string> Capabilities)
{
    public bool Can(string capability) => Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One staged settings change, as <c>config diff</c> reports it. Passwords arrive masked.</summary>
public sealed record WcpConfigChange(string Key, string Old, string New);

/// <summary>One file in the SD card logger's folder.</summary>
/// <param name="ModifiedUnix">Its modification time, in seconds since 1970 (UTC); 0 when the device sent none.</param>
public sealed record WcpLogFile(string Name, long Size, long ModifiedUnix)
{
    public DateTimeOffset? Modified => ModifiedUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(ModifiedUnix) : null;
}

/// <summary>One piece of a log file and how long the whole file is.</summary>
public sealed record WcpLogChunk(byte[] Data, long Total);

/// <summary>What the device reported the console command printing, and whether it succeeded.</summary>
public sealed record WcpConsoleResult(bool Ok, IReadOnlyList<string> Output, string Error);

/// <summary>A Lua script stored on the device (<c>lua list</c>).</summary>
public sealed record WcpLuaFile(string Name, long Size);

/// <summary>One script's line in <c>lua status</c>: <c>script state mem_kb cb_max_ms queue_drops errors stack_free</c>.</summary>
/// <param name="StackFree">
/// The bytes of the script task's stack it has never used, its high-water mark; null
/// from a firmware that does not report it. A small number is a script close to
/// overflowing its stack.
/// </param>
public sealed record WcpLuaScript(
    string Name, string State, long MemKb, long CallbackMaxMs, long QueueDrops, long Errors, long? StackFree = null);

/// <summary>
/// What <c>lua status</c> reports: the engine's own <c>key=value</c> lines, and a
/// line per script.
/// </summary>
/// <param name="Values"><c>engine</c>, <c>tx</c> (enabled, disabled or suspended), <c>tx_count</c>, <c>tx_drop</c>.</param>
public sealed record WcpLuaStatus(WcpValues Values, IReadOnlyList<WcpLuaScript> Scripts)
{
    /// <summary>Whether scripts may transmit right now: <c>tx=enabled</c>, as opposed to disabled or suspended.</summary>
    public bool TransmitEnabled => Values["tx"].Equals("enabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A line from a running script: <c>!LUA &lt;name&gt; &lt;kind&gt; &lt;text&gt;</c>.
/// </summary>
/// <param name="Name">The script, or <c>-</c> for the engine itself ("autostart skipped after crash", "tx suspended").</param>
/// <param name="Kind"><c>print</c>, <c>emit</c>, <c>error</c>, <c>state</c> — or, from the engine, whatever word follows the dash.</param>
/// <param name="Text">The rest: the printed text; <c>&lt;tag&gt; &lt;value&gt;</c> for emit; the message; the new state.</param>
public sealed record WcpLuaEvent(string Name, string Kind, string Text);

/// <summary>One frame from a <c>!CAN</c> event.</summary>
/// <param name="DeviceMs">The device's uptime when it was received, in milliseconds.</param>
/// <param name="Remote">A remote (RTR) frame, flagged <c>sr</c> or <c>xr</c>: it carries a DLC and no data.</param>
public sealed record WcpCanEvent(long DeviceMs, uint Id, bool Extended, byte[] Data, bool Remote = false, int Dlc = -1)
{
    /// <summary>The length the frame declared: the data's, except on a remote frame.</summary>
    public int Length => Dlc >= 0 ? Dlc : Data.Length;
}

/// <summary>
/// The host side of WCP: one request at a time, a CRC on every line, the retry
/// the contract asks for, and every command as a typed method.
///
/// <para>
/// <b>One request outstanding, by construction.</b> The firmware runs commands one
/// at a time behind a mutex it shares with its own console, and that mutex is
/// not re-entrant. A host that pipelined requests would find out which of them
/// it had broken from the replies; so every call here takes the one slot and
/// holds it until its final line, a timeout, or the link closing.
/// </para>
///
/// <para>
/// <b>What is retried, and what is not.</b> The contract says retry once on
/// <c>ERR CRC</c> or a timeout. <c>ERR CRC</c> is always safe to retry: the
/// firmware threw the request away without running it. A timeout is not the
/// same thing at all — the request may have run and only its answer been lost —
/// so a timeout is retried only for a command that does the same thing twice as
/// once. <c>can send</c> is not one: a frame retried after a lost "OK sent" is a
/// frame sent twice to a module on a moving car's bus. Nor are <c>console</c>
/// (any command at all), <c>lua run</c>, <c>lua eval</c> or <c>reboot</c>.
/// </para>
///
/// <para>
/// <b>A retry after a timeout keeps its seq.</b> The firmware keeps the final
/// replies to its last eight requests and answers a repeated seq from that store
/// without running the command again (review item F17), so the second attempt of
/// a request that did run gets its answer rather than a second execution. A
/// retry because data lines were lost or damaged takes a new seq instead: the
/// store holds only the final line, and the lines that went missing are what is
/// wanted.
/// </para>
///
/// <para>
/// <b>Arming is kept alive here.</b> The transmit gate is deliberately light — the
/// owner's choice, revised into the contract on 2026-11-22: <c>can arm</c> takes no
/// arguments and allows any identifier, and the firmware disarms on
/// <c>can disarm</c>, a reboot, or 10 s with no WCP request at all. So this pings
/// every <see cref="PingInterval"/> for as long as it believes the unit is armed,
/// well inside the 5 s the contract asks a host to keep to — and also while
/// <c>config unlock</c> is in force or settings are staged, because the firmware
/// treats the same 10 s of silence as a disconnect and drops both. The person-facing gate
/// — confirming once per arming — is the application's, not this class's.
/// </para>
///
/// <para>
/// None of this replaces the firmware's rules. Everything the firmware enforces
/// it enforces whatever a host does, and the checks here exist so that a mistake
/// is refused before a round trip rather than after one — never so that the
/// firmware can be trusted less.
/// </para>
/// </summary>
public sealed class WcpClient : IDisposable
{
    private readonly IWcpPort _port;
    private readonly WcpSequence _sequence;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Timer _pinger;

    private Pending? _pending;
    private DateTime? _armedSince;
    private bool _unlocked;
    private bool _staged;
    private string? _fault;
    private volatile bool _disposed;

    /// <summary>The default wait for a final line, from the contract.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long <see cref="Detect"/> waits for <c>hello</c>: 2.5 s, long enough for the
    /// firmware's hold on WCP while an ELM327 reply is due (at most 1.5 s, review
    /// item F3) and the answer after it.
    /// </summary>
    public static readonly TimeSpan DetectTimeout = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// How long a <c>hello</c> answered <c>ERR BUSY</c> — a unit still booting — is
    /// asked again before giving up on it (review item H2).
    /// </summary>
    public static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(10);

    /// <summary>The pause between those attempts.</summary>
    public static readonly TimeSpan BusyRetryPause = TimeSpan.FromMilliseconds(250);

    /// <summary>How long <c>config save</c> is waited for: it validates and writes flash (contract 8.6).</summary>
    public static readonly TimeSpan ConfigSaveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long <c>reboot</c> is waited for: its OK comes before the restart (contract 8.6).</summary>
    public static readonly TimeSpan RebootTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long the firmware stays armed with no WCP request, from the contract.</summary>
    public static readonly TimeSpan IdleDisarm = TimeSpan.FromSeconds(10);

    /// <summary>The most a <c>log read</c> returns in one call, in bytes.</summary>
    public const int MaxLogChunk = 3072;

    public WcpClient(IWcpPort port, WcpSequence? sequence = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _sequence = sequence ?? new WcpSequence();
        _pinger = new Timer(_ => KeepAlive(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        _port.LineReceived += OnLine;
        _port.Closed += OnClosed;
    }

    /// <summary>How long a request waits for its final line before it is retried or abandoned.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// How often a ping goes out while armed, unlocked or staged. Two seconds: the
    /// contract asks for something at least every 5 s against a 10 s idle disarm,
    /// and two leaves room for a lost ping, and a slow request holding the slot,
    /// without disarming.
    /// </summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether <c>config unlock</c> is in force, as far as this host knows. The
    /// firmware ends it at a disconnect, which to it is 10 s without a request, so
    /// the keep-alive runs while this is set.
    /// </summary>
    public bool IsUnlocked
    {
        get
        {
            lock (_gate) return _unlocked;
        }
    }

    /// <summary>
    /// Whether this host has staged a setting it has not saved or discarded. The
    /// firmware drops the staged copy after the same 10 s of silence, so the
    /// keep-alive runs while this is set as well.
    /// </summary>
    public bool HasStagedChanges
    {
        get
        {
            lock (_gate) return _staged;
        }
    }

    /// <summary>
    /// The transmit fault the device latched, from <c>!DISARM fault &lt;reason&gt;</c>
    /// (<c>tx-timeout</c>, <c>bus-off</c>, <c>error-passive</c>, <c>tec</c>), or null.
    /// While latched the controller is back to listen-only and Lua transmit is
    /// suspended; only <see cref="CanResume"/> (or <c>lua resume-tx</c>) clears it.
    /// </summary>
    public string? TransmitFault
    {
        get
        {
            lock (_gate) return _fault;
        }
    }

    /// <summary>The reason given by the last disarm this host heard of, e.g. <c>idle</c> or <c>fault bus-off</c>; null before any.</summary>
    public string? LastDisarmReason { get; private set; }

    /// <summary>What <see cref="Hello"/> reported, once it has been asked.</summary>
    public WcpHello? Identity { get; private set; }

    /// <summary>
    /// Whether transmit is armed, as far as this host knows: set by a successful
    /// <see cref="CanArm"/> or an <c>!ARMED</c> event, cleared by <c>!DISARM</c>, by
    /// <see cref="CanDisarm"/>, by a reboot, by a send refused <c>NOTARMED</c>, and by
    /// the link closing.
    /// </summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate) return _armedSince is not null;
        }
    }

    /// <summary>When this host saw transmit armed, by its own clock; null while disarmed.</summary>
    public DateTime? ArmedSinceUtc
    {
        get
        {
            lock (_gate) return _armedSince;
        }
    }

    /// <summary>Requests sent again after <c>ERR CRC</c>, a timeout, or a damaged reply.</summary>
    public int Retries { get; private set; }

    /// <summary>Replies that arrived with fewer data lines than their <c>n=</c> said.</summary>
    public long ShortReplies { get; private set; }

    /// <summary>How many times the device said it had restarted (<c>!BOOT</c>).</summary>
    public int Boots { get; private set; }

    /// <summary>Lines whose CRC was wrong, over the session.</summary>
    public long DamagedLines { get; private set; }

    /// <summary>Replies that arrived for a request no longer waiting — late answers to a timeout.</summary>
    public long StaleLines { get; private set; }

    /// <summary>Pings the keep-alive sent, and how many of them went unanswered.</summary>
    public long Pings { get; private set; }

    public long PingFailures { get; private set; }

    /// <summary>Frames lost on the device since the stream started, as its <c>!DROP</c> events add up.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>A frame from the CAN stream. Raised on the link's reading thread.</summary>
    public event Action<WcpCanEvent>? CanFrame;

    /// <summary>Frames the device lost since the last report (<c>!DROP</c>).</summary>
    public event Action<long>? Dropped;

    /// <summary>The device armed transmit (<c>!ARMED</c>).</summary>
    public event Action? ArmedOn;

    /// <summary>
    /// The device disarmed, with its reason: <c>host</c>, <c>idle</c>, or
    /// <c>fault &lt;reason&gt;</c> for a latched transmit fault (see
    /// <see cref="TransmitFault"/>), <c>reconfig</c> when something outside WCP
    /// reconfigured the controller, or any other word a later firmware adds — or <c>link</c> or <c>boot</c> when this host saw
    /// the link close or the unit restart.
    /// </summary>
    public event Action<string>? Disarmed;

    /// <summary>
    /// The device restarted (<c>!BOOT &lt;fw&gt;</c>), with the firmware it came up
    /// with. Raised after the arming is forgotten and <c>hello</c> has been asked
    /// again, so <see cref="Identity"/> is the new one.
    /// </summary>
    public event Action<string>? Booted;

    /// <summary>A line from a Lua script on the device, or from its engine (<c>!LUA</c>).</summary>
    public event Action<WcpLuaEvent>? Lua;

    /// <summary>Any other event, by type and fields, for whatever a later firmware adds.</summary>
    public event Action<string, string>? OtherEvent;

    // ----- detection ---------------------------------------------------------

    /// <summary>
    /// Asks <c>hello</c>, and says whether the custom firmware answered.
    ///
    /// <para>
    /// Null means plain ELM327: no WCP reply, a reply that is not version 1, or —
    /// the quick case — the ELM327 chip echoing the request back, which is what a
    /// stock unit does with a line it does not recognise as a command. A stock
    /// unit costs at most two short timeouts here and is otherwise untouched; the
    /// chip answers the stray line with "?" and that is all.
    /// </para>
    /// </summary>
    /// <para>
    /// A unit still booting answers <c>ERR BUSY</c>, which is not stock firmware at
    /// all: that is asked again for up to <see cref="BusyWindow"/>.
    /// </para>
    /// </summary>
    /// <param name="timeout">
    /// <see cref="DetectTimeout"/> by default, shorter than the contract's 3 s,
    /// because this is asked of every WiCAN Pro on every connection and the answer
    /// for a stock one with echo off is the silence.
    /// </param>
    /// <param name="busyWindow">How long to keep asking a unit that answers <c>BUSY</c>; <see cref="BusyWindow"/> by default.</param>
    public static WcpClient? Detect(IWcpPort port, TimeSpan? timeout = null, TimeSpan? pingInterval = null,
        TimeSpan? busyWindow = null)
    {
        var client = new WcpClient(port)
        {
            Timeout = timeout ?? DetectTimeout,
            PingInterval = pingInterval ?? TimeSpan.FromSeconds(2),
        };

        try
        {
            WcpHello hello = client.HelloWhenReady(busyWindow ?? BusyWindow);

            if (hello.Protocol == Wcp.Version)
            {
                return client;
            }
        }
        catch (Exception e) when (e is TimeoutException or WcpNotSupportedException or WcpException or IOException
                                      or InvalidOperationException)
        {
        }

        client.Dispose();
        return null;
    }

    // ----- the one request ---------------------------------------------------

    /// <summary>
    /// Sends one command and waits for its final line.
    ///
    /// Throws <see cref="WcpException"/> for an <c>ERR</c> reply, <see cref="TimeoutException"/>
    /// when nothing final came, and <see cref="IOException"/> when the link closed
    /// while it waited.
    /// </summary>
    /// <param name="retryOnTimeout">
    /// False for anything that must not run twice: see the class remarks.
    /// </param>
    public WcpReply Request(string command, TimeSpan? timeout = null, bool retryOnTimeout = true)
    {
        WcpReply reply = Exchange(command, timeout, retryOnTimeout);

        if (!reply.Ok) throw new WcpException(reply.Code, reply.Summary, command);

        return reply;
    }

    /// <summary>The same, returning an <c>ERR</c> as a reply rather than throwing it.</summary>
    public WcpReply Exchange(string command, TimeSpan? timeout = null, bool retryOnTimeout = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TimeSpan wait = timeout ?? Timeout;

        // Bounded, so a request stuck behind a dead one fails rather than hangs:
        // two whole attempts of whatever holds the slot, and a little over.
        if (!_slot.Wait(wait * 2 + TimeSpan.FromSeconds(1)))
            throw new TimeoutException("The WiCAN is still busy with an earlier request.");

        try
        {
            return ExchangeHoldingSlot(command, wait, retryOnTimeout);
        }
        finally
        {
            _slot.Release();
        }
    }

    private WcpReply ExchangeHoldingSlot(string command, TimeSpan wait, bool retryOnTimeout)
    {
        int seq = _sequence.Next();
        int resends = 0;

        // Whether the attempt before this one reused its seq after a timeout. Its
        // answer then comes from the firmware's reply store (review item F17),
        // which holds the final line only, so a reply short of its data lines is
        // asked for once more under a fresh seq — one resend more than otherwise.
        bool replayed = false;

        // Data lines of an attempt that timed out, kept for the replay of the
        // same seq: the lines may have arrived and only the final been lost.
        List<string> carried = [];

        while (true)
        {
            string line = Wcp.Request(seq, command);
            var pending = new Pending(seq);

            lock (_gate) _pending = pending;

            try
            {
                if (!_port.IsOpen) throw new IOException("The WiCAN link is not open.");

                _port.SendLine(line);

                bool done = pending.Done.Wait(wait);

                lock (_gate) _pending = null;

                if (pending.Closed) throw new IOException("The WiCAN link closed while waiting for a reply.");

                if (pending.Echoed)
                    throw new WcpNotSupportedException(
                        "The adapter echoed the request back as an ELM327 command: this is stock WiCAN firmware, "
                        + "without the WCP control lane.");

                if (!done)
                {
                    if (resends < 1 && retryOnTimeout)
                    {
                        // The same seq: if it ran, the firmware answers from its
                        // store rather than running it again.
                        Retries++;
                        resends++;
                        replayed = true;
                        carried = pending.Tainted ? [] : [.. pending.Data];
                        continue;
                    }

                    throw new TimeoutException($"The WiCAN did not answer '{command}' within {wait.TotalSeconds:0.#} s.");
                }

                WcpLine final = pending.Final!;
                string summary = final.Text;
                bool short_ = false;
                List<string> data = pending.Data;

                // OK carries the number of data lines it sent (contract 8.3):
                // fewer arrived means one was lost whole, which no CRC can show.
                if (final.Kind == WcpLineKind.Ok && TrySplitCount(final.Text, out int count, out string rest))
                {
                    summary = rest;

                    // A replay with no data lines of its own completes the lines
                    // the timed-out attempt did receive, if they are all there.
                    if (replayed && data.Count == 0 && carried.Count == count && !pending.Tainted) data = carried;

                    short_ = data.Count < count;
                    if (short_) ShortReplies++;
                }

                // ERR CRC is the firmware saying the request never ran, so a
                // retry is always safe, and keeps the seq. A short reply, or one
                // with a damaged line in it, ran but lost part of its answer, so
                // it is asked again where asking again is harmless — under a new
                // seq, so that it runs again rather than being answered from the
                // store without the lines that went missing.
                // ERR BUSY console is the same promise: it waited for the console
                // lock and did not run, and the firmware never stores it.
                bool crc = final.Kind == WcpLineKind.Error
                           && (final.Code == "CRC"
                               || final.Code == WcpWire.Busy && WcpWire.Says(final.Text, WcpWire.BusyConsole));
                bool lost = (pending.Tainted || short_) && retryOnTimeout;
                int allowed = replayed ? 2 : 1;

                if ((crc || lost) && resends < allowed)
                {
                    Retries++;
                    resends++;
                    carried = [];

                    if (lost)
                    {
                        seq = _sequence.Next();
                        replayed = false;
                    }

                    continue;
                }

                if (short_ && retryOnTimeout)
                    throw new InvalidDataException($"The WiCAN's answer to '{command}' lost data lines twice.");

                return new WcpReply(seq, command, final.Kind == WcpLineKind.Ok, final.Code, summary, data);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_pending, pending)) _pending = null;
                }
            }
        }
    }

    /// <summary>Splits <c>n=&lt;count&gt; [summary]</c>. False for a final line without the count.</summary>
    internal static bool TrySplitCount(string text, out int count, out string rest)
    {
        count = 0;
        rest = text;

        if (!text.StartsWith("n=", StringComparison.Ordinal)) return false;

        int space = text.IndexOf(' ');
        string number = space < 0 ? text[2..] : text[2..space];

        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out count)) return false;

        rest = space < 0 ? "" : text[(space + 1)..];
        return true;
    }

    private sealed class Pending(int seq)
    {
        public int Seq { get; } = seq;

        public List<string> Data { get; } = [];

        public WcpLine? Final { get; set; }

        public bool Closed { get; set; }

        public bool Echoed { get; set; }

        public bool Tainted { get; set; }

        public ManualResetEventSlim Done { get; } = new();
    }

    // ----- what arrives ------------------------------------------------------

    private void OnLine(string raw)
    {
        if (!WcpLine.TryParse(raw, out WcpLine line, out bool damaged))
        {
            if (!damaged) return;

            DamagedLines++;

            // A damaged reply line is part of whatever is waiting, and its seq
            // cannot be trusted to say so. A damaged event line is nobody's.
            if (raw.StartsWith('#'))
            {
                lock (_gate)
                {
                    if (_pending is { } p) p.Tainted = true;
                }
            }

            return;
        }

        switch (line.Kind)
        {
            case WcpLineKind.Event:
                Dispatch(line);
                return;

            case WcpLineKind.Request:
                lock (_gate)
                {
                    if (_pending is { } waiting && waiting.Seq == line.Seq)
                    {
                        waiting.Echoed = true;
                        waiting.Done.Set();
                    }
                }

                return;
        }

        lock (_gate)
        {
            bool ours = _pending is { } p
                        && (p.Seq == line.Seq
                            || line.Seq == 0 && line.Kind == WcpLineKind.Error && line.Code == "CRC");

            if (!ours)
            {
                StaleLines++;
                return;
            }

            if (line.Kind == WcpLineKind.Data)
            {
                _pending!.Data.Add(line.Text);
                return;
            }

            _pending!.Final = line;
            _pending.Done.Set();
        }
    }

    private void OnClosed()
    {
        lock (_gate)
        {
            if (_pending is { } p)
            {
                p.Closed = true;
                p.Done.Set();
            }
        }

        // The firmware disarms when the host goes; this host has gone, as far as
        // it is concerned, so it stops believing otherwise. An unlock and a
        // staged copy end with the session the same way.
        ForgetSession();
        if (ClearArming()) Raise(() => Disarmed?.Invoke("link"));
    }

    /// <summary>What ends with a disconnect or a restart besides the arming: the unlock and the staged settings.</summary>
    private void ForgetSession()
    {
        lock (_gate)
        {
            _unlocked = false;
            _staged = false;
        }

        UpdatePinging();
    }

    private void Dispatch(WcpLine line)
    {
        switch (line.Code)
        {
            case "CAN":
                if (TryParseCan(line.Text, out WcpCanEvent frame)) Raise(() => CanFrame?.Invoke(frame));
                else DamagedLines++;
                break;

            case "DROP":
                if (long.TryParse(line.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
                {
                    DroppedFrames += n;
                    Raise(() => Dropped?.Invoke(n));
                }

                break;

            case "ARMED":
                SetArmed();
                Raise(() => ArmedOn?.Invoke());
                break;

            case "BOOT":
                OnBoot(line.Text.Trim());
                break;

            case "DISARM":
            {
                string reason = line.Text.Trim();
                LastDisarmReason = reason;

                // A latched transmit fault: the controller is back to listen-only
                // and stays so until can resume. Remembered, so a status read can
                // say why transmit stopped rather than only that it did.
                if (WcpWire.IsFault(reason, out string fault))
                {
                    lock (_gate) _fault = fault;
                }

                ClearArming();
                Raise(() => Disarmed?.Invoke(reason));
                break;
            }

            case "LUA":
                if (TryParseLua(line.Text, out WcpLuaEvent lua)) Raise(() => Lua?.Invoke(lua));
                break;

            default:
                Raise(() => OtherEvent?.Invoke(line.Code, line.Text));
                break;
        }
    }

    /// <summary>
    /// A restart: everything the session had set up on the device is gone —
    /// arming, an unlock, a stream — so the arming is forgotten here and
    /// <c>hello</c> asked again, off the reading thread, since a request waits for
    /// lines only that thread can deliver.
    /// </summary>
    private void OnBoot(string firmware)
    {
        Boots++;

        // A restart clears a fault latch with everything else.
        lock (_gate) _fault = null;
        ForgetSession();

        if (ClearArming())
        {
            LastDisarmReason = "boot";
            Raise(() => Disarmed?.Invoke("boot"));
        }

        Task.Run(() =>
        {
            try
            {
                if (!_disposed) HelloWhenReady(BusyWindow);
            }
            catch (Exception e) when (e is TimeoutException or WcpException or IOException or InvalidOperationException
                                          or ObjectDisposedException or WcpNotSupportedException or InvalidDataException)
            {
            }

            Raise(() => Booted?.Invoke(firmware));
        });
    }

    /// <summary>
    /// Runs a subscriber, keeping its exceptions off the link's reading thread —
    /// where one escaping would end the thread, and with it both the ELM327 and
    /// the WCP side of the link.
    /// </summary>
    private static void Raise(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Reads the fields of a <c>!CAN</c> event: <c>&lt;t_ms&gt; &lt;id_hex&gt; &lt;flags&gt; &lt;dlc&gt; [data_hex]</c>,
    /// the flags <c>s</c> or <c>x</c>, then <c>r</c> for a remote frame (contract 8.8).
    ///
    /// The data is absent for a frame with none and for every remote frame. A data
    /// length that disagrees with the DLC is a torn line and is refused rather than
    /// decoded.
    /// </summary>
    public static bool TryParseCan(string fields, out WcpCanEvent frame)
    {
        frame = null!;

        string[] parts = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 4 or > 5) return false;

        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ms)) return false;
        if (!TryParseId(parts[1], out uint id)) return false;

        bool extended, remote;

        switch (parts[2])
        {
            case "s": extended = false; remote = false; break;
            case "x": extended = true; remote = false; break;
            case "sr": extended = false; remote = true; break;
            case "xr": extended = true; remote = true; break;
            default: return false;
        }

        if (extended ? id > 0x1FFFFFFF : id > 0x7FF) return false;

        if (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int dlc) || dlc > 8)
            return false;

        string hex = parts.Length == 5 ? parts[4] : "";
        if (hex.Length != (remote ? 0 : dlc * 2)) return false;

        byte[] data;

        try
        {
            data = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return false;
        }

        frame = new WcpCanEvent(ms, id, extended, data, remote, dlc);
        return true;
    }

    /// <summary>
    /// Reads <c>&lt;name&gt; &lt;kind&gt; [text]</c>. The text keeps its spaces: a
    /// printed line is whatever the script printed.
    /// </summary>
    public static bool TryParseLua(string fields, out WcpLuaEvent lua)
    {
        lua = null!;

        string[] parts = fields.Split(' ', 3);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;

        // Script output arrives escaped (review item F11): a '>' or a byte outside
        // printable ASCII as \xHH, which is undone here for whoever shows it.
        string text = parts.Length > 2 ? parts[2] : "";
        if (parts[1] is "print" or "emit" or "error") text = Wcp.Unescape(text);

        lua = new WcpLuaEvent(parts[0], parts[1], text);
        return true;
    }

    /// <summary>A CAN identifier in hex, with or without <c>0x</c>.</summary>
    public static bool TryParseId(string text, out uint id)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];

        return uint.TryParse(span, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id)
               && span.Length is > 0 and <= 8 && id <= 0x1FFFFFFF;
    }

    /// <summary>An identifier as the firmware writes it: three hex digits for a standard one, eight for an extended one.</summary>
    public static string IdHex(uint id) => id > 0x7FF ? id.ToString("X8", CultureInfo.InvariantCulture) : id.ToString("X3", CultureInfo.InvariantCulture);

    // ----- session -----------------------------------------------------------

    /// <summary><c>hello</c>: which protocol, which firmware, what it can do.</summary>
    public WcpHello Hello()
    {
        var values = new WcpValues(Request("hello").Data);

        var hello = new WcpHello(
            (int)(values.Long("wcp") ?? 0),
            values["fw"],
            values["device"],
            values["obd"],
            [.. values["caps"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

        Identity = hello;
        return hello;
    }

    /// <summary>
    /// <see cref="Hello"/>, asked again while the device answers <c>ERR BUSY</c> —
    /// as it does while it is still booting — for up to <paramref name="window"/>.
    /// Anything else, including a timeout, ends it at once.
    /// </summary>
    public WcpHello HelloWhenReady(TimeSpan window)
    {
        DateTime until = DateTime.UtcNow + window;

        while (true)
        {
            try
            {
                return Hello();
            }
            catch (WcpException e) when (e.Code == WcpWire.Busy && DateTime.UtcNow + BusyRetryPause < until && !_disposed)
            {
                Thread.Sleep(BusyRetryPause);
            }
        }
    }

    /// <summary><c>ping</c>: the device's uptime, in milliseconds. Also the keep-alive.</summary>
    public long Ping()
    {
        WcpReply reply = Request("ping");

        return long.TryParse(reply.Summary.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ms) ? ms : 0;
    }

    /// <summary><c>status</c>: voltage, Wi-Fi, CAN counters, arming, AutoPID, logger, memory.</summary>
    public WcpValues Status() => new(Request("status").Data);

    /// <summary>
    /// <c>reboot</c>. Not retried on a timeout: a reboot that happened and whose
    /// "OK" was lost would otherwise be asked for a second time of a unit that
    /// is just coming back up.
    /// </summary>
    public void Reboot()
    {
        Request("reboot", RebootTimeout, retryOnTimeout: false);
        ClearArming();
    }

    // ----- settings ----------------------------------------------------------

    /// <summary>
    /// <c>config get</c>: every setting, passwords shown as <c>****</c>. A value the
    /// device cut at the line limit, marked with a trailing <c>~</c>, is fetched
    /// whole with <see cref="ConfigGetFull"/>.
    /// </summary>
    public WcpValues ConfigGet() => Expanded(new WcpValues(Request("config get").Data));

    /// <summary><c>config get &lt;key&gt;</c>: one setting, whole, or null when the device sent none.</summary>
    public string? ConfigGet(string key)
    {
        CheckKey(key);

        var values = new WcpValues(Request($"config get {key}").Data);
        if (!values.Has(key)) return null;

        string value = values[key];
        return IsCut(value) ? ConfigGetFull(key) : value;
    }

    /// <summary>
    /// <c>config get &lt;key&gt; --b64</c>: one setting's whole value, however long,
    /// sent as base64 data lines. The way to read a value the plain form cut short
    /// with a <c>~</c>; passwords are masked in it as everywhere else.
    /// </summary>
    public string ConfigGetFull(string key) => FullValue("config get", key);

    /// <summary>
    /// <c>config secrets &lt;key&gt; --b64</c>: the same, unmasked. Refused (<c>DENIED</c>)
    /// without <see cref="ConfigUnlock"/>.
    /// </summary>
    public string ConfigSecretFull(string key) => FullValue("config secrets", key);

    /// <summary>
    /// The base64 form: data lines of the whole value's text, and a final
    /// <c>OK n=&lt;lines&gt; &lt;bytes&gt;</c> whose byte count is checked against what decoded.
    /// </summary>
    private string FullValue(string verb, string key)
    {
        CheckKey(key);

        WcpReply reply = Request($"{verb} {key} {WcpWire.Base64Flag}");

        // Tolerant of a firmware that labels the first line with the key.
        List<string> parts = [.. reply.Data
            .Select((l, i) => i == 0 && l.StartsWith(key + "=", StringComparison.Ordinal) ? l[(key.Length + 1)..] : l)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)];

        byte[] bytes = DecodeBase64Lines(parts)
                       ?? throw new InvalidDataException($"The value of {key} did not arrive as base64.");

        string said = reply.Summary.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        if (long.TryParse(said, NumberStyles.None, CultureInfo.InvariantCulture, out long expected) && expected != bytes.Length)
            throw new InvalidDataException($"The value of {key} arrived as {bytes.Length} bytes where the WiCAN said {expected}.");

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool IsCut(string value) => value.Length > 0 && value[^1] == WcpWire.Truncated;

    /// <summary>The same settings with every value the device cut short read again whole.</summary>
    private WcpValues Expanded(WcpValues values, bool secrets = false)
    {
        if (!values.Pairs.Any(p => IsCut(p.Value))) return values;

        return new WcpValues(values.Pairs.Select(p =>
            $"{p.Key}={(IsCut(p.Value) && IsKey(p.Key) ? (secrets ? ConfigSecretFull(p.Key) : ConfigGetFull(p.Key)) : p.Value)}"));
    }

    private static bool IsKey(string key) =>
        key.Length > 0 && !key.Any(c => c is '=' or ' ' or '\r' or '\n' or '*');

    /// <summary>
    /// Base64 data lines decoded: line by line when each is whole quanta, joined
    /// first otherwise. Null when they are not base64.
    /// </summary>
    private static byte[]? DecodeBase64Lines(IReadOnlyList<string> lines)
    {
        try
        {
            if (lines.All(l => l.Length % 4 == 0))
            {
                var all = new List<byte>();
                foreach (string line in lines) all.AddRange(Convert.FromBase64String(line));
                return [.. all];
            }

            return Convert.FromBase64String(string.Concat(lines));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>config secrets</c>: every setting with passwords unmasked. The device
    /// refuses it (<c>DENIED</c>) unless <see cref="ConfigUnlock"/> ran first in
    /// this session.
    /// </summary>
    public WcpValues ConfigSecrets() => Expanded(new WcpValues(Request("config secrets").Data), secrets: true);

    /// <summary>
    /// <c>config unlock</c>: allows <see cref="ConfigSecrets"/> until disconnect or
    /// reboot. The firmware counts 10 s without a request as a disconnect, so the
    /// keep-alive pings from here until the link closes or the unit restarts.
    /// </summary>
    public void ConfigUnlock()
    {
        Request("config unlock");

        lock (_gate) _unlocked = true;
        UpdatePinging();
    }

    /// <summary>
    /// <c>config set &lt;key&gt;=&lt;value&gt;</c>: stages one change in the device's RAM
    /// copy. Nothing reaches flash until <see cref="ConfigSave"/>.
    /// </summary>
    public void ConfigSet(string key, string value)
    {
        CheckKey(key);
        ArgumentNullException.ThrowIfNull(value);

        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new WcpException("HOST", "A setting's value cannot contain a line break.", "config set");

        // A masked password read back and staged as it was would save "****" as
        // the password. The firmware refuses it too (review item F19).
        if (value.Contains(WcpWire.Masked, StringComparison.Ordinal))
            throw new WcpException("HOST",
                $"The value for {key} contains '{WcpWire.Masked}', which is how the WiCAN masks a password. "
                + "Staging it would save the mask as the password; give the real value.", "config set");

        Request($"config set {key}={value}");
        MarkStaged();
    }

    private void MarkStaged()
    {
        lock (_gate) _staged = true;
        UpdatePinging();
    }

    private void ClearStaged()
    {
        lock (_gate) _staged = false;
        UpdatePinging();
    }

    /// <summary><c>config diff</c>: each staged change, old and new.</summary>
    public IReadOnlyList<WcpConfigChange> ConfigDiff()
    {
        var changes = new List<WcpConfigChange>();

        foreach (string line in Request("config diff").Data)
        {
            int eq = line.IndexOf('=');
            int arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
            if (eq <= 0 || arrow < eq) continue;

            changes.Add(new WcpConfigChange(line[..eq], line[(eq + 1)..arrow], line[(arrow + 4)..]));
        }

        return changes;
    }

    /// <summary><c>config discard</c>: drops every staged change.</summary>
    public void ConfigDiscard()
    {
        Request("config discard");
        ClearStaged();
    }

    /// <summary>
    /// <c>config save</c>: validates the staged copy as the device does at boot,
    /// then writes it to flash. A setting that would fail validation is refused
    /// with <c>ERR ARGS &lt;key&gt;: &lt;why&gt;</c>, which arrives here as a
    /// <see cref="WcpException"/> — and nothing is written, so a save can never
    /// leave the unit with a config that factory-resets it at the next boot.
    /// </summary>
    /// <param name="reboot">Restart afterwards, so the change takes effect.</param>
    /// <returns>The device's summary, e.g. "saved; reboot to apply".</returns>
    public string ConfigSave(bool reboot = false)
    {
        // Longer than the default: this validates, writes a temporary file to
        // flash and renames it. Retried on a timeout only without the reboot.
        WcpReply reply;

        try
        {
            reply = Request(
                reboot ? "config save --reboot" : "config save",
                Longer(ConfigSaveTimeout),
                retryOnTimeout: !reboot);
        }
        catch (WcpException e) when (e.IsConfigChanged)
        {
            throw new WcpException(e.Code,
                "The WiCAN's settings changed since these were staged (by its web page, or another client), so "
                + "it refused to save over them. Discard, read the settings again, and stage the change afresh.",
                e.Command);
        }

        ClearStaged();
        if (reboot) ClearArming();

        return reply.Summary;
    }

    /// <summary>
    /// A command's own timeout, or this client's general one where that is longer —
    /// so a test that shortens <see cref="Timeout"/> does not lengthen everything,
    /// and nothing waits less than its command needs.
    /// </summary>
    private TimeSpan Longer(TimeSpan own) => own > Timeout ? own : Timeout;

    private static void CheckKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.Any(c => c is '=' or ' ' or '\r' or '\n' or '*'))
            throw new WcpException("HOST", $"'{key}' is not a setting's name.", "config");
    }

    // ----- console -----------------------------------------------------------

    /// <summary>
    /// <c>console &lt;command line&gt;</c>: any of the device's own console commands,
    /// with what it printed. A failure is returned rather than thrown, so its
    /// output — usually the explanation — is not lost with it. Not retried on a
    /// timeout, because a console command can be anything.
    /// </summary>
    public WcpConsoleResult Console(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        WcpReply reply = Exchange($"console {commandLine.Trim()}", retryOnTimeout: false);

        return new WcpConsoleResult(reply.Ok, reply.Data, reply.Ok ? "" : $"{reply.Code} {reply.Summary}".Trim());
    }

    // ----- AutoPID -----------------------------------------------------------

    /// <summary>
    /// <c>autopid data</c>: the AutoPID JSON object. The device splits it across
    /// lines at 200 characters, wherever the split falls, so the lines are joined
    /// back together with nothing between them.
    /// </summary>
    public string AutopidData() => string.Concat(Request("autopid data").Data);

    /// <summary><c>autopid get &lt;name&gt;</c>: one value, or null when the device sent none.</summary>
    public string? AutopidGet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var values = new WcpValues(Request($"autopid get {name.Trim()}").Data);
        return values.Has(name.Trim()) ? values[name.Trim()] : null;
    }

    public void AutopidPause() => Request("autopid pause");

    public void AutopidResume() => Request("autopid resume");

    // ----- logging -----------------------------------------------------------

    /// <summary><c>log status</c>: whether the SD logger is on, its file, size and the card's free space.</summary>
    public WcpValues LogStatus() => new(Request("log status").Data);

    /// <summary>
    /// How many files the last <see cref="LogList"/> said it left out, because
    /// <c>log read</c> could not read them by name (<c>&lt;k&gt; files; &lt;m&gt; skipped (…)</c>).
    /// </summary>
    public int LogListSkipped { get; private set; }

    /// <summary><c>log list</c>: the files in <c>/sdcard/obd_logs/</c> that <c>log read</c> can read.</summary>
    public IReadOnlyList<WcpLogFile> LogList()
    {
        var files = new List<WcpLogFile>();
        WcpReply reply = Request("log list");

        LogListSkipped = SkippedIn(reply.Summary);

        foreach (string line in reply.Data)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            long size = long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long s) ? s : -1;
            long mtime = parts.Length > 2 && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long m) ? m : 0;
            files.Add(new WcpLogFile(parts[0], size, mtime));
        }

        return files;
    }

    /// <summary>The <c>m</c> of "<c>…; m skipped (…)</c>", or 0.</summary>
    internal static int SkippedIn(string summary)
    {
        string[] words = summary.Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries);

        for (int i = 1; i < words.Length; i++)
        {
            if (words[i].Equals(WcpWire.Skipped, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(words[i - 1], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                return n;
        }

        return 0;
    }

    /// <summary>
    /// <c>log read &lt;name&gt; &lt;offset&gt; &lt;length&gt;</c>: up to 3 KB of a log file.
    ///
    /// <para>
    /// The data lines are base64. Each is decoded on its own when every line is a
    /// whole number of base64 quanta — which a firmware that encodes 150 bytes to
    /// a line produces — and the lines are joined first otherwise, which covers a
    /// firmware that encodes the whole chunk and then cuts it at 200 characters.
    /// The decoded length is then checked against the count in the final line, and
    /// a chunk that disagrees is asked for again once: each line has its own CRC,
    /// but a line lost whole leaves nothing damaged to notice.
    /// </para>
    /// </summary>
    public WcpLogChunk LogRead(string name, long offset, int length)
    {
        CheckLogName(name);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxLogChunk);

        for (int attempt = 0; ; attempt++)
        {
            WcpReply reply = Request(
                string.Create(CultureInfo.InvariantCulture, $"log read {name} {offset} {length}"));

            string[] counts = reply.Summary.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (counts.Length < 2
                || !int.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int bytes)
                || !long.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long total))
                throw new InvalidDataException($"The WiCAN's answer to a log read did not say how much it sent: '{reply.Summary}'.");

            byte[]? data = Decode(reply.Data);

            if (data is not null && data.Length == bytes) return new WcpLogChunk(data, total);

            if (attempt >= 1)
                throw new InvalidDataException(
                    $"A piece of {name} at {offset} arrived as {data?.Length.ToString(CultureInfo.InvariantCulture) ?? "undecodable"} "
                    + $"bytes where the WiCAN said {bytes}, twice.");

            Retries++;
        }

        static byte[]? Decode(IReadOnlyList<string> lines)
        {
            try
            {
                if (lines.All(l => l.Length % 4 == 0))
                {
                    var all = new List<byte>();
                    foreach (string line in lines) all.AddRange(Convert.FromBase64String(line));
                    return [.. all];
                }

                return Convert.FromBase64String(string.Concat(lines));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A whole log file, pulled a chunk at a time into <paramref name="into"/>.
    /// </summary>
    /// <param name="progress">Called after each chunk with the bytes so far and the file's length.</param>
    /// <returns>How many bytes were written.</returns>
    public long LogDownload(string name, Stream into, Action<long, long>? progress = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(into);

        long offset = 0;
        long total = long.MaxValue;

        while (offset < total)
        {
            cancel.ThrowIfCancellationRequested();

            WcpLogChunk chunk = LogRead(name, offset, MaxLogChunk);
            total = chunk.Total;

            if (chunk.Data.Length == 0)
            {
                if (offset < total)
                    throw new InvalidDataException(
                        $"{name} stopped at {offset} of {total} bytes: the WiCAN sent an empty piece.");

                break;
            }

            into.Write(chunk.Data);
            offset += chunk.Data.Length;
            progress?.Invoke(offset, total);
        }

        return offset;
    }

    /// <summary>
    /// <c>log enable</c> / <c>log disable</c>: stages the logger's setting. It takes
    /// a <see cref="ConfigSave"/> and a reboot to apply, which the reply says.
    /// </summary>
    public string LogEnable(bool on)
    {
        string summary = Request(on ? "log enable" : "log disable").Summary;
        MarkStaged();
        return summary;
    }

    private static void CheckLogName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The file list is space-separated, so a name with a space in it could
        // not have come from it; and a slash would reach outside the folder.
        if (name.Any(c => c is ' ' or '/' or '\\' or '\r' or '\n' or '*') || name.Contains("..", StringComparison.Ordinal))
            throw new WcpException("HOST", $"'{name}' is not the name of a file in the log folder.", "log read");
    }

    // ----- CAN, receive ------------------------------------------------------

    /// <summary>
    /// <c>can status</c>: mode, bitrate, counters, <c>armed</c>, <c>tx_rate_cap</c>, and
    /// <c>rx_recent</c> — the frames received in the last second, which is what
    /// the firmware checks before it lets transmit be armed.
    /// </summary>
    public WcpValues CanStatus()
    {
        var values = new WcpValues(Request("can status").Data);

        // A firmware that reports its fault latch here is believed over the
        // event, which may have been missed — in both directions.
        foreach (string key in WcpWire.FaultKeys)
        {
            if (!values.Has(key)) continue;

            string v = values[key].Trim();
            bool none = v.Length == 0 || v is "0" or "none" or "no" or "off" or "-";

            lock (_gate) _fault = none ? null : v is "1" or "yes" or "on" ? _fault ?? "unknown" : v;
            break;
        }

        return values;
    }

    /// <summary>
    /// <c>can start [kbps]</c>: starts the CAN controller, listen-only. Without
    /// a bitrate the device uses its configured one.
    /// </summary>
    /// <param name="kbps">Kilobits per second, e.g. 500 (contract 8.8); null for the configured rate.</param>
    public void CanStart(int? kbps = null)
    {
        if (kbps is { } k) ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        Request(kbps is { } rate
            ? string.Create(CultureInfo.InvariantCulture, $"can start {rate}")
            : "can start");
    }

    /// <summary><c>can stop</c>. The device refuses (<c>STATE</c>) while slcan or another configured user needs the controller.</summary>
    public void CanStop() => Request("can stop");

    /// <summary>
    /// <c>can stream on [id mask x|s]</c>: starts <c>!CAN</c> events, optionally only
    /// for frames whose identifier matches <paramref name="id"/> where
    /// <paramref name="mask"/> has a bit set.
    ///
    /// <para>
    /// A filter is worth having: a busy 500 kbit/s bus is close to what the
    /// 2 Mbaud link can carry as text, and what does not fit is dropped.
    /// </para>
    ///
    /// <para>
    /// A filter is for standard (11-bit) frames or extended (29-bit) ones, and an
    /// extended one is said so with a trailing <c>x</c>, a standard one with <c>s</c>,
    /// rather than left to the width of the numbers: <c>18DAF110</c> written as <c>0x00000123</c> is still
    /// extended. The device answers <c>ERR STATE</c> while the controller is
    /// stopped, so <see cref="CanStart"/> comes first.
    /// </para>
    /// </summary>
    /// <param name="extended">
    /// Filter extended frames. Null decides by width: extended when either number
    /// is above 0x7FF.
    /// </param>
    public void CanStreamOn(uint? id = null, uint? mask = null, bool? extended = null)
    {
        if (id is null != mask is null)
            throw new ArgumentException("A filter needs both an identifier and a mask, or neither.");

        if (id is not { } i || mask is not { } m)
        {
            Request("can stream on");
            return;
        }

        bool x = extended ?? (i > 0x7FF || m > 0x7FF);

        if (!x && (i > 0x7FF || m > 0x7FF))
            throw new WcpException("HOST", "A standard-frame filter's identifier and mask are 11 bits: at most 7FF.", "can stream on");

        if (i > 0x1FFFFFFF || m > 0x1FFFFFFF)
            throw new WcpException("HOST", "An extended filter's identifier and mask are 29 bits: at most 1FFFFFFF.", "can stream on");

        string digits = x ? "X8" : "X3";

        Request(string.Create(CultureInfo.InvariantCulture,
            $"can stream on {i.ToString(digits, CultureInfo.InvariantCulture)} {m.ToString(digits, CultureInfo.InvariantCulture)} {(x ? WcpWire.ExtendedFlag : WcpWire.StandardFlag)}"));
    }

    /// <summary><c>can stream off</c>.</summary>
    public void CanStreamOff() => Request("can stream off");

    // ----- CAN, transmit -----------------------------------------------------

    /// <summary>
    /// <c>can arm</c>: allows transmitting on any identifier until <see cref="CanDisarm"/>,
    /// a reboot, or 10 s without a request. A ping then goes out every
    /// <see cref="PingInterval"/> for as long as it lasts.
    ///
    /// Nothing here asks a person. That is the application's job, once per arming,
    /// and it is why this is a method of its own rather than something
    /// <see cref="CanSend"/> does on the way.
    /// </summary>
    public void CanArm()
    {
        Request("can arm");
        SetArmed();
    }

    /// <summary>
    /// <c>can resume</c>: clears a latched transmit fault (<c>!DISARM fault …</c>),
    /// after which the unit may be armed again. It does not arm; but it is the step
    /// that lets transmit back on after the bus said something was wrong, so the
    /// application asks a person first, as for arming.
    /// </summary>
    public void CanResume()
    {
        Request(WcpWire.CanResume);

        lock (_gate) _fault = null;
    }

    /// <summary><c>can disarm</c>: ends transmit at once.</summary>
    public void CanDisarm()
    {
        try
        {
            Request("can disarm");
        }
        catch (WcpException e) when (e.Code == "NOTARMED")
        {
            // Already the state being asked for.
        }

        ClearArming();
    }

    /// <summary>
    /// <c>can send &lt;id_hex&gt; [x] &lt;data_hex&gt;</c>: one frame, 0 to 8 bytes.
    ///
    /// <para>
    /// Refused here when this host does not believe the unit is armed, which saves
    /// a round trip and is no substitute for the device refusing it too. Never
    /// retried on a timeout: see the class remarks. The 200 frames/s cap comes back
    /// as a <see cref="WcpException"/> with <see cref="WcpException.IsRateLimited"/>
    /// set and is not retried either — the cap is there to stop a runaway loop, and
    /// retrying is what a runaway loop does.
    /// </para>
    /// </summary>
    /// <param name="extended">Send as a 29-bit identifier. Required for one above 0x7FF.</param>
    /// <returns>The device's summary, "sent".</returns>
    public string CanSend(uint id, ReadOnlySpan<byte> data, bool extended = false)
    {
        if (data.Length > 8)
            throw new WcpException("HOST", $"A CAN frame carries at most 8 bytes, not {data.Length}.", "can send");

        if (id > 0x1FFFFFFF || !extended && id > 0x7FF)
            throw new WcpException("HOST", $"0x{id:X} is not a {(extended ? "29" : "11")}-bit identifier.", "can send");

        if (!IsArmed)
            throw new WcpException("NOTARMED", "Transmit is not armed. Arm it first.", "can send");

        string idText = extended ? id.ToString("X8", CultureInfo.InvariantCulture) : id.ToString("X3", CultureInfo.InvariantCulture);
        string command = data.IsEmpty
            ? $"can send {idText}{(extended ? " x" : "")}"
            : $"can send {idText}{(extended ? " x" : "")} {Convert.ToHexString(data)}";

        try
        {
            return Request(command, retryOnTimeout: false).Summary;
        }
        catch (WcpException e) when (e.Code == "NOTARMED")
        {
            // The device disarmed and the event saying so was lost; believe it.
            ClearArming();
            throw;
        }
    }

    /// <summary><c>can txlog</c>: the last 32 frames transmitted, as the device lists them.</summary>
    public IReadOnlyList<string> CanTxLog() => Request("can txlog").Data;

    // ----- Lua ---------------------------------------------------------------

    /// <summary>
    /// The most script bytes in one <c>lua put</c>. A request is at most 512 bytes
    /// and the command, name, offset, seq and CRC take up to about 60 of them, so
    /// 300 bytes — 400 characters of base64 — leaves a margin.
    /// </summary>
    public const int MaxLuaChunk = 300;

    /// <summary>
    /// How long <c>lua eval</c> is waited for (contract 8.6). The firmware gives a
    /// chunk 5 s, so the 3 s default would give up on one still within its limit.
    /// </summary>
    public static readonly TimeSpan LuaEvalTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long <c>lua run</c> is waited for: it compiles and runs the script's top level.</summary>
    public static readonly TimeSpan LuaRunTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The most Lua source <c>lua eval</c> takes, decoded, in bytes: 360 (contract
    /// 8.11, which replaced 8.6's 4 KB). As base64 that is 480 characters, which with
    /// the seq, the command and the CRC is just inside the 512-byte request.
    /// </summary>
    public const int MaxEvalBytes = 360;

    /// <summary><c>lua status</c>: the engine, whether scripts may transmit, and each script.</summary>
    public WcpLuaStatus LuaStatus()
    {
        WcpReply reply = Request("lua status");

        // The engine's key=value lines, and one line per script that is itself
        // key=value pairs: script=<name> state=… mem_kb=… (contract 8.8).
        var scripts = new List<WcpLuaScript>();
        var engine = new List<string>();

        foreach (string line in reply.Data)
        {
            if (!line.StartsWith("script=", StringComparison.Ordinal))
            {
                engine.Add(line);
                continue;
            }

            var fields = new WcpValues(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            scripts.Add(new WcpLuaScript(
                fields["script"], fields["state"], fields.Long("mem_kb") ?? 0, fields.Long("cb_max_ms") ?? 0,
                fields.Long("queue_drops") ?? 0, fields.Long("errors") ?? 0, fields.Long(WcpWire.StackFree)));
        }

        return new WcpLuaStatus(new WcpValues(engine), scripts);
    }

    /// <summary><c>lua list</c>: the stored scripts.</summary>
    public IReadOnlyList<WcpLuaFile> LuaList()
    {
        var files = new List<WcpLuaFile>();

        foreach (string line in Request("lua list").Data)
        {
            string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;

            files.Add(new WcpLuaFile(p[0],
                p.Length > 1 && long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size) ? size : -1));
        }

        return files;
    }

    /// <summary>
    /// Uploads a script with <c>lua put &lt;name&gt; &lt;offset&gt; &lt;base64&gt;</c>, a chunk
    /// at a time; the first chunk, at offset 0, truncates what was there.
    ///
    /// <para>
    /// Each answer is <c>OK n=0 &lt;file_total_bytes&gt;</c> (contract 8.7), and the total
    /// must be where this chunk ends, or the upload has failed.
    /// </para>
    ///
    /// <para>
    /// <b>A chunk whose answer was lost</b> (review item H1). The firmware refuses a
    /// chunk at any offset but the file's end (contract 8.12), so a chunk that
    /// landed and is simply sent again is refused as an overlap. The retry keeps its
    /// seq, so a firmware with the reply store answers it from there; one without,
    /// or a store that has moved on, refuses it. Either way, a timeout or that
    /// refusal is followed by <c>lua list</c>: if the stored size is where this chunk
    /// ends, it landed and the upload goes on; if it is where it starts, it did not
    /// and is sent again; anything else, and the upload starts over from 0, which
    /// truncates. A first chunk is always simply sent again, since offset 0 truncates
    /// whatever is there.
    /// </para>
    ///
    /// <para>Uploading does not run anything. <see cref="LuaRun"/> does.</para>
    /// </summary>
    /// <returns>The size uploaded, in bytes.</returns>
    public long LuaPut(string name, ReadOnlySpan<byte> script)
    {
        CheckScriptName(name);

        if (script.IsEmpty) throw new WcpException("HOST", "A script with nothing in it is not worth uploading.", "lua put");

        byte[] bytes = script.ToArray();
        int recoveries = 0;
        int offset = 0;

        while (offset < bytes.Length)
        {
            int length = Math.Min(MaxLuaChunk, bytes.Length - offset);
            string b64 = Convert.ToBase64String(bytes, offset, length);
            string command = string.Create(CultureInfo.InvariantCulture, $"lua put {name} {offset} {b64}");

            WcpReply reply;

            try
            {
                reply = Request(command);
            }
            catch (Exception e) when (e is TimeoutException || e is WcpException { Code: "ARGS" } && offset > 0)
            {
                if (++recoveries > MaxPutRecoveries) throw;

                // The first chunk truncates, so sending it again is always right;
                // and the size of whatever was there before says nothing about it.
                if (offset == 0)
                {
                    LuaPutRecoveries++;
                    continue;
                }

                long stored = StoredSize(name);

                if (stored == offset + length)
                {
                    LuaPutRecoveries++;
                    offset += length;
                    continue;
                }

                // An ARGS that is not an overlap — the chunk did not land — is the
                // device refusing the chunk itself, and sending it again would only
                // be refused again.
                if (e is WcpException) throw;

                LuaPutRecoveries++;
                offset = stored == offset ? offset : 0;
                continue;
            }

            string first = reply.Summary.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

            if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out long said)
                || said != offset + length)
                throw new InvalidDataException(
                    $"Uploading {name}: the WiCAN acknowledged {(first.Length > 0 ? first : "nothing")} bytes "
                    + $"for the {length} at {offset}.");

            offset += length;
        }

        return bytes.Length;
    }

    /// <summary>How many lost chunks one upload recovers from before it gives up.</summary>
    public const int MaxPutRecoveries = 3;

    /// <summary>Chunks of an upload recovered with <c>lua list</c> after a lost answer (see <see cref="LuaPut"/>).</summary>
    public int LuaPutRecoveries { get; private set; }

    /// <summary>A stored script's size, by <c>lua list</c>; -1 when it is not there.</summary>
    private long StoredSize(string name) =>
        LuaList().FirstOrDefault(f => f.Name == name)?.Size ?? -1;

    /// <summary><c>lua get &lt;name&gt;</c>: a stored script's bytes.</summary>
    public byte[] LuaGet(string name)
    {
        CheckScriptName(name);

        IReadOnlyList<string> lines = Request($"lua get {name}").Data;

        return DecodeBase64Lines(lines) ?? throw new InvalidDataException($"{name} did not arrive as base64.");
    }

    /// <summary><c>lua delete &lt;name&gt;</c>: stops it if it is running, then removes it.</summary>
    public void LuaDelete(string name)
    {
        CheckScriptName(name);
        Request($"lua delete {name}");
    }

    /// <summary>
    /// <c>lua run &lt;name&gt;</c>: loads and starts a stored script now. A compile
    /// error comes back as a <see cref="WcpException"/> with code <c>ARGS</c> and the
    /// error as its message. Not retried on a timeout: a script that started and
    /// whose "OK" was lost would be started again.
    /// </summary>
    public void LuaRun(string name)
    {
        CheckScriptName(name);
        Request($"lua run {name}", Longer(LuaRunTimeout), retryOnTimeout: false);
    }

    /// <summary>
    /// <c>lua stop [name]</c>: one script, or — with no name — all of them. Without a
    /// name this is the kill switch, and it stops Lua transmitting at once.
    /// </summary>
    /// <exception cref="WcpException">
    /// <c>STATE</c> when a script did not stop within the firmware's 2 s, naming the
    /// ones still stopping (<c>ERR STATE timeout stopping=a,b</c>).
    /// </exception>
    public void LuaStop(string? name = null)
    {
        if (name is not null) CheckScriptName(name);

        string command = name is null ? "lua stop" : $"lua stop {name}";

        try
        {
            Request(command, Longer(LuaStopTimeout));
        }
        catch (WcpException e) when (e.Code == "STATE" && e.Message.Contains(WcpWire.Stopping, StringComparison.Ordinal))
        {
            string names = e.Message[(e.Message.IndexOf(WcpWire.Stopping, StringComparison.Ordinal) + WcpWire.Stopping.Length)..].Trim();

            throw new WcpException(e.Code,
                $"Not every script stopped within the WiCAN's 2 s; still stopping: {names}. The rest are stopped. "
                + "Ask again, or restart the WiCAN to be sure.", e.Command);
        }
    }

    /// <summary>How long <c>lua stop</c> is waited for: the firmware gives the scripts 2 s, and the review asked for 10 here.</summary>
    public static readonly TimeSpan LuaStopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>lua eval &lt;base64&gt;</c>: a one-off chunk in a scratch VM, with its output.
    ///
    /// <para>
    /// The code travels as base64 (contract 8.6), so it may be several lines, up to
    /// <see cref="MaxEvalBytes"/> — 360 bytes, what fits in one 512-byte request as
    /// base64 (contract 8.11). Longer code is refused here with that said, and is
    /// better uploaded and run. Its output and its error are unescaped (see
    /// <see cref="Wcp.Unescape"/>), and a chunk stopped at the time limit says so.
    /// </para>
    /// </summary>
    public WcpConsoleResult LuaEval(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        byte[] bytes = Encoding.UTF8.GetBytes(code);

        if (bytes.Length > MaxEvalBytes)
            throw new WcpException("HOST",
                $"lua eval takes at most {MaxEvalBytes} bytes of code, not {bytes.Length}: that is what fits, as base64, "
                + $"in one {Wcp.MaxRequestBytes}-byte request. Upload it with lua put and run it instead.", "lua eval");

        string command = $"lua eval {Convert.ToBase64String(bytes)}";

        WcpReply reply = Exchange(command, Longer(LuaEvalTimeout), retryOnTimeout: false);

        // The output arrives escaped (review item F11); undone for whoever reads it.
        string error = reply.Ok ? "" : $"{reply.Code} {Wcp.Unescape(reply.Summary)}".Trim();

        if (!reply.Ok && reply.Code == "STATE" && WcpWire.Says(reply.Summary, WcpWire.LuaTimeout))
            error = $"{error} (the chunk ran past the WiCAN's time limit and was stopped)";

        return new WcpConsoleResult(reply.Ok, [.. reply.Data.Select(Wcp.Unescape)], error);
    }

    /// <summary><c>lua resume-tx</c>: lifts a bus-off suspension of Lua transmit.</summary>
    public void LuaResumeTx() => Request("lua resume-tx");

    /// <summary>
    /// A script's name as the device stores it, <c>/littlefs/lua/&lt;name&gt;.lua</c>:
    /// letters, digits, <c>_</c> and <c>-</c>, so it can be neither a path nor two words.
    /// </summary>
    public static bool IsScriptName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 32 && name != "-"
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void CheckScriptName(string name)
    {
        if (!IsScriptName(name))
            throw new WcpException("HOST",
                $"'{name}' is not a script name: up to 32 letters, digits, '_' or '-', without '.lua'.", "lua");
    }

    // ----- keep-alive --------------------------------------------------------

    private void StartPinging()
    {
        if (_disposed) return;

        try
        {
            _pinger.Change(PingInterval, PingInterval);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StopPinging()
    {
        try
        {
            _pinger.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetArmed()
    {
        lock (_gate) _armedSince ??= DateTime.UtcNow;

        UpdatePinging();
    }

    /// <summary>Forgets the arming; true if there was one to forget.</summary>
    private bool ClearArming()
    {
        bool was;

        lock (_gate)
        {
            was = _armedSince is not null;
            _armedSince = null;
        }

        UpdatePinging();
        return was;
    }

    /// <summary>
    /// Whether anything the firmware ends after 10 s of silence is in force here:
    /// the arming, an unlock, or staged settings (review item H3).
    /// </summary>
    private bool NeedsKeepAlive
    {
        get
        {
            lock (_gate) return _armedSince is not null || _unlocked || _staged;
        }
    }

    /// <summary>Starts or stops the keep-alive to match <see cref="NeedsKeepAlive"/>.</summary>
    private void UpdatePinging()
    {
        lock (_pingGate)
        {
            bool needed = NeedsKeepAlive && !_disposed;

            if (needed == _pinging) return;

            _pinging = needed;

            if (needed) StartPinging();
            else StopPinging();
        }
    }

    private readonly Lock _pingGate = new();
    private bool _pinging;

    /// <summary>
    /// One tick of the keep-alive.
    ///
    /// Skipped, not queued, when a request already holds the slot: that request
    /// reached the device and counts as the keep-alive itself, and a ping queued
    /// behind it would only add a second one late.
    /// </summary>
    private void KeepAlive()
    {
        if (_disposed) return;

        if (!NeedsKeepAlive)
        {
            UpdatePinging();
            return;
        }

        if (!_slot.Wait(0)) return;

        try
        {
            Pings++;

            // Short and never retried: a ping that is late is replaced by the
            // next one, and one retried would arrive with it.
            ExchangeHoldingSlot("ping", Timeout < TimeSpan.FromSeconds(1) ? Timeout : TimeSpan.FromSeconds(1), retryOnTimeout: false);
        }
        catch (Exception e) when (e is TimeoutException or IOException or WcpException or InvalidOperationException
                                      or ObjectDisposedException or WcpNotSupportedException)
        {
            PingFailures++;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Stops the keep-alive and lets go of the link. Disarms first if armed and
    /// the link is still there: the device would disarm on its own 10 s after the
    /// pings stop, and a car should not spend those 10 s armed for nobody.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (IsArmed && _port.IsOpen)
        {
            try
            {
                CanDisarm();
            }
            catch (Exception e) when (e is WcpException or TimeoutException or IOException or InvalidOperationException
                                          or WcpNotSupportedException)
            {
            }
        }

        _disposed = true;

        lock (_pingGate)
        {
            _pinging = false;
            StopPinging();
        }

        _pinger.Dispose();

        _port.LineReceived -= OnLine;
        _port.Closed -= OnClosed;
    }
}
