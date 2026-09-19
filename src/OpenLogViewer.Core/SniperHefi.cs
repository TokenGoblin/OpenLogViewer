using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// The standard CRC-32 (poly 0xEDB88320 reflected, init 0xFFFFFFFF, final
/// invert — the same algorithm <c>zlib.crc32</c>/PKZIP use), which
/// <c>SNIPER_ECU_CLIENT_SPEC.md</c> §3.1 calls for.
///
/// Deliberately separate from <see cref="MaxxProtocol.Crc32"/>: that one is
/// CRC-32/MPEG-2 (poly 0x04C11DB7, no reflection, no final invert) — a
/// different algorithm that happens to share a name, not something to reuse
/// here by mistake.
/// </summary>
internal static class StandardCrc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint c = i;

            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;

            table[i] = c;
        }

        return table;
    }

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

        return ~crc;
    }
}

/// <summary>
/// Layer 3 of <c>SNIPER_ECU_CLIENT_SPEC.md</c> — the HEFI application protocol
/// riding on top of the CAN frames <see cref="SniperCan"/> addresses: building
/// an outbound read/write message, reassembling a block reply, and telling a
/// read opcode from a write one.
///
/// <b>Nothing here has been run against real hardware</b> — see the marker
/// value, the open/close block-transfer framing, and the calibration-offset
/// map below for exactly which parts the spec itself calls unconfirmed.
/// </summary>
public static class SniperHefi
{
    /// <summary>
    /// The constant word every outbound message opens with. §3.1: the spec
    /// names it <c>DAT_0054da48</c> in the decompiled app but could not read
    /// its value without a hardware capture — <b>🔴 [CONFIRM ON HW]</b>, and
    /// this remains true even against the reference client
    /// (<c>sniper_can.py:build_hefi_message</c>): it also leaves this at 0
    /// with the same comment, "capture a real exchange to confirm its value".
    /// Zero is a documented placeholder two independent readings of the
    /// decompiled app agree on using, not a value anyone has confirmed the
    /// real dongle accepts.
    /// </summary>
    public const uint UnconfirmedMarker = 0;

