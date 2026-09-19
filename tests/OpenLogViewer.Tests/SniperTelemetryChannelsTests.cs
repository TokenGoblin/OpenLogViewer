using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// The 119-channel catalog transcribed from <c>telemetry_channels.csv</c>.
/// These tests only pin down the transcription itself — the catalog has no
/// per-channel byte offset (see <see cref="SniperTelemetryChannels"/>'s own
/// header comment), so there is nothing here about decoding a real reply.
/// </summary>
public class SniperTelemetryChannelsTests
{
    [Fact]
    public void ThereAreExactlyTheOneHundredNineteenChannelsTheSpecDescribes() =>
        Assert.Equal(119, SniperTelemetryChannels.All.Count);

    [Fact]
    public void EveryEntrysIndexMatchesItsPositionInTheList()
    {
        for (int i = 0; i < SniperTelemetryChannels.All.Count; i++)
            Assert.Equal(i, SniperTelemetryChannels.All[i].Index);
    }

    /// <summary>§5's own "sample indices" list, checked against the full transcription.</summary>
    [Theory]
    [InlineData(2, "RPM")]
    [InlineData(3, "Inj PW")]
    [InlineData(4, "Duty Cycle")]
    [InlineData(6, "Target AFR")]
    [InlineData(7, "AFR")]
    [InlineData(15, "Fuel Flow")]
    [InlineData(18, "Estimated VE")]
    [InlineData(19, "Ignition Timing")]
    [InlineData(24, "MAP")]
    [InlineData(25, "TPS")]
    [InlineData(26, "MAT")]
    [InlineData(27, "CTS")]
    [InlineData(28, "Battery")]
    [InlineData(107, "Speed")]
    [InlineData(109, "Input Shaft Speed")]
    public void NamedHighlightChannelsMatchTheSpecsSampleIndices(int index, string expectedName) =>
        Assert.Equal(expectedName, SniperTelemetryChannels.ByIndex(index)?.Name);

    [Fact]
    public void ByIndexReturnsNullOutsideTheCatalog()
    {
        Assert.Null(SniperTelemetryChannels.ByIndex(-1));
        Assert.Null(SniperTelemetryChannels.ByIndex(119));
    }

    /// <summary>§5: "flags(bit0=hidden default)" — spot-checked against a couple of concrete rows.</summary>
    [Fact]
    public void HiddenByDefaultReadsBitZeroOfTheFlagWord()
    {
        // RPM: default flag=256 (0x100), bit 0 clear -> shown by default.
        Assert.False(SniperTelemetryChannels.ByIndex(2)!.HiddenByDefault);

        // Main Rev Limit: default flag=257 (0x101), bit 0 set -> hidden by default.
        Assert.True(SniperTelemetryChannels.ByIndex(20)!.HiddenByDefault);
    }
}
