using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

public class PresetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"olv-presets-{Guid.NewGuid():N}");

    private string File_ => Path.Combine(_dir, "presets.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SavedPresetsSurviveAReload()
    {
        var store = new PresetStore(File_);
        store.Save("Boost tuning", ["RPM", "MAP", "Boost psi"]);

        var reopened = new PresetStore(File_);

        Assert.Single(reopened.Presets);
        Assert.Equal("Boost tuning", reopened.Presets[0].Name);
        Assert.Equal(["RPM", "MAP", "Boost psi"], reopened.Presets[0].Channels);
    }

    [Fact]
    public void SavingTheSameNameReplacesRatherThanDuplicates()
    {
        var store = new PresetStore(File_);
        store.Save("Idle", ["RPM"]);
        store.Save("idle", ["RPM", "CLT"]);

        Assert.Single(store.Presets);
        Assert.Equal(2, store.Presets[0].Channels.Count);
        // The name keeps the casing it was most recently saved with.
        Assert.Equal("idle", store.Presets[0].Name);
    }

    [Fact]
    public void NamesAreTrimmedAndInnerWhitespaceCollapsed()
    {
        var store = new PresetStore(File_);
        store.Save("  Cold   start  ", ["CLT"]);

        Assert.Equal("Cold start", store.Presets[0].Name);
    }

    [Fact]
    public void DuplicateChannelsAreDroppedWithinAPreset()
    {
        var store = new PresetStore(File_);
        store.Save("Dupes", ["RPM", "rpm", "MAP"]);

        Assert.Equal(2, store.Presets[0].Channels.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void APresetNeedsAName(string name)
    {
        var store = new PresetStore(File_);

        Assert.Throws<ArgumentException>(() => store.Save(name, ["RPM"]));
    }

    [Fact]
    public void APresetNeedsAtLeastOneChannel()
    {
        var store = new PresetStore(File_);

        Assert.Throws<ArgumentException>(() => store.Save("Empty", []));
    }

    [Fact]
    public void DeleteRemovesAPresetAndPersists()
    {
        var store = new PresetStore(File_);
        store.Save("Gone", ["RPM"]);
        store.Save("Kept", ["MAP"]);

        Assert.True(store.Delete("gone"));
        Assert.False(store.Delete("never existed"));

        Assert.Equal(["Kept"], new PresetStore(File_).Presets.Select(p => p.Name));
    }

    [Fact]
    public void ACorruptFileIsIgnoredRatherThanThrowing()
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, "{ this is not json");

        var store = new PresetStore(File_);

        Assert.Empty(store.Presets);
        // And the store stays usable.
        store.Save("Fresh", ["RPM"]);
        Assert.Single(new PresetStore(File_).Presets);
    }

    [Fact]
    public void AMissingFileIsNotAnError()
    {
        var store = new PresetStore(Path.Combine(_dir, "nested", "deep", "presets.json"));

        Assert.Empty(store.Presets);
        store.Save("Works", ["RPM"]);
        Assert.Single(store.Presets);
    }

    [Theory]
    // The file is hand-editable, so property casing must not matter.
    [InlineData("{\"version\":1,\"presets\":[{\"name\":\"Boost\",\"channels\":[\"RPM\",\"MAP\"]}]}")]
    [InlineData("{\"Version\":1,\"Presets\":[{\"Name\":\"Boost\",\"Channels\":[\"RPM\",\"MAP\"]}]}")]
    [InlineData("{\"presets\":[{\"name\":\"Boost\",\"channels\":[\"RPM\",\"MAP\"],}],}")]
    public void HandEditedFilesLoadRegardlessOfCasingOrTrailingCommas(string json)
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File_, json);

        var store = new PresetStore(File_);

        Assert.Single(store.Presets);
        Assert.Equal("Boost", store.Presets[0].Name);
        Assert.Equal(["RPM", "MAP"], store.Presets[0].Channels);
    }

    [Fact]
    public void WhatIsWrittenCanBeReadBack()
    {
        var store = new PresetStore(File_);
        store.Save("Round trip", ["RPM", "MAP"]);

        string json = System.IO.File.ReadAllText(File_);
        Assert.Contains("\"presets\"", json);

        Assert.Single(new PresetStore(File_).Presets);
    }

    [Fact]
    public void FindIsCaseInsensitive()
    {
        var store = new PresetStore(File_);
        store.Save("Wide Open Throttle", ["TPS"]);

        Assert.NotNull(store.Find("wide open throttle"));
        Assert.Null(store.Find("something else"));
    }

    [Fact]
    public void LongNamesAreCapped()
    {
        var store = new PresetStore(File_);
        store.Save(new string('x', 200), ["RPM"]);

        Assert.True(store.Presets[0].Name.Length <= 40);
    }
}