    /// <summary>
    /// Builds one outbound HEFI message — replicates <c>FUN_00492e10</c>, §3.1,
    /// and now checked directly against the reference implementation
    /// (<c>sniper_can.py:build_hefi_message</c>), which agrees byte-for-byte:
    /// assemble native-endian, byte-swap whole 4-byte words only, CRC the
    /// rest. ✅ for the whole shape; 🟡 only for whether
    /// <see cref="UnconfirmedMarker"/> is the real marker value (see its own
    /// doc comment — even the reference leaves this unconfirmed).
    /// </summary>
    /// <returns>
    /// The complete big-endian, CRC'd buffer — <b>not yet fragmented</b> into
    /// CAN frames. See <see cref="FragmentForSend"/>.
    /// </returns>
    public static byte[] BuildMessage(uint cmd0, uint cmd1, uint cmd2, ReadOnlySpan<byte> payload)
    {
        int headerWords = 4; // marker, cmd0, cmd1, cmd2
        var buffer = new byte[headerWords * 4 + payload.Length];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), UnconfirmedMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), cmd0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), cmd1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), cmd2);
        payload.CopyTo(buffer.AsSpan(16));

        // Step 2: byte-swap every *whole* 4-byte word in place — the wire is
        // big-endian. The reference implementation swaps
        // `range(0, len(buf) - (len(buf) % 4), 4)`: any trailing partial word
        // (a payload whose length is not a multiple of 4) is left exactly as
        // written rather than refused or padded — confirmed against
        // sniper_can.py:build_hefi_message, not a guess.
        int wholeWords = buffer.Length - (buffer.Length % 4);

        for (int i = 0; i < wholeWords; i += 4)
            buffer.AsSpan(i, 4).Reverse();

        // Step 3: CRC-32 over the buffer excluding the first (marker) word,
        // appended big-endian — matches `zlib.crc32(bytes(buf[4:]))` exactly.
        uint crc = StandardCrc32.Compute(buffer.AsSpan(4));

        var withCrc = new byte[buffer.Length + 4];
        buffer.CopyTo(withCrc, 0);
        BinaryPrimitives.WriteUInt32BigEndian(withCrc.AsSpan(buffer.Length), crc);

        return withCrc;
    }

    /// <summary>
    /// Splits a built message into 8-byte CAN-frame payloads for
    /// <see cref="SniperCanFrame"/> — replicates <c>sniper_can.py:fragment</c>
    /// exactly: a plain 8-byte chunker over the whole built message (marker
    /// through CRC), last chunk short if the length is not a multiple of 8,
    /// nothing more.
    ///
    /// <b>An unresolved disagreement between the two reverse-engineering
    /// sources, not a settled fact.</b> Both <c>SNIPER_ECU_CLIENT_SPEC.md</c>
    /// §3.1 and <c>HEFI_CAN_protocol.md</c>'s prose describe the app as
    /// "opening with a length/handshake frame and closing with a zero-length
    /// frame" around the fragmented body — but <c>sniper_can.py</c>'s own
    /// <c>fragment()</c> implements no such thing, and
    /// <c>HEFI_CAN_protocol.md</c>'s own closing note says "Pure-protocol
    /// helpers (bulk_can_id, build_hefi_message, fragment) unit-tested
    /// offline and pass" — real, if hardware-independent, confirmation that
    /// this plain chunker is what the decompiled code actually does, not
    /// just an untested guess. This implementation follows that — but the
    /// prose disagreement is real and unexplained, not resolved, and a
    /// capture could still show an extra framing step this omits.
    /// <b>[CONFIRM ON HW]</b> either way.
    /// </summary>
    public static IReadOnlyList<byte[]> FragmentForSend(ReadOnlySpan<byte> message)
    {
        var frames = new List<byte[]>();

        for (int at = 0; at < message.Length; at += 8)
        {
            int chunk = Math.Min(8, message.Length - at);
            frames.Add(message.Slice(at, chunk).ToArray());
        }

        return frames;
    }

    /// <summary>
    /// Recognises the first frame of a block reply. §3.2: the reassembler
    /// knows a transfer has begun by this <c>htonl</c> marker value, and the
    /// dword right after it is the total reply length. ✅
    /// </summary>
    public const uint BlockReplyMarker = 0xF2493722;
}

/// <summary>
/// Reassembles a Sniper block-read reply from its CAN-frame fragments —
/// replicates <c>FUN_004d6310</c>, <c>SNIPER_ECU_CLIENT_SPEC.md</c> §3.2. ✅
///
/// One instance per in-flight reply: feed it frame payloads as they arrive
/// via <see cref="Offer"/>; <see cref="IsComplete"/> turns true once
/// <see cref="Result"/> has every expected byte.
/// </summary>
public sealed class SniperBlockReassembler
{
    private readonly List<byte> _buffer = [];
    private int? _expectedLength;
    private bool _started;

    public bool IsComplete { get; private set; }

    /// <summary>The reassembled bytes, valid once <see cref="IsComplete"/> is true.</summary>
    public byte[] Result => [.. _buffer];

