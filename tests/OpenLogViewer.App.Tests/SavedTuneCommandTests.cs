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
    public void ATuneOpenedFromAFileMayNotBeSentBackAnyWhichWay()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        Assert.True(vm.TuneIsFromFile);
        Assert.False(vm.CanWriteSettings);
        Assert.False(vm.CanWriteTable);
        Assert.False(vm.CanBurn);
        Assert.Contains("opened from a file", vm.WriteTableToEcu().Message);
    }

    [Fact]
    public void ADefinitionOpenedAfterATuneKeepsNothingOfIt()
    {
        // The symbols especially. They say which build a definition should be
        // read as, and carrying one firmware's over to another writes a file
        // whose signature and whose conditionals disagree — which takes the
        // wrong branch everywhere it is read back, and says nothing.
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);
        Assert.True(vm.OpenDefinition(WriteIni()));

        Assert.False(vm.TuneIsFromFile);
        Assert.True(vm.TuneIsPlaceholder);

        // Nothing of the file's is left to leak into a later save.
        string path = Temp(".msq");
        Assert.Contains("definition rather than a tune", vm.SaveTuneToFile(path));
        Assert.False(File.Exists(path));
    }

    // ----- restoring ----------------------------------------------------------

    [Fact]
    public void ARestoreNeedsAControllerToRestoreTo()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.Contains("Not connected", vm.PlanRestore(WriteTune(600)));
        Assert.Null(vm.PendingRestore);
        Assert.False(vm.CanApplyRestore);
    }

    [Fact]
    public void ARestoreIsRefusedAgainstATuneThatIsNotTheEcus()
    {
        // Working out the difference between a file and a placeholder is working
        // out the difference between two things that are both not the ECU.
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        Assert.Contains("Read the ECU's own tune first", vm.PlanRestore(WriteTune(400)));
        Assert.Null(vm.PendingRestore);
    }

    [Fact]
    public void ApplyingWithNothingPlannedDoesNothing()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.Contains("Nothing has been planned", vm.ApplyRestore());
    }

    [Fact]
    public void APlanIsForgottenWhenItIsCancelled()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        vm.CancelRestore();

        Assert.Null(vm.PendingRestore);
        Assert.False(vm.CanApplyRestore);
    }

    [Fact]
    public void ThereIsNothingToCompareUntilThereIsATune()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.Contains("no tune to compare", vm.CompareWithSavedTune(WriteTune(600)));
    }

    // ----- a comparison that did not happen -----------------------------------

    /// <summary>Not valid XML, so reading it throws rather than returning nothing.</summary>
    private string WriteCorruptTune()
    {
        string path = Temp(".msq");
        File.WriteAllText(path, "<msq><page number=\"0\"><constant name=\"crank");
        return path;
    }

    /// <summary>
    /// The failure that made this worth a property of its own. Compare against a
    /// good file, then a corrupt one: the second answer used to be the first
    /// one's list of differences, standing as the answer to a question about a
    /// different file.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeReadDoesNotInheritTheLastComparison()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        vm.CompareWithSavedTune(WriteTune(400));
        Assert.Single(vm.TuneDifferences);
        Assert.True(vm.TuneCompared);

        string outcome = vm.CompareWithSavedTune(WriteCorruptTune());

        Assert.False(vm.TuneCompared);
        Assert.Empty(vm.TuneDifferences);
        Assert.Contains("Could not read", outcome);
    }

    /// <summary>
    /// And on a fresh session the same failure leaves the list empty, which is
    /// the more dangerous half: an empty list reads as "the file matches".
    /// </summary>
    [Fact]
    public void AnEmptyListAfterAFailedReadIsNotAMatch()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        vm.CompareWithSavedTune(WriteCorruptTune());

        Assert.Empty(vm.TuneDifferences);
        Assert.False(vm.TuneCompared);
    }

    [Fact]
    public void ASuccessfulSaveSaysItSaved()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        vm.SaveTuneToFile(Temp(".msq"));

        Assert.True(vm.TuneSaved);
    }

    /// <summary>
    /// A save that cannot be written must not be read as a success because a
    /// file of that name happens to exist — which is exactly what saving over
    /// one TunerStudio is holding open produces.
    /// </summary>
    [Fact]
    public void ASaveOntoAHeldFileIsNotASave()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        _harness.PutDefinition(vm, WriteIni());

        Assert.True(vm.OpenSavedTune(WriteTune(600)), vm.EcuTuneSummary);

        string held = Temp(".msq");
        File.WriteAllText(held, "someone else's tune");

        using (File.Open(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            vm.SaveTuneToFile(held);

            Assert.False(vm.TuneSaved);
        }

        // And the file it would have claimed to have written is untouched.
        Assert.Equal("someone else's tune", File.ReadAllText(held));
    }

    [Fact]
    public void SavingWithNoTuneIsNotASave()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        vm.SaveTuneToFile(Temp(".msq"));

        Assert.False(vm.TuneSaved);
    }
}
