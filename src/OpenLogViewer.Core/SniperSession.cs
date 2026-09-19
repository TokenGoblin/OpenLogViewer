namespace OpenLogViewer.Core;

/// <summary>
/// A session with a Holley Sniper ECU over its USB→CAN dongle — the plumbing
/// from <c>SNIPER_ECU_CLIENT_SPEC.md</c> §4 (connect/discover/identify/sync)
/// and §5 (telemetry poll), built on <see cref="ISniperCanTransport"/>,
/// <see cref="SniperCan"/> and <see cref="SniperHefi"/>.
///
/// <para>
/// <b>Deliberately not an <see cref="ILiveSource"/>.</b> That interface needs
/// fixed, named channels this session cannot honestly provide: §5 says the
/// per-channel offset inside a polled telemetry reply is "not yet mapped" and
/// needs a hardware capture (rev the engine, watch which bytes move) to
/// recover — inventing an offset to make a gauge appear would be exactly the
/// kind of unverified-presented-as-verified this project's own hardware
/// testing discipline exists to prevent (see <c>docs/hardware-testing.md</c>).
/// What this type gives instead is the proven mechanics — send a request,
/// get a reassembled reply — so that once real hardware maps the channel
/// offsets, an <c>ILiveSource</c> adapter can sit on top of this without
/// touching anything below it.
/// </para>
/// <para>
/// <b>Nothing in this type has been run against a real dongle or ECU.</b>
/// Every step is a best-effort reading of a spec whose own author never had
/// hardware to confirm it against — §4 marks the whole session sequence 🟡,
/// "assembled from the evidence", with exact ordering and timing explicitly
/// <b>[CONFIRM ON HW]</b>. Treat a successful <see cref="Open"/> as "the USB
/// and CAN layers work", not as "this talks to a real Sniper ECU" — that
/// claim needs a bench.
/// </para>
/// </summary>
public sealed class SniperSession : IDisposable
{
    /// <summary>
    /// §4: "the flash path uses 1,000,000 (1 Mbit); this is the most likely
    /// bus rate" — 🟡, not confirmed as the rate a live session should use.
    /// </summary>
    public const uint LikelyBitrate = 1_000_000;

    /// <summary>§5: the VE/Learn overlay's poll timer interval.</summary>
    public static readonly TimeSpan TelemetryPollInterval = TimeSpan.FromMilliseconds(2000);

    private readonly ISniperCanTransport _transport;
    private readonly bool _ownsTransport;

    private int _srcNode;
    private int _dstNode = -1;

