using OpenLogViewer.App;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

public class MathChannelWorkflowTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private MainViewModel Loaded()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        vm.Load(_harness.WriteTypicalLog());
        return vm;
    }

    private static void Define(MainViewModel vm, string name, string expression, string units = "")
    {
        vm.NewMathName = name;
        vm.NewMathUnits = units;
        vm.NewMathExpression = expression;
        vm.AddMathChannel();
    }

    [Fact]
    public void AnAddedChannelJoinsTheListAndIsPlottable()
    {
        MainViewModel vm = Loaded();

        Define(vm, "AFR Error", "AFR - AFR Target", "AFR");

        ChannelItem item = Assert.Single(vm.Channels, c => c.Name == "AFR Error");
        Assert.True(item.IsCalculated);
        Assert.Equal("AFR", item.Units);
        Assert.False(item.IsFlat);

        item.IsVisible = true;
        Assert.Contains(vm.Channels, c => c.Name == "AFR Error" && c.IsVisible);
    }

    [Fact]
    public void ACalculatedChannelIsAvailableAsAHistogramAxis()
    {
        // The whole point of deriving one is to bin against it.
        MainViewModel vm = Loaded();

        Define(vm, "AFR Error", "AFR - AFR Target");

        Assert.Contains(vm.AxisChannels, c => c.Name == "AFR Error");
    }

    [Fact]
    public void ADefinitionSurvivesLoadingAnotherLog()
    {
        MainViewModel vm = Loaded();
        Define(vm, "Double RPM", "RPM * 2");

        vm.Load(_harness.WriteTypicalLog());

        Assert.Contains(vm.Channels, c => c.Name == "Double RPM" && c.IsCalculated);
    }

    [Fact]
    public void ADefinitionThatDoesNotFitTheLogIsReportedRatherThanLost()
    {
        MainViewModel vm = Loaded();
        Define(vm, "Power", "RPM * 2");

        // A log without RPM: the definition stays, but cannot be applied here.
        vm.Load(_harness.WriteCsv(("MAP", [10, 20, 30])));

        Assert.DoesNotContain(vm.Channels, c => c.Name == "Power");
        Assert.True(vm.HasMathProblems);
        Assert.Contains("Power", vm.MathProblems);
        Assert.Contains(vm.MathChannels, c => c.Name == "Power");
    }

    [Fact]
    public void RemovingADefinitionTakesItsChannelWithIt()
    {
        MainViewModel vm = Loaded();
        Define(vm, "Double RPM", "RPM * 2");

        vm.RemoveMathChannel(vm.MathChannels.Single());

        Assert.DoesNotContain(vm.Channels, c => c.Name == "Double RPM");
        Assert.Empty(vm.MathChannels);
    }

    [Fact]
    public void ARemovedChannelStopsBeingPlotted()
    {
        // Leaving it on the plot with nothing behind it would be a trace of a
        // channel that no longer exists.
        MainViewModel vm = Loaded();
        Define(vm, "Double RPM", "RPM * 2");
        vm.Channels.Single(c => c.Name == "Double RPM").IsVisible = true;

        vm.RemoveMathChannel(vm.MathChannels.Single());

        Assert.DoesNotContain(vm.Channels, c => c.Name == "Double RPM");
    }

    [Fact]
    public void EditingReopensTheDefinitionAndKeepsItPlottedWhenSavedAgain()
    {
        MainViewModel vm = Loaded();
        Define(vm, "Double RPM", "RPM * 2", "RPM");
        vm.Channels.Single(c => c.Name == "Double RPM").IsVisible = true;

        vm.EditMathChannel(vm.MathChannels.Single());

        Assert.Equal("Double RPM", vm.NewMathName);
        Assert.Equal("RPM * 2", vm.NewMathExpression);
        Assert.Equal("RPM", vm.NewMathUnits);
        Assert.Empty(vm.MathChannels);

        vm.NewMathExpression = "RPM * 3";
        vm.AddMathChannel();

        ChannelItem item = Assert.Single(vm.Channels, c => c.Name == "Double RPM");
        Assert.True(item.IsVisible);
        Assert.Equal(2400, item.Channel.At(0), 3);   // RPM starts at 800
    }

    [Fact]
    public void CancellingAnEditPutsTheOriginalBack()
    {
        // Opening the editor removes the definition, so cancelling has to restore
        // it — and restore the original, not what was typed over it.
        MainViewModel vm = Loaded();
        Define(vm, "Double RPM", "RPM * 2", "RPM");
        vm.Channels.Single(c => c.Name == "Double RPM").IsVisible = true;

        vm.EditMathChannel(vm.MathChannels.Single());
        vm.NewMathExpression = "RPM * 99";
        vm.CancelMathEdit();

        MathChannel restored = Assert.Single(vm.MathChannels);
        Assert.Equal("RPM * 2", restored.Expression);
        Assert.Equal("RPM", restored.Units);

        ChannelItem item = Assert.Single(vm.Channels, c => c.Name == "Double RPM");
        Assert.True(item.IsVisible);
        Assert.Equal(1600, item.Channel.At(0), 3);
    }

    [Fact]
    public void CancellingAFreshEntryJustClearsTheForm()
    {
        MainViewModel vm = Loaded();

        vm.NewMathName = "Half";
        vm.NewMathExpression = "RPM / 2";
        vm.CancelMathEdit();

        Assert.Empty(vm.MathChannels);
        Assert.Equal("", vm.NewMathName);
        Assert.Equal("", vm.NewMathExpression);
    }

    [Fact]
    public void RenamingDuringAnEditKeepsTheChannelPlotted()
    {
        MainViewModel vm = Loaded();
        Define(vm, "Double RPM", "RPM * 2");
        vm.Channels.Single(c => c.Name == "Double RPM").IsVisible = true;

        vm.EditMathChannel(vm.MathChannels.Single());
        vm.NewMathName = "Twice RPM";
        vm.AddMathChannel();

        Assert.DoesNotContain(vm.Channels, c => c.Name == "Double RPM");
        Assert.True(Assert.Single(vm.Channels, c => c.Name == "Twice RPM").IsVisible);
    }

    [Fact]
    public void AddingOneDoesNotDisturbWhatIsAlreadyPlotted()
    {
        MainViewModel vm = Loaded();
        var before = vm.Channels.Where(c => c.IsVisible).Select(c => c.Name).ToList();

        Define(vm, "Double RPM", "RPM * 2");

        Assert.Equal(before, vm.Channels.Where(c => c.IsVisible).Select(c => c.Name));
    }

    [Fact]
    public void ThePreviewReportsTheRangeBeforeTheChannelExists()
    {
        MainViewModel vm = Loaded();

        vm.NewMathExpression = "RPM * 2";

        Assert.Contains("…", vm.MathPreview);
    }

    [Fact]
    public void ThePreviewReportsAMistakeRatherThanARange()
    {
        MainViewModel vm = Loaded();

        vm.NewMathExpression = "RPM +";
        Assert.DoesNotContain("…", vm.MathPreview);

        vm.NewMathExpression = "Torque * 2";
        Assert.Contains("Torque", vm.MathPreview);
    }

    [Fact]
    public void ThePreviewNamesAConstantResultAsOne()
    {
        MainViewModel vm = Loaded();

        vm.NewMathExpression = "1 + 1";

        Assert.Contains("Constant", vm.MathPreview);
    }

    [Fact]
    public void AnEmptyNameOrExpressionAddsNothing()
    {
        MainViewModel vm = Loaded();

        Define(vm, "", "RPM * 2");
        Define(vm, "Nothing", "   ");

        Assert.Empty(vm.MathChannels);
    }

    [Fact]
    public void ADuplicateNameIsRefusedRatherThanShadowingTheFirst()
    {
        MainViewModel vm = Loaded();
        Define(vm, "Double RPM", "RPM * 2");

        vm.NewMathName = "double rpm";
        vm.NewMathExpression = "RPM * 3";

        Assert.Throws<InvalidOperationException>(vm.AddMathChannel);
        Assert.Single(vm.MathChannels);
    }
}
