using System.IO;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class ChannelStyleTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"olv-style-{Guid.NewGuid():N}");

    private string Path_ => Path.Combine(_dir, "channels.json");

    private ChannelStyleStore Store() => new(Path_);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ----- the record -------------------------------------------------------

    [Theory]
    [InlineData(0, 100, true)]
    [InlineData(-40, 40, true)]
    [InlineData(100, 0, false)]      // the wrong way round
    [InlineData(50, 50, false)]      // no width to divide by
    public void OnlyAUsablePairCountsAsARange(double min, double max, bool usable) =>
        Assert.Equal(usable, new ChannelStyle("RPM", Min: min, Max: max).HasRange);

    [Fact]
    public void HalfAScaleIsNotAScale()
    {
        Assert.False(new ChannelStyle("RPM", Min: 0).HasRange);
        Assert.False(new ChannelStyle("RPM", Max: 8000).HasRange);
    }

    [Fact]
    public void ANonFiniteBoundIsRejected()
    {
        Assert.False(new ChannelStyle("RPM", Min: double.NaN, Max: 8000).HasRange);
        Assert.False(new ChannelStyle("RPM", Min: 0, Max: double.PositiveInfinity).HasRange);
    }

    [Fact]
    public void AnEntryPinningNothingIsEmpty()
    {
        Assert.True(new ChannelStyle("RPM").IsEmpty);
        Assert.False(new ChannelStyle("RPM", Color: 0x3366FF).IsEmpty);
        Assert.False(new ChannelStyle("RPM", Min: 0, Max: 8000).IsEmpty);
    }

    // ----- the store --------------------------------------------------------

    [Fact]
    public void APinnedColourSurvivesANewStore()
    {
        Store().SetColor("RPM", 0x3366FF);

        Assert.Equal(0x3366FF, Store().For("RPM")?.Color);
    }

    [Fact]
    public void APinnedScaleSurvivesANewStore()
    {
        Store().SetRange("RPM", 0, 8000);

        ChannelStyle? style = Store().For("RPM");

        Assert.Equal(0, style?.Min);
        Assert.Equal(8000, style?.Max);
        Assert.True(style!.HasRange);
    }

    [Fact]
    public void ColourAndScaleArePinnedIndependently()
    {
        ChannelStyleStore store = Store();
        store.SetColor("RPM", 0x3366FF);
        store.SetRange("RPM", 0, 8000);

        // Clearing one must not take the other with it.
        store.SetColor("RPM", null);

        ChannelStyle? style = Store().For("RPM");

        Assert.False(style!.HasColor);
        Assert.True(style.HasRange);
    }

    [Fact]
    public void ClearingBothHalvesRemovesTheEntryEntirely()
    {
        ChannelStyleStore store = Store();
        store.SetColor("RPM", 0x3366FF);
        store.SetRange("RPM", 0, 8000);

        store.SetColor("RPM", null);
        store.SetRange("RPM", null, null);

        Assert.Null(Store().For("RPM"));
        Assert.Empty(Store().Styles);
    }

    [Fact]
    public void ChannelsAreMatchedWithoutRegardToCase()
    {
        Store().SetRange("Coolant Temp", 60, 120);

        Assert.NotNull(Store().For("COOLANT TEMP"));
        Assert.NotNull(Store().For("coolant temp"));
    }

    [Fact]
    public void ClearPutsAChannelBackToAutomatic()
    {
        ChannelStyleStore store = Store();
        store.SetColor("RPM", 0x3366FF);
        store.SetRange("RPM", 0, 8000);

        store.Clear("RPM");

        Assert.Null(Store().For("RPM"));
    }

    [Fact]
    public void ANameThatWasNeverPinnedHasNoStyle() =>
        Assert.Null(Store().For("Nothing here"));

    [Fact]
    public void AMissingFileIsAnEmptyStoreRatherThanAFailure()
    {
        ChannelStyleStore store = Store();

        Assert.Empty(store.Styles);
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void AGarbledFileIsReadAsNoStylesRatherThanThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");

        Assert.Empty(Store().Styles);
    }

    [Fact]
    public void AnEntryWithNoChannelNameIsSkippedOnRead()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            { "Version": 1, "Channels": [
              { "Channel": "", "Color": 255 },
              { "Channel": "RPM", "Color": 255 }
            ] }
            """);

        Assert.Single(Store().Styles);
        Assert.NotNull(Store().For("RPM"));
    }

    [Fact]
    public void AnEntryPinningNothingIsSkippedOnRead()
    {
        // Otherwise a hand-edited file could carry rows that mean nothing and
        // would be written straight back out again.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            { "Version": 1, "Channels": [ { "Channel": "RPM" } ] }
            """);

        Assert.Empty(Store().Styles);
    }

    [Fact]
    public void APinnedRangeTheWrongWayRoundIsDroppedOnRead()
    {
        // Written by hand rather than by the app, which refuses the pair. An
        // entry that can never be honoured pins nothing, so it is dropped like
        // any other empty one rather than kept as dead weight in the file.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            { "Version": 1, "Channels": [ { "Channel": "RPM", "Min": 8000, "Max": 0 } ] }
            """);

        Assert.Null(Store().For("RPM"));
    }

    [Fact]
    public void AColourOutsideTwentyFourBitsIsNotHonoured()
    {
        // Colours are packed 0xRRGGBB; anything else came from a hand-edited
        // file and would be unpacked into a colour nobody chose.
        Assert.False(new ChannelStyle("RPM", Color: -1).HasColor);
        Assert.False(new ChannelStyle("RPM", Color: 0x1000000).HasColor);
        Assert.True(new ChannelStyle("RPM", Color: 0xFFFFFF).HasColor);
        Assert.True(new ChannelStyle("RPM", Color: 0).HasColor);
    }
}