    public SniperSession(ISniperCanTransport transport, bool ownsTransport = true)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ownsTransport = ownsTransport;
    }

    public bool IsOpen => _transport.IsOpen;

    /// <summary>The ECU's node id, once <see cref="Open"/> has discovered it. -1 until then.</summary>
    public int EcuNode => _dstNode;

    /// <summary>
    /// Opens the transport, sets the bus rate, and attempts node discovery —
    /// §4 steps 1-3. Throws if the transport will not open or no
    /// node-discovery frame arrives within <paramref name="discoveryTimeout"/>.
    ///
    /// This is the point past which "it opened" stops meaning "the USB link
    /// works" and starts meaning "something on the CAN bus answered a ping
    /// the way the spec expects a Sniper ECU to" — a real but much weaker
    /// claim given none of this is hardware-confirmed.
    ///
    /// <b>Open question the reference client raises rather than answers:</b>
    /// <c>sniper_can.py</c> does not implement discovery at all — its
    /// <c>send_hefi</c> just defaults to <c>dst=1, src=0</c> and sends. That
    /// may mean a Sniper ECU's node id is conventionally always 1 and
    /// discovery is unnecessary in practice, or it may mean the reference
    /// client is a simplified debug tool that skips a step the real
    /// <c>Sniper.exe</c> performs — nothing here resolves which. Discovery
    /// remains the primary path because the main spec's §4 calls for it
    /// explicitly; a caller whose discovery times out could reasonably try
    /// <c>dst=1</c> as a fallback, but this method does not do that
    /// automatically since it is exactly the kind of assumption this codebase
    /// tries not to bake in silently.
    /// </summary>
    public void Open(TimeSpan? discoveryTimeout = null)
    {
        _transport.Open();
        _transport.SetBitrate(LikelyBitrate);

        _srcNode = 0;

        _transport.Send(new SniperCanFrame(
            SniperCan.SimpleId(_srcNode, SniperCan.SimpleCommand.PingOrEnumerate), []));

        TimeSpan timeout = discoveryTimeout ?? TimeSpan.FromSeconds(3);
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            SniperCanFrame? frame = _transport.Receive(deadline - DateTime.UtcNow);
            if (frame is null) break;

            if (SniperCan.Classify(frame.CanId) == SniperFrameKind.NodeDiscovery)
            {
                // §4: "the app stores the discovered node id and uses it as
                // dst/src thereafter." Which field of the frame carries the
                // node id is not given by the spec beyond "learn... from
                // NODE-DISCOVERY frames" — read from the low 11 bits of the
                // id, mirroring how every other id in §2 packs a node number,
                // which is a plausible reading rather than a confirmed one.
                _dstNode = (int)(frame.CanId & 0x7FF);
                return;
            }
        }

        throw new TimeoutException(
            "No node-discovery reply arrived from a Sniper ECU within the timeout. Unverified "
            + "against real hardware — this may mean nothing is listening, or that this "
            + "implementation's guess at the discovery handshake is wrong.");
    }

    /// <summary>
    /// Sends one HEFI request and waits for its reassembled reply — the
    /// primitive both calibration reads and telemetry polls are built from.
    /// §3.1 (build) + §3.2 (reassemble) + §4 (this session's own node ids).
    /// </summary>
    public byte[] SendRequest(uint cmd0, uint cmd1, uint cmd2, ReadOnlySpan<byte> payload, TimeSpan? timeout = null)
    {
        if (_dstNode < 0)
            throw new InvalidOperationException("No ECU node discovered yet — call Open() first.");

        byte[] message = SniperHefi.BuildMessage(cmd0, cmd1, cmd2, payload);
        uint bulkId = SniperCan.BulkId(_dstNode, _srcNode);

        foreach (byte[] chunk in SniperHefi.FragmentForSend(message))
            _transport.Send(new SniperCanFrame(bulkId, chunk));

        var reassembler = new SniperBlockReassembler();
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));

        while (!reassembler.IsComplete && DateTime.UtcNow < deadline)
        {
            SniperCanFrame? frame = _transport.Receive(deadline - DateTime.UtcNow);
            if (frame is null) break;

            if (SniperCan.Classify(frame.CanId) == SniperFrameKind.Bulk) reassembler.Offer(frame.Data);
        }

        if (!reassembler.IsComplete)
            throw new TimeoutException(
                $"No complete reply arrived for HEFI command 0x{cmd0:X8} within the timeout.");

        return reassembler.Result;
    }

    /// <summary>
    /// Issues the device-info read (opcode group <c>0x45</c>, §3.3/§4 step 4).
    /// The exact sub-opcode/cmd1/cmd2 values for this group are 🔴 — the spec
    /// only names the group, not the full triplet — so this uses the group
    /// byte alone with cmd1/cmd2 zero, which may not be what a real ECU wants.
    /// </summary>
    public byte[] ReadDeviceInfo(TimeSpan? timeout = null) => SendRequest(0x45, 0, 0, [], timeout);

    /// <summary>
    /// Reads one calibration region by its read opcode (the <c>_a_1</c>
    /// member of a §3.3 triplet — see <see cref="SniperOpcodes.IsRead"/>).
    /// </summary>
    public byte[] ReadCalibrationBlock(uint readOpcode, TimeSpan? timeout = null)
    {
        if (!SniperOpcodes.IsRead(readOpcode))
            throw new ArgumentOutOfRangeException(nameof(readOpcode), $"0x{readOpcode:X8} is not a read opcode.");

        return SendRequest(readOpcode, 0, 0, [], timeout);
    }

    /// <summary>
    /// One telemetry poll, raw — §5's <c>FUN_00492a20(0x40000A01, 0, 0x1008,
    /// 0xf04, callback, flush=1)</c> read, reissued on <see cref="TelemetryPollInterval"/>
    /// by a real client. The mapping from that call's six parameters onto this
    /// session's <c>(cmd0,cmd1,cmd2)</c> triple is 🟡 inferred by position
    /// (cmd0=0x40000A01, cmd1=0, cmd2=0x1008) — the spec shows the call site,
    /// not the generic read wrapper's own parameter list, so the remaining two
    /// arguments (<c>0xf04</c>, <c>flush=1</c>) are not represented here at
    /// all rather than guessed into the wrong slot.
    /// </summary>
    /// <returns>
    /// The raw reassembled reply bytes. <b>Not decoded into named channels</b> —
    /// see this type's own header comment for why.
    /// </returns>
    public byte[] PollTelemetryRaw(TimeSpan? timeout = null) => SendRequest(0x40000A01, 0, 0x1008, [], timeout);

    public void Dispose()
    {
        if (_ownsTransport) _transport.Dispose();
    }
}
