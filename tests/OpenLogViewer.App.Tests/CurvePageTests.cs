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

        [CurveEditor]
           curve = warmupCurve, "Warmup Enrichment"
              columnLabel = "Coolant", "Enrichment"
              xBins = wueBins, coolant
              yBins = wuePct

        [UserDefined]
           dialog = wrapped, "Warmup"
              panel = warmupCurve
              panel = note

           dialog = note, ""
              field = "Warmup must reach 100% when hot"

           dialog = engine, "Engine", yAxis
              field = "Cranking RPM", crankingRPM

        [Menu]
           menu = "&Settings"
              subMenu = engine, "Engine"
              subMenu = warmupCurve, "Warmup Curve"
              subMenu = wrapped, "Warmup Page"
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

        Assert.True(vm.HasOpenCurve);
        Assert.Null(vm.OpenDialog);
        Assert.False(vm.ShowSettingsFields);

        TuneCurveEdit curve = vm.OpenCurve!;
        Assert.Equal("Warmup Enrichment", curve.Title);
        Assert.Equal(4, curve.Count);
        Assert.Equal("Coolant", curve.XLabel);
    }

    [Fact]
    public void APageThatWrapsACurveShowsBothItAndItsFields()
    {
        // MS2's warmup page is exactly this: a curve and a note, put there with
        // the same directive a group of fields uses.
        MainViewModel vm = Opened();

        vm.OpenMenuEntry = Entry(vm, "Warmup Page");

        Assert.True(vm.HasOpenCurve);
        Assert.Equal("Warmup Enrichment", vm.OpenCurve!.Title);

        Assert.NotNull(vm.OpenDialog);
        Assert.Contains("warmupCurve", vm.OpenDialog!.Curves);
        Assert.True(vm.ShowSettingsFields);
    }

    [Fact]
    public void OpeningAPageOfFieldsPutsTheCurveAway()
    {
        MainViewModel vm = Opened();

        vm.OpenMenuEntry = Entry(vm, "Warmup Curve");
        Assert.True(vm.HasOpenCurve);

        vm.OpenMenuEntry = Entry(vm, "Engine");

        Assert.False(vm.HasOpenCurve);
        Assert.NotNull(vm.OpenDialog);
    }

    [Fact]
    public void MovingAPointIsCountedAndCanBePutBack()
    {
        MainViewModel vm = Opened();
        vm.OpenMenuEntry = Entry(vm, "Warmup Curve");

        vm.OpenCurve!.SetY(1, 120);
        vm.CurveChanged();

        Assert.True(vm.HasCurveChanges);
        Assert.Contains("1 moved", vm.CurveSummary);

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

        vm.OpenCurve!.SetY(1, 120);
        vm.CurveChanged();

        Assert.False(vm.CanWriteCurve);
        Assert.Contains("definition rather than a tune", vm.WriteCurveToEcu());
    }

    [Fact]
    public void ThereIsNothingToSendWhenNoCurveIsOpen()
    {
        MainViewModel vm = _harness.NewViewModel(out _);

        Assert.False(vm.HasOpenCurve);
        Assert.False(vm.CanWriteCurve);
        Assert.Equal("", vm.CurveSummary);
    }
}
