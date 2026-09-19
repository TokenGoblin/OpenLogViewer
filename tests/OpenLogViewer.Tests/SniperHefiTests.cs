using System.Buffers.Binary;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Layer 3 of the Holley Sniper protocol — message building, CRC, block
/// reassembly and opcode taxonomy, per <c>SNIPER_ECU_CLIENT_SPEC.md</c> §3.
/// None of this has been run against a real dongle; what these tests pin
/// down is that the implementation does what the spec's prose describes,
/// which is a different and weaker claim than "this is what a real Sniper
/// ECU accepts".
/// </summary>
public class SniperHefiTests
{
    /// <summary>
    /// §3.1 step 3 says the CRC-32 is "standard... i.e. zlib.crc32" — checked
    /// against the algorithm's own canonical check value, the same one every
    /// CRC-32/ISO-HDLC implementation is validated against: the CRC of the
    /// ASCII bytes "123456789" is 0xCBF43926. Independent of anything else in
    /// this codebase, so a bug shared between the implementation and a
    /// hand-rolled comparison could not hide itself.
    /// </summary>
    [Fact]
    public void StandardCrc32MatchesItsOwnCanonicalCheckValue() =>
        Assert.Equal(0xCBF43926u, StandardCrc32.Compute("123456789"u8));

    /// <summary>
    /// §3.1 step 3: the CRC covers the buffer <i>excluding the first (marker)
    /// word</i>, stored big-endian as the message's last four bytes.
    /// </summary>
    [Fact]
    public void BuildMessageAppendsACrcOverEverythingAfterTheMarker()
    {
        byte[] message = SniperHefi.BuildMessage(0x01020304, 0x05060708, 0x090a0b0c, []);

        uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(message.Length - 4));
        uint expected = StandardCrc32.Compute(message[4..^4]);

        Assert.Equal(expected, storedCrc);
    }

    [Fact]
    public void BuildMessageByteSwapsEveryWordSoTheWireHoldsBigEndian()
    {
        // The net effect of "assemble, then byte-swap every word" is that
        // cmd0 lands on the wire as its plain big-endian representation —
        // the second word, right after the 4-byte marker.
        byte[] message = SniperHefi.BuildMessage(0x01020304, 0, 0, []);

        Assert.Equal([0x01, 0x02, 0x03, 0x04], message[4..8]);
    }

    /// <summary>
    /// Confirmed against <c>sniper_can.py:build_hefi_message</c>: a trailing
    /// partial word is left exactly as written, not refused and not padded —
    /// the reference's own swap loop is bounded to
    /// <c>len(buf) - (len(buf) % 4)</c>, skipping any remainder entirely.
    /// </summary>
    [Fact]
    public void BuildMessageLeavesATrailingPartialWordUnswapped()
    {
        byte[] message = SniperHefi.BuildMessage(0, 0, 0, [0xAA, 0xBB, 0xCC]);

        // 4 header words (16 bytes) + 3-byte payload = 19 bytes before the CRC.
        // The payload's odd 3 bytes are the last, non-whole word and must be
        // untouched by the byte-swap pass.
        Assert.Equal([0xAA, 0xBB, 0xCC], message[16..19]);
    }

    [Fact]
    public void BuildMessageLayoutIsMarkerThenThreeCommandWordsThenPayload()
    {
        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD];
        byte[] message = SniperHefi.BuildMessage(0x11111111, 0x22222222, 0x33333333, payload);

        // 4 header words + 1 payload word + 1 CRC word = 24 bytes.
        Assert.Equal(24, message.Length);
    }

    [Fact]
    public void FragmentForSendIsAPlainEightByteChunkerLikeTheReferenceImplementation()
    {
        // Matches sniper_can.py:fragment exactly - no length-header frame, no
        // zero-length close frame; confirmed against the reference file once
        // it became available (see this method's own doc comment for the
        // full story, including the still-unresolved prose disagreement).
        byte[] message = new byte[20];
        IReadOnlyList<byte[]> frames = SniperHefi.FragmentForSend(message);

        Assert.Equal(3, frames.Count); // 8 + 8 + 4
        Assert.Equal(8, frames[0].Length);
        Assert.Equal(8, frames[1].Length);
        Assert.Equal(4, frames[2].Length);
    }

    [Fact]
    public void FragmentForSendSplitsTheBodyIntoEightByteChunks()
    {
        byte[] message = [.. Enumerable.Range(0, 17).Select(i => (byte)i)]; // 17 bytes: 8+8+1

        IReadOnlyList<byte[]> frames = SniperHefi.FragmentForSend(message);

        Assert.Equal(3, frames.Count);
        Assert.Equal(8, frames[0].Length);
        Assert.Equal(8, frames[1].Length);
        Assert.Single(frames[2]);
    }

    [Fact]
    public void ReassemblerCompletesOnceTheDeclaredLengthArrives()
    {
        var reassembler = new SniperBlockReassembler();

        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, SniperHefi.BlockReplyMarker);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 4 + 10); // expected_len = 10

        reassembler.Offer(header);
        Assert.False(reassembler.IsComplete);

        reassembler.Offer([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.False(reassembler.IsComplete);

        reassembler.Offer([9, 10]);
        Assert.True(reassembler.IsComplete);
        Assert.Equal(10, reassembler.Result.Length);
        Assert.Equal((byte)1, reassembler.Result[0]);
        Assert.Equal((byte)10, reassembler.Result[9]);
    }

    [Fact]
    public void ReassemblerTreatsANonMatchingFirstFrameAsOrdinaryPayload()
    {
        var reassembler = new SniperBlockReassembler();

        // Eight bytes that do not start with the block-reply marker.
        reassembler.Offer([0, 0, 0, 0, 0, 0, 0, 0]);
        reassembler.Offer([]); // zero-dlc flush with no known expected length: no-op

        Assert.False(reassembler.IsComplete);
        Assert.Equal(8, reassembler.Result.Length);
    }

    [Theory]
    [InlineData(0x40000a01u, true)]
    [InlineData(0x40000a03u, true)]
    [InlineData(0x40000a11u, true)] // different table nibble, same direction
    [InlineData(0x40000a02u, false)]
    [InlineData(0x40000a00u, false)]
    [InlineData(0x50000a01u, false)] // wrong high bytes entirely
    public void IsReadRecognisesOnlyReadDirectionOpcodes(uint opcode, bool expected) =>
        Assert.Equal(expected, SniperOpcodes.IsRead(opcode));

    [Theory]
    [InlineData(0x40000a02u, true)]
    [InlineData(0x40000a62u, true)]
    [InlineData(0x40000a01u, false)]
    [InlineData(0x40000a03u, false)]
    public void IsWriteRecognisesOnlyWriteDirectionOpcodes(uint opcode, bool expected) =>
        Assert.Equal(expected, SniperOpcodes.IsWrite(opcode));

    [Fact]
    public void EveryWriteOpcodeInTheSpecsTableRoundTrips()
    {
        // The eight pairs §3.3 gives verbatim.
        (uint Opcode, int Offset)[] pairs =
        [
            (0x40000a02, 0x040c), (0x40000a62, 0x3e90),
            (0x40000a12, 0x2a8c), (0x40000a72, 0x40a4),
            (0x40000a22, 0x3c54), (0x40000a92, 0x4830),
            (0x40000a42, 0x3d08), (0x40000ac2, 0x4f98),
        ];

        foreach ((uint opcode, int offset) in pairs)
        {
            Assert.True(SniperOpcodes.IsWrite(opcode));
            Assert.Equal(offset, SniperOpcodes.WriteOpcodeToImageOffset[opcode]);
        }
    }
}
