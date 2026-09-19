using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// Layer 2 of the Holley Sniper protocol — CAN id encoding and RX
/// classification, per <c>SNIPER_ECU_CLIENT_SPEC.md</c> §2. Pure arithmetic,
/// so unlike almost everything else in the Sniper support, this is fully
/// checkable without hardware: the formulas themselves are marked ✅
/// (verified in the decompiler/bytes) even though the node id values that
/// feed them are not.
/// </summary>
public class SniperCanTests
{
    /// <summary>
    /// Reproduces §2's three concrete simple-command examples exactly, working
    /// backwards from the observed ids to confirm the formula this codebase
    /// uses actually produces them for node=0.
    /// </summary>
    [Theory]
    [InlineData(SniperCan.SimpleCommand.PingOrEnumerate, 0x70014004u)]
    [InlineData(SniperCan.SimpleCommand.NodeAck, 0x700D4004u)]
    [InlineData(SniperCan.SimpleCommand.Unnamed0F, 0x700F4004u)]
    public void SimpleCommandIdsMatchTheSpecsWorkedExamples(byte command, uint expected) =>
        Assert.Equal(expected, SniperCan.SimpleId(node: 0, command));

    [Fact]
    public void SimpleIdFoldsInTheNodeAtBitThree()
    {
        uint baseline = SniperCan.SimpleId(0, SniperCan.SimpleCommand.PingOrEnumerate);
        uint withNode = SniperCan.SimpleId(5, SniperCan.SimpleCommand.PingOrEnumerate);

        Assert.Equal(baseline | (5u << 3), withNode);
    }

    [Fact]
    public void BulkIdSetsBitThirtyOneAndFoldsInBothNodes()
    {
        uint id = SniperCan.BulkId(dst: 0x12, src: 0x34);

        Assert.NotEqual(0u, id & 0x80000000);

        uint expected = (uint)((((0x12 & 0x7FF) << 14) | (0x34 & 0x7FF)) << 3) | 0xA0014004;
        Assert.Equal(expected, id);
    }

    [Fact]
    public void BulkIdMasksNodesToElevenBits()
    {
        // A node id past 0x7FF must not bleed into the bits either node folds
        // into, or two different out-of-range nodes would collide.
        uint low = SniperCan.BulkId(dst: 0x7FF, src: 0);
        uint high = SniperCan.BulkId(dst: 0x7FF + 0x800, src: 0);

        Assert.Equal(low, high);
    }

    [Fact]
    public void ABulkIdClassifiesAsBulk() =>
        Assert.Equal(SniperFrameKind.Bulk, SniperCan.Classify(SniperCan.BulkId(1, 2)));

    /// <summary>
    /// A plain simple-command id (node 0) does not classify as DATA — its base
    /// pattern has bit 2 set — and does not classify as BULK either, so it
    /// falls to NODE-DISCOVERY by the spec's own three-way split. Documented
    /// as a real, non-obvious consequence of the classification order in
    /// <see cref="SniperCan.Classify"/>'s own doc comment; pinned here so a
    /// future edit cannot silently change it.
    /// </summary>
    [Fact]
    public void APlainSimpleCommandIdFallsThroughToNodeDiscovery() =>
        Assert.Equal(
            SniperFrameKind.NodeDiscovery,
            SniperCan.Classify(SniperCan.SimpleId(0, SniperCan.SimpleCommand.PingOrEnumerate)));

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x00000001u)]
    [InlineData(0x70000000u)] // top nibble 7, but bit 2 clear -> still Data; the first check wins.
    public void IdsWithBitTwoClearAreData(uint id) =>
        Assert.Equal(SniperFrameKind.Data, SniperCan.Classify(id));

    [Fact]
    public void AnIdMatchingNoneOfTheThreeRulesIsUnknown()
    {
        // Bit 2 set (not Data), bit 31 clear (not Bulk), top nibble not 7 (not NodeDiscovery).
        uint id = 0x00000004;
        Assert.Equal(SniperFrameKind.Unknown, SniperCan.Classify(id));
    }
}
