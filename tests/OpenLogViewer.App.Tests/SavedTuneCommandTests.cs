using System.IO;
using OpenLogViewer.App;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Saving the tune to a file, opening one, and comparing.
/// </summary>
public class SavedTuneCommandTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        _harness.Dispose();
        foreach (string p in _temp) { try { File.Delete(p); } catch (IOException) { } }
    }

    private const string Firmware = """
        [MegaTune]
           signature = "test firmware"

        [Constants]
        page = 1
        nPages = 1
        pageSize = 32
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageValueWrite  = "w%2o%2c%v"
           crankingRPM = scalar, U16, 0, "rpm", 1, 0, 0, 10000, 0
           fanOn       = bits,   U08, 2, [0:0], "No", "Yes"

        [UserDefined]
           dialog = engine, "Engine", yAxis
              field = "Cranking RPM", crankingRPM

        [Menu]
           menu = "&Engine"
              subMenu = engine, "Engine"
        """;

    private string Temp(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"olv-{Guid.NewGuid():N}{extension}");
        _temp.Add(path);
        return path;
    }

    private string WriteIni()
    {
        string path = Temp(".ini");
        File.WriteAllText(path, Firmware);
        return path;
    }

    /// <summary>A tune file for the firmware above, with the given cranking RPM.</summary>
    private string WriteTune(int crankingRpm, string signature = "test firmware")
    {
        string path = Temp(".msq");

        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <msq xmlns="http://www.msefi.com/:msq">
            <versionInfo fileFormat="5.0" nPages="1" signature="{signature}"/>
            <page number="0" size="32">
            <constant digits="0" name="crankingRPM" units="rpm">{crankingRpm}.0</constant>
            <constant name="fanOn">"Yes"</constant>
            </page>
            </msq>
            """);

        return path;
    }

    // ----- saving -----------------------------------------------------------

    [Fact]
    public void ThereIsNothingToSaveUntilThereIsATune()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.False(vm.CanSaveTune);
        Assert.Contains("no tune", vm.SaveTuneToFile(Temp(".msq")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADefinitionOpenedWithNoEcuIsNotWorthSaving()
    {
        // Every value in it is a zero standing in for one, and a file of those
        // would look exactly like a tune.
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.True(vm.OpenDefinition(WriteIni()));
        Assert.False(vm.CanSaveTune);

        string path = Temp(".msq");
        Assert.Contains("definition rather than a tune", vm.SaveTuneToFile(path));
        Assert.False(File.Exists(path));
    }

    // ----- opening ----------------------------------------------------------

    [Fact]
    public void ASavedTuneOpensWithItsValuesAndItsPages()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        Assert.False(vm.TuneIsPlaceholder);
        Assert.True(vm.CanSaveTune);
        Assert.True(vm.HasSettingsPages);
    }

    [Fact]
    public void ATuneWhoseFirmwareIsNotHereSaysWhereToPutTheDefinition()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.False(vm.OpenSavedTune(WriteTune(600, "some firmware nobody has")));
        Assert.Contains("no definition", vm.EcuTuneSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFileThatDoesNotSayWhichFirmwareItIsForIsRefused()
    {
        string path = Temp(".msq");
        File.WriteAllText(path, """
            <msq xmlns="http://www.msefi.com/:msq">
            <page number="0" size="32"><constant name="crankingRPM">600.0</constant></page>
            </msq>
            """);

        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.False(vm.OpenSavedTune(path));
        Assert.Contains("does not say which firmware", vm.EcuTuneSummary);
    }

    [Fact]
    public void SomethingThatIsNotATuneAtAllIsReportedRatherThanThrown()
    {
        string path = Temp(".msq");
        File.WriteAllText(path, "this is not a tune");

        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.False(vm.OpenSavedTune(path));
        Assert.Contains("Could not read", vm.EcuTuneSummary);
    }

    // ----- comparing --------------------------------------------------------

    [Fact]
    public void ATuneComparedWithTheFileItCameFromMatches()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        string tune = WriteTune(600);
        Assert.True(vm.OpenSavedTune(tune), vm.EcuTuneSummary);

        Assert.Contains("matches", vm.CompareWithSavedTune(tune));
        Assert.Empty(vm.TuneDifferences);
    }

    [Fact]
    public void ASettingThatDiffersIsNamedWithBothValues()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        string outcome = vm.CompareWithSavedTune(WriteTune(400));

        TuneDifference difference = Assert.Single(vm.TuneDifferences);
        Assert.Equal("crankingRPM", difference.Name);
        Assert.Contains("400 rpm", outcome, StringComparison.Ordinal);
        Assert.Contains("600 rpm", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparingAgainstAnotherFirmwareSaysSoRatherThanListingEverything()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        Assert.Contains("different firmwares", vm.CompareWithSavedTune(WriteTune(600, "something else")));
    }

    [Fact]
    public void ThereIsNothingToCompareUntilThereIsATune()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.Contains("no tune to compare", vm.CompareWithSavedTune(WriteTune(600)));
    }
}
