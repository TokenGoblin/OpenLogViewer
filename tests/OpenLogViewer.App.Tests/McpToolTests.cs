using System.IO;
using OpenLogViewer.App.Mcp;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// The tools themselves, called directly.
///
/// <para>
/// The end-to-end file proves the transport and that every tool resolves its
/// dependencies; it calls each one once with deliberately useless arguments, so
/// it cannot notice a tool that refuses a perfectly good request. These are the
/// cases where the answer matters.
/// </para>
/// </summary>
public class McpToolTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly ImmediateUiDispatcher _dispatcher = new();

    public void Dispose()
    {
        _harness.Dispose();
        foreach (string p in _temp) { try { File.Delete(p); } catch (IOException) { } }
    }

    private MainViewModel WithTable(out FakeController board)
    {
        MainViewModel vm = EcuFixture.Connected(_harness, out board);
        vm.SelectedEcuTable = vm.EcuTables.First(t => t.Name == "VE Table");
        vm.SelectedCells = TuneSelection.Cell(0, 0);

        return vm;
    }

    private static T Read<T>(object reply, string name) =>
        (T)reply.GetType().GetProperty(name)!.GetValue(reply)!;

    /// <summary>
    /// A reply's refusal text, or the whole reply when it did not refuse. Safe
    /// to pass as an assertion message, which is evaluated whether or not the
    /// assertion fails — reading "reason" directly throws on a reply that
    /// succeeded and therefore has none.
    /// </summary>
    private static string Why(object reply) =>
        reply.GetType().GetProperty("reason")?.GetValue(reply) as string ?? reply.ToString() ?? "";

    // ----- edit_table ----------------------------------------------------------

    [Theory]
    [InlineData("add")]
    [InlineData("scale")]
    [InlineData("set")]
    [InlineData("interpolate")]
    [InlineData("revert")]
    public async Task EveryOperationTheDescriptionAdvertisesIsAccepted(string operation)
    {
        // "add" was refused outright. TuneEditKind.Add is the enum's zero value,
        // so the guard that asked "did the switch fall through to default?" was
        // true for a perfectly good nudge — the commonest table edit there is,
        // and the one the documentation leads with.
        MainViewModel vm = WithTable(out _);

        object reply = await TuneTools.EditTable(operation, 5, vm, _dispatcher);

        Assert.True(
            Read<bool>(reply, "edited"),
            $"'{operation}' was refused: {reply.GetType().GetProperty("reason")?.GetValue(reply)}");
    }

    [Fact]
    public async Task AddActuallyMovesTheCell()
    {
        MainViewModel vm = WithTable(out _);
        double before = vm.TableEdit![0, 0];

        object reply = await TuneTools.EditTable("add", 5, vm, _dispatcher);

        Assert.True(Read<bool>(reply, "edited"));
        Assert.Equal(1, Read<int>(reply, "moved"));
        Assert.Equal(before + 5, vm.TableEdit[0, 0], 6);
    }

    [Fact]
    public async Task AnUnknownOperationIsStillRefused()
    {
        MainViewModel vm = WithTable(out _);

        object reply = await TuneTools.EditTable("obliterate", 5, vm, _dispatcher);

        Assert.False(Read<bool>(reply, "edited"));
        Assert.Contains("is not an operation", Read<string>(reply, "reason"), StringComparison.Ordinal);
    }

    // ----- the write tools' success flag ---------------------------------------

    [Fact]
    public async Task ARefusedWriteIsNotReportedAsSent()
    {
        // The flag the tool descriptions tell an agent to branch on. It used to
        // be derived from the message's first word, so "No table is open." —
        // which does not begin with "Nothing" — came back as sent: true, telling
        // an agent that bytes had reached a running engine when nothing had left
        // the machine.
        MainViewModel vm = _harness.NewViewModel();

        object reply = await EcuWriteTools.WriteTableToEcu(vm, _dispatcher);

        Assert.False(Read<bool>(reply, "sent"));
        Assert.False(Read<bool>(reply, "declined"));
        Assert.Equal("No table is open.", Read<string>(reply, "message"));
    }

    [Fact]
    public async Task NorIsOneRefusedForWantOfAConnection()
    {
        // A table is open — from a definition, with nothing attached — so this
        // gets past the "no table" guard and refuses for the connection instead.
        MainViewModel vm = _harness.NewViewModel();
        _harness.WriteDefinition(vm, "test.ini", EcuFixture.Firmware);

        vm.OpenDefinition(Path.Combine(vm.Workspace.EnsureDefinitions(), "test.ini"));
        vm.SelectedEcuTable = vm.EcuTables.FirstOrDefault(t => t.Name == "VE Table");

        object reply = await EcuWriteTools.BurnTableToEcu(vm, _dispatcher);

        Assert.False(Read<bool>(reply, "sent"));
    }

    [Fact]
    public async Task ADeclinedWriteSaysSoSeparately()
    {
        MainViewModel vm = WithTable(out _);
        vm.EditTable(TuneTableEdit.Add(5));
        _harness.Confirmation.Answer = false;

        object reply = await EcuWriteTools.WriteTableToEcu(vm, _dispatcher);

        Assert.False(Read<bool>(reply, "sent"));
        Assert.True(Read<bool>(reply, "declined"));
    }

    [Fact]
    public async Task AWriteThatLandsIsReportedAsSent()
    {
        MainViewModel vm = WithTable(out FakeController board);
        vm.EditTable(TuneTableEdit.Add(5));

        object reply = await EcuWriteTools.WriteTableToEcu(vm, _dispatcher);

        Assert.True(Read<bool>(reply, "sent"), Read<string>(reply, "message"));
        Assert.False(Read<bool>(reply, "declined"));
        Assert.NotNull(board);
    }

    // ----- save_tune_to_file and compare_with_saved_tune -----------------------
    //
    // These two decided their own success by looking at the file system rather
    // than by asking the thing that did the work, and both could report a
    // success that had not happened. Nothing here covered them at all.

    private string Temp(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}{extension}");
        _temp.Add(path);
        return path;
    }

    private readonly List<string> _temp = [];

    [Fact]
    public async Task SavingATuneSaysItSaved()
    {
        MainViewModel vm = WithTable(out _);
        string path = Temp(".msq");

        object reply = await TuneFileTools.SaveTuneToFile(path, "", vm, _dispatcher);

        Assert.True(Read<bool>(reply, "saved"));
        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// A file of that name existing afterwards is not evidence this write put it
    /// there. Saving over one TunerStudio is holding open fails and leaves the
    /// older file exactly where it was — which used to be answered as a success
    /// pointing at somebody else's tune.
    /// </summary>
    [Fact]
    public async Task SavingOverAHeldFileIsNotReportedAsASave()
    {
        MainViewModel vm = WithTable(out _);

        string held = Temp(".msq");
        File.WriteAllText(held, "someone else's tune");

        using var _hold = File.Open(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        object reply = await TuneFileTools.SaveTuneToFile(held, "", vm, _dispatcher);

        Assert.False(Read<bool>(reply, "saved"));
        Assert.Contains("Could not write", Read<string>(reply, "reason"));
    }

    /// <summary>
    /// The worst wrong answer this server can give. A file that could not be
    /// read used to come back as "compared: true" carrying either the previous
    /// comparison's differences or, on a fresh session, an empty list — and an
    /// empty list reads as "the file matches the ECU", which is the thing
    /// somebody checks before driving.
    /// </summary>
    [Fact]
    public async Task AComparisonAgainstAnUnreadableFileIsNotAComparison()
    {
        MainViewModel vm = WithTable(out _);

        string corrupt = Temp(".msq");
        File.WriteAllText(corrupt, "<msq><page number=\"0\"><constant name=\"crank");

        object reply = await TuneFileTools.CompareWithSavedTune(corrupt, vm, _dispatcher);

        Assert.False(Read<bool>(reply, "compared"));
        Assert.Contains("Could not read", Read<string>(reply, "reason"));
    }

    [Fact]
    public async Task AComparisonAgainstAGoodFileStillReportsItsDifferences()
    {
        MainViewModel vm = WithTable(out _);

        string saved = Temp(".msq");
        Assert.True(Read<bool>(await TuneFileTools.SaveTuneToFile(saved, "", vm, _dispatcher), "saved"));

        object reply = await TuneFileTools.CompareWithSavedTune(saved, vm, _dispatcher);

        // The reply carries "reason" only when it refused, so it cannot be read
        // as the assertion's message — Assert.True evaluates that either way.
        Assert.True(Read<bool>(reply, "compared"), Why(reply));
    }
}
