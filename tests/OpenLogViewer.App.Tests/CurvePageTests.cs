using System.IO;
using OpenLogViewer.App;
using OpenLogViewer.Core;
using Xunit;

namespace OpenLogViewer.App.Tests;

/// <summary>
/// Opening a curve from the settings list.
///
/// A firmware's menu makes no distinction between a page of fields, a table and
/// a curve — an entry names something and the file does not say which. Curves
/// were skipped in both places they appear, so 23 of a MicroSquirt's 131 menu
/// entries and 14 more of its pages opened nothing at all.
/// </summary>
public class CurvePageTests : IDisposable
{
    private readonly ViewModelHarness _harness = new();
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        _harness.Dispose();
        foreach (string p in _temp) { try { File.Delete(p); } catch (IOException) { } }
    }

    /// <summary>
    /// One curve reached straight from the menu, one reached through a page that
    /// wraps it — which is how MS2 spells its warmup enrichment.
    /// </summary>
    private const string Firmware = """
        [MegaTune]
           signature = "curve firmware"

        [Constants]
        page = 1
        nPages = 1
        pageSize = 64
        pageIdentifier = "\x01"
        pageReadCommand = "r%2o%2c"
        pageValueWrite  = "w%2o%2c%v"
           wueBins     = array,  U08, 0, [4], "F", 1.0, -40, -40, 215, 0
           wuePct      = array,  U08, 8, [4], "%", 1.0, 0, 0, 250, 0
           crankingRPM = scalar, U16, 16, "rpm", 1, 0, 0, 10000, 0
           rpmBins     = array,  U08, 20, [2], "rpm", 100, 0, 0, 25500, 0
           mapBins     = array,  U08, 24, [2], "kPa", 1, 0, 0, 255, 0
           veTable     = array,  U08, 28, [2x2], "%", 1, 0, 0, 255, 0

        [CurveEditor]
           curve = warmupCurve, "Warmup Enrichment"
              columnLabel = "Coolant", "Enrichment"
              xBins = wueBins, coolant
              yBins = wuePct

           curve = ghostCurve, "Names Bins This Build Has Not"
              xBins = noSuchBins
              yBins = alsoNotHere

        [UserDefined]
           dialog = wrapped, "Warmup"
              panel = warmupCurve
              panel = note

           dialog = note, ""
              field = "Warmup must reach 100% when hot"

           dialog = engine, "Engine", yAxis
              field = "Cranking RPM", crankingRPM

        [TableEditor]
           table = veTableTbl, veTableMap, "VE Table", 2
              xBins = rpmBins, rpm
              yBins = mapBins, map
              zBins = veTable

        [Menu]
           menu = "&Settings"
              subMenu = engine, "Engine"
              subMenu = warmupCurve, "Warmup Curve"
              subMenu = wrapped, "Warmup Page"
              subMenu = veTableTbl, "VE Table"
              subMenu = veTableMap, "VE Table 3D"
              subMenu = ghostCurve, "A Curve With No Bins"
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

    private MainViewModel Opened()
    {
        MainViewModel vm = _harness.NewViewModel(out _);
        Assert.True(vm.OpenDefinition(WriteIni()), vm.EcuTuneSummary);
        vm.ShowSettingsPages = true;
        return vm;
    }

    private static SettingsMenuEntry Entry(MainViewModel vm, string title) =>
        vm.SettingsMenu.Single(m => !m.IsHeading && m.Title == title);

    [Fact]
    public void AMenuEntryNamingACurveIsOffered()
    {
        MainViewModel vm = Opened();

        SettingsMenuEntry entry = Entry(vm, "Warmup Curve");

        Assert.True(entry.IsCurve);
        Assert.False(Entry(vm, "Engine").IsCurve);
    }

    [Fact]
    public void OpeningItPutsTheCurveOnScreenAndNoFields()
    {
        MainViewModel vm = Opened();

        vm.OpenMenuEntry = Entry(vm, "Warmup Curve");

        Assert.True(vm.HasOpenCurves);
        Assert.Null(vm.OpenDialog);
        Assert.False(vm.ShowSettingsFields);

        TuneCurveEdit curve = vm.OpenCurves[0];
        Assert.Equal("Warmup Enrichment", curve.Title);
        Assert.Equal(4, curve.Count);
        Assert.Equal("Coolant", curve.XLabel);
    }

    [Fact]
    public void AMenuEntryNamingATableOpensThatTable()
    {
        // The third thing an entry can name, and the last one that opened
        // nothing: 51 of a MicroSquirt's entries and 53 of an MS3's.
        MainViewModel vm = Opened();

        SettingsMenuEntry entry = Entry(vm, "VE Table");
        Assert.True(entry.IsTable);

        vm.OpenMenuEntry = entry;

        Assert.True(vm.ShowTableView);
        Assert.Equal("VE Table", vm.SelectedEcuTable?.Name);

        // And the settings list stays where it was, so following a menu into a
        // table does not lose the reader's place in it.
        Assert.True(vm.ShowSettingsPages);
        Assert.False(vm.HasOpenCurves);
    }

    [Fact]
    public void TheThreeDimensionalNameLeadsToTheSameTable()
    {
        // A firmware declares a table under two names and a menu may point at
        // either.
        MainViewModel vm = Opened();

        vm.OpenMenuEntry = Entry(vm, "VE Table 3D");

        Assert.True(vm.ShowTableView);
        Assert.Equal("VE Table", vm.SelectedEcuTable?.Name);
    }

    [Fact]
    public void ACurveNamingBinsThisBuildLacksIsNotOffered()
    {
        // Offered and then opening a blank pane is worse than not offered: the
        // entry looks like every other one and does nothing at all.
        MainViewModel vm = Opened();

        Assert.DoesNotContain(
            vm.SettingsMenu, m => !m.IsHeading && m.Title == "A Curve With No Bins");
    }

    [Fact]
    public void SwitchingToTheTablesHalfPutsTheCurveAway()
    {
        // Otherwise the plot and its Send button stay drawn over the table
        // editor, still willing to write.
        MainViewModel vm = Opened();
        vm.OpenMenuEntry = Entry(vm, "Warmup Curve");

        Assert.True(vm.ShowCurves);

        vm.ShowEcuTables = true;

        Assert.False(vm.ShowCurves);
    }

    [Fact]
    public void APageThatWrapsACurveShowsBothItAndItsFields()
    {
        // MS2's warmup page is exactly this: a curve and a note, put there with
        // the same directive a group of fields uses.
        MainViewModel vm = Opened();

        vm.OpenMenuEntry = Entry(vm, "Warmup Page");

        Assert.True(vm.HasOpenCurves);
        Assert.Equal("Warmup Enrichment", vm.OpenCurves[0].Title);

        Assert.NotNull(vm.OpenDialog);
        Assert.Contains("warmupCurve", vm.OpenDialog!.Curves);
        Assert.True(vm.ShowSettingsFields);
    }

    [Fact]
    public void OpeningAPageOfFieldsPutsTheCurveAway()
    {
        MainViewModel vm = Opened();

        vm.OpenMenuEntry = Entry(vm, "Warmup Curve");
        Assert.True(vm.HasOpenCurves);

        vm.OpenMenuEntry = Entry(vm, "Engine");

        Assert.False(vm.HasOpenCurves);
        Assert.NotNull(vm.OpenDialog);
    }

    [Fact]
    public void MovingAPointIsCountedAndCanBePutBack()
    {
        MainViewModel vm = Opened();
        vm.OpenMenuEntry = Entry(vm, "Warmup Curve");

        vm.OpenCurves[0].SetY(1, 120);
        vm.CurveChanged();

        Assert.True(vm.HasCurveChanges);
        Assert.Contains("1 point moved", vm.CurveSummary);

        vm.RevertCurve();

        Assert.False(vm.HasCurveChanges);
        Assert.Contains("nothing changed", vm.CurveSummary);
    }

    [Fact]
    public void ACurveFromAPlaceholderTuneMayNotBeSent()
    {
        // The same refusal every other write makes. A definition opened with no
        // controller behind it is all zeros, and a curve of zeros sent to a
        // running engine is fuelling of zero.
        MainViewModel vm = Opened();
        vm.OpenMenuEntry = Entry(vm, "Warmup Curve");

        vm.OpenCurves[0].SetY(1, 120);
        vm.CurveChanged();

        Assert.False(vm.CanWriteCurve);
        Assert.Contains("definition rather than a tune", vm.WriteCurveToEcu());
    }

    [Fact]
    public void ThereIsNothingToSendWhenNoCurveIsOpen()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.False(vm.HasOpenCurves);
        Assert.False(vm.CanWriteCurve);
        Assert.Equal("", vm.CurveSummary);
    }
}
