using System.IO;
using OpenLogViewer.App;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Opening a firmware definition with no controller behind it.
///
/// The tune that results is laid out exactly like a real one and is full of
/// noughts, which is the hazard: nothing about it looks like a placeholder, and
/// a definition can be opened while an ECU is still attached.
/// </summary>
public class DefinitionOnlyTuneTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        _harness.Dispose();
        foreach (string p in _temp) { try { File.Delete(p); } catch (IOException) { } }
    }

    private string WriteIni(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-def-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, body);
        _temp.Add(path);
        return path;
    }

    private const string Firmware = """
        [Constants]
        page = 1
        nPages = 1
        pageSize = 32
        pageIdentifier = "\$tsCanId\x01"
        pageReadCommand = "r%2i%2o%2c"
        pageValueWrite  = "w%2i%2o%2c%v"
        burnCommand     = "b%2i"
           crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0

        [UserDefined]
           dialog = engine, "Engine", yAxis
              field = "Cranking RPM", crankingRPM

        [Menu]
           menu = "&Engine"
              subMenu = engine, "Engine"
        """;

    [Fact]
    public void ADefinitionOpensAsAPlaceholderRatherThanAsATune()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.True(vm.OpenDefinition(WriteIni(Firmware)));

        Assert.True(vm.TuneIsPlaceholder);
        Assert.True(vm.HasSettingsPages);

        // Nothing may be sent from it, connected or not. The gate is the
        // placeholder rather than the connection, because a definition can be
        // opened with an ECU still attached — and writing these noughts to a
        // running engine is exactly what that would otherwise do.
        Assert.False(vm.CanWriteSettings);
        Assert.False(vm.CanBurn);
        Assert.False(vm.CanBurnSettings);

        // The table half too. It was the one gate left open, and a table write
        // reaches the running engine by the same wire as the rest.
        Assert.False(vm.CanWriteTable);
    }

    [Fact]
    public void ATableFromAPlaceholderIsRefusedEvenWhenAskedDirectly()
    {
        // A greyed button is not the whole guard: this is reachable from a
        // scripted run, where nothing consults CanWriteTable at all.
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.True(vm.OpenDefinition(WriteIni(Firmware)));

        Assert.Contains("definition rather than a tune", vm.WriteTableToEcu().Message);
    }

    /// <summary>
    /// Every one of them, not just the write.
    ///
    /// <para>
    /// Three of the five carried no such refusal at all. The properties behind
    /// the buttons did — <c>CanBurn</c>, <c>CanBurnSettings</c> — so the interface
    /// looked right, and every MCP write tool calls these methods directly and
    /// never asks a button anything. A burn is the one that cannot be undone by
    /// turning the key off, so a foreign firmware's page reaching flash is the
    /// worst of the five and was among the three.
    /// </para>
    /// </summary>
    [Fact]
    public void NoWriteOrBurnPathWillSendAPlaceholder()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.True(vm.OpenDefinition(WriteIni(Firmware)));
        Assert.True(vm.TuneIsPlaceholder);

        (string Path, string Message)[] answers =
        [
            ("write_table", vm.WriteTableToEcu().Message),
            ("burn_table", vm.BurnTableToEcu().Message),
            ("write_settings", vm.WriteSettingsToEcu().Message),
            ("burn_settings", vm.BurnSettingsToEcu().Message),
            ("write_curve", vm.WriteCurveToEcu().Message),
        ];

        string[] wrong = [.. answers
            .Where(a => !a.Message.Contains("definition rather than a tune", StringComparison.Ordinal))
            .Select(a => $"{a.Path}: {a.Message}")];

        Assert.True(
            wrong.Length == 0,
            "these did not refuse a placeholder tune:\n  " + string.Join("\n  ", wrong));
    }

    [Fact]
    public void OpeningASecondDefinitionLeavesNoPageFromTheFirst()
    {
        // A page left on screen stays bound to the edit that has just been
        // thrown away, so anything typed into it lands in an image nothing will
        // ever send.
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.True(vm.OpenDefinition(WriteIni(Firmware)));
        vm.OpenMenuEntry = vm.SettingsMenu.First(m => !m.IsHeading);
        Assert.NotNull(vm.OpenDialog);

        Assert.True(vm.OpenDefinition(WriteIni(Firmware.Replace("crankingRPM", "crankRpm"))));

        Assert.Null(vm.OpenDialog);
        Assert.Null(vm.OpenMenuEntry);
    }

    [Fact]
    public void ADefinitionWithNoPagesIsRefusedAndSaysSo()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.False(vm.OpenDefinition(WriteIni("[Menu]\n   menu = \"&Engine\"\n")));
        Assert.Contains("no pages", vm.EcuTuneSummary, StringComparison.OrdinalIgnoreCase);
    }
}
