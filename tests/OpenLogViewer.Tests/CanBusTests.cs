using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The half of a sniffer that is not the wire: what is made of the frames once
/// something has handed them over.
///
/// All of this is the same whichever ECU or adapter is on the other end, which
/// is the point — a MaxxECU relaying its bus and a plain CAN adapter should give
/// one tool rather than two that resemble each other.
/// </summary>
public class CanBusTests
{
    private static CanFrame Frame(int id, long at, params byte[] data) => new(id, id > 0x7FF, data, at);

    [Fact]
    public void FramesGatherUnderTheirIdentifier()
    {
        var bus = new CanBus();

        bus.Add([Frame(0x100, 0, 1), Frame(0x200, 10, 2), Frame(0x100, 20, 3)]);

        Assert.Equal(3, bus.Seen);
        Assert.Equal(2, bus.Count);

        CanId first = bus.Ids()[0];
        Assert.Equal(0x100, first.Id);
        Assert.Equal(2, first.Count);
        Assert.Equal<byte[]>([3], first.Data);
    }

    /// <summary>
    /// What moved is tracked to the bit.
    ///
    /// Bytes are not enough. One byte on a vehicle bus routinely carries eight
    /// unrelated flags, so "this byte changes" says nothing about which of them
    /// did — and watching a single bit move when a switch is pressed is the whole
    /// method of reading a bus nobody has documented.
    /// </summary>
    [Fact]
    public void TheBitsThatMoveAreRemembered()
    {
        var bus = new CanBus();

        bus.Add(Frame(0x300, 0, 0b0000_0000, 0xFF));
        bus.Add(Frame(0x300, 100, 0b0000_1000, 0xFF));
        bus.Add(Frame(0x300, 200, 0b0000_0000, 0xFF));

        CanId id = Assert.Single(bus.Ids());

        // The fourth bit moved and nothing else did, including the byte that was
        // busy but constant.
        Assert.Equal(0b0000_1000, id.Changed[0]);
        Assert.Equal(0, id.Changed[1]);
        Assert.True(id.EverChanged);
    }

    [Fact]
    public void AnIdentifierThatNeverMovesSaysSo()
    {
        var bus = new CanBus();

        for (int i = 0; i < 5; i++) bus.Add(Frame(0x400, i * 100, 0xAA, 0xBB));

        Assert.False(Assert.Single(bus.Ids()).EverChanged);
    }

    /// <summary>
    /// Timing separates what is sent on a clock from what is sent when something
    /// happens — and the second kind is where the interesting frames are.
    /// </summary>
    [Fact]
    public void SomethingSentOnATimerLooksPeriodic()
    {
        var bus = new CanBus();

        for (int i = 0; i < 10; i++) bus.Add(Frame(0x500, i * 10_000, (byte)i));

        CanId id = Assert.Single(bus.Ids());

        Assert.Equal(10_000, id.Gap!.Value, 0);
        Assert.Equal(100, id.Rate!.Value, 0);
        Assert.True(id.LooksPeriodic);
    }

    [Fact]
    public void SomethingSentWhenSomethingHappensDoesNot()
    {
        var bus = new CanBus();

        foreach (long at in new long[] { 0, 10_000, 20_000, 900_000, 905_000 })
            bus.Add(Frame(0x600, at, 1));

        Assert.False(Assert.Single(bus.Ids()).LooksPeriodic);
    }

    [Fact]
    public void ARateNeedsTwoFramesToMeasureBetween()
    {
        var bus = new CanBus();
        bus.Add(Frame(0x700, 50, 9));

        CanId id = Assert.Single(bus.Ids());

        Assert.Null(id.Gap);
        Assert.Null(id.Rate);
        Assert.False(id.LooksPeriodic);
    }

    // ----- the move the whole exercise is made of --------------------------------

    /// <summary>
    /// Mark the bus, make one thing happen, and see the short list of what that
    /// thing touched.
    ///
    /// This is how an undocumented bus is read, and the value of it is entirely in
    /// what it leaves out: a vehicle bus carrying two thousand frames a second is
    /// unreadable, and four lines saying which identifiers moved when a door
    /// opened is not.
    /// </summary>
    [Fact]
    public void AfterAMarkOnlyWhatChangedIsReported()
    {
        var bus = new CanBus();

        bus.Add(Frame(0x100, 0, 0x00, 0x11));
        bus.Add(Frame(0x200, 10, 0xAA));
        bus.Mark();

        // The bus carries on: one identifier repeats unchanged, one moves a bit,
        // and one appears that was not there before.
        bus.Add(Frame(0x200, 100, 0xAA));
        bus.Add(Frame(0x100, 110, 0x04, 0x11));
        bus.Add(Frame(0x300, 120, 0x01));

        IReadOnlyList<CanChange> changes = bus.SinceMark();

        Assert.Equal(2, changes.Count);

        // New first, because a new identifier is the louder result.
        Assert.True(changes[0].IsNew);
        Assert.Equal(0x300, changes[0].Id.Id);

        Assert.False(changes[1].IsNew);
        Assert.Equal(0x100, changes[1].Id.Id);
        Assert.Equal(0x04, changes[1].Moved[0]);
        Assert.Equal(0, changes[1].Moved[1]);
    }