    /// <summary>
    /// Feeds one CAN frame's payload into the reassembly. §3.2: the first
    /// 8-byte frame carries the marker and a length dword
    /// (<c>expected_len = payload_len_dword - 4</c>); following frames'
    /// payloads concatenate until that many bytes have arrived; a zero-dlc
    /// frame flushes/pads.
    /// </summary>
    public void Offer(ReadOnlySpan<byte> payload)
    {
        if (IsComplete) return;

        if (payload.Length == 0)
        {
            // "A zero-dlc frame flushes/pads" — the spec does not say pads
            // with what or to where, so this is treated as end-of-transfer
            // when a length is already known, and otherwise ignored.
            if (_started && _expectedLength is { } expected && _buffer.Count >= expected)
                IsComplete = true;

            return;
        }

        if (!_started)
        {
            _started = true;

            if (payload.Length == 8)
            {
                uint marker = BinaryPrimitives.ReadUInt32BigEndian(payload);
                uint lengthDword = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);

                if (marker == SniperHefi.BlockReplyMarker)
                {
                    // "expected_len = payload_len_dword - 4" per §3.2.
                    _expectedLength = (int)lengthDword - 4;
                    return;
                }
            }

            // The first frame did not look like the documented marker+length
            // header. Treated as ordinary payload rather than thrown away,
            // since the spec's own confidence here is ✅ for the shape but the
            // marker itself was never captured against a real reply.
        }

        _buffer.AddRange(payload.ToArray());

        if (_expectedLength is { } len && _buffer.Count >= len) IsComplete = true;
    }
}

/// <summary>
/// The command taxonomy dispatched by "DoQueue" (<c>FUN_0048f040</c>),
/// §3.3-3.4 — telling a read opcode from a write one, and where a write
/// lands in the calibration image.
/// </summary>
public static class SniperOpcodes
{
    /// <summary>
    /// The fixed pattern every per-table opcode triplet shares once both the
    /// table nibble (bits 4-7) and the direction nibble (bits 0-3) are masked
    /// off — <c>0x40000a__</c>, §3.3. ✅
    /// </summary>
    private const uint TripletBase = 0x40000a00;

    /// <summary>
    /// True for a read opcode — <c>0x40000a_1</c>/<c>0x40000a_3</c> in the
    /// per-table triplet family (the table nibble, second-lowest hex digit,
    /// varies per table and is ignored here; only the direction nibble is
    /// checked). Outbound payload for one of these is NULL; the ECU streams
    /// the block back. §3.3. ✅
    /// </summary>
    public static bool IsRead(uint opcode) =>
        (opcode & 0xFFFFFF00) == TripletBase && (opcode & 0x0F) is 0x01 or 0x03;

    /// <summary>
    /// True for a write opcode — <c>0x40000a_2</c>. Outbound payload is the
    /// calibration region being pushed. §3.3. ✅
    /// </summary>
    public static bool IsWrite(uint opcode) =>
        (opcode & 0xFFFFFF00) == TripletBase && (opcode & 0x0F) == 0x02;

    /// <summary>
    /// The write opcode → calibration-image byte offset map, §3.3. ✅ for the
    /// pairs themselves; 🔴 <b>[CONFIRM ON HW]</b> for what each region
    /// actually is — the spec has the offsets from decompiled code but not
    /// the ECU's own names for what lives at them.
    /// </summary>
    public static readonly IReadOnlyDictionary<uint, int> WriteOpcodeToImageOffset = new Dictionary<uint, int>
    {
        [0x40000a02] = 0x040c,
        [0x40000a12] = 0x2a8c,
        [0x40000a22] = 0x3c54,
        [0x40000a42] = 0x3d08,
        [0x40000a62] = 0x3e90,
        [0x40000a72] = 0x40a4,
        [0x40000a92] = 0x4830,
        [0x40000ac2] = 0x4f98,
    };

    /// <summary>
    /// Where a block reply lands in the "current tune" cache, §3.4 — the same
    /// offsets as <see cref="WriteOpcodeToImageOffset"/> plus one more
    /// (<c>+0xd8</c>) that has no corresponding write opcode in §3.3's table.
    /// ✅ for the offsets; the missing write counterpart is left unexplained
    /// by the spec rather than guessed at here.
    /// </summary>
    public static readonly IReadOnlyList<int> ReplyCacheOffsets =
        [0xd8, 0x40c, 0x2a8c, 0x3c54, 0x3d08, 0x3e90, 0x40a4, 0x4830, 0x4f98];
}
