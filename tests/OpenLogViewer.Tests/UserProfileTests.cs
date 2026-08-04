using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.Tests;

/// <summary>
/// What belongs to the person using this, and what ships with it.
///
/// The line matters in both directions. Somebody's saved channel selections,
/// their filters and the ECUs they own are theirs, and must not travel with the
/// application to anybody else — a build that arrived with a stranger's presets
/// in it would be both baffling and a small breach of their privacy. And the
/// application's own defaults must not depend on any of that, or a new install
/// behaves differently from a developer's machine and nobody can reproduce it.
/// </summary>
public class UserProfileTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (string path in _temp)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }
    }

    private string TempFile(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{name}-{Guid.NewGuid():N}.json");
        _temp.Add(path);

        return path;
    }

    // ----- a new install is empty ---------------------------------------------

    /// <summary>
    /// Nothing is seeded. A first run has no presets, no filters and no
    /// calculated channels — whatever is on the machine that built the release
    /// stays on it.
    /// </summary>
    [Fact]
    public void AFreshProfileHasNothingInIt()
    {
        Assert.Empty(new PresetStore(TempFile("presets")).Presets);
        Assert.Empty(new FilterStore(TempFile("filters")).Filters);
        Assert.Empty(new MathChannelStore(TempFile("math")).Channels);

        var settings = new SettingsStore(TempFile("settings"));

        Assert.Empty(settings.KnownEcus);
        Assert.Empty(settings.EcuLastUsed);
        Assert.Null(settings.RecordingFolder);
        Assert.Null(settings.DataFolder);
        Assert.Null(settings.ThemeId);
    }

    /// <summary>
    /// The suggested filters come from the log in front of you, not from a list
    /// somebody else wrote. A log with different channels gets different
    /// suggestions, and they arrive switched off either way.
    /// </summary>
    [Fact]
    public void SuggestedFiltersComeFromTheLogRatherThanFromAProfile()
    {
        var document = new LogDocument
        {
            FilePath = "",
            FormatName = "test",
            Channels =
            [
                LogChannel.Adopt("CLT", "F", 0, [70, 120, 180]),
                LogChannel.Adopt("RPM", "rpm", 0, [0, 2000, 4000]),
            ],
            Time = new LogChannel("Time", "s", 3, [0, 1, 2]),
        };

        IReadOnlyList<LogFilter> suggested = [.. SampleFilter.Suggest(document)];

        Assert.NotEmpty(suggested);
        Assert.All(suggested, f => Assert.False(f.Enabled, $"{f.Name} should arrive switched off"));
        Assert.All(suggested, f =>
            Assert.Contains(f.Channel, document.Channels.Select(c => c.Name)));
    }

    // ----- devices are remembered, and can be forgotten -----------------------

    [Fact]
    public void ADeviceAndWhenItWasUsedSurviveARestart()
    {
        string path = TempFile("settings");
        DateTimeOffset when = DateTimeOffset.Now.AddMinutes(-5);

        var settings = new SettingsStore(path);
        settings.SetKnownEcus(new Dictionary<string, string> { ["USB\\VID_2341"] = "Speeduino 2025.01.7" });
        settings.SetEcuLastUsed(new Dictionary<string, DateTimeOffset> { ["USB\\VID_2341"] = when });

        var reopened = new SettingsStore(path);

        Assert.Equal("Speeduino 2025.01.7", reopened.KnownEcus["USB\\VID_2341"]);

        // To the second: the file stores a round-trip string, and the point of
        // the value is ordering rather than precision.
        Assert.Equal(when.ToUnixTimeSeconds(), reopened.EcuLastUsed["USB\\VID_2341"].ToUnixTimeSeconds());
    }

    /// <summary>
    /// A settings file written before use times were recorded still loads, and
    /// its devices are still remembered — they simply have no time, which sorts
    /// them below the ones that do.
    /// </summary>
    [Fact]
    public void AnOlderProfileWithoutUseTimesStillLoads()
    {
        string path = TempFile("settings");

        File.WriteAllText(path, """
            {"version":1,"knownEcus":{"USB\\VID_067B":"MS2/Extra 3.4.1"}}
            """);

        var settings = new SettingsStore(path);

        Assert.Equal("MS2/Extra 3.4.1", settings.KnownEcus["USB\\VID_067B"]);
        Assert.Empty(settings.EcuLastUsed);
    }

    /// <summary>
    /// Forgetting takes the devices and nothing else. The reason to reach for it
    /// is a dongle that has been sold, not a wish to lose an afternoon's filters.
    /// </summary>
    [Fact]
    public void ForgettingDevicesLeavesEverythingElseAlone()
    {
        string path = TempFile("settings");

        var settings = new SettingsStore(path);
        settings.SetKnownEcus(new Dictionary<string, string> { ["USB\\VID_2341"] = "Speeduino" });
        settings.SetEcuLastUsed(new Dictionary<string, DateTimeOffset> { ["USB\\VID_2341"] = DateTimeOffset.Now });
        settings.SetTheme("gruvbox");
        settings.SetUnits(UnitSystem.Imperial);
        settings.SetLiveRate(5);

        settings.ForgetKnownEcus();

        Assert.Empty(settings.KnownEcus);
        Assert.Empty(settings.EcuLastUsed);

        // The preferences are not devices and are not swept up.
        Assert.Equal("gruvbox", settings.ThemeId);
        Assert.Equal(UnitSystem.Imperial, settings.Units);
        Assert.Equal(5, settings.LiveRate);

        // And it stuck.
        Assert.Empty(new SettingsStore(path).KnownEcus);
    }

    [Fact]
    public void ForgettingWhenNothingIsRememberedIsHarmless()
    {
        var settings = new SettingsStore(TempFile("settings"));

        settings.ForgetKnownEcus();
        settings.ForgetKnownEcus();

        Assert.Empty(settings.KnownEcus);
    }

    /// <summary>
    /// Both halves go together. A signature with no time, or a time with no
    /// signature, is half a memory — and the half that survived would put a
    /// device in a list that nothing else knows anything about.
    /// </summary>
    [Fact]
    public void ForgettingClearsBothHalvesOfTheMemory()
    {
        string path = TempFile("settings");

        var settings = new SettingsStore(path);
        settings.SetKnownEcus(new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" });
        settings.SetEcuLastUsed(new Dictionary<string, DateTimeOffset>
        {
            ["a"] = DateTimeOffset.Now,
            ["b"] = DateTimeOffset.Now.AddDays(-1),
        });

        settings.ForgetKnownEcus();

        var reopened = new SettingsStore(path);

        Assert.Empty(reopened.KnownEcus);
        Assert.Empty(reopened.EcuLastUsed);
    }
}
