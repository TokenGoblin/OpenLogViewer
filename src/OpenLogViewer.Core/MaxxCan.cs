using System.Buffers.Binary;

namespace OpenLogViewer.Core;

/// <summary>
/// A MaxxECU relaying its CAN bus over USB — what MTune calls the CAN Analyzer.
///
/// <para>
/// The ECU keeps received frames in a ring of sixty seventeen-byte records and
/// hands them over on command <c>0x38</c>, emptying what it gives. Records carry
/// the identifier as a full 32-bit word, so 29-bit extended identifiers arrive
/// intact, and each one has a hardware microsecond timestamp taken when the frame
/// arrived rather than when it was collected — which is what makes bus timing
/// worth measuring rather than guessing at.
/// </para>
/// <para>
/// <b>It sees the whole bus.</b> The controller's hardware acceptance filter is
/// set to match everything — identifier nought, mask nought — and the analyzer is
/// fed from the driver before any of the ECU's own identifier matching happens.
/// So frames the ECU is not configured to use, and identifiers nobody has told it
/// about, all reach the ring. That is what makes this useful for reading a bus
/// nobody has documented, and it is not something to assume of a relay: an ECU
/// that filtered in hardware would show only what it already knew.
/// </para>
/// <para>
/// Read out of the 1.151 firmware by the analysis in the TCU repository — the
/// ring producer, the filter set-up, and the superloop drain that feeds it. Not
/// yet confirmed on a wire, which for the promiscuity claim above means: enable
/// it, put the ECU on any bus, and watch for an identifier it has no reason to
/// know.
/// </para>
/// </summary>
public static class MaxxCan
{
    /// <summary>The command that empties the ring.</summary>
    public const byte Take = 0x38;

    /// <summary>How long one record is.</summary>
    public const int RecordLength = 17;

    /// <summary>
    /// Frames one read can carry.
    ///
    /// The ECU caps a reply at 256 bytes whatever is being read, and fifteen
    /// records is 255 of them. Asking for sixteen asks for 272 and is refused, so
    /// this is the ceiling rather than a choice.
    /// </summary>
    public const int MostPerRead = MaxxTune.MaximumWrite / RecordLength;

    /// <summary>
    /// The setting that turns the analyzer on, as MTune names it.
    ///
    /// Nothing reaches the ring while it is nought, and the ECU clears it when the
    /// link drops — so it is armed for a session rather than set once and
    /// forgotten. It lives in the tune like any other setting, which means
    /// <see cref="MaxxTune.Write"/> can set it, and a MaxxECU write is permanent
    /// as it lands.
    /// </summary>
    public const string EnableSetting = "CAN Analyzer Enable";

    /// <summary>Where that setting lives, which the firmware reads directly.</summary>
    public const int EnableAt = 0xCDB2;

    /// <summary>
    /// Pulls frames out of a reply.
    ///
    /// The record is a length and flags byte, a 32-bit identifier, a 32-bit
    /// microsecond stamp, then the data. The low nibble of the first byte is how
    /// many of the eight data bytes mean anything; the rest of them are whatever
    /// the ring happened to hold and must not be shown as payload.
    /// </summary>
    public static IReadOnlyList<CanFrame> Frames(ReadOnlySpan<byte> reply)
    {
        var frames = new List<CanFrame>(reply.Length / RecordLength);

        for (int at = 0; at + RecordLength <= reply.Length; at += RecordLength)
        {
            ReadOnlySpan<byte> record = reply.Slice(at, RecordLength);

            int length = record[0] & 0x0F;

            // A record the ECU never filled, which is what an empty ring slot
            // reads as. Nought-length frames are legal on a bus, but a slot that
            // is entirely zero has no identifier and no stamp either, and taking
            // it produces a phantom frame at identifier nought.
            if (length == 0 && record[1..].TrimStart((byte)0).IsEmpty) continue;

            if (length > 8) length = 8;

            frames.Add(new CanFrame(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[1..]),

                // The flag beside the length. The firmware copies it from the
                // driver's own record and it has not been established whether it
                // marks an extended identifier or simply a filled slot, so the
                // identifier is trusted over it: anything past eleven bits is
                // extended because it cannot be anything else.
                (record[0] & 0x10) != 0
                    || BinaryPrimitives.ReadUInt32LittleEndian(record[1..]) > 0x7FF,

                record.Slice(9, length).ToArray(),
                BinaryPrimitives.ReadUInt32LittleEndian(record[5..])));
        }

        return frames;
    }

    /// <summary>
    /// Asks the ECU for up to <paramref name="frames"/> of them.
    ///
    /// What comes back is emptied from the ring, so a reply not read is a reply
    /// lost — there is no asking again.
    /// </summary>
    public static byte[] Ask(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frames, MostPerRead);

        return MaxxUsbProtocol.Request(MaxxUsbProtocol.Read, Take, 0, frames * RecordLength);
    }
}
