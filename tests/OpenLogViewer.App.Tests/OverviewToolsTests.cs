using OpenLogViewer.App.Mcp;
using Xunit;

namespace OpenLogViewer.App.Tests;

public class OverviewToolsTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly ImmediateUiDispatcher _dispatcher = new();
    private readonly NoWindow _windows = new();

    public void Dispose() => _harness.Dispose();

    private static T Read<T>(object reply, string name) =>
        (T)reply.GetType().GetProperty(name)!.GetValue(reply)!;

    private static readonly OverviewFindingInput TableFinding = new(
        "Warning", "Fuelling", "Lean cruise", "Cruise cells read lean of target.", "AFR 15.2 vs target 14.7",
        new OverviewChangeInput(OverviewChange.TableCellKind, "VE Table", 4, 2, CurrentValue: "78", ProposedValue: "82"));

    private static readonly OverviewFindingInput SettingFinding = new(
        "Watch", "Idle", "Idle target a touch low", "", "",
        new OverviewChangeInput(OverviewChange.SettingKind, PageName: "Idle Control", FieldLabel: "Target RPM",
            CurrentValue: "800", ProposedValue: "850"));

    private static readonly OverviewFindingInput NoteFinding = new(
        "Note", "Coolant", "Warms up in under two minutes", "Nothing to change here.");

    [Fact]
    public async Task PushingAReportPopulatesTheViewModel()
    {
        MainViewModel vm = _harness.NewViewModel();

        object reply = await OverviewTools.PushOverviewReport(
            "Two things worth a look", "Cruise fuelling and idle target.",
            [TableFinding, SettingFinding, NoteFinding], vm, _windows, _dispatcher);

        Assert.True(Read<bool>(reply, "published"));
        Assert.Equal(3, Read<int>(reply, "findings"));
        Assert.Equal(2, Read<int>(reply, "withChanges"));
        Assert.Equal("Two things worth a look", vm.OverviewHeadline);
        Assert.Equal(3, vm.OverviewFindings.Count);
        Assert.Equal(1, vm.OverviewRevision);
        Assert.True(vm.HasOverview);
    }

    [Fact]
    public async Task PushingAgainReplacesRatherThanAppends()
    {
        MainViewModel vm = _harness.NewViewModel();

        await OverviewTools.PushOverviewReport("First", "First pass.", [TableFinding], vm, _windows, _dispatcher);
        object reply = await OverviewTools.PushOverviewReport(
            "Second", "Second pass.", [NoteFinding], vm, _windows, _dispatcher);

        Assert.Equal(2, Read<int>(reply, "revision"));
        Assert.Single(vm.OverviewFindings);
        Assert.Equal("Second", vm.OverviewHeadline);
    }

    [Fact]
    public async Task PushingWithNoWindowStillSucceeds()
    {
        // Showing the window is best-effort, not a precondition — a headless
        // client, or a test, still gets the report onto the view model.
        MainViewModel vm = _harness.NewViewModel();

        object reply = await OverviewTools.PushOverviewReport(
            "Headless", "No window to show.", [NoteFinding], vm, _windows, _dispatcher);

        Assert.True(Read<bool>(reply, "published"));
    }

    [Fact]
    public async Task AnUnknownLevelIsRefused()
    {
        MainViewModel vm = _harness.NewViewModel();
        var bad = new OverviewFindingInput("Critical", "X", "X", "X");

        object reply = await OverviewTools.PushOverviewReport("H", "S", [bad], vm, _windows, _dispatcher);

        Assert.False(Read<bool>(reply, "published"));
        Assert.Contains("is not a level", Read<string>(reply, "reason"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIncompleteChangeIsKeptAsAnObservationWithAWarning()
    {
        MainViewModel vm = _harness.NewViewModel();
        var incomplete = new OverviewFindingInput(
            "Note", "X", "Half-described change", "",
            Change: new OverviewChangeInput(OverviewChange.TableCellKind)); // no table name, no cell

        object reply = await OverviewTools.PushOverviewReport("H", "S", [incomplete], vm, _windows, _dispatcher);

        Assert.True(Read<bool>(reply, "published"));
        Assert.Equal(1, Read<int>(reply, "findings"));
        Assert.Equal(0, Read<int>(reply, "withChanges"));
        Assert.Single(Read<List<string>>(reply, "warnings"));
        Assert.False(vm.OverviewFindings.Single().HasChange);
    }

    [Fact]
    public async Task GetOverviewReportRereadsWithoutRegenerating()
    {
        MainViewModel vm = _harness.NewViewModel();
        await OverviewTools.PushOverviewReport("H", "S", [TableFinding], vm, _windows, _dispatcher);

        object reply = await OverviewTools.GetOverviewReport(vm, _dispatcher);

        Assert.True(Read<bool>(reply, "read"));
        Assert.Equal(1, Read<int>(reply, "revision"));
        Assert.Equal("H", Read<string>(reply, "headline"));
    }

    [Fact]
    public async Task GetOverviewReportRefusesWithNothingPublished()
    {
        MainViewModel vm = _harness.NewViewModel();

        object reply = await OverviewTools.GetOverviewReport(vm, _dispatcher);

        Assert.False(Read<bool>(reply, "read"));
    }

    [Fact]
    public async Task GetOverviewSelectionsReflectsWhatWasTicked()
    {
        MainViewModel vm = _harness.NewViewModel();
        await OverviewTools.PushOverviewReport(
            "H", "S", [TableFinding, SettingFinding, NoteFinding], vm, _windows, _dispatcher);

        // Simulates a person ticking the box, the same way McpToolTests simulates
        // a selection by setting vm.SelectedCells directly.
        vm.OverviewFindings.First(f => f.Title == "Lean cruise").Accepted = true;

        object reply = await OverviewTools.GetOverviewSelections(vm, _dispatcher);

        Assert.Equal(3, Read<int>(reply, "totalFindings"));
        Assert.Equal(1, Read<int>(reply, "acceptedCount"));

        var accepted = (object[])reply.GetType().GetProperty("accepted")!.GetValue(reply)!;
        object only = Assert.Single(accepted);
        Assert.Equal("Lean cruise", Read<string>(only, "title"));

        object change = Read<object>(only, "change");
        Assert.Equal(OverviewChange.TableCellKind, Read<string>(change, "kind"));
        Assert.Equal("VE Table", Read<string>(change, "tableName"));
    }

    [Fact]
    public async Task ClearOverviewEmptiesEverything()
    {
        MainViewModel vm = _harness.NewViewModel();
        await OverviewTools.PushOverviewReport("H", "S", [TableFinding], vm, _windows, _dispatcher);

        object reply = await OverviewTools.ClearOverview(vm, _dispatcher);

        Assert.True(Read<bool>(reply, "cleared"));
        Assert.False(vm.HasOverview);
        Assert.Empty(vm.OverviewFindings);
        Assert.Equal("", vm.OverviewHeadline);
    }
}
