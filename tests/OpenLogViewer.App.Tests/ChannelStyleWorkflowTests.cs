using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using OpenLogViewer.Core;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Pinned trace colours and fixed scales, driven the way the sidebar drives
/// them.
/// </summary>
public class ChannelStyleWorkflowTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}");

    private string In(string name) => Path.Combine(_settings, name);

    /// <summary>
    /// Every store is given a temporary path, settings included — these tests
    /// pin colours and switch schemes, both of which persist, and a defaulted
    /// store would read and write the user's own settings.
    /// </summary>
    private MainViewModel NewViewModel() => new(
        presets: new PresetStore(In("presets.json")),
        filters: new FilterStore(In("filters.json")),
        settings: new SettingsStore(In("settings.json")),
        math: new MathChannelStore(In("math.json")),
        styles: new ChannelStyleStore(In("channels.json")));

    private MainViewModel Loaded()
    {
        MainViewModel vm = NewViewModel();
        vm.Load(_harness.WriteTypicalLog());
        return vm;
    }

    private static ChannelItem Rpm(MainViewModel vm) => vm.Channels.First(c => c.Name == "RPM");

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_settings)) Directory.Delete(_settings, recursive: true);
    }

    // ----- scale ------------------------------------------------------------

    [Fact]
    public void AChannelIsScaledToItsOwnRangeUntilOneIsPinned()
    {
        ChannelItem rpm = Rpm(Loaded());

        Assert.False(rpm.HasFixedRange);
        Assert.Equal("", rpm.ScaleNote);
    }

    [Fact]
    public void APinnedScaleIsWhatTheTraceIsDrawnOver()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);

        Assert.True(vm.PinRange(rpm, 0, 8000));

        (double min, double range) = rpm.Scale(holdSteady: true);

        Assert.Equal(0, min);
        Assert.Equal(8000, range);
        Assert.True(rpm.HasFixedRange);
    }

    [Fact]
    public void APinnedScaleIgnoresTheSteadyChannelFloor()
    {
        // The floor exists to stop a near-constant channel filling its lane with
        // its own last decimal place. Somebody who named a range has answered
        // that question already, so it must not be widened underneath them.
        MainViewModel vm = Loaded();
        ChannelItem target = vm.Channels.First(c => c.Name == "AFR Target");

        vm.PinRange(target, 10, 20);

        Assert.Equal((10.0, 10.0), target.Scale(holdSteady: true));
        Assert.Equal((10.0, 10.0), target.Scale(holdSteady: false));
    }

    [Theory]
    [InlineData(8000, 0)]    // the wrong way round
    [InlineData(100, 100)]   // no width
    [InlineData(0, double.NaN)]
    public void ABoundPairThatIsNotARangeIsRefusedAndSaysWhy(double min, double max)
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);

        Assert.False(vm.PinRange(rpm, min, max));
        Assert.False(rpm.HasFixedRange);
        Assert.Contains("low value below a high one", vm.Hint);
    }

    [Fact]
    public void APinnedScaleSurvivesOpeningAnotherLog()
    {
        MainViewModel vm = Loaded();
        vm.PinRange(Rpm(vm), 0, 8000);

        MainViewModel reopened = NewViewModel();
        reopened.Load(_harness.WriteTypicalLog());

        Assert.Equal((0.0, 8000.0), Rpm(reopened).Scale(holdSteady: true));
    }

    [Fact]
    public void UnpinningGivesTheChannelItsOwnRangeBack()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);
        (double Min, double Range) before = rpm.Scale(holdSteady: true);

        vm.PinRange(rpm, 0, 8000);
        vm.PinRange(rpm, null, null);

        Assert.False(rpm.HasFixedRange);
        Assert.Equal(before, rpm.Scale(holdSteady: true));
        Assert.Null(new ChannelStyleStore(In("channels.json")).For("RPM"));
    }

    // ----- colour -----------------------------------------------------------

    [Fact]
    public void APinnedColourSurvivesASchemeChange()
    {
        // The whole point of pinning one: trace colours are otherwise re-picked
        // for every scheme, because a palette is only separable against the
        // background it was chosen for.
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);
        var chosen = Color.FromRgb(0x33, 0x66, 0xFF);

        vm.SelectedTheme = ThemeCatalog.Find("midnight");
        vm.PinColor(rpm, chosen);
        vm.SelectedTheme = ThemeCatalog.Find("daylight");

        Assert.Equal(chosen, rpm.Color);
        Assert.True(rpm.HasFixedColor);
    }

    [Fact]
    public void AnUnpinnedChannelStillFollowsTheScheme()
    {
        MainViewModel vm = Loaded();
        ChannelItem clt = vm.Channels.First(c => c.Name == "CLT");
        vm.SelectedTheme = ThemeCatalog.Find("midnight");
        vm.PinColor(Rpm(vm), Color.FromRgb(0x33, 0x66, 0xFF));

        Color before = clt.Color;
        vm.SelectedTheme = ThemeCatalog.Find("daylight");

        Assert.NotEqual(before, clt.Color);
        Assert.False(clt.HasFixedColor);
    }

    [Fact]
    public void PlottingAChannelDoesNotOverwriteItsPinnedColour()
    {
        // Colours are normally handed out as channels are plotted; a pinned one
        // has to survive that too, not just a scheme change.
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);
        var chosen = Color.FromRgb(0x33, 0x66, 0xFF);

        vm.PinColor(rpm, chosen);
        rpm.IsVisible = false;
        rpm.IsVisible = true;

        Assert.Equal(chosen, rpm.Color);
    }

    [Fact]
    public void APinnedColourSurvivesOpeningAnotherLog()
    {
        MainViewModel vm = Loaded();
        vm.PinColor(Rpm(vm), Color.FromRgb(0x33, 0x66, 0xFF));

        MainViewModel reopened = NewViewModel();
        reopened.Load(_harness.WriteTypicalLog());

        Assert.Equal(Color.FromRgb(0x33, 0x66, 0xFF), Rpm(reopened).Color);
    }

    [Fact]
    public void UnpinningAColourHandsItBackToThePalette()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);

        vm.PinColor(rpm, Color.FromRgb(0x33, 0x66, 0xFF));
        vm.PinColor(rpm, null);

        Assert.False(rpm.HasFixedColor);
        Assert.Contains(rpm.Color, vm.PaletteColors);
    }

    [Fact]
    public void APinnedPaletteEntryIsNotHandedOutToAnotherChannel()
    {
        // Pinning a channel to a palette colour must not leave a second trace
        // drawn in the same one — two traces the same colour is exactly the
        // failure the palettes are chosen to avoid.
        // The colour is taken from the scheme the view model ends on, which is
        // the only way the two can collide — a colour from outside that palette
        // takes nothing away from it.
        MainViewModel vm = Loaded();
        vm.SelectedTheme = ThemeCatalog.Find("nord");

        Color entry = vm.PaletteColors[1];
        vm.PinColor(Rpm(vm), entry);

        // Away and back, so the whole palette is handed out afresh with the pin
        // already in place.
        vm.SelectedTheme = ThemeCatalog.Find("midnight");
        vm.SelectedTheme = ThemeCatalog.Find("nord");

        Assert.Equal(entry, Rpm(vm).Color);
        Assert.DoesNotContain(
            vm.Channels.Where(c => !c.HasFixedColor),
            c => c.Color == entry);
    }

    [Fact]
    public void EveryPlottedTraceKeepsItsOwnColourAcrossASchemeChange()
    {
        MainViewModel vm = Loaded();
        vm.SelectedTheme = ThemeCatalog.Find("nord");
        vm.PinColor(Rpm(vm), vm.PaletteColors[2]);

        vm.SelectedTheme = ThemeCatalog.Find("midnight");
        vm.SelectedTheme = ThemeCatalog.Find("nord");

        var plotted = vm.Channels.Where(c => c.IsVisible).Select(c => c.Color).ToList();

        Assert.Equal(plotted.Count, plotted.Distinct().Count());
    }

    [Fact]
    public void ColourAndScaleAreIndependent()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);

        vm.PinColor(rpm, Color.FromRgb(0x33, 0x66, 0xFF));
        vm.PinRange(rpm, 0, 8000);
        vm.PinColor(rpm, null);

        Assert.False(rpm.HasFixedColor);
        Assert.True(rpm.HasFixedRange);
    }

    [Fact]
    public void BackToAutomaticClearsBoth()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);

        vm.PinColor(rpm, Color.FromRgb(0x33, 0x66, 0xFF));
        vm.PinRange(rpm, 0, 8000);
        vm.ClearStyle(rpm);

        Assert.False(rpm.HasFixedColor);
        Assert.False(rpm.HasFixedRange);
        Assert.Null(new ChannelStyleStore(In("channels.json")).For("RPM"));
    }

    // ----- the editor -------------------------------------------------------

    [Fact]
    public void TheEditorOpensSeededWithWhatTheChannelIsDrawnOverNow()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);
        (double min, double range) = rpm.Scale(holdSteady: true);

        vm.BeginStyleEdit(rpm);

        Assert.True(vm.EditingStyle);
        Assert.Equal("RPM", vm.StyleTargetName);
        Assert.Equal(Math.Round(min, 3), double.Parse(vm.StyleMin, CultureInfo.InvariantCulture), 3);
        Assert.Equal(Math.Round(min + range, 3), double.Parse(vm.StyleMax, CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void ApplyingTheEditorPinsTheScaleAndShuts()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);

        vm.BeginStyleEdit(rpm);
        vm.StyleMin = "0";
        vm.StyleMax = "8000";

        Assert.True(vm.CommitStyleEdit());
        Assert.False(vm.EditingStyle);
        Assert.Equal((0.0, 8000.0), rpm.Scale(holdSteady: true));
    }

    [Fact]
    public void ClearingBothBoxesGoesBackToAutomatic()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);
        vm.PinRange(rpm, 0, 8000);

        vm.BeginStyleEdit(rpm);
        vm.StyleMin = "";
        vm.StyleMax = "";

        Assert.True(vm.CommitStyleEdit());
        Assert.False(rpm.HasFixedRange);
    }

    [Fact]
    public void TextThatIsNotANumberKeepsTheEditorOpenAndSaysSo()
    {
        MainViewModel vm = Loaded();

        vm.BeginStyleEdit(Rpm(vm));
        vm.StyleMin = "idle";
        vm.StyleMax = "8000";

        Assert.False(vm.CommitStyleEdit());
        Assert.True(vm.EditingStyle);
        Assert.Contains("two numbers", vm.Hint);
    }

    [Fact]
    public void CancellingChangesNothing()
    {
        MainViewModel vm = Loaded();
        ChannelItem rpm = Rpm(vm);

        vm.BeginStyleEdit(rpm);
        vm.StyleMin = "0";
        vm.StyleMax = "8000";
        vm.CancelStyleEdit();

        Assert.False(vm.EditingStyle);
        Assert.False(rpm.HasFixedRange);
    }

    [Fact]
    public void TheSeededBoundsAreReadableRatherThanTheFloatsRoundingError()
    {
        // A float widened to double prints seventeen digits; nobody wants to
        // edit 5502.99993896484375.
        MainViewModel vm = Loaded();
        vm.BeginStyleEdit(Rpm(vm));

        Assert.DoesNotContain("E", vm.StyleMax, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.StyleMax.Length <= 12, $"seeded max was '{vm.StyleMax}'");
    }
}
