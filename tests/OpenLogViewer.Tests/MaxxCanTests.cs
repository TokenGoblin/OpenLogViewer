using System.Buffers.Binary;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Reading a MaxxECU's CAN ring — what MTune calls the CAN Analyzer.
///
/// The records are composed here to the layout read out of the 1.151 firmware:
/// seventeen bytes, a length and flag byte, a 32-bit identifier, a 32-bit
/// microsecond stamp, and eight data bytes of which only the declared length
/// means anything. Nothing about this has been on a wire yet, so these tests are
/// what the decode is checked against until an ECU is on a bus.
/// </summary>
public class MaxxCanTests
{
    /// <summary>Lays one record out the way the ECU does.</summary>
    private static byte[] Record(
        uint id, int length, uint at, byte[] data, bool flag = false, byte[]? tail = null)
    {
        var record = new byte[MaxxCan.RecordLength];

        record[0] = (byte)((length & 0x0F) | (flag ? 0x10 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), id);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(5), at);

        // The whole eight bytes are written, because the ring does not clear what
        // a shorter frame leaves behind.
        (tail ?? data).CopyTo(record, 9);
        data.CopyTo(record, 9);

        return record;
    }

    [Fact]
    public void AFrameComesOutOfItsRecord()
    {
        byte[] reply = Record(0x7E8, 8, 1_234_567, [1, 2, 3, 4, 5, 6, 7, 8]);

        CanFrame frame = Assert.Single(MaxxCan.Frames(reply));

        Assert.Equal(0x7E8, frame.Id);
        Assert.False(frame.IsExtended);
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6, 7, 8], frame.Data);
        Assert.Equal(1_234_567, frame.At);
        Assert.Equal("7E8", frame.Label);
    }

    /// <summary>
    /// A 29-bit identifier arrives whole.
    ///
    /// The record carries the identifier as a full 32-bit word for this reason.
    /// Masked to eleven bits — the obvious thing to do, and wrong — an extended
    /// frame is filed under the low bits it happens to share with something else,
    /// which on a bus that uses both looks like one identifier behaving
    /// erratically rather than two identifiers.
    /// </summary>
    [Fact]
    public void AnExtendedIdentifierIsNotTruncated()
    {
        byte[] reply = Record(0x18DAF110, 8, 0, [0, 0, 0, 0, 0, 0, 0, 0], flag: true);

        CanFrame frame = Assert.Single(MaxxCan.Frames(reply));

        Assert.Equal(0x18DAF110, frame.Id);
        Assert.True(frame.IsExtended);
        Assert.Equal("18DAF110", frame.Label);
    }

    /// <summary>
    /// An identifier too big for eleven bits is extended whatever the flag says.
    ///
    /// The flag beside the length is copied from the driver and has not been
    /// pinned down — it may mark an extended frame or merely a filled slot. The
    /// identifier cannot be ambiguous in the same way, so it decides.
    /// </summary>
    [Fact]
    public void AWideIdentifierIsExtendedEvenWithoutTheFlag()
    {
        byte[] reply = Record(0x1FFFFF, 1, 0, [0xAA], flag: false);

        Assert.True(Assert.Single(MaxxCan.Frames(reply)).IsExtended);
    }

    /// <summary>
    /// Only the declared bytes are payload.
    ///
    /// The ring does not wipe a slot before reusing it, so a three-byte frame
    /// landing where an eight-byte one was leaves five bytes of the old one
    /// behind. Reporting those as data invents payload that was never on the bus
    /// — and on an undocumented bus, invented bytes are what somebody then spends
    /// an evening trying to decode.
    /// </summary>
    [Fact]
    public void TheBytesAfterTheDeclaredLengthAreNotPayload()
    {
        byte[] reply = Record(
            0x100, 3, 0, [0x11, 0x22, 0x33], tail: [9, 9, 9, 9, 9, 9, 9, 9]);

        CanFrame frame = Assert.Single(MaxxCan.Frames(reply));

        Assert.Equal(3, frame.Length);
        Assert.Equal<byte[]>([0x11, 0x22, 0x33], frame.Data);
        Assert.Equal("112233", frame.Hex);
    }

    /// <summary>
    /// Several frames come out of one reply, in the order the ring held them.
    /// </summary>
    [Fact]
    public void AReplyCarriesSeveralFramesInOrder()
    {
        byte[] reply =
        [
            .. Record(0x100, 1, 10, [1]),
            .. Record(0x200, 1, 20, [2]),
            .. Record(0x300, 1, 30, [3]),
        ];

        IReadOnlyList<CanFrame> frames = MaxxCan.Frames(reply);

        Assert.Equal([0x100, 0x200, 0x300], frames.Select(f => f.Id));
        Assert.Equal([10L, 20L, 30L], frames.Select(f => f.At));
    }

    /// <summary>
    /// An empty slot is not a frame at identifier nought.
    ///
    /// A ring the ECU has not filled reads as zeroes, and a zero record has a
    /// legal length — nought — so taking it at face value produces a phantom
    /// frame every time the bus is quiet. Identifier nought is also a real and
    /// very high priority identifier on a live bus, so the phantom lands
    /// somewhere it would be believed.
    /// </summary>
    [Fact]
    public void AnEmptySlotIsNotAFrame()
    {
        Assert.Empty(MaxxCan.Frames(new byte[MaxxCan.RecordLength * 3]));

        // But a genuine nought-length frame at a real identifier is one.
        byte[] real = Record(0x123, 0, 99, []);
        CanFrame frame = Assert.Single(MaxxCan.Frames(real));

        Assert.Equal(0x123, frame.Id);
        Assert.Empty(frame.Data);
    }

    /// <summary>A reply that stops mid-record gives up what is whole and no more.</summary>
    [Fact]
    public void AHalfRecordAtTheEndIsIgnored()
    {
        byte[] reply = [.. Record(0x100, 1, 5, [7]), 0x01, 0x02, 0x03];

        Assert.Single(MaxxCan.Frames(reply));
    }

    /// <summary>
    /// A read asks for whole records, and for no more than the ECU will send.
    ///
    /// The 256-byte reply cap is not a choice here: fifteen records is 255 bytes
    /// and sixteen is 272, which is refused outright rather than truncated.
    /// </summary>
    [Fact]
    public void AReadAsksForWholeRecordsWithinTheReplyCap()
    {
        Assert.Equal(15, MaxxCan.MostPerRead);

        byte[] request = MaxxCan.Ask(MaxxCan.MostPerRead);

        Assert.Equal(MaxxUsbProtocol.Read, request[0]);
        Assert.Equal(MaxxCan.Take, request[1]);
        Assert.Equal(255, request[4] | (request[5] << 8));
        Assert.True(255 <= MaxxTune.MaximumWrite);

        Assert.Throws<ArgumentOutOfRangeException>(() => MaxxCan.Ask(MaxxCan.MostPerRead + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxxCan.Ask(0));
    }

    /// <summary>
    /// The request is a well-formed one, checksum and all, so it goes out like any
    /// other.
    /// </summary>
    [Fact]
    public void TheRequestIsAnOrdinaryReadWithItsChecksum()
    {
        byte[] request = MaxxCan.Ask(4);

        Assert.Equal(MaxxUsbProtocol.RequestLength, request.Length);
        Assert.Equal(
            MaxxProtocol.Crc32(request.AsSpan(0, 6)),
            BitConverter.ToUInt32(request, 6));
    }

    /// <summary>
    /// The dropped-frame counter is inside the window the runtime-snapshot command
    /// will answer, so reading it needs no command of its own.
    ///
    /// Worth pinning as arithmetic rather than a remembered number: the counter is
    /// at 0x2000881E, the window starts at 0x20007DAC and the firmware's bound is 3,201,
    /// and if
    /// either of those ever turns out different the offset is wrong in a way that
    /// reads plausible rubbish rather than failing.
    /// </summary>
    [Fact]
    public void TheDropCounterIsInsideTheSnapshotWindow()
    {
        Assert.Equal(0x2000881E - 0x20007DAC, MaxxCan.DropCountAt);
        Assert.Equal(2674, MaxxCan.DropCountAt);

        // Two bytes of it, inside the bound the firmware enforces. The bound is
        // exclusive, so the window is 3,200 bytes and 3,201 is the first total
        // refused — not the 3,288 MTune asks for and is told 0x30 about.
        Assert.True(MaxxCan.DropCountAt + 2 <= MaxxCan.SnapshotWindow);
        Assert.Equal(3201, MaxxCan.SnapshotLimit);
        Assert.Equal(3200, MaxxCan.SnapshotWindow);
    }

    /// <summary>
    /// Losses are counted as a distance, so a counter that wrapped still reports
    /// more rather than less.
    ///
    /// Sixteen bits, free-running, never reset. On a bus shedding frames steadily
    /// it comes all the way round in well under a minute, and a capture that
    /// subtracted its starting reading would report the remainder — seventy
    /// thousand frames lost, reported as four and a half thousand. That is worse
    /// than admitting ignorance, because it looks like a measurement.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 25, 15)]
    [InlineData(65_530, 4, 10)]
    [InlineData(65_535, 0, 1)]
    [InlineData(0, 65_535, 65_535)]
    public void LossesAreCountedAsADistanceAndNotASubtraction(int last, int now, int expected) =>
        Assert.Equal(expected, MaxxCan.Since(last, now));

    /// <summary>
    /// The analyzer's enable flag is where the firmware reads it, and it is a
    /// setting somebody can be shown by name rather than an address to poke.
    /// </summary>
    [Fact]
    public void TheEnableFlagIsANamedSettingAtTheAddressTheFirmwareReads()
    {
        Assert.Equal(52658, MaxxCan.EnableAt);
        Assert.Equal("CAN Analyzer Enable", MaxxCan.EnableSetting);
    }
}