    /// <summary>
    /// A frame that arrives again carrying what it carried before is the bus
    /// idling, not a result, and must not be listed as one — on a real bus that
    /// would report every periodic identifier on it and bury the answer.
    /// </summary>
    [Fact]
    public void RepeatingUnchangedIsNotAChange()
    {
        var bus = new CanBus();

        bus.Add(Frame(0x100, 0, 0x55));
        bus.Mark();

        for (int i = 1; i < 50; i++) bus.Add(Frame(0x100, i * 1000, 0x55));

        Assert.Empty(bus.SinceMark());
    }

    /// <summary>
    /// A mark with nothing before it makes everything new, which is what somebody
    /// starting a capture and pressing mark immediately should see.
    /// </summary>
    [Fact]
    public void EverythingIsNewAgainstAnEmptyMark()
    {
        var bus = new CanBus();
        bus.Mark();

        bus.Add(Frame(0x100, 0, 1));

        CanChange change = Assert.Single(bus.SinceMark());
        Assert.True(change.IsNew);
    }

    /// <summary>
    /// Frames at one identifier can change length. Rare, and real, and the
    /// comparison must not fall over it or quietly ignore the part that appeared.
    /// </summary>
    [Fact]
    public void APayloadThatGrowsCountsAsMoved()
    {
        var bus = new CanBus();

        bus.Add(Frame(0x100, 0, 0x01));
        bus.Mark();
        bus.Add(Frame(0x100, 100, 0x01, 0x80));

        CanChange change = Assert.Single(bus.SinceMark());

        Assert.False(change.IsNew);
        Assert.Equal(0, change.Moved[0]);
        Assert.Equal(0x80, change.Moved[1]);
    }

    [Fact]
    public void ClearingForgetsEverythingIncludingTheMark()
    {
        var bus = new CanBus { Dropped = 7 };

        bus.Add(Frame(0x100, 0, 1));
        bus.Mark();
        bus.Clear();

        Assert.Equal(0, bus.Seen);
        Assert.Equal(0, bus.Count);
        Assert.Null(bus.Dropped);
        Assert.Empty(bus.SinceMark());
    }

    // ----- getting the capture out of here ---------------------------------------

    [Fact]
    public void ACaptureIsWrittenAsSavvyCanReadsIt()
    {
        string csv = CanExport.SavvyCan([Frame(0x7E8, 1_234_567, 1, 2, 3)]);
        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Time Stamp,ID,Extended,Dir,Bus,LEN", lines[0], StringComparison.Ordinal);

        string[] row = lines[1].Split(',');

        Assert.Equal("1234567", row[0]);
        Assert.Equal("0x000007E8", row[1]);
        Assert.Equal("false", row[2]);
        Assert.Equal("3", row[5]);

        // Eight data columns whatever the length, or every column after a short
        // row shifts in anything that counts commas.
        Assert.Equal(14, row.Length);
        Assert.Equal("0x01", row[6]);
        Assert.Equal("0x00", row[13]);
    }

    [Fact]
    public void ACaptureIsWrittenAsCandumpReadsIt()
    {
        string log = CanExport.CanDump(
        [
            Frame(0x123, 2_500_000, 0xDE, 0xAD),
            Frame(0x18DAF110, 2_500_500, 0x01),
        ]);

        string[] lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("(2.500000) can0 123#DEAD", lines[0]);

        // Eight digits is how a reader knows an extended identifier, there being
        // no other flag in the line.
        Assert.Equal("(2.500500) can0 18DAF110#01", lines[1]);
    }

    [Fact]
    public void TheSummarySaysWhereToLook()
    {
        var bus = new CanBus();

        bus.Add(Frame(0x100, 0, 0x00, 0xFF));
        bus.Add(Frame(0x100, 10_000, 0x08, 0xFF));

        string csv = CanExport.Summary(bus.Ids());
        string[] row = csv.Split('\n')[1].Split(',');

        Assert.Equal("100", row[0]);
        Assert.Equal("2", row[2]);
        Assert.Equal("08FF", row[7]);   // the data as it stands
        Assert.Equal("0800", row[8]);   // and the one bit that moved
    }
}
